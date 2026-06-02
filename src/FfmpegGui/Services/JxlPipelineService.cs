using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 管道服务：尝试将 `djxl` 的 stdout 直接连接到 `cjpegli` 的 stdin，避免中间临时文件。
    /// 说明：本方法为最佳努力实现，会记录 stdout/stderr 到日志并返回两个进程的退出码（0 表示成功）。
    /// 若管道模式不可用或失败，调用者应回退到基于临时文件的方案。
    /// </summary>
    public static class JxlPipelineService
    {
        public static async Task<int> TryPipeDjxlToCjpegliAsync(string inputPath, string outputPath, int quality = 90, int threads = 0, Action<string>? logCallback = null, System.Threading.CancellationToken ct = default)
        {
            var djxl = DjxlService.DetectedPath;
            var cjpeg = CjpegliService.DetectedPath;
            if (string.IsNullOrEmpty(djxl) || string.IsNullOrEmpty(cjpeg))
            {
                logCallback?.Invoke("[pipeline] 未检测到 djxl 或 cjpegli，无法使用管道。\n");
                return -1;
            }

            logCallback?.Invoke($"[pipeline] 尝试管道：{Path.GetFileName(djxl)} -> {Path.GetFileName(cjpeg)}\n");

            try
            {
                using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var linkedToken = linked.Token;

                // 启动 cjpegli：从 stdin 读取，输出到 stdout（通过重定向捕获后写入文件）
                var cjArgs = $"- - --quality {quality}";
                if (threads > 0) cjArgs += $" --num_threads={threads}";

                var psiCj = new ProcessStartInfo
                {
                    FileName = cjpeg,
                    Arguments = cjArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var procCj = Process.Start(psiCj);
                if (procCj == null)
                {
                    logCallback?.Invoke("[pipeline] 启动 cjpegli 失败\n");
                    return -1;
                }

                var cjErrTask = ConsumeLinesAsync(procCj.StandardError, s => logCallback?.Invoke("[cjpegli] " + s + "\n"));

                // 启动 djxl：解码为 PNG 并通过 '-' 输出到 stdout（必须指定 --output_format 以避免格式检测失败）
                var djArgs = $"\"{inputPath}\" --output_format=png -";
                var psiDj = new ProcessStartInfo
                {
                    FileName = djxl,
                    Arguments = djArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var procDj = Process.Start(psiDj);
                if (procDj == null)
                {
                    logCallback?.Invoke("[pipeline] 启动 djxl 失败\n");
                    try { procCj.Kill(); } catch { }
                    return -1;
                }

                var djErrTask = ConsumeLinesAsync(procDj.StandardError, s => logCallback?.Invoke("[djxl] " + s + "\n"));

                // 传输流并写文件
                var copyTasks = new[]
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            await procDj.StandardOutput.BaseStream.CopyToAsync(procCj.StandardInput.BaseStream, linkedToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { logCallback?.Invoke("[pipeline] 传输取消\n"); }
                        catch (Exception ex) { logCallback?.Invoke($"[pipeline] 数据传输失败: {ex.Message}\n"); }
                        finally { try { procCj.StandardInput.Close(); } catch { } }
                    }),
                    Task.Run(async () =>
                    {
                        try
                        {
                            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                            await procCj.StandardOutput.BaseStream.CopyToAsync(fs, linkedToken).ConfigureAwait(false);
                            await fs.FlushAsync(linkedToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { logCallback?.Invoke("[pipeline] 写入输出文件取消\n"); }
                        catch (Exception ex) { logCallback?.Invoke($"[pipeline] 写入输出文件失败: {ex.Message}\n"); }
                    })
                };

                // 等待所有任务与进程完成或超时
                var allTasks = Task.WhenAll(copyTasks).ContinueWith(_ => { });
                var processesTask = Task.WhenAll(procDj.WaitForExitAsync(), procCj.WaitForExitAsync());

                var completed = await Task.WhenAny(Task.WhenAll(allTasks, processesTask, djErrTask, cjErrTask), Task.Delay(Timeout.Infinite, linkedToken)).ConfigureAwait(false);
                if (linkedToken.IsCancellationRequested)
                {
                    try { procDj.Kill(entireProcessTree: true); } catch { }
                    try { procCj.Kill(entireProcessTree: true); } catch { }
                    logCallback?.Invoke("[pipeline] 超时或取消，已终止进程\n");
                    return -1;
                }

                var djExit = procDj.HasExited ? procDj.ExitCode : -1;
                var cjExit = procCj.HasExited ? procCj.ExitCode : -1;
                try { procDj.Dispose(); } catch { }
                try { procCj.Dispose(); } catch { }

                if (djExit == 0 && cjExit == 0)
                {
                    logCallback?.Invoke("[pipeline] 管道完成 (退出码 0)\n");
                    return 0;
                }
                else
                {
                    logCallback?.Invoke($"[pipeline] 管道失败: djxl={djExit}, cjpegli={cjExit}\n");
                    return djExit != 0 ? djExit : cjExit;
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[pipeline] 异常: {ex.Message}\n");
                return -1;
            }
        }

        private static async Task ConsumeLinesAsync(StreamReader reader, Action<string> onLine)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    onLine?.Invoke(line);
                }
            }
            catch { }
        }
    }
}
