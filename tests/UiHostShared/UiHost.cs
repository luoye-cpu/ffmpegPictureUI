using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace UiHostShared
{
    /// <summary>
    /// **UI 测试组的共享 GUI 底座**（2026-10-05 物理拆分时抽出的唯一一份）。
    ///
    /// <para><b>它拥有什么</b>：Avalonia 无头引导、真实 <see cref="FfmpegGui.MainWindow"/> 窗口
    /// <see cref="W"/>、运行级临时目录 <see cref="TempDir"/>、可观测性/失败明细管道
    /// （<see cref="FailMsgs"/>）、以及 UiTestHost 里那**全部** GUI 助手与纯 token 助手族。</para>
    ///
    /// <para><b>为什么必须是同一份</b>：这 17 个组**共用一个活窗口** `W`：它们靠点控件、读
    /// `CommandText`、反射调 private 处理器来断言。拆分前这些助手是 `UiTestHost/Program.cs` 里
    /// 的一批 `static` 方法；若 5 个新工程各复制一份，就会出现 5 份同名不同实现的
    /// `Cmd()`/`Pump()`/`SweepReset()` —— 本仓已有「两处独立常数漂移 2.7e-4」的前车之鉴
    /// ⇒ **只此一份**。</para>
    ///
    /// <para><b>与 <c>Tests.Shared.Harness</c> 的分工</b>：本类**不重复**计数与汇总格式 ——
    /// 那些在 <see cref="Tests.Shared.Harness"/>（含 <c>PROBE RESULT: pass=n fail=m</c> 汇总行）。
    /// 但 UI 宿主的历史输出形状与 ServiceProbe 不同：它逐行打 <c>[PASS] name</c> /
    /// <c>[FAIL] name :: detail</c>（带方括号 + 明细），并自打汇总行
    /// <c>===== UI 测试汇总: N PASS / M FAIL =====</c>。既有 5 条 UI 门禁脚本
    /// （<c>verify-ui-host</c> / <c>ui-param-matrix</c> / <c>ui-strategy-map</c> /
    /// <c>ui-param-defects</c> / <c>cli-strict</c>）逐字 grep 这些形状
    /// （<c>\[regex\]::Match($text, '(\d+) PASS / (\d+) FAIL')</c>）
    /// ⇒ 本类**逐字保留**该形状，只是把计数搬进 <see cref="Harness"/> 单一真值
    /// （这样 <c>PROBE RESULT</c> 与 UI 汇总行读的是**同一个**计数）。</para>
    ///
    /// <para>⚠ 断言文本一字未改（门禁在 grep 它们，且 <c>verify-ui-param-defects.ps1:198</c>
    /// 逐锚点要求在册）。</para>
    /// </summary>
    public static class UiHost
    {
        /// <summary>private **实例**成员（反射调 MainWindow 的 private 方法/字段）。</summary>
        public const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
        /// <summary>private **静态**成员（反射注入 ExifToolService 的静态检测态）。</summary>
        public const BindingFlags BFS = BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>**被测的真实窗口**。分组断言全部通过它点控件 / 读状态。</summary>
        public static FfmpegGui.MainWindow W = null!;

        /// <summary>仓库根：从程序集所在目录向上查找 <c>ffmpegPictureUI.sln</c>，避免硬编码本机路径。</summary>
        public static readonly string RepoRoot = FindRepoRoot();

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

        /// <summary>语料/素材目录（默认 <c>tests/output/sources</c>；可由命令行首参覆盖）。</summary>
        public static string SrcDir { get; set; } =
            System.IO.Path.Combine(RepoRoot, "tests", "output", "sources");

        /// <summary>
        /// 失败明细（供宿主尾部逐条 <c>FAILED -&gt; …</c> 打印）。
        /// ⚠ 这是**可观测性**而不是计数：计数只有 <see cref="Tests.Shared.Harness"/> 一份。
        /// </summary>
        public static readonly List<string> FailMsgs = new();

        // ══════════ 运行级 GUID 临时目录（P1-E 任务 B）══════════
        // 中间产物**一律**写这里，绝不写进共享语料目录 SrcDir（tests/output/sources/）。
        // 为什么：SrcDir 被多个门禁与多个并发实例共享 —— 往里写临时文件会让别处
        // 「枚举目录里所有 .png」的读数抖动（实测 _*.png 0→1→0），且中途被杀会留下垃圾。
        /// <summary>本次运行的 GUID 临时目录（由 <see cref="Bootstrap"/> 建立，退出时清理）。</summary>
        public static string TempDir { get; private set; } = "";

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
        /// （逐字保留旧宿主语义。）
        /// </summary>
        public static void CleanupTempDir()
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

        // ══════════════════ 启动 / 引导 ══════════════════

        /// <summary>
        /// fail-fast：必须先显式指定 PLAN 目录。
        /// <para>背景（实测）：<c>bin/.../PLAN</c> Junction 不存在时 PlanFolderPath 会向上误命中
        /// <c>&lt;parent-dir&gt;</c>（假阳性），外部工具探测随即落入**无超时**的扩展路径枚举
        /// （<c>C:\Program Files</c> + <c>%LocalAppData%\Programs</c> + 每个 PATH 目录），
        /// 栈恒在 FileSystemEnumerator.MoveNext，&gt;13 min 不收敛。
        /// ⇒ 未设 / 指向不存在目录时立刻拒绝运行，绝不进入那条路径。</para>
        /// </summary>
        /// <returns>true = 可以继续；false = 已打印 FATAL（调用方应返回 98）。</returns>
        public static bool CheckPlanDir()
        {
            var planDir = Environment.GetEnvironmentVariable("FFMPEGGUI_PLAN_DIR");
            if (string.IsNullOrWhiteSpace(planDir) || !System.IO.Directory.Exists(planDir))
            {
                Console.WriteLine("FATAL: FFMPEGGUI_PLAN_DIR is not set or does not point to an existing directory.");
                Console.WriteLine("  current value = " + (planDir ?? "<null>"));
                Console.WriteLine("  refused to start: without an explicit PLAN dir the host may enter an");
                Console.WriteLine("  unbounded external-tool path scan (observed: >13 min hang, no convergence).");
                Console.WriteLine("  fix: set FFMPEGGUI_PLAN_DIR=<repo>/publish/PLAN before running.");
                return false;
            }
            Console.WriteLine("PLAN dir = " + planDir);
            return true;
        }

        /// <summary>
        /// 解析组 exe 的命令行（5 个 UI 组工程**共用同一口径**，避免各自漂移）。
        ///
        /// <para><b>接受的形式</b>：</para>
        /// <list type="bullet">
        /// <item><description><c>--list</c> / <c>-l</c> ⇒ <see cref="UiArgsKind.List"/></description></item>
        /// <item><description>空 ⇒ <see cref="UiArgsKind.All"/>（跑全组）</description></item>
        /// <item><description><c>&lt;mode&gt;</c> ⇒ <see cref="UiArgsKind.Mode"/></description></item>
        /// <item><description><c>&lt;素材目录&gt; [mode]</c> ⇒ 目录**可选**，为兼容旧宿主
        /// （其首参就是 SrcDir，见 <c>verify-ui-host.ps1:154</c> 传 <c>$srcDir</c>）
        /// 与既有人工习惯。</description></item>
        /// <item><description><c>&lt;mode&gt; &lt;素材目录&gt;</c> ⇒ 两者顺序也可颠倒（同样为兼容）。</description></item>
        /// </list>
        ///
        /// <para><b>fail-closed</b>：既不是已知 mode、也不是已存在的目录 ⇒ 返回
        /// <see cref="UiArgsKind.Unknown"/>（调用方 exit 2）。**绝不静默跑全组** ——
        /// 本仓忌"静默降级"：把拼错的 mode 当成"跑全组"会让该 mode **永远不被跑到**
        /// 而看板全绿（= 假覆盖）。</para>
        /// </summary>
        public static UiArgs ParseArgs(string[] args, string[] knownModes)
        {
            var r = new UiArgs { Kind = UiArgsKind.All, Mode = "", SrcDirOverride = "", Unknown = "" };
            foreach (var a in args)
            {
                if (string.Equals(a, "--list", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-l", StringComparison.OrdinalIgnoreCase))
                { r.Kind = UiArgsKind.List; return r; }

                // 素材目录：**显式存在**即认（不因为以 '-' 开头就排除，路径可能以它开头）
                if (System.IO.Directory.Exists(a)) { r.SrcDirOverride = a; continue; }

                var low = a.ToLowerInvariant();
                if (Array.IndexOf(knownModes, low) >= 0)
                {
                    if (r.Mode != "")
                    { r.Kind = UiArgsKind.Unknown; r.Unknown = a; return r; }   // 两个 mode ⇒ 拒绝
                    r.Mode = low;
                    continue;
                }

                // 既不是目录、也不是已知 mode ⇒ 响亮拒绝
                r.Kind = UiArgsKind.Unknown; r.Unknown = a; return r;
            }
            r.Kind = r.Mode == "" ? UiArgsKind.All : UiArgsKind.Mode;
            return r;
        }

        /// <summary>命令行解析结果的类别。</summary>
        public enum UiArgsKind
        {
            /// <summary>跑全组。</summary>
            All,
            /// <summary>跑指定 mode。</summary>
            Mode,
            /// <summary>列出本组 mode。</summary>
            List,
            /// <summary>无法识别 ⇒ 调用方 exit 2（fail-closed）。</summary>
            Unknown,
        }

        /// <summary>命令行解析结果。</summary>
        public struct UiArgs
        {
            /// <summary>类别。</summary>
            public UiArgsKind Kind;
            /// <summary>要跑的 mode（小写；<see cref="UiArgsKind.All"/> 时为空）。</summary>
            public string Mode;
            /// <summary>素材目录覆盖（空串 = 用默认 <see cref="SrcDir"/>）。</summary>
            public string SrcDirOverride;
            /// <summary>无法识别的那个参数（用于报错点名）。</summary>
            public string Unknown;
        }

        /// <summary>把 <see cref="SrcDir"/> 覆盖到指定目录（空/不存在则忽略）。</summary>
        public static void ApplySrcDirOverride(string dir)
        {
            if (!string.IsNullOrWhiteSpace(dir) && System.IO.Directory.Exists(dir)) SrcDir = dir;
        }

        /// <summary>
        /// 起 Avalonia 无头平台 + 建真实 <see cref="W"/> 并 <c>Show()</c> + <c>Pump(10)</c>
        /// （Opened -&gt; InitControls）。
        /// <para>⚠ 这是**全部 5 个组工程唯一**的窗口建立路径 —— 保证「同一个窗口、同一批
        /// 启动副作用（含 InitControls 里 fire-and-forget 的工具探测）」。</para>
        /// </summary>
        public static void BootWindow()
        {
            AppBuilder.Configure<FfmpegGui.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                .SetupWithoutStarting();

            W = new FfmpegGui.MainWindow();
            W.Show();
            Pump(10); // Opened -> InitControls
        }

        /// <summary>
        /// 一次完整引导：按需覆盖素材目录、建运行级 TempDir、起窗口、**落实外部工具路径**。
        /// <para><b>必须</b>在建窗口前建 TempDir（组内断言会往它写中间产物）。</para>
        /// <para>⚠ <paramref name="srcDirOverride"/> 空则沿用默认 <see cref="SrcDir"/>
        /// （**不是**旧宿主那种"首参即 SrcDir"的裸取值 —— 那样会把 mode 名误当目录，
        /// 实测踩到：`Tests.UiCore.exe &lt;sources&gt; ui-encoder-linkage` 报"不认识 mode"）。</para>
        /// </summary>
        public static void Bootstrap(string srcDirOverride = "")
        {
            ApplySrcDirOverride(srcDirOverride);
            TempDir = CreateTempDir();
            // ⚠⚠ 顺序**必须**是：先落实工具路径 ⇒ 再建窗口。
            //   为什么（实测）：`InitControls()` → `FullDetectionAsync()` 是 **fire-and-forget**，
            //   而它的 **Step 2**（ffmpeg 像素格式 + **编码器列表**）有一段前置
            //   `if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))`
            //   （`MainWindow.xaml.cs:1248-1249`）。若 `FfmpegPath` 此刻为空，
            //   Step 2 **整段跳过**（只打一行 `[detect] ffmpeg 未找到…`）⇒ 编码器列表永不加载
            //   ⇒ `EncoderCombo` 停在占位项、`GetCurrentEncoderBackend()` 结果不稳
            //   ⇒ `N5b`（`-threads`/策略载体）与 `L1e-0`（硬件项）间歇转红。
            //   实测：把 ApplyToolPaths() 放在 BootWindow() **之后**时，日志里
            //   `编码器列表加载完成` 一次都不出现，N5b 必红；放在**之前**才会走到 Step 2。
            ApplyToolPaths();
            BootWindow();
        }

        /// <summary>
        /// **落实外部工具路径与探测**（拆出旧宿主 `GroupG_EndToEnd` 里那段隐式前置，
        /// 见 <c>UiTestHost/Program.cs:781-784</c>）。
        ///
        /// <para><b>为什么必须提到共享引导里</b>：旧宿主只在 <c>GroupG</c> 里做过这三件事
        /// （<c>st.FfmpegDirectory</c> / <c>st.OutputDirectory</c> /
        /// <c>ExternalToolsDetector.EnsureAllDetected()</c>），而 G 排在 <c>Main</c> 固定顺序的
        /// 第 7 位 ⇒ **后面所有需要外部编码器的组都在"顺带继承"G 的副作用**。
        /// 拆成一个 mode 一个进程后没有这个祖先，实测：
        /// <list type="bullet">
        /// <item><description><c>L1e-0 本环境存在硬件编码器项</c>（ui-param-defects）
        /// 间歇转红：<c>EncoderCombo</c> 里没有 <c>⚡</c> 开头的项 ⇒ 连带的
        /// <c>L1f/L1g/L1h/L1i/L1j/L1k</c> 六条**整段消失**（不是判红，是根本没跑到）。
        /// 实测单跑 3 次红 1 次。</description></item>
        /// <item><description>整个 <c>ui-color</c> 可能**一条断言都不出**（输出变成
        /// <c>cjpegli.exe</c> 的 Usage 转储）—— 因为编码器路由退化。</description></item>
        /// </list>
        /// ⇒ 处理方式（**不改断言、不放宽判据**）：把旧宿主那两行设置 + 一次同步探测
        /// **原样**搬进共享引导，使每个 mode 都在"工具已落实"的同一稳态下起跑 ——
        /// 这正是旧宿主里 G 建立的同一状态，只是从"顺带继承"变成"显式契约"。</para>
        ///
        /// <para>⚠ 不是"让各组依赖 G 先跑"：那会把被移除的顺序运气重新引回来。
        /// 本方法是**共享**的（UiHostShared 唯一一份），不是各组各抄一遍。</para>
        /// </summary>
        public static void ApplyToolPaths()
        {
            var plan = Environment.GetEnvironmentVariable("FFMPEGGUI_PLAN_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(plan)) return;
            var outDir = TempDir;
            try { if (!System.IO.Directory.Exists(outDir)) System.IO.Directory.CreateDirectory(outDir); } catch { }
            var st = FfmpegGui.Services.AppSettingsService.Current;
            st.FfmpegDirectory = System.IO.Path.Combine(plan, "ffmpeg-full");
            st.OutputDirectory = outDir;
            // ⚠⚠ 2026-10-05：**必须同时把 `JxlLibDir` 从仓库根推导出来**（与 `Harness.InitExternalTools`
            //   同一收口，见那里的长注释）。动机是一次实测的**静默换编码器**：
            //   `CjpegliService`/`CjxlService` 靠 `JxlLibDir` 定位 `cjpegli.exe`/`cjxl.exe`，
            //   而该字段**不**从 `FfmpegDirectory` 推导、也不由 `EnsureAllDetected()` 设置，
            //   只来自被 `.gitignore` 忽略的便携 `settings.json`（装的是本机绝对路径）。
            //   ⇒ 缺它时**不会红**，而是**静默退回 ffmpeg 的 mjpeg**（产物带 `Lavc63.14.100` COM 段），
            //     汇总数字一模一样，只有断言里的实测值（2694B → 2553B）会暴露 —— 典型的"绿在错误后端上"。
            //   ⚠ 只在文件确实存在、且当前值**无效**时写入（不覆盖用户/便携配置的有效值）。
            var jxlBin = System.IO.Path.Combine(plan, "jxl", "bin");
            if (System.IO.File.Exists(System.IO.Path.Combine(jxlBin, "cjpegli.exe"))
                || System.IO.File.Exists(System.IO.Path.Combine(jxlBin, "cjxl.exe")))
            {
                if (string.IsNullOrWhiteSpace(st.JxlLibDir) || !System.IO.Directory.Exists(st.JxlLibDir))
                    st.JxlLibDir = jxlBin;
            }
            var artifacts = System.IO.Path.Combine(plan, "artifacts");
            if (System.IO.Directory.Exists(artifacts) && string.IsNullOrWhiteSpace(st.WindowsArtifactsDir))
                st.WindowsArtifactsDir = artifacts;
            try { FfmpegGui.Services.ExternalToolsDetector.EnsureAllDetected(); } catch { }
        }

        /// <summary>
        /// **把「输出格式 ⇒ 编码器下拉」这条联动走完并落到稳态**（消除 `EncoderCombo` 里
        /// **上一个格式的残留选中项**）。
        ///
        /// <para><b>缺陷链（2026-10-05 实测定位；属旧宿主既有的顺序耦合，非拆分引入）</b>：
        /// <list type="number">
        /// <item><description>启动时 <c>FormatCombo = StillFormats[0] = "JPEG"</c>（:536），
        /// 编码器下拉随之选中 <c>cjpegli …</c>（JPEG 默认，见 <c>GetDefaultEncoder</c>）。</description></item>
        /// <item><description><c>SetFormat("PNG")</c> 触发 <c>FormatCombo_SelectionChanged</c> ⇒
        /// <c>UpdateFormatCapabilities()</c>（末尾 :2272 调 <c>UpdateThreadAvailabilityForFormat</c>）
        /// **以及** fire-and-forget 的 <c>RefreshEncoderListAsync()</c>（:2051，**async**）。</description></item>
        /// <item><description>若此刻异步刷新**还没落地**，<c>EncoderCombo.SelectedItem</c> 仍是
        /// **JPEG 时代的 `cjpegli`** ⇒ <c>GetCurrentEncoderBackend()</c>（:2111）返回 <c>Cjpegli</c>
        /// ⇒ <c>UpdateThreadAvailabilityForFormat</c> 把 <c>SingleThreadCheck.IsChecked = true</c>
        /// 且 <c>AutoThreadsCheck = false</c>（:2343-2358）。</description></item>
        /// <item><description>刷新稍后落地，把下拉换成 PNG 的 <c>png</c> —— 但 **<c>else</c> 分支只恢复
        /// <c>IsEnabled</c>、不复位 <c>IsChecked</c>**（:2367-2372）⇒ <c>SingleThreadCheck</c> **留在 true**
        /// ⇒ 命令恒为 <c>-threads 1</c>（:1425）。</description></item>
        /// </list>
        /// <c>GroupN</c> 的 <c>N5b</c> 把「载入预设后的命令」与「同场景直接选中的基线命令」**逐字节**比较，
        /// 而两条路径对本开关的写入不同步 —— 基线路径调 <c>UpdateOptionAvailability()</c>
        /// （会踩上面这条链），往返路径调 <c>ApplyPresetData()</c>（把预设里的
        /// <c>p.SingleThread</c> 原样恢复，:5944）⇒ 一个 <c>-threads 1</c>、一个 <c>-threads 16</c>。
        /// 实测单跑 <c>ui-preset-roundtrip</c> 10 次红 8 次。</para>
        ///
        /// <para><b>做法</b>：把格式**离开再切回**（保证 <c>SetFormat</c> 是一次真实变更，
        /// 联动路径才会跑），并 <c>Pump</c> 到编码器下拉不再含上一个格式的残留项为止。
        /// ⚠ 只动"格式联动是否走完"这一件事，**不碰**任何被断言的控件取值。</para>
        /// </summary>
        /// <param name="fmt">目标格式（通常是 "PNG"）。</param>
        public static void SettleFormatLinkage(string fmt = "PNG")
        {
            var enc = Find<ComboBox>("EncoderCombo");
            var fc = Find<ComboBox>("FormatCombo");
            if (enc == null || fc == null) return;
            var other = "JPEG XL";
            if (string.Equals(fc.SelectedItem as string, other, StringComparison.Ordinal)) other = "TIFF";
            SetFormat(other);
            SetFormat(fmt);
            // Pump 到编码器列表换成**目标格式**的项
            // （RefreshEncoderListAsync 的续体靠 UI 队列推进，故必须 Pump 而不是 Sleep）。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 20.0)
            {
                var sel = enc.SelectedItem as string ?? "";
                bool stale = sel.Contains("cjpegli", StringComparison.OrdinalIgnoreCase)
                          || sel.Contains("cjxl", StringComparison.OrdinalIgnoreCase);
                if (enc.ItemCount > 0 && !stale) break;
                Pump(10);
            }
            // ⚠⚠ 必须再**多 Pump 一段**：本方法做了**两次** SetFormat，而
            //   `FormatCombo_SelectionChanged` 每次都 fire-and-forget
            //   `RefreshEncoderListAsync()`（:2051）+ `UpdateFormatCapabilities()`。
            //   第一次那次 SetFormat 的异步续体可能在第二次之后才落地 ⇒ 它会按**旧格式**
            //   重建 `ColorSpaceCombo`（`Items.Clear()` + `SelectedIndex=0`，:2224/:2245）
            //   ⇒ 把调用方随后设的色域选择**抹掉**。实测：K1 报 `colorSpace=auto`
            //   （`SetStrategyRadio` 之后设的 "Display P3" 被抹）。多 Pump 让所有待处理续体跑完。
            Pump(40);
        }

        /// <summary>
        /// **等 `<c>EncoderCombo</c>` 的项与当前格式一致**（`RefreshEncoderListAsync` 是 async，
        /// `SetFormat` 不等待它 —— 见 <c>MainWindow.xaml.cs:2051</c>）。
        ///
        /// <para><b>与 <see cref="SettleFormatLinkage"/> 的分工</b>：后者是**入口前置**，
        /// 把「格式⇒编码器」联动整体走到稳态；本方法是给**组内**临时切格式后用的轻量等待
        /// （实测 <c>L1e</c> 的 `SetFormat("AVIF")` 之后列表可能还没换过来 ⇒
        /// `Find⚡` 找不到硬件项 ⇒ `L1e-0` 转红，且因它 `return`，`L1f`..`L1k`
        /// **六条整段消失** ← 这是最危险的形态：断言静默变少而不是变红）。
        /// 实测 <c>ui-param-defects</c> 单跑 12 次红 2 次。</para>
        ///
        /// <para>⚠ 只驱动 UI 队列（<c>Pump</c>）等列表非空且不再含上一个格式的残留项，
        /// **不做断言、不改计数、不碰任何被断言的取值**。</para>
        /// </summary>
        /// <param name="expectContains">期望当前选中项包含的子串（如 "avif"）；空则只等非空。</param>
        public static void AwaitEncoderListSettled(string expectContains = "")
        {
            var enc = Find<ComboBox>("EncoderCombo");
            if (enc == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 20.0)
            {
                if (enc.ItemCount > 0)
                {
                    if (expectContains == "") return;
                    // 列表里出现期望的编码器族即认为已换过来
                    bool found = false;
                    for (int i = 0; i < enc.ItemCount; i++)
                    {
                        var s = enc.Items[i] as string ?? "";
                        if (s.Contains(expectContains, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                    }
                    if (found) return;
                }
                Pump(10);
            }
        }

        /// <summary>
        /// **把「ffmpeg 能力 + 编码器列表」两个缓存同步落实**（消除 `EncoderCombo` 未定型的竞态）。
        ///
        /// <para><b>为什么光等日志不够</b>：<c>FullDetectionAsync</c> 的 Step 2 是
        /// <c>Task.Run(...)</c> + <c>Task.WhenAny(t, Task.Delay(8000))</c>
        /// （<c>MainWindow.xaml.cs:1254-1265</c>）——**等的是"任务起来过"，不是"缓存填好了"**。
        /// 且在 UI 线程上 `Pump` 只跑排队作业，不能保证那个后台 Task 已写完静态缓存
        /// ⇒ 既有的 <see cref="AwaitStartupDetection"/>（按日志文本判断）仍有残余窗口：
        /// 实测 <c>L1e-0</c>（硬件项）6 次红 1 次、<c>N5b</c>（`-threads`/策略载体）6 次红 4 次。</para>
        ///
        /// <para><b>本方法做什么</b>：直接**同步等**这两个服务把缓存建好 ——
        /// <list type="bullet">
        /// <item><description><c>FormatCapabilitiesService.InitializeAsync(ffmpegPath)</c>：填充逐格式能力
        /// （`ColorSpaceCombo` 的候选项、`SupportsColorRange` 等都读它）。</description></item>
        /// <item><description><c>EncoderDetectionService.GetAllEncodersAsync(ffmpegPath)</c>：填充
        /// <c>_allEncoders</c>，`EncoderCombo` 的可用项与 `GetCurrentEncoderBackend()` 都读它。</description></item>
        /// </list>
        /// 两者都是**幂等且带缓存**的（`GetAllEncodersAsync` 命中 `_allEncoders` 直接返回），
        /// 故后台那次重复调用不会造成额外开销，也不会改变最终值 —— 只是把"何时定型"
        /// 从"碰运气"变成"调用方保证"。</para>
        ///
        /// <para>⚠ **不做任何断言、不打 PASS/FAIL、不动计数**；也**不**替代各组的
        /// <c>SweepReset()</c>（那只把 UI 控件复位到基线）。</para>
        /// </summary>
        public static void SettleCapabilities()
        {
            var ff = "";
            try { ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath; } catch { }
            if (string.IsNullOrWhiteSpace(ff)) return;

            // ⚠⚠ **必须**用 Task.Run 把等待挪出 UI 同步上下文，否则**死锁**（实测踩过）：
            //   Avalonia 无头模式下本方法跑在 UI 线程上，而 `InitializeAsync` /
            //   `GetAllEncodersAsync` 内部有 await —— 其续体会被排队回 UI 同步上下文，
            //   而我们又在 UI 线程上**同步阻塞**等它 ⇒ 续体永远没机会跑 ⇒ 双方互等。
            //   实测症状：`Tests.UiCore.exe ... ui-preset-roundtrip` 进程存活 >10 min、
            //   CPU 仅 1.8 s（纯空等），必须外部击杀。
            //   ⚠ 这正是本仓既有约定：`tests/UiTestHost/Program.cs:1009` 的注释
            //     「Task.Run 包裹：避免在 UI 同步上下文 await 续体造成死锁（同产品代码做法）」。
            //   ⇒ 在**线程池**上跑这两个 await，再同步等 —— 池线程没有 UI 同步上下文，
            //     续体直接在池上完成，不会排回 UI 线程。
            try
            {
                // ⚠ 外层再套一个**硬超时**（Task.Wait(ms)）：万一未来产品代码又引入
                //   "续体必须回 UI 线程"的写法，本前置会**响亮超时**而不是把整个 mode 挂死
                //   （挂死比失败更坏：门禁只能靠外部击杀，且看不出是哪一步）。
                var work = System.Threading.Tasks.Task.Run(() =>
                {
                    try { FfmpegGui.Services.FormatCapabilitiesService.InitializeAsync(ff).GetAwaiter().GetResult(); }
                    catch { }
                    try { FfmpegGui.Services.EncoderDetectionService.GetAllEncodersAsync(ff).GetAwaiter().GetResult(); }
                    catch { }
                });
                if (!work.Wait(TimeSpan.FromSeconds(60)))
                    Console.WriteLine("      [warn] SettleCapabilities 超过 60s 未完成（按未定型继续，不断言）");
            }
            catch { }
            Pump(6);
        }

        /// <summary>
        /// 会话收尾：关窗口 + 清 TempDir + 打旧宿主那两行尾部输出。
        /// <para>⚠ 与拆分前**逐字一致**：空行、<c>===== UI 测试汇总: N PASS / M FAIL =====</c>、
        /// 随后逐条 <c>  FAILED -&gt; name :: detail</c>。5 条 UI 门禁按
        /// <c>(\d+) PASS / (\d+) FAIL</c> 解析本行。</para>
        /// </summary>
        /// <returns>旧宿主语义的退出码 = FAIL 条数（与拆分前一致）。</returns>
        public static int Finish()
        {
            try { W?.Close(); } catch { }
            CleanupTempDir();

            Console.WriteLine();
            Console.WriteLine($"===== UI 测试汇总: {Tests.Shared.Harness.Pass} PASS / {Tests.Shared.Harness.Fail} FAIL =====");
            foreach (var m in FailMsgs) Console.WriteLine("  FAILED -> " + m);
            return Tests.Shared.Harness.Fail;
        }

        // ══════════════ 基础设施 helper ══════════════

        /// <summary>把 Avalonia UI 线程的排队作业跑 <paramref name="n"/> 轮（让绑定/事件处理器落地）。</summary>
        public static void Pump(int n = 6) { for (int i = 0; i < n; i++) Dispatcher.UIThread.RunJobs(); }

        /// <summary>按 x:Name 找控件（真实控件树）。</summary>
        public static T? Find<T>(string name) where T : Control => W.FindControl<T>(name);

        /// <summary>反射设置 <c>_inputPath</c>（UI 选文件的等价注入；无它 <c>RegenerateCommand</c> 会提前返回）。</summary>
        public static void SetInput(string path) =>
            typeof(FfmpegGui.MainWindow).GetField("_inputPath", BF)!.SetValue(W, path);

        /// <summary>反射调 <c>RegenerateCommand</c> 并按旧宿主节奏 Pump。</summary>
        public static void Regen()
        {
            typeof(FfmpegGui.MainWindow).GetMethod("RegenerateCommand", BF)!.Invoke(W, null);
            Pump(8);
        }

        /// <summary>当前预览命令串（绑定到 <c>CommandText</c> 文本框）。</summary>
        public static string Cmd() => Find<TextBox>("CommandText")?.Text ?? "";

        /// <summary>选输出格式（<c>FormatCombo</c>）并按旧宿主节奏 Pump。</summary>
        public static void SetFormat(string s) { var c = Find<ComboBox>("FormatCombo"); if (c != null) c.SelectedItem = s; Pump(8); }

        /// <summary>按 x:Name 升起一个 Button 的 Click 事件（真实事件处理器）。</summary>
        public static void Click(string name)
        {
            var b = Find<Button>(name);
            b?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(8);
        }

        /// <summary>
        /// UI 宿主的判定并打印一行：<c>[PASS] name</c> / <c>[FAIL] name :: detail</c>。
        /// <para>⚠ 形状与拆分前**逐字一致**（方括号 + detail），但**计数走
        /// <see cref="Tests.Shared.Harness"/> 的唯一真值** —— 这样本行与
        /// <c>PROBE RESULT</c> 汇总读的是同一份计数，不会脱钩。</para>
        /// </summary>
        public static void Check(string name, bool cond, string detail = "")
        {
            if (cond) { Tests.Shared.Harness.PassOnly(); Console.WriteLine($"[PASS] {name}"); }
            else
            {
                Tests.Shared.Harness.FailOnly();
                FailMsgs.Add($"{name} :: {detail}");
                Console.WriteLine($"[FAIL] {name} :: {detail}");
            }
        }

        /// <summary>把可能抛异常的用例体包起来：异常 = 一条 <c>[FAIL] name (异常)</c>，不中断整组。</summary>
        public static void Safe(string name, Action a)
        {
            try { a(); } catch (Exception ex) { Check(name + " (异常)", false, ex.Message); }
        }

        // ══════════════ 子进程（UI 组专用形状：带超时 + CreateNoWindow）══════════════
        // ⚠ 与 Harness.RunCapture 的差别是**逐字保留旧宿主语义**：
        //   旧宿主这两个 helper 用 WaitForExit(60000) / (30000) 硬超时且 CreateNoWindow
        //   （UI 宿主不能弹出控制台窗口）。改走 Harness 会丢掉超时 ⇒ 挂死风险。
        //   实测调用者只有 H组(H11) / I组 / P组。

        /// <summary>跑子进程并**丢弃输出**，返回退出码（60 s 硬超时；逐字保留旧宿主实现）。</summary>
        public static int Run(string exe, string arguments)
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

        /// <summary>跑子进程并返回 stdout（30 s 硬超时；逐字保留旧宿主实现）。</summary>
        public static string RunOut(string exe, string arguments)
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

        // ══════════════ 命令串 token 化 + 按下标取值（J/K/L/M/N/I 共用）══════════════
        // 口径（旧宿主注释逐字保留）：
        //   `Contains` 对 "-pix_fmt yuvj444p" 与 "-pix_fmt yuvj420p" 都真 ⇒ 断言没有区分力。
        //   ⇒ 一律先把命令串切成 token（引号内的空格不切），再按**下标关系**定位断言
        //     （「-pix_fmt 的下一个 token 必须**等于** yuvj444p」），并用
        //     「改回默认值 ⇒ 该 token 必须消失」做反向负控。

        /// <summary>把命令串切成 token（引号内的空格/制表符**不**切；引号本身保留在 token 里）。</summary>
        public static List<string> Tok(string s)
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

        /// <summary>从下标 <paramref name="from"/> 起找第一个精确等于 <paramref name="key"/> 的 token 下标；无 ⇒ -1。</summary>
        public static int IdxFrom(List<string> t, string key, int from)
        {
            for (int i = (from < 0 ? 0 : from); i < t.Count; i++) if (t[i] == key) return i;
            return -1;
        }

        /// <summary>找第一个精确等于 <paramref name="key"/> 的 token 下标；无 ⇒ -1。</summary>
        public static int Idx(List<string> t, string key) => IdxFrom(t, key, 0);

        /// <summary>按**下标关系**取值：key 之后紧邻的那个 token。缺 key / 无值 ⇒ null。</summary>
        public static string? Val(List<string> t, string key) => ValAfter(t, key, 0);

        /// <summary>按**下标关系**取值，从 <paramref name="from"/> 起找 key。缺 key / 无值 ⇒ null。</summary>
        public static string? ValAfter(List<string> t, string key, int from)
        {
            int i = IdxFrom(t, key, from);
            return (i >= 0 && i + 1 < t.Count) ? t[i + 1] : null;
        }

        /// <summary>token **精确成员**判断（不是整串 Contains）。</summary>
        public static bool HasTok(List<string> t, string exact)
        {
            foreach (var x in t) if (x == exact) return true;
            return false;
        }

        /// <summary>-vf 的取值再按 ',' 切成滤镜段（token 内再切一层），用于 iccgen / zscale / scale 这类断言。</summary>
        public static List<string> VfSegs(List<string> t)
        {
            var outp = new List<string>();
            var vf = Val(t, "-vf");
            if (vf == null) return outp;
            foreach (var seg in vf.Trim('"').Split(','))
                if (seg.Length > 0) outp.Add(seg);
            return outp;
        }

        /// <summary>-vf 的滤镜段里有**精确等于** <paramref name="seg"/> 的一段。</summary>
        public static bool VfHas(List<string> t, string seg)
        {
            foreach (var s in VfSegs(t)) if (s == seg) return true;
            return false;
        }

        /// <summary>-vf 的滤镜段里有以 <paramref name="prefix"/> 开头的一段。</summary>
        public static bool VfHasPrefix(List<string> t, string prefix)
        {
            foreach (var s in VfSegs(t)) if (s.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>token 精确成员判断（不是整串 Contains）。</summary>
        public static bool TokEq(List<string> t, string a, string b) => a == b;

        /// <summary>把 token 列表还原成可读串（用 " | " 连接）。</summary>
        public static string TokStr(List<string> t) => string.Join(" | ", t);

        /// <summary>诊断：把命令串的 token 数与前 110 字符快照打到 stdout（非断言）。</summary>
        public static void DumpCmd(string tag, string cmd)
        {
            var t = Tok(cmd);
            Console.WriteLine($"      [dump] {tag} :: {t.Count} tokens");
            Console.WriteLine("        " + TokStr(t));
        }

        // ══════════════ 基线复位（多组共用的"可复现起点"）══════════════

        /// <summary>
        /// 把 UI 复位到「可复现的基线」：静态模式 / PNG / 高级关 / 策略=推荐 / 色域 auto。
        /// <para>⚠ 这是 J/K/L/M/N/O 各组的**共同前置**：它把上一组留下的状态清掉，
        /// 使这些组能独立运行（拆分后每个 mode 各自一个进程时尤其关键）。</para>
        /// </summary>
        public static void SweepReset()
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

        /// <summary>选中某个策略单选并**锁定**手动语义（_iccModeTouched=true），
        /// 使色域联动不再覆盖它 —— 否则「选 Display P3 自动改策略」会让断言不可复现。</summary>
        public static void SetStrategyRadio(string name)
        {
            var rb = Find<RadioButton>(name);
            if (rb != null) { rb.IsChecked = true; Pump(6); }
            typeof(FfmpegGui.MainWindow).GetField("_iccModeTouched", BF)?.SetValue(W, true);
        }

        /// <summary>在 EncoderCombo 里挑一个满足谓词的编码器并选中（找不到 ⇒ null）。
        /// 必要性（实测）：各格式的**默认**编码器不一定走 ffmpeg 管线 ——
        /// JPEG 默认 cjpegli、JPEG XL 默认 cjxl ⇒ 预览命令是 cjpegli/cjxl 命令行，
        /// 里面**没有** -q:v / -pix_fmt / -huffman 这些 ffmpeg token。若不显式选编码器，
        /// 断言就会把「路由不同」误判成「参数没传递」。本 helper 让路由**可复现**。</summary>
        public static string? PickEncoder(Func<string, bool> pred)
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
        public static string? PickFfmpegEncoder() => PickEncoder(s =>
            !s.Contains("cjpegli", StringComparison.OrdinalIgnoreCase) &&
            !s.Contains("cjxl", StringComparison.OrdinalIgnoreCase));

        /// <summary>取 -vf 里 zscale 滤镜段的某个参数（p / t / m / pin / tin / min）。
        /// ⚠ 载体说明（实测）：PNG/TIFF/JXL 等 **RGB 原生**格式的**目标**色域写在 zscale 滤镜里
        /// （`p=`/`t=`/`m=`），**不是** -color_primaries/-color_trc —— 后两者是**输入声明**。
        /// 断言目标色域必须走本 helper，否则就是「载体选错 ⇒ 断言恒真」。</summary>
        public static string? ZscaleParam(List<string> t, string key)
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
        public static string UiState() =>
            $"advColor={Find<CheckBox>("UseAdvancedColor")?.IsChecked} advCodec={Find<CheckBox>("UseAdvancedCodec")?.IsChecked} " +
            $"lossless={Find<CheckBox>("LosslessCheck")?.IsChecked} enc={Find<ComboBox>("EncoderCombo")?.SelectedItem as string}";

        /// <summary>统计 token 列表里精确等于 <paramref name="key"/> 的个数（L 组判据用）。</summary>
        public static int CountTok(List<string> t, string key)
        {
            int n = 0;
            foreach (var x in t) if (x == key) n++;
            return n;
        }

        /// <summary>把一行文本裁到 110 字符（失败明细/诊断串用；逐字保留旧宿主实现）。</summary>
        public static string Snip(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length > 110 ? s.Substring(0, 110) + "…" : s;
        }

        // ══════════════ 启动期探测的**同步点**（跨组共享前置）══════════════

        /// <summary>启动期检测日志里的 7 个槽位名（与 <c>MainWindow.FullDetectionAsync</c> 的 Mk 一一对应）。</summary>
        public static readonly string[] StartupDetectNames =
            { "cjxl", "cjpegli", "djxl", "exiftool", "JxrEncApp", "avifenc", "dngtool" };

        /// <summary>
        /// **等启动期的 <c>FullDetectionAsync()</c> 把 7 个槽位都打完**（超时上限见
        /// <paramref name="timeoutSec"/>，默认 45 s，与 GroupP 的口径一致）。
        ///
        /// <para><b>为什么必须是共享前置</b>：<c>InitControls()</c> 里那次工具探测是
        /// **fire-and-forget**（<c>Task.Run</c> ×7 并发，<c>MainWindow.xaml.cs:1227-1245</c>）。
        /// 探测未落地时：
        /// <list type="bullet">
        /// <item><description><c>ExifToolService._detectedPath</c> 可能正被后台
        /// <c>Detect()</c> 回填 ⇒ 与 <c>C3d</c>「清空 <c>_detectedPath</c> 再断言禁用」**竞态**
        /// （实测单跑 ui-panel-visibility 3 次红 1 次）。</description></item>
        /// <item><description><c>EncoderCombo</c> 的可用项列表
        /// （<c>encoders.Where(e =&gt; e.IsAvailable)</c>，:2063）尚未定型 ⇒ 与 <c>N5b</c>
        /// 的「两条路径逐字节比命令」竞态（实测单跑 ui-preset-roundtrip 6 次红 3 次），
        /// 以及 <c>L1e-0</c>「本环境存在硬件编码器项」间歇红 + 连带 <c>L1f</c>..<c>L1k</c>
        /// 六条整段不执行。</description></item>
        /// </list>
        /// 旧宿主之所以从不红：它把 <c>GroupP</c> 排在**启动后第一个**（本函数就是它等的那个条件），
        /// 于是后续所有组都在"探测已落地"的稳态下跑。拆成单 mode 后若不显式等，
        /// 就丢掉了这个隐式前置 ⇒ 本函数把它变成**显式、可复现**的一步。</para>
        ///
        /// <para>⚠ **只等 Step 1 是不够的**（实测）：<c>EncoderCombo</c> 的列表来自
        /// <b>Step 2</b> 的 <c>EncoderDetectionService.GetAllEncodersAsync(ffmpegPath)</c>
        /// （<c>MainWindow.xaml.cs:1261</c>，同样 fire-and-forget + 8 s 超时）。
        /// 故本函数必须同时等到 <c>[detect]</c> 里 Step 1 的 7 条结论行**和**
        /// Step 2 的那两行进度/超时行都出现（或超时），否则 <c>L1e-0</c>/<c>N5b</c>
        /// 仍会因"列表未定型"间歇转红。</para>
        ///
        /// <para>⚠ 汇总口径：本函数**不做任何断言**、不打 PASS/FAIL、不改计数 ——
        /// 它只是等待既有后台工作收敛。</para>
        /// </summary>
        /// <returns>true = 7 个槽位 + Step 2 都已收尾；false = 超时（调用方自行决定是否记为测量失败）。</returns>
        public static bool AwaitStartupDetection(double timeoutSec = 45.0)
        {
            var box = Find<TextBox>("LogText");
            if (box == null) return false;
            var verdicts = new List<string>();
            var others = new List<string>();
            var suspects = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSec)
            {
                Pump(6);
                ClassifyDetectLines(box.Text, StartupDetectNames, verdicts, others, suspects);
                if (verdicts.Count >= StartupDetectNames.Length && Step2Settled(box.Text)) return true;
            }
            return false;
        }

        /// <summary>
        /// Step 2（ffmpeg 像素格式检测 / 编码器列表加载）是否已收尾 —— 二者各自要么"完成"、
        /// 要么"超时/失败"，无论哪种都说明**列表已定型**（可能是空，但不会再变）。
        /// <para>判据逐字对齐 <c>MainWindow.xaml.cs:1255-1263</c> 的四种 Log 文本。</para>
        /// </summary>
        private static bool Step2Settled(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            bool pixDone = text.Contains("[detect] ffmpeg 像素格式检测完成", StringComparison.Ordinal)
                        || text.Contains("[detect] ⚠️ ffmpeg 像素格式检测超时", StringComparison.Ordinal)
                        || text.Contains("[detect] ⚠️ ffmpeg 像素格式检测失败", StringComparison.Ordinal);
            bool encDone = text.Contains("[detect] ffmpeg 编码器列表加载完成", StringComparison.Ordinal)
                        || text.Contains("[detect] ⚠️ ffmpeg 编码器列表超时", StringComparison.Ordinal)
                        || text.Contains("[detect] ⚠️ ffmpeg 编码器列表失败", StringComparison.Ordinal);
            // ffmpeg 未定位时 Step 2 整段跳过 ⇒ 这一行即"已收尾"
            bool skipped = text.Contains("[detect] ffmpeg 未找到", StringComparison.Ordinal);
            return skipped || (pixDone && encDone);
        }

        // ══════════════ P 组：启动期检测日志的分类器 ══════════════

        /// <summary>
        /// 面板里的 `[detect] ` 开头行分三类（口径必须**按形状**而不是按"有没有冒号"）：
        /// <para>verdicts = `[detect] &lt;七个槽位名之一&gt;: &lt;尾巴&gt;`          ⇒ 槽位打的结论（P 组判据对象）<br/>
        /// suspects = `[detect] &lt;别的名字&gt;: &lt;OK|未找到|探测抛异常…&gt;` ⇒ **空槽位/漏改名**的确切形态<br/>
        /// others   = 其余 `[detect]` 行                            ⇒ Step2 的 ffmpeg 进度行</para>
        /// <para>（`[detect] ffmpeg 已定位: C:\…\ffmpeg.exe` / `…像素格式检测完成` / `…编码器列表加载完成`
        /// / `[detect] ffmpeg 未找到（PATH 或 PLAN 均未检测到），跳过进程探测`）</para>
        /// <para>⚠ 实测本机就会多出这些 ⇒ 若按"名字不认识就判红"，P4 会**天天假红**；
        /// 所以 suspects 只收「尾巴是**结论词**、名字却不在七个之内」的行 —— 那才是空槽位/漏改名的信号。</para>
        /// </summary>
        public static void ClassifyDetectLines(string? text, string[] names,
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
    }
}
