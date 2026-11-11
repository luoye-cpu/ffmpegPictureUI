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
        /// ootf —— BT.2100-2 Table 5 HLG OOTF 门禁（常数逐字取自建议书正文）。
        /// 重点验：γ 公式、α=L_W 的峰值约定、代数反解的往返恒等、不钳位（Note 5h）、
        /// 以及一个**外部可解释锚点**：HLG 0.75 码值应落在 SDR 参考白附近。
        /// </summary>
        private static void ProbeOotf()
        {
            double G(double lw) => FfmpegGui.Services.ColorMapping.Bt2100Ootf.SystemGamma(lw);
            Check(Math.Abs(G(1000) - 1.2) < 1e-12, $"γ(1000nit) = 1.2（建议书明示）");
            Check(Math.Abs(G(4000) - 1.4527) < 1e-3, $"γ(4000nit) = 1.2+0.42·log10(4) = {G(4000):F4}（3 位有效 1.45）");
            Check(Math.Abs(G(300) - 0.9804) < 1e-3, $"γ(300nit) = {G(300):F4}（≈0.98）");
            Check(Math.Abs(G(10000) - 1.62) < 1e-12, $"γ(10000nit) = {G(10000):F4}（=1.62）");
            double sum = FfmpegGui.Services.ColorMapping.Bt2100Ootf.YsR
                       + FfmpegGui.Services.ColorMapping.Bt2100Ootf.YsG
                       + FfmpegGui.Services.ColorMapping.Bt2100Ootf.YsB;
            Check(Math.Abs(sum - 1.0) < 1e-12, $"Y_S 系数和 = 1（{sum}）→ 消色差时 Y_S≡E，不引入额外增益");

            double r = 1, g = 1, b = 1;
            FfmpegGui.Services.ColorMapping.Bt2100Ootf.Apply(ref r, ref g, ref b, 1000);
            Check(Math.Abs(r - 1000) < 1e-9 && Math.Abs(g - 1000) < 1e-9,
                $"α=L_W：消色差名义峰值 (1,1,1) → {r:F1} nits（应等于 L_W=1000）");

            bool ach = true;
            for (int i = 1; i <= 20; i++)
            {
                double e = i / 10.0;   // 含超白 1.1..2.0
                double rr = e, gg = e, bb = e;
                FfmpegGui.Services.ColorMapping.Bt2100Ootf.Apply(ref rr, ref gg, ref bb, 1000);
                double want = FfmpegGui.Services.ColorMapping.Bt2100Ootf.ApplyAchromatic(e, 1000);
                if (Math.Abs(rr - want) > 1e-9 * Math.Max(1, want)) ach = false;
            }
            Check(ach, "一般式与消色差特例一致：F = α·E^γ（含超白 E>1）");

            bool mono = true;
            for (int i = 1; i < 512; i++)
                if (FfmpegGui.Services.ColorMapping.Bt2100Ootf.ApplyAchromatic(i / 511.0 * 2, 1000)
                    < FfmpegGui.Services.ColorMapping.Bt2100Ootf.ApplyAchromatic((i - 1) / 511.0 * 2, 1000) - 1e-12)
                    mono = false;
            Check(mono, "消色差 OOTF 在 [0,2] 上单调不降");

            // 往返恒等（含超白与负值：Note 5h 要求不得钳位，反解必须能原路返回）
            double worst = 0; string worstAt = "";
            foreach (var t in new[] { (0.3, 0.2, 0.1), (1.5, 1.5, 1.5), (2.0, 0.5, 0.05), (0.9, -0.2, 0.05) })
            {
                double x = t.Item1, y = t.Item2, z = t.Item3;
                var key = $"({x},{y},{z})";
                FfmpegGui.Services.ColorMapping.Bt2100Ootf.Apply(ref x, ref y, ref z, 4000);
                FfmpegGui.Services.ColorMapping.Bt2100Ootf.ApplyInverse(ref x, ref y, ref z, 4000);
                double e = Math.Max(Math.Abs(x - t.Item1), Math.Max(Math.Abs(y - t.Item2), Math.Abs(z - t.Item3)));
                if (e > worst) { worst = e; worstAt = key; }
            }
            Check(worst < 1e-12, $"OOTF ∘ OOTF⁻¹ 恒等（最大误差 {worst:E2} @ {worstAt}，含超白/负值）");

            double sw = 1.5, sg = 1.5, sb = 1.5;
            FfmpegGui.Services.ColorMapping.Bt2100Ootf.Apply(ref sw, ref sg, ref sb, 1000);
            Check(sw > 1000, $"Note 5h：超白不被钳位（1.5 → {sw:F1} nits > L_W）");

            // 外部锚点：HLG 系统级 EOTF 的 0.75 码值经 OOTF(1000nit) 后应落在 SDR 参考白附近
            double e75 = TC.Hlg.ToLinear(0.75);
            double rr75 = e75, gg75 = e75, bb75 = e75;
            FfmpegGui.Services.ColorMapping.Bt2100Ootf.Apply(ref rr75, ref gg75, ref bb75, 1000);
            Console.WriteLine($"    锚点：HLG 0.75 → 场景线性 {e75:F5} → 显示 {rr75:F1} nits（SDR 参考白 {TC.SdrWhiteNits:F0}）");
            Check(Math.Abs(rr75 - TC.SdrWhiteNits) / TC.SdrWhiteNits < 0.05,
                $"HLG 0.75 码值≈SDR 参考白（{rr75:F1} vs {TC.SdrWhiteNits:F0} nits，偏差 {100 * Math.Abs(rr75 - TC.SdrWhiteNits) / TC.SdrWhiteNits:F1}%）");
        }

    }
}
