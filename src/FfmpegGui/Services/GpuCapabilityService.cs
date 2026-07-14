using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
        public string DeviceType { get; set; } = "";     // d3d11va / cuda / qsv / vulkan / vaapi
        public string Description { get; set; } = "";     // 设备描述
        public bool IsAvailable { get; set; }
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
        public DateTime DetectedAt { get; set; }
        public bool HasAnyGpu => Devices.Any(d => d.IsAvailable);
        public int VerifiedCount => Encoders.Values.Count(e => e.Availability == GpuEncoderAvailability.Verified);
        public int AvailableCount => Encoders.Values.Count(e => e.Availability == GpuEncoderAvailability.DeviceFoundUntested);
    }

    // ═══════════════════════════════════════════════════════════
    // GPU 硬件编码能力检测服务
    // ═══════════════════════════════════════════════════════════
    public static class GpuCapabilityService
    {
        private static GpuCapabilityReport? _cachedReport;
        private static readonly object _lock = new();

        /// <summary>已缓存的检测结果（null = 尚未检测）</summary>
        public static GpuCapabilityReport? CachedReport
        {
            get { lock (_lock) return _cachedReport; }
        }

        /// <summary>是否有任何 GPU 硬件加速设备</summary>
        public static bool HasAnyGpu => _cachedReport?.HasAnyGpu ?? false;

        /// <summary>Intel QuickSync 是否可用</summary>
        public static bool HasIntelQsv => GetEncoderStatus("mjpeg_qsv")?.IsUsable == true;

        /// <summary>NVIDIA NVENC 是否可用</summary>
        public static bool HasNvidiaNvenc => GetEncoderStatus("mjpeg_nvenc")?.IsUsable == true;

        /// <summary>AMD AMF 是否可用</summary>
        public static bool HasAmdAmf => GetEncoderStatus("mjpeg_amf")?.IsUsable == true;

        /// <summary>获取指定编码器的检测状态</summary>
        public static GpuEncoderStatus? GetEncoderStatus(string encoderName)
        {
            var report = CachedReport;
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
            lock (_lock)
            {
                if (_cachedReport != null)
                    return _cachedReport;
            }

            var report = new GpuCapabilityReport { DetectedAt = DateTime.Now };
            var ffmpeg = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;

            if (string.IsNullOrWhiteSpace(ffmpeg) || !System.IO.File.Exists(ffmpeg))
            {
                lock (_lock) { _cachedReport = report; }
                return report;
            }

            // Step 1: 检测硬件加速设备
            report.Devices = await DetectHardwareDevicesAsync(ffmpeg);

            // Step 2: 对照已知 GPU 编码器列表检测可用性
            await DetectGpuEncodersAsync(ffmpeg, report);

            lock (_lock) { _cachedReport = report; }
            return report;
        }

        /// <summary>清除缓存，强制下次重新检测</summary>
        public static void ClearCache()
        {
            lock (_lock) { _cachedReport = null; }
        }

        // ═══════════════════════════════════════════════════════
        // 内部实现
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 运行 ffmpeg -init_hw_device list 获取可用硬件加速设备
        /// 备用: ffmpeg -hwaccels 列出支持的硬件加速方法
        /// </summary>
        private static async Task<List<GpuDeviceInfo>> DetectHardwareDevicesAsync(string ffmpegPath)
        {
            var devices = new List<GpuDeviceInfo>();

            // 方法1: -hwaccels（最可靠）
            try
            {
                var output = await RunFfmpegAsync(ffmpegPath, "-hide_banner -hwaccels", 8000);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (trimmed.Length == 0 || trimmed.StartsWith("--"))
                            continue;

                        var isAvailable = !trimmed.Contains("not available", StringComparison.OrdinalIgnoreCase);
                        devices.Add(new GpuDeviceInfo
                        {
                            DeviceType = trimmed.ToLowerInvariant(),
                            Description = trimmed,
                            IsAvailable = isAvailable
                        });
                    }
                }
            }
            catch { /* 非致命，继续 */ }

            // 方法2: 补充 -v verbose 初始化测试
            await TryDetectDeviceViaInitAsync(ffmpegPath, "d3d11va", devices);
            await TryDetectDeviceViaInitAsync(ffmpegPath, "cuda", devices);
            await TryDetectDeviceViaInitAsync(ffmpegPath, "qsv", devices);
            await TryDetectDeviceViaInitAsync(ffmpegPath, "vulkan", devices);
            await TryDetectDeviceViaInitAsync(ffmpegPath, "vaapi", devices);

            return devices;
        }

        /// <summary>通过 -init_hw_device 尝试初始化特定设备类型</summary>
        private static async Task TryDetectDeviceViaInitAsync(
            string ffmpegPath, string deviceType, List<GpuDeviceInfo> devices)
        {
            // 避免重复
            if (devices.Any(d => d.DeviceType.Equals(deviceType, StringComparison.OrdinalIgnoreCase)))
                return;

            try
            {
                // 用 256x256 避免某些编码器的最小分辨率限制
                var stderr = await RunFfmpegStderrAsync(ffmpegPath,
                    $"-hide_banner -init_hw_device {deviceType} -f lavfi -i color=c=black:s=256x256 -vframes 1 -f null -",
                    8000);

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    var lower = stderr.ToLowerInvariant();
                    var failed = lower.Contains("cannot open") ||
                                 lower.Contains("failed to create") ||
                                 lower.Contains("no device") ||
                                 lower.Contains("error initializing") ||
                                 lower.Contains("impossible to convert") ||
                                 lower.Contains("dll") && lower.Contains("failed to open");  // AMF DLL 缺失

                    if (!failed)
                    {
                        devices.Add(new GpuDeviceInfo
                        {
                            DeviceType = deviceType.ToLowerInvariant(),
                            Description = GetDeviceFriendlyName(deviceType),
                            IsAvailable = true
                        });
                    }
                    else
                    {
                        devices.Add(new GpuDeviceInfo
                        {
                            DeviceType = deviceType.ToLowerInvariant(),
                            Description = GetDeviceFriendlyName(deviceType),
                            IsAvailable = false
                        });
                    }
                }
            }
            catch
            {
                // 设备检测失败，忽略
            }
        }

        /// <summary>检测所有已知 GPU 编码器的可用性</summary>
        private static async Task DetectGpuEncodersAsync(string ffmpegPath, GpuCapabilityReport report)
        {
            // 所有需要检测的 GPU 编码器及其友好名称
            var gpuEncoderDefs = new (string name, string friendly, string requiredDevice)[]
            {
                // MJPEG 系列
                ("mjpeg_qsv",   "Intel QuickSync MJPEG",   "qsv"),
                ("mjpeg_nvenc", "NVIDIA NVENC MJPEG",       "cuda"),
                ("mjpeg_amf",   "AMD AMF MJPEG",            "d3d11va"),
                ("mjpeg_vaapi", "VAAPI MJPEG",              "vaapi"),
                // AV1 系列
                ("av1_qsv",     "Intel QuickSync AV1",      "qsv"),
                ("av1_nvenc",   "NVIDIA NVENC AV1",         "cuda"),
                ("av1_amf",     "AMD AMF AV1",              "d3d11va"),
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

                // Step 2: 运行时编码验证
                status.Availability = GpuEncoderAvailability.DeviceFoundUntested;
                var verified = await QuickEncodeTestAsync(ffmpegPath, name);

                if (verified)
                {
                    status.Availability = GpuEncoderAvailability.Verified;
                    status.WarningMessage = null;
                }
                else
                {
                    status.Availability = GpuEncoderAvailability.Failed;
                    status.WarningMessage = $"{friendly} 运行时验证失败，可能 GPU 驱动不兼容或硬件不支持此编码格式";
                }

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
        /// 成功 = GPU 编码器实际可用。
        /// </summary>
        private static async Task<bool> QuickEncodeTestAsync(string ffmpegPath, string encoderName)
        {
            // 尝试两种分辨率：256×256 和 512×512（对付最小分辨率限制）
            foreach (var resolution in new[] { "256x256", "512x512" })
            {
                try
                {
                    var args = $"-hide_banner -nostats -f lavfi -i color=c=black:s={resolution}:r=1 -vframes 1 " +
                               $"-c:v {encoderName} -f null -";

                    var stderr = await RunFfmpegStderrAsync(ffmpegPath, args, 8000);

                    if (string.IsNullOrWhiteSpace(stderr))
                        continue;

                    var lower = stderr.ToLowerInvariant();

                    // 检查"最小分辨率不足"：如果是这个原因，用更大的分辨率重试
                    if (lower.Contains("frame dimensions are less than the minimum") ||
                        lower.Contains("resolution is unsupported") ||
                        lower.Contains("current resolution is unsupported"))
                    {
                        continue; // 尝试更大的分辨率
                    }

                    // 硬失败标志 — 不重试
                    var hardFail = lower.Contains("cannot open") ||
                                   lower.Contains("failed to initializ") ||
                                   lower.Contains("no device") ||
                                   lower.Contains("impossible to convert") ||
                                   lower.Contains("encoder not found") ||
                                   lower.Contains("invalid argument") ||
                                   lower.Contains("dll") && lower.Contains("failed to open") ||
                                   lower.Contains("current codec type is unsupported");

                    if (hardFail)
                        return false;

                    // 成功标志
                    var success = lower.Contains("video:") ||
                                  lower.Contains("frame=") && lower.Contains("fps=") ||
                                  lower.Contains("encoded");

                    if (success)
                        return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
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

        private static async Task<string> RunFfmpegStderrAsync(string ffmpegPath, string args, int timeoutMs)
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
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return "";

                var errorTask = p.StandardError.ReadToEndAsync();

                var completed = await Task.WhenAny(
                    Task.WhenAll(errorTask, p.WaitForExitAsync()),
                    Task.Delay(timeoutMs));

                if (completed is Task delayTask)
                {
                    try { p.Kill(); } catch { }
                }

                return errorTask.IsCompleted ? errorTask.Result : "";
            }
            catch
            {
                return "";
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
                "vaapi" => "VA-API (Linux)",
                "opencl" => "OpenCL",
                _ => deviceType
            };
        }
    }
}
