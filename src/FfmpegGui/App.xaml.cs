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

        public override void OnFrameworkInitializationCompleted()
        {
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