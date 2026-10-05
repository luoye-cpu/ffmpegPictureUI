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

        // ⚠ 2026-09-21 加固 1/3：并发信号量提为**字段**（跨循环共享）。
        //   原先在 ProcessAsync 内 `new SemaphoreSlim(_concurrency)` ⇒ 快速重启时
        //   新旧两个循环各持一个信号量 ⇒ **瞬时总并发可达 2×_concurrency**（峰值内存超预期）。
        private SemaphoreSlim _sem = new(2, 2);

        // ⚠ 2026-09-21 加固 2/3：后台循环句柄 —— Start() 先等旧循环退出，再启动新循环。
        private Task? _runner;

        /// <summary>调度器是否正在运行（有活跃的 CTS 且未被取消）</summary>
        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
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
            // 动态限制：根据可用内存与**队列内容**调整最大并发数（每任务预留见 EstimatePerTaskMemoryMB）
            _concurrency = ClampConcurrencyByMemory(_concurrency, EstimatePerTaskMemoryMB());
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
            }

            // ⚠ 2026-09-21 加固 2/3：**先等旧循环真正退出**再启动新循环。
            //   原先只 Cancel + null ⇒ 旧循环可能仍在 `await Task.WhenAll(tasks)` 收尾，
            //   新循环立即启动 ⇒ 两批任务重叠（配合加固 1 的共享信号量后已不会超并发，
            //   但仍避免同一队列被两个循环同时消费、以及并发峰值抖动）。超时兜底 3s，异常不外抛。
            var prev = _runner;
            if (prev != null && !prev.IsCompleted)
            {
                try { prev.Wait(TimeSpan.FromSeconds(3)); }
                catch { /* 旧循环的异常不应阻断重启 */ }
            }
            _runner = null;

            RebuildSemaphore();

            _stopAfterQueueRequested = false;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _runner = Task.Run(() => ProcessAsync(ct));
        }

        /// <summary>
        /// 重建并发信号量以匹配 <c>_concurrency</c>。
        /// ⚠ 2026-09-21 加固 3/3：必须在**无活跃任务**时调用（<c>Start()</c> 已先等旧循环退出）。
        /// </summary>
        private void RebuildSemaphore()
        {
            var old = _sem;
            _sem = new SemaphoreSlim(_concurrency, _concurrency);
            try { old.Dispose(); } catch { /* 旧实例可能仍被已退出的循环引用，忽略 */ }
        }

        /// <summary>
        /// 每任务内存预留（MB）。**不再用拍脑袋的固定值**——2026-09-15 实测（Release 构建，采样进程
        /// WorkingSet64，素材为 16-bit + bt2020/PQ 的 PNG，见 docs/TESTING.md §6 第 28 条）：
        ///   · GainMap 纯托管编码：1080p(2.07MP) 峰值 218.8MB、4K(8.29MP) 峰值 **680.3MB**
        ///     ⇒ 线性拟合 ≈ **65MB + 74MB/百万像素**（4 份 <c>float[像素×4]</c> 线性缓冲 ≈64B/px，加 JPEG/PFM 缓冲）；
        ///   · 普通通路 4K 峰值：**传统管线 51.9MB**（全程流式，不进托管线性缓冲）；
        ///     **引擎（现为默认）151.5MB** —— 引擎同样要整帧解码成 rgb48le 再做托管变换。
        ///     ⚠ 2026-09-16 更正：本注释原写「普通 ffmpeg 通路 4K 峰值仅 **53.7MB**」，
        ///     那是**传统管线**的数字；引擎成为默认执行路径后该前提失效，`plainMB` 已相应重标。
        /// 旧的固定 200MB 对 4K GainMap **低估 3.4 倍**（高并发 + 大图可 OOM），对普通通路又高估 3.7 倍
        /// （白白压低了并发）。这里按「队列里只要有走线性缓冲的任务，就按 4K 量级预留」的保守口径分两档。
        /// 判据只看**入队期已知的选项标志**（GainMap 编码 / RAW 预处理），不做文件嗅探
        /// ——读盘或调 ffprobe 不该发生在 <see cref="Start"/> 里。
        /// ⚠ 已知缺口：**Ultra HDR JPEG 作为输入**（解码侧）没有便宜的入队期标志，仍按普通档预留；
        ///   该通路同样会开 4K 量级线性缓冲。后续若要做，应在 <see cref="Add"/> 时做轻量头部嗅探（读几十字节即可）。
        /// </summary>
        private int EstimatePerTaskMemoryMB()
        {
            // ⚠ 2026-09-16 重新标定 plainMB：**自引擎成为默认执行路径起，普通通路不再是「全程流式」**。
            //   同一 4K 素材 A/B 实测（Release，采样 WorkingSet64，同一台机器同一轮）：
            //     · 普通 + 引擎（默认，`--format jpg`）     峰值 **151.5MB**
            //     · 普通 + 传统（`--color-engine legacy`）  峰值 ** 51.9MB**
            //     · GainMap（引擎）                        峰值 **684.2MB**
            //   差异来源：引擎普通出口也要先把整帧解码成 `rgb48le` 原始缓冲再做托管变换
            //   （4K ⇒ 3840×2160×6 ≈ 49.8MB 入 + 24.9MB 出，加 GC/文件缓冲），
            //   而传统管线把色彩工作全交给 ffmpeg 子进程、本进程只走管道。
            //   ⇒ 旧值 64MB 是**按传统通路标定的**，对现在的默认通路**低估 2.4 倍**。
            //   取值 192MB = 实测 151.5MB 留 ~27% 余量（峰值随素材/编码器版本浮动）。
            const int plainMB = 192;
            const int linearMB = 768;  // 实测 GainMap 4K = 684.2MB，留 ~12% 余量（峰值随素材/编码器版本浮动）
            try
            {
                foreach (var it in _queue)
                {
                    if (it.Options != null
                        && (it.Options.JpegGainMap || RawService.IsRawFile(it.InputPath)))
                        return linearMB;
                }
                return plainMB;
            }
            catch
            {
                return linearMB;   // 枚举失败 ⇒ 按最坏情况预留：宁可慢，不可 OOM
            }
        }

        /// <summary>根据系统可用内存与每任务预留量限制并发数（下限 1，上限不变）</summary>
        private static int ClampConcurrencyByMemory(int requested, int perTaskMB)
        {
            try
            {
                var memInfo = GC.GetGCMemoryInfo();
                var availableMB = memInfo.TotalAvailableMemoryBytes / (1024 * 1024);
                var maxByMemory = (int)Math.Max(1, availableMB / Math.Max(1, perTaskMB));
                return Math.Max(1, Math.Min(requested, maxByMemory));
            }
            catch { return requested; }
        }

        /// <summary>
        /// 将所有"已停止"和"失败"的项重新加入队列（状态复位为"待处理"）。
        /// 调用方负责在调用此方法前停止正在运行的队列。
        /// </summary>
        public void RequeueStoppedAndFailed(List<QueueItem> allItems)
        {
            foreach (var item in allItems)
            {
                if (item.IsStopped || (item.IsFailure && item.ExitCode != 0))
                {
                    item.Status = "待处理";
                    item.IsCancelled = false;
                    item.ExitCode = null;
                    item.Log += "[重新排队]\n";
                    _queue.Enqueue(item);
                    _onItemUpdated?.Invoke(item);
                }
            }
        }

        public void Stop()
        {
            // 立即取消 CTS 并清除优雅停止请求
            _stopAfterQueueRequested = false;
            _cts?.Cancel();
            _cts = null;
            // 临时目录由各任务自己的 finally 删除（取消路径同样会跑到；见 QueueProcessor 任务级 finally）。
            // 旧写法在这里统一删 _pendingRawDirs，但那个列表只在 Stop() 时被处理 ⇒ 正常跑完一批全留在盘上（P1-6）。
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
            // ⚠ 2026-09-21 加固 1/3：使用**共享字段**信号量（跨循环），不再每次 new。
            //   （原先 `new SemaphoreSlim(_concurrency)` 会在快速重启时造成 2× 并发。）
            var sem = _sem;
            var tasks = new List<Task>();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // 定期清理已完成任务（每 10 次迭代清理一次，更及时）
                    if (tasks.Count > 0 && tasks.Count % 10 == 0)
                        tasks.RemoveAll(t => t.IsCompleted);

                    // 如果已请求在当前队列完成后停止，且当前无待处理项且所有已启动任务均完成，则退出循环
                    // 修复：每次循环都检查，不仅在取出任务时
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
                    // 跳过已停止的任务
                    if (item.IsCancelled)
                    {
                        item.Status = "已删除";
                        _onItemUpdated?.Invoke(item);
                        continue;
                    }

                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    var captured = item; // capture for closure

                    // ── 2026-08-15 自适应线程分配 ──
                    // 自动模式 (AutoThreads): 每任务线程 = max(1, 核数/并发任务数),
                    // 所有任务合计吃满核数; 并发≥核数时每任务 1 线程 (任务并行占满核)。
                    // 手动/单线程模式: 遵循用户固定值。实测: 串行×多线程最快,
                    // 超饱和 (16并发×20线程) 反而最慢 — 此分配避免超饱和。
                    if (captured.Options.AutoThreads)
                    {
                        captured.Options.Threads =
                            Models.FfmpegOptions.ComputeAdaptiveThreads(_concurrency);
                    }

                    var tempDirsToCleanup = new List<string>(); // 任务内创建的临时目录（RAW 预处理等），完成后统一清理
                    // P1-4 需要：目标路径与“执行前是否已存在”必须声明在 try 外，finally 才拿得到
                    string? runOutputPath = null;
                    bool outputExistedBefore = false;
                    var t = Task.Run(async () =>
                    {
                        // P1-5：下面的预处理会把 captured.InputPath **改写**成临时件（UltraHDR PFM /
                        // RAW→TIFF / MaxDimension 预缩放 PNG），而临时目录在项结束时删除。
                        // 不还原的话，「重新排队」拿着已消失的临时路径重跑必然失败（且错误信息还指向不存在的文件）。
                        var originalInputPath = captured.InputPath;
                        // ⚠⚠ 2026-09-21：把「用户实际提供的源文件」钉死在**改写之前** —— 供两处使用：
                        //   ① **元数据恢复的取源**：RAW 的拍摄字段（`DateTimeOriginal` / `CreateDate` /
                        //      `LensModel` / `ExposureProgram` / `MeteringMode` / `Flash` 等）**只存在于原文件**；
                        //      中间件 `dngtool -d -T` 只搬 5 项 EXIF ⇒ 以临时件为源会永久丢失（实测 5/5 厂商）。
                        //   ② **队列显示名**：否则 RAW 输入会显示内部临时文件名 `xxx_raw.tiff`。
                        //   幂等赋值（只写一次）：同一项可能被多次改写 InputPath。
                        if (string.IsNullOrEmpty(captured.SourceInputPath))
                            captured.SourceInputPath = originalInputPath;
                    try
                    {
                        // ── 前置：**元数据剥离需要 exiftool**，缺失时不得静默放行（2026-09-22，管线审查 §3.5）────
                        // 实测缺陷：exiftool 不可用时选「删除全部元数据」，ffmpeg 侧仍用 `-map_metadata 0`
                        //   **把源元数据整体复制** ⇒ 产物**原样带出 GPS**，且日志**无任何点名**、任务报成功。
                        // 处置沿用本文件既有先例（DNG/dngtool 的「**可用性诚实原则**」）：
                        //   · **显式选了「删除全部」**（用户明确要求剥离）⇒ **点名 + 失败**（不静默降级）；
                        //   · 仅**个别开关**为真 ⇒ 只**点名警告**、**不失败** —— 因为 `StripExifGps` 的
                        //     **默认值就是 true**（隐私默认），若也失败则「缺 exiftool」会把**每个**任务都判失败。
                        // ⚠ 消息一律 **ASCII**：`_probe-cjk-hardcode-scan` 的余量为 0，新增中文行会立刻顶红。
                        bool stripAllRequested = captured.Options.MetadataMode == Models.MetadataMode.StripAll;
                        bool stripFlagsOn = captured.Options.StripExifTime || captured.Options.StripExifCamera
                                         || captured.Options.StripExifAll || captured.Options.StripXmp
                                         || captured.Options.StripExifGps;
                        if ((stripAllRequested || stripFlagsOn) && !ExifToolService.IsAvailable)
                        {
                            captured.Log += "[meta] exiftool NOT FOUND\n";
                            captured.Log += "[meta]   requested metadata strip CANNOT be performed"
                                         + " (ffmpeg would still copy source metadata via -map_metadata 0)\n";
                            captured.Log += "[meta]   put exiftool under PLAN/exiftool/ , or turn the strip options off\n";
                            if (stripAllRequested)
                            {
                                captured.Log += "[meta]   -> refusing this item (no silent fallback)\n";
                                captured.ExitCode = -1;
                                captured.Status = "失败 (exiftool 未找到)";
                                _onItemUpdated?.Invoke(captured);
                                return;
                            }
                            captured.Log += "[meta]   -> continuing WITHOUT the requested strip (named above)\n";
                            _onItemUpdated?.Invoke(captured);
                        }

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
                            runOutputPath = finalOutputPath;
                            outputExistedBefore = File.Exists(finalOutputPath);

                            // ⚠⚠ 2026-10-05 新增（P0 数据损失修复）：`--no-overwrite` 时**存在即拒绝**。
                            //   动机（实测）：全链路无条件 `-y` ⇒ 重跑一次就**静默**毁掉上次（可能手改过的）
                            //   产物，无提示、退出码 0。备份/不可逆丢失属最高风险等级，
                            //   而"重跑同一批"又恰恰是最常见的操作。
                            //   ⚠ 语义选择「**拒绝并点名**」而不是「自动改名」：
                            //     改名会让用户以为写到了目标路径（本仓既有 `[output-conflict]` 只处理
                            //     **同批内**重名，与此不冲突）。拒绝则冲突显式、可预期。
                            //   ⚠ 默认 `AllowOverwriteExisting=true` ⇒ 本分支不触发，既有行为逐字不变。
                            if (outputExistedBefore && !captured.Options.AllowOverwriteExisting)
                            {
                                captured.Log += $"[跳过] 目标文件已存在且已指定 --no-overwrite ⇒ 不覆盖：{finalOutputPath}\n";
                                _onItemUpdated?.Invoke(captured);
                                captured.ExitCode = 1;
                                // ⚠ 状态词必须以「失败」开头：`QueueItem.IsFailure`（= `HasError`）按
                                //   `StartsWith("失败")` 判定，而**队列汇总的「失败 N 项」与退出码都读它**
                                //   ⇒ 若写成「跳过（…）」会**不计入失败**、汇总仍是「失败 0 项」且 exit 0
                                //   ⇒ 脚本无法察觉"什么都没转换"（实测踩过）。
                                //   ⇒ 用「失败（跳过：目标已存在且未允许覆盖）」：既计入失败、又保留原因。
                                captured.Status = "失败（跳过：目标已存在且未允许覆盖）";
                                return;
                            }

                            // ── 根据编码器后端调度 ──
                            var backend = captured.Options.EncoderBackend;
                            var inputExt = Path.GetExtension(captured.InputPath).ToLowerInvariant();

                            // 诚实提示（P7a）：用户显式选了引擎，但本条任务会走外部编码器/专用管线。
                            // 引擎目前只接管默认 ffmpeg 分支的色彩执行；不说出来就等于“选了引擎但没人走”。
                            // ⚠ GainMap 自 2026-09-16 起**已接入引擎**（引擎可走时由引擎的 GainMap 出口执行），
                            //   只有引擎不可走（如 GainMap + 最长边缩放）才落回专用管线 —— 提示必须按此收窄，
                            //   否则会对“其实走了引擎”的任务谎报“尚未接入”（反向的不诚实）。
                            // ⚠ 2026-09-17：`ShouldRoute` 提到此处单点求值，供 GainMap 提示与色域映射判定共用
                            //   （同一纯函数，不构成第二份判据）。
                            // ⚠ `#136` 切片 B：`InputMayHaveMultipleFrames` 现在会**真的探测** `.webp`/`.avif`
                            //   （旧代码在「输出非 gif/apng」时直接回落 `IsAnimated`，而它对这两者不探测
                            //   ⇒ 多帧 webp/avif 输入被判成单帧 ⇒ 引擎按单帧处理 ⇒ **静默丢帧**，`#43`）。
                            //   探测有代价（每条 1~2 次 ffprobe，实测 47~83 ms 量级）⇒ **每项只求值一次**，
                            //   在此单点求值，供 `ShouldRoute`、GainMap 专用分派、`TryRunColorEngineAsync`
                            //   三处复用。三处必须是**同一份**判据，否则就是"路由说能走、分派说不能走"。
                            // ⚠ `#136` 收尾轮：本变量新增**第 4 个**消费点 = 下方「预缩放跳过」判定
                            //   （`maxDimSkipAnimated`）。它要问的本来就是「**输入**是否多帧」⇒ 改为直接消费
                            //   本变量，不再调 `IsAnimated(captured)`（那会把「**输出**容器是 gif/apng」也算进来，
                            //   而那一格对预缩放点是多余的）。详见预缩放段的注释。
                            var inputMayHaveMultipleFrames = await InputMayHaveMultipleFramesAsync(captured, ct);

                            // ── 友好提醒（2026-09-19，维护者裁决第 1 项）：静态输入 + 动图意图 ──────────
                            // 现象（实测，2026-09-19）：`--animation-fps N` 配**单帧静态输入**时，命令行里会出现
                            //   `fps=N` 滤镜，而 webp 动画编码器（libwebp_anim）要求真多帧输入 ⇒ ffmpeg 侧
                            //   `WebPAnimEncoderAssemble() failed with error: 1`、零产物；`-c:v libwebp` 变体更坏
                            //   （rc=0 且 0 字节 = "静默成功"+空产物）。引擎路径则给 `AnimatedInput` 拒绝，但其
                            //   文案写的是「输入为动画」—— 对**静态**输入属**反向误导**。
                            //   ⇒ 三种 `--color-engine` 下用户看到三种互不相同、且都不说明真实原因的结局。
                            // 处置（维护者裁定）：**只提醒、不拦截** —— 用户坚持要跑就照常跑，但必须先被告知。
                            // 判据：`AnimationFps.HasValue`（输出侧「多帧意图」）**且** 输入侧真探测为**单帧**。
                            // ⚠ **不改任何路由判据**：`inputMayHaveMultipleFrames` 仍是「意图 OR 探测」，与
                            //   `InputMayHaveMultipleFramesAsync` 原语义逐字一致（它在 AnimationFps 有值时
                            //   短路返回 true、**不做**输入探测）。本块只是**额外**取一次输入侧结论。
                            // ⚠ 代价受控：仅当 AnimationFps 有值时才走这一支，而那时上面的短路**没探测过**
                            //   ⇒ 总探测次数仍为 1（.webp/.avif 各 1 次 ffprobe；.jxl 走 `AnimatedJxlCache`）。
                            // ⚠ 为什么不会改坏 `IsAnimated`：它在 `:4759` 先按 `AnimationFps.HasValue` 短路为
                            //   true，**到不了**下面那条读 `AnimatedWebpCache` 的 `.webp` 子句 ⇒ 本块即使提前
                            //   把该缓存写热，也无法翻转它的答案（否则就是"修一个洞、开另一个洞"）。
                            if (captured.Options.AnimationFps.HasValue
                                && !await InputHasMultipleFramesAsync(captured, ct))
                            {
                                // ⚠⚠ 2026-09-20：**这两条告警必须各自保持「单行」** —— 它们走的是诊断 sink
                                //   （`captured.Log +=`），而 `_probe-cjk-hardcode-scan` 的「UI 面」判据是**按行**判定：
                                //   一旦折行，**续行**上没有 sink 标记 ⇒ 被误计入「UI 面文案」桶
                                //   （实测：折成 4 行时该门禁由 832 涨到 **835**，正是这 3 条续行）。
                                //   ⇒ 文案一字不改，只把折行合回一行。**别再为了排版拆行。**
                                captured.Log += $"[动画] ⚠️ 已请求动图帧率（--animation-fps {captured.Options.AnimationFps.Value}），但输入是单帧静态图：本次仍会照常执行（不拦截），不过 webp/gif/apng 等动画出口需要真多帧输入，极可能失败或产出 0 字节文件。\n";
                                captured.Log += "[动画]    如非本意，请去掉 --animation-fps；若确实要动图，请改用多帧输入（.gif/.apng/动画 .webp/.avif/.jxl）。\n";
                            }

                            // ── jbrd 有效值归一（2026-09-30）────────────────────────────────────────
                            // 开关自 09-30 起**默认启用**（`FfmpegOptions.DefaultJxlLosslessJpeg = true`），
                            // 而 `JxlLosslessJpeg` 这个字段是「本次任务的有效值」：只有输入真是 JPEG 才成立。
                            // 为什么必须在这里归一：`ImageEncoderArgs.UnsupportedEncoderSettings` 只看这个
                            // 布尔就把 jxl 判成「引擎不可承接」（重封装要复制 DCT 系数，引擎必须先解码），
                            // 而它拿不到输入路径（`ColorEngineRouter.BlockReason(o, outputPath, animated)`
                            // 的签名里没有输入）⇒ 不归一的话 **PNG→JXL 会被整批挡出色彩引擎**。
                            // 必须排在 `engineRoutable` **之前**：再往下的预缩放（:649）与 Ultra HDR 解码
                            // 都会改写 `InputPath`，那时就看不出「原始输入是不是 JPEG」了。
                            // ⚠ 2026-10-01 补第三个条件（目标格式）：同一个布尔还是
                            //   `CjxlService.UsesLosslessJpegRewrap` 的输入，而后者决定
                            //   "源的 Orientation 要不要留在产物上"。只与到"输入是 JPEG" ⇒
                            //   JPEG→JPEG/PNG/AVIF/WebP 也自认"像素没动"，于是把取向标签留在
                            //   已被旋正的像素上 ⇒ 查看器二次旋转（出货包实测，见该谓词注释）。
                            if (captured.Options.JxlLosslessJpeg
                                && (!CjxlService.IsJpegInputPath(captured.InputPath)
                                    || !captured.Options.Format.Equals("jxl", StringComparison.OrdinalIgnoreCase)))
                                captured.Options.JxlLosslessJpeg = false;

                            bool engineRoutable = ColorMapping.ColorEngineRouter.ShouldRoute(
                                captured.Options, finalOutputPath, inputMayHaveMultipleFrames);
            bool gainMapViaDedicated = captured.Options.JpegGainMap && !engineRoutable;
            // ⚠ 2026-09-17（cjxl 出口轮）：`backend != Ffmpeg` **不再等于**"引擎不接管" ——
            //   jxl+Cjxl 已被引擎的 cjxl 出口接管（见 `ColorTransformPlan.ExternalEncoderExit`）。
            //   若仍按 backend 单独判，就会对**其实走了引擎**的任务谎报"尚未接入"（反向的不诚实），
            //   与上面 GainMap 那条注释同款理由。故判据改为「非 Ffmpeg 后端 **且** 引擎不可走」。
            bool externalBackendNotRouted = backend != EncoderBackend.Ffmpeg && !engineRoutable;
            if (ColorMapping.ColorEngineRouter.Requested(captured.Options)
                && (externalBackendNotRouted || gainMapViaDedicated
                    || inputExt is ".jxl" or ".jxr" or ".avif"))
                captured.Log += $"[色彩引擎] ⚠️ 本次由"
                    + (gainMapViaDedicated ? " GainMap 专用管线（引擎不可走）"
                       : externalBackendNotRouted ? $" 外部编码器 {backend}"
                       : $" {inputExt} 专用输入管线")
                    + " 处理，色彩引擎尚未接入该路径（P7 逐条接通）；色彩决策仍按所选策略\n";

                            // ── 可用性诚实（2026-09-17）：色域映射（GMO）是引擎专有算子，传统链只有硬裁切 ──
                            // 判据单一来源 = ColorEngineRouter（显式 legacy + on ⇒ 硬失败；auto 回退 ⇒ 仅告警）。
                            // 不做这件事的实测后果：legacy + `--color-gamut-map on` 与 off 的产物**逐字节相同**
                            // （27148B / 57C1B004F4123EAA）、日志 0 条 gamut 行 ⇒ 用户开了开关却什么都没发生。
                            var gamutConflict = ColorMapping.ColorEngineRouter.LegacyGamutMapConflict(captured.Options);
                            if (gamutConflict != null)
                            {
                                captured.Log += $"[色彩引擎] ❌ {gamutConflict}\n";
                                _onItemUpdated?.Invoke(captured);
                                throw new InvalidOperationException(gamutConflict);
                            }
                            if (ColorMapping.ColorEngineRouter.GamutMapRequested(captured.Options) && !engineRoutable)
                            {
                                // 默认 auto 下引擎不可走属正常归属（用户没选 legacy）⇒ 只告警，绝不硬失败，
                                // 否则默认切引擎后所有非引擎格式（JXR/DNG/动画）都会全红。
                                // （2026-09-17 cjxl 出口轮更正：曾把「JXL 的 Cjxl 后端子情形」也列在此处，现已接入。）
                                captured.Log += "[色彩引擎] ⚠️ 已请求色域映射（--color-gamut-map on），但本次引擎不可走"
                                    + " ⇒ 传统链只有硬裁切（钳位），该开关**不会被兑现**（按钳位处理）\n";
                                _onItemUpdated?.Invoke(captured);
                            }

                            // 格式守卫（可用性诚实原则）：后端与目标格式不匹配时【直接报错终止】，
                            // 不再静默回退 FFmpeg —— 静默回退会导致「实际编码器 ≠ 用户所选」，让用户误判可用性，
                            // 且可能产出扩展名与真实内容不符的错标文件（如 backend=Cjxl 但 format=avif）。
                            if (!EncoderBackendCompat.IsCompatibleWith(backend, captured.Options.Format))
                            {
                                throw new InvalidOperationException(
                                    $"所选编码器后端 {backend} 不适用于目标格式 {captured.Options.Format}。" +
                                    "请改用兼容的编码器/格式组合（不回退到其他编码器，避免产出与所选不符的文件）。");
                            }

                            // ── jbrd 无损重封装 × ffmpeg-libjxl 后端：点名，不静默（2026-09-30）──────
                            // 该开关自 09-30 起**默认启用**，于是「JPEG 输入 + jxl 输出 + 手动选了 ffmpeg
                            // 后端」从罕见格变成常见格。而本工具链的 ffmpeg-libjxl **写不出 jbrd**
                            // （`FfmpegCommandBuilder` 的 2026-09-19 更正已登记；本次复测同一份 JPEG：
                            //  ffmpeg-libjxl 5,551.3 kB / 1.73× vs cjxl 真重封装 3,041.9 kB / 0.95×，
                            //  且只有后者的 jxlinfo 报 "JPEG bitstream reconstruction data available"）。
                            // 那条分支只发 `-distance 0`，是"低损重编码"，不是用户勾的那件事
                            // ⇒ 必须说出来；只写在源码注释里等于没写（用户看不到注释）。
                            if (captured.Options.JxlLosslessJpeg && backend == EncoderBackend.Ffmpeg
                                && captured.Options.Format.Equals("jxl", StringComparison.OrdinalIgnoreCase))
                            {
                                captured.Log += "[cjxl] ⚠️ 无损重封装(jbrd)开着，但本次后端是 ffmpeg 的 libjxl：该出口实测不写 jbrd 盒 ⇒ 产物是重新编码的像素，不是 JPEG 比特流重建（体积也更大）。要真重封装请改用 cjxl 后端。\n";
                                _onItemUpdated?.Invoke(captured);
                            }

                            // Ultra HDR JPEG 输入：GainMapDecoder (纯 C#) 应用增益图恢复 HDR 线性像素
                            // JXR 格式由 ProcessJxrAsync 自行处理（直接 RAW→TIFF 管道，保 HDR 元数据）
                            var isJxrOutput = captured.Options.Format.Equals("jxr", StringComparison.OrdinalIgnoreCase);
                            // Ultra HDR 输入有**两条**承载路：JPEG（MPF + APP2 ISO 21496-1）与
                            // AVIF/HEIC（ISO-BMFF `tmap` 派生项 + 独立增益图 item）。探测入口不同，
                            // 但都汇入同一个「解码为线性 HDR → 替换 InputPath」的流程。
                            // ⚠ ISO-BMFF 侧由 `IsIsoBmffGainMapAsync` **自己点名**「反向方向/未支持形式」，
                            //   故此处返回 false 不等于静默丢弃。
                            var isIsoBmffGainMapInput = inputExt is ".avif" or ".heic" or ".heif"
                                && await GainMapDecoder.IsIsoBmffGainMapAsync(captured.InputPath, s => captured.Log += s, ct);
                            if (!isJxrOutput
                                && (isIsoBmffGainMapInput
                                    || (inputExt is ".jpg" or ".jpeg"
                                        && await IsUltraHdrJpegAsync(captured.InputPath, s => captured.Log += s, ct))))
                            {
                                var skipForJxl = captured.Options.Format.Equals("jxl", StringComparison.OrdinalIgnoreCase)
                                    && !captured.Options.JxlPreserveUltrahdr;
                                if (skipForJxl)
                                {
                                    captured.Log += "[UltraHDR] 输入为 Ultra HDR JPEG，但已关闭「保留增益图」→ 按 SDR 基础图处理\n";
                                }
                                else
                                {
                                    if (isIsoBmffGainMapInput)
                                        captured.Log += "[UltraHDR] 检测到 AVIF/HEIC 增益图（ISO-BMFF tmap 项），GainMapDecoder 应用增益图恢复 HDR...\n";
                                    else
                                        captured.Log += "[UltraHDR] 检测到 Ultra HDR JPEG，GainMapDecoder 应用增益图恢复 HDR...\n";
                                    _onItemUpdated?.Invoke(captured);
                                    var decoded = await DecodeUltraHdrToLinearAsync(captured, captured.InputPath, ct);
                                    if (decoded != null)
                                    {
                                        captured.InputPath = decoded.Value.path;
                                        tempDirsToCleanup.Add(decoded.Value.tempDir);   // P1-6：随本项结束一起删
                                        // 线性相对亮度：cjxl 用 RGB_D65_SRG_Rel_Lin(SDR 基图/BT.709) 或
                                        // RGB_D65_202_Rel_Lin(HDR PQ 基图/BT.2020) + --intensity_target=峰值 精确表达线性 HDR
                                        captured.Options.DecodedUltraHdrColorSpace =
                                            decoded.Value.jxlColorSpace ?? "RGB_D65_SRG_Rel_Lin";
                                        captured.Options.DecodedUltraHdrPeakNits = decoded.Value.peakNits;
                                        // ⚠⚠ 同时声明**输入侧**色彩（2026-10-04 审查 线D P1-1 修）：
                                        //   解码产物是**线性** HDR（PFM，1.0 = SDR 白点 203nits）。只写
                                        //   `DecodedUltraHdr*` 的话，`ColorIntentFactory` 拿不到源描述符
                                        //   ⇒ 落到 `AssumeByConvention` 的 `CodecDefault` ⇒ 规划层置信度闸门
                                        //   （`MinConfidenceForMapping = CicpTag`）**不许改像素** ⇒ 计划 `None`
                                        //   （直通）⇒ **不挂色调映射** ⇒ 线性 HDR 像素被按 sRGB 直接编码、
                                        //   >1.0 截顶（实测：直通 YMAX=251 vs 往返 255；YAVG 125.5 vs 134.7）。
                                        //   ⚠ 形状与 2026-10-03 已修的 RAW 直通缺陷**完全相同** —— 那条路当时漏了。
                                        //   与 RAW 同一条路：走 `ColorSourceDecl*`（`BuildColorArgsSplit` 会把
                                        //   它当 `-i` 前的输入声明，且**不**会污染输出目标）。
                                        captured.Options.ColorSourceDeclPrimaries = "bt709";
                                        captured.Options.ColorSourceDeclTrc = "linear";
                                        captured.Log += $"[UltraHDR] ✅ 已解码为线性 HDR PFM（峰值 {decoded.Value.peakNits:F0} nits），继续编码\n";
                                    }
                                    else
                                    {
                                        captured.Log += "[UltraHDR] ⚠️ 增益图应用失败，按 SDR 基础图处理\n";
                                    }
                                    _onItemUpdated?.Invoke(captured);
                                }
                            }

                            // ── RAW 预处理：Bayer 传感器数据去马赛克 ──
                            // dngtool 将相机 RAW → 线性 16-bit TIFF，解决 ffmpeg 无法处理
                            // Bayer RAW 和色彩映射错误的问题。
                            // 注意: DNG 输出目标跳过此步骤——ProcessDngAsync 需要原始传感器数据
                            // (Bayer 相位/白平衡)，必须由 dngtool 直接读取输入 RAW 文件编码。
                            var isDngOutput = captured.Options.Format.Equals("dng", StringComparison.OrdinalIgnoreCase);
                            // 可用性诚实原则：RAW 输入（非 DNG 输出）必须经 dngtool 去马赛克；工具缺失时【直接报错】，
                            // 不再落到默认 ffmpeg 直解（ffmpeg 无法正确解 Bayer → 产出错色/伪影却标记「已完成」）。
                            if (RawService.IsRawFile(captured.InputPath) && !isDngOutput && !RawService.IsAvailable)
                            {
                                throw new InvalidOperationException(
                                    "输入为 RAW 格式，需 dngtool 去马赛克，但未检测到 dngtool。" +
                                    "请将 dngtool 放入 PLAN/artifacts/ 或在设置中配置路径（不回退 ffmpeg 直解，避免错色输出）。");
                            }
                            if (RawService.IsRawFile(captured.InputPath) && RawService.IsAvailable
                                && !isDngOutput)
                            {
                                captured.Log += "[RAW] 检测到 RAW 文件，正在进行 dngtool 去马赛克预处理...\n";
                                _onItemUpdated?.Invoke(captured);
                                var rawTempDir = Path.Combine(PlatformServices.GetTempDir(), $"raw_{Guid.NewGuid():N}");
                                Directory.CreateDirectory(rawTempDir);
                                tempDirsToCleanup.Add(rawTempDir);  // 任务完成后统一删除，避免磁盘泄漏
                                var rawTiff = Path.Combine(rawTempDir, $"{Path.GetFileNameWithoutExtension(captured.InputPath)}_raw.tiff");
                                var success = await RawService.PreProcessAsync(captured.InputPath, rawTiff, s =>
                                {
                                    captured.Log += s;
                                    _onItemUpdated?.Invoke(captured);
                                }, ct,
                                highlightMode: captured.Options.DngHighlightMode,
                                threads: captured.Options.Threads);   // 2026-08-15: UI 线程选项生效
                                if (success)
                                {
                                    // ⚠⚠ 2026-09-20（审查修正）：**这里不要写死色域** —— primaries 紧接着会按
                                    //   目标容器的 HDR 承载能力重新取值（bt2020 / bt709，见下方几行）。
                                    //   写死会让日志与事实**不符**：实测目标 avif 时实际是 `bt2020`，
                                    //   而本行却报「色彩空间: BT.709 + 线性传输」——正是本仓最忌讳的「宣称≠交付」。
                                    //   ⇒ 本行只报「已完成预处理」，具体标记交给紧随其后的那条日志。
                                    captured.Log += "[RAW] ✅ 预处理完成，使用线性 TIFF 继续编码（色彩标记见下一条）。\n";
                                    captured.InputPath = rawTiff;
                                    // ── 输入声明：**与用户是否选目标无关，必须总是写**（2026-10-04 修）──────────
                                    // 中间件是 dngtool 的去马赛克**线性**输出，原色是**相机原生**（宽，无标准名）
                                    // ⇒ 输入侧取最接近的宽色域 bt2020 作工作空间近似（比 bt709 少削色）。
                                    // ⚠ 这一对只喂输入侧（`BuildColorArgsSplit`）与引擎的源描述符，**不**兼作输出目标。
                                    // ⚠⚠ 必须在用户判定**之外**：用户选了目标时中间件**仍是线性**，不写它就会退回
                                    //   「探测文件 ⇒ 无色彩标签 ⇒ 惯例假定 sRGB」那条老路（置信度不够 ⇒ 计划直通）。
                                    captured.Options.ColorSourceDeclPrimaries = "bt2020";
                                    captured.Options.ColorSourceDeclTrc = "linear";

                                    // ── 用户是否**已经**选了目标色域（2026-10-04 修，第二版）────────────────
                                    // ⚠⚠ **三轴并列、与是否高级模式无关**：`ColorPrimaries` / `ColorTrc` /
                                    //   `ColorSpace` 任一被用户显式指定 ⇒ 判「用户已选」⇒ RAW 默认块**不得触发**。
                                    //   历史坑（两个方向都踩过，2026-10-04 复查定案）：
                                    //     ① 旧版只查 `UseAdvancedColorParameters` + `ColorPrimaries`/`ColorTrc`，
                                    //        **漏了非高级模式下的 `ColorSpace`**（CLI `--color-space sRGB`、GUI 友好名
                                    //        走的正是这条）⇒ 用户选择被静默覆盖（实测交付 `bt2020 + sRGB`，用户要 bt709）。
                                    //     ② 补 `ColorSpace` 时又用 `UseAdvancedColorParameters ? … : …` **二选一**，
                                    //        ⇒ 把 `ColorPrimaries`/`ColorTrc` 那一维在非高级模式下**删掉了**
                                    //        ⇒ CLI `--color-primaries bt709`（`CliParser` 只赋值、**不置**高级标志）照样被覆盖。
                                    //   ⇒ 最终形态：三轴 **或** 关系，不分模式。
                                    bool rawUserChoseTarget =
                                        !string.IsNullOrWhiteSpace(captured.Options.ColorPrimaries)
                                        || !string.IsNullOrWhiteSpace(captured.Options.ColorTrc)
                                        || (!string.IsNullOrWhiteSpace(captured.Options.ColorSpace)
                                            && !captured.Options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase));

                                    // ── ⚠⚠ 2026-10-04 修（P1：`rec709` 被**静默忽略**）──────────────────────
                                    //   上面 `rawUserChoseTarget` 门挡住的**整块**默认档逻辑，其中包含
                                    //   `--raw-color-target rec709`（"强制 bt709 + sRGB，覆盖容器能力"）。
                                    //   后果（实测）：`--raw-color-target rec709 --color-space sRGB` 时
                                    //   日志只说「用户已指定目标色域（sRGB）⇒ 保留用户配置」，**一个字都不提 rec709**
                                    //   ⇒ 用户以为 `rec709` 生效了，实际被丢弃 —— 正是本仓最忌的「静默丢弃不点名」，
                                    //   且与 `CliParser` 的帮助文本（"覆盖容器能力"）**矛盾**。
                                    //   语义裁定：**用户显式目标优先**（既有三轴设计，不改），但**必须点名冲突**。
                                    //   ⚠ 只 `rec709` 有冲突：`rec2020` 就是默认档（同值），`auto` 的作用域只是
                                    //     TIFF 那条 SDR 例外档（用户已给目标时本就不参与）⇒ 二者不点名（避免噪声）。
                                    string? rawModeEarly = captured.Options.RawColorTarget;
                                    if (rawUserChoseTarget
                                        && string.Equals(rawModeEarly, "rec709", StringComparison.OrdinalIgnoreCase))
                                    {
                                        captured.Log += "[RAW] ⚠️ --raw-color-target rec709 与显式目标色域**同时给出**："
                                                      + "按「用户显式目标优先」处理 ⇒ **rec709 本次不生效**"
                                                      + "（若确实要强制 bt709+sRGB，请**去掉**显式目标色域参数）\n";
                                    }

                                    // ── TIFF 例外档的**位深默认**：RAW→TIFF 默认 **16-bit**（2026-10-04 用户裁定）──
                                    //   TIFF 例外档的用意是「广色域**保真**」：Rec.2020 + SDR 曲线，且 **16-bit**
                                    //   以留住 dngtool 中间件的 16-bit 线性数据。
                                    //   ⚠ 此前 16-bit 只是**恰好**（`TargetBitDepth` 跟随源探测位深）—— 换一台
                                    //     12-bit 的机器就会变成 12-bit ⇒ 不保证。此处**显式钉住**。
                                    //   ⚠ 仅在用户**未指定**位深时生效（用户指定优先）；⚠ 与是否选了目标**无关** ——
                                    //     位深默认属于**容器/中间件保真**，不属于目标色域那条轴。
                                    if (captured.Options.BitDepth == null
                                        && captured.Options.Format.Trim().TrimStart('.').ToLowerInvariant() is "tiff" or "tif")
                                        captured.Options.BitDepth = 16;

                                    if (!rawUserChoseTarget)
                                    {
                                        // 用户未指定目标色域时，应用 RAW 预处理默认值。
                                        // ⚠⚠ 2026-09-20（本轮）：primaries 由「**恒** bt709」改为
                                        //   **按目标容器的 HDR 承载能力**选：
                                        //     · 目标能承载 HDR ⇒ **BT.2020** —— 相机 RAW 的色域远大于 BT.709，
                                        //       标 bt709 等于把广色域**白压**成窄色域；
                                        //     · 否则 ⇒ BT.709（SDR 容器的诚实标定）。
                                        //   判据与规划层**同一个函数**（`CanHdrWith` 叠位深前提，即 `CanCarryHdrAt`
                                        //   的内联形式）⇒ 不引入第二份判据。JPEG 族**开启 GainMap** 时算「能承载 HDR」
                                        //   （Ultra HDR = SDR 底片 + 增益层）；**关掉时仍按 SDR 容器**处理。
                                        // ⚠ `ColorTrc` 恒为 `linear` 不变：dngtool 的 `-d` 输出的就是线性 RGB，
                                        //   本次改动**只动 primaries**。
                                        // ── 输出目标三档（用户未选时的默认）──────────────────────────
                                        //   ① 容器能承载 HDR **传递**（avif / jxl / png-16bit…）⇒ **Rec.2020 + PQ**
                                        //   ② 否则若是 **TIFF**（唯一例外，ICC 可描述广色域但无 HDR 传递）⇒ **Rec.2020 + SDR**
                                        //   ③ 其余（webp / jpg / jpegli / gif / jxr / ppm…）= 默认路线 ⇒ **bt709 + sRGB**
                                        // ⚠ 判据用 `CanHdr`（**不**叠加 GainMap）：Ultra HDR 的**底图**必须是 SDR，
                                        //   把 jpg+GainMap 算进 HDR 档会让底图变成 PQ（语义错）。
                                        // ⚠ 仍保留 `GetFormatColorCapabilities` 那道能力钳制作安全网（单一真值）。
                                        var rawCaps = ColorMapping.ColorMappingEngine.CapsFor(captured.Options.Format);
                                        // ⚠⚠ 判「能否承载 HDR 传递」必须用**实际交付档**，不是"请求档 ?? 8"
                                        //   （2026-10-04 审查 线D P1-2 修）：实际交付位深**跟随源**
                                        //   （`ColorIntentFactory` 的 `TargetBitDepth = Min(BitDepth ?? 源位深, 容器上限)`），
                                        //   而 RAW 中间件恒 16-bit（dngtool `-6`）⇒ 用户不指定位深时实际就是 16
                                        //   （再按容器上限 clamp）。旧写法 `BitDepth ?? 8` 把 png/jxl 判成 8-bit
                                        //   ⇒「不能承载 HDR 传递」⇒ 默认档降到 `bt709/sRGB`，而产物实际是
                                        //   `rgb48be`(16-bit) ⇒ **16-bit PNG 被标 SDR**（与注释「png-16bit ⇒ Rec.2020+PQ」、
                                        //   文档 `RAW2TIF §7.1`、以及本函数日志自己写的"位深=跟随源"三处矛盾）。
                                        //   实测：ARW→PNG 默认档旧 = `rgb48be, bt709`；修后 = `rgb48be, smpte2084, bt2020`。
                                        int rawTargetBitDepth = captured.Options.BitDepth ?? Math.Min(16, rawCaps.MaxBitDepth);
                                        // ⚠ `CapsFor` 返回**非空** `FormatColorCaps`（见 `ColorTransformPlan.cs:615` 签名）
                                        //   ⇒ 原先的 `rawCaps != null` 是**死判据**（恒真），2026-10-04 删除。
                                        bool rawCanHdrTransfer = rawCaps.CanHdr
                                            && (!rawCaps.HdrRequiresHighBitDepth || rawTargetBitDepth > 8);
                                        // ⚠ **不写 `?? ""`**：`FfmpegOptions.Format` 声明为非空（`= "jpg"`），
                                        //   而 `x ?? ""` 会让 Roslyn **反推 `x` 可能为空**并向后传播 ⇒ 凭空产生
                                        //   5 条 CS8602（2026-10-04 查明的根因，见 `RAW2TIF_QA_DEFECT §7.5`）。
                                        var rawFmtTok = captured.Options.Format.Trim().TrimStart('.').ToLowerInvariant();
                                        bool rawIsTiff = rawFmtTok is "tiff" or "tif";

                                        // ── 输出色域模式（`--raw-color-target`，2026-10-04 新增，三档）────────────
                                        //   ① rec2020（**默认**）= 上面那三档，一字不改（"Rec.2020 表达优先"）
                                        //   ② auto   = 在默认档之上叠加**内容判定**：该图实际用色未超出 bt709 范围时不用 Rec2020
                                        //   ③ rec709 = 强制 bt709 + sRGB（最大兼容性），覆盖容器能力
                                        // ⚠⚠ `auto` 的作用域**仅限「SDR 传递的 bt2020 档位」**（当前 = TIFF 例外档）：
                                        //   HDR 传递档（bt2020 + PQ）**不参与** —— bt2020 是 BT.2100/PQ 的组成部分，
                                        //   只降原色会产生 `bt709 + PQ` 这个合法但**非标准**的组合
                                        //   ⇒ 2026-10-04 用户裁定：HDR 档保持 bt2020 + PQ。
                                        // ⚠ 未知取值**必须点名**再回退默认档：静默回退就是"看起来配置过"的假象
                                        //   （与 `--color-gamut-map` 同口径）。
                                        // ⚠⚠ 本块每条日志**必须写成单行**：`_probe-cjk-hardcode-scan` 的桶判据看
                                        //   "第一个命中字面量"，而折行后后续行不再带 sink 标记 ⇒ 会被误计入 CsUi
                                        //   （余量 0，fail-closed）。单行且以 `[RAW]` 开头 ⇒ 恒落 TAG 桶（不参与判据）。
                                        // ⚠ 实测失败时**保守保持 bt2020**（三态：不把"测不出"当成"装得下"）。
                                        bool rawGamutDowngrade = false;
                                        string rawTargetMode = captured.Options.RawColorTarget ?? "rec2020";
                                        if (rawTargetMode == "rec709")
                                        {
                                            rawGamutDowngrade = true;
                                            captured.Log += "[RAW] 色域模式=rec709（强制 bt709 + sRGB，最大兼容性；覆盖容器能力）\n";
                                        }
                                        else if (rawTargetMode == "auto")
                                        {
                                            if (rawCanHdrTransfer)
                                            {
                                                captured.Log += "[RAW] 色域模式=auto：本档位为 HDR 传递档（bt2020 属 BT.2100 组成部分）⇒ 不降级；如需强制窄色域请用 --raw-color-target rec709 或显式指定目标色域\n";
                                            }
                                            else if (!rawIsTiff)
                                            {
                                                captured.Log += "[RAW] 色域模式=auto：本档位本就为 bt709（该容器不承载 Rec.2020）⇒ 无需判定\n";
                                            }
                                            else
                                            {
                                                var fit = await ColorMapping.RawGamutFit.MeasureAsync(rawTiff, ct).ConfigureAwait(false);
                                                if (!fit.Measurable)
                                                {
                                                    captured.Log += $"[RAW] 色域模式=auto：用色实测不可用（{fit.Reason}）⇒ 保守保持 bt2020（不猜）\n";
                                                }
                                                else
                                                {
                                                    rawGamutDowngrade = fit.FitsIn709;
                                                    captured.Log += $"[RAW] 色域模式=auto 用色实测：抽样 {fit.SampledPixels} 像素，地板={fit.LuminanceFloor:F5}，超出 bt709={fit.FracOutOf709:P4}（阈值<{ColorMapping.RawGamutFit.FracThreshold:P2}），最负超出={fit.MaxNegExcursion:F5}（阈值>{ColorMapping.RawGamutFit.NegThreshold:F4}）⇒ {(fit.FitsIn709 ? "装在 bt709 内 ⇒ 本档位降为 bt709 + sRGB" : "超出 bt709 ⇒ 保持 bt2020")}\n";
                                                }
                                            }
                                        }
                                        else if (rawTargetMode != "rec2020")
                                        {
                                            captured.Log += $"[RAW] ⚠️ 未知色域模式「{rawTargetMode}」⇒ 按默认 rec2020（Rec.2020 表达优先）处理\n";
                                        }

                                        captured.Options.ColorPrimaries =
                                            rawGamutDowngrade ? "bt709"
                                            : (rawCanHdrTransfer || rawIsTiff) ? "bt2020" : "bt709";
                                        captured.Options.ColorTrc = rawCanHdrTransfer ? "pq" : "srgb";
                                        captured.Options.UseAdvancedColorParameters = true;  // 关键：启用高级模式让 CP/CT 生效
                                        // ⚠⚠ 2026-09-20：**必须同时打上「这是程序默认、不是用户意图」的标志** ——
                                        //   否则 `ColorIntentFactory` 会把「`ColorTrc` 非空」判成 `targetExplicit`
                                        //   ⇒ `it.Target` 非 null ⇒ 规划层「无色彩通路容器（GIF/JXR）⇒ 钉成 sRGB」
                                        //   那条**不生效** ⇒ **RAW → GIF 被契约拒绝**（实测；而普通 PNG → GIF 正常）。
                                        //   ⚠ 2026-10-03：`ColorIntentFactory` 已按 `caps.HasColorPath` 细化该判据 ——
                                        //   有色彩通路的容器判显式（让目标真正生效）、无通路的仍留非显式（保住上面那条）。
                                        captured.Options.ColorFromRawDefault = true;
                                        captured.Log += $"[RAW] 未指定高级色彩参数 ⇒ 输入声明 {captured.Options.ColorSourceDeclPrimaries}/{captured.Options.ColorSourceDeclTrc}；"
                                            + $"输出目标 {captured.Options.ColorPrimaries}/{captured.Options.ColorTrc}"
                                            + $"（目标 {captured.Options.Format}：能承载 HDR 传递={rawCanHdrTransfer}，TIFF 例外={rawIsTiff}，"
                                            + $"位深={captured.Options.BitDepth?.ToString() ?? "跟随源"}，判据档={rawTargetBitDepth}）\n";
                                    }
                                    else
                                    {
                                        captured.Log += $"[RAW] 用户已指定目标色域（{captured.Options.ColorSpace ?? captured.Options.ColorPrimaries}）⇒ 保留用户配置；"
                                            + $"输入声明已按线性中间件补齐为 {captured.Options.ColorSourceDeclPrimaries}/{captured.Options.ColorSourceDeclTrc}\n";
                                    }
                                }
                                else
                                {
                                    // 可用性诚实原则：dngtool 预处理失败 → 直接报错，不回退 ffmpeg 直解（会产出错色）。
                                    throw new InvalidOperationException(
                                        "RAW 去马赛克预处理失败（dngtool）。不回退 ffmpeg 直解以避免错色输出；请检查文件是否损坏或 dngtool 是否正常工作。");
                                }
                                _onItemUpdated?.Invoke(captured);
                            }

                            // ── 图片最长边限制：统一预处理阶段缩放（所有后端共用）──
                            // 在格式分发前插入：若超限则先用 ffmpeg 缩放到临时文件，再走常规转码路线
                            // ⚠ 多帧输入（GIF/APNG/动图 JXL/**动图 webp**/动画 AVIF）必须跳过：单帧 PNG
                            // 中间件会丢掉全部动画帧，且 inputExt 仍路由到动图两步法导致错乱（P1-N6）——
                            // 缩放由各动图管线自身的 AnimationScaleW 等参数处理。
                            // ⚠ `#136` 收尾轮：判据由 `IsAnimated(captured)` 改为**直接消费**上面单点求值的
                            //   `inputMayHaveMultipleFrames`。理由：预缩放要跳过的**只是**「**输入**有多帧」
                            //   这一种情形，而旧判据里那条「**输出**容器是 gif/apng ⇒ 跳预缩放」对预缩放点
                            //   是**多余的**（静态输入 → 动图输出并不需要跳过预缩放）。B2 已删掉
                            //   `InputMayHaveMultipleFramesAsync` 的「轴 A」（输出容器）⇒ 它现在**纯粹是
                            //   输入侧** ⇒ 正好就是这里要问的问题。
                            //   ⚠ 动图 `.webp` 由此**一并被正确跳过**：旧判据对 `.webp` 恒 false ⇒ 动图 webp
                            //   会进本块，靠「预缩放因 image2 固定文件名失败 + 容忍分支不改写 InputPath」**碰巧**
                            //   没坏（实测见 `verify-engine-firstframe.ps1` 的更正段），不是「被跳过」。
                            var maxDimInputExt = Path.GetExtension(captured.InputPath).ToLowerInvariant();
                            var maxDimSkipAnimated = inputMayHaveMultipleFrames;
                            // ⚠ 本块现在只对**静态确定**的 avif 才会执行（多帧/不确定的 avif 已由上面
                            //   `inputMayHaveMultipleFrames` 直接判 true ⇒ 本块短路）。保留它是为了不打断
                            //   `verify-avif-anim-probe.ps1` 钉住的「三处消费 determinate」的点名式载体；
                            //   其探测结论与 `InputHasMultipleFramesAsync` 的同源探测一致（同一路径同一命令）。
                            if (!maxDimSkipAnimated && maxDimInputExt == ".avif")
                            {
                                var (avifProbeAnimated, avifProbeDeterminate) = await IsAnimatedAvifAsync(
                                    captured.InputPath, s => { captured.Log += s; _onItemUpdated?.Invoke(captured); }, ct);
                                // `#136`：探测**不确定** ⇒ 保守按**多帧**（跳过预缩放 = 安全方向），绝不静默当静态。
                                maxDimSkipAnimated = avifProbeAnimated || !avifProbeDeterminate;
                            }
                            if (captured.Options.EnableMaxDimension && captured.Options.MaxDimension > 0
                                && !maxDimSkipAnimated)
                            {
                                var maxFilter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(captured.Options, captured.InputPath);
                                if (!string.IsNullOrEmpty(maxFilter))
                                {
                                    captured.Log += $"[MaxDimension] 检测到超限，先使用 ffmpeg 缩放到临时文件再转码\n";
                                    _onItemUpdated?.Invoke(captured);

                                    var scaleDir = Path.Combine(PlatformServices.GetTempDir(), $"scale_{Guid.NewGuid():N}");
                                    Directory.CreateDirectory(scaleDir);
                                    tempDirsToCleanup.Add(scaleDir);
                                    // 中间格式选择：JXL 输入用 JXL 无损中转（-distance 0），保留 HDR/广色域/
                                    // alpha 色彩元数据——PNG 无法表达 PQ/HLG TRC，会造成高光削顶；
                                    // 且缩放产物仍是合法 JXL，DetectType 可识别，JXL 专用管线（djxl→cjxl/
                                    // cjpegli 管道）路由不中断。其余输入维持 PNG（SDR 无损且解码快）。
                                    var inputIsJxl = Path.GetExtension(captured.InputPath).Equals(".jxl", StringComparison.OrdinalIgnoreCase);
                                    var scaledInput = Path.Combine(scaleDir, $"scaled_{Guid.NewGuid():N}{(inputIsJxl ? ".jxl" : ".png")}");

                                    // 直接手写最简单的缩放命令：输入色彩覆盖(可选) + scale 滤镜 + 无损中间输出
                                    // 注意：-i 前的色彩参数由 BuildColorArgsSplit 返回，但为避免依赖私有方法，
                                    // 这里直接使用最通用的写法：ffmpeg 自动推断输入色彩，仅追加 scale 滤镜
                                    var colorMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(captured.InputPath,
                                        s => { captured.Log += s; _onItemUpdated?.Invoke(captured); }, ct);
                                    string inputColorArgs = "";
                                    if (!string.IsNullOrEmpty(colorMeta.colorPrimaries))
                                        inputColorArgs += $"-color_primaries {colorMeta.colorPrimaries} ";
                                    if (!string.IsNullOrEmpty(colorMeta.colorTrc))
                                        inputColorArgs += $"-color_trc {colorMeta.colorTrc} ";

                                    var scaleArgs = inputIsJxl
                                        ? $"-y {inputColorArgs}-i \"{captured.InputPath}\" -vf \"{maxFilter}\" -distance 0 \"{scaledInput}\""
                                        : $"-y {inputColorArgs}-i \"{captured.InputPath}\" -vf \"{maxFilter}\" -compression_level 0 \"{scaledInput}\"";

                                    // 实际执行
                                    captured.Log += $"[cmd] ffmpeg {scaleArgs}\n";
                                    var scaleExit = await FfmpegRunner.RunAsync(scaleArgs, s =>
                                    {
                                        captured.Log += s;
                                        _onItemUpdated?.Invoke(captured);
                                    }, AppSettingsService.Current.FfmpegPath, ct);

                                    if (scaleExit != 0 && inputIsJxl)
                                    {
                                        // ffmpeg 构建缺 libjxl 编码器等 → 回退 PNG 中转（HDR 输入将削顶，日志明示）
                                        captured.Log += "[MaxDimension] ⚠️ JXL 中转缩放失败，回退 PNG 中转（HDR 输入可能削顶）\n";
                                        _onItemUpdated?.Invoke(captured);
                                        scaledInput = Path.Combine(scaleDir, $"scaled_{Guid.NewGuid():N}.png");
                                        var pngScaleArgs = $"-y {inputColorArgs}-i \"{captured.InputPath}\" -vf \"{maxFilter}\" -compression_level 0 \"{scaledInput}\"";
                                        captured.Log += $"[cmd] ffmpeg {pngScaleArgs}\n";
                                        scaleExit = await FfmpegRunner.RunAsync(pngScaleArgs, s =>
                                        {
                                            captured.Log += s;
                                            _onItemUpdated?.Invoke(captured);
                                        }, AppSettingsService.Current.FfmpegPath, ct);
                                    }

                                    if (scaleExit == 0 && File.Exists(scaledInput))
                                    {
                                        captured.InputPath = scaledInput;
                                        captured.Log += $"[MaxDimension] ✅ 预缩放完成，新输入: {Path.GetFileName(scaledInput)}\n";
                                    }
                                    else
                                    {
                                        captured.Log += $"[MaxDimension] ⚠️ 预缩放失败 (退出码 {scaleExit})，将尝试原图继续\n";
                                    }
                                    _onItemUpdated?.Invoke(captured);
                                }
                            }

                            // 色彩后置是否已在主分支跑完：下面那些专用通路（外部编码器 / JXL·JXR·AVIF·GIF）
                            // 不进入主分支的 `{ …色彩后置… }`，因此需要一个统一入口决定“补做还是显式报未参与”（P7a-3）。
                            bool colorPostStepsDone = false;

                            // ── cjxl 自动光子噪声：ISO 解析提到**分叉之前**（2026-09-30）────────────────
                            // 本链上共有三条 cjxl 出口：`:714` 的 `ProcessCjxlAsync`（直编/管道）、
                            // 引擎可走时的 `RawColorPipeline` cjxl 出口、以及 JXL·JXR 输入的两步法回退
                            // （`ProcessJxlInputAsync` / `FallbackDjxlDecodeToFile`）。三者传的是**同一份**
                            // `captured.Options`，而 `:714` 写成 `backend == Cjxl && !engineRoutable` ⇒
                            // 解析若内联在 `ProcessCjxlAsync` 里，**默认路线（引擎）就拿不到**：cjxl 的
                            // `--photon_noise_iso` 只吃浮点，收到未解析的 `auto` 会在读 stdin 之前退出 1、
                            // 零产物，写方表现为"管道正在被关闭"（实测 2026-09-30，`tests/output/t62/`）。
                            // 判据取「cjxl 可用 且 目标是 jxl」= 上面三条出口的并集（那两条回退各自还额外
                            // 要求 target 是 jxl），不另起第二份判定。
                            if (CjxlService.IsAvailable
                                && captured.Options.Format.Equals("jxl", StringComparison.OrdinalIgnoreCase))
                                await ResolveCjxlPhotonNoiseIsoAsync(captured, ct);

                            // ── FFmpeg libjxl 后端的能力点名（2026-10-02 新增）───────────────────────
                            // 实测本仓出货包 `ffmpeg -h encoder=libjxl`，其 AVOptions **只有**
                            //   -effort / -distance / -modular / -xyb 四项
                            // ⇒ `--photon_noise_iso` 与 `--progressive` 在这条后端上是**结构性不存在**
                            //   （不是"支持但没接"，所以补参数无用，只能点名 + 指路）。
                            // UI 侧这两只控件挂在 `JxlCjxlPanel` 内（仅 cjxl 后端可见）⇒ 手动操作碰不到；
                            // 但 CLI（`--cjxl-photon-noise-iso` / `--cjxl-progressive`）与预设回放能绕开
                            // 面板直接置位 ⇒ 必须在这里点名，否则用户拿到"勾了却没生效、日志一句都没有"
                            // 的静默丢弃 —— 正是本仓最忌讳的形状（COLOR_TRAPS P9）。
                            if (captured.Options.EncoderBackend != EncoderBackend.Cjxl
                                && captured.Options.Format.Equals("jxl", StringComparison.OrdinalIgnoreCase))
                            {
                                if (captured.Options.CjxlAutoPhotonNoise || captured.Options.CjxlPhotonNoiseIso > 0)
                                    captured.Log += "[jxl] ⚠ 光子噪声未生效：当前后端是 FFmpeg 的 libjxl，"
                                                  + "它不提供 --photon_noise_iso（实测 -h encoder=libjxl 仅含"
                                                  + " effort/distance/modular/xyb）⇒ 本次不合成噪声；"
                                                  + "需要该功能请把后端切到 cjxl\n";
                                if (captured.Options.CjxlProgressive)
                                    captured.Log += "[jxl] ⚠ --progressive 未生效：FFmpeg 的 libjxl 没有该选项"
                                                  + " ⇒ 本次不启用渐进；需要该功能请把后端切到 cjxl\n";
                            }

                            // GIF → AVIF：FFmpeg 编码器丢弃 alpha，走 avifenc 两步法保留透明通道。
                            // ⚠ 可用性判断**不得写在本条件里**（P7f）：曾经写成 `&& HasAvifencAvailable()`，
                            //   于是 avifenc 缺失时整个意图会静默落到后面的通用 ffmpeg 分支（上面注释自己写着“丢 alpha”），
                            //   用户却看到“已完成”⇒ 违反“可用性诚实”。现在意图总是路由到两步法，由方法内部显式失败。
                            if (inputExt == ".gif"
                                && captured.Options.Format.Equals("avif", StringComparison.OrdinalIgnoreCase))
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
                            else if (inputExt == ".jxr")
                            {
                                // JXR 输入：JxrDecApp 解码 → 根据目标格式选择编码器
                                await ProcessJxrInputAsync(captured, finalOutputPath, ct);
                            }
                            // Gain Map (Ultra HDR) JPEG：纯 C# GainMapEncoder + cjpegli
                            // ⚠ 2026-09-16：**引擎可走时由引擎出口执行**（RawColorPipeline 的 GainMap 分支，
                            //   与这里共用同一份解码串与参数公式 GainMapJpegCodec）⇒ 本条专用管线降级为
                            //   **回退路径**：显式 --color-engine legacy，或引擎不可走（如 GainMap + 最长边缩放）。
                            //   判据与主分支用同一个 ShouldRoute，避免"路由说能走、分派说不能走"两套口径。
                            else if (captured.Options.JpegGainMap
                                && (captured.Options.Format == "jpg" || captured.Options.Format == "jpeg"
                                    || captured.Options.Format == "avif")
                                && !ColorMapping.ColorEngineRouter.ShouldRoute(
                                       captured.Options, finalOutputPath, inputMayHaveMultipleFrames))
                            {
                                // ⚠ 2026-10-02 起含 `avif`：avif 的增益图走 ISO-BMFF `tmap` 派生项
                                //   （容器由 `GainMapEncoder.EncodeAsync(container: "avif")` 分流）。
                                //   不加这一支 ⇒ `--color-engine legacy` 下 avif+增益图会**静默丢掉增益图**
                                //   （落到普通 avif 编码），违反"不得静默降级"。
                                await ProcessGainMapJpegAsync(captured, finalOutputPath, ct);
                            }
                            else if (backend == EncoderBackend.Cjxl && !engineRoutable)
                            {
                                // ⚠ 2026-09-17（cjxl 出口轮）：加 `!engineRoutable` 是本轮**唯一的分派改动**。
                                //   原来无条件走这里 ⇒ 引擎**根本轮不到**（本分支在引擎插入点之前），
                                //   于是"放宽路由门"这件事会完全无效（路由放行了、分派却把任务截走）。
                                //   现在：引擎可走 ⇒ 落到下面的 `else`（引擎插入点）⇒ 计划位
                                //   `ExternalEncoderExit` 让 `RawColorPipeline` 走 cjxl 出口；
                                //   引擎不可走 ⇒ 照旧走专用管线（与改动前逐字相同）。
                                //   `engineRoutable` 来自**同一个** `ShouldRoute`（单点求值，不构成第二份判据）。
                                await ProcessCjxlAsync(captured, finalOutputPath, ct);
                            }
                            else if (backend == EncoderBackend.Cjpegli)
                            {
                                await ProcessCjpegliAsync(captured, finalOutputPath, ct);
                            }
                            else if (backend == EncoderBackend.Jxr && !engineRoutable)
                            {
                                // ⚠ 2026-09-17（JXR 出口轮）：加 `!engineRoutable` 与上面 Cjxl 那条**同款**。
                                //   本分支在引擎插入点之前 ⇒ 不加这一条，引擎**根本轮不到**（路由放行了、
                                //   分派却把任务截走）。引擎可走 ⇒ 落到下面的 `else` ⇒ 计划位
                                //   `ExternalEncoderExit` 让 `RawColorPipeline` 走 JXR 出口
                                //   （BMP/TIFF 中转 + JxrEncApp）；引擎不可走 ⇒ 照旧走专用管线。
                                //   `engineRoutable` 来自**同一个** `ShouldRoute`（单点求值，不是第二份判据）。
                                await ProcessJxrAsync(captured, finalOutputPath, ct);
                            }
                            else if (backend == EncoderBackend.Dng)
                            {
                                await ProcessDngAsync(captured, finalOutputPath, ct);
                            }
                            else
                            {
                                // P7a 引擎插入点：只替代“这一次 ffmpeg 运行”，后面的后置步骤两路共用
                                var eng = await TryRunColorEngineAsync(
                                    captured, captured.InputPath, finalOutputPath, inputMayHaveMultipleFrames, ct);
                                var (engUsed, engExit, engPlan) = eng;
                                int exitCode;
                                // ⚠⚠ 2026-09-20：**非 JPEG 目标 + `--jpeg-gain-map` ⇒ 契约拒绝（`exit 91`，同码）**。
                                //   为什么必须在这里补：BMP/DNG/PPM 这类「**连色彩通路都没有**」的出口，
                                //   `ColorIntentFactory` 返回 null ⇒ 规划层的 `SupportsGainMap` 闸**根本不运行**
                                //   （见本文件中解释「规划层的 `SupportsGainMap` 闸**根本不运行**」的那条注释）⇒ 请求被**静默丢弃**：
                                //   L3 实测 20 例 `gainmap-absent`（`-f bmp --jpeg-gain-map true`）产物是**普通 BMP**、
                                //   日志**无任何点名**、末行仍报「完成 1 项, 失败 0 项」——正是本仓最忌讳的「看起来配置过」。
                                //   ⚠ 判据带 `!ShouldRoute`：引擎**可走**的非 JPEG（如 `-f png`）仍由规划层拒绝
                                //   （现状逐字不变），本条只补「引擎不可走 ⇒ 规划层轮不到」的那一半。
                                //   文案/替代方案与规划层 `ColorStrategyPlanning.cs:181-193` 同源同义（单一真源在那边）。
                                bool gainMapOnUnsupportedTarget = captured.Options.JpegGainMap
                                    && !(captured.Options.Format == "jpg" || captured.Options.Format == "jpeg")
                                    && !ColorMapping.ColorEngineRouter.ShouldRoute(
                                           captured.Options, finalOutputPath, inputMayHaveMultipleFrames);
                                if (gainMapOnUnsupportedTarget)
                                {
                                    captured.Log += $"[色彩裁决] ❌ 契约拒绝（不出产物）：增益图（GainMap）不适用于 {captured.Options.Format}：它依赖 JPEG 的 APP2/XMP+MPF 结构\n";
                                    captured.Log += "[色彩裁决]    建议：要保留 HDR：输出改用 AVIF/JXL（原生存 PQ/HLG）；或把目标改为 JPEG 并开启增益图\n";
                                    captured.Log += "[色彩裁决]    可替代方案：jpg（JPEG 族是唯一能承载 GainMap 的容器）· avif · jxl（原生存 PQ/HLG）\n";
                                    _onItemUpdated?.Invoke(captured);
                                    exitCode = 91;
                                }
                                else if (engUsed)
                                {
                                    exitCode = engExit;
                                }
                                else
                                {
                                    // ════════════════════════════════════════════════════════════════════
                                    //  S6：传统管线消费与引擎**同一份**裁决（规划 §3 P3「单一消费接口」）
                                    // ════════════════════════════════════════════════════════════════════
                                    // 维护者裁定：「拒绝，用自研管线完成色彩映射」⇒ legacy 必须**听裁决**：
                                    //   · Reject              ⇒ **不产出**（非零退出码 + Reason/Advice/Alternatives）；
                                    //   · ProceedWithDegradation ⇒ 照常产出 + 逐条打降级码；
                                    //   · Proceed             ⇒ 不变。
                                    // 为什么挂在这里：本 else 体是两条入口的**唯一汇合点** ——
                                    //   入口①显式 `--color-engine legacy`（`TryRunColorEngineAsync:774` 早退）；
                                    //   入口②auto 默认 + 引擎不可走（`ColorEngineRouter.BlockReason` 非空那条早退）；
                                    //   另有第三种「计划为直通/保真」（见本文件中「直通 或 **超广色域保真**」那段）
//     ⇒ 已经带回了 `engPlan`，白捡一个裁决。
                                    // 为什么不在 block 分支（`ColorEngineRouter.BlockReason` 非空那条）内部：它在算 intent **之前**就 return，
                                    //   且 explicit-engine 那条是「硬失败 90」（路由前置条件不满足 ≠ 任务级终局，
                                    //   两者语义不同，见 `ColorVerdict.cs` 的 ⚠）；且它覆盖不到入口①。
                                    // ⚠ **不新增任何 Reject 格**：这里只**消费**规划层既有裁决，
                                    //   `gif`/`jxr`/`ppm` 这类 `HasColorPath=false` 的格式在规划层的结局**没有变**
                                    //   （容器不能**携带**色彩信息 ≠ 不能转换，见 `COLOR_ENGINE_INTERFACE_PLAN` §5-B）。
                                    // ⚠ 算不出裁决时（`ResolveLegacyVerdictPlan` 返回 null：DNG/PPM/BMP 这类
                                    //   **连色彩通路都没有**的出口，`ColorIntentFactory` 对它们返回 null）
                                    //   **保持既有行为** —— 传统管线是它们的正常归属，不猜一个裁决出来。
                                    var legacyPlan = engPlan
                                        ?? FfmpegCommandBuilder.ResolveLegacyVerdictPlan(captured.Options, captured.InputPath);
                                    var legacyVerdict = legacyPlan?.Verdict;
                                    bool legacyReject = legacyVerdict?.Kind == ColorMapping.VerdictKind.Reject;
                                    // ⚠ **只在"裁决由本消费点算出来"时打印**（`engPlan == null`）。
                                    //   `engPlan != null` 只有一种来源：「直通 或 **超广色域保真**」那段——
                                    //   那条路**引擎侧已经打过同一份裁决与同一批降级码**（见本文件中「色彩后置全套」那段），
                                    //   再打一遍就是同一条损失在日志里出现两次（不同标签，容易被误读成两处损失）。
                                    //   ⇒ 一次裁决只由**它的产出方**打印一次。
                                    //   （`legacyReject` 在 `engPlan != null` 时恒 false：引擎侧 `:837` 已把 Reject 拦下。）
                                    if (legacyVerdict != null && engPlan == null)
                                    {
                                        captured.Log += $"[色彩裁决] 裁决={legacyVerdict.Kind}\n";
                                        // 逐条打**稳定 Code**（`DegradationCodes.*`，契约；`Text` 只作人读补充）。
                                        // 格式与引擎侧 `降级（{Code}）：{Text}` **逐字一致**，只换日志标签（见 LogAlternatives 说明）。
                                        for (int i = 0; i < legacyVerdict.Degradations.Count; i++)
                                            captured.Log += $"[色彩裁决] ⚠️ 降级（{legacyVerdict.Degradations[i].Code}）：{legacyVerdict.Degradations[i].Text}\n";
                                        if (legacyReject)
                                        {
                                            captured.Log += $"[色彩裁决] ❌ 契约拒绝（不出产物）：{legacyVerdict.Reason}\n";
                                            if (!string.IsNullOrWhiteSpace(legacyVerdict.Advice))
                                                captured.Log += $"[色彩裁决]    建议：{legacyVerdict.Advice}\n";
                                            // 结构化替代路径（稳定 `AlternativeKind`）：格式与引擎侧 `可替代方案：` 逐字一致。
                                            LogAlternatives(s => captured.Log += s, legacyVerdict.Alternatives, "[色彩裁决]");
                                        }
                                    }
                                    if (legacyReject)
                                    {
                                        _onItemUpdated?.Invoke(captured);
                                        // 91 = 「契约拒绝」退出码，与引擎侧 Reject（`TryRunColorEngineAsync:845`）**同码**：
                                        // 同一条裁决在两条管线上应当给出同一个可机读结局，否则调用方无法统一处理。
                                        exitCode = 91;
                                    }
                                    else
                                    {
                                        var args = FfmpegCommandBuilder.BuildArguments(captured.Options, captured.InputPath, finalOutputPath);
                                        LogInjectedScale(captured, args);      // 扩展 ③：缩了要让用户看得见
                                        captured.Command = "ffmpeg " + args;
                                        captured.Log += $"[cmd] ffmpeg {args}\n";
                                        if (!string.IsNullOrWhiteSpace(captured.Options.FaithfulIccPath))
                                            captured.Log += $"[保真] 超广色域像素原样保真，ICC={Path.GetFileName(captured.Options.FaithfulIccPath)}\n";
                                        _onItemUpdated?.Invoke(captured);
                                        exitCode = await FfmpegRunner.RunAsync(args, s =>
                                        {
                                            captured.Log += s;
                                            _onItemUpdated?.Invoke(captured);
                                        }, AppSettingsService.Current.FfmpegPath, ct);
                                    }
                                }
                                captured.ExitCode = exitCode;
                                captured.Status = exitCode == 0 ? "已完成" : $"失败 (退出码 {exitCode})";

                                // DNG/RAW 输入失败时给出提示
                                if (exitCode != 0 && RawService.IsRawFile(captured.InputPath))
                                {
                                    captured.Log += "[RAW] ⚠️ 文件转换失败。可能原因：\n";
                                    captured.Log += "[RAW]   - Bayer 传感器原始数据需 dngtool 去马赛克（已自动尝试）\n";
                                    captured.Log += "[RAW]   - 文件损坏或格式不受支持\n";
                                    captured.Log += "[RAW]   - 请确保 dngtool 已放入 PLAN/artifacts/ 目录\n";
                                }

                                // ── 统一元数据恢复 ──
                                // FFmpeg 的 -map_metadata 0 在不同 muxer 下行为不一致（尤其是 ICC Profile、
                                // XMP 包、Exif IFD 子标签等），因此对所有输出格式都走 exiftool 恢复，
                                // 确保 ICC 色彩描述、XMP、Exif 等完整保留。AVIF muxer 尤为严重，
                                // 其他格式（TIFF/PNG/WebP/JXL）也存在不同程度的丢失。
                                if (exitCode == 0)
                                    await RestoreMetadataAsync(captured, finalOutputPath);

                                // ── 色彩后置全套（sBIT / cICP / 引擎 ICC / 裁决点 ICC / TIFF ICC / 保真 ICC）──
                                // 与链尾已登记的专用通路**共用同一入口** ⇒ 判据不再有两份（P7a-3 第三轮）。
                                await ApplyColorPostStepsAsync(captured, finalOutputPath, captured.Options,
                                    captured.InputPath, exitCode,
                                    engineUsed: engUsed, mainBranch: true, engPlan: engPlan, ct: ct);
                            colorPostStepsDone = true;   // 本分支已跑完全套色彩后置（元数据恢复/sBIT/cICP/ICC/保真）
                            }

                            // ── P7a-3：非主分支通路的色彩后置，**只对做过同源登记的执行** ──
                            // 登记过 ⇒ 这次的命令行确实由 FfmpegCommandBuilder 产出，按同源 options+输入复算裁决点是安全的；
                            // 没登记 ⇒ 命令行是别处自建的（外部编码器本体 / 两步法 filter_complex），
                            //   拿“按源文件推断的出口语义”去贴标签就是在赌（P7d 实测：同一内容两条路像素并不相同）
                            //   ⇒ 宁可只显式记录“裁决点未参与”，也绝不猜标签。
                            if (!colorPostStepsDone && File.Exists(finalOutputPath))
                            {
                                if (captured.ColorPostOptions != null && captured.ColorPostProbeInput != null)
                                    await ApplyColorPostStepsAsync(captured, finalOutputPath, captured.ColorPostOptions,
                                        captured.ColorPostProbeInput, captured.ExitCode ?? 0,
                                        engineUsed: false, mainBranch: false, engPlan: null, ct: ct);
                                else if ((captured.ExitCode ?? 0) == 0)
                                {
                                    captured.Log += "[色彩后置] ⚠️ 本通路的命令行不由 FfmpegCommandBuilder 产出 ⇒ 裁决点未参与（不猜标签、不补 ICC）；"
                                        + "如需完整色彩后置请改用默认 FFmpeg 路径，或按 P7a-3 先接同源登记\n";
                                    _onItemUpdated?.Invoke(captured);
                                }
                            }

                            // ── ExifTool 后处理 ──
                            // ⚠ 若隐私清理已被**并入元数据复制**（见 ExifToolService.CanAbsorbStrip：GPS/XMP 只可能
                            //   来自源文件复制 ⇒ 「复制时排除」与「先复制再剥掉」等价），就跳过这次单独调用，
                            //   省掉一次 exiftool 启动（实测单次启动 ~850ms，是本项后置链最大的固定开销）。
                            if (captured.ExitCode == 0
                                && captured.Options.MetadataMode == Models.MetadataMode.PreserveAll
                                && ExifToolService.NeedsProcessing(captured.Options)
                                && !ExifToolService.CanAbsorbStrip(captured.Options))
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
                                            },
                                            ct);
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
                            // ── 追加 .png 后缀（JXL/AVIF 兼容性）──
                            if (captured.ExitCode == 0 && captured.Options.AppendPngExtension)
                            {
                                var fmt = captured.Options.Format.ToLower();
                                if (fmt is "avif" or "jxl")
                                {
                                    try
                                    {
                                        var newPath = finalOutputPath + ".png";
                                        File.Move(finalOutputPath, newPath);
                                        finalOutputPath = newPath;
                                        captured.OutputPath = newPath;
                                        captured.Log += $"[rename] 已追加 .png 后缀: {Path.GetFileName(newPath)}\n";
                                    }
                                    catch (Exception ex)
                                    {
                                        captured.Log += $"[rename] ⚠️ 追加 .png 后缀失败: {ex.Message}\n";
                                    }
                                }
                            }
                            captured.CompletedAt = DateTimeOffset.UtcNow;
                        }
                        catch (OperationCanceledException)
                        {
                            captured.Status = "已停止";
                            captured.CompletedAt = DateTimeOffset.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            captured.Status = "失败: " + ex.Message;
                            captured.CompletedAt = DateTimeOffset.UtcNow;
                        }
                        finally
                        {
                            // P1-4：未成功的任务不留“看着像成功”的半成品（各专用通路都是直写最终路径 + 无条件 -y）
                            // 判据用**确证失败**而不是“不算成功”：状态/退出码有任一没落到位时宁可留下文件，也不误删有效产物。
                            if (captured.HasError || captured.IsStopped
                                || (captured.ExitCode.HasValue && captured.ExitCode.Value != 0))
                                TryDiscardPartialOutput(captured, runOutputPath, outputExistedBefore);

                            // 清理任务内创建的临时目录（RAW 预处理等）
                            foreach (var dir in tempDirsToCleanup)
                            {
                                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                            }
                            captured.InputPath = originalInputPath;   // P1-5：临时件已删，路径必须回到真实源文件
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

        /// <summary>
        /// S7：把**结构化**替代路径逐条打进日志（<see cref="ColorMapping.ColorAlternative"/>）。
        /// <para>
        /// 打的是稳定枚举 <c>AlternativeKind</c> + <c>Value</c>（契约），`Why` 只作人读补充
        /// —— 门禁**只允许**断言 Kind/Value，禁止断言自由文本（规划 §6.4）。
        /// 空集合 ⇒ 一行不打（不得打「无替代方案」之类的噪声）。
        /// </para>
        /// </summary>
        private static void LogAlternatives(Action<string> log, IReadOnlyList<ColorMapping.ColorAlternative>? alts)
            => LogAlternatives(log, alts, "[色彩引擎]");

        /// <summary>
        /// 同 <see cref="LogAlternatives(Action{string}, IReadOnlyList{ColorMapping.ColorAlternative})"/>，
        /// 但可换日志标签。
        /// <para>
        /// **为什么需要换标签**（S6）：传统管线消费裁决时必须打 <c>[色彩裁决]</c> 而**不能**打
        /// <c>[色彩引擎]</c> —— `verify-color-wiring.ps1:110` 与 `verify-gainmap-engine.ps1:116` 都断言
        /// 「显式 legacy 一行 <c>色彩引擎</c> 日志都不该出现（逃生门零污染）」。裁决是**规划层**产物、
        /// 不是引擎**执行**产物，标签分开后该断言继续为真且仍有牙（它拦的是"legacy 偷偷走引擎执行"）。
        /// </para>
        /// </summary>
        private static void LogAlternatives(Action<string> log, IReadOnlyList<ColorMapping.ColorAlternative>? alts, string tag)
        {
            if (alts == null || alts.Count == 0) return;
            for (int i = 0; i < alts.Count; i++)
                log($"{tag}    可替代方案：{alts[i].Kind}/{alts[i].Value} —— {alts[i].Why}\n");
        }

        /// <summary>
        /// P7a：色彩引擎执行点。**只替换“这一次 ffmpeg 运行”**，其后的元数据恢复 / PNG sBIT·cICP / TIFF ICC
        /// 等后置步骤与 legacy 完全共用（否则就是“接了引擎却少了标签”的半品）。
        /// used=false ⇒ 未请求引擎（或引擎结论是“什么都不用改”），调用方照旧走 legacy；
        /// used=true ⇒ 引擎已负责本次像素，exitCode 即结果。
        /// ⚠ 用户显式选了 engine 而条件不满足 ⇒ **显式失败**并说明原因，绝不静默改走 legacy（可用性诚实原则）。
        /// <para>
        /// ⚠ `#136` 切片 B：<paramref name="inputMayHaveMultipleFrames"/> 由**调用方**传入，本方法**不再自己求值**。
        /// 理由：该判据现在会真的探测 `.webp`/`.avif`（有代价），且 `ProcessAsync` 的 `ShouldRoute`、GainMap
        /// 专用分派、本方法三处**必须**用同一份结果 —— 各自求值就会出现「路由说能走、分派说不能走」两套口径。
        /// </para>
        /// </summary>
        private async Task<(bool used, int exitCode, ColorMapping.ColorTransformPlan? plan)> TryRunColorEngineAsync(
            QueueItem item, string inputPath, string outputPath, bool inputMayHaveMultipleFrames, CancellationToken ct)
        {
            if (!ColorMapping.ColorEngineRouter.Requested(item.Options)) return (false, 0, null);
            void Log(string s) { item.Log += s; _onItemUpdated?.Invoke(item); }

            // ⚠ 2026-09-17（S2）：`block` 已是**结构化**的 `EngineBlock{Code,Text,Alternatives}`
            //   （规划 §5-D）。日志**同时**打 `block.Code` 与 `block.Text`：前者是稳定 Code（供门禁/GUI 判定），
            //   后者与改动前的文案**逐字相同**（有门禁/日志在匹配它，如 `本次不适用引擎` / `按要求应走引擎`）
            //   ⇒ 两个都要留，不得只留其一。
            //   `block.Alternatives` 见下方逐条打印（S7 起有消费者）。
            var block = ColorMapping.ColorEngineRouter.BlockReason(
                item.Options, outputPath, inputMayHaveMultipleFrames);
            if (block != null)
            {
                // **2026-09-16**：区分「用户显式要求引擎」与「引擎是默认」——
                //  • 显式要求（--color-engine engine）⇒ 仍**硬失败并点名**（可用性诚实：绝不静默回退）；
                //  • 默认（未指定）⇒ 该格式本就不在引擎的编码出口内（JXR/DNG/动画），
                //    传统管线是它的**正常归属** ⇒ 记一条说明后继续走传统管线。
                //    ⚠ 2026-09-17 更正（第二次）：本清单此前还列着 **AVIF**/**GainMap**/**GIF**，三者
                //    现已全部接入引擎（AVIF：`ImageEncoderArgs.IsEngineEncodable` 白名单 +
                //      `ImageEncoderArgs.BuildAvifOptions`；GainMap：`RawColorPipeline` 的 GainMap 出口；
                //      GIF：白名单 + `BuildGifFilterChain`/`BuildGifOptions` + 出口补 alpha）
                //      ⇒ 已删，只剩真正结构上不属于引擎的项
                //      （GainMap + 最长边缩放的组合仍显式拦，见 Router）。
                //    ⚠ 同日第三次更正：**JXL** 也从本清单删除，但只删一半 ——
                //      `jxl` 的 **Ffmpeg 后端**已进白名单（`ImageEncoderArgs.BuildJxlOptions`），
                //      而 **Cjxl 后端**仍属「格式在出口内、后端不在」⇒ 它由 `BlockReason` 的
                //      `backend != Ffmpeg` 那道门（在**白名单检查之后**）拦住，理由是"用户选了外部编码器后端"，
                //      不是本清单的"格式不在出口内" —— 两种拒绝理由不同，不要混记。
                //    ⚠ 同日第四次更正（cjxl 出口轮）：上面那半**也补上了** —— `BlockReason` 现在对
                //      `jxl + Cjxl` **放行**（`ExternalEncoderExit` 计划位），故 jxl 的两个后端**都**在
                //      引擎内。⚠ 出口内 cjxl 缺失等失败一律**显式失败**，不回退 ffmpeg 的 libjxl
                //      （见 `RawColorPipeline.EncodeJxlViaCjxlAsync`）。
                //    ⚠ 同日第五次更正（JXR 出口轮）：**JXR 也接入**（`RawColorPipeline.EncodeJxrViaJxrEncAppAsync`，
                //      BMP/TIFF 中转 + JxrEncApp）⇒ 本清单只剩 **DNG**（需传感器 Bayer 数据，结构上不接入）
                //      与**动画**（引擎单帧）。
                // ⚠ 此前不分这两种情况 ⇒ 默认切引擎后所有非引擎格式都会硬失败
                //   （实测 verify-format-regression 的 avif/gif 全红、退出码 90）。
                if (ColorMapping.ColorEngineRouter.ExplicitlyRequested(item.Options))
                {
                    Log($"[色彩引擎] ❌ 按要求应走引擎，但本次不可走（{block.Code}）：{block.Text}\n");
                    LogAlternatives(Log, block.Alternatives);
                    return (true, 90, null);
                }
                Log($"[色彩引擎] 本次不适用引擎（{block.Code}：{block.Text}）⇒ 由传统管线处理（非引擎编码出口的格式属正常归属）\n");
                // ⚠ 2026-09-27 维护者定案：**HDR→SDR 的色调映射契约以自研管线（引擎）为准**
                //   （引擎口径：Hable + `white=203nit`，且仅在出口语义本身是 HDR 时发 `intensity_target`）。
                //   回落到传统管线后用的是 ffmpeg `tonemap=hable` 收满量程、SDR 出口不发 intensity_target
                //   ⇒ 同一份 HDR 源两条路**码值域稳定差 ~10 dB**（同路跨目标只差 ~30 dB，故这是契约不是色彩错误；
                //   实测与推理见 `docs/COLOR_MATRIX_FIX_PLAN_2026-09-24.md` 的 #44 收口段末表格，任务 #55）。
                //   既然定案"以自研为准"，这条差异就**不能静默发生** ⇒ 每次回退都点名。
                //   文案 ASCII：`_probe-cjk-hardcode-scan` 会把 `.Log +=` 的行算进 UI 面（现余量 12）。
                Log("[color-engine] NOTE: falling back to the legacy chain, whose HDR->SDR tone-map contract "
                    + "differs from the in-house engine (which is the standard per maintainer decision 2026-09-27)\n");
                LogAlternatives(Log, block.Alternatives);
                return (false, 0, null);
            }

            var intent = ColorMapping.ColorIntentFactory.FromOptions(item.Options, inputPath, out var why);
            if (intent == null)
            {
                Log($"[色彩引擎] ❌ 选项→色彩意图装配失败：{why}\n");
                return (true, 90, null);
            }
            var plan = ColorMapping.ColorMappingEngine.Plan(intent);
            item.Command = $"[色彩引擎] {plan.Action} 后端={plan.Backend} 源={plan.Src?.Name ?? "?"} 目标={plan.Dst?.Name ?? item.Options.ColorSpace ?? "auto"} 裁决={plan.Verdict?.Kind}";
            Log($"[色彩引擎] 决策：{plan.Action} 色彩损失={plan.ColorLoss}｜{plan.Reason}\n");
            // ── S6：把**裁决类别**单独打一行（稳定、可机读）──
            // ⚠ 与 `:828` 的 `item.Command` 里的 `裁决=…` 不同：`item.Command` 在 headless 日志中
            //   **不回显**（实测：全仓只有 GUI 的 CommandLabel 与 dry-run 读它）⇒ 门禁看不到。
            //   这一行与**传统管线**消费点打的 `[色彩裁决] 裁决=…` **逐字同格式**，两条管线的裁决
            //   因此可以按同一口径直接对拍（`verify-color-strategy` 的 parity 断言即读它）。
            //   用 `[色彩裁决]` 而不是 `[色彩引擎]`：见 `LogAlternatives` 的标签说明。
            Log($"[色彩裁决] 裁决={plan.Verdict?.Kind}\n");
            // ── S7：把「引擎产出正确但有可量化损失」**逐条**告诉用户 ──
            // ⚠ 这是**结构化**消费：打 `ColorDegradation.Code`（稳定契约，见 `DegradationCodes`），
            //   `Text` 只作人读补充。此前 `plan.Verdict` 存在但从不被读 ⇒ 用户看不到任何损失提示
            //   （"引擎产出正确但有损"对用户完全不可见）。格式与门禁断言逐字对齐，勿改前缀。
            if (plan.Verdict != null && plan.Verdict.Degradations.Count > 0)
                foreach (var d in plan.Verdict.Degradations)
                    Log($"[色彩引擎] ⚠️ 降级（{d.Code}）：{d.Text}\n");
            if (!plan.IsOk)
            {
                Log("[色彩引擎] ❌ 契约拒绝（不出产物）\n");
                if (!string.IsNullOrWhiteSpace(plan.Advice)) Log($"[色彩引擎]    建议：{plan.Advice}\n");
                // ⚠ Reject 的 `Advice` 是**自由文本**（保留兼容既有日志/门禁），
                //   `Verdict.Alternatives` 才是**结构化**替代路径（稳定 `AlternativeKind`）。
                //   两者都打：前者给人读，后者供门禁/GUI 判定（规划 §6.4「不用自由文本当判据」）。
                LogAlternatives(Log, plan.Verdict?.Alternatives);
                return (true, 91, null);
            }
            if (!plan.GainMap
                && !plan.ExternalEncoderExit
                && (plan.Action == ColorMapping.ColorAction.None
                    || plan.Action == ColorMapping.ColorAction.CarryIcc))
            {
                // 直通 或 **超广色域保真**（像素零改动）⇒ 重编码交给 ffmpeg。
                // ⚠ `!plan.GainMap`：GainMap 的产物是**输出结构**（底图 + 增益图 + MPF/XMP），
                //   `Action=None` 只说明"像素无需色彩映射"，并不等于"不需要 GainMap 出口"。
                //   少了这一条，直通的 GainMap 任务会掉进下面的 ffmpeg 分支 ⇒ 产出一个**没有增益图的
                //   普通 JPEG**（静默降级，正是本项要消灭的缺陷）。
                // 保真路径的 ICC 附着由**既有机制**承担：传统发射器（`BuildArguments` → `DecideOutputColor`）
                // 会在同一份 options 上做**同源判定**（同一个 `ResolveFaithfulUltraWideIcc`）并置
                // `FaithfulIccPath` ⇒ 队列后置步骤据此嵌入源 ICC（实测 prophoto-faithful 26/0）。
                // ⚠ `!plan.ExternalEncoderExit`（2026-09-17 cjxl 出口轮）：与 GainMap 那条**同款理由** ——
                //   cjxl 出口的差别在**编码器**，不在"像素要不要映射"。`Action=None` 只说明"像素无需映射"，
                //   并不等于"可以交给 ffmpeg 编码"。少了这一条，直通的 jxl+Cjxl 任务会掉进下面的
                //   ffmpeg 分支 ⇒ 产物由 **ffmpeg 的 libjxl** 出（用户选的 cjxl 及其 `-e/--progressive/
                //   --photon_noise_iso` 全丢），即**静默换编码器**。实测变异证据见本轮报告。
                if (plan.Action == ColorMapping.ColorAction.CarryIcc)
                    Log("[色彩引擎] 保真路径：像素零改动；源 ICC 由后置步骤嵌入"
                      + (string.IsNullOrWhiteSpace(plan.IccPath) ? "" : $"（{Path.GetFileName(plan.IccPath)}）") + "\n");
                Log("[色彩引擎] 计划为直通（不改像素）⇒ 本次由 ffmpeg 完成重编码\n");
                return (false, 0, plan);
            }

            // 标注一致性守卫：引擎写的标签与 BuildArguments 同源判定不一致时，属“三处映射不一致”类缺陷的现行证据。
            // ⚠ GainMap 出口跳过：它的色彩描述由 GainMapEncoder 写在底图 ICC / ISO 元数据里，
            //   计划层的 OutPrimaries 只是"源→目标"的推导结果，二者本就不该相等 ⇒ 比出来必然是假告警。
            if (!plan.GainMap)
            {
                var (op, _, _) = FfmpegCommandBuilder.EffectiveOutputColorTokens(item.Options, inputPath);
                if (!string.IsNullOrWhiteSpace(plan.OutPrimaries) && !string.IsNullOrWhiteSpace(op)
                    && !string.Equals(plan.OutPrimaries, op, StringComparison.OrdinalIgnoreCase))
                    Log($"[色彩引擎] ⚠️ 输出原色标签不一致：引擎={plan.OutPrimaries} 同源判定={op}（请当缺陷报告，勿静默采用）\n");
            }

            try
            {
                // 并发暂取 1：DOP 属于调度层话题，接线阶段先把“结果对不对”钉住再谈吞吐
                var st = await ColorMapping.RawColorPipeline.TransformFileAsync(
                    inputPath, outputPath, plan.ToSpec(), plan, s => Log(s), ct, 1,
                    options: item.Options).ConfigureAwait(false);
                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                {
                    Log("[色彩引擎] ❌ 未产出文件\n");
                    return (true, 92, plan);
                }
                Log($"[色彩引擎] ✅ {st.Width}x{st.Height} 变换 {st.ProcessMs}ms（解码 {st.DecodeMs}ms／编码 {st.EncodeMs}ms，"
                    + $"{st.MPixPerSecond:F1} MPix/s）\n");
                return (true, 0, plan);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"[色彩引擎] ❌ 执行失败：{ex.GetType().Name}: {ex.Message}\n");
                return (true, 92, plan);
            }
        }

        // ── 色彩后置（单一裁决点的交付端）──

        /// <summary>
        /// P7a-3 同源登记：声明“本任务这次执行的命令行确实由 <c>FfmpegCommandBuilder.BuildArguments</c> 产出”，
        /// 并把那次调用用的 options 实例与探测输入交出去，链尾才能按**同一份输入**复算裁决点。
        /// ⚠ 必须与 BuildArguments 的参数一致：传 clone 就要登记 clone（否则后置与命令行不同源）。
        /// 没登记 = 命令行是别处自建的 ⇒ 链尾不套裁决点判据（见 <see cref="EmbedDroppedFilterIccAsync"/>）。
        /// </summary>
        private static void NoteColorDecisionSource(QueueItem item, FfmpegOptions usedOptions, string usedInput)
        {
            item.ColorPostOptions = usedOptions;
            item.ColorPostProbeInput = usedInput;
        }

        /// <summary>
        /// 专用通路统一入口：**建命令与同源登记在同一个语句里完成**，结构上不可能“建了命令却忘了登记”。
        /// （主分支不用本函数：它自己就是 colorPostStepsDone 那条完整后置路径。）
        /// </summary>
        private static string BuildArgsAndNote(QueueItem item, FfmpegOptions opts, string input, string output)
        {
            NoteColorDecisionSource(item, opts, input);
            var args = FfmpegCommandBuilder.BuildArguments(opts, input, output);
            LogInjectedScale(item, args);      // 扩展 ③：缩了要让用户看得见
            return args;
        }

        /// <summary>
        /// 扩展 ③：把「最长边缩放**确实进了命令行**」回显一行 —— 「缩了但用户不知道」本身仍是**静默**。
        /// ⚠ **只读命令行、不读意图**：命令行里有才是真有（与 <c>docs/TESTING.md</c> §6 第 70 条同理 ——
        /// 「日志标记」单独不足以做门禁，这里反过来只用**外部可观测量**做回显）。
        /// ⚠ 判别式（<c>-2</c> 分水岭）见 <c>FfmpegCommandBuilder.FindInjectedScaleFilter</c>：
        /// <c>AnimationScaleW</c> 的 <c>scale={W}:-1:</c> 与 preScale 的 <c>scale=ceil(…):</c> 都不会被误报。
        /// </summary>
        private static string LogInjectedScale(QueueItem item, string args)
        {
            var inj = FfmpegCommandBuilder.FindInjectedScaleFilter(args);
            if (inj != null)
                item.Log += $"[MaxDimension] 命令含最长边缩放 {inj}（R1：插在色彩变换之前）\n";
            return args;
        }

        /// <summary>
        /// 色彩后置的**唯一**入口：统一元数据恢复之后的五步（PNG sBIT / PNG cICP / 引擎 ICC /
        /// 裁决点 ICC 后置 / TIFF 目标 ICC / 超广保真 ICC）。主分支与 P7a-3 登记过的专用通路都调本方法
        /// ⇒ 判据只有一份。⚠ 必须在元数据恢复之后调用（exiftool 复制会改写 chunk / 回带源 ICC）。
        /// </summary>
        /// <param name="opts">产出这次命令行的同一个 options 实例（专用通路传登记克隆，不能拿 item.Options 顶替）</param>
        /// <param name="probeInput">裁决点当时用的探测输入（管道/两步法下 ≠ item.InputPath，P7d）</param>
        /// <param name="engPlan">仅引擎路径非空（引擎在本进程转像素，ICC 必须在此补齐）；专用通路传 null</param>
        /// <param name="ct">取消令牌（`#26`：调用方 `ProcessAsync(ct)` 持有它，本跳之前一直是断点）</param>
        private async Task ApplyColorPostStepsAsync(QueueItem captured, string finalOutputPath,
            FfmpegOptions opts, string probeInput, int exitCode,
            bool engineUsed, bool mainBranch, ColorMapping.ColorTransformPlan? engPlan,
            CancellationToken ct = default)
        {
                // ── PNG 3.0: 10/12-bit 有效位 (sBIT chunk) ──
                // ffmpeg PNG 编码器无 sBIT 能力（10/12-bit 输入自动提升为 16-bit 容器，
                // 有效位语义丢失）。编码完成后以二进制写入 sBIT chunk，
                // 使输出符合 PNG 3.0 规范（16-bit 容器 + sBIT 记录有效位）。
                // 必须在元数据恢复之后执行（exiftool 复制元数据可能改写 chunk）。
                if (exitCode == 0
                    && opts.BitDepth is 10 or 12
                    && finalOutputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    if (PngCicpService.TryInsertSbit(finalOutputPath, opts.BitDepth.Value))
                        captured.Log += $"[png3] 已写入 sBIT chunk（{opts.BitDepth}-bit 有效位）\n";
                    else
                        captured.Log += "[png3] ⚠️ sBIT chunk 写入失败（已跳过）\n";
                    _onItemUpdated?.Invoke(captured);
                }

                // ── PNG 3.0: cICP chunk（CICP 色彩标签）──
                // 实测 ffmpeg 的 PNG 编码器不写 cICP；既无 iCCP 又无 cICP 的 PNG 会被查看器按
                // sRGB 解读（HDR / 广色域 PNG 因此错色、偏暗）。
                // 规范（PNG Third Edition，已核）：优先级 cICP=1 > iCCP=2 > sRGB=3 > cHRM/gAMA=4，
                // 且“无 cICP 时至多一个嵌入 profile”⇒ cICP 与 iCCP 允许并存。
                // 但正因 cICP 优先级更高，本处仍**只在无 iCCP 时写**：携带的源 ICC 可能是
                // 定制/非 CICP 可命名的，被 cICP 覆盖会更差；与自生成标准 ICC 并写的收益要等
                // 能区分“ICC 是谁生成的”时再做（见 PngCicpService.HasColorChunk 注释）。
                // 输出语义由 EffectiveOutputColorTokens 给出（与 BuildArguments 同源）；
                // 映射不到 H.273 编号时宁可不写，也绝不写与像素失配的标签。
                if (exitCode == 0
                    && finalOutputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(opts.FaithfulIccPath))
                {
                    // 保真路径（FaithfulIccPath 非空）已在此跳过：像素未转、仅靠 ICC 描述，
                    // 而 ProPhoto/Adobe 等无法用 CICP 命名 ⇒ 写任何 cICP 都是假标签。
                    // 另一类同样危险的情形：cICP 判定时 ICC 还未附上（hasIccp 为假），
                    // 之后才由元数据恢复/后置步骤附 ICC，形似“cICP+ICC 矛盾”——因此还需检查源色域名。
                    PngCicpService.HasColorChunk(finalOutputPath, out bool hasIccp, out bool hasCicp);
                    var (op, ot, srcCicp) = FfmpegCommandBuilder.EffectiveOutputColorTokens(
                        opts, probeInput);
                    // 策略判定取**计划层生效值**（与命令行/裁决点同源）：S2 的覆盖会让声明值与生效值不同，
                    // 用声明值会把"覆盖后已改像素"的输出仍按"携带源 ICC"处理（PngCicpService 亦按此判据取舍）。
                    var effStrat = FfmpegCommandBuilder.EffectiveColorStrategy(opts, probeInput);
                    // ⚠ `#26`：本方法新增了形参 `ct`（取消令牌）⇒ 这里的局部名由 `ct` 改为 `ctNum`
                    //   （它指的是 cICP 的 transfer 号，与本仓 `ct` = CancellationToken 的惯例撞名，不改就 CS0136）。
                    bool nameable = FfmpegCommandBuilder.TryGetCicpNumbers(op, ot, out byte cp, out byte ctNum);
                    // 区分“标签从哪来”：源容器自带 CICP ⇒ S2 也应携带（丢掉就是丢信息）。
                    // 源只有 ICC 时：从 ICC 本身量出标准空间——色度（DetectIccPrimaries，容差 2e-3）
                    // 与曲线（ReadIccCurve + 等价判定）**都命中标准值**才算标准 ICC ⇒ 可写 cICP；
                    // 任一不命中（定制/非中性/无法解析）⇒ 不写，不拿猜出的标签降级源 ICC。
                    bool iccStd = false;
                    if (effStrat == Models.ColorStrategy.CarryIcc
                        && hasIccp && !srcCicp && !hasCicp)
                    {
                        string? iccTmp = null;
                        try
                        {
                            iccTmp = IccProfileService.ExtractIccToTempFile(finalOutputPath,
                                s => { captured.Log += s; _onItemUpdated?.Invoke(captured); }, ct).path;
                            string? ip = IccProfileService.DetectIccPrimaries(iccTmp);
                            string? it = FfmpegCommandBuilder.CicpTokenOfCurve(
                                IccProfileService.ReadIccCurve(iccTmp));
                            if (ip != null && it != null
                                && FfmpegCommandBuilder.TryGetCicpNumbers(ip, it, out cp, out ctNum))
                            {
                                op = ip; ot = it; nameable = true; iccStd = true;
                            }
                        }
                        catch { iccStd = false; }
                        finally { IccProfileService.TryDeleteIcc(iccTmp); }
                    }
                    var act = PngCicpService.DecideLabel(effStrat,
                        hasIccp, hasCicp, srcCicp, iccStd, nameable);
                    switch (act)
                    {
                        case PngCicpService.PngLabelAction.WriteCicp:
                            captured.Log += PngCicpService.TryInsertCicp(finalOutputPath, cp, ctNum)
                                ? $"[png3] 已写入 cICP（primaries={cp}, transfer={ctNum} ← {op}/{ot}"
                                  + (iccStd ? "，由源 ICC 量出标准空间" : "") + $"；策略={effStrat}"
                                  + (hasIccp ? "，与标准 ICC 并写" : "") + "）\n"
                                : "[png3] ⚠️ cICP chunk 写入失败（已跳过）\n";
                            break;
                        case PngCicpService.PngLabelAction.SkipSourceIcc:
                            captured.Log += "[png3] 未写 cICP：S2 且源只有 ICC，而该 ICC 非标准色彩空间（色度未命中已知空间）；所造 cICP 会降级源 ICC\n";
                            break;
                        case PngCicpService.PngLabelAction.SkipUnnameable:
                            if (op != null || ot != null)
                                captured.Log += $"[png3] 未写 cICP：输出语义 {op}/{ot} 不可用 H.273 命名（宁缺勿错），ICC 仍为唯一描述\n";
                            break;
                    }
                    // S3「仅 CICP」意图：输出不应残留 ICC；若残留属异常，必须可见。
                    if (effStrat == Models.ColorStrategy.BakeCicpOnly && hasIccp)
                        captured.Log += "[png3] ⚠️ 仅CICP 策略下输出仍含 iCCP（应被剥离）\n";
                    // ── 与 cICP 并存的低优先级块必须剥掉（任务 #31(ii)，2026-09-26 实测）──
                    // 实测两格产物里 `cICP.transfer=13(sRGB)` 与 `gAMA=45455(γ2.2)` + `sRGB` + `cHRM` 同在
                    // ⇒ 认 cICP 的读者与只看 gAMA/sRGB 的老读者解出**不同**结果。
                    // ⚠ 放在 switch **之外**：块可能是 ffmpeg 编码器自己写的（实测 HDR→PNG 默认格没有任何
                    //   `[png3]` 日志却带着 cICP）⇒ 只在"本服务写了 cICP"的分支里剥，就会漏掉编码器写的那类。
                    if (PngCicpService.TryStripChunksSupersededByCicp(finalOutputPath, out int stripped))
                    {
                        if (stripped > 0)
                            captured.Log += $"[png3] 已剥离与 cICP 并存、且被其取代的 {stripped} 个色彩块（gAMA/cHRM/sRGB）"
                                          + "（规范优先级 cICP>iCCP>sRGB>cHRM/gAMA）\n";
                    }
                    else
                        captured.Log += "[png3] ⚠️ 与 cICP 并存的低优先级色彩块剥离失败（产物仍可能自相矛盾，已保留原文件）\n";
                    _onItemUpdated?.Invoke(captured);
                }

                // ── 引擎路径的 ICC 后置（P7a）──
                // legacy 由 iccgen **在编码时**嵌入目标 ICC；引擎不跑 iccgen（像素在本进程转换），
                // 因此计划要求附 ICC 时必须在此补齐，否则输出只有标签，甚至被元数据恢复带回
                // **源 ICC** 而与转换后像素失配。TIFF 由下面那段（同源逻辑）负责，此处排除。
                if (exitCode == 0 && engineUsed && engPlan is { AttachIcc: true }
                    && engPlan.Action != ColorMapping.ColorAction.CarryIcc
                    && string.IsNullOrWhiteSpace(opts.FaithfulIccPath)
                    && !opts.Format.Equals("tiff", StringComparison.OrdinalIgnoreCase)
                    && !opts.Format.Equals("tif", StringComparison.OrdinalIgnoreCase))
                {
                    string? iccFile = engPlan.IccPath;
                    if (string.IsNullOrWhiteSpace(iccFile) && !string.IsNullOrWhiteSpace(engPlan.IccPrimaries))
                        iccFile = IccProfileService.GetOrGenerateStandardIcc(probeInput,
                            engPlan.IccPrimaries, engPlan.IccTrc, engPlan.IccMatrix,
                            s => { captured.Log += s; _onItemUpdated?.Invoke(captured); }, ct);
                    if (!string.IsNullOrWhiteSpace(iccFile) && File.Exists(iccFile))
                    {
                        var iccExit = await ExifToolService.EmbedIccProfileFromFileAsync(iccFile, finalOutputPath,
                            s => { captured.Log += s; _onItemUpdated?.Invoke(captured); });
                        captured.Log += iccExit == 0
                            ? $"[色彩引擎] 已嵌入目标 ICC（{engPlan.IccPrimaries}/{engPlan.IccTrc}），与引擎转换后的像素一致\n"
                            : $"[色彩引擎] ⚠️ ICC 嵌入失败（退出码 {iccExit}）；输出可能缺少色彩描述\n";
                    }
                    else
                        captured.Log += "[色彩引擎] ⚠️ 计划要求附 ICC，但无法定位/生成目标 ICC ⇒ 输出缺少色彩描述（显式记录，不静默）\n";
                    _onItemUpdated?.Invoke(captured);
                }

                // ── legacy 路径的 ICC 后置（步骤 3：单一裁决点判“要附而编码器不落盘”）──
                // 非主分支的专用通路走同一个入口（见链尾的同名调用），避免“同一缺陷只有一条路径被修”。
                await EmbedDroppedFilterIccAsync(captured, finalOutputPath, probeInput,
                    engineUsed: engineUsed, mainBranch: mainBranch, exitCode: exitCode, decisionOptions: opts, ct: ct);

                // ── TIFF 目标色彩空间 ICC 嵌入（遵循 JPEG XL 的 ICC 策略）──
                // 2026-09-15 复测纠正（docs/TESTING.md 六.18）：本构建 ffmpeg 的 **tiff 编码器会**落盘 iccgen
                // 生成的 ICC（旧注释“实测不写”只对早期构建成立）。本步不依赖“iccgen 是否进了这次命令行”，
                // 直接按目标色彩空间贴权威 ICC ⇒ 属同语义的幂等覆盖，保留以便显式目标一定拿到目标 ICC。
                // 用户指定目标色彩空间时像素已被 zscale 转到该色域，ICC 必须与之一致，
                // 否则 TIFF 被按 sRGB 误读，或经元数据恢复回带的「源 ICC」与转换后像素失配。
                // TIFF 恒 SDR：HDR 目标的 trc 钳到 iec61966-2-1（与像素 zscale 的 dstT=capTrc 一致；
                // iccgen 不支持 HDR trc）。此步在元数据恢复之后，覆盖任何被回带的源 ICC。
                // 仅 Recommended/Manual 生效；CarryIcc 保源 ICC、BakeCicpOnly 剥 ICC，均不在此嵌目标 ICC。
                // 判据取**计划层生效策略**（与命令行同源）：S2 被覆盖（如 GainMap）后声明值不再代表实际结局。
                // ── 2026-10-04 门控放宽（修复 P1-1）────────────────────────────────
                // 旧门控只看 `opts.ColorSpace`，于是经「精确参数」(ColorPrimaries/ColorTrc) 指定目标的
                // TIFF（如 CLI `--color-primaries bt709 --color-trc srgb`）被漏掉 ⇒ 交付物零色彩声明。
                // 现与 ColorSpace 路径同义：**有显式目标**即贴对应标准 ICC。
                // ⚠ 判据用 `HasExplicitColorTarget`（只认 CP/CT）—— `ColorMatrix` 是**输入声明**、
                //   不是目标轴（见 `FfmpegOptions.HasExplicitColorTarget` 的说明）。
                string? tiffPrimaries = null;
                string? tiffMatrix = null;
                string? tiffLabel = null;
                if (!string.IsNullOrWhiteSpace(opts.ColorSpace)
                    && !opts.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    var csSpec = ColorSpaceRegistry.Resolve(opts.ColorSpace);
                    if (csSpec != null)
                    {
                        tiffPrimaries = csSpec.OutPrimaries;
                        tiffMatrix = csSpec.OutMatrix;
                        tiffLabel = csSpec.Key;
                    }
                }
                // ⚠ 2026-10-04：**也覆盖 RAW 预处理默认目标**（`ColorFromRawDefault=true`）。
                //   旧口径把程序默认目标排除在外 ⇒ ARW→TIF 默认档（日志宣称"输出目标 bt2020/iec61966-2-1"）
                //   像素被 zscale 真转到 bt2020、**却零色彩声明**（TIFF 无 CICP 通路）⇒ 下游按 sRGB 误读。
                //   日志宣称目标而产物不声明 = 本仓最忌的「宣称 ≠ 交付」。
                //   原先"避免覆盖 RAW 原生描述"的顾虑对**无 ICC 的线性中间件**不成立（没有可覆盖的东西）。
                else if (opts.HasExplicitColorTarget
                    && !string.IsNullOrWhiteSpace(opts.ColorPrimaries))
                {
                    tiffPrimaries = opts.ColorPrimaries;
                    // TIFF 是 RGB 原生容器：ICС 仅描述 primaries+transfer，matrix 不影响色度学，
                    // 故矩阵缺省时回落 bt709（与 zscale 的 SDR 默认一致）；用户显式给了则尊重。
                    tiffMatrix = !string.IsNullOrWhiteSpace(opts.ColorMatrix) ? opts.ColorMatrix : "bt709";
                    tiffLabel = opts.ColorPrimaries + (string.IsNullOrWhiteSpace(opts.ColorTrc) ? "" : "/" + opts.ColorTrc);
                }

                if (exitCode == 0
                    && FfmpegCommandBuilder.EffectiveColorStrategy(opts, probeInput)
                       is Models.ColorStrategy.Recommended or Models.ColorStrategy.Manual
                    && string.IsNullOrWhiteSpace(opts.FaithfulIccPath)   // 保真路径：像素未转→目标标准 ICC 会失配
                    && (opts.Format.Equals("tiff", StringComparison.OrdinalIgnoreCase)
                        || opts.Format.Equals("tif", StringComparison.OrdinalIgnoreCase))
                    && !string.IsNullOrWhiteSpace(tiffPrimaries))
                {
                    // TIFF 格式能力恒将 trc 钳为 iec61966-2-1（见 GetFormatColorCapabilities），
                    // 像素 zscale 的 dstT=capTrc 亦然；故目标 ICC 的 trc 必须恒用 iec61966-2-1，
                    // 与像素编码精确一致（避免 Adobe RGB/BT.2020 的 spec.OutTrc=bt709 与像素 sRGB trc 失配）。
                    var tiffTrc = "iec61966-2-1";
                    var tiffIcc = IccProfileService.GetOrGenerateStandardIcc(
                        probeInput, tiffPrimaries, tiffTrc, tiffMatrix,
                        s => { captured.Log += s; _onItemUpdated?.Invoke(captured); }, ct);
                    if (!string.IsNullOrWhiteSpace(tiffIcc))
                    {
                        var tiffIccExit = await ExifToolService.EmbedIccProfileFromFileAsync(
                            tiffIcc, finalOutputPath,
                            s => { captured.Log += s; _onItemUpdated?.Invoke(captured); });
                        captured.Log += tiffIccExit == 0
                            ? $"[tiff] 已嵌入目标色彩空间 ICC（{tiffLabel}），与转换后像素一致（遵循 JPEG XL ICC 策略）\n"
                            : $"[tiff] ⚠️ 目标 ICC 嵌入失败（退出码 {tiffIccExit}）\n";
                    }
                    else
                    {
                        captured.Log += $"[tiff] ⚠️ 无法生成目标 ICC（{tiffLabel}），TIFF 可能缺少色彩描述\n";
                    }
                    _onItemUpdated?.Invoke(captured);
                }

            // ── 超广色域保真 ICC 附着（点三）──
            // FfmpegCommandBuilder 已判定并置位 FaithfulIccPath：像素完全未转换，
            // 也未跑 iccgen（iccgen 无法命名 ProPhoto），故必须在此把命名 ICC 贴到输出容器。
            // 必须在元数据恢复之后：exiftool 安全复制会排除源 ICC，本步为权威覆盖。
            if (exitCode == 0
                && !string.IsNullOrWhiteSpace(opts.FaithfulIccPath)
                && File.Exists(opts.FaithfulIccPath))
            {
                var fIcc = opts.FaithfulIccPath!;
                var fExit = await ExifToolService.EmbedIccProfileFromFileAsync(fIcc, finalOutputPath,
                    s => { captured.Log += s; _onItemUpdated?.Invoke(captured); });
                captured.Log += fExit == 0
                    ? $"[保真] 已附着超广色域命名 ICC（{Path.GetFileName(fIcc)}），像素未转换\n"
                    : $"[保真] ⚠️ 命名 ICC 附着失败（退出码 {fExit}）；输出可能缺少色彩描述\n";
                _onItemUpdated?.Invoke(captured);
            }
        }
        /// <summary>
        /// 裁决点判「本应附 ICC，但该容器的编码器会把 <c>iccgen</c> 的 ICC 丢弃」时的后置补齐（步骤 3）。
        /// 实测：png(iCCP)/jpg(APP2)/tiff 能落盘，**webp 不能**（libwebp 不写 ICCP 块），exiftool 后置嵌入可正常读回。
        /// 主分支与已登记的专用通路**共用本入口**（P7a-3），避免“同一个缺陷只有一条路径被修”；
        /// 未登记的通路（命令行非裁决点产出）不得调用本方法，只能显式记录“裁决点未参与”。
        /// </summary>
        /// <param name="decisionOptions">命令行实际用的 options（与 <c>actualInput</c> 成对）；主分支传 null 即用 item.Options。</param>
        /// <param name="ct">取消令牌（`#26`：唯一调用方 `ApplyColorPostStepsAsync` 持有它，本跳之前是断点）</param>
        private async Task EmbedDroppedFilterIccAsync(QueueItem item, string outputPath, string actualInput,
            bool engineUsed, bool mainBranch, int exitCode = 0, FfmpegOptions? decisionOptions = null,
            CancellationToken ct = default)
        {
            // 引擎路径不跑 iccgen（像素在本进程转换），它的目标 ICC 由 P7a 那段后置逻辑负责。
            if (exitCode != 0 || engineUsed) return;
            var copts = decisionOptions ?? item.Options;
            var fmtL = (item.Options.Format ?? "").ToLowerInvariant();
            if (!mainBranch)
            {
                // 只在“这个组合确实会丢东西”时告警，不给所有专用通路制造噪音。
                bool warned = false;
                if (!string.IsNullOrWhiteSpace(copts.FaithfulIccPath)
                    && fmtL is "png" or "apng" or "jpg" or "jpeg" or "tiff" or "tif" or "webp")
                {
                    item.Log += $"[色彩后置] ⚠️ 本通路不经过主分支的保真 ICC 附着（{Path.GetFileName(copts.FaithfulIccPath)} 未嵌入）⇒ 输出缺少色彩描述\n";
                    warned = true;
                }
                else if (fmtL is "png" or "apng" && copts.BitDepth is 10 or 12)
                {
                    item.Log += "[色彩后置] ⚠️ 本通路不经过主分支的 PNG sBIT 补写 ⇒ 10/12-bit 有效位标记缺失\n";
                    warned = true;
                }
                if (warned) _onItemUpdated?.Invoke(item);
            }
            // 保真路径：像素未转，附自生成标准 ICC 会失配 ⇒ 只依赖命名 ICC 那一步（主分支已做）。
            if (!string.IsNullOrWhiteSpace(copts.FaithfulIccPath)) return;
            var dec = FfmpegCommandBuilder.DecideOutputColor(copts, actualInput);
            if (!dec.NeedsPostEmbedIcc) return;
            string? wIcc = IccProfileService.GetOrGenerateStandardIcc(actualInput,
                dec.OutPrimaries!, dec.OutTrc!, dec.MatrixForOutput ?? "bt709",
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            if (!string.IsNullOrWhiteSpace(wIcc) && File.Exists(wIcc))
            {
                var wExit = await ExifToolService.EmbedIccProfileFromFileAsync(wIcc, outputPath,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                item.Log += wExit == 0
                    ? $"[色彩] 已嵌入出口语义 ICC（{dec.OutPrimaries}/{dec.OutTrc}）——该容器编码器会丢弃 iccgen 的 ICC，故后置补齐\n"
                    : $"[色彩] ⚠️ ICC 嵌入失败（退出码 {wExit}）；输出色彩描述缺失，广色域像素将被按 sRGB 误读\n";
            }
            else
                item.Log += $"[色彩] ⚠️ 无法生成出口语义 ICC（{dec.OutPrimaries}/{dec.OutTrc}）⇒ 输出缺少色彩描述（显式记录，不静默）\n";
            _onItemUpdated?.Invoke(item);
        }

        // ── 编码器后端专用处理方法 ──

        /// <summary>
        /// 该输出格式是否属于「ffmpeg 侧会**映射**源元数据的 CICP 格式」。
        /// <para>`FfmpegCommandBuilder` 为保住 `cICP`/`cHRM`/`gAMA` 色彩 chunk，对
        /// `avif`/`jxl`/`png`/`apng` 一律用 `-map_metadata 0`（而非 `-map_metadata -1`）
        /// ⇒ 源 EXIF 会被一并带进产物。</para>
        /// <para>⚠ <b>实测只有 `png`/`apng` 真的泄漏 GPS</b>：`avif` 虽然同样用 `-map_metadata 0`，
        /// 但产物里查不到 GPS（`avif`/`jxl` 的 EXIF 落地方式不同）；`jpg`/`webp`/`tiff` 用
        /// `-map_metadata -1`，本就不泄漏。
        /// ⇒ 这里**只列 `png`/`apng`**，避免给 avif/jxl 白加一次 exiftool 启动（~850ms/项）。
        /// 若将来 avif/jxl 也开始泄漏，需另立断言并按实测扩列。</para>
        /// </summary>
        private static bool IsCicpMetadataMappedFormat(string? format)
            => format is not null
               && (format.Equals("png", StringComparison.OrdinalIgnoreCase)
                   || format.Equals("apng", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 该格式的**任一后端通路**是否会"顺带把源元数据整体带进产物"（从而需要一次真正的后置剥离）。
        /// <para>⚠⚠ 2026-10-05 新增（P0 隐私泄露修复）：原先只有 <see cref="IsCicpMetadataMappedFormat"/>
        /// 一种情形被判为"带出来了"，因为当时已知的只有 ffmpeg 为保 CICP 而 `-map_metadata 0`。
        /// 但审查发现**第二条通路**：`jxl` 的 **cjxl jbrd 无损 JPEG 重封装**（`--jxl-lossless-jpeg`，
        /// **默认开启**）**逐字拷贝源 JPEG 码流** ⇒ 源 EXIF/GPS **原样进入产物**。</para>
        /// <para>实测（审查复现，默认参数、无任何元数据开关）：
        /// <code>
        /// FfmpegGui.exe --headless -i gps.jpg -o out -f jxl
        ///   ⇒ 产物 jxl 内 GPSLatitude/GPSLongitudeRef/… **全在**，退出码 0，无告警
        ///   ⇒ 对照：同参数 -f png/jpg/webp/tiff/avif ⇒ GPS 均被正确剥除
        ///   ⇒ 决定性对照：加 `--jxl-lossless-jpeg false` ⇒ **GPS 消失**
        ///   ⇒ 且 `--strip-metadata` **也无效**（只有 `--strip-all-exif` 能去掉）
        /// </code></para>
        /// <para>⚠ 为什么判"按格式"而不是"按本次是否真的走了 jbrd"：本方法在**元数据阶段**调用，
        /// 而此刻已无法可靠回溯编码段的实际分支；按格式判是**保守方向**（多做一次剥离 = 更干净），
        /// 不会漏。剥离只删 EXIF/GPS/XMP/IPTC，不碰 JXL 的色彩标注。</para>
        /// </summary>
        private static bool FormatMayCarrySourceMetadata(string? format)
            => IsCicpMetadataMappedFormat(format)
               || (format is not null && format.Equals("jxl", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 元数据阶段唯一入口：先恢复描述性元数据，再按需嵌缩略图（EXIF IFD1）。
        /// <para><b>顺序即不变量</b>：缩略图属于元数据阶段，必须排在**色彩后置之前** ——
        /// 本仓的裁决权分配是「元数据先写、色彩标签最后定」，反过来会让色彩判据看到已被二次改写的文件。
        /// 隐私开关在这里同样生效（见 <see cref="ThumbnailService.PrivacySkipReason"/>）：
        /// `StripAll` / `StripExifAll` 是用户明确的「产物里不要留描述性元数据」，缩略图不能让它们破口。</para>
        /// <para>收口在本方法而不是 19 个调用点：那些点是各通路的早退分支，逐点接线必然漏。</para>
        /// </summary>
        private async Task RestoreMetadataAsync(QueueItem item, string outputPath)
        {
            await RestoreMetadataCoreAsync(item, outputPath);

            if (!item.Options.EnableThumbnail) return;
            await ThumbnailService.ApplyAsync(outputPath, item.Options,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); });
        }

        /// <summary>外部工具编码后恢复元数据（优先 exiftool，不可用时回退 ffmpeg）</summary>
        private async Task RestoreMetadataCoreAsync(QueueItem item, string outputPath)
        {
            if (item.Options.MetadataMode != Models.MetadataMode.PreserveAll)
            {
                // ⚠⚠ 2026-09-21：**`StripAll` 不等于「已经剥离干净」**。
                //   ffmpeg 侧对 CICP 格式（`png`/`apng`）走的是 `-map_metadata 0`
                //   —— 那是为了保住 `cICP`/`cHRM`/`gAMA` 这些**色彩** chunk（见
                //   `FfmpegCommandBuilder` 的 `isCicpFormat` 分支），但它会**顺带把源 EXIF（含 GPS）
                //   带进产物**；而 StripAll 原先在这里**直接 return** ⇒ 既不复制、也不清理
                //   ⇒ **用户的隐私删除被静默绕过**。
                //   实测（源 jpg 带 GPS，`--strip-metadata`）：jpg / webp / tiff 产物 GPS **均无**，
                //   **png / apng 却带 GPS** ⇒ 只有这两种格式中招。
                //   ⇒ 补一次真正的剥离。⚠ 用 `StripDescriptiveMetadataAsync`（内部先做 `-EXIF:all=`
                //     复位：ffmpeg 写坏的 `eXIf` 块会让**任何** exiftool 写操作直接以退出码 1 失败），
                //     且它**只删 EXIF/GPS/XMP/IPTC**，`cICP`/`cHRM`/`gAMA` 等色彩 chunk 不受影响。
                //   ⚠ 不能复用 `RunAsync` + `BuildArguments`：`--strip-metadata` 并不置 `StripExifAll`
                //     ⇒ 那条路只发 `-gps:all=`，在坏 `eXIf` 上必然失败（实测 GPS 残留）。
                // ⚠⚠ 2026-10-05 扩围（P0 隐私泄露修复，见 `FormatMayCarrySourceMetadata` 的说明）：
                //   ① 判据由 `IsCicpMetadataMappedFormat`（仅 png/apng）扩到
                //      `FormatMayCarrySourceMetadata`（+ jxl 的 jbrd 无损重封装路径）；
                //   ② 触发条件由「仅 StripAll」扩到「**StripAll 或 任一剥离开关为真**」——
                //      实测缺陷：默认配置下 `StripExifGps` 本就是 true，而 `-f jxl` 产物**仍带 GPS**，
                //      `--strip-gps` / `--strip-xmp` 亦然 ⇒ 用户显式要求删 GPS 却静默保留。
                //   ⚠ 剥离用 `StripDescriptiveMetadataAsync`（只删 EXIF/GPS/XMP/IPTC），
                //     不碰 cICP/cHRM/gAMA 这类**色彩** chunk，也不碰 JXL 的色彩标注 ⇒ 不会破坏色彩契约。
                //   ⚠⚠ 2026-10-05 用户裁定后补充：`jxl` 的 **jbrd 无损重封装是绝对默认**，
                //     而它与"剥除元数据"**结构上不可兼得**（jbrd 逐字复制源码流，exiftool 改不了其内部字节）。
                //     ⇒ 仅当用户**显式**要求剥除时，才由 `CjxlService.UsesLosslessJpegRewrap` 放弃重封装；
                //       该取舍由 `RawColorPipeline`/`CjxlService` 在日志里**点名**，绝不静默。
                bool stripRequested = item.Options.MetadataMode == Models.MetadataMode.StripAll
                                   || item.Options.StripExifGps
                                   || item.Options.StripExifAll
                                   || item.Options.StripExifTime
                                   || item.Options.StripExifCamera
                                   || item.Options.StripXmp;
                if (stripRequested
                    && FormatMayCarrySourceMetadata(item.Options.Format)
                    && ExifToolService.IsAvailable
                    && File.Exists(outputPath))
                {
                    try
                    {
                        item.Log += "[exiftool] 剥离描述性元数据（该格式/后端的通路会把源元数据带进产物）...\n";
                        _onItemUpdated?.Invoke(item);
                        var stripExit = await ExifToolService.StripDescriptiveMetadataAsync(
                            outputPath, s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                        // ⚠ 用 if/else 而不是三元续行：CJK 门禁按「**语句首行**是否命中诊断 sink」分桶，
                        //   而 `? "中文"` / `: $"中文"` 这两行是**续行**、不命中 sink ⇒ 被判进 `CsUi`
                        //   判据桶 ⇒ 门禁 +1 转红（实测 828→829；余量为 0，且不得为此抬基线）。
                        //   写成两条独立的 `item.Log += …` 语句后，中文落在 sink 行内 ⇒ 记诊断桶。
                        if (stripExit == 0)
                            item.Log += "[exiftool] ✅ 描述性元数据已剥离\n";
                        else
                            item.Log += $"[exiftool] ⚠️ 剥离描述性元数据退出码 {stripExit}\n";
                    }
                    catch (Exception ex)
                    {
                        item.Log += $"[exiftool] ⚠️ 剥离描述性元数据异常: {ex.Message}\n";
                    }
                }
                return;
            }

            var backend = item.Options.EncoderBackend;

            // ── 判断是否需要保护输出文件的色彩元数据 ──
            // 原则：如果用户手动指定了色彩参数，说明用户有明确的色彩意图，
            //       不应被源文件元数据覆盖；如果一切为 auto，则恢复源文件元数据。
            var userSpecifiedColor =
                item.Options.HasExplicitColorParams ||
                (!string.IsNullOrWhiteSpace(item.Options.ColorSpace)
                 && !item.Options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase));

            // 编码器后端保护：外部编码器/FFmpeg 已嵌入色彩信息
            // cjpegli 管道模式不嵌入任何色彩标签（无 JFIF/ICC），不应保护
            // ⚠⚠ 2026-09-22（管线审查 §36.6 的实测结论）：**GainMap 变体是例外** ——
            //   上面那句对**普通** cjpegli 输出成立，但对 **GainMap 出口不成立**：
            //   增益图出口由 `GainMapEncoder` **自己写底图 ICC**（对齐 libultrahdr：底图是什么色域就附什么 ICC）。
            //   若不保护 ⇒ 随元数据带回的**源 ICC 会覆盖底图 ICC** ⇒ **底图错标**。
            //   实测（HDR 源 + 源附 P3 ICC + `--format jpg --jpeg-gain-map true`）：
            //     · 默认（引擎/Ffmpeg 后端）：底图 ICC = `75FF19E1…` —— **自己的色域**，正确；
            //     · `--encoder cjpegli`：底图 ICC = `D6881218…` —— 与**源 P3 ICC 逐字节相同** ⇒ 错标。
            var encoderProtectsColor =
                backend == EncoderBackend.Cjxl ||
                backend == EncoderBackend.Jxr ||
                backend == EncoderBackend.Ffmpeg ||
                (backend == EncoderBackend.Cjpegli && item.Options.JpegGainMap);

            // 综合判断：用户指定 或 编码器保护 → 安全模式
            var protectColorMetadata = userSpecifiedColor || encoderProtectsColor;

            // TIFF 特殊处理：TIFF 使用 ICC Profile 色彩管理，不支持视频风格 color_primaries。
            // 元数据复制因此走“不覆盖色彩标签”的路径；最终出口 ICC 由色彩后置步骤决定
            // （源无 ICC 时它负责补上目标 ICC；源有 ICC 而像素已转到目标色域时它覆盖回带的源 ICC）。
            var isTiff = item.Options.Format.Equals("tiff", StringComparison.OrdinalIgnoreCase)
                      || item.Options.Format.Equals("tif", StringComparison.OrdinalIgnoreCase);
            if (isTiff && userSpecifiedColor)
            {
                item.Log += "[tiff] TIFF 以 ICC Profile 承载色彩（无 color_primaries 模式）：元数据复制不覆盖色彩标签，"
                    + "出口 ICC 由色彩后置步骤嵌入并覆盖随元数据带回的源 ICC\n";
                protectColorMetadata = false; // 允许完整元数据复制（含 ICC Profile）
            }

            // ── 优先：exiftool ──
            if (ExifToolService.IsAvailable)
            {
                try
                {
                    if (protectColorMetadata)
                    {
                        var reason = userSpecifiedColor ? "用户指定色彩空间" : "编码器保护";
                        item.Log += $"[exiftool] 从源文件恢复元数据（安全模式：{reason}，不覆盖色彩标签）...\n";
                    }
                    else
                        item.Log += "[exiftool] 从源文件恢复元数据...\n";
                    _onItemUpdated?.Invoke(item);

                    // ⚠ `Orientation` 只在**免解码重封装**这一种情形下必须保留（像素原样搬 DCT 系数 ⇒
                    //   源的取向标签仍然成立）；其余一切路径的像素都被解码器旋正过 ⇒ 带回该标签 = 二次旋转。
                    //   判据取 `CjxlService.UsesLosslessJpegRewrap`（与 cjxl 命令行里 `--lossless_jpeg=1`
                    //   的判据**同一份实现**，不另写一遍）。
                    bool keepOrientationTag = CjxlService.UsesLosslessJpegRewrap(item.InputPath, item.Options);
                    var copyExit = protectColorMetadata
                        ? await ExifToolService.CopyMetadataSafeAsync(
                            item.InputPath, outputPath,
                            s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                            absorb: item.Options,   // 可吸收时把 GPS/XMP 的隐私清理并入本次复制（省一次 exiftool 启动）
                            dropOrientation: !keepOrientationTag)
                        : await ExifToolService.CopyMetadataAsync(
                            item.InputPath, outputPath,
                            s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                            absorb: item.Options,
                            dropOrientation: !keepOrientationTag);

                    if (copyExit == 0)
                    {
                        // 安全模式下 ICC Profile 可能被排除。
                        // 仅当**计划层生效策略**为「携带(CarryIcc)」时才显式回带源 ICC；
                        // 推荐/手动——像素已转换到目标（目标 ICC 由 iccgen/exiftool 生成），回带源 ICC 会失配，故不回带。
                        // ⚠ 用声明值会让 S2 被覆盖（如 GainMap 要求底图映射）后仍回带源 ICC ⇒ 与已映射的像素失配。
                        bool effCarry = FfmpegCommandBuilder.EffectiveColorStrategy(item.Options, item.InputPath)
                                        == Models.ColorStrategy.CarryIcc;
                        // ⚠ 「用户指定了色彩空间」这一条**不能**一概跳过源 ICC 恢复（2026-09-26 实测缺陷）：
                        //   对**编码器会丢弃 ICC 的容器**（能力表 `EncoderWritesGeneratedIcc=false`，本构建实测只有 webp），
                        //   这里的跳过 + P7a 后置的 `Action != CarryIcc` 排除**叠加**后，没有任何一方负责嵌入 ⇒
                        //   产物**零色彩标注**，而查看器把无标注 webp 按 sRGB 解读 ⇒ 广色域像素被洗白。
                        //   复现：源 PNG 挂 Display P3 ICC + `--color-strategy carry --color-space BT.2020` →
                        //   webp 产物 96 B、`exiftool -b -icc_profile` 抽出 **0 B**、裁决仍是 `Proceed`（legacy/engine 两条都中）。
                        //   携带的承诺是"像素零改动"⇒ 唯一能真实描述这些像素的就是**源 ICC**；
                        //   给没动过的像素挂一张目标空间的 ICC 才是假标签，所以这里补带源 ICC 并点名空间选择为何没落到标签上。
                        bool containerDropsIcc = !FfmpegGui.Services.ColorMapping.ColorMappingEngine
                            .CapsFor(item.Options.Format).EncoderWritesGeneratedIcc;
                        if (protectColorMetadata && effCarry && (!userSpecifiedColor || containerDropsIcc))
                        {
                            var iccExit = await ExifToolService.CopyIccProfileAsync(
                                item.InputPath, outputPath,
                                s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                            if (iccExit != 0)
                            {
                                item.Log += $"[exiftool] ⚠️ ICC Profile 恢复失败（退出码 {iccExit}），目标格式可能不支持 exiftool ICC 写入（如 JXL）。\n" +
                                    $"[exiftool] 建议：勾选「保留 Ultra HDR 增益图」或使用外部 cjxl 编码，以正确保留色彩配置。\n";
                            }
                            // ⚠ 每条诊断都要落在**带 `item.Log +=` 的那一行**上：cjk 棘轮（②-c）是**词法**判据，
                            //   折行会让消息掉进「UI 面」桶（本仓已把它登记为三类误计之一，范式见 HANDOVER §5 第 5 行）。
                            else if (userSpecifiedColor)
                                item.Log += "[exiftool] ICC Profile 已恢复（本容器编码器丢弃 ICC ⇒ 携带策略下标签只能取源 ICC；指定的目标空间未落到标签）\n";
                            else
                                item.Log += "[exiftool] ICC Profile 已恢复\n";
                        }
                        else if (protectColorMetadata && userSpecifiedColor)
                        {
                            item.Log += "[exiftool] 用户已指定色彩空间，跳过源文件 ICC Profile 恢复（避免覆盖用户选择）\n";
                        }
                        // ── RAW 补漏：从**源 RAW** 取回中间件丢掉的拍摄字段 ──
                        // 必须在这里（常规恢复**之后**）：常规恢复的源是中间 TIFF，那些字段根本不在里面。
                        await RestoreRawCaptureFieldsAsync(item, outputPath);

                        // JPEG 输出添加 JFIF 头（提高手机兼容性）
                        if (IsJpegOutput(item))
                            await ExifToolService.EnsureJfifHeaderAsync(outputPath);
                        item.Log += "[exiftool] 元数据恢复完成\n";
                        return;
                    }
                    else if (protectColorMetadata)
                    {
                        item.Log += $"[exiftool] 元数据恢复警告: 退出码 {copyExit}，已跳过 ffmpeg 回退（色彩保护模式）\n";
                        // ⚠ 常规复制失败**不等于**拍摄字段也补不回：补漏是**独立的一次调用**且只写白名单
                        //   ⇒ 仍然尝试（部分元数据胜过全丢）。实测 PNG 目标曾因 ffmpeg 写坏的 `eXIf`
                        //   块让常规复制恒失败，若在此直接 return，补漏就永远不生效。
                        await RestoreRawCaptureFieldsAsync(item, outputPath);
                        return;
                    }
                    else
                    {
                        item.Log += $"[exiftool] 元数据恢复警告: 退出码 {copyExit}，尝试 ffmpeg 回退...\n";
                    }
                }
                catch (Exception ex)
                {
                    if (protectColorMetadata)
                    {
                        item.Log += $"[exiftool] 元数据恢复异常: {ex.Message}，已跳过 ffmpeg 回退（色彩保护模式）\n";
                        return;
                    }
                    item.Log += $"[exiftool] 元数据恢复异常: {ex.Message}，尝试 ffmpeg 回退...\n";
                }
            }
            else
            {
                item.Log += "[metadata] exiftool 未检测到，使用 ffmpeg 回退方案恢复元数据\n";
            }

            // ── 回退：ffmpeg 重新封装 ──
            // 仅在色彩不需要保护时才执行 ffmpeg 回退。
            // ffmpeg -map_metadata 1 会从源文件映射全局元数据（可能含 ICC/色彩标签），
            // 与用户手动指定的色彩参数或编码器嵌入的色彩信息可能冲突。
            if (protectColorMetadata)
            {
                item.Log += "[ffmpeg-meta] 已跳过回退（色彩保护模式）\n";
                return;
            }
            await RestoreMetadataViaFfmpegAsync(item, outputPath);
        }

        /// <summary>
        /// RAW 输入专用补漏：从**用户实际提供的源 RAW** 取回中间件（`dngtool -d -T`，实为 dcraw 兼容 TIFF 写出）
        /// 丢掉的拍摄字段（`DateTimeOriginal` / `CreateDate` / `LensModel` / `ExposureProgram` /
        /// `MeteringMode` / `Flash` 等）。实测 5/5 厂商复现，见 `docs/RAW_PIPELINE_AUDIT.md §10`。
        ///
        /// <para><b>触发条件（三条全满足才动手）</b>：① 有 <see cref="QueueItem.SourceInputPath"/>；
        /// ② 它与当前 <see cref="QueueItem.InputPath"/> **不同**（确实被中间件替换过 ⇒ 常规恢复的源已不是原文件）；
        /// ③ 它是 RAW 家族文件。</para>
        ///
        /// <para>⚠ <b>不做「把源整个换掉再复制一遍」</b> —— 那会把源 RAW 的 `ColorSpace`（常为 Adobe RGB）、
        /// `PixelXDimension/YDimension`（常为内嵌预览尺寸）、`Orientation`（像素已被解码器旋正）一并带回，
        /// 与产物冲突。故只走**白名单**，见 <see cref="ExifToolService.CopyRawCaptureFieldsAsync"/>。</para>
        ///
        /// <para>⚠ 补漏失败**不得**影响任务结果：像素与主元数据此时已经正确，只记日志。</para>
        /// </summary>
        private async Task RestoreRawCaptureFieldsAsync(QueueItem item, string outputPath)
        {
            var src = item.SourceInputPath;
            if (string.IsNullOrEmpty(src)) return;
            if (string.Equals(src, item.InputPath, StringComparison.OrdinalIgnoreCase)) return; // 源未被替换 ⇒ 常规恢复已覆盖
            if (!RawService.IsRawFile(src)) return;
            if (!File.Exists(src)) return;
            if (!ExifToolService.IsAvailable)
            {
                item.Log += "[exiftool] ⚠️ 未检测到 exiftool ⇒ 无法从源 RAW 补回拍摄字段（拍摄时间 DateTimeOriginal/CreateDate、镜头型号等仍会缺失）\n";
                return;
            }

            try
            {
                _onItemUpdated?.Invoke(item);
                var exit = await ExifToolService.CopyRawCaptureFieldsAsync(
                    src, outputPath, item.Options,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                if (exit == 0)
                    item.Log += "[exiftool] ✅ 已从源 RAW 补回拍摄字段\n";
                else if (exit == -2)
                { /* 已按隐私开关跳过；说明行由被调方打印 */ }
                else
                    item.Log += $"[exiftool] ⚠️ 从源 RAW 补回拍摄字段失败（退出码 {exit}）\n";
            }
            catch (Exception ex)
            {
                item.Log += $"[exiftool] ⚠️ 从源 RAW 补回拍摄字段异常: {ex.Message}\n";
            }
        }

        /// <summary>
        /// 使用 ffmpeg 重新封装来恢复元数据：以目标文件的像素流为基础，
        /// 从源文件映射元数据，输出到临时文件后原子替换。
        /// </summary>
        private async Task RestoreMetadataViaFfmpegAsync(QueueItem item, string outputPath)
        {
            // 使用带正确扩展名的临时文件路径，确保 ffmpeg 可以自动识别输出格式
            var tempPath = Path.Combine(PlatformServices.GetTempDir(),
                $"meta_{Guid.NewGuid():N}_{Path.GetFileName(outputPath)}");
            try
            {
                item.Log += "[ffmpeg-meta] 使用 ffmpeg 从源文件恢复元数据...\n";
                _onItemUpdated?.Invoke(item);

                // 双输入重新封装：
                //   -i outputPath  (map 0 = 目标文件的像素流)
                //   -i inputPath   (map_metadata 1 = 源文件的元数据)
                //   -c copy        (不解码，直接复制码流)
                //   -map_metadata 1 (从第二个输入映射全局元数据)
                //   -map_metadata:s:v 1:s:v (从第二个输入映射视频流元数据)
                var ffArgs = $"-y -i \"{outputPath}\" -i \"{item.InputPath}\" " +
                             $"-map 0 -map_metadata 1 -map_metadata:s:v 1:s:v " +
                             $"-c copy \"{tempPath}\"";

                item.Log += $"[ffmpeg-meta] ffmpeg {ffArgs}\n";
                // ⚠ 这里**有意不传 ct**（全仓唯一一处）：本方法跑在“编码已经成功”之后，是秒级的
                // `-c copy` 重封装。若传 ct，用户在收尾瞬间按“停止”会走到
                // catch (OperationCanceledException) → Status = "已停止"（见 :602），
                // 而 finally 对“已停止”会调用 TryDiscardPartialOutput **把已完成的合法产物删掉**（见 :613）
                // ⇒ 那是把“修取消传播”变成“按停止就丢成品”的回归。取消该拦的是编码/预处理，
                // 不是这个已经产出成品之后的收尾动作。
                var exitCode = await FfmpegRunner.RunAsync(ffArgs,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                    AppSettingsService.Current.FfmpegPath);

                if (exitCode == 0 && File.Exists(tempPath))
                {
                    // 单步替换（旧写法 File.Delete + File.Move 并不“原子”：Delete 之后 Move 失败 = 产物整个丢了）
                    try
                    {
                        TryAtomicReplace(tempPath, outputPath);
                        item.Log += "[ffmpeg-meta] 元数据恢复完成（ffmpeg 重新封装）\n";
                    }
                    catch (Exception ex)
                    {
                        item.Log += $"[ffmpeg-meta] ⚠️ 文件替换失败（原产物保持原样，未被删除）: {ex.Message}\n";
                        TryCleanupTemp(tempPath);
                    }
                }
                else
                {
                    item.Log += $"[ffmpeg-meta] ffmpeg 重新封装失败 (退出码 {exitCode})，元数据可能丢失\n";
                    TryCleanupTemp(tempPath);
                }
            }
            catch (Exception ex)
            {
                item.Log += $"[ffmpeg-meta] 异常: {ex.Message}\n";
                TryCleanupTemp(tempPath);
            }
        }

        private static void TryCleanupTemp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>
        /// P1-4：任务未成功时处置半成品输出。各通路都是**直写最终路径 + 无条件 <c>-y</c>**，
        /// 于是中止/失败会留下 0 字节或截断的文件冒充合法产物（实测：动图截断时 ffmpeg 退出码仍是 0，
        /// 更不必说真失败的情况；见 docs/TESTING.md 六.11/六.15）。
        /// 只删**本任务新建**的文件；若目标在执行前就已存在，则原件已被 <c>-y</c> 覆盖，
        /// 再删一次属于二次销毁 ⇒ 保留并显式告警。
        /// </summary>
        private static void TryDiscardPartialOutput(QueueItem item, string? outputPath, bool existedBefore)
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            long len;
            try { if (!File.Exists(outputPath)) return; len = new FileInfo(outputPath).Length; }
            catch { return; }

            if (existedBefore)
            {
                item.Log += $"[清理] ⚠️ 任务未成功，而输出路径在执行前已有文件（被 -y 覆盖为 {len}B 的半成品）⇒ 不擅自删除，请重新转换\n";
                return;
            }
            try
            {
                File.Delete(outputPath);
                item.Log += $"[清理] 任务未成功 ⇒ 已删除本次新建的半成品输出（{len}B），不留在原地冒充合法产物\n";
            }
            catch (Exception ex)
            {
                item.Log += $"[清理] ⚠️ 半成品输出删除失败：{ex.Message}\n";
            }
        }

        /// <summary>
        /// P1-3：把临时件替换为正式产物。绝不再用 <c>Delete</c> + <c>Move</c> 两步——中间一步失败就什么都不剩了。
        /// 同卷走 <c>File.Move(..., overwrite:true)</c>（改名，原子）；跨卷会抛 IOException，
        /// 此时改为“先复制到目标目录的旁路件，再在同目录内原子改名”，两条路任何一步失败原产物都完整在位。
        /// </summary>
        private static void TryAtomicReplace(string sourcePath, string destPath)
        {
            try
            {
                File.Move(sourcePath, destPath, overwrite: true);
                return;
            }
            catch (IOException)
            {
                // 跨卷：MoveFileEx 不支持，退回“同目录旁路件 + 原子改名”
            }
            var swap = destPath + ".swap.tmp";
            try
            {
                File.Copy(sourcePath, swap, overwrite: true);
                File.Move(swap, destPath, overwrite: true);
                TryCleanupTemp(sourcePath);
            }
            catch
            {
                TryCleanupTemp(swap);
                throw;   // 交由调用方记日志；此处原产物从未被删除
            }
        }

        /// <summary>
        /// **自动光子噪声的 ISO 解析（cjxl 专用，2026-09-30 上提到分叉之前）**：
        /// 把 UI 的"auto"意图换成 cjxl 能吃的浮点数。
        ///
        /// 为什么必须是**分叉前的一次调用**（此前它内联在 `ProcessCjxlAsync` 里）：cjxl 的
        /// `--photon_noise_iso` 解析器是 `ParseFloat && >= 0`（libjxl `tools/cjxl_main.cc`），
        /// **没有 `auto` 这个取值** —— 未解析的占位串会让 cjxl 在读取 stdin 之前就以 1 退出
        /// （实测 2026-09-30：`Unable to interpret as float: auto.`、零产物、写方 broken pipe）。
        /// 而 `:714` 的分派是 `backend == Cjxl && !engineRoutable` ⇒ 引擎可走时**根本进不到**
        /// `ProcessCjxlAsync`，内联版本等于只服务了一条路线；`RawColorPipeline` 的 cjxl 出口
        /// 直接拿未解析的 options 建命令行 ⇒ 勾了自动光子噪声的 jxl 任务在默认路线上必失败。
        ///
        /// 判不到 ISO（无 exiftool / 无 EXIF，典型 = PNG 输入）⇒ **清掉标志**，让"跳过"名副其实。
        /// `BuildCjxlArguments` 另有一道 fail-closed 兜底（真执行时宁可省略也不发占位串），
        /// 防的是今后第三条路线重犯，不替代本方法。
        /// </summary>
        private async Task ResolveCjxlPhotonNoiseIsoAsync(QueueItem item, CancellationToken ct)
        {
            if (!item.Options.CjxlAutoPhotonNoise || item.Options.CjxlPhotonNoiseIso > 0) return;

            var iso = await ExifToolService.ReadIsoAsync(item.InputPath, s => item.Log += s, ct);
            if (iso.HasValue)
            {
                // cjxl 建议值域 100-3200，超出则钳制
                item.Options.CjxlPhotonNoiseIso = Math.Clamp(iso.Value, 100, 3200);
                item.Log += $"[cjxl] 自动光子噪声: 从 EXIF 读取 ISO={iso.Value} → --photon_noise_iso={item.Options.CjxlPhotonNoiseIso}\n";
            }
            else
            {
                item.Log += "[cjxl] 自动光子噪声: 无法读取 ISO（无 exiftool 或无 ISO 元数据），跳过\n";
                // 让那句「跳过」名副其实并在此处兜住：cjxl 没有 auto 这个取值（BuildCjxlArguments
                // 里的 auto 只是 UI 预览占位），标志不清就会把一条跑不通的参数真的发给编码器。
                item.Options.CjxlAutoPhotonNoise = false;
            }
            _onItemUpdated?.Invoke(item);
        }

        /// <summary>cjxl 编码（优先直接编码，失败自动转 PNG 再试）</summary>
        private async Task ProcessCjxlAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            // 自动光子噪声的 ISO 已在**分叉之前**解析完（`ResolveCjxlPhotonNoiseIsoAsync`，
            // 2026-09-30 上提）⇒ 此处不再重复读 EXIF：两条路线必须传同一份已解析 options，
            // 否则"引擎出口拿到未解析的 auto"就是本次修的那个缺陷的形状。
            var isJpegInput = CjxlService.IsJpegInputPath(item.InputPath);

            // ── 非 JPEG 输入：保留源 ICC（exiftool 无法写入 JXL 的 ICC，需编码器侧嵌入）──
            // JPEG 输入走无损重封装会自动保留原始 JPEG 元数据（含 ICC），无需处理。
            // Ultra HDR 解码输出跳过：线性 HDR 帧色彩由 color_space/intensity_target 表达。
            // 仅在 PreserveAll 元数据模式下处理（StripAll 时用户不想要任何元数据）。
            // 关键：cjxl 对 PNG 等容器输入会忽略 -x icc_pathname（仅 PPM/PAM raw 流生效），
            //       但会自动读取输入 PNG 内嵌的 ICC 块 → 将 ICC 嵌入输入副本即可。
            //       其他输入（TIFF/WebP 等）cjxl 直接编码会失败 → 走 ffmpeg 管道（-x 生效）。
            // ⚠ 上面"直接编码失败才走管道"的分支动机是**色彩**，但"-x 生效"这句要说清边界（#44 实测，
            //   取证 `tests/output/t44/cjxl_x_probe2.log` / `noise_floor.log`）：cjxl 的规则是
            //   「**输入自带色彩元数据（ICC/CICP）时以输入为准、`-x` 被静默忽略**」，与容器还是裸流无关
            //   （HDR PNG 三种 `-x color_space` 得到同一份字节；同源 PPM 才生效；无 ICC 的 SDR PNG 也生效）。
            //   且 `-x` **只写声明、不转像素** ⇒ 光靠"换条路 + 传 -x"修不了 #44，只会把"目标被忽略"
            //   变成"声称 P3、像素仍是原域"。要兑现目标必须在**交给 cjxl 之前**把像素转好。
            string? srcIccPath = null;
            string? iccEmbedTmpCopy = null;   // ICC 嵌入输入副本（临时 PNG，编码后须清理）
            if (!isJpegInput
                && item.Options.MetadataMode == Models.MetadataMode.PreserveAll
                && string.IsNullOrWhiteSpace(item.Options.DecodedUltraHdrColorSpace))
            {
                var (icc, _) = IccProfileService.ExtractIccToTempFile(item.InputPath);
                if (icc != null)
                {
                    var inputExt = Path.GetExtension(item.InputPath).ToLowerInvariant();
                    if (inputExt is ".png" or ".apng")
                    {
                        // PNG 输入：ICC 嵌入输入副本，cjxl 自动读取（-x 对容器输入无效）
                        try
                        {
                            var tmpCopy = Path.Combine(PlatformServices.GetTempDir(),
                                $"icc_embed_{Guid.NewGuid():N}.png");
                            File.Copy(item.InputPath, tmpCopy, overwrite: true);
                            var embedExit = await ExifToolService.EmbedIccProfileFromFileAsync(icc, tmpCopy,
                                s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                            if (embedExit == 0)
                            {
                                item.Log += "[cjxl] 源 PNG 带 ICC，已嵌入输入副本供 cjxl 自动读取\n";
                                item.InputPath = tmpCopy;  // 编码使用副本（含 ICC）
                                iccEmbedTmpCopy = tmpCopy; // 登记：任务结束随 finally 删除（此前泄漏，P2）
                                srcIccPath = icc;
                            }
                            else
                            {
                                TryDeleteIcc(tmpCopy);
                            }
                        }
                        catch { TryDeleteIcc(icc); }
                    }
                    else
                    {
                        // 其他输入：交给 BuildCjxlArguments 的 icc_pathname（管道路径生效）
                        srcIccPath = icc;
                        item.Log += "[cjxl] 检测到源文件 ICC，将由 cjxl 嵌入输出 JXL\n";
                    }
                }
            }

            try
            {
            // ── #44-① 插点 B：直编前那道「要不要转像素」的闸门 ──
            // 直连 cjxl **做不到**改像素：cjxl 对自带色彩元数据的输入会忽略 `-x`，而 `-x` 本来也**只写声明、
            // 不转像素**（上方 #44 那段已登记实测）⇒ 只要裁决点要求的出口语义与输入侧声明不同，用户的显式
            // 目标就会被静默吃掉。此时改道插点 A 的管道路线（ffmpeg 按 dec 转像素 → 裸 PPM/PAM
            // → cjxl `-x color_space=`，裸流输入下 `-x` 生效）。
            // ⚠ 判据是**语义差**（见下面 `jxlNeedsRemap` 那段的读数），不是链的数量 —— 原写法
            //   `dec.PixelChains.Count > 0` 在 jxl 这一族恒假（裁决点被 `isRgbNativeFmt` 挡掉了 tonemap 链），
            //   与 `LogInjectedScale` 的"读命令行"同口径地，这里要读的是**交付什么**而不是**有没有链**。
            // ⚠ 三条护栏：
            //   ① 无链（auto / 直通 / 保真 / JPEG 无损重封装 / 动图）⇒ 下面每一句照旧执行，
            //      日志文案一字不动（`verify-webp-anim-probe.ps1` 逐字匹配「[cjxl] 直接编码 …」两行）；
            //   ② 动图不改道：PPM/PAM 管道是**单帧**裸流，改道会把动画静默截成一帧；既有那条
            //      「回退到 FFmpeg libjxl_anim」分支走主构建器（#43 已修好，能兑现目标），语义不动；
            //   ③ 出口语义必须**落得进 cjxl 的 color_space 枚举**（只有 sRGB / DisplayP3 / Rec2100PQ /
            //      Rec2100HLG，见 `ColorSpaceRegistry.MapPrimariesTransferToCjxl`）才改道：本路线**不给
            //      ICC**（裸流 + `-x` 是唯一声明手段），命名不了的目标（典型 = SDR BT.2020）改了只会
            //      得到「像素已转 BT.2020、标签仍是 cjxl 对裸流的默认解读」⇒ 比「目标被忽略」更坏
            //      （§#44「半个修法比现状更坏」那条告警），故维持现状。
            // ⚠ JPEG 输入 + 显式目标：改道后**不再**有 `-d 0 --lossless_jpeg=1` 的 DCT 无损重封装 —— 这是
            //   本格**唯一**能兑现目标的方式（要转像素就必须先解码重编），下面那行 `[cjxl] target needs
            //   pixel remap …` 点名了改道原因，不静默。
            var jxlDecision = FfmpegCommandBuilder.DecideOutputColor(item.Options, item.InputPath);
            // ── #44 观测面（本次新增，缺陷一的诊断入口）──
            // 本旁路原先**零读数**：实测 `tests/output/post44/jxl_legacy_*/o.txt` 里 `grep -c 色彩裁决` = 0
            // ⇒ 闸门为何从不触发（插点 B 的判据 `PixelChains.Count > 0`）无从判断，只能猜。
            // ⚠ 主消费点（`:810` 那句 `[色彩裁决] 裁决=…`）在 jxl 直编路线上**到不了**，所以这一行打的是
            //   **本旁路自己**那一次 `DecideOutputColor` 的结果 —— 复用既有 `Short()` 摘要，不另立第二份字段口径。
            // 文案 ASCII + `[` 前缀 + `item.Log +=` sink ⇒ 落进 `_probe-cjk-hardcode-scan` 不参与判据的
            // CsTag/CsDiag 桶（UI 面余量只有 10，不动它）。
            var jxlObsVerdict = FfmpegCommandBuilder.ResolveLegacyVerdictPlan(item.Options, item.InputPath)?.Verdict;
            item.Log += $"[色彩裁决] 通路=cjxl-legacy {jxlDecision.Short()}"
                      + $" reject={(jxlObsVerdict == null ? "null" : jxlObsVerdict.Kind.ToString())}\n";
            _onItemUpdated?.Invoke(item);
            // ── #44 缺陷一的新判据（2026-09-27，由上面那行观测读数定出来的，不是先猜再验）──
            // 实测读数（`tests/output/post44obs/jxl_legacy_*/o.txt`，五格 `链数` 全为 0）：
            //   auto  出口=bt2020/smpte2084 链数=0 · p3 出口=smpte432/iec61966-2-1 链数=0 · srgb 出口=bt709/iec61966-2-1 链数=0
            //   bt2020 出口=bt2020/bt709 链数=0 · p3pq 出口=bt2020/smpte2084 链数=0
            // ⇒ 闸门 `PixelChains.Count > 0` **从不触发**的因，是裁决点自己给 jxl 算出了**空链**
            //   （`isRgbNativeFmt` 含 jxl，而 tonemap 链的许可支只补了 png/tiff ⇒ HDR→SDR 既不 tonemap
            //    也不 zscale），而出口语义**已经**被换成目标令牌 ⇒ 「链为空但目标已变」这一格原判据失明。
            // ⇒ 判据改成读**语义**：用户显式指定的目标 ≠ 直编路线将要交付的语义（= 输入侧声明，因为
            //   cjxl 直编永不改像素）。链非空仍是充分条件（保 png/tiff 那类既有改道），两者取或。
            var jxlNeedsRemap = jxlDecision.PixelChains.Count > 0
                                || FfmpegCommandBuilder.DirectEncodeTargetDiffers(jxlDecision);
            var jxlPipeSpace = (!IsAnimated(item) && jxlNeedsRemap)
                ? ColorEncodingHelper.MapPrimariesTransfer(jxlDecision.OutPrimaries, jxlDecision.OutTrc)
                : null;
            // ── 出口语义落不进 cjxl 的 color_space 枚举时，改走 **ICC 描述**（#44 的最后一格）──
            // 实测（`tests/output/t44/icc_pathname_probe.ps1`）：cjxl 在**裸 PPM/PAM 输入**上
            //   `-x icc_pathname=` 生效 —— 无 flag `42139 B/92278FBF…`（jxlinfo `sRGB primaries`）
            //   vs 带 ICC `42374 B/5FFED91E…`（jxlinfo `584-byte ICC profile, CMM type "lcms"`）；
            //   而 `-x color_space=Rec2020` 被 cjxl 直接拒收（`exit=1`，无产物）⇒ SDR BT.2020 只能这么落。
            // ⚠ 这与"exiftool **后置**写 JXL ICC 无效"（`verify-color-caps.ps1:52` 钉的那条，仍然成立）
            //   是**两层不同的事**：那是编完之后往容器补，这是编码时由 cjxl 自己嵌。别混着一句带过。
            string? jxlPipeIcc = null;
            if (jxlPipeSpace == null && jxlNeedsRemap && !IsAnimated(item)
                && !string.IsNullOrWhiteSpace(jxlDecision.OutPrimaries)
                && !string.IsNullOrWhiteSpace(jxlDecision.OutTrc))
            {
                jxlPipeIcc = IccProfileService.GetOrGenerateStandardIcc(item.InputPath,
                    jxlDecision.OutPrimaries!, jxlDecision.OutTrc!,
                    jxlDecision.MatrixForOutput ?? "bt709",
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            }
            if (jxlPipeSpace == null && jxlPipeIcc == null && jxlNeedsRemap && !IsAnimated(item))
            {
                // 既不能命名、又生成不出 ICC ⇒ 宁可维持现状并在此**点名**（本仓价值序：静默 < 响亮但看不懂
                //   < 响亮且点名），也绝不"改道后只改标签"（§#44「半个修法比现状更坏」）。
                // 文案 ASCII：`_probe-cjk-hardcode-scan` 的 UI 面余量只有 10。
                item.Log += $"[cjxl] WARNING: target {jxlDecision.OutPrimaries ?? "unknown"}/{jxlDecision.OutTrc ?? "unknown"}"
                          + $" has no cjxl color_space enum and this route cannot carry an ICC"
                          + $" => direct encode kept, pixels stay {jxlDecision.DeclPrimaries ?? "unknown"}/{jxlDecision.DeclTrc ?? "unknown"}"
                          + " (target NOT applied); use --color-engine engine or a CICP-nameable target\n";
                _onItemUpdated?.Invoke(item);
            }
            if (jxlPipeSpace != null || jxlPipeIcc != null)
            {
                // 文案 ASCII：`_probe-cjk-hardcode-scan` 的 UI 面余量只有 10
                item.Log += $"[cjxl] target needs pixel remap ({jxlDecision.PixelChains.Count} chain) "
                          + $"=> skip direct encode, use ffmpeg pipe + cjxl "
                          + (jxlPipeSpace != null ? $"-x color_space={jxlPipeSpace}\n"
                                                  : $"-x icc_pathname=<generated {jxlDecision.OutPrimaries}/{jxlDecision.OutTrc}>\n");
                _onItemUpdated?.Invoke(item);
                var pixelPipe = await PipeFfmpegToCjxlAsync(item, outputPath, ct, jxlDecision, jxlPipeIcc);
                item.ExitCode = pixelPipe.exitCode;
                item.Status = pixelPipe.status;
                if (pixelPipe.exitCode == 0)
                    await RestoreMetadataAsync(item, outputPath);
                return;   // finally 照旧执行（清理提取的临时 ICC 与 ICC 输入副本）
            }

            // 第一步：直接尝试 cjxl
            item.Command = "cjxl " + CjxlService.BuildCjxlArguments(item.InputPath, outputPath, item.Options, default, srcIccPath);
            var inputExtForCjxl = Path.GetExtension(item.InputPath).ToLowerInvariant();
            // effort 的缺省值与 CjxlService.BuildCjxlArguments 同一口径（未设置时 cjxl 默认 7）：
            // 旧写法此处写 5，而它上一行生成的 item.Command 已经是 7 ⇒ 日志与命令两个数不一致。
            item.Log += $"[cjxl] 直接编码 (输入: {inputExtForCjxl}, 目标: jxl, effort={item.Options.JxlEffort ?? 7}, threads={item.Options.Threads})\n";
            _onItemUpdated?.Invoke(item);
            // A-1（2026-09-26）：两条分支传同一份 options（item.Options）—— 此前 JPEG 分支另起一份逐字段
            // 搬运清单，漏掉 Lossless 与色彩字段 ⇒ 用户勾的无损被吞成有损距离，而它上方那行 item.Command
            // 却是用完整 opts 生成的 ⇒ 详情窗显示的命令与实际执行的并非同一条。effort 的缺省值不在此处补，
            // 由 CjxlService.BuildCjxlArguments 唯一负责；JPEG 输入时 srcIccPath 恒为 null
            // （上面那段 ICC 保留只在非 JPEG 分支里赋值）⇒ 合并调用不改变 ICC 语义。
            if (isJpegInput)
            {
                item.Log += item.Options.JxlLosslessJpeg
                    ? "[cjxl] 检测到 JPEG 输入，启用无损重封装模式（-d 0 --lossless_jpeg=1，不解码 DCT 系数）\n"
                    : "[cjxl] 检测到 JPEG 输入，关闭无损重封装 → 解码后按质量参数重新编码\n";
            }
            var exitCode = await CjxlService.RunWithOptionsAsync(
                item.InputPath, outputPath, item.Options,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, srcIccPath, ct: ct);

            if (exitCode == 0)
            {
                item.ExitCode = 0;
                // A-2（2026-09-26）：状态按是否真走 jbrd 判定，与上面那句按 JxlLosslessJpeg 分支的日志同口径；
                // 旧写法只看输入是不是 JPEG ⇒ 关闭无损重封装时也写无损重封装，自相矛盾。
                // ⚠⚠ 2026-10-05（P0 修复）：判据必须与 `CjxlService.UsesLosslessJpegRewrap` **同源**
                //   （那是"本次真的走重封装"的唯一实现）。旧写法只看 `isJpegInput && JxlLosslessJpeg`，
                //   漏掉了 UsesLosslessJpegRewrap 的另外几个条件（增益图/强制模块化/显式剥除），
                //   会把"没走重封装"的本次也标成"无损重封装"。
                bool didRewrap = CjxlService.UsesLosslessJpegRewrap(item.InputPath, item.Options);
                if (didRewrap)
                    item.Status = "已完成 (cjxl 无损重封装)";
                else
                    item.Status = "已完成 (cjxl)";
                if (didRewrap)
                {
                    // ⚠⚠ 2026-10-05（P0 修复）：**jbrd 重封装产物不得再做任何元数据写入**。
                    //   根因（实测，三段对照见 `ProcessJxlInputAsync` 里更完整的记录）：
                    //     cjxl 产出的 JXL 带 `jbrd` 重构数据（可逐字节还原原始 JPEG），
                    //     而 `RestoreMetadataAsync` → exiftool 会**改写容器内 EXIF/APP 段结构**
                    //     ⇒ djxl 之后**拒绝无损重构**、静默降级为 `--pixels_to_jpeg` 像素重编码
                    //       （退出码仍为 0）⇒ 逐字节可逆性丧失、色度 4:2:0 变 4:4:4。
                    //   实测铁证（同一夹具 23917 B）：
                    //     · 纯 cjxl → 纯 djxl ⇒ `Reconstructed to JPEG`、SHA 相同 ✅
                    //     · 同一 JXL 跑一次 `exiftool -GPS:all=` → djxl ⇒ `could not decode losslessly` ❌
                    //   而 `StripExifGps` **默认开启** ⇒ 这在**默认路径**上就会发生。
                    //   ⇒ 元数据已随原始码流一起进入 JXL，**无需也无法**在容器层再写；
                    //     保住"逐字节可逆"这条唯一卖点优先于容器层标签整理。
                    item.Log += "[cjxl] 本次为 jbrd 无损重封装 ⇒ 跳过元数据恢复（产物含可逐字节还原的原始 JPEG 码流，任何容器层元数据写入都会破坏该能力）\n";
                    // ⚠⚠ 2026-10-05：**把"隐私承诺与无损可逆不可兼得"显式化**（不得静默）。
                    //   事实（实测）：jbrd 把**源 JPEG 码流逐字**放进产物 ⇒ 源里的 GPS/EXIF 必然随之进入；
                    //   而一旦对容器做元数据写入来剥除它们，djxl 就**拒绝无损重构**（降级像素重编码）。
                    //   ⇒ 二者在"JPEG→JXL 且启用 jbrd"这一格上**结构上不可兼得**。
                    //   用户裁定「无损重封装是**绝对默认**」⇒ 本次**保住可逆性**，但必须
                    //   **响亮点名"隐私剥除本次未生效"**，并给出可执行的替代路径 —— 绝不静默。
                    //   （`--strip-gps` 等**显式**请求已在 `CjxlService.UsesLosslessJpegRewrap` 里
                    //     使本次**放弃**重封装 ⇒ 那条路会真正剥除，见该谓词的注释。）
                    bool privacyWanted = item.Options.StripExifGps || item.Options.StripExifAll
                                      || item.Options.StripXmp || item.Options.StripExifTime
                                      || item.Options.StripExifCamera
                                      || item.Options.MetadataMode == Models.MetadataMode.StripAll;
                    if (privacyWanted)
                    {
                        // ⚠ 每条 `item.Log +=` 必须是**完整单语句**：拆成续行（`+ "中文"`）时续行不命中
                        //   CJK 门禁的 sink 判定 ⇒ 被计进 `CsUi` 判据桶（余量为 0，不得增加）。
                        item.Log += "[cjxl] ⚠️ 隐私提示：本次为 jbrd 无损重封装，产物内的 EXIF/GPS 与源逐字节一致（这是『可逐字节还原原始 JPEG』的前提，无法在保持可逆的同时剥除）。\n";
                        item.Log += "[cjxl] ⚠️ 若需要真正剥除 GPS/EXIF，请显式给出剥除开关（如 --strip-gps / --strip-metadata）—— 那会让本次放弃重封装并执行剥除。\n";
                        item.Log += "[cjxl] ⚠️ 或改用 --jxl-lossless-jpeg false（解码后重编码，可自由剥除元数据）。\n";
                    }
                    return;
                }
                await RestoreMetadataAsync(item, outputPath);
                return;
            }

            // 动图不进行 PNG 中转（会丢失动画帧），回退到 FFmpeg 内置编码器
            if (IsAnimated(item))
            {
                item.Log += $"[cjxl] 动图/不支持格式（退出码 {exitCode}），回退到 FFmpeg libjxl_anim 编码器\n";
                _onItemUpdated?.Invoke(item);

                var ffmpegOpts = CloneOptionsForFfmpeg(item.Options);
                var args = BuildArgsAndNote(item, ffmpegOpts, item.InputPath, outputPath);
                item.Command = "ffmpeg " + args; item.Log += $"[cmd] ffmpeg {args}\n";
                var ffExit = await FfmpegRunner.RunAsync(args, s =>
                {
                    item.Log += s;
                    _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath, ct);
                item.ExitCode = ffExit;
                item.Status = ffExit == 0 ? "已完成 (cjxl→ffmpeg 回退)" : $"失败 (ffmpeg 退出码 {ffExit})";
                return;
            }

            // 第二步：直接编码失败 → ffmpeg 管道直通 cjxl（无中间文件）
            var inputExtForPipe = Path.GetExtension(item.InputPath).ToLowerInvariant();
            item.Log += $"[cjxl] 直接编码失败 (退出码 {exitCode})，{inputExtForPipe} 不被 cjxl 直接支持，将通过 ffmpeg 管道直通 cjxl（无磁盘中间文件）\n";
            _onItemUpdated?.Invoke(item);

            var pipeResult = await PipeFfmpegToCjxlAsync(item, outputPath, ct);
            item.ExitCode = pipeResult.exitCode;
            item.Status = pipeResult.status;
            if (pipeResult.exitCode == 0)
                await RestoreMetadataAsync(item, outputPath);
            }
            finally
            {
                TryDeleteIcc(srcIccPath);  // 清理提取的临时 ICC 文件
                // ICC 嵌入副本是本任务生成的临时 PNG，编码后必须删除（此前每次泄漏一整份输入）
                if (iccEmbedTmpCopy != null)
                {
                    try { if (File.Exists(iccEmbedTmpCopy)) File.Delete(iccEmbedTmpCopy); } catch { }
                }
            }
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
                var args = BuildArgsAndNote(item, ffmpegOpts, item.InputPath, outputPath);
                item.Command = "ffmpeg " + args; item.Log += $"[cmd] ffmpeg {args}\n";
                var ffExit = await FfmpegRunner.RunAsync(args, s =>
                {
                    item.Log += s;
                    _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath, ct);
                item.ExitCode = ffExit;
                item.Status = ffExit == 0 ? "已完成 (cjpegli→ffmpeg 回退)" : $"失败 (ffmpeg 退出码 {ffExit})";
                return;
            }

            // 第二步：直接编码失败 → ffmpeg 管道直通 cjpegli（无中间文件）
            var ext = Path.GetExtension(item.InputPath).ToLowerInvariant();
            item.Log += $"[cjpegli] 直接编码失败 (退出码 {exit})，{ext} 不被支持，通过 ffmpeg 管道直通 cjpegli（无磁盘中间文件）\n";
            _onItemUpdated?.Invoke(item);

            var pipeResult = await PipeFfmpegToCjpegliAsync(item, outputPath, ct);
            item.ExitCode = pipeResult.exitCode;
            item.Status = pipeResult.status;
            if (pipeResult.exitCode == 0)
                await RestoreMetadataAsync(item, outputPath);
        }

        /// <summary>
        /// Gain Map (Ultra HDR) JPEG 编码（纯 C# 管线，无 ultrahdr 依赖）：
        /// 1. ffmpeg 解码 → 线性 RGB 浮点像素
        /// 2. GainMapEncoder → 分段 Reinhard 色调映射 → cjpegli 基础图 + 增益图 → 打包
        /// </summary>
        private async Task ProcessGainMapJpegAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var ffmpegPath = AppSettingsService.Current.FfmpegPath;

            if (!CjpegliService.IsAvailable
                && (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath)))
            {
                // 可用性诚实原则：GainMap 用纯 C# 编码 + 所选 JPEG 后端(cjpegli 优先，否则 ffmpeg mjpeg)；
                // 两者皆不可用才报错（不静默产出无增益图的普通 JPEG）。
                throw new InvalidOperationException(
                    "已启用 Gain Map (Ultra HDR) JPEG，但 cjpegli 与 ffmpeg(mjpeg) 均不可用，无法编码增益图。");
            }

            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"gainmap_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                // Step 1: 尺寸
                item.Log += "[GainMap] Step 1: 获取图像尺寸...\n";
                _onItemUpdated?.Invoke(item);
                var (width, height) = await ProbeImageSizeAsync(item.InputPath, ffmpegPath,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                if (width <= 0 || height <= 0)
                {
                    item.ExitCode = -1;
                    item.Status = "失败 (无法获取图像尺寸)";
                    return;
                }
                item.Log += $"[GainMap]   尺寸: {width}x{height}\n";

                // Step 2: ffmpeg 解码 → 线性 RGB 浮点 (gbrpf32le planar rawvideo)
                //   HDR 输入 (PQ/HLG) → zscale t=linear → 线性空间; 1.0 = 峰值亮度 (npl)
                //   SDR 输入 → zscale t=linear → 1.0 = SDR 白点 (headroom=1, 无增益)
                // ⚠ 解码串与峰值/原色参数自 2026-09-16 起由 GainMapJpegCodec 提供**单一真值**：
                //   引擎的 GainMap 出口（RawColorPipeline）走同一份 —— 两条路径若各写一份，
                //   同一素材会得到不同 headroom（偏暗/增益范围错），且极难定位。
                var gp = ColorMapping.GainMapJpegCodec.ResolveEncodeParams(item.Options, item.InputPath);
                var isHdrInput = gp.IsHdrInput;
                var nits = gp.Nits;
                var peakNits = gp.PeakNits;
                var srcPrim = gp.SrcPrim;
                if (isHdrInput)
                    item.Log += $"[GainMap]   源峰值 {peakNits:F0} nits（来源：{gp.PeakSourceText}）\n";
                item.Log += $"[GainMap]   源原色: {srcPrim}（底图将由此映射到目标底图色域）\n";

                var rawPath = Path.Combine(tempDir, "hdr.rgba");
                // 尝试 1: zscale 转线性 (HDR PQ/HLG 输入)；pin= 锁定原色语义
                item.Log += $"[GainMap] Step 2: ffmpeg 解码为线性 RGB 浮点 " +
                    $"({(isHdrInput ? $"HDR PQ/HLG npl={nits}" : "SDR")})...\n";
                _onItemUpdated?.Invoke(item);
                var linArgs = ColorMapping.GainMapJpegCodec.LinearDecodeArgs(item.InputPath, rawPath, nits, srcPrim);
                item.Log += $"[cmd] ffmpeg {linArgs}\n";
                var decodeExit = await FfmpegRunner.RunAsync(
                    linArgs,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ffmpegPath, ct);
                // 尝试 2: zscale 失败 (无色彩元数据的线性输入如 dngtool TIFF) → 直接解码
                if (decodeExit != 0 || !File.Exists(rawPath))
                {
                    item.Log += "[GainMap] ⚠️ zscale 线性转换失败，尝试直接解码 (输入可能已是线性)...\n";
                    _onItemUpdated?.Invoke(item);
                    var dirArgs = ColorMapping.GainMapJpegCodec.DirectDecodeArgs(item.InputPath, rawPath);
                    item.Log += $"[cmd] ffmpeg {dirArgs}\n";
                    decodeExit = await FfmpegRunner.RunAsync(
                        dirArgs,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ffmpegPath, ct);
                }
                if (decodeExit != 0 || !File.Exists(rawPath))
                {
                    item.Log += $"[GainMap] ⚠️ ffmpeg 线性解码失败 (exit {decodeExit})\n";
                    _onItemUpdated?.Invoke(item);
                    item.ExitCode = decodeExit;
                    item.Status = $"失败 (线性解码退出码 {decodeExit})";
                    return;
                }
                PlatformServices.MarkAsTemporaryFile(rawPath);

                // Step 3: 读取像素 (gbrpf32le planar: G/B/R 三平面) → RGBA 交错
                int pixelCount = width * height;
                var rawBytes = await File.ReadAllBytesAsync(rawPath, ct);
                if (rawBytes.Length < pixelCount * 3L * 4L)
                {
                    item.Log += "[GainMap] ⚠️ RAW 像素数据不完整\n";
                    _onItemUpdated?.Invoke(item);
                    item.ExitCode = -1;
                    item.Status = "失败 (像素数据不完整)";
                    return;
                }
                // gbrpf32le: 平面顺序 G, B, R (ffmpeg gbr 命名), 各 pixelCount 个 float
                // ⚠ 交错实现与引擎 GainMap 出口共用同一份（GainMapJpegCodec.GbrPlanarToRgba）：
                //   平面序理解不一致只会表现为 R/B 互换（大图上才显形），必须单一真值。
                var pixels = ColorMapping.GainMapJpegCodec.GbrPlanarToRgba(rawBytes, pixelCount);

                // Step 4: GainMapEncoder 纯 C# 编码
                var gmq = item.Options.JpegGainMapQuality >= 0
                    ? item.Options.JpegGainMapQuality : 75;
                // 质量 → butteraugli distance (与 cjpegli 语义一致)
                float baseDist = Math.Clamp((100f - item.Options.Quality) * 0.25f, 0.5f, 6.0f);
                float gmDist = Math.Clamp((100f - gmq) * 0.25f, 0.5f, 6.0f);

                item.Log += $"[GainMap] Step 3: GainMapEncoder 编码 (纯 C#, 峰值 {peakNits:F0}nits, " +
                    $"base d={baseDist:F2}, gm d={gmDist:F2}, 底={(string.Equals(item.Options.JpegGainMapBaseGamut, "bt2020", StringComparison.OrdinalIgnoreCase) ? "Rec.2020(SDR)" : "sRGB(SDR)")})...\n";
                _onItemUpdated?.Invoke(item);
                item.Command = "[GainMap] 纯托管编码 (GainMapEncoder + cjpegli)";
                // 渐进级别：**单一真值** `GainMapJpegCodec.ProgressiveLevelOf`（`1→2 / 0→0 / 其它→-1`），
                // 与引擎 GainMap 出口（`RawColorPipeline`）共用同一份映射 —— 调用点**不得**另写映射。
                // ⚠ 2026-09-19 接线：此前本调用点**不传**该形参（吃默认 `-1` = 不传 `-p`），
                //   于是 `--jpeg-progressive 0`（要基线）在本专用管线上被静默忽略、恒得 cjpegli
                //   默认的渐进底图。接线后 `0` ⇒ `-p 0` ⇒ SOF1 顺序、`1` ⇒ `-p 2` ⇒ SOF2 渐进。
                // 文案纯 ASCII：`_probe-cjk-hardcode-scan.ps1` 的 C# UI 面余量为 0（`item.Log +=` 属
                // 诊断 sink 本不判，但保持一致以免下次口径变化时踩线）。
                int gmProgLv = ColorMapping.GainMapJpegCodec.ProgressiveLevelOf(item.Options);
                item.Log += gmProgLv >= 0
                    ? $"[GainMap] JPEG progressive level -p {gmProgLv} (--jpeg-progressive={item.Options.JpegProgressiveId})\n"
                    : "[GainMap] JPEG progressive level: unspecified (cjpegli default)\n";
                var ok = await GainMapEncoder.EncodeAsync(
                    pixels, width, height, outputPath,
                    hdrPeakNits: peakNits, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
                    multiChannel: item.Options.JpegGainMapMultiChannel,
                    baseQuality: baseDist, gainMapQuality: gmDist,
                    downsample: Math.Max(item.Options.JpegGainMapDownsample, 1),
                    baseGamut: item.Options.JpegGainMapBaseGamut,
                    sourcePrimaries: srcPrim,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct,
                    jpegProgressiveLevel: gmProgLv,
                    container: GainMapEncoder.ContainerOf(outputPath));

                item.ExitCode = ok ? 0 : -1;
                item.Status = ok ? "已完成 (Gain Map 纯托管编码)" : "失败 (GainMapEncoder 编码失败)";
                if (ok)
                    await RestoreMetadataAsync(item, outputPath);
            }
            catch (OperationCanceledException)
            {
                item.Status = "已停止";
            }
            catch (Exception ex)
            {
                item.Log += $"[GainMap] 编码异常: {ex.Message}\n";
                item.ExitCode = -1;
                item.Status = $"失败: {ex.Message}";
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// JxrEncApp 编码：ffmpeg 解码输入 → BMP 临时文件 → JxrEncApp 编码为 JPEG XR。
        /// JxrEncApp 原生支持 BMP/TIFF 输入，BMP 是无损中间格式。
        /// </summary>
        private async Task ProcessJxrAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            if (!JxrService.IsAvailable)
            {
                item.Log += "[jxr] JxrEncApp.exe 未检测到，无法编码\n";
                item.ExitCode = -1;
                item.Status = "失败 (JxrEncApp 未找到)";
                return;
            }

            var ffmpegPath = AppSettingsService.Current.FfmpegPath;
            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"jxr_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var inputExt = Path.GetExtension(item.InputPath).ToLowerInvariant();

                string intermediatePath;
                bool isHdr;
                // #44-① 插点 C：第一步是否**真的按裁决点转了像素**，以及转到的目标 ICC 路径/标签。
                // JxrEncApp 写不了 ICC（它只吃 BMP/TIFF 的像素）⇒ 转了像素就必须由 exiftool 在编码成功后
                // 把目标 ICC 补进产物（实测 exiftool 对 JXR 可写、逐字节可读回，见 RawColorPipeline 的
                // JXR 出口注释）；否则得到「像素已是目标域、文件没有任何色彩描述」的半件修法，比不转更坏。
                bool jxrPixelsRemapped = false;
                string? jxrTargetIcc = null;
                string jxrTargetIccLabel = "";

                // ── 常规输入：ffmpeg 解码 → BMP/TIFF ──
                {
                    int bitDepth = item.Options.BitDepth ?? await ProbeBitDepthAsync(item.InputPath, ffmpegPath,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                    isHdr = bitDepth > 8;
                    var intermediateExt = isHdr ? ".tiff" : ".bmp";
                    intermediatePath = Path.Combine(tempDir, $"input{intermediateExt}");
                    var pixFmt = isHdr ? "rgb48le" : "bgr24";
                    item.Log += $"[jxr] 检测位深: {bitDepth}-bit, 像素格式: {pixFmt}\n";

                    item.Log += $"[jxr] Step 1: ffmpeg 解码为 {intermediateExt.ToUpper()}（{pixFmt}）...\n";
                    _onItemUpdated?.Invoke(item);

                    // ── #44-① 插点 C：两步法第一步与色彩裁决点**同源** ──
                    // 旧写法在此**第三遍**自算色彩判定（`BuildJxrColorArgs`）：把**目标** tokens 拼到 `-i`
                    // 之前 ⇒ 只是把"源的解释"改成目标、像素一步不转（`-vf` 那段守卫自证恒为 null）
                    // ⇒ 用户的显式目标被静默吃掉（#44 的 jxr 格）。现：
                    //   输入声明 = dec.Decl*（如实描述源；Ultra HDR 解码输入 bt709/linear 亦由同一处给出，
                    //   与旧 `BuildJxrColorArgs` 第一条分支逐字同值）；像素 = dec.PixelChains（与主构建器、
                    //   cjxl 管道路线**同一份链**）。矩阵声明走白名单 `ToFfmpegInputMatrixName`（`gbr`→`rgb`、
                    //   不可用则不拼），与 `BuildArguments` 同一份口径。
                    var jxrDecision = FfmpegCommandBuilder.DecideOutputColor(item.Options, item.InputPath);
                    var colorArgs = "";
                    if (!string.IsNullOrWhiteSpace(jxrDecision.DeclPrimaries))
                        colorArgs += $"-color_primaries {jxrDecision.DeclPrimaries} ";
                    if (!string.IsNullOrWhiteSpace(jxrDecision.DeclTrc))
                        colorArgs += $"-color_trc {jxrDecision.DeclTrc} ";
                    var jxrDeclMatrix = ColorSpaceRegistry.ToFfmpegInputMatrixName(jxrDecision.DeclMatrix);
                    if (jxrDeclMatrix != null)
                        colorArgs += $"-colorspace {jxrDeclMatrix} ";
                    var tiffExtra = isHdr ? " -compression_algo raw -pred none" : "";

                    // 无标签 >8bit 一律**不猜**：旧口径「仅 RAW 来源才做 Rec.2020→sRGB 的 HDR 猜测(P9)」此前
                    // 已在 2026-09-18 被实测证伪为不可达（RAW 在上游被 dngtool 预解码成 `*_raw.tiff` 并注入
                    // bt709/linear 声明），那段自证恒为 null 的死守卫随本次同源改造一并删除 —— 判据回到唯一
                    // 一份：改不改像素只由裁决点的 `PixelChains` 说，本处不再留第二套方向。
                    var filterArg = "";
                    if (jxrDecision.PixelChains.Count > 0)
                    {
                        // 逗号串照主构建器的 `-vf` 形态；链尾再收一口 `format={pixFmt}`（与被删除的旧守卫同形），
                        // 保证中转 BMP/TIFF 拿到的正是本分支要写的像素格式。
                        filterArg = $"-vf \"{string.Join(",", jxrDecision.PixelChains)},format={pixFmt}\" ";
                        jxrPixelsRemapped = true;
                    }
                    // ⚠ ICC 的生成**不能**挂在"像素转了没有"这个条件里（2026-09-27 由 #49 的 LEGACY 层实测抓出）：
                    //   请求目标 == 源色域时 `PixelChains` 恒为 0（不转是合法的），旧写法于是既不生成也不嵌入任何
                    //   描述；而 `RestoreMetadataAsync` 那侧又因「用户已指定色彩空间」跳过回带源 ICC
                    //   ⇒ 产物**零标注**，JXR 再无 CICP 可退 ⇒ 查看器按 sRGB 解读 P3/BT.2020 数值（洗白）。
                    //   ICC 描述的是**存进文件的这组值**，与"这一路有没有真的动过像素"无关 ⇒ 只要裁决点给得出
                    //   成对的出口 token 就该嵌。
                    if (!string.IsNullOrWhiteSpace(jxrDecision.OutPrimaries)
                        && !string.IsNullOrWhiteSpace(jxrDecision.OutTrc))
                    {
                        jxrTargetIccLabel = $"{jxrDecision.OutPrimaries}/{jxrDecision.OutTrc}";
                        jxrTargetIcc = IccProfileService.GetOrGenerateStandardIcc(item.InputPath,
                            jxrDecision.OutPrimaries!, jxrDecision.OutTrc!,
                            jxrDecision.MatrixForOutput ?? "bt709",
                            s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                    }
                    else if (jxrPixelsRemapped)
                        jxrTargetIccLabel = "unnamed-by-decision";   // 出口语义拿不到成对 token ⇒ 生成不了标准 ICC
                    // P7a-3 同源登记：算链用的就是 item.Options（未克隆）⇒ 登记同一实例 + 本次真源。
                    // 登记后链尾不再打「裁决点未参与」；但**补 ICC 这一步不能只靠链尾**：共享后置的判据是
                    // `dec.NeedsPostEmbedIcc`，而它要求「该容器的编码器会丢弃 iccgen 的 ICC」
                    // （`FfmpegCommandBuilder.EncoderWritesFilterIcc` 只登记 webp 会丢，jxr 不在其列）⇒ 恒 false，
                    // 故目标 ICC 由上面的 `jxrTargetIcc` + 编码成功后的 exiftool 那一步负责。
                    NoteColorDecisionSource(item, item.Options, item.InputPath);
                    // ⚠ 2026-09-18（#13 站点 A）：中转目标是 BMP/TIFF，走 ffmpeg `image2` 复用器 —— 它按**固定文件名**
                    //   写盘，**不能**承载多帧（多帧输入时 ffmpeg 报 `Cannot write more than one file with the same
                    //   name… Are you missing the -update option or a sequence pattern?` 并以 -22 退出）。
                    //   判据与 #139（FfmpegCommandBuilder.targetHoldsMultipleFrames）同一条：**目标容器是否支持多帧**。
                    //   这里目标恒为单帧 ⇒ 恒插 `-frames:v 1`（不按输入猜）。
                    //   实测（10 帧 webp → BMP）：无该参数 exit=-22；有该参数 exit=0，且产物与「输入侧先截断到 1 帧」
                    //   逐字节相同（md5 f0962bbba643686a6cf34fc68cf7a375）。
                    //   实测（单帧 png / 单帧 16bit png → BMP/TIFF）：加与不加**逐字节相同**
                    //   （8bit md5 50b30a75760707cbc79913a585dd3710，16bit md5 9255a75a0a79e6bdb5dd6159063ca9ff）
                    //   ⇒ 对单帧输入是 no-op，不引入回归。
                    var decodeArgs = $"-y {colorArgs}-i \"{item.InputPath}\" {filterArg}-pix_fmt {pixFmt}{tiffExtra} -frames:v 1 \"{intermediatePath}\"";
                    item.Command = $"ffmpeg {decodeArgs}";
                    // ⚠ 不能省：本文件其余 ffmpeg 调用点一律记 `[cmd] ffmpeg …`，唯独这里曾经只写 `item.Command`
                    //   （UI 字段，不进日志）⇒ 门禁想断言这条命令只能取到空串，会写出必红的断言并误判成产品回归
                    //   （2026-09-17 实测踩到，见 docs/TESTING.md §6 第 65 条）。
                    item.Log += $"[cmd] ffmpeg {decodeArgs}\n";
                    var decExit = await FfmpegRunner.RunAsync(decodeArgs,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ffmpegPath, ct);
                    if (decExit != 0 || !File.Exists(intermediatePath))
                    {
                        item.ExitCode = decExit;
                        item.Status = $"失败 ({intermediateExt} 解码退出码 {decExit})";
                        return;
                    }
                }

                // 提示 OS 优先内存缓存中间文件，减少 SSD 写入
                PlatformServices.MarkAsTemporaryFile(intermediatePath);

                // Step 2: JxrEncApp 编码
                item.Log += "[jxr] Step 2: JxrEncApp 编码 JPEG XR...\n";
                _onItemUpdated?.Invoke(item);
                var quality = item.Options.Quality / 100.0;
                if (item.Options.Lossless) quality = 1.0;
                var jxrArgs = JxrService.BuildArguments(intermediatePath, outputPath, quality);
                item.Command = "JxrEncApp " + jxrArgs;
                var jxrExit = await JxrService.RunAsync(jxrArgs,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);

                item.ExitCode = jxrExit;
                item.Status = jxrExit == 0
                    ? (isHdr ? "已完成 (JxrEncApp JPEG XR HDR)" : "已完成 (JxrEncApp JPEG XR)")
                    : $"失败 (JxrEncApp 退出码 {jxrExit})";
                if (jxrExit == 0)
                {
                    // ── HDR 色彩元数据写入（JxrEncApp 不支持嵌入色彩配置）──
                    if (!string.IsNullOrWhiteSpace(item.Options.DecodedUltraHdrColorSpace))
                    {
                        await WriteJxrHdrMetadataAsync(item, outputPath, ct);
                    }
                    await RestoreMetadataAsync(item, outputPath);
                    // ── #44-① 插点 C 的后半：有色彩描述要落地就得落地（**不再只看"转没转像素"**，同上注）──
                    // 必须在元数据恢复**之后**（否则回带的源 ICC 会与转换后的像素失配，与主分支后置同序）。
                    // 文案 ASCII：`_probe-cjk-hardcode-scan` 的 UI 面余量只有 10。
                    if (jxrPixelsRemapped || !string.IsNullOrWhiteSpace(jxrTargetIcc))
                    {
                        if (!string.IsNullOrWhiteSpace(jxrTargetIcc) && File.Exists(jxrTargetIcc))
                        {
                            var embExit = await ExifToolService.EmbedIccProfileFromFileAsync(
                                jxrTargetIcc, outputPath,
                                s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                            item.Log += embExit == 0
                                ? $"[jxr] target ICC embedded via exiftool ({jxrTargetIccLabel})"
                                  + " - JxrEncApp itself cannot write ICC\n"
                                : $"[jxr] WARNING: target ICC embed failed (exit {embExit})"
                                  + $" ({jxrTargetIccLabel}) - the file has no color description\n";
                        }
                        else
                            item.Log += $"[jxr] WARNING: target {jxrTargetIccLabel} has no standard ICC"
                                      + " - output carries no color description (explicit, not silent)\n";
                        _onItemUpdated?.Invoke(item);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = "已停止";
            }
            catch (Exception ex)
            {
                item.Log += $"[jxr] 异常: {ex.Message}\n";
                item.ExitCode = -1;
                item.Status = $"失败: {ex.Message}";
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>将 HDR 色彩元数据写入 JXR（JxrEncApp 不支持色彩配置，最佳努力写入 EXIF/XMP）</summary>
        private async Task WriteJxrHdrMetadataAsync(QueueItem item, string outputPath, CancellationToken ct = default)
        {
            if (!ExifToolService.IsAvailable) return;
            try
            {
                item.Log += "[jxr] 写入 HDR 色彩元数据...\n";
                _onItemUpdated?.Invoke(item);

                var args = $"-overwrite_original -m " +
                    $"-EXIF:ColorSpace=Uncalibrated " +
                    $"-EXIF:ImageDescription=\"HDR Rec.2100 PQ\" " +
                    $"\"{outputPath}\"";

                var exit = await ExifToolService.RunRawAsync(args,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                if (exit == 0)
                    item.Log += "[jxr] HDR 元数据已写入 (Uncalibrated + Rec.2100 PQ 描述)\n";
                else
                    item.Log += $"[jxr] ⚠️ HDR 元数据写入警告 (退出码 {exit})\n";
            }
            catch (Exception ex)
            {
                item.Log += $"[jxr] HDR 元数据写入异常: {ex.Message}\n";
            }
        }

        /// <summary>10-bit PQ RAW (x2bgr10le) → 无压缩 Radiance HDR (.hdr, 32bppRGBE)，JxrEncApp 可识别为 HDR</summary>
        private static async Task<string?> ConvertRawToRadianceHdrAsync(
            string rawPath, int width, int height, string tempDir,
            Action<string>? logCallback = null)
        {
            var hdrPath = Path.Combine(tempDir, "input.hdr");
            try
            {
                var rawBytes = await File.ReadAllBytesAsync(rawPath);
                var expectedSize = width * height * 4;
                if (rawBytes.Length < expectedSize)
                {
                    logCallback?.Invoke($"[jxr-hdr] RAW size mismatch: {rawBytes.Length} vs {expectedSize}\n");
                    return null;
                }

                // 写入 Radiance 头（无压缩格式）
                using var fs = new FileStream(hdrPath, FileMode.Create, FileAccess.Write);
                using var sw = new StreamWriter(fs, System.Text.Encoding.ASCII, 4096, true);
                sw.NewLine = "\n";
                sw.Write("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {0} +X {1}\n", height, width);
                sw.Flush();

                // 逐像素转换: 10-bit PQ(BGR packed) → linear float → RGBE
                var rgbeRow = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int i = rowOffset + x * 4;
                        uint packed = (uint)(rawBytes[i] | (rawBytes[i + 1] << 8)
                            | (rawBytes[i + 2] << 16) | (rawBytes[i + 3] << 24));
                        // x2bgr10le: B[0:9], G[10:19], R[20:29], X[30:31]
                        float b10 = (packed & 0x3FF) / 1023.0f;
                        float g10 = ((packed >> 10) & 0x3FF) / 1023.0f;
                        float r10 = ((packed >> 20) & 0x3FF) / 1023.0f;
                        float r = PqToLinear(r10);
                        float g = PqToLinear(g10);
                        float b = PqToLinear(b10);
                        RgbToRgbe(r, g, b, out byte R, out byte G, out byte B, out byte E);
                        int dst = x * 4;
                        rgbeRow[dst] = R;
                        rgbeRow[dst + 1] = G;
                        rgbeRow[dst + 2] = B;
                        rgbeRow[dst + 3] = E;
                    }
                    await fs.WriteAsync(rgbeRow, 0, rgbeRow.Length);
                }
                logCallback?.Invoke($"[jxr-hdr] Radiance HDR 已生成 ({width}x{height}, RGBE, uncompressed)\n");
                return hdrPath;
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[jxr-hdr] 转换失败: {ex.Message}\n");
                return null;
            }
        }

        /// <summary>ST.2084 (PQ) EOTF → linear light</summary>
        private static float PqToLinear(float pq)
        {
            // ST.2084 EOTF: L = ((max(V^(1/m2) - c1, 0)) / (c2 - c3 * V^(1/m2)))^(1/m1)
            const float m1 = 2610f / 16384f;   // 0.1593017578125
            const float m2 = 2523f / 32f;       // 78.84375
            const float c1 = 3424f / 4096f;     // 0.8359375
            const float c2 = 2413f / 128f;      // 18.8515625
            const float c3 = 2392f / 128f;      // 18.6875
            float v = MathF.Pow(pq, 1.0f / m2);
            float num = MathF.Max(v - c1, 0);
            float den = c2 - c3 * v;
            return MathF.Pow(num / den, 1.0f / m1);
        }

        /// <summary>Linear float RGB → RGBE (Radiance shared-exponent)</summary>
        private static void RgbToRgbe(float r, float g, float b,
            out byte R, out byte G, out byte B, out byte E)
        {
            float max = MathF.Max(r, MathF.Max(g, b));
            if (max < 1e-32f) { R = G = B = E = 0; return; }
            int e = (int)MathF.Ceiling(MathF.Log2(max));
            e = Math.Clamp(e + 128, 0, 255);
            float scale = MathF.Pow(2, e - 128 - 8);
            R = (byte)Math.Clamp((int)(r / scale + 0.5f), 0, 255);
            G = (byte)Math.Clamp((int)(g / scale + 0.5f), 0, 255);
            B = (byte)Math.Clamp((int)(b / scale + 0.5f), 0, 255);
            E = (byte)e;
        }

        /// <summary>
        /// DNG 编码输出：任意 RAW/DNG 输入 → DNG 文件。
        /// 使用 dngtool (LibRaw + Adobe DNG SDK 1.7.1)。
        /// 压缩: 无损 JPEG (默认) 或 JXL (DNG 1.7)。
        /// </summary>
        private async Task ProcessDngAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            if (!RawService.IsAvailable || !RawService.IsDngTool)
            {
                item.Log += "[dng] dngtool.exe 未检测到，无法编码 DNG\n";
                item.Log += "[dng] 请将 dngtool.exe 放入 PLAN/artifacts/ 目录（含 DNG 1.7 JXL 支持）\n";
                item.ExitCode = -1;
                item.Status = "失败 (dngtool 未找到)";
                return;
            }

            // 输入必须为 RAW/DNG；非 RAW 输入无法生成 DNG（无传感器数据）
            if (!RawService.IsRawFile(item.InputPath))
            {
                item.Log += "[dng] ⚠️ 仅 RAW/DNG 文件可编码为 DNG（普通图片无传感器数据）\n";
                item.ExitCode = -1;
                item.Status = "失败 (非 RAW 输入)";
                return;
            }

            // 压缩方式: 无损 JPEG 或 JXL（由独立 DNG 选项字段控制，兼容旧 JxlModular 语义）
            int compression = item.Options.DngCompression == 1 ? 1 : 0;
            int jxlQuality = item.Options.DngJxlQuality;
            // 兼容旧字段：若 DngCompression 未设置但 JxlModular=true（旧预设），按 JXL 处理
            if (compression == 0 && item.Options.JxlModular == true)
            {
                compression = 1;
                jxlQuality = item.Options.Lossless ? 0 : Math.Clamp(item.Options.Quality, 1, 100);
            }

            item.Log += $"[dng] 编码 DNG ({(compression == 1 ? $"JXL q={jxlQuality}" : "无损 JPEG")}" +
                        $"{(item.Options.DngLinear ? ", 线性 DNG (无 CFA)" : ", 保留 CFA (Bayer)")})...\n";
            _onItemUpdated?.Invoke(item);

            var success = await RawService.EncodeToDngAsync(
                item.InputPath, outputPath, compression, jxlQuality,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct,
                linear: item.Options.DngLinear,
                jxlEffort: item.Options.DngJxlEffort,
                jxlDecodeSpeed: item.Options.DngJxlDecodeSpeed,
                bitDepth: item.Options.DngBitDepth,
                highlightMode: item.Options.DngHighlightMode,
                threads: item.Options.Threads);   // 2026-08-15: UI 线程选项生效

            item.ExitCode = success ? 0 : -1;
            item.Status = success
                ? $"已完成 ({(compression == 1 ? "JXL 压缩 DNG" : "DNG")})"
                : "失败 (dngtool 编码失败)";

            if (success)
                await RestoreMetadataAsync(item, outputPath);
        }

        // #44-① 插点 C：原 `BuildJxrColorArgs`（JXR 第一步自算的那套色彩判定）**已退役** ——
        // 它是本项目反复消灭的「第三套自算色彩判定」形状：把目标 tokens 拼到 `-i` 前只改「源的解释」、
        // 像素一步不转。其职责现由单一裁决点承担（`DecideOutputColor` 的 `Decl*` + `PixelChains`），
        // 上面 `ProcessJxrAsync` 第一步即按它拼命令行；Ultra HDR 解码输入的 bt709/linear 声明
        // 同样由裁决点给出（`BuildColorArgsSplit` 第一条分支），值与退役前逐字相同。

        /// <summary>检测 JPEG 是否为 Ultra HDR (含增益图)：纯托管容器直读优先，exiftool(MPImage2) 兜底</summary>
        private static async Task<bool> IsUltraHdrJpegAsync(string path, Action<string>? log = null, CancellationToken ct = default)
        {
            // 纯托管：ISO 21496-1 APP2 为权威判据；无 ISO 时由 MPF + 副图降采样比例判定
            if (await GainMapDecoder.IsUltraHdrManagedAsync(path)) return true;
            if (!ExifToolService.IsAvailable) return false;
            try
            {
                // 兜底：旧 Ultra HDR 仅带 XMP + MPF，exiftool 读 MPF 第二幅 (MPImage2)
                var result = await ExifToolService.GetTagAsync(path, "MPImage2", log, ct);
                return !string.IsNullOrWhiteSpace(result);
            }
            catch { return false; }
        }

        /// <summary>
        /// 将 Ultra HDR JPEG 解码为线性 HDR 像素，并落地为自描述 PFM 浮点文件（失败返回 null）。
        /// PFM 保留 >1.0 的线性 HDR 值（1.0=SDR 白点 203nits），ffmpeg/cjxl 均原生可读，
        /// 避免旧 .rgba 无头裸流不可读、以及 PPM(rgb48le 整数) 管道削顶线性 HDR 的问题。
        /// 返回 (PFM 路径, 峰值亮度 nits, 线性像素原色语义的 cjxl color_space)。
        /// </summary>
        private async Task<(string path, float peakNits, string? jxlColorSpace, string tempDir)?> DecodeUltraHdrToLinearAsync(QueueItem item, string inputPath, CancellationToken ct)
        {
            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"uhdr_linear_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var rawPath = Path.Combine(tempDir, "hdr.rgba");
                var result = await GainMapDecoder.DecodeToLinearRawAsync(inputPath, rawPath,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                if (result == null || !File.Exists(rawPath))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                    return null;
                }
                var (w, h, peakNits, jxlColorSpace) = result.Value;
                // gbrpf32le 无头裸流 → 自描述 PFM 浮点（ffmpeg/cjxl 原生可读，保留线性 HDR >1.0，不削顶）
                var pfmPath = Path.Combine(tempDir, "hdr.pfm");
                var convArgs = $"-y -f rawvideo -pix_fmt gbrpf32le -s {w}x{h} -i \"{rawPath}\" -pix_fmt gbrpf32le \"{pfmPath}\"";
                item.Log += $"[cmd] ffmpeg {convArgs}\n";
                var convExit = await FfmpegRunner.RunAsync(convArgs,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, AppSettingsService.Current.FfmpegPath, ct);
                if (convExit != 0 || !File.Exists(pfmPath))
                {
                    item.Log += "[UltraHDR] ❌ 线性像素转 PFM 失败\n";
                    try { Directory.Delete(tempDir, true); } catch { }
                    return null;
                }
                PlatformServices.MarkAsTemporaryFile(pfmPath);
                // tempDir 交给调用方登记到**本项**的 tempDirsToCleanup：项一结束就删。
                // 旧写法塞进 _pendingRawDirs，而那个列表只在 Stop() 时处理 ⇒ 正常跑完一批全留在盘上（P1-6）。
                return (pfmPath, peakNits, jxlColorSpace, tempDir);
            }
            catch
            {
                try { Directory.Delete(tempDir, true); } catch { }
                return null;
            }
        }

        /// <summary>判断输出是否为 JPEG 格式</summary>
        private static bool IsJpegOutput(QueueItem item)
        {
            var fmt = (item.Options.Format ?? "").ToLowerInvariant();
            return fmt is "jpg" or "jpeg" or "jpegli";
        }

        /// <summary>
        /// P2-3：读子进程 stdout 时**必须同时排空 stderr**。两个管道都是固定缓冲（Windows 上约 4KB），
        /// 只读 stdout 时，子进程往 stderr 写满缓冲就会阻塞在写上，而我们又永远等不到 stdout ⇒ 整项卡死
        /// （表现为“探测不返回”，队列槽位被永久占住）。这里把两条流并发读干，返回 stdout。
        /// </summary>
        private static async Task<string> ReadStdoutDrainingStderrAsync(Process p)
        {
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();   // 读完即弃：这些探测只要 stdout
            var stdout = await outTask;
            try { await errTask; } catch { /* 排空失败不影响探测结果 */ }
            return stdout;
        }

        /// <summary>用 ffprobe 探测输入文件的位深</summary>
        /// <param name="log">日志回调（取消分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26`：调用方一直持有它却没往下传，本次接通）</param>
        private static async Task<int> ProbeBitDepthAsync(string inputPath, string ffmpegPath,
            Action<string>? log = null, CancellationToken ct = default)
        {
            try
            {
                var ffprobePath = PlatformServices.ResolveFfprobePath(ffmpegPath)
                    ?? Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
                if (!File.Exists(ffprobePath)) ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");

                var psi = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return 8;
                // `#26`：本方法原本**没有任何超时**；本次只接上调用方的取消令牌 ⇒ 取消即杀树，
                // 按既有兜底返回 8（**不新增超时**，不扩大本次范围）。
                using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
                var output = (await ReadStdoutDrainingStderrAsync(p)).Trim();
                await p.WaitForExitAsync();
                if (ct.IsCancellationRequested)
                {
                    var msg = $"[probe] 位深探测被取消 ⇒ 已终止：{Path.GetFileName(inputPath)}（按 8bit 处理）";
                    if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
                    return 8;
                }

                // 从像素格式推断位深
                return output switch
                {
                    string s when s.Contains("16") || s.Contains("48") || s.Contains("64") => 16,
                    string s when s.Contains("12") => 12,
                    string s when s.Contains("10") => 10,
                    string s when s.Contains("p010") || s.Contains("p012") => 10,
                    _ => 8
                };
            }
            catch { return 8; }
        }

        /// <summary>
        /// GainMap 专用管线的尺寸入口 —— **显示**尺寸（含 autorotate）。
        /// <para>⚠ 2026-09-30：本方法曾是仓内**又一份**自写 `-show_entries stream=width,height`
        /// （= coded 尺寸）。它的下游把 <c>width*height</c> 当 <c>pixelCount</c> 去切一段
        /// **由 autorotate 解码出来的** gbrpf32le raw（<c>Step 3</c>），而长度自检用的是
        /// <c>rawBytes.Length &lt; pixelCount*3*4</c> —— 转置下字节数恒等 ⇒ 拦不住 ⇒
        /// 与 <c>RawColorPipeline</c> 同一类"按 coded 标注旋正像素"的行错位。
        /// 现统一转发 <see cref="ImageGeometry"/>（仓内宽高探测的唯一实现）。</para>
        /// </summary>
        /// <param name="log">日志回调（探测失败/超时的点名；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26`：调用方一直持有它却没往下传，本次接通）</param>
        private static Task<(int width, int height)> ProbeImageSizeAsync(string inputPath, string ffmpegPath,
            Action<string>? log = null, CancellationToken ct = default)
        {
            var ffprobePath = PlatformServices.ResolveFfprobePath(ffmpegPath)
                ?? Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
            if (!File.Exists(ffprobePath)) ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
            return ImageGeometry.DisplaySizeAsync(inputPath, ffprobePath, log, ct);
        }

        /// <summary>用 ffmpeg 将输入转为临时高质量 PNG，返回路径（失败返回 null 并设置状态）</summary>
        private async Task<string?> PreConvertToPngAsync(QueueItem item, CancellationToken ct)
        {
            var tmp = Path.Combine(PlatformServices.GetTempDir(), $"ffmpeg_preconv_{Guid.NewGuid():N}.png");
            item.Log += "[preconv] 使用 ffmpeg 转为高质量 PNG 中间格式\n";
            // 传递 -map_metadata 0 保留源文件元数据到临时 PNG，确保后续外部工具编码时有元数据可用
            item.Command = $"ffmpeg -y -i \"{item.InputPath}\" -map_metadata 0 -compression_level 0 \"{tmp}\"";
            _onItemUpdated?.Invoke(item);

            var args = $"-y -i \"{item.InputPath}\" -map_metadata 0 -compression_level 0 \"{tmp}\"";
            item.Log += $"[cmd] ffmpeg {args}\n";
            var exit = await FfmpegRunner.RunAsync(args,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                AppSettingsService.Current.FfmpegPath, ct);

            if (exit != 0 || !File.Exists(tmp))
            {
                item.Log += $"[preconv] ffmpeg 转换失败 (退出码 {exit})\n";
                item.ExitCode = exit;
                item.Status = $"失败 (预转换退出码 {exit})";
                return null;
            }

            PlatformServices.MarkAsTemporaryFile(tmp);
            item.Log += "[preconv] 转换完成\n";
            return tmp;
        }

        /// <summary>
        /// ffmpeg 解码 → 管道 → cjxl 编码（无磁盘中间文件）。
        /// 命令等价于: ffmpeg -i input -compression_level 0 -f image2pipe -vcodec png - | cjxl - output.jxl -d X -e Y
        /// 元数据通过 exiftool 在编码后恢复。
        /// </summary>
        /// <param name="pixelDecision">
        /// #44-① 插点 A：插点 B 判「本次要转像素」时把**那一份**裁决产物传进来（同源，不重算第二次）。
        /// null ⇒ 按本次输入现算一次。两种结局：
        ///  • 链为空，或出口语义落不进 cjxl 的 <c>color_space</c> 枚举 ⇒ 命令行拼法与改动前**一致**
        ///    （仍由 <see cref="BuildPipeColorArgs"/> 给输入声明 / ICC 携带取舍 / 最长边缩放）；
        ///  • 链非空**且**出口可命名 ⇒ 输入声明取 <c>dec.Decl*</c>、像素取 <c>-vf dec.PixelChains</c>、
        ///    出口取 <c>-x color_space=</c>（裸 PPM/PAM 流不携带色彩元数据 ⇒ <c>-x</c> 生效，#44 实测），
        ///    并按 P7a-3 **同源登记**，链尾据此复算同一份裁决。
        /// </param>
        private async Task<(int exitCode, string status)> PipeFfmpegToCjxlAsync(QueueItem item, string outputPath,
            CancellationToken ct, FfmpegCommandBuilder.OutputColorDecision? pixelDecision = null,
            string? pixelIccPath = null)
        {
            var cjxlPath = CjxlService.DetectedPath;
            if (string.IsNullOrEmpty(cjxlPath))
            {
                item.Log += "[cjxl-pipe] cjxl 未找到，无法使用管道\n";
                return (-1, "失败 (cjxl 未找到)");
            }

            var ffmpegPath = AppSettingsService.Current.FfmpegPath;
            void PipeLog(string s) { item.Log += s; _onItemUpdated?.Invoke(item); }

            // 探测一次即可（`GetCachedProbe` 带缓存）：它同时供两处用 ——
            //  ① auto 格把它交给 cjxl 的色彩推导（与改动前同一份 hdrMeta）；
            //  ② ppm/pam 的选择要看 alpha，而**改动前真正执行的那份 argv**（`PipeFfmpegToExternalEncoderAsync`
            //     内部自己拼的）恒按探测值选，展示用的那一份却用 hdrMeta（显式目标下恒 default=false ⇒ 只能显示
            //     ppm）⇒ 两处各拼一遍、显示与执行可以不一致。本次收敛成一次拼装（见下方 ffArgs）。
            var inMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(item.InputPath, PipeLog, ct);
            var autoColorSpace = string.IsNullOrWhiteSpace(item.Options.ColorSpace)
                || item.Options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase);
            var hdrMeta = autoColorSpace ? inMeta : default(FfmpegCommandBuilder.ColorMetadata);

            var dec = pixelDecision ?? FfmpegCommandBuilder.DecideOutputColor(item.Options, item.InputPath);
            // 改道判据与插点 B 同一条：链非空（要转像素）且出口语义落得进 cjxl 的 color_space 枚举。
            var cjxlSpace = dec.PixelChains.Count > 0
                ? ColorEncodingHelper.MapPrimariesTransfer(dec.OutPrimaries, dec.OutTrc)
                : null;

            string ffArgs;
            string cjxlArgs;
            string? pipeIccPath = null;

            if (cjxlSpace != null || pixelIccPath != null)
            {
                // ── 输入侧：如实声明**源**用什么色彩语义（dec.Decl*），不是目标 ──
                // 旧写法（`BuildPipeColorArgs` 的显式目标分支）把**目标** tokens 拼到 `-i` 前 ⇒ 只把源的
                // 解释改掉、像素一步不转 ⇒ 目标被静默吃掉（#44 的缺陷形状）。矩阵声明走白名单
                // `ToFfmpegInputMatrixName`（与 `BuildArguments` 同一份，不可用则跳过、绝不原样拼）。
                var inArgs = "";
                if (!string.IsNullOrWhiteSpace(dec.DeclPrimaries))
                    inArgs += $"-color_primaries {dec.DeclPrimaries} ";
                if (!string.IsNullOrWhiteSpace(dec.DeclTrc))
                    inArgs += $"-color_trc {dec.DeclTrc} ";
                var declMatrix = ColorSpaceRegistry.ToFfmpegInputMatrixName(dec.DeclMatrix);
                if (declMatrix != null)
                    inArgs += $"-colorspace {declMatrix} ";

                // ── 像素侧：链内容 = 裁决点的 PixelChains（逗号串，与 BuildArguments 的 AppendVideoFilter 同形）──
                // `-pix_fmt` 在前、`-vf` 在后：照本管道路线既有写法（`BuildPipeColorArgs` 亦此序），不自己发明。
                var outArgs = inMeta.bitDepth > 8
                    ? $"-pix_fmt {(inMeta.hasAlpha ? "rgba64le" : "rgb48le")} "
                    : "";
                var chain = $"\"{string.Join(",", dec.PixelChains)}\"";
                // 最长边限制插在**链首色彩变换之前**（R1 顺序判据与 `BuildPipeColorArgs` 共用同一份实现）
                var scaleFilter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(item.Options, item.InputPath);
                if (!string.IsNullOrEmpty(scaleFilter))
                    chain = FfmpegCommandBuilder.InsertScaleFilterAtChainHead(chain, scaleFilter);
                outArgs += $"-vf {chain} ";

                var pipeVcodec = inMeta.hasAlpha ? "pam" : "ppm";
                ffArgs = $"-y {inArgs}-i \"{item.InputPath}\" {outArgs}-compression_level 0 "
                       + $"-f image2pipe -c:v {pipeVcodec} -";
                LogInjectedScale(item, ffArgs);   // 扩展 ③：缩了要让用户看得见

                // ── 出口侧：裸流不携带任何色彩元数据 ⇒ `-x color_space=` 生效；本路线**不传 icc_pathname** ──
                // ⚠ `--intensity_target` 与 color_space **成对**传（`BuildCjxlArguments` 的文档要求：只传一个
                //   会让"色彩空间来自裁决、峰值来自选项"混源）。取值顺序照本仓既有那三处，不新增判据：
                //   ① 只在**出口语义本身是 HDR**(PQ/HLG) 时才发（SDR 目标发它 = 假亮度声明；改动前 SDR 格
                //      走的 `MapToIntensityTarget` 两个重载对 SDR 同样返回 0）；
                //   ② 显式目标 ⇒ 用 `MapToIntensityTarget(options)`（与改动前显式目标格的 fallback 同源）；
                //   ③ auto ⇒ 用源峰值 `MapToIntensityTarget(hdrMeta)`（与改动前 ICC 分支里那句同值）；
                //   ④ 仍拿不到 ⇒ 1000，与引擎出口 `RawColorPipeline` 那句 HDR 兜底同数。
                var pipeNits = 0;
                if (dec.OutTrc is "smpte2084" or "arib-std-b67")
                {
                    pipeNits = ColorEncodingHelper.MapToIntensityTarget(item.Options);
                    if (pipeNits <= 0) pipeNits = ColorEncodingHelper.MapToIntensityTarget(hdrMeta);
                    if (pipeNits <= 0) pipeNits = 1000;
                }
                // 出口描述二选一：能命名 ⇒ `-x color_space=`；不能命名但生成了 ICC ⇒ `-x icc_pathname=`
                //   （**裸流上实测生效**，见插点 B 那段读数）。两者都拿不到时由调用方维持直编并点名。
                cjxlArgs = CjxlService.BuildCjxlArguments("-", outputPath, item.Options, hdrMeta, pixelIccPath,
                    cjxlSpace, pipeNits);
                // 同源登记（P7a-3）：**算链用的就是 item.Options**（未克隆、未改），故登记同一实例；
                // 探测输入 = 喂进管道的那份真源（此处可能与 item.Options 的 ICC 副本同为副本路径，见插点 B）。
                NoteColorDecisionSource(item, item.Options, item.InputPath);
            }
            else
            {
                (var pipeInputColor, var pipeOutputColor, pipeIccPath) =
                    BuildPipeColorArgs(item.Options, item.InputPath, PipeLog, ct);
                var pipeVcodec = inMeta.hasAlpha ? "pam" : "ppm";
                ffArgs = $"-y {pipeInputColor}-i \"{item.InputPath}\" {pipeOutputColor}-compression_level 0 "
                       + $"-f image2pipe -c:v {pipeVcodec} -";
                cjxlArgs = CjxlService.BuildCjxlArguments("-", outputPath, item.Options, hdrMeta, pipeIccPath);
            }

            item.Command = $"ffmpeg {ffArgs} | cjxl {cjxlArgs}";
            item.Log += $"[cmd] {item.Command}\n";   // 管道两侧都带色彩覆盖，不给看命令行就没法定矩阵/范围
            item.Log += $"[cjxl-pipe] ffmpeg 管道 → cjxl（无中间文件）\n";
            _onItemUpdated?.Invoke(item);

            var pipeResult = await PipeFfmpegToExternalEncoderAsync(
                item, ffmpegPath, cjxlPath, cjxlArgs, outputPath, "cjxl", ct, ffArgs);
            TryDeleteIcc(pipeIccPath);  // 清理临时 ICC 文件
            return pipeResult;
        }

        /// <summary>
        /// ffmpeg 解码 → 管道 → cjpegli 编码（无磁盘中间文件）。
        /// </summary>
        private async Task<(int exitCode, string status)> PipeFfmpegToCjpegliAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var cjpegliPath = CjpegliService.DetectedPath;
            if (string.IsNullOrEmpty(cjpegliPath))
            {
                item.Log += "[cjpegli-pipe] cjpegli 未找到，无法使用管道\n";
                return (-1, "失败 (cjpegli 未找到)");
            }

            var ffmpegPath = AppSettingsService.Current.FfmpegPath;

            // auto 模式：探测输入 HDR 属性
            var hdrMeta = default(FfmpegCommandBuilder.ColorMetadata);
            if (string.IsNullOrWhiteSpace(item.Options.ColorSpace)
                || item.Options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                hdrMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(item.InputPath,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            }

            var (pipeInputColor2, pipeOutputColor2, pipeIccPath2) = BuildPipeColorArgs(item.Options, item.InputPath,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            var cjpegliArgs = CjpegliService.BuildCjpegliArguments("-", outputPath, item.Options, hdrMeta, pipeIccPath2);
            var pipeVcodec2 = hdrMeta.hasAlpha ? "pam" : "ppm";

            item.Command = $"ffmpeg -y {pipeInputColor2}-i \"{item.InputPath}\" {pipeOutputColor2}-compression_level 0 -f image2pipe -c:v {pipeVcodec2} - | cjpegli {cjpegliArgs}";
            item.Log += $"[cmd] {item.Command}\n";
            item.Log += $"[cjpegli-pipe] ffmpeg 管道 → cjpegli（无中间文件）\n";
            _onItemUpdated?.Invoke(item);

            var pipeResult2 = await PipeFfmpegToExternalEncoderAsync(
                item, ffmpegPath, cjpegliPath, cjpegliArgs, outputPath, "cjpegli", ct);
            TryDeleteIcc(pipeIccPath2);  // 清理临时 ICC 文件
            return pipeResult2;
        }

        /// <summary>
        /// 通用管道方法：ffmpeg 解码 → stdout (PPM/PAM 流) → 外部编码器 stdin → 输出文件。
        /// 此方法消除了所有"解码→写临时文件→读取再编码"的磁盘中转。
        /// 注意：必须使用 PPM/PAM raw 流而非 PNG 流——cjxl 的 -x (color_space/icc_pathname)
        /// 仅对 PPM 等 raw 输入生效，PNG 容器流下会被静默忽略（实测验证）。
        /// </summary>
        /// <param name="prebuiltFfmpegArgs">
        /// 调用方**已经拼好并展示过**的那一份 ffmpeg argv（`#44-①` 插点 A 传入，令"给用户看的命令"与
        /// "真正执行的命令"出自同一次拼装）。为 null 时保持既有行为：本方法内部再拼一遍
        /// （cjxl/cjpegli 之外的旧调用点，以及 cjpegli 管道路线，逐字不变）。
        /// </param>
        private async Task<(int exitCode, string status)> PipeFfmpegToExternalEncoderAsync(
            QueueItem item,
            string ffmpegPath,
            string encoderPath,
            string encoderArgs,
            string outputPath,
            string encoderTag,
            CancellationToken ct,
            string? prebuiltFfmpegArgs = null)
        {
            Process? procFf = null;
            Process? procEnc = null;
            try
            {
                // 超时与 CjxlService 对齐为 30 分钟：effort 9/10 + 大图合法耗时远超 10 分钟
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var linkedToken = linked.Token;

                // ffmpeg: 解码输入为 PPM/PAM 流输出到 stdout
                // primaries/trc 在 -i 前作输入覆盖，colorspace/pix_fmt 在 -i 后
                // 有 alpha → PAM（P7，支持透明），无 alpha → PPM（P6）
                // ⚠ `#26`：本处 `linkedToken`（`CreateLinkedTokenSource(timeoutCts.Token, ct)` 的产物）
                //   原本就建好躺在同一作用域里却没往下交 —— 正是 `#25` 报告里「最干净的一处」。
                var ffArgs = prebuiltFfmpegArgs;
                if (ffArgs == null)
                {
                    var (pipeInColor, pipeOutColor, _) = BuildPipeColorArgs(item.Options, item.InputPath,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, linkedToken);
                    var inMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(item.InputPath,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, linkedToken);
                    var pipeVcodec = inMeta.hasAlpha ? "pam" : "ppm";
                    ffArgs = $"-y {pipeInColor}-i \"{item.InputPath}\" {pipeOutColor}-compression_level 0 -f image2pipe -c:v {pipeVcodec} -";
                }
                var psiFf = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = ffArgs,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // 外部编码器：从 stdin 读取 PNG 流
                var psiEnc = new ProcessStartInfo
                {
                    FileName = encoderPath,
                    Arguments = encoderArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                procFf = Process.Start(psiFf);
                if (procFf == null)
                {
                    item.Log += $"[{encoderTag}-pipe] 启动 ffmpeg 失败\n";
                    return (-1, $"失败 (ffmpeg 启动失败)");
                }
                PlatformServices.SetSafePriority(procFf, AppSettingsService.Current.FfmpegPriority);

                procEnc = Process.Start(psiEnc);
                if (procEnc == null)
                {
                    item.Log += $"[{encoderTag}-pipe] 启动 {encoderTag} 失败\n";
                    return (-1, $"失败 ({encoderTag} 启动失败)");
                }
                PlatformServices.SetSafePriority(procEnc, AppSettingsService.Current.FfmpegPriority);

                // 非阻塞消费 stderr 日志
                var ffLogTask = ConsumeStreamLinesAsync(procFf.StandardError, s =>
                {
                    item.Log += $"[ffmpeg] {s}\n";
                });
                var encLogTask = ConsumeStreamLinesAsync(procEnc.StandardError, s =>
                {
                    item.Log += $"[{encoderTag}] {s}\n";
                    _onItemUpdated?.Invoke(item);
                });
                var encOutTask = ConsumeStreamLinesAsync(procEnc.StandardOutput, s =>
                {
                    item.Log += $"[{encoderTag}] {s}\n";
                });

                // ── 关键：先完成管道传输 + 关闭 stdin，再等待进程退出 ──
                Exception? transferError = null;
                try
                {
                    await procFf.StandardOutput.BaseStream.CopyToAsync(
                        procEnc.StandardInput.BaseStream, linkedToken);
                }
                catch (OperationCanceledException)
                {
                    item.Log += $"[{encoderTag}-pipe] 传输被取消\n";
                }
                catch (Exception ex)
                {
                    transferError = ex;
                    item.Log += $"[{encoderTag}-pipe] 传输错误: {ex.Message}\n";
                }

                // 关闭编码器 stdin 发送 EOF
                try { procEnc.StandardInput.Close(); } catch { }

                // 等待编码器正常退出
                try { await procEnc.WaitForExitAsync(linkedToken); }
                catch (OperationCanceledException) { try { if (!procEnc.HasExited) procEnc.Kill(entireProcessTree: true); } catch { } }

                // 传输已失败 ⇒ 编码器不会再读 stdin，而 ffmpeg 还有整幅裸流要写 ⇒ 它会**永久阻塞在
                // 写端**（实测 891x981 的 5.2 MB PPM 流写满管道缓冲即卡死）。继续等的唯一结局是等满
                // 30 分钟超时，而真因早已打在上面的 stderr 逐行日志里 ⇒ 立刻杀掉，让结论马上落地。
                // 与 `RawColorPipeline.PipeToEncoderAsync` 同一条修法（2026-09-30）。
                if (transferError != null)
                {
                    try { if (!procFf.HasExited) procFf.Kill(entireProcessTree: true); } catch { }
                }
                // ffmpeg 应已自行退出（stdout 已读完）；但取消/编码器提前退出时 ffmpeg 可能
                // 阻塞在无读者的 stdout —— 必须用 linkedToken 打断并杀进程（P1-N4）
                try { await procFf.WaitForExitAsync(linkedToken); }
                catch (OperationCanceledException) { try { if (!procFf.HasExited) procFf.Kill(entireProcessTree: true); } catch { } }

                // 等待日志消费完成
                await ffLogTask;
                await encLogTask;
                await encOutTask;

                if (linkedToken.IsCancellationRequested)
                {
                    item.Log += $"[{encoderTag}-pipe] 超时或取消\n";
                    return (-1, "已停止/超时");
                }

                var encExitCode = procEnc.HasExited ? procEnc.ExitCode : -1;
                if (encExitCode == 0 && transferError == null)
                {
                    item.Log += $"[{encoderTag}-pipe] 管道编码完成\n";
                    return (0, $"已完成 ({encoderTag} 管道)");
                }
                else
                {
                    item.Log += $"[{encoderTag}-pipe] 编码失败: {encoderTag} 退出码 {encExitCode}\n";
                    return (encExitCode != 0 ? encExitCode : -1,
                            $"失败 ({encoderTag} 退出码 {encExitCode})");
                }
            }
            catch (OperationCanceledException)
            {
                return (-1, "已停止");
            }
            catch (Exception ex)
            {
                item.Log += $"[{encoderTag}-pipe] 异常: {ex.Message}\n";
                return (-1, $"失败 (管道异常: {ex.Message})");
            }
            finally
            {
                if (procEnc != null && !procEnc.HasExited)
                {
                    try { procEnc.Kill(entireProcessTree: true); } catch { }
                }
                if (procFf != null && !procFf.HasExited)
                {
                    try { procFf.Kill(entireProcessTree: true); } catch { }
                }
                try { procEnc?.Dispose(); } catch { }
                try { procFf?.Dispose(); } catch { }
            }
        }

        /// <summary>AVIF → GIF/WebP：分轨提取颜色+alpha，再合并编码保留透明通道</summary>
        private async Task ProcessAvifToGifWebpAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var fmt = item.Options.Format.ToLowerInvariant();
            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"avif2gifwebp_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Step 0: 检测是否为动画 AVIF。静态 AVIF (单视频流/无 alpha 轨道)
                // 不需要两步法，直接回退普通 ffmpeg 单命令（避免 -map 0:2 找不到轨道而失败）。
                var (avifProbeAnimated, avifProbeDeterminate) = await IsAnimatedAvifAsync(item.InputPath,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                // `#136`：探测**不确定** ⇒ 保守按**多帧**（走两步法），绝不静默当静态（那会丢帧）。
                bool isAnimatedAvif = avifProbeAnimated || !avifProbeDeterminate;
                if (!isAnimatedAvif)
                {
                    item.Log += $"[avif→{fmt}] 输入为静态 AVIF（单视频流），走普通 ffmpeg 单命令路径\n";
                    _onItemUpdated?.Invoke(item);
                    var ffArgs = BuildArgsAndNote(item, item.Options, item.InputPath, outputPath);
                    item.Command = "ffmpeg " + ffArgs; item.Log += $"[cmd] ffmpeg {ffArgs}\n";
                    var fallbackExit = await FfmpegRunner.RunAsync(ffArgs, s =>
                    {
                        item.Log += s; _onItemUpdated?.Invoke(item);
                    }, AppSettingsService.Current.FfmpegPath, ct);
                    item.ExitCode = fallbackExit;
                    item.Status = fallbackExit == 0 ? $"已完成 (avif→{fmt})" : $"失败 (退出码 {fallbackExit})";
                    return;
                }

                // Step 1: 分轨提取颜色(0:2) + alpha(0:3) → 分别解码避免 alphamerge 压缩色彩
                item.Command = $"[avif→{fmt}] 两步法: 分轨提取颜色+alpha → alphamerge 合并编码";
                item.Log += $"[avif→{fmt}] Step1: 分轨提取颜色+alpha...\n";
                _onItemUpdated?.Invoke(item);

                // ⚠ P7e：流号**不得硬编码**。旧写法写死 `-map 0:2`（颜色）/`-map 0:3`（alpha），
                //   但不同来源的 AVIF 流布局并不一致：实测本工具链的 avifenc 产物只有两条流
                //   （index0=1 帧、index1=10 帧，两条都是彩色 av1/yuv420p，SATAVG 相同 ⇒ 根本无 alpha 平面），
                //   写 0:2 直接 exit=-22。现在按“**帧数最多的流 = 颜色；另一条帧数与颜色相同的 = alpha**”选：
                //   本例选 0:1 且无 alpha；而对旧 4 流布局（primary/thumb 1 帧 + color 10 帧 + alpha 10 帧）
                //   依旧选出 0:2 / 0:3 ⇒ **向后兼容，零行为变化**。探测失败则回退到旧的 0:2/0:3。
                var (colorIdx, alphaIdx) = await ProbeAvifStreamRolesAsync(item.InputPath,
                    AppSettingsService.Current.FfmpegPath, s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                item.Log += $"[avif→{fmt}]   选流：颜色=0:{colorIdx}，alpha={(alphaIdx is int ai ? $"0:{ai}" : "无")}（按帧数判，非硬编码）\n";

                // ⚠ 序号必须**连续**：这里曾写 `-frame_pts 1`，让 image2 用 PTS 当文件序号。一旦某条流的
                //   PTS 间隔 >1，写出的就是带空洞的序号，而 Step2 按 `%04d` 顺序读会在第一个空洞处**静默截断**
                //   （退出码仍为 0、状态"已完成"）⇒ 帧数丢失无人知晓。现在按帧序连续编号，读取侧同一基号。
                var colorArgs = $"-y -i \"{item.InputPath}\" -map 0:{colorIdx} -fps_mode passthrough -start_number 0 \"{tempDir}\\c_%04d.png\"";
                var alphaArgs = alphaIdx is int ai2
                    ? $"-y -i \"{item.InputPath}\" -map 0:{ai2} -fps_mode passthrough -start_number 0 \"{tempDir}\\a_%04d.png\""
                    : null;

                item.Log += $"[cmd] ffmpeg {colorArgs}\n";
                var colorExit = await FfmpegRunner.RunAsync(colorArgs, s => { item.Log += s; }, AppSettingsService.Current.FfmpegPath, ct);
                if (colorExit != 0) { item.ExitCode = colorExit; item.Status = $"失败 (颜色流 {colorExit})"; return; }

                var hasAlpha = Directory.GetFiles(tempDir, "a_*.png").Length > 0;
                if (!hasAlpha && alphaArgs != null)
                {
                    item.Log += $"[cmd] ffmpeg {alphaArgs}\n";
                    await FfmpegRunner.RunAsync(alphaArgs, s => { }, AppSettingsService.Current.FfmpegPath, ct);
                }
                hasAlpha = Directory.GetFiles(tempDir, "a_*.png").Length > 0;

                var colorFiles = Directory.GetFiles(tempDir, "c_*.png");
                item.Log += $"[avif→{fmt}]   颜色 {colorFiles.Length} 帧, alpha {(hasAlpha ? "有" : "无")}\n";

                var fps = item.Options.AnimationFps ?? 33;
                string encodeArgs;

                if (fmt == "gif")
                {
                    // A-13（2026-09-26）：关抖动要显式 dither=none（paletteuse 默认 sierra2_4a，省略≠关），
                    // 与 ImageEncoderArgs.BuildGifFilterChain 同口径 —— 这是调色板链的第二份拷贝，
                    // 两处必须一起改，否则 AVIF/GIF 两步转换路径与常规路径又分叉。
                    var dither = item.Options.GifDither ? "=dither=bayer:bayer_scale=5:diff_mode=rectangle" : "=dither=none";
                    if (hasAlpha)
                    {
                        // 单 pass: color(0:v) + alpha(1:v) → alphamerge → palettegen → paletteuse
                        encodeArgs = $"-y -framerate {fps} -start_number 0 -i \"{tempDir}\\c_%04d.png\" -framerate {fps} -start_number 0 -i \"{tempDir}\\a_%04d.png\" -filter_complex \"[0:v][1:v]alphamerge,split[a][b];[a]palettegen=reserve_transparent=1:stats_mode=full:max_colors=256[p];[b][p]paletteuse{dither}\" -loop 0 \"{outputPath}\"";
                    }
                    else
                    {
                        encodeArgs = $"-y -framerate {fps} -start_number 0 -i \"{tempDir}\\c_%04d.png\" -vf \"split[a][b];[a]palettegen=stats_mode=full:max_colors=256[p];[b][p]paletteuse{dither}\" -loop 0 \"{outputPath}\"";
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
                        encodeArgs = $"-y -framerate {fps} -start_number 0 -i \"{tempDir}\\c_%04d.png\" -framerate {fps} -start_number 0 -i \"{tempDir}\\a_%04d.png\" -filter_complex \"[0:v][1:v]alphamerge,format=yuva420p\" -c:v libwebp_anim -q:v {quality} -loop {loop} \"{outputPath}\"";
                    }
                    else
                    {
                        encodeArgs = $"-y -framerate {fps} -start_number 0 -i \"{tempDir}\\c_%04d.png\" -c:v libwebp_anim -q:v {quality} -loop {loop} \"{outputPath}\"";
                    }
                }

                item.Log += $"[avif→{fmt}] Step2: 编码...\n";
                _onItemUpdated?.Invoke(item);

                item.Log += $"[cmd] ffmpeg {encodeArgs}\n";
                var encodeExit = await FfmpegRunner.RunAsync(encodeArgs, s =>
                {
                    item.Log += s; _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath, ct);

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
            // ⚠ P7f：不得在本方法里“再算一套”查找路径，否则会与分派条件分叉（曾经就是这样）。
            var avifencPath = PlatformServices.ResolveAvifencPath();
            if (avifencPath == null)
            {
                // 可用性诚实：本分支现在只在“分派与执行同源后仍判为不可用”时到达（理论上不应发生）。
                // 绝不静默改用 ffmpeg 单命令——那会让用户以为在用 avifenc，且产物静默丢 alpha 与动图 AVIF 特性。
                item.Command = "（未执行：avifenc 不可用）";
                item.Log += "[gif→avif] 未找到 avifenc.exe ⇒ 显式失败（不静默回退）。\n"
                    + "  请任选其一：① 把 avifenc.exe 放入发布包 PLAN/artifacts/；"
                    + "② 在设置里指定 AvifencPath 或 WindowsArtifactsDir；③ 改选其他输出格式。\n";
                item.ExitCode = -1;
                item.Status = "失败 (avifenc 不可用，未静默回退)";
                _onItemUpdated?.Invoke(item);
                return;
            }

            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"avifenc_frames_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            item.Command = "[gif→avif] 两步法: ffmpeg 提取RGBA帧 → avifenc 编码动图AVIF";

            try
            {
                // Step 1: 提取 GIF 帧为带 alpha 的 PNG
                item.Log += "[gif→avif] Step1: 提取 GIF 帧为 RGBA PNG...\n";
                _onItemUpdated?.Invoke(item);

                var extractArgs = $"-y -i \"{item.InputPath}\" -pix_fmt rgba -fps_mode passthrough \"{tempDir}\\f_%04d.png\"";
                item.Log += $"[cmd] ffmpeg {extractArgs}\n";
                var extractExit = await FfmpegRunner.RunAsync(extractArgs, s =>
                {
                    item.Log += s;
                    _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath, ct);

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
                // ⚠ P7h（实测）：avifenc 1.4.2 在 `-j 1` 且**帧数 ≥4** 时段错误（0xC0000005 / 退出码 -1073741819），
                //   与 alpha 平面、--repetition-count 均无关；-j 2/4/8 或不传 -j 全部正常。
                //   旧写法 Math.Max(1, Options.Threads) 在 Threads=0(auto，headless 即此值) 下恒为 1 ⇒ 动画 GIF→AVIF 必崩。
                var threads = item.Options.Threads;
                if (threads == 1)
                {
                    item.Log += "[gif→avif]   ⚠ 单线程会触发 avifenc 1.4.2 的段错误（≥4 帧），已提为 -j 2（上游缺陷，非本仓库）\n";
                    _onItemUpdated?.Invoke(item);
                    threads = 2;
                }

                var avifencArgs = new StringBuilder();
                var headArgs = $"-q {quality} -s {speed}" + (threads >= 2 ? $" -j {threads}" : "");   // Threads<=0 ⇒ 不传 -j，由 avifenc 自适应
                avifencArgs.Append(headArgs).Append(' ');
                // ⚠ P2-4（实测）：帧文件**只能传相对文件名**，配合下面的 WorkingDirectory。
                //   绝对路径每帧 ~98 字符 ⇒ 约 330 帧就撞满 CreateProcess 的 32767 上限
                //   （420 帧 GIF 实测 ErrorStartingProcess「文件名或扩展名太长」）；相对文件名每帧 11 字符 ⇒ 约 2900 帧。
                var frameNames = pngFiles.Select(f => Path.GetFileName(f)).ToList();
                avifencArgs.Append(string.Join(' ', frameNames));
                avifencArgs.Append(' ');
                var outputAbsPath = Path.GetFullPath(outputPath);
                var tailArgs = $"-o \"{outputAbsPath}\" --fps {fps} --repetition-count infinite";
                avifencArgs.Append(tailArgs);

                // 回显给用户的命令行做摘要：整条可能有几十万字节，灌进 item.Log 只会把日志冲爆。
                item.Log += $"[gif→avif]   avifenc {headArgs} {frameNames[0]} … {frameNames[^1]}（共 {frameNames.Count} 帧） {tailArgs}\n";

                // 预检：超限就让 Win32 抛「文件名或扩展名太长」用户是没法办的 ⇒ 这里显式失败并给出处置建议（不静默回退）。
                const int MaxWindowsCmdLine = 32767;   // CreateProcess 的命令行总长上限（含 "exe" + 空格 + 参数）
                var cmdLineChars = avifencPath.Length + avifencArgs.Length + 3;
                if (cmdLineChars > MaxWindowsCmdLine)
                {
                    item.Log += $"[gif→avif]   ✗ {frameNames.Count} 帧使 avifenc 命令行达到 {cmdLineChars} 字符，"
                              + $"超过 Windows 上限 {MaxWindowsCmdLine} ⇒ 显式失败。\n"
                              + "  可行做法：① 减少帧数（裁短 GIF 或降低目标 fps）；② 改输出 GIF/WebP 等其他动画格式。\n";
                    item.ExitCode = -1;
                    item.Status = "失败 (帧数超出 avifenc 命令行上限)";
                    _onItemUpdated?.Invoke(item);
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = avifencPath,
                    Arguments = avifencArgs.ToString(),
                    WorkingDirectory = tempDir,          // 帧文件按相对文件名传入，必须由这里解析（P2-4）
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
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
                    PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    item.Status = "已停止";
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

            // JXL→JXL 且输入为 JPEG 重构型：不能走 djxl 重构分支——
            // djxl 按输出扩展名决定格式，不支持写出 .jxl（会失败或产出 JPEG 内容的错标文件）。
            // 改按原生码流处理：解码像素 → cjxl/ffmpeg 重编码（无损选项下为像素级无损）。
            var effectiveType = jxlType;
            if (jxlType == JxlImageType.JpegReconstruction
                && targetFmt == "jxl")
            {
                item.Log += "[jxl] 目标仍为 JXL：跳过 djxl 重构（djxl 无法输出 .jxl），改走解码→重编码管线\n";
                _onItemUpdated?.Invoke(item);
                effectiveType = JxlImageType.NativeCodestream;
            }

            if (effectiveType == JxlImageType.JpegReconstruction)
            {
                if (DjxlService.IsAvailable)
                {
                    item.Log += "[jxl] 使用 djxl 从 JXL 还原原始 JPEG（无损，不解码像素）\n";
                    item.Command = $"djxl \"{item.InputPath}\" \"{outputPath}\"";
                    // ⚠⚠ 2026-10-05（P0 修复）：**捕获 djxl 的"静默降级"**。
                    //   根因（实测）：当 JXL 的 jbrd 重构数据被破坏（典型成因见下方
                    //   `RestoreMetadataAsync` 处的说明）时，djxl **不会失败**，而是打一行
                    //     Warning: could not decode losslessly to JPEG. Retrying with --pixels_to_jpeg...
                    //   然后**按像素重编码**并**以退出码 0 结束** ⇒ 上层只看退出码 ⇒ 状态写成
                    //   「已完成 (djxl 重构 JPEG)」= **日志与事实相反**（本仓最忌"宣称≠交付"）。
                    //   后果实测：往返 JPEG 不再逐字节相同、色度由 4:2:0 变 4:4:4
                    //   （`verify-jbrd-e2e` 的 A photoicc 三条断言因此转红）。
                    //   ⇒ 判据取 djxl 自己那行 `pixels_to_jpeg`（唯一可靠信号），命中即**点名**，
                    //     且**不得**再声称"重构"。
                    bool djxlDegradedToPixels = false;
                    var exit = await DjxlService.RunAsync(item.InputPath, outputPath, item.Options.Threads,
                        s =>
                        {
                            if (s.Contains("pixels_to_jpeg", StringComparison.OrdinalIgnoreCase)
                                || s.Contains("could not decode losslessly", StringComparison.OrdinalIgnoreCase))
                                djxlDegradedToPixels = true;
                            item.Log += s; _onItemUpdated?.Invoke(item);
                        }, ct);
                    item.ExitCode = exit;
                    if (exit == 0 && djxlDegradedToPixels)
                    {
                        item.Log += "[jxl] ⚠️ djxl 未能无损重构（其内部已降级为 --pixels_to_jpeg 像素重编码）⇒ 本产物不是原始 JPEG 的逐字节还原，色度采样等可能已改变。\n";
                    }
                    // ⚠ 用 if/else 而非三元续行：`Status` 是**用户可见文案**（队列列直接显示），
                    //   而续行 `? "中文"` 不命中 sink 判定 ⇒ 会被计进 CJK 门禁的 `CsUi` 判据桶。
                    //   写成独立赋值语句后，每条都是自足的 `item.Status = "…";`（与既有 30+ 条同族同形）。
                    if (exit != 0)
                        item.Status = $"失败 (djxl 退出码 {exit})";
                    else if (djxlDegradedToPixels)
                        item.Status = "已完成 (djxl 降级为像素重编码：非无损重构)";
                    else
                        item.Status = "已完成 (djxl 重构 JPEG)";
                    if (exit != 0) { /* 失败：不进入后置 */ }
                    else if (djxlDegradedToPixels)
                    {
                        // ⚠ 降级后产物是**重新编码**的普通 JPEG，不再是"原始码流" ⇒ 走正常元数据阶段。
                        await RestoreMetadataAsync(item, outputPath);
                    }
                    else
                    {
                        // ⚠⚠ 2026-10-05（P0 修复）：**无损重构通路必须跳过元数据恢复**。
                        //   根因（实测，因果已逐条验证）：djxl 已把**原始 JPEG 字节**原样写出，
                        //   而 `RestoreMetadataAsync` 会调 exiftool 往产物里写标签；对 .jpg 产物它是
                        //   "安全复制元数据"，会**重写 APP1/APP2 并改变它们的顺序**，
                        //   而 djxl 的重构判定要求容器内字节与源一致 ⇒ **无损承诺被这一步自己毁掉**。
                        //   铁证（三段对照，同一夹具 `src_photo_local.jpg` 23917 B）：
                        //     · 纯 `cjxl -d 0 --lossless_jpeg=1` → 纯 `djxl` ⇒ **Reconstructed to JPEG**、SHA 相同 ✅
                        //     · 同一 JXL 先跑一次 `exiftool --GPS:all`（即默认的 GPS 隐私清理）
                        //       → 再 `djxl` ⇒ **could not decode losslessly**、降级像素重编码 ❌
                        //     · 产品默认路径产物 ⇒ djxl 降级 ⇒ 往返 SHA 不同、yuvj420j → yuvj444p
                        //   ⇒ 结论：对"刚由 djxl 无损重构出来的 JPEG"**不得再做任何元数据写入**。
                        //     这不是"少做一步"，而是**保住该通路唯一的卖点**（逐字节可逆）；
                        //     元数据已随原始码流一起回来，无需恢复。
                        item.Log += "[jxl] 无损重构通路：跳过元数据恢复（产物即原始 JPEG 字节，任何写入都会破坏逐字节可逆性）\n";
                    }
                }
                else
                {
                    item.Log += "[djxl] 未检测到 djxl，回退到 ffmpeg\n";
                    // #151（2026-09-19）：djxl 缺失时「你显式指定的编码器后端被忽略」必须**点名**，
                    //   否则用户会以为 --encoder 生效了。裁定：**响亮 + 点名，不是硬失败**
                    //   （产物是对的 ⇒ ProceedWithDegradation）。
                    //   ⚠ 文案刻意用 **ASCII**：`_probe-cjk-hardcode-scan` 的 `CsUi` 余量为 0，
                    //     新增中文代码串会立刻转红（优先纯 ASCII 是本仓既定偏好）。
                    if (item.Options.EncoderBackend != EncoderBackend.Ffmpeg)
                        item.Log += "[djxl] NOTE: --encoder " + item.Options.EncoderBackend + " was requested but is IGNORED on this fallback path; ffmpeg is used instead\n";
                    var args = BuildArgsAndNote(item, item.Options, item.InputPath, outputPath);
                    item.Command = "ffmpeg " + args; item.Log += $"[cmd] ffmpeg {args}\n";
                    // #15 心跳站点 A（jxl-recon-no-djxl）：JXL 链兜底站点 —— ffmpeg 直接吃 .jxl，
                    // 与 B/C/D 同风险（畸形字节形状会让 JXL 解复用器忙等：0 输出 + 烧 CPU）。
                    var exit = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath, ct, heartbeat: true);
                    item.ExitCode = exit;
                    item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                }
            }
            else if (effectiveType == JxlImageType.NativeCodestream)
            {
                // 动图 JXL 不解码为 PNG 中间格式（会丢失动画帧），直接用 ffmpeg 处理
                if (IsAnimated(item))
                {
                    item.Log += "[jxl] 动图 JXL 不进行 djxl→PNG 中间解码，回退到 ffmpeg\n";
                    _onItemUpdated?.Invoke(item);
                    var ffmpegOpts = CloneOptionsForFfmpeg(item.Options);
                    var args2 = BuildArgsAndNote(item, ffmpegOpts, item.InputPath, outputPath);   // 同源登记 + 建命令（P7a-3）
                    item.Command = "ffmpeg " + args2; item.Log += $"[cmd] ffmpeg {args2}\n";
                    // #15 心跳站点 B（jxl-anim）：动图 JXL 直接交 ffmpeg ⇒ 与 A/C/D 同链同风险。
                    var exit2 = await FfmpegRunner.RunAsync(args2,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath, ct, heartbeat: true);
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
                        // cjpegli 不支持 stdin 管道，需要临时文件
                        (CjpegliService.IsAvailable && (targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli"))
                        // cjxl 不支持 stdin 管道，需要临时文件（仅用于 JXL 目标）
                        || (CjxlService.IsAvailable && targetFmt == "jxl");
                        // 注意：WebP 输出仍优先走 PPM 管道 (djxl → ffmpeg libwebp_anim/libwebp)，
                        // 只有当需要外部编码器且该编码器不支持 stdin 时才回退 PNG 中转。

                    if (usePngIntermediary)
                    {
                        // ── JXL 目标：优先使用 djxl→cjxl 管道（cjxl 支持 stdin）──
                        if (CjxlService.IsAvailable && targetFmt == "jxl")
                        {
                            item.Log += "[pipeline] 尝试 djxl -> cjxl 管道（无中间文件）\n";
                            var pipeResult = await PipeDjxlToCjxlAsync(item, outputPath, ct);
                            if (pipeResult.exitCode == 0)
                            {
                                item.ExitCode = 0;
                                item.Status = pipeResult.status;
                                await RestoreMetadataAsync(item, outputPath);
                                return;
                            }
                            item.Log += "[pipeline] djxl→cjxl 管道失败，回退到 PNG 中转\n";
                        }

                        // ── JPEG/JPEGLI 目标：优先使用 djxl→cjpegli 管道 ──
                        if (CjpegliService.IsAvailable && (targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli"))
                        {
                            item.Log += "[pipeline] 尝试 djxl -> cjpegli 管道\n";
                            var pipeExit = await JxlPipelineService.TryPipeDjxlToCjpegliAsync(
                                item.InputPath, outputPath, item.Options,
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

                                                // ── 回退/直接：PNM 中转（djxl v0.12+ 不支持 PNG 输出）──
                        // 注意: djxl v0.12 不再识别 .png 扩展名，需用 .pnm (PPM/PAM 自适应)
                        var tmp = Path.Combine(PlatformServices.GetTempDir(), Guid.NewGuid().ToString() + ".pnm");
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
                        // item.Command 由 PipeDjxlToFfmpegAsync 内部按实际 PPM/PAM 流设置
                        await PipeDjxlToFfmpegAsync(item, outputPath, ct);
                    }
                }
                else
                {
                    item.Log += "[djxl] 未检测到 djxl，使用 ffmpeg 直接处理 JXL\n";
                    // #151（2026-09-19）：同站点 A —— 显式指定的编码器后端在此降级路径上被忽略，必须点名。
                    //   ⚠ 文案用 ASCII（`cjk-hardcode` 的 `CsUi` 余量为 0，见站点 A 的注释）。
                    if (item.Options.EncoderBackend != EncoderBackend.Ffmpeg)
                        item.Log += "[djxl] NOTE: --encoder " + item.Options.EncoderBackend + " was requested but is IGNORED on this fallback path; ffmpeg is used instead\n";
                    var args = BuildArgsAndNote(item, item.Options, item.InputPath, outputPath);
                    item.Command = "ffmpeg " + args; item.Log += $"[cmd] ffmpeg {args}\n";
                    // #15 心跳站点 C（jxl-native-no-djxl）：JXL 链兜底站点之一（ffmpeg 直接吃 .jxl），
                    // 而 ffmpeg 的 JXL 解复用器在畸形字节形状上会**忙等**（无输出 + 烧 CPU）
                    // ⇒ 旧守卫的 `cpuAdvanced` 会让它永不被判死。见 FfmpegRunner 的实测依据。
                    var exit = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath, ct, heartbeat: true);
                    item.ExitCode = exit;
                    item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                }
            }
            else
            {
                var args = BuildArgsAndNote(item, item.Options, item.InputPath, outputPath);
                item.Log += $"[cmd] ffmpeg {args}\n";
                // #15 心跳站点 D（jxl-unknown）：**畸形 .jxl 实测就落在这个分支**（不以 FF 0A 开头 ⇒ JxlInspector 判
                // Unknown ⇒ 两个类型分支都不匹配 ⇒ 通用兜底）。ffmpeg 的 JXL 解复用器在该字节
                // 形状上忙等（0 输出 + 烧 CPU）⇒ 旧守卫的 `cpuAdvanced` 会让它永不被判死。
                var exit = await FfmpegRunner.RunAsync(args,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                    AppSettingsService.Current.FfmpegPath, ct, heartbeat: true);
                item.ExitCode = exit;
                item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
            }
        }

        /// <summary>
        /// JXR 输入智能处理：JxrDecApp 解码 JXR → BMP → 根据目标格式选择编码器。
        /// JxrDecApp 不支持 stdout 管道，必须经过磁盘 BMP 中间文件。
        /// </summary>
        private async Task ProcessJxrInputAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            // 查找 JxrDecApp（解码器），与 JxrEncApp（编码器）在同一 artifacts 目录
            var jxrDecPath = ResolveJxrDecAppPath(s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            if (string.IsNullOrEmpty(jxrDecPath))
            {
                item.Log += "[jxr] JxrDecApp.exe 未检测到，无法解码 JXR 文件\n";
                item.Log += "[jxr] FFmpeg 不支持 JXR 格式，请将 JxrDecApp.exe 放入 PLAN/artifacts/ 目录\n";
                item.ExitCode = -1;
                item.Status = "失败 (JXR 解码器不可用)";
                return;
            }

            var targetFmt = (item.Options.Format ?? "").ToLowerInvariant();
            item.Log += $"[jxr] 输入: JXR  |  目标格式: {targetFmt}  |  JxrDecApp: 可用  |  cjpegli: {(CjpegliService.IsAvailable ? "可用" : "不可用")}  |  cjxl: {(CjxlService.IsAvailable ? "可用" : "不可用")}\n";

            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"jxr_input_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Step 1: JxrDecApp 解码 JXR → BMP/TIF
                // 注意: JxrDecApp 的 BMP 写入器仅支持 ≤32bpp 常规格式 (24bpp/32bppBGRA/8bit灰度等)。
                // NVIDIA 等工具导出的 HDR JXR (128bppRGBAFloat scRGB) 无法输出 BMP → 自动回退
                // "TIF + -c 0" (24bppRGB)，补丁版 JxrDecApp 内置 scRGB→sRGB 转换 (Convert_Float_To_U8)。
                var intermediatePath = Path.Combine(tempDir, "decoded.bmp");
                item.Log += "[jxr] Step 1: JxrDecApp 解码 JXR → BMP...\n";
                _onItemUpdated?.Invoke(item);

                var decArgs = $"-i \"{item.InputPath}\" -o \"{intermediatePath}\"";
                item.Command = $"JxrDecApp {decArgs}";
                var decExit = await RunJxrDecAppAsync(jxrDecPath, decArgs,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);

                if (decExit != 0 || !File.Exists(intermediatePath))
                {
                    // BMP 输出失败（典型：高位深 / float 像素格式，如 NVIDIA HDR 截图 128bppRGBAFloat）
                    // -c 0 输出 8-bit sRGB TIF（BMP 扩展名会把目标映射为 BGR 导致转换表缺失，故用 TIF）
                    item.Log += $"[jxr]   BMP 输出不支持该像素格式 (退出码 {decExit})，回退 TIF + -c 0 (8-bit sRGB)...\n";
                    _onItemUpdated?.Invoke(item);
                    intermediatePath = Path.Combine(tempDir, "decoded.tif");
                    decArgs = $"-i \"{item.InputPath}\" -o \"{intermediatePath}\" -c 0";
                    item.Command = $"JxrDecApp {decArgs}";
                    decExit = await RunJxrDecAppAsync(jxrDecPath, decArgs,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);

                    if (decExit != 0 || !File.Exists(intermediatePath))
                    {
                        item.ExitCode = decExit;
                        item.Status = $"失败 (JxrDecApp 退出码 {decExit})";
                        return;
                    }
                    item.Log += "[jxr]   TIF 解码成功 (scRGB→sRGB 转换完成)\n";
                }
                var intermediateSize = new FileInfo(intermediatePath).Length;
                item.Log += $"[jxr]   解码完成: {intermediateSize / 1024}KB {Path.GetExtension(intermediatePath).TrimStart('.').ToUpperInvariant()}\n";
                PlatformServices.MarkAsTemporaryFile(intermediatePath); // 提示 OS 优先内存缓存

                // Step 2: 根据目标格式选择编码路径
                var useCjxl = CjxlService.IsAvailable && targetFmt == "jxl";
                var useCjpegli = CjpegliService.IsAvailable && (targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli");
                var useJxrEnc = targetFmt == "jxr";

                if (useCjxl || useCjpegli)
                {
                    // ── cjxl / cjpegli: PPM 管道直通（跳过 PNG 磁盘文件，省 10% 时间 + 21MB 磁盘）──
                    item.Log += $"[jxr] Step 2: ffmpeg {Path.GetExtension(intermediatePath).TrimStart('.').ToUpperInvariant()} → PPM pipe → {(useCjxl ? "cjxl" : "cjpegli")}（无磁盘中间文件）...\n";
                    _onItemUpdated?.Invoke(item);

                    // 临时替换 InputPath 指向中间文件（管道方法使用 item.InputPath 作为 ffmpeg 输入）
                    var savedInputPath = item.InputPath;
                    item.InputPath = intermediatePath;
                    try
                    {
                        (int exitCode, string status) pipeResult;
                        if (useCjxl)
                        {
                            pipeResult = await PipeFfmpegToCjxlAsync(item, outputPath, ct);
                            // item.Command 已由 PipeFfmpegToCjxlAsync 设置为实际命令（PPM/PAM 管道）
                        }
                        else
                        {
                            pipeResult = await PipeFfmpegToCjpegliAsync(item, outputPath, ct);
                            // item.Command 已由 PipeFfmpegToCjpegliAsync 设置为实际命令（PPM/PAM 管道）
                        }

                        if (pipeResult.exitCode == 0)
                        {
                            item.ExitCode = 0;
                            item.Status = pipeResult.status;
                        }
                        else
                        {
                            // ── 管道失败 → 回退 PNG 磁盘中转 ──
                            item.Log += $"[jxr] 管道失败（退出码 {pipeResult.exitCode}），回退 BMP→PNG 磁盘中转...\n";
                            _onItemUpdated?.Invoke(item);
                            var pngPath = Path.Combine(tempDir, "decoded.png");
                            var pngArgs = $"-y -i \"{intermediatePath}\" -compression_level 0 \"{pngPath}\"";
                            item.Log += $"[cmd] ffmpeg {pngArgs}\n";
                            var pngExit = await FfmpegRunner.RunAsync(pngArgs,
                                s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                                AppSettingsService.Current.FfmpegPath, ct);

                            if (pngExit != 0 || !File.Exists(pngPath))
                            {
                                item.ExitCode = pngExit;
                                item.Status = $"失败 (BMP→PNG 回退退出码 {pngExit})";
                                return;
                            }

                            if (useCjxl)
                            {
                                item.Command = "cjxl " + CjxlService.BuildCjxlArguments(pngPath, outputPath, item.Options);
                                var cjxlExit = await CjxlService.RunWithOptionsAsync(pngPath, outputPath, item.Options,
                                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct: ct);
                                item.ExitCode = cjxlExit;
                                item.Status = cjxlExit == 0 ? "已完成 (JXR→PNG→cjxl 回退)" : $"失败 (cjxl 退出码 {cjxlExit})";
                            }
                            else
                            {
                                item.Command = "cjpegli " + CjpegliService.BuildCjpegliArguments(pngPath, outputPath, item.Options);
                                var cjexit = await CjpegliService.RunWithOptionsAsync(pngPath, outputPath, item.Options,
                                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                                item.ExitCode = cjexit;
                                item.Status = cjexit == 0 ? "已完成 (JXR→PNG→cjpegli 回退)" : $"失败 (cjpegli 退出码 {cjexit})";
                            }
                        }
                    }
                    finally
                    {
                        item.InputPath = savedInputPath;
                    }
                }
                else if (useJxrEnc)
                {
                    // ── JXR → JXR：重新编码（可调整质量/无损）──
                    item.Log += "[jxr] Step 2: JxrEncApp 重新编码 → JXR...\n";
                    _onItemUpdated?.Invoke(item);
                    var quality = item.Options.Lossless ? 1.0 : item.Options.Quality / 100.0;
                    var encArgs = JxrService.BuildArguments(intermediatePath, outputPath, quality);
                    item.Command = "JxrEncApp " + encArgs;
                    var encExit = await JxrService.RunAsync(encArgs,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                    item.ExitCode = encExit;
                    item.Status = encExit == 0 ? "已完成 (JXR→重新编码→JXR)" : $"失败 (JxrEncApp 退出码 {encExit})";
                }
                else
                {
                    // ── FFmpeg 标准编码：中间文件 → 目标格式 ──
                    item.Log += $"[jxr] Step 2: ffmpeg {Path.GetExtension(intermediatePath).TrimStart('.').ToUpperInvariant()} → {targetFmt.ToUpper()}...\n";
                    _onItemUpdated?.Invoke(item);

                    var ffmpegOpts = CloneOptionsForFfmpeg(item.Options);
                    // 确保 FFmpeg 使用正确的编码器
                    ffmpegOpts.EncoderBackend = EncoderBackend.Ffmpeg;
                    var args = BuildArgsAndNote(item, ffmpegOpts, intermediatePath, outputPath);
                    item.Command = "ffmpeg " + args; item.Log += $"[cmd] ffmpeg {args}\n";
                    var ffExit = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath, ct);
                    item.ExitCode = ffExit;
                    item.Status = ffExit == 0 ? $"已完成 (JXR→{Path.GetExtension(intermediatePath).TrimStart('.').ToUpperInvariant()}→ffmpeg {targetFmt})" : $"失败 (ffmpeg 退出码 {ffExit})";
                }

                // ── 元数据恢复 ──
                if (item.ExitCode == 0)
                    await RestoreMetadataAsync(item, outputPath);
            }
            catch (OperationCanceledException)
            {
                item.Status = "已停止";
            }
            catch (Exception ex)
            {
                item.Log += $"[jxr] 异常: {ex.Message}\n";
                item.ExitCode = -1;
                item.Status = $"失败: {ex.Message}";
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// djxl 解码 JXL → stdout(PNG流) → cjxl stdin → 输出 JXL（无磁盘中间文件）。
        /// 管道失败时返回非零，调用方回退到 PNG 中转方案。
        /// </summary>
        private async Task<(int exitCode, string status)> PipeDjxlToCjxlAsync(
            QueueItem item, string outputPath, CancellationToken ct)
        {
            var djxlPath = DjxlService.DetectedPath;
            var cjxlPath = CjxlService.DetectedPath;
            if (string.IsNullOrEmpty(djxlPath) || string.IsNullOrEmpty(cjxlPath))
                return (-1, "失败 (djxl 或 cjxl 未找到)");

            // 探测 JXL 输入色彩（ffprobe 读 libjxl demuxer 的枚举值）与 alpha：
            // PPM/PAM 流不携带色彩标签，必须由 cjxl -x color_space 显式标记，
            // 否则广色域 JXL 重编码会静默降级为 sRGB。
            var jxlMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(item.InputPath,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            var pipeFmt = jxlMeta.hasAlpha ? "pam" : "ppm";

            // 无损语义明示：本路径是「解码像素 → 重编码」，勾选无损（-d 0）仅保证像素级无损，
            // 与源 JXL 码流并非位等价（XYB→像素→重新熵编码）；元数据由 exiftool 后置恢复。
            if (item.Options.Lossless)
            {
                item.Log += "[djxl→cjxl] ⚠️ 无损模式为解码后像素级无损（-d 0），非源码流位等价；如需位等价还原请从原始 JPEG/RAW 重新转码\n";
                _onItemUpdated?.Invoke(item);
            }

            // 超广色域保真（P2 收尾）：ProPhoto/Adobe RGB 等源 ffprobe 无法命名（近似为 bt709），
            // 仅靠 -x color_space 会误标 sRGB 导致欠饱和 → 改携带源 ICC 精确描述。
            // 与 ffmpeg 管道路径同源（ResolveFaithfulUltraWideIcc，白名单已含 jxl 目标）；
            // PreserveAll 模式再补充容器内嵌 ICC 提取（与 ProcessCjxlAsync 直编路径一致）。
            string? pipeIccPath = null;
            if (string.IsNullOrWhiteSpace(item.Options.DecodedUltraHdrColorSpace))
            {
                pipeIccPath = FfmpegCommandBuilder.ResolveFaithfulUltraWideIcc(
                    item.Options, jxlMeta.sourceGamutName, jxlMeta.colorTrc, item.Options.Format);
                if (pipeIccPath == null
                    && item.Options.MetadataMode == Models.MetadataMode.PreserveAll)
                {
                    var (iccPipe, _) = IccProfileService.ExtractIccToTempFile(item.InputPath);
                    pipeIccPath = iccPipe;
                    if (pipeIccPath != null)
                        item.Log += "[djxl→cjxl] 已提取源 ICC，将嵌入输出 JXL\n";
                }
            }

            Process? procDj = null;
            Process? procCj = null;
            try
            {
                // 超时与 CjxlService 对齐为 30 分钟：effort 9/10 + 大图合法耗时远超 10 分钟
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var linkedToken = linked.Token;

                // cjxl: 从 stdin 读取，编码为 JXL（色彩由探测元数据显式标记；超广色域/PreserveAll 时携带源 ICC）
                var cjxlArgs = CjxlService.BuildCjxlArguments("-", outputPath, item.Options, jxlMeta, pipeIccPath);
                var psiCj = new ProcessStartInfo
                {
                    FileName = cjxlPath,
                    Arguments = cjxlArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // djxl: 解码 JXL → PPM/PAM 流输出到 stdout（raw 流下 cjxl -x 才生效）
                var djArgs = $"\"{item.InputPath}\" --output_format={pipeFmt} -";
                var psiDj = new ProcessStartInfo
                {
                    FileName = djxlPath,
                    Arguments = djArgs,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                procCj = Process.Start(psiCj);
                if (procCj == null) return (-1, "失败 (cjxl 启动失败)");
                PlatformServices.SetSafePriority(procCj, AppSettingsService.Current.FfmpegPriority);
                procDj = Process.Start(psiDj);
                if (procDj == null) return (-1, "失败 (djxl 启动失败)");
                PlatformServices.SetSafePriority(procDj, AppSettingsService.Current.FfmpegPriority);

                // 非阻塞消费日志
                var djLogTask = ConsumeStreamLinesAsync(procDj.StandardError,
                    s => item.Log += $"[djxl] {s}\n");
                var cjLogTask = ConsumeStreamLinesAsync(procCj.StandardError,
                    s => { item.Log += $"[cjxl] {s}\n"; _onItemUpdated?.Invoke(item); });
                var cjOutTask = ConsumeStreamLinesAsync(procCj.StandardOutput,
                    s => item.Log += $"[cjxl] {s}\n");

                // 管道传输：djxl stdout → cjxl stdin
                Exception? transferError = null;
                try
                {
                    await procDj.StandardOutput.BaseStream.CopyToAsync(
                        procCj.StandardInput.BaseStream, linkedToken);
                }
                catch (OperationCanceledException) { item.Log += "[djxl→cjxl] 传输取消\n"; }
                catch (Exception ex) { transferError = ex; item.Log += $"[djxl→cjxl] 传输错误: {ex.Message}\n"; }

                try { procCj.StandardInput.Close(); } catch { }
                try { await procCj.WaitForExitAsync(linkedToken); }
                catch (OperationCanceledException) { try { if (!procCj.HasExited) procCj.Kill(entireProcessTree: true); } catch { } }
                // 传输已经失败 ⇒ cjxl 不会再读任何东西，而 djxl 还有整幅 PNM 要写 ⇒ 它会**永久阻塞在
                // 写端**（实测同形状：891x981 的 5.2 MB 流写满管道缓冲即卡死）。继续等的唯一结局是等满
                // 30 分钟超时 ⇒ 立刻杀掉写侧。与 `RawColorPipeline.PipeToEncoderAsync` 同一条修法。
                if (transferError != null)
                {
                    try { if (!procDj.HasExited) procDj.Kill(entireProcessTree: true); } catch { }
                }
                // djxl 可能阻塞在无读者的 stdout（cjxl 提前退出时）——取消/超时必须打断并杀进程，
                // 裸 CancellationToken.None 等待会让 finally 的 Kill 永不可达（P1-N4）
                try { await procDj.WaitForExitAsync(linkedToken); }
                catch (OperationCanceledException) { try { if (!procDj.HasExited) procDj.Kill(entireProcessTree: true); } catch { } }

                await djLogTask; await cjLogTask; await cjOutTask;

                var exitCode = procCj.HasExited ? procCj.ExitCode : -1;
                // ⚠ 判据必须含 `transferError == null`：只认 cjxl 退出码，会在一截**半截流**上判成功
                //（cjxl 收到被截断的 PNM 也可能以 0 退出）。与 `PipeFfmpegToExternalEncoderAsync`
                //  和 `JxlPipelineService` 同一条成功契约。
                if (exitCode == 0 && transferError == null)
                {
                    item.Log += "[djxl→cjxl] 管道编码完成\n";
                    return (0, "已完成 (djxl→cjxl 管道)");
                }
                if (exitCode == 0)
                    return (-1, $"失败 (cjxl 退出码 0，但管道传输失败：{transferError?.Message})");
                return (exitCode, $"失败 (cjxl 退出码 {exitCode})");
            }
            catch (OperationCanceledException) { return (-1, "已停止"); }
            catch (Exception ex)
            {
                item.Log += $"[djxl→cjxl] 异常: {ex.Message}\n";
                return (-1, $"失败: {ex.Message}");
            }
            finally
            {
                if (procCj != null && !procCj.HasExited) try { procCj.Kill(true); } catch { }
                if (procDj != null && !procDj.HasExited) try { procDj.Kill(true); } catch { }
                try { procCj?.Dispose(); } catch { }
                try { procDj?.Dispose(); } catch { }
                TryDeleteIcc(pipeIccPath);  // 本任务提取的临时源 ICC，用后即删（走受保护版本）
            }
        }

        private async Task<bool> FallbackDjxlDecodeToFile(QueueItem item, string tmp, string outputPath, CancellationToken ct)
        {
            // 动图不解码为 PNM 中间格式
            if (IsAnimated(item))
            {
                item.Log += "[djxl] 动图不进行 PNM 中间解码，回退失败\n";
                item.ExitCode = -1;
                item.Status = "失败 (动图不支持 PNM 中转)";
                return false;
            }

            item.Log += "[djxl] 解码 JXL 为中间 PNM (PPM/PAM)\n";
            var exit = await DjxlService.RunAsync(item.InputPath, tmp, item.Options.Threads,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            if (exit == 0 && File.Exists(tmp))
            {
                PlatformServices.MarkAsTemporaryFile(tmp);
                var targetFmt = (item.Options.Format ?? "").ToLowerInvariant();

                // 根据输出格式选择外部编码器
                if (targetFmt == "jxl" && CjxlService.IsAvailable)
                {
                    item.Log += "[cjxl] 对中间 PNG 进行编码\n";
                    var cjxlExit = await CjxlService.RunWithOptionsAsync(tmp, outputPath, item.Options,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct: ct);
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
                    var args = BuildArgsAndNote(item, item.Options, tmp, outputPath);
                    item.Command = "ffmpeg " + args; item.Log += $"[cmd] ffmpeg {args}\n";
                    var exit2 = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath, ct);
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
                // ⚠ U1（2026-10-01）：djxl 的 PPM/PAM 写出器**吃不下 float 图像** —— 实测对
                //   `512x384, lossy, 32-bit float (8 exponent bits) RGB` 的 .jxl 直接报
                //   `JxlDecoderSetImageOutBitDepth failed` → `DecompressJxlToPackedPixelFile failed`（exit 1）。
                //   这类文件的典型入口正是"带增益图的 JPEG → JXL"（cjxl 把 HDR 底编成 float）。
                //   同一文件用 `djxl → .png` 与 `ffmpeg 直读 .jxl` 都能出图（实测 345242 B / 342726 B），
                //   ⇒ 解码失败不该是终点，终点是换一条走得通的解码路，并且**把换路说出来**（不许静默）。
                item.Log += $"[djxl] PNM 中转失败 (exit {exit}) ⇒ 改走 ffmpeg 直读 .jxl（djxl 的 PPM 输出不支持 float 图像）\n";
                var argsDirect = BuildArgsAndNote(item, item.Options, item.InputPath, outputPath);
                item.Command = "ffmpeg " + argsDirect;
                item.Log += $"[cmd] ffmpeg {argsDirect}\n";
                // 心跳守卫不用在这里再点一次：#15-b（2026-09-19）起它是 `RunAsync` 的**默认行为**（opt-out），
                //   本文件里那 4 处显式写法只是 JXL 链的文档锚点（由 `verify-ffmpeg-heartbeat.ps1` ① 钉着）。
                //   这条直读路同样吃这个默认 —— 该解复用器在畸形字节形状上会忙等（无输出 + 烧 CPU）。
                var exitDirect = await FfmpegRunner.RunAsync(argsDirect,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                    AppSettingsService.Current.FfmpegPath, ct);
                item.ExitCode = exitDirect;
                item.Status = exitDirect == 0
                    ? "已完成 (ffmpeg 直读 JXL)"
                    : $"失败 (djxl PNM exit {exit} 且 ffmpeg 直读 exit {exitDirect})";
                if (exitDirect == 0) await RestoreMetadataAsync(item, outputPath);
                return exitDirect == 0;
            }
        }

        /// <summary>
        /// 将 djxl 解码输出通过管道直接传给 ffmpeg 编码，避免中间 PNG 临时文件。
        /// 适用于 JXL → PNG/WebP/AVIF/TIFF/GIF 等所有非 JPEG/JXL 目标格式。
        /// 关键修复：
        /// 1. 使用 PPM/PAM 管道而非 PNG，保留 HDR 色彩标签
        /// 2. 使用 ppm_pipe/pam_pipe demuxer 避免 image2pipe 无法识别 PAM
        /// 3. HDR 源不使用 iccgen（icgen 不支持 HDR），直接 tonemap + iccgen 或仅 tonemap
        /// 4. 注入真实色彩元数据到高级参数，确保 tonemap 分支正确触发
        /// </summary>
        private async Task PipeDjxlToFfmpegAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var djxl = DjxlService.DetectedPath;
            if (string.IsNullOrEmpty(djxl))
            {
                item.Log += "[pipe] djxl 未找到，回退到 ffmpeg 直接处理\n";
                var args = BuildArgsAndNote(item, item.Options, item.InputPath, outputPath);
                item.Log += $"[cmd] ffmpeg {args}\n";
                var exit = await FfmpegRunner.RunAsync(args,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                    AppSettingsService.Current.FfmpegPath, ct);
                item.ExitCode = exit;
                item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                return;
            }

            item.Log += "[pipe] 尝试 djxl → ffmpeg 管道\n";
            _onItemUpdated?.Invoke(item);

            // 探测 JXL 输入色彩与 alpha：使用 PPM/PAM raw 流（避免 PNG 丢失广色域 ICC）
            var jxlMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(item.InputPath,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            var pipeFmt = jxlMeta.hasAlpha ? "pam" : "ppm";

            // 关键修复：将探测到的 JXL 真实色彩元数据注入 FfmpegOptions 高级参数，
            // 使 BuildArguments 的 BuildColorArgsSplit 走高级模式路径（直接使用声明的
            // primaries/trc），而不是探测 stdin "-"（探测失败 → 错误假设 sRGB）。
            if (jxlMeta.colorPrimaries != null && jxlMeta.colorTrc != null)
            {
                var isPq = jxlMeta.colorTrc.Equals("smpte2084", StringComparison.OrdinalIgnoreCase);
                var isHlg = jxlMeta.colorTrc.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
                if (isPq || isHlg)
                {
                    // PQ 与 HLG 必须分别标记：HLG 源误标 PQ 会让显示端套错 EOTF（P1-N7）
                    item.Options.DecodedUltraHdrColorSpace = isPq ? "Rec2100PQ" : "Rec2100HLG";
                    item.Log += $"[pipe] 注入 JXL 真实色彩 (HDR): p={jxlMeta.colorPrimaries} t={jxlMeta.colorTrc} m={jxlMeta.colorSpace}\n";
                }
                else
                {
                    // SDR JXL：声明**输入**色彩 —— 走 `ColorSourceDecl*`（与 RAW 同一条路），
                    // ⚠⚠ **绝不**写 `ColorPrimaries/ColorTrc`：那对字段在 `DecideOutputColor` 里是
                    //   **输出目标**，写进去会把用户显式指定的目标（`--color-space` / `--color-primaries`）
                    //   静默覆盖成「这个 JXL 输入自带的色」—— 旧注释「目标仍由 ColorSpace 决定」**与代码相反**。
                    //   实测（2026-10-04 审查 线B P1-1）：`-i x.jxl -f png --color-space "Display P3"`
                    //   产物 CICP 仍是 `bt709`（P3 被吞）；对照普通 png 源同参数 ⇒ `smpte432` ✓。
                    item.Options.ColorSourceDeclPrimaries = jxlMeta.colorPrimaries;
                    item.Options.ColorSourceDeclTrc = jxlMeta.colorTrc;
                    item.Options.ColorMatrix = jxlMeta.colorSpace ?? "bt709";   // 输入矩阵声明（`-i` 前 -colorspace）
                    item.Log += $"[pipe] 注入 JXL 真实色彩: p={jxlMeta.colorPrimaries} t={jxlMeta.colorTrc} m={jxlMeta.colorSpace}\n";
                }
            }

            // 关键修复：按流类型选择专用管道 demuxer。
            // -f image2pipe 无法识别 PAM（带 alpha）流，报 "Could not find codec parameters (Video: none, none)"。
            var pipeDemuxer = pipeFmt switch
            {
                "pam" => "pam_pipe",
                "ppm" => "ppm_pipe",
                _     => "image2pipe"
            };

            // 关键修复：管道场景仅需 -color_range pc；输入色彩已通过 DecodedUltraHdrColorSpace/ColorPrimaries/ColorTrc 传递给 ffmpeg，
            // 无需在 colorPrefix 中重复 -color_primaries/-color_trc（BuildArguments 已处理）。
            var colorPrefix = "-color_range pc ";

            // 构建 ffmpeg 参数：去除 iccgen（HDR 管道不支持），确保正确的 tonemap 路径
            var ffmpegArgs = BuildFfmpegPipeArguments(item.Options, outputPath, colorPrefix, pipeDemuxer, item.Options.Format, item.Options.Format.ToLower() == "webp",
                realInputForProbe: item.InputPath, itemForRegistration: item);   // P7d：stdin 探不到位深/alpha，必须拿真实源文件做探测替代
            item.Command = $"djxl \"{item.InputPath}\" --output_format={pipeFmt} - | ffmpeg {ffmpegArgs}";
            // 与其他分支一致地回显命令行：专用通路过去只设 item.Command 不写日志，一出问题就“无法定性”
            //（本仓库铁律：宣称/日志不一致必须附命令行原文）。管道场景必须看到完整 ffmpeg 参数才能查矩阵/范围漂移。
            item.Log += $"[cmd] djxl \"{Path.GetFileName(item.InputPath)}\" --output_format={pipeFmt} - | ffmpeg {ffmpegArgs}\n";
            _onItemUpdated?.Invoke(item);

            Process? procDj = null;
            Process? procFf = null;
            try
            {
                // 超时与 CjxlService 对齐为 30 分钟：effort 9/10 + 大图合法耗时远超 10 分钟
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var linkedToken = linked.Token;

                var ffmpegPath = AppSettingsService.Current.FfmpegPath;

                // djxl 解码 JXL → PPM/PAM 流输出到 stdout（raw 流 + ffmpeg 输入覆盖保证色彩正确）
                var djArgs = $"\"{item.InputPath}\" --output_format={pipeFmt} -";
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

                // ffmpeg 从 stdin 读取 PNG 流
                var psiFf = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = ffmpegArgs,
                    RedirectStandardInput = true,
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
                    item.Log += "[pipe] 启动 djxl 失败\n";
                    item.ExitCode = -1;
                    item.Status = "失败 (djxl 启动失败)";
                    return;
                }
                PlatformServices.SetSafePriority(procDj, AppSettingsService.Current.FfmpegPriority);

                procFf = Process.Start(psiFf);
                if (procFf == null)
                {
                    item.Log += "[pipe] 启动 ffmpeg 失败\n";
                    item.ExitCode = -1;
                    item.Status = "失败 (ffmpeg 启动失败)";
                    return;
                }
                PlatformServices.SetSafePriority(procFf, AppSettingsService.Current.FfmpegPriority);

                // 启动 stderr/stdout 消费者（非阻塞，并行运行）
                var djErrTask = ConsumeStreamLinesAsync(procDj.StandardError, s => item.Log += $"[djxl] {s}\n");
                var ffLogTask = ConsumeStreamLinesAsync(procFf.StandardError, s =>
                {
                    item.Log += s + "\n";
                    _onItemUpdated?.Invoke(item);
                });
                var ffOutTask = ConsumeStreamLinesAsync(procFf.StandardOutput, s => item.Log += s + "\n");

                // ── 关键：先完成传输 + 关闭 stdin，再等待进程退出（避免死锁）──
                Exception? transferError = null;
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
                    transferError = ex;
                    item.Log += $"[pipe] 传输错误: {ex.Message}\n";
                }

                // 关闭 ffmpeg stdin 以发送 EOF，让 ffmpeg 完成编码
                try { procFf.StandardInput.Close(); } catch { }

                // 等待 ffmpeg 正常退出（stdin 已关闭，ffmpeg 会自行结束）
                try { await procFf.WaitForExitAsync(linkedToken); }
                catch (OperationCanceledException) { try { if (!procFf.HasExited) procFf.Kill(entireProcessTree: true); } catch { } }
                // 传输已经失败 ⇒ ffmpeg 不会再读 stdin，djxl 会**永久阻塞在无人读的 stdout 上**
                // ⇒ 立刻杀写侧，别等满 30 分钟（与上面两条管道同一条修法）
                if (transferError != null)
                {
                    try { if (!procDj.HasExited) procDj.Kill(entireProcessTree: true); } catch { }
                }
                // djxl：取消/ffmpeg 提前退出时可能阻塞在无读者的 stdout —— 用 linkedToken 打断并杀进程（P1-N4）
                try { await procDj.WaitForExitAsync(linkedToken); }
                catch (OperationCanceledException) { try { if (!procDj.HasExited) procDj.Kill(entireProcessTree: true); } catch { } }

                // 等待日志消费者完成
                await djErrTask;
                await ffLogTask;
                await ffOutTask;

                var ffExitCode = procFf.HasExited ? procFf.ExitCode : -1;
                if (ffExitCode == 0 && transferError != null)
                {
                    // ⚠ 必须保持**单行**：折行会让续行脱离 `item.Log +=` 这个 sink 标记，
                    //   被 `_probe-cjk-hardcode-scan` 误计入「UI 面文案」桶（本文件 :383 登记过同因漂移）。
                    item.Log += $"[pipe] ffmpeg 退出码 0，但管道传输失败 ⇒ 判失败、不恢复元数据（半截流产出的文件不能当成功）：{transferError.Message}\n";
                    ffExitCode = -1;
                }
                // 成功判据与另外三条管道**同一条契约**（`transferError == null` 出现在正向条件里，
                // 由 `verify-color-wiring.ps1 (10h)` 逐站点比对）：只认进程退出码，会在一截半截流上判成功。
                var pipeOk = (ffExitCode == 0 && transferError == null);
                item.ExitCode = ffExitCode;
                item.Status = pipeOk ? "已完成 (djxl→ffmpeg 管道)" : $"失败 (ffmpeg 退出码 {ffExitCode})";
                if (pipeOk) await RestoreMetadataAsync(item, outputPath);
            }
            catch (OperationCanceledException)
            {
                item.Status = "已停止";
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

        /// <summary>
        /// 构建管道中 ffmpeg 的色彩参数。
        /// 返回 (inputArgs, outputArgs)：inputArgs 放 -i 前作输入覆盖，outputArgs 放 -i 后。
        /// </summary>
        /// <summary>
        /// 构建 ffmpeg 管道色彩参数 + 位深保留 + ICC 提取。
        /// 返回 (输入覆盖参数, 输出参数, 提取的 ICC 临时文件路径)。
        /// iccPath 非 null 时调用方必须负责删除该临时文件。
        /// </summary>
        /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26`：3 个调用方各自持有 `ct`/`linkedToken`，本方法此前 static 无形参）</param>
        private static (string inputArgs, string outputArgs, string? iccPath) BuildPipeColorArgs(
            Models.FfmpegOptions options, string? inputPath = null,
            Action<string>? log = null, CancellationToken ct = default)
        {
            string inputArgs = "", outputArgs = "";
            string? iccPath = null;

            // ── 位深保留：16-bit+ 输入显式指定 PNG 输出位深（防御性，确保不被降级）──
            // 实测 ffmpeg PNG 编码器对 16-bit 输入默认输出 rgb48be（自动保留），
            // 此处显式指定消除不确定性；8-bit 输入不指定（保持默认 rgb24）。
            var depthMeta = default(FfmpegCommandBuilder.ColorMetadata);
            if (!string.IsNullOrEmpty(inputPath))
                depthMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath, log, ct);
            if (depthMeta.bitDepth > 8)
                outputArgs += $"-pix_fmt {(depthMeta.hasAlpha ? "rgba64le" : "rgb48le")} ";

            // ── 点三：超广色域「像素原样 + 仅靠 ICC」保真（管道 ffmpeg → cjpegli/cjxl）──
            // 管道输入无容器元数据可搭，ICC 只能由编码器 -x icc_pathname 嵌入；
            // 像素完全不动，也不写任何 CICP 输入标签（与附着的 ProPhoto ICC 会冲突）。
            if (string.IsNullOrWhiteSpace(options.FaithfulIccPath) && !string.IsNullOrEmpty(inputPath))
                options.FaithfulIccPath = FfmpegCommandBuilder.ResolveFaithfulUltraWideIcc(
                    options, depthMeta.sourceGamutName, depthMeta.colorTrc, options.Format);
            if (!string.IsNullOrWhiteSpace(options.FaithfulIccPath))
                return ("", outputArgs.Trim(), options.FaithfulIccPath);

            // ⚠ 2026-10-04：`HasExplicitColorParams` 本身 = CP/CT/Matrix 任一非空 ⇒ 原先的合取是冗余的；
            //   同时不再读 UI 布尔（勾选/不勾选不得有策略差别，见 `FfmpegOptions.HasExplicitColorParams`）。
            if (options.HasExplicitColorParams)
            {
                if (!string.IsNullOrWhiteSpace(options.ColorPrimaries))
                    inputArgs += $"-color_primaries {options.ColorPrimaries} ";
                if (!string.IsNullOrWhiteSpace(options.ColorTrc))
                    inputArgs += $"-color_trc {options.ColorTrc} ";
                if (!string.IsNullOrWhiteSpace(options.ColorMatrix))
                    outputArgs += ColorSpaceRegistry.FfmpegColorSpaceArg(options.ColorMatrix);
            }
            else if (!string.IsNullOrWhiteSpace(options.ColorSpace)
                     && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                // 简化模式显式选定色彩空间：委托 ColorSpaceRegistry 完整覆盖
                // sRGB/BT.709/Display P3/P3 PQ/BT.2020 PQ/HLG（旧代码只认 3 个旧名，广色域落回 bt709——P4）。
                var (pP, pT, pM) = ColorSpaceRegistry.FfmpegTokens(options.ColorSpace);
                if (pP != null)
                {
                    // 超广色域目标（Adobe RGB/ProPhoto/ColorMatch）：cjxl 无 color_space 枚举（Registry 已置 null），
                    // 归入标准 BT.2020 —— 源→BT.2020 像素转换 + 携带标准 BT.2020 ICC（icc_pathname 优先于 color_space）。
                    // 修复 #2：旧行为经 CjxlColorSpace=Rec2100PQ 把 SDR 误标 HDR PQ → 画面变暗。镜像 auto 分支既有已验证处理。
                    if (ColorSpaceRegistry.IsUltraWide(options.ColorSpace) && !string.IsNullOrEmpty(inputPath))
                    {
                        var srcMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath, log, ct);
                        bool uwHlg = srcMeta.colorTrc == "arib-std-b67";
                        bool uwHdr = srcMeta.colorTrc == "smpte2084" || uwHlg;
                        var (stdP, stdT, stdM) = ColorSpaceRegistry.UltraWideNormalizeTokens(options.ColorSpace, uwHdr, uwHlg);
                        // 源→BT.2020：统一用 BuildToBt2020Filter，输出传递烘焙到 stdT（与下方 ICC/CICP 声明严格一致）。
                        // 修复：旧代码对非超广源用 BuildGamutTransformFilter(src,"BT.2020")，盲取裸 "BT.2020" Spec 的
                        // smpte2084 把 SDR 源 bake 成 PQ，却配 bt709 的 stdIcc → 像素与标签失配（变暗）。stdT 已由
                        // UltraWideNormalizeTokens 按目标 Spec + 源 HDR 正确算出（DCI-P3/超广 SDR→bt709，P3-PQ/HDR→PQ）。
                        var srcGamut = string.IsNullOrWhiteSpace(srcMeta.sourceGamutName) ? "sRGB" : srcMeta.sourceGamutName;
                        var xform = ColorSpaceRegistry.BuildToBt2020Filter(srcGamut, stdT);
                        if (xform != null)
                        {
                            outputArgs += $"-vf \"{xform}\" ";
                            // SDR 归一化目标携带标准 BT.2020 ICC；HDR(stdIcc==null) 由 cjxl color_space/CICP 表达。
                            var stdIcc = IccProfileService.GetOrGenerateStandardIcc(inputPath, stdP, stdT, stdM, log, ct);
                            if (stdIcc != null) iccPath = stdIcc;
                        }
                    }
                    else
                    {
                        inputArgs += $"-color_primaries {pP} -color_trc {pT} ";
                        // HDR 目标（PQ/HLG）：源 gamma → 目标传递函数烘焙（format=rgb48le 前置规避 libzimg 尺寸整除/无标签错误）
                        if (pT == "smpte2084" || pT == "arib-std-b67")
                            outputArgs += $"-vf \"format=rgb48le,zscale=transferin=bt709:transfer={pT}\" ";
                        outputArgs += $"-colorspace {pM} ";
                    }
                }
            }
            // auto 模式：探测输入属性。16-bit 且无标签时优先探测 ICC，
            // 有 ICC 则按其语义处理（sRGB 不转换 / 可映射空间 zscale 烘焙 / 不可映射则携带 ICC），
            // 无 ICC 时回退到 Rec.2020→sRGB 转换（HDR 猜测）。
            // Ultra HDR 解码输出（DecodedUltraHdrColorSpace）跳过：线性 HDR 帧已明确色彩语义，
            // 由外部 cjxl 的 color_space/intensity_target 参数表达，不能被源 ICC/猜测烘焙覆盖。
            else if (!string.IsNullOrEmpty(inputPath)
                     && (string.IsNullOrWhiteSpace(options.ColorSpace)
                         || options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
                     && string.IsNullOrWhiteSpace(options.DecodedUltraHdrColorSpace))
            {
                var hdrMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath, log, ct);
                // 超广色域源(Adobe RGB/ProPhoto/ColorMatch)：zscale/iccgen 不能命名其 primaries，
                // 探测会近似为 bt709 → cjxl 误标 color_space=sRGB → 欠饱和偏色。cjxl 又无 SDR-BT.2020 枚举。
                // 因此携带源 ICC（icc_pathname）完整描述色域、像素原样保真（对齐 D2“完整 ICC 描述广色域”）。
                if (ColorSpaceRegistry.IsUltraWide(hdrMeta.sourceGamutName))
                {
                    // 超广色域归入标准 BT.2020：矩阵转像素 + 生成标准 BT.2020 ICC(icc_pathname)。
                    // 传递函数感知 HDR（CICP）：源 trc 为 PQ/HLG → 保留 HDR(bt2020+smpte2084/arib-std-b67)；否则 SDR(bt709)。
                    bool uwHlg = hdrMeta.colorTrc == "arib-std-b67";
                    bool uwHdr = hdrMeta.colorTrc == "smpte2084" || uwHlg;
                    var (stdP, stdT, stdM) = ColorSpaceRegistry.UltraWideNormalizeTokens(hdrMeta.sourceGamutName, uwHdr, uwHlg);
                    var stdIcc = IccProfileService.GetOrGenerateStandardIcc(inputPath, stdP, stdT, stdM, log, ct);
                    var xform = ColorSpaceRegistry.BuildUltraWideToBt2020Filter(hdrMeta.sourceGamutName, uwHdr, uwHlg);
                    if (stdIcc != null && xform != null)
                    {
                        outputArgs += $"-vf \"{xform}\" ";
                        return (inputArgs, outputArgs, stdIcc);
                    }
                    // 回退: 生成失败则携带源 ICC(色彩正确, 避免误标 sRGB)。
                    var (uwIcc, _) = IccProfileService.ExtractIccToTempFile(inputPath, log, ct);
                    if (uwIcc != null)
                    {
                        return (inputArgs, outputArgs, uwIcc);
                    }
                }
                // 广色域源但 cjxl 无 color_space 枚举（DCI-P3/SDR-BT.2020）→ 携带源 ICC 完整描述，
                // 否则既不标 color_space 又不走 icc_pathname → cjxl 回落 sRGB → 欠饱和。
                // （Display P3 有 DisplayP3 枚举，不进此分支；P3 由 hdrMeta→color_space 处理）
                else if (ColorSpaceRegistry.MapPrimariesTransferToCjxl(hdrMeta.colorPrimaries, hdrMeta.colorTrc) == null
                         && hdrMeta.colorPrimaries is "smpte431" or "bt2020")
                {
                    var (wIcc, _) = IccProfileService.ExtractIccToTempFile(inputPath, log, ct);
                    if (wIcc != null)
                    {
                        return (inputArgs, outputArgs, wIcc);
                    }
                }
                if (hdrMeta.bitDepth > 8)
                {
                    if (!string.IsNullOrEmpty(hdrMeta.colorPrimaries))
                    {
                        inputArgs += $"-color_primaries {hdrMeta.colorPrimaries} ";
                    }
                    else
                    {
                        // 无标签 16-bit → 探测输入 ICC，按其语义决定处理方式
                        // Ultra HDR 解码输出场景跳过（线性 HDR 帧色彩由 DecodedUltraHdrColorSpace 表达）
                        var (extractedIcc, iccDesc) = IccProfileService.ExtractIccToTempFile(inputPath, log, ct);
                        // 度量优先（色度+曲线），描述只作兜底：我们生成的标准 ICC 描述是“RGB built-in”
                        var guessed = ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(extractedIcc, iccDesc);
                        if (extractedIcc != null && !string.IsNullOrEmpty(guessed)
                            && string.IsNullOrWhiteSpace(options.DecodedUltraHdrColorSpace))
                        if (extractedIcc != null && !string.IsNullOrEmpty(guessed))
                        {
                            var (primariesIn, transferIn) = MapIccToZscale(guessed);
                            if (primariesIn == "bt709" && transferIn == "bt709")
                            {
                                // sRGB ICC → 无需转换（修正旧逻辑误判 sRGB 内容为 Rec.2020 的问题）
                                // 像素保持原样，ICC 由编码器携带（iccx_pathname）
                                iccPath = extractedIcc;
                                outputArgs += "-colorspace bt709 ";
                            }
                            else if (primariesIn != null)
                            {
                                // 可映射的广色域 ICC（Display P3 / Rec.2020）→ zscale 烘焙到 sRGB
                                outputArgs += $"-vf \"format=rgb48le,zscale=primariesin={primariesIn}:primaries=bt709:transferin={transferIn}:transfer=iec61966-2-1\" ";
                                TryDeleteIcc(extractedIcc); // 已烘焙，无需携带
                            }
                            else
                            {
                                // 不可映射的 ICC（Adobe RGB / ProPhoto 等）→ 像素原样 + 携带 ICC，
                                // 由 cjxl 嵌入输出，查看器按 ICC 正确解释
                                iccPath = extractedIcc;
                            }
                        }
                        else
                        {
                            // 无 ICC：仅 RAW 来源输入才做 Rec.2020→sRGB 的 HDR 猜测(P9)；
                            // 其余无标签 16-bit 输入保守按 sRGB（不猜测/不转换），避免误判。
                            TryDeleteIcc(extractedIcc);
                            if (RawService.IsRawFile(inputPath))
                                outputArgs += "-vf \"format=rgb48le,zscale=primariesin=bt2020:primaries=bt709:transferin=bt709:transfer=iec61966-2-1\" ";
                            else
                                outputArgs += "-colorspace bt709 ";
                        }
                    }
                    if (!string.IsNullOrEmpty(hdrMeta.colorTrc))
                        inputArgs += $"-color_trc {hdrMeta.colorTrc} ";
                    // 修复: PPM/PAM raw 编码器不接受 RGB 色彩空间标记 "gbr"（ffprobe 对 RGB PNG
                    // 回报 color_space=gbr），传入会报 "Unable to parse colorspace option value gbr"
                    // 致 ffmpeg 退出 -22、无输出，进而 cjxl/cjpegli 读到空输入失败（仅 >8bit 输入进入本分支，
                    // 故表现为 16-bit/HDR 转 JXL 失败而 8-bit 正常）。raw 流本就不携带色彩元数据，
                    // 色彩语义由 cjxl 的 -x color_space 表达，故跳过 gbr。
                    if (!string.IsNullOrEmpty(hdrMeta.colorSpace)
                        && !hdrMeta.colorSpace.Equals("gbr", StringComparison.OrdinalIgnoreCase))
                        outputArgs += $"-colorspace {hdrMeta.colorSpace} ";
                }
            }

            // ── 图片最长边限制：把 scale 插到管道 ffmpeg 的滤镜链**链首色彩变换之前** ──
            // 管道 ffmpeg 是手写命令（不走 BuildArguments），需在此统一注入，
            // 确保 cjxl/cjpegli 管道路径也生效最长边限制。
            var scaleFilter = string.IsNullOrEmpty(inputPath)
                ? null
                : FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(options, inputPath);
            if (!string.IsNullOrEmpty(scaleFilter))
            {
                // ⚠ **顺序统一（R1，2026-09-18）**：改为插到**第一条色彩变换之前**，不再插到 `quoteEnd`（= 链尾）。
                //   旧写法 `before + "," + scaleFilter + after` 把 scale 追加到引号内末尾 ⇒ 映射 → 缩放，
                //   与引擎侧（缩放 → 映射）相反（2×2 对照实测 PSNR 36.47dB / 最大差 14417）。
                //   顺序判据只有一份：`FfmpegCommandBuilder.InsertScaleFilterAtChainHead`（`FfmpegCommandBuilder.cs` 内唯一实现）。
                //
                // ⚠ 旧写法里的 `LastIndexOf('"')` 是**侥幸正确**：它假设 `outputArgs` 里带引号的 token
                //   只有 `-vf` 一个。一旦将来别处引入第二个带引号 token（如 `-metadata comment="…"`），
                //   全局最后一个引号会落到它上面 ⇒ 把 scale 插进**无关参数**里。现改为取 `-vf` 之后的
                //   **配对**引号区间，不再依赖「全局最后一个引号」这个隐含前提。
                var vfIdx = outputArgs.LastIndexOf("-vf", StringComparison.Ordinal);
                if (vfIdx >= 0)
                {
                    var quoteStart = outputArgs.IndexOf('"', vfIdx);
                    if (quoteStart >= 0)
                    {
                        var quoteEnd = outputArgs.IndexOf('"', quoteStart + 1);   // 配对引号（不再是全局最后一个）
                        if (quoteEnd > quoteStart)
                        {
                            var chain = outputArgs.Substring(quoteStart, quoteEnd - quoteStart + 1);
                            outputArgs = outputArgs[..quoteStart]
                                       + FfmpegCommandBuilder.InsertScaleFilterAtChainHead(chain, scaleFilter)
                                       + outputArgs[(quoteEnd + 1)..];
                        }
                    }
                }
                else
                {
                    outputArgs += $"-vf \"{scaleFilter}\" ";
                }
            }

            return (inputArgs, outputArgs, iccPath);
        }

        /// <summary>将 ICC 推断的色彩空间名映射为 zscale 的 primariesin/transferin 参数。返回 (null,null) 表示无法映射</summary>
        private static (string? primariesIn, string? transferIn) MapIccToZscale(string guessed)
        {
            return guessed switch
            {
                "sRGB" => ("bt709", "bt709"),
                "Display P3" => ("smpte432", "bt709"),
                "DCI-P3" => ("smpte431", "bt709"),
                "Rec.2020" => ("bt2020", "bt709"),
                "Rec.2100" => ("bt2020", "smpte2084"),
                _ => (null, null)  // Adobe RGB / ProPhoto / ColorMatch 等无 zscale 命名 → 携带 ICC
            };
        }

        private static void TryDeleteIcc(string? path)
        {
            // 委托 IccProfileService 的受保护版本：PLAN/iccs 用户权威 ICC 与纯托管命名缓存
            // 是共享资源绝不删除（私有无条件删除曾会在管道路径误删用户 ICC —— P0-N3）。
            IccProfileService.TryDeleteIcc(path);
        }

        /// <summary>构建 ffmpeg 从 stdin 读取 PPM/PAM 流的命令行参数</summary>
        /// <param name="colorPrefix">输入色彩覆盖参数（如 -color_primaries bt2020 -color_trc smpte2084），
        /// 必须放在 -i 之前才生效；PPM/PAM raw 流不携带任何色彩标签，必须显式声明</param>
        /// <param name="pipeDemuxer">管道 demuxer。默认 image2pipe 仅识别 png/jpeg/tiff 等 image2 编码家族，
        /// 无法识别 PAM 流（Stream #0:0 Video: none, none 报错）。PAM 必须用 pam_pipe，PPM 用 ppm_pipe。</param>
        private static string BuildFfmpegPipeArguments(Models.FfmpegOptions options, string outputPath,
            string? colorPrefix = null, string? pipeDemuxer = null, string? targetFormat = null, bool isWebp = false,
            string? realInputForProbe = null, QueueItem? itemForRegistration = null)
        {
            // 以 stdin (-) 为输入，用专用 pipe demuxer 指定 PPM/PAM 流。
            // ⚠ P7d：本构建的 PPM demuxer 解出来是 **rgb48le**（djxl 对 16-bit JXL 输出 16-bit PPM），
            //   而位深/alpha 探测对 `-` 必然失败 ⇒ 不写 `-pix_fmt` 就让编码器自选像素格式，
            //   同一内容会与主分支逐像素不同（实测 94,22,238 vs 116,16,237）。
            //   故必须把“喂进管道的真实源文件”当探测替代传下去。
            var demuxer = pipeDemuxer ?? "image2pipe";

            // 关键修复：管道场景禁用 iccgen（iccgen 依赖帧色彩标签，PPM/PAM 流无标签且 HDR 会崩溃）。
            // Recommended 与旧 IccMode.None 行为一致（不额外强制携带源 ICC），保持管道原行为不变。
            var pipeOptions = options.DeepClone();
            pipeOptions.ColorStrategy = Models.ColorStrategy.Recommended;

            var args = FfmpegCommandBuilder.BuildArguments(pipeOptions, "-", outputPath, realInputForProbe);
            // 扩展 ③：缩了要让用户看得见（管道路径的命令行同样只读、不读意图）
            if (itemForRegistration != null) LogInjectedScale(itemForRegistration, args);
            // 同源登记：这里用的是 **pipeOptions（已按管道场景改为 Recommended 策略的克隆）**，
            // 必须把同一个实例交出去（登记 item.Options 就是不同源）；探测输入用喂进管道的那份真实源文件。
            if (itemForRegistration != null && !string.IsNullOrWhiteSpace(realInputForProbe))
                NoteColorDecisionSource(itemForRegistration, pipeOptions, realInputForProbe!);

            // 插入 -f <demuxer>（及色彩覆盖）到 -i - 之前
            var idx = args.IndexOf("-i \"-\"", StringComparison.Ordinal);
            if (idx >= 0)
            {
                args = args.Substring(0, idx) + $"{colorPrefix}-f {demuxer} " + args.Substring(idx);
            }
            else
            {
                idx = args.IndexOf("-i -", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    args = args.Substring(0, idx) + $"{colorPrefix}-f {demuxer} " + args.Substring(idx);
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

        /// <summary>
        /// P7e：为动图 AVIF 选“颜色流 / alpha 流”，取代写死的 0:2、0:3。
        /// 规则（可验证、不猜语义）：帧数最多的流 = 颜色（并列取 index 最小）；
        /// 另一条**帧数与颜色流完全相同**的流才当 alpha（alpha 平面必须与颜色逐帧对齐），否则视为无 alpha。
        /// 实测依据见 docs/TESTING.md 六.10（两条流都是彩色 av1/yuv420p、SATAVG 相同 ⇒ 1 帧那条是首帧副本而非 alpha）。
        /// 探不到任何视频流 ⇒ 回退旧映射 (2, 3)，绝不因探测失败而改变已正常工作的路径。
        /// </summary>
        /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26`：调用方一直持有它却没往下传，本次接通）</param>
        private static async Task<(int colorIdx, int? alphaIdx)> ProbeAvifStreamRolesAsync(string inputPath,
            string ffmpegPath, Action<string>? log = null, CancellationToken ct = default)
        {
            try
            {
                var dir = Path.GetDirectoryName(ffmpegPath);
                var ffprobe = string.IsNullOrEmpty(dir) ? "ffprobe.exe" : Path.Combine(dir, "ffprobe.exe");
                if (!File.Exists(ffprobe)) ffprobe = "ffprobe.exe";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobe,
                    // ⚠ 必须 -count_frames：AVIF 容器里常不写 nb_frames，不数帧就分不出主图/首帧副本
                    Arguments = $"-v error -select_streams v -count_frames -show_entries stream=index,nb_read_frames -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false, CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return (2, 3);
                // `#26`：本方法用**同步** `WaitForExit(20000)`，它不接受 ct ⇒ 用 `ct.Register` 杀树接线，
                // 不改调用方签名；原有 20s 超时**不变**（先到者生效）。
                using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
                var to = proc.StandardOutput.ReadToEndAsync(); var te = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(20000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    // 原写法在此**静默**返回兜底值（旧素材上本就正确）⇒ 补一句点名，不再无声。
                    var msg = $"[avif] 流角色探测超时（20s）⇒ 已终止：{Path.GetFileName(inputPath)}（回退 0:2/0:3）";
                    if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
                    return (2, 3);
                }
                if (ct.IsCancellationRequested)
                {
                    var msg = $"[avif] 流角色探测被取消 ⇒ 已终止：{Path.GetFileName(inputPath)}（回退 0:2/0:3）";
                    if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
                    return (2, 3);
                }
                await te;
                var streams = new List<(int idx, int frames)>();
                foreach (var line in (await to).Split('\n'))
                {
                    var parts = line.Trim().Split(',');
                    if (parts.Length < 2) continue;
                    if (int.TryParse(parts[0], out var ix)
                        && int.TryParse(parts[1], out var fr) && fr > 0)
                        streams.Add((ix, fr));
                }
                if (streams.Count == 0) return (2, 3);
                var color = streams.OrderByDescending(s => s.frames).ThenBy(s => s.idx).First();
                int? alpha = streams.Where(s => s.idx != color.idx && s.frames == color.frames)
                                   .OrderBy(s => s.idx).Select(s => (int?)s.idx).FirstOrDefault();
                return (color.idx, alpha);
            }
            catch { return (2, 3); }   // 失败不猜：保持旧行为（旧素材上本来就能跑）
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
                // ⚠⚠ 2026-10-04 修（P1：本克隆点漏抄 3 个字段 ⇒ 动图回退路径语义错位）：
                //   本方法**逐字段手抄**（见下方 :5075 附近的既有警告「漏抄即丢」），
                //   而 2026-10-03/04 新增了**输入声明**轴 `ColorSourceDeclPrimaries/Trc` 与 `--raw-color-target`：
                //   不抄 ⇒ 动图回退（`IsAnimated` ⇒ `CloneOptionsForFfmpeg`）时 `ColorSourceDecl*` 为空
                //   ⇒ `FfmpegCommandBuilder.BuildColorArgsSplit`（`:822-826` 首选 `ColorSourceDecl*`）落空，
                //   回落到 `:832-852` 分支 —— 那条**拿 `ColorPrimaries/ColorTrc` 当 `-i` 前输入声明**，
                //   而它们现在是**输出目标**（PQ/bt2020）⇒ 正是 `ColorSourceDecl*` 这对字段要消灭的缺陷
                //   （该文件 `:813-817` 的注释明写此坑）⇒ 修法必须在这一处补齐，否则修复只覆盖非动图路径。
                ColorSourceDeclPrimaries = original.ColorSourceDeclPrimaries,
                ColorSourceDeclTrc = original.ColorSourceDeclTrc,
                RawColorTarget = original.RawColorTarget,
                ColorFromRawDefault = original.ColorFromRawDefault,
                UseAdvancedColorParameters = original.UseAdvancedColorParameters,
                ColorPrimaries = original.ColorPrimaries,
                ColorTrc = original.ColorTrc,
                ColorMatrix = original.ColorMatrix,
                ColorRange = original.ColorRange,
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
                JpegGainMapDownsample = original.JpegGainMapDownsample,
                JpegGainMapMultiChannel = original.JpegGainMapMultiChannel,
                TiffCompressionAlgo = original.TiffCompressionAlgo,
                // ExifTool
                StripExifGps = original.StripExifGps,
                StripExifTime = original.StripExifTime,
                StripExifCamera = original.StripExifCamera,
                StripExifAll = original.StripExifAll,
                StripXmp = original.StripXmp,
                // 缩略图（逐字段手抄的第二处克隆点，漏抄即丢 ⇒ 与 MainWindow 那处同批登记）
                EnableThumbnail = original.EnableThumbnail,
                ThumbnailLongEdge = original.ThumbnailLongEdge,
                ThumbnailQuality = original.ThumbnailQuality,
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

        // avifenc 的定位与可用性均统一走全局唯一入口 PlatformServices.ResolveAvifencPath()（P7f）。
        // 本文件不得再拼探测路径，也不得另加 HasAvifencAvailable() 这类薄包装：
        //   它作为“第二个口径”导致过静默回退，而且可用性判断写进分派条件后，工具缺失时整个意图
        //   会静默落到通用 ffmpeg 分支（违反“可用性诚实”）。

        // ═══════════════════════════════════════════════
        // JXR 解码器辅助方法（JxrDecApp）
        // ═══════════════════════════════════════════════

        /// <summary>解析 JxrDecApp.exe 路径（解码器，与编码器同目录）</summary>
        /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26`：调用方 `ProcessJxrInputAsync(..., CancellationToken ct)` 持有它却没传下来）</param>
        private static string? ResolveJxrDecAppPath(Action<string>? log = null, CancellationToken ct = default)
        {
            // ① PLAN 便携包自动检测（与 JxrEncApp 同目录 artifacts/）
            var planFound = PlatformServices.TryFindInPlanFolder(PlatformServices.JxrDec);
            if (planFound != null && File.Exists(planFound)) return planFound;

            // ② 从 JxrEncApp 同目录推断
            var encPath = JxrService.DetectedPath;
            if (!string.IsNullOrEmpty(encPath))
            {
                var dir = Path.GetDirectoryName(encPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    var candidate = Path.Combine(dir, PlatformServices.JxrDec);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            // ③ 程序同目录
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var local = Path.Combine(exeDir, PlatformServices.JxrDec);
            if (File.Exists(local)) return local;

            // ④ 系统 PATH
            if (PlatformServices.TryFindInPath(PlatformServices.JxrDec, out var pathFound, log, ct))
                return pathFound;

            return null;
        }

        /// <summary>运行 JxrDecApp 解码器</summary>
        private static async Task<int> RunJxrDecAppAsync(
            string jxrDecPath, string arguments,
            Action<string>? logCallback, CancellationToken ct)
        {
            logCallback?.Invoke($"[jxr-dec] {Path.GetFileName(jxrDecPath)} {arguments}\n");

            var psi = new ProcessStartInfo
            {
                FileName = jxrDecPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };
            process.ErrorDataReceived += (_, e) =>
            { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };

            try
            {
                process.Start();
                PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (ct.CanBeCanceled)
                {
                    using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
                    try { await process.WaitForExitAsync(ct); }
                    catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
                }
                else { await process.WaitForExitAsync(); }
                return process.ExitCode;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[jxr-dec] 启动失败: {ex.Message}\n");
                return -1;
            }
        }

        /// <summary>
        /// <see cref="IsAnimatedAvifAsync"/> 的默认超时（秒）。**与 <see cref="ProbeAvifStreamRolesAsync"/> 的 20 s 口径一致**
        /// —— 同一条 AVIF 探测链上的兄弟站点，取同一预算。
        /// </summary>
        internal const int DefaultAvifProbeTimeoutSec = 20;

        /// <summary>
        /// 取 AVIF 动画探测的超时（秒）：默认 <see cref="DefaultAvifProbeTimeoutSec"/>，可被环境变量
        /// <c>FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC</c> 覆盖（**仅供测试/诊断**，与 <c>FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC</c> 同约定）。
        /// ⚠ 夹紧到 [1, 600]（照 <see cref="ResolveJxlProbeTimeoutSec"/> 的风格）：
        /// <c>0</c>/负数会让看门狗**立刻**触发 ⇒ 所有 AVIF 都被判「不确定」⇒ 一律按多帧（保守，但白跑两步法）。
        /// </summary>
        private static int ResolveAvifProbeTimeoutSec()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable("FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC");
                if (int.TryParse(raw, out var sec)) return Math.Clamp(sec, 1, 600);
            }
            catch { }
            return DefaultAvifProbeTimeoutSec;
        }

        /// <summary>
        /// 探测输入 AVIF 是否为动画。**当前判据只有一条**：视频**流条数** ≥ 2。
        /// ⚠ 措辞（`#136` 切片 B 更正）：**不要**把它理解成「层叠」—— AVIF 动画的标准结构是
        /// **流 0 = 主图 item（`nb_frames=1`）+ 流 1 = 动画轨（`nb_frames=N`）**；静态 avif 恒 1 流。
        /// ⚠ 静态 AVIF（单视频流、无 alpha）回退普通 ffmpeg 单命令路径。
        /// </summary>
        /// <returns>
        /// <c>(animated, determinate)</c>：<c>animated</c> = 探测到的答案（**不确定时恒为 <c>false</c>**）；
        /// <c>determinate</c> = 本次探测是否**确定**（= ffprobe **正常退出** **且** 输出可解析出流信息）。
        /// ⚠ 调用方**必须**消费 <c>determinate</c>：不确定 ⇒ 保守按**多帧**处理
        /// （同 <see cref="InputMayHaveMultipleFramesAsync"/> 的政策：「宁可回退传统管线，也不冒『静默只保留首帧』的风险」）。
        /// </returns>
        /// <param name="log">日志回调（不确定分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26` 接通；`#136` 起与超时合成同一个杀树看门狗）</param>
        private static async Task<(bool animated, bool determinate)> IsAnimatedAvifAsync(string inputPath,
            Action<string>? log = null, CancellationToken ct = default)
        {
            // ⚠ 三态化的**唯一出口**：所有「不确定」的来源都收敛到这里 ⇒ 一条**点名**日志 + determinate=false，
            //   绝不静默把「不知道」当成「不是」（`#136`）。
            void Indeterminate(string why)
            {
                var msg = $"[avif] 动画探测{why} ⇒ 不确定：{Path.GetFileName(inputPath)}（按多帧处理）";
                if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
            }

            try
            {
                var ffprobe = PlatformServices.ResolveFfprobePath(AppSettingsService.Current.FfmpegPath)
                    ?? Path.Combine(Path.GetDirectoryName(AppSettingsService.Current.FfmpegPath) ?? "", "ffprobe.exe");
                if (!File.Exists(ffprobe)) ffprobe = "ffprobe.exe";

                // 探测流信息：数**视频流条数**（`videoCount >= 2`）。
                // ⚠ 措辞更正（`#136` 切片 B，**只改措辞、判据一行未动**）：旧注释把 `videoCount >= 2` 写成
                //   「**层叠**/多流」是**误导** —— AVIF 动画的标准结构**不是**层叠，而是
                //   **流 0 = 主图 item（`nb_frames=1`）+ 流 1 = 动画轨（`nb_frames=N`）**
                //   （实测 `ffmpeg -map 0:1 -f null -` ⇒ `frame=10` / `frame=200`；静态 avif 恒 1 流）。
                //   按「层叠」理解会让人以为两流是同一时刻的两层画面，进而误判「数流条数」只是启发式。
                // ⚠ 旧注释写「或视频流有 >1 帧，或有 alpha 轨道」是**承诺三条、实现一条**（`#136` 切片 A
                //   已把注释改成与实现一致）；扩判据需要素材实测，另立任务。
                var psi = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    // ⚠ 必须带 `nb_frames`（2026-09-20 加入）：判据已从「数流条数」改为「是否真有流带多帧」，
                    //   见下方判据段的更正说明。缺该字段 ⇒ 全部流按「帧数未知」处理 ⇒ 不确定（保守，不会假绿）。
                    Arguments = $"-v error -show_entries stream=index,codec_type,nb_frames -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) { Indeterminate("起不来（ffprobe 未启动）"); return (false, false); }

                // `#136`：本方法原本**没有任何超时**（只有调用方的取消令牌）。
                // 用「链接 CTS + 杀树看门狗」补上 —— 与 `ProbeAvifStreamRolesAsync`（同步版）**同机制**：
                // 超时 / 取消 ⇒ `Kill(entireProcessTree: true)` ⇒ 管道关闭 ⇒ 阻塞中的读**必然**返回。
                // ⚠ 这样既不依赖 `ReadToEndAsync` 的取消语义，也**不把**本方法改成同步 `WaitForExit(int)`（保持异步）。
                var timeoutSec = ResolveAvifProbeTimeoutSec();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                using var killReg = timeoutCts.Token.Register(() =>
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                });

                var output = await ReadStdoutDrainingStderrAsync(proc);
                await proc.WaitForExitAsync();

                if (ct.IsCancellationRequested) { Indeterminate("被取消"); return (false, false); }
                if (timeoutCts.IsCancellationRequested) { Indeterminate($"超时（{timeoutSec}s）"); return (false, false); }
                if (proc.ExitCode != 0) { Indeterminate($"异常退出（exit={proc.ExitCode}）"); return (false, false); }

                var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                // 「确定」= 至少解析到一行流信息（`index,codec_type[,nb_frames]`）；一行都没有 ⇒ 我们什么都没学到。
                if (!lines.Any(l => l.Split(',').Length >= 2 && int.TryParse(l.Split(',')[0], out _)))
                {
                    Indeterminate("输出不可解析");
                    return (false, false);
                }
                // ⚠⚠ 判据**不再是**「视频流条数 ≥ 2」（2026-09-20 更正）。旧判据把「输入有第二条流」当成
                //   「输入多帧」，而**静态 AVIF + alpha**（libavif 产物 `Color`+`Alpha`）与**增益图 AVIF**
                //   （`Color`+`GMap`）都是 2 条流、`nb_frames` 全为 1（实测）⇒ 旧判据把两者**一律误判成动画**：
                //   引擎被整体绕过（`AnimatedInput`），且 AVIF→GIF/WebP 会走两步法并把第二流当成 alpha。
                //   新判据逐条都有实测依据：
                //     · 1 条流                     ⇒ 确定不是动画（`still.avif`）
                //     · 存在 `nb_frames > 1` 的流  ⇒ 确定是动画（`anim_avif.avif` 流1=8 · `a.avif` 流1=10）
                //     · 全为单帧 + 容器带 `tmap`   ⇒ 确定不是动画（增益图；`Color`+`GMap`）
                //     · 全为单帧 + 无 `tmap`       ⇒ **沿用旧行为**（按多帧 ⇒ 两步法）：此时第二流是 **alpha**，
                //       两步法本就是为「分轨提取颜色+alpha」而设，改判静态会**丢 alpha**（`alpha_real.avif` 实测）
                //     · 帧数读不到                 ⇒ 不确定（消费者 `|| !avifProbeDeterminate` ⇒ 保守按多帧）
                var videoStreams = new List<(int Index, int Frames)>();
                foreach (var l in lines)
                {
                    var f = l.Split(',');
                    if (f.Length < 2) continue;
                    if (!f[1].Trim().Equals("video", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse(f[0], out var ix)) continue;
                    int fr = 0;
                    if (f.Length >= 3) int.TryParse(f[2].Trim(), out fr);
                    videoStreams.Add((ix, fr));
                }
                if (videoStreams.Count < 2) return (false, true);        // 单视频流 ⇒ 静态
                int maxFrames = 0;
                bool framesKnown = true;
                foreach (var v in videoStreams)
                {
                    if (v.Frames <= 0) framesKnown = false;
                    else if (v.Frames > maxFrames) maxFrames = v.Frames;
                }
                if (framesKnown && maxFrames > 1) return (true, true);    // 确有流多帧 ⇒ 动画
                if (!framesKnown)
                {
                    // ⚠ 片段用**纯 ASCII**：`Indeterminate(...)` 的参数不是诊断 sink，中文会被
                    //   `_probe-cjk-hardcode-scan` 计入「UI 面」判据（余量为 0）⇒ 按该门禁的既有指引用 ASCII。
                    Indeterminate("multi-stream but frame count unknown");
                    return (false, false);
                }
                var tmap = IsoBmffGainMapProbe.HasTmapItemFast(inputPath);
                if (tmap == null)
                {
                    Indeterminate("multi-stream single-frame but container undecided");
                    return (false, false);
                }
                return tmap.Value ? (false, true) : (true, true);
            }
            catch (Exception ex) { Indeterminate($"异常（{ex.GetType().Name}）"); return (false, false); }
        }

        /// <summary>
        /// <see cref="IsAnimatedWebpAsync"/> 的默认超时（秒）。**与 <see cref="DefaultAvifProbeTimeoutSec"/> 同口径 = 20 s**
        /// —— 两条探测都跑在**引擎路由之前**的同一条路径上（同一项任务最多各跑一次），取同一预算。
        /// </summary>
        internal const int DefaultWebpProbeTimeoutSec = 20;

        /// <summary>
        /// 取 webp 动画探测的超时（秒）：默认 <see cref="DefaultWebpProbeTimeoutSec"/>，可被环境变量
        /// <c>FFMPEGGUI_WEBP_PROBE_TIMEOUT_SEC</c> 覆盖（**仅供测试/诊断**，与
        /// <c>FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC</c> / <c>FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC</c> 同约定）。
        /// ⚠ 夹紧到 [1, 600]（照 <see cref="ResolveJxlProbeTimeoutSec"/> 的风格）：
        /// <c>0</c>/负数会让看门狗**立刻**触发 ⇒ 所有 webp 都被判「不确定」⇒ 一律按多帧
        /// （保守，但每个静态 webp 都白回退传统管线）。
        /// </summary>
        private static int ResolveWebpProbeTimeoutSec()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable("FFMPEGGUI_WEBP_PROBE_TIMEOUT_SEC");
                if (int.TryParse(raw, out var sec)) return Math.Clamp(sec, 1, 600);
            }
            catch { }
            return DefaultWebpProbeTimeoutSec;
        }

        /// <summary>
        /// 跑一次 ffprobe，带「可注入超时 + 取消 + **杀进程树**」看门狗。
        /// <para>
        /// ⚠ 必须**先起异步读、再等退出**：把同步 `ReadToEnd()` 写在 `WaitForExit` 之前会让超时变成
        /// **死代码**（`IsAnimatedJxl` 的旧形态 —— 1 字节 `.jxl` 让队列永久挂死；见
        /// `tests/scripts/_probe-sync-read-before-wait-scan.ps1` 与 `verify-jxl-probe-timeout.ps1`）。
        /// </para>
        /// <para>
        /// ⚠ 与 <see cref="IsAnimatedAvifAsync"/> **同机制、但未共用**：那边把这段写在方法体内联，
        /// 而它的形态被 `tests/scripts/verify-avif-anim-probe.ps1` 的**逐字锚点**钉着（切片 B 明确
        /// 「avif 判据一行不改」）⇒ 此处新增的 helper **只**服务 webp 侧。两者的合并属后续切片。
        /// </para>
        /// </summary>
        /// <returns>
        /// <c>(stdout, failReason)</c>：<c>failReason != null</c> ⇒ 本次探测**失败**（起不来 / 被取消 /
        /// 超时 / 非 0 退出），调用方必须按「**不确定**」处理，**不得**当成"答案是否定的"。
        /// </returns>
        private static async Task<(string stdout, string? failReason)> RunFfprobeWithWatchdogAsync(
            string ffprobe, string args, int timeoutSec, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = args,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return ("", "起不来（ffprobe 未启动）");

            // 超时 / 取消 ⇒ `Kill(entireProcessTree: true)` ⇒ 管道关闭 ⇒ 阻塞中的读**必然**返回。
            // ⚠ 杀**进程树**：只杀父进程会留下 ffprobe 孙进程。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            using var killReg = timeoutCts.Token.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            var output = await ReadStdoutDrainingStderrAsync(proc);
            await proc.WaitForExitAsync();

            if (ct.IsCancellationRequested) return ("", "被取消");
            if (timeoutCts.IsCancellationRequested) return ("", $"超时（{timeoutSec}s）");
            if (proc.ExitCode != 0) return ("", $"异常退出（exit={proc.ExitCode}）");
            return (output, null);
        }

        /// <summary>
        /// 探测输入 webp 是否为动画。**判据是合取式**（`#136` 切片 B）：
        /// <list type="number">
        ///   <item><c>ffprobe -show_entries format=format_name</c>（零解码）⇒ <c>webp_pipe</c> = 静态（**单帧**）、
        ///     <c>webp_anim</c> = 动画**容器** ⇒ 继续第 2 步；其余取值 / 报错 / 超时 ⇒ **不确定**。</item>
        ///   <item><c>ffprobe -count_packets -show_entries stream=nb_read_packets</c>（**全流**）⇒
        ///     取**所有流包数的最大值**：<c>&gt; 1</c> = 多帧、<c>== 1</c> = 单帧；解析不出 ⇒ **不确定**。</item>
        /// </list>
        /// <para>
        /// **为什么必须合取**（实测，`#136` 切片 B1）：`webp_anim` 是**容器**属性 —— 它只说明 `VP8X`
        /// 的动画标志位（`0x02`）置位**且**至少有一个 `ANMF`。存在一个**合法但只有 1 帧**的形态
        /// （`VP8X(0x02) + ANIM + 1×ANMF`）⇒ 单用 `format_name` 会把它误判成**多帧**。
        /// 反过来单用帧数也不够：`VP8X(0x02)` 置位却**没有** `ANMF` 的文件是**非法**的
        /// （ffprobe 报 `Invalid data found`）⇒ 那一路必须落到「不确定」，而不是"单帧"。
        /// </para>
        /// <para>
        /// ⚠ **不得加 `-select_streams v:0`**：对多帧容器 `v:0` 恒为主图 item，会把多帧读成 1 帧
        /// （实测：动画 webp/avif **全流**报 `[1,N]`、`v:0` 报 `[1]`）⇒ 在**决策路径**上等于静默丢帧。
        /// 第 2 步的解析器取**最大值**而不是第一行，就是为了让这条判据不依赖"恰好只有一条流"。
        /// </para>
        /// <para>
        /// **为什么用 `-count_packets` 而不是 `-count_frames`**（实测，`#136` 切片 B1）：
        /// `-count_packets` **零解码**，代价与文件大小无关（实测 3.9 KB → 45 MB、11,500× 体积跨度上
        /// 恒定 47–83 ms）；`-count_frames` 要真解码，实测跨度 48.6 ms → 2578 ms 且**无可靠上界**
        /// （取决于内容与码率，`-threads` 无效）。⚠ 两者在**布尔口径**上一致
        /// （`(packets &gt; 1) == (frames &gt; 1)`，10/10 样本成立），门禁里有交叉断言钉住这一点；
        /// **不要**把"倍数"当契约（实测 1× → 35.7×，是**材料属性**不是信号属性）。
        /// </para>
        /// <para>
        /// ⚠ **`-read_intervals` 压不掉代价**（已实测不可行）：`+#2` / `#2` / `+#2%` 报
        /// `Invalid interval start specification`；`0%+#2` / `+0.08` 语法通过但退化成 `N/A`
        /// ⇒ 真动画被判「不确定」，**比不可行更糟**。
        /// </para>
        /// </summary>
        /// <returns>
        /// <c>(animated, determinate)</c>：<c>animated</c> = 探测到的答案（**不确定时恒为 <c>false</c>**）；
        /// <c>determinate</c> = 本次探测是否**确定**（= 两步 ffprobe 都正常退出**且**输出可解析）。
        /// ⚠ 调用方**必须**消费 <c>determinate</c>：不确定 ⇒ 保守按**多帧**处理
        /// （同 <see cref="InputHasMultipleFramesAsync"/> 的政策）。
        /// </returns>
        private static async Task<(bool animated, bool determinate)> IsAnimatedWebpAsync(string inputPath,
            Action<string>? log = null, CancellationToken ct = default)
        {
            // ⚠ 三态化的**唯一出口**：所有「不确定」的来源都收敛到这里 ⇒ 一条**点名**日志 + determinate=false，
            //   绝不静默把「不知道」当成「不是」（`#136`）。
            void Indeterminate(string why)
            {
                var msg = $"[webp] 动画探测{why} ⇒ 不确定：{Path.GetFileName(inputPath)}（按多帧处理）";
                if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
            }

            // 命中缓存 ⇒ 上一次探测是**确定**的（不确定的结果从不入缓存，见下方两处写入点）。
            // ⚠ 未命中 ⇒ `false`：这是安全的 —— `:282` 的探测**早于** `IsAnimated` 的其余调用点 ⇒ 那边拿到的是热缓存。
            if (AnimatedWebpCache.TryGetValue(inputPath, out var cachedWebp)) return (cachedWebp, true);

            try
            {
                var ffprobe = PlatformServices.ResolveFfprobePath(AppSettingsService.Current.FfmpegPath)
                    ?? Path.Combine(Path.GetDirectoryName(AppSettingsService.Current.FfmpegPath) ?? "", "ffprobe.exe");
                if (!File.Exists(ffprobe)) ffprobe = "ffprobe.exe";

                var timeoutSec = ResolveWebpProbeTimeoutSec();

                // ── 第 1 步：容器名（零解码）──
                var step1 = await RunFfprobeWithWatchdogAsync(ffprobe,
                    $"-v error -show_entries format=format_name -of default=nw=1 \"{inputPath}\"", timeoutSec, ct);
                if (step1.failReason != null) { Indeterminate(step1.failReason); return (false, false); }

                var name = ParseFormatName(step1.stdout);
                if (name == null) { Indeterminate("输出不可解析"); return (false, false); }
                if (name.Equals("webp_pipe", StringComparison.OrdinalIgnoreCase)) { AnimatedWebpCache[inputPath] = false; return (false, true); }
                if (!name.Equals("webp_anim", StringComparison.OrdinalIgnoreCase))
                {
                    // 其它取值（将来的新容器名等）**不是**"单帧"的证据 —— 我们没学到任何东西。
                    Indeterminate($"未知容器名（{name}）");
                    return (false, false);
                }

                // ── 第 2 步：包数（零解码；**全流**，取各流最大值）──
                // ⚠ 这一步是**必需的**：`webp_anim` 只说明"容器允许动画"，不说明"真的有 >1 帧"
                //   （1 帧动画容器是合法形态，实测）。
                var step2 = await RunFfprobeWithWatchdogAsync(ffprobe,
                    $"-v error -count_packets -show_entries stream=nb_read_packets -of default=nw=1 \"{inputPath}\"",
                    timeoutSec, ct);
                if (step2.failReason != null) { Indeterminate(step2.failReason); return (false, false); }

                var packets = ParseMaxNbReadPackets(step2.stdout);
                // ⚠ 解析不出（含 ffprobe 以 exit=0 却只给 `N/A` 的形态）⇒ **不确定**，绝不当成单帧。
                if (packets == null) { Indeterminate("包数不可解析"); return (false, false); }
                var webpIsAnim = packets.Value > 1;
                AnimatedWebpCache[inputPath] = webpIsAnim;
                return (webpIsAnim, true);
            }
            catch (Exception ex) { Indeterminate($"异常（{ex.GetType().Name}）"); return (false, false); }
        }

        /// <summary>
        /// 从 `-show_entries format=format_name -of default=nw=1` 的输出里取容器名（取不到 ⇒ <c>null</c>）。
        /// ⚠ 逐行解析而不是取第一行：`-of default=nw=1` 会打出 `format_name=webp_anim` 这种 `key=value` 行，
        /// 但把"第一行"当答案会在输出形状变化时**静默取错**（例如先打了别的 key）。
        /// </summary>
        private static string? ParseFormatName(string output)
        {
            foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!line.Substring(0, eq).Trim().Equals("format_name", StringComparison.OrdinalIgnoreCase)) continue;
                var v = line.Substring(eq + 1).Trim();
                if (v.Length > 0) return v;
            }
            return null;
        }

        /// <summary>
        /// 从 `-show_entries stream=nb_read_packets -of default=nw=1` 的输出里取**各流包数的最大值**
        /// （一行都解析不到 ⇒ <c>null</c>）。
        /// <para>
        /// ⚠ 必须取**最大值**而不是第一行：与 avif 侧同款理由（那边 `v:0` 恒为主图 item ⇒ 多帧被读成 1 帧）。
        /// webp 目前实测恒 1 条流，但判据**不依赖**这一点。
        /// </para>
        /// <para>
        /// ⚠ `N/A`（`-count_packets` 失败但仍以 0 退出时的形态）**解析不出** ⇒ 返回 <c>null</c>
        /// ⇒ 调用方落「不确定」⇒ 保守按多帧。**这正是要防的那一格**：把 `N/A` 当 0/1 会静默丢帧。
        /// </para>
        /// </summary>
        private static long? ParseMaxNbReadPackets(string output)
        {
            long? max = null;
            foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!line.Substring(0, eq).Trim().Equals("nb_read_packets", StringComparison.OrdinalIgnoreCase)) continue;
                if (long.TryParse(line.Substring(eq + 1).Trim(), out var n))
                {
                    if (max == null || n > max.Value) max = n;
                }
            }
            return max;
        }

        /// <summary>
        /// **引擎路由专用**：这次任务是否**真的有多帧输入**（引擎是单帧处理器，多帧即丢帧）。
        ///
        /// <para>
        /// 为什么不直接用 <see cref="IsAnimated"/>：后者把「**输出容器**是 gif/apng」也算作动图 ——
        /// 那一条说的是"容器**可以**承载动画"，**不是**"本次输入有多帧"。拿它当路由判据的后果是
        /// **所有** gif/apng 输出都被挡在引擎外（连带早已在白名单里的 `apng` 从未真正走到引擎），
        /// 而路由层的形参名与拒绝理由串都写着"**输入**为动画"（<c>ColorEngineRouter.BlockReason</c>）
        /// ⇒ 传进去的值与它声称的含义不是一回事。
        /// </para>
        /// <para>
        /// **判据（`#136` 切片 B 起：输出侧 + 输入侧，两支共用同一份输入侧判据）**：
        /// <list type="bullet">
        ///   <item>输出侧：目标容器是 <c>gif/apng</c>（**可以**承载动画）或用户显式设了
        ///     <c>AnimationFps</c>（多帧意图）⇒ <c>true</c>。</item>
        ///   <item>输入侧：一律走 <see cref="InputHasMultipleFramesAsync"/> —— **不再**因为"输出不是
        ///     gif/apng"就跳过输入探测。</item>
        /// </list>
        /// </para>
        /// <para>
        /// ⚠ **本次修的是哪一格**（`#43`）：旧实现在「输出非 gif/apng」时直接 `return IsAnimated(item)`，
        /// 而 <see cref="IsAnimated"/> 对 `.webp`/`.avif` **恒 false**（它不探测这两者的帧数）⇒
        /// **多帧 webp/avif 输入 + 非 gif/apng 目标**被判成单帧 ⇒ 引擎按单帧处理 ⇒ **静默丢帧**。
        /// 反向的那一格（输出是 gif/apng 时把 `.webp/.avif` **无条件**当多帧）也一并收窄成**真探测** ——
        /// 否则静态 webp 永远进不了引擎，而它本来没有丢帧风险。
        /// </para>
        /// <para>
        /// ⚠ **`#136` 收尾轮更新（上面那句「对 `.webp` 恒 false」已过期）**：<see cref="IsAnimated"/> 的
        /// **输入侧**已补 `.webp` —— 它读 <c>AnimatedWebpCache</c>（由 <see cref="IsAnimatedWebpAsync"/>
        /// 在**确定**时写入），故 `.webp` 现在**不再**恒 false。仅 **`.avif`** 仍恒 false：`.avif` 的多帧
        /// 判定由本函数的输入侧子句与预缩放点各自消费，**不经** <see cref="IsAnimated"/>。
        /// </para>
        /// <para>
        /// ⚠ **保守方向不变**：可能承载动画的容器一律按多帧处理；**探测不确定 ⇒ 也按多帧**
        /// （同 <see cref="IsAnimatedAvifAsync"/> / <see cref="IsAnimatedWebpAsync"/> 的三态纪律）。
        /// 宁可回退传统管线，也不冒"静默只保留首帧"的风险。
        /// </para>
        /// <para>
        /// ⚠ **有代价 ⇒ 每项只求值一次**：`.webp`/`.avif` 输入会真的跑 ffprobe（实测 47~83 ms 量级）。
        /// 调用方（`ProcessAsync`）在**路由判定之前**单点求值一次，供 `ShouldRoute`、GainMap 专用分派、
        /// <see cref="TryRunColorEngineAsync"/> 三处复用 —— 三处必须是**同一份**结果。
        /// ⚠ **`#136` 收尾轮：第 4 个消费点** = 预缩放跳过判定（`ProcessAsync` 里的 `maxDimSkipAnimated`）——
        /// 它已由 `IsAnimated(captured)` 改为直接消费本函数的返回值（理由见该处注释）。
        /// </para>
        /// </summary>
        /// <param name="ct">取消令牌（`#136` 切片 B：探测要能被"停止"打断）</param>
        private static async Task<bool> InputMayHaveMultipleFramesAsync(QueueItem item, CancellationToken ct)
        {
            // ⚠ 2026-09-18 删除「轴 A」短路（原为 `var fmt = …; if (fmt is "gif" or "apng") return true;`）：
            //   本函数问的是**输入**是否可能多帧，**与输出容器无关**。旧代码那句
            //   `if (fmt is not ("gif" or "apng")) return IsAnimated(item);` 是**分支选择器**（决定走哪一支），
            //   **不是**「目标可承载动画 ⇒ 判多帧」的判据。折进来会让**任何 gif/apng 输出**都被判多帧
            //   ⇒ 路由命中 AnimatedInput ⇒ 引擎被挡 ⇒ gif/apng 出口退回 legacy ⇒ 引擎 gif 出口的调色板链
            //   与 alpha 合成全丢（**静默降质**，实测 32.33 vs 44.33 dB）。
            //   回归锁 = `verify-color-strategy.ps1` 的 `map-gif-*` 三格（**只有整轮能抓到**）。
            if (item.Options.AnimationFps.HasValue) return true;      // 用户显式要动图 ⇒ 多帧意图
            return await InputHasMultipleFramesAsync(item, ct);
        }

        /// <summary>
        /// **只问输入文件本身**的多帧判据（与输出格式无关）—— <see cref="InputMayHaveMultipleFramesAsync"/>
        /// 的输入侧子句，**两支共用**。
        /// <para>
        /// ⚠ `.webp`/`.avif` 走**真探测**（三态）；其余按容器名保守判定。
        /// ⚠ `.jxl` 沿用 <see cref="IsAnimatedJxl"/>（它自带「只缓存确定结果」的缓存与 120 s 预算）。
        /// </para>
        /// </summary>
        private static async Task<bool> InputHasMultipleFramesAsync(QueueItem item, CancellationToken ct)
        {
            var inputExt = Path.GetExtension(item.InputPath).ToLowerInvariant();
            if (inputExt is ".gif" or ".apng") return true;
            if (inputExt == ".jxl") return IsAnimatedJxl(item.InputPath, s => item.Log += s);
            if (inputExt == ".webp")
            {
                var (webpProbeAnimated, webpProbeDeterminate) = await IsAnimatedWebpAsync(
                    item.InputPath, s => { item.Log += s; }, ct);
                // 探测**不确定** ⇒ 保守按**多帧**，绝不静默当静态（`#136` 三态纪律）。
                return webpProbeAnimated || !webpProbeDeterminate;
            }
            if (inputExt == ".avif")
            {
                var (avifProbeAnimated, avifProbeDeterminate) = await IsAnimatedAvifAsync(
                    item.InputPath, s => { item.Log += s; }, ct);
                // 同上：不确定 ⇒ 保守按多帧。
                return avifProbeAnimated || !avifProbeDeterminate;
            }
            return false;
        }

        /// <summary>判断队列项是否为动图（输出为GIF/APNG、设有帧率、或输入为动图文件）</summary>
        private static bool IsAnimated(QueueItem item)
        {
            var fmt = (item.Options.Format ?? "").ToLowerInvariant();
            // 输出格式为 GIF / APNG → 始终是动图
            if (fmt is "gif" or "apng") return true;
            // 用户设置了帧率 → 动图模式
            if (item.Options.AnimationFps.HasValue) return true;
            // 输入文件是动图格式（.gif / .apng 可能含动画）
            var inputExt = Path.GetExtension(item.InputPath).ToLowerInvariant();
            if (inputExt is ".gif" or ".apng") return true;
            // ⚠ `#136` 收尾：`.webp` **必须走真探测**，不能按扩展名保守 —— 那会把**静态** webp 也判成动画，
            //   反而把最常见的格子挡在引擎外（假阳性）。读 `AnimatedWebpCache`（由 `IsAnimatedWebpAsync` 在**确定**时写入）。
            //   ⚠ 这里**绝不能**把 `.webp` 加进上面的 `fmt is "gif" or "apng"` —— 那一行是"**输出容器**"轴，
            //   把输入侧折进去正是 B2 那次真回归的形态指纹（`verify-color-strategy` 63/0 → 60/3）。
            if (inputExt == ".webp" && AnimatedWebpCache.TryGetValue(item.InputPath, out var webpAnim) && webpAnim) return true;
            // 动图 JXL：.jxl 扩展名不说明是静图还是动画，需探测帧数（P1-N5）
            if (inputExt == ".jxl" && IsAnimatedJxl(item.InputPath, s => item.Log += s)) return true;
            return false;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> AnimatedJxlCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// <see cref="IsAnimatedWebpAsync"/> 的「**只缓存确定结果**」缓存（`#136` 收尾轮）——
        /// 键 = 输入路径；**只在 <c>determinate = true</c> 时写**（理由与 <see cref="AnimatedJxlCache"/> 逐字同款：
        /// 把「不知道」写成 <c>false</c> 等于永久固化成「它不是动画」⇒ 后续走单帧路径且再也不会重新探测）。
        /// <para>
        /// ⚠ **刻意与 <see cref="AnimatedJxlCache"/> 分开，不合并**：两者判据不同（jxl 用 <c>-count_frames</c>；
        /// webp 用 <c>format_name</c> + <c>-count_packets</c> **合取**），合成一个字典会让"这个结论来自哪套判据"
        /// 失去来源；且 `verify-jxl-probe-timeout.ps1` 逐字钉住了 `AnimatedJxlCache` 的三条形态。
        /// </para>
        /// <para>
        /// ⚠ 消费方是 <see cref="IsAnimated"/> 的**输入侧** `.webp` 子句（**只读**，不触发探测）：
        /// <c>ProcessAsync</c> 在路由判定前已单点探测过 `.webp` ⇒ 那 4 个调用点拿到的是**热缓存**。
        /// 未命中 ⇒ `false`（= 该项还没被探过，与补 `.webp` 之前的行为一致）。
        /// </para>
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> AnimatedWebpCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// <see cref="IsAnimatedJxl"/> 的默认超时（秒）。**实测选定**（依据见其 <c>remarks</c>）；
        /// 改动前请先读那段 —— 调小会让大动画 JXL 落进「按静图处理」的**静默丢帧**路径。
        /// </summary>
        internal const int DefaultJxlProbeTimeoutSec = 120;

        /// <summary>
        /// 取帧数探测超时（秒）：默认 <see cref="DefaultJxlProbeTimeoutSec"/>，可被环境变量
        /// <c>FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC</c> 覆盖（**仅供测试/诊断**，与 <c>FFMPEGGUI_CJXL_STUB</c>、
        /// <c>FFMPEGGUI_FFMPEG_DIR</c> 等同约定）：门禁要在十几秒内验证「超时可达 + Kill 杀树 + 日志响亮」
        /// 这套**机制**，而 120 s × 3 正例 × 3 个变异态会拖垮运行器的时间预算。
        /// ⚠ 夹紧到 [1, 600]：<c>0</c>/负数会让 <c>WaitForExit(0)</c> 立刻返回 <c>false</c>
        /// ⇒ **所有**动画 JXL 都被静默当静图（正是本次要修的静默丢帧）。
        /// </summary>
        private static int ResolveJxlProbeTimeoutSec()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable("FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC");
                if (int.TryParse(raw, out var sec)) return Math.Clamp(sec, 1, 600);
            }
            catch { }
            return DefaultJxlProbeTimeoutSec;
        }

        /// <summary>
        /// 探测 JXL 是否为动画（ffprobe 解码帧数 &gt; 1）。**按路径缓存，但只缓存「确定」的探测结果**
        /// （见 <see cref="ProbeAnimatedJxl"/> 的 <c>determinate</c>）；
        /// 探测失败（ffprobe 缺失/文件异常）按静图处理，回退原有单帧管线。
        /// </summary>
        /// <remarks>
        /// ⚠ **缓存策略（`#26` 裁定，2026-09-18）：只缓存「确定」的探测结果。**
        /// <list type="bullet">
        /// <item>ffprobe **正常退出**且拿到帧数（≤1 帧 ⇒ <c>false</c>；&gt;1 帧 ⇒ <c>true</c>）⇒ **写缓存**。</item>
        /// <item>超时（<see cref="DefaultJxlProbeTimeoutSec"/>）/ 被取消 / ffprobe 起不来 / 输出不可解析
        ///   ⇒ **不写缓存**。</item>
        /// </list>
        /// 理由：在「不确定」的结局上缓存 <c>false</c>，等于把「**我不知道**」永久固化成「**它不是动画**」
        /// —— 后续所有帧都走单帧路径，且**再也不会重新探测**（静默丢帧）；缓存 <c>true</c> 同理会把
        /// 一次抖动固化成永久走动画路径。只有 ffprobe 正常退出时，它的回答才是「确定」的。
        /// </remarks>
        /// <param name="log">
        /// 可选日志出口。⚠ **超时必须响亮**：`ffprobe` 对畸形输入（实测「不以 <c>FF 0A</c> 开头
        /// ⇒ 判 <c>Unknown</c> ⇒ 走通用兜底」且「该字节形状让 ffmpeg 的 jpegxl 解复用器忙等」的**合取**，
        /// 命中 1 B <c>FF</c> / 1 B <c>00</c> / 2 B <c>00 00</c>）**永不退出**，
        /// 此时按静图处理会**静默丢帧** ⇒ 必须留一条可诊断的行。
        /// 传 <c>null</c>（默认）时只是没有日志出口，**行为不变**。
        /// </param>
        /// <remarks>
        /// ⚠ **超时值 120 s 是实测选定的，不是拍的**：`-count_frames` 会**解码全部帧**，成本 ≈
        /// 启动开销 + 帧数 × 每帧解码成本。本机实测（同一 `testsrc2` 裸码流重复拼接成合法多帧 JXL，
        /// `ffprobe -count_frames` 确认帧数正确）：
        /// <list type="bullet">
        /// <item>1080p ≈ 0.023 s/帧</item>
        /// <item>2160p ≈ 0.089 s/帧（4K）</item>
        /// <item>4320p ≈ 0.393 s/帧（8K）</item>
        /// </list>
        /// ⇒ 20 s 只够 **4K ≈226 帧 / 8K ≈51 帧**（= 4K 仅 7.5 s 视频），**真实动画会掉进
        /// 「按静图处理」的静默丢帧路径**；120 s 覆盖 **4K ≈1354 帧（45 s 视频）/ 8K ≈305 帧 /
        /// 1080p ≈5300 帧**。
        /// ⚠ 取舍：超时越长 ⇒ 畸形文件把队列堵得越久（且本探测**没有** <c>CancellationToken</c>，
        /// 「Stop」打断不了它）。按本仓价值序「**静默远比响亮失败糟**」，宁可多等也不静默丢帧。
        /// ⚠ 不要用「文件大小」当预算依据：解码成本取决于**像素数**，与字节数不成比例
        /// （高压缩比的大图字节少但解码一样慢）。
        /// ⚠ 该值可被 <c>FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC</c> 覆盖（见 <see cref="ResolveJxlProbeTimeoutSec"/>），
        /// 但**只供门禁验证机制**：生产默认永远是 <see cref="DefaultJxlProbeTimeoutSec"/> = 120 s，
        /// 门禁里同时有一条源码形态自检钉住这个默认值。
        /// </remarks>
        private static bool IsAnimatedJxl(string inputPath, Action<string>? log = null)
        {
            // 命中缓存 ⇒ 上一次探测是**确定**的（不确定的结果从不入缓存，见下方写入条件与 remarks）。
            if (AnimatedJxlCache.TryGetValue(inputPath, out var cached)) return cached;

            var animated = ProbeAnimatedJxl(inputPath, log, out var determinate);

            // ⚠ **只缓存「确定」的探测结果**：超时 / 被取消 / ffprobe 起不来 / 输出不可解析
            //   ⇒ 答案只是「我不知道」，**不得**写缓存 —— 写 `false` 等于把「不知道」永久固化成
            //   「它不是动画」⇒ 后续所有帧走单帧路径且**再也不会重新探测**（静默丢帧）；
            //   写 `true` 同理会把一次抖动固化成永久走动画路径。
            if (determinate) AnimatedJxlCache[inputPath] = animated;
            return animated;
        }

        /// <summary>
        /// <see cref="IsAnimatedJxl"/> 的实际探测：返回「是否动画」，并经 <paramref name="determinate"/>
        /// 告知本次探测是否**确定**（= ffprobe **正常退出**且**拿到帧数**）。
        /// ⚠ 不确定的结局（超时 / 被取消 / ffprobe 起不来 / 输出不可解析 / 异常）一律
        /// <c>determinate = false</c> ⇒ 调用方**不写缓存**（理由见 <see cref="IsAnimatedJxl"/> 的 remarks）。
        /// </summary>
        private static bool ProbeAnimatedJxl(string inputPath, Action<string>? log, out bool determinate)
        {
            determinate = false;
            try
            {
                var ffprobe = PlatformServices.ResolveFfprobePath(AppSettingsService.Current.FfmpegPath)
                    ?? Path.Combine(Path.GetDirectoryName(AppSettingsService.Current.FfmpegPath) ?? "", "ffprobe.exe");
                if (!File.Exists(ffprobe)) ffprobe = "ffprobe.exe";

                var psi = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    // count_frames 实际解码计数：libjxl demuxer 不填 nb_frames 元数据
                    Arguments = $"-v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;            // ffprobe 起不来 ⇒ 不确定（不缓存）
                // ⚠ 必须**先起异步读、再等超时**（照 `ProbeAvifStreamRolesAsync` 的兄弟范式）：
                //   旧的 `StandardOutput.ReadToEnd()`（同步阻塞）写在 `WaitForExit(30000)` **之前**
                //   ⇒ 管道不关就永不返回，**超时是死代码**（实测 1 字节 `.jxl` 让队列永久挂死、
                //   且超时后也没有 `Kill`）。同步读改成异步读后，`WaitForExit` 才真的能到期。
                var timeoutSec = ResolveJxlProbeTimeoutSec();
                var outTask = proc.StandardOutput.ReadToEndAsync();
                _ = proc.StandardError.ReadToEndAsync();   // stderr 同样必须排空（管道写满会把子进程堵死）
                if (!proc.WaitForExit(timeoutSec * 1000))
                {
                    // ⚠ 杀**进程树**（与兄弟站点一致）：只杀父进程会留下 ffprobe 孙进程。
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    log?.Invoke($"[jxl] 帧数探测超时（{timeoutSec}s）⇒ 已终止 ffprobe：{Path.GetFileName(inputPath)}（按静图处理）\n");
                    return false;                          // 超时 ⇒ 不确定（不缓存）
                }
                var output = outTask.GetAwaiter().GetResult().Trim();
                var first = output.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                if (int.TryParse(first, out var frames))
                {
                    determinate = true;                    // ffprobe 正常退出 **且** 拿到帧数 ⇒ 确定
                    return frames > 1;
                }
                return false;                              // 正常退出但输出不可解析 ⇒ 不确定（不缓存）
            }
            catch { return false; }                        // 异常 ⇒ 不确定（不缓存）
        }
    }
}
