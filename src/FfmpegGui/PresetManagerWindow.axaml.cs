using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FfmpegGui.Models;
using FfmpegGui.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FfmpegGui
{
    public partial class PresetManagerWindow : Window
    {
        private List<Models.PresetEntry> _presets = new();
        private Models.PresetEntry? _selectedPreset;

        /// <summary>用户选择应用的预设数据（关闭后由调用方读取）</summary>
        public PresetData? AppliedPreset { get; private set; }

        /// <summary>从外部构建当前预设数据（由 MainWindow 在 ShowDialog 前设置）</summary>
        public PresetData? CurrentSettings { get; set; }

        public PresetManagerWindow()
        {
            InitializeComponent();
            LoadPresets();
        }

        // ── 内部方法 ──

        private void LoadPresets()
        {
            _presets = PresetManagerService.GetAllPresets();

            var listBox = this.FindControl<ListBox>("PresetListBox");
            if (listBox != null)
                listBox.ItemsSource = _presets;
        }

        private void RefreshList()
        {
            _presets = PresetManagerService.GetAllPresets();
            var listBox = this.FindControl<ListBox>("PresetListBox");
            if (listBox != null)
            {
                listBox.ItemsSource = null;
                listBox.ItemsSource = _presets;
            }
        }

        private void PresetList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var listBox = this.FindControl<ListBox>("PresetListBox");
            if (listBox?.SelectedItem is Models.PresetEntry entry)
            {
                _selectedPreset = entry;
                ShowDetail(entry);
            }
            else
            {
                _selectedPreset = null;
                ShowDetail(null);
            }
        }

        private void ShowDetail(Models.PresetEntry? entry)
        {
            var title = this.FindControl<TextBlock>("DetailTitle");
            var text = this.FindControl<TextBlock>("DetailText");

            if (entry == null)
            {
                if (title != null) title.Text = "选择一个预设查看详情";
                if (text != null) text.Text = "";
                return;
            }

            var d = entry.Data;
            var desc = $"格式: {d.Format ?? "—"}  |  质量: {d.Quality}%  |  色度: {d.Chroma ?? "auto"}  |  位深: {d.BitDepth ?? "auto"}";
            if (!string.IsNullOrWhiteSpace(d.ColorSpace) && d.ColorSpace != "auto")
                desc += $"  |  色彩空间: {d.ColorSpace}";
            if (d.Lossless)
                desc += "  |  无损";
            desc += $"  |  线程: {(d.AutoThreads ? "自动" : d.ManualThreads.ToString())}";
            desc += $"  |  元数据: {d.MetadataMode ?? "保留"}";

            if (title != null) title.Text = entry.Name;
            if (text != null) text.Text = desc;
        }

        // ── 按钮事件 ──

        private void Apply_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedPreset == null)
            {
                ShowWarning("请先选择一个预设");
                return;
            }

            AppliedPreset = _selectedPreset.Data;
        }

        private async void SaveCurrent_Click(object? sender, RoutedEventArgs e)
        {
            if (CurrentSettings == null)
            {
                ShowWarning("当前无可用设置");
                return;
            }

            // 简易输入对话框：用 TaskCompletionSource + 弹出式输入
            var name = await ShowInputDialogAsync("保存预设", "请输入预设名称:");
            if (string.IsNullOrWhiteSpace(name)) return;

            var ok = PresetManagerService.SaveUserPreset(name.Trim(), CurrentSettings);
            if (!ok)
            {
                // 同名 → 询问是否覆盖
                var overwrite = await ShowConfirmDialogAsync("覆盖确认",
                    $"预设 \"{name.Trim()}\" 已存在，是否覆盖?");
                if (overwrite)
                {
                    PresetManagerService.OverwriteUserPreset(name.Trim(), CurrentSettings);
                }
                else return;
            }

            RefreshList();
        }

        private async void ImportFile_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入预设文件",
                AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("JSON 预设") { Patterns = new[] { "*.json" } } }
            });

            if (files == null || files.Count == 0) return;

            int imported = 0;
            foreach (var file in files)
            {
                if (PresetManagerService.ImportExternalPreset(file.Path.LocalPath))
                    imported++;
            }

            if (imported > 0)
                RefreshList();
        }

        private async void Delete_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedPreset == null)
            {
                ShowWarning("请先选择一个预设");
                return;
            }

            if (_selectedPreset.Source == "builtin")
            {
                ShowWarning("内置预设不可删除");
                return;
            }

            var confirm = await ShowConfirmDialogAsync("删除确认",
                $"确定要删除预设 \"{_selectedPreset.Name}\" 吗？");
            if (!confirm) return;

            PresetManagerService.DeleteUserPreset(_selectedPreset.Name);
            _selectedPreset = null;
            RefreshList();
        }

        private void ApplyClose_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedPreset == null)
            {
                ShowWarning("请先选择一个预设再关闭");
                return;
            }

            AppliedPreset = _selectedPreset.Data;
            Close();
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        // ── 对话框辅助 ──

        private async Task<string?> ShowInputDialogAsync(string title, string message)
        {
            var tcs = new TaskCompletionSource<string?>();

            var dialog = new Window
            {
                Title = title,
                Width = 360,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
                    Margin = new Avalonia.Thickness(16),
                    Children =
                    {
                        new TextBlock { Text = message, FontSize = 13, Margin = new Avalonia.Thickness(0,0,0,12),
                            [Grid.RowProperty] = 0 },
                        new TextBox { Name = "InputBox", FontSize = 13,
                            [Grid.RowProperty] = 1, Margin = new Avalonia.Thickness(0,0,0,12) },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 8,
                            [Grid.RowProperty] = 2,
                            Children =
                            {
                                new Button { Content = "确定", Padding = new Avalonia.Thickness(12,4), IsDefault = true },
                                new Button { Content = "取消", Padding = new Avalonia.Thickness(12,4), IsCancel = true }
                            }
                        }
                    }
                }
            };

            var inputBox = (dialog.Content as Grid)?.Children
                .OfType<TextBox>().FirstOrDefault(t => t.Name == "InputBox");

            var buttons = ((dialog.Content as Grid)?.Children[2] as StackPanel)?.Children;
            if (buttons != null && buttons.Count >= 2)
            {
                ((Button)buttons[0]).Click += (_, _) =>
                {
                    tcs.TrySetResult(inputBox?.Text);
                    dialog.Close();
                };
                ((Button)buttons[1]).Click += (_, _) =>
                {
                    tcs.TrySetResult(null);
                    dialog.Close();
                };
            }

            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        private async Task<bool> ShowConfirmDialogAsync(string title, string message)
        {
            var tcs = new TaskCompletionSource<bool>();

            var dialog = new Window
            {
                Title = title,
                Width = 340,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new Grid
                {
                    RowDefinitions = new RowDefinitions("*,Auto"),
                    Margin = new Avalonia.Thickness(16),
                    Children =
                    {
                        new TextBlock { Text = message, FontSize = 13, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            [Grid.RowProperty] = 0, Margin = new Avalonia.Thickness(0,0,0,12) },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 8,
                            [Grid.RowProperty] = 1,
                            Children =
                            {
                                new Button { Content = "确定", Padding = new Avalonia.Thickness(12,4), IsDefault = true },
                                new Button { Content = "取消", Padding = new Avalonia.Thickness(12,4), IsCancel = true }
                            }
                        }
                    }
                }
            };

            var buttons = ((dialog.Content as Grid)?.Children[1] as StackPanel)?.Children;
            if (buttons != null && buttons.Count >= 2)
            {
                ((Button)buttons[0]).Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
                ((Button)buttons[1]).Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
            }

            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        private async void ShowWarning(string message)
        {
            await ShowConfirmDialogAsync("提示", message);
        }
    }
}
