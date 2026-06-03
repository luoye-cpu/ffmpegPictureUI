using FfmpegGui.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

                            if (inputExt == ".jxl")
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
                                var exitCode = await FfmpegRunner.RunAsync(args, s =>
                                {
                                    captured.Log += s;
                                    _onItemUpdated?.Invoke(captured);
                                }, AppSettingsService.Current.FfmpegPath);
                                captured.ExitCode = exitCode;
                                captured.Status = exitCode == 0 ? "已完成" : $"失败 (退出码 {exitCode})";
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
                        }
                        catch (OperationCanceledException)
                        {
                            captured.Status = "已取消";
                        }
                        catch (Exception ex)
                        {
                            captured.Status = "失败: " + ex.Message;
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

        /// <summary>cjxl 编码（普通图片→JXL 或 JPEG→JXL 无损重封装）</summary>
        private async Task ProcessCjxlAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var isJpegInput = Path.GetExtension(item.InputPath).ToLowerInvariant() is ".jpg" or ".jpeg";
            var effort = item.Options.JxlEffort ?? 7;

            if (isJpegInput)
            {
                item.Log += "[cjxl] JPEG → JXL 无损重封装（不解码，速度 5-10×）\n";
                var exitCode = await CjxlService.RunWithOptionsAsync(
                    item.InputPath, outputPath, item.Options,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                item.ExitCode = exitCode;
                item.Status = exitCode == 0 ? "已完成 (cjxl 无损重封装)" : $"失败 (cjxl 退出码 {exitCode})";
            }
            else
            {
                item.Log += "[cjxl] 普通图片 → JXL 编码\n";
                var exitCode = await CjxlService.RunWithOptionsAsync(
                    item.InputPath, outputPath, item.Options,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                item.ExitCode = exitCode;
                item.Status = exitCode == 0 ? "已完成 (cjxl)" : $"失败 (cjxl 退出码 {exitCode})";
            }
        }

        /// <summary>cjpegli 编码</summary>
        private async Task ProcessCjpegliAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            item.Log += "[cjpegli] 使用 cjpegli 进行编码\n";
            _onItemUpdated?.Invoke(item);
            var exit = await CjpegliService.RunWithOptionsAsync(
                item.InputPath, outputPath, item.Options,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            item.ExitCode = exit;
            item.Status = exit == 0 ? "已完成 (cjpegli)" : $"失败 (cjpegli 退出码 {exit})";
        }

        /// <summary>JXL 输入智能处理（DJXL 重构 / djxl→cjpegli 管道 / ffmpeg 回退）</summary>
        private async Task ProcessJxlInputAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var targetFmt = (item.Options.Format ?? "").ToLowerInvariant();
            item.Log += "[jxl] 检测 JXL 类型并选择最优转换流程...\n";
            var jxlType = JxlInspector.DetectType(item.InputPath);

            if (jxlType == JxlImageType.JpegReconstruction)
            {
                if (DjxlService.IsAvailable)
                {
                    item.Log += "[djxl] 尝试还原原始 JPEG（无损重建）\n";
                    var exit = await DjxlService.RunAsync(item.InputPath, outputPath, item.Options.Threads,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                    item.ExitCode = exit;
                    item.Status = exit == 0 ? "已完成 (djxl 重构 JPEG)" : $"失败 (djxl 退出码 {exit})";
                }
                else
                {
                    item.Log += "[djxl] 未检测到 djxl，回退到 ffmpeg\n";
                    var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                    var exit = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath);
                    item.ExitCode = exit;
                    item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                }
            }
            else if (jxlType == JxlImageType.NativeCodestream)
            {
                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".png");
                var tmpCreated = false;
                try
                {
                    if (DjxlService.IsAvailable)
                    {
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
                            }
                            else
                            {
                                item.Log += "[pipeline] 管道失败，回退到临时文件\n";
                                tmpCreated = await FallbackDjxlDecodeToFile(item, tmp, outputPath, ct);
                            }
                        }
                        else
                        {
                            tmpCreated = await FallbackDjxlDecodeToFile(item, tmp, outputPath, ct);
                        }
                    }
                    else
                    {
                        item.Log += "[djxl] 未检测到 djxl，使用 ffmpeg 直接处理 JXL\n";
                        var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                        var exit = await FfmpegRunner.RunAsync(args,
                            s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                            AppSettingsService.Current.FfmpegPath);
                        item.ExitCode = exit;
                        item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                    }
                }
                finally
                {
                    try { if (tmpCreated && File.Exists(tmp)) File.Delete(tmp); } catch { }
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
            item.Log += "[djxl] 解码 JXL 为中间 PNG\n";
            var exit = await DjxlService.RunAsync(item.InputPath, tmp, item.Options.Threads,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            if (exit == 0 && File.Exists(tmp))
            {
                if (CjpegliService.IsAvailable)
                {
                    item.Log += "[cjpegli] 对中间文件进行编码\n";
                    var cjexit = await CjpegliService.RunWithOptionsAsync(tmp, outputPath, item.Options,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                    item.ExitCode = cjexit;
                    item.Status = cjexit == 0 ? "已完成 (cjpegli 编码)" : $"失败 (cjpegli 退出码 {cjexit})";
                    return true;
                }
                else
                {
                    item.Log += "[ffmpeg] 对中间文件编码\n";
                    var args = FfmpegCommandBuilder.BuildArguments(item.Options, tmp, outputPath);
                    var exit2 = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath);
                    item.ExitCode = exit2;
                    item.Status = exit2 == 0 ? "已完成 (ffmpeg)" : $"失败 (ffmpeg 退出码 {exit2})";
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
    }
}
