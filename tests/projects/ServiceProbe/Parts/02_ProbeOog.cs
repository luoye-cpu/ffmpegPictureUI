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
        // ───────────────────── 越界带：内核 vs zimg 逐块对表 ─────────────────────

        /// <summary>
        /// `oog` —— 把**同一份** P3 编码值的小图分别交给 ①ffmpeg zscale（色度学参照）与
        /// ②引擎自研 H1 内核（<see cref="FfmpegGui.Services.ColorMapping.RawColorPipeline"/>），
        /// 逐块比 16-bit 输出。动机：产物级 A/B（`verify-decision-delivery` ⑬）只能给出聚合 PSNR，
        /// 而两路都过 `-d 2.4` 有损 ⇒ 聚合数被**共享量化噪声**吃掉，分不清是算子差异还是编码噪声。
        /// 这里在**进编码器之前**比，噪声不进账。
        /// ⚠ 判据在无损通道（16-bit PNG + rawvideo）上成立，不依赖任何有损编码。
        /// </summary>
        private static async Task ProbeOog()
        {
            const string dir = "tests/output/validate";
            Directory.CreateDirectory(dir);
            var ff = AAS.Current.FfmpegPath;
            if (string.IsNullOrEmpty(ff) || !File.Exists(ff)) { Skip("oog：ffmpeg 未定位"); return; }

            // 8 个纯色块，每块 8x8 像素；值取在 **P3 编码域**的 16-bit 整数上（含大量 sRGB 越界色）
            int[][] blocks = new[]
            {
                new[] { 65535, 0, 0 }, new[] { 0, 65535, 0 }, new[] { 0, 0, 65535 },
                new[] { 65535, 0, 65535 }, new[] { 0, 65535, 65535 }, new[] { 65535, 65535, 0 },
                new[] { 30000, 5000, 60000 }, new[] { 5000, 15000, 58000 },
            };
            const int W = 8 * 8, H = 8;
            var raw = new byte[W * H * 6];
            for (int b = 0; b < blocks.Length; b++)
                for (int y = 0; y < H; y++)
                    for (int x = b * 8; x < b * 8 + 8; x++)
                    {
                        int o = (y * W + x) * 6;
                        for (int c = 0; c < 3; c++)
                        {
                            ushort v = (ushort)blocks[b][c];
                            raw[o + c * 2] = (byte)(v & 0xFF);            // rgb48le = 小端
                            raw[o + c * 2 + 1] = (byte)(v >> 8);
                        }
                    }
            string gridRaw = $"{dir}/oog_grid.raw", gridPng = $"{dir}/oog_grid.png";
            File.WriteAllBytes(gridRaw, raw);
            RunCap(ff, $"-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb48le -video_size {W}x{H} "
                     + $"-i \"{gridRaw}\" -update 1 \"{gridPng}\"");
            Check(File.Exists(gridPng) && new FileInfo(gridPng).Length > 100, $"越界网格输入就绪（{W}x{H}, 8 块）");

            // ① 参照 = zimg：与 legacy 路**逐字相同**的那条链（产物级已证 legacy 逐比特等于它）
            string refRaw = $"{dir}/oog_zimg.raw";
            RunCap(ff, $"-y -hide_banner -loglevel error -i \"{gridPng}\" "
                     + $"-vf \"format=rgb48le,zscale=pin=smpte432:tin=iec61966-2-1:min=gbr:p=bt709:t=iec61966-2-1:m=bt709\" "
                     + $"-f rawvideo -pix_fmt rgb48le -update 1 \"{refRaw}\"");

            // ② 被测 = 引擎内核：显式源=Display P3、目标=sRGB、16-bit 出口、GMO 关闭（默认契约）
            var srcD = CS.FromNamedSpace("Display P3");
            var dstD = CS.FromCicp("bt709", "iec61966-2-1", "sRGB");
            if (srcD == null || dstD == null) { Check(false, "oog：描述符构造失败（P3 或 sRGB 取不到）"); return; }
            var it = new CI { Strategy = ST.Recommended, Source = srcD, Target = dstD,
                              Format = CE.CapsFor("png"), TargetBitDepth = 16, TargetExplicit = true,
                              AllowApproximateCurve = false };
            var plan = CE.Plan(it);
            Check(plan.Action == CA.Map && plan.Backend == CB.InProcess,
                  $"oog：该用例应走 H1 进程内映射（实为 {plan.Action}/{plan.Backend}｜{plan.Reason}）");
            var spec = plan.ToSpec();
            Check(!spec.GamutMap, $"oog：GMO 必须关（默认契约=硬裁切），实为 GamutMap={spec.GamutMap}");

            string kernPng = $"{dir}/oog_h1.png", kernRaw = $"{dir}/oog_h1.raw";
            if (File.Exists(kernPng)) File.Delete(kernPng);
            await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                gridPng, kernPng, spec, plan, null, default, 2);
            Check(File.Exists(kernPng) && new FileInfo(kernPng).Length > 100, "oog：内核产出非空 PNG");
            RunCap(ff, $"-y -hide_banner -loglevel error -i \"{kernPng}\" "
                     + $"-f rawvideo -pix_fmt rgb48le -update 1 \"{kernRaw}\"");

            // ③ 逐块对表
            var a = ReadU16Le(refRaw); var k = ReadU16Le(kernRaw);
            Check(a.Length == k.Length && a.Length == W * H * 3, $"两侧样本数一致（zimg={a.Length} h1={k.Length}）");
            if (a.Length != k.Length || a.Length == 0) return;
            int worst = 0, worstBlock = -1, worstCh = -1;
            var perBlock = new int[blocks.Length];
            for (int i = 0; i < a.Length; i += 3)
            {
                int px = i / 3; int blk = px % blocks.Length;
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(a[i + c] - k[i + c]);
                    if (d > worst) { worst = d; worstBlock = blk; worstCh = c; }
                    perBlock[blk] = Math.Max(perBlock[blk], d);
                }
            }
            var sb = new System.Text.StringBuilder("逐块最大|Δ|（16-bit 单位）：");
            for (int b2 = 0; b2 < perBlock.Length; b2++)
                sb.Append($" 块{b2}={perBlock[b2]}");
            Console.WriteLine("    " + sb);
            Check(worst <= 128,
                  $"越界带：H1 内核与 zimg 参照逐块一致（容差 128/65535 ≈ 0.5/255，实差最大 {worst}"
                  + $" = {Math.Round(worst / 257.0, 1)}/255，出现在块 {worstBlock} 的通道 {new[] { 'R', 'G', 'B' }[Math.Max(worstCh, 0)]}，"
                  + $"源值={blocks[Math.Max(worstBlock, 0)][Math.Max(worstCh, 0)]}）｜{sb}");
        }

    }
}
