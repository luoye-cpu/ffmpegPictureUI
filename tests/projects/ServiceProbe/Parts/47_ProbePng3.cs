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
        /// <summary>png3 &lt;file.png&gt; [sbitBits（0=不写）] [primaries transfer]：调生产代码 PngCicpService 写 chunk。</summary>
        private static void ProbePng3(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("用法: png3 <file.png> [sbitBits] [primaries transfer]"); return; }
            string path = args[1];
            // ⚠ 两项要分开报：旧写法 bits=0（意为“不写 sBIT”）也会去调 TryInsertSbit，
            //    它失败后把“cICP 已写成”一起抹平成“写入结果=False”⇒ 看板上误判为失败（实测）。
            bool sbitOk = true, cicpOk = true;
            bool didSbit = false, didCicp = false;
            int sbitBits = 0;
            if (args.Length >= 3 && int.TryParse(args[2], out int bits))
            {
                if (bits > 0) { didSbit = true; sbitBits = bits; sbitOk = FfmpegGui.Services.PngCicpService.TryInsertSbit(path, bits); }
            }
            if (args.Length >= 5 && byte.TryParse(args[3], out byte pr) && byte.TryParse(args[4], out byte tr))
            {
                didCicp = true; cicpOk = FfmpegGui.Services.PngCicpService.TryInsertCicp(path, pr, tr);
            }
            Console.WriteLine($"png3: {path} "
                + (didSbit ? $"sBIT(写 {sbitBits}bit)={sbitOk} " : "sBIT=跳过 ")
                + (didCicp ? $"cICP={cicpOk} " : "cICP=跳过 ")
                + $"大小={(new FileInfo(path).Exists ? new FileInfo(path).Length : -1)}");
            Check((!didSbit || sbitOk) && (!didCicp || cicpOk), "png3 chunk 写入成功");
        }

    }
}
