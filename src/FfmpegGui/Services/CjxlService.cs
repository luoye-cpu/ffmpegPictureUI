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
        // ⚠ 锁同时罩住 `Detect()` 与两个 getter（同 `DjxlService`：旧写法在 Detect 第一行就
        //   `_detected = true; _detectedPath = null;`，扫描窗口内的并发读者会拿到
        //   "已检测但不可用"的假阴，而 cjxl 的有无直接决定 JXL 出口与 DNG 压缩分派）。
        private static readonly object _gate = new();

        /// <summary>
        /// cjxl.exe 是否可用
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                lock (_gate)
                {
                    if (!_detected)
                        Detect();
                    return _detectedPath != null;
                }
            }
        }

        /// <summary>已检测到的 cjxl.exe 完整路径（null = 不可用）</summary>
        public static string? DetectedPath
        {
            get
            {
                lock (_gate)
                {
                    if (!_detected) Detect();
                    return _detectedPath;
                }
            }
        }

        public static void Detect()
        {
            lock (_gate) { DetectCore(); }
        }

        /// <summary>
        /// 检测体（五优先级）：① 手动指定路径 → ② PLAN 便携包 → ③ ffmpeg/程序同目录
        /// → ④ 系统扩展搜索 → ⑤ 系统 PATH（非递归）。
        /// ⚠ 本注释原先只声明"三优先级"而实现是五级 —— 这类"逐级兜底"代码的声明与实现
        ///   必须同步（djxl 的 ⑤ 曾只有一句注释、没有代码）。
        /// </summary>
        private static void DetectCore()
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
                            // ⚠ 手动目录同样可能是一棵大树 ⇒ 走带预算的递归（见 EnumerateFilesSafe 注释）
                            var list = ExternalToolsDetector.EnumerateFilesSafe(
                                manual, PlatformServices.CjxlSearchWildcard);
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
        /// 输入扩展名是否为 JPEG —— **全仓唯一判据**（2026-09-30 收拢）。
        /// <para>
        /// 此前至少三份各写一遍：<c>CjxlService.BuildCjxlArguments</c> 的
        /// <c>is ".jpg" or ".jpeg"</c>、<c>QueueProcessor.ProcessCjxlAsync:1949</c> 同一对、
        /// <c>MainWindow.IsJpegInput</c> 的 <c>.jpg/.jpeg/.jpe/.jfif</c> 四元 ——
        /// 少一份就会有一格"用户勾了重封装却被当成非 JPEG"。列表取**最宽**的那份（UI 侧）。
        /// </para>
        /// </summary>
        public static bool IsJpegInputPath(string? path)
        {
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif";
        }

        // 增益图探针的按路径缓存：判据被命令行/队列/命令行构造三处消费，不缓存就是每处一次 IO。
        // 上限是防长跑批量（几万张图）把缓存变成泄漏 —— 满了就整表清空，不做 LRU（值只用于"别重复读头"）。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _gainMapJpegCache =
            new(StringComparer.OrdinalIgnoreCase);
        private const int GainMapCacheCap = 512;
        private const long GainMapHeadBytes = 256 * 1024;

        /// <summary>
        /// 这张 JPEG 是否**带增益图**（Ultra HDR / ISO 21496-1）—— 带就说明它不能免解码重封装。
        /// <para>实测（2026-10-01，出货包）：对这类文件发 <c>--lossless_jpeg=1</c>，cjxl **不报错**，
        /// 而是把它解成 HDR float 再编码，产物是 <c>lossy, 32-bit float (8 exponent bits) RGB</c>、
        /// <c>jxlinfo</c> 里没有 reconstruction 盒 ⇒ "无损"宣称与实际交付不符；
        /// 且该 float 产物连自家反向通路都吃不下（djxl 的 PPM 路报
        /// <c>JxlDecoderSetImageOutBitDepth failed</c>，0 产物 rc=1）。取证：<c>tests/output/t76/jbrd-e2e.ps1</c> 的 ultrahdr 臂。</para>
        /// <para>判据复用仓内既有的托管容器解析器 <see cref="GainMapDecoder.JpegContainer.IsGainMapContainer"/>
        /// （ISO 21496-1 为权威；无 ISO 时要求 MPF≥2 幅 + 副图是同比例严格降采样，防把 MPO 3D/全景误判），
        /// 不另写一份 JPEG 解析。只读文件头 ⇒ 大图不必整文件读入。</para>
        /// </summary>
        public static bool JpegCarriesGainMap(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                var key = fi.FullName;
                if (_gainMapJpegCache.TryGetValue(key, out var hit)) return hit;
                int len = (int)Math.Min(GainMapHeadBytes, fi.Length);
                var head = new byte[len];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    fs.ReadExactly(head, 0, len);
                bool ans = GainMapDecoder.JpegContainer.Parse(head)?.IsGainMapContainer() ?? false;
                if (_gainMapJpegCache.Count >= GainMapCacheCap) _gainMapJpegCache.Clear();
                _gainMapJpegCache[key] = ans;
                return ans;
            }
            catch { return false; }   // 读不动 ⇒ 按"不带增益图"处理（保持原行为，不因探针失败改道）
        }

        /// <summary>
        /// 本次是否**真的**走「JPEG→JXL 免解码重封装」（复制 DCT 系数、**不解码像素**）。
        /// <para>这是仓内该判据的**唯一实现**：<see cref="BuildCjxlArguments"/> 用它决定
        /// <c>--lossless_jpeg=1</c>，<c>QueueProcessor</c> 的元数据恢复用它决定
        /// **要不要把源的 <c>Orientation</c> 留在产物上**（见 <c>ExifToolService</c> 的同名参数）。
        /// 两处必须同源：像素没动 ⇒ 取向标签仍然成立、必须保留；像素被解码器旋正 ⇒
        /// 同一张标签会让查看器**二次旋转**。判据各写一份时，第二类缺陷（宣称≠交付）必然复发。</para>
        /// </summary>
        public static bool UsesLosslessJpegRewrap(string? inputPath, Models.FfmpegOptions opts)
            // ⚠ **第三个条件（目标格式必须是 jxl）是 2026-10-01 补的**，缺它的后果是实测到的：
            //   该谓词同时是 `QueueProcessor` 决定"要不要把源的 Orientation 留在产物上"的唯一判据，
            //   而 09-30 起 `JxlLosslessJpeg` 默认开着 ⇒ 只与"输入是 JPEG"相与时，
            //   **JPEG→JPEG/PNG/AVIF/WebP 也被当成"像素没动"** ⇒ 像素已被解码器旋正、标签却还写着 8
            //   ⇒ 查看器二次旋转。出货包实测（`tests/output/t76/orientation-rewrap.ps1`，
            //   源 coded=640x480/Orientation=8）：jpg/png/avif/webp 四格产物 coded=480,640 且 Orientation=8。
            //   本谓词的语义是"本次**真的**走重封装"，而重封装的产物只能是 JXL ⇒ 格式条件本来就在语义里。
            // ⚠ **第四个条件（不能是带增益图的 JPEG）同日补**：cjxl 对这类输入会**静默**放弃重封装
            //   （解成 float 再编，不报错），于是前三个条件全真而"像素没动"为假 ⇒ 同一类"宣称≠交付"。
            // ⚠ **第五个条件（不能同时强制模块化）**：`-m 1` 与"携带 JPEG 码流重构数据"结构上不能并存
            //   （modular 模式装不下 jbrd），所以强制模块化时本次就是像素重编码。判据放在这里而不是只放
            //   在命令行里，是因为 `QueueProcessor` 的取向标签决策读的是本谓词 —— 两处必须同一份实现。
            //   只认 `== true`（与 `ImageEncoderArgs` 的 ffmpeg 路线一致）：面板的"未勾选"含义是
            //   "不强制模块化"，而 cjxl 的 `-m 0` 是"**强制** VarDCT"，把两者划等号会改默认行为。
            => opts != null && IsJpegInputPath(inputPath) && opts.JxlLosslessJpeg
               && string.Equals(opts.Format, "jxl", StringComparison.OrdinalIgnoreCase)
               && !JpegCarriesGainMap(inputPath)
               && opts.JxlModular != true;

        /// <summary>
        /// 使用 cjxl 将指定输入文件编码为 JXL（支持完整选项）。
        /// </summary>
        /// <param name="iccPath">输入提取的 ICC 文件路径（可选）。非 JPEG 输入且源带 ICC 时传入，
        /// 由 cjxl 嵌入输出（exiftool 无法写入 JXL 的 ICC，这是唯一嵌入路径）</param>
        /// <param name="colorSpaceOverride">引擎 cjxl 出口专用：规划层翻译好的 color_space 名，直透
        /// <see cref="BuildCjxlArguments"/>（2026-10-02 随 PNG 中转出口接入 —— 该出口同样要传）。</param>
        /// <param name="intensityTargetOverride">同上，nits；与 colorSpaceOverride **成对**传。</param>
        /// <param name="hdrMeta">输入色彩探测结果，直透 <see cref="BuildCjxlArguments"/>（默认值即可，
        /// 仅当 colorSpaceOverride 为空时参与标注推导）。</param>
        public static async Task<int> RunWithOptionsAsync(
            string inputPath, string outputPath,
            Models.FfmpegOptions opts,
            Action<string>? logCallback = null,
            string? iccPath = null,
            CancellationToken ct = default,
            string? colorSpaceOverride = null,
            int intensityTargetOverride = 0,
            FfmpegCommandBuilder.ColorMetadata hdrMeta = default)
        {
            if (_detectedPath == null)
                throw new InvalidOperationException("cjxl.exe 未找到");

            var args = BuildCjxlArguments(inputPath, outputPath, opts, hdrMeta, iccPath,
                colorSpaceOverride, intensityTargetOverride, log: logCallback);
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
        /// <param name="forPreview">
        /// **仅 UI 预览**传 true（默认 false = 真执行）。预览要把"待从 EXIF 解析"的自动光子噪声显示成
        /// <c>--photon_noise_iso=auto</c>；而 cjxl 的取值解析器是 <c>ParseFloat &amp;&amp; &gt;=0</c>
        /// （libjxl <c>tools/cjxl_main.cc</c> 的 <c>ParsePhotonNoiseParameter</c>），**没有 auto 这个取值**
        /// ⇒ 真执行时未解析的 auto 一律**省略该 flag**（2026-09-30 实测：cjxl 退出码 1、
        /// <c>Unable to interpret as float: auto.</c>、零产物，而写方表现为管道 broken pipe）。
        /// </param>
        /// <param name="log">
        /// 省略/丢弃某个用户请求的参数时**点名**用（可用性诚实）。只在真执行路径传；预览不需要。
        /// </param>
        public static string BuildCjxlArguments(string input, string output, Models.FfmpegOptions opts,
            FfmpegCommandBuilder.ColorMetadata hdrMeta = default, string? iccPath = null,
            string? colorSpaceOverride = null, int intensityTargetOverride = 0,
            bool forPreview = false, Action<string>? log = null)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"\"{input}\" \"{output}\"");

            var isJpegInput = IsJpegInputPath(input);
            // ⚠ fail-closed 点名（2026-09-30）：jbrd 无损重封装要求把 **JPEG 原件**交给 cjxl 复制 DCT
            //   系数；管道输入（`"-"` = 解码后的 PPM/PAM 裸流）在结构上不可能重封装，而 `isJpegInput`
            //   按扩展名判 ⇒ `"-"` 恒 false ⇒ 开关为开时这条命令会**静默退化成像素重编码**
            //   （实测：引擎路线下产物 911 kB / `-d 3.8`，而真重封装是 3,049 kB 的逐比特无损）。
            //   路由层本应拦住这一格（`ImageEncoderArgs` 把它列为引擎不可承接、`ProcessCjxlAsync`
            //   需要改像素时也会改道并写明理由）；这里兜的是"今后又有新路线绕过去"。
            if (opts.JxlLosslessJpeg && isJpegInput == false && !forPreview)
                log?.Invoke("[cjxl] --lossless_jpeg is requested but the input is not a JPEG file"
                          + " (pipe/raw stream): DCT coefficients cannot be repacked from decoded pixels"
                          + " => this run re-encodes pixels instead of losslessly repacking the JPEG\n");
            // ⚠ U1 点名（2026-10-01，出货包实测）：带增益图的 JPEG（Ultra HDR / ISO 21496-1）上，
            //   cjxl 收到 --lossless_jpeg=1 时**不报错**，而是解成 HDR float 重编码，产物既无
            //   reconstruction 盒（"无损"为假）又无法被自家反向通路读回。判据见 JpegCarriesGainMap。
            if (opts.JxlLosslessJpeg && isJpegInput && !forPreview
                && string.Equals(opts.Format, "jxl", StringComparison.OrdinalIgnoreCase)
                && JpegCarriesGainMap(input))
                log?.Invoke("[cjxl] --lossless_jpeg is requested but this JPEG carries a gain map (Ultra HDR / ISO 21496-1):"
                          + " cjxl cannot repack its DCT coefficients and would silently re-encode it as HDR float"
                          + " => sending --lossless_jpeg=0 and naming it instead of claiming lossless repacking\n");
            // ⚠ U5 接线（2026-10-01）：此前 `--jxl-modular` 只在 ffmpeg/libjxl 路线落地（`ImageEncoderArgs`），
            //   cjxl 路线 0 引用 ⇒ 同一个开关两条后端一条活一条**静默失效**（出货包实测两端产物 SHA 相同）。
            //   cjxl 侧的写法是 `-m 1` / `--modular=1`（见其 advanced options）。
            if (opts.JxlModular == true)
            {
                sb.Append(" --modular=1");
                if (opts.JxlLosslessJpeg && isJpegInput && !forPreview
                    && string.Equals(opts.Format, "jxl", StringComparison.OrdinalIgnoreCase))
                    log?.Invoke("[cjxl] --jxl-modular and lossless JPEG repacking are structurally exclusive"
                              + " (modular mode cannot carry JPEG bitstream reconstruction data)"
                              + " => modular mode wins, this run re-encodes pixels\n");
            }
            var effort = opts.JxlEffort ?? 7;

            sb.Append($" -e {effort}");

            if (opts.Threads > 0)
                sb.Append($" --num_threads={opts.Threads}");

            // 实际交给 cjxl 的 distance。**判据是这一个数是否为 0，不是 opts.Lossless**：
            // 有损分支在 Quality=100 时算出 `-d 0.0`，cjxl 同样按数学无损处理。
            double emittedDistance;

            if (UsesLosslessJpegRewrap(input, opts))
            {
                // JPEG→JXL 无损重封装（高级选项开关开启）：不解码，直接复制 DCT 系数
                emittedDistance = 0;
                sb.Append(" -d 0 --lossless_jpeg=1");
            }
            else if (isJpegInput)
            {
                // JPEG 输入关闭无损重封装 → 解码后重新编码。
                // 注意：cjxl 对 JPEG 输入默认启用 lossless_jpeg，必须显式 --lossless_jpeg=0 覆盖
                if (opts.Lossless)
                {
                    emittedDistance = 0;
                    sb.Append(" -d 0 --lossless_jpeg=0");
                }
                else
                {
                    emittedDistance = ColorMapping.ImageEncoderArgs.MapJxlDistance(opts.Quality);
                    sb.Append($" -d {emittedDistance:F1} --lossless_jpeg=0");
                }
            }
            else if (opts.Lossless)
            {
                // 非 JPEG 输入的无损模式：distance=0
                emittedDistance = 0;
                sb.Append(" -d 0");
            }
            else
            {
                // 有损：质量→distance 映射
                emittedDistance = ColorMapping.ImageEncoderArgs.MapJxlDistance(opts.Quality);
                sb.Append($" -d {emittedDistance:F1}");
            }

            if (opts.CjxlProgressive)
                sb.Append(" --progressive");

            // ── 光子噪声：与"数学无损"互斥 ────────────────────────────────────────────────
            // 实测（2026-09-30，891x981 16-bit，同一源同一 effort）：
            //   `-d 0`                 → djxl 回读与源**逐字节相同**（真无损）
            //   `-d 0 --photon_noise_iso=400` → **99.46%** 的 16-bit 样本被改动（平均偏差 30875/65535），
            //                            而 jxlinfo 对两者都报 "(possibly) lossless" —— 噪声是解码端按
            //                            帧头参数合成的，所以体积只多 10 B，尺寸读数完全看不出来。
            // ⇒ 无损出口叠加噪声 = 产物不再是无损，却仍被标成无损 ⇒ 本构造器**不输出**这个组合，
            //   并点名（UI 侧同步把该选项在无损时置灰）。
            bool noiseRequested = opts.CjxlAutoPhotonNoise || opts.CjxlPhotonNoiseIso > 0;
            if (!noiseRequested) { /* 用户没要噪声 */ }
            else if (emittedDistance <= 0)
            {
                log?.Invoke($"[cjxl] photon noise skipped: distance={emittedDistance:F1} is mathematically"
                          + " lossless, and adding synthesized noise would break bit-exactness"
                          + " while jxlinfo still reports 'lossless'\n");
            }
            else if (opts.CjxlPhotonNoiseIso > 0)
                sb.Append($" --photon_noise_iso={opts.CjxlPhotonNoiseIso}");
            else if (forPreview)
            {
                // 自动模式：实际 ISO 由 QueueProcessor 在处理前从 EXIF 读取并写回
                // CjxlPhotonNoiseIso；此处（仅预览）尚未解析时显示 auto 占位
                sb.Append(" --photon_noise_iso=auto");
            }
            else
            {
                log?.Invoke("[cjxl] --photon_noise_iso dropped: auto mode was requested but the ISO value"
                          + " was never resolved from EXIF. cjxl has no 'auto' value for this flag"
                          + " (its parser requires a float), so emitting the placeholder would make the"
                          + " encoder exit 1 with no output\n");
            }

            // ── 色彩空间映射：将 FFmpeg 色彩参数翻译为 cjxl -x color_space ──
            // 注意：isPipe=true 时仍需设置色彩空间，因为 PPM 管道不携带任何色彩元数据。
            // ⚠ **本行原写"-x color_space= 是输出容器标签，与输入格式无关"—— 实测是假的**（#44，
            //   `tests/output/t44/cjxl_x_probe2.log`）：cjxl 的准确规则是「**输入自带色彩元数据（ICC/CICP）
            //   时以输入为准、`-x` 被静默忽略；输入不带时才生效**」——
            //   · HDR PNG：不给 `-x` / 给 DisplayP3 / 给 sRGB ⇒ **同一份 16399 B / A9DB6D0E82285B1E**；
            //   · 同源转 PPM 后 ⇒ 8051 / 8492 / 8053 B 三份不同，jxlinfo 分别报 sRGB / P3 primaries；
            //   · 无内嵌 ICC 的 SDR PNG ⇒ `-x` 生效（8061 vs 8433 B）⇒ 决定因素不是"容器 vs 裸流"。
            // ⚠ 且 `-x` **只写声明、不重映射像素**（`p3.png` 带 `ProfileDescription: DisplayP3`、
            //   `n1.png` 无 ICC，而两者像素差只落在有损编码噪声档 27–30 dB）⇒ 想靠"改走管道让 `-x` 生效"
            //   来兑现用户目标会造出"声称 P3、像素仍是原域"的产物：**必须先把像素转过去**。
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
