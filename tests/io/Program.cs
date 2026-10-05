using System;
using Tests.Shared;

namespace Tests.Io
{
    /// <summary>
    /// 子系统测试组：**io**（进程 / 管道 / IPC）。
    /// <para>覆盖 mode：`ipc` `procstreams`（见 <see cref="TestGroups"/>）。</para>
    /// <para>独立运行：<c>Tests.Io.exe</c>（跑全组）、<c>Tests.Io.exe ipc</c>（只跑一个 mode）。</para>
    /// <para>输出契约与旧 `ServiceProbe` 逐字一致（`PASS n`/`FAIL n`/`SKIP`、尾部 `PASS=n FAIL=m`、退出码 0/1）
    /// ⇒ 既有门禁脚本无需改动。</para>
    /// </summary>
    internal static class Program
    {
        private const string Group = "io";

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
            // 未知 mode **响亮报错**（fail-closed，不静默跑全组 —— 本仓忌"静默降级"）。
            if (mode != "" && Array.IndexOf(known, mode) < 0)
            {
                Console.Error.WriteLine($"错误: 本组不认识 mode '{mode}'。可用: {string.Join(", ", known)}");
                return 2;
            }

            if (mode == "" || mode == "ipc") IoGroup.RunIpc();
            if (mode == "" || mode == "procstreams") IoGroup.RunProcStreams();

            Harness.EmitProbeResult();   // ⚠ 必须是 `PROBE RESULT:` 口径（运行器与多个门禁按它判"跑没跑"）
            return Harness.ExitCode();
        }
    }
}
