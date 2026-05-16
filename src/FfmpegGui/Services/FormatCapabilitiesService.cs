using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
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
            // 2) 远端查询（模拟/实际）
            await TryFetchRemoteCapabilitiesAsync();
            // 3) 检测本地 ffmpeg 支持的编解码器/像素格式
            await DetectLocalFfmpegCapabilitiesAsync(ffmpegPath);
        }

        private static void SeedLocalRules()
        {
            _cache["jpg"] = new FormatCapabilities
            {
                Format = "jpg",
                SupportsQuality = true,
                SupportsChroma = true,
                SupportsBitDepth = false,
                SupportsMetadata = true,
                SupportsLossless = false,
                SupportedBitDepths = new List<int> { 8 },
                SupportedColorSpaces = new List<string> { "BT.601", "BT.709" }
            };

            _cache["png"] = new FormatCapabilities
            {
                Format = "png",
                SupportsQuality = false,
                SupportsChroma = false,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportedBitDepths = new List<int> { 8, 16 },
                SupportedColorSpaces = new List<string> { "BT.709" }
            };

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

            _cache["tiff"] = new FormatCapabilities
            {
                Format = "tiff",
                SupportsQuality = false,
                SupportsChroma = false,
                SupportsBitDepth = true,
                SupportsMetadata = true,
                SupportsLossless = true,
                SupportedBitDepths = new List<int> { 8, 10, 12, 16 },
                SupportedColorSpaces = new List<string> { "BT.709" }
            };

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
        }

        private static async Task TryFetchRemoteCapabilitiesAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                // 示例：github gist/raw/json 或自建 hosted json
                var url = "https://raw.githubusercontent.com/example/ffmpeg-format-capabilities/main/capabilities.json";
                var txt = await http.GetStringAsync(url);
                var doc = JsonSerializer.Deserialize<Dictionary<string, FormatCapabilities>>(txt);
                if (doc != null)
                {
                    foreach (var kv in doc)
                    {
                        _cache[kv.Key.ToLower()] = kv.Value;
                    }
                }
            }
            catch { /* 忽略远端失败 */ }
        }

        private static async Task DetectLocalFfmpegCapabilitiesAsync(string? ffmpegPath = null)
        {
            try
            {
                var fileName = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;
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
