using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tests.Shared;
using FfmpegGui.Services;

namespace Tests.GainMapDecode
{
    /// <summary>
    /// 子系统测试组：**gainmap-decode** —— GainMap/Ultra HDR 的**解码闭环**、**方向标签（Orientation）**
    /// 与 **PSNR 位深/域**。
    ///
    /// <para><b>来源</b>：2026-10-05 从 <c>tests/GainMapTestHost/Program.cs</c>（1726 行单文件宿主）
    /// 物理拆出。方法体**逐字复制**自该宿主（门禁 <c>verify-gainmap-host.ps1</c> 正在 grep 这些文本）。</para>
    ///
    /// <para><b>覆盖的 mode</b>：<c>gainmap-decode</c>（见 <c>TestGroups</c>）。</para>
    ///
    /// <para><b>⚠ 编码→解码 依赖的处理方式 = 方案 (a)：消费方自造夹具</b></para>
    /// <para>旧宿主里 B 段（解码闭环）读的是 A 段写出的 <c>outDir\{name}.jpg</c>；直接照搬会让本组
    /// <b>依赖另一个进程先跑过</b>，而已有的失败形态是 <c>Check($"解码闭环 {name}: 输入缺失", false)</c>
    /// —— 它在"没人跑过编码"时会把 4 条<b>纯环境问题</b>判成解码缺陷（假红），而人看到红的第一反应是去改解码器。
    /// ⇒ 本组用<b>自带夹具</b>：<c>EnsureEncodedFixtureAsync</c> 调<b>同一个产品编码器</b>
    /// （<c>GainMapEncoder.EncodeAsync</c>，参数与 A 段<b>逐字相同</b>）现造一份到<b>本组自己的目录</b>，
    /// 再走原本的"输入缺失 ⇒ 判红"路径。这样：</para>
    /// <list type="bullet">
    /// <item>本组可独立跑、可与 gainmap-encode 任意顺序跑，不再有跨进程前置；</item>
    /// <item><b>编码器真失败时仍然判红且不静默</b>：夹具造不出 ⇒ <c>EnsureEncodedFixtureAsync</c> 返回 false
    ///   ⇒ 原样的 <c>Check("…: 输入缺失", false)</c> 命中 ⇒ 退出码非 0 ⇒ 绝不会 vacuous-pass；</item>
    /// <item>夹具参数与 A 段同源，所以 B 段的 PSNR 阈值（30dB）等读数口径不变。</item>
    /// </list>
    /// <para>⚠ 刻意的<b>唯一</b>行为差异（必须记账）：旧宿主下 B 段测的是"<b>A 段刚写出的那个文件</b>"；
    /// 本组测的是"<b>本进程按同样参数现造的文件</b>"。两者只有在"同一份输入 + 同一份编码器 + 确定性编码"
    /// 时逐字节同一 —— 本仓编码链是确定性的，且测试用的 <c>hdrPeak/多通道/downsample</c> 与 A 段完全一致。
    /// 这不是"放宽判据"，是把<b>隐式跨进程耦合</b>换成<b>显式同参夹具</b>。</para>
    /// </summary>
    internal static partial class GainMapDecodeGroup
    {
        /// <summary>
        /// 本组独占的产物目录（旧宿主是共用的 <c>tests/output/gainmap</c>）。
        /// <para>⚠ 分目录的理由：拆分后若三组共写一个目录，同名件互相覆盖 ⇒ 「这份文件是谁产出的」不可追溯。</para>
        /// </summary>
        internal static string OutputDir
            => Path.Combine(Environment.CurrentDirectory, "tests", "output", "gainmap-decode");

        // ⚠ <c>Check(name, ok)</c> / <c>Known(...)</c> 与旧宿主同形的适配器定义在
        //   `GainMapDecodeGroupD.cs`（同一 partial 类）—— **只此一份**，见那里的注释。

        internal static int KnownCount => Harness.KnownCount;
        internal static System.Collections.Generic.IReadOnlyList<string> KnownList => Harness.KnownList;

        internal static void RunDecode() => ProbeDecode().GetAwaiter().GetResult();

        // ═══════════════════════════════════════════════════════════════════
        //  本组入口：旧宿主 Main 的 B 段 + E 段 + D 段里属于"解码/像素内核"的那些
        // ═══════════════════════════════════════════════════════════════════

        private static async Task ProbeDecode()
        {
            var outDir = OutputDir;
            Directory.CreateDirectory(outDir);

            // ── B. 解码闭环（夹具自带：见类注释的"方案 (a)"）──
            Console.WriteLine("═══ B. 解码闭环验证 ═══");
            foreach (var (name, peak) in new[] { ("sdr", 203f), ("hdr1000", 1000f), ("hdr1000rgb", 1000f), ("hdr4000", 4000f) })
            {
                // ⚠ 参数与旧宿主 A 段的同名格**逐字相同**（sdr/hdr1000/hdr1000rgb/hdr4000）。
                await EnsureEncodedFixtureAsync(outDir, name, peak, multiChannel: name == "hdr1000rgb", downsample: 4);
                await DecodeRoundTripAsync(outDir, name, peak);
            }

            // ── E. 带方向标签 (Orientation) 的输入 —— 显示尺寸口径回归 ──
            Console.WriteLine();
            Console.WriteLine("═══ E. 方向标签 (Orientation=8) 显示尺寸回归 ═══");
            await OrientedGainMapDecodeTestAsync(outDir);
            await OrientedQualityAnalysisTestAsync(outDir);

            // ── D 段里属于像素内核 / PSNR / 线程 / UI 死锁的那些（旧宿主由 PngCicpTestAsync 链式触发）──
            Console.WriteLine();
            Console.WriteLine("═══ D. 像素内核 / PSNR / 线程 / UI 死锁 ═══");
            await RunPsnrCalculatorAsync(outDir);
        }

        /// <summary>
        /// 旧宿主 <c>PngCicpTestAsync</c> 的**尾部**（D15–D22）+ 其链尾（D23–D32）。
        /// <para>⚠ 旧宿主是 <c>PngCicpTestAsync → PsnrCalculatorTestAsync → SimdPixelOpsTestAsync →
        /// PsnrBitDepthDomainTestAsync</c> 一条链。拆分后 <c>PngCicpTestAsync</c>（PNG 3.0 chunk）归
        /// <c>gainmap-struct</c>，其余归本组；<c>PsnrBitDepthDomainTestAsync</c>（D33–D38，位深/目标域）
        /// 归 <c>gainmap-encode</c> 并在那里被**显式点名调用**（它原属"编码产物"读数族）。
        /// ⚠ <c>AdaptiveThreadTest</c> 在旧链里被调用**两次**（PngCicp 尾一次、SimdPixelOps 尾一次）
        /// ⇒ 本组也调用两次，读数条数才对得上（少一次就是 5 条断言凭空消失）。</para>
        /// </summary>
        private static async Task PngCicpTailAsync(string outDir)
        {
            // ── ⚠ 拆分引入的**进程内顺序依赖**：先预热 RawService 的 dngtool 探测 ──
            // 旧宿主 D 段里，D13（RAW 媒体信息）先跑 ⇒ `MediaInfoService` 内部调
            // `RawService.Detect()` 把 `_detectedPath` 填上；随后 D16a-c（`CaptureDngArgs` →
            // `RawService.EncodeToDngAsync`）才走 `IsDngTool`。
            // ⚠ `IsDngTool` 是**纯字段读**（`RawService.cs:19`），它**不会**触发 `Detect()`；
            //   而 `EncodeToDngAsync` 开头正是 `if (!IsDngTool) return false;`（`RawService.cs:215`）。
            // ⇒ 拆分后 D13 归了 gainmap-struct，本组若不预热就**没有任何东西**去填 `_detectedPath`
            //   ⇒ D16a-c 稳定三红，且读数里 `[D16] 单线程 0.0s`（`TimeDngEncode` 秒回）
            //   —— 原因是**拆分**（D13 被搬走）、不是产品缺陷，属"拆分引入的假红"。
            // ⇒ 这里显式点名预热（判据仍是原来的 D16a-c，未放宽、未删除）。
            _ = RawService.IsAvailable;   // = Detect()，填 `_detectedPath`
            Console.WriteLine($"      [预热] dngtool 定位: {RawService.DetectedPath ?? "(未找到)"} (IsDngTool={RawService.IsDngTool})");

            // ── UI 死锁回归 (2026-08-14): UI 线程同步调用色彩探测不应卡死 ──
            // 复现方式: 用 UI 同步上下文 (DispatcherSynchronizationContext) 模拟 Avalonia,
            // 在"UI 线程"同步调用 FfmpegCommandBuilder.BuildArguments (内部走色彩探测)。
            // 修复前: ReadColorTagsAsync 捕获上下文 → 续体回 UI 线程 → 死锁。
            // 修复后: Task.Run + ConfigureAwait(false) → 正常返回。
            await UiDeadlockTestAsync(outDir);

            // ── RAW 线程参数传递 (2026-08-15): UI 线程选项 → dngtool -threads ──
            // 单线程 (1) 与多线程 (8) 应产生不同命令且都含 -threads
            var t1 = CaptureDngArgs(1);
            var t8 = CaptureDngArgs(8);
            Check("D16a 单线程参数", t1.Contains("-threads 1"));
            Check("D16b 多线程参数", t8.Contains("-threads 8"));
            Check("D16c 参数区分", t1 != t8);
            // 实际耗时对比 (effort=1 小样本, 避免长时间等待)
            var th1 = await TimeDngEncode(1);
            var th8 = await TimeDngEncode(8);
            Console.WriteLine($"      [D16] 单线程 {th1:F1}s vs 多线程 {th8:F1}s");
            Check("D16d 多线程不慢于单线程", th8 <= th1 * 2.5 + 2);

            // ── 自适应线程分配逻辑 (2026-08-15) ──
            AdaptiveThreadTest();

            // ── 软件完整链路: AutoThreads 标志 → 队列并发 → 每任务线程覆盖 (2026-08-15) ──
            // 模拟 QueueProcessor: 任务执行前若 AutoThreads → Threads = ComputeAdaptiveThreads(并发)
            var chainCases = new (int concurrency, int expectThreads)[]
            {
                (1, Environment.ProcessorCount),
                (2, Math.Max(1, Environment.ProcessorCount / 2)),
                (4, Math.Max(1, Environment.ProcessorCount / 4)),
                (Environment.ProcessorCount, 1),
                (100, 1)
            };
            foreach (var (conc, expect) in chainCases)
            {
                var opts = new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "jpg", AutoThreads = true, Threads = 16   // 基准值, 应被覆盖
                };
                // 模拟 QueueProcessor 分配
                opts.Threads = FfmpegGui.Models.FfmpegOptions.ComputeAdaptiveThreads(conc);
                Check($"D18 并发={conc} → 每任务 {opts.Threads} (期望 {expect})", opts.Threads == expect);
            }
            // 手动模式: AutoThreads=false → Threads 不被覆盖
            var manual = new FfmpegGui.Models.FfmpegOptions { Format = "jpg", AutoThreads = false, Threads = 8 };
            Check("D19 手动模式不覆盖 (8)", manual.Threads == 8);
            // 单线程: AutoThreads=false + Threads=1
            var singleT = new FfmpegGui.Models.FfmpegOptions { Format = "jpg", AutoThreads = false, Threads = 1 };
            Check("D20 单线程=1", singleT.Threads == 1);

            // ── Photoshop 检测 (2026-08-15, PsRenderService) ──
            PsRenderService.ClearCache();
            var psAvailable = PsRenderService.Detect();
            if (psAvailable)
                Console.WriteLine($"      [D22] Photoshop: {PsRenderService.DetectedPath}");
            // 本机有 PS 应检测到; 无 PS 环境跳过 (不视为失败)
            var hasPs = psAvailable || !File.Exists(@"C:\Program Files\Adobe\Adobe Photoshop 2026\Photoshop.exe");
            Check("D22 PS 检测", hasPs || !OperatingSystem.IsWindows());

            // ── .NET 原生 PSNR (2026-08-15, PsnrCalculator 替代 ffmpeg psnr filter) ──
            await PsnrCalculatorTestAsync(outDir);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  以下为 tests/GainMapTestHost/Program.cs 的原文
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 自带夹具（方案 (a)）：按 A 段**逐字相同**的参数现造 <c>{name}.jpg</c> 到本组目录。
        /// <para>返回值<b>不参与判据</b>：造不出 ⇒ 后续 <c>DecodeRoundTripAsync</c> 里原有的
        /// 「输入缺失」判红会命中（fail-loud，绝不 vacuous-pass）。这里的 <c>Console.WriteLine</c>
        /// 只是让"为什么红"可复盘。</para>
        /// </summary>
        private static async Task EnsureEncodedFixtureAsync(string outDir, string name, float hdrPeak, bool multiChannel, int downsample)
        {
            var outPath = Path.Combine(outDir, $"{name}.jpg");
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
            {
                Console.WriteLine($"\n── 夹具(gainmap-decode): 复用已有 {name}.jpg（{new FileInfo(outPath).Length} 字节）──");
                return;
            }
            Console.WriteLine($"\n── 夹具(gainmap-decode): 现造 {name}.jpg（peak={hdrPeak}, multiChannel={multiChannel}, ds={downsample}）──");
            try
            {
                const int w = 256, h = 192;
                var pixels = CreateHdrTestPixels(w, h, hdrPeak);
                var gmLog = new System.Text.StringBuilder();
                var ok = await GainMapEncoder.EncodeAsync(
                    pixels, w, h, outPath,
                    hdrPeakNits: hdrPeak, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
                    multiChannel: multiChannel,
                    baseQuality: 1.5f, gainMapQuality: 1.5f,
                    downsample: downsample,
                    log: s => { gmLog.Append(s); Console.Write(s); });
                Console.WriteLine($"  夹具结果: ok={ok}, 存在={File.Exists(outPath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  夹具异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════
        //  合成 HDR 测试像素
        // ═══════════════════════════════════════════════
        /// <summary>
        /// 生成 HDR 线性测试图 (输入约定: 1.0 = 峰值亮度, zscale npl 语义):
        ///  - 背景渐变 (SDR 区域 ≤ 白点/峰值)
        ///  - 中央高光方块 (峰值=1.0 → 测试 headroom)
        ///  - 彩色方块 (测试色度)
        /// 注意: 像素值是"峰值空间"相对值 (1.0=峰值), EncodeAsync 内部会
        ///       按 ×(hdrPeak/sdrWhite) 转换到 SDR 白点空间做色调映射。
        /// <para>⚠ 本组**必须自带**这份夹具：B 段的解码闭环要把「解码像素」与
        /// 「<b>同一函数</b>生成的编码输入」逐像素对比；若与 gainmap-encode 各写一份，
        /// 两边一旦漂移，PSNR 读数就会变成"两份夹具之差"而不是"编解码误差"。
        /// 故此处与服务端 A 段**逐字相同**（本仓对这种"同一常数两处实现"的漂移有前车之鉴）。</para>
        /// </summary>
        private static float[] CreateHdrTestPixels(int w, int h, float hdrPeakNits)
        {
            var px = new float[w * h * 4];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    float u = (float)x / w, v = (float)y / h;

                    // 背景: 亮度渐变 (相对峰值 0.01..0.2, 对应 SDR 低-中亮区)
                    float bgLum = 0.02f + 0.18f * u * (1f - v * 0.5f);
                    float r = bgLum, g = bgLum * 0.9f, b = bgLum * 1.1f;

                    // 中央高光块 (中心 1/4 区域): 峰值亮度 (1.0 = 峰值)
                    bool inHighlight = Math.Abs(x - w / 2f) < w / 8f && Math.Abs(y - h / 2f) < h / 8f;
                    if (inHighlight)
                    {
                        // 高斯衰减的高光: 中心 = 峰值 (1.0)
                        float dx = (x - w / 2f) / (w / 8f);
                        float dy = (y - h / 2f) / (h / 8f);
                        float fall = MathF.Exp(-(dx * dx + dy * dy) * 2f);
                        float lum = 0.5f + 0.5f * fall;
                        r = lum;
                        g = lum * 0.98f;
                        b = lum * 0.96f;
                    }

                    // 右下彩色块 (红色, 0.25 = 1.2x SDR 白点相对 1000nits 峰值)
                    // 注意: 避免极端饱和色 (JPEG YUV 色度重采样 overshoot → clip)
                    if (x > w * 0.7f && y > h * 0.7f)
                    {
                        r = 0.25f; g = 0.10f; b = 0.08f;
                    }
                    // 左下蓝色块 (SDR, 温和饱和度)
                    if (x < w * 0.2f && y > h * 0.7f)
                    {
                        r = 0.06f; g = 0.08f; b = 0.14f;
                    }

                    px[i + 0] = r;
                    px[i + 1] = g;
                    px[i + 2] = b;
                    px[i + 3] = 1f;
                }
            }
            return px;
        }

        // ═══════════════════════════════════════════════
        //  解码闭环: 编码产物 → 解码 → 像素对比
        // ═══════════════════════════════════════════════
        private static async Task DecodeRoundTripAsync(string outDir, string name, float hdrPeak)
        {
            Console.WriteLine($"\n── 解码闭环 {name} (peak={hdrPeak}) ──");
            var jpg = Path.Combine(outDir, $"{name}.jpg");
            if (!File.Exists(jpg))
            {
                Check($"解码闭环 {name}: 输入缺失", false);
                return;
            }

            var rawOut = Path.Combine(outDir, $"{name}_decoded.rgba");
            // ⚠ 先清掉**上一轮遗留**的同名产物再判：本目录是跨轮复用的固定路径，
            //   留着旧文件会让「解码器有没有写出增益图」这一判据两头都不可信 ——
            //   正向断言会因残留而**假绿**，负向断言会因残留而**假红**
            //   （实测 `sdr_decoded.rgba` 是 09-19 那批"SDR 也写假增益图"时期的遗留物，
            //   09-20 裁定后产品不再写它，而文件还在 ⇒ 负断言当场被它判红）。
            try { if (File.Exists(rawOut)) File.Delete(rawOut); } catch { }
            try
            {
                // ── 零余量（SDR）格：产物按裁定是**普通 JPEG** ⇒ 闭环要比的不是"HDR 值对不对"，
                //   而是解码器**不许把普通 JPEG 当成有增益图**（编侧不写、解侧不猜，两侧同一契约）。
                if (hdrPeak <= GainMapEncoder.KSdrWhiteNits)
                {
                    var plain = await GainMapDecoder.DecodeToLinearRawAsync(jpg, rawOut,
                        log: s => Console.Write(s));
                    Check($"{name}: 零余量产物被解码器判为**非 UltraHDR**（不凭空造增益），实得 w={plain?.w ?? 0}",
                        plain == null && !File.Exists(rawOut));
                    return;
                }

                // ── 手动对照: ffmpeg 解码基础图 (整个文件) ──
                var ffmpeg = AppSettingsService.Current.FfmpegPath;
                var manualBase = Path.Combine(Path.GetTempPath(), $"gm_manual_{name}.rgba");
                var psiM = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{jpg}\" -pix_fmt gbrpf32le -f rawvideo \"{manualBase}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var pM = System.Diagnostics.Process.Start(psiM))
                {
                    if (pM == null) { Check($"解码闭环 {name}: ffmpeg 进程启动失败", false); return; }
                    pM.OutputDataReceived += (_, e) => { };
                    pM.ErrorDataReceived += (_, e) => { };
                    pM.BeginOutputReadLine();
                    pM.BeginErrorReadLine();
                    await pM.WaitForExitAsync();
                }
                if (File.Exists(manualBase) && new FileInfo(manualBase).Length >= 256 * 192 * 12)
                {
                    var mb = await File.ReadAllBytesAsync(manualBase);
                    int idx = (96 * 256 + 128);
                    var gM = BitConverter.ToSingle(mb, idx * 4);
                    var bM = BitConverter.ToSingle(mb, (256 * 192) * 4 + idx * 4);
                    var rM = BitConverter.ToSingle(mb, (256 * 192 * 2) * 4 + idx * 4);
                    Console.WriteLine($"  手动基础图中心: sRGB(R={rM:F3},G={gM:F3},B={bM:F3}) (应≈0.98)");
                }

                var result = await GainMapDecoder.DecodeToLinearRawAsync(jpg, rawOut,
                    log: s => Console.Write(s));
                Check($"{name}: 解码成功 ({result?.w}x{result?.h})", result != null && File.Exists(rawOut));
                if (result == null || !File.Exists(rawOut)) return;

                // 读取解码像素并与原始输入对比
                var w = result.Value.w; var h = result.Value.h;
                var bytes = await File.ReadAllBytesAsync(rawOut);
                Check($"{name}: 像素数据完整 ({bytes.Length} vs 需 {w * h * 12})", bytes.Length >= w * h * 12);

                // gbrpf32le 平面 → 交错 RGBA float (与解码器输出一致: 1.0 = SDR 白点)
                // 对比: 编码输入 1.0 = 峰值, 解码输出 1.0 = SDR 白点
                // → 参考 = 编码输入 × (峰值 / SDR白点)
                var src = CreateHdrTestPixels(w, h, hdrPeak);
                float scale = hdrPeak / GainMapEncoder.KSdrWhiteNits;
                var decoded = new float[w * h * 4];
                // 平面顺序 G, B, R (gbrpf32le)
                var g = new float[w * h]; var b = new float[w * h]; var r = new float[w * h];
                Buffer.BlockCopy(bytes, 0, g, 0, w * h * 4);
                Buffer.BlockCopy(bytes, w * h * 4, b, 0, w * h * 4);
                Buffer.BlockCopy(bytes, w * h * 8, r, 0, w * h * 4);

                // PSNR 对比 (仅有效区域)
                double mse = 0; double maxErr = 0; int count = 0;
                for (int i = 0; i < w * h; i++)
                {
                    // 解码输出: 1.0 = SDR 白点; 编码输入: 1.0 = 峰值
                    // 统一到 SDR 白点空间
                    float srcR = src[i * 4 + 0] * scale;
                    float srcG = src[i * 4 + 1] * scale;
                    float srcB = src[i * 4 + 2] * scale;
                    float dR = r[i] - srcR, dG = g[i] - srcG, dB = b[i] - srcB;
                    mse += dR * dR + dG * dG + dB * dB;
                    var me = Math.Max(Math.Abs(dR), Math.Max(Math.Abs(dG), Math.Abs(dB)));
                    if (me > maxErr) maxErr = me;
                    count += 3;
                }
                mse /= count;
                // ⚠ PSNR 的峰值参考必须是**对比信号的实际满量程**：两侧已换算到“1.0 = SDR 白”域，
                // 而 HDR 参考的峰值是 scale = hdrPeak/203（如 4000nit → 19.7）。
                // 旧写法固定用 1.0 作 peak ⇒ 大 headroom 下 PSNR 被系统性低估 20·log10(scale)
                // （hdr4000 低估 25.9dB、hdr1000 低估 13.9dB）——“7.66dB 质量缺陷”实为度量口径错。
                var psnr = mse > 0 ? 10 * Math.Log10((double)scale * scale / mse) : 99;
                Console.WriteLine($"  {name}: 参考满量程 peak={scale:F3}（峰值参考已修正）");
                // 阈值说明（均基于**已修正的峰值参考**）：
                //  - 统一取 30dB 作为“往返无明显失真”的工程下限，不再按 headroom 分档给不同阈值。
                //  - 实测：sdr 46.4 / hdr1000 36.5 / hdr4000 33.6 dB（headroom 越大、增益图 8bit 量化误差越大，单调吻合）。
                //  - 旧注释里“hdr1000 ~20dB、hdr4000 ~15dB 为合理水平”是用错峰值参考量出来的，已作废。
                var threshold = 30.0;
                if (psnr >= threshold) Check($"{name}: PSNR={psnr:F2}dB (阈 {threshold}dB)", true);
                else Check($"{name}: PSNR={psnr:F2}dB (阈 {threshold}dB)", false);
                Console.WriteLine($"  {name}: maxErr={maxErr:F4}, PSNR={psnr:F2}dB");

                // 采样点调试 (中心高光 / 背景 / 彩色块)
                foreach (var (px, py, tag) in new[] {
                    (w/2, h/2, "中心高光"), (w/4, h/4, "背景"), (w*8/10, h*8/10, "红色块"), (w/10, h*8/10, "蓝色块") })
                {
                    int i = py * w + px;
                    Console.WriteLine($"    {tag}({px},{py}): 参考=({src[i*4]*scale:F3},{src[i*4+1]*scale:F3},{src[i*4+2]*scale:F3}) " +
                        $"解码=({r[i]:F3},{g[i]:F3},{b[i]:F3})");
                }
            }
            catch (Exception ex)
            {
                Check($"{name}: 解码异常 {ex.Message}", false);
            }
        }

        // ═══════════════════════════════════════════════
        //  E1. GainMap 解码路径 × Orientation（显示尺寸口径）
        // ═══════════════════════════════════════════════
        /// <summary>
        /// E1: 底图带 Orientation=8 的 Ultra HDR JPEG → GainMapDecoder.DecodeToLinearRawAsync
        /// 必须报**显示尺寸**（宽高互换），且线性产物与 ffmpeg 独立参照逐像素一致。
        /// </summary>
        private static async Task OrientedGainMapDecodeTestAsync(string outDir)
        {
            Console.WriteLine("\n── E1 GainMap 解码 × Orientation=8 ──");
            var ffmpeg = AppSettingsService.Current.FfmpegPath;
            var ffprobe = FindFfprobeForTest(ffmpeg);
            var exif = ExifToolService.DetectedPath;
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg) || ffprobe == null || exif == null)
            {
                Check($"E1 工具链齐备 (ffmpeg={ffmpeg != null}, ffprobe={ffprobe != null}, exiftool={exif != null})", false);
                return;
            }

            // 夹具几何：coded **横向** 256x192（w≠h），Orientation=8 ⇒ 显示应为 192x256（纵向）。
            // 内容刻意做成「行内容 ≠ 列内容」（竖条 + 横带 + 角块 + 棋盘波纹），剪切/转置才可辨。
            const int codedW = 256, codedH = 192;
            const int dispW = codedH, dispH = codedW;
            const float hdrPeak = 1000f;
            var fixturePixels = CreateOrientedFixturePixels(codedW, codedH);

            var plainJpg = Path.Combine(outDir, "e1_oriented_plain.jpg");
            var encLog = new System.Text.StringBuilder();
            var encOk = await GainMapEncoder.EncodeAsync(
                fixturePixels, codedW, codedH, plainJpg,
                hdrPeakNits: hdrPeak, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
                multiChannel: false, baseQuality: 1.5f, gainMapQuality: 1.5f, downsample: 4,
                log: s => encLog.Append(s));
            Check($"E1a 夹具: Ultra HDR 编码完成 (coded {codedW}x{codedH})", encOk && File.Exists(plainJpg));
            if (!encOk || !File.Exists(plainJpg)) return;

            // ── 打取向标签 ──
            // ⚠ exiftool 陷阱（2026-09-30 实测）：`-Orientation=8` **不带 `-n`** 会按字符串查表 ⇒ 静默写成 **3**
            //   （180° ⇒ 宽高不互换）⇒ 夹具当场失去牙。故这里一律带 `-n`，且**回读标签** + 用 ffprobe
            //   复核「coded 与 display 确实互换」两件事，而不是相信写入调用成功。
            var jpg = Path.Combine(outDir, "e1_oriented.jpg");
            File.Copy(plainJpg, jpg, true);
            var stampExit = await RunExifToolAsync($"-overwrite_original -n -Orientation=8 \"{jpg}\"", exif);
            var orientTag = await ReadOrientationNumericAsync(jpg, exif);
            var mp2AfterStamp = await ExifToolService.GetTagAsync(jpg, "MPImage2");
            var codedProbe = await ProbeCodedSizeForTestAsync(jpg, ffprobe);
            var dispProbe = await ImageGeometry.DisplaySizeAsync(jpg, ffprobe, s => Console.Write(s));
            Check($"E1b 夹具取向有牙: exiftool 回读 Orientation={orientTag} (退出码 {stampExit}) ⇒ coded {codedProbe.w}x{codedProbe.h} / display {dispProbe.w}x{dispProbe.h}（期望 8 / {codedW}x{codedH} / {dispW}x{dispH}）",
                stampExit == 0 && orientTag == "8"
                && codedProbe.w == codedW && codedProbe.h == codedH
                && dispProbe.w == dispW && dispProbe.h == dispH);
            // 增益图结构必须在 exiftool 重写 APP1 之后仍然可读（否则 E1 测的就不是 Ultra HDR 解码路径了）。
            Check($"E1c 打标签后增益图结构仍在 (MPImage2 {(mp2AfterStamp?.Length ?? 0)} 字节)", !string.IsNullOrWhiteSpace(mp2AfterStamp));

            // ── 独立参照：三条命令全部只走 ffmpeg，不碰任何产品实现 ──
            var refAuto = Path.Combine(outDir, "e1_ref_autorotate.raw");
            var refT2 = Path.Combine(outDir, "e1_ref_transpose2.raw");
            var refCoded = Path.Combine(outDir, "e1_ref_noautorotate.raw");
            foreach (var f in new[] { refAuto, refT2, refCoded })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            int exA = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -i \"{jpg}\" -pix_fmt gbrpf32le -f rawvideo \"{refAuto}\"", null, ffmpeg);
            int exB = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -noautorotate -i \"{jpg}\" -vf transpose=2 -pix_fmt gbrpf32le -f rawvideo \"{refT2}\"", null, ffmpeg);
            int exC = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -noautorotate -i \"{jpg}\" -pix_fmt gbrpf32le -f rawvideo \"{refCoded}\"", null, ffmpeg);
            long needDisp = (long)dispW * dispH * 12, needCoded = (long)codedW * codedH * 12;
            bool rotDirectionPinned = exA == 0 && exB == 0 && File.Exists(refAuto) && File.Exists(refT2)
                && new FileInfo(refAuto).Length == needDisp
                && new FileInfo(refAuto).Length == new FileInfo(refT2).Length
                && FixedTimeBytesEqual(await File.ReadAllBytesAsync(refAuto), await File.ReadAllBytesAsync(refT2));
            long refAutoLen = File.Exists(refAuto) ? new FileInfo(refAuto).Length : 0;
            Check($"E1d ffmpeg 参照: 默认解码=显示几何 {refAutoLen}B 且与 `-noautorotate + transpose=2` **逐字节相同**（取向 8 ⇒ rotation 90 的方向由实测钉死）",
                rotDirectionPinned);
            Check($"E1e 夹具 coded 帧（-noautorotate）与旋正帧**不是**同一份字节（否则夹具无轴可换）",
                exC == 0 && new FileInfo(refCoded).Length == needCoded && refCoded.Length > 0
                && !FixedTimeBytesEqual(ReadFirstK(await File.ReadAllBytesAsync(refCoded), 4096),
                                        ReadFirstK(await File.ReadAllBytesAsync(refAuto), 4096)));

            // ── 被测：产品解码路径 ──
            var rawOut = Path.Combine(outDir, "e1_oriented_decoded.rgba");
            try { if (File.Exists(rawOut)) File.Delete(rawOut); } catch { }   // 跨轮复用目录：残留会制造假绿
            var res = await GainMapDecoder.DecodeToLinearRawAsync(jpg, rawOut, log: s => Console.Write(s));
            Check($"E1f GainMapDecoder 报**显示**尺寸：期望 {dispW}x{dispH}，实得 {res?.w ?? 0}x{res?.h ?? 0}（coded={codedW}x{codedH}）",
                res != null && res.Value.w == dispW && res.Value.h == dispH);
            if (res == null || !File.Exists(rawOut))
            {
                // 走到这里说明产品对"带取向标签的 Ultra HDR"直接解码失败 ⇒ 不是跳过，而是显式判红并说明。
                Check($"E1g 解码器对取向 Ultra HDR 产出线性像素（实得 {(res == null ? "null（解码失败）" : "有尺寸但无文件")}）；" +
                    "注：本用例刻意让整图压在 SDR 白点以下 ⇒ 增益图恒 1x，若仍解不出即路径缺陷", false);
                return;
            }

            var outBytes = await File.ReadAllBytesAsync(rawOut);
            var refBytes = await File.ReadAllBytesAsync(refAuto);
            Check($"E1h 线性产物长度 = 显示尺寸×12B（实得 {outBytes.Length}，需 {needDisp}）", outBytes.Length >= needDisp);

            // 参照 = 对 ffmpeg 旋正帧做**教科书 sRGB EOTF**（独立实现，不抄 SimdPixelOps，避免"把被测实现抄进参照"）。
            // 夹具刻意压在 SDR 白点以下 ⇒ GainMapEncoder 的分段 Reinhard 直通 ⇒ 增益图恒 1x，
            // 于是「EOTF(ffmpeg 底图帧)」与产品线性输出应当**逐像素相等**（只剩 8bit 量化残差）。
            // 峰值参考取 1.0 = SDR 白点（产物约定），夹具最大线性值 ≈0.98 ⇒ 满量程=1。
            int n = dispW * dispH;
            var prod = new[] { PlaneFloats(outBytes, 0, n, false), PlaneFloats(outBytes, 1, n, false), PlaneFloats(outBytes, 2, n, false) };
            var refr = new[] { PlaneFloats(refBytes, 0, n, true), PlaneFloats(refBytes, 1, n, true), PlaneFloats(refBytes, 2, n, true) };
            double mseOk = 0, mseSheared = 0;
            for (int c = 0; c < 3; c++)
            {
                var a = prod[c]; var b = refr[c];
                for (int i = 0; i < n; i++)
                {
                    double d = a[i] - b[i]; mseOk += d * d;
                    // 已知坏读数：字节不变、按 **coded** 步长重排（复现"声明未旋几何 ⇒ 每行错位"的缺陷）
                    int si = (i / codedW) * dispW + (i % codedW);
                    double ds = a[i] - b[si]; mseSheared += ds * ds;
                }
            }
            mseOk /= 3.0 * n; mseSheared /= 3.0 * n;
            double psnrOk = mseOk > 0 ? 10 * Math.Log10(1.0 / mseOk) : double.PositiveInfinity;
            double psnrSheared = mseSheared > 0 ? 10 * Math.Log10(1.0 / mseSheared) : double.PositiveInfinity;
            Console.WriteLine($"  E1: 按显示几何 PSNR={psnrOk:F2}dB, 按 coded 步长重排 PSNR={psnrSheared:F2}dB");
            Check($"E1i 线性产物 vs ffmpeg 独立参照（显示几何）PSNR={psnrOk:F2}dB ≥ 40dB", psnrOk >= 40);
            // 本条是**判据自身的牙齿**：若夹具能被转置洗白（对称/均匀），这条会当场红。
            Check($"E1j 有牙自证: 同一份参照按 coded 步长重排后 PSNR={psnrSheared:F2}dB 必须 <20dB 且比正确读数差 >20dB",
                psnrSheared < 20 && psnrOk - psnrSheared > 20);
        }

        // ═══════════════════════════════════════════════
        //  E2. 质量分析服务 × Orientation（分辨率预检不得误拒）
        // ═══════════════════════════════════════════════
        /// <summary>
        /// E2: 源带 Orientation=8、产物无方向标签 ⇒ QualityAnalysisService.AnalyzeAsync
        /// 不得以「分辨率不一致」拒绝；无损产物必须读出 inf。
        /// <para>动机（2026-09-30 缺陷）：预检曾读 **coded** 尺寸 ⇒ 对旋正后的正确产物报
        /// 「源图 (6000x4000) 与输出图 (4000x6000) 分辨率不一致」而拒绝对拍。现在预检走
        /// <see cref="ImageGeometry"/>（显示口径）。本用例同时留一条**反证臂**（E2g1/E2g2）：产物轴向
        /// 真的不一致时预检必须仍然拒绝 —— 否则"修好"可能只是把闸门拆了。</para>
        /// </summary>
        private static async Task OrientedQualityAnalysisTestAsync(string outDir)
        {
            Console.WriteLine("\n── E2 质量分析 × Orientation=8 ──");
            var ffmpeg = AppSettingsService.Current.FfmpegPath;
            var ffprobe = FindFfprobeForTest(ffmpeg);
            var exif = ExifToolService.DetectedPath;
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg) || ffprobe == null || exif == null)
            {
                Check("E2 工具链齐备 (ffmpeg/ffprobe/exiftool)", false);
                return;
            }

            const int codedW = 64, codedH = 32;      // coded 横向；Orientation=8 ⇒ 显示 32x64
            const int dispW = codedH, dispH = codedW;

            // 夹具内容：testsrc2 在 64x32 上行列内容互不相同（渐变 + 计时条 + 十字），转置/剪切可辨。
            var srcPlain = Path.Combine(outDir, "e2_src_plain.jpg");
            int exSrc = await FfmpegRunner.RunAsync(
                $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc2=size={codedW}x{codedH}:r=25\" -frames:v 1 -update 1 -q:v 2 \"{srcPlain}\"",
                null, ffmpeg);
            var src = Path.Combine(outDir, "e2_src_oriented.jpg");
            File.Copy(srcPlain, src, true);
            // ⚠ 同 E1：必须 `-n`（否则静默写成 3=180° ⇒ 轴向不变 ⇒ 夹具没牙），并回读核验。
            int stamp = await RunExifToolAsync($"-overwrite_original -n -Orientation=8 \"{src}\"", exif);
            var tag = await ReadOrientationNumericAsync(src, exif);
            var coded = await ProbeCodedSizeForTestAsync(src, ffprobe);
            var disp = await ImageGeometry.DisplaySizeAsync(src, ffprobe);
            Check($"E2a 夹具取向有牙: Orientation 回读={tag} (退出码 {stamp}) ⇒ coded {coded.w}x{coded.h} / display {disp.w}x{disp.h}（期望 8 / {codedW}x{codedH} / {dispW}x{dispH}）",
                exSrc == 0 && stamp == 0 && tag == "8"
                && coded.w == codedW && coded.h == codedH && disp.w == dispW && disp.h == dispH);

            // ── 无损臂：ffmpeg 直接转 PNG（产物天然已是旋正后的 32x64，且不带取向标签）──
            var lossless = Path.Combine(outDir, "e2_lossless.png");
            try { if (File.Exists(lossless)) File.Delete(lossless); } catch { }
            int exLl = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -i \"{src}\" -update 1 \"{lossless}\"", null, ffmpeg);
            var llSize = await ImageGeometry.DisplaySizeAsync(lossless, ffprobe);
            Check($"E2b 无损产物几何 = 源的**显示**尺寸 {llSize.w}x{llSize.h}（期望 {dispW}x{dispH}，退出码 {exLl}）",
                exLl == 0 && File.Exists(lossless) && llSize.w == dispW && llSize.h == dispH);

            var qa = await QualityAnalysisService.AnalyzeAsync(src, lossless);
            Console.WriteLine($"      [E2] 无损臂 Success={qa.Success} PSNR={qa.PsnrAverage} SSIM={qa.SsimAll} Error=\"{qa.Error.Replace("\n", " ")}\"");
            Check($"E2c 取向源 × 旋正产物 ⇒ **不得**报「分辨率不一致」（实得 Error=\"{qa.Error.Split('\n')[0]}\"）",
                !qa.Error.Contains("分辨率不一致"));
            Check($"E2d 预检放行并算出 PSNR（数值或 inf，实得 {(qa.PsnrAverage.HasValue ? qa.PsnrAverage.Value.ToString("F3") : "无")}，Success={qa.Success}）",
                qa.Success && qa.PsnrAverage.HasValue);
            Check($"E2e 无损产物的「无损」读数 = inf（实得 PSNR={qa.PsnrAverage}）",
                qa.Success && (double.IsPositiveInfinity(qa.PsnrAverage ?? 0) || (qa.PsnrAverage ?? -1) >= 99));

            // ── 有损臂：证明 E2e 的 inf 不是恒返回值 ──
            var lossy = Path.Combine(outDir, "e2_lossy.jpg");
            try { if (File.Exists(lossy)) File.Delete(lossy); } catch { }
            int exLy = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -i \"{src}\" -q:v 12 -update 1 \"{lossy}\"", null, ffmpeg);
            var qaLossy = await QualityAnalysisService.AnalyzeAsync(src, lossy);
            Console.WriteLine($"      [E2] 有损臂 Success={qaLossy.Success} PSNR={qaLossy.PsnrAverage} Error=\"{qaLossy.Error.Replace("\n", " ")}\"");
            Check($"E2f 有损产物同样放行，但 PSNR 必须是**有限**值（实得 {qaLossy.PsnrAverage}，退出码 {exLy}）",
                exLy == 0 && !qaLossy.Error.Contains("分辨率不一致") && qaLossy.PsnrAverage.HasValue
                && !double.IsPositiveInfinity(qaLossy.PsnrAverage.Value));

            // ── 反证臂：产物轴向真的不同（-noautorotate ⇒ 64x32）⇒ 预检必须仍然拒绝 ──
            // ⚠ 实测（本轮）：ffmpeg 会把**帧级** displaymatrix/EXIF 取向**透传**进产物，连 `-map_metadata -1`
            //   都剥不掉（它管的是文件/流级元数据）⇒ 只加 `-noautorotate` 的 PNG 再被读成 32x64，
            //   显示口径下两轴其实**一致**，预检不拒才是对的。所以这里再用 exiftool `-all=` 把标签剥干净，
            //   并**先自证**该产物的显示尺寸确实等于 coded（64x32）—— 否则本条反证臂是空枪。
            var wrongAxis = Path.Combine(outDir, "e2_wrong_axis.png");
            try { if (File.Exists(wrongAxis)) File.Delete(wrongAxis); } catch { }
            int exBad = await FfmpegRunner.RunAsync($"-y -hide_banner -loglevel error -noautorotate -i \"{src}\" -update 1 \"{wrongAxis}\"", null, ffmpeg);
            int exStrip = await RunExifToolAsync($"-overwrite_original -all= \"{wrongAxis}\"", exif);
            var badSize = await ImageGeometry.DisplaySizeAsync(wrongAxis, ffprobe);
            Check($"E2g1 反证臂夹具成立: 剥标签后产物显示尺寸 = coded {codedW}x{codedH}（实得 {badSize.w}x{badSize.h}，ffmpeg 退出码 {exBad}，exiftool 退出码 {exStrip}）",
                exBad == 0 && exStrip == 0 && badSize.w == codedW && badSize.h == codedH);
            var qaBad = await QualityAnalysisService.AnalyzeAsync(src, wrongAxis);
            Console.WriteLine($"      [E2] 反证臂 Success={qaBad.Success} PSNR={qaBad.PsnrAverage} Error=\"{qaBad.Error.Replace("\n", " ")}\"");
            Check($"E2g2 反证: 轴向真不一致的产物仍必须被「分辨率不一致」拒绝（实得 Error=\"{qaBad.Error.Split('\n')[0]}\"，Success={qaBad.Success}）",
                qaBad.Error.Contains("分辨率不一致") && !qaBad.Success);
        }

        // ═══════════════════════════════════════════════
        //  E1/E2 共用夹具与探针辅助
        // ═══════════════════════════════════════════════

        /// <summary>
        /// E1 夹具像素（约定同 <see cref="CreateHdrTestPixels"/>：1.0 = 峰值亮度）。
        /// <para>⚠ 全部像素刻意压在 **SDR 白点以下**（峰值取 1000nits ⇒ 白点 = 203/1000 ≈ 0.203，
        /// 本夹具最大 0.18）。理由：`GainMapEncoder` 的分段 Reinhard 对 y≤1 直通 ⇒ 增益图恒 1x（空间常量）。
        /// 增益图（MPImage2 副图）**并不携带取向标签**，若让增益随内容变化，旋正后的底图与未旋正的增益图
        /// 坐标就会错配，"独立参照"将不再唯一确定产品输出；恒增益夹具把这一层噪声摘掉，
        /// 使线性产物可被 ffmpeg 底图帧 + 独立 sRGB EOTF **逐像素**复核（几何才是本用例的被测量）。</para>
        /// <para>非对称性：竖亮条（列向特征）+ 顶暗带（行向特征）+ 右下块 + 棋盘波纹，w≠h ⇒ 转置/剪切可辨。</para>
        /// </summary>
        private static float[] CreateOrientedFixturePixels(int w, int h)
        {
            var px = new float[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float v = 0.010f + 0.055f * x / w;                 // 仅沿 x 的斜坡（行内不同列，行间相同）
                    if (x < w / 8) v = 0.170f;                          // 左侧亮竖条 —— 列向特征
                    if (y < h / 6) v = 0.020f;                          // 顶部暗横带 —— 行向特征
                    if (x > w * 0.85f && y > h * 0.80f) v = 0.120f;     // 右下块 —— 与上面两者都不重合
                    if (((x / 8) + (y / 8)) % 2 == 0) v *= 1.12f;       // 棋盘波纹：让每一行都不等于任何列
                    v = Math.Min(v, 0.180f);                            // 仍低于 SDR 白点 0.203 ⇒ 增益恒 1x
                    int i = (y * w + x) * 4;
                    px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 1f;   // 消色差：避免色度二次采样引入与几何无关的噪声
                }
            }
            return px;
        }

        /// <summary>教科书 sRGB EOTF（独立于 SimdPixelOps.SrgbToLinearScalar，参照不许抄被测实现）。</summary>
        private static float SrgbEotfForTest(float v)
        {
            v = Math.Clamp(v, 0f, 1f);
            return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>从 gbrpf32le **平面** rawvideo（平面顺序 G,B,R）取一个平面的 float；可选套 EOTF。</summary>
        private static float[] PlaneFloats(byte[] raw, int plane, int count, bool srgbToLinear)
        {
            var dst = new float[count];
            int off = plane * count * 4;
            for (int i = 0; i < count && off + i * 4 + 4 <= raw.Length; i++)
            {
                float v = BitConverter.ToSingle(raw, off + i * 4);
                dst[i] = srgbToLinear ? SrgbEotfForTest(v) : v;
            }
            return dst;
        }

        private static bool FixedTimeBytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static byte[] ReadFirstK(byte[] src, int k)
        {
            int len = Math.Min(k, src.Length);
            var dst = new byte[len];
            Array.Copy(src, dst, len);
            return dst;
        }

        /// <summary>ffprobe 定位（与 QualityAnalysisService.FindFfprobe 同判据；那份是 private，此处独立实现以便测试自证）。</summary>
        private static string? FindFfprobeForTest(string? ffmpegPath)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath)) return null;
            var dir = Path.GetDirectoryName(ffmpegPath) ?? "";
            var p = Path.Combine(dir, "ffprobe");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) p += ".exe";
            if (File.Exists(p)) return p;
            p = ffmpegPath.Replace("ffmpeg", "ffprobe");
            return File.Exists(p) ? p : null;
        }

        /// <summary>
        /// 只读 **coded** 宽高（`stream=width,height`）。
        /// <para>⚠ 这是**故意**在产品之外造的"另一把尺"：本用例要量的正是 coded 与 display 之差，
        /// 若两头都用 <see cref="ImageGeometry"/> 就退化成自我循环。`src/` 侧的单一真值门禁
        /// （<c>_probe-geometry-single-source-scan.ps1</c> 只扫 <c>src/FfmpegGui</c>）不受本行影响。</para>
        /// </summary>
        private static async Task<(int w, int h)> ProbeCodedSizeForTestAsync(string path, string ffprobe)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{path}\"",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                });
                if (p == null) return (0, 0);
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                _ = await errTask;
                var parts = (await outTask).Trim().Split(',');
                return parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) ? (w, h) : (0, 0);
            }
            catch { return (0, 0); }
        }

        /// <summary>
        /// 读**数值**取向（`exiftool -n -Orientation`）。
        /// <para>⚠ 必须带 `-n`：默认输出的是**描述**（"Rotate 270 CW"），而本用例的判据是数值 8
        /// ——「写入不带 -n ⇒ 静默变 3」与「读取不带 -n ⇒ 只给描述」是同一枚硬币的两面，
        /// 只有数值读数能同时挡住这两种错。取不到 ⇒ 返回空串（断言随之判红，不猜）。</para>
        /// </summary>
        private static async Task<string> ReadOrientationNumericAsync(string path, string exiftoolPath)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = exiftoolPath,
                    Arguments = $"-n -Orientation \"{path}\"",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                });
                if (p == null) return "";
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                _ = await e;   // perl 的 locale 警告走 stderr，与读数无关
                var line = (await o).Trim();
                int colon = line.IndexOf(':');
                return colon >= 0 ? line[(colon + 1)..].Trim() : line;
            }
            catch { return ""; }
        }

        /// <summary>直调 exiftool（走 ExifToolService 自检测到的路径，不硬编码）；返回退出码。</summary>
        private static async Task<int> RunExifToolAsync(string arguments, string exiftoolPath)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = exiftoolPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                });
                if (p == null) return -1;
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                var line = (await o).Trim();
                if (line.Length > 0) Console.WriteLine($"      [exiftool] {line}");
                var errText = (await e).Trim();
                if (errText.Length > 0) Console.WriteLine($"      [exiftool stderr] {errText}");
                return p.ExitCode;
            }
            catch (Exception ex) { Console.WriteLine($"      [exiftool] 异常 {ex.Message}"); return -1; }
        }
    }
}
