using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tests.Shared;
using FfmpegGui.Services;
using CA = FfmpegGui.Services.ColorMapping.ColorAction;
using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;
namespace Tests.Io
{
    internal static class IoGroup
    {
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);
        // ── 入口（供 Program 分派；方法体即抽出的 Probe* 原文）──
        internal static void RunIpc() => ProbeIpc();
        internal static void RunProcStreams() => ProbeProcStreams();
        private static void Skip(string msg) => Harness.Skip(msg);
        private static string RunCapture(string exe, string args, string? redirectOut = null) => Harness.RunCapture(exe, args, redirectOut);
        private static int RunCap(string exe, string args) => Harness.RunCap(exe, args);

        private static void ProbeIpc()
        {
            // ⚠ 本探针只做**自包含**的两项（不依赖与自身进程内的 IPC 服务器做往返）：
            //   ① S-P1-6：对端接受连接但永不回包时，客户端必须按超时返回，绝不能无限挂起；
            //   ② S-P1-5：能力缓存字段类型 + 并发读冒烟。
            // 为什么不做「应用 IPC 服务器往返」：实测在**同进程内**同时跑服务端与客户端时，
            //   Ping 得不到响应（客户端 3 秒超时返回「请求超时」），根因未定位；
            //   而往返用例一旦挂住会把整条门禁拖死 ⇒ 在查清之前**不要**把它加进门禁清单。
            //   服务端循环本身的修复（S-P1-4）见源码注释，其缺陷有直接证据：
            //   修复前**第一个**客户端就会让服务器抛异常死掉（客户端侧表现为 IOException: Pipe is broken）。

            // ① 静默对端：接受连接但永不回包
            try
            {
                var sn = "ffmpegpictureui-probe-silent-" + Environment.ProcessId;
                using var silent = new System.IO.Pipes.NamedPipeServerStream(sn,
                    System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await silent.WaitForConnectionAsync();
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(20));
                });
                using var sc = new System.IO.Pipes.NamedPipeClientStream(".", sn,
                    System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                sc.Connect(3000);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var rTo = FfmpegGui.Services.IpcService.SendRequestAsync(sc,
                    new FfmpegGui.Services.IpcService.IpcRequest
                    { Id = 99, Type = FfmpegGui.Services.IpcService.RequestType.Ping },
                    TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                sw.Stop();
                Check(rTo != null && !rTo.Success && (rTo.Message?.Contains("超时") ?? false)
                        && sw.Elapsed.TotalSeconds < 10,
                    $"静默对端下客户端按超时返回（实耗 {sw.Elapsed.TotalSeconds:F1}s，消息「{rTo?.Message}」）"
                    + " —— 旧写法读无 token、写/刷无界，这里会无限挂起");
            }
            catch (Exception ex)
            {
                Check(false, $"静默对端超时用例异常: {ex.GetType().Name}: {ex.Message}");
            }

            // ② S-P1-5：缓存字段类型（确定性断言）+ 并发读冒烟
            var fld = typeof(FfmpegGui.Services.FormatCapabilitiesService)
                .GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(fld != null && fld.FieldType.Name.Contains("ConcurrentDictionary"),
                $"能力缓存字段是并发字典（实为 {fld?.FieldType.Name}）");

            var errsBox = new long[1];
            var init = System.Threading.Tasks.Task.Run(
                () => FfmpegGui.Services.FormatCapabilitiesService.InitializeAsync());
            var readers = new System.Threading.Tasks.Task[8];
            for (int i = 0; i < readers.Length; i++)
            {
                int seed = i;
                readers[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int k = 0; k < 400; k++)
                    {
                        try
                        {
                            _ = FfmpegGui.Services.FormatCapabilitiesService
                                .GetCapabilities(((seed + k) % 2 == 0) ? "PNG" : "jxl");
                        }
                        catch { System.Threading.Interlocked.Increment(ref errsBox[0]); }
                    }
                });
            }
            System.Threading.Tasks.Task.WaitAll(readers);
            try { init.Wait(TimeSpan.FromSeconds(60)); } catch { }
            Check(errsBox[0] == 0, $"并发读能力表 + 初始化写不抛异常（异常 {errsBox[0]} 次）");
            Check(FfmpegGui.Services.FormatCapabilitiesService.GetCapabilities("PNG") != null,
                "大写「PNG」也能命中缓存（ToLowerInvariant 与缓存键一致）");

            // ③ IPC 往返（同进程）：起服务端 → 连它 → Ping → 期望 pong。
            //    这一条在 2026-09-15 之前**不可能**通过：服务端用 StreamReader/StreamWriter 会抛异常
            //    被 `catch (Exception) { }` 静默吞掉（对端表现为刚连上就断管），且消息被
            //    `AppJsonContext.WriteIndented = true` 序列化成**多行** JSON（接收端按行只拿到碎片）
            //    ⇒ 服务端永远不回包、客户端只能超时。两处都修好后实测 0.07 秒拿到 pong。
            //    ⚠ 管道名按用户 SID 派生 ⇒ 跑本探针时**不能**有正在运行的 GUI 实例。
            {
                var cts = new System.Threading.CancellationTokenSource();
                var srv = System.Threading.Tasks.Task.Run(
                    () => FfmpegGui.Services.IpcService.StartServerAsync(null, null, cts.Token));
                System.Threading.Thread.Sleep(300);
                var c = FfmpegGui.Services.IpcService.TryConnectAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (c == null)
                {
                    Check(false, "IPC 往返：5 秒内未连上本进程内的服务端（管道名被占用？先关掉运行中的实例）");
                }
                else
                {
                    using (c)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var r = FfmpegGui.Services.IpcService.SendRequestAsync(c,
                            new FfmpegGui.Services.IpcService.IpcRequest
                            { Id = 42, Type = FfmpegGui.Services.IpcService.RequestType.Ping },
                            TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                        sw.Stop();
                        Check(r?.Success == true && r.Message == "pong",
                            $"IPC 往返（同进程）拿到 pong（实耗 {sw.Elapsed.TotalSeconds:F2}s，实得「{r?.Message}」）");
                    }
                }
                // ④ 请求处理器：响应必须**回显 req.Id**，且「什么都没做」时**不得谎报成功**。
                //    旧写法把 Id 写死成 0（客户端按 resp.Id == req.Id 匹配 ⇒ GetStatus/Pause/Resume/Stop
                //    这些请求永远匹配不上、只能超时，尽管服务端其实已经执行了）；并且在既无 MainWindow
                //    也无 orchestrator 时仍报 Success=true（"Paused"/"Stopped"）——谎报成功比报错更糟。
                FfmpegGui.Services.IpcService.IpcResponse? Ask(
                    FfmpegGui.Services.IpcService.RequestType t, long id)
                {
                    var cc = FfmpegGui.Services.IpcService
                        .TryConnectAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    if (cc == null) return null;
                    using (cc)
                    {
                        return FfmpegGui.Services.IpcService.SendRequestAsync(cc,
                            new FfmpegGui.Services.IpcService.IpcRequest { Id = id, Type = t },
                            TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    }
                }

                // ⚠ 断言必须同时看 Message：`SendRequestAsync` 超时返回的**合成响应**也带 req.Id
                //    ⇒ 只断言 `Id == req.Id` 是**空断言**（Id 写死 0 时客户端匹配不上、走超时兜底，
                //    而兜底响应的 Id 恰好就是 req.Id）。这一点是**变异测试抓出来的**：
                //    把 GetStatus 的 `Id = req.Id` 改回 0，只断言 Id 的版本依然全绿。
                var st = Ask(FfmpegGui.Services.IpcService.RequestType.GetStatus, 7);
                Check(st != null && st.Id == 7 && st.Success && (st.Message?.Contains("队列计数不可用") ?? false),
                    $"GetStatus 回显 req.Id 且拿到**真实**响应（发 7，实得 Id={st?.Id} Success={st?.Success}「{st?.Message}」）");
                var ps = Ask(FfmpegGui.Services.IpcService.RequestType.Pause, 8);
                Check(ps != null && ps.Id == 8 && !ps.Success && (ps.Message?.Contains("未执行") ?? false),
                    $"无 MainWindow/orchestrator 时 Pause 如实报失败且回显 Id（发 8，实得 Id={ps?.Id} Success={ps?.Success}「{ps?.Message}」）");
                var sp = Ask(FfmpegGui.Services.IpcService.RequestType.Stop, 9);
                Check(sp != null && sp.Id == 9 && !sp.Success && (sp.Message?.Contains("未执行") ?? false),
                    $"无 MainWindow/orchestrator 时 Stop 如实报失败且回显 Id（发 9，实得 Id={sp?.Id} Success={sp?.Success}「{sp?.Message}」）");

                try { cts.Cancel(); } catch { }
                try { srv.Wait(TimeSpan.FromSeconds(3)); } catch { }
            }

            // ⑤ 端到端 SubmitJob：IPC → DefaultQueueOrchestrator → QueueProcessor → 真实转换 → 产物。
            //    这是 IPC 的**核心功能**（把任务提交给正在运行的实例），而此前 IPC 从未跑通
            //    ⇒ 这条路径**从未被验证过**。无 MainWindow 时处理器走 orchestrator 分支，
            //    而 DefaultQueueOrchestrator 有无参构造 ⇒ 可以 headless 驱动。
            //    ⚠ 这里必须**另起一个服务端**（带真实 orchestrator）：上面的块用的是 (null, null)，
            //      若共用会把 Pause/Stop 的断言语义改掉（有 orchestrator 时它们会返回 Success=true）。
            {
                var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "ipcsubmit_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                var inDir = System.IO.Path.Combine(work, "in");
                var outDir = System.IO.Path.Combine(work, "out");
                System.IO.Directory.CreateDirectory(inDir);
                System.IO.Directory.CreateDirectory(outDir);
                var srcImg = System.IO.Path.Combine(inDir, "a.png");

                var ff = System.IO.Path.Combine(
                    Environment.GetEnvironmentVariable("FFMPEGGUI_FFMPEG_DIR") ?? "", "ffmpeg.exe");
                if (!System.IO.File.Exists(ff))
                {
                    Console.WriteLine("INFO 未找到 ffmpeg（FFMPEGGUI_FFMPEG_DIR 未设？）⇒ ⑤ SubmitJob 用例 SKIP");
                }
                else
                {
                    var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ff,
                        Arguments = $"-y -v error -f lavfi -i \"testsrc=size=64x64:rate=1\" -frames:v 1 \"{srcImg}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })!;
                    mk.WaitForExit();

                    // DefaultQueueOrchestrator 是 internal ⇒ 用**反射**构造并转成公开的 IQueueOrchestrator。
                    // 之所以不用 InternalsVisibleTo：改 csproj 会触发 NuGet 还原，而本环境还原不可用
                    // （见 HANDOVER §C.4 的 ProgramData 坑）⇒ 反射是这里最省事且不动生产配置的办法。
                    var orchType = typeof(FfmpegGui.Services.IpcService).Assembly
                        .GetType("FfmpegGui.Services.DefaultQueueOrchestrator", throwOnError: true)!;
                    var orch = (FfmpegGui.Services.IQueueOrchestrator)Activator.CreateInstance(orchType)!;
                    var cts2 = new System.Threading.CancellationTokenSource();
                    var srv2 = System.Threading.Tasks.Task.Run(
                        () => FfmpegGui.Services.IpcService.StartServerAsync(orch, null, cts2.Token));
                    System.Threading.Thread.Sleep(300);

                    var cc = FfmpegGui.Services.IpcService
                        .TryConnectAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    if (cc == null)
                    {
                        Check(false, "SubmitJob：5 秒内未连上服务端");
                    }
                    else
                    {
                        using (cc)
                        {
                            // 手搓 payload JSON（IpcJsonContext 是 internal，探针用不了）；
                            // 属性名必须与 SubmitJobPayload 完全一致（源生成默认大小写敏感）。
                            var payload = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                InputPaths = new[] { srcImg },
                                OutputDirectory = outDir,
                                Format = "jpg",
                                EncoderBackend = 0,   // EncoderBackend.Ffmpeg
                                Quality = 90,
                                Concurrency = 1,
                                PreserveStructure = false
                            });
                            var r = FfmpegGui.Services.IpcService.SendRequestAsync(cc,
                                new FfmpegGui.Services.IpcService.IpcRequest
                                {
                                    Id = 11,
                                    Type = FfmpegGui.Services.IpcService.RequestType.SubmitJob,
                                    Payload = payload
                                },
                                TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                            Check(r?.Id == 11 && r.Success && (r.Message?.Contains("Submitted") ?? false),
                                $"SubmitJob 受理（实得 Id={r?.Id} Success={r?.Success}「{r?.Message}」）");

                            // 等产物：这一步才算端到端（IPC → 编排 → 队列 → ffmpeg → 文件）
                            var outFile = System.IO.Path.Combine(outDir, "a.jpg");
                            var sw2 = System.Diagnostics.Stopwatch.StartNew();
                            while (sw2.Elapsed < TimeSpan.FromSeconds(90) && !System.IO.File.Exists(outFile))
                                System.Threading.Thread.Sleep(200);
                            Check(System.IO.File.Exists(outFile) && new System.IO.FileInfo(outFile).Length > 0,
                                $"SubmitJob 端到端产出文件（期望 {outFile}）");
                        }
                    }
                    try { cts2.Cancel(); } catch { }
                    try { srv2.Wait(TimeSpan.FromSeconds(3)); } catch { }
                }
            }
        }

        /// <summary>
        /// S-P1-1 / S-P0-2 的三条不变式（用 --settings 独立文件驱动真实的 AppSettingsService）：
        /// ① 写不进去时 Save() 必须返回 false 并说清原因（旧写法 <c>catch {}</c> 吞掉 ⇒ 用户以为已保存）；
        /// ② 原子写不留 <c>.tmp</c>，且第二次保存要把上一版留在 <c>.bak</c>（旧写法直写，打断即毁唯一一份）；
        /// ③ 设置文件损坏时 Load 必须留下 <c>LastLoadIssue</c>（旧写法静默重置为默认值）。
        /// </summary>

        private static void ProbeProcStreams()
        {
            var it = new FfmpegGui.Models.QueueItem();
            const int T = 8, N = 400;
            System.Threading.Tasks.Parallel.For(0, T, t =>
            {
                for (int i = 0; i < N; i++) it.Log += $"t{t}-{i};";
            });
            int seen = 0;
            for (int t = 0; t < T; t++) for (int i = 0; i < N; i++) if (it.Log.Contains($"t{t}-{i};")) seen++;
            int separators = it.Log.Split(';').Length - 1;
            // P2-1 的回归锁：属性内修好后，并发追加必须一条不丢、一条不重
            Check(seen == T * N && separators == T * N,
                $"并发追加日志一条不丢不重（期望 {T * N}，实际在位 {seen}，写入计数 {separators}）");

            const string Flood = "/c for /L %i in (1,1,8000) do @echo stderr-flood-line-%i 1>&2";
            System.Diagnostics.Process Start() => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = @"C:\Windows\System32\cmd.exe",
                    Arguments = Flood,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;

            var p1 = Start();
            bool naiveDone = System.Threading.Tasks.Task.Run(() => p1.StandardOutput.ReadToEnd()).Wait(TimeSpan.FromSeconds(10));
            Check(!naiveDone, $"只读 stdout 在 stderr 洪泛下卡死（10 秒内未返回＝预期；实际返回={naiveDone}）");
            try { p1.Kill(entireProcessTree: true); } catch { }
            try { p1.WaitForExit(3000); } catch { }

            var p2 = Start();
            var bothTask = System.Threading.Tasks.Task.Run(async () =>
            {
                var o = p2.StandardOutput.ReadToEndAsync();
                var e = p2.StandardError.ReadToEndAsync();
                await System.Threading.Tasks.Task.WhenAll(o, e);
                await p2.WaitForExitAsync();
                return e.Result.Length;
            });
            bool drained = bothTask.Wait(TimeSpan.FromSeconds(120));
            Check(drained && bothTask.IsCompletedSuccessfully && bothTask.Result > 100_000,
                $"两条流并发排空即可正常读完（stderr 实得 {(bothTask.IsCompletedSuccessfully ? bothTask.Result : 0)} 字符）");
        }

        /// <summary>
        /// P2-5 行为锁：子进程输出必须按 UTF-8 解码，且该结论不依赖宿主环境。
        /// ① 原始流逐字节比对 ⇒ 证明子进程（同 ffmpeg/cjxl/exiftool 一族）产出确为 UTF-8 字节，
        ///    即「声明 UTF-8」是正确的解码选择，而不是随手挑的；
        /// ② 声明 <c>StandardOutputEncoding/StandardErrorEncoding = UTF8</c> ⇒ 两条流逐字还原；
        /// ③ 负控：同一批字节按非 UTF-8 单字节代码页解必然不还原 ⇒ ② 的断言能识别解码错误，不是空跑；
        /// ④ 现场复现：宿主输出编码为非 UTF-8（zh-CN 控制台的 936 等）且**不声明**解码编码 ⇒ 乱码。
        ///    这正是 P2-5 的现场；修好后每处重定向都显式声明，行为与环境解耦。
        /// </summary>

    }
}

