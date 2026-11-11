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
        /// 跨进程往返的**裸字节客户端**：不经过 `StreamReader`/`StreamWriter`，直接往已连接的管道
        /// 写原始请求字节、再直接读原始响应字节。
        /// 目的：把上一轮那个未定位的问题**一分为二**——
        ///   · 若裸字节能收到响应 ⇒ 服务端处理是好的，问题在客户端的 Stream 层用法；
        ///   · 若裸字节也收不到 ⇒ 服务端确实没读到请求，问题在管道/服务端侧。
        /// ⚠ 需要先起 `ipc-server`（管道名按用户 SID 派生，测试期间不能有正在运行的 GUI 实例）。
        /// </summary>
        private static void ProbeIpcRawClient()
        {
            var c = FfmpegGui.Services.IpcService.TryConnectAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (c == null) { Check(false, "裸字节：8 秒内未连上 IPC 服务器"); return; }
            using (c)
            {
                var req = "{\"Id\":7,\"Type\":6,\"Payload\":null}\n";   // Type=6 → Ping
                var bytes = Encoding.UTF8.GetBytes(req);
                try
                {
                    c.Write(bytes, 0, bytes.Length);
                    c.Flush();
                    Console.WriteLine($"[raw] 已写 {bytes.Length} 字节，等待原始响应…");
                }
                catch (Exception ex)
                {
                    Check(false, $"裸字节写入失败: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                var buf = new byte[512];
                var readTask = c.ReadAsync(buf, 0, buf.Length);
                var done = System.Threading.Tasks.Task.WhenAny(readTask,
                    System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
                if (!ReferenceEquals(done, readTask))
                {
                    Check(false, "裸字节：5 秒内没读到任何响应字节 ⇒ 服务端侧没处理该请求（不是 StreamReader 的问题）");
                    return;
                }
                var n = readTask.GetAwaiter().GetResult();
                var text = Encoding.UTF8.GetString(buf, 0, n);
                Check(n > 0 && text.Contains("pong"),
                    $"裸字节：收到 {n} 字节响应「{text.Trim()}」（含 pong ⇒ 服务端处理正常，问题在客户端 Stream 层）");
            }
        }

    }
}
