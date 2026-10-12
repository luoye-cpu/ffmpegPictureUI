using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services;

/// <summary>
/// 大缓冲的**准入**门：在按整帧租 float[] 之前先看真实空闲物理内存够不够，不够就**有上限地等**，
/// 超时后仍然继续（并响亮点名）。设计目标只有一条：把"静默 OOM"换成"可见的等待"，
/// 同时**不新增死锁面**（等待有上限 ⇒ 两个互相等对方释放内存的任务不可能永久挂住）。
///
/// <para>为什么接缝在这里而不是 <see cref="QueueProcessor.EstimatePerTaskMemoryMB"/>：
/// 那里是**不随像素缩放**的平坦除数（判据只看入队期已知的选项标志、不做文件嗅探，是那里的设计约束），
/// 而这里 <c>w×h</c> 是**已知的** ⇒ 估算能落到这一帧真正要占的量上。
/// 实测这条链的大头（8K 单任务 3363MB）就是 <c>GainMapEncoder</c> 一次租 4 份
/// <c>float[pixelCount*4]</c>（≈64B/px）加解码中间缓冲，而不是并发数。</para>
///
/// <para>估算式由实测拟合（2026-10-12，Release，采样本进程 WorkingSet64，
/// 与 <c>tests/scripts/verify-gainmap-memory.ps1</c> 同夹具 —— testsrc 生成的 16-bit PNG + cICP 9/16）：
///   1080p(2.07MP) <b>263.5</b> / 4K(8.29MP) <b>818.4</b> / 8K(33.2MP) <b>3363.1</b> MB
///   ⇒ <c>峰值MB ≈ 70 + 99 × 百万像素</c>（三点偏差 4.5% / 8.9% / 0.3%，判据上限 15%，
///   见 <c>ServiceProbe</c> 的 <c>memadmit</c> ② 与 <c>docs/IMPLEMENT_PLAN_2026-10-11.md</c>）。
/// 取"空闲物理内存"而不是 <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>：后者是**总量**，
/// 本机实测 32380MB 而 freePhysical 只有 9296MB ⇒ 拿总量除会把系统推到真实上限之外。</para>
/// </summary>
public static class MemoryAdmission
{
    /// <summary>实测拟合的固定项（MB：进程/线程/JPEG 与池化缓冲等）。</summary>
    public const double BaseMB = 70.0;
    /// <summary>实测拟合的每百万像素项（MB/MP）。</summary>
    public const double PerMpMB = 99.0;

    /// <summary>等待预算。超时不是失败 —— 仍继续执行，但会在日志里点名（失败时由逐任务捕获报出）。</summary>
    public const int WaitBudgetMs = 60_000;
    private const int PollMs = 500;

    /// <summary>这一帧预计要占多少 MB（整帧像素数进、MB 出）。</summary>
    public static double EstimateMb(long pixels)
        => BaseMB + PerMpMB * (Math.Max(1L, pixels) / 1_000_000.0);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    /// <summary>
    /// 真实空闲物理内存（MB）。取不到（非 Windows、API 失败、或读数不自洽）⇒ 返回 false，
    /// 调用方**按"不拦"处理**：宁可没有这道门，也不拿一个假读数去放行/拦住任务。
    /// 自洽判据 = <c>0 &lt; avail ≤ total</c> 且 <c>load ∈ [0,100]</c> —— 结构体布局写错时
    /// 这个 API 的典型症状就是返回一堆垃圾（<c>dwLength</c> 必须先赋 sizeof）。
    /// </summary>
    public static bool TryGetAvailableMb(out double availableMb, out double totalMb, out int loadPercent)
    {
        availableMb = totalMb = 0; loadPercent = -1;
        try
        {
            var m = new MemoryStatusEx();
            m.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
            if (!GlobalMemoryStatusEx(ref m)) return false;
            if (m.ullTotalPhys == 0 || m.ullAvailPhys > m.ullTotalPhys) return false;
            if (m.dwMemoryLoad > 100) return false;
            availableMb = m.ullAvailPhys / 1048576.0;
            totalMb = m.ullTotalPhys / 1048576.0;
            loadPercent = (int)m.dwMemoryLoad;
            return true;
        }
        catch { return false; }        // 任何互操作异常都退回"不拦"
    }

    /// <summary>
    /// 等到"看起来放得下这一帧"为止，最多等 <paramref name="budgetMs"/>（默认 <see cref="WaitBudgetMs"/>）。
    /// 三处输出都必须响亮：开始等、多久后放行、超时仍继续 —— 静默的降级是本仓最忌的形态。
    /// ⚠ <paramref name="budgetMs"/> 之所以是参数而不是常量：门禁要**确定性**地看到
    ///   "等待 → 超时 → 仍继续"这条完整路径。只靠把真机器压到缺内存才碰得上它 = 判据无牙
    ///   （实测踩过：`-j 4` 跑 8K×4 全成功、日志里一句准入点名都没有）。生产调用点一律用默认值。
    /// </summary>
    public static async Task WaitAsync(long pixels, string what, Action<string>? log, CancellationToken ct,
                                       int budgetMs = WaitBudgetMs)
    {
        if (!TryGetAvailableMb(out var availMb, out _, out _)) return;
        double needMb = EstimateMb(pixels);
        if (availMb >= needMb) return;

        log?.Invoke($"[MemoryAdmission] {what}: need ~{needMb:F0}MB, free {availMb:F0}MB"
                  + $" => waiting up to {budgetMs / 1000}s\n");
        int waitedMs = 0;
        while (waitedMs < budgetMs)
        {
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
            waitedMs += PollMs;
            if (!TryGetAvailableMb(out availMb, out _, out _)) return;
            if (availMb >= needMb)
            {
                log?.Invoke($"[MemoryAdmission] {what}: admitted after {waitedMs / 1000.0:F1}s"
                          + $" (free {availMb:F0}MB >= {needMb:F0}MB)\n");
                return;
            }
        }
        log?.Invoke($"[MemoryAdmission] \u26a0\ufe0f {what}: still only {availMb:F0}MB free < ~{needMb:F0}MB after "
                  + $"{budgetMs / 1000}s => proceeding anyway; a real OOM is reported per item, not silently\n");
    }
}
