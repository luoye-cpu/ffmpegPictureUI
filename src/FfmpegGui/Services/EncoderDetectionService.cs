using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    public static class EncoderDetectionService
    {
        private static List<EncoderInfo>? _allEncoders;
        private static readonly Dictionary<string, string[]> FormatEncoderMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jpg"] = new[] { "mjpeg", "mjpeg_qsv", "mjpeg_vaapi", "mjpeg_nvenc", "mjpeg_amf" },
            ["jpeg"] = new[] { "mjpeg", "mjpeg_qsv", "mjpeg_vaapi", "mjpeg_nvenc", "mjpeg_amf" },
            ["png"] = new[] { "png", "png_vaapi" },
            ["webp"] = new[] { "libwebp", "libwebp_anim", "webp" },
            ["avif"] = new[] { "libaom-av1", "libsvtav1", "librav1e", "av1_nvenc", "av1_amf", "av1_qsv", "av1_vaapi" },
            ["tiff"] = new[] { "tiff" },
            ["jxl"] = new[] { "libjxl", "libjxl_anim", "jpegxl" },
            ["bmp"] = new[] { "bmp" },
            ["gif"] = new[] { "gif" }
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
        /// 获取指定图片格式可用的编码器列表
        /// </summary>
        public static async Task<List<EncoderInfo>> GetEncodersForFormatAsync(string format, string? ffmpegPath = null)
        {
            var all = await GetAllEncodersAsync(ffmpegPath);

            if (!FormatEncoderMap.TryGetValue(format, out var candidateNames))
                return new List<EncoderInfo>();

            // 筛选出实际可用的编码器
            var available = all
                .Where(e => candidateNames.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // 按名称排序
            available.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return available;
        }

        /// <summary>
        /// 获取默认编码器
        /// </summary>
        public static string GetDefaultEncoder(string format)
        {
            return format.ToLower() switch
            {
                "jpg" or "jpeg" => "mjpeg",
                "png" => "png",
                "webp" => "libwebp",
                "avif" => "libaom-av1",
                "tiff" => "tiff",
                "jxl" => "libjxl",
                _ => ""
            };
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
        /// 清除缓存（ffmpeg 路径变更后调用）
        /// </summary>
        public static void ClearCache()
        {
            _allEncoders = null;
        }
    }

    public class EncoderInfo
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool SupportsFrameMultithreading { get; set; }
        public bool SupportsExperimental { get; set; }
        public override string ToString() => $"{Name} — {Description}";
    }
}
