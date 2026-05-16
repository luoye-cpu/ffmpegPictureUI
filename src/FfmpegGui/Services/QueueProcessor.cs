using FfmpegGui.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    public class QueueProcessor
    {
        private readonly ConcurrentQueue<QueueItem> _queue = new();
        private readonly List<Task> _running = new();
        private CancellationTokenSource? _cts;
        private int _concurrency = 2;
        private readonly Action<QueueItem> _onItemUpdated;

        public QueueProcessor(Action<QueueItem> onItemUpdated)
        {
            _onItemUpdated = onItemUpdated;
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
            _cts = new CancellationTokenSource();
            Task.Run(() => ProcessAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
        }

        private async Task ProcessAsync(CancellationToken ct)
        {
            var sem = new SemaphoreSlim(_concurrency);
            var tasks = new List<Task>();
            while (!ct.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var item))
                {
                    // 跳过已取消的任务
                    if (item.IsCancelled)
                    {
                        item.Status = "已删除";
                        _onItemUpdated?.Invoke(item);
                        continue;
                    }

                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    var t = Task.Run(async () =>
                    {
                        try
                        {
                            item.Status = "处理中";
                            _onItemUpdated?.Invoke(item);

                            // 确保输出目录存在
                            var outDir = Path.GetDirectoryName(item.OutputPath);
                            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                                Directory.CreateDirectory(outDir);

                            var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, item.OutputPath);
                            var exitCode = await FfmpegRunner.RunAsync(args, s =>
                            {
                                item.Log += s;
                                _onItemUpdated?.Invoke(item);
                            }, AppSettingsService.Current.FfmpegPath);

                            item.ExitCode = exitCode;
                            item.Status = exitCode == 0 ? "已完成" : $"失败 (退出码 {exitCode})";
                        }
                        catch (OperationCanceledException)
                        {
                            item.Status = "已取消";
                        }
                        catch (Exception ex)
                        {
                            item.Status = "失败: " + ex.Message;
                        }
                        finally
                        {
                            _onItemUpdated?.Invoke(item);
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
    }
}
