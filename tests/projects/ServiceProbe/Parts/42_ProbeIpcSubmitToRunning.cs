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
        /// 向**正在运行的实例**（真实 GUI）提交一个任务并等产物：`ipc-submit &lt;输入文件&gt; &lt;输出目录&gt;`。
        /// 用途：验证 GUI 侧两件事 ——
        ///   ① `App.xaml.cs` 起的 IPC 服务端确实可用（而不是只在探针自建的服务端里可用）；
        ///   ② `HandleSubmitJob` 走 `mainWindow != null` 分支时，**S-P1-3 的 UI 线程编组真的生效**：
        ///      编组坏了就不会有产物（要么在非 UI 线程操作控件时抛异常、要么永远排不进队列）。
        ///      ⇒ 这条断言是 S-P1-3 唯一的**运行期**证据（此前只有代码审查）。
        /// ⚠ 需要目标实例已启动且 `EnableIpcServer = true`。
        /// </summary>
        private static void ProbeIpcSubmitToRunning(string inFile, string outDir)
        {
            var c = FfmpegGui.Services.IpcService.TryConnectAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (c == null)
            {
                Check(false, "未连上正在运行的实例（它起了吗？EnableIpcServer 是否为 true？）");
                return;
            }
            using (c)
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    InputPaths = new[] { inFile },
                    OutputDirectory = outDir,
                    Format = "jpg",
                    EncoderBackend = 0,   // EncoderBackend.Ffmpeg
                    Quality = 90,
                    Concurrency = 1,
                    PreserveStructure = false
                });
                var r = FfmpegGui.Services.IpcService.SendRequestAsync(c,
                    new FfmpegGui.Services.IpcService.IpcRequest
                    { Id = 21, Type = FfmpegGui.Services.IpcService.RequestType.SubmitJob, Payload = payload },
                    TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                Check(r?.Id == 21 && r.Success && (r.Message?.Contains("Submitted") ?? false),
                    $"向运行中实例 SubmitJob 受理（Id={r?.Id} Success={r?.Success}「{r?.Message}」）");

                var expected = System.IO.Path.Combine(outDir,
                    System.IO.Path.GetFileNameWithoutExtension(inFile) + ".jpg");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(120) && !System.IO.File.Exists(expected))
                    System.Threading.Thread.Sleep(250);
                Check(System.IO.File.Exists(expected) && new System.IO.FileInfo(expected).Length > 0,
                    $"GUI 队列真的处理并产出（{expected}）⇒ UI 线程编组生效（否则排不进队列或必抛）");
            }
        }

    }
}
