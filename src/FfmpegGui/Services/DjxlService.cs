using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// djxl.exe 集成服务：用于从 JXL 恢复/解码为 PNG/JPEG 等。检测优先级：手动指定路径/目录 -> ffmpeg 同目录/程序目录 -> PATH。
    /// </summary>
    public static class DjxlService
    {
        private static string? _detectedPath;
        private static bool _detected;
        // ⚠ 锁必须同时罩住 `Detect()` 与两个 getter：旧写法 Detect() 第一行就
        //   `_detected = true; _detectedPath = null;` ⇒ 扫描期间并发读者会拿到
        //   "已检测但不可用"的**假阴**（本工具的有无直接决定 JXL 走哪条路）。
        private static readonly object _gate = new();

        public static bool IsAvailable
        {
            get { lock (_gate) { if (!_detected) Detect(); return _detectedPath != null; } }
        }

        public static string? DetectedPath
        {
            get { lock (_gate) { if (!_detected) Detect(); return _detectedPath; } }
        }

        public static void Detect()
        {
            lock (_gate) { DetectCore(); }
        }

        private static void DetectCore()
        {
            _detected = true;
            _detectedPath = null;

            try
            {
                var manual = AppSettingsService.Current.JxlLibDir
                          ?? AppSettingsService.Current.CjxlPath
                          ?? AppSettingsService.Current.CjpegliPath;
                if (!string.IsNullOrWhiteSpace(manual))
                {
                    if (File.Exists(manual) && Path.GetFileName(manual).ToLower().Contains("djxl"))
                    {
                        _detectedPath = manual; return;
                    }
                    if (Directory.Exists(manual))
                    {
                        var candidate = Path.Combine(manual, PlatformServices.Djxl);
                        if (File.Exists(candidate)) { _detectedPath = candidate; return; }
                        try
                        {
                            // ⚠ 手动目录同样可能是一棵大树 ⇒ 走带预算的递归（见 EnumerateFilesSafe 注释）
                            var list = ExternalToolsDetector.EnumerateFilesSafe(
                                manual, PlatformServices.DjxlSearchWildcard);
                            if (list.Count > 0)
                            {
                                var pick = ExternalToolsDetector.ChooseBestExecutable(list);
                                if (!string.IsNullOrEmpty(pick)) { _detectedPath = pick; return; }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // ② PLAN 便携包自动检测
            try
            {
                var planFound = PlatformServices.TryFindInPlanFolder(PlatformServices.Djxl);
                if (planFound != null) { _detectedPath = planFound; return; }
            }
            catch { }

            // ③ 同目录查找（ffmpeg 目录 -> 程序目录）
            try
            {
                var ffmpegDir = AppSettingsService.Current.FfmpegDir;
                var programDir = AppDomain.CurrentDomain.BaseDirectory;
                var dirs = new[] { ffmpegDir, programDir };
                foreach (var dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    var candidate = Path.Combine(dir, PlatformServices.Djxl);
                    if (File.Exists(candidate)) { _detectedPath = candidate; return; }
                }
            }
            catch { }

            // ④ 扩展搜索路径（Windows: LocalAppData\Programs 等）
            try
            {
                var extended = ExternalToolsDetector.FindToolInExtendedPaths(
                    PlatformServices.Djxl, PlatformServices.DjxlSearchWildcard);
                if (extended != null) { _detectedPath = extended; return; }
            }
            catch { }

            // ⑤ 系统 PATH —— **非递归**（`where`/`which` 一次解析，不会被 PATH 里的大树拖死）
            //   ⚠ 这一级原本**只有一句注释、没有代码**，而本类 `:10` 的文档早就声明
            //     「检测优先级 … -> PATH」⇒ 声明与实现冲突。它在本轮变成真缺陷：
            //     `GetExtendedSearchPaths` 排除了 `C:\Windows*`（递归扫系统树既分钟级、又会翻到
            //     `Prefetch\*.pf` 这种假工具），而它给出的理由正是"第⑤步会非递归兜住 PATH 里的工具"
            //     ⇒ 该前提对 djxl 不成立 ⇒ 把 `djxl.exe` 放进 `System32` 的用户从"④ 侥幸扫到"
            //     变成"永远发现不了"，且 djxl 缺失只会静默改走 ffmpeg 兜底、用户看不出来。
            //   ⚠ 兄弟服务（`CjxlService` / `CjpegliService` / `JxrService`）一直是这么写的。
            try
            {
                if (PlatformServices.TryFindInPath(PlatformServices.Djxl, out var pathFound) && pathFound != null)
                    _detectedPath = pathFound;
            }
            catch { }
        }

        public static void ClearCache()
        {
            _detected = false; _detectedPath = null;
        }

        public static async Task<int> RunAsync(string inputPath, string outputPath, int threads = 0, Action<string>? logCallback = null, System.Threading.CancellationToken ct = default)
        {
            if (_detectedPath == null)
                throw new InvalidOperationException("djxl.exe 未找到");
            // djxl 命令：djxl input.jxl output.png （或 .jpg 来重构）
            // threads>0 时透传 --num_threads（此前签名收下线程数却从不使用，P2）
            var args = $"\"{inputPath}\" \"{outputPath}\"";
            if (threads > 0)
                args += $" --num_threads={threads}";
            logCallback?.Invoke($"[djxl] {_detectedPath} {args}\n");

            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath,
                Arguments = args,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
            var linkedToken = linked.Token;

            process.OutputDataReceived += (_, e) => { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };

            try
            {
                process.Start();
                PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try
                {
                    await process.WaitForExitAsync(linkedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    logCallback?.Invoke("[djxl] 已取消或超时，已终止进程\n");
                    return -1;
                }

                return process.ExitCode;
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[djxl] 启动失败: {ex.Message}\n");
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return -1;
            }
        }
    }
}
