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
        private static void ProbeBench(string[] args)
        {
            // 绝对门槛只在 Release 下成立（Debug 关掉优化，吞吐会差 5-10 倍，拿它当基线会误判）
            bool isRelease = typeof(Program).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                is System.Reflection.AssemblyConfigurationAttribute[] { Length: > 0 } cfg
                && string.Equals(cfg[0].Configuration, "Release", StringComparison.OrdinalIgnoreCase);
            if (!isRelease)
                Console.WriteLine("⚠ 当前为 Debug 构建：只输出数值，不做绝对吞吐门槛（请用 -c Release 复跑）");

            // ⚠ 计时前先收口测量环境（混合架构下线程落 E 核 ⇒ 同一二进制吞吐腰斩、绝对门槛按调度假红）
            Console.WriteLine("  " + BenchCorePin.Apply() + " ｜ " + BenchCorePin.LoadLine());

            bool big = args.Length >= 2 && args[1] == "big";
            var sizes = big ? new[] { (1024, 1024), (4096, 2160), (8192, 4096) } : new[] { (1024, 1024), (4096, 2160) };
            var namedCases = new (string name, string sp, string st, string dp, string dt)[]
            {
                ("sRGB→P3(矩阵同曲线)", "bt709", "iec61966-2-1", "smpte432", "iec61966-2-1"),
                ("BT.2020→sRGB(含裁切)", "bt2020", "bt709", "bt709", "iec61966-2-1"),
                ("PQ→sRGB(变曲线+裁切)", "bt2020", "smpte2084", "bt709", "iec61966-2-1"),
                // ⚠ 第四格是**低档的活臂**（2026-10-12）：③(a) 把解码侧缩放接进快路径后，上面三格全部
                //   落到"快路径适用 ⇒ ≥40"档 ⇒ ≥3 那一档在 bench 里**没有任何格子经过**，退化成死码，
                //   它对账臂（"关表档增益必须 ≈1×"）也就永远不会红。加一格目标为 PQ 的（EncodeScale≠1
                //   ⇒ 按实测 1457 码的插值误差必须关表）让两档都有活臂、两条对账都有对象。
                ("sRGB→PQ(目标域换算 ⇒ 关表)", "bt709", "iec61966-2-1", "bt2020", "smpte2084"),
            };
            int cores = Environment.ProcessorCount;
            // ⚠ 钉过核 ⇒ 阶梯按**实际可用**线程排（不是全机核数）。超订阅会毒后面的单核计时：
            //   同一掩码 0xFFF 实测「起跑前钉」cores=12 ⇒ 8b PQ→sRGB 单核 38.9，
            //   「起跑后钉、阶梯仍按 20」⇒ 同格只剩 29.0（前一档 dop=20 的线程池自旋线程没散尽）。
            if (BenchCorePin.PinnedCount > 0) cores = Math.Min(cores, BenchCorePin.PinnedCount);
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
                    double singleSpreadMs = 0, singleSlowSpreadMs = 0;
                    foreach (int dop in new[] { 1, Math.Min(2, cores), Math.Min(4, cores), Math.Min(8, cores), cores })
                    {
                        // 每档取 N 次最优（min 耗时）：单样本估扩展比噪声大（实测同一用例 2.9×/3.2×/5.2× 飘）
                        double ms = double.MaxValue, slowMs = double.MaxValue;
                        // 极差（3 次重复的 max-min）。判据**不变**（仍是 min/min 与同一阈值），只是红的时候能当场分清「这格真慢」与「这台机器在忙」——
                        //   full8 那条 1.4×>1.3 就是负载（同 mode 用时 71.6 s vs 平时 57–59 s），当时无从区分（账本原先记成「比值臂还是单样本」是错的：两臂自 `a4c7cfd`(10-07) 起就都是 min-of-3）。
                        double msWorst = 0, slowMsWorst = 0;
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
                            msWorst = Math.Max(msWorst, sw.Elapsed.TotalMilliseconds);
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
                                slowMsWorst = Math.Max(slowMsWorst, sw2.Elapsed.TotalMilliseconds);
                            }
                            double slow = (long)w * h / 1e6 / Math.Max(1e-6, slowMs / 1000.0);
                            if (dop == 1) singleSlow = slow; else bestSlow = slow;
                        }
                        Console.WriteLine($"  {name,-24} dop={dop,-2} {ms,8:F1}ms  {mpxs,6:F1} MPix/s  {mbs,6:F0} MB/s(读+写)");
                        if (dop == 1)
                        {
                            single = mpxs;
                            singleSpreadMs = msWorst - ms;
                            singleSlowSpreadMs = slowMsWorst - slowMs;
                        }
                        best = Math.Max(best, mpxs);
                    }
                    Console.WriteLine($"    参考（解析式单核）= {singleSlow:F1} MPix/s  [3 次极差] 快路径 {singleSpreadMs:F1}ms / 解析式 {singleSlowSpreadMs:F1}ms  快路径增益 = {single / Math.Max(1e-6, singleSlow):F1}×");
                    if (!isRelease) continue;
                    // 门槛**分档**（2026-10-11，L-7：性能账此前无人看守）。阈值由"这一格**该不该**走表快路径"决定。
                    // ⚠ 分档必须取**独立参照**，不许直接调内核那句判据 `CK.TableFastPathOperatorsAllow`：
                    //   内核的 `fast` 现在就走它 ⇒ 判据一旦写坏，分档会跟着一起改口，实测 10× 的吞吐回落
                    //   被"重新分档"吸收成合法低档 ⇒ **无声放过**。这不是假设：第一版就是那么写的，
                    //   变异 M1（判据整支 `return false`）实测 `pass=16 fail=1`，唯一红的是一条无关的端到端项。
                    //   ⇒ 独立口径 = 这一格**声明的色度学**需不需要"查表表达不了的算子"；
                    //     另加一条「独立口径 == 内核判据」的对账断言，M1/M2 两个方向才都会红。
                    //   · 快路径适用 ⇒ 单核 ≥40 MPix/s（实测 sRGB→P3 = 45.0~56.9、BT.2020→sRGB = 57.2~78.5、
                    //     PQ→sRGB = 117.0~154.5 —— 最后一格是 2026-10-12 ③(a) 把解码侧 nits 常数乘接进快路径
                    //     之后从 5.5 涨上来的，多核最高 1099.6 MPix/s）。
                    //     这一档才是"回归看守"：内核判据被改坏、某格悄悄退回解析式（≈5 MPix/s）⇒ 立刻红。
                    //     旧写法拿同一个 40 去套 PQ→sRGB，那格**永远**红（LinearScale=10000/203=49.26，
                    //     查表表达不了 ⇒ 走解析式是**正确行为**），于是整条 bench 被当作"已知假红"没人看。
                    //     ⚠ 40 这一档的实测余量**不大**（分档落地后连跑三轮：sRGB→P3 = 45.0/46.2/46.5，
                    //     BT.2020→sRGB = 57.2/60.0/60.1，PQ 那档 = 5.6~5.8 vs 阈值 3）⇒ 最小项只剩 11%。
                    //     整轮若在负载下假红，正确处置是**静机复跑**（本仓既有规矩"计时必须单机跑、并发期禁测"），
                    //     或加大 best-of-N；**不许**为了凑绿灯把 40 往下调 —— 那正是 L-7 要治的"没人看守"。
                    //   · 快路径被合法关闭 ⇒ 降到解析式水位 ≥3 MPix/s（实测 sRGB→PQ 那格两跑都是 4.2，
                    //     余量 28%）。合法关闭的是四类：目标域换算 EncodeScale≠1（表是线性域均匀采样、
                    //     PQ 编码近黑极陡 ⇒ 插值基不对，实测差 1457 码@16bit / 8-bit 出口 6 码）、
                    //     色调映射、HLG 正/逆 OOTF、色域映射。源侧 nits 缩放**不再**是关闭理由（③(a)）。
                    //   · 反向自证：分档必须与**实测增益**一致。同一格两次计时只差"表给不给"这一个动作，
                    //     所以它们的比值量的就是**内核那句判据**：判为适用却测出 ≈1× ⇒ 快路径没生效；
                    //     判为关闭却测出 >1.3× ⇒ 有算子被跳过、或分档落后于内核。两边都会红。
                    //   · 扩展比 ≥4× 仍**只在 ≥8MPix（工作集 ≥50MB、带宽受限）的尺寸上作门禁**：
                    //     小帧（6MB）单核已近内存带宽上限（实测 157.8 MPix/s ≈ 6.3 GB/s），此时"扩展比"
                    //     量的是调度开销而非并行能力（实测 3.4×/6.4× 飘手），作门禁无意义 → 只记录。
                    // 独立口径：曲线的归一域峰值（PQ=10000nits、HLG=1000nits、其余=SDR 参考白 203nits）。
                    // 换算与生产 `ColorTransformPlan.ToSpec()` 同口径，但**在探针里独立写一份**——
                    // 它是内核判据的对照臂，不是它的副本。
                    // 2026-10-12（性能提案 (a)）规则收窄到**目标侧**：源侧的 nits 换算已经是一个常数乘
                    //   （快循环里做，与解析式同运算）⇒ **不再**构成关表理由；
                    //   目标侧仍构成，因为 `CurveTables.Oetf` 是线性域均匀采样、PQ 编码曲线近黑极陡
                    //   ⇒ 插值基不对（实测最大差 1457 码@16bit、8-bit 出口 6 码，见 contract 的 C20 族注释）。
                    static double NitsOf(TC c) => c.Kind switch
                    {
                        TC.CurveKind.Pq => TC.PqPeakNits,
                        TC.CurveKind.Hlg => FfmpegGui.Services.ColorMapping.Bt2100Ootf.NominalPeakNits,
                        _ => TC.SdrWhiteNits,
                    };
                    bool fastCell = Math.Abs(NitsOf(spec.DstCurve) - TC.SdrWhiteNits) < 1e-9
                                    && (spec.Tone == null || !spec.Tone.Active)
                                    && !spec.SrcIsHlgScene && !spec.DstIsHlgScene && !spec.GamutMap;
                    //   ⚠ GMO 这一项取**声明位** `GamutMap` 而非 `GamutMapActive`：后者还要求目标亮度系数齐
                    //   （`DstLuminances is { Length: 3 }`）。若某格声明了 GMO 而系数没给全 ⇒ 内核会照走快路径、
                    //   本侧判它"该关表"⇒ 对账红 —— 这正是要点名的「声明了却没生效」那一类，不该被静默放过。
                    // 对账（牙齿所在）：独立分档与内核判据不许各说各话。
                    //   内核把该走表的格关了 ⇒ 吞吐掉到解析式水位，本条与下面的 ≥40 档一起红（变异 M1）。
                    //   内核把不该走表的格放开了 ⇒ 本条与"关闭档增益必须 ≈1×"一起红，且像素侧由 C20 逐字节锁兜住（变异 M2）。
                    bool kernelFast = CK.TableFastPathOperatorsAllow(spec);
                    Check(kernelFast == fastCell,
                        $"{name} 快路径对账：独立分档={fastCell} ／ 内核判据={kernelFast} ⇒ 两者必须一致（不一致 = 判据被改坏或本门禁口径落后）");
                    double floorMpix = fastCell ? 40.0 : 3.0;
                    string tier = fastCell ? "快路径适用" : "快路径被合法关闭(解析式)";
                    Check(single >= floorMpix, $"{name} 单核 ≥{floorMpix:F0}MPix/s（{tier}，实 {single:F1}）");
                    double gainCell = single / Math.Max(1e-6, singleSlow);
                    // ── 停顿探测器（2026-10-22）──────────────────────────────────────────────
                    //   起因：整轮与单跑三次红在**三个不同格子**（1.3× / 2.9MPix / 23.3MPix），而本探针
                    //   **自己就打印了打断证据**——那一格的 `[3 次极差] 快路径 3372.9ms`（同格最优 2862.7ms），
                    //   极差/最优 ≈ 1.18 ⇒ 至少一次重复被系统停顿打断了两倍时长。比值取的是两条 best-of-3
                    //   之比，重复被打断时它就是噪声，量的不再是内核判据。
                    //   ⚠ 只**赦红不赦绿**（第一版写成了"停顿即 Skip"，当场被实测否证：一台忙碌机器上它把
                    //     一条 12.5× ≪ 阈值 2.0 的**明明通过**的格子也降成 SKIP ⇒ 正信号被丢掉）。
                    //     现在：先照常算判决，只有"本会判红"且"极差/最优 > 0.5"时才改记未判定，
                    //     且必须点名两条极差/两条最优，读数可复核；绝对地板与快路径对账档不依赖重复间
                    //     稳定性，一律照常判。**不放宽任何判据、不下调任何阈值**（那是 L-7 要治的"没人看守"）。
                    double bestMsFast = (long)w * h / 1e6 / Math.Max(1e-9, single) * 1000.0;
                    double bestMsSlow = (long)w * h / 1e6 / Math.Max(1e-9, singleSlow) * 1000.0;
                    double stallRatio = Math.Max(singleSpreadMs / Math.Max(1e-9, bestMsFast),
                                                 singleSlowSpreadMs / Math.Max(1e-9, bestMsSlow));
                    // ⚠ 关表档的比值必须**交错成对采样**（2026-10-22 实测后改）：原来拿两条各自
                    //   best-of-3 的循环相除 ⇒ 负载下带来 30% 量级的系统性偏置（那一轮快路径极差只有
                    //   49.7ms/450ms = 11%，比值却从真值 1.0 漂到 1.3 ⇒ 判据假红，而"极差/最优"
                    //   根本捕不到这种**两臂间的漂移**）。成对交替（给表→不给表 各一次）取中位数
                    //   能把同机漂移抵消。适用档余量是 10× 量级，仍用原 best-of-3（不必多花计时）。
                    double gainCell2 = gainCell;
                    if (!fastCell)
                    {
                        var pr = new double[3];
                        var prRaw = new double[3];
                        for (int k = 0; k < 3; k++)
                        {
                            Array.Clear(dst);
                            var swPf = System.Diagnostics.Stopwatch.StartNew();
                            CK.TransformRgb48le(new ReadOnlySpan<byte>(src), new Span<byte>(dst), spec, w * h, ttS, ttD);
                            swPf.Stop();
                            Array.Clear(dst);
                            var swPs = System.Diagnostics.Stopwatch.StartNew();
                            CK.TransformRgb48le(src, dst, spec, w * h);
                            swPs.Stop();
                            double rk = swPs.Elapsed.TotalMilliseconds / Math.Max(1e-9, swPf.Elapsed.TotalMilliseconds);
                            pr[k] = rk; prRaw[k] = rk;
                        }
                        Array.Sort(pr);
                        gainCell2 = pr[1];
                        Console.WriteLine($"    关表档交错复测 3 对（逐对 {prRaw[0]:F2}/{prRaw[1]:F2}/{prRaw[2]:F2}×）中位 = {gainCell2:F2}×"
                            + $"（两条独立 best-of-3 相除给的是 {gainCell:F2}× ⇒ 差 {Math.Abs(gainCell2 - gainCell):F2}）");
                    }
                    bool gainVerdictFinal = fastCell ? (gainCell >= 2.0) : (gainCell2 <= 1.3);
                    if (!gainVerdictFinal && stallRatio > 0.5)
                        Skip($"{name} 增益比值档本轮**未判定**：极差/最优={stallRatio:F2}（快路径 {singleSpreadMs:F1}ms/{bestMsFast:F1}ms、"
                             + $"解析式 {singleSlowSpreadMs:F1}ms/{bestMsSlow:F1}ms）⇒ 重复被系统停顿打断，请静机复跑");
                    else if (fastCell)
                        Check(gainCell >= 2.0, $"{name} 分档=快路径适用 ⇒ 实测增益必须 ≥2×（实 {gainCell:F1}×；≈1× 说明快路径其实没开，判据与分档不符）");
                    else
                        Check(gainCell2 <= 1.3, $"{name} 分档=快路径关闭 ⇒ 实测增益必须 ≈1×（交错中位 {gainCell2:F1}×；>1.3 说明有算子被跳过或分档落后于内核）");
                    bool bandwidthBound = (long)w * h >= 8_000_000;
                    if (bandwidthBound)
                        Check(best / Math.Max(1e-6, single) >= 4.0, $"{name} 多核扩展比 ≥4×（实 {best / Math.Max(1e-6, single):F1}×）");
                    else
                        Console.WriteLine($"    (小帧扩展比 {best / Math.Max(1e-6, single):F1}× 仅记录：单核已近带宽上限，不作门禁)");
                }
            }

            // ── 8b：8-bit **交付出口**的吞吐档（2026-10-13，补 (a) 欠账"只量了等价性、没量吞吐"）──
            // 为什么必须单独有一档：上面那 4 格量的是 `TransformRgb48le`（16-bit 出口），
            //   而 jpg/webp/gif 与 `--bit-depth 8` 的 png 走的是**另一个内核** `TransformRgb48leToRgb8`。
            //   本档一接上就抓到一个真缺陷：`EncodeTo8` 无条件先算 `curve.FromLinear`（幂函数）再在
            //   有表时扔掉 ⇒ 每像素白算三次，8-bit 出口实测只有 22.7 MPix/s（同 spec 的 16-bit 出口 135）。
            //   修后复测 64.0（PQ→sRGB）/ 45.8（sRGB→P3）。阈值就钉在**修前值之上**：
            //   floor=25 ⇒ 白算那颗 pow 若被加回来（16.4/22.7）当场红；同时**不贴**在分布边缘
            //   （修后最小项 45.8 = 阈值的 1.8 倍，与既有 ≥40 档同量级余量）。
            // ⚠ 与 16-bit 档同一纪律：分档取**独立口径**（这里用声明的色度学），并另加一条
            //   「实测增益 ≥2×」的对账臂 —— 只钉绝对值的话，机器整体变慢会假红，而"快路径没开"
            //   反而可能被一起放宽掉；比值那条量的才是内核判据本身。
            {
                var (w8b, h8b) = (1920, 1080);
                var src8b = SyntheticFrame16(w8b, h8b);
                var dst8b = new byte[w8b * h8b * 3];
                Console.WriteLine($"\n--- 8-bit 交付出口 {w8b}x{h8b}（rgb48 入 → rgb8 出）---");
                // ⚠ 标签里的 `{name}` 会被 #75 归一成同一个槽位 ⇒ 两格的同类断言压成同一个 key，
                //   所以登记写的是"3 个模板 × 每模板 2 次"（Labels=3 / Times=6），不是"6 个不同标签"。
                var cells8b = new (string name, string sp, string st, string dp, string dt)[] {
                    ("PQ→sRGB（HDR 源走主力 8-bit 交付）", "bt2020", "smpte2084", "bt709", "iec61966-2-1"),
                    ("sRGB→P3（纯 SDR 快路径）", "bt709", "iec61966-2-1", "smpte432", "iec61966-2-1"),
                };
                int seen8b = 0;
                foreach (var (name, sp, st, dp, dt) in cells8b)
                {
                    var spec8b = Spec(sp, st, dp, dt);
                    var tS8b = CK.BuildTables(spec8b.SrcCurve);
                    var tD8b = CK.BuildTables(spec8b.DstCurve);
                    int n8b = w8b * h8b;
                    double msFast = double.MaxValue, msSlow = double.MaxValue;
                    for (int rep = 0; rep < 3; rep++)
                    {
                        Array.Clear(dst8b);
                        var swA = System.Diagnostics.Stopwatch.StartNew();
                        CK.TransformRgb48leToRgb8(src8b, dst8b, spec8b, n8b, tS8b, tD8b, w8b, 0);
                        swA.Stop();
                        msFast = Math.Min(msFast, swA.Elapsed.TotalMilliseconds);
                    }
                    for (int rep = 0; rep < 3; rep++)
                    {
                        Array.Clear(dst8b);
                        var swB = System.Diagnostics.Stopwatch.StartNew();
                        CK.TransformRgb48leToRgb8(src8b, dst8b, spec8b, n8b, null, null, w8b, 0);
                        swB.Stop();
                        msSlow = Math.Min(msSlow, swB.Elapsed.TotalMilliseconds);
                    }
                    double mpFast = n8b / 1e6 / Math.Max(1e-9, msFast / 1000.0);
                    double mpSlow = n8b / 1e6 / Math.Max(1e-9, msSlow / 1000.0);
                    bool allow8b = CK.TableFastPathOperatorsAllowRgb8(spec8b);
                    seen8b++;
                    Console.WriteLine($"  {name,-42} 表={mpFast:F1} 解析={mpSlow:F1} MPix/s 增益={mpFast / Math.Max(1e-9, mpSlow):F1}× 判据={allow8b}");
                    if (!isRelease) continue;
                    Check(allow8b, $"8b {name} 前置：这一格**该**走表路径（内核 8-bit 判据={allow8b}）——否则下面的增益档无从判定");
                    Check(mpFast >= 25.0, $"8b {name} 单核 ≥25MPix/s（实 {mpFast:F1}；修前实测 16.4~22.7，白算的 FromLinear 一回来就红）");
                    Check(mpFast / Math.Max(1e-9, mpSlow) >= 2.0,
                        $"8b {name} 表 vs 解析增益 ≥2×（实 {mpFast / Math.Max(1e-9, mpSlow):F1}×；≈1× 说明表路径实际没生效）");
                }
                // ⚠ 这一条是**运行时**格子数锁：#75 那把尺量的是**源文本**，循环体只有一份源文本 ⇒
                //   两格跑同一份 Check 源码，删掉 `cells8b` 里的一格，#75 的标签条数**不会变**。
                //   所以"每一档都还在"必须由这里钉（跑过几格 == 声明几格），而不是指望标签计数。
                Check(seen8b == cells8b.Length && seen8b == 2,
                    $"8b 档位数量：声明 {cells8b.Length} 格、实跑 {seen8b} 格（须 2）——少一格就是某条交付路口的看守消失");
            }

            // 端到端：引擎（H1，解→变换→编）vs 传统 zscale（H2，单进程）
            BenchEndToEnd(isRelease);
        }

        /// <summary>端到端吞吐对比：同一张 2K 图，PNG→PNG，各自跑 3 次取最好。</summary>
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

            // 两条臂**交错**取样（每轮先 H2 再 H1），各取 min。旧写法是「3 次 H2 跑完再 3 次 H1」⇒
            // 任何单向漂移（源图磁盘缓存热起来、CPU 频率爬升、后台活动进来）都**整笔落到一条臂**上，
            // 而比值是两条臂相除 ⇒ 漂移不会被抵消，会被当成代价比放大。
            //   实测（同一台机器、同一构建，只改取样形状）：分段跑 5 次 = 1.68/1.88/1.96/1.97/2.00，
            //   另有两次带并发活动 = 2.09/2.53；**交错 5 轮**后 = 1.83/1.87/1.89/1.90/1.90 ⇒ 散布由
            //   0.85× 收到 0.07×，判据 `H1 ≤ H2×2.0` 不再贴在分布边缘（余量 ~5%）。
            //   ⇒ 阈值**没动**。剩下的事实是：H1 引擎端到端 ≈ zscale 单进程的 1.9 倍（三进程 + 整帧内存变换），
            //   真要往下压走性能清单 S0/表折叠那条，别动这条判据（`超出则调代价模型` 原话即此意）。
            double h2 = double.MaxValue, h1 = double.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                RunCli(ff, $"-y -hide_banner -loglevel error -i \"{srcPng}\" -vf \"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709,format=rgb48le\" \"{dir}/h2.png\"");
                sw2.Stop(); h2 = Math.Min(h2, sw2.Elapsed.TotalMilliseconds);

                var sw1 = System.Diagnostics.Stopwatch.StartNew();
                FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                    srcPng, Path.Combine(dir, "h1.png"), spec, plan, null, default, 1).GetAwaiter().GetResult();
                sw1.Stop(); h1 = Math.Min(h1, sw1.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"  端到端 2K PNG sRGB→P3： H2(zscale 单进程)={h2:F0}ms  H1(引擎 三进程+内存变换)={h1:F0}ms  比值={h1 / h2:F2}×");
            bool h1ok = File.Exists(Path.Combine(dir, "h1.png")) && new FileInfo(Path.Combine(dir, "h1.png")).Length > 0;
            Check(h1ok, "引擎端到端产出有效 PNG");
            if (isRelease) Check(h1 <= h2 * 2.0, $"端到端 H1 ≤ H2×2.0（实 {h1 / h2:F2}×；超出则调代价模型）");
        }

        private static void RunCli(string exe, string args)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, Arguments = args, RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            p?.WaitForExit();
        }

    }
}
