using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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
    public static string JxrEnc    => ToolName("JxrEncApp");
    public static string JxrDec    => ToolName("JxrDecApp");
    public static string Exiftool  => OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";
    public static string Avifenc   => ToolName("avifenc");
    public static string DngToolName => OperatingSystem.IsWindows() ? "dngtool.exe" : "dngtool";

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
    /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
    /// <param name="ct">取消令牌（`#26`：调用方 `QueueProcessor.ProcessJxrInputAsync` 持有它却没传下来，本次接通）</param>
    public static bool TryFindInPath(string toolName, out string? fullPath,
        Action<string>? log = null, CancellationToken ct = default)
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
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                RedirectStandardError = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            // ⚠ 先挂读取任务、再等退出（本仓范式，见 `FfmpegCommandBuilder.ProbeWithFfprobe`）：
            //   旧的 `StandardOutput.ReadToEnd()`（同步阻塞）写在 `WaitForExit(5000)` **之前**
            //   ⇒ 子进程不关管道就永不返回，**超时是死代码、且超时后没有 Kill**。
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            // `#26`：同步 `WaitForExit(5000)` 不接受 ct ⇒ 用 `ct.Register` 杀树接线（不改调用方）；原有 5s 超时**不变**。
            using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                var tmsg = $"[path] {finder} 查找超时（5s）⇒ 已终止：{toolName}（未解析到路径）";
                if (log != null) log(tmsg + "\n"); else System.Diagnostics.Trace.WriteLine(tmsg);
                return false;
            }
            if (ct.IsCancellationRequested)
            {
                var cmsg = $"[path] {finder} 查找被取消 ⇒ 已终止：{toolName}（未解析到路径）";
                if (log != null) log(cmsg + "\n"); else System.Diagnostics.Trace.WriteLine(cmsg);
                return false;
            }
            var output = outTask.GetAwaiter().GetResult().Trim();
            errTask.GetAwaiter().GetResult();
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
    // PLAN 文件夹检测（便携包自动识别 + 开发模式回退）
    // ═══════════════════════════════════════════════

    private static string? _planPath;
    private static bool _planScanned;

    /// <summary>
    /// PLAN 文件夹路径。搜索优先级：
    /// 1. 程序同目录下的 PLAN/（发布包）
    /// 2. 当前工作目录下的 PLAN/（dotnet run 开发模式）
    /// 3. 进程可执行文件所在目录向上查找（临时解决方案）
    /// 4. 环境变量 FFMPEGGUI_PLAN_DIR（显式指定）
    /// </summary>
    public static string? PlanFolderPath
    {
        get
        {
            if (!_planScanned)
            {
                _planScanned = true;
                
                // 优先使用环境变量显式指定（CI/部署场景）
                var envPlan = Environment.GetEnvironmentVariable("FFMPEGGUI_PLAN_DIR");
                if (!string.IsNullOrWhiteSpace(envPlan) && Directory.Exists(envPlan))
                {
                    _planPath = envPlan;
                    return _planPath;
                }

                // ① 程序同目录（发布包 / 单文件发布）
                var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PLAN");
                if (Directory.Exists(baseDir))
                {
                    _planPath = baseDir;
                    return _planPath;
                }

                // ② 当前工作目录（dotnet run 开发模式，项目根目录）
                var cwd = Directory.GetCurrentDirectory();
                var cwdPlan = Path.Combine(cwd, "PLAN");
                if (Directory.Exists(cwdPlan))
                {
                    _planPath = cwdPlan;
                    return _planPath;
                }

                // ③ 向上查找目录树（处理 bin/Release 等嵌套情况）
                var dir = new DirectoryInfo(cwd);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "PLAN");
                    if (Directory.Exists(candidate))
                    {
                        _planPath = candidate;
                        return _planPath;
                    }
                    dir = dir.Parent;
                }

                // ④ 进程可执行文件目录向上查找（单文件发布等特殊场景）
                try
                {
                    var procPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(procPath))
                    {
                        var procDir = Path.GetDirectoryName(procPath);
                        if (!string.IsNullOrEmpty(procDir))
                        {
                            var pDir = new DirectoryInfo(procDir);
                            while (pDir != null)
                            {
                                var cand = Path.Combine(pDir.FullName, "PLAN");
                                if (Directory.Exists(cand))
                                {
                                    _planPath = cand;
                                    return _planPath;
                                }
                                pDir = pDir.Parent;
                            }
                        }
                    }
                }
                catch { }
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
        [JxrEnc]   = new[] { "artifacts" },
        [JxrDec]   = new[] { "artifacts" },
        [Avifenc]  = new[] { "artifacts" },
        [DngToolName] = new[] { "artifacts" },
    };

    /// <summary>
    /// 在 PLAN 文件夹中查找名称包含指定关键字的子目录（如 "ffmpeg-full"）。
    /// 用于支持目录名带日期/后缀变体的场景（如 ffmpeg-full-2026.7.24）。
    /// 返回第一个匹配的子目录绝对路径；未找到返回 null。
    /// </summary>
    public static string? FindPlanSubDir(string planPath, string nameContains)
    {
        if (!Directory.Exists(planPath)) return null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(planPath))
            {
                if (Path.GetFileName(dir).Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// avifenc 定位的**全局唯一入口**（P7f）。顺序：手动路径 → artifacts 目录 → PLAN 便携文件夹 → ffmpeg 同目录。
    /// ⚠ 曾经同一件事有三份拷贝且口径不一：队列分派判“可用”、执行方法自己重找时漏了 PLAN 分支
    ///   ⇒ 便携发布包布局下 GIF→AVIF 两步法永远跑不到并**静默回退**；UI 又只查其中三项（漏手动路径）。
    ///   所有“avifenc 在哪 / 是否可用”的提问都必须走本函数，不得在调用方重新拼路径。
    /// </summary>
    public static string? ResolveAvifencPath()
    {
        var manual = AppSettingsService.Current.AvifencPath;
        if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual)) return manual;

        var artifactsDir = AppSettingsService.Current.WindowsArtifactsDir;
        if (!string.IsNullOrWhiteSpace(artifactsDir))
        {
            var p = Path.Combine(artifactsDir, Avifenc);
            if (File.Exists(p)) return p;
        }

        var inPlan = TryFindInPlanFolder(Avifenc);
        if (inPlan != null && File.Exists(inPlan)) return inPlan;

        var byFfmpeg = Path.Combine(AppSettingsService.Current.FfmpegDir ?? "", Avifenc);
        return File.Exists(byFfmpeg) ? byFfmpeg : null;
    }

    /// <summary>在 PLAN 文件夹的对应子目录中查找指定工具。未找到返回 null。</summary>
    public static string? TryFindInPlanFolder(string toolName)
    {
        var plan = PlanFolderPath;
        if (plan == null) return null;
        if (!PlanSubDirs.TryGetValue(toolName, out var subDirs)) return null;
        foreach (var sub in subDirs)
        {
            string? dir;
            if (toolName == Ffmpeg || toolName == Ffprobe)
            {
                // ffmpeg/ffprobe: 目录名只需包含 "ffmpeg-full"（如 ffmpeg-full-2026.7.24）
                dir = FindPlanSubDir(plan, "ffmpeg-full");
                if (dir == null) continue;
            }
            else
            {
                dir = Path.Combine(plan, sub);
                if (!Directory.Exists(dir)) continue;
            }
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

    /// <summary>缓存子目录前缀（用于识别和清理僵尸目录，2026-08-16 与实际创建点对齐）</summary>
    private static readonly string[] TempDirPrefixes =
    {
        "raw_", "gainmap_", "gmdecode_", "uhdr_linear_", "jxr_", "jxr_input_",
        "avif2gifwebp_", "avifenc_frames_", "ffmpeg_ps_check"
    };

    /// <summary>缓存文件前缀（用于识别和清理僵尸临时文件，2026-08-16 补充质量分析/预转换）</summary>
    private static readonly string[] TempFilePrefixes =
    {
        "icc_extract_", "qa_src_", "qa_enc_", "qa_psnr_src_", "qa_psnr_enc_",
        "ffmpeg_preconv_"
    };

    /// <summary>
    /// 清理崩溃/异常退出遗留的僵尸临时目录/文件（超过 24 小时的旧项）。
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
            // 僵尸临时文件（ICC 提取、质量分析、预转换等，非目录）
            foreach (var prefix in TempFilePrefixes)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(cacheDir, $"{prefix}*"))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if (fi.LastWriteTimeUtc < cutoff)
                            {
                                File.Delete(file);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { /* 清理失败不影响主流程 */ }
    }
}
