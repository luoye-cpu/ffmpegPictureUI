using System.IO;
using System.Text.Json.Serialization;

namespace FfmpegGui.Models
{
    public class AppSettings
    {
        public string? FfmpegDirectory { get; set; }
        public string? OutputDirectory { get; set; }

        /// <summary>手动指定的 cjxl.exe 路径（留空则自动检测）</summary>
        public string? CjxlPath { get; set; }

        /// <summary>手动指定的 exiftool 路径（留空则自动检测）</summary>
        public string? ExifToolPath { get; set; }

        public bool PreserveInputFolderStructure { get; set; } = false;

        public int MaxQueueSize { get; set; } = 16;

        public int ThemeMode { get; set; } = 2;

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
