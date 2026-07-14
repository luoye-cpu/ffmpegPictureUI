using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FfmpegGui.Models;
using FfmpegGui.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FfmpegGui
{
    public partial class ImageDetailWindow : Window
    {
        private readonly string _filePath;
        private readonly QueueItem? _queueItem;
        private MediaInfoModel? _mediaInfo;

        // 元数据编辑状态
        private readonly Dictionary<string, TextBox> _metaBoxes = new();
        private Dictionary<string, string> _metaBackup = new();

        public ImageDetailWindow()
        {
            InitializeComponent();
            _filePath = "";
        }

        public ImageDetailWindow(string filePath, QueueItem? queueItem = null)
        {
            InitializeComponent();
            _filePath = filePath;
            _queueItem = queueItem;
            Title = $"📋 图片详情 — {Path.GetFileName(filePath)}";
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object? sender, RoutedEventArgs e)
        {
            await LoadMediaInfoAsync();
            BuildMetaFields();
        }

        // ══════════════════════════════════════
        //  Tab 1: 技术信息
        // ══════════════════════════════════════

        private async Task LoadMediaInfoAsync()
        {
            try
            {
                _mediaInfo = await MediaInfoParser.ParseAsync(_filePath, _queueItem);
            }
            catch
            {
                _mediaInfo = new MediaInfoModel { FileName = Path.GetFileName(_filePath) };
            }

            var m = _mediaInfo;
            SetText("InfoFileName", m.FileName);
            SetText("InfoFullPath", m.FullPath);
            SetText("InfoFormat", string.IsNullOrWhiteSpace(m.Format) ? "未知" : m.Format.ToUpper());
            SetText("InfoFileSize", MediaInfoParser.FormatSize(m.FileSize));
            SetText("InfoModified", m.LastModified.ToString("yyyy-MM-dd HH:mm:ss"));

            if (m.Width > 0)
            {
                var mp = (m.Width * m.Height) / 1_000_000.0;
                SetText("InfoResolution", $"{m.Width} × {m.Height} ({mp:F1} MP)");
            }
            SetText("InfoBitDepth", m.BitDepth > 0 ? $"{m.BitDepth} bits" : "未知");
            SetText("InfoPixFmt", string.IsNullOrWhiteSpace(m.PixelFormat) ? "—" : m.PixelFormat);
            SetText("InfoColor", MediaInfoParser.FormatColorInfo(m));
            SetText("InfoCodec", string.IsNullOrWhiteSpace(m.CodecName) ? "—" : m.CodecName);

            // ICC
            if (!string.IsNullOrWhiteSpace(m.IccDescription))
                SetText("InfoIcc", $"📎 内嵌 ICC: {m.IccDescription}" + (m.IccSize > 0 ? $" ({m.IccSize} bytes)" : ""));
            else
                SetText("InfoIcc", "（未内嵌 ICC Profile）");

            // 编码参数（仅队列项有）
            if (_queueItem != null)
            {
                var card = this.FindControl<Border>("EncodeParamsCard");
                if (card != null) card.IsVisible = true;
                SetText("InfoQuality", m.Quality?.ToString() ?? "—");
                SetText("InfoChroma", m.Chroma ?? "auto");
                SetText("InfoEncoder", $"{m.Encoder ?? "ffmpeg"} ({m.EncoderBackend ?? "FFmpeg"})");
                SetText("InfoLossless", m.IsLossless ? "✅ 无损" : "有损");

                // 转换指令
                var cmdCard = this.FindControl<Border>("CommandCard");
                var cmdBox = this.FindControl<TextBox>("InfoCommand");
                if (cmdCard != null && cmdBox != null && !string.IsNullOrWhiteSpace(_queueItem.Command))
                {
                    cmdCard.IsVisible = true;
                    cmdBox.Text = _queueItem.Command;
                }

                // 运行日志
                var logCard = this.FindControl<Border>("LogCard");
                var logBox = this.FindControl<TextBox>("InfoLog");
                if (logCard != null && logBox != null && !string.IsNullOrWhiteSpace(_queueItem.Log))
                {
                    logCard.IsVisible = true;
                    logBox.Text = _queueItem.Log;
                }
            }

            // 质量分析
            if (m.Ssim.HasValue || m.Psnr.HasValue)
            {
                var card = this.FindControl<Border>("QualityCard");
                if (card != null) card.IsVisible = true;
                SetText("InfoSsim", m.Ssim.HasValue ? $"{m.Ssim:F4}" : "—");
                SetText("InfoPsnr", m.Psnr.HasValue ? $"{m.Psnr:F2} dB" : "—");
            }
        }

        // ══════════════════════════════════════
        //  Tab 2: 元数据编辑
        // ══════════════════════════════════════

        private void BuildMetaFields()
        {
            var panel = this.FindControl<StackPanel>("MetaFieldsPanel");
            if (panel == null) return;
            panel.Children.Clear();
            _metaBoxes.Clear();

            var grouped = ExifToolService.MetadataFields
                .GroupBy(kv => kv.Value.Category)
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                var header = new TextBlock
                {
                    Text = GetCategoryTitle(group.Key),
                    FontWeight = FontWeight.Bold,
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 2)
                };
                panel.Children.Add(header);

                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("90,*"),
                    Margin = new Thickness(8, 0, 0, 0)
                };

                int row = 0;
                foreach (var (displayName, _) in group)
                {
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                    var label = new TextBlock
                    {
                        Text = displayName,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 11,
                        Margin = new Thickness(0, 2)
                    };
                    Grid.SetRow(label, row); Grid.SetColumn(label, 0);
                    grid.Children.Add(label);

                    var tb = new TextBox
                    {
                        FontSize = 11,
                        Margin = new Thickness(4, 1),
                        PlaceholderText = displayName
                    };
                    Grid.SetRow(tb, row); Grid.SetColumn(tb, 1);
                    grid.Children.Add(tb);

                    _metaBoxes[displayName] = tb;
                    row++;
                }
                panel.Children.Add(grid);
            }

            SetMetaStatus($"共 {_metaBoxes.Count} 个字段（点击「读取元数据」加载）");
            var fileLabel = this.FindControl<TextBlock>("MetaFileLabel");
            if (fileLabel != null) fileLabel.Text = $"📁 {_filePath}";
        }

        private static string GetCategoryTitle(ExifToolService.MetadataCategory cat) => cat switch
        {
            ExifToolService.MetadataCategory.基本信息 => "📋 基本信息",
            ExifToolService.MetadataCategory.日期时间 => "📅 日期时间",
            ExifToolService.MetadataCategory.相机信息 => "📷 相机信息",
            ExifToolService.MetadataCategory.拍摄参数 => "⚙ 拍摄参数",
            ExifToolService.MetadataCategory.GPS位置 => "📍 GPS 位置",
            ExifToolService.MetadataCategory.图片属性 => "🖼 图片属性",
            ExifToolService.MetadataCategory.IPTC信息 => "📰 IPTC 信息",
            ExifToolService.MetadataCategory.XMP信息 => "🏷 XMP 信息",
            ExifToolService.MetadataCategory.色彩配置 => "🎨 色彩配置",
            _ => cat.ToString()
        };

        private async void ReadMeta_Click(object? sender, RoutedEventArgs e)
        {
            if (!File.Exists(_filePath)) { SetMetaStatus("❌ 文件不存在"); return; }
            SetMetaStatus("⏳ 正在读取...");
            try
            {
                var data = await Task.Run(() => ExifToolService.ReadMetadataAsync(_filePath));
                foreach (var (name, tb) in _metaBoxes)
                {
                    if (data.TryGetValue(name, out var val))
                        tb.Text = val ?? "";
                }
                _metaBackup = _metaBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text ?? "");
                var count = _metaBoxes.Count(kv => !string.IsNullOrWhiteSpace(kv.Value.Text));
                SetMetaStatus($"✅ 读取成功，{count} 个字段有值");
            }
            catch (Exception ex)
            {
                SetMetaStatus($"❌ 读取失败: {ex.Message}");
            }
        }

        private async void SaveMeta_Click(object? sender, RoutedEventArgs e)
        {
            if (!File.Exists(_filePath)) { SetMetaStatus("❌ 文件不存在"); return; }
            SetMetaStatus("⏳ 正在保存...");
            try
            {
                var tags = new Dictionary<string, string>();
                foreach (var (name, tb) in _metaBoxes)
                {
                    var val = tb.Text?.Trim() ?? "";
                    tags[name] = val;
                }
                var (exitCode, output) = await Task.Run(() =>
                    ExifToolService.WriteMetadataAsync(_filePath, tags, keepBackup: true));
                if (exitCode == 0)
                {
                    _metaBackup = _metaBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text ?? "");
                    var count = tags.Count(kv => !string.IsNullOrWhiteSpace(kv.Value));
                    SetMetaStatus($"✅ 已保存 {count} 个字段");
                }
                else
                    SetMetaStatus($"⚠️ 写入失败（退出码 {exitCode}）");
            }
            catch (Exception ex)
            {
                SetMetaStatus($"❌ 保存失败: {ex.Message}");
            }
        }

        private void UndoMeta_Click(object? sender, RoutedEventArgs e)
        {
            if (_metaBackup.Count == 0) { SetMetaStatus("无撤销数据"); return; }
            foreach (var (name, val) in _metaBackup)
            {
                if (_metaBoxes.TryGetValue(name, out var tb))
                    tb.Text = val;
            }
            SetMetaStatus("已还原到上次读取/保存的状态");
        }

        private void ClearMeta_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var (_, tb) in _metaBoxes) tb.Text = "";
            SetMetaStatus("已清空所有字段（未保存到文件）");
        }

        private async void EmbedIcc_Click(object? sender, RoutedEventArgs e)
        {
            if (!File.Exists(_filePath)) { SetMetaStatus("❌ 文件不存在"); return; }
            if (!ExifToolService.IsAvailable) { SetMetaStatus("❌ exiftool 未检测到，无法嵌入 ICC"); return; }

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

            SetMetaStatus("⏳ 正在嵌入 ICC...");
            try
            {
                var exit = await Task.Run(() =>
                    ExifToolService.EmbedIccProfileFromFileAsync(iccPath, _filePath, null));
                if (exit == 0)
                    SetMetaStatus($"✅ ICC 已嵌入: {Path.GetFileName(iccPath)}");
                else
                    SetMetaStatus($"⚠️ 嵌入失败（退出码 {exit}）");
            }
            catch (Exception ex)
            {
                SetMetaStatus($"❌ 嵌入失败: {ex.Message}");
            }
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();

        // ── 辅助 ──

        private void SetText(string name, string value)
        {
            var ctrl = this.FindControl<TextBlock>(name);
            if (ctrl != null) ctrl.Text = value;
        }

        private void SetMetaStatus(string msg)
        {
            var ctrl = this.FindControl<TextBlock>("MetaStatus");
            if (ctrl != null)
            {
                ctrl.Text = msg;
                ctrl.Foreground = msg.StartsWith("❌") ? Brushes.Red
                    : msg.StartsWith("⚠") ? Brushes.Orange
                    : msg.StartsWith("✅") ? Brushes.Green
                    : Brushes.Gray;
            }
        }
    }
}
