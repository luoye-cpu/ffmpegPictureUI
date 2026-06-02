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
                foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories))
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

            // 构建候选优先级列表（优先按文件名特征匹配 CPU 指令集）
            var ordered = new List<string>();
            var supported = new List<string>();
            foreach (var c in list)
            {
                var name = Path.GetFileName(c).ToLowerInvariant();
                if (name.Contains("avx512") || name.Contains("avx512f"))
                {
                    if (CpuFeatureService.HasAvx512F) supported.Add(c);
                }
                else if (name.Contains("avx2"))
                {
                    if (CpuFeatureService.HasAvx2) supported.Add(c);
                }
                else if (name.Contains("avx"))
                {
                    if (CpuFeatureService.HasAvx) supported.Add(c);
                }
                else if (name.Contains("sse4") || name.Contains("sse41"))
                {
                    if (CpuFeatureService.HasSse41) supported.Add(c);
                }
                else if (name.Contains("sse2"))
                {
                    if (CpuFeatureService.HasSse2) supported.Add(c);
                }
                else if (name.Contains("neon") || name.Contains("advsimd") || name.Contains("arm"))
                {
                    if (CpuFeatureService.HasAdvSimd) supported.Add(c);
                }
            }

            // 优先将支持的按优先级加入 ordered 列表
            if (supported.Count > 0)
            {
                var priority = new[] { "avx512", "avx2", "avx", "sse4", "sse41", "sse2", "neon" };
                foreach (var tag in priority)
                {
                    foreach (var s in supported)
                        if (Path.GetFileName(s).ToLowerInvariant().Contains(tag) && !ordered.Contains(s)) ordered.Add(s);
                }
                // 加入剩余 supported
                foreach (var s in supported) if (!ordered.Contains(s)) ordered.Add(s);
            }

            // 然后加入不带特征标识的通用候选
            foreach (var c in list)
            {
                var name = Path.GetFileName(c).ToLowerInvariant();
                if (!name.Contains("avx") && !name.Contains("sse") && !name.Contains("neon") && !name.Contains("arm"))
                {
                    if (!ordered.Contains(c)) ordered.Add(c);
                }
            }

            // 最后加入剩余未加入的候选
            foreach (var c in list) if (!ordered.Contains(c)) ordered.Add(c);

            // 逐个验证候选（运行短样本，例如 --version），首个通过运行验证的优先返回
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

            // 若没有候选通过短样本验证，则退回到原始优先选择逻辑：返回第一个通用或第一个候选
            foreach (var c in list)
            {
                var name = Path.GetFileName(c).ToLowerInvariant();
                if (!name.Contains("avx") && !name.Contains("sse") && !name.Contains("neon") && !name.Contains("arm"))
                    return c;
            }

            return list[0];
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
    }
}
