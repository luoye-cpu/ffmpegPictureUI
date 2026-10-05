using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tests.Shared;
using FfmpegGui.Services;

namespace Tests.GainMapStruct
{
    /// <summary>
    /// 子系统测试组：**gainmap-struct** —— GainMap 产物的**输出结构验证**（exiftool）
    /// 与 PNG 3.0 <c>sBIT</c>/<c>cICP</c> chunk。
    ///
    /// <para><b>来源</b>：2026-10-05 从 <c>tests/GainMapTestHost/Program.cs</c> 物理拆出，
    /// 方法与断言**逐字复制**（门禁 <c>verify-gainmap-host.ps1</c> 正在 grep 这些文本）。</para>
    ///
    /// <para><b>覆盖的 mode</b>：<c>gainmap-struct</c>（见 <c>TestGroups</c>）。</para>
    ///
    /// <para><b>⚠ 编码→结构 依赖的处理方式 = 方案 (a)：消费方自造夹具</b></para>
    /// <para>旧宿主里 C 段读的是 A 段写出的 <c>outDir\hdr1000.jpg</c>。直接照搬会让本组依赖
    /// 另一个进程先跑过，而已有的失败形态是 <c>Check("结构验证: 输入缺失", false)</c> —— 它会把
    /// "没人跑过编码"这一<b>环境问题</b>记成结构缺陷。⇒ 本组用<b>自带夹具</b>：
    /// <c>EnsureStructFixtureAsync</c> 调<b>同一个产品编码器</b>、参数与 A 段 <c>hdr1000</c> 格
    /// <b>逐字相同</b>，现造一份到本组自己的目录；造不出 ⇒ 原样的「输入缺失」判红命中
    /// ⇒ <b>fail-closed，绝不 vacuous-pass</b>。</para>
    /// </summary>
    internal static class GainMapStructGroup
    {
        /// <summary>本组独占的产物目录（旧宿主是共用的 <c>tests/output/gainmap</c>）。</summary>
        internal static string OutputDir
            => Path.Combine(Environment.CurrentDirectory, "tests", "output", "gainmap-struct");

        private static void Check(string name, bool ok)
        {
            if (ok) Console.WriteLine($"  ✅ {name}");
            else Console.WriteLine($"  ❌ {name}");
            Harness.Check(ok, name);
        }

        /// <summary>
        /// 已知问题（不粉饰为通过，也不阶断本轮）：必须打印实测值与修复阶段，并计入汇总。
        /// “编不过/静默失败”比显式 KNOWN-ISSUE 更危险（GainMapTestHost 因上轮删 IccMode 属性而长期编不过，就是教训）。
        /// </summary>
        private static void Known(string name, string observed, string fixPhase)
        {
            Console.WriteLine($"  ⚠️ KNOWN-ISSUE {name} —— 实测 {observed}（待 {fixPhase} 修复）");
            Harness.KnownOnly($"{name} [{observed}] → {fixPhase}");
        }

        internal static int KnownCount => Harness.KnownCount;
        internal static System.Collections.Generic.IReadOnlyList<string> KnownList => Harness.KnownList;

        internal static void RunStruct() => ProbeStruct().GetAwaiter().GetResult();

        private static async Task ProbeStruct()
        {
            var outDir = OutputDir;
            Directory.CreateDirectory(outDir);

            // ── C. 结构验证 (exiftool) ──
            Console.WriteLine("═══ C. 输出结构验证 ═══");
            var hdr1000 = Path.Combine(outDir, "hdr1000.jpg");
            await EnsureStructFixtureAsync(outDir, hdr1000);
            await StructureCheckAsync(hdr1000);

            // ── D. PNG 3.0 chunk 服务 (PngCicpService) ──
            Console.WriteLine();
            Console.WriteLine("═══ D. PNG 3.0 sBIT/cICP (PngCicpService) ═══");
            await PngCicpTestAsync(outDir);
        }

        /// <summary>
        /// 自带夹具（方案 (a)）：按 A 段 <c>hdr1000</c> 格的**逐字相同参数**现造 <c>hdr1000.jpg</c>。
        /// <para>返回值不参与判据：造不出 ⇒ <c>StructureCheckAsync</c> 里原有的「输入缺失」判红命中。</para>
        /// </summary>
        private static async Task EnsureStructFixtureAsync(string outDir, string outPath)
        {
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
            {
                Console.WriteLine($"\n── 夹具(gainmap-struct): 复用已有 hdr1000.jpg（{new FileInfo(outPath).Length} 字节）──");
                return;
            }
            Console.WriteLine("\n── 夹具(gainmap-struct): 现造 hdr1000.jpg（peak=1000, multiChannel=False, ds=4）──");
            try
            {
                const int w = 256, h = 192;
                var pixels = CreateHdrTestPixels(w, h, 1000f);
                var gmLog = new System.Text.StringBuilder();
                var ok = await GainMapEncoder.EncodeAsync(
                    pixels, w, h, outPath,
                    hdrPeakNits: 1000f, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
                    multiChannel: false,
                    baseQuality: 1.5f, gainMapQuality: 1.5f,
                    downsample: 4,
                    log: s => { gmLog.Append(s); Console.Write(s); });
                Console.WriteLine($"  夹具结果: ok={ok}, 存在={File.Exists(outPath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  夹具异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  以下为 tests/GainMapTestHost/Program.cs 的原文
        // ═══════════════════════════════════════════════════════════════════

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
            Check("D8 tonemap 链 (auto→sRGB 标定 + iccgen)", cmdAuto.Contains(
                "format=yuv444p,tonemap=hable:param=0.5,format=rgb48le,zscale=pin=bt2020:tin=linear:min=gbr:p=bt709:t=iec61966-2-1:m=bt709")
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
            bool d10P3 = cmdH2P3.Contains("zscale=pin=bt2020:tin=linear:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709")
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

            // ⚠ 旧宿主此处继续尾调 `UiDeadlockTestAsync` → D16..D22 → PsnrCalculatorTestAsync
            //   → SimdPixelOpsTestAsync → PsnrBitDepthDomainTestAsync（D15–D38）。
            //   拆分为三个组工程后，那些已分别归 **gainmap-decode**（D15–D32）与
            //   **gainmap-encode**（D33–D38）并各自被**显式点名调用**；本组不再向下链式调用，
            //   否则同一批断言会在两个组里各跑一次 ⇒ 读数分叉。
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
        //  PNG chunk 探针 / 夹具（只被 C/D 段用 ⇒ 随段落一起搬来）
        // ═══════════════════════════════════════════════

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

        /// <summary>
        /// 生成 HDR 线性测试图 (输入约定: 1.0 = 峰值亮度, zscale npl 语义)。
        /// <para>⚠ 与旧宿主 A 段**逐字相同**：结构组的夹具必须与被检产物同源，
        /// 否则"结构验证过了"证明的不是产品。</para>
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
    }
}
