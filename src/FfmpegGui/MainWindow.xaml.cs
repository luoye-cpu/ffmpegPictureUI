using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.ObjectModel;
using System.IO;
using FfmpegGui.Models;
using FfmpegGui.Services;
using System.Threading.Tasks;

namespace FfmpegGui
{
    public partial class MainWindow : Window
    {
        private string? _inputPath;
        private string? _outputPath;
        private FormatCapabilities? _currentCapabilities;
        private readonly ObservableCollection<string> _queueView = new();
        private readonly QueueProcessor _queueProcessor;
        private bool _initialized;

        private ComboBox? FormatCombo;
        private ComboBox? EncoderCombo;
        private Slider? QualitySlider;
        private TextBlock? QualityValue;
        private ComboBox? ChromaCombo;
        private ComboBox? ColorSpaceCombo;
        private ComboBox? ColorPrimariesCombo;
        private ComboBox? ColorTrcCombo;
        private ComboBox? ColorMatrixCombo;
        private CheckBox? UseAdvancedColor;
        private StackPanel? AdvancedColorPanel;
        private ComboBox? BitDepthCombo;
        private NumericUpDown? ThreadsBox;
        private CheckBox? PreserveMetadata;
        private CheckBox? LosslessCheck;
        private ListBox? QueueList;
        private NumericUpDown? ConcurrencyBox;
        private TextBox? CommandText;
        private TextBox? MediaInfoText;
        private TextBox? LogText;
        private TextBox? FfmpegPathBox;
        private TextBox? OutputDirBox;
        private CheckBox? AutoThreadsCheck;
        private CheckBox? SingleThreadCheck;
        private TextBlock? ThreadHintLabel;
        private TextBlock? ConcurrencyLabel;
        private TextBlock? QueueCountLabel;
        private CheckBox? UseAdvancedCodec;
        private StackPanel? AdvancedCodecPanel;
        private StackPanel? PngCodecPanel;
        private StackPanel? WebpCodecPanel;
        private StackPanel? AvifCodecPanel;
        private StackPanel? JxlCodecPanel;
        private StackPanel? JpegCodecPanel;
        private StackPanel? TiffCodecPanel;
        private ComboBox? PngPredCombo;
        private ComboBox? WebpPresetCombo;
        private NumericUpDown? AvifCpuUsedBox;
        private CheckBox? AvifStillPictureCheck;
        private ComboBox? AvifTuneCombo;
        private ComboBox? AvifPresetCombo;
        private NumericUpDown? JxlEffortBox;
        private CheckBox? JxlModularCheck;
        private ComboBox? JpegHuffmanCombo;
        private ComboBox? TiffCompressionCombo;
        private Border? DropZone;
        private TextBlock? DropHint;
        private Border? MediaDropZone;
        private TextBlock? FileCountLabel;
        private ListBox? MediaFileList;
        private TextBlock? MediaFileCount;
        private readonly List<string> _selectedFiles = new();
        private readonly ObservableCollection<string> _mediaFiles = new();
        private readonly List<Models.QueueItem> _queueItems = new();

        public MainWindow()
        {
            InitializeComponent();
            _queueProcessor = new QueueProcessor(OnQueueItemUpdated);
            Opened += (_, _) => InitControls();
        }

        private void InitControls()
        {
            if (_initialized) return;
            _initialized = true;

            // 获取 UI 控件引用
            FormatCombo = this.FindControl<ComboBox>("FormatCombo");
            EncoderCombo = this.FindControl<ComboBox>("EncoderCombo");
            QualitySlider = this.FindControl<Slider>("QualitySlider");
            QualityValue = this.FindControl<TextBlock>("QualityValue");
            ChromaCombo = this.FindControl<ComboBox>("ChromaCombo");
            ColorSpaceCombo = this.FindControl<ComboBox>("ColorSpaceCombo");
            ColorPrimariesCombo = this.FindControl<ComboBox>("ColorPrimariesCombo");
            ColorTrcCombo = this.FindControl<ComboBox>("ColorTrcCombo");
            ColorMatrixCombo = this.FindControl<ComboBox>("ColorMatrixCombo");
            UseAdvancedColor = this.FindControl<CheckBox>("UseAdvancedColor");
            AdvancedColorPanel = this.FindControl<StackPanel>("AdvancedColorPanel");
            BitDepthCombo = this.FindControl<ComboBox>("BitDepthCombo");
            ThreadsBox = this.FindControl<NumericUpDown>("ThreadsBox");
            PreserveMetadata = this.FindControl<CheckBox>("PreserveMetadata");
            LosslessCheck = this.FindControl<CheckBox>("LosslessCheck");
            QueueList = this.FindControl<ListBox>("QueueList");
            ConcurrencyBox = this.FindControl<NumericUpDown>("ConcurrencyBox");
            ConcurrencyLabel = this.FindControl<TextBlock>("ConcurrencyLabel");
            CommandText = this.FindControl<TextBox>("CommandText");
            MediaInfoText = this.FindControl<TextBox>("MediaInfoText");
            LogText = this.FindControl<TextBox>("LogText");
            FfmpegPathBox = this.FindControl<TextBox>("FfmpegPathBox");
            OutputDirBox = this.FindControl<TextBox>("OutputDirBox");
            AutoThreadsCheck = this.FindControl<CheckBox>("AutoThreadsCheck");
            SingleThreadCheck = this.FindControl<CheckBox>("SingleThreadCheck");
            ThreadHintLabel = this.FindControl<TextBlock>("ThreadHintLabel");
            UseAdvancedCodec = this.FindControl<CheckBox>("UseAdvancedCodec");
            AdvancedCodecPanel = this.FindControl<StackPanel>("AdvancedCodecPanel");
            PngCodecPanel = this.FindControl<StackPanel>("PngCodecPanel");
            WebpCodecPanel = this.FindControl<StackPanel>("WebpCodecPanel");
            AvifCodecPanel = this.FindControl<StackPanel>("AvifCodecPanel");
            JxlCodecPanel = this.FindControl<StackPanel>("JxlCodecPanel");
            JpegCodecPanel = this.FindControl<StackPanel>("JpegCodecPanel");
            TiffCodecPanel = this.FindControl<StackPanel>("TiffCodecPanel");
            PngPredCombo = this.FindControl<ComboBox>("PngPredCombo");
            WebpPresetCombo = this.FindControl<ComboBox>("WebpPresetCombo");
            AvifCpuUsedBox = this.FindControl<NumericUpDown>("AvifCpuUsedBox");
            AvifStillPictureCheck = this.FindControl<CheckBox>("AvifStillPictureCheck");
            AvifTuneCombo = this.FindControl<ComboBox>("AvifTuneCombo");
            AvifPresetCombo = this.FindControl<ComboBox>("AvifPresetCombo");
            JxlEffortBox = this.FindControl<NumericUpDown>("JxlEffortBox");
            JxlModularCheck = this.FindControl<CheckBox>("JxlModularCheck");
            JpegHuffmanCombo = this.FindControl<ComboBox>("JpegHuffmanCombo");
            TiffCompressionCombo = this.FindControl<ComboBox>("TiffCompressionCombo");
            DropZone = this.FindControl<Border>("DropZone");
            DropHint = this.FindControl<TextBlock>("DropHint");
            MediaDropZone = this.FindControl<Border>("MediaDropZone");
            FileCountLabel = this.FindControl<TextBlock>("FileCountLabel");
            MediaFileList = this.FindControl<ListBox>("MediaFileList");
            MediaFileCount = this.FindControl<TextBlock>("MediaFileCount");

            // 设置绑定和初始值
            if (FormatCombo != null) FormatCombo.SelectedIndex = 0;
            if (ChromaCombo != null) ChromaCombo.SelectedIndex = 0; // auto
            if (BitDepthCombo != null) BitDepthCombo.SelectedIndex = 0; // 8
            if (ColorSpaceCombo != null) ColorSpaceCombo.SelectedIndex = 0; // auto
            if (ColorPrimariesCombo != null) ColorPrimariesCombo.SelectedIndex = 0;
            if (ColorTrcCombo != null) ColorTrcCombo.SelectedIndex = 0;
            if (ColorMatrixCombo != null) ColorMatrixCombo.SelectedIndex = 0;

            if (QueueList != null) QueueList.ItemsSource = _queueView;

            // 注册事件
            if (ColorSpaceCombo != null) ColorSpaceCombo.SelectionChanged += ColorSpaceCombo_SelectionChanged;
            
            // 线程复选框互斥逻辑
            if (AutoThreadsCheck != null)
            {
                AutoThreadsCheck.IsChecked = true;
                AutoThreadsCheck.IsCheckedChanged += (_, _) => UpdateThreadControls();
            }
            if (SingleThreadCheck != null)
            {
                SingleThreadCheck.IsCheckedChanged += (_, _) => UpdateThreadControls();
            }
            UpdateThreadControls();

            // 队列计数 + 并发数标签更新
            _queueView.CollectionChanged += (_, _) => UpdateQueueCountLabel();
            if (ConcurrencyBox != null)
            {
                ConcurrencyBox.ValueChanged += (_, _) => UpdateConcurrencyLabel();
                UpdateConcurrencyLabel();
            }

            // 队列项双击 → 打开详情窗口
            if (QueueList != null)
                QueueList.DoubleTapped += QueueList_DoubleTapped;

            // 媒体文件列表 — 双击查看详情
            if (MediaFileList != null)
            {
                MediaFileList.ItemsSource = _mediaFiles;
                MediaFileList.DoubleTapped += MediaFileList_DoubleTapped;
            }

            // 拖放支持 — 集成到媒体信息框
            if (MediaDropZone != null)
            {
                DragDrop.SetAllowDrop(MediaDropZone, true);
                MediaDropZone.AddHandler(DragDrop.DragEnterEvent, DragEnter);
                MediaDropZone.AddHandler(DragDrop.DragLeaveEvent, DragLeave);
                MediaDropZone.AddHandler(DragDrop.DropEvent, DropHandler);
            }

            UpdateQualityLabel();
            if (QualitySlider != null) QualitySlider.PropertyChanged += (_, e) => UpdateQualityLabel();

            // 加载已保存的设置（如果有 ffmpeg 路径会自动触发能力检测）
            LoadSettings();
        }

        private void LoadSettings()
        {
            var settings = AppSettingsService.Current;
            if (!string.IsNullOrWhiteSpace(settings.FfmpegDirectory) && FfmpegPathBox != null)
                FfmpegPathBox.Text = settings.FfmpegDirectory;
            if (!string.IsNullOrWhiteSpace(settings.OutputDirectory) && OutputDirBox != null)
                OutputDirBox.Text = settings.OutputDirectory;

            // 如果已有 ffmpeg 路径，启动时自动检测能力
            if (!string.IsNullOrWhiteSpace(settings.FfmpegDirectory))
            {
                _ = FullDetectionAsync();
            }
        }

        private async Task FullDetectionAsync()
        {
            if (LogText != null) LogText.Text += "正在检测 ffmpeg 能力与可用编码器...\n";
            await FormatCapabilitiesService.InitializeAsync(AppSettingsService.Current.FfmpegPath);
            
            // 预加载所有格式的编码器
            await EncoderDetectionService.GetAllEncodersAsync(AppSettingsService.Current.FfmpegPath);
            
            await RefreshEncoderListAsync();
            if (LogText != null) LogText.Text += "能力检测完成。\n";
            UpdateOptionAvailability();
        }

        private void UpdateQualityLabel()
        {
            if (QualitySlider != null && QualityValue != null)
            {
                var fmt = FormatCombo?.SelectedItem as string ?? "jpg";
                var val = (int)QualitySlider.Value;
                QualityValue.Text = Models.FfmpegOptions.GetQualityLabel(fmt, val);
            }
            RegenerateCommand();
        }

        private void RegenerateCommand()
        {
            if (string.IsNullOrWhiteSpace(_inputPath)) return;
            var fmt = FormatCombo?.SelectedItem as string ?? "jpg";
            var chroma = ChromaCombo?.SelectedItem as string ?? "auto";
            var bitdepthStr = BitDepthCombo?.SelectedItem as string ?? "8";
            if (!int.TryParse(bitdepthStr, out var bitdepth)) bitdepth = 8;
            var encStr = EncoderCombo?.SelectedItem as string ?? "";
            var encName = encStr.Contains(" — ") ? encStr.Split(" — ")[0] : encStr;
            var useAdv = UseAdvancedColor?.IsChecked ?? false;
            var useAdvCodec = UseAdvancedCodec?.IsChecked ?? false;
            var autoTh = AutoThreadsCheck?.IsChecked ?? true;
            var singleTh = SingleThreadCheck?.IsChecked ?? false;
            int threads = singleTh ? 1 : autoTh ? Models.FfmpegOptions.ComputeAutoThreads() : (int)(ThreadsBox?.Value ?? 4);

            var opts = new Models.FfmpegOptions
            {
                Format = fmt, Quality = (int)(QualitySlider?.Value ?? 92),
                Chroma = chroma, BitDepth = bitdepth,
                ColorSpace = ColorSpaceCombo?.SelectedItem as string,
                UseAdvancedColorParameters = useAdv,
                ColorPrimaries = useAdv ? (ColorPrimariesCombo?.SelectedItem as string) : null,
                ColorTrc = useAdv ? (ColorTrcCombo?.SelectedItem as string) : null,
                ColorMatrix = useAdv ? (ColorMatrixCombo?.SelectedItem as string) : null,
                Encoder = encName, Threads = threads,
                PreserveMetadata = PreserveMetadata?.IsChecked ?? true,
                Lossless = LosslessCheck?.IsChecked ?? false,
                PngPred = useAdvCodec ? (PngPredCombo?.SelectedItem as string) : null,
                WebpPreset = useAdvCodec ? (WebpPresetCombo?.SelectedItem as string) : null,
                AvifCpuUsed = useAdvCodec ? (int?)AvifCpuUsedBox?.Value : null,
                AvifStillPicture = useAdvCodec ? AvifStillPictureCheck?.IsChecked : null,
                AvifTune = useAdvCodec ? (AvifTuneCombo?.SelectedItem as string) : null,
                AvifPreset = useAdvCodec ? (AvifPresetCombo?.SelectedItem as string) : null,
                JxlEffort = useAdvCodec ? (int?)JxlEffortBox?.Value : null,
                JxlModular = useAdvCodec ? JxlModularCheck?.IsChecked : null,
                JpegHuffman = useAdvCodec ? (JpegHuffmanCombo?.SelectedItem as string) : null,
                TiffCompressionAlgo = useAdvCodec ? (TiffCompressionCombo?.SelectedItem as string) : null
            };
            var outp = GetOutputPath(_inputPath, opts.Format);
            var args = Services.FfmpegCommandBuilder.BuildArguments(opts, _inputPath, outp);
            if (CommandText != null) CommandText.Text = "ffmpeg " + args;
        }

        private void UpdateThreadControls()
        {
            var auto = AutoThreadsCheck?.IsChecked ?? true;
            var single = SingleThreadCheck?.IsChecked ?? false;

            if (ThreadsBox != null)
            {
                ThreadsBox.IsEnabled = !auto && !single;
                if (auto)
                {
                    var val = Models.FfmpegOptions.ComputeAutoThreads();
                    ThreadsBox.Value = val;
                    if (ThreadHintLabel != null)
                        ThreadHintLabel.Text = $"(CPU {Environment.ProcessorCount} → 分配 {val})";
                }
                else if (single)
                {
                    ThreadsBox.Value = 1;
                    if (ThreadHintLabel != null)
                        ThreadHintLabel.Text = "(单线程模式)";
                }
                else
                {
                    if (ThreadHintLabel != null)
                        ThreadHintLabel.Text = "(手动)";
                }
            }

            // 互斥
            if (single && AutoThreadsCheck?.IsChecked == true)
            {
                if (AutoThreadsCheck != null) AutoThreadsCheck.IsChecked = false;
            }
            if (auto && SingleThreadCheck?.IsChecked == true)
            {
                if (SingleThreadCheck != null) SingleThreadCheck.IsChecked = false;
            }
        }

        private void UpdateQueueCountLabel()
        {
            if (QueueCountLabel != null)
                QueueCountLabel.Text = $"队列: {_queueView.Count}";
            if (FileCountLabel != null)
                FileCountLabel.Text = $"总文件: {_queueView.Count}";
        }

        private void UpdateConcurrencyLabel()
        {
            if (ConcurrencyLabel != null && ConcurrencyBox != null)
                ConcurrencyLabel.Text = $"(同时 {ConcurrencyBox.Value:0} 个任务)";
        }

        private void FormatCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateOptionAvailability();
            _ = RefreshEncoderListAsync();
        }

        private async Task RefreshEncoderListAsync()
        {
            if (EncoderCombo == null || FormatCombo == null) return;

            var fmt = (FormatCombo.SelectedItem as string ?? "jpg").ToLower();
            var encoders = await EncoderDetectionService.GetEncodersForFormatAsync(fmt);

            EncoderCombo.Items!.Clear();
            if (encoders.Count > 0)
            {
                foreach (var enc in encoders)
                {
                    EncoderCombo.Items.Add(enc.ToString());
                }

                // 尝试选中默认编码器
                var defaultEnc = EncoderDetectionService.GetDefaultEncoder(fmt);
                var idx = encoders.FindIndex(e => e.Name.Equals(defaultEnc, StringComparison.OrdinalIgnoreCase));
                EncoderCombo.SelectedIndex = idx >= 0 ? idx : 0;
                EncoderCombo.IsEnabled = true;
            }
            else
            {
                EncoderCombo.Items.Add("(未检测到可用编码器)");
                EncoderCombo.SelectedIndex = 0;
                EncoderCombo.IsEnabled = false;
            }
        }

        private void UpdateOptionAvailability()
        {
            if (FormatCombo == null) return;
            var fmt = (FormatCombo.SelectedItem as string ?? "jpg").ToLower();
            _currentCapabilities = FormatCapabilitiesService.GetCapabilities(fmt);

            if (_currentCapabilities != null)
            {
                if (QualitySlider != null)
                {
                    QualitySlider.IsEnabled = _currentCapabilities.SupportsQuality;
                    if (_currentCapabilities.SupportsQuality)
                    {
                        // 切换到该格式的视觉无损默认值
                        QualitySlider.Value = Models.FfmpegOptions.GetDefaultQuality(fmt);
                    }
                }
                if (ChromaCombo != null) ChromaCombo.IsEnabled = _currentCapabilities.SupportsChroma;
                if (BitDepthCombo != null)
                {
                    BitDepthCombo.IsEnabled = _currentCapabilities.SupportsBitDepth;
                    // 动态更新位深选项
                    BitDepthCombo.Items!.Clear();
                    foreach (var bd in _currentCapabilities.SupportedBitDepths)
                    {
                        BitDepthCombo.Items.Add(bd.ToString());
                    }
                    if (BitDepthCombo.Items.Count > 0) BitDepthCombo.SelectedIndex = 0;
                }
                if (PreserveMetadata != null) PreserveMetadata.IsEnabled = _currentCapabilities.SupportsMetadata;
                if (LosslessCheck != null)
                {
                    if (fmt is "png" or "tiff")
                    {
                        // PNG/TIFF 纯无损格式，强制勾选且锁定
                        LosslessCheck.IsEnabled = false;
                        LosslessCheck.IsChecked = true;
                    }
                    else
                    {
                        LosslessCheck.IsEnabled = _currentCapabilities.SupportsLossless;
                        if (!_currentCapabilities.SupportsLossless)
                            LosslessCheck.IsChecked = false;
                    }
                }

                if (ColorSpaceCombo != null)
                {
                    ColorSpaceCombo.Items!.Clear();
                    ColorSpaceCombo.Items.Add("auto"); // 始终保留 auto
                    foreach (var cs in _currentCapabilities.SupportedColorSpaces)
                    {
                        if (!ColorSpaceCombo.Items.Contains(cs))
                            ColorSpaceCombo.Items.Add(cs);
                    }
                    ColorSpaceCombo.IsEnabled = _currentCapabilities.SupportedColorSpaces.Count > 0;
                    if (ColorSpaceCombo.Items.Count > 0) ColorSpaceCombo.SelectedIndex = 0;
                }
            }
            else
            {
                if (QualitySlider != null) QualitySlider.IsEnabled = true;
                if (ChromaCombo != null) ChromaCombo.IsEnabled = true;
                if (BitDepthCombo != null) BitDepthCombo.IsEnabled = true;
                if (LosslessCheck != null) LosslessCheck.IsEnabled = true;
                if (ColorSpaceCombo != null) ColorSpaceCombo.IsEnabled = true;
            }

            UpdateCodecPanelVisibility(fmt);
            RegenerateCommand();
        }

        private void UpdateCodecPanelVisibility(string fmt)
        {
            // 全部隐藏
            if (PngCodecPanel != null) PngCodecPanel.IsVisible = false;
            if (WebpCodecPanel != null) WebpCodecPanel.IsVisible = false;
            if (AvifCodecPanel != null) AvifCodecPanel.IsVisible = false;
            if (JxlCodecPanel != null) JxlCodecPanel.IsVisible = false;
            if (JpegCodecPanel != null) JpegCodecPanel.IsVisible = false;
            if (TiffCodecPanel != null) TiffCodecPanel.IsVisible = false;

            // 按格式显示对应面板
            switch (fmt)
            {
                case "png": if (PngCodecPanel != null) PngCodecPanel.IsVisible = true; break;
                case "webp": if (WebpCodecPanel != null) WebpCodecPanel.IsVisible = true; break;
                case "avif": if (AvifCodecPanel != null) AvifCodecPanel.IsVisible = true; break;
                case "jxl": if (JxlCodecPanel != null) JxlCodecPanel.IsVisible = true; break;
                case "jpg": case "jpeg": if (JpegCodecPanel != null) JpegCodecPanel.IsVisible = true; break;
                case "tiff": if (TiffCodecPanel != null) TiffCodecPanel.IsVisible = true; break;
            }
        }

        private async Task InitializeCapabilitiesAsync()
        {
            if (LogText != null) LogText.Text += "正在检测本地 ffmpeg 能力...\n";
            await FormatCapabilitiesService.InitializeAsync();
            if (LogText != null) LogText.Text += "能力检测完成。\n";
            UpdateOptionAvailability();
        }

        private async void SelectFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片文件",
                AllowMultiple = false,
                FileTypeFilter = new[] 
                {
                    new FilePickerFileType("图片文件")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.tiff", "*.webp", "*.avif", "*.jxl" }
                    },
                    new FilePickerFileType("所有文件")
                    {
                        Patterns = new[] { "*" }
                    }
                }
            });

            if (files != null && files.Count > 0)
            {
                _inputPath = files[0].Path.LocalPath;
                AddToMediaFiles(files.Select(f => f.Path.LocalPath));
                UpdateMediaFileCount();
                if (LogText != null) LogText.Text += $"已选择: {_inputPath}\n";
                if (MediaInfoText != null) MediaInfoText.Text = "正在获取媒体信息...";
                var info = await MediaInfoService.GetMediaInfoAsync(_inputPath);
                if (MediaInfoText != null) MediaInfoText.Text = info;
            }
        }

        private void AddToQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // 如果有文件夹扫描结果，批量导入
            if (_selectedFiles.Count > 0)
            {
                foreach (var file in _selectedFiles)
                {
                    AddSingleToQueue(file);
                }
                if (LogText != null) LogText.Text += $"已批量添加 {_selectedFiles.Count} 个文件到队列\n";
                _selectedFiles.Clear();
                return;
            }

            if (string.IsNullOrWhiteSpace(_inputPath))
            {
                if (LogText != null) LogText.Text += "请先选择文件，再添加到队列\n";
                return;
            }

            AddSingleToQueue(_inputPath);
        }

        private void AddSingleToQueue(string inputPath)
        {
            var fmt = FormatCombo?.SelectedItem as string ?? "jpg";
            var chroma = ChromaCombo?.SelectedItem as string ?? "4:2:0";
            var bitdepthStr = BitDepthCombo?.SelectedItem as string ?? "8";
            int bitdepth = int.TryParse(bitdepthStr, out var bd) ? bd : 8;
            var encoderStr = EncoderCombo?.SelectedItem as string ?? "";
            var encoderName = encoderStr.Contains(" — ") ? encoderStr.Split(" — ")[0] : encoderStr;

            var autoThreads = AutoThreadsCheck?.IsChecked ?? true;
            var singleThread = SingleThreadCheck?.IsChecked ?? false;
            int threads = singleThread ? 1
                : autoThreads ? Models.FfmpegOptions.ComputeAutoThreads()
                : (int)(ThreadsBox?.Value ?? 4);

            var useAdvCodec = UseAdvancedCodec?.IsChecked ?? false;
            var options = new FfmpegOptions
            {
                Format = fmt,
                Quality = (int)(QualitySlider?.Value ?? 75),
                Chroma = chroma,
                BitDepth = bitdepth,
                ColorSpace = ColorSpaceCombo?.SelectedItem as string,
                Encoder = encoderName,
                Threads = threads,
                PreserveMetadata = PreserveMetadata?.IsChecked ?? true,
                Lossless = LosslessCheck?.IsChecked ?? false,
                PngPred = useAdvCodec ? (PngPredCombo?.SelectedItem as string) : null,
                WebpPreset = useAdvCodec ? (WebpPresetCombo?.SelectedItem as string) : null,
                AvifCpuUsed = useAdvCodec ? (int?)AvifCpuUsedBox?.Value : null,
                AvifStillPicture = useAdvCodec ? AvifStillPictureCheck?.IsChecked : null,
                AvifTune = useAdvCodec ? (AvifTuneCombo?.SelectedItem as string) : null,
                AvifPreset = useAdvCodec ? (AvifPresetCombo?.SelectedItem as string) : null,
                JxlEffort = useAdvCodec ? (int?)JxlEffortBox?.Value : null,
                JxlModular = useAdvCodec ? JxlModularCheck?.IsChecked : null,
                JpegHuffman = useAdvCodec ? (JpegHuffmanCombo?.SelectedItem as string) : null,
                TiffCompressionAlgo = useAdvCodec ? (TiffCompressionCombo?.SelectedItem as string) : null
            };

            var outp = GetOutputPath(inputPath, options.Format);
            var item = new Models.QueueItem { InputPath = inputPath, OutputPath = outp, Options = options };
            _queueProcessor.Add(item);
            _queueView.Add($"{Path.GetFileName(item.InputPath)} — {item.Status}");
            _queueItems.Add(item);
            if (LogText != null) LogText.Text += $"已添加到队列: {item.InputPath}\n";

            // 自动生成 ffmpeg 指令预览
            var cmdArgs = FfmpegCommandBuilder.BuildArguments(options, inputPath, outp);
            if (CommandText != null) CommandText.Text = "ffmpeg " + cmdArgs;
        }

        private void StartQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            int concurrency = (int)(ConcurrencyBox?.Value ?? 2);
            _queueProcessor.Start(concurrency);
            if (LogText != null) LogText.Text += $"队列开始，并行: {concurrency} 个任务\n";
        }

        private void StopQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _queueProcessor.Stop();
            if (LogText != null) LogText.Text += "队列已停止\n";
        }

        private void OnQueueItemUpdated(Models.QueueItem item)
        {
            Dispatcher.UIThread.Post(() =>
            {
                for (int i = 0; i < _queueView.Count; i++)
                {
                    if (_queueView[i].StartsWith(Path.GetFileName(item.InputPath)))
                    {
                        _queueView[i] = $"{Path.GetFileName(item.InputPath)} — {item.Status}";
                        break;
                    }
                }
            });
        }

        private void DeleteSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (QueueList?.SelectedIndex is int idx and >= 0 && idx < _queueItems.Count)
            {
                var item = _queueItems[idx];
                if (item.Status == "处理中")
                {
                    if (LogText != null) LogText.Text += "无法删除正在处理的任务\n";
                    return;
                }
                item.IsCancelled = true;
                _queueView.RemoveAt(idx);
                _queueItems.RemoveAt(idx);
                if (LogText != null) LogText.Text += $"已删除: {Path.GetFileName(item.InputPath)}\n";
                UpdateQueueCountLabel();
            }
        }

        private void QueueList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (QueueList?.SelectedIndex is int idx and >= 0 && idx < _queueItems.Count)
            {
                var item = _queueItems[idx];
                var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, item.OutputPath);
                var win = new ProgressWindow(item, "ffmpeg " + args);
                win.Show();
            }
        }

        private void RemoveMediaFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (MediaFileList?.SelectedIndex is int idx and >= 0 && idx < _mediaFiles.Count)
            {
                _mediaFiles.RemoveAt(idx);
                UpdateMediaFileCount();
            }
        }

        private void ClearMediaFiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _mediaFiles.Clear();
            _selectedFiles.Clear();
            UpdateMediaFileCount();
        }

        private void ClearFiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _mediaFiles.Clear();
            _selectedFiles.Clear();
            UpdateMediaFileCount();
        }

        private async void MediaFileList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (MediaFileList?.SelectedItem is string path && !string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    var info = await MediaInfoService.GetMediaInfoAsync(path);
                    // 用简单弹窗展示
                    var win = new Window
                    {
                        Title = $"媒体信息 — {System.IO.Path.GetFileName(path)}",
                        Width = 600, Height = 450,
                        WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner
                    };
                    var tb = new TextBox 
                    { 
                        Text = info, IsReadOnly = true, AcceptsReturn = true,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    };
                    win.Content = tb;
                    win.Show();
                }
                catch (Exception ex)
                {
                    if (LogText != null) LogText.Text += $"获取媒体信息失败: {ex.Message}\n";
                }
            }
        }

        private void UpdateMediaFileCount()
        {
            if (MediaFileCount != null)
                MediaFileCount.Text = $"共 {_mediaFiles.Count} 个文件";
        }

        private void AddToMediaFiles(IEnumerable<string> files)
        {
            foreach (var f in files)
            {
                if (!_mediaFiles.Contains(f))
                    _mediaFiles.Add(f);
            }
        }

        private void ColorSpaceCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateAdvancedColorControls();
        }

        private void UseAdvancedColor_Checked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (AdvancedColorPanel != null) AdvancedColorPanel.IsVisible = true;
        }

        private void UseAdvancedColor_Unchecked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (AdvancedColorPanel != null) AdvancedColorPanel.IsVisible = false;
        }

        private void UseAdvancedCodec_Checked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (AdvancedCodecPanel != null) AdvancedCodecPanel.IsVisible = true;
        }

        private void UseAdvancedCodec_Unchecked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (AdvancedCodecPanel != null) AdvancedCodecPanel.IsVisible = false;
        }

        private void UpdateAdvancedColorControls()
        {
            var sel = (ColorSpaceCombo?.SelectedItem as string ?? "BT.709").ToUpper();
            switch (sel)
            {
                case "BT.601":
                    SetComboSelection(ColorPrimariesCombo, "bt470bg");
                    SetComboSelection(ColorTrcCombo, "bt470bg");
                    SetComboSelection(ColorMatrixCombo, "bt601");
                    break;
                case "BT.709":
                    SetComboSelection(ColorPrimariesCombo, "bt709");
                    SetComboSelection(ColorTrcCombo, "bt709");
                    SetComboSelection(ColorMatrixCombo, "bt709");
                    break;
                case "BT.2020":
                    SetComboSelection(ColorPrimariesCombo, "bt2020");
                    SetComboSelection(ColorTrcCombo, "smpte2084");
                    SetComboSelection(ColorMatrixCombo, "bt2020");
                    break;
                default:
                    SetComboSelection(ColorPrimariesCombo, "bt709");
                    SetComboSelection(ColorTrcCombo, "bt709");
                    SetComboSelection(ColorMatrixCombo, "bt709");
                    break;
            }
        }

        private void SetComboSelection(ComboBox? combo, string value)
        {
            if (combo == null) return;
            for (int i = 0; i < combo.Items!.Count; i++)
            {
                if ((combo.Items[i] as string) == value)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private void BuildCommand_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_inputPath))
            {
                if (LogText != null) LogText.Text += "请先选择输入文件\n";
                return;
            }

            var fmt = FormatCombo?.SelectedItem as string ?? "jpg";
            var chroma = ChromaCombo?.SelectedItem as string ?? "4:2:0";
            var bitdepthStr = BitDepthCombo?.SelectedItem as string ?? "8";
            int bitdepth = int.TryParse(bitdepthStr, out var bd) ? bd : 8;
            var encoderStr = EncoderCombo?.SelectedItem as string ?? "";
            var encoderName = encoderStr.Contains(" — ") ? encoderStr.Split(" — ")[0] : encoderStr;

            var autoThreads = AutoThreadsCheck?.IsChecked ?? true;
            var singleThread = SingleThreadCheck?.IsChecked ?? false;
            int threads = singleThread ? 1
                : autoThreads ? Models.FfmpegOptions.ComputeAutoThreads()
                : (int)(ThreadsBox?.Value ?? 4);

            var useAdv = UseAdvancedColor?.IsChecked ?? false;
            var useAdvCodec = UseAdvancedCodec?.IsChecked ?? false;
            var options = new FfmpegOptions
            {
                Format = fmt,
                Quality = (int)(QualitySlider?.Value ?? 75),
                Chroma = chroma,
                BitDepth = bitdepth,
                ColorSpace = ColorSpaceCombo?.SelectedItem as string,
                UseAdvancedColorParameters = useAdv,
                ColorPrimaries = useAdv ? (ColorPrimariesCombo?.SelectedItem as string) : null,
                ColorTrc = useAdv ? (ColorTrcCombo?.SelectedItem as string) : null,
                ColorMatrix = useAdv ? (ColorMatrixCombo?.SelectedItem as string) : null,
                Encoder = encoderName,
                Threads = threads,
                PreserveMetadata = PreserveMetadata?.IsChecked ?? true,
                Lossless = LosslessCheck?.IsChecked ?? false,
                PngPred = useAdvCodec ? (PngPredCombo?.SelectedItem as string) : null,
                WebpPreset = useAdvCodec ? (WebpPresetCombo?.SelectedItem as string) : null,
                AvifCpuUsed = useAdvCodec ? (int?)AvifCpuUsedBox?.Value : null,
                AvifStillPicture = useAdvCodec ? AvifStillPictureCheck?.IsChecked : null,
                AvifTune = useAdvCodec ? (AvifTuneCombo?.SelectedItem as string) : null,
                AvifPreset = useAdvCodec ? (AvifPresetCombo?.SelectedItem as string) : null,
                JxlEffort = useAdvCodec ? (int?)JxlEffortBox?.Value : null,
                JxlModular = useAdvCodec ? JxlModularCheck?.IsChecked : null,
                JpegHuffman = useAdvCodec ? (JpegHuffmanCombo?.SelectedItem as string) : null,
                TiffCompressionAlgo = useAdvCodec ? (TiffCompressionCombo?.SelectedItem as string) : null
            };

            _outputPath = GetOutputPath(_inputPath, options.Format);
            var args = FfmpegCommandBuilder.BuildArguments(options, _inputPath, _outputPath);
            if (CommandText != null) CommandText.Text = "ffmpeg " + args;
        }

        private async void Run_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CommandText?.Text))
            {
                if (LogText != null) LogText.Text += "请先生成命令\n";
                return;
            }

            if (LogText != null) LogText.Text += "开始执行 ffmpeg...\n";
            var args = CommandText.Text.Replace("ffmpeg ", "");

            try
            {
                int exit = await FfmpegRunner.RunAsync(args, s =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (LogText != null) LogText.Text += s;
                    });
                }, AppSettingsService.Current.FfmpegPath);

                if (LogText != null) LogText.Text += $"ffmpeg 退出码 {exit}\n";
            }
            catch (Exception ex)
            {
                if (LogText != null) LogText.Text += $"执行失败: {ex.Message}\n";
            }
        }

        private async void BrowseFfmpeg_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 ffmpeg.exe",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件") { Patterns = new[] { "*.exe" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });

            if (files != null && files.Count > 0)
            {
                var fullPath = files[0].Path.LocalPath;
                var dir = Path.GetDirectoryName(fullPath) ?? "";
                AppSettingsService.Current.FfmpegDirectory = dir;
                AppSettingsService.Save();
                EncoderDetectionService.ClearCache();

                if (FfmpegPathBox != null) FfmpegPathBox.Text = dir;
                if (LogText != null) LogText.Text += $"FFmpeg 目录已更新: {dir}\n";

                // 触发完整能力检测与编码器刷新
                _ = FullDetectionAsync();
            }
        }

        private async void BrowseOutputDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择输出目录",
                AllowMultiple = false
            });

            if (folders != null && folders.Count > 0)
            {
                var dir = folders[0].Path.LocalPath;
                AppSettingsService.Current.OutputDirectory = dir;
                AppSettingsService.Save();

                if (OutputDirBox != null) OutputDirBox.Text = dir;
                if (LogText != null) LogText.Text += $"输出目录已更新: {dir}\n";
            }
        }

        private async void SelectFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择包含图片的文件夹",
                AllowMultiple = false
            });

            if (folders == null || folders.Count == 0) return;

            var dir = folders[0].Path.LocalPath;
            var supported = new[] { ".png", ".jpg", ".jpeg", ".tiff", ".tif", ".webp", ".avif", ".jxl", ".bmp", ".gif" };

            try
            {
                var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                    .Where(f => supported.Contains(Path.GetExtension(f).ToLower()))
                    .ToList();

                if (files.Count == 0)
                {
                    if (LogText != null) LogText.Text += $"文件夹中未找到支持的图片文件: {dir}\n";
                    return;
                }

                _selectedFiles.Clear();
                _selectedFiles.AddRange(files);
                AddToMediaFiles(files);
                UpdateMediaFileCount();
                if (LogText != null) LogText.Text += $"已扫描到 {files.Count} 个文件\n";

                // 取第一个文件获取媒体信息
                _inputPath = files[0];
                if (MediaInfoText != null) MediaInfoText.Text = "正在获取媒体信息...";
                var info = await MediaInfoService.GetMediaInfoAsync(_inputPath);
                if (MediaInfoText != null) MediaInfoText.Text = $"[文件夹: {dir}]\n共 {files.Count} 个文件\n\n{info}";
            }
            catch (Exception ex)
            {
                if (LogText != null) LogText.Text += $"文件夹扫描失败: {ex.Message}\n";
            }
        }

        private string GetOutputPath(string inputPath, string format)
        {
            var outDir = AppSettingsService.Current.OutputDirectory;
            var fileName = Path.GetFileNameWithoutExtension(inputPath) + "." + format;
            return string.IsNullOrWhiteSpace(outDir)
                ? Path.ChangeExtension(inputPath, format)
                : Path.Combine(outDir, fileName);
        }

        private async void ExportPreset_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var preset = BuildPresetData();
            var json = preset.ToJson();

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出预设文件",
                DefaultExtension = ".json",
                FileTypeChoices = new[] { new FilePickerFileType("JSON 预设") { Patterns = new[] { "*.json" } } }
            });

            if (file != null)
            {
                await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, json);
                if (LogText != null) LogText.Text += $"预设已导出: {file.Path.LocalPath}\n";
            }
        }

        private async void ImportPreset_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入预设文件",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON 预设") { Patterns = new[] { "*.json" } } }
            });

            if (files == null || files.Count == 0) return;

            try
            {
                var json = await System.IO.File.ReadAllTextAsync(files[0].Path.LocalPath);
                var preset = Models.PresetData.FromJson(json);
                ApplyPresetData(preset);
                if (LogText != null) LogText.Text += $"预设已导入: {files[0].Path.LocalPath}\n";
            }
            catch (Exception ex)
            {
                if (LogText != null) LogText.Text += $"导入失败: {ex.Message}\n";
            }
        }

        private Models.PresetData BuildPresetData()
        {
            return new Models.PresetData
            {
                Format = FormatCombo?.SelectedItem as string,
                Quality = (int)(QualitySlider?.Value ?? 92),
                Chroma = ChromaCombo?.SelectedItem as string,
                ColorSpace = ColorSpaceCombo?.SelectedItem as string,
                UseAdvancedColor = UseAdvancedColor?.IsChecked ?? false,
                ColorPrimaries = ColorPrimariesCombo?.SelectedItem as string,
                ColorTrc = ColorTrcCombo?.SelectedItem as string,
                ColorMatrix = ColorMatrixCombo?.SelectedItem as string,
                BitDepth = BitDepthCombo?.SelectedItem as string,
                AutoThreads = AutoThreadsCheck?.IsChecked ?? true,
                SingleThread = SingleThreadCheck?.IsChecked ?? false,
                ManualThreads = (int)(ThreadsBox?.Value ?? 4),
                PreserveMetadata = PreserveMetadata?.IsChecked ?? true,
                Lossless = LosslessCheck?.IsChecked ?? false,
                UseAdvancedCodec = UseAdvancedCodec?.IsChecked ?? false,
                PngPred = PngPredCombo?.SelectedItem as string,
                WebpPreset = WebpPresetCombo?.SelectedItem as string,
                AvifCpuUsed = (int?)AvifCpuUsedBox?.Value,
                AvifTune = AvifTuneCombo?.SelectedItem as string,
                AvifPreset = AvifPresetCombo?.SelectedItem as string,
                AvifStillPicture = AvifStillPictureCheck?.IsChecked,
                JxlEffort = (int?)JxlEffortBox?.Value,
                JxlModular = JxlModularCheck?.IsChecked,
                JpegHuffman = JpegHuffmanCombo?.SelectedItem as string,
                TiffCompressionAlgo = TiffCompressionCombo?.SelectedItem as string,
                Concurrency = (int)(ConcurrencyBox?.Value ?? 2)
            };
        }

        private void ApplyPresetData(Models.PresetData p)
        {
            SetComboByValue(FormatCombo, p.Format);
            if (QualitySlider != null && p.Quality >= 0) QualitySlider.Value = p.Quality;
            SetComboByValue(ChromaCombo, p.Chroma);
            SetComboByValue(BitDepthCombo, p.BitDepth);
            SetComboByValue(ColorSpaceCombo, p.ColorSpace);
            if (UseAdvancedColor != null) UseAdvancedColor.IsChecked = p.UseAdvancedColor;
            SetComboByValue(ColorPrimariesCombo, p.ColorPrimaries);
            SetComboByValue(ColorTrcCombo, p.ColorTrc);
            SetComboByValue(ColorMatrixCombo, p.ColorMatrix);
            if (AutoThreadsCheck != null) AutoThreadsCheck.IsChecked = p.AutoThreads;
            if (SingleThreadCheck != null) SingleThreadCheck.IsChecked = p.SingleThread;
            if (ThreadsBox != null) ThreadsBox.Value = p.ManualThreads;
            UpdateThreadControls();
            if (PreserveMetadata != null) PreserveMetadata.IsChecked = p.PreserveMetadata;
            if (LosslessCheck != null) LosslessCheck.IsChecked = p.Lossless;
            if (UseAdvancedCodec != null) UseAdvancedCodec.IsChecked = p.UseAdvancedCodec;
            SetComboByValue(PngPredCombo, p.PngPred);
            SetComboByValue(WebpPresetCombo, p.WebpPreset);
            if (AvifCpuUsedBox != null && p.AvifCpuUsed.HasValue) AvifCpuUsedBox.Value = p.AvifCpuUsed.Value;
            SetComboByValue(AvifTuneCombo, p.AvifTune);
            SetComboByValue(AvifPresetCombo, p.AvifPreset);
            if (AvifStillPictureCheck != null && p.AvifStillPicture.HasValue) AvifStillPictureCheck.IsChecked = p.AvifStillPicture.Value;
            if (JxlEffortBox != null && p.JxlEffort.HasValue) JxlEffortBox.Value = p.JxlEffort.Value;
            if (JxlModularCheck != null && p.JxlModular.HasValue) JxlModularCheck.IsChecked = p.JxlModular.Value;
            SetComboByValue(JpegHuffmanCombo, p.JpegHuffman);
            SetComboByValue(TiffCompressionCombo, p.TiffCompressionAlgo);
            if (ConcurrencyBox != null) ConcurrencyBox.Value = p.Concurrency;
            UpdateConcurrencyLabel();
            UpdateOptionAvailability();
            UpdateQualityLabel();
        }

        private static void SetComboByValue(ComboBox? combo, string? value)
        {
            if (combo == null || value == null) return;
            for (int i = 0; i < combo.Items!.Count; i++)
            {
                if ((combo.Items[i] as string) == value)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

        private void DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data.Contains(DataFormats.Files))
            {
                if (MediaDropZone != null) MediaDropZone.BorderBrush = Avalonia.Media.Brushes.DodgerBlue;
                if (MediaInfoText != null) MediaInfoText.Text = "释放以载入文件/文件夹...";
                e.DragEffects = DragDropEffects.Copy;
            }
        }

        private void DragLeave(object? sender, DragEventArgs e)
        {
            if (MediaDropZone != null) MediaDropZone.BorderBrush = Avalonia.Media.Brushes.LightGray;
        }

        private async void DropHandler(object? sender, DragEventArgs e)
        {
            DragLeave(sender, e); // 还原样式
            if (!e.Data.Contains(DataFormats.Files)) return;

            var files = e.Data.GetFiles()?.Select(f => f.Path.LocalPath).ToArray();
            if (files == null || files.Length == 0) return;

            // 单文件 → 作为输入文件
            if (files.Length == 1)
            {
                var path = files[0];
                if (System.IO.Directory.Exists(path))
                {
                    await ScanFolderAsync(path);
                }
                else
                {
                    _inputPath = path;
                    var supported = new[] { ".png", ".jpg", ".jpeg", ".tiff", ".tif", ".webp", ".avif", ".jxl", ".bmp", ".gif" };
                    if (supported.Contains(System.IO.Path.GetExtension(path).ToLower()))
                    {
                        if (LogText != null) LogText.Text += $"已拖放: {path}\n";
                        if (MediaInfoText != null) MediaInfoText.Text = "正在获取媒体信息...";
                        var info = await MediaInfoService.GetMediaInfoAsync(path);
                        if (MediaInfoText != null) MediaInfoText.Text = info;
                    }
                    else
                    {
                        if (LogText != null) LogText.Text += $"不支持的文件类型: {path}\n";
                    }
                }
            }
            // 多文件或文件夹 → 批量添加
            else
            {
                _selectedFiles.Clear();
                var supported = new[] { ".png", ".jpg", ".jpeg", ".tiff", ".tif", ".webp", ".avif", ".jxl", ".bmp", ".gif" };
                foreach (var path in files)
                {
                    if (System.IO.Directory.Exists(path))
                    {
                        try
                        {
                            var dirFiles = System.IO.Directory.EnumerateFiles(path, "*.*", System.IO.SearchOption.AllDirectories)
                                .Where(f => supported.Contains(System.IO.Path.GetExtension(f).ToLower()));
                            _selectedFiles.AddRange(dirFiles);
                        }
                        catch { }
                    }
                    else if (supported.Contains(System.IO.Path.GetExtension(path).ToLower()))
                    {
                        _selectedFiles.Add(path);
                    }
                }

                if (_selectedFiles.Count > 0)
                {
                    AddToMediaFiles(_selectedFiles);
                    if (LogText != null) LogText.Text += $"已拖放 {_selectedFiles.Count} 个文件\n";
                    _selectedFiles.Clear();
                    UpdateMediaFileCount();
                }
            }
        }

        private async Task ScanFolderAsync(string dir)
        {
            var supported = new[] { ".png", ".jpg", ".jpeg", ".tiff", ".tif", ".webp", ".avif", ".jxl", ".bmp", ".gif" };
            try
            {
                var files = System.IO.Directory.EnumerateFiles(dir, "*.*", System.IO.SearchOption.AllDirectories)
                    .Where(f => supported.Contains(System.IO.Path.GetExtension(f).ToLower()))
                    .ToList();

                if (files.Count == 0)
                {
                    if (LogText != null) LogText.Text += $"文件夹中未找到支持的图片文件: {dir}\n";
                    return;
                }

                _selectedFiles.Clear();
                _selectedFiles.AddRange(files);
                AddToMediaFiles(files);
                UpdateMediaFileCount();
                if (LogText != null) LogText.Text += $"已扫描到 {files.Count} 个文件\n";
                _inputPath = files[0];
                if (MediaInfoText != null) MediaInfoText.Text = $"正在获取媒体信息...";
                var info = await MediaInfoService.GetMediaInfoAsync(_inputPath);
                if (MediaInfoText != null) MediaInfoText.Text = $"[文件夹: {dir}]\n共 {files.Count} 个文件\n\n{info}";
            }
            catch (Exception ex)
            {
                if (LogText != null) LogText.Text += $"文件夹扫描失败: {ex.Message}\n";
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}