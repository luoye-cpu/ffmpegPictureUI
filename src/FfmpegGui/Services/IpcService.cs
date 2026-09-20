using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FfmpegGui.Models;

namespace FfmpegGui.Services
{
    /// <summary>
    /// Named Pipe 单实例 IPC 服务
    /// 管道名称: ffmpegpictureui-{user-sid} （每用户隔离）
    /// 协议: JSON Lines (每行一个 JSON 消息)
    /// </summary>
    public static class IpcService
    {
        private const string PipeBaseName = "ffmpegpictureui";
        private static readonly string PipeName = GetPipeName();
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

        private static readonly ConcurrentQueue<IpcRequest> PendingRequests = new();
        private static CancellationTokenSource? _shutdownCts;

        /// <summary>IPC 请求类型</summary>
        public enum RequestType
        {
            SubmitJob,      // 提交任务队列
            GetStatus,      // 查询状态
            Pause,          // 暂停队列
            Resume,         // 继续队列
            Stop,           // 停止队列
            Shutdown,       // 退出进程（GUI 优雅关闭）
            Ping            // 心跳/连接测试
        }

        /// <summary>IPC 请求载体</summary>
        public class IpcRequest
        {
            public long Id { get; set; }              // 请求 ID（用于匹配响应）
            public RequestType Type { get; set; }     // 请求类型
            public string? Payload { get; set; }      // JSON 字符串载荷
        }

        /// <summary>IPC 响应载体</summary>
        public class IpcResponse
        {
            public long Id { get; set; }
            public bool Success { get; set; }
            public string? Message { get; set; }
            public string? Data { get; set; }  // JSON 字符串
        }

        public sealed record SubmitJobPayload(
            string[] InputPaths,
            string OutputDirectory,
            string Format,
            EncoderBackend EncoderBackend,
            int Quality,
            int Concurrency,
            bool PreserveStructure,
            Dictionary<string, string>? ExtraOptions = null
        );

        public sealed record StatusPayload(
            bool IsRunning,
            int TotalItems,
            int CompletedItems,
            int FailedItems,
            string? CurrentFile
        );

        /// <summary>获取当前用户专属管道名（含 SID，Windows）或用户名（跨平台）</summary>
        private static string GetPipeName()
        {
            string userId;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var sid = WindowsIdentity.GetCurrent()?.User?.Value;
                    if (!string.IsNullOrEmpty(sid)) return $"{PipeBaseName}-{sid}";
                }
                userId = Environment.UserName;
            }
            catch
            {
                userId = "user";
            }
            return $"{PipeBaseName}-{userId}";
        }

        /// <summary>
        /// 尝试以客户端身份连接已有实例；成功返回连接流，失败返回 null
        /// </summary>
        public static async Task<NamedPipeClientStream?> TryConnectAsync(TimeSpan timeout)
        {
            try
            {
                var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync((int)timeout.TotalMilliseconds);
                return client;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 发送请求并等待响应（同步等待，带超时）
        /// </summary>
        public static async Task<IpcResponse?> SendRequestAsync(NamedPipeClientStream client, IpcRequest req, TimeSpan timeout)
        {
            var effective = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(10);
            var deadline = DateTime.UtcNow + effective;
            IpcResponse TimedOut() => new() { Id = req.Id, Success = false, Message = "请求超时" };
            // 用同一个 deadline 给每一步兜底：任何一步超期都立刻返回，绝不再无界等待。
            async Task<bool> Bounded(Task t)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                return ReferenceEquals(
                    await Task.WhenAny(t, Task.Delay(remaining, CancellationToken.None)).ConfigureAwait(false), t);
            }

            try
            {
                var body = JsonSerializer.Serialize(req, IpcJsonContext.Default.IpcRequest) + "\n";
                var bytes = Encoding.UTF8.GetBytes(body);

                // ⚠ 修复（S-P1-6）三层，缺一不可：
                //  ① 旧写法 `await reader.ReadLineAsync()` **完全没传 token**，签名上的 `timeout` 也从未被使用
                //     （内部硬编码 10 秒）⇒ 对端不回包时永久挂起；
                //  ② 只给 ReadLineAsync 传 token 仍不够：管道读一旦在飞行中，取消不保证能中断它；
                //  ③ **实测最要命的一段是写入**：对端接受连接但不消费时，`WriteAsync` 会一直挂着
                //     —— 此时读阶段的超时**根本没机会生效**（本仓实测：静默对端 + 2 秒超时仍然挂死）。
                //     所以写、刷、读三段全部用 WhenAny + Delay 有界化。
                var writeTask = client.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);
                if (!await Bounded(writeTask)) return TimedOut();
                await writeTask.ConfigureAwait(false);

                var flushTask = client.FlushAsync(CancellationToken.None);
                if (!await Bounded(flushTask)) return TimedOut();
                await flushTask.ConfigureAwait(false);

                // 等待响应行
                using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                while (true)
                {
                    var readTask = reader.ReadLineAsync();
                    if (!await Bounded(readTask)) return TimedOut();
                    var line = await readTask.ConfigureAwait(false);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var resp = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcResponse);
                        if (resp?.Id == req.Id) return resp;
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException)
            {
                return TimedOut();
            }
            catch (IOException ex)
            {
                return new IpcResponse { Id = req.Id, Success = false, Message = "连接已断开: " + ex.Message };
            }
            return null;
        }

        /// <summary>
        /// 启动 IPC 服务器（在主程序启动时调用）
        /// </summary>
        public static async Task StartServerAsync(IQueueOrchestrator? orchestrator, MainWindow? mainWindow, CancellationToken shutdownToken = default)
        {
            _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);

            // ⚠ 修复（S-P1-4，含两个连带缺陷）：
            //  ① 旧写法在循环**外**先 await 一次 WaitForConnectionAsync 把第 1 个连接建立起来，
            //     紧接着在循环里又调**阻塞的** server.WaitForConnection()；此刻管道**已经处于已连接状态**，
            //     该调用抛 InvalidOperationException，异常从 fire-and-forget 的任务里逃出去（无人 await）
            //     ⇒ **第一个客户端到来时服务器就死了**，之后所有连接静默失联，调用方看不到任何错误。
            //  ② 复用同一个 NamedPipeServerStream + Disconnect() 时，accept 之后**读端很快失效**：
            //     实测客户端连上后一写就报 `IOException: Pipe is broken`，往返永远拿不到响应
            //     （与 ① 是两回事——修完 ① 仍复现）。改为**每次连接新建服务端对象**，`using` 结束即断开。
            while (!_shutdownCts.Token.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    4, // Max connections
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await server.WaitForConnectionAsync(_shutdownCts.Token);
                }
                catch (OperationCanceledException) { break; }   // 正常关闭
                catch (Exception) { break; }                    // 管道对象已不可用，无法继续

                if (_shutdownCts.Token.IsCancellationRequested) break;

                try
                {
                    // ⚠ 修复（S-P1-4 的第三个连带缺陷）：**不要**在这条管道上用 StreamReader/StreamWriter。
                    // 实测证据链：① 裸字节客户端「刚连上就写失败：Pipe is broken」⇒ 服务端在 accept 之后
                    // 很快关闭了读端；② 换成裸字节服务端后，服务端**确实收到了**客户端请求字节
                    // （追踪到 `raw read n=30`）；③ 控制组（自包含的裸管道，同样 4 实例/ConnectAsync/异步）
                    // 往返完全正常 ⇒ 环境、连接方式、实例数都已排除。
                    // ⇒ 结论：`new StreamReader/StreamWriter(server, …)` 这一步在这条管道上抛异常，
                    //   被下面的 `catch (Exception) { }` 静默吞掉，随后 `using var server` 释放
                    //   ⇒ 对端看到断管、往返永远拿不到响应。改用**裸字节 + 手工分行**（协议是 JSON Lines）。
                    var readBuf = new byte[64 * 1024];
                    var pending = new StringBuilder();
                    while (!_shutdownCts.Token.IsCancellationRequested)
                    {
                        var n = await server.ReadAsync(readBuf.AsMemory(0, readBuf.Length), _shutdownCts.Token);
                        if (n <= 0) break;   // 对端关闭

                        pending.Append(Encoding.UTF8.GetString(readBuf, 0, n));
                        int nl;
                        while ((nl = IndexOfNewline(pending)) >= 0)
                        {
                            var line = pending.ToString(0, nl);
                            pending.Remove(0, nl + 1);
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            IpcRequest? req;
                            try { req = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcRequest); }
                            catch { continue; }
                            if (req == null) continue;

                            var resp = await HandleRequestAsync(req, orchestrator, mainWindow);
                            if (resp != null)
                            {
                                var outBytes = Encoding.UTF8.GetBytes(
                                    JsonSerializer.Serialize(resp, IpcJsonContext.Default.IpcResponse) + "\n");
                                await server.WriteAsync(outBytes.AsMemory(0, outBytes.Length), _shutdownCts.Token);
                                await server.FlushAsync(_shutdownCts.Token);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }   // 关闭中
                catch (Exception) { }                          // 单个客户端出错：只断开它，不拖垮服务器
                // 本轮 `using` 结束即断开该连接；下一轮循环为下一个客户端**新建**服务端对象
            }
        }

        /// <summary>在 <see cref="StringBuilder"/> 里找第一个换行符（JSON Lines 分行用）</summary>
        private static int IndexOfNewline(StringBuilder b)
        {
            for (int i = 0; i < b.Length; i++)
            {
                if (b[i] == '\n') return i;
            }
            return -1;
        }

        private static async Task<IpcResponse?> HandleRequestAsync(IpcRequest req, IQueueOrchestrator? orchestrator, MainWindow? mainWindow)
        {
            // S-P1-3：IPC 服务器跑在线程池线程上，而 MainWindow 是 Avalonia 控件树（**非线程安全**）。
            // AddItemsToQueue / PauseQueue / ResumeQueue / Close 这些都必须回到 UI 线程执行，
            // 否则轻则 UI 状态错乱，重则抛异常或进程崩溃。
            // 无 Avalonia（headless 服务 / ServiceProbe）时直接执行，保持原有语义。
            if (mainWindow != null && Avalonia.Application.Current != null)
                return await OnUiThreadAsync(() => DispatchAsync(req, orchestrator, mainWindow));
            return await DispatchAsync(req, orchestrator, mainWindow);
        }

        /// <summary>
        /// 把动作编组到 Avalonia UI 线程执行并取回结果。
        /// 不用 <c>Dispatcher.InvokeAsync</c> 的多个重载：本仓按 Avalonia 12 的 API 走
        /// <c>Post</c> + <c>TaskCompletionSource</c>，语义明确且没有重载二义性风险。
        /// </summary>
        private static Task<T> OnUiThreadAsync<T>(Func<Task<T>> action)
        {
            var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
            if (dispatcher.CheckAccess()) return action();
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.Post(async () =>
            {
                try { tcs.TrySetResult(await action()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        private static async Task<IpcResponse?> DispatchAsync(IpcRequest req, IQueueOrchestrator? orchestrator, MainWindow? mainWindow)
        {
            return req.Type switch
            {
                RequestType.Ping => new IpcResponse { Id = req.Id, Success = true, Message = "pong" },

                RequestType.SubmitJob => await HandleSubmitJob(req, orchestrator, mainWindow),

                RequestType.GetStatus => HandleGetStatus(req, orchestrator),

                RequestType.Pause => await HandlePauseAsync(req, orchestrator, mainWindow),

                RequestType.Resume => await HandleResumeAsync(req, mainWindow),

                RequestType.Stop => await HandleStopAsync(req, orchestrator, mainWindow),

                RequestType.Shutdown => await HandleShutdownAsync(req, orchestrator, mainWindow),

                _ => new IpcResponse { Id = req.Id, Success = false, Message = $"Unknown request type: {req.Type}" }
            };
        }

        private static async Task<IpcResponse?> HandleSubmitJob(IpcRequest req, IQueueOrchestrator? orchestrator, MainWindow? mainWindow)
        {
            SubmitJobPayload? payload;
            try { payload = JsonSerializer.Deserialize(req.Payload ?? "{}", IpcJsonContext.Default.SubmitJobPayload); }
            catch (Exception ex) { return new IpcResponse { Id = req.Id, Success = false, Message = $"Invalid payload: {ex.Message}" }; }

            if (payload == null || payload.InputPaths.Length == 0)
                return new IpcResponse { Id = req.Id, Success = false, Message = "No input files" };

            var items = new List<QueueItem>();
            foreach (var path in payload.InputPaths)
            {
                // 简单的文件/目录展开
                var files = ExpandInput(pattern: path);
                foreach (var file in files)
                {
                    var opts = new FfmpegOptions
                    {
                        Format = payload.Format,
                        EncoderBackend = payload.EncoderBackend,
                        Quality = payload.Quality,
                    };

                    // ── 透传轴（色彩策略 S1–S4 / 色彩空间 / ICC / GainMap / 元数据 / 位深 / 色度 / 几何 …）──
                    // 复用 CLI 的 key→字段映射 `CliParser.ApplyExtraOption`：IPC 与 CLI 必须是**同一套语义**，
                    // 另写一份平行映射迟早会漂移（例如同一个 `--bit-depth` 在两侧落到不同字段）。
                    // 非法取值口径也随之天然一致（见 CliParser.cs 的「取值解析失败统一口径」一节）：
                    //   · 2026-09-19 P1-E 起，**一切「可解析取值」的参数**（int/bool/double/float、
                    //     color-strategy / icc-mode / color-engine / color-gamut-map /
                    //     jpeg-gain-map-base-gamut）解析失败一律抛 ArgumentException ⇒ 这里转成
                    //     Success=false + 原因。这是 IPC 协议**原生的**报错通道（与上面 :325 的
                    //     "Invalid payload: …" 同款），不是第三种口径；若让它逃到
                    //     StartServerAsync 的 `catch (Exception) { }` 里，客户端会以为"配置生效了"。
                    //     ⚠ 修复前只有 color-gamut-map 一族会抛，color-strategy / icc-mode 与全部
                    //       typed 解析是**静默忽略**的（同族两套口径）—— 那段旧注释已随之作废。
                    //   · 仍**不**校验自由字符串型取值（chroma / color-space / …）：取值域在下游，
                    //     详见 CliParser.cs 同节「刻意不做的两件事」。
                    // ⚠ 保持向后兼容：ExtraOptions 为 null / 空时，下面的循环一次都不执行，
                    //   行为与修复前逐字相同（回归锁 verify-ipc-extra-options.ps1 的 E*/F* 锁住了这点）。
                    if (payload.ExtraOptions != null)
                    {
                        foreach (var kv in payload.ExtraOptions)
                        {
                            try
                            {
                                CliParser.ApplyExtraOption(opts, kv.Key, kv.Value);
                            }
                            catch (ArgumentException ex)
                            {
                                return new IpcResponse
                                {
                                    Id = req.Id,
                                    Success = false,
                                    Message = $"Invalid payload: {ex.Message}"
                                };
                            }
                        }
                    }
                    var outputPath = BuildOutputPath(file, payload.OutputDirectory, payload.Format, payload.PreserveStructure);
                    items.Add(new QueueItem
                    {
                        InputPath = file,
                        OutputPath = outputPath,
                        Options = opts
                    });
                }
            }

            if (items.Count == 0)
                return new IpcResponse { Id = req.Id, Success = false, Message = "No matching input files" };

            // 优先通过 MainWindow 入队（包含 UI 更新）；后备 orchestrator
            int added = 0;
            if (mainWindow != null)
            {
                added = mainWindow.AddItemsToQueue(items);
                if (added > 0 && !mainWindow.IsQueueRunning)
                    mainWindow.StartProcessing();
                else if (added == 0)
                    return new IpcResponse { Id = req.Id, Success = false, Message = "No matching input files" };
            }
            else
            {
                // 无 MainWindow（headless 服务）：直接用 orchestrator
                if (orchestrator != null && !orchestrator.IsRunning)
                    await orchestrator.StartAsync(items, payload.Concurrency);
                else
                    return new IpcResponse { Id = req.Id, Success = false, Message = "队列正在运行，请先停止再提交新任务" };
                added = items.Count;
            }

            return new IpcResponse { Id = req.Id, Success = true, Message = $"Submitted {added} jobs" };
        }

        private static IpcResponse? HandleGetStatus(IpcRequest req, IQueueOrchestrator? orchestrator)
        {
            // ⚠ 队列计数目前**拿不到**：IQueueOrchestrator 只暴露 IsRunning，没有队列长度/完成数/失败数。
            // 旧写法把三个计数写死成 0 却报 Success=true、Message="ok" ⇒ 客户端会把「0」当成真实状态。
            // 这里保留 0（协议字段不凭空改语义），但把「不可用」这件事写进 Message，避免把假数据当真。
            var data = JsonSerializer.Serialize(new StatusPayload(
                IsRunning: orchestrator?.IsRunning ?? false,
                TotalItems: 0, // 实际需要 orchestrator 暴露队列长度
                CompletedItems: 0,
                FailedItems: 0,
                CurrentFile: null
            ), IpcJsonContext.Default.StatusPayload);
            return new IpcResponse
            {
                Id = req.Id,
                Success = true,
                Message = orchestrator == null
                    ? "ok（无 orchestrator：IsRunning 恒 false，队列计数不可用）"
                    : "ok（队列计数不可用：orchestrator 未暴露长度/完成数/失败数）",
                Data = data
            };
        }

        private static async Task<IpcResponse?> HandlePauseAsync(IpcRequest req, IQueueOrchestrator? orchestrator, MainWindow? mainWindow)
        {
            if (mainWindow != null)
            {
                mainWindow.PauseQueue();
                return new IpcResponse { Id = req.Id, Success = true, Message = "Paused" };
            }
            if (orchestrator != null)
            {
                await orchestrator.StopAsync(graceful: true);
                return new IpcResponse { Id = req.Id, Success = true, Message = "Paused (orchestrator)" };
            }
            // ⚠ 旧写法在两者都为 null 时**仍报 Success=true / "Paused (orchestrator)"**，实际什么都没做
            // ⇒ 谎报成功比报错更糟（客户端会以为暂停生效了）。改为显式失败并说明原因。
            return new IpcResponse
            {
                Id = req.Id,
                Success = false,
                Message = "Pause 未执行：既没有 MainWindow 也没有 orchestrator"
            };
        }

        private static Task<IpcResponse?> HandleResumeAsync(IpcRequest req, MainWindow? mainWindow)
        {
            if (mainWindow != null)
            {
                mainWindow.ResumeQueue();
                return Task.FromResult<IpcResponse?>(new IpcResponse { Id = req.Id, Success = true, Message = "Resumed" });
            }
            // 没有 mainWindow 时无法续跑（orchestrator 未提供 Resume 能力）——旧写法这条其实是对的，只是 Id 没回显
            return Task.FromResult<IpcResponse?>(new IpcResponse
            {
                Id = req.Id,
                Success = false,
                Message = "Resume 未执行：需要 MainWindow（orchestrator 未提供续跑能力）"
            });
        }

        private static async Task<IpcResponse?> HandleStopAsync(IpcRequest req, IQueueOrchestrator? orchestrator, MainWindow? mainWindow)
        {
            bool did = false;
            if (mainWindow != null)
            {
                mainWindow.PauseQueue();   // MainWindow 目前只公开 PauseQueue（没有公开的 StopQueue）
                did = true;
            }
            if (orchestrator != null)
            {
                await orchestrator.StopAsync(graceful: true);
                did = true;
            }
            return new IpcResponse
            {
                Id = req.Id,
                Success = did,
                Message = !did
                    ? "Stop 未执行：既没有 MainWindow 也没有 orchestrator"
                    : (mainWindow != null
                        ? "Stopped（MainWindow 侧只做了 Pause：它没有公开的 StopQueue）"
                        : "Stopped (orchestrator)")
            };
        }

        private static async Task<IpcResponse?> HandleShutdownAsync(IpcRequest req, IQueueOrchestrator? orchestrator, MainWindow? mainWindow)
        {
            if (mainWindow != null)
            {
                mainWindow.AllowFullExit = true;
                mainWindow.Close();
            }
            if (orchestrator != null)
                await orchestrator.StopAsync(graceful: false);
            return new IpcResponse { Id = req.Id, Success = true, Message = "Shutting down" };
        }

        // 简易的输入展开（文件/目录/通配符）
        private static string[] ExpandInput(string pattern)
        {
            if (File.Exists(pattern)) return new[] { pattern };
            if (Directory.Exists(pattern))
            {
                var exts = AppSettings.AllImageFormats.Values.SelectMany(e => e).Union(AppSettings.AllVideoFormats.Values.SelectMany(e => e)).ToArray();
                var list = new List<string>();
                foreach (var ext in exts)
                    list.AddRange(Directory.GetFiles(pattern, "*" + ext, SearchOption.AllDirectories));
                return list.ToArray();
            }
            // 通配符
            var dir = Path.GetDirectoryName(pattern);
            var fn = Path.GetFileName(pattern);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            if (Directory.Exists(dir))
            {
                return Directory.GetFiles(dir, fn, SearchOption.TopDirectoryOnly);
            }
            return Array.Empty<string>();
        }

        private static string BuildOutputPath(string inputFile, string outputDir, string format, bool preserve)
        {
            var ext = "." + format.ToLowerInvariant();
            var name = Path.GetFileNameWithoutExtension(inputFile) + ext;
            if (string.IsNullOrEmpty(outputDir)) return Path.Combine(Path.GetDirectoryName(inputFile) ?? ".", name);
            if (preserve)
            {
                var rel = Path.GetRelativePath(Path.GetPathRoot(inputFile)!, inputFile);
                return Path.Combine(outputDir, rel);
            }
            return Path.Combine(outputDir, name);
        }
    }
}