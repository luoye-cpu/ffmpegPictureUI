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
                    // ── 硬件编码器预设 ──
                    if (!string.IsNullOrWhiteSpace(options.AvifHwPreset) && options.AvifHwPreset != "平衡")
                    {
                        var enc = options.Encoder ?? "";
                        if (enc.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase))
                        {
                            // NVENC: p1(快) p4(平衡) p7(好)
                            args.Add("-preset"); args.Add(options.AvifHwPreset == "高质量" ? "p7" : "p1");
                        }
                        else if (enc.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase))
                        {
                            // QSV: veryfast(快) medium(平衡) veryslow(好)
                            args.Add("-preset"); args.Add(options.AvifHwPreset == "高质量" ? "veryslow" : "veryfast");
                        }
                        else if (enc.StartsWith("av1_amf", StringComparison.OrdinalIgnoreCase))
                        {
                            // AMF: speed(快) balanced(平衡) quality(好)
                            args.Add("-quality"); args.Add(options.AvifHwPreset == "高质量" ? "quality" : "speed");
                        }
                        else if (enc.StartsWith("av1_vaapi", StringComparison.OrdinalIgnoreCase))
                        {
                            // VAAPI: compression_level 1(快) 4(平衡) 7(好)
                            args.Add("-compression_level"); args.Add(options.AvifHwPreset == "高质量" ? "7" : "1");
                        }
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

            if (options.MetadataMode == Models.MetadataMode.StripAll)
            {
                args.Add("-map_metadata");
                args.Add("-1");
                args.Add("-map_chapters");
                args.Add("-1");
            }
            else
            {
                // 全局元数据映射（Exif/XMP/ICC 等）
                args.Add("-map_metadata");
                args.Add("0");
                // 流级别元数据映射（确保视频流中的色彩、旋转等标签保留）
                args.Add("-map_metadata:s:v");
                args.Add("0:s:v");
                // 章节信息映射（视频输入可能含章节）
                args.Add("-map_chapters");
                args.Add("0");
            }

            // 色彩参数：仅当勾选"使用高级色彩参数"时生效，否则按简化 ColorSpace
            if (options.UseAdvancedColorParameters
                && (!string.IsNullOrWhiteSpace(options.ColorPrimaries) 
                 || !string.IsNullOrWhiteSpace(options.ColorTrc) 
                 || !string.IsNullOrWhiteSpace(options.ColorMatrix)))
            {
                if (!string.IsNullOrWhiteSpace(options.ColorPrimaries)) { args.Add("-color_primaries"); args.Add(options.ColorPrimaries); }
                if (!string.IsNullOrWhiteSpace(options.ColorTrc)) { args.Add("-color_trc"); args.Add(options.ColorTrc); }
                if (!string.IsNullOrWhiteSpace(options.ColorMatrix)) { args.Add("-colorspace"); args.Add(options.ColorMatrix); }
            }
            else if (!string.IsNullOrWhiteSpace(options.ColorSpace)
                     && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-colorspace");
                args.Add(MapColorSpace(options.ColorSpace));
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

        private static string MapColorSpace(string displayName)
        {
            return displayName switch
            {
                "BT.601" => "bt470bg",
                "BT.709" => "bt709",
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
            // JPEG-LI butteraugli distance 0-15，同 JXL 尺度
            return Math.Round((100 - quality) * 15.0 / 100.0, 1);
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
    }
}