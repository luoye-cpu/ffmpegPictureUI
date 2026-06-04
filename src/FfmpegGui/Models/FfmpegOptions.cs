namespace FfmpegGui.Models
{
    /// <summary>
    /// 元数据处理模式
    /// </summary>
    public enum MetadataMode
    {
        PreserveAll,  // 保留所有元数据
        StripAll      // 删除所有元数据
    }

    public class FfmpegOptions
    {
        public string Format { get; set; } = "jpg";
        public int Quality { get; set; } = 75;
        public string Chroma { get; set; } = "4:2:0";
        /// <summary>
        /// 位深：null = auto（不指定，由编码器自行判断）
        /// </summary>
        public int? BitDepth { get; set; } = null;
        public string? ColorSpace { get; set; }
        public bool UseAdvancedColorParameters { get; set; } = false;
        public string? ColorPrimaries { get; set; }
        public string? ColorTrc { get; set; }
        public string? ColorMatrix { get; set; }
        public int Threads { get; set; } = ComputeAutoThreads();
        public MetadataMode MetadataMode { get; set; } = MetadataMode.PreserveAll;
        public string? Encoder { get; set; }
        /// <summary>编码器后端类型（由 UI 设置，供 QueueProcessor 调度）</summary>
        public Services.EncoderBackend EncoderBackend { get; set; } = Services.EncoderBackend.Ffmpeg;
        public bool Lossless { get; set; } = false;
        // 高级编码器私有选项
        public string? PngPred { get; set; }
        public int? PngDpi { get; set; }
        public string? WebpPreset { get; set; }
        /// <summary>WebP 无损模式压缩级别 (0-6, 0=最快)</summary>
        public int? WebpCompressionLevel { get; set; }
        public int? AvifCpuUsed { get; set; }
        public bool? AvifStillPicture { get; set; }
        public bool? AvifRowMt { get; set; }
        public int? JxlEffort { get; set; }
        public bool? JxlModular { get; set; }
        /// <summary>
        /// JPEG→JXL 无损重封装模式：不解码像素，直接复制 DCT 系数，速度极快且完全保留原图质量
        /// </summary>
        public bool JxlLosslessJpeg { get; set; } = false;
        /// <summary>cjxl 渐进式解码 (--progressive)</summary>
        public bool CjxlProgressive { get; set; } = false;
        /// <summary>cjxl 光子噪声 ISO (0=禁用, 100-3200)</summary>
        public int CjxlPhotonNoiseIso { get; set; } = 0;
        public string? JpegHuffman { get; set; }
        /// <summary>JPEG DCT 算法: "int" / "fastint" / "float"</summary>
        public string? JpegDct { get; set; }
        public string? TiffCompressionAlgo { get; set; }
        public int? TiffDpi { get; set; }
        public string? AvifTune { get; set; }
        public string? AvifPreset { get; set; }

        // ── cjpegli / jpegli 专属高级选项 ──
        /// <summary>色度子采样: "444", "422", "420", "440"</summary>
        public string CjpegliChromaSubsampling { get; set; } = "444";
        /// <summary>渐进模式: -1=自动, 0=基线, 1=渐进 (使用 jpegli 默认扫描脚本6)</summary>
        public int CjpegliProgressiveId { get; set; } = -1;
        /// <summary>Huffman 表优化</summary>
        public bool CjpegliOptimize { get; set; } = true;
        /// <summary>自适应量化</summary>
        public bool CjpegliAdaptiveQuant { get; set; } = true;
        /// <summary>编码器后端: "libjpeg" / "sjpeg"</summary>
        public string CjpegliEncoderBackend { get; set; } = "libjpeg";
        /// <summary>PSNR 目标 (sjpeg, 0=禁用)</summary>
        public float CjpegliPsnrTarget { get; set; } = 0;
        /// <summary>多线程是否可用（运行时检测，默认 false）</summary>
        public bool CjpegliMultiThreadAvailable { get; set; } = false;

        // ── ExifTool 隐私清理选项（仅在 exiftool 可用时生效）──
        /// <summary>删除 GPS 位置信息（默认勾选）</summary>
        public bool StripExifGps { get; set; } = true;
        /// <summary>删除拍摄时间日期</summary>
        public bool StripExifTime { get; set; } = false;
        /// <summary>删除相机/镜头型号与拍摄参数</summary>
        public bool StripExifCamera { get; set; } = false;
        /// <summary>删除全部 EXIF 数据</summary>
        public bool StripExifAll { get; set; } = false;
        /// <summary>删除 XMP 元数据</summary>
        public bool StripXmp { get; set; } = false;

        // ── 动图参数 ──
        /// <summary>帧率 (FPS)，null=不指定</summary>
        public int? AnimationFps { get; set; }
        /// <summary>循环次数: 0=无限, -1=不循环, >0=指定次数</summary>
        public int AnimationLoop { get; set; } = 0;
        /// <summary>GIF 调色板优化</summary>
        public bool GifPaletteOptimize { get; set; } = true;
        /// <summary>GIF 抖动处理</summary>
        public bool GifDither { get; set; } = true;
        /// <summary>动图缩放宽度 (0=保持原始)</summary>
        public int AnimationScaleW { get; set; } = 0;

        public static int ComputeAutoThreads()
        {
            int total = Environment.ProcessorCount;
            if (total >= 12) return Math.Max(1, total - 4);
            if (total > 4)   return Math.Max(1, total - 2);
            return Math.Max(1, total - 1);
        }

        /// <summary>
        /// 各格式视觉无损默认质量值 (0-100)
        /// </summary>
        public static int GetDefaultQuality(string format) => format.ToLower() switch
        {
            "png" => 100,
            "apng" => 100,
            "webp" => 95,
            "avif" => 90,
            "jxl" => 90,
            "tiff" => 0,
            "jpegli" => 92,
            "gif" => 90,
            _ => 92 // jpg 默认
        };

        /// <summary>butteraugli distance 0-15（用于 cjpegli 外部工具路径）</summary>
        public static double MapJpegliDistance(int quality) =>
            Math.Round((100 - quality) * 15.0 / 100.0, 1);

        // ── 正反映射：滑块 0-100 ↔ 各格式实际编码参数 ──

        // JPEG q:v 2-31（整数，越小质量越高）
        public static int MapJpegQualityForward(int quality) => (int)Math.Round(2 + (100 - quality) * 29.0 / 100.0);
        public static int MapJpegQualityInverse(double qv) => (int)Math.Round(100 - (Math.Clamp(qv, 2, 31) - 2) * 100.0 / 29.0);

        // JPEGli distance 0-15（1 位小数）
        public static double MapJpegliDistanceForward(int quality) => MapJpegliDistance(quality);
        public static int MapJpegliDistanceInverse(double d) => (int)Math.Round(100 - Math.Clamp(d, 0, 15) * 100.0 / 15.0);

        // PNG compression_level 0-9（整数，越大压缩越狠）
        public static int MapPngLevelForward(int quality) => (int)Math.Round((100 - quality) * 9.0 / 100.0);
        public static int MapPngLevelInverse(double level) => (int)Math.Round(100 - Math.Clamp(level, 0, 9) * 100.0 / 9.0);

        // WebP q:v 0-100（与滑块同尺度）
        public static int MapWebpQualityForward(int quality) => quality;
        public static int MapWebpQualityInverse(double qv) => (int)Math.Clamp(qv, 0, 100);

        // AVIF CRF 0-63（整数，越小质量越高）
        public static int MapAvifCrfForward(int quality) => (int)Math.Round((100 - quality) * 63.0 / 100.0);
        public static int MapAvifCrfInverse(double crf) => (int)Math.Round(100 - Math.Clamp(crf, 0, 63) * 100.0 / 63.0);

        // JXL distance 0-15（1 位小数）
        public static double MapJxlDistanceForward(int quality) => Math.Round((100 - quality) * 15.0 / 100.0, 1);
        public static int MapJxlDistanceInverse(double d) => (int)Math.Round(100 - Math.Clamp(d, 0, 15) * 100.0 / 15.0);

        /// <summary>滑块值 → 格式实际参数文本（用于输入框显示）</summary>
        public static string FormatQualityForDisplay(string fmt, int quality) => fmt.ToLower() switch
        {
            "jpg" or "jpeg" => MapJpegQualityForward(quality).ToString(),
            "jpegli" => MapJpegliDistanceForward(quality).ToString("F1"),
            "png" => MapPngLevelForward(quality).ToString(),
            "webp" => MapWebpQualityForward(quality).ToString(),
            "avif" => MapAvifCrfForward(quality).ToString(),
            "jxl" => MapJxlDistanceForward(quality).ToString("F1"),
            "tiff" => "N/A",
            _ => quality.ToString(),
        };

        /// <summary>格式实际参数 → 滑块值（用户输入解析用）</summary>
        public static int ParseQualityFromDisplay(string fmt, double value) => fmt.ToLower() switch
        {
            "jpg" or "jpeg" => MapJpegQualityInverse(value),
            "jpegli" => MapJpegliDistanceInverse(value),
            "png" => MapPngLevelInverse(value),
            "webp" => MapWebpQualityInverse(value),
            "avif" => MapAvifCrfInverse(value),
            "jxl" => MapJxlDistanceInverse(value),
            _ => (int)Math.Clamp(value, 0, 100),
        };

        /// <summary>质量参数标签名</summary>
        public static string GetQualityLabel(string format, int quality) => format.ToLower() switch
        {
            "jpg" or "jpeg" => $"质量: {quality}% → q:v {MapJpegQualityForward(quality)}",
            "jpegli" => $"质量: {quality}% → distance {MapJpegliDistanceForward(quality):F1}",
            "png" => $"压缩: {quality}% → level {MapPngLevelForward(quality)}",
            "webp" => $"质量: {quality}% → q:v {MapWebpQualityForward(quality)}",
            "avif" => $"质量: {quality}% → CRF {MapAvifCrfForward(quality)}",
            "jxl" => $"质量: {quality}% → distance {MapJxlDistanceForward(quality):F1}",
            "tiff" => "质量: 不适用 (无损格式)",
            _ => $"质量: {quality}%"
        };
    }
}