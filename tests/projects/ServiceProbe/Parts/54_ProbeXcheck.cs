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
        /// xcheck —— 引擎与外部参照（libzimg/zscale）对同一图做同一转换，逐像素比对。
        /// 这是“自研精度可信”的硬证据：PSNR(16bit) 与峰值 ΔE。
        /// 用法：ServiceProbe xcheck &lt;input&gt; &lt;srcPrim&gt; &lt;srcTrc&gt; &lt;dstPrim&gt; &lt;dstTrc&gt; [outDir]
        /// </summary>
        private static async System.Threading.Tasks.Task ProbeXcheck(string[] args)
        {
            if (args.Length < 6)
            {
                Console.WriteLine("usage: ServiceProbe xcheck <input> <srcPrim> <srcTrc> <dstPrim> <dstTrc> [outDir]");
                _fail++; return;
            }
            string input = args[1];
            var (w, h) = await FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(input).ConfigureAwait(false);
            if (w <= 0) { Check(false, "无法探测尺寸"); return; }
            var outDir = args.Length >= 7 ? args[6] : Path.Combine(Path.GetTempPath(), "cmsx");
            Directory.CreateDirectory(outDir);
            var spec = Spec(args[2], args[3], args[4], args[5]);

            // A) 引擎路径
            var raw = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                .DecodeToRgb48Async(input, w, h).ConfigureAwait(false);
            var raw2 = new byte[raw.Length];
            CK.TransformRgb48le(raw, raw2, spec, w * h, CK.BuildTables(spec.SrcCurve), CK.BuildTables(spec.DstCurve));
            string enginePng = Path.Combine(outDir, "engine.png");
            await FfmpegGui.Services.ColorMapping.RawColorPipeline.WriteRgb48ToPngAsync(
                raw2, w, h, enginePng, args[4], args[5], null).ConfigureAwait(false);

            // B) 参照路径（zimg 自己完成同样的 primaries/曲线转换）
            //    ⚠️ 必须像生产代码那样在 **-i 之前**声明输入色彩（-color_primaries/-color_trc）：
            //    源文件无 CICP 时（lavfi 产出的 PNG 为 gbr/unknown/unknown），只写 zscale 的 tin=
            //    会被当 unknown 处理（实测参照几乎不变，差仅 244/65535），导致“参照失效”而误判引擎错。
            string mTok = args[4] == "bt2020" ? "bt2020nc" : "bt709";
            // ⚠ 参照臂的 trc 口径修正（F-9 后续，2026-10-11）：zimg 把 `t=bt709` 实现成**纯幂 γ(1/2.4)**，
            //   而 CICP/标签 `bt709` 指的是 ITU-R BT.709-6 的**分段定义式** ⇒ 拿 zimg 的 `t=bt709` 当参照，
            //   等于拿"另一个实现的说法"当标准，那 4 格豁免就是这么来的。
            //   现在参照臂 = `zscale t=linear`（色度学仍纯 zimg）+ `lutrgb`=定义式 OETF，
            //   常数逐字取自本机 `tools/src/dng_sdk/.../dng_color_space.h:134-140`
            //   （α=1.0992968268094429、β=0.0180539685108078、趾段 4.5、γ=0.45）⇒ **不走本仓曲线代码**，
            //   参照的独立性保留（只是换掉那个已知非标准的实现细节）。
            string refT = args[5] == "bt709" ? "linear" : args[5];
            const string DefOetf = "floor(maxval*if(lt(val/maxval\\,0.0180539685108078)\\,4.5*val/maxval\\,1.0992968268094429*pow(val/maxval\\,0.45)-0.0992968268094429)+0.5)";
            string refTail = args[5] == "bt709"
                ? $",lutrgb=r='{DefOetf}':g='{DefOetf}':b='{DefOetf}'" : "";
            if (args[5] == "bt709") Console.WriteLine("  参照臂：zscale t=linear + lutrgb=BT.709-6 定义式（DNG SDK 常数）");
            // 入口侧同形修（2026-10-11 续）：zimg 把 `tin=bt709` 也实现成纯幂 γ(1/2.4) 解码，
            //   ⇒ `BT.2020→sRGB` 两格的分歧在**入口反解**，不是引擎错。
            //   参照臂改为「自己按 BT.709-6 定义式反解成线性 + 告诉 zscale tin=linear」，
            //   反解式与常数同上（编码域拐点 4.5×β=0.0812428582986351、α−1=0.0992968268094429）。
            const string DefInv = "maxval*if(lt(val/maxval\\,0.0812428582986351)\\,(val/maxval)/4.5\\,pow(((val/maxval)+0.0992968268094429)/1.0992968268094429\\,1/0.45))";
            string refTin = args[3] == "bt709" ? "linear" : args[3];
            string refPre = args[3] == "bt709"
                ? $"format=rgb48le,lutrgb=r='{DefInv}':g='{DefInv}':b='{DefInv}',format=gbrpf32le,"
                : "format=gbrpf32le,";
            if (args[3] == "bt709") Console.WriteLine("  参照臂：入口按 BT.709-6 定义式反解(lutrgb) + zscale tin=linear");
            string zimgPng = Path.Combine(outDir, "zimg.png");
            var ff = AppSettingsService.Current.FfmpegPath;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ff,
                Arguments = $"-y -hide_banner -loglevel error -color_primaries {args[2]} -color_trc {args[3]} -i \"{input}\" "
                  + $"-vf \"{refPre}zscale=pin={args[2]}:tin={refTin}:min=gbr:rin=pc:p={args[4]}:t={refT}:m={mTok}:r=pc,format=rgb48le{refTail}\" \"{zimgPng}\"",
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (p == null) { Check(false, "无法启动 ffmpeg"); return; }
                string err = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await p.WaitForExitAsync().ConfigureAwait(false);
                if (p.ExitCode != 0) { Check(false, $"zscale 参照路径失败: {err.Trim()}"); return; }
            }

            // C) 比对：两路均写成 16bit PNG → 各自解回 rgb48 再比
            var a = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(enginePng, w, h).ConfigureAwait(false);
            var b = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(zimgPng, w, h).ConfigureAwait(false);
            double psnr = CM.Psnr16(a, b);
            int maxDiff = 0, deSamples = 0; double worstDeltaE = 0, sumDeltaE = 0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i + 6 <= n; i += 6)
            {
                int r1 = a[i] | (a[i + 1] << 8), g1 = a[i + 2] | (a[i + 3] << 8), b1 = a[i + 4] | (a[i + 5] << 8);
                int r2 = b[i] | (b[i + 1] << 8), g2 = b[i + 2] | (b[i + 3] << 8), b2 = b[i + 4] | (b[i + 5] << 8);
                maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(r1 - r2), Math.Max(Math.Abs(g1 - g2), Math.Abs(b1 - b2))));
                if ((i / 6) % 53 == 0)   // 抽样算 ΔE（8bit 口径，与业界一致）
                {
                    double de = CM.DeltaE76((byte)(r1 >> 8), (byte)(g1 >> 8), (byte)(b1 >> 8),
                                             (byte)(r2 >> 8), (byte)(g2 >> 8), (byte)(b2 >> 8));
                    worstDeltaE = Math.Max(worstDeltaE, de); sumDeltaE += de; deSamples++;
                }
            }
            Console.WriteLine($"xcheck {Path.GetFileName(input)} {args[2]}/{args[3]}→{args[4]}/{args[5]}  {w}x{h}");
            Console.WriteLine($"  PSNR(16bit)={psnr:F2}dB  maxDiff={maxDiff}/65535  ΔE max={worstDeltaE:F3} avg={(deSamples > 0 ? sumDeltaE / deSamples : 0):F4} (样本 {deSamples})");
            Check(psnr >= 55, $"引擎 vs zimg PSNR ≥ 55dB（实 {psnr:F1}）");
            Check(maxDiff <= 1500, $"最大单通道差异 ≤ 1500/65535（实 {maxDiff}）");
            Check(worstDeltaE <= 1.5, $"峰值 ΔE*ab ≤ 1.5（实 {worstDeltaE:F3}）");
        }

        /// <summary>行主序 3x3 乘法（仅探针内部用于互逆自洽校验）。</summary>
        private static double[] Mul9(double[] a, double[] b)
        {
            var o = new double[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    o[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
            return o;
        }

    }
}
