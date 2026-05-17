using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
        private const int TimeoutMs = 30_000;

        /// <summary>
        /// 对原始图片和编码后图片进行 SSIM + PSNR 质量分析
        /// </summary>
        public static async Task<QualityResult> AnalyzeAsync(
            string sourcePath, string encodedPath, string? ffmpegPath = null)
        {
            var result = new QualityResult();
            var fileName = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;

            // ---- 前置校验 ----
            if (!File.Exists(sourcePath))
            {
                result.Error = $"源文件不存在: {sourcePath}";
                return result;
            }
            if (!File.Exists(encodedPath))
            {
                result.Error = $"输出文件不存在: {encodedPath}";
                return result;
            }

            // 分辨率一致性预检
            var resCheck = await CheckResolutionMatchAsync(sourcePath, encodedPath, fileName);
            if (!string.IsNullOrEmpty(resCheck))
            {
                result.Error = resCheck;
                return result;
            }

            try
            {
                // 使用 filter_complex + 显式输出 pad 标签，确保两个滤镜链各自独立输出
                // 旧版 -lavfi 在某些 ffmpeg 构建中对 ; 分隔的多链支持不稳定
                var args = $"-hide_banner -i \"{sourcePath}\" -i \"{encodedPath}\" " +
                           $"-filter_complex \"[0:v][1:v]ssim[ssim_out];[0:v][1:v]psnr[psnr_out]\" " +
                           $"-map \"[ssim_out]\" -map \"[psnr_out]\" -f null -";

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

                // 并发读取 stdout 和 stderr，避免管道缓冲区满导致死锁
                var stderrTask = p.StandardError.ReadToEndAsync();
                var stdoutTask = p.StandardOutput.ReadToEndAsync();

                // 超时控制：30 秒内未完成则终止 ffmpeg
                using var cts = new CancellationTokenSource(TimeoutMs);
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 超时则强制结束进程
                    try { p.Kill(entireProcessTree: true); } catch { }
                    result.Error = $"质量分析超时（超过 {TimeoutMs / 1000} 秒），ffmpeg 可能无法处理该文件对";
                    return result;
                }

                var stderr = await stderrTask;
                var stdout = await stdoutTask;

                // 合并 stdout+stderr 以便全面解析
                result.RawOutput = stderr + stdout;
                result.Success = p.ExitCode == 0;

                // [关键修复] 解析合并后的完整输出，而非仅 stderr
                // 部分 ffmpeg 构建将 SSIM/PSNR 结果输出到 stdout
                ParseResult(result.RawOutput, result);

                if (!result.SsimAll.HasValue && !result.PsnrAverage.HasValue)
                {
                    // 提取最后 500 个字符作为错误信息
                    var tail = result.RawOutput.Length > 500
                        ? result.RawOutput.Substring(result.RawOutput.Length - 500)
                        : result.RawOutput;
                    result.Error = string.IsNullOrWhiteSpace(tail)
                        ? $"ffmpeg 无输出 (退出码 {p.ExitCode})"
                        : tail.Trim();
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// 通过 ffprobe 快速检查两图分辨率是否一致。
        /// 返回 null 表示一致或无法检测（不阻塞后续分析），返回非空字符串则是错误信息。
        /// </summary>
        private static async Task<string?> CheckResolutionMatchAsync(
            string sourcePath, string encodedPath, string ffmpegPath)
        {
            try
            {
                var srcRes = await GetResolutionAsync(sourcePath, ffmpegPath);
                var encRes = await GetResolutionAsync(encodedPath, ffmpegPath);

                if (srcRes == null || encRes == null)
                    return null; // 无法获取分辨率，跳过检查

                if (srcRes.Value.Width != encRes.Value.Width || srcRes.Value.Height != encRes.Value.Height)
                    return $"源图 ({srcRes.Value.Width}x{srcRes.Value.Height}) 与输出图 ({encRes.Value.Width}x{encRes.Value.Height}) " +
                           $"分辨率不一致，SSIM/PSNR 要求两图尺寸完全相同";

                return null; // 一致
            }
            catch
            {
                return null; // 预检失败不影响主流程
            }
        }

        private static async Task<(int Width, int Height)?> GetResolutionAsync(
            string filePath, string ffmpegPath)
        {
            try
            {
                var probePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe");
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    probePath += ".exe";
                if (!File.Exists(probePath))
                    probePath = ffmpegPath.Replace("ffmpeg", "ffprobe");
                if (!File.Exists(probePath))
                    return null;

                var psi = new ProcessStartInfo
                {
                    FileName = probePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return null;

                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();

                // 输出格式: "1920,1080"
                var parts = output.Trim().Split(',');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], out var w) &&
                    int.TryParse(parts[1], out var h))
                {
                    return (w, h);
                }
            }
            catch { }
            return null;
        }

        // 匹配数值或 "inf"（无损编码时 PSNR/SSIM dB 为无穷大）
        private const string ValuePattern = @"(?:[-.\deE+]+|inf)";

        private static void ParseResult(string output, QualityResult result)
        {
            // ---- SSIM 解析 ----
            // 覆盖多种 ffmpeg 版本输出格式:
            // "SSIM Y:0.987654 U:0.976543 V:0.965432 All:0.978543 (17.68)"
            // "SSIM All:0.978543 (16.28dB)"
            // "SSIM All:0.978543"
            // "SSIM R:0.987654 G:0.976543 B:0.965432 All:0.978543 (17.68)"  -- RGB 源
            // "SSIM All:1.000000 (inf)"  -- 无损/完全相同
            var ssimMatch = Regex.Match(output,
                @"SSIM\s+(?:[YUVRGBrgbyuv]:" + ValuePattern + @"\s*)*?All:\s*(" + ValuePattern + @")(?:\s*\((" + ValuePattern + @")\s*(?:dB)?\s*\))?",
                RegexOptions.IgnoreCase);

            // 回退: 更宽松的模式
            if (!ssimMatch.Success)
            {
                ssimMatch = Regex.Match(output,
                    @"SSIM.*?All:\s*(" + ValuePattern + @")",
                    RegexOptions.IgnoreCase);
            }

            if (ssimMatch.Success)
            {
                ParseDouble(ssimMatch.Groups[1].Value, v => result.SsimAll = v);
                if (ssimMatch.Groups[2].Success)
                    ParseDouble(ssimMatch.Groups[2].Value, v => result.SsimDB = v);
            }

            // ---- PSNR 解析 ----
            // 覆盖多种输出格式:
            // "PSNR y:42.36 u:45.21 v:44.87 average:43.15 min:41.23 max:45.89"
            // "PSNR average:43.15 min:41.23 max:45.89"
            // "PSNR r:42.36 g:45.21 b:44.87 average:43.15"
            // "PSNR y:inf u:inf v:inf average:inf min:inf max:inf"  -- 无损/完全相同
            var psnrMatch = Regex.Match(output,
                @"PSNR\s+(?:[yuvrgbYUVRGB]:" + ValuePattern + @"\s*)*?average:\s*(" + ValuePattern + @")(?:\s*min:\s*(" + ValuePattern + @"))?(?:\s*max:\s*(" + ValuePattern + @"))?",
                RegexOptions.IgnoreCase);

            // 回退: 更宽松的模式
            if (!psnrMatch.Success)
            {
                psnrMatch = Regex.Match(output,
                    @"PSNR.*?average:\s*(" + ValuePattern + @")",
                    RegexOptions.IgnoreCase);
            }

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
            // "inf" 表示无损/完全相同 → PositiveInfinity
            if (string.Equals(s, "inf", StringComparison.OrdinalIgnoreCase))
            {
                setter(double.PositiveInfinity);
                return;
            }

            if (double.TryParse(s,
                    NumberStyles.Any | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out var val))
                setter(val);
        }
    }
}
