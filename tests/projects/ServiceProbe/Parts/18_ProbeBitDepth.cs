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
        /// bitdepth —— 端到端验证 Plan.OutBitDepth 真的驱动了出口位深（否则新接的 8bit 内核就是死代码）。
        /// 用 Adobe→sRGB（不可命名⇒必走 H1）强制发生变换，再读 PNG IHDR 的位深字节。
        /// </summary>
        private static async Task ProbeBitDepth(string[] args)
        {
            string inPng = args.Length > 1 ? args[1] : "tests/output/validate/bit_in.png";
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(inPng))!);
            // 造一个 16bit 输入图（内容无关，只验位宽）
            if (!File.Exists(inPng))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(ff!,
                    $"-y -hide_banner -loglevel error -f lavfi -i \"smptehdbars=s=64x48\" -pix_fmt rgb48le -frames:v 1 \"{inPng}\"")
                { RedirectStandardError = true, UseShellExecute = false };
                using var pr = System.Diagnostics.Process.Start(psi)!; pr.StandardError.ReadToEnd(); pr.WaitForExit();
            }
            Check(File.Exists(inPng) && new FileInfo(inPng).Length > 100, $"16bit 输入就绪 {inPng}");

            var adobe = CS.FromNamedSpace("Adobe RGB");
            var srgb = CS.FromCicp("bt709", "iec61966-2-1", "sRGB");
            if (adobe == null || srgb == null) { Check(false, "描述符构造失败"); return; }
            var spec = Spec("bt470bg", "bt470bg", "bt709", "iec61966-2-1");

            foreach (var (depth, expect) in new[] { (8, 8), (16, 16) })
            {
                var it = new CI { Strategy = ST.Recommended, Source = adobe, Target = srgb, Format = CE.CapsFor("png"),
                                  TargetBitDepth = depth, TargetExplicit = true, AllowApproximateCurve = false };
                var plan = CE.Plan(it);
                Check(plan.Action == CA.Map && plan.Backend == CB.InProcess,
                    $"depth={depth}: 用例需走 H1 映射（实为 {plan.Action}/{plan.Backend}）");
                Check(plan.OutBitDepth == expect, $"depth={depth}: Plan.OutBitDepth 应为 {expect}（实为 {plan.OutBitDepth}）");
                Check(plan.NeedsDither == (depth == 8), $"depth={depth}: NeedsDither 应与位深一致（实={plan.NeedsDither}）");

                string outPng = $"tests/output/validate/bit_{depth}.png";
                if (File.Exists(outPng)) File.Delete(outPng);
                var st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(inPng, outPng, spec, plan, null, default, 2);
                Check(File.Exists(outPng) && new FileInfo(outPng).Length > 100, $"depth={depth}: 端到端产出非空 PNG（{st.ProcessMs}ms）");
                int bd = PngBitDepth(outPng);
                Check(bd == expect, $"depth={depth}: 输出 PNG IHDR 位深应为 {expect}（实为 {bd}）");
            }
        }

        /// <summary>直接读 PNG IHDR 的 bit depth 字节（宽 4 + 高 4 后的那一个）。</summary>
        private static int PngBitDepth(string path)
        {
            var b = File.ReadAllBytes(path);
            return b.Length > 25 ? b[24] : -1;
        }

    }
}
