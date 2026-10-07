using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// JxrEncApp.exe 集成服务：JPEG XR 编码（Microsoft jxrlib 参考实现）。
    /// 支持 BMP/TIFF/HDR 输入，无损/有损，Alpha 通道，32 种像素格式。
    /// 检测优先级：手动路径 → ffmpeg 同目录 → 程序同目录 → 系统 PATH。
    /// </summary>
    public static class JxrService
    {
        private static string? _detectedPath;
        private static bool _detected;
        // ⚠ 锁同时罩住 `Detect()` 与两个 getter（同 `DjxlService`：扫描窗口内的并发读者
        //   会拿到"已检测但不可用"的假阴，而 JXR 的编解码分派直接读它）。
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

            // ① 手动指定路径（WindowsArtifactsDir 优先，旧字段兼容）
            var manual = AppSettingsService.Current.WindowsArtifactsDir
                      ?? AppSettingsService.Current.JxrPath;
            if (!string.IsNullOrWhiteSpace(manual))
            {
                if (File.Exists(manual))
                {
                    _detectedPath = manual;
                    return;
                }
                if (Directory.Exists(manual))
                {
                    var candidate = Path.Combine(manual, PlatformServices.JxrEnc);
                    if (File.Exists(candidate)) { _detectedPath = candidate; return; }
                }
            }

            // ② PLAN 便携包自动检测
            try
            {
                var planFound = PlatformServices.TryFindInPlanFolder(PlatformServices.JxrEnc);
                if (planFound != null) { _detectedPath = planFound; return; }
            }
            catch { }

            // ③ ffmpeg 同目录 → 程序同目录
            var dirs = new[]
            {
                AppSettingsService.Current.FfmpegDir ?? "",
                AppDomain.CurrentDomain.BaseDirectory,
            };
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var candidate = Path.Combine(dir, PlatformServices.JxrEnc);
                if (File.Exists(candidate)) { _detectedPath = candidate; return; }
            }

            // ④ 扩展搜索路径
            try
            {
                var extended = ExternalToolsDetector.FindToolInExtendedPaths(
                    PlatformServices.JxrEnc, $"*{PlatformServices.JxrEnc}*");
                if (extended != null) { _detectedPath = extended; return; }
            }
            catch { }

            // ⑤ 系统 PATH
            if (PlatformServices.TryFindInPath(PlatformServices.JxrEnc, out var pathFound))
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
        /// 构建 JxrEncApp 编码命令行。
        /// </summary>
        /// <param name="inputPath">输入文件路径（BMP/TIFF）</param>
        /// <param name="outputPath">输出 .jxr 路径</param>
        /// <param name="quality">质量 0.0-1.0 (1.0=无损)</param>
        /// <param name="chromaSubsampling">色度子采样: 1=4:2:0, 2=4:2:2, 3=4:4:4 (默认), -1=auto</param>
        /// <param name="progressive">渐进编码（默认 true）</param>
        /// <param name="overlapping">重叠级别: 0/1/2, -1=auto</param>
        public static string BuildArguments(
            string inputPath, string outputPath,
            double quality = 1.0,
            int chromaSubsampling = -1,
            bool progressive = true,
            int overlapping = -1)
        {
            var sb = new StringBuilder();
            sb.Append($"-i \"{inputPath}\"");
            sb.Append($" -o \"{outputPath}\"");
            sb.Append($" -q {quality:F2}");

            if (chromaSubsampling >= 0 && chromaSubsampling <= 3)
                sb.Append($" -d {chromaSubsampling}");

            if (!progressive)
                sb.Append(" -p");

            if (overlapping >= 0 && overlapping <= 2)
                sb.Append($" -l {overlapping}");

            return sb.ToString();
        }

        /// <summary>
        /// 从用户选项解析 JxrEncApp 的 `-q` 质量值 (0.0-1.0) —— **唯一真源**。
        ///
        /// ⚠ 抽出来的动机（2026-10-07，D-1）：JXR 有两个真实出口
        ///   （`QueueProcessor.ProcessJxrAsync` 与 `RawColorPipeline.EncodeJxrViaJxrEncAppAsync`），
        ///   两处都写着同一条判据「`Quality/100.0`，`Lossless` 时恒 1.0」。
        ///   `--dry-run` 预览**此前完全没有这条判据**（只写 `ffmpeg`，见 D-1），
        ///   若在预览里再抄一份就会变成第三处 ⇒ 迟早漂移。故收拢到本方法，三处共用。
        /// </summary>
        /// <param name="qualityPercent">用户质量 (0-100)</param>
        /// <param name="lossless">无损开关（true 时恒 1.0）</param>
        public static double ResolveQuality(int qualityPercent, bool lossless)
            => lossless ? 1.0 : qualityPercent / 100.0;

        /// <summary>
        /// 执行 JxrEncApp 编码。
        /// </summary>
        public static async Task<int> RunAsync(
            string arguments,
            Action<string>? logCallback = null,
            CancellationToken ct = default)
        {
            if (_detectedPath == null)
                throw new InvalidOperationException("JxrEncApp.exe 未找到");

            logCallback?.Invoke($"[jxr] {Path.GetFileName(_detectedPath)} {arguments}\n");

            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };
            process.ErrorDataReceived += (_, e) =>
            { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };

            try
            {
                process.Start();
                PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                // 超时守卫（对齐 CjxlService 的 30 分钟）：JxrEncApp 挂死时若无超时将永久
                // 阻塞队列项并泄漏并发槽位；ct 为 default（调用方未传）时尤甚（P2）。
                using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(30));
                using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                using var reg = linked.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
                try
                {
                    await process.WaitForExitAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        logCallback?.Invoke("[jxr] 已超时（30 分钟），已终止进程\n");
                        return -1;
                    }
                    throw;   // 用户取消：维持原有取消语义
                }
                return process.ExitCode;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[jxr] 启动失败: {ex.Message}\n");
                return -1;
            }
        }
    }
}
