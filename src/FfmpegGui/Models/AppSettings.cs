using System.IO;

namespace FfmpegGui.Models
{
    public class AppSettings
    {
        public string? FfmpegDirectory { get; set; }
        public string? OutputDirectory { get; set; }

        // 新增：是否在输出目录下保留与输入相同的子目录结构
        public bool PreserveInputFolderStructure { get; set; } = false;

        public string FfmpegPath =>
            string.IsNullOrWhiteSpace(FfmpegDirectory)
                ? "ffmpeg"
                : Path.Combine(FfmpegDirectory, "ffmpeg.exe");

        public string FfprobePath =>
            string.IsNullOrWhiteSpace(FfmpegDirectory)
                ? "ffprobe"
                : Path.Combine(FfmpegDirectory, "ffprobe.exe");
    }
}
