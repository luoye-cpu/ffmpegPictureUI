using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 编码器后端类型
    /// </summary>
    public enum EncoderBackend
    {
        Ffmpeg,
        Cjpegli,   // 外部 cjpegli 工具
        Cjxl,      // 外部 cjxl 工具
        Ultrahdr,  // 外部 ultrahdr_app 工具 (Gain Map / Ultra HDR)
        Jxr        // 外部 JxrEncApp 工具 (JPEG XR)
    }

    public class EncoderInfo
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool SupportsFrameMultithreading { get; set; }
        public bool SupportsExperimental { get; set; }
        /// <summary>编码器后端类型</summary>
        public EncoderBackend Backend { get; set; } = EncoderBackend.Ffmpeg;
        /// <summary>外部工具检测到的路径（仅外部工具有效）</summary>
        public string? DetectedPath { get; set; }
        /// <summary>是否可用</summary>
        public bool IsAvailable => Backend == EncoderBackend.Ffmpeg || !string.IsNullOrWhiteSpace(DetectedPath);

        /// <summary>将显示名称中的后端类型编码，方便后续解析</summary>
        public string DisplayName => Backend switch
        {
            EncoderBackend.Cjpegli => $"🔧 cjpegli — JPEG-LI (jpegli 库)",
            EncoderBackend.Cjxl => $"🔧 cjxl — JPEG XL (参考实现)",
            EncoderBackend.Ultrahdr => $"🔧 ultrahdr — Gain Map / Ultra HDR (谷歌官方)",
            EncoderBackend.Jxr => $"🔧 JxrEncApp — JPEG XR (微软参考)",
            _ => $"{Name} — {Description}"
        };

        public override string ToString() => DisplayName;

        /// <summary>从 EncoderCombo 选中项解析后端</summary>
        public static EncoderBackend ParseBackend(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return EncoderBackend.Ffmpeg;
            if (displayName.Contains("cjpegli")) return EncoderBackend.Cjpegli;
            if (displayName.Contains("cjxl")) return EncoderBackend.Cjxl;
            if (displayName.Contains("ultrahdr")) return EncoderBackend.Ultrahdr;
            if (displayName.Contains("jxr") || displayName.Contains("JxrEnc")) return EncoderBackend.Jxr;
            return EncoderBackend.Ffmpeg;
        }

        /// <summary>从 EncoderCombo 选中项解析编码器名称（FFmpeg 编码器名或外部工具标识）</summary>
        public static string ParseEncoderName(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return "";
            if (displayName.Contains("cjpegli")) return "cjpegli";
            if (displayName.Contains("cjxl")) return "cjxl";
            if (displayName.Contains("ultrahdr")) return "ultrahdr";
            if (displayName.Contains("jxr") || displayName.Contains("JxrEnc")) return "jxr";
            // FFmpeg 编码器: "mjpeg — MJPEG..." → "mjpeg"
            var dashIdx = displayName.IndexOf(" — ");
            return dashIdx > 0 ? displayName.Substring(0, dashIdx).Trim() : displayName.Trim();
        }
    }

    public static class EncoderDetectionService
    {
        private static List<EncoderInfo>? _allEncoders;
        private static HashSet<string>? _allDecoders;
        private static HashSet<string>? _allMuxers;

        private static readonly Dictionary<string, string[]> FormatEncoderMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jpg"] = new[] { "mjpeg", "libultrahdr", "mjpeg_qsv", "mjpeg_vaapi", "mjpeg_nvenc", "mjpeg_amf" },
            ["jpeg"] = new[] { "mjpeg", "libultrahdr", "mjpeg_qsv", "mjpeg_vaapi", "mjpeg_nvenc", "mjpeg_amf" },
            ["png"] = new[] { "png", "png_vaapi" },
            ["webp"] = new[] { "libwebp", "libwebp_anim", "webp" },
            ["avif"] = new[] { "libaom-av1", "libsvtav1", "librav1e", "av1_nvenc", "av1_amf", "av1_qsv", "av1_vaapi" },
            ["tiff"] = new[] { "tiff" },
            ["jxl"] = new[] { "libjxl", "libjxl_anim", "jpegxl" },
            ["jxr"] = new[] { "jxr" },
            ["bmp"] = new[] { "bmp" },
            ["gif"] = new[] { "gif" },
            ["apng"] = new[] { "apng", "png" }
        };

        // 格式 → 必需的 muxer 名称（ffmpeg 写文件时使用）
        private static readonly Dictionary<string, string> FormatMuxerMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jpg"] = "image2",    ["jpeg"] = "image2",
            ["png"] = "apng",      ["apng"] = "apng",
            ["webp"] = "webp",     ["avif"] = "avif",
            ["tiff"] = "image2",   ["tif"] = "image2",
            ["jxl"] = "jpegxl",    ["jxr"] = "image2",
            ["bmp"] = "image2",    ["gif"] = "gif"
        };

        // 格式 → 必需的 decoder 名称（ffmpeg 读文件时使用，null=内置支持）
        private static readonly Dictionary<string, string[]> FormatDecoderMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jpg"] = new[] { "mjpeg" },       ["jpeg"] = new[] { "mjpeg" },
            ["png"] = new[] { "png" },          ["apng"] = new[] { "apng", "png" },
            ["webp"] = new[] { "webp" },        ["avif"] = new[] { "av1" },
            ["tiff"] = new[] { "tiff" },        ["tif"] = new[] { "tiff" },
            ["jxl"] = new[] { "jpegxl", "libjxl" }, ["jxr"] = new[] { "jxr" },
            ["bmp"] = new[] { "bmp" },          ["gif"] = new[] { "gif" },
            ["heic"] = new[] { "hevc" },        ["heif"] = new[] { "hevc", "heif" },
            ["dng"] = new[] { "tiff" }
        };

        /// <summary>
        /// 运行 ffmpeg -encoders 并缓存所有编码器
        /// </summary>
        public static async Task<List<EncoderInfo>> GetAllEncodersAsync(string? ffmpegPath = null)
        {
            if (_allEncoders != null && _allEncoders.Count > 0) return _allEncoders;

            var fileName = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;
            _allEncoders = new List<EncoderInfo>();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = "-hide_banner -encoders",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return _allEncoders;
                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();

                ParseEncoders(output);
            }
            catch
            {
                // ffmpeg 不可用时清空缓存，允许后续重试
                _allEncoders = null;
                return new List<EncoderInfo>();
            }

            return _allEncoders;
        }

        private static void ParseEncoders(string output)
        {
            if (_allEncoders == null) return;
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // ffmpeg -encoders 原始行格式 (7字符标志位):
                // " VF...D libwebp              libwebp WebP"
                //  ^^^^^^^
                //  0123456
                //  0:空格 1:V/A/S 2:F/帧 3:S/切片 4:X/实验 5:B 6:D/直接渲染
                if (line.Length < 9) continue;

                // 标题行以 . 或 - 开头(跳过)
                var secondChar = line.Length > 1 ? line[1] : ' ';
                if (secondChar == '.' || secondChar == '-' || secondChar == '=') continue;

                // 必须是视频或音频
                if (secondChar != 'V' && secondChar != 'A') continue;

                // 取前7个字符为标志位, 其余为 "name        description"
                var flags = line.Substring(0, Math.Min(7, line.Length));
                var remaining = line.Substring(flags.Length).TrimStart();

                var spaceIdx = remaining.IndexOf(' ');
                if (spaceIdx <= 0) continue;

                var name = remaining.Substring(0, spaceIdx).Trim();
                var desc = remaining.Substring(spaceIdx + 1).Trim();

                _allEncoders.Add(new EncoderInfo
                {
                    Name = name,
                    Description = desc,
                    SupportsFrameMultithreading = flags.Length >= 3 && flags[2] == 'F',
                    SupportsExperimental = flags.Length >= 5 && flags[4] == 'X'
                });
            }
        }

        /// <summary>
        /// 获取指定图片格式可用的编码器列表（包括 FFmpeg 编码器和外部工具）。
        /// </summary>
        public static async Task<List<EncoderInfo>> GetEncodersForFormatAsync(string format, string? ffmpegPath = null)
        {
            var result = new List<EncoderInfo>();
            var fmt = format.ToLower();

            // ── 1) FFmpeg 编码器 ──
            if (FormatEncoderMap.TryGetValue(fmt, out var candidateNames) && candidateNames.Length > 0)
            {
                var all = await GetAllEncodersAsync(ffmpegPath);
                var ffmpegEncoders = all
                    .Where(e => candidateNames.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                foreach (var enc in ffmpegEncoders)
                {
                    enc.Backend = EncoderBackend.Ffmpeg;
                }
                ffmpegEncoders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                result.AddRange(ffmpegEncoders);
            }

            // ── 2) 外部工具编码器 ──
            switch (fmt)
            {
                // JPEG 格式：cjpegli + ultrahdr_app 作为独立编码器可选
                case "jpg":
                case "jpeg":
                    if (CjpegliService.IsAvailable)
                        result.Add(new EncoderInfo
                        {
                            Name = "cjpegli",
                            Description = "JPEG-LI (jpegli 库)",
                            Backend = EncoderBackend.Cjpegli,
                            DetectedPath = CjpegliService.DetectedPath,
                            SupportsFrameMultithreading = false
                        });
                    if (UltrahdrService.IsAvailable)
                        result.Add(new EncoderInfo
                        {
                            Name = "ultrahdr",
                            Description = "Gain Map / Ultra HDR (谷歌官方)",
                            Backend = EncoderBackend.Ultrahdr,
                            DetectedPath = UltrahdrService.DetectedPath,
                            SupportsFrameMultithreading = true
                        });
                    break;

                // JXL 格式：cjxl 作为独立编码器可选
                case "jxl":
                    if (CjxlService.IsAvailable)
                        result.Add(new EncoderInfo
                        {
                            Name = "cjxl",
                            Description = "JPEG XL (参考实现)",
                            Backend = EncoderBackend.Cjxl,
                            DetectedPath = CjxlService.DetectedPath,
                            SupportsFrameMultithreading = true
                        });
                    break;

                // JXR 格式：JxrEncApp 作为独立编码器
                case "jxr":
                    if (JxrService.IsAvailable)
                        result.Add(new EncoderInfo
                        {
                            Name = "jxr",
                            Description = "JPEG XR (微软参考)",
                            Backend = EncoderBackend.Jxr,
                            DetectedPath = JxrService.DetectedPath,
                            SupportsFrameMultithreading = false
                        });
                    break;
            }

            return result;
        }

        /// <summary>
        /// 获取默认编码器名称（优先外部工具，其次 libultrahdr，最后 FFmpeg 内置）
        /// </summary>
        public static string GetDefaultEncoder(string format)
        {
            return format.ToLower() switch
            {
                "jpg" or "jpeg" => CjpegliService.IsAvailable ? "cjpegli"
                    : UltrahdrService.IsAvailable ? "ultrahdr"
                    : HasCachedLibultrahdr() ? "libultrahdr" : "mjpeg",
                "png" => "png",
                "webp" => "libwebp",
                "avif" => HasCachedSvtAv1() ? "libsvtav1" : "libaom-av1",
                "tiff" => "tiff",
                "jxl" => CjxlService.IsAvailable ? "cjxl" : "libjxl",
                "jxr" => JxrService.IsAvailable ? "jxr" : "jxr",
                "apng" => "apng",
                "gif" => "gif",
                _ => ""
            };
        }

        /// <summary>
        /// 检测 libultrahdr 是否在缓存的编码器列表中
        /// </summary>
        private static bool HasCachedLibultrahdr()
        {
            return _allEncoders?.Any(e => e.Name == "libultrahdr") == true;
        }

        /// <summary>检测 libsvtav1 是否在缓存的编码器列表中</summary>
        private static bool HasCachedSvtAv1()
        {
            return _allEncoders?.Any(e => e.Name == "libsvtav1") == true;
        }

        /// <summary>
        /// 获取指定格式可用的 Gain Map 编码器是否就绪
        /// </summary>
        public static bool IsLibultrahdrAvailable => HasCachedLibultrahdr();

        /// <summary>
        /// 检测 libjxl 是否支持 -lossless_jpeg 参数（JPEG→JXL 无损重封装）
        /// 需要 ffmpeg >= 7.0 且编译了 libjxl >= 0.10
        /// </summary>
        public static async Task<bool> SupportsJxlLosslessJpegAsync(string? ffmpegPath = null)
        {
            var fileName = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = "-hide_banner -h encoder=libjxl",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return false;
                var output = await p.StandardOutput.ReadToEndAsync();
                var error = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();

                var combined = output + error;
                return combined.Contains("-lossless_jpeg", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取所有可用的 muxer（复用器/封装器）。用于判断目标格式是否可写。
        /// </summary>
        public static async Task<HashSet<string>> GetAllMuxersAsync(string? ffmpegPath = null)
        {
            if (_allMuxers != null) return _allMuxers;
            _allMuxers = await ParseSimpleListAsync("muxers", ffmpegPath);
            return _allMuxers;
        }

        /// <summary>
        /// 获取所有可用的 decoder（解码器）。用于判断输入格式是否可读。
        /// </summary>
        public static async Task<HashSet<string>> GetAllDecodersAsync(string? ffmpegPath = null)
        {
            if (_allDecoders != null) return _allDecoders;
            _allDecoders = await ParseSimpleListAsync("decoders", ffmpegPath);
            return _allDecoders;
        }

        /// <summary>判断 ffmpeg 是否能写入指定图片格式（检查 muxer 可用性）</summary>
        public static async Task<bool> IsMuxerAvailableAsync(string format, string? ffmpegPath = null)
        {
            if (!FormatMuxerMap.TryGetValue(format.ToLower(), out var muxerName))
                return false;
            var muxers = await GetAllMuxersAsync(ffmpegPath);
            return muxers.Contains(muxerName);
        }

        /// <summary>判断 ffmpeg 是否能解码指定图片格式（检查 decoder 可用性）</summary>
        public static async Task<bool> IsDecoderAvailableAsync(string format, string? ffmpegPath = null)
        {
            if (!FormatDecoderMap.TryGetValue(format.ToLower(), out var decoderNames))
                return true; // 未在映射表中 → 假定内置支持
            var decoders = await GetAllDecodersAsync(ffmpegPath);
            return decoderNames.Any(d => decoders.Contains(d));
        }

        /// <summary>获取指定格式的人类可读能力状态描述</summary>
        public static async Task<string> GetFormatStatusAsync(string format, string? ffmpegPath = null)
        {
            var fmt = format.ToLower();
            var parts = new List<string>();

            // 编码器
            var encoders = await GetEncodersForFormatAsync(fmt, ffmpegPath);
            var bestEncoder = encoders.FirstOrDefault(e => e.IsAvailable);
            if (bestEncoder != null)
            {
                var tag = bestEncoder.Backend == EncoderBackend.Ffmpeg ? "" :
                          bestEncoder.Backend == EncoderBackend.Cjxl ? " (外部 cjxl)" :
                          bestEncoder.Backend == EncoderBackend.Cjpegli ? " (外部 cjpegli)" :
                          bestEncoder.Backend == EncoderBackend.Ultrahdr ? " (外部 ultrahdr)" :
                          bestEncoder.Backend == EncoderBackend.Jxr ? " (外部 JxrEncApp)" : "";
                parts.Add($"编码: {bestEncoder.Name}{tag}");
            }
            else
            {
                parts.Add("编码: ❌ 不可用");
            }

            // Muxer
            var muxerOk = await IsMuxerAvailableAsync(fmt, ffmpegPath);
            parts.Add(muxerOk ? "封装: ✅" : "封装: ❌ 不支持");

            // Decoder（仅特定格式需要检查）
            if (FormatDecoderMap.ContainsKey(fmt))
            {
                var decOk = await IsDecoderAvailableAsync(fmt, ffmpegPath);
                if (!decOk) parts.Add("解码: ❌ 不支持");
            }

            return $"{fmt.ToUpper()} — " + string.Join(", ", parts);
        }

        /// <summary>解析 ffmpeg 简单列表输出（-muxers / -decoders 等）为名称集合</summary>
        private static async Task<HashSet<string>> ParseSimpleListAsync(
            string listType, string? ffmpegPath = null)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileName = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = $"-hide_banner -{listType}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return result;
                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.Length < 9) continue;
                    var secondChar = line.Length > 1 ? line[1] : ' ';
                    if (secondChar == '.' || secondChar == '-' || secondChar == '=') continue;

                    var flags = line.Substring(0, Math.Min(7, line.Length));
                    var remaining = line.Substring(flags.Length).TrimStart();
                    var spaceIdx = remaining.IndexOf(' ');
                    if (spaceIdx <= 0) continue;
                    var name = remaining.Substring(0, spaceIdx).Trim();
                    if (!string.IsNullOrEmpty(name))
                        result.Add(name);
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 清除缓存（ffmpeg 路径变更后调用）
        /// </summary>
        public static void ClearCache()
        {
            _allEncoders = null;
            _allDecoders = null;
            _allMuxers = null;
        }
    }
}
