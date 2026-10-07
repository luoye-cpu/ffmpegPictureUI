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

            // ⚠ `peak` / `peakmeasure` / `xcheck` / `psnr` 需要文件参数（旧宿主同样如此）：
            //   裸跑时它们打 usage 并记 fail=1 —— 保持原样，不特判（否则与旧读数分叉）。
            //
            // ⚠⚠ 2026-10-07 修复（L-2）：**必须传完整 `args`，不能传 `args[1..]`**。
            //   本组的方法体是从旧宿主 `ServiceProbe/Program.cs` **逐字抽出**的，它们的下标约定
            //   与旧宿主一致 —— `args[0]` 是 mode 名、`args[1]` 起才是实参（见下方各 Probe 的
            //   `args.Length < N` / `args[1]` / `args[2]` 用法，如 `DiagnosticsGroup.ProbePsnr`
            //   的 `args.Length < 3` 与 `args[1]`/`args[2]`）。
            //   原写法 `args[1..]` 把实参**整体前移一格**塞进下标 0 ⇒ 每个用法都比预期少 1 个参数
            //   ⇒ **给足实参也照样打 `usage:` + fail=1**。实机实测（2026-10-07）：
            //     · `Tests.Diagnostics.exe psnr <a> <b>`        ⇒ `usage: …` + `pass=0 fail=1`（错）
            //     · `Tests.Diagnostics.exe psnr psnr <a> <b>`   ⇒ `pass=2 fail=0`（多塞占位才对）
            //   ⇒ 与 `docs/TESTING.md` §6 第 24 条**不是**同一件事：第 24 条讲「**不给**实参 ⇒ usage」
            //     （正确契约）；本条是「**给足了**仍 usage」= 拆分引入的回归。
            //   ⚠ 同理修了 `tests/engine/Program.cs` 的 `gamut`。
            if (mode == "" || mode == "bench") DiagnosticsGroup.RunBench(args);
            if (mode == "" || mode == "peak") DiagnosticsGroup.RunPeak(args);
            if (mode == "" || mode == "peakmeasure") DiagnosticsGroup.RunPeakMeasure(args);
            if (mode == "" || mode == "psnr") DiagnosticsGroup.RunPsnr(args);
            if (mode == "" || mode == "xcheck") DiagnosticsGroup.RunXcheck(args);
            if (mode == "" || mode == "selftest") DiagnosticsGroup.RunSelfTest();
            if (mode == "" || mode == "tooldetect") DiagnosticsGroup.RunToolDetect(args);
            if (mode == "" || mode == "i18n") DiagnosticsGroup.RunI18n();

            Harness.EmitProbeResult();   // ⚠ 必须是 `PROBE RESULT:` 口径（运行器与多个门禁按它判"跑没跑"）
            return Harness.ExitCode();
        }
    }
}
