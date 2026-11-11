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
        private static void ProbePq()
        {
            // ST 2084 公开锚点：编码值 → 亮度(nits)
            (float code, double nits)[] anchors = new[]
            {
                (0.5f, 92.7), (0.5799f, 203.0), (0.7516f, 1000.0), (1.0f, 10000.0),
            };
            foreach (var (code, nits) in anchors)
            {
                double got = SimdPixelOps.PqEotfScalar(code) * SimdPixelOps.PqPeakNits;
                Check(Math.Abs(got - nits) / nits < 0.035, $"EOTF({code}) = {got:F1} nits (期望 {nits})");
            }
            // 互逆：OETF(EOTF(E)) ≈ E，且 EOTF(OETF(L)) ≈ L
            foreach (float l in new[] { 0.0002f, 0.0031f, 0.0203f, 0.1f, 0.5f, 1.0f })
            {
                float e = SimdPixelOps.PqOetfScalar(l);
                float back = SimdPixelOps.PqEotfScalar(e);
                Check(Math.Abs(back - l) < 1e-4f + l * 1e-3f, $"往返 L={l} → E={e:F5} → L'={back:F6}");
            }
            // 黑位基准区：E < 0.18 应给接近 0（不能反向变亮）
            Check(SimdPixelOps.PqEotfScalar(0.0f) == 0f, "EOTF(0) = 0");
            Check(SimdPixelOps.PqEotfScalar(0.1f) < SimdPixelOps.PqEotfScalar(0.5f), "EOTF 单调递增");
            Check(SimdPixelOps.PqEotfScalar(0.5f) > 0.001f, "EOTF(0.5) 非零（旧符号错误会返回 0）");
        }

    }
}
