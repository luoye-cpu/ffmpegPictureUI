using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services;

/// <summary>
/// 图像**显示尺寸**的唯一探测实现（coded 尺寸 + 解码器实际会施加的旋转）。
///
/// <para><b>为什么需要它</b>（2026-09-30 实机确证的缺陷）：ffmpeg 对带取向标签的静像会
/// <b>autorotate</b> —— 本仓随包的 ffmpeg（git-2026-09-26）对 <c>Orientation=8</c> 的 16-bit TIFF
/// 在**解码帧**上挂 <c>3x3 displaymatrix rotation=90</c>，于是 <c>-i</c> 之后滤镜链里跑的像素是
/// 4000×6000，而 <c>ffprobe -show_entries stream=width,height</c> 报的仍是 coded 的 6000×4000。
/// 全仓没有任何一处写 <c>-noautorotate</c> ⇒ 旋正是既成事实，且元数据恢复策略
/// （<see cref="ExifToolService"/> 的 RAW 拍摄字段白名单、<c>QueueProcessor</c> 的 <c>Orientation</c> 排除）
/// 正是**依赖**「像素已被解码器旋正」才排除该标签的。因此**显示尺寸才是唯一正确的几何口径**，
/// 继续拿 coded 尺寸去标注 rawvideo 会把每一行错位重排（实测产物 PSNR 7.97 dB ⇒ 剪切成花）。</para>
///
/// <para><b>为什么必须收在这一点</b>：本方法存在前，"探宽高"在仓内有**三份**实现
/// （<c>RawColorPipeline.ProbeSizeAsync</c>、<c>GainMapDecoder.ProbeSizeAsync</c>、
/// <c>QualityAnalysisService.GetResolutionAsync</c>），全部只读 coded 尺寸 ⇒ 修一处漏两处。
/// 现在三者一律转发到本类，由 <c>verify-geometry-single-source</c> 门禁锁住"不得再出现第二份
/// <c>-show_entries stream=width,height</c>"。</para>
///
/// <para><b>成本</b>：与旧的 csv 探测同为一次 ffprobe 进程（实测 256×256 PNG 均势 ~100 ms，
/// 进程启动占大头）；差异只在 ffprobe 需解出第一帧才能看到帧级 side data。</para>
/// </summary>
public static class ImageGeometry
{
    /// <summary>与 <c>FfmpegCommandBuilder</c> 的既有探测同一水位：5 s 后杀树，宁可回退也不挂住队列。</summary>
    public const int TimeoutMs = 5000;

    /// <summary>
    /// 纯函数：coded 尺寸 + 解码器会施加的旋转角 ⇒ 实际帧尺寸。
    /// <para>只有 ±90（即 <c>|rotation| % 180 == 90</c>）交换宽高；0/±180/未知 ⇒ 原样。
    /// 角度取绝对值后再取模，是因为 ffprobe 对 <c>Orientation=8</c> 报 <c>90</c>、
    /// 对 <c>Orientation=6</c> 报 <c>-90</c>，两者都要交换。</para>
    /// </summary>
    public static (int w, int h) ApplyRotation(int codedW, int codedH, int rotationDeg)
    {
        if (Math.Abs(rotationDeg) % 180 != 90) return (codedW, codedH);
        return (codedH, codedW);
    }

    /// <summary>探测参数：一次调用同时取回 stream 的 coded 宽高与**第一帧**的 side data。</summary>
    /// <param name="inputArgsPrefix">解复用器提示（如增益图裸流的 <c>-f hevc </c>），须落在 <c>-i</c> 之前。</param>
    public static string BuildProbeArguments(string path, string inputArgsPrefix = "")
        => $"-v error {inputArgsPrefix}-select_streams v:0 -read_intervals \"%+#1\" "
         + $"-show_entries stream=width,height -show_entries frame_side_data_list -of json \"{path}\"";

    /// <summary>
    /// 纯函数：从上面那条命令的 JSON 里解析出显示尺寸。
    /// <para>⚠ 用 <see cref="JsonDocument"/> 而不是沿 <c>ExtractJsonNumber</c> 的"找键名再往后读数字"：
    /// 那套写法只认正数（<c>v &gt; 0</c> 才返回），而 <c>rotation</c> 的合法值**包含 0 和负数**，
    /// 且 JSON 里 <c>"displaymatrix"</c> 的字符串值自带换行与数字，裸扫会读到矩阵元素。</para>
    /// <para>取不到 streams（ffprobe 报错/空文件）⇒ <c>(0,0)</c>，由调用方按各自的既有兜底处理。</para>
    /// </summary>
    public static (int w, int h) ParseDisplaySize(string ffprobeJson)
    {
        if (string.IsNullOrWhiteSpace(ffprobeJson)) return (0, 0);
        try
        {
            using var doc = JsonDocument.Parse(ffprobeJson);
            int codedW = 0, codedH = 0;
            if (doc.RootElement.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array && streams.GetArrayLength() > 0)
            {
                var s = streams[0];
                codedW = ReadInt(s, "width");
                codedH = ReadInt(s, "height");
            }
            if (codedW <= 0 || codedH <= 0) return (0, 0);
            return ApplyRotation(codedW, codedH, ReadRotation(doc.RootElement));
        }
        catch (JsonException) { return (0, 0); }
    }

    /// <summary>
    /// 取第一个 <c>displaymatrix</c> side data 的 <c>rotation</c>；没有就是 0（不旋转）。
    /// <para>只认 <c>side_data_type == "3x3 displaymatrix"</c> 的那条：同帧还挂着 <c>EXIF metadata</c>
    /// 等其他 side data，逐条扫键名会把无关数字当成角度。</para>
    /// </summary>
    private static int ReadRotation(JsonElement root)
    {
        if (!root.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
            return 0;
        foreach (var f in frames.EnumerateArray())
        {
            if (!f.TryGetProperty("side_data_list", out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var sd in list.EnumerateArray())
            {
                if (!sd.TryGetProperty("side_data_type", out var t)
                    || t.ValueKind != JsonValueKind.String
                    || !t.GetString()!.Contains("displaymatrix", StringComparison.OrdinalIgnoreCase))
                    continue;
                return ReadInt(sd, "rotation");
            }
        }
        return 0;
    }

    private static int ReadInt(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>
    /// 探测 <paramref name="path"/> 的**显示**尺寸；失败返回 <c>(0,0)</c>（调用方兜底，本类不猜）。
    /// <para>⚠ 管道输入（<c>"-"</c>）恒返回 <c>(0,0)</c>：ffprobe 不能对 stdin 二次取帧，
    /// 这与 <c>ProbePeakNits</c> 的既有处理一致。</para>
    /// <para>stderr 必须排空（P2-3 的实测教训：管道写满会把子进程堵死，<c>WaitForExit</c> 永不返回）。</para>
    /// </summary>
    public static async Task<(int w, int h)> DisplaySizeAsync(string path, string? ffprobePath,
        Action<string>? log = null, CancellationToken ct = default, string inputArgsPrefix = "")
    {
        if (string.IsNullOrEmpty(path) || path == "-") return (0, 0);
        if (string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffprobePath)) return (0, 0);
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = BuildProbeArguments(path, inputArgsPrefix),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p == null) return (0, 0);
            // 两个读取任务**先挂起不 await**（P2-3 的实测教训：不排空管道，子进程写满 ~4KB 就永不退出），
            // 但也不能在退出前 await —— 那会把下面的超时判据变成死代码（`#26` 修的就是这个）。
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeoutMs);
            using var reg = timeoutCts.Token.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                var kind = ct.IsCancellationRequested ? "被取消" : $"超时（{TimeoutMs / 1000}s）";
                var msg = $"[probe] 显示尺寸探测{kind} ⇒ 已终止：{Path.GetFileName(path)}（按未探测处理）";
                if (log != null) log(msg + "\n"); else Trace.WriteLine(msg);
                return (0, 0);
            }
            var json = await outTask.ConfigureAwait(false);
            var err = await errTask.ConfigureAwait(false);
            var (w, h) = ParseDisplaySize(json);
            // ── 分块源必须以**组的整幅尺寸**为准：上面那条 stream 探测取的 v:0 是第一块 ──
            // 实测真图（5888×4416，108 块 512×512）：不覆盖时本函数返回 512×512，
            // 于是「最长边」判据以为已经够小 ⇒ 静默不缩放（这条是 #3 的用户可见后果）。
            // 只有 tiles>1 才覆盖：tiles==1 时 ffmpeg 不建图，v:0 就是整幅，覆盖反而多此一举。
            // 取不到组（旧 ffmpeg / 非 ISOBMFF 图像）⇒ 原样返回，不猜。
            if (TryGetTileGroupInfo(path, out int gt, out int gw, out int gh, log, ct)
                && gt > 1 && gw > 0 && gh > 0)
                return (gw, gh);
            if (w <= 0 || h <= 0)
            {
                var msg = $"[probe] 显示尺寸探测无结果：{Path.GetFileName(path)}"
                        + (string.IsNullOrWhiteSpace(err) ? "" : $"（ffprobe: {err.Trim()}）");
                if (log != null) log(msg + "\n"); else Trace.WriteLine(msg);
            }
            return (w, h);
        }
        catch (Exception ex)
        {
            var msg = $"[probe] 显示尺寸探测异常：{Path.GetFileName(path)} ⇒ {ex.Message}";
            if (log != null) log(msg + "\n"); else Trace.WriteLine(msg);
            return (0, 0);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  分块（Tile Grid）流组 —— 整幅显示尺寸的唯一来源
    // ══════════════════════════════════════════════════════════════════
    //
    // 为什么必须在这里：上面那条 stream 探测写的是 `-select_streams v:0`，而**分块图的 v:0 是第一块**。
    // 实测（真图 5888×4416，108 块 512×512）：stream 口径给出 **512×512** ⇒ 一切按显示尺寸做的算术
    // 都错且不报错，最要命的是 `BuildMaxDimensionScaleFilter` 的"锁哪条边"判据——它据此判定
    // 「512 ≤ 1920，无需缩放」⇒ 用户勾的最长边被**静默丢弃**，交付 5888 宽。
    // 组的 `components[0].width/height` 由容器分块表直接给出（同文件 coded 6144×4608 / display 5888×4416），
    // 属于"整幅"口径，且与 `ProbeWithFfprobe` 的 coded 口径互不干扰。
    //
    // ⚠ 版本相关：`stream_groups` 自 2025-10-30 起才有（`fftools/ffmpeg_demux.c::istg_parse_tile_grid`）。
    //   取不到 ⇒ 本函数返回 false ⇒ 调用方**保留原值**（宁可维持旧行为，也不猜一个尺寸）。
    private const double GroupCacheTtlSeconds = 10.0;
    private static readonly Dictionary<string, (DateTime Time, int Tiles, int W, int H)> GroupCache = new();
    private static readonly object GroupCacheLock = new();

    /// <summary>能携带 tile grid 的容器（ISOBMFF 图像家族）。只有它们才付这次额外探测。</summary>
    private static readonly string[] TileCapableExtensions =
        { ".heic", ".heics", ".heif", ".heifs", ".hif", ".avif", ".avifs" };

    public static bool IsTileCapableContainer(string path)
        => !string.IsNullOrEmpty(path)
           && Array.Exists(TileCapableExtensions,
                e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解析该文件的 Tile Grid 流组：<paramref name="tiles"/> = 块数，
    /// <paramref name="displayW/H"/> = 组声明的**整幅显示**尺寸（取不到时 0）。
    /// 返回 true 只表示"读到了 Tile Grid 组信息"；<c>tiles == 1</c> 时 ffmpeg 不建图、v:0 就是整幅，
    /// 调用方**不得**用组值覆盖 stream 值（故两者都判）。
    /// </summary>
    public static bool TryGetTileGroupInfo(string path, out int tiles, out int displayW, out int displayH,
        Action<string>? log = null, CancellationToken ct = default)
    {
        tiles = 0; displayW = 0; displayH = 0;
        if (string.IsNullOrEmpty(path) || path == "-" || !IsTileCapableContainer(path)) return false;
        lock (GroupCacheLock)
        {
            if (GroupCache.TryGetValue(path, out var hit)
                && (DateTime.UtcNow - hit.Time).TotalSeconds < GroupCacheTtlSeconds)
            {
                tiles = hit.Tiles; displayW = hit.W; displayH = hit.H;
                return hit.Tiles > 0;
            }
        }
        try
        {
            var ffprobe = PlatformServices.ResolveFfprobePath(AppSettingsService.Current.FfmpegPath);
            if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe)) return false;
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -show_stream_groups -of json \"{path}\"",
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                RedirectStandardError = true,   // 与本文件 DisplaySizeAsync 同纪律：管道必须排空
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p == null) return false;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            // 本方法是**同步**入口（被发射判据与 DisplaySizeAsync 双向调用）⇒ 按本仓既有纪律：
            // 先挂两个读取任务再 WaitForExit(ms)，超时/取消都杀进程树；绝不在退出前 await 读（那会让超时成死代码）。
            using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
            if (!p.WaitForExit(TimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                var kind = ct.IsCancellationRequested ? "被取消" : $"超时（{TimeoutMs / 1000}s）";
                var msg = $"[probe] 流组探测{kind} ⇒ 已终止：{Path.GetFileName(path)}（按非组处理）";
                if (log != null) log(msg + "\n"); else Trace.WriteLine(msg);
                return false;
            }
            var json = outTask.GetAwaiter().GetResult();
            errTask.GetAwaiter().GetResult();
            ParseTileGroup(json, out tiles, out displayW, out displayH);
        }
        catch (Exception ex)
        {
            var msg = $"[probe] 流组探测异常：{Path.GetFileName(path)} ⇒ {ex.Message}（按非组处理）";
            if (log != null) log(msg + "\n"); else Trace.WriteLine(msg);
            tiles = 0; displayW = 0; displayH = 0;
            return false;
        }
        lock (GroupCacheLock)
        {
            if (GroupCache.Count >= 256) GroupCache.Clear();
            GroupCache[path] = (DateTime.UtcNow, tiles, displayW, displayH);
        }
        return tiles > 0;
    }

    /// <summary>
    /// 纯函数：从 `-show_stream_groups` 的 JSON 里取 Tile Grid 分量。
    /// 用 <see cref="JsonDocument"/> 而不是扫键名——本文件 :56 已写明理由（<c>"width"</c> 会撞上
    /// <c>"coded_width"</c>，而组的两个口径必须分清：display 5888×4416 与 coded 6144×4608）。
    /// </summary>
    public static void ParseTileGroup(string ffprobeJson, out int tiles, out int displayW, out int displayH)
    {
        tiles = 0; displayW = 0; displayH = 0;
        if (string.IsNullOrWhiteSpace(ffprobeJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(ffprobeJson);
            if (!doc.RootElement.TryGetProperty("stream_groups", out var groups)
                || groups.ValueKind != JsonValueKind.Array) return;
            foreach (var g in groups.EnumerateArray())
            {
                if (!g.TryGetProperty("type", out var ty) || ty.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(ty.GetString(), "Tile Grid", StringComparison.OrdinalIgnoreCase)) continue;
                if (!g.TryGetProperty("components", out var comps)
                    || comps.ValueKind != JsonValueKind.Array || comps.GetArrayLength() == 0) return;
                var c = comps[0];
                tiles = ReadInt(c, "nb_tiles");
                displayW = ReadInt(c, "width");
                displayH = ReadInt(c, "height");
                return;
            }
        }
        catch (JsonException) { tiles = 0; displayW = 0; displayH = 0; }
    }
}
