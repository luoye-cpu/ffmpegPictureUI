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
        /// peak —— 端到端验“源峰值读取”：ffprobe 静态元数据 → ColorIntentFactory → Plan.Tone。
        ///
        /// 素材由 verify-color-peak.ps1 现造，故意让 **MaxCLL=600 与 mastering=4000 不同**，
        /// 于是一次数值就能分辨四种结果：600=对（键名 max_content、MaxCLL 优先）；
        /// 4000=又取了两者较大值（过度压暗）；0.4=有理数被重复换算；0=探测失效（旧代码正是这个）。
        /// 另跑无元数据的 HDR 图：必须回退到源曲线名义峰值，且**日志要写明是回退**。
        /// </summary>
        private static void ProbePeak(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: ServiceProbe peak <file> [expectMetaPeak] [userPeakNits]");
                _fail++;
                return;
            }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string file = args[1];
            double expect = args.Length >= 3 && double.TryParse(args[2], System.Globalization.NumberStyles.Any, ci, out var e) ? e : -1;
            double userPeak = args.Length >= 4 && double.TryParse(args[3], System.Globalization.NumberStyles.Any, ci, out var u) ? u : 0;
            if (!File.Exists(file)) { Check(false, $"素材存在：{file}（由 verify-color-peak.ps1 生成）"); return; }

            var m = FfmpegGui.Services.FfmpegCommandBuilder.ProbeInputColorMetadata(file);
            Console.WriteLine($"trc={m.colorTrc ?? "-"} metaPeak={m.peakNits:F2} metaSource={m.peakSource ?? "-"}");
            if (expect >= 0)
                Check(System.Math.Abs(m.peakNits - expect) < System.Math.Max(0.01, expect * 1e-3)
                      && (expect == 0 || !string.IsNullOrEmpty(m.peakSource)),
                    $"元数据峰值 = {expect}（实读 {m.peakNits:F2}，来源 {m.peakSource ?? "-"}）");

            var o = new FfmpegGui.Models.FfmpegOptions
            {
                Format = "png", ColorSpace = "sRGB", ColorStrategy = ST.Recommended,
                ColorToneMap = "hable", BitDepth = 8, ColorHdrPeakNits = userPeak,
            };
            var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, file, out var why);
            Check(it != null, $"选项→意图可装配（{(it == null ? why : "OK")}）");
            if (it == null) return;
            Console.WriteLine($"intentPeak={it.HdrPeakNits:F2} intentSource={it.HdrPeakSource ?? "(无→策略层回退)"}");
            double wantIntent = userPeak > 0 ? userPeak : m.peakNits;
            Check(System.Math.Abs(it.HdrPeakNits - wantIntent) < 1e-6,
                  $"优先级 用户({userPeak}) > 元数据({m.peakNits:F2}) ⇒ 意图取 {wantIntent:F2}");

            var p = CE.Plan(it);
            if (p.Tone == null) { Check(false, $"色调映射未挂载（action={p.Action} reason={p.Reason}）"); return; }
            double wantPeak = wantIntent > 0 ? wantIntent : (it.Source?.NitsOfLinearOne ?? 0);
            Console.WriteLine($"tone={p.Tone} colorLoss={p.ColorLoss}");
            Console.WriteLine($"reason={p.Reason}");
            Check(System.Math.Abs(p.Tone.PeakNits - wantPeak) < System.Math.Max(0.01, wantPeak * 1e-3)
                  && p.Tone.Active && p.Tone.Headroom > 1.0 + 1e-6,
                  $"计划峰值={wantPeak:F2} 且真的激活（peak={p.Tone.PeakNits:F2} headroom={p.Tone.Headroom:F2}）");
            Check(p.Reason.Contains("峰值来源="), "日志写明峰值来源（不静默）");
            Check(userPeak > 0 ? p.Reason.Contains("用户 --color-hdr-peak")
                               : (m.peakNits > 0 ? p.Reason.Contains(m.peakSource ?? "§") : p.Reason.Contains("名义峰值")),
                  $"来源文案与实际一致（{(userPeak > 0 ? "用户" : m.peakNits > 0 ? m.peakSource : "名义回退")}）");
        }

    }
}
