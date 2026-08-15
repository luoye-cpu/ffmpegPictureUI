using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Data.Core;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;
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

            // ── NativeAOT 安全：使用编译绑定（ClrPropertyInfo + CompiledBindingPathBuilder）──
            // 与 Avalonia XAML 编译器生成的 AOT 安全代码路径一致，
            // 避免 ReflectionBinding 的 IL2026/IL3050 警告及裁剪/动态代码风险。
            var refreshProp = new ClrPropertyInfo(
                nameof(LocalizationService.RefreshVersion),
                getter: o => ((LocalizationService)o!).RefreshVersion,
                setter: null, // 只读属性
                typeof(int));
            var compiledPath = new CompiledBindingPathBuilder()
                .Property(refreshProp, PropertyInfoAccessorFactory.CreateInpcPropertyAccessor)
                .Build();
            mb.Bindings.Add(new CompiledBinding(compiledPath)
            {
                Source = LocalizationService.Instance,
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
