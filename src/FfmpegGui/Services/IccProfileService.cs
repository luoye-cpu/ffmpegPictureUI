using System;
using System.IO;

namespace FfmpegGui.Services
{
    /// <summary>
    /// ICC 配置文件解析与验证服务。
    /// 支持读取 ICC 文件头、设备类别、色彩空间、描述标签，
    /// 并推断常见标准色彩空间名称。
    /// </summary>
    public static class IccProfileService
    {
        /// <summary>
    /// 用 exiftool 从图片提取内嵌 ICC 配置文件到临时文件。
    /// 返回 (临时文件路径, ICC 描述)；无 ICC / exiftool 不可用 / 失败时返回 (null, null)。
    /// 调用方负责删除返回的临时文件。
    /// 带 5 秒超时保护：exiftool 挂起时杀死进程，防止阻塞编码队列。
    /// </summary>
    public static (string? path, string? description) ExtractIccToTempFile(string imagePath)
    {
        try
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return (null, null);
            if (!ExifToolService.IsAvailable)
                return (null, null);

            var tmp = Path.Combine(PlatformServices.GetTempDir(), $"icc_extract_{Guid.NewGuid():N}.icc");
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ExifToolService.DetectedPath!,
                Arguments = $"-b -icc_profile \"{imagePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p == null) return (null, null);

            // RedirectStandardOutput 下 StandardOutput.BaseStream 是原始二进制流，可安全 CopyTo。
            // 注意：读取放后台任务，主线程 WaitForExit(5s) 超时 → Kill（避免进程挂起时 CopyTo 无限阻塞）。
            using var ms = new MemoryStream();
            var readTask = Task.Run(() => p.StandardOutput.BaseStream.CopyTo(ms));
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                TryDelete(tmp);
                return (null, null);
            }
            try { readTask.Wait(1000); } catch { }  // 进程已退出，读取应立即完成

            if (ms.Length < 128) { TryDelete(tmp); return (null, null); }
            File.WriteAllBytes(tmp, ms.ToArray());
            if (!IsValidIccProfile(tmp)) { TryDelete(tmp); return (null, null); }

            var info = ParseInfo(tmp);
            return (tmp, info?.Description);
        }
        catch { return (null, null); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>公开删除临时 ICC 文件（供探测等外部调用方清理）</summary>
    public static void TryDeleteIcc(string path)
        => TryDelete(path);

    /// <summary>验证文件是否为合法 ICC v2/v4 配置文件</summary>
        public static bool IsValidIccProfile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var bytes = File.ReadAllBytes(filePath);
                if (bytes.Length < 128) return false;

                // ICC 文件魔数: bytes 36-39 = "acsp"
                var magic = System.Text.Encoding.ASCII.GetString(bytes, 36, 4);
                return magic == "acsp";
            }
            catch { return false; }
        }

        /// <summary>解析 ICC 文件的基本头信息和描述标签</summary>
        public static IccProfileInfo? ParseInfo(string filePath)
        {
            try
            {
                var bytes = File.ReadAllBytes(filePath);
                if (bytes.Length < 132) return null;

                var info = new IccProfileInfo
                {
                    FileSize = bytes.Length,
                    DeviceClass = ReadAscii(bytes, 12, 4).Trim('\0').Trim(),
                    ColorSpace = ReadAscii(bytes, 16, 4).Trim('\0').Trim(),
                    Pcs = ReadAscii(bytes, 20, 4).Trim('\0').Trim(),
                };

                // 版本号 (bytes 8-11): BCD 编码，如 0x04 0x20 0x00 0x00 → "4.2.0"
                info.Version = $"{bytes[8]}.{bytes[9]}.{bytes[10]}";

                // 解析标签表: offset 128-131 = tag count (uint32 big-endian)
                var tagCount = ReadBigEndianU32(bytes, 128);
                for (int i = 0; i < tagCount && (132 + i * 12 + 11) < bytes.Length; i++)
                {
                    var tagBase = 132 + i * 12;
                    var sig = ReadAscii(bytes, tagBase, 4);
                    if (sig == "desc")
                    {
                        var tagOff = (int)ReadBigEndianU32(bytes, tagBase + 4);
                        var tagSize = (int)ReadBigEndianU32(bytes, tagBase + 8);
                        if (tagOff > 0 && tagOff + 20 <= bytes.Length && tagSize > 12)
                        {
                            // desc 标签结构: type 'desc' (4) + reserved (4) + asciiLen (4)
                            var asciiLen = (int)ReadBigEndianU32(bytes, tagOff + 8);
                            if (asciiLen > 0 && tagOff + 12 + asciiLen <= bytes.Length)
                            {
                                info.Description = System.Text.Encoding.ASCII
                                    .GetString(bytes, tagOff + 12, asciiLen - 1)
                                    .Trim('\0').Trim();
                            }
                        }
                        break; // 找到第一个 desc 标签即停止
                    }
                }

                return info;
            }
            catch { return null; }
        }

        /// <summary>从 ICC 描述字符串推断常见色彩空间名称（用于 UI 自动填充源色彩空间）</summary>
        public static string? GuessColorSpace(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return null;
            var d = description.ToLowerInvariant();

            if (d.Contains("srgb") || d.Contains("iec61966")) return "sRGB";
            if (d.Contains("adobergb") || d.Contains("adobe rgb")) return "Adobe RGB";
            if (d.Contains("display p3") || d.Contains("displayp3")) return "Display P3";
            if (d.Contains("dci-p3") || d.Contains("dci p3")) return "DCI-P3";
            if (d.Contains("prophoto") || d.Contains("romm")) return "ProPhoto RGB";
            if (d.Contains("rec.2020") || d.Contains("bt.2020") || d.Contains("rec2020")) return "Rec.2020";
            if (d.Contains("rec.2100") || d.Contains("bt.2100")) return "Rec.2100";
            if (d.Contains("colormatch")) return "ColorMatch RGB";

            return null;
        }

        // ── 辅助方法 ──

        private static string ReadAscii(byte[] bytes, int offset, int length)
        {
            return System.Text.Encoding.ASCII.GetString(bytes, offset, length);
        }

        private static uint ReadBigEndianU32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24)
                 | ((uint)bytes[offset + 1] << 16)
                 | ((uint)bytes[offset + 2] << 8)
                 | bytes[offset + 3];
        }
    }

    /// <summary>ICC 配置文件解析结果</summary>
    public class IccProfileInfo
    {
        /// <summary>文件字节数</summary>
        public int FileSize { get; set; }
        /// <summary>设备类别: mntr(显示器)/scnr(扫描仪)/prtr(打印机)/spac(色彩空间)</summary>
        public string DeviceClass { get; set; } = "";
        /// <summary>数据色彩空间: RGB/CMYK/GRAY/Lab/XYZ</summary>
        public string ColorSpace { get; set; } = "";
        /// <summary>Profile Connection Space: XYZ 或 Lab</summary>
        public string Pcs { get; set; } = "";
        /// <summary>ICC 规范版本号</summary>
        public string Version { get; set; } = "";
        /// <summary>ASCII 描述文本</summary>
        public string? Description { get; set; }

        /// <summary>是否为显示器 ICC（用于软校样）</summary>
        public bool IsMonitorProfile => DeviceClass == "mntr";

        public override string ToString()
        {
            var desc = Description != null ? $" \"{Description}\"" : "";
            return $"ICC {Version} | {DeviceClass}/{ColorSpace}/{Pcs}{desc} | {FileSize} bytes";
        }
    }
}
