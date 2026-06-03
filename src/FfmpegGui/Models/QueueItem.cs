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
        public string Log { get; set; } = string.Empty;
        public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
        /// <summary>任务开始处理的时间</summary>
        public DateTimeOffset? StartedAt { get; set; }
        /// <summary>任务完成的时间</summary>
        public DateTimeOffset? CompletedAt { get; set; }
        public bool IsCancelled { get; set; } = false;

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
