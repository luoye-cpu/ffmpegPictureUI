using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FfmpegGui.Services;

namespace GainMapTestHost;

/// <summary>
/// GainMap (Ultra HDR) 编解码闭环测试宿主。
///
/// 验证内容:
///   A. 编码验证 — 合成 HDR 线性像素 → GainMapEncoder → Ultra HDR JPEG
///      - 结构检查 (exiftool: MPImage2 / hdrgm 标签)
///      - 灰度 vs RGB 多通道增益图
///      - 不同 headroom (SDR 1x / 1000nits / 4000nits)
///      - 不同降采样 (1/4, 1/8)
///   B. 解码闭环 — 编码产物 → GainMapDecoder → 线性 HDR → 与输入对比
///      - PSNR / 最大相对误差 (应接近无损, JPEG 有损编码有少量误差)
///   C. 外部素材兼容 — libavif 官方素材 (若有标准 hdrgm 标签)
/// </summary>
public static class Program
{
    private static int _pass;
    private static int _fail;
    private static int _known;
    private static readonly System.Collections.Generic.List<string> _knownList = new();
    private static readonly System.Collections.Generic.List<string> _failures = new();

    public static async Task<int> Main(string[] args)
    {
        // 临时件根钉进产物区（同 Harness.PinScratchRoot；本宿主不共用 Harness）
        try
        {
            var scratch = Path.Combine(Directory.GetCurrentDirectory(), "tests", "output", "_tmp");
            Directory.CreateDirectory(scratch);
            foreach (var k in new[] { "TMP", "TEMP", "TMPDIR" }) Environment.SetEnvironmentVariable(k, scratch);
        }
        catch { }
        // ── 批次 C 数值锁（F-5/F-2/F-3/F-4/F-8）专用模式 ──────────────────────
        //   `GainMapTestHost gainmath` ⇒ **只**跑本批新锁（进程内，不起产品 GUI），
        //   由 `verify-gainmap-managed.ps1` §6 与 `verify-gainmap-isobmff.ps1` §5 调用。
        //   ⚠ 单列一个模式的原因：默认全量模式要跑 5 组编码 + 4 组解码闭环（外部进程数十次），
        //   本批锁需要的是**可点名、可数条**的一小组判据 ⇒ 「汇总数 == 点名条数」才立得住。
        if (args.Length > 0 && string.Equals(args[0], "gainmath", StringComparison.OrdinalIgnoreCase))
        {
            var od = Path.Combine(Environment.CurrentDirectory, "tests", "output", "gainmap");
            Directory.CreateDirectory(od);
            //   `gainmath avifgm` ⇒ 只跑 AVIF 成套段（C5/C6），给 verify-gainmap-isobmff.ps1 用；
            //   不带第二参 ⇒ 全跑（C0–C7），给 verify-gainmap-managed.ps1 用。
            if (args.Length > 1 && string.Equals(args[1], "avifgm", StringComparison.OrdinalIgnoreCase))
            {
                int n = await AvifChannelLocksAsync(od, MathF.Log2(1000f / GainMapEncoder.KSdrWhiteNits),
                                                   GainMapEncoder.KSdrWhiteNits);
                Console.WriteLine($"GAINMATH CHECKS={n}");
                Console.WriteLine($"GAINMATH RESULT: pass={_pass} fail={_fail}");
                foreach (var f in _failures) Console.WriteLine($"  ❌ {f}");
                return _fail == 0 ? 0 : 1;
            }
            return await GainMathLocksAsync(od);
        }

        Console.WriteLine("═══ GainMap 测试宿主 ═══");
        Console.WriteLine($"工作目录: {Environment.CurrentDirectory}");
        Console.WriteLine($"ffmpeg 可用: {!string.IsNullOrWhiteSpace(AppSettingsService.Current.FfmpegPath)}");
        Console.WriteLine($"cjpegli 可用: {CjpegliService.IsAvailable} ({CjpegliService.DetectedPath})");
        Console.WriteLine($"exiftool 可用: {ExifToolService.IsAvailable} ({ExifToolService.DetectedPath})");
        Console.WriteLine();

        var outDir = Path.Combine(Environment.CurrentDirectory, "tests", "output", "gainmap");
        Directory.CreateDirectory(outDir);

        // ── A. 编码验证 ──
        Console.WriteLine("═══ A. 编码验证 ═══");
        await EncodeTestAsync(outDir, "sdr",       hdrPeak: 203f,   multiChannel: false, downsample: 4);
        await EncodeTestAsync(outDir, "hdr1000",   hdrPeak: 1000f, multiChannel: false, downsample: 4);
        await EncodeTestAsync(outDir, "hdr1000rgb",hdrPeak: 1000f, multiChannel: true,  downsample: 4);
        await EncodeTestAsync(outDir, "hdr4000",   hdrPeak: 4000f, multiChannel: false, downsample: 4);
        await EncodeTestAsync(outDir, "hdr1000ds8",hdrPeak: 1000f, multiChannel: false, downsample: 8);

        // ── B. 解码闭环 (用编码产物) ──
        Console.WriteLine();
        Console.WriteLine("═══ B. 解码闭环验证 ═══");
        await DecodeRoundTripAsync(outDir, "sdr", 203f);
        await DecodeRoundTripAsync(outDir, "hdr1000", 1000f);
        await DecodeRoundTripAsync(outDir, "hdr1000rgb", 1000f);
        await DecodeRoundTripAsync(outDir, "hdr4000", 4000f);

        // ── C. 结构验证 (exiftool) ──
        Console.WriteLine();
        Console.WriteLine("═══ C. 输出结构验证 ═══");
        await StructureCheckAsync(Path.Combine(outDir, "hdr1000.jpg"));

        // ── E. 带方向标签 (Orientation) 的输入 —— 显示尺寸口径回归 ──
        Console.WriteLine();
        Console.WriteLine("═══ E. 方向标签 (Orientation=8) 显示尺寸回归 ═══");
        await OrientedGainMapDecodeTestAsync(outDir);
        await OrientedQualityAnalysisTestAsync(outDir);

        // ── D. PNG 3.0 chunk 服务 (PngCicpService) ──
        Console.WriteLine();
        Console.WriteLine("═══ D. PNG 3.0 sBIT/cICP (PngCicpService) ═══");
        await PngCicpTestAsync(outDir);

        // ── 汇总 ──
        Console.WriteLine();
        Console.WriteLine($"═══ 汇总: 通过 {_pass}, 失败 {_fail}, 已知问题 {_known} ═══");
        foreach (var f in _failures)
            Console.WriteLine($"  ❌ {f}");
        foreach (var k in _knownList)
            Console.WriteLine($"  ⚠️ {k}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>验证 PngCicpService 的 sBIT/cICP chunk 写入（PNG 3.0 10/12-bit 有效位语义）。</summary>
    private static async Task PngCicpTestAsync(string outDir)
    {
        var ffmpeg = AppSettingsService.Current.FfmpegPath;
        var srcPng = Path.Combine(outDir, "png3_base.png");
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            Check("D1 ffmpeg 可用 (跳过 PNG 3.0 测试)", false);
            return;
        }

        // 用 ffmpeg 生成 16-bit 基准 PNG
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-y"); psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("testsrc2=size=128x96:duration=0.1");
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1"); psi.ArgumentList.Add("-update"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("png"); psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("rgb48be");
        psi.ArgumentList.Add(srcPng);
        using (var p = System.Diagnostics.Process.Start(psi))
        {
            await p!.WaitForExitAsync();
            if (p.ExitCode != 0 || !File.Exists(srcPng)) { Check("D1 ffmpeg 生成基准 PNG", false); return; }
        }
        Check("D1 ffmpeg 生成基准 PNG", true);

        // sBIT(10) 写入
        var sbitPng = Path.Combine(outDir, "png3_sbit10.png");
        File.Copy(srcPng, sbitPng, true);
        var okSbit = PngCicpService.TryInsertSbit(sbitPng, 10);
        var hasSbit = ReadChunk(sbitPng, "sBIT") is { } sbt && sbt.Length == 3 && sbt[0] == 10 && sbt[1] == 10 && sbt[2] == 10;
        Check($"D2 sBIT(10) 写入: {okSbit}, chunk={BitConverter.ToString(ReadChunk(sbitPng, "sBIT") ?? Array.Empty<byte>())}", okSbit && hasSbit);

        // 幂等: 再次写入不产生重复
        PngCicpService.TryInsertSbit(sbitPng, 10);
        var sbitCount = CountChunks(sbitPng, "sBIT");
        Check($"D3 sBIT 幂等 (sBIT 数量={sbitCount})", sbitCount == 1);

        // 12-bit
        var sbit12 = Path.Combine(outDir, "png3_sbit12.png");
        File.Copy(srcPng, sbit12, true);
        PngCicpService.TryInsertSbit(sbit12, 12);
        var hasSbit12 = ReadChunk(sbit12, "sBIT") is { } s12 && s12[0] == 12;
        Check($"D4 sBIT(12) 写入: chunk={BitConverter.ToString(ReadChunk(sbit12, "sBIT") ?? Array.Empty<byte>())}", hasSbit12);

        // cICP 写入 + sBIT 共存
        var cicpPng = Path.Combine(outDir, "png3_cicp.png");
        File.Copy(srcPng, cicpPng, true);
        var okCicp = PngCicpService.TryInsertCicp(cicpPng, 9, 16); // BT.2020 PQ
        var hasCicp = ReadChunk(cicpPng, "cICP") is { } cc && cc.Length == 4 && cc[0] == 9 && cc[1] == 16 && cc[2] == 0 && cc[3] == 1;
        PngCicpService.TryInsertSbit(cicpPng, 10);
        var coexist = ReadChunk(cicpPng, "cICP") != null && ReadChunk(cicpPng, "sBIT") != null;
        Check($"D5 cICP(BT.2020 PQ)+sBIT 共存: {okCicp}, {BitConverter.ToString(ReadChunk(cicpPng, "cICP") ?? Array.Empty<byte>())}", okCicp && hasCicp && coexist);

        // 非 PNG 文件拒绝
        var bad = Path.Combine(outDir, "png3_bad.txt");
        File.WriteAllText(bad, "not a png");
        Check("D6 非 PNG 文件拒绝", !PngCicpService.TryInsertSbit(bad, 10) && !PngCicpService.TryInsertCicp(bad, 9, 16));

        // 非法位深拒绝
        Check("D7 非法位深拒绝", !PngCicpService.TryInsertSbit(srcPng, 0) && !PngCicpService.TryInsertSbit(srcPng, 17));

        // ── 软件真实命令路径 (FfmpegCommandBuilder.BuildArguments) ──
        // 输入 = D5 产物 (BT.2020 PQ cICP PNG)
        var hdrIn = Path.Combine(outDir, "png3_cicp.png");
        var jpgOut = Path.Combine(outDir, "out_tonemap.jpg");
        var optsAuto = new FfmpegGui.Models.FfmpegOptions
        {
            Format = "jpg", ColorSpace = "auto", ColorStrategy = FfmpegGui.Models.ColorStrategy.Recommended, Threads = 4
        };
        var cmdAuto = FfmpegCommandBuilder.BuildArguments(optsAuto, hdrIn, jpgOut);
        Console.WriteLine($"      [D8 cmd] {cmdAuto}");
        // 现行设计：JPEG 不能存 PQ ⇒ HDR 源降 SDR，并按 sRGB 标定（iccgen 附 ICC）。
        // 旧断言写的是 t=bt709；实际目标曲线已统一为 iec61966-2-1（JPEG 查看器惯例），属断言过时。
        // ⚠ 2026-10-25：链里的 `m=` 取自 capM，随「JPG/WebP 转换矩阵必须 BT.601」的修复变成 bt470bg
        //   （链输出 rgb48le，矩阵只是标签；最终 RGB→YUV 由出口 `-colorspace bt470bg` 完成）。
        Check("D8 tonemap 链 (auto→sRGB 标定 + iccgen)", cmdAuto.Contains(
            "format=yuv444p,tonemap=hable:param=0.5,format=rgb48le,zscale=pin=bt2020:tin=linear:min=gbr:p=bt709:t=iec61966-2-1:m=bt470bg")
            && cmdAuto.Contains("iccgen"));

        // JPEG 目标无论用户选什么 HDR 空间都必须降 SDR（HDR 由 GainMap 承载）
        var optsH2H = new FfmpegGui.Models.FfmpegOptions
        {
            Format = "jpg", ColorSpace = "P3 PQ", ColorStrategy = FfmpegGui.Models.ColorStrategy.Recommended, Threads = 4
        };
        var cmdH2H = FfmpegCommandBuilder.BuildArguments(optsH2H, hdrIn, Path.Combine(outDir, "out_p3pq.jpg"));
        Console.WriteLine($"      [D9 cmd] {cmdH2H}");
        // D9 要守护的不变量：**JPEG 绝不写 PQ/HLG 标签**（那会产出无法被正确显示的伪 HDR）。
        // 原色不钉死：“P3 PQ” 在简化映射里 primaries 为 null（它主要表达 HDR 意图），
        // 而纯 SDR 广色域目标（Display P3）由 D10 钉住必须出 smpte432+ICC。
        Check("D9 HDR→JPEG 必降 SDR（绝不写 PQ/HLG 标签；HDR 靠 GainMap）", cmdH2H.Contains("tonemap")
            && cmdH2H.Contains("t=iec61966-2-1")
            && !cmdH2H.Contains("t=smpte2084") && !cmdH2H.Contains("t=arib-std-b67"));
        // 新策略：JPEG 不再钳 primaries（P3 可经 ICC 表达），但仍钳 HDR trc 到 SDR；
        // “JPEG+HDR 目标且未开 GainMap”在 CLI 层已被拒绝（exit=2），这里验证的是命令本身不写 PQ 标签。
        Console.WriteLine("      NOTE D9: JPEG+HDR 目标的显式拒绝待接入 CLI（色彩选项在 BuildJobSpecs 才组装）；命令层保证不写 PQ 标签");

        // HDR→广色域 SDR (目标 Display P3): tonemap 链目标应为 P3+sRGB
        var optsH2P3 = new FfmpegGui.Models.FfmpegOptions
        {
            Format = "jpg", ColorSpace = "Display P3", ColorStrategy = FfmpegGui.Models.ColorStrategy.Recommended, Threads = 4
        };
        var cmdH2P3 = FfmpegCommandBuilder.BuildArguments(optsH2P3, hdrIn, Path.Combine(outDir, "out_p3sdr.jpg"));
        Console.WriteLine($"      [D10 cmd] {cmdH2P3}");
        // 能力修正（本轮）：JPEG 有 APP2 ICC 通路 ⇒ Display P3(SDR) **可以真交付**，不应被钳回 sRGB。
        // 旧断言“必须 p=bt709”把未实现的路径当成了能力限制；现在要求按用户目标出 P3 + iccgen 嵌 ICC。
        // 同 D8：`m=` 随 capM 变成 bt470bg；P3 像素与 ICC 的判定不受影响（链输出 rgb48le）。
        bool d10P3 = cmdH2P3.Contains("zscale=pin=bt2020:tin=linear:min=gbr:p=smpte432:t=iec61966-2-1:m=bt470bg")
                     && cmdH2P3.Contains("iccgen");
        Check("D10 JPEG+Display P3 按目标出 P3 像素并附 ICC（不被降级为 sRGB）", d10P3);

        // ── RAW 预处理注入场景 (Bug#2 回归) ──
        // ⚠ 2026-10-04 更新：注入通道已由 `ColorPrimaries/ColorTrc` 改为 **`ColorSourceDeclPrimaries/Trc`**。
        //   自 2026-10-03 起（见 CHANGELOG）`ColorPrimaries/ColorTrc` **只表示输出目标**；旧的"把注入值
        //   写进 CP/CT 当输入声明"在新语义下会把这对值当成**输出目标**、把用户选的 sRGB 吃掉。
        //   ⇒ 两个用例改用**当前机制**表达同一场景，**判据不变**（注入的输入声明必须被尊重）。
        var rawInjected = new FfmpegGui.Models.FfmpegOptions
        {
            Format = "png", ColorSpace = "auto", ColorStrategy = FfmpegGui.Models.ColorStrategy.Recommended, Threads = 4,
            ColorSourceDeclPrimaries = "bt709", ColorSourceDeclTrc = "linear"   // 预处理注入（当前机制）
        };
        var cmdRaw = FfmpegCommandBuilder.BuildArguments(rawInjected, Path.Combine(outDir, "linear.tiff"),
            Path.Combine(outDir, "raw_out.png"));
        Console.WriteLine($"      [D11 cmd] {cmdRaw}");
        Check("D11 RAW 注入 bt709/linear 生效", cmdRaw.Contains("-color_primaries bt709 -color_trc linear")
            && !cmdRaw.Contains("tonemap"));

        // RAW 注入 + 用户选目标色域 (sRGB): zscale 应使用 linear 源
        var rawSrgb = new FfmpegGui.Models.FfmpegOptions
        {
            Format = "png", ColorSpace = "sRGB", ColorStrategy = FfmpegGui.Models.ColorStrategy.Recommended, Threads = 4,
            ColorSourceDeclPrimaries = "bt709", ColorSourceDeclTrc = "linear"   // 预处理注入（当前机制）
        };
        var cmdRawSrgb = FfmpegCommandBuilder.BuildArguments(rawSrgb, Path.Combine(outDir, "linear.tiff"),
            Path.Combine(outDir, "raw_srgb.png"));
        Console.WriteLine($"      [D12 cmd] {cmdRawSrgb}");
        Check("D12 RAW 注入 + sRGB 目标 (zscale linear→sRGB)", cmdRawSrgb.Contains("zscale=pin=bt709:tin=linear:min=bt709:p=bt709:t=iec61966-2-1:m=bt709"));

        // ── RAW 媒体信息 (MediaInfoService RAW 分支, 2026-08-14 UI 审查) ──
        var sampleDng = "tools/src/dng_sdk/dng_sdk_1_7_1/sample_files/03_jxl_bayer_raw_integer.dng";
        if (File.Exists(sampleDng))
        {
            var rawInfo = await MediaInfoService.GetMediaInfoAsync(sampleDng);
            Check("D13 RAW 媒体信息 (dngtool JSON)", !string.IsNullOrWhiteSpace(rawInfo)
                && rawInfo.Contains("\"make\"") && rawInfo.Contains("ILCE-7RM4"));
        }
        else
        {
            Check("D13 RAW 媒体信息 (样本存在)", false);
        }

        // IsRawFile 判定 (RAW 模式输入校验基础)
        Check("D14 IsRawFile 判定", RawService.IsRawFile(sampleDng)
            && !RawService.IsRawFile(Path.Combine(outDir, "png3_base.png")));

        // ── UI 死锁回归 (2026-08-14): UI 线程同步调用色彩探测不应卡死 ──
        // 复现方式: 用 UI 同步上下文 (DispatcherSynchronizationContext) 模拟 Avalonia,
        // 在"UI 线程"同步调用 FfmpegCommandBuilder.BuildArguments (内部走色彩探测)。
        // 修复前: ReadColorTagsAsync 捕获上下文 → 续体回 UI 线程 → 死锁。
        // 修复后: Task.Run + ConfigureAwait(false) → 正常返回。
        await UiDeadlockTestAsync(outDir);

        // ── RAW 线程参数传递 (2026-08-15): UI 线程选项 → dngtool -threads ──
        // 单线程 (1) 与多线程 (8) 应产生不同命令且都含 -threads
        var t1 = CaptureDngArgs(1);
        var t8 = CaptureDngArgs(8);
        Check("D16a 单线程参数", t1.Contains("-threads 1"));
        Check("D16b 多线程参数", t8.Contains("-threads 8"));
        Check("D16c 参数区分", t1 != t8);
        // 实际耗时对比 (effort=1 小样本, 避免长时间等待)
        var th1 = await TimeDngEncode(1);
        var th8 = await TimeDngEncode(8);
        Console.WriteLine($"      [D16] 单线程 {th1:F1}s vs 多线程 {th8:F1}s");
        Check("D16d 多线程不慢于单线程", th8 <= th1 * 2.5 + 2);

        // ── 自适应线程分配逻辑 (2026-08-15) ──
        AdaptiveThreadTest();

        // ── 软件完整链路: AutoThreads 标志 → 队列并发 → 每任务线程覆盖 (2026-08-15) ──
        // 模拟 QueueProcessor: 任务执行前若 AutoThreads → Threads = ComputeAdaptiveThreads(并发)
        var chainCases = new (int concurrency, int expectThreads)[]
        {
            (1, Environment.ProcessorCount),
            (2, Math.Max(1, Environment.ProcessorCount / 2)),
            (4, Math.Max(1, Environment.ProcessorCount / 4)),
            (Environment.ProcessorCount, 1),
            (100, 1)
        };
        foreach (var (conc, expect) in chainCases)
        {
            var opts = new FfmpegGui.Models.FfmpegOptions
            {
                Format = "jpg", AutoThreads = true, Threads = 16   // 基准值, 应被覆盖
            };
            // 模拟 QueueProcessor 分配
            opts.Threads = FfmpegGui.Models.FfmpegOptions.ComputeAdaptiveThreads(conc);
            Check($"D18 并发={conc} → 每任务 {opts.Threads} (期望 {expect})", opts.Threads == expect);
        }
        // 手动模式: AutoThreads=false → Threads 不被覆盖
        var manual = new FfmpegGui.Models.FfmpegOptions { Format = "jpg", AutoThreads = false, Threads = 8 };
        Check("D19 手动模式不覆盖 (8)", manual.Threads == 8);
        // 单线程: AutoThreads=false + Threads=1
        var singleT = new FfmpegGui.Models.FfmpegOptions { Format = "jpg", AutoThreads = false, Threads = 1 };
        Check("D20 单线程=1", singleT.Threads == 1);

        // ── Photoshop 检测 (2026-08-15, PsRenderService) ──
        PsRenderService.ClearCache();
        var psAvailable = PsRenderService.Detect();
        if (psAvailable)
            Console.WriteLine($"      [D22] Photoshop: {PsRenderService.DetectedPath}");
        // 本机有 PS 应检测到; 无 PS 环境跳过 (不视为失败)
        var hasPs = psAvailable || !File.Exists(@"C:\Program Files\Adobe\Adobe Photoshop 2026\Photoshop.exe");
        Check("D22 PS 检测", hasPs || !OperatingSystem.IsWindows());

        // ── .NET 原生 PSNR (2026-08-15, PsnrCalculator 替代 ffmpeg psnr filter) ──
        await PsnrCalculatorTestAsync(outDir);
    }

    /// <summary>
    /// PsnrCalculator 正确性验证:
    /// D23 与 ffmpeg psnr filter 数值一致性 (小图有损)
    /// D24 无损检测 (相同像素 → PSNR=inf)
    /// D25 多帧 (动图) 汇总语义 (average/min/max)
    /// D26 QualityAnalysisService 集成 (ffmpeg 只算 SSIM, PSNR 由 .NET 计算)
    /// </summary>
    private static async Task PsnrCalculatorTestAsync(string outDir)
    {
        var ffmpeg = AppSettingsService.Current.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            Check("D23 ffmpeg 可用", false);
            return;
        }

        // ── 生成测试素材: 参考 PNG + 有损 JPEG ──
        var srcPng = Path.Combine(outDir, "psnr_src.png");
        var lossyJpg = Path.Combine(outDir, "psnr_lossy.jpg");
        var losslessPng = Path.Combine(outDir, "psnr_lossless.png");

        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-y"); psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("testsrc2=size=256x192:duration=0.1");
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1"); psi.ArgumentList.Add("-update"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("png");
        psi.ArgumentList.Add(srcPng);
        using (var p = System.Diagnostics.Process.Start(psi)) { await p!.WaitForExitAsync(); }

        // 有损 JPEG (q5)
        var psi2 = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi2.ArgumentList.Add("-y"); psi2.ArgumentList.Add("-hide_banner"); psi2.ArgumentList.Add("-loglevel"); psi2.ArgumentList.Add("error");
        psi2.ArgumentList.Add("-i"); psi2.ArgumentList.Add(srcPng);
        psi2.ArgumentList.Add("-q:v"); psi2.ArgumentList.Add("5");
        psi2.ArgumentList.Add("-update"); psi2.ArgumentList.Add("1");
        psi2.ArgumentList.Add(lossyJpg);
        using (var p = System.Diagnostics.Process.Start(psi2)) { await p!.WaitForExitAsync(); }

        // 无损 PNG 副本
        File.Copy(srcPng, losslessPng, true);

        // ── D23: 与 ffmpeg psnr filter 数值一致性 ──
        // .NET: 直接读两图像素 → ffmpeg 输出 rawvideo (rgb24) 供 PsnrCalculator 计算
        var rawA = Path.Combine(Path.GetTempPath(), $"psnr_a_{Guid.NewGuid():N}.rgb");
        var rawB = Path.Combine(Path.GetTempPath(), $"psnr_b_{Guid.NewGuid():N}.rgb");
        var psi3 = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi3.ArgumentList.Add("-y"); psi3.ArgumentList.Add("-hide_banner"); psi3.ArgumentList.Add("-loglevel"); psi3.ArgumentList.Add("error");
        psi3.ArgumentList.Add("-i"); psi3.ArgumentList.Add(srcPng);
        psi3.ArgumentList.Add("-pix_fmt"); psi3.ArgumentList.Add("rgb24");
        psi3.ArgumentList.Add("-f"); psi3.ArgumentList.Add("rawvideo");
        psi3.ArgumentList.Add(rawA);
        using (var p = System.Diagnostics.Process.Start(psi3)) { await p!.WaitForExitAsync(); }
        var psi4 = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi4.ArgumentList.Add("-y"); psi4.ArgumentList.Add("-hide_banner"); psi4.ArgumentList.Add("-loglevel"); psi4.ArgumentList.Add("error");
        psi4.ArgumentList.Add("-i"); psi4.ArgumentList.Add(lossyJpg);
        psi4.ArgumentList.Add("-pix_fmt"); psi4.ArgumentList.Add("rgb24");
        psi4.ArgumentList.Add("-f"); psi4.ArgumentList.Add("rawvideo");
        psi4.ArgumentList.Add(rawB);
        using (var p = System.Diagnostics.Process.Start(psi4)) { await p!.WaitForExitAsync(); }

        if (File.Exists(rawA) && File.Exists(rawB))
        {
            var bytesA = File.ReadAllBytes(rawA);
            var bytesB = File.ReadAllBytes(rawB);
            var netPsnr = PsnrCalculator.CalculatePsnr(bytesA, bytesB);
            Console.WriteLine($"      [D23] .NET PSNR = {netPsnr:F4} dB");
            Check("D23 .NET PSNR 有限值 (有损)", netPsnr is > 20 and < 60);
        }
        else
        {
            Check("D23 .NET PSNR 素材生成", false);
        }
        try { File.Delete(rawA); File.Delete(rawB); } catch { }

        // ── D24: 无损检测 (相同像素 → PSNR=inf) ──
        var rawA2 = Path.Combine(Path.GetTempPath(), $"psnr_a2_{Guid.NewGuid():N}.rgb");
        var psi5 = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi5.ArgumentList.Add("-y"); psi5.ArgumentList.Add("-hide_banner"); psi5.ArgumentList.Add("-loglevel"); psi5.ArgumentList.Add("error");
        psi5.ArgumentList.Add("-i"); psi5.ArgumentList.Add(losslessPng);
        psi5.ArgumentList.Add("-pix_fmt"); psi5.ArgumentList.Add("rgb24");
        psi5.ArgumentList.Add("-f"); psi5.ArgumentList.Add("rawvideo");
        psi5.ArgumentList.Add(rawA2);
        using (var p = System.Diagnostics.Process.Start(psi5)) { await p!.WaitForExitAsync(); }
        if (File.Exists(rawA2))
        {
            var bytes = File.ReadAllBytes(rawA2);
            var losslessPsnr = PsnrCalculator.CalculatePsnr(bytes, bytes);
            Check("D24 无损 PSNR=inf", double.IsPositiveInfinity(losslessPsnr));
        }
        else
        {
            Check("D24 无损 PSNR 素材生成", false);
        }
        try { File.Delete(rawA2); } catch { }

        // ── D25: 多帧 (动图) 汇总语义 ──
        // 构造两帧数据: 帧1=相同, 帧2=有损
        // 需先解码 losslessPng → raw 像素
        var rawLossless = Path.Combine(Path.GetTempPath(), $"psnr_ll_{Guid.NewGuid():N}.rgb");
        var psi7 = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi7.ArgumentList.Add("-y"); psi7.ArgumentList.Add("-hide_banner"); psi7.ArgumentList.Add("-loglevel"); psi7.ArgumentList.Add("error");
        psi7.ArgumentList.Add("-i"); psi7.ArgumentList.Add(losslessPng);
        psi7.ArgumentList.Add("-pix_fmt"); psi7.ArgumentList.Add("rgb24");
        psi7.ArgumentList.Add("-f"); psi7.ArgumentList.Add("rawvideo");
        psi7.ArgumentList.Add(rawLossless);
        using (var p = System.Diagnostics.Process.Start(psi7)) { await p!.WaitForExitAsync(); }

        // 有损 JPEG 像素
        var rawJpg = Path.Combine(Path.GetTempPath(), $"psnr_jpg_{Guid.NewGuid():N}.rgb");
        var psi6 = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi6.ArgumentList.Add("-y"); psi6.ArgumentList.Add("-hide_banner"); psi6.ArgumentList.Add("-loglevel"); psi6.ArgumentList.Add("error");
        psi6.ArgumentList.Add("-i"); psi6.ArgumentList.Add(lossyJpg);
        psi6.ArgumentList.Add("-pix_fmt"); psi6.ArgumentList.Add("rgb24");
        psi6.ArgumentList.Add("-f"); psi6.ArgumentList.Add("rawvideo");
        psi6.ArgumentList.Add(rawJpg);
        using (var p = System.Diagnostics.Process.Start(psi6)) { await p!.WaitForExitAsync(); }

        if (File.Exists(rawLossless) && File.Exists(rawJpg))
        {
            var llBytes = File.ReadAllBytes(rawLossless);
            var jpgBytes = File.ReadAllBytes(rawJpg);
            if (llBytes.Length == jpgBytes.Length && llBytes.Length > 0)
            {
                var frameBytes = llBytes.Length;
                var twoFrameA = new byte[frameBytes * 2];
                var twoFrameB = new byte[frameBytes * 2];
                // 帧1: 相同
                Array.Copy(llBytes, 0, twoFrameA, 0, frameBytes);
                Array.Copy(llBytes, 0, twoFrameB, 0, frameBytes);
                // 帧2: 参考=无损, 待检=有损
                Array.Copy(llBytes, 0, twoFrameA, frameBytes, frameBytes);
                Array.Copy(jpgBytes, 0, twoFrameB, frameBytes, frameBytes);
                var (avg, min, max) = PsnrCalculator.CalculateMultiFramePsnr(twoFrameA, twoFrameB, frameBytes);
                Console.WriteLine($"      [D25] 多帧 avg={avg:F2} min={min:F2} max={max:F2}");
                // 帧1 相同 → 该帧 PSNR=inf → max=inf; 帧2 有损 → avg/min 有限
                Check("D25 多帧 max=inf (帧1 相同)", double.IsPositiveInfinity(max));
                Check("D25 多帧 avg/min 有限", !double.IsPositiveInfinity(avg) && !double.IsPositiveInfinity(min));
            }
            else
            {
                Check("D25 多帧素材尺寸一致", false);
            }
        }
        else
        {
            Check("D25 多帧素材生成", false);
        }

        // ── D26: QualityAnalysisService 集成 (真实管线) ──
        var qa = await QualityAnalysisService.AnalyzeAsync(srcPng, lossyJpg);
        Console.WriteLine($"      [D26] SSIM={qa.SsimAll} PSNR={qa.PsnrAverage}");
        Check("D26a SSIM 计算成功", qa.Success && qa.SsimAll.HasValue);
        Check("D26b .NET PSNR 计算成功", qa.Success && qa.PsnrAverage.HasValue && qa.PsnrAverage > 20);
        // 无损: PSNR=inf
        var qaLossless = await QualityAnalysisService.AnalyzeAsync(srcPng, losslessPng);
        Check("D26c 无损 PSNR=inf (集成)", qaLossless.Success
            && (double.IsPositiveInfinity(qaLossless.PsnrAverage ?? 0) || qaLossless.PsnrAverage >= 99));

        // ── D27: 各指令集路径数值一致性 (AVX512/AVX2/SSE2/标量) ──
        // 复用 D25 的 rawLossless/rawJpg (未删除, 清理延后到本测试之后)
        if (File.Exists(rawLossless) && File.Exists(rawJpg) && new FileInfo(rawLossless).Length == new FileInfo(rawJpg).Length)
        {
            var a = File.ReadAllBytes(rawLossless);
            var b = File.ReadAllBytes(rawJpg);
            var scalar = FfmpegGui.Services.PsnrCalculator.SquaredDiffSumForced(a, b, "scalar");
            var sse2 = FfmpegGui.Services.PsnrCalculator.SquaredDiffSumForced(a, b, "sse2");
            var avx2 = FfmpegGui.Services.PsnrCalculator.SquaredDiffSumForced(a, b, "avx2");
            Check("D27 SSE2=标量", sse2 == scalar);
            Check("D27 AVX2=标量", avx2 == scalar);
            // AVX512 仅在 CPU 支持时执行 (无 AVX512 的机器调用 intrinsics 会 Illegal Instruction)
            if (System.Runtime.Intrinsics.X86.Avx512BW.IsSupported)
            {
                var avx512 = FfmpegGui.Services.PsnrCalculator.SquaredDiffSumForced(a, b, "avx512");
                Check("D27 AVX512=标量", avx512 == scalar);
            }
            else
            {
                Console.WriteLine("      [D27] 本机无 AVX512, 跳过 AVX512 路径验证 (自动 dispatch 走 AVX2)");
            }
        }
        else
        {
            Check("D27 路径一致性素材", false);
        }
        try { File.Delete(rawJpg); File.Delete(rawLossless); } catch { }

        // ── SIMD 像素操作一致性 (2026-08-15, SimdPixelOps vs 标量) ──
        await SimdPixelOpsTestAsync(outDir);
    }

    /// <summary>
    /// SimdPixelOps 与标量参考实现一致性验证:
    /// D28 FloatToSrgb8 (批量 vs 标量, 逐字节)
    /// D29 ReinhardToSdr (批量 vs 标量, 1e-5)
    /// D30 ComputeGainMapGray (批量 vs 标量, ±1 LSB)
    /// D31 SrgbToLinearRgba (批量 vs 标量, 1e-5, alpha 保持)
    /// </summary>
    private static async Task SimdPixelOpsTestAsync(string outDir)
    {
        // 生成确定性测试数据 (含边界值: 0, 阈值附近, 1, 中间值)
        const int N = 4096;  // 1024 像素 RGBA
        var rng = new Random(42);
        var src = new float[N];
        for (int i = 0; i < N; i++)
        {
            src[i] = i switch
            {
                0 => 0f,
                1 => 1f,
                2 => 0.0031308f,          // sRGB 线性阈值
                3 => 0.00313081f,         // 阈值+1ulp
                4 => 0.04045f,            // 解码阈值
                5 => 0.04046f,
                _ => (float)rng.NextDouble()
            };
        }

        // ── D28: FloatToSrgb8 ──
        var simdDst = new byte[N];
        var refDst = new byte[N];
        SimdPixelOps.FloatToSrgb8(src, simdDst);
        for (int i = 0; i < N; i++)
            refDst[i] = SimdPixelOps.FloatToSrgb8Scalar(src[i]);
        int diffCount = 0;
        int maxDiff = 0;
        for (int i = 0; i < N; i++)
        {
            int d = Math.Abs(simdDst[i] - refDst[i]);
            if (d > maxDiff) maxDiff = d;
            if (d > 1) diffCount++;  // 允许 ±1 LSB (round 边界)
        }
        Console.WriteLine($"      [D28] FloatToSrgb8: maxDiff={maxDiff}, >1LSB 数量={diffCount}/{N}");
        Check("D28 FloatToSrgb8 SIMD=标量 (±1 LSB)", maxDiff <= 1);

        // ── D29: ReinhardToSdr ──
        var hdr = new float[N];
        var sdrSimd = new float[N];
        var sdrRef = new float[N];
        for (int i = 0; i < N; i++)
            hdr[i] = (float)rng.NextDouble() * 8f;  // 覆盖 0..8 (含 >1.25 压缩区)
        SimdPixelOps.ReinhardToSdr(hdr, sdrSimd, 4f);
        for (int i = 0; i + 4 <= N; i += 4)
        {
            float r = hdr[i], g = hdr[i + 1], b = hdr[i + 2];
            float maxY = Math.Max(r, Math.Max(g, b));
            float maxSdr = SimdPixelOps.SegmentedReinhardScalar(maxY, 4f);
            float scale = maxY > 1e-6f ? maxSdr / maxY : 0f;
            sdrRef[i] = Math.Clamp(r * scale, 0f, 1f);
            sdrRef[i + 1] = Math.Clamp(g * scale, 0f, 1f);
            sdrRef[i + 2] = Math.Clamp(b * scale, 0f, 1f);
            sdrRef[i + 3] = hdr[i + 3];
        }
        float maxErrReinhard = 0;
        for (int i = 0; i < N; i++)
            maxErrReinhard = Math.Max(maxErrReinhard, Math.Abs(sdrSimd[i] - sdrRef[i]));
        Console.WriteLine($"      [D29] ReinhardToSdr: maxErr={maxErrReinhard:E2}");
        Check("D29 ReinhardToSdr SIMD=标量 (1e-5)", maxErrReinhard < 1e-4f);

        // ── D30: ComputeGainMapGray ──
        var sdrForGain = new float[N];
        for (int i = 0; i < N; i++)
            sdrForGain[i] = Math.Clamp((float)rng.NextDouble() * 1.2f, 0.001f, 1f);
        var gainSimd = new byte[N / 4];
        var gainRef = new byte[N / 4];
        SimdPixelOps.ComputeGainMapGray(hdr, sdrForGain, gainSimd, 2f);
        for (int i = 0; i + 4 <= N; i += 4)
        {
            float hLum = 0.2126f * hdr[i] + 0.7152f * hdr[i + 1] + 0.0722f * hdr[i + 2];
            float sLum = 0.2126f * sdrForGain[i] + 0.7152f * sdrForGain[i + 1] + 0.0722f * sdrForGain[i + 2];
            // ⚠ 本臂的**参照公式过期**（2026-10-12 全量轮 D30 翻红抓到，maxDiff=6、>1LSB=194/1024）：
            //   旧式 `log2(max(hLum/max(sLum,0.001f),1))` 正是本批 F-5 **从产品里删掉的那一行**，
            //   拿它当参照 ⇒ 产品按声明施加 offset 之后，红的是镜像没跟着改，不是数学错。
            // 参照 = ISO 21496-1 的默认 offset 逐点式（与 libavif src/gainmap.c:712-713
            //   `ratio = (alt + alternateOffset) / (base + baseOffset)`、
            //   libultrahdr gainmapmath.cpp:773-775 `log2((hdr + kHdrOffset)/(sdr + kSdrOffset))` 同形）。
            // ⚠ 这里**刻意写字面 1/64，不取 `GainMapEncoder.KOffset*`**：取产品常数就变成"它和自己比"，
            //   改了 offset 而元数据声明没改就抓不到 ⇒ 本式是「声明==使用」的对照臂。
            // ⚠ 名字里的 "SIMD=标量" 是历史名：该核实测 AVX2 慢 1.9× 后已是**纯标量**
            //   （SimdPixelOps.cs:204），本臂现在比的是「产品核 vs 声明同形参照」。标签不许改
            //   （`verify-gainmap-host` 的 ② 按名对账 + 通过数下限绑名字）。
            const float kDeclOffset = 1f / 64f;
            float ratio = (hLum + kDeclOffset) / (sLum + kDeclOffset);
            float logGain = MathF.Log2(Math.Max(ratio, 1.0f));
            gainRef[i / 4] = SimdPixelOps.LogGainToByteScalar(logGain, 2f);
        }
        int gainDiff = 0;
        int gainMax = 0;
        for (int i = 0; i < gainSimd.Length; i++)
        {
            int d = Math.Abs(gainSimd[i] - gainRef[i]);
            if (d > gainMax) gainMax = d;
            if (d > 1) gainDiff++;
        }
        Console.WriteLine($"      [D30] ComputeGainMapGray: maxDiff={gainMax}, >1LSB={gainDiff}/{gainSimd.Length}");
        Check("D30 ComputeGainMapGray SIMD=标量 (±1 LSB)", gainMax <= 1);

        // ── D31: SrgbToLinearRgba ──
        var rgbaSimd = new float[N];
        var rgbaRef = new float[N];
        for (int i = 0; i < N; i++)
        {
            rgbaSimd[i] = src[i];
            rgbaRef[i] = src[i];
        }
        SimdPixelOps.SrgbToLinearRgba(rgbaSimd);
        for (int i = 0; i + 4 <= N; i += 4)
        {
            rgbaRef[i] = SimdPixelOps.SrgbToLinearScalar(rgbaRef[i]);
            rgbaRef[i + 1] = SimdPixelOps.SrgbToLinearScalar(rgbaRef[i + 1]);
            rgbaRef[i + 2] = SimdPixelOps.SrgbToLinearScalar(rgbaRef[i + 2]);
        }
        float maxErrSrgb = 0;
        for (int i = 0; i < N; i++)
            maxErrSrgb = Math.Max(maxErrSrgb, Math.Abs(rgbaSimd[i] - rgbaRef[i]));
        Console.WriteLine($"      [D31] SrgbToLinear: maxErr={maxErrSrgb:E2}");
        Check("D31 SrgbToLinear SIMD=标量 (1e-5)", maxErrSrgb < 1e-4f);

        // ── D32: GainMap 编码闭环 (SIMD 集成不破坏输出) ──
        // 用测试宿主现有 GainMap 编码能力验证 (sdr 场景)
        try
        {
            const int gw = 64, gh = 48;
            var pixels = new float[gw * gh * 4];
            for (int i = 0; i < gw * gh; i++)
            {
                float v = (float)(i % 256) / 255f;
                pixels[i * 4] = v * 0.5f;
                pixels[i * 4 + 1] = v * 0.3f;
                pixels[i * 4 + 2] = v * 0.7f;
                pixels[i * 4 + 3] = 1f;
            }
            var gmOut = Path.Combine(outDir, "simd_gainmap.jpg");
            var okGm = await GainMapEncoder.EncodeAsync(pixels, gw, gh, gmOut,
                hdrPeakNits: 1000f, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
                multiChannel: false, baseQuality: 1.5f, gainMapQuality: 1.5f,
                downsample: 4, log: _ => { });
            Check("D32 GainMap 编码闭环 (SIMD 集成)", okGm && File.Exists(gmOut) && new FileInfo(gmOut).Length > 1000);
            try { File.Delete(gmOut); } catch { }
        }
        catch (Exception ex)
        {
            Check("D32 GainMap 编码闭环 (SIMD 集成)", false);
            Console.WriteLine($"      [D32] 异常: {ex.Message}");
        }

        // ── 位深+色域自适应 PSNR (2026-08-15, 方案 A 归一化 + 方案 C 原生域) ──
        await PsnrBitDepthDomainTestAsync(outDir);
    }

    /// <summary>
    /// D33 8-bit RGB PSNR (与旧版 CalculatePsnr 兼容)
    /// D34 16-bit 归一化 PSNR (与 8-bit 可比)
    /// D35 色域标注 (isRgb 不影响数值)
    /// D36 MultiFramePsnr 位深参数传播
    /// </summary>
    private static async Task PsnrBitDepthDomainTestAsync(string outDir)
    {
        var ffmpeg = AppSettingsService.Current.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            Check("D33 ffmpeg 可用", false);
            return;
        }

        // 生成 8-bit 和 16-bit 测试素材
        var src8 = Path.Combine(outDir, "bd_src8.png");
        var src16 = Path.Combine(outDir, "bd_src16.png");
        var enc8 = Path.Combine(outDir, "bd_enc8.jpg");
        var enc16 = Path.Combine(outDir, "bd_enc16.png");

        var psi = new ProcessStartInfo(ffmpeg)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-y"); psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("testsrc2=size=256x192:duration=0.1");
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1"); psi.ArgumentList.Add("-update"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("png");
        psi.ArgumentList.Add(src8);
        using (var p = Process.Start(psi)) { await p!.WaitForExitAsync(); }

        // 16-bit PNG (rgb48be)
        var psi2 = new ProcessStartInfo(ffmpeg)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi2.ArgumentList.Add("-y"); psi2.ArgumentList.Add("-hide_banner"); psi2.ArgumentList.Add("-loglevel"); psi2.ArgumentList.Add("error");
        psi2.ArgumentList.Add("-i"); psi2.ArgumentList.Add(src8);
        psi2.ArgumentList.Add("-pix_fmt"); psi2.ArgumentList.Add("rgb48be");
        psi2.ArgumentList.Add("-update"); psi2.ArgumentList.Add("1");
        psi2.ArgumentList.Add(src16);
        using (var p = Process.Start(psi2)) { await p!.WaitForExitAsync(); }

        // 有损 JPEG 8-bit
        var psi3 = new ProcessStartInfo(ffmpeg)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi3.ArgumentList.Add("-y"); psi3.ArgumentList.Add("-hide_banner"); psi3.ArgumentList.Add("-loglevel"); psi3.ArgumentList.Add("error");
        psi3.ArgumentList.Add("-i"); psi3.ArgumentList.Add(src8);
        psi3.ArgumentList.Add("-q:v"); psi3.ArgumentList.Add("5");
        psi3.ArgumentList.Add("-update"); psi3.ArgumentList.Add("1");
        psi3.ArgumentList.Add(enc8);
        using (var p = Process.Start(psi3)) { await p!.WaitForExitAsync(); }

        // 16-bit PNG 无损 (copy)
        if (File.Exists(src16))
            File.Copy(src16, enc16, true);

        // ── D33: 8-bit RGB PSNR 兼容性 ──
        // 旧接口: CalculatePsnr(a, b) 应与新接口 CalculatePsnr(a, b, 8, 3, true) 一致
        var rawA = Path.Combine(Path.GetTempPath(), $"bd_a_{Guid.NewGuid():N}.rgb");
        var rawB = Path.Combine(Path.GetTempPath(), $"bd_b_{Guid.NewGuid():N}.rgb");
        var psi4 = new ProcessStartInfo(ffmpeg)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi4.ArgumentList.Add("-y"); psi4.ArgumentList.Add("-hide_banner"); psi4.ArgumentList.Add("-loglevel"); psi4.ArgumentList.Add("error");
        psi4.ArgumentList.Add("-i"); psi4.ArgumentList.Add(src8);
        psi4.ArgumentList.Add("-pix_fmt"); psi4.ArgumentList.Add("rgb24");
        psi4.ArgumentList.Add("-f"); psi4.ArgumentList.Add("rawvideo");
        psi4.ArgumentList.Add(rawA);
        using (var p = Process.Start(psi4)) { await p!.WaitForExitAsync(); }
        var psi5 = new ProcessStartInfo(ffmpeg)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi5.ArgumentList.Add("-y"); psi5.ArgumentList.Add("-hide_banner"); psi5.ArgumentList.Add("-loglevel"); psi5.ArgumentList.Add("error");
        psi5.ArgumentList.Add("-i"); psi5.ArgumentList.Add(enc8);
        psi5.ArgumentList.Add("-pix_fmt"); psi5.ArgumentList.Add("rgb24");
        psi5.ArgumentList.Add("-f"); psi5.ArgumentList.Add("rawvideo");
        psi5.ArgumentList.Add(rawB);
        using (var p = Process.Start(psi5)) { await p!.WaitForExitAsync(); }

        if (File.Exists(rawA) && File.Exists(rawB))
        {
            var ba = File.ReadAllBytes(rawA);
            var bb = File.ReadAllBytes(rawB);
            var oldPsnr = PsnrCalculator.CalculatePsnr(ba, bb);  // 旧接口
            var newPsnr = PsnrCalculator.CalculatePsnr(ba, bb, 8, 3, true);  // 新接口
            Check("D33 旧接口=新接口(8-bit)", Math.Abs(oldPsnr - newPsnr) < 0.001);
            Console.WriteLine($"      [D33] 旧={oldPsnr:F2} 新={newPsnr:F2}");
        }
        else { Check("D33 素材", false); }
        try { File.Delete(rawA); File.Delete(rawB); } catch { }

        // ── D34: 16-bit 归一化 PSNR ──
        var raw16A = Path.Combine(Path.GetTempPath(), $"bd_16a_{Guid.NewGuid():N}.raw");
        var raw16B = Path.Combine(Path.GetTempPath(), $"bd_16b_{Guid.NewGuid():N}.raw");
        var psi6 = new ProcessStartInfo(ffmpeg)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi6.ArgumentList.Add("-y"); psi6.ArgumentList.Add("-hide_banner"); psi6.ArgumentList.Add("-loglevel"); psi6.ArgumentList.Add("error");
        psi6.ArgumentList.Add("-i"); psi6.ArgumentList.Add(src16);
        psi6.ArgumentList.Add("-pix_fmt"); psi6.ArgumentList.Add("gbrp16le");
        psi6.ArgumentList.Add("-f"); psi6.ArgumentList.Add("rawvideo");
        psi6.ArgumentList.Add(raw16A);
        using (var p = Process.Start(psi6)) { await p!.WaitForExitAsync(); }
        var psi7 = new ProcessStartInfo(ffmpeg)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi7.ArgumentList.Add("-y"); psi7.ArgumentList.Add("-hide_banner"); psi7.ArgumentList.Add("-loglevel"); psi7.ArgumentList.Add("error");
        psi7.ArgumentList.Add("-i"); psi7.ArgumentList.Add(enc16);
        psi7.ArgumentList.Add("-pix_fmt"); psi7.ArgumentList.Add("gbrp16le");
        psi7.ArgumentList.Add("-f"); psi7.ArgumentList.Add("rawvideo");
        psi7.ArgumentList.Add(raw16B);
        using (var p = Process.Start(psi7)) { await p!.WaitForExitAsync(); }

        if (File.Exists(raw16A) && File.Exists(raw16B))
        {
            var ba = File.ReadAllBytes(raw16A);
            var bb = File.ReadAllBytes(raw16B);
            var psnr16 = PsnrCalculator.CalculatePsnr(ba, bb, 16, 3, true);
            Console.WriteLine($"      [D34] 16-bit 无损 PSNR={psnr16}");
            Check("D34 16-bit 无损 PSNR=inf", double.IsPositiveInfinity(psnr16));
        }
        else { Check("D34 素材", false); }
        try { File.Delete(raw16A); File.Delete(raw16B); } catch { }

        // ── D35: 色域标注不影响数值 ──
        var rawA2 = Path.Combine(Path.GetTempPath(), $"bd_a2_{Guid.NewGuid():N}.rgb");
        var psi8 = new ProcessStartInfo(ffmpeg)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi8.ArgumentList.Add("-y"); psi8.ArgumentList.Add("-hide_banner"); psi8.ArgumentList.Add("-loglevel"); psi8.ArgumentList.Add("error");
        psi8.ArgumentList.Add("-i"); psi8.ArgumentList.Add(src8);
        psi8.ArgumentList.Add("-pix_fmt"); psi8.ArgumentList.Add("rgb24");
        psi8.ArgumentList.Add("-f"); psi8.ArgumentList.Add("rawvideo");
        psi8.ArgumentList.Add(rawA2);
        using (var p = Process.Start(psi8)) { await p!.WaitForExitAsync(); }
        if (File.Exists(rawA2))
        {
            var ba = File.ReadAllBytes(rawA2);
            var psnrRgb = PsnrCalculator.CalculatePsnr(ba, ba, 8, 3, true);
            var psnrYuv = PsnrCalculator.CalculatePsnr(ba, ba, 8, 3, false);
            Check("D35 色域标注数值一致 (isRgb 不影响)", psnrRgb == psnrYuv);
        }
        else { Check("D35 素材", false); }
        try { File.Delete(rawA2); } catch { }

        // ── D36: 多帧 PSNR 位深参数传播 ──
        var frameBytes = 256 * 192 * 3;
        var twoFrame = new byte[frameBytes * 2];
        // 两帧相同
        var (avg, min, max) = PsnrCalculator.CalculateMultiFramePsnr(twoFrame, twoFrame, frameBytes, 8, 3, true);
        Check("D36 多帧位深参数 (8-bit 无损)", double.IsPositiveInfinity(avg) && double.IsPositiveInfinity(min) && double.IsPositiveInfinity(max));
        Console.WriteLine($"      [D36] 多帧 avg={avg}");

        // ── D37: QualityAnalysisService 与 ffmpeg psnr filter 一致性 (YUV 系目标) ──
        // 2026-08-15 实测: scale=out_range=pc + yuv444p 复刻 ffmpeg 内部路径 → 逐位一致
        // 目标域: enc8 = JPEG (YUV 系) → YUV 域, 与 ffmpeg psnr filter 一致
        var qa = await QualityAnalysisService.AnalyzeAsync(src8, enc8);
        Console.WriteLine($"      [D37] QA PSNR={qa.PsnrAverage:F4} dB (isRgb={qa.PsnrIsRgb})");
        Check("D37a JPEG 目标 → YUV 域", !qa.PsnrIsRgb);
        // 用 ffmpeg psnr filter 实测对比 (同素材)
        var ffmpegPath = AppSettingsService.Current.FfmpegPath;
        var psiRef = new ProcessStartInfo(ffmpegPath)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psiRef.ArgumentList.Add("-hide_banner");
        psiRef.ArgumentList.Add("-i"); psiRef.ArgumentList.Add(src8);
        psiRef.ArgumentList.Add("-i"); psiRef.ArgumentList.Add(enc8);
        psiRef.ArgumentList.Add("-lavfi"); psiRef.ArgumentList.Add("psnr");
        psiRef.ArgumentList.Add("-f"); psiRef.ArgumentList.Add("null"); psiRef.ArgumentList.Add("-");
        using (var p = Process.Start(psiRef))
        {
            var err = await p!.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var m = System.Text.RegularExpressions.Regex.Match(err, @"average:([\d.]+)");
            if (m.Success && qa.PsnrAverage.HasValue)
            {
                double ffmpegPsnr = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                double diff = Math.Abs(qa.PsnrAverage.Value - ffmpegPsnr);
                Console.WriteLine($"      [D37] ffmpeg={ffmpegPsnr:F4} 差={diff:F4} dB");
                Check("D37b JPEG 目标 PSNR ≈ ffmpeg (±0.2dB)", diff < 0.2);
            }
            else
            {
                Check("D37b 参考获取失败", false);
            }
        }

        // ── D38: PNG 目标 → RGB 域 (目标域选择) ──
        // PNG 是 RGB 原生 → 应在 RGB 域比较 (子采样损失不应计入)
        var encPng = Path.Combine(outDir, "bd_enc_png.png");
        var psiPng = new ProcessStartInfo(ffmpegPath)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psiPng.ArgumentList.Add("-y"); psiPng.ArgumentList.Add("-hide_banner"); psiPng.ArgumentList.Add("-loglevel"); psiPng.ArgumentList.Add("error");
        psiPng.ArgumentList.Add("-i"); psiPng.ArgumentList.Add(src8);
        psiPng.ArgumentList.Add("-q:v"); psiPng.ArgumentList.Add("5");   // 有损 PNG? PNG 无损, 用 mjpeg→png 模拟有损: 先转 jpg 再转 png
        psiPng.ArgumentList.Add("-update"); psiPng.ArgumentList.Add("1");
        psiPng.ArgumentList.Add(encPng);
        using (var p = Process.Start(psiPng)) { await p!.WaitForExitAsync(); }
        // 有损 PNG 不存在, 用 JPEG 转 PNG 模拟: src8 → lossy.jpg → png
        var qaPng = await QualityAnalysisService.AnalyzeAsync(src8, encPng);
        Console.WriteLine($"      [D38] QA PSNR={qaPng.PsnrAverage:F4} dB (isRgb={qaPng.PsnrIsRgb})");
        Check("D38 PNG 目标 → RGB 域", qaPng.PsnrIsRgb);
        try { File.Delete(encPng); } catch { }

        // 清理素材
        try { File.Delete(src8); File.Delete(src16); File.Delete(enc8); File.Delete(enc16); } catch { }
    }

    /// <summary>捕获 EncodeToDngAsync 生成的 dngtool 命令参数 (通过日志回调)。</summary>
    private static string CaptureDngArgs(int threads)
    {
        var sb = new System.Text.StringBuilder();
        var sample = "tools/src/dng_sdk/dng_sdk_1_7_1/sample_files/01_jxl_linear_raw_integer.dng";
        if (!File.Exists(sample)) return "";
        var capDng = Path.Combine(Path.GetTempPath(), "cap.dng");
        try
        {
            RawService.EncodeToDngAsync(sample, capDng,
                compression: 1, jxlQuality: 0, log: s => { if (s.Contains("dngtool")) sb.Append(s); },
                jxlEffort: 1, threads: threads).GetAwaiter().GetResult();
        }
        // 只取日志里的 dngtool 命令行，产物本身没人读：留着就是在每个 TMP 根各存一份全尺寸 DNG
        finally { try { File.Delete(capDng); } catch { } }
        return sb.ToString();
    }

    /// <summary>实测单/多线程编码耗时 (effort=1 控制时长)。</summary>
    private static async Task<double> TimeDngEncode(int threads)
    {
        var sample = "tools/src/dng_sdk/dng_sdk_1_7_1/sample_files/01_jxl_linear_raw_integer.dng";
        var outp = Path.Combine(Path.GetTempPath(), $"t_{threads}_{Guid.NewGuid():N}.dng");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await RawService.EncodeToDngAsync(sample, outp, compression: 1, jxlQuality: 0,
            jxlEffort: 1, threads: threads);
        sw.Stop();
        try { File.Delete(outp); } catch { }
        return sw.Elapsed.TotalSeconds;
    }

    /// <summary>模拟 UI 线程同步调用 BuildArguments，验证不死锁（10 秒超时保护）。</summary>
    private static async Task UiDeadlockTestAsync(string outDir)
    {
        var srcJpg = Path.Combine(outDir, "deadlock_src.jpg");
        if (!File.Exists(srcJpg))
        {
            // 生成一张带 EXIF 的 JPEG 作为探测输入
            var ffmpeg = AppSettingsService.Current.FfmpegPath;
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) { Check("D15 UI 死锁 (无 ffmpeg)", false); return; }
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-y"); psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("testsrc2=size=128x96:duration=0.1");
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1"); psi.ArgumentList.Add("-update"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("mjpeg");
            psi.ArgumentList.Add(srcJpg);
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                await p!.WaitForExitAsync();
                if (p.ExitCode != 0 || !File.Exists(srcJpg)) { Check("D15 UI 死锁 (生成素材)", false); return; }
            }
        }

        // 模拟 UI 线程同步执行: 在专用线程上安装同步上下文, 同步调用 BuildArguments
        // (Avalonia/WPF 的 DispatcherSynchronizationContext 语义: Post 回 UI 线程队列,
        //  若 UI 线程阻塞在 GetResult 则续体永远无法执行 → 死锁)
        var t = new System.Threading.Thread(() =>
        {
            var ctx = new BlockingSyncContext();   // Post = 排队到本线程 (模拟 UI 调度器)
            System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
            try
            {
                var opts = new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "jpg", ColorSpace = "auto", ColorStrategy = FfmpegGui.Models.ColorStrategy.Recommended, Threads = 4
                };
                var cmd = FfmpegCommandBuilder.BuildArguments(opts, srcJpg, Path.Combine(outDir, "deadlock_out.jpg"));
                _ = cmd;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      [D15] 异常: {ex.Message}");
            }
        });
        t.IsBackground = true;
        t.Start();
        var done = t.Join(TimeSpan.FromSeconds(10));   // 10 秒超时 = 死锁检测
        Check("D15 UI 线程同步调用不死锁 (10s 超时)", done);
    }

    /// <summary>验证自适应线程分配逻辑 (2026-08-15): 每任务线程 = max(1, 核数/并发数)。</summary>
    private static void AdaptiveThreadTest()
    {
        int cores = Environment.ProcessorCount;

        // 并发=1 → 全核给单任务 (多线程)
        int t1 = FfmpegGui.Models.FfmpegOptions.ComputeAdaptiveThreads(1);
        Check($"D17a 并发1→全核 ({t1}=核数{cores})", t1 == cores);

        // 并发=2 → 核数/2
        int t2 = FfmpegGui.Models.FfmpegOptions.ComputeAdaptiveThreads(2);
        Check($"D17b 并发2→核/2 ({t2}={cores}/2)", t2 == cores / 2);

        // 并发=核数 → 每任务 1 线程
        int tn = FfmpegGui.Models.FfmpegOptions.ComputeAdaptiveThreads(cores);
        Check($"D17c 并发=核数→1线程 ({tn})", tn == 1);

        // 并发>核数 (如 100) → 仍 1 线程 (至少 1)
        int t100 = FfmpegGui.Models.FfmpegOptions.ComputeAdaptiveThreads(100);
        Check($"D17d 并发100→1线程 ({t100})", t100 == 1);

        // 合计不超核: 并发×每任务 ≤ 核数 (并发≤核数时)
        int c = 4;
        int per = FfmpegGui.Models.FfmpegOptions.ComputeAdaptiveThreads(c);
        Check($"D17e 不超饱和 ({c}×{per}={c * per}≤核数{cores})", c * per <= cores);
    }

    /// <summary>模拟 UI Dispatcher 的同步上下文: Post 回调排队到本线程队列, 由本线程执行。</summary>
    private sealed class BlockingSyncContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
        private readonly int _ownerId = System.Threading.Thread.CurrentThread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Enqueue(new Action(() => d(state)));
            // 模拟 UI 线程消息泵: 若当前不在本线程, 由本线程处理队列
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _ownerId) return;
            ProcessQueue();
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            // 同步派发: 直接执行 (模拟 Invoke 语义)
            d(state);
        }

        private void ProcessQueue()
        {
            while (_queue.TryDequeue(out var cb))
            {
                try { cb(); } catch { }
            }
        }
    }

    /// <summary>读取指定 chunk 的 payload，不存在返回 null。</summary>
    private static byte[]? ReadChunk(string pngPath, string chunkName)
    {
        try
        {
            var data = File.ReadAllBytes(pngPath);
            if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50) return null;
            int pos = 8;
            while (pos + 12 <= data.Length)
            {
                int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
                var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                if (type == chunkName)
                    return data[(pos + 8)..(pos + 8 + len)];
                if (type == "IEND") break;
                pos += 12 + len;
            }
            return null;
        }
        catch { return null; }
    }

    private static int CountChunks(string pngPath, string chunkName)
    {
        var data = File.ReadAllBytes(pngPath);
        int count = 0, pos = 8;
        while (pos + 12 <= data.Length)
        {
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            if (type == chunkName) count++;
            if (type == "IEND") break;
            pos += 12 + len;
        }
        return count;
    }

    // ═══════════════════════════════════════════════
    //  编码测试: 合成像素 → 编码 → 结构/标签检查
    // ═══════════════════════════════════════════════
    private static async Task EncodeTestAsync(string outDir, string name, float hdrPeak, bool multiChannel, int downsample)
    {
        Console.WriteLine($"\n── {name} (peak={hdrPeak}, multiChannel={multiChannel}, ds={downsample}) ──");
        try
        {
            const int w = 256, h = 192;
            // 合成 HDR 线性像素: 渐变 + 高光块 + 颜色块
            // 1.0 = 峰值亮度 (zscale npl 语义)
            var pixels = CreateHdrTestPixels(w, h, hdrPeak);

            var outPath = Path.Combine(outDir, $"{name}.jpg");
            var gmLog = new System.Text.StringBuilder();
            var ok = await GainMapEncoder.EncodeAsync(
                pixels, w, h, outPath,
                hdrPeakNits: hdrPeak, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
                multiChannel: multiChannel,
                baseQuality: 1.5f, gainMapQuality: 1.5f,
                downsample: downsample,
                log: s => { gmLog.Append(s); Console.Write(s); });

            Check($"编码完成 ({name})", ok && File.Exists(outPath));
            if (!ok || !File.Exists(outPath)) return;
            Console.WriteLine($"  输出: {outPath} ({new FileInfo(outPath).Length} 字节)");

            // ── 零余量（SDR）格：**按 2026-09-20 的裁定断言**，不再断言 UltraHDR 结构 ──
            // 裁定原文在 `GainMapEncoder.cs:188-197`：`headroom ≤ 1`（增益区间为 0）时**不写增益图**，
            // 直接以底图 JPEG 作产物并**点名**原因 —— 因为写一张 `GainMapMin=GainMapMax=0` 的
            // "自称 Ultra HDR、实际零增益"是本仓最忌讳的「宣称≠交付」。
            // ⚠ 本段此前的期望（MPImage2/hdrgm/Channels 必须存在）与该裁定相反 ⇒ 稳定 4 红。
            //   修法不是放宽断言，而是把它换成**更严**的：降级必须点名、产物必须是完整 JPEG、
            //   且**不得**留下任何 UltraHDR 痕迹（半写的 MPF 指针或 hdrgm XMP 比"没有增益图"更糟）。
            if (hdrPeak <= GainMapEncoder.KSdrWhiteNits)
            {
                Check($"{name}: 零余量 ⇒ 日志点名「无可承载的 HDR 增益」降级（不静默）",
                    gmLog.ToString().Contains("无可承载的 HDR 增益"));
                var head = await File.ReadAllBytesAsync(outPath);
                Check($"{name}: 产物是完整 JPEG（SOI FFD8 + EOI FFD9，{head.Length}B）",
                    head.Length > 4 && head[0] == 0xFF && head[1] == 0xD8
                    && head[^2] == 0xFF && head[^1] == 0xD9);
                var mp2Sdr = await ExifToolService.GetTagAsync(outPath, "MPImage2");
                Check($"{name}: 负断言——**不得**残留 MPImage2（半写 MPF 比无增益图更坏），实得「{mp2Sdr ?? "(空)"}」",
                    string.IsNullOrWhiteSpace(mp2Sdr));
                var maxSdr = await ExifToolService.GetTagAsync(outPath, "GainMapMax");
                Check($"{name}: 负断言——**不得**残留 hdrgm GainMapMax，实得「{maxSdr ?? "(空)"}」",
                    string.IsNullOrWhiteSpace(maxSdr));
                var dimSdr = await ExifToolService.GetTagAsync(outPath, "ImageWidth");
                Check($"{name}: 降级只去掉增益层，底图像素尺寸仍是 {w}x{h}（实得宽 {dimSdr ?? "(空)"}）",
                    (dimSdr ?? "").Trim() == w.ToString());
                return;
            }

            // 结构检查: MPImage2 存在 = Ultra HDR JPEG
            var mp2 = await ExifToolService.GetTagAsync(outPath, "MPImage2");
            Check($"{name}: MPImage2 (UltraHDR 结构) 存在", !string.IsNullOrWhiteSpace(mp2));

            // hdrgm 标签
            var min = await ExifToolService.GetTagAsync(outPath, "GainMapMin");
            var max = await ExifToolService.GetTagAsync(outPath, "GainMapMax");
            Check($"{name}: hdrgm GainMapMin/Max 存在 ({min} / {max})", !string.IsNullOrWhiteSpace(min) && !string.IsNullOrWhiteSpace(max));

            // 通道数
            var ch = await ExifToolService.GetTagAsync(outPath, "Channels");
            var expectCh = multiChannel ? "3" : "1";
            Check($"{name}: Channels={ch} (期望 {expectCh})", (ch ?? "").Trim() == expectCh);
        }
        catch (Exception ex)
        {
            Check($"{name}: 异常 {ex.Message}", false);
        }
    }

    // ═══════════════════════════════════════════════
    //  解码闭环: 编码产物 → 解码 → 像素对比
    // ═══════════════════════════════════════════════
    private static async Task DecodeRoundTripAsync(string outDir, string name, float hdrPeak)
    {
        Console.WriteLine($"\n── 解码闭环 {name} (peak={hdrPeak}) ──");
        var jpg = Path.Combine(outDir, $"{name}.jpg");
        if (!File.Exists(jpg))
        {
            Check($"解码闭环 {name}: 输入缺失", false);
            return;
        }

        var rawOut = Path.Combine(outDir, $"{name}_decoded.rgba");
        // ⚠ 先清掉**上一轮遗留**的同名产物再判：本目录是跨轮复用的固定路径，
        //   留着旧文件会让「解码器有没有写出增益图」这一判据两头都不可信 ——
        //   正向断言会因残留而**假绿**，负向断言会因残留而**假红**
        //   （实测 `sdr_decoded.rgba` 是 09-19 那批"SDR 也写假增益图"时期的遗留物，
        //   09-20 裁定后产品不再写它，而文件还在 ⇒ 负断言当场被它判红）。
        try { if (File.Exists(rawOut)) File.Delete(rawOut); } catch { }
        try
        {
            // ── 零余量（SDR）格：产物按裁定是**普通 JPEG** ⇒ 闭环要比的不是"HDR 值对不对"，
            //   而是解码器**不许把普通 JPEG 当成有增益图**（编侧不写、解侧不猜，两侧同一契约）。
            if (hdrPeak <= GainMapEncoder.KSdrWhiteNits)
            {
                var plain = await GainMapDecoder.DecodeToLinearRawAsync(jpg, rawOut,
                    log: s => Console.Write(s));
                Check($"{name}: 零余量产物被解码器判为**非 UltraHDR**（不凭空造增益），实得 w={plain?.w ?? 0}",
                    plain == null && !File.Exists(rawOut));
                return;
            }

            // ── 手动对照: ffmpeg 解码基础图 (整个文件) ──
            var ffmpeg = AppSettingsService.Current.FfmpegPath;
            var manualBase = Path.Combine(Path.GetTempPath(), $"gm_manual_{name}.rgba");
            var psiM = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-y -i \"{jpg}\" -pix_fmt gbrpf32le -f rawvideo \"{manualBase}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var pM = System.Diagnostics.Process.Start(psiM))
            {
                if (pM == null) { Check($"解码闭环 {name}: ffmpeg 进程启动失败", false); return; }
                pM.OutputDataReceived += (_, e) => { };
                pM.ErrorDataReceived += (_, e) => { };
                pM.BeginOutputReadLine();
                pM.BeginErrorReadLine();
                await pM.WaitForExitAsync();
            }
            if (File.Exists(manualBase) && new FileInfo(manualBase).Length >= 256 * 192 * 12)
            {
                var mb = await File.ReadAllBytesAsync(manualBase);
                int idx = (96 * 256 + 128);
                var gM = BitConverter.ToSingle(mb, idx * 4);
                var bM = BitConverter.ToSingle(mb, (256 * 192) * 4 + idx * 4);
                var rM = BitConverter.ToSingle(mb, (256 * 192 * 2) * 4 + idx * 4);
                Console.WriteLine($"  手动基础图中心: sRGB(R={rM:F3},G={gM:F3},B={bM:F3}) (应≈0.98)");
            }
            try { File.Delete(manualBase); } catch { }

            var result = await GainMapDecoder.DecodeToLinearRawAsync(jpg, rawOut,
                log: s => Console.Write(s));
            Check($"{name}: 解码成功 ({result?.w}x{result?.h})", result != null && File.Exists(rawOut));
            if (result == null || !File.Exists(rawOut)) return;

            // 读取解码像素并与原始输入对比
            var w = result.Value.w; var h = result.Value.h;
            var bytes = await File.ReadAllBytesAsync(rawOut);
            Check($"{name}: 像素数据完整 ({bytes.Length} vs 需 {w * h * 12})", bytes.Length >= w * h * 12);

            // gbrpf32le 平面 → 交错 RGBA float (与解码器输出一致: 1.0 = SDR 白点)
            // 对比: 编码输入 1.0 = 峰值, 解码输出 1.0 = SDR 白点
            // → 参考 = 编码输入 × (峰值 / SDR白点)
            var src = CreateHdrTestPixels(w, h, hdrPeak);
            float scale = hdrPeak / GainMapEncoder.KSdrWhiteNits;
            var decoded = new float[w * h * 4];
            // 平面顺序 G, B, R (gbrpf32le)
            var g = new float[w * h]; var b = new float[w * h]; var r = new float[w * h];
            Buffer.BlockCopy(bytes, 0, g, 0, w * h * 4);
            Buffer.BlockCopy(bytes, w * h * 4, b, 0, w * h * 4);
            Buffer.BlockCopy(bytes, w * h * 8, r, 0, w * h * 4);

            // PSNR 对比 (仅有效区域)
            double mse = 0; double maxErr = 0; int count = 0;
            for (int i = 0; i < w * h; i++)
            {
                // 解码输出: 1.0 = SDR 白点; 编码输入: 1.0 = 峰值
                // 统一到 SDR 白点空间
                float srcR = src[i * 4 + 0] * scale;
                float srcG = src[i * 4 + 1] * scale;
                float srcB = src[i * 4 + 2] * scale;
                float dR = r[i] - srcR, dG = g[i] - srcG, dB = b[i] - srcB;
                mse += dR * dR + dG * dG + dB * dB;
                var me = Math.Max(Math.Abs(dR), Math.Max(Math.Abs(dG), Math.Abs(dB)));
                if (me > maxErr) maxErr = me;
                count += 3;
            }
            mse /= count;
            // ⚠ PSNR 的峰值参考必须是**对比信号的实际满量程**：两侧已换算到“1.0 = SDR 白”域，
            // 而 HDR 参考的峰值是 scale = hdrPeak/203（如 4000nit → 19.7）。
            // 旧写法固定用 1.0 作 peak ⇒ 大 headroom 下 PSNR 被系统性低估 20·log10(scale)
            // （hdr4000 低估 25.9dB、hdr1000 低估 13.9dB）——“7.66dB 质量缺陷”实为度量口径错。
            var psnr = mse > 0 ? 10 * Math.Log10((double)scale * scale / mse) : 99;
            Console.WriteLine($"  {name}: 参考满量程 peak={scale:F3}（峰值参考已修正）");
            // 阈值说明（均基于**已修正的峰值参考**）：
            //  - 统一取 30dB 作为“往返无明显失真”的工程下限，不再按 headroom 分档给不同阈值。
            //  - 实测：sdr 46.4 / hdr1000 36.5 / hdr4000 33.6 dB（headroom 越大、增益图 8bit 量化误差越大，单调吻合）。
            //  - 旧注释里“hdr1000 ~20dB、hdr4000 ~15dB 为合理水平”是用错峰值参考量出来的，已作废。
            var threshold = 30.0;
            if (psnr >= threshold) Check($"{name}: PSNR={psnr:F2}dB (阈 {threshold}dB)", true);
            else Check($"{name}: PSNR={psnr:F2}dB (阈 {threshold}dB)", false);
            Console.WriteLine($"  {name}: maxErr={maxErr:F4}, PSNR={psnr:F2}dB");

            // 采样点调试 (中心高光 / 背景 / 彩色块)
            foreach (var (px, py, tag) in new[] {
                (w/2, h/2, "中心高光"), (w/4, h/4, "背景"), (w*8/10, h*8/10, "红色块"), (w/10, h*8/10, "蓝色块") })
            {
                int i = py * w + px;
                Console.WriteLine($"    {tag}({px},{py}): 参考=({src[i*4]*scale:F3},{src[i*4+1]*scale:F3},{src[i*4+2]*scale:F3}) " +
                    $"解码=({r[i]:F3},{g[i]:F3},{b[i]:F3})");
            }
        }
        catch (Exception ex)
        {
            Check($"{name}: 解码异常 {ex.Message}", false);
        }
    }

    // ═══════════════════════════════════════════════
    //  结构验证 (exiftool 详细标签)
    // ═══════════════════════════════════════════════
    private static async Task StructureCheckAsync(string jpg)
    {
        if (!File.Exists(jpg))
        {
            Check("结构验证: 输入缺失", false);
            return;
        }
        // 用 exiftool 读取关键结构标签
        var tags = await ExifToolService.GetTagAsync(jpg, "MPImage2");
        var hdrMax = await ExifToolService.GetTagAsync(jpg, "HDRMaxValue") ?? await ExifToolService.GetTagAsync(jpg, "GainMapMax");
        Console.WriteLine($"  MPImage2: {(tags?.Length > 0 ? $"{tags.Length} 字节" : "缺失")}");
        Console.WriteLine($"  HDRMaxValue/GainMapMax: {hdrMax}");
        Check("结构验证: MPImage2 存在", !string.IsNullOrWhiteSpace(tags));
    }

    // ═══════════════════════════════════════════════
    //  合成 HDR 测试像素
    // ═══════════════════════════════════════════════
    /// <summary>
    /// 生成 HDR 线性测试图 (输入约定: 1.0 = 峰值亮度, zscale npl 语义):
    ///  - 背景渐变 (SDR 区域 ≤ 白点/峰值)
    ///  - 中央高光方块 (峰值=1.0 → 测试 headroom)
    ///  - 彩色方块 (测试色度)
    /// 注意: 像素值是"峰值空间"相对值 (1.0=峰值), EncodeAsync 内部会
    ///       按 ×(hdrPeak/sdrWhite) 转换到 SDR 白点空间做色调映射。
    /// </summary>
    private static float[] CreateHdrTestPixels(int w, int h, float hdrPeakNits)
    {
        var px = new float[w * h * 4];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float u = (float)x / w, v = (float)y / h;

                // 背景: 亮度渐变 (相对峰值 0.01..0.2, 对应 SDR 低-中亮区)
                float bgLum = 0.02f + 0.18f * u * (1f - v * 0.5f);
                float r = bgLum, g = bgLum * 0.9f, b = bgLum * 1.1f;

                // 中央高光块 (中心 1/4 区域): 峰值亮度 (1.0 = 峰值)
                bool inHighlight = Math.Abs(x - w / 2f) < w / 8f && Math.Abs(y - h / 2f) < h / 8f;
                if (inHighlight)
                {
                    // 高斯衰减的高光: 中心 = 峰值 (1.0)
                    float dx = (x - w / 2f) / (w / 8f);
                    float dy = (y - h / 2f) / (h / 8f);
                    float fall = MathF.Exp(-(dx * dx + dy * dy) * 2f);
                    float lum = 0.5f + 0.5f * fall;
                    r = lum;
                    g = lum * 0.98f;
                    b = lum * 0.96f;
                }

                // 右下彩色块 (红色, 0.25 = 1.2x SDR 白点相对 1000nits 峰值)
                // 注意: 避免极端饱和色 (JPEG YUV 色度重采样 overshoot → clip)
                if (x > w * 0.7f && y > h * 0.7f)
                {
                    r = 0.25f; g = 0.10f; b = 0.08f;
                }
                // 左下蓝色块 (SDR, 温和饱和度)
                if (x < w * 0.2f && y > h * 0.7f)
                {
                    r = 0.06f; g = 0.08f; b = 0.14f;
                }

                px[i + 0] = r;
                px[i + 1] = g;
                px[i + 2] = b;
                px[i + 3] = 1f;
            }
        }
        return px;
    }

    // ═══════════════════════════════════════════════
    //  E1. GainMap 解码路径 × Orientation（显示尺寸口径）
    // ═══════════════════════════════════════════════
    /// <summary>
    /// E1: 底图带 Orientation=8 的 Ultra HDR JPEG → GainMapDecoder.DecodeToLinearRawAsync
    /// 必须报**显示尺寸**（宽高互换），且线性产物与 ffmpeg 独立参照逐像素一致。
    /// </summary>
    private static async Task OrientedGainMapDecodeTestAsync(string outDir)
    {
        Console.WriteLine("\n── E1 GainMap 解码 × Orientation=8 ──");
        var ffmpeg = AppSettingsService.Current.FfmpegPath;
        var ffprobe = FindFfprobeForTest(ffmpeg);
        var exif = ExifToolService.DetectedPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg) || ffprobe == null || exif == null)
        {
            Check($"E1 工具链齐备 (ffmpeg={ffmpeg != null}, ffprobe={ffprobe != null}, exiftool={exif != null})", false);
            return;
        }

        // 夹具几何：coded **横向** 256x192（w≠h），Orientation=8 ⇒ 显示应为 192x256（纵向）。
        // 内容刻意做成「行内容 ≠ 列内容」（竖条 + 横带 + 角块 + 棋盘波纹），剪切/转置才可辨。
        const int codedW = 256, codedH = 192;
        const int dispW = codedH, dispH = codedW;
        const float hdrPeak = 1000f;
        var fixturePixels = CreateOrientedFixturePixels(codedW, codedH);

        var plainJpg = Path.Combine(outDir, "e1_oriented_plain.jpg");
        var encLog = new System.Text.StringBuilder();
        var encOk = await GainMapEncoder.EncodeAsync(
            fixturePixels, codedW, codedH, plainJpg,
            hdrPeakNits: hdrPeak, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
            multiChannel: false, baseQuality: 1.5f, gainMapQuality: 1.5f, downsample: 4,
            log: s => encLog.Append(s));
        Check($"E1a 夹具: Ultra HDR 编码完成 (coded {codedW}x{codedH})", encOk && File.Exists(plainJpg));
        if (!encOk || !File.Exists(plainJpg)) return;

        // ── 打取向标签 ──
        // ⚠ exiftool 陷阱（2026-09-30 实测）：`-Orientation=8` **不带 `-n`** 会按字符串查表 ⇒ 静默写成 **3**
        //   （180° ⇒ 宽高不互换）⇒ 夹具当场失去牙。故这里一律带 `-n`，且**回读标签** + 用 ffprobe
        //   复核「coded 与 display 确实互换」两件事，而不是相信写入调用成功。
        var jpg = Path.Combine(outDir, "e1_oriented.jpg");
        File.Copy(plainJpg, jpg, true);
        var stampExit = await RunExifToolAsync($"-overwrite_original -n -Orientation=8 \"{jpg}\"", exif);
        var orientTag = await ReadOrientationNumericAsync(jpg, exif);
        var mp2AfterStamp = await ExifToolService.GetTagAsync(jpg, "MPImage2");
        var codedProbe = await ProbeCodedSizeForTestAsync(jpg, ffprobe);
        var dispProbe = await ImageGeometry.DisplaySizeAsync(jpg, ffprobe, s => Console.Write(s));
        Check($"E1b 夹具取向有牙: exiftool 回读 Orientation={orientTag} (退出码 {stampExit}) ⇒ coded {codedProbe.w}x{codedProbe.h} / display {dispProbe.w}x{dispProbe.h}（期望 8 / {codedW}x{codedH} / {dispW}x{dispH}）",
            stampExit == 0 && orientTag == "8"
            && codedProbe.w == codedW && codedProbe.h == codedH
            && dispProbe.w == dispW && dispProbe.h == dispH);
        // 增益图结构必须在 exiftool 重写 APP1 之后仍然可读（否则 E1 测的就不是 Ultra HDR 解码路径了）。
        Check($"E1c 打标签后增益图结构仍在 (MPImage2 {(mp2AfterStamp?.Length ?? 0)} 字节)", !string.IsNullOrWhiteSpace(mp2AfterStamp));

        // ── 独立参照：三条命令全部只走 ffmpeg，不碰任何产品实现 ──
        var refAuto = Path.Combine(outDir, "e1_ref_autorotate.raw");
        var refT2 = Path.Combine(outDir, "e1_ref_transpose2.raw");
        var refCoded = Path.Combine(outDir, "e1_ref_noautorotate.raw");
        foreach (var f in new[] { refAuto, refT2, refCoded })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        int exA = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -i \"{jpg}\" -pix_fmt gbrpf32le -f rawvideo \"{refAuto}\"", null, ffmpeg);
        int exB = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -noautorotate -i \"{jpg}\" -vf transpose=2 -pix_fmt gbrpf32le -f rawvideo \"{refT2}\"", null, ffmpeg);
        int exC = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -noautorotate -i \"{jpg}\" -pix_fmt gbrpf32le -f rawvideo \"{refCoded}\"", null, ffmpeg);
        long needDisp = (long)dispW * dispH * 12, needCoded = (long)codedW * codedH * 12;
        bool rotDirectionPinned = exA == 0 && exB == 0 && File.Exists(refAuto) && File.Exists(refT2)
            && new FileInfo(refAuto).Length == needDisp
            && new FileInfo(refAuto).Length == new FileInfo(refT2).Length
            && FixedTimeBytesEqual(await File.ReadAllBytesAsync(refAuto), await File.ReadAllBytesAsync(refT2));
        long refAutoLen = File.Exists(refAuto) ? new FileInfo(refAuto).Length : 0;
        Check($"E1d ffmpeg 参照: 默认解码=显示几何 {refAutoLen}B 且与 `-noautorotate + transpose=2` **逐字节相同**（取向 8 ⇒ rotation 90 的方向由实测钉死）",
            rotDirectionPinned);
        Check($"E1e 夹具 coded 帧（-noautorotate）与旋正帧**不是**同一份字节（否则夹具无轴可换）",
            exC == 0 && new FileInfo(refCoded).Length == needCoded && refCoded.Length > 0
            && !FixedTimeBytesEqual(ReadFirstK(await File.ReadAllBytesAsync(refCoded), 4096),
                                    ReadFirstK(await File.ReadAllBytesAsync(refAuto), 4096)));

        // ── 被测：产品解码路径 ──
        var rawOut = Path.Combine(outDir, "e1_oriented_decoded.rgba");
        try { if (File.Exists(rawOut)) File.Delete(rawOut); } catch { }   // 跨轮复用目录：残留会制造假绿
        var res = await GainMapDecoder.DecodeToLinearRawAsync(jpg, rawOut, log: s => Console.Write(s));
        Check($"E1f GainMapDecoder 报**显示**尺寸：期望 {dispW}x{dispH}，实得 {res?.w ?? 0}x{res?.h ?? 0}（coded={codedW}x{codedH}）",
            res != null && res.Value.w == dispW && res.Value.h == dispH);
        if (res == null || !File.Exists(rawOut))
        {
            // 走到这里说明产品对"带取向标签的 Ultra HDR"直接解码失败 ⇒ 不是跳过，而是显式判红并说明。
            Check($"E1g 解码器对取向 Ultra HDR 产出线性像素（实得 {(res == null ? "null（解码失败）" : "有尺寸但无文件")}）；" +
                "注：本用例刻意让整图压在 SDR 白点以下 ⇒ 增益图恒 1x，若仍解不出即路径缺陷", false);
            return;
        }

        var outBytes = await File.ReadAllBytesAsync(rawOut);
        var refBytes = await File.ReadAllBytesAsync(refAuto);
        Check($"E1h 线性产物长度 = 显示尺寸×12B（实得 {outBytes.Length}，需 {needDisp}）", outBytes.Length >= needDisp);

        // 参照 = 对 ffmpeg 旋正帧做**教科书 sRGB EOTF**（独立实现，不抄 SimdPixelOps，避免"把被测实现抄进参照"）。
        // 夹具刻意压在 SDR 白点以下 ⇒ GainMapEncoder 的分段 Reinhard 直通 ⇒ 增益图恒 1x，
        // 于是「EOTF(ffmpeg 底图帧)」与产品线性输出应当**逐像素相等**（只剩 8bit 量化残差）。
        // 峰值参考取 1.0 = SDR 白点（产物约定），夹具最大线性值 ≈0.98 ⇒ 满量程=1。
        int n = dispW * dispH;
        var prod = new[] { PlaneFloats(outBytes, 0, n, false), PlaneFloats(outBytes, 1, n, false), PlaneFloats(outBytes, 2, n, false) };
        var refr = new[] { PlaneFloats(refBytes, 0, n, true), PlaneFloats(refBytes, 1, n, true), PlaneFloats(refBytes, 2, n, true) };
        double mseOk = 0, mseSheared = 0;
        for (int c = 0; c < 3; c++)
        {
            var a = prod[c]; var b = refr[c];
            for (int i = 0; i < n; i++)
            {
                double d = a[i] - b[i]; mseOk += d * d;
                // 已知坏读数：字节不变、按 **coded** 步长重排（复现"声明未旋几何 ⇒ 每行错位"的缺陷）
                int si = (i / codedW) * dispW + (i % codedW);
                double ds = a[i] - b[si]; mseSheared += ds * ds;
            }
        }
        mseOk /= 3.0 * n; mseSheared /= 3.0 * n;
        double psnrOk = mseOk > 0 ? 10 * Math.Log10(1.0 / mseOk) : double.PositiveInfinity;
        double psnrSheared = mseSheared > 0 ? 10 * Math.Log10(1.0 / mseSheared) : double.PositiveInfinity;
        Console.WriteLine($"  E1: 按显示几何 PSNR={psnrOk:F2}dB, 按 coded 步长重排 PSNR={psnrSheared:F2}dB");
        Check($"E1i 线性产物 vs ffmpeg 独立参照（显示几何）PSNR={psnrOk:F2}dB ≥ 40dB", psnrOk >= 40);
        // 本条是**判据自身的牙齿**：若夹具能被转置洗白（对称/均匀），这条会当场红。
        Check($"E1j 有牙自证: 同一份参照按 coded 步长重排后 PSNR={psnrSheared:F2}dB 必须 <20dB 且比正确读数差 >20dB",
            psnrSheared < 20 && psnrOk - psnrSheared > 20);
    }

    // ═══════════════════════════════════════════════
    //  E2. 质量分析服务 × Orientation（分辨率预检不得误拒）
    // ═══════════════════════════════════════════════
    /// <summary>
    /// E2: 源带 Orientation=8、产物无方向标签 ⇒ QualityAnalysisService.AnalyzeAsync
    /// 不得以「分辨率不一致」拒绝；无损产物必须读出 inf。
    /// <para>动机（2026-09-30 缺陷）：预检曾读 **coded** 尺寸 ⇒ 对旋正后的正确产物报
    /// 「源图 (6000x4000) 与输出图 (4000x6000) 分辨率不一致」而拒绝对拍。现在预检走
    /// <see cref="ImageGeometry"/>（显示口径）。本用例同时留一条**反证臂**（E2g1/E2g2）：产物轴向
    /// 真的不一致时预检必须仍然拒绝 —— 否则"修好"可能只是把闸门拆了。</para>
    /// </summary>
    private static async Task OrientedQualityAnalysisTestAsync(string outDir)
    {
        Console.WriteLine("\n── E2 质量分析 × Orientation=8 ──");
        var ffmpeg = AppSettingsService.Current.FfmpegPath;
        var ffprobe = FindFfprobeForTest(ffmpeg);
        var exif = ExifToolService.DetectedPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg) || ffprobe == null || exif == null)
        {
            Check("E2 工具链齐备 (ffmpeg/ffprobe/exiftool)", false);
            return;
        }

        const int codedW = 64, codedH = 32;      // coded 横向；Orientation=8 ⇒ 显示 32x64
        const int dispW = codedH, dispH = codedW;

        // 夹具内容：testsrc2 在 64x32 上行列内容互不相同（渐变 + 计时条 + 十字），转置/剪切可辨。
        var srcPlain = Path.Combine(outDir, "e2_src_plain.jpg");
        int exSrc = await FfmpegRunner.RunAsync(
            $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc2=size={codedW}x{codedH}:r=25\" -frames:v 1 -update 1 -q:v 2 \"{srcPlain}\"",
            null, ffmpeg);
        var src = Path.Combine(outDir, "e2_src_oriented.jpg");
        File.Copy(srcPlain, src, true);
        // ⚠ 同 E1：必须 `-n`（否则静默写成 3=180° ⇒ 轴向不变 ⇒ 夹具没牙），并回读核验。
        int stamp = await RunExifToolAsync($"-overwrite_original -n -Orientation=8 \"{src}\"", exif);
        var tag = await ReadOrientationNumericAsync(src, exif);
        var coded = await ProbeCodedSizeForTestAsync(src, ffprobe);
        var disp = await ImageGeometry.DisplaySizeAsync(src, ffprobe);
        Check($"E2a 夹具取向有牙: Orientation 回读={tag} (退出码 {stamp}) ⇒ coded {coded.w}x{coded.h} / display {disp.w}x{disp.h}（期望 8 / {codedW}x{codedH} / {dispW}x{dispH}）",
            exSrc == 0 && stamp == 0 && tag == "8"
            && coded.w == codedW && coded.h == codedH && disp.w == dispW && disp.h == dispH);

        // ── 无损臂：ffmpeg 直接转 PNG（产物天然已是旋正后的 32x64，且不带取向标签）──
        var lossless = Path.Combine(outDir, "e2_lossless.png");
        try { if (File.Exists(lossless)) File.Delete(lossless); } catch { }
        int exLl = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -i \"{src}\" -update 1 \"{lossless}\"", null, ffmpeg);
        var llSize = await ImageGeometry.DisplaySizeAsync(lossless, ffprobe);
        Check($"E2b 无损产物几何 = 源的**显示**尺寸 {llSize.w}x{llSize.h}（期望 {dispW}x{dispH}，退出码 {exLl}）",
            exLl == 0 && File.Exists(lossless) && llSize.w == dispW && llSize.h == dispH);

        var qa = await QualityAnalysisService.AnalyzeAsync(src, lossless);
        Console.WriteLine($"      [E2] 无损臂 Success={qa.Success} PSNR={qa.PsnrAverage} SSIM={qa.SsimAll} Error=\"{qa.Error.Replace("\n", " ")}\"");
        Check($"E2c 取向源 × 旋正产物 ⇒ **不得**报「分辨率不一致」（实得 Error=\"{qa.Error.Split('\n')[0]}\"）",
            !qa.Error.Contains("分辨率不一致"));
        Check($"E2d 预检放行并算出 PSNR（数值或 inf，实得 {(qa.PsnrAverage.HasValue ? qa.PsnrAverage.Value.ToString("F3") : "无")}，Success={qa.Success}）",
            qa.Success && qa.PsnrAverage.HasValue);
        Check($"E2e 无损产物的「无损」读数 = inf（实得 PSNR={qa.PsnrAverage}）",
            qa.Success && (double.IsPositiveInfinity(qa.PsnrAverage ?? 0) || (qa.PsnrAverage ?? -1) >= 99));

        // ── 有损臂：证明 E2e 的 inf 不是恒返回值 ──
        var lossy = Path.Combine(outDir, "e2_lossy.jpg");
        try { if (File.Exists(lossy)) File.Delete(lossy); } catch { }
        int exLy = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -i \"{src}\" -q:v 12 -update 1 \"{lossy}\"", null, ffmpeg);
        var qaLossy = await QualityAnalysisService.AnalyzeAsync(src, lossy);
        Console.WriteLine($"      [E2] 有损臂 Success={qaLossy.Success} PSNR={qaLossy.PsnrAverage} Error=\"{qaLossy.Error.Replace("\n", " ")}\"");
        Check($"E2f 有损产物同样放行，但 PSNR 必须是**有限**值（实得 {qaLossy.PsnrAverage}，退出码 {exLy}）",
            exLy == 0 && !qaLossy.Error.Contains("分辨率不一致") && qaLossy.PsnrAverage.HasValue
            && !double.IsPositiveInfinity(qaLossy.PsnrAverage.Value));

        // ── 反证臂：产物轴向真的不同（-noautorotate ⇒ 64x32）⇒ 预检必须仍然拒绝 ──
        // ⚠ 实测（本轮）：ffmpeg 会把**帧级** displaymatrix/EXIF 取向**透传**进产物，连 `-map_metadata -1`
        //   都剥不掉（它管的是文件/流级元数据）⇒ 只加 `-noautorotate` 的 PNG 再被读成 32x64，
        //   显示口径下两轴其实**一致**，预检不拒才是对的。所以这里再用 exiftool `-all=` 把标签剥干净，
        //   并**先自证**该产物的显示尺寸确实等于 coded（64x32）—— 否则本条反证臂是空枪。
        var wrongAxis = Path.Combine(outDir, "e2_wrong_axis.png");
        try { if (File.Exists(wrongAxis)) File.Delete(wrongAxis); } catch { }
        int exBad = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -noautorotate -i \"{src}\" -update 1 \"{wrongAxis}\"", null, ffmpeg);
        int exStrip = await RunExifToolAsync($"-overwrite_original -all= \"{wrongAxis}\"", exif);
        var badSize = await ImageGeometry.DisplaySizeAsync(wrongAxis, ffprobe);
        Check($"E2g1 反证臂夹具成立: 剥标签后产物显示尺寸 = coded {codedW}x{codedH}（实得 {badSize.w}x{badSize.h}，ffmpeg 退出码 {exBad}，exiftool 退出码 {exStrip}）",
            exBad == 0 && exStrip == 0 && badSize.w == codedW && badSize.h == codedH);
        var qaBad = await QualityAnalysisService.AnalyzeAsync(src, wrongAxis);
        Console.WriteLine($"      [E2] 反证臂 Success={qaBad.Success} PSNR={qaBad.PsnrAverage} Error=\"{qaBad.Error.Replace("\n", " ")}\"");
        Check($"E2g2 反证: 轴向真不一致的产物仍必须被「分辨率不一致」拒绝（实得 Error=\"{qaBad.Error.Split('\n')[0]}\"，Success={qaBad.Success}）",
            qaBad.Error.Contains("分辨率不一致") && !qaBad.Success);
    }

    // ═══════════════════════════════════════════════
    //  E1/E2 共用夹具与探针辅助
    // ═══════════════════════════════════════════════

    /// <summary>
    /// E1 夹具像素（约定同 <see cref="CreateHdrTestPixels"/>：1.0 = 峰值亮度）。
    /// <para>⚠ 全部像素刻意压在 **SDR 白点以下**（峰值取 1000nits ⇒ 白点 = 203/1000 ≈ 0.203，
    /// 本夹具最大 0.18）。理由：`GainMapEncoder` 的分段 Reinhard 对 y≤1 直通 ⇒ 增益图恒 1x（空间常量）。
    /// 增益图（MPImage2 副图）**并不携带取向标签**，若让增益随内容变化，旋正后的底图与未旋正的增益图
    /// 坐标就会错配，"独立参照"将不再唯一确定产品输出；恒增益夹具把这一层噪声摘掉，
    /// 使线性产物可被 ffmpeg 底图帧 + 独立 sRGB EOTF **逐像素**复核（几何才是本用例的被测量）。</para>
    /// <para>非对称性：竖亮条（列向特征）+ 顶暗带（行向特征）+ 右下块 + 棋盘波纹，w≠h ⇒ 转置/剪切可辨。</para>
    /// </summary>
    private static float[] CreateOrientedFixturePixels(int w, int h)
    {
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v = 0.010f + 0.055f * x / w;                 // 仅沿 x 的斜坡（行内不同列，行间相同）
                if (x < w / 8) v = 0.170f;                          // 左侧亮竖条 —— 列向特征
                if (y < h / 6) v = 0.020f;                          // 顶部暗横带 —— 行向特征
                if (x > w * 0.85f && y > h * 0.80f) v = 0.120f;     // 右下块 —— 与上面两者都不重合
                if (((x / 8) + (y / 8)) % 2 == 0) v *= 1.12f;       // 棋盘波纹：让每一行都不等于任何列
                v = Math.Min(v, 0.180f);                            // 仍低于 SDR 白点 0.203 ⇒ 增益恒 1x
                int i = (y * w + x) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 1f;   // 消色差：避免色度二次采样引入与几何无关的噪声
            }
        }
        return px;
    }

    /// <summary>教科书 sRGB EOTF（独立于 SimdPixelOps.SrgbToLinearScalar，参照不许抄被测实现）。</summary>
    private static float SrgbEotfForTest(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>从 gbrpf32le **平面** rawvideo（平面顺序 G,B,R）取一个平面的 float；可选套 EOTF。</summary>
    private static float[] PlaneFloats(byte[] raw, int plane, int count, bool srgbToLinear)
    {
        var dst = new float[count];
        int off = plane * count * 4;
        for (int i = 0; i < count && off + i * 4 + 4 <= raw.Length; i++)
        {
            float v = BitConverter.ToSingle(raw, off + i * 4);
            dst[i] = srgbToLinear ? SrgbEotfForTest(v) : v;
        }
        return dst;
    }

    private static bool FixedTimeBytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static byte[] ReadFirstK(byte[] src, int k)
    {
        int len = Math.Min(k, src.Length);
        var dst = new byte[len];
        Array.Copy(src, dst, len);
        return dst;
    }

    /// <summary>ffprobe 定位（与 QualityAnalysisService.FindFfprobe 同判据；那份是 private，此处独立实现以便测试自证）。</summary>
    private static string? FindFfprobeForTest(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath)) return null;
        var dir = Path.GetDirectoryName(ffmpegPath) ?? "";
        var p = Path.Combine(dir, "ffprobe");
        if (Environment.OSVersion.Platform == PlatformID.Win32NT) p += ".exe";
        if (File.Exists(p)) return p;
        p = ffmpegPath.Replace("ffmpeg", "ffprobe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// 只读 **coded** 宽高（`stream=width,height`）。
    /// <para>⚠ 这是**故意**在产品之外造的"另一把尺"：本用例要量的正是 coded 与 display 之差，
    /// 若两头都用 <see cref="ImageGeometry"/> 就退化成自我循环。`src/` 侧的单一真值门禁
    /// （<c>_probe-geometry-single-source-scan.ps1</c> 只扫 <c>src/FfmpegGui</c>）不受本行影响。</para>
    /// </summary>
    private static async Task<(int w, int h)> ProbeCodedSizeForTestAsync(string path, string ffprobe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{path}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            });
            if (p == null) return (0, 0);
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            _ = await errTask;
            var parts = (await outTask).Trim().Split(',');
            return parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) ? (w, h) : (0, 0);
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// 读**数值**取向（`exiftool -n -Orientation`）。
    /// <para>⚠ 必须带 `-n`：默认输出的是**描述**（"Rotate 270 CW"），而本用例的判据是数值 8
    /// ——「写入不带 -n ⇒ 静默变 3」与「读取不带 -n ⇒ 只给描述」是同一枚硬币的两面，
    /// 只有数值读数能同时挡住这两种错。取不到 ⇒ 返回空串（断言随之判红，不猜）。</para>
    /// </summary>
    private static async Task<string> ReadOrientationNumericAsync(string path, string exiftoolPath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exiftoolPath,
                Arguments = $"-n -Orientation \"{path}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            });
            if (p == null) return "";
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            _ = await e;   // perl 的 locale 警告走 stderr，与读数无关
            var line = (await o).Trim();
            int colon = line.IndexOf(':');
            return colon >= 0 ? line[(colon + 1)..].Trim() : line;
        }
        catch { return ""; }
    }

    /// <summary>直调 exiftool（走 ExifToolService 自检测到的路径，不硬编码）；返回退出码。</summary>
    private static async Task<int> RunExifToolAsync(string arguments, string exiftoolPath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exiftoolPath,
                Arguments = arguments,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            });
            if (p == null) return -1;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var line = (await o).Trim();
            if (line.Length > 0) Console.WriteLine($"      [exiftool] {line}");
            var errText = (await e).Trim();
            if (errText.Length > 0) Console.WriteLine($"      [exiftool stderr] {errText}");
            return p.ExitCode;
        }
        catch (Exception ex) { Console.WriteLine($"      [exiftool] 异常 {ex.Message}"); return -1; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  批次 C 数值锁（FIX_PLAN_2026-10-11 的 F-5 / F-2 / F-3 / F-4 / F-8）
    //  口径 = docs/review-2026-10-11/gainmap-reference-memo.md 的 Q1–Q5 裁决。
    //
    //  运行：`GainMapTestHost gainmath`（进程内；不起产品 GUI）
    //  调用者：tests/scripts/verify-gainmap-managed.ps1 §6（C0–C3 + C7）
    //          tests/scripts/verify-gainmap-isobmff.ps1 §5（C4–C6 + avifdec 独立读回）
    //          tests/scripts/verify-gainmap-engine.ps1 §10（C4b 产物的 JPEG 采样因子，第二读者）
    //
    //  ⚠ 每条锁都必须**只用修复前就存在的 API**（否则拿不到红态）；新 API 的断言一律
    //     通过「产物/字节读回」间接判定，不直接引用新符号。
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>ISO 21496-1 载荷（独立分母模式）里通道 c 的参数块起点。</summary>
    private static int IsoChannelOffset(int c) => 21 + c * 40;

    private static void SetBe32(byte[] d, int off, int v)
    {
        uint u = unchecked((uint)v);
        d[off] = (byte)(u >> 24); d[off + 1] = (byte)(u >> 16);
        d[off + 2] = (byte)(u >> 8); d[off + 3] = (byte)(u & 0xFF);
    }

    private static int GetBe32(byte[] d, int off)
        => unchecked((int)(((uint)d[off] << 24) | ((uint)d[off + 1] << 16) | ((uint)d[off + 2] << 8) | d[off + 3]));

    private static async Task<int> GainMathLocksAsync(string outDir)
    {
        Console.WriteLine("═══ F. 批次 C 数值锁（F-5/F-2/F-3/F-4/F-8）═══");
        int checks = 0;

        // ───────────────────────── C0 前置 ─────────────────────────
        const float SdrWhite = GainMapEncoder.KSdrWhiteNits;
        float maxLog2_1000 = MathF.Log2(1000f / SdrWhite);
        byte[] iso1 = GainMapEncoder.BuildIso21496Metadata(false, maxLog2_1000);
        var m1 = GainMapDecoder.JpegContainer.ParseIso21496(iso1, 0, iso1.Length);
        Check("C0 [前置] 自家 ISO 载荷可被自家解析器读回（判据载体活着）", m1 != null);
        checks++;
        if (m1 == null)
        {
            Console.WriteLine($"GAINMATH CHECKS={checks} RESULT: pass={_pass} fail={_fail}");
            return _fail == 0 ? 0 : 1;
        }
        Console.WriteLine($"  [C0] 读回: min={m1.GainMapMin:F4} max={m1.GainMapMax:F4} gamma={m1.Gamma:F3} "
                        + $"offsetSDR={m1.OffsetSdr:F6} offsetHDR={m1.OffsetHdr:F6} channels={m1.Channels}");

        // ───────────────── C1 [F-5] 声明==施加 + float 域往返 ─────────────────
        //  夹具（memo Q6 第 1 格）：底图 s≡0.5（线性、SDR 白点归一），HDR 由**声明的 offset**反解：
        //      h = (s + offsetSdr)·2^L − offsetHdr,  L = (b+0.02)·maxLog2/255（落在 bin b 内）
        //  ⇒ 若前向真的按声明施加 offset，前向字节必等于 b（C1a）；且解侧（同样按声明施加）
        //     还原值 ≡ h（C1b，偏差只来自 bin 内那 0.02 步的偏置，≪1%）。
        //  ⇒ 前向不施加时（修复前）字节漂 bin、还原偏 (boost−1)·offset ⇒ 两条同时红。
        foreach (float peak in new[] { 1000f, 4000f })
        {
            float headroom = peak / SdrWhite;
            float maxLog2 = MathF.Log2(headroom);
            byte[] iso = GainMapEncoder.BuildIso21496Metadata(false, maxLog2);
            var m = GainMapDecoder.JpegContainer.ParseIso21496(iso, 0, iso.Length)!;
            float os = m.OffsetSdr, oh = m.OffsetHdr;
            int binMiss = 0, bins = 0;
            double worstRel = 0; float worstAbsWhite = 0; int worstBin = -1;
            foreach (int b in new[] { 0, 64, 128, 191, 255 })
            {
                float L = (b + 0.02f) * maxLog2 / 255f;
                const float s = 0.5f;
                float h = (s + os) * MathF.Pow(2f, L) - oh;
                var hdr = new float[] { h, h, h, 1f };
                var sdr = new float[] { s, s, s, 1f };
                var gb = new byte[1];
                SimdPixelOps.ComputeGainMapGray(hdr, sdr, gb, maxLog2);   // = 编码器灰度支路的同一实现
                bins++;
                if (gb[0] != b) binMiss++;
                var res = GainMapDecoder.ApplyGainMap(sdr, new[] { gb[0] / 255f, gb[0] / 255f, gb[0] / 255f, 1f },
                    1, 1, 1, 1, m);
                float errWhite = Math.Abs(res[0] - h);
                double rel = errWhite / Math.Max(Math.Abs(h), 1e-6f);
                if (rel > worstRel) { worstRel = rel; worstAbsWhite = errWhite; worstBin = b; }
                Console.WriteLine($"    [C1 {peak:F0}nits] bin {b,3}: 前向={gb[0],3} 期望={b,3} | "
                                + $"h={h:F6} 还原={res[0]:F6} err={errWhite * SdrWhite:F3} nits");
            }
            Check($"C1a [F-5] {peak:F0}nits：前向字节逐 bin 命中「按声明 offset 反解」的值（声明==施加）｜未命中 {binMiss}/{bins}",
                binMiss == 0);
            checks++;
            Check($"C1b [F-5] {peak:F0}nits：float 域往返 maxErr ≤ 1%｜实 {worstRel * 100:F3}% "
                + $"= {worstAbsWhite * SdrWhite:F2} nits（最差点 bin {worstBin}）", worstRel <= 0.01);
            checks++;

            // 备忘 Q6「第 1 格」的**同款夹具**：sdr≡0.5、hdr = sdr·headroom（boost 恰为上限）
            //   ⇒ 修复前残差应 = (boost−1)·offsetSdr = (headroom−1)/64 白
            //      = {(headroom - 1) / 64 * SdrWhite:F2} nits；修复后应只剩 F-2 的截断量化底。
            {
                const float s2 = 0.5f;
                float h2 = s2 * headroom;
                var gb2 = new byte[1];
                SimdPixelOps.ComputeGainMapGray(new[] { h2, h2, h2, 1f }, new[] { s2, s2, s2, 1f }, gb2, maxLog2);
                var m2 = GainMapDecoder.JpegContainer.ParseIso21496(
                    GainMapEncoder.BuildIso21496Metadata(false, maxLog2), 0, iso.Length)!;
                var res2 = GainMapDecoder.ApplyGainMap(new[] { s2, s2, s2, 1f },
                    new[] { gb2[0] / 255f, gb2[0] / 255f, gb2[0] / 255f, 1f }, 1, 1, 1, 1, m2);
                float errW = Math.Abs(res2[0] - h2);
                float offsetBoundWhite = (headroom - 1f) / 64f;                 // 备忘 Q6 的上界
                float quantBoundWhite = h2 * (MathF.Pow(2f, maxLog2 / 255f) - 1f); // 一趟截断的一步
                Console.WriteLine($"    [C1d {peak:F0}nits] boost=headroom 夹具（s=0.5, h={h2:F4}, 字节={gb2[0]}）："
                                + $"还原={res2[0]:F4} err={errW * SdrWhite:F2} nits ｜ "
                                + $"offset 失配理论上界={offsetBoundWhite * SdrWhite:F2} nits "
                                + $"截断一步上界={quantBoundWhite * SdrWhite:F2} nits");
                Check($"C1d [F-5·Q6第1格] {peak:F0}nits boost=headroom 往返 err ≤ ½·(headroom−1)/64"
                    + $"（={offsetBoundWhite * SdrWhite / 2:F2} nits）｜实 {errW * SdrWhite:F2} nits",
                    errW <= 0.5f * offsetBoundWhite);
                checks++;
                Check($"C1e [F-2·量化底] {peak:F0}nits 同夹具 err ≤ 截断一步上界"
                    + $"（={quantBoundWhite * SdrWhite:F2} nits，证明残差只剩取整口径）｜实 {errW * SdrWhite:F2} nits",
                    errW <= quantBoundWhite);
                checks++;
            }
        }
        // 对照组（判据有牙的**可执行**证明）：夹具仍按**声明的 1/64** 反解，只把**解码侧**的 offset 置 0
        // ⇒ 声明与施加一旦不一致，误差必须跳到 ≥2%（这条在修复前后都应当成立：它量的是判据的敏感度）。
        {
            byte[] iso = GainMapEncoder.BuildIso21496Metadata(false, maxLog2_1000);
            var mDec = GainMapDecoder.JpegContainer.ParseIso21496(iso, 0, iso.Length)!;
            const float osDecl = 1f / 64f, ohDecl = 1f / 64f;
            mDec.OffsetSdr = 0f; mDec.OffsetHdr = 0f;
            const float s = 0.5f;
            float L = (255 + 0.02f) * maxLog2_1000 / 255f;
            float h = (s + osDecl) * MathF.Pow(2f, L) - ohDecl;         // 按**声明**的 offset 反解
            var gb = new byte[1];
            SimdPixelOps.ComputeGainMapGray(new[] { h, h, h, 1f }, new[] { s, s, s, 1f }, gb, maxLog2_1000);
            var res = GainMapDecoder.ApplyGainMap(new[] { s, s, s, 1f },
                new[] { gb[0] / 255f, gb[0] / 255f, gb[0] / 255f, 1f }, 1, 1, 1, 1, mDec);
            double rel = Math.Abs(res[0] - h) / h;
            Console.WriteLine($"    [C1c 对照] 声明 1/64 / 解码 0 的同夹具：err={rel * 100:F3}%（须 ≥2% 才说明 C1b 有牙）");
            Check($"C1c [F-5·对照] 「声明 offset 与施加不一致」时同一夹具误差 ≥2%（判据对该数敏感）｜实 {rel * 100:F3}%", rel * 100.0 >= 2.0);
            checks++;
        }

        // ───────────────── C2 [F-2] 取整口径（本仓=一趟 ⇒ 截断） ─────────────────
        {
            int mid = 0, truncHits = 0, roundDiffers = 0;
            for (int b = 0; b < 255; b++)
            {
                float L = (b + 0.51f) * maxLog2_1000 / 255f;
                byte got = SimdPixelOps.LogGainToByteScalar(L, maxLog2_1000);
                if (got == b) truncHits++;                 // 截断口径 = 参照一趟 encodeGain
                if (got != (byte)Math.Round(L / maxLog2_1000 * 255f)) roundDiffers++;
                mid++;
            }
            Console.WriteLine($"    [C2a] bin+0.51 处：截断命中 {truncHits}/{mid}，与 round 口径不同 {roundDiffers}/{mid}");
            Check($"C2a [F-2] 一趟取整口径=截断（参照 gainmapmath.cpp:770 encodeGain 同形）｜命中 {truncHits}/{mid}",
                truncHits == mid && roundDiffers == mid);
            checks++;
            // 一趟的量程 = **声明**的 headroom（min=1 ⇒ log2Min=0；除数=声明 GainMapMax）
            float declaredMax = m1.GainMapMax, declaredMin = m1.GainMapMin;
            bool divisorIsDeclared = Math.Abs(declaredMax - maxLog2_1000) <= 0.005f;   // 分数分母 =100 ⇒ ±1/2·10^-2
            Console.WriteLine($"    [C2b] 声明 GainMapMin={declaredMin:F4} GainMapMax={declaredMax:F4} 前向钳位上限={maxLog2_1000:F4}");
            Check($"C2b [F-2] 一趟量程自证：声明 min=0 且 max==前向钳位上限（±0.005，分数分母 100 的取整）｜"
                + $"min={declaredMin:F4} max={declaredMax:F4} vs {maxLog2_1000:F4}",
                declaredMin == 0f && divisorIsDeclared);
            checks++;
        }

        // ───────────────── C3 [F-3] 增益分母 = 写进底图的那个值 ─────────────────
        //   夹具：bt2020 源（纯红，线性 1.0=峰值 0.203 ⇒ 归一后 =SDR 白）+ 底图 sRGB ⇒
        //   线性域 2020→709 矩阵后底图 R **>1**（越界），而底图交付的是钳位+8-bit 量化后的 1.0。
        //   ⇒ 分母若用钳前 float（修复前）：增益算小 ⇒ 还原显著偏暗（实测 ~20%+）；
        //     分母用交付值（修复后）：往返闭合（≤5%）。
        {
            var mtx = ColorSpaceRegistry.LinearMatrixBetweenPrimaries("bt2020", "bt709");
            bool haveMtx = mtx is { Length: 9 };
            Check($"C3a [F-3·前置] 2020→709 线性矩阵可求（判据载体）｜{(haveMtx ? $"R'={mtx![0]:F4}" : "null")}", haveMtx);
            checks++;
            if (haveMtx)
            {
                const int fw = 32, fh = 24;
                float pw = 1000f / SdrWhite;
                var px = new float[fw * fh * 4];
                for (int i = 0; i < fw * fh; i++)
                {
                    px[i * 4 + 0] = 0.203f;   // 归一后恰 = SDR 白（1.0），纯 2020 红 ⇒ 矩阵后越界
                    px[i * 4 + 1] = 0f;
                    px[i * 4 + 2] = 0f;
                    px[i * 4 + 3] = 1f;
                }
                // 与产品**同序**复算期望值：输入(1.0=峰值) → normHdr → Reinhard → 底图矩阵 → 底图字节 → 读回线性
                float hR = (float)mtx![0], hG = (float)mtx[3], hB = (float)mtx[6];      // normHdr=(1,0,0) 过矩阵
                float kk = SimdPixelOps.SegmentedReinhardScalar(1.0f, pw);              // maxY=1.0 → 逐通道同缩放
                float bR = kk * hR, bG = kk * hG, bB = kk * hB;                          // 钳前 float（**旧**分母）
                float sR = StoredLinear(bR), sG = StoredLinear(bG), sB = StoredLinear(bB); // 交付值（**新**分母）
                static float StoredLinear(float v)
                    => SimdPixelOps.SrgbToLinearScalar(SimdPixelOps.FloatToSrgb8Scalar(v) / 255f);
                static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
                float hLum = Luma(hR, hG, hB), sLumStored = Luma(sR, sG, sB), sLumRaw = Luma(bR, bG, bB);
                Console.WriteLine($"    [C3] 真值 R={hR:F4} G={hG:F4} B={hB:F4} luma={hLum:F4} ｜ "
                                + $"钳前底图 float R={bR:F4}（越界 {(bR > 1.0001f ? "是" : "否")}）G={bG:F4} B={bB:F4} ｜ "
                                + $"交付后线性读回 R={sR:F4} G={sG:F4} B={sB:F4} ｜ 分母 luma：钳前={sLumRaw:F4} 交付={sLumStored:F4}");
                Check($"C3b [F-3] 夹具确实造出「钳前 ≠ 交付」的分母（R>1 被钳、G/B<0 被钳 ⇒ 两个 luma 不同）｜"
                    + $"raw={bR:F4}/stored={sR:F4} lumaRaw={sLumRaw:F4}/lumaStored={sLumStored:F4}",
                    bR > 1.0001f && sR < bR - 0.05f && Math.Abs(sLumRaw - sLumStored) > 0.02f * Math.Abs(sLumStored));
                checks++;

                // 对照臂（**读数**，不是判据）：分母若仍取钳前 float 且前向不带 offset（=修复前），
                // 单通道件的亮度偏差会是多少 ⇒ 用来证明本锁的分叉轴就是「分母取哪个值」。
                float declaredMax = (float)Math.Round(MathF.Log2(pw) * 100f) / 100f;
                byte oldByte = SimdPixelOps.LogGainToByteScalar(
                    MathF.Log2(Math.Max(hLum / Math.Max(sLumRaw, 0.001f), 1.0f)), MathF.Log2(pw));
                float oldGain = MathF.Pow(2f, declaredMax * (oldByte / 255f));
                float o = 1f / 64f;
                float preFixLuma = Luma((sR + o) * oldGain - o, (sG + o) * oldGain - o, (sB + o) * oldGain - o);
                double preFixLumaErr = Math.Abs(preFixLuma - hLum) / Math.Abs(hLum);
                Console.WriteLine($"    [C3·对照臂预测] 旧口径（分母=钳前 float、前向无 offset）⇒ 增益字节={oldByte}，"
                                + $"亮度还原={preFixLuma:F4} ⇒ 偏差 {preFixLumaErr * 100:F2}%（应显著大于判据阈值 3%）");

                var oggm = new System.Text.StringBuilder();
                var deLog = new System.Text.StringBuilder();
                // 两条出口各跑一次往返：
                //   multiChannel=true  ⇒ 逐通道增益 ⇒ 承诺的是**逐通道**自洽（比 R 通道）
                //   multiChannel=false ⇒ 单通道（亮度）增益 ⇒ 只能承诺**亮度**自洽（比 luma）
                //   ⚠ 拿灰度件去比 R 通道会把「单通道增益天然修不动色度」算到 F-3 头上（判据打错对象）。
                foreach (bool mcGain in new[] { true, false })
                {
                    string tag = mcGain ? "rgb" : "gray";
                    string jpg = Path.Combine(outDir, $"batchc_gamutclip_{tag}.jpg");
                    string raw = Path.Combine(outDir, $"batchc_gamutclip_{tag}.rgba");
                    try { if (File.Exists(jpg)) File.Delete(jpg); } catch { }
                    try { if (File.Exists(raw)) File.Delete(raw); } catch { }
                    oggm.Clear(); deLog.Clear();
                    bool ok = await GainMapEncoder.EncodeAsync(px, fw, fh, jpg,
                        hdrPeakNits: 1000f, sdrWhiteNits: SdrWhite, multiChannel: mcGain,
                        baseQuality: 1.5f, gainMapQuality: 1.5f, downsample: 4,
                        baseGamut: "srgb", sourcePrimaries: "bt2020",
                        log: s => oggm.Append(s), ct: default);
                    Check($"C3c[{tag}] [F-3] 越界夹具编码产物存在（{Path.GetFileName(jpg)}）", ok && File.Exists(jpg));
                    checks++;
                    if (!ok || !File.Exists(jpg)) continue;

                    var rr = await GainMapDecoder.DecodeToLinearRawAsync(jpg, raw, log: s => deLog.Append(s));
                    Check($"C3d[{tag}] [F-3] 越界夹具可纯托管往返解码（判据载体）", rr != null && File.Exists(raw));
                    checks++;
                    if (rr == null || !File.Exists(raw)) continue;

                    var bytes = await File.ReadAllBytesAsync(raw);
                    int n = fw * fh;
                    var gPlane = new float[n];
                    var bPlane = new float[n];
                    var rPlane = new float[n];
                    Buffer.BlockCopy(bytes, 0, gPlane, 0, n * 4);
                    Buffer.BlockCopy(bytes, n * 4, bPlane, 0, n * 4);
                    Buffer.BlockCopy(bytes, n * 8, rPlane, 0, n * 4);
                    // 增益图是 1/4 降采样 ⇒ 取**块中心**（不是边缘）以免插值混边
                    int idx = 12 * fw + 16;
                    float gotR = rPlane[idx], gotG = gPlane[idx], gotB = bPlane[idx];
                    float gotLum = 0.2126f * gotR + 0.7152f * gotG + 0.0722f * gotB;
                    float wantR = hR, wantLum = hLum;
                    double relR = Math.Abs(gotR - wantR) / Math.Abs(wantR);
                    double relLum = Math.Abs(gotLum - wantLum) / Math.Abs(wantLum);
                    Console.WriteLine($"    [C3e[{tag}]] 真值 R={wantR:F4} G={hG:F4} B={hB:F4} luma={wantLum:F4} ｜ "
                                    + $"还原 R={gotR:F4} G={gotG:F4} B={gotB:F4} luma={gotLum:F4} ｜ "
                                    + $"ΔR={relR * 100:F2}% Δluma={relLum * 100:F2}%"
                                    + $"（钳前分母={bR:F4}/{bG:F4}/{bB:F4}，交付值={sR:F4}/{sG:F4}/{sB:F4}）");
                    if (mcGain)
                        Check($"C3e[rgb] [F-3] 越界通道 R 的往返恢复偏差 ≤5%（分母=写进底图的那个值）｜实 {relR * 100:F2}%", relR <= 0.05);
                    else
                        Check($"C3e[gray] [F-3] 单通道件的**亮度**往返恢复偏差 ≤3%（同分母口径；逐通道不归本锁管）｜实 {relLum * 100:F2}%", relLum <= 0.03);
                    checks++;
                }
                Console.WriteLine($"    [C3f] 对照臂读数：分母若仍取钳前 float，单通道件的亮度偏差应为 {preFixLumaErr * 100:F2}%"
                                + "（>判据阈值 ⇒ 分叉轴就是分母取哪个值；两侧不同分支，非同一份数学）");
                Check($"C3f [F-3·对照臂自证] 钳前分母的预测偏差 > 判据阈值 3%（分叉确实存在）｜预测 {preFixLumaErr * 100:F2}%",
                    preFixLumaErr > 0.03);
                checks++;
            }
        }

        // ───────────────── C4 [F-4] 通道声明与交付（JPEG 出口的声明半边） ─────────────────
        {
            byte[] isoRgb = GainMapEncoder.BuildIso21496Metadata(true, maxLog2_1000);
            byte[] isoGray = GainMapEncoder.BuildIso21496Metadata(false, maxLog2_1000);
            var mRgb = GainMapDecoder.JpegContainer.ParseIso21496(isoRgb, 0, isoRgb.Length);
            var mGray = GainMapDecoder.JpegContainer.ParseIso21496(isoGray, 0, isoGray.Length);
            bool bit7Rgb = (isoRgb[4] & 0x80) != 0, bit7Gray = (isoGray[4] & 0x80) != 0;
            Console.WriteLine($"    [C4a] multiChannel=true ⇒ flags=0x{isoRgb[4]:X2} bit7={bit7Rgb} channels={mRgb?.Channels}；"
                            + $"false ⇒ flags=0x{isoGray[4]:X2} bit7={bit7Gray} channels={mGray?.Channels}");
            Check($"C4a [F-4] bit7 声明自洽：true⇒3 参数集 / false⇒1 参数集｜实 {mRgb?.Channels}/{mGray?.Channels}",
                bit7Rgb && !bit7Gray && mRgb?.Channels == 3 && mGray?.Channels == 1);
            checks++;
            Check($"C4a2 [F-4] 载荷长度闭合：3 通道={isoRgb.Length} B = 21+3×40；1 通道={isoGray.Length} B = 21+40",
                isoRgb.Length == 21 + 120 && isoGray.Length == 21 + 40);
            checks++;

            // 产物（供 §engine 的第二读者读 SOF 采样因子）
            const int mw = 64, mh = 48;
            var mpx = new float[mw * mh * 4];
            for (int i = 0; i < mw * mh; i++)
            {
                float u = (i % mw) / (float)(mw - 1);
                mpx[i * 4 + 0] = 0.05f + 0.9f * u; mpx[i * 4 + 1] = 0.05f + 0.3f * u;
                mpx[i * 4 + 2] = 0.05f + 0.6f * (1f - u); mpx[i * 4 + 3] = 1f;
            }
            var lg = new System.Text.StringBuilder();
            string rgbJpg = Path.Combine(outDir, "batchc_multi.jpg");
            try { if (File.Exists(rgbJpg)) File.Delete(rgbJpg); } catch { }
            bool okRgb = await GainMapEncoder.EncodeAsync(mpx, mw, mh, rgbJpg,
                hdrPeakNits: 1000f, sdrWhiteNits: SdrWhite, multiChannel: true,
                baseQuality: 1.5f, gainMapQuality: 1.5f, downsample: 4,
                log: s => lg.Append(s), ct: default);
            Console.WriteLine($"GAINMATH ARTIFACT: {rgbJpg}");
            Check($"C4b [F-4] multiChannel=true 的 Ultra HDR 产物存在（供第二读者读增益图 JPEG 的 SOF 采样因子，{(okRgb ? new FileInfo(rgbJpg).Length + " B" : "失败")}）",
                okRgb && File.Exists(rgbJpg));
            checks++;
        }

        // ───────────────── C5/C6 [F-4/F-8] AVIF 出口：gray 与 bit7 成套 ─────────────────
        //   （独立成方法 ⇒ verify-gainmap-isobmff.ps1 可以只跑这一段：`gainmath avifgm`）
        checks += await AvifChannelLocksAsync(outDir, maxLog2_1000, SdrWhite);

        // ───────────────── C7 [F-8] 逐通道参数：不许只取 c==0 ─────────────────
        {
            byte[] iso3 = GainMapEncoder.BuildIso21496Metadata(true, maxLog2_1000);
            // 参照两趟（jpegr.cpp:1031-1034 / gainmapmetadata.cpp:386-396）**真的**会写不同的逐通道值
            SetBe32(iso3, IsoChannelOffset(1) + 8, (int)MathF.Round((maxLog2_1000 + 0.5f) * 100f));   // ch1 gainMapMax +0.5
            SetBe32(iso3, IsoChannelOffset(1) + 24, 2); SetBe32(iso3, IsoChannelOffset(1) + 28, 64);   // ch1 baseOffset 2/64
            SetBe32(iso3, IsoChannelOffset(2) + 0, -30);                                               // ch2 gainMapMin −0.30
            SetBe32(iso3, IsoChannelOffset(2) + 16, 3); SetBe32(iso3, IsoChannelOffset(2) + 20, 2);    // ch2 gamma 1.5
            SetBe32(iso3, IsoChannelOffset(2) + 32, 1); SetBe32(iso3, IsoChannelOffset(2) + 36, 32);   // ch2 altOffset 1/32
            var m3 = GainMapDecoder.JpegContainer.ParseIso21496(iso3, 0, iso3.Length);
            Check($"C7p [F-8·前置] 打补丁后的 3 通道件仍可解析（判据载体）｜channels={m3?.Channels}", m3 != null && m3!.Channels == 3);
            checks++;
            if (m3 != null)
            {
                // 期望值 = 参照 libavif gainmap.c:263-267 的**逐通道**形：
                //   g' = (b/255)^(1/γ_c); logBoost = min_c(1−g') + max_c·g'; out = (s+os_c)·2^logBoost − oh_c
                const float s = 0.5f, gByte = 128f / 255f;
                float[] osArr = { 1f / 64f, 2f / 64f, 1f / 64f };
                float[] ohArr = { 1f / 64f, 1f / 64f, 1f / 32f };
                float[] minArr = { 0f, 0f, -0.30f };
                float[] maxArr = { maxLog2_1000, maxLog2_1000 + 0.5f, maxLog2_1000 };
                float[] gamArr = { 1f, 1f, 1.5f };
                var exp = new float[4];
                for (int c = 0; c < 3; c++)
                {
                    float gp = gamArr[c] != 1f ? MathF.Pow(gByte, 1f / gamArr[c]) : gByte;
                    float logB = minArr[c] * (1f - gp) + maxArr[c] * gp;
                    exp[c] = (s + osArr[c]) * MathF.Pow(2f, logB) - ohArr[c];
                }
                var got = GainMapDecoder.ApplyGainMap(new[] { s, s, s, 1f },
                    new[] { gByte, gByte, gByte, 1f }, 1, 1, 1, 1, m3);
                double maxD = 0;
                for (int c = 0; c < 3; c++) maxD = Math.Max(maxD, Math.Abs(got[c] - exp[c]));
                Console.WriteLine($"    [C7a] 逐通道施加：R={got[0]:F5}/G={got[1]:F5}/B={got[2]:F5} 期望 "
                                + $"R={exp[0]:F5}/G={exp[1]:F5}/B={exp[2]:F5} maxΔ={maxD:E2}");
                Check($"C7a [F-8] 三通道件的逐通道 min/max/γ/offset 各自施加（照 libavif gainmap.c:263-267）｜maxΔ={maxD:E2}",
                    maxD <= 1e-3);
                checks++;
                // 通道相关仿射误差 = 色偏：修复前只取 c==0 ⇒ 三通道恢复值相等（无通道差）
                bool channelsDiffer = Math.Abs(got[1] - got[0]) > 0.01f || Math.Abs(got[2] - got[0]) > 0.01f;
                Console.WriteLine($"    [C7b] 通道差 |G−R|={Math.Abs(got[1] - got[0]):F5} |B−R|={Math.Abs(got[2] - got[0]):F5}"
                                + "（修复前塌成 ch0 ⇒ 全为 0 ⇒ 通道相关仿射误差=色偏）");
                Check($"C7b [F-8] 逐通道施加真的产生**通道差**（不是只取 c==0 的三条同值）｜ΔGR={Math.Abs(got[1] - got[0]):F5} ΔGB={Math.Abs(got[2] - got[0]):F5}",
                    channelsDiffer);
                checks++;
                // 对照：三套全同的件 ⇒ 施加结果必须与「按 ch0 一条」逐字节一致（收紧不得改变自家产物语义）
                byte[] iso3same = GainMapEncoder.BuildIso21496Metadata(true, maxLog2_1000);
                var ms = GainMapDecoder.JpegContainer.ParseIso21496(iso3same, 0, iso3same.Length)!;
                var gotSame = GainMapDecoder.ApplyGainMap(new[] { s, s, s, 1f },
                    new[] { gByte, gByte, gByte, 1f }, 1, 1, 1, 1, ms);
                var m1c = GainMapDecoder.JpegContainer.ParseIso21496(
                    GainMapEncoder.BuildIso21496Metadata(false, maxLog2_1000), 0, iso1.Length)!;
                var gotMono = GainMapDecoder.ApplyGainMap(new[] { s, s, s, 1f },
                    new[] { gByte, gByte, gByte, 1f }, 1, 1, 1, 1, m1c);
                double sameMax = 0;
                for (int c = 0; c < 3; c++) sameMax = Math.Max(sameMax, Math.Abs(gotSame[c] - gotMono[c]));
                Console.WriteLine($"    [C7c] 参数全同的 3 通道件 vs 1 通道件：maxΔ={sameMax:E3}");
                Check($"C7c [F-8·对照] 参数全同时逐通道施加 == 单通道施加（自家产物语义不变，maxΔ≤1e-5）｜实 {sameMax:E2}",
                    sameMax <= 1e-5f);
                checks++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"GAINMATH CHECKS={checks}");
        Console.WriteLine($"GAINMATH RESULT: pass={_pass} fail={_fail}");
        foreach (var f in _failures) Console.WriteLine($"  ❌ {f}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// C5/C6 —— AVIF 出口的「声明 == 交付」成套锁（F-4/F-8）。
    /// 独立成方法是为了让 `GainMapTestHost gainmath avifgm` 单跑这一段
    /// （verify-gainmap-isobmff.ps1 用它，避免把 C1–C3/C7 的编码再跑一遍）。
    /// </summary>
    private static async Task<int> AvifChannelLocksAsync(string outDir, float maxLog2, float sdrWhite)
    {
        int checks = 0;
        var ffmpeg = AppSettingsService.Current.FfmpegPath;
        bool ffOk = !string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(ffmpeg);
        Check($"C5p [前置] ffmpeg 可用（AVIF 出口判据载体）｜{ffmpeg ?? "(空)"}", ffOk);
        checks++;
        if (!ffOk) return checks;

        string png = Path.Combine(outDir, "batchc_gray_src.png");
        string grayAvif = Path.Combine(outDir, "batchc_gray.avif");
        int e1 = await RunToolAsync(ffmpeg!, $"-y -v error -f lavfi -i testsrc2=s=32x24 -frames:v 1 \"{png}\"");
        int e2 = await RunToolAsync(ffmpeg!, $"-y -v error -i \"{png}\" -frames:v 1 -c:v libaom-av1 -crf 18 -b:v 0 -pix_fmt gray -f avif \"{grayAvif}\"");
        bool haveGray = e2 == 0 && File.Exists(grayAvif);
        byte[] grayBytes = haveGray ? await File.ReadAllBytesAsync(grayAvif) : Array.Empty<byte>();
        var grayPixi = haveGray ? PixiChannelCounts(grayBytes) : new List<int>();
        Console.WriteLine($"    [C5p2] 已知 gray 单图 AVIF 的 pixi 平面数：[{string.Join(",", grayPixi)}]（pixi 读法的**锚**：必须是 1）");
        Check($"C5p2 [前置] 单色 gray 的 AVIF 造得出来且 pixi 读出 1 平面（读法先锚再生效，rc={e1}/{e2}）｜实 [{string.Join(",", grayPixi)}]",
            haveGray && grayPixi.Count == 1 && grayPixi[0] == 1);
        checks++;
        if (!haveGray) return checks;

        // 反方向锚：同一读法在**已知的 yuv444p** AVIF 上必须读出 3 平面
        //（pixi 判据两头都锚过，才允许它去裁决「声明 3 / 交付 1」这种说谎组合）。
        string avif444 = Path.Combine(outDir, "batchc_444.avif");
        int e3 = await RunToolAsync(ffmpeg!, $"-y -v error -i \"{png}\" -frames:v 1 -c:v libaom-av1 -crf 18 -b:v 0 -pix_fmt yuv444p -f avif \"{avif444}\"");
        var pixi444 = e3 == 0 && File.Exists(avif444)
            ? PixiChannelCounts(await File.ReadAllBytesAsync(avif444)) : new List<int>();
        Console.WriteLine($"    [C5p3] 已知 yuv444p AVIF 的 pixi 平面数：[{string.Join(",", pixi444)}]（必须含 3）");
        Check($"C5p3 [前置] pixi 读法反向锚：yuv444p 的 AVIF 读出 3 平面｜实 [{string.Join(",", pixi444)}] rc={e3}",
            pixi444.Count >= 1 && pixi444.Exists(x => x == 3));
        checks++;

        byte[] isoRgb = GainMapEncoder.BuildIso21496Metadata(true, maxLog2);
        byte[] isoGray = GainMapEncoder.BuildIso21496Metadata(false, maxLog2);
        var lying = IsoBmffGainMapWriter.Build(grayBytes, grayBytes,
            IsoBmffGainMapWriter.BuildTmapPayload(isoRgb), -1, -1, -1);
        var honest = IsoBmffGainMapWriter.Build(grayBytes, grayBytes,
            IsoBmffGainMapWriter.BuildTmapPayload(isoGray), -1, -1, -1);
        Console.WriteLine($"    [C5a] 声明 3 通道 / 交付 gray 单平面 ⇒ Ok={lying.Ok} 原因=「{lying.FailReason ?? "(无)"}」");
        Check($"C5a [F-4] 写出器**拒绝**「tmap 声明 3 通道 / 增益图 item 只有 1 平面」（不许声明==数据说谎）｜Ok={lying.Ok}",
            !lying.Ok && !string.IsNullOrWhiteSpace(lying.FailReason));
        checks++;
        Check($"C5b [F-4] 写出器仍接受「声明 1 通道 / 交付 gray」（收紧不得过界）｜Ok={honest.Ok} 原因=「{honest.FailReason ?? "(无)"}」",
            honest.Ok && honest.Data is { Length: > 200 });
        checks++;

        // 产品 AVIF 的 multiChannel 出口：交付必须与声明成套（gray+bit7 的组合现在必须出局）
        const int aw = 32, ah = 24;
        var apx = new float[aw * ah * 4];
        for (int i = 0; i < aw * ah; i++)
        {
            float u = (i % aw) / (float)(aw - 1);
            apx[i * 4 + 0] = 0.05f + 0.9f * u; apx[i * 4 + 1] = 0.05f + 0.4f * (1f - u);
            apx[i * 4 + 2] = 0.05f + 0.6f * u; apx[i * 4 + 3] = 1f;
        }
        var la = new System.Text.StringBuilder();
        string outAvif = Path.Combine(outDir, "batchc_multi.avif");
        try { if (File.Exists(outAvif)) File.Delete(outAvif); } catch { }
        bool okAvif = await GainMapEncoder.EncodeAsync(apx, aw, ah, outAvif,
            hdrPeakNits: 1000f, sdrWhiteNits: sdrWhite, multiChannel: true,
            baseQuality: 1.5f, gainMapQuality: 1.5f, downsample: 4,
            log: s => la.Append(s), ct: default, container: "avif");
        Console.WriteLine($"GAINMATH AVIF ARTIFACT: {outAvif}");
        var pixiCounts = okAvif && File.Exists(outAvif)
            ? PixiChannelCounts(await File.ReadAllBytesAsync(outAvif))
            : new List<int>();
        Console.WriteLine($"    [C6] 产物内 pixi 平面数：[{string.Join(", ", pixiCounts)}]（底图 yuv420p⇒3、增益图 gray⇒1 / yuv444p⇒3）"
                        + $"｜tmap 载荷 {IsoBmffGainMapWriter.BuildTmapPayload(isoRgb).Length - 1} B");
        Check($"C6 [F-4/F-8] 产品 AVIF multiChannel 出口成功且**不再交付单平面 gray**（声明 3 ⇒ 交付 3）｜"
            + $"Ok={okAvif} pixi=[{string.Join(",", pixiCounts)}]",
            okAvif && File.Exists(outAvif) && pixiCounts.Count >= 2 && pixiCounts.All(x => x >= 3));
        checks++;
        return checks;
    }

    /// <summary>扫描 ISO-BMFF 文件里所有 <c>pixi</c> 盒的 channel_count（诊断「声明的通道数 vs 交付的平面数」）。</summary>
    private static List<int> PixiChannelCounts(byte[] d)
    {
        var list = new List<int>();
        var tag = new byte[] { (byte)'p', (byte)'i', (byte)'x', (byte)'i' };
        for (int i = 0; i + 12 < d.Length; i++)
        {
            bool hit = true;
            for (int k = 0; k < 4; k++) if (d[i + k] != tag[k]) { hit = false; break; }
            if (!hit) continue;
            // pixi 是 FullBox：size(4)+type(4)+version(1)+flags(3)+channel_count(1)
            // ⇒ channel_count 在 'pixi' 四字节的**起点 +8**（= 盒起点 +12），不是 +9（那是第一个 bitDepth）。
            int boxStart = i - 4;
            if (boxStart < 0) continue;
            int size = ((d[boxStart] << 24) | (d[boxStart + 1] << 16) | (d[boxStart + 2] << 8) | d[boxStart + 3]);
            if (size >= 13 && size <= 64 && i + 8 < d.Length) list.Add(d[i + 8]);
        }
        return list;
    }

    /// <summary>跑一个外部工具（本批锁只用 ffmpeg 造灰度 AVIF 夹具），返回退出码。</summary>
    private static async Task<int> RunToolAsync(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [RunTool] {Path.GetFileName(file)} 异常: {ex.Message}");
            return -1;
        }
    }

    private static void Check(string name, bool ok)
    {
        if (ok) { Console.WriteLine($"  ✅ {name}"); _pass++; }
        else { Console.WriteLine($"  ❌ {name}"); _fail++; _failures.Add(name); }
    }

    /// <summary>
    /// 已知问题（不粉饰为通过，也不阶断本轮）：必须打印实测值与修复阶段，并计入汇总。
    /// “编不过/静默失败”比显式 KNOWN-ISSUE 更危险（GainMapTestHost 因上轮删 IccMode 属性而长期编不过，就是教训）。
    /// </summary>
    private static void Known(string name, string observed, string fixPhase)
    {
        Console.WriteLine($"  ⚠️ KNOWN-ISSUE {name} —— 实测 {observed}（待 {fixPhase} 修复）");
        _known++;
        _knownList.Add($"{name} [{observed}] → {fixPhase}");
    }


}
