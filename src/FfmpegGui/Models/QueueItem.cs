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
        private readonly object _logGate = new();

        /// <summary>
        /// 本线程上一次从 <see cref="Log"/> getter 拿到的长度 = 它稍后写回时的“快照长度”。
        /// <c>X.Log += e</c> 展开为 get → 拼接 → set，三步都在同一条线程上执行，故此通道可靠。
        /// </summary>
        [ThreadStatic] private static int _logSnapshotLen;

        /// <summary>日志最大长度（5MB），超出时自动截断头部保留尾部</summary>
        private const int MaxLogLength = 5 * 1024 * 1024;

        /// <summary>
        /// 任务日志。<b>P2-1</b>：<c>item.Log += s</c> 是「读快照 → 拼接 → 写回」，
        /// 同一项会被多线程写（本项线程 + ffmpeg/cjxl 的 stdout 与 stderr 回调），交错时后写者拿旧快照整段覆盖
        /// ⇒ 实测 8 线程 × 400 次追加只剩 433 行。修在**属性内部**（241 处调用点一行都不用改）：
        /// getter 记下交给本线程的快照长度，setter 在锁内只把 <c>value</c> 超出该快照的部分接上 ⇒ 无损也不重复。
        /// ⚠ 试过“按最长公共前缀做只追加合并”，**不成立**：纯字符串里没带快照长度，
        ///   两次追加共享前缀时（如 <c>t1-23;</c> 与 <c>t1-6;</c> 公共前缀到 <c>t1-</c>）会被缝成一行而损坏内容。
        ///   ⇒ 快照通道对不上时宁可退回旧的“直接赋值”语义，也绝不缝合。回归锁：<c>ServiceProbe procstreams</c> 第①项。
        /// </summary>
        public string Log
        {
            get { lock (_logGate) { _logSnapshotLen = _log.Length; return _log; } }
            set => CommitLog(value ?? string.Empty);
        }

        private void CommitLog(string value)
        {
            lock (_logGate)
            {
                int baseLen = _logSnapshotLen;
                bool snapshotUsable = baseLen >= 0 && baseLen <= value.Length && baseLen <= _log.Length
                    && string.CompareOrdinal(value, 0, _log, 0, baseLen) == 0;
                if (snapshotUsable)
                {
                    if (value.Length > baseLen) _log += value.Substring(baseLen);   // 只接上本次追加的部分
                }
                else
                {
                    _log = value;                                                    // 通道对不上 ⇒ 退回旧语义（不猜、不缝）
                }
                TruncateLocked();
            }
        }

        private void TruncateLocked()
        {
            if (_log.Length <= MaxLogLength) return;
            var excess = _log.Length - MaxLogLength + 1024 * 1024;
            if (excess > 0 && excess < _log.Length)
                _log = $"[日志已截断 {excess / 1024}KB | 超出 {MaxLogLength / 1024 / 1024}MB 上限]\n"
                     + _log.Substring(excess);
        }

        /// <summary>向日志追加文本（锁内直接追加：不走读-改-写，也就不依赖快照通道）</summary>
        public void AppendLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_logGate)
            {
                _log += text;
                TruncateLocked();
            }
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

        /// <summary>
        /// P7a-3「同源登记」：非主分支通路如果这次执行的命令行是由 <c>FfmpegCommandBuilder.BuildArguments</c>
        /// 产出的，就在这里记下**那次调用真正用的 options 实例与探测输入**（两者必须成对，不然等于没登记）。
        /// 链尾的色彩后置只对登记过的任务执行；没登记 ⇒ 命令行是别处自建的（外部编码器、两步法 filter_complex），
        /// 此时**只许显式记录“裁决点未参与”，绝不拿按源文件推断的语义去贴标签**（P7d 的教训）。
        /// </summary>
        public FfmpegOptions? ColorPostOptions { get; set; }
        /// <summary>与 <see cref="ColorPostOptions"/> 成对的探测输入（stdin 时存“喂进管道的真实源文件”）。详见该属性说明。</summary>
        public string? ColorPostProbeInput { get; set; }

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

        // ── 终态/状态判定单一真值（P2-6 收敛）──
        // 此前"失败*/已完成*/已停止/已删除/处理中/待处理"的魔法字符串判定散落约 24 处，
        // 各处组合语义略有差异（StartsWith vs ==、是否看 ExitCode）。统一收敛到这里，
        // 每个谓词的语义注释即其规范定义；替换点必须选择语义等价的谓词，不得"顺手统一"。
        /// <summary>状态以"失败"开头（与 HasError 同义，语义命名版）</summary>
        public bool IsFailure => HasError;

        /// <summary>状态以"已完成"开头（仅看状态文本，不看 ExitCode——部分历史判定点仅用前缀）</summary>
        public bool IsSuccessStatus => !string.IsNullOrEmpty(Status) && Status.StartsWith("已完成");

        /// <summary>状态精确等于"已停止"（用户主动停止；注意"失败…"不是已停止）</summary>
        public bool IsStopped => Status == "已停止";

        /// <summary>状态精确等于"已删除"（用户从队列移除）</summary>
        public bool IsDeleted => Status == "已删除";

        /// <summary>
        /// 是否到达终态：已完成(前缀)/失败(前缀)/已停止/已删除。
        /// 与 DefaultQueueOrchestrator.IsCompletedOrTerminal 逐字等价
        /// （已完成、失败用前缀匹配——状态可带后缀如"已完成 (ffmpeg)"；
        ///   已停止、已删除用精确匹配——历史上无后缀形态）。
        /// </summary>
        public bool IsTerminal =>
            (!string.IsNullOrEmpty(Status) && Status.StartsWith("已完成"))
            || IsFailure || IsStopped || IsDeleted;

        /// <summary>状态精确等于"待处理"（排队中，尚未启动）</summary>
        public bool IsPending => Status == "待处理";

        /// <summary>状态精确等于"处理中"（正在转换）</summary>
        public bool IsProcessing => Status == "处理中";

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
