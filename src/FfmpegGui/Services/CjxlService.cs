using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// cjxl.exe 集成服务：JPEG → JXL 无损重封装（直接复制 DCT 系数，速度 5-10×）
    /// </summary>
    public static class CjxlService
    {
        private static string? _detectedPath;
        private static bool _detected;

        /// <summary>
        /// cjxl.exe 是否可用
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (!_detected)
                    Detect();
                return _detectedPath != null;
            }
        }

        /// <summary>
        /// 检测 cjxl.exe 位置（按优先级）：
        /// 1. ffmpeg 同目录
        /// 2. 程序同目录
        /// 3. 系统 PATH
        /// </summary>
        public static void Detect()
        {
            _detected = true;
            _detectedPath = null;

            var candidates = new[]
            {
                // ffmpeg 同目录
                Path.Combine(Path.GetDirectoryName(AppSettingsService.Current.FfmpegPath) ?? "", "cjxl.exe"),
                // 程序同目录
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cjxl.exe"),
                // PATH 中
                "cjxl.exe"
            };

            foreach (var candidate in candidates)
            {
                if (candidate == "cjxl.exe")
                {
                    // 检查 PATH
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "where",
                            Arguments = "cjxl.exe",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        if (p != null)
                        {
                            var output = p.StandardOutput.ReadToEnd().Trim();
                            p.WaitForExit();
                            if (!string.IsNullOrWhiteSpace(output))
                            {
                                var firstLine = output.Split(new[] { '\r', '\n' },
                                    StringSplitOptions.RemoveEmptyEntries)[0];
                                if (File.Exists(firstLine))
                                {
                                    _detectedPath = firstLine;
                                    return;
                                }
                            }
                        }
                    }
                    catch { }
                }
                else if (File.Exists(candidate))
                {
                    _detectedPath = candidate;
                    return;
                }
            }
        }

        /// <summary>
        /// 重置检测缓存（ffmpeg 路径变更后调用）
        /// </summary>
        public static void ClearCache()
        {
            _detected = false;
            _detectedPath = null;
        }

        /// <summary>
        /// 使用 cjxl 进行 JPEG → JXL 无损重封装
        /// </summary>
        /// <param name="inputPath">输入 JPEG 文件路径</param>
        /// <param name="outputPath">输出 JXL 文件路径</param>
        /// <param name="effort">编码努力 (1-9)</param>
        /// <param name="threads">线程数</param>
        /// <param name="logCallback">日志回调</param>
        /// <returns>退出码（0=成功）</returns>
        public static async Task<int> RunAsync(
            string inputPath, string outputPath,
            int effort = 7, int threads = 0,
            Action<string>? logCallback = null)
        {
            if (_detectedPath == null)
                throw new InvalidOperationException("cjxl.exe 未找到");

            // cjxl 命令格式:
            // cjxl input.jpg output.jxl -d 0 -e N --num_threads=N
            // -d 0: 无损
            // -e N: effort
            // 自动检测 JPEG 输入并使用 --jpeg_transcode 模式
            var args = $"\"{inputPath}\" \"{outputPath}\" -d 0 -e {effort}";
            if (threads > 0)
                args += $" --num_threads={threads}";

            logCallback?.Invoke($"[cjxl] {args}{Environment.NewLine}");

            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine);
            };

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
                logCallback?.Invoke($"[cjxl] 启动失败: {ex.Message}{Environment.NewLine}");
                return -1;
            }
        }
    }
}
