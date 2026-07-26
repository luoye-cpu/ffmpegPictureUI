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

            Process? procDj = null;
            Process? procCj = null;
            try
            {
                using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var linkedToken = linked.Token;

                // 启动 cjpegli：从 stdin 读取，输出到 stdout
                var distance = Models.FfmpegOptions.MapJpegliDistance(quality);
                // 管道模式下不传递 --num_threads：部分版本不支持，且管道 I/O 非 CPU 密集
                var cjArgs = $"- - --distance {distance:F1}";

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

                procCj = Process.Start(psiCj);
                if (procCj == null)
                {
                    logCallback?.Invoke("[pipeline] 启动 cjpegli 失败\n");
                    return -1;
                }
                PlatformServices.SetSafePriority(procCj, AppSettingsService.Current.FfmpegPriority);

                // 启动 djxl：解码为 PNG 并通过 '-' 输出到 stdout
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

                procDj = Process.Start(psiDj);
                if (procDj == null)
                {
                    logCallback?.Invoke("[pipeline] 启动 djxl 失败\n");
                    return -1;
                }
                PlatformServices.SetSafePriority(procDj, AppSettingsService.Current.FfmpegPriority);

                // 启动 stderr 消费者（非阻塞）
                var cjErrTask = ConsumeLinesAsync(procCj.StandardError, s => logCallback?.Invoke("[cjpegli] " + s + "\n"));
                var djErrTask = ConsumeLinesAsync(procDj.StandardError, s => logCallback?.Invoke("[djxl] " + s + "\n"));

                // ── 管道传输：先完成传输 + 关闭流，再等待进程退出（避免死锁）──
                // 传输 1：djxl stdout → cjpegli stdin
                Exception? transferError = null;
                try
                {
                    await procDj.StandardOutput.BaseStream.CopyToAsync(procCj.StandardInput.BaseStream, linkedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logCallback?.Invoke("[pipeline] 传输取消\n");
                }
                catch (Exception ex)
                {
                    transferError = ex;
                    logCallback?.Invoke($"[pipeline] 数据传输失败: {ex.Message}\n");
                }

                // 关闭 cjpegli stdin 发送 EOF
                try { procCj.StandardInput.Close(); } catch { }

                // 传输 2：cjpegli stdout → 输出文件
                Exception? writeError = null;
                try
                {
                    using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await procCj.StandardOutput.BaseStream.CopyToAsync(fs, linkedToken).ConfigureAwait(false);
                    await fs.FlushAsync(linkedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logCallback?.Invoke("[pipeline] 写入输出文件取消\n");
                }
                catch (Exception ex)
                {
                    writeError = ex;
                    logCallback?.Invoke($"[pipeline] 写入输出文件失败: {ex.Message}\n");
                }

                // 现在 stream 都已关闭/读完，进程应该自行退出
                try { await procCj.WaitForExitAsync(linkedToken).ConfigureAwait(false); } catch (OperationCanceledException) { }
                try { await procDj.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }

                await cjErrTask;
                await djErrTask;

                if (linkedToken.IsCancellationRequested)
                {
                    logCallback?.Invoke("[pipeline] 超时或取消，已终止进程\n");
                    return -1;
                }

                // 如果 cjpegli 启动即失败（如参数错误），stdin 早已关闭，传输必然失败，但仍应正常退出
                var djExit = procDj.HasExited ? procDj.ExitCode : -1;
                var cjExit = procCj.HasExited ? procCj.ExitCode : -1;

                if (djExit == 0 && cjExit == 0 && transferError == null && writeError == null)
                {
                    logCallback?.Invoke("[pipeline] 管道完成 (退出码 0)\n");
                    return 0;
                }
                else
                {
                    logCallback?.Invoke($"[pipeline] 管道失败: djxl={djExit}, cjpegli={cjExit}\n");
                    return djExit != 0 ? djExit : (cjExit != 0 ? cjExit : -1);
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[pipeline] 异常: {ex.Message}\n");
                return -1;
            }
            finally
            {
                if (procCj != null && !procCj.HasExited)
                {
                    try { procCj.Kill(entireProcessTree: true); } catch { }
                }
                if (procDj != null && !procDj.HasExited)
                {
                    try { procDj.Kill(entireProcessTree: true); } catch { }
                }
                try { procDj?.Dispose(); } catch { }
                try { procCj?.Dispose(); } catch { }
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
