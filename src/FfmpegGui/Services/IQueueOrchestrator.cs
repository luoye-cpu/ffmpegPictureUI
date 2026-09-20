using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FfmpegGui.Models;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 队列编排器接口 - 统一 GUI/CLI/后台服务的队列控制入口
    /// </summary>
    public interface IQueueOrchestrator
    {
        /// <summary>是否有任务正在运行</summary>
        bool IsRunning { get; }

        /// <summary>启动队列处理</summary>
        Task StartAsync(IReadOnlyList<QueueItem> items, int concurrency = 2, CancellationToken ct = default);

        /// <summary>等待队列处理完成（StartAsync 后调用）</summary>
        Task WaitForCompletionAsync(CancellationToken ct = default);

        /// <summary>停止队列处理</summary>
        /// <param name="graceful">true=完成当前项后停止，false=立即取消</param>
        Task StopAsync(bool graceful = true);

        /// <summary>队列项进度更新事件</summary>
        event Action<QueueItem> ItemProgress;

        /// <summary>队列项完成事件</summary>
        event Action<QueueItem> ItemCompleted;

        /// <summary>队列全部完成事件</summary>
        event Action QueueCompleted;
    }
}