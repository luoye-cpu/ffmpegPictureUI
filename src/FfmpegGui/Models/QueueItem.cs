using System;

namespace FfmpegGui.Models
{
    public class QueueItem
    {
        public string InputPath { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public FfmpegOptions Options { get; set; } = new FfmpegOptions();
        public string Status { get; set; } = "待处理";
        public int? ExitCode { get; set; }
        public string Log { get; set; } = string.Empty;
        public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
        public bool IsCancelled { get; set; } = false;
    }
}
