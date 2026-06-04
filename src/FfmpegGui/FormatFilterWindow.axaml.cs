using Avalonia.Controls;
using Avalonia.Interactivity;
using FfmpegGui.Models;
using FfmpegGui.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FfmpegGui
{
    public partial class FormatFilterWindow : Window
    {
        private readonly Dictionary<string, CheckBox> _checkBoxes = new();
        private List<string>? _result;

        public FormatFilterWindow()
        {
            InitializeComponent();
            BuildCheckBoxes();
        }

        /// <summary>
        /// 显示格式筛选窗口（模态），返回用户选择的格式名称列表；取消返回 null
        /// </summary>
        public static async Task<List<string>?> ShowFilterDialog(Window owner)
        {
            var win = new FormatFilterWindow();
            return await win.ShowDialog<List<string>?>(owner);
        }

        private void BuildCheckBoxes()
        {
            var panel = this.FindControl<StackPanel>("FormatCheckPanel");
            if (panel == null) return;

            var currentEnabled = AppSettingsService.Current.EnabledImageFormats;
            var enabledSet = new HashSet<string>(currentEnabled);

            foreach (var kv in AppSettings.AllImageFormats)
            {
                var cb = new CheckBox
                {
                    Content = $"{kv.Key}  ({string.Join(", ", kv.Value.Select(e => "*" + e))})",
                    IsChecked = enabledSet.Contains(kv.Key),
                    Tag = kv.Key,
                    Margin = new Avalonia.Thickness(0, 0, 0, 0)
                };
                _checkBoxes[kv.Key] = cb;
                panel.Children.Add(cb);
            }
        }

        private List<string> GetSelectedFormats()
        {
            return _checkBoxes
                .Where(kv => kv.Value.IsChecked == true)
                .Select(kv => kv.Key)
                .ToList();
        }

        private void SelectAll_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var cb in _checkBoxes.Values)
                cb.IsChecked = true;
        }

        private void DeselectAll_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var cb in _checkBoxes.Values)
                cb.IsChecked = false;
        }

        private void Ok_Click(object? sender, RoutedEventArgs e)
        {
            _result = GetSelectedFormats();
            // 持久化到设置
            AppSettingsService.Current.EnabledImageFormats = _result;
            AppSettingsService.Save();
            Close(_result);
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e)
        {
            Close((List<string>?)null);
        }
    }
}
