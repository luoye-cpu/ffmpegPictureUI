using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using System;
using System.IO;
using System.Reflection;
using FfmpegGui.Services;

namespace FfmpegGui
{
    public partial class App : Application
    {
        // ═══════════════════════════════════════════════
        // 托盘图标引用和静态访问器
        // ═══════════════════════════════════════════════

        private TrayIcon? _trayIcon;
        private NativeMenu? _trayMenu;
        private MainWindow? _mainWindow;

        /// <summary>关窗是否隐藏到托盘（运行时可改；来源 <see cref="Models.AppSettings.CloseToTray"/>）。</summary>
        private bool _closeToTray = true;

        /// <summary>托盘是否真的可用（TrayIcon 创建成功且图标位图非空）。<c>false</c> ⇒ 关窗必须真退出。</summary>
        private bool _trayAvailable;

        /// <summary>退出闸：ExitApp 可能被 Closed 处理器再调一次，重入会二次 Close 窗口。</summary>
        private bool _exiting;

        /// <summary>托盘不可用的原因；<c>null</c> = 一切正常。写进托盘日志并在设置菜单里回显。</summary>
        public static string? TrayFailureReason { get; private set; }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            // 托盘是否常驻由设置决定（旧写法把 IsVisible 写死成 false ⇒ 见下方长注）。
            _closeToTray = AppSettingsService.Current.CloseToTray;

            // 代码创建托盘图标和菜单（避免 XAML 事件绑定限制）
            // 注意：在不支持托盘的平台（如某些无 AppIndicator 的 Linux）上，
            // 创建 TrayIcon 可能抛出异常，需优雅回退
            //
            // ⚠ 2026-10-31 缺陷修复（维护者实机反馈「托盘图标消失 + 关窗后进程不退」）：
            //   旧写法 `IsVisible = false` ⇒ 进程一起来托盘里**根本没有图标**，只有走到
            //   「关窗 ⇒ HideMainWindowToTray」那一刻才点亮；而唯一的退出入口（托盘菜单「退出」）
            //   就挂在那枚默认不亮的图标上 ⇒ 用户看到的正是「图标没了 / 关不掉 / 只能在任务管理器里杀」。
            //   ⇒ 现在：CloseToTray=true（默认）时启动即常驻可见。
            // ⚠ 失败路径也不许静默：旧写法把异常扔进 `Debug.WriteLine`，Release 下**零痕迹**
            //   ⇒ 用户机器上「托盘为什么没有」永远查不出来。现在统一落 tray.log（见 LogTray）。
            try
            {
                var icon = LoadTrayIcon();
                if (icon == null)
                {
                    _trayAvailable = false;
                    TrayFailureReason = "icon resource missing/undecodable: avares://FfmpegGui/Resources/icon_raw.png";
                    LogTray($"[tray] create=skipped icon=null reason={TrayFailureReason} ⇒ 关窗改为真退出");
                }
                else
                {
                    var showItem = new NativeMenuItem("显示主窗口");
                    showItem.Click += (_, _) => ShowMainWindow();

                    var pauseItem = new NativeMenuItem("暂停队列");
                    pauseItem.Click += (_, _) =>
                    {
                        if (_mainWindow is MainWindow mw)
                        {
                            if (mw.IsQueueRunning) mw.PauseQueue();
                            else mw.ResumeQueue();
                        }
                    };

                    var exitItem = new NativeMenuItem("退出");
                    exitItem.Click += (_, _) => ExitApp();

                    _trayMenu = new NativeMenu { Items = { showItem, pauseItem, new NativeMenuItemSeparator(), exitItem } };

                    _trayIcon = new TrayIcon
                    {
                        ToolTipText = "FFmpegPictureUI",
                        IsVisible = _closeToTray,
                        Menu = _trayMenu,
                        Icon = icon
                    };
                    _trayAvailable = true;
                    TrayFailureReason = null;
                    LogTray($"[tray] create=ok IsVisible={_trayIcon.IsVisible} closeToTray={_closeToTray} icon=loaded");
                }
            }
            catch (Exception ex)
            {
                // Linux 无 AppIndicator / macOS 旧版本等不支持托盘的环境
                // 记录日志但不影响主功能
                _trayIcon = null;
                _trayMenu = null;
                _trayAvailable = false;
                TrayFailureReason = $"{ex.GetType().Name}: {ex.Message}";
                LogTray($"[tray] create=failed reason={TrayFailureReason} ⇒ 关窗改为真退出");
            }
        }

        /// <summary>
        /// 托盘/关窗链路的诊断落盘。**GUI 下用户看不到控制台**，旧写法只有 <c>Debug.WriteLine</c>，
        /// Release 里一行都不留 ⇒ 「托盘没图标」「托盘创建失败」在用户机器上完全不可见。
        /// 目标目录：<c>FFMPEGGUI_DIAG_DIR</c>（门禁点名）&gt; 缓存目录（<see cref="PlatformServices.GetTempDir"/>）&gt; exe 同目录。
        /// 本方法自身不得抛：诊断写不进去也不能拖垮启动。
        /// </summary>
        internal static void LogTray(string message)
        {
            // ⚠ `[tray]` 标签一律由**调用方**写在消息开头：本仓判据按「行首第一个字面量是否以 [ 开头」
            //   分桶，诊断日志必须落 [tag] 桶，否则每一行都会被算成「UI 面硬编码中文」。
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            System.Diagnostics.Debug.WriteLine(line);
            try
            {
                var dir = Environment.GetEnvironmentVariable("FFMPEGGUI_DIAG_DIR");
                if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(dir))
                {
                    var cache = PlatformServices.GetTempDir();
                    dir = (!string.IsNullOrWhiteSpace(cache) && Path.IsPathRooted(cache))
                        ? cache
                        : (Path.GetDirectoryName(Environment.ProcessPath) ?? ".");
                }
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "tray.log"), line + Environment.NewLine);
            }
            catch { /* 诊断本身不得再抛 */ }
        }

        /// <summary>从程序集资源加载托盘图标（AvaloniaResource 打包为 avares 流）</summary>
        private static WindowIcon? LoadTrayIcon()
        {
            try
            {
                var assembly = typeof(App).Assembly;
                // AvaloniaResource 的资源名格式：<程序集名>/<路径>
                // ⚠ 用 PNG 而不是 icon.ico：Pillow 写出的 ICO 每条目都是 PNG 载荷，Windows shell 认，
                //   但 Avalonia/Skia 的 ICO 读取器解出来是 1×1（UiTestHost C20c 实测）⇒ 托盘会"有图标但看不见"。
                var resourceName = "avares://FfmpegGui/Resources/icon_raw.png";
                if (Application.Current?.Resources == null) return null;

                // 使用 AssetLoader 打开资源流
                var uri = new Uri(resourceName);
                if (!Avalonia.Platform.AssetLoader.Exists(uri)) return null;
                using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                return new WindowIcon(new Bitmap(stream));
            }
            catch (Exception ex)
            {
                LogTray($"[tray] icon=load-failed {ex.GetType().Name}: {ex.Message}");
                return null; // 图标缺失不影响功能
            }
        }

        /// <summary>
        /// 托盘图标是否可见
        /// </summary>
        public static bool IsTrayIconVisible
        {
            get => (Current as App)?._trayIcon?.IsVisible ?? false;
            set
            {
                if (Current is App app && app._trayIcon != null)
                    app._trayIcon.IsVisible = value;
            }
        }

        /// <summary>托盘是否可用（创建成功且图标非空）。<c>false</c> ⇒ 关窗走真退出路径，不隐藏。</summary>
        public static bool IsTrayAvailable => (Current as App)?._trayAvailable ?? false;

        /// <summary>当前关窗行为：<c>true</c> = 隐藏到托盘。</summary>
        public static bool CloseToTrayEnabled => (Current as App)?._closeToTray ?? true;

        /// <summary>
        /// 运行期修改关窗行为（⚙ 设置菜单调用）：<c>false</c> ⇒ 立即收掉常驻托盘图标，
        /// 且此后关窗即真退出。设置项本身由调用方落盘。
        /// </summary>
        public static void SetCloseToTray(bool enabled)
        {
            if (Current is App app)
            {
                app._closeToTray = enabled;
                if (app._trayIcon != null) app._trayIcon.IsVisible = enabled;
                LogTray($"[tray] runtime closeToTray={enabled} trayAvailable={app._trayAvailable} trayVisible={app._trayIcon?.IsVisible}");
            }
        }

        /// <summary>
        /// 更新托盘提示文本
        /// </summary>
        public static void SetTrayToolTip(string text)
        {
            if (Current is App app && app._trayIcon != null)
                app._trayIcon.ToolTipText = text;
        }

        /// <summary>
        /// 显示主窗口（从托盘恢复）
        /// </summary>
        public static void ShowMainWindow()
        {
            if (Current is App app)
            {
                app._mainWindow?.Show();
                app._mainWindow?.Activate();
                app._mainWindow?.Focus();
                LogTray("[tray] window shown from tray");
            }
        }

        /// <summary>
        /// 隐藏主窗口到托盘
        /// </summary>
        public static void HideMainWindowToTray()
        {
            if (Current is App app)
            {
                app._mainWindow?.Hide();
                IsTrayIconVisible = true;

                // 更新提示文本：显示队列状态
                if (app._mainWindow is MainWindow mw && mw.IsQueueRunning)
                {
                    var count = mw.GetProcessingCount();
                    SetTrayToolTip($"FFmpegPictureUI - 队列运行中 ({count} 任务处理中)");
                }
            }
        }

        /// <summary>
        /// 真正退出进程。**这是唯一的退出实现**：托盘菜单「退出」、⚙ 菜单「退出程序」、
        /// 关窗真退的兜底都走这里，不许再出现第二份 `Environment.Exit`。
        /// 顺序：收托盘 → 关窗（AllowFullExit 闸打开）→ 显式 Shutdown → Environment.Exit 兜底。
        /// </summary>
        public void ExitApp()
        {
            if (_exiting)
            {
                Environment.Exit(0);
                return;
            }
            _exiting = true;
            LogTray("[tray] exit requested");

            try
            {
                if (_trayIcon != null)
                {
                    _trayIcon.IsVisible = false;
                    _trayIcon.Dispose();
                }
            }
            catch (Exception ex) { LogTray($"[tray] exit: dispose tray failed {ex.GetType().Name}: {ex.Message}"); }
            finally { _trayIcon = null; _trayMenu = null; _trayAvailable = false; }

            try
            {
                if (_mainWindow is MainWindow mw)
                {
                    mw.AllowFullExit = true;
                    mw.Close();
                }
            }
            catch (Exception ex) { LogTray($"[tray] exit: close window failed {ex.GetType().Name}: {ex.Message}"); }

            try
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown(0);
            }
            catch (Exception ex) { LogTray($"[tray] exit: shutdown failed {ex.GetType().Name}: {ex.Message}"); }

            // Shutdown 是"请求"，若渲染/队列线程还占着控制权就可能不落地 ⇒ 这里必须硬落，
            // 否则就是用户看到的"关窗后进程还在"。退出码恒 0：退出是用户意图，不是失败。
            Environment.Exit(0);
        }

        /// <summary>
        /// 切换主题模式：true=深色, false=浅色
        /// </summary>
        public static void SetTheme(bool dark)
        {
            if (Current != null)
            {
                Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            }
        }

        /// <summary>
        /// 获取当前是否为深色模式
        /// </summary>
        public static bool IsDarkMode()
        {
            return Current?.ActualThemeVariant == ThemeVariant.Dark;
        }

        // ═══════════════════════════════════════════════
        // GPU 加速状态（由 Program.cs 在启动时设置）
        // ═══════════════════════════════════════════════

        /// <summary>当前会话 GPU 加速是否已启用</summary>
        public static bool IsGpuEnabled => Program.IsGpuAccelerated;

        public override void OnFrameworkInitializationCompleted()
        {
            // 启动时初始化本地化
            var lang = Services.AppSettingsService.Current.Language;
            if (string.IsNullOrWhiteSpace(lang)) lang = "zh-CN";
            Services.LocalizationService.Instance.LoadLocale(lang);

            // 启动时应用已保存的主题（默认深色）
            var themeMode = Services.AppSettingsService.Current.ThemeMode;
            RequestedThemeVariant = themeMode == 1 ? ThemeVariant.Light : ThemeVariant.Dark;

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _mainWindow = new MainWindow();

                // ── 关窗行为（2026-10-31 修复）────────────────────────────────────
                // 旧写法**无条件** args.Cancel=true + 隐藏到托盘 ⇒ 只要托盘那条链坏掉
                // （图标资源缺失 / 平台不支持托盘 / IsVisible 被写死 false），用户关窗后就落进
                // 「窗口没了、托盘没图标、进程还在」的黑洞，唯一出口是任务管理器。
                // 现在：只有「设置要托盘 且 托盘确实可用」才拦关闭；否则关窗 = 真退出。
                _mainWindow.Closing += (_, args) =>
                {
                    if (_mainWindow.AllowFullExit) return;

                    if (_closeToTray && _trayAvailable)
                    {
                        args.Cancel = true;
                        HideMainWindowToTray();
                        LogTray("[tray] window closing ⇒ hidden to tray");
                        return;
                    }

                    LogTray($"[tray] window closing ⇒ real exit (closeToTray={_closeToTray} trayAvailable={_trayAvailable})");
                    _mainWindow.AllowFullExit = true;
                    args.Cancel = false;
                };

                // 兜底：窗口真关掉了就**必须**死（含 IpcService 的关机请求走的那条 AllowFullExit 路径）。
                // 默认 OnLastWindowClose 多数情况会退出，但队列/渲染线程仍活时不一定 ⇒ 显式 ExitApp。
                _mainWindow.Closed += (_, _) =>
                {
                    if (_mainWindow is { AllowFullExit: true })
                        ExitApp();
                };

                desktop.MainWindow = _mainWindow;
            }

            // IPC 服务器仅在用户显式开启时启动（默认关闭）。
            // 后台化核心是"窗口最小化到托盘待命"，进程保留队列继续跑。
            if (Services.AppSettingsService.Current.EnableIpcServer)
            {
                _ = IpcService.StartServerAsync(orchestrator: null, _mainWindow);
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}