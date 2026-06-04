using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// cjpegli.exe 集成服务（简易封装）：用于将像素/图片文件编码为 JPEG（使用 jpegli）
    /// 设计目标：与 CjxlService 保持一致的检测优先级（手动 -> 目录 -> PATH）和 RunAsync 接口。
    /// 注意：不同版本的 cjpegli 支持的命令行参数可能不同，建议在运行前使用 --help 校验本地二进制。
    /// </summary>
    public static class CjpegliService
    {
        private static string? _detectedPath;
        private static bool _detected;

        public static bool IsAvailable
        {
            get { if (!_detected) Detect(); return _detectedPath != null; }
        }

        public static string? DetectedPath
        {
            get { if (!_detected) Detect(); return _detectedPath; }
        }

        public static void Detect()
        {
            _detected = true;
            _detectedPath = null;

            try
            {
                // 优先使用专用设置，如果用户未设置则兼容旧的 CjxlPath（有时用户把工具目录存放到同一字段）
                var manual = AppSettingsService.Current.CjpegliPath ?? AppSettingsService.Current.CjxlPath;
                if (!string.IsNullOrWhiteSpace(manual))
                {
                    // 情况 1：manual 直接指向 cjpegli.exe 文件
                    if (File.Exists(manual) && Path.GetFileName(manual).ToLower().Contains("cjpegli"))
                    {
                        _detectedPath = manual;
                        return;
                    }
                    // 情况 2：manual 指向某个文件（比如 cjxl.exe），在其所在目录下查找 cjpegli
                    if (File.Exists(manual))
                    {
                        var dir = Path.GetDirectoryName(manual);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            var sibling = Path.Combine(dir, "cjpegli.exe");
                            if (File.Exists(sibling)) { _detectedPath = sibling; return; }
                            try
                            {
                                var list = new List<string>();
                                foreach (var f in Directory.EnumerateFiles(dir, "*cjpegli*.exe", SearchOption.TopDirectoryOnly))
                                {
                                    if (File.Exists(f)) list.Add(f);
                                }
                                if (list.Count > 0)
                                {
                                    var pick = ExternalToolsDetector.ChooseBestExecutable(list);
                                    if (!string.IsNullOrEmpty(pick)) { _detectedPath = pick; return; }
                                }
                            }
                            catch { }
                        }
                    }
                    // 情况 3：manual 是目录，在其下递归查找
                    if (Directory.Exists(manual))
                    {
                        var candidate = Path.Combine(manual, "cjpegli.exe");
                        if (File.Exists(candidate)) { _detectedPath = candidate; return; }
                        try
                        {
                            var list = new List<string>();
                            foreach (var f in Directory.EnumerateFiles(manual, "*cjpegli*.exe", SearchOption.AllDirectories))
                            {
                                if (File.Exists(f)) list.Add(f);
                            }
                            if (list.Count > 0)
                            {
                                var pick = ExternalToolsDetector.ChooseBestExecutable(list);
                                if (!string.IsNullOrEmpty(pick)) { _detectedPath = pick; return; }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (TryFindInPath("cjpegli.exe", out var pathFound))
            {
                _detectedPath = pathFound;
                return;
            }
        }

        private static bool TryFindInPath(string exeName, out string? fullPath)
        {
            fullPath = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "where" : "which",
                    Arguments = exeName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (File.Exists(firstLine))
                    {
                        fullPath = firstLine;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void ClearCache()
        {
            _detected = false;
            _detectedPath = null;
        }

        /// <summary>
        /// 使用 cjpegli 将指定输入文件编码为 JPEG。此方法基于文件 I/O（调用外部二进制并等待退出）。
        /// 若需更高效的无磁盘管道模式，可在上层实现进程间流式连接（见设计文档）。
        /// </summary>
        public static async Task<int> RunAsync(string inputPath, string outputPath, int quality = 90, int threads = 0, Action<string>? logCallback = null, System.Threading.CancellationToken ct = default)
        {
            return await RunWithOptionsAsync(inputPath, outputPath, new Models.FfmpegOptions
            {
                Quality = quality,
                Threads = threads,
                Format = "jpegli"
            }, logCallback, ct);
        }

        /// <summary>
        /// 使用 cjpegli 将指定输入文件编码为 JPEG（支持完整 cjpegli 选项）。
        /// </summary>
        public static async Task<int> RunWithOptionsAsync(string inputPath, string outputPath, Models.FfmpegOptions opts, Action<string>? logCallback = null, System.Threading.CancellationToken ct = default)
        {
            if (_detectedPath == null)
                throw new InvalidOperationException("cjpegli.exe 未找到");

            var args = BuildCjpegliArguments(inputPath, outputPath, opts);
            logCallback?.Invoke($"[cjpegli] {Path.GetFileName(_detectedPath)} {args}\n");

            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
            var linkedToken = linked.Token;

            process.OutputDataReceived += (_, e) => { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try
                {
                    await process.WaitForExitAsync(linkedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    logCallback?.Invoke("[cjpegli] 已取消或超时，已终止进程\n");
                    return -1;
                }

                return process.ExitCode;
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[cjpegli] 启动失败: {ex.Message}{Environment.NewLine}");
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return -1;
            }
        }

        /// <summary>
        /// 构建 cjpegli 命令行参数字符串（不含 exe 路径）。
        /// 用于 UI 预览和实际执行。
        /// </summary>
        public static string BuildCjpegliArguments(string input, string output, Models.FfmpegOptions opts)
        {
            var sb = new StringBuilder();
            sb.Append($"\"{input}\" \"{output}\"");

            // 质量 → butteraugli distance（感知均匀，优于 --quality 线性映射）
            var distance = Models.FfmpegOptions.MapJpegliDistance(opts.Quality);
            sb.Append($" --distance {distance:F1}");

            // 色度子采样（auto 时不指定，由编码器自行判断）
            if (!string.IsNullOrWhiteSpace(opts.CjpegliChromaSubsampling)
                && !opts.CjpegliChromaSubsampling.Equals("auto", StringComparison.OrdinalIgnoreCase))
                sb.Append($" --chroma_subsampling {opts.CjpegliChromaSubsampling}");

            // 渐进模式
            if (opts.CjpegliProgressiveId >= 0)
                sb.Append($" --progressive {opts.CjpegliProgressiveId}");

            // Huffman 优化
            if (!opts.CjpegliOptimize)
                sb.Append(" --optimize OFF");

            // 自适应量化
            if (!opts.CjpegliAdaptiveQuant)
                sb.Append(" --adaptive_q OFF");

            // 编码器后端
            if (!string.IsNullOrWhiteSpace(opts.CjpegliEncoderBackend) &&
                !opts.CjpegliEncoderBackend.Equals("libjpeg", StringComparison.OrdinalIgnoreCase))
                sb.Append($" --jpeg_encoder {opts.CjpegliEncoderBackend}");

            // PSNR 目标（仅 sjpeg 后端有效）
            if (opts.CjpegliPsnrTarget > 0)
                sb.Append($" --psnr {opts.CjpegliPsnrTarget:F2}");

            // 线程（仅当多线程可用时）
            if (opts.CjpegliMultiThreadAvailable && opts.Threads > 0)
                sb.Append($" --num_threads={opts.Threads}");

            return sb.ToString();
        }

        // TODO: 添加 Stream/pipe API：RunStreamAsync(Stream input, Stream output, ...)
    }
}
