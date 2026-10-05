using System;
using Tests.Shared;

namespace Tests.Diagnostics
{
    /// <summary>
    /// 子系统测试组：**diagnostics**（性能 / 自检 / 工具探测 / i18n）。
    /// <para>覆盖 mode：`bench` `peak` `peakmeasure` `psnr` `xcheck` `selftest` `tooldetect` `i18n`。</para>
    /// <para>独立运行：<c>Tests.Diagnostics.exe</c>（跑全组）、<c>Tests.Diagnostics.exe selftest</c>（只跑一个 mode）。</para>
    /// <para>输出契约与旧 `ServiceProbe` 逐字一致 ⇒ 既有门禁脚本无需改动。</para>
    /// </summary>
    internal static class Program
    {
        private const string Group = "diagnostics";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Harness.InitExternalTools();

            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            var known = TestGroups.ModesOf(Group);

            if (mode is "--list" or "-l")
            {
                foreach (var m in known) Console.WriteLine(m);
                return 0;
            }
            if (mode != "" && Array.IndexOf(known, mode) < 0)
            {
                Console.Error.WriteLine($"错误: 本组不认识 mode '{mode}'。可用: {string.Join(", ", known)}");
                return 2;
            }

            // ⚠ `peak` / `peakmeasure` / `xcheck` 需要文件参数（旧宿主同样如此）：
            //   裸跑时它们打 usage 并记 fail=1 —— 保持原样，不特判（否则与旧读数分叉）。
            var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

            if (mode == "" || mode == "bench") DiagnosticsGroup.RunBench(rest);
            if (mode == "" || mode == "peak") DiagnosticsGroup.RunPeak(rest);
            if (mode == "" || mode == "peakmeasure") DiagnosticsGroup.RunPeakMeasure(rest);
            if (mode == "" || mode == "psnr") DiagnosticsGroup.RunPsnr(rest);
            if (mode == "" || mode == "xcheck") DiagnosticsGroup.RunXcheck(rest);
            if (mode == "" || mode == "selftest") DiagnosticsGroup.RunSelfTest();
            if (mode == "" || mode == "tooldetect") DiagnosticsGroup.RunToolDetect(rest);
            if (mode == "" || mode == "i18n") DiagnosticsGroup.RunI18n();

            Harness.EmitProbeResult();   // ⚠ 必须是 `PROBE RESULT:` 口径（运行器与多个门禁按它判"跑没跑"）
            return Harness.ExitCode();
        }
    }
}
