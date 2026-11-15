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
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe icc <色彩空间名> [v2|v4]"); _fail++; return; }
            var spec = args[1];
            // 可选第三参 = 版本档（缺省 v4.4）；v2 臂专验"真 v2"的 tag 类型
            var ver = args.Length >= 3
                ? FfmpegGui.Services.IccProfileBuilder.ParseVersionToken(args[2])
                : FfmpegGui.Services.IccProfileBuilder.IccProfileVersion.V44;
            var s = ColorSpaceRegistry.Resolve(spec);
            Console.WriteLine($"spec={s?.Key ?? "(未识别)"} kind={s?.Kind.ToString() ?? "-"} " +
                $"out={s?.OutPrimaries}/{s?.OutTrc}/{s?.OutMatrix}");
            var icc = IccProfileService.ResolveNamedIcc(spec, null,
                s?.OutPrimaries ?? "bt709", s?.OutTrc ?? "iec61966-2-1", s?.OutMatrix ?? "bt709", ver);
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
            // ── 版本档结构锁 ──
            if (ver == FfmpegGui.Services.IccProfileBuilder.IccProfileVersion.V2)
            {
                Check(b[8] == 2 && b[9] == 0 && b[10] == 0 && b[11] == 0, "v2 header = 02 00 00 00");
                var (doff, _) = IccProfileService.FindIccTag(b, "desc");
                Check(doff > 0 && ReadAscii(b, (int)doff, 4) == "desc", "v2 desc 为 descriptionType('desc')");
                var (coff, _) = IccProfileService.FindIccTag(b, "cprt");
                Check(coff > 0 && ReadAscii(b, (int)coff, 4) == "text", "v2 cprt 为 textType('text')");
                var (roff, _) = IccProfileService.FindIccTag(b, "rTRC");
                Check(roff > 0 && ReadAscii(b, (int)roff, 4) == "curv", "v2 rTRC 为 curv（v2 不认 para）");
                // curv 形态二选一：count=1 = 单值幂 gamma（u8Fixed8，ProPhoto/AdobeRGB）；count=256 = 采样表（非幂曲线）
                uint v2cnt = (uint)((b[roff + 8] << 24) | (b[roff + 9] << 16) | (b[roff + 10] << 8) | b[roff + 11]);
                Check(v2cnt == 1 || v2cnt == 256, $"v2 curv 形态合法（count={v2cnt}：1=单值幂 gamma / 256=采样表）");
                var (hoff, _) = IccProfileService.FindIccTag(b, "chad");
                Check(hoff == 0, "v2 不写 chad");
            }
            else
            {
                Check(b[8] == 4 && b[9] == 0x40 && b[10] == 0 && b[11] == 0, "v4.4 header = 04 40 00 00");
                var (coff, _) = IccProfileService.FindIccTag(b, "cprt");
                Check(coff > 0 && ReadAscii(b, (int)coff, 4) == "mluc", "v4.4 cprt 为 mluc（非 v2 textType）");
                var (hoff, _) = IccProfileService.FindIccTag(b, "chad");
                Check(hoff > 0, "v4.4 保留 chad");
            }
        }

        private static double ReadS15(byte[] b, int o)
            => ((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]) / 65536.0;

    }
}
