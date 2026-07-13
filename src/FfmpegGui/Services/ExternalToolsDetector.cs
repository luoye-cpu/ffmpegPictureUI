using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 扫描并识别用户选择目录中与 JPEG / JXL 处理相关的可执行文件与库（cjxl/djxl/cjpegli 等）。
    /// 供 UI 在用户选择工具目录后展示检测结果。
    /// </summary>
    public static class ExternalToolsDetector
    {
        public class ExecutableProbeResult
        {
            public bool IsRunnable { get; set; }
            public int? ExitCode { get; set; }
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;
            public string? Version { get; set; }
            public string? DetectedFeatures { get; set; }
            /// <summary>从输出中检测到的 SIMD 指令集列表（如 avx2, sse4）</summary>
            public List<string> SimdFeatures { get; set; } = new List<string>();
        }

        /// <summary>
        /// 对指定可执行文件进行短时间的运行探测（尝试 --version / --help 等），用于检测是否在当前 CPU 上可运行以及解析版本/优化信息。
        /// 返回结果包含 stdout/stderr、退出码以及从输出中识别到的版本/指令集标识（如 avx2）。
        /// </summary>
        public static ExecutableProbeResult ProbeExecutable(string exePath, int timeoutMs = 2000)
        {
            var res = new ExecutableProbeResult();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return res;

            var argsToTry = new[] { "--version", "-version", "--help", "-h", "/?" };
            foreach (var arg in argsToTry)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = arg,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var p = Process.Start(psi);
                    if (p == null) continue;

                    // 读取输出（通常很短）并等待退出（带超时）
                    string outStr = string.Empty;
                    string errStr = string.Empty;
                    try
                    {
                        outStr = p.StandardOutput.ReadToEnd();
                        errStr = p.StandardError.ReadToEnd();
                    }
                    catch { }

                    var exited = p.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                        continue;
                    }

                    res.ExitCode = p.ExitCode;
                    res.StdOut = outStr ?? string.Empty;
                    res.StdErr = errStr ?? string.Empty;

                    var combined = (res.StdOut + "\n" + res.StdErr).ToLowerInvariant();
                    res.SimdFeatures = ExtractSimdFeatures(combined);
                    res.DetectedFeatures = res.SimdFeatures.Count > 0 ? string.Join(", ", res.SimdFeatures) : null;
                    res.Version = ExtractVersionFromOutput(combined);

                    // 判定为可运行：退出码为0或有输出文本被视为可用（某些工具在 --version 时返回非0但仍输出信息）
                    res.IsRunnable = (res.ExitCode == 0) || !string.IsNullOrWhiteSpace(res.StdOut) || !string.IsNullOrWhiteSpace(res.StdErr);
                    return res;
                }
                catch (Exception ex)
                {
                    // 记录异常到 stderr 字段，继续尝试下一个参数
                    res.StdErr += ex.Message + "\n";
                    try { if (File.Exists(exePath) && OperatingSystem.IsWindows()) { } } catch { }
                }
            }

            return res;
        }

        /// <summary>
        /// 从运行输出中提取检测到的 SIMD 指令集列表。支持以下模式：
        /// - libjxl 风格: "avx2, sse4" 或 "[AVX2, SSE4]"
        /// - ffmpeg 风格: "--enable-avx2 --enable-avx"
        /// - 通用关键词: "avx512", "avx2", "avx", "sse4", "sse2", "neon"
        /// </summary>
        public static List<string> ExtractSimdFeatures(string lowerCombinedOutput)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(lowerCombinedOutput)) return result;

            // ffmpeg 配置标记: --enable-avx512f, --enable-avx2, --enable-avx, --enable-sse42, --enable-sse2
            var ffmpegPatterns = new[] { "enable-avx512f", "enable-avx512", "enable-avx2", "enable-avx", "enable-sse42", "enable-sse41", "enable-sse4", "enable-sse2", "enable-neon" };
            foreach (var pat in ffmpegPatterns)
            {
                if (lowerCombinedOutput.Contains(pat))
                {
                    var tag = pat.Replace("enable-", "");
                    if (!result.Contains(tag)) result.Add(tag);
                }
            }

            // libjxl / 通用: 关键词匹配
            var genericTags = new[] { "avx512f", "avx512", "avx2", "avx", "sse41", "sse4", "sse2", "neon", "advsimd" };
            foreach (var tag in genericTags)
            {
                if (!result.Contains(tag) && lowerCombinedOutput.Contains(tag))
                    result.Add(tag);
            }

            return result;
        }

        // 保留旧方法用于向后兼容（返回单一最高特征）
        private static string? GetFeatureTagFromOutput(string lowerCombinedOutput)
        {
            var features = ExtractSimdFeatures(lowerCombinedOutput);
            return features.Count > 0 ? features[features.Count - 1] : null;
        }

        private static string? ExtractVersionFromOutput(string combined)
        {
            if (string.IsNullOrEmpty(combined)) return null;
            try
            {
                var m = Regex.Match(combined, "\\d+\\.\\d+(\\.\\d+)?");
                if (m.Success) return m.Value;
            }
            catch { }
            return null;
        }

        public class ScanResult
        {
            public string? SelectedDirectory { get; set; }
            public string? CjxlExe { get; set; }
            public string? DjxlExe { get; set; }
            public string? CjpegliExe { get; set; }
            public List<string> OtherExecutables { get; } = new List<string>();
            public List<string> FoundDlls { get; } = new List<string>();
        }

        public static ScanResult ScanDirectory(string dir)
        {
            var res = new ScanResult { SelectedDirectory = dir };
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return res;

            try
            {
                // 查找所有可执行文件（包含子目录），并按已知名称分类（支持带特征后缀的可执行文件）
                foreach (var exe in Directory.EnumerateFiles(dir, PlatformServices.ExeSearchWildcard, SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(exe).ToLowerInvariant();
                    if (name.Contains("cjxl"))
                    {
                        if (res.CjxlExe == null) res.CjxlExe = exe;
                        else if (!res.CjxlExe.Contains("avx") && name.Contains("avx")) res.CjxlExe = exe;
                    }
                    else if (name.Contains("djxl"))
                    {
                        if (res.DjxlExe == null) res.DjxlExe = exe;
                        else if (!res.DjxlExe.Contains("avx") && name.Contains("avx")) res.DjxlExe = exe;
                    }
                    else if (name.Contains("cjpegli") || name.Contains("jpegli"))
                    {
                        if (res.CjpegliExe == null) res.CjpegliExe = exe;
                        else if (!res.CjpegliExe.Contains("avx") && name.Contains("avx")) res.CjpegliExe = exe;
                    }
                    else res.OtherExecutables.Add(exe);
                }

                // 常见可能需要的 DLL（jpegli/libjxl/skcms/lcms2 等）
                var dllPatterns = new[] { "*jpegli*.dll", "*libjpegli*.dll", "*libjxl*.dll", "*skcms*.dll", "*lcms2*.dll", "*jxl*.dll" };
                foreach (var pat in dllPatterns)
                {
                    try
                    {
                        foreach (var dll in Directory.EnumerateFiles(dir, pat, SearchOption.AllDirectories))
                        {
                            if (!res.FoundDlls.Contains(dll)) res.FoundDlls.Add(dll);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return res;
        }

        public static string? ChooseBestExecutable(IEnumerable<string> candidates)
        {
            if (candidates == null) return null;
            CpuFeatureService.Detect();
            var list = new List<string>(candidates);
            // 过滤掉用户手动忽略的路径
            try
            {
                var ignored = AppSettingsService.Current.IgnoredToolPaths;
                if (ignored != null && ignored.Count > 0)
                {
                    list.RemoveAll(p => ignored.Contains(p, StringComparer.OrdinalIgnoreCase));
                }
            }
            catch { }
            if (list.Count == 0) return null;

            // ── 使用 CPU 特征标签优先级排序候选 ──
            var priorityTags = CpuFeatureService.GetSimdPriorityTags();
            var ordered = new List<string>();
            var remaining = new List<string>(list);

            // 按优先级标签依次匹配
            foreach (var tag in priorityTags)
            {
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var name = Path.GetFileName(remaining[i]).ToLowerInvariant();
                    // generic 匹配所有未匹配的
                    if (tag == "generic" || name.Contains(tag))
                    {
                        ordered.Add(remaining[i]);
                        remaining.RemoveAt(i);
                    }
                }
            }
            // 剩余未匹配的追加到末尾
            ordered.AddRange(remaining);

            // 逐个验证候选（运行 --version 等），首个通过运行验证的优先返回
            foreach (var cand in ordered)
            {
                try
                {
                    var probe = ProbeExecutable(cand, timeoutMs: 2000);
                    if (probe != null && probe.IsRunnable)
                        return cand;
                }
                catch { }
            }

            // 回退：返回第一个文件存在且文件名不含特征后缀的通用版本
            foreach (var c in list)
            {
                var name = Path.GetFileName(c).ToLowerInvariant();
                bool hasFeatureTag = false;
                foreach (var tag in priorityTags)
                {
                    if (tag != "generic" && name.Contains(tag)) { hasFeatureTag = true; break; }
                }
                if (!hasFeatureTag) return c;
            }

            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>
        /// 专门探测 ffmpeg 的 SIMD 编译能力（通过 ffmpeg -version 输出）
        /// </summary>
        public static ExecutableProbeResult? ProbeFfmpeg(string? ffmpegPath = null)
        {
            try
            {
                var path = ffmpegPath ?? AppSettingsService.Current.FfmpegPath;
                if (string.IsNullOrWhiteSpace(path)) return null;
                // 如果 path 只是 "ffmpeg"（未指定目录），尝试从 PATH 解析
                if (!File.Exists(path) && path.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var psi = new ProcessStartInfo { FileName = "where", Arguments = "ffmpeg", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                        using var wp = Process.Start(psi);
                        if (wp != null)
                        {
                            var wout = wp.StandardOutput.ReadToEnd().Trim();
                            wp.WaitForExit(3000);
                            var firstLine = wout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(firstLine) && File.Exists(firstLine))
                                path = firstLine;
                        }
                    }
                    catch { }
                }
                if (!File.Exists(path)) return null;
                return ProbeExecutable(path, 3000);
            }
            catch { return null; }
        }

        public static string? GetFeatureTagFromFileName(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var name = Path.GetFileName(path).ToLowerInvariant();
            if (name.Contains("avx512")) return "avx512";
            if (name.Contains("avx2")) return "avx2";
            if (name.Contains("avx")) return "avx";
            if (name.Contains("sse41") || name.Contains("sse4")) return "sse4";
            if (name.Contains("sse2")) return "sse2";
            if (name.Contains("neon") || name.Contains("advsimd")) return "neon";
            return null;
        }

        /// <summary>
        /// 获取扩展的外部工具搜索路径（Windows: LocalAppData\Programs, Program Files 等）。
        /// 用于在 ffmpeg 同目录之外发现 cjxl/exiftool 等工具。
        /// </summary>
        public static List<string> GetExtendedSearchPaths()
        {
            var paths = new List<string>();

            if (!OperatingSystem.IsWindows())
                return paths;

            try
            {
                // %LocalAppData%\Programs\ （scoop 安装位置）
                var localPrograms = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs");
                if (Directory.Exists(localPrograms))
                    paths.Add(localPrograms);

                // C:\Program Files\ 及子目录
                var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (Directory.Exists(progFiles))
                {
                    paths.Add(progFiles);
                    // 常见子目录
                    foreach (var sub in new[] { "exiftool", "ffmpeg", "ImageMagick" })
                    {
                        var subPath = Path.Combine(progFiles, sub);
                        if (Directory.Exists(subPath))
                            paths.Add(subPath);
                    }
                }

                // PATH 环境变量中的所有目录（作为回退搜索源）
                var pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (var segment in pathEnv.Split(';'))
                    {
                        var trimmed = segment.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && Directory.Exists(trimmed))
                            paths.Add(trimmed);
                    }
                }
            }
            catch { }

            return paths;
        }

        /// <summary>
        /// 在扩展搜索路径中查找工具。返回找到的第一个匹配路径，未找到返回 null。
        /// </summary>
        public static string? FindToolInExtendedPaths(string toolName, string searchWildcard)
        {
            var extendedPaths = GetExtendedSearchPaths();
            foreach (var dir in extendedPaths)
            {
                var found = PlatformServices.FindToolInDirectory(dir, toolName, searchWildcard);
                if (found != null) return found;
            }
            return null;
        }

        // ═══════════════════════════════════════════════
        // 统一工具版本探测与能力报告
        // ═══════════════════════════════════════════════

        /// <summary>单个工具的能力摘要</summary>
        public class ToolCapability
        {
            public string Name { get; set; } = "";
            public string? Path { get; set; }
            public string? Version { get; set; }
            public string? SimdFeatures { get; set; }
            public bool IsAvailable { get; set; }
            public string StatusIcon => IsAvailable ? "✅" : "❌";
            public override string ToString() => $"{StatusIcon} {Name}: {(IsAvailable ? $"v{Version} [{SimdFeatures}]" : "未检测到")}";
        }

        /// <summary>
        /// 探测所有外部工具的版本和能力，返回结构化报告。
        /// 用于启动日志和 UI 状态栏展示。
        /// </summary>
        public static List<ToolCapability> ProbeAllTools()
        {
            var results = new List<ToolCapability>();

            // 确保所有 Service 已完成检测
            CjxlService.Detect();
            DjxlService.Detect();
            CjpegliService.Detect();
            UltrahdrService.Detect();
            JxrService.Detect();
            ExifToolService.Detect();

            // ffmpeg
            var ffmpegPath = AppSettingsService.Current.FfmpegPath;
            var ffmpegProbe = ProbeExecutable(ffmpegPath, 4000);
            results.Add(new ToolCapability
            {
                Name = "ffmpeg",
                Path = ffmpegPath,
                Version = ffmpegProbe.Version ?? TryExtractFfmpegVersion(ffmpegPath),
                SimdFeatures = ffmpegProbe.DetectedFeatures,
                IsAvailable = ffmpegProbe.IsRunnable
            });

            // cjxl
            var cjxl = CjxlService.DetectedPath;
            var cjxlProbe = cjxl != null ? ProbeExecutable(cjxl, 2000) : null;
            results.Add(new ToolCapability
            {
                Name = "cjxl",
                Path = cjxl,
                Version = cjxlProbe?.Version ?? TryExtractCjxlVersion(cjxl),
                SimdFeatures = cjxlProbe?.DetectedFeatures ?? TryExtractCjxlSimd(cjxl),
                IsAvailable = CjxlService.IsAvailable
            });

            // djxl
            var djxl = DjxlService.DetectedPath;
            results.Add(new ToolCapability
            {
                Name = "djxl",
                Path = djxl,
                Version = ProbeAndVersion(djxl),
                IsAvailable = DjxlService.IsAvailable
            });

            // cjpegli
            var cjpegli = CjpegliService.DetectedPath;
            results.Add(new ToolCapability
            {
                Name = "cjpegli",
                Path = cjpegli,
                Version = ProbeCjpegliVersion(cjpegli),
                IsAvailable = CjpegliService.IsAvailable
            });

            // exiftool
            var et = ExifToolService.DetectedPath;
            results.Add(new ToolCapability
            {
                Name = "exiftool",
                Path = et,
                Version = ProbeAndVersion(et),
                IsAvailable = ExifToolService.IsAvailable
            });

            // ultrahdr
            var uhdr = UltrahdrService.DetectedPath;
            results.Add(new ToolCapability
            {
                Name = "ultrahdr_app",
                Path = uhdr,
                Version = ProbeUltrahdrVersion(uhdr),
                IsAvailable = UltrahdrService.IsAvailable
            });

            // JxrEncApp
            var jxr = JxrService.DetectedPath;
            results.Add(new ToolCapability
            {
                Name = "JxrEncApp",
                Path = jxr,
                Version = ProbeAndVersion(jxr),
                IsAvailable = JxrService.IsAvailable
            });

            // avifenc
            var avifenc = AppSettingsService.Current.AvifencPath;
            if (string.IsNullOrWhiteSpace(avifenc))
                avifenc = Path.Combine(AppSettingsService.Current.FfmpegDir ?? "", PlatformServices.Avifenc);
            results.Add(new ToolCapability
            {
                Name = "avifenc",
                Path = File.Exists(avifenc) ? avifenc : null,
                Version = ProbeAndVersion(avifenc),
                IsAvailable = File.Exists(avifenc)
            });

            return results;
        }

        // ── 每工具专用版本探测 ──

        private static string? ProbeAndVersion(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var probe = ProbeExecutable(path, 2000);
            return probe.Version;
        }

        /// <summary>cjpegli 不支持 --version，需通过 stderr 提取版本</summary>
        private static string? ProbeCjpegliVersion(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path, Arguments = "-h",
                    RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(2000);
                return ExtractVersionFromOutput(err);
            }
            catch { return null; }
        }

        /// <summary>ultrahdr_app 版本在 stdout 中 "lib version: vX.Y.Z"</summary>
        private static string? ProbeUltrahdrVersion(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path, Arguments = "-h",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(2000);
                // "lib version: v1.4.0"
                var m = Regex.Match(output, @"lib version:\s*v?([\d.]+)");
                return m.Success ? m.Groups[1].Value : ExtractVersionFromOutput(output);
            }
            catch { return null; }
        }

        /// <summary>从 cjxl --version 输出中提取版本: "cjxl v0.11.2 332feb1 [AVX2,SSE2]"</summary>
        private static string? TryExtractCjxlVersion(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path, Arguments = "--version",
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                // "cjxl v0.11.2 332feb1 [AVX2,SSE2]"
                var m = Regex.Match(output, @"v(\d+\.\d+\.\d+)");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>从 cjxl --version 输出中提取 SIMD 标签: [AVX2,SSE2]</summary>
        private static string? TryExtractCjxlSimd(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path, Arguments = "--version",
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                var m = Regex.Match(output, @"\[([^\]]+)\]");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>从 ffmpeg 输出中提取版本</summary>
        private static string? TryExtractFfmpegVersion(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path, Arguments = "-version",
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                // "ffmpeg version git-2026-07-05-97cbffe917"
                var m = Regex.Match(output, @"ffmpeg version ([\w\.\-]+)");
                return m.Success ? m.Groups[1].Value : ExtractVersionFromOutput(output);
            }
            catch { return null; }
        }
    }
}
