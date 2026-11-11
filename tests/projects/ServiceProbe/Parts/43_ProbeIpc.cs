using System;
using System.IO;
using System.Text;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;   // 色彩映射引擎的曲线表（P1）
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;     // 执行内核（P3）
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;     // PSNR/ΔE 度量
using CS = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor;
using CE = FfmpegGui.Services.ColorMapping.ColorMappingEngine;
using CI = FfmpegGui.Services.ColorMapping.ColorIntent;
using CA = FfmpegGui.Services.ColorMapping.ColorAction;
using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
using CL = FfmpegGui.Services.ColorMapping.ColorLoss;
using ST = FfmpegGui.Models.ColorStrategy;
using DC = FfmpegGui.Services.ColorMapping.DescriptorConfidence;
using OR = FfmpegGui.Services.ColorMapping.OverrideReason;
using AC = FfmpegGui.Services.ColorMapping.AnnotChoice;
using CSC = FfmpegGui.Services.ColorMapping.CarryScope;
using MI = FfmpegGui.Services.ColorMapping.ManualIntent;
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;   // S5′ 裁决探针
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;         // `--raw-color-target auto` 判据内核（2026-10-04）
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;          // S5′ 裁决探针
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;     // S5′ 裁决探针
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using GMJ = FfmpegGui.Services.ColorMapping.GainMapJpegCodec;   // GainMap 解码口径/编码参数单一真值（gmgamut）
using AAS = FfmpegGui.Services.AppSettingsService;

namespace ServiceProbe
{
    internal static partial class Program
    {
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

    }
}
