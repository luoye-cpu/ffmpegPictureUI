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

        public static bool IsAvailable
        {
            get { if (!_detected) Detect(); return _detectedPath != null; }
        }

        public static string? DetectedPath
        {
            get { if (!_detected) Detect(); return _detectedPath; }
        }

        public static void Detect()
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
                            var list = new System.Collections.Generic.List<string>();
                            foreach (var f in Directory.EnumerateFiles(manual, PlatformServices.DjxlSearchWildcard, SearchOption.AllDirectories))
                            {
                                if (File.Exists(f)) list.Add(f);
                            }
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

            // ⑤ PATH
        }

        private static bool TryFindInPath(string exeName, out string? fullPath)
        {
            fullPath = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "where" : "which",
                    Arguments = exeName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (File.Exists(firstLine)) { fullPath = firstLine; return true; }
                }
            }
            catch { }
            return false;
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
            var args = $"\"{inputPath}\" \"{outputPath}\"";
            logCallback?.Invoke($"[djxl] {_detectedPath} {args}\n");

            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
            var linkedToken = linked.Token;

            process.OutputDataReceived += (_, e) => { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };

            try
            {
                process.Start();
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
