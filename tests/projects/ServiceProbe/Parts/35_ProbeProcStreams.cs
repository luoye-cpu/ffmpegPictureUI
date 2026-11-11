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
        /// P2-1 / P2-3 的直接证据。
        /// ① 8 线程 × 400 次对同一 <see cref="FfmpegGui.Models.QueueItem.Log"/> 做 <c>+=</c>：
        ///    要求一条不丢、一条不重（旧实现是「读快照→拼接→写回」，并发下必然互相覆盖）。
        /// ② 让子进程往 stderr 灌约 170KB：<c>只读 stdout</c> 的写法会死锁（管道缓冲约 4KB 写满后子进程阻塞在写上），
        ///    两条流并发排空则正常返回 —— 用来证明 P2-3 是可复现故障，不是理论风险。
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

    }
}
