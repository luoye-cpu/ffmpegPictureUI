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
        /// peakmeasure —— 峰值链路最后一档：无用户值、无元数据时**整帧实测内容峰值**。
        ///
        /// 为什么不算“自己验自己”：期望 nits 由**素材生成时写入的最高码值**经 PQ 曲线定义换算
        /// （PQ EOTF 本身另有 curve 门禁的锚点），与实测路径无关。另外必须钉住：
        ///  • 用户显式给了峰值 ⇒ 绝不再去实测（不拿猜值覆盖用户值）；
        ///  • 实测低于参考白 ⇒ 钳到参考白，**不因图暗而增亮**；
        ///  • 执行层只改副本，Plan.Tone 不被污染；
        ///  • 表路径与解析式路径同值；
        ///  • 用户可见日志写明“峰值来源=整帧实测”。
        /// </summary>
        private static async System.Threading.Tasks.Task ProbePeakMeasure(string[] args)
        {
            if (args.Length < 3) { Console.WriteLine("usage: ServiceProbe peakmeasure <png> <maxCode 0..65535> [userPeakNits]"); _fail++; return; }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string src = args[1];
            if (!int.TryParse(args[2], out int maxCode) || maxCode < 0 || maxCode > 65535)
            { Check(false, $"最高码值参数非法：{args[2]}"); return; }
            double userPeak = args.Length >= 4 && double.TryParse(args[3], System.Globalization.NumberStyles.Any, ci, out var u) ? u : 0;
            if (!File.Exists(src)) { Check(false, $"素材存在 {src}（由 verify-color-peak.ps1 生成）"); return; }

            // ① 内核级：表路径与解析式路径必须同值
            var spec0 = new FfmpegGui.Services.ColorMapping.TransformSpec
            {
                SrcCurve = TC.Pq, DstCurve = TC.Srgb, LinearScale = TC.PqPeakNits / TC.SdrWhiteNits,
                Tone = new FfmpegGui.Services.ColorMapping.ToneMapParams
                { Mode = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, PeakNits = TC.PqPeakNits },
            };
            byte[] synth = SyntheticFrame16(64, 48);
            double vTab = CK.MeasurePeakLinearRgb48(synth, 64 * 48, spec0, CK.BuildTables(TC.Pq));
            double vAna = CK.MeasurePeakLinearRgb48(synth, 64 * 48, spec0, null);
            Check(System.Math.Abs(vTab - vAna) <= System.Math.Max(1e-4, vAna * 1e-4),
                $"实测内核：表路径与解析式同值（{vTab:F6} vs {vAna:F6}）");

            // ② 端到端：走生产管道（解码→实测→变换→编码）
            var o = new FfmpegGui.Models.FfmpegOptions
            {
                Format = "png", ColorSpace = "sRGB", ColorStrategy = ST.Recommended,
                ColorToneMap = "hable", BitDepth = 16, ColorHdrPeakNits = userPeak,
            };
            var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, src, out var why);
            Check(it != null, $"选项→意图（{(it == null ? why : "OK")}）");
            if (it == null) return;
            var plan = CE.Plan(it);
            if (plan.Tone == null) { Check(false, $"tonemap 未挂载（{plan.Reason}）"); return; }
            Console.WriteLine($"plan.Tone={plan.Tone} MeasurePeak={plan.Tone.MeasurePeak}");
            Check(userPeak > 0 ? !plan.Tone.MeasurePeak : plan.Tone.MeasurePeak,
                  userPeak > 0 ? "用户已指定峰值 ⇒ 不标记实测" : "无用户值/无元数据 ⇒ 标记待实测");

            var spec = plan.ToSpec();
            string outPng = Path.Combine(Path.GetTempPath(), $"pmout_{Guid.NewGuid():N}.png");
            var logSb = new StringBuilder();
            var st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                src, outPng, spec, plan, s => logSb.Append(s), default, 1).ConfigureAwait(false);
            double expect = maxCode > 0 ? TC.Pq.ToLinear(maxCode / 65535.0) * TC.PqPeakNits : 0;
            Console.WriteLine($"maxCode={maxCode} 期望={expect:F2}nit 实测={st.MeasuredPeakNits:F2}nit");
            Console.WriteLine($"spec.Tone={spec.Tone} log={logSb.ToString().Trim().Replace('\n', ' ')}");
            try { File.Delete(outPng); } catch { }

            Check(System.Math.Abs(plan.Tone.PeakNits - (userPeak > 0 ? userPeak : TC.PqPeakNits)) < 1e-6,
                  $"执行层不污染规划层 Plan.Tone（仍为 {(userPeak > 0 ? userPeak : TC.PqPeakNits):F0}nit）");
            Check(!spec.Tone!.MeasurePeak, "实测标记已清除（不会重复扫描）");
            if (userPeak > 0)
            {
                Check(st.MeasuredPeakNits == 0, $"用户指定峰值时确实未实测（得 {st.MeasuredPeakNits}）");
                Check(System.Math.Abs(spec.Tone.PeakNits - userPeak) < 1e-6, "spec 峰值仍等于用户值");
                return;
            }
            // 两条口径分别验：**实测到了什么**（原始值，哪怕落在钳制区间外）与**策略采了什么**（钳制后）。
            // 否则“很暗的图”会因钳制到 203nit 而看不出实测本身错没错。
            Check(System.Math.Abs(st.MeasuredPeakNits - expect) <= System.Math.Max(1.0, expect * 0.02),
                  $"实测原始值 = 期望 {expect:F2}nit（±2%，实 {st.MeasuredPeakNits:F2}）");
            double wantApplied = System.Math.Clamp(expect, TC.SdrWhiteNits, TC.PqPeakNits);
            Check(System.Math.Abs(spec.Tone.PeakNits - wantApplied) <= System.Math.Max(1.0, wantApplied * 0.02),
                  $"采用值 = clamp(期望,{TC.SdrWhiteNits:F0}…{TC.PqPeakNits:F0}) = {wantApplied:F2}nit（实 {spec.Tone.PeakNits:F2}）");
            Check(spec.Tone.Active == (wantApplied > TC.SdrWhiteNits * (1 + 1e-6)),
                  $"Active 与采用值一致（active={spec.Tone.Active} headroom={spec.Tone.Headroom:F3}）"
                  + (wantApplied <= TC.SdrWhiteNits ? "——图很暗 ⇒ 恒等，**绝不因图暗而增亮**" : ""));
            Check(logSb.ToString().Contains("峰值来源=整帧实测"), "用户可见日志写明实测来源");
        }

    }
}
