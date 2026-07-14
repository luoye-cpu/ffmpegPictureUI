using System.Diagnostics;
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
                4 => ProcessPriorityClass.BelowNormal,
                5 => ProcessPriorityClass.Idle,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch { /* 降级：优先级设置不影响核心功能 */ }
    }

    /// <summary>返回适合当前平台的大文件临时目录</summary>
    public static string GetTempDir() =>
        IsLinux && Directory.Exists("/var/tmp") ? "/var/tmp" : Path.GetTempPath();
}
