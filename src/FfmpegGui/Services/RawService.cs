using System.Diagnostics;

namespace FfmpegGui.Services;

/// <summary>
/// RAW 图像预处理服务。
/// 使用 dcraw 将相机 RAW/Bayer 传感器数据去马赛克为线性 16-bit TIFF，
/// 解决 ffmpeg 无法直接处理 Bayer RAW 的问题以及色彩映射错误。
///
/// 支持格式: DNG, CR2, CR3, NEF, ARW, ORF, RAF, RW2, PEF, 3FR, SRW, ...
/// </summary>
public static class RawService
{
    private static string? _detectedPath;
    private static bool _detected;

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

        // 1) 手动路径
        var manual = AppSettingsService.Current.DcrawPath;
        if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual))
        {
            _detectedPath = manual;
            return true;
        }

        // 2) PLAN 文件夹
        var planPath = PlatformServices.TryFindInPlanFolder(PlatformServices.DcrawName)
                    ?? PlatformServices.TryFindInPlanFolder("dcraw");  // 兼容无扩展名
        if (planPath != null)
        {
            _detectedPath = planPath;
            return true;
        }

        // 3) 同目录
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var local = Path.Combine(exeDir, PlatformServices.DcrawName);
        if (File.Exists(local))
        {
            _detectedPath = local;
            return true;
        }

        // 4) 系统 PATH
        if (PlatformServices.TryFindInPath("dcraw", out var path))
        {
            _detectedPath = path;
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
    /// dcraw 输出相机白平衡校正后的线性 RGB 数据。
    /// </summary>
    /// <param name="rawPath">RAW 文件路径</param>
    /// <param name="outputTiffPath">输出 TIFF 路径</param>
    /// <param name="log">日志回调</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成功返回 true</returns>
    public static async Task<bool> PreProcessAsync(
        string rawPath, string outputTiffPath,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            log?.Invoke("[RAW] dcraw 未检测到，无法预处理 RAW 文件。\n");
            log?.Invoke("[RAW] 安装 dcraw: https://www.dechifro.org/dcraw/\n");
            return false;
        }

        // dcraw 参数说明:
        //   -4        : 输出 16-bit 线性 RGB (无 gamma 曲线)
        //   -T        : 输出 TIFF 格式
        //   -o 0      : 原始相机色彩空间 (不做色彩矩阵变换)
        //   -q 3      : 高质量 AHD 去马赛克插值
        //   -W        : 使用相机白平衡 (不自动调整)
        //   -H 1      : 高光裁剪模式 (保留高光细节)
        //   -6        : 16-bit 输出 (默认)
        var args = $"-4 -T -o 0 -q 3 -W -H 1 -6 \"{rawPath}\"";
        // 注意: dcraw 输出文件名由 -T 自动生成 (.tiff 扩展名)
        // 我们用 -O 指定输出，但 dcraw 不支持 -O，需要后处理重命名

        log?.Invoke($"[RAW] dcraw 去马赛克: {Path.GetFileName(rawPath)}\n");
        log?.Invoke($"[RAW] dcraw {args}\n");

        var tcs = new TaskCompletionSource<bool>();
        var psi = new ProcessStartInfo
        {
            FileName = _detectedPath!,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(outputTiffPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null)
            {
                log?.Invoke("[RAW] 无法启动 dcraw 进程。\n");
                return false;
            }

            var sb = new System.Text.StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); log?.Invoke(e.Data + "\n"); } };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); log?.Invoke(e.Data + "\n"); } };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
            {
                log?.Invoke($"[RAW] dcraw 退出码 {p.ExitCode}\n");
                return false;
            }

            // dcraw 输出文件名为: 输入文件名(去扩展名).tiff
            var rawName = Path.GetFileNameWithoutExtension(rawPath);
            var dcrawOutput = Path.Combine(Path.GetDirectoryName(outputTiffPath)!, rawName + ".tiff");
            if (File.Exists(dcrawOutput) && dcrawOutput != outputTiffPath)
            {
                File.Move(dcrawOutput, outputTiffPath, overwrite: true);
            }

            // 标记为临时文件，提示 OS 优先内存缓存（dcraw 产出的 TIFF 仅用于后续 ffmpeg 编码）
            if (File.Exists(outputTiffPath))
                PlatformServices.MarkAsTemporaryFile(outputTiffPath);

            log?.Invoke($"[RAW] 预处理完成: {Path.GetFileName(outputTiffPath)}\n");
            return File.Exists(outputTiffPath);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[RAW] dcraw 异常: {ex.Message}\n");
            return false;
        }
    }
}
