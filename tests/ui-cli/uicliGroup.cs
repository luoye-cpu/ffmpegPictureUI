using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tests.Shared;
using UiHostShared;

namespace Tests.UiCli
{
    /// <summary>
    /// 子系统测试组：**ui-cli** —— 2026-10-05 从 UiTestHost 的单个 3500 行
    /// <c>Program.cs</c> 中物理拆出。组体**逐字**搬来（断言文本一字未改，门禁在 grep 它们）。
    /// <para><b>共享 GUI 底座</b>：窗口 <c>W</c>、<c>TempDir</c>、以及全部 GUI/token 助手
    /// 都在 UiHost（UiHostShared 工程）—— 本类只保留同名**转发包装**，
    /// 不复制实现。</para>
    /// </summary>
    internal static class uicliGroup
    {
        // ══════════════ 转发包装（**不复制实现**，一律转 UiHostShared.UiHost）══════════════
        // 为什么保留裸名包装：组体是**逐字**从旧宿主搬来的（断言文本一个字未改）。
        // 若把组体里的 `Pump(6)` 全改成 `UiHost.Pump(6)`，就等于"顺手改了 3000 行"，
        // 反而把拆分风险从"结构"扩散到"每一行"。保留同名包装 = 迁移零文本改动。
        // ⚠ 计数/输出格式**只有** Tests.Shared.Harness 一份真值（Check 转发的就是它）。
        // ⚠ BF/BFS 用 `const` 而不是 `static readonly`：UiHost.BF 本身就是 const，
        //   而某个工程可能一个反射都不用它 ⇒ `static readonly` 会触发 CS0414
        //   （"字段已赋值但从未使用"）。const 不占字段、不产生该警告，
        //   语义也更强（编译期内联，不存在"两个实现漂移"的余地）。
        private const BindingFlags BF = UiHost.BF;
        private const BindingFlags BFS = UiHost.BFS;
        private static FfmpegGui.MainWindow W { get => UiHost.W; set => UiHost.W = value; }
        private static string RepoRoot => UiHost.RepoRoot;
        private static string SrcDir => UiHost.SrcDir;
        private static string TempDir => UiHost.TempDir;

        private static void Pump(int n = 6) => UiHost.Pump(n);
        private static T? Find<T>(string name) where T : Control => UiHost.Find<T>(name);
        private static void SetInput(string path) => UiHost.SetInput(path);
        private static void Regen() => UiHost.Regen();
        private static string Cmd() => UiHost.Cmd();
        private static void SetFormat(string s) => UiHost.SetFormat(s);
        private static void Click(string name) => UiHost.Click(name);
        private static void Check(string name, bool cond, string detail = "") => UiHost.Check(name, cond, detail);
        private static void Safe(string name, Action a) => UiHost.Safe(name, a);
        private static int Run(string exe, string arguments) => UiHost.Run(exe, arguments);
        private static string RunOut(string exe, string arguments) => UiHost.RunOut(exe, arguments);
        private static List<string> Tok(string s) => UiHost.Tok(s);
        private static int IdxFrom(List<string> t, string key, int from) => UiHost.IdxFrom(t, key, from);
        private static int Idx(List<string> t, string key) => UiHost.Idx(t, key);
        private static string? Val(List<string> t, string key) => UiHost.Val(t, key);
        private static string? ValAfter(List<string> t, string key, int from) => UiHost.ValAfter(t, key, from);
        private static bool HasTok(List<string> t, string exact) => UiHost.HasTok(t, exact);
        private static List<string> VfSegs(List<string> t) => UiHost.VfSegs(t);
        private static bool VfHas(List<string> t, string seg) => UiHost.VfHas(t, seg);
        private static bool VfHasPrefix(List<string> t, string prefix) => UiHost.VfHasPrefix(t, prefix);
        private static bool TokEq(List<string> t, string a, string b) => UiHost.TokEq(t, a, b);
        private static string TokStr(List<string> t) => UiHost.TokStr(t);
        private static void DumpCmd(string tag, string cmd) => UiHost.DumpCmd(tag, cmd);
        private static void SweepReset() => UiHost.SweepReset();
        private static void SetStrategyRadio(string name) => UiHost.SetStrategyRadio(name);
        private static string? PickEncoder(Func<string, bool> pred) => UiHost.PickEncoder(pred);
        private static string? PickFfmpegEncoder() => UiHost.PickFfmpegEncoder();
        private static string? ZscaleParam(List<string> t, string key) => UiHost.ZscaleParam(t, key);
        private static string UiState() => UiHost.UiState();
        private static int CountTok(List<string> t, string key) => UiHost.CountTok(t, key);
        private static string Snip(string s) => UiHost.Snip(s);
        private static void ClassifyDetectLines(string? text, string[] names,
            System.Collections.Generic.List<string> verdicts,
            System.Collections.Generic.List<string> others,
            System.Collections.Generic.List<string> suspects)
            => UiHost.ClassifyDetectLines(text, names, verdicts, others, suspects);

        // ══════════════════════════ 组 M ══════════════════════════
        static void GroupM_CliStrict()
        {
            Console.WriteLine("\n########## M组: P1-E CLI 取值口径(D4/D5/D6) + 任务B/C 回归锁 ##########");
            SweepReset();
            Console.WriteLine("  [state] " + UiState());

            // ── 反射入口 ──
            // `CliParser.ApplyExtraOption` / `BuildBaseOptions` 是 internal/private，而
            // FfmpegGui.csproj 的 InternalsVisibleTo 只给了 GainMapTestHost（且**硬约束禁止改该 csproj**）
            // ⇒ 走反射。K4/K5 早已用同一条路径。
            var apply = typeof(FfmpegGui.CliParser).GetMethod("ApplyExtraOption",
                BindingFlags.NonPublic | BindingFlags.Static);
            var buildBase = typeof(FfmpegGui.CliParser).GetMethod("BuildBaseOptions",
                BindingFlags.NonPublic | BindingFlags.Static);
            Check("M0 ApplyExtraOption/BuildBaseOptions 可反射", apply != null && buildBase != null,
                  $"apply={(apply != null)} buildBase={(buildBase != null)}");
            if (apply == null || buildBase == null) return;

            // 「一条 key/value 经 ApplyExtraOption」的结局压成一个字符串：
            // 正常 ⇒ "ok"；异常 ⇒ "<异常类型名>: <消息>"。这样断言既能判类型、也能判消息是否点名 key。
            Func<string, string, string> applyOutcome = (k, v) =>
            {
                var o = new FfmpegGui.Models.FfmpegOptions();
                try { apply.Invoke(null, new object?[] { o, k, v }); return "ok"; }
                catch (System.Reflection.TargetInvocationException tie)
                { var ie = tie.InnerException ?? tie; return ie.GetType().Name + ": " + ie.Message; }
            };
            Func<string, string, FfmpegGui.Models.FfmpegOptions> applyOpts = (k, v) =>
            {
                var o = new FfmpegGui.Models.FfmpegOptions();
                apply.Invoke(null, new object?[] { o, k, v });
                return o;
            };

            // ══════ M1（D4）：空格形式下的负值必须被消费 ══════
            // 定性 = **缺陷**。`-1` 在本仓是**合法取值**：AnimationLoop 里=「不循环」
            // （FfmpegOptions.cs:403），JpegGainMapQuality 里=「用默认 75」
            // （QueueProcessor.cs:1877-1878）。修复前判据 `!StartsWith("-")` 不消费它，
            // 本键反被置为字面量 "true"（Parse 的 else 分支），随后 int.Parse 抛错被兜底 catch 吞掉。
            Safe("M1 D4 负值空格形式被消费", () =>
            {
                var p1 = FfmpegGui.CliParser.Parse(new[] { "--animation-loop", "-1" });
                p1.ExtraOptions.TryGetValue("animation-loop", out var v1);
                Check("M1a --animation-loop -1 取值被消费(不是 true)", v1 == "-1", $"value={v1 ?? "<none>"}");

                var p2 = FfmpegGui.CliParser.Parse(new[] { "--threads", "-1" });
                p2.ExtraOptions.TryGetValue("threads", out var v2);
                var o2 = applyOpts("threads", v2 ?? "true");
                Check("M1b --threads -1 ⇒ options.Threads == -1（修复前恒 0）",
                      v2 == "-1" && o2.Threads == -1, $"extra={v2 ?? "<none>"} Threads={o2.Threads}");

                var p3 = FfmpegGui.CliParser.Parse(new[] { "--animation-loop=-1" });
                p3.ExtraOptions.TryGetValue("animation-loop", out var v3);
                Check("M1c 空格形式 ≡ 内联形式（修复前只有内联可用）",
                      v1 == v3 && v1 == "-1", $"space={v1 ?? "<none>"} inline={v3 ?? "<none>"}");

                // 负控①：下一个 token 是**真选项**时仍不得被当取值消费（防"过度消费"）
                var p4 = FfmpegGui.CliParser.Parse(new[] { "--bogus-flag", "--dry-run" });
                p4.ExtraOptions.TryGetValue("bogus-flag", out var v4);
                Check("M1d 负控 后随 --选项 不被当取值", v4 == "true" && p4.DryRun,
                      $"bogus-flag={v4 ?? "<none>"} DryRun={p4.DryRun}");

                // 负控②：'-' 开头但非数字 ⇒ 仍是选项
                var p5 = FfmpegGui.CliParser.Parse(new[] { "--animation-scale-w", "-x" });
                p5.ExtraOptions.TryGetValue("animation-scale-w", out var v5);
                Check("M1e 负控 -x 仍当选项（键置 true）", v5 == "true", $"value={v5 ?? "<none>"}");
            });

            // ══════ M2（D5）：同族参数同口径 —— 非法取值一律 ArgumentException 且点名 key ══════
            // 定性 = **缺陷**：修复前 --color-strategy / --icc-mode 静默忽略，而同一张表里的
            // --color-gamut-map / --jpeg-gain-map-base-gamut 抛错 ⇒ 用户/自动化脚本无法预知。
            // 同时把 typed（int/bool/double/float）解析一并收口（`--quality abc` 此前也静默）。
            Safe("M2 D5 非法取值口径统一", () =>
            {
                var cases = new[,] {
                    { "color-strategy",          "bogus" },
                    { "icc-mode",                "bogus" },
                    { "color-engine",            "bogus" },
                    { "color-gamut-map",         "bogus" },
                    { "jpeg-gain-map-base-gamut","bogus" },
                    { "bit-depth",               "banana" },
                    { "threads",                 "abc" },
                    { "lossless",                "yes-please" },
                    { "color-hdr-peak",          "bright" },
                    // ── 2026-09-19（维护者裁决第 2 项：严格化）：6 个「取值域封闭且由本仓自持」的字符串轴 ──
                    // 收口前它们的非法值与合法值在外部**不可区分**（静默落默认值 / 静默归一），
                    // 逐项取证与"为什么只收口这 6 个"见 `CliParser.ApplyExtraOption` 各自的 case 注释
                    // 与 `ParseTokenStrict` 的 doc。每个 `bogus` 值都是"合法值的**近邻拼错**"
                    // （不是随便乱写）⇒ 能真正证明"差一个字符会被抓到"。
                    { "chroma",                  "4:4:0" },
                    { "color-range",             "tvv" },
                    { "color-709-curve",         "zimg2" },
                    { "color-carry-scope",       "alll" },
                    { "color-tone-map",          "reinhart" },
                    { "jpeg-huffman",            "optimall" },
                };
                for (int i = 0; i < cases.GetLength(0); i++)
                {
                    var k = cases[i, 0]; var v = cases[i, 1];
                    var out1 = applyOutcome(k, v);
                    Check("M2a " + k + " 非法值 ⇒ ArgumentException",
                          out1.StartsWith("ArgumentException", StringComparison.Ordinal), out1);
                    Check("M2b " + k + " 异常消息点名该 key",
                          out1.Contains("--" + k, StringComparison.Ordinal), out1);
                }

                // 正控：合法取值不得被误伤
                bool ok = true; string detail = "";
                try
                {
                    var CS = FfmpegGui.Models.ColorStrategy.Recommended;
                    ok &= applyOpts("color-strategy", "recommended").ColorStrategy == CS;
                    ok &= applyOpts("color-strategy", "carry").ColorStrategy == FfmpegGui.Models.ColorStrategy.CarryIcc;
                    ok &= applyOpts("color-strategy", "cicp-only").ColorStrategy == FfmpegGui.Models.ColorStrategy.BakeCicpOnly;
                    ok &= applyOpts("color-strategy", "manual").ColorStrategy == FfmpegGui.Models.ColorStrategy.Manual;
                    ok &= applyOpts("icc-mode", "None").ColorStrategy == CS;
                    ok &= applyOpts("icc-mode", "BakeToStandard").ColorStrategy == CS;
                    ok &= applyOpts("icc-mode", "CarryIcc").ColorStrategy == FfmpegGui.Models.ColorStrategy.CarryIcc;
                    ok &= applyOpts("icc-mode", "BakeOnly").ColorStrategy == FfmpegGui.Models.ColorStrategy.BakeCicpOnly;
                    ok &= applyOpts("color-engine", "engine").ColorEngine == "engine";
                    ok &= applyOpts("color-engine", "LEGACY").ColorEngine == "legacy";
                    ok &= applyOpts("color-gamut-map", "ON").ColorGamutMap == "on";
                    ok &= applyOpts("jpeg-gain-map-base-gamut", "BT2020").JpegGainMapBaseGamut == "bt2020";
                    // ── 2026-09-19（裁决第 2 项：严格化）正控：6 个新收口轴的**全部**合法取值 ──
                    // ⚠ 本段同时是「**分层语义**」的守卫：`4:4:4`（chroma）与 `bt2390`（tone-map）都是
                    //   「**能识别但本管线不支持**」⇒ 必须被 CLI **接受**、由引擎层响亮拒绝
                    //   （`_lib-reject.ps1:624-626` / `:694-697`、`verify-color-token-normalize.ps1:203-207`
                    //   就是钉住这条的既有断言）。把它们误判成非法 ⇒ 引擎块码不可达 + 那三条断言同时失效。
                    foreach (var cv in new[] { "auto", "4:4:4", "4:2:2", "4:2:0" })
                        ok &= applyOpts("chroma", cv).Chroma == cv;
                    // 大小写归一（修复前 `--chroma AUTO` 静默等于 4:2:0 —— 下游 `is "4:4:4"` 是原串比较）
                    ok &= applyOpts("chroma", "AUTO").Chroma == "auto";
                    foreach (var rv in new[] { "auto", "tv", "pc" })
                        ok &= applyOpts("color-range", rv).ColorRange == rv;
                    ok &= applyOpts("color-709-curve", "ZIMG").Color709Curve == "zimg";
                    ok &= applyOpts("color-709-curve", "std").Color709Curve == "std";
                    // carry-scope 保留数字串：`Enum.TryParse` 本来就接受 0/1/2，收窄时不得把既有写法挡在门外
                    foreach (var sv in new[] { "none", "unnameable", "all", "0", "1", "2" })
                        ok &= applyOpts("color-carry-scope", sv).ColorCarryScope == sv;
                    foreach (var tv in new[] { "none", "reinhard", "hable", "mobius", "bt2446a", "bt2390" })
                        ok &= applyOpts("color-tone-map", tv).ColorToneMap == tv;
                    ok &= applyOpts("jpeg-huffman", "optimal").JpegHuffman == "optimal";
                    ok &= applyOpts("jpeg-huffman", "default").JpegHuffman == "default";
                    // 顺带修的一处**静默取反**：ffmpeg 原生的 1/0 现在归一成 optimal/default
                    // （修复前 `1` 落 else 分支 ⇒ 下发 `-huffman 0`，与用户意图**相反**且零告警）。
                    ok &= applyOpts("jpeg-huffman", "1").JpegHuffman == "optimal";
                    ok &= applyOpts("jpeg-huffman", "0").JpegHuffman == "default";
                }
                catch (Exception ex) { ok = false; detail = ex.GetType().Name + ": " + ex.Message; }
                Check("M2c 合法取值全部仍被接受（正控）", ok, detail);

                // 端到端：整条 CLI 路径（Parse → BuildBaseOptions）必须报错，而不是"退出码 0 + 静默默认"
                var parsed = FfmpegGui.CliParser.Parse(new[] { "--color-strategy", "bogus" });
                string e2e;
                try { buildBase.Invoke(null, new object?[] { parsed }); e2e = "ok"; }
                catch (System.Reflection.TargetInvocationException tie)
                { var ie = tie.InnerException ?? tie; e2e = ie.GetType().Name + ": " + ie.Message; }
                Check("M2d 端到端 Parse→BuildBaseOptions 对非法策略报错",
                      e2e.StartsWith("ArgumentException", StringComparison.Ordinal)
                      && e2e.Contains("--color-strategy", StringComparison.Ordinal), e2e);

                // 端到端正控：合法策略经同一条路径必须成功
                var parsedOk = FfmpegGui.CliParser.Parse(new[] { "--color-strategy", "carry" });
                var optsOk = (FfmpegGui.Models.FfmpegOptions?)buildBase.Invoke(null, new object?[] { parsedOk });
                Check("M2e 端到端 合法策略 carry ⇒ CarryIcc",
                      optsOk != null && optsOk.ColorStrategy == FfmpegGui.Models.ColorStrategy.CarryIcc,
                      $"ColorStrategy={optsOk?.ColorStrategy.ToString() ?? "<null>"}");

                // ── M2f（2026-09-19，裁决第 2 项）：**分层语义**守卫 —— 「能识别但本管线不支持」必须放行 ──
                // 为什么单列一条独立锚点：CLI 严格化最容易踩的坑就是"顺手把下游要拒的值也拒了"。
                // 下面三格各自的**下游**拒绝点与**既有断言**（改坏它们会同时失效，属静默退化）：
                //   · tone-map bt2390  → ColorIntentFactory.cs:202 引擎显式拒绝
                //   · chroma   4:4:4   → _lib-reject.ps1:624-626 / :694-697（webp 引擎拒绝）
                //   · color-range auto → _lib-matrix.ps1:58（语义 = "不下发"，不是同轴另一取值）
                // 判据只问"这条路抛不抛"，不问下游怎么拒 —— CLI 层不该知道下游的取舍。
                var layered = new[,] {
                    { "color-tone-map", "bt2390" },
                    { "chroma",         "4:4:4"  },
                    { "color-range",    "auto"   },
                };
                bool lok = true; string ldetail = "";
                for (int i = 0; i < layered.GetLength(0); i++)
                {
                    var oc = applyOutcome(layered[i, 0], layered[i, 1]);
                    if (oc != "ok")
                    { lok = false; ldetail += layered[i, 0] + "=" + layered[i, 1] + " => " + oc + "; "; }
                }
                Check("M2f 分层语义 能识别但不支持的 token 被 CLI 放行", lok, ldetail);
            });

            // ══════ M3（D6）：--bit-depth auto ══════
            // 定性 = **两半**（见报告表）：
            //   · 「auto ≡ 未设置」是**有意** —— 模型 BitDepth=null 的语义就是 auto
            //     （FfmpegOptions.cs:146-149），UI 也把下拉的 auto 映射为 null
            //     （MainWindow.xaml.cs:1351-1353）⇒ M3f 把这条等价性**锁住**，防后人"修"成别的语义。
            //   · 「靠解析异常被吞来实现、且非法值无任何提示」是**缺陷** ⇒ M3a 锁"显式接受"、
            //     M3d 锁"其它非法值必须报错"（修复前 banana 与 auto 结果完全相同、都无声）。
            Safe("M3 D6 bit-depth auto", () =>
            {
                // ⚠ 本块刻意**不**让 applyOpts 抛出的异常中断后续断言（否则一条红会让后面全部不报，
                //   诊断力归零）。tryOpts 把"异常"折成 null，每条断言自己判 null。
                Func<string, string, FfmpegGui.Models.FfmpegOptions?> tryOpts = (k, v) =>
                {
                    var o = new FfmpegGui.Models.FfmpegOptions();
                    try { apply.Invoke(null, new object?[] { o, k, v }); return o; }
                    catch (System.Reflection.TargetInvocationException) { return null; }
                };

                // M3a 必须**有牙**：不能写 Check(..., true)。判据是"这条路真的不抛"。
                var autoOutcome = applyOutcome("bit-depth", "auto");
                Check("M3a --bit-depth auto 被显式接受（不抛异常）",
                      autoOutcome == "ok", autoOutcome);

                var oAuto = tryOpts("bit-depth", "auto");
                Check("M3b auto ⇒ BitDepth=null（与模型语义一致）",
                      oAuto != null && oAuto.BitDepth == null,
                      oAuto == null ? "applyOpts threw" : $"BitDepth={(oAuto.BitDepth.HasValue ? oAuto.BitDepth.Value.ToString() : "<null>")}");

                var o8 = tryOpts("bit-depth", "8");
                var o16 = tryOpts("bit-depth", "16");
                Check("M3c 正控 8/16 逐值生效",
                      o8 != null && o8.BitDepth == 8 && o16 != null && o16.BitDepth == 16,
                      $"8=>{(o8 == null ? "<threw>" : o8.BitDepth.ToString())} 16=>{(o16 == null ? "<threw>" : o16.BitDepth.ToString())}");

                var bad = applyOutcome("bit-depth", "banana");
                Check("M3d banana ⇒ ArgumentException 点名 bit-depth（修复前静默=与 auto 同结果）",
                      bad.StartsWith("ArgumentException", StringComparison.Ordinal)
                      && bad.Contains("--bit-depth", StringComparison.Ordinal), bad);

                // M3e（2026-09-25 **反转**）：旧断言锁的是"修复前现状"（`0 不被拒 ⇒ 刻意不做范围校验`），
                //   当时给的理由是"本仓 0/-1 大量作哨兵"（AnimationScaleW=0 保持原始、AnimationLoop=-1 不循环）。
                //   但 bit-depth 的哨兵是 **auto/null**（由 M3b + M3f 锁住），不是 0 ⇒ 那条理由不适用：
                //   放行 0 只会让它流到下游去取一个不存在的出口档，正是 P2.5 要修的**静默降档**缺陷本身。
                //   ⇒ 现在要求 0 与 banana 同命（ArgumentException 点名 --bit-depth）。这是**收紧**，不是放宽。
                var zOutcome = applyOutcome("bit-depth", "0");
                Check("M3e --bit-depth 0 被拒（范围校验；哨兵是 auto/null 而非 0）",
                      zOutcome.StartsWith("ArgumentException", StringComparison.Ordinal)
                      && zOutcome.Contains("--bit-depth", StringComparison.Ordinal), zOutcome);

                var oDef = new FfmpegGui.Models.FfmpegOptions();
                Check("M3f auto ≡ 未设置（有意，锁住）",
                      oAuto != null && oAuto.BitDepth == oDef.BitDepth,
                      $"auto={(oAuto == null ? "<threw>" : oAuto.BitDepth.ToString())} default={oDef.BitDepth}");
            });

            // ══════ M4（任务 B）：临时产物不得进共享语料目录 ══════
            // B1/B2/B3（在 H11/I3/I9 块里**当场**断言）负责"写的时候不在 SrcDir"；
            // M4 负责"跑完之后 SrcDir 干净"（后者单独不足以有牙：那几个产物在各自块尾被删掉了）。
            Safe("M4 任务B 临时目录卫生", () =>
            {
                Check("M4a 运行级 TempDir 已建立且不在 SrcDir 下",
                      TempDir.Length > 0 && System.IO.Directory.Exists(TempDir)
                      && !TempDir.StartsWith(SrcDir, StringComparison.OrdinalIgnoreCase),
                      $"TempDir={TempDir} SrcDir={SrcDir}");

                var leftovers = System.IO.Directory.GetFiles(SrcDir, "_*.png");
                Check("M4b 共享语料目录内无 _*.png 残留", leftovers.Length == 0,
                      "leftovers=[" + string.Join(",", leftovers) + "]");

                foreach (var n in new[] { "_h11_bt2020.png", "_prophoto_src.png", "_p3_8bit.png", "_k5_same_out.png" })
                    Check("M4c " + n + " 不在 SrcDir",
                          !System.IO.File.Exists(System.IO.Path.Combine(SrcDir, n)),
                          System.IO.Path.Combine(SrcDir, n));
            });

            // ══════ M5（任务 C）：动图参数粘性不得泄漏到静态模式 ══════
            // 定性 = **缺陷**（不是"保留用户输入"的约定）：非动图模式下 AnimationPanel 整体
            // IsVisible=false —— 用户**看不到也改不了**这几个框，残留文本却仍进命令：
            //   · 静态 WebP 会被塞 `-vf fps=…,scale=…`（FfmpegCommandBuilder.cs:253-261）；
            //   · 静态 **JXL 会被静默换成 `-c:v libjxl_anim`**（该分支判据就是
            //     options.AnimationFps.HasValue，:361）。
            // 修法 = 按模式取值（**不**清空控件 ⇒ M5f 锁"用户输入仍在"）。
            Safe("M5 任务C 动图参数不粘到静态模式", () =>
            {
                var mode = Find<ComboBox>("ConversionModeCombo");
                var fps = Find<TextBox>("AnimationFpsBox");
                var sw = Find<TextBox>("AnimationScaleWBox");
                var lp = Find<TextBox>("AnimationLoopBox");
                if (mode == null || fps == null || sw == null || lp == null)
                { Check("M5-0 动图控件存在", false, $"mode={mode != null} fps={fps != null} sw={sw != null} lp={lp != null}"); return; }

                // ① 动图模式设值 ⇒ 必须进命令（正控：证明输入真的生效，不是"本来就没有"）
                mode.SelectedIndex = 1; Pump(16);
                SetFormat("WebP (动图)"); Pump(16);
                var ll = Find<CheckBox>("LosslessCheck"); if (ll != null && ll.IsEnabled) ll.IsChecked = false;
                fps.Text = "12"; sw.Text = "320"; lp.Text = "3"; Pump(16); Regen();
                var tA = Tok(Cmd());
                Check("M5a 动图模式 fps=12 进命令（正控）", VfHas(tA, "fps=12"), $"vf={Val(tA, "-vf") ?? "<none>"}");
                Check("M5b 动图模式 scale=320 进命令（正控）", VfHas(tA, "scale=320:-1:flags=lanczos"),
                      $"vf={Val(tA, "-vf") ?? "<none>"}");

                // ② 切回静态模式（**不清空**文本框）⇒ 命令里不得再出现动图参数
                mode.SelectedIndex = 0; Pump(16);
                SetFormat("WebP"); Pump(16); Regen();
                var tB = Tok(Cmd());
                DumpCmd("M5 静态WebP(动图模式残留fps=12)", Cmd());
                Check("M5c 静态 WebP 无 fps=/scale= 滤镜（修复前有）",
                      !VfHasPrefix(tB, "fps=") && !VfHasPrefix(tB, "scale="),
                      $"vf={Val(tB, "-vf") ?? "<none>"}");

                // ③ 静态 JXL 是最严重的一格：修复前 AnimationFps.HasValue ⇒ -c:v libjxl_anim。
                //   ⚠ 必须显式选 **ffmpeg 的 libjxl** 编码器：JXL 的默认后端是 cjxl（外部命令行，
                //     里面根本没有 -c:v / -vf）⇒ 不选编码器时本断言会因"载体不存在"而恒真（实测踩到）。
                SetFormat("JPEG XL"); Pump(16);
                var pickedJxl = PickEncoder(s => s.StartsWith("libjxl —", StringComparison.Ordinal));
                Pump(12); Regen();
                var tC = Tok(Cmd());
                DumpCmd("M5 静态JXL(动图模式残留fps=12)", Cmd());
                Check("M5d 静态 JXL 不得被换成 libjxl_anim（修复前会）",
                      Val(tC, "-c:v") != null && !HasTok(tC, "libjxl_anim") && !VfHasPrefix(tC, "fps="),
                      $"picked={pickedJxl ?? "<none>"} c:v={Val(tC, "-c:v") ?? "<none>"} vf={Val(tC, "-vf") ?? "<none>"}");

                // ④ 切回动图模式：用户输入**仍在**（证明修法是"按模式取值"而非"清空控件"）
                mode.SelectedIndex = 1; Pump(16);
                SetFormat("WebP (动图)"); Pump(16); Regen();
                var tD = Tok(Cmd());
                Check("M5e 切回动图模式 输入仍在(fps=12)", VfHas(tD, "fps=12"), $"vf={Val(tD, "-vf") ?? "<none>"}");
                Check("M5f 负控 文本框内容确实未被清空",
                      (fps.Text ?? "") == "12" && (sw.Text ?? "") == "320",
                      $"fps='{fps.Text}' sw='{sw.Text}'");
            });

            SweepReset();
        }

        // ══════════════════════════ 组 P ══════════════════════════
        static void GroupP_StartupDetectLog()
        {
            var box = Find<TextBox>("LogText");
            Check("P0 面板控件 LogText 在位（本组前提）", box != null,
                  "FindControl<TextBox>(\"LogText\") == null ⇒ 面板根本没构造起来");
            if (box == null) return;

            var names = new[] { "cjxl", "cjpegli", "djxl", "exiftool", "JxrEncApp", "avifenc", "dngtool" };
            // 轮询上限 45 s：本机预热实测 1~2 s，但「PLAN 整棵缺失 ⇒ 走扩展搜索预算」的最坏路径要几十秒。
            // ⚠ 超时**不能判红**，要单列「测量失败」⇒ 否则"这台机器装了什么应用"会决定断言颜色（§6 口径）。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var verdicts = new System.Collections.Generic.List<string>();
            var others = new System.Collections.Generic.List<string>();
            var unknowns = new System.Collections.Generic.List<string>();
            while (sw.Elapsed.TotalSeconds < 45.0)
            {
                Pump(6);
                ClassifyDetectLines(box.Text, names, verdicts, others, unknowns);
                if (verdicts.Count >= names.Length) break;
            }

            string Ro() => $"用时 {sw.Elapsed.TotalSeconds:N1}s；结论行 {verdicts.Count}、其它 [detect] 诊断行 {others.Count}、疑似空槽位 {unknowns.Count}";
            Check("P1 七个槽位都打出了结论行（槽位数 = 日志行数）", verdicts.Count == names.Length,
                  verdicts.Count == names.Length ? Ro() : "★测量失败（不是产品失败）★ " + Ro()
                    + " ⇒ 60s 内没等齐，本机探测路径异常，本组后续断言不作数");
            if (verdicts.Count != names.Length) return;

            // P2 每条结论行都必须真的给出结论（修之前某两条压根不打，另一条打出来什么都没有）
            var unconcluded = new System.Collections.Generic.List<string>();
            foreach (var l in verdicts)
                if (!(l.EndsWith("OK", StringComparison.Ordinal) || l.EndsWith("未找到", StringComparison.Ordinal)))
                    unconcluded.Add(l);
            Check("P2 每条结论行都以 OK/未找到 收尾（无'检测了但不说结果'形态）",
                  unconcluded.Count == 0,
                  unconcluded.Count == 0 ? Ro() : $"违例 {unconcluded.Count} 条：{string.Join(" ⧉ ", unconcluded)}");

            // P3 逐个点名：7 个工具各恰有一行（少一个 = 那个槽位又静默了；多一个 = 重复打）
            foreach (var n in names)
            {
                var hits = new System.Collections.Generic.List<string>();
                foreach (var l in verdicts)
                    if (l.StartsWith("[detect] " + n + ": ", StringComparison.Ordinal)) hits.Add(l);
                Check($"P3 [{n}] 恰有一条结论行", hits.Count == 1,
                      $"实 {hits.Count} 条（0 = 该槽位又静默了；>1 = 重复打）：{string.Join(" ⧉ ", hits)}");
            }

            // P4「名字不认识的**结论行**」= 空槽位/漏删工具的确切字节形态（旧缺陷：删 UltrahdrService 后
            //     留下 `Task.Run(() => { try { } catch { } })`，以及某条打出 `[detect] : OK` 这类无名行）。
            Check("P4 不存在'未知名字却带结论'的行（空槽位/漏改名的直接形态）", unknowns.Count == 0,
                  $"疑似空槽位行 {unknowns.Count} 条：{string.Join(" ⧉ ", unknowns)}");

            // P5 反控：证明本组不是"恒绿"——把 7 个名字逐一拼回去必须能数到 7
            int accounted = 0;
            foreach (var n in names)
                foreach (var l in verdicts)
                    if (l.StartsWith("[detect] " + n + ": ", StringComparison.Ordinal)) { accounted++; break; }
            Check("P5 对照组：P3 逐名累加必须恰好吃掉全部 7 条结论行（防本组被判据漏行恒绿）",
                  accounted == verdicts.Count && accounted == names.Length,
                  $"accounted={accounted} verdicts={verdicts.Count} names={names.Length}；{Ro()}");
        }

        // ══════════════ 入口（供 Program.Main 逐 mode 分派）══════════════
        // 只做转发 + 一个**共享收敛前置**：组体本身是逐字搬来的私有方法，保持原貌不动。
        internal static void RunM()
        {
            // ⚠ 共享前置（见生成器 $convergePre 长注释）：等启动期 fire-and-forget 的
            //   工具探测（7 个槽位）收敛，复现旧宿主"GroupP 先跑"建立的稳态。
            //   不做任何断言、不动 PASS/FAIL 计数 —— 只等既有后台工作落地。
            UiHost.AwaitStartupDetection();
            // ⚠ 再落实一次外部工具路径/探测（旧宿主只在 GroupG 里做过，见 :781-784）：
            //   不做则 `EncoderCombo` 的硬件项/外部工具后端不可解析 ⇒ `L1e-0` 间歇红、
            //   连带 `L1f`..`L1k` 六条**整段不执行**（实测单跑 3 次红 1 次）。
            UiHost.ApplyToolPaths();
            // ⚠ 再把「ffmpeg 能力 + 编码器列表」两个缓存**同步**落实：
            //   Step 2 是 Task.Run + Task.WhenAny(8s)，等日志文本仍有残余窗口
            //   ⇒ 实测 L1e-0 6 次红 1 次、N5b 6 次红 4 次。本调用直接等缓存建好
            //   （两服务都幂等带缓存，重复调用不改变最终值）。
            UiHost.SettleCapabilities();
            // ⚠⚠ 最后把**格式 ⇒ 编码器下拉**这条联动走完：`SetFormat` 只 fire-and-forget 起
            //   `RefreshEncoderListAsync`（:2051 async）⇒ 紧随其后的读取可能仍看到**上一个格式**的
            //   残留项（启动是 JPEG ⇒ cjpegli）⇒ `GetCurrentEncoderBackend()`=Cjpegli
            //   ⇒ `SingleThreadCheck` 被置真且**再不复位**（:2367-2372 的 else 只恢复 IsEnabled）
            //   ⇒ 命令恒 `-threads 1`（:1425）。N5b 逐字节比两条路径 ⇒ 分叉。
            //   实测 ui-preset-roundtrip 单跑 10 次红 8 次；本行把联动走到稳态。
            UiHost.SettleFormatLinkage();
            GroupM_CliStrict();
        }
        // ⚠ P **故意不加**收敛前置：本组测的就是「启动期刚打完那 7 行」这个窗口，
        //   先调 AwaitStartupDetection() 会把被测状态本身消掉（见生成器注释）。
        internal static void RunP() => GroupP_StartupDetectLog();
    }
}
