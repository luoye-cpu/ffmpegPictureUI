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
    /// 将 QueueItem.HasError (bool) 转换为前景色画刷，报错时返回 Red，否则返回当前主题对应的文字颜色
    /// </summary>
    public class ErrorToColorConverter : IValueConverter
    {
        public static readonly ErrorToColorConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is true)
                return Brushes.Red;

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
