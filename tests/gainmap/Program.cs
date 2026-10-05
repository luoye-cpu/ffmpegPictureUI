using System;
using Tests.Shared;

namespace Tests.GainMap
{
    /// <summary>
    /// 子系统测试入口：**gainmap**（增益图 / Ultra HDR）。
    ///
    /// <para>用法：
    /// <code>
    ///   Tests.GainMap.exe              # 跑本组全部用例
    ///   Tests.GainMap.exe oog          # 只跑指定 mode（与旧 ServiceProbe 同名，便于迁移习惯）
    ///   Tests.GainMap.exe --list       # 列出本组覆盖的 mode
    /// </code></para>
    ///
    /// <para><b>输出契约</b>与拆分前逐字一致：每行 <c>PASS &lt;msg&gt;</c> / <c>FAIL &lt;msg&gt;</c> /
    /// <c>SKIP &lt;msg&gt;</c>，尾部 <c>PASS=n FAIL=m</c>，退出码 0/1。</para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Harness.InitExternalTools();

            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            var known = TestGroups.ModesOf("gainmap");
            if (mode is "--list" or "-l") { foreach (var m in known) Console.WriteLine(m); return 0; }
            // 未知 mode **响亮报错**（fail-closed，不静默跑全组 —— 本仓忌"静默降级"）。
            if (mode != "" && Array.IndexOf(known, mode) < 0)
            {
                Console.Error.WriteLine($"错误: 本组不认识 mode '{mode}'。可用: {string.Join(", ", known)}");
                return 2;
            }

            if (mode == "" || mode == "gainmap") GainMapGroup.RunGainMap(args);
            if (mode == "" || mode == "ultrahdr") GainMapGroup.RunUltraHdr(args);
            if (mode == "" || mode == "gainmap-isobmff") GainMapGroup.RunGainMapIsoBmff(args);
            if (mode == "" || mode == "oog") GainMapGroup.RunOog();
            if (mode == "" || mode == "avifgm") GainMapGroup.RunAvifGainMap();

            // ⚠ 必须用 EmitProbeResult()（旧宿主口径 `PROBE RESULT: pass=n fail=m`），**不能**用
            //   `SummaryLine()`（`PASS=n FAIL=m`）：运行器 `_run-step3-gates.ps1:844/851` 以
            //   `$sum -notmatch 'PROBE RESULT'` 判定探针"到底跑没跑"⇒ 换格式会被标成
            //   「⚠未执行(未知 mode 或无 PROBE RESULT 汇总)」，且 :857 的失败记账（读 `fail=[1-9]`）
            //   随之失效；另有门禁直接 regex 解析本行（verify-color-engine-xcheck.ps1:80 等）。
            Harness.EmitProbeResult();
            return Harness.ExitCode();
        }
    }
}
