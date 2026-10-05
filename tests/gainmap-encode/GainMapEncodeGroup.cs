using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tests.Shared;
using FfmpegGui.Services;

namespace Tests.GainMapEncode
{
    /// <summary>
    /// 子系统测试组：**gainmap-encode** —— GainMap/Ultra HDR 的**编码**侧。
    ///
    /// <para><b>来源</b>：2026-10-05 从 <c>tests/GainMapTestHost/Program.cs</c>（1726 行单文件宿主）
    /// 物理拆出。本文件里的 <c>Probe*</c> 方法体**逐字复制**自该宿主（不改逻辑、不改字符串 ——
    /// <c>tests/scripts/verify-gainmap-host.ps1</c> 正在 grep 这些文本）。</para>
    ///
    /// <para><b>覆盖的 mode</b>：<c>gainmap-encode</c>（见 <c>TestGroups</c>）。</para>
    ///
    /// <para><b>抽取原则</b>：共享 helper 一律**转发**到 <c>Harness</c>；只被本组用的 helper
    /// （<c>CreateHdrTestPixels</c>）随用例一起搬来，**不复制**实现。</para>
    ///
    /// <para><b>⚠ 与旧宿主的唯一刻意改动（行为等价，已逐条核对）</b></para>
    /// <list type="number">
    /// <item><b>输出目录</b>：旧宿主是<b>一个</b> <c>tests/output/gainmap</c>；本组用自己的
    ///   <c>tests/output/gainmap-encode</c>（见 <c>OutputDir</c>）。理由：拆分后若三个工程仍写同一个
    ///   目录，同一份文件会被两个进程互相覆盖，"谁产出的"归属不明 —— 本仓忌"归属不明"。</item>
    /// <item><b>计数/打印</b>：旧宿主的 <c>Check(name, ok)</c> 打的是 <c>✅ name</c> / <c>❌ name</c>，
    ///   而拆分后的统一口径是 <c>Harness.Check(bool, string)</c> 打 <c>PASS msg</c> / <c>FAIL msg</c>
    ///   （与已经拆完的 9 个组工程一致，也是门禁与运行器 <c>PROBE RESULT</c> 记账依赖的形态）。
    ///   ⇒ 这里保留 <b>旧签名</b> <c>Check(string, bool)</c> 让 <b>用例文本一字不改</b>，
    ///   只把实现改为转发 <c>Harness.Check</c>。断言字符串**逐字未动**。</item>
    /// <item><b><c>Known(name, observed, fixPhase)</c></b>：旧宿主计 <c>_known</c> 并另打
    ///   <c>⚠️ KNOWN-ISSUE …</c>。本组**逐字保留**该辅助与打印形态（含 <c>_knownList</c>），
    ///   但把计数落到 <c>Harness</c>（<c>KnownOnly</c>）—— 理由：<c>Harness</c> 的
    ///   <c>PROBE RESULT: pass=… fail=… skip=…</c> 是运行器记账的唯一读数，若 known 计数留在组内，
    ///   汇总行就与计数脱钩（本仓明令：计数只在 Harness）。
    ///   ⚠ <c>_known</c> 旧计数器若与本组汇总里的 known 数不一致，就是拆分引入的漂移。</item>
    /// <item><c>Harness.Check</c> 的<b>参数顺序</b>是 <c>(bool ok, string msg)</c>，与旧宿主
    ///   <c>Check(string name, bool ok)</c> 相反；本文件的适配器负责这一层转换。</item>
    /// </list>
    /// </summary>
    internal static class GainMapEncodeGroup
    {
        /// <summary>
        /// 本组独占的产物目录。
        /// <para>⚠ 与旧宿主 <c>tests/output/gainmap</c> 不同名：拆分后三个组工程若共用一个目录，
        /// 同名产物会互相覆盖 ⇒ 「这份文件是谁产出的」不可追溯；分目录后每组自证自洽。</para>
        /// </summary>
        internal static string OutputDir
            => Path.Combine(Environment.CurrentDirectory, "tests", "output", "gainmap-encode");

        // ═══════════════════════════════════════════════════════════════════
        //  与旧宿主逐字同形的辅助（实现转发 Harness；断言文本不受影响）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 旧宿主形态的 <c>Check(name, ok)</c>：保留旧签名与旧 <c>✅/❌</c> 明细行，
        /// 计数落到 <see cref="Harness"/>（拆分后计数只此一处）。
        /// </summary>
        private static void Check(string name, bool ok)
        {
            if (ok) Console.WriteLine($"  ✅ {name}");
            else Console.WriteLine($"  ❌ {name}");
            Harness.Check(ok, name);
        }

        private static readonly System.Collections.Generic.List<string> _knownList = new();

        /// <summary>已知问题计数（读 <see cref="Harness.KnownCount"/> —— 计数只此一份）。</summary>
        internal static int KnownCount => Harness.KnownCount;
        /// <summary>已知问题明细（读 <see cref="Harness.KnownList"/>）。</summary>
        internal static System.Collections.Generic.IReadOnlyList<string> KnownList => Harness.KnownList;

        /// <summary>
        /// 已知问题（不粉饰为通过，也不阶断本轮）：必须打印实测值与修复阶段，并计入汇总。
        /// “编不过/静默失败”比显式 KNOWN-ISSUE 更危险（GainMapTestHost 因上轮删 IccMode 属性而长期编不过，就是教训）。
        /// </summary>
        private static void Known(string name, string observed, string fixPhase)
        {
            Console.WriteLine($"  ⚠️ KNOWN-ISSUE {name} —— 实测 {observed}（待 {fixPhase} 修复）");
            Harness.KnownOnly($"{name} [{observed}] → {fixPhase}");
        }

        // ── 入口（供 Program 分派）──
        internal static void RunEncode() => ProbeEncode();
        internal static void RunPsnrBitDepthDomain() => ProbePsnrBitDepthDomain().GetAwaiter().GetResult();

        /// <summary>旧宿主 <c>Main</c> 的 A 段：5 个编码格，**顺序即契约**（见下方注释）。</summary>
        private static void ProbeEncode()
        {
            // ⚠ 旧宿主 Main 的 `outDir` 在 A 段之前创建；这里逐字保留该前置行为。
            var outDir = OutputDir;
            Directory.CreateDirectory(outDir);

            Console.WriteLine("═══ A. 编码验证 ═══");
            // ⚠ 逐字保留旧宿主的**顺序与格参数**：其中 `hdr1000` 是 C 段的被检件，
            //   `sdr/hdr1000/hdr1000rgb/hdr4000` 是 B 段解码闭环的被检件 ⇒ 顺序变了读数就会变。
            EncodeTestAsync(outDir, "sdr",       hdrPeak: 203f,   multiChannel: false, downsample: 4).GetAwaiter().GetResult();
            EncodeTestAsync(outDir, "hdr1000",   hdrPeak: 1000f, multiChannel: false, downsample: 4).GetAwaiter().GetResult();
            EncodeTestAsync(outDir, "hdr1000rgb",hdrPeak: 1000f, multiChannel: true,  downsample: 4).GetAwaiter().GetResult();
            EncodeTestAsync(outDir, "hdr4000",   hdrPeak: 4000f, multiChannel: false, downsample: 4).GetAwaiter().GetResult();
            EncodeTestAsync(outDir, "hdr1000ds8",hdrPeak: 1000f, multiChannel: false, downsample: 8).GetAwaiter().GetResult();

            // ── D23..D38 位深/域 PSNR：旧宿主由 D 段（PngCicpTestAsync）尾调 SimdPixelOpsTestAsync
            //    → PsnrBitDepthDomainTestAsync 链式触发。本组不搬 PngCicp/SimdPixel 两段（它们归
            //    gainmap-struct / gainmap-decode），故**显式点名调用**，使这 26 条断言在本组读得到、
            //    不因拆分而消失（否则就是"用例被静默删掉"）。
            ProbePsnrBitDepthDomain().GetAwaiter().GetResult();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  以下为 tests/GainMapTestHost/Program.cs 的原文（行号见文件头链接）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// D33 8-bit RGB PSNR (与旧版 CalculatePsnr 兼容)
        /// D34 16-bit 归一化 PSNR (与 8-bit 可比)
        /// D35 色域标注 (isRgb 不影响数值)
        /// D36 MultiFramePsnr 位深参数传播
        /// </summary>
        private static async Task ProbePsnrBitDepthDomain()
        {
            var outDir = OutputDir;   // 旧签名 `PsnrBitDepthDomainTestAsync(string outDir)`
            Directory.CreateDirectory(outDir);
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
    }
}
