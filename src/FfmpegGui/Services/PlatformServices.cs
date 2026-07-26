using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace FfmpegGui.Services;

/// <summary>
/// 统一平台抽象层——所有平台相关常量、工具名、搜索模式的集中管理。
/// 未来迁移 Linux/macOS 时，只需确保此类中的常量正确，其余代码无需改动。
/// </summary>
public static class PlatformServices
{
    // ═══════════════════════════════════════════════
    // 文件名与搜索模式
    // ═══════════════════════════════════════════════

    /// <summary>可执行文件扩展名（含点，如 ".exe"）</summary>
    public static string ExeExtension => OperatingSystem.IsWindows() ? ".exe" : "";

    /// <summary>给基础工具名附加平台可执行文件后缀</summary>
    public static string ToolName(string baseName) => baseName + ExeExtension;

    /// <summary>目录扫描时搜索可执行文件的通配符</summary>
    public static string ExeSearchWildcard => OperatingSystem.IsWindows() ? "*.exe" : "*";

    // ── 预定义工具名（全局唯一引用点）──
    public static string Ffmpeg    => ToolName("ffmpeg");
    public static string Ffprobe   => ToolName("ffprobe");
    public static string Cjxl      => ToolName("cjxl");
    public static string Djxl      => ToolName("djxl");
    public static string Cjpegli   => ToolName("cjpegli");
    public static string Ultrahdr  => ToolName("ultrahdr_app");
    public static string JxrEnc    => ToolName("JxrEncApp");
    public static string JxrDec    => ToolName("JxrDecApp");
    public static string Exiftool  => OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";
    public static string Avifenc   => ToolName("avifenc");
    public static string DcrawName => OperatingSystem.IsWindows() ? "dcraw.exe" : "dcraw";

    // ── 目录搜索模式（用于 Directory.EnumerateFiles）──
    public static string CjxlSearchWildcard    => OperatingSystem.IsWindows() ? "*cjxl*.exe"   : "*cjxl*";
    public static string DjxlSearchWildcard    => OperatingSystem.IsWindows() ? "*djxl*.exe"   : "*djxl*";
    public static string CjpegliSearchWildcard => OperatingSystem.IsWindows() ? "*cjpegli*.exe" : "*cjpegli*";

    // ── 共享库搜索模式 ──
    public static string[] SharedLibSearchPatterns => OperatingSystem.IsWindows()
        ? new[] { "*jpegli*.dll", "*libjxl*.dll", "*skcms*.dll", "*lcms2*.dll", "*jxl*.dll" }
        : new[] { "libjpegli*.so*", "libjxl*.so*", "libskcms*.so*", "liblcms2*.so*" };

    // ── FilePicker 过滤器 ──
    public static string[] ExeFilePickerPatterns =>
        OperatingSystem.IsWindows() ? new[] { "*.exe" } : new[] { "*" };

    // ═══════════════════════════════════════════════
    // 平台感知属性
    // ═══════════════════════════════════════════════

    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux   => OperatingSystem.IsLinux();
    public static bool IsMacOs   => OperatingSystem.IsMacOS();
    public static bool IsArm64   => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    public static bool IsX64     => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    // ═══════════════════════════════════════════════
    // 工具路径解析
    // ═══════════════════════════════════════════════

    /// <summary>在系统 PATH 中查找可执行文件（Windows=where, Unix=which）</summary>
    public static bool TryFindInPath(string toolName, out string? fullPath)
    {
        fullPath = null;
        try
        {
            var finder = OperatingSystem.IsWindows() ? "where" : "which";
            var psi = new ProcessStartInfo
            {
                FileName = finder,
                Arguments = toolName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            if (string.IsNullOrWhiteSpace(output)) return false;
            var firstLine = output.Split(new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries)[0];
            if (File.Exists(firstLine)) { fullPath = firstLine; return true; }
        }
        catch { }
        return false;
    }

    /// <summary>从 ffmpeg 路径推断 ffprobe 路径</summary>
    public static string? ResolveFfprobePath(string ffmpegPath)
    {
        var dir = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var probe = Path.Combine(dir, Ffprobe);
        if (File.Exists(probe)) return probe;
        // 回退：替换文件名中的 ffmpeg → ffprobe
        var ffmpegName = Path.GetFileNameWithoutExtension(ffmpegPath);
        if (ffmpegName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(dir, Ffprobe);
        return null;
    }

    /// <summary>在目录及子目录中查找特定工具（优先直接匹配，其次递归）</summary>
    public static string? FindToolInDirectory(
        string directory, string toolName, string searchWildcard)
    {
        if (!Directory.Exists(directory)) return null;
        var candidate = Path.Combine(directory, toolName);
        if (File.Exists(candidate)) return candidate;
        try
        {
            var list = new List<string>();
            foreach (var f in Directory.EnumerateFiles(
                directory, searchWildcard, SearchOption.AllDirectories))
            {
                if (File.Exists(f)) list.Add(f);
            }
            if (list.Count > 0)
                return ExternalToolsDetector.ChooseBestExecutable(list);
        }
        catch { }
        return null;
    }

    // ═══════════════════════════════════════════════
    // PLAN 文件夹检测（便携包自动识别）
    // ═══════════════════════════════════════════════

    private static string? _planPath;
    private static bool _planScanned;

    /// <summary>PLAN 文件夹路径（程序同目录下的 PLAN/），null=不存在</summary>
    public static string? PlanFolderPath
    {
        get
        {
            if (!_planScanned)
            {
                _planScanned = true;
                var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PLAN");
                if (Directory.Exists(p)) _planPath = p;
            }
            return _planPath;
        }
    }

    /// <summary>PLAN 文件夹中各工具 → 子目录映射 (v2.0 简化版)</summary>
    private static readonly Dictionary<string, string[]> PlanSubDirs = new()
    {
        [Ffmpeg]  = new[] { "ffmpeg-full" },
        [Ffprobe] = new[] { "ffmpeg-full" },
        [Cjxl]    = new[] { Path.Combine("jxl", "bin"), "jxl" },
        [Djxl]    = new[] { Path.Combine("jxl", "bin"), "jxl" },
        [Cjpegli] = new[] { Path.Combine("jxl", "bin"), "jxl" },
        [Exiftool] = new[] { "exiftool" },
        ["exiftool(-k).exe"] = new[] { "exiftool" },
        [Ultrahdr] = new[] { "artifacts" },
        [JxrEnc]   = new[] { "artifacts" },
        [JxrDec]   = new[] { "artifacts" },
        [Avifenc]  = new[] { "artifacts" },
        [DcrawName] = new[] { "artifacts" },
    };

    /// <summary>在 PLAN 文件夹的对应子目录中查找指定工具。未找到返回 null。</summary>
    public static string? TryFindInPlanFolder(string toolName)
    {
        var plan = PlanFolderPath;
        if (plan == null) return null;
        if (!PlanSubDirs.TryGetValue(toolName, out var subDirs)) return null;
        foreach (var sub in subDirs)
        {
            var dir = Path.Combine(plan, sub);
            if (!Directory.Exists(dir)) continue;
            var candidate = Path.Combine(dir, toolName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ═══════════════════════════════════════════════
    // 进程优先级（跨平台安全）
    // ═══════════════════════════════════════════════

    /// <summary>设置进程优先级。当前在 Windows 上使用 ProcessPriorityClass</summary>
    public static void SetSafePriority(Process process, int priorityLevel)
    {
        try
        {
            process.PriorityClass = priorityLevel switch
            {
                0 => ProcessPriorityClass.RealTime,
                1 => ProcessPriorityClass.High,
                2 => ProcessPriorityClass.AboveNormal,
                3 => ProcessPriorityClass.Normal,
                4 => ProcessPriorityClass.BelowNormal,
                5 => ProcessPriorityClass.Idle,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[priority] 设置失败 (level={priorityLevel}): {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════
    // RAM 优化临时文件
    // ═══════════════════════════════════════════════

    /// <summary>
    /// 标记文件为临时文件，提示 OS 优先使用内存缓存、延迟写入磁盘。
    /// Windows: FILE_ATTRIBUTE_TEMPORARY → OS 尽量缓存在内存，减少磁盘 I/O
    /// Linux:   在 /dev/shm 上的文件天然 RAM 驻留，无需额外标记
    /// </summary>
    public static void MarkAsTemporaryFile(string filePath)
    {
        if (IsLinux) return;
        try
        {
            File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.Temporary);
        }
        catch { /* 非致命：属性设置失败不影响功能 */ }
    }

    /// <summary>在 RAM 优化临时目录中创建子目录</summary>
    public static string CreateTempSubDir(string prefix)
    {
        var dir = Path.Combine(GetTempDir(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 返回适合当前平台的临时目录，优先级：
    /// ① 用户设置 CacheDirectory（GUI 中手动配置）
    /// ② 环境变量 FFMPEGGUI_RAMDISK（用户挂载的 RAM 盘）
    /// ③ Linux /dev/shm（tmpfs 内存文件系统，零磁盘磨损）
    /// ④ 系统默认临时目录（Windows 通常已是 SSD）
    /// </summary>
    public static string GetTempDir()
    {
        // ① 用户在设置中指定的缓存目录（最高优先级）
        try
        {
            var cacheDir = AppSettingsService.Current.CacheDirectory;
            if (!string.IsNullOrWhiteSpace(cacheDir))
            {
                try { Directory.CreateDirectory(cacheDir); } catch { }
                if (Directory.Exists(cacheDir))
                    return cacheDir;
            }
        }
        catch { }

        // ② 用户通过环境变量指定的 RAM 盘路径（跨平台通用方案）
        try
        {
            var envPath = Environment.GetEnvironmentVariable("FFMPEGGUI_RAMDISK");
            if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
            {
                // 确保 RAM 盘有足够空间（至少 500MB），不够则回退
                try
                {
                    var driveInfo = new DriveInfo(Path.GetPathRoot(envPath)!);
                    if (driveInfo.AvailableFreeSpace > 500_000_000)
                        return envPath;
                }
                catch { /* 无法检测空间，信任用户配置 */ return envPath; }
            }
        }
        catch { }

        // ② Linux: /dev/shm 是内核保证的 tmpfs，大小默认为 50% RAM
        if (IsLinux && Directory.Exists("/dev/shm"))
        {
            try
            {
                // 安全检测：至少需要 500MB 可用空间
                var shmInfo = new DriveInfo("/dev/shm");
                if (shmInfo.AvailableFreeSpace > 500_000_000)
                    return "/dev/shm";
            }
            catch { /* 无法检测，保守使用 /var/tmp */ }
            // /dev/shm 空间不足，回退 /var/tmp（持久临时，避免 /tmp 被 systemd 清理）
            if (Directory.Exists("/var/tmp"))
                return "/var/tmp";
        }

        // ③ 标准临时目录（Windows: %TEMP% 通常已在 SSD 上）
        return Path.GetTempPath();
    }

    // ═══════════════════════════════════════════════
    // 缓存文件清理
    // ═══════════════════════════════════════════════

    /// <summary>缓存子目录前缀（用于识别和清理僵尸目录）</summary>
    private static readonly string[] TempDirPrefixes =
    {
        "raw_", "gainmap_", "ultrahdr_", "jxr_", "jxr_input_",
        "uhdr_decode_", "avif2gifwebp_", "avifenc_frames_"
    };

    /// <summary>
    /// 清理崩溃/异常退出遗留的僵尸临时目录（超过 24 小时的旧目录）。
    /// 应在应用启动时调用一次。
    /// </summary>
    public static void CleanupZombieTempDirs()
    {
        try
        {
            var cacheDir = GetTempDir();
            if (!Directory.Exists(cacheDir)) return;

            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var prefix in TempDirPrefixes)
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(cacheDir, $"{prefix}*"))
                    {
                        try
                        {
                            var dirInfo = new DirectoryInfo(dir);
                            if (dirInfo.LastWriteTimeUtc < cutoff)
                            {
                                Directory.Delete(dir, true);
                            }
                        }
                        catch { /* 单个目录清理失败不影响其他 */ }
                    }
                }
                catch { /* 枚举失败不影响其他前缀 */ }
            }
        }
        catch { /* 清理失败不影响主流程 */ }
    }
}
