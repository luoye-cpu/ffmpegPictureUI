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
        /// <summary>pq —— SMPTE ST 2084 EOTF/OETF 锚点值与互逆自检（防符号/常数写错致画面变黑）。</summary>
        private static void ProbeDngXmpArgs()
        {
            // 判据形状：两臂必须真的分叉（不是"两条都含/都不含"）。
            // 背景（账本 §40.9）：dngtool 构建 qDNGUseXMP=0 ⇒ DNG 出口的 XMP(700) 只能靠产品侧
            // 这条 exiftool 后置抄；原来白名单里没有 XMP ⇒ DNG→DNG 的评级/关键词/crs 设置静默丢。
            var keep = new FfmpegGui.Models.FfmpegOptions();
            string a = ExifToolService.BuildDngStdTagsCarryArgs("SRC.dng", "DST.dng", keep);
            var strip = new FfmpegGui.Models.FfmpegOptions { StripXmp = true };
            string b = ExifToolService.BuildDngStdTagsCarryArgs("SRC.dng", "DST.dng", strip);
            Console.WriteLine("DNG-XMP-ARGS default=" + a);
            Console.WriteLine("DNG-XMP-ARGS stripxmp=" + b);
            Check(a.Contains("-XMP:all"), "默认必须抄 XMP（-XMP:all 在串里）");
            Check(!b.Contains("-XMP:all"), "--strip-xmp 必须不抄 XMP（两臂真的分叉）");
        }

    }
}
