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
        // ══════════════════════════════════════════════════════════════
        // pngopt <case> —— PNG 出口优化器判据宿主（png-optimizer-integration T6，契约 C-PROBE@1）
        //   六 case：pixel / ihdr / chunk / argv / off / timeout。
        //   每断言一行 `PASS|FAIL pngopt-<case>-NN <读数>`（T7 门禁按前缀逐条计数）。
        //
        //   【判据自足纪律】六个 case 的素材全部现场自造（ffmpeg lavfi / pngcicp / exiftool），
        //   不消费 args 里的 input.png 占位（args[2]/args[3] 忽略）、不引用 benchmark 轮次产物
        //   （AGENTS §3 一轮一目录隔离防假绿）；工作目录 = tests/output/pngopt-selftest/<case>-<guid8>/。
        //
        //   【进程内调用纪律】全部走 PngOptimizerService.ApplyPngOptimizerAsync 进程内直调 +
        //   内存 log lambda 收集播报——T5 实证 headless CLI 的 Info 级日志不落收尾段
        //   （OnItemCompleted 的 final FlushItemLog 早于 pngopt 收尾段）⇒ 判据不依赖 headless 日志文件。
        //   oxipng 路径由产品解析链（C-RESOLVE@1）自己定位；probe 只对惯例位
        //   publish/PLAN/artifacts/oxipng.exe（C-TOOL@1）做存在性前置——缺件打 SKIP，
        //   fail-closed（T7 把 skip>0 判 FAIL，不静默绿）。
        //
        //   【timeout case 注入方式】C-PROBE@1 把注入方式授权给 T6 实现选择，判据 =「超时播报 +
        //   产物原样」。产品侧 600s 是 public const（编译期内联，反射只读不可写），不可参数化
        //   ⇒ 注入 = 真实工具链（自造 24MP 纯噪声素材 + zopfli30 + CancellationToken.None）真跑
        //   600s 触达产品分支⑤——与 T5 冒烟实证（corpus/B04.png 6000×4000=24MP × zopfli30 =
        //   600090ms timeout，`b04-z30-dbg.log:81`）同形态；72MB 纯噪声是 zopfli 30 轮迭代的
        //   最坏输入，600s 内数学上跑不完 ⇒ 判据确定性成立。任务书备选的「反射/InternalsVisibleTo/
        //   环境变量把超时参数化」需要改产品代码（write_set 禁触 src/）且契约无此授权 ⇒ 不采用。
        //   T9② 的 ≥24MP 端到端复验另做（照片类素材），两臂互不替代。
        // ══════════════════════════════════════════════════════════════
        private static System.Threading.Tasks.Task ProbePngOpt(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法: pngopt <pixel|ihdr|chunk|argv|off|timeout> [input.png 占位, 忽略] [mode 占位, 忽略]");
                return System.Threading.Tasks.Task.CompletedTask;
            }
            switch (args[1].ToLowerInvariant())
            {
                case "pixel": PngOptCasePixel(); break;
                case "ihdr": PngOptCaseIhdr(); break;
                case "chunk": PngOptCaseChunk(); break;
                case "argv": PngOptCaseArgv(); break;
                case "off": PngOptCaseOff(); break;
                case "timeout": PngOptCaseTimeout(); break;
                default:
                    Console.WriteLine("pngopt: 未知 case（合法值 pixel/ihdr/chunk/argv/off/timeout）");
                    return System.Threading.Tasks.Task.CompletedTask;
            }
            return System.Threading.Tasks.Task.CompletedTask;

            // ── 共享 helper（局部函数：全部判据留在 ProbePngOpt 单方法体内，配合 mirror-parity 条数锁）──

            /// <summary>从 BaseDirectory 向上定位仓库根（标志 = tests/scripts/_run-step3-gates.ps1 在位）。</summary>
            string PngOptRepoRoot()
            {
                var d = new DirectoryInfo(AppContext.BaseDirectory);
                for (int up = 0; d != null && up < 12; d = d.Parent, up++)
                {
                    if (File.Exists(Path.Combine(d.FullName, "tests", "scripts", "_run-step3-gates.ps1")))
                        return d.FullName;
                }
                return Directory.GetCurrentDirectory();
            }

            /// <summary>一轮一 GUID 目录（AGENTS §3）。产物区约定：tests/output/pngopt-selftest/&lt;case&gt;-&lt;guid8&gt;/。</summary>
            string PngOptWorkDir(string caseName)
            {
                string d = Path.Combine(PngOptRepoRoot(), "tests", "output", "pngopt-selftest",
                    $"{caseName}-{Guid.NewGuid().ToString("N").Substring(0, 8)}");
                Directory.CreateDirectory(d);
                return d;
            }

            /// <summary>
            /// 工具链前置：oxipng 取惯例位（C-TOOL@1，真实执行仍由产品解析链 C-RESOLVE@1 自定位）、
            /// ffmpeg/exiftool 优先 AppSettingsService（InitExternalTools 已定位 PLAN），fallback 惯例位。
            /// </summary>
            bool PngOptToolsReady(out string oxipng, out string ffmpeg, out string exiftool)
            {
                string root = PngOptRepoRoot();
                oxipng = Path.Combine(root, "publish", "PLAN", "artifacts", "oxipng.exe");
                ffmpeg = AAS.Current.FfmpegPath ?? "";
                exiftool = AAS.Current.ExifToolPath ?? "";
                if (string.IsNullOrWhiteSpace(ffmpeg)) ffmpeg = Path.Combine(root, "publish", "PLAN", "ffmpeg-full", "ffmpeg.exe");
                if (string.IsNullOrWhiteSpace(exiftool)) exiftool = Path.Combine(root, "publish", "PLAN", "exiftool", "exiftool.exe");
                return File.Exists(oxipng) && File.Exists(ffmpeg) && File.Exists(exiftool);
            }

            /// <summary>ffmpeg lavfi 一帧 PNG 素材生成（-compression_level 0/9 按素材需要）。</summary>
            bool PngOptMakePng(string ffmpeg, string lavfi, string pixFmt, string compLevel, string outPath)
            {
                try
                {
                    RunCapture(ffmpeg, $"-y -hide_banner -loglevel error -f lavfi -i \"{lavfi}\" -frames:v 1 "
                                     + $"-pix_fmt {pixFmt} -c:v png -compression_level {compLevel} \"{outPath}\"");
                    return File.Exists(outPath) && new FileInfo(outPath).Length > 0;
                }
                catch { return false; }
            }

            /// <summary>ffmpeg -f md5 解码帧指纹（C-PIXEL@1 口径：像素逐位等价的对撞尺）。</summary>
            string PngOptFfmd5(string ffmpeg, string path)
                => RunCapture(ffmpeg, $"-v error -i \"{path}\" -f md5 -").Trim();

            /// <summary>进程内直调产品执行链（T6 纪律：不依赖 headless CLI 日志文件）。</summary>
            void PngOptRunOne(string work, string? mode, List<string> logs)
                => PngOptimizerService.ApplyPngOptimizerAsync(work, mode, s => logs.Add(s),
                       System.Threading.CancellationToken.None).GetAwaiter().GetResult();

            /// <summary>
            /// PNG chunk 结构级遍历（len+type+payload+CRC 定界）。IHDR 解析与 chunk 序列判据共用。
            /// </summary>
            List<(string Type, byte[] Payload)> PngOptReadChunks(string path)
            {
                var data = File.ReadAllBytes(path);
                var list = new List<(string, byte[])>();
                int pos = 8;
                while (pos + 12 <= data.Length)
                {
                    int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
                    string type = Encoding.ASCII.GetString(data, pos + 4, 4);
                    if (len < 0 || pos + 12 + (long)len > data.Length) break;
                    list.Add((type, data.AsSpan(pos + 8, len).ToArray()));
                    if (type == "IEND") break;
                    pos += 12 + len;
                }
                return list;
            }

            /// <summary>IHDR 四字段（规范强制第 0 个 chunk：签名 8 + len 4 + type 4，data 从 16 起）。</summary>
            (int W, int H, int BitDepth, int ColorType) PngOptReadIhdr(string path)
            {
                var data = File.ReadAllBytes(path);
                int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16));
                int h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20));
                return (w, h, data[24], data[25]);
            }

            /// <summary>
            /// iCCP payload 解出 ICC profile（R3.2「iCCP 解压数据等价」口径——oxipng 允许重压 iCCP 的
            /// zlib 层，zlib 字节可变而解压数据必须不变）。布局：profile-name\0 compression-method(0)\0 zlib-data。
            /// </summary>
            byte[] PngOptIccpProfile(byte[] iccpPayload)
            {
                int p = Array.IndexOf(iccpPayload, (byte)0);
                using var ms = new MemoryStream(iccpPayload, p + 2, iccpPayload.Length - p - 2);
                using var zs = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                zs.CopyTo(outMs);
                return outMs.ToArray();
            }

            // ── case: pixel —— 小 PNG × 三档真跑，ffmpeg -f md5 解码帧对撞输入 + 产物字节 ≤ 输入 ──
            void PngOptCasePixel()
            {
                var ready = PngOptToolsReady(out var oxipng, out var ff, out var _);
                Check(ready, $"pngopt-pixel-01 工具链齐（oxipng 惯例位 {oxipng} + ffmpeg + exiftool）");
                if (!ready) { Skip("pngopt pixel: oxipng/ffmpeg 缺件 ⇒ 判据 fail-closed（T7 判 FAIL，不静默绿）"); return; }

                string dir = PngOptWorkDir("pixel");
                string src = Path.Combine(dir, "pixel-src.png");
                bool made = PngOptMakePng(ff, "testsrc2=s=256x256:rate=1", "rgba", "9", src);
                Check(made, $"pngopt-pixel-02 素材 256×256 RGBA 8-bit 生成（{new FileInfo(src).Length} B）");
                if (!made) { Skip("pngopt pixel: 素材生成失败 ⇒ 判据 fail-closed"); return; }

                long srcLen = new FileInfo(src).Length;
                string md5Src = PngOptFfmd5(ff, src);
                Console.WriteLine($"[pngopt-pixel] 输入 md5={md5Src} bytes={srcLen}");

                int idx = 3;
                foreach (var mode in new[] { "max", "zopfli15", "zopfli30" })
                {
                    string work = Path.Combine(dir, $"pixel-{mode}.png");
                    File.Copy(src, work, true);
                    var logs = new List<string>();
                    PngOptRunOne(work, mode, logs);
                    string md5After = PngOptFfmd5(ff, work);
                    long afterLen = new FileInfo(work).Length;
                    Check(md5After == md5Src,
                        $"pngopt-pixel-{idx:00} 档位 {mode} 解码帧 ffmpeg md5 逐位等价（{md5Src} → {md5After}）");
                    idx++;
                    Check(afterLen <= srcLen,
                        $"pngopt-pixel-{idx:00} 档位 {mode} 产物字节 ≤ 输入（{srcLen} → {afterLen}）");
                    idx++;
                    Console.WriteLine($"[pngopt-pixel] {mode} 播报原文：{(logs.Count > 0 ? logs[0] : "(无)")}");
                }
            }

            // ── case: ihdr —— 8-bit RGBA 与 16-bit rgb48be × 三档，IHDR 宽高/位深/色型不变（R2.3）──
            void PngOptCaseIhdr()
            {
                var ready = PngOptToolsReady(out var _, out var ff, out var _);
                Check(ready, "pngopt-ihdr-01 工具链齐（oxipng 惯例位 + ffmpeg + exiftool）");
                if (!ready) { Skip("pngopt ihdr: oxipng/ffmpeg 缺件 ⇒ 判据 fail-closed（T7 判 FAIL，不静默绿）"); return; }

                string dir = PngOptWorkDir("ihdr");
                string src8 = Path.Combine(dir, "ihdr8-src.png");
                string src16 = Path.Combine(dir, "ihdr16-src.png");
                bool made8 = PngOptMakePng(ff, "testsrc2=s=256x256:rate=1", "rgba", "9", src8);
                bool made16 = PngOptMakePng(ff, "testsrc2=s=256x256:rate=1", "rgb48be", "9", src16);
                Check(made8, $"pngopt-ihdr-02 8-bit RGBA 素材生成（{new FileInfo(src8).Length} B）");
                Check(made16, $"pngopt-ihdr-03 16-bit rgb48be 素材生成（{new FileInfo(src16).Length} B）");
                if (!made8 || !made16) { Skip("pngopt ihdr: 素材生成失败 ⇒ 判据 fail-closed"); return; }

                int idx = 4;
                foreach (var mode in new[] { "max", "zopfli15", "zopfli30" })
                {
                    string work = Path.Combine(dir, $"ihdr8-{mode}.png");
                    File.Copy(src8, work, true);
                    PngOptRunOne(work, mode, new List<string>());
                    var ih = PngOptReadIhdr(work);
                    Check($"{ih.W}x{ih.H} bd={ih.BitDepth} ct={ih.ColorType}" == "256x256 bd=8 ct=6",
                        $"pngopt-ihdr-{idx:00} 档位 {mode} 8-bit RGBA IHDR 不变（实 {ih.W}x{ih.H} bd={ih.BitDepth} ct={ih.ColorType}）");
                    idx++;
                }
                foreach (var mode in new[] { "max", "zopfli15", "zopfli30" })
                {
                    string work = Path.Combine(dir, $"ihdr16-{mode}.png");
                    File.Copy(src16, work, true);
                    PngOptRunOne(work, mode, new List<string>());
                    var ih = PngOptReadIhdr(work);
                    Check($"{ih.W}x{ih.H} bd={ih.BitDepth} ct={ih.ColorType}" == "256x256 bd=16 ct=2",
                        $"pngopt-ihdr-{idx:00} 档位 {mode} 16-bit rgb48be IHDR 不变、不降 8-bit（实 {ih.W}x{ih.H} bd={ih.BitDepth} ct={ih.ColorType}）");
                    idx++;
                }
            }

            // ── case: chunk —— 现场构造 iCCP/cICP/eXIf（pngcicp + exiftool 五步），三档后
            //      pngcicp info 三 chunk 保留 + 非 IDAT 序列不重排 + payload 等价（R3.1/R3.2）──
            void PngOptCaseChunk()
            {
                var ready = PngOptToolsReady(out var _, out var ff, out var exiftool);
                string pngcicp = Path.Combine(PngOptRepoRoot(), "tools", "src", "pngcicp", "bin", "Release", "net11.0", "pngcicp.exe");
                // ICC 源 = 在册语料 DSC00037.jpg（AGENTS §7 fail-closed 素材；与 benchmark/T2 构造同源，536 B）
                string iccSrc = Path.Combine(PngOptRepoRoot(), "tests", "output", "corpus", "gainmap-iso21496", "DSC00037.jpg");
                bool prep = ready && File.Exists(pngcicp) && File.Exists(iccSrc);
                Check(prep, $"pngopt-chunk-01 构造链工具齐（oxipng/ffmpeg/exiftool/pngcicp/ICC 源 {iccSrc}）");
                if (!prep) { Skip("pngopt chunk: 构造链缺件 ⇒ 判据 fail-closed（T7 判 FAIL，不静默绿）"); return; }

                string dir = PngOptWorkDir("chunk");
                string iccPath = Path.Combine(dir, "embedded.icc");
                RunCapture(exiftool, $"-q -b -icc_profile \"{iccSrc}\"", iccPath);
                Check(File.Exists(iccPath) && new FileInfo(iccPath).Length > 0,
                    $"pngopt-chunk-02 ICC 抽取非空（{new FileInfo(iccPath).Length} B，exiftool -b -icc_profile）");

                // 五步构造（照 _make-png-opt-corpus.ps1 §10 逐字）：复制 → pngcicp cICP → exiftool iCCP
                // → exiftool eXIf → pngcicp info 校验（构造参数 1/13/0/1 = sRGB primaries/transfer、matrix 0、full 1）
                string baseFile = Path.Combine(dir, "chunk-meta.png");
                if (!PngOptMakePng(ff, "testsrc2=s=256x256:rate=1", "rgba", "9", baseFile))
                { Skip("pngopt chunk: 素材生成失败 ⇒ 判据 fail-closed"); return; }
                RunCapture(pngcicp, $"cicp \"{baseFile}\" \"{baseFile}\" 1 13 0 1");
                RunCapture(exiftool, $"-q \"-icc_profile<={iccPath}\" -overwrite_original \"{baseFile}\"");
                RunCapture(exiftool, $"-q \"-exififd:datetimeoriginal=2026:01:01 00:00:00\" -overwrite_original \"{baseFile}\"");
                string info0 = RunCapture(pngcicp, $"info \"{baseFile}\"");
                Check(info0.Contains("iCCP:") && info0.Contains("cICP:") && info0.Contains("eXIf:"),
                    "pngopt-chunk-03 基线件三关键 chunk 齐（pngcicp info）");
                foreach (var l in info0.Split('\n')) Console.WriteLine("[pngopt-chunk] 基线 info " + l.TrimEnd('\r'));

                var chunksBefore = PngOptReadChunks(baseFile);
                string seqBefore = string.Join(",", chunksBefore.Where(c => c.Type != "IDAT").Select(c => c.Type));
                byte[] cicpBefore = chunksBefore.First(c => c.Type == "cICP").Payload;
                byte[] exifBefore = chunksBefore.First(c => c.Type == "eXIf").Payload;
                byte[] iccpProfBefore = PngOptIccpProfile(chunksBefore.First(c => c.Type == "iCCP").Payload);
                Console.WriteLine($"[pngopt-chunk] 基线非 IDAT 序列={seqBefore} cICP={cicpBefore.Length}B eXIf={exifBefore.Length}B iCCP解压={iccpProfBefore.Length}B");

                int idx = 4;
                foreach (var mode in new[] { "max", "zopfli15", "zopfli30" })
                {
                    string work = Path.Combine(dir, $"chunk-{mode}.png");
                    File.Copy(baseFile, work, true);
                    var logs = new List<string>();
                    PngOptRunOne(work, mode, logs);
                    string info = RunCapture(pngcicp, $"info \"{work}\"");
                    Check(info.Contains("iCCP:") && info.Contains("cICP:") && info.Contains("eXIf:"),
                        $"pngopt-chunk-{idx:00} 档位 {mode} 三关键 chunk 保留（pngcicp info）");
                    idx++;
                    var chunksAfter = PngOptReadChunks(work);
                    string seqAfter = string.Join(",", chunksAfter.Where(c => c.Type != "IDAT").Select(c => c.Type));
                    Check(seqAfter == seqBefore,
                        $"pngopt-chunk-{idx:00} 档位 {mode} 非 IDAT chunk 序列不重排（{seqBefore} → {seqAfter}）");
                    idx++;
                    bool payloadEq;
                    try
                    {
                        payloadEq = chunksAfter.First(c => c.Type == "cICP").Payload.SequenceEqual(cicpBefore)
                                 && chunksAfter.First(c => c.Type == "eXIf").Payload.SequenceEqual(exifBefore)
                                 && PngOptIccpProfile(chunksAfter.First(c => c.Type == "iCCP").Payload).SequenceEqual(iccpProfBefore);
                    }
                    catch { payloadEq = false; }
                    Check(payloadEq,
                        $"pngopt-chunk-{idx:00} 档位 {mode} iCCP 解压/cICP/eXIf payload 等价（R3.2）");
                    idx++;
                    Console.WriteLine($"[pngopt-chunk] {mode} 播报原文：{(logs.Count > 0 ? logs[0] : "(无)")}");
                }
            }

            // ── case: argv —— BuildArgs 三档与 C-PARAMS@1 终值快照逐 token 对撞（快照内嵌断言）──
            void PngOptCaseArgv()
            {
                // 快照 = C-PARAMS@1 三档 argv 终值（T1 实证冻结）：公共追加保守集
                // --interlace keep --nb --nc --np --ng --out；zopfli 档 -z --zi N 与 -o max 同传。
                const string expMax = "IN.png|-o|max|--interlace|keep|--nb|--nc|--np|--ng|--out|OUT.png";
                const string expZ15 = "IN.png|-o|max|-z|--zi|15|--interlace|keep|--nb|--nc|--np|--ng|--out|OUT.png";
                const string expZ30 = "IN.png|-o|max|-z|--zi|30|--interlace|keep|--nb|--nc|--np|--ng|--out|OUT.png";
                var aMax = PngOptimizerService.BuildArgs("max", "IN.png", "OUT.png");
                Check(string.Join("|", aMax) == expMax,
                    $"pngopt-argv-01 max 档 argv 与 C-PARAMS@1 终值逐 token 一致（实 {string.Join(" ", aMax)}）");
                var aZ15 = PngOptimizerService.BuildArgs("zopfli15", "IN.png", "OUT.png");
                Check(string.Join("|", aZ15) == expZ15,
                    $"pngopt-argv-02 zopfli15 档 argv 与 C-PARAMS@1 终值逐 token 一致（实 {string.Join(" ", aZ15)}）");
                var aZ30 = PngOptimizerService.BuildArgs("zopfli30", "IN.png", "OUT.png");
                Check(string.Join("|", aZ30) == expZ30,
                    $"pngopt-argv-03 zopfli30 档 argv 与 C-PARAMS@1 终值逐 token 一致（实 {string.Join(" ", aZ30)}）");
                bool threw = false;
                try { _ = PngOptimizerService.BuildArgs("foo", "IN.png", "OUT.png"); }
                catch (ArgumentException) { threw = true; }
                Check(threw, "pngopt-argv-04 非法档位 foo 抛 ArgumentException（值域封闭负控）");
            }

            // ── case: off —— mode=off/null/空串 零进程零播报零触碰（R1.1 负控，哨兵有牙）──
            void PngOptCaseOff()
            {
                // 哨兵设计：where.exe 对任意 argv 立即 exit 1。若三形态的短路（R1.1 第一行）被改坏
                // ——off 走未知档位播报、null/空串走解析后播报——「零播报」断言当场红；
                // 若连未知档位闸也被改坏，哨兵会被当 oxipng 真启动（exit 1 ⇒ 失败播报）⇒ 同样红。双层牙。
                string sentinel = Path.Combine(Environment.SystemDirectory, "where.exe");
                Check(File.Exists(sentinel),
                    $"pngopt-off-01 哨兵 where.exe 在位（{sentinel}；被当 oxipng 启动必留 exit 1 播报痕迹）");
                if (!File.Exists(sentinel)) { Skip("pngopt off: 哨兵缺位 ⇒ fail-closed"); return; }

                var ready = PngOptToolsReady(out var _, out var ff, out var _);
                if (!ready) { Skip("pngopt off: ffmpeg 缺件（素材生成依赖）⇒ fail-closed"); return; }
                string dir = PngOptWorkDir("off");
                string src = Path.Combine(dir, "off-src.png");
                bool made = PngOptMakePng(ff, "testsrc2=s=256x256:rate=1", "rgba", "9", src);
                Check(made, $"pngopt-off-02 素材生成（{made}）");
                if (!made) { Skip("pngopt off: 素材生成失败 ⇒ fail-closed"); return; }

                var svc = AAS.Current;
                string? saved = svc.PngOptimizerPath;
                svc.PngOptimizerPath = sentinel;
                try
                {
                    int idx = 3;
                    foreach (var mode in new string?[] { "off", null, "" })
                    {
                        string label = mode == null ? "null" : mode.Length == 0 ? "空串" : mode;
                        string work = Path.Combine(dir, $"off-{idx}.png");
                        File.Copy(src, work, true);
                        string shaBefore = Sha(work);
                        var logs = new List<string>();
                        PngOptRunOne(work, mode, logs);
                        Check(logs.Count == 0,
                            $"pngopt-off-{idx:00} 档位 {label} 零播报（实 {logs.Count} 条{(logs.Count > 0 ? "：" + logs[0] : "")}）");
                        idx++;
                        Check(Sha(work) == shaBefore,
                            $"pngopt-off-{idx:00} 档位 {label} 产物逐字节不变（sha256 对撞）");
                        idx++;
                    }
                }
                finally { svc.PngOptimizerPath = string.IsNullOrEmpty(saved) ? null : saved; }
            }

            // ── case: timeout —— 24MP 纯噪声 × zopfli30 真跑 600s 触达产品分支⑤（注入方式见方法头注释）──
            void PngOptCaseTimeout()
            {
                var fld = typeof(PngOptimizerService).GetField(nameof(PngOptimizerService.TimeoutSeconds));
                int tv = fld != null && fld.GetValue(null) is int v ? v : -1;
                Check(tv == 600, $"pngopt-timeout-01 产品超时常量 == 600（实 {tv}；C-EXEC@1，const 反射只读）");
                if (tv != 600) { Skip("pngopt timeout: 超时常量漂移 ⇒ 判据前提失效，fail-closed"); return; }

                var ready = PngOptToolsReady(out var _, out var ff, out var _);
                Check(ready, "pngopt-timeout-02 工具链齐（oxipng 惯例位 + ffmpeg）");
                if (!ready) { Skip("pngopt timeout: 工具缺件 ⇒ fail-closed（T7 判 FAIL，不静默绿）"); return; }

                string dir = PngOptWorkDir("timeout");
                string src = Path.Combine(dir, "noise24mp.png");
                // 24MP（5000×4800）rgb24 纯噪声：-compression_level 0 实测 0.3s / 72,224,536 B
                //（T6 自验 _probe 轮读数）；纯噪声是 zopfli 30 轮迭代的确定性最坏输入 ⇒ 600s 内跑不完。
                bool made = PngOptMakePng(ff, "nullsrc=s=5000x4800:rate=1,noise=alls=80:allf=u", "rgb24", "0", src);
                Check(made, $"pngopt-timeout-03 24MP 噪声素材生成（{made}；{new FileInfo(src).Length} B）");
                if (!made) { Skip("pngopt timeout: 素材生成失败 ⇒ fail-closed"); return; }

                string shaBefore = Sha(src);
                var logs = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                PngOptRunOne(src, "zopfli30", logs);
                sw.Stop();
                string expected = "[pngopt] ⚠️ 优化超时（600s 上限），保留原样（档位 zopfli30；大图建议改用 max 档）";
                Check(logs.Count == 1 && logs[0] == expected,
                    $"pngopt-timeout-04 超时播报逐字 == C-EXEC@1 模板（实 {logs.Count} 条：「{(logs.Count > 0 ? logs[0] : "(无)")}」；耗时 {sw.Elapsed.TotalSeconds:F0}s）");
                Check(Sha(src) == shaBefore,
                    "pngopt-timeout-05 超时回落产物逐字节原样（sha256 对撞，R4.2）");
                var residue = Directory.GetFiles(dir, "*.pngopt-*.png");
                Check(residue.Length == 0,
                    $"pngopt-timeout-06 临时件无残留（实 {residue.Length} 个；finally 删除纪律）");
            }
        }

    }
}
