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
        // ═══════════════════ orientation：取向标签下的几何口径（显示尺寸 vs coded 尺寸）═══════════════════

        /// <summary>
        /// orientation [输出根目录] —— 带 <c>Orientation</c> 标签的输入，引擎与几何探测必须按**显示**尺寸工作。
        ///
        /// <para><b>本门禁钉住的缺陷（2026-09-30 实机确证）**：用户的 6000×4000 16-bit 无压缩 TIFF
        /// （SONY ILCE-7M2，<c>Orientation=8</c>，无 ICC）转 JXL 出"花图"。根因不在 JXL、也不在色彩：
        /// ffmpeg 对取向标签会 **autorotate**（随包 git-2026-09-26 实测：解码帧挂
        /// <c>3x3 displaymatrix rotation=90</c>，帧尺寸 4000×6000），而 <c>RawColorPipeline.ProbeSizeAsync</c>
        /// 当时读的是 <c>ffprobe stream=width,height</c> = **coded** 6000×4000 ⇒ 编码段用
        /// <c>-video_size 6000x4000</c> 去声明一段真实排布为 4000×6000 的 raw ⇒ **每行错位重排**。
        /// 实测该产物 vs 真值 <b>PSNR 7.97 dB</b>；把同一段 raw 按 4000×6000 重排再 <c>transpose=1</c>
        /// 则 <b>inf</b>（逐字节等）⇒ 错的是几何标注，不是像素处理。旧的长度守卫 <c>rawLen &lt; w*h*6</c>
        /// 对此**完全盲**（转置下字节数恒等）。</para>
        ///
        /// <para><b>夹具的三处硬自检</b>（不成立就直接判红，不许"看起来跑了"）：
        /// ① coded 尺寸必须是 64×32 而 rotation 必须是 90 —— 二者**不相等**才是有效夹具，
        ///    否则整条门禁退化成恒真（这是"锚在绝对常数上的判据要有一例把常数挪开"的同类）；
        /// ② 参照臂（ffmpeg 直解的 PNG）必须**不带** rotation side data —— 否则两臂各旋一次，
        ///    对拍的是"两次旋转是否抵消"而不是几何口径；
        /// ③ 写标签必须用 <c>exiftool -n</c>：实测 <c>-Orientation=8</c> **不带 <c>-n</c>** 会写成
        ///    <b>3</b>（PrintConv 反查失败后落默认值）⇒ 180° 不改变轴向 ⇒ 夹具静默失牙。</para>
        ///
        /// <para><b>为什么源用 64×32 而不是 1024×1024</b>：判据要的是"轴向可分辨"，
        /// 方图在转置下不可分辨（与 ① 同一道理）；小图让整条门禁跑在秒级。</para>
        /// </summary>
        private static async System.Threading.Tasks.Task ProbeOrientation(string[] args)
        {
            string ff = AppSettingsService.Current.FfmpegPath;
            string ffp = AppSettingsService.Current.FfprobePath;
            string exif = FfmpegGui.Services.ExifToolService.DetectedPath ?? "";
            if (string.IsNullOrWhiteSpace(ff) || !File.Exists(ff))
            { Check(false, "orientation：未找到 ffmpeg（AppSettings 未初始化 / publish/PLAN 缺失）"); return; }
            if (string.IsNullOrWhiteSpace(ffp) || !File.Exists(ffp))
            { Check(false, "orientation：未找到 ffprobe ⇒ 夹具前提无法自检"); return; }
            if (string.IsNullOrWhiteSpace(exif) || !File.Exists(exif))
            { Check(false, "orientation：未检测到 exiftool ⇒ 无法造取向夹具（显式判红，不静默跳过）"); return; }

            string root = args.Length >= 2 ? args[1] : "tests/output/validate";
            // 运行级隔离（GUID）：固定目录会被并发同名实例互删 ⇒ 假红（TESTING §6 第 66 条）
            string dir = Path.Combine(root, "orient_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Console.WriteLine($"orient-out={dir}");
            var created = new List<string>();

            // 独立进程读数（**不复用** ImageGeometry 的解析器 ⇒ 本门禁是产品代码的对照臂）
            async Task<string> Cap(string exe, string a)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, a)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                })!;
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();     // P2-3：两路都要排空
                await p.WaitForExitAsync().ConfigureAwait(false);
                return (await so.ConfigureAwait(false) + await se.ConfigureAwait(false)).Trim();
            }
            async Task Run(string exe, string a) { await Cap(exe, a).ConfigureAwait(false); }
            // 探测参数一律取自 ImageGeometry.BuildProbeArguments（**单一真值**）。此前这两条命令是手写的
            // （`-show_entries stream=width,height` 与 `frame_side_data_list` 各写一份），产品侧 2026-11-02
            // 给它补上 `stream_side_data` 之后探针没跟上 ⇒ 这一臂看不到 Frame Cropping，只能读到 coded 尺寸
            // （docs/HANDOVER-2026-11-02.md §7 第 6 项）。**参数同源、解析自写**：本探针是产品解析器的对照臂，
            // 复用 ParseDisplaySize 会让两臂一起错（口径隔离正是本门禁存在的理由）。
            string ProbeArgs(string path) => FfmpegGui.Services.ImageGeometry.BuildProbeArguments(path);
            // coded 尺寸：ffprobe 的 stream 段（**旧口径**，本门禁专门拿它当"错的那把尺"）
            (int w, int h) Coded(string path)
            {
                string json = Cap(ffp, ProbeArgs(path)).GetAwaiter().GetResult();
                var m = System.Text.RegularExpressions.Regex.Match(json,
                    @" ""width""\s*:\s*(\d+)\s*,\s*""height""\s*:\s*(\d+)");
                return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
            }
            int Rotation(string path)
            {
                string json = Cap(ffp, ProbeArgs(path)).GetAwaiter().GetResult();
                var m = System.Text.RegularExpressions.Regex.Match(json, @" ""rotation""\s*:\s*(-?\d+)");
                return m.Success ? int.Parse(m.Groups[1].Value) : 0;
            }
            // 像素对照：两臂同格式 → ffmpeg 自带 psnr（与本仓 PsnrCalculator 无关 ⇒ 独立参照）
            // ⚠ 取不到数字时**必须报出原因**（变异实测：几何口径退回 coded 后两臂尺寸不同 ⇒
            //   ffmpeg 直接拒绝，读数会是空的；只写"无读数"就等于把最有信息量的一条证据丢掉）
            string Psnr(string a, string b, string fmt)
            {
                string outp = Cap(ff, $"-hide_banner -i \"{a}\" -i \"{b}\" "
                            + $"-lavfi \"[0:v]format={fmt}[x];[1:v]format={fmt}[y];[x][y]psnr\" -f null -")
                          .GetAwaiter().GetResult();
                var m = System.Text.RegularExpressions.Regex.Match(outp, @"average:(inf|[0-9][0-9.]*)");
                if (m.Success) return m.Groups[1].Value;
                var w = System.Text.RegularExpressions.Regex.Match(outp,
                    @"(Width and height of input videos must be same|Invalid data|No such file)");
                return w.Success ? $"图不可比:{w.Groups[1].Value}" : "无读数";
            }
            // 夹具：lavfi 生成的**非对称**图（testsrc2 含渐变+文字，行/列内容互不相同 ⇒ 剪切可测）
            async Task<string> MakeTiff(string name, string pix, int orientation)
            {
                var p = Path.Combine(dir, name);
                await Run(ff, $"-y -hide_banner -loglevel error -f lavfi "
                            + $"-i \"testsrc2=size=64x32:duration=1\" -frames:v 1 -pix_fmt {pix} \"{p}\"")
                          .ConfigureAwait(false);
                created.Add(p);
                if (orientation != 0)
                    await Run(exif, $"-q -n -overwrite_original \"-Orientation={orientation}\" \"{p}\"")
                          .ConfigureAwait(false);
                return p;
            }
            async Task<(FfmpegGui.Services.ColorMapping.RawColorPipeline.Stats st, int w, int h)> EngineRun(
                string src, string outPath, int bitDepth, int maxDim)
            {
                var o = new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "png", ColorStrategy = ST.Recommended, BitDepth = bitDepth,
                    EnableMaxDimension = maxDim > 0, MaxDimension = maxDim > 0 ? maxDim : 0,
                };
                var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, src, out var why);
                if (it == null) throw new InvalidOperationException($"选项→意图失败：{why}");
                var plan = CE.Plan(it);
                if (!plan.IsOk) throw new InvalidOperationException($"计划被拒：{plan.Reason}");
                var st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                    src, outPath, plan.ToSpec(), plan, null, default, 1, ff, o).ConfigureAwait(false);
                var (w, h) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                    .ProbeSizeAsync(outPath, ff).ConfigureAwait(false);
                return (st, w, h);
            }

            try
            {
                // ── 夹具 + 前提自检 ──
                string src16 = await MakeTiff("o16_orient8.tif", "rgb48le", 8).ConfigureAwait(false);
                string src8  = await MakeTiff("o8_orient8.tif", "rgb24", 8).ConfigureAwait(false);
                string srcNR = await MakeTiff("o16_norot.tif", "rgb48le", 0).ConfigureAwait(false);
                var (cw, ch) = Coded(src16);
                int rot = Rotation(src16);
                var (dw, dh) = (ch, cw);   // 期望显示尺寸 = coded 交换（rotation=90）
                Console.WriteLine($"orient-fixture coded={cw}x{ch} rotation={rot} displayExpect={dw}x{dh}");
                Check(cw == 64 && ch == 32, $"夹具 coded = 64x32（实 {cw}x{ch}）");
                Check(rot == 90,
                    $"夹具 rotation = 90（实 {rot}）⇒ 取向标签真的会被 autorotate；"
                  + $"若为 0/-180 说明 exiftool 少了 -n（实测会静默写成 3 ⇒ 轴向不变 ⇒ 门禁失牙）");
                Check(cw != dw && ch != dh, "夹具前提：coded 与显示尺寸**轴向不同**（相同则该夹具恒真、测不到本缺陷）");

                // ── 取数口径（HANDOVER-2026-11-02 §7 第 6 项）：参数同源 + 同一份字节上两把尺都看得到 ──
                string args16 = ProbeArgs(src16);
                Check(args16.Contains("stream_side_data") && args16.Contains("frame_side_data_list")
                      && args16.Contains("-read_intervals"),
                    $"取向探针的 ffprobe 参数取自 ImageGeometry.BuildProbeArguments（stream_side_data / "
                  + $"frame_side_data_list / -read_intervals 三要素齐；实得「{args16}」）—— 手写清单会与产品侧漂开："
                  + $"2026-11-02 就是这里少了 stream_side_data ⇒ 看不到 Frame Cropping");
                var (vuW, vuH, cuX, cuY) = FfmpegGui.Services.ImageGeometry.ParseDisplaySizeWithCrop(
                    Cap(ffp, args16).GetAwaiter().GetResult());
                Check(vuW == dw && vuH == dh && cuX == 0 && cuY == 0,
                    $"同一份 JSON 上产品解析器给可见区 {dw}x{dh}（实 {vuW}x{vuH}，裁切 {cuX}x{cuY}）—— "
                  + $"本夹具没有容器级 clap，报出非零裁切就是凭空扣减（会把真像素裁掉）");
                var (nrW, nrH) = (Coded(srcNR).w, Coded(srcNR).h);
                Check(Rotation(srcNR) == 0 && nrW == 64 && nrH == 32,
                    $"负控夹具无取向标签（rotation={Rotation(srcNR)}，coded={nrW}x{nrH}）");

                // ── 探测口径本身：显示尺寸必须与 coded 不同 ──
                var (pw, ph) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                    .ProbeSizeAsync(src16, ff).ConfigureAwait(false);
                Console.WriteLine($"orient-probe coded={cw}x{ch} probe(显示)={pw}x{ph}");
                Check(pw == dw && ph == dh,
                    $"ProbeSizeAsync 报**显示**尺寸 {dw}x{dh}（实 {pw}x{ph}；旧实现报 coded {cw}x{ch}）");

                // ── O1：16-bit 取向源 → 引擎产出必须是 32x64 且与独立参照逐字节等 ──
                // 参照 = ffmpeg 直解（同样吃 autorotate），不经本仓任何代码 ⇒ 两臂只可能差在几何标注。
                string ref16 = Path.Combine(dir, "ref16.png"); created.Add(ref16);
                await Run(ff, $"-y -hide_banner -loglevel error -i \"{src16}\" -frames:v 1 "
                            + $"-pix_fmt rgb48le -compression_level 0 \"{ref16}\"").ConfigureAwait(false);
                var (rcw, rch) = Coded(ref16);
                Check(rcw == dw && rch == dh && Rotation(ref16) == 0,
                    $"参照臂前提：ffmpeg 直解产物 coded={rcw}x{rch}（期望 {dw}x{dh}）且**不带** rotation"
                  + $"（实 {Rotation(ref16)}）⇒ 对拍的是几何口径，不是「两次旋转是否抵消」");
                string e1 = Path.Combine(dir, "O1_engine16.png"); created.Add(e1);
                var s1 = await EngineRun(src16, e1, 16, 0).ConfigureAwait(false);
                string p1 = Psnr(e1, ref16, "gbrp16le");
                Console.WriteLine($"orient-case id=O1-16bit expect={dw}x{dh} engine={s1.w}x{s1.h} "
                                + $"stats={s1.st.Width}x{s1.st.Height} psnr={p1}");
                Check(s1.w == dw && s1.h == dh, $"O1 引擎产出尺寸 = {dw}x{dh}（实 {s1.w}x{s1.h}）");
                Check(s1.st.Width == s1.w && s1.st.Height == s1.h,
                    $"O1 Stats 与实际产物一致（{s1.st.Width}x{s1.st.Height} vs {s1.w}x{s1.h}）");
                Check(p1 == "inf",
                    $"O1 像素与独立参照**逐字节等**（psnr={p1}）⇒ 不是「接近」，是没有任何行错位；"
                  + $"缺陷未修时本条实测读数为「图不可比:Width and height of input videos must be same」"
                  + $"（两臂轴向不同），用户侧产物 vs 真值则是 7.97dB 量级");

                // ── O2：8-bit 出口（16→8 降位由引擎内核做，允许 ±1 LSB 舍入差）──
                string ref8 = Path.Combine(dir, "ref8.png"); created.Add(ref8);
                await Run(ff, $"-y -hide_banner -loglevel error -i \"{src8}\" -frames:v 1 "
                            + $"-pix_fmt rgb24 -compression_level 0 \"{ref8}\"").ConfigureAwait(false);
                string e2 = Path.Combine(dir, "O2_engine8.png"); created.Add(e2);
                var s2 = await EngineRun(src8, e2, 8, 0).ConfigureAwait(false);
                string p2 = Psnr(e2, ref8, "gbrp");
                double p2v = double.TryParse(p2, out var v2) ? v2 : (p2 == "inf" ? 999 : -1);
                Console.WriteLine($"orient-case id=O2-8bit expect={dw}x{dh} engine={s2.w}x{s2.h} psnr={p2}");
                Check(s2.w == dw && s2.h == dh, $"O2 引擎产出尺寸 = {dw}x{dh}（实 {s2.w}x{s2.h}）");
                Check(p2v >= 45.0, $"O2 8-bit 出口 psnr ≥45dB（实 {p2}）⇒ 几何正确（剪切会掉到个位数 dB）");

                // ── O3：取向源 + 最长边限制 ⇒ 滤镜必须锁**显示**口径的长边（纵向分支）──
                string filter3 = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(
                    new FfmpegGui.Models.FfmpegOptions
                    { Format = "png", EnableMaxDimension = true, MaxDimension = 16 }, src16) ?? "";
                string e3 = Path.Combine(dir, "O3_engine_max16.png"); created.Add(e3);
                var s3 = await EngineRun(src16, e3, 16, 16).ConfigureAwait(false);
                Console.WriteLine($"orient-case id=O3-maxdim filter=\"{filter3}\" expect=8x16 "
                                + $"engine={s3.w}x{s3.h} stats={s3.st.Width}x{s3.st.Height}");
                Check(filter3.Contains("-2:16", StringComparison.Ordinal),
                    $"O3 滤镜锁的是**纵向**长边（scale=-2:16，实得 \"{filter3}\"）；"
                  + $"按 coded 64x32 判会选成 scale=16:-2 ⇒ 最长边突破用户上限");
                Check(s3.w == 8 && s3.h == 16, $"O3 引擎产出 = 8x16（实 {s3.w}x{s3.h}）");
                Check(s3.st.Width == s3.w && s3.st.Height == s3.h,
                    $"O3 Stats 与实际产物一致（{s3.st.Width}x{s3.st.Height} vs {s3.w}x{s3.h}）");

                // ── O4 负控：无取向标签的同一张图 ⇒ 不许凭空旋转 ──
                string e4 = Path.Combine(dir, "O4_engine_norot.png"); created.Add(e4);
                var s4 = await EngineRun(srcNR, e4, 16, 0).ConfigureAwait(false);
                Console.WriteLine($"orient-case id=O4-negative expect=64x32 engine={s4.w}x{s4.h}");
                Check(s4.w == 64 && s4.h == 32,
                    $"O4 负控：无取向标签 ⇒ 产出保持 64x32（实 {s4.w}x{s4.h}）；"
                  + $"若变成 32x64 说明探测在凭空施加旋转");
            }
            catch (Exception ex)
            {
                Check(false, $"orientation 探针异常：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                int total = 0;
                foreach (var p in created) { try { if (File.Exists(p)) total++; } catch { } }
                int removed = 0;
                foreach (var p in created) { try { if (File.Exists(p)) { File.Delete(p); removed++; } } catch { } }
                bool dirGone = false;
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    { Directory.Delete(dir); dirGone = true; }
                }
                catch { }
                Console.WriteLine($"orient-cleanup: 删除文件 {removed}/{total}，目录{(dirGone ? "已" : "未")}删除");
            }
        }


    }
}
