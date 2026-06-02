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
    }
}
