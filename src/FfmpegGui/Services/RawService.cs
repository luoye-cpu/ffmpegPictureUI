using System.Diagnostics;

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
        ".x3f",                           // Sigma
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

        // ① dngtool (新引擎): 手动路径 → PLAN → 同目录 → PATH
        var manual = AppSettingsService.Current.DngToolPath;
        if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual))
        {
            _detectedPath = manual;
            return true;
        }

        var planDng = PlatformServices.TryFindInPlanFolder(PlatformServices.DngToolName);
        if (planDng != null)
        {
            _detectedPath = planDng;
            return true;
        }

        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var localDng = Path.Combine(exeDir, PlatformServices.DngToolName);
        if (File.Exists(localDng))
        {
            _detectedPath = localDng;
            return true;
        }

        if (PlatformServices.TryFindInPath(PlatformServices.DngToolName, out var dngPath))
        {
            _detectedPath = dngPath;
            return true;
        }

        return false;
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
    /// <returns>成功返回 true</returns>
    public static async Task<bool> EncodeToDngAsync(
        string rawPath, string outputDngPath,
        int compression = 0, int jxlQuality = 0,
        Action<string>? log = null, CancellationToken ct = default,
        bool linear = false, int jxlEffort = 7, int jxlDecodeSpeed = 4,
        int bitDepth = 16, int highlightMode = 1, int threads = 0)
    {
        if (!IsDngTool)
        {
            log?.Invoke("[RAW] DNG 编码需要 dngtool 引擎\n");
            return false;
        }

        log?.Invoke($"[RAW] dngtool 编码 DNG: {Path.GetFileName(rawPath)}\n");
        var args = $"-e -i \"{rawPath}\" -O \"{outputDngPath}\"";
        if (compression == 1)
        {
            args += " -jxl";
            if (jxlQuality > 0) args += $" -q {jxlQuality}";
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
        log?.Invoke($"[RAW] dngtool {args}\n");

        var ok = await RunProcessAsync(_detectedPath!, args, outputDngPath, "dngtool-e", log, ct);
        if (ok)
            log?.Invoke($"[RAW] ✅ DNG 编码完成 ({(compression == 1 ? "JXL" : "无损 JPEG")}{(linear ? ", 线性" : ", CFA")})\n");
        return ok;
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
                RedirectStandardError = true,
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

            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
            {
                log?.Invoke($"[RAW] {tag} 退出码 {p.ExitCode}\n");
                return false;
            }

            if (File.Exists(outputTiffPath))
                PlatformServices.MarkAsTemporaryFile(outputTiffPath);

            return File.Exists(outputTiffPath);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[RAW] {tag} 异常: {ex.Message}\n");
            return false;
        }
    }
}
