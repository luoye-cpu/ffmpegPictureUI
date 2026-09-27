using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FfmpegGui.Models;

namespace FfmpegGui.Services
{
    /// <summary>JSONL 紧凑序列化上下文（AOT 安全）</summary>
    [JsonSourceGenerationOptions(
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(LogEvent))]
    internal partial class LogJsonContext : JsonSerializerContext
    {
    }

    /// <summary>日志格式</summary>
    public enum LogFormat
    {
        Text,
        JsonLines
    }

    /// <summary>结构化日志事件</summary>
    public sealed class LogEvent
    {
        [JsonPropertyName("t")] public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
        [JsonPropertyName("l")] public string Level { get; set; } = "info";
        [JsonPropertyName("c")] public string? Category { get; set; }
        [JsonPropertyName("m")] public string Message { get; set; } = "";
        [JsonPropertyName("f")] public string? File { get; set; }
        [JsonPropertyName("p")] public int? Progress { get; set; }      // 完成项数
        [JsonPropertyName("tot")] public int? Total { get; set; }       // 总项数
        [JsonPropertyName("dur")] public double? DurationSec { get; set; } // 单项耗时秒
        [JsonPropertyName("code")] public int? ExitCode { get; set; }
    }

    /// <summary>
    /// 控制台队列进度观察者 - 用于 --headless 模式，将队列进度输出到 stdout
    /// 支持 Text/JSONL 双模式，以及双写文件+控制台
    /// </summary>
    internal sealed class ConsoleQueueObserver : IQueueObserver, IDisposable
    {
        private int _completedCount;
        private int _totalCount;
        private int _failedCount;
        private readonly LogLevel _logLevel;
        private readonly LogFormat _logFormat;
        private readonly StreamWriter? _logWriter;
        private readonly object _writeLock = new();

        public ConsoleQueueObserver(LogLevel logLevel = LogLevel.Info, LogFormat logFormat = LogFormat.Text, string? logFile = null)
        {
            _logLevel = logLevel;
            _logFormat = logFormat;

            if (!string.IsNullOrWhiteSpace(logFile))
            {
                try
                {
                    // 确保日志文件所在目录存在
                    var dir = Path.GetDirectoryName(logFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    _logWriter = new StreamWriter(logFile, append: false) { AutoFlush = true };
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"⚠️ 无法打开日志文件: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _logWriter?.Dispose();
        }

        public void SetTotalCount(int count)
        {
            _totalCount = count;
        }

        public void OnItemProgress(QueueItem item)
        {
            if (_logLevel >= LogLevel.Debug)
            {
                Write(LogLevel.Debug, "progress",
                    $"[{Volatile.Read(ref _completedCount) + 1}/{_totalCount}] {item.DisplayName} → {item.Status}",
                    file: item.InputPath);
                FlushItemLog(item, final: false);
            }
        }

        public void OnItemCompleted(QueueItem item)
        {
            // ⚠ P2-2：本回调由多个项线程并发进入，计数必须原子。
            var done = Interlocked.Increment(ref _completedCount);
            bool hasError = ItemHasError(item);
            // ⚠ 失败计数**不得**挂在“算得出耗时”的分支里：专项通路（gif→avif 两步法等）在子方法内
            //   就发布了终态，此时 QueueProcessor 尚未写 CompletedAt ⇒ 旧写法既不计失败、也不报错级
            //   （实测一条失败任务打印成「完成 1 项, 失败 0 项」）。
            if (hasError) Interlocked.Increment(ref _failedCount);
            var level = hasError ? LogLevel.Error
                                 : (item.IsStopped || item.IsDeleted ? LogLevel.Warn : LogLevel.Info);
            var elapsedSec = (item.CompletedAt.HasValue && item.StartedAt.HasValue)
                ? (item.CompletedAt.Value - item.StartedAt.Value).TotalSeconds
                : (double?)null;

            if (elapsedSec.HasValue)
            {
                var elapsedStr = FormatDuration(TimeSpan.FromSeconds(elapsedSec.Value));
                Write(level, "item_completed",
                    $"[{done}/{_totalCount}] {item.DisplayName} {item.Status} ({elapsedStr})",
                    file: item.InputPath,
                    progress: done,
                    total: _totalCount,
                    durationSec: elapsedSec,
                    exitCode: item.ExitCode);
            }
            else
            {
                Write(level, "item_completed",
                    $"[{done}/{_totalCount}] {item.DisplayName} → {item.Status}",
                    file: item.InputPath,
                    progress: done,
                    total: _totalCount,
                    exitCode: item.ExitCode);
            }

            // 项级详细日志（编码器/色彩/元数据各阶段输出）：增量刷出。
            // ❗旧写法只在完成时一次性转储 item.Log，而元数据恢复/保真 ICC 附着等收尾步骤在
            //    完成事件之后仍在追加日志 → 那些行永远不可见（曾据此误判“附着未执行”）。
            FlushItemLog(item, final: true);
        }

        /// <summary>已转储过项级日志长度的记录（按项引用）。</summary>
        private readonly Dictionary<QueueItem, int> _itemLogSeen = new();

        /// <summary>
        /// 增量输出项级日志：只打印自上次以来新追加的部分。
        /// 失败项以 Error 级输出（headless 下可诊断）；其余仅 Debug/Trace 阈值下输出。
        /// </summary>
        private void FlushItemLog(QueueItem item, bool final)
        {
            var log = item.Log ?? "";
            bool asError = final && ItemHasError(item);
            if (!asError && _logLevel < LogLevel.Debug) { return; }

            string tail;
            lock (_writeLock)
            {
                _itemLogSeen.TryGetValue(item, out int seen);
                // 日志超 MaxLogLength 被裁剪时 seen 会大于当前长度 → 从头重输（宁可重复不可遗漏）
                if (seen > log.Length) seen = 0;
                if (log.Length <= seen) return;
                tail = log.Substring(seen);
                // 完成事件并不等于任务结束（元数据恢复/ICC 附着等收尾在其后运行），
                // 故不能删除记录：否则后续进度事件会从 0 重输整段日志（实测重复输出）。
                // 映射在队列完成时统一清理。
                _itemLogSeen[item] = log.Length;
            }

            var lv = asError ? LogLevel.Error : LogLevel.Debug;
            foreach (var raw in tail.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0) continue;
                Write(lv, "item_log", line, file: item.OutputPath ?? item.InputPath);
            }
        }

        public void OnQueueCompleted()
        {
            int done = Volatile.Read(ref _completedCount), failed = Volatile.Read(ref _failedCount);
            Write(LogLevel.Info, "queue_completed",
                $"队列处理完毕 — 完成 {done} 项, 失败 {failed} 项",
                progress: done,
                total: _totalCount);
            lock (_writeLock) { _itemLogSeen.Clear(); }   // 项级日志增量记录随队列释放
        }


        // P2-6 收敛定性：旧写法 Contains("失败")/Contains("错误") 对当前状态词表
        // 【不会误判】—— 全部失败态均以"失败"开头（无中缀形态），没有任何状态含"错误"；
        // 但 Contains 是脆弱写法（若未来出现"已完成 (回退失败)"式后缀会假阳）。
        // 统一为 IsFailure（StartsWith 语义），行为对现有词表完全等价。
        private static bool ItemHasError(QueueItem item) => item.IsFailure;

        private void Write(LogLevel level, string category, string message,
            string? file = null, int? progress = null, int? total = null, double? durationSec = null, int? exitCode = null)
        {
            // 级别过滤：枚举值越小越严重 (Error=0 … Trace=4)，故仅输出「严重度 ≥ 阈值」的消息。
            // 修正旧反向比较（level >= _logLevel）：默认 Info 阈值下 ❌ 错误消息永不可见、
            // 而 Debug 消息反而全量输出（Text 模式）；JSONL 模式则完全不过滤。
            if ((int)level > (int)_logLevel) return;

            lock (_writeLock)
            {
                if (_logFormat == LogFormat.JsonLines)
                {
                    var evt = new LogEvent
                    {
                        Level = level.ToString().ToLowerInvariant(),
                        Category = category,
                        Message = message,
                        File = file,
                        Progress = progress,
                        Total = total,
                        DurationSec = durationSec,
                        ExitCode = exitCode
                    };
                    var json = JsonSerializer.Serialize(evt, LogJsonContext.Default.LogEvent);
                    WriteToOutputs(json);
                }
                else
                {
                    // Text 格式
                    string text;
                    if (level == LogLevel.Error) text = $"❌ {message}";
                    else if (level == LogLevel.Warn) text = $"⚠️  {message}";
                    else if (category == "queue_completed") text = $"═══ {message}";
                    else text = message;

                    WriteToOutputs(text);
                }
            }
        }

        private void WriteToOutputs(string text)
        {
            // 控制台（带颜色）
            if (_logFormat == LogFormat.Text)
            {
                var prev = Console.ForegroundColor;
                if (text.StartsWith("❌")) Console.ForegroundColor = ConsoleColor.Red;
                else if (text.StartsWith("⚠️")) Console.ForegroundColor = ConsoleColor.Yellow;
                else if (text.StartsWith("✅")) Console.ForegroundColor = ConsoleColor.Green;
                else if (text.StartsWith("═══")) Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(text);
                Console.ForegroundColor = prev;
            }
            else
            {
                Console.WriteLine(text);
            }

            // 文件
            _logWriter?.WriteLine(text);
        }

        private static string FormatDuration(TimeSpan d)
        {
            if (d.TotalSeconds < 1) return "<1s";
            if (d.TotalMinutes < 1) return $"{d.TotalSeconds:F1}s";
            if (d.TotalHours < 1) return $"{d.TotalMinutes:F0}m {d.Seconds}s";
            return $"{d.TotalHours:F0}h {d.Minutes}m {d.Seconds}s";
        }
    }

    /// <summary>日志级别</summary>
    public enum LogLevel
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3,
        Trace = 4
    }
}