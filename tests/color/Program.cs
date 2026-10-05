using System;
using Tests.Shared;

namespace Tests.Color
{
    /// <summary>
    /// 子系统测试入口：**color**（色彩基元与数学：矩阵 / 曲线 / OOTF / HLG / BT.2446 / PQ /
    /// 与 zimg 对拍 / ICC 度量识别）。
    ///
    /// <para>用法：
    /// <code>
    ///   Tests.Color.exe            # 跑本组全部用例
    ///   Tests.Color.exe matrix     # 只跑指定 mode（与旧 ServiceProbe 同名，便于迁移习惯）
    ///   Tests.Color.exe --list     # 列出本组覆盖的 mode
    /// </code></para>
    ///
    /// <para><b>输出契约</b>与拆分前逐字一致：每行 <c>PASS &lt;msg&gt;</c> / <c>FAIL &lt;msg&gt;</c> /
    /// <c>SKIP &lt;msg&gt;</c>，尾部 <c>PROBE RESULT: pass=n fail=m</c>（有跳过时追加 <c> skip=k</c>），
    /// 退出码 0/1 —— 既有门禁脚本无需改动。</para>
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
                foreach (var m in TestGroups.ModesOf("color")) Console.WriteLine(m);
                return 0;
            }

            // 本组已知 mode ⇒ 只跑它；空 ⇒ 跑全组。未知 mode **响亮报错**（fail-closed，
            // 不静默跑全组 —— 本仓忌"静默降级"）。
            var known = TestGroups.ModesOf("color");
            if (mode != "" && Array.IndexOf(known, mode) < 0)
            {
                Console.Error.WriteLine($"错误: 本组不认识 mode '{mode}'。可用: {string.Join(", ", known)}");
                return 2;
            }

            if (mode == "" || mode == "curve") ColorGroup.RunCurve();
            if (mode == "" || mode == "matrix") ColorGroup.RunMatrix();
            if (mode == "" || mode == "ootf") ColorGroup.RunOotf();
            if (mode == "" || mode == "hlgwire") ColorGroup.RunHlgWire();
            if (mode == "" || mode == "bt2446") ColorGroup.RunBt2446();
            if (mode == "" || mode == "pq") ColorGroup.RunPq();
            if (mode == "" || mode == "colormath") ColorGroup.RunColorMath();
            if (mode == "" || mode == "iccname") ColorGroup.RunIccName();

            // ⚠ 汇总行必须是**仓库运行器要求的字面形状** `PROBE RESULT: pass=n fail=m`
            //   （见 tests/scripts/_run-step3-gates.ps1:844/851/857 与各 verify-*.ps1 的
            //   `PROBE RESULT: pass=(\d+) fail=(\d+)`）。写成 `PASS=n FAIL=m` 会被判
            //   「⚠未执行(未知 mode 或无 PROBE RESULT 汇总)」⇒ 本组的失败**不再被计数**。
            Harness.EmitProbeResult();
            return Harness.ExitCode();
        }
    }
}
