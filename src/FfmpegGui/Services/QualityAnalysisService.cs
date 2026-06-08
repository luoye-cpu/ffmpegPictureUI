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
                // 选择输出文件中最佳的视轨（多轨 AVIF 等需选动画轨而非封面轨）
                var srcStream = "0:v";
                var encStream = await SelectBestVideoStreamAsync(encodedPath, fileName);

                // ★ 动图修复:
                // 1) settb=1/1000 + setpts=N 按帧序号对齐（忽略原始帧间隔），消除不同
                //    帧率/时间基准导致的帧错位对比——这才是 PSNR 极低的根本原因
                // 2) split → 各自独立的帧拷贝馈入 ssim / psnr，避免共用 pad
                //    时第二个滤镜读到错误帧（PSNR 全 inf 问题）
                var args = $"-hide_banner -hwaccel cuda -i \"{sourcePath}\" -hwaccel cuda -i \"{encodedPath}\" " +
                           $"-filter_complex \"[{srcStream}]settb=1/1000,setpts=N,split[src1][src2];[{encStream}]settb=1/1000,setpts=N,split[enc1][enc2];[src1][enc1]ssim[ssim_out];[src2][enc2]psnr[psnr_out]\" " +
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
        /// 为输出文件选择最佳视频流（多轨 AVIF 等需要跳过静态封面轨，选动画轨）
        /// 返回流选择器如 "1:v" 或 "1:v:2"
        /// </summary>
        private static async Task<string> SelectBestVideoStreamAsync(
            string encodedPath, string ffmpegPath)
        {
            try
            {
                var probePath = FindFfprobe(ffmpegPath);
                if (probePath == null) return "1:v";

                var psi = new ProcessStartInfo
                {
                    FileName = probePath,
                    Arguments = $"-v error -show_entries stream=index,codec_type,nb_frames -of csv=p=0 \"{encodedPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return "1:v";

                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();

                // 解析: index,codec_type,nb_frames
                // 例: 0,video,1  /  1,video,1  /  2,video,10  /  3,video,10
                int bestStreamIdx = -1;
                int bestFrames = -1;
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 3
                        && parts[1].Trim().Equals("video", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(parts[0].Trim(), out var idx)
                        && int.TryParse(parts[2].Trim(), out var frames))
                    {
                        if (frames > bestFrames)
                        {
                            bestFrames = frames;
                            bestStreamIdx = idx;
                        }
                    }
                }

                if (bestStreamIdx >= 0)
                    return bestStreamIdx == 0 ? "1:v" : $"1:v:{bestStreamIdx}";

                return "1:v";
            }
            catch
            {
                return "1:v"; // 探测失败则回退默认
            }
        }

        private static string? FindFfprobe(string ffmpegPath)
        {
            var dir = Path.GetDirectoryName(ffmpegPath) ?? "";
            var probePath = Path.Combine(dir, "ffprobe");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                probePath += ".exe";
            if (File.Exists(probePath))
                return probePath;
            probePath = ffmpegPath.Replace("ffmpeg", "ffprobe");
            if (File.Exists(probePath))
                return probePath;
            return null;
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
                var probePath = FindFfprobe(ffmpegPath);
                if (probePath == null) return null;

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
            //
            // ★ 动图修复: 动图（GIF/APNG/动画WebP 等）的 ssim/psnr 滤镜会为每一帧输出一行，
            //    最后一行才是所有帧的汇总平均值。这里使用 Matches 取最后一个匹配，
            //    避免误取第一帧的偏低分数。
            var ssimMatches = Regex.Matches(output,
                @"SSIM\s+(?:[YUVRGBrgbyuv]:" + ValuePattern + @"\s*)*?All:\s*(" + ValuePattern + @")(?:\s*\((" + ValuePattern + @")\s*(?:dB)?\s*\))?",
                RegexOptions.IgnoreCase);

            // 回退: 更宽松的模式
            if (ssimMatches.Count == 0)
            {
                ssimMatches = Regex.Matches(output,
                    @"SSIM.*?All:\s*(" + ValuePattern + @")",
                    RegexOptions.IgnoreCase);
            }

            // 取最后一个匹配（动图时为汇总平均值，静图为唯一匹配）
            var ssimMatch = ssimMatches.Count > 0 ? ssimMatches[^1] : null;
            if (ssimMatch != null && ssimMatch.Success)
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
            var psnrMatches = Regex.Matches(output,
                @"PSNR\s+(?:[yuvrgbYUVRGB]:" + ValuePattern + @"\s*)*?average:\s*(" + ValuePattern + @")(?:\s*min:\s*(" + ValuePattern + @"))?(?:\s*max:\s*(" + ValuePattern + @"))?",
                RegexOptions.IgnoreCase);

            // 回退: 更宽松的模式
            if (psnrMatches.Count == 0)
            {
                psnrMatches = Regex.Matches(output,
                    @"PSNR.*?average:\s*(" + ValuePattern + @")",
                    RegexOptions.IgnoreCase);
            }

            // 取最后一个匹配（动图时为汇总平均值，静图为唯一匹配）
            var psnrMatch = psnrMatches.Count > 0 ? psnrMatches[^1] : null;
            if (psnrMatch != null && psnrMatch.Success)
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
