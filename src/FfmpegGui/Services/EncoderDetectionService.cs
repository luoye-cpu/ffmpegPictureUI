using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        Jxr,       // 外部 JxrEncApp 工具 (JPEG XR)
        Dng        // 外部 dngtool 工具 (DNG 输出, LibRaw + DNG SDK)
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
        /// <summary>是否可用。外部工具需有路径；FFmpeg 编码器默认可用，但硬件编码器需排除未编译/验证失败</summary>
        public bool IsAvailable
        {
            get
            {
                if (Backend != EncoderBackend.Ffmpeg)
                    return !string.IsNullOrWhiteSpace(DetectedPath);
                // FFmpeg 内置编码器：硬件编码器若未编译（NotCompiled）或运行时验证失败（Failed）则不可用
                if (IsHardwareEncoder)
                {
                    return GpuAvailability is GpuEncoderAvailability.Verified
                        or GpuEncoderAvailability.DeviceFoundUntested
                        or GpuEncoderAvailability.CompiledNoDevice
                        or GpuEncoderAvailability.Unknown;
                }
                return true;
            }
        }

        // ═══════════════════════════════════════════════
        // GPU 硬件编码器字段
        // ═══════════════════════════════════════════════

        /// <summary>是否为 GPU 硬件编码器</summary>
        public bool IsHardwareEncoder { get; set; }

        /// <summary>GPU 编码器可用性状态</summary>
        public GpuEncoderAvailability GpuAvailability { get; set; } = GpuEncoderAvailability.Unknown;

        /// <summary>GPU 编码器不可用时的提示信息</summary>
        public string? GpuWarningMessage { get; set; }

        /// <summary>GPU 编码器是否实际可用（编译 + 硬件存在 + 运行时通过）</summary>
        public bool IsGpuUsable => IsHardwareEncoder && GpuAvailability == GpuEncoderAvailability.Verified;

        /// <summary>将显示名称中的后端类型编码，方便后续解析</summary>
        public string DisplayName
        {
            get
            {
                var gpuIcon = IsHardwareEncoder
                    ? GpuAvailability switch
                    {
                        GpuEncoderAvailability.Verified => "⚡ ",
                        GpuEncoderAvailability.DeviceFoundUntested => "⚡ ",
                        GpuEncoderAvailability.CompiledNoDevice => "⚡⚠️ ",
                        GpuEncoderAvailability.Failed => "⚡❌ ",
                        _ => ""
                    }
                    : "";

                return Backend switch
                {
                    EncoderBackend.Cjpegli => $"🔧 cjpegli — JPEG-LI (jpegli 库)",
                    EncoderBackend.Cjxl => $"🔧 cjxl — JPEG XL (参考实现)",
                    EncoderBackend.Jxr => $"🔧 JxrEncApp — JPEG XR (微软参考)",
                    _ => $"{gpuIcon}{Name} — {Description}"
                };
            }
        }

        public override string ToString() => DisplayName;

        /// <summary>从 EncoderCombo 选中项解析后端</summary>
        public static EncoderBackend ParseBackend(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return EncoderBackend.Ffmpeg;
            if (displayName.Contains("cjpegli")) return EncoderBackend.Cjpegli;
            if (displayName.Contains("cjxl")) return EncoderBackend.Cjxl;
            if (displayName.Contains("jxr") || displayName.Contains("JxrEnc")) return EncoderBackend.Jxr;
            if (displayName.Contains("dngtool")) return EncoderBackend.Dng;
            return EncoderBackend.Ffmpeg;
        }

        /// <summary>从 EncoderCombo 选中项解析编码器名称（FFmpeg 编码器名或外部工具标识）</summary>
        public static string ParseEncoderName(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return "";
            if (displayName.Contains("cjpegli")) return "cjpegli";
            if (displayName.Contains("cjxl")) return "cjxl";
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
            ["jpg"] = new[] { "mjpeg", "mjpeg_qsv", "mjpeg_vaapi", "mjpeg_nvenc", "mjpeg_amf" },
            ["jpeg"] = new[] { "mjpeg", "mjpeg_qsv", "mjpeg_vaapi", "mjpeg_nvenc", "mjpeg_amf" },
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

        // 格式 → 必需的 muxer 名称列表（任一存在即可；ffmpeg 各版本 muxer 名不同）
        // ⚠️ ffmpeg 22 实测：无 jpegxl muxer，JXL 静态图用 image2 muxer + libjxl 编码器输出
        private static readonly Dictionary<string, string[]> FormatMuxerMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jpg"] = new[] { "image2" },    ["jpeg"] = new[] { "image2" },
            ["png"] = new[] { "apng", "image2" }, ["apng"] = new[] { "apng", "image2" },
            ["webp"] = new[] { "webp", "image2" }, ["avif"] = new[] { "avif", "image2" },
            ["tiff"] = new[] { "image2" },   ["tif"] = new[] { "image2" },
            ["jxl"] = new[] { "image2", "jpegxl" }, ["jxr"] = new[] { "image2" },
            ["bmp"] = new[] { "image2" },    ["gif"] = new[] { "gif" }
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

                // ── GPU 编码器状态标注 ──
                foreach (var enc in ffmpegEncoders)
                {
                    enc.Backend = EncoderBackend.Ffmpeg;
                    if (IsGpuEncoderName(enc.Name))
                    {
                        enc.IsHardwareEncoder = true;
                        var gpuStatus = GpuCapabilityService.GetEncoderStatus(enc.Name);
                        if (gpuStatus != null)
                        {
                            enc.GpuAvailability = gpuStatus.Availability;
                            enc.GpuWarningMessage = gpuStatus.WarningMessage;
                        }
                        else
                        {
                            enc.GpuAvailability = GpuEncoderAvailability.Unknown;
                        }
                    }
                }
                ffmpegEncoders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                result.AddRange(ffmpegEncoders);
            }

            // ── 2) 外部工具编码器 ──
            switch (fmt)
            {
                // JPEG 格式：cjpegli 作为独立编码器可选
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

                // DNG 格式：dngtool 作为独立编码器 (LibRaw + DNG SDK)
                case "dng":
                    if (RawService.IsDngTool)
                        result.Add(new EncoderInfo
                        {
                            Name = "dngtool",
                            Description = "DNG (LibRaw + DNG SDK 1.7, 支持 JXL 压缩)",
                            Backend = EncoderBackend.Dng,
                            DetectedPath = RawService.DetectedPath,
                            SupportsFrameMultithreading = false
                        });
                    break;
            }

            return result;
        }

        /// <summary>
        /// 获取默认编码器名称（优先外部工具，最后 FFmpeg 内置）
        /// </summary>
        public static string GetDefaultEncoder(string format)
        {
            return format.ToLower() switch
            {
                "jpg" or "jpeg" => CjpegliService.IsAvailable ? "cjpegli"
                    : HasCachedLibultrahdr() ? "libultrahdr" : "mjpeg",
                "png" => "png",
                "webp" => "libwebp",
                "avif" => HasCachedSvtAv1() ? "libsvtav1" : "libaom-av1",
                "tiff" => "tiff",
                "jxl" => CjxlService.IsAvailable ? "cjxl" : "libjxl",
                "jxr" => JxrService.IsAvailable ? "jxr" : "jxr",
                "dng" => RawService.IsDngTool ? "dngtool" : "dngtool",
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

        // ═══════════════════════════════════════════════
        // GPU 硬件编码器判断
        // ═══════════════════════════════════════════════

        /// <summary>已知 GPU 硬件编码器名称集合</summary>
        private static readonly HashSet<string> GpuEncoderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "mjpeg_qsv", "mjpeg_nvenc", "mjpeg_amf", "mjpeg_vaapi",
            "av1_qsv", "av1_nvenc", "av1_amf", "av1_vaapi",
            "h264_nvenc", "h264_qsv", "h264_amf", "h264_vaapi",
            "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_vaapi",
            "png_vaapi"
        };

        /// <summary>判断编码器名称是否为 GPU 硬件编码器</summary>
        public static bool IsGpuEncoderName(string encoderName)
        {
            return GpuEncoderNames.Contains(encoderName);
        }

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

        /// <summary>判断 ffmpeg 是否能写入指定图片格式（检查 muxer 可用性，任一候选匹配即可）</summary>
        public static async Task<bool> IsMuxerAvailableAsync(string format, string? ffmpegPath = null)
        {
            if (!FormatMuxerMap.TryGetValue(format.ToLower(), out var muxerNames))
                return false;
            var muxers = await GetAllMuxersAsync(ffmpegPath);
            return muxerNames.Any(m => muxers.Contains(m));
        }

        /// <summary>判断 ffmpeg 是否能解码指定图片格式（检查 decoder 可用性）。
        /// JXR 特殊处理：ffmpeg 无 jxr 解码器，但软件有 JxrDecApp 外部解码器（PLAN/artifacts）。</summary>
        public static async Task<bool> IsDecoderAvailableAsync(string format, string? ffmpegPath = null)
        {
            var fmt = format.ToLower();
            // JXR：外部 JxrDecApp 是实际解码路径（ProcessJxrInputAsync），ffmpeg 内置 jxr 解码器几乎不存在
            if (fmt == "jxr")
            {
                if (PlatformServices.TryFindInPlanFolder(PlatformServices.JxrDec) != null)
                    return true;
                return false;
            }
            if (!FormatDecoderMap.TryGetValue(fmt, out var decoderNames))
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
                          bestEncoder.Backend == EncoderBackend.Jxr ? " (外部 JxrEncApp)" :
                          bestEncoder.Backend == EncoderBackend.Dng ? " (外部 dngtool)" : "";
                parts.Add($"编码: {bestEncoder.Name}{tag}");
            }
            else
            {
                parts.Add("编码: ❌ 不可用");
            }

            // Muxer
            // ⚠️ 外部工具后端（dngtool/JxrEncApp/cjxl/cjpegli）不经过 ffmpeg muxer，
            // 由外部工具直接写容器。ffmpeg 的 muxer 检查对它们无意义：
            //   - DNG：dngtool -e 直接写 DNG（ffmpeg 无 dng muxer，但软件可正常输出）
            //   - JXR：JxrEncApp 直接写 JXR（ffmpeg 的 image2 muxer 实际不参与）
            // 因此仅对 FFmpeg 内置编码器检查 muxer，外部工具显示"封装: 外部工具"。
            if (bestEncoder != null && bestEncoder.Backend == EncoderBackend.Ffmpeg)
            {
                var muxerOk = await IsMuxerAvailableAsync(fmt, ffmpegPath);
                parts.Add(muxerOk ? "封装: ✅" : "封装: ❌ 不支持");
            }
            else if (bestEncoder != null)
            {
                parts.Add("封装: 外部工具");
            }
            else
            {
                parts.Add("封装: ❌ 不支持");
            }

            // Decoder（仅特定格式需要检查）
            if (FormatDecoderMap.ContainsKey(fmt))
            {
                var decOk = await IsDecoderAvailableAsync(fmt, ffmpegPath);
                if (!decOk) parts.Add("解码: ❌ 不支持");
            }

            return $"{fmt.ToUpper()} — " + string.Join(", ", parts);
        }

        /// <summary>解析 ffmpeg 简单列表输出（-muxers / -decoders 等）为名称集合</summary>
        /// <remarks>
        /// ⚠️ ffmpeg 各版本输出格式不同（2026-08 实测）：
        ///   ffmpeg 4/5/6  -muxers: " E..... webp   WebP file"（1 空格 + 6 标志 + 空格）
        ///   ffmpeg 22     -muxers: "  E  webp          WebP"（2 空格 + E + 2 空格）—— 前缀仅 5 字符！
        ///   -encoders/-decoders 均为 7 字符前缀（" V....D name"）
        /// 因此不能硬编码前缀长度，改为正则提取每行第二个 token（名称）。
        /// </remarks>
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
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("---", StringComparison.Ordinal)) continue;

                    // 数据行格式：<标志位> <名称> <描述>。名称 = 第二个 token。
                    // 标题/说明行（"Formats:"、" D.. = Demuxing supported"）不会匹配或
                    // 第二个 token 以 '=' 开头被过滤。
                    var m = Regex.Match(line, @"^\s*\S+\s+(\S+)");
                    if (!m.Success) continue;
                    var name = m.Groups[1].Value;
                    if (name.Length == 0) continue;
                    // 过滤说明行（"D.. = ..." 的第二个 token 是 '='）与非法名称
                    if (!char.IsLetterOrDigit(name[0])) continue;
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
