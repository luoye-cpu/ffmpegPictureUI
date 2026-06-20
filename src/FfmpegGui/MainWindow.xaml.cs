using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
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
        private readonly ObservableCollection<Models.QueueItem> _queueView = new();
        private readonly QueueProcessor _queueProcessor;
        private bool _initialized;

        private ComboBox? FormatCombo;
        private ComboBox? ConversionModeCombo;
        private ComboBox? EncoderCombo;
        private Slider? QualitySlider;
        private TextBox? QualityBox;
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
        private ComboBox? MetadataModeCombo;
        private Border? ExifToolPanel;
        private TextBlock? ExifToolHint;
        private CheckBox? StripExifGpsCheck;
        private CheckBox? StripExifTimeCheck;
        private CheckBox? StripExifCameraCheck;
        private CheckBox? StripExifAllCheck;
        private CheckBox? StripXmpCheck;
        private CheckBox? LosslessCheck;
        private ListBox? QueueList;
        private TextBox? ConcurrencyBox;
        private TextBox? CommandText;
        private TextBox? MediaInfoText;
        private TextBox? LogText;
        private TextBox? FfmpegPathBox;
        private TextBox? OutputDirBox;
        private TextBox? CjxlPathBox;
        private TextBox? ExifToolPathBox;
        private TextBox? AvifencPathBox;
        private TextBox? UltrahdrPathBox;
        private CheckBox? PreserveInputStructure;
        private CheckBox? StopAfterCurrentCheck;
        private CheckBox? ShowErrorsOnlyCheck;
        private TextBlock? QueueProgressLabel;
        private TextBlock? QueueEtaLabel;
        private TextBlock? ElapsedLabel;
        private Button? ClearQueueButton;
        private CheckBox? AutoThreadsCheck;
        private bool _isQueueRunning = false;
        private bool _updatingQuality = false;  // 防止滑块/数字框双向同步时递归
        private bool _suppressCommandRegen = false; // 批量更新选项时抑制重复 RegenerateCommand 调用
        private CheckBox? SingleThreadCheck;
        private TextBlock? ThreadHintLabel;
        private TextBlock? ConcurrencyLabel;
        private TextBlock? QueueCountLabel;
        private Button? ConcurrencyUpBtn;
        private Button? ConcurrencyDownBtn;
        private Button? ThemeToggleBtn;
        private CheckBox? UseAdvancedCodec;
        private StackPanel? AdvancedCodecPanel;
        // 动图参数
        private StackPanel? AnimationPanel;
        private TextBox? AnimationFpsBox;
        private TextBox? AnimationLoopBox;
        private TextBox? AnimationScaleWBox;
        private StackPanel? AnimationDurationPanel;
        private TextBox? AnimationDurationBox;
        // 各格式高级面板
        private StackPanel? PngCodecPanel;
        private StackPanel? GifCodecPanel;
        private StackPanel? ApngCodecPanel;
        private CheckBox? GifPaletteCheck;
        private CheckBox? GifDitherCheck;
        private StackPanel? WebpCodecPanel;
        private StackPanel? AvifCodecPanel;
        private StackPanel? JxlCodecPanel;
        private StackPanel? JxlFfmpegPanel;
        private StackPanel? JxlCjxlPanel;
        private StackPanel? JpegCodecPanel;
        private StackPanel? TiffCodecPanel;
        private StackPanel? JxrCodecPanel;
        // 外部工具折叠面板
        private Button? ToggleToolsBtn;
        private StackPanel? ToolsDetailPanel;
        private Border? ToolsCompactPanel;
        private StackPanel? ToolsStatusBar;
        private TextBox? JxrPathBox;
        private ComboBox? PngPredCombo;
        private ComboBox? WebpPresetCombo;
        private NumericUpDown? WebpCompressionBox;
        private StackPanel? WebpLosslessPanel;
        private NumericUpDown? AvifCpuUsedBox;
        private CheckBox? AvifStillPictureCheck;
        private CheckBox? AvifRowMtCheck;
        private CheckBox? AutoUseSimdCheck;
        private ComboBox? AvifTuneCombo;
        // SVT-AV1 专用控件
        private StackPanel? LibaomAvifPanel;
        private StackPanel? SvtAvifPanel;
        private StackPanel? HwAvifPanel;
        private NumericUpDown? SvtPresetBox;
        private ComboBox? SvtTuneCombo;
        private CheckBox? SvtStillPictureCheck;
        private ComboBox? HwPresetCombo;
        private ComboBox? PriorityCombo;
        private NumericUpDown? JxlEffortBox;
        private NumericUpDown? CjxlEffortBox;
        private CheckBox? JxlModularCheck;
        private CheckBox? CjxlProgressiveCheck;
        private NumericUpDown? CjxlPhotonNoiseBox;
        private ComboBox? JpegHuffmanCombo;
        private ComboBox? JpegDctCombo;
        private ComboBox? JpegProgressiveCombo;
        // ── Gain Map (Ultra HDR) JPEG 控件 ──
        private Border? JpegGainMapPanel;
        private StackPanel? JpegGainMapOptions;
        private CheckBox? JpegGainMapFollowMainCheck;
        private StackPanel? JpegGainMapQualityPanel;
        private TextBox? JpegGainMapQualityBox;
        private Button? GainMapQualityUpBtn;
        private Button? GainMapQualityDownBtn;
        private Slider? JpegGainMapNitsSlider;
        private TextBox? JpegGainMapNitsBox;
        private ComboBox? JpegGainMapHdrCfCombo;
        private ComboBox? JpegGainMapDownsampleCombo;
        private CheckBox? JpegGainMapMultiChannelCheck;
        private ComboBox? TiffCompressionCombo;
        // ── cjpegli / jpegli 高级面板控件 ──
        private StackPanel? JpegliCodecPanel;
        private ComboBox? JpegliChromaCombo;
        private ComboBox? JpegliProgressiveCombo;
        private CheckBox? JpegliOptimizeCheck;
        private CheckBox? JpegliAdaptiveQuantCheck;
        private ComboBox? JpegliEncoderBackendCombo;
        private Border? JpegliPsnrPanel;
        private NumericUpDown? JpegliPsnrBox;
        // ── 拖放区域 ──
        private Border? DropZone;
        private TextBlock? DropHint;
        private Border? MediaDropZone;
        private TextBlock? FileCountLabel;
        private ListBox? MediaFileList;
        private TextBlock? MediaFileCount;
        private Button? FormatFilterBtn;
        private readonly List<string> _selectedFiles = new();
        // 当批量拖拽多个文件夹时，记录每个已选文件对应的输入根目录，
        // 以便在保留输入目录结构时按各自根目录计算相对路径。
        private readonly Dictionary<string, string> _selectedFileBaseDirs = new();
        private readonly ObservableCollection<string> _mediaFiles = new();
        private readonly List<Models.QueueItem> _queueItems = new();
        private string? _inputBaseDir;

        public MainWindow()
        {
            InitializeComponent();
            _queueProcessor = new QueueProcessor(OnQueueItemUpdated, OnQueueStopped);
            Opened += (_, _) => InitControls();
        }

        private void InitControls()
        {
            if (_initialized) return;
            _initialized = true;

            // 获取 UI 控件引用
            FormatCombo = this.FindControl<ComboBox>("FormatCombo");
            ConversionModeCombo = this.FindControl<ComboBox>("ConversionModeCombo");
            EncoderCombo = this.FindControl<ComboBox>("EncoderCombo");
            QualitySlider = this.FindControl<Slider>("QualitySlider");
            QualityBox = this.FindControl<TextBox>("QualityBox");
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
            MetadataModeCombo = this.FindControl<ComboBox>("MetadataModeCombo");
            ExifToolPanel = this.FindControl<Border>("ExifToolPanel");
            ExifToolHint = this.FindControl<TextBlock>("ExifToolHint");
            StripExifGpsCheck = this.FindControl<CheckBox>("StripExifGpsCheck");
            StripExifTimeCheck = this.FindControl<CheckBox>("StripExifTimeCheck");
            StripExifCameraCheck = this.FindControl<CheckBox>("StripExifCameraCheck");
            StripExifAllCheck = this.FindControl<CheckBox>("StripExifAllCheck");
            StripXmpCheck = this.FindControl<CheckBox>("StripXmpCheck");
            LosslessCheck = this.FindControl<CheckBox>("LosslessCheck");
            QueueList = this.FindControl<ListBox>("QueueList");
            ConcurrencyBox = this.FindControl<TextBox>("ConcurrencyBox");
            ConcurrencyLabel = this.FindControl<TextBlock>("ConcurrencyLabel");
            CommandText = this.FindControl<TextBox>("CommandText");
            MediaInfoText = this.FindControl<TextBox>("MediaInfoText");
            LogText = this.FindControl<TextBox>("LogText");
            FfmpegPathBox = this.FindControl<TextBox>("FfmpegPathBox");
            OutputDirBox = this.FindControl<TextBox>("OutputDirBox");
            CjxlPathBox = this.FindControl<TextBox>("CjxlPathBox");
            ExifToolPathBox = this.FindControl<TextBox>("ExifToolPathBox");
            AvifencPathBox = this.FindControl<TextBox>("AvifencPathBox");
            UltrahdrPathBox = this.FindControl<TextBox>("UltrahdrPathBox");
            JxrPathBox = this.FindControl<TextBox>("JxrPathBox");
            ToggleToolsBtn = this.FindControl<Button>("ToggleToolsBtn");
            ToolsDetailPanel = this.FindControl<StackPanel>("ToolsDetailPanel");
            ToolsCompactPanel = this.FindControl<Border>("ToolsCompactPanel");
            ToolsStatusBar = this.FindControl<StackPanel>("ToolsStatusBar");
            PreserveInputStructure = this.FindControl<CheckBox>("PreserveInputStructure");
            AutoUseSimdCheck = this.FindControl<CheckBox>("AutoUseSimdCheck");
            AutoThreadsCheck = this.FindControl<CheckBox>("AutoThreadsCheck");
            SingleThreadCheck = this.FindControl<CheckBox>("SingleThreadCheck");
            ThreadHintLabel = this.FindControl<TextBlock>("ThreadHintLabel");
            UseAdvancedCodec = this.FindControl<CheckBox>("UseAdvancedCodec");
            AdvancedCodecPanel = this.FindControl<StackPanel>("AdvancedCodecPanel");
            // 动图参数
            AnimationPanel = this.FindControl<StackPanel>("AnimationPanel");
            AnimationFpsBox = this.FindControl<TextBox>("AnimationFpsBox");
            AnimationLoopBox = this.FindControl<TextBox>("AnimationLoopBox");
            AnimationScaleWBox = this.FindControl<TextBox>("AnimationScaleWBox");
            AnimationDurationPanel = this.FindControl<StackPanel>("AnimationDurationPanel");
            AnimationDurationBox = this.FindControl<TextBox>("AnimationDurationBox");
            // 动图参数默认空（auto，编码器自行决定）
            if (AnimationFpsBox != null) AnimationFpsBox.Text = "";
            if (AnimationLoopBox != null) AnimationLoopBox.Text = "";
            if (AnimationScaleWBox != null) AnimationScaleWBox.Text = "";
            if (AnimationDurationBox != null) AnimationDurationBox.Text = "";
            if (AnimationPanel != null) AnimationPanel.IsVisible = false;
            // 各格式高级面板
            PngCodecPanel = this.FindControl<StackPanel>("PngCodecPanel");
            GifCodecPanel = this.FindControl<StackPanel>("GifCodecPanel");
            ApngCodecPanel = this.FindControl<StackPanel>("ApngCodecPanel");
            GifPaletteCheck = this.FindControl<CheckBox>("GifPaletteCheck");
            GifDitherCheck = this.FindControl<CheckBox>("GifDitherCheck");
            WebpCodecPanel = this.FindControl<StackPanel>("WebpCodecPanel");
            AvifCodecPanel = this.FindControl<StackPanel>("AvifCodecPanel");
            JxlCodecPanel = this.FindControl<StackPanel>("JxlCodecPanel");
            JxlFfmpegPanel = this.FindControl<StackPanel>("JxlFfmpegPanel");
            JxlCjxlPanel = this.FindControl<StackPanel>("JxlCjxlPanel");
            JpegCodecPanel = this.FindControl<StackPanel>("JpegCodecPanel");
            TiffCodecPanel = this.FindControl<StackPanel>("TiffCodecPanel");
            JxrCodecPanel = this.FindControl<StackPanel>("JxrCodecPanel");
            PngPredCombo = this.FindControl<ComboBox>("PngPredCombo");
            WebpPresetCombo = this.FindControl<ComboBox>("WebpPresetCombo");
            WebpCompressionBox = this.FindControl<NumericUpDown>("WebpCompressionBox");
            WebpLosslessPanel = this.FindControl<StackPanel>("WebpLosslessPanel");
            AvifCpuUsedBox = this.FindControl<NumericUpDown>("AvifCpuUsedBox");
            AvifStillPictureCheck = this.FindControl<CheckBox>("AvifStillPictureCheck");
            AvifRowMtCheck = this.FindControl<CheckBox>("AvifRowMtCheck");
            AvifTuneCombo = this.FindControl<ComboBox>("AvifTuneCombo");
            // AVIF 编码器特定面板
            LibaomAvifPanel = this.FindControl<StackPanel>("LibaomAvifPanel");
            SvtAvifPanel = this.FindControl<StackPanel>("SvtAvifPanel");
            HwAvifPanel = this.FindControl<StackPanel>("HwAvifPanel");
            SvtPresetBox = this.FindControl<NumericUpDown>("SvtPresetBox");
            SvtTuneCombo = this.FindControl<ComboBox>("SvtTuneCombo");
            SvtStillPictureCheck = this.FindControl<CheckBox>("SvtStillPictureCheck");
            HwPresetCombo = this.FindControl<ComboBox>("HwPresetCombo");
            PriorityCombo = this.FindControl<ComboBox>("PriorityCombo");
            JxlEffortBox = this.FindControl<NumericUpDown>("JxlEffortBox");
            JxlModularCheck = this.FindControl<CheckBox>("JxlModularCheck");
            // cjxl 专属控件
            CjxlEffortBox = this.FindControl<NumericUpDown>("CjxlEffortBox");
            CjxlProgressiveCheck = this.FindControl<CheckBox>("CjxlProgressiveCheck");
            CjxlPhotonNoiseBox = this.FindControl<NumericUpDown>("CjxlPhotonNoiseBox");
            JpegHuffmanCombo = this.FindControl<ComboBox>("JpegHuffmanCombo");
            JpegDctCombo = this.FindControl<ComboBox>("JpegDctCombo");
            JpegProgressiveCombo = this.FindControl<ComboBox>("JpegProgressiveCombo");
            // ── Gain Map (Ultra HDR) JPEG 控件 ──
            JpegGainMapPanel = this.FindControl<Border>("JpegGainMapPanel");
            JpegGainMapOptions = this.FindControl<StackPanel>("JpegGainMapOptions");
            JpegGainMapFollowMainCheck = this.FindControl<CheckBox>("JpegGainMapFollowMainCheck");
            JpegGainMapQualityPanel = this.FindControl<StackPanel>("JpegGainMapQualityPanel");
            JpegGainMapQualityBox = this.FindControl<TextBox>("JpegGainMapQualityBox");
            GainMapQualityUpBtn = this.FindControl<Button>("GainMapQualityUpBtn");
            GainMapQualityDownBtn = this.FindControl<Button>("GainMapQualityDownBtn");
            JpegGainMapNitsSlider = this.FindControl<Slider>("JpegGainMapNitsSlider");
            JpegGainMapNitsBox = this.FindControl<TextBox>("JpegGainMapNitsBox");
            JpegGainMapHdrCfCombo = this.FindControl<ComboBox>("JpegGainMapHdrCfCombo");
            JpegGainMapDownsampleCombo = this.FindControl<ComboBox>("JpegGainMapDownsampleCombo");
            JpegGainMapMultiChannelCheck = this.FindControl<CheckBox>("JpegGainMapMultiChannelCheck");
            TiffCompressionCombo = this.FindControl<ComboBox>("TiffCompressionCombo");
            // ── cjpegli / jpegli 高级面板控件 ──
            JpegliCodecPanel = this.FindControl<StackPanel>("JpegliCodecPanel");
            JpegliChromaCombo = this.FindControl<ComboBox>("JpegliChromaCombo");
            JpegliProgressiveCombo = this.FindControl<ComboBox>("JpegliProgressiveCombo");
            JpegliOptimizeCheck = this.FindControl<CheckBox>("JpegliOptimizeCheck");
            JpegliAdaptiveQuantCheck = this.FindControl<CheckBox>("JpegliAdaptiveQuantCheck");
            JpegliEncoderBackendCombo = this.FindControl<ComboBox>("JpegliEncoderBackendCombo");
            JpegliPsnrPanel = this.FindControl<Border>("JpegliPsnrPanel");
            JpegliPsnrBox = this.FindControl<NumericUpDown>("JpegliPsnrBox");
            DropZone = this.FindControl<Border>("DropZone");
            DropHint = this.FindControl<TextBlock>("DropHint");
            MediaDropZone = this.FindControl<Border>("MediaDropZone");
            FileCountLabel = this.FindControl<TextBlock>("FileCountLabel");
            MediaFileList = this.FindControl<ListBox>("MediaFileList");
            MediaFileCount = this.FindControl<TextBlock>("MediaFileCount");
            QueueCountLabel = this.FindControl<TextBlock>("QueueCountLabel");
            FormatFilterBtn = this.FindControl<Button>("FormatFilterBtn");

            // 设置绑定和初始值
            if (FormatCombo != null) FormatCombo.SelectedIndex = 0;
            if (ChromaCombo != null) ChromaCombo.SelectedIndex = 0; // auto
            if (BitDepthCombo != null) BitDepthCombo.SelectedIndex = 0; // auto
            if (ColorSpaceCombo != null) ColorSpaceCombo.SelectedIndex = 0; // auto
            if (ColorPrimariesCombo != null) ColorPrimariesCombo.SelectedIndex = 0;
            if (ColorTrcCombo != null) ColorTrcCombo.SelectedIndex = 0;
            if (ColorMatrixCombo != null) ColorMatrixCombo.SelectedIndex = 0;

            if (QueueList != null) QueueList.ItemsSource = _queueView;

            // 注册事件
            if (ColorSpaceCombo != null) ColorSpaceCombo.SelectionChanged += ColorSpaceCombo_SelectionChanged;
            // 其他参数变更时刷新命令预览
            if (ChromaCombo != null) ChromaCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (BitDepthCombo != null) BitDepthCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (EncoderCombo != null) EncoderCombo.SelectionChanged += (_, _) =>
            {
                UpdateThreadAvailabilityForFormat(NormalizeFormat(FormatCombo?.SelectedItem as string));
                UpdateCodecPanelVisibility(NormalizeFormat(FormatCombo?.SelectedItem as string));
                UpdateAvifEncoderPanel();
                RegenerateCommand();
            };
            if (ThreadsBox != null) ThreadsBox.ValueChanged += (_, _) => RegenerateCommand();
            // 动图参数
            if (AnimationFpsBox != null) AnimationFpsBox.TextChanged += (_, _) => RegenerateCommand();
            if (AnimationLoopBox != null) AnimationLoopBox.TextChanged += (_, _) => RegenerateCommand();
            if (AnimationScaleWBox != null) AnimationScaleWBox.TextChanged += (_, _) => RegenerateCommand();
            if (AnimationDurationBox != null) AnimationDurationBox.TextChanged += (_, _) => RegenerateCommand();
            if (GifPaletteCheck != null) GifPaletteCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (GifDitherCheck != null) GifDitherCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            // 高级色彩参数
            if (ColorPrimariesCombo != null) ColorPrimariesCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (ColorTrcCombo != null) ColorTrcCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (ColorMatrixCombo != null) ColorMatrixCombo.SelectionChanged += (_, _) => RegenerateCommand();
            // 高级编码器选项
            if (PngPredCombo != null) PngPredCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (WebpPresetCombo != null) WebpPresetCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (WebpCompressionBox != null) WebpCompressionBox.ValueChanged += (_, _) => RegenerateCommand();
            if (AvifCpuUsedBox != null) AvifCpuUsedBox.ValueChanged += (_, _) => RegenerateCommand();
            if (AvifStillPictureCheck != null) AvifStillPictureCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (AvifRowMtCheck != null) AvifRowMtCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (AvifTuneCombo != null) AvifTuneCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (SvtPresetBox != null) SvtPresetBox.ValueChanged += (_, _) => RegenerateCommand();
            if (SvtTuneCombo != null) SvtTuneCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (SvtStillPictureCheck != null) SvtStillPictureCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (HwPresetCombo != null) HwPresetCombo.SelectionChanged += (_, _) => RegenerateCommand();
            // 进程优先级变更时持久化
            if (PriorityCombo != null)
            {
                var savedPriority = AppSettingsService.Current.FfmpegPriority;
                PriorityCombo.SelectedIndex = Math.Clamp(savedPriority, 0, 5);
                PriorityCombo.SelectionChanged += (_, _) =>
                {
                    AppSettingsService.Current.FfmpegPriority = PriorityCombo.SelectedIndex;
                    AppSettingsService.Save();
                };
            }
            if (JxlEffortBox != null) JxlEffortBox.ValueChanged += (_, _) => RegenerateCommand();
            if (JxlModularCheck != null) JxlModularCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            // cjxl 控件事件
            if (CjxlEffortBox != null) CjxlEffortBox.ValueChanged += (_, _) => RegenerateCommand();
            if (CjxlProgressiveCheck != null) CjxlProgressiveCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (CjxlPhotonNoiseBox != null) CjxlPhotonNoiseBox.ValueChanged += (_, _) => RegenerateCommand();
            if (JpegHuffmanCombo != null) JpegHuffmanCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (JpegDctCombo != null) JpegDctCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (JpegProgressiveCombo != null) JpegProgressiveCombo.SelectionChanged += (_, _) => RegenerateCommand();
            // Gain Map 控件事件
            if (JpegGainMapFollowMainCheck != null)
            {
                JpegGainMapFollowMainCheck.IsCheckedChanged += (_, _) =>
                {
                    if (JpegGainMapQualityPanel != null)
                        JpegGainMapQualityPanel.IsVisible = JpegGainMapFollowMainCheck.IsChecked != true;
                    RegenerateCommand();
                };
            }
            if (JpegGainMapQualityBox != null)
            {
                JpegGainMapQualityBox.TextChanged += (_, _) => RegenerateCommand();
                // 失焦时格式化：限制 1-100 范围
                JpegGainMapQualityBox.LostFocus += (_, _) =>
                {
                    if (int.TryParse(JpegGainMapQualityBox.Text, out var q))
                        JpegGainMapQualityBox.Text = Math.Clamp(q, 1, 100).ToString();
                    else
                        JpegGainMapQualityBox.Text = "75";
                };
            }
            if (GainMapQualityUpBtn != null)
                GainMapQualityUpBtn.Click += (_, _) => AdjustGainMapQuality(1);
            if (GainMapQualityDownBtn != null)
                GainMapQualityDownBtn.Click += (_, _) => AdjustGainMapQuality(-1);
            if (JpegGainMapNitsBox != null)
                JpegGainMapNitsBox.TextChanged += (_, _) =>
                {
                    SyncNitsToSlider();
                    RegenerateCommand();
                };
            if (JpegGainMapNitsSlider != null)
            {
                JpegGainMapNitsSlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value))
                    {
                        JpegGainMapNitsBox!.Text = ((int)JpegGainMapNitsSlider.Value).ToString();
                        RegenerateCommand();
                    }
                };
            }
            if (JpegGainMapHdrCfCombo != null)
                JpegGainMapHdrCfCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (JpegGainMapDownsampleCombo != null)
                JpegGainMapDownsampleCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (JpegGainMapMultiChannelCheck != null)
                JpegGainMapMultiChannelCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (TiffCompressionCombo != null) TiffCompressionCombo.SelectionChanged += (_, _) => RegenerateCommand();
            // cjpegli / jpegli 高级选项事件
            if (JpegliChromaCombo != null) JpegliChromaCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (JpegliProgressiveCombo != null) JpegliProgressiveCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (JpegliOptimizeCheck != null) JpegliOptimizeCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (JpegliAdaptiveQuantCheck != null) JpegliAdaptiveQuantCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (JpegliEncoderBackendCombo != null) { JpegliEncoderBackendCombo.SelectionChanged += (_, _) => { UpdateJpegliPsnrVisibility(); RegenerateCommand(); }; }
            if (JpegliPsnrBox != null) JpegliPsnrBox.ValueChanged += (_, _) => RegenerateCommand();
            
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

            if (PreserveInputStructure != null)
            {
                // 初始化值（LoadSettings 也会设置一次，冗余安全）
                PreserveInputStructure.IsChecked = AppSettingsService.Current.PreserveInputFolderStructure;
                PreserveInputStructure.IsCheckedChanged += (_, _) =>
                {
                    AppSettingsService.Current.PreserveInputFolderStructure = PreserveInputStructure.IsChecked ?? false;
                    AppSettingsService.Save();
                };
                if (AutoUseSimdCheck != null)
                {
                    AutoUseSimdCheck.IsChecked = AppSettingsService.Current.AutoUseSimdBinaries;
                    AutoUseSimdCheck.IsCheckedChanged += (_, _) =>
                    {
                        AppSettingsService.Current.AutoUseSimdBinaries = AutoUseSimdCheck.IsChecked ?? false;
                        AppSettingsService.Save();
                    };
                }
            }

            // 无损编码 / JXL 强制元数据 → 每次变化刷新命令与选项
            if (LosslessCheck != null)
                LosslessCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (MetadataModeCombo != null)
                MetadataModeCombo.SelectionChanged += (_, _) => { UpdateExifToolPanelState(); RegenerateCommand(); };
            // ExifTool 复选框变更时刷新命令预览
            if (StripExifGpsCheck != null)
                StripExifGpsCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (StripExifTimeCheck != null)
                StripExifTimeCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (StripExifCameraCheck != null)
                StripExifCameraCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (StripExifAllCheck != null)
                StripExifAllCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (StripXmpCheck != null)
                StripXmpCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (UseAdvancedColor != null)
                UseAdvancedColor.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (UseAdvancedCodec != null)
                UseAdvancedCodec.IsCheckedChanged += (_, _) => RegenerateCommand();

            // 清空队列按钮引用与初始可用性
            ClearQueueButton = this.FindControl<Button>("ClearQueueButton");
            if (ClearQueueButton != null)
                ClearQueueButton.IsEnabled = !_isQueueRunning;

            // 停止设置复选框（放在队列按钮旁）
            StopAfterCurrentCheck = this.FindControl<CheckBox>("StopAfterCurrentCheck");
            ShowErrorsOnlyCheck = this.FindControl<CheckBox>("ShowErrorsOnlyCheck");
            QueueProgressLabel = this.FindControl<TextBlock>("QueueProgressLabel");
            QueueEtaLabel = this.FindControl<TextBlock>("QueueEtaLabel");
            ElapsedLabel = this.FindControl<TextBlock>("ElapsedLabel");
            if (StopAfterCurrentCheck != null)
            {
                StopAfterCurrentCheck.IsCheckedChanged += (_, _) => 
                {
                    // 仅在队列正在运行时立即生效；否则在 Start 时会检查此复选框
                    if (StopAfterCurrentCheck.IsChecked == true)
                    {
                        if (_isQueueRunning)
                        {
                            if (LogText != null) LogText.Text += "已设置：完成当前队列后停止\n";
                        }
                    }
                    else
                    {
                        if (_isQueueRunning)
                        {
                            if (LogText != null) LogText.Text += "已取消：完成当前队列后停止\n";
                        }
                    }
                };
            }

            // 队列计数 + 并发数标签更新
            _queueView.CollectionChanged += (_, _) => UpdateQueueCountLabel();
            ConcurrencyUpBtn = this.FindControl<Button>("ConcurrencyUpBtn");
            ConcurrencyDownBtn = this.FindControl<Button>("ConcurrencyDownBtn");
            if (ConcurrencyBox != null)
            {
                // 仅允许输入数字
                ConcurrencyBox.AddHandler(TextBox.TextInputEvent, (_, e) =>
                {
                    if (e.Text != null && !e.Text.All(char.IsDigit))
                        e.Handled = true; // 阻止非数字字符输入
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                // 失去焦点时校验范围
                ConcurrencyBox.LostFocus += (_, _) => ValidateAndApplyConcurrency();
                // 从配置加载已保存的队列上限值
                ConcurrencyBox.Text = Math.Clamp(AppSettingsService.Current.MaxQueueSize, 1, 128).ToString();
                UpdateConcurrencyLabel();
            }

            // 队列项双击 → 打开详情窗口
            if (QueueList != null)
                QueueList.DoubleTapped += QueueList_DoubleTapped;

            // 主题切换按钮
            ThemeToggleBtn = this.FindControl<Button>("ThemeToggleBtn");
            if (ThemeToggleBtn != null)
            {
                var isDark = AppSettingsService.Current.ThemeMode != 1; // 默认深色
                App.SetTheme(isDark);
                ThemeToggleBtn.Content = isDark ? "☀" : "🌙";
            }

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
                MediaDropZone.AddHandler(DragDrop.DragOverEvent, DragOver);
                MediaDropZone.AddHandler(DragDrop.DropEvent, DropHandler);
            }

            UpdateQualityLabel();
            // 质量滑块与数字输入框双向同步（用 _updatingQuality 标志防递归）
            // 输入框显示的是各格式的实际参数值（如 JPEG q:v 5、JXL distance 1.2）
            if (QualitySlider != null)
                QualitySlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value) && !_updatingQuality)
                    {
                        _updatingQuality = true;
                        var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
                        if (QualityBox != null) QualityBox.Text = Models.FfmpegOptions.FormatQualityForDisplay(fmt, (int)QualitySlider.Value, GetCurrentEncoderBackend());
                        _updatingQuality = false;
                        UpdateQualityLabel();
                    }
                };
            if (QualityBox != null)
            {
                QualityBox.TextChanged += (_, _) =>
                {
                    if (_updatingQuality) return;
                    var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
                    if (double.TryParse(QualityBox.Text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        _updatingQuality = true;
                        var sliderVal = Models.FfmpegOptions.ParseQualityFromDisplay(fmt, parsed, GetCurrentEncoderBackend());
                        if (QualitySlider != null) QualitySlider.Value = sliderVal;
                        _updatingQuality = false;
                        UpdateQualityLabel();
                    }
                };
                // 失焦时格式化文本（清理无效输入，显示正确的格式参数值）
                QualityBox.LostFocus += (_, _) =>
                {
                    if (QualitySlider != null)
                    {
                        var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
                        QualityBox.Text = Models.FfmpegOptions.FormatQualityForDisplay(fmt, (int)QualitySlider.Value, GetCurrentEncoderBackend());
                    }
                };
            }

            // 进度刷新定时器（每秒更新一次）
            var progressTimer = new System.Timers.Timer(1000);
            progressTimer.Elapsed += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => UpdateProgressDisplay());
            };
            progressTimer.Start();

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
            if (!string.IsNullOrWhiteSpace(settings.CjxlPath) && CjxlPathBox != null)
                CjxlPathBox.Text = settings.CjxlPath;
            if (!string.IsNullOrWhiteSpace(settings.ExifToolPath) && ExifToolPathBox != null)
                ExifToolPathBox.Text = settings.ExifToolPath;
            if (!string.IsNullOrWhiteSpace(settings.AvifencPath) && AvifencPathBox != null)
                AvifencPathBox.Text = settings.AvifencPath;
            if (!string.IsNullOrWhiteSpace(settings.UltrahdrPath) && UltrahdrPathBox != null)
                UltrahdrPathBox.Text = settings.UltrahdrPath;
            if (!string.IsNullOrWhiteSpace(settings.JxrPath) && JxrPathBox != null)
                JxrPathBox.Text = settings.JxrPath;
            if (PreserveInputStructure != null)
                PreserveInputStructure.IsChecked = settings.PreserveInputFolderStructure;
            if (ConcurrencyBox != null)
                ConcurrencyBox.Text = Math.Clamp(settings.MaxQueueSize, 1, 128).ToString();

            // 启动时自动检测能力（即使没有 ffmpeg 路径也尝试 PATH 检测）
            _ = FullDetectionAsync();
        }

        private async Task FullDetectionAsync()
        {
            if (LogText != null) LogText.Text += "正在检测 ffmpeg 能力与可用编码器...\n";
            CjxlService.ClearCache();
            CjpegliService.ClearCache();
            CjxlService.Detect();
            CjpegliService.Detect();
            await FormatCapabilitiesService.InitializeAsync(AppSettingsService.Current.FfmpegPath);
            
            // 预加载所有格式的编码器
            await EncoderDetectionService.GetAllEncodersAsync(AppSettingsService.Current.FfmpegPath);
            
            await RefreshEncoderListAsync();

            // CPU 指令集检测
            try
            {
                CpuFeatureService.Detect();
                if (LogText != null)
                {
                    LogText.Text += $"[cpu] 指令集检测: {CpuFeatureService.Summary()}\n";
                    if (CpuFeatureService.HasAvx2)
                        LogText.Text += "[cpu] 建议：优先使用带 avx2/avx 优化的本地二进制以获得更好性能。\n";
                }
            }
            catch { }

            // ffmpeg SIMD 编译能力探测
            try
            {
                var ffmpegProbe = ExternalToolsDetector.ProbeFfmpeg();
                if (LogText != null && ffmpegProbe != null && ffmpegProbe.IsRunnable)
                {
                    if (ffmpegProbe.SimdFeatures.Count > 0)
                        LogText.Text += $"[ffmpeg] SIMD 编译选项: {string.Join(", ", ffmpegProbe.SimdFeatures)}\n";
                    else
                        LogText.Text += "[ffmpeg] 未检测到 SIMD 编译选项（可能为通用构建）\n";
                    if (!string.IsNullOrWhiteSpace(ffmpegProbe.Version))
                        LogText.Text += $"[ffmpeg] 版本: {ffmpegProbe.Version}\n";
                }
            }
            catch { }
            if (LogText != null)
            {
                LogText.Text += "能力检测完成。\n";
                if (CjxlService.IsAvailable)
                {
                    LogText.Text += $"✅ 检测到 cjxl（{CjxlService.DetectedPath}）\n";
                    try
                    {
                        var tag = ExternalToolsDetector.GetFeatureTagFromFileName(CjxlService.DetectedPath);
                        if (!string.IsNullOrEmpty(tag))
                        {
                            LogText.Text += $"[cpu] cjxl 优化标识: {tag}\n";
                        }

                        // 运行短样本探测，解析版本/特征信息（异步到线程池避免阻塞 UI）
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var path = CjxlService.DetectedPath;
                                if (string.IsNullOrEmpty(path)) return;
                                var probe = await Task.Run(() => ExternalToolsDetector.ProbeExecutable(path, 2000));
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    if (probe != null && probe.IsRunnable)
                                    {
                                        if (!string.IsNullOrWhiteSpace(probe.Version)) LogText.Text += $"[probe] cjxl 版本: {probe.Version}\n";
                                        if (!string.IsNullOrWhiteSpace(probe.DetectedFeatures)) LogText.Text += $"[probe] cjxl 输出特征: {probe.DetectedFeatures}\n";
                                        var combined = (probe.StdOut + probe.StdErr).Trim();
                                        if (!string.IsNullOrEmpty(combined))
                                        {
                                            var shortOut = combined.Length > 200 ? combined.Substring(0, 200) + "..." : combined;
                                            LogText.Text += $"[probe] cjxl 输出: {shortOut}\n";
                                        }
                                    }
                                    else
                                    {
                                        LogText.Text += "[probe] cjxl 运行探测失败或不兼容（已跳过自动启用）\n";
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                    catch { }
                }
                else
                    LogText.Text += "ℹ️ 未检测到 cjxl.exe，JPEG→JXL 将使用 ffmpeg\n";

                // djxl 检测
                DjxlService.ClearCache();
                DjxlService.Detect();
                if (DjxlService.IsAvailable)
                {
                    LogText.Text += $"✅ 检测到 djxl（{DjxlService.DetectedPath}）\n";
                    try
                    {
                        var djxlTag = ExternalToolsDetector.GetFeatureTagFromFileName(DjxlService.DetectedPath);
                        if (!string.IsNullOrEmpty(djxlTag))
                            LogText.Text += $"[cpu] djxl 优化标识: {djxlTag}\n";

                        // 异步探测 djxl 版本与 SIMD
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var path = DjxlService.DetectedPath;
                                if (string.IsNullOrEmpty(path)) return;
                                var probe = await Task.Run(() => ExternalToolsDetector.ProbeExecutable(path, 2000));
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    if (probe != null && probe.IsRunnable)
                                    {
                                        if (!string.IsNullOrWhiteSpace(probe.Version)) LogText.Text += $"[probe] djxl 版本: {probe.Version}\n";
                                        if (probe.SimdFeatures.Count > 0) LogText.Text += $"[probe] djxl SIMD: {string.Join(", ", probe.SimdFeatures)}\n";
                                    }
                                    else
                                        LogText.Text += "[probe] djxl 运行探测失败或不兼容\n";
                                });
                            }
                            catch { }
                        });
                    }
                    catch { }
                }
                else
                    LogText.Text += "ℹ️ 未检测到 djxl.exe，JXL 解码将回退到 ffmpeg\n";

                if (CjpegliService.IsAvailable)
                {
                    LogText.Text += $"✅ 检测到 cjpegli（{CjpegliService.DetectedPath}）\n";
                    try
                    {
                        var tag = ExternalToolsDetector.GetFeatureTagFromFileName(CjpegliService.DetectedPath);
                        if (!string.IsNullOrEmpty(tag))
                        {
                            LogText.Text += $"[cpu] cjpegli 优化标识: {tag}\n";
                        }

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var path = CjpegliService.DetectedPath;
                                if (string.IsNullOrEmpty(path)) return;
                                var probe = await Task.Run(() => ExternalToolsDetector.ProbeExecutable(path, 2000));
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    if (probe != null && probe.IsRunnable)
                                    {
                                        if (!string.IsNullOrWhiteSpace(probe.Version)) LogText.Text += $"[probe] cjpegli 版本: {probe.Version}\n";
                                        if (!string.IsNullOrWhiteSpace(probe.DetectedFeatures)) LogText.Text += $"[probe] cjpegli 输出特征: {probe.DetectedFeatures}\n";
                                        var combined = (probe.StdOut + probe.StdErr).Trim();
                                        if (!string.IsNullOrEmpty(combined))
                                        {
                                            var shortOut = combined.Length > 200 ? combined.Substring(0, 200) + "..." : combined;
                                            LogText.Text += $"[probe] cjpegli 输出: {shortOut}\n";
                                        }
                                    }
                                    else
                                    {
                                        LogText.Text += "[probe] cjpegli 运行探测失败或不兼容（已跳过自动启用）\n";
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                    catch { }
                }
                else
                    LogText.Text += "ℹ️ 未检测到 cjpegli，Jpegli 编码将回退到 ffmpeg/libjpeg\n";
            }

            // ultrahdr_app 检测
            UltrahdrService.Detect();
            if (LogText != null)
            {
                if (UltrahdrService.IsAvailable)
                    LogText.Text += $"✅ 检测到 ultrahdr_app（{UltrahdrService.DetectedPath}）\n";
                else
                    LogText.Text += "ℹ️ 未检测到 ultrahdr_app.exe，Ultra HDR 将使用 ffmpeg libultrahdr（如可用）\n";
            }

            // JxrEncApp 检测
            JxrService.Detect();
            if (LogText != null)
            {
                if (JxrService.IsAvailable)
                    LogText.Text += $"✅ 检测到 JxrEncApp（{JxrService.DetectedPath}）\n";
                else
                    LogText.Text += "ℹ️ 未检测到 JxrEncApp.exe，JPEG XR 将不可用\n";
            }

            // ExifTool 检测与 UI 更新
            ExifToolService.Detect();
            if (LogText != null)
            {
                if (ExifToolService.IsAvailable)
                    LogText.Text += $"✅ 检测到 exiftool（{ExifToolService.DetectedPath}）\n";
            }
            UpdateExifToolPanelState();
            UpdateOptionAvailability();
            RefreshToolsStatusBar();
        }

        private void UpdateQualityLabel()
        {
            if (QualitySlider != null && QualityValue != null)
            {
                var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
                var val = (int)QualitySlider.Value;
                QualityValue.Text = Models.FfmpegOptions.GetQualityLabel(fmt, val, GetCurrentEncoderBackend());
                // 确保数字输入框与滑块保持同步（程序化变更时）
                if (QualityBox != null && !_updatingQuality)
                {
                    _updatingQuality = true;
                    QualityBox.Text = Models.FfmpegOptions.FormatQualityForDisplay(fmt, val, GetCurrentEncoderBackend());
                    _updatingQuality = false;
                }
            }
            RegenerateCommand();
        }

        private string BuildCjpegliPreviewCommand(string input, string output, Models.FfmpegOptions opts)
        {
            // 如果检测到 cjpegli 则生成完整 cjpegli 命令，否则提示不可用
            if (CjpegliService.IsAvailable && !string.IsNullOrWhiteSpace(CjpegliService.DetectedPath))
            {
                var exe = CjpegliService.DetectedPath;
                var args = CjpegliService.BuildCjpegliArguments(input, output, opts);
                return $"\"{exe}\" {args}";
            }
            // 回退：ffmpeg mjpeg 编码（标准 JPEG，非 jpegli）并带说明
            return $"# cjpegli 不可用，回退到 ffmpeg:{Environment.NewLine}ffmpeg -i \"{input}\" -c:v mjpeg -q:v {Math.Clamp(100 - opts.Quality, 2, 31)} \"{output}\"";
        }

        private async void RegenerateCommand()
        {
            if (_suppressCommandRegen || string.IsNullOrWhiteSpace(_inputPath)) return;
            var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            var chroma = ChromaCombo?.SelectedItem as string ?? "auto";
            var bitdepthStr = BitDepthCombo?.SelectedItem as string ?? "auto";
            int? bitdepth = null;
            if (!bitdepthStr.Equals("auto", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(bitdepthStr, out var bd))
                bitdepth = bd;
            var useAdv = UseAdvancedColor?.IsChecked ?? false;
            var useAdvCodec = UseAdvancedCodec?.IsChecked ?? false;
            var autoTh = AutoThreadsCheck?.IsChecked ?? true;
            var singleTh = SingleThreadCheck?.IsChecked ?? false;
            int threads = singleTh ? 1 : autoTh ? Models.FfmpegOptions.ComputeAutoThreads() : (int)(ThreadsBox?.Value ?? 4);

            var backend = GetCurrentEncoderBackend();
            var encName = GetCurrentEncoderName();
            var outputPath = GetOutputPath(_inputPath, fmt);

            // ── 根据编码器后端生成不同命令 ──
            if (backend == EncoderBackend.Cjpegli)
            {
                var useJpegliAdv = UseAdvancedCodec?.IsChecked ?? false;
                var jpegliOpts = new Models.FfmpegOptions
                {
                    Format = fmt,
                    Quality = (int)(QualitySlider?.Value ?? 92),
                    Threads = 1,
                    CjpegliChromaSubsampling = useJpegliAdv ? (JpegliChromaCombo?.SelectedItem as string ?? "auto") : "auto",
                    CjpegliProgressiveId = useJpegliAdv ? JpegliProgressiveCombo?.SelectedIndex switch { 0 => -1, 1 => 0, 2 => 2, _ => -1 } : -1,
                    CjpegliOptimize = useJpegliAdv ? (JpegliOptimizeCheck?.IsChecked ?? true) : true,
                    CjpegliAdaptiveQuant = useJpegliAdv ? (JpegliAdaptiveQuantCheck?.IsChecked ?? true) : true,
                    CjpegliEncoderBackend = useJpegliAdv ? (JpegliEncoderBackendCombo?.SelectedIndex == 1 ? "sjpeg" : "libjpeg") : "libjpeg",
                    CjpegliPsnrTarget = useJpegliAdv ? (float)(JpegliPsnrBox?.Value ?? 0) : 0,
                    CjpegliMultiThreadAvailable = false
                };
                if (CommandText != null)
                {
                    CommandText.Text = BuildCjpegliPreviewCommand(_inputPath, outputPath, jpegliOpts);
                }
                return;
            }

            if (backend == EncoderBackend.Cjxl)
            {
                var effort = useAdvCodec ? (int?)CjxlEffortBox?.Value ?? 7 : 7;
                var progressive = useAdvCodec ? (CjxlProgressiveCheck?.IsChecked ?? false) : false;
                var photonNoise = useAdvCodec ? (int)(CjxlPhotonNoiseBox?.Value ?? 0) : 0;
                var isJpegInput = IsJpegInput(_inputPath);
                var qualityVal = (int)(QualitySlider?.Value ?? 90);
                var distance = (100 - qualityVal) * 15.0 / 100.0;
                var t = threads > 0 ? $" --num_threads={threads}" : "";

                var cmd = new System.Text.StringBuilder();
                cmd.Append("\"").Append(CjxlService.DetectedPath).Append("\" \"")
                   .Append(_inputPath).Append("\" \"").Append(outputPath).Append("\" -e ").Append(effort).Append(t);

                if (isJpegInput)
                {
                    LockLosslessForJxl();
                    cmd.Append(" -d 0 --lossless_jpeg=1");
                }
                else if (LosslessCheck?.IsChecked ?? false)
                {
                    cmd.Append(" -d 0");
                }
                else
                {
                    RestoreLosslessAndQuality();
                    cmd.Append(" -d ").Append($"{distance:F1}");
                }

                if (progressive) cmd.Append(" --progressive");
                if (photonNoise > 0) cmd.Append(" --photon_noise_iso=").Append(photonNoise);

                if (CommandText != null)
                    CommandText.Text = cmd.ToString();

                return;
            }

            // ── Gain Map (Ultra HDR) JPEG：RAW + cjpegli SDR 基础图 → ultrahdr_app ──
            if (backend == EncoderBackend.Ultrahdr && fmt is "jpg" or "jpeg")
            {
                var qualityVal = (int)(QualitySlider?.Value ?? 90);
                var gmq = ParseGainMapQuality();
                var nits = ParseGainMapNits();
                var hasCjpegli = CjpegliService.IsAvailable;
                var cmd = new System.Text.StringBuilder();
                cmd.Append("[两步法] ffmpeg → p010 RAW");
                if (hasCjpegli) cmd.Append(" + cjpegli SDR 优化");
                cmd.Append(" → ultrahdr_app -q ").Append(qualityVal).Append(" -L ").Append(nits);
                if (gmq >= 0) cmd.Append(" -Q ").Append(gmq);
                cmd.Append(" -z \"").Append(outputPath).Append("\"");
                if (CommandText != null)
                    CommandText.Text = cmd.ToString();
                return;
            }

            if (backend == EncoderBackend.Ultrahdr)
            {
                var qualityVal = (int)(QualitySlider?.Value ?? 90);
                var gmq = useAdvCodec ? ParseGainMapQuality() : -1;
                var nits = useAdvCodec ? ParseGainMapNits() : 1000;
                var cmd = new System.Text.StringBuilder();
                cmd.Append("ultrahdr_app -m 0 -p \"<raw>\" -w <W> -h <H> -q ")
                   .Append(qualityVal).Append(" -a 0");
                if (gmq >= 0) cmd.Append(" -Q ").Append(gmq);
                if (nits > 0) cmd.Append(" -L ").Append(nits);
                cmd.Append(" -z \"").Append(outputPath).Append("\"");
                if (CommandText != null)
                    CommandText.Text = cmd.ToString();
                return;
            }

            if (backend == EncoderBackend.Jxr)
            {
                var qualityVal = (int)(QualitySlider?.Value ?? 90);
                var lossless = LosslessCheck?.IsChecked ?? false;
                var q = lossless ? "1.0" : $"{qualityVal / 100.0:F2}";
                var cmd = new System.Text.StringBuilder();
                cmd.Append("JxrEncApp -i <input.bmp> -o \"").Append(outputPath)
                   .Append("\" -q ").Append(q);
                if (CommandText != null)
                    CommandText.Text = cmd.ToString();
                return;
            }

            // --- FFmpeg 后端：JPEG→JXL 无损重封装自动检测 ---
            bool jxlLosslessJpeg = false;
            if (fmt is "jxl" && IsJpegInput(_inputPath))
            {
                if (await EncoderDetectionService.SupportsJxlLosslessJpegAsync())
                {
                    jxlLosslessJpeg = true;
                    LockLosslessForJxl();
                    if (LogText != null)
                        LogText.Text += "[jxl] FFmpeg 检测到 libjxl 支持 lossless_jpeg，将使用无损重封装模式\n";
                }
                else
                {
                    if (LogText != null)
                        LogText.Text += "[jxl] FFmpeg libjxl 不支持 lossless_jpeg，可选择 cjxl 编码器启用极速模式\n";
                }
            }
            else
            {
                RestoreLosslessAndQuality();
            }

            var opts = new Models.FfmpegOptions
            {
                Format = fmt, Quality = (int)(QualitySlider?.Value ?? 92),
                Chroma = chroma, BitDepth = bitdepth,
                ColorSpace = ColorSpaceCombo?.SelectedItem as string,
                UseAdvancedColorParameters = useAdv,
                ColorPrimaries = useAdv ? (ColorPrimariesCombo?.SelectedItem as string) : null,
                ColorTrc = useAdv ? (ColorTrcCombo?.SelectedItem as string) : null,
                ColorMatrix = useAdv ? (ColorMatrixCombo?.SelectedItem as string) : null,
                Encoder = encName, EncoderBackend = backend, Threads = threads,
                MetadataMode = GetMetadataMode(),
                Lossless = LosslessCheck?.IsChecked ?? false,
                PngPred = useAdvCodec ? (PngPredCombo?.SelectedItem as string) : null,
                WebpPreset = useAdvCodec ? (WebpPresetCombo?.SelectedItem as string) : null,
                WebpCompressionLevel = useAdvCodec ? (int?)WebpCompressionBox?.Value : null,
                AvifCpuUsed = useAdvCodec ? (int?)AvifCpuUsedBox?.Value : null,
                AvifStillPicture = useAdvCodec ? AvifStillPictureCheck?.IsChecked : null,
                AvifRowMt = useAdvCodec ? AvifRowMtCheck?.IsChecked : null,
                AvifTune = useAdvCodec ? (AvifTuneCombo?.SelectedItem as string) : null,
                AvifPreset = GetAvifPresetValue(),
                AvifSvtPreset = useAdvCodec ? (int?)SvtPresetBox?.Value : null,
                AvifSvtTune = useAdvCodec ? (SvtTuneCombo?.SelectedItem as string) : null,
                AvifHwPreset = useAdvCodec ? (HwPresetCombo?.SelectedItem as string) : null,
                JxlEffort = useAdvCodec ? (int?)JxlEffortBox?.Value : null,
                JxlModular = useAdvCodec ? JxlModularCheck?.IsChecked : null,
                JxlLosslessJpeg = jxlLosslessJpeg,
                CjxlProgressive = useAdvCodec ? (CjxlProgressiveCheck?.IsChecked ?? false) : false,
                CjxlPhotonNoiseIso = useAdvCodec ? (int)(CjxlPhotonNoiseBox?.Value ?? 0) : 0,
                JpegHuffman = useAdvCodec ? (JpegHuffmanCombo?.SelectedItem as string) : null,
                JpegDct = useAdvCodec ? (JpegDctCombo?.SelectedItem as string is "auto" ? null : JpegDctCombo?.SelectedItem as string) : null,
                JpegProgressiveId = useAdvCodec ? ParseJpegProgressiveId() : -1,
                JpegGainMap = (GetCurrentEncoderBackend() == EncoderBackend.Ultrahdr),
                JpegGainMapQuality = ParseGainMapQuality(),
                JpegGainMapTargetNits = ParseGainMapNits(),
                JpegGainMapHdrCf = ParseGainMapHdrCf(),
                JpegGainMapDownsample = ParseGainMapDownsample(),
                JpegGainMapMultiChannel = (UseAdvancedCodec?.IsChecked == true)
                    && (JpegGainMapMultiChannelCheck?.IsChecked ?? false),
                TiffCompressionAlgo = useAdvCodec ? (TiffCompressionCombo?.SelectedItem as string) : null,
                StripExifGps = StripExifGpsCheck?.IsChecked ?? true,
                StripExifTime = StripExifTimeCheck?.IsChecked ?? false,
                StripExifCamera = StripExifCameraCheck?.IsChecked ?? false,
                StripExifAll = StripExifAllCheck?.IsChecked ?? false,
                StripXmp = StripXmpCheck?.IsChecked ?? false
            };
            if (CommandText != null)
            {
                var args = Services.FfmpegCommandBuilder.BuildArguments(opts, _inputPath, outputPath);
                CommandText.Text = "ffmpeg " + args;
            }
        }

        // ── JXL 无损控件锁定/恢复 ──
        private void LockLosslessForJxl()
        {
            if (LosslessCheck != null)
            {
                LosslessCheck.IsChecked = true;
                LosslessCheck.IsEnabled = false;
            }
            if (QualitySlider != null)
                QualitySlider.IsEnabled = false;
            if (QualityBox != null)
                QualityBox.IsEnabled = false;
        }

        private void RestoreLosslessAndQuality()
        {
            if (LosslessCheck != null && !LosslessCheck.IsEnabled
                && _currentCapabilities?.SupportsLossless == true)
            {
                LosslessCheck.IsEnabled = true;
            }
            if (QualitySlider != null && !QualitySlider.IsEnabled
                && _currentCapabilities?.SupportsQuality == true)
            {
                QualitySlider.IsEnabled = true;
                if (QualityBox != null) QualityBox.IsEnabled = true;
            }
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

            RegenerateCommand();
        }

        private int GetConcurrencyValue()
        {
            if (ConcurrencyBox == null) return 16;
            if (int.TryParse(ConcurrencyBox.Text, out var v))
                return Math.Clamp(v, 1, 128);
            return 16;
        }

        private void ValidateAndApplyConcurrency()
        {
            if (ConcurrencyBox == null) return;
            var val = GetConcurrencyValue();
            var text = val.ToString();
            if (ConcurrencyBox.Text != text)
                ConcurrencyBox.Text = text;
            // 红色边框反馈：超出范围或无效输入
            var valid = int.TryParse(ConcurrencyBox.Text, out var raw) && raw >= 1 && raw <= 128;
            ConcurrencyBox.BorderBrush = valid
                ? Avalonia.Media.Brushes.Transparent
                : Avalonia.Media.Brushes.Red;
            if (valid)
            {
                AppSettingsService.Current.MaxQueueSize = val;
                AppSettingsService.Save();
            }
            UpdateConcurrencyLabel();
            UpdateQueueCountLabel();
        }

        private void ConcurrencyUp_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (ConcurrencyBox == null) return;
            var val = GetConcurrencyValue();
            if (val < 128)
                ConcurrencyBox.Text = (val + 1).ToString();
            ValidateAndApplyConcurrency();
        }

        private void ConcurrencyDown_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (ConcurrencyBox == null) return;
            var val = GetConcurrencyValue();
            if (val > 1)
                ConcurrencyBox.Text = (val - 1).ToString();
            ValidateAndApplyConcurrency();
        }

        private void UpdateQueueCountLabel()
        {
            if (QueueCountLabel != null)
                QueueCountLabel.Text = $"队列: {_queueView.Count} 项";
            if (FileCountLabel != null)
                FileCountLabel.Text = $"总文件: {_queueView.Count}";
        }

        /// <summary>更新进度显示：队列 N/M + 已用时间 + 剩余预估</summary>
        private void UpdateProgressDisplay()
        {
            // 队列进度
            var completed = _queueItems.Count(i => i.CompletedAt.HasValue || i.Status == "已删除");
            var total = _queueItems.Count;
            if (QueueProgressLabel != null)
                QueueProgressLabel.Text = total > 0 ? $"队列: {completed} / {total}" : "队列: 0 / 0";

            // 已用时间：当前处理中任务
            var processing = _queueItems.FirstOrDefault(i => i.Status == "处理中" && i.StartedAt.HasValue);
            if (processing != null && ElapsedLabel != null)
            {
                var elapsed = DateTimeOffset.UtcNow - processing.StartedAt!.Value;
                ElapsedLabel.Text = elapsed.TotalMinutes >= 1
                    ? $"已用: {elapsed.TotalMinutes:F1}m"
                    : $"已用: {elapsed.TotalSeconds:F0}s";
            }
            else if (ElapsedLabel != null)
            {
                ElapsedLabel.Text = "";
            }

            // 剩余预估：基于已完成任务的平均耗时
            if (QueueEtaLabel != null)
            {
                var finishedItems = _queueItems.Where(i => i.StartedAt.HasValue && i.CompletedAt.HasValue).ToList();
                if (finishedItems.Count > 0 && total > completed)
                {
                    var avgSec = finishedItems.Average(i => (i.CompletedAt!.Value - i.StartedAt!.Value).TotalSeconds);
                    var remaining = total - completed;
                    var etaSec = avgSec * remaining;
                    if (etaSec >= 3600)
                        QueueEtaLabel.Text = $"预计剩余: {etaSec / 3600:F1}h";
                    else if (etaSec >= 60)
                        QueueEtaLabel.Text = $"预计剩余: {etaSec / 60:F1}m";
                    else
                        QueueEtaLabel.Text = $"预计剩余: {etaSec:F0}s";
                }
                else
                {
                    QueueEtaLabel.Text = "";
                }
            }
        }

        private void UpdateConcurrencyLabel()
        {
            if (ConcurrencyLabel != null)
                ConcurrencyLabel.Text = $"(同时 {GetConcurrencyValue()} 个任务)";
        }

        private void FormatCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateOptionAvailability();
            _ = RefreshEncoderListAsync();
        }

        // 静态图片模式格式列表
        private static readonly string[] StillFormats = { "JPEG", "PNG", "WebP", "AVIF", "JPEG XL", "TIFF" };
        // 动图模式格式列表
        private static readonly string[] AnimatedFormats = { "GIF", "WebP (动图)", "PNG (APNG)", "AVIF (动图)", "JPEG XL (动图)" };

        private static int ParseInt(string? text, int defaultValue, int min, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return defaultValue;
            if (int.TryParse(text, out var val))
                return Math.Clamp(val, min, max);
            return defaultValue;
        }

        private static int? ParseOptionalInt(string? text, int min, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (int.TryParse(text, out var val))
                return Math.Clamp(val, min, max);
            return null;
        }

        private static double? ParseOptionalDouble(string? text, double min, double max)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (double.TryParse(text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var val))
                return Math.Clamp(val, min, max);
            return null;
        }

        /// <summary>
        /// 获取当前模式下的允许文件扩展名。动图模式下包含视频格式。
        /// </summary>
        private string[] GetEnabledExtensionsForCurrentMode()
        {
            var isAnimation = ConversionModeCombo?.SelectedIndex == 1;
            return isAnimation
                ? AppSettingsService.Current.GetEnabledExtensionsIncludingVideo()
                : AppSettingsService.Current.GetEnabledExtensions();
        }

        private void ConversionMode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (FormatCombo == null || ConversionModeCombo == null) return;

            var previousFormat = FormatCombo.SelectedItem as string ?? "";
            var isAnimated = ConversionModeCombo.SelectedIndex == 1;
            var targetFormats = isAnimated ? AnimatedFormats : StillFormats;

            // 动图模式：控制面板可见性
            if (AnimationPanel != null)
                AnimationPanel.IsVisible = isAnimated;
            // 视频时长控制仅在动图模式下显示
            if (AnimationDurationPanel != null)
                AnimationDurationPanel.IsVisible = isAnimated;

            // 更新 FormatCombo 选项列表
            FormatCombo.Items!.Clear();
            foreach (var f in targetFormats)
                FormatCombo.Items.Add(f);

            // 尝试保留之前的选中项（如果在新列表中）
            var idx = Array.IndexOf(targetFormats, previousFormat);
            if (idx >= 0)
            {
                FormatCombo.SelectedIndex = idx;
            }
            else
            {
                FormatCombo.SelectedIndex = 0;
                // 需要手动触发 SelectionChanged（列表重建后 SelectedIndex=0 可能不会自动触发）
                UpdateOptionAvailability();
                _ = RefreshEncoderListAsync();
            }
        }

        private async Task RefreshEncoderListAsync()
        {
            if (EncoderCombo == null || FormatCombo == null) return;

            var fmt1 = NormalizeFormat(FormatCombo.SelectedItem as string);
            var isAnimMode = ConversionModeCombo?.SelectedIndex == 1;

            var encoders = await EncoderDetectionService.GetEncodersForFormatAsync(fmt1);

            // 动图 JXL：cjxl 不支持动画，从编码器列表中移除
            if (isAnimMode && fmt1 == "jxl")
                encoders = encoders.Where(e => e.Backend != EncoderBackend.Cjxl).ToList();

            EncoderCombo.Items!.Clear();
            if (encoders.Count > 0)
            {
                foreach (var enc in encoders)
                {
                    EncoderCombo.Items.Add(enc.ToString());
                }

                // 尝试选中默认编码器
                var defaultEnc = EncoderDetectionService.GetDefaultEncoder(fmt1);
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

        /// <summary>
        /// 获取当前选中的编码器后端类型
        /// </summary>
        private EncoderBackend GetCurrentEncoderBackend()
        {
            var encStr = EncoderCombo?.SelectedItem as string ?? "";
            return EncoderInfo.ParseBackend(encStr);
        }

        /// <summary>
        /// 获取当前选中的编码器名称（FFmpeg 编码器名或外部工具标识）
        /// </summary>
        private string GetCurrentEncoderName()
        {
            var encStr = EncoderCombo?.SelectedItem as string ?? "";
            return EncoderInfo.ParseEncoderName(encStr);
        }

        /// <summary>
        /// 将 UI 显示名称映射为内部小写格式名。
        /// "JPEG"→"jpg", "JPEG LI"→"jpg"（JPEG LI 现已整合为 JPEG 编码器选项）, "JPEG XL"→"jxl", 其他→小写
        /// </summary>
        private static string NormalizeFormat(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return "jpeg";
            return displayName.Trim() switch
            {
                "JPEG" => "jpg",
                "JPEG LI" => "jpg",  // 已整合到 JPEG 格式的 cjpegli 编码器选项中
                "JPEG XL" => "jxl",
                "JPEG XL (动图)" => "jxl",
                "PNG" => "png",
                "PNG (APNG)" => "apng",
                "WebP" => "webp",
                "WebP (动图)" => "webp",
                "AVIF" => "avif",
                "AVIF (动图)" => "avif",
                "TIFF" => "tiff",
                "JPEG XR" => "jxr",
                "GIF" => "gif",
                _ => displayName.Trim().ToLower()
            };
        }

        private void UpdateOptionAvailability()
        {
            if (FormatCombo == null) return;
            var fmt3 = NormalizeFormat(FormatCombo.SelectedItem as string);
            _currentCapabilities = FormatCapabilitiesService.GetCapabilities(fmt3);

            // 批量更新期间抑制 RegenerateCommand，避免冗余调用导致 UI 卡顿
            _suppressCommandRegen = true;
            try
            {

            if (_currentCapabilities != null)
            {
                if (QualitySlider != null)
                {
                    QualitySlider.IsEnabled = _currentCapabilities.SupportsQuality;
                    if (QualityBox != null) QualityBox.IsEnabled = _currentCapabilities.SupportsQuality;
                    if (_currentCapabilities.SupportsQuality)
                    {
                        // 切换到该格式的视觉无损默认值
                        QualitySlider.Value = Models.FfmpegOptions.GetDefaultQuality(fmt3);
                    }
                }
                if (ChromaCombo != null) ChromaCombo.IsEnabled = _currentCapabilities.SupportsChroma;
                if (BitDepthCombo != null)
                {
                    // 所有格式均可选 auto，因此始终启用
                    BitDepthCombo.IsEnabled = true;
                    // 动态更新位深选项：始终首位为 auto
                    BitDepthCombo.Items!.Clear();
                    BitDepthCombo.Items.Add("auto");
                    foreach (var bd in _currentCapabilities.SupportedBitDepths)
                    {
                        var s = bd.ToString();
                        if (!BitDepthCombo.Items.Contains(s))
                            BitDepthCombo.Items.Add(s);
                    }
                    if (BitDepthCombo.Items.Count > 0) BitDepthCombo.SelectedIndex = 0;
                }
                if (MetadataModeCombo != null) MetadataModeCombo.IsEnabled = _currentCapabilities.SupportsMetadata;
                if (LosslessCheck != null)
                {
                    if (fmt3 is "png" or "tiff" or "apng")
                    {
                        // PNG/TIFF/APNG 纯无损格式，强制勾选且锁定
                        LosslessCheck.IsEnabled = false;
                        LosslessCheck.IsChecked = true;
                        // 质量锁定 100%，滑块和输入框均禁用
                        if (QualitySlider != null)
                        {
                            QualitySlider.Value = 100;
                            QualitySlider.IsEnabled = false;
                        }
                        if (QualityBox != null)
                        {
                            QualityBox.Text = Models.FfmpegOptions.FormatQualityForDisplay(fmt3, 100, GetCurrentEncoderBackend());
                            QualityBox.IsEnabled = false;
                        }
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
                if (QualityBox != null) QualityBox.IsEnabled = true;
                if (ChromaCombo != null) ChromaCombo.IsEnabled = true;
                if (BitDepthCombo != null) BitDepthCombo.IsEnabled = true;
                if (LosslessCheck != null) LosslessCheck.IsEnabled = true;
                if (ColorSpaceCombo != null) ColorSpaceCombo.IsEnabled = true;
            }

            UpdateCodecPanelVisibility(fmt3);
            UpdateThreadAvailabilityForFormat(fmt3);
            }
            finally
            {
                _suppressCommandRegen = false;
            }
            RegenerateCommand();
        }

        /// <summary>
        /// 根据当前编码器后端更新线程控件的可用性。
        /// cjpegli 不支持多线程，强制锁定为单线程。
        /// </summary>
        private void UpdateThreadAvailabilityForFormat(string fmt)
        {
            var backend = GetCurrentEncoderBackend();
            var isSingleThreadOnly = backend == EncoderBackend.Cjpegli;
            if (isSingleThreadOnly)
            {
                if (AutoThreadsCheck != null)
                {
                    AutoThreadsCheck.IsEnabled = false;
                    AutoThreadsCheck.IsChecked = false;
                }
                if (SingleThreadCheck != null)
                {
                    SingleThreadCheck.IsEnabled = false;
                    SingleThreadCheck.IsChecked = true;
                }
                if (ThreadsBox != null)
                {
                    ThreadsBox.IsEnabled = false;
                    ThreadsBox.Value = 1;
                }
                if (ThreadHintLabel != null)
                    ThreadHintLabel.Text = "(该编码器仅支持单线程)";
            }
            else
            {
                if (AutoThreadsCheck != null) AutoThreadsCheck.IsEnabled = true;
                if (SingleThreadCheck != null) SingleThreadCheck.IsEnabled = true;
                UpdateThreadControls();
            }
        }

        /// <summary>
        /// 当 cjpegli 编码器后端切换时，更新 PSNR 目标面板的可见性。
        /// 仅 sjpeg 后端支持 PSNR 目标搜索。
        /// </summary>
        private void UpdateJpegliPsnrVisibility()
        {
            if (JpegliEncoderBackendCombo == null || JpegliPsnrPanel == null) return;
            var isSjpeg = JpegliEncoderBackendCombo.SelectedIndex == 1;
            JpegliPsnrPanel.IsVisible = isSjpeg;
        }

        private void UpdateCodecPanelVisibility(string fmt)
        {
            // 全部隐藏
            if (PngCodecPanel != null) PngCodecPanel.IsVisible = false;
            if (GifCodecPanel != null) GifCodecPanel.IsVisible = false;
            if (ApngCodecPanel != null) ApngCodecPanel.IsVisible = false;
            if (WebpCodecPanel != null) WebpCodecPanel.IsVisible = false;
            if (AvifCodecPanel != null) AvifCodecPanel.IsVisible = false;
            if (JxlCodecPanel != null) JxlCodecPanel.IsVisible = false;
            if (JpegCodecPanel != null) JpegCodecPanel.IsVisible = false;
            if (JpegliCodecPanel != null) JpegliCodecPanel.IsVisible = false;
            if (JpegGainMapPanel != null) JpegGainMapPanel.IsVisible = false;
            if (TiffCodecPanel != null) TiffCodecPanel.IsVisible = false;
            if (JxrCodecPanel != null) JxrCodecPanel.IsVisible = false;

            // 恢复动图模式下可能被隐藏的控件默认值
            if (WebpLosslessPanel != null) WebpLosslessPanel.IsVisible = true;

            var backend = GetCurrentEncoderBackend();
            var isAnimMode = ConversionModeCombo?.SelectedIndex == 1;

            // 按格式+编码器后端+动图模式显示对应面板
            switch (fmt)
            {
                case "png": if (PngCodecPanel != null) PngCodecPanel.IsVisible = true; break;

                case "apng":
                    if (ApngCodecPanel != null) ApngCodecPanel.IsVisible = true;
                    break;

                case "gif":
                    if (GifCodecPanel != null) GifCodecPanel.IsVisible = true;
                    break;

                case "webp":
                    if (WebpCodecPanel != null) WebpCodecPanel.IsVisible = true;
                    // 动图 WebP (libwebp_anim) 不支持无损压缩级别，隐藏相关控件
                    if (isAnimMode && WebpLosslessPanel != null)
                        WebpLosslessPanel.IsVisible = false;
                    break;

                case "avif":
                    if (AvifCodecPanel != null) AvifCodecPanel.IsVisible = true;
                    UpdateAvifEncoderPanel();
                    // 动图 AVIF：强制禁用 still-picture + 隐藏 LosslessCheck
                    if (AvifStillPictureCheck != null)
                    {
                        AvifStillPictureCheck.IsEnabled = !isAnimMode;
                        if (isAnimMode) AvifStillPictureCheck.IsChecked = false;
                    }
                    if (isAnimMode && LosslessCheck != null)
                        LosslessCheck.IsEnabled = false;
                    break;

                case "jxl":
                    if (JxlCodecPanel != null) JxlCodecPanel.IsVisible = true;
                    // 动图 JXL：cjxl 不支持动画，强制只显示 FFmpeg 面板（隐藏 cjxl 选项）
                    if (isAnimMode)
                    {
                        if (JxlFfmpegPanel != null) JxlFfmpegPanel.IsVisible = true;
                        if (JxlCjxlPanel != null) JxlCjxlPanel.IsVisible = false;
                    }
                    else
                    {
                        // 根据后端切换 JXL 子面板
                        if (JxlFfmpegPanel != null)
                            JxlFfmpegPanel.IsVisible = backend != EncoderBackend.Cjxl;
                        if (JxlCjxlPanel != null)
                            JxlCjxlPanel.IsVisible = backend == EncoderBackend.Cjxl;
                    }
                    // 动图 JXL：禁用 LosslessCheck（libjxl_anim 不支持无损模式）
                    if (isAnimMode && LosslessCheck != null)
                        LosslessCheck.IsEnabled = false;
                    break;

                case "jpg": case "jpeg":
                    // jpg/jpeg 格式：根据编码器后端显示不同高级面板
                    if (backend == EncoderBackend.Cjpegli)
                    {
                        if (JpegliCodecPanel != null) JpegliCodecPanel.IsVisible = true;
                        if (JpegGainMapPanel != null) JpegGainMapPanel.IsVisible = false;
                    }
                    else if (backend == EncoderBackend.Ultrahdr)
                    {
                        // ultrahdr 后端：显示 Gain Map 面板 + 基础 JPEG 选项，隐藏 FFmpeg JPEG 面板
                        if (JpegCodecPanel != null) JpegCodecPanel.IsVisible = true;
                        if (JpegliCodecPanel != null) JpegliCodecPanel.IsVisible = false;
                        if (JpegGainMapPanel != null) JpegGainMapPanel.IsVisible = true;
                    }
                    else
                    {
                        if (JpegCodecPanel != null) JpegCodecPanel.IsVisible = true;
                        // Gain Map 仅 libultrahdr 编码器可用时显示
                        if (JpegGainMapPanel != null)
                            JpegGainMapPanel.IsVisible = EncoderDetectionService.IsLibultrahdrAvailable;
                    }
                    break;
                case "tiff": if (TiffCodecPanel != null) TiffCodecPanel.IsVisible = true; break;
                case "jxr": if (JxrCodecPanel != null) JxrCodecPanel.IsVisible = true; break;
            }
        }

        /// <summary>
        /// 根据当前 AVIF 编码器切换显示不同的高级选项面板
        /// </summary>
        private void UpdateAvifEncoderPanel()
        {
            var enc = EncoderCombo?.SelectedItem as string ?? "";
            var isSvt = enc.StartsWith("libsvt", StringComparison.OrdinalIgnoreCase);
            var isLibaom = enc.StartsWith("libaom", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(enc);

            // 隐藏所有子面板
            if (LibaomAvifPanel != null) LibaomAvifPanel.IsVisible = false;
            if (SvtAvifPanel != null) SvtAvifPanel.IsVisible = false;
            if (HwAvifPanel != null) HwAvifPanel.IsVisible = false;

            // 根据编码器显示对应面板
            if (isLibaom)
            {
                if (LibaomAvifPanel != null) LibaomAvifPanel.IsVisible = true;
            }
            else if (isSvt)
            {
                if (SvtAvifPanel != null) SvtAvifPanel.IsVisible = true;
            }
            else
            {
                // 硬件编码器 / 其他
                if (HwAvifPanel != null) HwAvifPanel.IsVisible = true;
            }
        }

        /// <summary>
        /// 获取当前编码器对应的 AvifPreset 值（SVT: 数值字符串, libaom: null）
        /// </summary>
        private string? GetAvifPresetValue()
        {
            var enc = EncoderCombo?.SelectedItem as string ?? "";
            if (enc.StartsWith("libsvt", StringComparison.OrdinalIgnoreCase))
                return SvtPresetBox?.Value.ToString();
            return null; // libaom: usage 由 tune IQ 自动管理
        }

        /// <summary>
        /// 从 UI 下拉框获取当前元数据处理模式
        /// </summary>
        private Models.MetadataMode GetMetadataMode()
        {
            if (MetadataModeCombo?.SelectedIndex == 1)
                return Models.MetadataMode.StripAll;
            return Models.MetadataMode.PreserveAll;
        }

        /// <summary>
        /// 根据 exiftool 是否可用，显示/隐藏 ExifTool 面板，
        /// 并根据当前元数据模式（保留/删除全部）启用/禁用其选项
        /// </summary>
        private void UpdateExifToolPanelState()
        {
            var exifAvailable = ExifToolService.IsAvailable;
            var isPreserveMode = GetMetadataMode() == Models.MetadataMode.PreserveAll;

            if (ExifToolPanel != null)
                ExifToolPanel.IsVisible = exifAvailable;

            if (ExifToolHint != null && exifAvailable)
                ExifToolHint.Text = isPreserveMode
                    ? "已检测到 exiftool，可选择性删除以下元数据："
                    : "元数据模式为「删除全部」，exiftool 选项不生效";

            var exifEnabled = exifAvailable && isPreserveMode;
            if (StripExifGpsCheck != null) StripExifGpsCheck.IsEnabled = exifEnabled;
            if (StripExifTimeCheck != null) StripExifTimeCheck.IsEnabled = exifEnabled;
            if (StripExifCameraCheck != null) StripExifCameraCheck.IsEnabled = exifEnabled;
            if (StripExifAllCheck != null) StripExifAllCheck.IsEnabled = exifEnabled;
            if (StripXmpCheck != null) StripXmpCheck.IsEnabled = exifEnabled;
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

            var isAnimationMode = ConversionModeCombo?.SelectedIndex == 1;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = isAnimationMode ? "选择图片或视频文件" : "选择图片文件",
                AllowMultiple = false,
                FileTypeFilter = isAnimationMode
                    ? AppSettingsService.Current.GetAnimationFilePickerFilter()
                    : AppSettingsService.Current.GetImageFilePickerFilter()
            });

            if (files != null && files.Count > 0)
            {
                _inputPath = files[0].Path.LocalPath;
                // 单文件选择时，不设置输入基目录（仅在选择文件夹时保留结构）
                _inputBaseDir = null;
                AddToMediaFiles(files.Select(f => f.Path.LocalPath));
                // 单文件选择时清理任何残留的批量映射
                _selectedFileBaseDirs.Clear();
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
                    _selectedFileBaseDirs.TryGetValue(file, out var baseDir);
                    AddSingleToQueue(file, baseDir);
                }
                if (LogText != null) LogText.Text += $"已批量添加 {_selectedFiles.Count} 个文件到队列\n";
                _selectedFiles.Clear();
                _selectedFileBaseDirs.Clear();
                return;
            }

            // _selectedFiles 为空但 _mediaFiles 有内容：从已选文件列表批量添加
            if (_mediaFiles.Count > 0)
            {
                foreach (var file in _mediaFiles)
                {
                    _selectedFileBaseDirs.TryGetValue(file, out var baseDir);
                    AddSingleToQueue(file, baseDir);
                }
                if (LogText != null) LogText.Text += $"已从列表批量添加 {_mediaFiles.Count} 个文件到队列\n";
                return;
            }

            if (string.IsNullOrWhiteSpace(_inputPath))
            {
                if (LogText != null) LogText.Text += "请先选择文件，再添加到队列\n";
                return;
            }

            AddSingleToQueue(_inputPath);
        }

        /// <summary>
        /// 添加单个文件到队列。返回 true 表示成功添加。
        /// </summary>
        private bool AddSingleToQueue(string inputPath, string? inputBaseDir = null)
        {
            // 注：队列本身无容量上限，"并行编码任务数"仅控制同时运行的任务数
            var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            var chroma = ChromaCombo?.SelectedItem as string ?? "4:2:0";
            var bitdepthStr = BitDepthCombo?.SelectedItem as string ?? "auto";
            int? bitdepth = null;
            if (!bitdepthStr.Equals("auto", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(bitdepthStr, out var bd))
                bitdepth = bd;
            var encoderStr = EncoderCombo?.SelectedItem as string ?? "";
            var encoderName = EncoderInfo.ParseEncoderName(encoderStr);
            var encoderBackend = EncoderInfo.ParseBackend(encoderStr);

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
                Encoder = encoderName, EncoderBackend = encoderBackend,
                Threads = threads,
                MetadataMode = GetMetadataMode(),
                Lossless = LosslessCheck?.IsChecked ?? false,
                PngPred = useAdvCodec ? (PngPredCombo?.SelectedItem as string) : null,
                WebpPreset = useAdvCodec ? (WebpPresetCombo?.SelectedItem as string) : null,
                WebpCompressionLevel = useAdvCodec ? (int?)WebpCompressionBox?.Value : null,
                AvifCpuUsed = useAdvCodec ? (int?)AvifCpuUsedBox?.Value : null,
                AvifStillPicture = useAdvCodec ? AvifStillPictureCheck?.IsChecked : null,
                AvifRowMt = useAdvCodec ? AvifRowMtCheck?.IsChecked : null,
                AvifTune = useAdvCodec ? (AvifTuneCombo?.SelectedItem as string) : null,
                AvifPreset = GetAvifPresetValue(),
                AvifSvtPreset = useAdvCodec ? (int?)SvtPresetBox?.Value : null,
                AvifSvtTune = useAdvCodec ? (SvtTuneCombo?.SelectedItem as string) : null,
                AvifHwPreset = useAdvCodec ? (HwPresetCombo?.SelectedItem as string) : null,
                JxlEffort = useAdvCodec ? (int?)JxlEffortBox?.Value : null,
                JxlModular = useAdvCodec ? JxlModularCheck?.IsChecked : null,
                JxlLosslessJpeg = fmt is "jxl" && IsJpegInput(inputPath),
                JpegHuffman = useAdvCodec ? (JpegHuffmanCombo?.SelectedItem as string) : null,
                JpegDct = useAdvCodec ? (JpegDctCombo?.SelectedItem as string is "auto" ? null : JpegDctCombo?.SelectedItem as string) : null,
                JpegProgressiveId = useAdvCodec ? ParseJpegProgressiveId() : -1,
                JpegGainMap = (encoderBackend == EncoderBackend.Ultrahdr),
                JpegGainMapQuality = ParseGainMapQuality(),
                JpegGainMapTargetNits = ParseGainMapNits(),
                JpegGainMapHdrCf = ParseGainMapHdrCf(),
                JpegGainMapDownsample = ParseGainMapDownsample(),
                JpegGainMapMultiChannel = (UseAdvancedCodec?.IsChecked == true)
                    && (JpegGainMapMultiChannelCheck?.IsChecked ?? false),
                TiffCompressionAlgo = useAdvCodec ? (TiffCompressionCombo?.SelectedItem as string) : null,
                StripExifGps = StripExifGpsCheck?.IsChecked ?? true,
                StripExifTime = StripExifTimeCheck?.IsChecked ?? false,
                StripExifCamera = StripExifCameraCheck?.IsChecked ?? false,
                StripExifAll = StripExifAllCheck?.IsChecked ?? false,
                StripXmp = StripXmpCheck?.IsChecked ?? false,
                // 动图参数
                AnimationFps = ParseOptionalInt(AnimationFpsBox?.Text, 1, 60),
                AnimationLoop = ParseInt(AnimationLoopBox?.Text, 0, -1, 999),
                GifPaletteOptimize = GifPaletteCheck?.IsChecked ?? true,
                GifDither = GifDitherCheck?.IsChecked ?? true,
                AnimationScaleW = ParseOptionalInt(AnimationScaleWBox?.Text, 0, 4096) ?? 0,
                AnimationDuration = ParseOptionalDouble(AnimationDurationBox?.Text, 0.1, 3600) ?? 0
            };

            // Gain Map 模式下使用 JpegProgressiveId，默认 Baseline
            if (options.JpegGainMap)
                options.CjpegliProgressiveId = options.JpegProgressiveId switch { 1 => 2, _ => 0 };

            var outp = GetOutputPath(inputPath, options.Format, inputBaseDir);
            var item = new Models.QueueItem { InputPath = inputPath, OutputPath = outp, Options = options, InputBaseDir = inputBaseDir };

            // 生成实际将执行的命令并存入队列项（供详情窗口展示）
            item.Command = BuildQueueItemCommand(item);

            _queueProcessor.Add(item);
            _queueView.Add(item);
            _queueItems.Add(item);
            if (LogText != null) LogText.Text += $"已添加到队列: {item.InputPath}\n";

            // 自动生成指令预览
            if (CommandText != null)
            {
                CommandText.Text = item.Command;
            }
            return true;
        }

        private void StartQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            int concurrency = GetConcurrencyValue();
            // 重新排队已停止和失败的项目
            _queueProcessor.RequeueStoppedAndFailed(_queueItems);
            // 如果设置为保留输入目录结构，则在开始队列前为所有待处理项预先生成输出路径与所在目录，
            // 并更新每项的 OutputPath（以防用户在入队后修改了输出目录）。
            if (AppSettingsService.Current.PreserveInputFolderStructure)
            {
                foreach (var qi in _queueItems)
                {
                    try
                    {
                        // 重新计算输出路径并写回队列项（使用每项记录的输入基目录）
                        qi.OutputPath = GetOutputPath(qi.InputPath, qi.Options.Format, qi.InputBaseDir);
                        var dir = Path.GetDirectoryName(qi.OutputPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        // 生成并记录将要执行的命令预览，便于日志追踪
                        qi.Command = BuildQueueItemCommand(qi);
                        qi.Log += $"[cmd-preview] {qi.Command}\n";
                    }
                    catch { }
                }
            }

            _queueProcessor.Start(concurrency);
            _isQueueRunning = true;
            // 如果复选框已勾选，则在启动后请求完成当前队列后停止
            if (StopAfterCurrentCheck?.IsChecked == true)
            {
                _queueProcessor.StopAfterCurrentQueue();
                if (LogText != null) LogText.Text += "已设置：完成当前队列后停止\n";
            }
            if (ClearQueueButton != null)
                ClearQueueButton.IsEnabled = false;
            if (LogText != null) LogText.Text += $"队列开始，并行: {concurrency} 个任务\n";
        }

        private void StopQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _queueProcessor.Stop();
            _isQueueRunning = false;
            if (LogText != null) LogText.Text += "队列已停止\n";
            if (ClearQueueButton != null)
                ClearQueueButton.IsEnabled = true;
        }

        private void OnQueueItemUpdated(Models.QueueItem item)
        {
            Dispatcher.UIThread.Post(() =>
            {
                // 若该项已被标记为删除，从 UI 中移除
                if (item.Status == "已删除")
                {
                    var idx = _queueItems.FindIndex(x => ReferenceEquals(x, item));
                    if (idx >= 0 && idx < _queueView.Count)
                    {
                        _queueView.RemoveAt(idx);
                        _queueItems.RemoveAt(idx);
                        return;
                    }

                    // 回退：通过输入路径匹配清理残留项（同时清理 _queueItems 和 _queueView）
                    var fname = Path.GetFileName(item.InputPath);
                    for (int j = _queueView.Count - 1; j >= 0; j--)
                    {
                        if (Path.GetFileName(_queueView[j].InputPath)
                            .Equals(fname, StringComparison.OrdinalIgnoreCase))
                        {
                            _queueView.RemoveAt(j);
                        }
                    }
                    for (int j = _queueItems.Count - 1; j >= 0; j--)
                    {
                        if (Path.GetFileName(_queueItems[j].InputPath)
                            .Equals(fname, StringComparison.OrdinalIgnoreCase))
                        {
                            _queueItems.RemoveAt(j);
                        }
                    }
                }
                // Status 变更由 INotifyPropertyChanged + DataTemplate 绑定自动反映到 UI
                // 若"仅显示报错"模式开启，刷新过滤
                if (ShowErrorsOnlyCheck?.IsChecked == true)
                    ApplyErrorOnlyFilter();
            });
        }

        private void OnQueueStopped()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _isQueueRunning = false;
                if (LogText != null) LogText.Text += "队列已完成\n";
                if (ClearQueueButton != null) ClearQueueButton.IsEnabled = true;
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

        private void ShowErrorsOnly_IsCheckedChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (QueueList == null) return;
            if (ShowErrorsOnlyCheck?.IsChecked == true)
                ApplyErrorOnlyFilter();
            else
                QueueList.ItemsSource = _queueView;
        }

        private void ApplyErrorOnlyFilter()
        {
            if (QueueList == null) return;
            var filtered = new ObservableCollection<Models.QueueItem>(
                _queueView.Where(item => item.HasError || !item.IsCompleted));
            QueueList.ItemsSource = filtered;
        }

        private void ClearQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_isQueueRunning)
            {
                if (LogText != null) LogText.Text += "请先停止队列再清空。\n";
                return;
            }
            try
            {
                // 1. 清空处理器内部待处理队列（尚未被取走执行的任务）
                _queueProcessor.ClearPending();

                // 2. 从本地 UI 列表中移除所有非"处理中"的项（已完成/已出错/待处理等都清掉）
                int removedCount = 0;
                for (int i = _queueItems.Count - 1; i >= 0; i--)
                {
                    if (_queueItems[i].Status != "处理中")
                    {
                        _queueItems.RemoveAt(i);
                        if (i < _queueView.Count)
                            _queueView.RemoveAt(i);
                        removedCount++;
                    }
                }

                UpdateQueueCountLabel();
                if (LogText != null)
                    LogText.Text += removedCount > 0
                        ? $"已清空队列中 {removedCount} 个任务\n"
                        : "队列中没有可清空的任务。\n";
            }
            catch (Exception ex)
            {
                if (LogText != null) LogText.Text += $"清空队列失败: {ex.Message}\n";
            }
        }

        /// <summary>
        /// 为队列项构建实际将执行的完整命令行（与 QueueProcessor 调度逻辑一致）。
        /// 覆盖所有后端路径：ffmpeg / cjxl / cjpegli / djxl 管道 / avifenc 两步法 等。
        /// </summary>
        private static string BuildQueueItemCommand(QueueItem item)
        {
            var fmt = (item.Options.Format ?? "").ToLowerInvariant();
            var inputExt = Path.GetExtension(item.InputPath).ToLowerInvariant();

            // ── GIF → AVIF（avifenc 两步法）──
            if (inputExt == ".gif" && fmt == "avif")
            {
                return $"[两步法] ffmpeg 提取RGBA帧 → avifenc 编码为动图AVIF";
            }

            // ── AVIF → GIF/WebP（分轨+alphamerge）──
            if (inputExt == ".avif" && (fmt == "gif" || fmt == "webp"))
            {
                return $"[两步法] ffmpeg 分轨提取颜色+alpha → alphamerge 合并编码为 {fmt.ToUpper()}";
            }

            // ── JXL 输入 ──
            if (inputExt == ".jxl")
            {
                return BuildJxlInputCommand(item.InputPath, item.OutputPath, item.Options);
            }

            // ── cjxl 后端 ──
            if (item.Options.EncoderBackend == EncoderBackend.Cjxl && CjxlService.IsAvailable)
            {
                return "cjxl " + CjxlService.BuildCjxlArguments(item.InputPath, item.OutputPath, item.Options);
            }

            // ── Gain Map (Ultra HDR) JPEG：RAW + cjpegli SDR → ultrahdr_app ──
            if (item.Options.JpegGainMap && (fmt == "jpg" || fmt == "jpeg"))
            {
                var q = item.Options.Quality;
                var nits = item.Options.JpegGainMapTargetNits;
                var gmq = item.Options.JpegGainMapQuality;
                var cj = CjpegliService.IsAvailable ? " + cjpegli SDR 优化" : "";
                return $"[两步法] ffmpeg → p010 RAW{cj} → ultrahdr_app -q {q} -L {nits}" +
                    (gmq >= 0 ? $" -Q {gmq}" : "") + $" -z \"{item.OutputPath}\"";
            }

            // ── cjpegli 后端 ──
            if (item.Options.EncoderBackend == EncoderBackend.Cjpegli && CjpegliService.IsAvailable)
            {
                return "cjpegli " + CjpegliService.BuildCjpegliArguments(item.InputPath, item.OutputPath, item.Options);
            }

            // ── 默认 FFmpeg ──
            var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, item.OutputPath);
            return "ffmpeg " + args;
        }

        /// <summary>构建 JXL 输入文件的实际执行命令（与 ProcessJxlInputAsync 一致）</summary>
        private static string BuildJxlInputCommand(string inputPath, string outputPath, FfmpegOptions options)
        {
            var jxlType = JxlInspector.DetectType(inputPath);
            var targetFmt = (options.Format ?? "").ToLowerInvariant();
            var isAnimated = options.AnimationFps.HasValue
                || targetFmt == "gif" || targetFmt == "apng";

            if (jxlType == JxlImageType.JpegReconstruction)
            {
                if (DjxlService.IsAvailable)
                    return $"djxl \"{inputPath}\" \"{outputPath}\"";
                else
                    return "ffmpeg " + FfmpegCommandBuilder.BuildArguments(options, inputPath, outputPath);
            }

            if (jxlType == JxlImageType.NativeCodestream)
            {
                if (isAnimated)
                {
                    var ffmpegOpts = CloneOptionsForFfmpegCommand(options);
                    return "ffmpeg " + FfmpegCommandBuilder.BuildArguments(ffmpegOpts, inputPath, outputPath);
                }

                if (DjxlService.IsAvailable)
                {
                    var usePngIntermediary =
                        (CjpegliService.IsAvailable && (targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli"))
                        || (CjxlService.IsAvailable && targetFmt == "jxl");

                    if (usePngIntermediary)
                    {
                        if ((targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli") && CjpegliService.IsAvailable)
                        {
                            // djxl→cjpegli 管道（优先）或 PNG 中转
                            return $"djxl \"{inputPath}\" --output_format=png - | cjpegli "
                                + CjpegliService.BuildCjpegliArguments("-", outputPath, options);
                        }
                        if (targetFmt == "jxl" && CjxlService.IsAvailable)
                        {
                            // cjxl 需要文件输入 → PNG 中转
                            return $"[两步法] djxl 解码 → 临时PNG → cjxl "
                                + CjxlService.BuildCjxlArguments("<tmp.png>", outputPath, options);
                        }
                    }

                    // 直接管道：djxl → ffmpeg
                    var pipeArgs = FfmpegCommandBuilder.BuildArguments(options, "-", outputPath);
                    var idx = pipeArgs.IndexOf("-i \"-\"", StringComparison.Ordinal);
                    if (idx >= 0)
                        pipeArgs = pipeArgs.Substring(0, idx) + "-f image2pipe " + pipeArgs.Substring(idx);
                    return $"djxl \"{inputPath}\" --output_format=png - | ffmpeg " + pipeArgs;
                }

                // 无 djxl，回退 ffmpeg
                return "ffmpeg " + FfmpegCommandBuilder.BuildArguments(options, inputPath, outputPath);
            }

            // 未知/其他 JXL 类型：ffmpeg 直接处理
            return "ffmpeg " + FfmpegCommandBuilder.BuildArguments(options, inputPath, outputPath);
        }

        /// <summary>为命令预览生成 FFmpeg 编码器回退选项（与 QueueProcessor.CloneOptionsForFfmpeg 一致）</summary>
        private static FfmpegOptions CloneOptionsForFfmpegCommand(FfmpegOptions original)
        {
            var fmt = (original.Format ?? "").ToLowerInvariant();
            string encoder = fmt switch
            {
                "avif" => "libsvtav1",
                "jxl" => "libjxl_anim",
                "jpg" or "jpeg" or "jpegli" => "mjpeg",
                "png" or "apng" => "apng",
                "webp" => "libwebp_anim",
                _ => original.Encoder ?? ""
            };
            return new FfmpegOptions
            {
                Format = original.Format ?? "avif",
                Quality = original.Quality,
                Chroma = original.Chroma,
                BitDepth = original.BitDepth,
                ColorSpace = original.ColorSpace,
                Threads = original.Threads,
                MetadataMode = original.MetadataMode,
                Encoder = encoder,
                EncoderBackend = EncoderBackend.Ffmpeg,
                Lossless = original.Lossless,
                AnimationFps = original.AnimationFps,
                AnimationLoop = original.AnimationLoop,
                GifPaletteOptimize = original.GifPaletteOptimize,
                GifDither = original.GifDither,
                AnimationScaleW = original.AnimationScaleW,
                AvifStillPicture = false,
                AvifCpuUsed = original.AvifCpuUsed,
                AvifRowMt = original.AvifRowMt,
                AvifTune = original.AvifTune,
                AvifPreset = original.AvifPreset,
                JxlEffort = original.JxlEffort,
                JxlModular = original.JxlModular,
                PngPred = original.PngPred,
                WebpPreset = original.WebpPreset,
                WebpCompressionLevel = original.WebpCompressionLevel,
                JpegHuffman = original.JpegHuffman,
                JpegDct = original.JpegDct,
                JpegProgressiveId = original.JpegProgressiveId,
                JpegGainMap = original.JpegGainMap,
                JpegGainMapQuality = original.JpegGainMapQuality,
                JpegGainMapTargetNits = original.JpegGainMapTargetNits,
                JpegGainMapHdrCf = original.JpegGainMapHdrCf,
                JpegGainMapDownsample = original.JpegGainMapDownsample,
                JpegGainMapMultiChannel = original.JpegGainMapMultiChannel,
                TiffCompressionAlgo = original.TiffCompressionAlgo,
            };
        }

        private void QueueList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (QueueList?.SelectedIndex is int idx and >= 0 && idx < _queueItems.Count)
            {
                var item = _queueItems[idx];
                // 如果没有预存命令，则实时生成（兼容旧队列项）
                var command = string.IsNullOrEmpty(item.Command)
                    ? BuildQueueItemCommand(item)
                    : item.Command;
                var win = new ProgressWindow(item, command);
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
            _selectedFileBaseDirs.Clear();
            UpdateMediaFileCount();
        }

        private void ClearFiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _mediaFiles.Clear();
            _selectedFiles.Clear();
            _selectedFileBaseDirs.Clear();
            UpdateMediaFileCount();
        }

        private async void MediaFileList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (MediaFileList?.SelectedItem is string path && !string.IsNullOrWhiteSpace(path))
            {
                await OpenMetadataEditorWindowAsync(path);
            }
        }

        /// <summary>打开元数据编辑窗口（含 ffmpeg 媒体信息 + exiftool 元数据编辑器）</summary>
        private async Task OpenMetadataEditorWindowAsync(string filePath)
        {
            // 获取 ffmpeg 媒体信息
            var mediaInfo = "正在获取媒体信息...";
            try { mediaInfo = await MediaInfoService.GetMediaInfoAsync(filePath); }
            catch (Exception ex) { mediaInfo = $"获取媒体信息失败: {ex.Message}"; }

            var editor = new Controls.MetadataEditor { FilePath = filePath };

            // 媒体信息面板（只读，固定高度可滚动）
            var mediaInfoBox = new TextBox
            {
                Text = mediaInfo,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize = 11,
                Height = 140,
                FontFamily = "Consolas, monospace"
            };

            var infoHeader = new TextBlock
            {
                Text = "📊 FFmpeg 媒体信息",
                FontWeight = Avalonia.Media.FontWeight.Bold,
                FontSize = 13,
                Margin = new Avalonia.Thickness(0, 0, 0, 4)
            };

            var infoSection = new Border
            {
                Margin = new Avalonia.Thickness(0, 0, 0, 10),
                Child = new StackPanel { Children = { infoHeader, mediaInfoBox } }
            };

            var layout = new DockPanel { Margin = new Avalonia.Thickness(12) };
            DockPanel.SetDock(infoSection, Avalonia.Controls.Dock.Top);
            layout.Children.Add(infoSection);
            layout.Children.Add(editor);

            var win = new Window
            {
                Title = $"📝 元数据编辑 — {Path.GetFileName(filePath)}",
                Width = 680, Height = 750,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = layout
            };
            win.Show(this);
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

        private void UseAdvancedColor_IsCheckedChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (AdvancedColorPanel != null) AdvancedColorPanel.IsVisible = UseAdvancedColor?.IsChecked == true;
        }

        private void UseAdvancedCodec_IsCheckedChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (AdvancedCodecPanel != null) AdvancedCodecPanel.IsVisible = UseAdvancedCodec?.IsChecked == true;
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
            RegenerateCommand();
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

            var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            var chroma = ChromaCombo?.SelectedItem as string ?? "4:2:0";
            var bitdepthStr = BitDepthCombo?.SelectedItem as string ?? "auto";
            int? bitdepth = null;
            if (!bitdepthStr.Equals("auto", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(bitdepthStr, out var bd))
                bitdepth = bd;
            var encoderStr = EncoderCombo?.SelectedItem as string ?? "";
            var encoderName = EncoderInfo.ParseEncoderName(encoderStr);
            var encoderBackend = EncoderInfo.ParseBackend(encoderStr);

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
                Encoder = encoderName, EncoderBackend = encoderBackend,
                Threads = threads,
                MetadataMode = GetMetadataMode(),
                Lossless = LosslessCheck?.IsChecked ?? false,
                PngPred = useAdvCodec ? (PngPredCombo?.SelectedItem as string) : null,
                WebpPreset = useAdvCodec ? (WebpPresetCombo?.SelectedItem as string) : null,
                AvifCpuUsed = useAdvCodec ? (int?)AvifCpuUsedBox?.Value : null,
                AvifStillPicture = useAdvCodec ? AvifStillPictureCheck?.IsChecked : null,
                AvifRowMt = useAdvCodec ? AvifRowMtCheck?.IsChecked : null,
                AvifTune = useAdvCodec ? (AvifTuneCombo?.SelectedItem as string) : null,
                AvifPreset = GetAvifPresetValue(),
                AvifSvtPreset = useAdvCodec ? (int?)SvtPresetBox?.Value : null,
                AvifSvtTune = useAdvCodec ? (SvtTuneCombo?.SelectedItem as string) : null,
                AvifHwPreset = useAdvCodec ? (HwPresetCombo?.SelectedItem as string) : null,
                JxlEffort = useAdvCodec ? (int?)JxlEffortBox?.Value : null,
                JxlModular = useAdvCodec ? JxlModularCheck?.IsChecked : null,
                JxlLosslessJpeg = fmt is "jxl" && IsJpegInput(_inputPath),
                JpegHuffman = useAdvCodec ? (JpegHuffmanCombo?.SelectedItem as string) : null,
                JpegDct = useAdvCodec ? (JpegDctCombo?.SelectedItem as string is "auto" ? null : JpegDctCombo?.SelectedItem as string) : null,
                JpegProgressiveId = useAdvCodec ? ParseJpegProgressiveId() : -1,
                JpegGainMap = (GetCurrentEncoderBackend() == EncoderBackend.Ultrahdr),
                JpegGainMapQuality = ParseGainMapQuality(),
                JpegGainMapTargetNits = ParseGainMapNits(),
                JpegGainMapHdrCf = ParseGainMapHdrCf(),
                JpegGainMapDownsample = ParseGainMapDownsample(),
                JpegGainMapMultiChannel = (UseAdvancedCodec?.IsChecked == true)
                    && (JpegGainMapMultiChannelCheck?.IsChecked ?? false),
                TiffCompressionAlgo = useAdvCodec ? (TiffCompressionCombo?.SelectedItem as string) : null,
                StripExifGps = StripExifGpsCheck?.IsChecked ?? true,
                StripExifTime = StripExifTimeCheck?.IsChecked ?? false,
                StripExifCamera = StripExifCameraCheck?.IsChecked ?? false,
                StripExifAll = StripExifAllCheck?.IsChecked ?? false,
                StripXmp = StripXmpCheck?.IsChecked ?? false
            };

            _outputPath = GetOutputPath(_inputPath, options.Format);
            if (CommandText != null)
            {
                var isCjxl = fmt is "jxl" && CjxlService.IsAvailable
                    && (IsJpegInput(_inputPath) || options.EncoderBackend == EncoderBackend.Cjxl);
                if (isCjxl)
                {
                    var effort = options.JxlEffort ?? 7;
                    var t = threads > 0 ? $" --num_threads={threads}" : "";
                    var cmd = $"cjxl \"{_inputPath}\" \"{_outputPath}\" -d 0 -e {effort}{t} --lossless_jpeg=1";
                    CommandText.Text = cmd;
                }
                else
                {
                    var args = FfmpegCommandBuilder.BuildArguments(options, _inputPath, _outputPath);
                    CommandText.Text = "ffmpeg " + args;
                }
            }
        }

        // `StopAfterCurrent_Click` 已移除，使用队列旁的复选框 `StopAfterCurrentCheck` 控制该行为。

        private void ThemeToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var isDark = !App.IsDarkMode();
            App.SetTheme(isDark);
            if (ThemeToggleBtn != null)
                ThemeToggleBtn.Content = isDark ? "☀" : "🌙";
            AppSettingsService.Current.ThemeMode = isDark ? 2 : 1;
            AppSettingsService.Save();
            // 主题切换时刷新队列列表，确保 Foreground 绑定重新计算
            RefreshQueueListBinding();
        }

        /// <summary>刷新队列列表 ItemsSource 绑定，触发 DataTemplate 重新应用前景色</summary>
        private void RefreshQueueListBinding()
        {
            if (QueueList == null) return;
            var items = QueueList.ItemsSource;
            QueueList.ItemsSource = null;
            QueueList.ItemsSource = items;
        }

        private async void FormatFilter_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var result = await FormatFilterWindow.ShowFilterDialog(this);
            if (result != null)
            {
                // 格式筛选变更后，刷新当前已选文件的过滤显示
                RefreshMediaFilesFilter();
            }
        }

        /// <summary>根据当前启用的格式，重新过滤已选文件列表</summary>
        private void RefreshMediaFilesFilter()
        {
            var enabledExts = new HashSet<string>(AppSettingsService.Current.GetEnabledExtensions());
            var toRemove = _mediaFiles
                .Where(f => !enabledExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
            foreach (var f in toRemove)
                _mediaFiles.Remove(f);
            toRemove = _selectedFiles
                .Where(f => !enabledExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
            foreach (var f in toRemove)
            {
                _selectedFiles.Remove(f);
                _selectedFileBaseDirs.Remove(f);
            }
            UpdateMediaFileCount();
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

        private async void BrowseCjxl_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            // 改为选择文件夹：用户选择包含 cjxl/djxl/cjpegli 等工具的目录（例如 D:\...\bin）
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择包含 JPEG/JXL 工具的目录",
                AllowMultiple = false
            });

            if (folders != null && folders.Count > 0)
            {
                var dir = folders[0].Path.LocalPath;
                AppSettingsService.Current.CjxlPath = dir; // JPEG XL 参考实现库目录（含 cjxl/djxl/cjpegli）
                AppSettingsService.Save();
                if (CjxlPathBox != null) CjxlPathBox.Text = dir;

                // 刷新所有 JPEG XL 工具的检测
                CjxlService.ClearCache();
                UltrahdrService.ClearCache();
                JxrService.ClearCache();
                CjxlService.Detect();
                DjxlService.ClearCache();
                DjxlService.Detect();
                CjpegliService.ClearCache();
                CjpegliService.Detect();
                UltrahdrService.Detect();

                // 扫描目录中的其他相关工具（例如 djxl / cjpegli / 相关 DLL）并在日志中展示
                var scan = ExternalToolsDetector.ScanDirectory(dir);
                if (LogText != null)
                {
                    if (!string.IsNullOrEmpty(scan.CjxlExe))
                        LogText.Text += $"✅ 在目录找到 cjxl: {scan.CjxlExe}\n";
                    else if (CjxlService.IsAvailable)
                        LogText.Text += $"✅ 检测到 cjxl（PATH/同目录）: {CjxlService.DetectedPath}\n";
                    else
                        LogText.Text += $"⚠️ 未在所选目录找到 cjxl.exe（将回退到自动检测）\n";

                    if (!string.IsNullOrEmpty(scan.DjxlExe))
                        LogText.Text += $"✅ 在目录找到 djxl: {scan.DjxlExe}\n";
                    if (!string.IsNullOrEmpty(scan.CjpegliExe))
                        LogText.Text += $"✅ 在目录找到 cjpegli: {scan.CjpegliExe}\n";
                    if (scan.OtherExecutables.Count > 0)
                        LogText.Text += $"ℹ️ 其他可执行文件: {scan.OtherExecutables.Count} 个（可能包含 ffmpeg 附带工具）\n";
                    if (scan.FoundDlls.Count > 0)
                        LogText.Text += $"ℹ️ 发现相关 DLL: {scan.FoundDlls.Count} 个（注意运行时依赖）\n";
                }

                RegenerateCommand();
            }
        }

        private async void BrowseExifTool_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 exiftool",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件") { Patterns = new[] { "*.exe" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });
            if (files != null && files.Count > 0)
            {
                var path = files[0].Path.LocalPath;
                AppSettingsService.Current.ExifToolPath = path;
                AppSettingsService.Save();
                if (ExifToolPathBox != null) ExifToolPathBox.Text = path;
                ExifToolService.Detect();
                UpdateExifToolPanelState();
                if (LogText != null)
                    LogText.Text += ExifToolService.IsAvailable
                        ? $"✅ exiftool 路径已更新: {path}\n"
                        : $"⚠️ exiftool 路径已设置但无法使用: {path}\n";
                RegenerateCommand();
            }
        }

        private void ClearCjxlPath_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            AppSettingsService.Current.CjxlPath = null;
            AppSettingsService.Current.CjpegliPath = null;
            AppSettingsService.Save();
            if (CjxlPathBox != null) CjxlPathBox.Text = "";
            CjxlService.ClearCache();
            CjxlService.Detect();
            DjxlService.ClearCache();
            DjxlService.Detect();
            CjpegliService.ClearCache();
            CjpegliService.Detect();
            UltrahdrService.ClearCache();
            UltrahdrService.Detect();
            if (LogText != null)
            {
                LogText.Text += CjxlService.IsAvailable
                    ? $"✅ cjxl 自动检测: {CjxlService.DetectedPath}\n"
                    : "ℹ️ cjxl: 未检测到\n";
                LogText.Text += DjxlService.IsAvailable
                    ? $"✅ djxl 自动检测: {DjxlService.DetectedPath}\n"
                    : "ℹ️ djxl: 未检测到\n";
                LogText.Text += CjpegliService.IsAvailable
                    ? $"✅ cjpegli 自动检测: {CjpegliService.DetectedPath}\n"
                    : "ℹ️ cjpegli: 未检测到\n";
            }
            RegenerateCommand();
        }

        private void ClearExifToolPath_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            AppSettingsService.Current.ExifToolPath = null;
            AppSettingsService.Save();
            if (ExifToolPathBox != null) ExifToolPathBox.Text = "";
            ExifToolService.Detect();
            UpdateExifToolPanelState();
            if (LogText != null)
                LogText.Text += ExifToolService.IsAvailable
                    ? $"✅ exiftool 自动检测: {ExifToolService.DetectedPath}\n"
                    : "ℹ️ exiftool: 未检测到\n";
            RegenerateCommand();
        }

        private async void BrowseAvifenc_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 avifenc.exe",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件") { Patterns = new[] { "*.exe" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });
            if (files != null && files.Count > 0)
            {
                var path = files[0].Path.LocalPath;
                AppSettingsService.Current.AvifencPath = path;
                AppSettingsService.Save();
                if (AvifencPathBox != null) AvifencPathBox.Text = path;
                if (LogText != null) LogText.Text += $"avifenc 路径已更新: {path}\n";
                RegenerateCommand();
            }
        }

        private void ClearAvifencPath_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            AppSettingsService.Current.AvifencPath = null;
            AppSettingsService.Save();
            if (AvifencPathBox != null) AvifencPathBox.Text = "";
            if (LogText != null) LogText.Text += "avifenc: 已切换为自动检测（ffmpeg 同目录）\n";
            RegenerateCommand();
        }

        private async void BrowseUltrahdr_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 ultrahdr_app.exe",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件") { Patterns = new[] { "*.exe" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });
            if (files != null && files.Count > 0)
            {
                var path = files[0].Path.LocalPath;
                AppSettingsService.Current.UltrahdrPath = path;
                AppSettingsService.Save();
                if (UltrahdrPathBox != null) UltrahdrPathBox.Text = path;
                if (LogText != null) LogText.Text += $"ultrahdr 路径已更新: {path}\n";
                UltrahdrService.ClearCache();
                UltrahdrService.Detect();
                RegenerateCommand();
            }
        }

        private void ClearUltrahdrPath_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            AppSettingsService.Current.UltrahdrPath = null;
            AppSettingsService.Save();
            if (UltrahdrPathBox != null) UltrahdrPathBox.Text = "";
            if (LogText != null) LogText.Text += "ultrahdr: 已切换为自动检测\n";
            UltrahdrService.ClearCache();
            UltrahdrService.Detect();
            RegenerateCommand();
        }

        private void ToggleToolsPanel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (ToolsDetailPanel != null)
                ToolsDetailPanel.IsVisible = !ToolsDetailPanel.IsVisible;
            RefreshToolsStatusBar();
        }

        private async void BrowseJxr_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 JxrEncApp.exe",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件") { Patterns = new[] { "*.exe" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });
            if (files != null && files.Count > 0)
            {
                var path = files[0].Path.LocalPath;
                AppSettingsService.Current.JxrPath = path;
                AppSettingsService.Save();
                if (JxrPathBox != null) JxrPathBox.Text = path;
                if (LogText != null) LogText.Text += $"JxrEncApp 路径已更新: {path}\n";
                JxrService.ClearCache();
                JxrService.Detect();
                RegenerateCommand();
            }
        }

        private void ClearJxrPath_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            AppSettingsService.Current.JxrPath = null;
            AppSettingsService.Save();
            if (JxrPathBox != null) JxrPathBox.Text = "";
            if (LogText != null) LogText.Text += "JxrEncApp: 已切换为自动检测\n";
            JxrService.ClearCache();
            JxrService.Detect();
            RegenerateCommand();
        }

        /// <summary>刷新顶部工具状态指示器（折叠态）</summary>
        private void RefreshToolsStatusBar()
        {
            if (ToolsStatusBar == null) return;
            ToolsStatusBar.Children.Clear();
            AddToolStatus(CjxlService.IsAvailable, "cjxl");
            AddToolStatus(ExifToolService.IsAvailable, "exiftool");
            AddToolStatus(CjpegliService.IsAvailable, "cjpegli");
            AddToolStatus(HasAvifencAvailable(), "avifenc");
            AddToolStatus(UltrahdrService.IsAvailable, "ultrahdr");
            AddToolStatus(JxrService.IsAvailable, "JxrEnc");
        }

        private void AddToolStatus(bool available, string name)
        {
            if (ToolsStatusBar == null) return;
            var tb = new TextBlock
            {
                Text = $"{(available ? "✅" : "❌")} {name}",
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            ToolTip.SetTip(tb, available ? $"{name} 已检测到" : $"{name} 未检测到");
            ToolsStatusBar.Children.Add(tb);
        }

        private static bool HasAvifencAvailable()
        {
            var manual = AppSettingsService.Current.AvifencPath;
            if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual)) return true;
            var dir = AppSettingsService.Current.FfmpegDir;
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "avifenc.exe"));
        }

        private void RedetectTools_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // 清除所有手动路径，改为自动同目录 + PATH 检测
            AppSettingsService.Current.CjxlPath = null;
            AppSettingsService.Current.ExifToolPath = null;
            AppSettingsService.Current.CjpegliPath = null;
            AppSettingsService.Current.AvifencPath = null;
            AppSettingsService.Current.UltrahdrPath = null;
            AppSettingsService.Save();
            if (CjxlPathBox != null) CjxlPathBox.Text = "";
            if (ExifToolPathBox != null) ExifToolPathBox.Text = "";
            if (AvifencPathBox != null) AvifencPathBox.Text = "";
            if (UltrahdrPathBox != null) UltrahdrPathBox.Text = "";

            if (LogText != null) LogText.Text += "正在自动重新检测外部工具（同目录 → PATH）...\n";
            CjxlService.ClearCache();
            CjpegliService.ClearCache();
            UltrahdrService.ClearCache();
            JxrService.ClearCache();
            DjxlService.ClearCache();
            CjxlService.Detect();
            CjpegliService.Detect();
            UltrahdrService.Detect();
            DjxlService.Detect();
            DjxlService.Detect();
            ExifToolService.Detect();
            UpdateExifToolPanelState();

            if (LogText != null)
            {
                LogText.Text += CjxlService.IsAvailable
                    ? $"✅ cjxl: {CjxlService.DetectedPath}\n"
                    : "ℹ️ cjxl: 未检测到\n";
                LogText.Text += DjxlService.IsAvailable
                    ? $"✅ djxl: {DjxlService.DetectedPath}\n"
                    : "ℹ️ djxl: 未检测到\n";
                LogText.Text += CjpegliService.IsAvailable
                    ? $"✅ cjpegli: {CjpegliService.DetectedPath}\n"
                    : "ℹ️ cjpegli: 未检测到\n";
                LogText.Text += ExifToolService.IsAvailable
                    ? $"✅ exiftool: {ExifToolService.DetectedPath}\n"
                    : "ℹ️ exiftool: 未检测到\n";
            }
            RefreshToolsStatusBar();
            RegenerateCommand();
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
            // 记录当前输入基目录，后续用于保留子目录结构
            _inputBaseDir = dir;
            var supported = AppSettingsService.Current.GetEnabledExtensions();

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
                _selectedFileBaseDirs.Clear();
                foreach (var f in files)
                {
                    _selectedFiles.Add(f);
                    _selectedFileBaseDirs[f] = dir;
                }
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

        private string GetOutputPath(string inputPath, string format, string? overrideInputBaseDir = null)
        {
            // jpegli 生成标准 .jpg，apng 生成 .png
            var ext = format switch
            {
                "jpegli" => "jpg",
                "apng" => "png",
                _ => format
            };
            var outDir = AppSettingsService.Current.OutputDirectory;
            var fileName = Path.GetFileNameWithoutExtension(inputPath) + "." + ext;

            string resultPath;
            if (string.IsNullOrWhiteSpace(outDir))
            {
                resultPath = Path.ChangeExtension(inputPath, ext);
            }
            else
            {
                // 当用户选择保留输入文件夹结构时，且存在输入基目录，则在输出目录下重建相对路径
                var baseDirToUse = overrideInputBaseDir ?? _inputBaseDir;
                if (AppSettingsService.Current.PreserveInputFolderStructure && !string.IsNullOrWhiteSpace(baseDirToUse))
                {
                    try
                    {
                        var rel = Path.GetRelativePath(baseDirToUse!, inputPath);
                        // 如果不是基于 base 的子路径（例如不同盘符），Path.GetRelativePath 会以 ".." 开头
                        if (rel.StartsWith(".."))
                        {
                            resultPath = Path.Combine(outDir, fileName);
                        }
                        else
                        {
                                        var relDir = Path.GetDirectoryName(rel);
                                        // 尝试保留最外层目录名
                                        var baseTrim = baseDirToUse!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                        var baseFolderName = string.IsNullOrEmpty(baseTrim) ? null : Path.GetFileName(baseTrim);
                                        if (!string.IsNullOrEmpty(relDir))
                                        {
                                            string destDir;
                                            if (!string.IsNullOrEmpty(baseFolderName))
                                                destDir = Path.Combine(outDir, baseFolderName, relDir);
                                            else
                                                destDir = Path.Combine(outDir, relDir);
                                            resultPath = Path.Combine(destDir, fileName);
                                        }
                                        else
                                        {
                                            if (!string.IsNullOrEmpty(baseFolderName))
                                                resultPath = Path.Combine(outDir, baseFolderName, fileName);
                                            else
                                                resultPath = Path.Combine(outDir, fileName);
                                        }
                        }
                    }
                    catch
                    {
                        resultPath = Path.Combine(outDir, fileName);
                    }
                }
                else
                {
                    resultPath = Path.Combine(outDir, fileName);
                }
            }

            try
            {
                return Path.GetFullPath(resultPath);
            }
            catch
            {
                return resultPath;
            }
        }

        /// <summary>
        /// 判断输入文件是否为 JPEG 格式（用于 JPEG→JXL 快速路径检测）
        /// </summary>
        private static bool IsJpegInput(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif";
        }

        /// <summary>
        /// 解析增益图质量：勾选"跟随主图"返回 -1，否则从 TextBox 读取 1-100 的值
        /// </summary>
        private int ParseGainMapQuality()
        {
            if (JpegGainMapFollowMainCheck?.IsChecked == true)
                return -1;
            if (int.TryParse(JpegGainMapQualityBox?.Text, out var q))
                return Math.Clamp(q, 1, 100);
            return -1; // 默认跟随主图
        }

        /// <summary>
        /// 解析目标亮度 (nit)：从 TextBox 读取 200-10000 的值
        /// </summary>
        private int ParseGainMapNits()
        {
            if (int.TryParse(JpegGainMapNitsBox?.Text, out var n))
                return Math.Clamp(n, 200, 10000);
            return 1000;
        }

        /// <summary>解析 HDR 色彩格式：当前仅支持 0=p010</summary>
        private int ParseGainMapHdrCf()
        {
            // 仅 p010 (ffmpeg rawvideo 不支持 rgba1010102 / rgbahalffloat 的 packed 布局)
            return 0;
        }

        /// <summary>解析增益图下采样因子。默认半分辨率(2)，仅在勾选"高级编码选项"时使用手动设置。</summary>
        private int ParseGainMapDownsample()
        {
            if (UseAdvancedCodec?.IsChecked != true)
                return 2;  // 默认半分辨率
            return JpegGainMapDownsampleCombo?.SelectedIndex switch
            {
                0 => 1,   // 满分辨率
                2 => 4,   // 1/4
                3 => 8,   // 1/8
                _ => 2    // 1/2 (默认)
            };
        }

        /// <summary>解析 JPEG 渐进模式 (Gain Map / mjpeg 路径): -1=自动, 0=基线, 1=渐进</summary>
        private int ParseJpegProgressiveId()
        {
            if (UseAdvancedCodec?.IsChecked != true)
                return -1;  // 未展开高级选项时自动
            return JpegProgressiveCombo?.SelectedIndex switch
            {
                0 => -1,  // 自动
                1 => 0,   // 基线/标准
                2 => 1,   // 渐进式
                _ => 0
            };
        }

        /// <summary>增益图质量 +/- 按钮调整</summary>
        private void AdjustGainMapQuality(int delta)
        {
            var current = ParseGainMapQuality();
            if (current < 0) current = 75; // 从"跟随主图"切换到手动时，默认 75
            var val = Math.Clamp(current + delta, 1, 100);
            if (JpegGainMapQualityBox != null)
                JpegGainMapQualityBox.Text = val.ToString();
        }

        /// <summary>Nits Slider 变更时同步到 TextBox</summary>
        private void SyncNitsToSlider()
        {
            if (JpegGainMapNitsSlider == null || JpegGainMapNitsBox == null) return;
            if (int.TryParse(JpegGainMapNitsBox.Text, out var n))
                JpegGainMapNitsSlider.Value = Math.Clamp(n, 200, 10000);
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
                MetadataMode = MetadataModeCombo?.SelectedIndex == 1 ? "StripAll" : "PreserveAll",
                Lossless = LosslessCheck?.IsChecked ?? false,
                UseAdvancedCodec = UseAdvancedCodec?.IsChecked ?? false,
                PngPred = PngPredCombo?.SelectedItem as string,
                WebpPreset = WebpPresetCombo?.SelectedItem as string,
                AvifCpuUsed = (int?)AvifCpuUsedBox?.Value,
                AvifTune = AvifTuneCombo?.SelectedItem as string,
                AvifPreset = GetAvifPresetValue(),
                AvifStillPicture = AvifStillPictureCheck?.IsChecked,
                JxlEffort = (int?)JxlEffortBox?.Value,
                JxlModular = JxlModularCheck?.IsChecked,
                JpegHuffman = JpegHuffmanCombo?.SelectedItem as string,
                JpegDct = JpegDctCombo?.SelectedItem as string,
                JpegProgressiveId = ParseJpegProgressiveId(),
                TiffCompressionAlgo = TiffCompressionCombo?.SelectedItem as string,
                StripExifGps = StripExifGpsCheck?.IsChecked ?? true,
                StripExifTime = StripExifTimeCheck?.IsChecked ?? false,
                StripExifCamera = StripExifCameraCheck?.IsChecked ?? false,
                StripExifAll = StripExifAllCheck?.IsChecked ?? false,
                StripXmp = StripXmpCheck?.IsChecked ?? false,
                Concurrency = GetConcurrencyValue(),
                MaxQueueSize = GetConcurrencyValue()
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
            if (MetadataModeCombo != null)
            {
                if (p.MetadataMode == "StripAll")
                    MetadataModeCombo.SelectedIndex = 1;
                else
                    MetadataModeCombo.SelectedIndex = 0;
            }
            // ExifTool 选项
            if (StripExifGpsCheck != null) StripExifGpsCheck.IsChecked = p.StripExifGps;
            if (StripExifTimeCheck != null) StripExifTimeCheck.IsChecked = p.StripExifTime;
            if (StripExifCameraCheck != null) StripExifCameraCheck.IsChecked = p.StripExifCamera;
            if (StripExifAllCheck != null) StripExifAllCheck.IsChecked = p.StripExifAll;
            if (StripXmpCheck != null) StripXmpCheck.IsChecked = p.StripXmp;
            if (LosslessCheck != null) LosslessCheck.IsChecked = p.Lossless;
            if (UseAdvancedCodec != null) UseAdvancedCodec.IsChecked = p.UseAdvancedCodec;
            SetComboByValue(PngPredCombo, p.PngPred);
            SetComboByValue(WebpPresetCombo, p.WebpPreset);
            if (AvifCpuUsedBox != null && p.AvifCpuUsed.HasValue) AvifCpuUsedBox.Value = p.AvifCpuUsed.Value;
            SetComboByValue(AvifTuneCombo, p.AvifTune);
            // AvifPreset 由编码器上下文动态决定（SVT: SvtPresetBox, libaom: 不可用）
            if (AvifStillPictureCheck != null && p.AvifStillPicture.HasValue) AvifStillPictureCheck.IsChecked = p.AvifStillPicture.Value;
            if (JxlEffortBox != null && p.JxlEffort.HasValue) JxlEffortBox.Value = p.JxlEffort.Value;
            if (JxlModularCheck != null && p.JxlModular.HasValue) JxlModularCheck.IsChecked = p.JxlModular.Value;
            SetComboByValue(JpegHuffmanCombo, p.JpegHuffman);
            if (JpegProgressiveCombo != null && p.JpegProgressiveId is >= -1 and <= 1)
                JpegProgressiveCombo.SelectedIndex = p.JpegProgressiveId + 1;  // -1→0(自动), 0→1(基线), 1→2(渐进)
            SetComboByValue(TiffCompressionCombo, p.TiffCompressionAlgo);
            if (ConcurrencyBox != null) ConcurrencyBox.Text = Math.Clamp(p.MaxQueueSize, 1, 128).ToString();
            UpdateConcurrencyLabel();
            UpdateOptionAvailability();
            UpdateExifToolPanelState();
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
            if (e.DataTransfer.Contains(DataFormat.File))
            {
                if (MediaDropZone != null) MediaDropZone.BorderBrush = Avalonia.Media.Brushes.DodgerBlue;
                if (MediaInfoText != null) MediaInfoText.Text = "释放以载入文件/文件夹...";
                e.DragEffects = DragDropEffects.Copy;
            }
        }

        private void DragOver(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Contains(DataFormat.File))
            {
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

            var files = new List<string>();

            // 使用 IStorageItem 接口获取拖放文件
            if (e.DataTransfer.Contains(DataFormat.File))
            {
                var items = e.DataTransfer.TryGetFiles();
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        var localPath = item.TryGetLocalPath() ?? item.Path.LocalPath;
                        if (!string.IsNullOrWhiteSpace(localPath))
                            files.Add(localPath);
                    }
                }
            }

            if (files.Count == 0) return;

            // 单文件 → 作为输入文件
            if (files.Count == 1)
            {
                var path = files[0];
                if (System.IO.Directory.Exists(path))
                {
                    await ScanFolderAsync(path);
                }
                else
                {
                    _inputPath = path;
                    var supported = GetEnabledExtensionsForCurrentMode();
                    if (supported.Contains(System.IO.Path.GetExtension(path).ToLower()))
                    {
                        // 追加到已选文件列表（不清空已有文件，保持目录结构映射）
                        if (!_mediaFiles.Contains(path))
                            _mediaFiles.Add(path);
                        if (!_selectedFiles.Contains(path))
                            _selectedFiles.Add(path);
                        var parentDir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(parentDir) && !_selectedFileBaseDirs.ContainsKey(path))
                            _selectedFileBaseDirs[path] = parentDir;
                        UpdateMediaFileCount();
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
            // 多文件或文件夹 → 批量追加（不清空已有文件）
            else
            {
                var supported = GetEnabledExtensionsForCurrentMode();
                foreach (var path in files)
                {
                    if (System.IO.Directory.Exists(path))
                    {
                        try
                        {
                            var dirFiles = System.IO.Directory.EnumerateFiles(path, "*.*", System.IO.SearchOption.AllDirectories)
                                .Where(f => supported.Contains(System.IO.Path.GetExtension(f).ToLower()));
                            foreach (var f in dirFiles)
                            {
                                if (!_selectedFiles.Contains(f))
                                    _selectedFiles.Add(f);
                                // 将此文件映射到当前被拖入的文件夹作为基目录（最新拖放优先）
                                _selectedFileBaseDirs[f] = path;
                            }
                        }
                        catch { }
                    }
                    else if (supported.Contains(System.IO.Path.GetExtension(path).ToLower()))
                    {
                        if (!_selectedFiles.Contains(path))
                            _selectedFiles.Add(path);
                        var parentDir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(parentDir) && !_selectedFileBaseDirs.ContainsKey(path))
                            _selectedFileBaseDirs[path] = parentDir;
                    }
                }

                if (_selectedFiles.Count > 0)
                {
                    AddToMediaFiles(_selectedFiles);
                    if (LogText != null) LogText.Text += $"已拖放 {_selectedFiles.Count} 个文件\n";
                    // 注意：不清空 _selectedFiles，以便后续"添加到队列"可批量导入
                    UpdateMediaFileCount();
                }
            }
        }

        private async Task ScanFolderAsync(string dir)
        {
            var supported = GetEnabledExtensionsForCurrentMode();
            try
            {
                // 记录基目录以便后续在输出目录中保留相对结构
                _inputBaseDir = dir;
                var files = System.IO.Directory.EnumerateFiles(dir, "*.*", System.IO.SearchOption.AllDirectories)
                    .Where(f => supported.Contains(System.IO.Path.GetExtension(f).ToLower()))
                    .ToList();

                if (files.Count == 0)
                {
                    if (LogText != null) LogText.Text += $"文件夹中未找到支持的图片文件: {dir}\n";
                    return;
                }

                _selectedFiles.Clear();
                _selectedFileBaseDirs.Clear();
                foreach (var f in files)
                {
                    _selectedFiles.Add(f);
                    _selectedFileBaseDirs[f] = dir;
                }
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