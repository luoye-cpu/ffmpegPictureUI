using System;
using System.ComponentModel;
using System.IO;

namespace FfmpegGui.Models
{
    public class QueueItem : INotifyPropertyChanged
    {
        public string InputPath { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public FfmpegOptions Options { get; set; } = new FfmpegOptions();
        // 当在批量添加时保留每个输入文件对应的输入基目录（用于保留目录结构）
        public string? InputBaseDir { get; set; }
        /// <summary>实际执行的命令行（用于详情窗口展示）</summary>
        public string Command { get; set; } = string.Empty;

        private string _status = "待处理";
        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public int? ExitCode { get; set; }
        private string _log = string.Empty;

        /// <summary>日志最大长度（5MB），超出时自动截断头部保留尾部</summary>
        private const int MaxLogLength = 5 * 1024 * 1024;

        public string Log
        {
            get => _log;
            set
            {
                _log = value ?? string.Empty;
                // 自动截断超长日志
                if (_log.Length > MaxLogLength)
                {
                    var excess = _log.Length - MaxLogLength + 1024 * 1024;
                    if (excess > 0 && excess < _log.Length)
                        _log = $"[日志已截断 {excess / 1024}KB | 超出 {MaxLogLength / 1024 / 1024}MB 上限]\n"
                             + _log.Substring(excess);
                }
            }
        }

        /// <summary>向日志追加文本（比 += 更高效，不使用 StringBuilder 中间对象）</summary>
        public void AppendLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Log += text;
        }
        public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

        private DateTimeOffset? _startedAt;
        /// <summary>任务开始处理的时间</summary>
        public DateTimeOffset? StartedAt
        {
            get => _startedAt;
            set
            {
                if (_startedAt != value)
                {
                    _startedAt = value;
                    OnPropertyChanged(nameof(StartedAt));
                    OnPropertyChanged(nameof(DurationText));
                }
            }
        }

        private DateTimeOffset? _completedAt;
        /// <summary>任务完成的时间</summary>
        public DateTimeOffset? CompletedAt
        {
            get => _completedAt;
            set
            {
                if (_completedAt != value)
                {
                    _completedAt = value;
                    OnPropertyChanged(nameof(CompletedAt));
                    OnPropertyChanged(nameof(DurationText));
                }
            }
        }
        public bool IsCancelled { get; set; } = false;

        /// <summary>耗时显示文本（如 "⏱ 3.2s" / "⏱ 1m 23s"）。处理中显示实时计时（由 UI 定时器每秒刷新）。</summary>
        public string DurationText
        {
            get
            {
                if (!StartedAt.HasValue) return string.Empty;
                var end = CompletedAt ?? DateTimeOffset.UtcNow;
                var d = end - StartedAt.Value;
                if (d < TimeSpan.Zero) d = TimeSpan.Zero;
                return "⏱ " + FormatDuration(d);
            }
        }

        /// <summary>刷新耗时显示（处理中实时计时，由 UI 定时器每秒调用）</summary>
        public void RefreshDuration() => OnPropertyChanged(nameof(DurationText));

        /// <summary>将耗时格式化为人类可读文本（&lt;1s / 3.2s / 1m 23s / 2h 5m 10s）</summary>
        private static string FormatDuration(TimeSpan d)
        {
            if (d.TotalSeconds < 1) return "<1s";
            if (d.TotalMinutes < 1) return $"{d.TotalSeconds:F1}s";
            if (d.TotalHours < 1) return $"{d.TotalMinutes:F0}m {d.Seconds}s";
            return $"{d.TotalHours:F0}h {d.Minutes}m {d.Seconds}s";
        }

        /// <summary>队列列表显示文本</summary>
        public string DisplayText => $"{Path.GetFileName(InputPath)} — {Status}";

        /// <summary>是否为报错条目（状态以"失败"开头）</summary>
        public bool HasError => !string.IsNullOrEmpty(Status) && Status.StartsWith("失败");

        /// <summary>是否已完成转换（可进行元数据编辑）</summary>
        public bool IsCompleted => !string.IsNullOrEmpty(Status) && Status.StartsWith("已完成") && ExitCode == 0;

        private bool _isMetadataExpanded;
        /// <summary>元数据编辑面板是否展开</summary>
        public bool IsMetadataExpanded
        {
            get => _isMetadataExpanded;
            set
            {
                if (_isMetadataExpanded != value)
                {
                    _isMetadataExpanded = value;
                    OnPropertyChanged(nameof(IsMetadataExpanded));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
