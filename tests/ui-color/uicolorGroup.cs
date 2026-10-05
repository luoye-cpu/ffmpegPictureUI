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

namespace Tests.UiColor
{
    /// <summary>
    /// 子系统测试组：**ui-color** —— 2026-10-05 从 UiTestHost 的单个 3500 行
    /// <c>Program.cs</c> 中物理拆出。组体**逐字**搬来（断言文本一字未改，门禁在 grep 它们）。
    /// <para><b>共享 GUI 底座</b>：窗口 <c>W</c>、<c>TempDir</c>、以及全部 GUI/token 助手
    /// 都在 UiHost（UiHostShared 工程）—— 本类只保留同名**转发包装**，
    /// 不复制实现。</para>
    /// </summary>
    internal static class uicolorGroup
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

        // ══════════════════════════ 组 H ══════════════════════════
        static void GroupH_Color()
        {
            Console.WriteLine("\n########## H组: 色彩管线 (Registry/超广色域/CICP) ##########");
            var R = typeof(FfmpegGui.Services.ColorSpaceRegistry);
            // FfmpegTokens(name) -> (primaries,trc,matrix)
            (string?, string?, string?) Tokens(string n)
            {
                var t = R.GetMethod("FfmpegTokens")!.Invoke(null, new object?[] { n })!;
                var ty = t.GetType();
                return ((string?)ty.GetField("Item1")!.GetValue(t),
                        (string?)ty.GetField("Item2")!.GetValue(t),
                        (string?)ty.GetField("Item3")!.GetValue(t));
            }

            Safe("H1 Display P3 → smpte432+iec61966-2-1 (P5)", () =>
            {
                var (p, t, m) = Tokens("Display P3");
                Check("H1 P3 tagging", p == "smpte432" && t == "iec61966-2-1" && m == "bt709", $"{p}/{t}/{m}");
            });
            Safe("H2 P3 PQ → 归一 BT.2020 PQ (超集零削色, 非 sRGB 降级)", () =>
            {
                // P3 PQ 无法原样表达（实测）：zscale 不能烘 smpte432+PQ 组合、cjxl 无 P3+PQ 枚举、
                // iccgen 不支持 smpte2084 → ColorSpaceRegistry 故意归一到 BT.2020 PQ（P3 ⊂ BT.2020）。
                var (p, t, m) = Tokens("P3 PQ");
                Check("H2 P3PQ CICP", p == "bt2020" && t == "smpte2084" && m == "bt2020nc", $"{p}/{t}/{m}");
            });
            Safe("H3 Adobe RGB → 归一 bt2020 (D1)", () =>
            {
                var (p, _, m) = Tokens("Adobe RGB");
                Check("H3 Adobe归一BT2020", p == "bt2020" && m == "bt2020nc", $"{p}/{m}");
            });
            Safe("H4 Adobe→BT2020 线性矩阵数值正确", () =>
            {
                var mat = (double[]?)R.GetMethod("LinearMatrixToBt2020")!.Invoke(null, new object?[] { "Adobe RGB" });
                // 已知 Adobe RGB(D65)→BT.2020 线性 row0 ≈ [0.8775,0.0774,0.0452]
                bool ok = mat != null && mat.Length == 9
                    && Math.Abs(mat[0] - 0.8775) < 0.01 && Math.Abs(mat[1] - 0.0774) < 0.01
                    && Math.Abs(mat[2] - 0.0452) < 0.01
                    && Math.Abs(mat[0] + mat[1] + mat[2] - 1.0) < 0.01;
                Check("H4 矩阵数值", ok, mat == null ? "null" : string.Join(",", Array.ConvertAll(mat, x => x.ToString("0.0000"))));
            });
            Safe("H5 分类标志", () =>
            {
                bool uwA = (bool)R.GetMethod("IsUltraWide")!.Invoke(null, new object?[] { "Adobe RGB" })!;
                bool uwP = (bool)R.GetMethod("IsUltraWide")!.Invoke(null, new object?[] { "ProPhoto RGB" })!;
                bool uw3 = (bool)R.GetMethod("IsUltraWide")!.Invoke(null, new object?[] { "Display P3" })!;
                Check("H5 IsUltraWide", uwA && uwP && !uw3, $"adobe={uwA} prophoto={uwP} p3={uw3}");
            });
            Safe("H6 超广色域矩阵滤镜生成", () =>
            {
                var f = (string?)R.GetMethod("BuildUltraWideToBt2020Filter", new[] { typeof(string) })!.Invoke(null, new object?[] { "Adobe RGB" });
                var f3 = (string?)R.GetMethod("BuildUltraWideToBt2020Filter", new[] { typeof(string) })!.Invoke(null, new object?[] { "Display P3" });
                // 必须保留 SDR 传递(bt709)，不能回退到 PQ(smpte2084)。
                Check("H6 滤镜链", f != null && f.Contains("colorchannelmixer") && f.Contains("rr=0.87")
                      && f.Contains("t=bt709") && !f.Contains("smpte2084") && f3 == null,
                      Snip(f ?? "null") + " | p3=" + (f3 == null ? "null✓" : "NONNULL✗"));
            });
            Safe("H7 cjxl 简化模式 Display P3 → DisplayP3 (P4)", () =>
            {
                var opt = new FfmpegGui.Models.FfmpegOptions { ColorSpace = "Display P3", UseAdvancedColorParameters = false };
                var cs = FfmpegGui.Services.ColorEncodingHelper.MapToCjxlColorSpace(opt);
                Check("H7 cjxl P3", cs == "DisplayP3", $"cs={cs}");
            });
            Safe("H8 无 BT.601/<BT.709 泄漏", () =>
            {
                var (p601, _, _) = Tokens("BT.601");
                Check("H8 BT.601已移除", p601 == null, $"BT.601→{p601 ?? "null✓"}");
            });
            Safe("H9 MapPrimariesTransfer bt2020+PQ → Rec2100PQ", () =>
            {
                var cs = FfmpegGui.Services.ColorEncodingHelper.MapPrimariesTransfer("bt2020", "smpte2084");
                Check("H9 bt2020PQ→cjxl", cs == "Rec2100PQ", $"cs={cs}");
            });
            Safe("H12 SDR-BT.2020 无枚举→null (不误导Rec2100PQ)", () =>
            {
                var cs = FfmpegGui.Services.ColorEncodingHelper.MapPrimariesTransfer("bt2020", "bt709");
                Check("H12 SDR2020→null", cs == null, $"cs={cs ?? "null✓"}");
            });
            Safe("H13 HDR目标 BT.2020 PQ → cjxl CICP Rec2100PQ", () =>
            {
                var opt = new FfmpegGui.Models.FfmpegOptions { ColorSpace = "BT.2020 PQ", UseAdvancedColorParameters = false };
                var cs = FfmpegGui.Services.ColorEncodingHelper.MapToCjxlColorSpace(opt);
                Check("H13 HDR目标CICP", cs == "Rec2100PQ", $"cs={cs}");
            });
            Safe("H14 超广归一 HDR 感知 (PQ vs SDR)", () =>
            {
                // ⚠ 宿主过期修正（非产品回归）：ColorSpaceRegistry.cs:214 的实际签名是
                //   BuildUltraWideToBt2020Filter(string?, bool hdr, bool hlg, bool allowApproximateCurve = true)
                // 即 **4 参**。按 3 参 GetMethod 会返回 null ⇒ NRE。第 4 参传 true，与 1 参重载的默认行为一致
                // （H6 用的正是 1 参重载，Adobe RGB 的 2.2 曲线在 true 下可表达）。
                var mi = R.GetMethod("BuildUltraWideToBt2020Filter",
                    new[] { typeof(string), typeof(bool), typeof(bool), typeof(bool) });
                if (mi == null) { Check("H14 HDR-aware", false, "4-arg overload not found (宿主反射签名过期)"); return; }
                var hdr = (string?)mi.Invoke(null, new object?[] { "Adobe RGB", true, false, true });
                var sdr = (string?)mi.Invoke(null, new object?[] { "Adobe RGB", false, false, true });
                Check("H14 HDR-aware", hdr != null && hdr.Contains("t=smpte2084")
                      && sdr != null && sdr.Contains("t=bt709") && !sdr.Contains("smpte2084"),
                      $"hdrHasPQ={(hdr?.Contains("smpte2084"))} sdrHasBT709={(sdr?.Contains("t=bt709"))}");
            });
            Safe("H10 命令端到端含 smpte432 (PNG+Display P3)", () =>
            {
                var csCombo = Find<ComboBox>("ColorSpaceCombo");
                if (csCombo == null) { Check("H10 ColorSpaceCombo存在", false, "null"); return; }
                csCombo.SelectedItem = "Display P3"; Pump(6);
                SetFormat("PNG"); Regen();
                var c = Cmd();
                Check("H10 命令含smpte432且非bt2020", c.Contains("smpte432") && !c.Contains("bt2020"), Snip(c));
                csCombo.SelectedIndex = 0; Pump(4);
            });
            Safe("H11 真实ffmpeg: Adobe→BT.2020 矩阵转换可执行+正确标记", () =>
            {
                var filter = (string?)R.GetMethod("BuildUltraWideToBt2020Filter", new[] { typeof(string) })!.Invoke(null, new object?[] { "Adobe RGB" });
                if (filter == null) { Check("H11 滤镜非空", false, "null"); return; }
                var ff = System.IO.Path.Combine(RepoRoot, "publish", "PLAN", "ffmpeg-full", "ffmpeg.exe");
                var probeExe = System.IO.Path.Combine(RepoRoot, "publish", "PLAN", "ffmpeg-full", "ffprobe.exe");
                var src = System.IO.Path.Combine(SrcDir, "src_8bit.png");
                var outP = System.IO.Path.Combine(TempDir, "_h11_bt2020.png");   // 任务 B：临时产物不进共享语料目录
                try { if (System.IO.File.Exists(outP)) System.IO.File.Delete(outP); } catch { }
                var a1 = Run(ff, $"-y -hide_banner -loglevel error -i \"{src}\" -vf \"{filter}\" -color_primaries bt2020 -color_trc bt709 -colorspace bt2020nc \"{outP}\"");
                bool produced = a1 == 0 && System.IO.File.Exists(outP) && new System.IO.FileInfo(outP).Length > 0;
                string probeOut = produced ? RunOut(probeExe, $"-v error -select_streams v:0 -show_entries stream=color_primaries -of csv=p=0 \"{outP}\"") : "";
                Check("H11 Adobe→BT.2020 实转", produced && probeOut.Trim().Equals("bt2020", StringComparison.OrdinalIgnoreCase),
                      $"exit={a1} produced={produced} primaries={probeOut.Trim()}");
                // ── B1（P1-E 任务 B）：此刻中间产物**真实存在** ⇒ 当场断言它不在共享语料目录。
                //    必须放在块尾那次删除**之前**：删掉之后断言会恒真、失去区分力。
                Check("B1 H11 中间产物落在 TempDir 而非 SrcDir",
                      System.IO.File.Exists(outP)
                      && !System.IO.File.Exists(System.IO.Path.Combine(SrcDir, "_h11_bt2020.png")),
                      $"outP={outP} exists={System.IO.File.Exists(outP)}");
                try { if (System.IO.File.Exists(outP)) System.IO.File.Delete(outP); } catch { }
            });
        }

        // ══════════════════════════ 组 I ══════════════════════════
        static void GroupI_IccUi()
        {
            Console.WriteLine("\n########## I组: GUI ICC 选项→命令生效 ##########");
            SetInput(System.IO.Path.Combine(SrcDir, "src_8bit.png"));
            Safe("I1 None模式 命令无iccgen(源sRGB)", () =>
            {
                var rb = Find<RadioButton>("IccModeNone"); if (rb != null) rb.IsChecked = true; Pump();
                var cs = Find<ComboBox>("ColorSpaceCombo"); if (cs != null) cs.SelectedIndex = 0;
                SetFormat("PNG"); Regen(); var c = Cmd();
                Check("I1 None无iccgen", !c.Contains("iccgen"), Snip(c));
            });
            Safe("I2 Bake+目标DisplayP3 命令含smpte432+转换", () =>
            {
                var rb = Find<RadioButton>("IccModeBake"); if (rb != null) rb.IsChecked = true; Pump();
                var cs = Find<ComboBox>("ColorSpaceCombo"); if (cs != null) cs.SelectedItem = "Display P3"; Pump();
                SetFormat("PNG"); Regen(); var c = Cmd();
                Check("I2 Bake P3", c.Contains("smpte432") && (c.Contains("zscale") || c.Contains("iccgen")), Snip(c));
            });
            Safe("I3 ProPhoto 源 → 像素原样保真(无 zscale/iccgen)", () =>
            {
                // 源空间下拉已删（点二）、Adobe/ProPhoto 也不在「目标」快速选择里（zscale 无法命名其 primaries，
                // 从非 ProPhoto 源“转到 ProPhoto”无法诚实表达）→ 保真路径由「源 ICC 探测」驱动。
                // 用真实服务生成 ProPhoto ICC 并附着到测试源，验证 UI 生成的命令走保真分支。
                var icc = FfmpegGui.Services.IccProfileService.GetFaithfulIcc("ProPhoto RGB");
                if (icc == null) { Check("I3 ProPhoto保真", false, "GetFaithfulIcc 返回 null"); return; }
                // headless 主机无 PLAN 同级目录，且中途会被重新 Detect → 每次自维注入 exiftool 路径（同 C3）
                var etType = typeof(FfmpegGui.Services.ExifToolService);
                var exifReal = System.IO.Path.Combine(RepoRoot, "publish", "PLAN", "exiftool", "exiftool.exe");
                etType.GetField("_detectedPath", BFS)?.SetValue(null, System.IO.File.Exists(exifReal) ? exifReal : null);
                etType.GetField("_detected", BFS)?.SetValue(null, true);
                if (!FfmpegGui.Services.ExifToolService.IsAvailable)
                { Check("I3 ProPhoto保真", false, $"exiftool 不可用（{exifReal} 存在={System.IO.File.Exists(exifReal)}）"); return; }
                var pp = System.IO.Path.Combine(TempDir, "_prophoto_src.png");   // 任务 B：临时产物不进共享语料目录
                try
                {
                    System.IO.File.Copy(System.IO.Path.Combine(SrcDir, "src_8bit.png"), pp, true);
                    // Task.Run 包裹：避免在 UI 同步上下文 await 续体造成死锁（同产品代码做法）
                    var embedExit = System.Threading.Tasks.Task.Run(() => FfmpegGui.Services.ExifToolService
                        .EmbedIccProfileFromFileAsync(icc, pp, null)).GetAwaiter().GetResult();

                    var rb = Find<RadioButton>("IccModeNone"); if (rb != null) rb.IsChecked = true; Pump();
                    var cs = Find<ComboBox>("ColorSpaceCombo"); if (cs != null) cs.SelectedIndex = 0; Pump();
                    SetInput(pp); SetFormat("PNG"); Regen();
                    var c = Cmd();
                    // 探测链必须真能识别出 ProPhoto 源（struct 按值传参旧 bug 会使它永远为空），
                    // 且命令中不得出现任何像素转换/标签写入。
                    var m = FfmpegGui.Services.FfmpegCommandBuilder.ProbeInputColorMetadata(pp);
                    var expected = FfmpegGui.Services.FfmpegCommandBuilder.ResolveFaithfulUltraWideIcc(
                        new FfmpegGui.Models.FfmpegOptions { Format = "png" }, m.sourceGamutName, m.colorTrc, "png");
                    Check("I3 ProPhoto保真", m.sourceGamutName == "ProPhoto RGB" && m.hasIccSemantics
                            && expected != null
                            && !c.Contains("zscale") && !c.Contains("iccgen")
                            && !c.Contains("color_primaries") && !c.Contains("color_trc"),
                        $"icc={icc} embedExit={embedExit} etAvail={FfmpegGui.Services.ExifToolService.IsAvailable} " +
                        $"gamut={m.sourceGamutName} iccSem={m.hasIccSemantics} faithful={expected != null} :: {Snip(c)}");
                    // ── B2（P1-E 任务 B）：pp 此刻真实存在（finally 里才删）⇒ 当场断言它不在共享语料目录 ──
                    Check("B2 I3 中间产物落在 TempDir 而非 SrcDir",
                          System.IO.File.Exists(pp)
                          && !System.IO.File.Exists(System.IO.Path.Combine(SrcDir, "_prophoto_src.png")),
                          $"pp={pp} exists={System.IO.File.Exists(pp)}");
                }
                finally
                {
                    SetInput(System.IO.Path.Combine(SrcDir, "src_8bit.png"));
                    try { if (System.IO.File.Exists(pp)) System.IO.File.Delete(pp); } catch { }
                }
            });
            Safe("I4 Carry模式 命令含iccgen", () =>
            {
                var rb = Find<RadioButton>("IccModeCarry"); if (rb != null) rb.IsChecked = true; Pump();
                var cs = Find<ComboBox>("ColorSpaceCombo"); if (cs != null) cs.SelectedIndex = 0;
                SetFormat("PNG"); Regen(); var c = Cmd();
                Check("I4 Carry含iccgen", c.Contains("iccgen"), Snip(c));
                var rn = Find<RadioButton>("IccModeNone"); if (rn != null) rn.IsChecked = true; Pump();
            });

            // ── 色彩空间→ICC 模式联动 (新需求) ──
            void ResetTouched() => typeof(FfmpegGui.MainWindow).GetField("_iccModeTouched", BF)?.SetValue(W, false);
            void SetCs(string v) { var c = Find<ComboBox>("ColorSpaceCombo"); if (c != null) c.SelectedItem = v; Pump(6); }

            Safe("I5 选非BT.709(Display P3)→自动BakeToStandard", () =>
            {
                ResetTouched();
                SetCs("auto"); ResetTouched();
                SetCs("Display P3");
                Check("I5 P3→自动Bake", Find<RadioButton>("IccModeBake")?.IsChecked == true,
                      $"bake={Find<RadioButton>("IccModeBake")?.IsChecked}");
            });
            Safe("I6 回标准色域(sRGB)→自动回None", () =>
            {
                ResetTouched();
                SetCs("Display P3"); ResetTouched();
                SetCs("sRGB");
                Check("I6 sRGB→回None", Find<RadioButton>("IccModeNone")?.IsChecked == true,
                      $"none={Find<RadioButton>("IccModeNone")?.IsChecked}");
            });
            Safe("I7 用户手动选Carry后 色彩联动不覆盖(尊重手动)", () =>
            {
                ResetTouched();
                var carry = Find<RadioButton>("IccModeCarry"); if (carry != null) carry.IsChecked = true; Pump(4); // 手动 → touched=true
                SetCs("Display P3"); // 联动应因 touched 而跳过
                Check("I7 手动优先", Find<RadioButton>("IccModeBake")?.IsChecked != true
                                        && Find<RadioButton>("IccModeCarry")?.IsChecked == true,
                      $"bake={Find<RadioButton>("IccModeBake")?.IsChecked} carry={Find<RadioButton>("IccModeCarry")?.IsChecked}");
            });
            Safe("I8 P3 PQ 烘焙目标不降级(命令含 bt2020+smpte2084)", () =>
            {
                ResetTouched();
                SetCs("P3 PQ"); SetFormat("PNG"); Regen();
                // headless 下首个跨进程探测（exiftool/ffprobe）可能抢占消息循环，导致上一项测试
                // 之后的首次 ComboBox 赋值未生效 → 确认已选中，否则重选一次再再生成。
                if ((Find<ComboBox>("ColorSpaceCombo")?.SelectedItem as string) != "P3 PQ")
                { SetCs("P3 PQ"); Pump(12); Regen(); }
                var c = Cmd();
                // 归一化为 BT.2020 PQ（非 sRGB 降级、不丢 HDR 传递），与 Registry 决策一致
                Check("I8 P3PQ不降级", c.Contains("bt2020") && c.Contains("smpte2084"), Snip(c));
                SetCs("auto");
                var rn2 = Find<RadioButton>("IccModeNone"); if (rn2 != null) rn2.IsChecked = true; Pump();
            });

            // ── 8-bit 广色域源传播(高-1) + Bake 读探测源(中-3): 用 iccgen 生成真实 8-bit Display-P3 源 ──
            var ffExe = System.IO.Path.Combine(RepoRoot, "publish", "PLAN", "ffmpeg-full", "ffmpeg.exe");
            var p3 = System.IO.Path.Combine(TempDir, "_p3_8bit.png");   // 任务 B：临时产物不进共享语料目录
            try { Run(ffExe, $"-y -hide_banner -loglevel error -i \"{System.IO.Path.Combine(SrcDir, "src_8bit.png")}\" -vf \"format=rgb48le,zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=smpte432:t=iec61966-2-1:m=bt709,iccgen\" \"{p3}\""); } catch { }
            Safe("I9 8bit-P3源 auto→命令含smpte432 (高-1)", () =>
            {
                if (!System.IO.File.Exists(p3)) { Check("I9 P3源生成", false, "missing"); return; }
                // ── B3（P1-E 任务 B）：p3 此刻真实存在（I10 块尾才删）⇒ 当场断言它不在共享语料目录 ──
                Check("B3 I9 中间产物落在 TempDir 而非 SrcDir",
                      !System.IO.File.Exists(System.IO.Path.Combine(SrcDir, "_p3_8bit.png")), $"p3={p3}");
                SetInput(p3);
                var rb = Find<RadioButton>("IccModeNone"); if (rb != null) rb.IsChecked = true; Pump();
                var cs = Find<ComboBox>("ColorSpaceCombo"); if (cs != null) cs.SelectedIndex = 0;
                var sc = Find<ComboBox>("IccSourceSpaceCombo"); if (sc != null) sc.SelectedIndex = 0;
                SetFormat("PNG"); Regen(); var c = Cmd();
                Check("I9 8bitP3→smpte432", c.Contains("smpte432"), Snip(c));
            });
            Safe("I10 Bake P3源→BT.2020PQ 读探测源 (中-3)", () =>
            {
                var rb = Find<RadioButton>("IccModeBake"); if (rb != null) rb.IsChecked = true; Pump();
                var sc = Find<ComboBox>("IccSourceSpaceCombo"); if (sc != null) sc.SelectedIndex = 0; // auto(从文件检测)
                var cs = Find<ComboBox>("ColorSpaceCombo"); if (cs != null) cs.SelectedItem = "BT.2020 PQ"; Pump(6);
                SetFormat("PNG"); Regen(); var c = Cmd();
                // 修复前 bake 源会回落 sRGB(pin=bt709); 修复后读探测 smpte432。
                Check("I10 Bake源来自探测pin=smpte432", c.Contains("pin=smpte432") && c.Contains("p=bt2020"), Snip(c));
                var rn = Find<RadioButton>("IccModeNone"); if (rn != null) rn.IsChecked = true;
                if (cs != null) cs.SelectedIndex = 0;
                SetInput(System.IO.Path.Combine(SrcDir, "src_8bit.png"));
                try { if (System.IO.File.Exists(p3)) System.IO.File.Delete(p3); } catch { }
            });
        }

        // ══════════════════════════ 组 K ══════════════════════════
        static void GroupK_StrategyMap()
        {
            Console.WriteLine("\n########## K组: 四大模式 UI 映射 + 4→3 折叠 (P1-D) ##########");
            SweepReset();

            var radioNames = new[] { "IccModeNone", "IccModeCarry", "IccModeBake", "IccModeBakeOnly" };
            var strategies = new[] { "Recommended", "CarryIcc", "BakeCicpOnly", "Manual" };
            var cmds = new List<string>();

            // ── K1 UI 4 个策略单选：反射读回 GetColorStrategy + 命令串快照 ──
            // ⚠ 场景必须选**有区分力**的：PNG + sRGB 源 + 目标 auto 时，Recommended/Manual/BakeCicpOnly
            //   三条命令**逐字节相同**（无事可做）⇒ 「逐值验证」会退化成恒真。故统一取
            //   **显式目标 Display P3**（广色域目标：会触发 zscale 转换 + ICC 取舍），
            //   四策略才真正分叉。
            for (int i = 0; i < radioNames.Length; i++)
            {
                var rn = radioNames[i]; var exp = strategies[i];
                Safe("K1 " + rn, () =>
                {
                    SetFormat("PNG"); Pump(10);
                    var cs = Find<ComboBox>("ColorSpaceCombo");
                    if (cs != null) { cs.SelectedIndex = 0; Pump(6); }
                    SetStrategyRadio(rn);
                    if (cs != null) { cs.SelectedItem = "Display P3"; Pump(10); }
                    Regen();
                    var got = typeof(FfmpegGui.MainWindow).GetMethod("GetColorStrategy", BF)!.Invoke(W, null)!.ToString();
                    var sel = Find<ComboBox>("ColorSpaceCombo")?.SelectedItem as string;
                    Check("K1 " + rn + "→" + exp + " (目标=Display P3)", got == exp && sel == "Display P3",
                          $"GetColorStrategy={got} expect={exp} colorSpace={sel ?? "<null>"}");
                    cmds.Add(Cmd());
                    DumpCmd("K1 " + rn + "(" + exp + ") target=P3", Cmd());
                });
            }

            // ── K2 区分力：CarryIcc 与其余三者必须不同；BakeCicpOnly 与 Recommended 必须不同 ──
            // （这两条正是「4→3 折叠断言」的**负控**：若它们也相同，说明场景没有区分力、断言恒真）
            Safe("K2 策略区分力", () =>
            {
                if (cmds.Count != 4) { Check("K2 快照数=4", false, $"got={cmds.Count}"); return; }
                var sameAs = new List<string>();
                if (cmds[1] == cmds[0]) sameAs.Add("CarryIcc==Recommended");
                if (cmds[1] == cmds[2]) sameAs.Add("CarryIcc==BakeCicpOnly");
                if (cmds[1] == cmds[3]) sameAs.Add("CarryIcc==Manual");
                Check("K2a CarryIcc 与其余三者都不同", sameAs.Count == 0, "same=" + string.Join(",", sameAs));
                Check("K2b BakeCicpOnly 与 Recommended 不同", cmds[2] != cmds[0],
                      $"cicpOnly={Snip(cmds[2])} | rec={Snip(cmds[0])}");
                // 登记（非断言失败）：本场景下 Manual 与 Recommended 命令**逐字节相同**
                Console.WriteLine(cmds[3] == cmds[0]
                    ? "      [info] Manual 与 Recommended 在本场景逐字节相同（登记：两者语义不同但预览命令同形）"
                    : "      [info] Manual 与 Recommended 命令不同");
            });

            // ── K3 逐策略 token 级断言（ICC 载体精确取值，目标 Display P3 / PNG）──
            Safe("K3 逐策略 ICC token", () =>
            {
                if (cmds.Count != 4) { Check("K3 快照数=4", false, $"got={cmds.Count}"); return; }
                var tRec = Tok(cmds[0]); var tCar = Tok(cmds[1]); var tCicp = Tok(cmds[2]); var tMan = Tok(cmds[3]);
                // Recommended：归一转换 + 生成 ICC（zscale 到 P3 + iccgen）
                Check("K3a Recommended⇒zscale p=smpte432 且 iccgen",
                      ZscaleParam(tRec, "p") == "smpte432" && VfHas(tRec, "iccgen"),
                      $"p={ZscaleParam(tRec, "p") ?? "<none>"} iccgen={VfHas(tRec, "iccgen")}");
                // CarryIcc：**不转换**（无 zscale）+ 保留源流元数据
                Check("K3b CarryIcc⇒无 zscale + -map_metadata:s:v=0:s:v",
                      ZscaleParam(tCar, "p") == null && Val(tCar, "-map_metadata:s:v") == "0:s:v",
                      $"p={ZscaleParam(tCar, "p") ?? "<none>"} -map_metadata:s:v={Val(tCar, "-map_metadata:s:v") ?? "<none>"}");
                // BakeCicpOnly：转换但**禁 iccgen**（这是它与 Recommended 的唯一分叉点）
                Check("K3c BakeCicpOnly⇒zscale p=smpte432 且无 iccgen",
                      ZscaleParam(tCicp, "p") == "smpte432" && !VfHas(tCicp, "iccgen"),
                      $"p={ZscaleParam(tCicp, "p") ?? "<none>"} iccgen={VfHas(tCicp, "iccgen")}");
                // 负控：非 CarryIcc 三者必须**都剥离**源流元数据（否则 K3b 无区分力）
                Check("K3d 非 CarryIcc 剥离(-map_metadata:s:v=-1)",
                      Val(tRec, "-map_metadata:s:v") == "-1"
                      && Val(tCicp, "-map_metadata:s:v") == "-1"
                      && Val(tMan, "-map_metadata:s:v") == "-1",
                      $"rec={Val(tRec, "-map_metadata:s:v") ?? "<none>"} cicp={Val(tCicp, "-map_metadata:s:v") ?? "<none>"} man={Val(tMan, "-map_metadata:s:v") ?? "<none>"}");
            });

            // ── K4 CLI 层：--color-strategy 4 别名 → 对应策略（经 CliParser 解析 + ApplyExtraOption 映射）──
            Safe("K4 CLI --color-strategy 4 别名", () =>
            {
                var aliases = new[,] {
                    { "recommended", "Recommended" }, { "carry-icc", "CarryIcc" },
                    { "cicp-only",   "BakeCicpOnly" }, { "manual",    "Manual" },
                };
                var mi = typeof(FfmpegGui.CliParser).GetMethod("ApplyExtraOption",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (mi == null) { Check("K4 ApplyExtraOption 可反射", false, "not found"); return; }
                for (int i = 0; i < aliases.GetLength(0); i++)
                {
                    var alias = aliases[i, 0]; var exp = aliases[i, 1];
                    // 走**真实解析入口**：CliParser.Parse 把 `--color-strategy <v>` 收进 ExtraOptions
                    var parsed = FfmpegGui.CliParser.Parse(new[] { "--headless", "--color-strategy", alias });
                    bool inExtra = parsed.ExtraOptions.TryGetValue("color-strategy", out var val) && val == alias;
                    var opts = new FfmpegGui.Models.FfmpegOptions();
                    mi.Invoke(null, new object?[] { opts, "color-strategy", alias });
                    var got = opts.ColorStrategy.ToString();
                    Check("K4 " + alias + "→" + exp, inExtra && got == exp,
                          $"inExtra={inExtra} val={val ?? "<none>"} ColorStrategy={got}");
                }
            });

            // ── K5 4→3 折叠：旧 --icc-mode 的 None 与 BakeToStandard 必须**逐 token 完全相同** ──
            // 等价类必须用**同一输出路径**生成命令，否则路径 token 本身就会不同（那是假红）。
            Safe("K5 4→3 折叠 (--icc-mode None ≡ BakeToStandard)", () =>
            {
                var mi = typeof(FfmpegGui.CliParser).GetMethod("ApplyExtraOption",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (mi == null) { Check("K5 ApplyExtraOption 可反射", false, "not found"); return; }
                var src = System.IO.Path.Combine(SrcDir, "src_8bit.png");
                var outP = System.IO.Path.Combine(TempDir, "_k5_same_out.png");   // 任务 B：临时产物不进共享语料目录
                string Build(string iccMode)
                {
                    // ⚠ 目标必须显式（Display P3）：否则 Recommended 与 BakeCicpOnly 对本源无事可做、
                    //   命令逐字节相同 ⇒ 「折叠 ≠ 非折叠」的负控会失去区分力（恒真）。
                    var o = new FfmpegGui.Models.FfmpegOptions { Format = "png", ColorSpace = "Display P3" };
                    mi.Invoke(null, new object?[] { o, "icc-mode", iccMode });
                    return FfmpegGui.Services.FfmpegCommandBuilder.BuildArguments(o, src, outP);
                }
                var cNone = Build("None");
                var cBake = Build("BakeToStandard");
                var cCarry = Build("CarryIcc");
                var cOnly = Build("BakeOnly");
                var tNone = Tok(cNone); var tBake = Tok(cBake);
                Check("K5a None ≡ BakeToStandard (逐 token)", cNone == cBake,
                      $"none={Snip(cNone)} | bake={Snip(cBake)}");
                Check("K5b 折叠后与 CarryIcc 不同", cNone != cCarry, $"carry={Snip(cCarry)}");
                Check("K5c 折叠后与 BakeOnly 不同", cNone != cOnly, $"only={Snip(cOnly)}");
                // 折叠等价类必须落到 Recommended（语义层，不只字面相同）
                var oNone = new FfmpegGui.Models.FfmpegOptions();
                mi.Invoke(null, new object?[] { oNone, "icc-mode", "None" });
                var oBake = new FfmpegGui.Models.FfmpegOptions();
                mi.Invoke(null, new object?[] { oBake, "icc-mode", "BakeToStandard" });
                var oCarry = new FfmpegGui.Models.FfmpegOptions();
                mi.Invoke(null, new object?[] { oCarry, "icc-mode", "CarryIcc" });
                var oOnly = new FfmpegGui.Models.FfmpegOptions();
                mi.Invoke(null, new object?[] { oOnly, "icc-mode", "BakeOnly" });
                Check("K5d 折叠语义 = Recommended",
                      oNone.ColorStrategy == FfmpegGui.Models.ColorStrategy.Recommended
                      && oBake.ColorStrategy == FfmpegGui.Models.ColorStrategy.Recommended
                      && oCarry.ColorStrategy == FfmpegGui.Models.ColorStrategy.CarryIcc
                      && oOnly.ColorStrategy == FfmpegGui.Models.ColorStrategy.BakeCicpOnly,
                      $"none={oNone.ColorStrategy} bake={oBake.ColorStrategy} carry={oCarry.ColorStrategy} only={oOnly.ColorStrategy}");
                DumpCmd("K5 None", cNone);
                DumpCmd("K5 CarryIcc", cCarry);
                DumpCmd("K5 BakeOnly", cOnly);
            });

            // ── K6 ColorStrategyMapper.FromIccMode 逐值（纯映射层，不经 UI/CLI）──
            Safe("K6 FromIccMode 逐值", () =>
            {
                var M = typeof(FfmpegGui.Models.ColorStrategyMapper);
                var mi = M.GetMethod("FromIccMode", BindingFlags.Public | BindingFlags.Static);
                if (mi == null) { Check("K6 FromIccMode 可反射", false, "not found"); return; }
                var enumType = typeof(FfmpegGui.Models.IccMode);
                var cases = new[,] {
                    { "None", "Recommended" }, { "BakeToStandard", "Recommended" },
                    { "CarryIcc", "CarryIcc" }, { "BakeOnly", "BakeCicpOnly" },
                };
                for (int i = 0; i < cases.GetLength(0); i++)
                {
                    var v = Enum.Parse(enumType, cases[i, 0]);
                    var got = mi.Invoke(null, new[] { v })!.ToString();
                    Check("K6 " + cases[i, 0] + "→" + cases[i, 1], got == cases[i, 1],
                          $"FromIccMode({cases[i, 0]})={got}");
                }
            });

            // ── K7 TryParse 别名（ColorStrategy.cs:56-70）逐值 ──
            Safe("K7 TryParse 别名", () =>
            {
                var M = typeof(FfmpegGui.Models.ColorStrategyMapper);
                var mi = M.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static);
                if (mi == null) { Check("K7 TryParse 可反射", false, "not found"); return; }
                var cases = new[,] {
                    { "recommended", "Recommended" }, { "carry-icc", "CarryIcc" },
                    { "cicp-only", "BakeCicpOnly" }, { "manual", "Manual" },
                };
                for (int i = 0; i < cases.GetLength(0); i++)
                {
                    var args = new object?[] { cases[i, 0], null };
                    var ok = (bool)mi.Invoke(null, args)!;
                    var got = args[1]?.ToString() ?? "<null>";
                    Check("K7 " + cases[i, 0] + "→" + cases[i, 1], ok && got == cases[i, 1],
                          $"ok={ok} value={got}");
                }
                // 负控：非法取值必须返回 false（否则「能解析」恒真）
                var badArgs = new object?[] { "definitely-not-a-strategy", null };
                var badOk = (bool)mi.Invoke(null, badArgs)!;
                Check("K7 负控 非法取值⇒false", !badOk, $"ok={badOk}");
            });

            SweepReset();
        }

        // ══════════════════════════ 组 R ══════════════════════════
        static void GroupR_EncoderArgDomains()
        {
            Console.WriteLine("\n########## R组: 编码参数分域与取值域 ##########");

            int N(List<string> a, string t) => a.Count(x => x == t);
            bool No(List<string> a, params string[] ts) => ts.All(t => !a.Contains(t));
            string? After(List<string> a, string t)
            {
                int i = a.IndexOf(t);
                return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
            }
            var IA = typeof(FfmpegGui.Services.ColorMapping.ImageEncoderArgs);
            var buildAvif = IA.GetMethod("BuildAvifOptions", BindingFlags.Static | BindingFlags.Public)!;
            var buildGif = IA.GetMethod("BuildGifFilterChain", BindingFlags.Static | BindingFlags.Public)!;
            List<string> Avif(FfmpegGui.Models.FfmpegOptions o) =>
                (List<string>)buildAvif.Invoke(null, new object?[] { o })!;
            string? GifChain(FfmpegGui.Models.FfmpegOptions o) =>
                (string?)buildGif.Invoke(null, new object?[] { o, Array.Empty<string>() });

            FfmpegGui.Models.FfmpegOptions AvifBase(string encoder) => new()
            {
                Format = "avif", Encoder = encoder, Quality = 50,
                AvifCpuUsed = 4, AvifStillPicture = true, AvifRowMt = true,
                AvifAqMode = "variance", AvifEnableCdef = true, AvifEnableIntrabc = true,
                AvifHwPresetLevel = 4, AvifNvencSpatialAq = true, AvifNvencAqStrength = 8,
            };

            Safe("R1 AVIF 硬件后端不得吃 libaom 私有项", () =>
            {
                var a = Avif(AvifBase("av1_nvenc"));
                Check("R1a nvenc 有真实质量轴 -cq", a.Contains("-cq"), "args=" + string.Join(" ", a));
                Check("R1b nvenc 不再吃 -crf（实测 10 与 60 同哈希）", !a.Contains("-crf"),
                      "found -crf=" + After(a, "-crf"));
                Check("R1c nvenc 不吃 libaom 私有项",
                      No(a, "-cpu-used", "-aq-mode", "-enable-cdef", "-enable-intrabc",
                         "-row-mt", "-still-picture", "-aom-params", "-usage"),
                      "args=" + string.Join(" ", a));
                var q = Avif(AvifBase("av1_qsv"));
                Check("R1d qsv 不吃 -crf", !q.Contains("-crf"), "args=" + string.Join(" ", q));
                var amf = Avif(AvifBase("av1_amf"));
                // 实测：AMF 也有私有 -usage ⇒ 收到 libaom 的 "allintra" 直接 rc=127（默认配置必失败）
                Check("R1e amf 不发 -usage allintra", No(amf, "-usage"),
                      "args=" + string.Join(" ", amf));
                Check("R1f amf 不发 -aom-params", !amf.Contains("-aom-params"),
                      "args=" + string.Join(" ", amf));
                var l = Avif(AvifBase("libaom-av1"));
                Check("R1g libaom 仍走 -crf（未被误伤）", l.Contains("-crf"),
                      "crf=" + After(l, "-crf"));
                Check("R1h libaom 不吃 -cq", No(l, "-cq"), "found -cq");
            });

            Safe("R2 SVT tune 对表（旧实现整体错位一格）", () =>
            {
                string TuneOf(string display)
                {
                    var o = AvifBase("libsvt-av1");
                    o.AvifSvtTune = display;
                    var a = Avif(o);
                    var d = a.IndexOf("-svtav1-params");
                    return d >= 0 && d + 1 < a.Count ? a[d + 1] : "<none>";
                }
                Check("R2a PSNR⇒tune=1", TuneOf("PSNR").Contains("tune=1"), TuneOf("PSNR"));
                Check("R2b SSIM⇒tune=2", TuneOf("SSIM").Contains("tune=2"), TuneOf("SSIM"));
                Check("R2c VMAF⇒tune=5", TuneOf("VMAF").Contains("tune=5"), TuneOf("VMAF"));
                Check("R2d 默认档不发 tune", !TuneOf("默认").Contains("tune="), TuneOf("默认"));
                var multi = Avif(new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "avif", Encoder = "libsvt-av1", Quality = 50,
                    AvifSvtTune = "VMAF", AvifTune = "PSNR", AvifSvtPreset = 4, Lossless = true,
                });
                Check("R2e -svtav1-params 只出现一次（实测重复出现是整体替换）",
                      N(multi, "-svtav1-params") == 1, "count=" + N(multi, "-svtav1-params"));
                Check("R2f SVT 无损走 lossless=1（旧 crf0 实测 45.89dB 是假的）",
                      string.Join(" ", multi).Contains("lossless=1"),
                      "args=" + string.Join(" ", multi));
            });

            Safe("R3 tune 显示串语言无关（A-7）", () =>
            {
                string ForLibaom(string display)
                {
                    var o = AvifBase("libaom-av1");
                    o.AvifTune = display;
                    return string.Join(" ", Avif(o));
                }
                var zh = ForLibaom("IQ (图像优化)");
                var en = ForLibaom("IQ (Image Optimized)");
                Check("R3a zh 与 en 的 IQ 产出同一条参数", zh == en, $"zh=[{zh}] en=[{en}]");
                Check("R3b IQ 确实 tune=iq", en.Contains("tune=iq"), en);
                Check("R3c 默认档两种语言都不发 tune",
                      !ForLibaom("默认").Contains("tune=") && !ForLibaom("Default").Contains("tune="),
                      ForLibaom("Default"));
                // 采集侧给 **SVT** 那把下拉硬塞的 fallback 是带尾巴的 "VMAF (主观)"（MainWindow 采集侧），
                // 所以前缀折叠要在 SVT 分支上验；而 libaom 分支的旧写法发的是
                // vmaf_without_preprocessing —— 实测该值不存在（rc=127）⇒ 正确行为是**不再发**它。
                var tail = Avif(new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "avif", Encoder = "libsvt-av1", Quality = 50, AvifSvtTune = "VMAF (主观)",
                });
                Check("R3d SVT 认得带尾巴的显示串（前缀折叠⇒tune=5）",
                      string.Join(" ", tail).Contains("tune=5"), "args=" + string.Join(" ", tail));
                Check("R3e libaom 不再发不存在的 vmaf_without_preprocessing",
                      !ForLibaom("VMAF").Contains("vmaf_without_preprocessing"), ForLibaom("VMAF"));
            });

            Safe("R4 NVENC 空间 AQ 与 aq-strength 取值域（A-4/A-5）", () =>
            {
                var on = Avif(AvifBase("av1_nvenc"));
                Check("R4a 勾选必须显式发 -spatial-aq 1（省略==0 是惰性的）",
                      After(on, "-spatial-aq") == "1", "-spatial-aq=" + After(on, "-spatial-aq"));
                var off = Avif(new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "avif", Encoder = "av1_nvenc", Quality = 50,
                    AvifNvencSpatialAq = false, AvifNvencAqStrength = 8, AvifHwPresetLevel = 4,
                });
                Check("R4b 取消勾选发 0", After(off, "-spatial-aq") == "0",
                      "-spatial-aq=" + After(off, "-spatial-aq"));
                Check("R4c 未开空间 AQ 时不发 aq-strength（实测三态同哈希）",
                      !off.Contains("-aq-strength"), "args=" + string.Join(" ", off));
                var zero = Avif(new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "avif", Encoder = "av1_nvenc", Quality = 50,
                    AvifNvencSpatialAq = true, AvifNvencAqStrength = 0, AvifHwPresetLevel = 4,
                });
                Check("R4d aq-strength=0 不发（0 越界 rc=127，语义应是「不设」）",
                      !zero.Contains("-aq-strength"), "args=" + string.Join(" ", zero));
                var over = Avif(new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "avif", Encoder = "av1_nvenc", Quality = 50,
                    AvifNvencSpatialAq = true, AvifNvencAqStrength = 99, AvifHwPresetLevel = 4,
                });
                Check("R4e 越上界被钳到 15", After(over, "-aq-strength") == "15",
                      "aq-strength=" + After(over, "-aq-strength"));
            });

            Safe("R5 静帧任务仍要能走到 -still-picture（附带发现）", () =>
            {
                // 旧判据含 AnimationLoop != 0，而采集侧给静帧恒填 -1 ⇒ 恒真 ⇒ 该参数永不可达
                var a = Avif(new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "avif", Encoder = "libaom-av1", Quality = 50,
                    AvifStillPicture = true, AnimationLoop = -1, AnimationFps = null,
                });
                Check("R5 静帧发 -still-picture 1", After(a, "-still-picture") == "1",
                      "args=" + string.Join(" ", a));
            });

            Safe("R6 GIF 关闭抖动必须显式（A-13①）", () =>
            {
                var off = GifChain(new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "gif", GifPaletteOptimize = true, GifDither = false, AnimationFps = 10,
                });
                Check("R6a 关抖动⇒paletteuse=dither=none（省略==sierra2_4a 不是关）",
                      off != null && off.Contains("paletteuse=dither=none"), $"chain=[{off}]");
                var on = GifChain(new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "gif", GifPaletteOptimize = true, GifDither = true, AnimationFps = 10,
                });
                Check("R6b 开抖动仍是 bayer", on != null && on.Contains("dither=bayer"), $"chain=[{on}]");
            });

            Safe("R7 PNG 无损不再绑死压缩级别（A-14）", () =>
            {
                string ArgsFor(int quality) => FfmpegGui.Services.FfmpegCommandBuilder.BuildArguments(
                    new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", Quality = quality, Lossless = true, UseAdvancedColorParameters = false,
                    },
                    System.IO.Path.Combine(SrcDir, "src_8bit.png"), "out.png");
                var mid = ArgsFor(50);
                Check("R7a 无损 + 质量 50 ⇒ 级别不是被钉死的 0（旧写法 11.3× 体积）",
                      mid.Contains("-compression_level") && !mid.Contains("-compression_level 0"),
                      "args=" + mid);
                // 「强制无损」这条既定语义的证据不在命令行里（PNG 编码本身无损，没有 -lossy 旗标可查），
                // 它由 J9a / J14d-1 / L2a-L2d 钉住 ⇒ 这里只验新放行的轴守住了界、也没混进有损参数。
                // ⚠ 这道界**不是** ffmpeg 给的：`-h full` 里 -compression_level 声明是
                //   (from INT_MIN to INT_MAX)（通用 AVCodecContext 项，无界），zlib 的 0..9 是实现域
                //   ⇒ 越界只能由我们自己在放行点守，正因无界才必须有这条断言。
                int lvl = int.MinValue;
                var m = System.Text.RegularExpressions.Regex.Match(mid, @"-compression_level (\S+)");
                if (m.Success) int.TryParse(m.Groups[1].Value, out lvl);
                Check("R7b 放行的 PNG 级别守在本软件的 0..9 域内（ffmpeg 声明无界，界由我们守）",
                      lvl >= 0 && lvl <= 9, $"level={lvl} args=" + mid);
                Check("R7c PNG 命令行里不出现有损质量旗标", !mid.Contains("-q:v"),
                      "args=" + mid);
            });

            Safe("R8 WebP 有损放行压缩级别 / APNG 补发 pred 与 dpi（A-15/A-16）", () =>
            {
                var lossy = FfmpegGui.Services.FfmpegCommandBuilder.BuildArguments(
                    new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "webp", Quality = 75, Lossless = false,
                        WebpPreset = "none", WebpCompressionLevel = 6,
                    },
                    System.IO.Path.Combine(SrcDir, "src_8bit.png"), "out.webp");
                Check("R8a 有损 WebP 也发 -compression_level（旧注释判成无损专有）",
                      lossy.Contains("-compression_level 6"), "args=" + lossy);
                var apng = FfmpegGui.Services.FfmpegCommandBuilder.BuildArguments(
                    new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "apng", Quality = 50, Lossless = true,
                        PngPred = "sub", PngDpi = 300,
                    },
                    System.IO.Path.Combine(SrcDir, "src_8bit.png"), "out.png");
                Check("R8b APNG 补发 -pred（实测每帧生效）", apng.Contains("-pred sub"), "args=" + apng);
                Check("R8c APNG 补发 -dpi", apng.Contains("-dpi 300"), "args=" + apng);
            });

            // 负控：整组不能是恒真 —— 若分域判据全部失效（例如实现退化成"谁都发一套"），
            // 上面 R1/R2/R4 的"不得包含"断言必须转红。这里量一次"确实存在被排除的项"作为对照。
            Safe("R9 负控：R1 的排除集非空", () =>
            {
                var a = Avif(AvifBase("av1_nvenc"));
                var excluded = new[] { "-cpu-used", "-aq-mode", "-enable-cdef", "-row-mt", "-still-picture" }
                    .Count(a.Contains);
                Check("R9a nvenc 分支确实被排除了这 5 项", excluded == 0,
                      $"仍出现 {excluded} 项");
                Check("R9b 同一套输入在 libaom 分支上会出现其中至少 3 项（证明排除是**按后端**的）",
                      new[] { "-cpu-used", "-aq-mode", "-enable-cdef", "-row-mt", "-still-picture" }
                          .Count(Avif(AvifBase("libaom-av1")).Contains) >= 3, "libaom 命中不足");
            });
        }

        // ══════════════ 入口（供 Program.Main 逐 mode 分派）══════════════
        // 只做转发 + 一个**共享收敛前置**：组体本身是逐字搬来的私有方法，保持原貌不动。
        internal static void RunH()
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

            // ⚠ 额外前置（见生成器 $colorPre 长注释）：把输出格式预置为 PNG，
            //   复现旧宿主里 GroupG 在 H 之前做过的 SetFormat("PNG")。
            //   不做这一步时 H10 会因 SetFormat("PNG") **首次真正生效**而重建 ColorSpaceCombo
            //   （:2224 Clear + :2245 SelectedIndex=0），把刚选的 "Display P3" 抹掉 ⇒ 稳定转红。
            SetFormat("PNG");
            GroupH_Color();
        }
        internal static void RunI()
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
            GroupI_IccUi();
        }
        internal static void RunK()
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
            GroupK_StrategyMap();
        }
        internal static void RunR()
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
            GroupR_EncoderArgDomains();
        }
    }
}
