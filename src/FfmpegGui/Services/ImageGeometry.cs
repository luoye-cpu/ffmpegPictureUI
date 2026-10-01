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
}
