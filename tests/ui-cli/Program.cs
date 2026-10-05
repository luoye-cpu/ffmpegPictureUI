using System;
using Tests.Shared;
using UiHostShared;

namespace Tests.UiCli
{
    /// <summary>
    /// 子系统测试入口：**ui-cli**（P1-E CLI 取值口径严格化 + 任务 B/C 回归锁 /
    /// 启动期检测面板的「槽位 = 工具 = 日志行」不变式）。
    ///
    /// <para>用法：
    /// <code>
    ///   Tests.UiCli.exe                             # 跑本组全部 mode
    ///   Tests.UiCli.exe ui-cli-strict               # 只跑指定 mode
    ///   Tests.UiCli.exe --list                      # 列出本组覆盖的 mode
    ///   Tests.UiCli.exe &lt;素材目录&gt; [mode]           # 可选覆盖 tests/output/sources
    /// </code></para>
    ///
    /// <para><b>输出契约</b>与拆分前的 UiTestHost 逐字一致（见 <c>ui-core/Program.cs</c> 的长注释）。</para>
    ///
    /// <para>⚠⚠ <b>顺序不变式（本工程的核心约束）</b>：<c>GroupP_StartupDetectLog</c> 必须
    /// **紧跟窗口启动**执行，不能排在别的组之后。原因（旧宿主注释实测记录）：
    /// 中间任何别的组都会把日志面板清空 —— 典型是 <c>GroupN</c> 的 N6 负控写
    /// <c>logBox.Text = ""</c> 只留本次产生的日志 ⇒ 放末尾时那 7 行 <c>[detect]</c> 早被抹掉，
    /// 本组会一路轮询到 45 s 超时（实测把宿主从 6 s 拖到 79 s）。</para>
    ///
    /// <para><b>拆分后如何保住这条性质</b>（三重）：</para>
    /// <list type="number">
    /// <item><description><b>P 是分派顺序里的第一位</b>：全组运行时 P 紧跟
    /// <c>UiHost.Bootstrap()</c> 执行，中间**没有任何**先跑的组。</description></item>
    /// <item><description><b>P 也可单独运行</b>：<c>ui-startup-detect</c> 时除 Bootstrap 外
    /// 什么都不跑 ⇒ "紧跟启动"这条性质在单 mode 下**更强**（这正是拆分的收益）。</description></item>
    /// <item><description><b>M 排在 P 之后</b>：M 自身以 <c>SweepReset()</c> 开头、不依赖 P 留下的
    /// 状态；反过来 P 也不依赖 M。故「P 先、M 后」既不破坏 P 的启动窗口，也不影响 M 的可复现性。</description></item>
    /// </list>
    /// <para>⚠ <b>什么会坏</b>：若把 M（或任何别组）插到 P 之前，P 的 7 行 <c>[detect]</c>
    /// 就可能已被清空/污染，P1–P5 会退化成"测量失败"或一路轮询到 45 s 超时。
    /// 故本工程把顺序**写死在代码里并在此显式记录**，避免后人调序。</para>
    /// </summary>
    internal static class Program
    {
        /// <summary>组表名（= <see cref="TestGroups"/> 的键，也是 <c>tests/&lt;组名&gt;</c> 目录名）。</summary>
        private const string Group = "ui-cli";

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

                // ── P **紧跟启动**（顺序不变式，见类注释）───────────────────────────
                // ⚠ 全组运行时这里**必须**是第一个被执行的组；**不得**在它之前插入
                //   任何 SetInput / SweepReset / 其它组调用 —— 那会改变
                //   「启动 ⇒ 立刻观察启动期日志」这条被测语义本身。
                if (mode == "" || mode == "ui-startup-detect") uicliGroup.RunP();

                // M 在 P 之后：M 自足（SweepReset 开头），且不清日志面板。
                // 这里的 SetInput 只服务 M（P 已跑完，不再读输入路径）。
                if (mode == "" || mode == "ui-cli-strict")
                {
                    UiHost.SetInput(System.IO.Path.Combine(UiHost.SrcDir, "src_8bit.png"));
                    uicliGroup.RunM();
                }
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
