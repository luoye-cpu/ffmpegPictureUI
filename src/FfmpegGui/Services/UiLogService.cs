using System;
using System.IO;
using System.Text;

namespace FfmpegGui.Services
{
    public enum UiLogLevel
    {
        Info,
        Success,
        Warn,
        Error
    }

    /// <summary>
    /// 主窗口执行日志的唯一写入口。
    /// - 所有写操作可来自任意线程；Sink 由 UI 在初始化时注册，并负责切回 UI 线程。
    /// - 每条记录带本地时间、级别；消息内的换行按行展开，保证文件与 TextBox 都是真正的一行一条。
    /// - 可选文件落盘；文件打开失败不影响 UI 日志。
    /// </summary>
    public static class UiLogService
    {
        private static readonly object Gate = new();
        private static Action<string>? _sink;
        private static StreamWriter? _writer;
        private static string? _filePath;

        public static void Attach(Action<string> sink)
        {
            lock (Gate) { _sink = sink; }
        }

        /// <summary>启用落盘；path 为 null/空表示关闭。失败静默但不影响 UI。</summary>
        public static void ConfigureFile(string? path)
        {
            lock (Gate)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
                _filePath = path;
                if (string.IsNullOrWhiteSpace(path)) return;

                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    // FileShare.ReadWrite：程序运行期间用户/排障工具仍可 tail/读取日志文件。
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
                }
                catch
                {
                    _writer = null;
                }
            }
        }

        public static void Info(string message) => Write(UiLogLevel.Info, message);
        public static void Success(string message) => Write(UiLogLevel.Success, message);
        public static void Warn(string message) => Write(UiLogLevel.Warn, message);
        public static void Error(string message) => Write(UiLogLevel.Error, message);

        public static void Write(UiLogLevel level, string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            var body = message.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
            if (body.Length == 0) return;

            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var levelTag = level switch
            {
                UiLogLevel.Success => "OK",
                UiLogLevel.Warn => "WARN",
                UiLogLevel.Error => "ERROR",
                _ => "INFO"
            };

            var lines = new System.Collections.Generic.List<string>();
            foreach (var raw in body.Split('\n'))
                lines.Add($"[{stamp}] [{levelTag}] {raw}");

            Action<string>? sink;
            lock (Gate)
            {
                foreach (var line in lines)
                    try { _writer?.WriteLine(line); } catch { /* 落盘失败不影响 UI */ }
                sink = _sink;
            }

            // 锁外调用 sink：UI sink 现在是入队（可能很快），但也不能把文件锁
            // 和 UI 调度串在一起，避免后台日志写互相等待。
            if (sink != null)
            {
                foreach (var line in lines)
                    try { sink(line); } catch { /* UI sink 不应反向破坏日志写 */ }
            }
        }

        public static string? CurrentFile
        {
            get { lock (Gate) return _filePath; }
        }
    }
}
