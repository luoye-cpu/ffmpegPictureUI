using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    public class QualityResult
    {
        public double? SsimAll { get; set; }
        public double? SsimDB { get; set; }
        public double? PsnrAverage { get; set; }
        public double? PsnrMin { get; set; }
        public double? PsnrMax { get; set; }
        public string RawOutput { get; set; } = "";
        public bool Success { get; set; }
        public string Error { get; set; } = "";
    }

    public static class QualityAnalysisService
    {
        /// <summary>
        /// 对原始图片和编码后图片进行 SSIM + PSNR 质量分析
        /// </summary>
        public static async Task<QualityResult> AnalyzeAsync(
            string sourcePath, string encodedPath, string? ffmpegPath = null)
        {
            var result = new QualityResult();
            var fileName = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;

            try
            {
                // 使用显式输入 pad 标签确保两个滤镜独立运行
                // -lavfi "ssim;psnr" 中 ; 会串联，ssim 输出单流 → psnr 只有单输入报错
                var args = $"-hide_banner -i \"{sourcePath}\" -i \"{encodedPath}\" " +
                           $"-lavfi \"[0:v][1:v]ssim;[0:v][1:v]psnr\" -f null -";

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null)
                {
                    result.Error = "无法启动 ffmpeg";
                    return result;
                }

                var stderr = await p.StandardError.ReadToEndAsync();
                var stdout = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();

                // 合并 stdout+stderr 以便全面解析
                result.RawOutput = stderr + stdout;
                result.Success = p.ExitCode == 0;

                // 即使退出码非零也尝试解析（可能仍有有效数据）
                ParseResult(stderr, result);
                if (!result.SsimAll.HasValue && !result.PsnrAverage.HasValue)
                {
                    // 提取最后 500 个字符作为错误信息
                    var tail = stderr.Length > 500 ? stderr.Substring(stderr.Length - 500) : stderr;
                    result.Error = string.IsNullOrWhiteSpace(tail) ? "ffmpeg 无输出" : tail.Trim();
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        private static void ParseResult(string output, QualityResult result)
        {
            // SSIM 格式多样:
            // "SSIM Y:0.987654 U:0.976543 V:0.965432 All:0.978543 (17.68)"
            // 或 "SSIM All:0.978543 (16.28dB)"
            // 或 "SSIM All:0.978543"
            var ssimMatch = Regex.Match(output, 
                @"SSIM\s+(?:[YUV]:[-.\de]+\s*)*?All:\s*([-.\d]+)(?:\s*\(([-.\d]+)\s*(?:dB)?\s*\))?", 
                RegexOptions.IgnoreCase);
            if (ssimMatch.Success)
            {
                ParseDouble(ssimMatch.Groups[1].Value, v => result.SsimAll = v);
                if (ssimMatch.Groups[2].Success)
                    ParseDouble(ssimMatch.Groups[2].Value, v => result.SsimDB = v);
            }

            // PSNR 格式多样:
            // "PSNR y:42.36 u:45.21 v:44.87 average:43.15 min:41.23 max:45.89"
            // 或 "PSNR average:43.15"
            var psnrMatch = Regex.Match(output,
                @"PSNR\s+(?:[yuv]:[-.\de]+\s*)*?average:\s*([-.\d]+)(?:\s*min:\s*([-.\d]+))?(?:\s*max:\s*([-.\d]+))?",
                RegexOptions.IgnoreCase);
            if (psnrMatch.Success)
            {
                ParseDouble(psnrMatch.Groups[1].Value, v => result.PsnrAverage = v);
                if (psnrMatch.Groups[2].Success)
                    ParseDouble(psnrMatch.Groups[2].Value, v => result.PsnrMin = v);
                if (psnrMatch.Groups[3].Success)
                    ParseDouble(psnrMatch.Groups[3].Value, v => result.PsnrMax = v);
            }
        }

        private static void ParseDouble(string s, Action<double> setter)
        {
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                setter(val);
        }
    }
}
