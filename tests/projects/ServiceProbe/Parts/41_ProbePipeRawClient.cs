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
        /// <summary>**环境对照**用的裸管道客户端（见 <see cref="ProbePipeRawServer"/>）。</summary>
        private static void ProbePipeRawClient()
        {
            const string name = "ffmpegpictureui-piperaw-control";
            using var c = new System.IO.Pipes.NamedPipeClientStream(".",
                name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            try
            {
                // 关键对照点：生产代码的 TryConnectAsync 用的是 **ConnectAsync(超时)**，
                // 而能跑通的控制组用的是**同步** Connect(超时)。这里刻意改用 ConnectAsync 复现差异。
                var ca = c.ConnectAsync(8000);
                if (!ca.Wait(TimeSpan.FromSeconds(12)))
                {
                    Check(false, "裸管道对照(ConnectAsync)：连接未在 12 秒内完成");
                    return;
                }
            }
            catch (Exception ex) { Check(false, $"裸管道对照(ConnectAsync)：连接失败 {ex.GetType().Name}: {ex.Message}"); return; }
            try
            {
                var req = Encoding.UTF8.GetBytes("ping\n");
                c.Write(req, 0, req.Length);
                c.Flush();
            }
            catch (Exception ex)
            {
                Check(false, $"裸管道对照：**写入**失败 {ex.GetType().Name}: {ex.Message} ⇒ 本环境的管道写入受限，"
                    + "之前关于 IpcService 往返失败的结论不可断言");
                return;
            }
            var buf = new byte[256];
            var readTask = c.ReadAsync(buf, 0, buf.Length);
            if (!readTask.Wait(TimeSpan.FromSeconds(5)))
            {
                Check(false, "裸管道对照：5 秒内没读到响应 ⇒ 本环境管道收发不通");
                return;
            }
            var n = readTask.Result;
            Check(n > 0 && Encoding.UTF8.GetString(buf, 0, n).Contains("pong"),
                $"裸管道对照：自包含的裸管道往返**正常**（收到「{Encoding.UTF8.GetString(buf, 0, n).Trim()}」）"
                + " ⇒ 管道本身可用，IpcService 的往返失败可归到生产代码");
        }

    }
}
