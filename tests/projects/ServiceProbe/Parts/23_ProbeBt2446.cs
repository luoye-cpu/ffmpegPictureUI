using System;
using System.IO;
using System.Text;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;   // 色彩映射引擎的曲线表（P1）
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;     // 执行内核（P3）
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;     // PSNR/ΔE 度量
using CS = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor;
using CE = FfmpegGui.Services.ColorMapping.ColorMappingEngine;
using CI = FfmpegGui.Services.ColorMapping.ColorIntent;
using CA = FfmpegGui.Services.ColorMapping.ColorAction;
using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
using CL = FfmpegGui.Services.ColorMapping.ColorLoss;
using ST = FfmpegGui.Models.ColorStrategy;
using DC = FfmpegGui.Services.ColorMapping.DescriptorConfidence;
using OR = FfmpegGui.Services.ColorMapping.OverrideReason;
using AC = FfmpegGui.Services.ColorMapping.AnnotChoice;
using CSC = FfmpegGui.Services.ColorMapping.CarryScope;
using MI = FfmpegGui.Services.ColorMapping.ManualIntent;
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;   // S5′ 裁决探针
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;         // `--raw-color-target auto` 判据内核（2026-10-04）
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;          // S5′ 裁决探针
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;     // S5′ 裁决探针
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using GMJ = FfmpegGui.Services.ColorMapping.GainMapJpegCodec;   // GainMap 解码口径/编码参数单一真值（gmgamut）
using AAS = FfmpegGui.Services.AppSettingsService;

namespace ServiceProbe
{
    internal static partial class Program
    {
        /// <summary>
        /// bt2446 —— ITU-R Report BT.2446-1 §4 Method A 门禁（常数逐字取自建议书 Table 2/3/4）。
        /// 拿不到外部参照（ffmpeg 无此算子），所以用“标准自洽”作铁门：
        ///   • 分段 knee 在两个边界必须连续 ⇒ 能反推二次段常数是否读错；
        ///   • §4.2 的 255 端点必须精确落到 Lmax=1000 nits ⇒ 能反推指数多项式是否读错。
        /// ⚠ 不拿“往返恒等”当门禁：§4.1 的 Y' 是 1/12.4 域、§4.2 的 Y' 是 BT.2020 SDR 域，不同编码。
        /// </summary>
        private static void ProbeBt2446()
        {
            double Rho(double l) => FfmpegGui.Services.ColorMapping.Bt2446MethodA.Rho(l);
            double Knee(double y) => FfmpegGui.Services.ColorMapping.Bt2446MethodA.Step2Knee(y);
            double TM(double y) => FfmpegGui.Services.ColorMapping.Bt2446MethodA.ToneMapLuma(y);
            double Inv(double y) => FfmpegGui.Services.ColorMapping.Bt2446MethodA.InverseLumaNits(y);

            Console.WriteLine($"    ρ_HDR(1000)={Rho(1000):F4}  ρ_SDR(100)={Rho(100):F4}  E(255)={FfmpegGui.Services.ColorMapping.Bt2446MethodA.Exponent(255):F6}");
            double s = 0.2627 + 0.6780 + 0.0593;
            Check(Math.Abs(s - 1.0) < 1e-12, $"亮度系数和 = 1（{s}）");
            Check(Math.Abs(TM(0.0)) < 1e-12, $"端点：Y'=0 → 0（{TM(0.0):E2}）");
            Check(Math.Abs(TM(1.0) - 1.0) < 1e-9, $"端点：Y'=1 → 1（{TM(1.0):F8}）");

            // 铁门 1：三段 knee 在边界必须连续（常数读错会直接断裂）
            double c1 = Math.Abs(Knee(0.7399) - Knee(0.7399 + 1e-9));
            double c2 = Math.Abs(Knee(0.9909) - Knee(0.9909 + 1e-9));
            Check(c1 < 2e-3, $"knee 第一段/二次段在 0.7399 连续（跳变 {c1:E2}）");
            Check(c2 < 2e-3, $"knee 二次段/第三段在 0.9909 连续（跳变 {c2:E2}）");
            Check(Math.Abs(Knee(1.0) - 1.0) < 1e-12, $"knee 上端点 = 1（{Knee(1.0):F6}）");

            bool mono = true, inr = true; int bad = -1;
            for (int i = 1; i < 1024; i++)
            {
                double a = TM((i - 1) / 1023.0), b = TM(i / 1023.0);
                if (b < a - 1e-12) { mono = false; bad = i; break; }
                if (b < -1e-12 || b > 1 + 1e-12) { inr = false; break; }
            }
            Check(mono, $"§4.1 亮度曲线单调不降（首个下降 idx={bad}）");
            Check(inr, "§4.1 输出落在 [0,1]");

            // 铁门 2：§4.2 的 255 端点必须等于 Lmax
            double peak = Inv(1.0);
            Check(Math.Abs(peak - FfmpegGui.Services.ColorMapping.Bt2446MethodA.LmaxNits) < 0.5,
                $"§4.2 锚点：Y'=1 → {peak:F2} nits，应等于 Lmax=1000（验证指数多项式常数读法）");
            bool mono2 = true;
            for (int i = 1; i < 256; i++) if (Inv(i / 255.0) < Inv((i - 1) / 255.0) - 1e-12) mono2 = false;
            Check(mono2 && Math.Abs(Inv(0.0)) < 1e-12, "§4.2 反变换单调且 f(0)=0");
            Console.WriteLine($"    §4.2 取样：Y'=0.5 → {Inv(0.5):F1} nits；0.75 → {Inv(0.75):F1} nits；1.0 → {peak:F1} nits");

            // 中性灰不得引入色度偏移（Table 3 结构件）
            FfmpegGui.Services.ColorMapping.Bt2446MethodA.ColourCorrect(0.9, 0.9, 0.9, TM(FfmpegGui.Services.ColorMapping.Bt2446MethodA.LumaPrime(0.9, 0.9, 0.9)),
                out double cbg, out double crg, out double ytm);
            Check(Math.Abs(cbg) < 1e-12 && Math.Abs(crg) < 1e-12, $"中性灰：Cb'=Cr'=0（{cbg:E1}/{crg:E1}）");
            // 超白（Y'=1）不得被钳到 <1
            Check(TM(1.0) >= 1.0 - 1e-9, "峰值不被压到 1 以下（保留全部高光层次到端点）");

            // ── 像素级接入（ApplyRgb 的 Bt2446A 分支）──
            var pa = new FfmpegGui.Services.ColorMapping.ToneMapParams
            { Mode = FfmpegGui.Services.ColorMapping.ToneMapMode.Bt2446A, PeakNits = 1000, SdrWhiteNits = 203, SdrPeakNits = 100 };
            Check(pa.Active, $"bt2446a 参数已激活（headroom={pa.Headroom:F3}）");

            // ① 中性灰必须保持中性（不得引入伪色度）
            float x0 = 0.5f * (float)(1000.0 / 203.0), xr = x0, xg = x0, xb = x0;
            FfmpegGui.Services.ColorMapping.ToneMap.ApplyRgb(ref xr, ref xg, ref xb, pa, TC.Srgb);
            Check(Math.Abs(xr - xg) < 1e-6f && Math.Abs(xg - xb) < 1e-6f,
                $"中性灰输入→中性灰输出（{xr:F6}/{xg:F6}/{xb:F6}）");

            // ② 与独立单元实现逐点一致：输出码值应等于 ToneMapLuma(Gamma124(v_m))
            bool agree = true; double worstA = 0; int wIdx = -1;
            for (int i = 1; i <= 32; i++)
            {
                double vm = i / 32.0;                            // “1.0 = L_HDR” 域的线性值
                double vo = vm * 1000.0 / 203.0;                 // → 引擎线性域（1.0 = SDR 白）
                float rr = (float)vo, gg = (float)vo, bb = (float)vo;
                FfmpegGui.Services.ColorMapping.ToneMap.ApplyRgb(ref rr, ref gg, ref bb, pa, TC.Srgb);
                double code = TC.Srgb.FromLinear(rr);            // 重新编码回码值
                double want = TM(FfmpegGui.Services.ColorMapping.Bt2446MethodA.Gamma124(vm));
                double d = Math.Abs(code - want);
                if (d > worstA) { worstA = d; wIdx = i; }
                if (d > 2e-4) agree = false;
            }
            Check(agree, $"像素路径与单元实现一致（最大偏差 {worstA:E2} @i={wIdx}）");

            // ③ 峰值：v_m=1 → 码值 1.0（L_HDR 映到目标满量程）
            float pr = (float)(1000.0 / 203.0), pg = pr, pb = pr;
            FfmpegGui.Services.ColorMapping.ToneMap.ApplyRgb(ref pr, ref pg, ref pb, pa, TC.Srgb);
            double pcode = TC.Srgb.FromLinear(pr);
            Check(Math.Abs(pcode - 1.0) < 1e-4, $"L_HDR 峰值→目标满量程（码值 {pcode:F6}）");

            // ④ 单调（消色差，按码值）
            bool monoA = true; float prev = -1f;
            for (int i = 0; i <= 64; i++)
            {
                double vo = i / 64.0 * 1000.0 / 203.0;
                float rr = (float)vo, gg = (float)vo, bb = (float)vo;
                FfmpegGui.Services.ColorMapping.ToneMap.ApplyRgb(ref rr, ref gg, ref bb, pa, TC.Srgb);
                float c = (float)TC.Srgb.FromLinear(rr);
                if (c < prev - 1e-5f) { monoA = false; break; }
                prev = c;
            }
            Check(monoA, "像素路径消色差单调不降");

            // ⑤ 契约：请求 bt2446a 时确实挂载并归为 ToneMapped（不再被当作未实现拒绝）
            var pqS = CS.FromCicp("bt2020", "smpte2084", "BT.2100 PQ", nitsOfLinearOne: TC.PqPeakNits)!;
            var srgbS = CS.FromCicp("bt709", "iec61966-2-1", "sRGB")!;
            var pA = CE.Plan(new CI { Strategy = ST.Manual, Source = pqS, Target = srgbS, Format = CE.CapsFor("png"),
                                       TargetExplicit = true, HdrPeakNits = 1000,
                                       ToneMapRequested = true, ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Bt2446A });
            Check(pA.Tone is { Active: true } && pA.Tone.Mode == FfmpegGui.Services.ColorMapping.ToneMapMode.Bt2446A
                  && pA.ColorLoss == CL.ToneMapped,
                $"契约挂载 bt2446a（Tone={pA.Tone} loss={pA.ColorLoss}）");
        }

    }
}
