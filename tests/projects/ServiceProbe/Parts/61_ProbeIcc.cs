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
        // ────────────────────────────── icc ──────────────────────────────

        private static void ProbeIcc(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe icc <色彩空间名>"); _fail++; return; }
            var spec = args[1];
            var s = ColorSpaceRegistry.Resolve(spec);
            Console.WriteLine($"spec={s?.Key ?? "(未识别)"} kind={s?.Kind.ToString() ?? "-"} " +
                $"out={s?.OutPrimaries}/{s?.OutTrc}/{s?.OutMatrix}");
            var icc = IccProfileService.ResolveNamedIcc(spec, null,
                s?.OutPrimaries ?? "bt709", s?.OutTrc ?? "iec61966-2-1", s?.OutMatrix ?? "bt709");
            Console.WriteLine($"icc={icc ?? "(null)"}");
            if (icc == null) { Check(false, "命名 ICC 解析成功"); return; }
            var b = File.ReadAllBytes(icc);
            var info = IccProfileService.ParseInfo(icc);
            Console.WriteLine($"iccSize={b.Length} version={info?.Version} class={info?.DeviceClass} cs={info?.ColorSpace} pcs={info?.Pcs} desc={info?.Description}");
            Check(IccProfileService.IsValidIccProfile(icc), "ICC 魔数有效");
            Check(!string.IsNullOrWhiteSpace(info?.Description), "desc 标签存在");
            foreach (var tag in new[] { "desc", "wtpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "chad" })
            {
                var (off, len) = IccProfileService.FindIccTag(b, tag);
                Console.WriteLine($"tag {tag}: offset={off} size={len}");
            }
            var (wo, wl) = IccProfileService.FindIccTag(b, "wtpt");
            Check(wo > 0 && wl == 20, "wtpt 为 20 字节 XYZ");
            if (wo > 0 && wl >= 20)
            {
                // XYZ = s15Fixed16 ×3 (大端, 值 = raw/65536)
                double X = ReadS15(b, (int)wo + 8), Y = ReadS15(b, (int)wo + 12), Z = ReadS15(b, (int)wo + 16);
                Console.WriteLine($"wtpt X={X:0.0000} Y={Y:0.0000} Z={Z:0.0000}");
                Check(Math.Abs(Y - 1.0) < 0.01, "wtpt Y ≈ 1.0 (PCS 归一)");
            }
        }

        private static double ReadS15(byte[] b, int o)
            => ((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]) / 65536.0;

    }
}
