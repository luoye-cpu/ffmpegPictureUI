using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    public static class FfmpegRunner
    {
        public static async Task<int> RunAsync(string arguments, Action<string>? logCallback = null, string? ffmpegPath = null)
        {
            var fileName = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (s, e) => { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };

            process.Start();

            // 应用用户设定的进程优先级（与 Windows 任务管理器顺序一致）
            try
            {
                process.PriorityClass = AppSettingsService.Current.FfmpegPriority switch
                {
                    0 => ProcessPriorityClass.RealTime,
                    1 => ProcessPriorityClass.High,
                    2 => ProcessPriorityClass.AboveNormal,
                    4 => ProcessPriorityClass.BelowNormal,
                    5 => ProcessPriorityClass.Idle,
                    _ => ProcessPriorityClass.Normal
                };
            }
            catch { /* 权限不足时静默忽略 */ }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }
}