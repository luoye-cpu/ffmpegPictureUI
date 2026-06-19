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

            // ① 手动指定路径
            var manual = AppSettingsService.Current.UltrahdrPath;
            if (!string.IsNullOrWhiteSpace(manual))
            {
                if (File.Exists(manual))
                {
                    _detectedPath = manual;
                    return;
                }
                if (Directory.Exists(manual))
                {
                    var candidate = Path.Combine(manual, "ultrahdr_app.exe");
                    if (File.Exists(candidate)) { _detectedPath = candidate; return; }
                }
            }

            // ② ffmpeg 同目录 → 程序同目录
            var dirs = new[]
            {
                AppSettingsService.Current.FfmpegDir ?? "",
                AppDomain.CurrentDomain.BaseDirectory,
            };
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var candidate = Path.Combine(dir, "ultrahdr_app.exe");
                if (File.Exists(candidate)) { _detectedPath = candidate; return; }
            }

            // ③ 系统 PATH
            if (TryFindInPath("ultrahdr_app.exe", out var pathFound))
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
                    var firstLine = output.Split(new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries)[0];
                    if (File.Exists(firstLine)) { fullPath = firstLine; return true; }
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
        /// 构建 ultrahdr_app 编码命令行（不含 exe 路径）。
        /// 场景 0：单一 HDR RAW 输入 → Ultra HDR JPEG（自动 tone-map 生成 SDR 基底）。
        /// </summary>
        /// <param name="hdrRawPath">HDR RAW 文件路径（p010 或 rgba1010102）</param>
        /// <param name="outputPath">输出 JPEG 路径</param>
        /// <param name="width">图像宽度</param>
        /// <param name="height">图像高度</param>
        /// <param name="quality">JPEG 质量 (0-100)</param>
        /// <param name="hdrCf">HDR 色彩格式: 0=p010, 5=rgba1010102</param>
        /// <param name="gainmapQuality">增益图质量 (-1=同主质量)</param>
        /// <param name="targetNits">目标显示器亮度 nit</param>
        public static string BuildArguments(
            string hdrRawPath, string outputPath,
            int width, int height, int quality,
            int hdrCf = 0, int gainmapQuality = -1, int targetNits = 1000)
        {
            var sb = new StringBuilder();
            sb.Append($"-m 0");  // encode mode
            sb.Append($" -p \"{hdrRawPath}\"");
            sb.Append($" -w {width}");
            sb.Append($" -h {height}");
            sb.Append($" -q {quality}");
            sb.Append($" -a {hdrCf}");

            if (gainmapQuality >= 0)
                sb.Append($" -Q {gainmapQuality}");
            if (targetNits > 0)
                sb.Append($" -L {targetNits}");

            sb.Append($" -z \"{outputPath}\"");
            return sb.ToString();
        }

        /// <summary>
        /// 执行 ultrahdr_app 编码。
        /// </summary>
        public static async Task<int> RunAsync(
            string arguments,
            Action<string>? logCallback = null)
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
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync();
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[ultrahdr] 启动失败: {ex.Message}\n");
                return -1;
            }
        }
    }
}
