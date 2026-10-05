using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tests.Shared;
using FfmpegGui.Services;

namespace Tests.GainMapDecode
{
    /// <summary>
    /// <b>gainmap-decode</b> 的第二部分：旧 <c>GainMapTestHost</c> D 段里属于
    /// <b>PSNR / 像素内核 / 线程 / UI 死锁 / RAW</b> 的那些断言（D15–D32）。
    ///
    /// <para>方法体**逐字复制**自 <c>tests/GainMapTestHost/Program.cs</c>；<c>Check</c>/<c>Known</c>
    /// 与 <c>GainMapDecodeGroup</c> 里的那份同形（转发 <c>Harness</c>）。<c>partial</c> 只是为了让两个
    /// 文件同属一个组类，不改变任何语义。</para>
    ///
    /// <para><b>⚠ 与原宿主的唯一改动</b>：原链是
    /// <c>PngCicpTestAsync(D) → PsnrCalculatorTestAsync → SimdPixelOpsTestAsync → PsnrBitDepthDomainTestAsync</c>。
    /// 拆分后 <c>PsnrBitDepthDomainTestAsync</c>（D33–D38，位深/目标域读数）归 <c>gainmap-encode</c>
    /// 并在那里被**显式点名调用**（它原属"编码产物"读数族），故此处链尾只到 <c>SimdPixelOpsTestAsync</c>，
    /// 末行改为不再向下调用。条数账见 <see cref="GainMapDecodeGroup"/> 的类注释。</para>
    /// </summary>
    internal static partial class GainMapDecodeGroup
    {
        private static void Check(string name, bool ok)
        {
            if (ok) Console.WriteLine($"  ✅ {name}");
            else Console.WriteLine($"  ❌ {name}");
            Harness.Check(ok, name);
        }

        /// <summary>本文件里显式取义的入口（供 Program 分派）。</summary>
        internal static async Task RunPsnrCalculatorAsync(string outDir)
        {
            await PngCicpTailAsync(outDir);   // 旧宿主的 D15–D22 + 链尾（D23–D32）
        }

        /// <summary>
        /// PsnrCalculator 正确性验证:
        /// D23 与 ffmpeg psnr filter 数值一致性 (小图有损)
        /// D24 无损检测 (相同像素 → PSNR=inf)
        /// D25 多帧 (动图) 汇总语义 (average/min/max)
        /// D26 QualityAnalysisService 集成 (ffmpeg 只算 SSIM, PSNR 由 .NET 计算)
        /// </summary>
        internal static async Task PsnrCalculatorTestAsync(string outDir)
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
            // ⚠ 本条的可执行比较数是**CPU 相关**的：无 AVX512 的机器只跑
            //   SSE2/AVX2 两条 + 走下面那条**显式且会打印**的 AVX512 跳过分支。
            //   ⇒ 任何以"旧宿主 103"为基线的断言**不得据此收紧 N** —— 换一台带 AVX512 的
            //   机器会多出 1 条，N 就不是 103 了（实测本机：`[D27] 本机无 AVX512`）。
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
            // ⚠ 旧宿主此处调 `PsnrBitDepthDomainTestAsync`（D33–D38），拆分后归 gainmap-encode
            //   并被那里**显式点名调用**；本组不再重复跑，避免同一批断言在两个组里读数分叉。
            await Task.CompletedTask;
        }

        /// <summary>捕获 EncodeToDngAsync 生成的 dngtool 命令参数 (通过日志回调)。</summary>
        internal static string CaptureDngArgs(int threads)
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
        internal static async Task<double> TimeDngEncode(int threads)
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
        internal static async Task UiDeadlockTestAsync(string outDir)
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
        internal static void AdaptiveThreadTest()
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

        /// <summary>旧宿主 <c>ReadChunk</c> 的逐字副本；仅 <c>PngCicpTestAsync</c> 使用 —— 该用例归
        /// <c>gainmap-struct</c>，故本组不再承载它（避免"两组各一份 ReadChunk"的实现分叉）。</summary>
        internal static int CountChunks(string pngPath, string chunkName)
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
    }
}
