using System.Diagnostics;
using System.Text.Json;

namespace FfmpegGui.Services;

/// <summary>
/// RAW 图像预处理服务。
/// 使用 dngtool (LibRaw + Adobe DNG SDK 1.7.1) 将相机 RAW/Bayer 传感器数据去马赛克为线性 16-bit TIFF，
/// 解决 ffmpeg 无法直接处理 Bayer RAW 的问题以及色彩映射错误。
/// dngtool 支持 DNG 1.7 JXL 压缩 (需 DNG SDK)，并支持 DNG 编码输出。
///
/// 支持格式: DNG (含 1.7 JXL), CR2, CR3, NEF, ARW, ORF, RAF, RW2, PEF, 3FR, SRW, ...
/// </summary>
public static class RawService
{
    private static string? _detectedPath;
    private static bool _detected;

    /// <summary>当前引擎是否为 dngtool (支持 DNG 1.7 JXL 解码/编码)</summary>
    public static bool IsDngTool => _detectedPath != null &&
        Path.GetFileName(_detectedPath).StartsWith("dngtool", StringComparison.OrdinalIgnoreCase);

    /// <summary>已知 RAW 文件扩展名（小写）</summary>
    public static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dng", ".cr2", ".cr3", ".crw",  // Canon
        ".nef", ".nrw",                   // Nikon
        ".arw", ".srf", ".sr2",           // Sony
        ".orf",                           // Olympus
        ".raf",                           // Fujifilm
        ".rw2", ".rwl",                   // Panasonic
        ".pef",                           // Pentax
        ".3fr",                           // Hasselblad
        ".srw",                           // Samsung
        ".mrw",                           // Minolta
        // ⚠ 2026-09-21：**移除 `.x3f`（Sigma Foveon）** —— 实测 LibRaw **不支持**该格式：
        //   `dngtool -info *.X3F` ⇒ "Unsupported file format or not RAW file"；
        //   4 个 X3F 样本（DP1 / SD14 / SD15）**全部失败**。
        //   保留它会让文件对话框"声称支持"却必然失败 ⇒ 「UI 与真实不符」。
        ".erf",                           // Epson
        ".kdc", ".dcr",                   // Kodak
        ".mef",                           // Mamiya
        ".mos",                           // Leaf
        ".iiq",                           // Phase One
        ".bay", ".raw",                   // Generic
    };

    public static bool IsRawFile(string path)
    {
        var ext = Path.GetExtension(path);
        return RawExtensions.Contains(ext);
    }

    public static bool IsAvailable => Detect();

    public static string? DetectedPath => _detectedPath;

    public static bool Detect()
    {
        if (_detected) return _detectedPath != null;
        _detected = true;

        // ① dngtool (新引擎): 手动路径 → PLAN → artifacts 目录 → ffmpeg/程序同目录 → PATH（全部非递归）
        //   ⚠ 声明与实现必须一致（本文件原先漏了两级）：
        //     · 原"同目录"只查**程序目录**，漏了 ffmpeg 同目录 —— 兄弟服务（Cjxl/Jxr/ExifTool）两级都查；
        //     · 原链里**没有 artifacts 目录**，而 `PreProcessAsync` 的报错文案恰恰让用户把 dngtool 放进
        //       `PLAN/artifacts/`，`ProbeAllTools` 的 dngtool 行也按 artifacts 找 ⇒ 两份口径可以各报一半。
        //   ⚠ 刻意**不补**"系统扩展搜索"那一级：dngtool 是自研随包二进制，补了只会把"确实没装"的场景
        //     变成每次最多 `ExtendedScanTotalBudgetSec = 60 s` 的全机扫描（零收益、高代价）。
        // ⚠ 每一级的判据是「文件在」**且**「探一次确认真的起得来」，起不来就**继续下一级**（2026-09-23）。
        //   动机：过去只判 `File.Exists` ⇒ 落进 `WindowsApps` 那种 ACL 拒绝启动的 exe 时
        //   `IsAvailable == true`、面板报 ✅，而 `PreProcessAsync` 一跑就 `InvalidApplication`
        //   （且已产出的成品会被半成品清理删掉）。
        //   ⚠ 探测必须**在本方法里**做，不能只靠 `ProbeAllTools` 把结论写进 `PlatformServices` 的备忘录：
        //     本方法开头有 `_detected` 一次性缓存，GUI Step1 先跑完 ⇒ 后来探出的反例**永远回流不到**
        //     队列所读的那个值。（实测踩过：把垃圾 exe 指到 manual 级，面板照样 ✅ ⇒ 备忘录式修法不成立。）
        //   ⚠ 只有 `LaunchFailed` 算"起不来"，**超时不算**（见 `ExecutableProbeResult.LaunchFailed` 注释）。
        foreach (var cand in EnumerateCandidates())
        {
            if (!Launchable(cand)) continue;
            _detectedPath = cand;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 六级候选，**顺序即优先级**：手动 → PLAN → artifacts → ffmpeg 同目录 → 程序同目录 → PATH（全部非递归）。
    /// 与逐块写法逐字等价，只是把"命中即返回"拆成"先按优先级列出、再逐个验起得起不来"。
    /// </summary>
    private static IEnumerable<string> EnumerateCandidates()
    {
        var manual = AppSettingsService.Current.DngToolPath;
        if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual)) yield return manual;

        var planDng = PlatformServices.TryFindInPlanFolder(PlatformServices.DngToolName);
        if (planDng != null && File.Exists(planDng)) yield return planDng;

        var artifactsDir = AppSettingsService.Current.WindowsArtifactsDir;
        if (!string.IsNullOrWhiteSpace(artifactsDir))
        {
            var inArtifacts = Path.Combine(artifactsDir, PlatformServices.DngToolName);
            if (File.Exists(inArtifacts)) yield return inArtifacts;
        }

        var ffmpegDir = AppSettingsService.Current.FfmpegDir;
        if (!string.IsNullOrWhiteSpace(ffmpegDir))
        {
            var besideFfmpeg = Path.Combine(ffmpegDir, PlatformServices.DngToolName);
            if (File.Exists(besideFfmpeg)) yield return besideFfmpeg;
        }

        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var localDng = Path.Combine(exeDir, PlatformServices.DngToolName);
        if (File.Exists(localDng)) yield return localDng;

        if (PlatformServices.TryFindInPath(PlatformServices.DngToolName, out var dngPath)
            && !string.IsNullOrWhiteSpace(dngPath) && File.Exists(dngPath!))
            yield return dngPath!;
    }

    /// <summary>
    /// 该候选是否真能启动。先查 <see cref="PlatformServices.IsToolUnlaunchable"/> 备忘录
    /// （同一路径每进程最多探一次），未知才用 `CanLaunch` 验一次 ⇒ **单次启动、界内 ~1 s**。
    /// ⚠ 这里**不能**用 `ProbeExecutable`：那个原语会依次试 5 组参数、每组最多等 2 s（最坏 ~10 s），
    ///   而本方法经 `IsAvailable` 挂在 `InitControls()` / `RefreshArtifactsServices()` 这类
    ///   **UI 线程**调用点上（"启动"与"重新检测/换目录"都是用户必点的路径）⇒ 未声明的上界会冻住窗口。
    /// </summary>
    private static bool Launchable(string path)
    {
        if (PlatformServices.IsToolUnlaunchable(path)) return false;
        if (!ExternalToolsDetector.CanLaunch(path))
        {
            PlatformServices.RecordToolUnlaunchable(path);
            return false;
        }
        PlatformServices.RecordToolLaunchable(path);
        return true;
    }

    public static void ClearCache()
    {
        _detected = false;
        _detectedPath = null;
    }

    /// <summary>
    /// 将 RAW 文件预处理为线性 16-bit TIFF。
    /// 优先使用 dngtool (LibRaw + DNG SDK, 支持 DNG 1.7 JXL 压缩)；
    /// dngtool 不可用时无法预处理 RAW 文件。
    /// </summary>
    /// <param name="rawPath">RAW 文件路径</param>
    /// <param name="outputTiffPath">输出 TIFF 路径</param>
    /// <param name="log">日志回调</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="highlightMode">高光模式 (LibRaw -H: 0=裁剪, 1=恢复, 2=blend)。2026-08-14 起从 UI 传递。</param>
    /// <param name="threads">多线程数 (0=自动用硬件并发, 1=单线程)。2026-08-15 新增。</param>
    /// <returns>成功返回 true</returns>
    public static async Task<bool> PreProcessAsync(
        string rawPath, string outputTiffPath,
        Action<string>? log = null, CancellationToken ct = default,
        int highlightMode = 1, int threads = 0)
    {
        if (!IsAvailable)
        {
            log?.Invoke("[RAW] 未检测到 RAW 解码引擎 (dngtool)，无法预处理 RAW 文件。\n");
            log?.Invoke("[RAW] 请将 dngtool.exe 放入 PLAN/artifacts/ 目录\n");
            return false;
        }

        // ── dngtool: 唯一引擎 (LibRaw + DNG SDK, 支持 DNG 1.7 JXL) ──
        log?.Invoke($"[RAW] dngtool 去马赛克: {Path.GetFileName(rawPath)}\n");
        // 去马赛克参数: -d 线性 RGB, -T TIFF, -o 0 相机空间, -q 3 AHD, -W 相机白平衡, -H <mode> 高光, -6 16-bit
        // dngtool 扩展: -i 输入, -O 输出
        var dngArgs = $"-d -T -o 0 -q 3 -W -H {highlightMode} -6 -i \"{rawPath}\" -O \"{outputTiffPath}\"";
        // 多线程 (2026-08-15): 0=自动(硬件并发), 1=单线程, N=指定
        if (threads > 0)
            dngArgs += $" -threads {threads}";
        log?.Invoke($"[RAW] dngtool {dngArgs}\n");

        var ok = await RunProcessAsync(_detectedPath!, dngArgs, outputTiffPath, "dngtool", log, ct);
        if (ok)
        {
            log?.Invoke($"[RAW] ✅ dngtool 预处理完成 (DNG 1.7 JXL 支持)\n");
            return true;
        }

        log?.Invoke("[RAW] ⚠️ dngtool 处理失败，无法继续（RAW 解码仅支持 dngtool 引擎）。\n");
        return false;
    }

    /// <summary>
    /// 构建 `dngtool -e`（DNG 编码）的实参串 —— **唯一真源**。
    ///
    /// ⚠ 抽出来的动机（2026-10-07，D-1）：`--dry-run` 预览曾**自己另写一份** `dngtool` 命令
    ///   （旧 `MainWindow.BuildQueueItemCommand`），与这里**两处判据**，实测已经漂移两处：
    ///     `--dng-bit-depth 8` 漏 `-4`，且漏 `-jxlq`（JXL 质量）。
    ///   ⇒ 预览声称的命令与真实执行**不是同一条**，任何以 dry-run 为准的审计得到**假阴性**。
    ///   现在预览与执行都调本方法 ⇒ 结构上不可能再漂移（判据只有一份）。
    /// </summary>
    /// <param name="rawPath">输入 RAW/DNG 文件</param>
    /// <param name="outputDngPath">输出 DNG 路径</param>
    /// <param name="compression">0=无损 JPEG, 1=JXL</param>
    /// <param name="jxlQuality">JXL 质量 (0=无损, 1-100=有损)</param>
    /// <param name="linear">true=输出线性 DNG（无 CFA，体积更小），false=保留 CFA (Bayer)</param>
    /// <param name="jxlEffort">JXL 编码努力 (1-9, 默认 7)</param>
    /// <param name="jxlDecodeSpeed">JXL 解码速度提示 (DNG 规范 1-4, 默认 4)</param>
    /// <param name="bitDepth">输出位深 (8 或 16)</param>
    /// <param name="highlightMode">高光模式 (LibRaw -H: 0=裁剪, 1=恢复, 2=blend)</param>
    /// <param name="threads">多线程数 (0=自动用硬件并发, 1=单线程)</param>
    /// <param name="lensCorrection">true = 让 dngtool 读源 RAW 的镜头三表并换算成规范 OpcodeList3</param>
    /// <param name="suppressGps">true = 让 dngtool **不写**源的 GPS 标准位。
    /// ⚠ 工具侧**默认搬**（GPS 有 EXIF 标准位，静默丢掉就是本轮修的缺陷），所以"不搬"只能由
    /// 调用方显式请求。产品口径 = **默认不勾选、全保留**（<c>StripExifGps</c> 默认 false，2026-10-20 四处统一），
    /// 只有界面勾了删除 GPS 或 CLI 给了 --strip-gps 才传 true。也不走"事后删"：2026-10-05 实测过排除式在部分容器上
    /// 报 updated 而 GPS 仍在 ⇒ 必须由写侧一开始就不写。</param>
    /// <returns>dngtool 实参串（不含可执行名）</returns>
    public static string BuildEncodeToDngArguments(
        string rawPath, string outputDngPath,
        int compression = 0, int jxlQuality = 0,
        bool linear = false, int jxlEffort = 7, int jxlDecodeSpeed = 4,
        int bitDepth = 16, int highlightMode = 1, int threads = 0,
        bool carryRawMetadata = true, bool lensCorrection = false, bool suppressGps = false)
    {
        var args = $"-e -i \"{rawPath}\" -O \"{outputDngPath}\"";
        if (compression == 1)
        {
            args += " -jxl";
            // ⚠⚠ 2026-09-21：改用**独立的 JXL 质量参数 `-jxlq`**。
            //   原先用 `-q`（= demosaic 质量）且**仅当 jxlQuality > 0 才传** ⇒
            //   默认（jxlQuality == 0 = 无损）时**不传 `-q`**，而 dngtool 侧 `quality` 默认是 **3**
            //   （demosaic AHD）⇒ 被当成 JXL 质量 3
            //   ⇒ `SetDistance((100-3)*15/100 = 14.55f)` ⇒ **严重有损**！
            //   实测：JXL 产物 raw 校验和 `sum=725637202` ≠ 源 `711851004`
            //   （无损 JPEG 产物则与源**完全一致**）。
            //   现在**总是显式传 `-jxlq`**，dngtool 侧 `jxlQuality` 只由该参数设置 ✓
            args += $" -jxlq {Math.Max(0, jxlQuality)}";
        }
        else
        {
            args += " -lossless";
        }
        if (linear)
            args += " -linear";
        // 位深: dngtool 的 -4/-6 对应 8/16-bit（-6 是默认，仅 8-bit 时显式传 -4）
        if (bitDepth <= 8)
            args += " -4";
        // 高光模式 (LibRaw -H, 仅解码阶段有效)
        if (highlightMode != 1)
            args += $" -H {highlightMode}";
        // JXL 编码参数（dngtool ≥ 2026-08-13 支持 -effort/-decode_speed）
        if (compression == 1)
        {
            args += $" -effort {jxlEffort} -decode_speed {jxlDecodeSpeed}";
        }
        // 多线程 (2026-08-15): 0=自动(硬件并发), 1=单线程, N=指定
        if (threads > 0)
            args += $" -threads {threads}";
        // 隐私剥除请求 ⇒ 让 dngtool 连嵌入原件/预览/拍摄字段一起不写（这三样 exiftool 事后删不干净：
        // 嵌入原件是规范 50828 里的完整源文件字节，预览 JPEG 内部还各自带 EXIF）。
        if (!carryRawMetadata)
            args += " -no-raw-meta";
        // 镜头校正: 源有没有表由 dngtool 自己判并点名（lens-tables=absent），这里只表达"请求过"
        if (lensCorrection)
            args += " -lens-corr";
        // 用户要求剥 GPS（默认 false=不剥）⇒ 显式请求 dngtool 不写 GPS 标准位（写侧就不写，
        // 不靠事后删）；工具会点名 gps-suppressed=1，不会静默。
        if (suppressGps)
            args += " -no-gps";
        return args;
    }

    /// <summary>
    /// 将 RAW/DNG 文件编码为 DNG 输出 (dngtool -e)。
    /// </summary>
    /// <param name="rawPath">输入 RAW/DNG 文件</param>
    /// <param name="outputDngPath">输出 DNG 路径</param>
    /// <param name="compression">0=无损 JPEG, 1=JXL</param>
    /// <param name="jxlQuality">JXL 质量 (0=无损, 1-100=有损)</param>
    /// <param name="log">日志回调</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="linear">true=输出线性 DNG（无 CFA，体积更小），false=保留 CFA (Bayer)</param>
    /// <param name="jxlEffort">JXL 编码努力 (1-9, 默认 7)</param>
    /// <param name="jxlDecodeSpeed">JXL 解码速度提示 (DNG 规范 1-4, 默认 4)</param>
    /// <param name="bitDepth">输出位深 (8 或 16)</param>
    /// <param name="highlightMode">高光模式 (LibRaw -H: 0=裁剪, 1=恢复, 2=blend)</param>
    /// <param name="threads">多线程数 (0=自动用硬件并发, 1=单线程)。2026-08-15 新增。</param>
    /// <param name="lensCorrection">true = 源 RAW 镜头三表 → 规范 opcode（默认关，它改渲染像素）</param>
    /// <returns>成功返回 true</returns>
    public static async Task<bool> EncodeToDngAsync(
        string rawPath, string outputDngPath,
        int compression = 0, int jxlQuality = 0,
        Action<string>? log = null, CancellationToken ct = default,
        bool linear = false, int jxlEffort = 7, int jxlDecodeSpeed = 4,
        int bitDepth = 16, int highlightMode = 1, int threads = 0,
        bool carryRawMetadata = true, bool lensCorrection = false, bool suppressGps = false)
    {
        if (!IsDngTool)
        {
            log?.Invoke("[RAW] DNG 编码需要 dngtool 引擎\n");
            return false;
        }

        log?.Invoke($"[RAW] dngtool 编码 DNG: {Path.GetFileName(rawPath)}\n");
        var args = BuildEncodeToDngArguments(
            rawPath, outputDngPath, compression, jxlQuality,
            linear, jxlEffort, jxlDecodeSpeed, bitDepth, highlightMode, threads,
            carryRawMetadata, lensCorrection, suppressGps);
        log?.Invoke($"[RAW] dngtool {args}\n");

        var ok = await RunProcessAsync(_detectedPath!, args, outputDngPath, "dngtool-e", log, ct);
        if (ok)
            log?.Invoke($"[RAW] ✅ DNG 编码完成 ({(compression == 1 ? "JXL" : "无损 JPEG")}{(linear ? ", 线性" : ", CFA")}{(lensCorrection ? ", 已请求镜头校正" : "")})\n");
        return ok;
    }

    /// <summary>`dngtool -idc` 的参数（只读探测，不产出文件）。</summary>
    public static string BuildIdcArguments(string inputPath) => $"-idc \"{inputPath}\"";

    /// <summary>
    /// 读源文件索尼 IDC 生效版本的两条版本时间戳（tag 0xD100/0xD101）。
    /// <para>非索尼、无 IDC 链、dngtool 缺席或读不到 ⇒ <c>(null, null)</c>，这**不是失败**。</para>
    /// <para>⚠ 文本是**源文件里的内容**，而它接下来要拼进 exiftool 的命令行 ⇒ 只接受
    /// XMP 日期形状 <c>YYYY:MM:DD HH:MM:SS</c> 的值，其余一律当读不到（不校验就等于把
    /// 未验证的字节交进另一条命令行）。</para>
    /// </summary>
    public static async Task<(string? create, string? modify)> ReadIdcVersionDatesAsync(
        string inputPath, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!IsDngTool) return (null, null);
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath!,
                Arguments = BuildIdcArguments(inputPath),
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                RedirectStandardError = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            p = Process.Start(psi);
            if (p is null) return (null, null);
            PlatformServices.SetSafePriority(p, AppSettingsService.Current.FfmpegPriority);
            // ⚠ 两条管道都要排空：只读 stdout 时 dngtool 把 stderr 缓冲写满就阻塞在写上，
            //   我们永远等不到 stdout ⇒ 队列槽位被永久占住（P2-3 治过的同一形状）。
            var stdout = await PlatformServices.ReadStdoutDrainingStderrAsync(p);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return (null, null);
            return ParseIdcVersionDates(stdout);
        }
        catch (OperationCanceledException)
        {
            // 取消 ≠ 读不到：先杀进程（不留孤儿继续跑），再原样抛给队列
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[RAW] dngtool -idc 探测异常：{ex.GetType().Name}\n");
            return (null, null);
        }
        finally
        {
            p?.Dispose();
        }
    }

    /// <summary>从 `-idc` 的 JSON 里取两条版本时间戳（与进程解耦，便于直接喂样本对账）。</summary>
    public static (string? create, string? modify) ParseIdcVersionDates(string json)
    {
        string? create = null, modify = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
                return (null, null);
            foreach (var e in entries.EnumerateArray())
            {
                var tag = e.TryGetProperty("tag", out var t) ? t.GetString() : null;
                var txt = e.TryGetProperty("text", out var x) ? x.GetString() : null;
                if (tag is null || txt is null || !IsXmpDateForm(txt)) continue;
                if (string.Equals(tag, "0xD100", StringComparison.OrdinalIgnoreCase)) create = txt;
                else if (string.Equals(tag, "0xD101", StringComparison.OrdinalIgnoreCase)) modify = txt;
            }
        }
        catch (JsonException)
        {
            return (null, null);
        }
        return (create, modify);
    }

    /// <summary>定长手检 <c>YYYY:MM:DD HH:MM:SS</c>（不引正则：这条跑在每次 DNG 编码的尾巴上）。</summary>
    public static bool IsXmpDateForm(string s)
    {
        if (s.Length != 19 || s[4] != ':' || s[7] != ':' || s[10] != ' '
            || s[13] != ':' || s[16] != ':')
            return false;
        foreach (var i in new[] { 0, 1, 2, 3, 5, 6, 8, 9, 11, 12, 14, 15, 17, 18 })
            if (!char.IsAsciiDigit(s[i])) return false;
        return true;
    }

    /// <summary>运行 RAW 解码进程 (dngtool 通用执行器)</summary>
    private static async Task<bool> RunProcessAsync(
        string exePath, string args, string outputTiffPath,
        string tag, Action<string>? log, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                RedirectStandardError = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                log?.Invoke($"[RAW] 无法启动 {tag} 进程。\n");
                return false;
            }
            PlatformServices.SetSafePriority(p, AppSettingsService.Current.FfmpegPriority);

            p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // 取消必须杀进程：否则 dngtool 在后台继续跑到自然结束（孤儿进程，
                // 占 CPU 且可能写完一个无人消费的大 TIFF）（P2）
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            catch (Exception)
            {
                // 其他等待异常同样先杀进程再走外层统一日志
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            if (p.ExitCode != 0)
            {
                log?.Invoke($"[RAW] {tag} 退出码 {p.ExitCode}\n");
                return false;
            }

            if (File.Exists(outputTiffPath))
                PlatformServices.MarkAsTemporaryFile(outputTiffPath);

            return File.Exists(outputTiffPath);
        }
        catch (OperationCanceledException)
        {
            // 维持取消语义：向上传播，调用方按"已停止"处理
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[RAW] {tag} 异常: {ex.Message}\n");
            return false;
        }
    }
}
