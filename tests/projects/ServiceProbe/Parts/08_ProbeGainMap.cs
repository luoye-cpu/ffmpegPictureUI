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
        // ────────────────────────────── gainmap ──────────────────────────────

        private static void ProbeGainMap(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe gainmap <jpeg> [outSecondary]"); _fail++; return; }
            var path = args[1];
            var bytes = File.ReadAllBytes(path);
            Console.WriteLine($"file={path} size={bytes.Length}");

            var c = GainMapDecoder.JpegContainer.Parse(bytes);
            Check(c != null, "容器解析非 null");
            if (c == null) return;
            Console.WriteLine($"images={c.Images.Count}");
            for (int i = 0; i < c.Images.Count; i++)
            {
                var im = c.Images[i];
                Console.WriteLine($"image[{i}] start={im.Start} len={im.Length} {im.W}x{im.H}");
            }
            Console.WriteLine($"mpfEntries={c.MpfEntries.Count} mpfBase={c.MpfBaseOffset}");
            for (int i = 0; i < c.MpfEntries.Count; i++)
            {
                var e = c.MpfEntries[i];
                int abs = c.MpfBaseOffset + (int)e.Offset;
                bool soi = abs > 0 && abs + 1 < bytes.Length && bytes[abs] == 0xFF && bytes[abs + 1] == 0xD8;
                Console.WriteLine($"mpf[{i}] attr=0x{e.Attr:X8} size={e.Size} offset={e.Offset} abs={abs} soi={soi}");
            }
            var iso = c.IsoMetadata;
            if (iso == null) Console.WriteLine("iso=null");
            else
            {
                Console.WriteLine($"iso min={iso.GainMapMin:0.####} max={iso.GainMapMax:0.####} gamma={iso.Gamma:0.####} " +
                    $"offsetSDR={iso.OffsetSdr:0.####} offsetHDR={iso.OffsetHdr:0.####} channels={iso.Channels} " +
                    $"hdrBase={iso.BaseRenditionIsHdr} capMin={iso.HdrCapacityMin:0.####} capMax={iso.HdrCapacityMax:0.####}");
            }
            Check(c.IsGainMapContainer(), "IsGainMapContainer()=true");
            Check(c.Images.Count >= 2, $"顶层图像数 {c.Images.Count} >= 2");
            Check(iso != null, "ISO 21496-1 元数据存在");
            // 增益区间非零：SDR 底 max>0（上映射）或 HDR 底 min<0（下映射）均合法
            Check(iso != null && (iso.GainMapMax > 0 || iso.GainMapMin < 0), "ISO 增益区间非零");
            if (iso != null && iso.BaseRenditionIsHdr)
                Console.WriteLine("note=HDR 底（backwardDirection）：增益应为 min<0, max=0");

            var sec = c.GetSecondaryImage();
            Check(sec != null, "副图定位成功");
            if (sec != null && args.Length >= 3)
            {
                File.WriteAllBytes(args[2], c.Slice(sec.Value));
                Console.WriteLine($"secondary_written={args[2]} bytes={c.Slice(sec.Value).Length}");
            }
        }

    }
}
