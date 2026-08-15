using System;
using System.Collections.Generic;
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
        /// <summary>PSNR 计算域: true=RGB, false=YUV (仅标注, 不影响数值)</summary>
        public bool PsnrIsRgb { get; set; } = true;
        /// <summary>PSNR 位深 (8/10/12/16)</summary>
        public int PsnrBitDepth { get; set; } = 8;
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

            // ★ JXL/JXR 解码支持：ffmpeg 不支持这些格式，需先用外部工具转临时 PNG
            string? tempPngPath = null;
            string? tempPngPath2 = null;
            string? rawSrcPath = null;
            string? rawEncPath = null;
            var actualSourcePath = sourcePath;
            var actualEncodedPath = encodedPath;
            try
            {
                // JXL 源文件 → djxl 转 PNG
                if (sourcePath.EndsWith(".jxl", StringComparison.OrdinalIgnoreCase))
                {
                    var djxlPath = DjxlService.DetectedPath;
                    if (!string.IsNullOrEmpty(djxlPath) && File.Exists(djxlPath))
                    {
                        tempPngPath = Path.Combine(PlatformServices.GetTempDir(), $"qa_src_{Guid.NewGuid():N}.png");
                        if (await RunDecoderAsync(djxlPath, $"\"{sourcePath}\" \"{tempPngPath}\"") == 0
                            && File.Exists(tempPngPath) && new FileInfo(tempPngPath).Length > 0)
                            actualSourcePath = tempPngPath;
                        else { TryDeleteFile(tempPngPath); tempPngPath = null; result.Error = "JXL 源解码失败"; return result; }
                    }
                    else { result.Error = "JXL 源需 djxl.exe"; return result; }
                }

                // JXR 源/编码文件 → JxrDecApp 转 BMP
                // ⚠️ 2026-08-15 修复: 仅 JXR 输入才需要 JxrDecApp (此前无条件要求导致非 JXR 分析失败)
                var isJxrInput = sourcePath.EndsWith(".jxr", StringComparison.OrdinalIgnoreCase)
                              || encodedPath.EndsWith(".jxr", StringComparison.OrdinalIgnoreCase);
                var jxrDecPath = isJxrInput ? FindJxrDecApp() : null;
                if (isJxrInput && string.IsNullOrEmpty(jxrDecPath))
                {
                    result.Error = $"JXR 质量分析需要 JxrDecApp.exe，但未检测到。\n已尝试路径:\n  {_lastJxrDecError}\n请将 JxrDecApp.exe 放入上述任一目录中。";
                    return result;
                }

                if (sourcePath.EndsWith(".jxr", StringComparison.OrdinalIgnoreCase))
                {
                    tempPngPath = Path.Combine(PlatformServices.GetTempDir(), $"qa_src_{Guid.NewGuid():N}.bmp");
                    if (await RunDecoderAsync(jxrDecPath, $"-i \"{sourcePath}\" -o \"{tempPngPath}\"") == 0
                        && File.Exists(tempPngPath) && new FileInfo(tempPngPath).Length > 0)
                    {
                        actualSourcePath = tempPngPath;
                        PlatformServices.MarkAsTemporaryFile(tempPngPath);
                    }
                    else { TryDeleteFile(tempPngPath); tempPngPath = null; result.Error = "JXR 源解码失败"; return result; }
                }

                if (encodedPath.EndsWith(".jxr", StringComparison.OrdinalIgnoreCase))
                {
                    tempPngPath2 = Path.Combine(PlatformServices.GetTempDir(), $"qa_enc_{Guid.NewGuid():N}.bmp");
                    if (await RunDecoderAsync(jxrDecPath, $"-i \"{encodedPath}\" -o \"{tempPngPath2}\"") == 0
                        && File.Exists(tempPngPath2) && new FileInfo(tempPngPath2).Length > 0)
                    {
                        actualEncodedPath = tempPngPath2;
                        PlatformServices.MarkAsTemporaryFile(tempPngPath2);
                    }
                    else { TryDeleteFile(tempPngPath2); tempPngPath2 = null; result.Error = "JXR 编码输出解码失败"; return result; }
                }

                // 分辨率一致性预检（使用实际源文件路径）
                var resCheck = await CheckResolutionMatchAsync(actualSourcePath, actualEncodedPath, fileName);
                if (!string.IsNullOrEmpty(resCheck))
                {
                    result.Error = resCheck;
                    return result;
                }
                // 分辨率 (用于 .NET 原生 PSNR 的帧大小计算)
                var srcRes = await GetResolutionAsync(actualSourcePath, fileName)
                             ?? await GetResolutionAsync(actualEncodedPath, fileName);
                if (srcRes == null)
                {
                    result.Error = "无法获取图像分辨率，无法进行质量分析";
                    return result;
                }

                // ── 目标域选择 (2026-08-15: 按目标输出格式的编码域做质量分析) ──
                // 跨格式比较原则: 子采样损失是格式固有特性, 应在编码器原生域比较才能公平反映质量
                //   RGB 系 (png/tiff/jxl/bmp/apng/gif): RGB 域
                //   YUV 系 (jpg/jpeg/webp/avif/heic/heif/jxr/视频): YUV 域
                // 实测 (256x192): WebP(420) 在 YUV444 域被惩罚 10dB, RGB 域恢复合理值
                //   → 目标为 RGB 系格式时必须用 RGB 域, 否则子采样损失被误判为编码质量差
                bool targetIsRgb = IsRgbNativeFormat(encodedPath);

                // ── 位深探测 (支持 8-16bit) ──
                int bitsPerSample = 8;
                try
                {
                    var probe = FfmpegCommandBuilder.ProbeInputColorMetadata(actualSourcePath);
                    bitsPerSample = probe.bitDepth > 0 ? probe.bitDepth : 8;
                }
                catch { }
                if (bitsPerSample <= 8)
                {
                    try
                    {
                        var probe2 = FfmpegCommandBuilder.ProbeInputColorMetadata(actualEncodedPath);
                        if (probe2.bitDepth > bitsPerSample) bitsPerSample = probe2.bitDepth;
                    }
                    catch { }
                }
                result.PsnrBitDepth = bitsPerSample;
                result.PsnrIsRgb = targetIsRgb;

                // 域容器选择: 8-bit → 8bit, 高位深 → 16bit
                bool useHighBitDepth = bitsPerSample > 8;
                string pixFmt;
                if (targetIsRgb)
                    pixFmt = useHighBitDepth ? "rgb48le" : "rgb24";
                else
                    pixFmt = useHighBitDepth ? "yuv444p16le" : "yuv444p";
                int bytesPerPixel = useHighBitDepth ? 6 : 3;   // 3ch × (1|2)byte
                int frameBytes = srcRes.Value.Width * srcRes.Value.Height * bytesPerPixel;
                int psnrBitsPerSample = useHighBitDepth ? 16 : 8;

                // 选择输出文件中最佳的视轨（多轨 AVIF 等需选动画轨而非封面轨）
                var srcStream = "0:v";
                var encStream = await SelectBestVideoStreamAsync(actualEncodedPath, fileName);

                // ★ 动图修复:
                // 1) settb=1/1000 + setpts=N 按帧序号对齐（忽略原始帧间隔），消除不同
                //    帧率/时间基准导致的帧错位对比——这才是 PSNR 极低的根本原因
                // 2) SSIM 由 ffmpeg ssim filter 计算; PSNR 由 .NET 原生 (PsnrCalculator) 计算
                //    —— ffmpeg 同时输出两路目标域 raw 供 .NET 读取 (split 复制流)
                // 3) scale=out_range=pc: 统一 full range (PNG=pc, JPEG=tv→pc), 否则值域错乱 PSNR 假低分
                rawSrcPath = Path.Combine(PlatformServices.GetTempDir(), $"qa_psnr_src_{Guid.NewGuid():N}.raw");
                rawEncPath = Path.Combine(PlatformServices.GetTempDir(), $"qa_psnr_enc_{Guid.NewGuid():N}.raw");
                var args = $"-hide_banner -i \"{actualSourcePath}\" -i \"{actualEncodedPath}\" " +
                           $"-filter_complex \"[{srcStream}]settb=1/1000,setpts=N,scale=out_range=pc,format={pixFmt},split[srcA][srcB];" +
                           $"[{encStream}]settb=1/1000,setpts=N,scale=out_range=pc,format={pixFmt},split[encA][encB];" +
                           $"[srcA][encA]ssim[ssim_out]\" " +
                           $"-map \"[ssim_out]\" -f null - " +
                           $"-map \"[srcB]\" -f rawvideo -pix_fmt {pixFmt} \"{rawSrcPath}\" " +
                           $"-map \"[encB]\" -f rawvideo -pix_fmt {pixFmt} \"{rawEncPath}\"";

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

                // ── .NET 原生 PSNR (2026-08-15, 替代 ffmpeg psnr filter) ──
                // 读取 ffmpeg 输出的两路 rawvideo 计算 PSNR，支持位深与色域自适应。
                // 多帧 (动图): 全局 MSE → average, 逐帧 PSNR → min/max。
                if (File.Exists(rawSrcPath) && File.Exists(rawEncPath))
                {
                    try
                    {
                        var rawA = File.ReadAllBytes(rawSrcPath);
                        var rawB = File.ReadAllBytes(rawEncPath);
                        if (rawA.Length > 0 && rawA.Length == rawB.Length)
                        {
                            var (avg, min, max) = PsnrCalculator.CalculateMultiFramePsnr(rawA, rawB, frameBytes,
                                bitsPerSample: psnrBitsPerSample, channels: 3, isRgb: false);
                            result.PsnrAverage = avg;
                            if (!double.IsPositiveInfinity(min)) result.PsnrMin = min;
                            if (!double.IsPositiveInfinity(max)) result.PsnrMax = max;
                        }
                        else if (rawA.Length != rawB.Length)
                        {
                            result.Error = "rawvideo 输出长度不一致（动图帧数不同），PSNR 无法计算";
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Error = $"PSNR 计算失败: {ex.Message}";
                    }
                }

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
            finally
            {
                // 清理 JXL 解码产生的临时 PNG 文件
                if (tempPngPath != null)
                    TryDeleteFile(tempPngPath);
                if (tempPngPath2 != null)
                    TryDeleteFile(tempPngPath2);
                // 清理 .NET PSNR 用的 rawvideo 临时文件
                if (rawSrcPath != null)
                    TryDeleteFile(rawSrcPath);
                if (rawEncPath != null)
                    TryDeleteFile(rawEncPath);
            }

            return result;
        }

        /// <summary>
        /// 安全删除临时文件（不抛异常）
        /// </summary>
        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>
        /// 判断目标输出格式是否为 RGB 原生 (2026-08-15: 目标域选择依据)。
        /// RGB 系: PNG/TIFF/BMP/JXL/APNG/GIF — 无子采样, RGB 域比较公平
        /// YUV 系: JPEG/WebP/AVIF/HEIC/JXR/视频 — 内部 YUV 编码 (4:2:0/4:4:4)
        /// 注: 无损 WebP/JXL 在任意域 PSNR 均为 inf, 不受归类影响
        /// </summary>
        private static bool IsRgbNativeFormat(string outputPath)
        {
            var ext = Path.GetExtension(outputPath).ToLowerInvariant();
            return ext switch
            {
                ".png" or ".apng" or ".tiff" or ".tif" or ".bmp" or ".jxl" or ".gif" => true,
                _ => false
            };
        }

        /// <summary>运行外部解码器进程，返回退出码</summary>
        private static async Task<int> RunDecoderAsync(string exePath, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return -1;
                await p.WaitForExitAsync();
                return p.ExitCode;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 查找 JxrDecApp.exe
        /// 优先级：手动路径 > ffmpeg/程序同目录 > 其他工具同目录
        /// </summary>
        private static string? FindJxrDecApp()
        {
            var tried = new List<string>();
            string? found = null;

            bool Try(string? dir, string label)
            {
                if (string.IsNullOrEmpty(dir)) return false;
                var p = Path.Combine(dir, PlatformServices.JxrDec);
                tried.Add($"[{label}] {p}");
                if (File.Exists(p)) { found = p; return true; }
                return false;
            }

            // ① 最高优先级：手动路径
            if (Try(AppSettingsService.Current.JxrPath, "手动JxrPath")) return found;
            if (!string.IsNullOrEmpty(AppSettingsService.Current.JxrPath))
                if (Try(Path.GetDirectoryName(AppSettingsService.Current.JxrPath), "手动JxrPath目录")) return found;

            // ② 同目录：ffmpeg + 程序目录
            if (Try(AppSettingsService.Current.FfmpegDir, "FfmpegDir")) return found;
            if (Try(AppDomain.CurrentDomain.BaseDirectory, "AppDir")) return found;

            // ③ 其他工具路径同目录
            if (Try(JxrService.DetectedPath, "JxrEncDetected")) return found;
            if (JxrService.DetectedPath != null)
                if (Try(Path.GetDirectoryName(JxrService.DetectedPath), "JxrEncDir")) return found;
            foreach (var cp in new[] { AppSettingsService.Current.CjxlPath, AppSettingsService.Current.CjpegliPath,
                AppSettingsService.Current.AvifencPath })
            {
                if (!string.IsNullOrEmpty(cp))
                {
                    if (Try(Path.GetDirectoryName(cp), "CfgToolDir")) return found;
                }
            }

            _lastJxrDecError = string.Join("\n  ", tried);
            return null;
        }
        private static string _lastJxrDecError = "";

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
            // ⚠️ 2026-08-15: PSNR 已由 .NET 原生 (PsnrCalculator) 计算, 此处仅作为
            //    rawvideo 输出失败时的回退 (兼容旧 ffmpeg 输出)。
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
            if (psnrMatch != null && psnrMatch.Success && !result.PsnrAverage.HasValue)
            {
                ParseDouble(psnrMatch.Groups[1].Value, v => result.PsnrAverage = v);
                if (psnrMatch.Groups[2].Success && !result.PsnrMin.HasValue)
                    ParseDouble(psnrMatch.Groups[2].Value, v => result.PsnrMin = v);
                if (psnrMatch.Groups[3].Success && !result.PsnrMax.HasValue)
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
