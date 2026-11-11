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
        /// **环境对照**用的裸管道服务端（完全不涉及生产代码）：
        /// 只为回答一个问题——「这个环境里命名管道到底能不能正常收发」。
        /// 若这对自包含的裸管道都跑不通，则之前关于 `IpcService` 往返失败的一切结论都要重新标注为
        /// 「受环境限制，不可断言」，否则才能把责任归到生产代码上。
        /// </summary>
        private static void ProbePipeRawServer(int seconds)
        {
            const string name = "ffmpegpictureui-piperaw-control";
            using var srv = new System.IO.Pipes.NamedPipeServerStream(name,
                System.IO.Pipes.PipeDirection.InOut,
                4,   // 刻意与生产一致（生产是 4；控制组原为 1）——验证实例数是否是差异来源
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            Console.WriteLine("[piperaw-server] 等待连接…");
            var accept = srv.WaitForConnectionAsync();
            if (!accept.Wait(TimeSpan.FromSeconds(10))) { Console.WriteLine("[piperaw-server] 10s 内无人连接"); return; }
            Console.WriteLine("[piperaw-server] 已连接，等待请求…");
            var buf = new byte[256];
            var readTask = srv.ReadAsync(buf, 0, buf.Length);
            if (!readTask.Wait(TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine("[piperaw-server] 10s 内没读到请求字节 ⇒ 本环境管道读不通");
                return;
            }
            var n = readTask.Result;
            Console.WriteLine($"[piperaw-server] 读到 {n} 字节: {Encoding.UTF8.GetString(buf, 0, n).Trim()}");
            var resp = Encoding.UTF8.GetBytes("pong\n");
            srv.Write(resp, 0, resp.Length);
            srv.Flush();
            Console.WriteLine("[piperaw-server] 已回 pong");
            System.Threading.Thread.Sleep(Math.Max(1, seconds) * 1000);
        }

    }
}
