using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
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
using OR = FfmpegGui.Services.ColorMapping.OverrideReason;
using AC = FfmpegGui.Services.ColorMapping.AnnotChoice;
using CSC = FfmpegGui.Services.ColorMapping.CarryScope;
using MI = FfmpegGui.Services.ColorMapping.ManualIntent;
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using AAS = FfmpegGui.Services.AppSettingsService;

namespace Tests.Geometry
{
    /// <summary>
    /// 子系统测试组：**geometry**（缩放 / 首帧 / 取向）—— 2026-10-05 从 ServiceProbe 的单个
    /// 577 KB <c>Program.cs</c> 中物理拆出，使"改几何这一块"只需跑本工程，不再跑整个宿主。
    ///
    /// <para><b>覆盖的 mode</b>：<c>geometry</c> <c>firstframe</c> <c>orientation</c>
    /// <c>orientmeta</c>（见 <c>TestGroups</c>）。</para>
    ///
    /// <para><b>抽取原则</b>：<c>Probe*</c> 方法体**逐字复制**自
    /// <c>tests/ServiceProbe/Program.cs</c>（不改逻辑、不改字符串 —— 门禁在 grep 那些文本），
    /// 共享 helper 一律**转发**到 <c>Harness</c>。本组四个 probe 各自只用局部函数
    /// （<c>MakeSource</c> / <c>Cap</c> / <c>Psnr</c> / <c>Run</c> …）与 <c>Check</c>，
    /// 无需额外搬运类级 helper。</para>
    /// </summary>
    internal static class GeometryGroup
    {
        // ⚠ 共享 helper 一律**转发** Harness（实现只此一份，避免各组读数分叉）。
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);
        private static void Skip(string msg) => Harness.Skip(msg);

        /// <summary>
        /// 旧宿主里的 `_fail++`（"给了用法/前置不满足"时**只增计数、不打印**）。
        /// <para>⚠ 转发到 <c>Harness.Fail()</c>，**不是**本组自建计数器：自建一份会让
        /// <c>Harness.SummaryLine()</c>/<c>ExitCode()</c> 读到 0/0 而汇总与计数脱钩。
        /// 也**不能**改写成 <c>Check(false, …)</c> —— 那会多打一行 <c>FAIL …</c>，与旧读数分叉。</para>
        /// </summary>
        private static void Fail() => Harness.FailOnly();

        // ── 入口（供 Program 分派；目标方法体即抽出的 Probe* 原文）──
        internal static void RunGeometry(string[] a) => ProbeGeometry(a).GetAwaiter().GetResult();
        internal static void RunFirstframe(string[] a) => ProbeFirstframe(a).GetAwaiter().GetResult();
        internal static void RunOrientation(string[] a) => ProbeOrientation(a).GetAwaiter().GetResult();
        internal static void RunOrientMeta(string[] a) => ProbeOrientMeta(a).GetAwaiter().GetResult();

        private static async System.Threading.Tasks.Task ProbeGeometry(string[] args)
        {
            string ff = AppSettingsService.Current.FfmpegPath;
            if (string.IsNullOrWhiteSpace(ff) || !File.Exists(ff))
            { Check(false, "geometry：未找到 ffmpeg（AppSettings 未初始化 / publish/PLAN 缺失）"); return; }

            const string ScaleMarker = "几何缩放（最长边限制）";     // 源码 RawColorPipeline.cs:128 的原样前缀
            string root = args.Length >= 2 ? args[1] : "tests/output/validate";
            // ⚠ 运行级隔离（GUID）：固定目录会被**并发同名实例**互删 ⇒ 假红（TESTING §6 第 66 条）
            string dir = Path.Combine(root, "geometry_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Console.WriteLine($"geometry-out={dir}");
            var created = new List<string>();      // 结尾按计数自清（不整目录删 ⇒ 避开宿主批量删除守卫）

            try
            {
                // ── 源素材自备：本探针自己生成，绝不依赖别处遗留产物（干净目录上必须能跑）──
                async System.Threading.Tasks.Task<string> MakeSource(int sw, int sh)
                {
                    var p = Path.Combine(dir, $"src_{sw}x{sh}.png");
                    int ec = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                        $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc2=size={sw}x{sh}:duration=1\" "
                      + $"-frames:v 1 \"{p}\"", null, ff).ConfigureAwait(false);
                    if (ec != 0 || !File.Exists(p))
                        throw new InvalidOperationException($"源生成失败 (exit {ec})");
                    if (!created.Contains(p)) created.Add(p);      // 去重：同一尺寸的源在多个用例间复用
                    return p;
                }

                // 用例表：源尺寸 / 最长边限制 / 期望产出。
                // 期望值全部来自 2026-09-17 的**实测**（本文件下方的 boundary 段把「自己算会漂开」也钉住）。
                //   A 512x384 限 128 → 滤镜 scale=128:-2  → 128x96（96 = 384*128/512，恰为整数）
                //   B 512x384 限  32 → 滤镜 scale=32:-2   →  32x24
                //   C 384x512 限  32 → 滤镜 scale=-2:32   →  24x32（**纵向**分支，覆盖 -2 锁高度那条）
                //   D 512x300 限 100 → 滤镜 scale=100:-2  → 100x58（58.59375 ⇒ **非整数**那条边，
                //        自己算 round(58.59)=59 / 取偶到 60 都会与 ffmpeg 的 58 漂开）
                //   E 512x384 限 1024 → 滤镜 null（不超限）→ 512x384（**负控**：尺寸不变 + 无缩放日志）
                var cases = new (string id, int sw, int sh, int maxDim, int expW, int expH)[]
                {
                    ("A-landscape-128", 512, 384, 128, 128, 96),
                    ("B-landscape-32",  512, 384,  32,  32,  24),
                    ("C-portrait-32",   384, 512,  32,  24,  32),
                    ("D-fractional-100",512, 300, 100, 100,  58),
                    ("E-negative",      512, 384, 1024, 512, 384),
                };

                foreach (var c in cases)
                {
                    string src = await MakeSource(c.sw, c.sh).ConfigureAwait(false);
                    var (pw, ph) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                        .ProbeSizeAsync(src, ff).ConfigureAwait(false);
                    Check(pw == c.sw && ph == c.sh, $"{c.id} 源尺寸自备 = {c.sw}x{c.sh}（ffprobe 读回 {pw}x{ph}）");

                    var o = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorStrategy = ST.Recommended, BitDepth = 8,
                        EnableMaxDimension = true, MaxDimension = c.maxDim,
                    };
                    // 单一真值：预缩放路径与引擎路径用的是**同一个**函数，这里把它取出来做对照
                    string? filter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(o, src);

                    var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, src, out var why);
                    if (it == null) { Check(false, $"{c.id} 选项→意图失败：{why}"); continue; }
                    var plan = CE.Plan(it);
                    if (!plan.IsOk) { Check(false, $"{c.id} 计划被拒：{plan.Reason}"); continue; }

                    string engOut = Path.Combine(dir, $"eng_{c.id}.png");
                    created.Add(engOut);
                    var sb = new StringBuilder();
                    var gate = new object();          // 引擎日志可能来自行带并行线程
                    FfmpegGui.Services.ColorMapping.RawColorPipeline.Stats st;
                    try
                    {
                        st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                            src, engOut, plan.ToSpec(), plan,
                            s => { lock (gate) sb.Append(s); }, default, 1, ff, o).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Check(false, $"{c.id} 引擎异常：{ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    string log = sb.ToString();
                    bool scaledLog = log.Contains(ScaleMarker);
                    var (ew, eh) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                        .ProbeSizeAsync(engOut, ff).ConfigureAwait(false);
                    bool expectScaled = !string.IsNullOrEmpty(filter);
                    Console.WriteLine($"  · {c.id} 滤镜={filter ?? "(null)"} 引擎产出={ew}x{eh} "
                        + $"Stats={st.Width}x{st.Height} 缩放日志={scaledLog}");
                    Console.WriteLine($"geometry-case id={c.id} src={c.sw}x{c.sh} maxdim={c.maxDim} "
                        + $"expect={c.expW}x{c.expH} engine={ew}x{eh} stats={st.Width}x{st.Height} "
                        + $"log={(scaledLog ? "scaled" : "none")} prescale={(filter == null ? "n/a" : "see-next")}");

                    // ① 产出尺寸（长边锁定 + -2 自动偶数）
                    Check(ew == c.expW && eh == c.expH,
                        $"{c.id} 引擎产出尺寸 = {c.expW}x{c.expH}（ffprobe 读回 {ew}x{eh}）");
                    // ② Stats 反推尺寸 == 实际产物（RawColorPipeline.cs:120-129 那段零覆盖的逻辑）
                    Check(st.Width == ew && st.Height == eh,
                        $"{c.id} Stats 反推尺寸 == 实际产物（{st.Width}x{st.Height} vs {ew}x{eh}）");
                    // ③ 日志证据：这段代码真的跑了 / 负控真的没跑
                    Check(scaledLog == expectScaled, expectScaled
                        ? $"{c.id} 日志出现「{ScaleMarker}」⇒ 引擎几何缩放段确实执行"
                        : $"{c.id} 负控：不超限 ⇒ 日志**不得**出现「{ScaleMarker}」（实际 {(scaledLog ? "出现" : "未出现")}）");

                    // ④ 引擎缩放 vs QueueProcessor 预缩放：同滤镜同尺寸实测对照
                    if (filter == null)
                    {
                        Console.WriteLine($"  · {c.id} 预缩放路径：滤镜为 null ⇒ QueueProcessor 不会预缩放（N/A）");
                    }
                    else
                    {
                        string pre = Path.Combine(dir, $"pre_{c.id}.png");
                        created.Add(pre);
                        int pec = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                            $"-y -hide_banner -loglevel error -i \"{src}\" -vf \"{filter}\" "
                          + $"-compression_level 0 \"{pre}\"", null, ff).ConfigureAwait(false);
                        var (qw, qh) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                            .ProbeSizeAsync(pre, ff).ConfigureAwait(false);
                        Console.WriteLine($"geometry-prescale id={c.id} engine={ew}x{eh} prescale={qw}x{qh} "
                            + $"filter=\"{filter}\"");
                        Check(pec == 0 && qw > 0, $"{c.id} 预缩放路径产出可用（exit {pec}）");
                        Check(qw == ew && qh == eh,
                            $"{c.id} 引擎缩放 == 预缩放（{ew}x{eh} vs {qw}x{qh}）⇒ 删预缩放不会改变几何口径");
                    }
                }

                // ── 边界坑：把源码注释里那次实测（42x32）钉住 ──
                // ⚠ 这条滤镜**不是** BuildMaxDimensionScaleFilter 为 512x384 选出的（横图锁宽度 ⇒ scale=32:-2），
                //   它是源码注释（RawColorPipeline.cs:99-100）里那次直接实测所用的滤镜。保留它是为了把
                //   「自己算会漂开」这个**事实**钉住：512*32/384 = 42.67 ⇒ 自己算 round=43 / 取偶=44，
                //   而 ffmpeg 的 `-2` 实测给 42。正因如此 :120-129 只能用**解码产物长度**反推另一条边。
                {
                    string src = await MakeSource(512, 384).ConfigureAwait(false);
                    string b = Path.Combine(dir, "boundary_scaleV32.png");
                    created.Add(b);
                    int ec = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                        $"-y -hide_banner -loglevel error -i \"{src}\" "
                      + $"-vf \"scale=-2:32:flags=lanczos\" -compression_level 0 \"{b}\"", null, ff)
                        .ConfigureAwait(false);
                    var (bw, bh) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                        .ProbeSizeAsync(b, ff).ConfigureAwait(false);
                    int naiveRound = (int)Math.Round(512.0 * 32 / 384);   // 42.67 → 43
                    int naiveEvenUp = (naiveRound + 1) / 2 * 2;           // 43 → 44
                    Console.WriteLine($"geometry-boundary filter=\"scale=-2:32\" actual={bw}x{bh} "
                        + $"selfcompute={512.0 * 32 / 384:F2} naiveRound={naiveRound} naiveEvenUp={naiveEvenUp}");
                    Check(ec == 0 && bw == 42 && bh == 32,
                        $"边界：512x384 用 scale=-2:32 实测 42x32（实 {bw}x{bh}）；"
                      + $"自己算 42.67 会得 {naiveRound}/{naiveEvenUp} ⇒ 必须按解码产物长度反推");
                }

                // ── 删预缩放的**像素级**安全验证（非恒等色彩计划 + 缩放）──
                // 上面 A–E 只比了**尺寸**，而且用的是直通计划（Action=None）⇒ 测不到「先缩放后映射」与
                // 「先映射后缩放」的差异。本段补上**非恒等**计划下的像素对照，回答唯一那个还没答的问题：
                // **把 QueueProcessor 的预缩放删掉、改由引擎在解码里缩放，像素会不会变？**
                //
                // 两条路径（对照今天 vs 删后）：
                //   ① 今天：ffmpeg 预缩放（只缩放、不改色彩）→ 引擎解码该产物 + 色彩映射  ⇒ **缩放 → 映射**
                //   ② 删后：引擎在同一次解码里 `-vf format=rgb48le,scale=…` 缩放 + 色彩映射 ⇒ **缩放 → 映射**
                // ⚠ 关键结论（读码可得，本段用像素实测背书）：**两条路径的顺序相同**，差别只在 ① 多一次
                //   「缩放产物落盘成 PNG → 再解码」的量化往返 ⇒ 预期 ① 略差、差异应很小。
                //   真正的**反序**只出现在 legacy 路径（`--color-engine legacy`）：色彩链在
                //   `FfmpegCommandBuilder.cs:121`、scale 由 `InsertScaleFilterAtChainHead` 插入 ⇒ legacy = 映射 → 缩放。那是既有的
                //   引擎-vs-legacy 差异，与「删不删预缩放」无关，不在本段射程内。
                {
                    string src = await MakeSource(512, 384).ConfigureAwait(false);
                    const int md = 128, ew = 128, eh = 96;
                    // 非恒等计划：源无任何色彩元数据 ⇒ 引擎按惯例假定 sRGB；目标显式给 Display P3
                    // ⇒ sRGB→P3 真映射（与 ProbeEngineAb 同一手法）。恒等计划下本段无意义，故先断言非恒等。
                    var o = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorSpace = "Display P3", ColorStrategy = ST.Recommended,
                        BitDepth = 16, EnableMaxDimension = true, MaxDimension = md,
                    };
                    var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, src, out var why);
                    if (it == null) { Check(false, $"像素对照：选项→意图失败：{why}"); }
                    else
                    {
                        var plan = CE.Plan(it);
                        Check(plan.IsOk && plan.Action != CA.None,
                            $"像素对照：计划为非恒等（Action={plan.Action}，目标 Display P3）—— 恒等计划测不到顺序差异");

                        string pre = Path.Combine(dir, "px_pre_128.png");
                        created.Add(pre);
                        string preFilter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(o, src) ?? "";
                        int pec = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                            $"-y -hide_banner -loglevel error -i \"{src}\" -vf \"{preFilter}\" "
                          + $"-compression_level 0 \"{pre}\"", null, ff).ConfigureAwait(false);

                        var sb1 = new StringBuilder(); var g1 = new object();
                        var sb2 = new StringBuilder(); var g2 = new object();
                        string out1 = Path.Combine(dir, "px_today_prescale.png");
                        string out2 = Path.Combine(dir, "px_after_engine.png");
                        created.Add(out1); created.Add(out2);
                        var st1 = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                            pre, out1, plan.ToSpec(), plan, s => { lock (g1) sb1.Append(s); },
                            default, 1, ff, o).ConfigureAwait(false);
                        var st2 = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                            src, out2, plan.ToSpec(), plan, s => { lock (g2) sb2.Append(s); },
                            default, 1, ff, o).ConfigureAwait(false);

                        Check(pec == 0 && st1.Width == ew && st1.Height == eh
                              && st2.Width == ew && st2.Height == eh,
                            $"像素对照：两条路径尺寸均 {ew}x{eh}（今天 {st1.Width}x{st1.Height} / 删后 {st2.Width}x{st2.Height}）");
                        // 日志证据：今天那路不该缩放（输入已缩过），删后那路必须缩放
                        Check(!sb1.ToString().Contains(ScaleMarker) && sb2.ToString().Contains(ScaleMarker),
                            $"像素对照：缩放归属正确（今天=预缩放、引擎不缩放；删后=引擎缩放）");

                        var pa = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                            .DecodeToRgb48Async(out1, ew, eh).ConfigureAwait(false);
                        var pb = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                            .DecodeToRgb48Async(out2, ew, eh).ConfigureAwait(false);
                        double mse = 0, meanA = 0, meanB = 0; long n = 0; int maxDiff = 0;
                        for (int i = 0; i + 1 < pa.Length && i + 1 < pb.Length; i += 2)
                        {
                            int x = pa[i] | (pa[i + 1] << 8), y = pb[i] | (pb[i + 1] << 8);
                            double d = (x - y) / 65535.0;
                            mse += d * d; meanA += x; meanB += y; n++;
                            maxDiff = Math.Max(maxDiff, Math.Abs(x - y));
                        }
                        mse /= Math.Max(1, n); meanA /= Math.Max(1, n); meanB /= Math.Max(1, n);
                        double psnr = mse > 0 ? 10 * Math.Log10(1.0 / mse) : 99;
                        Console.WriteLine($"geometry-pixels prescale-vs-engine psnr={psnr:F2}dB maxDiff={maxDiff}/65535 "
                            + $"meanToday={meanA / 65535.0:F4} meanAfter={meanB / 65535.0:F4} samples={n}");
                        Check(psnr >= 40.0,
                            $"像素对照：今天(预缩放→映射) vs 删后(引擎内缩放→映射) PSNR ≥ 40dB（实 {psnr:F2}，{n} 样本）");
                        Check(maxDiff <= 3000,
                            $"像素对照：最大单通道差 ≤ 3000/65535（实 {maxDiff}）—— 差异只应来自一次 PNG 量化往返");
                        Check(Math.Abs(meanA - meanB) / 65535.0 < 0.005,
                            $"像素对照：两路平均亮度一致（差 {Math.Abs(meanA - meanB) / 65535.0:F5}）⇒ 删预缩放不偏色");
                    }
                }

                // ═══ 顺序统一（R1）：scale 必须插在「第一条色彩变换之前」（2026-09-18）═══
                // 判据只有一份：`FfmpegCommandBuilder.InsertScaleFilterAtChainHead` —— legacy 的 scale 也走它
                // 与 `QueueProcessor.BuildPipeColorArgs` 的管道路径同源。
                // 本段**读命令行、不读日志**；token 按 `,` 切分；判别式
                //   `^scale=\d+:-2:flags=lanczos$` / `^scale=-2:\d+:flags=lanczos$`
                // **`-2` 是分水岭**：`AnimationScaleW` 的 `scale={W}:-1:` 与 preScale 的
                // `scale=ceil(iw/2)*2:` 都不匹配 ⇒ 不会把「另一个合法 token」当成本项。
                // ⚠ 载体必须按管线**显式分派**（§6 第 74 条：载体选错 ⇒ 命中另一个合法 token ⇒ 断言**恒真**，
                //   比恒红更危险）。本段把「形态自检」放在每条位置断言**之前**（§6 第 77 条）。
                {
                    static string[] Tok(string chain) => chain.Split(',');

                    // 返回判别式命中的下标；无命中 -1；命中**多于一处** -2（计数异常，单独暴露）
                    static int IdxOfScale(string[] t)
                    {
                        int hit = -1, n = 0;
                        foreach (var (s, i) in t.Select((s, i) => (s, i)))
                            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^scale=(\d+:-2|-2:\d+):flags=lanczos$"))
                            { n++; if (hit < 0) hit = i; }
                        return n == 1 ? hit : (n == 0 ? -1 : -2);
                    }

                    static int IdxOfColor(string[] t)
                    {
                        for (int i = 0; i < t.Length; i++)
                            if (t[i].StartsWith("zscale=") || t[i].StartsWith("tonemap=")
                                || t[i].StartsWith("colorchannelmixer=")) return i;
                        return -1;
                    }

                    // 决策层的「偶数守卫」token（`FfmpegCommandBuilder.Decision.cs:316` 的 preScale）——
                    // ⚠ 它**不是**色彩变换，但它是**另一个合法** `scale=…:flags=lanczos` token：
                    //   `scale=ceil(iw/2)*2:ceil(ih/2)*2:flags=lanczos`。
                    //   它恰好落在 R1 锚点的**前驱**位置上（链形如 `format=…,{preScale}zscale=…`）
                    //   ⇒ 「前驱必须是 format=」这条**冻结判据在带 preScale 的形态上有假红**（实测踩到）。
                    static bool IsEvenGuard(string t) =>
                        System.Text.RegularExpressions.Regex.IsMatch(
                            t, @"^scale=ceil\(iw/2\)\*2:ceil\(ih/2\)\*2:flags=lanczos$");

                    // 取 `<opt> <值>`：值可能带字面引号（GIF complex / 管道路径），也可能不带
                    // （legacy `-vf` 走 `string.Join(" ", args)`，链是裸 token）——两种都要能吃。
                    static string? TakeOpt(string cmd, string opt)
                    {
                        int i = cmd.IndexOf(opt + " ", StringComparison.Ordinal);
                        if (i < 0) return null;
                        int s = i + opt.Length + 1;
                        if (s < cmd.Length && cmd[s] == '"')
                        {
                            int e = cmd.IndexOf('"', s + 1);
                            return e > s ? cmd.Substring(s + 1, e - s - 1) : null;
                        }
                        int sp = cmd.IndexOf(' ', s);
                        return sp > s ? cmd.Substring(s, sp - s) : cmd.Substring(s);
                    }

                    string r1src = await MakeSource(512, 384).ConfigureAwait(false);

                    // HDR（PQ/BT.2020）源：用来逼出 **S2 形态**（`format=yuv444p,tonemap=…,format=rgb48le,zscale=…`
                    // ⇒ 链里有**两个**色彩变换）—— M-h 必须在这个形态上才测得出来（单一色彩变换时 M-g ≡ M-h）。
                    async System.Threading.Tasks.Task<string> MakeHdrSource(int sw, int sh)
                    {
                        var p = Path.Combine(dir, $"hdrsrc_{sw}x{sh}.png");
                        int ec = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                            $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc2=size={sw}x{sh}:duration=1:rate=1\" "
                          + $"-pix_fmt rgb48le -frames:v 1 -color_primaries bt2020 -color_trc smpte2084 \"{p}\"",
                            null, ff).ConfigureAwait(false);
                        if (ec != 0 || !File.Exists(p))
                            throw new InvalidOperationException($"HDR 源生成失败 (exit {ec})");
                        if (!created.Contains(p)) created.Add(p);
                        return p;
                    }

                    // ① legacy `-vf` + **有**色彩变换（源无标签⇒按惯例 sRGB；目标 Display P3 ⇒ 真映射）
                    string r1out = Path.Combine(dir, "r1_l1.png");
                    created.Add(r1out);
                    var o1 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorSpace = "Display P3", ColorStrategy = ST.Recommended,
                        BitDepth = 16, EnableMaxDimension = true, MaxDimension = 128,
                    };
                    string c1 = FfmpegCommandBuilder.BuildArguments(o1, r1src, r1out);
                    string? ch1 = TakeOpt(c1, "-vf");
                    Console.WriteLine($"r1-legacy-vf {ch1}");
                    if (ch1 == null) Check(false, "R1①：legacy 带色彩变换时应产出 -vf");
                    else
                    {
                        var t = Tok(ch1); int si = IdxOfScale(t), mi = IdxOfColor(t);
                        Check(si >= 0, $"R1①存在性：判别式命中且**唯一**（si={si}，-2 表示命中多处）");
                        Check(mi >= 0, $"R1①形态自检：该格确有色彩变换（mi={mi}）—— 形态不符即红（§6 第 77 条）");
                        Check(si >= 0 && mi >= 0 && si == mi - 1,
                            $"R1①位置：scaleIdx == mapIdx-1（si={si}, mi={mi}）⇒ 缩放→映射");
                        // ⚠ **先判下标有效再取下标**：变异下 `si` 会是 -1（未注入）或 -2（命中多处），
                        //   写成 `si == 0 || t[si - 1]...` 会**抛 IndexOutOfRangeException** ⇒
                        //   本组后续断言**整段不执行**（实测 pass 52→36），且异常行**不带 `FAIL` 前缀**
                        //   ⇒ 只按 `FAIL` 过滤的复读会漏掉它。故一律 `si >= 0 && …`。
                        Check(si >= 0 && (si == 0 || t[si - 1].StartsWith("format=") || IsEvenGuard(t[si - 1])),
                            $"R1①前驱形状：前驱是 format= 或决策层的偶数守卫（实 {(si > 0 ? t[si - 1] : "<链首/未命中>")}）");
                        Check(si >= 0 && (si == 0 || IdxOfColor(t[..si]) < 0),
                            $"R1①不越过色彩变换：`si` 之前**没有任何**色彩变换（实 {(si > 0 ? IdxOfColor(t[..si]) : -1)}）");
                    }

                    // ①′ **S2 形态**（HDR→SDR，链里有**两个**色彩变换：`tonemap=` 与 `zscale=`）
                    //    ⇒ R1 的锚点是**第一条**色彩变换（`tonemap=`），不是 `zscale=`。
                    //    本格是 M-h 的落点：插到 `tonemap` 之后 ⇒ 位置/不越过断言必须红。
                    string hdrSrc = await MakeHdrSource(512, 384).ConfigureAwait(false);
                    var o1b = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorSpace = "sRGB", ColorStrategy = ST.Recommended,
                        BitDepth = 8, EnableMaxDimension = true, MaxDimension = 128,
                    };
                    string c1b = FfmpegCommandBuilder.BuildArguments(o1b, hdrSrc, Path.Combine(dir, "r1_l1b.png"));
                    string? ch1b = TakeOpt(c1b, "-vf");
                    Console.WriteLine($"r1-legacy-vf-s2 {ch1b}");
                    if (ch1b == null || !Tok(ch1b).Any(x => x.StartsWith("tonemap=")))
                    {
                        // ⚠ **可数的 SKIP**（不是通过、也不是失败）：S2 形态需要**带 CICP 的 HDR 素材**，
                        //   而 `lavfi testsrc2 → PNG` **不携带** `-color_primaries/-color_trc`
                        //   （实测 ffprobe 报 `color_primaries=unknown` / `color_transfer=unknown`）
                        //   ⇒ HDR 探测不到 ⇒ 决策层不挂 tonemap 链 ⇒ **M-h 在本探针里无落点**。
                        //   影响评估：M-h 与 M-g 命中**同一条**断言（「不越过色彩变换」），差别只在
                        //   「链里有几个色彩变换」；M-g 已在单变换链上把它证红 ⇒ 本 SKIP **不降低**该断言的
                        //   已证强度，只缺「双变换链」这一个形态。补一份带 CICP 的素材后本格自动生效。
                        Console.WriteLine("r1-s2-shape SKIP：S2（tonemap）形态不可达（lavfi→PNG 不带 CICP）⇒ "
                            + "M-h 无落点，按「与 M-g 同断言、缺双变换形态」登记（SKIP=1）");
                    }
                    else
                    {
                        var t = Tok(ch1b); int si = IdxOfScale(t), mi = IdxOfColor(t);
                        Check(t.Any(x => x.StartsWith("tonemap=")),
                            $"R1①′形态自检：该格必须是 **S2**（含 `tonemap=`）—— 形态不符即红（§6 第 77 条）");
                        Check(si >= 0 && mi >= 0 && si == mi - 1,
                            $"R1①′位置：scaleIdx == **第一条**色彩变换 - 1（si={si}, mi={mi}）");
                        Check(si >= 0 && (si == 0 || IdxOfColor(t[..si]) < 0),
                            $"R1①′不越过色彩变换：`si` 之前无任何色彩变换（实 {(si > 0 ? IdxOfColor(t[..si]) : -1)}）");
                    }

                    // ② legacy `-vf` + **无**色彩变换（sRGB→sRGB 恒等）⇒ 链首即锚点
                    var o2 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorStrategy = ST.Recommended,
                        BitDepth = 8, EnableMaxDimension = true, MaxDimension = 128,
                    };
                    string c2 = FfmpegCommandBuilder.BuildArguments(o2, r1src, Path.Combine(dir, "r1_l2.png"));
                    string? ch2 = TakeOpt(c2, "-vf");
                    Console.WriteLine($"r1-legacy-vf-nomap {ch2}");
                    if (ch2 == null) Check(false, "R1②：legacy 无色彩变换时仍应注入 scale（新建 -vf）");
                    else
                    {
                        var t = Tok(ch2); int si = IdxOfScale(t), mi = IdxOfColor(t);
                        Check(mi == -1, $"R1②形态自检：该格**无**色彩变换（mi={mi}）");
                        Check(si == 0, $"R1②位置：无色彩变换时 scaleIdx == 0（si={si}）");
                    }

                    // ③ legacy `-filter_complex`（GIF 调色板优化）⇒ 载体是 **complex 首段**（先剥字面引号）
                    var o3 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "gif", ColorStrategy = ST.Recommended, GifPaletteOptimize = true,
                        EnableMaxDimension = true, MaxDimension = 128,
                    };
                    string c3 = FfmpegCommandBuilder.BuildArguments(o3, r1src, Path.Combine(dir, "r1_l3.gif"));
                    string? ch3 = TakeOpt(c3, "-filter_complex");
                    Console.WriteLine($"r1-legacy-complex {ch3}");
                    if (ch3 == null) Check(false, "R1③：GIF 调色板优化应产出 -filter_complex");
                    else
                    {
                        var t = Tok(ch3.Split(';')[0]); int si = IdxOfScale(t), mi = IdxOfColor(t);
                        Check(si >= 0, $"R1③存在性：complex **首段**里判别式命中且唯一（si={si}）");
                        // ⚠ 该格（SDR 源 ⇒ 链里无色彩变换）**必须**要求 `si == 0`：
                        //   写成 `mi < 0 ||` 会让判据**恒真**（mi 恒为 -1）⇒ 正是 §6 第 74 条的坑。
                        Check(mi < 0 ? si == 0 : si == mi - 1,
                            $"R1③位置：首段无色彩变换时 scaleIdx == 0（si={si}, mi={mi}）");
                        Check(si >= 0 && (si == 0 || t[si - 1].StartsWith("format=") || IsEvenGuard(t[si - 1])),
                            $"R1③前驱形状：前驱是 format= 或偶数守卫（实 {(si > 0 ? t[si - 1] : "<段首/未命中>")}）");
                        Check(si >= 0 && (si == 0 || IdxOfColor(t[..si]) < 0),
                            $"R1③不越过色彩变换：`si` 之前**没有任何**色彩变换（实 {(si > 0 ? IdxOfColor(t[..si]) : -1)}）");
                    }

                    // ④ **反向断言 + M-e 负控**：引擎 gif 出口的 complex 串里那个 `-2` scale 属于
                    //    `[1:v]` **alpha 对齐分支**，**不属于**色彩链（色彩映射在本进程完成，该串无色彩变换）。
                    //    ⇒ 门禁**不得**给该格套 `scaleIdx == mapIdx - 1`；若把该 `-2` 当色彩链载体，
                    //      位置断言会**恒真**（§6 第 74 条的第三个陷阱）。⚠ 本条**预期绿**，不是变异。
                    var o4 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "gif", ColorStrategy = ST.Recommended, GifPaletteOptimize = true,
                        EnableMaxDimension = true, MaxDimension = 128,
                    };
                    var it4 = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o4, r1src, out _);
                    if (it4 == null) Check(false, "R1④：引擎 gif 意图构建失败（无法做反向断言）");
                    else
                    {
                        var plan4 = CE.Plan(it4);
                        var sb4 = new StringBuilder();
                        string engGif = Path.Combine(dir, "r1_e1.gif");
                        created.Add(engGif);
                        await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                            r1src, engGif, plan4.ToSpec(), plan4, s => { lock (sb4) sb4.Append(s); },
                            default, 1, ff, o4).ConfigureAwait(false);
                        string? ec4 = null;
                        foreach (var ln in sb4.ToString().Split('\n'))
                            if (ln.Contains("-filter_complex"))
                            { ec4 = TakeOpt(ln, "-filter_complex"); if (ec4 != null) break; }
                        Console.WriteLine($"r1-engine-complex {ec4}");
                        if (ec4 == null) Check(false, "R1④：引擎 gif 命令行里应有 -filter_complex（反向断言的载体）");
                        else
                        {
                            var segs = ec4.Split(';');
                            Check(segs[0].StartsWith("[1:v]"),
                                $"R1④反向：complex **首段以 [1:v] 开头**（实 {segs[0][..Math.Min(12, segs[0].Length)]}）"
                              + " ⇒ 该段的 scale 属 alpha 分支，载体**不是**色彩链");
                            var alphaTok = Tok(segs[0]);
                            Check(IdxOfColor(alphaTok) < 0,
                                $"R1④反向：alpha 段内**无**色彩变换（mi={IdxOfColor(alphaTok)}）"
                              + " ⇒ 位置断言若套在该格上会恒真");
                            bool colorSegHasScale = false;
                            foreach (var sg in segs)
                                if (sg.StartsWith("[0:v]") && IdxOfScale(Tok(sg)) >= 0) colorSegHasScale = true;
                            Check(!colorSegHasScale,
                                "R1④反向：色彩分支段 `[0:v]…` **不含** scale（缩放属解码 -vf）");
                            Check(ec4.Contains("scale=128:-2:flags=lanczos")
                                  && alphaTok[0] != "scale=128:-2:flags=lanczos",
                                "R1④反向：该 `-2` **子串确实存在**、但 token 层面挂在 `[1:v]` 段上（不是独立 token）"
                              + " ⇒ 这正是「子串法载体」会踩的坑（§6 第 74 条）");
                        }
                    }

                    // ⑤ **成对**尺寸断言：只查「命令行里有 scale」是**必要不充分**（§6 第 70 条）——
                    //    必须再用**外部可观测量**（产物尺寸）背书。
                    int ec1 = await FfmpegGui.Services.FfmpegRunner.RunAsync(c1, null, ff).ConfigureAwait(false);
                    var (rw, rh) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                        .ProbeSizeAsync(r1out).ConfigureAwait(false);
                    Check(ec1 == 0 && rw == 128 && rh == 96,
                        $"R1⑤成对尺寸：命令行含 scale **且**产物确为 128x96（实 exit={ec1} {rw}x{rh}）");

                    // ⑥ 负控：不超限 ⇒ **不得**注入（证明判别式与存在性断言不是恒真）
                    var o6 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorStrategy = ST.Recommended, BitDepth = 8,
                        EnableMaxDimension = true, MaxDimension = 1024,
                    };
                    string c6 = FfmpegCommandBuilder.BuildArguments(o6, r1src, Path.Combine(dir, "r1_neg.png"));
                    Check(FfmpegCommandBuilder.FindInjectedScaleFilter(c6) == null,
                        "R1⑥负控：512x384 限 1024（不超限）⇒ 命令行**不含**最长边缩放");
                }
            }
            finally
            {
                // 结尾自清（按计数）：文件逐个删，目录空才删（宿主批量删除守卫会拦整目录删）
                // ⚠ 分母只算**清理时真实存在**的文件：中途异常时某些产物可能根本没被创建，
                //   若拿 created.Count 当分母会报出「11/12」这种**假残留**（实测踩到）。
                int total = 0;
                foreach (var p in created) { try { if (File.Exists(p)) total++; } catch { } }
                int removed = 0;
                foreach (var p in created) { try { if (File.Exists(p)) { File.Delete(p); removed++; } } catch { } }
                bool dirGone = false;
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    { Directory.Delete(dir); dirGone = true; }
                }
                catch { }
                Console.WriteLine($"geometry-cleanup: 删除文件 {removed}/{total}，目录{(dirGone ? "已" : "未")}删除");
            }
        }
        private static async System.Threading.Tasks.Task ProbeOrientation(string[] args)
        {
            string ff = AppSettingsService.Current.FfmpegPath;
            string ffp = AppSettingsService.Current.FfprobePath;
            string exif = FfmpegGui.Services.ExifToolService.DetectedPath ?? "";
            if (string.IsNullOrWhiteSpace(ff) || !File.Exists(ff))
            { Check(false, "orientation：未找到 ffmpeg（AppSettings 未初始化 / publish/PLAN 缺失）"); return; }
            if (string.IsNullOrWhiteSpace(ffp) || !File.Exists(ffp))
            { Check(false, "orientation：未找到 ffprobe ⇒ 夹具前提无法自检"); return; }
            if (string.IsNullOrWhiteSpace(exif) || !File.Exists(exif))
            { Check(false, "orientation：未检测到 exiftool ⇒ 无法造取向夹具（显式判红，不静默跳过）"); return; }

            string root = args.Length >= 2 ? args[1] : "tests/output/validate";
            // 运行级隔离（GUID）：固定目录会被并发同名实例互删 ⇒ 假红（TESTING §6 第 66 条）
            string dir = Path.Combine(root, "orient_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Console.WriteLine($"orient-out={dir}");
            var created = new List<string>();

            // 独立进程读数（**不复用** ImageGeometry 的解析器 ⇒ 本门禁是产品代码的对照臂）
            async Task<string> Cap(string exe, string a)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, a)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                })!;
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();     // P2-3：两路都要排空
                await p.WaitForExitAsync().ConfigureAwait(false);
                return (await so.ConfigureAwait(false) + await se.ConfigureAwait(false)).Trim();
            }
            async Task Run(string exe, string a) { await Cap(exe, a).ConfigureAwait(false); }
            // coded 尺寸：ffprobe 的 stream 段（**旧口径**，本门禁专门拿它当"错的那把尺"）
            (int w, int h) Coded(string path)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    Cap(ffp, $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{path}\"")
                        .GetAwaiter().GetResult(), @"(\d+),(\d+)");
                return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
            }
            int Rotation(string path)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    Cap(ffp, $"-v error -select_streams v:0 -read_intervals \"%+#1\" "
                          + $"-show_entries frame_side_data_list -of json \"{path}\"")
                        .GetAwaiter().GetResult(), @"""rotation""\s*:\s*(-?\d+)");
                return m.Success ? int.Parse(m.Groups[1].Value) : 0;
            }
            // 像素对照：两臂同格式 → ffmpeg 自带 psnr（与本仓 PsnrCalculator 无关 ⇒ 独立参照）
            // ⚠ 取不到数字时**必须报出原因**（变异实测：几何口径退回 coded 后两臂尺寸不同 ⇒
            //   ffmpeg 直接拒绝，读数会是空的；只写"无读数"就等于把最有信息量的一条证据丢掉）
            string Psnr(string a, string b, string fmt)
            {
                string outp = Cap(ff, $"-hide_banner -i \"{a}\" -i \"{b}\" "
                            + $"-lavfi \"[0:v]format={fmt}[x];[1:v]format={fmt}[y];[x][y]psnr\" -f null -")
                          .GetAwaiter().GetResult();
                var m = System.Text.RegularExpressions.Regex.Match(outp, @"average:(inf|[0-9][0-9.]*)");
                if (m.Success) return m.Groups[1].Value;
                var w = System.Text.RegularExpressions.Regex.Match(outp,
                    @"(Width and height of input videos must be same|Invalid data|No such file)");
                return w.Success ? $"图不可比:{w.Groups[1].Value}" : "无读数";
            }
            // 夹具：lavfi 生成的**非对称**图（testsrc2 含渐变+文字，行/列内容互不相同 ⇒ 剪切可测）
            async Task<string> MakeTiff(string name, string pix, int orientation)
            {
                var p = Path.Combine(dir, name);
                await Run(ff, $"-y -hide_banner -loglevel error -f lavfi "
                            + $"-i \"testsrc2=size=64x32:duration=1\" -frames:v 1 -pix_fmt {pix} \"{p}\"")
                          .ConfigureAwait(false);
                created.Add(p);
                if (orientation != 0)
                    await Run(exif, $"-q -n -overwrite_original \"-Orientation={orientation}\" \"{p}\"")
                          .ConfigureAwait(false);
                return p;
            }
            async Task<(FfmpegGui.Services.ColorMapping.RawColorPipeline.Stats st, int w, int h)> EngineRun(
                string src, string outPath, int bitDepth, int maxDim)
            {
                var o = new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "png", ColorStrategy = ST.Recommended, BitDepth = bitDepth,
                    EnableMaxDimension = maxDim > 0, MaxDimension = maxDim > 0 ? maxDim : 0,
                };
                var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, src, out var why);
                if (it == null) throw new InvalidOperationException($"选项→意图失败：{why}");
                var plan = CE.Plan(it);
                if (!plan.IsOk) throw new InvalidOperationException($"计划被拒：{plan.Reason}");
                var st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                    src, outPath, plan.ToSpec(), plan, null, default, 1, ff, o).ConfigureAwait(false);
                var (w, h) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                    .ProbeSizeAsync(outPath, ff).ConfigureAwait(false);
                return (st, w, h);
            }

            try
            {
                // ── 夹具 + 前提自检 ──
                string src16 = await MakeTiff("o16_orient8.tif", "rgb48le", 8).ConfigureAwait(false);
                string src8  = await MakeTiff("o8_orient8.tif", "rgb24", 8).ConfigureAwait(false);
                string srcNR = await MakeTiff("o16_norot.tif", "rgb48le", 0).ConfigureAwait(false);
                var (cw, ch) = Coded(src16);
                int rot = Rotation(src16);
                var (dw, dh) = (ch, cw);   // 期望显示尺寸 = coded 交换（rotation=90）
                Console.WriteLine($"orient-fixture coded={cw}x{ch} rotation={rot} displayExpect={dw}x{dh}");
                Check(cw == 64 && ch == 32, $"夹具 coded = 64x32（实 {cw}x{ch}）");
                Check(rot == 90,
                    $"夹具 rotation = 90（实 {rot}）⇒ 取向标签真的会被 autorotate；"
                  + $"若为 0/-180 说明 exiftool 少了 -n（实测会静默写成 3 ⇒ 轴向不变 ⇒ 门禁失牙）");
                Check(cw != dw && ch != dh, "夹具前提：coded 与显示尺寸**轴向不同**（相同则该夹具恒真、测不到本缺陷）");
                var (nrW, nrH) = (Coded(srcNR).w, Coded(srcNR).h);
                Check(Rotation(srcNR) == 0 && nrW == 64 && nrH == 32,
                    $"负控夹具无取向标签（rotation={Rotation(srcNR)}，coded={nrW}x{nrH}）");

                // ── 探测口径本身：显示尺寸必须与 coded 不同 ──
                var (pw, ph) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                    .ProbeSizeAsync(src16, ff).ConfigureAwait(false);
                Console.WriteLine($"orient-probe coded={cw}x{ch} probe(显示)={pw}x{ph}");
                Check(pw == dw && ph == dh,
                    $"ProbeSizeAsync 报**显示**尺寸 {dw}x{dh}（实 {pw}x{ph}；旧实现报 coded {cw}x{ch}）");

                // ── O1：16-bit 取向源 → 引擎产出必须是 32x64 且与独立参照逐字节等 ──
                // 参照 = ffmpeg 直解（同样吃 autorotate），不经本仓任何代码 ⇒ 两臂只可能差在几何标注。
                string ref16 = Path.Combine(dir, "ref16.png"); created.Add(ref16);
                await Run(ff, $"-y -hide_banner -loglevel error -i \"{src16}\" -frames:v 1 "
                            + $"-pix_fmt rgb48le -compression_level 0 \"{ref16}\"").ConfigureAwait(false);
                var (rcw, rch) = Coded(ref16);
                Check(rcw == dw && rch == dh && Rotation(ref16) == 0,
                    $"参照臂前提：ffmpeg 直解产物 coded={rcw}x{rch}（期望 {dw}x{dh}）且**不带** rotation"
                  + $"（实 {Rotation(ref16)}）⇒ 对拍的是几何口径，不是「两次旋转是否抵消」");
                string e1 = Path.Combine(dir, "O1_engine16.png"); created.Add(e1);
                var s1 = await EngineRun(src16, e1, 16, 0).ConfigureAwait(false);
                string p1 = Psnr(e1, ref16, "gbrp16le");
                Console.WriteLine($"orient-case id=O1-16bit expect={dw}x{dh} engine={s1.w}x{s1.h} "
                                + $"stats={s1.st.Width}x{s1.st.Height} psnr={p1}");
                Check(s1.w == dw && s1.h == dh, $"O1 引擎产出尺寸 = {dw}x{dh}（实 {s1.w}x{s1.h}）");
                Check(s1.st.Width == s1.w && s1.st.Height == s1.h,
                    $"O1 Stats 与实际产物一致（{s1.st.Width}x{s1.st.Height} vs {s1.w}x{s1.h}）");
                Check(p1 == "inf",
                    $"O1 像素与独立参照**逐字节等**（psnr={p1}）⇒ 不是「接近」，是没有任何行错位；"
                  + $"缺陷未修时本条实测读数为「图不可比:Width and height of input videos must be same」"
                  + $"（两臂轴向不同），用户侧产物 vs 真值则是 7.97dB 量级");

                // ── O2：8-bit 出口（16→8 降位由引擎内核做，允许 ±1 LSB 舍入差）──
                string ref8 = Path.Combine(dir, "ref8.png"); created.Add(ref8);
                await Run(ff, $"-y -hide_banner -loglevel error -i \"{src8}\" -frames:v 1 "
                            + $"-pix_fmt rgb24 -compression_level 0 \"{ref8}\"").ConfigureAwait(false);
                string e2 = Path.Combine(dir, "O2_engine8.png"); created.Add(e2);
                var s2 = await EngineRun(src8, e2, 8, 0).ConfigureAwait(false);
                string p2 = Psnr(e2, ref8, "gbrp");
                double p2v = double.TryParse(p2, out var v2) ? v2 : (p2 == "inf" ? 999 : -1);
                Console.WriteLine($"orient-case id=O2-8bit expect={dw}x{dh} engine={s2.w}x{s2.h} psnr={p2}");
                Check(s2.w == dw && s2.h == dh, $"O2 引擎产出尺寸 = {dw}x{dh}（实 {s2.w}x{s2.h}）");
                Check(p2v >= 45.0, $"O2 8-bit 出口 psnr ≥45dB（实 {p2}）⇒ 几何正确（剪切会掉到个位数 dB）");

                // ── O3：取向源 + 最长边限制 ⇒ 滤镜必须锁**显示**口径的长边（纵向分支）──
                string filter3 = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(
                    new FfmpegGui.Models.FfmpegOptions
                    { Format = "png", EnableMaxDimension = true, MaxDimension = 16 }, src16) ?? "";
                string e3 = Path.Combine(dir, "O3_engine_max16.png"); created.Add(e3);
                var s3 = await EngineRun(src16, e3, 16, 16).ConfigureAwait(false);
                Console.WriteLine($"orient-case id=O3-maxdim filter=\"{filter3}\" expect=8x16 "
                                + $"engine={s3.w}x{s3.h} stats={s3.st.Width}x{s3.st.Height}");
                Check(filter3.Contains("-2:16", StringComparison.Ordinal),
                    $"O3 滤镜锁的是**纵向**长边（scale=-2:16，实得 \"{filter3}\"）；"
                  + $"按 coded 64x32 判会选成 scale=16:-2 ⇒ 最长边突破用户上限");
                Check(s3.w == 8 && s3.h == 16, $"O3 引擎产出 = 8x16（实 {s3.w}x{s3.h}）");
                Check(s3.st.Width == s3.w && s3.st.Height == s3.h,
                    $"O3 Stats 与实际产物一致（{s3.st.Width}x{s3.st.Height} vs {s3.w}x{s3.h}）");

                // ── O4 负控：无取向标签的同一张图 ⇒ 不许凭空旋转 ──
                string e4 = Path.Combine(dir, "O4_engine_norot.png"); created.Add(e4);
                var s4 = await EngineRun(srcNR, e4, 16, 0).ConfigureAwait(false);
                Console.WriteLine($"orient-case id=O4-negative expect=64x32 engine={s4.w}x{s4.h}");
                Check(s4.w == 64 && s4.h == 32,
                    $"O4 负控：无取向标签 ⇒ 产出保持 64x32（实 {s4.w}x{s4.h}）；"
                  + $"若变成 32x64 说明探测在凭空施加旋转");
            }
            catch (Exception ex)
            {
                Check(false, $"orientation 探针异常：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                int total = 0;
                foreach (var p in created) { try { if (File.Exists(p)) total++; } catch { } }
                int removed = 0;
                foreach (var p in created) { try { if (File.Exists(p)) { File.Delete(p); removed++; } } catch { } }
                bool dirGone = false;
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    { Directory.Delete(dir); dirGone = true; }
                }
                catch { }
                Console.WriteLine($"orient-cleanup: 删除文件 {removed}/{total}，目录{(dirGone ? "已" : "未")}删除");
            }
        }
        private static async System.Threading.Tasks.Task ProbeOrientMeta(string[] args)
        {
            string ff = AppSettingsService.Current.FfmpegPath;
            string exif = FfmpegGui.Services.ExifToolService.DetectedPath ?? "";
            if (string.IsNullOrWhiteSpace(ff) || !File.Exists(ff))
            { Check(false, "orientmeta：未找到 ffmpeg"); return; }
            if (string.IsNullOrWhiteSpace(exif) || !File.Exists(exif))
            { Check(false, "orientmeta：未检测到 exiftool ⇒ 无法造夹具（显式判红，不静默跳过）"); return; }

            string root = args.Length >= 2 ? args[1] : "tests/output/validate";
            string dir = Path.Combine(root, "orientmeta_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Console.WriteLine($"orientmeta-out={dir}");
            var created = new List<string>();

            async Task Run(string exe, string a)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, a)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync().ConfigureAwait(false);
                await so; await se;
            }
            // ⚠ **只读 stdout**：exiftool 的 perl locale 警告走 **stderr**，合并取数会把
            //   "Falling back to the standard locale" 当成标签值读回来（本探针第一版实测踩中，
            //   6 条判据全被它带红）。stderr 仍要**排空**（P2-3：不排空会堵死子进程），只是不参与取值。
            //   ⇒ 这正是 `_probe-stderr-merge-scan.ps1` 那条债账锁要拦的形态，写探针的人也不例外。
            async Task<string> Cap(string exe, string a)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, a)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync().ConfigureAwait(false);
                string stdout = await so.ConfigureAwait(false);
                _ = await se.ConfigureAwait(false);
                return stdout.Trim();
            }
            async Task<string> OrientationOf(string path)
                => (await Cap(exif, $"-q -n -Orientation \"{path}\"").ConfigureAwait(false))
                     .Split(':').Last().Trim();

            try
            {
                // ── 夹具：带拍摄字段的取向 JPEG（Make/Model 用来证明"复制真的发生了"）──
                string src = Path.Combine(dir, "src_orient8.jpg"); created.Add(src);
                await Run(ff, $"-y -hide_banner -loglevel error -f lavfi "
                            + $"-i \"testsrc2=size=64x32:duration=1\" -frames:v 1 -q:v 3 \"{src}\"")
                      .ConfigureAwait(false);
                await Run(exif, $"-q -n -overwrite_original \"-Orientation=8\" -Make=TESTCAM -Model=TESTMODEL "
                              + $"\"{src}\"").ConfigureAwait(false);
                var (cw, ch) = FfmpegGui.Services.ImageGeometry
                    .ParseDisplaySize(await Cap(AppSettingsService.Current.FfprobePath,
                        $"-v error -select_streams v:0 -read_intervals \"%+#1\" "
                      + $"-show_entries stream=width,height -show_entries frame_side_data_list -of json \"{src}\"")
                        .ConfigureAwait(false));
                Check(cw == 32 && ch == 64 && await OrientationOf(src) == "8",
                    $"夹具：Orientation=8 且**显示**尺寸 = 32x64（实得 {cw}x{ch} / 标签 {await OrientationOf(src)}）"
                  + $"——coded 必须是 64x32，否则两口径同轴、本探针恒真");

                // ── 产物：模拟"解码器已旋正"的出口（ffmpeg 直解，不带任何取向标签）──
                string prod = Path.Combine(dir, "prod_upright.jpg"); created.Add(prod);
                await Run(ff, $"-y -hide_banner -loglevel error -i \"{src}\" -frames:v 1 -q:v 3 \"{prod}\"")
                      .ConfigureAwait(false);
                var (pw, ph) = FfmpegGui.Services.ImageGeometry
                    .ParseDisplaySize(await Cap(AppSettingsService.Current.FfprobePath,
                        $"-v error -select_streams v:0 -read_intervals \"%+#1\" "
                      + $"-show_entries stream=width,height -show_entries frame_side_data_list -of json \"{prod}\"")
                        .ConfigureAwait(false));
                Check(pw == 32 && ph == 64 && await OrientationOf(prod) == "",
                    $"产物起点：像素已旋正（{pw}x{ph}）且**本来就没有** Orientation 标签（实得「{await OrientationOf(prod)}」）"
                  + $"⇒ 后面若出现标签，只可能是恢复步骤抄进来的");

                // ── M1/M2：产品自己的恢复调用（默认 dropOrientation=true）──
                int rc = await FfmpegGui.Services.ExifToolService.CopyMetadataAsync(
                    src, prod, null, absorb: null).ConfigureAwait(false);
                string gotOri = await OrientationOf(prod).ConfigureAwait(false);
                string gotMake = (await Cap(exif, $"-q -s -Make \"{prod}\"").ConfigureAwait(false))
                                    .Split(':').Last().Trim();
                string gotModel = (await Cap(exif, $"-q -s -Model \"{prod}\"").ConfigureAwait(false))
                                    .Split(':').Last().Trim();
                // ⚠ 排除式**不生效**时 exiftool 返回退出码 0（它只是照抄了标签），所以 rc 不是证据。
                //   控制臂：同一次调用里必须看到 Make/Model 到了产物（M2）——M1 只有在 M2 成立时才有意义。
                Console.WriteLine($"orientmeta-full  rc={rc} Orientation=「{gotOri}」 Make=「{gotMake}」 Model=「{gotModel}」");
                Check(rc == 0, $"M0 恢复调用退出码 0（实得 {rc}）");
                Check(gotOri == "",
                    $"M1 全量复制（-all:all）后产物**不得**带 Orientation（实得「{gotOri}」）"
                  + $"⇒ 修前实测这里就是 8 ⇒ 查看器二次旋转");
                Check(gotMake == "TESTCAM" && gotModel == "TESTMODEL",
                    $"M2 同一次复制必须仍把 Make/Model 带过去（实得 {gotMake}/{gotModel}）"
                  + $"⇒ 证明排除是**逐标签**的，不是把整块复制掐了（否则 M1 会假绿）");

                // ── M3：安全模式同判据 ──
                string prod2 = Path.Combine(dir, "prod_upright_safe.jpg"); created.Add(prod2);
                await Run(ff, $"-y -hide_banner -loglevel error -i \"{src}\" -frames:v 1 -q:v 3 \"{prod2}\"")
                      .ConfigureAwait(false);
                int rc2 = await FfmpegGui.Services.ExifToolService.CopyMetadataSafeAsync(
                    src, prod2, null, absorb: null).ConfigureAwait(false);
                string got2 = await OrientationOf(prod2).ConfigureAwait(false);
                string mk2 = (await Cap(exif, $"-q -s -Make \"{prod2}\"").ConfigureAwait(false))
                                .Split(':').Last().Trim();
                Console.WriteLine($"orientmeta-safe  rc={rc2} Orientation=「{got2}」 Make=「{mk2}」");
                Check(got2 == "" && mk2 == "TESTCAM",
                    $"M3 安全模式（-EXIF:all）同样剥掉 Orientation 且仍带 Make"
                  + $"（实得 Orientation=「{got2}」 Make=「{mk2}」）⇒ 两条恢复通路都得修，漏一条等于没修");

                // ── M4：反证臂——免解码重封装必须**保留**标签（判据有牙的唯一证据）──
                string prod3 = Path.Combine(dir, "prod_rewrap.jpg"); created.Add(prod3);
                await Run(ff, $"-y -hide_banner -loglevel error -i \"{src}\" -frames:v 1 -q:v 3 \"{prod3}\"")
                      .ConfigureAwait(false);
                int rc3 = await FfmpegGui.Services.ExifToolService.CopyMetadataAsync(
                    src, prod3, null, absorb: null, dropOrientation: false).ConfigureAwait(false);
                string got3 = await OrientationOf(prod3).ConfigureAwait(false);
                Console.WriteLine($"orientmeta-rewrap rc={rc3} Orientation=「{got3}」");
                Check(got3 == "8",
                    $"M4 传 dropOrientation:false（重封装路径）时标签必须**留得住**（实得「{got3}」）"
                  + $"⇒ 若这条也变空，说明排除是无条件的 ⇒ 会把真需要取向提示的免解码产物做坏");

                // ── M5：单一判据的行为面 ──
                var oRewrap = new FfmpegGui.Models.FfmpegOptions { Format = "jxl", JxlLosslessJpeg = true };
                var oPlain = new FfmpegGui.Models.FfmpegOptions { Format = "jxl", JxlLosslessJpeg = false };
                bool jpgOn = FfmpegGui.Services.CjxlService.UsesLosslessJpegRewrap(src, oRewrap);
                bool jpgOff = FfmpegGui.Services.CjxlService.UsesLosslessJpegRewrap(src, oPlain);
                bool tifOn = FfmpegGui.Services.CjxlService.UsesLosslessJpegRewrap(
                    Path.Combine(dir, "x.tif"), oRewrap);
                Console.WriteLine($"orientmeta-predicate jpg/on={jpgOn} jpg/off={jpgOff} tif/on={tifOn}");
                Check(jpgOn && !jpgOff && !tifOn,
                    $"M5 重封装判据三格：JPEG+开=true / JPEG+关=false / 非 JPEG+开=false"
                  + $"（实得 {jpgOn}/{jpgOff}/{tifOn}）⇒ 只有这一份实现能同时喂 cjxl 命令行与恢复步骤");
            }
            catch (Exception ex)
            {
                Check(false, $"orientmeta 探针异常：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                int total = 0;
                foreach (var p in created) { try { if (File.Exists(p)) total++; } catch { } }
                int removed = 0;
                foreach (var p in created) { try { if (File.Exists(p)) { File.Delete(p); removed++; } } catch { } }
                bool dirGone = false;
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    { Directory.Delete(dir); dirGone = true; }
                }
                catch { }
                Console.WriteLine($"orientmeta-cleanup: 删除文件 {removed}/{total}，目录{(dirGone ? "已" : "未")}删除");
            }
        }
        private static async System.Threading.Tasks.Task ProbeFirstframe(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: ServiceProbe firstframe <input> [outDir]");
                Fail(); return;
            }
            string input = args[1];
            if (!File.Exists(input)) { Check(false, $"firstframe：输入不存在 {input}"); return; }
            string ff = AppSettingsService.Current.FfmpegPath;
            if (string.IsNullOrWhiteSpace(ff) || !File.Exists(ff))
            { Check(false, "firstframe：未找到 ffmpeg（AppSettings 未初始化 / publish/PLAN 缺失）"); return; }

            const string ScaleMarker = "几何缩放（最长边限制）";     // RawColorPipeline.cs:145 的原样前缀
            string root = args.Length >= 3 ? args[2] : "tests/output/validate";
            string dir = Path.Combine(root, "firstframe_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Console.WriteLine($"firstframe-out={dir}");
            var created = new List<string>();

            try
            {
                var (w0, h0) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                    .ProbeSizeAsync(input, ff).ConfigureAwait(false);
                Check(w0 > 0 && h0 > 0, $"firstframe：能探测输入尺寸（{w0}x{h0}）");
                if (w0 <= 0 || h0 <= 0) return;

                // 最长边限制 = **较长边的一半**（保证超限 ⇒ 缩放段真的跑；至少 2，`-2` 取偶的下限）。
                // ⚠ 期望的另一条边**不在这里算**（`-2` 的取偶会与手算漂开，见 geometry 的 42x32 坑）——
                //   改由下面那条**单帧参照**给出独立观测量。
                int maxDim = Math.Max(2, Math.Max(w0, h0) / 2);
                var o = new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "png", ColorStrategy = ST.Recommended, BitDepth = 8,
                    EnableMaxDimension = true, MaxDimension = maxDim,
                };

                // ⚠ 风险②：必须断言 filter 非空 —— 否则输入没超限 ⇒ 缩放段不跑 ⇒ 尺寸断言**真空绿**。
                string? filter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(o, input);
                Console.WriteLine($"firstframe-filter \"{filter ?? "(null)"}\"");
                Check(!string.IsNullOrEmpty(filter),
                    $"firstframe：输入确实超限 ⇒ 缩放滤镜非空（filter={filter ?? "(null)"}；为空则本 mode 无判别力）");
                if (string.IsNullOrEmpty(filter)) return;

                var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, input, out var why);
                if (it == null) { Check(false, $"firstframe：选项→意图失败：{why}"); return; }
                var plan = CE.Plan(it);
                if (!plan.IsOk) { Check(false, $"firstframe：计划被拒：{plan.Reason}"); return; }

                // ── 单帧参照：**同一滤镜**、只取首帧 ⇒ "不竖叠时另一条边应是多少"的独立观测量 ──
                // ⚠ 这条命令**自己带 `-frames:v 1`**：不带就会踩同一个 image2 多帧缺陷
                //   （`QueueProcessor` 的预缩放块正是缺它才失败，与本 mode 要锁的东西互为镜像）。
                string refPng = Path.Combine(dir, "firstframe_ref.png");
                created.Add(refPng);
                string refArgs = $"-y -hide_banner -loglevel error -i \"{input}\" -frames:v 1 -vf \"{filter}\" "
                               + $"-compression_level 0 \"{refPng}\"";
                Console.WriteLine($"firstframe-ref {refArgs}");
                int refEc = await FfmpegGui.Services.FfmpegRunner.RunAsync(refArgs, null, ff).ConfigureAwait(false);
                int rw = 0, rh = 0;
                if (refEc == 0 && File.Exists(refPng))
                {
                    var rr = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                        .ProbeSizeAsync(refPng, ff).ConfigureAwait(false);
                    rw = rr.w; rh = rr.h;
                }
                Check(refEc == 0 && rw > 0 && rh > 0,
                    $"firstframe：单帧参照产出可用（exit {refEc}，{rw}x{rh}）—— 期望尺寸的**独立**来源");

                // ── 被测：引擎主解码（多帧输入 + 超限 ⇒ 走 `RawColorPipeline.cs:126-128`）──
                string outPng = Path.Combine(dir, "firstframe_out.png");
                created.Add(outPng);
                var sb = new StringBuilder();
                var gate = new object();
                FfmpegGui.Services.ColorMapping.RawColorPipeline.Stats st;
                try
                {
                    st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                        input, outPng, plan.ToSpec(), plan,
                        s => { lock (gate) sb.Append(s); }, default, 1, ff, o).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Check(false, $"firstframe：引擎异常：{ex.GetType().Name}: {ex.Message}");
                    return;
                }
                string log = sb.ToString();

                // ── 载体分派：解码步 vs 编码步（见方法头）──
                string? dec = null, enc = null;
                var rejects = new List<string>();
                foreach (var raw in log.Split('\n'))
                {
                    if (!raw.Contains("[色彩] ffmpeg ")) continue;
                    if (!raw.Contains("-f rawvideo")) continue;
                    string body = raw.Substring(raw.IndexOf("[色彩] ffmpeg ", StringComparison.Ordinal)
                                              + "[色彩] ffmpeg ".Length);
                    int iRaw = body.IndexOf("-f rawvideo", StringComparison.Ordinal);
                    int iIn = body.IndexOf("-i ", StringComparison.Ordinal);
                    bool encShape = iIn < 0 || iRaw < iIn;
                    if (encShape)
                    {
                        if (enc == null) enc = body;
                        continue;
                    }
                    // ⚠ 自检：解码步候选里**不得**出现编码特征（选错载体 ⇒ 断言恒真）
                    if (body.Contains("-video_size") || body.Contains("image2"))
                    { rejects.Add("含编码特征(-video_size/image2)"); continue; }
                    if (dec == null) dec = body;
                }
                Console.WriteLine($"firstframe-cmd {dec ?? "(未取到)"}");
                Console.WriteLine($"firstframe-enc {enc ?? "(未取到)"}");
                foreach (var rj in rejects) Console.WriteLine($"firstframe-reject {rj}");
                Check(dec != null, "firstframe：从引擎日志里取到**解码步**命令行（载体已显式分派，拒绝项见 firstframe-reject）");
                Check(enc != null, "firstframe：同时取到**编码步**命令行（供门禁复读 -video_size 做自洽断言）");

                int iV1 = -1, iInDec = -1;
                if (dec != null)
                {
                    iV1 = dec.IndexOf("-frames:v 1", StringComparison.Ordinal);
                    iInDec = dec.IndexOf("-i ", StringComparison.Ordinal);
                }
                bool v1AfterI = iV1 >= 0 && iInDec >= 0 && iV1 > iInDec;
                Check(v1AfterI,
                    $"firstframe：解码步的 `-frames:v 1` 落在 `-i` **之后**（下标 {iV1} > {iInDec}）"
                  + " —— 它是**输出侧**选项，放 `-i` 之前会被当输入选项而无效");

                var (ew, eh) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                    .ProbeSizeAsync(outPng, ff).ConfigureAwait(false);
                bool lockedOk = (w0 >= h0 ? ew : eh) == maxDim;
                Console.WriteLine($"firstframe-case input={Path.GetFileName(input)} src={w0}x{h0} maxdim={maxDim} "
                    + $"engine={ew}x{eh} stats={st.Width}x{st.Height} ref={rw}x{rh} "
                    + $"log={(log.Contains(ScaleMarker) ? "scaled" : "none")} "
                    + $"framesv1={(iV1 >= 0 ? "yes" : "no")} v1afteri={(v1AfterI ? "yes" : "no")}");

                Check(lockedOk, $"firstframe：产物**锁定的那条边** == MaxDimension（{ew}x{eh}，maxdim={maxDim}）");
                // ⭐ 本 mode 的**主判据**：产物 == 单帧参照 ⇒ 解码步只取了一帧。
                //   若缺 `-frames:v 1`，N 帧竖叠 ⇒ 另一条边变成 N 倍 ⇒ 与参照**不等**。
                Check(ew == rw && eh == rh,
                    $"firstframe：产物 == 单帧参照（{ew}x{eh} vs {rw}x{rh}）⇒ 解码步**只取首帧**"
                  + "（缺 `-frames:v 1` 时另一条边会变成 N 倍，如 128x96 → 128x960）");
                Check(st.Width == ew && st.Height == eh,
                    $"firstframe：Stats 反推尺寸 == 实际产物（{st.Width}x{st.Height} vs {ew}x{eh}）");
                Check(log.Contains(ScaleMarker),
                    $"firstframe：日志出现「{ScaleMarker}」⇒ 引擎几何缩放段确实执行（否则尺寸断言无判别力）");
            }
            finally
            {
                // 结尾自清（按计数）：文件逐个删，目录空才删（宿主批量删除守卫会拦整目录删）。
                // ⚠ 分母只算**清理时真实存在**的文件（照 geometry 的实测修正：中途异常时某些产物
                //   根本没被创建，拿 created.Count 当分母会报出「N/M」这种**假残留**）。
                int total = 0;
                foreach (var p in created) { try { if (File.Exists(p)) total++; } catch { } }
                int removed = 0;
                foreach (var p in created) { try { if (File.Exists(p)) { File.Delete(p); removed++; } } catch { } }
                bool dirGone = false;
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    { Directory.Delete(dir); dirGone = true; }
                }
                catch { }
                Console.WriteLine($"firstframe-cleanup: 删除文件 {removed}/{total}，目录{(dirGone ? "已" : "未")}删除");
            }
        }
    }
}
