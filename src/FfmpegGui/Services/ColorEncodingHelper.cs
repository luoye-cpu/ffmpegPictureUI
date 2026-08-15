using System;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 色彩空间编码名称映射：将 FFmpeg 色彩参数翻译为 cjxl/cjpegli 的 -x color_space=... 参数。
    /// cjxl/cjpegli 需要显式的色彩编码名称来正确标记和编码原始像素数据（如 PPM 管道）。
    /// </summary>
    public static class ColorEncodingHelper
    {
        /// <summary>
        /// 根据 FfmpegOptions 计算 cjxl/cjpegli 的 -x color_space 值。
        /// 优先使用高级参数 (ColorPrimaries+ColorTrc)，否则回退到简化 ColorSpace。
        /// </summary>
        /// <returns>cjxl 兼容的色彩编码缩写；null 表示不指定（使用编码器默认）</returns>
        public static string? MapToCjxlColorSpace(Models.FfmpegOptions options)
        {
            // ── 高级模式：使用 primaries + transfer 精确映射 ──
            if (options.UseAdvancedColorParameters
                && !string.IsNullOrWhiteSpace(options.ColorPrimaries))
            {
                return MapPrimariesTransfer(options.ColorPrimaries, options.ColorTrc);
            }

            // ── 简化模式：根据 ColorSpace 下拉框映射 ──
            if (!string.IsNullOrWhiteSpace(options.ColorSpace)
                && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return options.ColorSpace switch
                {
                    "sRGB" => "sRGB",
                    "BT.709" => "sRGB",
                    "BT.2020 PQ" => "Rec2100PQ",
                    "BT.2020 HLG" => "Rec2100HLG",
                    // 兼容旧名称
                    "BT.2020" => "Rec2100PQ",
                    "BT.601" => "sRGB",
                    _ => null
                };
            }

            // auto / 未设置 → 不指定（编码器自动检测或使用默认 sRGB）
            return null;
        }

        /// <summary>
        /// 根据探测到的色彩元数据直接映射（用于 auto 模式下色彩自动检测）。
        /// 不限制位深：8-bit 广色域（如 P3 sRGB 内容）也能正确标记，
        /// PPM/PAM 管道流不携带色彩标签，必须依赖此映射显式标记。
        /// </summary>
        public static string? MapToCjxlColorSpace(FfmpegCommandBuilder.ColorMetadata hdrMeta)
        {
            return MapPrimariesTransfer(hdrMeta.colorPrimaries, hdrMeta.colorTrc);
        }

        /// <summary>
        /// 根据探测到的色彩元数据计算 --intensity_target（用于 auto 模式下 HDR 自动检测）。
        /// </summary>
        public static int MapToIntensityTarget(FfmpegCommandBuilder.ColorMetadata hdrMeta)
        {
            if (hdrMeta.bitDepth <= 8)
                return 0;
            var t = hdrMeta.colorTrc ?? "";
            if (t.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
                || t.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase))
                return 1000;
            return 0;
        }

        /// <summary>
        /// 根据 FfmpegOptions 计算 --intensity_target 值 (nits)。
        /// 仅对 HDR 传输函数 (PQ/HLG) 生效。
        /// </summary>
        /// <returns>nits 值；0 表示不指定</returns>
        public static int MapToIntensityTarget(Models.FfmpegOptions options)
        {
            // 检查是否为 HDR 传输函数
            var trc = options.ColorTrc;
            if (options.UseAdvancedColorParameters && !string.IsNullOrWhiteSpace(trc))
            {
                if (trc.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
                    || trc.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase))
                {
                    if (options.JpegGainMapTargetNits > 0)
                        return options.JpegGainMapTargetNits;
                    return 1000;
                }
            }

            // 简化模式：HDR (PQ/HLG) 需要设置 intensity_target
            if (!string.IsNullOrWhiteSpace(options.ColorSpace)
                && (options.ColorSpace.Equals("BT.2020 PQ", StringComparison.OrdinalIgnoreCase)
                    || options.ColorSpace.Equals("BT.2020 HLG", StringComparison.OrdinalIgnoreCase)
                    || options.ColorSpace.Equals("BT.2020", StringComparison.OrdinalIgnoreCase))
                && !options.UseAdvancedColorParameters)
            {
                if (options.JpegGainMapTargetNits > 0)
                    return options.JpegGainMapTargetNits;
                return 1000;
            }

            return 0;
        }

        /// <summary>
        /// 精确映射：primaries + transfer → cjxl color_space 缩写
        /// </summary>
        public static string? MapPrimariesTransfer(string? primaries, string? transfer)
        {
            var p = (primaries ?? "").ToLowerInvariant();
            var t = (transfer ?? "").ToLowerInvariant();

            // BT.2020 primaries
            if (p == "bt2020")
            {
                return t switch
                {
                    "smpte2084" => "Rec2100PQ",         // BT.2100 PQ (HDR)
                    "arib-std-b67" => "Rec2100HLG",     // BT.2100 HLG (HDR)
                    "linear" => "RGB_D65_202_Rel_Lin",  // BT.2020 Linear (HDR/float)
                    // BT.2020 SDR (bt709/bt1886 etc.)：cjxl 不支持此组合的命名
                    // 像素色彩由 ffmpeg 管道端处理，JXL 编码时将回退为默认 sRGB 标记
                    _ => null,
                };
            }

            // BT.709 / sRGB primaries
            if (p == "bt709" || p == "bt470bg" || p == "smpte170m")
            {
                return t switch
                {
                    "linear" => "RGB_D65_SRG_Rel_Lin",
                    _ => "sRGB",
                };
            }

            // Display P3
            if (p == "smpte432" || p.Contains("p3"))
            {
                return t switch
                {
                    "smpte2084" => "Rec2100PQ",  // Display P3 with PQ (not standard but map to Rec2100)
                    _ => "DisplayP3",
                };
            }

            // 仅有 transfer（无 primaries）
            if (string.IsNullOrWhiteSpace(p) && !string.IsNullOrWhiteSpace(t))
            {
                return t switch
                {
                    "smpte2084" => "Rec2100PQ",
                    "arib-std-b67" => "Rec2100HLG",
                    _ => null,
                };
            }

            return null;
        }
    }
}
