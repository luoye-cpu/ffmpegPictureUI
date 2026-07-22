using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using FfmpegGui.Services;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace FfmpegGui
{
    /// <summary>
    /// XAML 标记扩展：用于动态绑定本地化字符串。
    /// 用法：Content="{ext:Loc app.title}"
    /// 语言切换时所有绑定自动刷新。
    /// </summary>
    public class LocExtension : MarkupExtension
    {
        public string Key { get; set; } = string.Empty;

        public LocExtension() { }

        public LocExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            // 使用 MultiBinding：同时绑定 RefreshVersion（触发器）和 Key，
            // 当语言切换时 RefreshVersion 变化导致 MultiBinding 重新调用转换器
            var mb = new MultiBinding
            {
                Converter = new LocValueConverter(Key),
            };
            mb.Bindings.Add(new Binding
            {
                Source = LocalizationService.Instance,
                Path = "RefreshVersion",
                Mode = BindingMode.OneWay
            });
            return mb;
        }
    }

    /// <summary>
    /// 本地化值转换器：忽略 RefreshVersion 值，直接从 LocalizationService 获取对应 Key 的本地化字符串。
    /// </summary>
    internal class LocValueConverter : IMultiValueConverter
    {
        private readonly string _key;

        public LocValueConverter(string key)
        {
            _key = key;
        }

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            return LocalizationService.Instance[_key];
        }
    }
}
