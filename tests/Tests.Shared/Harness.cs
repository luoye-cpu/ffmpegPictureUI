using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Tests.Shared
{
    /// <summary>
    /// 测试宿主的**共享底座**（2026-10-05 物理拆分时抽出的唯一一份）。
    ///
    /// <para><b>为什么抽这一层</b>：拆分前三个宿主（ServiceProbe 577 KB / UiTestHost 238 KB /
    /// GainMapTestHost 98 KB）各自是单个巨型 <c>Program.cs</c>，且各自**复制**了一份
    /// Check/RunCapture/工具解析/夹具生成。物理拆成"一个子系统一个工程"后，若每组再各带一份，
    /// 就会有 N 份同名不同实现的判据 —— 本仓已有「两处独立常数漂移 2.7e-4」的血泪教训
    /// ⇒ helper **只此一份**。</para>
    ///
    /// <para><b>边界</b>：本类**不含任何对产品的断言**，只有：计数/输出格式、子进程调用、
    /// 工具链定位、夹具生成。断言一律留在各组里（各组自己决定判什么）。</para>
    ///
    /// <para><b>契约</b>：输出格式与退出码**与拆分前逐字一致** —— 既有 20+ 个门禁脚本
    /// 直接 grep <c>PASS n</c> / <c>FAIL n</c> / <c>SKIP</c> 并读退出码，不得改变。</para>
    /// </summary>
    public static class Harness
    {
        private static int _pass, _fail, _skip;

        /// <summary>通过计数。</summary>
        public static int Pass => _pass;
        /// <summary>失败计数。</summary>
        public static int Fail => _fail;
        /// <summary>跳过计数（不计入判据，但必须打印出来供人看）。</summary>
        public static int SkipCount => _skip;

        /// <summary>把计数清零（一次进程内跑多组时用；单组运行时无需调用）。</summary>
        public static void ResetCounters() { _pass = 0; _fail = 0; _skip = 0; }

        /// <summary>仓库根：由 <c>AppContext.BaseDirectory</c> 向上找 <c>publish/PLAN</c> 定位。</summary>
        public static string RepoRoot { get; private set; } = "";

        /// <summary>
        /// 从输出目录向上定位仓库内工具链（publish/PLAN），写入 AppSettings。
        /// 没这一步，依赖 ffmpeg(iccgen) / exiftool 的断言会"SKIP 开绿"，看板上看不出来。
        /// </summary>
        public static void InitExternalTools()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int up = 0; dir != null && up < 8; dir = dir.Parent, up++)
                {
                    var ff = Path.Combine(dir.FullName, "publish", "PLAN", "ffmpeg-full", "ffmpeg.exe");
                    if (!File.Exists(ff)) continue;
                    RepoRoot = dir.FullName;
                    var st = FfmpegGui.Services.AppSettingsService.Current;
                    st.FfmpegDirectory = Path.GetDirectoryName(ff);
                    var et = Path.Combine(dir.FullName, "publish", "PLAN", "exiftool", "exiftool.exe");
                    if (File.Exists(et)) st.ExifToolPath = et;
                    Console.WriteLine($"tools-root={dir.FullName}");
                    return;
                }
                Console.WriteLine("tools-root=(未找到 publish/PLAN，需外部工具的断言将 SKIP)");
            }
            catch (Exception ex) { Console.WriteLine($"tools-init 异常: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>判定并打印一行。格式 <c>PASS &lt;msg&gt;</c> / <c>FAIL &lt;msg&gt;</c>（与拆分前一致）。</summary>
        public static void Check(bool ok, string msg)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {msg}");
            if (ok) _pass++; else _fail++;
        }

        /// <summary>
        /// **只增计数、不打印**（2026-10-05 拆分时补）。
        /// <para><b>为什么必须有</b>：旧宿主里有若干探针在"给了用法/前置不满足"时**只**做
        /// `_fail++`（配一行自打的 `usage: …`），**不**经过 <see cref="Check"/>：
        /// <code>
        /// $ ServiceProbe.exe icc
        /// usage: ServiceProbe icc &lt;色彩空间名&gt;
        /// PROBE RESULT: pass=0 fail=1        (exit 1)
        /// </code>
        /// 若改写成 `Check(false, …)` 会**多打一行 `FAIL …`**、文本也不同 ⇒ 与旧读数分叉
        /// （门禁会 grep 这些行）。而各组建一份自己的 `_fail` 又会让
        /// <see cref="SummaryLine"/>/<see cref="ExitCode"/> 读到 0/0（汇总与计数脱钩）。
        /// ⇒ 唯一正确形态 = 计数只在 <see cref="Harness"/>，探针用本方法"只增不打印"。</para>
        /// <para>⚠ 命名：本方法**不能**叫 `Fail` —— 那个名字已被下面的只读属性占用
        /// （`public static int Fail =&gt; _fail;`，是 `Pass`/`Fail`/`SkipCount` 三元组之一），
        /// C# 不允许同类型上"方法 + 属性"同名（实测踩过 CS0102，整个 tests/* 一起构建失败）。</para>
        /// </summary>
        public static void FailOnly() => _fail++;

        /// <summary>只增 PASS 计数、不打印（与 <see cref="FailOnly()"/> 对称，供同类"自打一行"的探针用）。</summary>
        public static void PassOnly() => _pass++;

        /// <summary>只增 SKIP 计数、不打印。</summary>
        public static void SkipOnly() => _skip++;

        /// <summary>跳过一行（不计入 PASS/FAIL，但打印出来）。</summary>
        public static void Skip(string msg)
        {
            Console.WriteLine($"SKIP {msg}");
            _skip++;
        }

        /// <summary>打印标准汇总行（各宿主尾部的形状由调用方决定）。</summary>
        public static string SummaryLine() => $"PASS={_pass} FAIL={_fail}";

        /// <summary>打印含 skip 的汇总行。</summary>
        public static string SummaryLineWithSkip() => $"PASS={_pass} FAIL={_fail} SKIP={_skip}";

        /// <summary>
        /// **旧宿主口径的汇总行**：<c>PROBE RESULT: pass=n fail=m</c>（+ 有 skip 时 <c> skip=k</c>）。
        /// <para><b>⚠⚠ 子系统的入口必须打这一行，不能用 <see cref="SummaryLine"/> 代替</b> ——
        /// 本仓运行器 `_run-step3-gates.ps1:844/851` 用它判断探针**到底跑没跑**：
        /// <c>$sum -notmatch 'PROBE RESULT'</c> ⇒ 标记 <c>⚠未执行(未知 mode 或无 PROBE RESULT 汇总)</c>；
        /// 而 :857 的记账又读 <c>fail=[1-9]</c> 判失败。多个门禁脚本另有
        /// <c>[regex]::Match($o, 'PROBE RESULT: pass=(\d+) fail=(\d+)')</c> 直接解析本行
        /// （如 `verify-color-engine-xcheck.ps1`、`_probe-tool-detect.ps1`）⇒ 换格式会被判"未执行/未解析到"。</para>
        /// <para>⚠ 大小写与空格**逐字**照旧：`PROBE RESULT: pass=` 全小写、冒号后一个空格。</para>
        /// </summary>
        public static string ProbeResultLine()
            => _skip > 0
               ? $"PROBE RESULT: pass={_pass} fail={_fail} skip={_skip}"
               : $"PROBE RESULT: pass={_pass} fail={_fail}";

        /// <summary>打印 <see cref="ProbeResultLine"/> 并返回它（供入口一行收尾）。</summary>
        public static string EmitProbeResult()
        {
            var line = ProbeResultLine();
            Console.WriteLine(line);
            return line;
        }

        /// <summary>退出码：有失败 ⇒ 1，否则 0（与拆分前一致）。</summary>
        public static int ExitCode() => _fail > 0 ? 1 : 0;

        // ───────────────────── 子进程调用 ─────────────────────

        /// <summary>运行并**继承**输出（用于把工具输出直接打到宿主 stdout）。返回退出码。</summary>
        public static int RunCap(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };
                using var p = Process.Start(psi);
                p!.WaitForExit();
                return p.ExitCode;
            }
            catch (Exception ex) { Console.WriteLine($"RunCap 异常: {ex.Message}"); return -1; }
        }

        /// <summary>
        /// 运行并**捕获合并输出**（stdout+stderr）。<paramref name="redirectOut"/> 非空时把 stdout 另存该文件。
        /// ⚠ 合并取数只适用于"值确实可能在 stderr"的场景；只读 stdout 请用 <see cref="ExecOut"/>。
        /// </summary>
        public static string RunCapture(string exe, string args, string? redirectOut = null)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi)!;
                var so = p.StandardOutput.ReadToEnd();
                var se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (redirectOut != null) { try { File.WriteAllText(redirectOut, so); } catch { } }
                return so + "\n" + se;
            }
            catch (Exception ex) { Console.WriteLine($"RunCapture 异常: {ex.Message}"); return ""; }
        }

        /// <summary>
        /// **只读 stdout** 的子进程调用（对应第 56 条的"只读版"约定）。
        /// ⚠ 用本方法读工具值时，工具若把值打到 stderr 就会读空 ⇒ 只在确认值走 stdout 时用。
        /// </summary>
        public static string ExecOut(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi)!;
                var so = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();      // 必须排空，否则子进程可能被堵死
                p.WaitForExit();
                return so;
            }
            catch (Exception ex) { Console.WriteLine($"ExecOut 异常: {ex.Message}"); return ""; }
        }

        /// <summary>
        /// **二进制安全**地把子进程 stdout 落盘并返回其文本形式（2026-10-05 拆分时补）。
        /// <para><b>为什么必须有</b>：旧宿主的 `RunCapture(exe, args, redirectOut)` 用
        /// <c>FileStream.CopyTo</c> **按字节**把 stdout 写到 <paramref name="redirectOut"/>，
        /// 而调用方（如 `ProbeThumbnail`）随后读该文件做**字节数/哈希**判定 ——
        /// 典型案例是 <c>exiftool -b -ThumbnailImage</c> 输出的是**二进制图像**（含 0x00）。
        /// 若用 <c>File.WriteAllText</c> 会**破坏 0x00 字节**，判定随之失真。</para>
        /// <para>⇒ 本方法保持旧语义：**逐字节**写文件；返回值仍是"stdout+stderr 合并文本"
        /// （与旧 `RunCapture` 的返回值一致，供调用方按文本 grep）。</para>
        /// </summary>
        public static string RunCaptureBinary(string exe, string args, string? redirectOut = null)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                // ⚠ 必须先排空 stderr 再读 stdout（或反之）会在大输出时死锁 ⇒ 用异步读一条流。
                var errTask = p.StandardError.ReadToEndAsync();
                string? outFile = redirectOut;
                if (outFile != null)
                {
                    using (var fs = File.Create(outFile))
                        p.StandardOutput.BaseStream.CopyTo(fs);      // **逐字节**，不做编码转换
                }
                else
                {
                    p.StandardOutput.BaseStream.CopyTo(Stream.Null);
                }
                var err = errTask.GetAwaiter().GetResult();
                p.WaitForExit();
                var so = redirectOut != null && File.Exists(redirectOut)
                    ? Encoding.UTF8.GetString(File.ReadAllBytes(redirectOut))   // 仅用于"文本形式"返回
                    : "";
                return so + "\n" + err;
            }
            catch (Exception ex) { Console.WriteLine($"RunCaptureBinary 异常: {ex.Message}"); return ""; }
        }

        /// <summary>同步等待一个 Task&lt;int&gt; 并返回其退出码。</summary>
        public static int RunTask(System.Threading.Tasks.Task<int> t)
        {
            t.GetAwaiter().GetResult();
            return t.Result;
        }

        // ───────────────────── 工具链路径 ─────────────────────

        /// <summary>在 <c>publish/PLAN/&lt;sub&gt;</c> 下找第一个存在的可执行文件；找不到返回 ""。</summary>
        public static string FindTool(params string[] relativeParts)
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int up = 0; dir != null && up < 8; dir = dir.Parent, up++)
                {
                    // ⚠ 不用 LINQ/Concat 扩展：本库要保持"零依赖、零歧义"，
                    //   纯 Path.Combine + 逐段拼接最不容易在拆分过程中被改坏。
                    var p = dir.FullName;
                    p = Path.Combine(p, "publish");
                    p = Path.Combine(p, "PLAN");
                    foreach (var seg in relativeParts) p = Path.Combine(p, seg);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return "";
        }

        /// <summary>对应 app 二进制（被测物）的 Release 路径；不保证存在。</summary>
        public static string AppExeRelease
            => Path.Combine(RepoRoot == "" ? "." : RepoRoot,
                            "src", "FfmpegGui", "bin", "Release", "net11.0", "win-x64", "FfmpegGui.exe");

        /// <summary>SHA-256 前 16 位（用于"被测产物是哪一份"的可追溯性）。</summary>
        public static string Sha16(string path)
        {
            try
            {
                using var s = File.OpenRead(path);
                using var h = System.Security.Cryptography.SHA256.Create();
                var d = h.ComputeHash(s);
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(d[i].ToString("X2"));
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// SHA-256 **完整 64 位十六进制**（2026-10-05 拆分时补）。
        /// <para><b>为什么必须有</b>：旧宿主里部分探针（如 `ProbeThumbnail` 的 T8/T11/T12/T17/T19）
        /// 用的是**全 64 字符**哈希做逐字节同一性判定，而 `Harness` 原先只有 <see cref="Sha16"/>（16 字符）。
        /// 若各处自写一份 SHA，就会出现"同一概念两种实现"的漂移（本仓已有前车之鉴）
        /// ⇒ 完整哈希只此一份。<paramref name="path"/> 不可读时返回空串。</para>
        /// </summary>
        public static string Sha(string path)
        {
            try
            {
                using var s = File.OpenRead(path);
                using var h = System.Security.Cryptography.SHA256.Create();
                var d = h.ComputeHash(s);
                var sb = new StringBuilder(d.Length * 2);
                foreach (var b in d) sb.Append(b.ToString("X2"));
                return sb.ToString();
            }
            catch { return ""; }
        }
    }
}
