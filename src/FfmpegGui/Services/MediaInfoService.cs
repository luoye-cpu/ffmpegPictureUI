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