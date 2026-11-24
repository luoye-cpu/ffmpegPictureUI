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
        private static void Regen() => UiHost.Regen();
        private static string Cmd() => UiHost.Cmd();
        private static void SetFormat(string s) => UiHost.SetFormat(s);
        private static void Click(string name) => UiHost.Click(name);
        private static void Check(string name, bool cond, string detail = "") => UiHost.Check(name, cond, detail);
        private static void Safe(string name, Action a) => UiHost.Safe(name, a);
        private static List<string> Tok(string s) => UiHost.Tok(s);
        private static string? Val(List<string> t, string key) => UiHost.Val(t, key);
        private static bool VfHas(List<string> t, string seg) => UiHost.VfHas(t, seg);
        private static void DumpCmd(string tag, string cmd) => UiHost.DumpCmd(tag, cmd);
        private static void SweepReset() => UiHost.SweepReset();
        private static void SetStrategyRadio(string name) => UiHost.SetStrategyRadio(name);
        private static string? PickEncoder(Func<string, bool> pred) => UiHost.PickEncoder(pred);
        private static string? ZscaleParam(List<string> t, string key) => UiHost.ZscaleParam(t, key);
        private static string Snip(string s) => UiHost.Snip(s);

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
                          hint?.Text != null && (hint?.Text ?? "").Contains("exiftool", StringComparison.OrdinalIgnoreCase),
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

            // C22（2026-11-16，UI-04 锁）：oxipng（PNG 优化器）面板显隐 + 默认档回归锁。
            //   c5f18e7 把面板从 PngCodecPanel 移到 StillOptionsPanel 顶层，判定 = 静态 PNG 才显示；
            //   此前该迁移零 UI 断言（Grep tests/projects 无一处引用 PngOptimizer）。
            //   面板被误挂回动图出口 / 默认档被改回 off ⇒ 本三条当场红。
            Safe("C22 oxipng面板显隐+默认max", () =>
            {
                // 先确保静态模式（C5 可能把模式停在 RAW）
                var m = Find<ComboBox>("ConversionModeCombo");
                if (m != null) m.SelectedIndex = 0; Pump(8);
                var panel = Find<StackPanel>("PngOptimizerPanel");
                var combo = Find<ComboBox>("PngOptimizerCombo");

                SetFormat("PNG"); Pump(8);
                Check("C22a 静态 PNG → oxipng 面板显示", panel?.IsVisible == true,
                      $"visible={panel?.IsVisible}");
                Check("C22b 默认档 SelectedIndex=2 (max)", combo?.SelectedIndex == 2,
                      $"idx={combo?.SelectedIndex}");

                SetFormat("JPEG"); Pump(8);
                Check("C22c 非 PNG(JPEG) → oxipng 面板隐藏", panel?.IsVisible == false,
                      $"visible={panel?.IsVisible}");
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

            // ── N8 设置改→重启→GUI 生效（2026-11-12 批 4·3，docs/TEST_MODULE_PLAN_2026-11-12.md）──
            //   调研盲区#6：服务层 roundtrip 有（ServiceProbe settings mode），「GUI 端改→重启→生效」零判据。
            //   重启链三段进程内兑现：① Save() 落盘（便携契约：exe 同目录 settings.json，AppSettingsService.cs:11-14）
            //   ② 清 _current 缓存 ⇒ Current 惰性重读磁盘（= 新进程的第一步，AppSettingsService.cs:28）
            //   ③ 设置 → 控件：LoadSettings()（MainWindow.xaml.cs:1336；ctor :1333 调的就是它 ⇒ 调它 = 新窗口
            //     构造的「应用」半段）。⚠ 有意不 new 第二个 MainWindow：其 ctor 会起托盘线程/异步全量检测，
            //     第二实例的线程与托盘生命周期不受 Bootstrap 管 ⇒ 用 LoadSettings 等价锁「应用」段。
            //   ⚠ Save() 真写 UiTestHost.exe 侧 settings.json（bin 内，工作区合规）：先快照原字节，
            //     finally 原样回写/删除 + 服务层缓存复位（verify-ipc-extra-options 对产品 exe 侧同款先例）。
            //   ⚠ LoadSettings 副作用 = LogText 追加 [PLAN]/[detect] 行 + 再踢一次异步检测；
            //     GroupP 在 Main 里最先跑（:125 注释实证其日志面在 P 时点固化）⇒ 此处追加无下游受害者；
            //     仍做 LogText 快照/还原保持判据面干净。
            Safe("N8 设置改→重启→GUI 生效", () =>
            {
                var st = FfmpegGui.Services.AppSettingsService.Current;
                bool origPreserve = st.PreserveInputFolderStructure;
                int origMaxQ = st.MaxQueueSize;
                var asmDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
                var settingsPath = System.IO.Path.Combine(asmDir, "settings.json");
                byte[]? origBytes = System.IO.File.Exists(settingsPath) ? System.IO.File.ReadAllBytes(settingsPath) : null;
                var logBox = Find<TextBox>("LogText");
                var logSnap = logBox?.Text;
                try
                {
                    bool flipped = !origPreserve;
                    st.PreserveInputFolderStructure = flipped;
                    st.MaxQueueSize = 37;
                    Check("N8a Save() 落盘成功", FfmpegGui.Services.AppSettingsService.Save(),
                          "LastSaveIssue=" + (FfmpegGui.Services.AppSettingsService.LastSaveIssue ?? "<null>")
                          + " LastSavedTo=" + (FfmpegGui.Services.AppSettingsService.LastSavedTo ?? "<null>"));

                    // 「重启」②：清服务层缓存 ⇒ Current 惰性重读磁盘（新进程读盘等价）
                    var curFld = typeof(FfmpegGui.Services.AppSettingsService)
                        .GetField("_current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    curFld!.SetValue(null, null);
                    var reloaded = FfmpegGui.Services.AppSettingsService.Current;
                    Check("N8b 重读(bool) 翻转在", reloaded.PreserveInputFolderStructure == flipped,
                          $"got={reloaded.PreserveInputFolderStructure} expect={flipped}");
                    Check("N8c 重读(int) =37", reloaded.MaxQueueSize == 37, $"got={reloaded.MaxQueueSize}");

                    // 「重启」③：设置→控件（ctor :1333 同款入口）
                    typeof(FfmpegGui.MainWindow).GetMethod("LoadSettings", BF)!.Invoke(W, null);
                    Pump(10);
                    var cb = Find<TextBox>("ConcurrencyBox");
                    var pb = Find<CheckBox>("PreserveInputStructure");
                    Check("N8d 控件(并发)=37", cb?.Text == "37", $"got={cb?.Text ?? "<null>"}");
                    Check("N8e 控件(bool) 翻转在", pb != null && pb.IsChecked == flipped,
                          $"got={pb?.IsChecked.ToString() ?? "<null>"} expect={flipped}");
                }
                finally
                {
                    st.PreserveInputFolderStructure = origPreserve;
                    st.MaxQueueSize = origMaxQ;
                    FfmpegGui.Services.AppSettingsService.Save();
                    if (origBytes != null) System.IO.File.WriteAllBytes(settingsPath, origBytes);
                    else { try { System.IO.File.Delete(settingsPath); } catch { } }
                    typeof(FfmpegGui.Services.AppSettingsService)
                        .GetField("_current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                        .SetValue(null, null);
                    _ = FfmpegGui.Services.AppSettingsService.Current;   // 惰性重载回原值
                    typeof(FfmpegGui.MainWindow).GetMethod("LoadSettings", BF)?.Invoke(W, null);
                    if (logBox != null && logSnap != null) logBox.Text = logSnap;   // 还原日志面
                    Pump(4);
                }
            });

            // ── N9 四子窗口 headless 打开/关闭冒烟（2026-11-12 批 4·4，调研盲区#4）──
            //   调研证据：tests/projects 对四个子窗口 0 引用 ⇒ 打开即崩/控件丢失整段无判据。
            //   全部走**真实构造**（无参/轻参 ctor）→ Show → Pump → 控件在位 → Close；
            //   不碰 ShowFilterDialog/ShowDialog 模态路径（headless 下模态泵无人应答；构造路径与模态共享
            //   InitializeComponent ⇒ 构造级回归照样被抓）。ProgressWindow 的 500ms 定时器在 Closed 自停（:98）。
            Safe("N9a FormatFilterWindow 冒烟", () =>
            {
                var w = new FfmpegGui.FormatFilterWindow();
                try
                {
                    w.Show(); Pump(6);
                    var panel = w.FindControl<Avalonia.Controls.StackPanel>("FormatCheckPanel");
                    int cbCount = panel?.Children.Count ?? 0;
                    Check("N9a 打开+格式复选框建出", w.IsVisible && cbCount > 0,
                          $"visible={w.IsVisible} checkboxCount={cbCount}");
                }
                finally { w.Close(); }
            });
            Safe("N9b PresetManagerWindow 冒烟", () =>
            {
                var w = new FfmpegGui.PresetManagerWindow();
                try
                {
                    w.Show(); Pump(6);
                    var lb = w.FindControl<Avalonia.Controls.ListBox>("PresetListBox");
                    Check("N9b 打开+PresetListBox 在", w.IsVisible && lb != null,
                          $"visible={w.IsVisible} listbox={(lb != null ? "ok" : "missing")}");
                }
                finally { w.Close(); }
            });
            Safe("N9c ImageDetailWindow 冒烟", () =>
            {
                // 1×1 PNG 夹具（自造自清）；ctor 挂 Loaded→LoadMediaInfoAsync（catch-all，headless 安全）
                var img = System.IO.Path.Combine(TempDir, "n9_probe.png");
                System.IO.File.WriteAllBytes(img, new byte[] {
                    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                    0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                    0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                    0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
                    0x42, 0x60, 0x82 });
                var w = new FfmpegGui.ImageDetailWindow(img, null);
                try
                {
                    w.Show(); Pump(10);
                    var t = w.Title ?? "";
                    Check("N9c 打开+标题带文件名", w.IsVisible && t.Contains("n9_probe"),
                          $"visible={w.IsVisible} title={t}");
                }
                finally { w.Close(); try { System.IO.File.Delete(img); } catch { } }
            });
            Safe("N9d ProgressWindow 冒烟", () =>
            {
                // OutputPath 指向真实存在的 1×1 PNG（ctor 会把 FilePath 递给 MetadataEditorCtrl）
                var img = System.IO.Path.Combine(TempDir, "n9_probe_pw.png");
                System.IO.File.WriteAllBytes(img, new byte[] {
                    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                    0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                    0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                    0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
                    0x42, 0x60, 0x82 });
                var item = new FfmpegGui.Models.QueueItem
                { InputPath = img, OutputPath = img, Command = "-fake-args-for-smoke" };
                var w = new FfmpegGui.ProgressWindow(item, item.Command);
                try
                {
                    w.Show(); Pump(6);
                    var me = w.FindControl<FfmpegGui.Controls.MetadataEditor>("MetadataEditorCtrl");
                    Check("N9d 打开+MetadataEditorCtrl 在", w.IsVisible && me != null,
                          $"visible={w.IsVisible} metaEditor={(me != null ? "ok" : "missing")}");
                }
                finally { w.Close(); try { System.IO.File.Delete(img); } catch { } }
            });

            // ── N10 Batch D：矩阵预设的三态往返，以及依赖后端的候选回放 ──
            Safe("N10 输出矩阵预设三态与后端联动", () =>
            {
                var matrix = Find<ComboBox>("OutputMatrixCombo")!;
                var encoder = Find<ComboBox>("EncoderCombo")!;
                var mapMatrix = mwType.GetMethod("MapOutMatrixValue", BFS)!;
                var refreshEncoders = mwType.GetMethod("RefreshEncoderListAsync", BF)!;
                var genField = mwType.GetField("_formatRefreshGen", BF)!;
                var pendingField = mwType.GetField("_pendingEncoderPreset", BF)!;
                System.Threading.Tasks.Task RefreshEncoders() => (System.Threading.Tasks.Task)refreshEncoders.Invoke(W, null)!;
                bool FinishRefreshes(params System.Threading.Tasks.Task[] tasks)
                {
                    for (int spin = 0; spin < 300 && tasks.Any(t => !t.IsCompleted); spin++) Pump(1);
                    return tasks.All(t => t.IsCompletedSuccessfully);
                }
                string? MatrixToken(object? item) => mapMatrix.Invoke(null, new object?[] { item as string }) as string;
                string? SelectedMatrix() => MatrixToken(matrix.SelectedItem);
                int MatrixIndex(string token)
                {
                    for (int i = 0; i < matrix.ItemCount; i++)
                        if (MatrixToken(matrix.Items[i]) == token) return i;
                    return -1;
                }
                FfmpegGui.Models.PresetData SaveMatrixPreset() => FfmpegGui.Models.PresetData.FromJson(
                    ((FfmpegGui.Models.PresetData)build.Invoke(W, null)!).ToJson());
                void ApplyMatrixPreset(FfmpegGui.Models.PresetData preset)
                {
                    applyUi!.Invoke(W, new object?[] { preset });
                    Pump(12);
                }

                try
                {
                    SweepReset();
                    // PNG 下 rgb 与 auto 都可选，固定格式隔离出「明确清空」与「旧字段缺失」的差别。
                    matrix.SelectedIndex = 0; Pump(8);
                    var auto = SaveMatrixPreset();
                    matrix.SelectedIndex = MatrixIndex("rgb"); Pump(8);
                    var rgb = SaveMatrixPreset();
                    Check("N10a 真实保存→JSON→重读：明确 auto 与 rgb 仍是两个值",
                          auto.ColorOutMatrix == "auto" && rgb.ColorOutMatrix == "rgb",
                          $"auto={auto.ColorOutMatrix ?? "<null>"} rgb={rgb.ColorOutMatrix ?? "<null>"}");

                    var beforeAuto = SelectedMatrix();
                    ApplyMatrixPreset(auto);
                    Check("N10b 应用明确 auto 覆盖已选 rgb",
                          beforeAuto == "rgb" && matrix.SelectedIndex == 0,
                          $"before={beforeAuto} index={matrix.SelectedIndex} after={SelectedMatrix() ?? "<null>"}");

                    var beforeRgbIndex = matrix.SelectedIndex;
                    ApplyMatrixPreset(rgb);
                    Check("N10c 应用保存的 rgb 恢复已切为 auto 的控件",
                          beforeRgbIndex == 0 && SelectedMatrix() == "rgb",
                          $"beforeIndex={beforeRgbIndex} after={SelectedMatrix() ?? "<null>"}");

                    var legacy = FfmpegGui.Models.PresetData.FromJson("{\"format\":\"PNG\",\"quality\":85}");
                    matrix.SelectedIndex = MatrixIndex("rgb"); Pump(8);
                    var beforeLegacy = SelectedMatrix();
                    ApplyMatrixPreset(legacy);
                    Check("N10d 旧 JSON 缺少矩阵字段不覆盖已选 rgb",
                          legacy.ColorOutMatrix == null && beforeLegacy == "rgb" && SelectedMatrix() == "rgb",
                          $"legacy={legacy.ColorOutMatrix ?? "<null>"} before={beforeLegacy} after={SelectedMatrix() ?? "<null>"}");

                    // 同为 JPEG：xyb 只在 cjpegli 候选中。先用真实 GUI 保存 cjpegli+xyb，切到
                    // mjpeg 确认候选消失，再应用预设；若在恢复 backend 之前选矩阵，xyb 无从复原。
                    SetFormat("JPEG"); Pump(10);
                    var cjpegli = PickEncoder(s => s.Contains("cjpegli", StringComparison.OrdinalIgnoreCase));
                    var xybIndex = MatrixIndex("xyb");
                    matrix.SelectedIndex = xybIndex; Pump(8);
                    var xyb = SaveMatrixPreset();
                    var mjpeg = PickEncoder(s => s.StartsWith("mjpeg —", StringComparison.Ordinal));
                    bool onMjpegWithoutXyb = mjpeg != null && Equals(encoder.SelectedItem, mjpeg)
                        && MatrixIndex("xyb") < 0;
                    ApplyMatrixPreset(xyb);
                    bool restoredInOrder = cjpegli != null && Equals(encoder.SelectedItem, cjpegli) && SelectedMatrix() == "xyb";
                    var temporarilyIncompatible = FfmpegGui.Models.PresetData.FromJson(xyb.ToJson());
                    temporarilyIncompatible.EncoderName = "mjpeg";
                    temporarilyIncompatible.EncoderBackend = "Ffmpeg";
                    ApplyMatrixPreset(temporarilyIncompatible);
                    bool deferred = Equals(encoder.SelectedItem, mjpeg) && MatrixIndex("xyb") < 0 && SelectedMatrix() == null;
                    PickEncoder(s => s.Contains("cjpegli", StringComparison.OrdinalIgnoreCase)); Pump(8);
                    bool xybRestoredAfterBackendChange = cjpegli != null && Equals(encoder.SelectedItem, cjpegli)
                        && SelectedMatrix() == "xyb";

                    // 同代连发两次真实 encoder 刷新，最终后端与 xyb 均须保留；不是只测一次幂等 refill。
                    int sameGeneration = (int)genField.GetValue(W)!;
                    var refreshA = RefreshEncoders();
                    var refreshB = RefreshEncoders();
                    bool sameGenerationXybKept = FinishRefreshes(refreshA, refreshB)
                        && (int)genField.GetValue(W)! == sameGeneration
                        && Equals(encoder.SelectedItem, cjpegli) && SelectedMatrix() == "xyb";

                    // 待回放的旧预设不能覆盖用户随后改选的编码器；同代刷新仍须保留这次选择。
                    pendingField.SetValue(W, (sameGeneration, xyb));
                    PickEncoder(s => s.StartsWith("mjpeg —", StringComparison.Ordinal));
                    bool userEncoderCancelledPending = pendingField.GetValue(W) == null;
                    bool userEncoderKept = FinishRefreshes(RefreshEncoders()) && userEncoderCancelledPending
                        && Equals(encoder.SelectedItem, mjpeg) && SelectedMatrix() == null;

                    // 从真实不兼容预设产生 pending xyb。用户改选 601 后再切 cjpegli，不能复活旧 xyb。
                    ApplyMatrixPreset(temporarilyIncompatible);
                    matrix.SelectedIndex = MatrixIndex("smpte170m"); Pump(8);
                    bool userMatrixSelected = SelectedMatrix() == "smpte170m";
                    PickEncoder(s => s.Contains("cjpegli", StringComparison.OrdinalIgnoreCase)); Pump(8);
                    bool userMatrixKept = FinishRefreshes(RefreshEncoders()) && userMatrixSelected
                        && Equals(encoder.SelectedItem, cjpegli) && SelectedMatrix() == null;

                    // AVIF 同格式也必须先确定具体 encoder，再恢复它支持的色度和位深。
                    // 从真实 AOM 4:4:4/12-bit 快照切到仅 4:2:0/10-bit 的 SVT，防旧候选吞掉预设值。
                    SetFormat("AVIF"); Pump(10);
                    var aom = PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "libaom-av1");
                    var avifChroma = Find<ComboBox>("ChromaCombo")!;
                    var avifDepth = Find<ComboBox>("BitDepthCombo")!;
                    avifChroma.SelectedItem = "4:4:4";
                    avifDepth.SelectedItem = "12"; Pump(8);
                    var aomPreset = SaveMatrixPreset();
                    var svt = PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "libsvtav1");
                    bool svtCandidatesRestricted = svt != null
                        && !avifChroma.Items.Contains("4:4:4") && !avifDepth.Items.Contains("12");
                    ApplyMatrixPreset(aomPreset);
                    bool avifAxesRestored = aom != null && svtCandidatesRestricted
                        && aomPreset.Chroma == "4:4:4" && aomPreset.BitDepth == "12"
                        && Equals(encoder.SelectedItem, aom)
                        && Equals(avifChroma.SelectedItem, "4:4:4") && Equals(avifDepth.SelectedItem, "12");

                    // 旧预设只指定 Ffmpeg 后端时，不得把当前同后端的 SVT 强换成列表首项 AOM。
                    PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "libsvtav1");
                    var backendOnlyPreset = FfmpegGui.Models.PresetData.FromJson(aomPreset.ToJson());
                    backendOnlyPreset.EncoderName = null;
                    backendOnlyPreset.Chroma = "4:2:0";
                    backendOnlyPreset.BitDepth = "10";
                    ApplyMatrixPreset(backendOnlyPreset);
                    bool legacyBackendKept = svt != null && Equals(encoder.SelectedItem, svt);

                    // 内置带名 SVT 档必须明确指向 SVT；不能依赖当前选择碰巧就是 SVT。
                    PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "libaom-av1");
                    var builtinSvt = FfmpegGui.Services.PresetManagerService.GetPresetByName(
                        "🚀 AVIF SVT — 极致质量 (preset 2, CRF 18, 4:2:0 10-bit)");
                    if (builtinSvt != null) ApplyMatrixPreset(builtinSvt.Data);
                    bool builtinSvtSelected = builtinSvt != null
                        && FfmpegGui.Services.EncoderInfo.ParseEncoderName(builtinSvt.Data.EncoderName) == "libsvtav1"
                        && svt != null && Equals(encoder.SelectedItem, svt);

                    // 真正通过 SimplePresetCombo.SelectionChanged 走简洁预设入口：PNG 当前编码器
                    // → JPEG 预设指定 mjpeg，且质量/色域/位深/矩阵值都应在异步回放后仍成立。
                    var simpleCombo = Find<ComboBox>("SimplePresetCombo");
                    var simpleActiveField = mwType.GetField("_simpleActivePreset", BF)!;
                    var simpleSigField = mwType.GetField("_simpleAppliedSig", BF)!;
                    var simpleModeField = mwType.GetField("_simpleModeActive", BF)!;
                    var priorActivePreset = simpleActiveField.GetValue(W);
                    var priorSimpleSig = simpleSigField.GetValue(W);
                    bool priorSimpleMode = (bool)simpleModeField.GetValue(W)!;
                    int priorSimpleIndex = simpleCombo?.SelectedIndex ?? -1;
                    int priorSimpleItemCount = simpleCombo?.Items?.Count ?? 0;
                    bool simpleHandlerApplied = false;
                    bool qualityTextInputApplied = false;
                    bool formatChangeCancelledPending = false;
                    string simpleDebug = "not applied";
                    try
                    {
                        if (simpleCombo?.Items != null)
                        {
                            if (!priorSimpleMode) Click("SimpleModeEntryBtn");
                            SetFormat("PNG");
                            PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "png");
                            var simplePresetData = new FfmpegGui.Models.PresetData
                            {
                                ConversionMode = 0, Format = "JPEG", EncoderBackend = "Ffmpeg", EncoderName = "mjpeg",
                                Quality = 77, ColorSpace = "Display P3", BitDepth = "8", ColorOutMatrix = "smpte170m"
                            };
                            var simplePresetEntry = new FfmpegGui.Models.PresetEntry
                            { Name = "__ui_preset_refresh_probe__", Data = simplePresetData };
                            simpleCombo.Items.Add(simplePresetEntry);
                            simpleCombo.SelectedIndex = priorSimpleItemCount;
                            for (int spin = 0; spin < 300; spin++)
                            {
                                Pump(1);
                                if (ReferenceEquals(simpleActiveField.GetValue(W), simplePresetEntry)
                                    && string.Equals(Find<ComboBox>("FormatCombo")?.SelectedItem as string, "JPEG", StringComparison.OrdinalIgnoreCase)
                                    && FfmpegGui.Services.EncoderInfo.ParseEncoderName(encoder.SelectedItem as string) == "mjpeg"
                                    && pendingField.GetValue(W) == null) break;
                            }
                            var simpleFormatNow = Find<ComboBox>("FormatCombo")?.SelectedItem as string;
                            var simpleEncoderNow = FfmpegGui.Services.EncoderInfo.ParseEncoderName(encoder.SelectedItem as string);
                            var simpleQualityNow = Find<Slider>("QualitySlider")?.Value ?? -1;
                            var simpleSpaceNow = Find<ComboBox>("ColorSpaceCombo")?.SelectedItem as string;
                            var simpleDepthNow = Find<ComboBox>("BitDepthCombo")?.SelectedItem as string;
                            var simpleMatrixNow = SelectedMatrix();
                            simpleHandlerApplied = ReferenceEquals(simpleActiveField.GetValue(W), simplePresetEntry)
                                && string.Equals(simpleFormatNow, "JPEG", StringComparison.OrdinalIgnoreCase)
                                && simpleEncoderNow == "mjpeg"
                                && Math.Abs(simpleQualityNow - simplePresetData.Quality) < 0.5
                                && Equals(simpleSpaceNow, simplePresetData.ColorSpace)
                                && Equals(simpleDepthNow, simplePresetData.BitDepth)
                                && simpleMatrixNow == simplePresetData.ColorOutMatrix;
                            simpleDebug = $"simpleFormat={simpleFormatNow} encoder={simpleEncoderNow} quality={simpleQualityNow} space={simpleSpaceNow} depth={simpleDepthNow} matrix={simpleMatrixNow}";
                            // 显示刷新必须保留 77；用户主动输入 JPEG q:v=2 仍须更新为质量 100。
                            if (Find<TextBox>("QualityBox") is { } qualityInput)
                            {
                                qualityInput.Text = "2"; Pump(8);
                                qualityTextInputApplied = Find<Slider>("QualitySlider")?.Value == 100;
                            }

                            // 安排一条仍处于当前代的 pending preset，再真实切回 PNG；格式 handler 必须
                            // 递增 generation 并丢弃它，不允许晚到的 JPEG 预设回放污染新格式。
                            int oldGeneration = (int)genField.GetValue(W)!;
                            var interrupted = new FfmpegGui.Models.PresetData { Format = "JPEG", EncoderBackend = "Ffmpeg", EncoderName = "mjpeg" };
                            pendingField.SetValue(W, (oldGeneration, interrupted));
                            SetFormat("PNG");
                            formatChangeCancelledPending = pendingField.GetValue(W) == null
                                && (int)genField.GetValue(W)! > oldGeneration;
                        }
                    }
                    finally
                    {
                        if (!priorSimpleMode && (bool)simpleModeField.GetValue(W)!) Click("SimpleReturnBtn");
                        if (simpleCombo?.Items != null)
                            while (simpleCombo.Items.Count > priorSimpleItemCount)
                                simpleCombo.Items.RemoveAt(simpleCombo.Items.Count - 1);
                        if (simpleCombo?.Items != null && priorSimpleIndex >= 0 && priorSimpleIndex < simpleCombo.Items.Count)
                            simpleCombo.SelectedIndex = priorSimpleIndex;
                        simpleActiveField.SetValue(W, priorActivePreset);
                        simpleSigField.SetValue(W, priorSimpleSig);
                    }

                    Check("N10e 简洁预设 handler 跨 PNG→JPEG 回放 encoder/质量/色域/位深/矩阵；切格式取消 pending",
                          xyb.ColorOutMatrix == "xyb" && onMjpegWithoutXyb && restoredInOrder && deferred
                          && xybRestoredAfterBackendChange && sameGenerationXybKept && userEncoderKept && userMatrixKept
                          && avifAxesRestored && legacyBackendKept && builtinSvtSelected
                          && simpleHandlerApplied && qualityTextInputApplied && formatChangeCancelledPending,
                          $"saved={xyb.ColorOutMatrix ?? "<null>"} cjpegli={cjpegli} mjpeg={mjpeg} "
                          + $"onMjpegWithoutXyb={onMjpegWithoutXyb} restoredInOrder={restoredInOrder} deferred={deferred} "
                          + $"sameGenerationXybKept={sameGenerationXybKept} userEncoderKept={userEncoderKept} userMatrixKept={userMatrixKept} qualityTextInput={qualityTextInputApplied} "
                          + $"avifAxesRestored={avifAxesRestored} legacyBackendKept={legacyBackendKept} builtinSvtSelected={builtinSvtSelected} "
                          + $"liveEncoder={encoder.SelectedItem} liveMatrix={SelectedMatrix() ?? "<null>"} "
                          + $"simpleApplied={simpleHandlerApplied} formatCancelledPending={formatChangeCancelledPending} "
                          + $"quality={Find<Slider>("QualitySlider")?.Value} space={Find<ComboBox>("ColorSpaceCombo")?.SelectedItem} "
                          + $"depth={Find<ComboBox>("BitDepthCombo")?.SelectedItem} matrixBeforeCancel={SelectedMatrix() ?? "<null>"} {simpleDebug}");
                }
                finally { SweepReset(); }
            });

            // 硬件矩阵/预设组必须等待一次**自己发起且可追踪**的真实刷新完成。
            // 旧实现只 Pump(12)，可能读到上一格式的残留编码器列表；随后迟到的刷新会把注入项替换，
            // 让 QSV/提示断言变成随机红。直接等待现有 Task，不新增产品侧测试钩子或断言。
            void SettleEncoderRefresh()
            {
                var refresh = mwType.GetMethod("RefreshEncoderListAsync", BF)!;
                var task = (System.Threading.Tasks.Task)refresh.Invoke(W, null)!;
                for (int spin = 0; spin < 5000 && !task.IsCompleted; spin++)
                {
                    Pump(1);
                    System.Threading.Thread.Sleep(1);
                }
                if (!task.IsCompleted) throw new TimeoutException("RefreshEncoderListAsync did not settle within 5s");
                task.GetAwaiter().GetResult();
                Pump(12);
            }

            // ── N11/N12/N13：色彩矩阵 GUI 收口（G1/G2/G5/G8 + GUI→CLI 采集一致性）──
            Safe("N11 AVIF rgb矩阵与编码器/色度/位深联动", () =>
            {
                try
                {
                    SweepReset(); SetFormat("AVIF"); Pump(12); SettleEncoderRefresh();
                    var matrix = Find<ComboBox>("OutputMatrixCombo")!;
                    var chroma = Find<ComboBox>("ChromaCombo")!;
                    var depth = Find<ComboBox>("BitDepthCombo")!;
                    var mapMatrix = mwType.GetMethod("MapOutMatrixValue", BFS)!;
                    string? MatrixToken(object? item) => mapMatrix.Invoke(null, new object?[] { item as string }) as string;
                    string? SelectedMatrix() => MatrixToken(matrix.SelectedItem);
                    int MatrixIndex(string token)
                    {
                        for (int i = 0; i < matrix.ItemCount; i++)
                            if (MatrixToken(matrix.Items[i]) == token) return i;
                        return -1;
                    }
                    var aom = PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "libaom-av1");
                    int rgbIndex = MatrixIndex("rgb");
                    Check("N11a AOM 提供 rgb 输出矩阵", aom != null && rgbIndex >= 0,
                          $"aom={aom ?? "<none>"} rgbIndex={rgbIndex}");

                    depth.SelectedItem = "12";
                    chroma.SelectedItem = "4:4:4";
                    matrix.SelectedIndex = rgbIndex; Pump(8); Regen();
                    var rgbCommand = Cmd();
                    Check("N11b rgb锁色度为auto且置灰并说明原因",
                          SelectedMatrix() == "rgb" && Equals(chroma.SelectedItem, "auto") && !chroma.IsEnabled
                          && !string.IsNullOrWhiteSpace(Avalonia.Controls.ToolTip.GetTip(chroma) as string),
                          $"matrix={SelectedMatrix() ?? "<null>"} chroma={chroma.SelectedItem} enabled={chroma.IsEnabled} tip={Avalonia.Controls.ToolTip.GetTip(chroma)}");
                    Check("N11c rgb不锁位深且命令兑现gbrp12le",
                          depth.IsEnabled && Equals(depth.SelectedItem, "12") && rgbCommand.Contains("-pix_fmt gbrp12le")
                          && !rgbCommand.Contains("yuv420p12le"), Snip(rgbCommand));

                    matrix.SelectedIndex = 0; Pump(8); Regen();
                    Check("N11d 离开rgb后色度恢复可选且不误改其他选择",
                          chroma.IsEnabled && Equals(chroma.SelectedItem, "auto") && Equals(depth.SelectedItem, "12"),
                          $"chromaEnabled={chroma.IsEnabled} chroma={chroma.SelectedItem} depth={depth.SelectedItem}");

                    matrix.SelectedIndex = MatrixIndex("rgb"); Pump(6);
                    var svt = PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "libsvtav1");
                    Pump(8);
                    Check("N11e SVT收窄矩阵候选并回auto，rgb不再可选",
                          svt != null && MatrixIndex("rgb") < 0 && matrix.SelectedIndex == 0,
                          $"svt={svt ?? "<none>"} rgbIndex={MatrixIndex("rgb")} selected={matrix.SelectedIndex}");

                    // R6：GUI 候选集必须与 ImageEncoderArgs 同源。硬件未检测到时就注入一个显示项，
                    // 只测真实控件的过滤行为，绝不把这一项写成“硬件编码已验证”。
                    var encoderCombo = Find<ComboBox>("EncoderCombo")!;
                    var injected = new List<string>();
                    void SelectEncoderEvenIfUnverified(string name)
                    {
                        var found = PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == name);
                        if (found == null)
                        {
                            encoderCombo.Items.Add(name); injected.Add(name);
                            // SelectionChanged 及其候选重建都是同步链路；不要额外 Pump 让迟到的真实列表
                            // 刷新先删除这个“未验证注入项”，否则测到的是刷新后的默认编码器而非 name。
                            encoderCombo.SelectedItem = name;
                        }
                    }
                    bool HasValue(ComboBox combo, string value) => combo.Items.Cast<object?>().Any(x => Equals(x as string, value));
                    bool RgbMatchesSingleTruth(string encoder)
                    {
                        var expected = FfmpegGui.Services.ColorMapping.ColorMappingEngine.ResolveOutMatrixCapability(
                            "avif", "rgb", FfmpegGui.Services.EncoderBackend.Ffmpeg, encoder)
                            != FfmpegGui.Services.ColorMapping.OutMatrixCapability.Rejected;
                        return (MatrixIndex("rgb") >= 0) == expected;
                    }

                    SelectEncoderEvenIfUnverified("av1_nvenc");
                    Check("N11f NVENC候选与后端一致：rgb/444/422可用且12-bit不露出",
                          RgbMatchesSingleTruth("av1_nvenc") && HasValue(chroma, "4:4:4") && HasValue(chroma, "4:2:2")
                          && HasValue(depth, "10") && !HasValue(depth, "12"),
                          $"rgb={MatrixIndex("rgb")} chroma={string.Join(',', chroma.Items.Cast<object?>())} depth={string.Join(',', depth.Items.Cast<object?>())}");
                    SelectEncoderEvenIfUnverified("av1_qsv");
                    var qsvRgbAllowed = FfmpegGui.Services.ColorMapping.ColorMappingEngine.ResolveOutMatrixCapability(
                        "avif", "rgb", FfmpegGui.Services.EncoderBackend.Ffmpeg, "av1_qsv")
                        != FfmpegGui.Services.ColorMapping.OutMatrixCapability.Rejected;
                    Check("N11g QSV矩阵候选逐格等同同源函数，色度/位深收窄（未做实机编码宣称）",
                          RgbMatchesSingleTruth("av1_qsv") && !HasValue(chroma, "4:4:4") && HasValue(chroma, "4:2:0")
                          && HasValue(depth, "10") && !HasValue(depth, "12"),
                          $"encoder={FfmpegGui.Services.EncoderInfo.ParseEncoderName(encoderCombo.SelectedItem as string ?? "")} rgb={MatrixIndex("rgb")} expectedRgb={qsvRgbAllowed} chroma={string.Join(',', chroma.Items.Cast<object?>())} depth={string.Join(',', depth.Items.Cast<object?>())}");
                    SelectEncoderEvenIfUnverified("av1_amf");
                    Check("N11h AMF矩阵候选逐格等同同源函数，色度/位深收窄（未做实机编码宣称）",
                          RgbMatchesSingleTruth("av1_amf") && !HasValue(chroma, "4:4:4") && HasValue(chroma, "4:2:0")
                          && HasValue(depth, "10") && !HasValue(depth, "12"),
                          $"encoder={FfmpegGui.Services.EncoderInfo.ParseEncoderName(encoderCombo.SelectedItem as string ?? "")} rgb={MatrixIndex("rgb")} chroma={string.Join(',', chroma.Items.Cast<object?>())} depth={string.Join(',', depth.Items.Cast<object?>())}");
                    foreach (var item in injected) encoderCombo.Items.Remove(item);
                }
                finally { SweepReset(); }
            });

            Safe("N12 rgb预设二次回放与GUI到CLI采集一致", () =>
            {
                try
                {
                    SweepReset(); SetFormat("AVIF"); Pump(12); SettleEncoderRefresh();
                    var aom = PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "libaom-av1");
                    var matrix = Find<ComboBox>("OutputMatrixCombo")!;
                    var chroma = Find<ComboBox>("ChromaCombo")!;
                    var depth = Find<ComboBox>("BitDepthCombo")!;
                    var pendingField = mwType.GetField("_pendingEncoderPreset", BF)!;
                    var preset = FfmpegGui.Models.PresetData.FromJson(
                        ((FfmpegGui.Models.PresetData)build.Invoke(W, null)!).ToJson());
                    preset.Format = "AVIF";
                    preset.EncoderBackend = "Ffmpeg";
                    preset.EncoderName = "libaom-av1";
                    preset.ColorOutMatrix = "rgb";
                    preset.Chroma = "4:2:0";
                    preset.BitDepth = "12";
                    void ApplyRgbPreset()
                    {
                        applyUi!.Invoke(W, new object?[] { preset });
                        for (int spin = 0; spin < 300 && pendingField.GetValue(W) != null; spin++) Pump(1);
                        Pump(8); Regen();
                    }

                    matrix.SelectedIndex = 0;
                    ApplyRgbPreset();
                    Check("N12a rgb预设最终色度auto置灰、位深12、命令gbrp12le",
                          aom != null && Equals(chroma.SelectedItem, "auto") && !chroma.IsEnabled
                          && Equals(depth.SelectedItem, "12") && Cmd().Contains("-pix_fmt gbrp12le"),
                          $"aom={aom ?? "<none>"} chroma={chroma.SelectedItem} enabled={chroma.IsEnabled} depth={depth.SelectedItem} cmd={Snip(Cmd())}");

                    // 模拟旧回放留下的置灰但非 auto 状态；矩阵索引保持 rgb，第二次应用不能依赖 SelectionChanged。
                    chroma.SelectedItem = "4:2:0";
                    ApplyRgbPreset();
                    Check("N12b 同矩阵索引二次应用仍重新套锁",
                          Equals(chroma.SelectedItem, "auto") && !chroma.IsEnabled && Cmd().Contains("-pix_fmt gbrp12le"),
                          $"chroma={chroma.SelectedItem} enabled={chroma.IsEnabled} cmd={Snip(Cmd())}");

                    var collect = mwType.GetMethod("CollectOptionsFromUiAsync", BF)!;
                    var collectTask = (System.Threading.Tasks.Task<FfmpegGui.Models.FfmpegOptions>)collect
                        .Invoke(W, new object?[] { System.IO.Path.Combine(SrcDir, "src_8bit.png"), false })!;
                    for (int spin = 0; spin < 300 && !collectTask.IsCompleted; spin++) Pump(1);
                    FfmpegGui.Models.FfmpegOptions? collected = collectTask.IsCompletedSuccessfully ? collectTask.Result : null;
                    Check("N12c GUI采集与预设/CLI同源：encoder/rgb/auto/12完全一致",
                          collected != null && collected.Encoder == "libaom-av1" && collected.ColorOutMatrix == "rgb"
                          && collected.Chroma == "auto" && collected.BitDepth == 12,
                          $"completed={collectTask.Status} encoder={collected?.Encoder ?? "<null>"} matrix={collected?.ColorOutMatrix ?? "<null>"} chroma={collected?.Chroma ?? "<null>"} depth={collected?.BitDepth.ToString() ?? "<null>"}");

                    // R5：预设携带 NVENC 不支持的 12-bit 时，最终 GUI 必须停 auto 并点名位深；
                    // 不能只靠队列在入队后拒绝，也不能静默把 12 留在下拉/命令里。
                    // N12a-c 已走完整异步预设回放；这里直接调用同一终端回放方法并注入未验证 NVENC 项，
                    // 避免“本轮是否检测到硬件”改变被测路径。该项只作控件测试，不是硬件编码证据。
                    preset.EncoderName = "av1_nvenc";
                    preset.ColorOutMatrix = "bt709";
                    preset.Chroma = "4:2:0";
                    preset.BitDepth = "12";
                    var encoderCombo = Find<ComboBox>("EncoderCombo")!;
                    bool injectedNvenc = !encoderCombo.Items.OfType<string>().Any(s =>
                        FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "av1_nvenc");
                    if (injectedNvenc) encoderCombo.Items.Add("av1_nvenc");
                    mwType.GetMethod("ApplyPresetEncoderMatrix", BF)!.Invoke(W, new object?[] { preset });
                    Regen();
                    var resetHint = Find<TextBlock>("ColorOptionResetHint");
                    string depthLabel = FfmpegGui.Services.LocalizationService.Instance["bit.depth"];
                    Check("N12d NVENC 12-bit预设最终回auto并点名位深，GUI不把无效预设留到队列才失败",
                          Equals(FfmpegGui.Services.EncoderInfo.ParseEncoderName(Find<ComboBox>("EncoderCombo")?.SelectedItem as string ?? ""), "av1_nvenc")
                          && Equals(depth.SelectedItem, "auto") && resetHint != null && resetHint.IsVisible
                          && (resetHint.Text ?? "").Contains(depthLabel) && !Cmd().Contains("yuv420p12le") && !Cmd().Contains("gbrp12le"),
                          $"encoder={Find<ComboBox>("EncoderCombo")?.SelectedItem} depth={depth.SelectedItem} depthItems={string.Join(',', depth.Items.Cast<object?>())} pendingBitDepth={mwType.GetField("_bitDepthResetPending", BF)?.GetValue(W)} pendingPreset={pendingField.GetValue(W) != null} visible={resetHint?.IsVisible} text={resetHint?.Text ?? "<null>"} cmd={Snip(Cmd())}");
                    if (injectedNvenc) encoderCombo.Items.Remove("av1_nvenc");
                }
                finally { SweepReset(); }
            });

            Safe("N13 能力重置提示点名且合法保留时隐藏", () =>
            {
                try
                {
                    SweepReset(); SetFormat("AVIF"); Pump(12); SettleEncoderRefresh();
                    var chroma = Find<ComboBox>("ChromaCombo")!;
                    var depth = Find<ComboBox>("BitDepthCombo")!;
                    var colorSpace = Find<ComboBox>("ColorSpaceCombo")!;
                    var hint = Find<TextBlock>("ColorOptionResetHint");
                    var aom = PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "libaom-av1");
                    chroma.SelectedItem = "4:4:4"; depth.SelectedItem = "12";
                    var updateChroma = mwType.GetMethod("UpdateChromaBitDepthOptions", BF)!;
                    var updateArgs = updateChroma.GetParameters().Length == 0 ? Array.Empty<object?>() : new object?[] { false };
                    updateChroma.Invoke(W, updateArgs);
                    bool baselineHidden = hint != null && !hint.IsVisible;
                    var svt = PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "libsvtav1");
                    Pump(8);
                    string chromaLabel = FfmpegGui.Services.LocalizationService.Instance["chroma.subsampling"];
                    string depthLabel = FfmpegGui.Services.LocalizationService.Instance["bit.depth"];
                    Check("N13a 编码器收窄后重置色度/位深并同时点名",
                          aom != null && svt != null && baselineHidden
                          && Equals(chroma.SelectedItem, "auto") && Equals(depth.SelectedItem, "auto")
                          && hint != null && hint.IsVisible && (hint?.Text ?? "").Contains(chromaLabel) && (hint?.Text ?? "").Contains(depthLabel),
                          $"encoder={FfmpegGui.Services.EncoderInfo.ParseEncoderName(Find<ComboBox>("EncoderCombo")?.SelectedItem as string ?? "")} baselineHidden={baselineHidden} chroma={chroma.SelectedItem} depth={depth.SelectedItem} pendingChroma={mwType.GetField("_chromaResetPending", BF)?.GetValue(W)} pendingBitDepth={mwType.GetField("_bitDepthResetPending", BF)?.GetValue(W)} visible={hint?.IsVisible} text={hint?.Text ?? "<null>"}");

                    chroma.SelectedItem = "4:2:0"; depth.SelectedItem = "10";
                    PickEncoder(s => FfmpegGui.Services.EncoderInfo.ParseEncoderName(s) == "libaom-av1");
                    Pump(8);
                    Check("N13b 合法选择保留时提示消失",
                          Equals(chroma.SelectedItem, "4:2:0") && Equals(depth.SelectedItem, "10")
                          && hint != null && !hint.IsVisible,
                          $"chroma={chroma.SelectedItem} depth={depth.SelectedItem} visible={hint?.IsVisible} text={hint?.Text ?? "<null>"}");

                    SetFormat("AVIF"); Pump(10);
                    colorSpace.SelectedItem = "BT.2020 PQ"; Pump(6);
                    SetFormat("JPEG"); Pump(10);
                    string colorLabel = FfmpegGui.Services.LocalizationService.Instance["color.space.quick"];
                    Check("N13c 格式收窄HDR色彩空间后回auto并点名",
                          Equals(colorSpace.SelectedItem, "auto") && hint != null && hint.IsVisible
                          && (hint?.Text ?? "").Contains(colorLabel),
                          $"colorSpace={colorSpace.SelectedItem} visible={hint?.IsVisible} text={hint?.Text ?? "<null>"}");
                }
                finally { SweepReset(); }
            });

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
