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
        /// memadmit —— 大缓冲**准入门**（<c>MemoryAdmission</c>）的两类自证 + 一个留给门禁去对账的读数行。
        /// ① 互操作读数自洽（0 &lt; avail ≤ total、load ∈ [0,100]）。取不到时必须响亮 SKIP：
        ///    产品侧的降级是"不拦" ⇒ 静默通过 = 这道门不存在而没人知道。
        /// ② 拟合式对**三个实测点**的偏差 ≤15%（钉住"有人只改了一个常数"这种漂移）。
        /// ③ 打印 <c>ADMIT availMB=… totalMB=… load=…</c> ⇒ <c>verify-gainmap-memory.ps1</c> 拿
        ///    CIM 的 FreePhysicalMemory 当**第二个独立来源**对账。结构体布局/未赋 dwLength 是这类
        ///    API 最常见的坏法，而它通常**照样返回 true** ⇒ 只有另一个读者能抓到。
        /// </summary>
        private static void ProbeMemAdmit(string[] args)
        {
            // ── `memadmit pressure`：确定性地走完整条"等待 → 超时 → 仍继续"路径 ──
            // 造一个必然不满足的需求（1e12 像素 ⇒ 估需 ~9.9e7 MB），预算压到 2s：
            //   不靠把真机器压到缺内存才碰得到这条分支（那条路实测**碰不到** —— 8K×4 全成功而日志
            //   一句点名都没有 ⇒ 只靠真压力 = 判据无牙）。
            if (args.Length >= 2 && string.Equals(args[1], "pressure", StringComparison.OrdinalIgnoreCase))
            {
                var lines = new System.Collections.Generic.List<string>();
                var swP = System.Diagnostics.Stopwatch.StartNew();
                FfmpegGui.Services.MemoryAdmission.WaitAsync(1_000_000_000_000L, "pressure-probe",
                    s => lines.Add(s), default, 2_000).GetAwaiter().GetResult();
                swP.Stop();
                string all = string.Concat(lines);
                double elP = swP.Elapsed.TotalSeconds;
                Check(all.Contains("[MemoryAdmission]") && all.Contains("waiting up to"),
                    $"P-1 开始等待必须点名（不静默降级）；实发 {lines.Count} 条");
                Check(all.Contains("proceeding anyway"),
                    "P-2 超时后必须点名「仍继续」，而不是抛异常、也不是永久挂住");
                Check(elP >= 1.9 && elP <= 8.0,
                    $"P-3 预算被尊重：整条路径 {elP:F2}s（预算 2s；下界防忙等、上界防没放行）");
                var linesQ = new System.Collections.Generic.List<string>();
                FfmpegGui.Services.MemoryAdmission.WaitAsync(4L, "quiet-probe",
                    s => linesQ.Add(s), default, 2_000).GetAwaiter().GetResult();
                Check(linesQ.Count == 0,
                    $"P-4 空闲明显够时**一句都不许发**（实发 {linesQ.Count} 条 ⇒ 会刷满日志）");
                // 反向对账：估算必须**随像素单调**，否则"大帧先被拦住"这件事根本不成立
                double e1 = FfmpegGui.Services.MemoryAdmission.EstimateMb(2_000_000);
                double e2 = FfmpegGui.Services.MemoryAdmission.EstimateMb(20_000_000);
                Check(e2 > e1 * 5.0, $"P-5 估算随像素放大（2MP={e1:F0}MB vs 20MP={e2:F0}MB）");
                Console.WriteLine("  ADMIT-PRESSURE lines=" + lines.Count + " elapsedS=" + elP.ToString("F2"));
                return;
            }

            bool got = FfmpegGui.Services.MemoryAdmission.TryGetAvailableMb(
                out double avail, out double total, out int load);
            Check(got, $"① GlobalMemoryStatusEx 可用且读数自洽（实 avail={avail:F0}MB total={total:F0}MB load={load}%）");
            if (!got)
            {
                Skip("②③ 依赖可用读数 ⇒ 未判定（非 Windows 或 API 失败；产品侧同时已降级为「不拦」）");
                return;
            }
            // 实测三点（WorkingSet64，与 verify-gainmap-memory 同夹具）：见 MemoryAdmission 文档
            var pts = new (double Mp, double Measured)[] { (2.0736, 263.5), (8.2944, 818.4), (33.1776, 3363.1) };
            foreach (var (mp, measured) in pts)
            {
                double est = FfmpegGui.Services.MemoryAdmission.EstimateMb((long)(mp * 1_000_000.0));
                double dev = Math.Abs(est - measured) / measured;
                Check(dev <= 0.15,
                    $"② 拟合 {mp:F2}MP ⇒ 估 {est:F0}MB vs 实测 {measured:F1}MB（偏差 {dev:P1}，上限 15%）");
            }
            Console.WriteLine($"  ADMIT availMB={avail:F0} totalMB={total:F0} load={load}");
        }

    }
}
