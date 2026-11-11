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
        // ═══════════════════════════════════════════════════════════════
        //  gmgamut —— GainMap 出口的「源传递特性 / 底图原色」接线锁（2026-10-28 两条缺陷同批）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// ServiceProbe gmgamut [outDir]
        /// <para>臂① **源传递特性必须被命名**：未标注素材（容器读不到 trc）在旧实现里让 zscale 报
        /// code 3074 后落「直解」兜底 ⇒ 把 gamma 编码值当线性 ⇒ 底图二次编码提亮
        /// （纯色块实测 13→64、128→189）。现由 <see cref="GMJ.ResolveEncodeParams"/> 五级取数、
        /// <see cref="GMJ.DecodeArgsFor"/> 单点分派，兜底支已删除。</para>
        /// <para>臂② **底图原色不许按近似 token 蒙**：ProPhoto / Adobe RGB / ColorMatch 没有 CICP 命名，
        /// 探测把 primaries 归成 bt709 并登记 <c>primariesApproximated</c>；GainMap 底图**不携带源 ICC**，
        /// 把近似当真实就是「只改标签不改像素」⇒ 必须改走具名空间矩阵。</para>
        /// <para>两臂各带一条**对照**（旧串 / 旧分派必须给出不同的数），并各带一条**夹具判别力正控**
        /// （夹具若哪天被工具写了标签，本 mode 先在那里红）。</para>
        /// <para>⚠ 两把尺不许对齐：下面两个 <c>foreach</c> 里的 2 处 <c>Check</c> 调用位按 #76 的源文本
        /// 口径各算 1 条，运行时按两文件各跑一遍 ⇒ 运行时通过数是它的两倍。登记取源文本人。</para>
        /// </summary>
        private static async System.Threading.Tasks.Task ProbeGmGamut(string[] args)
        {
            const int W = 64, H = 64;
            int[] rows = { 8, 24, 40, 56 };
            int[][] bandSrc = { new[] { 13, 13, 13 }, new[] { 128, 128, 128 },
                                new[] { 200, 200, 200 }, new[] { 20, 60, 180 } };
            string ff = AppSettingsService.Current.FfmpegPath;
            string outRoot = Path.GetFullPath(args.Length >= 2 ? args[1] : Path.Combine("tests", "output", "validate"));
            string dir = Path.Combine(outRoot, "gmgamut_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Console.WriteLine($"gmgamut-out={dir}");

            // ── 夹具：四档纯色（13 / 128 / 200 中性 + 一档饱和蓝），**不带任何色彩标签** ──
            var rgb = new byte[W * H * 3];
            for (int y = 0; y < H; y++)
            {
                var v = bandSrc[Math.Min(y / (H / bandSrc.Length), bandSrc.Length - 1)];
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 3;
                    rgb[o] = (byte)v[0]; rgb[o + 1] = (byte)v[1]; rgb[o + 2] = (byte)v[2];
                }
            }
            string rawFixture = Path.Combine(dir, "blocks.rgb");
            string flatPng = Path.Combine(dir, "blocks.png");
            File.WriteAllBytes(rawFixture, rgb);
            int ecFix = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                $"-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb24 -s {W}x{H} -i \"{rawFixture}\" "
              + $"-frames:v 1 \"{flatPng}\"", null, ff).ConfigureAwait(false);
            Check(ecFix == 0 && File.Exists(flatPng),
                $"gmgamut 夹具：未标注纯色 PNG 产出（exit {ecFix}，ffmpeg={ff ?? "(null)"}）");
            if (!File.Exists(flatPng)) return;

            // 判别力正控：这张图必须**真的**命名不了源传递，且没有近似登记。
            // 若哪天工具开始给 PNG 写 gAMA/cICP，下面的全部结论都失去依据 ⇒ 必须先在这里红。
            var flatMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(flatPng);
            Check(!GMJ.IsZscaleNameableTransfer(flatMeta.colorTrc),
                $"gmgamut 夹具判别力：容器 trc 必须命名不了（实得 \"{flatMeta.colorTrc ?? "(null)"}\"）");
            Check(!flatMeta.primariesApproximated,
                $"gmgamut 夹具：未标注 PNG 不该带近似登记（实 primaries={flatMeta.colorPrimaries ?? "(null)"}）");

            // ── ① 取值表与滤镜串形态（表来自本机 ffmpeg -h filter=zscale 的 transferin 允许值）──
            var trcPos = new[] { "bt709", "iec61966-2-1", "iec61966-2-4", "linear", "bt470m", "bt470bg",
                                 "smpte170m", "smpte240m", "log100", "log316", "bt2020-10", "bt2020-12",
                                 "smpte2084", "arib-std-b67" };
            var trcNeg = new[] { "", "unknown", "unspecified", "srgb", "bt2020", "dci-p3", "gamma1.8", "prophoto" };
            Check(Array.TrueForAll(trcPos, GMJ.IsZscaleNameableTransfer),
                $"gmgamut ①：zscale 可命名的 {trcPos.Length} 项传递全部认（表与本机工具同值）");
            Check(Array.TrueForAll(trcNeg, t => !GMJ.IsZscaleNameableTransfer(t)),
                "gmgamut ①：表外取值一律判「命名不了」（unknown/unspecified/纯幂曲线/原色名都不许进 zscale）");
            string fStr = GMJ.LinearDecodeFilter(1000.0, "bt709", "iec61966-2-1");
            Check(fStr.Contains("t=linear") && fStr.Contains(":p=bt709") && fStr.Contains(":tin=iec61966-2-1")
                  && fStr.Contains(":pin=bt709") && fStr.Contains("npl=1000") && fStr.Contains("r=pc"),
                  $"gmgamut ①：线性化串同时钉住 t=linear / p / tin / pin / npl / r=pc（实得 {fStr}）");

            // 接线锁：两条执行路径只许经 DecodeArgsFor 分派，且不许再留「zscale 失败 ⇒ 直解」的兜底。
            string? repoRoot = FindRepoRoot();
            Check(repoRoot != null, "gmgamut ①：能找到仓库根（下面两处文本锁的前提）");
            if (repoRoot != null)
            {
                foreach (var f in new[]
                {
                    Path.Combine(repoRoot!, "src", "FfmpegGui", "Services", "QueueProcessor.cs"),
                    Path.Combine(repoRoot!, "src", "FfmpegGui", "Services", "ColorMapping", "RawColorPipeline.cs"),
                })
                {
                    string txt = File.ReadAllText(f);
                    string nm = Path.GetFileName(f);
                    Check(!txt.Contains("LinearDecodeArgs(") && !txt.Contains("DirectDecodeArgs(")
                          && txt.Contains("DecodeArgsFor("),
                          $"{nm} 只许经 GainMapJpegCodec.DecodeArgsFor 分派解码串（直调任一臂=第二份分派）");
                    Check(!txt.Contains("尝试直接解码"),
                          $"{nm} 不许再有「zscale 失败 ⇒ 尝试直接解码」的兜底（直解把 gamma 值当线性 ⇒ 底图提亮）");
                }
            }

            // ── ② 未标注臂：五级取数落到「sRGB 惯例假定」，分派落到 zscale 臂，像素线性化一次 ──
            var pFlat = GMJ.ResolveEncodeParams(new FfmpegGui.Models.FfmpegOptions(), flatPng);
            Check(pFlat.SrcTrc == "iec61966-2-1" && !pFlat.ManagedEotf && !pFlat.AlreadyLinear
                  && pFlat.SrcTrcSource.Contains("惯例假定"),
                  $"gmgamut ②：未标注 PNG 的 trc 取数=「sRGB 惯例假定」（trc={pFlat.SrcTrc}，来源={pFlat.SrcTrcSource}）");
            string flatRaw = Path.Combine(dir, "flat.zscale.raw");
            string flatArgs = GMJ.DecodeArgsFor(pFlat, flatPng, flatRaw);
            Check(flatArgs.Contains("zscale=t=linear") && flatArgs.Contains("tin=iec61966-2-1")
                  && flatArgs.Contains("-frames:v 1"),
                  $"gmgamut ②：未标注 PNG 的分派必须是 zscale 臂（实得 {flatArgs}）");
            int ecZ = await FfmpegGui.Services.FfmpegRunner.RunAsync(flatArgs, null, ff).ConfigureAwait(false);
            Check(ecZ == 0 && File.Exists(flatRaw),
                $"gmgamut ②：新串在真实 zscale 上跑通（exit {ecZ}）—— 修前这一条就是 code 3074");
            if (ecZ != 0 || !File.Exists(flatRaw)) return;
            var flatPix = GMJ.DecodeToRgba(pFlat, File.ReadAllBytes(flatRaw), W * H);
            float srgbRef = (float)TC.Srgb.ToLinear(128 / 255.0);
            float zGot = flatPix[(rows[1] * W + W / 2) * 4];
            Check(Math.Abs(zGot - srgbRef) < 0.005f,
                $"gmgamut ②：128 档解成线性 {zGot:F5}，与托管 sRGB 曲线参照 {srgbRef:F5} 吻合（zscale/托管两套互测）");

            // 对照臂：旧兜底（直解、不补 EOTF）必须给出**明显不同**的数 ⇒ 尺子有牙。
            string dirRaw = Path.Combine(dir, "flat.direct.raw");
            await FfmpegGui.Services.FfmpegRunner.RunAsync(GMJ.DirectDecodeArgs(flatPng, dirRaw), null, ff).ConfigureAwait(false);
            float dGot = GMJ.GbrPlanarToRgba(File.ReadAllBytes(dirRaw), W * H)[(rows[1] * W + W / 2) * 4];
            Check(dGot - srgbRef > 0.15f,
                $"gmgamut ②对照：直解不补 EOTF 时 128 档给 {dGot:F4}（编码值原样）⇒ 与线性参照差 {dGot - srgbRef:F4}"
              + "（这条差就是当年「底图被提亮」的成因，必须看得见）");

            // ── ③ ProPhoto 臂：源可度量、CICP 命名不了 ⇒ 具名空间 + 托管 EOTF ──
            string? ppIcc = IccProfileService.GetFaithfulIcc("ProPhoto RGB");
            string et = ExifToolService.DetectedPath ?? "";
            string ppPng = Path.Combine(dir, "blocks_prophoto.png");
            File.Copy(flatPng, ppPng, true);
            string etOut = "";
            bool wrote = false;
            if (ppIcc != null && et.Length > 0)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = et,
                    // ⚠ 写法必须是 `-icc_profile<=文件`：少那个 `=` 时 exiftool 报「Invalid tag name」
                    //   却**照旧退出码 0**，只吐「0 image files updated / 1 image files unchanged」
                    //   ⇒ 判据读 updated、见到 unchanged 即红（2026-10-28 本探针自己踩过一次这条）。
                    Arguments = $"-overwrite_original \"-icc_profile<={ppIcc}\" \"{ppPng}\"",
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                };
                using var pr = System.Diagnostics.Process.Start(psi)!;
                etOut = (pr.StandardOutput.ReadToEnd() + pr.StandardError.ReadToEnd())
                        .Replace("\r", " ").Replace("\n", " ").Trim();
                pr.WaitForExit(20000);
                wrote = etOut.Contains("1 image files updated") && !etOut.Contains("unchanged");
            }
            Check(ppIcc != null && et.Length > 0 && wrote,
                $"gmgamut ③：ProPhoto ICC 已写进 PNG（icc={ppIcc ?? "(null)"}，exiftool stdout={etOut}）");
            if (!wrote) return;
            var ppMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(ppPng);
            Check(string.Equals(ppMeta.sourceGamutName, "ProPhoto RGB", StringComparison.OrdinalIgnoreCase)
                  && ppMeta.primariesApproximated && ppMeta.colorPrimaries == "bt709",
                  $"gmgamut ③判别力：必须识别出 ProPhoto **并**登记「bt709 只是近似」"
                + $"（实 {ppMeta.sourceGamutName ?? "(null)"}/{ppMeta.colorPrimaries}/approx={ppMeta.primariesApproximated}）");
            var pPp = GMJ.ResolveEncodeParams(new FfmpegGui.Models.FfmpegOptions(), ppPng);
            Check(pPp.ManagedEotf && pPp.SrcCurve != null && pPp.PrimariesApproximated
                  && string.Equals(pPp.SourceSpaceName, "ProPhoto RGB", StringComparison.OrdinalIgnoreCase),
                  $"gmgamut ③：ProPhoto 落「直解 + 托管 EOTF」并交出具名空间（trc 来源={pPp.SrcTrcSource}）");
            string ppRaw = Path.Combine(dir, "pp.direct.raw");
            string ppArgs = GMJ.DecodeArgsFor(pPp, ppPng, ppRaw);
            Check(!ppArgs.Contains("zscale") && ppArgs.Contains("-pix_fmt gbrpf32le"),
                  $"gmgamut ③：托管 EOTF 臂的分派不许带 zscale（实得 {ppArgs}）");
            int ecP = await FfmpegGui.Services.FfmpegRunner.RunAsync(ppArgs, null, ff).ConfigureAwait(false);
            Check(ecP == 0 && File.Exists(ppRaw), $"gmgamut ③：上面那条分派命令实际出件裸流（exit {ecP}）");
            if (ecP != 0 || !File.Exists(ppRaw)) return;
            var ppPix = GMJ.DecodeToRgba(pPp, File.ReadAllBytes(ppRaw), W * H);
            float ppRef = (float)TC.ProPhoto.ToLinear(128 / 255.0);
            float pGot = ppPix[(rows[1] * W + W / 2) * 4];
            Check(Math.Abs(pGot - ppRef) < 0.005f,
                $"gmgamut ③：ProPhoto 的 128 档解成 {pGot:F5}，与源 ICC 曲线参照 {ppRef:F5} 吻合");
            Check(Math.Abs(pGot - srgbRef) > 0.05f,
                $"gmgamut ③对照：同一档在「拿 BT.709/sRGB 解 ProPhoto」下差 {Math.Abs(pGot - srgbRef):F4}"
              + " ⇒ 曲线错配必须看得见（近似 token 冒充真曲线就是这条的落点）");

            // ── ④ 具名空间矩阵：非空且非恒等（恒等=只改标签不改像素）──
            var ppMtx = ColorSpaceRegistry.LinearMatrix("ProPhoto RGB", "sRGB");
            Check(ppMtx != null && MaxAbsFromIdentity(ppMtx!) > 0.05,
                $"gmgamut ④：LinearMatrix(ProPhoto RGB→sRGB) 非空且偏离恒等（最大偏离 "
              + $"{(ppMtx == null ? -1 : MaxAbsFromIdentity(ppMtx)):F4}）");

            // ── ⑤ 端到端：底图字节必须真被矩阵搬过；对照臂（近似位=false，即修前）必须原地不动 ──
            var sbTrue = new StringBuilder(); var sbCtrl = new StringBuilder(); var sbBenign = new StringBuilder();
            string jpgTrue = Path.Combine(dir, "mapped.jpg");
            string jpgCtrl = Path.Combine(dir, "identity.jpg");
            string jpgBenign = Path.Combine(dir, "flat_srgb.jpg");
            bool okTrue = await GainMapEncoder.EncodeAsync((float[])ppPix.Clone(), W, H, jpgTrue,
                hdrPeakNits: GainMapEncoder.KSdrWhiteNits, baseGamut: "srgb", sourcePrimaries: "bt709",
                sourceSpaceName: "ProPhoto RGB", primariesApproximated: true,
                gainMapMetadata: "iso", log: s => sbTrue.Append(s)).ConfigureAwait(false);
            bool okCtrl = await GainMapEncoder.EncodeAsync((float[])ppPix.Clone(), W, H, jpgCtrl,
                hdrPeakNits: GainMapEncoder.KSdrWhiteNits, baseGamut: "srgb", sourcePrimaries: "bt709",
                sourceSpaceName: "ProPhoto RGB", primariesApproximated: false,
                gainMapMetadata: "iso", log: s => sbCtrl.Append(s)).ConfigureAwait(false);
            bool okBenign = await GainMapEncoder.EncodeAsync(flatPix, W, H, jpgBenign,
                hdrPeakNits: GainMapEncoder.KSdrWhiteNits, baseGamut: "srgb", sourcePrimaries: "bt709",
                gainMapMetadata: "iso", log: s => sbBenign.Append(s)).ConfigureAwait(false);
            Check(okTrue && okCtrl && okBenign && File.Exists(jpgTrue) && File.Exists(jpgCtrl) && File.Exists(jpgBenign),
                "gmgamut ⑤：三臂编码均出件（映射臂 / 对照臂 / 良性恒等臂）");
            // 零增益短路：SDR 峰值 ⇒ 产物**就是**底图 JPEG 本体 ⇒ 下面读回的字节即交付物（否则像素断言读的是空气）
            Check(sbTrue.ToString().Contains("普通 JPEG"),
                "gmgamut ⑤：SDR 输入按零增益短路产出底图本体（日志点名），像素读回的即交付的");
            var bTrue = await BandsOfAsync(jpgTrue); var bCtrl = await BandsOfAsync(jpgCtrl);
            // ⚠ 读不回像素**必须点名再停**（裸 `return` 会让整段像素断言静默缺席，pass 数照涨 ⇒ 假绿）
            Check(bTrue != null && bCtrl != null,
                "gmgamut ⑤：两臂底图都能解回 rgb24（读不回就没有像素结论可言，本 mode 到此为止并报错）");
            if (bTrue == null || bCtrl == null) return;
            string tLog = sbTrue.ToString(), cLog = sbCtrl.ToString(), nLog = sbBenign.ToString();
            int fork = Math.Abs(bTrue[3][2] - bCtrl[3][2]);
            Check(fork >= 8,
                $"gmgamut ⑤**核心**：同一份像素，近似位在案把饱和档 B 从不映射的 {bCtrl[3][2]} 搬到 {bTrue[3][2]}"
              + $"（Δ={fork}，源码值 180）⇒ 具名空间矩阵真的接在像素上");
            Check(!cLog.Contains("ProPhoto RGB → bt709"),
                "gmgamut ⑤对照：近似位关掉（修前的形状）时日志不得出现具名空间映射");
            Check(tLog.Contains("ProPhoto RGB → bt709") && !tLog.Contains("未能求出"),
                "gmgamut ⑤：映射臂日志点名「ProPhoto RGB → bt709」，且不得出现求不出矩阵的告警");
            Check(cLog.Contains("恒等") && !cLog.Contains("未能求出"),
                "gmgamut ⑤良性：bt709→sRGB 底图是同空间恒等 ⇒ 只许报恒等，不许把良性当成色偏风险告警");
            Check(nLog.Contains("恒等"),
                "gmgamut ⑤良性：未标注 sRGB 源的 srgb 底图也报恒等（默认档不再静默）");
            var sbDanger = new StringBuilder();
            string jpgDanger = Path.Combine(dir, "nomatrix.jpg");
            await GainMapEncoder.EncodeAsync((float[])ppPix.Clone(), W, H, jpgDanger,
                hdrPeakNits: GainMapEncoder.KSdrWhiteNits, baseGamut: "srgb", sourcePrimaries: "no-such-prim",
                gainMapMetadata: "iso", log: s => sbDanger.Append(s)).ConfigureAwait(false);
            Check(sbDanger.ToString().Contains("未能求出"),
                "gmgamut ⑤危险缺席：谁都命名不了的原色必须**点名**「未能求出…矩阵」（不映射只改标签是静默错色）");

            async Task<int[][]?> BandsOfAsync(string img)
            {
                string r = Path.Combine(dir, Path.GetFileNameWithoutExtension(img) + ".px.raw");
                int e = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                    $"-y -hide_banner -loglevel error -i \"{img}\" -frames:v 1 -pix_fmt rgb24 -f rawvideo \"{r}\"",
                    null, ff).ConfigureAwait(false);
                if (e != 0 || !File.Exists(r) || new FileInfo(r).Length < (long)W * H * 3)
                {
                    // ⚠ 空手必须**印两侧输入**（exit + 是否存在 + 实际字节数），否则读手静默 null = 假绿
                    Console.WriteLine($"gmgamut 读手失败: {img} → exit={e} exists={File.Exists(r)} "
                      + $"len={(File.Exists(r) ? new FileInfo(r).Length : -1)}B 需≥{(long)W * H * 3}B");
                    return null;
                }
                var d = File.ReadAllBytes(r);
                var list = new int[rows.Length][];
                for (int i = 0; i < rows.Length; i++)
                {
                    int o = (rows[i] * W + W / 2) * 3;
                    list[i] = new[] { (int)d[o], (int)d[o + 1], (int)d[o + 2] };
                }
                return list;
            }
            static double MaxAbsFromIdentity(double[] m)
            {
                double worst = 0;
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                    {
                        double id = r == c ? 1.0 : 0.0;
                        worst = Math.Max(worst, Math.Abs(m[r * 3 + c] - id));
                    }
                return worst;
            }
        }
    }
}
