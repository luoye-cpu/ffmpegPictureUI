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
            args.Add("-i");
            args.Add($"\"{inputPath}\"");

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
                case "jpegli":
                    args.Add("-q:v");
                    args.Add(MapJpegQuality(options.Quality).ToString());
                    if (!string.IsNullOrWhiteSpace(options.JpegHuffman))
                    { args.Add("-huffman"); args.Add(options.JpegHuffman); }
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
                        args.Add("-lossless");
                        args.Add("1");
                    }
                    else
                    {
                        args.Add("-q:v");
                        args.Add(options.Quality.ToString());
                    }
                    if (!string.IsNullOrWhiteSpace(options.WebpPreset) && options.WebpPreset != "none")
                    { args.Add("-preset"); args.Add(options.WebpPreset); }
                    break;
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
                    if (!string.IsNullOrWhiteSpace(options.AvifTune)
                        && int.TryParse(options.AvifTune, out var t))
                    {
                        if (options.Encoder?.StartsWith("libsvt", StringComparison.OrdinalIgnoreCase) == true)
                        { args.Add("-svtav1-params"); args.Add($"tune={t}"); }
                        else
                        { args.Add("-tune"); args.Add(t.ToString()); }
                    }
                    if (options.AvifStillPicture == true)
                    { args.Add("-still-picture"); args.Add("1"); }
                    if (!string.IsNullOrWhiteSpace(options.AvifPreset) && options.AvifPreset != "auto")
                    {
                        // libaom → -usage; SVT → -preset
                        if (options.Encoder?.StartsWith("libsvt", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            // SVT preset: map good→7, realtime→10 (approximate)
                            args.Add("-preset"); args.Add(options.AvifPreset == "realtime" ? "10" : "7");
                        }
                        else
                        {
                            args.Add("-usage"); args.Add(options.AvifPreset);
                        }
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
                    // --- JPEG→JXL 快速路径：不解码，直接复制 DCT 系数 ---
                    // 仅当输入为 JPEG 且启用 JxlLosslessJpeg 时才生效
                    // 此时忽略 distance/effort/modular 参数，ffmpeg 自动处理
                    if (options.JxlLosslessJpeg)
                    {
                        // distance=0 确保无损，配合 lossless_jpeg=1 跳过解码
                        args.Add("-distance");
                        args.Add("0");
                        // 核心参数：告诉 libjxl 输入是 JPEG，直接转码 DCT 系数
                        args.Add("-lossless_jpeg");
                        args.Add("1");
                        // effort 可保留用于 JXL 的压缩效率优化（可选）
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

            // 色度采样 或 位深 为 auto 则不指定 pix_fmt，由 ffmpeg 自动选择
            if (!string.IsNullOrWhiteSpace(options.Chroma) 
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
            }
            else
            {
                args.Add("-map_metadata");
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
    }
}