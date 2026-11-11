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
        /// <summary>faithful &lt;色彩空间名&gt; [期望 yes|no] —— 保真 ICC 可用性 + 确定性(重复生成同字节)。</summary>
        private static void ProbeFaithful(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe faithful <spec> [yes|no]"); _fail++; return; }
            var spec = args[1];
            var expectYes = args.Length >= 3 ? args[2].StartsWith("y", StringComparison.OrdinalIgnoreCase) : (bool?)null;
            var p1 = IccProfileService.GetFaithfulIcc(spec);
            var p2 = IccProfileService.GetFaithfulIcc(spec);
            Console.WriteLine($"spec={spec} faithfulOnly={IccProfileBuilder.IsFaithfulOnly(spec)} icc={p1 ?? "(null)"}");
            if (p1 == null)
            {
                if (expectYes == false) Check(true, $"{spec} 不走保真路径（预期）");
                else Check(false, $"{spec} 应有保真 ICC 但为 null");
                return;
            }
            Check(expectYes != false, $"{spec} 保真 ICC 解析成功");
            var b1 = File.ReadAllBytes(p1);
            Check(p2 == p1 && File.ReadAllBytes(p2!).AsSpan().SequenceEqual(b1), "重复解析同一缓存且字节一致（确定性）");
            Check(IccProfileService.IsValidIccProfile(p1), "ICC 魔数有效");
            var info = IccProfileService.ParseInfo(p1);
            Console.WriteLine($"size={b1.Length} version={info?.Version} class={info?.DeviceClass} cs={info?.ColorSpace} desc={info?.Description}");
            Check(!string.IsNullOrWhiteSpace(info?.Description), "mluc desc 可回读");
            Check((info?.ColorSpace ?? "").StartsWith("RGB"), "色彩空间签名 RGB");
            // 关键 tag 齐备且 wtpt = D50
            foreach (var t in new[] { "wtpt", "bkpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "chad", "desc" })
            {
                var (off, len) = IccProfileService.FindIccTag(b1, t);
                Check(off > 0 && len > 0, $"tag {t} 存在 (off={off} len={len})");
            }
            var (wo, _) = IccProfileService.FindIccTag(b1, "wtpt");
            Check(Math.Abs(ReadS15(b1, (int)wo + 12) - 1.0) < 0.001, "wtpt.Y = 1.0（D50 PCS 归一）");
            // tag 目录必须升序（ICC.1 强制 + 本仓库二分查找依赖）
            uint n = (uint)((b1[128] << 24) | (b1[129] << 16) | (b1[130] << 8) | b1[131]);
            bool sorted = true;
            for (int i = 1; i < n; i++)
                if (string.CompareOrdinal(ReadAscii(b1, 132 + (i - 1) * 12, 4), ReadAscii(b1, 132 + i * 12, 4)) > 0) sorted = false;
            Check(sorted, "tag 目录升序");
        }

    }
}
