using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using System;
using System.Globalization;

namespace FfmpegGui.Models
{
    /// <summary>
    /// 将 QueueItem 的状态转换为前景色：失败红、警告黄，其余返回当前主题文字色。
    /// 也兼容旧的 bool 绑定（true = HasError）。
    /// </summary>
    public class ErrorToColorConverter : IValueConverter
    {
        public static readonly ErrorToColorConverter Instance = new();

        /// <summary>警告条目颜色（amber；在深/浅主题下均保持可读）。</summary>
        public static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#FFFFC107"));

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is QueueItem item)
            {
                if (item.HasError) return Brushes.Red;
                if (item.IsWarning) return WarningBrush;
            }
            else if (value is true)
            {
                return Brushes.Red;
            }

            // 返回当前主题对应的系统文字画刷（深色→白，浅色→深灰）
            var app = Application.Current;
            if (app != null)
            {
                var theme = app.ActualThemeVariant;
                if (app.TryGetResource("SystemControlForegroundBaseHighBrush", theme, out var brush) && brush != null)
                    return brush;
                // 回退：根据主题手动指定
                return theme == ThemeVariant.Dark ? Brushes.White : Brushes.Black;
            }
            return Brushes.Black;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
