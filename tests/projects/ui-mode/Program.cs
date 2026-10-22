using System;
using Tests.Shared;
using UiHostShared;

namespace Tests.UiMode
{
    /// <summary>
    /// 子系统测试入口：**ui-mode**（UI 模式轴：简洁模式切换 / 窗口后台可见性 / GUI 驱动端到端真实转换 / GainMap 开关不得冲掉目标色域·位深·质量）。
    ///
    /// <para>用法：
    /// <code>
    ///   Tests.UiMode.exe                             # 跑本组全部 mode
    ///   Tests.UiMode.exe ui-simple-mode        # 只跑指定 mode
    ///   Tests.UiMode.exe --list                      # 列出本组覆盖的 mode
    ///   Tests.UiMode.exe &lt;素材目录&gt; [mode]           # 可选覆盖 tests/output/sources
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
        private const string Group = "ui-mode";

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

                // ⚠ 复现旧宿主 `Main` 的同一个前置（见 ui-core/Program.cs 的长注释）：
                //   本组 G 组虽然自己 SetInput，但 E/F/O 读的是窗口当前状态，统一先设输入
                //   与旧宿主 `SetInput(src_8bit.png)` 之后才进各组**完全一致**。
                UiHost.SetInput(System.IO.Path.Combine(UiHost.SrcDir, "src_8bit.png"));

                if (mode == "" || mode == "ui-simple-mode") uimodeGroup.RunE();
                if (mode == "" || mode == "ui-background-visibility") uimodeGroup.RunF();
                if (mode == "" || mode == "ui-end-to-end") uimodeGroup.RunG();
                if (mode == "" || mode == "ui-gainmap-toggle") uimodeGroup.RunO();
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
