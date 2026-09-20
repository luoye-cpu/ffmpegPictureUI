using FfmpegGui.Models;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 队列进度观察者接口 - 用于解耦 QueueProcessor 与 UI/CLI
    /// </summary>
    public interface IQueueObserver
    {
        /// <summary>队列项进度更新（状态/日志/耗时变化）</summary>
        void OnItemProgress(QueueItem item);

        /// <summary>队列项完成（成功/失败/取消）</summary>
        void OnItemCompleted(QueueItem item);

        /// <summary>整个队列处理完毕</summary>
        void OnQueueCompleted();
    }
}