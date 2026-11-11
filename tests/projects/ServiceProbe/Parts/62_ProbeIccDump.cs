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
        /// <summary>icc-dump &lt;file.icc&gt; [tag ...] —— 打印 ICC 头 + tag 表 + 指定 tag 摘要（校准参考/自检）。</summary>
        private static void ProbeIccDump(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe icc-dump <file.icc> [tag ...]"); _fail++; return; }
            var path = args[1];
            var b = File.ReadAllBytes(path);
            var info = IccProfileService.ParseInfo(path);
            Console.WriteLine($"file={path} size={b.Length} declaredSize={(uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3])}");
            Console.WriteLine($"version={info?.Version} class={info?.DeviceClass} cs={info?.ColorSpace} pcs={info?.Pcs} desc={info?.Description}");
            Console.WriteLine($"creator={ReadAscii(b, 80, 4)} intent={ReadAscii(b, 64, 4)} magic={ReadAscii(b, 36, 4)}");
            uint tagCount = (uint)((b[128] << 24) | (b[129] << 16) | (b[130] << 8) | b[131]);
            Console.WriteLine($"tags={tagCount}");
            for (int i = 0; i < tagCount && 132 + i * 12 + 11 < b.Length; i++)
            {
                int e = 132 + i * 12;
                var sig = ReadAscii(b, e, 4);
                uint off = (uint)((b[e + 4] << 24) | (b[e + 5] << 16) | (b[e + 6] << 8) | b[e + 7]);
                uint len = (uint)((b[e + 8] << 24) | (b[e + 9] << 16) | (b[e + 10] << 8) | b[e + 11]);
                var type = off + 8 <= b.Length - 4 ? ReadAscii(b, (int)off, 4) : "????";
                Console.WriteLine($"  {sig} type={type} off={off} len={len}");
            }
            foreach (var tag in args.Skip(2))
            {
                var (off, len) = IccProfileService.FindIccTag(b, tag);
                if (off == 0 || len == 0 || off + len > b.Length) { Console.WriteLine($"{tag}: 未命中/越界"); continue; }
                var o = (int)off;
                var t = ReadAscii(b, o, 4);
                if (t == "para")
                {
                    int ft = (int)ReadBe16(b, o + 8);
                    // parametricCurveType: sig(4) + reserved(4) + functionType(2) + reserved(2) → 参数从 +12 起
                    Console.WriteLine($"{tag}: para type={ft} " + string.Join(" ", EnumerateS15(b, o + 12, ((int)len - 12) / 4)));
                }
                else if (t == "curv")
                {
                    uint n = ReadBe32(b, o + 8);
                    Console.WriteLine($"{tag}: curv count={n}" + (n > 0 && n <= 4 ? $" vals=[{string.Join(",", CurvVals(b, o + 12, (int)n))}]" : ""));
                }
                else if (t == "XYZ ")
                    Console.WriteLine($"{tag}: XYZ [{string.Join(", ", EnumerateS15(b, o + 8, 3))}]");
                else if (t == "mntr")
                    Console.WriteLine($"{tag}: XYZType [{string.Join(", ", EnumerateS15(b, o + 8, 9))}]");
                else if (t == "desc")
                {
                    int al = (int)ReadBe32(b, o + 8);
                    Console.WriteLine($"{tag}: desc \"{Encoding.ASCII.GetString(b, o + 12, Math.Max(0, al - 1))}\"");
                }
                else Console.WriteLine($"{tag}: type={t} len={len}");
            }
        }

        private static string ReadAscii(byte[] b, int o, int n)
            => o >= 0 && o + n <= b.Length ? Encoding.ASCII.GetString(b, o, n) : "????";
        private static int ReadBe16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        private static uint ReadBe32(byte[] b, int o)
            => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        private static IEnumerable<string> EnumerateS15(byte[] b, int o, int count)
        {
            for (int k = 0; k < count && o + k * 4 + 3 < b.Length; k++)
                yield return (ReadBe32(b, o + k * 4) / 65536.0).ToString("0.0000");
        }
        private static IEnumerable<string> CurvVals(byte[] b, int o, int n)
        {
            for (int k = 0; k < n && o + k * 2 + 1 < b.Length; k++)
                yield return (((b[o + k * 2] << 8) | b[o + k * 2 + 1]) / 65535.0).ToString("0.0000");
        }

    }
}
