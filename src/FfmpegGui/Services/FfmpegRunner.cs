using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    public static class FfmpegRunner
    {
        /// <summary>
        /// P1-2 判为挂死前要求的静默时长（分钟）—— **仅**用于心跳被拒绝注入的降级路径（stdout 承载数据）。
        /// ⚠ 该路径对**忙等型**挂死结构性失明（`cpuAdvanced` 会给它续命）；默认路径走 <c>-progress pipe:1</c> 心跳。
        /// </summary>
        private const int InactivityTimeoutMinutes = 30;

        /// <summary>
        /// 心跳模式（<c>-progress pipe:1</c>）下「进度块停止到达」的判死秒数。
        /// </summary>
        /// <remarks>
        /// 取值依据（2026-09-18 实测，本仓自带 ffmpeg + 4K 单帧 libjxl <c>-effort 9</c>）：
        /// <list type="bullet">
        /// <item>合法长任务的**首个** progress 块在 <c>t≈0.018 s</c> 到达 —— 该块由 ffmpeg 的进度线程
        /// **周期性**发出，不要求帧号推进（整个 10.3 s 编码里 <c>frame=</c> 恒 0、<c>out_time_us=</c> 恒 40000）。</item>
        /// <item>块间隔：min 0.437 s / p50 0.544 s / max 0.574 s。</item>
        /// <item>忙等型挂死（1 字节畸形 .jxl）：15 s 内 <b>0 字节 / 0 块</b>（两个流都空，却在烧 CPU）。</item>
        /// </list>
        /// ⇒ 120 s 对「最大块间隔 0.574 s」有 ~200× 余量、对「首个块 0.018 s」有 ~6600× 余量，
        /// 且与 <c>QueueProcessor.DefaultJxlProbeTimeoutSec = 120</c> 取值一致。
        /// ⚠ 唯一已知的「合法零进度窗口」是**输入打开/探测阶段**（本地文件为毫秒级）；
        /// 本仓无网络流、无 <c>-f concat</c> 千条清单 ⇒ 该窗口不可能达到 120 s。
        /// ⚠ 判据**不得**改成「<c>out_time</c> 是否推进」：上面实测已证明单帧重活的 <c>out_time_us</c>
        /// 全程不动 ⇒ 那样会把合法的单帧长编码**误杀**。
        /// </remarks>
        internal const int DefaultHeartbeatTimeoutSec = 120;

        /// <summary>挂死守卫触发的返回码（借用 GNU timeout 的 124 约定，便于与真实编码失败区分）。</summary>
        public const int HangExitCode = -124;

        /// <summary>心跳判死秒数（<c>FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC</c> 可注入，便于门禁限时验证；生产默认见常量）。</summary>
        internal static int ResolveHeartbeatTimeoutSec()
        {
            var raw = Environment.GetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC");
            if (int.TryParse(raw, out var sec)) return Math.Clamp(sec, 1, 3600);
            return DefaultHeartbeatTimeoutSec;
        }

        // ⚠ `heartbeat` **默认 true（opt-out）**，不是 opt-in —— 这是 #15-b 的**类级根因**修法：
        //   忙等型挂死（畸形 .jxl 让 ffmpeg 的 JXL 解复用器空转）的特征是「**0 进度块 + 持续烧 CPU**」，
        //   旧双信号判据（无输出 **且** 无 CPU 增长）被 CPU 喂饱后 `InactivityTimeoutMinutes = 30` 那条分支
        //   **结构性不可达** ⇒ 任何未显式开启心跳的调用点一旦忙等就永久挂死。
        //   ⇒ 把心跳做成**默认行为**：全部 **ffmpeg** 调用点自动获得「进度块到达」这个与 CPU 正交的活性信号；
        //     目标不是 ffmpeg、或参数把数据写 stdout 的调用，由 `CanInjectProgressPipe` 自检**拒绝**注入并响亮登记。
        //     ⚠ 判据只在**真的注入了** `-progress` 时才生效 —— 没注入 ⇒ 该判据**不适用**（退回旧判据），
        //       绝不能把「没进度块」当成判死依据：非 ffmpeg 子进程永远发不出 ffmpeg 的进度块。
        //     只有明确需要旧判据时才传 `false`（当前仓内无调用点这么做）。
        public static async Task<int> RunAsync(string arguments, Action<string>? logCallback = null, string? ffmpegPath = null, CancellationToken ct = default, bool heartbeat = true)
        {
            var fileName = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;

            // ── 心跳模式（#15 / #15-b）──────────────────────────────────────────────────────
            // 动机：**忙等型**挂死（畸形 .jxl 让 ffmpeg 的 JXL 解复用器空转）既无输出、又在烧 CPU
            // ⇒ 旧判据的 `cpuAdvanced` 分支会不断重置计时 ⇒ 30 分钟阈值**永不可达**。
            // 办法：给 ffmpeg 追加 `-progress pipe:1`，让**进度块到达**成为活性信号（与 loglevel 正交、
            // 也不要求帧号推进）⇒ 忙等时 0 块，可判死；合法长任务每 ~0.5 s 一块，不会被误杀。
            // ⚠ 心跳模式的活性**只**认进度块（`lastProgressTicks`），**不**认任意 stdout/stderr 输出 ——
            //   否则一行 stderr 噪声就能给忙等续命（旧实现用的正是「任意输出」这条更弱的信号）。
            var heartbeatActive = false;
            if (heartbeat)
            {
                if (CanInjectProgressPipe(fileName, arguments))
                {
                    arguments += " -progress pipe:1";
                    heartbeatActive = true;
                    logCallback?.Invoke($"[心跳] 已追加 `-progress pipe:1`（**不改动 [cmd] 行**）；连续 {ResolveHeartbeatTimeoutSec()} 秒无进度块 ⇒ 判为挂死\n");
                }
                else
                {
                    // 响亮登记、**不静默降级**：本次退回旧判据，对忙等型挂死仍然失明（这是已知残余）。
                    // ⚠ 目标不是 ffmpeg 时这**不是「降级」而是「判据不适用」**：`-progress` 是 ffmpeg 专有选项，
                    //   注入到别的可执行文件会**改坏它的命令行**（探针用 cmd.exe 当子进程替身：追加后 ping 收到
                    //   未知选项、0.0 s 退 1 ⇒ `runner` mode 由 2/0 变 0/2，2026-09-19 E⑫ 整轮实测）。
                    //   ⇒ 非 ffmpeg 子进程**永远发不出 ffmpeg 的进度块**，用「无进度块」判它是**误杀**，不是守卫。
                    // ⚠ 新串**必须纯 ASCII**：cjk 锁 `CsUi` 余量为 0，且它是**按行**计数（一行只计 1）
                    //   ⇒ 这里刻意把原有 2 个 CJK 行合并/保持为 **2 个 CJK 行**，不新增行。
                    var why = IsFfmpegTarget(fileName)
                        ? "该调用把数据写在 stdout（image2pipe / pipe:1 / 裸 `-`）"
                        : "target is not ffmpeg (" + System.IO.Path.GetFileName(fileName) + ")";
                    logCallback?.Invoke("[心跳] ⚠ " + why + " ⇒ **拒绝**注入 `-progress pipe:1`；本次退回旧判据，对忙等型挂死失明\n");
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            // 无输出不等于无进展：RawColorPipeline 等调用带 `-loglevel error`，全程可能一条输出都没有。
            // ⇒ 旧判据同时跟踪“最后一次输出”和“累计 CPU 时间”，两者都静止才判挂死。
            // ⚠ 该判据**只**用于心跳被拒绝注入的降级路径；默认路径的活性判据是 `lastProgressTicks`。
            long lastActivityTicks = DateTime.UtcNow.Ticks;
            long lastProgressTicks = DateTime.UtcNow.Ticks;
            void NoteActivity() => Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
            void NoteProgress() => Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                NoteActivity();
                if (heartbeatActive && IsProgressKeyValue(e.Data))
                {
                    // 进度块每 ~0.5 s 一块、每块 ~11 行 —— 原样转发会淹没队列日志与 UI。
                    // 只把「收尾」这一条稳定信号留一行，其余**只当活性**用、不上抛。
                    NoteProgress();
                    if (e.Data.StartsWith("progress=end", StringComparison.Ordinal))
                        logCallback?.Invoke("[心跳] ffmpeg 进度收尾（progress=end）\n");
                    return;
                }
                logCallback?.Invoke(e.Data + Environment.NewLine);
            };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) { NoteActivity(); logCallback?.Invoke(e.Data + Environment.NewLine); } };

            process.Start();

            try
            {
                PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
            }
            catch { }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var cpuAtLastCheck = SafeCpuTime(process);
            var reg = ct.CanBeCanceled
                ? ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } })
                : default;
            var waitTask = process.WaitForExitAsync(ct);
            // 心跳模式下按心跳周期轮询（注入小阈值时才能限时判死）；非心跳维持原 30 s 不变。
            var heartbeatTimeoutTicks = TimeSpan.FromSeconds(ResolveHeartbeatTimeoutSec()).Ticks;
            var pollSeconds = heartbeatActive
                ? Math.Min(30, Math.Max(1, ResolveHeartbeatTimeoutSec()))
                : 30;
            try
            {
                while (true)
                {
                    var done = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(pollSeconds))).ConfigureAwait(false);
                    if (ReferenceEquals(done, waitTask)) break;

                    if (heartbeatActive)
                    {
                        // 心跳模式：活性**只**由「进度块到达」决定 —— 这里**故意**不看 cpuAdvanced，
                        // 也**不**看任意 stdout/stderr 输出，因为忙等型挂死正是「0 进度块 + 持续烧 CPU」；
                        // 让 CPU（或一行噪声）续命就等于对它失明。
                        var silent = DateTime.UtcNow.Ticks - Interlocked.Read(ref lastProgressTicks);
                        if (silent < heartbeatTimeoutTicks) continue;

                        // ⚠ **取消语义确定性优先**：取消已被请求时不判死，直接交给下面 `await waitTask` 抛 OCE。
                        //   否则「判死」与「取消」在同一 tick 上纯竞速（谁先到谁赢）⇒ 调用方要求取消却拿到
                        //   `HangExitCode`，语义不可预测。判死是**启发式**，取消是**调用方的确定意图** ⇒ 让后者赢。
                        if (ct.IsCancellationRequested) break;

                        try { if (!process.HasExited) process.Kill(true); } catch { }
                        logCallback?.Invoke($"[超时] ffmpeg 连续 {ResolveHeartbeatTimeoutSec()} 秒**无进度心跳**"
                            + $"（`-progress pipe:1` 一个块都没有）⇒ 判为挂死，已终止进程树"
                            + $"（退出码 {HangExitCode}，显式失败不静默）\n");
                        try { await waitTask.ConfigureAwait(false); } catch { }
                        return HangExitCode;
                    }

                    // ── 降级路径：心跳被拒绝注入（stdout 承载数据）时退回旧双信号判据 ────────────────
                    // ⚠ 该判据对忙等型挂死**结构性失明**（CPU 会给它续命）—— 这正是 #15-b 把心跳改成
                    //   默认行为的原因；此处只是拒绝注入时的兜底，已在注入处响亮登记（不静默降级）。
                    var silentOutput = DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivityTicks);
                    var cpuNow = SafeCpuTime(process);
                    // 零星 CPU 增长不算“在干活”：等待型进程（如 sleep/ping 循环）每秒也只有几毫秒，
                    // 真要判“活着”需要一个周期内至少 1 秒的 CPU 时间。
                    var cpuAdvanced = cpuNow.HasValue && cpuAtLastCheck.HasValue
                        && (cpuNow.Value - cpuAtLastCheck.Value) >= TimeSpan.FromSeconds(1);
                    cpuAtLastCheck = cpuNow;

                    if (cpuAdvanced)
                    {
                        // 没有输出但在烧 CPU ⇒ 正常干活（多为 -loglevel error 的转码），重新计时。
                        Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
                        continue;
                    }
                    if (silentOutput < TimeSpan.FromMinutes(InactivityTimeoutMinutes).Ticks) continue;

                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    logCallback?.Invoke($"[超时] ffmpeg 连续 {InactivityTimeoutMinutes} 分钟**既无输出也不消耗 CPU** ⇒ 判为挂死，"
                        + $"已终止进程树（退出码 {HangExitCode}，显式失败不静默）\n");
                    try { await waitTask.ConfigureAwait(false); } catch { }
                    return HangExitCode;
                }
            }
            finally { reg.Dispose(); }

            try { await waitTask.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw;
            }
            return process.ExitCode;
        }

        /// <summary>
        /// 心跳可用性自检（两道），任一不满足即**拒绝**注入 `-progress pipe:1`：
        /// <list type="number">
        /// <item><b>目标必须是 ffmpeg</b>（见 <see cref="IsFfmpegTarget"/>）—— `-progress` 是 ffmpeg 专有选项，
        /// 注入到别的可执行文件会**改坏它的命令行**：探针（`tests/ServiceProbe` 的 `runner` mode）用
        /// <c>cmd.exe</c> 当子进程替身，`/c PING.EXE -n 3 127.0.0.1 &gt;nul` 被追加 `-progress pipe:1` 后
        /// ping 收到未知选项、**0.0 s 退 1** ⇒ 探针「静默正常退出不被误杀」「取消抛 OCE」两条同时转红
        /// （2026-09-19 E⑫ 整轮实测：`runner` 由 2/0 变 0/2）。
        /// ⚠ 语义要点：**非 ffmpeg 子进程永远发不出 ffmpeg 的进度块** ⇒ 它们不该被进度协议判活/判死，
        /// 应退回旧判据；「没注入却按没进度块判死」是**误杀**，不是守卫。</item>
        /// <item>参数不得把数据写 stdout（<c>image2pipe</c> / <c>pipe:1</c> / 裸 <c>-</c>），否则会污染数据流。</item>
        /// </list>
        /// 本仓的数据管道（`-f image2pipe … -`）目前全部是手搓 Process.Start、不经本类，
        /// 但本类是 <c>public static</c> ⇒ 必须挡住未来的误用（一旦混入就是像素流损坏级）。
        /// </summary>
        private static bool CanInjectProgressPipe(string fileName, string arguments)
        {
            if (!IsFfmpegTarget(fileName)) return false;
            if (arguments.IndexOf("-progress", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (arguments.IndexOf("image2pipe", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (arguments.IndexOf("pipe:1", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            var tokens = arguments.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length == 0 || tokens[tokens.Length - 1] != "-";
        }

        /// <summary>
        /// 目标是不是 ffmpeg —— 判据是**可执行文件名含 `ffmpeg`**（不区分大小写）。
        /// 生产全部调用点都解析到 <c>AppSettingsService.Current.FfmpegPath</c>（= `…/ffmpeg.exe`，
        /// 见 <c>PlatformServices.ToolName("ffmpeg")</c>；退化情形也只是裸名 `ffmpeg.exe`）⇒ **恒为真**，
        /// #15-b 的类级修法不受影响。
        /// ⚠ 已知残余（响亮日志、非静默）：若用户把 ffmpeg 二进制**改名**（如 `ff.exe`）且配置指向它，
        /// 心跳会被跳过。本仓把名字写死在 <c>PlatformServices</c>，该情形只可能来自外部替换。
        /// </summary>
        private static bool IsFfmpegTarget(string fileName)
        {
            try
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
                return name.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// `-progress` 的块是 `key=value` 行（键为**小写字母 / 数字 / 下划线**，例如 `stream_0_0_q`）；
        /// 用**形态**识别，避免误吞真实输出。
        /// ⚠ 键里**必须允许数字**：漏掉这一条会让 `stream_0_0_q=` 这类行漏进队列日志
        /// （本门禁 ③ 就是这么抓到的 —— 实测 21 行泄漏）。
        /// </summary>
        private static bool IsProgressKeyValue(string line)
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) return false;
            for (var i = 0; i < eq; i++)
            {
                var c = line[i];
                if (c != '_' && (c < 'a' || c > 'z') && (c < '0' || c > '9')) return false;
            }
            return true;
        }

        /// <summary>累计 CPU 时间（进程刚退出/权限受限时可能取不到，取不到就当未知）</summary>
        private static TimeSpan? SafeCpuTime(Process process)
        {
            try { return process.HasExited ? null : process.TotalProcessorTime; }
            catch { return null; }
        }
    }
}