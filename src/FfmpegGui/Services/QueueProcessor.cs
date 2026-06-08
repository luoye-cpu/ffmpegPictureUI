using FfmpegGui.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    public class QueueProcessor
    {
        private readonly ConcurrentQueue<QueueItem> _queue = new();
        private readonly object _queueLock = new();
        private readonly List<Task> _running = new();
        private CancellationTokenSource? _cts;
        private int _concurrency = 2;
        // 请求在当前队列完成后优雅停止（不立刻 Cancel）
        private volatile bool _stopAfterQueueRequested = false;
        private readonly Action<QueueItem> _onItemUpdated;
        private readonly Action? _onQueueStopped;

        public QueueProcessor(Action<QueueItem> onItemUpdated, Action? onStopped = null)
        {
            _onItemUpdated = onItemUpdated;
            _onQueueStopped = onStopped;
        }

        public void Add(QueueItem item)
        {
            item.Status = "待处理";
            _queue.Enqueue(item);
            _onItemUpdated?.Invoke(item);
        }

        public void SetConcurrency(int c) => _concurrency = Math.Max(1, c);

        public void Start(int? concurrency = null)
        {
            if (concurrency.HasValue)
                _concurrency = Math.Max(1, concurrency.Value);
            // 如果已在运行则先停止
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
            }
            // 每次启动时清除优雅停止请求
            _stopAfterQueueRequested = false;
            _cts = new CancellationTokenSource();
            Task.Run(() => ProcessAsync(_cts.Token));
        }

        public void Stop()
        {
            // 立即取消并清除任何优雅停止请求
            _stopAfterQueueRequested = false;
            _cts?.Cancel();
            _cts = null;
        }

        /// <summary>
        /// 请求在当前队列处理完后优雅停止（不会立刻中断正在运行的任务）
        /// </summary>
        public void StopAfterCurrentQueue()
        {
            _stopAfterQueueRequested = true;
        }

        /// <summary>
        /// 取消已请求的“完成当前队列后停止”请求（如果尚未生效）。
        /// </summary>
        public void CancelStopAfterCurrentQueue()
        {
            _stopAfterQueueRequested = false;
        }

        /// <summary>
        /// 清空待处理（尚未开始执行）的队列项，并返回被清空的项列表。
        /// 注意：调用方负责处理 UI 更新，此处不再逐个回调 _onItemUpdated。
        /// </summary>
        public List<QueueItem> ClearPending()
        {
            var removed = new List<QueueItem>();
            lock (_queueLock)
            {
                while (_queue.TryDequeue(out var item))
                {
                    item.IsCancelled = true;
                    item.Status = "已删除";
                    removed.Add(item);
                }
            }
            return removed;
        }

        private async Task ProcessAsync(CancellationToken ct)
        {
            var sem = new SemaphoreSlim(_concurrency);
            var tasks = new List<Task>();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                // 如果已请求在当前队列完成后停止，且当前无待处理项且所有已启动任务均完成，则退出循环
                if (_stopAfterQueueRequested && _queue.IsEmpty && tasks.All(t => t.IsCompleted))
                {
                    break;
                }

                QueueItem? item = null;
                lock (_queueLock)
                {
                    if (_queue.TryDequeue(out var it)) item = it;
                }
                if (item != null)
                {
                    // 跳过已取消的任务
                    if (item.IsCancelled)
                    {
                        item.Status = "已删除";
                        _onItemUpdated?.Invoke(item);
                        continue;
                    }

                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    var captured = item; // capture for closure
                    var t = Task.Run(async () =>
                    {
                        try
                        {
                            captured.Status = "处理中";
                            captured.StartedAt = DateTimeOffset.UtcNow;
                            _onItemUpdated?.Invoke(captured);

                            // 确保输出目录存在 —— 先将输出路径标准化为绝对路径，避免相对路径在不同进程中导致位置不一致
                            string finalOutputPath;
                            try
                            {
                                finalOutputPath = Path.GetFullPath(item.OutputPath);
                            }
                            catch
                            {
                                finalOutputPath = item.OutputPath;
                            }

                            var outDir = Path.GetDirectoryName(finalOutputPath);
                            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                                Directory.CreateDirectory(outDir);

                            // ── 根据编码器后端调度 ──
                            var backend = captured.Options.EncoderBackend;
                            var inputExt = Path.GetExtension(captured.InputPath).ToLowerInvariant();

                            // GIF → AVIF：FFmpeg 编码器丢弃 alpha，走 avifenc 两步法保留透明通道
                            if (inputExt == ".gif"
                                && captured.Options.Format.Equals("avif", StringComparison.OrdinalIgnoreCase)
                                && HasAvifencAvailable())
                            {
                                await ProcessGifToAvifViaAvifencAsync(captured, finalOutputPath, ct);
                            }
                            // AVIF → GIF/WebP：动画轨道分离+alphamerge 需两步法
                            else if (inputExt == ".avif"
                                && (captured.Options.Format.Equals("gif", StringComparison.OrdinalIgnoreCase)
                                    || captured.Options.Format.Equals("webp", StringComparison.OrdinalIgnoreCase)))
                            {
                                await ProcessAvifToGifWebpAsync(captured, finalOutputPath, ct);
                            }
                            else if (inputExt == ".jxl")
                            {
                                // JXL 输入：智能检测类型并选择最优路径（独立于编码器选择）
                                await ProcessJxlInputAsync(captured, finalOutputPath, ct);
                            }
                            else if (backend == EncoderBackend.Cjxl)
                            {
                                await ProcessCjxlAsync(captured, finalOutputPath, ct);
                            }
                            else if (backend == EncoderBackend.Cjpegli)
                            {
                                await ProcessCjpegliAsync(captured, finalOutputPath, ct);
                            }
                            else
                            {
                                var args = FfmpegCommandBuilder.BuildArguments(captured.Options, captured.InputPath, finalOutputPath);
                                captured.Command = "ffmpeg " + args;
                                captured.Log += $"[cmd] ffmpeg {args}\n";
                                _onItemUpdated?.Invoke(captured);
                                var exitCode = await FfmpegRunner.RunAsync(args, s =>
                                {
                                    captured.Log += s;
                                    _onItemUpdated?.Invoke(captured);
                                }, AppSettingsService.Current.FfmpegPath);
                                captured.ExitCode = exitCode;
                                captured.Status = exitCode == 0 ? "已完成" : $"失败 (退出码 {exitCode})";

                                // AVIF 通过 FFmpeg -map_metadata 0 也不会保留元数据（FFmpeg AVIF muxer 限制），
                                // 需要 exiftool 单独恢复。其他格式走 -map_metadata 0 一般可保留。
                                if (exitCode == 0 && captured.Options.Format.Equals("avif", StringComparison.OrdinalIgnoreCase))
                                    await RestoreMetadataAsync(captured, finalOutputPath);
                            }

                            // ── ExifTool 后处理 ──
                            if (captured.ExitCode == 0
                                && captured.Options.MetadataMode == Models.MetadataMode.PreserveAll
                                && ExifToolService.NeedsProcessing(captured.Options))
                            {
                                if (ExifToolService.IsAvailable)
                                {
                                    try
                                    {
                                        captured.Log += "[exiftool] 开始隐私清理...\n";
                                        _onItemUpdated?.Invoke(captured);
                                        var exifExit = await ExifToolService.RunAsync(
                                            finalOutputPath,
                                            captured.Options,
                                            s =>
                                            {
                                                captured.Log += s;
                                                _onItemUpdated?.Invoke(captured);
                                            });
                                        if (exifExit == 0)
                                            captured.Log += "[exiftool] 隐私清理完成\n";
                                        else
                                            captured.Log += $"[exiftool] 警告: 退出码 {exifExit}\n";
                                    }
                                    catch (Exception ex)
                                    {
                                        captured.Log += $"[exiftool] 错误: {ex.Message}\n";
                                    }
                                }
                                else
                                {
                                    captured.Log += "[exiftool] 未检测到 exiftool，已跳过元数据隐私清理。请安装 exiftool 并在设置中配置路径以启用该功能。\n";
                                    _onItemUpdated?.Invoke(captured);
                                }
                            }
                            captured.CompletedAt = DateTimeOffset.UtcNow;
                        }
                        catch (OperationCanceledException)
                        {
                            captured.Status = "已取消";
                            captured.CompletedAt = DateTimeOffset.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            captured.Status = "失败: " + ex.Message;
                            captured.CompletedAt = DateTimeOffset.UtcNow;
                        }
                        finally
                        {
                            _onItemUpdated?.Invoke(captured);
                            sem.Release();
                        }
                    }, ct);

                    tasks.Add(t);
                }
                else
                {
                    // 空队列，短暂等待
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }

                // 等待正在运行的任务完成
                try { await Task.WhenAll(tasks.ToArray()); } catch { }
            }
            finally
            {
                try { _onQueueStopped?.Invoke(); } catch { }
            }
        }

        // ── 编码器后端专用处理方法 ──

        /// <summary>外部工具编码后恢复元数据（cjxl/cjpegli/djxl 不会自动保留元数据）</summary>
        private async Task RestoreMetadataAsync(QueueItem item, string outputPath)
        {
            if (item.Options.MetadataMode != Models.MetadataMode.PreserveAll)
                return;

            if (!ExifToolService.IsAvailable)
            {
                item.Log += "[metadata] ⚠️ 未检测到 exiftool，外部工具编码将丢失元数据。\n";
                item.Log += "[metadata] 请安装 exiftool 并确保其在 PATH 或工具目录中。\n";
                _onItemUpdated?.Invoke(item);
                return;
            }

            try
            {
                item.Log += "[exiftool] 从源文件恢复元数据...\n";
                _onItemUpdated?.Invoke(item);
                var copyExit = await ExifToolService.CopyMetadataAsync(
                    item.InputPath, outputPath,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                if (copyExit == 0)
                    item.Log += "[exiftool] 元数据恢复完成\n";
                else
                    item.Log += $"[exiftool] 元数据恢复警告: 退出码 {copyExit}\n";
            }
            catch (Exception ex)
            {
                item.Log += $"[exiftool] 元数据恢复错误: {ex.Message}\n";
            }
        }

        /// <summary>cjxl 编码（优先直接编码，失败自动转 PNG 再试）</summary>
        private async Task ProcessCjxlAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var isJpegInput = Path.GetExtension(item.InputPath).ToLowerInvariant() is ".jpg" or ".jpeg";

            // 第一步：直接尝试 cjxl
            item.Command = "cjxl " + CjxlService.BuildCjxlArguments(item.InputPath, outputPath, item.Options);
            var inputExtForCjxl = Path.GetExtension(item.InputPath).ToLowerInvariant();
            item.Log += $"[cjxl] 直接编码 (输入: {inputExtForCjxl}, 目标: jxl, effort={item.Options.JxlEffort ?? 7}, threads={item.Options.Threads})\n";
            _onItemUpdated?.Invoke(item);
            int exitCode;
            if (isJpegInput)
            {
                item.Log += "[cjxl] 检测到 JPEG 输入，启用无损重封装模式（-d 0 --lossless_jpeg=1，不解码 DCT 系数）\n";
                var jpegOpts = new Models.FfmpegOptions
                {
                    Quality = item.Options.Quality,
                    JxlEffort = item.Options.JxlEffort ?? 7,
                    CjxlProgressive = item.Options.CjxlProgressive,
                    CjxlPhotonNoiseIso = item.Options.CjxlPhotonNoiseIso,
                    Threads = item.Options.Threads
                };
                exitCode = await CjxlService.RunWithOptionsAsync(
                    item.InputPath, outputPath, jpegOpts,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
            }
            else
            {
                exitCode = await CjxlService.RunWithOptionsAsync(
                    item.InputPath, outputPath, item.Options,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
            }

            if (exitCode == 0)
            {
                item.ExitCode = 0;
                item.Status = isJpegInput ? "已完成 (cjxl 无损重封装)" : "已完成 (cjxl)";
                await RestoreMetadataAsync(item, outputPath);
                return;
            }

            // 动图不进行 PNG 中转（会丢失动画帧），回退到 FFmpeg 内置编码器
            if (IsAnimated(item))
            {
                item.Log += $"[cjxl] 动图/不支持格式（退出码 {exitCode}），回退到 FFmpeg libjxl_anim 编码器\n";
                _onItemUpdated?.Invoke(item);

                var ffmpegOpts = CloneOptionsForFfmpeg(item.Options);
                var args = FfmpegCommandBuilder.BuildArguments(ffmpegOpts, item.InputPath, outputPath);
                item.Command = "ffmpeg " + args;
                var ffExit = await FfmpegRunner.RunAsync(args, s =>
                {
                    item.Log += s;
                    _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath);
                item.ExitCode = ffExit;
                item.Status = ffExit == 0 ? "已完成 (cjxl→ffmpeg 回退)" : $"失败 (ffmpeg 退出码 {ffExit})";
                return;
            }

            // 第二步：直接编码失败，通过 ffmpeg 转 PNG 再试
            item.Log += $"[cjxl] 直接编码失败 (退出码 {exitCode})，输入格式可能不被 cjxl 直接支持，将通过 ffmpeg 转为 PNG 中间格式后重试\n";
            _onItemUpdated?.Invoke(item);

            var tmp = await PreConvertToPngAsync(item, ct);
            if (tmp == null) return; // 预转换已设置失败状态

            // 用 PNG 重新编码
            item.Log += "[cjxl] 用中间 PNG 重新编码...\n";
            _onItemUpdated?.Invoke(item);
            item.Command = "cjxl " + CjxlService.BuildCjxlArguments(tmp, outputPath, item.Options);
            exitCode = await CjxlService.RunWithOptionsAsync(
                tmp, outputPath, item.Options,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); });

            item.ExitCode = exitCode;
            item.Status = exitCode == 0 ? "已完成 (cjxl, PNG 中转)" : $"失败 (cjxl 退出码 {exitCode})";
            if (exitCode == 0) await RestoreMetadataAsync(item, outputPath);

            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        /// <summary>cjpegli 编码（优先直接编码，失败自动转 PNG 再试）</summary>
        private async Task ProcessCjpegliAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            // 第一步：直接尝试 cjpegli
            item.Command = "cjpegli " + CjpegliService.BuildCjpegliArguments(item.InputPath, outputPath, item.Options);
            item.Log += "[cjpegli] 直接编码...\n";
            _onItemUpdated?.Invoke(item);
            var exit = await CjpegliService.RunWithOptionsAsync(
                item.InputPath, outputPath, item.Options,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);

            if (exit == 0)
            {
                item.ExitCode = 0;
                item.Status = "已完成 (cjpegli)";
                await RestoreMetadataAsync(item, outputPath);
                return;
            }

            // 动图不进行 PNG 中转（会丢失动画帧），回退到 FFmpeg 内置编码器
            if (IsAnimated(item))
            {
                item.Log += $"[cjpegli] 动图/不支持格式（退出码 {exit}），回退到 FFmpeg mjpeg 编码器\n";
                _onItemUpdated?.Invoke(item);

                var ffmpegOpts = CloneOptionsForFfmpeg(item.Options);
                var args = FfmpegCommandBuilder.BuildArguments(ffmpegOpts, item.InputPath, outputPath);
                item.Command = "ffmpeg " + args;
                var ffExit = await FfmpegRunner.RunAsync(args, s =>
                {
                    item.Log += s;
                    _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath);
                item.ExitCode = ffExit;
                item.Status = ffExit == 0 ? "已完成 (cjpegli→ffmpeg 回退)" : $"失败 (ffmpeg 退出码 {ffExit})";
                return;
            }

            // 第二步：直接编码失败，通过 ffmpeg 转 PNG 再试
            var ext = Path.GetExtension(item.InputPath).ToLowerInvariant();
            item.Log += $"[cjpegli] 直接编码失败 (退出码 {exit})，{ext} 可能不被支持，转为 PNG 再试\n";
            _onItemUpdated?.Invoke(item);

            var tmp = await PreConvertToPngAsync(item, ct);
            if (tmp == null) return;

            item.Log += "[cjpegli] 用中间 PNG 重新编码...\n";
            _onItemUpdated?.Invoke(item);
            item.Command = "cjpegli " + CjpegliService.BuildCjpegliArguments(tmp, outputPath, item.Options);
            exit = await CjpegliService.RunWithOptionsAsync(
                tmp, outputPath, item.Options,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);

            item.ExitCode = exit;
            item.Status = exit == 0 ? "已完成 (cjpegli, PNG 中转)" : $"失败 (cjpegli 退出码 {exit})";
            if (exit == 0) await RestoreMetadataAsync(item, outputPath);

            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        /// <summary>用 ffmpeg 将输入转为临时高质量 PNG，返回路径（失败返回 null 并设置状态）</summary>
        private async Task<string?> PreConvertToPngAsync(QueueItem item, CancellationToken ct)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"ffmpeg_preconv_{Guid.NewGuid():N}.png");
            item.Log += "[preconv] 使用 ffmpeg 转为高质量 PNG 中间格式\n";
            item.Command = $"ffmpeg -y -i \"{item.InputPath}\" -compression_level 0 \"{tmp}\"";
            _onItemUpdated?.Invoke(item);

            var args = $"-y -i \"{item.InputPath}\" -compression_level 0 \"{tmp}\"";
            var exit = await FfmpegRunner.RunAsync(args,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                AppSettingsService.Current.FfmpegPath);

            if (exit != 0 || !File.Exists(tmp))
            {
                item.Log += $"[preconv] ffmpeg 转换失败 (退出码 {exit})\n";
                item.ExitCode = exit;
                item.Status = $"失败 (预转换退出码 {exit})";
                return null;
            }

            item.Log += "[preconv] 转换完成\n";
            return tmp;
        }

        /// <summary>AVIF → GIF/WebP：分轨提取颜色+alpha，再合并编码保留透明通道</summary>
        private async Task ProcessAvifToGifWebpAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var fmt = item.Options.Format.ToLowerInvariant();
            var tempDir = Path.Combine(Path.GetTempPath(), $"avif2gifwebp_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Step 1: 分轨提取颜色(0:2) + alpha(0:3) → 分别解码避免 alphamerge 压缩色彩
                item.Command = $"[avif→{fmt}] 两步法: 分轨提取颜色+alpha → alphamerge 合并编码";
                item.Log += $"[avif→{fmt}] Step1: 分轨提取颜色+alpha...\n";
                _onItemUpdated?.Invoke(item);

                var colorArgs = $"-y -i \"{item.InputPath}\" -map 0:2 -vsync 0 -frame_pts 1 \"{tempDir}\\c_%04d.png\"";
                var alphaArgs = $"-y -i \"{item.InputPath}\" -map 0:3 -vsync 0 -frame_pts 1 \"{tempDir}\\a_%04d.png\"";

                var colorExit = await FfmpegRunner.RunAsync(colorArgs, s => { item.Log += s; }, AppSettingsService.Current.FfmpegPath);
                if (colorExit != 0) { item.ExitCode = colorExit; item.Status = $"失败 (颜色流 {colorExit})"; return; }

                var hasAlpha = Directory.GetFiles(tempDir, "a_*.png").Length > 0;
                if (!hasAlpha) await FfmpegRunner.RunAsync(alphaArgs, s => { }, AppSettingsService.Current.FfmpegPath);
                hasAlpha = Directory.GetFiles(tempDir, "a_*.png").Length > 0;

                var colorFiles = Directory.GetFiles(tempDir, "c_*.png");
                item.Log += $"[avif→{fmt}]   颜色 {colorFiles.Length} 帧, alpha {(hasAlpha ? "有" : "无")}\n";

                var fps = item.Options.AnimationFps ?? 33;
                string encodeArgs;

                if (fmt == "gif")
                {
                    var dither = item.Options.GifDither ? "=dither=bayer:bayer_scale=5:diff_mode=rectangle" : "";
                    if (hasAlpha)
                    {
                        // 单 pass: color(0:v) + alpha(1:v) → alphamerge → palettegen → paletteuse
                        encodeArgs = $"-y -framerate {fps} -i \"{tempDir}\\c_%04d.png\" -framerate {fps} -i \"{tempDir}\\a_%04d.png\" -filter_complex \"[0:v][1:v]alphamerge,split[a][b];[a]palettegen=reserve_transparent=1:stats_mode=full:max_colors=256[p];[b][p]paletteuse{dither}\" -loop 0 \"{outputPath}\"";
                    }
                    else
                    {
                        encodeArgs = $"-y -framerate {fps} -i \"{tempDir}\\c_%04d.png\" -vf \"split[a][b];[a]palettegen=stats_mode=full:max_colors=256[p];[b][p]paletteuse{dither}\" -loop 0 \"{outputPath}\"";
                    }
                }
                else // webp
                {
                    var quality = item.Options.Quality;
                    var loop = item.Options.AnimationLoop;
                    if (loop < 0) loop = 0;
                    if (hasAlpha)
                    {
                        // 颜色 + alpha 分离帧 → alphamerge 合并 → yuva420p → libwebp_anim
                        encodeArgs = $"-y -framerate {fps} -i \"{tempDir}\\c_%04d.png\" -framerate {fps} -i \"{tempDir}\\a_%04d.png\" -filter_complex \"[0:v][1:v]alphamerge,format=yuva420p\" -c:v libwebp_anim -q:v {quality} -loop {loop} \"{outputPath}\"";
                    }
                    else
                    {
                        encodeArgs = $"-y -framerate {fps} -i \"{tempDir}\\c_%04d.png\" -c:v libwebp_anim -q:v {quality} -loop {loop} \"{outputPath}\"";
                    }
                }

                item.Log += $"[avif→{fmt}] Step2: 编码...\n";
                _onItemUpdated?.Invoke(item);

                var encodeExit = await FfmpegRunner.RunAsync(encodeArgs, s =>
                {
                    item.Log += s; _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath);

                item.ExitCode = encodeExit;
                item.Status = encodeExit == 0 ? $"已完成 (avif→{fmt})" : $"失败 (退出码 {encodeExit})";

                if (encodeExit == 0)
                    await RestoreMetadataAsync(item, outputPath);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>GIF → AVIF 两步法：先提取带透明通道的 PNG 帧，再用 avifenc 编码</summary>
        private async Task ProcessGifToAvifViaAvifencAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            // 优先手动路径，回退 ffmpeg 同目录
            var avifencPath = AppSettingsService.Current.AvifencPath;
            if (string.IsNullOrWhiteSpace(avifencPath) || !File.Exists(avifencPath))
                avifencPath = Path.Combine(AppSettingsService.Current.FfmpegDir ?? "", "avifenc.exe");
            if (!File.Exists(avifencPath))
            {
                item.Log += "[gif→avif] avifenc.exe 未找到，回退到 FFmpeg 单命令\n";
                _onItemUpdated?.Invoke(item);
                // fall through to normal FFmpeg path
                var fallbackArgs = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                item.Command = "ffmpeg " + fallbackArgs;
                var fallbackExit = await FfmpegRunner.RunAsync(fallbackArgs, s =>
                {
                    item.Log += s; _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath);
                item.ExitCode = fallbackExit;
                item.Status = fallbackExit == 0 ? "已完成 (ffmpeg 回退)" : $"失败 (退出码 {fallbackExit})";
                return;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"avifenc_frames_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            item.Command = "[gif→avif] 两步法: ffmpeg 提取RGBA帧 → avifenc 编码动图AVIF";

            try
            {
                // Step 1: 提取 GIF 帧为带 alpha 的 PNG
                item.Log += "[gif→avif] Step1: 提取 GIF 帧为 RGBA PNG...\n";
                _onItemUpdated?.Invoke(item);

                var extractArgs = $"-y -i \"{item.InputPath}\" -pix_fmt rgba -vsync 0 \"{tempDir}\\f_%04d.png\"";
                var extractExit = await FfmpegRunner.RunAsync(extractArgs, s =>
                {
                    item.Log += s;
                    _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath);

                if (extractExit != 0)
                {
                    item.ExitCode = extractExit;
                    item.Status = $"失败 (帧提取退出码 {extractExit})";
                    return;
                }

                var pngFiles = Directory.GetFiles(tempDir, "*.png").OrderBy(f => f).ToList();
                if (pngFiles.Count == 0)
                {
                    item.ExitCode = -1;
                    item.Status = "失败 (未提取到任何帧)";
                    return;
                }

                item.Log += $"[gif→avif]   提取 {pngFiles.Count} 帧\n";

                // Step 2: avifenc 编码为动图 AVIF
                item.Log += "[gif→avif] Step2: avifenc 编码...\n";
                _onItemUpdated?.Invoke(item);

                var fps = item.Options.AnimationFps ?? 33; // 默认 33fps（接近原始 GIF）
                var quality = Math.Max(20, Math.Min(100, item.Options.Quality)); // avifenc -q 范围 0-100
                var speed = Math.Clamp(item.Options.AvifCpuUsed ?? 5, 0, 10);
                var threads = Math.Max(1, item.Options.Threads);

                var avifencArgs = new StringBuilder();
                avifencArgs.Append($"-q {quality} ");
                avifencArgs.Append($"-s {speed} ");
                avifencArgs.Append($"-j {threads} ");
                foreach (var f in pngFiles)
                    avifencArgs.Append($"\"{f}\" ");
                avifencArgs.Append($"-o \"{outputPath}\" ");
                avifencArgs.Append($"--fps {fps} ");
                avifencArgs.Append($"--repetition-count infinite");

                item.Log += $"[gif→avif]   avifenc {avifencArgs}\n";

                var psi = new ProcessStartInfo
                {
                    FileName = avifencPath,
                    Arguments = avifencArgs.ToString(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) { item.Log += e.Data + "\n"; _onItemUpdated?.Invoke(item); }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) { item.Log += e.Data + "\n"; _onItemUpdated?.Invoke(item); }
                };

                try
                {
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    item.Status = "已取消";
                    return;
                }

                item.ExitCode = process.ExitCode;
                item.Status = process.ExitCode == 0 ? "已完成 (avifenc 两步法)" : $"失败 (avifenc 退出码 {process.ExitCode})";

                if (process.ExitCode == 0)
                    await RestoreMetadataAsync(item, outputPath);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>JXL 输入智能处理（DJXL 重构 / djxl→cjpegli 管道 / ffmpeg 回退）</summary>
        private async Task ProcessJxlInputAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var targetFmt = (item.Options.Format ?? "").ToLowerInvariant();
            var jxlType = JxlInspector.DetectType(item.InputPath);
            var jxlTypeName = jxlType switch
            {
                JxlImageType.JpegReconstruction => "JPEG 重构（可从 JXL 无损还原原始 JPEG）",
                JxlImageType.NativeCodestream => "原生 JXL 码流（需解码为像素再编码）",
                _ => "未知/其他"
            };
            item.Log += $"[jxl] 输入类型: {jxlTypeName}\n";
            item.Log += $"[jxl] 目标格式: {targetFmt}  |  djxl: {(DjxlService.IsAvailable ? "可用" : "不可用")}  |  cjpegli: {(CjpegliService.IsAvailable ? "可用" : "不可用")}  |  cjxl: {(CjxlService.IsAvailable ? "可用" : "不可用")}\n";

            if (jxlType == JxlImageType.JpegReconstruction)
            {
                if (DjxlService.IsAvailable)
                {
                    item.Log += "[jxl] 使用 djxl 从 JXL 还原原始 JPEG（无损，不解码像素）\n";
                    item.Command = $"djxl \"{item.InputPath}\" \"{outputPath}\"";
                    var exit = await DjxlService.RunAsync(item.InputPath, outputPath, item.Options.Threads,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                    item.ExitCode = exit;
                    item.Status = exit == 0 ? "已完成 (djxl 重构 JPEG)" : $"失败 (djxl 退出码 {exit})";
                    if (exit == 0) await RestoreMetadataAsync(item, outputPath);
                }
                else
                {
                    item.Log += "[djxl] 未检测到 djxl，回退到 ffmpeg\n";
                    var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                    item.Command = "ffmpeg " + args;
                    var exit = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath);
                    item.ExitCode = exit;
                    item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                }
            }
            else if (jxlType == JxlImageType.NativeCodestream)
            {
                // 动图 JXL 不解码为 PNG 中间格式（会丢失动画帧），直接用 ffmpeg 处理
                if (IsAnimated(item))
                {
                    item.Log += "[jxl] 动图 JXL 不进行 djxl→PNG 中间解码，回退到 ffmpeg\n";
                    _onItemUpdated?.Invoke(item);
                    var ffmpegOpts = CloneOptionsForFfmpeg(item.Options);
                    var args2 = FfmpegCommandBuilder.BuildArguments(ffmpegOpts, item.InputPath, outputPath);
                    item.Command = "ffmpeg " + args2;
                    var exit2 = await FfmpegRunner.RunAsync(args2,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath);
                    item.ExitCode = exit2;
                    item.Status = exit2 == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit2})";
                    return;
                }

                if (DjxlService.IsAvailable)
                {
                    // ── 判断是否需要 PNG 中转 ──
                    // 仅在以下情况使用 PNG 中间文件（外部编码器不支持 stdin）：
                    //   ① 输出 JPEG/JPEGLI 且 cjpegli 可用 → 管道优先，失败回退 PNG 中转
                    //   ② 输出 JXL 且 cjxl 可用 → cjxl 不支持 stdin，需 PNG 中转
                    // 其他所有输出格式 → djxl 直接管道传给 ffmpeg
                    var usePngIntermediary =
                        (CjpegliService.IsAvailable && (targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli"))
                        || (CjxlService.IsAvailable && targetFmt == "jxl");

                    if (usePngIntermediary)
                    {
                        // ── JPEG/JPEGLI 目标：优先使用 djxl→cjpegli 管道 ──
                        if (CjpegliService.IsAvailable && (targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli"))
                        {
                            item.Log += "[pipeline] 尝试 djxl -> cjpegli 管道\n";
                            var pipeExit = await JxlPipelineService.TryPipeDjxlToCjpegliAsync(
                                item.InputPath, outputPath, item.Options.Quality, item.Options.Threads,
                                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                            if (pipeExit == 0)
                            {
                                item.ExitCode = 0;
                                item.Status = "已完成 (cjpegli 管道)";
                                await RestoreMetadataAsync(item, outputPath);
                                return;
                            }
                            else
                            {
                                item.Log += "[pipeline] 管道失败，回退到临时文件\n";
                            }
                        }

                        // ── 回退/直接：PNG 中转（cjxl/cjpegli 需要文件输入）──
                        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".png");
                        var tmpCreated = false;
                        try
                        {
                            tmpCreated = await FallbackDjxlDecodeToFile(item, tmp, outputPath, ct);
                        }
                        finally
                        {
                            try { if (tmpCreated && File.Exists(tmp)) File.Delete(tmp); } catch { }
                        }
                    }
                    else
                    {
                        // ── 其他格式：djxl 管道 → ffmpeg ──
                        item.Command = $"djxl \"{item.InputPath}\" --output_format=png - | ffmpeg {BuildFfmpegPipeArguments(item.Options, outputPath)}";
                        await PipeDjxlToFfmpegAsync(item, outputPath, ct);
                    }
                }
                else
                {
                    item.Log += "[djxl] 未检测到 djxl，使用 ffmpeg 直接处理 JXL\n";
                    var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                    item.Command = "ffmpeg " + args;
                    var exit = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath);
                    item.ExitCode = exit;
                    item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                }
            }
            else
            {
                var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                var exit = await FfmpegRunner.RunAsync(args,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                    AppSettingsService.Current.FfmpegPath);
                item.ExitCode = exit;
                item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
            }
        }

        private async Task<bool> FallbackDjxlDecodeToFile(QueueItem item, string tmp, string outputPath, CancellationToken ct)
        {
            // 动图不解码为 PNG 中间格式
            if (IsAnimated(item))
            {
                item.Log += "[djxl] 动图不进行 PNG 中间解码，回退失败\n";
                item.ExitCode = -1;
                item.Status = "失败 (动图不支持 PNG 中转)";
                return false;
            }

            item.Log += "[djxl] 解码 JXL 为中间 PNG\n";
            var exit = await DjxlService.RunAsync(item.InputPath, tmp, item.Options.Threads,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            if (exit == 0 && File.Exists(tmp))
            {
                var targetFmt = (item.Options.Format ?? "").ToLowerInvariant();

                // 根据输出格式选择外部编码器
                if (targetFmt == "jxl" && CjxlService.IsAvailable)
                {
                    item.Log += "[cjxl] 对中间 PNG 进行编码\n";
                    var cjxlExit = await CjxlService.RunWithOptionsAsync(tmp, outputPath, item.Options,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                    item.ExitCode = cjxlExit;
                    item.Status = cjxlExit == 0 ? "已完成 (cjxl, PNG 中转)" : $"失败 (cjxl 退出码 {cjxlExit})";
                    if (cjxlExit == 0) await RestoreMetadataAsync(item, outputPath);
                    return true;
                }
                else if ((targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli") && CjpegliService.IsAvailable)
                {
                    item.Log += "[cjpegli] 对中间文件进行编码\n";
                    var cjexit = await CjpegliService.RunWithOptionsAsync(tmp, outputPath, item.Options,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                    item.ExitCode = cjexit;
                    item.Status = cjexit == 0 ? "已完成 (cjpegli 编码)" : $"失败 (cjpegli 退出码 {cjexit})";
                    if (cjexit == 0) await RestoreMetadataAsync(item, outputPath);
                    return true;
                }
                else
                {
                    item.Log += "[ffmpeg] 对中间文件编码\n";
                    var args = FfmpegCommandBuilder.BuildArguments(item.Options, tmp, outputPath);
                    item.Command = "ffmpeg " + args;
                    var exit2 = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath);
                    item.ExitCode = exit2;
                    item.Status = exit2 == 0 ? "已完成 (ffmpeg)" : $"失败 (ffmpeg 退出码 {exit2})";
                    // ffmpeg with -map_metadata 0 preserves metadata from the input file,
                    // but here the input is tmp PNG which has no metadata, so we still need restore.
                    if (exit2 == 0) await RestoreMetadataAsync(item, outputPath);
                    return true;
                }
            }
            else
            {
                item.ExitCode = exit;
                item.Status = $"失败 (djxl 解码退出码 {exit})";
                return false;
            }
        }

        /// <summary>
        /// 将 djxl 解码输出通过管道直接传给 ffmpeg 编码，避免中间 PNG 临时文件。
        /// 适用于 JXL → PNG/WebP/AVIF/TIFF/GIF 等所有非 JPEG/JXL 目标格式。
        /// </summary>
        private async Task PipeDjxlToFfmpegAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var djxl = DjxlService.DetectedPath;
            if (string.IsNullOrEmpty(djxl))
            {
                item.Log += "[pipe] djxl 未找到，回退到 ffmpeg 直接处理\n";
                var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                var exit = await FfmpegRunner.RunAsync(args,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                    AppSettingsService.Current.FfmpegPath);
                item.ExitCode = exit;
                item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                return;
            }

            item.Log += "[pipe] 尝试 djxl → ffmpeg 管道\n";
            _onItemUpdated?.Invoke(item);

            var ffmpegArgs = BuildFfmpegPipeArguments(item.Options, outputPath);

            Process? procDj = null;
            Process? procFf = null;
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var linkedToken = linked.Token;

                var ffmpegPath = AppSettingsService.Current.FfmpegPath;

                // djxl 解码 JXL → PNG 流输出到 stdout
                var djArgs = $"\"{item.InputPath}\" --output_format=png -";
                var psiDj = new ProcessStartInfo
                {
                    FileName = djxl,
                    Arguments = djArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // ffmpeg 从 stdin 读取 PNG 流
                var psiFf = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = ffmpegArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                procDj = Process.Start(psiDj);
                if (procDj == null)
                {
                    item.Log += "[pipe] 启动 djxl 失败\n";
                    item.ExitCode = -1;
                    item.Status = "失败 (djxl 启动失败)";
                    return;
                }

                procFf = Process.Start(psiFf);
                if (procFf == null)
                {
                    item.Log += "[pipe] 启动 ffmpeg 失败\n";
                    item.ExitCode = -1;
                    item.Status = "失败 (ffmpeg 启动失败)";
                    return;
                }

                // 启动 stderr/stdout 消费者（非阻塞，并行运行）
                var djErrTask = ConsumeStreamLinesAsync(procDj.StandardError, s => item.Log += $"[djxl] {s}\n");
                var ffLogTask = ConsumeStreamLinesAsync(procFf.StandardError, s =>
                {
                    item.Log += s + "\n";
                    _onItemUpdated?.Invoke(item);
                });
                var ffOutTask = ConsumeStreamLinesAsync(procFf.StandardOutput, s => item.Log += s + "\n");

                // ── 关键：先完成传输 + 关闭 stdin，再等待进程退出（避免死锁）──
                try
                {
                    await procDj.StandardOutput.BaseStream.CopyToAsync(procFf.StandardInput.BaseStream, linkedToken);
                }
                catch (OperationCanceledException)
                {
                    item.Log += "[pipe] 传输被取消\n";
                }
                catch (Exception ex)
                {
                    item.Log += $"[pipe] 传输错误: {ex.Message}\n";
                }

                // 关闭 ffmpeg stdin 以发送 EOF，让 ffmpeg 完成编码
                try { procFf.StandardInput.Close(); } catch { }

                // 等待 ffmpeg 正常退出（stdin 已关闭，ffmpeg 会自行结束）
                try { await procFf.WaitForExitAsync(linkedToken); } catch (OperationCanceledException) { }
                // djxl 应该已经退出（CopyToAsync 完成后 stdout 已读完）
                try { await procDj.WaitForExitAsync(CancellationToken.None); } catch { }

                // 等待日志消费者完成
                await djErrTask;
                await ffLogTask;
                await ffOutTask;

                var ffExitCode = procFf.HasExited ? procFf.ExitCode : -1;
                item.ExitCode = ffExitCode;
                item.Status = ffExitCode == 0 ? "已完成 (djxl→ffmpeg 管道)" : $"失败 (ffmpeg 退出码 {ffExitCode})";
                if (ffExitCode == 0) await RestoreMetadataAsync(item, outputPath);
            }
            catch (OperationCanceledException)
            {
                item.Status = "已取消";
            }
            catch (Exception ex)
            {
                item.Log += $"[pipe] 异常: {ex.Message}\n";
                item.ExitCode = -1;
                item.Status = $"失败 (管道异常: {ex.Message})";
            }
            finally
            {
                // 确保进程被清理
                if (procFf != null && !procFf.HasExited)
                {
                    try { procFf.Kill(entireProcessTree: true); } catch { }
                }
                if (procDj != null && !procDj.HasExited)
                {
                    try { procDj.Kill(entireProcessTree: true); } catch { }
                }
                try { procDj?.Dispose(); } catch { }
                try { procFf?.Dispose(); } catch { }
            }
        }

        /// <summary>构建 ffmpeg 从 stdin 读取 PNG 流的命令行参数</summary>
        private static string BuildFfmpegPipeArguments(Models.FfmpegOptions options, string outputPath)
        {
            // 以 stdin (-) 为输入，用 image2pipe 格式指定 PNG 流
            var args = FfmpegCommandBuilder.BuildArguments(options, "-", outputPath);
            // 插入 -f image2pipe 到 -i - 之前
            var idx = args.IndexOf("-i \"-\"", StringComparison.Ordinal);
            if (idx >= 0)
            {
                args = args.Substring(0, idx) + "-f image2pipe " + args.Substring(idx);
            }
            else
            {
                // 如果 BuildArguments 生成的格式不同，兼容处理
                idx = args.IndexOf("-i -", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    args = args.Substring(0, idx) + "-f image2pipe " + args.Substring(idx);
                }
            }
            return args;
        }

        private static async Task ConsumeStreamLinesAsync(StreamReader reader, Action<string> onLine)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    onLine(line);
                }
            }
            catch { }
        }

        /// <summary>动图回退：将外部工具参数克隆为 FFmpeg 内置编码器参数</summary>
        private static Models.FfmpegOptions CloneOptionsForFfmpeg(Models.FfmpegOptions original)
        {
            var fmt = (original.Format ?? "").ToLowerInvariant();
            string encoder = fmt switch
            {
                "avif" => "libsvtav1",  // SVT-AV1 对 yuva420p 支持比 libaom-av1 更可靠
                "jxl" => "libjxl_anim",
                "jpg" or "jpeg" or "jpegli" => "mjpeg",
                "png" or "apng" => "apng",
                "webp" => "libwebp_anim",
                _ => original.Encoder ?? ""
            };

            return new Models.FfmpegOptions
            {
                Format = original.Format ?? "avif",
                Quality = original.Quality,
                Chroma = original.Chroma,
                BitDepth = original.BitDepth,
                ColorSpace = original.ColorSpace,
                UseAdvancedColorParameters = original.UseAdvancedColorParameters,
                ColorPrimaries = original.ColorPrimaries,
                ColorTrc = original.ColorTrc,
                ColorMatrix = original.ColorMatrix,
                Threads = original.Threads,
                MetadataMode = original.MetadataMode,
                Encoder = encoder,
                EncoderBackend = EncoderBackend.Ffmpeg,
                Lossless = original.Lossless,
                // 动图参数
                AnimationFps = original.AnimationFps,
                AnimationLoop = original.AnimationLoop,
                GifPaletteOptimize = original.GifPaletteOptimize,
                GifDither = original.GifDither,
                AnimationScaleW = original.AnimationScaleW,
                // AVIF 动图强制 still-picture=0
                AvifStillPicture = false,
                AvifCpuUsed = original.AvifCpuUsed,
                AvifRowMt = original.AvifRowMt,
                AvifTune = original.AvifTune,
                AvifPreset = original.AvifPreset,
                // JXL
                JxlEffort = original.JxlEffort,
                JxlModular = original.JxlModular,
                // 编码器私有选项
                PngPred = original.PngPred,
                WebpPreset = original.WebpPreset,
                WebpCompressionLevel = original.WebpCompressionLevel,
                JpegHuffman = original.JpegHuffman,
                JpegDct = original.JpegDct,
                JpegGainMap = original.JpegGainMap,
                JpegGainMapQuality = original.JpegGainMapQuality,
                JpegGainMapTargetNits = original.JpegGainMapTargetNits,
                TiffCompressionAlgo = original.TiffCompressionAlgo,
                // ExifTool
                StripExifGps = original.StripExifGps,
                StripExifTime = original.StripExifTime,
                StripExifCamera = original.StripExifCamera,
                StripExifAll = original.StripExifAll,
                StripXmp = original.StripXmp,
                // Cjxl/Cjpegli 选项（回退时忽略，FFmpeg 不使用）
                CjxlProgressive = false,
                CjxlPhotonNoiseIso = 0,
                CjpegliChromaSubsampling = "auto",
                CjpegliProgressiveId = -1,
                CjpegliOptimize = true,
                CjpegliAdaptiveQuant = true,
                CjpegliEncoderBackend = "libjpeg",
                CjpegliPsnrTarget = 0,
                CjpegliMultiThreadAvailable = false,
            };
        }

        /// <summary>判断 avifenc.exe 是否可用（手动路径 > ffmpeg 同目录）</summary>
        private static bool HasAvifencAvailable()
        {
            var manualPath = AppSettingsService.Current.AvifencPath;
            if (!string.IsNullOrWhiteSpace(manualPath) && File.Exists(manualPath))
                return true;
            return File.Exists(Path.Combine(AppSettingsService.Current.FfmpegDir ?? "", "avifenc.exe"));
        }

        /// <summary>判断队列项是否为动图（输出为GIF/APNG、设有帧率、或输入为动图文件）</summary>
        private static bool IsAnimated(QueueItem item)
        {
            var fmt = (item.Options.Format ?? "").ToLowerInvariant();
            // 输出格式为 GIF / APNG → 始终是动图
            if (fmt is "gif" or "apng") return true;
            // 用户设置了帧率 → 动图模式
            if (item.Options.AnimationFps.HasValue) return true;
            // 输入文件是动图格式（.gif / .apng / .avif 可能含动画）
            var inputExt = Path.GetExtension(item.InputPath).ToLowerInvariant();
            if (inputExt is ".gif" or ".apng") return true;
            return false;
        }
    }
}
