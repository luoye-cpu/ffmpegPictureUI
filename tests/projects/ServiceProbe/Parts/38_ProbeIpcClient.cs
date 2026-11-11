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
        /// 跨进程往返的**客户端**端：连到另一个进程里的 `ipc-server`，发 Ping 并断言拿到 pong。
        /// </summary>
        private static void ProbeIpcClient()
        {
            var c = FfmpegGui.Services.IpcService.TryConnectAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (c == null)
            {
                Check(false, "跨进程：8 秒内未连上 IPC 服务器（对端起了吗？管道名按用户 SID 派生）");
                return;
            }
            using (c)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = FfmpegGui.Services.IpcService.SendRequestAsync(c,
                    new FfmpegGui.Services.IpcService.IpcRequest
                    { Id = 1, Type = FfmpegGui.Services.IpcService.RequestType.Ping },
                    TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                sw.Stop();
                Check(r?.Success == true && r.Message == "pong",
                    $"跨进程 Ping 往返拿到 pong（实耗 {sw.Elapsed.TotalSeconds:F2}s，实得「{r?.Message}」）");
            }
        }

    }
}
