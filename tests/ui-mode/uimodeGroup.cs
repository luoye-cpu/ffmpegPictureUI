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

namespace Tests.UiMode
{
    /// <summary>
    /// 子系统测试组：**ui-mode** —— 2026-10-05 从 UiTestHost 的单个 3500 行
    /// <c>Program.cs</c> 中物理拆出。组体**逐字**搬来（断言文本一字未改，门禁在 grep 它们）。
    /// <para><b>共享 GUI 底座</b>：窗口 <c>W</c>、<c>TempDir</c>、以及全部 GUI/token 助手
    /// 都在 UiHost（UiHostShared 工程）—— 本类只保留同名**转发包装**，
    /// 不复制实现。</para>
    /// </summary>
    internal static class uimodeGroup
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

        // ══════════════════════════ 组 E ══════════════════════════
        static void GroupE_SimpleMode()
        {
            Console.WriteLine("\n########## E组: 简洁模式 ##########");
            Safe("E1 进入简洁模式→面板切换", () =>
            {
                Click("SimpleModeEntryBtn");
                var simple = Find<Grid>("SimpleModePanel");
                var full = Find<DockPanel>("FullModePanel");
                Check("E1 简洁面板可见", simple?.IsVisible == true, $"visible={simple?.IsVisible}");
                Check("E1b 完整面板隐藏", full?.IsVisible == false, $"visible={full?.IsVisible}");
            });
            Safe("E2 简洁模式预设有项+自动编码开关", () =>
            {
                var preset = Find<ComboBox>("SimplePresetCombo");
                var toggle = Find<ToggleSwitch>("AutoEncodeToggle");
                Check("E2 预设下拉存在", preset != null, "preset null");
                Check("E2b 自动编码开关存在", toggle != null, "toggle null");
            });
            Safe("E3 返回完整模式→面板复原", () =>
            {
                Click("SimpleReturnBtn");
                var simple = Find<Grid>("SimpleModePanel");
                var full = Find<DockPanel>("FullModePanel");
                Check("E3 完整面板恢复", full?.IsVisible == true, $"visible={full?.IsVisible}");
                Check("E3b 简洁面板隐藏", simple?.IsVisible == false, $"visible={simple?.IsVisible}");
            });

            // E4: 简洁模式的预设必须能把**高级模式的整体状态**摆出来（含"转换模式"这条新轴）。
            // 三种格式里 GIF 只存在于 AnimatedFormats、DNG 只存在于 RAW 模式，而三个模式的格式
            // 候选集互斥 ⇒ 预设若不携带模式，切完预设后 FormatCombo 根本选不中，且是**静默**选不中。
            Safe("E4 预设联动高级模式状态（模式轴）", () =>
            {
                var combo = Find<ComboBox>("SimplePresetCombo");
                var mode = Find<ComboBox>("ConversionModeCombo");
                var fmt = Find<ComboBox>("FormatCombo");
                if (combo?.Items == null || mode == null || fmt == null)
                { Check("E4 控件存在", false, "combo/mode/fmt null"); return; }

                int gifIdx = -1, pngIdx = -1;
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    if (combo.Items[i] is not FfmpegGui.Models.PresetEntry pe) continue;
                    if (gifIdx < 0 && string.Equals(pe.Data.Format, "GIF", StringComparison.OrdinalIgnoreCase)) gifIdx = i;
                    if (pngIdx < 0 && string.Equals(pe.Data.Format, "PNG", StringComparison.OrdinalIgnoreCase)) pngIdx = i;
                }
                Check("E4a 预设表含 GIF 与 PNG 条目", gifIdx >= 0 && pngIdx >= 0, $"gif={gifIdx} png={pngIdx}");

                // 应用预设会改掉整套高级模式状态（这正是"完整快照"要做到的），所以先用同一套
                // 机制拍一张快照，测完还原 —— 否则后面的组会读到被本组改过的控件默认值。
                var buildSnap = typeof(FfmpegGui.MainWindow).GetMethod("BuildPresetData", BF);
                var applySnap = typeof(FfmpegGui.MainWindow).GetMethod("ApplyPresetData", BF);
                if (buildSnap == null || applySnap == null)
                { Check("E4 快照反射入口可用", false, "BuildPresetData/ApplyPresetData missing"); return; }
                var snapshot = buildSnap.Invoke(W, null);

                Click("SimpleModeEntryBtn");   // 预设切换仅在简洁模式激活时生效
                combo.SelectedIndex = gifIdx; Pump(12);
                Check("E4b GIF 预设把模式推到动图", mode.SelectedIndex == 1, $"mode={mode.SelectedIndex}");
                Check("E4c GIF 预设确实选中 GIF 格式", (fmt.SelectedItem as string) == "GIF", $"fmt={fmt.SelectedItem}");

                combo.SelectedIndex = pngIdx; Pump(12);
                Check("E4d PNG 预设把模式拉回静态", mode.SelectedIndex == 0, $"mode={mode.SelectedIndex}");
                Click("SimpleReturnBtn");

                applySnap.Invoke(W, new object?[] { snapshot });
                Pump(12);
                Check("E4e 快照还原后模式与格式回到应用前",
                      mode.SelectedIndex == 0 && (fmt.SelectedItem as string) != "GIF",
                      $"mode={mode.SelectedIndex} fmt={fmt.SelectedItem}");
            });

            // E5: 结构锁 —— 简洁模式是**覆盖层**，不得再有第二套选项真值。
            // 行为断言在重构后是恒真的（两条路径已是同一条），所以这里锁"别长回第二套"。
            Safe("E5 简洁模式与高级模式同源（结构锁）", () =>
            {
                var p = System.IO.Path.Combine(RepoRoot, "src", "FfmpegGui", "MainWindow.xaml.cs");
                var src = System.IO.File.ReadAllText(p);
                int Count(string pat) => System.Text.RegularExpressions.Regex.Matches(src, pat).Count;

                Check("E5a 第二套预设→选项映射已移除",
                      src.IndexOf("CreateQueueItemFromSimplePreset", StringComparison.Ordinal) < 0,
                      "CreateQueueItemFromSimplePreset still present");
                var collectSites = Count(@"UseAdvancedColorParameters = useAdv");
                Check("E5b 控件→FfmpegOptions 采集点唯一", collectSites == 1, $"sites={collectSites}");
                var collectRefs = Count(@"CollectOptionsFromUiAsync\(");
                Check("E5c 采集点被入队与两条预览共用", collectRefs >= 4, $"refs={collectRefs}");

                // 简洁模式的两个入队 handler 必须走共享路径，且不得自己往队列里塞 item
                string Slice(string from, string to)
                {
                    int a = src.IndexOf(from, StringComparison.Ordinal);
                    int b = src.IndexOf(to, a + from.Length, StringComparison.Ordinal);
                    if (a < 0 || b < 0) return "";
                    return src.Substring(a, b - a);
                }
                var addFiles = Slice("SimpleAddFiles_Click", "SimpleClearMedia_Click");
                var drop = Slice("private async void SimpleDrop", "SimpleClearAll_Click");
                Check("E5d 文件选择入队走 AddSingleToQueue",
                      addFiles.Length > 0 && addFiles.Contains("await AddSingleToQueue(")
                      && !addFiles.Contains("_queueProcessor.Add("), $"len={addFiles.Length}");
                Check("E5e 拖放入队走 AddSingleToQueue",
                      drop.Length > 0 && drop.Contains("await AddSingleToQueue(")
                      && !drop.Contains("_queueProcessor.Add("), $"len={drop.Length}");
            });

            // E6: 旧预设 JSON 里没有的轴，应用时**不得**被 schema 默认值写进控件。
            // 具体盯 AnimationLoop：循环框出厂是**空**（=auto，采集侧解析成 0），而 PresetData 的
            // 旧默认值是 -1（无限循环）⇒ 无条件回放的差异是「0 → -1」，产物会从播一次变成不停播。
            Safe("E6 旧预设未表达的轴不得改控件", () =>
            {
                var applyE6 = typeof(FfmpegGui.MainWindow).GetMethod("ApplyPresetData", BF);
                var buildE6 = typeof(FfmpegGui.MainWindow).GetMethod("BuildPresetData", BF);
                var box = Find<TextBox>("AnimationLoopBox");
                var fpsBox = Find<TextBox>("AnimationFpsBox");
                if (applyE6 == null || buildE6 == null || box == null)
                { Check("E6 反射/控件可用", false, "apply/build/AnimationLoopBox null"); return; }

                var snap = buildE6.Invoke(W, null);
                try
                {
                    box.Text = "";
                    if (fpsBox != null) fpsBox.Text = "";
                    Pump(4);
                    var legacy = FfmpegGui.Models.PresetData.FromJson("{\"format\":\"GIF\",\"quality\":85}");
                    Check("E6a 旧预设的 AnimationLoop 解析为空", !legacy.AnimationLoop.HasValue,
                          "hasValue=" + legacy.AnimationLoop.HasValue);
                    applyE6.Invoke(W, new object?[] { legacy });
                    Pump(10);
                    Check("E6b 旧预设不得把循环框写成 -1", box.Text != "-1", "text=[" + box.Text + "]");
                    Check("E6c 旧预设不得把 fps 框写成数值", string.IsNullOrWhiteSpace(fpsBox?.Text),
                          "text=[" + fpsBox?.Text + "]");
                }
                finally
                {
                    applyE6.Invoke(W, new object?[] { snap });
                    Pump(10);
                }
            });

            // E7: 覆盖层的宗旨 —— 它没有自己的内容，显示与操作的都是高级模式那一份。
            Safe("E7 覆盖层与高级模式共用同一份已选集合", () =>
            {
                var mfField = typeof(FfmpegGui.MainWindow).GetField("_mediaFiles", BF);
                var advList = Find<ListBox>("MediaFileList");
                var simList = Find<ListBox>("SimpleMediaList");
                if (mfField?.GetValue(W) is not System.Collections.IList shared || advList == null || simList == null)
                { Check("E7 共享集合与两个列表可用", false, "mediaFiles/列表 取不到"); return; }

                Click("SimpleModeEntryBtn"); Pump(6);
                Check("E7a 两个列表绑的是同一个集合实例（无第二份内容源）",
                      ReferenceEquals(simList.ItemsSource, advList.ItemsSource)
                      && ReferenceEquals(simList.ItemsSource, shared),
                      $"simple={simList.ItemsSource?.GetType().Name} adv={advList.ItemsSource?.GetType().Name}");

                var src = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot, "src", "FfmpegGui", "MainWindow.xaml.cs"));
                Check("E7b 第二份已选集合已从产品码移除", !src.Contains("_simpleMediaFiles"),
                      "仍存在 _simpleMediaFiles");
                int a = src.IndexOf("private void SimpleReturn_Click", System.StringComparison.Ordinal);
                int b = src.IndexOf("private void SimpleStartQueue_Click", a, System.StringComparison.Ordinal);
                var retBody = a >= 0 && b > a ? src.Substring(a, b - a) : "";
                Check("E7c 返回完整模式不再做「合并/去重添加」（无 _mediaFiles.Add）",
                      retBody.Length > 0 && !retBody.Contains("_mediaFiles.Add("),
                      "len=" + retBody.Length);

                // 行为面：往高级模式那份集合里加一项，覆盖层重新进入后必须显示同一项与同一计数；
                // 再点覆盖层的「清空」，共享集合必须真的空掉 —— 证明覆盖层的清空动的是同一份内容。
                var snapshot = new List<string>(shared.Cast<object>().Select(x => x.ToString() ?? ""));
                var probe = System.IO.Path.Combine(SrcDir, "zz-overlay-probe.png");
                int before = shared.Count;
                if (!shared.Contains(probe)) shared.Add(probe);
                Click("SimpleReturnBtn"); Pump(4);
                Click("SimpleModeEntryBtn"); Pump(8);
                var cnt = Find<TextBlock>("SimpleFileCount")?.Text ?? "";
                Check("E7d 覆盖层计数标签反映共享集合", cnt.StartsWith(shared.Count.ToString()),
                      $"label=[{cnt}] shared={shared.Count} before={before}");
                typeof(FfmpegGui.MainWindow)
                    .GetMethod("SimpleClearMedia_Click", BF)
                    ?.Invoke(W, new object?[] { null, null });
                Pump(6);
                Check("E7e 覆盖层的清空动的是同一份集合", shared.Count == 0,
                      $"shared={shared.Count}");
                // 还原到本条断言之前的内容，避免污染后面的组（IList 没有 Clear，逐行删）
                for (int i = shared.Count - 1; i >= 0; i--) shared.RemoveAt(i);
                foreach (var s in snapshot) shared.Add(s);
                typeof(FfmpegGui.MainWindow).GetMethod("UpdateMediaFileCount", BF)?.Invoke(W, null);
                Click("SimpleReturnBtn"); Pump(4);
            });

            // E8: 回显的忠实性 —— 覆盖层显示的是"高级模式当前的生效设置"，不是"最后点了哪条预设"；
            //      两者一旦不符必须点名（锁的是"覆盖层骗人"这类最难被发现的显示缺陷）。
            Safe("E8 覆盖层回显 = 高级模式当前生效设置", () =>
            {
                var loc = FfmpegGui.Services.LocalizationService.Instance;
                var fmtCombo = Find<ComboBox>("FormatCombo");
                var modeCombo = Find<ComboBox>("ConversionModeCombo");
                var label = Find<TextBlock>("SimpleCurrentStateLabel");
                var combo = Find<ComboBox>("SimplePresetCombo");
                var q = Find<Slider>("QualitySlider");
                if (fmtCombo == null || modeCombo == null || label == null || combo == null || q == null)
                { Check("E8 所需控件可用", false, "有控件为 null"); return; }

                var mwT = typeof(FfmpegGui.MainWindow);
                // 前置条件自己清：E4/E6 已经应用过预设，残留的指纹会让 E8d 因"别组改过状态"而假红
                mwT.GetField("_simpleAppliedSig", BF)?.SetValue(W, "");
                var snap8 = mwT.GetMethod("BuildPresetData", BF)?.Invoke(W, null);
                Click("SimpleModeEntryBtn"); Pump(6);

                var fmtDisp = fmtCombo.SelectedItem as string ?? "";
                var modeDisp = modeCombo.SelectedItem as string ?? "";
                Check("E8a 回显含当前格式的显示串", fmtDisp.Length > 0 && (label.Text ?? "").Contains(fmtDisp),
                      $"label=[{label.Text}] fmt={fmtDisp}");
                Check("E8b 回显含当前转换模式的显示串", modeDisp.Length > 0 && (label.Text ?? "").Contains(modeDisp),
                      $"label=[{label.Text}] mode={modeDisp}");
                Check("E8c 回显以本地化标题开头（不是硬编码中文）",
                      (label.Text ?? "").StartsWith(loc["simple.current.state"]), label.Text ?? "");
                Check("E8d 没应用过预设时不喊不一致（防假警报）",
                      !(label.Text ?? "").Contains(loc["simple.preset.custom"]), label.Text ?? "");

                combo.SelectedIndex = combo.SelectedIndex == 0 ? 1 : 0; Pump(12);
                Check("E8e 刚应用完预设 ⇒ 与预设一致，不喊不一致",
                      !(label.Text ?? "").Contains(loc["simple.preset.custom"]), label.Text ?? "");

                q.Value = Math.Clamp(q.Value - 20, q.Minimum, q.Maximum); Pump(8);
                mwT.GetMethod("UpdateSimpleCounts", BF)?.Invoke(W, null);
                Check("E8f 改掉质量后回显点名不一致",
                      (label.Text ?? "").Contains(loc["simple.preset.custom"]), label.Text ?? "");

                if (snap8 != null)
                {
                    mwT.GetMethod("ApplyPresetData", BF)?.Invoke(W, new object?[] { snap8 });
                    Pump(10);
                }
                Click("SimpleReturnBtn"); Pump(4);
            });
        }

        // ══════════════════════════ 组 F ══════════════════════════
        static void GroupF_BackgroundVisibility()
        {
            Console.WriteLine("\n########## F组: 后台/隐藏可见性 ##########");
            Safe("F1 窗口Hide→IsVisible=false(后台化核心)", () =>
            {
                W.Hide(); Pump();
                Check("F1 隐藏后不可见", !W.IsVisible, $"IsVisible={W.IsVisible}");
                W.Show(); Pump();
                Check("F1b 恢复显示", W.IsVisible, $"IsVisible={W.IsVisible}");
            });
        }

        // ══════════════════════════ 组 G ══════════════════════════
        static void GroupG_EndToEnd()
        {
            Console.WriteLine("\n########## G组: GUI驱动端到端真实转换 ##########");
            Safe("G1 PNG端到端(添加队列→开始→产物)", () =>
            {
                var plan = System.IO.Path.Combine(RepoRoot, "publish", "PLAN");
                // ⚠⚠ 2026-09-22（`HANDOVER §33` 项 1）：**不得再用共享固定目录 `tests/output/gui-test`**。
                //   它被 5 条 UI 门禁（ui-host / ui-param-matrix / ui-strategy-map / ui-param-defects / cli-strict）
                //   **共用** —— 而 `st.OutputDirectory` 是**静态设置、跨组持续生效** ⇒ 预览/编码命令会把同名产物
                //   写进同一个目录 ⇒ 两个实例/两条门禁互相覆盖 ⇒ **偶发假红**（实测整轮两次红、红的还不是同一条：
                //   Run A 的 `H10` 子 ffmpeg 命令因写盘争抢 `exit 1`）。已登记同类成因：`COLOR_TRAPS:45`。
                //   ⇒ 改用**本实例的运行级目录** `TempDir`（`%TEMP%/uitesthost_<guid>`）——
                //     与 `:47-49` 既有约定**完全一致**（「中间产物一律写这里，绝不写进共享目录」），且**退出时自清**。
                var outDir = TempDir;
                if (!System.IO.Directory.Exists(outDir)) System.IO.Directory.CreateDirectory(outDir);
                var st = FfmpegGui.Services.AppSettingsService.Current;
                st.FfmpegDirectory = System.IO.Path.Combine(plan, "ffmpeg-full");
                st.OutputDirectory = outDir;
                FfmpegGui.Services.ExternalToolsDetector.EnsureAllDetected();

                var input = System.IO.Path.Combine(SrcDir, "src_8bit.png");
                var outFile = System.IO.Path.Combine(outDir, "src_8bit.png");
                try { if (System.IO.File.Exists(outFile)) System.IO.File.Delete(outFile); } catch { }

                SetInput(input);
                SetFormat("PNG"); // ffmpeg 原生编码器, 最稳, 不依赖外部工具
                var mf = typeof(FfmpegGui.MainWindow).GetField("_mediaFiles", BF)?.GetValue(W) as System.Collections.IList;
                if (mf != null && !mf.Contains(input)) mf.Add(input);

                // 「添加到队列」「开始队列」按钮无 x:Name → 反射调用其 private 事件处理器
                typeof(FfmpegGui.MainWindow).GetMethod("AddToQueue_Click", BF)?.Invoke(W, new object?[] { null, null });
                Pump(30);
                typeof(FfmpegGui.MainWindow).GetMethod("StartQueue_Click", BF)?.Invoke(W, new object?[] { null, null });

                // 轮询等待后台转换产物 (QueueProcessor 在后台 Task 跑 ffmpeg, 超时 45s)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 45000)
                {
                    Pump(4);
                    if (System.IO.File.Exists(outFile) && new System.IO.FileInfo(outFile).Length > 0) break;
                    System.Threading.Thread.Sleep(120);
                }
                bool produced = System.IO.File.Exists(outFile) && new System.IO.FileInfo(outFile).Length > 0;
                long sz = produced ? new System.IO.FileInfo(outFile).Length : 0;
                Check("G1 PNG端到端产物", produced,
                      $"exists={System.IO.File.Exists(outFile)} size={sz}B elapsed={sw.ElapsedMilliseconds}ms");
            });
        }

        // ══════════════════════════ 组 O ══════════════════════════
        static void GroupO_GainMapToggleKeepsTargets()
        {
            Safe("O1/O2/O3 GainMap 开关保持目标三项", () =>
            {
                var gm = Find<ComboBox>("HdrModeCombo");
                if (gm == null) { Check("O1 HdrModeCombo 存在", false, "null"); return; }

                SweepReset();
                gm.SelectedIndex = 0; Pump(8);      // ⚠ SweepReset 不含本开关 ⇒ 本组自己归零，避免跨组串状态
                SetFormat("JPEG"); Pump(10);

                var csCombo = Find<ComboBox>("ColorSpaceCombo");
                var bdCombo = Find<ComboBox>("BitDepthCombo");
                var qSlider = Find<Slider>("QualitySlider");

                // ① 记下**刷新后的默认值**（缺陷若复现，三项会被冲成这些值）
                var defCs = csCombo?.SelectedItem as string;
                var defBd = bdCombo?.SelectedItem as string;
                var defQ = qSlider?.Value ?? double.NaN;

                // ② 选一组**与默认值不同**的取值 —— 这本身就是反控：若取值 == 默认值，O2/O3 就是恒真的
                var pickCs = "Display P3";
                var pickBd = "auto";
                if (bdCombo != null && bdCombo.Items!.Count > 1)
                    pickBd = bdCombo.Items[bdCombo.Items.Count - 1] as string ?? "auto";
                var pickQ = (int)defQ > 50 ? (int)defQ - 10 : (int)defQ + 10;

                Check("O1 待测值与默认值不同（反控：否则 O2/O3 恒真）",
                      pickCs != defCs && pickBd != defBd && (int)pickQ != (int)defQ,
                      $"cs={defCs}->{pickCs} bd={defBd}->{pickBd} q={defQ}->{pickQ}");

                if (csCombo != null) csCombo.SelectedItem = pickCs;
                if (bdCombo != null) bdCombo.SelectedItem = pickBd;
                if (qSlider != null) qSlider.Value = pickQ;
                Pump(10);

                // ③ 真实 UI 路径：勾上 GainMap（XAML `IsCheckedChanged` ⇒ 处理器）
                gm.SelectedIndex = 1; Pump(14);
                var onCs = csCombo?.SelectedItem as string;
                var onBd = bdCombo?.SelectedItem as string;
                var onQ = qSlider?.Value ?? double.NaN;
                Check("O2a 开 GainMap 后目标色域保持", onCs == pickCs, $"期望={pickCs} 实={onCs}（默认={defCs}）");
                Check("O2b 开 GainMap 后位深保持", onBd == pickBd, $"期望={pickBd} 实={onBd}（默认={defBd}）");
                Check("O2c 开 GainMap 后质量保持", Math.Abs(onQ - pickQ) < 0.5, $"期望={pickQ} 实={onQ}（默认={defQ}）");

                // ④ 再取消 —— 双向都必须保持
                gm.SelectedIndex = 0; Pump(14);
                var offCs = csCombo?.SelectedItem as string;
                var offBd = bdCombo?.SelectedItem as string;
                var offQ = qSlider?.Value ?? double.NaN;
                Check("O3a 关 GainMap 后目标色域保持", offCs == pickCs, $"期望={pickCs} 实={offCs}（默认={defCs}）");
                Check("O3b 关 GainMap 后位深保持", offBd == pickBd, $"期望={pickBd} 实={offBd}（默认={defBd}）");
                Check("O3c 关 GainMap 后质量保持", Math.Abs(offQ - pickQ) < 0.5, $"期望={pickQ} 实={offQ}（默认={defQ}）");
            });
        }

        // ══════════════ 入口（供 Program.Main 逐 mode 分派）══════════════
        // 只做转发 + 一个**共享收敛前置**：组体本身是逐字搬来的私有方法，保持原貌不动。
        internal static void RunE()
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
            GroupE_SimpleMode();
        }
        internal static void RunF()
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
            GroupF_BackgroundVisibility();
        }
        internal static void RunG()
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
            GroupG_EndToEnd();
        }
        internal static void RunO()
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
            GroupO_GainMapToggleKeepsTargets();
        }
    }
}
