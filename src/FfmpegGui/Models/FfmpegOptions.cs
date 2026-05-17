namespace FfmpegGui.Models
{
    public class FfmpegOptions
    {
        public string Format { get; set; } = "jpg";
        public int Quality { get; set; } = 75;
        public string Chroma { get; set; } = "4:2:0";
        public int BitDepth { get; set; } = 8;
        public string? ColorSpace { get; set; }
        public bool UseAdvancedColorParameters { get; set; } = false;
        public string? ColorPrimaries { get; set; }
        public string? ColorTrc { get; set; }
        public string? ColorMatrix { get; set; }
        public int Threads { get; set; } = ComputeAutoThreads();
        public bool PreserveMetadata { get; set; } = true;
        public string? Encoder { get; set; }
        public bool Lossless { get; set; } = false;
        // 高级编码器私有选项
        public string? PngPred { get; set; }
        public int? PngDpi { get; set; }
        public string? WebpPreset { get; set; }
        public int? AvifCpuUsed { get; set; }
        public bool? AvifStillPicture { get; set; }
        public int? JxlEffort { get; set; }
        public bool? JxlModular { get; set; }
        /// <summary>
        /// JPEG→JXL 无损重封装模式：不解码像素，直接复制 DCT 系数，速度极快且完全保留原图质量
        /// </summary>
        public bool JxlLosslessJpeg { get; set; } = false;
        public string? JpegHuffman { get; set; }
        public string? TiffCompressionAlgo { get; set; }
        public int? TiffDpi { get; set; }
        public string? AvifTune { get; set; }
        public string? AvifPreset { get; set; }

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
            "webp" => 95,
            "avif" => 85,
            "jxl" => 90,
            "tiff" => 0,
            _ => 92 // jpg 默认
        };

        /// <summary>
        /// 质量参数标签名
        /// </summary>
        public static string GetQualityLabel(string format, int quality) => format.ToLower() switch
        {
            "jpg" or "jpeg" => $"质量: {quality}% → q:v {2 + (int)Math.Round((100 - quality) * 29.0 / 100.0)}",
            "png" => $"压缩: {quality}% → level {(int)Math.Round((100 - quality) * 9.0 / 100.0)}",
            "webp" => $"质量: {quality}% → q:v {quality}",
            "avif" => $"质量: {quality}% → CRF {(int)Math.Round((100 - quality) * 63.0 / 100.0)}",
            "jxl" => $"质量: {quality}% → distance {((100 - quality) * 15.0 / 100.0):F1}",
            "tiff" => "质量: 不适用 (无损格式)",
            _ => $"质量: {quality}%"
        };
    }
}