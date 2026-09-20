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

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            // 代码创建托盘图标和菜单（避免 XAML 事件绑定限制）
            // 注意：在不支持托盘的平台（如某些无 AppIndicator 的 Linux）上，
            // 创建 TrayIcon 可能抛出异常，需优雅回退
            try
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
                    IsVisible = false,
                    Menu = _trayMenu,
                    Icon = LoadTrayIcon()
                };
            }
            catch (Exception ex)
            {
                // Linux 无 AppIndicator / macOS 旧版本等不支持托盘的环境
                // 记录日志但不影响主功能
                System.Diagnostics.Debug.WriteLine($"[TrayIcon] 初始化失败，将禁用托盘功能: {ex.Message}");
                _trayIcon = null;
                _trayMenu = null;
            }
        }

        /// <summary>从程序集资源加载托盘图标（AvaloniaResource 打包为 avares 流）</summary>
        private static WindowIcon? LoadTrayIcon()
        {
            try
            {
                var assembly = typeof(App).Assembly;
                // AvaloniaResource 的资源名格式：<程序集名>/<路径>
                var resourceName = "avares://FfmpegGui/Resources/icon.ico";
                if (Application.Current?.Resources == null) return null;

                // 使用 AssetLoader 打开资源流
                var uri = new Uri(resourceName);
                if (!Avalonia.Platform.AssetLoader.Exists(uri)) return null;
                using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                return new WindowIcon(new Bitmap(stream));
            }
            catch
            {
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

        private void ExitApp()
        {
            if (_mainWindow is MainWindow mw)
            {
                mw.AllowFullExit = true;
                mw.Close();
            }
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
                
                // 窗口关闭时隐藏到托盘（而非退出）
                _mainWindow.Closing += (_, args) =>
                {
                    if (!_mainWindow.AllowFullExit)
                    {
                        args.Cancel = true;
                        HideMainWindowToTray();
                    }
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