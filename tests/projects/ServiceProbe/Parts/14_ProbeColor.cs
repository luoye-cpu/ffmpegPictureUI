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
        /// <summary>color &lt;文件&gt; [格式] [exiftool.exe] —— 源色彩探测结果 + 保真判定。</summary>
        private static void ProbeColor(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe color <file> [fmt] [exiftool]"); _fail++; return; }
            // ICC/EXIF 语义探测依赖 exiftool：探针进程无 PLAN 自动检测，需显式传入路径
            if (args.Length >= 4) FfmpegGui.Services.AppSettingsService.Current.ExifToolPath = args[3];
            Console.WriteLine($"exiftool available={ExifToolService.IsAvailable} path={ExifToolService.DetectedPath ?? "(null)"}");
            var (ip, id) = IccProfileService.ExtractIccToTempFile(args[1]);
            Console.WriteLine($"extractedIcc={(ip == null ? "(null)" : Path.GetFileName(ip))} desc={id ?? "(null)"} " +
                $"guessed={IccProfileService.GuessColorSpace(id) ?? "(null)"}");
            if (ip != null) IccProfileService.TryDeleteIcc(ip);
            var m = FfmpegGui.Services.FfmpegCommandBuilder.ProbeInputColorMetadata(args[1]);
            Console.WriteLine($"gamut={m.sourceGamutName ?? "(null)"} primaries={m.colorPrimaries ?? "-"} trc={m.colorTrc ?? "-"} " +
                $"matrix={m.colorSpace ?? "-"} hasIccSemantics={m.hasIccSemantics} isRgb={m.isRgb} bitDepth={m.bitDepth} " +
                $"peakNits={m.peakNits} {m.width}x{m.height}");
            // 同时给出“度量优先”的识别结果（与 sourceGamutName 同一入口），便于脚本断言而不看描述文字
            {
                var (ip2, id2) = IccProfileService.ExtractIccToTempFile(args[1]);
                Console.WriteLine($"spaceName=" + (FfmpegGui.Services.ColorMapping.ColorSpaceIdentify
                    .IdentifyIccSpaceName(ip2, id2) ?? "(null)"));
                IccProfileService.TryDeleteIcc(ip2);
            }
            var opt = new FfmpegGui.Models.FfmpegOptions { Format = args.Length >= 3 ? args[2] : "png" };
            var f = FfmpegGui.Services.FfmpegCommandBuilder.ResolveFaithfulUltraWideIcc(opt, m.sourceGamutName, m.colorTrc, opt.Format);
            Console.WriteLine($"faithfulIcc={(f ?? "(null)")}");
        }

    }
}
