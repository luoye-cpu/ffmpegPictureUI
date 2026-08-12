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
        /// <summary>是否原生支持 CICP (H.273) 色彩标记 (primaries/trc/matrix)</summary>
        public bool SupportsCicp { get; set; }
        /// <summary>CICP 支持说明（用于 UI 提示）</summary>
        public string CicpNote { get; set; } = "";
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
            // JPEG: mjpeg 仅 8-bit YUV，不支持 CICP（用 EXIF/JFIF 色彩标签）
            _cache["jpg"] = new FormatCapabilities
            {
                Format = "jpg",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = false,
                SupportsMetadata = true,
                SupportsLossless = false,
                SupportsGainMap = true,
                SupportsCicp = false,
                CicpNote = "JPEG 使用 EXIF/JFIF 色彩标签，非 CICP；非 sRGB 时自动嵌入 ICC",
                SupportedBitDepths = new List<int> { 8 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // PNG: 8/16-bit RGB，cHRM 块保留 CICP
            _cache["png"] = new FormatCapabilities
            {
                Format = "png",
                SupportsQuality = false,
                SupportsChroma = false,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportsCicp = true,
                CicpNote = "PNG cHRM 块保留 primaries/trc",
                SupportedBitDepths = new List<int> { 8, 16 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // WebP: VP8 仅 8-bit，不支持 CICP
            _cache["webp"] = new FormatCapabilities
            {
                Format = "webp",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = false,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportsCicp = false,
                CicpNote = "WebP 不支持 CICP；非 sRGB 时自动嵌入 ICC",
                SupportedBitDepths = new List<int> { 8 },
                SupportedColorSpaces = new List<string> { "BT.709" }
            };

            // AVIF: libaom-av1 支持 8/10/12-bit YUV+RGB，原生 CICP
            _cache["avif"] = new FormatCapabilities
            {
                Format = "avif",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportsCicp = true,
                CicpNote = "AVIF 原生支持 CICP (H.273)，8/10/12-bit",
                SupportedBitDepths = new List<int> { 8, 10, 12 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // TIFF: 8/16-bit RGB/YUV，使用 ICC Profile（非 CICP）
            _cache["tiff"] = new FormatCapabilities
            {
                Format = "tiff",
                SupportsQuality = false,
                SupportsChroma = false,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportsCicp = false,
                CicpNote = "TIFF 使用 ICC Profile；非 sRGB 时自动嵌入 ICC",
                SupportedBitDepths = new List<int> { 8, 16 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // GIF: 256 色调色板，无色彩管理
            _cache["gif"] = new FormatCapabilities
            {
                Format = "gif",
                SupportsQuality = true,
                SupportsChroma = false,
                SupportsBitDepth = false,
                SupportsMetadata = false,
                SupportsLossless = false,
                SupportsCicp = false,
                CicpNote = "GIF 无色彩管理（256 色调色板）",
                SupportedBitDepths = new List<int> { 8 },
                SupportedColorSpaces = new List<string>()
            };

            // APNG: 动画 PNG，同 PNG 能力
            _cache["apng"] = new FormatCapabilities
            {
                Format = "apng",
                SupportsQuality = false,
                SupportsChroma = false,
                SupportsBitDepth = true,
                SupportsMetadata = false,
                SupportsLossless = true,
                SupportsCicp = true,
                CicpNote = "APNG 同 PNG，cHRM 块保留 CICP",
                SupportedBitDepths = new List<int> { 8, 16 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // JPEG XL: libjxl 8/16/32-bit RGB，原生 CICP
            _cache["jxl"] = new FormatCapabilities
            {
                Format = "jxl",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportsCicp = true,
                CicpNote = "JXL 原生 CICP + ICC，支持 8/16/32-bit",
                SupportedBitDepths = new List<int> { 8, 10, 12, 16, 32 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // JPEG XR: JxrEncApp 外部工具，8/16/32-bit
            _cache["jxr"] = new FormatCapabilities
            {
                Format = "jxr",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportsCicp = false,
                CicpNote = "JXR 使用 EXIF ColorSpace + ICC",
                SupportedBitDepths = new List<int> { 8, 16, 32 },
                SupportedColorSpaces = new List<string> { "BT.709", "BT.2020" }
            };

            // DNG: dngtool 外部工具 (LibRaw + DNG SDK 1.7.1)，任意 RAW/DNG → DNG
            _cache["dng"] = new FormatCapabilities
            {
                Format = "dng",
                SupportsQuality = true,   // JXL 有损质量 (需勾选 JXL Modular 选项)
                SupportsChroma = false,
                SupportsBitDepth = false, // 保留源位深
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportsCicp = false,
                CicpNote = "DNG 使用 Camera Profile + AsShot 白平衡",
                SupportedBitDepths = new List<int> { 8, 12, 14, 16 },
                SupportedColorSpaces = new List<string> { },
                SupportsGainMap = false
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
