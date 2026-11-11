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
        /// P1-2 的守卫探针：用 <c>cmd.exe</c> 当子进程替身（FfmpegRunner 的 ffmpegPath 可注入），
        /// 这样不必真等 30 分钟也能测“静默 + 不耗 CPU”这一类挂死。
        /// ⚠ 2026-09-19（#15-b 收尾轮）**载体大改**，起因是一条真实回归：心跳改为**默认开启**后，
        /// 注入自检原先**只看参数、不看目标** ⇒ 给 <c>cmd.exe</c> 追加 ` -progress pipe:1` 会**改坏它的
        /// 命令行**（ping 收到未知选项、**0.0 s 退 1**）⇒ case A / case C **两条同时转红**
        /// （E⑫ 整轮实测：`runner` 2/0 → 0/2）。修法（`CanInjectProgressPipe` 加 `IsFfmpegTarget` 一道）后
        /// **cmd.exe 不再被注入** ⇒ 它的活性判据回到**旧双信号路径**。
        /// ⇒ 于是 C 段载体**必须换**：cmd 上「心跳判死」分支已不可达 ⇒ 原载体上「取消优先于心跳判死」
        /// 会**恒绿**（空断言，看着像在测其实没测）。新载体 = **真 ffmpeg + 1 字节畸形 `.jxl`**
        /// （与 <c>verify-ffmpeg-heartbeat.ps1</c> ② 同一 fixture）：名字像 ffmpeg ⇒ **会被注入**，
        /// 且**零进度块**（本探针前置实测：4 s 仍存活、进度 0 字节）⇒ 同时满足「会被注入」∧「零进度块」
        /// 两个必要条件，才能真的二分“谁先到谁赢”。
        /// ⚠ B 段（原 <c>runner --hang</c>）同理：原载体是 cmd、靠**降低阈值**判死；cmd 不再被注入后
        /// 它只能走旧 30 min 路径 ⇒ 换成真 ffmpeg 忙等，并**默认跑**（不再 gate 在 flag 后 —— 否则
        /// 整轮跑的是无参数 `runner`，心跳判死路径会**零覆盖**）。成本 ~2 s（hbSec=1 即判死）。
        /// </summary>
        private static async Task ProbeRunnerGuard(string[] args)
        {
            const string Cmd = @"C:\Windows\System32\cmd.exe";
            // ❗ 替身一律用**绝对路径**，不要写裸命令名：
            //   本宿主下 Start-Process 传给子进程的环境块**不含 System32**（实测 where.exe ping ⇒ exit=1、
            //   cmd 报 "'ping' is not recognized..."），而宿主里 Get-Command ping.exe 却能找到。
            //   用裸名会让本探针在“经门禁脚本跑”时假红（退出码 1/0.2s），而直接跑却绿 —— 曾据此误判为产物陈旧。
            //   生产代码不受影响：FfmpegRunner 用的是 AppSettingsService.Current.FfmpegPath（绝对路径）。
            // ⚠ 2026-10-22 载体大改（旧替身是 `PING.EXE -n 3 127.0.0.1 >nul`）：ping **依赖 ICMP**，
            //   而本机对「非宿主进程的子树」禁 ICMP ⇒ 替身恒退 1（`PING: transmit failed. 常见故障。`×3、
            //   Sent=3 Received=0 Lost=3、2.1s），本探针在整轮与单跑里**恒红**，与被测代码无关。
            //   三层排除之后才动手（不是"看着不顺眼就换载体"）：
            //     ① 逐字段相同的 ProcessStartInfo 在 PowerShell 宿主里 ⇒ **rc=0**/2.09s ⇒ 不是 FfmpegRunner；
            //     ② 在一份与产品**无关**的新建中性控制台程序里 ⇒ **rc=1**/2.1s ⇒ 不是 ServiceProbe；
            //     ③ 换别的静默替身：`timeout.exe /t 2 /nobreak` ⇒ rc=1/0.1s（`Input redirection is not
            //        supported`）、`choice.exe /t 2 /d y` ⇒ rc=1/0.1s ⇒ 本宿主没有控制台输入，两者皆废。
            //   ⇒ 用**不依赖网络也不依赖控制台输入**的 `cmd /c powershell -Command Start-Sleep`：
            //     实测 rc=0、stdout/stderr 全空、~2-3.4s。**牙没有丢**：这个载体真吃实参，一旦有人把
            //     `-progress pipe:1` 注回非 ffmpeg 目标，powershell 会因多余实参报错退出 ⇒ A 段与取消段照样转红。
            const string SilentSleeper = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
            string SilentCarrier(double seconds) =>
                $"/c {SilentSleeper} -NoProfile -NonInteractive -Command Start-Sleep -Milliseconds {(int)(seconds * 1000.0)}";
            // 忙等载体：真 ffmpeg + 1 字节 `0x00` 的 .jxl（与 verify-ffmpeg-heartbeat.ps1 ② 同一 fixture）。
            // ⚠ 实测（本探针落地前）：`-i <1字节.jxl> -frames:v 1 <out.png>` 在 4.0 s 时**仍存活且进度 0 字节**
            //   ⇒ 满足「会被注入」（名字含 ffmpeg）∧「零进度块」两个必要条件。
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runnerprobe_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(work);
            var badJxl = System.IO.Path.Combine(work, "bad.jxl");
            System.IO.File.WriteAllBytes(badJxl, new byte[] { 0x00 });
            var logA = new System.Text.StringBuilder();
            var swA = System.Diagnostics.Stopwatch.StartNew();
            var rcA = await FfmpegRunner.RunAsync(SilentCarrier(2), s => logA.Append(s), Cmd);
            swA.Stop();
            // ⚠ 失败时必须把子进程输出带出来：只报“退出码 1”无法区分“cmd 自己报错”与“守卫误杀”。
            //   实测（2026-09-15）：经 Start-Process 启动本探针时 cmd 退 1/0.2s，直接跑却退 0/2.4s；
            //   没有这段输出就只能靠猜。
            Check(rcA == 0 && logA.ToString().IndexOf("[超时]") < 0,
                $"静默但正常退出的子进程不被误杀（退出码 {rcA}，{swA.Elapsed.TotalSeconds:F1}s）"
                + (rcA == 0 ? "" : $"；子进程输出=<{Dump(logA)}>"));

            // ── C-legacy：取消语义（cmd 替身 ⇒ (A) 后走**旧双信号路径**）──────────────────────
            //   ⚠ 它**不再**经过心跳判死分支 ⇒ 只能验“旧路径的取消语义”，别当成“取消优先于心跳判死”。
            //   ⚠ 两条都用 `threwC` 前置判据 ⇒ **绿/红都算 2 条**，断言总数不随成败漂移（基线才可对账）。
            var logC = new System.Text.StringBuilder();
            var swC = System.Diagnostics.Stopwatch.StartNew();
            var threwC = false;
            var rcC = 0;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    rcC = await FfmpegRunner.RunAsync(SilentCarrier(600), s => logC.Append(s), Cmd, cts.Token);
                }
                catch (OperationCanceledException) { threwC = true; }
            }
            swC.Stop();
            Check(threwC, "C-legacy 取消应抛 OperationCanceledException"
                + (threwC ? "" : $"（实际返回码 {rcC}；子进程输出=<{Dump(logC)}>）"));
            Check(threwC && swC.Elapsed.TotalSeconds < 30,
                $"C-legacy 取消在 {swC.Elapsed.TotalSeconds:F1}s 内传播（子进程被终止，不是等到自然退出）");

            // ── C1：真 ffmpeg 忙等（零进度块）下，**取消必须优先于心跳判死** ──────────────────────
            //   hbSec=8 ⇒ 心跳判死最早落在 ~8 s；取消在 3 s ⇒ 若实现把取消**推迟到心跳 tick**（或让心跳
            //   抢先判死），本用例会拿到 HangExitCode（≈8 s）而不是 OCE ⇒ 转红。
            //   ⚠ 两条自检专防“空断言”：注入必须真的发生、且心跳判死行必须**不出现**。
            {
                var prev = Environment.GetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC");
                Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", "8");
                try
                {
                    var log1 = new System.Text.StringBuilder();
                    var sw1 = System.Diagnostics.Stopwatch.StartNew();
                    var threw1 = false;
                    var rc1 = 0;
                    using (var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    {
                        try
                        {
                            rc1 = await FfmpegRunner.RunAsync(
                                $"-y -hide_banner -loglevel error -i \"{badJxl}\" -frames:v 1 \"{System.IO.Path.Combine(work, "out_c1.png")}\"",
                                s => log1.Append(s), ff, cts1.Token);
                        }
                        catch (OperationCanceledException) { threw1 = true; }
                    }
                    sw1.Stop();
                    var t1 = log1.ToString();
                    Check(threw1, "C1 真 ffmpeg 忙等（零进度块）下，**取消**必须抛 OperationCanceledException"
                        + (threw1 ? "" : $"；实际返回码 {rc1}（{FfmpegRunner.HangExitCode} = 心跳判死抢先），"
                            + $"耗时 {sw1.Elapsed.TotalSeconds:F1}s；输出=<{Dump(log1)}>"));
                    Check(threw1 && sw1.Elapsed.TotalSeconds >= 2.5 && sw1.Elapsed.TotalSeconds <= 6,
                        $"C1 取消在 ~3 s 生效（实测 {sw1.Elapsed.TotalSeconds:F1}s ∈ [2.5, 6]）⇒ **远早于** 8 s 心跳阈值"
                        + " ⇒ 取消没有被推迟到心跳 tick");
                    Check(t1.IndexOf("[心跳] 已追加") >= 0,
                        "C1 自检：本用例确实**注入了** `-progress pipe:1`（否则「取消优先」是空断言）");
                    Check(t1.IndexOf("无进度心跳") < 0,
                        "C1 自检：**没有**出现心跳判死行 ⇒ 判死没有抢先于取消");
                }
                finally { Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", prev); }
            }

            // ── B：忙等（零进度块）必须被心跳**限时判死**（原 `--hang` 的真 ffmpeg 版）────────────
            {
                var prevB = Environment.GetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC");
                Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", "1");
                try
                {
                    var logB = new System.Text.StringBuilder();
                    var swB = System.Diagnostics.Stopwatch.StartNew();
                    var rcB = await FfmpegRunner.RunAsync(
                        $"-y -hide_banner -loglevel error -i \"{badJxl}\" -frames:v 1 \"{System.IO.Path.Combine(work, "out_stall.png")}\"",
                        s => logB.Append(s), ff);
                    swB.Stop();
                    var txt = logB.ToString();
                    Check(rcB == FfmpegRunner.HangExitCode && txt.IndexOf("无进度心跳") >= 0,
                        $"B 忙等（零进度块）被心跳限时判死（退出码 {rcB}，期望 {FfmpegRunner.HangExitCode}，"
                        + $"耗时 {swB.Elapsed.TotalSeconds:F0}s）—— 忙等型挂死不再永不返回");
                }
                finally { Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", prevB); }
            }

            try { System.IO.Directory.Delete(work, true); } catch { }
        }

        /// <summary>
        /// 把子进程输出压成一行并截断，供断言失败时携带。
        /// 只报退出码的诊断价值极低（“退出码 1”既可能是守卫误杀，也可能是 cmd 自己报错）。
        /// </summary>
        private static string Dump(System.Text.StringBuilder sb)
        {
            var s = sb.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length == 0) return "(无输出)";
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }

        /// <summary>
        /// iccname —— ICC 空间识别必须**靠度量**（色度矩阵+曲线），不能被描述文字左右。
        ///
        /// 现场用生产代码生成各标准 ICC，并把描述**强制当成误导值**（“RGB built-in”——那正是
        /// 我们生成的 ICC 的真实描述）再问识别结果：认对 ⇒ 保真/归一/底图原色判断有据可依；
        /// 认错或认不出 ⇒ 这些判断会**静默失效**（上一轮实测就是这个情形）。
        /// </summary>

        private static string? FindRepoRoot()        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && d != null; i++, d = d.Parent)
                if (System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "tools", "src"))) return d.FullName;
            return null;
        }

    }
}
