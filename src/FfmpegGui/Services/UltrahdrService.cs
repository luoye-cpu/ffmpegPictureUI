using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// ultrahdr_app.exe 集成服务：用于生成 Gain Map (Ultra HDR) JPEG。
    /// 检测优先级：手动路径 → ffmpeg 同目录 → 程序同目录 → 系统 PATH。
    /// 注意：ultrahdr_app 仅接受裸像素 RAW 输入（p010/rgba1010102 等），
    ///       需要配合 ffmpeg 解码为 RAW 后传入。
    /// </summary>
    public static class UltrahdrService
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

            // ① 手动指定路径（WindowsArtifactsDir 优先，旧字段兼容）
            var manual = AppSettingsService.Current.WindowsArtifactsDir
                      ?? AppSettingsService.Current.UltrahdrPath;
            if (!string.IsNullOrWhiteSpace(manual))
            {
                if (File.Exists(manual))
                {
                    _detectedPath = manual;
                    return;
                }
                if (Directory.Exists(manual))
                {
                    var candidate = Path.Combine(manual, PlatformServices.Ultrahdr);
                    if (File.Exists(candidate)) { _detectedPath = candidate; return; }
                }
            }

            // ② PLAN 便携包自动检测
            try
            {
                var planFound = PlatformServices.TryFindInPlanFolder(PlatformServices.Ultrahdr);
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
                var candidate = Path.Combine(dir, PlatformServices.Ultrahdr);
                if (File.Exists(candidate)) { _detectedPath = candidate; return; }
            }

            // ④ 扩展搜索路径
            try
            {
                var extended = ExternalToolsDetector.FindToolInExtendedPaths(
                    PlatformServices.Ultrahdr, $"*{PlatformServices.Ultrahdr}*");
                if (extended != null) { _detectedPath = extended; return; }
            }
            catch { }

            // ⑤ 系统 PATH
            if (PlatformServices.TryFindInPath(PlatformServices.Ultrahdr, out var pathFound))
            {
                _detectedPath = pathFound;
                return;
            }
        }

        private static bool TryFindInPath(string exeName, out string? fullPath)
        {
            return PlatformServices.TryFindInPath(exeName, out fullPath);
        }

        public static void ClearCache()
        {
            _detected = false;
            _detectedPath = null;
        }

        /// <summary>
        /// 构建 ultrahdr_app 编码命令行（不含 exe 路径）。
        /// 场景 0：单一 HDR RAW 输入 → Ultra HDR JPEG（自动 tone-map 生成 SDR 基底）。
        /// 场景 2：HDR RAW + 自定义 SDR 基础图 → Ultra HDR JPEG（使用 -i 传入 cjpegli 编码的高质量 SDR 基础图）。
        /// </summary>
        /// <param name="hdrRawPath">HDR RAW 文件路径（p010 或 rgba1010102）</param>
        /// <param name="outputPath">输出 JPEG 路径</param>
        /// <param name="width">图像宽度</param>
        /// <param name="height">图像高度</param>
        /// <param name="quality">JPEG 质量 (0-100)，仅在无自定义 SDR 基础图时生效</param>
        /// <param name="hdrCf">HDR 色彩格式: 0=p010, 5=rgba1010102</param>
        /// <param name="gainmapQuality">增益图质量 (-1=同主质量)</param>
        /// <param name="targetNits">目标显示器亮度 nit</param>
        /// <param name="sdrBasePath">可选：自定义 SDR 基础图 JPEG 路径（场景 2，由 cjpegli 预编码）</param>
        /// <param name="gainmapDownsample">增益图下采样因子: 1=满分辨率, 2=1/2(默认), 4=1/4, 8=1/8</param>
        public static string BuildArguments(
            string hdrRawPath, string outputPath,
            int width, int height, int quality,
            int hdrCf = 0, int gainmapQuality = -1, int targetNits = 1000,
            string? sdrBasePath = null, int gainmapDownsample = 2)
        {
            var sb = new StringBuilder();
            sb.Append($"-m 0");  // encode mode
            sb.Append($" -p \"{hdrRawPath}\"");
            sb.Append($" -w {width}");
            sb.Append($" -h {height}");
            sb.Append($" -q {quality}");
            sb.Append($" -a {hdrCf}");

            // 场景 2：传入 cjpegli 预编码的 SDR 基础图（更小体积）
            if (!string.IsNullOrWhiteSpace(sdrBasePath))
                sb.Append($" -i \"{sdrBasePath}\"");

            if (gainmapQuality >= 0)
                sb.Append($" -Q {gainmapQuality}");
            if (targetNits > 0)
                sb.Append($" -L {targetNits}");
            if (gainmapDownsample > 1)
                sb.Append($" -s {gainmapDownsample}");

            sb.Append($" -z \"{outputPath}\"");
            return sb.ToString();
        }

        /// <summary>同 BuildArguments，但增加多通道增益图开关 (-M)</summary>
        public static string BuildArguments(
            string hdrRawPath, string outputPath,
            int width, int height, int quality,
            int hdrCf, int gainmapQuality, int targetNits,
            string? sdrBasePath, int gainmapDownsample,
            bool multiChannel)
        {
            var sb = new StringBuilder(BuildArguments(hdrRawPath, outputPath, width, height, quality,
                hdrCf, gainmapQuality, targetNits, sdrBasePath, gainmapDownsample));
            // -M 在 -z 之前插入
            var zIdx = sb.ToString().LastIndexOf(" -z");
            if (zIdx > 0 && !multiChannel)
                sb.Insert(zIdx, " -M 0");
            return sb.ToString();
        }

        /// <summary>
        /// 执行 ultrahdr_app 编码。
        /// </summary>
        public static async Task<int> RunAsync(
            string arguments,
            Action<string>? logCallback = null,
            CancellationToken ct = default)
        {
            if (_detectedPath == null)
                throw new InvalidOperationException("ultrahdr_app.exe 未找到");

            logCallback?.Invoke($"[ultrahdr] {Path.GetFileName(_detectedPath)} {arguments}\n");

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
                if (ct.CanBeCanceled)
                {
                    using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
                    try { await process.WaitForExitAsync(ct); }
                    catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
                }
                else { await process.WaitForExitAsync(); }
                return process.ExitCode;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[ultrahdr] 启动失败: {ex.Message}\n");
                return -1;
            }
        }
    }
}
