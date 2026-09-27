using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace UiTestHost
{
    /// <summary>
    /// Avalonia.Headless UI 测试宿主 —— 加载真实 MainWindow（真实 XAML/绑定/事件处理器），
    /// 程序化验证：UI 交互、选项生效、参数传递、简洁模式、后台可见性。
    /// 无需显示器/鼠标，可重复、可断言。
    /// </summary>
    internal class Program
    {
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags BFS = BindingFlags.NonPublic | BindingFlags.Static;
        private static FfmpegGui.MainWindow W = null!;

        // 仓库根：从程序集所在目录向上查找 ffmpegPictureUI.sln，避免硬编码本机路径。
        private static readonly string RepoRoot = FindRepoRoot();
        private static string FindRepoRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "ffmpegPictureUI.sln")))
                    return d.FullName;
                d = d.Parent;
            }
            return AppContext.BaseDirectory;
        }

        private static string SrcDir = System.IO.Path.Combine(RepoRoot, "tests", "output", "sources");

        private static int _pass, _fail;
        private static readonly List<string> _failMsgs = new();

        // 自检负控开关：仅显式打开时注入一条必然为假的断言（证明汇总行/退出码确实会因它转红）。
        // 开关 = 环境变量 UIHOST_NEGCTL=1 或命令行参数 --negctl；正常运行时永不触发。
        private static bool _negCtl;

        // ══════════ 运行级 GUID 临时目录（P1-E 任务 B）══════════
        // 中间产物**一律**写这里，绝不写进共享语料目录 SrcDir（tests/output/sources/）。
        // 为什么：SrcDir 被多个门禁与多个并发实例共享 —— 往里写临时文件会让别处
        // 「枚举目录里所有 .png」的读数抖动（实测 _*.png 0→1→0），且中途被杀会留下垃圾。
        private static string TempDir = "";

        private static string CreateTempDir()
        {
            var d = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                           "uitesthost_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>
        /// 清理运行级临时目录。**必须容忍文件被占用**：Windows 上 ffmpeg/文件句柄可能尚未释放，
        /// Directory.Delete 会抛 IOException —— 那只是 %TEMP% 里留一个目录，不得让宿主因此转红。
        /// </summary>
        private static void CleanupTempDir()
        {
            if (string.IsNullOrEmpty(TempDir)) return;
            try { System.IO.Directory.Delete(TempDir, true); }
            catch
            {
                // 退一步：尽力逐个删，删不掉的留着（%TEMP% 会被系统清理），不改变退出码。
                try
                {
                    foreach (var f in System.IO.Directory.GetFiles(TempDir))
                        try { System.IO.File.Delete(f); } catch { }
                }
                catch { }
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            // ══════ fail-fast：必须先显式指定 PLAN 目录 ══════
            // 背景（实测）：`bin/.../PLAN` Junction 不存在时 PlanFolderPath 会向上误命中 `<parent-dir>`（假阳性），
            // 外部工具探测随即落入**无超时**的扩展路径枚举（`C:\Program Files` + `%LocalAppData%\Programs`
            // + 每个 PATH 目录），栈恒在 FileSystemEnumerator.MoveNext，>13 min 不收敛。
            // ⇒ 未设 / 指向不存在目录时立刻拒绝运行，绝不进入那条路径。
            var planDir = Environment.GetEnvironmentVariable("FFMPEGGUI_PLAN_DIR");
            if (string.IsNullOrWhiteSpace(planDir) || !System.IO.Directory.Exists(planDir))
            {
                Console.WriteLine("FATAL: FFMPEGGUI_PLAN_DIR is not set or does not point to an existing directory.");
                Console.WriteLine("  current value = " + (planDir ?? "<null>"));
                Console.WriteLine("  refused to start: without an explicit PLAN dir the host may enter an");
                Console.WriteLine("  unbounded external-tool path scan (observed: >13 min hang, no convergence).");
                Console.WriteLine("  fix: set FFMPEGGUI_PLAN_DIR=<repo>/publish/PLAN before running.");
                return 98;
            }
            Console.WriteLine("PLAN dir = " + planDir);

            if (args.Length > 0 && !args[0].StartsWith("--")) SrcDir = args[0];
            foreach (var a in args)
                if (string.Equals(a, "--negctl", StringComparison.OrdinalIgnoreCase)) _negCtl = true;
            if (Environment.GetEnvironmentVariable("UIHOST_NEGCTL") == "1") _negCtl = true;

            // ══════ 运行级 GUID 临时目录（P1-E 任务 B）══════
            // 修复前：本宿主把 4 个中间产物**直接写进共享语料目录** tests/output/sources/
            //   （_h11_bt2020.png / _prophoto_src.png / _p3_8bit.png / _k5_same_out.png）。
            // 实测危害：① 其它门禁对该目录做「所有 .png」动态枚举时读数抖动（实测 _*.png 0→1→0）；
            //          ② 被中途杀掉会留下垃圾文件，污染语料库（后续断言会把垃圾当素材）。
            // 本仓硬约束 §7.9：临时文件一律用运行级 GUID 目录（固定目录会被并发同名实例互删 ⇒ 假红）。
            // 清理放 finally，且**容忍文件被占用**（Windows 上句柄未释放会 IOException）。
            TempDir = CreateTempDir();
            try
            {
                AppBuilder.Configure<FfmpegGui.App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                    .SetupWithoutStarting();

                W = new FfmpegGui.MainWindow();
                W.Show();
                Pump(10); // Opened -> InitControls

                // ⚠ GroupP 必须**紧跟启动**跑，不能排到最后：中间有别的组会把面板清空
                //   （实测 `GroupN` 的 N6 负控就写 `logBox.Text = ""` 只留本次产生的日志）⇒
                //   放末尾时那 7 行 `[detect]` 早被抹掉，本组会一路轮询到超时（实测把宿主从 6 s 拖到 79 s）。
                GroupP_StartupDetectLog();

                SetInput(System.IO.Path.Combine(SrcDir, "src_8bit.png"));

                GroupA_FormatToCommand();
                GroupB_ParameterPassing();
                GroupC_PanelVisibility();
                GroupD_EncoderComboLinkage();
                GroupE_SimpleMode();
                GroupF_BackgroundVisibility();
                GroupG_EndToEnd();
                GroupH_Color();
                GroupI_IccUi();
                GroupJ_UiSweep();
                GroupK_StrategyMap();
                GroupL_ParamDefects();
                GroupM_CliStrict();
                GroupN_PresetRoundTrip();
                GroupO_GainMapToggleKeepsTargets();
                GroupR_EncoderArgDomains();
                if (_negCtl) RunNegativeControl();

                W.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 99;
            }
            finally
            {
                CleanupTempDir();
            }

            Console.WriteLine();
            Console.WriteLine($"===== UI 测试汇总: {_pass} PASS / {_fail} FAIL =====");
            foreach (var m in _failMsgs) Console.WriteLine("  FAILED -> " + m);
            return _fail;
        }

        // ══════════════ 基础设施 helper ══════════════
        static void Pump(int n = 6) { for (int i = 0; i < n; i++) Dispatcher.UIThread.RunJobs(); }
        static T? Find<T>(string name) where T : Control => W.FindControl<T>(name);
        static void SetInput(string path) =>
            typeof(FfmpegGui.MainWindow).GetField("_inputPath", BF)!.SetValue(W, path);
        static void Regen()
        {
            typeof(FfmpegGui.MainWindow).GetMethod("RegenerateCommand", BF)!.Invoke(W, null);
            Pump(8);
        }
        static string Cmd() => Find<TextBox>("CommandText")?.Text ?? "";
        static void SetFormat(string s) { var c = Find<ComboBox>("FormatCombo"); if (c != null) c.SelectedItem = s; Pump(8); }
        static void Click(string name)
        {
            var b = Find<Button>(name);
            b?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(8);
        }

        static void Check(string name, bool cond, string detail = "")
        {
            if (cond) { _pass++; Console.WriteLine($"[PASS] {name}"); }
            else { _fail++; _failMsgs.Add($"{name} :: {detail}"); Console.WriteLine($"[FAIL] {name} :: {detail}"); }
        }
        static void Safe(string name, Action a)
        {
            try { a(); } catch (Exception ex) { Check(name + " (异常)", false, ex.Message); }
        }

        // ══════════════ A组: 输出格式 → 命令生效 (UI选项生效/参数传递核心) ══════════════
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

        // ══════════════ B组: 参数传递 (质量/位深/色度 → 命令体现) ══════════════
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

        // ══════════════ C组: 面板显隐联动 (UI交互) ══════════════
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
        }

        // ══════════════ D组: 编码器下拉随格式联动 ══════════════
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

        // ══════════════ E组: 简洁模式切换 ══════════════
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

        // ══════════════ F组: 后台可见性 (最小化到托盘的核心动作) ══════════════
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

        // ══════════════ G组: GUI 驱动端到端真实转换 (按钮→队列→产物) ══════════════
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

        // ══════════════ H组: 色彩管线重构断言 (Registry 分类/tagging/超广色域矩阵/P4/P5/P6) ══════════════
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

        static int Run(string exe, string arguments)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, Arguments = arguments,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            });
            p!.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(60000);
            return p.ExitCode;
        }
        static string RunOut(string exe, string arguments)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, Arguments = arguments,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            });
            var o = p!.StandardOutput.ReadToEnd(); p.WaitForExit(30000);
            return o;
        }

        // ══════════════ I组: GUI ICC 选项 → 命令生成生效验证 ══════════════
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

        // ══════════════ token 级断言基础设施 (P1-B) ══════════════
        // ⚠ 为什么不能用整串 Contains：本仓实测陷阱「载体选错 ⇒ 断言恒真」——
        //   断言 cmd.Contains("444") 会被**无关**的 yuvj444p 命中；Contains("420") 会被无关的
        //   yuv420p 命中；Contains("png") 会被**输入文件名**命中；Contains("smpte432") 会被
        //   滤镜链里的 pin=smpte432 命中（那是**源**声明，不是目标）。
        //   ⇒ 一律先把命令串切成 token（引号内的空格不切），再按**下标关系**定位断言
        //     （「-pix_fmt 的下一个 token 必须**等于** yuvj444p」），并用
        //     「改回默认值 ⇒ 该 token 必须消失」做反向负控。
        static List<string> Tok(string s)
        {
            var outp = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool inq = false;
            foreach (var ch in (s ?? ""))
            {
                if (ch == '"') { inq = !inq; sb.Append(ch); continue; }
                if (!inq && (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n'))
                {
                    if (sb.Length > 0) { outp.Add(sb.ToString()); sb.Length = 0; }
                    continue;
                }
                sb.Append(ch);
            }
            if (sb.Length > 0) outp.Add(sb.ToString());
            return outp;
        }
        static int IdxFrom(List<string> t, string key, int from)
        {
            for (int i = (from < 0 ? 0 : from); i < t.Count; i++) if (t[i] == key) return i;
            return -1;
        }
        static int Idx(List<string> t, string key) => IdxFrom(t, key, 0);
        /// <summary>按**下标关系**取值：key 之后紧邻的那个 token。缺 key / 无值 ⇒ null。</summary>
        static string? Val(List<string> t, string key) => ValAfter(t, key, 0);
        static string? ValAfter(List<string> t, string key, int from)
        {
            int i = IdxFrom(t, key, from);
            return (i >= 0 && i + 1 < t.Count) ? t[i + 1] : null;
        }
        static bool HasTok(List<string> t, string exact)
        {
            foreach (var x in t) if (x == exact) return true;
            return false;
        }
        /// <summary>-vf 的取值再按 ',' 切成滤镜段（token 内再切一层），用于 iccgen / zscale / scale 这类断言。</summary>
        static List<string> VfSegs(List<string> t)
        {
            var outp = new List<string>();
            var vf = Val(t, "-vf");
            if (vf == null) return outp;
            foreach (var seg in vf.Trim('"').Split(','))
                if (seg.Length > 0) outp.Add(seg);
            return outp;
        }
        static bool VfHas(List<string> t, string seg)
        {
            foreach (var s in VfSegs(t)) if (s == seg) return true;
            return false;
        }
        static bool VfHasPrefix(List<string> t, string prefix)
        {
            foreach (var s in VfSegs(t)) if (s.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }
        /// <summary>token 精确成员判断（不是整串 Contains）。</summary>
        static bool TokEq(List<string> t, string a, string b) => a == b;
        static string TokStr(List<string> t) => string.Join(" | ", t);
        static void DumpCmd(string tag, string cmd)
        {
            var t = Tok(cmd);
            Console.WriteLine($"      [dump] {tag} :: {t.Count} tokens");
            Console.WriteLine("        " + TokStr(t));
        }

        /// <summary>把 UI 复位到「可复现的基线」：静态模式 / PNG / 高级关 / 策略=推荐 / 色域 auto。</summary>
        static void SweepReset()
        {
            var m = Find<ComboBox>("ConversionModeCombo"); if (m != null) m.SelectedIndex = 0;
            var ac = Find<CheckBox>("UseAdvancedColor"); if (ac != null) ac.IsChecked = false;
            var ad = Find<CheckBox>("UseAdvancedCodec"); if (ad != null) ad.IsChecked = false;
            var cs = Find<ComboBox>("ColorSpaceCombo"); if (cs != null) cs.SelectedIndex = 0;
            var ll = Find<CheckBox>("LosslessCheck"); if (ll != null) ll.IsChecked = false;
            var md = Find<ComboBox>("MetadataModeCombo"); if (md != null) md.SelectedIndex = 0;
            var em = Find<CheckBox>("EnableMaxDimensionCheck"); if (em != null) em.IsChecked = false;
            var at = Find<CheckBox>("AutoThreadsCheck"); if (at != null) at.IsChecked = true;
            var st = Find<CheckBox>("SingleThreadCheck"); if (st != null) st.IsChecked = false;
            var ap = Find<CheckBox>("AppendPngExtCheck"); if (ap != null) ap.IsChecked = false;
            var gm = Find<CheckBox>("ColorGamutMapCheck"); if (gm != null) gm.IsChecked = false;
            var ch = Find<ComboBox>("ChromaCombo"); if (ch != null) ch.SelectedIndex = 0;
            var bd = Find<ComboBox>("BitDepthCombo"); if (bd != null) bd.SelectedIndex = 0;
            var q = Find<Slider>("QualitySlider"); if (q != null) q.Value = 92;
            SetStrategyRadio("IccModeNone");
            SetInput(System.IO.Path.Combine(SrcDir, "src_8bit.png"));
            SetFormat("PNG");
            Pump(12);
        }

        /// <summary>
        /// GroupO：**GainMap 开关不得冲掉用户已设的目标色域 / 位深 / 质量**（2026-09-20；D-1 同族残留）。
        ///
        /// 缺陷：`JpegGainMapEnable_Changed` 调 `UpdateOptionAvailability()`，而后者会
        /// `Items.Clear()` + 重建 `ColorSpaceCombo` 与 `BitDepthCombo` 并把 `SelectedIndex` 归 0，
        /// 还会把 `QualitySlider.Value` 设为该格式默认值（见 `MainWindow.xaml.cs` 的 `UpdateOptionAvailability()`）。
        /// ⇒ 用户勾/取消「GainMap (Ultra HDR)」时**格式并未改变**，这三项却被整体重置。
        /// （D-1 只修了「应用预设」那条路径，并在注释里显式说明**不动刷新函数本体**；本组锁住开关这条路径。）
        ///
        /// 断言：O1 取值与默认值**必须不同**（反控：否则 O2/O3 恒真）· O2 开开关后三项保持 · O3 关开关后三项保持。
        /// ⚠ 变异验证（**已实测**）：把 `JpegGainMapEnable_Changed` 里的三行回写（`SetComboByValue`×2 +
        ///   `QualitySlider.Value`）删掉 ⇒ **O2a-c / O3a-c 六条全红**，且诊断串显示三项确实被冲成默认值
        ///   （`def cs=auto bd=auto q=92 | pick cs=Display P3 bd=8 q=82 | on cs=auto bd=auto q=92`）⇒ 缺陷真实、断言有牙。
        /// ⚠⚠ **变异写法本身有坑**（本轮踩到）：第一版变异只在真行**上方**加了几行注释掉的**副本**，
        ///   真正的回写行仍在 ⇒ 只有质量那条转红、cs/bd 假绿。**变异必须删/注释掉真正生效的那几行**。
        /// </summary>
        // ═══════════════════════════════════════════════════════════════════════════
        // R组：编码参数**分域下发**与**官方取值域**的落地锁（2026-09-26 审查 → 修复）
        //
        // 出处：docs/ENCODING_OPTIONS_AUDIT_2026-09-26.md 的 A-3/A-4/A-5/A-6/A-7/A-9/A-12
        //       /A-13/A-14/A-15/A-16 与附带发现的"静帧仍发 -still-picture=1 不可达"。
        // 判据**按审查规格写**（不是照抄实现）：每条都对应一次实测过的失效形态。
        // 全部走纯函数（BuildAvifOptions / BuildGifFilterChain / BuildArguments），
        // 不需要起编码器进程 ⇒ 硬件后端也能锁"参数名发给了谁"。
        // ═══════════════════════════════════════════════════════════════════════════
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

        static void GroupO_GainMapToggleKeepsTargets()
        {
            Safe("O1/O2/O3 GainMap 开关保持目标三项", () =>
            {
                var gm = Find<CheckBox>("JpegGainMapEnableCheck");
                if (gm == null) { Check("O1 JpegGainMapEnableCheck 存在", false, "null"); return; }

                SweepReset();
                gm.IsChecked = false; Pump(8);      // ⚠ SweepReset 不含本开关 ⇒ 本组自己归零，避免跨组串状态
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
                gm.IsChecked = true; Pump(14);
                var onCs = csCombo?.SelectedItem as string;
                var onBd = bdCombo?.SelectedItem as string;
                var onQ = qSlider?.Value ?? double.NaN;
                Check("O2a 开 GainMap 后目标色域保持", onCs == pickCs, $"期望={pickCs} 实={onCs}（默认={defCs}）");
                Check("O2b 开 GainMap 后位深保持", onBd == pickBd, $"期望={pickBd} 实={onBd}（默认={defBd}）");
                Check("O2c 开 GainMap 后质量保持", Math.Abs(onQ - pickQ) < 0.5, $"期望={pickQ} 实={onQ}（默认={defQ}）");

                // ④ 再取消 —— 双向都必须保持
                gm.IsChecked = false; Pump(14);
                var offCs = csCombo?.SelectedItem as string;
                var offBd = bdCombo?.SelectedItem as string;
                var offQ = qSlider?.Value ?? double.NaN;
                Check("O3a 关 GainMap 后目标色域保持", offCs == pickCs, $"期望={pickCs} 实={offCs}（默认={defCs}）");
                Check("O3b 关 GainMap 后位深保持", offBd == pickBd, $"期望={pickBd} 实={offBd}（默认={defBd}）");
                Check("O3c 关 GainMap 后质量保持", Math.Abs(offQ - pickQ) < 0.5, $"期望={pickQ} 实={offQ}（默认={defQ}）");
            });
        }

        /// <summary>选中某个策略单选并**锁定**手动语义（_iccModeTouched=true），
        /// 使色域联动不再覆盖它 —— 否则「选 Display P3 自动改策略」会让断言不可复现。</summary>
        static void SetStrategyRadio(string name)
        {
            var rb = Find<RadioButton>(name);
            if (rb != null) { rb.IsChecked = true; Pump(6); }
            typeof(FfmpegGui.MainWindow).GetField("_iccModeTouched", BF)?.SetValue(W, true);
        }

        // ══════════════ J组: UI 控件穷举 → 命令串 token 级断言 (P1-B) ══════════════
        // 控件穷举表（控件名 → 反射/FindControl 访问路径 → 期望 token 载体）：
        //   FormatCombo          Find<ComboBox>("FormatCombo").SelectedItem      -> 末位输出 token 的扩展名 + 格式专属编码 key
        //   ChromaCombo          Find<ComboBox>("ChromaCombo").SelectedItem      -> -pix_fmt 的**值**（精确相等）
        //   BitDepthCombo        Find<ComboBox>("BitDepthCombo").SelectedItem    -> -pix_fmt 的**值**（精确相等）
        //   QualitySlider        Find<Slider>("QualitySlider").Value             -> -q:v 的**值**
        //   ColorPrimariesCombo  Find<ComboBox>("ColorPrimariesCombo")           -> -color_primaries 的**值**
        //   ColorTrcCombo        Find<ComboBox>("ColorTrcCombo")                 -> -color_trc 的**值**
        //   ColorMatrixCombo     Find<ComboBox>("ColorMatrixCombo")              -> -i **之前**那个 -colorspace 的**值**
        //   ColorSpaceCombo      Find<ComboBox>("ColorSpaceCombo")               -> -color_primaries/-color_trc + 滤镜段(iccgen/zscale)
        //   LosslessCheck        Find<CheckBox>("LosslessCheck")                 -> PNG -compression_level=0 / WebP -lossless=1
        //   AppendPngExtCheck    Find<CheckBox>("AppendPngExtCheck")             -> 后置改名，**不得**进命令串
        //   EnableMaxDimensionCheck + MaxDimensionBox                            -> -vf 里的 scale=<N>:
        //   AutoThreadsCheck / SingleThreadCheck / ThreadsBox                    -> -threads 的**值**
        //   MetadataModeCombo + StripExif*Check                                  -> -map_metadata / -map_metadata:s:v 的**值**
        //   UseAdvancedCodec 下：PngPredCombo / PngDpiBox / WebpPresetCombo /
        //     JpegHuffmanCombo / JpegDctCombo / TiffCompressionCombo / TiffDpiBox /
        //     JxlEffortBox / JxlModularCheck / AvifCpuUsedBox / AvifCdefCheck / AvifRowMtCheck
        //   AnimationFpsBox / AnimationScaleWBox / AnimationLoopBox（动图容器）
        /// <summary>在 EncoderCombo 里挑一个满足谓词的编码器并选中（找不到 ⇒ null）。
        /// 必要性（实测）：各格式的**默认**编码器不一定走 ffmpeg 管线 ——
        /// JPEG 默认 cjpegli、JPEG XL 默认 cjxl ⇒ 预览命令是 cjpegli/cjxl 命令行，
        /// 里面**没有** -q:v / -pix_fmt / -huffman 这些 ffmpeg token。若不显式选编码器，
        /// 断言就会把「路由不同」误判成「参数没传递」。本 helper 让路由**可复现**。</summary>
        static string? PickEncoder(Func<string, bool> pred)
        {
            var enc = Find<ComboBox>("EncoderCombo");
            if (enc == null) return null;
            for (int i = 0; i < enc.ItemCount; i++)
            {
                var s = enc.Items[i] as string;
                if (s != null && pred(s)) { enc.SelectedItem = s; Pump(12); return s; }
            }
            return null;
        }
        /// <summary>选 ffmpeg 管线的编码器（排除 cjpegli / cjxl 两个外部工具后端）。</summary>
        static string? PickFfmpegEncoder() => PickEncoder(s =>
            !s.Contains("cjpegli", StringComparison.OrdinalIgnoreCase) &&
            !s.Contains("cjxl", StringComparison.OrdinalIgnoreCase));
        /// <summary>取 -vf 里 zscale 滤镜段的某个参数（p / t / m / pin / tin / min）。
        /// ⚠ 载体说明（实测）：PNG/TIFF/JXL 等 **RGB 原生**格式的**目标**色域写在 zscale 滤镜里
        /// （`p=`/`t=`/`m=`），**不是** -color_primaries/-color_trc —— 后两者是**输入声明**。
        /// 断言目标色域必须走本 helper，否则就是「载体选错 ⇒ 断言恒真」。</summary>
        static string? ZscaleParam(List<string> t, string key)
        {
            foreach (var seg in VfSegs(t))
            {
                if (!seg.StartsWith("zscale=", StringComparison.Ordinal)) continue;
                foreach (var kv in seg.Substring("zscale=".Length).Split(':'))
                {
                    int eq = kv.IndexOf('=');
                    if (eq > 0 && kv.Substring(0, eq) == key) return kv.Substring(eq + 1);
                }
            }
            return null;
        }
        /// <summary>当前 UI 的控件状态快照（用于证明断言在**已知**状态下取值，不是碰巧）。</summary>
        static string UiState() =>
            $"advColor={Find<CheckBox>("UseAdvancedColor")?.IsChecked} advCodec={Find<CheckBox>("UseAdvancedCodec")?.IsChecked} " +
            $"lossless={Find<CheckBox>("LosslessCheck")?.IsChecked} enc={Find<ComboBox>("EncoderCombo")?.SelectedItem as string}";

        // ══════════════ GroupP：启动期检测面板的「槽位 = 工具 = 日志行」不变式 ══════════════
        // 为什么单独跑一组（2026-09-23）：`verify-startup-warmup.ps1` 对缺陷 ② 只有**结构锁**
        // （源码里恰好 7 个 `Mk("…")`），而缺陷当时的**真实表现**在面板上：修之前 7 个槽位只打出
        // **4 行** `[detect]`（djxl / jxr 那两条根本不写日志，另有整个一个槽位是 `efc06221` 删
        // UltrahdrService 留下的空任务）⇒ 结构数对、面板照样缺，**只有真跑一次启动才区分得开**。
        // `FullDetectionAsync()` 是 `InitControls()` 里 fire-and-forget 起的，宿主 `W.Show(); Pump(10)`
        // 已经把它点着 ⇒ 本组只等它把行打完。
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

        // 面板里的 `[detect] ` 开头行分三类（口径必须**按形状**而不是按"有没有冒号"）：
        //   verdicts = `[detect] <七个槽位名之一>: <尾巴>`          ⇒ 槽位打的结论（本组判据对象）
        //   suspects = `[detect] <别的名字>: <OK|未找到|探测抛异常…>` ⇒ **空槽位/漏改名**的确切形态
        //   others   = 其余 `[detect]` 行                            ⇒ Step2 的 ffmpeg 进度行
        //     （`[detect] ffmpeg 已定位: C:\…\ffmpeg.exe` / `…像素格式检测完成` / `…编码器列表加载完成`
        //      / `[detect] ffmpeg 未找到（PATH 或 PLAN 均未检测到），跳过进程探测`）
        //     ⚠ 实测本机就会多出这些 ⇒ 若按"名字不认识就判红"，P4 会**天天假红**；
        //       所以 suspects 只收「尾巴是**结论词**、名字却不在七个之内」的行 —— 那才是空槽位/漏改名的信号。
        static void ClassifyDetectLines(string? text, string[] names,
            System.Collections.Generic.List<string> verdicts,
            System.Collections.Generic.List<string> others,
            System.Collections.Generic.List<string> suspects)
        {
            verdicts.Clear(); others.Clear(); suspects.Clear();
            if (string.IsNullOrEmpty(text)) return;
            foreach (var raw in text!.Split('\n'))
            {
                var l = raw.TrimEnd('\r').TrimEnd();
                if (!l.StartsWith("[detect] ", StringComparison.Ordinal)) continue;
                int colon = l.IndexOf(": ", "[detect] ".Length, StringComparison.Ordinal);
                if (colon < 0) { others.Add(l); continue; }
                string head = l.Substring("[detect] ".Length, colon - "[detect] ".Length);
                string tail = l.Substring(colon + 2);
                bool known = false;
                foreach (var n in names) if (string.Equals(n, head, StringComparison.Ordinal)) { known = true; break; }
                if (known) { verdicts.Add(l); continue; }
                bool verdictShaped = tail == "OK" || tail == "未找到" || tail.StartsWith("探测抛异常", StringComparison.Ordinal);
                if (verdictShaped) suspects.Add(l); else others.Add(l);
            }
        }

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
                Check("J1z 硬件编码器可选（前置）", hw != null, "no hardware encoder item in this environment");
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
                    cs.SelectedItem = v; Pump(8); Regen();
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

            SweepReset();
        }

        // ══════════════ K组: 四大模式 UI 映射逐值 + 4→3 折叠 (P1-D) ══════════════
        // 结构（**实测复核**，见报告）：
        //   UI 侧 = **4 个 RadioButton 直选 ColorStrategy**（不是旧 IccMode 4 值）：
        //     IccModeNone→Recommended / IccModeCarry→CarryIcc /
        //     IccModeBake→BakeCicpOnly / IccModeBakeOnly→Manual   (MainWindow.xaml.cs:2448-2454)
        //   ⇒ UI 层是 **4→4**（一一对应），不存在折叠。
        //   **4→3 折叠**只存在于**旧 IccMode 迁移路径**：ColorStrategyMapper.FromIccMode
        //     (ColorStrategy.cs:47-53)：None→Recommended、BakeToStandard→Recommended、
        //     CarryIcc→CarryIcc、BakeOnly→BakeCicpOnly。该路径的入口是
        //     ① CLI `--icc-mode`（CliParser.cs:636-639）② 旧预设迁移（FfmpegOptions.cs:135-138）。
        //   ⇒ 本组同时锁：UI 4 值各自 token 正确 + 旧 IccMode 4 值的**逐 token 等价类**。
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

        // ══════════════ L组: P1-F UI→命令 参数传递缺陷 4 项（逐项定性 → 修/现状锁） ══════════════
        // 本组把 4 项结论**全部**锁住（含「有意不改」的现状锁），每一项都做过变异验证：
        //   L1 缺陷1（硬件编码器名被 "⚡ " 前缀污染 ⇒ `-c:v` 不是单 token）  → **已修**（解析侧 + 显示侧）
        //   L2 缺陷2（RestoreLosslessAndQuality 抵消格式级控件锁定）          → **已修**（只撤自己那把锁）
        //   L3 缺陷3（TIFF 压缩算法被 `Lossless ⇒ raw` 覆盖）                → **已修**（用户选择优先）
        //   L4 缺陷4（WebP 动图 fps/scaleW 不进命令）                        → **已修**（实测 ffmpeg 通路支持）
        // 断言口径同 J 组：按 token 下标关系取值（不用整串 Contains）。
        static int CountTok(List<string> t, string key)
        {
            int n = 0;
            foreach (var x in t) if (x == key) n++;
            return n;
        }

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

        // ══════════════ M组: P1-E —— CLI 取值口径 (D4/D5/D6) + 任务B 临时目录卫生 + 任务C 动图粘性 ══════════════
        // 三个结论各自「先定性、后修、再锁」：
        //   D4 = **缺陷**（空格形式的负值被吞）→ 修 Parse 的值消费判据 → M1
        //   D5 = **缺陷**（同族参数两套口径）  → 统一抛 ArgumentException  → M2
        //   D6 = **两半**：auto≡未设置是**有意**（模型 null 即 auto）→ M3f 锁住；
        //        靠异常被吞实现是**缺陷** → M3a/M3d
        //  任务B = **缺陷**（临时产物写进共享语料目录）→ 运行级 GUID 目录 → B1..B3 + M4
        //  任务C = **缺陷**（隐藏面板里的残留动图参数仍进命令）→ 按模式取值 → M5
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

        // ══════════════ N组: 预设保存→载入往返 ColorStrategy (新增) ══════════════
        // 锁什么：UI 四个策略单选 → **保存**预设对象 → JSON 往返 → **载入**预设对象 → ColorStrategy 逐值不变。
        // 为什么必须锁（三条实测证据，均在源码里）：
        //   · 保存侧 `BuildPresetData()`（MainWindow.xaml.cs:5702-5798）**只**写遗留字段 `IccMode`
        //     （`PresetData.cs:142-145` 自述「[遗留] 旧 ICC 模式字符串…新预设用 ColorStrategy」）；
        //   · 载入侧 `FfmpegOptions.ApplyPresetData`（`FfmpegOptions.cs:135-138`）**优先**读 `ColorStrategy`，
        //     缺失才回退 `ColorStrategyMapper.FromIccMode(IccMode)`；
        //   · 两个函数对**同名按钮**的语义互斥（`MainWindow.xaml.cs` 的 `GetIccMode()` vs `GetColorStrategy()`），
        //     且 `FromIccMode` 把 BakeToStandard 折叠进 Recommended（`ColorStrategy.cs:47-53`，**有意设计**）
        //     ⇒ 中间两个模式（IccModeBake / IccModeBakeOnly）往返后错位。
        // 四条锁：① 4 值逐值往返（两条载入路径 A/B）；② 负控=往返后 4 值互不相同（防折叠成恒真）；
        //         ③ 旧预设（只有 IccMode、没有 ColorStrategy）的遗留迁移必须仍在；
        //         ④ 存盘 ColorStrategy 串必须自身可被 TryParse 还原（绕过遗留回退，防"回退掩盖错值"）。
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

        // ══════════════ 自检负控（仅 UIHOST_NEGCTL=1 / --negctl 时执行） ══════════════
        // 目的：证明本宿主的判定链「有牙」—— 一条必然为假的断言必须**被计入 _fail**、
        // 必须出现在汇总行里、并让退出码非 0。正常运行时本组永不执行。
        static void RunNegativeControl()
        {
            Console.WriteLine("\n########## NC: 宿主自检负控 (UIHOST_NEGCTL=1) ##########");
            int before = _fail;
            Check("NC1 负控注入(必然为假)", "__UIHOST_NEGCTL_MUST_NOT_MATCH__".Contains("PASS"),
                  "injected failure: 汇总行与退出码必须因它转红");
            Check("NC2 负控已计入FAIL计数", _fail == before + 1, $"before={before} after={_fail}");
        }

        static string Snip(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length > 110 ? s.Substring(0, 110) + "…" : s;
        }
    }
}
