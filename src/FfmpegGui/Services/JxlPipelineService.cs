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
        public static async Task<int> TryPipeDjxlToCjpegliAsync(string inputPath, string outputPath, Models.FfmpegOptions opts, Action<string>? logCallback = null, System.Threading.CancellationToken ct = default)
        {
            var djxl = DjxlService.DetectedPath;
            var cjpeg = CjpegliService.DetectedPath;
            if (string.IsNullOrEmpty(djxl) || string.IsNullOrEmpty(cjpeg))
            {
                logCallback?.Invoke("[pipeline] 未检测到 djxl 或 cjpegli，无法使用管道。\n");
                return -1;
            }

            logCallback?.Invoke($"[pipeline] 尝试管道：{Path.GetFileName(djxl)} -> {Path.GetFileName(cjpeg)}\n");

            // 探测 JXL 输入色彩与 alpha：PPM/PAM 流不携带色彩标签，
            // 必须由 cjpegli -x color_space 显式标记（否则广色域 JXL 降级为 sRGB）
            var jxlMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath, logCallback, ct);
            var pipeFmt = jxlMeta.hasAlpha ? "pam" : "ppm";
            var colorSpace = ColorEncodingHelper.MapToCjxlColorSpace(jxlMeta);

            Process? procDj = null;
            Process? procCj = null;
            try
            {
                using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(30));
                using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var linkedToken = linked.Token;

                // 启动 cjpegli：从 stdin 读取，输出到 stdout
                // 统一由 BuildCjpegliArguments 生成参数，确保高级选项（色度采样/渐进/fixed_code/
                // 自适应量化/ICC）与本编码器直编路径一致；includeThreads=false：裸流管道下
                // 不传 --num_threads（部分版本不支持，且管道 I/O 非 CPU 密集）。
                // 色彩标记在下方显式补充：输入为 "-" 时 BuildCjpegliArguments 不生成 color_space。
                var cjArgs = CjpegliService.BuildCjpegliArguments("-", "-", opts, jxlMeta, iccPath: null, includeThreads: false);
                if (!string.IsNullOrWhiteSpace(colorSpace))
                    cjArgs += $" -x color_space={colorSpace}";

                var psiCj = new ProcessStartInfo
                {
                    FileName = cjpeg,
                    Arguments = cjArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
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

                // 启动 djxl：解码为 PPM/PAM 流并通过 '-' 输出到 stdout（raw 流下 -x 才生效）
                var djArgs = $"\"{inputPath}\" --output_format={pipeFmt} -";
                var psiDj = new ProcessStartInfo
                {
                    FileName = djxl,
                    Arguments = djArgs,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
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

                // ── 管道传输：双管道并发泵（消除死锁）──
                // cjpegli 的 JPEG 输出远超管道缓冲（~64KB）：若先等 stdin 喂完再读 stdout，
                // cjpegli 会阻塞在 stdout 写、停止消费 stdin → djxl 阻塞 → 死锁到超时。
                // 因此 stdout→文件 与 stdin 喂入必须【同时】泵送，全部完成后才关 stdin 等退出。
                var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                Exception? writeError = null;
                Exception? transferError = null;

                // 泵 1：cjpegli stdout → 输出文件（先启动，尽早排空）
                var writeTask = Task.Run(async () =>
                {
                    try
                    {
                        await procCj.StandardOutput.BaseStream.CopyToAsync(fs, linkedToken).ConfigureAwait(false);
                        await fs.FlushAsync(linkedToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { logCallback?.Invoke("[pipeline] 写入输出文件取消\n"); }
                    catch (Exception ex)
                    {
                        writeError = ex;
                        logCallback?.Invoke($"[pipeline] 写入输出文件失败: {ex.Message}\n");
                    }
                }, CancellationToken.None);

                // 泵 2：djxl stdout → cjpegli stdin（与泵 1 并发）
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

                // 关闭 cjpegli stdin 发送 EOF（两个泵都推进到不能推进为止）
                try { procCj.StandardInput.Close(); } catch { }
                await writeTask.ConfigureAwait(false);
                try { fs.Dispose(); } catch { }

                // 等待进程退出：必须带 linkedToken（超时/取消可打断），
                // 且取消路径先杀进程——djxl 可能阻塞在无读者的 stdout 上永不退出，
                // 裸 CancellationToken.None 等待会让 finally 的 Kill 永不可达（P1-N4）。
                try { await procCj.WaitForExitAsync(linkedToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { if (!procCj.HasExited) procCj.Kill(entireProcessTree: true); } catch { } }
                try { await procDj.WaitForExitAsync(linkedToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { if (!procDj.HasExited) procDj.Kill(entireProcessTree: true); } catch { } }

                await cjErrTask;
                await djErrTask;

                if (linkedToken.IsCancellationRequested)
                {
                    logCallback?.Invoke("[pipeline] 超时或取消，已终止进程\n");
                    CleanupTruncatedOutput(outputPath);
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
                    // 失败时输出必然截断/损坏，删除避免上层按"文件存在"误判成功（P2）
                    CleanupTruncatedOutput(outputPath);
                    return djExit != 0 ? djExit : (cjExit != 0 ? cjExit : -1);
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[pipeline] 异常: {ex.Message}\n");
                CleanupTruncatedOutput(outputPath);
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

        /// <summary>删除失败/取消后残留的截断输出，避免上层按"文件存在"误判成功（best-effort）。</summary>
        private static void CleanupTruncatedOutput(string outputPath)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
    }
}
