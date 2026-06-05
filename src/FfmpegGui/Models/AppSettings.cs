using System.IO;
using System.Text.Json.Serialization;

namespace FfmpegGui.Models
{
    public class AppSettings
    {
        public string? FfmpegDirectory { get; set; }
        public string? OutputDirectory { get; set; }

        /// <summary>手动指定的 cjxl.exe 路径或包含外部工具的目录（留空则自动检测）</summary>
        public string? CjxlPath { get; set; }

        /// <summary>手动指定的 exiftool 路径（留空则自动检测）</summary>
        public string? ExifToolPath { get; set; }

        /// <summary>手动指定的 cjpegli.exe 路径或包含 JPEG 库的目录（留空则自动检测）</summary>
        public string? CjpegliPath { get; set; }

        /// <summary>手动指定的 avifenc.exe 路径（留空则自动检测 ffmpeg 同目录）</summary>
        public string? AvifencPath { get; set; }

        public bool PreserveInputFolderStructure { get; set; } = false;

        public int MaxQueueSize { get; set; } = 16;

        public int ThemeMode { get; set; } = 2;

        /// <summary>
        /// 启用后：在检测到与 CPU 指令集匹配的优化二进制时，自动优先使用并保存工具路径（仅在用户未手动指定时生效）。
        /// </summary>
        public bool AutoUseSimdBinaries { get; set; } = true;

        /// <summary>
        /// 用户手动忽略的外部工具路径（持久化）。检测/选择时将跳过这些路径。
        /// </summary>
        public System.Collections.Generic.List<string> IgnoredToolPaths { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>ffmpeg.exe 完整路径（计算属性，不持久化）</summary>
        [JsonIgnore]
        public string FfmpegPath =>
            string.IsNullOrWhiteSpace(FfmpegDirectory)
                ? "ffmpeg"
                : Path.Combine(FfmpegDirectory, "ffmpeg.exe");

        /// <summary>ffprobe.exe 完整路径（计算属性，不持久化）</summary>
        [JsonIgnore]
        public string FfprobePath =>
            string.IsNullOrWhiteSpace(FfmpegDirectory)
                ? "ffprobe"
                : Path.Combine(FfmpegDirectory, "ffprobe.exe");

        /// <summary>ffmpeg 所在目录（计算属性，不持久化）</summary>
        [JsonIgnore]
        public string? FfmpegDir =>
            string.IsNullOrWhiteSpace(FfmpegDirectory) ? null : FfmpegDirectory;

        // ── 图片文件格式筛选 ──

        /// <summary>
        /// 所有可选的图片格式定义（名称 → 扩展名列表）
        /// </summary>
        [JsonIgnore]
        public static readonly Dictionary<string, string[]> AllImageFormats = new()
        {
            ["PNG"]  = new[] { ".png" },
            ["JPEG"] = new[] { ".jpg", ".jpeg", ".jpe", ".jfif" },
            ["JPEG XL"] = new[] { ".jxl" },
            ["WebP"] = new[] { ".webp" },
            ["AVIF"] = new[] { ".avif" },
            ["TIFF"] = new[] { ".tiff", ".tif" },
            ["BMP"]  = new[] { ".bmp" },
            ["GIF"]  = new[] { ".gif" },
        };

        /// <summary>用户启用的图片格式名称列表（持久化到 settings.json）</summary>
        public List<string> EnabledImageFormats { get; set; } = new() { "PNG", "JPEG", "JPEG XL", "WebP", "AVIF", "TIFF", "BMP", "GIF" };

        /// <summary>根据 EnabledImageFormats 获取所有启用的扩展名（小写）</summary>
        public string[] GetEnabledExtensions()
        {
            var exts = new List<string>();
            foreach (var name in EnabledImageFormats)
            {
                if (AllImageFormats.TryGetValue(name, out var arr))
                    exts.AddRange(arr);
            }
            return exts.Select(e => e.ToLowerInvariant()).ToArray();
        }

        /// <summary>根据 EnabledImageFormats 生成 FilePicker 的 FileTypeFilter</summary>
        public Avalonia.Platform.Storage.FilePickerFileType[] GetImageFilePickerFilter()
        {
            var enabledExts = GetEnabledExtensions();
            var patterns = enabledExts.Select(e => "*" + e).ToArray();
            return new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("图片文件") { Patterns = patterns },
                new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            };
        }
    }
}
