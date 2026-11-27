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
    /// 入参可以是 <see cref="QueueItem"/>（兼容旧的无路径绑定）、
    /// <see cref="QueueItem.StatusColorKey"/> 的字符串键（error/warning/normal，会随状态变化发通知），
    /// 或旧的 bool 绑定（true = HasError）。
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
            else if (value is string key)
            {
                // StatusColorKey：走这条路径才有 INotifyPropertyChanged 驱动的重算。
                if (key == "error") return Brushes.Red;
                if (key == "warning") return WarningBrush;
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
