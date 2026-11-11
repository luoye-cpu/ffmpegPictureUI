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
        /// <summary>
        /// S-P1-3/4/5/6 的行为锁：用**生产代码**的 IpcService 跑一轮真实命名管道往返。
        /// ① 服务器能连续服务多个客户端 —— 旧写法在循环外先 await 一次 WaitForConnectionAsync
        ///    把第 1 个连接建立起来，循环里又调**阻塞的** WaitForConnection()（此时管道已连接）
        ///    ⇒ 抛 InvalidOperationException，异常从 fire-and-forget 任务里逃出
        ///    ⇒ **第一个客户端到来时服务器就死了**，之后全部静默失联；
        /// ② 客户端发坏数据/提前断开只影响它自己，后续客户端仍可正常 Ping；
        /// ③ SendRequestAsync 的超时**真的生效**（旧写法 ReadLineAsync 没传 token ⇒ 对端不回包就无限挂起）；
        /// ④ 能力缓存字段是 ConcurrentDictionary 且并发读 + 初始化写不抛异常（S-P1-5）。
        /// ⚠ 用真实管道名（按用户 SID 派生）⇒ **不能**与正在运行的 GUI 实例同时跑。
        /// </summary>
        /// <summary>
        /// 跨进程往返的**服务端**端：起 IPC 服务器并驻留指定秒数。
        /// 为什么需要它：上一轮把服务端与客户端放在**同一个进程**里测往返时，Ping 拿不到响应（根因未定位）；
        /// 而真实的 IPC 用法本来就是跨进程（第二个实例 / CLI 连到正在跑的 GUI）⇒ 用两个进程重测，
        /// 把「同进程」这个干扰因素排除掉。
        /// ⚠ 管道名按用户 SID 派生 ⇒ 测试期间**不能**有正在运行的 GUI 实例。
        /// </summary>
        private static void ProbeIpcServer(int seconds)
        {
            var cts = new System.Threading.CancellationTokenSource();
            var task = FfmpegGui.Services.IpcService.StartServerAsync(null, null, cts.Token);
            Console.WriteLine($"[ipc-server] 已启动，驻留 {seconds}s");
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(seconds));
            try { cts.Cancel(); } catch { }
            try { task.Wait(TimeSpan.FromSeconds(3)); } catch { }
            Console.WriteLine("[ipc-server] 退出");
        }

    }
}
