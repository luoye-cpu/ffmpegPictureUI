using System;
using Tests.Shared;

namespace Tests.Engine
{
    /// <summary>
    /// 子系统测试入口：**engine**（自研引擎与像素内核）。
    ///
    /// <para>用法：
    /// <code>
    ///   Tests.Engine.exe            # 跑本组全部用例
    ///   Tests.Engine.exe gamutfit   # 只跑指定 mode（与旧 ServiceProbe 同名，便于迁移习惯）
    ///   Tests.Engine.exe --list     # 列出本组覆盖的 mode
    /// </code></para>
    ///
    /// <para><b>输出契约</b>与拆分前逐字一致：每行 <c>PASS &lt;msg&gt;</c> / <c>FAIL &lt;msg&gt;</c> /
    /// <c>SKIP &lt;msg&gt;</c>，尾部 <c>PASS=n FAIL=m</c>，退出码 0/1 —— 既有门禁脚本无需改动。</para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Harness.InitExternalTools();

            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            if (mode is "--list" or "-l")
            {
                foreach (var m in TestGroups.ModesOf("engine")) Console.WriteLine(m);
                return 0;
            }

            // 本组已知 mode ⇒ 只跑它；空 ⇒ 跑全组。未知 mode **响亮报错**（fail-closed，
            // 不静默跑全组 —— 本仓忌"静默降级"）。
            var known = TestGroups.ModesOf("engine");
            if (mode != "" && Array.IndexOf(known, mode) < 0)
            {
                Console.Error.WriteLine($"错误: 本组不认识 mode '{mode}'。可用: {string.Join(", ", known)}");
                return 2;
            }

            if (mode == "" || mode == "gamutfit") EngineGroup.RunGamutFit();
            // ⚠ 2026-10-05 审查补：这三个 mode 组表早已声明，但当初**漏了分派**
            //   ⇒ 独立跑时**静默 0 条断言**（旧宿主分别是 engine 35/0、simdswitch 28/0、
            //     gamut 需参数故 0/0）。属"声明了却没实现"，已补齐。
            //
            // ⚠⚠ 2026-10-07 修复（L-2）：**必须传完整 `args`，不能传 `args[1..]`**。
            //   `ProbeGamut`（`GamutFit.cs:670`）是从旧宿主**逐字抽出**的，下标约定与旧宿主一致
            //   （`args[0]` = mode 名、`args[1]` = jpeg、`args[2]` = expectedToken）。
            //   原写法 `args[1..]` 使实参整体前移一格 ⇒ 给足实参也打 `usage:` + fail=1。
            //   实测（2026-10-07）：`Tests.Engine.exe gamut <jpg>` ⇒ usage / fail=1（错）；
            //   `Tests.Engine.exe gamut gamut <jpg>` ⇒ 真跑（对）。
            //   ⚠ `engine`/`simdswitch` 本就无参，保持不传。
            if (mode == "" || mode == "engine") EngineGroup.RunEngineAb();
            if (mode == "" || mode == "simdswitch") EngineGroup.RunSimdSwitch();
            if (mode == "" || mode == "gamut") EngineGroup.RunGamut(args);

            Harness.EmitProbeResult();   // ⚠ 必须是 `PROBE RESULT:` 口径（运行器与多个门禁按它判"跑没跑"）
            return Harness.ExitCode();
        }
    }
}
