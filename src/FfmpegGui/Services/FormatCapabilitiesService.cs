using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    public class FormatCapabilities
    {
        public string Format { get; set; } = "";
        public bool SupportsQuality { get; set; }
        public bool SupportsChroma { get; set; }
        public bool SupportsBitDepth { get; set; }
        public bool SupportsMetadata { get; set; }
        public bool SupportsLossless { get; set; }
        public List<int> SupportedBitDepths { get; set; } = new List<int>();
        public List<string> SupportedColorSpaces { get; set; } = new List<string>();
        /// <summary>是否支持 Gain Map (Ultra HDR) 编码</summary>
        public bool SupportsGainMap { get; set; }
        /// <summary>
        /// ICC 嵌入支持级别:
        /// "native" = 编码器直接支持 -icc_profile
        /// "iccgen" = 需通过 iccgen 滤镜（FFmpeg ≥ 7.0 + lcms2）
        /// "" = 不支持
        /// </summary>
        public string IccEmbedSupport { get; set; } = "";
    }

    public static class FormatCapabilitiesService
    {
        private static readonly Dictionary<string, FormatCapabilities> _cache = new();

        public static FormatCapabilities? GetCapabilities(string format)
        {
            format = format.ToLower();
            if (_cache.TryGetValue(format, out var cap)) return cap;
            return null;
        }

        public static async Task InitializeAsync(string? ffmpegPath = null)
        {
            // 1) 本地静态规则
            SeedLocalRules();
            // 2) 远端查询已禁用（离线优先，无网络依赖）
            // 3) 检测本地 ffmpeg 支持的编解码器/像素格式
            await DetectLocalFfmpegCapabilitiesAsync(ffmpegPath);
        }

        private static void SeedLocalRules()
        {
            // JPEG: 仅 8-bit（JPEG XT 12-bit 不普及）
            _cache["jpg"] = new FormatCapabilities
            {
                Format = "jpg",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = false,
                SupportsMetadata = true,
                SupportsLossless = false,
                SupportsGainMap = true,
                SupportedBitDepths = new List<int> { 8 },
                SupportedColorSpaces = new List<string> { "BT.601", "BT.709", "BT.2020" }
            };

            // PNG: 1/2/4/8/16-bit，常用 8/16；HDR PNG 支持 BT.2020
            _cache["png"] = new FormatCapabilities
            {
                Format = "png",
                SupportsQuality = false,
                SupportsChroma = false,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportedBitDepths = new List<int> { 8, 16 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // WebP: 仅 8-bit（VP8 不支持高位深）
            _cache["webp"] = new FormatCapabilities
            {
                Format = "webp",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = false,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportedBitDepths = new List<int> { 8 },
                SupportedColorSpaces = new List<string> { "BT.709" }
            };

            // AVIF: AV1 编码，支持 8/10/12-bit
            _cache["avif"] = new FormatCapabilities
            {
                Format = "avif",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportedBitDepths = new List<int> { 8, 10, 12 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // TIFF: 支持 8/16-bit 整数（ffmpeg），部分场景可达 32-bit
            _cache["tiff"] = new FormatCapabilities
            {
                Format = "tiff",
                SupportsQuality = false,
                SupportsChroma = false,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportedBitDepths = new List<int> { 8, 16 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // GIF: 256 色调色板动图，8-bit
            _cache["gif"] = new FormatCapabilities
            {
                Format = "gif",
                SupportsQuality = true,
                SupportsChroma = false,
                SupportsBitDepth = false,
                SupportsMetadata = false,
                SupportsLossless = false,
                SupportedBitDepths = new List<int> { 8 },
                SupportedColorSpaces = new List<string>()
            };

            // APNG: 动画 PNG，支持 8/16-bit
            _cache["apng"] = new FormatCapabilities
            {
                Format = "apng",
                SupportsQuality = false,
                SupportsChroma = false,
                SupportsBitDepth = true,
                SupportsMetadata = false,
                SupportsLossless = true,
                SupportedBitDepths = new List<int> { 8, 16 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // JPEG XL: 支持 8/10/12/16-bit 整数，甚至 32-bit 浮点
            _cache["jxl"] = new FormatCapabilities
            {
                Format = "jxl",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportedBitDepths = new List<int> { 8, 10, 12, 16 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // JPEG XR: 支持 8/16/32-bit 整数/浮点，无损+有损，Alpha 通道
            _cache["jxr"] = new FormatCapabilities
            {
                Format = "jxr",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportedBitDepths = new List<int> { 8, 16, 32 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };
        }

        private static async Task DetectLocalFfmpegCapabilitiesAsync(string? ffmpegPath = null)
        {
            try
            {
                var fileName = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;
                if (string.IsNullOrWhiteSpace(fileName) || !System.IO.File.Exists(fileName))
                    return; // ffmpeg 不可用，跳过本地检测
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = "-hide_banner -pix_fmts",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return;
                var outp = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();

                // 简单判断是否支持特定像素格式
                foreach (var kv in _cache)
                {
                    var cap = kv.Value;
                    cap.SupportedColorSpaces = cap.SupportedColorSpaces ?? new List<string>();
                    void AddIfNotExists(string cs)
                    {
                        if (!cap.SupportedColorSpaces.Contains(cs, StringComparer.OrdinalIgnoreCase))
                            cap.SupportedColorSpaces.Add(cs);
                    }
                    if (outp.Contains("yuv444p")) AddIfNotExists("BT.709");
                    if (outp.Contains("yuv420p10le") || outp.Contains("yuv420p12le")) AddIfNotExists("BT.2020");
                }
            }
            catch { }
        }
    }
}
