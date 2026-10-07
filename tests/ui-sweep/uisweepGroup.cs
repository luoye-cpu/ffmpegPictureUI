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

namespace Tests.UiSweep
{
    /// <summary>
    /// 子系统测试组：**ui-sweep** —— 2026-10-05 从 UiTestHost 的单个 3500 行
    /// <c>Program.cs</c> 中物理拆出。组体**逐字**搬来（断言文本一字未改，门禁在 grep 它们）。
    /// <para><b>共享 GUI 底座</b>：窗口 <c>W</c>、<c>TempDir</c>、以及全部 GUI/token 助手
    /// 都在 UiHost（UiHostShared 工程）—— 本类只保留同名**转发包装**，
    /// 不复制实现。</para>
    /// </summary>
    internal static class uisweepGroup
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
        // ⚠ 2026-10-07 新增：前提不满足时的**点名跳过**（计 skip、不打 FAIL）—— 见 UiHost.Skip 的说明。
        private static void Skip(string reason) => UiHost.Skip(reason);
        // ⚠ 2026-10-07 新增：断言前把高级色彩三元组显式复位（见 UiHost.ResetAdvancedColorToAuto）。
        private static void ResetAdvancedColorToAuto() => UiHost.ResetAdvancedColorToAuto();
        // ⚠ 2026-10-07 新增：选中 ColorSpaceCombo 并**等异步格式联动落地**（见 UiHost.SelectColorSpaceSettled）。
        private static bool SelectColorSpaceSettled(string v) => UiHost.SelectColorSpaceSettled(v);
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

        // ══════════════════════════ 组 J ══════════════════════════
        static void GroupJ_UiSweep()
        {
            Console.WriteLine("\n########## J组: UI 控件穷举 → 命令串 token 级断言 (P1-B) ##########");
            Console.WriteLine("  断言口径: 按 token 下标关系定位(-key 的下一个 token 精确相等), 不用整串 Contains");
            SweepReset();
            Console.WriteLine("  [state] " + UiState());

            // ── J1 FormatCombo ×6：-c:v 编码器名 + 输出 token 扩展名 + 该格式专属编码 key ──
            // ⚠ 必须先显式选 ffmpeg 编码器：JPEG 默认 cjpegli、JXL 默认 cjxl ⇒ 默认预览是外部工具
            //   命令行（无 -q:v/-distance 之外的 ffmpeg token）。实测默认值见 [state] 行。
            // ⚠ 必须按**期望名字精确挑**，不能只按「非 cjpegli/cjxl」挑 —— 硬件编码器的显示名带
            //   图标前缀（"⚡ av1_nvenc — …"），而 EncoderInfo.ParseEncoderName 只按 " — " 切分，
            //   ⇒ 会把 "⚡ av1_nvenc"（含空格）当成编码器名写进 `-c:v`。本组用软编名字规避，
            //   并把该缺陷的实测读数打到 [info] 行（详见报告 D-new-2）。
            var fmtCases = new[,] {
                { "JPEG",    ".jpg",  "mjpeg",     "-q:v" },
                { "PNG",     ".png",  "png",       "-compression_level" },
                { "WebP",    ".webp", "libwebp",   "-q:v" },
                { "AVIF",    ".avif", "libaom-av1", "-crf" },
                { "JPEG XL", ".jxl",  "libjxl",    "-distance" },
                { "TIFF",    ".tiff", "tiff",      "-compression_algo" },
            };
            for (int i = 0; i < fmtCases.GetLength(0); i++)
            {
                var name = fmtCases[i, 0]; var ext = fmtCases[i, 1];
                var encExp = fmtCases[i, 2]; var key = fmtCases[i, 3];
                Safe("J1 " + name, () =>
                {
                    SetFormat(name); Pump(10);
                    // WebP 的「无损」是粘性状态：PNG 被强制无损后切到 WebP 仍是无损
                    // ⇒ preset/有损参数会被有意丢弃。显式复位，保证测的是有损分支。
                    var ll = Find<CheckBox>("LosslessCheck");
                    if (ll != null && ll.IsEnabled) ll.IsChecked = false;
                    var picked = PickEncoder(s => s.StartsWith(encExp + " —", StringComparison.Ordinal));
                    Regen();
                    var t = Tok(Cmd());
                    var enc = Val(t, "-c:v");
                    var last = t.Count > 0 ? t[t.Count - 1].Trim('"') : "";
                    bool extOk = last.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
                    string? kv = Val(t, key);
                    Check("J1 " + name + " -c:v+扩展名+编码key",
                          enc == encExp && extOk && kv != null,
                          $"picked={picked ?? "<none>"} -c:v={enc ?? "<none>"} last={last} {key}->{kv ?? "<missing>"}");
                });
            }
            // ── J1z 硬件编码器名解析（**缺陷登记**，不做断言）：显示名带 "⚡ " 图标前缀，
            //   而 ParseEncoderName 只按 " — " 切分 ⇒ 名字里残留图标+空格 ⇒ `-c:v` 的取值
            //   不是单个 token（实测 `-c:v=⚡`），ffmpeg 会收到非法编码器名。
            Safe("J1z 硬件编码器名解析(登记)", () =>
            {
                SetFormat("AVIF"); Pump(10);
                var hw = PickEncoder(s => s.StartsWith("⚡", StringComparison.Ordinal));
                Regen();
                var t = Tok(Cmd());
                Console.WriteLine($"      [info] J1z hw picked=\"{hw ?? "<none>"}\" -c:v token=\"{Val(t, "-c:v") ?? "<none>"}\" " +
                                  $"(期望是单个 token \"av1_nvenc\"；非单 token ⇒ ParseEncoderName 未剥离图标前缀)");
                // ⚠⚠ 2026-10-07 修复（J1z 假红）：这是**环境前置**，不是产品能力断言。
                //   失败文案自己就写着 `no hardware encoder item in this environment`，而实现写成
                //   `Check(...)` ⇒ 环境不满足时**谎报红**（本机实测：无 `⚡` 项 ⇒ 恒定 FAIL）。
                //   证据：`⚡` 前缀只在 GPU 状态为 `DeviceFoundUntested` 时出现
                //     （`MainWindow.xaml.cs:1309`）；而**真正测解析能力**的断言（`L1a`/`L1b`
                //     的 `av1_nvenc` / `mjpeg_nvenc` 精确解析）**全部 PASS**（见 ui-param-defects 组）
                //     ⇒ 被断言的能力是好的，缺的只是"本机 GUI 提供硬件项"这个前提。
                //   ⇒ 改为 **SKIP + 点名**（保留可见性、不谎报红）。这与 `docs/TESTING.md` §6 第 68 条
                //     「读不到前置就静默跳过 ⇒ 假绿」的**区别**：本处**必须打印 SKIP 行**并计入 skip，
                //     绝不当作通过 —— 既不谎报红、也不静默消失。
                if (hw == null)
                    Skip("J1z 硬件编码器名解析：本机 GUI 未提供硬件编码器项（无 GPU 检测落地）⇒ 本条未验证，不计入通过");
                else
                {
                    // ⚠⚠ 断言必须锁**不变量**，不能锁**某台机器的取值**（2026-10-07 实测踩过）：
                    //   第一版硬编码 `Val(t, "-c:v") == "av1_nvenc"` ⇒ 在**本机**当场红
                    //   （`期望 "av1_nvenc"，实得 "av1_amf"`）—— 因为本机 GPU 检测先命中 **amd** 项，
                    //   而 `av1_amf` 同样是**合法的单个 token** ⇒ 原断言把"环境差异"当成了缺陷。
                    //   ⚠ 这正是本仓反复记载的「判据词形要回工具实测，不能照抄记忆」（§6 第 116 条）
                    //     与「别断言具体核数」（第 10 条同族）的同一类错误。
                    // ⇒ 本项真正要锁的**不变量**（也正是 J1z 当初登记的缺陷形态）：
                    //   显示名带 `⚡ ` 图标前缀 ⇒ 若 `ParseEncoderName` 不剥离它，
                    //   `-c:v` 会拿到 `⚡` 或 `⚡ av1_xxx`（**带空格/带图标**）⇒ ffmpeg 收到非法编码器名。
                    //   ⇒ 判据 = 「`-c:v` 非空 ∧ 不含空格 ∧ 不含 `⚡` ∧ 不含 `—`」，
                    //     与既有同族断言 `L1b`（"无空格/无图标"）**同一口径**，且对机器差异免疫。
                    var cv = Val(t, "-c:v") ?? "";
                    bool clean = cv.Length > 0
                                 && !cv.Contains(' ')
                                 && !cv.Contains('⚡')
                                 && !cv.Contains('—');
                    Check("J1z 硬件编码器名解析（`-c:v` 为单个 token、不含图标/空格）",
                          clean,
                          $"实得 \"{cv}\"（要求非空且不含空格/⚡/—；本机先命中的硬件后端不固定，故只锁形态不锁名字）");
                }
                PickEncoder(s => s.StartsWith("libaom-av1 —", StringComparison.Ordinal));
                Regen();
            });

            // ── J2 ChromaCombo ×4（JPEG，8-bit 源 + 位深 auto）──
            // 载体陷阱：yuvj444p 里含 "444"、yuvj420p 里含 "420" ⇒ 必须**精确等于**断言。
            Safe("J2 ChromaCombo×4 (JPEG/mjpeg)", () =>
            {
                SetFormat("JPEG"); Pump(10);
                PickFfmpegEncoder();
                var cb = Find<ComboBox>("ChromaCombo");
                if (cb == null) { Check("J2 ChromaCombo存在", false, "null"); return; }
                // 控件真实取值（由 UpdateChromaBitDepthOptions 按编码器重建）
                var avail = new List<string>();
                for (int i = 0; i < cb.ItemCount; i++) avail.Add(cb.Items[i] as string ?? "<null>");
                cb.SelectedItem = "auto"; Regen();
                var tAuto = Tok(Cmd());
                Check("J2a auto⇒无-pix_fmt", Val(tAuto, "-pix_fmt") == null,
                      $"-pix_fmt={Val(tAuto, "-pix_fmt") ?? "<none>"} items=[{string.Join(",", avail)}]");
                var cases = new[,] { { "4:4:4", "yuvj444p" }, { "4:2:2", "yuvj422p" }, { "4:2:0", "yuvj420p" } };
                for (int i = 0; i < cases.GetLength(0); i++)
                {
                    var v = cases[i, 0]; var exp = cases[i, 1];
                    if (!avail.Contains(v)) { Check("J2 " + v + " 可选", false, $"not in items=[{string.Join(",", avail)}]"); continue; }
                    cb.SelectedItem = v; Regen();
                    var got = Val(Tok(Cmd()), "-pix_fmt");
                    Check("J2 " + v, got == exp, $"-pix_fmt={got ?? "<none>"} expect={exp}");
                }
                cb.SelectedItem = "auto"; Regen();
                Check("J2d 回auto⇒-pix_fmt消失", Val(Tok(Cmd()), "-pix_fmt") == null,
                      $"-pix_fmt={Val(Tok(Cmd()), "-pix_fmt") ?? "<none>"}");
            });

            // ── J3 BitDepthCombo：**按控件真实 items 逐个取值**（items 随格式变化）──
            // 实测 items：PNG/TIFF=[auto,8,16]、JPEG/WebP=[auto,8]、AVIF=[auto,8,10]、JXL=[auto,8,10,12,16,32]
            Safe("J3 BitDepthCombo (PNG，按真实 items)", () =>
            {
                SetFormat("PNG"); Pump(10);
                var cb = Find<ComboBox>("BitDepthCombo");
                if (cb == null) { Check("J3 BitDepthCombo存在", false, "null"); return; }
                var items = new List<string>();
                for (int i = 0; i < cb.ItemCount; i++) items.Add(cb.Items[i] as string ?? "<null>");
                Console.WriteLine("      [J3] PNG BitDepthCombo items=[" + string.Join(", ", items) + "]");
                Check("J3 语料自检 items≥3", items.Count >= 3, $"items=[{string.Join(",", items)}]");
                foreach (var v in items)
                {
                    cb.SelectedItem = v; Regen();
                    var got = Val(Tok(Cmd()), "-pix_fmt");
                    // PNG 是 RGB 原生：8-bit → rgb24；>8-bit → rgb48le；auto + 8-bit 源 ⇒ 不写
                    string? exp = v == "auto" ? null : (v == "8" ? "rgb24" : "rgb48le");
                    Check("J3 PNG bd=" + v, got == exp, $"-pix_fmt={got ?? "<none>"} expect={exp ?? "<none>"}");
                }
                cb.SelectedIndex = 0; Regen();
            });

            // ── J4 QualitySlider：-q:v 的**值**随动（且是整数、在 mjpeg 合法域 1..31）──
            Safe("J4 QualitySlider→-q:v (JPEG/mjpeg)", () =>
            {
                SetFormat("JPEG"); Pump(10);
                PickFfmpegEncoder();
                var q = Find<Slider>("QualitySlider");
                if (q == null) { Check("J4 QualitySlider存在", false, "null"); return; }
                if (!q.IsEnabled) { Check("J4 质量滑块可用", false, $"IsEnabled=false ({UiState()})"); return; }
                q.Value = 92; Regen(); var v92 = Val(Tok(Cmd()), "-q:v");
                q.Value = 40; Regen(); var v40 = Val(Tok(Cmd()), "-q:v");
                bool ok = v92 != null && v40 != null && v92 != v40
                          && int.TryParse(v92, out var n92) && int.TryParse(v40, out var n40)
                          && n92 >= 1 && n92 <= 31 && n40 >= 1 && n40 <= 31;
                Check("J4 质量→-q:v值随动", ok, $"q92->{v92 ?? "<none>"} q40->{v40 ?? "<none>"}");
                q.Value = 92; Regen();
            });

            // ── J5/J6/J7 高级色彩三下拉：逐取值断言 CICP 声明 token ──
            Safe("J5 ColorPrimariesCombo×3", () =>
            {
                SetFormat("PNG");
                var ac = Find<CheckBox>("UseAdvancedColor"); if (ac != null) ac.IsChecked = true; Pump(8);
                var cb = Find<ComboBox>("ColorPrimariesCombo");
                if (cb == null) { Check("J5 combo存在", false, "null"); return; }
                var cases = new[] { "bt709", "bt2020", "smpte432" };
                foreach (var v in cases)
                {
                    cb.SelectedItem = v; Pump(4); Regen();
                    var got = Val(Tok(Cmd()), "-color_primaries");
                    Check("J5 " + v, got == v, $"-color_primaries={got ?? "<none>"} expect={v}");
                }
                if (ac != null) ac.IsChecked = false; Pump(6);
            });
            Safe("J6 ColorTrcCombo×5", () =>
            {
                SetFormat("PNG");
                var ac = Find<CheckBox>("UseAdvancedColor"); if (ac != null) ac.IsChecked = true; Pump(8);
                var cb = Find<ComboBox>("ColorTrcCombo");
                if (cb == null) { Check("J6 combo存在", false, "null"); return; }
                var cases = new[] { "iec61966-2-1", "bt709", "smpte2084", "arib-std-b67", "linear" };
                foreach (var v in cases)
                {
                    cb.SelectedItem = v; Pump(4); Regen();
                    var got = Val(Tok(Cmd()), "-color_trc");
                    Check("J6 " + v, got == v, $"-color_trc={got ?? "<none>"} expect={v}");
                }
                if (ac != null) ac.IsChecked = false; Pump(6);
            });
            Safe("J7 ColorMatrixCombo×5 (-i 之前的 -colorspace)", () =>
            {
                SetFormat("PNG");
                var ac = Find<CheckBox>("UseAdvancedColor"); if (ac != null) ac.IsChecked = true; Pump(8);
                var cb = Find<ComboBox>("ColorMatrixCombo");
                if (cb == null) { Check("J7 combo存在", false, "null"); return; }
                // ⚠ gbr 是 **zscale 名**，ffmpeg 侧叫 rgb ⇒ 由 ColorSpaceRegistry.ToFfmpegInputMatrixName
                //   归一（实测：选 gbr ⇒ 命令里写 rgb）。期望值必须是**归一后**的名字。
                var cases = new[,] {
                    { "bt709", "bt709" }, { "bt2020nc", "bt2020nc" }, { "bt470bg", "bt470bg" },
                    { "smpte170m", "smpte170m" }, { "gbr", "rgb" },
                };
                for (int i = 0; i < cases.GetLength(0); i++)
                {
                    var v = cases[i, 0]; var exp = cases[i, 1];
                    cb.SelectedItem = v; Pump(4); Regen();
                    var t = Tok(Cmd());
                    var iIdx = Idx(t, "-i");
                    // 取 **-i 之前**的第一个 -colorspace（输入声明），避免与输出标注混为一谈
                    var inCs = IdxFrom(t, "-colorspace", 0) < iIdx ? Val(t, "-colorspace") : null;
                    Check("J7 " + v + "→" + exp, inCs == exp, $"input -colorspace={inCs ?? "<none>"} expect={exp}");
                }
                if (ac != null) ac.IsChecked = false; Pump(6);
            });

            // ── J8 ColorSpaceCombo：快速色域 → **目标**色域载体 ──
            // ⚠ 载体订正（实测，本仓典型陷阱）：PNG 等 RGB 原生格式的**目标**色域写在
            //   `-vf` 的 zscale 滤镜里（`p=`/`t=`/`m=`）；`-color_primaries`/`-color_trc`
            //   是**输入声明**（实测 Display P3 下它们恒为 bt709/iec61966-2-1）。
            //   原断言拿 -color_primaries 当目标载体 ⇒ 载体选错。现改为 ZscaleParam。
            Safe("J8 ColorSpaceCombo×7", () =>
            {
                SetFormat("PNG"); Pump(10);
                var cs = Find<ComboBox>("ColorSpaceCombo");
                if (cs == null) { Check("J8 combo存在", false, "null"); return; }
                var items = new List<string>();
                for (int i = 0; i < cs.ItemCount; i++) items.Add(cs.Items[i] as string ?? "<null>");
                Console.WriteLine("      [J8] ColorSpaceCombo items=[" + string.Join(", ", items) + "]");
                // 语料自检：7 个取值必须都在（防「选项被过滤掉 ⇒ SelectedItem=null ⇒ 断言恒真」）
                var want = new[] { "auto", "sRGB", "BT.709", "Display P3", "P3 PQ", "BT.2020 PQ", "BT.2020 HLG" };
                var missing = new List<string>();
                foreach (var w in want) if (!items.Contains(w)) missing.Add(w);
                Check("J8 语料自检 7 取值齐备", missing.Count == 0, "missing=" + string.Join(",", missing));

                // auto：不得出现任何广色域目标 token（zscale 目标 + iccgen 双载体都要查）
                cs.SelectedItem = "auto"; Pump(8); Regen();
                var tA = Tok(Cmd());
                Check("J8 auto⇒无zscale目标/无iccgen",
                      ZscaleParam(tA, "p") == null && !VfHas(tA, "iccgen"),
                      $"p={ZscaleParam(tA, "p") ?? "<none>"} iccgen={VfHas(tA, "iccgen")}");

                var cases = new[,] {
                    // sRGB 是「隐含默认目标」：与源一致 ⇒ **不写 zscale**，只写 CICP 声明
                    { "sRGB",         "",         "",             null,     "bt709", "iec61966-2-1" },
                    { "BT.709",       "bt709",    "bt709",        null,     null,    null },
                    { "Display P3",   "smpte432", "iec61966-2-1", "iccgen", null,    null },
                    { "P3 PQ",        "bt2020",   "smpte2084",    null,     null,    null },
                    { "BT.2020 PQ",   "bt2020",   "smpte2084",    null,     null,    null },
                    { "BT.2020 HLG",  "bt2020",   "arib-std-b67", null,     null,    null },
                };
                for (int i = 0; i < cases.GetLength(0); i++)
                {
                    var v = cases[i, 0]; var ep = cases[i, 1]; var et = cases[i, 2];
                    var filt = cases[i, 3]; var cp = cases[i, 4]; var ct = cases[i, 5];
                    // ⚠⚠ 2026-10-07 修复（J8 断言载体缺陷）：**每次选格前必须回落到 `auto`**。
                    //   根因（读 `ColorSpaceRegistry` 的两条 Spec 定义，逐字对拍）：
                    //     Key = "P3 PQ"        → OutPrimaries=bt2020, OutTrc=smpte2084, OutMatrix=bt2020nc
                    //     Key = "BT.2020 PQ"   → OutPrimaries=bt2020, OutTrc=smpte2084, OutMatrix=bt2020nc
                    //   ⇒ 这两个**面向用户的不同选项**在 ffmpeg 出口是**同一组 token**（设计如此：
                    //     P3⊂BT.2020，超集零削色）。而 `MainWindow` 的色域联动走 `SelectComboItem`
                    //     （**选到同值不触发变更**）⇒ 紧邻 `P3 PQ` 之后再选 `BT.2020 PQ` 时，
                    //     高级三元组**没有发生任何变化** ⇒ 某些轮次下 `Regen()` 的产出与期望载体不符，
                    //     表现为 `p=<none> t=<none>`（zscale 目标为空）。
                    //   ⇒ 修法：先 `auto`（清目标）**再**选目标格，保证每次都发生一次真实跃迁。
                    //   ⚠ 真正根因（2026-10-07 用诊断行定位，**不是**猜测）：
                    //     `UpdateFormatCapabilities()` 在 `hasHdr` 变化时会**重建 Items 并把
                    //     `ColorSpaceCombo.SelectedIndex` 复位成 0（= `auto`）**
                    //     （`MainWindow.xaml.cs:2222-2245`，复位就在 `:2245`）；它的触发链含
                    //     **fire-and-forget 的 `RefreshEncoderListAsync()`**（`:2051`，async）
                    //     ⇒ 直接 `cs.SelectedItem = v` 后立刻读，可能读到**被复位后的 `auto`**。
                    //     实测诊断行：`want=P3 PQ comboNow=auto gp=<none> gt=<none>` —— 赋值被吞掉。
                    //     这也解释了红格为何在轮次间**漂移**（`P3 PQ`/`BT.2020 PQ`/`J6 HLG`）：
                    //     异步落地时机不同。
                    //   ⇒ 修法：改用 `SelectColorSpaceSettled`（边选边等，直到目标**稳定在位**）；
                    //     等不到就**报红**（不静默通过）。
                    //   ⚠ 这是**等待正向完成条件**，不是 `Thread.Sleep` 冒充等待（本仓 §1.2 铁律）。
                    //   ⚠ 产品侧已由 CLI 反证为**正确**（`--color-space "P3 PQ"` 与 `"BT.2020 PQ"`
                    //     实测产出**逐字相同**且都正确的 zscale `p=bt2020:t=smpte2084`）
                    //     ⇒ **不得**为让本格变绿去改产品。
                    //   同族记载：`docs/TESTING.md` §6 第 74 条「断言『载体』选错 ⇒ 命中另一个合法 token」。
                    if (!SelectColorSpaceSettled(v))
                        Check("J8 " + v + " 目标色域能稳定选中（前置，防异步复位吞掉赋值）", false,
                              $"选中后 combo 实际停在「{cs.SelectedItem as string ?? "<null>"}」⇒ 本格读数不可信");
                    Regen();
                    // ⚠ 诊断行（2026-10-07）：把**真实控件状态**打出来，供定位"读到残留状态"这类缺陷。
                    //   本仓教训：断言失败时若只有期望/实得两个值，无法区分
                    //   ① 控件没被真正改动 ② 联动没触发 ③ 下游决策丢了 —— 三种成因修法完全不同。
                    var csNow = cs.SelectedItem as string;
                    var advOn = Find<CheckBox>("UseAdvancedColor")?.IsChecked;
                    Console.WriteLine($"      [J8dbg] want={v} comboNow={csNow ?? "<null>"} advColor={advOn} " +
                                      $"gp={ZscaleParam(Tok(Cmd()), "p") ?? "<none>"} gt={ZscaleParam(Tok(Cmd()), "t") ?? "<none>"}");
                    var t = Tok(Cmd());
                    var gp = ZscaleParam(t, "p"); var gt = ZscaleParam(t, "t");
                    bool ok;
                    string det;
                    if (v == "sRGB")
                    {
                        // 无 zscale + 输入声明即为 sRGB
                        ok = gp == null && gt == null
                             && Val(t, "-color_primaries") == cp && Val(t, "-color_trc") == ct;
                        det = $"zscale.p={gp ?? "<none>"} -color_primaries={Val(t, "-color_primaries") ?? "<none>"} -color_trc={Val(t, "-color_trc") ?? "<none>"}";
                    }
                    else
                    {
                        ok = gp == ep && gt == et;
                        if (filt != null) ok = ok && VfHas(t, filt);
                        det = $"p={gp ?? "<none>"} t={gt ?? "<none>"} iccgen={VfHas(t, "iccgen")}";
                    }
                    Check("J8 " + v + (v == "sRGB" ? "⇒无zscale+CICP=bt709/sRGB" : "⇒p=" + ep + ":t=" + et + (filt != null ? "+" + filt : "")),
                          ok, det);
                }
                // 改回 auto ⇒ 广色域目标 token 必须消失（反向负控）
                cs.SelectedItem = "auto"; Pump(8); Regen();
                var tB = Tok(Cmd());
                Check("J8 auto回归⇒smpte432/iccgen 消失",
                      ZscaleParam(tB, "p") == null && !VfHas(tB, "iccgen"),
                      $"p={ZscaleParam(tB, "p") ?? "<none>"} iccgen={VfHas(tB, "iccgen")}");
            });

            // ── J9 LosslessCheck ──
            // ⚠ 实测：PNG/TIFF/APNG 被 `UpdateOptionAvailability` **强制无损并锁定**
            //   （LosslessCheck.IsEnabled=false + IsChecked=true + 质量锁 100）⇒ PNG 下无法做
            //   「开/关」对照。故 PNG 断言改为「强制态可见」，开关对照用 WebP（真正可选无损）。
            Safe("J9 LosslessCheck", () =>
            {
                var ll = Find<CheckBox>("LosslessCheck");
                if (ll == null) { Check("J9 LosslessCheck存在", false, "null"); return; }
                SetFormat("PNG"); Pump(8);
                Regen();
                var tPng = Tok(Cmd());
                // ⚠ 实测：`UpdateOptionAvailability` 本意是把 PNG 的「无损」锁定（IsEnabled=false），
                //   但 `RegenerateCommand` 每次都会调 `RestoreLosslessAndQuality()`，它把
                //   `!IsEnabled && SupportsLossless` 的控件**重新启用** ⇒ 锁定被抵消（见报告 D-new-3）。
                //   故本项只断言「值仍是强制无损 + 命令体现无损」，不断言 IsEnabled。
                Check("J9a PNG⇒无损被强制勾选+compression_level=0",
                      ll.IsChecked == true && Val(tPng, "-compression_level") == "0",
                      $"checked={ll.IsChecked} enabled={ll.IsEnabled} -compression_level={Val(tPng, "-compression_level") ?? "<none>"}");
                SetFormat("WebP"); Pump(8);
                if (ll.IsEnabled) ll.IsChecked = false;
                Regen();
                var wOff = Val(Tok(Cmd()), "-lossless");
                ll.IsChecked = true; Regen();
                var wOn = Val(Tok(Cmd()), "-lossless");
                Check("J9b WebP 关⇒无-lossless", wOff == null, $"-lossless={wOff ?? "<none>"}");
                Check("J9c WebP 开⇒-lossless=1", wOn == "1", $"-lossless={wOn ?? "<none>"}");
                ll.IsChecked = false; SetFormat("PNG"); Regen();
            });

            // ── J9d cjxl 光子噪声 × 无损（2026-09-30：无损与合成噪声互斥）──
            // 本段**只断言控件可用性状态机**（这是我改的那一处），不断言命令串：
            // headless 宿主里 PLAN 工具链探测不到 cjxl（同 C3/H11 对 exiftool 的自维注入注记），
            // `JxlCjxlPanel` 因此不可见 ⇒ 在这里比命令串会测到"没有 cjxl"而不是"规则对不对"。
            // 命令串那一半由两处各自钉住：`ServiceProbe wire` 的 ①-⑤（纯函数）与
            // `verify-color-wiring.ps1` 的 (10g)（真产品 CLI 端到端）。
            // 实测依据：`-d 0 --photon_noise_iso=400` ⇒ 99.46% 的 16-bit 样本偏离源，
            // 而 jxlinfo 仍报 "(possibly) lossless"（噪声在解码端合成，体积只多 10 B）。
            Safe("J9d cjxl 光子噪声×无损", () =>
            {
                var adv = Find<CheckBox>("UseAdvancedCodec");
                if (adv != null) adv.IsChecked = true;
                Pump(10);
                SetFormat("JXL"); Pump(10); Regen();
                var auto = Find<CheckBox>("CjxlAutoPhotonNoiseCheck");
                var isoBox = Find<NumericUpDown>("CjxlPhotonNoiseBox");
                var ll = Find<CheckBox>("LosslessCheck");
                if (auto == null || isoBox == null || ll == null)
                {
                    Check("J9d 控件存在", false, $"auto={auto != null} iso={isoBox != null} lossless={ll != null}");
                    return;
                }
                ll.IsChecked = false; Pump(6);
                auto.IsChecked = true; Pump(6);
                Check("J9d-a 非无损+自动模式 ⇒ 复选框可选、手动 ISO 置灰",
                      auto.IsEnabled && !isoBox.IsEnabled,
                      $"auto.Enabled={auto.IsEnabled} iso.Enabled={isoBox.IsEnabled}");
                auto.IsChecked = false; Pump(6);
                Check("J9d-b 非无损+手动 ISO ⇒ 两只都可选（正控：证明 a 的置灰不是恒假）",
                      auto.IsEnabled && isoBox.IsEnabled,
                      $"auto.Enabled={auto.IsEnabled} iso.Enabled={isoBox.IsEnabled}");
                ll.IsChecked = true; Pump(6);
                Check("J9d-c 无损 ⇒ 两只都置灰（叠加会静默破坏逐比特无损）",
                      !auto.IsEnabled && !isoBox.IsEnabled,
                      $"auto.Enabled={auto.IsEnabled} iso.Enabled={isoBox.IsEnabled}");
                // 撤掉无损必须**原样回来**（只撤锁、不吞用户意图；IsChecked 全程未被本方法改写）
                ll.IsChecked = false; Pump(6);
                Check("J9d-d 撤掉无损 ⇒ 可选性回来且勾选状态未被吞",
                      auto.IsEnabled && isoBox.IsEnabled && auto.IsChecked == false,
                      $"auto.Enabled={auto.IsEnabled} iso.Enabled={isoBox.IsEnabled} auto.Checked={auto.IsChecked}");
                ll.IsChecked = false; SetFormat("PNG"); Pump(8); Regen();
            });

            // ── J10 AppendPngExtCheck：**后置改名**，不得改变命令 token（否则是参数串污染）──
            Safe("J10 AppendPngExtCheck 不进命令串", () =>
            {
                SetFormat("PNG");
                var ap = Find<CheckBox>("AppendPngExtCheck");
                if (ap == null) { Check("J10 控件存在", false, "null"); return; }
                ap.IsChecked = false; Regen(); var a = Tok(Cmd());
                ap.IsChecked = true; Regen(); var b = Tok(Cmd());
                Check("J10 勾选前后 token 完全相同", TokStr(a) == TokStr(b),
                      $"diff={(!(TokStr(a) == TokStr(b)))} off={Snip(TokStr(a))}");
                ap.IsChecked = false; Regen();
            });

            // ── J11 最长边限制：-vf 里必须出现 scale=<N>:-2: 且改回后消失 ──
            // ⚠ 实测：BuildMaxDimensionScaleFilter 在「源最长边 ≤ 上限」时返回 null（有意跳过缩放）。
            //   源 src_8bit.png = 512x384 ⇒ 必须取 < 512 的上限才能触发；取 640/1280 是 no-op。
            Safe("J11 EnableMaxDimensionCheck + MaxDimensionBox", () =>
            {
                SetFormat("PNG"); Pump(8);
                var en = Find<CheckBox>("EnableMaxDimensionCheck");
                var box = Find<NumericUpDown>("MaxDimensionBox");
                if (en == null || box == null) { Check("J11 控件存在", false, "null"); return; }
                en.IsChecked = false; Regen();
                Check("J11a 未启用⇒无scale滤镜", !VfHasPrefix(Tok(Cmd()), "scale="),
                      $"vf={Val(Tok(Cmd()), "-vf") ?? "<none>"}");
                en.IsChecked = true; box.Value = 256; Pump(8); Regen();
                var t = Tok(Cmd());
                bool has256 = VfHasPrefix(t, "scale=256:");
                Check("J11b 启用+256(<源最长边512)⇒scale=256:", has256, $"vf={Val(t, "-vf") ?? "<none>"}");
                box.Value = 192; Pump(8); Regen();
                var t2 = Tok(Cmd());
                Check("J11c 改 192⇒scale=192: 且 256 消失",
                      VfHasPrefix(t2, "scale=192:") && !VfHasPrefix(t2, "scale=256:"),
                      $"vf={Val(t2, "-vf") ?? "<none>"}");
                box.Value = 1920; Pump(8); Regen();
                Check("J11d 上限>源最长边⇒无scale(有意跳过)",
                      !VfHasPrefix(Tok(Cmd()), "scale="), $"vf={Val(Tok(Cmd()), "-vf") ?? "<none>"}");
                en.IsChecked = false; Regen();
                Check("J11e 关闭⇒scale 消失", !VfHasPrefix(Tok(Cmd()), "scale="),
                      $"vf={Val(Tok(Cmd()), "-vf") ?? "<none>"}");
            });

            // ── J12 线程三控件：-threads 的**值** ──
            Safe("J12 AutoThreads/SingleThread/ThreadsBox→-threads", () =>
            {
                SetFormat("PNG");
                var auto = Find<CheckBox>("AutoThreadsCheck");
                var single = Find<CheckBox>("SingleThreadCheck");
                var tb = Find<NumericUpDown>("ThreadsBox");
                if (auto == null || single == null || tb == null) { Check("J12 控件存在", false, "null"); return; }
                auto.IsChecked = true; single.IsChecked = false; Pump(8); Regen();
                var vAuto = Val(Tok(Cmd()), "-threads");
                single.IsChecked = true; Pump(8); Regen();
                var vSingle = Val(Tok(Cmd()), "-threads");
                single.IsChecked = false; auto.IsChecked = false; Pump(8);
                tb.Value = 3; Pump(8); Regen();
                var v3 = Val(Tok(Cmd()), "-threads");
                Check("J12a 单线程⇒-threads=1", vSingle == "1", $"-threads={vSingle ?? "<none>"}");
                Check("J12b 手动 3⇒-threads=3", v3 == "3", $"-threads={v3 ?? "<none>"}");
                Check("J12c 自动⇒-threads 为数值且≠手动值",
                      vAuto != null && int.TryParse(vAuto, out var na) && na >= 1 && vAuto != "3",
                      $"auto={vAuto ?? "<none>"}");
                auto.IsChecked = true; Pump(8); Regen();
            });

            // ── J13 元数据载体：-map_metadata / -map_metadata:s:v 由**色彩策略**决定 ──
            // ⚠ 实测（重要载体订正）：MetadataModeCombo **不参与**命令串生成 ——
            //   全仓 grep 显示 `options.MetadataMode` 只被 QueueProcessor（后置元数据步骤）消费，
            //   FfmpegCommandBuilder 里 `-map_metadata` 的判据是 `strategy != CarryIcc`。
            //   ⇒ 本项断言两件事：① 两个载体的精确取值（按策略/容器能力）② MetadataModeCombo
            //     在预览命令里**必须不改变任何 token**（否则是「两个真值」漂移）。
            Safe("J13 元数据 token 载体 + MetadataModeCombo 不影响预览", () =>
            {
                var md = Find<ComboBox>("MetadataModeCombo");
                if (md == null) { Check("J13 combo存在", false, "null"); return; }

                // ① 非 CICP 格式（JPEG）+ 推荐策略 ⇒ 完全剥离
                SetFormat("JPEG"); Pump(10); PickFfmpegEncoder();
                SetStrategyRadio("IccModeNone");
                md.SelectedIndex = 0; Pump(8); Regen();
                var tJpeg = Tok(Cmd());
                Check("J13a JPEG+推荐⇒-map_metadata=-1 且 -map_chapters=-1",
                      Val(tJpeg, "-map_metadata") == "-1" && Val(tJpeg, "-map_chapters") == "-1",
                      $"-map_metadata={Val(tJpeg, "-map_metadata") ?? "<none>"} -map_chapters={Val(tJpeg, "-map_chapters") ?? "<none>"}");

                // ② CICP 格式（PNG）+ 推荐策略 ⇒ 容器元数据保留、流元数据剥离
                SetFormat("PNG"); Pump(10); Regen();
                var tPng = Tok(Cmd());
                Check("J13b PNG+推荐⇒-map_metadata=0 且 -map_metadata:s:v=-1",
                      Val(tPng, "-map_metadata") == "0" && Val(tPng, "-map_metadata:s:v") == "-1",
                      $"-map_metadata={Val(tPng, "-map_metadata") ?? "<none>"} -map_metadata:s:v={Val(tPng, "-map_metadata:s:v") ?? "<none>"}");

                // ③ MetadataModeCombo 两个取值 ⇒ 命令 token 必须完全相同（负控：若将来接线，本项转红）
                md.SelectedIndex = 1; Pump(8); Regen();
                var tDel = Tok(Cmd());
                md.SelectedIndex = 0; Pump(8); Regen();
                var tKeep = Tok(Cmd());
                Check("J13c MetadataModeCombo 不改预览 token（现状锁）",
                      TokStr(tDel) == TokStr(tKeep),
                      $"del={Snip(TokStr(tDel))} | keep={Snip(TokStr(tKeep))}");
            });

            // ── J14 高级编码面板：逐控件 token（UseAdvancedCodec 打开后才读）──
            Safe("J14a PngPredCombo + PngDpiBox", () =>
            {
                var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = true; Pump(10);
                SetFormat("PNG"); Pump(10);
                var pred = Find<ComboBox>("PngPredCombo");
                var dpi = Find<NumericUpDown>("PngDpiBox");
                if (pred == null || dpi == null) { Check("J14a 控件存在", false, "null"); return; }
                var pv = new List<string>();
                for (int i = 0; i < pred.ItemCount; i++) pv.Add(pred.Items[i] as string ?? "<null>");
                Console.WriteLine("      [J14a] PngPredCombo items=[" + string.Join(", ", pv) + "]");
                dpi.Value = 0; Regen();
                Check("J14a-1 pred 默认值进命令", Val(Tok(Cmd()), "-pred") == "paeth",
                      $"-pred={Val(Tok(Cmd()), "-pred") ?? "<none>"}");
                pred.SelectedIndex = 0; Regen();
                Check("J14a-2 pred=none⇒-pred=none", Val(Tok(Cmd()), "-pred") == "none",
                      $"-pred={Val(Tok(Cmd()), "-pred") ?? "<none>"}");
                pred.SelectedIndex = 5; Regen();
                Check("J14a-3 pred=mixed⇒-pred=mixed", Val(Tok(Cmd()), "-pred") == "mixed",
                      $"-pred={Val(Tok(Cmd()), "-pred") ?? "<none>"}");
                dpi.Value = 300; Pump(6); Regen();
                Check("J14a-4 DPI=300⇒-dpi=300", Val(Tok(Cmd()), "-dpi") == "300", $"-dpi={Val(Tok(Cmd()), "-dpi") ?? "<none>"}");
                dpi.Value = 0; Pump(6); Regen();
                Check("J14a-5 DPI=0⇒-dpi 消失", Val(Tok(Cmd()), "-dpi") == null,
                      $"-dpi={Val(Tok(Cmd()), "-dpi") ?? "<none>"}");
                pred.SelectedIndex = 4; Pump(6); Regen();
                if (ad != null) ad.IsChecked = false; Pump(6);
            });
            Safe("J14b WebpPresetCombo (有损分支)", () =>
            {
                var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = true; Pump(10);
                SetFormat("WebP"); Pump(10);
                // ⚠ WebP 的无损是**粘性**状态（PNG 强制无损后会带过来）⇒ 必须显式复位到有损，
                //   否则 -preset 会被有意丢弃（实测打印 "ignored in lossless mode"）。
                var ll = Find<CheckBox>("LosslessCheck"); if (ll != null && ll.IsEnabled) ll.IsChecked = false;
                Pump(6);
                var cb = Find<ComboBox>("WebpPresetCombo");
                if (cb == null) { Check("J14b 控件存在", false, "null"); return; }
                foreach (var v in new[] { "photo", "drawing", "icon" })
                {
                    cb.SelectedItem = v; Pump(6); Regen();
                    var got = Val(Tok(Cmd()), "-preset");
                    Check("J14b " + v, got == v, $"-preset={got ?? "<none>"} expect={v} ({UiState()})");
                }
                cb.SelectedItem = "picture"; Pump(6); Regen();
                if (ad != null) ad.IsChecked = false; Pump(6);
            });
            Safe("J14c JpegHuffmanCombo + JpegDctCombo (JPEG/mjpeg)", () =>
            {
                var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = true; Pump(10);
                SetFormat("JPEG"); Pump(10);
                PickFfmpegEncoder();
                var hf = Find<ComboBox>("JpegHuffmanCombo");
                var dc = Find<ComboBox>("JpegDctCombo");
                if (hf == null || dc == null) { Check("J14c 控件存在", false, "null"); return; }
                hf.SelectedItem = "optimal"; dc.SelectedItem = "int"; Pump(6); Regen();
                var t = Tok(Cmd());
                Check("J14c-1 optimal⇒-huffman=1", Val(t, "-huffman") == "1", $"-huffman={Val(t, "-huffman") ?? "<none>"}");
                Check("J14c-2 dct=int⇒-dct=int", Val(t, "-dct") == "int", $"-dct={Val(t, "-dct") ?? "<none>"}");
                hf.SelectedItem = "default"; dc.SelectedItem = "auto"; Pump(6); Regen();
                var t2 = Tok(Cmd());
                Check("J14c-3 default⇒-huffman=0", Val(t2, "-huffman") == "0", $"-huffman={Val(t2, "-huffman") ?? "<none>"}");
                Check("J14c-4 dct=auto⇒-dct 消失", Val(t2, "-dct") == null, $"-dct={Val(t2, "-dct") ?? "<none>"}");
                hf.SelectedItem = "optimal"; Pump(6); Regen();
                if (ad != null) ad.IsChecked = false; Pump(6);
            });
            // ⚠ J14d 原为**缺陷登记项（P1-F 缺陷 3）**：TIFF 被 UpdateOptionAvailability 强制无损锁定
            //   （LosslessCheck.IsChecked=true, IsEnabled=false），而 FfmpegCommandBuilder 的
            //   `case "tiff"` 原先是 `if (options.Lossless) -compression_algo raw else TiffCompressionAlgo`
            //   ⇒ **Lossless 优先** ⇒ TiffCompressionCombo 的任何选择都被覆盖成 raw（下拉框成死控件）。
            //   ⚠ 2026-09-19（P1-F）**该缺陷已修**：builder 改为「用户选择优先；未指定算法才回落 raw」。
            //   定性依据：raw/lzw/deflate/packbits 四种**全是无损**算法（实测 8-bit/16-bit 源 PSNR 均
            //   `average:inf`，仅体积不同）⇒「无损就必须 raw」不成立。
            //   ⇒ 本项断言随之**由「锁住缺陷现状」反转为「锁住修复后行为」**：这是本波次**唯一**被改写的
            //     既有断言，已在报告里单独登记。它不是放宽/删除：新期望更严（旧断言只要求恒为 raw，
            //     新断言要求逐值精确等于用户所选），并按硬约束 §4 同时补了负控 J14d-4。
            Safe("J14d TiffCompressionCombo（P1-F 缺陷 3 已修：选择生效）", () =>
            {
                var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = true; Pump(10);
                SetFormat("TIFF"); Pump(10);
                var ll = Find<CheckBox>("LosslessCheck");
                var cb = Find<ComboBox>("TiffCompressionCombo");
                if (cb == null || ll == null) { Check("J14d 控件存在", false, "null"); return; }
                var items = new List<string>();
                for (int i = 0; i < cb.ItemCount; i++) items.Add(cb.Items[i] as string ?? "<null>");
                Console.WriteLine("      [J14d] TiffCompressionCombo items=[" + string.Join(", ", items) + "]");
                Check("J14d-1 TIFF 无损被强制勾选", ll.IsChecked == true,
                      $"checked={ll.IsChecked} enabled={ll.IsEnabled}");
                var cmds = new List<string>();
                foreach (var v in new[] { "lzw", "deflate", "packbits" })
                {
                    if (!items.Contains(v)) { Check("J14d " + v + " 可选", false, "not in items"); continue; }
                    cb.SelectedItem = v; Pump(6); Regen();
                    var got = Val(Tok(Cmd()), "-compression_algo");
                    Check("J14d-2 " + v + " 生效(P1-F 缺陷3 已修)", got == v,
                          $"-compression_algo={got ?? "<none>"} expect={v}");
                    cmds.Add(Cmd());
                }
                // 负控（改断言必补，硬约束 §4）：三个取值的命令串必须互不相同。
                // 若 `Lossless ⇒ raw` 覆盖回归，三条命令会逐字节相同 ⇒ 本负控转红。
                bool distinct = true;
                for (int i = 0; i < cmds.Count; i++)
                    for (int j = i + 1; j < cmds.Count; j++)
                        if (cmds[i] == cmds[j]) distinct = false;
                Check("J14d-4 负控 三取值命令串互异", distinct,
                      $"{cmds.Count} 条命令存在重复 ⇒ 选择未生效");
                // 字段本身有效（不经 UI 的强制无损）：直接构造 options 证明映射通
                var src = System.IO.Path.Combine(SrcDir, "src_8bit.png");
                var o = new FfmpegGui.Models.FfmpegOptions { Format = "tiff", Lossless = false, TiffCompressionAlgo = "deflate" };
                var args = FfmpegGui.Services.FfmpegCommandBuilder.BuildArguments(o, src, src);
                Check("J14d-3 TiffCompressionAlgo=deflate&Lossless=false⇒-compression_algo=deflate",
                      Val(Tok(args), "-compression_algo") == "deflate",
                      $"-compression_algo={Val(Tok(args), "-compression_algo") ?? "<none>"}");
                // J14d-5：**无损且未指定算法**时保持原默认 raw（旧行为逐字保留 ⇒ 防「顺手改掉默认」的回归）
                var o2 = new FfmpegGui.Models.FfmpegOptions { Format = "tiff", Lossless = true };
                var args2 = FfmpegGui.Services.FfmpegCommandBuilder.BuildArguments(o2, src, src);
                Check("J14d-5 无损且未指定算法⇒raw(默认保留)",
                      Val(Tok(args2), "-compression_algo") == "raw",
                      $"-compression_algo={Val(Tok(args2), "-compression_algo") ?? "<none>"}");
                if (ad != null) ad.IsChecked = false; Pump(6);
            });
            Safe("J14d2 TiffDpiBox（P1-B 修复项）", () =>
            {
                var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = true; Pump(10);
                SetFormat("TIFF"); Pump(10);
                var dpi = Find<NumericUpDown>("TiffDpiBox");
                if (dpi == null) { Check("J14d2 控件存在", false, "null"); return; }
                dpi.Value = 144; Pump(8); Regen();
                Check("J14d2-1 DPI=144⇒-dpi=144", Val(Tok(Cmd()), "-dpi") == "144",
                      $"-dpi={Val(Tok(Cmd()), "-dpi") ?? "<none>"}");
                dpi.Value = 0; Pump(8); Regen();
                Check("J14d2-2 DPI=0⇒-dpi 消失", Val(Tok(Cmd()), "-dpi") == null,
                      $"-dpi={Val(Tok(Cmd()), "-dpi") ?? "<none>"}");
                if (ad != null) ad.IsChecked = false; Pump(6);
            });
            Safe("J14e JxlEffortBox + JxlModularCheck (JPEG XL/libjxl)", () =>
            {
                var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = true; Pump(10);
                SetFormat("JPEG XL"); Pump(10);
                PickFfmpegEncoder();   // 默认是 cjxl（外部工具）⇒ 必须切到 libjxl 才走 ffmpeg 参数
                var eff = Find<NumericUpDown>("JxlEffortBox");
                var mod = Find<CheckBox>("JxlModularCheck");
                if (eff == null || mod == null) { Check("J14e 控件存在", false, "null"); return; }
                eff.Value = 3; mod.IsChecked = false; Pump(8); Regen();
                var t = Tok(Cmd());
                Check("J14e-1 effort=3⇒-effort=3", Val(t, "-effort") == "3", $"-effort={Val(t, "-effort") ?? "<none>"}");
                Check("J14e-2 modular 关⇒无 -modular", Val(t, "-modular") == null, $"-modular={Val(t, "-modular") ?? "<none>"}");
                mod.IsChecked = true; Pump(8); Regen();
                Check("J14e-3 modular 开⇒-modular=1", Val(Tok(Cmd()), "-modular") == "1",
                      $"-modular={Val(Tok(Cmd()), "-modular") ?? "<none>"}");
                mod.IsChecked = false; eff.Value = 7; Pump(8); Regen();
                if (ad != null) ad.IsChecked = false; Pump(6);
            });
            Safe("J14f AvifCpuUsedBox + AvifCdefCheck + AvifRowMtCheck", () =>
            {
                var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = true; Pump(10);
                SetFormat("AVIF"); Pump(10);
                // ⚠ 必须显式选 libaom-av1：默认 libsvtav1 走 SVT 分支，-cpu-used/-enable-cdef 是 libaom 参数。
                //   也不能用 PickFfmpegEncoder（会先命中带 "⚡ " 前缀的 av1_nvenc，触发 D-new-2）。
                PickEncoder(s => s.StartsWith("libaom-av1 —", StringComparison.Ordinal));
                var cpu = Find<NumericUpDown>("AvifCpuUsedBox");
                if (cpu == null) { Check("J14f 控件存在", false, "null"); return; }
                cpu.Value = 2; Pump(8); Regen();
                Check("J14f-1 cpu-used=2", Val(Tok(Cmd()), "-cpu-used") == "2",
                      $"-cpu-used={Val(Tok(Cmd()), "-cpu-used") ?? "<none>"}");
                var cdef = Find<CheckBox>("AvifCdefCheck");
                if (cdef != null)
                {
                    cdef.IsChecked = false; Pump(8); Regen();
                    Check("J14f-2 cdef 关⇒-enable-cdef=0", Val(Tok(Cmd()), "-enable-cdef") == "0",
                          $"-enable-cdef={Val(Tok(Cmd()), "-enable-cdef") ?? "<none>"}");
                    cdef.IsChecked = true; Pump(8); Regen();
                    Check("J14f-3 cdef 开⇒-enable-cdef=1", Val(Tok(Cmd()), "-enable-cdef") == "1",
                          $"-enable-cdef={Val(Tok(Cmd()), "-enable-cdef") ?? "<none>"}");
                }
                var rmt = Find<CheckBox>("AvifRowMtCheck");
                if (rmt != null)
                {
                    rmt.IsChecked = false; Pump(8); Regen();
                    Check("J14f-4 row-mt 关⇒无 -row-mt", Val(Tok(Cmd()), "-row-mt") == null,
                          $"-row-mt={Val(Tok(Cmd()), "-row-mt") ?? "<none>"}");
                    rmt.IsChecked = true; Pump(8); Regen();
                }
                cpu.Value = 5; Pump(8); Regen();
                if (ad != null) ad.IsChecked = false; Pump(6);
            });

            // ── J15 动图：AnimationFpsBox / AnimationScaleWBox / AnimationLoopBox / AnimationDurationBox ──
            // ⚠ 本项是 **P1-B 修复项**：修复前预览路径**完全不含**这四项（命令里连 token 都没有），
            //   而入队路径含 ⇒ 预览 ≠ 实际执行。修复点：MainWindow.xaml.cs 两处预览 opts 初始化。
            // ⚠ 载体选择（实测）：fps=/scale= 滤镜**只**由 builder 的 apng / jxl 分支写出
            //   （FfmpegCommandBuilder.cs:291-294 与 :342-345）；WebP 分支只写 -loop。
            //   ⇒ fps/scale 断言必须走 **JXL(libjxl→libjxl_anim)**；-loop 断言走 WebP。
            Safe("J15 动图参数 (JXL: fps/scale；WebP: -loop)", () =>
            {
                // ⚠ 2026-09-19 P1-E 任务 C —— **前提修正**（本组 6 条断言本体**逐字未改**）：
                //   本组原先**在静态模式下**给动图文本框赋值，依赖的正是「静态模式仍读动图参数」
                //   这一**已被判定为缺陷**的行为（静态 WebP 会被残留的 scale= 静默改尺寸；
                //   静态 JXL 会被换成 libjxl_anim）。修复后动图参数只在动图模式下读取
                //   （MainWindow.xaml.cs: IsAnimationMode()）⇒ 本组必须**先切到动图模式**，
                //   否则它测的就不再是「参数能否传到命令」而是「隐藏控件是否仍然生效」。
                //   ⚠ 载体选择（原注释，未变）：fps=/scale= 滤镜由 builder 的 apng/jxl 分支写出，
                //     -loop 由 WebP 分支写出。
                var modeJ15 = Find<ComboBox>("ConversionModeCombo");
                if (modeJ15 == null) { Check("J15-0 ConversionModeCombo 存在", false, "null"); return; }
                modeJ15.SelectedIndex = 1; Pump(16);          // 动图模式（本组前提）
                // ① JXL + libjxl：显式帧率 ⇒ 编码器切成 libjxl_anim + fps/scale 滤镜
                SetFormat("JPEG XL (动图)"); Pump(10);
                PickEncoder(s => s.StartsWith("libjxl —", StringComparison.Ordinal));
                var fps = Find<TextBox>("AnimationFpsBox");
                var sw = Find<TextBox>("AnimationScaleWBox");
                var lp = Find<TextBox>("AnimationLoopBox");
                var du = Find<TextBox>("AnimationDurationBox");
                if (fps == null || sw == null || lp == null) { Check("J15 控件存在", false, "null"); return; }
                fps.Text = ""; sw.Text = ""; lp.Text = ""; if (du != null) du.Text = "";
                Pump(14); Regen();
                var t0 = Tok(Cmd());
                Check("J15a 帧率空⇒无fps滤镜 且 -c:v 非 libjxl_anim",
                      !VfHasPrefix(t0, "fps=") && Val(t0, "-c:v") != "libjxl_anim",
                      $"-c:v={Val(t0, "-c:v") ?? "<none>"} vf={Val(t0, "-vf") ?? "<none>"}");
                fps.Text = "12"; sw.Text = "320"; Pump(14); Regen();
                var t1 = Tok(Cmd());
                Check("J15b fps=12⇒-c:v=libjxl_anim", Val(t1, "-c:v") == "libjxl_anim",
                      $"-c:v={Val(t1, "-c:v") ?? "<none>"}");
                Check("J15c fps=12⇒滤镜段 fps=12", VfHas(t1, "fps=12"), $"vf={Val(t1, "-vf") ?? "<none>"}");
                Check("J15d scaleW=320⇒滤镜段 scale=320:-1:flags=lanczos",
                      VfHas(t1, "scale=320:-1:flags=lanczos"), $"vf={Val(t1, "-vf") ?? "<none>"}");
                if (du != null)
                {
                    du.Text = "5"; Pump(14); Regen();
                    Check("J15e duration=5⇒-t=5", Val(Tok(Cmd()), "-t") == "5", $"-t={Val(Tok(Cmd()), "-t") ?? "<none>"}");
                    du.Text = ""; Pump(12);
                }
                fps.Text = ""; sw.Text = ""; Pump(14); Regen();
                var t3 = Tok(Cmd());
                Check("J15f 清空帧率⇒fps/scale 消失 且 -c:v 回 libjxl",
                      !VfHasPrefix(t3, "fps=") && Val(t3, "-c:v") == "libjxl",
                      $"-c:v={Val(t3, "-c:v") ?? "<none>"} vf={Val(t3, "-vf") ?? "<none>"}");

                // ② WebP：-loop（WebP 分支唯一的动图参数载体）
                SetFormat("WebP (动图)"); Pump(10);
                var ll = Find<CheckBox>("LosslessCheck"); if (ll != null && ll.IsEnabled) ll.IsChecked = false;
                lp.Text = "3"; Pump(14); Regen();
                Check("J15g WebP loop=3⇒-loop=3", Val(Tok(Cmd()), "-loop") == "3",
                      $"-loop={Val(Tok(Cmd()), "-loop") ?? "<none>"}");
                lp.Text = "0"; Pump(14); Regen();
                Check("J15h WebP loop=0⇒-loop=0", Val(Tok(Cmd()), "-loop") == "0",
                      $"-loop={Val(Tok(Cmd()), "-loop") ?? "<none>"}");
                lp.Text = ""; Pump(12); Regen();
                // ⚠ P1-F 缺陷 4 已修（2026-09-19）：WebP 分支现在也写 fps/scale 滤镜
                //   ⇒ 原先「WebP 只写 -loop」不再是全部事实。本组只锁 -loop；
                //     WebP 动图 fps/scale 的断言见 GroupL 的 L4。
                Console.WriteLine("      [info] J15 WebP 分支已断言 -loop；fps/scaleW 的断言见 GroupL/L4（P1-F 缺陷 4 已修）");
            });

            // ── J16 JPEG→JXL 无损重封装那只**共用**控件（2026-09-30：开关默认启用）──
            // 控件从 `JxlCjxlPanel` 迁到 `JxlLosslessJpegPanel`：JPEG 与 JPEG XL 两格共用**同一只**。
            // 迁走的理由与 `JpegGainMapPanel` 同一条：嵌进任一只按格式切换的面板，另一格就看不见它，
            // 只能再放第二只 ⇒ 两只控件 = 两个真值。命令形状那一半由 `ServiceProbe wire` 的 jbrd-①~⑥
            // 与 `verify-jxl-codestream-route` 的 ⑩（真产物 jbrd）各自钉住，这里只锁货构型。
            Safe("J16 jbrd 共用面板", () =>
            {
                var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = true; Pump(10);
                // ⚠ 上一组 J15 把转换模式留在**动图**（items = AnimatedFormats，里面没有 "PNG"/"JPEG"）
                //   ⇒ 那时 SetFormat("PNG") 会把 SelectedItem 置空、格式**没变**，负控就成了假红。
                //   先复位到静图，再谈格式切换。
                var mode = Find<ComboBox>("ConversionModeCombo"); if (mode != null) mode.SelectedIndex = 0; Pump(10);
                var panel = Find<StackPanel>("JxlLosslessJpegPanel");
                var chk = Find<CheckBox>("JxlLosslessJpegCheck");
                if (panel == null || chk == null)
                {
                    Check("J16a 控件存在且已迁到共用面板", false, $"panel={panel != null} chk={chk != null}");
                    return;
                }
                SetFormat("JPEG"); Pump(10);
                Check("J16a JPEG 格 ⇒ 共用面板可见、默认勾选",
                      panel.IsVisible && chk.IsChecked == true,
                      $"visible={panel.IsVisible} checked={chk.IsChecked}");
                SetFormat("PNG"); Pump(10);
                var fcombo = Find<ComboBox>("FormatCombo");
                Check("J16b 负控：PNG 格 ⇒ 面板隐藏（不是一只恒可见的装饰）",
                      !panel.IsVisible,
                      $"visible={panel.IsVisible} fmt={fcombo?.SelectedItem} nItems={fcombo?.Items?.Count} mode={Find<ComboBox>("ConversionModeCombo")?.SelectedIndex}");
                if (ad != null) ad.IsChecked = false; Pump(6);
            });

            SweepReset();
        }

        // ══════════════════════════ 组 L ══════════════════════════
        static void GroupL_ParamDefects()
        {
            Console.WriteLine("\n########## L组: P1-F UI→命令 参数传递缺陷 4 项 ##########");
            SweepReset();
            Console.WriteLine("  [state] " + UiState());

            // ── L1 缺陷1：硬件编码器显示名的 "⚡ " 图标前缀不得进 `-c:v` ──
            // 解析侧是**纯函数**（与机器有无 GPU 无关），故三条图标前缀各一条 + 无图标对照 + 2 条负控。
            Safe("L1 解析侧 ParseEncoderName 剥图标", () =>
            {
                var mi = typeof(FfmpegGui.Services.EncoderInfo)
                    .GetMethod("ParseEncoderName", BindingFlags.Public | BindingFlags.Static);
                if (mi == null) { Check("L1 ParseEncoderName 可反射", false, "not found"); return; }
                // 显示名与 EncoderInfo.DisplayName 的三条 gpuIcon 分支逐字同构
                // （GpuEncoderAvailability: Verified/DeviceFoundUntested ⇒ "⚡ "；CompiledNoDevice ⇒ "⚡⚠️ "；Failed ⇒ "⚡❌ "）
                var cases = new[,] {
                    { "⚡ av1_nvenc — NVIDIA NVENC av1 encoder (codec av1)", "av1_nvenc" },
                    { "⚡⚠️ av1_amf — AMD AMF av1 encoder (codec av1)", "av1_amf" },
                    { "⚡❌ mjpeg_nvenc — NVIDIA NVENC MJPEG encoder (codec mjpeg)", "mjpeg_nvenc" },
                    { "mjpeg — MJPEG (Motion JPEG)", "mjpeg" },
                    { "libaom-av1 — libaom AV1 (codec av1)", "libaom-av1" },
                };
                for (int i = 0; i < cases.GetLength(0); i++)
                {
                    var got = mi.Invoke(null, new object?[] { cases[i, 0] })?.ToString() ?? "<null>";
                    Check("L1a " + cases[i, 1] + " 解析精确", got == cases[i, 1],
                          $"in=\"{cases[i, 0]}\" got=\"{got}\" expect=\"{cases[i, 1]}\"");
                    bool clean = got.Length > 0;
                    foreach (var ch in got)
                        if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
                              || ch == '_' || ch == '-' || ch == '.')) clean = false;
                    Check("L1b " + cases[i, 1] + " 无空格/无图标", clean, $"got=\"{got}\"");
                }
                // 负控：空串 / 纯图标串都不得返回带图标的串（否则「剥离」恒真）
                var empty = mi.Invoke(null, new object?[] { "" })?.ToString() ?? "<null>";
                var iconOnly = mi.Invoke(null, new object?[] { "⚡ — x" })?.ToString() ?? "<null>";
                Check("L1c 负控 空串⇒空", empty == "", $"got=\"{empty}\"");
                Check("L1d 负控 纯图标⇒空(不残留图标)", iconOnly == "", $"got=\"{iconOnly}\"");
            });

            // UI 侧：从下拉框**真选**一个带图标前缀的编码器 ⇒ `-c:v` 必须是单个合法 token
            Safe("L1e UI 侧 硬件编码器 -c:v 单 token", () =>
            {
                SetFormat("AVIF"); Pump(12);
                var hw = PickEncoder(s => s.StartsWith("⚡", StringComparison.Ordinal));
                Check("L1e-0 本环境存在硬件编码器项", hw != null, "EncoderCombo 里没有以 ⚡ 开头的项");
                if (hw == null) return;
                Regen();
                var t = Tok(Cmd());
                var enc = Val(t, "-c:v");
                int ei = Idx(t, "-c:v");
                var next = (ei >= 0 && ei + 1 < t.Count) ? t[ei + 1] : null;
                var expect = typeof(FfmpegGui.Services.EncoderInfo)
                    .GetMethod("ParseEncoderName", BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, new object?[] { hw })?.ToString();
                DumpCmd("L1e hw=\"" + hw + "\"", Cmd());
                Check("L1f UI 侧 -c:v == ParseEncoderName(显示名)", enc == expect,
                      $"-c:v=\"{enc ?? "<none>"}\" expect=\"{expect ?? "<none>"}\"");
                Check("L1g UI 侧 -c:v 单 token(无空格/无图标)", enc != null && enc.Length > 0
                      && !enc.Contains(" ") && !enc.Contains("⚡") && !enc.Contains("⚠") && !enc.Contains("❌"),
                      $"-c:v=\"{enc ?? "<none>"}\"");
                Check("L1h UI 侧 -c:v 的下一 token 不是图标", next != null
                      && !next.Contains("⚡") && !next.Contains("—"),
                      $"next=\"{next ?? "<none>"}\" tokens={TokStr(t)}");
                // 负控：修复前 enc == "⚡" 且 next == 编码器名 ⇒ 下面这条必须能抓到「enc 是图标」
                Check("L1i 负控 断言有牙(enc 不得等于图标)", enc != "⚡" && enc != "⚡⚠️" && enc != "⚡❌",
                      $"enc=\"{enc ?? "<none>"}\"");
                // ── 显示侧同族缺陷（同一根因）的**直接**证据：硬件预设级别的取值路径 ──
                // GetAvifHwPresetLevel() 的四支判据（av1_nvenc/qsv/vaapi/amf）**全部**是 GPU 编码器
                //   ⇒ 显示串恒带 "⚡ " ⇒ 修复前 `StartsWith("av1_nvenc")` 恒假 ⇒ 每次落到「旧面板兜底」
                //   读**不存在的** HwPresetCombo（XAML 里没有该控件 ⇒ FindControl 返回 null
                //   ⇒ oldIdx = 1 ⇒ level = 2 ⇒ `-preset p2`），用户在自己面板上选的预设被静默替换。
                //   修复后走 nvenc 支：NvencPresetCombo.SelectedIndex=3 ⇒ level=4 ⇒ `-preset p4`。
                var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = true;
                Pump(10); Regen();
                // ⚠⚠ 2026-09-22（`HANDOVER §33` 项 2）：**本块需要的是「特定厂商」的 nvenc**，
                //   而上面 L1e 的 `PickEncoder(s => s.StartsWith("⚡"))` 只保证「**某个**硬件编码器」——
                //   它取的是**按名字排序后的第一个** `⚡` 项（列表按名字排序：`EncoderDetectionService.cs:346`），
                //   而本机同时有 `av1_amf`(AMD) 与 `av1_nvenc`(NVIDIA) 且 **`av1_amf` < `av1_nvenc`**
                //   ⇒ **`av1_amf` 只要被检测到，L1e 就必然选中它**（⚠ 实测它的检测是**间歇性**的：时有时无）
                //   ⇒ `encNow == "av1_nvenc"` 不成立 ⇒ 下面 else 直接判红。
                //   （整轮实测：两次红、红的还不是同一条 —— Run A 是 H10、Run B 是 L1j，后者报 `-c:v=av1_amf`。）
                //   ⇒ 这里**按名字显式选中 nvenc**（**不改 L1e 的语义与其厂商无关的覆盖**）：
                //     选择是**持久**的（L1e 的选择一直保留到本处），故本行**是承重的**。
                //   ⚠ 本机确实没有 nvenc 时 `PickEncoder` 返回 null ⇒ 走原 else（如实判红）。
                PickEncoder(s => s.Contains("av1_nvenc", StringComparison.Ordinal));
                Regen();
                var np = Find<ComboBox>("NvencPresetCombo");
                var encNow = Val(Tok(Cmd()), "-c:v");
                if (np != null && encNow == "av1_nvenc")
                {
                    np.SelectedIndex = 3; Pump(10); Regen();
                    var p3 = Val(Tok(Cmd()), "-preset");
                    Check("L1j 硬件预设级别走 nvenc 支(idx3⇒p4)", p3 == "p4",
                          $"-preset={p3 ?? "<none>"} (修复前= p2：走了旧面板兜底)");
                    np.SelectedIndex = 5; Pump(10); Regen();
                    var p5 = Val(Tok(Cmd()), "-preset");
                    Check("L1k 负控 预设随下拉变化(idx5⇒p6)", p5 == "p6",
                          $"-preset={p5 ?? "<none>"}");
                    np.SelectedIndex = 3; Pump(6);
                }
                else
                {
                    Check("L1j 硬件预设级别走 nvenc 支(idx3⇒p4)", false,
                          $"NvencPresetCombo={(np != null)} -c:v={encNow ?? "<none>"}");
                }
                if (ad != null) ad.IsChecked = false; Pump(8); Regen();
            });

            // ── L2 缺陷2：格式级控件锁定不得被「每次重生成命令」抵消 ──
            // 修复前：RegenerateCommand → RestoreLosslessAndQuality 判据 `!IsEnabled && SupportsLossless`
            //   ⇒ 把 UpdateOptionAvailability 刚设的 IsEnabled=false 打开（UI 说谎）。
            Safe("L2 纯无损格式控件锁定稳定", () =>
            {
                var ll = Find<CheckBox>("LosslessCheck");
                var q = Find<Slider>("QualitySlider");
                var qb = Find<TextBox>("QualityBox");
                if (ll == null || q == null) { Check("L2 控件存在", false, "null"); return; }
                var mode = Find<ComboBox>("ConversionModeCombo"); if (mode != null) mode.SelectedIndex = 0;
                Pump(8);

                SetFormat("PNG"); Pump(12); Regen();
                Check("L2a PNG 无损=勾选且禁用", ll.IsChecked == true && ll.IsEnabled == false,
                      $"IsChecked={ll.IsChecked} IsEnabled={ll.IsEnabled}");
                Check("L2b PNG 质量滑块禁用", q.IsEnabled == false, $"IsEnabled={q.IsEnabled}");
                if (qb != null) Check("L2c PNG 质量输入框禁用", qb.IsEnabled == false, $"IsEnabled={qb.IsEnabled}");
                Regen(); Regen();
                Check("L2d PNG 重生成两次后仍禁用(缺陷2核心)", ll.IsEnabled == false && q.IsEnabled == false,
                      $"lossless.IsEnabled={ll.IsEnabled} quality.IsEnabled={q.IsEnabled}");
                // 反射模拟「JXL 无损重封装锁」残留，再重生成命令：
                //   修复前 Restore 会把 IsEnabled 打开（PNG 的格式锁被抵消）；
                //   修复后只撤销 JXL 自己那把锁，格式锁仍然赢。
                typeof(FfmpegGui.MainWindow).GetMethod("LockLosslessForJxl", BF)!.Invoke(W, null);
                Check("L2e LockLosslessForJxl 生效", ll.IsEnabled == false, $"IsEnabled={ll.IsEnabled}");
                Regen();
                Check("L2f PNG 下 JXL 锁撤销后格式锁仍在", ll.IsEnabled == false,
                      $"IsEnabled={ll.IsEnabled} (修复前 RestoreLosslessAndQuality 会置 true)");

                SetFormat("TIFF"); Pump(12); Regen();
                Check("L2g TIFF 无损=勾选且禁用", ll.IsChecked == true && ll.IsEnabled == false,
                      $"IsChecked={ll.IsChecked} IsEnabled={ll.IsEnabled}");

                // 有损能力格式（SupportsLossless=true）⇒ 必须**可用**（证明不是永久锁死/一刀切）
                SetFormat("WebP"); Pump(12); Regen();
                Check("L2h WebP 无损可用", ll.IsEnabled == true, $"IsEnabled={ll.IsEnabled}");
                Check("L2i WebP 质量滑块可用", q.IsEnabled == true, $"IsEnabled={q.IsEnabled}");
                SetFormat("AVIF"); Pump(12); Regen();
                Check("L2j AVIF 无损可用", ll.IsEnabled == true, $"IsEnabled={ll.IsEnabled}");

                SetFormat("PNG"); Pump(12); Regen();
                Check("L2k 回到 PNG 再次禁用(幂等)", ll.IsEnabled == false, $"IsEnabled={ll.IsEnabled}");
            });

            // ── L3 缺陷3：TIFF 压缩算法下拉必须真正生效（此前被 `Lossless ⇒ raw` 覆盖成死控件）──
            // 定性 = **缺陷**：raw/lzw/deflate/packbits 四种**全是无损**算法
            //   （实测 8-bit 与 16-bit 源 PSNR 均 `average:inf`），「无损就必须 raw」不成立。
            Safe("L3 TIFF 压缩算法逐值生效", () =>
            {
                SetFormat("TIFF"); Pump(12);
                var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = true;
                Pump(10);
                var cb = Find<ComboBox>("TiffCompressionCombo");
                if (cb == null) { Check("L3 TiffCompressionCombo 存在", false, "null"); return; }
                var items = new List<string>();
                for (int i = 0; i < cb.ItemCount; i++) items.Add(cb.Items[i] as string ?? "<null>");
                Console.WriteLine("      [L3] TiffCompressionCombo items=[" + string.Join(", ", items) + "]");
                Check("L3a 四个算法在册", items.Count == 4 && items.Contains("raw")
                      && items.Contains("lzw") && items.Contains("deflate") && items.Contains("packbits"),
                      $"items=[{string.Join(",", items)}]");
                var cmds = new List<string>();
                foreach (var v in new[] { "raw", "lzw", "deflate", "packbits" })
                {
                    if (!items.Contains(v)) { Check("L3b " + v + " 可选", false, "not in items"); continue; }
                    cb.SelectedItem = v; Pump(8); Regen();
                    var got = Val(Tok(Cmd()), "-compression_algo");
                    Check("L3b " + v + " ⇒ -compression_algo=" + v, got == v,
                          $"-compression_algo={got ?? "<none>"} (修复前恒为 raw)");
                    cmds.Add(Cmd());
                }
                // 区分力负控：四个取值的命令串必须**互不相同**（修复前四者逐字节相同 ⇒ 死控件）
                bool distinct = true;
                for (int i = 0; i < cmds.Count; i++)
                    for (int j = i + 1; j < cmds.Count; j++)
                        if (cmds[i] == cmds[j]) distinct = false;
                Check("L3c 四个取值命令串互异(区分力负控)", distinct,
                      $"{cmds.Count} 条命令存在重复 ⇒ 用户选择未生效");
                // 高级关：TiffCompressionAlgo 由 UI 固定为 lzw（**现状锁**，不是缺陷）
                cb.SelectedIndex = 1; Pump(8);
                if (ad != null) ad.IsChecked = false; Pump(10); Regen();
                Check("L3d 高级关⇒-compression_algo=lzw(现状锁)",
                      Val(Tok(Cmd()), "-compression_algo") == "lzw",
                      $"-compression_algo={Val(Tok(Cmd()), "-compression_algo") ?? "<none>"}");
            });

            // ── L4 缺陷4：WebP 动图 fps / scaleW 必须进命令 ──
            // 定性 = **缺陷**（不是 ffmpeg 能力缺失）：同一 ffmpeg 下 libwebp 与 libwebp_anim
            //   **都**接受 `-vf "fps=…,scale=…"`（实测 10 帧 256x192 → 64x48、avg_frame_rate=10/1）。
            Safe("L4 WebP 动图 fps/scale 进命令", () =>
            {
                var mode = Find<ComboBox>("ConversionModeCombo");
                if (mode == null) { Check("L4 ConversionModeCombo 存在", false, "null"); return; }
                mode.SelectedIndex = 1; Pump(16);          // 动图模式
                SetFormat("WebP (动图)"); Pump(16);
                var ll = Find<CheckBox>("LosslessCheck"); if (ll != null && ll.IsEnabled) ll.IsChecked = false;
                var fps = Find<TextBox>("AnimationFpsBox");
                var sw = Find<TextBox>("AnimationScaleWBox");
                var lp = Find<TextBox>("AnimationLoopBox");
                if (fps == null || sw == null || lp == null) { Check("L4 动图控件存在", false, "null"); return; }
                fps.Text = ""; sw.Text = ""; lp.Text = ""; Pump(16); Regen();
                var t0 = Tok(Cmd());
                Check("L4a 空⇒无 fps/scale 滤镜",
                      !VfHasPrefix(t0, "fps=") && !VfHasPrefix(t0, "scale="),
                      $"vf={Val(t0, "-vf") ?? "<none>"}");
                fps.Text = "12"; sw.Text = "320"; lp.Text = "3"; Pump(16); Regen();
                var t1 = Tok(Cmd());
                DumpCmd("L4 webp anim fps=12 scaleW=320", Cmd());
                Check("L4b fps=12⇒滤镜段 fps=12", VfHas(t1, "fps=12"),
                      $"vf={Val(t1, "-vf") ?? "<none>"}");
                Check("L4c scaleW=320⇒滤镜段 scale=320:-1:flags=lanczos",
                      VfHas(t1, "scale=320:-1:flags=lanczos"), $"vf={Val(t1, "-vf") ?? "<none>"}");
                Check("L4d -loop=3 仍在(不回归)", Val(t1, "-loop") == "3",
                      $"-loop={Val(t1, "-loop") ?? "<none>"}");
                Check("L4e -vf 唯一(不得产生第二条)", CountTok(t1, "-vf") <= 1,
                      $"-vf count={CountTok(t1, "-vf")} vf={Val(t1, "-vf") ?? "<none>"}");
                fps.Text = ""; sw.Text = ""; lp.Text = ""; Pump(16); Regen();
                var t2 = Tok(Cmd());
                Check("L4f 负控 清空⇒fps/scale 消失",
                      !VfHasPrefix(t2, "fps=") && !VfHasPrefix(t2, "scale="),
                      $"vf={Val(t2, "-vf") ?? "<none>"}");
                mode.SelectedIndex = 0; Pump(16);
            });
            SweepReset();
        }

        // ══════════════ 入口（供 Program.Main 逐 mode 分派）══════════════
        // 只做转发 + 一个**共享收敛前置**：组体本身是逐字搬来的私有方法，保持原貌不动。
        internal static void RunJ()
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
            GroupJ_UiSweep();
        }
        internal static void RunL()
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
            // ⚠ 额外前置（见生成器 $explicitEntry['L'] 长注释）：把 AVIF 的编码器列表定型，
            //   否则 L1e-0 会因列表未换过来而红，并连带 L1f..L1k 六条**整段不执行**。
            SetFormat("AVIF");
            UiHost.AwaitEncoderListSettled("avif");
            GroupL_ParamDefects();
        }
    }
}
