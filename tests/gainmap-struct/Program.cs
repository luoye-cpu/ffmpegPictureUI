using System;
using Tests.Shared;

namespace Tests.GainMapStruct
{
    /// <summary>
    /// 子系统测试入口：**gainmap-struct**（GainMap 产物输出结构 / PNG 3.0 sBIT·cICP）。
    ///
    /// <para>用法：
    /// <code>
    ///   Tests.GainMapStruct.exe                 # 跑本组全部用例
    ///   Tests.GainMapStruct.exe gainmap-struct  # 只跑指定 mode
    ///   Tests.GainMapStruct.exe --list          # 列出本组覆盖的 mode
    /// </code></para>
    ///
    /// <para><b>输出契约</b>：明细行沿用旧宿主形态（<c>  ✅ msg</c> / <c>  ❌ msg</c>，
    /// <c>tests/scripts/verify-gainmap-host.ps1</c> 正是 grep 这些文本），尾部
    /// <c>═══ 汇总: 通过 N, 失败 M, 已知问题 K ═══</c> + <c>PROBE RESULT: pass=… fail=…</c>（运行器记账依赖后者），
    /// 退出码 <c>0/1</c>；未知 mode ⇒ <c>2</c>。</para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Harness.InitExternalTools();

            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            var known = TestGroups.ModesOf("gainmap-struct");
            if (mode is "--list" or "-l") { foreach (var m in known) Console.WriteLine(m); return 0; }
            // 未知 mode **响亮报错**（fail-closed，不静默跑全组 —— 本仓忌"静默降级"）。
            if (mode != "" && Array.IndexOf(known, mode) < 0)
            {
                Console.Error.WriteLine($"错误: 本组不认识 mode '{mode}'。可用: {string.Join(", ", known)}");
                return 2;
            }

            Console.WriteLine("═══ GainMap 输出结构验证（gainmap-struct）═══");
            Console.WriteLine($"工作目录: {Environment.CurrentDirectory}");
            Console.WriteLine($"输出目录: {GainMapStructGroup.OutputDir}");
            Console.WriteLine($"ffmpeg 可用: {!string.IsNullOrWhiteSpace(FfmpegGui.Services.AppSettingsService.Current.FfmpegPath)}");
            Console.WriteLine($"cjpegli 可用: {FfmpegGui.Services.CjpegliService.IsAvailable} ({FfmpegGui.Services.CjpegliService.DetectedPath})");
            Console.WriteLine($"exiftool 可用: {FfmpegGui.Services.ExifToolService.IsAvailable} ({FfmpegGui.Services.ExifToolService.DetectedPath})");

            if (mode == "" || mode == "gainmap-struct") GainMapStructGroup.RunStruct();

            Console.WriteLine();
            Console.WriteLine($"═══ 汇总: 通过 {Harness.Pass}, 失败 {Harness.Fail}, 已知问题 {GainMapStructGroup.KnownCount} ═══");
            foreach (var k in GainMapStructGroup.KnownList)
                Console.WriteLine($"  ⚠️ {k}");

            // ⚠ 必须用 EmitProbeResult()（旧宿主口径 `PROBE RESULT: pass=n fail=m`）：运行器
            //   `_run-step3-gates.ps1:844/851` 以 `$sum -notmatch 'PROBE RESULT'` 判定探针"到底跑没跑"，
            //   :857 的失败记账读 `fail=[1-9]`；另有门禁直接 regex 解析本行。
            Harness.EmitProbeResult();
            return Harness.ExitCode();
        }
    }
}
