using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FfmpegGui.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FfmpegGui.Controls
{
    public partial class MetadataEditor : UserControl
    {
        // ── 属性 ──
        public static readonly StyledProperty<string> FilePathProperty =
            AvaloniaProperty.Register<MetadataEditor, string>(nameof(FilePath), defaultValue: string.Empty);

        public string FilePath
        {
            get => GetValue(FilePathProperty);
            set => SetValue(FilePathProperty, value);
        }

        /// <summary>备份值：显示名 → 上次读取/保存的值</summary>
        private readonly Dictionary<string, string> _backupValues = new();

        /// <summary>动态生成的文本框：显示名 → TextBox</summary>
        private readonly Dictionary<string, TextBox> _fieldBoxes = new();

        private bool _isExpanded;
        private bool _fieldsBuilt;

        public MetadataEditor()
        {
            InitializeComponent();
        }

        /// <summary>切换展开/折叠，首次展开时构建字段</summary>
        private void ToggleExpand_Click(object? sender, RoutedEventArgs e)
        {
            _isExpanded = !_isExpanded;
            if (EditorPanel != null) EditorPanel.IsVisible = _isExpanded;
            if (ToggleIcon != null) ToggleIcon.Text = _isExpanded ? "▼" : "▶";

            if (_isExpanded && !_fieldsBuilt)
            {
                BuildFields();
                _fieldsBuilt = true;
            }
        }

        /// <summary>动态生成所有元数据编辑字段（按分类分组）</summary>
        private void BuildFields()
        {
            if (FieldsPanel == null) return;
            FieldsPanel.Children.Clear();
            _fieldBoxes.Clear();

            var grouped = ExifToolService.MetadataFields
                .GroupBy(kv => kv.Value.Category)
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                // 分类标题
                var header = new TextBlock
                {
                    Text = GetCategoryTitle(group.Key),
                    FontWeight = FontWeight.Bold,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                FieldsPanel.Children.Add(header);

                // 该分类下的字段网格
                var fields = group.ToList();
                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("80,*"),
                    Margin = new Thickness(4, 0, 0, 0)
                };

                for (int i = 0; i < fields.Count; i++)
                {
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var (displayName, _) = fields[i];

                    var label = new TextBlock
                    {
                        Text = displayName,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 11,
                        Margin = new Thickness(0, 2)
                    };
                    Grid.SetRow(label, i);
                    Grid.SetColumn(label, 0);
                    grid.Children.Add(label);

                    var textBox = new TextBox
                    {
                        FontSize = 11,
                        Margin = new Thickness(4, 1),
                        PlaceholderText = displayName
                    };
                    Grid.SetRow(textBox, i);
                    Grid.SetColumn(textBox, 1);
                    grid.Children.Add(textBox);

                    _fieldBoxes[displayName] = textBox;
                }

                FieldsPanel.Children.Add(grid);
            }

            UpdateFieldCount();
        }

        private static string GetCategoryTitle(ExifToolService.MetadataCategory cat) => cat switch
        {
            ExifToolService.MetadataCategory.基本信息 => "📋 基本信息",
            ExifToolService.MetadataCategory.日期时间 => "📅 日期时间",
            ExifToolService.MetadataCategory.相机信息 => "📷 相机信息",
            ExifToolService.MetadataCategory.拍摄参数 => "⚙️ 拍摄参数",
            ExifToolService.MetadataCategory.GPS位置 => "📍 GPS 位置",
            ExifToolService.MetadataCategory.图片属性 => "🖼 图片属性",
            ExifToolService.MetadataCategory.IPTC信息 => "📰 IPTC 信息",
            ExifToolService.MetadataCategory.XMP信息 => "🏷️ XMP 信息",
            ExifToolService.MetadataCategory.色彩配置 => "🎨 色彩配置",
            _ => cat.ToString()
        };

        private void UpdateFieldCount()
        {
            if (FieldCountLabel != null)
                FieldCountLabel.Text = $"({_fieldBoxes.Count} 个字段)";
        }

        /// <summary>读取文件现有元数据并填入编辑框</summary>
        private async void ReadMetadata_Click(object? sender, RoutedEventArgs e)
        {
            SetStatus("⏳ 正在读取元数据...", "gray");
            try
            {
                var filePath = ResolveFilePath();
                if (!File.Exists(filePath))
                {
                    SetStatus("❌ 文件不存在，请先完成转码。", "red");
                    return;
                }

                var metadata = await Task.Run(() => ExifToolService.ReadMetadataAsync(filePath));

                foreach (var (displayName, textBox) in _fieldBoxes)
                {
                    if (metadata.TryGetValue(displayName, out var value))
                        textBox.Text = value ?? "";
                }

                SaveBackup();
                var count = _fieldBoxes.Count(kv => !string.IsNullOrWhiteSpace(kv.Value.Text));
                SetStatus($"✅ 读取成功，共 {count} 个字段有值", "green");
            }
            catch (Exception ex)
            {
                SetStatus($"❌ 读取失败: {ex.Message}", "red");
            }
        }

        /// <summary>将编辑框内容写入文件</summary>
        private async void ApplyMetadata_Click(object? sender, RoutedEventArgs e)
        {
            SetStatus("⏳ 正在写入元数据...", "gray");
            try
            {
                var filePath = ResolveFilePath();
                if (!File.Exists(filePath))
                {
                    SetStatus("❌ 文件不存在，请先完成转码。", "red");
                    return;
                }

                var tags = new Dictionary<string, string>();
                foreach (var (displayName, textBox) in _fieldBoxes)
                {
                    tags[displayName] = textBox.Text?.Trim() ?? "";
                }

                var (exitCode, output) = await Task.Run(() =>
                    ExifToolService.WriteMetadataAsync(filePath, tags, keepBackup: true));

                if (exitCode == 0)
                {
                    SaveBackup();
                    var count = _fieldBoxes.Count(kv => !string.IsNullOrWhiteSpace(kv.Value.Text));
                    SetStatus($"✅ 已保存 {count} 个字段", "green");
                }
                else
                {
                    SetStatus($"⚠️ exiftool 退出码 {exitCode}:\n{output}", "red");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"❌ 写入失败: {ex.Message}", "red");
            }
        }

        /// <summary>还原到上次读取/保存时的值</summary>
        private void RestoreMetadata_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var (displayName, textBox) in _fieldBoxes)
            {
                if (_backupValues.TryGetValue(displayName, out var value))
                    textBox.Text = value;
            }
            SetStatus("已还原到上次读取/保存的值", "gray");
        }

        /// <summary>清空所有编辑框</summary>
        private void ClearAll_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var (_, textBox) in _fieldBoxes)
                textBox.Text = "";
            SetStatus("已清空所有字段（未保存到文件）", "gray");
        }

        /// <summary>嵌入外部 ICC 配置文件</summary>
        private async void EmbedIcc_Click(object? sender, RoutedEventArgs e)
        {
            var filePath = ResolveFilePath();
            if (!File.Exists(filePath)) { SetStatus("❌ 文件不存在", "red"); return; }
            if (!ExifToolService.IsAvailable) { SetStatus("❌ exiftool 未检测到", "red"); return; }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "选择 ICC 配置文件",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("ICC 配置文件") { Patterns = new[] { "*.icc", "*.icm" } },
                    new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });

            if (files == null || files.Count == 0) return;
            var iccPath = files[0].Path.LocalPath;

            SetStatus("⏳ 正在嵌入 ICC...", "gray");
            try
            {
                var exit = await Task.Run(() =>
                    ExifToolService.EmbedIccProfileFromFileAsync(iccPath, filePath, null));
                if (exit == 0)
                {
                    SetStatus($"✅ ICC 已嵌入: {Path.GetFileName(iccPath)}", "green");
                    // 更新 ICC 配置分类中的显示
                    if (_fieldBoxes.TryGetValue("ICC 配置文件", out var iccBox))
                        iccBox.Text = $"已嵌入: {Path.GetFileName(iccPath)}";
                }
                else
                    SetStatus($"⚠️ 嵌入失败（退出码 {exit}）", "red");
            }
            catch (Exception ex)
            {
                SetStatus($"❌ 嵌入失败: {ex.Message}", "red");
            }
        }

        // ── 辅助方法 ──

        private string ResolveFilePath()
        {
            var path = FilePath;
            if (!string.IsNullOrWhiteSpace(path)) return path;
            if (DataContext is Models.QueueItem item) return item.OutputPath;
            return "";
        }

        private void SaveBackup()
        {
            _backupValues.Clear();
            foreach (var (displayName, textBox) in _fieldBoxes)
                _backupValues[displayName] = textBox.Text ?? "";
        }

        private void SetStatus(string message, string color)
        {
            if (StatusText == null) return;
            StatusText.Text = message;
            StatusText.Foreground = color switch
            {
                "red"   => Brushes.Red,
                "green" => Brushes.Green,
                _       => Brushes.Gray
            };
        }
    }
}
