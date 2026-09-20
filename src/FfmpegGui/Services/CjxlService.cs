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
        /// P1-1：cjxl 的等待必须**带超时且响应取消**。之前两处都是裸 <c>WaitForExitAsync()</c>：
        /// 用户点停止、或 cjxl 自己挂死 ⇒ 该项永不结束、并发槽位永久泄漏、整条队列卡住
        /// （同仓库的 Djxl/Cjpegli 早有超时守卫，只有 cjxl 漏了）。
        /// 阈值取 30 分钟而非二者的 5 分钟：cjxl 在 effort 9/10 + 大图上合法耗时远高于它们，
        /// 本守卫的目标是杜绝“永久挂死”，不是砍掉慢编码。超时与取消都用非 0 返回并写日志，绝不静默。
        /// </summary>
        private const int ProcessTimeoutMinutes = 30;

        private static async Task<int> WaitGuardedAsync(Process process, CancellationToken ct, Action<string>? logCallback)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(ProcessTimeoutMinutes));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                logCallback?.Invoke(timeoutCts.IsCancellationRequested
                    ? $"[cjxl] ⏱ 超过 {ProcessTimeoutMinutes} 分钟仍未结束 ⇒ 判为挂死，已终止进程树（显式失败，不静默）\n"
                    : "[cjxl] 已取消 ⇒ 已终止进程树\n");
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
            string? iccPath = null,
            CancellationToken ct = default)
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
                return await WaitGuardedAsync(process, ct, logCallback);
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[cjxl] 启动失败: {ex.Message}{Environment.NewLine}");
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
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
        /// <param name="colorSpaceOverride">
        /// **引擎 cjxl 出口专用**（2026-09-17 接入）：由**规划层目标描述符**翻译好的
        /// cjxl <c>color_space</c> 名（翻译在 <c>ColorEncodingHelper.MapToCjxlColorSpace</c>）。
        /// 非 null 时**跳过**下面的探测/选项推导，直接采用 —— 出口的色彩语义只能来自计划，
        /// 不得回头读 options（否则"决策在计划层、标注在出口层"各算一遍）。
        /// </param>
        /// <param name="intensityTargetOverride">
        /// 同上，由出口算好的 <c>--intensity_target</c>（nits；0 = 不指定）。
        /// ⚠ 两者必须**成对**传：只传一个会让"色彩空间来自计划、峰值来自选项"混源。
        /// </param>
        public static string BuildCjxlArguments(string input, string output, Models.FfmpegOptions opts,
            FfmpegCommandBuilder.ColorMetadata hdrMeta = default, string? iccPath = null,
            string? colorSpaceOverride = null, int intensityTargetOverride = 0)
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
            // 出口已翻译好色彩语义 ⇒ 不再看探测/选项（见 colorSpaceOverride 的文档）
            bool hasExplicitColor = colorSpaceOverride != null || intensityTargetOverride > 0;

            // 有显式 ICC 文件时优先：ICC 定义完整色彩语义，跳过 color_space 简写（libjxl 中两者冲突时 ICC 优先）
            // 注意：-x 必须是独立 argv（cjxl 不接受 "-x key=value" 合成单参数），值为路径时整体加引号
            // Ultra HDR 解码输出（DecodedUltraHdrColorSpace）场景不适用：线性 HDR 帧的 PQ 语义
            // 由 color_space/intensity_target 精确表达，不能用源 ICC（可能来自 SDR 基础图）覆盖
            if (!string.IsNullOrWhiteSpace(iccPath) && System.IO.File.Exists(iccPath)
                && string.IsNullOrWhiteSpace(opts.DecodedUltraHdrColorSpace))
            {
                sb.Append($" -x \"icc_pathname={iccPath}\"");
                // HDR：即使走 ICC 路径，仍需 --intensity_target 声明显示峰值亮度（libjxl 中与 ICC 独立）。
                var itHdr = hasExplicitColor
                    ? intensityTargetOverride                       // 出口已算好（含 HDR 目标必给的兜底）
                    : ColorEncodingHelper.MapToIntensityTarget(hdrMeta);
                if (itHdr <= 0) itHdr = ColorEncodingHelper.MapToIntensityTarget(opts);
                if (itHdr > 0) sb.Append($" --intensity_target={itHdr}");
                return sb.ToString();
            }

            string? colorSpace = null;
            int intensityTarget = 0;

            if (hasExplicitColor)
            {
                // ── 引擎 cjxl 出口：色彩语义来自**计划**（colorSpace 可为 null ⇒ 由 ICC 描述）──
                colorSpace = colorSpaceOverride;
                intensityTarget = intensityTargetOverride;
            }
            // Ultra HDR 解码输出：线性 HDR PFM，显式标记线性色彩空间 + 真实峰值（优先级最高）
            else if (!string.IsNullOrWhiteSpace(opts.DecodedUltraHdrColorSpace))
            {
                colorSpace = opts.DecodedUltraHdrColorSpace;
                // intensity_target 取解码得到的真实峰值(203×2^GainMapMax)；旧值硬编码 10000 错约 10×。
                // 未提供峰值时（如 JXL 输入 PQ 管道另路径）沿用 PQ 标称 10000。
                intensityTarget = opts.DecodedUltraHdrPeakNits > 0
                    ? (int)Math.Clamp(Math.Round(opts.DecodedUltraHdrPeakNits), 100, 10000)
                    : 10000;
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
