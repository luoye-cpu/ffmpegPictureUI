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
        /// hlgwire —— **接线级**门禁：HLG 是否在**真实执行路径**上应用了 BT.2100 OOTF。
        ///
        /// 为什么需要它（2026-09-16 接手核查）：`ootf` 只测 <see cref="Bt2100Ootf"/> **类本身**（数学），
        /// `engine` 只测 P3→sRGB（SDR）—— 两者都覆盖不到「HLG 接线在真实内核路径上生效」。
        /// 实测：HLG 字段只加在 float RGBA 路径（`TransformRgba/8`），而该路径在 `src/` 中**零调用点**；
        /// 生产路径 `TransformRgb48le*` 与 `MeasurePeakLinearRgb48` **都没有 HLG 分支**。
        ///
        /// 判据不依赖任何实现细节：HLG 0.75 码值是 BT.2100 的漫反射白约定，
        /// 场景线性 0.26496 经 OOTF(1000nit) 应得 ≈202 nits ⇒ 归一化到「1.0 = 源峰值」即 ≈0.202。
        /// **某条路径若给出 0.265（场景光原值），就说明它漏了 OOTF。**
        /// </summary>
        private static void ProbeHlgWire()
        {
            const double Code = 0.75;
            const double Peak = 1000.0;   // HLG 名义峰值（与 ColorIntentFactory.FixHdrNits 同源）
            var hlg = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor
                .FromCicp("bt2020", "arib-std-b67", "BT.2100 HLG", nitsOfLinearOne: Peak)!;
            var srgb = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor
                .FromCicp("bt709", "iec61966-2-1", "sRGB")!;

            double scene = hlg.Curve.ToLinear(Code);
            double nits = FfmpegGui.Services.ColorMapping.Bt2100Ootf.ApplyAchromatic(scene, Peak);
            double expectNorm = nits / Peak;   // 归一到**源峰值**域（1.0 = 1000nits），仅作对照打印
            // ⚠️ 生产口径（与 `ColorTransformPlan.ToSpec()` 同源）：内核中间域是「1.0 = SDR 参考白」，
            //    源 HLG 的 1.0 = 1000nits 必须经 LinearScale = 1000/203 换过去 ⇒ 结果落在 1.0=203nits 域。
            //    旧探针**不设** LinearScale ⇒ 实际测的是「把 1000nits 归一直接当成 sRGB 白」的
            //    **非生产路径**（与 xcheck 同一类缺陷，见 2026-09-28 修复）。
            //    本用例 Code=0.75 ⇒ nits≈203 ⇒ expectSdr≈1.002（几乎正好是 SDR 白，便于观察）。
            double sdr = FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits;
            double expectSdr = nits / sdr;
            Console.WriteLine($"    HLG {Code} → 场景线性 {scene:F5} → OOTF 显示 {nits:F1} nits "
                + $"→ 归一(源峰值) {expectNorm:F5} → SDR 白域 {expectSdr:F5}");

            // 单像素 rgb48**le**（内核按 little-endian 解释字节对）
            ushort v = (ushort)Math.Round(Code * 65535.0);
            var buf = new byte[6];
            for (int k = 0; k < 3; k++) { buf[k * 2] = (byte)(v & 0xFF); buf[k * 2 + 1] = (byte)(v >> 8); }

            var spec = new FfmpegGui.Services.ColorMapping.TransformSpec
            {
                SrcCurve = hlg.Curve,
                DstCurve = srgb.Curve,
                Matrix = null,
                ClampLinearBeforeEncode = false,   // 不钳位，才能看到真实线性值
                SrcIsHlgScene = true,
                SrcHlgSystemGamma = FfmpegGui.Services.ColorMapping.Bt2100Ootf.SystemGamma(Peak),
                LinearScale = Peak / sdr,   // 与生产 ToSpec 同口径：HLG 1.0=1000nits → 1.0=203nits
            };

            // ① 峰值测量路径（tonemap 的 headroom 依据）——漏 OOTF 会让 headroom 算错。
            //    MeasurePeak 的口径是「已乘 LinearScale」⇒ 结果落在 1.0 = SDR 参考白域。
            double peak = FfmpegGui.Services.ColorMapping.ColorKernels.MeasurePeakLinearRgb48(buf, 1, spec, null);
            Check(Math.Abs(peak - expectSdr) / expectSdr < 0.05,
                $"① 峰值测量路径应用了 OOTF + 亮度域换算（期望 {expectSdr:F4}，实得 {peak:F4}）");

            // ② 主转换路径：HLG → sRGB，输出应落在**显示光**且经 SDR 白域换算。
            //    ⚠ Code=0.75 时 nits≈203 ⇒ expectSdr≈1.002 略超 1，48-bit 出口会钳到 65535
            //      （误差 ~0.2%，远小于 5% 容差）⇒ 不影响本判据的判别力。
            var dst = new byte[6];
            FfmpegGui.Services.ColorMapping.ColorKernels.TransformRgb48le(buf, dst, spec, 1, null, null);
            int o0 = dst[0] | (dst[1] << 8);
            double outLin = srgb.Curve.ToLinear(o0 / 65535.0);
            Check(Math.Abs(outLin - expectSdr) / expectSdr < 0.05,
                $"② 主转换路径应用了 OOTF + 亮度域换算（期望线性 {expectSdr:F4}，实得 {outLin:F4}）");

            // ③ float RGBA 路径：与 ①② 同一判据，防止两条实现各自漂移（本项目反复出现过"三处不一致"）
            var f = new float[4] { (float)Code, (float)Code, (float)Code, 1f };
            FfmpegGui.Services.ColorMapping.ColorKernels.TransformRgba(f, spec);
            double fLin = srgb.Curve.ToLinear(f[0]);
            Check(Math.Abs(fLin - expectSdr) / expectSdr < 0.05,
                $"③ float RGBA 路径应用了 OOTF + 亮度域换算（期望线性 {expectSdr:F4}，实得 {fLin:F4}）");
        }

    }
}
