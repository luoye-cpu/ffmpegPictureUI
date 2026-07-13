using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    public static class FfmpegRunner
    {
        public static async Task<int> RunAsync(string arguments, Action<string>? logCallback = null, string? ffmpegPath = null, CancellationToken ct = default)
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

            try
            {
                PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
            }
            catch { }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 支持 CancellationToken：取消时杀死进程树
            if (ct.CanBeCanceled)
            {
                using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
                try { await process.WaitForExitAsync(ct); }
                catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
            }
            else
            {
                await process.WaitForExitAsync();
            }
            return process.ExitCode;
        }
    }
}