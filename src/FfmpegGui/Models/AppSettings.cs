using System.IO;

namespace FfmpegGui.Models
{
    public class AppSettings
    {
        public string? FfmpegDirectory { get; set; }
        public string? OutputDirectory { get; set; }

        // 新增：是否在输出目录下保留与输入相同的子目录结构
        public bool PreserveInputFolderStructure { get; set; } = false;

        /// <summary>
        /// 最大队列容量（允许同时排队的任务数上限），范围 1-128，默认 16
        /// </summary>
        public int MaxQueueSize { get; set; } = 16;

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
