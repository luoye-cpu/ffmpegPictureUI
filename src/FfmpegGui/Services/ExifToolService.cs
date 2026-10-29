using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// ExifTool 集成服务：用于选择性剥离 EXIF 元数据（GPS、时间、相机信息等）
    /// 检测逻辑与 CjxlService 一致——在 ffmpeg 同目录、程序同目录、PATH 中查找
    ///
    /// 2026-09-02 Phase 1 优化：新增批量并行读取模式
    /// - 单文件串行调用保持不变（向后兼容）
    /// - 新增 ReadTagsBatchAsync(paths, tags, concurrency)：并发批量读取
    ///   - 将文件切分为并发块，每块复用 단一 exiftool -stay_open 进程
    ///   - 向后兼容：现有单文件串行调用完全不受影响
    /// </summary>
    public static class ExifToolService
    {
        private static string? _detectedPath;
        private static bool _detected;
        /// <summary>本进程内是否**已经试过**一次探测(失败也记一次)。</summary>
        private static bool _attempted;
        /// <summary>把"清缓存 → 5 级扫描 → 发布结果"整体串行化。</summary>
        private static readonly object _gate = new();

        /// <summary>
        /// 复位缓存 ⇒ 允许重新探测。**必须在**"用户改了 exiftool 路径 / 点重新检测 / 换了工具目录"
        /// 的路径上调用,否则 <see cref="Detect"/> 的"一次进程一遍"门控会让重试失效。
        /// </summary>
        public static void ClearCache()
        {
            lock (_gate) { _detected = false; _attempted = false; _detectedPath = null; }
        }

        /// <summary>
        /// 探测 exiftool。⚠ 默认**一个进程只扫一遍**(失败也认):本文件内有 **12 处**
        /// `if (_detectedPath == null) { Detect(); … }` 的兜底(本类 16 个 `if (_detectedPath == null)`
        /// 守卫中,后跟 `Detect(` 的那 12 处;全仓其余 4 处散布在 **四个**兄弟类里 ——
        /// `CjxlService.cs:213` / `CjpegliService.cs:167` / `DjxlService.cs:128` / `JxrService.cs:151`,
        /// 它们是 `throw` 守卫、不重扫)。没有这道门控时,
        /// "机器上真没装 exiftool"的场景会**每次调用**重跑整条 5 级链 —— 含最坏
        /// `ExtendedScanTotalBudgetSec = 60 s` 的扩展搜索 ⇒ 一次元数据操作卡一分钟。
        /// 需要强制重扫的入口请显式传 <paramref name="force"/> = true(或先 <see cref="ClearCache"/>)。
        /// </summary>
        public static void Detect(Action<string>? log = null, bool force = false)
        {
            lock (_gate)
            {
                if (!force && _attempted) return;
                _attempted = true;
                DetectCore(log);
            }
        }

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
        /// 获取已检测的 exiftool 路径（可能为 null）
        /// </summary>
        public static string? DetectedPath
        {
            get
            {
                if (!_detected) Detect();
                return _detectedPath;
            }
        }

        /// <summary>
        /// 通过 exiftool 读取图片的 ISO 感光度，用于 cjxl 的 <c>--photon_noise_iso</c>。
        /// 返回 null 表示无法读取（exiftool 不可用 / 文件无 ISO 元数据 / 解析失败）。
        /// 带 5 秒超时保护，防止 exiftool 挂起阻塞编码队列。
        /// <para>
        /// ⚠⚠ 2026-10-02 修复：原实现**只读** <c>-PhotographicSensitivity</c>（EXIF 2.3 的 0x8833），
        /// 而实测 Sony 机型（含 <c>.ARW</c> 与其导出的 16-bit TIFF、以及 dngtool 生成的中间 TIFF）
        /// **从不写这个标签** —— 三种素材实测该标签全为空、而 <c>-ISO</c> 均返回 400。
        /// 结果是「自动读取 EXIF ISO」功能 100% 失效（用户勾了却拿不到任何噪声，只有一行无人接的日志）。
        /// ⇒ 改为**多标签保序回退**：<c>-f</c> 强制打印缺失标签为 <c>-</c> 以保持行序，
        ///   按 <c>ISO → PhotographicSensitivity → ISOSpeedRatings → SonyISO → ISOSetting</c>
        ///   取第一个可解析的正整数。<c>-ISO</c> 是 exiftool 复合标签，覆盖面最广，放在最前。
        /// </para>
        /// </summary>
        public static async Task<int?> ReadIsoAsync(string imagePath, Action<string>? log = null, CancellationToken ct = default)
        {
            var exe = DetectedPath;
            if (exe == null || string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    // -f：缺标签也打印一行 "-"，保证下面的取数顺序与标签顺序一一对应
                    Arguments = $"-f -s -s -s -ISO -PhotographicSensitivity -ISOSpeedRatings -SonyISO -ISOSetting \"{imagePath}\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                    p.BeginErrorReadLine();   // P2-3：stderr 不排空会在管道缓冲(~4KB)写满时把子进程堵死，我们等 stdout/WaitForExit 就永等（实测可复现）
                // 5 秒超时保护，并与调用方的 ct 链接（#26）：外部取消与超时先到者生效
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
                    await p.WaitForExitAsync(cts.Token);
                    // 多标签保序回退：跳过 exiftool -f 为缺失标签打印的 "-"，取第一个可解析的正整数。
                    // 某些相机 ISO 可能是 "200" 或带小数（"100.5"），取整数部分。
                    foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var line = raw.Trim();
                        if (string.IsNullOrEmpty(line) || line == "-") continue;
                        if (int.TryParse(line.Split('.')[0], out var iso) && iso > 0)
                            return iso;
                    }
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    var msg = $"[exiftool] ISO 探测被取消/超时（5s）⇒ 已终止：{Path.GetFileName(imagePath)}";
                    if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 通过 exiftool 批量读取图片的色彩语义标签（单次进程）。
        /// 返回字典：标签名（含组前缀，如 "EXIF:ColorSpace"）→ 显示值（如 "sRGB"）。
        /// exiftool 不可用 / 读取失败返回空字典。
        /// <para>
        /// ⚠⚠ 2026-10-26（整轮门禁随机红后定位）：单趟 5 s 超时在负载下会抽风，而**超时的后果不是
        /// "少读一个标签"** —— 这张图会按「无 ICC、无标签」处理 ⇒ 上层 `AssumeByConvention` 当 sRGB
        /// ⇒ 源=目标 ⇒ 直通并把 BT.2020/PQ 的图原样标成 sRGB（实测：同一二进制单跑 78/0、整轮
        /// 里两格转红）。⇒ 首趟 5 s（快路径），超时再给一趟 20 s；两趟都超时才按未探测处理并**点名**。
        /// </para>
        /// </summary>
        public static async Task<Dictionary<string, string>> ReadColorTagsAsync(string imagePath, Action<string>? log = null, CancellationToken ct = default)
        {
            for (int attempt = 0; ; attempt++)
            {
                var (tags, timedOut) = await ReadColorTagsOnceAsync(imagePath, ct, attempt == 0 ? 5 : 20)
                    .ConfigureAwait(false);
                if (!timedOut) return tags;
                if (ct.IsCancellationRequested) return tags;
                if (attempt >= 1)
                {
                    // 只在**两趟都失败**时点名一次：单趟超时是重试信号，不是结论（每趟都打一行会让
                    // 「这次连 ICC 一起读、失败后按什么算」这句唯一的判据行被稀释成两条）。
                    var msg = $"[exiftool] 色彩标签+ICC 读取被取消/超时（5s ⇒ 20s 两趟）⇒ ICC 一并失去，按无 ICC 处理：{Path.GetFileName(imagePath)}"
                            + "（源可能被当成未标注 = sRGB 惯例，产出可能错标）";
                    if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
                    return tags;
                }
            }
        }

        private static async Task<(Dictionary<string, string> Tags, bool TimedOut)> ReadColorTagsOnceAsync(
            string imagePath, CancellationToken ct, int timeoutSeconds)
        {
            var result = new Dictionary<string, string>();
            var exe = DetectedPath;
            if (exe == null || string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return (result, false);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    // ⚠ 2026-10-09 性能：合并探测。原先这里只取标签，ICC 另起一次 `-b -icc_profile`
                    //   （`IccProfileService.ExtractIccToTempFile`），而单次 exiftool 实测 **0.170 s**
                    //   （perl 打包版，进程本身就贵）⇒ 每张图白付一趟。`-b` 在 `-json` 下把二进制
                    //   以 `base64:` 前缀放进同一条 JSON（实测键 `ICC_Profile:ICC_Profile`，
                    //   且不带 `-a` 也能取到 `ICC_Profile:ProfileDescription`），故一次调用即可两样都拿到。
                    //   消费者只有 `FfmpegCommandBuilder.ProbeInputColorMetadataCore`（全仓唯一调用点）。
                    Arguments = $"-json -G -ColorSpace -BitsPerSample -ColorType -SRGBRendering -PhotometricInterpretation -ProfileDescription -b -icc_profile \"{imagePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return (result, false);
                    p.BeginErrorReadLine();   // P2-3：stderr 不排空会在管道缓冲(~4KB)写满时把子进程堵死，我们等 stdout/WaitForExit 就永等（实测可复现）
                // 超时保护（两趟：5 s 快路径 ⇒ 20 s 重试，见 ReadColorTagsAsync），并与调用方的 ct 链接（#26）
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                string output;
                try
                {
                    // ⚠️ ConfigureAwait(false): 本方法可能被同步等待 (GetAwaiter().GetResult()),
                    // 若捕获 UI SynchronizationContext 会死锁。2026-08-14 修复。
                    output = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                    await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return (result, true);
                }
                if (string.IsNullOrWhiteSpace(output)) return (result, false);

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    return (result, false);
                foreach (var prop in root[0].EnumerateObject())
                {
                    if (prop.Name == "SourceFile") continue;
                    result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.GetRawText();
                }
            }
            catch { }
            return (result, false);
        }

        /// <summary>
        /// 探测体（五优先级）：① 手动指定路径 → ② PLAN 便携包 → ③ ffmpeg/程序同目录
        /// → ④ 系统扩展搜索 → ⑤ 系统 PATH。
        /// 注意：自动跳过 exiftool(-k).exe（带按键等待的交互版本），若仅找到 (-k) 版本则自动复制为
        ///       exiftool.exe 使用。
        /// ⚠ 本注释原先写"三优先级"而实现是五级 —— 声明与实现不符是这类"逐级兜底"代码的老毛病
        ///   （djxl 的 ⑤ 曾只有一句注释、没有代码），改任何一级都要同步这里。
        /// </summary>
        private static void DetectCore(Action<string>? log)
        {
            _detected = true;
            _detectedPath = null;

            var names = OperatingSystem.IsWindows()
                ? new[] { PlatformServices.Exiftool, "exiftool(-k).exe" }
                : new[] { PlatformServices.Exiftool };

            // ── ① 手动指定路径 ──
            var manual = AppSettingsService.Current.ExifToolPath;
            if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual))
            {
                _detectedPath = ResolveSafeExifToolPath(manual);
                if (_detectedPath != null) return;
            }

            // ── ② PLAN 便携包自动检测 ──
            try
            {
                // 先查标准名 exiftool.exe，再查 (-k) 版
                var planFound = PlatformServices.TryFindInPlanFolder(PlatformServices.Exiftool);
                if (planFound == null)
                    planFound = PlatformServices.TryFindInPlanFolder("exiftool(-k).exe");
                if (planFound != null) { _detectedPath = ResolveSafeExifToolPath(planFound); if (_detectedPath != null) return; }
            }
            catch { }

            // ── ③ 同目录（ffmpeg 目录 → 程序目录）──
            var dirs = new[]
            {
                AppSettingsService.Current.FfmpegDir ?? "",
                AppDomain.CurrentDomain.BaseDirectory,
            };
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                // 优先查找标准版 exiftool.exe，其次 (-k) 版
                foreach (var name in names)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        _detectedPath = ResolveSafeExifToolPath(candidate);
                        if (_detectedPath != null) return;
                    }
                }
            }

            // ── ④ 扩展搜索路径（Windows: LocalAppData\Programs 等）──
            try
            {
                var extended = ExternalToolsDetector.FindToolInExtendedPaths(
                    PlatformServices.Exiftool, $"*{PlatformServices.Exiftool}*", log);
                if (extended != null) { _detectedPath = ResolveSafeExifToolPath(extended); if (_detectedPath != null) return; }
            }
            catch { }

            // ── ⑤ 系统 PATH ──
            foreach (var name in names)
            {
                if (TryFindInPath(name, out var pathFound, log) && pathFound != null)
                {
                    _detectedPath = ResolveSafeExifToolPath(pathFound);
                    if (_detectedPath != null) return;
                }
            }
        }

        /// <summary>
        /// 将 exiftool(-k).exe 转换为标准 exiftool.exe（复制文件避免按键等待挂起）
        /// </summary>
        private static string? ResolveSafeExifToolPath(string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
                return null;

            var fileName = Path.GetFileName(candidatePath);
            // 标准版直接使用
            if (!fileName.Contains("(-k)"))
                return candidatePath;

            // (-k) 版本：查找或创建同目录下的标准 exiftool.exe
            var dir = Path.GetDirectoryName(candidatePath);
            if (string.IsNullOrEmpty(dir)) return null;
            var safePath = Path.Combine(dir, PlatformServices.Exiftool);

            if (File.Exists(safePath))
                return safePath;

            // 将 (-k) 版本复制为标准版（内容相同，文件名中的 (-k) 触发等待行为）
            try
            {
                File.Copy(candidatePath, safePath, overwrite: false);
                return safePath;
            }
            catch
            {
                // 复制失败则回退到 (-k) 版本（可能会挂起，但至少尝试了）
                return candidatePath;
            }
        }

        /// <summary>在系统 PATH 中查找可执行文件（通过 -ver 验证可用性）</summary>
        private static bool TryFindInPath(string exeName, out string? fullPath, Action<string>? log = null)
        {
            fullPath = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exeName,
                    Arguments = "-ver",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    // ⚠ 先挂读取任务、再等退出（本仓范式，见 `FfmpegCommandBuilder.ProbeWithFfprobe`）：
                    //   旧写法同步读在 `WaitForExit` 之前、且超时后不 Kill。
                    var pOut = p.StandardOutput.ReadToEndAsync();
                    var pErr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        var msg = $"[exiftool] -ver 探测超时（5s）⇒ 已终止：{exeName}（未解析到路径）";
                        if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
                        return false;
                    }
                    pOut.GetAwaiter().GetResult();
                    pErr.GetAwaiter().GetResult();
                    if (p.ExitCode == 0)
                    {
                        // 用 where/which 解析完整路径
                        var whichPsi = new ProcessStartInfo
                        {
                            FileName = OperatingSystem.IsWindows() ? "where" : "which",
                            Arguments = exeName,
                            RedirectStandardOutput = true,
                            StandardOutputEncoding = Encoding.UTF8,
                            RedirectStandardError = true,
                            StandardErrorEncoding = Encoding.UTF8,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var wp = Process.Start(whichPsi);
                        if (wp != null)
                        {
                            var wpOut = wp.StandardOutput.ReadToEndAsync();
                            var wpErr = wp.StandardError.ReadToEndAsync();
                            if (!wp.WaitForExit(5000))
                            {
                                try { wp.Kill(entireProcessTree: true); } catch { }
                                var msg = $"[exiftool] where/which 解析超时（5s）⇒ 已终止：{exeName}（未解析到路径）";
                                if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
                                return false;
                            }
                            var output = wpOut.GetAwaiter().GetResult().Trim();
                            wpErr.GetAwaiter().GetResult();
                            if (!string.IsNullOrWhiteSpace(output))
                            {
                                var firstLine = output.Split(new[] { '\r', '\n' },
                                    StringSplitOptions.RemoveEmptyEntries)[0];
                                if (File.Exists(firstLine))
                                {
                                    fullPath = firstLine;
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 根据选项构建 exiftool 参数
        /// </summary>
        public static string BuildArguments(string filePath, Models.FfmpegOptions options)
        {
            var sb = new StringBuilder();
            sb.Append("-overwrite_original ");

            if (options.StripExifGps)
                sb.Append("-gps:all= ");

            if (options.StripExifTime)
                sb.Append("-time:all= ");

            if (options.StripExifCamera)
            {
                // 清除相机/镜头相关标签（不影响其他 EXIF 如色彩空间、方向等）
                sb.Append("-Make= -Model= ");
                sb.Append("-LensMake= -LensModel= -LensSerialNumber= ");
                sb.Append("-FocalLength= -FocalLengthIn35mmFormat= ");
                sb.Append("-FNumber= -ApertureValue= -MaxApertureValue= ");
                sb.Append("-ExposureTime= -ShutterSpeedValue= ");
                sb.Append("-ISO= -ISOSpeedRatings= ");
                sb.Append("-Flash= -WhiteBalance= ");
                sb.Append("-ExposureProgram= -ExposureMode= -ExposureBiasValue= ");
                sb.Append("-MeteringMode= -SceneCaptureType= ");
                sb.Append("-LightSource= -SensingMethod= ");
                sb.Append("-CameraOwnerName= -BodySerialNumber= ");
            }

            if (options.StripExifAll)
                sb.Append("-exif:all= ");

            if (options.StripXmp)
                sb.Append("-xmp:all= ");

            sb.Append($"\"{filePath}\"");
            return sb.ToString();
        }

        /// <summary>
        /// 对文件执行 exiftool 清理
        /// </summary>
        public static async Task<int> RunAsync(
            string filePath,
            Models.FfmpegOptions options,
            Action<string>? logCallback = null,
            CancellationToken ct = default)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null)
                    throw new InvalidOperationException("exiftool 未检测到，请确保 exiftool.exe 位于 ffmpeg 同目录或系统 PATH 中");
            }

            var args = BuildArguments(filePath, options);
            logCallback?.Invoke($"[exiftool] {args}\n");

            // ⚠ 2026-09-21：本方法是**写**操作（exiftool 会重写整个文件）⇒ 统一走 RunWriteAsync：
            //   先清掉上次被超时杀掉时残留的 `_exiftool_tmp`（否则下次恒报
            //   `Error: Temporary file already exists`），并按文件大小放宽超时。
            //   原先此处内联了一份进程逻辑、硬编码 15 s，实测 276 MB 的 PNG 写一次需约 32 s ⇒ 必然超时。
            return await RunWriteAsync(filePath, args, logCallback, ct);
        }

        /// <summary>
        /// 判断当前选项是否需要调用 exiftool
        /// </summary>
        public static bool NeedsProcessing(Models.FfmpegOptions options)
        {
            return options.StripExifGps ||
                   options.StripExifTime ||
                   options.StripExifCamera ||
                   options.StripExifAll ||
                   options.StripXmp;
        }

        /// <summary>
        /// 将源文件的所有元数据复制到目标文件（保留目标文件的像素数据不变）。
        /// 用于外部工具（cjxl/cjpegli）编码后恢复丢失的元数据。
        /// 命令：exiftool -overwrite_original -TagsFromFile source -all:all target
        /// </summary>
        /// <param name="absorb">非空且 <see cref="CanAbsorbStrip"/> 为真时，把 GPS/XMP 的隐私清理**并入本次复制**
        /// （调用方据此跳过单独的那次隐私清理调用）。</param>
        /// <param name="dropOrientation">
        /// ⚠ 2026-10-01：**默认必须剥掉源的 `Orientation`**。全仓解码段都不写 `-noautorotate`
        /// ⇒ 产物的像素**已被 ffmpeg 旋正**，此时把 `Orientation=8` 抄到产物上 = 让合规查看器
        /// **再旋一次**（实测：修好几何后的 4000×6000 JXL 经本方法恢复元数据后，exiftool 读回
        /// `Orientation : 8` ⇒ 像素对、标签错）。
        /// 唯一的例外是**免解码重封装**（cjxl `--lossless_jpeg=1`，像素原样搬 DCT 系数 ⇒ 标签仍然成立、
        /// 必须保留）⇒ 调用方传 <c>dropOrientation: false</c>，判据取
        /// <see cref="CjxlService.UsesLosslessJpegRewrap"/>（与本判据同源，不许各写一份）。
        /// </param>
        public static async Task<int> CopyMetadataAsync(
            string sourcePath, string targetPath,
            Action<string>? logCallback = null,
            Models.FfmpegOptions? absorb = null,
            bool dropOrientation = true)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            var absorbNow = absorb != null && CanAbsorbStrip(absorb);

            // -all:all 复制所有可复制标签，但 ICC_Profile 等二进制块可能被跳过，
            // 因此显式追加 -ICC_Profile 确保色彩配置文件也被复制
            // ⚠ 2026-10-29 B5：光靠 `-all:all` 会**一项都不落地而退出码仍是 0**（实测：HD Photo 源 →
            //   cjpegli 出的 JPEG，exiftool 报「0 image files updated / 1 image files unchanged」，
            //   产物里 0 个 XMP/EXIF，而日志已打过「复制元数据」）。根因：`-all:all` 按**标签名**逐个映射，
            //   而该源的 XMP 挂在 exiftool 解析不出的命名空间前缀上（读回是 `XMP-prefix0` 而不是 `XMP-dc`）
            //   ⇒ 在 JPEG 侧找不到可写目标 ⇒ 整批跳过。Safe 变体用**显式组选择器**（`-XMP:all` 等）
            //   走整组搬运，同一对源/目标实测能落地 ⇒ 这里补同一组选择器。
            //   两变体的分工不变：本方法**不排除**色彩标签（-ICC_Profile 照旧），因为调用方
            //   （protectColorMetadata=false）要的正是"完整带回，含 ICC"。
            var args = $"-overwrite_original -m -TagsFromFile \"{sourcePath}\" " +
                       $"-all:all -EXIF:all -IPTC:all -XMP:all -MakerNotes:all -GPS:all -ICC_Profile " +
                       $"{BuildAbsorbExclusions(absorbNow ? absorb : null)}" +
                       $"{(dropOrientation ? OrientationExclusion : string.Empty)}\"{targetPath}\"";
            logCallback?.Invoke($"[exiftool] 复制元数据: {Path.GetFileName(sourcePath)} → {Path.GetFileName(targetPath)}\n"
                + (absorbNow ? "[exiftool] （已并入可吸收的隐私清理，省一次 exiftool 启动）\n" : ""));

            var (rc, copyOut) = await RunWriteCaptureAsync(targetPath, args, logCallback);
            ReportCopyOutcome(logCallback, targetPath, ParseUpdatedCount(copyOut));
            if (rc != 0) return rc;

            // ⚠⚠ 2026-10-20：这次补删**原来只有 CopyMetadataSafeAsync 有**，而 DNG 出口
            //   `protectColorMetadata` 为假时走的正是本方法 ⇒ 实测默认档 `-f dng` 的产物里
            //   GPS 与 93 条 MakerNotes 全在（排除式 `--GPS:all` 单独用**不生效**，见下面
            //   的共用实现）⇒ `StripExifGps` 的隐私默认在这条路上被打穿。
            //   两个变体从此共用一份实现，不再有"只补给一边"的形状。
            var delRc = await ApplyAbsorbStripAfterCopyAsync(targetPath, absorbNow ? absorb : null, logCallback);
            return delRc != 0 ? delRc : rc;
        }

        /// <summary>
        /// 复制之后要补的那次**显式删除**（GPS/XMP）—— 两个复制变体共用这一份实现。
        /// <para>为什么排除式不够：2026-10-05 实测同一份产物、同一份源，
        /// `… -GPS:all --GPS:all …`（复制时排除）报「1 image files updated」而 GPS **仍在**；
        /// `-GPS:all=`（就地删除）才真的删掉 ⇒ JXL/`brob` 这类复合容器上两者语义不同。</para>
        /// <para>为什么必须共用：本轮实测到"只补给 Safe 变体"本身就是缺陷 —— DNG 走非 Safe
        /// 那条分支，隐私默认被静默绕过。同一份政策各写一遍必然漂移，收成一处。</para>
        /// </summary>
        private static async Task<int> ApplyAbsorbStripAfterCopyAsync(
            string targetPath, Models.FfmpegOptions? absorb, Action<string>? logCallback)
        {
            if (absorb == null) return 0;
            var del = new StringBuilder("-overwrite_original -m ");
            if (absorb.StripExifGps) del.Append("\"-GPS:all=\" ");
            if (absorb.StripXmp) del.Append("\"-XMP:all=\" ");
            if (del.Length <= "-overwrite_original -m ".Length) return 0;
            del.Append('"').Append(targetPath).Append('"');
            var delRc = await RunWriteAsync(targetPath, del.ToString(), logCallback);
            if (delRc != 0)
            {
                // ⚠ 剥除失败**不得静默**：这是隐私承诺，失败必须点名（本仓"可用性诚实"原则）。
                // ⚠⚠ 写法约束（CJK 门禁余量 0，实测踩过）：**中文必须整条落在走 sink 的那一行里**。
                logCallback?.Invoke($"[exiftool] ⚠️ 隐私剥除（GPS/XMP 显式删除）退出码 {delRc} —— 产物可能仍带这些标签，请当缺陷报告\n");
                return delRc;
            }
            return 0;
        }

        /// <summary>
        /// 隐私清理能否**并入元数据复制**（从而省掉一次 exiftool 启动）。
        /// 实测：exiftool 在本机**单次启动就要 ~850ms**（`-ver` 也要 850ms，用自带 perl 直接跑
        /// `exiftool.pl` 同样 ~850ms ⇒ 慢的是 Perl 启动本身），而元数据复制/隐私清理各只需 ~50-180ms
        /// ⇒ 每项两次调用里约 **1.7s 全是启动开销**（占单项 2.65s 的约 70%）。
        /// 只吸收「标签来源唯一」的两项：**GPS 与 XMP** —— 它们只可能来自源文件的复制
        /// （ffmpeg 主路径带 `-map_metadata -1`，不会往产物写元数据），
        /// 所以「复制时排除」与「先复制再剥掉」**结果一致**。
        /// 其余（`time`/`camera`/`exif:all`）是**成组标签**，逐条排除容易漏 ⇒ 保持原两步路径不动。
        /// </summary>
        public static bool CanAbsorbStrip(Models.FfmpegOptions options)
            => (options.StripExifGps || options.StripXmp)
               && !options.StripExifTime && !options.StripExifCamera && !options.StripExifAll;

        /// <summary>构建「并入复制」用的排除项。<b>必须用 `--TAG` 排除而不是"少写一个 -TAG 包含"</b>：
        /// GPS 标签属于 EXIF 组，`-EXIF:all` 已经把它们带进来了 —— 本仓实测过一次
        /// （只去掉 `-GPS:all` 的写法**完全没有效果**，是元数据隐私门禁的断言 ① 抓出来的）。</summary>
        private static string BuildAbsorbExclusions(Models.FfmpegOptions? absorb)
        {
            if (absorb == null || !CanAbsorbStrip(absorb)) return "";
            var sb = new StringBuilder();
            if (absorb.StripExifGps) sb.Append("--GPS:all ");
            if (absorb.StripXmp) sb.Append("--XMP:all ");
            return sb.ToString();
        }

        /// <summary>
        /// 剥掉源的 <c>Orientation</c>（两条恢复通路共用的一份排除式）。
        /// <para>⚠ 形态必须是**裸名** <c>--Orientation</c>。写成 <c>--Orientation:all</c>（照抄上面
        /// GPS/XMP 的组通配样式）**实测不生效**：同一命令、同一夹具（JPEG 源 <c>Orientation=8</c> →
        /// 无标签 JPEG 产物），<c>--Orientation:all</c> 之后产物仍读回 <c>Orientation=8</c>，
        /// 而 <c>--Orientation</c> 与 <c>--EXIF:Orientation</c> 都能剥掉。
        /// 组通配对**整组**标签（GPS/XMP）有效，对**单个可跨组重名**的标签无效 ⇒ 别"顺手统一"回去。</para>
        /// <para>同理，单横线 <c>-Orientation</c> 是"复制该标签"而不是排除（与本文件 ① 那条 GPS 的
        /// 实测教训同族）。</para>
        /// <para>必须排在 <c>-TagsFromFile</c> / <c>-all:all</c> **之后**（exiftool 按参数顺序求值）。</para>
        /// </summary>
        private const string OrientationExclusion = "--Orientation ";

        /// <summary>
        /// ⚠⚠ PNG 目标必须先清掉**损坏的 `eXIf` 块**，否则 exiftool 会**拒绝写入任何标签**。
        ///
        /// <para><b>根因（实测 2026-09-21）</b>：ffmpeg 的 PNG 复用器写出的 `eXIf` 块里，
        /// IFD0 **原样照搬了 TIFF 专用的 `StripOffsets` / `StripByteCounts`** —— 而 PNG 里根本没有
        /// 「strip 数据」这个概念 ⇒ exiftool 读取该块时报
        /// <c>Error: Error reading StripOffsets data in IFD0</c> 并**以退出码 1 放弃写入**
        /// （`-m` 不生效：这是致命错误，不是 minor warning）。
        /// ⇒ **PNG 输出的元数据恢复曾经恒不生效**（日志只留一句「警告: 退出码 1」）。</para>
        ///
        /// <para><b>为什么是 `-EXIF:all=`（只清 EXIF 组）而不是 `-all=`</b>：
        /// PNG 的色彩靠 `iCCP` / `cICP` / `cHRM` / `gAMA` 这些**独立 chunk**承载，
        /// `-all=` 会连它们一起删掉。实测 `-EXIF:all=` 只重写 `eXIf`，其余 chunk 全部幸存。</para>
        ///
        /// <para>⚠ `-StripOffsets=`（只删该标签）**不可行**：exiftool 报
        /// <c>Sorry, StripOffsets is not writable</c> ⇒ 删不掉，读取依旧失败。</para>
        /// </summary>
        private static string PngExifResetArg(string targetPath)
            => targetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "-EXIF:all= " : "";

        /// <summary>
        /// **写**元数据的超时秒数：按目标文件大小放宽。
        /// <para>依据（实测 2026-09-21）：exiftool 写元数据 = **重写整个文件**（临时文件 + rename），
        /// 实测 **276 MB 的 PNG 约需 32 s** ⇒ 原先固定 15 s **必然超时**，
        /// 且被杀的进程会**留下 `&lt;目标&gt;_exiftool_tmp`**（污染输出目录 + 让后续调用恒失败）。</para>
        /// <para>取「15 s + 每 MB 0.25 s」，上限 600 s。⚠ 这是**放宽**而非取消保护：真挂死仍会被杀掉。</para>
        /// <para>只读探测（读标签）不要用它 —— 用默认 15 s 即可。</para>
        /// </summary>
        private static int WriteTimeoutSecondsFor(string targetPath)
        {
            try
            {
                var mb = new FileInfo(targetPath).Length / (1024.0 * 1024.0);
                return (int)Math.Clamp(15 + mb * 0.25, 15, 600);
            }
            catch { return 15; }
        }

        /// <summary>
        /// 清掉上一次被超时杀掉时残留的 `&lt;目标&gt;_exiftool_tmp`。
        /// ⚠ 不清掉的话，后续每次调用都会报 `Error: Temporary file already exists` 并**恒失败**
        /// （实测：RAW→PNG 的补漏即因此失败，见 `RAW_PIPELINE_AUDIT.md §10`）。
        /// </summary>
        private static void CleanStaleExifToolTemp(string targetPath)
        {
            try
            {
                var tmp = targetPath + "_exiftool_tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { /* 清理失败不阻断：让 exiftool 自己报错更可诊断 */ }
        }

        /// <summary>
        /// **写**元数据的统一出口：先清掉残留的 `_exiftool_tmp`，再按目标文件大小放宽超时。
        /// 所有会改动目标文件的 exiftool 调用都应走这里（`CopyMetadata*` / `CopyIccProfile*` / `RunAsync`）。
        /// </summary>
        private static Task<int> RunWriteAsync(string targetPath, string args, Action<string>? logCallback = null,
            CancellationToken ct = default)
        {
            CleanStaleExifToolTemp(targetPath);
            return RunRawAsync(args, logCallback, ct, WriteTimeoutSecondsFor(targetPath));
        }

        /// <summary>
        /// <see cref="RunWriteAsync"/> 的**带 stdout** 版本：exiftool 的写结果只写在它的总结行里
        /// （容器不收该标签时退出码照样 0，只打「0 image files updated / 1 image files unchanged」）。
        /// 两个复制变体要用它才能把"到底落地没有"说出口（2026-10-29 B5）。
        /// </summary>
        private static Task<(int Rc, string Stdout)> RunWriteCaptureAsync(string targetPath, string args,
            Action<string>? logCallback = null, CancellationToken ct = default)
        {
            CleanStaleExifToolTemp(targetPath);
            return RunCaptureAsync(args, logCallback, ct, WriteTimeoutSecondsFor(targetPath));
        }

        /// <summary>落地 0 件时的点名行（宣称"复制元数据"而产物没带上，是本仓反复出现的 F-13 同族形状）。
        /// 计数口径复用既有的 <see cref="ParseUpdatedCount"/>（一份实现，不另写正则）。</summary>
        private static void ReportCopyOutcome(Action<string>? logCallback, string targetPath, int updated)
        {
            if (updated == 0)
                logCallback?.Invoke($"[exiftool] ⚠️ 本次复制 **0 项落地**（exiftool 报「0 image files updated」而退出码 0）"
                    + $" ⇒ 产物 {Path.GetFileName(targetPath)} 未携带源元数据（不是「复制成功」）\n");
        }

        /// <summary>
        /// 安全复制元数据：仅复制 EXIF/IPTC/XMP 等描述性元数据，
        /// 跳过 ICC_Profile、ColorSpace、色彩相关标签，保护编码器写入的色彩元数据。
        /// 命令：exiftool -overwrite_original -TagsFromFile source
        ///       -EXIF:all -IPTC:all -XMP:all -MakerNotes:all -GPS:all
        ///       --ColorSpace --ICC_Profile --ColorSpaceData
        ///       --ColorPrimaries --TransferFunction --ColorMatrix
        ///       target
        /// </summary>
        /// <param name="absorb">非空且 <see cref="CanAbsorbStrip"/> 为真时，把 GPS/XMP 的隐私清理**并入本次复制**
        /// （调用方据此跳过单独的那次隐私清理调用，省一次 ~850ms 的 exiftool 启动）。</param>
        public static async Task<int> CopyMetadataSafeAsync(
            string sourcePath, string targetPath,
            Action<string>? logCallback = null,
            Models.FfmpegOptions? absorb = null,
            bool dropOrientation = true)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            var absorbNow = absorb != null && CanAbsorbStrip(absorb);
            // 仅复制描述性元数据组，排除色彩相关标签和 TIFF 结构标签
            // 注意：每个 --TAG 表示"从复制列表中排除该标签"
            // ⚠ `Orientation` 同属"TIFF 结构/几何"这一族（下面已排 StripOffsets/RowsPerStrip/
            //   PhotometricInterpretation… 那一串）——它此前**被漏掉**，而它恰恰是唯一会
            //   让查看器把已旋正的像素**再转一次**的那一个。理由见 CopyMetadataAsync 的同名参数。
            var args = $"-overwrite_original -m {PngExifResetArg(targetPath)}-TagsFromFile \"{sourcePath}\" " +
                       $"-EXIF:all -IPTC:all -XMP:all -MakerNotes:all -GPS:all " +
                       BuildAbsorbExclusions(absorbNow ? absorb : null) +
                       $"--ColorSpace --ICC_Profile --ColorSpaceData " +
                       $"--ColorPrimaries --TransferFunction --ColorMatrix " +
                       $"--ProfileDescription --ProfileCopyright " +
                       $"--StripOffsets --StripByteCounts --RowsPerStrip " +
                       $"--TileOffsets --TileByteCounts --TileWidth --TileLength " +
                       $"--Compression --Predictor --PhotometricInterpretation " +
                       $"--SamplesPerPixel --BitsPerSample --PlanarConfiguration " +
                       $"{(dropOrientation ? OrientationExclusion : string.Empty)}\"{targetPath}\"";
            logCallback?.Invoke($"[exiftool] 安全复制元数据（已排除色彩标签，保护编码器输出）: {Path.GetFileName(sourcePath)} → {Path.GetFileName(targetPath)}\n"
                + (absorbNow ? "[exiftool] （已并入可吸收的隐私清理，省一次 exiftool 启动）\n" : ""));

            var (rcSafe, copyOutSafe) = await RunWriteCaptureAsync(targetPath, args, logCallback);
            // 宣称与现实必须对上：本变体此前同样只打"要复制"，不看**落了几项**（B5 同一族）
            ReportCopyOutcome(logCallback, targetPath, ParseUpdatedCount(copyOutSafe));
            if (rcSafe != 0) return rcSafe;

            // ⚠⚠ 2026-10-05（P0 隐私泄露修复）：**排除式在 JXL 上不生效，必须再补一次显式删除**。
            //   根因（实测，同一份产物、同一份源）：
            //     · 排除式 `… -EXIF:all -GPS:all --GPS:all …`（本方法用的形状）
            //       ⇒ exiftool 报「1 image files updated」**但 GPS 仍在** ❌
            //     · 显式删除式 `-GPS:all=`
            //       ⇒ 报「1 image files updated」且 GPS **消失** ✅
            //   ⇒ 二者语义不同：`--TAG` 是"复制时排除"，而 `-TAG=` 是"就地删除"。
            //     JXL 上（元数据存在 `brob`/EXIF 盒的复合结构里）前者不足以清掉已存在的 GPS。
            //   证据链（2026-10-05 当时）：默认 `-f jxl` 转一张带 GPS 的 JPEG ⇒ 产物**公布坐标**、退出码 0、无告警；
            //     而那时 `StripExifGps` 的**默认值就是 true**（CLI 的 `?? true`）
            //     ⇒ 那是**默认路径上的静默隐私泄露**，不是边角情形。
            //     ⚠ 2026-10-20 政策统一：默认改成 false（不勾选=全保留），剥除只在显式请求时发生 ⇒
            //       本段是当时的证据，别再拿它当现在的默认值。
            //   ⚠ 只在**用户要求剥除**时补删（`absorbNow` 已含该判据；默认值现已是 false=不剥），
            //     `--preserve-metadata`/`--no-strip-gps` 时 `CanAbsorbStrip` 为假 ⇒ 不补删，GPS 正常保留。
            if (absorbNow && absorb != null)
            {
                // 补删与 CopyMetadataAsync 共用一份实现（ApplyAbsorbStripAfterCopyAsync）：
                // 2026-10-20 实测到"只有这条分支有补删"本身就是缺陷（DNG 走另一条 ⇒ 隐私默认被打穿）。
                var stripRc = await ApplyAbsorbStripAfterCopyAsync(targetPath, absorb, logCallback);
                if (stripRc != 0) return stripRc;
            }
            return rcSafe;
        }

        /// <summary>
        /// RAW 输入专用：从**用户实际提供的源 RAW** 补回「中间件丢掉」的拍摄字段。
        ///
        /// <para><b>为什么需要它</b>：RAW 输入（非 DNG 输出）会先经 `dngtool -d -T` 解成中间 TIFF，
        /// 而该步实为 **dcraw 兼容 TIFF 写出**（`Software = dcraw v9.26`）—— 只搬
        /// `Make/Model/ModifyDate/Software` + `ExposureTime/FNumber/ISO/FocalLength`。
        /// 随后产品的元数据恢复以**中间 TIFF** 为源 ⇒ 源 RAW 的
        /// `DateTimeOriginal` / `CreateDate` / `LensModel` / `ExposureProgram` / `MeteringMode` /
        /// `Flash` 等**永久丢失**（实测 5/5 厂商复现；`RAW_PIPELINE_AUDIT.md §10`）。
        /// 本方法在常规恢复**之后**、用**白名单**把这些字段从原 RAW 补回。</para>
        ///
        /// <para>⚠⚠ <b>只用白名单，绝不用 `-all:all`</b> —— 源 RAW 上有三类标签会与产物**冲突**：</para>
        /// <list type="bullet">
        /// <item>`ColorSpace`：RAW 上常为 `Adobe RGB` / `Uncalibrated`，回带会与编码器写入的色彩标签打架</item>
        /// <item>`PixelXDimension` / `PixelYDimension`：RAW 上常是**内嵌预览**的尺寸，与产物实际尺寸不符</item>
        /// <item>`Orientation`：像素**已由解码器旋正**，再回带会**二次旋转**</item>
        /// </list>
        ///
        /// <para>⚠⚠ <b>隐私开关必须在此同样生效</b>：`CanAbsorbStrip` 为真时，调用方会**跳过**
        /// 那一次独立的隐私清理 ⇒ 本方法若不排除 GPS/XMP，等于**把刚剥掉的隐私又装回去**。</para>
        ///
        /// <para>⚠ <b>`Flash` 必须写成 `-EXIF:Flash`</b>：实测不带组限定时，exiftool 会**顺带创建一个 XMP 块**
        /// （6 个 `XMP-exif:Flash*` 标签），**即使显式写了 `--XMP:all` 也拦不住** ⇒ 既是多余产物，
        /// 也是隐私开关的绕过口。加 `-EXIF:` 限定后 XMP 块为 0。</para>
        /// </summary>
        /// <returns>exiftool 退出码；0=成功，-2=按隐私开关跳过（非错误）</returns>
        public static async Task<int> CopyRawCaptureFieldsAsync(
            string rawPath, string targetPath, Models.FfmpegOptions options,
            Action<string>? logCallback = null)
        {
            if (options.StripExifAll)
            {
                logCallback?.Invoke("[exiftool] 已请求删除全部 EXIF ⇒ 跳过 RAW 拍摄字段补回\n");
                return -2;
            }

            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            var args = BuildRawCaptureFieldsArgs(rawPath, targetPath, options);
            logCallback?.Invoke($"[exiftool] 从源 RAW 补回拍摄字段（白名单，不覆盖色彩/尺寸/方向）: "
                + $"{Path.GetFileName(rawPath)} → {Path.GetFileName(targetPath)}\n");

            return await RunWriteAsync(targetPath, args, logCallback);
        }

        /// <summary>
        /// 构建「从源 RAW 补回拍摄字段」的 exiftool 参数。
        ///
        /// <para>⚠⚠ **独立成方法是为了能被探针断言** —— 下面四条约束都是**非显然**的，
        /// 且一旦被"顺手优化"掉就会**静默**出错（产物看起来完全正常，只是字段又丢了 / 隐私又漏了）：</para>
        /// <list type="number">
        /// <item><b>白名单，不用 `-all:all`</b>：源 RAW 的 `ColorSpace`（常为 Adobe RGB）、
        ///   `PixelXDimension/YDimension`（常是**内嵌预览**尺寸）、`Orientation`（像素**已被解码器旋正**）
        ///   回带会与产物冲突 —— 最后一项会导致**二次旋转**。</item>
        /// <item><b>`-EXIF:Flash` 而不是裸 `-Flash`</b>：裸写时 exiftool 会**顺带创建 XMP 块**
        ///   （6 个 `XMP-exif:Flash*` 标签），**即使显式写 `--XMP:all` 也拦不住**
        ///   ⇒ 既是多余产物，也是**隐私开关的绕过口**。</item>
        /// <item><b>PNG 目标必须先 `-EXIF:all=` 复位</b>：ffmpeg 写坏的 `eXIf` 块
        ///   （IFD0 里带 TIFF 专用 `StripOffsets`）会让整条命令以退出码 1 放弃写入。</item>
        /// <item><b>隐私开关必须在此生效</b>：`CanAbsorbStrip` 为真时那次独立隐私清理会被**跳过**
        ///   ⇒ 这里若不排除 GPS/XMP，就等于**把刚剥掉的隐私又装回去**。</item>
        /// </list>
        /// <para>⚠ 可见性为 <c>public</c> 是**刻意的** —— 与同类的 <see cref="BuildArguments"/> 一致：
        /// 探针 `ServiceProbe` 在**另一个程序集**里，`internal` 对它不可见，
        /// 而本仓**禁止改 csproj**（加 `InternalsVisibleTo` 会让 `dotnet build` 一起坏）⇒ 只能 public。</para>
        /// </summary>
        public static string BuildRawCaptureFieldsArgs(
            string rawPath, string targetPath, Models.FfmpegOptions options)
        {
            var sb = new StringBuilder();
            // ── 时间（`--strip-time` 时跳过）──
            if (!options.StripExifTime)
            {
                sb.Append("-DateTimeOriginal -CreateDate -ModifyDate ");
                sb.Append("-SubSecTime -SubSecTimeOriginal -SubSecTimeDigitized ");
                sb.Append("-OffsetTime -OffsetTimeOriginal -OffsetTimeDigitized ");
            }
            // ── 相机 / 镜头（`--strip-camera` 时跳过）──
            if (!options.StripExifCamera)
            {
                // ⚠ `-Make -Model` 是 2026-10-24 补的：ACR 路线（DNG SDK）写出的中间件 TIFF
                //   **一张标签都不带**（LibRaw 臂至少带 Make/Model/DateTime/ExifIFD），若只靠主恢复
                //   （源=中间件）就会把相机厂/型号丢掉。这两个字段正属「相机信息」⇒ 放在本开关下。
                sb.Append("-Make -Model ");
                sb.Append("-LensModel -LensMake -LensSerialNumber -LensInfo -LensSpecification -MaxApertureValue ");
            }
            // ── 曝光 / 测光 / 场景（描述性拍摄参数，不属于「相机信息」开关）──
            sb.Append("-ExposureProgram -MeteringMode -EXIF:Flash -ExposureMode -SceneCaptureType -CustomRendered ");
            sb.Append("-ShutterSpeedValue -ApertureValue -BrightnessValue -ExposureCompensation ");
            sb.Append("-SubjectDistance -LightSource -WhiteBalance -DigitalZoomRatio -FocalLengthIn35mmFormat ");
            sb.Append("-SubjectDistanceRange -SensingMethod -FileSource -SceneType ");
            sb.Append("-FocalPlaneXResolution -FocalPlaneYResolution -FocalPlaneResolutionUnit ");
            // ── 描述性文本 ──
            sb.Append("-Artist -Copyright -ImageDescription -UserComment ");
            sb.Append("-Rating -XPKeywords -XPComment -XPTitle -XPSubject -XPAuthor ");
            // ── 隐私开关：GPS / XMP ──
            if (!options.StripExifGps) sb.Append("-GPS:all ");
            if (!options.StripXmp) sb.Append("-XMP:all ");

            return $"-overwrite_original -m {PngExifResetArg(targetPath)}-TagsFromFile \"{rawPath}\" {sb}\"{targetPath}\"";
        }

        /// <summary>
        /// **把描述性元数据全部剥离**（写操作）。
        ///
        /// <para><b>为什么不能复用 <see cref="RunAsync"/> + <see cref="BuildArguments"/></b>：
        /// `--strip-metadata` 只把 <c>MetadataMode</c> 置成 <c>StripAll</c>，**并不置 <c>StripExifAll</c>**
        /// （见 `CliParser`）⇒ `BuildArguments` 只发出 `-gps:all=`，
        /// 而**不带** <see cref="PngExifResetArg"/> 的 EXIF 复位 ⇒ 在 ffmpeg 写坏的 `eXIf` 块上
        /// 直接以退出码 1 失败（实测：`png --strip-metadata` 产物 GPS 仍在）。</para>
        ///
        /// <para>⚠ **不删 ICC**：PNG 的色彩靠 `iCCP`/`cICP`/`cHRM`/`gAMA` 独立 chunk 承载，
        /// 剥离元数据不应连色彩一起删（与 `FfmpegCommandBuilder` 保 CICP 的意图一致）。</para>
        /// </summary>
        public static async Task<int> StripDescriptiveMetadataAsync(
            string targetPath, Action<string>? logCallback = null, CancellationToken ct = default)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            var args = $"-overwrite_original -m {PngExifResetArg(targetPath)}" +
                       $"-exif:all= -gps:all= -xmp:all= -iptc:all= \"{targetPath}\"";
            logCallback?.Invoke($"[exiftool] {args}\n");
            return await RunWriteAsync(targetPath, args, logCallback, ct);
        }

        /// <summary>仅复制 ICC Profile（安全模式下补偿色彩配置文件）</summary>
        /// <returns>0=写成；<see cref="NoWriteApplied"/>=exiftool 正常退出但**没写进任何文件**
        /// （容器不收该标签，如 AVIF；实测同一命令写 PNG 成功、写 AVIF 报 `0 image files updated` 且 rc=0）；
        /// 其他=exiftool 自己的退出码。</returns>
        public static async Task<int> CopyIccProfileAsync(
            string sourcePath, string targetPath,
            Action<string>? logCallback = null)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            var args = $"-overwrite_original -m -TagsFromFile \"{sourcePath}\" " +
                       $"-ICC_Profile \"{targetPath}\"";
            logCallback?.Invoke($"[exiftool] 恢复 ICC Profile: {Path.GetFileName(sourcePath)} → {Path.GetFileName(targetPath)}\n");

            return await WriteAndVerifyAsync(targetPath, args, logCallback).ConfigureAwait(false);
        }

        /// <summary>
        /// 写一次并通过 exiftool 的总结行确认**真的改了文件**。
        /// ⚠ 只看退出码会把「容器不收该标签」读成成功：exiftool 对 AVIF 的 ICC 写入返回 0，
        ///   实际输出 `0 image files updated / 1 image files unchanged`（2026-10-26 实测）。
        /// </summary>
        private static async Task<int> WriteAndVerifyAsync(string targetPath, string args,
            Action<string>? logCallback, CancellationToken ct = default)
        {
            CleanStaleExifToolTemp(targetPath);
            var (rc, stdout) = await RunCaptureAsync(args, logCallback, ct, WriteTimeoutSecondsFor(targetPath))
                .ConfigureAwait(false);
            if (rc != 0) return rc;
            // 读不到总结行（-w 未输出等）⇒ 按成功处理：此时没有证据说它没写，不能凭猜测判失败。
            if (ParseUpdatedCount(stdout) != 0) return 0;
            logCallback?.Invoke($"[exiftool] ⚠️ 未写入：exiftool 报「0 image files updated」⇒ 标签没有落到 "
                + $"{Path.GetExtension(targetPath).TrimStart('.')} 产物里（该容器不接受后置 ICC，或内容本就相同；"
                + "退出码仍是 0，不能当成功）\n");
            return NoWriteApplied;
        }

        /// <summary>将外部 ICC 文件嵌入到输出图片中（用户指定的 ICC 配置文件）</summary>
        /// <param name="iccFilePath">ICC 配置文件路径 (.icc / .icm)</param>
        /// <param name="targetPath">要嵌入 ICC 的目标图片路径</param>
        /// <param name="logCallback">日志回调</param>
        /// <returns>0=写成；<see cref="NoWriteApplied"/>=正常退出但没写进任何文件（容器不收后置 ICC）；其他=exiftool 退出码</returns>
        public static async Task<int> EmbedIccProfileFromFileAsync(
            string iccFilePath, string targetPath,
            Action<string>? logCallback = null)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            // exiftool 从文件读取 ICC 二进制并写入目标: -icc_profile<=file.icc
            var args = $"-overwrite_original -m " +
                       $"-icc_profile<=\"{iccFilePath}\" " +
                       $"\"{targetPath}\"";
            logCallback?.Invoke($"[exiftool] 嵌入 ICC Profile: {Path.GetFileName(iccFilePath)} → {Path.GetFileName(targetPath)}\n");

            return await WriteAndVerifyAsync(targetPath, args, logCallback).ConfigureAwait(false);
        }

        /// <summary>
        /// 把一张现成的 JPEG 嵌成目标文件的 <b>EXIF IFD1 缩略图</b>（<c>-ThumbnailImage&lt;=file</c>）。
        /// 命令行形状由 <see cref="ThumbnailService.BuildEmbedArgs"/> 单点产出（探针直接对账那份）。
        /// 实测：jpg / png / webp / avif / jxl 写后能回读到图像数据；tiff 会被 exiftool 拒（0 updated）。
        /// </summary>
        public static async Task<int> EmbedThumbnailAsync(
            string thumbPath, string targetPath,
            Action<string>? logCallback = null, CancellationToken ct = default)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            return await RunWriteAsync(targetPath, ThumbnailService.BuildEmbedArgs(thumbPath, targetPath),
                logCallback, ct);
        }

        /// <summary>
        /// 为 JPEG 添加 JFIF 头（提高手机兼容性）。
        /// <para>⚠ `F-11` 的这一半（2026-10-13）：原先**不传日志回调**且 `catch { }` 全吞 ⇒
        /// 这一步失败时一行证据都不留，而队列日志紧接着就打「元数据恢复完成」，
        /// 等于把「没做成」记成「做完了」（本仓忌的静默降级）。本方法**没有**把它并进
        /// 常规恢复那一次 exiftool 调用（那是 `F-11` 的性能账，未做），只把**分步点名**补回来。</para>
        /// </summary>
        /// <param name="logCallback">这一步结论的去处（成功与失败都点名；null = 只回 rc）。</param>
        /// <returns>exiftool 退出码。0=写成；&gt;0=exiftool 自己报错；
        /// <see cref="SkippedNoExiftool"/>=exiftool 不可用而**跳过**（跳过 ≠ 成功，调用方须播报）。</returns>
        public static async Task<int> EnsureJfifHeaderAsync(string targetPath, Action<string>? logCallback = null)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null)
                {
                    logCallback?.Invoke("[exiftool] JFIF 头**未写入**：exiftool 不可用（这一步被跳过，不是成功）");
                    return SkippedNoExiftool;
                }
            }
            try
            {
                var args = $"-overwrite_original -JFIFVersion=1.02 \"{targetPath}\"";
                var rc = await RunWriteAsync(targetPath, args, logCallback);
                logCallback?.Invoke($"[exiftool] JFIF 头写入 rc={rc}" + (rc == 0 ? "" : "（失败：产物保持原样）"));
                return rc;
            }
            catch (Exception ex)
            {
                // ⚠ 不许静默：这一步真出问题时，必须留下"是哪一步、为什么"
                logCallback?.Invoke($"[exiftool] JFIF 头写入异常：{ex.GetType().Name} {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 把索尼 IDC 的**版本时间戳**写进 DNG 的标准 XMP 位（<c>ApplicationNotes</c>=700 ⇒
        /// <c>xmp:CreateDate</c> / <c>xmp:ModifyDate</c>）。
        /// <para>为什么值可以直抄：IDC 的 0xD100/0xD101 存的就是 20 字符
        /// <c>YYYY:MM:DD HH:MM:SS</c>，与 XMP 日期形式同形 ⇒ **零换算**（换算一律不做，
        /// 因为索尼档位整数与别的值域之间没有一手对应材料）。</para>
        /// <para>两个值都由调用方保证是 <see cref="RawService.IsXmpDateForm"/> 通过的字符串；
        /// 这里仍再检一遍 —— 拼命令行的操作数不许有第三种来源。</para>
        /// </summary>
        public static string BuildIdcXmpDatesArgs(string? create, string? modify, string targetPath)
        {
            var sb = new StringBuilder("-overwrite_original ");
            if (create is not null) sb.Append($"-XMP:CreateDate=\"{create}\" ");
            if (modify is not null) sb.Append($"-XMP:ModifyDate=\"{modify}\" ");
            sb.Append($"\"{targetPath}\"");
            return sb.ToString();
        }

        /// <summary><see cref="BuildIdcXmpDatesArgs"/> 的写操作封装；两条都缺 ⇒ 不调用 exiftool（返回 <see cref="SkippedNoExiftool"/> 语义由调用方播报）。</summary>
        public static async Task<int> WriteIdcXmpDatesAsync(
            string targetPath, string? create, string? modify,
            Action<string>? logCallback = null, CancellationToken ct = default)
        {
            if (create is null && modify is null) return 0;
            foreach (var v in new[] { create, modify })
                if (v is not null && !RawService.IsXmpDateForm(v))
                {
                    logCallback?.Invoke("[exiftool] IDC 版本时间戳形状不合 XMP 日期形式 ⇒ 拒绝写入（不拼未校验的操作数）\n");
                    return -2;
                }
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null)
                    return SkippedNoExiftool;
            }
            var args = BuildIdcXmpDatesArgs(create, modify, targetPath);
            logCallback?.Invoke($"[exiftool] {args}\n");
            return await RunWriteAsync(targetPath, args, logCallback, ct);
        }

        /// <summary>
        /// DNG 出口：把**源件里 exiftool 认得的标准 EXIF/GPS 位**从源件抄进产物。
        /// <para>为什么必须存在：Adobe SDK 的 <c>WriteDNG</c> 只发它自己解析得到的那批
        /// （<c>dng_exif::ParseTag</c> 的分支），剩下的一批要么根本没有发射点
        /// （分辨率三件只在 <c>WriteTIFFWithProfile</c> 里写、Interop 整个 IFD 不写、
        /// <c>exif_tag_set</c> 里没有 ComponentsConfiguration/CompressedBitsPerPixel/
        /// FlashPixVersion/ExifImageWidth/ExifImageHeight 的 <c>Add</c>），
        /// 要么源容器不是 TIFF 头（CR3/RAF/MRW ⇒ dngtool 的遍历直接 <c>notTiff</c> 回退）
        /// ⇒ 产物里整片标准位缺席。这些都有 EXIF/GPS 标准位、PS/Camera Raw/exiftool 能正常解码
        /// ⇒ 按 2026-10-19 定的规矩一条不许丢。</para>
        /// <para>取"由公开注册表命名的标签"这一刀由 exiftool 自己完成（名单里只写名字，
        /// 未知/厂商私有 ID 它自己就没有名字 ⇒ 不会被搬）⇒ 私有件仍然一条不进产物。</para>
        /// <para>⚠ **每个名字都带组限定**（<c>-Group:Name&lt;Group:Name</c>），两侧都要：
        /// 目的侧不限定会写错容器（裸 <c>-Flash</c> 顺带创建 XMP 块，见
        /// <see cref="BuildRawCaptureFieldsArgs"/> 的第 2 条约束）；源侧不限定会**取错那一份**——
        /// 实测 CR3 的 <c>-ModifyDate</c> 未限时取 QuickTime 头的 UTC 值（18:21:21+08:00）
        /// 写进 IFD0 的本地时间位，与源 IFD0 自己那条（12:21:21）差 8 小时；
        /// <c>ColorSpace</c>/<c>Contrast</c> 在源件里同时有 [ExifIFD] 与厂商组两份。</para>
        /// <para>⚠ 隐私开关必须在这里同样生效：本方法跑在元数据阶段（含显式删除）**之后**，
        /// 这里不排除 GPS/时间 ⇒ 等于把刚剥掉的隐私又装回去。</para>
        /// <para>不抄的：0x0201/0x0202（预览/缩略图的字节偏移与长度，属"这份文件自己的布局"）、
        /// 0x927C MakerNote（厂商私有件，按政策丢弃）、0x010F/0x0110/0x0131/0x9000
        /// （Make/Model/Software/ExifVersion 由生产者按 DNG 规范自己声明——Adobe 的转换器
        /// 也把 Model 写成去厂商前缀的机型名，PS 的机型库按那个键查找）。</para>
        /// <para>值一律由 exiftool 自己从源件读 ⇒ 本方法不接触任何来自源文件内容的字符串，
        /// 没有拼接注入面。</para>
        /// </summary>
        private static readonly string[] DngCarryIfd0 =
        {
            "ImageDescription", "Artist", "Copyright",
            "XResolution", "YResolution", "ResolutionUnit",
            "XPKeywords", "XPComment", "XPTitle", "XPSubject", "XPAuthor",
        };

        /// <summary>`--strip-time` 时整组跳过。</summary>
        private static readonly string[] DngCarryTime =
        {
            "DateTimeOriginal", "CreateDate",
            "SubSecTime", "SubSecTimeOriginal", "SubSecTimeDigitized",
            "OffsetTime", "OffsetTimeOriginal", "OffsetTimeDigitized",
        };

        /// <summary>`--strip-camera` 时整组跳过。</summary>
        private static readonly string[] DngCarryCamera =
        {
            "LensModel", "LensMake", "LensSerialNumber", "LensInfo", "LensSpecification",
        };

        /// <summary>ExifIFD 里描述拍摄事实的公开位——不属于"相机信息"开关，任何搬运档都抄。</summary>
        private static readonly string[] DngCarryExif =
        {
            // ⚠ APEX 那一族（0x9201/0x9202/0x9203/0x9205）**不在名单里**，是刻意的：
            //   它们由 SDK 从解析像素的同一个 LibRaw 句柄算出 ⇒ 每个容器都有发射点（跨厂商 14 件
            //   实测从未报"缺"），而后置复制要经 exiftool 的 PrintConv 往返，反而把源件那个
            //   精确分数变成连分数近似（实测 Sony IDC：434/256 逐位相同 → 13166/7763，偏 4e-4）。
            //   ⇒ "SDK 已按源解析发出的，后置不许覆盖" 才是 ⑳ 组那条"只补没发射点的"本意。
            "ExposureProgram", "MeteringMode", "Flash", "ExposureMode",
            "SceneCaptureType", "CustomRendered",
            "ExposureCompensation", "SubjectDistance", "LightSource",
            "WhiteBalance", "DigitalZoomRatio", "FocalLengthIn35mmFormat",
            "SubjectDistanceRange", "SensingMethod", "FileSource", "SceneType",
            "FocalPlaneXResolution", "FocalPlaneYResolution", "FocalPlaneResolutionUnit",
            "UserComment", "Rating",
            // 以下由跨厂商跑量点名（2026-10-20，14 件私有 RAW）：SDK 不发、且源件里真的在
            "GainControl", "Contrast", "Saturation", "Sharpness",
            "SensitivityType", "RecommendedExposureIndex", "SerialNumber", "OwnerName",
            "ColorSpace", "ComponentsConfiguration", "CompressedBitsPerPixel",
            "FlashPixVersion", "ExifImageWidth", "ExifImageHeight",
        };

        private static readonly string[] DngCarryInterop = { "InteropIndex", "InteropVersion" };

        private static void AppendCarryNames(StringBuilder sb, string group, string[] names)
        {
            foreach (var n in names)
                sb.Append('-').Append(group).Append(':').Append(n)
                  .Append("<").Append(group).Append(':').Append(n).Append(' ');
        }

        public static string BuildDngStdTagsCarryArgs(
            string sourcePath, string targetPath, Models.FfmpegOptions options)
        {
            var sb = new StringBuilder();
            sb.Append("-overwrite_original -m -TagsFromFile \"").Append(sourcePath).Append("\" ");
            AppendCarryNames(sb, "IFD0", DngCarryIfd0);
            if (!options.StripExifTime)
            {
                AppendCarryNames(sb, "ExifIFD", DngCarryTime);
                AppendCarryNames(sb, "IFD0", new[] { "ModifyDate" });
            }
            if (!options.StripExifCamera) AppendCarryNames(sb, "ExifIFD", DngCarryCamera);
            AppendCarryNames(sb, "ExifIFD", DngCarryExif);
            AppendCarryNames(sb, "InteropIFD", DngCarryInterop);
            // XMP(tag 700)：标准 TIFF 位，PS/ACR 会读（评级、关键词、crs 设置）。
            // dngtool 自己写不了（构建 qDNGUseXMP=0）⇒ 只能在这一步按源件抄。默认全保留，
            // 只有显式 --strip-xmp 才不抄（2026-10-21 补：这条原来整条缺失，DNG→DNG 的 XMP 静默丢）。
            if (!options.StripXmp) sb.Append("-XMP:all ");
            if (!options.StripExifGps) sb.Append("-GPS:all ");
            return sb.Append('\"').Append(targetPath).Append('\"').ToString();
        }

        /// <summary><see cref="BuildDngStdTagsCarryArgs"/> 的写操作封装（exiftool 缺席 ⇒ <see cref="SkippedNoExiftool"/>，与"成功"区分开）。</summary>
        public static async Task<int> CarryStdTagsToDngAsync(
            string sourcePath, string targetPath, Models.FfmpegOptions options,
            Action<string>? logCallback = null, CancellationToken ct = default)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null)
                    return SkippedNoExiftool;
            }
            var args = BuildDngStdTagsCarryArgs(sourcePath, targetPath, options);
            logCallback?.Invoke($"[exiftool] {args}\n");
            return await RunWriteAsync(targetPath, args, logCallback, ct);
        }

        /// <summary>`EnsureJfifHeaderAsync` 在 exiftool 不可用时的返回值（**与"成功"区分开**）。</summary>
        public const int SkippedNoExiftool = int.MinValue;

        /// <summary>读取单个标签值（带 5 秒超时保护，防止 exiftool 挂起阻塞队列）</summary>
        public static async Task<string?> GetTagAsync(string path, string tag, Action<string>? log = null, CancellationToken ct = default)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return null;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _detectedPath,
                    Arguments = $"-{tag} -s -s -s \"{path}\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                    p.BeginErrorReadLine();   // P2-3：stderr 不排空会在管道缓冲(~4KB)写满时把子进程堵死，我们等 stdout/WaitForExit 就永等（实测可复现）
                // 5 秒超时保护，并与调用方的 ct 链接（#26）
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var output = (await p.StandardOutput.ReadToEndAsync(cts.Token)).Trim();
                    await p.WaitForExitAsync(cts.Token);
                    return string.IsNullOrWhiteSpace(output) ? null : output;
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    var msg = $"[exiftool] 标签 {tag} 读取被取消/超时（5s）⇒ 已终止：{Path.GetFileName(path)}";
                    if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
                    return null;
                }
            }
            catch { return null; }
        }

        /// <summary>执行原始 exiftool 命令（内部用，带超时保护防止挂起）</summary>
        /// <param name="timeoutSeconds">
        /// 超时秒数。<b>写操作必须按目标文件大小放宽</b> —— exiftool 写元数据要**重写整个文件**
        /// （临时文件 + rename），实测 **276 MB 的 PNG 需要约 32 s**，固定 15 s 必然超时
        /// 并**留下 `&lt;目标&gt;_exiftool_tmp` 残留**（既污染输出目录，又会让下一次调用报
        /// `Temporary file already exists`）。只读操作（探测标签）用默认值即可。
        /// </param>
        internal static async Task<int> RunRawAsync(string args, Action<string>? logCallback = null,
            CancellationToken ct = default, int timeoutSeconds = 15)
            => (await RunCaptureAsync(args, logCallback, ct, timeoutSeconds).ConfigureAwait(false)).Rc;

        /// <summary>
        /// 与 <see cref="RunRawAsync"/> 同一份进程体，但把 stdout 也带回来。
        /// 用途：exiftool 的**写结果**只能从它的总结行读出来 —— 容器不收该标签时它照样
        /// 退出码 0，只打印「0 image files updated / 1 image files unchanged」
        /// （2026-10-26 实测：AVIF 的 ICC 后置嵌入）。
        /// </summary>
        private static async Task<(int Rc, string Stdout)> RunCaptureAsync(string args,
            Action<string>? logCallback, CancellationToken ct, int timeoutSeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi);
            if (p == null) return (-1, "");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // 超时保护：默认 15 s（只读探测足够）；写操作由调用方按文件大小放宽（见 timeoutSeconds）。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                logCallback?.Invoke($"[exiftool] ⚠️ 超时({timeoutSeconds}s)或被取消，已终止 exiftool 进程\n");
                return (-2, stdout.ToString());
            }

            var stdoutStr = stdout.ToString().Trim();
            var stderrStr = stderr.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(stdoutStr))
                logCallback?.Invoke(stdoutStr + "\n");
            if (!string.IsNullOrWhiteSpace(stderrStr))
                logCallback?.Invoke(stderrStr + "\n");

            return (p.ExitCode, stdoutStr);
        }

        /// <summary>exiftool 正常退出但**没有改动目标文件**（总结行 `0 image files updated`）。</summary>
        public const int NoWriteApplied = -3;

        /// <summary>从 exiftool 总结行取「写入了几个文件」；读不到（非写操作/输出被截断）返回 -1。</summary>
        internal static int ParseUpdatedCount(string stdout)
        {
            var m = System.Text.RegularExpressions.Regex.Match(stdout ?? "",
                @"(\d+)\s+image files? (?:updated|created)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : -1;
        }

        // ──────── 元数据编辑功能 ────────

        /// <summary>元数据分类定义</summary>
        public enum MetadataCategory
        {
            基本信息, 日期时间, 相机信息, 拍摄参数, GPS位置,
            图片属性, IPTC信息, XMP信息, 色彩配置
        }

        /// <summary>元数据字段定义：显示名 → (exiftool标签名, 分类)</summary>
        public static readonly Dictionary<string, (string TagName, MetadataCategory Category)> MetadataFields = new()
        {
            // ── 基本信息 (17) ──
            ["标题"]           = ("Title",            MetadataCategory.基本信息),
            ["描述"]           = ("Description",      MetadataCategory.基本信息),
            ["作者"]           = ("Author",           MetadataCategory.基本信息),
            ["艺术家"]         = ("Artist",           MetadataCategory.基本信息),
            ["版权"]           = ("Copyright",        MetadataCategory.基本信息),
            ["注释"]           = ("Comment",          MetadataCategory.基本信息),
            ["关键词"]         = ("Keywords",         MetadataCategory.基本信息),
            ["评级"]           = ("Rating",           MetadataCategory.基本信息),
            ["文档名称"]       = ("DocumentName",     MetadataCategory.基本信息),
            ["软件"]           = ("Software",         MetadataCategory.基本信息),
            ["主题"]           = ("Subject",          MetadataCategory.基本信息),
            ["说明"]           = ("Instructions",     MetadataCategory.基本信息),
            ["类别"]           = ("Category",         MetadataCategory.基本信息),
            ["子类别"]         = ("SupplementalCategories", MetadataCategory.基本信息),
            ["来源"]           = ("Source",           MetadataCategory.基本信息),
            ["署名"]           = ("Credit",           MetadataCategory.基本信息),
            ["原始文件名"]     = ("OriginalFileName", MetadataCategory.基本信息),

            // ── 日期时间 (6) ──
            ["拍摄日期"]       = ("DateTimeOriginal",  MetadataCategory.日期时间),
            ["创建日期"]       = ("CreateDate",        MetadataCategory.日期时间),
            ["修改日期"]       = ("ModifyDate",        MetadataCategory.日期时间),
            ["数字化日期"]     = ("DigitizationDate",  MetadataCategory.日期时间),
            ["获取日期"]       = ("DateAcquired",      MetadataCategory.日期时间),
            ["GPS 日期"]       = ("GPSDateTime",       MetadataCategory.日期时间),

            // ── 相机信息 (10) ──
            ["相机制造商"]     = ("Make",              MetadataCategory.相机信息),
            ["相机型号"]       = ("Model",             MetadataCategory.相机信息),
            ["相机序列号"]     = ("BodySerialNumber",  MetadataCategory.相机信息),
            ["相机所有者"]     = ("CameraOwnerName",   MetadataCategory.相机信息),
            ["镜头制造商"]     = ("LensMake",          MetadataCategory.相机信息),
            ["镜头型号"]       = ("LensModel",         MetadataCategory.相机信息),
            ["镜头序列号"]     = ("LensSerialNumber",  MetadataCategory.相机信息),
            ["镜头规格"]       = ("LensSpecification", MetadataCategory.相机信息),
            ["镜头ID"]         = ("LensID",            MetadataCategory.相机信息),
            ["镜头信息"]       = ("LensInfo",          MetadataCategory.相机信息),

            // ── 拍摄参数 (12) ──
            ["焦距"]           = ("FocalLength",             MetadataCategory.拍摄参数),
            ["35mm 等效焦距"]  = ("FocalLengthIn35mmFormat", MetadataCategory.拍摄参数),
            ["光圈"]           = ("FNumber",                 MetadataCategory.拍摄参数),
            ["最大光圈"]       = ("MaxApertureValue",        MetadataCategory.拍摄参数),
            ["ISO"]            = ("ISO",                     MetadataCategory.拍摄参数),
            ["曝光时间"]       = ("ExposureTime",            MetadataCategory.拍摄参数),
            ["快门速度"]       = ("ShutterSpeedValue",       MetadataCategory.拍摄参数),
            ["曝光补偿"]       = ("ExposureBiasValue",       MetadataCategory.拍摄参数),
            ["曝光程序"]       = ("ExposureProgram",         MetadataCategory.拍摄参数),
            ["曝光模式"]       = ("ExposureMode",            MetadataCategory.拍摄参数),
            ["测光模式"]       = ("MeteringMode",            MetadataCategory.拍摄参数),
            ["场景类型"]       = ("SceneCaptureType",        MetadataCategory.拍摄参数),
            ["感光方式"]       = ("SensingMethod",           MetadataCategory.拍摄参数),
            ["闪光灯"]         = ("Flash",                   MetadataCategory.拍摄参数),
            ["白平衡"]         = ("WhiteBalance",            MetadataCategory.拍摄参数),
            ["光源"]           = ("LightSource",             MetadataCategory.拍摄参数),
            ["对比度"]         = ("Contrast",                MetadataCategory.拍摄参数),
            ["饱和度"]         = ("Saturation",              MetadataCategory.拍摄参数),
            ["锐度"]           = ("Sharpness",               MetadataCategory.拍摄参数),

            // ── GPS 位置 (8) ──
            ["GPS 纬度"]       = ("GPSLatitude",       MetadataCategory.GPS位置),
            ["GPS 经度"]       = ("GPSLongitude",      MetadataCategory.GPS位置),
            ["GPS 海拔"]       = ("GPSAltitude",       MetadataCategory.GPS位置),
            ["GPS 纬度参考"]   = ("GPSLatitudeRef",    MetadataCategory.GPS位置),
            ["GPS 经度参考"]   = ("GPSLongitudeRef",   MetadataCategory.GPS位置),
            ["GPS 海拔参考"]   = ("GPSAltitudeRef",    MetadataCategory.GPS位置),
            ["GPS 地图基准"]   = ("GPSMapDatum",       MetadataCategory.GPS位置),
            ["GPS 处理方法"]   = ("GPSProcessingMethod",MetadataCategory.GPS位置),

            // ── 图片属性 (9) ──
            ["方向"]           = ("Orientation",       MetadataCategory.图片属性),
            ["图像描述"]       = ("ImageDescription",  MetadataCategory.图片属性),
            ["用户注释"]       = ("UserComment",       MetadataCategory.图片属性),
            ["图像宽度"]       = ("ImageWidth",        MetadataCategory.图片属性),
            ["图像高度"]       = ("ImageHeight",       MetadataCategory.图片属性),
            ["位深"]           = ("BitsPerSample",     MetadataCategory.图片属性),
            ["压缩"]           = ("Compression",       MetadataCategory.图片属性),
            ["分辨率单位"]     = ("ResolutionUnit",    MetadataCategory.图片属性),
            ["X 分辨率"]       = ("XResolution",       MetadataCategory.图片属性),
            ["Y 分辨率"]       = ("YResolution",       MetadataCategory.图片属性),

            // ── IPTC 信息 (10) ──
            ["IPTC 署名"]      = ("Byline",            MetadataCategory.IPTC信息),
            ["IPTC 署名头衔"]  = ("BylineTitle",       MetadataCategory.IPTC信息),
            ["IPTC 标题"]      = ("Headline",          MetadataCategory.IPTC信息),
            ["IPTC 说明"]      = ("Caption",           MetadataCategory.IPTC信息),
            ["IPTC 说明作者"]  = ("CaptionWriter",     MetadataCategory.IPTC信息),
            ["IPTC 特殊说明"]  = ("SpecialInstructions",MetadataCategory.IPTC信息),
            ["IPTC 国家"]      = ("Country",           MetadataCategory.IPTC信息),
            ["IPTC 省/州"]     = ("ProvinceState",     MetadataCategory.IPTC信息),
            ["IPTC 城市"]      = ("City",              MetadataCategory.IPTC信息),
            ["IPTC 地点"]      = ("Location",          MetadataCategory.IPTC信息),
            ["IPTC 版权声明"]  = ("CopyrightNotice",   MetadataCategory.IPTC信息),

            // ── XMP 信息 (8) ──
            ["XMP 创建者"]     = ("Creator",           MetadataCategory.XMP信息),
            ["XMP 创建工具"]   = ("CreatorTool",       MetadataCategory.XMP信息),
            ["XMP 权利"]       = ("Rights",            MetadataCategory.XMP信息),
            ["XMP 网页声明"]   = ("WebStatement",      MetadataCategory.XMP信息),
            ["XMP 标记"]       = ("Label",             MetadataCategory.XMP信息),
            ["XMP 评级"]       = ("Rating",            MetadataCategory.XMP信息),
            ["XMP 标识符"]     = ("Identifier",        MetadataCategory.XMP信息),
            ["XMP 使用条款"]   = ("UsageTerms",        MetadataCategory.XMP信息),

            // ── 色彩配置 (6) ──
            ["色彩空间"]       = ("ColorSpace",        MetadataCategory.色彩配置),
            ["Gamma"]          = ("Gamma",             MetadataCategory.色彩配置),
            ["白点"]           = ("WhitePoint",        MetadataCategory.色彩配置),
            ["原色"]           = ("PrimaryChromaticities", MetadataCategory.色彩配置),
            ["ICC 配置文件"]   = ("ICCProfile",        MetadataCategory.色彩配置),
            ["色彩模式"]       = ("ColorMode",         MetadataCategory.色彩配置),
        };

        /// <summary>安全地将 JsonElement 转为字符串（处理数字/布尔/null 类型）</summary>
        private static string JsonElementToString(System.Text.Json.JsonElement element) => element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString() ?? "",
            System.Text.Json.JsonValueKind.Null => "",
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            _ => element.GetRawText()
        };

        /// <summary>
        /// 读取文件现有元数据（JSON 格式解析），返回 显示名→值 的字典
        /// </summary>
        public static async Task<Dictionary<string, string>> ReadMetadataAsync(string filePath)
        {
            var result = new Dictionary<string, string>();
            foreach (var key in MetadataFields.Keys)
                result[key] = "";

            // 确保已检测 exiftool
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null)
                    throw new InvalidOperationException("exiftool 未检测到，请确保 exiftool.exe 位于 ffmpeg 同目录或系统 PATH 中");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _detectedPath,
                    Arguments = $"-json -G \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null)
                    throw new InvalidOperationException("无法启动 exiftool 进程");

                // 事件驱动读取，避免 stdout/stderr 缓冲区死锁
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();

                var stdoutStr = stdout.ToString().Trim();
                var stderrStr = stderr.ToString().Trim();

                if (p.ExitCode != 0)
                    throw new InvalidOperationException($"exiftool 退出码 {p.ExitCode}: {stderrStr}");

                if (string.IsNullOrWhiteSpace(stdoutStr)) return result;

                using var doc = JsonDocument.Parse(stdoutStr);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    return result;

                var first = root[0];

                foreach (var field in MetadataFields)
                {
                    var tagName = field.Value.TagName;
                    foreach (var prop in first.EnumerateObject())
                    {
                        if (prop.Name.EndsWith($":{tagName}", StringComparison.OrdinalIgnoreCase) ||
                            prop.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            result[field.Key] = JsonElementToString(prop.Value);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"读取元数据失败: {ex.Message}", ex);
            }

            return result;
        }

        /// <summary>
        /// 将元数据写入文件，返回 (退出码, 输出信息)
        /// </summary>
        public static async Task<(int ExitCode, string Output)> WriteMetadataAsync(
            string filePath, Dictionary<string, string> tags, bool keepBackup = true)
        {
            // 确保已检测 exiftool
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null)
                    return (-1, "exiftool 未检测到，请确保 exiftool.exe 位于 ffmpeg 同目录或系统 PATH 中");
            }

            var argList = new List<string>();
            if (!keepBackup)
                argList.Add("-overwrite_original");

            foreach (var (displayName, value) in tags)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (!MetadataFields.TryGetValue(displayName, out var fieldDef))
                    continue;
                argList.Add($"-{fieldDef.TagName}={value}");
            }

            argList.Add($"\"{filePath}\"");

            var args = string.Join(" ", argList);

            try
            {
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

                using var p = Process.Start(psi);
                if (p == null)
                    throw new InvalidOperationException("无法启动 exiftool 进程");

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();

                var output = (stdout.ToString() + "\n" + stderr.ToString()).Trim();
                return (p.ExitCode, output);
            }
            catch (Exception ex)
            {
                return (-1, $"写入元数据异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 批量并行读取多文件的指定标签（Phase 1 优化：exiftool 批量并行化）
        /// - 将文件切分为并发块，每块复用单一 exiftool -stay_open 进程
        /// - 不改变现有单文件串行调用 API（100% 向后兼容）
        /// - 适用场景：队列中大量文件需读取相同标签（如预处理阶段统一读取色彩标签）
        /// </summary>
        /// <param name="filePaths">文件路径列表</param>
        /// <param name="tags">要读取的标签名（exiftool 标签名，如 "ColorSpace", "ColorPrimaries" 等）</param>
        /// <param name="concurrency">最大并发 exiftool 进程数（默认 Environment.ProcessorCount）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>文件路径 -> (标签名 -> 值) 的结果字典，失败的文件不会包含在结果中</returns>
        public static async Task<ConcurrentDictionary<string, Dictionary<string, string>>> ReadTagsBatchAsync(
            IEnumerable<string> filePaths,
            IEnumerable<string> tags,
            int concurrency = 0,
            CancellationToken ct = default)
        {
            var paths = filePaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
            var tagList = tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var results = new ConcurrentDictionary<string, Dictionary<string, string>>();

            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null)
                    return results; // exiftool 不可用时静默返回空结果（不抛异常，保证调用方不崩溃）
            }

            if (paths.Count == 0 || tagList.Count == 0)
                return results;

            // 并发度自动限制
            if (concurrency <= 0) concurrency = Environment.ProcessorCount;
            concurrency = Math.Max(1, Math.Min(concurrency, Environment.ProcessorCount * 2));

            // 将文件切分为并发块
            var chunks = paths.Chunk((paths.Count + concurrency - 1) / concurrency).ToList();

            var tasks = chunks.Select(async chunk =>
            {
                if (ct.IsCancellationRequested) return;
                await ProcessChunkAsync(chunk, tagList, results, ct);
            }).ToList();

            await Task.WhenAll(tasks);
            return results;
        }

        /// <summary>
        /// 处理单个并发块：启动单一 exiftool -stay_open 进程，批量推入文件路径，按 -execute 输出结果
        /// </summary>
        private static async Task ProcessChunkAsync(
            string[] chunk,
            List<string> tags,
            ConcurrentDictionary<string, Dictionary<string, string>> results,
            CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            if (chunk.Length == 0) return;

            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath!,
                Arguments = $"-stay_open True -q -q -q " + string.Join(" ", tags.Select(t => $"-{t}")) + " -json",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi);
            if (p == null) return;
                p.BeginErrorReadLine();   // P2-3：stderr 不排空会在管道缓冲(~4KB)写满时把子进程堵死，我们等 stdout/WaitForExit 就永等（实测可复现）

            CancellationTokenSource? cts = null;
            try
            {
                // 逐个文件写入 stdin，每个文件后发送 -execute
                var stdinWriter = p.StandardInput;
                var stdoutReader = p.StandardOutput;
                cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30)); // 单个 chunk 最多 30 秒

                // 启动输出读取任务（逐行读 JSON）
                var outputTask = Task.Run(async () =>
                {
                    var buffer = new StringBuilder();
                    // 不用 stdoutReader.EndOfStream（同步阻塞读取，CA2024）；改由 ReadLineAsync 返回 null 判定 EOF
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await stdoutReader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        if (line.Trim() == "{ready}") continue; // exiftool 就绪提示
                        if (line.Trim() == "") continue;

                        buffer.AppendLine(line);
                        // exiftool -json 输出单行 JSON（每个文件一个对象），可直接解析
                        if (line.TrimEnd().EndsWith("}"))
                        {
                            try
                            {
                                var json = buffer.ToString();
                                buffer.Clear();
                                using var doc = JsonDocument.Parse(json);
                                var root = doc.RootElement;

                                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                                {
                                    var first = root[0];
                                    var sourceFile = "";
                                    var dict = new Dictionary<string, string>();

                                    foreach (var prop in first.EnumerateObject())
                                    {
                                        if (prop.Name == "SourceFile")
                                            sourceFile = prop.Value.GetString() ?? "";
                                        else
                                            dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                                                ? prop.Value.GetString() ?? ""
                                                : prop.Value.GetRawText();
                                    }

                                    if (!string.IsNullOrEmpty(sourceFile))
                                        results.TryAdd(sourceFile, dict);
                                }
                            }
                            catch { /* 解析失败忽略该项，继续读下一行 */ }
                        }
                    }
                }, cts.Token);

                // 写入文件路径到 stdin
                foreach (var filePath in chunk)
                {
                    if (ct.IsCancellationRequested) break;
                    await stdinWriter.WriteLineAsync($"\"{filePath}\"").ConfigureAwait(false);
                    await stdinWriter.WriteLineAsync("-execute").ConfigureAwait(false);
                    await stdinWriter.FlushAsync().ConfigureAwait(false);
                }

                // 发送退出指令
                await stdinWriter.WriteLineAsync("-stay_open").ConfigureAwait(false);
                await stdinWriter.WriteLineAsync("False").ConfigureAwait(false);
                await stdinWriter.FlushAsync().ConfigureAwait(false);

                // 等待输出任务完成
                await outputTask.ConfigureAwait(false);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            }
            catch { /* 忽略单个 chunk 失败，不影响其他 chunk */ }
            finally
            {
                cts?.Dispose();
            }
        }
    }
}
