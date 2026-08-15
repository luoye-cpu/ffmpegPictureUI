using System;
using System.Collections.Generic;
using FfmpegGui.Models;

namespace FfmpegGui.Services
{
    public static class FfmpegCommandBuilder
    {
        public static string BuildArguments(FfmpegOptions options, string inputPath, string outputPath)
        {
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
            var (inputPrimaries, inputTrc, outputColorSpace) = BuildColorArgsSplit(options, inputPath);
            if (!string.IsNullOrWhiteSpace(inputPrimaries))
            {
                args.Add("-color_primaries"); args.Add(inputPrimaries);
            }
            if (!string.IsNullOrWhiteSpace(inputTrc))
            {
                args.Add("-color_trc"); args.Add(inputTrc);
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
            //  ICC 色彩管理 — 新模式 (CICP 始终启用)
            //  模式1 None:        仅 CICP, 丢弃 ICC
            //  模式2 CarryIcc:    保留源 ICC / 自动补充标准 ICC
            //  模式3 BakeToStandard: zscale 烘焙 + iccgen 标准 ICC
            //  模式4 BakeOnly:    zscale 烘焙，无 ICC 输出
            // ═══════════════════════════════════════════════════
            bool skipAutoBt2020Zscale = false;
            bool skipHdrTonemap = false;
            bool needsIccgen = false; // 是否需要 iccgen 生成标准 ICC

            // ── 模式 3/4: 烘焙像素 ──
            if (options.IccMode == Models.IccMode.BakeToStandard
                || options.IccMode == Models.IccMode.BakeOnly)
            {
                var srcParams = ResolveIccSourceParams(options);
                var dstParams = MapIccTargetColorSpace(options.IccTargetColorSpace);

                if (!ColorParamsEqual(srcParams, dstParams))
                {
                    // 覆盖输入色彩参数（源 = 源文件 ICC 描述的空间）
                    inputPrimaries = srcParams.primaries;
                    inputTrc = srcParams.trc;
                    // 输出 YUV 矩阵标记为目标空间
                    outputColorSpace = dstParams.matrix;

                    // zscale 烘焙 (含输入输出参数)
                    var zscaleFilter = BuildZscaleBakeFilter(srcParams, dstParams);
                    AppendVideoFilter(args, zscaleFilter);

                    skipAutoBt2020Zscale = true;
                    skipHdrTonemap = true;

                    // 模式 3: 烘焙后 iccgen 从帧元数据生成标准 ICC
                    // zscale 已更新帧的 primaries/trc 为目标值, iccgen 自动匹配
                    if (options.IccMode == Models.IccMode.BakeToStandard)
                        needsIccgen = true;
                }
            }
            // ── 模式 2: 携带 ICC (源有则保留，无则补标准 ICC) ──
            else if (options.IccMode == Models.IccMode.CarryIcc)
            {
                // 保留所有元数据 (包括 ICC Profile)
                // iccgen 为无 ICC 的源自动生成标准 sRGB ICC
                needsIccgen = true;
            }

            // ── HDR→SDR 色彩降级 ──
            if (!skipHdrTonemap)
            {
                var needsTonemap = NeedsHdrToSdrTonemap(options, inputPath, inputPrimaries, inputTrc);
                if (needsTonemap && !isRgbNativeFmt)
                {
                    // tonemap 滤镜内部自带色彩空间转换（HDR→linear→SDR），无需外部 zscale 链。
                    // 原实现 zscale=t=linear→tonemap→zscale 存在两个问题：
                    //   ① zscale (libzimg) 对 YUV 4:2:0 输入要求宽高可被子采样因子整除，
                    //      奇数尺寸（如 1921x1081）报 code 1027。
                    //   ② 中间 zscale 依赖帧色彩标签，无标签时报 3074 (no path between colorspaces)。
                    // format=yuv444p 前缀：4:4:4 无子采样约束，任意尺寸可用；也避免 4:2:0 色度损失。
                    //
                    // 2026-08-14 修复（实测驱动）:
                    //   tonemap 滤镜输出为 linear light 像素，帧色彩标签泄漏为 unspecified/linear，
                    //   直接输出会导致：① 容器 CICP 标签错误（PNG 实测 cICP=primaries=2,transfer=8）
                    //                    ② 像素未应用 gamma（实测 YAVG 12416 vs 正确 gamma 编码 30770，
                    //                       linear→gamma 关系精确吻合，画面明显过暗）
                    //   修复链: tonemap → RGB → zscale 应用目标 gamma + primaries 转换 → 正确像素+标签。
                    var tmSrcP = inputPrimaries ?? "bt2020";  // tonemap 保持输入 primaries，需转换到目标
                    var tmDstP = "bt709";
                    var tmDstT = "bt709";
                    if (!options.UseAdvancedColorParameters
                        && !string.IsNullOrWhiteSpace(options.ColorSpace)
                        && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        // 简化模式：目标为所选色彩空间（NeedsHdrToSdrTonemap 已排除 HDR 目标，此处恒为 SDR）
                        var target = MapSimplifiedColorSpace(options.ColorSpace);
                        tmDstT = target.trc ?? "bt709";
                        tmDstP = target.primaries ?? "bt709";
                    }
                    else if (options.UseAdvancedColorParameters
                             && !string.IsNullOrWhiteSpace(options.ColorTrc))
                    {
                        // 高级参数模式：目标为所选 trc/primaries
                        tmDstT = options.ColorTrc!;
                        tmDstP = options.ColorPrimaries ?? "bt709";
                    }
                    AppendVideoFilter(args,
                        $"format=yuv444p,tonemap=hable:param=0.5,format=rgb48le," +
                        $"zscale=pin={tmSrcP}:tin=linear:min=gbr:p={tmDstP}:t={tmDstT}:m=bt709");
                    skipHdrTonemap = true;
                }
            }

            // ── 简化模式目标色域转换（SDR→HDR / SDR→SDR 色域映射）──
            // 当用户选择非 auto 目标色域且未使用 ICC 烘焙/高级参数时，
            // 添加 zscale 滤镜将像素从「实际输入色彩」转换到「目标色彩」。
            // zscale 输出帧自动携带目标 primaries/trc 标签，编码器会传递到输出文件。
            // 注意：HDR→SDR 不在此处理（由 NeedsHdrToSdrTonemap 使用 tonemap 曲线）。
            if (!skipAutoBt2020Zscale
                && !options.UseAdvancedColorParameters
                && !string.IsNullOrWhiteSpace(options.ColorSpace)
                && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var targetParams = MapSimplifiedColorSpace(options.ColorSpace);
                if (targetParams.primaries != null && targetParams.trc != null)
                {
                    // 实际输入色彩（来自 BuildColorArgsSplit 的 -i 前声明）
                    var srcP = inputPrimaries ?? "bt709";
                    var srcT = inputTrc ?? "iec61966-2-1";
                    var srcM = outputColorSpace ?? "bt709";

                    var dstP = targetParams.primaries!;
                    var dstT = targetParams.trc!;
                    var dstM = targetParams.matrix ?? "bt709";

                    // 判断源是否为 HDR（PQ/HLG）→ 若目标是 SDR，交给 tonemap 处理
                    var srcIsHdr = srcT.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
                                || srcT.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
                    var dstIsHdr = dstT.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
                                || dstT.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);

                    // 仅在源≠目标时添加 zscale（避免无意义转换）
                    // 排除 HDR→SDR（由 tonemap 处理，避免高光裁剪）
                    if ((!string.Equals(srcP, dstP, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(srcT, dstT, StringComparison.OrdinalIgnoreCase))
                        && !(srcIsHdr && !dstIsHdr))
                    {
                        // format=rgb48le 前缀（RGB 域转换）：规避 libzimg 两个问题——
                        //  ① 对 YUV 4:2:0 输入的尺寸整除要求（奇数尺寸 1027 错误）
                        //  ② RGB 输入时无色彩标签导致的 3074 (no path between colorspaces)
                        // RGB 域无子采样约束，任意尺寸可用；16-bit 精度避免色度损失。
                        var zscaleFilter = $"format=rgb48le,zscale=pin={srcP}:tin={srcT}:min={srcM}:p={dstP}:t={dstT}:m={dstM}";
                        AppendVideoFilter(args, zscaleFilter);

                        // 更新输出矩阵标记（zscale 已转换像素，输出色彩空间为目标）
                        outputColorSpace = dstM;

                        // zscale 已完成色彩转换，跳过后续 tonemap
                        skipHdrTonemap = true;
                    }
                }
            }

            switch (fmt)
            {
                case "jpg":
                case "jpeg":
                    // ── Gain Map (Ultra HDR) 模式：使用 libultrahdr 编码器参数 ──
                    if (options.JpegGainMap && options.Encoder == "libultrahdr")
                    {
                        args.Add("-compression_q");
                        args.Add(options.Quality.ToString());
                        var gmq = options.JpegGainMapQuality >= 0
                            ? options.JpegGainMapQuality
                            : options.Quality;
                        args.Add("-gainmap_compression_q");
                        args.Add(gmq.ToString());
                        args.Add("-target_display_nits");
                        args.Add(options.JpegGainMapTargetNits.ToString());
                    }
                    else if (options.EncoderBackend == EncoderBackend.Cjpegli)
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
                    if (options.Lossless)
                    {
                        args.Add("-compression_level");
                        args.Add("0");
                    }
                    else
                    {
                        args.Add("-compression_level");
                        args.Add(MapPngCompression(options.Quality).ToString());
                    }
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
                        // compression_level 在无损模式下为 zlib 级别 (0-6)，做范围防护
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
                    }
                    else
                    {
                        args.Add("-q:v");
                        args.Add(options.Quality.ToString());
                        if (!string.IsNullOrWhiteSpace(options.WebpPreset) && options.WebpPreset != "none")
                        { args.Add("-preset"); args.Add(options.WebpPreset); }
                    }
                    // 动图 WebP: -loop 控制循环
                    if (options.AnimationLoop >= 0)
                    { args.Add("-loop"); args.Add(options.AnimationLoop.ToString()); }
                    break;
                case "gif":
                {
                    // 构建滤镜链（FPS + 缩放）
                    var filters = new List<string>();
                    if (options.AnimationFps.HasValue)
                        filters.Add($"fps={options.AnimationFps.Value}");
                    if (options.AnimationScaleW > 0)
                        filters.Add($"scale={options.AnimationScaleW}:-1:flags=lanczos");

                    if (options.GifPaletteOptimize)
                    {
                        var filterChain = string.Join(",", filters);
                        var complex = string.IsNullOrEmpty(filterChain)
                            ? $"split[s0][s1];[s0]palettegen=reserve_transparent=1[p];[s1][p]paletteuse"
                            : $"{filterChain},split[s0][s1];[s0]palettegen=reserve_transparent=1[p];[s1][p]paletteuse";
                        if (options.GifDither)
                            complex += "=dither=bayer:bayer_scale=5:diff_mode=rectangle";
                        args.Insert(1, "-filter_complex");
                        args.Insert(2, $"\"{complex}\"");
                    }
                    else
                    {
                        if (filters.Count > 0)
                        { args.Add("-vf"); args.Add(string.Join(",", filters)); }
                    }
                    if (options.AnimationLoop != -1)
                    { args.Add("-loop"); args.Add(options.AnimationLoop.ToString()); }
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
                    if (options.AnimationLoop >= 0)
                    { args.Add("-plays"); args.Add(options.AnimationLoop.ToString()); }
                    break;
                }
                case "avif":
                    if (options.Lossless)
                    {
                        args.Add("-crf");
                        args.Add("0");
                    }
                    else
                    {
                        args.Add("-crf");
                        args.Add(MapAvifCrf(options.Quality).ToString());
                    }
                    if (options.AvifCpuUsed.HasValue)
                    { args.Add("-cpu-used"); args.Add(options.AvifCpuUsed.Value.ToString()); }
                    var isSvt = options.Encoder?.StartsWith("libsvt", StringComparison.OrdinalIgnoreCase) == true;
                    if (!string.IsNullOrWhiteSpace(options.AvifTune) && options.AvifTune != "默认")
                    {
                        switch (options.AvifTune)
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
                    var isAnimated = options.AnimationFps.HasValue || options.AnimationLoop != 0 || options.AvifStillPicture == false;
                    if (isAnimated)
                    { args.Add("-still-picture"); args.Add("0"); }
                    else if (options.AvifStillPicture == true)
                    { args.Add("-still-picture"); args.Add("1"); }
                    if (options.AvifRowMt == true)
                    { args.Add("-row-mt"); args.Add("1"); }
                    // IQ tune 已设置 -usage allintra，不再重复设置
                    var iqActive = options.AvifTune == "IQ (图像优化)" && !isSvt;
                    if (!iqActive && !string.IsNullOrWhiteSpace(options.AvifPreset) && options.AvifPreset != "auto")
                    {
                        // ── 编码器特定参数 ──
                        if (isSvt)
                        {
                            // SVT-AV1: preset + tune
                            if (options.AvifSvtPreset.HasValue)
                            { args.Add("-preset"); args.Add(options.AvifSvtPreset.Value.ToString()); }
                            if (!string.IsNullOrWhiteSpace(options.AvifSvtTune) && options.AvifSvtTune != "默认")
                            {
                                var svtTuneVal = options.AvifSvtTune switch
                                {
                                    "VMAF (主观)" => "1",
                                    "PSNR" => "2",
                                    "SSIM" => "3",
                                    _ => "1"
                                };
                                args.Add("-svtav1-params"); args.Add($"tune={svtTuneVal}");
                            }
                            // SVT still-picture
                            if (options.AvifStillPicture == true)
                            { args.Add("-still-picture"); args.Add("1"); }
                            else if (options.AvifStillPicture == false)
                            { args.Add("-still-picture"); args.Add("0"); }
                        }
                    }
                    // ── libaom-av1 高级图像选项 ──
                    if (!isSvt)
                    {
                        // aq-mode: 自适应量化
                        if (!string.IsNullOrWhiteSpace(options.AvifAqMode))
                        { args.Add("-aq-mode"); args.Add(options.AvifAqMode); }
                        // CDEF
                        if (options.AvifEnableCdef == false)
                        { args.Add("-enable-cdef"); args.Add("0"); }
                        else if (options.AvifEnableCdef == true)
                        { args.Add("-enable-cdef"); args.Add("1"); }
                        // Intrabc (屏幕内容)
                        if (options.AvifEnableIntrabc == false)
                        { args.Add("-enable-intrabc"); args.Add("0"); }
                        else if (options.AvifEnableIntrabc == true)
                        { args.Add("-enable-intrabc"); args.Add("1"); }
                        // 降噪 (denoise-noise-level)
                        if (options.AvifDenoiseLevel.HasValue && options.AvifDenoiseLevel.Value > 0)
                        { args.Add("-denoise-noise-level"); args.Add(options.AvifDenoiseLevel.Value.ToString()); }
                    }
                    // ── 硬件编码器预设 (新:精细7档) ──
                    // 优先使用新预设级别，回退旧 AvifHwPreset 兼容
                    var hwLevel = options.AvifHwPresetLevel;
                    if (hwLevel >= 1 && hwLevel <= 7)
                    {
                        var enc = options.Encoder ?? "";
                        if (enc.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase))
                        { args.Add("-preset"); args.Add($"p{hwLevel}"); }
                        else if (enc.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase))
                        {
                            var qsvPresets = new[] { "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };
                            args.Add("-preset"); args.Add(qsvPresets[Math.Clamp(hwLevel - 1, 0, 6)]);
                        }
                        else if (enc.StartsWith("av1_amf", StringComparison.OrdinalIgnoreCase))
                        {
                            args.Add("-quality"); args.Add(hwLevel <= 2 ? "speed" : hwLevel <= 5 ? "balanced" : "quality");
                        }
                        else if (enc.StartsWith("av1_vaapi", StringComparison.OrdinalIgnoreCase))
                        { args.Add("-compression_level"); args.Add(hwLevel.ToString()); }
                    }
                    else if (!string.IsNullOrWhiteSpace(options.AvifHwPreset) && options.AvifHwPreset != "平衡")
                    {
                        // 旧字段回退兼容
                        var enc2 = options.Encoder ?? "";
                        if (enc2.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase))
                        { args.Add("-preset"); args.Add(options.AvifHwPreset == "高质量" ? "p7" : "p1"); }
                        else if (enc2.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase))
                        { args.Add("-preset"); args.Add(options.AvifHwPreset == "高质量" ? "veryslow" : "veryfast"); }
                        else if (enc2.StartsWith("av1_amf", StringComparison.OrdinalIgnoreCase))
                        { args.Add("-quality"); args.Add(options.AvifHwPreset == "高质量" ? "quality" : "speed"); }
                        else if (enc2.StartsWith("av1_vaapi", StringComparison.OrdinalIgnoreCase))
                        { args.Add("-compression_level"); args.Add(options.AvifHwPreset == "高质量" ? "7" : "1"); }
                    }
                    // ── NVENC 高级选项 ──
                    if (options.Encoder?.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (options.AvifNvencAqStrength.HasValue)
                        { args.Add("-aq-strength"); args.Add(options.AvifNvencAqStrength.Value.ToString()); }
                        if (options.AvifNvencSpatialAq == false)
                        { args.Add("-spatial-aq"); args.Add("0"); }
                        else if (options.AvifNvencSpatialAq == true)
                        { args.Add("-spatial-aq"); args.Add("1"); }
                    }
                    // ── QSV/VAAPI 低功耗模式 ──
                    if (options.Encoder?.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase) == true
                        || options.Encoder?.StartsWith("av1_vaapi", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (options.AvifLowPower == true)
                        { args.Add("-low_power"); args.Add("1"); }
                        else if (options.AvifLowPower == false)
                        { args.Add("-low_power"); args.Add("0"); }
                    }
                    // GIF → AVIF：两步滤镜链 —— pal8→rgba（保留透明索引→alpha）+ rgba→yuva420p（编码器格式）
                    if (inputPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    {
                        args.Add("-vf");
                        args.Add("format=rgba,format=yuva420p");
                    }
                    break;
                case "tiff":
                    if (options.Lossless)
                    {
                        args.Add("-compression_algo");
                        args.Add("raw");
                    }
                    else if (!string.IsNullOrWhiteSpace(options.TiffCompressionAlgo))
                    {
                        args.Add("-compression_algo");
                        args.Add(options.TiffCompressionAlgo);
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
                    // --- JPEG→JXL 快速路径：不解码，直接复制 DCT 系数 ---
                    // FFmpeg 8.x+ 中 libjxl 自动检测 JPEG 输入并启用无损重封装，
                    // 只需设置 -distance 0 即可。旧版 -lossless_jpeg 已移除。
                    if (options.JxlLosslessJpeg)
                    {
                        args.Add("-distance");
                        args.Add("0");
                        if (options.JxlEffort.HasValue)
                        { args.Add("-effort"); args.Add(options.JxlEffort.Value.ToString()); }
                    }
                    else if (options.Lossless)
                    {
                        args.Add("-distance");
                        args.Add("0");
                        if (options.JxlEffort.HasValue)
                        { args.Add("-effort"); args.Add(options.JxlEffort.Value.ToString()); }
                        if (options.JxlModular == true)
                        { args.Add("-modular"); args.Add("1"); }
                    }
                    else
                    {
                        args.Add("-distance");
                        args.Add(MapJxlDistance(options.Quality).ToString("F1"));
                        if (options.JxlEffort.HasValue)
                        { args.Add("-effort"); args.Add(options.JxlEffort.Value.ToString()); }
                        if (options.JxlModular == true)
                        { args.Add("-modular"); args.Add("1"); }
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
            var inputHasAlpha = GetCachedProbe(inputPath).hasAlpha;
            int EffectiveBitDepth()
            {
                if (options.BitDepth.HasValue) return options.BitDepth.Value;
                var detectedBd = ProbeInputBitDepth(inputPath);
                return detectedBd > 8 ? detectedBd : 8;
            }

            // ── AVIF 位深按编码器 clamp（2026-08-16 实测驱动）──
            // libaom-av1/av1_nvenc: 8/10/12-bit；libsvtav1/av1_qsv/av1_amf: 仅 8/10-bit。
            // 防止 16-bit 输入 auto 位深 → yuv420p16le 编码失败（libaom/libsvt 均不支持 16-bit YUV）。
            var avifMaxBd = int.MaxValue;
            if (fmt == "avif")
            {
                var enc = options.Encoder ?? "";
                avifMaxBd = (enc.StartsWith("libaom", StringComparison.OrdinalIgnoreCase)
                          || enc.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase)) ? 12 : 10;
            }
            int ClampAvifBd(int bd) => Math.Min(bd, avifMaxBd);

            if (!string.IsNullOrWhiteSpace(options.Chroma)
                && !options.Chroma.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-pix_fmt");
                args.Add(MapPixFmt(options.Format, options.Chroma, ClampAvifBd(EffectiveBitDepth()), inputHasAlpha, options.ColorRange));
            }
            else if (!options.BitDepth.HasValue)
            {
                // 位深为 auto：从输入文件自动探测实际位深并传递
                // 修复 HDR 图片（16-bit+）输出时退化为 8-bit 的问题
                var detectedBd = ProbeInputBitDepth(inputPath);
                if (detectedBd > 8)
                {
                    args.Add("-pix_fmt");
                    args.Add(MapPixFmt(options.Format, options.Chroma, ClampAvifBd(detectedBd), inputHasAlpha, options.ColorRange));
                }
            }
            else if (options.BitDepth.HasValue)
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
            bool stripAllMeta = options.IccMode == Models.IccMode.None
                             || options.IccMode == Models.IccMode.BakeOnly;

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

            // ── 非 CICP 格式 + 非 sRGB → 自动 ICC 嵌入 ──
            // JPEG/WebP/TIFF 不支持 CICP，若输出非 sRGB，需 iccgen 补充 ICC
            bool isNonCicpFormat = fmt is "jpg" or "jpeg" or "webp" or "tiff";
            bool isNonSrgb = options.ColorSpace != null
                          && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase)
                          && !options.ColorSpace.Equals("sRGB", StringComparison.OrdinalIgnoreCase);
            if (isNonCicpFormat && isNonSrgb && !needsIccgen)
            {
                needsIccgen = true;
            }

            // ═══════════════════════════════════════════════════
            //  ICC 嵌入 — iccgen 自动生成标准 ICC（模式2/3 + 非CICP格式补偿）
            //  CICP 始终启用（在所有模式中已通过 -color_primaries/-color_trc 传递）
            // ═══════════════════════════════════════════════════
            if (needsIccgen)
            {
                AppendVideoFilter(args, "iccgen");
            }

            // ── 输出色彩矩阵（YUV colorspace，必须在 -i 之后写入输出流）──
            // PNG/TIFF 是 RGB 原生格式，不需要 YUV 色彩矩阵参数
            if (!isRgbNativeFmt && !string.IsNullOrWhiteSpace(outputColorSpace))
            {
                args.Add("-colorspace"); args.Add(outputColorSpace);
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

            return string.Join(" ", args);
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

            // Ultra HDR 解码输出：显式标记 Rec.2100 PQ（优先级最高）
            if (!string.IsNullOrWhiteSpace(options.DecodedUltraHdrColorSpace))
            {
                primaries = "bt2020";
                trc = "smpte2084";       // PQ (SMPTE ST 2084)
                colorspace = "bt2020nc";
                return (primaries, trc, colorspace);
            }

            // 高级参数模式 或 管线注入的输入色彩声明（如 RAW 预处理标记 bt709/linear）。
            // 2026-08-14 修复：RAW 去马赛克输出为线性 16-bit TIFF，QueueProcessor 注入
            // ColorPrimaries=bt709 + ColorTrc=linear 描述输入；但 RAW 模式禁用高级色彩
            // 面板（UseAdvancedColorParameters=false），导致注入值被忽略 → 线性像素
            // 无标签输出 → PNG/TIFF/WebP 查看器按 sRGB 解释 → 画面明显过暗。
            // 修复：ColorPrimaries 与 ColorTrc 均非空时视为输入声明直接使用。
            if ((options.UseAdvancedColorParameters
                 || (!string.IsNullOrWhiteSpace(options.ColorPrimaries)
                     && !string.IsNullOrWhiteSpace(options.ColorTrc)))
                && (!string.IsNullOrWhiteSpace(options.ColorPrimaries)
                 || !string.IsNullOrWhiteSpace(options.ColorTrc)
                 || !string.IsNullOrWhiteSpace(options.ColorMatrix)))
            {
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
                if (hdrMeta.bitDepth > 8
                    && !string.IsNullOrWhiteSpace(hdrMeta.colorPrimaries)
                    && !string.IsNullOrWhiteSpace(hdrMeta.colorTrc))
                {
                    // 输入有明确色彩元数据（如真正的 HDR 文件）
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
                if (hdrMeta.bitDepth > 8)
                {
                    primaries = hdrMeta.colorPrimaries;
                    trc = hdrMeta.colorTrc;
                    colorspace = hdrMeta.colorSpace;
                }
            }

            return (primaries, trc, colorspace);
        }

        /// <summary>将 UI 简化色彩空间名称映射为 (primaries, trc, matrix)</summary>
        private static (string? primaries, string? trc, string? matrix) MapSimplifiedColorSpace(string displayName)
        {
            return displayName switch
            {
                "sRGB" => ("bt709", "iec61966-2-1", "bt709"),
                "BT.709" => ("bt709", "bt709", "bt709"),
                "Display P3" => ("smpte432", "iec61966-2-1", "bt709"),
                "P3 PQ" => ("smpte432", "smpte2084", "bt709"),
                "BT.2020 PQ" => ("bt2020", "smpte2084", "bt2020nc"),
                "BT.2020 HLG" => ("bt2020", "arib-std-b67", "bt2020nc"),
                // 兼容旧名称（逐步淘汰）
                "BT.601" => ("bt470bg", "bt470bg", "bt470bg"),
                "BT.2020" => ("bt2020", "smpte2084", "bt2020nc"),
                _ => (null, null, null)
            };
        }

        private static string MapColorSpace(string displayName)
        {
            return displayName switch
            {
                "sRGB" => "bt709",
                "BT.709" => "bt709",
                "Display P3" => "bt709",
                "P3 PQ" => "bt709",
                "BT.2020 PQ" => "bt2020nc",
                "BT.2020 HLG" => "bt2020nc",
                "BT.601" => "bt470bg",
                "BT.2020" => "bt2020nc",
                _ => displayName
            };
        }

        private static int MapJpegQuality(int quality)
        {
            // 将 0-100 映射到 ffmpeg JPEG qscale 2-31，数值越小质量越高
            return (int)Math.Round(2 + (100 - quality) * 29.0 / 100.0);
        }

        private static int MapPngCompression(int quality)
        {
            // compression_level 0-9，0 最快/最大体积，9 最慢/最小体积
            return (int)Math.Round((100 - quality) * 9.0 / 100.0);
        }

        private static int MapAvifCrf(int quality)
        {
            // avif 使用 crf 0-63，0 最好
            return (int)Math.Round((100 - quality) * 63.0 / 100.0);
        }

        private static double MapJxlDistance(int quality)
        {
            // JPEG XL distance 0-15，0=无损，15=最低质量
            return Math.Round((100 - quality) * 15.0 / 100.0, 1);
        }

        private static double MapJpegliDistance(int quality)
        {
            // JPEG-LI butteraugli distance 0-25（扩展范围，输出略小于同质量 mjpeg）
            return Math.Round((100 - quality) * 25.0 / 100.0, 1);
        }

        /// <summary>
        /// 根据输出格式、色度子采样、位深与输入透明通道映射 pix_fmt。
        /// 色度子采样在全部位深（8/10/12/16）下均生效。
        /// </summary>
        private static string MapPixFmt(string format, string? chroma, int bitDepth, bool hasAlpha, string? colorRange)
        {
            var fmt = format.ToLower();
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

            // 带透明通道的 YUV 输出：编码器对非 4:2:0 的 alpha 支持有限
            // （libsvtav1 仅 yuva420p；libaom-av1 无 4:2:2 alpha），
            // 统一回退到最兼容的 4:2:0 alpha 以保留透明通道。
            if (hasAlpha && fmt is not ("jpg" or "jpeg"))
            {
                return bd switch
                {
                    10 => "yuva420p10le",
                    12 => "yuva420p12le",
                    16 => "yuva420p16le",
                    _ => "yuva420p",
                };
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

            // 这些格式原生支持 HDR（>8bit），不需要 tonemap
            if (fmt is "avif" or "jxl" or "tiff" or "jxr") return false;
            // PNG 仅在 >8bit 时支持 HDR
            if (fmt is "png" or "apng")
            {
                var bd = options.BitDepth ?? ProbeInputBitDepth(inputPath);
                if (bd > 8) return false;
            }

            // ── 目标色彩空间检查：目标为 HDR (PQ/HLG) 时不需要 tonemap ──
            // HDR→HDR 转换（如 BT.2020 PQ→P3 PQ）由简化 zscale 分支处理；
            // 若此处不排除，tonemap 会先把 HDR 压成 SDR、zscale 再按 HDR 解译，
            // 造成双重转换错误。2026-08-14 修复。
            if (options.UseAdvancedColorParameters)
            {
                if (options.ColorTrc is "smpte2084" or "arib-std-b67")
                    return false;
            }
            else if (!string.IsNullOrWhiteSpace(options.ColorSpace)
                     && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var target = MapSimplifiedColorSpace(options.ColorSpace);
                if (target.trc is "smpte2084" or "arib-std-b67")
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
        }

        // ── ffprobe 探测缓存：同一输入文件在短时间内只探测一次 ──
        // 此前 BuildArguments 内部对同一文件最多起 3 次 ffprobe 进程（色彩/位深/范围），
        // 每条转换任务额外增加 ~30ms×N 进程开销。合并为单次查询 + 10 秒缓存。
        private static readonly Dictionary<string, (DateTime Time, ColorMetadata Meta)> ProbeCache = new();
        private const int ProbeCacheMax = 256;
        private const double ProbeCacheTtlSeconds = 10.0;
        private static readonly object ProbeCacheLock = new();

        /// <summary>带缓存的探测入口：同一输入 10 秒内只起一次 ffprobe 进程</summary>
        private static ColorMetadata GetCachedProbe(string inputPath)
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

            var meta = ProbeInputColorMetadataCore(inputPath);

            lock (ProbeCacheLock)
            {
                if (ProbeCache.Count >= ProbeCacheMax)
                    ProbeCache.Clear();  // 简单清空（缓存仅作短期去重，无需 LRU）
                ProbeCache[inputPath] = (DateTime.UtcNow, meta);
            }
            return meta;
        }

        /// <summary>使用 ffprobe/exiftool 同步探测输入文件的色彩元数据（带缓存，见 GetCachedProbe）</summary>
        public static ColorMetadata ProbeInputColorMetadata(string inputPath)
            => GetCachedProbe(inputPath);

        /// <summary>
        /// 探测核心实现（无缓存，供 GetCachedProbe 调用）。
        /// 双引擎：exiftool 优先（EXIF ColorSpace + ICC 语义），ffprobe 回退/补充（pix_fmt/位深/alpha）。
        /// - exiftool 可用：ColorSpace 标签 + ICC 描述推断色彩语义（相机 JPEG 广色域识别）
        /// - ffprobe 始终提供：pix_fmt → 位深/alpha/isRgb（exiftool 无此能力）
        /// - 语义合并：exiftool 有值 > ffprobe 值 > 默认
        /// </summary>
        private static ColorMetadata ProbeInputColorMetadataCore(string inputPath)
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
            var exifColorTags = Task.Run(() => ExifToolService.ReadColorTagsAsync(inputPath))
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
                    var (iccPath, iccDesc) = IccProfileService.ExtractIccToTempFile(inputPath);
                    if (iccPath != null)
                    {
                        iccGuessed = IccProfileService.GuessColorSpace(iccDesc);
                        IccProfileService.TryDeleteIcc(iccPath);
                    }
                }
                catch { }
            }

            // ── 色彩语义决策：ICC > EXIF ColorSpace ──
            if (!string.IsNullOrWhiteSpace(iccGuessed))
            {
                ApplyIccSemantics(meta, iccGuessed);
            }
            else if (!string.IsNullOrWhiteSpace(exifColorSpace))
            {
                // EXIF ColorSpace: 1=sRGB, 2=Adobe RGB（无 zscale 命名，近似 bt709 + 上层携带 ICC）
                if (exifColorSpace.Contains("Adobe", StringComparison.OrdinalIgnoreCase)
                    || exifColorSpace == "2")
                {
                    meta.colorPrimaries = "bt709";
                    meta.colorTrc = "bt709";
                    meta.hasIccSemantics = true; // 标记：上层应携带 ICC 而非仅靠标签
                }
                else if (exifColorSpace.Contains("sRGB", StringComparison.OrdinalIgnoreCase)
                         || exifColorSpace == "1")
                {
                    meta.colorPrimaries = "bt709";
                    meta.colorTrc = "iec61966-2-1";
                    meta.hasIccSemantics = true;
                }
            }

            // ── 2) ffprobe 补充：pix_fmt → 位深/alpha/isRgb（始终执行，exiftool 无此能力）──
            var ffMeta = ProbeWithFfprobe(inputPath);
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
            return meta;
        }

        /// <summary>将 ICC 推断的色彩空间名应用到 ColorMetadata（hasIccSemantics 标记供上层携带 ICC）</summary>
        private static void ApplyIccSemantics(ColorMetadata meta, string guessed)
        {
            meta.hasIccSemantics = true;
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
                    meta.colorTrc = "bt709";
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
                    // 无 zscale 命名 → 近似 bt709，上层携带 ICC
                    meta.colorPrimaries = "bt709";
                    meta.colorTrc = "bt709";
                    break;
            }
        }

        /// <summary>纯 ffprobe 探测（pix_fmt/色彩标签），供回退与补充使用</summary>
        private static ColorMetadata ProbeWithFfprobe(string inputPath)
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
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=bits_per_raw_sample,pix_fmt,color_primaries,color_transfer,color_space -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p == null) return meta;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                var parts = output.Split(',');
                // ⚠️ 关键：ffprobe 的 -show_entries stream=A,B,C 输出顺序为流内部固定顺序
                // （与参数书写顺序无关）。实测（ffmpeg 22.x / ffprobe 8.x）为：
                //   pix_fmt, color_space, color_transfer, color_primaries, bits_per_raw_sample
                // （注意：color_transfer 在 color_primaries 之前！2026-08-14 实测修正）
                // 因此：
                //   parts[0]=pix_fmt        → 位深/alpha/RGB 判断的来源
                //   parts[1]=color_space    → YUV 矩阵 (gbr/bt709/bt2020nc)
                //   parts[2]=color_transfer → 传输函数 (smpte2084/iec61966-2-1/...)
                //   parts[3]=color_primaries→ 原色 (bt709/bt2020/smpte432/...)
                // 兼容性：若未来版本输出更多字段，前 4 项顺序不变。
                if (parts.Length >= 1 && !string.IsNullOrEmpty(parts[0]))
                {
                    var pixFmt = parts[0];
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
                // parts[1] = color_space (YUV 矩阵)
                if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]) && parts[1] != "unknown" && parts[1] != "N/A")
                    meta.colorSpace = parts[1];
                // parts[2] = color_transfer (实测顺序: transfer 在 primaries 前)
                if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]) && parts[2] != "unknown" && parts[2] != "N/A")
                    meta.colorTrc = parts[2];
                // parts[3] = color_primaries
                if (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3]) && parts[3] != "unknown" && parts[3] != "N/A")
                    meta.colorPrimaries = parts[3];
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

        // ═══════════════════════════════════════════════════════════
        //  ICC 辅助方法
        // ═══════════════════════════════════════════════════════════

        /// <summary>解析 ICC 烘焙的源色彩空间参数</summary>
        private static (string primaries, string trc, string matrix) ResolveIccSourceParams(
            Models.FfmpegOptions options)
        {
            // 优先：用户手动选择的源色彩空间
            if (!string.IsNullOrWhiteSpace(options.IccSourceColorSpace))
                return MapNamedColorSpace(options.IccSourceColorSpace);

            // 其次：从 ICC 文件描述推断
            if (!string.IsNullOrWhiteSpace(options.IccFilePath))
            {
                var info = IccProfileService.ParseInfo(options.IccFilePath);
                var guessed = IccProfileService.GuessColorSpace(info?.Description);
                if (guessed != null)
                    return MapNamedColorSpace(guessed);
            }

            // 回退：假定 sRGB（安全默认值）
            return ("bt709", "iec61966-2-1", "bt709");
        }

        /// <summary>目标色彩空间名称 → (primaries, trc, matrix)</summary>
        private static (string primaries, string trc, string matrix) MapIccTargetColorSpace(
            string name)
        {
            var n = (name ?? "sRGB").ToLowerInvariant();
            if (n.Contains("srgb") || n.Contains("bt.709") || n.Contains("709") || n.Contains("iec61966"))
                return ("bt709", "iec61966-2-1", "bt709");
            if (n.Contains("adobe") || n.Contains("adobergb"))
                return ("bt709", "bt709", "bt709");
            if (n.Contains("display p3") || n.Contains("displayp3"))
                return ("smpte432", "bt709", "bt709");
            if (n.Contains("dci-p3") || n.Contains("dci p3"))
                return ("smpte431", "smpte428", "bt709");
            if (n.Contains("prophoto") || n.Contains("romm"))
                return ("bt470bg", "bt709", "bt709");
            if (n.Contains("rec.2020") || n.Contains("bt.2020") || n.Contains("rec2020"))
                return ("bt2020", "smpte2084", "bt2020nc");
            if (n.Contains("rec.2100") || n.Contains("bt.2100"))
                return ("bt2020", "smpte2084", "bt2020nc");
            // 默认 sRGB
            return ("bt709", "iec61966-2-1", "bt709");
        }

        /// <summary>常见色彩空间名称 → (primaries, trc, matrix)</summary>
        private static (string primaries, string trc, string matrix) MapNamedColorSpace(
            string name)
            => (name ?? "").ToLowerInvariant() switch
            {
                var n when n.Contains("srgb") || n.Contains("bt.709") || n.Contains("iec61966")
                    => ("bt709", "iec61966-2-1", "bt709"),
                var n when n.Contains("adobergb") || n.Contains("adobe rgb")
                    => ("bt709", "bt709", "bt709"),
                var n when n.Contains("display p3") || n.Contains("displayp3")
                    => ("smpte432", "bt709", "bt709"),
                var n when n.Contains("dci-p3") || n.Contains("dci p3")
                    => ("smpte431", "smpte428", "bt709"),
                var n when n.Contains("prophoto") || n.Contains("romm")
                    => ("bt470bg", "bt709", "bt709"),
                var n when n.Contains("rec.2020") || n.Contains("bt.2020") || n.Contains("rec2020")
                    => ("bt2020", "smpte2084", "bt2020nc"),
                var n when n.Contains("rec.2100") || n.Contains("bt.2100")
                    => ("bt2020", "smpte2084", "bt2020nc"),
                var n when n.Contains("colormatch")
                    => ("bt709", "bt709", "bt709"),
                _ => ("bt709", "iec61966-2-1", "bt709")
            };

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