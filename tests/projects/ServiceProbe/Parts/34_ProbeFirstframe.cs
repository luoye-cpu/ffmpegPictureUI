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
        // ═══════════════════ firstframe：引擎「多帧输入只取首帧」的直调载体 ═══════════════════

        /// <summary>
        /// firstframe &lt;input&gt; [输出根目录] —— **直调引擎**，验证 <c>RawColorPipeline</c> 的解码步
        /// 对多帧输入**只取首帧**（`-frames:v 1`），而不是把 N 帧顺序写进同一条 rawvideo
        /// （后者会让 `h = px/w` 反推出 `h = N×单帧高`，而完整性检查恰好成立 ⇒ **静默产出竖叠错图**）。
        ///
        /// <para>
        /// <b>为什么需要这个 mode</b>（2026-09-18，`#136` 切片 C）：<c>verify-engine-firstframe.ps1</c>
        /// 原先拿**完整 CLI**（`--color-engine engine`）喂动图 webp，靠"引擎被路由到"才走得到那条解码命令。
        /// 而 `#136` 拆轴后「动画输入 + 显式 engine」会变成 <c>exit 90</c> 硬失败
        /// （<c>ColorEngineRouter.BlockReason</c> 的 <c>AnimatedInput</c> 分支 + <c>ExplicitlyRequested</c>）
        /// ⇒ CLI 载体**整体失效**。本 mode 绕过路由层直调 <c>TransformFileAsync</c>，
        /// 让这条守卫在**行为改变之前**就先站住（先换载体、再改行为）。
        /// </para>
        ///
        /// <para>
        /// <b>为什么必须传 <c>options</c></b>：<c>RawColorPipeline.cs:94-95</c> 是
        /// <c>scaleFilter = options != null ? BuildMaxDimensionScaleFilter(options, inputPath) : null</c>
        /// ⇒ 不传 options 就**跑不到**几何缩放段，也就复现不了「N 帧竖叠」这个缺陷形态
        /// （同文件的 <c>bitdepth</c> 就是 <c>options=null</c>，故它当不了本项载体）。
        /// </para>
        ///
        /// <para>
        /// <b>载体分派（§6 第 74 条：载体选错 ⇒ 断言恒真，比恒红更危险）</b>：引擎日志里有**两条**
        /// <c>-f rawvideo</c> —— 解码步（<c>RawColorPipeline.cs:126-128</c>，<c>-i</c> 在 <c>-f rawvideo</c> **之前**）
        /// 与编码步（<c>BuildEncodeArgs:793</c> / <c>WriteRgb48ToPngAsync:1054</c>，<c>-f rawvideo</c> 在前且带
        /// <c>-video_size</c>）。本 mode 按「<c>-f rawvideo</c> 的下标 <b>&gt;</b> <c>-i</c> 的下标」显式分派，
        /// 并把**编码特征**（<c>-video_size</c> / <c>image2</c>）作为**拒绝条件**；被拒的候选会打
        /// <c>firstframe-reject</c> 供人核对。
        /// </para>
        ///
        /// <para>
        /// 打印的结构化行：<c>firstframe-out=</c> / <c>firstframe-filter</c> / <c>firstframe-cmd</c>（解码步）/
        /// <c>firstframe-enc</c>（编码步，供门禁复读 <c>-video_size</c> 做"引擎自报尺寸 == 产物尺寸"）/
        /// <c>firstframe-ref</c>（**单帧参照**：同一滤镜只取首帧缩放，给出"不竖叠时另一条边应是多少"的
        /// **独立观测量** —— 不靠门禁自己算，因为 <c>-2</c> 的取偶会与手算漂开）/
        /// <c>firstframe-case</c> / <c>firstframe-cleanup:</c>。输出走 GUID 运行级隔离，结尾按计数自清。
        /// </para>
        /// </summary>
        private static async System.Threading.Tasks.Task ProbeFirstframe(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: ServiceProbe firstframe <input> [outDir]");
                _fail++; return;
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
