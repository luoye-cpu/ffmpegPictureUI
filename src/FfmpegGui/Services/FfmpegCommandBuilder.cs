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
            var isRgbNativeFmt = fmt0 is "png" or "tiff" or "apng";
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
                    AppendVideoFilter(args,
                        "zscale=t=linear:npl=10000,tonemap=hable:param=0.5,zscale=t=bt709:m=bt709:r=tv,format=yuv420p");
                    skipHdrTonemap = true;
                }
            }

            // BT.2020 HDR: auto zscale (仅简化模式未烘焙时)
            if (!skipAutoBt2020Zscale
                && !options.UseAdvancedColorParameters
                && !string.IsNullOrWhiteSpace(options.ColorSpace))
            {
                var cs = options.ColorSpace;
                var isHdrTarget = cs.Equals("BT.2020 PQ", StringComparison.OrdinalIgnoreCase)
                               || cs.Equals("BT.2020 HLG", StringComparison.OrdinalIgnoreCase)
                               || cs.Equals("BT.2020", StringComparison.OrdinalIgnoreCase);
                if (isHdrTarget)
                {
                    var targetTrc = cs.Contains("HLG") ? "arib-std-b67" : "smpte2084";
                    var actualInputTrc = inputTrc;
                    if (string.IsNullOrWhiteSpace(actualInputTrc))
                        actualInputTrc = "bt709";
                    if (!actualInputTrc.Equals(targetTrc, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendVideoFilter(args, $"zscale=transferin={actualInputTrc}:transfer={targetTrc}");
                    }
                }
            }

            switch (fmt)
            {
                case "jpg":
                case "jpeg":
                    // ── ultrahdr_app 外部工具路径（EncoderBackend.Ultrahdr）──
                    // 不由 FFmpeg 处理，在 QueueProcessor.ProcessUltrahdrAsync 中调度
                    if (options.EncoderBackend == Services.EncoderBackend.Ultrahdr)
                    {
                        // 不生成 FFmpeg 命令，由外部工具路径处理
                        // 此处仍添加基本参数以确保 BuildArguments 输出非空
                    }
                    // ── Gain Map (Ultra HDR) 模式：使用 libultrahdr 编码器参数 ──
                    else if (options.JpegGainMap && options.Encoder == "libultrahdr")
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
                    { args.Add("-huffman"); args.Add(options.JpegHuffman); }
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
            // 色度采样 或 位深 为 auto 则不指定 pix_fmt，由 ffmpeg 自动选择
            else if (!string.IsNullOrWhiteSpace(options.Chroma) 
                && !options.Chroma.Equals("auto", StringComparison.OrdinalIgnoreCase)
                && options.BitDepth.HasValue)
            {
                args.Add("-pix_fmt");
                args.Add(MapPixFmt(options));
            }
            // 位深为 auto：从输入文件自动探测实际位深并传递
            // 修复 HDR 图片（16-bit+）输出时退化为 8-bit 的问题
            else if (!options.BitDepth.HasValue)
            {
                var detectedBd = ProbeInputBitDepth(inputPath);
                if (detectedBd > 8)
                {
                    var autoBdOptions = new FfmpegOptions
                    {
                        Format = options.Format,
                        Chroma = options.Chroma,
                        BitDepth = detectedBd
                    };
                    args.Add("-pix_fmt");
                    args.Add(MapPixFmt(autoBdOptions));
                }
            }
            // 位深已手动指定但色度为 auto：直接按位深设置 pix_fmt
            // （修复 Chroma=auto 时 BitDepth 设置失效的 Bug）
            else if (options.BitDepth.HasValue)
            {
                args.Add("-pix_fmt");
                args.Add(MapPixFmt(options));
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
        /// 这是关键修复：-color_primaries/-color_trc 必须放在 -i 之前，大多数编码器
        /// （AVIF/JXL/PNG/TIFF）才会将其传递到输出文件。
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

            if (options.UseAdvancedColorParameters
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
                (primaries, trc, colorspace) = MapSimplifiedColorSpace(options.ColorSpace);
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

        private static string MapPixFmt(FfmpegOptions options)
        {
            var fmt = options.Format.ToLower();
            var bd = options.BitDepth ?? 8; // null 理论不会到这里(外层已判断)，但兜底用 8
            if (fmt == "png" || fmt == "tiff")
            {
                if (bd <= 8) return "rgb24";
                return "rgb48le"; // 10/12/16 使用 48le 作为通用输出
            }

            // 默认使用 YUV pix fmt
            if (bd <= 8)
            {
                return options.Chroma switch
                {
                    "4:4:4" => "yuv444p",
                    "4:2:2" => "yuv422p",
                    _ => "yuv420p",
                };
            }

            if (bd == 10) return "yuv420p10le";
            if (bd == 12) return "yuv420p12le";
            if (bd == 16) return "yuv420p16le";
            return "yuv420p10le";
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

        /// <summary>使用 ffprobe 同步探测输入文件的位深</summary>
        private static int ProbeInputBitDepth(string inputPath)
        {
            try
            {
                var ffprobe = FindFfprobe();
                if (ffprobe == null) return 0;
                // 同时查询 bits_per_raw_sample 和 pix_fmt，优先前者，回退到后者
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=bits_per_raw_sample,pix_fmt -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p == null) return 0;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                // 格式: bits_per_raw_sample_value,pix_fmt_value
                var parts = output.Split(',');
                // 优先 bits_per_raw_sample
                if (parts.Length >= 1 && int.TryParse(parts[0], out var bd) && bd > 0) return bd;
                // 回退：从 pix_fmt 解析（rgb48le→16, yuv420p10le→10, gray16le→16）
                if (parts.Length >= 2) return ParseBitDepthFromPixFmt(parts[1]);
            }
            catch { }
            return 0;
        }

        /// <summary>从像素格式字符串中提取位深</summary>
        private static int ParseBitDepthFromPixFmt(string? pixFmt)
        {
            if (string.IsNullOrWhiteSpace(pixFmt)) return 0;
            // 常见格式: rgb48le→48/3=16, yuv420p10le→10, gray16le→16, rgba64le→64/4=16
            var match = System.Text.RegularExpressions.Regex.Match(pixFmt, @"(\d+)");
            if (!match.Success) return 0;
            var val = int.Parse(match.Value);
            if (pixFmt.StartsWith("rgb") || pixFmt.StartsWith("bgr") || pixFmt.StartsWith("gbr"))
                return val / 3;  // rgb48 → 16
            if (pixFmt.StartsWith("rgba") || pixFmt.StartsWith("bgra") || pixFmt.StartsWith("argb"))
                return val / 4;  // rgba64 → 16
            if (pixFmt.StartsWith("gray") || pixFmt.StartsWith("ya"))
                return val;      // gray16le → 16
            // YUV 格式: yuv420p10le → 10
            return val;
        }

        /// <summary>
        /// 判断是否需要 HDR→SDR 色调映射。
        /// 仅在输入明确为 HDR（PQ/HLG 传输函数 或 用户选择 BT.2020）时触发。
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

            // 仅当输入明确为 HDR（PQ/HLG 传输函数）时才触发 tonemap
            // 16-bit 无元数据 TIF 不满足此条件
            if (!string.IsNullOrWhiteSpace(inputTrc))
            {
                return inputTrc.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
                    || inputTrc.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>探测输入文件的色彩范围。RGB 像素格式返回 "pc"（全范围），否则返回 null（不覆盖默认）。</summary>
        private static string? ProbeInputColorRange(string inputPath)
        {
            try
            {
                var ffprobe = FindFfprobe();
                if (ffprobe == null) return null;
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p == null) return null;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);

                // RGB 系列像素格式是全范围（0-255, 0-65535）
                if (output.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)
                    || output.StartsWith("bgr", StringComparison.OrdinalIgnoreCase)
                    || output.StartsWith("gbr", StringComparison.OrdinalIgnoreCase)
                    || output.StartsWith("rgba", StringComparison.OrdinalIgnoreCase)
                    || output.StartsWith("bgra", StringComparison.OrdinalIgnoreCase)
                    || output.StartsWith("argb", StringComparison.OrdinalIgnoreCase))
                {
                    return "pc";
                }
            }
            catch { }
            return null;
        }

        public struct ColorMetadata
        {
            public int bitDepth;
            public string? colorPrimaries;
            public string? colorTrc;
            public string? colorSpace;
        }

        /// <summary>使用 ffprobe 同步探测输入文件的 HDR 色彩元数据</summary>
        public static ColorMetadata ProbeInputColorMetadata(string inputPath)
        {
            var meta = new ColorMetadata();
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
                // 格式: bits_per_raw_sample,pix_fmt,color_primaries,color_transfer,color_space
                var parts = output.Split(',');
                // 位深：优先 bits_per_raw_sample，回退 pix_fmt
                if (parts.Length >= 1 && int.TryParse(parts[0], out var bd) && bd > 0)
                    meta.bitDepth = bd;
                else if (parts.Length >= 2)
                    meta.bitDepth = ParseBitDepthFromPixFmt(parts[1]);
                if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]) && parts[2] != "unknown" && parts[2] != "N/A")
                    meta.colorPrimaries = parts[2];
                if (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3]) && parts[3] != "unknown" && parts[3] != "N/A")
                    meta.colorTrc = parts[3];
                if (parts.Length >= 5 && !string.IsNullOrEmpty(parts[4]) && parts[4] != "unknown" && parts[4] != "N/A")
                    meta.colorSpace = parts[4];
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