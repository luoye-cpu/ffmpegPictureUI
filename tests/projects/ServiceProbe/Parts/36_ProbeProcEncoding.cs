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
        /// P2-5 行为锁：子进程输出必须按 UTF-8 解码，且该结论不依赖宿主环境。
        /// ① 原始流逐字节比对 ⇒ 证明子进程（同 ffmpeg/cjxl/exiftool 一族）产出确为 UTF-8 字节，
        ///    即「声明 UTF-8」是正确的解码选择，而不是随手挑的；
        /// ② 声明 <c>StandardOutputEncoding/StandardErrorEncoding = UTF8</c> ⇒ 两条流逐字还原；
        /// ③ 负控：同一批字节按非 UTF-8 单字节代码页解必然不还原 ⇒ ② 的断言能识别解码错误，不是空跑；
        /// ④ 现场复现：宿主输出编码为非 UTF-8（zh-CN 控制台的 936 等）且**不声明**解码编码 ⇒ 乱码。
        ///    这正是 P2-5 的现场；修好后每处重定向都显式声明，行为与环境解耦。
        /// </summary>
        private static void ProbeProcEncoding()
        {
            const string Text = "中文路径·测试_图片-αβγ.png";
            const string ExpectedOut = "OUT:" + Text;
            const string ExpectedErr = "ERR:" + Text;

            // 宿主可能是 ServiceProbe.exe 自身，也可能经 dotnet 启动（此时 ProcessPath=dotnet.exe，需补 dll）
            var host = Environment.ProcessPath!;
            var prefix = System.IO.Path.GetFileNameWithoutExtension(host)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? "\"" + System.Reflection.Assembly.GetEntryAssembly()!.Location + "\" "
                : "";
            var arguments = prefix + "emit-utf8 \"" + Text + "\"";

            System.Diagnostics.ProcessStartInfo Psi(Encoding? enc) => new()
            {
                FileName = host,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc
            };

            // ① 原始字节：子进程产出的是 UTF-8（读 BaseStream，绕开任何解码）
            using (var p = System.Diagnostics.Process.Start(Psi(null))!)
            {
                using var ms = new System.IO.MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(ms);
                p.WaitForExit();
                var raw = ms.ToArray();
                var want = Encoding.UTF8.GetBytes(ExpectedOut);
                var same = raw.Length == want.Length && raw.AsSpan().SequenceEqual(want);
                Check(same, $"子进程产出确为 UTF-8 字节（原始流 {raw.Length} 字节 vs 期望 {want.Length} 字节，逐字节{(same ? "一致" : "不一致")}）");
            }

            // ② 声明 UTF-8 解码 ⇒ 两条流逐字还原
            using (var p = System.Diagnostics.Process.Start(Psi(Encoding.UTF8))!)
            {
                var o = p.StandardOutput.ReadToEnd();
                var e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Check(o == ExpectedOut, $"声明 UTF-8 后 stdout 逐字还原（实得「{o}」）");
                Check(e == ExpectedErr, $"声明 UTF-8 后 stderr 逐字还原（实得「{e}」）");
            }

            // ③ 负控：证明上述断言能识别「解码器选错」
            var wrong = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(ExpectedOut));
            Check(wrong != ExpectedOut,
                "负控：同批字节按非 UTF-8 单字节代码页解码必不还原 ⇒ 上述断言能识别解码错误（非空跑）");

            // ④ 现场复现：宿主输出编码非 UTF-8 + 不声明解码编码（.NET 默认就用宿主 Console.OutputEncoding）
            var saved = Console.OutputEncoding;
            string observed;
            bool simulated;
            try
            {
                Console.OutputEncoding = Encoding.Latin1;   // 代表 zh-CN 控制台的 936 一类非 UTF-8 代码页
                simulated = true;
            }
            catch (Exception ex)
            {
                simulated = false;
                observed = "";
                Console.WriteLine($"INFO 无法把宿主输出编码切到非 UTF-8（{ex.GetType().Name}）⇒ ④ 现场复现 SKIP");
            }
            if (simulated)
            {
                try
                {
                    using var p = System.Diagnostics.Process.Start(Psi(null))!;   // 不声明解码编码 = 旧写法
                    observed = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                }
                finally { Console.OutputEncoding = saved; }
                Check(observed != ExpectedOut,
                    $"现场复现：宿主输出编码非 UTF-8 且未声明解码编码 ⇒ 乱码（实得「{observed}」）");
            }
            // 用 CodePage 比较：Console.OutputEncoding 返回的 UTF8Encoding 实例带控制台默认回退字符，
            // 与 Encoding.UTF8 静态实例的 EncoderFallback 不同 ⇒ Encoding.Equals 会假红。
            Check(Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage,
                $"探针自身仍以 UTF-8 输出（否则门禁日志自身会乱码；实为 {Console.OutputEncoding.WebName}/{Console.OutputEncoding.CodePage}）");
        }

    }
}
