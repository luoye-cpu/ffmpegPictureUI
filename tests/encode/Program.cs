using System;
using Tests.Shared;

namespace Tests.Encode
{
    /// <summary>
    /// 子系统测试入口：**encode**（编码参数与产物）。
    ///
    /// <para>用法：
    /// <code>
    ///   Tests.Encode.exe              # 跑本组全部用例
    ///   Tests.Encode.exe thumbnail    # 只跑指定 mode（与旧 ServiceProbe 同名，便于迁移习惯）
    ///   Tests.Encode.exe --list       # 列出本组覆盖的 mode
    /// </code></para>
    ///
    /// <para><b>输出契约</b>与拆分前逐字一致：每行 <c>PASS &lt;msg&gt;</c> / <c>FAIL &lt;msg&gt;</c> /
    /// <c>SKIP &lt;msg&gt;</c>，尾部 <c>PASS=n FAIL=m</c>，退出码 0/1。</para>
    ///
    /// <para><b>`emit-utf8` 子进程端</b>：<c>encoding</c> 探针会把 <b>本 exe 自己</b>当子进程
    /// （<c>Environment.ProcessPath</c> + <c>emit-utf8 &lt;text&gt;</c>）来验证"产出字节 = UTF-8"。
    /// 该分支拆分前在旧宿主 <c>Main</c> 里，属**载体**而非 probe ⇒ 必须一并搬来，
    /// 否则子进程会走"未知 mode"分支 ⇒ 三条断言全红（且原因是拆分，不是产品缺陷）。</para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // ── P2-5 行为探针的子进程端：把 UTF-8 字节原样写到两条流上 ──
            // 直接写 Console.OpenStandard*()，绕开 Console 编码设置，保证「产出的字节 = UTF-8」
            // 可被父进程逐字节验证。放在 InitExternalTools 之前：子进程要快，且不依赖外部工具。
            if (args.Length > 0 && args[0].Equals("emit-utf8", StringComparison.OrdinalIgnoreCase))
            {
                var payload = "OUT:" + (args.Length > 1 ? args[1] : "");
                Console.OpenStandardOutput().Write(System.Text.Encoding.UTF8.GetBytes(payload));
                Console.OpenStandardOutput().Flush();
                var errPayload = "ERR:" + (args.Length > 1 ? args[1] : "");
                Console.OpenStandardError().Write(System.Text.Encoding.UTF8.GetBytes(errPayload));
                Console.OpenStandardError().Flush();
                return 0;
            }

            Harness.InitExternalTools();

            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            var known = TestGroups.ModesOf("encode");
            if (mode is "--list" or "-l") { foreach (var m in known) Console.WriteLine(m); return 0; }
            // 未知 mode **响亮报错**（fail-closed，不静默跑全组 —— 本仓忌"静默降级"）。
            if (mode != "" && Array.IndexOf(known, mode) < 0)
            {
                Console.Error.WriteLine($"错误: 本组不认识 mode '{mode}'。可用: {string.Join(", ", known)}");
                return 2;
            }

            if (mode == "" || mode == "encoding") EncodeGroup.RunProcEncoding();
            if (mode == "" || mode == "bitdepth") EncodeGroup.RunBitDepth(args);
            if (mode == "" || mode == "png3") EncodeGroup.RunPng3(args);
            if (mode == "" || mode == "thumbnail") EncodeGroup.RunThumbnail(args);

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
