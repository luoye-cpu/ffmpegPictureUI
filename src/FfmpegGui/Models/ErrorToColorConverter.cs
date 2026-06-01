using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace FfmpegGui.Models
{
    /// <summary>
    /// 将 QueueItem.HasError (bool) 转换为前景色画刷，报错时返回 Red，否则返回 UnsetValue（保持默认颜色）
    /// </summary>
    public class ErrorToColorConverter : IValueConverter
    {
        public static readonly ErrorToColorConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is true)
                return Brushes.Red;
            // 不设置该属性，让 TextBlock 沿用默认/继承的前景色
            return AvaloniaProperty.UnsetValue;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
