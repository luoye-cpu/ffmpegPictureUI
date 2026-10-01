using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 统一 raw 管道（引擎 L4 的进程外侧）：解码 → <b>引擎色彩变换</b> → 编码。
///
/// 这是"接管所有色彩映射"的落地形态：ffmpeg 只负责**解码/编码与像素格式**，
/// 一切曲线与色度映射都在本进程内由 <see cref="ColorKernels"/> 完成。
///
/// ⚠️ 唯一色彩注入点约定：本文件之外不得再向 raw 管道里塞 zscale 的 `p=`/`t=` 色彩变换
/// （历史上"两处映射入口漏改"就是这个原因）。zscale 仍可作为 H2 后端整体使用，
/// 但那种情况下**不经过**本管道（由 Plan 的 Backend 决定，二者互斥）。
/// </summary>
public static class RawColorPipeline
{
    /// <summary>一次运行的统计（写进日志，供性能回归 diff）。</summary>
    public sealed class Stats
    {
        public int Width, Height;
        public long ElapsedMs;
        public long DecodeMs, ProcessMs, EncodeMs;
        public long PeakManagedBytes;
        public string Scheduler = "";
        /// <summary>P4c：执行层**原始实测**内容峰值（nits，未经钳制）；0 = 未实测（用户指定或元数据已足够）。
        /// 采用值（钳制后）看 <c>spec.Tone.PeakNits</c> 与日志，两者分开才能分别审计“测得对不对”与“策略采得对不对”。</summary>
        public double MeasuredPeakNits;
        public double MPixPerSecond => ElapsedMs > 0 ? (double)Width * Height / 1_000_000.0 / (ElapsedMs / 1000.0) : 0;
        public double ProcessMPixPerSecond => ProcessMs > 0 ? (double)Width * Height / 1_000_000.0 / (ProcessMs / 1000.0) : 0;
    }

    /// <summary>
    /// 把 inputPath 解码为 rgb48le，按 spec 变换，再交给 outputPathExt 对应的编码器写出。
    /// 返回统计数据；失败抛异常（调用方按"引擎失败 → 回退 legacy"策略处理）。
    /// </summary>
    public static async Task<Stats> TransformFileAsync(
        string inputPath, string outputPath, TransformSpec spec, ColorTransformPlan plan,
        Action<string>? log = null, CancellationToken ct = default, int concurrency = 1, string? ffmpegOverride = null,
        Models.FfmpegOptions? options = null)
    {
        var ffmpeg = ffmpegOverride ?? AppSettingsService.Current.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            throw new InvalidOperationException("色彩引擎需要 ffmpeg 做解码/编码，但未找到 ffmpeg");

        // ── GainMap（Ultra HDR JPEG）出口 ──
        // 与常规出口的差别不在"色彩"，而在**输出结构**：产物是"基础图 + 增益图 + MPF/XMP"的容器，
        // 由纯托管的 GainMapEncoder 打包，不经 ffmpeg 编码器。因此解码、变换、编码三步都与常规出口不同
        // ⇒ 在入口处分流，避免把 GainMap 硬塞进 rgb48le 那条整数通路（那条通路没有 >1.0 的余量，
        //   而 GainMap 的输入契约要求线性 HDR 浮点，1.0 = 峰值 nits）。
        if (plan.GainMap)
            return await TransformGainMapAsync(inputPath, outputPath, plan, options, log, ct, ffmpeg).ConfigureAwait(false);

        // ── JXL 出口的 ICC 能力诚实门（2026-09-17 接入 JXL 时新增）──
        // **本出口**（ffmpeg 的 libjxl）写不了 ICC，两条路都不通（均为实测）：
        //  ① 编码器丢弃：`-vf iccgen` 生成的 ICC **不落盘**（同一份 raw 用 legacy 参照
        //     `ref.jxl` 与引擎出口产物，exiftool 都读不出 ICC）；
        //  ② 后置嵌入被拒：`exiftool "-icc_profile<=p3.icc" -overwrite_original w.jxl`
        //     ⇒ 退出码 **1**，`Error: [minor] Will wrap JXL codestream in ISO BMFF container for writing`、
        //     `0 image files updated`（同一命令写 PNG 成功 584B）⇒ 是 JXL 容器限制，不是命令写错。
        // 计划层给 jxl 置 AttachIcc 是**按格式能力表**（`CanCarryArbitraryIcc=true`，对 cjxl 后端成立
        // —— 它用 `-x icc_pathname=` 落盘），而**本出口**兑现不了 ⇒ 必须在执行体这一层拒绝。
        // 不拒的后果：后置步骤失败后产物**无任何色彩描述**（jxl 的 CICP 也被择一规则清掉了），
        // 正是历史偏色根因（"看起来配置过、产物却没描述"）。拒绝优于静默。
        // 注：`CanCarryArbitraryIcc=true` 对 jxl 这个**格式**并没有错（cjxl 后端确实能写 ICC），
        // 所以这里不是"能力表写错了"，而是"本出口比格式能力窄"—— 这正是它必须落在执行体而非规划层的原因。
        var outExt = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        // ⚠ **按出口分叉，不能按扩展名判**（2026-09-17 接入 cjxl 出口时更正）：
        //   上面那些实测结论全部只对 **ffmpeg 的 libjxl 出口**成立。**cjxl 出口**（`ExternalEncoderExit`）
        //   走的是另一条路：`-x icc_pathname=<file>` 由 cjxl **自己**把 ICC 写进产物（实测可用，
        //   且这正是 `CanCarryArbitraryIcc=true` 这条格式能力在 cjxl 后端下的兑现方式）。
        //   若仍按扩展名判，cjxl 出口会被自己的"格式是 jxl"挡住 ⇒ 明明能写 ICC 却拒绝，
        //   等于把"出口能力"错当成"格式能力"（两者不等宽，正是本门必须落在执行体、且必须分叉的原因）。
        if (outExt == "jxl" && plan.AttachIcc && !plan.ExternalEncoderExit)
            throw new InvalidOperationException(
                "JXL 出口无法附 ICC（ffmpeg 的 libjxl 编码器丢弃 iccgen 的 ICC，exiftool 亦拒写 JXL 容器）："
                + "请改用可 CICP 命名的目标空间（sRGB / Display P3 / Rec.2100 PQ / HLG），"
                + "或改用 PNG/TIFF/WebP 出口携带 ICC；若要保留任意 ICC 请改用 cjxl 后端");

        // w0/h0 = **显示**尺寸（含 autorotate 后的轴向），见 ProbeSizeAsync 的注释：解码段不写
        // `-noautorotate`，所以只有这个口径能与后面 rawvideo 的真实排布对上。
        var (w0, h0) = await ProbeSizeAsync(inputPath, ffmpeg, ct).ConfigureAwait(false);
        if (w0 <= 0 || h0 <= 0) throw new InvalidOperationException("无法获取图像尺寸");

        // ── 几何缩放（最长边限制）：引擎自接，不再因此拒绝任务 ──
        // 滤镜串复用传统管线的**同一个** `FfmpegCommandBuilder.BuildMaxDimensionScaleFilter`
        // （单一真值：超限判定 / 锁最长边 / `-2` 自动偶数 / lanczos 都在那里）⇒ 两条管线的几何口径
        // 不会各写一份。返回 null = 未启用 / 未超限 / 宽高探测失败 ⇒ 不缩放。
        // 放在**解码**这一步：变换与编码直接按缩放后的尺寸做，省掉全尺寸像素的往返。
        // ⚠ 与 legacy 的顺序差异（legacy =「色彩变换 → 缩放」，此处 =「缩放 → 色彩变换」）已登记：
        //   二者只在「降采样 × 非线性曲线」的交换律上差二阶量，不影响可读性与标注正确性。
        var scaleFilter = options != null
            ? FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(options, inputPath) : null;
        bool scaled = !string.IsNullOrEmpty(scaleFilter);
        string vf = scaled ? $"format=rgb48le,{scaleFilter}" : "format=rgb48le";
        // 缩放后尺寸：**锁定的那条边恒 = MaxDimension**（同一滤镜语义，legacy 注释已实测“不漂移”），
        // 另一条只能由**解码产物长度**反推 —— `-2` 那条边由 ffmpeg 定偶数（实测 512x384 限 32 时
        // 纵向 `scale=-2:32` 得 42x32，按 512*32/384=42.67 自己算会取到 43/44），自己算必与它漂开。
        // rawvideo 无行填充 ⇒ 长度 = w*h*6 精确成立，反推是唯一可靠来源。
        int w = scaled ? (w0 >= h0 ? options!.MaxDimension : 0) : w0;
        int h = scaled ? (w0 >= h0 ? 0 : options!.MaxDimension) : h0;
        var st = new Stats { Width = w, Height = h };
        var sw = Stopwatch.StartNew();
        var tmp = Path.Combine(PlatformServices.GetTempDir(), $"cms_{Guid.NewGuid():N}.rgb48");
        var tmpOut = tmp + ".out";

        try
        {
            // ── 解码：只让 ffmpeg 做容器/像素格式（+ 最长边缩放）工作，不碰色彩 ──
            //   注意不写 `zscale=...` 之类滤镜，避免隐式色彩转换。
            // ⚠ `-frames:v 1` **不可省**（2026-09-18 实测缺陷）：本管道的输入契约是**单帧**（rgb48le 一条 raw），
            //   而多帧容器（动图 webp/gif/apng…）不加它会**把 N 帧顺序写进同一条 rawvideo** ⇒ `rawLen = N×w×h×6`。
            //   实测（anim_webp.webp 256×192 / 10 帧 + `--color-engine engine --color-space BT.2020
            //   --max-dimension-enabled true --max-dimension 128`）：产物 **128x960**（10 帧竖着叠成一张图）、
            //   **exit 0**、日志报 `几何缩放：256x192 → 128x960`。这不是"丢帧"，是**产出一张用户看不出错的错图**。
            //   ⚠ 为什么下面 `:122-126` 的反推守卫**拦不住**：h 是**反推**出来的（`h = px/w`），
            //   在 h=960 下完整性检查 `(long)w*h*6 == rawLen` **恰好成立** ⇒ 静默通过。
            //   ⚠ 2026-09-18 更正（原注释的举例等式 `128×960×6 == 10×256×192×6` 是**假**的）：
            //   缩放是**逐帧**的，故 `rawLen` 的正确分解是 `10×128×96×6 == 737280`（10 帧 × **缩放后**单帧
            //   128×96），**不是** `10×256×192×6 == 2949120`（原始尺寸）；由此 `h = px/w = 737280/6/128 = 960`
            //   = `10 × 缩放后单帧高(96)` ⇒ 「另一条边被乘 N」这个机制成立，只是**乘的是缩放后的高**。
            //   加 `-frames:v 1` 后 rawLen 恒等于单帧字节数，反推守卫才真正成为守卫（见其上的尺寸推算）。
            //   放在 `-i` **之后**：`-frames:v` 是**输出侧**选项（限制写出的帧数）；放 `-i` 之前会被当成输入选项而无效。
            // ⚠ 2026-09-18 决定：本处**不**补上界/比例断言。理由：加 `-frames:v 1` 后 rawLen 恒为单帧字节数，
            //   本判据已由「恰好成立的巧合」变成**真守卫**；常数上界会误杀合法的高瘦图（如 128×4096）；
            //   唯一非任意的 `h ≤ h0` 在 `scale=-2` 偶数取整下对退化输入会取到 2 > 1（如 3x1 → 2x2）⇒ 引入假红。
            //   守卫放**门禁层**（`verify-engine-firstframe.ps1`；变异实测：去掉 `-frames:v 1` ⇒ 产物 128x960 ⇒ 转红）。
            var dec = await RunToEndAsync(ffmpeg,
                $"-y -hide_banner -loglevel error -i \"{inputPath}\" -frames:v 1 -vf \"{vf}\" -f rawvideo \"{tmp}\"",
                log, ct).ConfigureAwait(false);
            st.DecodeMs = sw.ElapsedMilliseconds;
            if (dec != 0 || !File.Exists(tmp))
                throw new InvalidOperationException($"解码失败 (exit {dec})");
            long rawLen = new FileInfo(tmp).Length;
            if (scaled)
            {
                long px = rawLen / 6;
                if (w == 0) w = checked((int)(px / h)); else h = checked((int)(px / w));
                if (px <= 0 || (long)w * h * 6 != rawLen)
                    throw new InvalidOperationException(
                        $"几何缩放后尺寸推算失败（raw {rawLen}B ⇒ 推算 {w}x{h}，长度非 w*h*6 的整数倍）");
                st.Width = w; st.Height = h;
                log?.Invoke($"[色彩] 几何缩放（最长边限制）：{w0}x{h0} → {w}x{h}（{scaleFilter}）\n");
            }
            else if (rawLen != (long)w * h * 6)
                // ⚠ 必须是**精确等式**而不是下界（2026-09-30）：`rawLen < w*h*6` 这种写法对
                //   "几何口径取错"是**盲的** —— coded 6000×4000 与旋正后 4000×6000 的字节数**完全相同**，
                //   下界判据照样放行，产物却已被按行错位重排（实测 PSNR 7.97 dB）。
                //   解码段带 `-frames:v 1` ⇒ 一帧 rgb48le 的长度就是 `w*h*6`，多一字节都是异常。
                throw new InvalidOperationException(
                    $"解码产物与显示尺寸不符（{rawLen}B ≠ {w}×{h}×6={(long)w * h * 6}B）⇒ 几何口径不可信");

            var sched = ColorScheduler.ForItem(concurrency, w, h, 6);        // rgb48le = 6 B/px
            st.Scheduler = sched.ToLogFragment();
            PlatformServices.MarkAsTemporaryFile(tmp);

            // ── 变换（引擎）──
            var sw2 = Stopwatch.StartNew();
            var (stSrc, stDst) = TablesFor(spec, w * h);
            // 峰值最后一档（P4c）：规划层既无用户值也无容器静态元数据时留下 MeasurePeak 标记，
            // 在这里用**已解码的真实像素**替掉名义兜底（静像几乎全不带 MaxCLL，实测）。
            if (spec.Tone is { MeasurePeak: true })
                ApplyMeasuredPeak(spec, st,
                    await MeasureContentPeakNitsAsync(tmp, w * h, spec, stSrc, ct).ConfigureAwait(false), log);
            bool out8 = plan.OutBitDepth == 8;          // 出口位深由 Plan 真驱动（解码固定 rgb48le，降位只发生在变换那一次遍历）
            if (sched.Streaming)
                await TransformStreamedAsync(tmp, tmpOut, spec, w, h, sched, ct, stSrc, stDst, out8).ConfigureAwait(false);
            else
                await TransformWholeFrameAsync(tmp, tmpOut, spec, w, h, sched, ct, stSrc, stDst, out8).ConfigureAwait(false);
            st.ProcessMs = sw2.ElapsedMilliseconds;
            st.PeakManagedBytes = GC.GetTotalAllocatedBytes(true);
            if (log != null)
            {
                var p = new ProcessInfo();
                // 源/目标描述符必须进日志：`item.Command` 里的同一信息在 headless 日志中**不回显**，
                // 而「源是怎么被认定的」（ICC 度量 / CICP 标签 / 惯例假定 / --icc-file）是审计
                // “这次转换的起点对不对”的唯一凭据（描述符 Name 自带来源标记，如 `[--icc-file]`）。
                log($"[色彩] 源={plan.Src?.Name ?? "?"}（{plan.Src?.Confidence.ToString() ?? "-"}）"
                  + $" 目标={plan.Dst?.Name ?? "auto（看齐源）"}\n"
                  + $"[色彩] {plan.Reason}\n[色彩] {sched.ToLogFragment()} 变换耗时 {st.ProcessMs}ms " +
                    $"吞吐 {st.ProcessMPixPerSecond:F1} MPix/s\n");
            }

            // ── 编码：把变换后的 rgb48le 喂回去，色彩标注来自 Plan ──
            // ⚠ **出口分叉点**（2026-09-17）：cjxl 出口只换这一段，解码段/变换段完全共用。
            var sw3 = Stopwatch.StartNew();
            int ec;
            // ⚠ 出口分叉要**先分 jxr 再分 jxl**：两者都置 `ExternalEncoderExit`，但编码器完全不同
            //   （jxr 恒走 JxrEncApp；jxl 视后端而定）。按扩展名二次分叉，而不是给计划层新开一个位
            //   —— `ExternalEncoderExit` 的语义就是「编码由引擎内的外部编码器出口完成」，对两者都成立。
            if (plan.ExternalEncoderExit && outExt == "jxr")
            {
                ec = await EncodeJxrViaJxrEncAppAsync(inputPath, tmpOut, outputPath, plan, options, w, h, log, ct)
                        .ConfigureAwait(false);
            }
            else if (plan.ExternalEncoderExit)
            {
                ec = await EncodeJxlViaCjxlAsync(inputPath, tmpOut, outputPath, plan, options, w, h, log, ct)
                        .ConfigureAwait(false);
            }
            else
            {
                var encArgs = BuildEncodeArgs(inputPath, tmpOut, outputPath, plan, options, w, h);
                ec = await RunToEndAsync(ffmpeg, encArgs, log, ct).ConfigureAwait(false);
            }
            st.EncodeMs = sw3.ElapsedMilliseconds;
            if (ec != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException(plan.ExternalEncoderExit
                    ? $"{(outExt == "jxr" ? "JxrEncApp" : "cjxl")} 编码失败 (exit {ec})"
                    : $"编码失败 (exit {ec})");
            st.ElapsedMs = sw.ElapsedMilliseconds;
            return st;
        }
        finally
        {
            TryDelete(tmp); TryDelete(tmpOut);
        }
    }

    /// <summary>
    /// GainMap（Ultra HDR JPEG）出口：**线性 HDR 浮点像素 → GainMapEncoder → XMP/MPF 打包**。
    ///
    /// 与常规出口的三点不同，以及各自的理由：
    /// <list type="number">
    /// <item><b>解码用 zscale 线性化 + gbrpf32le</b>（而不是 rgb48le）：GainMapEncoder 的输入契约是
    /// "线性、1.0 = hdrPeakNits"的浮点像素，rgb48le 是整数且没有 >1.0 的余量。
    /// 滤镜串与参数来自 <see cref="GainMapJpegCodec"/> —— 与旧专用管线**同一份**，不另写。</item>
    /// <item><b>变换步不做任何色彩映射</b>：底图色域矩阵、分段 Reinhard 色调映射、增益图计算全部在
    /// GainMapEncoder 内部完成。此处若再做一次映射就是**双重施加**（色彩被改两次，且第二次是错的）。</item>
    /// <item><b>编码走 GainMapEncoder，跳过 ffmpeg 与 BuildEncodeArgs</b>：产物不是普通 JPEG，
    /// 标签（-color_primaries 等）由容器内的底图 ICC / ISO 元数据承担，ffmpeg 参数无从表达。</item>
    /// </list>
    ///
    /// 几何缩放（最长边限制）：**本出口不承接**，命中即显式抛异常（调用方按"引擎失败"处理，
    /// 不会静默产出一个底图与增益图尺寸不匹配的容器）。理由：增益图是按底图尺寸降采样的，
    /// 两者几何强耦合（MPF 偏移亦然），把 scale 插进这条链是一条尚未被任何门禁覆盖的新路径。
    /// 路由层（<see cref="ColorEngineRouter.UnconsumedOutputSettings"/>）已先行拦截该组合，
    /// 这里只是防止绕过路由直接调本方法。
    ///
    /// 失败语义与 <see cref="TransformFileAsync"/> 一致：抛异常 ⇒ 调用方按"引擎失败 → 回退"处理。
    /// </summary>
    private static async Task<Stats> TransformGainMapAsync(string inputPath, string outputPath,
        ColorTransformPlan plan, Models.FfmpegOptions? options, Action<string>? log, CancellationToken ct, string ffmpeg)
    {
        if (options == null)
            throw new InvalidOperationException(
                "GainMap 出口需要 FfmpegOptions（底图色域 / 质量 / 降采样 / 目标峰值都只存在于其中）");

        var scaleFilter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(options, inputPath);
        if (!string.IsNullOrEmpty(scaleFilter))
            // ⚠ 理由措辞与 `ColorEngineRouter` 的 `EngineBlockCodes.GeometryWithGainMap` **必须一致**（同一条限制的两个出口）。
            //   收窄后不再说「该组合尚未验证」—— 引擎**普通出口**的几何缩放**已验证**（`ServiceProbe geometry` + `verify-geometry-engine`），
            //   未验证的是**这条 GainMap 链**：scale 要插进下面的线性解码串，底图与增益图必须同几何 ⇒ 属**新链**，不是"没测过"。
            throw new InvalidOperationException(
                $"GainMap 出口不承接最长边几何缩放（{scaleFilter}）：底图与增益图必须同几何，scale 未接入本出口的线性解码链");

        // 可用性诚实：GainMap 用纯托管编码 + 所选 JPEG 后端（cjpegli 优先，否则 ffmpeg mjpeg）；
        // 两者皆不可用才失败，绝不静默产出一个没有增益图的普通 JPEG。
        if (!CjpegliService.IsAvailable && (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)))
            throw new InvalidOperationException("GainMap 需要 cjpegli 或 ffmpeg(mjpeg) 之一，两者均不可用");

        var (w, h) = await ProbeSizeAsync(inputPath, ffmpeg, ct).ConfigureAwait(false);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("无法获取图像尺寸");

        var ep = GainMapJpegCodec.ResolveEncodeParams(options, inputPath);
        log?.Invoke($"[GainMap]   源峰值 {ep.PeakNits:F0} nits（来源：{ep.PeakSourceText}）\n");
        log?.Invoke($"[GainMap]   源原色: {ep.SrcPrim}（底图将由此映射到目标底图色域）\n");
        // 决策日志里那条 tonemap（Hable/峰值）是**计划层为普通出口**算的，本出口不使用：
        // 底图压暗由 GainMapEncoder 的分段 Reinhard 按上面这个峰值完成。不写这句，日志会看起来
        // 像"Hable@峰值 应用了"，而实际生效的是 Reinhard@峰值 —— 属于"决策与执行不一致"的假象。
        log?.Invoke("[GainMap] 色调映射由本出口自持（分段 Reinhard，峰值即上行），"
                  + "计划层的 tonemap 参数不适用于 GainMap\n");

        var st = new Stats { Width = w, Height = h };
        var sw = Stopwatch.StartNew();
        var tmp = Path.Combine(PlatformServices.GetTempDir(), $"cms_gm_{Guid.NewGuid():N}.rgba");
        try
        {
            // ── 解码：zscale 线性化（首选）→ 已是线性的输入（dngtool TIFF）回退直解 ──
            log?.Invoke($"[GainMap] Step 2: ffmpeg 解码为线性 RGB 浮点 "
                      + $"({(ep.IsHdrInput ? $"HDR PQ/HLG npl={ep.Nits}" : "SDR")})...\n");
            var dec = await RunToEndAsync(ffmpeg,
                GainMapJpegCodec.LinearDecodeArgs(inputPath, tmp, ep.Nits, ep.SrcPrim), log, ct).ConfigureAwait(false);
            if (dec != 0 || !File.Exists(tmp))
            {
                log?.Invoke("[GainMap] ⚠️ zscale 线性转换失败，尝试直接解码 (输入可能已是线性)...\n");
                dec = await RunToEndAsync(ffmpeg,
                    GainMapJpegCodec.DirectDecodeArgs(inputPath, tmp), log, ct).ConfigureAwait(false);
            }
            st.DecodeMs = sw.ElapsedMilliseconds;
            if (dec != 0 || !File.Exists(tmp))
                throw new InvalidOperationException($"GainMap 线性解码失败 (exit {dec})");
            PlatformServices.MarkAsTemporaryFile(tmp);

            // ── 平面 → RGBA 交错（gbrpf32le 的平面序是 G, B, R）──
            long pixelCount = (long)w * h;
            var raw = await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false);
            if (raw.Length < pixelCount * 12L)
                throw new InvalidOperationException($"GainMap 像素数据不完整（{raw.Length}B < {pixelCount * 12L}B）");
            var sw2 = Stopwatch.StartNew();
            var pixels = GainMapJpegCodec.GbrPlanarToRgba(raw, checked((int)pixelCount));
            st.ProcessMs = sw2.ElapsedMilliseconds;

            // ── 计划层的标注**不适用于本出口**，必须清空 ──
            // 底图 ICC 由 GainMapEncoder 自己写（对齐 libultrahdr 的 writeIccProfile：底图是什么色域就附什么 ICC），
            // 而计划层的目标空间是**由源推导**的（如 bt2020 + sRGB 传递），二者不是一回事：
            //  · 保留 AttachIcc ⇒ 调用方的后置步骤（ApplyColorPostStepsAsync 的「引擎 ICC」段）会用计划层的
            //    目标空间覆盖底图 ICC —— 底图是 sRGB/Rec.2020(SDR) 的 SDR 图，贴目标空间 ICC 属**错标**；
            //  · 且改写 JPEG 的 APP2 段可能破坏 MPF 偏移（增益图的定位完全依赖它）。
            // 这里的清空是"执行体声明自己实际写了什么"，不是绕过后置逻辑。
            plan.AttachIcc = false;
            plan.IccPath = null;
            plan.IccPrimaries = plan.IccTrc = plan.IccMatrix = null;
            plan.OutPrimaries = plan.OutTransfer = plan.OutMatrix = null;

            // ── 编码：纯托管 GainMapEncoder（内含归一化 → 分段 Reinhard → 底图原色矩阵 → 打包）──
            var gmq = options.JpegGainMapQuality >= 0 ? options.JpegGainMapQuality : 75;
            // 质量 → butteraugli distance（与 cjpegli 语义一致）
            float baseDist = Math.Clamp((100f - options.Quality) * 0.25f, 0.5f, 6.0f);
            float gmDist = Math.Clamp((100f - gmq) * 0.25f, 0.5f, 6.0f);
            log?.Invoke($"[GainMap] Step 3: GainMapEncoder 编码 (纯 C#, 峰值 {ep.PeakNits:F0}nits, "
                      + $"base d={baseDist:F2}, gm d={gmDist:F2}, "
                      + $"底={(string.Equals(options.JpegGainMapBaseGamut, "bt2020", StringComparison.OrdinalIgnoreCase) ? "Rec.2020(SDR)" : "sRGB(SDR)")})...\n");
            var sw3 = Stopwatch.StartNew();
            // 渐进级别单一真值（GainMapJpegCodec.ProgressiveLevelOf）：`--jpeg-progressive` 的
            // `-1/0/1` → cjpegli 的 `-p -1/0/2`。此前 GainMap 路径**完全不读**该选项
            // （实测 0 与 1 的产物 MD5 相同，恒渐进）⇒ 这是本次接线的落点。
            int gmProgLv = GainMapJpegCodec.ProgressiveLevelOf(options);
            // ⚠ 文案必须纯 ASCII：`_probe-cjk-hardcode-scan.ps1` 的 C# UI 面余量为 0。
            log?.Invoke(gmProgLv >= 0
                ? $"[GainMap] JPEG progressive level -p {gmProgLv} (--jpeg-progressive={options.JpegProgressiveId})\n"
                : "[GainMap] JPEG progressive level: unspecified (cjpegli default)\n");
            bool ok = await GainMapEncoder.EncodeAsync(
                pixels, w, h, outputPath,
                hdrPeakNits: ep.PeakNits, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
                multiChannel: options.JpegGainMapMultiChannel,
                baseQuality: baseDist, gainMapQuality: gmDist,
                downsample: Math.Max(options.JpegGainMapDownsample, 1),
                baseGamut: options.JpegGainMapBaseGamut,
                sourcePrimaries: ep.SrcPrim,
                log: log, ct: ct,
                jpegProgressiveLevel: gmProgLv).ConfigureAwait(false);
            st.EncodeMs = sw3.ElapsedMilliseconds;
            if (!ok || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException("GainMapEncoder 编码失败");
            st.ElapsedMs = sw.ElapsedMilliseconds;
            return st;
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// **cjxl 出口**（2026-09-17 接入；计划位 <see cref="ColorTransformPlan.ExternalEncoderExit"/>）：
    /// 把**已变换**的 raw 像素经 **PPM/PAM 管道**喂给外部 <c>cjxl.exe</c> 编码。
    ///
    /// 为什么必须走管道、而不是写成中间 PPM 文件：`cjxl` 的 <c>-x color_space=</c> /
    /// <c>-x icc_pathname=</c> **只对 PPM/PAM 输入生效**（喂 PNG 等容器输入时 <c>-x</c> 被忽略、
    /// 只认文件内嵌 ICC —— `QueueProcessor` 的 cjxl 分支已登记该实测结论）。落盘中间文件并不能
    /// 让 <c>-x</c> 更可靠，只是多一次磁盘往返，故**不落中间 PPM**。
    ///
    /// 与常规出口的差别只有**编码段**：解码段与变换段完全复用（raw 已是最终像素），
    /// 本方法只负责「raw → ffmpeg 管道 → cjxl → outputPath」。
    ///
    /// ⚠ **失败语义（本出口最关键的一条）**：任何一环不满足（cjxl 缺失 / 计划要 ICC 但拿不到 /
    /// 目标语义既不能 CICP 命名又没有 ICC / 进程退出码非 0）一律**抛异常**，**绝不回退 ffmpeg 的
    /// libjxl 编码器** —— 回退会让产物照样生成、只是编码器被**静默换掉**（用户选的
    /// <c>-e/--progressive/--photon_noise_iso</c> 全丢），是本项目最忌讳的"看起来配置过"。
    /// 调用方（`QueueProcessor.TryRunColorEngineAsync`）把异常映射为**任务失败**（退出码 92）。
    /// </summary>
    /// <returns>cjxl 的退出码（非 0 由调用方的统一判据转成失败）。</returns>
    private static async Task<int> EncodeJxlViaCjxlAsync(string inputPath, string rawPath, string outputPath,
        ColorTransformPlan plan, Models.FfmpegOptions? options, int w, int h, Action<string>? log, CancellationToken ct)
    {
        if (options == null)
            throw new InvalidOperationException(
                "cjxl 出口需要 FfmpegOptions（质量/effort/渐进/光子噪声只存在于其中）");

        // ── ① 可用性诚实：cjxl 缺失 ⇒ 显式失败（**不查可用性就该在路由层放行的理由见 Router 的注释**）──
        var cjxl = CjxlService.DetectedPath;
        if (string.IsNullOrWhiteSpace(cjxl) || !File.Exists(cjxl))
            throw new InvalidOperationException(
                "cjxl 出口：未找到 cjxl.exe ⇒ 拒绝回退 ffmpeg 的 libjxl 编码器"
                + "（回退会静默换掉你选的编码器与它的全部参数）");

        // ── ② 色彩语义：**只能来自计划**（`plan.Dst`，无目标时源即目标 —— 直通/携带的像素未改）──
        var d = plan.Dst ?? plan.Src;

        // ICC：cjxl 用 `-x icc_pathname=` 自己写进产物 —— 这正是 `CanCarryArbitraryIcc=true`
        // 这条**格式能力**在 cjxl 后端下的兑现方式（ffmpeg 的 libjxl 出口做不到，见本文件开头的门）。
        string? iccFile = null;
        if (plan.AttachIcc)
        {
            iccFile = plan.IccPath;
            // 目标 ICC：计划只给了 (primaries, trc, matrix) 时按标准空间生成（与后置步骤同一函数）
            if ((string.IsNullOrWhiteSpace(iccFile) || !File.Exists(iccFile))
                && !string.IsNullOrWhiteSpace(plan.IccPrimaries))
                iccFile = IccProfileService.GetOrGenerateStandardIcc(
                    inputPath, plan.IccPrimaries, plan.IccTrc, plan.IccMatrix, log, ct);
            // ── 源 ICC 的**重新提取**（2026-09-17 实测踩到）──────────────────────────────
            // `CarryIcc` 计划的 `IccPath` 指向**源** ICC，而该临时文件在识别阶段就被回收了：
            // `ColorIntentFactory` 走 `ExtractIccToTempFile` → `FromIccFile`（度量）→ `TryDeleteIcc`
            // ⇒ 计划里的路径**已经悬空**。此前不暴露，是因为 CarryIcc 一律在
            // `QueueProcessor.TryRunColorEngineAsync` 里提前返回 `(false,0,plan)`（引擎不参与、
            // 传统管线重新提取）—— 本次 cjxl 出口让引擎**真的执行**了这类计划，悬空路径就露出来了。
            // ⚠ 只对 `CarryIcc` 兜底（像素零改动 ⇒ 该附的就是源 ICC）；`Map` 类计划若拿不到**目标** ICC
            //   绝不能退而附源 ICC —— 那正是"ICC 与变换后像素失配"的经典错标，必须失败。
            if ((string.IsNullOrWhiteSpace(iccFile) || !File.Exists(iccFile))
                && plan.Action == ColorAction.CarryIcc)
            {
                var (reextracted, _) = IccProfileService.ExtractIccToTempFile(inputPath);
                if (!string.IsNullOrWhiteSpace(reextracted) && File.Exists(reextracted)) iccFile = reextracted;
            }
            if (string.IsNullOrWhiteSpace(iccFile) || !File.Exists(iccFile))
                throw new InvalidOperationException(
                    "cjxl 出口：计划要求附 ICC（"
                    + (plan.Action == ColorAction.CarryIcc ? "源 ICC" : "目标 ICC")
                    + "），但无法定位/生成 ⇒ 拒绝产出无色彩描述的 JXL"
                    + "（宁可失败，也不让产物看起来配置过、实际没有任何色彩描述）");
        }

        // 色彩空间简写：**只在不走 ICC 时**给 —— 本格式 `CanHaveBothIccAndCicp=false`（E2 实测），
        // 择一规则与计划一致，出口不自行加戏。
        // ⚠ **判据是"计划有没有要求写 CICP"，不是"目标描述符存不存在"**（2026-09-17 实测踩到）：
        //   低置信度源（无 ICC、无标签 ⇒ 惯例假定）的计划会**故意不写任何标注**（`Action=None`、
        //   `Out*` 全空、`AttachIcc=false`），此时若拿 `plan.Dst` 硬翻一个 `color_space` 出来，
        //   就是**出口替计划加了一个它没决定的标注**（"猜一个色域写标签"，正是本项目禁止的）。
        //   ⇒ 以计划自己的标注决策（`OutPrimaries`/`OutTransfer`）为准。
        string? cjxlSpace = null;
        if (iccFile == null)
        {
            bool planWantsCicp = !string.IsNullOrWhiteSpace(plan.OutPrimaries)
                              || !string.IsNullOrWhiteSpace(plan.OutTransfer);
            if (planWantsCicp)
            {
                // 主路径：由**目标描述符**翻译（规划层的语义真值）
                cjxlSpace = ColorEncodingHelper.MapToCjxlColorSpace(plan.Dst);
                // 备路径：描述符缺失/不可命名时，直接翻译**计划自己的 CICP 决策**
                //（同一对 token，不构成第二份判据 —— 它正是 `Out*` 的来源）
                if (cjxlSpace == null)
                    cjxlSpace = ColorEncodingHelper.MapPrimariesTransfer(plan.OutPrimaries, plan.OutTransfer);
                if (cjxlSpace == null)
                    // ⚠ 必须**点名出路**（2026-09-17 实测登记的能力缺口）：同一请求走 ffmpeg-libjxl 后端
                    //   **可以**交付（ffmpeg 的 JXL muxer 能写任意 CICP：实测 `-color_primaries bt2020
                    //   -color_trc bt709` ⇒ jxlinfo 读回「Rec.2100 primaries, 709 transfer」），窄的是
                    //   **cjxl 的 `-x color_space=` 枚举**，不是这个格式、也不是这个目标。只说"拒绝"会让
                    //   用户以为"这个目标做不到"，其实只是**这个后端**做不到 ⇒ 文案必须点名 `-e Ffmpeg`。
                    throw new InvalidOperationException(
                        $"cjxl 出口：计划要求标注 {plan.OutPrimaries}/{plan.OutTransfer}，"
                        + "但该语义落不进 cjxl 的 color_space 枚举（仅 sRGB / Display P3 / Rec.2100 PQ / HLG），"
                        + "且计划未提供 ICC ⇒ 拒绝产出一个标注兑现不了的 JXL。⚠ 出路：改用 -e Ffmpeg（ffmpeg 的 libjxl 能写任意 CICP），或改用可枚举的目标（sRGB / Display P3 / Rec.2100 PQ / HLG）");
            }
            else
            {
                // 计划**本身就没要求**标注 ⇒ 照做（与直连 cjxl 管道同结局：无 `-x color_space=`），
                // 但必须**说出来** —— 否则用户看到"已完成"却不知道产物没有任何色彩描述。
                log?.Invoke("[色彩] cjxl 出口：计划未要求色彩标注（既无 ICC 也无 CICP）"
                          + $"⇒ 产物不带色彩描述（计划依据：{plan.Reason}）\n");
            }
        }

        // ── ③ 显示峰值：与直连 cjxl 管道**同源同规则**（同一份 MapToIntensityTarget）──
        // 补一条硬要求：目标为 HDR 时**必须有值** —— 缺了它 libjxl 回落默认峰值 ⇒ HDR 亮度就错了
        //（不是"少个标签"，是亮度语义错；见 CjxlService 已登记的实测结论）。
        var hdrMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath, log, ct);
        int itNits = ColorEncodingHelper.MapToIntensityTarget(hdrMeta);
        if (itNits <= 0 && ColorEncodingHelper.IsHdrDescriptor(d)) itNits = 1000;

        // ── ④ 编码参数：与直连路径**共用同一个** BuildCjxlArguments（不另写一份）──
        // 色彩语义以 override 传入（来自计划）；质量/effort/渐进/光子噪声仍由它按 options 产出。
        var cjxlArgs = CjxlService.BuildCjxlArguments("-", outputPath, options, hdrMeta, iccFile,
            cjxlSpace, itNits, log: log);

        // ── ⑤ ffmpeg 侧：raw → PPM/PAM 流 ──
        // ⚠ **`pixIn` 是盘上中间件的格式，不是交付格式**（2026-09-30 实测修，取证 `tests/output/t63/`、
        //   `t65/`、`t66/`）：原先 PPM 的样本深度直接跟 `plan.OutBitDepth`（引擎中间件档，直通恒 16），
        //   于是 8-bit 源也被 cjxl 按 **16-bit** 无损编码 —— 同一份像素 8bit 交付 11,485.5 kB、
        //   16bit 交付 **20,233.6 kB（+76.2%）**，比源 PNG（12,163.9 kB）还大 66%，正是用户报的
        //   "无损比 PNG 还大"。
        //   为什么不在规划层把直通支改成 8bit：那样会让引擎走 `ColorKernels.EncodeTo8`，
        //   其 `code16/65535*255` 在 float 下对 ×257 码值**不是恒等**（实测 0..91 恒等、92 起约半数
        //   偏低 1、252..255 恒偏低 1）⇒ 把无损改成 ±1 有损。而 ffmpeg 的 `format=rgb24` 与 cjxl
        //   往返都是逐比特精确的（E1/E2/E3）⇒ **降位交给 ffmpeg 做**，中间件保持 16bit 不动。
        //   交付档取自 `plan.TargetBitDepth` + 容器自己的 `MapPixFmt`（与 png/tiff 出口同一判据，
        //   也与 `CollectDegradations` ①b 播报的那一份一致）。
        string pixIn = plan.OutBitDepth == 8 ? "rgb24" : "rgb48le";
        string ppmPix = ImageEncoderArgs.MapPixFmt("jxl", null, plan.TargetBitDepth, hdrMeta.hasAlpha, null);
        string ffArgs;
        if (hdrMeta.hasAlpha)
        {
            // 引擎 raw 通路是 rgb24/rgb48le（**无 alpha 平面**）⇒ 直接喂进去会把透明**静默拍平**。
            // 与 GIF 出口同款解法：把**原文件**作第二输入，只取它的 alpha 平面再 merge。
            // ⚠ 只用第二输入的 **alpha**；RGB 一律用**变换后**的像素（原文件的 RGB 是未变换的）。
            // ⚠ 第二输入必须套**同一个**最长边 scale 滤镜：raw 在解码步已缩放，不套则两路尺寸对不上
            //   （GIF 出口实测缺了它 ffmpeg 报 "could not choose their formats"）。
            var scaleFilter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(options, inputPath);
            string alphaFmt = plan.OutBitDepth == 8 ? "rgba" : "rgba64le";  // 与 pixIn 同深，否则 alphamerge 无法协商
            string merge = $"[1:v]{(string.IsNullOrEmpty(scaleFilter) ? "" : scaleFilter + ",")}"
                         + $"format={alphaFmt},alphaextract[a];[0:v]format={pixIn}[b];[b][a]alphamerge,format={ppmPix}";
            ffArgs = $"-y -hide_banner -loglevel error -f rawvideo -pix_fmt {pixIn} -video_size {w}x{h} "
                   + $"-i \"{rawPath}\" -i \"{inputPath}\" -filter_complex \"{merge}\" "
                   + $"-compression_level 0 -f image2pipe -c:v pam -";
        }
        else
        {
            ffArgs = $"-y -hide_banner -loglevel error -f rawvideo -pix_fmt {pixIn} -video_size {w}x{h} "
                   + $"-i \"{rawPath}\" -vf \"format={ppmPix}\" "
                   + $"-compression_level 0 -f image2pipe -c:v ppm -";
        }
        if (ppmPix != pixIn)
            log?.Invoke($"[色彩] cjxl 出口：中间件 {pixIn} → 交付 {ppmPix}"
                      + $"（目标位深 {plan.TargetBitDepth}bit；16bit 载体无损编码会显著变大且并非用户所要）\n");

        // 命令行必须可追溯（与 RunToEndAsync 同款理由）：A/B 对拍时"两条管线差在哪个参数"只能靠这两行
        // ⚠ `cjxl 命令行:` 这个**显式标签不能省**：本方法后面还有若干 `[色彩] cjxl 出口：…` 的散文行，
        //   若命令行也写成裸 `[色彩] cjxl `，两者前缀相同 ⇒ 门禁用 `-Last 1` 取命令行时**必然取到散文**
        //   （实测 4 条断言因此假红：拿到的"命令行"是"变换后的 raw → PPM → cjxl"）。
        log?.Invoke($"[色彩] ffmpeg {ffArgs}\n");
        log?.Invoke($"[色彩] cjxl 命令行: {cjxlArgs}\n");
        log?.Invoke($"[色彩] cjxl 出口：变换后的 raw → {(hdrMeta.hasAlpha ? "PAM（含 alpha，第二输入补平面）" : "PPM")}"
                  + $" → cjxl（不落中间文件）\n");

        int exitCode = await PipeToEncoderAsync(ffmpegPath: AppSettingsService.Current.FfmpegPath,
            ffArgs, cjxl, cjxlArgs, log, ct).ConfigureAwait(false);

        // ⚠ **成功判据不只是退出码**：还要真的产出非空文件。少了这一条，cjxl 以 0 退出但没写文件
        //   （如参数拼错到它静默忽略）会被当成成功 ⇒ 调用方随后才报"未产出"，归因就错了。
        if (exitCode == 0 && (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0))
            throw new InvalidOperationException("cjxl 出口：cjxl 退出码为 0 但未产出非空文件");

        // ── ⑥ 兑现声明：ICC 已由 cjxl 自己写进产物 ⇒ 必须把 AttachIcc 清掉 ──
        // 否则调用方的**后置步骤**（`ApplyColorPostStepsAsync` 的「引擎 ICC」段）会再用 exiftool 嵌一次：
        // 对 JXL 那是**必然失败**（实测退出码 1、"Will wrap JXL codestream in ISO BMFF container"、
        // "0 image files updated"）⇒ 用户会看到一条"ICC 嵌入失败，输出可能缺少色彩描述"的**假告警**，
        // 而产物其实已经有 ICC 了。这与 GainMap 出口"清空标注字段 = 执行体声明自己实际写了什么"同款处理。
        if (exitCode == 0 && iccFile != null)
        {
            plan.AttachIcc = false;
            log?.Invoke($"[色彩] cjxl 出口：{(plan.Action == ColorAction.CarryIcc ? "源" : "目标")} ICC "
                      + "已由 cjxl 写入产物 ⇒ 跳过 exiftool 后置"
                      + "（exiftool 写 JXL 的 ICC 会失败，实测退出码 1）\n");
        }
        return exitCode;
    }

    /// <summary>
    /// **JXR 出口**（2026-09-17 接入；计划位 <see cref="ColorTransformPlan.ExternalEncoderExit"/>）：
    /// 把**已变换**的 raw 像素落成中转容器（BMP/TIFF），交给外部 <c>JxrEncApp.exe</c> 编码为 JPEG XR。
    ///
    /// <para>
    /// **为什么必须落中转文件**（与 cjxl 出口的管道形态不同）：`JxrEncApp` 的接口就是纯文件式
    /// <c>-i input -o output</c>（实测自述用法：<c>bmp: &lt;=8bpc, BGR</c> / <c>tif: &gt;=8bpc, RGB</c> /
    /// <c>hdr: 32bppRGBE only</c>），不接受 stdin/rawvideo。
    /// </para>
    /// <para>
    /// **为什么不改道 ffmpeg（此口径的唯一权威，其余站点引本段）**：本构建的 ffmpeg **确实有** `libjxr`
    /// —— 实测 git-2026-09-26-8864fd0aec 的 <c>-codecs</c> 报
    /// <c>DEVILS jpegxr …(decoders: libjxr)(encoders: libjxr)</c>，取证 <c>tests/output/t50/</c>。
    /// 本段此前写作「本工具链的 ffmpeg 没有 jxr 编码器」，那是**过期事实**，2026-09-27 按实测改正。
    /// 但它当不了 JXR 出口的替代执行体：<c>-h encoder=libjxr</c> 的**私有 AVOption 零条** ⇒ 面板的
    /// 质量/无损旋钮全部失效（实测 7 种质量变体产物同 sha），且它写 <c>24-bit RGB</c> 而 JxrEncApp 写
    /// <c>24-bit BGR</c>、rgba 用一个非标准 GUID ⇒ 改道等于静默丢掉用户参数并改字节序。
    /// **结论不变、理由换**：出口内任何一环不满足都**显式失败**，绝不回退。
    /// </para>
    /// <para>
    /// **中转格式与位深（均为实测，不是推断）**：8-bit ⇒ BMP（<c>bgr24</c>）；&gt;8-bit ⇒ TIFF
    /// （<c>rgb48le</c>，<c>-compression_algo raw -pred none</c> 免压缩免预测）。位深**保真**：
    /// <c>rgb48le</c> TIFF → JxrEncApp → JxrDecApp <c>-c 10</c>（48bppRGB）回读仍是 <c>rgb48le</c>。
    /// </para>
    /// <para>
    /// **alpha（与 GIF/cjxl 出口同款坑）**：引擎 raw 通路是 <c>rgb24</c>/<c>rgb48le</c>（**无 alpha 平面**），
    /// 直接喂进去会把透明**静默拍平**。JXR **支持** alpha（实测：32bpp BGRA BMP → 产物 PixelFormat
    /// <c>32-bit BGRA</c> → JxrDecApp <c>-c 22 -a 2</c> 回读 alpha 与源一致）⇒ 用第二输入取原文件的
    /// alpha 平面再 merge（<c>bgra</c> / <c>rgba64le</c>），与 cjxl 出口逐字同款。
    /// </para>
    /// <para>
    /// ⚠ **本出口不写 ICC**：目标 ICC 由队列后置步骤（<c>ApplyColorPostStepsAsync</c> 的「引擎 ICC」段）
    /// 统一嵌入 —— 与 png/tiff/webp **同一机制、单一入口**（实测 exiftool 对 JXR 写 ICC 后逐字节可读回，
    /// 且 JxrDecApp 仍能解码）。出口内再写一次就是第二套口径。
    /// </para>
    /// </summary>
    private static async Task<int> EncodeJxrViaJxrEncAppAsync(
        string inputPath, string tmpOut, string outputPath, ColorTransformPlan plan,
        Models.FfmpegOptions? options, int w, int h, Action<string>? log, CancellationToken ct)
    {
        if (options == null)
            throw new InvalidOperationException(
                "JXR 出口需要 FfmpegOptions（质量/无损只存在于其中）");

        // ── ① 可用性诚实：JxrEncApp 缺失 ⇒ 显式失败 ──
        // **没有可回退的编码器**：不是「ffmpeg 缺 jxr 编码器」（它有 libjxr，理由见本方法 <summary>），
        // 而是改道 libjxr 会静默丢掉质量参数并改字节序 ⇒「回退」只会产出与请求不符的文件。
        var jxr = JxrService.DetectedPath;
        if (string.IsNullOrWhiteSpace(jxr) || !File.Exists(jxr))
            throw new InvalidOperationException(
                "JXR 出口：未找到 JxrEncApp.exe ⇒ 拒绝产出 JXR"
                + "（ffmpeg 的 libjxr 无质量参数且字节序不合，不作为回退项）");

        var ffmpeg = AppSettingsService.Current.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            throw new InvalidOperationException("JXR 出口需要 ffmpeg 做 raw → BMP/TIFF 中转，但未找到 ffmpeg");

        bool out8 = plan.OutBitDepth == 8;
        // ⚠ **中间件精度 ≠ 交付样本深度**（2026-10-01，与 cjxl 出口同一条修法）：
        //   `out8` 说的是盘上那份 raw 是什么格式（直通/携带的中间件恒 16-bit），
        //   而 JXR 该出几 bit 由 `plan.TargetBitDepth` 决定。原先三处（中转容器、`pixOut`、
        //   `tiffExtra`）全用 `out8` ⇒ 8-bit 源被绕成 16-bit JXR。实测（800x600 8-bit PNG，
        //   `--lossless`）：交付 `pix_fmt=rgb48le`、877.2 kB，而同素材的 png/tiff 出口都正确给
        //   `rgb24` ⇒ 只有这一条出口漏了。降位仍交给 ffmpeg 的 `-pix_fmt`（对 ×257 码值精确）。
        bool deliver8 = plan.TargetBitDepth <= 8;
        var meta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath, log, ct);
        bool hasAlpha = meta.hasAlpha;

        // ── ② 中转文件落临时目录，`finally` 用 File.Delete 清理 ──
        // ⚠ 清理一律走 `TryDelete`（内部是 `File.Delete`）：本环境 `Remove-Item` 对**仓外**路径
        //   静默无效（已实测登记）⇒ 用 Remove-Item 会留下中转文件。
        var dir = Path.Combine(PlatformServices.GetTempDir(), $"cmjxr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var inter = Path.Combine(dir, deliver8 ? "in.bmp" : "in.tiff");
        try
        {
            string pixIn = out8 ? "rgb24" : "rgb48le";
            string pixOut = hasAlpha ? (deliver8 ? "bgra" : "rgba64le") : (deliver8 ? "rgb24" : "rgb48le");
            // TIFF 必须显式免压缩/免预测：JxrEncApp 自述只吃 `tif: >=8bpc, RGB`，
            // 压缩过的 TIFF 是否被它接受未经验证 ⇒ 用最朴素的无压缩形态（实测可用）。
            string tiffExtra = deliver8 ? "" : " -compression_algo raw -pred none";

            string ffArgs;
            if (hasAlpha)
            {
                // 与 cjxl 出口同款：第二输入只取**原文件**的 alpha 平面（RGB 一律用**变换后**的像素，
                // 原文件的 RGB 是未变换的），并套**同一个**最长边 scale 滤镜（raw 在解码步已缩放，
                // 不套则两路尺寸对不上）。
                var scaleFilter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(options, inputPath);
                string merge = $"[1:v]{(string.IsNullOrEmpty(scaleFilter) ? "" : scaleFilter + ",")}"
                             + $"format={pixOut},alphaextract[a];[0:v]format={pixIn}[b];[b][a]alphamerge";
                // ⚠ 2026-09-18（#13 站点 B）：中转目标是 BMP/TIFF（`image2` 复用器，**固定文件名**）⇒ 只能写一帧。
                //   底图 `[0:v]`（`{tmpOut}`，raw）恒为 1 帧，但 **alpha 源 `[1:v]` 是原文件**：动图输入（如 10 帧 webp）
                //   会给 `[1:v]` 10 帧 ⇒ 滤镜图产出 10 帧 ⇒ 复用器报
                //   `Cannot write more than one file with the same name…` 并以 -22 退出（实测）。
                //   判据与 #139（FfmpegCommandBuilder.targetHoldsMultipleFrames）同一条：**目标容器是否支持多帧**，
                //   这里恒为单帧 ⇒ 限制**输出**帧数为 1。
                //   为什么不是「限制 alpha 源那一路」：把 `-frames:v 1` 放到第二个 `-i` **之前**会被 ffmpeg 拒绝
                //   （它是输出选项；实测 `-frames:v … cannot be applied to input url … Move this option before the
                //   file it belongs to`，且**无产物**）。另一种「`[1:v]trim=end_frame=1`」实测与本法**产物同尺寸**
                //   （均 33039B），但要在 scale 滤镜前插入 trim 并重排逗号，复杂且易错 ⇒ 取输出侧限制。
                //   ⚠ 错位担忧已实测排除：`-frames:v 1` 取的是**输出**第 0 帧 = `[0:v]` 帧0 + `[1:v]` 帧0，
                //     两者**同帧号**，不会拿帧1 的 alpha 配帧0 的底图（3 帧、后两帧全透明的 apng 作 alpha 源，
                //     输出 alphaYMIN16=59938 ≠ 0，即取到的是**不透明**的帧0）。
                //   实测（单帧 底+alpha）：加与不加**逐字节相同**（md5 273ce11b8f9e5d62b08b48dc31ae0270）⇒ no-op。
                ffArgs = $"-y -hide_banner -loglevel error -f rawvideo -pix_fmt {pixIn} -video_size {w}x{h} "
                       + $"-i \"{tmpOut}\" -i \"{inputPath}\" -filter_complex \"{merge}\" "
                       + $"-pix_fmt {pixOut}{tiffExtra} -frames:v 1 \"{inter}\"";
            }
            else
            {
                ffArgs = $"-y -hide_banner -loglevel error -f rawvideo -pix_fmt {pixIn} -video_size {w}x{h} "
                       + $"-i \"{tmpOut}\" -pix_fmt {pixOut}{tiffExtra} \"{inter}\"";
            }

            log?.Invoke($"[色彩] ffmpeg {ffArgs}\n");
            int fx = await RunToEndAsync(ffmpeg, ffArgs, log, ct).ConfigureAwait(false);
            if (fx != 0 || !File.Exists(inter) || new FileInfo(inter).Length == 0)
                throw new InvalidOperationException(
                    // ⚠ 中转容器名要说**实际写的那个**：容器由 `deliver8`（交付档）选，而 `out8` 说的是
                    //   盘上中间件（直通/携带恒 16-bit）⇒ 8-bit 直通源写的是 in.bmp，报 TIFF 就是假文案
                    //   （2026-10-01 打包冒烟实测到：日志「→ TIFF」而命令行 `-pix_fmt rgb24 … in.bmp`）。
                    $"JXR 出口：raw → {(deliver8 ? "BMP" : "TIFF")} 中转失败 (exit {fx})");

            // ── ③ JxrEncApp 编码 ──
            double quality = options.Lossless ? 1.0 : options.Quality / 100.0;
            var jxrArgs = JxrService.BuildArguments(inter, outputPath, quality);
            // ⚠ `JxrEncApp 命令行:` 这个显式标签不能省：本方法前面还有 `[色彩] ffmpeg …` 与
            //   `[色彩] JXR 出口：…` 等行，门禁用宽前缀取命令行时会取到散文（cjxl 出口踩过同款）。
            log?.Invoke($"[色彩] JxrEncApp 命令行: {jxrArgs}\n");
            int ec = await JxrService.RunAsync(jxrArgs, s => log?.Invoke(s), ct).ConfigureAwait(false);
            if (ec != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                return ec != 0 ? ec : -1;   // 调用方按「编码失败 (exit …)」统一报错（不在这里吞掉）
            log?.Invoke($"[色彩] JXR 出口：变换后的 raw → {(deliver8 ? "BMP" : "TIFF")}"
                      + $"（{(hasAlpha ? "含 alpha，第二输入补平面" : "无 alpha")}）→ JxrEncApp（中转文件已清理）\n");
            return 0;
        }
        finally
        {
            try { if (File.Exists(inter)) File.Delete(inter); } catch { }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 两进程管道：<paramref name="exeA"/> 的 stdout → <paramref name="exeB"/> 的 stdin。
    /// 与 `QueueProcessor.PipeFfmpegToExternalEncoderAsync` 同款顺序约束（**先灌流并关闭 stdin，
    /// 再等退出**）：反了会死锁 —— 编码器等 EOF、而读方还在等编码器退出。
    /// 30 分钟上限与直连 cjxl 路径对齐（effort 9/10 + 大图合法耗时远超 10 分钟）。
    /// </summary>
    private static async Task<int> PipeToEncoderAsync(string ffmpegPath, string ffArgs,
        string encoderPath, string encoderArgs, Action<string>? log, CancellationToken ct)
    {
        Process? pa = null, pb = null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
            var token = linked.Token;

            pa = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath, Arguments = ffArgs,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false, CreateNoWindow = true,
            });
            if (pa == null) { log?.Invoke("[色彩] ❌ cjxl 出口：ffmpeg 启动失败\n"); return -1; }
            // 2026-09-21: this path previously MISSED the process-priority call, so the UI
            // "process priority" setting had no effect here (UI vs. real behaviour mismatch).
            Services.PlatformServices.SetSafePriority(pa, Services.AppSettingsService.Current.FfmpegPriority);

            pb = Process.Start(new ProcessStartInfo
            {
                FileName = encoderPath, Arguments = encoderArgs,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false, CreateNoWindow = true,
            });
            if (pb == null) { log?.Invoke("[色彩] ❌ cjxl 出口：cjxl 启动失败\n"); return -1; }
            // 2026-09-21: same as above - the external encoder (cjxl) also needs the priority.
            Services.PlatformServices.SetSafePriority(pb, Services.AppSettingsService.Current.FfmpegPriority);

            // 两侧 stderr 都必须**排空**（否则子进程写满管道缓冲即阻塞，表现为"卡住"）
            var errA = pa.StandardError.ReadToEndAsync(token);
            var errB = pb.StandardError.ReadToEndAsync(token);
            var outB = pb.StandardOutput.ReadToEndAsync(token);

            Exception? transferError = null;
            try
            {
                await pa.StandardOutput.BaseStream.CopyToAsync(pb.StandardInput.BaseStream, token)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { log?.Invoke("[色彩] cjxl 出口：传输被取消\n"); }
            catch (Exception ex) { transferError = ex; log?.Invoke($"[色彩] cjxl 出口：传输错误 {ex.Message}\n"); }

            try { pb.StandardInput.Close(); } catch { }        // 关 stdin 发 EOF
            try { await pb.WaitForExitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { if (!pb.HasExited) pb.Kill(entireProcessTree: true); } catch { } }
            // 传输已经失败 ⇒ 编码器不会再读任何东西，而 ffmpeg 还有整幅 PPM 要写：实测 891x981 的
            // 5.2 MB 流写满管道缓冲（64 KB 量级）后就**永久阻塞在写端**——此时没有任何东西会再读它的
            // stdout。等下去的唯一结局是 30 分钟超时，用户届时看到的是"超时或已取消"，真因（编码器的
            // 参数错误）被埋在下面那行 encErr 里 ⇒ 直接杀掉，让真因立刻浮出来。
            // ⚠ 这里**不取消 token**：`errA`/`errB` 是 `ReadToEndAsync(token)`，一取消就读不到
            //   cjxl 的 stderr 了，而那正是本次要交给用户的那句话。
            if (transferError != null)
            {
                try { if (!pa.HasExited) pa.Kill(entireProcessTree: true); } catch { }
            }
            // ffmpeg 应已自行退出；取消/编码器提前退出时它可能阻塞在无读者的 stdout ⇒ 用 token 打断并杀
            try { await pa.WaitForExitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { if (!pa.HasExited) pa.Kill(entireProcessTree: true); } catch { } }

            string ffErr, encErr;
            try { ffErr = await errA.ConfigureAwait(false); } catch { ffErr = ""; }
            try { encErr = await errB.ConfigureAwait(false); } catch { encErr = ""; }
            try { await outB.ConfigureAwait(false); } catch { }

            if (token.IsCancellationRequested)
                throw new OperationCanceledException("cjxl 出口：超时或已取消", token);

            if (pa.ExitCode != 0 && !string.IsNullOrWhiteSpace(ffErr))
                log?.Invoke($"[色彩] ffmpeg（cjxl 出口侧）: {ffErr.TrimEnd()}\n");
            if (!string.IsNullOrWhiteSpace(encErr)) log?.Invoke($"[色彩] cjxl: {encErr.TrimEnd()}\n");
            if (transferError != null)
                // 异常文本必须自带编码器原话：这条是任务详情里唯一与"为什么失败"面对面的字段，
                // 只写"管道传输失败（管道正在被关闭）"等于把 broken pipe 这个**症状**当结论回给用户。
                throw new InvalidOperationException(
                    $"cjxl 出口：管道传输失败（{transferError.Message}）"
                    + $"；cjxl 提前退出，退出码 {pb.ExitCode}"
                    + (string.IsNullOrWhiteSpace(encErr)
                        ? "，且 cjxl 未输出任何信息"
                        : $"，cjxl 说：{encErr.Trim()}"));
            return pb.ExitCode;
        }
        finally
        {
            try { if (pa is { HasExited: false }) pa.Kill(entireProcessTree: true); } catch { }
            try { if (pb is { HasExited: false }) pb.Kill(entireProcessTree: true); } catch { }
            try { pa?.Dispose(); } catch { }
            try { pb?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 编码参数：目标位深 ≥16、按 Plan 写 CICP；ICC 由调用方后置附着（exiftool/编码器）。
    /// <para>
    /// **2026-09-16**：质量/无损/结构参数改由 <see cref="ImageEncoderArgs"/> 统一构造
    /// （此前是硬编码：png 恒 <c>-compression_level 100</c>、webp 恒 <c>-quality 90</c>、jpg 恒 <c>-q:v 2</c>，
    /// 导致用户设置的质量/无损被整批拒绝）。<paramref name="o"/> 为 null 时退回原有固定值，
    /// 保证既有调用（探针等）行为不变。
    /// </para>
    /// <para>
    /// **2026-09-17**：AVIF 接入本出口。与其余格式的三点不同：
    /// ① 显式 <c>-c:v</c>（仅在用户选了编码器时，与传统管线同策略）；
    /// ② **CICP 标签拆侧**：<c>-color_primaries/-color_trc</c> 前置到 <c>-i</c> **之前**
    ///    （AVIF 的 nclx 由编码器读**输入帧属性**写入，输出侧标签被静默丢弃），
    ///    而 <c>-colorspace</c>（矩阵）留在输出侧（前置会被完全忽略，连 RGB→YUV 系数一起丢）；
    /// ③ <c>-pix_fmt</c> 按**与传统管线同一判据**决定写不写（色度显式 / 位深显式 / 源位深 &gt; 8），
    ///    位深由**源位深**驱动（<see cref="ImageEncoderArgs.AvifEffectiveBitDepth"/>）。
    ///    故本方法新增 <paramref name="inputPath"/> 参数（要探源位深；
    ///    中间件 <paramref name="rawPath"/> 是 rgb24/rgb48le，探它得不到源位深）。
    /// </para>
    /// <para>
    /// **2026-09-17（第二批）**：GIF 接入本出口。与其余格式的两点不同：
    /// ① 滤镜通路：GIF 必须带调色板链（见 <see cref="ImageEncoderArgs.BuildGifFilterChain"/>），
    ///    写成 <c>-filter_complex</c>；
    /// ② **第二输入**：把**原文件**再喂进来只取 alpha 平面（<c>alphaextract</c>）与变换后的
    ///    RGB <c>alphamerge</c> —— 引擎 raw 通路没有 alpha，不补就会把透明静默拍平。
    ///    代价 = GIF 任务多一次源解码（单帧，实测可忽略）。
    /// </para>
    /// <para>
    /// **2026-09-17（第三批）**：JXL 接入本出口。与其余格式的两点不同：
    /// ① <b>CICP 标签整体前置</b>（与 AVIF 同病，实测：输出侧标签对 rawvideo 输入被忽略，
    ///    <c>jxlinfo</c> 读回 <c>sRGB primaries</c>；前置后读回 <c>P3 primaries</c>）；
    /// ② <b>不接受 AttachIcc 的计划</b>（见 <see cref="TransformFileAsync"/> 的同名拒绝：
    ///    ffmpeg 的 libjxl 丢弃 iccgen 的 ICC，exiftool 又拒写 JXL）⇒ 本出口只写 CICP。
    /// </para>
    /// </summary>
    private static string BuildEncodeArgs(string inputPath, string rawPath, string outputPath, ColorTransformPlan plan,
        Models.FfmpegOptions? o, int w, int h)
    {
        var ext = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        // ── CICP 标签分两组：原色/传递（P/T）与矩阵。两组在 AVIF 出口必须写在不同侧 ──
        // （2026-09-17 实测，见下方 inLabels/outLabels 的说明）
        //   P/T 写在 `-i` **之前** ⇒ 驱动 AVIF 的 nclx primaries/transfer；
        //   矩阵写在 `-i` **之后** ⇒ 既决定 RGB→YUV 的转换系数，又写进 nclx matrix。
        string labels = "";
        if (!string.IsNullOrWhiteSpace(plan.OutPrimaries)) labels += $" -color_primaries {plan.OutPrimaries}";
        if (!string.IsNullOrWhiteSpace(plan.OutTransfer)) labels += $" -color_trc {plan.OutTransfer}";
        string matrixLabels = "";
        if (!string.IsNullOrWhiteSpace(plan.OutMatrix)) matrixLabels += $" -colorspace {plan.OutMatrix}";
        // ── YUV 原生输出必须显式给出 RGB→YUV 的**转换矩阵**（2026-09-16 修复）──
        // 矩阵在这里有两个角色：① **转换参数**（swscale 用哪个系数把 RGB 拆成 Y′CbCr）；
        // ② 容器标注。Plan 对**非 CICP 容器**（jpg/webp）会把 `OutMatrix` 清空（标注改由 ICC 承担），
        // 但**转换仍然需要一个矩阵** —— 少了它，swscale 按图像尺寸取默认（SD ⇒ bt601），
        // 而传统管线按容器能力表取 bt709 ⇒ 两者像素不同。
        // 实测证据：同源 P3→sRGB，legacy 出 `-colorspace bt709`、引擎无该参数，
        // jpg 的 PSNR 只有 **22.4dB**（luma 21.1dB，均值几乎相同 ⇒ 是系数差异不是亮度偏移）；
        // 补上后见 TESTING §6 第 49 条的 A/B 复测。
        // 取值与 legacy 的 `capM`（jpg/webp = bt709）同源，保证切换管线时逐字节可对拍。
        if (string.IsNullOrWhiteSpace(plan.OutMatrix) && (ext is "jpg" or "jpeg" or "webp"))
            matrixLabels += " -colorspace bt709";
        string pixIn = plan.OutBitDepth == 8 ? "rgb24" : "rgb48le";
        // ── AVIF / JXL：P/T 标签必须写在 `-i` **之前**（两者分别于 2026-09-17 实测驱动）──
        // AVIF 的 nclx `colr` 盒由**编码器读输入帧的色彩属性**写入，输出侧
        // `-color_primaries`/`-color_trc` 对 rawvideo 输入**完全无效**（被静默丢弃）。
        // 同一素材、同一标签的 A/B（tests/output/_probe-avif-label）：
        //   标签在 `-i` 之后 ⇒ nclx `primaries/transfer = unknown`（色彩声明丢失，容器谎报）；
        //   标签在 `-i` 之前 ⇒ nclx `smpte432/iec61966-2-1`（正确）。
        // ⚠ 矩阵**不能**跟着前置：前置后 ffmpeg 完全忽略它 —— nclx matrix 变 `unknown`，
        //   且 RGB→YUV 退回默认系数（实测 5133B vs 正确 5153B，像素不同）。
        // 其余格式的标签由 muxer/编码器在**输出侧**读取，故仅 AVIF 拆侧。
        // ⚠ **JXL 与 AVIF 同病**（2026-09-17 实测）：输出侧标签对 rawvideo 输入**被忽略**
        //   —— 同一份 raw + `-color_primaries smpte432`（输出侧）产出的 jxl，
        //   `jxlinfo` 读回 `sRGB primaries`（回落默认）；把同一对标签**前置到 `-i` 之前**
        //   则读回 `P3 primaries`（16910B，与 legacy 参照一致）。故 JXL 亦整体前置。
        //   矩阵对 JXL 无意义（`RgbNative=true`，`OutMatrix` 通常为空）⇒ 留输出侧无副作用。
        bool labelOnInput = ext is "avif" or "jxl";
        string inLabels = labelOnInput ? labels : "";
        string outLabels = labelOnInput ? matrixLabels : labels + matrixLabels;
        string inArgs = $"{inLabels} -f rawvideo -pix_fmt {pixIn} -video_size {w}x{h} -i \"{rawPath}\"";
        string opts, pixFmt, codec = "";
        if (o != null)
        {
            opts = ImageEncoderArgs.BuildEncoderOptions(ext, o);
            pixFmt = ImageEncoderArgs.MapEnginePixFmt(ext, o) ?? "";
            // ── AVIF（2026-09-17 接入）──
            // ① 编码器显式化：传统管线只在用户**选了**编码器时才写 `-c:v`（空 ⇒ ffmpeg 自选）。
            //    实测：avif muxer 的默认视频编码器是 `av1`，ffmpeg 落到 **libaom-av1**，
            //    且产物与显式 `-c:v libaom-av1` **逐字节相同**（641B / C7F59F91…）
            //    ⇒ 引擎沿用同一策略（有则写、无则不写），两条管线的编码器选择因此一致。
            // ② 像素格式：判据**与传统管线逐字同源**（FfmpegCommandBuilder 的通用 pix_fmt 块）——
            //    色度显式 || 位深显式 || 源位深 > 8 ⇒ 写 `-pix_fmt`；否则**不写**。
            //    ⚠ 不能无条件写：不写时 libaom 自选 **gbrp**（RGB 4:4:4 全采样；实测同一素材
            //    legacy 与引擎产物逐字节相同），引擎若擅自写 `yuv420p` 会把 4:4:4 降成 4:2:0
            //    —— 5153B vs 10808B、PSNR 41.7dB，属**静默质量回归**。
            //    位深由**源位深**驱动（见 ImageEncoderArgs.AvifEffectiveBitDepth：不能用
            //    `plan.OutBitDepth` —— 规划层对 YUV 容器恒给 16 ⇒ 8-bit 源会产出 10-bit AVIF）。
            if (ext == "avif")
            {
                if (!string.IsNullOrWhiteSpace(o.Encoder)) codec = $" -c:v {o.Encoder}";
                bool chromaSet = !string.IsNullOrWhiteSpace(o.Chroma)
                                 && !o.Chroma.Equals("auto", StringComparison.OrdinalIgnoreCase);
                var srcBd = FfmpegCommandBuilder.ProbeInputBitDepthForEngine(inputPath);
                if (chromaSet || o.BitDepth.HasValue || srcBd > 8)
                {
                    var bd = ImageEncoderArgs.AvifEffectiveBitDepth(o, srcBd);
                    pixFmt = " -pix_fmt " + ImageEncoderArgs.MapPixFmt("avif", o.Chroma, bd, false, o.ColorRange);
                }
            }
            // ── GIF（2026-09-17 接入）──
            // 编码器显式化策略与 AVIF 同（传统管线 `if (!string.IsNullOrWhiteSpace(options.Encoder))`
            // 对**所有**格式都写 `-c:v`；用户没选 ⇒ 空 ⇒ ffmpeg 自选 `gif`）。
            // 不写 `-pix_fmt`：调色板格式由 paletteuse 输出 pal8，显式 pix_fmt 会与之冲突
            //（传统管线的 `skipGenericPixFmt = fmt is "gif"` 是同一结论）。
            else if (ext == "gif" && !string.IsNullOrWhiteSpace(o.Encoder))
            { codec = $" -c:v {o.Encoder}"; }
        }
        else
        {
            // 无 options（探针/内部调用）：保持历史固定值
            opts = ext switch { "png" or "apng" => " -compression_level 100", "webp" => " -quality 90",
                                "jpg" or "jpeg" => " -q:v 2", "tiff" or "tif" => " -compression deflate", _ => "" };
            pixFmt = "";
        }

        // ── GIF 出口的调色板链与透明通道（2026-09-17 接入）────────────────────────────────
        // ① **调色板链与 legacy 同一份**：`ImageEncoderArgs.BuildGifFilterChain`（含
        //    `palettegen=reserve_transparent=1` / `paletteuse=dither=bayer…`，逐字搬自传统管线）。
        //    实测（512×384）：不带链 18719B/PSNR 32.33dB，带链 17032B/44.33dB ⇒ 不写就是**静默降质**。
        // ② **透明度必须自己补**：引擎 raw 通路是 rgb24/rgb48le（**无 alpha 平面**），而链里的
        //    `reserve_transparent=1` 依赖 alpha ⇒ 直接把 raw 喂进去会把透明**静默拍平**。
        //    实测（32×32 真透明 GIF 素材）：
        //      raw 无 alpha 直入 ⇒ 931B / alphaYMIN=255（全不透明，透明丢失）；
        //      把**原文件**作第二输入、只取它的 alpha 平面再 merge ⇒ 861B / alphaYMIN=0，
        //      与 legacy 直解产物 **PSNR=inf（含 alpha 平面）**，逐像素等价。
        //    ⚠ 只用第二输入的 **alpha**；RGB 一律用**变换后**的像素（原文件的 RGB 是未变换的）。
        // ③ 几何缩放（最长边限制）在解码步已作用于变换后的像素 ⇒ alpha 分支必须套**同一个** scale
        //    滤镜，否则两路尺寸对不上（实测缺了它 ffmpeg 报 "could not choose their formats"）。
        // ④ 不写任何 CICP/矩阵标签：调色板容器没有色彩标注通路（写了也没人读，还可能让 ffmpeg
        //    插入多余的色彩转换）。与 legacy 的 `skipGenericPixFmt` / `fmt != "gif"` 同口径。
        string gifExtraIn = "", gifFilter = "";
        if (ext == "gif" && o != null)
        {
            var gifScale = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(o, inputPath);
            string alphaFmt = plan.OutBitDepth == 8 ? "rgba" : "rgba64le";   // 与 pixIn 同深，否则 alphamerge 无法协商
            string alphaBranch = "[1:v]" + (string.IsNullOrEmpty(gifScale) ? "" : gifScale + ",")
                               + $"format={alphaFmt},alphaextract[a]";
            string merge = $"{alphaBranch};[0:v]format={pixIn}[b];[b][a]alphamerge";
            var gifChain = ImageEncoderArgs.BuildGifFilterChain(o, merge)!;  // merge 非空 ⇒ 恒非 null
            gifExtraIn = $" -i \"{inputPath}\"";
            gifFilter = $" -filter_complex \"{gifChain}\"";
            outLabels = "";
        }
        // ⚠⚠ 2026-09-22（管线审查 §3.4 修复）：**alpha 不能只在 gif 出口补**。
        //   引擎 raw 通路是 rgb24/rgb48le（**无 alpha 平面**）⇒ 只要输入带 alpha 且**需要色彩变换**，
        //   png/apng/tiff/webp 的产物都会被**静默拍平** —— 实机复现（源 `src_alpha.png`，`pix_fmt=rgba`）：
        //     透明源 → png(Display P3) = **rgb24** · tiff = **rgb24** · webp = **yuv420p**（两条管线都丢）。
        //   修法：把 gif 出口既有的「**第二输入只取原文件的 alpha 平面 + `alphamerge`**」推广到这些出口，
        //   并**显式给出带 alpha 的 `-pix_fmt`** —— 否则编码器仍按"无 alpha 的输入帧"推断 ⇒ 补了也白补。
        //   ⚠ 判据与 cjxl/JXR 出口**同源**：同一个 `ProbeInputColorMetadata().hasAlpha`（内部走缓存）。
        //   ⚠ **avif 不在本次范围**：它的 alpha 是**独立辅助流**，引擎出口已登记为「已知不支持」
        //     （`hasAlpha` 硬编码 false、ffmpeg 侧实测也丢）⇒ 保持登记状态，不在此处"顺手修"。
        else if (o != null && ext is "png" or "apng" or "tiff" or "tif" or "webp"
                 && FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath).hasAlpha)
        {
            var aScale = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(o, inputPath);
            string aFmt = plan.OutBitDepth == 8 ? "rgba" : "rgba64le";   // 与 pixIn 同深，否则 alphamerge 无法协商
            string aMerge = $"[1:v]{(string.IsNullOrEmpty(aScale) ? "" : aScale + ",")}"
                          + $"format={aFmt},alphaextract[a];[0:v]format={pixIn}[b];[b][a]alphamerge";
            gifExtraIn = $" -i \"{inputPath}\"";
            // ⚠⚠ **必须限制输出帧数**：底图 `[0:v]` 是 rawvideo（恒 1 帧），而 alpha 源 `[1:v]` 是**原文件** ——
            //   多帧输入（如 10 帧 webp）会给 `[1:v]` 10 帧 ⇒ 滤镜图产出 10 帧 ⇒ 编码器报
            //   `编码失败 (exit -22)`（实测：多帧 webp + 最长边 128 走本分支即复现）。
            //   ⇒ 与 JXR 出口（`EncodeJxrViaJxrEncAppAsync`，`#13 站点 B`）**同一处置**：限制**输出**帧数。
            //   语义也与引擎既有约定一致：**多帧输入只取首帧**（本函数的解码步就带 `-frames:v 1`）。
            gifFilter = $" -filter_complex \"{aMerge}\" -frames:v 1";
            // ⚠ 这里的位深**必须**与 `pixIn`（中间件档）同源，不能改成 `plan.TargetBitDepth`：
            //   上面的滤镜链把帧定成 `format={pixIn}`，交付档若更低就变成 swscale 的**隐式降位**
            //   —— 绕开引擎自己的有序抖动，正是 `ColorKernels.EncodeTo8` 那条弯路（见
            //   `ColorStrategyPlanning` 的 OutBitDepth 注释）。
            //   代价由实测划定（`tests/output/t73/probe2.ps1`，真透明源 alphaYMIN=11）：
            //   非 RGB 原生容器里只有 webp 会因此多写一档（`-pix_fmt yuva420p16le`），
            //   而 libwebp 不吃它 ⇒ ffmpeg 自行谈回 `yuva420p`，产物 pix_fmt/alpha 都与 legacy 同
            //   （YMIN 10 vs 11，差 1 LSB 属有损 webp 正常范围）⇒ **声明与实际交付不符，但无行为后果**。
            //   `--bit-depth 10 -f webp` 同格产物逐字节相同 ⇒ 该档不影响任何可达格的结果。
            var alphaPix = ImageEncoderArgs.MapPixFmt(ext, o.Chroma, plan.OutBitDepth, true, o.ColorRange);
            if (!string.IsNullOrWhiteSpace(alphaPix)) pixFmt = $" -pix_fmt {alphaPix}";
        }
        return $"-y -hide_banner -loglevel error {inArgs}{gifExtraIn}{gifFilter}{outLabels}{codec}{pixFmt}{opts} \"{outputPath}\"";
    }

    /// <summary>
    /// 大帧才值得预先建 16-bit 查表（2×65536 次 pow ≈ 几 ms）；小图直接走解析式，避免固定开销。
    /// HDR输出（不钳位）不适用表，自动回退解析式。
    /// </summary>
    private static (ColorKernels.CurveTables? Src, ColorKernels.CurveTables? Dst) TablesFor(TransformSpec spec, int pixels)
    {
        if (!spec.ClampLinearBeforeEncode || pixels < 256_000) return (null, null);
        if (spec.SrcCurve.Kind == TransferCurve.CurveKind.Table || spec.DstCurve.Kind == TransferCurve.CurveKind.Table)
            return (null, null);
        return (ColorKernels.BuildTables(spec.SrcCurve), ColorKernels.BuildTables(spec.DstCurve));
    }

    /// <summary>整帧模式：读入 → 行带并行变换 → 写出。</summary>
    private static async Task TransformWholeFrameAsync(string src, string dst, TransformSpec spec,
        int w, int h, ColorScheduler sched, CancellationToken ct,
        ColorKernels.CurveTables? stSrc, ColorKernels.CurveTables? stDst, bool out8)
    {
        long bytes = (long)w * h * 6;
        int len = checked((int)bytes);
        int outLen = out8 ? w * h * 3 : len;
        var buf = RentSmart(len);
        var outBuf = RentSmart(outLen);
        try
        {
            await using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None,
                                                  1 << 20, FileOptions.SequentialScan)
                ;
            await fs.ReadExactlyAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);
            // 行带并行：lambda 内不能捕获 Span，改用数组+偏移就地构造 span
            sched.RunBands(h, (rowStart, rowCount) =>
            {
                ct.ThrowIfCancellationRequested();
                int off = rowStart * w * 6, n = rowCount * w * 6;
                if (out8)
                {
                    int oOff = rowStart * w * 3, oN = rowCount * w * 3;
                    ColorKernels.TransformRgb48leToRgb8(new ReadOnlySpan<byte>(buf, off, n), new Span<byte>(outBuf, oOff, oN),
                        spec, rowCount * w, stSrc, stDst, w, rowStart * w);
                }
                else
                    ColorKernels.TransformRgb48le(new ReadOnlySpan<byte>(buf, off, n), new Span<byte>(outBuf, off, n), spec, n / 6, stSrc, stDst);
            });
            await using var os = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None,
                                                1 << 20, FileOptions.SequentialScan);
            await os.WriteAsync(new Memory<byte>(outBuf, 0, outLen), ct).ConfigureAwait(false);
        }
        finally
        {
            ReturnSmart(buf); ReturnSmart(outBuf);
        }
    }

    /// <summary>
    /// 缓冲分配策略：BufferPool.Rent 内部会 `Math.Min(length, 256MB)` 钳制，
    /// 请求大于池上限时会拿到**小于请求量**的数组（直接用会越界）。
    /// 因此只对小尺寸走池，整帧级大缓冲一律普通数组（ArrayPool 对 >1MB 本来就是新分配，无损失）。
    /// </summary>
    private const int PoolUseLimitBytes = 4 * 1024 * 1024;
    private static byte[] RentSmart(int len) => len <= PoolUseLimitBytes ? BufferPool.RentByte(len) : new byte[len];
    private static void ReturnSmart(byte[] b)
    {
        if (b.Length <= PoolUseLimitBytes) BufferPool.ReturnByte(b);
        // 大于阈值的缓冲未进池，直接交给 GC
    }

    /// <summary>流式模式：分块读→变换→写（全帧不驻留，内存受控）。</summary>
    private static async Task TransformStreamedAsync(string src, string dst, TransformSpec spec,
        int w, int h, ColorScheduler sched, CancellationToken ct,
        ColorKernels.CurveTables? stSrc, ColorKernels.CurveTables? stDst, bool out8)
    {
        int rows = Math.Max(1, sched.TileRows > 0 ? sched.TileRows : 64);
        int tileBytes = rows * w * 6;
        var inBuf = BufferPool.RentByte(tileBytes);
        var outBuf = BufferPool.RentByte(out8 ? rows * w * 3 : tileBytes);
        try
        {
            await using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 20, FileOptions.SequentialScan);
            await using var os = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
            for (int rowStart = 0; rowStart < h; rowStart += rows)
            {
                ct.ThrowIfCancellationRequested();
                int rc = Math.Min(rows, h - rowStart);
                int n = rc * w, bytes = n * 6, oBytes = out8 ? n * 3 : bytes;
                try { await fs.ReadExactlyAsync(inBuf.AsMemory(0, bytes), ct).ConfigureAwait(false); }
                catch (EndOfStreamException) { break; }
                if (out8)
                    ColorKernels.TransformRgb48leToRgb8(inBuf.AsSpan(0, bytes), outBuf.AsSpan(0, oBytes), spec, n, stSrc, stDst, w, rowStart * w);
                else
                    ColorKernels.TransformRgb48le(inBuf.AsSpan(0, bytes), outBuf.AsSpan(0, bytes), spec, n, stSrc, stDst);
                await os.WriteAsync(outBuf.AsMemory(0, oBytes), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            BufferPool.ReturnByte(inBuf); BufferPool.ReturnByte(outBuf);
        }
    }

    /// <summary>
    /// 整帧实测内容峰值（nits）：按块顺序读已解码的 rgb48le 中间文件，内存受控且与运行模式无关。
    /// 返回 0 表示“测不出可用结果”（空文件/像素数不符），调用方必须**保持名义值**而不是猜一个。
    /// </summary>
    public static async Task<double> MeasureContentPeakNitsAsync(string rawRgb48Path, int pixels,
        TransformSpec spec, ColorKernels.CurveTables? srcTab, CancellationToken ct = default)
    {
        var tone = spec.Tone;
        if (tone == null || pixels <= 0 || !File.Exists(rawRgb48Path)) return 0;
        const int ChunkPixels = 1 << 16;                       // 64K 像素 ≈ 1 MB rgb48：够喂顺序读，也不吃内存
        var buf = new byte[Math.Min(pixels, ChunkPixels) * 6];
        double peakLin = 0;
        await using var fs = new FileStream(rawRgb48Path, FileMode.Open, FileAccess.Read, FileShare.None,
                                            1 << 20, FileOptions.SequentialScan);
        for (int done = 0; done < pixels; )
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(pixels - done, ChunkPixels);
            try { await fs.ReadExactlyAsync(buf.AsMemory(0, n * 6), ct).ConfigureAwait(false); }
            catch (EndOfStreamException) { break; }           // 尺寸不符 ⇒ 用已读部分（下面仍会被钳制）
            double v = ColorKernels.MeasurePeakLinearRgb48(new ReadOnlySpan<byte>(buf, 0, n * 6), n, spec, srcTab);
            if (v > peakLin) peakLin = v;
            done += n;
        }
        return peakLin * Math.Max(1.0, tone.SdrWhiteNits);     // 引擎线性域“1.0 = SDR 白”→ nits
    }

    /// <summary>
    /// 把实测峰值装回 spec（并取消 MeasurePeak 标记，避免重复扫描）。
    /// 钳制区间 = [SdrWhiteNits, 名义峰值]：**实测到很暗的图也不会因此增亮**（headroom 至少 1），
    /// 也不会超过源曲线代码上限（PQ=10000）。改的是副本：<c>ToSpec()</c> 与 Plan.Tone 同实例，
    /// 原地改会污染规划结果（日志/合同矩阵看到的就不是同一个值了）。
    /// </summary>
    private static void ApplyMeasuredPeak(TransformSpec spec, Stats st, double measured, Action<string>? log)
    {
        var tone = spec.Tone!;
        double nominal = tone.PeakNits;
        spec.Tone = tone.Clone();
        spec.Tone.MeasurePeak = false;
        if (!double.IsFinite(measured) || measured <= 0)
        {
            log?.Invoke($"[色彩] 峰值实测不可用（读得 {measured:F1}nit）⇒ 保持名义 {nominal:F0}nit\n");
            return;
        }
        double applied = Math.Clamp(measured, tone.SdrWhiteNits, Math.Max(tone.SdrWhiteNits, nominal));
        st.MeasuredPeakNits = measured;                 // 存**原始实测值**（钳制前），便于分别审计测量与策略
        spec.Tone.PeakNits = applied;
        log?.Invoke($"[色彩] 峰值来源=整帧实测：{measured:F0}nit ⇒ 采用 {applied:F0}nit"
                  + $"（名义兜底 {nominal:F0}nit，{spec.Tone}）\n");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  供验证/基准复用的低层步骤（避免测试代码自己拼命令行）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>解码任意图为 rgb48le 原始字节（无色彩变换）。</summary>
    public static async Task<byte[]> DecodeToRgb48Async(string path, int w, int h, CancellationToken ct = default, string? ffmpegOverride = null)
    {
        var ffmpeg = ffmpegOverride ?? AppSettingsService.Current.FfmpegPath;
        var tmp = Path.Combine(PlatformServices.GetTempDir(), $"cmsd_{Guid.NewGuid():N}.rgb48");
        try
        {
            // ⚠ `-frames:v 1`：调用方（探针 psnr / xcheck）一律按 `w×h` **单帧**解释返回的字节
            //   （`Psnr16` 逐字节对拍、按 `w*h` 分帧）⇒ 多帧容器必须只取首帧，否则返回 N 帧拼接、
            //   对拍结论失去意义（与 `TransformFileAsync` 主解码同一个缺陷形态，2026-09-18 同批修）。
            int ec = await RunToEndAsync(ffmpeg,
                $"-y -hide_banner -loglevel error -i \"{path}\" -frames:v 1 -vf \"format=rgb48le\" -f rawvideo \"{tmp}\"", null, ct).ConfigureAwait(false);
            if (ec != 0 || !File.Exists(tmp)) throw new InvalidOperationException($"解码失败 exit={ec}");
            return await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false);
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>把 rgb48le 原始字节写成 16-bit PNG（可选 CICP 标注）。</summary>
    public static async Task WriteRgb48ToPngAsync(byte[] rgb48, int w, int h, string outPath,
        string? primaries = null, string? trc = null, string? matrix = null, CancellationToken ct = default, string? ffmpegOverride = null)
    {
        var ffmpeg = ffmpegOverride ?? AppSettingsService.Current.FfmpegPath;
        var tmp = Path.Combine(PlatformServices.GetTempDir(), $"cmse_{Guid.NewGuid():N}.rgb48");
        try
        {
            await File.WriteAllBytesAsync(tmp, rgb48, ct).ConfigureAwait(false);
            string labels = (!string.IsNullOrWhiteSpace(primaries) ? $" -color_primaries {primaries}" : "")
                          + (!string.IsNullOrWhiteSpace(trc) ? $" -color_trc {trc}" : "")
                          + (!string.IsNullOrWhiteSpace(matrix) ? $" -colorspace {matrix}" : "");
            int ec = await RunToEndAsync(ffmpeg,
                $"-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb48le -video_size {w}x{h} -i \"{tmp}\"{labels} \"{outPath}\"", null, ct)
                .ConfigureAwait(false);
            if (ec != 0) throw new InvalidOperationException($"编码失败 exit={ec}");
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// 引擎的几何真值入口 —— **显示**尺寸（autorotate 之后 ffmpeg 实际吐出的宽高）。
    /// <para>⚠ 2026-09-30 前本方法是自写的一份 `-show_entries stream=width,height`（= **coded** 尺寸），
    /// 而下面的解码段从不写 `-noautorotate` ⇒ 带取向标签的输入（实测 <c>Orientation=8</c> 的
    /// 6000×4000 16-bit TIFF）解码出的是 4000×6000，却被按 coded 尺寸标注给编码器 ⇒ **每行错位重排**
    /// （产物 vs 真值 PSNR **7.97 dB**；把同一段 raw 按 4000×6000 重排再旋回则 **inf**，逐字节等）。
    /// 长度自检拦不住它：<c>rawLen = w*h*6</c> 在转置下恒等。⇒ 口径改由 <see cref="ImageGeometry"/>
    /// 统一提供，本方法只保留既有签名与既有 ffprobe 解析来源（<c>AppSettingsService.FfmprobePath</c>）。</para>
    /// </summary>
    public static Task<(int w, int h)> ProbeSizeAsync(string path, string? ffmpegOverride = null, CancellationToken ct = default)
        => ImageGeometry.DisplaySizeAsync(path, AppSettingsService.Current.FfprobePath, log: null, ct);

    private static async Task<int> RunToEndAsync(string exe, string args, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) { log?.Invoke("[色彩] ❌ ffmpeg 不可用\n"); return -1; }
        // 命令行必须可追溯：A/B 对拍时"两条管线到底差在哪个参数"只能靠这一行回答
        //（实测踩过：jpg 22dB 差异查了半天，就是因为引擎没记命令）。
        log?.Invoke($"[色彩] ffmpeg {args}\n");
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true,
        });
        if (p == null) return -1;
        string err = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(err)) log?.Invoke(err.TrimEnd() + "\n");
        return p.ExitCode;
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    /// <summary>日志用的小载体（避免为一条统计去构造对象）。</summary>
    private sealed class ProcessInfo { }
}
