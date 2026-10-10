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

namespace Tests.UiCore
{
    /// <summary>
    /// 子系统测试组：**ui-core** —— 2026-10-05 从 UiTestHost 的单个 3500 行
    /// <c>Program.cs</c> 中物理拆出。组体**逐字**搬来（断言文本一字未改，门禁在 grep 它们）。
    /// <para><b>共享 GUI 底座</b>：窗口 <c>W</c>、<c>TempDir</c>、以及全部 GUI/token 助手
    /// 都在 UiHost（UiHostShared 工程）—— 本类只保留同名**转发包装**，
    /// 不复制实现。</para>
    /// </summary>
    internal static class uicoreGroup
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

        // ══════════════════════════ 组 A ══════════════════════════
        static void GroupA_FormatToCommand()
        {
            Console.WriteLine("\n########## A组: 输出格式→生成命令 (UI选项生效) ##########");

            Safe("A1 JPEG→mjpeg命令", () =>
            {
                SetFormat("JPEG"); Regen(); var c = Cmd();
                Check("A1 JPEG", c.Contains(".jpg") && (c.Contains("mjpeg") || c.Contains("ffmpeg")), Snip(c));
            });
            Safe("A2 PNG→png命令", () =>
            {
                SetFormat("PNG"); Regen(); var c = Cmd();
                Check("A2 PNG", c.Contains(".png") && c.Contains("png"), Snip(c));
            });
            Safe("A3 WebP→webp命令", () =>
            {
                SetFormat("WebP"); Regen(); var c = Cmd();
                Check("A3 WebP", c.Contains(".webp") && c.Contains("webp"), Snip(c));
            });
            // ★ 缺陷1 GUI侧关键验证: AVIF 必须走 ffmpeg/av1 路由, 绝不能是 cjxl
            Safe("A4 AVIF→ffmpeg路由(非cjxl)", () =>
            {
                SetFormat("AVIF"); Regen(); var c = Cmd();
                // UI 路由本质: ffmpeg + .avif 输出 + crf/av1 质量模型 (编码器具体名由运行时 ffmpeg -encoders 异步探测填充)
                bool routed = c.Contains(".avif") && c.Contains("ffmpeg")
                              && (c.Contains("-crf") || c.Contains("av1") || c.Contains("libaom") || c.Contains("svt"));
                bool notJxl = !c.Contains("cjxl", StringComparison.OrdinalIgnoreCase);
                Check("A4 AVIF路由(ffmpeg+.avif+crf/av1)", routed, Snip(c));
                Check("A4b AVIF非cjxl(缺陷1核心)", notJxl, Snip(c));
                Console.WriteLine(c.Contains("未检测到")
                    ? "      info: 编码器名=占位符 (headless未配ffmpeg跑-encoders探测; 真实GUI启动异步填充av1, 属正常降级)"
                    : "      info: 编码器名已检测");
            });
            Safe("A5 JPEG XL→jxl命令", () =>
            {
                SetFormat("JPEG XL"); Regen(); var c = Cmd();
                Check("A5 JXL", c.Contains(".jxl") && (c.Contains("jxl") || c.Contains("cjxl") || c.Contains("libjxl")), Snip(c));
            });
            Safe("A6 TIFF→tiff命令", () =>
            {
                SetFormat("TIFF"); Regen(); var c = Cmd();
                Check("A6 TIFF", c.Contains(".tif") && c.Contains("tiff"), Snip(c));
            });
        }

        // ══════════════════════════ 组 B ══════════════════════════
        static void GroupB_ParameterPassing()
        {
            Console.WriteLine("\n########## B组: 参数传递 (质量/位深/色度) ##########");

            Safe("B1 质量值改变→命令改变", () =>
            {
                SetFormat("JPEG");
                var q = Find<Slider>("QualitySlider");
                if (q != null) q.Value = 92; Regen(); var c92 = Cmd();
                if (q != null) q.Value = 40; Regen(); var c40 = Cmd();
                Check("B1 质量传递", !string.IsNullOrEmpty(c92) && c92 != c40,
                      $"q92={Snip(c92)} | q40={Snip(c40)}");
            });
            // ⚠ B2 修正（原断言与环境耦合，恒红）：UI 有**两个**色度控件 ——
            //   通用 ChromaCombo（取值形如 "4:2:0"）与 cjpegli 专用 JpegliChromaCombo（取值形如 "420"）。
            //   cjpegli 路由**只读后者**（MainWindow.xaml.cs:1417），且**仅在「高级编码」勾选时才读**
            //   （`useJpegliAdv` 为假时硬编码 "auto"）。原断言拿整条命令串查 "420" ⇒ 与事实不符。
            //   本断言改为「双控件分工」的正向 + 负控：专用控件必须进命令，通用控件必须**不**进命令。
            Safe("B2 cjpegli色度:专用控件进命令+通用控件不进命令", () =>
            {
                SetFormat("JPEG"); Pump(8);

                // 1) 显式选中 cjpegli 编码器（不依赖「默认项恰好是 cjpegli」）
                var enc = Find<ComboBox>("EncoderCombo");
                string? picked = null;
                if (enc != null)
                    for (int i = 0; i < enc.ItemCount; i++)
                    {
                        var s = enc.Items[i] as string;
                        if (s != null && s.Contains("cjpegli", StringComparison.OrdinalIgnoreCase)) { picked = s; break; }
                    }
                if (picked != null && enc != null) { enc.SelectedItem = picked; Pump(8); }

                // 2) 勾选「高级编码」——否则 JpegliChromaCombo 被忽略，命令恒用 auto
                var advCodec = Find<CheckBox>("UseAdvancedCodec");
                if (advCodec != null) advCodec.IsChecked = true; Pump(6);

                // 3) 两个色度控件故意设成不同值（值域本身不同：4:2:0 vs 420）
                var generic = Find<ComboBox>("ChromaCombo"); if (generic != null) generic.SelectedItem = "4:2:0";
                var dedicated = Find<ComboBox>("JpegliChromaCombo"); if (dedicated != null) dedicated.SelectedItem = "420";
                Pump(8); Regen();
                var c = Cmd();

                bool isCjpegliRoute = picked != null && c.Contains("cjpegli");
                bool dedicatedIn = c.Contains("--chroma_subsampling 420");
                bool genericOut = !c.Contains("4:2:0") && !c.Contains("yuv420");
                Check("B2a cjpegli路由成立(前置)", isCjpegliRoute, $"enc={picked} :: {Snip(c)}");
                Check("B2b 专用色度控件值进命令", dedicatedIn, Snip(c));
                Check("B2c 通用色度控件值不进命令(负控)", genericOut, Snip(c));

                // 复位，避免状态泄漏到后续组
                if (advCodec != null) advCodec.IsChecked = false;
                if (dedicated != null) dedicated.SelectedItem = "auto";
                if (generic != null) generic.SelectedItem = "auto";
                Pump(6);
            });
            Safe("B3 位深→PNG命令体现", () =>
            {
                SetFormat("PNG");
                var bd = Find<ComboBox>("BitDepthCombo");
                if (bd != null) bd.SelectedItem = "16";
                Regen(); var c16 = Cmd();
                Check("B3 位深传递", !string.IsNullOrEmpty(c16), Snip(c16));
                if (bd != null) bd.SelectedItem = "auto"; Regen();
            });
        }

        // ══════════════════════════ 组 C ══════════════════════════
        static void GroupC_PanelVisibility()
        {
            Console.WriteLine("\n########## C组: 面板显隐联动 (UI交互) ##########");

            Safe("C1 高级色彩→面板显示", () =>
            {
                var chk = Find<CheckBox>("UseAdvancedColor");
                var panel = Find<StackPanel>("AdvancedColorPanel");
                if (chk != null) chk.IsChecked = true; Pump();
                Check("C1 高级色彩面板", panel?.IsVisible == true, $"visible={panel?.IsVisible}");
                if (chk != null) chk.IsChecked = false; Pump();
                Check("C1b 取消→隐藏", panel?.IsVisible == false, $"visible={panel?.IsVisible}");
            });
            Safe("C2 高级编码→面板显示", () =>
            {
                var chk = Find<CheckBox>("UseAdvancedCodec");
                var panel = Find<StackPanel>("AdvancedCodecPanel");
                if (chk != null) chk.IsChecked = true; Pump();
                Check("C2 高级编码面板", panel?.IsVisible == true, $"visible={panel?.IsVisible}");
            });
            Safe("C3 exiftool可用→面板显示+strip随模式", () =>
            {
                // 反射注入 exiftool 检测可用 (真实运行时由 Detect() 设置; headless exe 无 PLAN 同级目录,
                // 故注入以验证 UI 逻辑本身: ExifToolPanel.IsVisible=exifAvailable, strip.IsEnabled=exifAvailable&&preserve)
                var et = typeof(FfmpegGui.Services.ExifToolService);
                var exifReal = System.IO.Path.Combine(RepoRoot, "publish", "PLAN", "exiftool", "exiftool.exe");
                et.GetField("_detectedPath", BFS)?.SetValue(null, System.IO.File.Exists(exifReal) ? exifReal : "exiftool");
                et.GetField("_detected", BFS)?.SetValue(null, true);

                var mode = Find<ComboBox>("MetadataModeCombo");
                var panel = Find<Border>("ExifToolPanel");
                var gps = Find<CheckBox>("StripExifGpsCheck");

                if (mode != null) mode.SelectedIndex = 1; Pump(8); // 删除模式 → 触发 UpdateExifToolPanelState
                Check("C3 exiftool可用→面板显示", panel?.IsVisible == true, $"visible={panel?.IsVisible}");
                Check("C3b 删除模式→strip复选框禁用", gps?.IsEnabled == false, $"enabled={gps?.IsEnabled}");

                if (mode != null) mode.SelectedIndex = 0; Pump(8); // 保留模式
                Check("C3c 保留模式→strip复选框启用", gps?.IsEnabled == true, $"enabled={gps?.IsEnabled}");
            });
            // ⚠⚠ 2026-09-22（管线审查 §3.5 的修复锁）：**exiftool 不可用**时 ——
            //   面板必须**保持可见**、所有相关选项**禁用**、模式**强制回「保留」**、且**给出显式提示**。
            //   为什么必须有这条锁：旧行为是「面板**直接隐藏** + 提示**只在可用时才设置**」，
            //   而「元数据模式」下拉当时**仍可选中「删除全部」** ⇒ 剥离**静默失效**：
            //   ffmpeg 侧仍用 `-map_metadata 0` 把源元数据整体复制 ⇒ 产物**原样带出 GPS**
            //   （已实机复现：缺 exiftool 时产物 GPS 与源逐字相同，且日志零点名、任务报成功）。
            Safe("C3d exiftool不可用→面板可见+选项禁用+强制保留+提示", () =>
            {
                var fPath = typeof(FfmpegGui.Services.ExifToolService).GetField("_detectedPath", BFS);
                var fDet = typeof(FfmpegGui.Services.ExifToolService).GetField("_detected", BFS);
                var oldPath = fPath?.GetValue(null);
                var oldDet = fDet?.GetValue(null);
                try
                {
                    // 模拟「已经检测过、但没找到 exiftool」（IsAvailable 的判据是 `_detectedPath != null`）
                    fDet?.SetValue(null, true);
                    fPath?.SetValue(null, null);
                    typeof(FfmpegGui.MainWindow).GetMethod("UpdateExifToolPanelState", BF)!.Invoke(W, null);
                    Pump(8);
                    var mode = Find<ComboBox>("MetadataModeCombo");
                    var panel = Find<Border>("ExifToolPanel");
                    var gps = Find<CheckBox>("StripExifGpsCheck");
                    var hint = Find<TextBlock>("ExifToolHint");
                    Check("C3d exiftool不可用→面板仍可见（否则用户得不到任何解释）",
                          panel?.IsVisible == true, $"visible={panel?.IsVisible}");
                    Check("C3d exiftool不可用→模式下拉禁用（不得再选「删除全部」）",
                          mode?.IsEnabled == false, $"enabled={mode?.IsEnabled}");
                    Check("C3d exiftool不可用→模式强制回「保留」",
                          mode?.SelectedIndex == 0, $"idx={mode?.SelectedIndex}");
                    Check("C3d exiftool不可用→strip复选框禁用",
                          gps?.IsEnabled == false, $"enabled={gps?.IsEnabled}");
                    Check("C3d exiftool不可用→提示明确说明不可用（点名 exiftool）",
                          hint?.Text != null && hint.Text.Contains("exiftool", StringComparison.OrdinalIgnoreCase),
                          $"hint=\"{hint?.Text}\"");
                }
                finally
                {
                    // ⚠ 必须**还原**，否则这条锁会把后续用例的状态带坏（且是跨用例的静态状态）
                    fDet?.SetValue(null, oldDet);
                    fPath?.SetValue(null, oldPath);
                    typeof(FfmpegGui.MainWindow).GetMethod("UpdateExifToolPanelState", BF)!.Invoke(W, null);
                    Pump(8);
                }
            });
            // 还原后必须回到「可用」态（否则说明上面那条锁污染了静态状态）
            Check("C3d 还原后 strip复选框重新启用",
                  Find<CheckBox>("StripExifGpsCheck")?.IsEnabled == true,
                  $"enabled={Find<CheckBox>("StripExifGpsCheck")?.IsEnabled}");
            Safe("C4 转换模式=动图→动图面板", () =>
            {
                var m = Find<ComboBox>("ConversionModeCombo");
                var anim = Find<StackPanel>("AnimationPanel");
                if (m != null) m.SelectedIndex = 1; Pump(8);
                Check("C4 动图面板", anim?.IsVisible == true, $"visible={anim?.IsVisible}");
            });
            Safe("C5 转换模式=RAW→RAW面板", () =>
            {
                var m = Find<ComboBox>("ConversionModeCombo");
                var raw = Find<StackPanel>("RawPanel");
                var still = Find<StackPanel>("StillOptionsPanel");
                if (m != null) m.SelectedIndex = 2; Pump(8);
                Check("C5 RAW面板显示", raw?.IsVisible == true, $"visible={raw?.IsVisible}");
                Check("C5b RAW时静态选项隐藏", still?.IsVisible == false, $"visible={still?.IsVisible}");
                if (m != null) m.SelectedIndex = 0; Pump(8); // 复位静态模式
            });

            // C6（2026-10-09）：JPEG LI 面板里「渐进档 vs 固定 Huffman 码表」冲突提示的运行时回归锁。
            // 为什么必须有：cjpegli 的固定码表只接受 p 0（实测与 p1/p2 同用 ⇒ 退出码 1、零产物），
            // 所以取消「优化 Huffman 编码」时 CjpegliService.BuildCjpegliArguments 只能把用户选的渐进档
            // 降成 -p 0 —— 这件事在加本行之前**完全静默**（实机读数：命令行只剩 -p 0、产物 Baseline、
            // 日志一句都没有）。三条对照臂（选基线/优化开着/收起高级面板）必须**不**提示，
            // 否则这条提示就是恒真的、判不出回归。
            Safe("C6 JPEG LI 固定码表 vs 渐进档 冲突提示", () =>
            {
                var adv = Find<CheckBox>("UseAdvancedCodec");
                var panel = Find<StackPanel>("JpegliCodecPanel");
                var opt = Find<CheckBox>("JpegliOptimizeCheck");
                var prog = Find<ComboBox>("JpegliProgressiveCombo");
                var hint = Find<TextBlock>("JpegliProgConflictHint");
                var adv0 = adv?.IsChecked;
                var panel0 = panel?.IsVisible;
                var opt0 = opt?.IsChecked;
                var prog0 = prog?.SelectedIndex;
                try
                {
                    if (adv != null) adv.IsChecked = true; Pump(4);
                    // JPEG LI 面板的可见性由「编码器下拉选中项 + 工具可用性过滤」共同决定，
                    // 本组验的是**冲突判定与刷新接线**本身 ⇒ 直接把面板置为可见（同 C3 的注入理由）。
                    if (panel != null) panel.IsVisible = true;
                    if (opt != null) opt.IsChecked = true; Pump(4);
                    if (prog != null) prog.SelectedIndex = 2; Pump(4); // 2 = 渐进

                    if (opt != null) opt.IsChecked = false; Pump(8);   // 勾掉优化 ⇒ 渐进会被降 ⇒ 该提示
                    Check("C6 冲突成立→渐进选项下方提示可见", hint?.IsVisible == true, $"visible={hint?.IsVisible}");

                    if (prog != null) prog.SelectedIndex = 1; Pump(8); // 对照臂①：本来就选基线 ⇒ 没降级
                    Check("C6b 选基线（无降级）→提示隐藏", hint?.IsVisible == false, $"visible={hint?.IsVisible}");

                    if (prog != null) prog.SelectedIndex = 2; Pump(8);
                    if (opt != null) opt.IsChecked = true; Pump(8);    // 对照臂②：优化开着 ⇒ 不冲突
                    Check("C6c 优化开着（无冲突）→提示隐藏", hint?.IsVisible == false, $"visible={hint?.IsVisible}");

                    if (opt != null) opt.IsChecked = false; Pump(8);
                    if (adv != null) adv.IsChecked = false; Pump(8);    // 对照臂③：收起高级面板 ⇒ 采集侧恒 optimize=true
                    Check("C6d 收起高级编码面板→提示隐藏", hint?.IsVisible == false, $"visible={hint?.IsVisible}");
                }
                finally
                {
                    if (adv != null) adv.IsChecked = adv0;
                    if (panel != null) panel.IsVisible = panel0 ?? false;
                    if (opt != null) opt.IsChecked = opt0 ?? true;
                    if (prog != null) prog.SelectedIndex = prog0 ?? 2;
                    Pump(4);
                }
            });

            // C20/C21（2026-10-02）：托盘缺图标那一批的运行时回归锁。
            // 旧失效链全是静默的：icon 没进 `<AvaloniaResource>` ⇒ AssetLoader.Exists=false ⇒ LoadTrayIcon()
            // 返回 null ⇒ 托盘只有菜单。ServiceProbe 那边只能验"程序集清单收录了它"，**运行时解析**
            // （XAML 的 `Icon="avares://…"` 字符串转 ImageSource）只有本宿主能验。
            Safe("C20 图标资源在运行时可解析（托盘/窗口）", () =>
            {
                Check("C20a 窗口 Icon 已由 XAML 解析", W.Icon != null,
                    $"Icon={(W.Icon == null ? "null" : W.Icon.GetType().Name)}");
                var uri = new System.Uri("avares://FfmpegGui/Resources/icon_raw.png");
                bool exists = Avalonia.Platform.AssetLoader.Exists(uri);
                Check("C20b AssetLoader 认得托盘用的那条 avares URI", exists,
                    "App.LoadTrayIcon() 判的就是这一步");
                if (exists)
                {
                    using var s = Avalonia.Platform.AssetLoader.Open(uri);
                    using var bmp = new Avalonia.Media.Imaging.Bitmap(s);
                    // 对照：同一资源**先整读进内存**再解码。AssetLoader 返回的流若不可 seek，
                    // Skia 的位图解码会静默给出一张退化图（1×1）——那才是真正的失效点。
                    using var ms = new System.IO.MemoryStream();
                    s.Position = 0; s.CopyTo(ms); ms.Position = 0;
                    using var bmp2 = new Avalonia.Media.Imaging.Bitmap(ms);
                    // 第二对照：**仓里那份原 PNG** 直接按文件路径解码 + 比较 avares 吐出的字节数。
                    // 若文件解码正常而 avares 是 1×1 ⇒ 打包进去的字节就不是它（资源收录/路径问题），
                    // 而不是解码器的问题 —— 两者的修法完全不同。
                    var diskPath = "";
                    for (var di = new System.IO.DirectoryInfo(AppContext.BaseDirectory); di != null; di = di.Parent)
                    {
                        var cand = System.IO.Path.Combine(di.FullName, "src", "FfmpegGui", "Resources", "icon_raw.png");
                        if (System.IO.File.Exists(cand)) { diskPath = cand; break; }
                    }
                    int diskW = -1, diskH = -1;
                    if (System.IO.File.Exists(diskPath))
                    {
                        using var b3 = new Avalonia.Media.Imaging.Bitmap(diskPath);
                        diskW = b3.PixelSize.Width; diskH = b3.PixelSize.Height;
                    }
                    // 对照件：宿主自己素材目录里那张**已知 8bit PNG**（src_8bit.png）。
                    // 对照臂不动就不能改结论 ⇒ 若连它都解成 1×1，那是 headless 宿主根本不解码位图，
                    // 本条只能记 SKIP（判不了），绝不能记绿、也不能算产品红。
                    var ctlPath = System.IO.Path.Combine(SrcDir, "src_8bit.png");
                    int ctlW = -1, ctlH = -1;
                    if (System.IO.File.Exists(ctlPath))
                    {
                        using var b4 = new Avalonia.Media.Imaging.Bitmap(ctlPath);
                        ctlW = b4.PixelSize.Width; ctlH = b4.PixelSize.Height;
                    }
                    // C20d：收录**是不是那份字节**（原缺陷就是收录缺失；解码在本宿主判不了，
                    // 但"路径对、内容对、exe 图标位有资源"这三条是可以硬验的）。
                    var avBytes = ms.ToArray();
                    long fileLen = System.IO.File.Exists(diskPath) ? new System.IO.FileInfo(diskPath).Length : -1;
                    Check("C20d avares 吐出的就是那份 PNG",
                          avBytes.Length == fileLen && avBytes.Length > 8
                          && avBytes[1] == (byte)'P' && avBytes[2] == (byte)'N' && avBytes[3] == (byte)'G',
                          $"avares={avBytes.Length} B vs 文件={fileLen} B，魔数 %PNG");
                    bool hostDecodes = ctlW > 1 && ctlH > 1;
                    if (!hostDecodes)
                    {
                        // 对照件也是 1×1 ⇒ 本宿主根本不解码位图。这条**既不能记绿也不能记红**，
                        // 宿主没有 skip 桶，就打成显式 N/A 行留在日志里（读者看得见"没判"）。
                        Console.WriteLine("  [N/A] C20c 位图解码尺寸：headless 宿主不解码"
                            + $"（对照件 src_8bit.png 也解成 {ctlW}x{ctlH}），图标字节已由 C20d 验过");
                    }
                    else
                    {
                        Check("C20c 图标可解码且 ≥16px",
                            bmp.PixelSize.Width >= 16 && bmp.PixelSize.Height >= 16,
                            $"流直解={bmp.PixelSize.Width}x{bmp.PixelSize.Height} 对照件={ctlW}x{ctlH}");
                    }
                }
            });
            Safe("C21 缩略图面板随容器能力显隐", () =>
            {
                var adv = Find<CheckBox>("UseAdvancedCodec");
                if (adv != null) adv.IsChecked = true;
                SetFormat("AVIF");
                bool panelOn = Find<StackPanel>("ThumbnailPanel")?.IsVisible ?? false;
                bool boxOn = Find<NumericUpDown>("ThumbnailSizeBox")?.IsEnabled ?? false;
                SetFormat("TIFF");
                bool panelOff = Find<StackPanel>("ThumbnailPanel")?.IsVisible ?? true;
                var tick = Find<CheckBox>("EnableThumbnailCheck");
                bool reset = tick == null || tick.IsChecked != true;
                Check("C21 能力 true(AVIF) 显示且可编辑 / 能力 false(TIFF) 隐藏并复位勾选",
                    panelOn && boxOn && !panelOff && reset,
                    $"avif(visible={panelOn},enabled={boxOn}) tiff(visible={panelOff},复位={reset})");
            });
        }

        // ══════════════════════════ 组 D ══════════════════════════
        static void GroupD_EncoderComboLinkage()
        {
            Console.WriteLine("\n########## D组: 编码器下拉联动 ##########");
            Safe("D1 格式切换→EncoderCombo重填", () =>
            {
                SetFormat("AVIF"); var encAvif = Find<ComboBox>("EncoderCombo")?.ItemCount ?? -1;
                SetFormat("JPEG XL"); var encJxl = Find<ComboBox>("EncoderCombo")?.ItemCount ?? -1;
                Check("D1 EncoderCombo有项", encAvif > 0 && encJxl > 0, $"avif={encAvif} jxl={encJxl}");
            });
        }

        // ══════════════════════════ 组 N ══════════════════════════
        static void GroupN_PresetRoundTrip()
        {
            Console.WriteLine("\n########## N组: 预设保存→载入往返 ColorStrategy ##########");
            SweepReset();

            var radioNames = new[] { "IccModeNone", "IccModeCarry", "IccModeBake", "IccModeBakeOnly" };
            var strategies = new[] { "Recommended", "CarryIcc", "BakeCicpOnly", "Manual" };

            var mwType = typeof(FfmpegGui.MainWindow);
            var build = mwType.GetMethod("BuildPresetData", BF);
            if (build == null) { Check("N0 BuildPresetData 可反射", false, "not found"); return; }

            var tripA = new List<string>();            // 载入路径 A：FfmpegOptions.ApplyPresetData（CLI --preset 用）
            var tripB = new List<string>();            // 载入路径 B：CreateQueueItemFromSimplePreset（简洁模式入队）
            var savedStrategy = new List<string>();    // 存盘对象里的 ColorStrategy 原样值（诊断）
            var savedIccMode = new List<string>();     // 存盘对象里的 IccMode 原样值（诊断）

            // ── N1 逐按钮：选中 → 保存 → JSON 往返 → 载入 → 读回 ──
            for (int i = 0; i < radioNames.Length; i++)
            {
                var rn = radioNames[i]; var exp = strategies[i];
                Safe("N1 " + rn, () =>
                {
                    SetStrategyRadio(rn);
                    Pump(8);
                    var live = mwType.GetMethod("GetColorStrategy", BF)!.Invoke(W, null)!.ToString();
                    // 保存（真实 UI → 预设对象）
                    var p = (FfmpegGui.Models.PresetData)build.Invoke(W, null)!;
                    savedStrategy.Add(p.ColorStrategy ?? "<null>");
                    savedIccMode.Add(p.IccMode ?? "<null>");
                    // 真实持久化路径：JSON 序列化 + 反序列化（证明字段能过盘；null 字段会被 WhenWritingNull 省略）
                    var p2 = FfmpegGui.Models.PresetData.FromJson(p.ToJson());
                    // 载入路径 A：FfmpegOptions.ApplyPresetData
                    var opts = new FfmpegGui.Models.FfmpegOptions();
                    opts.ApplyPresetData(p2);
                    var gotA = opts.ColorStrategy.ToString();
                    tripA.Add(gotA);
                    Check("N1a " + rn + " 实时=" + exp + " ⇒ 载入A 不变", live == exp && gotA == exp,
                          $"live={live} saved.IccMode={p2.IccMode ?? "<null>"} saved.ColorStrategy={p2.ColorStrategy ?? "<null>"} gotA={gotA} expect={exp}");

                    // 载入路径 B（2026-09-26 重写）：预设 → ApplyPresetData(真实控件) → 唯一采集点。
                    // 原先这条走 `CreateQueueItemFromSimplePreset`（简洁模式自带的第二套
                    // PresetData→FfmpegOptions 映射）；那条映射已删 —— 简洁模式现在与入队共用采集点，
                    // 所以这条锁量的是「预设能否完整描述高级模式状态」，比原来更强。
                    var apply = mwType.GetMethod("ApplyPresetData", BF);
                    var collect = mwType.GetMethod("CollectOptionsFromUiAsync", BF);
                    if (apply == null || collect == null)
                    {
                        Check("N1b " + rn + " 反射入口可用", false,
                              "ApplyPresetData=" + (apply == null ? "missing" : "ok")
                            + " CollectOptionsFromUiAsync=" + (collect == null ? "missing" : "ok"));
                        return;
                    }
                    apply.Invoke(W, new object?[] { p2 });
                    Pump(8);
                    var collectTask = (System.Threading.Tasks.Task<FfmpegGui.Models.FfmpegOptions>)collect
                        .Invoke(W, new object?[] { System.IO.Path.Combine(SrcDir, "src_8bit.png"), false })!;
                    for (int spin = 0; spin < 300 && !collectTask.IsCompleted; spin++) Pump(1);
                    if (!collectTask.IsCompleted)
                    {
                        Check("N1b " + rn + " 采集已收敛", false, "collector never completed (UI pump starved)");
                        return;
                    }
                    var gotB = collectTask.Result.ColorStrategy.ToString();
                    tripB.Add(gotB);
                    Check("N1b " + rn + " 实时=" + exp + " ⇒ 载入B(预设→控件→采集) 不变", gotB == exp,
                          $"gotB={gotB} expect={exp}");
                });
            }

            // ── N2 负控：4 个按钮往返后的策略必须**互不相同** ──
            // 若某个折叠把它们压成同一个，"逐值不变"会退化成恒真（4 个都等于同一个值也算"不变"）。
            Safe("N2 负控 往返后 4 值互不相同", () =>
            {
                if (tripA.Count != 4 || tripB.Count != 4)
                { Check("N2 快照数=4", false, $"A={tripA.Count} B={tripB.Count}"); return; }
                Check("N2a 载入A 往返后 4 值互不相同", new HashSet<string>(tripA).Count == 4,
                      "tripA=[" + string.Join(",", tripA) + "]");
                Check("N2b 载入B 往返后 4 值互不相同", new HashSet<string>(tripB).Count == 4,
                      "tripB=[" + string.Join(",", tripB) + "]");
                // 负控的负控：保存侧 IccMode 必须仍是 4 个不同值。
                // 否则说明"按钮→IccMode"的映射本身塌了，N1 的错位根因就不是"漏写 ColorStrategy"。
                Check("N2c 保存侧 IccMode 4 值互不相同", new HashSet<string>(savedIccMode).Count == 4,
                      "IccMode=[" + string.Join(",", savedIccMode) + "]");
            });

            // ── N3 向后兼容：**只有 IccMode、没有 ColorStrategy** 的旧预设，迁移路径必须仍在 ──
            // 锁的是"修保存侧时别把遗留迁移顺手删掉"：手工构造旧格式 JSON，走真实反序列化入口。
            Safe("N3 旧预设(仅 IccMode)迁移仍在", () =>
            {
                var cases = new[,] {
                    { "CarryIcc",       "CarryIcc" },
                    { "BakeOnly",       "BakeCicpOnly" },
                    { "BakeToStandard", "Recommended" },
                    { "None",           "Recommended" },
                };
                for (int i = 0; i < cases.GetLength(0); i++)
                {
                    var json = "{\"IccMode\":\"" + cases[i, 0] + "\"}";
                    var p = FfmpegGui.Models.PresetData.FromJson(json);
                    var opts = new FfmpegGui.Models.FfmpegOptions();
                    opts.ApplyPresetData(p);
                    var got = opts.ColorStrategy.ToString();
                    Check("N3 旧预设 IccMode=" + cases[i, 0] + "⇒" + cases[i, 1],
                          p.ColorStrategy == null && got == cases[i, 1],
                          $"parsed.ColorStrategy={p.ColorStrategy ?? "<null>"} ColorStrategy={got} expect={cases[i, 1]}");
                }
            });

            // ── N4 载体锁：存盘的 ColorStrategy 字符串必须**自身可解析回同一策略** ──
            // 为什么必须单独锁（实测教训）：N1 只看"载入后的最终值"。对 None/Carry 两个模式，载入侧的
            // 遗留回退 FromIccMode(IccMode) 恰好给出**同一答案** ⇒ 即使 ColorStrategy 写错或写空，
            // N1 也照样绿（实测：把存盘的 "carry-icc" 改成 "carry-iccMUT"，N1a/N1b/N2 全部仍绿 ——
            // 回退把它掩盖了）。本锁绕过回退，直接要求规范串能被 ColorStrategyMapper.TryParse 还原，
            // 这样 4 个模式的"载体"都具备区分力，而不是只有 Bake/BakeOnly 两个分叉模式。
            Safe("N4 存盘 ColorStrategy 规范串可还原", () =>
            {
                var mi = typeof(FfmpegGui.Models.ColorStrategyMapper)
                    .GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static);
                if (mi == null) { Check("N4 TryParse 可反射", false, "not found"); return; }
                for (int i = 0; i < radioNames.Length; i++)
                {
                    var raw = savedStrategy[i];
                    var args = new object?[] { raw, null };
                    var ok = (bool)mi.Invoke(null, args)!;
                    var got = args[1]?.ToString() ?? "<null>";
                    Check("N4 " + radioNames[i] + " 存盘串可还原为 " + strategies[i],
                          ok && got == strategies[i],
                          $"saved.ColorStrategy={raw} ok={ok} parsed={got} expect={strategies[i]}");
                }
            });

            // ── N5 应用预设（MainWindow.ApplyPresetData）必须恢复策略单选 ──
            // 为什么单开一组：N1/N3/N4 走的都是 `FfmpegOptions.ApplyPresetData`（CLI `--preset` 与
            // 简洁模式入队），**碰不到**「应用预设」按钮那条 UI 路径（`MainWindow.ApplyPresetData`，
            // 由 `OpenPresetManager_Click` / `SimplePresetCombo_SelectionChanged` 调用）。
            // 而缺陷恰在那条路径：该方法内对 `IccMode*` / `ColorStrategy` 的引用数 = 0
            // ⇒ 应用预设后 4 个单选保持原样，下一次保存/编码用的是**旧策略**（预设形同虚设）。
            // 场景：PNG + 默认目标（auto）。**刻意不用** Display P3 目标 —— 实测 `ApplyPresetData` 末尾
            // 调用的 `UpdateOptionAvailability()`（MainWindow.xaml.cs:2147-2170）会 clear + 重建
            // ColorSpaceCombo 并把 SelectedIndex 归 0（auto）⇒ 预设里的**目标色域在"应用预设"后本就留不住**
            // （这是另一条既有行为，不在本次修复范围；已登记在报告里）。若用 P3 做场景，断言会把
            // "目标色域被重置"误判成"策略没恢复"。
            // ⚠ 预览串里**没有** `--color-strategy` 字面量（那是 CLI 的键，不是 ffmpeg 参数）；策略经像素链
            //   体现为 zscale / iccgen / -map_metadata（口径同 K3）。auto 目标下 4 策略收敛成两类：
            //   CarryIcc（无 zscale + `-map_metadata:s:v 0:s:v`）与其余三者（`-map_metadata:s:v -1`）
            //   ⇒ 命令级断言只在这一条边界上有区分力；**4 值逐值的强断言由 N5a「单选恢复」承担**。
            var applyUi = mwType.GetMethod("ApplyPresetData", BF);
            if (applyUi == null) { Check("N5 ApplyPresetData 可反射", false, "not found"); }
            else
            {
                // ① 逐策略基线：**直接选中**该策略时的命令串（同场景，作为「恢复后命令」的期望值）
                var baseCmd = new List<string>();
                for (int i = 0; i < radioNames.Length; i++)
                {
                    var rn = radioNames[i];
                    Safe("N5 基线 " + rn, () =>
                    {
                        SweepReset();
                        SetFormat("PNG"); Pump(8);
                        SetStrategyRadio(rn);
                        // 归一化到「真实 UI 稳态」：`ApplyPresetData` 末尾会调 `UpdateOptionAvailability`
                        // （实测副作用：PNG 被强制锁成无损 ⇒ `-compression_level 0`）。基线若不补这一步，
                        // N5b 的比较会把"跨状态噪声"（如 compression_level 1 vs 0）误报成策略差异。
                        typeof(FfmpegGui.MainWindow).GetMethod("UpdateOptionAvailability", BF)!.Invoke(W, null);
                        Pump(8);
                        Regen();
                        baseCmd.Add(Cmd());
                        DumpCmd("N5 基线 " + rn + "(" + strategies[i] + ")", Cmd());
                    });
                }

                // ② 往返：设单选 → 存预设(真实 JSON 往返) → 改成**另一种**策略 → 应用预设 → 单选恢复
                for (int i = 0; i < radioNames.Length; i++)
                {
                    var rn = radioNames[i]; var exp = strategies[i];
                    var otherIdx = (i + 1) % radioNames.Length;   // 换成另一种策略（必不同）
                    Safe("N5 往返 " + rn, () =>
                    {
                        SweepReset();
                        SetFormat("PNG"); Pump(8);
                        SetStrategyRadio(rn);
                        Regen();
                        var p = (FfmpegGui.Models.PresetData)build.Invoke(W, null)!;
                        var pBack = FfmpegGui.Models.PresetData.FromJson(p.ToJson());
                        // 改成另一种策略（真实 UI 操作；证明"恢复"不是"本来就对"）
                        SetStrategyRadio(radioNames[otherIdx]);
                        Pump(8);
                        var before = mwType.GetMethod("GetColorStrategy", BF)!.Invoke(W, null)!.ToString();
                        // 应用预设（真实 UI 路径，此前缺陷所在）
                        applyUi.Invoke(W, new object?[] { pBack });
                        Pump(12);
                        var after = mwType.GetMethod("GetColorStrategy", BF)!.Invoke(W, null)!.ToString();
                        Check("N5a " + rn + " 应用预设后单选恢复", after == exp,
                              $"改前={before}({radioNames[otherIdx]}) 应用后={after} 期望={exp} "
                              + $"saved.ColorStrategy={pBack.ColorStrategy ?? "<null>"} saved.IccMode={pBack.IccMode ?? "<null>"}");
                        Regen();
                        var restored = Cmd();
                        if (restored != baseCmd[i])
                        {
                            int k = 0, n = Math.Min(restored.Length, baseCmd[i].Length);
                            while (k < n && restored[k] == baseCmd[i][k]) k++;
                            int s0 = Math.Max(0, k - 40);
                            Console.WriteLine($"      [diff] N5b {rn} firstDiff@{k}");
                            Console.WriteLine($"      [diff] got  …{restored.Substring(s0)}");
                            Console.WriteLine($"      [diff] base …{baseCmd[i].Substring(s0)}");
                        }
                        Check("N5b " + rn + " 恢复后命令 == 同场景直接选中基线", restored == baseCmd[i],
                              $"got={Snip(restored)} | base={Snip(baseCmd[i])}");
                        // ③ token 级：与策略一致的 ffmpeg token（auto 目标下的口径见本组开头注释）
                        var t = Tok(restored);
                        bool tokenOk = exp == "CarryIcc"
                            ? ZscaleParam(t, "p") == null && Val(t, "-map_metadata:s:v") == "0:s:v"
                            : Val(t, "-map_metadata:s:v") == "-1";
                        Check("N5c " + rn + " 命令 token 与策略一致", tokenOk,
                              $"p={ZscaleParam(t, "p") ?? "<none>"} iccgen={VfHas(t, "iccgen")} "
                              + $"mm:v={Val(t, "-map_metadata:s:v") ?? "<none>"}");
                        // ④ 区分力负控：在**同一状态**下切到另一种策略，跨 CarryIcc 边界的组合命令必须变
                        //    （若不变，N5b/N5c 就是恒真的 —— 正是 K1 登记过的"两策略同形"陷阱）
                        SetStrategyRadio(radioNames[otherIdx]); Regen();
                        var otherCmd = Cmd();
                        SetStrategyRadio(rn); Regen();
                        bool crossesCarry = (exp == "CarryIcc") ^ (strategies[otherIdx] == "CarryIcc");
                        if (crossesCarry)
                            Check("N5d " + rn + " 负控 换策略⇒命令必变", otherCmd != restored,
                                  $"same! {exp} vs {strategies[otherIdx]} :: {Snip(restored)}");
                        else
                            Console.WriteLine($"      [info] N5d {rn}: {exp} 与 {strategies[otherIdx]} 在本场景命令同形（登记，不判红）");
                    });
                }

                // ── N6 负控：ColorStrategy 与 IccMode 都是垃圾串 ⇒ 单选不得被静默改写成 Recommended ──
                // 防的是一种"看起来修好了"的错法：解析失败时兜底成 Recommended。
                // 做法：先选一个**非默认**策略（CarryIcc），再应用一个两字段都是垃圾串的预设；
                // 要求单选**保持 CarryIcc**，且日志**点名**两个字段（本仓价值序：响亮且点名）。
                Safe("N6 负控 双垃圾串不得静默改写策略", () =>
                {
                    SweepReset();
                    SetStrategyRadio("IccModeCarry");
                    Pump(8);
                    var logBox = Find<TextBox>("LogText");
                    if (logBox != null) logBox.Text = "";   // 只留本次应用产生的日志
                    var p = new FfmpegGui.Models.PresetData
                    {
                        ColorStrategy = "definitely-not-a-strategy",
                        IccMode = "not-an-icc-mode",
                    };
                    applyUi.Invoke(W, new object?[] { p });
                    Pump(12);
                    var got = mwType.GetMethod("GetColorStrategy", BF)!.Invoke(W, null)!.ToString();
                    Check("N6a 负控 双垃圾串⇒保持现状(CarryIcc)", got == "CarryIcc",
                          $"got={got} expect=CarryIcc（被静默改成 Recommended 即为缺陷）");
                    var log = logBox?.Text ?? "";
                    Check("N6b 负控 日志点名两个字段(响亮且点名)",
                          log.Contains("color strategy unresolved")
                          && log.Contains("definitely-not-a-strategy")
                          && log.Contains("not-an-icc-mode"),
                          "log=" + Snip(log));
                });

                // ── N7 旧预设（只有 IccMode、无 ColorStrategy）经**应用预设**UI 路径的迁移 ──
                // 与 N3（FfmpegOptions 路径）对位：锁住 UI 路径上遗留迁移同样存在（含 4→3 折叠）。
                // 每个 case 先把单选设成**与期望不同**的策略 ⇒ "恢复"才有区分力。
                Safe("N7 旧预设经 UI 路径迁移", () =>
                {
                    var cases = new[,] {
                        { "CarryIcc",       "CarryIcc" },
                        { "BakeOnly",       "BakeCicpOnly" },
                        { "BakeToStandard", "Recommended" },
                        { "None",           "Recommended" },
                    };
                    for (int i = 0; i < cases.GetLength(0); i++)
                    {
                        SweepReset();
                        var beforeRadio = cases[i, 1] == "Recommended" ? "IccModeCarry" : "IccModeNone";
                        SetStrategyRadio(beforeRadio);
                        Pump(8);
                        // 真实旧格式 JSON：**只有** IccMode 字段
                        var json = "{\"IccMode\":\"" + cases[i, 0] + "\"}";
                        var p = FfmpegGui.Models.PresetData.FromJson(json);
                        applyUi.Invoke(W, new object?[] { p });
                        Pump(12);
                        var got = mwType.GetMethod("GetColorStrategy", BF)!.Invoke(W, null)!.ToString();
                        Check("N7 旧预设(UI路径) IccMode=" + cases[i, 0] + "⇒" + cases[i, 1],
                              p.ColorStrategy == null && got == cases[i, 1],
                              $"改前={beforeRadio} parsed.ColorStrategy={p.ColorStrategy ?? "<null>"} 应用后={got} 期望={cases[i, 1]}");
                    }
                });
            }

            // 诊断表（非断言）：把「期望 vs 实际」打出来供人工核对
            Console.WriteLine("      [table] 按钮 | 实时策略 | 存盘IccMode | 存盘ColorStrategy | 载入A(FfmpegOptions) | 载入B(简洁模式)");
            for (int i = 0; i < radioNames.Length && i < tripA.Count; i++)
                Console.WriteLine($"      [table] {radioNames[i]} | {strategies[i]} | {savedIccMode[i]} | {savedStrategy[i]} | {tripA[i]} | {tripB[i]}");

            typeof(FfmpegGui.MainWindow).GetField("_simpleActivePreset", BF)?.SetValue(W, null);
            SweepReset();
        }

        // ══════════════ 入口（供 Program.Main 逐 mode 分派）══════════════
        // 只做转发 + 一个**共享收敛前置**：组体本身是逐字搬来的私有方法，保持原貌不动。
        internal static void RunA()
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
            GroupA_FormatToCommand();
        }
        internal static void RunB()
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
            GroupB_ParameterPassing();
        }
        internal static void RunC()
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
            GroupC_PanelVisibility();
        }
        internal static void RunD()
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
            GroupD_EncoderComboLinkage();
        }
        internal static void RunN()
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
            GroupN_PresetRoundTrip();
        }
    }
}
