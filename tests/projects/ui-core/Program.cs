using System;
using Tests.Shared;
using UiHostShared;

namespace Tests.UiCore
{
    /// <summary>
    /// 子系统测试入口：**ui-core**（输出格式→命令 / 参数传递 / 面板显隐联动 /
    /// 编码器下拉联动 / 预设保存·载入往返 ColorStrategy）。
    ///
    /// <para>用法：
    /// <code>
    ///   Tests.UiCore.exe                             # 跑本组全部 mode
    ///   Tests.UiCore.exe ui-parameter-passing        # 只跑指定 mode
    ///   Tests.UiCore.exe --list                      # 列出本组覆盖的 mode
    ///   Tests.UiCore.exe &lt;素材目录&gt; [mode]           # 可选覆盖 tests/output/sources
    /// </code></para>
    ///
    /// <para><b>输出契约</b>与拆分前的 UiTestHost **逐字一致**：每行 <c>[PASS] name</c> /
    /// <c>[FAIL] name :: detail</c>，尾部 <c>===== UI 测试汇总: N PASS / M FAIL =====</c>
    /// 与逐条 <c>  FAILED -&gt; …</c>，退出码 = FAIL 条数；另打一行
    /// <c>PROBE RESULT: pass=n fail=m</c> 供仓库运行器识别（<c>_run-step3-gates.ps1:844</c>）。</para>
    ///
    /// <para><b>窗口引导</b>：与其它 4 个 UI 组工程**共用** <see cref="UiHost"/> 的窗口建立路径
    /// 与全部 GUI 助手。⚠ 必须调 <c>SetInput(src_8bit.png)</c>：本组 A/B/C/D 四个组自身
    /// **不设输入路径**，而 <c>MainWindow.RegenerateCommand</c> 在 <c>_inputPath</c> 为空时
    /// **提前返回**（<c>MainWindow.xaml.cs:1415</c>）⇒ 不设会得到空命令串、断言整片转红。
    /// 旧宿主是在 <c>Main</c> 里跑完 P 组后统一设的，拆分后由本入口复现同一前提。</para>
    /// </summary>
    internal static class Program
    {
        /// <summary>组表名（= <see cref="TestGroups"/> 的键，也是 <c>tests/&lt;组名&gt;</c> 目录名）。</summary>
        private const string Group = "ui-core";

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

                // ⚠ 复现旧宿主 `Main` 的同一个前置（见类注释）：A/B/C/D 依赖 _inputPath。
                UiHost.SetInput(System.IO.Path.Combine(UiHost.SrcDir, "src_8bit.png"));

                if (mode == "" || mode == "ui-format-command") uicoreGroup.RunA();
                if (mode == "" || mode == "ui-parameter-passing") uicoreGroup.RunB();
                if (mode == "" || mode == "ui-panel-visibility") uicoreGroup.RunC();
                if (mode == "" || mode == "ui-encoder-linkage") uicoreGroup.RunD();
                if (mode == "" || mode == "ui-preset-roundtrip") uicoreGroup.RunN();
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
