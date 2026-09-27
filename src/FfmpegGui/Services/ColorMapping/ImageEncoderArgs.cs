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
        // A-13（2026-09-26）：关闭抖动必须显式发 dither=none —— paletteuse 的官方默认是 sierra2_4a，
        // 省略参数不等于关闭（三向实测：bayer 29959 / 裸 paletteuse 30073 / dither=none 26714 B）。
        // 判据别拿 GIF 源做：pal8 无量化误差，五种 dither 产物逐字节相同 ⇒ 只有真彩色源才分得出。
        else
            chain += "=dither=none";
        return chain;
    }

    /// <summary>
    /// AVIF 的**编码器后端**（参数分域的唯一判据，2026-09-26 新增）。
    /// <para>
    /// **为什么需要它**：此前 <see cref="BuildAvifOptions"/> 只用一个 <c>isSvt</c> 布尔排除 libaom 项
    /// ⇒ 四个硬件后端照收 <c>-crf / -cpu-used / -still-picture / -row-mt / -aq-mode /
    /// -enable-cdef / -enable-intrabc / -usage / -aom-params / -tune</c>。其中三条**必然失败**（实测）：
    /// av1_amf 收 <c>-usage allintra</c> ⇒ Unable to parse、rc=127（AMF 也有同名私有项，但取值域是
    /// transcoding/lowlatency/webcam/high_quality/lowlatency_high_quality）；av1_nvenc 收
    /// <c>-tune psnr</c> 或 <c>-tune ssim</c> ⇒ Unable to parse、rc=127（NVENC 的 tune 域是
    /// hq/uhq/ll/ull/lossless）。其余是响亮失败的反面：ffmpeg 只告警
    /// <c>Codec AVOption ... has not been used</c>，产物不变。
    /// </para>
    /// <para>
    /// 各后端**真实存在**的质量轴与取值域（随包 ffmpeg git-2026-09-24 的
    /// <c>ffmpeg -h encoder=X</c> 自报表 + 产物核对，全部为本轮实测）：
    /// </para>
    /// <list type="table">
    /// <item><description><c>Libaom</c> = <c>libaom-av1</c>：<c>-crf</c> [-1..63]、<c>-cpu-used</c> [0..8]、
    /// <c>-aq-mode</c> [-1..4]（none/variance/complexity/cyclic）、<c>-row-mt</c>、<c>-still-picture</c>、
    /// <c>-enable-cdef</c>、<c>-enable-intrabc</c>、<c>-denoise-noise-level</c>、<c>-usage</c>
    /// （good/realtime/allintra）、<c>-tune</c>（**只有** psnr|ssim）、<c>-aom-params</c>（IQ 走
    /// <c>tune=iq</c>，实测 rc=0）。crf 0 实测 PSNR=inf ⇒ 真无损。</description></item>
    /// <item><description><c>Svt</c> = <c>libsvtav1</c>：<c>-crf</c> [0..63]（实测日志回显
    /// <c>BRC mode / rate factor : CRF / 30.00</c> ⇒ 真生效）、<c>-qp</c> [0..63]、<c>-preset</c>
    /// [-2..13]（实测 14 ⇒ out of range）、<c>-svtav1-params</c> dict（<c>tune</c> 域见
    /// <see cref="SvtTuneValue"/>、<c>lossless</c> 见 A-12）。**没有** <c>-still-picture</c> /
    /// <c>-cpu-used</c> / <c>-aq-mode</c>。</description></item>
    /// <item><description><c>Nvenc</c> = <c>av1_nvenc</c>：质量轴 <c>-cq</c> [0..63]（**0=auto**，
    /// 实测 1/10/20/45/63 = 27907/23815/17032/5429/1542 B）、<c>-preset</c> p1..p7、
    /// <c>-spatial-aq</c>（默认 0）、<c>-aq-strength</c> [1..15] 默认 8；<c>-low_power</c> 该编码器
    /// 的自报表里**没有**。<b>无损做不到</b>：<c>-tune lossless</c> 这一档在选项表里确实存在，但
    /// AV1 实测到开编码器时报 <c>not supported</c>、rc=127（<c>-rc constqp</c> 同），故无损勾选
    /// 只能落到 <c>-cq 1</c>（实测 PSNR 66.17dB，仍是有损）并如实出声。</description></item>
    /// <item><description><c>Qsv</c> = <c>av1_qsv</c>：自报表内**无** crf/cq/qp（实测 <c>-cq</c> 报
    /// has not been used），常量质量走通用 <c>-global_quality</c>；<c>-preset</c> 具名值
    /// veryfast/faster/fast/medium/slow/slower/veryslow（实测 <c>-preset p7</c> 被拒）；
    /// <c>-low_power</c> 存在。</description></item>
    /// <item><description><c>Amf</c> = <c>av1_amf</c>：常量质量 = <c>-rc cqp</c> + <c>-qp_i</c>/-qp_p
    /// [-1..255]（实测 300 ⇒ out of range [-1 - 255]）；另有 <c>-rc qvbr</c> +
    /// <c>-qvbr_quality_level</c> [-1..51]（实测 60 ⇒ out of range）；<c>-quality</c>
    /// 具名值 high_quality/quality/balanced/speed。</description></item>
    /// <item><description><c>Vaapi</c> = <c>av1_vaapi</c>：⚠ **随包构建不含该编码器**
    /// （<c>-h encoder=av1_vaapi</c> 报 <c>Codec is not recognized</c>，<c>-encoders</c> 里 av1 只有
    /// libaom/svt/d3d12va/nvenc/qsv/amf/mf/vulkan）⇒ 本机**无法**实测任何一条。质量轴取上游
    /// vaapi 共用码控读的 <c>AVCodecContext.global_quality</c>（通用项，域 INT_MIN..INT_MAX，
    /// ffmpeg 层不会越界），生效性未验。</description></item>
    /// <item><description><c>Other</c>：未识别的编码器（含本构建不含的 <c>librav1e</c>）只发
    /// <c>-crf</c>，不发任何私有项。</description></item>
    /// </list>
    /// </summary>
    private enum AvifBackend { Libaom, Svt, Nvenc, Qsv, Amf, Vaapi, Other }

    /// <summary>
    /// <c>FfmpegOptions.Encoder</c> → <see cref="AvifBackend"/>。传入的应是已剥掉图标与说明的**干净
    /// 编码器名**（采集侧走 <c>EncoderInfo.ParseEncoderName</c>）。
    /// <para>
    /// ⚠ 空串/未识别的兜底是**有意的分歧**：空串按 <c>Libaom</c> 处理（与旧实现的 <c>!isSvt</c> 口径
    /// 逐字一致 —— 旧代码在 Encoder 为空时照发 libaom 项；且不给 <c>-c:v</c> 时 ffmpeg 为 avif
    /// 选的默认编码器就是 libaom-av1），而**其它**未识别名字（librav1e 等）按 <c>Other</c> 只发 crf，
    /// 免得把 libaom 的私有项发到别人身上。
    /// </para>
    /// </summary>
    private static AvifBackend ClassifyAvifBackend(string? encoder)
    {
        var e = (encoder ?? "").Trim();
        if (e.Length == 0 || e.StartsWith("libaom", StringComparison.OrdinalIgnoreCase)) return AvifBackend.Libaom;
        if (e.StartsWith("libsvt", StringComparison.OrdinalIgnoreCase)) return AvifBackend.Svt;
        if (e.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase)) return AvifBackend.Nvenc;
        if (e.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase)) return AvifBackend.Qsv;
        if (e.StartsWith("av1_amf", StringComparison.OrdinalIgnoreCase)) return AvifBackend.Amf;
        if (e.StartsWith("av1_vaapi", StringComparison.OrdinalIgnoreCase)) return AvifBackend.Vaapi;
        return AvifBackend.Other;
    }

    /// <summary>
    /// tune 的**语言无关**归一（A-7）：两把 tune 下拉存进 <c>FfmpegOptions</c> 的都是
    /// **本地化之后的显示串**，旧代码直接拿中文字面量当键比对 ⇒ locale 实测
    /// <c>avif.tune.iq</c> 在 zh 是 IQ (图像优化)、在 en 是 IQ (Image Optimized) ⇒ 英文界面下
    /// 整条 IQ 什么都不发；而 <c>!= 默认</c> 这类哨兵在英文下恒真（进了 switch 又落空）。
    /// <para>
    /// 现在只认 token/前缀，zh 与 en 两种显示串折到同一个值；**默认档与不认识的档一律折成空串
    /// = 不发参数**（旧实现正是靠中文字面量识别默认档，才在英文下变成半生效状态 ⇒ 宁可什么都不发，
    /// 也不发一个错值）。当前 locale 里出现的 5 个值全部覆盖：默认/Default、PSNR、SSIM、VMAF、
    /// IQ (…)；另外采集侧在简单模式下硬塞的 VMAF (主观) 这类带尾巴的写法也能按前缀认出来。
    /// </para>
    /// </summary>
    private static string NormalizeTuneToken(string? display)
    {
        var u = ((display ?? "").Trim()).ToUpperInvariant();
        if (u.Length == 0) return "";
        if (u.StartsWith("IQ", StringComparison.Ordinal)) return "iq";
        if (u.Contains("MS_SSIM", StringComparison.Ordinal)) return "ms_ssim";
        if (u.Contains("VMAF", StringComparison.Ordinal)) return "vmaf";
        if (u.Contains("PSNR", StringComparison.Ordinal)) return "psnr";
        if (u.Contains("SSIM", StringComparison.Ordinal)) return "ssim";
        return "";
    }

    /// <summary>
    /// SVT-AV1 的 tune 取值（A-6：旧实现整体错位一格）。域**不是**凭记忆写的，取本机库的自报：
    /// 传 tune=6 时 SVT 自己打印
    /// <c>Invalid tune flag [0 - 5, 0 for VQ, 1 for PSNR, 2 for SSIM, 3 for IQ, 4 for MS_SSIM, and 5 for VMAF]</c>，
    /// 且逐个传 0..5 时 SVT 的配置回显（<c>preset / tune / pred struct</c> 行）依次是
    /// VQ / PSNR / SSIM / (IQ 直接 abort) / MS_SSIM / VMAF ⇒ 双向对齐。
    /// <para>
    /// <c>iq</c> 返回 null 是因为实测 <c>tune=3</c> 在随机访问结构下直接
    /// <c>Tune IQ only supports all-intra and low delay</c> + bad parameter ⇒ GUI 没有 all-intra 通路，
    /// 发出去就是 100% 失败（旧代码把 VMAF 折成 tune=3，恰好落在这个崩溃值上）。
    /// </para>
    /// </summary>
    private static string? SvtTuneValue(string token) => token switch
    {
        "psnr" => "1",
        "ssim" => "2",
        "vmaf" => "5",
        "ms_ssim" => "4",
        "vq" => "0",
        _ => null,
    };

    /// <summary>
    /// AV1 的 log-QP 域（0..63，libaom/SVT 的 crf）→ qindex 域（0..255，AMF 的 <c>-qp_i</c>/<c>-qp_p</c>）。
    /// <para>
    /// 逐值取本机 libaom 源码 <c>tools/src/aom/av1/encoder/av1_quantize.c</c> 的
    /// <c>quantizer_to_qindex[]</c> 表：0..61 段是 4 倍关系，表尾两项是 249 与 255（不是线性外推）。
    /// 下限取 1 是因为该 AVOption 的 -1 表示**不设**（会把 QP 交回码控自行决定），而滑块拉到最好
    /// 一档时语义应当是 QP 最低，不是把 QP 交出去。
    /// </para>
    /// <para>
    /// ⚠ 这条换算只在 <c>av1_amf</c> 用到，而本机**没有 AMD GPU**（amfrt64.dll 打不开、编码器开不起来）
    /// ⇒ 换算是**表算**、非实测：AMF 是否按 AV1 qindex 解释这个值、cqp 对 AV1 支持到不到位，均未验。
    /// </para>
    /// </summary>
    private static int AvifQIndexFromCrf(int crf)
    {
        var c = Math.Clamp(crf, 0, 63);
        return Math.Max(1, c >= 63 ? 255 : c == 62 ? 249 : c * 4);
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
    /// 覆盖范围与**参数分域**（2026-09-26 起重写，逐条依据见 <see cref="AvifBackend"/> 的实测表）：
    /// 质量轴按编码器选（libaom/SVT 用 <c>-crf</c>、NVENC 用 <c>-cq</c>、QSV/VAAPI 用
    /// <c>-global_quality</c>、AMF 用 <c>-rc cqp</c> + <c>-qp_i</c>/<c>-qp_p</c>）；
    /// libaom 私有项（<c>-cpu-used / -tune / -usage / -aom-params / -still-picture / -row-mt /
    /// -aq-mode / -enable-cdef / -enable-intrabc / -denoise-noise-level</c>）**只发 libaom**；
    /// SVT 发 <c>-preset</c> 与**唯一一个** <c>-svtav1-params</c> dict（<c>tune=</c>、无损时
    /// <c>lossless=1</c>）；硬件编码器发各自具名档位；NVENC 另有
    /// <c>-spatial-aq</c> + <c>-aq-strength</c>；<c>-low_power</c> 只发给 QSV（四支自报表里**只有
    /// av1_qsv 有这一项**，实测 nvenc/svt/amf 的表内 0 命中）与 VAAPI（沿用旧口径，本机未验）。
    /// </para>
    /// <para>
    /// ⚠ 显示串一律先过 <see cref="NormalizeTuneToken"/> 再比对（A-7：中文显示串当参数键在英文界面下
    /// 整条静默失效）；<c>-svtav1-params</c> 的多键必须合并成一次出现（实测重复出现是**整体替换**）。
    /// </para>
    /// </summary>
    public static List<string> BuildAvifOptions(FfmpegOptions o)
    {
        var args = new List<string>(16);
        var backend = ClassifyAvifBackend(o.Encoder);
        // GUI 只有**一条**质量滑块，先把它折成 AV1 的 log-QP 域（0..63，0 最好），再送到该编码器
        // **真实存在**的那条质量轴上。旧写法对四个硬件后端照发 -crf，而 crf 不是它们的 AVOption。
        var crf = o.Lossless ? 0 : MapAvifCrf(o.Quality);
        // SVT 的私有项统一攒成**一个** dict 再发：实测重复出现的 -svtav1-params 是**整体替换**而非
        // 合并（分两次发 tune=5 与 lossless=1 的产物，与只发 lossless=1 逐字节相同 e7816f68，
        // 而合并成 tune=5:lossless=1 是另一个产物 dff9419a）⇒ 多个键必须用 : 连在同一次出现里。
        var svtParams = new List<string>(2);

        // ── 质量轴（逐编码器；域与生效性见 ClassifyAvifBackend 的实测表）──
        switch (backend)
        {
            case AvifBackend.Nvenc:
                // NVENC AV1 的常量质量轴是 -cq（自报域 0..63，但 **0 是 auto 不是最好档**）⇒ 下限取 1。
                // 实测单调生效（512x512 同素材同构建）：cq 1/10/20/45/63 = 27907/23815/17032/5429/1542 B；
                // 对照旧写法：-crf 10 与 -crf 60 产物**同哈希** 28e1ac81（19696 B）且 ffmpeg 报
                // Codec AVOption crf has not been used ⇒ 滑块此前对 NVENC 完全无效。
                args.Add("-cq"); args.Add(Math.Clamp(crf, 1, 63).ToString());
                // NVENC AV1 **没有**无损档：实测 -tune lossless 与 -rc constqp 都报 not supported、rc=127
                // ⇒ 勾了无损只能落到 cq 下限（最好但仍有损，实测 -cq 1 的 PSNR 66.17dB 而非 inf）。
                if (o.Lossless)
                    Console.WriteLine("[avif/av1_nvenc] --lossless true is not honoured: the NVENC AV1 encoder "
                        + "has no lossless mode (ffmpeg -tune lossless / -rc constqp both fail with not "
                        + "supported). Falling back to the best constant-quality setting (-cq 1), which "
                        + "is still lossy. Use libaom-av1 or libsvtav1 for a truly lossless AVIF.");
                break;
            case AvifBackend.Qsv:
                // av1_qsv 的自报表里**没有** crf / cq / qp（实测 -cq 对它报 has not been used），
                // 常量质量输入是 AVCodecContext 的通用项 -global_quality（ICQ/QVBR 的取值来源）。
                // 上限压到 51 是为了落在 QSV 常量质量各档**共同**的域内（避免越界硬失败）。
                // ⚠ 本机无 Intel 核显（av1_qsv 一开编码器就失败）⇒ 只验到**参数名与 ffmpeg 层合法**，
                //   取值是否真改变产物**未验**。
                args.Add("-global_quality"); args.Add(Math.Clamp(crf, 1, 51).ToString());
                break;
            case AvifBackend.Amf:
                // av1_amf 的常量质量轴是 cqp 码控下的 -qp_i / -qp_p（自报域 -1..255，实测传 300 报
                // out of range [-1 - 255] ⇒ 域已核）。AMF 的 QP 是 qindex 域，不是 log-QP 域，
                // 故按 libaom 的 quantizer_to_qindex 表换算（见 AvifQIndexFromCrf）。
                // ⚠ 本机无 AMD GPU（amfrt64.dll 打不开）⇒ 生效性与 cqp 对 AV1 的支持度**未验**，
                //   仅保证 ffmpeg 层不发散（同一条命令换成 -usage allintra 时 rc=127 的解析失败可证）。
                var amfQp = AvifQIndexFromCrf(crf).ToString();
                args.Add("-rc"); args.Add("cqp");
                args.Add("-qp_i"); args.Add(amfQp);
                args.Add("-qp_p"); args.Add(amfQp);
                break;
            case AvifBackend.Vaapi:
                // ⚠ **随包 ffmpeg 根本不含 av1_vaapi**（-h encoder=av1_vaapi 报 Codec is not recognized，
                //   -encoders 里也只有 d3d12va / nvenc / qsv / amf / mf / vulkan）⇒ 本分支在本机
                //   无任何可实测依据。取上游 vaapi 共用码控读的那条轴（AVCodecContext.global_quality），
                //   它是通用 AVOption（域 INT_MIN..INT_MAX）⇒ 不可能在 ffmpeg 层越界；生效性**未验**。
                args.Add("-global_quality"); args.Add(Math.Clamp(crf, 1, 63).ToString());
                break;
            default:
                // libaom-av1 与 libsvtav1 **都**有真 -crf：自报表 libaom [-1..63]、libsvtav1 [0..63]；
                // SVT 实测日志回显 BRC mode / rate factor : CRF / 30.00 ⇒ 真生效。
                // 未知编码器（如本构建不含的 librav1e）也走这里：-crf 是各软件 AV1 编码器的公共写法。
                args.Add("-crf"); args.Add(crf.ToString());
                break;
        }
        // ── tune（A-6 逐值对表 + A-7 语言无关）──
        // 两把下拉存的都是**本地化之后的显示串**，旧代码拿中文字面量当键 ⇒ 英文界面下整条 IQ 静默失效、
        // 而 != 默认 这类哨兵在英文下恒真。先折成 token、再按编码器域分发（见 NormalizeTuneToken）。
        var tuneTok = NormalizeTuneToken(o.AvifTune);
        var svtTuneTok = NormalizeTuneToken(o.AvifSvtTune);
        switch (backend)
        {
            case AvifBackend.Libaom:
                switch (tuneTok)
                {
                    case "psnr":
                    case "ssim":
                        // libaom 的 -tune 自报域是 -1..1，即只有 psnr | ssim（其余值 Unable to parse、rc=127）
                        args.Add("-tune"); args.Add(tuneTok);
                        break;
                    case "iq":
                        // IQ 只能走原始 aom 参数：实测 -usage allintra + -aom-params tune=iq 为 rc=0
                        args.Add("-usage"); args.Add("allintra");
                        args.Add("-aom-params"); args.Add("tune=iq");
                        break;
                    case "vmaf":
                        // 本构建的 libaom **不带** VMAF tune：实测 -tune vmaf_without_preprocessing 与
                        // -tune vmaf 都是 Unable to parse、rc=127（该 AVOption 的域就到 ssim 为止），
                        // -aom-params tune=vmaf 也 rc=127 并提示需 -DCONFIG_TUNE_VMAF=1 ⇒ 发出去必崩。
                        // 旧代码正是在这里发了个不存在的值 ⇒ 选 VMAF 的 libaom 任务 100% 失败。
                        Console.WriteLine("[avif/libaom-av1] tune=vmaf is not available in this libaom build "
                            + "(-tune accepts only psnr | ssim, and -aom-params tune=vmaf fails with "
                            + "CONFIG_TUNE_VMAF), so no tuning flag is emitted. Switch encoder to libsvtav1 "
                            + "for a VMAF-tuned encode.");
                        break;
                }
                break;
            case AvifBackend.Svt:
                // SVT 的 tune 只能走 -svtav1-params（下面统一成一个 dict 发）。专用那把下拉优先于通用
                // tune 那把：两把同时有值时旧代码会发**两次** -svtav1-params，而后一次整体替换前一次。
                var sv = SvtTuneValue(svtTuneTok.Length > 0 ? svtTuneTok : tuneTok);
                if (sv != null)
                {
                    // 无损时逐档放行/拦下（实测 preset 7 + crf 0 + lossless=1）：
                    //   tune=0(VQ) / 1(PSNR) / 2(SSIM) ⇒ PSNR **inf**，无损成立；
                    //   tune=4(MS_SSIM) ⇒ 32.16dB、tune=5(VMAF) ⇒ 53.14dB ⇒ 这两个会**悄悄把
                    //   lossless=1 打掉**（产物仍是 rc=0、不报错，但已有损）。
                    // 无损承诺比调优指标重，且 GUI 的 SVT tune 默认档恰好是 VMAF ⇒ 必须拦。
                    if (!o.Lossless || sv == "0" || sv == "1" || sv == "2") svtParams.Add("tune=" + sv);
                    else
                        Console.WriteLine("[avif/libsvtav1] tune=" + sv + " dropped because lossless is on: with "
                            + "lossless=1 this tune silently voids losslessness (measured PSNR 53.14dB for "
                            + "tune=5 and 32.16dB for tune=4, versus inf for tune=0/1/2). The lossless "
                            + "promise wins here; turn lossless off to keep the tuning metric.");
                }
                else if (tuneTok == "iq" && svtTuneTok.Length == 0)
                    // IQ 在 SVT 里就是 tune=3，而实测（随机访问结构下）它直接 bad parameter：
                    // Tune IQ only supports all-intra and low delay ⇒ 本构建的 GUI 没有 all-intra 通路，
                    // 故不发。旧代码把 VMAF 折成 tune=3 恰好落在这个崩溃值上（见 A-6）。
                    Console.WriteLine("[avif/libsvtav1] tune=IQ is not usable on SVT-AV1 (tune=3 aborts with "
                        + "Tune IQ only supports all-intra and low delay), so no tuning flag is emitted. "
                        + "Use libaom-av1 for IQ.");
                break;
        }
        // ── 结构/速度轴 -cpu-used：libaom 私有项（自报域 0..8）──
        // 实测对 nvenc / svt 它只会得到 Codec AVOption cpu-used has not been used ⇒ 旧代码白发。
        // SVT 的速度轴是 -preset（GUI 已有 SvtPresetBox），不在此挪用 cpu-used 的档位。
        if (backend == AvifBackend.Libaom && o.AvifCpuUsed.HasValue)
        { args.Add("-cpu-used"); args.Add(o.AvifCpuUsed.Value.ToString()); }
        // ── 静帧 / 行并行：-still-picture 与 -row-mt 都是 **libaom 私有项** ──
        // 实测对 nvenc / svt 它们只会换来 Codec AVOption has not been used（libsvtav1 的自报表里
        // 根本没有 -still-picture ⇒ SVT 上不存在正确接法）。
        // ⚠ 顺带修掉一个恒真的动图判据：旧写法含 o.AnimationLoop != 0 一项，而**静帧任务**里
        //   MainWindow 给 AnimationLoop 的就是默认值 -1（-1 != 0 恒真）⇒ isAnimated 恒真 ⇒
        //   面板上默认勾选的静帧复选框只能发出 -still-picture 0，-still-picture 1 这一支**不可达**。
        //   现按字面语义判动图：给了 fps、或循环计数有效（0=无限循环、>0=有限次，-1=非动图模式）、
        //   或用户显式取消静帧。
        if (backend == AvifBackend.Libaom)
        {
            var isAnimated = o.AnimationFps.HasValue || o.AnimationLoop >= 0 || o.AvifStillPicture == false;
            if (isAnimated)
            { args.Add("-still-picture"); args.Add("0"); }
            else if (o.AvifStillPicture == true)
            { args.Add("-still-picture"); args.Add("1"); }
            if (o.AvifRowMt == true)
            { args.Add("-row-mt"); args.Add("1"); }
        }
        // ── SVT 的速度轴 -preset（自报域 -2..13；实测传 14 报 out of range [-2 - 13]）──
        else if (backend == AvifBackend.Svt)
        {
            // 旧实现把 -preset 绑在 AvifPreset 非空上、取值却来自 AvifSvtPreset ⇒ 预设里只带
            // AvifSvtPreset 时档位会被整条丢掉；这里直接按 AvifSvtPreset 判。
            if (o.AvifSvtPreset.HasValue)
            { args.Add("-preset"); args.Add(o.AvifSvtPreset.Value.ToString()); }
        }
        // A-12：SVT 的 crf 0 **不是**无损（实测 crf 0 = PSNR 45.89dB），真无损要 lossless=1
        // （实测 rc=0、PSNR=inf、25991 B @preset 7；对照 libaom 的 crf 0 本身就是 inf ⇒ 两编码器口径对齐）。
        // ⚠ 上面的 tune 分支会在全档被拦下时留下**只有 lossless=1** 的 dict，正是这条保住了无损。
        if (backend == AvifBackend.Svt && o.Lossless) svtParams.Add("lossless=1");
        if (svtParams.Count > 0)
        { args.Add("-svtav1-params"); args.Add(string.Join(":", svtParams)); }
        // ── libaom-av1 高级图像选项（这一组**全是 libaom 私有项**，A-9）──
        // 旧口径只用 !isSvt 排除 libaom 项 ⇒ 四个硬件后端照收 -aq-mode / -enable-cdef /
        // -enable-intrabc / -denoise-noise-level，实测每个都只得到 has not been used 告警。
        if (backend == AvifBackend.Libaom)
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
        // ── 硬件编码器的速度/质量预设（GUI 的 7 档下拉；A-9 的另一半：档位名也必须按编码器发）──
        // 档位名逐支核过自报表：
        //   av1_nvenc -preset 的具名值是 p1..p7（另有 slow|medium|fast），实测 -preset veryslow
        //     报 Unable to parse ⇒ 旧的 p{n} 写法正确；
        //   av1_qsv   -preset 的具名值是 veryfast..veryslow（实测 -preset p7 报 Unable to parse）；
        //   av1_amf   -quality 的具名值是 speed|balanced|quality|high_quality（实测 banana 被拒）；
        //   av1_vaapi -compression_level 是 AVCodecContext 的**通用**项（域 INT_MIN..INT_MAX，
        //     默认 -1）⇒ ffmpeg 层不会越界；上游 vaapi 侧把它当质量级读并再按设备能力钳制。
        var hwLevel = Math.Clamp(o.AvifHwPresetLevel, 1, 7);   // 越界档位在下面各轴上都可能硬失败 ⇒ 钳制
        switch (backend)
        {
            case AvifBackend.Nvenc:
                args.Add("-preset"); args.Add($"p{hwLevel}");
                break;
            case AvifBackend.Qsv:
            {
                var qsvPresets = new[] { "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };
                args.Add("-preset"); args.Add(qsvPresets[hwLevel - 1]);
                break;
            }
            case AvifBackend.Amf:
                args.Add("-quality"); args.Add(hwLevel <= 2 ? "speed" : hwLevel <= 5 ? "balanced" : "quality");
                break;
            case AvifBackend.Vaapi:
                args.Add("-compression_level"); args.Add(hwLevel.ToString());
                break;
        }
        // 旧实现在这里还有一条 回退旧 AvifHwPreset 的分支（拿 平衡 / 高质量 这类中文显示串当键，
        // 即 A-7 的第 393/398/400/402/404 行），**已删除**。删除依据（两条独立、都已核实）：
        //   ① 该分支不可达：进入条件是档位不在 1..7，而 AvifHwPresetLevel 默认 4、MainWindow 采的是
        //      7 或 1..7、PresetManagerService 的六处内置预设也全是 1..7 ⇒ 条件恒假；
        //   ② 依赖的字段恒空：HwPresetCombo 在 MainWindow.xaml 里**不存在**（grep 命中 0），
        //      采集侧只能给 null；FfmpegOptions.ApplyPreset 不搬 AvifHwPreset；CliParser 也没有对应键。
        // ── NVENC 的空间 AQ（A-4 / A-5）──
        if (backend == AvifBackend.Nvenc)
        {
            // 三向对照实测（固定 -cq 20）：省略 == -spatial-aq 0（同哈希 21eed557 / 17032 B），
            // 只有传 1 才变（6d0b6211 / 16636 B）⇒ NVENC 侧默认本就是 0 ⇒ 勾选**必须**显式发 1，
            // 否则该复选框两个状态等价（A-4 的成因）。三态仍区分：null = 用户没表态 ⇒ 不发。
            if (o.AvifNvencSpatialAq == true)
            { args.Add("-spatial-aq"); args.Add("1"); }
            else if (o.AvifNvencSpatialAq == false)
            { args.Add("-spatial-aq"); args.Add("0"); }

            // aq-strength：自报域 1..15、默认 8（实测传 0 与传 16 都是 out of range、rc=127）。
            // 0 在本文件里的语义是**不设该参数**（面板把 0 解释成关闭 AQ，而 0 不是合法取值）；
            // 越上界钳到 15，而不是发一个必崩的值。
            // 另外官方语义是**仅当 Spatial AQ 开启**该字段才参与（实测不开空间 AQ 时传 5、传 8
            // 与省略三者产物同哈希 21eed557）⇒ 空间 AQ 未开时不发它，并如实出声（否则又是一个
            // 看得见、按得动、但改变不了任何东西的旋钮）。
            var aqStrength = o.AvifNvencAqStrength;
            if (aqStrength.HasValue && aqStrength.Value > 0)
            {
                if (o.AvifNvencSpatialAq == true)
                { args.Add("-aq-strength"); args.Add(Math.Clamp(aqStrength.Value, 1, 15).ToString()); }
                else
                    Console.WriteLine("[avif/av1_nvenc] -aq-strength dropped because spatial AQ is off: NVENC "
                        + "only uses this field when -spatial-aq is 1 (verified: with spatial AQ off, "
                        + "strength 5 / strength 8 / omitting it all yield the identical file). Enable "
                        + "Spatial AQ to apply it. The value 0 means do not emit this option at all, "
                        + "because the encoder range starts at 1.");
            }
        }
        // ── QSV / VAAPI 低功耗模式（-low_power 在 av1_qsv 的自报表里确有此项）──
        if (backend == AvifBackend.Qsv || backend == AvifBackend.Vaapi)
        {
            if (o.AvifLowPower == true)
            { args.Add("-low_power"); args.Add("1"); }
            else if (o.AvifLowPower == false)
            { args.Add("-low_power"); args.Add("0"); }
        }
        return args;
    }

    /// <summary>
    /// AVIF 的**编码器特定位深上限**（单一真值），按 <see cref="ClassifyAvifBackend"/> 的同一套分类给：
    /// <c>libaom-av1</c> / <c>av1_nvenc</c> ⇒ 12；<c>libsvtav1</c> / <c>av1_qsv</c> / <c>av1_amf</c> /
    /// <c>av1_vaapi</c> / 未识别名 ⇒ 10。
    /// <para>
    /// ★ **空编码器名 ⇒ 12，不是 10**（任务 #36，2026-09-26 收口）。空名意味着命令里**不写** <c>-c:v</c>
    ///   ⇒ 由 ffmpeg 为 <c>.avif</c> 挑默认编码器，实测本仓随包构建挑的就是 <c>libaom-av1</c>
    ///   （<c>Stream #0:0 -> #0:0 (png (native) -> av1 (libaom-av1))</c>）。
    ///   <see cref="ClassifyAvifBackend"/> 一直是这么处理空串的（"不给 -c:v 时 ffmpeg 为 avif 选的默认
    ///   编码器就是 libaom-av1"），而本函数原先按 <c>options.Encoder ?? ""</c> 做前缀匹配 ⇒ 空串落到
    ///   else 支得 10。两条口径在同一个类里相隔 350 行、互相矛盾。
    ///   代价是实打实的：CLI/headless **没有**任何入口能写 <c>options.Encoder</c>（<c>--encoder</c> 映射到
    ///   <c>EncoderBackend</c> 枚举，不是编码器名）⇒ 命令行 <c>--bit-depth 12</c> 在裁决点被永久钳到 10，
    ///   并向用户播报"本工具对该容器实测可交付的上限 = 10bit" —— 那句是**假的**。
    ///   同一函数还被 UI 位深下拉的旧三元式（<c>? 12 : 10</c>）复制过一份，现由
    ///   <c>MainWindow.UpdateChromaBitDepthOptions</c> 改调本函数收掉。
    /// </para>
    /// <para>
    /// 实测各编码器自报的像素格式表（本仓随包 ffmpeg <c>-h encoder=</c>，2026-09-26）：
    /// libaom-av1 含 <c>yuv420p12le / yuv422p12le / yuv444p12le / gbrp12le</c>；
    /// av1_nvenc 含 <c>p012le / p212le / yuv444p12msble</c>；
    /// libsvtav1 只到 <c>yuv420p10le</c>；av1_qsv 只到 <c>p010le</c>；av1_amf 只到 <c>p010le</c>。
    /// ⇒ <c>tests/scripts/verify-decision-delivery.ps1</c> ⑯ 现场解析这张表，但它只对**两行**下判断：
    ///   「不写 -c:v 时 ffmpeg 实际选的默认编码器」与 <c>libsvtav1</c>（后者用来证明解析器能区分不同
    ///   编码器，否则"交付 == 实测上限"就是恒等式）⇒ 随包 ffmpeg 换了默认编码器会红在前提那条。
    /// ⚠ <c>av1_nvenc ⇒ 12</c> 这一行**本轮没有被端到端验过**，且已知可疑：本表只决定"允许到哪档"，
    ///   真正的像素格式名由 <see cref="MapPixFmt"/> 给（12bit ⇒ <c>yuv420p12le</c>），而 nvenc 的自报表里
    ///   **没有** <c>yuv420p12le</c>（只有 <c>p012le / p212le / yuv444p12msble</c>）⇒ 12bit + nvenc 大概率
    ///   会被编码器拒收。本机无 NVIDIA GPU（见本文件 <c>AvifBackend</c> 注："本机无法实测任何一条"）
    ///   ⇒ 登记为待办，不在本轮改（改它需要能起 nvenc 的机器）。libaom 一行是实测过的（⑯ 的前提条）。
    /// </para>
    /// </summary>
    public static int AvifMaxBitDepthForEncoder(string? encoder)
        => ClassifyAvifBackend(encoder) switch
        {
            AvifBackend.Libaom or AvifBackend.Nvenc => 12,
            _ => 10,
        };

    /// <summary>见 <see cref="AvifMaxBitDepthForEncoder"/>（按 <c>options.Encoder</c> 取档）。</summary>
    public static int AvifMaxBitDepth(FfmpegOptions o) => AvifMaxBitDepthForEncoder(o.Encoder);

    /// <summary>
    /// AVIF 出口的**有效位深**（引擎侧入口）。与传统管线
    /// <c>FfmpegCommandBuilder</c> 里的 <c>ClampAvifBd(EffectiveBitDepth())</c> **逐字等价**：
    /// <list type="number">
    /// <item>用户显式 <c>--bit-depth N</c> ⇒ <c>min(N, 编码器上限)</c>；</item>
    /// <item>否则用**探测到的源位深**：源 ≤ 8 ⇒ 8（传统管线此时**根本不写** <c>-pix_fmt</c>，
    ///       等价于编码器默认的 <c>yuv420p</c>；引擎显式写 <c>yuv420p</c> 结果相同）；</item>
    /// <item>源 &gt; 8 ⇒ <c>min(源位深, 编码器上限)</c>（未选编码器时上限 12 ⇒ 16-bit 源出 12bit，
    ///       不是 16 —— 16-bit YUV 编码器不支持）。</item>
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
