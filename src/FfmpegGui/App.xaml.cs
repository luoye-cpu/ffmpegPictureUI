using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace FfmpegGui
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
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
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}