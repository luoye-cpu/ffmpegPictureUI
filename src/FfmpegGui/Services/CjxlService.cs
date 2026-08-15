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

        /// <summary>已检测到的 cjxl.exe 完整路径（null = 不可用）</summary>
        public static string? DetectedPath
        {
            get
            {
                if (!_detected) Detect();
                return _detectedPath;
            }
        }

        /// <summary>
        /// 检测 cjxl.exe 位置（三优先级）：
        /// ① 手动指定路径（AppSettings.CjxlPath）
        /// ② ffmpeg 同目录 / 程序同目录
        /// ③ 系统 PATH
        /// </summary>
        public static void Detect()
        {
            _detected = true;
            _detectedPath = null;

            // 测试桩支持
            try
            {
                var stub = Environment.GetEnvironmentVariable("FFMPEGGUI_CJXL_STUB");
                if (!string.IsNullOrWhiteSpace(stub) && stub == "1")
                {
                    _detectedPath = PlatformServices.Cjxl;
                    return;
                }
            }
            catch { }

            // ── ① 手动指定路径（JxlLibDir 优先，旧字段兼容）──
            var manual = AppSettingsService.Current.JxlLibDir
                      ?? AppSettingsService.Current.CjxlPath
                      ?? AppSettingsService.Current.CjpegliPath;
            if (!string.IsNullOrWhiteSpace(manual))
            {
                try
                {
                    // 如果用户直接指定了可执行文件路径
                    if (File.Exists(manual))
                    {
                        _detectedPath = manual;
                        return;
                    }

                    // 如果用户指定的是目录，尝试在该目录（及子目录）查找 cjxl
                    if (Directory.Exists(manual))
                    {
                        var candidate = Path.Combine(manual, PlatformServices.Cjxl);
                        if (File.Exists(candidate))
                        {
                            _detectedPath = candidate;
                            return;
                        }

                        try
                        {
                            var list = new System.Collections.Generic.List<string>();
                            foreach (var found in Directory.EnumerateFiles(manual, PlatformServices.CjxlSearchWildcard, SearchOption.AllDirectories))
                            {
                                if (File.Exists(found)) list.Add(found);
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
                catch { }
            }

            // ── ② PLAN 便携包自动检测 ──
            try
            {
                var planFound = PlatformServices.TryFindInPlanFolder(PlatformServices.Cjxl);
                if (planFound != null) { _detectedPath = planFound; return; }
            }
            catch { }

            // ── ③ 同目录（ffmpeg 目录 → 程序目录）──
            var ffmpegDir = AppSettingsService.Current.FfmpegDir;
            var programDir = AppDomain.CurrentDomain.BaseDirectory;

            var dirs = new[] { ffmpegDir, programDir };
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var found = PlatformServices.FindToolInDirectory(dir, PlatformServices.Cjxl, PlatformServices.CjxlSearchWildcard);
                if (found != null) { _detectedPath = found; return; }
            }

            // ── ④ 扩展搜索路径（Windows: LocalAppData\Programs, Program Files 等）──
            try
            {
                var extendedPath = ExternalToolsDetector.FindToolInExtendedPaths(
                    PlatformServices.Cjxl, PlatformServices.CjxlSearchWildcard);
                if (extendedPath != null) { _detectedPath = extendedPath; return; }
            }
            catch { }

            // ── ⑤ 系统 PATH ──
            if (PlatformServices.TryFindInPath(PlatformServices.Cjxl, out var pathFound))
            {
                _detectedPath = pathFound;
                return;
            }
        }

        /// <summary>在系统 PATH 中查找可执行文件（已迁移至 PlatformServices）</summary>
        [Obsolete("使用 PlatformServices.TryFindInPath 代替")]
        private static bool TryFindInPath(string exeName, out string? fullPath)
        {
            return PlatformServices.TryFindInPath(exeName, out fullPath);
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
            Action<string>? logCallback = null,
            CancellationToken ct = default)
        {
            if (_detectedPath == null)
                throw new InvalidOperationException("cjxl.exe 未找到");

            // 测试桩：当环境变量 FFMPEGGUI_CJXL_STUB=1 时，不实际启动 cjxl，而是模拟写入输出文件（用于本地验证）
            try
            {
                var stub = Environment.GetEnvironmentVariable("FFMPEGGUI_CJXL_STUB");
                if (!string.IsNullOrWhiteSpace(stub) && stub == "1")
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(outputPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        // 写入一个小的占位文件
                        await File.WriteAllTextAsync(outputPath, "cjxl-stub-output");
                        logCallback?.Invoke($"[cjxl-stub] 写入: {outputPath}{Environment.NewLine}");
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        logCallback?.Invoke($"[cjxl-stub] 写入失败: {ex.Message}{Environment.NewLine}");
                        return -1;
                    }
                }
            }
            catch { }

            // cjxl 命令格式:
            // cjxl input.jpg output.jxl -d 0 -e N --num_threads=N
            // -d 0: 无损
            // -e N: effort
            // 自动检测 JPEG 输入并使用 --jpeg_transcode 模式
            var args = $"\"{inputPath}\" \"{outputPath}\" -d 0 -e {effort} --lossless_jpeg=1";
            if (threads > 0)
                args += $" --num_threads={threads}";

            logCallback?.Invoke($"[cjxl] JPEG→JXL 无损重封装: -d 0 -e {effort} --lossless_jpeg=1 (直接复制 DCT 系数，不解码像素){Environment.NewLine}");
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
                PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
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

        /// <summary>
        /// 使用 cjxl 将指定输入文件编码为 JXL（支持完整选项）。
        /// </summary>
        /// <param name="iccPath">输入提取的 ICC 文件路径（可选）。非 JPEG 输入且源带 ICC 时传入，
        /// 由 cjxl 嵌入输出（exiftool 无法写入 JXL 的 ICC，这是唯一嵌入路径）</param>
        public static async Task<int> RunWithOptionsAsync(
            string inputPath, string outputPath,
            Models.FfmpegOptions opts,
            Action<string>? logCallback = null,
            string? iccPath = null)
        {
            if (_detectedPath == null)
                throw new InvalidOperationException("cjxl.exe 未找到");

            var args = BuildCjxlArguments(inputPath, outputPath, opts, default, iccPath);
            logCallback?.Invoke($"[cjxl] {Path.GetFileName(_detectedPath)} {args}\n");

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
            { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };
            process.ErrorDataReceived += (_, e) =>
            { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };

            try
            {
                process.Start();
                PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
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

        /// <summary>
        /// 构建 cjxl 命令行参数字符串（不含 exe 路径）。
        /// 用于 UI 预览和实际执行。
        /// </summary>
        /// <param name="hdrMeta">auto 模式下的输入色彩探测结果（可选）</param>
        /// <param name="iccPath">输入提取的 ICC 文件路径（可选）。存在时以 ICC 定义完整色彩语义，
        /// 优先于 color_space 简写标签（libjxl 中 ICC 优先）</param>
        public static string BuildCjxlArguments(string input, string output, Models.FfmpegOptions opts,
            FfmpegCommandBuilder.ColorMetadata hdrMeta = default, string? iccPath = null)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"\"{input}\" \"{output}\"");

            var isJpegInput = Path.GetExtension(input).ToLowerInvariant() is ".jpg" or ".jpeg";
            var effort = opts.JxlEffort ?? 7;

            sb.Append($" -e {effort}");

            if (opts.Threads > 0)
                sb.Append($" --num_threads={opts.Threads}");

            if (isJpegInput && opts.JxlLosslessJpeg)
            {
                // JPEG→JXL 无损重封装（高级选项开关开启）：不解码，直接复制 DCT 系数
                sb.Append(" -d 0 --lossless_jpeg=1");
            }
            else if (isJpegInput)
            {
                // JPEG 输入关闭无损重封装 → 解码后重新编码。
                // 注意：cjxl 对 JPEG 输入默认启用 lossless_jpeg，必须显式 --lossless_jpeg=0 覆盖
                if (opts.Lossless)
                    sb.Append(" -d 0 --lossless_jpeg=0");
                else
                    sb.Append($" -d {(100 - opts.Quality) * 15.0 / 100.0:F1} --lossless_jpeg=0");
            }
            else if (opts.Lossless)
            {
                // 非 JPEG 输入的无损模式：distance=0
                sb.Append(" -d 0");
            }
            else
            {
                // 有损：质量→distance 映射
                var distance = (100 - opts.Quality) * 15.0 / 100.0;
                sb.Append($" -d {distance:F1}");
            }

            if (opts.CjxlProgressive)
                sb.Append(" --progressive");

            if (opts.CjxlAutoPhotonNoise)
            {
                // 自动模式：实际 ISO 由 QueueProcessor 在处理前从 EXIF 读取并写回
                // CjxlPhotonNoiseIso；此处若尚未解析（UI 预览场景）显示 auto 占位
                var iso = opts.CjxlPhotonNoiseIso > 0 ? opts.CjxlPhotonNoiseIso.ToString() : "auto";
                sb.Append($" --photon_noise_iso={iso}");
            }
            else if (opts.CjxlPhotonNoiseIso > 0)
                sb.Append($" --photon_noise_iso={opts.CjxlPhotonNoiseIso}");

            // ── 色彩空间映射：将 FFmpeg 色彩参数翻译为 cjxl -x color_space ──
            // 注意：isPipe=true 时仍需设置色彩空间，因为 PPM 管道不携带任何色彩元数据。
            // cjxl 的 -x color_space= 是输出容器标签，与输入格式无关。
            var isPipe = input == "-";

            // 有显式 ICC 文件时优先：ICC 定义完整色彩语义，跳过 color_space 简写（libjxl 中两者冲突时 ICC 优先）
            // 注意：-x 必须是独立 argv（cjxl 不接受 "-x key=value" 合成单参数），值为路径时整体加引号
            // Ultra HDR 解码输出（DecodedUltraHdrColorSpace）场景不适用：线性 HDR 帧的 PQ 语义
            // 由 color_space/intensity_target 精确表达，不能用源 ICC（可能来自 SDR 基础图）覆盖
            if (!string.IsNullOrWhiteSpace(iccPath) && System.IO.File.Exists(iccPath)
                && string.IsNullOrWhiteSpace(opts.DecodedUltraHdrColorSpace))
            {
                sb.Append($" -x \"icc_pathname={iccPath}\"");
                return sb.ToString();
            }

            string? colorSpace = null;
            int intensityTarget = 0;

            // Ultra HDR 解码输出：显式标记 Rec.2100 PQ（优先级最高，线性 HDR 帧语义精确表达）
            if (!string.IsNullOrWhiteSpace(opts.DecodedUltraHdrColorSpace))
            {
                colorSpace = opts.DecodedUltraHdrColorSpace;
                intensityTarget = 10000; // PQ 默认 10000 nits
            }
            else
            {
                // 探测元数据优先（含 8-bit 广色域）：PPM/PAM 管道流不携带色彩标签，必须显式标记；
                // 无探测结果时回退到用户选项映射。
                colorSpace = ColorEncodingHelper.MapToCjxlColorSpace(hdrMeta);
                intensityTarget = ColorEncodingHelper.MapToIntensityTarget(hdrMeta);
                if (string.IsNullOrWhiteSpace(colorSpace))
                {
                    colorSpace = ColorEncodingHelper.MapToCjxlColorSpace(opts);
                    intensityTarget = ColorEncodingHelper.MapToIntensityTarget(opts);
                }
            }

            if (!string.IsNullOrWhiteSpace(colorSpace))
            {
                // -x 必须是独立 argv；color_space 值无空格，无需内层引号
                sb.Append($" -x color_space={colorSpace}");
            }

            // ── HDR 亮度目标（PQ/HLG 时设置）──
            if (intensityTarget > 0)
            {
                sb.Append($" --intensity_target={intensityTarget}");
            }

            return sb.ToString();
        }
    }
}
