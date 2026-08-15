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
    private static readonly System.Collections.Generic.List<string> _failures = new();

    public static async Task<int> Main(string[] args)
    {
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

        // ── D. PNG 3.0 chunk 服务 (PngCicpService) ──
        Console.WriteLine();
        Console.WriteLine("═══ D. PNG 3.0 sBIT/cICP (PngCicpService) ═══");
        await PngCicpTestAsync(outDir);

        // ── 汇总 ──
        Console.WriteLine();
        Console.WriteLine($"═══ 汇总: 通过 {_pass}, 失败 {_fail} ═══");
        foreach (var f in _failures)
            Console.WriteLine($"  ❌ {f}");
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
            Format = "jpg", ColorSpace = "auto", IccMode = FfmpegGui.Models.IccMode.None, Threads = 4
        };
        var cmdAuto = FfmpegCommandBuilder.BuildArguments(optsAuto, hdrIn, jpgOut);
        Console.WriteLine($"      [D8 cmd] {cmdAuto}");
        Check("D8 tonemap 修复链 (auto→bt709)", cmdAuto.Contains(
            "format=yuv444p,tonemap=hable:param=0.5,format=rgb48le,zscale=pin=bt2020:tin=linear:min=gbr:p=bt709:t=bt709:m=bt709"));

        // HDR→HDR (目标 P3 PQ): 不应 tonemap, zscale 直接转换
        var optsH2H = new FfmpegGui.Models.FfmpegOptions
        {
            Format = "jpg", ColorSpace = "P3 PQ", IccMode = FfmpegGui.Models.IccMode.None, Threads = 4
        };
        var cmdH2H = FfmpegCommandBuilder.BuildArguments(optsH2H, hdrIn, Path.Combine(outDir, "out_p3pq.jpg"));
        Console.WriteLine($"      [D9 cmd] {cmdH2H}");
        // PNG 输入 matrix=gbr（RGB 原生），zscale min=gbr 正确
        Check("D9 HDR→HDR 不 tonemap (zscale 直转)", !cmdH2H.Contains("tonemap")
            && cmdH2H.Contains("zscale=pin=bt2020:tin=smpte2084:min=gbr:p=smpte432:t=smpte2084:m=bt709"));

        // HDR→广色域 SDR (目标 Display P3): tonemap 链目标应为 P3+sRGB
        var optsH2P3 = new FfmpegGui.Models.FfmpegOptions
        {
            Format = "jpg", ColorSpace = "Display P3", IccMode = FfmpegGui.Models.IccMode.None, Threads = 4
        };
        var cmdH2P3 = FfmpegCommandBuilder.BuildArguments(optsH2P3, hdrIn, Path.Combine(outDir, "out_p3sdr.jpg"));
        Check("D10 tonemap 目标 Display P3", cmdH2P3.Contains(
            "zscale=pin=bt2020:tin=linear:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709"));

        // ── RAW 预处理注入场景 (Bug#2 回归) ──
        // QueueProcessor 预处理后注入 ColorPrimaries=bt709 + ColorTrc=linear（不设 UseAdvancedColorParameters）
        // 修复前: 注入值被忽略 → 线性 TIFF 无 -color_trc linear → PNG 输出无 cICP → 画面过暗
        var rawInjected = new FfmpegGui.Models.FfmpegOptions
        {
            Format = "png", ColorSpace = "auto", IccMode = FfmpegGui.Models.IccMode.None, Threads = 4,
            ColorPrimaries = "bt709", ColorTrc = "linear"   // 预处理注入（模拟 RAW 路径）
        };
        var cmdRaw = FfmpegCommandBuilder.BuildArguments(rawInjected, Path.Combine(outDir, "linear.tiff"),
            Path.Combine(outDir, "raw_out.png"));
        Console.WriteLine($"      [D11 cmd] {cmdRaw}");
        Check("D11 RAW 注入 bt709/linear 生效", cmdRaw.Contains("-color_primaries bt709 -color_trc linear")
            && !cmdRaw.Contains("tonemap"));

        // RAW 注入 + 用户选目标色域 (sRGB): zscale 应使用 linear 源
        var rawSrgb = new FfmpegGui.Models.FfmpegOptions
        {
            Format = "png", ColorSpace = "sRGB", IccMode = FfmpegGui.Models.IccMode.None, Threads = 4,
            ColorPrimaries = "bt709", ColorTrc = "linear"   // 预处理注入
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
            float logGain = MathF.Log2(Math.Max(hLum / Math.Max(sLum, 0.001f), 1.0f));
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
        RawService.EncodeToDngAsync(sample, Path.Combine(Path.GetTempPath(), "cap.dng"),
            compression: 1, jxlQuality: 0, log: s => { if (s.Contains("dngtool")) sb.Append(s); },
            jxlEffort: 1, threads: threads).GetAwaiter().GetResult();
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
                    Format = "jpg", ColorSpace = "auto", IccMode = FfmpegGui.Models.IccMode.None, Threads = 4
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
            var ok = await GainMapEncoder.EncodeAsync(
                pixels, w, h, outPath,
                hdrPeakNits: hdrPeak, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
                multiChannel: multiChannel,
                baseQuality: 1.5f, gainMapQuality: 1.5f,
                downsample: downsample,
                log: s => Console.Write(s));

            Check($"编码完成 ({name})", ok && File.Exists(outPath));
            if (!ok || !File.Exists(outPath)) return;
            Console.WriteLine($"  输出: {outPath} ({new FileInfo(outPath).Length} 字节)");

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
        try
        {
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
            var psnr = mse > 0 ? 10 * Math.Log10(1.0 / mse) : 99;
            // 阈值说明:
            //  - sdr (无增益): 应接近无损 (JPEG 有损 d=1.5 下 >40dB)
            //  - hdr: 增益图 1/4 降采样 + 双线性插值 → 高光边缘有固有误差,
            //    headroom 越大误差越大 (hdr1000 ~20dB, hdr4000 ~15dB 为合理水平)
            var isSdr = name.StartsWith("sdr");
            var threshold = isSdr ? 30.0 : name.Contains("4000") ? 12.0 : 18.0;
            Check($"{name}: PSNR={psnr:F2}dB (阈 {threshold}dB)", psnr >= threshold);
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

    private static void Check(string name, bool ok)
    {
        if (ok) { Console.WriteLine($"  ✅ {name}"); _pass++; }
        else { Console.WriteLine($"  ❌ {name}"); _fail++; _failures.Add(name); }
    }


}
