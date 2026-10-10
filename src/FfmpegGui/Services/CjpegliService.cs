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
        // ⚠ 锁同时罩住 `Detect()` 与两个 getter：旧写法 Detect() 第一行就
        //   `_detected = true; _detectedPath = null;` ⇒ 扫描期间并发读者会拿到
        //   "已检测但不可用"的假阴（路由分派直接读它）。同 `DjxlService`。
        private static readonly object _gate = new();

        public static bool IsAvailable
        {
            get { lock (_gate) { if (!_detected) Detect(); return _detectedPath != null; } }
        }

        public static string? DetectedPath
        {
            get { lock (_gate) { if (!_detected) Detect(); return _detectedPath; } }
        }

        public static void Detect()
        {
            lock (_gate) { DetectCore(); }
        }

        private static void DetectCore()
        {
            _detected = true;
            _detectedPath = null;

            try
            {
                var manual = AppSettingsService.Current.JxlLibDir
                          ?? AppSettingsService.Current.CjpegliPath
                          ?? AppSettingsService.Current.CjxlPath;
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
                            var sibling = Path.Combine(dir, PlatformServices.Cjpegli);
                            if (File.Exists(sibling)) { _detectedPath = sibling; return; }
                            try
                            {
                                var list = new List<string>();
                                foreach (var f in Directory.EnumerateFiles(dir, PlatformServices.CjpegliSearchWildcard, SearchOption.TopDirectoryOnly))
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
                        var candidate = Path.Combine(manual, PlatformServices.Cjpegli);
                        if (File.Exists(candidate)) { _detectedPath = candidate; return; }
                        try
                        {
                            // ⚠ 手动目录同样可能是一棵大树 ⇒ 走带预算的递归（见 EnumerateFilesSafe 注释）
                            var list = ExternalToolsDetector.EnumerateFilesSafe(
                                manual, PlatformServices.CjpegliSearchWildcard);
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

            // ── ② PLAN 便携包自动检测 ──
            try
            {
                var planFound = PlatformServices.TryFindInPlanFolder(PlatformServices.Cjpegli);
                if (planFound != null) { _detectedPath = planFound; return; }
            }
            catch { }

            // ── ③ 同目录（ffmpeg 目录 → 程序目录）──
            try
            {
                var ffmpegDir = AppSettingsService.Current.FfmpegDir;
                var programDir = AppDomain.CurrentDomain.BaseDirectory;
                var dirs = new[] { ffmpegDir, programDir };
                foreach (var dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    var found = PlatformServices.FindToolInDirectory(dir, PlatformServices.Cjpegli, PlatformServices.CjpegliSearchWildcard);
                    if (found != null) { _detectedPath = found; return; }
                }
            }
            catch { }

            // ── ④ 扩展搜索路径 ──
            try
            {
                var extended = ExternalToolsDetector.FindToolInExtendedPaths(
                    PlatformServices.Cjpegli, PlatformServices.CjpegliSearchWildcard);
                if (extended != null) { _detectedPath = extended; return; }
            }
            catch { }

            // ── ⑤ 系统 PATH ──
            if (PlatformServices.TryFindInPath(PlatformServices.Cjpegli, out var pathFound))
            {
                _detectedPath = pathFound;
                return;
            }
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

            // log 传下去 ⇒ 「选项被降级」（如固定码表逼出的 -p 0）那句说明进本次调用的日志，
            // 而不是只在命令行串里多出一个 -p 0 让用户自己猜为什么。
            var args = BuildCjpegliArguments(inputPath, outputPath, opts, log: logCallback);
            logCallback?.Invoke($"[cjpegli] {Path.GetFileName(_detectedPath)} {args}\n");

            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath,
                Arguments = args,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
            var linkedToken = linked.Token;

            process.OutputDataReceived += (_, e) => { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };

            try
            {
                process.Start();
                PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
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
        /// <param name="hdrMeta">auto 模式下的输入色彩探测结果（可选）</param>
        /// <param name="log">
        /// 「选项被降级」播报的去向。**只有真执行侧的调用方传**（预览/命令行串调用方不传），
        /// 与本仓 <c>CjxlService.BuildCjxlArguments(…, forPreview, log)</c> 同一约定：
        /// 本函数每次生成命令行都会被调多遍，无条件写 Console 会让一个任务刷出好几条同样的话。
        /// </param>
        public static string BuildCjpegliArguments(string input, string output, Models.FfmpegOptions opts,
            FfmpegCommandBuilder.ColorMetadata hdrMeta = default, string? iccPath = null, bool includeThreads = true,
            Action<string>? log = null)
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

            // 渐进模式 (0=sequential/baseline, 2=progressive, -1=不传参使用默认)
            var progId = opts.CjpegliProgressiveId;
            // Huffman 优化关闭 → --fixed_code 仅与 sequential (-p 0) 兼容，自动降级（2026-08-16 实测）
            if (!opts.CjpegliOptimize && progId != 0)
            {
                // 出声（2026-10-09 实机复现的**静默**降级）：用户要的渐进档在这里被改成 -p 0 ⇒ 产物从
                // Progressive(SOF2) 变成非渐进。降级本身躲不掉 —— 实测 `--fixed_code` 配 `-p 1/2`
                // 或不配 `-p` ⇒ cjpegli 退出码 1、零产物 —— 但“改了用户的选择不说话”是另一回事。
                // 只写 log（不写 Console）的理由见本方法 log 形参：预览侧的冲突提示由
                // MainWindow.UpdateJpegliProgressiveConflictHint 承担。
                // ⚠ 文案必须纯 ASCII：`_probe-cjk-hardcode-scan.ps1` 的 CsUi 面余量为 0。
                var requested = progId < 0
                    ? "no -p (cjpegli's own default = progressive level 2)"
                    : $"-p {progId}";
                log?.Invoke("[cjpegli] --fixed_code requires -p 0: " + requested
                    + " was requested while Huffman code optimization is off, so this image is encoded with"
                    + " -p 0 (sequential/baseline) instead. Keep 'Optimize Huffman coding' enabled to get progressive output.\n");
                progId = 0;
            }
            if (progId >= 0)
                sb.Append($" -p {progId}");

            // Huffman 优化（当前 cjpegli: --fixed_code 禁用优化；旧版 --optimize 参数已随 libjxl 移除）
            if (!opts.CjpegliOptimize)
                sb.Append(" --fixed_code");

            // 自适应量化（当前 cjpegli: --noadaptive_quantization；旧版 --adaptive_q 参数已移除）
            if (!opts.CjpegliAdaptiveQuant)
                sb.Append(" --noadaptive_quantization");

            // ⚠️ 2026-08-16: 当前 cjpegli 版本（libjxl 新）不支持 --jpeg_encoder / --psnr
            // （实测报 "Unknown argument" 且编码失败），已移除参数生成；UI 侧 sjpeg 后端已禁用。
            // 如未来 cjpegli 重新支持 sjpeg 后端，可在此恢复：
            //   sb.Append($" --jpeg_encoder {opts.CjpegliEncoderBackend}");
            //   sb.Append($" --psnr {opts.CjpegliPsnrTarget:F2}");

            // 线程（仅当多线程可用时；includeThreads=false 供裸流管道调用，与 JxlPipelineService 语义一致）
            if (includeThreads && opts.CjpegliMultiThreadAvailable && opts.Threads > 0)
                sb.Append($" --num_threads={opts.Threads}");

            // ── 色彩空间映射 ──
            // JPEG 输出为 SDR；管道模式下 ffmpeg 已将色彩转为 PPM RGB，不应再套用色彩标记
            // 有显式 ICC 文件时优先：嵌入 ICC（libjxl 中 ICC 优先于 color_space 简写）
            // 注意：-x 必须是独立 argv（不接受 "-x key=value" 合成单参数），值为路径时整体加引号
            if (!string.IsNullOrWhiteSpace(iccPath) && System.IO.File.Exists(iccPath))
            {
                sb.Append($" -x \"icc_pathname={iccPath}\"");
                return sb.ToString();
            }
            string? colorSpace = null;
            var isPipe2 = input == "-";
            if (!isPipe2)
            {
                if (hdrMeta.bitDepth > 8)
                    colorSpace = ColorEncodingHelper.MapToCjxlColorSpace(hdrMeta);
                else
                    colorSpace = ColorEncodingHelper.MapToCjxlColorSpace(opts);
            }
            if (!string.IsNullOrWhiteSpace(colorSpace))
            {
                // -x 必须是独立 argv；color_space 值无空格，无需内层引号
                sb.Append($" -x color_space={colorSpace}");
            }

            return sb.ToString();
        }

        // TODO: 添加 Stream/pipe API：RunStreamAsync(Stream input, Stream output, ...)
    }
}
