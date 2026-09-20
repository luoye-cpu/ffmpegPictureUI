using System;
using System.Collections.Generic;
using FfmpegGui.Models;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 图像编码器参数构造（**单一真值**）：把 <see cref="FfmpegOptions"/> 的质量 / 无损 / 结构参数
/// 映射成 ffmpeg 编码器选项。
///
/// <para>
/// **为什么单独成类**：传统管线（<c>FfmpegCommandBuilder</c>）与自研引擎
/// （<c>RawColorPipeline.BuildEncodeArgs</c>）都要发这些参数。此前只有传统管线有完整实现，
/// 引擎侧是**硬编码**的（png 恒 <c>-compression_level 100</c>、webp 恒 <c>-quality 90</c>、
/// jpg 恒 <c>-q:v 2</c>），于是 <c>ColorEngineRouter</c> 只能把"质量 / 无损 / 结构参数"
/// 整批列为「引擎未承接」并**拒绝**任务 ⇒ 引擎在 GUI 默认设置下几乎不可用。
/// 把参数构造收敛到此处后，两侧共用同一份映射（数值映射尤其容易漂移，必须单点）。
/// </para>
///
/// <para>
/// ⚠ 各编码器的**真实**能力（逐项实测 <c>ffmpeg -h encoder=…</c>，ffmpeg git-2026-09-01）：
/// </para>
/// <list type="table">
/// <item><description><c>mjpeg</c>：私有选项仅 <c>-huffman</c>；<c>-dct</c> 是通用选项（可用）；
/// <b>无</b> <c>-progressive</c>（⚠ **不是「空操作」而是硬失败**：2026-09-19 实测给 mjpeg 传
/// <c>-progressive</c> 会被 ffmpeg 以 <c>Unrecognized option 'progressive'</c> 拒绝、整条命令中止
/// ⇒ 绝不能发它；此前本行写作「该参数对本后端本就是空操作」，理由错误已更正）；
/// 像素格式 yuvj420p/422p/444p、yuv420p/422p/444p</description></item>
/// <item><description><c>png</c>：<c>-dpi</c> <c>-pred</c>；像素格式 rgb24/rgba/rgb48be/rgba64be/pal8/gray…</description></item>
/// <item><description><c>libwebp</c>：<c>-lossless</c> <c>-preset</c> <c>-quality</c>；像素格式 <b>bgra / yuv420p / yuva420p</b>（<b>不支持 4:4:4</b>）</description></item>
/// <item><description><c>tiff</c>：<c>-dpi</c> <c>-compression_algo</c>；像素格式 rgb24/rgb48le/rgba/rgba64le/gray…</description></item>
/// </list>
/// </summary>
public static class ImageEncoderArgs
{
    /// <summary>0-100 质量 → mjpeg 的 qscale（2-31，越小越好）。</summary>
    public static int MapJpegQuality(int quality) => (int)Math.Round(2 + (100 - quality) * 29.0 / 100.0);

    /// <summary>0-100 质量 → PNG <c>compression_level</c>（0-9，0 最快/最大体积）。</summary>
    public static int MapPngCompression(int quality) => (int)Math.Round((100 - quality) * 9.0 / 100.0);

    /// <summary>0-100 质量 → cjpegli 的 butteraugli distance（0-25，越小越好）。</summary>
    public static double MapJpegliDistance(int quality) => Math.Round((100 - quality) * 25.0 / 100.0, 1);

    /// <summary>0-100 质量 → AVIF 的 crf（0-63，0 最好）。</summary>
    public static int MapAvifCrf(int quality) => (int)Math.Round((100 - quality) * 63.0 / 100.0);

    /// <summary>
    /// 0-100 质量 → JPEG XL 的 <c>-distance</c>（0=无损，15=最低质量）。
    /// **单一真值**（2026-09-17 从 <c>FfmpegCommandBuilder.MapJxlDistance</c> 搬入，公式一字未改）。
    /// </summary>
    public static double MapJxlDistance(int quality) => Math.Round((100 - quality) * 15.0 / 100.0, 1);

    /// <summary>
    /// 按格式构造**编码器参数串**（不含 <c>-i/-pix_fmt/色彩标注</c>，那些由调用方负责）。
    /// 语义与 <c>FfmpegCommandBuilder</c> 的对应分支**逐条对齐**（切换管线时产物必须可 A/B 对拍）。
    /// 不支持的格式返回空串（由调用方决定是否报错）。
    /// </summary>
    public static string BuildEncoderOptions(string format, FfmpegOptions o)
    {
        var fmt = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        var a = new List<string>(8);

        switch (fmt)
        {
            case "jpg":
            case "jpeg":
                // 出声：本出口（ffmpeg/mjpeg）吃不下「无损 JPEG」（mjpeg 有损 ⇒ `--lossless true` 无效，
                // 原先静默产有损）。渐进 JPEG 不在此处出声 —— 它由 `ColorEngineRouter.BlockReason`
                // 的 `ProgressiveJpeg` 拦截（auto 记日志后回退传统管线、显式 engine 硬失败），
                // 传统管线侧另有 `FfmpegCommandBuilder` 的告警。
                // ⚠ 文案一律 **ASCII**：`_probe-cjk-hardcode-scan.ps1` 的 `CsUi` 余量为 0。
                if (o.Lossless)
                    Console.WriteLine("⚠ [jpeg] --lossless true ignored: the mjpeg encoder is lossy, so the output is lossy. "
                        + "The only lossless JPEG path here is DNG container compression (--format dng --dng-compression 0).");
                if (o.EncoderBackend == EncoderBackend.Cjpegli)
                {
                    a.Add("-distance"); a.Add(MapJpegliDistance(o.Quality).ToString("F1"));
                }
                else
                {
                    a.Add("-q:v"); a.Add(MapJpegQuality(o.Quality).ToString());
                }
                if (!string.IsNullOrWhiteSpace(o.JpegHuffman))
                { a.Add("-huffman"); a.Add(o.JpegHuffman == "optimal" ? "1" : "0"); }
                if (!string.IsNullOrWhiteSpace(o.JpegDct) && o.JpegDct != "auto")
                { a.Add("-dct"); a.Add(o.JpegDct); }
                break;

            case "png":
            case "apng":
                a.Add("-compression_level");
                a.Add(o.Lossless ? "0" : MapPngCompression(o.Quality).ToString());
                if (!string.IsNullOrWhiteSpace(o.PngPred)) { a.Add("-pred"); a.Add(o.PngPred); }
                if (o.PngDpi is > 0) { a.Add("-dpi"); a.Add(o.PngDpi.Value.ToString()); }
                break;

            case "webp":
                if (o.Lossless)
                {
                    a.Add("-lossless"); a.Add("1");
                    // 显式 -q:v 100：保证 libwebp 以无损 hint 运行（与既有行为一致）
                    a.Add("-q:v"); a.Add("100");
                    var cl = Math.Clamp(o.WebpCompressionLevel ?? 4, 0, 6);
                    a.Add("-compression_level"); a.Add(cl.ToString());
                    // 无损下不传 picture/photo 预设（会配置有损量化参数，可能静默退化为有损）
                    if (o.WebpPreset is "default" or "none") { a.Add("-preset"); a.Add(o.WebpPreset); }
                    else if (!string.IsNullOrWhiteSpace(o.WebpPreset))
                        // 出声：无损模式下 preset 被**有意**丢弃，但用户看不到（ASCII 文案，理由见 jpg 分支）。
                        Console.WriteLine("⚠ [webp] --webp-preset ignored in lossless mode: presets configure lossy "
                            + "quantization and can silently degrade -lossless 1 to lossy.");
                }
                else
                {
                    a.Add("-q:v"); a.Add(o.Quality.ToString());
                    if (!string.IsNullOrWhiteSpace(o.WebpPreset) && o.WebpPreset != "none")
                    { a.Add("-preset"); a.Add(o.WebpPreset); }
                    // 出声：`-compression_level` 是无损模式的 zlib 级别，有损模式下无效（原先静默丢弃）。
                    if (o.WebpCompressionLevel.HasValue)
                        Console.WriteLine("⚠ [webp] --webp-compression ignored for lossy WebP: it maps to the "
                            + "lossless-mode zlib level (0-6); use --lossless true to apply it.");
                }
                break;

            case "tiff":
            case "tif":
                if (o.Lossless)
                {
                    a.Add("-compression_algo"); a.Add("raw");
                }
                else if (!string.IsNullOrWhiteSpace(o.TiffCompressionAlgo))
                {
                    a.Add("-compression_algo"); a.Add(o.TiffCompressionAlgo);
                }
                if (o.TiffDpi is > 0) { a.Add("-dpi"); a.Add(o.TiffDpi.Value.ToString()); }
                break;

            case "avif":
                a.AddRange(BuildAvifOptions(o));
                break;

            case "gif":
                a.AddRange(BuildGifOptions(o));
                break;

            case "jxl":
                a.AddRange(BuildJxlOptions(o));
                break;
        }

        return a.Count == 0 ? "" : " " + string.Join(' ', a);
    }

    /// <summary>
    /// **JXL 编码参数（单一真值，仅 ffmpeg libjxl 后端）**：传统管线
    /// <c>FfmpegCommandBuilder</c> 的 <c>case "jxl":</c> 与自研引擎出口共用本方法
    /// （2026-09-17 从传统管线**搬移**，逐项、逐顺序一致）。
    /// <para>
    /// ⚠ **只覆盖「静帧 + ffmpeg libjxl」子情形**（即本方法存在的全部理由）：
    /// ① **动图 JXL**（<c>libjxl_anim</c> + <c>fps/scale</c> 滤镜）不在此列 —— 引擎是单帧处理，
    ///    动图在路由层就被 `inputIsAnimated` 挡住（见 <c>ColorEngineRouter.BlockReason</c>）；
    /// ② **<c>JxlLosslessJpeg</c>（JPEG→JXL 免解码重封装 DCT 系数）**不在此列 —— 引擎的
    ///    执行体是"解码成 raw RGB → 重新编码"，**结构上不可能**复制 DCT 系数 ⇒ 该选项由
    ///    <see cref="UnsupportedEncoderSettings"/> 显式拒绝（否则用户选了无损重封装、
    ///    拿到的却是"解码再编码"的有损结果，且日志一句话都没有）；
    /// ③ **cjxl 后端**不在此列 —— cjxl 不吃 rawvideo，其参数由
    ///    <c>CjxlService.BuildCjxlArguments</c> 负责，路由层由 `backend != Ffmpeg` 拦下。
    /// </para>
    /// </summary>
    public static List<string> BuildJxlOptions(FfmpegOptions o)
    {
        var a = new List<string>(6);
        if (o.Lossless)
        {
            a.Add("-distance"); a.Add("0");
            if (o.JxlEffort.HasValue) { a.Add("-effort"); a.Add(o.JxlEffort.Value.ToString()); }
            if (o.JxlModular == true) { a.Add("-modular"); a.Add("1"); }
        }
        else
        {
            a.Add("-distance"); a.Add(MapJxlDistance(o.Quality).ToString("F1"));
            if (o.JxlEffort.HasValue) { a.Add("-effort"); a.Add(o.JxlEffort.Value.ToString()); }
            if (o.JxlModular == true) { a.Add("-modular"); a.Add("1"); }
        }
        return a;
    }

    /// <summary>
    /// **GIF 编码参数（单一真值）**：传统管线 <c>FfmpegCommandBuilder.BuildArguments</c> 的
    /// <c>case "gif":</c> 与自研引擎出口共用本方法（2026-09-17 从传统管线**整体搬移**，
    /// 逐项、逐顺序一致）。
    /// <para>
    /// ⚠ GIF 的编码器参数**只有 <c>-loop</c> 一项**（`-y`/`-c:v` 由调用方负责）；
    /// 其余"参数"其实是**滤镜**（palettegen/paletteuse），见
    /// <see cref="BuildGifFilterChain"/> —— 两者分开是因为传统管线把滤镜写成
    /// <c>-filter_complex</c>、引擎出口写成 <c>-filter_complex</c>（多输入），
    /// 而 <c>-loop</c> 在两者里都是普通输出选项。
    /// </para>
    /// <para>
    /// 判据 <c>AnimationLoop != -1</c> 与原实现逐字相同：默认 <c>-1</c> ⇒ **不写**（交 ffmpeg 默认）。
    /// </para>
    /// </summary>
    public static List<string> BuildGifOptions(FfmpegOptions o)
    {
        var a = new List<string>(2);
        if (o.AnimationLoop != -1) { a.Add("-loop"); a.Add(o.AnimationLoop.ToString()); }
        return a;
    }

    /// <summary>
    /// **GIF 调色板滤镜链（单一真值）**：传统管线与自研引擎出口共用（2026-09-17 从
    /// <c>FfmpegCommandBuilder</c> 的 <c>case "gif":</c> **整体搬移**，逐项、逐顺序一致）。
    ///
    /// <para>
    /// 为什么必须带这条链（实测，512×384 素材）：GIF 是调色板格式，
    /// **不带链也能出**（`-c:v gif` 自选量化），但质量与体积都差一个档：
    /// 不带链 18719B / PSNR **32.33dB**；带链（含 dither）**17032B / 44.33dB**。
    /// 传统管线默认 <c>GifPaletteOptimize=true</c> ⇒ 引擎不写这条链就会**静默降质**。
    /// </para>
    /// <para>
    /// <b>写成简单滤镜（<c>-vf</c>）还是复杂滤镜（<c>-filter_complex</c>）？</b>
    /// 实测两者产物**逐字节相同**（md5 <c>1299CCECB0A563464AA127789CC81355</c>）——
    /// 带标签的 <c>split[s0][s1]</c> 图只要单进单出，ffmpeg 就接受它出现在 <c>-vf</c> 里
    /// （仓内既有先例：<c>verify-decision-delivery.ps1</c> 造透明 GIF 素材时同样用 <c>-vf</c>）。
    /// ⇒ 引擎出口**不需要**为此新增 <c>-filter_complex</c> 通路；调用方按自己的需要选写法即可
    /// （传统管线必须用 <c>-filter_complex</c>：它要把色彩链与 palette 链合成一张图，
    /// 且 ffmpeg 不允许同一流同时用 simple/complex 滤镜）。
    /// </para>
    /// <para>
    /// <paramref name="leading"/> = 链首的其它滤镜（按给定顺序）。传统管线传**色彩转换链**
    /// （tonemap/zscale/iccgen，原先经 <c>AppendVideoFilter</c> 写成独立 <c>-vf</c>）；
    /// 引擎出口传**补 alpha 的 merge 图**（见 <c>RawColorPipeline.BuildEncodeArgs</c>）。
    /// 两者都必须排在 <c>fps/scale</c> 之前，与原实现一致。
    /// </para>
    /// <para>
    /// 返回 null 表示"什么都不用写"（<c>GifPaletteOptimize=false</c> 且无前置滤镜、无 fps/scale）
    /// —— 调用方**不得**把 null 当成空串写进命令行。
    /// </para>
    /// </summary>
    public static string? BuildGifFilterChain(FfmpegOptions o, params string?[] leading)
    {
        var filters = new List<string>(4);
        foreach (var l in leading)
            if (!string.IsNullOrWhiteSpace(l)) filters.Add(l!);
        if (o.AnimationFps.HasValue) filters.Add($"fps={o.AnimationFps.Value}");
        if (o.AnimationScaleW > 0) filters.Add($"scale={o.AnimationScaleW}:-1:flags=lanczos");

        // 关闭调色板优化 ⇒ 只写前置滤镜 + fps/scale（与原实现的 else 分支一致）
        if (!o.GifPaletteOptimize)
            return filters.Count == 0 ? null : string.Join(",", filters);

        var head = string.Join(",", filters);
        var chain = (head.Length == 0 ? "" : head + ",")
                  + "split[s0][s1];[s0]palettegen=reserve_transparent=1[p];[s1][p]paletteuse";
        if (o.GifDither)
            chain += "=dither=bayer:bayer_scale=5:diff_mode=rectangle";
        return chain;
    }

    /// <summary>
    /// **AVIF 编码参数（单一真值）**：传统管线 <c>FfmpegCommandBuilder.BuildArguments</c> 的
    /// <c>case "avif":</c> 与自研引擎出口共用本方法（2026-09-17 从传统管线**整体搬移**，
    /// 逐项、逐顺序一致，避免两处口径漂移）。
    /// <para>
    /// ⚠ **不含** <c>-c:v</c>（两处都在别处发）、**不含** GIF→AVIF 的两步滤镜链
    /// （<c>format=rgba,format=yuva420p</c>）—— 后者依赖输入路径，且 GIF→AVIF 走
    /// <c>avifenc.exe</c> 专用管线，不在引擎出口内。
    /// </para>
    /// <para>
    /// 覆盖：crf（无损 ⇒ 0）、<c>-cpu-used</c>、<c>-tune</c>（libaom）/ <c>-svtav1-params tune=</c>（SVT）、
    /// IQ 档（<c>-usage allintra</c> + <c>-aom-params tune=iq</c>）、<c>-still-picture</c>、<c>-row-mt</c>、
    /// SVT preset/tune、libaom 高级（aq-mode / cdef / intrabc / denoise）、
    /// 硬件编码器 7 档预设（nvenc/qsv/amf/vaapi）与旧字段回退、NVENC aq-strength+spatial-aq、
    /// QSV/VAAPI low_power。
    /// </para>
    /// </summary>
    public static List<string> BuildAvifOptions(FfmpegOptions o)
    {
        var args = new List<string>(16);
        if (o.Lossless)
        {
            args.Add("-crf");
            args.Add("0");
        }
        else
        {
            args.Add("-crf");
            args.Add(MapAvifCrf(o.Quality).ToString());
        }
        if (o.AvifCpuUsed.HasValue)
        { args.Add("-cpu-used"); args.Add(o.AvifCpuUsed.Value.ToString()); }
        var isSvt = o.Encoder?.StartsWith("libsvt", StringComparison.OrdinalIgnoreCase) == true;
        if (!string.IsNullOrWhiteSpace(o.AvifTune) && o.AvifTune != "默认")
        {
            switch (o.AvifTune)
            {
                case "PSNR":
                    if (isSvt) { args.Add("-svtav1-params"); args.Add("tune=1"); }
                    else { args.Add("-tune"); args.Add("psnr"); }
                    break;
                case "SSIM":
                    if (isSvt) { args.Add("-svtav1-params"); args.Add("tune=2"); }
                    else { args.Add("-tune"); args.Add("ssim"); }
                    break;
                case "VMAF":
                    if (isSvt) { args.Add("-svtav1-params"); args.Add("tune=3"); }
                    else { args.Add("-tune"); args.Add("vmaf_without_preprocessing"); }
                    break;
                case "IQ (图像优化)":
                    // libaom 仅支持 tune=iq（原始 aom 参数），SVT-AV1 不支持
                    if (!isSvt)
                    {
                        args.Add("-usage"); args.Add("allintra");
                        args.Add("-aom-params"); args.Add("tune=iq");
                    }
                    break;
            }
        }
        // 动图 AVIF: still-picture=0
        var isAnimated = o.AnimationFps.HasValue || o.AnimationLoop != 0 || o.AvifStillPicture == false;
        if (isAnimated)
        { args.Add("-still-picture"); args.Add("0"); }
        else if (o.AvifStillPicture == true)
        { args.Add("-still-picture"); args.Add("1"); }
        if (o.AvifRowMt == true)
        { args.Add("-row-mt"); args.Add("1"); }
        // IQ tune 已设置 -usage allintra，不再重复设置
        var iqActive = o.AvifTune == "IQ (图像优化)" && !isSvt;
        if (!iqActive && !string.IsNullOrWhiteSpace(o.AvifPreset) && o.AvifPreset != "auto")
        {
            // ── 编码器特定参数 ──
            if (isSvt)
            {
                // SVT-AV1: preset + tune
                if (o.AvifSvtPreset.HasValue)
                { args.Add("-preset"); args.Add(o.AvifSvtPreset.Value.ToString()); }
                if (!string.IsNullOrWhiteSpace(o.AvifSvtTune) && o.AvifSvtTune != "默认")
                {
                    var svtTuneVal = o.AvifSvtTune switch
                    {
                        "VMAF (主观)" => "1",
                        "PSNR" => "2",
                        "SSIM" => "3",
                        _ => "1"
                    };
                    args.Add("-svtav1-params"); args.Add($"tune={svtTuneVal}");
                }
                // SVT still-picture
                if (o.AvifStillPicture == true)
                { args.Add("-still-picture"); args.Add("1"); }
                else if (o.AvifStillPicture == false)
                { args.Add("-still-picture"); args.Add("0"); }
            }
        }
        // ── libaom-av1 高级图像选项 ──
        if (!isSvt)
        {
            // aq-mode: 自适应量化
            if (!string.IsNullOrWhiteSpace(o.AvifAqMode))
            { args.Add("-aq-mode"); args.Add(o.AvifAqMode); }
            // CDEF
            if (o.AvifEnableCdef == false)
            { args.Add("-enable-cdef"); args.Add("0"); }
            else if (o.AvifEnableCdef == true)
            { args.Add("-enable-cdef"); args.Add("1"); }
            // Intrabc (屏幕内容)
            if (o.AvifEnableIntrabc == false)
            { args.Add("-enable-intrabc"); args.Add("0"); }
            else if (o.AvifEnableIntrabc == true)
            { args.Add("-enable-intrabc"); args.Add("1"); }
            // 降噪 (denoise-noise-level)
            if (o.AvifDenoiseLevel.HasValue && o.AvifDenoiseLevel.Value > 0)
            { args.Add("-denoise-noise-level"); args.Add(o.AvifDenoiseLevel.Value.ToString()); }
        }
        // ── 硬件编码器预设 (新:精细7档) ──
        // 优先使用新预设级别，回退旧 AvifHwPreset 兼容
        var hwLevel = o.AvifHwPresetLevel;
        if (hwLevel >= 1 && hwLevel <= 7)
        {
            var enc = o.Encoder ?? "";
            if (enc.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase))
            { args.Add("-preset"); args.Add($"p{hwLevel}"); }
            else if (enc.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase))
            {
                var qsvPresets = new[] { "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };
                args.Add("-preset"); args.Add(qsvPresets[Math.Clamp(hwLevel - 1, 0, 6)]);
            }
            else if (enc.StartsWith("av1_amf", StringComparison.OrdinalIgnoreCase))
            { args.Add("-quality"); args.Add(hwLevel <= 2 ? "speed" : hwLevel <= 5 ? "balanced" : "quality"); }
            else if (enc.StartsWith("av1_vaapi", StringComparison.OrdinalIgnoreCase))
            { args.Add("-compression_level"); args.Add(hwLevel.ToString()); }
        }
        else if (!string.IsNullOrWhiteSpace(o.AvifHwPreset) && o.AvifHwPreset != "平衡")
        {
            // 旧字段回退兼容
            var enc2 = o.Encoder ?? "";
            if (enc2.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase))
            { args.Add("-preset"); args.Add(o.AvifHwPreset == "高质量" ? "p7" : "p1"); }
            else if (enc2.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase))
            { args.Add("-preset"); args.Add(o.AvifHwPreset == "高质量" ? "veryslow" : "veryfast"); }
            else if (enc2.StartsWith("av1_amf", StringComparison.OrdinalIgnoreCase))
            { args.Add("-quality"); args.Add(o.AvifHwPreset == "高质量" ? "quality" : "speed"); }
            else if (enc2.StartsWith("av1_vaapi", StringComparison.OrdinalIgnoreCase))
            { args.Add("-compression_level"); args.Add(o.AvifHwPreset == "高质量" ? "7" : "1"); }
        }
        // ── NVENC 高级选项 ──
        if (o.Encoder?.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (o.AvifNvencAqStrength.HasValue)
            { args.Add("-aq-strength"); args.Add(o.AvifNvencAqStrength.Value.ToString()); }
            if (o.AvifNvencSpatialAq == false)
            { args.Add("-spatial-aq"); args.Add("0"); }
            else if (o.AvifNvencSpatialAq == true)
            { args.Add("-spatial-aq"); args.Add("1"); }
        }
        // ── QSV/VAAPI 低功耗模式 ──
        if (o.Encoder?.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase) == true
            || o.Encoder?.StartsWith("av1_vaapi", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (o.AvifLowPower == true)
            { args.Add("-low_power"); args.Add("1"); }
            else if (o.AvifLowPower == false)
            { args.Add("-low_power"); args.Add("0"); }
        }
        return args;
    }

    /// <summary>
    /// AVIF 的**编码器特定位深上限**（单一真值）：<c>libaom-av1</c> / <c>av1_nvenc</c> 支持
    /// 8/10/12-bit；<c>libsvtav1</c> / <c>av1_qsv</c> / <c>av1_amf</c> / <c>av1_vaapi</c> 仅 8/10-bit。
    /// <para>
    /// ⚠ 与既有口径**逐字一致**（含"<c>options.Encoder</c> 为空 ⇒ 落到 10"这条隐含行为）：
    /// 传统管线在 <c>FfmpegCommandBuilder</c> 里用的是 <c>options.Encoder ?? ""</c> 做前缀匹配，
    /// 空串既不以 libaom 也不以 av1_nvenc 开头 ⇒ 上限 10。这里必须沿用同一判定，
    /// 否则引擎与传统管线在"未显式指定编码器"时会对同一个 16-bit 源给出不同位深。
    /// </para>
    /// <para>
    /// 实测（`ffmpeg -h encoder=`）：libaom-av1 支持 yuv420p/422p/444p/gbrp + 10le/12le + gray；
    /// libsvtav1 **只**支持 yuv420p / yuv420p10le。
    /// </para>
    /// </summary>
    public static int AvifMaxBitDepth(FfmpegOptions o)
    {
        var enc = o.Encoder ?? "";
        return (enc.StartsWith("libaom", StringComparison.OrdinalIgnoreCase)
             || enc.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase)) ? 12 : 10;
    }

    /// <summary>
    /// AVIF 出口的**有效位深**（引擎侧入口）。与传统管线
    /// <c>FfmpegCommandBuilder</c> 里的 <c>ClampAvifBd(EffectiveBitDepth())</c> **逐字等价**：
    /// <list type="number">
    /// <item>用户显式 <c>--bit-depth N</c> ⇒ <c>min(N, 编码器上限)</c>；</item>
    /// <item>否则用**探测到的源位深**：源 ≤ 8 ⇒ 8（传统管线此时**根本不写** <c>-pix_fmt</c>，
    ///       等价于编码器默认的 <c>yuv420p</c>；引擎显式写 <c>yuv420p</c> 结果相同）；</item>
    /// <item>源 &gt; 8 ⇒ <c>min(源位深, 编码器上限)</c>（16-bit 源 + libaom 默认 ⇒ 10，不是 16 —— 16-bit YUV 编码器不支持）。</item>
    /// </list>
    /// <para>
    /// ⚠ **为什么引擎不能只看 <c>plan.OutBitDepth</c>**：AVIF 是 YUV 容器、<c>RgbNative=false</c>，
    /// 规划层对 YUV 容器**恒给 16-bit 中间件**（见 <c>ColorStrategyPlanning</c> 的 <c>OutBitDepth</c> 注释）
    /// ⇒ 只用它会让 **8-bit 源也产出 10-bit AVIF**（文件体积翻倍、无任何质量收益），
    /// 与 legacy「8-bit 源出 8-bit」不一致。实测依据见本轮 A/B 报告。
    /// </para>
    /// </summary>
    public static int AvifEffectiveBitDepth(FfmpegOptions o, int sourceBitDepth)
    {
        var max = AvifMaxBitDepth(o);
        if (o.BitDepth.HasValue) return Math.Min(o.BitDepth.Value, max);
        return sourceBitDepth > 8 ? Math.Min(sourceBitDepth, max) : 8;
    }

    /// <summary>
    /// 格式 → <c>-pix_fmt</c>（**单一真值**，2026-09-17 从 <c>FfmpegCommandBuilder</c> 整体搬移，
    /// 原为 <c>private static MapPixFmt</c>，逻辑一字未改）。
    /// <para>
    /// 传统管线与自研引擎出口共用：引擎把变换后的 RGB 原始帧喂给编码器，必须显式给出
    /// YUV 的色度子采样与位深 —— 否则编码器按自己的默认猜（AVIF 会因此与 legacy 不一致）。
    /// </para>
    /// </summary>
    public static string MapPixFmt(string format, string? chroma, int bitDepth, bool hasAlpha, string? colorRange)
    {
        var fmt = (format ?? "").ToLower();
        var bd = bitDepth <= 0 ? 8 : bitDepth; // 非法/未知位深兜底 8
        // PNG/TIFF/APNG/JXL 均为 RGB 原生格式：JXL 使用 XYB 色彩空间，libjxl 编码器接受 RGB 输入。
        // 若喂入 yuv420p 会先做 YUV→RGB 转换（色度子采样损失）且奇宽奇高时 libjxl 拒绝。
        if (fmt is "png" or "tiff" or "apng" or "jxl")
        {
            if (bd <= 8) return hasAlpha ? "rgba" : "rgb24";
            return hasAlpha ? "rgba64le" : "rgb48le"; // 10/12/16 使用 48le 作为通用输出
        }

        // JPEG（mjpeg）：像素格式与色彩范围联动。
        // yuvj 系列 = full range（JPEG/JFIF 规范约定，默认；强制 yuv 系列会发灰）。
        // 用户显式选择 tv → yuv 系列（limited 编码，非标但尊重用户选择）。
        if ((fmt is "jpg" or "jpeg") && bd <= 8)
        {
            var isTv = colorRange?.Equals("tv", StringComparison.OrdinalIgnoreCase) == true;
            if (isTv)
            {
                return chroma switch
                {
                    "4:4:4" => "yuv444p",
                    "4:2:2" => "yuv422p",
                    _ => "yuv420p",
                };
            }
            return chroma switch
            {
                "4:4:4" => "yuvj444p",
                "4:2:2" => "yuvj422p",
                _ => "yuvj420p",
            };
        }

        // 透明通道处理（非 JPEG/JPEG XL/AVIF）：
        // libsvtav1 仅支持 yuva420p；libaom-av1 无 4:2:2 alpha 支持。
        // 统一回退到最兼容的 4:2:0 alpha。
        // 修复：AVIF 有 alpha 输入 → 回退普通 YUV（避免 libzimg 奇数尺寸 1027 错误）
        if (hasAlpha && fmt is not ("jpg" or "jpeg" or "avif"))
        {
            return bd switch
            {
                10 => "yuva420p10le",
                12 => "yuva420p12le",
                16 => "yuva420p16le",
                _ => "yuva420p",
            };
        }

        // AVIF 有 alpha 输入 → 回退普通 YUV，丢弃 alpha 确保兼容（防止 1027）
        if (hasAlpha && fmt == "avif")
        {
            // fall through to plain YUV below (alpha dropped)
        }

        // 默认使用 YUV pix fmt — 色度子采样在全部位深下均生效
        // 修复 Bug：此前 10/12/16-bit 恒返回 yuv420p*le，忽略 4:4:4/4:2:2 选择
        if (bd <= 8)
        {
            return chroma switch
            {
                "4:4:4" => "yuv444p",
                "4:2:2" => "yuv422p",
                _ => "yuv420p",
            };
        }

        var depth = bd switch
        {
            12 => "12",
            16 => "16",
            _ => "10",
        };
        return chroma switch
        {
            "4:4:4" => $"yuv444p{depth}le",
            "4:2:2" => $"yuv422p{depth}le",
            _ => $"yuv420p{depth}le",
        };
    }

    /// <summary>
    /// 引擎编码出口覆盖的格式（其余由外部编码器 / 专用管线负责）。
    /// <para>
    /// ⚠ **本方法是该清单的唯一真值**（2026-09-17 合并）：路由侧
    /// <see cref="ColorEngineRouter.BlockReason"/> 的 <c>supported</c> 判据改为**调用本方法**，
    /// 不再自持一份字面量 —— 此前两处各写一份（avif 轮遗留），而本方法当时**全仓无调用者**，
    /// 是典型的"文档指向一份没人用的副本"漂移温床。二者问的是同一个问题，故合并；
    /// 新增/移除出口格式**只需改这里**（回归锁：`ServiceProbe wire` 的"白名单必须恰为这 9 个"）。
    /// </para>
    /// <para>
    /// <c>gif</c> 于 **2026-09-17** 接入（见 <see cref="BuildGifFilterChain"/>：调色板链与
    /// <c>-loop</c> 与本方法同源）。⚠ 引擎出口对 GIF **不是**"直接喂 raw 就完事"：
    /// 调色板链的 <c>reserve_transparent=1</c> 依赖 alpha，而引擎 raw 通路是 rgb24/rgb48le
    /// ⇒ <c>RawColorPipeline</c> 会把**原文件**作为第二输入取 alpha 平面再 merge，否则透明会被静默拍平。
    /// </para>
    /// <para>
    /// <c>jxl</c> 于 **2026-09-17** 接入，**只覆盖「静帧 + ffmpeg libjxl」子情形**：
    /// cjxl 后端仍被 <c>ColorEngineRouter.BlockReason</c> 的 <c>backend != Ffmpeg</c> 挡住
    /// （cjxl 不吃 rawvideo）；动图 JXL 被 `inputIsAnimated` 挡住；
    /// <c>JxlLosslessJpeg</c> 被 <see cref="UnsupportedEncoderSettings"/> 挡住。
    /// ⚠ JXL 的标注只能走 **CICP**：ffmpeg libjxl 编码器**不落盘 ICC**
    /// （实测：<c>-vf iccgen</c> 的 ICC 被丢弃；exiftool 拒写 JXL，退出码 1）
    /// ⇒ 引擎出口对需要 ICC 的计划**显式拒绝**，见 <c>RawColorPipeline</c> 的 jxl 出口。
    /// </para>
    /// </summary>
    public static bool IsEngineEncodable(string? format)
        => (format ?? "").Trim().TrimStart('.').ToLowerInvariant()
           is "png" or "apng" or "tiff" or "tif" or "webp" or "jpg" or "jpeg" or "avif" or "gif" or "jxl" or "jxr";

    /// <summary>
    /// 引擎出口需要的 <c>-pix_fmt</c>。
    /// <para>
    /// **只覆盖 mjpeg**，且**总是**给出显式值（auto ⇒ 4:2:0）。为什么 auto 也要显式：
    /// 引擎把**RGB 原始帧**喂给编码器，mjpeg 会自选**最高质量**的 `yuvj444p`；
    /// 而 legacy 是从已解码的 YUV 帧出发、且其 `MapPixFmt` 在 auto 下落到 `yuvj420p`。
    /// A/B 实测差异：legacy `yuvj420p` 17837B vs 引擎 `yuvj444p` 26510B（PSNR 22.4dB）。
    /// 显式写出即与 legacy 逐字节对齐（420 也是 JPEG 的业界惯例）。
    /// </para>
    /// <para>
    /// ⚠ 其余格式**故意**返回 null：png/tiff 是 RGB 原生（色度无意义，写了反而可能与 cHRM 冲突）、
    /// libwebp 只接受 yuv420p/yuva420p/bgra（写 4:4:4 会编码失败）——这些由
    /// <see cref="UnsupportedEncoderSettings"/> 拒绝。
    /// </para>
    /// </summary>
    public static string? MapEnginePixFmt(string format, FfmpegOptions o)
    {
        var fmt = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (fmt is not ("jpg" or "jpeg")) return null;
        bool isTv = o.ColorRange?.Equals("tv", StringComparison.OrdinalIgnoreCase) == true;
        var y = isTv ? "yuv" : "yuvj";
        var sub = (o.Chroma ?? "auto").Trim().ToLowerInvariant() switch
        {
            "4:4:4" => "444p",
            "4:2:2" => "422p",
            _ => "420p",                       // auto / 4:2:0 / 未知 ⇒ 4:2:0（与 legacy 的 auto 落点一致）
        };
        return $" -pix_fmt {y}{sub}";
    }

    /// <summary>
    /// 本格式**真正无法承接**的输出参数（引擎出口用）。返回 null 表示全部可承接。
    /// <para>
    /// 与 <c>ColorEngineRouter.UnconsumedOutputSettings</c> 的分工：那里判定的是"能否交给引擎执行"，
    /// 含几何变换/动画/GainMap 等**结构上不属于引擎**的项；这里只判定"编码器参数是否吃得下"。
    /// </para>
    /// <para>
    /// 色度子采样按格式给结论：<c>libwebp</c> 只接受 yuv420p/yuva420p/bgra（**无 4:4:4**），
    /// 故 webp 下 4:4:4 / 4:2:2 属不可承接；mjpeg 支持 444/422/420，可承接；
    /// png/tiff 为 RGB 原生，色度参数**无意义**（既不算承接也不算缺失，与既有行为一致）；
    /// **AVIF + libsvtav1** 只接受 yuv420p / yuv420p10le，4:4:4 / 4:2:2 会被 ffmpeg **静默降为
    /// 4:2:0**（实测产物逐字节相同）⇒ 不可承接（libaom-av1 支持 444/422，可承接）。
    /// </para>
    /// </summary>
    public static string? UnsupportedEncoderSettings(string format, FfmpegOptions o)
    {
        var fmt = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        var bad = new List<string>(3);
        bool chromaSet = !string.IsNullOrWhiteSpace(o.Chroma)
                         && !o.Chroma.Equals("auto", StringComparison.OrdinalIgnoreCase);
        if (chromaSet && fmt == "webp" && o.Chroma is "4:4:4" or "4:2:2")
            bad.Add($"色度子采样 {o.Chroma}（libwebp 仅接受 4:2:0 / bgra，无 4:4:4）");
        // ── AVIF + libsvtav1：编码器只接受 yuv420p / yuv420p10le（实测 `ffmpeg -h encoder=libsvtav1`）──
        // ⚠ 这里拦的是**静默降级**，不是硬失败：实测 `-c:v libsvtav1 -pix_fmt yuv444p` 与
        //   `-pix_fmt yuv420p` 的产物**逐字节相同**（944B / CD38A739…），即 ffmpeg 把 4:4:4
        //   悄悄换成了 4:2:0 —— 用户要 4:4:4、拿到 4:2:0、日志里一句话都没有。
        //   引擎宁可拒绝（`auto` 下回退传统管线、显式 `engine` 下硬失败并点名），也不静默降级。
        if (chromaSet && fmt == "avif" && o.Chroma is "4:4:4" or "4:2:2"
            && o.Encoder?.StartsWith("libsvt", StringComparison.OrdinalIgnoreCase) == true)
            bad.Add($"色度子采样 {o.Chroma}（libsvtav1 仅接受 yuv420p / yuv420p10le，"
                  + "ffmpeg 会把 4:4:4/4:2:2 静默降为 4:2:0）");
        // ── JXL + JPEG 无损重封装：引擎结构上做不到（2026-09-17 接入 jxl 时新增）──
        // 传统管线把它实现为「不解码，直接复制 DCT 系数」（`FfmpegCommandBuilder` 的
        // `if (options.JxlLosslessJpeg)` 分支：只发 `-distance 0`，靠 FFmpeg 8.x+ 的 libjxl
        // 自动检测 JPEG 输入并无损重封装）。引擎的执行体是**解码成 raw RGB → 重新编码**，
        // 没有 DCT 系数可复制 ⇒ 用户选了「无损重封装」却会拿到「解码再编码」的有损产物，
        // 且日志不会提一个字。宁可拒绝并点名，也不静默换语义。
        if (fmt == "jxl" && o.JxlLosslessJpeg)
            bad.Add("JXL 的 JPEG 无损重封装（引擎需先解码成 raw RGB，无法复制 DCT 系数；"
                  + "请改用传统管线或 cjxl 后端）");
        return bad.Count == 0 ? null : string.Join("、", bad);
    }
}
