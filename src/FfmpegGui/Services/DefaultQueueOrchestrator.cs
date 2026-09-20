using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FfmpegGui.Models;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 默认队列编排器实现 - 包装 QueueProcessor 并暴露 IQueueOrchestrator 接口
    /// </summary>
    internal sealed class DefaultQueueOrchestrator : IQueueOrchestrator
    {
        private readonly QueueProcessor _processor;
        private TaskCompletionSource<bool>? _completionTcs;
        private readonly object _tcsLock = new();

        /// <summary>已完成项计数（供 OnItemCompleted 判断终态去重）</summary>
        private readonly HashSet<QueueItem> _completedItems = new();

        public DefaultQueueOrchestrator()
        {
            _processor = new QueueProcessor(
                onItemUpdated: item =>
                {
                    ItemProgress?.Invoke(item);

                    // 当项达到终态时触发 ItemCompleted（去重，避免同一项重复回调）
                    if (item.IsCompletedOrTerminal())
                    {
                        bool newlyCompleted;
                        lock (_completedItems)
                        {
                            newlyCompleted = _completedItems.Add(item);
                        }
                        if (newlyCompleted)
                            ItemCompleted?.Invoke(item);
                    }
                },
                onStopped: () =>
                {
                    QueueCompleted?.Invoke();
                    lock (_tcsLock)
                    {
                        try { _completionTcs?.TrySetResult(true); } catch { }
                    }
                }
            );
        }

        public bool IsRunning => _processor.IsRunning;

        public event Action<QueueItem>? ItemProgress;
        public event Action<QueueItem>? ItemCompleted;
        public event Action? QueueCompleted;

        public Task StartAsync(IReadOnlyList<QueueItem> items, int concurrency = 2, CancellationToken ct = default)
        {
            lock (_tcsLock)
            {
                _completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            lock (_completedItems) { _completedItems.Clear(); }

            foreach (var item in items)
            {
                _processor.Add(item);
            }

            _processor.Start(concurrency);

            // ✨ 关键：headless 模式下，入队完成后请求"当前队列处理完即优雅停止"
            // 这样当队列空且所有任务完成时，ProcessAsync 会自然退出并触发 onStopped
            _processor.StopAfterCurrentQueue();

            // 支持外部取消 → 立即停止
            if (ct.CanBeCanceled)
            {
                ct.Register(() =>
                {
                    try { _processor.Stop(); } catch { }
                });
            }

            return Task.CompletedTask; // QueueProcessor 内部异步运行，不在此等待
        }

        /// <summary>等待队列处理完成（StartAsync 后调用）</summary>
        public async Task WaitForCompletionAsync(CancellationToken ct = default)
        {
            TaskCompletionSource<bool>? tcs;
            lock (_tcsLock) tcs = _completionTcs;
            if (tcs == null) return; // 从未启动
            if (ct.CanBeCanceled)
                ct.Register(() => { try { tcs.TrySetResult(false); } catch { } });

            // 🛡️ 兜底看门狗判的是「停滞」而非「整队预算」：连续 MaxStallWindows 个窗口内
            // 没有任何项目进入终态才停。此处曾经是 `TimeSpan.FromSeconds(120)` 的**整队**硬超时，
            // 于是批量转换（总耗时 >120s 是常态）必然在跑到一半时 Stop() + 抛 TimeoutException，
            // 把已经成功的项一起判为失败（headless 退出码 1）。
            const int StallWindowSeconds = 10 * 60;
            const int MaxStallWindows = 3;

            var stalled = 0;
            int lastCompleted;
            lock (_completedItems) lastCompleted = _completedItems.Count;

            while (true)
            {
                var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(StallWindowSeconds)));
                if (ReferenceEquals(finished, tcs.Task)) break;

                int now;
                lock (_completedItems) now = _completedItems.Count;
                if (now != lastCompleted)
                {
                    lastCompleted = now;
                    stalled = 0;
                    continue;
                }
                if (++stalled >= MaxStallWindows)
                {
                    try { _processor.Stop(); } catch { }
                    // Stop() 只是发取消；半成品产物的清理、临时目录的删除都在各项的 finally 里，
                    // 进程随即退出就跑不到（实测：仍留下 1 个 0 字节产物）。
                    // ⚠ 不能用 _processor.IsRunning 当判据——消费循环一退出它就是 false，而各项任务还在收尾。
                    // ⇒ 按“终态计数不再增长”判定收尾完成：静默约 1 秒即放行，最长 5 秒封顶（防把看门狗变成死等）。
                    var graceDeadline = DateTime.UtcNow.AddSeconds(5);
                    int lastDone, stable = 0;
                    lock (_completedItems) lastDone = _completedItems.Count;
                    while (DateTime.UtcNow < graceDeadline && stable < 4)
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                        int nowDone;
                        lock (_completedItems) nowDone = _completedItems.Count;
                        stable = nowDone == lastDone ? stable + 1 : 0;
                        lastDone = nowDone;
                    }
                    throw new TimeoutException(
                        $"队列 {StallWindowSeconds * MaxStallWindows / 60.0:0.#} 分钟内没有任何项目进入终态，判为卡死，已强制停止");
                }
            }
            // 正常完成
            await tcs.Task;
        }

        public Task StopAsync(bool graceful = true)
        {
            if (graceful)
                _processor.StopAfterCurrentQueue();
            else
                _processor.Stop();

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// QueueItem 扩展方法 - 判断是否达到终态
    /// </summary>
    internal static class QueueItemExtensions
    {
        public static bool IsCompletedOrTerminal(this QueueItem item)
        {
            // P2-6 收敛：与 QueueItem.IsTerminal 逐字等价（已完成/失败前缀 + 已停止/已删除精确）
            return item.IsTerminal;
        }
    }
}