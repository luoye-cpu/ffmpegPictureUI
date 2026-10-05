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
            /// <summary>
            /// ⚠ 与「超时」**必须区分**：以下三种都算「这个文件根本不可作为工具启动」——
            ///  ① `Process.Start` 自己抛（实测：ACL 拒绝启动的 `WindowsApps\...\djxl.exe`、非 PE 的伪 `.exe`、文件已消失）；
            ///  ② 进程起来了但**加载器**当场把它杀掉（缺依赖 DLL 等，退出码 `0xC0000135/0xC000007B/0xC0000142`，
            ///     实测这类"起得来但不干活"若不算起不来，兜底照样报 ✅ 而任务必失败）；
            ///  ③ 空路径。
            /// ⇒ 但**探测超时不算**（`exiftool(-k).exe` 这类"打印完等你按键"的工具恒超时却确实能用）。
            /// </summary>
            public bool LaunchFailed { get; set; }
            public int? ExitCode { get; set; }
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;
            public string? Version { get; set; }
            public string? DetectedFeatures { get; set; }
            /// <summary>从输出中检测到的 SIMD 指令集列表（如 avx2, sse4）</summary>
            public List<string> SimdFeatures { get; set; } = new List<string>();
        }

        /// <summary>
        /// **只回答"这个文件能不能作为进程启动"** —— 不解析版本、不试多组参数。
        /// 为什么单独有它（2026-09-23）：可用性收口原本复用 <see cref="ProbeExecutable"/>，
        /// 而那个原语会**依次试 5 组参数**（`--version/-version/--help/-h/?`）每组最多等 2 s
        /// ⇒ 最坏 **~10 s**。而 `RawService.Detect()` 有 UI 线程调用点
        /// （`MainWindow.InitControls()` 与 `RefreshArtifactsServices()` ⇒ "重新检测/换目录"），
        /// 等于把一个未声明的上界放进用户必点的交互路径里。
        /// 三种"起不来"形态**逐字保持**（与 `ExecutableProbeResult.LaunchFailed` 同一口径）：
        ///   ① `Process.Start` 抛（ACL 拒绝、非 PE、文件已消失）；
        ///   ② 起来了但被**加载器**当场杀掉（`0xC0000135/0xC000007B/0xC0000142`）；
        ///   ③ 空路径 / 文件不存在。
        /// ⚠ **超时不算起不来** ⇒ 跑到预算仍未退出即判"起得来"并立刻 Kill
        ///   （`exiftool(-k).exe` 这类"打印完等你按键"的工具恒"不退出"却确实能用）。
        /// 预算默认 1000 ms ⇒ 单次启动、界内可预期。
        /// </summary>
        public static bool CanLaunch(string? exePath, int budgetMs = 1000)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;   // ③
            Process? p = null;
            try
            {
                p = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--version",            // 只试一组：起不起得来与参数是否被认识无关
                    UseShellExecute = false,
                    RedirectStandardOutput = false,     // 不重定向 ⇒ 无需排空，也就不会因缓冲堵死
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                });
            }
            catch
            {
                return false;                            // ①
            }
            if (p == null) return false;                 // ①（某些宿主 Start 返回 null）
            try
            {
                if (!p.WaitForExit(budgetMs))
                {
                    // 预算内没退出 ⇒ 它确实在跑（等输入/等参数）⇒ 判可启动，别把它留下
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return true;
                }
                return !IsLoaderFailureExit(p.ExitCode); // ②
            }
            catch { return true; }                       // 读不到退出码但进程已存在过 ⇒ 保守判「起得来」
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// 对指定可执行文件进行短时间的运行探测（尝试 --version / --help 等），用于检测是否在当前 CPU 上可运行以及解析版本/优化信息。
        /// 返回结果包含 stdout/stderr、退出码以及从输出中识别到的版本/指令集标识（如 avx2）。
        /// </summary>
        /// <param name="log">日志回调（超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        public static ExecutableProbeResult ProbeExecutable(string exePath, int timeoutMs = 2000,
            Action<string>? log = null)
        {
            var res = new ExecutableProbeResult();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                // ⚠ 文件根本不存在 ⇒ 属于「起不来」而不是「探测超时」⇒ 必须让兜底把它排除，
                //   否则枚举与启动之间的时间窗里文件消失（删档/清理器）也会被判成可用工具。
                res.LaunchFailed = true;
                return res;
            }

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
                        StandardOutputEncoding = Encoding.UTF8,
                        RedirectStandardError = true,
                        StandardErrorEncoding = Encoding.UTF8,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var p = Process.Start(psi);
                    if (p == null) { res.LaunchFailed = true; continue; }
                    res.LaunchFailed = false;   // 起得来 ⇒ 清掉更早的参数留下的标记

                    // ⚠ 先挂读取任务、再等退出（本仓范式，见 `FfmpegCommandBuilder.ProbeWithFfprobe`）：
                    //   旧写法把同步读写在 `WaitForExit(timeoutMs)` **之前** ⇒ 子进程不关管道就永不返回，
                    //   **超时（以及超时后的 Kill）是死代码**。
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();

                    var exited = p.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                        var tmsg = $"[probe] 工具探测超时（{timeoutMs}ms）⇒ 已终止：{Path.GetFileName(exePath)}（参数 {arg}）";
                        if (log != null) log(tmsg + "\n"); else System.Diagnostics.Trace.WriteLine(tmsg);
                        continue;
                    }

                    string outStr = string.Empty;
                    string errStr = string.Empty;
                    try
                    {
                        outStr = outTask.GetAwaiter().GetResult();
                        errStr = errTask.GetAwaiter().GetResult();
                    }
                    catch { }

                    res.ExitCode = p.ExitCode;
                    res.StdOut = outStr ?? string.Empty;
                    res.StdErr = errStr ?? string.Empty;

                    var combined = (res.StdOut + "\n" + res.StdErr).ToLowerInvariant();
                    res.SimdFeatures = ExtractSimdFeatures(combined);
                    res.DetectedFeatures = res.SimdFeatures.Count > 0 ? string.Join(", ", res.SimdFeatures) : null;
                    res.Version = ExtractVersionFromOutput(combined);

                    // 判定为可运行：退出码为0或有输出文本被视为可用（某些工具在 --version 时返回非0但仍输出信息）
                    res.IsRunnable = (res.ExitCode == 0) || !string.IsNullOrWhiteSpace(res.StdOut) || !string.IsNullOrWhiteSpace(res.StdErr);
                    // ⚠ 起来了但被**加载器**当场杀掉（缺 DLL / 镜像格式不对）⇒ 等同"起不来"（见 LaunchFailed 注释 ②）
                    if (!res.IsRunnable && IsLoaderFailureExit(res.ExitCode)) res.LaunchFailed = true;
                    return res;
                }
                catch (Exception ex)
                {
                    // 记录异常到 stderr 字段，继续尝试下一个参数
                    // ⚠ 起不来 ≠ 超时：标记下来，供 ChooseBestExecutable 的兜底排除（见 LaunchFailed 注释）
                    res.LaunchFailed = true;
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
                // ⚠ 走 EnumerateFilesSafe：用户完全可能选到一棵大树（实测含 junction 回环的目录会让
                //   AllDirectories 永不返回），这里必须"有预算地扫 + 说明扫到哪"，不能挂死界面。
                foreach (var exe in EnumerateFilesSafe(dir, PlatformServices.ExeSearchWildcard))
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
                        foreach (var dll in EnumerateFilesSafe(dir, pat))
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

        /// <summary>递归扫描的**单根**预算（秒）。判据取自实测，不是猜的：
        /// 合法根 `C:\Program Files` 全树 = 11.5 s ⇒ 留 ~30% 余量取 15 s。</summary>
        public const int PerRootScanBudgetSec = 15;

        /// <summary>
        /// 一次 <see cref="FindToolInExtendedPaths"/> 的**总**预算（秒）。
        /// <para>⚠⚠ 2026-10-05 加固（P0 启动停顿修复）：总预算此前**只在每根之前**检查
        /// ⇒ 单个大根能吃掉整段预算，而本探测对**多个工具**各做一遍 ⇒ 最坏是"根数 × 15 s × 工具数"。
        /// 现改为**把剩余预算传给每个根**（见 <see cref="FindToolInExtendedPaths"/>）：
        /// 每根实际能用的秒数 = `min(单根预算, 总预算剩余)`，且遍历**内部**也会因预算耗尽而截断
        /// （`EnumerateFilesSafe` 自己就带 `budgetSec` 检查）。⇒ 总耗时被**真正**钉在 60 s 量级。</para>
        /// </summary>
        public const int ExtendedScanTotalBudgetSec = 60;

        /// <summary>
        /// 退出码是否属于「进程被 Windows 加载器当场杀掉」（镜像/依赖问题）—— 这类文件**不是可用的工具**，
        /// 与「工具本身不支持该参数」（业务失败）或「探测超时」都不同。
        /// </summary>
        private static bool IsLoaderFailureExit(int? exitCode)
        {
            if (!exitCode.HasValue) return false;
            int c = exitCode.Value;
            return c == unchecked((int)0xC0000135)    // STATUS_DLL_NOT_FOUND
                || c == unchecked((int)0xC000007B)    // STATUS_INVALID_IMAGE_FORMAT
                || c == unchecked((int)0xC0000142);   // STATUS_DLL_INIT_FAILED
        }

        /// <summary>
        /// 扫描日志出口：有 log 回调就交给它（进应用日志面板），否则进 Trace。
        /// ⚠ 调用点必须把**整条消息写在同一行**。机制（2026-09-23 实测，别再凭猜改）：
        ///   `_probe-cjk-hardcode-scan` 的 ②-c 按「该行**第一个**含中文的字面量」分桶——
        ///   以 <c>[</c> 开头的进 <c>[tag]</c> 桶（不判），否则看同一行有没有 sink 令牌，再落进判据桶（UI 面）。
        ///   本类的消息一律以 <c>[detect] …</c> 开头 ⇒ 靠的是**前缀进 [tag] 桶**而**不是** sink：
        ///   sink 正则里的 <c>\bLog\s*\(</c> 在 <c>DiagLog(</c> 处**没有词边界**（前面是字母 g）⇒ 不匹配。
        ///   ⇒ 一旦把字面量折行，续行的第一个字面量既不以 <c>[</c> 开头、同行也没有 sink ⇒ 抬一次棘轮。
        /// </summary>
        private static void DiagLog(Action<string>? log, string msg)
        {
            if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
        }

        /// <summary>
        /// 带预算的递归文件收集。**四条硬约束都对应实测踩过的坑**：
        ///  ① 自己走显式栈、**跳过重解析点**：PATH 里只要出现一棵大树（实测本机进程 PATH 含
        ///    `C:\Users\&lt;me&gt;`，用户配置文件根目录），.NET 的 AllDirectories 递归就**永不返回**
        ///    —— 60 s 仍不返回、句柄以 ~2000/s 涨到 9 万、单核满载 ⇒ 启动/入队路径直接挂死。
        ///  ② 预算按**目录**收口而不是按命中收口：`*djxl*.exe` 这种稀有通配在"只数命中"的写法下
        ///    永远不会触发检查点，等于没有预算。
        ///  ③ 文件枚举与子目录枚举**各自容错**：两者若共用一个 try，"这一层的文件读崩了"会连带
        ///    **整棵子树不入栈** ⇒ 又回到"静默丢命中"。
        ///  ④ 被跳过的目录数必须**报出来**：只报超时不报跳过 = 把"扫不全"这件事藏进成功路径。
        /// 超预算 ⇒ 返回已收集到的部分，并**点名**是哪个根被截断（不得静默）。
        /// </summary>
        public static List<string> EnumerateFilesSafe(
            string root, string wildcard, Action<string>? log = null, int budgetSec = PerRootScanBudgetSec)
        {
            var hits = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return hits;

            var sw = Stopwatch.StartNew();
            var stack = new Stack<string>();
            stack.Push(root);
            int dirs = 0, reparse = 0, blocked = 0;
            bool truncated = false;
            while (stack.Count > 0)
            {
                if (sw.Elapsed.TotalSeconds > budgetSec) { truncated = true; break; }
                var dir = stack.Pop();
                try
                {
                    dirs++;
                    foreach (var f in Directory.EnumerateFiles(dir, wildcard)) hits.Add(f);
                }
                catch { blocked++; /* 本层文件不可读 ⇒ 只丢这一层，子目录照旧入栈 */ }
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(dir))
                    {
                        if (sw.Elapsed.TotalSeconds > budgetSec) { truncated = true; break; }
                        try
                        {
                            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) { reparse++; continue; }
                        }
                        catch { blocked++; continue; }
                        stack.Push(d);
                    }
                }
                catch { blocked++; }
            }
            // ⚠ 只在**真的截断**时出声。实测普通令牌下 `C:\Program Files\WindowsApps` 的 DACL 无 AU/BU 允许 ACE
            //   ⇒ "被挡目录数 > 0" 在一次**正常**扫描里恒成立；重解析点更是设计内跳过。
            //     把它们做成常态日志 ⇒ 每根×每工具都喷一行（`ScanDirectory` 一次就 7 行），把"点名异常"稀释成噪声。
            if (truncated)
                DiagLog(log, $"[detect] 递归扫描超出 {budgetSec}s 预算 ⇒ 已截断：{root}（已扫 {dirs} 个目录，跳过重解析 {reparse} 个、不可访问 {blocked} 个；扫不到的工具可在设置里手动指定路径）");
            return hits;
        }

        /// <summary>按**目录边界**判断 <c>candidate</c> 是否等于 <c>parent</c> 或位于其下（Windows 路径大小写不敏感）。</summary>
        private static bool IsUnderDirectory(string candidate, string parent)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(parent)) return false;
            var c = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var p = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (c.Length < p.Length) return false;
            if (!c.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
            // 必须正好相等，或紧接着一个分隔符（否则 C:\Windowsold 会冒充 C:\Windows 的子目录）
            return c.Length == p.Length || c[p.Length] == Path.DirectorySeparatorChar || c[p.Length] == Path.AltDirectorySeparatorChar;
        }

        /// <summary>
        /// 候选是否可能是「可直接 Process.Start 的可执行文件」。
        /// Windows 按扩展名收口（Prefetch 的 <c>*.EXE-*.pf</c>、DLL、说明文件等一律不算）；
        /// 其余平台的外部工具常无扩展名 ⇒ 不做限制（否则会误杀）。
        /// </summary>
        private static bool IsExecutableFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!OperatingSystem.IsWindows()) return true;
            var ext = Path.GetExtension(path);
            return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".com", StringComparison.OrdinalIgnoreCase);
        }

        public static string? ChooseBestExecutable(IEnumerable<string> candidates)
        {
            if (candidates == null) return null;
            CpuFeatureService.Detect();
            var list = new List<string>(candidates);
            // ⚠ 只接受**真正可执行**的候选（本函数是唯一收口，故在这里统一把关）：
            //   调用方的通配常写成 `*exiftool*` / `*jxr*` 这种**不限扩展名**的形式，而 Windows 的 PATH
            //   含 `C:\Windows`，候选又以 AllDirectories 递归 ⇒ 实测命中
            //   `C:\Windows\Prefetch\EXIFTOOL.EXE-65513778.pf`，加上末尾那条「探测全失败仍返回 list[0]」的
            //   回退，面板会报 `exiftool: ✅` 而真正 Process.Start 时抛 InvalidApplication（任务失败、
            //   已产出的成品还被半成品清理删掉）。⇒ 非可执行文件必须在进入排序/回退前就剔除。
            list.RemoveAll(p => !IsExecutableFile(p));
            // ⚠ 用户手动忽略的路径
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
            var unlaunchable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cand in ordered)
            {
                try
                {
                    var probe = ProbeExecutable(cand, timeoutMs: 2000);
                    if (probe != null && probe.IsRunnable)
                        return cand;
                    // ⚠ 只登记「Process.Start 自己抛了」的候选。探测**超时**不进这里 ——
                    //   `exiftool(-k).exe` 这类"打印完等你按键"的工具恒超时，但它确实能用，
                    //   把超时也算成起不来 ⇒ 直接砍掉一条真实可用的安装形态。
                    if (probe != null && probe.LaunchFailed) unlaunchable.Add(cand);
                }
                catch { unlaunchable.Add(cand); }
            }

            // 回退：返回第一个文件存在且文件名不含特征后缀的通用版本
            foreach (var c in list)
            {
                if (unlaunchable.Contains(c)) continue;
                var name = Path.GetFileName(c).ToLowerInvariant();
                bool hasFeatureTag = false;
                foreach (var tag in priorityTags)
                {
                    if (tag != "generic" && name.Contains(tag)) { hasFeatureTag = true; break; }
                }
                if (!hasFeatureTag) return c;
            }

            // ⚠⚠ 末尾兜底**不得**把"已证实起不来"的文件报成可用工具（可用性诚实原则）：
            //   实测本机 `C:\Program Files\WindowsApps\LinYuSen.HDRImageViewer_1.0.31.0_x64__phzzaxm6z2j1m\
            //   encoders\x64\djxl.exe` 是别的包里的私有二进制，普通令牌 Process.Start 直接 Access Denied，
            //   而旧写法照样返回它 ⇒ 面板 ✅、真跑时 InvalidApplication（任务失败），
            //   并让「模拟工具缺失」的两条门禁（jxl-codestream-route ⑦ / ffmpeg-heartbeat ③）自检转红。
            var launchable = list.Where(p => !unlaunchable.Contains(p)).ToList();
            return launchable.Count > 0 ? launchable[0] : null;
        }

        /// <summary>
        /// 专门探测 ffmpeg 的 SIMD 编译能力（通过 ffmpeg -version 输出）
        /// </summary>
        /// <param name="log">日志回调（超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        public static ExecutableProbeResult? ProbeFfmpeg(string? ffmpegPath = null, Action<string>? log = null)
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
                        var psi = new ProcessStartInfo
                        {
                            FileName = "where",
                            Arguments = "ffmpeg",
                            RedirectStandardOutput = true,
                            StandardOutputEncoding = Encoding.UTF8,   // P2-5：默认按控制台代码页解码，非 ASCII 路径会乱码
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var wp = Process.Start(psi);
                        if (wp != null)
                        {
                            // ⚠ 先挂读取任务、再等退出（本仓范式）：旧写法同步读在 `WaitForExit(3000)` 之前。
                            //   注意：本 psi **未**重定向 stderr ⇒ 只挂 stdout 读取任务（取 StandardError 会抛）。
                            var woutTask = wp.StandardOutput.ReadToEndAsync();
                            if (!wp.WaitForExit(3000))
                            {
                                try { wp.Kill(entireProcessTree: true); } catch { }
                                var wmsg = "[probe] where ffmpeg 超时（3s）⇒ 已终止（未解析到 ffmpeg 路径）";
                                if (log != null) log(wmsg + "\n"); else System.Diagnostics.Trace.WriteLine(wmsg);
                                return null;
                            }
                            var wout = woutTask.GetAwaiter().GetResult().Trim();
                            var firstLine = wout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(firstLine) && File.Exists(firstLine))
                                path = firstLine;
                        }
                    }
                    catch { }
                }
                if (!File.Exists(path)) return null;
                return ProbeExecutable(path, 3000, log);
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

            // ⚠ 可注入的收口（**门禁与部署都需要它**）：显式给出搜索根时，本函数只返回这些根。
            //   动机是实测教训：「模拟某工具不可用」的门禁原先只能把 PLAN/手动路径指向空目录，
            //   而探测的第 ④ 步仍会把整台机器找一遍 —— 本机因此被 `C:\Program Files\WindowsApps\`
            //   里另一个应用自带的私有 `djxl.exe` 命中 ⇒ 降级断言的自检判「模拟没生效」而红。
            //   ⇒ 降级路径的判据**不能依赖本机恰好没装过某个第三方应用**。
            //   空段与不存在的目录一律丢弃（否则一个末尾 `;` 就等于"不覆盖"，静默失效）。
            var over = Environment.GetEnvironmentVariable("FFMPEGGUI_EXT_SEARCH_DIRS");
            if (!string.IsNullOrWhiteSpace(over))
            {
                foreach (var seg in over.Split(';'))
                {
                    var t = seg.Trim();
                    if (t.Length > 0 && Directory.Exists(t)) paths.Add(t);
                }
                return paths;
            }

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
                    // ⚠⚠ 2026-10-05 修（P0 启动停顿）：**不再把整棵 `C:\Program Files` 交给递归扫描**。
                    //   实测：整树递归 = 11.5 s/**根**（本文件 `PerRootScanBudgetSec` 的标定值就是它），
                    //   而本探测对**多个工具**各做一遍 ⇒ 6 工具 × 11.5 s ≈ 70 s 纯等待，
                    //   用户看到的是"点了没反应"。
                    //   而"工具装在 Program Files 下"的真实形态几乎都是**具名子目录**
                    //   （`Program Files\exiftool`、`\ffmpeg`）⇒ 下面那几个子目录已覆盖；
                    //   其余情况由第⑤步 `TryFindInPath`（**非递归**）与"设置里手动指定路径"覆盖。
                    //   ⇒ 去掉整树根，只保留具名子目录 ⇒ **不丢可发现性，去掉分钟级空转**。
                    //   ⚠ 若确有用户把工具放在 `Program Files\<厂商>\<产品>\bin`，可用设置手动指定，
                    //     或设 `FFMPEGGUI_EXT_SEARCH_DIRS` 显式给出根（该注入面本就有）。
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
                    // ⚠ 不扫系统目录（本函数返回的每个目录都会被 FindToolInDirectory 以
                    //   SearchOption.AllDirectories **递归**遍历）：实测 PATH 含 `C:\Windows` ⇒
                    //   扫到 `C:\Windows\Prefetch\EXIFTOOL.EXE-*.pf` 并被当成工具（面板假 ✅，真正
                    //   Process.Start 抛 InvalidApplication），且遍历整棵 Windows 树本身是分钟级开销。
                    //   「装在 PATH 里的工具」另由第⑤步 TryFindInPath **非递归**解析 ⇒ 排除系统目录不丢能力。
                    var winDir = (Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "")
                                     .TrimEnd(Path.DirectorySeparatorChar);
                    foreach (var segment in pathEnv.Split(';'))
                    {
                        var trimmed = segment.Trim();
                        if (string.IsNullOrEmpty(trimmed) || !Directory.Exists(trimmed)) continue;
                        if (winDir.Length > 0 && IsUnderDirectory(trimmed, winDir)) continue;
                        // ⚠⚠ 2026-10-05 修（P0：工具缺失时启动**停顿数十秒~数分钟**，像卡死）：
                        //   **PATH 里的目录一律不进"递归扩展扫描"**。
                        //   根因（实测 + 读码）：本函数返回的每个根都会被 `FindToolInDirectory`
                        //   以**递归**方式遍历（`EnumerateFilesSafe`），而 PATH 是**用户环境**，
                        //   常见形态就含用户配置目录（如 `C:\Users\<me>`）⇒ 实测扫了 **67,558 个目录**、
                        //   耗时 **135.8 s**，stdout 停在「正在检测外部工具...」不动。
                        //   而"装在 PATH 里的工具"**另有第⑤步 `TryFindInPath` 做非递归解析**
                        //   （本文件上方注释自己就写着这一点）⇒ 把 PATH 从递归面里摘掉**不丢任何能力**。
                        //   ⚠ 预算不是没做（单根 15 s / 总 60 s），但它**在每根之前**检查
                        //     ⇒ 单个大根就能吃掉整段预算，且该探测对**多个工具**各做一遍。
                        //   ⇒ 摘掉 PATH 后：递归面只剩少数"工具安装位置"根（LocalAppData\Programs、
                        //     Program Files 及其 exiftool/ffmpeg/ImageMagick 子目录）—— 这些才值得递归。
                        //   ⚠ 保留 `paths.Add` 之外的**非递归**命中路径不变（见 TryFindInPath）。
                        continue;
                    }
                }
            }
            catch { }

            return paths;
        }

        /// <summary>
        /// 在扩展搜索路径中查找工具。返回找到的第一个匹配路径，未找到返回 null。
        /// ⚠ 除单根预算外再收一道**总**预算：实测扩展根有 35 个，只按单根收口最坏仍是分钟级。
        /// </summary>
        public static string? FindToolInExtendedPaths(
            string toolName, string searchWildcard, Action<string>? log = null)
        {
            var extendedPaths = GetExtendedSearchPaths();
            var sw = Stopwatch.StartNew();
            foreach (var dir in extendedPaths)
            {
                if (sw.Elapsed.TotalSeconds > ExtendedScanTotalBudgetSec)
                {
                    DiagLog(log, $"[detect] {toolName} 的扩展搜索超出总预算 {ExtendedScanTotalBudgetSec}s ⇒ 放弃剩余根（可在设置里手动指定该工具路径）");
                    return null;
                }
                // ⚠⚠ 2026-10-05：把**剩余总预算**也传给单根扫描（取两者较小）
                //   —— 否则单个大根可以一路吃掉整个总预算（旧写法只在每根**之前**查总预算）。
                int remain = (int)Math.Max(1, ExtendedScanTotalBudgetSec - sw.Elapsed.TotalSeconds);
                var found = PlatformServices.FindToolInDirectory(dir, toolName, searchWildcard, log,
                    Math.Min(PerRootScanBudgetSec, remain));
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
        /// 预热所有外部工具检测（headless 模式调用，避免并发首次访问时重复 Detect）。
        /// 仅触发各 Service 的检测逻辑，不做版本探测。
        /// </summary>
        /// <param name="log">日志回调（`#26`：`ExifToolService.Detect` 的取消/超时点名用；null ⇒ 退回 Trace.WriteLine）</param>
        public static void EnsureAllDetected(Action<string>? log = null)
        {
            try { CjxlService.Detect(); } catch { }
            try { DjxlService.Detect(); } catch { }
            try { CjpegliService.Detect(); } catch { }
            try { JxrService.Detect(); } catch { }
            try { ExifToolService.Detect(log); } catch { }
            try { RawService.Detect(); } catch { }
        }

        /// <summary>
        /// 探测所有外部工具的版本和能力，返回结构化报告。
        /// 用于启动日志和 UI 状态栏展示。
        /// </summary>
        /// <param name="log">日志回调（`#26`：本方法内 6 处探测的超时点名统一走它；null ⇒ 退回 Trace.WriteLine）</param>
        public static List<ToolCapability> ProbeAllTools(Action<string>? log = null)
        {
            var results = new List<ToolCapability>();

            // ⚠ 这里原先无条件把 5 个 service 的 `Detect()` **各再跑一遍**：GUI 启动 Step1 已经
            //   `ClearCache(); Detect();` 过一次 ⇒ 同一条 5 级路径搜索（含最坏 60 s 的扩展搜索）
            //   在一次启动里被跑两遍。⇒ 改成"确保已检测"而不是"强制重扫"：读 `DetectedPath`
            //   （getter 自带"没检测过才检测"），需要重扫的调用方自己先 `ClearCache()`。
            _ = CjxlService.DetectedPath;
            _ = DjxlService.DetectedPath;
            _ = CjpegliService.DetectedPath;
            _ = JxrService.DetectedPath;
            // exiftool 走 Detect(log) 而不是 DetectedPath：`#26` 要求把它的取消/超时点名送进 log，
            // 而它自身已带"一进程一遍"门控 ⇒ 这里重复调用是零开销。
            ExifToolService.Detect(log);
            // RawService 的 `DetectedPath` **不触发**检测 ⇒ 保留显式 Detect()（其内部是
            // `if (_detected) return` 的幂等守卫，零开销）。
            RawService.Detect();

            // ffmpeg
            var ffmpegPath = AppSettingsService.Current.FfmpegPath;
            var ffmpegProbe = ProbeExecutable(ffmpegPath, 4000, log);
            results.Add(new ToolCapability
            {
                Name = "ffmpeg",
                Path = ffmpegPath,
                Version = ffmpegProbe.Version ?? TryExtractFfmpegVersion(ffmpegPath, log),
                SimdFeatures = ffmpegProbe.DetectedFeatures,
                IsAvailable = ffmpegProbe.IsRunnable
            });

            // cjxl
            var cjxl = CjxlService.DetectedPath;
            var cjxlProbe = cjxl != null ? ProbeExecutable(cjxl, 2000, log) : null;
            results.Add(new ToolCapability
            {
                Name = "cjxl",
                Path = cjxl,
                Version = cjxlProbe?.Version ?? TryExtractCjxlVersion(cjxl, log),
                SimdFeatures = cjxlProbe?.DetectedFeatures ?? TryExtractCjxlSimd(cjxl, log),
                IsAvailable = CjxlService.IsAvailable
            });

            // djxl
            var djxl = DjxlService.DetectedPath;
            results.Add(new ToolCapability
            {
                Name = "djxl",
                Path = djxl,
                Version = ProbeAndVersion(djxl, log),
                IsAvailable = DjxlService.IsAvailable
            });

            // cjpegli
            var cjpegli = CjpegliService.DetectedPath;
            results.Add(new ToolCapability
            {
                Name = "cjpegli",
                Path = cjpegli,
                Version = ProbeCjpegliVersion(cjpegli, log),
                IsAvailable = CjpegliService.IsAvailable
            });

            // exiftool
            var et = ExifToolService.DetectedPath;
            results.Add(new ToolCapability
            {
                Name = "exiftool",
                Path = et,
                Version = ProbeAndVersion(et, log),
                IsAvailable = ExifToolService.IsAvailable
            });

            // JxrEncApp
            var jxr = JxrService.DetectedPath;
            results.Add(new ToolCapability
            {
                Name = "JxrEncApp",
                Path = jxr,
                Version = ProbeAndVersion(jxr, log),
                IsAvailable = JxrService.IsAvailable
            });

            // avifenc — ⚠ P7f：面板展示的可用性必须与实际执行同源，否则会出现“面板说可用、队列却默默改路径”。
            // 这里曾经是第四份拷贝：它走 FindInArtifactsOrPlan()，额外包含“去扩展名”兼底
            // ⇒ 若 PLAN 里只有无后缀的 `avifenc`，面板会报可用而 Windows 下根本不可执行（反向假报）。
            // 现在统一用全局唯一定义 PlatformServices.ResolveAvifencPath()。
            var avifenc = PlatformServices.ResolveAvifencPath();
            results.Add(new ToolCapability
            {
                Name = "avifenc",
                Path = avifenc,
                Version = ProbeAndVersion(avifenc ?? "", log),   // ⚠ 必须在 IsAvailable 之前：本行顺带记录启动性
                // ⚠ `!= null` 不够 —— 见 `ProbeAndVersion` 的注释：起不来的 exe 也必须算不可用（面板与执行同源）。
                IsAvailable = avifenc != null && !PlatformServices.IsToolUnlaunchable(avifenc)
            });

            // dngtool — DNG 1.7 JXL 解码/编码 (LibRaw + Adobe DNG SDK)
            var dngtool = AppSettingsService.Current.DngToolPath;
            if (string.IsNullOrWhiteSpace(dngtool) || !File.Exists(dngtool))
                dngtool = FindInArtifactsOrPlan(PlatformServices.DngToolName);
            results.Add(new ToolCapability
            {
                Name = "dngtool",
                Path = File.Exists(dngtool) ? dngtool : null,
                Version = ProbeAndVersion(dngtool, log),          // ⚠ 同上：先探测并记录，再判可用性
                IsAvailable = File.Exists(dngtool) && !PlatformServices.IsToolUnlaunchable(dngtool)
            });

            return results;
        }

        /// <summary>在 artifacts 目录和 PLAN 文件夹中查找工具</summary>
        private static string? FindInArtifactsOrPlan(string toolFileName)
        {
            // 1) 用户指定的 artifacts 目录
            var artifactsDir = AppSettingsService.Current.WindowsArtifactsDir;
            if (!string.IsNullOrWhiteSpace(artifactsDir))
            {
                var p = Path.Combine(artifactsDir, toolFileName);
                if (File.Exists(p)) return p;
            }
            // 2) PLAN 便携文件夹（试完整名 + 去扩展名）
            var planFound = PlatformServices.TryFindInPlanFolder(toolFileName);
            if (planFound == null)
            {
                var nameNoExt = Path.GetFileNameWithoutExtension(toolFileName);
                if (nameNoExt != toolFileName)
                    planFound = PlatformServices.TryFindInPlanFolder(nameNoExt);
            }
            if (planFound != null) return planFound;
            // 3) ffmpeg 同目录
            var ffDir = AppSettingsService.Current.FfmpegDir;
            if (!string.IsNullOrWhiteSpace(ffDir))
            {
                var p = Path.Combine(ffDir, toolFileName);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // ── 每工具专用版本探测 ──

        private static string? ProbeAndVersion(string? path, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var probe = ProbeExecutable(path, 2000, log);
            // ⚠ 顺手把「起得来 / 起不来」记进 PlatformServices 的启动性备忘录（2026-09-23）。
            //   动机：`avifenc` / `dngtool` 这两个**没有独立 Service** 的工具，可用性一直只看
            //   `!= null` / `File.Exists` ⇒ 落进 WindowsApps 那种 ACL 拒绝启动的 exe 时，
            //   面板报 ✅、队列却 `InvalidApplication` 失败（且已产出的成品会被半成品清理删掉）。
            //   这里记录是**零额外开销**的：探测本来就在上一行跑了，只是原先把结论丢掉只留 Version。
            //   ⚠ 只记 `LaunchFailed`（含"Process.Start 抛异常"与"被加载器当场杀掉"），
            //     **不记超时** —— `exiftool(-k).exe` 这类"打印完等你按键"的工具恒超时却确实能用。
            if (probe.LaunchFailed) PlatformServices.RecordToolUnlaunchable(path);
            else PlatformServices.RecordToolLaunchable(path);
            return probe.Version;
        }

        /// <summary>cjpegli 不支持 --version，需通过 stderr 提取版本</summary>
        /// <param name="log">日志回调（超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        private static string? ProbeCjpegliVersion(string? path, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path, Arguments = "-h",
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,   // P2-5：默认按控制台代码页解码，非 ASCII 输出会乱码
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                // ⚠ 先挂读取任务、再等退出（本仓范式，见 `FfmpegCommandBuilder.ProbeWithFfprobe`）：
                //   旧写法同步读在 `WaitForExit(2000)` **之前** ⇒ 超时（与超时后的 Kill）是死代码。
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(2000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    var tmsg = $"[probe] cjpegli 版本探测超时（2s）⇒ 已终止：{Path.GetFileName(path)}";
                    if (log != null) log(tmsg + "\n"); else System.Diagnostics.Trace.WriteLine(tmsg);
                    return null;
                }
                return ExtractVersionFromOutput(errTask.GetAwaiter().GetResult());
            }
            catch { return null; }
        }

        /// <summary>从 cjxl --version 输出中提取版本: "cjxl v0.11.2 332feb1 [AVX2,SSE2]"</summary>
        /// <param name="log">日志回调（超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        private static string? TryExtractCjxlVersion(string? path, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path, Arguments = "--version",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,   // P2-5：默认按控制台代码页解码，非 ASCII 输出会乱码
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                // ⚠ 先挂读取任务、再等退出（本仓范式）：旧写法同步读在 `WaitForExit(2000)` **之前**。
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(2000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    var tmsg = $"[probe] cjxl 版本探测超时（2s）⇒ 已终止：{Path.GetFileName(path)}";
                    if (log != null) log(tmsg + "\n"); else System.Diagnostics.Trace.WriteLine(tmsg);
                    return null;
                }
                var output = outTask.GetAwaiter().GetResult();
                // "cjxl v0.11.2 332feb1 [AVX2,SSE2]"
                var m = Regex.Match(output, @"v(\d+\.\d+\.\d+)");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>从 cjxl --version 输出中提取 SIMD 标签: [AVX2,SSE2]</summary>
        /// <param name="log">日志回调（超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        private static string? TryExtractCjxlSimd(string? path, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path, Arguments = "--version",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,   // P2-5：默认按控制台代码页解码，非 ASCII 输出会乱码
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                // ⚠ 先挂读取任务、再等退出（本仓范式）：旧写法同步读在 `WaitForExit(2000)` **之前**。
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(2000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    var tmsg = $"[probe] cjxl SIMD 标签探测超时（2s）⇒ 已终止：{Path.GetFileName(path)}";
                    if (log != null) log(tmsg + "\n"); else System.Diagnostics.Trace.WriteLine(tmsg);
                    return null;
                }
                var output = outTask.GetAwaiter().GetResult();
                var m = Regex.Match(output, @"\[([^\]]+)\]");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>从 ffmpeg 输出中提取版本</summary>
        /// <param name="log">日志回调（超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
        private static string? TryExtractFfmpegVersion(string? path, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path, Arguments = "-version",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,   // P2-5：默认按控制台代码页解码，非 ASCII 输出会乱码
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                // ⚠ 先挂读取任务、再等退出（本仓范式）：旧写法同步读在 `WaitForExit(3000)` **之前**。
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(3000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    var tmsg = $"[probe] ffmpeg 版本探测超时（3s）⇒ 已终止：{Path.GetFileName(path)}";
                    if (log != null) log(tmsg + "\n"); else System.Diagnostics.Trace.WriteLine(tmsg);
                    return null;
                }
                var output = outTask.GetAwaiter().GetResult();
                // "ffmpeg version git-2026-07-05-97cbffe917"
                var m = Regex.Match(output, @"ffmpeg version ([\w\.\-]+)");
                return m.Success ? m.Groups[1].Value : ExtractVersionFromOutput(output);
            }
            catch { return null; }
        }
    }
}
