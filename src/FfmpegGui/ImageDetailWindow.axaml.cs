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
            Title = string.Format(LocalizationService.Instance["detail.title.file"], Path.GetFileName(filePath));
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
            var loc = LocalizationService.Instance;
            SetText("InfoFileName", m.FileName);
            SetText("InfoFullPath", m.FullPath);
            SetText("InfoFormat", string.IsNullOrWhiteSpace(m.Format) ? loc["detail.unknown"] : m.Format.ToUpper());
            SetText("InfoFileSize", MediaInfoParser.FormatSize(m.FileSize));
            SetText("InfoModified", m.LastModified.ToString("yyyy-MM-dd HH:mm:ss"));

            if (m.Width > 0)
            {
                var mp = (m.Width * m.Height) / 1_000_000.0;
                SetText("InfoResolution", $"{m.Width} × {m.Height} ({mp:F1} MP)");
            }
            SetText("InfoBitDepth", m.BitDepth > 0 ? $"{m.BitDepth} bits" : loc["detail.unknown"]);
            SetText("InfoPixFmt", string.IsNullOrWhiteSpace(m.PixelFormat) ? "—" : m.PixelFormat);
            SetText("InfoColor", MediaInfoParser.FormatColorInfo(m));
            SetText("InfoCodec", string.IsNullOrWhiteSpace(m.CodecName) ? "—" : m.CodecName);

            // ICC
            if (!string.IsNullOrWhiteSpace(m.IccDescription))
                SetText("InfoIcc", string.Format(loc["detail.icc.embedded"], m.IccDescription) + (m.IccSize > 0 ? $" ({m.IccSize} bytes)" : ""));
            else
                SetText("InfoIcc", loc["detail.icc.none"]);

            // 编码参数（仅队列项有）
            if (_queueItem != null)
            {
                var card = this.FindControl<Border>("EncodeParamsCard");
                if (card != null) card.IsVisible = true;
                SetText("InfoQuality", m.Quality?.ToString() ?? "—");
                SetText("InfoChroma", m.Chroma ?? "auto");
                SetText("InfoEncoder", $"{m.Encoder ?? "ffmpeg"} ({m.EncoderBackend ?? "FFmpeg"})");
                SetText("InfoLossless", m.IsLossless ? loc["detail.lossless.yes"] : loc["detail.lossless.no"]);

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

            SetMetaStatus(string.Format(LocalizationService.Instance["detail.meta.fieldcount"], _metaBoxes.Count));
            var fileLabel = this.FindControl<TextBlock>("MetaFileLabel");
            if (fileLabel != null) fileLabel.Text = $"📁 {_filePath}";
        }

        private static string GetCategoryTitle(ExifToolService.MetadataCategory cat)
        {
            var loc = LocalizationService.Instance;
            return cat switch
            {
                ExifToolService.MetadataCategory.基本信息 => loc["detail.category.basic"],
                ExifToolService.MetadataCategory.日期时间 => loc["detail.category.datetime"],
                ExifToolService.MetadataCategory.相机信息 => loc["detail.category.camera"],
                ExifToolService.MetadataCategory.拍摄参数 => loc["detail.category.shooting"],
                ExifToolService.MetadataCategory.GPS位置 => loc["detail.category.gps"],
                ExifToolService.MetadataCategory.图片属性 => loc["detail.category.imageprops"],
                ExifToolService.MetadataCategory.IPTC信息 => loc["detail.category.iptc"],
                ExifToolService.MetadataCategory.XMP信息 => loc["detail.category.xmp"],
                ExifToolService.MetadataCategory.色彩配置 => loc["detail.category.color"],
                _ => cat.ToString()
            };
        }

        private async void ReadMeta_Click(object? sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            if (!File.Exists(_filePath)) { SetMetaStatus(loc["detail.err.filemissing"]); return; }
            SetMetaStatus(loc["detail.status.reading"]);
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
                SetMetaStatus(string.Format(loc["detail.status.readok"], count));
            }
            catch (Exception ex)
            {
                SetMetaStatus(string.Format(loc["detail.err.readfail"], ex.Message));
            }
        }

        private async void SaveMeta_Click(object? sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            if (!File.Exists(_filePath)) { SetMetaStatus(loc["detail.err.filemissing"]); return; }
            SetMetaStatus(loc["detail.status.saving"]);
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
                    SetMetaStatus(string.Format(loc["detail.status.saveok"], count));
                }
                else
                    SetMetaStatus(string.Format(loc["detail.err.writefail"], exitCode));
            }
            catch (Exception ex)
            {
                SetMetaStatus(string.Format(loc["detail.err.savefail"], ex.Message));
            }
        }

        private void UndoMeta_Click(object? sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            if (_metaBackup.Count == 0) { SetMetaStatus(loc["detail.status.noundo"]); return; }
            foreach (var (name, val) in _metaBackup)
            {
                if (_metaBoxes.TryGetValue(name, out var tb))
                    tb.Text = val;
            }
            SetMetaStatus(loc["detail.status.undone"]);
        }

        private void ClearMeta_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var (_, tb) in _metaBoxes) tb.Text = "";
            SetMetaStatus(LocalizationService.Instance["detail.status.cleared"]);
        }

        private async void EmbedIcc_Click(object? sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            if (!File.Exists(_filePath)) { SetMetaStatus(loc["detail.err.filemissing"]); return; }
            if (!ExifToolService.IsAvailable) { SetMetaStatus(loc["detail.err.noexiftool.icc"]); return; }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = loc["detail.dlg.selecticc"],
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(loc["detail.filetype.icc"]) { Patterns = new[] { "*.icc", "*.icm" } },
                    new Avalonia.Platform.Storage.FilePickerFileType(loc["detail.filetype.all"]) { Patterns = new[] { "*" } }
                }
            });

            if (files == null || files.Count == 0) return;
            var iccPath = files[0].Path.LocalPath;

            SetMetaStatus(loc["detail.status.embedding"]);
            try
            {
                var exit = await Task.Run(() =>
                    ExifToolService.EmbedIccProfileFromFileAsync(iccPath, _filePath, null));
                if (exit == 0)
                    SetMetaStatus(string.Format(loc["detail.status.embedok"], Path.GetFileName(iccPath)));
                else
                    SetMetaStatus(string.Format(loc["detail.err.embedfail"], exit));
            }
            catch (Exception ex)
            {
                SetMetaStatus(string.Format(loc["detail.err.embedexc"], ex.Message));
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
