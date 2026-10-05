using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tests.Shared;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;
using CS = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor;
using CE = FfmpegGui.Services.ColorMapping.ColorMappingEngine;
using CI = FfmpegGui.Services.ColorMapping.ColorIntent;
using CA = FfmpegGui.Services.ColorMapping.ColorAction;
using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
using CL = FfmpegGui.Services.ColorMapping.ColorLoss;
using ST = FfmpegGui.Models.ColorStrategy;
using DC = FfmpegGui.Services.ColorMapping.DescriptorConfidence;
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;

namespace Tests.Diagnostics
{
    /// <summary>
    /// 子系统测试组：**diagnostics**（性能 / 自检 / 工具探测 / i18n）。
    /// <para>覆盖 mode：bench / peak / peakmeasure / psnr / xcheck / selftest / tooldetect / i18n。</para>
    /// <para>方法体自 `tests/ServiceProbe/Program.cs` **逐字抽出**（不改逻辑、不改字符串文本），
    /// 以保证与旧宿主**读数逐条一致**。</para>
    /// </summary>
    internal static class DiagnosticsGroup
    {
        // ⚠ 共享 helper 一律**转发** Harness（实现只此一份，避免各组读数分叉）。
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);
        private static void Skip(string msg) => Harness.Skip(msg);
        private static string RunCapture(string exe, string args, string? redirectOut = null) => Harness.RunCapture(exe, args, redirectOut);
        private static int RunCap(string exe, string args) => Harness.RunCap(exe, args);
        private static int RunTask(System.Threading.Tasks.Task<int> t) => Harness.RunTask(t);
        // ⚠ `_pass/_fail/_skip` 的**唯一实现**在 `Harness`（不在此复制，否则各组读数会分叉）。
        //   这两个 shim 只为让抽出的原文（`Harness.FailOnly();` 之类）**逐字不改**地编译。

        // ── 入口（供 Program 分派；实现即抽出的 Probe* 原文）──
                /// <summary>跑一条 CLI 并等它结束（**丢弃输出**）。仅本组内 `ProbeXcheck`/`ProbePeakMeasure` 使用。</summary>
        private static void RunCli(string exe, string args)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, Arguments = args, RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            p?.WaitForExit();
        }
internal static void RunBench(string[] a) => ProbeBench(a);
        internal static void RunPeak(string[] a) => ProbePeak(a);
        internal static void RunPeakMeasure(string[] a) => ProbePeakMeasure(a).GetAwaiter().GetResult();
        internal static void RunPsnr(string[] a) => ProbePsnr(a).GetAwaiter().GetResult();
        internal static void RunXcheck(string[] a) => ProbeXcheck(a).GetAwaiter().GetResult();
        internal static void RunSelfTest() => ProbeSelfTest();
        internal static void RunToolDetect(string[] a) => ProbeToolDetect(a);
        internal static void RunI18n() => ProbeI18n();
        private static void ProbeBench(string[] args)
        {
            // 绝对门槛只在 Release 下成立（Debug 关掉优化，吞吐会差 5-10 倍，拿它当基线会误判）
            bool isRelease = typeof(Program).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                is System.Reflection.AssemblyConfigurationAttribute[] { Length: > 0 } cfg
                && string.Equals(cfg[0].Configuration, "Release", StringComparison.OrdinalIgnoreCase);
            if (!isRelease)
                Console.WriteLine("⚠ 当前为 Debug 构建：只输出数值，不做绝对吞吐门槛（请用 -c Release 复跑）");

            bool big = args.Length >= 2 && args[1] == "big";
            var sizes = big ? new[] { (1024, 1024), (4096, 2160), (8192, 4096) } : new[] { (1024, 1024), (4096, 2160) };
            var namedCases = new (string name, string sp, string st, string dp, string dt)[]
            {
                ("sRGB→P3(矩阵同曲线)", "bt709", "iec61966-2-1", "smpte432", "iec61966-2-1"),
                ("BT.2020→sRGB(含裁切)", "bt2020", "bt709", "bt709", "iec61966-2-1"),
                ("PQ→sRGB(变曲线+裁切)", "bt2020", "smpte2084", "bt709", "iec61966-2-1"),
            };
            int cores = Environment.ProcessorCount;
            foreach (var (w, h) in sizes)
            {
                var src = SyntheticFrame16(w, h);
                var dst = new byte[src.Length];
                Console.WriteLine($"\n--- {w}x{h} ({(long)w * h / 1_000_000.0:F1} MPix, rgb48={src.Length / 1048576}MB) cores={cores} ---");
                foreach (var (name, sp, st, dp, dt) in namedCases)
                {
                    var spec = Spec(sp, st, dp, dt);
                    var ttS = CK.BuildTables(spec.SrcCurve); var ttD = CK.BuildTables(spec.DstCurve);
                    double single = 0, best = 0, singleSlow = 0, bestSlow = 0;
                    foreach (int dop in new[] { 1, Math.Min(2, cores), Math.Min(4, cores), Math.Min(8, cores), cores })
                    {
                        // 每档取 N 次最优（min 耗时）：单样本估扩展比噪声大（实测同一用例 2.9×/3.2×/5.2× 飘）
                        double ms = double.MaxValue, slowMs = double.MaxValue;
                        for (int rep = 0; rep < 3; rep++)
                        {
                            Array.Clear(dst);
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            int rows = Math.Max(1, h / dop);
                            var opt = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = dop };
                            System.Threading.Tasks.Parallel.For(0, (h + rows - 1) / rows, opt, b =>
                            {
                                int rs = b * rows, rc = Math.Min(rows, h - rs);
                                int off = rs * w * 6, n = rc * w;
                                CK.TransformRgb48le(new ReadOnlySpan<byte>(src, off, n * 6), new Span<byte>(dst, off, n * 6), spec, n, ttS, ttD);
                            });
                            sw.Stop();
                            ms = Math.Min(ms, sw.Elapsed.TotalMilliseconds);
                        }
                        double mpxs = (long)w * h / 1e6 / Math.Max(1e-6, ms / 1000.0);
                        double mbs = src.Length * 2.0 / 1048576.0 / Math.Max(1e-6, ms / 1000.0);
                        if (dop == 1 || dop == cores)
                        {
                            // 解析式（无表）对照，取 3 次最优
                            for (int rep = 0; rep < 3; rep++)
                            {
                                Array.Clear(dst);
                                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                                CK.TransformRgb48le(src, dst, spec, w * h);
                                sw2.Stop();
                                slowMs = Math.Min(slowMs, sw2.Elapsed.TotalMilliseconds);
                            }
                            double slow = (long)w * h / 1e6 / Math.Max(1e-6, slowMs / 1000.0);
                            if (dop == 1) singleSlow = slow; else bestSlow = slow;
                        }
                        Console.WriteLine($"  {name,-24} dop={dop,-2} {ms,8:F1}ms  {mpxs,6:F1} MPix/s  {mbs,6:F0} MB/s(读+写)");
                        if (dop == 1) single = mpxs;
                        best = Math.Max(best, mpxs);
                    }
                    Console.WriteLine($"    参考（解析式单核）= {singleSlow:F1} MPix/s  快路径增益 = {single / Math.Max(1e-6, singleSlow):F1}×");
                    if (!isRelease) continue;
                    // 门槛（Release、表驱动快路径）：单核 ≥ 40 MPix/s（全尺寸都门禁）；
                    // 扩展比 ≥4× **只在 ≥8MPix（工作集 ≥50MB、带宽受限）的尺寸上作门禁**：
                    //   小帧（6MB）单核已接近内存带宽上限（实测 157.8 MPix/s ≈ 6.3 GB/s），此时“扩展比”衡量的是
                    //   调度开销而非并行能力（实测 3.4×/6.4× 飘手），作门禁无意义 → 只记录。
                    Check(single >= 40, $"{name} 单核 ≥40MPix/s（实 {single:F1}）");
                    bool bandwidthBound = (long)w * h >= 8_000_000;
                    if (bandwidthBound)
                        Check(best / Math.Max(1e-6, single) >= 4.0, $"{name} 多核扩展比 ≥4×（实 {best / Math.Max(1e-6, single):F1}×）");
                    else
                        Console.WriteLine($"    (小帧扩展比 {best / Math.Max(1e-6, single):F1}× 仅记录：单核已近带宽上限，不作门禁)");
                }
            }

            // 端到端：引擎（H1，解→变换→编）vs 传统 zscale（H2，单进程）
            BenchEndToEnd(isRelease);
        }

        /// <summary>端到端吞吐对比：同一张 2K 图，PNG→PNG，各自跑 3 次取最好。</summary>

        private static void ProbePeak(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: ServiceProbe peak <file> [expectMetaPeak] [userPeakNits]");
                Harness.FailOnly();
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

        /// <summary>
        /// psnr &lt;图A&gt; &lt;图B&gt; [阈dB] —— 两条执行路径产物是否一致的通用度量。
        /// 统一解到 rgb48 再比（消除 pix_fmt/位深差异带来的假差异），峰值按**实际满量程** 65535 算。
        /// </summary>

        private static async System.Threading.Tasks.Task ProbePeakMeasure(string[] args)
        {
            if (args.Length < 3) { Console.WriteLine("usage: ServiceProbe peakmeasure <png> <maxCode 0..65535> [userPeakNits]"); Harness.FailOnly(); return; }
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

        /// <summary>
        /// decision —— 步骤 3（单一裁决点）的验收探针：把 <c>EffectiveOutputColorTokens</c> 当作“宣称值”，
        /// 与 <c>BuildArguments</c> **实际发出的命令行**逐项对（取 -i 之后的输出侧标签）。
        /// 不一致就是“同一问题两处答案不同”的现行证据，不拿推理交差。
        /// 网格：{png,jpg,webp,tiff} × {auto,sRGB,Display P3,BT.2020,BT.2020 PQ} × {SDR,P3,HDR} 源。
        /// </summary>

        private static async Task ProbePsnr(string[] args)
        {
            if (args.Length < 3) { Console.WriteLine("usage: ServiceProbe psnr <a> <b> [thresholdDb]"); Harness.FailOnly(); return; }
            double thr = args.Length >= 4 && double.TryParse(args[3], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 55;
            var (wa, ha) = await FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(args[1]).ConfigureAwait(false);
            var (wb, hb) = await FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(args[2]).ConfigureAwait(false);
            Check(wa > 0 && wa == wb && ha == hb, $"两图尺寸相同且可探测（{wa}x{ha} vs {wb}x{hb}）");
            if (wa <= 0 || wa != wb || ha != hb) return;
            var a = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(args[1], wa, ha).ConfigureAwait(false);
            var b = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(args[2], wb, hb).ConfigureAwait(false);
            double psnr = CM.Psnr16(a, b);
            int maxDiff = 0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i + 1 < n; i += 2)
                maxDiff = Math.Max(maxDiff, Math.Abs((a[i] | (a[i + 1] << 8)) - (b[i] | (b[i + 1] << 8))));
            Console.WriteLine($"psnr={psnr:F2}dB maxDiff={maxDiff}/65535 尺寸={wa}x{ha}");
            Check(psnr >= thr, $"两路产物 PSNR ≥ {thr}dB（实 {psnr:F2}）");
        }

        /// <summary>
        /// simdswitch —— 面板「SIMD 优化」(AppSettings.AutoUseSimdBinaries) 的**消费点**行为锁
        /// （2026-09-27 新增；编排与结构锁见 tests/scripts/verify-simd-switch-fallback.ps1）。
        ///
        /// 立锁动机：这个开关此前**只有写入端**（复选框 → Save() → settings.json），全仓
        /// **没有任何代码读它决定走不走 SIMD**，而 zh/en 两条 tooltip 都在承诺「关闭后逐像素回退」
        /// ⇒ 声明与实现冲突。实现落在 SimdKernelRouting（判据只此一份），本 mode 锁它**真的被消费**。
        ///
        /// 判据形状（全程进程内、确定性合成像素，不需要素材 / 外部 exe / 特定硬件）：
        ///   S0 本进程**从配置里**拿到的开关值 → 选路（配置→内核这一跳；编排脚本再用
        ///      FFMPEGGUI_AUTO_SIMD=false/true 各跑一次，交叉判定这两行确实跟着配置走）；
        ///   S1 选路本身：关 ⇒ 恒 scalar（与本机 ISA 无关）；开 ⇒ 探针自带的那份 ISA 优先级读数；
        ///   S2/S3 同一份输入、开关开/关两次：8-bit SSE 逐位相等，且回退那条与**探针自带的第三份
        ///         独立标量实现**逐字节相等（⇒ 「标量分支偷偷仍走 SIMD / 错位」都会红）；
        ///   S4 有牙自证：把 b 整体错位 1 字节 ⇒ SSE 与 PSNR 必须**跳出** S2/S3 的容差，
        ///      否则那两条只是在量「两边都是同一个错值」，容差本身没有牙；
        ///   S5 下限断言：输入必须真的有差异（全等帧 ⇒ 任何内核都返回 0 ⇒ 空断言恒绿通道）；
        ///   S6 SimdPixelOps 那一路消费者（tooltip 说的是「SIMD 加速的像素处理」，不止 PSNR）。
        ///
        /// ⚠ 正确性不依赖硬件：只有 S0a 那一臂需要「本机至少 SSE2」（win-x64 恒成立），
        ///   判不了时走 Skip() 点名，不静默消失。AVX-512/AVX2 只影响**性能读数**，不影响任何判据。
        /// </summary>

        private static async System.Threading.Tasks.Task ProbeXcheck(string[] args)
        {
            if (args.Length < 6)
            {
                Console.WriteLine("usage: ServiceProbe xcheck <input> <srcPrim> <srcTrc> <dstPrim> <dstTrc> [outDir]");
                Harness.FailOnly(); return;
            }
            string input = args[1];
            var (w, h) = await FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(input).ConfigureAwait(false);
            if (w <= 0) { Check(false, "无法探测尺寸"); return; }
            var outDir = args.Length >= 7 ? args[6] : Path.Combine(Path.GetTempPath(), "cmsx");
            Directory.CreateDirectory(outDir);
            var spec = Spec(args[2], args[3], args[4], args[5]);

            // A) 引擎路径
            var raw = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                .DecodeToRgb48Async(input, w, h).ConfigureAwait(false);
            var raw2 = new byte[raw.Length];
            CK.TransformRgb48le(raw, raw2, spec, w * h, CK.BuildTables(spec.SrcCurve), CK.BuildTables(spec.DstCurve));
            string enginePng = Path.Combine(outDir, "engine.png");
            await FfmpegGui.Services.ColorMapping.RawColorPipeline.WriteRgb48ToPngAsync(
                raw2, w, h, enginePng, args[4], args[5], null).ConfigureAwait(false);

            // B) 参照路径（zimg 自己完成同样的 primaries/曲线转换）
            //    ⚠️ 必须像生产代码那样在 **-i 之前**声明输入色彩（-color_primaries/-color_trc）：
            //    源文件无 CICP 时（lavfi 产出的 PNG 为 gbr/unknown/unknown），只写 zscale 的 tin=
            //    会被当 unknown 处理（实测参照几乎不变，差仅 244/65535），导致“参照失效”而误判引擎错。
            string mTok = args[4] == "bt2020" ? "bt2020nc" : "bt709";
            string zimgPng = Path.Combine(outDir, "zimg.png");
            var ff = AppSettingsService.Current.FfmpegPath;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ff,
                Arguments = $"-y -hide_banner -loglevel error -color_primaries {args[2]} -color_trc {args[3]} -i \"{input}\" "
                  + $"-vf \"format=gbrpf32le,zscale=pin={args[2]}:tin={args[3]}:min=gbr:rin=pc:p={args[4]}:t={args[5]}:m={mTok}:r=pc,format=rgb48le\" \"{zimgPng}\"",
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (p == null) { Check(false, "无法启动 ffmpeg"); return; }
                string err = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await p.WaitForExitAsync().ConfigureAwait(false);
                if (p.ExitCode != 0) { Check(false, $"zscale 参照路径失败: {err.Trim()}"); return; }
            }

            // C) 比对：两路均写成 16bit PNG → 各自解回 rgb48 再比
            var a = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(enginePng, w, h).ConfigureAwait(false);
            var b = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(zimgPng, w, h).ConfigureAwait(false);
            double psnr = CM.Psnr16(a, b);
            int maxDiff = 0, deSamples = 0; double worstDeltaE = 0, sumDeltaE = 0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i + 6 <= n; i += 6)
            {
                int r1 = a[i] | (a[i + 1] << 8), g1 = a[i + 2] | (a[i + 3] << 8), b1 = a[i + 4] | (a[i + 5] << 8);
                int r2 = b[i] | (b[i + 1] << 8), g2 = b[i + 2] | (b[i + 3] << 8), b2 = b[i + 4] | (b[i + 5] << 8);
                maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(r1 - r2), Math.Max(Math.Abs(g1 - g2), Math.Abs(b1 - b2))));
                if ((i / 6) % 53 == 0)   // 抽样算 ΔE（8bit 口径，与业界一致）
                {
                    double de = CM.DeltaE76((byte)(r1 >> 8), (byte)(g1 >> 8), (byte)(b1 >> 8),
                                             (byte)(r2 >> 8), (byte)(g2 >> 8), (byte)(b2 >> 8));
                    worstDeltaE = Math.Max(worstDeltaE, de); sumDeltaE += de; deSamples++;
                }
            }
            Console.WriteLine($"xcheck {Path.GetFileName(input)} {args[2]}/{args[3]}→{args[4]}/{args[5]}  {w}x{h}");
            Console.WriteLine($"  PSNR(16bit)={psnr:F2}dB  maxDiff={maxDiff}/65535  ΔE max={worstDeltaE:F3} avg={(deSamples > 0 ? sumDeltaE / deSamples : 0):F4} (样本 {deSamples})");
            Check(psnr >= 55, $"引擎 vs zimg PSNR ≥ 55dB（实 {psnr:F1}）");
            Check(maxDiff <= 1500, $"最大单通道差异 ≤ 1500/65535（实 {maxDiff}）");
            Check(worstDeltaE <= 1.5, $"峰值 ΔE*ab ≤ 1.5（实 {worstDeltaE:F3}）");
        }

        /// <summary>行主序 3x3 乘法（仅探针内部用于互逆自洽校验）。</summary>

        private static void ProbeSelfTest()
        {
            int w = 128, h = 128;
            var src = SyntheticFrame16(w, h);
            var cases = new (string name, string sp, string st, string dp, string dt)[]
            {
                ("sRGB→P3", "bt709", "iec61966-2-1", "smpte432", "iec61966-2-1"),
                ("P3→sRGB", "smpte432", "iec61966-2-1", "bt709", "iec61966-2-1"),
                ("sRGB→BT.2020", "bt709", "iec61966-2-1", "bt2020", "bt709"),
                ("BT.2020→sRGB", "bt2020", "bt709", "bt709", "iec61966-2-1"),
                ("sRGB 自身曲线往返", "bt709", "iec61966-2-1", "bt709", "iec61966-2-1"),
            };
            foreach (var (name, sp, st, dp, dt) in cases)
            {
                var spec = Spec(sp, st, dp, dt);
                var dst = new byte[src.Length];
                var dstFast = new byte[src.Length];
                var tt = (CK.BuildTables(spec.SrcCurve), CK.BuildTables(spec.DstCurve));
                CK.TransformRgb48le(src, dst, spec, w * h);
                CK.TransformRgb48le(src, dstFast, spec, w * h, tt.Item1, tt.Item2);   // 表驱动快路径
                
                // 0) 快路径 == 解析式参考路径（生产用表，验证必须覆盖表）
                //    ⚠️ 必须按 16-bit 样本比对：逐字节比会把 0x0100 vs 0x00FF（仅差 1 LSB）算成“差 255”。
                int lsb = 0, worstOff = -1;
                for (int i = 0; i + 2 <= dst.Length; i += 2)
                {
                    int dv = Math.Abs((dst[i] | (dst[i + 1] << 8)) - (dstFast[i] | (dstFast[i + 1] << 8)));
                    if (dv > lsb) { lsb = dv; worstOff = i; }
                }
                if (lsb > 1 && worstOff >= 0)
                {
                    int px = worstOff / 6 * 6;   // 同像素起点
                    Console.WriteLine($"    最差样本 byteOff={worstOff} 解析={(dst[worstOff] | (dst[worstOff + 1] << 8))} " +
                        $"表={(dstFast[worstOff] | (dstFast[worstOff + 1] << 8))} " +
                        $"输入像素=({src[px] | (src[px + 1] << 8)},{src[px + 2] | (src[px + 3] << 8)},{src[px + 4] | (src[px + 5] << 8)}) " +
                        $"输出像素 解析=({dst[px] | (dst[px + 1] << 8)},{dst[px + 2] | (dst[px + 3] << 8)},{dst[px + 4] | (dst[px + 5] << 8)}) " +
                        $"表=({dstFast[px] | (dstFast[px + 1] << 8)},{dstFast[px + 2] | (dstFast[px + 3] << 8)},{dstFast[px + 4] | (dstFast[px + 5] << 8)}) " +
                        $"src={spec.SrcCurve.Name} dst={spec.DstCurve.Name}");
                }
                // 容差按目标曲线自身性质定：BT.709/240M 的 toe 是幂段切线（ITU 系数取整后留有
                // ~21 LSB 台阶，lcms2/zimg 同样存在），拐点附近选分支不同可差至 24 LSB；其余曲线应 ≤1。
                int tolLsb = spec.DstCurve.HasItuRoundingKink ? 24 : 1;
                Check(lsb <= tolLsb, $"{name}: 表驱动与解析式一致（max {lsb}/65535，容差 {tolLsb}"
                    + (spec.DstCurve.HasItuRoundingKink ? "，ITU 取整拐点台阶" : "") + "）");

                // 0b) **8-bit 出口**也要表/解析双路径互验 —— 实际教训：EncodeTo8 的解析式分支
                //     漏乘 65535（把 0..1 当 16-bit 码值），整幅输出变近黑，而此缺口只在无表路径出现
                //     （小帧 <256k 像素就是不建表），16-bit 出口的上面那条断言根本看不到。
                {
                    var d8a = new byte[w * h * 3];
                    var d8t = new byte[w * h * 3];
                    CK.TransformRgb48leToRgb8(src, d8a, spec, w * h, null, null, w, 0);
                    CK.TransformRgb48leToRgb8(src, d8t, spec, w * h, tt.Item1, tt.Item2, w, 0);
                    int mx = 0, worst8 = -1;
                    for (int i = 0; i < d8a.Length; i++)
                    { int dd = Math.Abs(d8a[i] - d8t[i]); if (dd > mx) { mx = dd; worst8 = i; } }
                    // 均值也要报：只查“两路一致”会漏掉“两路一起错”（黑图两边都黑照样通过）
                    double meanA = 0, meanT = 0;
                    foreach (byte by in d8a) meanA += by;
                    foreach (byte by in d8t) meanT += by;
                    meanA /= Math.Max(1, d8a.Length); meanT /= Math.Max(1, d8t.Length);
                    Console.WriteLine($"    rgb8 出口：最大差异 {mx}/255（idx={worst8}）解析均值={meanA:F1} 表均值={meanT:F1}");
                    Check(mx <= (spec.DstCurve.HasItuRoundingKink ? 2 : 1),
                        $"{name}: rgb8 出口 表驱动与解析式一致（max {mx}/255）");
                    Check(meanA > 8 && meanT > 8, $"{name}: rgb8 出口亮度合理（均值 {meanA:F1}/{meanT:F1}，非全黑）");
                }

                // 0c) **8-bit 源降位必须恒等**（任务 #12；16 个抖动相位全扫，两条出口路径都扫）。
                //     依据：8-bit 源经 `format=rgb48le` 升位后码值恒为 **257 的整数倍** ⇒ 16→8 是可精确还原的，
                //     抖动在这种值上只会注入误差。旧抖动用**未居中**的 Bayer（减 2/16 而非 7.5/16）
                //     ⇒ t ∈ [−2/16, +13/16]，16 个相位里 6 个 t ≥ 0.5 ⇒ 精确值被抬 +1（纯色/渐变上长出斑）。
                //     居中后 |t| ≤ 0.46875 ⇒ `Round(v + t) == v` 对 16 个相位都成立 —— 这条断言就是钉这个的。
                //     ⚠ 只在"恒等变换"那一格判：其余 4 格做了真实色彩映射，改值是本分。
                if (name == "sRGB 自身曲线往返")
                {
                    var id48 = new byte[256 * 3 * 2];
                    var want = new byte[256 * 3];
                    for (int v = 0; v < 256; v++)
                        for (int c = 0; c < 3; c++)
                        {
                            int o = (v * 3 + c) * 2;
                            id48[o] = (byte)((v * 257) & 0xFF);
                            id48[o + 1] = (byte)((v * 257) >> 8);
                            want[v * 3 + c] = (byte)v;
                        }
                    int worstDev = 0, worstPh = -1, badPhases = 0, worstAt = -1;
                    for (int ph = 0; ph < 16; ph++)
                    {
                        for (int pass = 0; pass < 2; pass++)   // 0=解析式 1=表驱动（两条路都得恒等）
                        {
                            var out8 = new byte[256 * 3];
                            if (pass == 0) CK.TransformRgb48leToRgb8(id48, out8, spec, 256, null, null, 256, ph);
                            else CK.TransformRgb48leToRgb8(id48, out8, spec, 256, tt.Item1, tt.Item2, 256, ph);
                            int dev = 0, at = -1;
                            for (int i = 0; i < out8.Length; i++)
                            {
                                int dd = Math.Abs(out8[i] - want[i]);
                                if (dd > dev) { dev = dd; at = i; }
                            }
                            if (dev > 0) badPhases++;
                            if (dev > worstDev) { worstDev = dev; worstPh = ph; worstAt = at; }
                        }
                    }
                    Console.WriteLine($"    8-bit 源降位恒等：最大偏差 {worstDev}/255"
                        + (worstAt >= 0 ? $"（相位 {worstPh}，码值 {worstAt / 3}）" : "")
                        + $"，不恒等的相位×路径组合 {badPhases}/32");
                    Check(worstDev == 0 && badPhases == 0,
                        $"8-bit 源降位恒等（16 相位 × 解析/表两条路径；最大偏差 {worstDev}/255"
                        + (worstDev > 0 ? $"，出现在相位 {worstPh} 的码值 {worstAt / 3}" : "") + "）");
                    // ⚠ 对照组：这条断言必须**能红**。零偏差既可能是"真恒等"，也可能是"我根本没在比东西"
                    //   （want 构错、循环空转都会给出 0）。⇒ 同一份产物与 `want+1` 比，偏差必须恰为 1，
                    //   以此证明比较链路是活的；这一条红了说明判据本身失效，而不是产品的降位坏了。
                    {
                        var out8c = new byte[256 * 3];
                        CK.TransformRgb48leToRgb8(id48, out8c, spec, 256, null, null, 256, 0);
                        int ctlDev = int.MaxValue;
                        for (int i = 0; i < out8c.Length; i++)
                        {
                            if (want[i] >= 255) continue;   // 255+1 越界：钳位后会造出假 0，必须跳过顶格
                            int dd = Math.Abs(out8c[i] - (want[i] + 1));
                            if (dd < ctlDev) ctlDev = dd;
                        }
                        // 夹具/产物都必须**非常数**：want 若退化成全 0，"逐字节相等"与"什么都没比"长得一样。
                        int distinctWant = (int)want.Distinct().Count();
                        int distinctOut = (int)out8c.Distinct().Count();
                        Check(distinctWant >= 250 && distinctOut >= 250,
                            $"对照组：夹具与产物都必须是满值域斜坡（want 去重 {distinctWant}/256，产物 {distinctOut}/256）"
                            + " —— 常数夹具会让「恒等」与「没在比」长得一样");
                        // 用 min 而非 max：`want+1` 与产物的**最小**可能偏差必须是 1（产物恒等 ⇒ 全体差 1；
                        // 若这里读到 0，说明产物里存在某个码值恰好等于 want+1 ⇒ 恒等判据本身就不该报绿）。
                        Check(ctlDev == 1, $"对照组：与 want+1 比的最小偏差应为 1（实得 {ctlDev}）—— 读到 0 说明恒等判据没在比东西");
                    }
                }

                // 1) 快内核 == 参考实现（同一数学的两个入口不能分叉）
                double worst = 0; int checkedCount = 0;
                for (int i = 0; i < w * h; i += 7)
                {
                    int o = i * 6;
                    var (rr, gg, bb) = CK.TransformPixelReference(
                        (src[o] | (src[o + 1] << 8)) / 65535f,
                        (src[o + 2] | (src[o + 3] << 8)) / 65535f,
                        (src[o + 4] | (src[o + 5] << 8)) / 65535f, spec);
                    worst = Math.Max(worst, Math.Abs(rr * 65535 - (dst[o] | (dst[o + 1] << 8))));
                    worst = Math.Max(worst, Math.Abs(gg * 65535 - (dst[o + 2] | (dst[o + 3] << 8))));
                    worst = Math.Max(worst, Math.Abs(bb * 65535 - (dst[o + 4] | (dst[o + 5] << 8))));
                    checkedCount++;
                }
                Check(worst <= 1.5, $"{name}: 融合内核与参考实现一致（max {worst:F2}/65535，样本 {checkedCount}）");

                // 2) 中性保持：灰阶斜坡映射后 R=G=B（白点守恒的直接体现）
                int off = (h / 8) * w * 6;
                int bad = 0;
                for (int x = 0; x < w; x++)
                {
                    int o = off + x * 6;
                    int r = dst[o] | (dst[o + 1] << 8), g = dst[o + 2] | (dst[o + 3] << 8), b = dst[o + 4] | (dst[o + 5] << 8);
                    if (Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(g - b), Math.Abs(r - b))) > 2) bad++;
                }
                Check(bad == 0, $"{name}: 灰阶保持中性（异常 {bad}/{w}）");

                // 3) 单调性：斜坡不得出现反向跳变
                int rev = 0;
                for (int x = 1; x < w; x++)
                {
                    int a = off + (x - 1) * 6, bq = off + x * 6;
                    if ((dst[bq] | (dst[bq + 1] << 8)) < (dst[a] | (dst[a + 1] << 8))) rev++;
                }
                Check(rev == 0, $"{name}: 斜坡单调（反向 {rev}）");
            }

            // 4) 往返：sRGB→P3→sRGB 应基本恒等
            var a1 = new byte[src.Length]; var a2 = new byte[src.Length];
            CK.TransformRgb48le(src, a1, Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1"), w * h);
            CK.TransformRgb48le(a1, a2, Spec("smpte432", "iec61966-2-1", "bt709", "iec61966-2-1"), w * h);
            double psnr = CM.Psnr16(src, a2);
            Console.WriteLine($"  sRGB→P3→sRGB 往返 PSNR(16bit) = {psnr:F1} dB");
            Check(psnr >= 80, $"往返恒等（PSNR ≥ 80dB）");

            // 5) 恒等跳过：IsIdentity 必须为 true（避免无谓全帧重编码）
            Check(Spec("bt709", "iec61966-2-1", "bt709", "iec61966-2-1", matrix: false).IsIdentity, "同空间 Spec.IsIdentity=true");

            // 6) 8-bit 表路径：同空间往返必须字节不变
            var lut = TC.Srgb.BuildEotfTable8();
            var s8 = new byte[] { 0, 32, 64, 255, 128, 12, 200, 255, 1, 2, 3, 255 };
            var d8 = new byte[12];
            var enc = CK.BuildEncodeLut16(TC.Srgb, true);
            CK.TransformRgba8(s8, d8, Spec("bt709", "iec61966-2-1", "bt709", "iec61966-2-1", matrix: false), lut, w, h, enc);
            bool same = true;
            for (int i = 0; i < 12; i++) same &= s8[i] == d8[i];
            Check(same, "8bit 表路径：同空间往返字节不变");

            // 7) 调度器规模分档：小图单线程、中图整帧并行、大图流式（不新增并发限制，仅选策略）
            var sc1 = FfmpegGui.Services.ColorMapping.ColorScheduler.ForItem(2, 512, 512, 6);
            var sc2 = FfmpegGui.Services.ColorMapping.ColorScheduler.ForItem(2, 8192, 4096, 6);
            var scM = FfmpegGui.Services.ColorMapping.ColorScheduler.ForItem(2, 4096, 2160, 6);
            Check(sc1.Tier == "S" && sc1.Dop == 1, $"小图走 S 档单线程（实为 {sc1.Tier}/dop{sc1.Dop}）");
            Check(scM.Tier == "M" && !scM.Streaming, $"4K 走 M 档整帧并行（实为 {scM.Tier} stream={scM.Streaming}）");
            Check(sc2.Streaming && sc2.TileRows >= 1, $"8K 走 L 档流式（{sc2.ToLogFragment()}）");

            // ── 托盘/窗口图标资源（2026-10-02 的真实缺陷：托盘只有菜单没有图标）──
            // 成因链是**静默**的：`icon.ico` 没被记进 `<AvaloniaResource>` ⇒ `AssetLoader.Exists` 恒 false
            // ⇒ `App.LoadTrayIcon()` 返回 null ⇒ `TrayIcon.Icon` 空。而且旧那份文件其实是 32×32 的 PNG
            // 改了扩展名（既进不了 `ApplicationIcon`，也没有多档尺寸）。⇒ 两级各钉一条：
            //   ① 程序集里那份 avares 清单确实收录了它（不依赖 Avalonia 运行时，纯反射读清单字节）；
            //   ② `AssetLoader` 这条**产品真正走的路**能打开并解码。
            {
                var asm = typeof(FfmpegGui.App).Assembly;
                using (var ms = asm.GetManifestResourceStream("!AvaloniaResources"))
                {
                    var manifest = ms == null ? Array.Empty<byte>() : new BinaryReader(ms).ReadBytes((int)ms.Length);
                    Check(System.Text.Encoding.UTF8.GetString(manifest).Contains("Resources/icon_raw.png"),
                         $"① avares 清单收录 Resources/icon_raw.png（清单 {manifest.Length} B；缺失 ⇒ csproj 的 AvaloniaResource 又漏了）");
                }
                try
                {
                    var uri = new Uri("avares://FfmpegGui/Resources/icon_raw.png");
                    if (!Avalonia.Platform.AssetLoader.Exists(uri))
                    {
                        Check(false, "② AssetLoader.Exists 认得这条 avares URI");
                    }
                    else
                    {
                        using var s = Avalonia.Platform.AssetLoader.Open(uri);
                        using var bmp = new Avalonia.Media.Imaging.Bitmap(s);
                        Check(bmp.PixelSize.Width >= 16 && bmp.PixelSize.Height >= 16,
                             $"② 托盘图标可解码（实得 {bmp.PixelSize.Width}x{bmp.PixelSize.Height}）");
                    }
                }
                catch (Exception ex)
                {
                    // 本宿主没起 Avalonia 运行时 ⇒ 这条判不了，**点名 SKIP**，不许当成通过
                    Skip($"② AssetLoader 解码路径在本宿主判不了：{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// bench —— 性能基线：内核吐量（含 DOP 扩展性）+ 端到端 H1 vs H2(zscale) 对比。
        /// 数据写入控制台，由 run-color-engine-verification.ps1 归档到 docs。
        /// </summary>

        private static void ProbeToolDetect(string[] args)
        {
            int budgetSec = 120;
            if (args.Length > 1 && int.TryParse(args[1], out var bs) && bs > 0) budgetSec = bs;

            int Handles()
            {
                try
                {
                    using var self = System.Diagnostics.Process.GetCurrentProcess();
                    self.Refresh();
                    return self.HandleCount;
                }
                catch { return -1; }
            }

            var tmp = Path.Combine(Path.GetTempPath(),
                "tooldetect_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            int h0 = Handles();
            try
            {
                // ── ① 读数：每个扩展搜索根的递归扫描耗时 + 句柄增量 ──
                var roots = FfmpegGui.Services.ExternalToolsDetector.GetExtendedSearchPaths();
                Console.WriteLine($"[读数] 扩展搜索根 {roots.Count} 个，起始句柄 {h0}");
                // 看门狗：单根扫描若**永不返回**，只靠"下一行为什么没出现"归因太弱 ⇒ 每 5s 打一次句柄。
                bool watching = true;
                var wd = new System.Threading.Thread(() =>
                {
                    while (watching)
                    {
                        System.Threading.Thread.Sleep(5000);
                        if (!watching) break;
                        Console.WriteLine($"[watch] 句柄 {Handles()}");
                        Console.Out.Flush();
                    }
                }) { IsBackground = true };
                wd.Start();
                double spent = 0; int scanned = 0, skipped = 0;
                foreach (var r in roots)
                {
                    if (spent > budgetSec) { skipped++; continue; }
                    int hb = Handles();
                    // ⚠ **扫之前**先点名并 flush：卡在某个根上时，这条读数才能归因（否则输出停在半截说不清是谁）
                    Console.WriteLine($"[读数] → 扫描 {r}");
                    Console.Out.Flush();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var found = FfmpegGui.Services.PlatformServices.FindToolInDirectory(
                        r, FfmpegGui.Services.PlatformServices.Djxl,
                        FfmpegGui.Services.PlatformServices.DjxlSearchWildcard);
                    sw.Stop();
                    int ha = Handles();
                    spent += sw.Elapsed.TotalSeconds; scanned++;
                    Console.WriteLine($"[读数]   {sw.Elapsed.TotalSeconds,6:F1}s 句柄 {hb}->{ha} (Δ{ha - hb,6})  " +
                                      $"{r}  命中={(found ?? "(无)")}");
                }
                watching = false;
                if (skipped > 0) Console.WriteLine($"[读数] 超出预算 {budgetSec}s ⇒ {skipped} 个根未扫");
                int h1 = Handles();
                Console.WriteLine($"[读数] 合计 {spent:F1}s（扫 {scanned}/{roots.Count} 根），句柄 {h0}->{h1} (Δ{h1 - h0})");
                Console.Out.Flush();

                // ── ①b 反挂死锁：拿"用户配置文件根目录"当唯一根找一个必然不存在的工具 ──
                //   这是实测到的真实形态：本机进程 PATH 里出现过裸 `C:\Users\20210`，而旧的
                //   `SearchOption.AllDirectories` 递归在这种树上**永不返回**（60 s 时句柄已 9 万）。
                //   预算常量不凭记忆写：断言的是「不超过单根预算 + 5s 抖动」。
                if (OperatingSystem.IsWindows())
                {
                    var prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var swP = System.Diagnostics.Stopwatch.StartNew();
                    var ghost = FfmpegGui.Services.PlatformServices.FindToolInDirectory(
                        prof, "zz_no_such_tool_xyz.exe", "*zz_no_such_tool_xyz*.exe");
                    swP.Stop();
                    int hp = Handles();
                    //   ⚠ 上界**不得**只取产品常量：否则实现里把 `PerRootScanBudgetSec` 调大，本条判据
                    //     会跟着自动放宽 ⇒ 等于没有判据。⇒ 先锁常量的合理区间，再用它推上界。
                    int budgetConst = FfmpegGui.Services.ExternalToolsDetector.PerRootScanBudgetSec;
                    Check(budgetConst >= 5 && budgetConst <= 30,
                        $"①e 单根预算常量必须落在 [5,30]s（实得 {budgetConst}s；实测合法根 `C:\\Program Files` 全树 3.0s（`_probe-tool-detect.out:6`））");
                    Console.WriteLine($"[读数] 病态根 {prof} 单根扫描 {swP.Elapsed.TotalSeconds:F1}s 命中={(ghost ?? "(无)")} 句柄 {hp}");
                    Console.Out.Flush();
                    Check(ghost == null && hp >= 0 && swP.Elapsed.TotalSeconds <= budgetConst + 5,
                        $"①b 病态根必须在预算内返回 null（实得 {swP.Elapsed.TotalSeconds:F1}s / 上限 " +
                        $"{budgetConst + 5}s / 命中 {(ghost ?? "(null)")} / 句柄读数 {(hp < 0 ? "(取不到⇒判红)" : hp.ToString())}）");
                    // ⚠ 泄漏的判据是**斜率**不是绝对值：绝对阈值抓不住"每根几十、累计上千"的慢泄漏
                    //   （实测合法实现两次读数在 -2 ~ +35 之间摆动，而回归态是 9 万）。
                    int hb2 = Handles();
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    var ghost2 = FfmpegGui.Services.PlatformServices.FindToolInDirectory(
                        prof, "zz_no_such_tool_xyz.exe", "*zz_no_such_tool_xyz*.exe");
                    sw2.Stop();
                    int ha2 = Handles();
                    Check(ghost2 == null && hb2 >= 0 && ha2 >= 0 && (ha2 - hb2) <= 200,
                        $"①c 同一病态根**连扫两次**，第二次的句柄增量必须≈0（泄漏随次数线性涨；" +
                        $"Δ {ha2 - hb2} ≤ 200，第二次耗时 {sw2.Elapsed.TotalSeconds:F1}s）");
                }
                else Skip("①b/①c/①e 依赖 Windows 的『PATH 含大树』形态 ⇒ 本机不判（不是通过）");

                // ── ①f 反挂死判据**自己**要有牙（2026-09-22 变异实测的直接产物）──
                //   拿真实用户目录当"病态根"时，去掉 `EnumerateFilesSafe` 的 reparse 跳过 ⇒ ①b/①c
                //   **一条都不红**：真实目录本来就是靠 15 s 预算收口的，而 ①b 的上界 = 预算 + 5，
                //   结构上任何"重解析放大"都超不过 20 s ⇒ 判据形同虚设（成因=针无牙，不是没触发）。
                //   ⇒ 判据要咬得住，得自己造一个**必然成环**的根：`looproot\loop -> looproot`（自环 junction）。
                //     · 实现正确（跳过 reparse）⇒ 永不进入 loop ⇒ 亚秒级返回 null
                //     · 实现被改坏 ⇒ 无限下降直到撞满预算 ⇒ 本条转红（阈值 5 s ≪ 单根预算 15 s）
                //   ⚠ 收尾顺序是硬要求：**先单独摘掉 junction**（`Delete(link,false)` 只摘链接），
                //     再做递归清理 —— 反过来会让"递归删除跟着链接走进自己"（本仓已登记过 junction 删除坑）。
                if (OperatingSystem.IsWindows())
                {
                    var loopRoot = Path.Combine(tmp, "looproot");
                    var loopLink = Path.Combine(loopRoot, "loop");
                    Directory.CreateDirectory(loopRoot);
                    File.WriteAllText(Path.Combine(loopRoot, "loop_fixture.txt"), "fixture");
                    bool made = false;
                    try
                    {
                        var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c mklink /J \"{loopLink}\" \"{loopRoot}\"",
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        });
                        if (mk != null) { mk.WaitForExit(5000); }
                        made = Directory.Exists(loopLink);
                    }
                    catch (Exception ex) { Console.WriteLine($"[读数] mklink 异常：{ex.GetType().Name}"); }
                    Console.WriteLine($"[读数] 自环 junction 根造好={made}（{loopRoot}）");
                    if (!made) Skip("①f 本机造不出 junction（权限/被拦）⇒ 本条不判，不是通过");
                    else
                    {
                        // ⚠⚠ 判据**不能是耗时**：2026-09-23 变异实测 —— 去掉 reparse 跳过后，自环 walk
                        //   不是撞满 15 s 预算，而是在 ~12 层 `\loop` 后被 **MAX_PATH** 截停，全程 0.2 s
                        //   ⇒ 任何"≤ N 秒"型阈值都咬不住这条变异（`①b/①c 无牙`的翻版）。
                        //   ⇒ 换成**与时间无关**的判据：同一个标记文件必须**只被看见一次**。
                        //     跳过 reparse ⇒ 1 次；走进自环 ⇒ 每层都撞见一次 ⇒ 计数 >1 ⇒ 红。
                        var swL = System.Diagnostics.Stopwatch.StartNew();
                        var seen = FfmpegGui.Services.ExternalToolsDetector.EnumerateFilesSafe(loopRoot, "loop_fixture.txt");
                        var loopHit = FfmpegGui.Services.PlatformServices.FindToolInDirectory(
                            loopRoot, "zz_no_such_tool_xyz.exe", "*zz_no_such_tool_xyz*.exe");
                        swL.Stop();
                        // 负控/对照：同样计数法用在**没有环**的根上必须也是 1 ⇒ 证明红来自"成环"，不是计数口径写错
                        var plainRoot = Path.Combine(tmp, "plainroot");
                        Directory.CreateDirectory(plainRoot);
                        File.WriteAllText(Path.Combine(plainRoot, "loop_fixture.txt"), "fixture");
                        int plainSeen = FfmpegGui.Services.ExternalToolsDetector.EnumerateFilesSafe(plainRoot, "loop_fixture.txt").Count;
                        try { Directory.Delete(loopLink, false); } catch { }
                        bool linkGone = !Directory.Exists(loopLink);
                        Check(linkGone, "①f-清理 junction 必须能单独摘掉（摘不掉 ⇒ 立刻判红，绝不留给递归清理）");
                        Check(plainSeen == 1,
                            $"①f-对照 无环根的同名文件必须恰好被看见 1 次（实得 {plainSeen}）⇒ 下一条的计数口径本身可信");
                        Check(seen.Count == 1 && loopHit == null,
                            $"①f 自环根里的标记文件**只能被看见一次**（实得 {seen.Count} 次 / {swL.Elapsed.TotalSeconds:F1}s；" +
                            $"跳过 reparse 才做得到，>1 = 走进了环 ⇒ 与耗时无关，MAX_PATH 截停也照样红）");
                    }
                }
                else Skip("①f 的自环 junction 只在 Windows 上可造 ⇒ 本机不判，不是通过");

                // ── ② 诚实性：已证实**无法启动**的候选不得被报成可用工具 ──
                //   形态 (a)：一个后缀 .exe 但不是有效 PE 的文件（Prefetch 的 *.pf 之外的另一类"看着像"）。
                var junk = Path.Combine(tmp, "djxl_junk.exe");
                File.WriteAllText(junk, "this is not a portable executable");
                //   对照组（**负断言必须带一条两边都有的对照串**，否则"返回 null"可能只是因为根本没找到）：
                //   真能起来的 djxl 复制成一个不同名文件。
                //   ⚠ "文件存在" ≠ "起得来" ⇒ 每个候选都要**实跑一次**再认。2026-09-25 两种失效都踩过：
                //     · 只判 File.Exists 就用仓内 `tools/src/libjxl/build/tools/Release/djxl.exe` —— 它依赖
                //       `build/lib/Release/*.dll`（实测单独拷走 ⇒ 加载器退出 127、缺 `jxl_cms.dll`）
                //       ⇒ ②b/⑤/⑥ 一起变成 **5 条假红**（对照组自己起不来，负断言全部失去方向）。
                //     · 只认 `PlanFolderPath/jxl/bin/djxl.exe` —— 本机 PlanFolderPath 解析成 `C:\PLAN` 而那里没有
                //       jxl ⇒ ② 判红、⑤/⑥ 连带 SKIP（**覆盖丢失**，SKIP 不是通过）。
                //   ⇒ 候选按序：产品解析出的 PLAN 发布版 → 仓内 `publish/PLAN/jxl/bin/djxl.exe`
                //     （实测自带依赖、单独拷到 temp 仍 `--version` exit=0）。全部实跑失败 ⇒ 判红并列出试过什么。
                string LaunchableCtl(string exe)   // 自己启动来判，不走被测的 ChooseBestExecutable（否则是构造恒等）
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(exe, "--version")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var pp = System.Diagnostics.Process.Start(psi);
                        if (pp == null) return "Process.Start 返回 null";
                        var tOut = pp.StandardOutput.ReadToEndAsync();     // ⚠ 先挂读取、再等退出（本仓范式）
                        var tErr = pp.StandardError.ReadToEndAsync();
                        if (!pp.WaitForExit(8000)) { try { pp.Kill(entireProcessTree: true); } catch { } return "超时(8s)"; }
                        string o = tOut.GetAwaiter().GetResult() + tErr.GetAwaiter().GetResult();
                        return (pp.ExitCode == 0 || !string.IsNullOrWhiteSpace(o)) ? "ok" : $"exit={pp.ExitCode} 无输出";
                    }
                    catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
                }
                var ctlCands = new List<string>();
                void AddCtl(string p)
                {
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p) && !ctlCands.Contains(p)) ctlCands.Add(p);
                }
                AddCtl(Path.Combine(FfmpegGui.Services.PlatformServices.PlanFolderPath ?? "", "jxl", "bin", "djxl.exe"));
                for (var dp = new DirectoryInfo(AppContext.BaseDirectory); dp != null; dp = dp.Parent)
                    AddCtl(Path.Combine(dp.FullName, "publish", "PLAN", "jxl", "bin", "djxl.exe"));
                var real = Path.Combine(tmp, "djxl_real.exe");
                var realSrc = "";
                var ctlLog = new StringBuilder();
                foreach (var cd in ctlCands)
                {
                    try { File.Copy(cd, real, true); }
                    catch (Exception ex) { ctlLog.Append($" [{cd}] 拷贝失败 {ex.GetType().Name}"); continue; }
                    var vv = LaunchableCtl(real);
                    if (vv == "ok") { realSrc = cd; break; }
                    ctlLog.Append($" [{cd}] {vv}");
                }
                bool haveReal = realSrc.Length > 0;
                // ⚠ 对照组缺失**必须判红**（旧写法只 `Console.WriteLine("FAIL …")` ⇒ 不进 `_fail`、
                //   汇总仍是 `fail=0` ⇒ ②b/②c 整段蒸发而门禁照绿 = 假绿通道）。
                if (!haveReal)
                    Check(false, "② 对照组缺失：" + (ctlCands.Count == 0
                            ? "一个候选都没凑出来（PLAN 与仓内 publish 都没有 djxl.exe）"
                            : ctlLog.ToString().Trim())
                        + " ⇒ ②b/②c 无从判（绝不静默跳过）");

                var junkOnly = FfmpegGui.Services.ExternalToolsDetector
                    .ChooseBestExecutable(new[] { junk });
                Check(junkOnly == null,
                    $"②a 只有「起不来的 .exe」时返回 null（实得 {(junkOnly ?? "(null)")}）——不得回退成假 ✅");
                if (haveReal)
                {
                    var realOnly = FfmpegGui.Services.ExternalToolsDetector
                        .ChooseBestExecutable(new[] { real });
                    Check(string.Equals(realOnly, real, StringComparison.OrdinalIgnoreCase),
                        $"②b 对照组：真能起来的 exe 必须被选中（实得 {(realOnly ?? "(null)")}）");
                    var mixed = FfmpegGui.Services.ExternalToolsDetector
                        .ChooseBestExecutable(new[] { junk, real });
                    Check(string.Equals(mixed, real, StringComparison.OrdinalIgnoreCase),
                        "②c 真假混合：坏候选被跳过、选中好的（顺序把坏的放前面）");
                }

                //   形态 (b)：Prefetch 里的 `EXIFTOOL.EXE-<hash>.pf` —— 只有**无扩展名约束**的通配会命中它。
                var pfDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
                string? pfHit = null;
                try
                {
                    using var en = Directory.EnumerateFiles(pfDir, "*exiftool.exe*").GetEnumerator();
                    while (en.MoveNext()) { pfHit = en.Current; break; }
                }
                catch (Exception ex) { Console.WriteLine($"[读数] Prefetch 枚举不可用：{ex.GetType().Name}"); }
                Console.WriteLine($"[读数] `*exiftool.exe*` 在 Prefetch 命中: {(pfHit ?? "(本机无命中)")}");
                if (pfHit != null)
                {
                    var pf = FfmpegGui.Services.ExternalToolsDetector.ChooseBestExecutable(new[] { pfHit });
                    Check(pf == null, $"②d Prefetch 的 .pf 不得被当作工具（实得 {(pf ?? "(null)")}）");
                }
                // ⚠ 本机 Prefetch 读不到（非提权令牌的 DACL 只给 BA）或确实没有命中 ⇒ **点名 SKIP**，
                //   不许让这条"因为条件不满足而没跑"变成汇总里的一个绿点。
                else Skip("②d Prefetch 形态本机不可判（无命中或目录不可枚举）⇒ 不是通过");

                // ── ③ 可注入性：降级路径的门禁必须能**确定地**关掉系统级搜索根 ──
                if (OperatingSystem.IsWindows())
                {
                    var emptyA = Path.Combine(tmp, "rootA");
                    Directory.CreateDirectory(emptyA);
                    var saved = Environment.GetEnvironmentVariable("FFMPEGGUI_EXT_SEARCH_DIRS");
                    try
                    {
                        Environment.SetEnvironmentVariable("FFMPEGGUI_EXT_SEARCH_DIRS", emptyA);
                        var rs = FfmpegGui.Services.ExternalToolsDetector.GetExtendedSearchPaths();
                        Check(rs.Count == 1 && string.Equals(rs[0], emptyA, StringComparison.OrdinalIgnoreCase),
                            $"③ FFMPEGGUI_EXT_SEARCH_DIRS 覆盖系统根（实得 [{string.Join(" | ", rs)}]）");
                        Environment.SetEnvironmentVariable("FFMPEGGUI_EXT_SEARCH_DIRS", emptyA + ";");
                        var rs2 = FfmpegGui.Services.ExternalToolsDetector.GetExtendedSearchPaths();
                        Check(rs2.Count == 1 && string.Equals(rs2[0], emptyA, StringComparison.OrdinalIgnoreCase),
                            $"③b 末尾空段不得被当成「整个 PATH」（实得 [{string.Join(" | ", rs2)}]）");
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("FFMPEGGUI_EXT_SEARCH_DIRS", saved);
                    }
                    Console.WriteLine($"[读数] 撤掉覆盖后根数回到 {FfmpegGui.Services.ExternalToolsDetector.GetExtendedSearchPaths().Count}");
                }
                else Skip("③/③b 的可注入收口只在 Windows 上有意义（非 Windows 本就不做系统递归搜索）⇒ 不是通过");

                // ── ⑤ 系统 PATH 这一级必须真的存在且真的非递归 ──
                //   2026-09-22 实测：`DjxlService.Detect` 的 `// ⑤ PATH` **只有注释没有代码**，
                //   而 `GetExtendedSearchPaths` 排除 `C:\Windows*` 的理由正是"⑤ 会兜住 PATH 工具"
                //   ⇒ 前提不成立 ⇒ 放在 System32 的 djxl 永远发现不了（且只表现为静默换通路）。
                //   判据成对：**原语行为**（放得进 PATH 就找得到 / 拿掉就找不到）+ **接线源码锁**
                //   （`DjxlService` 真的调它 —— 光有原语不挡住"忘了接线"，那正是本次的缺陷形态）。
                if (OperatingSystem.IsWindows() && haveReal)
                {
                    var pathDir = Path.Combine(tmp, "pathroot");
                    Directory.CreateDirectory(pathDir);
                    File.Copy(realSrc, Path.Combine(pathDir, "djxl.exe"), true);
                    var savedPath = Environment.GetEnvironmentVariable("PATH");
                    bool foundInPath = false; string? foundAt = null;
                    try
                    {
                        Environment.SetEnvironmentVariable("PATH", pathDir + ";" + savedPath);
                        foundInPath = FfmpegGui.Services.PlatformServices.TryFindInPath(
                            FfmpegGui.Services.PlatformServices.Djxl, out foundAt);
                    }
                    finally { Environment.SetEnvironmentVariable("PATH", savedPath); }
                    Check(foundInPath && !string.IsNullOrEmpty(foundAt)
                          && Path.GetDirectoryName(foundAt)!.Equals(pathDir, StringComparison.OrdinalIgnoreCase),
                        $"⑤a 原语：只在 PATH 里的 djxl 必须被找到（实得 found={foundInPath} path={foundAt ?? "(null)"}）");
                    // 负控：同一原语在**没**注入 PATH 时必须找不到（否则 ⑤a 是"总能找到"的真空绿）
                    bool stillFound = FfmpegGui.Services.PlatformServices.TryFindInPath(
                        "zz_no_such_tool_in_path.exe", out var stillAt);
                    Check(!stillFound && stillAt == null,
                        $"⑤a-负控 不在 PATH 的工具必须找不到（实得 found={stillFound} path={stillAt ?? "(null)"}）");
                    // 源码锁要先定位仓库根（同 `InitExternalTools` 的向上走法；找不到 ⇒ 判红，不静默跳过）
                    string? djsPath = null;
                    for (var d2 = new DirectoryInfo(AppContext.BaseDirectory); d2 != null; d2 = d2.Parent)
                    {
                        var cand = Path.Combine(d2.FullName, "src", "FfmpegGui", "Services", "DjxlService.cs");
                        if (File.Exists(cand)) { djsPath = cand; break; }
                    }
                    Check(djsPath != null, $"⑤b 前置：定位到 DjxlService.cs（实得 {djsPath ?? "(未找到 ⇒ 接线锁无法判)"}）");
                    var djs = djsPath == null ? "" : File.ReadAllText(djsPath);
                    Check(djs.Contains("TryFindInPath(PlatformServices.Djxl"),
                        "⑤b 接线锁：DjxlService 的 ⑤ 真的调用 TryFindInPath（只剩注释/被删即红）");
                    Check(!System.Text.RegularExpressions.Regex.IsMatch(djs, @"//\s*⑤\s*PATH\s*\r?\n\s*\}"),
                        "⑤b2 接线锁：不得再出现「只有 `// ⑤ PATH` 注释、下一行就收尾」的形态");
                }
                else Skip("⑤a/⑤b 需要 Windows + 可执行的 djxl 对照组 ⇒ 本机不判（不是通过）");

                // ── ⑥ SIMD 标签选路：文件名后缀必须按本机 CPU 能力定序，且「标签优先」不得盖过「起不起来」 ──
                //   2026-09-24 补的**零覆盖**：`ChooseBestExecutable` 里这一段（`GetSimdPriorityTags()` → 逐个
                //   tag 用 `name.Contains(tag)` 收拢 → 剩余追加 → `generic` 兜住全部）此前**一条判据都没有** ——
                //   把它整段删掉，①/②/③/⑤ 全照样绿（它们要么只喂单元素候选，要么喂不带标签的文件名）。
                //   ⚠ 判据刻意**不重算产品的排序**（那只会做成"构造恒等"自检）：断言的是外部可观察后果 ——
                //     输入顺序无关、高优先标签必胜、高优先那份起不来时必须落到次高、全带标签且全起不来必须 null。
                //   ⚠ 夹具前提单独断言（不信任自己的构造）：选出的两个标签**互不为子串**，且三个文件名都
                //     不命中比 hi 更早的标签 ⇒ 否则"期望 = hi 那份"根本不可计算（子串歧义会让判据失去方向）。
                if (haveReal)
                {
                    var tags = FfmpegGui.Services.CpuFeatureService.GetSimdPriorityTags();
                    Check(tags.Length > 0 && tags[tags.Length - 1] == "generic",
                        $"⑥a1 标签表非空且末位是 generic（实得 {tags.Length} 项 / 末位 \"{tags[tags.Length - 1]}\"）");
                    bool anyBlank = false;
                    foreach (var t2 in tags) if (string.IsNullOrWhiteSpace(t2)) anyBlank = true;
                    //   匹配判据是 `name.Contains(tag)` ⇒ 一个空/空白标签会让**第一轮**吞掉全部候选并按**逆序**
                    //   返回（`for i = Count-1 downto 0`），选路静默退化成"最后入列者优先"，且不抛错不打日志。
                    Check(!anyBlank, "⑥a2 标签表不得含空/空白项（`Contains(\"\")` 恒真 ⇒ 排序退化为逆序，见上）");
                    string? hi = null, lo = null;
                    for (int a = 0; a < tags.Length && hi == null; a++)
                    {
                        if (tags[a] == "generic") break;
                        for (int b = a + 1; b < tags.Length; b++)
                        {
                            if (tags[b] == "generic") break;
                            if (!tags[a].Contains(tags[b], StringComparison.Ordinal)
                                && !tags[b].Contains(tags[a], StringComparison.Ordinal)) { hi = tags[a]; lo = tags[b]; break; }
                        }
                    }
                    if (hi == null || lo == null)
                    {
                        Skip("⑥b–⑥e 需要标签表里存在**互不为子串**的一对（本机表 = ["
                             + string.Join(", ", tags) + "]）⇒ 造不出无歧义夹具，不判，不是通过");
                    }
                    else
                    {
                        int idxHi = Array.IndexOf(tags, hi);
                        int idxLo = Array.IndexOf(tags, lo);
                        var simdDir = Path.Combine(tmp, "simd");
                        var simdJunk = Path.Combine(tmp, "simdjunk");
                        Directory.CreateDirectory(simdDir);
                        Directory.CreateDirectory(simdJunk);
                        string MkReal(string dir, string tag)
                        {
                            var p = Path.Combine(dir, "zzprobe-" + tag + ".exe");
                            File.Copy(realSrc, p, true);           // 同一份**真起得来**的二进制，只差文件名
                            return p;
                        }
                        var pHi = MkReal(simdDir, hi);
                        var pLo = MkReal(simdDir, lo);
                        var pPlain = Path.Combine(simdDir, "zzprobeplain.exe");
                        File.Copy(realSrc, pPlain, true);
                        var jHi = Path.Combine(simdJunk, "zzprobe-" + hi + ".exe");
                        File.WriteAllText(jHi, "this is not a portable executable");
                        var jLo = Path.Combine(simdJunk, "zzprobe-" + lo + ".exe");
                        File.WriteAllText(jLo, "this is not a portable executable");
                        string N(string? p) { return p == null ? "(null)" : Path.GetFileName(p); }
                        Check(idxHi >= 0 && idxLo > idxHi,
                            $"⑥a3 选出的标签顺序与表内优先级一致（hi={hi}@{idxHi} / lo={lo}@{idxLo}）");
                        bool premiseOk = true;
                        foreach (var nm in new[] { N(pHi), N(pLo), N(pPlain) })
                        {
                            var ln = nm.ToLowerInvariant();
                            for (int k = 0; k < idxHi; k++) if (ln.Contains(tags[k])) premiseOk = false;
                        }
                        foreach (var t2 in tags)
                            if (t2 != "generic" && N(pPlain).ToLowerInvariant().Contains(t2)) premiseOk = false;
                        Check(premiseOk,
                            $"⑥-premise 夹具前提：hi/lo/plain 的文件名都不得命中比 hi 更早的标签，且 plain 不得命中任何实标签" +
                            $"（表=[{string.Join(", ", tags)}]，hi={hi}@{idxHi}）");
                        var r1 = FfmpegGui.Services.ExternalToolsDetector.ChooseBestExecutable(new[] { pLo, pHi, pPlain });
                        var r2 = FfmpegGui.Services.ExternalToolsDetector.ChooseBestExecutable(new[] { pPlain, pHi, pLo });
                        var r3 = FfmpegGui.Services.ExternalToolsDetector.ChooseBestExecutable(new[] { pHi, pPlain, pLo });
                        Check(string.Equals(r1, pHi, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(r2, pHi, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(r3, pHi, StringComparison.OrdinalIgnoreCase),
                            $"⑥b 三种输入顺序都必须选高优先标签那份（实得 {N(r1)} / {N(r2)} / {N(r3)}，期望 {N(pHi)}）");
                        var r4 = FfmpegGui.Services.ExternalToolsDetector.ChooseBestExecutable(new[] { pPlain, pLo });
                        Check(string.Equals(r4, pLo, StringComparison.OrdinalIgnoreCase),
                            $"⑥c 拿掉 hi 时应落到次高标签那份（实得 {N(r4)}，期望 {N(pLo)}）⇒ 起作用的是标签匹配而不是「返回第一个」");
                        var r5 = FfmpegGui.Services.ExternalToolsDetector.ChooseBestExecutable(new[] { jHi, pLo, pPlain });
                        Check(string.Equals(r5, pLo, StringComparison.OrdinalIgnoreCase),
                            $"⑥d hi 位置换成起不来的垃圾 ⇒ 必须落到 lo（实得 {N(r5)}，期望 {N(pLo)}）：标签优先级不得盖过「起不起来」");
                        var r6 = FfmpegGui.Services.ExternalToolsDetector.ChooseBestExecutable(new[] { jHi, jLo });
                        Check(r6 == null,
                            $"⑥e 全是「带特征后缀 + 起不来」的候选 ⇒ 必须 null（实得 {N(r6)}）——末尾兜底不得把带标签的垃圾报成工具");
                        Console.WriteLine($"[读数] ⑥ 夹具 hi={hi} lo={lo} 表=[{string.Join(", ", tags)}] " +
                                          $"CPU={FfmpegGui.Services.CpuFeatureService.Summary()}");
                    }
                }
                else Skip("⑥ 依赖 ② 的真 djxl 对照组来造「都起得来、只差文件名」的三份候选（对照组缺失时 ② 已判红）⇒ 不判");

                // ── ④ 残留自证（精确路径，不用通配） ──
                int del = 0;
                foreach (var f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories)) { File.Delete(f); del++; }
                Directory.Delete(tmp, true);
                Check(!Directory.Exists(tmp), $"④ 运行级目录已清零（删 {del} 个文件）：{tmp}");
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        private static void ProbeI18n()
        {
            var loc = LocalizationService.Instance;

            // (0) 报告实际加载目录与文件时间戳 —— 产物陈旧时这是第一手线索
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            var probeDir = Path.Combine(exeDir, "Resources", "Locales");
            Console.WriteLine($"process-path={Environment.ProcessPath}");
            Console.WriteLine($"locale-dir={probeDir} exists={Directory.Exists(probeDir)}");
            foreach (var lang in new[] { "zh-CN", "en-US" })
            {
                var f = Path.Combine(probeDir, lang + ".json");
                if (File.Exists(f))
                    Console.WriteLine($"  {lang}.json bytes={new FileInfo(f).Length} mtime={File.GetLastWriteTime(f):yyyy-MM-dd HH:mm:ss}");
                else
                    Console.WriteLine($"  {lang}.json 缺失（将走程序集内嵌资源回退）");
            }

            // 代表 key：每个前缀至少 1 条（新增前缀 + 本轮之前就有的前缀都覆盖）
            var reps = new[]
            {
                // ── 本轮才补齐的 key（此前 100% 缺失 ⇒ 天然是「产物陈旧」的探针）──
                "gainmap.title", "priority.normal", "detail.category.camera", "meta.status.restored",
                "preset.dlg.delete", "tip.ps.verify", "dng.comp.jxl", "avif.aq.mode",
                "color.strategy.recommended", "settings.render.vulkan", "jpeg.huffman",
                "nvenc.p1", "qsv.veryslow", "svt.tune.vmaf", "vaapi.4", "amf.balanced",
                "progress.col.status", "filter.btn.ok", "png.pred.paeth", "webp.preset",
                "jxl.effort", "jxr.encoder.title", "jpegli.backend.sjpeg", "tiff.compression",
                "gif.dither", "max.dimension", "threads.auto.hint", "ph.cache.auto",
                "queue.running", "media.loading", "icc.invalid.file", "gpu.advice.switch.cpu",
                "label.ps.verify", "enable.max.dimension",
                // ── 本轮之前就存在的 key：一并锁住，防「只测新增、旧 key 回归无人管」──
                "app.title", "format.jpeg", "icc.mode.carry", "gain.map.quality", "cache.dir.tooltip",
                "preset.manager.title", "detail.technical.info", "progress.status.encoding",
                "format.filter.title", "queue.progress", "rgba.unavailable",
            };
            Console.WriteLine($"reps={reps.Length}");

            // (1)+(2)+(3) 两语言逐条：能加载 / 取到值 / 值 != key / 非空
            var perLang = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var lang in new[] { "zh-CN", "en-US" })
            {
                loc.LoadLocale(lang);
                Check(string.Equals(loc.CurrentLanguage, lang, StringComparison.Ordinal),
                      $"{lang}：LoadLocale 后 CurrentLanguage={loc.CurrentLanguage}");

                var vals = new Dictionary<string, string>(StringComparer.Ordinal);
                var bad = new List<string>();
                foreach (var k in reps)
                {
                    var v = loc[k];
                    vals[k] = v;
                    if (string.IsNullOrWhiteSpace(v) || string.Equals(v, k, StringComparison.Ordinal)) bad.Add(k);
                }
                perLang[lang] = vals;
                Check(bad.Count == 0,
                      $"{lang}：{reps.Length} 条代表 key 全部取到非空值且 != key 本身（异常 {bad.Count} 条{(bad.Count > 0 ? "：" + string.Join(", ", bad) : "")}）");
            }

            // 两套字典**确实不同**：抽 3 条 zh/en 应当不同的 key 逐对比较
            var mustDiffer = new[] { "gainmap.title", "priority.normal", "detail.category.camera" };
            var diff = 0;
            foreach (var k in mustDiffer)
                if (!string.Equals(perLang["zh-CN"][k], perLang["en-US"][k], StringComparison.Ordinal)) diff++;
            Check(diff == mustDiffer.Length,
                  $"zh-CN / en-US 确实是两套不同字典（{mustDiffer.Length} 条抽样中 {diff} 条取值不同）");

            // (4) 未定义 key 的回退契约：必须**返回 key 本身**（LocalizationService.cs:56-58）
            const string undef = "no.such.key.definitely.undefined";
            loc.LoadLocale("zh-CN");
            Check(string.Equals(loc[undef], undef, StringComparison.Ordinal),
                  "未定义 key 在 zh-CN 下回退为 key 本身（锁住索引器契约）");
            loc.LoadLocale("en-US");
            Check(string.Equals(loc[undef], undef, StringComparison.Ordinal),
                  "未定义 key 在 en-US 下同样回退为 key 本身");

            // 复位到默认语言，避免影响同进程内的后续调用
            loc.LoadLocale("zh-CN");
        }

        // ────────────────────────────── ultrahdr ──────────────────────────────

        /// <summary>
        /// ultrahdr —— 隔离「增益图**解码**是否真的生效」，把三段分开观测：
        /// ① 容器检测 ② 元数据读取 ③ 线性 HDR 解码。
        ///
        /// <para>
        /// 存在的理由：`QueueProcessor` 的 Ultra HDR 输入解码路径（`:481-510`）在 headless 下
        /// **不回显** `item.Log` ⇒ 无法靠日志判定它有没有执行。2026-10-02 的产物对拍发现
        /// 「带增益图 vs 剥掉增益图」产物**逐字节相同**（见
        /// `docs/GAINMAP_VENDOR_ANNOTATION_AUDIT_2026-10-02.md` §4）⇒ 必须有直接观测点把成因定位到某一段。
        /// </para>
        /// </summary>

        private static FfmpegGui.Services.ColorMapping.TransformSpec Spec(
            string sp, string st, string dp, string dt, bool matrix = true, bool clamp = true)
        {
            var src = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp(sp, st, sp) ?? throw new Exception($"src {sp}/{st}");
            var dst = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp(dp, dt, dp) ?? throw new Exception($"dst {dp}/{dt}");

            // ⚠️ 亮度域换算必须与生产 `ColorTransformPlan.ToSpec()` **同口径**，否则本探针测的根本
            //    不是真实引擎路径：PQ 的 1.0 = 10000nits、HLG = 1000nits、其余 = 203nits（SDR 参考白）。
            //    旧实现两项都留默认 1 ⇒ PQ→sRGB 不做 49× 换算，与 zimg 参照差出 PSNR 7.3dB，
            //    而那笔账长期被当成「zimg 参照口径分歧（PQ 归一约定不同）」豁免 —— 实为**探针自身的
            //    构造缺陷**（它绕过 ToSpec 手搓 spec），不是引擎的口径分歧。
            double Nits(FfmpegGui.Services.ColorMapping.TransferCurve c)
                => c.Kind switch
                {
                    FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Pq
                        => FfmpegGui.Services.ColorMapping.TransferCurve.PqPeakNits,
                    FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Hlg
                        => FfmpegGui.Services.ColorMapping.Bt2100Ootf.NominalPeakNits,
                    _ => FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits,
                };
            double sdr = FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits;

            // ⚠️ 白名单同步：因本缺陷而被判为「口径分歧」的格子，修复后应从 $knownNonDefects 移除，
            //    否则豁免会把真正的回归也一起免掉（门禁就失去牙齿）。见 verify-color-engine-xcheck.ps1。
            return new FfmpegGui.Services.ColorMapping.TransformSpec
            {
                SrcCurve = src.Curve,
                DstCurve = dst.Curve,
                Matrix = matrix ? src.MatrixTo(dst) : null,
                ClampLinearBeforeEncode = clamp,
                LinearScale = Nits(src.Curve) / sdr,
                EncodeScale = sdr / Nits(dst.Curve),
            };
        }

        /// <summary>合成测试帧（rgb48le）：灰阶斜坡 + 饱和原色 + 斜向渐变（灰阶图测不出色域错）。</summary>

        private static byte[] SyntheticFrame16(int w, int h)
        {
            var b = new byte[w * h * 6];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 6;
                    ushort r, g, bl;
                    int k = x + y;
                    if (y < h / 4) { r = g = bl = (ushort)((k % 4096) * 16); }                     // 灰阶斜坡
                    else if (y < h / 2) { int t = (k % 512) * 128; r = (ushort)Math.Min(t, 65535); g = bl = 0; }  // 红梯
                    else if (y < 3 * h / 4)                                                  // 饱和原色块
                    {
                        int c = (x / Math.Max(1, w / 6)) % 6;
                        r = (ushort)((c is 0 or 3 or 4) ? 65535 : 0);
                        g = (ushort)((c is 1 or 3 or 5) ? 65535 : 0);
                        bl = (ushort)((c is 2 or 4 or 5) ? 65535 : 0);
                    }
                    else { r = (ushort)(x * 65535 / Math.Max(1, w - 1)); g = (ushort)(y * 65535 / Math.Max(1, h - 1)); bl = 32768; }
                    b[o] = (byte)r; b[o + 1] = (byte)(r >> 8);
                    b[o + 2] = (byte)g; b[o + 3] = (byte)(g >> 8);
                    b[o + 4] = (byte)bl; b[o + 5] = (byte)(bl >> 8);
                }
            return b;
        }

        /// <summary>
        /// contract —— 四策略契约自检（规划 §6 C1–C6）：枚举 策略×容器×源 全格，
        /// 断言以 <b>EffectiveStrategy</b> 为准；带 OverrideReason 的覆盖格豁免结局断言（但必须有说明）。
        /// </summary>

        private static void BenchEndToEnd(bool isRelease)
        {
            var ff = AppSettingsService.Current.FfmpegPath;
            if (string.IsNullOrWhiteSpace(ff) || !File.Exists(ff)) { Console.WriteLine("  SKIP 端到端（无 ffmpeg）"); return; }
            var dir = Path.Combine(Path.GetTempPath(), "cmsbench");
            Directory.CreateDirectory(dir);
            string srcPng = Path.Combine(dir, "in.png");
            if (!File.Exists(srcPng) || new FileInfo(srcPng).Length == 0)
            {
                RunCli(ff, $"-y -hide_banner -loglevel error -f lavfi -i smptehdbars=s=2048x1080 -frames:v 1 \"{srcPng}\"");
                if (!File.Exists(srcPng)) { Check(false, "端到端基准图生成失败"); return; }
            }
            var src = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("bt709", "iec61966-2-1", "sRGB");
            var dst = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("smpte432", "iec61966-2-1", "P3");
            if (src == null || dst == null) return;
            var plan = FfmpegGui.Services.ColorMapping.ColorMappingEngine.Plan(src, dst,
                new FfmpegGui.Services.ColorMapping.PlanPolicy
                {
                    Format = FfmpegGui.Services.ColorMapping.ColorMappingEngine.CapsFor("png"),
                    AllowApproximateCurve = false,
                });
            var spec = Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1");

            double h2 = double.MaxValue, h1 = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                RunCli(ff, $"-y -hide_banner -loglevel error -i \"{srcPng}\" -vf \"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709,format=rgb48le\" \"{dir}/h2.png\"");
                sw.Stop(); h2 = Math.Min(h2, sw.Elapsed.TotalMilliseconds);
            }
            for (int i = 0; i < 3; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                    srcPng, Path.Combine(dir, "h1.png"), spec, plan, null, default, 1).GetAwaiter().GetResult();
                sw.Stop(); h1 = Math.Min(h1, sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"  端到端 2K PNG sRGB→P3： H2(zscale 单进程)={h2:F0}ms  H1(引擎 三进程+内存变换)={h1:F0}ms  比值={h1 / h2:F2}×");
            bool h1ok = File.Exists(Path.Combine(dir, "h1.png")) && new FileInfo(Path.Combine(dir, "h1.png")).Length > 0;
            Check(h1ok, "引擎端到端产出有效 PNG");
            if (isRelease) Check(h1 <= h2 * 2.0, $"端到端 H1 ≤ H2×2.0（实 {h1 / h2:F2}×；超出则调代价模型）");
        }

    }
}

