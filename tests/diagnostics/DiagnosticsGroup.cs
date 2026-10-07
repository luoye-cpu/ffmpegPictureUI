using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tests.Shared;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;
using CS = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor;
using CE = FfmpegGui.Services.ColorMapping.ColorMappingEngine;
using CI = FfmpegGui.Services.ColorMapping.ColorIntent;
using CA = FfmpegGui.Services.ColorMapping.ColorAction;
using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
using CL = FfmpegGui.Services.ColorMapping.ColorLoss;
using ST = FfmpegGui.Models.ColorStrategy;
using DC = FfmpegGui.Services.ColorMapping.DescriptorConfidence;
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;

namespace Tests.Diagnostics
{
    /// <summary>
    /// 子系统测试组：**diagnostics**（性能 / 自检 / 工具探测 / i18n）。
    /// <para>覆盖 mode：bench / peak / peakmeasure / psnr / xcheck / selftest / tooldetect / i18n。</para>
    /// <para>方法体自 `tests/ServiceProbe/Program.cs` **逐字抽出**（不改逻辑、不改字符串文本），
    /// 以保证与旧宿主**读数逐条一致**。</para>
    /// </summary>
    // After the two-step mode split (_gen-mode-split.ps1 + _gen-mode-split-reduce.ps1),
    // this file keeps ONLY the shared base: helpers used by >=2 modes plus the Run*
    // forwarding entry points. Each Probe* body (and its exclusive helpers) was copied
    // VERBATIM into Modes/<mode>.cs; those files are the other half of the same class
    // (partial class), so every call site is unchanged.
    // Assertion count is unchanged: acceptance = per-mode readings identical.
    internal static partial class DiagnosticsGroup
    {
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);

        private static void Skip(string msg) => Harness.Skip(msg);

        private static string RunCapture(string exe, string args, string? redirectOut = null) => Harness.RunCapture(exe, args, redirectOut);

        private static int RunCap(string exe, string args) => Harness.RunCap(exe, args);

        private static int RunTask(System.Threading.Tasks.Task<int> t) => Harness.RunTask(t);

        private static void RunCli(string exe, string args)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, Arguments = args, RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            p?.WaitForExit();
        }

internal static void RunBench(string[] a) => ProbeBench(a);

        internal static void RunPeak(string[] a) => ProbePeak(a);

        internal static void RunPeakMeasure(string[] a) => ProbePeakMeasure(a).GetAwaiter().GetResult();

        internal static void RunPsnr(string[] a) => ProbePsnr(a).GetAwaiter().GetResult();

        internal static void RunXcheck(string[] a) => ProbeXcheck(a).GetAwaiter().GetResult();

        internal static void RunSelfTest() => ProbeSelfTest();

        internal static void RunToolDetect(string[] a) => ProbeToolDetect(a);

        internal static void RunI18n() => ProbeI18n();

        private static FfmpegGui.Services.ColorMapping.TransformSpec Spec(
            string sp, string st, string dp, string dt, bool matrix = true, bool clamp = true)
        {
            var src = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp(sp, st, sp) ?? throw new Exception($"src {sp}/{st}");
            var dst = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp(dp, dt, dp) ?? throw new Exception($"dst {dp}/{dt}");

            // ⚠️ 亮度域换算必须与生产 `ColorTransformPlan.ToSpec()` **同口径**，否则本探针测的根本
            //    不是真实引擎路径：PQ 的 1.0 = 10000nits、HLG = 1000nits、其余 = 203nits（SDR 参考白）。
            //    旧实现两项都留默认 1 ⇒ PQ→sRGB 不做 49× 换算，与 zimg 参照差出 PSNR 7.3dB，
            //    而那笔账长期被当成「zimg 参照口径分歧（PQ 归一约定不同）」豁免 —— 实为**探针自身的
            //    构造缺陷**（它绕过 ToSpec 手搓 spec），不是引擎的口径分歧。
            double Nits(FfmpegGui.Services.ColorMapping.TransferCurve c)
                => c.Kind switch
                {
                    FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Pq
                        => FfmpegGui.Services.ColorMapping.TransferCurve.PqPeakNits,
                    FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Hlg
                        => FfmpegGui.Services.ColorMapping.Bt2100Ootf.NominalPeakNits,
                    _ => FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits,
                };
            double sdr = FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits;

            // ⚠️ 白名单同步：因本缺陷而被判为「口径分歧」的格子，修复后应从 $knownNonDefects 移除，
            //    否则豁免会把真正的回归也一起免掉（门禁就失去牙齿）。见 verify-color-engine-xcheck.ps1。
            return new FfmpegGui.Services.ColorMapping.TransformSpec
            {
                SrcCurve = src.Curve,
                DstCurve = dst.Curve,
                Matrix = matrix ? src.MatrixTo(dst) : null,
                ClampLinearBeforeEncode = clamp,
                LinearScale = Nits(src.Curve) / sdr,
                EncodeScale = sdr / Nits(dst.Curve),
            };
        }

        private static byte[] SyntheticFrame16(int w, int h)
        {
            var b = new byte[w * h * 6];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 6;
                    ushort r, g, bl;
                    int k = x + y;
                    if (y < h / 4) { r = g = bl = (ushort)((k % 4096) * 16); }                     // 灰阶斜坡
                    else if (y < h / 2) { int t = (k % 512) * 128; r = (ushort)Math.Min(t, 65535); g = bl = 0; }  // 红梯
                    else if (y < 3 * h / 4)                                                  // 饱和原色块
                    {
                        int c = (x / Math.Max(1, w / 6)) % 6;
                        r = (ushort)((c is 0 or 3 or 4) ? 65535 : 0);
                        g = (ushort)((c is 1 or 3 or 5) ? 65535 : 0);
                        bl = (ushort)((c is 2 or 4 or 5) ? 65535 : 0);
                    }
                    else { r = (ushort)(x * 65535 / Math.Max(1, w - 1)); g = (ushort)(y * 65535 / Math.Max(1, h - 1)); bl = 32768; }
                    b[o] = (byte)r; b[o + 1] = (byte)(r >> 8);
                    b[o + 2] = (byte)g; b[o + 3] = (byte)(g >> 8);
                    b[o + 4] = (byte)bl; b[o + 5] = (byte)(bl >> 8);
                }
            return b;
        }
    }
}
