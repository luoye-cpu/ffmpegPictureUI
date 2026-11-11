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
        /// tonemap —— P4a 色调映射门禁。
        /// 只验“数学性质 + 决策挂载 + 值域换算 + 端到端产出”，
        /// 不去比“跟 ffmpeg hable 一致”（Hable/BT.2390 常数未经标定，故意不实现、不假装通过）。
        /// </summary>
        private static async Task ProbeToneMap(string[] args)
        {
            var off = new FfmpegGui.Services.ColorMapping.ToneMapParams { Mode = FfmpegGui.Services.ColorMapping.ToneMapMode.None, PeakNits = 1000 };
            Check(!off.Active, $"未选算子 ⇒ 不激活（{off}）");
            var flat = new FfmpegGui.Services.ColorMapping.ToneMapParams { Mode = FfmpegGui.Services.ColorMapping.ToneMapMode.ReinhardSegmented, PeakNits = 203 };
            Check(!flat.Active && Math.Abs(flat.Headroom - 1.0) < 1e-12, "headroom=1 ⇒ 不激活（保证不会把 SDR 内容“静默压暗”）");
            Check(Math.Abs(FfmpegGui.Services.ColorMapping.ToneMap.Apply(0.42, flat) - 0.42) < 1e-12, "未激活时严格恒等");

            var p = new FfmpegGui.Services.ColorMapping.ToneMapParams
            { Mode = FfmpegGui.Services.ColorMapping.ToneMapMode.ReinhardSegmented, PeakNits = 1000, SdrWhiteNits = 203 };
            double hr = p.Headroom;
            Check(hr > 4 && hr < 5.5, $"headroom = 1000/203 ≈ {hr:F3}");
            Check(Math.Abs(FfmpegGui.Services.ColorMapping.ToneMap.Apply(0.0, p)) < 1e-6, "端点：Apply(0)=0");
            Check(Math.Abs(FfmpegGui.Services.ColorMapping.ToneMap.Apply(0.5, p) - 0.5) < 1e-6, "knee 以下直通（SDR 内容零损失）");
            Check(Math.Abs(FfmpegGui.Services.ColorMapping.ToneMap.Apply(hr, p) - 1.0) < 1e-3, "端点：峰值→1.0");
            var curve = FfmpegGui.Services.ColorMapping.ToneMap.BuildCurve(p, 512, 0, hr);
            bool mono = true; int bad = -1;
            for (int i = 1; i < curve.Length; i++) if (curve[i] < curve[i - 1] - 1e-7) { mono = false; bad = i; break; }
            Check(mono, $"曲线单调不降（512 点，首个下降点 idx={bad}）");
            bool inRange = true;
            for (int i = 0; i < curve.Length; i++) if (curve[i] < -1e-6 || curve[i] > 1 + 1e-6) { inRange = false; break; }
            Check(inRange, "输出全部落在 [0,1]");

            // 保色相：三通道按同一因子缩放（以 max 为压缩对象）
            float r0 = 3.1f, g0 = 1.55f, b0 = 0.62f;
            float r = r0, g = g0, b = b0;
            FfmpegGui.Services.ColorMapping.ToneMap.ApplyRgb(ref r, ref g, ref b, p, TC.Srgb);
            double gr0 = g0 / r0, gb0 = b0 / r0, gr1 = (double)g / r, gb1 = (double)b / r;
            Check(Math.Abs(gr1 - gr0) < 1e-4 && Math.Abs(gb1 - gb0) < 1e-4,
                $"保色相：G/R、B/R 不变（{gr0:F5}→{gr1:F5}, {gb0:F5}→{gb1:F5}）");
            Check(r > r0 / p.Headroom && r < r0, $"峰值通道被压缩但未截断到 0（{r0:F2}→{r:F3}）");

            // 决策挂载：未请求 ⇒ Tone==null；请求 ⇒ ColorLoss=ToneMapped
            var pq = CS.FromCicp("bt2020", "smpte2084", "BT.2100 PQ", nitsOfLinearOne: TC.PqPeakNits);
            var srgb = CS.FromCicp("bt709", "iec61966-2-1", "sRGB");
            if (pq == null || srgb == null) { Check(false, "描述符构造失败"); return; }
            var itNo = new CI { Strategy = ST.Manual, Source = pq, Target = srgb, Format = CE.CapsFor("png"),
                                 TargetExplicit = true, AllowApproximateCurve = false, HdrPeakNits = 1000 };
            var pNo = CE.Plan(itNo);
            Check(pNo.Tone == null && pNo.ColorLoss != CL.ToneMapped,
                $"未请求 tonemap ⇒ 不挂载（Tone={pNo.Tone} loss={pNo.ColorLoss}）");
            // ⚠ CI 是引用类型：必须另建一个意图，不能 `var itYes = itNo` 后改字段（会污染 pNo 的用例）
            var itYes = new CI { Strategy = ST.Manual, Source = pq, Target = srgb, Format = CE.CapsFor("png"),
                                 TargetExplicit = true, AllowApproximateCurve = false, HdrPeakNits = 1000,
                                 ToneMapRequested = true, ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.ReinhardSegmented };
            var pYes = CE.Plan(itYes);
            Check(pYes.Tone is { Active: true }, $"请求后已挂载（{pYes.Tone}）");
            Check(pYes.ColorLoss == CL.ToneMapped, $"ColorLoss 归类为 ToneMapped（实为 {pYes.ColorLoss}）");

            // 值域换算：PQ 的 1.0=10000nit → 需 LinearScale=10000/203；无 tonemap 时必须为 1
            double ls = pYes.ToSpec().LinearScale;
            Check(Math.Abs(ls - 10000.0 / 203.0) < 1e-6, $"ToSpec().LinearScale = 10000/203（实为 {ls:F4}）");
            Check(Math.Abs(pNo.ToSpec().LinearScale - 1.0) < 1e-12, "无 tonemap ⇒ LinearScale=1（不改旧行为）");

            // 端到端：只验“接线可用”（产出存在且可解）；亮度类断言不在此做——
            // ❌ 旧版本曾断言“tonemap 必提升平均亮度”，但本测图经 PQ 解码+缩放后根本无超白成分，
            //    而 knee 以下本就是直通 ⇒ 开/关必然一致 ⇒ 该断言是个错假设（测试设计问题，非实现问题）。
            string src = "tests/output/validate/tm_in.png";
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            var psi = new System.Diagnostics.ProcessStartInfo(ff!,
                $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc2=s=64x48:duration=1:rate=1\" -pix_fmt rgb48le -frames:v 1 " +
                $"-color_primaries bt2020 -color_trc smpte2084 \"{src}\"")
            { RedirectStandardError = true, UseShellExecute = false };
            using (var pr = System.Diagnostics.Process.Start(psi)!) { pr.StandardError.ReadToEnd(); pr.WaitForExit(); }
            if (File.Exists(src))
            {
                string o2 = "tests/output/validate/tm_on.png";
                if (File.Exists(o2)) File.Delete(o2);
                await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(src, o2, pYes.ToSpec(), pYes, null, default, 2);
                Check(File.Exists(o2) && new FileInfo(o2).Length > 100, "端到端：tonemap 计划能完成 解码→变换→编码 全链路");
            }
            else Check(false, $"端到端测试图生成失败 {src}");

            // 确定性内核级判据：造一条 PQ 编码域骟梯（含超白段），直接比较开/关两路输出
            {
                int n = 512;
                var inb = new byte[n * 6]; var outA = new byte[n * 6]; var outB = new byte[n * 6];
                for (int i = 0; i < n; i++)
                {
                    int v = (int)Math.Round(i / (n - 1.0) * 65535.0);
                    for (int c = 0; c < 3; c++) { inb[i * 6 + c * 2] = (byte)(v & 0xFF); inb[i * 6 + c * 2 + 1] = (byte)(v >> 8); }
                }
                // ⚠ 必须把两路统一到同一 nits 域（否则比的不是“是否 tonemap”，而是 LinearScale 差异）。
                const double nitScale = 10000.0 / 203.0;
                var specA = pNo.ToSpec();  specA.LinearScale = nitScale;   // 同域、无 tonemap（纯钳位）
                var specB = pYes.ToSpec();                                 // 同域 + tonemap
                CK.TransformRgb48le(inb, outA, specA, n, null, null);
                CK.TransformRgb48le(inb, outB, specB, n, null, null);
                Func<byte[], int, int> code = (buf, i) => buf[i * 6] + (buf[i * 6 + 1] << 8);
                // ① 存在“超白但未饱和”的压缩带：开 tonemap 后该段严格变暗且仍 >0。
                // ❌ 不可拿最高码做比较：那里关路被钳位到 1.0、开路也被映到端点 1.0，两者相同（无信息）。
                int cmpIdx = -1, cA = 0, cB = 0;
                for (int i = n - 1; i >= 0; i--)
                {
                    int a = code(outA, i), b2 = code(outB, i);
                    if (a != b2) { cmpIdx = i; cA = a; cB = b2; break; }
                }
                Check(cmpIdx > 0 && cB < cA && cB > 0,
                    $"存在超白压缩带（首个差异 idx={cmpIdx}：关={cA} 开={cB}，开路更暗但未丢黑）");
                // ② 单调性：开 tonemap 后仍逐码单调不降
                bool mono2 = true; int dIdx = -1;
                for (int i = 1; i < n; i++) if (code(outB, i) < code(outB, i - 1)) { mono2 = false; dIdx = i; break; }
                Check(mono2, $"tonemap 后码域仍单调（首个下降 idx={dIdx}）");
                // ③ 亚 knee 段逐值不变（SDR 内容零损失，不“静默压暗”）
                int sameCnt = 0, diffBelowKnee = 0;
                for (int i = 0; i < n; i++)
                {
                    double lin = TC.Pq.ToLinear(i / (n - 1.0)) * nitScale;   // → 1.0 = SDR 白（与两路一致）
                    if (lin <= 0.75)
                    {
                        if (code(outA, i) == code(outB, i)) sameCnt++; else diffBelowKnee++;
                    }
                }
                Check(diffBelowKnee == 0 && sameCnt > 20,
                    $"亚 knee 段逐值不变（相同={sameCnt}，异常={diffBelowKnee}）→ SDR 内容零损失");
            }
        }

        /// <summary>用 ffmpeg signalstats 取 YAVG（归一化到 0..1）。</summary>
        private static double MeanLuma(string path)
        {
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            var psi = new System.Diagnostics.ProcessStartInfo(ff!,
            $"-hide_banner -i \"{path}\" -vf format=gray,signalstats,metadata=print -f null -")
            { RedirectStandardError = true, UseShellExecute = false };
            using var pr = System.Diagnostics.Process.Start(psi)!;
            string err = pr.StandardError.ReadToEnd(); pr.WaitForExit();
            // signalstats 只写帧 metadata，必须经 metadata=print 输出（loglevel error 下\u539f\u672c\u4ec0\u4e48\u90fd\u770b\u4e0d\u5230）
            var mm = System.Text.RegularExpressions.Regex.Matches(err, "YAVG=(-?\\d+(?:\\.\\d+)?)");
            if (mm.Count == 0) return -1;
            double acc = 0;
            foreach (System.Text.RegularExpressions.Match x in mm)
                acc += double.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return acc / mm.Count / 255.0;
        }

    }
}
