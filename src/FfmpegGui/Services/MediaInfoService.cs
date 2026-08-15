using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    public static class MediaInfoService
    {
        public static async Task<string> GetMediaInfoAsync(string path, string? ffprobePath = null, string? ffmpegPath = null)
        {
            // ── RAW 文件: ffprobe 无法解析 (CR2/NEF/JXL-DNG 等), 用 dngtool -info ──
            // 2026-08-14 UI 审查修复: RAW 输入显示结构化 JSON 而非 ffprobe 错误
            if (RawService.IsRawFile(path) && RawService.IsAvailable)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = RawService.DetectedPath!,
                        Arguments = $"-info \"{path}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        var output = await p.StandardOutput.ReadToEndAsync();
                        await p.WaitForExitAsync();
                        if (!string.IsNullOrWhiteSpace(output)) return output.Trim();
                    }
                }
                catch { }
            }

            var probeName = ffprobePath ?? AppSettingsService.Current.FfprobePath;
            var ffmpegName = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;

            // 优先尝试 ffprobe（返回 JSON），否则回退到 ffmpeg -i stderr 输出
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = probeName,
                    Arguments = $"-v quiet -print_format json -show_format -show_streams \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return "无法启动 ffprobe。";
                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (!string.IsNullOrWhiteSpace(output)) return output;
            }
            catch { }

            try
            {
                var psi2 = new ProcessStartInfo
                {
                    FileName = ffmpegName,
                    Arguments = $"-hide_banner -i \"{path}\"",
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p2 = Process.Start(psi2);
                if (p2 == null) return "无法启动 ffmpeg。";
                var err = await p2.StandardError.ReadToEndAsync();
                await p2.WaitForExitAsync();
                return err;
            }
            catch (Exception ex)
            {
                return "无法执行 ffprobe 或 ffmpeg（请确保已安装并在 PATH 中）：" + ex.Message;
            }
        }
    }
}