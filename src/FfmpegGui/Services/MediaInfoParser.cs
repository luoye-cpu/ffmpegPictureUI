using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 媒体信息结构化模型（用于详情窗口技术信息页）
    /// </summary>
    public class MediaInfoModel
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Format { get; set; } = "";
        public long FileSize { get; set; }
        public DateTime LastModified { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }
        public string PixelFormat { get; set; } = "";
        public int? BitDepth { get; set; }
        public string ColorPrimaries { get; set; } = "";
        public string ColorTransfer { get; set; } = "";
        public string ColorSpace { get; set; } = "";
        public string ColorRange { get; set; } = "";
        public string CodecName { get; set; } = "";

        public string? IccDescription { get; set; }
        public int? IccSize { get; set; }

        public int? Quality { get; set; }
        public string? Chroma { get; set; }
        public string? Encoder { get; set; }
        public string? EncoderBackend { get; set; }
        public bool IsLossless { get; set; }

        public double? Ssim { get; set; }
        public double? Psnr { get; set; }
    }

    public static class MediaInfoParser
    {
        /// <summary>
        /// 从 ffprobe JSON 和文件系统信息构建结构化媒体信息模型
        /// </summary>
        public static async System.Threading.Tasks.Task<MediaInfoModel> ParseAsync(
            string filePath, Models.QueueItem? queueItem = null)
        {
            var model = new MediaInfoModel();

            // ── 文件系统信息 ──
            try
            {
                var fi = new FileInfo(filePath);
                model.FileName = fi.Name;
                model.FullPath = fi.FullName;
                model.FileSize = fi.Length;
                model.LastModified = fi.LastWriteTime;
            }
            catch { }

            // ── ffprobe JSON 信息 ──
            try
            {
                var json = await MediaInfoService.GetMediaInfoAsync(filePath);
                if (!string.IsNullOrWhiteSpace(json) && json.TrimStart().StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // format
                    if (root.TryGetProperty("format", out var fmt))
                    {
                        if (fmt.TryGetProperty("format_name", out var fn))
                            model.Format = fn.GetString() ?? "";
                    }

                    // streams
                    if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var stream in streams.EnumerateArray())
                        {
                            if (stream.TryGetProperty("codec_type", out var ct) && ct.GetString() == "video")
                            {
                                if (stream.TryGetProperty("width", out var w)) model.Width = w.GetInt32();
                                if (stream.TryGetProperty("height", out var h)) model.Height = h.GetInt32();
                                if (stream.TryGetProperty("pix_fmt", out var pf)) model.PixelFormat = pf.GetString() ?? "";
                                if (stream.TryGetProperty("bits_per_raw_sample", out var bd)) model.BitDepth = bd.GetInt32();
                                if (stream.TryGetProperty("color_primaries", out var cp)) model.ColorPrimaries = cp.GetString() ?? "";
                                if (stream.TryGetProperty("color_transfer", out var ct2)) model.ColorTransfer = ct2.GetString() ?? "";
                                if (stream.TryGetProperty("color_space", out var cs)) model.ColorSpace = cs.GetString() ?? "";
                                if (stream.TryGetProperty("color_range", out var cr)) model.ColorRange = cr.GetString() ?? "";
                                if (stream.TryGetProperty("codec_name", out var cn)) model.CodecName = cn.GetString() ?? "";
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // ── ICC 信息 ──
            try
            {
                var tag = await ExifToolService.GetTagAsync(filePath, "ProfileDescription");
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    model.IccDescription = tag;
                    // 尝试获取 ICC 大小
                    var sizeTag = await ExifToolService.GetTagAsync(filePath, "ProfileSize");
                    if (int.TryParse(sizeTag, out var sz)) model.IccSize = sz;
                }
            }
            catch { }

            // ── 编码参数（来自队列项）──
            if (queueItem != null)
            {
                var opts = queueItem.Options;
                model.Quality = opts.Quality;
                model.Chroma = opts.Chroma;
                model.Encoder = opts.Encoder;
                model.EncoderBackend = opts.EncoderBackend.ToString();
                model.IsLossless = opts.Lossless;
            }

            // ── 质量分析 ──
            if (queueItem != null)
            {
                try
                {
                    var log = queueItem.Log;
                    // 解析 SSIM
                    var ssimMatch = System.Text.RegularExpressions.Regex.Match(
                        log, @"SSIM.*?All:(\d+\.\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (ssimMatch.Success && double.TryParse(ssimMatch.Groups[1].Value, out var ssim))
                        model.Ssim = ssim;

                    // 解析 PSNR
                    var psnrMatch = System.Text.RegularExpressions.Regex.Match(
                        log, @"PSNR.*?average:(\d+\.\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (psnrMatch.Success && double.TryParse(psnrMatch.Groups[1].Value, out var psnr))
                        model.Psnr = psnr;
                }
                catch { }
            }

            return model;
        }

        /// <summary>格式化文件大小</summary>
        public static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };

        /// <summary>格式化色彩空间描述</summary>
        public static string FormatColorInfo(MediaInfoModel m)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(m.ColorPrimaries) && m.ColorPrimaries != "unknown")
                parts.Add($"primaries={m.ColorPrimaries}");
            if (!string.IsNullOrWhiteSpace(m.ColorTransfer) && m.ColorTransfer != "unknown")
                parts.Add($"transfer={m.ColorTransfer}");
            if (!string.IsNullOrWhiteSpace(m.ColorSpace) && m.ColorSpace != "unknown" && m.ColorSpace != "gbr")
                parts.Add($"matrix={m.ColorSpace}");
            if (!string.IsNullOrWhiteSpace(m.ColorRange))
                parts.Add($"range={m.ColorRange}");
            return parts.Count > 0 ? string.Join(" / ", parts) : "（未标记）";
        }
    }
}
