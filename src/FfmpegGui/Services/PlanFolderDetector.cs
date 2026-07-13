using System.IO;

namespace FfmpegGui.Services;

/// <summary>
/// PLAN 文件夹自动识别服务。
/// 程序启动时检测自身所在目录下是否存在 PLAN/ 文件夹，
/// 若存在则自动扫描并加载其中的外部工具组件。
/// 
/// PLAN 文件夹结构约定：
///   PLAN/
///   ├── ffmpeg-full/              ← ffmpeg.exe, ffprobe.exe
///   ├── jxl/                      ← bin/cjxl.exe, bin/djxl.exe, bin/cjpegli.exe
///   ├── exiftool/                 ← exiftool.exe
///   ├── artifacts/                ← ultrahdr_app.exe, JxrEncApp.exe, avifenc.exe
///   └── 使用说明.txt              ← 用户使用指南
/// </summary>
public static class PlanFolderDetector
{
    /// <summary>PLAN 文件夹查找结果</summary>
    public class PlanFolderResult
    {
        /// <summary>PLAN 文件夹绝对路径（null = 未找到）</summary>
        public string? PlanPath { get; set; }
        /// <summary>ffmpeg 所在目录</summary>
        public string? FfmpegDir { get; set; }
        /// <summary>JXL 工具所在目录（含 cjxl/djxl/cjpegli）</summary>
        public string? JxlBinDir { get; set; }
        /// <summary>exiftool 所在目录</summary>
        public string? ExifToolDir { get; set; }
        /// <summary>额外工具目录（ultrahdr_app, JxrEncApp 等）</summary>
        public string? ArtifactsDir { get; set; }

        public bool IsValid => PlanPath != null;
    }

    /// <summary>
    /// 在程序所在目录下查找 PLAN 文件夹，并自动识别内部工具组件。
    /// 仅在用户未手动配置对应路径时生效（不覆盖用户手动设置）。
    /// </summary>
    public static PlanFolderResult Detect()
    {
        var result = new PlanFolderResult();

        try
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var planPath = Path.Combine(exeDir, "PLAN");
            if (!Directory.Exists(planPath))
                return result;

            result.PlanPath = planPath;

            // ── 1) ffmpeg-full ──
            var ffmpegDir = Path.Combine(planPath, "ffmpeg-full");
            if (Directory.Exists(ffmpegDir))
            {
                // 验证目录中存在 ffmpeg
                var ffmpegExe = Path.Combine(ffmpegDir, PlatformServices.Ffmpeg);
                if (File.Exists(ffmpegExe))
                    result.FfmpegDir = ffmpegDir;
            }

            // ── 2) jxl ──
            var jxlDir = Path.Combine(planPath, "jxl");
            if (Directory.Exists(jxlDir))
            {
                // 优先 bin/ 子目录
                var binDir = Path.Combine(jxlDir, "bin");
                if (Directory.Exists(binDir))
                    result.JxlBinDir = binDir;
                else
                    result.JxlBinDir = jxlDir;
            }

            // ── 3) exiftool ──
            var etDir = Path.Combine(planPath, "exiftool");
            if (Directory.Exists(etDir))
            {
                result.ExifToolDir = etDir;
            }

            // ── 4) artifacts ──
            var artifactsDir = Path.Combine(planPath, "artifacts");
            if (Directory.Exists(artifactsDir))
                result.ArtifactsDir = artifactsDir;
        }
        catch { }

        return result;
    }

    /// <summary>
    /// 将检测到的 PLAN 文件夹路径应用到 AppSettings（仅在用户未手动设置时生效）。
    /// 同时触发各 Service 重新检测。
    /// </summary>
    public static void Apply(PlanFolderResult plan)
    {
        if (!plan.IsValid) return;

        var settings = AppSettingsService.Current;

        // ffmpeg 目录：仅在用户未手动设置时自动填充
        if (string.IsNullOrWhiteSpace(settings.FfmpegDirectory)
            && !string.IsNullOrWhiteSpace(plan.FfmpegDir))
        {
            settings.FfmpegDirectory = plan.FfmpegDir;
        }

        // JXL 工具目录
        if (string.IsNullOrWhiteSpace(settings.JxlLibDir)
            && !string.IsNullOrWhiteSpace(plan.JxlBinDir))
        {
            settings.JxlLibDir = plan.JxlBinDir;
        }

        // exiftool：查找 exiftool.exe 或 exiftool(-k).exe
        if (string.IsNullOrWhiteSpace(settings.ExifToolPath)
            && !string.IsNullOrWhiteSpace(plan.ExifToolDir))
        {
            var candidates = new[] { "exiftool.exe", "exiftool(-k).exe" };
            foreach (var c in candidates)
            {
                var p = Path.Combine(plan.ExifToolDir, c);
                if (File.Exists(p))
                {
                    settings.ExifToolPath = p;
                    break;
                }
            }
        }

        // 外部工具（ultrahdr_app, JxrEncApp, avifenc 等）
        if (!string.IsNullOrWhiteSpace(plan.ArtifactsDir))
        {
            if (string.IsNullOrWhiteSpace(settings.WindowsArtifactsDir))
            {
                settings.WindowsArtifactsDir = plan.ArtifactsDir;
            }
        }

        AppSettingsService.Save();

        // 刷新所有工具检测缓存
        CjxlService.ClearCache();
        DjxlService.ClearCache();
        CjpegliService.ClearCache();
        UltrahdrService.ClearCache();
        JxrService.ClearCache();
        // ExifTool: 直接重新检测
        ExifToolService.Detect();
    }
}
