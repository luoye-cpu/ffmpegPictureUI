using System;
using System.Collections.Generic;
using FfmpegGui.Models;

namespace FfmpegGui.Services
{
    // 声明为 partial：色彩输出决定的完整实现见 FfmpegCommandBuilder.Decision.cs（单一裁决点）。
    public static partial class FfmpegCommandBuilder
    {
        /// <param name="probeInputPath">
        /// 当 <paramref name="inputPath"/> 是 stdin（`"-"`，如 djxl→ffmpeg 管道）时，传入**喂进管道的那份真实源文件**。
        /// 背景（P7d 实测）：位深/alpha 靠探测输入得出，而对 `-` 探测必然失败，于是整条 `-pix_fmt`
        /// 被**静默跳过** ⇒ 编码器自选像素格式，与同一内容走主分支的结果逐像素不同（实测差 R 22 级）。
        /// 只影响像素格式类探测；**输出色彩决定（<c>DecideOutputColor</c>）仍按 inputPath**——
        /// 管道已把探测到的色彩元数据注入 options（高级参数模式），两处不同源是故意保留的。
        /// </param>
        public static string BuildArguments(FfmpegOptions options, string inputPath, string outputPath, string? probeInputPath = null)
        {
            // 探测专用输入：默认与真实输入一致；stdin 时由调用方给出可探测的替代文件。
            string probeInput = string.IsNullOrWhiteSpace(probeInputPath) ? inputPath : probeInputPath!;
            var args = new List<string>();
            args.Add("-y");

            // ── 输入色彩覆盖（必须在 -i 之前，否则编码器无法传递 primaries/transfer）──
            // 关键发现：-color_primaries/-color_trc 放在 -i 之前作为输入色彩覆盖，
            // 编码器才会将它们传递到输出文件；放在 -i 之后大多数编码器会忽略。
            var fmt0 = options.Format.ToLower();
            // PNG/TIFF/APNG 是 RGB 原生格式，-colorspace（YUV 矩阵）会与其 cHRM 块冲突；
            // 但 -color_primaries/-color_trc 仍需保留以正确解释输入色彩空间。
            // JXL 同样为 RGB 原生（XYB 色彩空间），不应写 YUV 矩阵标签。
            var isRgbNativeFmt = fmt0 is "png" or "tiff" or "apng" or "jxl";

            // ════════════════════════════════════════════════════
            //  单一裁决点（步骤 3）：这一次任务的色彩输出决定**只算一次**
            //  输入声明、像素链、iccgen、出口语义、标注取舍全部来自 dec；宣称值的读取点
            //  （<see cref="EffectiveOutputColorTokens"/> → PNG cICP 后置 / UI 文案、
            //   ColorIntentFactory → 引擎目标）读同一份。本函数内**不得再算第二套**
            //  （历史缺陷：“像素已 tonemap 成 SDR、宣称仍写 PQ”、“webp 标 bt709 而像素是 P3”）。
            // ════════════════════════════════════════════════════
            var dec = DecideOutputColor(options, inputPath, assignFaithfulPath: true);
            var outputColorSpace = dec.MatrixForOutput;

            // 保真路径（dec.Faithful）下 Decl* 已被裁决点置 null：不写任何 CICP 标记
            //（写 bt709 会与附着的 ProPhoto ICC 冲突；bt2020 则是削色后的假标签）。
            if (!string.IsNullOrWhiteSpace(dec.DeclPrimaries))
            {
                args.Add("-color_primaries"); args.Add(dec.DeclPrimaries!);
            }
            if (!string.IsNullOrWhiteSpace(dec.DeclTrc))
            {
                args.Add("-color_trc"); args.Add(dec.DeclTrc!);
            }
            // ── 输入矩阵声明（`-i` 前的 `-colorspace`）—— **2026-09-16 修复「角色错位」** ──
            // 此前矩阵**只**被写进 `dec.MatrixForOutput` 当"输出标签"，于是「精确色彩参数」里的
            // 矩阵**对输入解读毫无作用**：实测无标签 YUV 源下 `--color-matrix bt2020nc` 与 `bt709`
            // 的产物 **md5 相同、PSNR=inf**（YUV→RGB 走了 swscale 默认矩阵）。
            // 现按职责分离：这里声明「源用什么矩阵」（影响像素解读，与上面的 primaries/trc 同口径、同位置）；
            // 产物该标注什么仍由下面的输出 `-colorspace` 负责（受容器能力 capM 约束）。
            // ⚠ 必须走 ToFfmpegInputMatrixName 白名单：`bt2020c`/`ycgco`/`chroma-derived-*`/`ictcp`
            //   实测令 swscale 报 "Impossible to convert between the formats" ⇒ 整条命令 -40、零产物；
            //   返回 null 时**跳过**（绝不原样拼）。
            // ⚠ 安全性：对 RGB 原生输出不会污染其 CICP —— 实测 `-colorspace bt2020nc -i … out.png`
            //   的 PNG 仍报 `color_space=gbr`，且应用侧 PngCicpService 对 PNG 恒写 matrix=0。
            var declMatrix = ColorSpaceRegistry.ToFfmpegInputMatrixName(dec.DeclMatrix);
            if (declMatrix != null)
            {
                args.Add("-colorspace"); args.Add(declMatrix);
            }

            // ── 色彩范围检测：rgb48le 等全范围 RGB 输入 → 强制 pc range ──
            // 修复 HDR/高位深图片（如 16-bit TIF）转 YUV 时默认 limited range 导致的过曝
            var inputColorRange = ProbeInputColorRange(inputPath);
            if (!string.IsNullOrWhiteSpace(inputColorRange))
            {
                args.Add("-color_range"); args.Add(inputColorRange);
            }

            var isGifToAvif = inputPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                           && options.Format.Equals("avif", StringComparison.OrdinalIgnoreCase);

            args.Add("-i");
            args.Add($"\"{inputPath}\"");

            // GIF → AVIF：swscale 精确标志（输出选项，FFmpeg 8.x 不接受 -i 前 -pix_fmt）
            if (isGifToAvif)
            {
                args.Add("-sws_flags");
                args.Add("accurate_rnd+full_chroma_int");
            }

            // 编码器选择
            if (!string.IsNullOrWhiteSpace(options.Encoder))
            {
                args.Add("-c:v");
                args.Add(options.Encoder);
            }

            // 线程：总是传递
            args.Add("-threads");
            args.Add($"{options.Threads}");

            var fmt = options.Format.ToLower();

            // ═══════════════════════════════════════════════════
            //  格式色彩能力约束（实测驱动，针对失败模式修正）：由 dec 唯一给出，本处只应用位深硬约束。
            //  - JPEG(Ffmpeg mjpeg)/WebP(libwebp): 仅支持 SDR；P3 等广色域经 ICC 可交付；
            //  - TIFF: 仅支持 sRGB 显示色彩语义 (ICC 管理，color_primaries 无效)。
            //  - AVIF: 位深上限**只按实际会跑的编码器**给（libaom/av1_nvenc 12、其余 10，
            //    见 `ImageEncoderArgs.AvifMaxBitDepthForEncoder`，任务 #36）。原先另有一条
            //    "Display P3 + 高位深 ⇒ 强制 8bit"的特例，2026-09-26 任务 #39 **实测否证后已删除**
            //    （数据与理由就在 `GetFormatColorCapabilities` 的 avif 分支上）。
            // ═══════════════════════════════════════════════════
            int capMaxBitDepth = dec.CapMaxBitDepth;   // 已含硬约束；options.BitDepth 的钳制在裁决点完成

            // ═══════════════════════════════════════════════════
            //  色彩像素链：完全由单一裁决点 dec.PixelChains 给出（tonemap / 目标 zscale / D1 超广归一）
            //  本函数只负责“按决定写命令行”，不得在此复算任何色彩取舍
            // ═══════════════════════════════════════════════════
            // ── 色彩策略（取代 IccMode）：控制「是否转换 + 如何标定(ICC/CICP)」；
            //    具体取舍（preserveSource/stripIcc/iccgen/链）已在裁决点完成，此处只剩日志与元数据映射需要它。
            var strategy = options.ColorStrategy;

            // ── 按决定写色彩链（唯一的“决定→命令行”换算点；换算规则见 DecideOutputColor）──
            foreach (var chain in dec.PixelChains) AppendVideoFilter(args, chain);

            // ═══════════════════════════════════════════════════
            //  多帧输入 → 单帧容器规范化（强制取首帧）
            //  判据**只看目标容器能不能装多帧**，与**输入扩展名无关**：
            //  多帧输入（动图 gif/webp/apng/…）写单帧容器（jpg/png/tiff/…）时，image2 封装器报
            //  `Cannot write more than one file with the same name` ⇒ `Invalid argument`
            //  ⇒ 整条任务失败、零产物；故在输出前强制 `-frames:v 1`（仅第一帧）。
            //  多帧容器（gif/webp/avif/apng；以及 `--format jxl --animation-fps` 走 libjxl_anim 时）不加，保留全部帧。
            //  ⚠ 2026-09-18 修：旧判据是 `inputPath.EndsWith(".gif")` ⇒ 只保护 `.gif` 输入，
            //    `.webp`/`.apng` 动图写静态目标必失败（实测 exit=1、0 产物）。改成只看目标容器。
            //  ⚠ 对**单帧输入**加 `-frames:v 1` 是 no-op（本来就只有 1 帧）—— 实测产物逐像素不变
            //    （`verify-anim-static-target.ps1` 的单帧不回归格用 PSNR=average:inf 锁）。
            //  ⚠ `jxl + AnimationFps` 必须排除：libjxl_anim 能装多帧，若也插 `-frames:v 1` 会把动图
            //    **静默截成 1 帧**（变异实测 M2 复现：exit=0 且 1 帧）—— 失败要响，不能静默降级。
            // ═══════════════════════════════════════════════════
            bool targetHoldsMultipleFrames = fmt is "gif" or "webp" or "avif" or "apng"
                || (fmt == "jxl" && options.AnimationFps.HasValue);
            bool insertFramesV1 = !targetHoldsMultipleFrames; // 目标容器不支持多帧时强制首帧

            switch (fmt)
            {
                case "jpg":
                case "jpeg":
                    // Gain Map (Ultra HDR) 走纯 C# GainMapEncoder（见 QueueProcessor.ProcessGainMapJpegAsync），
                    // 不再经此 ffmpeg 分支；此处仅按编码器后端设置 JPEG 质量参数。
                    // ═══ 出声：本分支（ffmpeg/mjpeg）**吃不下**的两个请求，绝不静默丢弃 ═══
                    // ⚠ 新增文案一律 **ASCII**：`_probe-cjk-hardcode-scan.ps1` 的 `CsUi` 余量为 0
                    //   （该门禁按 sink 分桶，本处不是它认得的 sink ⇒ 中文会判红）。
                    // ① 渐进 JPEG：ffmpeg 的 mjpeg 编码器**没有**该能力（实测 `-h encoder=mjpeg` 无该项；
                    //    真传 `-progressive` 会被 ffmpeg 以 `Unrecognized option` 拒绝）⇒ 产物只能是基线。
                    //    引擎路径已由 `ColorEngineRouter.BlockReason` 拦截；此处覆盖
                    //    `--color-engine legacy`（路由层对 legacy 恒放行 ⇒ 原先**完全静默**）
                    //    与 `auto` 的回退格（后者另有一条引擎侧日志）。
                    if (options.JpegProgressiveId > 0)
                        Console.WriteLine("⚠ [jpeg] --jpeg-progressive ignored: ffmpeg's mjpeg encoder has no "
                            + "progressive support, so the output is baseline. Use -e Cjpegli for progressive JPEG.");
                    // ② 无损 JPEG：mjpeg 是**有损**编码器 ⇒ `--lossless true` 对本分支无效（原先静默产有损）。
                    if (options.Lossless)
                        Console.WriteLine("⚠ [jpeg] --lossless true ignored: the mjpeg encoder is lossy, so the output is lossy. "
                            + "The only lossless JPEG path here is DNG container compression (--format dng --dng-compression 0).");
                    if (options.EncoderBackend == EncoderBackend.Cjpegli)
                    {
                        // JPEG-LI (cjpegli) 使用 butteraugli distance（0-15），与 JXL 一致
                        args.Add("-distance");
                        args.Add(MapJpegliDistance(options.Quality).ToString("F1"));
                    }
                    else
                    {
                        args.Add("-q:v");
                        args.Add(MapJpegQuality(options.Quality).ToString());
                    }
                    if (!string.IsNullOrWhiteSpace(options.JpegHuffman))
                    { args.Add("-huffman"); args.Add(options.JpegHuffman == "optimal" ? "1" : "0"); }
                    if (!string.IsNullOrWhiteSpace(options.JpegDct) && options.JpegDct != "auto")
                    { args.Add("-dct"); args.Add(options.JpegDct); }
                    break;
                case "png":
                    // ── A-14（2026-09-26 修）：无损 ≠ 关掉 deflate ──
                    // 旧写法把「强制无损」实现成**恒** `-compression_level 0`（zlib level 0 = 存储，完全不压），
                    // 与用户指定的压缩级别无关。实测（随包 ffmpeg git-2026-09 系列，src_8bit.png 512x384）：
                    //   level 0 = 592110 B / level 1 = 27280 B / level 9 = 23364 B
                    //   三档各自解码回 rawvideo(rgb24) 的 md5 **与源解码 md5 完全相同**
                    //   ⇒ 0/1/9 全是真无损，25.4 倍的体积差**只来自压缩级别**，无损语义对此没有要求。
                    // 现只把「压缩级别」这一根轴交给 Quality（`MapPngCompression` 反比映射 0-9），
                    // 与 `options.Lossless` 解绑；「PNG/TIFF/APNG **强制无损**」这条既定产品语义**未动**
                    // （它由 MainWindow.UpdateOptionAvailability 保证，被 UiTestHost 的 J9a / L2a-L2d 锁住）。
                    // ⚠ 界面现状：png 下质量被钉在 100 且滑块禁用 ⇒ `MapPngCompression(100)` = **0**
                    //   ⇒ 默认命令行与产物**逐字节不变**（J9a 断言的 `-compression_level=0` 仍然成立）；
                    //   真正把这只旋钮交回用户需要界面侧放开那把锁（或给 PNG 单开级别控件），属另一条 lane。
                    args.Add("-compression_level");
                    args.Add(MapPngCompression(options.Quality).ToString());
                    if (!string.IsNullOrWhiteSpace(options.PngPred))
                    { args.Add("-pred"); args.Add(options.PngPred); }
                    if (options.PngDpi.HasValue && options.PngDpi.Value > 0)
                    { args.Add("-dpi"); args.Add(options.PngDpi.Value.ToString()); }
                    break;
                case "webp":
                    if (options.Lossless)
                    {
                        // ── 无损模式 ──
                        args.Add("-lossless");
                        args.Add("1");
                        // 显式设置 -q:v 100，保证 FFmpeg libwebp 内部以无损 hint 运行
                        args.Add("-q:v");
                        args.Add("100");
                        // ⚠ A-15（2026-09-26 复核）：`-compression_level` 是 AVCodecContext 的**通用**项，
                        //   落到 libwebp 的 method(0-6)，**有损同样生效**（旧注释称它是无损专有的 zlib 级别，
                        //   判错了；见下面有损分支的实测数字）。无损下它本来就在发，此处只做范围防护。
                        //   实测静帧无损：省略 14426 = level4 14426 = level6 14426，level0 = 27030，level9 钳到 6；
                        //   动图无损：省略 34822 / level0 41930 / level6 34404 ⇒ 无损下它一直是有效旋钮，两种模式都吃。
                        var cl = options.WebpCompressionLevel ?? 4;
                        if (cl < 0) cl = 0;
                        if (cl > 6) cl = 6;
                        args.Add("-compression_level");
                        args.Add(cl.ToString());
                        // 无损模式下不应传递 -preset（picture/photo 等预设会配置有损量化参数，
                        // 与 -lossless 1 冲突，可能导致 FFmpeg 静默退化为有损）
                        // 仅在 preset 为 "default" 或 "none" 时允许
                        if (!string.IsNullOrWhiteSpace(options.WebpPreset)
                            && (options.WebpPreset == "default" || options.WebpPreset == "none"))
                        {
                            args.Add("-preset");
                            args.Add(options.WebpPreset);
                        }
                        else if (!string.IsNullOrWhiteSpace(options.WebpPreset))
                        {
                            // 出声：无损模式下 preset 被**有意**丢弃（会配置有损量化参数、可能让 libwebp
                            // 静默退化为有损），但用户看不到 ⇒ 原先属"看起来配置过"。ASCII 文案，理由见 jpg 分支。
                            Console.WriteLine("⚠ [webp] --webp-preset ignored in lossless mode: presets configure lossy "
                                + "quantization and can silently degrade -lossless 1 to lossy.");
                        }
                    }
                    else
                    {
                        args.Add("-q:v");
                        args.Add(options.Quality.ToString());
                        var lossyPreset = !string.IsNullOrWhiteSpace(options.WebpPreset)
                                          && options.WebpPreset != "none";
                        // 空判要重复写在 if 里：IsNullOrWhiteSpace 的NotNullWhen 精化不会穿过中间 bool 变量，
                        // 否则这里会拿可空串喂 List.Add（CS8604）；语义与 lossyPreset 等价，不是新条件。
                        if (lossyPreset && options.WebpPreset is not null)
                        { args.Add("-preset"); args.Add(options.WebpPreset); }
                        // ── A-15（2026-09-26 修）：有损模式同样放行 -compression_level ──
                        // 旧注释/旧告警称它是无损专有的 zlib 级别、有损下无效 ⇒ **判错**，一个可用旋钮被藏掉。
                        // 它是 AVCodecContext 通用项，落 libwebp 的 method(0-6)，有损/无损、静帧/动图都吃。
                        // 三向实测（随包 ffmpeg；省略 / 传默认 4 / 传有效值，比产物 md5）：
                        //   静帧有损 q75 preset=none：省略 7482 = 4 7482，0 ⇒ 9868，6 ⇒ 7378，9 ⇒ 7378(钳到 6)
                        //   动图有损 q75 preset=none：省略 46126 = 4 46126，0 ⇒ 59472，6 ⇒ 43930
                        //   动图有损 无 preset：省略 46126 = 4 46126（GUI 走的就是这条默认口径）
                        // ⇒ 界面默认值 4 与「省略」**逐字节相同** ⇒ 放行不改动任何默认产物，只有改过才生效。
                        // ⚠ 唯一真实的失效场景：同一条命令给了**非 none 的 -preset** —— libwebp 的 preset
                        //   会把 method 钉死（实测 preset=picture 时 level 0/4/6 三档产物 md5 全等，
                        //   无损 + preset=default 也一样）。与本文件无损分支**丢弃 preset** 是同一处冲突的另一面，
                        //   故这里出声（ASCII：sink 限制见 jpg 分支注释）。
                        if (options.WebpCompressionLevel.HasValue)
                        {
                            var lcl = options.WebpCompressionLevel.Value;
                            // 实测 -1 不被当作「未指定」，libwebp 按 method 0 处理 ⇒ 产物反而变大，显式钳制。
                            if (lcl < 0) lcl = 0;
                            if (lcl > 6) lcl = 6;
                            args.Add("-compression_level");
                            args.Add(lcl.ToString());
                            if (lossyPreset)
                                Console.WriteLine("⚠ [webp] -compression_level is pinned by -preset "
                                    + options.WebpPreset + " (measured byte-identical output for level 0/4/6); "
                                    + "use --webp-preset none to let -compression_level apply.");
                        }
                    }
                    // 动图 WebP: -loop 控制循环
                    if (options.AnimationLoop >= 0)
                    { args.Add("-loop"); args.Add(options.AnimationLoop.ToString()); }
                    // ── 动图滤镜（fps / 缩放）：与 apng(:291-296) / jxl(:342-347) 分支**同口径** ──
                    // ⚠ P1-F 缺陷 4（2026-09-19 修）：此前 WebP 分支只写 -loop，用户在动图面板设的
                    //   帧率与宽度被**静默丢弃**（命令串里连 token 都没有，用户无从察觉）。
                    //   定性 = **缺陷**（不是 ffmpeg 能力缺失），实测证据（ffmpeg git-2026-09-01，
                    //   10 帧 / 256x192 的 anim_gif.gif）：
                    //     `-c:v libwebp_anim -loop 0 -vf "fps=10,scale=64:-1:flags=lanczos"`
                    //        ⇒ 10 帧 / 64x48 / avg_frame_rate=10/1
                    //     `-c:v libwebp`（本软件 WebP 的**默认**编码器）加同一 -vf
                    //        ⇒ 10 帧 / 64x48（两条通路都支持 fps/scale 滤镜；无滤镜对照 256x192）
                    //   ⇒ 走 `AppendVideoFilter` 合并写入（**不**新增第二条 -vf）：与色彩像素链
                    //     （`dec.PixelChains`，:121 先写）落在**同一条 -vf** 且像素链在前；
                    //     `iccgen` 段由 :563 在 switch **之后**追加 ⇒ 出现在 fps/scale 之后。
                    //     实测该条 -vf 为 `fps=12,scale=320:-1:flags=lanczos,iccgen`（唯一一条 -vf）。
                    if (options.AnimationFps.HasValue || options.AnimationScaleW > 0)
                    {
                        var webpFilters = new List<string>();
                        if (options.AnimationFps.HasValue)
                            webpFilters.Add($"fps={options.AnimationFps.Value}");
                        if (options.AnimationScaleW > 0)
                            webpFilters.Add($"scale={options.AnimationScaleW}:-1:flags=lanczos");
                        AppendVideoFilter(args, string.Join(",", webpFilters));
                    }
                    break;
                case "gif":
                {
                    // ── 2026-09-17：滤镜链与 -loop 的拼装**整体搬移**到
                    //    `ImageEncoderArgs.BuildGifFilterChain` / `BuildGifOptions`（单一真值），
                    //    传统管线与自研引擎出口共用同一份 —— 本项目的「两处口径漂移」就是这么反复修掉的。
                    //    搬移是**逐项、逐顺序**的纯移动，语义一字未改（同一源可用 `[cmd] ffmpeg …` 逐字对拍）。
                    //
                    // 色彩转换滤镜（tonemap/zscale/iccgen）此前经 AppendVideoFilter 写入独立 -vf。
                    // GIF 用 -filter_complex(palettegen/paletteuse)，ffmpeg 不允许同一流同时用 simple(-vf)
                    // 与 complex(-filter_complex) 滤镜（实测："Simple and complex filtering cannot be used
                    // together for the same stream"）→ 必须把 -vf 链并入 filter_complex 前端并移除独立 -vf。
                    string? colorChain = null;
                    var preVfIdx = args.FindIndex(a => a == "-vf");
                    if (preVfIdx >= 0 && preVfIdx + 1 < args.Count)
                    {
                        colorChain = args[preVfIdx + 1];   // 色彩转换置于链首（先 tonemap 再 fps/scale/palette）
                        args.RemoveAt(preVfIdx + 1);
                        args.RemoveAt(preVfIdx);
                    }

                    var gifChain = ColorMapping.ImageEncoderArgs.BuildGifFilterChain(options, colorChain);
                    if (gifChain != null)
                    {
                        if (options.GifPaletteOptimize)
                        {
                            args.Insert(1, "-filter_complex");
                            args.Insert(2, $"\"{gifChain}\"");
                        }
                        else
                        {
                            args.Add("-vf");
                            args.Add(gifChain);
                        }
                    }
                    args.AddRange(ColorMapping.ImageEncoderArgs.BuildGifOptions(options));
                    break;
                }
                case "apng":
                {
                    // APNG 必须显式指定 -f apng（.png 后缀默认走 image2 单帧封装）
                    args.Add("-f"); args.Add("apng");
                    if (string.IsNullOrWhiteSpace(options.Encoder) || options.Encoder == "png")
                    {
                        var idx = args.FindIndex(a => a == "-c:v");
                        if (idx >= 0 && idx + 1 < args.Count)
                            args[idx + 1] = "apng";
                        else
                        { args.Add("-c:v"); args.Add("apng"); }
                    }
                    var filters = new List<string>();
                    if (options.AnimationFps.HasValue)
                        filters.Add($"fps={options.AnimationFps.Value}");
                    if (options.AnimationScaleW > 0)
                        filters.Add($"scale={options.AnimationScaleW}:-1:flags=lanczos");
                    if (filters.Count > 0)
                    { args.Add("-vf"); args.Add(string.Join(",", filters)); }
                    // ── A-16（2026-09-26 修）：APNG 能力被静默截短 ──
                    // PNG 分支一直在发的 -pred / -dpi 此前在 APNG 下**一个都不发**，而 `apng` 编码器
                    // 与 `png` 编码器共用同一族 AVOptions：`ffmpeg -h encoder=apng` 列出的就是
                    // `(A)PNG encoder AVOptions: -dpi / -dpm / -pred (from 0 to 5) (default paeth)`
                    // （取值名 none/sub/up/avg/paeth/mixed，与 `_pngPredValues` 逐字相同）。
                    // 实测（随包 ffmpeg，anim_gif.gif 10 帧 → -f apng -c:v apng）：
                    //   -pred none 56979 B / sub 70234 B / paeth 73198 B / mixed 73211 B
                    //   四种产物解码回 rawvideo(rgb24) 的 md5 **互相同**（⇒ 换预测不改变像素，只改体积）
                    //   -dpi 300 ⇒ exiftool 读得 pHYs `Pixels Per Unit X = 11811 / Pixel Units = meters`，
                    //   不给 -dpi 的对照只读到 `1 / 1 / Unknown`（先量到「真的有该字段」的一份再下结论）
                    // 每帧语义（要求自测的一项）：同一素材只取 1 帧时 none→paeth 只差 623 B，
                    //   取满 10 帧时同口径差 16219 B（≈26 倍）⇒ 若 -pred 只作用首帧，这个差值不会随帧数放大，
                    //   故确认**对每一帧生效**（后续帧是帧间差分，预测模式的影响比首帧更大）。
                    // 16-bit / alpha 源同样吃（src_16bit.png + `-pred sub -dpi 150` ⇒ PSNR average:inf）。
                    // ⇒ 两个选项按 PNG 分支**同款判据与同款取值**下发，不新增界面字段。
                    // ⚠ 与 PNG 分支的**唯一**不同：这里**不发** `-compression_level`。实测 apng 的默认级别
                    //   就等于 6（省略 73198 = `-compression_level 6` 73198、9 ⇒ 68059、0 ⇒ **1743881**），
                    //   而 PNG 那根轴是 `MapPngCompression(Quality)` 且无损格式下 Quality 被钉在 100 ⇒ 0，
                    //   照搬会把 APNG 撑到 24 倍 ⇒ 要接这根轴得先单独定映射（见返回说明）。
                    if (!string.IsNullOrWhiteSpace(options.PngPred))
                    { args.Add("-pred"); args.Add(options.PngPred); }
                    if (options.PngDpi.HasValue && options.PngDpi.Value > 0)
                    { args.Add("-dpi"); args.Add(options.PngDpi.Value.ToString()); }
                    if (options.AnimationLoop >= 0)
                    { args.Add("-plays"); args.Add(options.AnimationLoop.ToString()); }
                    break;
                }
                case "avif":
                    // ⚠ 2026-09-17：AVIF 编码参数**整体搬移**到 ImageEncoderArgs.BuildAvifOptions（单一真值），
                    //   传统管线与自研引擎出口共用同一份 —— 本项目的「两处口径漂移」就是这么反复修掉的。
                    //   搬移是**逐项、逐顺序**的纯移动，语义一字未改（同一源可用 `[cmd] ffmpeg …` 逐字对拍）。
                    args.AddRange(ColorMapping.ImageEncoderArgs.BuildAvifOptions(options));
                    // GIF → AVIF：两步滤镜链 —— pal8→rgba（保留透明索引→alpha）+ rgba→yuva420p（编码器格式）
                    if (inputPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    {
                        args.Add("-vf");
                        args.Add("format=rgba,format=yuva420p");
                    }
                    break;
                case "tiff":
                    // ⚠ P1-F 缺陷 3（2026-09-19 修）：原先 `options.Lossless ⇒ 强制 raw` **优先于**用户选择，
                    //   而 TIFF 是**强制无损**格式（UpdateOptionAvailability 对 tiff 恒置 LosslessCheck=true
                    //   并锁定）⇒ `TiffCompressionCombo` 的四个取值（raw/lzw/deflate/packbits）产物完全相同，
                    //   下拉框成了死控件（用户改了却毫无效果）。
                    //   实测（ffmpeg git-2026-09-01，src_8bit.png→tiff，8-bit 与 16-bit 两轮）：
                    //     四种算法 PSNR 均为 `average:inf`（**都是无损**），仅体积不同
                    //     （8-bit：raw 590657B / lzw 52707B / deflate 26379B / packbits 594241B）
                    //   ⇒ 「无损就必须 raw」不成立（无任何正确性理由），改为**用户选择优先**；
                    //     未指定算法时保持原默认 raw（旧行为逐字保留）。
                    if (!string.IsNullOrWhiteSpace(options.TiffCompressionAlgo))
                    {
                        args.Add("-compression_algo");
                        args.Add(options.TiffCompressionAlgo);
                    }
                    else if (options.Lossless)
                    {
                        args.Add("-compression_algo");
                        args.Add("raw");
                    }
                    if (options.TiffDpi.HasValue && options.TiffDpi.Value > 0)
                    { args.Add("-dpi"); args.Add(options.TiffDpi.Value.ToString()); }
                    break;
                case "jxl":
                    // ── 动图 JXL：使用 libjxl_anim 编码器 ──
                    // 仅当用户显式设置了帧率才视为动图（AnimationLoop 默认 0，不能作为判据）
                    var isJxlAnimated = options.AnimationFps.HasValue;
                    if (isJxlAnimated)
                    {
                        // 替换编码器为 libjxl_anim（支持动图）
                        var encIdx = args.FindIndex(a => a == "-c:v");
                        if (encIdx >= 0 && encIdx + 1 < args.Count)
                            args[encIdx + 1] = "libjxl_anim";
                        else
                        { args.Add("-c:v"); args.Add("libjxl_anim"); }

                        // 动图滤镜（FPS + 缩放）
                        var jxlFilters = new List<string>();
                        if (options.AnimationFps.HasValue)
                            jxlFilters.Add($"fps={options.AnimationFps.Value}");
                        if (options.AnimationScaleW > 0)
                            jxlFilters.Add($"scale={options.AnimationScaleW}:-1:flags=lanczos");
                        if (jxlFilters.Count > 0)
                        { args.Add("-vf"); args.Add(string.Join(",", jxlFilters)); }

                        // 质量参数
                        if (options.Lossless)
                        { args.Add("-distance"); args.Add("0"); }
                        else
                        { args.Add("-distance"); args.Add(MapJxlDistance(options.Quality).ToString("F1")); }
                        if (options.JxlEffort.HasValue)
                        { args.Add("-effort"); args.Add(options.JxlEffort.Value.ToString()); }
                        if (options.JxlModular == true)
                        { args.Add("-modular"); args.Add("1"); }
                    }
                    // --- JPEG→JXL 快速路径：意图是"不解码、直接复制 DCT 系数" ---
                    // ⚠ 更正（2026-09-19 实测）：本工具链**做不到**无损重封装。此前注释称
                    //   "FFmpeg 8.x+ 中 libjxl 自动检测 JPEG 输入并启用无损重封装，只需 -distance 0"
                    //   ——**不成立**。实测 `-distance 0` 走的是普通有损→无损（modular）重编码：
                    //   `jxlinfo` 里**没有** "JPEG bitstream reconstruction data available"（无 jbrd），
                    //   且同一 JPEG 产物大小 35487B（ffmpeg-libjxl）vs 16497B（cjxl 后端真重封装）。
                    //   即本分支只是"低损重编码"，**不是** JPEG 比特流无损重封装。
                    //   真正的 jbrd 无损重封装只有 cjxl 后端（`-d 0 --lossless_jpeg=1`）能做到，
                    //   本工具链的 ffmpeg-libjxl 路径不具备该能力。
                    // ⚠ 该分支**不是**静帧 JXL 的通用参数（它靠"不解码"实现无损重封装）⇒
                    //   引擎出口**不承接**（见 ImageEncoderArgs.UnsupportedEncoderSettings），
                    //   故不能与下面的 BuildJxlOptions 合并；只有 `!JxlLosslessJpeg` 的静帧参数
                    //   才走单一真值 `ImageEncoderArgs.BuildJxlOptions`（2026-09-17 搬移）。
                    if (options.JxlLosslessJpeg)
                    {
                        args.Add("-distance");
                        args.Add("0");
                        if (options.JxlEffort.HasValue)
                        { args.Add("-effort"); args.Add(options.JxlEffort.Value.ToString()); }
                    }
                    else
                    {
                        args.AddRange(ColorMapping.ImageEncoderArgs.BuildJxlOptions(options));
                    }
                    break;
            }

            // GIF → AVIF：强制保留透明通道（GIF 调色板透明度 → alpha 通道）
            // 必须在通用 pix_fmt 之前，确保覆盖任何无 alpha 的默认值
            if (fmt == "avif"
                && inputPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-pix_fmt");
                args.Add(MapAvifAlphaPixFmt(options));
            }
            // WebP 无损模式：强制使用 RGB 像素格式。
            // libwebp 无损编码需要精确的 RGB 输入；如果输入为 YUV 窄范围（tv range），
            // YUV→RGB 转换会产生截断误差，导致 libwebp 检测到"非精确还原"而静默退化为有损。
            // 显式指定 rgba/rgba64le 可确保转换路径可控。
            else if (fmt == "webp" && options.Lossless)
            {
                args.Add("-pix_fmt");
                var bd = options.BitDepth ?? 8;
                args.Add(bd <= 8 ? "rgba" : "rgba64le");
            }
            // ── 通用像素格式选择 ──
            // 规则：
            // ① 色度采样显式指定（非 auto）→ 必须写 -pix_fmt。此前额外要求位深
            //    也非 auto，导致「位深 auto + 8-bit 输入」时子采样选择被静默丢弃、
            //    恒输出 4:2:0（Bug 修复）。
            // ② 位深 auto → 探测输入实际位深：>8 按输入位深输出，≤8 按 8-bit 输出。
            // ③ 色度为 auto → 仅按位深指定（默认 4:2:0）。
            var inputHasAlpha = GetCachedProbe(probeInput).hasAlpha;
            int EffectiveBitDepth()
            {
                if (options.BitDepth.HasValue) return options.BitDepth.Value;
                var detectedBd = ProbeInputBitDepth(probeInput);
                // 即使输入是 16-bit，也要受格式能力上限约束
                return Math.Min(detectedBd > 8 ? detectedBd : 8, capMaxBitDepth);
            }

            // ── AVIF 位深按编码器 clamp（2026-08-16 实测驱动）──
            // libaom-av1/av1_nvenc: 8/10/12-bit；libsvtav1/av1_qsv/av1_amf: 仅 8/10-bit。
            // 防止 16-bit 输入 auto 位深 → yuv420p16le 编码失败（libaom/libsvt 均不支持 16-bit YUV）。
            // ⚠ 2026-09-17：判定式**单一真值**搬到 ImageEncoderArgs.AvifMaxBitDepth（引擎出口也用同一份），
            //   表达式逐字等价 —— 本处仅转发，行为不变。
            var avifMaxBd = fmt == "avif" ? ColorMapping.ImageEncoderArgs.AvifMaxBitDepth(options) : int.MaxValue;
            int ClampAvifBd(int bd) => Math.Min(bd, avifMaxBd);

            // GIF 为调色板格式：paletteuse 输出 pal8，显式 -pix_fmt（如 16-bit 源的 yuv420p16le）会与 pal8
            // 冲突/编码失败 → GIF 输出跳过通用 pix_fmt（与 8-bit GIF 无 -pix_fmt 的既有正确行为一致）。
            bool skipGenericPixFmt = fmt is "gif";
            if (!skipGenericPixFmt
                && !string.IsNullOrWhiteSpace(options.Chroma)
                && !options.Chroma.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-pix_fmt");
                args.Add(MapPixFmt(options.Format, options.Chroma, ClampAvifBd(EffectiveBitDepth()), inputHasAlpha, options.ColorRange));
            }
            else if (!skipGenericPixFmt && !options.BitDepth.HasValue)
            {
                // 位深为 auto：从输入文件自动探测实际位深并传递
                // 修复 HDR 图片（16-bit+）输出时退化为 8-bit 的问题
                // ⚠ P7d：必须探 probeInput（stdin 探不到就会整条 -pix_fmt 不写，像素格式交给编码器自选）
                var detectedBd = ProbeInputBitDepth(probeInput);
                if (detectedBd > 8)
                {
                    args.Add("-pix_fmt");
                    args.Add(MapPixFmt(options.Format, options.Chroma, ClampAvifBd(Math.Min(detectedBd, capMaxBitDepth)), inputHasAlpha, options.ColorRange));
                }
            }
            else if (!skipGenericPixFmt && options.BitDepth.HasValue)
            {
                // 位深已手动指定但色度为 auto：直接按位深设置 pix_fmt
                // （修复 Chroma=auto 时 BitDepth 设置失效的 Bug）
                args.Add("-pix_fmt");
                args.Add(MapPixFmt(options.Format, options.Chroma, ClampAvifBd(options.BitDepth.Value), inputHasAlpha, options.ColorRange));
            }

            // ── 输出色彩范围 ──
            // 优先级: 用户显式选择 (tv/pc) > 自动 (RGB 输入 → pc)
            // 修复：PC 范围图片（TIFF/PNG 等恒为全范围 RGB）转 AVIF 等 YUV 格式时，
            // 输入侧已在 -i 前声明 pc，但输出侧未声明 → swscale 默认按 limited (tv)
            // 矩阵转换（黑位压缩）且编码器将输出标记为 tv。
            // 输出侧 -color_range：① 控制 swscale 转换矩阵（full/limited 完整映射）
            // ② AV1/AVIF 写入对应范围标记（CICP full_range_flag）。
            var outRange = ResolveOutputColorRange(options.ColorRange, inputColorRange);
            // JPEG/WebP 规范恒为 full range：mjpeg 编码器拒绝 limited 输入（编码失败），
            // WebP 容器无范围标记（tv 数据会发灰）。防御性回退 pc（预设注入等旁路）。
            if ((fmt is "jpg" or "jpeg" or "webp") && outRange == "tv")
            {
                outRange = "pc";
            }
            if (!string.IsNullOrWhiteSpace(outRange)
                && !isRgbNativeFmt
                && fmt != "gif") // 调色板格式无色彩范围概念
            {
                args.Add("-color_range");
                args.Add(outRange);
            }

            // ── 元数据映射（根据 ICC 模式决定保留或丢弃元数据）──
            // 模式 1 (None): CICP 始终保留，仅剥离非色彩元数据
            // 模式 4 (BakeOnly): 仅剥离 ICC 相关元数据
            // 模式 2/3: 保留全部元数据
            // 除「携带(CarryIcc)」外均剥离源流元数据（目标 ICC 由 iccgen/exiftool 后置生成，避免源 ICC 冲突）。
            bool stripAllMeta = strategy != Models.ColorStrategy.CarryIcc;

            // CICP 兼容格式列表（保留 CICP 不剥离）
            bool isCicpFormat = fmt is "avif" or "jxl" or "png" or "apng";

            if (stripAllMeta && !isCicpFormat)
            {
                // 非 CICP 格式：完全剥离元数据
                args.Add("-map_metadata");
                args.Add("-1");
                args.Add("-map_chapters");
                args.Add("-1");
            }
            else if (stripAllMeta && isCicpFormat)
            {
                // CICP 格式：仅剥离流元数据，保留色彩元数据
                args.Add("-map_metadata");
                args.Add("0");
                // 但移除数据流映射（避免复制 ICC Profile）
                args.Add("-map_metadata:s:v");
                args.Add("-1");
                args.Add("-map_chapters");
                args.Add("-1");
            }
            else
            {
                args.Add("-map_metadata");
                args.Add("0");
                args.Add("-map_metadata:s:v");
                args.Add("0:s:v");
                args.Add("-map_chapters");
                args.Add("0");
            }

            // ── ICC 取舍已在裁决点完成：是否附 ICC（AttachIcc）、编码器会不会把 iccgen 的 ICC 落盘
            //    （EncoderWritesFilterIcc）、需不需要队列后置嵌入（NeedsPostEmbedIcc）。本处只按决定写命令行。
            //  （D2：广色域/超广色域输出用「标准 ICC + CICP」完整描述、iccgen 不支持 HDR 传递等
            //   判据已全部移入 DecideOutputColor，不在此处复算。）

            // ═══════════════════════════════════════════════════
            //  关键约束：iccgen 仅在 RGB 域工作（对 10-bit+ YUV 输入报 "Not yet implemented"），
            //  且不支持 HDR 传递——两者均已作为判据进入裁决点。
            // ═══════════════════════════════════════════════════
            if (dec.RunIccgen)
            {
                // 高位深 YUV 输入 → iccgen 前先转 RGB（iccgen 仅支持 RGB 输入）；
                // 若还需 tonemap，tonemap 链已在前面处理过，这里只负责格式转换。
                if (dec.IccgenNeedsRgb) AppendVideoFilter(args, "format=rgb48le");
                AppendVideoFilter(args, "iccgen");
            }

            // ── 输出色彩矩阵（YUV colorspace，必须在 -i 之后写入输出流）──
            // PNG/TIFF 是 RGB 原生格式，不需要 YUV 色彩矩阵参数
            // 各编码器支持的 colorspace 白名单：只允许 YUV 矩阵写给 YUV 编码器。
            // gbr/rgb 为 RGB 矩阵：libwebp_anim/mjpeg/libaom/libsvtav1 一律拒绝
            // （实测 libwebp_anim: "Undefined constant ... in 'gbr'" → Invalid argument → 0 字节）。
            // RGB 原生格式(png/tiff/apng/jxl)已由 isRgbNativeFmt 跳过本块，故此处不含 gbr/rgb。
            // 白名单换成「ffmpeg 实际接受的名字」的**实测枚举**（ColorSpaceRegistry.FfmpegColorSpaceNames）。
            // 旧表里的 bt2020ncl / bt2020cl / ycgcocn / ycgcocs / bt2020 以及 smpte432 / smpte431
            //（基准名被误当矩阵）经实测**全部被 ffmpeg 拒绝** ⇒ 一旦放行就是整条命令 `-22`、零产物
            //（`Unable to parse "colorspace" option value …`）。规范化与可用性判定都收在 Registry 里。
            // 唯一的界面差异：zscale 认 `gbr`、ffmpeg 认 `rgb`，由 ToFfmpegColorSpaceName 转换。
            // ── 输出标注优先服从**容器能力**（capM）—— 2026-09-16 ──
            // jpg/webp 的能力表写明矩阵只能 bt709（写别的值：mjpeg/libwebp 会**静默忽略**并写自己的默认
            // bt470bg，而 bt2020c/ycgco 更会让 swscale 直接失败）。此前 capM 只用于像素链的目标矩阵，
            // **从不施加于用户的矩阵** ⇒ 用户的 `--color-matrix bt2020nc` 会被原样写进 jpg 的输出标签，
            // 造成「宣称 bt2020nc、实际文件里没有该标注」。现统一由 capM 优先。
            var outMatrixToken = dec.CapMatrix ?? outputColorSpace;
            var ffmpegColorSpace = ColorSpaceRegistry.ToFfmpegColorSpaceName(outMatrixToken);
            // RGB 矩阵族（gbr→rgb）**不写给 YUV 编码器**：libwebp_anim/mjpeg/libaom/libsvtav1 实测一律拒绝
            //（"Undefined constant ... in 'gbr'" ⇒ 0 字节）。RGB 原生格式(png/tiff/apng/jxl)已由
            // isRgbNativeFmt 跳过本块 ⇒ 此处保持旧口径，不写 rgb。
            if (!isRgbNativeFmt
                && ffmpegColorSpace != null
                && !ffmpegColorSpace.Equals("rgb", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-colorspace"); args.Add(ffmpegColorSpace);
            }

            // ── 图片最长边限制（下采样/缩放）──
            // ⚠ **顺序统一（R1，2026-09-18）**：scale 必须插在**第一条色彩变换之前**（= 缩放 → 映射）。
            //   此前走 AppendVideoFilter ⇒ **追加到链尾** ⇒ 映射 → 缩放，与引擎侧
            //   （`RawColorPipeline.cs:94-97` 的 `format=rgb48le,{scale}` ⇒ 缩放 → 映射）**顺序相反**，
            //   而两条顺序实测**不等价**（2×2 对照 PSNR 36.47dB / 最大单通道差 14417）。
            //   ⇒ 改用专用 helper（不复用 AppendVideoFilter：iccgen 段依赖其**在 switch 之后追加**的语义，
            //      见本文件中「`iccgen` 段由 … 在 switch **之后**追加」那段）。
            //   ⚠ 本行的探测入参仍是 `inputPath`；改成 `probeInput` 属批次 2（gap D），本批次**不动**。
            if (options.EnableMaxDimension && options.MaxDimension > 0)
            {
                var scaleFilter = BuildMaxDimensionScaleFilter(options, inputPath);
                if (!string.IsNullOrEmpty(scaleFilter))
                    InsertScaleFilterAtChainHead(args, scaleFilter);
            }

            // ── 单帧容器首帧选择 ──
            // 在输出路径前插入 -frames:v 1，确保多帧输入写单帧容器时仅取首帧
            // （判据见上方 targetHoldsMultipleFrames）。必须在 outputPath 之前才能生效。
            if (insertFramesV1)
            {
                args.Add("-frames:v");
                args.Add("1");
            }

            // ── APNG 编码器必须配 `-f apng`（2026-09-15 实测，属**数据完整性**问题）──
            // 只写 `-c:v apng` 而不指定 `-f apng` 时，`.png` 后缀会让 ffmpeg 走 image2/png 封装器，
            // 产出**没有 PNG 签名、没有 IHDR、也没有 IEND 的裸 APNG 块流**（实测 2404 字节，首字节即 fcTL，
            // ffprobe 报 `Invalid PNG signature`，任何解码器都读不了）——而**退出码是 0**，属静默损坏。
            // 手工对照（同一素材、同一 ffmpeg 构建）：`-c:v apng` ⇒ 缺签名；`-c:v apng -f apng` ⇒ 正常；
            // `-c:v png` / 默认 ⇒ 正常。
            // 为什么按「编码器」而不是「目标格式」判定：CloneOptionsForFfmpeg 把 **png 目标也映射成 apng 编码器**
            // （为让 .png 容器能承载动画）⇒ 此时走的是 case "png" 分支，拿不到 case "apng" 里那句 -f apng。
            // 受影响的三条通路：ProcessCjxlAsync / ProcessCjpegliAsync 的 ffmpeg 回退、JXR→ffmpeg 标准编码。
            if (string.Equals(options.Encoder, "apng", StringComparison.OrdinalIgnoreCase))
            {
                bool hasApngFmt = false;
                for (int i = 0; i + 1 < args.Count; i++)
                {
                    if (args[i] == "-f" && args[i + 1] == "apng") { hasApngFmt = true; break; }
                }
                if (!hasApngFmt) { args.Add("-f"); args.Add("apng"); }
            }

            args.Add($"\"{outputPath}\"");

            // 视频/动图最大时长限制
            if (options.AnimationDuration > 0)
            {
                // 在输出文件前插入 -t（时长限制）
                var outputIdx = args.Count - 1;
                args.Insert(outputIdx, "-t");
                args.Insert(outputIdx + 1, options.AnimationDuration.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // ── ⚠⚠ 2026-09-22（管线审查 §3.4 · **legacy 侧**）：透明输入不得被**静默拍平** ──────────────
            //   引擎侧已修（`RawColorPipeline.BuildEncodeArgs` 的 alpha 分支）；legacy 侧同病：
            //   色彩链先 `format=rgb48le`（**无 alpha 平面**）⇒ 透明被丢。实测（真透明源 + Display P3）：
            //     legacy → png = **`rgb48be`**、webp = **`yuv420p`**（均无 alpha 通道），而引擎侧已是 rgba/argb。
            //   修法沿用引擎**同款**：把**原文件**作第二输入、只取它的 **alpha 平面**，在色彩链**之后** `alphamerge`。
            //   ⚠ **只对带 alpha 的源生效** ⇒ 不透明源（现有全部门禁用例）的命令行**逐字不变**
            //     （可用「改动前后 `[cmd]` 逐字对比」验证 —— 这是本改动的主要回归护栏）。
            //   ⚠ 必须 `-frames:v 1`：底图 `[0:v]` 恒 1 帧，而 alpha 源是**原文件**（多帧输入会给 N 帧）
            //     ⇒ 否则编码器报 `-22`（**该坑 JXR 出口已登记过**，见 `RawColorPipeline.EncodeJxrViaJxrEncAppAsync` 注释）。
            //   ⚠ 必须**强制**给出带 alpha 的 `-pix_fmt`：否则编码器仍按链输出（无 alpha）推断 ⇒ 补了也白补。
            //   ⚠ 选项顺序：`-i` **先**、输出侧选项（`-pix_fmt` / `-frames:v`）**后** ——
            //     放 `-i` 之前会被 ffmpeg 当**输入**选项而失效。
            if (inputHasAlpha && !skipGenericPixFmt && fmt is "png" or "apng" or "tiff" or "tif" or "webp")
            {
                var vfIdx2 = args.FindIndex(a => a == "-vf");
                if (vfIdx2 >= 0 && vfIdx2 + 1 < args.Count)
                {
                    var chain = args[vfIdx2 + 1];
                    var aBd = options.BitDepth.HasValue ? options.BitDepth.Value : ProbeInputBitDepth(probeInput);
                    var alphaFmt = ClampAvifBd(aBd) <= 8 ? "rgba" : "rgba64le";   // 与链输出同深，否则 alphamerge 无法协商
                    var graph = $"[1:v]format={alphaFmt},alphaextract[a];[0:v]{chain}[b];[b][a]alphamerge";
                    args[vfIdx2] = "-filter_complex";
                    args[vfIdx2 + 1] = graph;
                    // ⚠⚠ 第二输入 `-i` **必须紧跟在第一个 `-i` 之后** —— 插到输出选项之后会被 ffmpeg
                    //   当作**输入**选项去解析（实测报 `Error parsing options for input file` + `-22`）。
                    var iIdx = args.FindIndex(a => a == "-i");
                    if (iIdx >= 0 && iIdx + 1 < args.Count)
                    {
                        args.Insert(iIdx + 2, "\"" + inputPath + "\"");
                        args.Insert(iIdx + 2, "-i");
                    }
                    // 输出侧选项（`-pix_fmt` / `-frames:v`）留在**输出路径之前**（输出路径恒在最后）。
                    // ⚠⚠ **`-frames:v 1` 必须复用既有的 `insertFramesV1` 判据**，不能无条件加 ——
                    //   否则**多帧容器**（gif/webp/avif/apng、`jxl + AnimationFps`）会被**静默截成 1 帧**：
                    //   实测（首版就是这么写的）`anim.webp → webp` 从 **10 帧变 1 帧**，
                    //   被 `verify-anim-static-target` ③ 与 `verify-webp-anim-probe` ③(b) 双双抓住。
                    var extra = new List<string> {
                        "-pix_fmt", MapPixFmt(options.Format, options.Chroma, ClampAvifBd(aBd), true, options.ColorRange)
                    };
                    if (insertFramesV1) { extra.Add("-frames:v"); extra.Add("1"); }
                    args.InsertRange(args.Count - 1, extra);
                }
            }

            return string.Join(" ", args);
        }

        /// <summary>
        /// 计算「图片最长边限制」的 scale 滤镜字符串（复用入口）。
        /// 供 FFmpeg 后端以及其他需要手工拼接 ffmpeg 滤镜链的路径（cjxl/cjpegli 管道等）调用。
        /// 返回 null 表示无需缩放（未启用 / 最长边未超限 / 探测失败）。
        /// </summary>
        public static string? BuildMaxDimensionScaleFilter(FfmpegOptions options, string inputPath)
        {
            // 前置判断：未启用或参数非法 → 直接返回 null（不缩放）
            if (!options.EnableMaxDimension || options.MaxDimension <= 0)
                return null;

            var maxLen = options.MaxDimension;
            var meta = GetCachedProbe(inputPath);
            var w = meta.width;
            var h = meta.height;

            // 宽高探测失败（管道 stdin 或 ffprobe 不可用）→ 跳过缩放，避免破坏转码
            if (w <= 0 || h <= 0)
                return null;

            // 最长边判定（宽高相等时视同横向处理）
            var longest = Math.Max(w, h);

            // 条件跳过：最长边 ≤ 预设参数 → 直接使用原图
            if (longest <= maxLen)
                return null;

            // ── 最优滤镜写法：锁定最长边，另一边 -2（自动偶数）──
            // 相比 "scale=W:H:force_original_aspect_ratio=decrease"：
            //   ① 精确 W:H 会因浮点舍入产生与源宽高比的微小偏差，而 decrease
            //      又把尺寸限制到适配框内，导致输出最长边可能小于 maxLen；
            //   ② 只锁最长边 + -2 让 ffmpeg 完全按源宽高比自动求另一条边，
            //      宽高比严格保持，且输出最长边恒等于 maxLen（不漂移）。
            // -2 表示自动计算并强制偶数：兼容 YUV 4:2:0 子采样与 AV1 等
            // 要求偶数尺寸的编码器（奇数宽高会报错）。
            // flags=lanczos：高质量下采样（明显优于默认 bilinear）。
            // 注意：scale=W:H 中 W 是宽度、H 是高度——横向图（w>=h）最长边是宽度，
            // 必须锁宽度 scale={maxLen}:-2（实机复测：3000x2250 限 2000 → 2000x1500）。
            return w >= h
                ? $"scale={maxLen}:-2:flags=lanczos"   // 横向/方形：锁宽度（最长边），高自动
                : $"scale=-2:{maxLen}:flags=lanczos";  // 纵向：锁高度（最长边），宽自动
        }

        /// <summary>
        /// 分离色彩参数：(primaries, trc) 放 -i 前作输入覆盖，(colorspace) 放 -i 后作输出矩阵。
        /// 关键原则：-i 前的 -color_primaries/-color_trc 必须描述「实际输入」的色彩空间，
        /// 而非目标色彩空间。目标转换由 zscale 滤镜完成（zscale 输出帧自动携带目标标签）。
        /// </summary>
        private static (string? primaries, string? trc, string? colorspace) BuildColorArgsSplit(
            Models.FfmpegOptions options, string inputPath)
        {
            string? primaries = null, trc = null, colorspace = null;

            // Ultra HDR 解码输出：中间格式为线性 sRGB PFM（1.0=SDR 白点 203nits，峰值见 DecodedUltraHdrPeakNits）。
            // 声明为 bt709 primaries + linear 传递（不再误标 bt2020/PQ —— 数据是线性 sRGB 而非 PQ 编码）。
            if (!string.IsNullOrWhiteSpace(options.DecodedUltraHdrColorSpace))
            {
                primaries = "bt709";
                trc = "linear";
                colorspace = "bt709";
                return (primaries, trc, colorspace);
            }

            // 高级参数模式 或 管线注入的输入色彩声明（如 RAW 预处理标记 bt709/linear）。
            // 关键修复：当 UseAdvancedColorParameters=true 且 ColorPrimaries/ColorTrc 至少有一个被设置时，
            // 视为用户/预处理明确声明了输入色彩，直接使用，不再探测输入文件。
            // 这确保 RAW 预处理注入的 bt709/linear 以及用户手动设置的 CP/CT 优先于 ColorSpace 探测。
            if (options.UseAdvancedColorParameters
                 && (!string.IsNullOrWhiteSpace(options.ColorPrimaries)
                     || !string.IsNullOrWhiteSpace(options.ColorTrc)
                     || !string.IsNullOrWhiteSpace(options.ColorMatrix)))
            {
                primaries = options.ColorPrimaries;
                trc = options.ColorTrc;
                colorspace = options.ColorMatrix;
            }
            else if ((!options.UseAdvancedColorParameters
                 && !string.IsNullOrWhiteSpace(options.ColorPrimaries)
                     && !string.IsNullOrWhiteSpace(options.ColorTrc))
                && (!string.IsNullOrWhiteSpace(options.ColorPrimaries)
                 || !string.IsNullOrWhiteSpace(options.ColorTrc)
                 || !string.IsNullOrWhiteSpace(options.ColorMatrix)))
            {
                // 兼容旧逻辑：未开启高级模式但手动设置了 CP/CT 时也视为输入声明
                primaries = options.ColorPrimaries;
                trc = options.ColorTrc;
                colorspace = options.ColorMatrix;
            }
            else if (!string.IsNullOrWhiteSpace(options.ColorSpace)
                     && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                // ── 简化模式：返回「实际输入」色彩（非目标）──
                // 探测输入文件的真实色彩元数据，用于 -i 前的输入声明。
                // 目标色域转换由后续的 zscale 滤镜完成。
                var hdrMeta = ProbeInputColorMetadata(inputPath);
                // 输入声明必须如实描述「实际输入」色彩。满足其一即传播探测到的 CICP：
                //   >8bit（HDR/高位深常见）、HDR 传递(PQ/HLG)、或广色域 primaries(bt2020/P3)。
                // 修复：旧 `bitDepth>8` 单条件漏掉 8-bit cICP HDR/广色域源（如 8-bit PNG 带
                //   cICP=bt2020/smpte2084）→ 被误声明为 SDR：① 跳过 HDR→SDR tonemap（高光裁剪/变暗）；
                //   ② iccgen 作用于仍带 smpte2084 标签的帧 → "Not yet implemented" → 0 字节输出。
                //   与 auto 分支口径统一，直接用 CICP tokens 判定（不依赖 ICC 推断的 sourceGamutName）。
                bool srcIsHdr = hdrMeta.colorTrc is "smpte2084" or "arib-std-b67";
                bool srcIsWide = hdrMeta.colorPrimaries is "bt2020" or "smpte432" or "smpte431";
                if ((hdrMeta.bitDepth > 8 || srcIsHdr || srcIsWide)
                    && !string.IsNullOrWhiteSpace(hdrMeta.colorPrimaries)
                    && !string.IsNullOrWhiteSpace(hdrMeta.colorTrc))
                {
                    // 输入有明确色彩元数据（HDR / 广色域 / 高位深）→ 如实声明
                    primaries = hdrMeta.colorPrimaries;
                    trc = hdrMeta.colorTrc;
                    colorspace = hdrMeta.colorSpace;
                }
                else
                {
                    // 输入无色彩元数据（如 16-bit TIFF 无标签）→ 假定 sRGB
                    primaries = "bt709";
                    trc = "iec61966-2-1";
                    colorspace = "bt709";
                }
            }
            else if (string.IsNullOrWhiteSpace(options.ColorSpace)
                     || options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var hdrMeta = ProbeInputColorMetadata(inputPath);
                // 传播探测到的输入 primaries/trc：>8bit 或 广/超广色域源均传播（旧代码仅 >8bit，
                // 导致 8-bit Display-P3/DCI 等常见广色域源丢 CICP/ICC 描述）。
                var srcPrim = ColorSpaceRegistry.FfmpegTokens(hdrMeta.sourceGamutName).primaries;
                bool wideSource = !string.IsNullOrEmpty(srcPrim) && srcPrim != "bt709";
                // cICP-only 源（无 ICC → sourceGamutName 空，如带 cICP 的 8-bit HDR/广色域 PNG）：
                // 直接用探测到的 CICP tokens 判定，否则 8-bit HDR 源在 auto 模式被当 SDR →
                // 跳过 tonemap + iccgen 作用于 HDR 帧（"Not yet implemented"）→ 编码失败/0 字节（与简化分支同源修复）。
                bool cicpHdr = hdrMeta.colorTrc is "smpte2084" or "arib-std-b67";
                bool cicpWide = hdrMeta.colorPrimaries is "bt2020" or "smpte432" or "smpte431";
                if (hdrMeta.bitDepth > 8 || wideSource || cicpHdr || cicpWide)
                {
                    primaries = hdrMeta.colorPrimaries;
                    trc = hdrMeta.colorTrc;
                    colorspace = hdrMeta.colorSpace;
                }
            }

            return (primaries, trc, colorspace);
        }

        /// <summary>将 UI 简化色彩空间名称映射为 (primaries, trc, matrix)（委托 ColorSpaceRegistry，与管道/烘焙同源同义）</summary>
        private static (string? primaries, string? trc, string? matrix) MapSimplifiedColorSpace(string displayName)
            => ColorSpaceRegistry.FfmpegTokens(displayName);

        private static string MapColorSpace(string displayName)
        {
            var (_, _, m) = ColorSpaceRegistry.FfmpegTokens(displayName);
            return m ?? displayName;
        }

        /// <summary>
        /// CICP 令牌 → ITU-T H.273 编号（cICP chunk 要的是字节）。
        /// ⚠ 不凭记忆：本表由 <c>tests/scripts/verify-png3-interop.ps1</c> 的“写 cICP(N) → ffprobe 读回令牌”
        /// 循环验证（ffmpeg 为 oracle）；任一不匹配则那条断言失败。
        /// </summary>
        public static bool TryGetCicpNumbers(string? primaries, string? trc, out byte p, out byte t)
        {
            p = 0; t = 0;
            if (primaries is null || trc is null) return false;
            if (!PrimariesToCicp.TryGetValue(primaries, out var pv) || !TransferToCicp.TryGetValue(trc, out var tv)) return false;
            p = pv; t = tv; return true;
        }

        /// <summary>
        /// ICC 实测曲线 → CICP 传递令牌：**仅当与某标准曲线等价**时返回其 token，否则 null（不猜）。
        /// 用于 S2“源只有 ICC”时判定该 ICC 是否就是标准色彩空间 ICC（色度由 DetectIccPrimaries 管，曲线由此管）。
        /// </summary>
        public static string? CicpTokenOfCurve(FfmpegGui.Services.ColorMapping.TransferCurve? c)
        {
            if (c == null) return null;
            // TransferCurve 是 struct ⇒ 参数 TransferCurve? 即 Nullable<T>，不能直接访问成员，需 .Value。
            var cv = c.Value;
            // 本文件未 using ColorMapping，故完全限定（也不能 `var T = 类型名`，类型不是值）。
            if (cv.Equivalent(FfmpegGui.Services.ColorMapping.TransferCurve.Srgb))    return "iec61966-2-1";
            if (cv.Equivalent(FfmpegGui.Services.ColorMapping.TransferCurve.Bt709))    return "bt709";
            if (cv.Equivalent(FfmpegGui.Services.ColorMapping.TransferCurve.Pq))       return "smpte2084";
            if (cv.Equivalent(FfmpegGui.Services.ColorMapping.TransferCurve.Hlg))      return "arib-std-b67";
            if (cv.Equivalent(FfmpegGui.Services.ColorMapping.TransferCurve.Linear))   return "linear";
            if (cv.Equivalent(FfmpegGui.Services.ColorMapping.TransferCurve.Gamma22))  return "bt470m";
            return null;   // 如 Gamma18/26/28、ProPhoto、Smpte240M 等：无对应或不精确 ⇒ 不命名
        }

        private static readonly System.Collections.Generic.Dictionary<string, byte> PrimariesToCicp = new() {
            ["bt709"] = 1, ["bt470m"] = 4, ["bt470bg"] = 5, ["smpte170m"] = 6, ["smpte240m"] = 7,
            ["film"] = 8, ["bt2020"] = 9,
            // ⚠ 靠 ffprobe 循环验证纠正：smpte431=11、smpte432=12（Display P3(D65) 是 **12**）。
            //   曾背成 11=smpte432 —— 那会把 Display P3 标成 DCI-P3 影院白点，属实质错标签。
            ["smpte431"] = 11, ["smpte432"] = 12,
        };

        private static readonly System.Collections.Generic.Dictionary<string, byte> TransferToCicp = new() {
            ["bt709"] = 1, ["bt470m"] = 4, ["bt470bg"] = 5, ["smpte170m"] = 6, ["smpte240m"] = 7,
            ["linear"] = 8, ["log"] = 9, ["log-sqrt"] = 10, ["iec61966-2-4"] = 11, ["bt1361"] = 12,
            ["iec61966-2-1"] = 13, ["bt2020-10"] = 14, ["bt2020-12"] = 15,
            ["smpte2084"] = 16, ["smpte428"] = 17, ["arib-std-b67"] = 18,
        };

        /// <summary>
        /// 容器色彩能力判定（唯一真源）：返回 (primaries, trc, matrix, maxBitDepth, useIcc) 约束。
        /// capMaxBitDepth 如 JPEG=8；capUseIcc 表示非 CICP 格式需 iccgen 携带 ICC（TIFF/JPEG/WebP）。
        /// 注意：返回 null 表示“不约束”，非“不支持”。
        /// </summary>
        private static (string? primaries, string? trc, string? matrix, int maxBitDepth, bool useIcc) GetFormatColorCapabilities(string format, Models.FfmpegOptions options)
        {
            var fmt = format.ToLowerInvariant();

            // JPEG (mjpeg/cjpegli)：有 APP2 ICC 通路 ⇒ **SDR 广色域（Display P3 / Adobe RGB / SDR-BT.2020）可经 ICC 表达**，
            // 故不再钳 primaries（旧写法把“未实现的路径”误写成了能力约束）；矩阵仍按 Y'CbCr 惯例钳 bt709。
            // 真正的硬边界只有两条：① 位深 8；② **HDR 传递无法由 JPEG 自身标定** ——
            // 除非开启 GainMap (Ultra HDR)：此时 HDR 由“SDR 底 + 增益图”承载，允许 PQ/HLG 目标。
            if (fmt is "jpg" or "jpeg")
            {
                return (null, options.JpegGainMap ? null : "iec61966-2-1", "bt709", 8, true);
            }

            // WebP (libwebp): **容器无 CICP 通路，但 RIFF 有 ICCP 块** ⇒ 非 sRGB 原色域可经 ICC 交付，
            // 不钳 primaries（旧写法把“写 -colorspace 会 EINVAL”误写成了“不能交付 P3”；EINVAL 的只是矩阵标签）。
            //   实测（本仓库 2026-09-14）：iccgen 生成的 P3 ICC **会被 libwebp 编码器丢弃**（产物内无 ICCP 块），
            //   而用 exiftool 后置嵌入同一 ICC 可正常读回 ⇒ 由裁决点 NeedsPostEmbedIcc 让队列补上那一步。
            // 仍钳的两项是真硬边界：trc 只能 SDR（无 HDR 标定通路）、matrix 只能 bt709（写其他值 EINVAL）。
            if (fmt == "webp")
            {
                return (null, "iec61966-2-1", "bt709", 8, true);
            }

            // TIFF: 仅 ICC 风格色彩管理；不支持 CICP color_primaries。
            // 实测：TIFF + P3(SDR) 可通过 ICC 正常表达；但 TIFF + PQ/HLG (HDR trc) 编码失败。
            // iccgen 仅支持标准 sRGB (bt709/iec61966-2-1)，不支持 HDR 标签。
            // TIFF 原生支持 ICC Profile 元数据，应该通过 -map_metadata 保留，而非 iccgen。
            // → capUseIcc = false，依赖元数据复制而非 iccgen。
            if (fmt is "tiff" or "tif")
            {
                return (null, "iec61966-2-1", null, 16, false);
            }

            // AVIF: 支持 HDR + HDR 色域；位深上限**只按实际会跑的编码器**取（libaom/av1_nvenc 12、
            // 其余 10，见 `ImageEncoderArgs.AvifMaxBitDepthForEncoder`，任务 #36）。
            // ❌ **2026-09-26 任务 #39：删掉原先这条特例**
            //   `if (options.ColorSpace == "Display P3" && (options.BitDepth ?? 8) > 8)
            //        return ("bt709", "iec61966-2-1", "bt709", 8, false);`
            // 它的理由只是注释里那句"Display P3 + 10/12/16-bit 在 libaom 上有回归风险（保守策略）"，
            // 全仓**没有任何实测记录**。补做的实测（取证 `tests/output/t36/probe_39b.ps1`）拿
            // **产品自己那条** avif+P3 命令行逐字复用、**只换 `-pix_fmt`**：
            //   yuv420p     rc=0 5505 B  ffprobe primaries=smpte432/trc=iec61966-2-1/matrix=bt709
            //   yuv420p10le rc=0 5595 B  同一套标签    PSNR(对 8bit 产物)=51.45 dB
            //   yuv420p12le rc=0 5435 B  同一套标签    PSNR(对 8bit 产物)=50.99 dB
            //   （stderr 全程无 "not support / Undefined / Invalid"）
            // ⇒ "回归"不可复现：三档都编得出来、nclx 标签都落得进去、像素与 8bit 档同源。
            // ⚠ 顺带量到那条特例**连自己声明的都没做到**：它写"回退 sRGB"，实际产物标签仍是 P3 ——
            //   nclx 来自 zscale 写进帧的色彩属性，而特例给的 capP/capT 只在 tonemap 链
            //   （`tmDstP = capP ?? tmDstP`）被消费 ⇒ 真正咬人的只有 `capBd=8` 那一项，
            //   也就是"无据削掉用户明确要的位深"。⇒ 撤掉；位深上限统一交回编码器能力那条唯一真值。
            if (fmt == "avif")
            {
                return (null, null, null, ColorMapping.ImageEncoderArgs.AvifMaxBitDepth(options), false);
            }

            // PNG / JPEG XL / JPEG XR / APNG / GIF: 无色彩/位深硬限制
            return (null, null, null, 16, false);
        }

        // ⚠ 质量映射的**单一真值**已移到 ImageEncoderArgs（传统管线与自研引擎共用同一份数值，
        //   避免两处漂移导致"切换管线后产物对不上"）。此处仅转发，语义一字未改。
        private static int MapJpegQuality(int quality) => ColorMapping.ImageEncoderArgs.MapJpegQuality(quality);

        private static int MapPngCompression(int quality) => ColorMapping.ImageEncoderArgs.MapPngCompression(quality);

        // ⚠ AVIF 的 crf 映射**单一真值**同样在 ImageEncoderArgs（2026-09-17 搬移），此处仅转发。
        private static int MapAvifCrf(int quality) => ColorMapping.ImageEncoderArgs.MapAvifCrf(quality);

        // ⚠ JXL 的 distance 映射**单一真值**在 ImageEncoderArgs（2026-09-17 搬移），此处仅转发。
        private static double MapJxlDistance(int quality) => ColorMapping.ImageEncoderArgs.MapJxlDistance(quality);

        private static double MapJpegliDistance(int quality) => ColorMapping.ImageEncoderArgs.MapJpegliDistance(quality);

        /// <summary>
        /// 根据输出格式、色度子采样、位深与输入透明通道映射 pix_fmt。
        /// 色度子采样在全部位深（8/10/12/16）下均生效。
        /// </summary>
        // ⚠ 2026-09-17：像素格式映射（`MapPixFmt`）**单一真值**搬到 ImageEncoderArgs，
        //   传统管线与自研引擎出口共用同一份（AVIF 接入引擎后必须共用，否则同一个源两条管线
        //   会给出不同的色度子采样/位深）。此处仅转发，逻辑一字未改。
        private static string MapPixFmt(string format, string? chroma, int bitDepth, bool hasAlpha, string? colorRange)
            => ColorMapping.ImageEncoderArgs.MapPixFmt(format, chroma, bitDepth, hasAlpha, colorRange);

        /// <summary>
        /// 解析输出色彩范围：用户显式 tv/pc 优先；
        /// auto（默认）跟随输入范围——RGB/yuvj 输入 → pc，yuv limited 输入 → tv，未知 → 不覆盖（ffmpeg 默认）。
        /// </summary>
        private static string? ResolveOutputColorRange(string? userRange, string? inputRange)
        {
            if (!string.IsNullOrWhiteSpace(userRange)
                && (userRange.Equals("tv", StringComparison.OrdinalIgnoreCase)
                 || userRange.Equals("pc", StringComparison.OrdinalIgnoreCase)))
            {
                return userRange.ToLowerInvariant();
            }
            // auto：跟随输入范围（"pc"/"tv"/null）
            return inputRange;
        }

        /// <summary>
        /// GIF → AVIF 透明通道像素格式映射。
        /// 根据用户设置的色度采样和位深，选择对应的 yuva 格式。
        /// </summary>
        private static string MapAvifAlphaPixFmt(FfmpegOptions options)
        {
            var bd = options.BitDepth ?? 8;
            var chroma = options.Chroma ?? "auto";

            if (bd <= 8)
            {
                return chroma switch
                {
                    "4:4:4" => "yuva444p",
                    "4:2:2" => "yuva422p",
                    _ => "yuva420p",
                };
            }

            // 高位深：仅 4:2:0 有广泛编码器支持
            if (bd == 10) return "yuva420p10le";
            // 12/16-bit 编码器支持有限，回退到 10-bit
            return "yuva420p10le";
        }

        /// <summary>
        /// 供**自研引擎出口**复用同一份输入位深探测（<c>RawColorPipeline</c> 的 AVIF 位深决策要用它）。
        /// 走 <c>GetCachedProbe</c>，不额外起进程、与上面的私有实现同一份缓存。
        /// </summary>
        public static int ProbeInputBitDepthForEngine(string inputPath) => ProbeInputBitDepth(inputPath);

        /// <summary>使用 ffprobe 同步探测输入文件的位深（复用缓存，不额外起进程）</summary>
        private static int ProbeInputBitDepth(string inputPath)
        {
            // 管道输入（stdin）无法探测，直接返回 0
            if (inputPath == "-") return 0;
            // 复用 GetCachedProbe：ProbeInputColorMetadataCore 已同时探测 bits_per_raw_sample 与 pix_fmt
            return GetCachedProbe(inputPath).bitDepth;
        }

        /// <summary>从像素格式字符串中提取位深（正确处理 yuvj420p→8, yuv420p10le→10, rgb48le→16 等）</summary>
        private static int ParseBitDepthFromPixFmt(string? pixFmt)
        {
            if (string.IsNullOrWhiteSpace(pixFmt)) return 0;
            // 常见格式:
            //   rgb48le/rgb48be → 48/3 = 16
            //   rgba64le → 64/4 = 16
            //   yuv420p10le → 10（p 后的数字）
            //   yuv444p12le → 12
            //   gray16le → 16
            //   yuvj420p / yuv420p → 8（无显式位深 = 8）
            // 注意：不能简单提取第一个数字（yuvj420p 的 "420" 是子采样，不是位深）

            // 优先匹配 "p<N>"（YUV planar 位深）或 "gray<N>"/"ya<N>"（灰度位深）
            var planar = System.Text.RegularExpressions.Regex.Match(pixFmt, @"p(\d+)(le|be)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (planar.Success)
                return int.Parse(planar.Groups[1].Value);

            var gray = System.Text.RegularExpressions.Regex.Match(pixFmt, @"^(gray|ya)(\d+)(le|be)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (gray.Success)
                return int.Parse(gray.Groups[2].Value);

            // RGB 系列: rgb48le → 48/3=16, rgba64le → 64/4=16, rgb24 → 8
            if (pixFmt.StartsWith("rgb") || pixFmt.StartsWith("bgr") || pixFmt.StartsWith("gbr"))
            {
                var num = System.Text.RegularExpressions.Regex.Match(pixFmt, @"(\d+)");
                if (num.Success)
                {
                    var val = int.Parse(num.Value);
                    var divisor = (pixFmt.StartsWith("rgba") || pixFmt.StartsWith("bgra") || pixFmt.StartsWith("argb")) ? 4 : 3;
                    return val / divisor;
                }
            }

            // YUV 无位深后缀（yuv420p/yuvj420p 等）→ 8-bit
            if (pixFmt.StartsWith("yuv") || pixFmt.StartsWith("yuva") || pixFmt.StartsWith("nv"))
                return 8;

            // 兜底：数字后跟 le/be（如 gray16le 已处理，其他格式）
            var tail = System.Text.RegularExpressions.Regex.Match(pixFmt, @"(\d+)(le|be)$");
            if (tail.Success) return int.Parse(tail.Groups[1].Value);

            return 0;
        }

        /// <summary>
        /// 判断是否需要 HDR→SDR 色调映射。
        /// 仅在输入明确为 HDR（PQ/HLG 传输函数）且目标为 SDR 时触发。
        /// 16-bit 无元数据 TIF 是普通高位深 SDR，不需要 tonemap。
        /// </summary>
        private static bool NeedsHdrToSdrTonemap(FfmpegOptions options, string inputPath,
            string? inputPrimaries, string? inputTrc)
        {
            var fmt = options.Format.ToLower();

            // 「用户有没有显式指定目标」—— 两条 HDR 原生容器的豁免支（下面的 avif/jxl/jxr 与 PNG/APNG）
            // **共用同一个计算**，不要各写一份（#43 的根因就是其中一份写成了无条件豁免）。
            bool noExplicitTarget = !options.UseAdvancedColorParameters
                && (string.IsNullOrWhiteSpace(options.ColorSpace)
                    || options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase));

            // 这些格式原生支持 HDR（>8bit），不需要 tonemap
            // 注意：TIFF 能力约束已将 trc 钳为 SDR (iec61966-2-1)，因此 TIFF 需 tonemap（见下方目标色彩判断）。
            // ⚠ **任务 #43（2026-09-27）**：这一支原先是**无条件** `return false`，于是"用户显式要 SDR 目标"
            //   也被一起豁免 —— 与下面 PNG/APNG 支注释里记的那次修复是**同一个错**，只是这三个格式的后果不同：
            //   实测（HDR/PQ 源，`--color-engine legacy`，矩阵 `tests/output/t43/matrix_pre.log` +
            //   可信复测 `tests/output/t43/matrix2_pre_measure.log`，逐格带哈希）：
            //     · avif：目标 ∈ {Display P3, sRGB, BT.2020} 三档全部
            //       `Error while filtering: Not yet implemented`（= HDR 帧被塞给 iccgen）⇒ 非零退出、零产物；
            //       本支**正是**这一族的因 —— 修后三档分别出货 4169B/smpte432、4281B/bt709、4725B/bt2020。
            //     · jxl（**已排除在本支之外，别再记在这儿**）：三个目标各得到**同一份
            //       18235 B / 387E3F40D905A96B、标签仍是 bt2020** 的产物 ⇒ 目标被静默吃掉，症状更坏；
            //       但改前改后**逐字节不变**，因为 legacy 的 jxl 走的是「cjxl 直接编码」旁路
            //       （日志里连 ffmpeg 的 `[cmd]` 都没有，命令行是 `cjxl.exe 源.png 目标.jxl -x color_space=DisplayP3`）
            //       ⇒ 本函数根本不在那条路上。真正的因是 P7a-3 未接线的色彩后置旁路，另记任务 #44。
            //     · jxr：同一族的**第三条**旁路（`[jxr] Step 2: JxrEncApp 编码 JPEG XR...` 两步法），
            //       auto 与显式 P3 得到**同一份 152166 B / 20D3E0175E9D12D8**、标签 `unknown`，
            //       中间那条 ffmpeg 命令行里 tonemap/zscale/iccgen 全无 ⇒ 本支同样管不到它，一并记 #44。
            //   ⇒ 现在只在「未指定目标」时保留 HDR 直通；显式目标继续往下走 tonemap 判定
            //     （HDR 目标如 `P3 PQ` 仍被下面"目标为 HDR ⇒ 不 tonemap"那段拦住，auto 与 P3 PQ 两列
            //     的交付**必须逐字不变**，那是这次修改的"不误伤"证据）。
            //   ⚠ **不照抄 PNG 的 `bd > 8` 前提**：那是 PNG 自己"能不能标 HDR"的代理；
            //     AVIF/JXL 在 8bit 下同样能用 nclx 标 PQ ⇒ 抄过来会凭空改掉一批 auto 格。
            //   ⚠ 本函数返回的是"**要不要** tonemap"，所以"保留 HDR 直通"= `return false`。
            //     第一版写成 `return noExplicitTarget` ⇒ 恰好把两列同时搞反：auto 格被强加 tonemap
            //     （产物 5247 B → 4281 B），而三个显式目标格命令行**逐字未变**（仍然 iccgen 失败）。
            //     pre/post 矩阵当场抓出 ⇒ 改这里必须跑矩阵，别看代码顺眼。
            if (fmt is "avif" or "jxl" or "jxr")
            {
                if (noExplicitTarget) return false;
            }
            // PNG >8bit 可承载 HDR，但**前提是输出被正确标注**（cICP 由 PngCicpService 后置写入）。
            // 旧写法无条件 return false，会把“用户显式要 SDR 目标（如 Display P3）”也一起豁免：
            // 结果既不 tonemap（本函数返 false）又被目标转换块排除（srcIsHdr && !dstIsHdr）
            // → PQ 像素塞进 SDR 标签容器，iccgen 处理不了 HDR 帧标签而直接转换失败。
            // 现在只在「未指定目标（auto）」时保留 HDR；显式 SDR 目标继续往下走 tonemap 判定。
            if (fmt is "png" or "apng")
            {
                var bd = options.BitDepth ?? ProbeInputBitDepth(inputPath);
                if (bd > 8 && noExplicitTarget) return false;
            }

            // ── 目标色彩空间检查：目标为 HDR (PQ/HLG) 时不需要 tonemap ──
            // HDR→HDR 转换（如 BT.2020 PQ→P3 PQ）由简化 zscale 分支处理；
            // 若此处不排除，tonemap 会先把 HDR 压成 SDR、zscale 再按 HDR 解译，
            // 造成双重转换错误。2026-08-14 修复。
            // SDR-only 格式（JPEG/WebP/TIFF）：即使目标声明 HDR trc 也只能表达 SDR → 必须 tonemap。
            // 与目标转换块 dstT=capTrc 的钳制口径统一。修复：旧代码仅钳 TIFF，JPEG/WebP + HDR 目标
            // → 既不 tonemap（本函数 return false）也不 zscale 转换（目标块 srcIsHdr&&!dstIsHdr 排除）
            // → PQ 像素直塞 8-bit SDR 容器 → 高光裁剪 + 整体变暗。
            bool fmtSdrOnly = fmt is "tiff" or "tif" or "jpg" or "jpeg" or "webp";
            if (options.UseAdvancedColorParameters)
            {
                if (!fmtSdrOnly && options.ColorTrc is "smpte2084" or "arib-std-b67")
                    return false;
            }
            else if (!string.IsNullOrWhiteSpace(options.ColorSpace)
                     && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var target = MapSimplifiedColorSpace(options.ColorSpace);
                var dstTrc = fmtSdrOnly ? "iec61966-2-1" : (target.trc ?? "");
                if (dstTrc is "smpte2084" or "arib-std-b67")
                    return false;
            }

            // 仅当输入明确为 HDR（PQ/HLG 传输函数）时才触发 tonemap
            // 16-bit 无元数据 TIF 不满足此条件
            if (!string.IsNullOrWhiteSpace(inputTrc))
            {
                return inputTrc.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
                    || inputTrc.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>探测输入文件的色彩范围：RGB/yuvj/grayj → "pc"（全范围），yuv（limited）→ "tv"，未知 → null（不覆盖默认）。复用缓存，不额外起进程。</summary>
        private static string? ProbeInputColorRange(string inputPath)
        {
            // 管道输入（stdin）无法探测，直接返回 null
            if (inputPath == "-") return null;
            // 由 GetCachedProbe 的 colorRange 标记判断（pix_fmt 推断，见 ProbeInputColorMetadataCore）
            return GetCachedProbe(inputPath).colorRange;
        }

        public struct ColorMetadata
        {
            public int bitDepth;
            public string? colorPrimaries;
            public string? colorTrc;
            public string? colorSpace;
            /// <summary>输入色彩范围（pix_fmt 推断）："pc"=全范围, "tv"=limited, null=未知</summary>
            public string? colorRange;
            /// <summary>输入是否含透明通道（由 pix_fmt 推断）</summary>
            public bool hasAlpha;
            /// <summary>输入是否为 RGB 系列像素格式（rgb/bgr/gbr/rgba 等，用于 pc range 判断）</summary>
            public bool isRgb;
            /// <summary>色彩语义是否来自 ICC/EXIF（true 时上层应携带 ICC 而非仅靠 primaries/trc 标签）</summary>
            public bool hasIccSemantics;
            /// <summary>探测到的源色彩空间规范名（如 "Adobe RGB"/"ProPhoto RGB"），供超广色域归一化到 BT.2020 判定</summary>
            public string? sourceGamutName;
            /// <summary>源峰值亮度 (nits)，来自 HDR 静态元数据；0=未读到（调用方必须回退到源曲线名义峰值并写明来源）。</summary>
            public double peakNits;
            /// <summary>peakNits 的来源（"MaxCLL" / "BS.2086 mastering"，null=未读到）——日志须能自证峰值不是猜的。</summary>
            public string? peakSource;
            /// <summary>图像宽度（像素），0 表示未成功探测</summary>
            public int width;
            /// <summary>图像高度（像素），0 表示未成功探测</summary>
            public int height;
        }

        // ── ffprobe 探测缓存：同一输入文件在短时间内只探测一次 ──
        // 此前 BuildArguments 内部对同一文件最多起 3 次 ffprobe 进程（色彩/位深/范围），
        // 每条转换任务额外增加 ~30ms×N 进程开销。合并为单次查询 + 10 秒缓存。
        private static readonly Dictionary<string, (DateTime Time, ColorMetadata Meta)> ProbeCache = new();
        private const int ProbeCacheMax = 256;
        private const double ProbeCacheTtlSeconds = 10.0;
        private static readonly object ProbeCacheLock = new();

        /// <summary>带缓存的探测入口：同一输入 10 秒内只起一次 ffprobe 进程</summary>
        /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26`：上游 15 个调用点**全部**持有 `ct`，此前一条都到不了这里）</param>
        private static ColorMetadata GetCachedProbe(string inputPath,
            Action<string>? log = null, CancellationToken ct = default)
        {
            if (inputPath == "-") return default;
            lock (ProbeCacheLock)
            {
                if (ProbeCache.TryGetValue(inputPath, out var entry)
                    && (DateTime.UtcNow - entry.Time).TotalSeconds < ProbeCacheTtlSeconds)
                {
                    return entry.Meta;
                }
            }

            var meta = ProbeInputColorMetadataCore(inputPath, log, ct);

            // ⚠ `#26` 已知取舍：取消时 `meta` 是**退化值**（与探测失败同一形态：bitDepth=0 / 各 token=null，
            //   调用方按「未知」回退），且它会被写进缓存。本缓存有 **10 秒 TTL**（`ProbeCacheTtlSeconds`）
            //   ⇒ 最长 10 秒后自愈，**不会永久毒化**。对比 `QueueProcessor.AnimatedJxlCache`（无 TTL）——
            //   那一处已在 `#26` 报告里单独登记为设计岔口。
            lock (ProbeCacheLock)
            {
                if (ProbeCache.Count >= ProbeCacheMax)
                    ProbeCache.Clear();  // 简单清空（缓存仅作短期去重，无需 LRU）
                ProbeCache[inputPath] = (DateTime.UtcNow, meta);
            }
            return meta;
        }

        /// <summary>使用 ffprobe/exiftool 同步探测输入文件的色彩元数据（带缓存，见 GetCachedProbe）</summary>
        /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26`：上游持有 `ct` 却没传下来，本次接通）</param>
        public static ColorMetadata ProbeInputColorMetadata(string inputPath,
            Action<string>? log = null, CancellationToken ct = default)
            => GetCachedProbe(inputPath, log, ct);

        /// <summary>
        /// 点三：判定并解析超广色域「像素原样 + 仅靠 ICC 描述」的保真 ICC。全部条件满足才生效：
        /// ① 源色域为 ProPhoto/ROMM（超出 BT.2020，归一必削色且无 CICP 命名）；
        /// ② 源为 SDR 传递（HDR 应走 CICP 而非 ICC）；③ 未选「仅CICP」策略（用户明确不要 ICC）；
        /// ④ 目标色域未指定或与源同空间（否则用户要求的是一次转换，保真无从表达）；
        /// ⑤ 输出容器可后置附着 ICC（exiftool 实测可写 PNG/JPEG/TIFF/WebP；
        ///    JXL/AVIF 不能由 exiftool 写 ICC → JXL 由 cjxl 直接携带源 ICC，AVIF 保持 BT.2020 归一）。
        /// 满足 → 返回 ICC 绝对路径（PLAN/iccs 用户文件优先，其次纯托管生成）；否则 null。
        /// </summary>
        public static string? ResolveFaithfulUltraWideIcc(Models.FfmpegOptions options,
            string? srcGamutName, string? srcTrc, string? format)
            => ResolveFaithfulUltraWideIcc(options.ColorStrategy, options.ColorSpace, srcGamutName, srcTrc, format);

        /// <summary>
        /// 同上，但**不依赖 <see cref="Models.FfmpegOptions"/>** —— 供色彩引擎的意图装配层
        /// （`ColorIntentFactory`）复用，保证「哪些源走保真」在**两条管线上是同一个判定**
        ///（2026-09-16 接入引擎时抽出；此前引擎侧完全没有保真概念 ⇒ 会把 ProPhoto 归一化并丢源 ICC）。
        /// </summary>
        public static string? ResolveFaithfulUltraWideIcc(Models.ColorStrategy strategy,
            string? colorSpace, string? srcGamutName, string? srcTrc, string? format)
        {
            if (strategy == Models.ColorStrategy.BakeCicpOnly) return null;
            if (srcTrc is "smpte2084" or "arib-std-b67") return null;
            if (!IccProfileBuilder.IsFaithfulOnly(srcGamutName)) return null;

            var f = (format ?? "").ToLowerInvariant();
            // jxl 亦适用：cjxl 支持 -x icc_pathname（BuildCjxlArguments 已有该分支），
            // 其 color_space 枚举无法命名 ProPhoto 等 —— 恰是 ICC 保真要解决的场景
            if (f is not ("png" or "apng" or "jpg" or "jpeg" or "jpegli" or "tiff" or "tif" or "webp" or "jxl"))
                return null;

            var target = ColorSpaceRegistry.Resolve(colorSpace);
            var src = ColorSpaceRegistry.Resolve(srcGamutName);
            if (target != null && src != null && target.Key != src.Key) return null;

            return IccProfileService.GetFaithfulIcc(srcGamutName);
        }

        /// <summary>
        /// 探测核心实现（无缓存，供 GetCachedProbe 调用）。
        /// 双引擎：exiftool 优先（EXIF ColorSpace + ICC 语义），ffprobe 回退/补充（pix_fmt/位深/alpha）。
        /// - exiftool 可用：ColorSpace 标签 + ICC 描述推断色彩语义（相机 JPEG 广色域识别）
        /// - ffprobe 始终提供：pix_fmt → 位深/alpha/isRgb（exiftool 无此能力）
        /// - 语义合并：exiftool 有值 > ffprobe 值 > 默认
        /// </summary>
        /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26`：上游 15 个调用点全部持有 `ct`，此前四级全断）</param>
        private static ColorMetadata ProbeInputColorMetadataCore(string inputPath,
            Action<string>? log = null, CancellationToken ct = default)
        {
            var meta = new ColorMetadata();
            // 管道输入（stdin）无法探测，直接返回空结构
            if (inputPath == "-") return meta;

            // ── 1) exiftool 优先：EXIF ColorSpace + ICC 描述（色彩语义真相来源）──
            // 相机 JPEG 常在 EXIF 写 ColorSpace（1=sRGB, 2=Adobe RGB），ffprobe 读不到；
            // 广色域真相在 ICC Profile（如 AdobeRGB/DisplayP3），exiftool 可提取解析。
            // ⚠️ 2026-08-14 死锁修复: 本方法在 UI 线程被同步调用 (RegenerateCommand→BuildArguments),
            // 直接 .GetAwaiter().GetResult() 会捕获 Avalonia UI SynchronizationContext →
            // async 方法内部 await 续体无法回到被阻塞的 UI 线程 → 整个软件卡死 (发布版必现)。
            // 用 Task.Run 包裹使 async 方法在线程池执行, 续体无需 UI 线程。
            var exifColorTags = Task.Run(() => ExifToolService.ReadColorTagsAsync(inputPath, log, ct))
                .GetAwaiter().GetResult();

            // EXIF ColorSpace 值（带任意组前缀，如 "EXIF:ColorSpace"）
            string? exifColorSpace = null;
            foreach (var kv in exifColorTags)
            {
                if (kv.Key.EndsWith(":ColorSpace", StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Equals("ColorSpace", StringComparison.OrdinalIgnoreCase))
                {
                    exifColorSpace = kv.Value;
                    break;
                }
            }

            // 位深（exiftool BitsPerSample，作为 pix_fmt 解析的交叉验证）
            string? bitsPerSample = null;
            foreach (var kv in exifColorTags)
            {
                if (kv.Key.EndsWith(":BitsPerSample", StringComparison.OrdinalIgnoreCase))
                {
                    bitsPerSample = kv.Value;
                    break;
                }
            }
            if (bitsPerSample != null && int.TryParse(bitsPerSample, out var exifBd) && exifBd > 0)
                meta.bitDepth = exifBd;

            // ICC 语义：提取 ICC → 解析描述 → 推断色彩空间（仅对 exiftool 可读的容器格式）
            string? iccGuessed = null;
            if (!string.IsNullOrWhiteSpace(exifColorSpace) || true) // 有 exiftool 即尝试 ICC（JPEG/TIFF/WebP 常见）
            {
                try
                {
                    var (iccPath, iccDesc) = IccProfileService.ExtractIccToTempFile(inputPath, log, ct);
                    if (iccPath != null)
                    {
                        // 先按**度量**（色度矩阵+曲线）定空间，描述文字只作兜底：
                        // 本仓库生成的标准 ICC 描述就是“RGB built-in”，只按描述猜会让广色域判断静默失效。
                        iccGuessed = ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(iccPath, iccDesc);
                        IccProfileService.TryDeleteIcc(iccPath);
                    }
                }
                catch { }
            }

            // ── 色彩语义决策：ICC > EXIF ColorSpace ──
            // ⚠ EXIF 的 `ColorSpace` 这里只先记成**候选**（`exifPrim`/`exifTrc`），是否采用要等 ffprobe 的
            //   CICP 出来再定 —— 见下方「CICP 优先」。ICC 分支不受影响（真 ICC 档案是显式色彩数据）。
            string? exifPrim = null;
            string? exifTrc = null;
            if (!string.IsNullOrWhiteSpace(iccGuessed))
            {
                // ⚠️ ColorMetadata 是 struct：必须接回返回值。
                // 旧写法 `ApplyIccSemantics(meta, ...)` 按值传递 → 丢弃全部修改 →
                // ICC 推得的 sourceGamutName/hasIccSemantics 永远为空，下游
                // 「超广色域携带 ICC / 归一 BT.2020 / 保真路径」全部静默失效（本次修复）。
                meta = ApplyIccSemantics(meta, iccGuessed);
            }
            else if (!string.IsNullOrWhiteSpace(exifColorSpace))
            {
                // EXIF ColorSpace: 1=sRGB, 2=Adobe RGB（无 zscale 命名，近似 bt709 + 上层携带 ICC）
                if (exifColorSpace.Contains("Adobe", StringComparison.OrdinalIgnoreCase)
                    || exifColorSpace == "2")
                {
                    exifPrim = "bt709";
                    exifTrc = "bt709";
                }
                else if (exifColorSpace.Contains("sRGB", StringComparison.OrdinalIgnoreCase)
                         || exifColorSpace == "1")
                {
                    exifPrim = "bt709";
                    exifTrc = "iec61966-2-1";
                }
            }

            // ── 2) ffprobe 补充：pix_fmt → 位深/alpha/isRgb（始终执行，exiftool 无此能力）──
            var ffMeta = ProbeWithFfprobe(inputPath, log, ct);
            // ⚠⚠ **CICP 优先（2026-09-20 修，缺陷 C）**：能写 CICP 的容器（AVIF/HEIC/JXL/MP4…）里，
            //   **容器的 CICP 才是权威色彩描述**；EXIF/XMP 的 `ColorSpace` 往往只是**源照片的残留标签**。
            //   实测 `seine_hdr_gainmap_srgb.avif`：CICP 说 `smpte2084`(PQ)，而 `XMP-exif:ColorSpace` 说 `sRGB`
            //   ⇒ 旧逻辑让 EXIF 覆盖 CICP ⇒ 判成「SDR 输入」⇒ **不插 HDR 色调映射链**
            //   ⇒ 下游 `iccgen` 遇到 **PQ 帧**硬失败（`Not yet implemented in FFmpeg`，**零产物**）。
            //   ⇒ CICP 可用（非 `unknown`/`unspecified`）时 EXIF 必须**让位**并**点名**；
            //     CICP 不可用时行为**完全不变**（相机 JPEG / PNG 实测 CICP 恒为 `unknown,unknown`
            //     ⇒ 仍靠 EXIF —— 这正是该分支存在的理由，不能一刀切删掉）。
            bool ffCicpUsable =
                (!string.IsNullOrWhiteSpace(ffMeta.colorTrc)
                    && ffMeta.colorTrc != "unknown" && ffMeta.colorTrc != "unspecified")
                || (!string.IsNullOrWhiteSpace(ffMeta.colorPrimaries)
                    && ffMeta.colorPrimaries != "unknown" && ffMeta.colorPrimaries != "unspecified");
            if (exifTrc != null)
            {
                if (ffCicpUsable)
                {
                    log?.Invoke($"[色彩] EXIF/XMP ColorSpace={exifColorSpace} 让位给容器 CICP（trc={ffMeta.colorTrc} prim={ffMeta.colorPrimaries}）—— 容器自带权威色彩描述时不采信 EXIF 残留\n");
                }
                else
                {
                    meta.colorPrimaries = exifPrim;
                    meta.colorTrc = exifTrc;
                    meta.hasIccSemantics = true; // 标记：上层应携带 ICC 而非仅靠标签
                }
            }
            // ffprobe 的 pix_fmt 位深更可靠（exiftool BitsPerSample 可能缺省），
            // 但 exiftool 位深优先（用户要求 exiftool 优先）；无 exiftool 位深时用 ffprobe
            if (meta.bitDepth <= 0) meta.bitDepth = ffMeta.bitDepth;
            meta.hasAlpha = ffMeta.hasAlpha;
            meta.isRgb = ffMeta.isRgb;
            if (string.IsNullOrWhiteSpace(meta.colorSpace)) meta.colorSpace = ffMeta.colorSpace;
            // 语义缺失时才回退 ffprobe 的 primaries/trc
            if (!meta.hasIccSemantics)
            {
                if (string.IsNullOrWhiteSpace(meta.colorPrimaries)) meta.colorPrimaries = ffMeta.colorPrimaries;
                if (string.IsNullOrWhiteSpace(meta.colorTrc)) meta.colorTrc = ffMeta.colorTrc;
            }
            // 宽高信息来自 ffprobe（exiftool 一般不提供宽高）
            meta.width = ffMeta.width;
            meta.height = ffMeta.height;
            // HDR 内容峰值只可能来自 ffprobe 的 frame side data（exiftool 无此能力）⇒ **必须显式带回**。
            // 此前本行缺失：ProbeWithFfprobe 里读了 side data 却没人拷贝，
            // 导致两条消费路径（引擎装配 / GainMap 日志）拿到的 peakNits 永远是 0（实测）。
            if (meta.peakNits <= 0 && ffMeta.peakNits > 0)
            {
                meta.peakNits = ffMeta.peakNits;
                meta.peakSource = ffMeta.peakSource;
            }
            return meta;
        }

        /// <summary>将 ICC 推断的色彩空间名应用到 ColorMetadata（hasIccSemantics 标记供上层携带 ICC）。
        /// ColorMetadata 为 struct，必须返回修改后的副本。</summary>
        private static ColorMetadata ApplyIccSemantics(ColorMetadata meta, string guessed)
        {
            meta.hasIccSemantics = true;
            meta.sourceGamutName = guessed;   // 记录源空间名，供 ColorSpaceRegistry 判定超广色域归一化
            switch (guessed)
            {
                case "sRGB":
                    meta.colorPrimaries = "bt709";
                    meta.colorTrc = "iec61966-2-1";
                    break;
                case "Adobe RGB":
                case "ColorMatch RGB":
                    // 无 zscale 命名 → 近似 bt709，上层携带 ICC 保留完整语义
                    meta.colorPrimaries = "bt709";
                    meta.colorTrc = "bt709";
                    break;
                case "Display P3":
                    meta.colorPrimaries = "smpte432";
                    meta.colorTrc = "iec61966-2-1";   // P5: Display P3 用 sRGB 传递函数(P3-D65 标准)
                    break;
                case "DCI-P3":
                    meta.colorPrimaries = "smpte431";
                    meta.colorTrc = "bt709";
                    break;
                case "Rec.2020":
                    meta.colorPrimaries = "bt2020";
                    meta.colorTrc = "bt709";
                    break;
                case "Rec.2100":
                    meta.colorPrimaries = "bt2020";
                    meta.colorTrc = "smpte2084";
                    break;
                case "ProPhoto RGB":
                    // 无 zscale 命名 → 近似 bt709，上层携带 ICC（有命名 ICC 时走保真路径，见
                    // ResolveFaithfulUltraWideIcc：像素不动 + 仅附着 ProPhoto ICC）
                    meta.colorPrimaries = "bt709";
                    meta.colorTrc = "bt709";
                    break;
            }
            return meta;
        }

        /// <summary>纯 ffprobe 探测（pix_fmt/色彩标签/宽高），供回退与补充使用</summary>
        /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26`：本方法此前四级全无 `ct`，用户取消按不动）</param>
        private static ColorMetadata ProbeWithFfprobe(string inputPath,
            Action<string>? log = null, CancellationToken ct = default)
        {
            var meta = new ColorMetadata();
            if (inputPath == "-") return meta;
            try
            {
                var ffprobe = FindFfprobe();
                if (ffprobe == null) return meta;
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height,bits_per_raw_sample,pix_fmt,color_primaries,color_transfer,color_space -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    RedirectStandardError = true,   // P2-3：排空见下方 BeginErrorReadLine
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p == null) return meta;
                // ⚠ 先挂上两个读取任务再等退出（与本文件 `ProbePeakNits` 同一范式）：旧的
                //   `StandardOutput.ReadToEnd()`（同步阻塞）写在 `WaitForExit(5000)` **之前**
                //   ⇒ 子进程不关管道就永不返回，**超时是死代码、且超时后没有 Kill**。
                //   实测：畸形 `.jxl`（1 字节、首字节 00/FF）让 ffprobe **永不退出** ⇒ 队列永久挂死。
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                // `#26`：同步 `WaitForExit(5000)` 不接受 ct ⇒ 用 `ct.Register` 杀树接线；原有 5s 超时**不变**。
                using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    // 原写法在此**静默**返回退化 meta ⇒ 补一句点名，不再无声。
                    var tmsg = $"[probe] ffprobe 色彩探测超时（5s）⇒ 已终止：{Path.GetFileName(inputPath)}（按未探测处理）";
                    if (log != null) log(tmsg + "\n"); else System.Diagnostics.Trace.WriteLine(tmsg);
                    return meta;
                }
                if (ct.IsCancellationRequested)
                {
                    var cmsg = $"[probe] ffprobe 色彩探测被取消 ⇒ 已终止：{Path.GetFileName(inputPath)}（按未探测处理）";
                    if (log != null) log(cmsg + "\n"); else System.Diagnostics.Trace.WriteLine(cmsg);
                    return meta;
                }
                var output = outTask.GetAwaiter().GetResult().Trim();
                errTask.GetAwaiter().GetResult();
                var parts = output.Split(',');
                // ⚠️ 关键：ffprobe 的 -show_entries stream=A,B,C 输出顺序为流内部固定顺序
                // （与参数书写顺序无关）。实测（ffmpeg 22.x / ffprobe 8.x）为：
                //   width, height, pix_fmt, color_space, color_transfer, color_primaries, bits_per_raw_sample
                // （注意：color_transfer 在 color_primaries 之前！2026-08-14 实测修正）
                // 实际输出示例: 512,384,yuvj420p,bt470bg,unknown,unknown,8
                // parts[0]=width, parts[1]=height, parts[2]=pix_fmt, parts[3]=color_space,
                // parts[4]=color_transfer, parts[5]=color_primaries, parts[6]=bits_per_raw_sample
                if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]))
                {
                    var pixFmt = parts[2];
                    meta.bitDepth = ParseBitDepthFromPixFmt(pixFmt);
                    // Alpha 检测：pix_fmt 含 'a' 字符（rgba/bgra/argb/ya8 等）。
                    // pal8 也含 'a' 但它是 8-bit 调色板（无透明通道语义），仅在 >8bit 分支使用时无影响。
                    meta.hasAlpha = pixFmt.Contains('a');
                    // RGB 系列像素格式（全范围 0-255/0-65535）→ pc range
                    meta.isRgb = pixFmt.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)
                              || pixFmt.StartsWith("bgr", StringComparison.OrdinalIgnoreCase)
                              || pixFmt.StartsWith("gbr", StringComparison.OrdinalIgnoreCase)
                              || pixFmt.StartsWith("rgba", StringComparison.OrdinalIgnoreCase)
                              || pixFmt.StartsWith("bgra", StringComparison.OrdinalIgnoreCase)
                              || pixFmt.StartsWith("argb", StringComparison.OrdinalIgnoreCase);
                    // ── 色彩范围推断（2026-08-15 扩展）──
                    // 之前只识别 RGB → pc；现在 TV/PC 自动选项需要完整判断：
                    //   yuvj/grayj 系列 = full range YUV（如 JPEG 解码输出）→ pc
                    //   yuv 系列 = limited YUV（视频帧/HEIC/AVIF 等）→ tv
                    //   RGB 系列 = 恒全范围 → pc
                    //   gray/pal8/其他 = 不判断（避免误标）→ null
                    if (meta.isRgb)
                        meta.colorRange = "pc";
                    else if (pixFmt.StartsWith("yuvj", StringComparison.OrdinalIgnoreCase)
                          || pixFmt.StartsWith("grayj", StringComparison.OrdinalIgnoreCase))
                        meta.colorRange = "pc";
                    else if (pixFmt.StartsWith("yuv", StringComparison.OrdinalIgnoreCase))
                        meta.colorRange = "tv";
                }
                // parts[0] = width
                if (parts.Length >= 1 && !string.IsNullOrEmpty(parts[0]) && int.TryParse(parts[0], out var w))
                    meta.width = w;
                // parts[1] = height
                if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]) && int.TryParse(parts[1], out var h))
                    meta.height = h;
                // parts[3] = color_space (YUV 矩阵)
                if (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3]) && parts[3] != "unknown" && parts[3] != "N/A")
                    meta.colorSpace = parts[3];
                // parts[4] = color_transfer (实测顺序: transfer 在 primaries 前)
                if (parts.Length >= 5 && !string.IsNullOrEmpty(parts[4]) && parts[4] != "unknown" && parts[4] != "N/A")
                    meta.colorTrc = parts[4];
                // parts[5] = color_primaries
                if (parts.Length >= 6 && !string.IsNullOrEmpty(parts[5]) && parts[5] != "unknown" && parts[5] != "N/A")
                    meta.colorPrimaries = parts[5];
                // parts[6] = bits_per_raw_sample (fallback 位深，若 pix_fmt 无位深信息)
                if (parts.Length >= 7 && !string.IsNullOrEmpty(parts[6]) && int.TryParse(parts[6], out var bps) && bps > meta.bitDepth)
                    meta.bitDepth = bps;
                // P8: 仅对 HDR (PQ/HLG) 源额外读一次 side data 峰值亮度（静像多为无 → 回退）。
                if (meta.colorTrc == "smpte2084" || meta.colorTrc == "arib-std-b67")
                {
                    var (pk, peakFrom) = ProbePeakNits(inputPath, log, ct);
                    meta.peakNits = pk;
                    meta.peakSource = peakFrom;
                }
            }
            catch { }
            return meta;
        }

        private static string? FindFfprobe()
        {
            var ffmpegDir = System.IO.Path.GetDirectoryName(AppSettingsService.Current.FfmpegPath);
            if (!string.IsNullOrEmpty(ffmpegDir))
            {
                var probe = System.IO.Path.Combine(ffmpegDir, "ffprobe.exe");
                if (System.IO.File.Exists(probe)) return probe;
            }
            return null;
        }

        /// <summary>
        /// P8/P4c：读 HDR 静态元数据的**内容峰值**（nits）及其来源。
        ///
        /// 语义对齐 ffmpeg <c>libavfilter/colorspace.c::ff_determine_signal_peak()</c>：
        /// **MaxCLL 优先**（内容真实峰值），只有取不到 MaxCLL 才退到 BS.2086 mastering display
        /// max_luminance——后者是调色**监视器**的峰值，是内容峰值的上界；旧实现取两者 Math.Max ⇒ 过度压暗。
        ///
        /// 键名与单位不凭记忆，全部由 <c>tests/scripts/_probe-hdr-peak-oracle.ps1</c> 实测本构建 ffprobe 得出：
        ///  • 内容峰值的 JSON 键是 <c>max_content</c>（**不是** max_cll/MaxCLL）——旧写法因此**从未读到 MaxCLL**；
        ///  • <c>max_luminance</c> 打印为**有理数字符串** "40000000/10000"，其值已是 cd/m²（=4000），
        ///    故必须按 num/den 解析（旧写法"取分子再 /10000"只在分母恰为 10000 时碰巧正确）。
        ///
        /// 只读第一帧（-read_intervals "%+#1"）：静像本就一帧，视频不必为取元数据而整片解码
        /// （实测：各容器退出码 0、首帧 side data 仍在；动图输出从 ~9KB 降到 ~0.9KB）。
        /// 读不到 ⇒ (0, null)，由调用方回退到源曲线名义峰值，并必须在日志写明来源。
        /// </summary>
        /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        /// <param name="ct">取消令牌（`#26`：本方法此前四级全无 `ct`，用户取消按不动）</param>
        private static (double peakNits, string? source) ProbePeakNits(string inputPath,
            Action<string>? log = null, CancellationToken ct = default)
        {
            if (inputPath == "-") return (0, null);
            try
            {
                var ffprobe = FindFfprobe();
                if (ffprobe == null) return (0, null);
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v error -select_streams v:0 -read_intervals \"%+#1\" -show_frames " +
                                $"-show_entries frame_side_data_list -of json \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p == null) return (0, null);
                // 先挂上两个读取任务再等退出：子进程写满管道时"只等退出"会互相卡死；
                // 本方法可被 UI 线程同步调用，故超时直接放弃（宁可回退名义峰值也不挂住界面）。
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                // `#26`：同步 `WaitForExit(5000)` 不接受 ct ⇒ 用 `ct.Register` 杀树接线；原有 5s 超时**不变**。
                using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    // 原写法在此**静默**返回 (0,null)（调用方回退名义峰值）⇒ 补一句点名，不再无声。
                    var tmsg = $"[probe] 峰值亮度探测超时（5s）⇒ 已终止：{Path.GetFileName(inputPath)}（回退名义峰值）";
                    if (log != null) log(tmsg + "\n"); else System.Diagnostics.Trace.WriteLine(tmsg);
                    return (0, null);
                }
                if (ct.IsCancellationRequested)
                {
                    var cmsg = $"[probe] 峰值亮度探测被取消 ⇒ 已终止：{Path.GetFileName(inputPath)}（回退名义峰值）";
                    if (log != null) log(cmsg + "\n"); else System.Diagnostics.Trace.WriteLine(cmsg);
                    return (0, null);
                }
                string json = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();
                double cll = ExtractJsonNumber(json, "max_content", "MaxCLL", "max_cll");
                if (cll > 0) return (cll, "MaxCLL");
                double mld = ExtractJsonNumber(json, "max_luminance", "MasteringDisplayMaxLuminance");
                return mld > 0 ? (mld, "BS.2086 mastering") : (0, null);
            }
            catch { return (0, null); }
        }

        /// <summary>
        /// 从 ffprobe JSON 文本中取 "key": value 的数值，兼容三种打印形态：
        /// 裸数字（<c>"max_content": 600</c>）、字符串小数、**有理数字符串**（<c>"40000000/10000"</c> ⇒ 4000）。
        /// 可给多个候选键名（不同 ffmpeg 版本打印名不同），取第一个解析出正值者。
        /// </summary>
        private static double ExtractJsonNumber(string json, params string[] keys)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            foreach (var key in keys)
            {
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) continue;
                i = json.IndexOf(':', i);
                if (i < 0) continue;
                int j = i + 1;
                while (j < json.Length && (json[j] == ' ' || json[j] == '"' || json[j] == '\t' || json[j] == ':')) j++;
                int k = j;
                while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '.' || json[k] == '/' || json[k] == '-')) k++;
                double v = ParseRationalOrDecimal(json.Substring(j, k - j));
                if (v > 0) return v;
            }
            return 0;
        }

        /// <summary>解析 "40000000/10000" | "4000.0" | "4000" 为 double；分母为 0 或格式不符 ⇒ 0（绝不猜）。</summary>
        private static double ParseRationalOrDecimal(string token)
        {
            if (string.IsNullOrEmpty(token)) return 0;
            const System.Globalization.NumberStyles NS = System.Globalization.NumberStyles.Any;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            int slash = token.IndexOf('/');
            if (slash < 0)
                return double.TryParse(token, NS, ci, out var d) ? d : 0;
            var parts = token.Split('/');
            if (parts.Length != 2) return 0;
            if (!double.TryParse(parts[0], NS, ci, out var num) || !double.TryParse(parts[1], NS, ci, out var den) || den == 0)
                return 0;
            return num / den;
        }

        // ═══════════════════════════════════════════════════════════
        //  ICC 辅助方法
        // ═══════════════════════════════════════════════════════════

        /// <summary>解析 ICC 烘焙的源色彩空间显示名（用于超广色域矩阵转换判定）</summary>
        private static string? GetIccSourceDisplayName(Models.FfmpegOptions options, string inputPath)
        {
            if (!string.IsNullOrWhiteSpace(options.IccFilePath))
            {
                var info = IccProfileService.ParseInfo(options.IccFilePath);
                return ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(options.IccFilePath, info?.Description);
            }
            // auto（UI 标“从文件检测”）：用探测到的源 ICC 色域名，而非回落 sRGB（与其余路径同源）。
            return GetCachedProbe(inputPath).sourceGamutName;
        }

        /// <summary>解析 ICC 烘焙的源色彩空间参数</summary>
        private static (string primaries, string trc, string matrix) ResolveIccSourceParams(
            Models.FfmpegOptions options, string inputPath)
        {
            // 源色彩空间不再手动指定（自动：优先 ICC 文件描述推断，其次探测）

            // 其次：从 ICC 文件描述推断
            if (!string.IsNullOrWhiteSpace(options.IccFilePath))
            {
                var info = IccProfileService.ParseInfo(options.IccFilePath);
                var guessed = ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(options.IccFilePath, info?.Description);
                if (guessed != null)
                    return MapNamedColorSpace(guessed);
            }

            // 再次：auto 时用探测到的源色域兑底（看齐源，避免将 P3/Adobe 源误当 sRGB）。
            // 优先用 ICC 描述推定的色域名（可判超广）；否则用 ffprobe 的 CICP tokens（cICP 源无 ICC 描述时）。
            var hp = GetCachedProbe(inputPath);
            if (!string.IsNullOrWhiteSpace(hp.sourceGamutName))
                return MapNamedColorSpace(hp.sourceGamutName);
            if (!string.IsNullOrWhiteSpace(hp.colorPrimaries))
                return (hp.colorPrimaries, hp.colorTrc ?? hp.colorPrimaries, hp.colorSpace ?? "bt709");

            // 回退：假定 sRGB（安全默认值）
            return ("bt709", "iec61966-2-1", "bt709");
        }

        /// <summary>目标色彩空间名称 → (primaries, trc, matrix)。委托 ColorSpaceRegistry（单一来源，bt470bg 等已根除）。</summary>
        private static (string primaries, string trc, string matrix) MapIccTargetColorSpace(
            string name)
        {
            var (p, t, m) = ColorSpaceRegistry.FfmpegTokens(name);
            return (p ?? "bt709", t ?? "iec61966-2-1", m ?? "bt709");
        }

        /// <summary>常见色彩空间名称 → (primaries, trc, matrix)。委托 ColorSpaceRegistry。</summary>
        private static (string primaries, string trc, string matrix) MapNamedColorSpace(
            string name)
        {
            var (p, t, m) = ColorSpaceRegistry.FfmpegTokens(name);
            return (p ?? "bt709", t ?? "iec61966-2-1", m ?? "bt709");
        }

        /// <summary>两个色彩参数集是否相同（避免无意义的 zscale 转换）。大小写不敏感比较。</summary>
        private static bool ColorParamsEqual(
            (string p, string t, string m) a,
            (string p, string t, string m) b)
        {
            return string.Equals(a.p, b.p, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.t, b.t, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.m, b.m, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>构造 zscale 烘焙滤镜字符串（含输入/输出参数，确保源→目标精确转换）</summary>
        private static string BuildZscaleBakeFilter(
            (string primaries, string trc, string matrix) src,
            (string primaries, string trc, string matrix) dst)
        {
            return $"zscale=pin={src.primaries}:tin={src.trc}:min={src.matrix}" +
                   $":p={dst.primaries}:t={dst.trc}:m={dst.matrix}";
        }

        /// <summary>向现有视频滤镜链追加滤镜，兼容 -vf 和 -filter_complex 两种模式</summary>
        private static void AppendVideoFilter(List<string> args, string filter)
        {
            // 优先查找 -vf
            var vfIdx = args.FindIndex(a => a == "-vf");
            if (vfIdx >= 0 && vfIdx + 1 < args.Count)
            {
                args[vfIdx + 1] = args[vfIdx + 1] + "," + filter;
                return;
            }
            // 其次查找 -filter_complex（GIF 等使用复杂滤镜图）
            var fcIdx = args.FindIndex(a => a == "-filter_complex");
            if (fcIdx >= 0 && fcIdx + 1 < args.Count)
            {
                // 复杂滤镜图中将滤镜插入到第一个 split 之前
                // 格式: [pre],split[s0][s1];[s0]...[s1]...
                var complex = args[fcIdx + 1];
                var splitIdx = complex.IndexOf(",split", StringComparison.Ordinal);
                if (splitIdx > 0)
                    args[fcIdx + 1] = complex.Insert(splitIdx, "," + filter);
                else
                    args[fcIdx + 1] = filter + "," + complex;
                return;
            }
            // 无现有滤镜：新建 -vf
            args.Add("-vf");
            args.Add(filter);
        }

        /// <summary>「色彩变换」滤镜的 token 前缀 —— 顺序统一 R1 的锚点（插在它们**之前**）</summary>
        private static readonly string[] ColorTransformTokens = { "zscale=", "tonemap=", "colorchannelmixer=" };

        /// <summary>
        /// **顺序统一（R1）**：把 <paramref name="scaleFilter"/> 插入到滤镜链里**第一条色彩变换之前**。
        ///
        /// <para>
        /// <b>为什么需要它</b>：色彩变换把像素搬到线性域再搬回来，几何缩放若在其**之后**做，等于在**已定标
        /// 的域**上重采样。两条顺序**不等价**（同一素材 2×2 对照实测 PSNR 36.47dB / 最大单通道差 14417）
        /// ⇒ 顺序必须固定为 **缩放 → 映射**。legacy 此前是**映射 → 缩放**（色彩链由 <c>:121</c> 追加、
        /// scale 由 <c>:535</c> 追加），与引擎侧（<c>RawColorPipeline.cs:94-97</c> 的
        /// <c>format=rgb48le,{scale}</c> ⇒ 缩放 → 映射）**相反**。
        /// </para>
        ///
        /// <para>
        /// <b>为何不复用 <see cref="AppendVideoFilter"/></b>：它是**追加**语义（<c>-vf</c> 分支
        /// <c>+= "," + filter</c>、complex 分支插在 <c>,split</c> 之前），而 <c>:498-499</c> 的 iccgen
        /// **依赖**该追加语义 —— 改它等于改一条已被别处依赖的契约。本方法只做「插到链首色彩变换之前」
        /// 这一件事，两者互不干扰。
        /// </para>
        ///
        /// <para>
        /// <b>两个守卫</b>：
        /// <list type="number">
        /// <item><b>字面引号必须留在最外层</b>：链可能是 <c>"format=…,zscale=…"</c>（引号是字符串的一部分，
        ///   见 <c>BuildArguments</c> 的 GIF 分支与 <c>QueueProcessor.BuildPipeColorArgs</c>）。在位置 0
        ///   插入会产出 <c>scale=…,"format=…"</c> ⇒ ffmpeg 解析失败（退出码 <c>-22</c>）⇒ 只在引号**内**插入。</item>
        /// <item><b>不得插进 <c>[label]</c> / <c>[1:v]</c> 段</b>：链首若有输入标签（如
        ///   <c>[0:v][1:v]alphamerge,…</c>），在标签之前插入会破坏滤镜图。本方法遇到链首标签时
        ///   <b>不注入、原样返回</b>（多标签 complex 的正确锚点是 merge **之后**，属<b>未覆盖形态</b>；
        ///   门禁**不得**对该形态套用 <c>scaleIdx == 0</c>）。</item>
        /// </list>
        /// </para>
        ///
        /// <para>
        /// <b>锚点规则</b>：只在**第一个 <c>;</c> 段**内查找第一条色彩变换（<c>zscale=</c> / <c>tonemap=</c> /
        /// <c>colorchannelmixer=</c>，且必须处在**滤镜 token 位置** —— 前一个字符是 <c>,</c> 或段首，
        /// 否则 <c>zscale=</c> 里的 <c>scale=</c> 子串会误命中）；找不到则锚点 = **该段链首**
        /// （= 「无色彩变换」时的退化，此时 <c>scaleIdx == 0</c>）。
        /// </para>
        /// </summary>
        internal static string InsertScaleFilterAtChainHead(string chain, string scaleFilter)
        {
            if (string.IsNullOrEmpty(chain) || string.IsNullOrEmpty(scaleFilter))
                return chain;

            // 守卫 1：字面引号留最外层 —— 只在引号内插入（位置 0 插入会破坏命令行，实测 ffmpeg 退出 -22）
            int bodyStart = chain.Length >= 2 && chain[0] == '"' ? 1 : 0;
            int bodyEnd = chain.Length >= 2 && chain[^1] == '"' ? chain.Length - 1 : chain.Length;
            var body = chain.Substring(bodyStart, bodyEnd - bodyStart);

            int segEnd = body.IndexOf(';');
            var firstSeg = segEnd < 0 ? body : body.Substring(0, segEnd);

            // 守卫 2：链首输入标签 ⇒ 不注入（未覆盖形态，见 XML 注释）
            if (firstSeg.StartsWith("["))
                return chain;

            int anchor = FindFirstColorTransformIndex(firstSeg);
            var newBody = body.Insert(anchor < 0 ? 0 : anchor, scaleFilter + ",");

            return chain.Substring(0, bodyStart) + newBody + chain.Substring(bodyEnd);
        }

        /// <summary>
        /// 返回本段内**第一条色彩变换滤镜**的起始下标（无则 -1）。
        /// 必须处在滤镜 token 位置：段首或前一个字符是 <c>,</c> —— 否则 <c>zscale=</c> 内部的 <c>scale=</c>
        /// 子串、以及别的滤镜名里同形子串都会误命中。
        /// </summary>
        private static int FindFirstColorTransformIndex(string segment)
        {
            int best = -1;
            foreach (var tok in ColorTransformTokens)
            {
                int from = 0;
                while (true)
                {
                    int i = segment.IndexOf(tok, from, StringComparison.Ordinal);
                    if (i < 0) break;
                    if (i == 0 || segment[i - 1] == ',')
                    {
                        if (best < 0 || i < best) best = i;
                        break;
                    }
                    from = i + 1;
                }
            }
            return best;
        }

        /// <summary>把 <paramref name="scaleFilter"/> 按 R1 插进 <paramref name="args"/> 里已有的 `-vf` / `-filter_complex`。</summary>
        /// <remarks>
        /// ⚠ 本批次**不实现**扩展 ①（无滤镜时由 helper 新建 <c>-vf</c>）与扩展 ②（多标签 complex 的
        /// merge 后锚点）—— 前者此处**委托**给 <see cref="AppendVideoFilter"/> 的兜底（链不存在时
        /// 「链首」就等于新建的 <c>-vf</c>，行为与改动前逐字一致），后者由
        /// <see cref="InsertScaleFilterAtChainHead(string,string)"/> 守卫 2 原样返回、**不注入**。
        /// </summary>
        private static void InsertScaleFilterAtChainHead(List<string> args, string scaleFilter)
        {
            var vfIdx = args.FindIndex(a => a == "-vf");
            if (vfIdx >= 0 && vfIdx + 1 < args.Count)
            {
                args[vfIdx + 1] = InsertScaleFilterAtChainHead(args[vfIdx + 1], scaleFilter);
                return;
            }
            var fcIdx = args.FindIndex(a => a == "-filter_complex");
            if (fcIdx >= 0 && fcIdx + 1 < args.Count)
            {
                args[fcIdx + 1] = InsertScaleFilterAtChainHead(args[fcIdx + 1], scaleFilter);
                return;
            }
            // 无现有滤镜 ⇒ 链不存在，「链首」即新建的 -vf（委托兜底，与改动前一致）
            AppendVideoFilter(args, scaleFilter);
        }

        /// <summary>
        /// 从**已构建的命令行**里取出「最长边缩放」注入项（R1 判别式：<c>scale=W:-2:…</c> / <c>scale=-2:H:…</c>）。
        /// 供调用方回显一行日志（扩展 ③：「缩了但用户不知道」本身仍是静默）。
        /// </summary>
        /// <remarks>
        /// ⚠ **只读命令行、不读意图** —— 命令行里有才是真有（<c>docs/TESTING.md</c> §6 第 70 条：
        /// 「日志标记」不足以做门禁）。
        /// ⚠ <c>-2</c> 是**分水岭**：<c>AnimationScaleW</c> 的 <c>scale={W}:-1:</c> 与 preScale 的
        /// <c>scale=ceil(iw/2)*2:</c> 都**不匹配**本判别式 ⇒ 不会把别的合法 scale token 当成本项。
        /// </remarks>
        public static string? FindInjectedScaleFilter(string args)
        {
            if (string.IsNullOrEmpty(args)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(args, @"scale=(?:\d+:-2|-2:\d+):flags=lanczos");
            return m.Success ? m.Value : null;
        }

        /// <summary>ICC 嵌入逻辑：根据输出格式选择最佳嵌入路径</summary>
        /// <remarks>
        /// 注意：当前 FFmpeg 构建不支持 -icc_profile 选项，
        /// 且 movie 滤镜无法直接读取 .icc 文件。因此：
        /// - JPEG/PNG/TIFF/WebP/AVIF/JXL 的 ICC 嵌入通过 QueueProcessor 中的 exiftool 后处理完成
        /// - 当无外部 ICC 文件时，AVIF/JXL 使用 iccgen 滤镜从色彩元数据生成 ICC
        /// - 外部 ICC 文件路径存储在 options.IccFilePath 中供 QueueProcessor 使用
        /// </remarks>
        private static void ApplyIccEmbedding(List<string> args,
            Models.FfmpegOptions options, string fmt)
        {
            bool hasExternalIcc = !string.IsNullOrWhiteSpace(options.IccFilePath)
                                && IccProfileService.IsValidIccProfile(options.IccFilePath);

            // 路径 A（JPEG/PNG/TIFF/WebP）：标记需要 exiftool 后处理
            // FFmpeg 命令行不添加任何 ICC 参数，由 QueueProcessor 在编码后调用 exiftool
            bool exiftoolFormats = fmt is "jpg" or "jpeg" or "png" or "tiff" or "webp";
            if (exiftoolFormats)
            {
                // ICC 嵌入推迟到 QueueProcessor 的 exiftool 后处理步骤
                return;
            }

            // 路径 B（AVIF/JXL）：
            // - 有外部 ICC 文件 → 推迟到 QueueProcessor exiftool 后处理（与 JPEG/PNG 一致）
            // - 无外部 ICC 文件 → 使用 iccgen 滤镜从帧色彩元数据自动生成 ICC
            if (fmt is "avif" or "jxl")
            {
                if (hasExternalIcc)
                {
                    // 外部 ICC 文件由 QueueProcessor 通过 exiftool 嵌入，此处不添加 iccgen
                    return;
                }
                // 无外部 ICC：iccgen 从帧元数据生成
                AppendVideoFilter(args, "iccgen");
            }
            // 其他格式（GIF、JPEG XR 等）：静默跳过，不支持 ICC 嵌入
        }
    }
}
