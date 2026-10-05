using System;
using Tests.Shared;
using UiHostShared;

namespace Tests.UiSweep
{
    /// <summary>
    /// 子系统测试入口：**ui-sweep**（UI 控件穷举 → 命令串 token 级断言 / UI→命令 参数传递缺陷锁）。
    ///
    /// <para>用法：
    /// <code>
    ///   Tests.UiSweep.exe                             # 跑本组全部 mode
    ///   Tests.UiSweep.exe ui-sweep        # 只跑指定 mode
    ///   Tests.UiSweep.exe --list                      # 列出本组覆盖的 mode
    ///   Tests.UiSweep.exe &lt;素材目录&gt; [mode]           # 可选覆盖 tests/output/sources
    /// </code></para>
    ///
    /// <para><b>输出契约</b>与拆分前的 UiTestHost 逐字一致：每行 <c>[PASS] name</c> /
    /// <c>[FAIL] name :: detail</c>，尾部 <c>===== UI 测试汇总: N PASS / M FAIL =====</c>
    /// 与逐条 <c>  FAILED -&gt; …</c>，退出码 = FAIL 条数；另打 <c>PROBE RESULT: pass=n fail=m</c>
    /// 供仓库运行器识别。窗口建立与全部 GUI 助手来自 <see cref="UiHost"/>（单一真值）。</para>
    /// </summary>
    internal static class Program
    {
        /// <summary>组表名（= <see cref="TestGroups"/> 的键，也是 <c>tests/&lt;组名&gt;</c> 目录名）。</summary>
        private const string Group = "ui-sweep";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // ── fail-fast：PLAN 目录（与拆分前的 UiTestHost 同一契约，退出码 98）──
            if (!UiHost.CheckPlanDir()) return 98;

            var known = TestGroups.ModesOf(Group);
            var a = UiHost.ParseArgs(args, known);

            if (a.Kind == UiHost.UiArgsKind.List)
            {
                foreach (var m in known) Console.WriteLine(m);
                return 0;
            }
            // 未知参数 ⇒ **响亮报错**（fail-closed，不静默跑全组 —— 本仓忌"静默降级"）
            if (a.Kind == UiHost.UiArgsKind.Unknown)
            {
                Console.Error.WriteLine($"错误: 本组不认识参数 '{a.Unknown}'。可用 mode: {string.Join(", ", known)}");
                return 2;
            }

            Harness.InitExternalTools();
            var mode = a.Mode;

            try
            {
                UiHost.Bootstrap(a.SrcDirOverride);

                // J/L 两组都以 SweepReset() 开头（它内部会 SetInput(src_8bit.png)，
                // 且把 UI 复位到「可复现基线」）⇒ 它们本来就**自足**，可独立运行。
                // 这里仍统一先设输入：与旧宿主 Main 的执行环境保持一致，避免"起点不同"。
                UiHost.SetInput(System.IO.Path.Combine(UiHost.SrcDir, "src_8bit.png"));

                if (mode == "" || mode == "ui-sweep") uisweepGroup.RunJ();
                if (mode == "" || mode == "ui-param-defects") uisweepGroup.RunL();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 99;
            }

            UiHost.Finish();
            Harness.EmitProbeResult();
            return Harness.ExitCode();
        }
    }
}
