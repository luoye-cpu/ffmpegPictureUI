using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    // ═══════════════════════════════════════════════════════════
    // GPU 硬件编码器可用性状态
    // ═══════════════════════════════════════════════════════════
    public enum GpuEncoderAvailability
    {
        Unknown = 0,             // 未检测
        NotCompiled,             // ffmpeg 未编译此编码器
        CompiledNoDevice,        // 编译了但无对应 GPU 硬件
        DeviceFoundUntested,     // 有 GPU 但未运行时验证
        Verified,                // 运行时验证通过 ✅
        Failed                   // 运行时验证失败 ❌
    }

    // ═══════════════════════════════════════════════════════════
    // 单个 GPU 硬件设备信息
    // ═══════════════════════════════════════════════════════════
    public class GpuDeviceInfo
    {
        public string DeviceType { get; set; } = "";     // d3d11va / cuda / qsv / vulkan / amf / vaapi
        public string Description { get; set; } = "";     // 设备描述
        public bool IsAvailable { get; set; }
        /// <summary>当前操作系统是否适用；false = 不探测，也不参与 HasAnyGpu。</summary>
        public bool IsApplicable { get; set; } = true;
        /// <summary>不可用/不适用的原因（用于日志，不把失败伪装成“未检测到”）。</summary>
        public string? StatusReason { get; set; }
    }

    // ═══════════════════════════════════════════════════════════
    // 单个 GPU 编码器检测结果
    // ═══════════════════════════════════════════════════════════
    public class GpuEncoderStatus
    {
        public string EncoderName { get; set; } = "";
        public string FriendlyName { get; set; } = "";          // 如 "Intel QuickSync (mjpeg_qsv)"
        public GpuEncoderAvailability Availability { get; set; }
        public string? WarningMessage { get; set; }
        public bool IsUsable => Availability == GpuEncoderAvailability.Verified;
    }

    // ═══════════════════════════════════════════════════════════
    // GPU 能力完整检测报告
    // ═══════════════════════════════════════════════════════════
    public class GpuCapabilityReport
    {
        public List<GpuDeviceInfo> Devices { get; set; } = new();
        public Dictionary<string, GpuEncoderStatus> Encoders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>编译进的 hwaccel 方法清单（<c>-hwaccels</c> 输出）。⚠ 仅信息展示：编译项与本机硬件存在与否无关，不参与设备判定（2026-11-04 P3）。</summary>
        public List<string> CompiledHwaccels { get; set; } = new();
        public DateTime DetectedAt { get; set; }
        /// <summary>只统计“当前 OS 适用且设备初始化通过”的条目；VA-API(Linux) 在 Windows 上不得把它抬成 true。</summary>
        public bool HasAnyGpu => Devices.Any(d => d.IsApplicable && d.IsAvailable);
        public int VerifiedCount => Encoders.Values.Count(e => e.Availability == GpuEncoderAvailability.Verified);
        /// <summary>尚未做运行时验证的编码器数。旧名 AvailableCount 数的是 DeviceFoundUntested，语义会误导。</summary>
        public int UntestedCount => Encoders.Values.Count(e => e.Availability == GpuEncoderAvailability.DeviceFoundUntested);
        /// <summary>真正可用的编码器数（仅 Verified）。保留该属性给报告/UI 使用。</summary>
        public int AvailableCount => Encoders.Values.Count(e => e.IsUsable);
    }

    // ═══════════════════════════════════════════════════════════
    // GPU 硬件编码能力检测服务
    // ═══════════════════════════════════════════════════════════
    public static class GpuCapabilityService
    {
        // ⚠ 2026-11-04 检测一致性批（P3）：报告按**被测的那件 ffmpeg**分档缓存。旧写法全局单份，
        //   且 ffmpeg 路径无效时把**空报告**也缓存进去（首测若早于路径回填 ⇒ GPU 状态恒 Unknown，
        //   直到手动重测）。换 ffmpeg 依赖 GUI 手动 ClearCache，CLI 与首启时序没有。
        private static readonly Dictionary<string, GpuCapabilityReport> _reportsByFfmpeg =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        private static string KeyFor(string? ffmpegPath)
        {
            var p = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;
            return p ?? "";
        }

        /// <summary>已缓存的检测结果（按当前 ffmpeg 取；null = 尚未检测）</summary>
        public static GpuCapabilityReport? CachedReport
        {
            get
            {
                lock (_lock) return _reportsByFfmpeg.TryGetValue(KeyFor(null), out var r) ? r : null;
            }
        }

        /// <summary>是否有任何 GPU 硬件加速设备</summary>
        public static bool HasAnyGpu => CachedReport?.HasAnyGpu ?? false;

        /// <summary>Intel QuickSync 是否可用</summary>
        public static bool HasIntelQsv => GetEncoderStatus("mjpeg_qsv")?.IsUsable == true;

        /// <summary>NVIDIA NVENC 是否可用</summary>
        public static bool HasNvidiaNvenc => GetEncoderStatus("mjpeg_nvenc")?.IsUsable == true;

        /// <summary>AMD AMF 是否可用</summary>
        public static bool HasAmdAmf => GetEncoderStatus("mjpeg_amf")?.IsUsable == true;

        /// <summary>获取指定编码器的检测状态（按被测 ffmpeg 取报告；2026-11-04 P3 加 ffmpegPath 形参）</summary>
        public static GpuEncoderStatus? GetEncoderStatus(string encoderName, string? ffmpegPath = null)
        {
            GpuCapabilityReport? report;
            lock (_lock) _reportsByFfmpeg.TryGetValue(KeyFor(ffmpegPath), out report);
            if (report?.Encoders == null) return null;
            return report.Encoders.TryGetValue(encoderName, out var status) ? status : null;
        }

        /// <summary>指定 GPU 编码器是否实际可用（编译 + 硬件存在 + 运行时验证通过）</summary>
        public static bool IsGpuEncoderUsable(string encoderName)
        {
            return GetEncoderStatus(encoderName)?.IsUsable ?? false;
        }

        // ═══════════════════════════════════════════════════════
        // 完整检测入口
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 运行完整 GPU 能力检测（后台线程安全，结果缓存）
        /// </summary>
        public static async Task<GpuCapabilityReport> DetectAsync(string? ffmpegPath = null)
        {
            var key = KeyFor(ffmpegPath);
            lock (_lock)
            {
                if (_reportsByFfmpeg.TryGetValue(key, out var cachedReport))
                    return cachedReport;
            }

            var report = new GpuCapabilityReport { DetectedAt = DateTime.Now };
            var ffmpeg = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;

            if (string.IsNullOrWhiteSpace(ffmpeg) || !System.IO.File.Exists(ffmpeg))
            {
                // ⚠ P3：路径无效时**不缓存**空报告 —— 旧写法把首测（可能早于路径回填）的空报告
                //   钉死在缓存里，GPU 状态从此恒 Unknown。返回未缓存结果，待路径就绪后的下一次
                //   调用重新检测。
                return report;
            }

            // Step 1: 编译进的 hwaccel 方法清单 —— 仅信息展示，不参与"设备存在"判定
            report.CompiledHwaccels = await ListCompiledHwaccelsAsync(ffmpeg);

            // Step 2: 真实设备探测（-init_hw_device 逐类实测）
            report.Devices = await DetectHardwareDevicesAsync(ffmpeg);

            // Step 3: 对照已知 GPU 编码器列表检测编译状态（仅 -encoders 解析，约 1s）
            await DetectGpuEncoderCompileStatusAsync(ffmpeg, report);

            lock (_lock) { _reportsByFfmpeg[key] = report; }
            return report;
        }

        /// <summary>
        /// 延迟运行 GPU 编码器运行时验证（较慢，每个编码器最多 8s/分辨率）。
        /// 返回本次实际验证过的状态（含 Verified/Failed 与失败原因），供 UI 逐条记录。
        /// </summary>
        public static async Task<IReadOnlyList<GpuEncoderStatus>> ValidateEncodersAsync(string? ffmpegPath = null)
        {
            GpuCapabilityReport? report;
            lock (_lock) _reportsByFfmpeg.TryGetValue(KeyFor(ffmpegPath), out report);
            if (report == null) return Array.Empty<GpuEncoderStatus>();

            var ffmpeg = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;
            if (string.IsNullOrWhiteSpace(ffmpeg) || !System.IO.File.Exists(ffmpeg))
                return Array.Empty<GpuEncoderStatus>();

            // 只验证"编译+有设备"但尚未验证的编码器
            var toValidate = report.Encoders.Values
                .Where(e => e.Availability == GpuEncoderAvailability.DeviceFoundUntested)
                .ToList();

            var results = new List<GpuEncoderStatus>(toValidate.Count);
            foreach (var status in toValidate)
            {
                var validation = await QuickEncodeTestAsync(ffmpeg, status.EncoderName);
                status.Availability = validation.Success
                    ? GpuEncoderAvailability.Verified
                    : GpuEncoderAvailability.Failed;
                status.WarningMessage = validation.Success ? null : validation.Detail;
                results.Add(status);
            }
            return results;
        }

        /// <summary>清除缓存，强制下次重新检测</summary>
        public static void ClearCache()
        {
            lock (_lock) { _reportsByFfmpeg.Clear(); }
        }

        // ═══════════════════════════════════════════════════════
        // 内部实现
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 真实设备探测：只认 <c>-init_hw_device</c> 逐类实测（2026-11-04 P3 重构）。
        /// 旧写法先用 <c>-hwaccels</c> 输出预填设备表（编译项≠硬件存在，且该输出没有
        /// "not available" 字样 ⇒ 几乎恒 IsAvailable=true），再经 TryDetectDeviceViaInitAsync
        /// 的"避免重复"守卫**短路掉真正的实测** —— 编译了 cuda 的机器即使没有 N 卡也会显示
        /// 设备可用。现在设备判定只认实测；编译方法清单走 <see cref="ListCompiledHwaccelsAsync"/>。
        /// </summary>
        private static async Task<List<GpuDeviceInfo>> DetectHardwareDevicesAsync(string ffmpegPath)
        {
            var devices = new List<GpuDeviceInfo>();

            // 顺序与驱动栈相关；amf 是独立设备类型，不能再拿通用的 d3d11va 冒充 AMD。
            await TryDetectDeviceViaInitAsync(ffmpegPath, "d3d11va", devices);
            await TryDetectDeviceViaInitAsync(ffmpegPath, "cuda", devices);
            await TryDetectDeviceViaInitAsync(ffmpegPath, "qsv", devices);
            await TryDetectDeviceViaInitAsync(ffmpegPath, "vulkan", devices);
            await TryDetectDeviceViaInitAsync(ffmpegPath, "amf", devices);
            await TryDetectDeviceViaInitAsync(ffmpegPath, "vaapi", devices);

            return devices;
        }

        /// <summary>
        /// 设备类型在当前 OS 是否适用。不适用不是“探测失败”，更不是“可用”。
        /// VA-API 是 Linux 接口；D3D11VA/AMF 是 Windows 接口。
        /// </summary>
        private static bool IsApplicableOnCurrentOs(string deviceType)
        {
            var type = deviceType.ToLowerInvariant();
            if (OperatingSystem.IsWindows())
                return type is "d3d11va" or "cuda" or "qsv" or "vulkan" or "amf";
            if (OperatingSystem.IsLinux())
                return type is "cuda" or "qsv" or "vulkan" or "vaapi";
            // macOS 走 VideoToolbox，不在当前探测集合内；其余一律不适用。
            return false;
        }

        /// <summary>编译进的 hwaccel 方法清单（仅展示；<c>-hwaccels</c> 输出与硬件存在与否无关）。</summary>
        private static async Task<List<string>> ListCompiledHwaccelsAsync(string ffmpegPath)
        {
            var methods = new List<string>();
            try
            {
                var output = await RunFfmpegAsync(ffmpegPath, "-hide_banner -hwaccels", 8000);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (trimmed.Length == 0 || trimmed.StartsWith("--"))
                            continue;
                        methods.Add(trimmed);
                    }
                }
            }
            catch { /* 非致命，继续 */ }
            return methods;
        }

        /// <summary>通过 -init_hw_device 尝试初始化特定设备类型</summary>
        private static async Task TryDetectDeviceViaInitAsync(
            string ffmpegPath, string deviceType, List<GpuDeviceInfo> devices)
        {
            // 避免重复
            if (devices.Any(d => d.DeviceType.Equals(deviceType, StringComparison.OrdinalIgnoreCase)))
                return;

            var description = GetDeviceFriendlyName(deviceType);
            if (!IsApplicableOnCurrentOs(deviceType))
            {
                devices.Add(new GpuDeviceInfo
                {
                    DeviceType = deviceType.ToLowerInvariant(),
                    Description = description,
                    IsApplicable = false,
                    IsAvailable = false,
                    StatusReason = "当前操作系统不适用"
                });
                return;
            }

            try
            {
                // 用 256x256 避免某些编码器的最小分辨率限制
                var probe = await RunFfmpegProbeAsync(ffmpegPath,
                    $"-hide_banner -init_hw_device {deviceType} -f lavfi -i color=c=black:s=256x256 -vframes 1 -f null -",
                    8000);

                var failure = probe.TimedOut
                    ? "探测超时"
                    : probe.ExitCode != 0
                        ? $"退出码 {probe.ExitCode}"
                        : DeviceFailureReason(probe.StdErr);

                devices.Add(new GpuDeviceInfo
                {
                    DeviceType = deviceType.ToLowerInvariant(),
                    Description = description,
                    IsAvailable = failure == null,
                    StatusReason = failure
                });
            }
            catch (Exception ex)
            {
                devices.Add(new GpuDeviceInfo
                {
                    DeviceType = deviceType.ToLowerInvariant(),
                    Description = description,
                    IsAvailable = false,
                    StatusReason = $"探测异常 {ex.GetType().Name}: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// 即使进程退出码为 0，也把明确的设备创建失败文本当作失败。
        /// 这里是“设备初始化”，不是编码器能力验证；文案不要让用户把它读成编码可用。
        /// </summary>
        private static string? DeviceFailureReason(string stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr)) return null;
            var lower = stderr.ToLowerInvariant();
            var markers = new[]
            {
                "device creation failed",
                "failed to create",
                "failed to initializ",
                "cannot open",
                "can't open",
                "no device",
                "error initializing",
                "impossible to convert",
                "cannot allocate memory",
                "failed to set value",
                "error parsing global options",
                "not available",
                "unsupported device",
                "cannot load",
                "no va display",
                "failed to open"
            };
            var hit = markers.FirstOrDefault(m => lower.Contains(m, StringComparison.Ordinal));
            return hit == null ? null : $"设备初始化失败（匹配 {hit}）";
        }

        /// <summary>检测所有已知 GPU 编码器的编译状态和硬件设备匹配（不运行耗时验证）</summary>
        private static async Task DetectGpuEncoderCompileStatusAsync(string ffmpegPath, GpuCapabilityReport report)
        {
            // 所有需要检测的 GPU 编码器及其友好名称
            var gpuEncoderDefs = new (string name, string friendly, string requiredDevice)[]
            {
                // MJPEG 系列
                ("mjpeg_qsv",   "Intel QuickSync MJPEG",   "qsv"),
                ("mjpeg_nvenc", "NVIDIA NVENC MJPEG",       "cuda"),
                ("mjpeg_amf",   "AMD AMF MJPEG",            "amf"),
                ("mjpeg_vaapi", "VAAPI MJPEG",              "vaapi"),
                // AV1 系列
                ("av1_qsv",     "Intel QuickSync AV1",      "qsv"),
                ("av1_nvenc",   "NVIDIA NVENC AV1",         "cuda"),
                ("av1_amf",     "AMD AMF AV1",              "amf"),
                ("av1_vaapi",   "VAAPI AV1",                "vaapi"),
            };

            // Step 1: 判断每个编码器是否在 ffmpeg 中编译
            var compiledEncoders = await GetCompiledEncodersAsync(ffmpegPath);

            foreach (var (name, friendly, device) in gpuEncoderDefs)
            {
                var status = new GpuEncoderStatus
                {
                    EncoderName = name,
                    FriendlyName = friendly
                };

                if (!compiledEncoders.Contains(name))
                {
                    status.Availability = GpuEncoderAvailability.NotCompiled;
                    status.WarningMessage = $"当前 ffmpeg 未编译 {friendly} 编码器";
                    report.Encoders[name] = status;
                    continue;
                }

                // 检查对应硬件设备是否存在
                var hasDevice = report.Devices.Any(d =>
                    d.IsAvailable && d.DeviceType.Equals(device, StringComparison.OrdinalIgnoreCase));

                if (!hasDevice)
                {
                    status.Availability = GpuEncoderAvailability.CompiledNoDevice;
                    status.WarningMessage = $"ffmpeg 已编译 {friendly}，但当前系统未检测到对应 GPU 硬件";
                    report.Encoders[name] = status;
                    continue;
                }

                // Step 2: 标记为待验证（运行时验证延迟到 ValidateEncodersAsync）——快速启动
                status.Availability = GpuEncoderAvailability.DeviceFoundUntested;
                status.WarningMessage = null;
                report.Encoders[name] = status;
            }
        }

        /// <summary>获取 ffmpeg 已编译的编码器列表（仅名称）</summary>
        private static async Task<HashSet<string>> GetCompiledEncodersAsync(string ffmpegPath)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var output = await RunFfmpegAsync(ffmpegPath, "-hide_banner -encoders", 10000);
                if (string.IsNullOrWhiteSpace(output)) return result;

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.Length < 9) continue;
                    var secondChar = line.Length > 1 ? line[1] : ' ';
                    if (secondChar != 'V') continue; // 仅视频编码器

                    var flags = line[..Math.Min(7, line.Length)];
                    var remaining = line[flags.Length..].TrimStart();
                    var spaceIdx = remaining.IndexOf(' ');
                    if (spaceIdx <= 0) continue;

                    var name = remaining[..spaceIdx].Trim();
                    result.Add(name);
                }
            }
            catch { /* 编码器列表获取失败 */ }
            return result;
        }

        /// <summary>
        /// 快速编码测试：生成 1 帧纯黑图像，使用指定 GPU 编码器编码到 null 输出。
        /// 使用 256×256 分辨率（某些 GPU 编码器如 NVENC 有最小分辨率限制）。
        /// 成功 = 进程退出码 0 + 实际编码出非零帧；不再用 stderr 里出现 "video:" 作为成功证据
        /// （ffmpeg 失败摘要也可能打印 video:0KiB）。
        /// </summary>
        private static async Task<EncodeValidationResult> QuickEncodeTestAsync(string ffmpegPath, string encoderName)
        {
            string? lastDetail = null;
            // 尝试两种分辨率：256×256 和 512×512（对付最小分辨率限制）
            foreach (var resolution in new[] { "256x256", "512x512" })
            {
                try
                {
                    var args = $"-hide_banner -nostats -f lavfi -i color=c=black:s={resolution}:r=1 -vframes 1 " +
                               $"-c:v {encoderName} -f null -";

                    var probe = await RunFfmpegProbeAsync(ffmpegPath, args, 8000);
                    if (probe.TimedOut)
                        return EncodeValidationResult.Fail("运行时验证超时");

                    // 最小分辨率不足：换 512 再试；这不是“编码器不支持”。
                    if (probe.ExitCode != 0 && LooksLikeResolutionIssue(probe.StdErr))
                    {
                        lastDetail = "256x256 低于编码器最小分辨率，已重试 512x512";
                        continue;
                    }

                    if (probe.ExitCode != 0)
                        return EncodeValidationResult.Fail(
                            $"退出码 {probe.ExitCode}；{Tail(probe.StdErr)}");

                    // 退出码 0 也需要看到实际编码帧。失败摘要里的 video:0KiB 不算证据。
                    if (!HasEncodedFrame(probe.StdErr))
                        return EncodeValidationResult.Fail(
                            $"退出码 0 但未看到编码帧；{Tail(probe.StdErr)}");

                    return EncodeValidationResult.Ok();
                }
                catch (Exception ex)
                {
                    return EncodeValidationResult.Fail($"{ex.GetType().Name}: {ex.Message}");
                }
            }

            return EncodeValidationResult.Fail(lastDetail ?? "未得到可判定的编码输出");
        }

        private static bool LooksLikeResolutionIssue(string stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr)) return false;
            var lower = stderr.ToLowerInvariant();
            return lower.Contains("frame dimensions are less than the minimum") ||
                   lower.Contains("resolution is unsupported") ||
                   lower.Contains("current resolution is unsupported");
        }

        private static bool HasEncodedFrame(string stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr)) return false;
            // ffmpeg 的成功收尾通常是 "frame=    1 ..."；小图也可能 "video:0KiB"（四舍五入），
            // 因此以 frame 计数为主，video 仅作补充。
            return Regex.IsMatch(stderr, @"frame=\s*[1-9][0-9]*", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(stderr, @"video:\s*[1-9][0-9.]*\s*KiB", RegexOptions.IgnoreCase);
        }

        private static string Tail(string? text, int max = 240)
        {
            if (string.IsNullOrWhiteSpace(text)) return "（无 stderr）";
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var tail = string.Join(" | ", lines.TakeLast(3));
            return tail.Length <= max ? tail : tail[^max..];
        }

        private sealed class EncodeValidationResult
        {
            public bool Success { get; private init; }
            public string? Detail { get; private init; }

            public static EncodeValidationResult Ok() => new() { Success = true };
            public static EncodeValidationResult Fail(string detail) => new() { Success = false, Detail = detail };
        }

        // ═══════════════════════════════════════════════════════
        // 辅助方法
        // ═══════════════════════════════════════════════════════

        private static async Task<string> RunFfmpegAsync(string ffmpegPath, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return "";

                var outputTask = p.StandardOutput.ReadToEndAsync();
                var errorTask = p.StandardError.ReadToEndAsync();
                var exitTask = p.WaitForExitAsync();

                var completed = await Task.WhenAny(
                    Task.WhenAll(outputTask, errorTask, exitTask),
                    Task.Delay(timeoutMs));

                if (completed is Task delayTask && delayTask.IsCompleted)
                {
                    try { p.Kill(); } catch { }
                    return outputTask.IsCompleted ? outputTask.Result : "";
                }

                return (outputTask.IsCompleted ? outputTask.Result : "")
                     + (errorTask.IsCompleted ? errorTask.Result : "");
            }
            catch
            {
                return "";
            }
        }

        private sealed class FfmpegProbeResult
        {
            public string StdErr { get; init; } = "";
            public int ExitCode { get; init; } = -1;
            public bool TimedOut { get; init; }
        }

        /// <summary>
        /// 跑一次 ffmpeg 探测，同时保留退出码与 stderr。设备/编码器判定必须以退出码为必要条件；
        /// 不能只看 stderr 是否出现某个词，否则 vaapi 的 "Device creation failed" 这类文本会漏判。
        /// </summary>
        private static async Task<FfmpegProbeResult> RunFfmpegProbeAsync(string ffmpegPath, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null)
                    return new FfmpegProbeResult { TimedOut = false, ExitCode = -1 };

                var outputTask = p.StandardOutput.ReadToEndAsync();
                var errorTask = p.StandardError.ReadToEndAsync();
                var exitTask = p.WaitForExitAsync();
                var allTask = Task.WhenAll(outputTask, errorTask, exitTask);

                var completed = await Task.WhenAny(allTask, Task.Delay(timeoutMs));
                if (completed != allTask)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    // 给 stderr 读取一个很短的收尾窗口；超时本身已是失败，不在这里无限等。
                    await Task.WhenAny(allTask, Task.Delay(1200));
                    return new FfmpegProbeResult
                    {
                        StdErr = errorTask.IsCompleted ? errorTask.Result : "",
                        ExitCode = p.HasExited ? p.ExitCode : -1,
                        TimedOut = true
                    };
                }

                return new FfmpegProbeResult
                {
                    StdErr = errorTask.Result,
                    ExitCode = p.ExitCode,
                    TimedOut = false
                };
            }
            catch (Exception ex)
            {
                return new FfmpegProbeResult
                {
                    StdErr = $"{ex.GetType().Name}: {ex.Message}",
                    ExitCode = -1,
                    TimedOut = false
                };
            }
        }

        private static string GetDeviceFriendlyName(string deviceType)
        {
            return deviceType.ToLowerInvariant() switch
            {
                "d3d11va" => "Direct3D 11 Video Acceleration",
                "dxva2" => "DirectX Video Acceleration 2",
                "cuda" => "NVIDIA CUDA / NVENC",
                "qsv" => "Intel QuickSync Video",
                "vulkan" => "Vulkan",
                "amf" => "AMD AMF Video Acceleration",
                "vaapi" => "VA-API (Linux)",
                "opencl" => "OpenCL",
                _ => deviceType
            };
        }
    }
}
