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
        /// psnr &lt;图A&gt; &lt;图B&gt; [阈dB] —— 两条执行路径产物是否一致的通用度量。
        /// 统一解到 rgb48 再比（消除 pix_fmt/位深差异带来的假差异），峰值按**实际满量程** 65535 算。
        /// </summary>
        private static async Task ProbePsnr(string[] args)
        {
            if (args.Length < 3) { Console.WriteLine("usage: ServiceProbe psnr <a> <b> [thresholdDb]"); _fail++; return; }
            double thr = args.Length >= 4 && double.TryParse(args[3], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 55;
            var (wa, ha) = await FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(args[1]).ConfigureAwait(false);
            var (wb, hb) = await FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(args[2]).ConfigureAwait(false);
            Check(wa > 0 && wa == wb && ha == hb, $"两图尺寸相同且可探测（{wa}x{ha} vs {wb}x{hb}）");
            if (wa <= 0 || wa != wb || ha != hb) return;
            var a = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(args[1], wa, ha).ConfigureAwait(false);
            var b = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(args[2], wb, hb).ConfigureAwait(false);
            double psnr = CM.Psnr16(a, b);
            int maxDiff = 0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i + 1 < n; i += 2)
                maxDiff = Math.Max(maxDiff, Math.Abs((a[i] | (a[i + 1] << 8)) - (b[i] | (b[i + 1] << 8))));
            Console.WriteLine($"psnr={psnr:F2}dB maxDiff={maxDiff}/65535 尺寸={wa}x{ha}");
            Check(psnr >= thr, $"两路产物 PSNR ≥ {thr}dB（实 {psnr:F2}）");
        }

    }
}
