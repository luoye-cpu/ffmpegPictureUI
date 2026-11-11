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
        /// framelen —— <c>GainMapEncoder.EncodeAsync</c> 的**帧长守卫**动态自证（2026-10-21 复核后补）。
        /// <para>为什么必须动态测：那三处守卫要 &gt;5.36 亿像素才触发，**造不出真实夹具**；但入口守卫位于
        ///   任何分配与外部工具检查**之前**，所以「1 个元素的输入 + 声明 65536×65536」就能走到它。</para>
        /// <para>判别器（修复前后真的分叉，不是"看着合理"）：修复前 `int 65536*65536` **回绕成 0**
        ///   ⇒ 守卫不存在，函数继续往下打印参数行并以空缓冲继续跑，日志里既没有「帧长」也不会出现
        ///   4294967296；修复后**立刻**返回 false，且只发一条把 long 真乘积点名的拒绝行。</para>
        /// </summary>
        private static async Task ProbeFrameLengthGuard()
        {
            var logs = new System.Collections.Generic.List<string>();
            bool ok;
            try
            {
                ok = await FfmpegGui.Services.GainMapEncoder.EncodeAsync(
                    new float[1], 65536, 65536,
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "framelen_probe.jpg"),
                    log: s => logs.Add(s));
            }
            catch (Exception ex)
            {
                Check(false, $"P-1 守卫必须早于一切分配 ⇒ 不得抛异常（实抛 {ex.GetType().Name}: {ex.Message}）");
                return;
            }
            string all = string.Concat(logs);
            Check(!ok, "P-1 超大声明帧长 ⇒ 返回 false（不是把回绕后的 0 像素当「空图」继续跑）");
            Check(all.Contains("帧长") && all.Contains("65536x65536"),
                $"P-2 拒绝原因点名尺寸（65536x65536；实发 {logs.Count} 条）");
            Check(all.Contains("4294967296"),
                "P-3 乘积按 **long** 计算（4294967296）；int 回绕成 0 的旧实现永远不会出现这句");
            Check(logs.Count == 1,
                $"P-4 守卫在**第一步**就返回（实发 {logs.Count} 条；先打「参数」行说明没拦住）");
            Console.WriteLine("  FRAMELEN lines=" + logs.Count);
        }

        /// <summary>
        /// bench —— 性能基线：内核吐量（含 DOP 扩展性）+ 端到端 H1 vs H2(zscale) 对比。
        /// 数据写入控制台，由 run-color-engine-verification.ps1 归档到 docs。
        /// </summary>
        /// <summary>
        /// bench 计时档的**测量环境**收口（2026-10-26，账本 §50）：把本进程钉到"快核簇"再计时。
        /// 动因 = 混合架构（P/E 核）下线程可被调度到 E 核，同一二进制的单核吞吐腰斩 ⇒
        /// `sRGB→P3 ≥40`、`8b PQ→sRGB ≥25` 这类**绝对**门槛按调度假红（实测同一份新构建二进制：
        /// 默认调度 35/2、钉快核簇 37/0，同期机器负载 77~82% ⇒ 差的是调度，不是代码）。
        /// ⚠ 阈值一个都不动 —— 这里只让读数可复现，不放宽判据。
        /// ⚠ 不硬编码掩码：核编号顺序随厂商/BIOS 变（Intel 通常 P 在前，AMD 桌面可能全簇同速），
        ///   所以按"逐核实测耗时"找最大相邻比来分簇；分不出簇（单簇机器）就**不钉**并在日志点名。
        /// ⚠ 子进程（ffmpeg/zimg）继承本进程亲和性 ⇒ 端到端 H1/H2 两臂同侧，比值判据不受影响
        ///   （实测钉核后 H1/H2 = 1.81×，门槛 ≤2.0）。
        /// </summary>
        private static class BenchCorePin
        {
            /// <summary>给定即优先采用（<c>0x</c> 前缀按十六进制，否则十进制），用于在特定机器上复现读数。</summary>
            public const string EnvMask = "FFMPEGGUI_PROBE_AFFINITY";
            /// <summary>钉上之后**实际可用**的逻辑线程数；0 = 未钉（沿用全机核数）。
            /// 计时阶梯按它排序，否则 `Environment.ProcessorCount` 仍是开机时读到的全机数
            /// （进程内改亲和性不会让它变小，实测钉 12 线程仍报 20）⇒ 阶梯超订阅。
            /// </summary>
            public static int PinnedCount { get; private set; }

            const double MinGapRatio = 1.25;     // 相邻两档实测耗时之比 ≥ 此值才认"存在快慢簇"
            const int SamplesPerCore = 3;        // 每核取最优：满载机器上单次样本会被抢占污染

            public static string Apply()
            {
                try
                {
                    if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                            System.Runtime.InteropServices.OSPlatform.Windows))
                        return "bench-affinity=未钉（非 Windows）";
                    int n = Environment.ProcessorCount;
                    if (n < 4) return $"bench-affinity=未钉（逻辑核 {n}，分簇无意义）";
                    if (n > 63) return $"bench-affinity=未钉（逻辑核 {n} > 63，位掩码法不适用）";
                    using var proc = System.Diagnostics.Process.GetCurrentProcess();

                    var ov = Environment.GetEnvironmentVariable(EnvMask);
                    if (!string.IsNullOrWhiteSpace(ov))
                    {
                        string t = ov.Trim();
                        long mv;
                        bool parsed = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? long.TryParse(t.Substring(2), System.Globalization.NumberStyles.HexNumber,
                                           System.Globalization.CultureInfo.InvariantCulture, out mv)
                            : long.TryParse(t, out mv);
                        if (!parsed || mv <= 0 || (mv >> n) != 0)
                            return $"bench-affinity=未钉（{EnvMask}=\"{ov}\" 不是逻辑核 {n} 范围内的掩码）";
                        proc.ProcessorAffinity = (IntPtr)mv;
                        PinnedCount = BitCount(mv);
                        return $"bench-affinity=0x{mv:X}（由 {EnvMask} 指定，{BitCount(mv)}/{n} 线程）";
                    }

                    long fullMask = (long)proc.ProcessorAffinity;
                    WarmUp();
                    var ms = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        proc.ProcessorAffinity = (IntPtr)(1L << i);
                        double best = double.MaxValue;
                        for (int s = 0; s < SamplesPerCore; s++)
                        {
                            double one = Chunk();
                            if (one < best) best = one;
                        }
                        ms[i] = best;
                    }
                    proc.ProcessorAffinity = (IntPtr)fullMask;

                    var keys = (double[])ms.Clone();
                    var order = new int[n];
                    for (int i = 0; i < n; i++) order[i] = i;
                    Array.Sort(keys, order);
                    double maxRatio = 0; int cut = -1;
                    for (int k = 1; k < n; k++)
                    {
                        if (keys[k - 1] <= 0) continue;
                        double r = keys[k] / keys[k - 1];
                        if (r > maxRatio) maxRatio = r;
                        // 至少留 2 个快核（超线程成对），且不许切在最后一个核上（那是"只留最快一个"的噪声解）
                        if (r >= MinGapRatio && r > 0 && k >= 2 && k < n && (cut < 0 || r > keys[cut] / keys[cut - 1]))
                            cut = k;
                    }
                    if (cut < 0)
                        return "bench-affinity=未钉（单簇机器：逐核 " +
                               $"{keys[0]:F1}~{keys[n - 1]:F1} ms，最大相邻比 {maxRatio:F2}<{MinGapRatio:F2} ⇒ 无快慢簇可分，按默认调度）";

                    long mask = 0;
                    for (int k = 0; k < cut; k++) mask |= 1L << order[k];
                    proc.ProcessorAffinity = (IntPtr)mask;
                    PinnedCount = cut;
                    var slow = new System.Text.StringBuilder();
                    for (int i = 0; i < n; i++)
                        if (((mask >> i) & 1) == 0) slow.Append(slow.Length == 0 ? "" : ",").Append(i);
                    return $"bench-affinity=0x{mask:X} 钉快核簇 {cut}/{n} 线程" +
                           $"（快核 {keys[0]:F1}~{keys[cut - 1]:F1} ms，慢核 {keys[cut]:F1}~{keys[n - 1]:F1} ms，" +
                           $"分档比 {keys[cut] / keys[cut - 1]:F2}×，未用逻辑核 [{slow}]）";
                }
                catch (Exception ex)
                {
                    return "bench-affinity=未钉（收口失败 ⇒ 退回默认调度，读数含 E 核风险：" +
                           $"{ex.GetType().Name}: {ex.Message}）";
                }
            }

            static int BitCount(long v) { int c = 0; while (v != 0) { c += (int)(v & 1); v >>= 1; } return c; }
            /// <summary>
            /// 机器负载读数（**不参与判据**）：一段窗口内全进程 CPU 增量 ÷（逻辑核数 × 窗口时长）。
            /// 为什么要打在日志里 = 计时档一红，第一个问题永远是"这格真慢"还是"这台机器在忙"；
            /// 本仓旧规矩是"别拿单次 LoadPercentage 当机器空闲"⇒ 那就把负载读数与吞吐数值
            /// **同一趟**留在日志里，让下一手能拿两轮日志对撞归因，而不是事后猜。
            /// </summary>
            public static string LoadLine()
            {
                try
                {
                    int n = Environment.ProcessorCount;
                    long t0 = BusyTicks();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    System.Threading.Thread.Sleep(400);
                    double busyMs = (BusyTicks() - t0) / 10_000.0;      // ticks = 100 ns
                    double sec = Math.Max(0.05, sw.Elapsed.TotalSeconds);
                    double pct = busyMs / 1000.0 / (sec * n) * 100.0;
                    return $"负载≈{pct:F0}%（{sec * 1000:F0} ms 窗口内全进程 CPU 增量 {busyMs:F0} ms ÷ {n} 逻辑核容量；不判据，仅供归因）";
                }
                catch (Exception ex)
                {
                    return $"负载=不可读（{ex.GetType().Name}）";
                }
            }

            static long BusyTicks()
            {
                long t = 0;
                foreach (var pr in System.Diagnostics.Process.GetProcesses())
                {
                    try { t += pr.TotalProcessorTime.Ticks; }
                    catch { }
                    finally { pr.Dispose(); }
                }
                return t;
            }


            /// <summary>预热：先跑两遍让 JIT 完成，避免"第一个被测核背上编译耗时"的系统性偏差。</summary>
            static void WarmUp() { Chunk(); Chunk(); }

            static readonly double[] CalibSink = new double[(1 << 18) >> 3];

            /// <summary>代表性小块：超越函数 + 乘法 + 周期性写内存（与色彩内核同型的"算子 + 带宽"混合负载）。</summary>
            static double Chunk()
            {
                // ⚠ **零分配**：标定用的缓冲区只在静态字段里分配一次。
                //   写成每次 `new double[...]` 会在测量前积下 ~2 MB gen0/LOH 压力，
                //   而下一个分配正是内核的 6 MB rgb48 大件 → GC 挂起落进被测段
                //   （实测首档极差 25.5 ms）⇒ 标定本身成了假红源。
                const int N = 1 << 18;
                var sink = CalibSink;
                double acc = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < N; i++)
                {
                    acc += Math.Sqrt((i & 1023) + 1.0) * 1.0009765625;
                    if ((i & 7) == 0) sink[i >> 3] = acc;
                }
                acc += sink[0];                       // 读回一次，防止整块被当成死码消除
                double ms = sw.Elapsed.TotalMilliseconds;
                if (double.IsNaN(acc)) Console.Error.Write('1');
                return ms;
            }
        }

    }
}
