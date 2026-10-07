using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FfmpegGui.Models;
using FfmpegGui.Services;

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
        private ComboBox? DngCompressionCombo;
        private StackPanel? DngJxlQualityPanel;
        private Slider? DngJxlQualitySlider;
        private TextBox? DngJxlQualityBox;
        private StackPanel? DngJxlAdvancedPanel;
        private NumericUpDown? DngJxlEffortBox;
        private NumericUpDown? DngJxlDecodeSpeedBox;
        private ComboBox? DngLinearCombo;
        /// <summary>
        /// ⚠ 2026-09-21：**「线性 DNG」适用条件提示** —— 仅在选中「线性」时显示。
        /// 实测 dngtool 对 Bayer 输入会明确拒绝（`-linear 仅适用于线性/去马赛克输入`），
        /// 而 UI 原先把「线性」与「保留 CFA」并列为可选且**无任何提示** ⇒ 「UI 与真实不符」。
        /// </summary>
        private TextBlock? DngLinearHint;
        private ComboBox? DngBitDepthCombo;
        private ComboBox? DngHighlightCombo;
        private TextBlock? RawEngineStatus;
        private StackPanel? RawPanel;
        /// <summary>
        /// ⚠ 2026-09-20：**DNG 输出专属选项面板**（压缩/JXL 质量/布局/位深/高光）。
        /// 可见性 = 「RAW 模式 **且** 输出格式 = dng」—— 原先这些选项直接在 <c>RawPanel</c> 里，
        /// 选「RAW → jpg/avif」时仍显示 ⇒ 「UI 与真实不符」。
        /// </summary>
        private StackPanel? DngOptionsPanel;
        private StackPanel? StillOptionsPanel;
        private ComboBox? EncoderCombo;
        private Slider? QualitySlider;
        private TextBox? QualityBox;
        private TextBlock? QualityValue;
        private ComboBox? ChromaCombo;
        private ComboBox? ColorSpaceCombo;
        private ComboBox? ColorPrimariesCombo;
        private ComboBox? ColorTrcCombo;
        private ComboBox? ColorMatrixCombo;
        private ComboBox? ColorRangeCombo;
        private ComboBox? TonemapCurveCombo;
        private TextBlock? ColorRangeLabel;
        private TextBlock? ColorConflictLabel;
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
        private CheckBox? AppendPngExtCheck;
        // 图片最长边限制
        private CheckBox? EnableMaxDimensionCheck;
        private StackPanel? MaxDimensionPanel;
        private NumericUpDown? MaxDimensionBox;
        private TextBlock? MaxDimensionHint;
        private ListBox? QueueList;
        private TextBox? ConcurrencyBox;
        private TextBox? CommandText;
        private TextBox? MediaInfoText;
        private TextBox? LogText;
        private TextBox? FfmpegPathBox;
        private TextBox? OutputDirBox;
        private TextBox? JxlLibDirBox;
        private TextBox? ExifToolPathBox;
        private TextBox? ArtifactsDirBox;
        private TextBox? CacheDirBox;
        private Button? CacheToggleBtn;
        private StackPanel? CachePanel;
        private TextBlock? JxlLibStatus;      // (保留兼容，已改用 StackPanel)
        private TextBlock? ArtifactsStatus;    // (保留兼容，已改用 StackPanel)
        // ── 外部工具详细状态面板（3 列水平布局）──
        private StackPanel? JxlToolsStatus;
        private StackPanel? ExifToolToolsStatus;
        private StackPanel? ArtifactsToolsStatus;
        // ── Photoshop ACR 验证 (2026-08-15) ──
        private TextBox? PhotoshopPathBox;
        private StackPanel? PsToolsStatus;
        private CheckBox? PreserveInputStructure;
        private CheckBox? StopAfterCurrentCheck;
        private CheckBox? ShowErrorsOnlyCheck;
        private TextBlock? QueueProgressLabel;
        private TextBlock? QueueEtaLabel;
        private TextBlock? ElapsedLabel;
        private Button? ClearQueueButton;
        private CheckBox? AutoThreadsCheck;
        private bool _isQueueRunning = false;
        private bool _allowFullExit = false;      // 允许完全退出（托盘菜单 Exit 时置 true）
        private bool _updatingQuality = false;  // 防止滑块/数字框双向同步时递归
        private bool _suppressCommandRegen = false; // 批量更新选项时抑制重复 RegenerateCommand 调用
        private CheckBox? SingleThreadCheck;
        private TextBlock? ThreadHintLabel;
        private TextBlock? ConcurrencyLabel;
        private TextBlock? QueueCountLabel;
        private Button? ConcurrencyUpBtn;
        private Button? ConcurrencyDownBtn;
        private Button? ThemeToggleBtn;
        private Button? GpuToggleBtn;
        private Button? SimpleModeEntryBtn;
        // 简洁模式控件
        private CheckBox? SimpleErrorsOnlyCheck;
        private DockPanel? FullModePanel;
        private Grid? SimpleModePanel;
        private ListBox? SimpleQueueList;
        private ListBox? SimpleMediaList;
        private Border? SimpleDropZone;
        private TextBlock? SimpleFileCount;
        private TextBlock? SimpleQueueCount;
        private ToggleSwitch? AutoEncodeToggle;
        private ComboBox? SimplePresetCombo;
        private TextBlock? SimpleStatusLabel;
        private TextBlock? SimpleProgressLabel;
        private TextBlock? SimpleEtaLabel;
        private TextBlock? SimpleElapsedLabel;
        private TextBlock? SimpleCurrentStateLabel;
        // 应用预设那一刻抓的核心轴指纹，用于判断"之后的状态还等不等于那次预设"
        private string _simpleAppliedSig = "";
        private bool _simpleModeActive;
        private bool _simpleDropHandlersAttached; // 防止重复进入简洁模式时拖放 handler 重复注册
        private PresetEntry? _simpleActivePreset;
        private System.Timers.Timer? _autoEncodeTimer;
        // GPU 编码器警告面板
        private Border? GpuEncoderWarning;
        private TextBlock? GpuEncoderWarningText;
        private TextBlock? GpuEncoderWarningHint;
        // 高级编码选项被忽略/降级提示面板（原先只走 Console，GUI 模式下无控制台 ⇒ 不可见）
        private Border? EncoderOptionWarning;
        private TextBlock? EncoderOptionWarningText;
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
        private ComboBox? PngPredCombo;
        private NumericUpDown? PngDpiBox;
        private ComboBox? WebpPresetCombo;
        private NumericUpDown? WebpCompressionBox;
        private StackPanel? WebpLosslessPanel;
        private NumericUpDown? AvifCpuUsedBox;
        private CheckBox? AvifStillPictureCheck;
        private CheckBox? AvifRowMtCheck;
        private CheckBox? AutoUseSimdCheck;
        private ComboBox? AvifTuneCombo;
        // ── libaom-av1 高级图像控件 ──
        private ComboBox? AvifAqModeCombo;
        private CheckBox? AvifCdefCheck;
        private CheckBox? AvifIntrabcCheck;
        private NumericUpDown? AvifDenoiseBox;
        // ── 各编码器专用面板 ──
        private StackPanel? LibaomAvifPanel;
        private StackPanel? SvtAvifPanel;
        private StackPanel? HwAvifPanel;
        private StackPanel? NvencAvifPanel;
        private StackPanel? QsvAvifPanel;
        private StackPanel? VaapiAvifPanel;
        private StackPanel? AmfAvifPanel;
        private NumericUpDown? SvtPresetBox;
        private ComboBox? SvtTuneCombo;
        // ── NVENC 专用控件 ──
        private ComboBox? NvencPresetCombo;
        private NumericUpDown? AvifNvencAqBox;
        private CheckBox? AvifNvencSpatialAqCheck;
        // ── QSV 专用控件 ──
        private ComboBox? QsvPresetCombo;
        private CheckBox? AvifQsvLowPowerCheck;
        // ── VAAPI 专用控件 ──
        private ComboBox? VaapiPresetCombo;
        private CheckBox? AvifVaapiLowPowerCheck;
        // ── AMF 专用控件 ──
        private ComboBox? AmfPresetCombo;
        // ── 旧硬件面板(保留兼容) ──
        private ComboBox? HwPresetCombo;
        private ComboBox? PriorityCombo;
        private NumericUpDown? JxlEffortBox;
        private NumericUpDown? CjxlEffortBox;
        private CheckBox? JxlModularCheck;
        private CheckBox? CjxlProgressiveCheck;
        private CheckBox? JxlLosslessJpegCheck;
        private StackPanel? JxlLosslessJpegPanel;
        private NumericUpDown? CjxlPhotonNoiseBox;
        private CheckBox? CjxlAutoPhotonNoiseCheck;
        private CheckBox? JxlPreserveUltrahdrCheck;
        private ComboBox? JpegHuffmanCombo;
        private ComboBox? JpegDctCombo;
        private ComboBox? JpegProgressiveCombo;
        // ── Gain Map (Ultra HDR) JPEG 控件 ──
        private Border? JpegGainMapPanel;
        private StackPanel? JpegGainMapOptions;
        private CheckBox? JpegGainMapFollowMainCheck;
        private ComboBox? HdrModeCombo;   // HDR 实现模式（GainMap | 传统）；可见性按容器能力
        private CheckBox? ColorGamutMapCheck;       // 色域映射（GMO）开关（高级色彩面板，默认关）
        private ComboBox? RawColorTargetCombo;      // RAW 输出色域模式（rec2020 | auto | rec709）
        private StackPanel? JpegGainMapQualityPanel;
        private TextBox? JpegGainMapQualityBox;
        private Button? GainMapQualityUpBtn;
        private Button? GainMapQualityDownBtn;
        private Slider? JpegGainMapNitsSlider;
        private TextBox? JpegGainMapNitsBox;
        private ComboBox? JpegGainMapHdrCfCombo;
        private ComboBox? JpegGainMapDownsampleCombo;
        private ComboBox? JpegGainMapTypeCombo;
        private ComboBox? JpegGainMapBaseCombo;
        private CheckBox? JpegGainMapMultiChannelCheck;  // 兼容旧代码引用
        private ComboBox? TiffCompressionCombo;
        private NumericUpDown? TiffDpiBox;
        // -- thumbnail (EXIF IFD1 embed) advanced codec panel controls --
        private StackPanel? ThumbnailPanel;
        private CheckBox? EnableThumbnailCheck;
        private NumericUpDown? ThumbnailSizeBox;
        private NumericUpDown? ThumbnailQualityBox;
        // ── cjpegli / jpegli 高级面板控件 ──
        private StackPanel? JpegliCodecPanel;
        private ComboBox? JpegliChromaCombo;
        private ComboBox? JpegliProgressiveCombo;
        private CheckBox? JpegliOptimizeCheck;
        private CheckBox? JpegliAdaptiveQuantCheck;
        private ComboBox? JpegliEncoderBackendCombo;
        private Border? JpegliPsnrPanel;
        private NumericUpDown? JpegliPsnrBox;
        // ── ICC 色彩管理控件 ──
        private RadioButton? IccModeNone, IccModeCarry, IccModeBake, IccModeBakeOnly;
        // 用户是否手动切换过 ICC 模式（true 后色彩空间联动不再自动改变模式）；抑制联动写入误标记
        private bool _iccModeTouched; private bool _suppressIccAutoLink;
        private StackPanel? IccFilePanel, IccBakePanel;
        private TextBox? IccPathBox;
        private TextBlock? IccInfoLabel, IccCompatText, IccPreviewText;
        private ComboBox? IccSourceSpaceCombo;
        private Border? IccCompatPanel, IccPreviewPanel;
        private string? _iccFilePath;
        private int _lastProbedSourceBitDepth; // 上次探测的源位深，供冲突检测使用
        // ── 拖放区域 ──
        private Border? DropZone;
        private TextBlock? DropHint;
        private Border? MediaDropZone;
        private TextBlock? FileCountLabel;
        private ListBox? MediaFileList;
        private TextBlock? MediaFileCount;
        private Button? FormatFilterBtn;
        private Button? PresetManagerBtn;
        private readonly List<string> _selectedFiles = new();
        // 当批量拖拽多个文件夹时，记录每个已选文件对应的输入根目录，
        // 以便在保留输入目录结构时按各自根目录计算相对路径。
        private readonly Dictionary<string, string> _selectedFileBaseDirs = new();
        private readonly ObservableCollection<string> _mediaFiles = new();
        private readonly List<Models.QueueItem> _queueItems = new();
        private string? _inputBaseDir;

        // ── PNG 预测模式值数组（与 PngPredCombo 的 SelectedIndex 对应）──
        // 索引: 0=none, 1=sub, 2=up, 3=avg, 4=paeth, 5=mixed
        private static readonly string[] _pngPredValues = { "none", "sub", "up", "avg", "paeth", "mixed" };

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
            ConversionModeCombo = this.FindControl<ComboBox>("ConversionModeCombo");
            FormatCombo = this.FindControl<ComboBox>("FormatCombo");
            DngCompressionCombo = this.FindControl<ComboBox>("DngCompressionCombo");
            DngJxlQualityPanel = this.FindControl<StackPanel>("DngJxlQualityPanel");
            DngJxlQualitySlider = this.FindControl<Slider>("DngJxlQualitySlider");
            DngJxlQualityBox = this.FindControl<TextBox>("DngJxlQualityBox");
            DngJxlAdvancedPanel = this.FindControl<StackPanel>("DngJxlAdvancedPanel");
            DngJxlEffortBox = this.FindControl<NumericUpDown>("DngJxlEffortBox");
            DngJxlDecodeSpeedBox = this.FindControl<NumericUpDown>("DngJxlDecodeSpeedBox");
            DngLinearCombo = this.FindControl<ComboBox>("DngLinearCombo");
            DngLinearHint = this.FindControl<TextBlock>("DngLinearHint");
            DngBitDepthCombo = this.FindControl<ComboBox>("DngBitDepthCombo");
            DngHighlightCombo = this.FindControl<ComboBox>("DngHighlightCombo");
            RawEngineStatus = this.FindControl<TextBlock>("RawEngineStatus");
            RawPanel = this.FindControl<StackPanel>("RawPanel");
            DngOptionsPanel = this.FindControl<StackPanel>("DngOptionsPanel");
            StillOptionsPanel = this.FindControl<StackPanel>("StillOptionsPanel");
            EncoderCombo = this.FindControl<ComboBox>("EncoderCombo");
            QualitySlider = this.FindControl<Slider>("QualitySlider");
            QualityBox = this.FindControl<TextBox>("QualityBox");
            QualityValue = this.FindControl<TextBlock>("QualityValue");
            ChromaCombo = this.FindControl<ComboBox>("ChromaCombo");
            ColorSpaceCombo = this.FindControl<ComboBox>("ColorSpaceCombo");
            ColorPrimariesCombo = this.FindControl<ComboBox>("ColorPrimariesCombo");
            ColorTrcCombo = this.FindControl<ComboBox>("ColorTrcCombo");
            ColorMatrixCombo = this.FindControl<ComboBox>("ColorMatrixCombo");
            ColorRangeCombo = this.FindControl<ComboBox>("ColorRangeCombo");
            TonemapCurveCombo = this.FindControl<ComboBox>("TonemapCurveCombo");
            ColorRangeLabel = this.FindControl<TextBlock>("ColorRangeLabel");
            ColorConflictLabel = this.FindControl<TextBlock>("ColorConflictLabel");
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
            AppendPngExtCheck = this.FindControl<CheckBox>("AppendPngExtCheck");
            // 图片最长边限制
            EnableMaxDimensionCheck = this.FindControl<CheckBox>("EnableMaxDimensionCheck");
            MaxDimensionPanel = this.FindControl<StackPanel>("MaxDimensionPanel");
            MaxDimensionBox = this.FindControl<NumericUpDown>("MaxDimensionBox");
            MaxDimensionHint = this.FindControl<TextBlock>("MaxDimensionHint");
            QueueList = this.FindControl<ListBox>("QueueList");
            ConcurrencyBox = this.FindControl<TextBox>("ConcurrencyBox");
            ConcurrencyLabel = this.FindControl<TextBlock>("ConcurrencyLabel");
            CommandText = this.FindControl<TextBox>("CommandText");
            MediaInfoText = this.FindControl<TextBox>("MediaInfoText");
            LogText = this.FindControl<TextBox>("LogText");
            FfmpegPathBox = this.FindControl<TextBox>("FfmpegPathBox");
            OutputDirBox = this.FindControl<TextBox>("OutputDirBox");
            JxlLibDirBox = this.FindControl<TextBox>("JxlLibDirBox");
            ExifToolPathBox = this.FindControl<TextBox>("ExifToolPathBox");
            ArtifactsDirBox = this.FindControl<TextBox>("ArtifactsDirBox");
            CacheDirBox = this.FindControl<TextBox>("CacheDirBox");
            CacheToggleBtn = this.FindControl<Button>("CacheToggleBtn");
            CachePanel = this.FindControl<StackPanel>("CachePanel");
            JxlLibStatus = this.FindControl<TextBlock>("JxlLibStatus");
            ArtifactsStatus = this.FindControl<TextBlock>("ArtifactsStatus");
            // ── 外部工具详细状态面板 ──
            JxlToolsStatus = this.FindControl<StackPanel>("JxlToolsStatus");
            ExifToolToolsStatus = this.FindControl<StackPanel>("ExifToolToolsStatus");
            ArtifactsToolsStatus = this.FindControl<StackPanel>("ArtifactsToolsStatus");
            // ── Photoshop ACR 验证 (2026-08-15) ──
            PhotoshopPathBox = this.FindControl<TextBox>("PhotoshopPathBox");
            PsToolsStatus = this.FindControl<StackPanel>("PsToolsStatus");
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
            PngDpiBox = this.FindControl<NumericUpDown>("PngDpiBox");
            WebpPresetCombo = this.FindControl<ComboBox>("WebpPresetCombo");
            WebpCompressionBox = this.FindControl<NumericUpDown>("WebpCompressionBox");
            WebpLosslessPanel = this.FindControl<StackPanel>("WebpLosslessPanel");
            AvifCpuUsedBox = this.FindControl<NumericUpDown>("AvifCpuUsedBox");
            AvifStillPictureCheck = this.FindControl<CheckBox>("AvifStillPictureCheck");
            AvifRowMtCheck = this.FindControl<CheckBox>("AvifRowMtCheck");
            AvifTuneCombo = this.FindControl<ComboBox>("AvifTuneCombo");
            // libaom-av1 高级图像控件
            AvifAqModeCombo = this.FindControl<ComboBox>("AvifAqModeCombo");
            AvifCdefCheck = this.FindControl<CheckBox>("AvifCdefCheck");
            AvifIntrabcCheck = this.FindControl<CheckBox>("AvifIntrabcCheck");
            AvifDenoiseBox = this.FindControl<NumericUpDown>("AvifDenoiseBox");
            // AVIF 编码器特定面板
            LibaomAvifPanel = this.FindControl<StackPanel>("LibaomAvifPanel");
            SvtAvifPanel = this.FindControl<StackPanel>("SvtAvifPanel");
            HwAvifPanel = this.FindControl<StackPanel>("HwAvifPanel");
            NvencAvifPanel = this.FindControl<StackPanel>("NvencAvifPanel");
            QsvAvifPanel = this.FindControl<StackPanel>("QsvAvifPanel");
            VaapiAvifPanel = this.FindControl<StackPanel>("VaapiAvifPanel");
            AmfAvifPanel = this.FindControl<StackPanel>("AmfAvifPanel");
            SvtPresetBox = this.FindControl<NumericUpDown>("SvtPresetBox");
            SvtTuneCombo = this.FindControl<ComboBox>("SvtTuneCombo");
            // NVENC / QSV / VAAPI / AMF 专用控件
            NvencPresetCombo = this.FindControl<ComboBox>("NvencPresetCombo");
            AvifNvencAqBox = this.FindControl<NumericUpDown>("AvifNvencAqBox");
            AvifNvencSpatialAqCheck = this.FindControl<CheckBox>("AvifNvencSpatialAqCheck");
            QsvPresetCombo = this.FindControl<ComboBox>("QsvPresetCombo");
            AvifQsvLowPowerCheck = this.FindControl<CheckBox>("AvifQsvLowPowerCheck");
            VaapiPresetCombo = this.FindControl<ComboBox>("VaapiPresetCombo");
            AvifVaapiLowPowerCheck = this.FindControl<CheckBox>("AvifVaapiLowPowerCheck");
            AmfPresetCombo = this.FindControl<ComboBox>("AmfPresetCombo");
            // 旧硬件面板(保留兼容)
            HwPresetCombo = this.FindControl<ComboBox>("HwPresetCombo");
            PriorityCombo = this.FindControl<ComboBox>("PriorityCombo");
            JxlEffortBox = this.FindControl<NumericUpDown>("JxlEffortBox");
            JxlModularCheck = this.FindControl<CheckBox>("JxlModularCheck");
            // cjxl 专属控件
            CjxlEffortBox = this.FindControl<NumericUpDown>("CjxlEffortBox");
            CjxlProgressiveCheck = this.FindControl<CheckBox>("CjxlProgressiveCheck");
            JxlLosslessJpegCheck = this.FindControl<CheckBox>("JxlLosslessJpegCheck");
            JxlLosslessJpegPanel = this.FindControl<StackPanel>("JxlLosslessJpegPanel");
            CjxlPhotonNoiseBox = this.FindControl<NumericUpDown>("CjxlPhotonNoiseBox");
            CjxlAutoPhotonNoiseCheck = this.FindControl<CheckBox>("CjxlAutoPhotonNoiseCheck");
            JxlPreserveUltrahdrCheck = this.FindControl<CheckBox>("JxlPreserveUltrahdrCheck");
            JpegHuffmanCombo = this.FindControl<ComboBox>("JpegHuffmanCombo");
            JpegDctCombo = this.FindControl<ComboBox>("JpegDctCombo");
            JpegProgressiveCombo = this.FindControl<ComboBox>("JpegProgressiveCombo");
            // ── Gain Map (Ultra HDR) JPEG 控件 ──
            JpegGainMapPanel = this.FindControl<Border>("JpegGainMapPanel");
            JpegGainMapOptions = this.FindControl<StackPanel>("JpegGainMapOptions");
            HdrModeCombo = this.FindControl<ComboBox>("HdrModeCombo");
            JpegGainMapFollowMainCheck = this.FindControl<CheckBox>("JpegGainMapFollowMainCheck");
            JpegGainMapQualityPanel = this.FindControl<StackPanel>("JpegGainMapQualityPanel");
            JpegGainMapQualityBox = this.FindControl<TextBox>("JpegGainMapQualityBox");
            GainMapQualityUpBtn = this.FindControl<Button>("GainMapQualityUpBtn");
            GainMapQualityDownBtn = this.FindControl<Button>("GainMapQualityDownBtn");
            JpegGainMapNitsSlider = this.FindControl<Slider>("JpegGainMapNitsSlider");
            JpegGainMapNitsBox = this.FindControl<TextBox>("JpegGainMapNitsBox");
            JpegGainMapHdrCfCombo = this.FindControl<ComboBox>("JpegGainMapHdrCfCombo");
            JpegGainMapDownsampleCombo = this.FindControl<ComboBox>("JpegGainMapDownsampleCombo");
            JpegGainMapTypeCombo = this.FindControl<ComboBox>("JpegGainMapTypeCombo");
            JpegGainMapBaseCombo = this.FindControl<ComboBox>("JpegGainMapBaseCombo");
            JpegGainMapMultiChannelCheck = this.FindControl<CheckBox>("JpegGainMapMultiChannelCheck");  // XAML 已改 ComboBox，此引用为 null 兼容
            // ── 色域映射（GMO）开关 ──
            ColorGamutMapCheck = this.FindControl<CheckBox>("ColorGamutMapCheck");
            RawColorTargetCombo = this.FindControl<ComboBox>("RawColorTargetCombo");
            TiffCompressionCombo = this.FindControl<ComboBox>("TiffCompressionCombo");
            TiffDpiBox = this.FindControl<NumericUpDown>("TiffDpiBox");
            ThumbnailPanel = this.FindControl<StackPanel>("ThumbnailPanel");
            EnableThumbnailCheck = this.FindControl<CheckBox>("EnableThumbnailCheck");
            ThumbnailSizeBox = this.FindControl<NumericUpDown>("ThumbnailSizeBox");
            ThumbnailQualityBox = this.FindControl<NumericUpDown>("ThumbnailQualityBox");
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
            PresetManagerBtn = this.FindControl<Button>("PresetManagerBtn");
            // ── ICC 色彩管理控件 ──
            IccModeNone = this.FindControl<RadioButton>("IccModeNone");
            IccModeCarry = this.FindControl<RadioButton>("IccModeCarry");
            IccModeBake = this.FindControl<RadioButton>("IccModeBake");
            IccModeBakeOnly = this.FindControl<RadioButton>("IccModeBakeOnly");
            IccFilePanel = this.FindControl<StackPanel>("IccFilePanel");
            IccBakePanel = this.FindControl<StackPanel>("IccBakePanel");
            IccPathBox = this.FindControl<TextBox>("IccPathBox");
            IccInfoLabel = this.FindControl<TextBlock>("IccInfoLabel");
            IccCompatText = this.FindControl<TextBlock>("IccCompatText");
            IccPreviewText = this.FindControl<TextBlock>("IccPreviewText");
            IccSourceSpaceCombo = this.FindControl<ComboBox>("IccSourceSpaceCombo");

            IccCompatPanel = this.FindControl<Border>("IccCompatPanel");
            IccPreviewPanel = this.FindControl<Border>("IccPreviewPanel");
            // GPU 编码器警告面板
            GpuEncoderWarning = this.FindControl<Border>("GpuEncoderWarning");
            GpuEncoderWarningText = this.FindControl<TextBlock>("GpuEncoderWarningText");
            GpuEncoderWarningHint = this.FindControl<TextBlock>("GpuEncoderWarningHint");
            EncoderOptionWarning = this.FindControl<Border>("EncoderOptionWarning");
            EncoderOptionWarningText = this.FindControl<TextBlock>("EncoderOptionWarningText");

            // 设置绑定和初始值
            if (FormatCombo != null) FormatCombo.SelectedIndex = 0;
            if (ChromaCombo != null) ChromaCombo.SelectedIndex = 0; // auto
            if (BitDepthCombo != null) BitDepthCombo.SelectedIndex = 0; // auto
            if (ColorSpaceCombo != null) ColorSpaceCombo.SelectedIndex = 0; // auto
            if (ColorPrimariesCombo != null) ColorPrimariesCombo.SelectedIndex = 0;
            if (ColorTrcCombo != null) ColorTrcCombo.SelectedIndex = 0;
            if (ColorMatrixCombo != null) ColorMatrixCombo.SelectedIndex = 0;
            if (ColorRangeCombo != null) ColorRangeCombo.SelectedIndex = 0; // auto
            // ⚠⚠ 2026-10-04 修（P0）：`RawColorTargetCombo` 此前**漏了这一个初始化**（其 8 个兄弟下拉都在上面 :536-543）。
            //   不加 ⇒ `SelectedItem == null` ⇒ `RawColorTarget` 恒 null ⇒ GUI 选 `auto`/`rec709` **无效**
            //   （`QueueProcessor.cs:666` 的 `?? "rec2020"` 兜底）。三项顺序见 `MainWindow.xaml`：rec2020 | auto | rec709，
            //   首项 `rec2020` 正是产品默认档（与 CLI 默认一致）⇒ 选中 0 不改变既有行为，只让**控件变得可用**。
            if (RawColorTargetCombo != null) RawColorTargetCombo.SelectedIndex = 0; // rec2020（默认档）

            // ── Photoshop 验证设置加载 (2026-08-15) ──
            RefreshPsPanelState();

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
                UpdateChromaBitDepthOptions();
                UpdateGpuEncoderWarning();
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
            // ⚠️ 联动规则: 选择 primaries 时自动补全匹配的 trc/matrix (如 bt2020 → smpte2084 + bt2020nc)
            if (ColorPrimariesCombo != null)
                ColorPrimariesCombo.SelectionChanged += (_, _) =>
                {
                    AutoLinkColorParameters();
                    UpdateAdvancedColorValidation();
                    RegenerateCommand();
                };
            if (ColorTrcCombo != null) ColorTrcCombo.SelectionChanged += (_, _) => { UpdateAdvancedColorValidation(); RegenerateCommand(); };
            if (ColorMatrixCombo != null) ColorMatrixCombo.SelectionChanged += (_, _) => { UpdateAdvancedColorValidation(); RegenerateCommand(); };
            if (ColorRangeCombo != null) ColorRangeCombo.SelectionChanged += (_, _) => RegenerateCommand();
            // ⚠⚠ 2026-10-04 修（P0：**控件是死的** —— 宣称≠交付）：
            //   本控件此前**既没有初始选中、也没有 SelectionChanged 处理器**（其 8 个兄弟下拉都有，见上面 :536-543）。
            //   后果：Avalonia 的 ComboBox 在无选中时 `SelectedItem == null` ⇒
            //   `RawColorTarget = RawColorTargetCombo?.SelectedItem as string`（:2879 / :5864）恒为 **null**
            //   ⇒ `QueueProcessor.cs:666` 的 `?? "rec2020"` **永远生效** ⇒ 用户在 GUI 里选
            //   `auto` / `rec709` **完全不起作用**，且命令预览不刷新。
            //   实测（审查 2026-10-04）：CLI `--raw-color-target rec709` 正常（产物 BT.709 ICC），
            //   缺陷**仅在 GUI** ⇒ 属"交付了却不能用的控件"。
            if (RawColorTargetCombo != null) RawColorTargetCombo.SelectionChanged += (_, _) => RegenerateCommand();
            // 高级编码器选项
            if (PngPredCombo != null) PngPredCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (PngDpiBox != null) PngDpiBox.ValueChanged += (_, _) => RegenerateCommand();
            if (WebpPresetCombo != null) WebpPresetCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (WebpCompressionBox != null) WebpCompressionBox.ValueChanged += (_, _) => RegenerateCommand();
            if (AvifCpuUsedBox != null) AvifCpuUsedBox.ValueChanged += (_, _) => RegenerateCommand();
            if (AvifStillPictureCheck != null) AvifStillPictureCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (AvifRowMtCheck != null) AvifRowMtCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (AvifTuneCombo != null) AvifTuneCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (SvtPresetBox != null) SvtPresetBox.ValueChanged += (_, _) => RegenerateCommand();
            if (SvtTuneCombo != null) SvtTuneCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (HwPresetCombo != null) HwPresetCombo.SelectionChanged += (_, _) => RegenerateCommand();
            // ── libaom-av1 高级图像控件 ──
            if (AvifAqModeCombo != null) AvifAqModeCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (AvifCdefCheck != null) AvifCdefCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (AvifIntrabcCheck != null) AvifIntrabcCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (AvifDenoiseBox != null) AvifDenoiseBox.ValueChanged += (_, _) => RegenerateCommand();
            // ── NVENC 控件 ──
            if (NvencPresetCombo != null) NvencPresetCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (AvifNvencAqBox != null) AvifNvencAqBox.ValueChanged += (_, _) => RegenerateCommand();
            if (AvifNvencSpatialAqCheck != null) AvifNvencSpatialAqCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            // ── QSV 控件 ──
            if (QsvPresetCombo != null) QsvPresetCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (AvifQsvLowPowerCheck != null) AvifQsvLowPowerCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            // ── VAAPI 控件 ──
            if (VaapiPresetCombo != null) VaapiPresetCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (AvifVaapiLowPowerCheck != null) AvifVaapiLowPowerCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            // ── AMF 控件 ──
            if (AmfPresetCombo != null) AmfPresetCombo.SelectionChanged += (_, _) => RegenerateCommand();
            // 进程优先级变更时持久化
            if (PriorityCombo != null)
            {
                var savedPriority = AppSettingsService.Current.FfmpegPriority;
                PriorityCombo.SelectedIndex = Math.Clamp(savedPriority, 0, 5);
                PriorityCombo.SelectionChanged += async (_, _) =>
                {
                    // RealTime 优先级风险确认
                    if (PriorityCombo.SelectedIndex == 0)
                    {
                        var confirmed = await ShowConfirmDialogAsync("⚠️ 实时优先级警告",
                            "「实时」优先级高于系统键盘/鼠标输入线程。\n" +
                            "如果 ffmpeg 进入 CPU 密集编码（如 AV1），系统将完全无响应，只能硬重启。\n\n" +
                            "确定要使用实时优先级吗？");
                        if (!confirmed)
                        {
                            // 恢复到之前的选择
                            PriorityCombo.SelectedIndex = Math.Clamp(
                                AppSettingsService.Current.FfmpegPriority, 0, 5);
                            return;
                        }
                    }
                    AppSettingsService.Current.FfmpegPriority = PriorityCombo.SelectedIndex;
                    AppSettingsService.Save();
                };
            }
            if (JxlEffortBox != null) JxlEffortBox.ValueChanged += (_, _) => RegenerateCommand();
            if (JxlModularCheck != null) JxlModularCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            // cjxl 控件事件
            if (CjxlEffortBox != null) CjxlEffortBox.ValueChanged += (_, _) => RegenerateCommand();
            if (CjxlProgressiveCheck != null) CjxlProgressiveCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (JxlLosslessJpegCheck != null) JxlLosslessJpegCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (CjxlPhotonNoiseBox != null) CjxlPhotonNoiseBox.ValueChanged += (_, _) => RegenerateCommand();
            if (CjxlAutoPhotonNoiseCheck != null)
            {
                CjxlAutoPhotonNoiseCheck.IsCheckedChanged += (_, _) =>
                {
                    // 自动模式禁用手动 ISO 输入框；无损时两只一起禁用（判据在
                    // UpdateCjxlPhotonNoiseAvailability，与 LosslessCheck 共用同一处）
                    UpdateCjxlPhotonNoiseAvailability();
                    RegenerateCommand();
                };
            }
            if (JxlPreserveUltrahdrCheck != null) JxlPreserveUltrahdrCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
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
            if (JpegGainMapTypeCombo != null)
                JpegGainMapTypeCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (JpegGainMapBaseCombo != null)
                JpegGainMapBaseCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (JpegGainMapMultiChannelCheck != null)  // XAML 已改 ComboBox，此引用为 null 兼容
                JpegGainMapMultiChannelCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (TiffCompressionCombo != null) TiffCompressionCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (TiffDpiBox != null) TiffDpiBox.ValueChanged += (_, _) => RegenerateCommand();
            if (TiffDpiBox != null) TiffDpiBox.ValueChanged += (_, _) => RegenerateCommand();
            if (EnableThumbnailCheck != null) EnableThumbnailCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (ThumbnailSizeBox != null) ThumbnailSizeBox.ValueChanged += (_, _) => RegenerateCommand();
            if (ThumbnailQualityBox != null) ThumbnailQualityBox.ValueChanged += (_, _) => RegenerateCommand();
            // cjpegli / jpegli 高级选项事件
            if (JpegliChromaCombo != null) JpegliChromaCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (JpegliProgressiveCombo != null) JpegliProgressiveCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (JpegliOptimizeCheck != null) JpegliOptimizeCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (JpegliAdaptiveQuantCheck != null) JpegliAdaptiveQuantCheck.IsCheckedChanged += (_, _) => RegenerateCommand();
            if (JpegliEncoderBackendCombo != null) { JpegliEncoderBackendCombo.SelectionChanged += (_, _) => { UpdateJpegliPsnrVisibility(); RegenerateCommand(); }; }
            if (JpegliPsnrBox != null) JpegliPsnrBox.ValueChanged += (_, _) => RegenerateCommand();
            // ── ICC 色彩管理事件 ──
            if (IccModeNone != null) IccModeNone.IsCheckedChanged += (_, _) => { MarkIccModeTouched(); UpdateIccPanelVisibility(); RegenerateCommand(); };
            if (IccModeCarry != null) IccModeCarry.IsCheckedChanged += (_, _) => { MarkIccModeTouched(); UpdateIccPanelVisibility(); RegenerateCommand(); };
            if (IccModeBake != null) IccModeBake.IsCheckedChanged += (_, _) => { MarkIccModeTouched(); UpdateIccPanelVisibility(); RegenerateCommand(); };
            if (IccModeBakeOnly != null) IccModeBakeOnly.IsCheckedChanged += (_, _) => { MarkIccModeTouched(); UpdateIccPanelVisibility(); RegenerateCommand(); };
            if (IccSourceSpaceCombo != null) IccSourceSpaceCombo.SelectionChanged += (_, _) => { UpdateIccPreview(); RegenerateCommand(); };

            // 初始化 ICC 面板状态
            UpdateIccPanelVisibility();
            
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

            // 图片最长边限制：从配置加载并持久化
            if (EnableMaxDimensionCheck != null)
            {
                EnableMaxDimensionCheck.IsChecked = AppSettingsService.Current.EnableMaxDimension;
                EnableMaxDimensionCheck.IsCheckedChanged += (_, _) =>
                {
                    AppSettingsService.Current.EnableMaxDimension = EnableMaxDimensionCheck.IsChecked ?? false;
                    AppSettingsService.Save();
                };
                if (MaxDimensionBox != null)
                {
                    MaxDimensionBox.Value = (decimal)Math.Clamp(AppSettingsService.Current.MaxLength, 1, 7680);
                    MaxDimensionBox.ValueChanged += (_, _) =>
                    {
                        if (MaxDimensionBox.Value.HasValue)
                        {
                            AppSettingsService.Current.MaxLength = (int)Math.Clamp(MaxDimensionBox.Value.Value, 1, 7680);
                            AppSettingsService.Save();
                        }
                    };
                }
            }

            // 无损编码 / JXL 强制元数据 → 每次变化刷新命令与选项
            if (LosslessCheck != null)
                LosslessCheck.IsCheckedChanged += (_, _) =>
                {
                    RegenerateCommand();
                    // 无损开关影响 WebP 无损压缩级别面板可见性
                    UpdateCodecPanelVisibility(NormalizeFormat(FormatCombo?.SelectedItem as string));
                    // 无损与 cjxl 光子噪声互斥（distance=0 加噪声会静默破坏逐比特无损）
                    UpdateCjxlPhotonNoiseAvailability();
                };
            
            // 图片最长边限制：复选框变更时刷新命令预览，并切换 MaxDimensionPanel 可见性
            if (EnableMaxDimensionCheck != null)
            {
                EnableMaxDimensionCheck.IsCheckedChanged += (_, _) =>
                {
                    RegenerateCommand();
                    if (MaxDimensionPanel != null)
                        MaxDimensionPanel.IsVisible = EnableMaxDimensionCheck.IsChecked == true;
                };
                // 初始化面板可见性
                if (MaxDimensionPanel != null)
                    MaxDimensionPanel.IsVisible = EnableMaxDimensionCheck.IsChecked == true;
            }
            
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

            // GPU 硬件加速开关按钮
            GpuToggleBtn = this.FindControl<Button>("GpuToggleBtn");
            if (GpuToggleBtn != null)
            {
                var gpuOn = App.IsGpuEnabled;
                GpuToggleBtn.Content = gpuOn ? "GPU" : "CPU";
                ToolTip.SetTip(GpuToggleBtn, gpuOn
                    ? "GPU 加速已启用（ANGLE/D3D11）— 点击切换为 CPU 软件渲染（需重启）"
                    : "GPU 加速已禁用 — 点击切换为 GPU 硬件加速（需重启）");
            }

            // ⚙ 设置菜单：初始化勾选态（渲染后端 / 主题 / 语言 / 缓存目录）
            SyncSettingsMenuChecks();

            // ── ComboBox 选项本地化（见 FillComboByLoc 的说明）──
            // ⚠ key 顺序必须与原 XAML 里 <sys:String> 的顺序**逐字一致**：后端是按
            //   SelectedIndex 取值的，顺序错位会导致「界面选 A、实际跑 B」且不报错。
            // 参数：ComboBox、默认选中索引（须与原 XAML 的 SelectedIndex 一致）、选项 key（顺序同原 XAML）
            FillComboByLoc(this.FindControl<ComboBox>("DngCompressionCombo"), 1,
                "dng.comp.lossless.jpeg", "dng.comp.jxl");
            FillComboByLoc(this.FindControl<ComboBox>("DngLinearCombo"), 0,
                "dng.layout.cfa", "dng.layout.linear");
            FillComboByLoc(this.FindControl<ComboBox>("DngBitDepthCombo"), 1,
                "dng.bitdepth.8", "dng.bitdepth.16");
            FillComboByLoc(this.FindControl<ComboBox>("DngHighlightCombo"), 1,
                "dng.hl.clip", "dng.hl.recover", "dng.hl.blend");
            FillComboByLoc(this.FindControl<ComboBox>("ColorRangeCombo"), 0,
                "color.range.auto", "color.range.tv", "color.range.pc");
            FillComboByLoc(this.FindControl<ComboBox>("PngPredCombo"), 4,
                "png.pred.none", "png.pred.sub", "png.pred.up",
                "png.pred.avg", "png.pred.paeth", "png.pred.mixed");
            FillComboByLoc(this.FindControl<ComboBox>("AvifTuneCombo"), 4,
                "avif.tune.default", "avif.tune.psnr", "avif.tune.ssim",
                "avif.tune.vmaf", "avif.tune.iq");
            FillComboByLoc(this.FindControl<ComboBox>("AvifAqModeCombo"), 0,
                "avif.aq.default", "avif.aq.variance", "avif.aq.complexity");
            FillComboByLoc(this.FindControl<ComboBox>("SvtTuneCombo"), 1,
                "svt.tune.default", "svt.tune.vmaf", "svt.tune.psnr", "svt.tune.ssim");
            FillComboByLoc(this.FindControl<ComboBox>("NvencPresetCombo"), 3,
                "nvenc.p1", "nvenc.p2", "nvenc.p3", "nvenc.p4", "nvenc.p5", "nvenc.p6", "nvenc.p7");
            FillComboByLoc(this.FindControl<ComboBox>("QsvPresetCombo"), 3,
                "qsv.veryfast", "qsv.faster", "qsv.fast", "qsv.medium",
                "qsv.slow", "qsv.slower", "qsv.veryslow");
            FillComboByLoc(this.FindControl<ComboBox>("VaapiPresetCombo"), 3,
                "vaapi.1", "vaapi.2", "vaapi.3", "vaapi.4", "vaapi.5", "vaapi.6", "vaapi.7");
            FillComboByLoc(this.FindControl<ComboBox>("AmfPresetCombo"), 1,
                "amf.speed", "amf.balanced", "amf.quality");
            FillComboByLoc(this.FindControl<ComboBox>("JpegGainMapTypeCombo"), 0,
                "gainmap.type.gray", "gainmap.type.rgb");
            FillComboByLoc(this.FindControl<ComboBox>("JpegGainMapBaseCombo"), 0,
                "gainmap.base.srgb", "gainmap.base.rec2020");
            // HDR 实现模式（用户-facing 单真值 = `FfmpegOptions.HdrMode`，它是 `JpegGainMap` 的强类型视图）
            FillComboByLoc(this.FindControl<ComboBox>("HdrModeCombo"), 0,
                "hdr.mode.traditional", "hdr.mode.gainmap");
            FillComboByLoc(this.FindControl<ComboBox>("JpegGainMapDownsampleCombo"), 1,
                "gainmap.ds.full", "gainmap.ds.half", "gainmap.ds.quarter",
                "gainmap.ds.eighth", "gainmap.ds.sixteenth");
            FillComboByLoc(this.FindControl<ComboBox>("MetadataModeCombo"), 0,
                "meta.keep.all", "meta.strip.all");
            FillComboByLoc(this.FindControl<ComboBox>("PriorityCombo"), 3,
                "priority.realtime", "priority.high", "priority.above.normal",
                "priority.normal", "priority.below.normal", "priority.low");
            FillComboByLoc(this.FindControl<ComboBox>("JpegProgressiveCombo"), 1,
                "jpeg.progressive.auto", "jpeg.progressive.baseline", "jpeg.progressive.prog");
            FillComboByLoc(this.FindControl<ComboBox>("JpegliProgressiveCombo"), 2,
                "jpeg.progressive.auto", "jpeg.progressive.baseline", "jpeg.progressive.prog");
            FillComboByLoc(this.FindControl<ComboBox>("JpegliEncoderBackendCombo"), 0,
                "jpegli.backend.libjpeg", "jpegli.backend.sjpeg");

            // ── 简洁模式控件 ──
            SimpleModeEntryBtn = this.FindControl<Button>("SimpleModeEntryBtn");
            SimpleErrorsOnlyCheck = this.FindControl<CheckBox>("SimpleErrorsOnlyCheck");
            FullModePanel = this.FindControl<DockPanel>("FullModePanel");
            SimpleModePanel = this.FindControl<Grid>("SimpleModePanel");
            SimpleQueueList = this.FindControl<ListBox>("SimpleQueueList");
            SimpleMediaList = this.FindControl<ListBox>("SimpleMediaList");
            SimpleDropZone = this.FindControl<Border>("SimpleDropZone");
            SimpleFileCount = this.FindControl<TextBlock>("SimpleFileCount");
            SimpleQueueCount = this.FindControl<TextBlock>("SimpleQueueCount");
            AutoEncodeToggle = this.FindControl<ToggleSwitch>("AutoEncodeToggle");
            SimplePresetCombo = this.FindControl<ComboBox>("SimplePresetCombo");
            SimpleStatusLabel = this.FindControl<TextBlock>("SimpleStatusLabel");
            SimpleProgressLabel = this.FindControl<TextBlock>("SimpleProgressLabel");
            SimpleEtaLabel = this.FindControl<TextBlock>("SimpleEtaLabel");
            SimpleElapsedLabel = this.FindControl<TextBlock>("SimpleElapsedLabel");
            SimpleCurrentStateLabel = this.FindControl<TextBlock>("SimpleCurrentStateLabel");

            // 简洁模式预设列表初始化
            if (SimplePresetCombo != null)
            {
                var allPresets = PresetManagerService.GetAllPresets();
                foreach (var p in allPresets)
                    SimplePresetCombo.Items.Add(p);
                SimplePresetCombo.SelectedIndex = 0;
                _simpleActivePreset = allPresets.FirstOrDefault();
                // 预设切换时自动应用到主界面参数
                SimplePresetCombo.SelectionChanged += SimplePresetCombo_SelectionChanged;
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

            // DNG JXL 质量滑块与输入框双向同步（RAW 模式）
            if (DngJxlQualitySlider != null)
                DngJxlQualitySlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value) && !_updatingQuality)
                    {
                        _updatingQuality = true;
                        if (DngJxlQualityBox != null)
                            DngJxlQualityBox.Text = ((int)DngJxlQualitySlider.Value).ToString();
                        _updatingQuality = false;
                    }
                };
            if (DngJxlQualityBox != null)
            {
                DngJxlQualityBox.TextChanged += (_, _) =>
                {
                    if (_updatingQuality) return;
                    if (int.TryParse(DngJxlQualityBox.Text, out var q) && DngJxlQualitySlider != null)
                    {
                        _updatingQuality = true;
                        DngJxlQualitySlider.Value = Math.Clamp(q, 0, 100);
                        _updatingQuality = false;
                    }
                };
                DngJxlQualityBox.LostFocus += (_, _) =>
                {
                    if (DngJxlQualitySlider != null)
                    {
                        var v = (int)DngJxlQualitySlider.Value;
                        DngJxlQualityBox.Text = v.ToString();
                        if (DngCompressionCombo != null && DngJxlQualityPanel != null)
                            DngJxlQualityPanel.IsVisible = DngCompressionCombo.SelectedIndex == 1;
                    }
                    RegenerateCommand();
                };
            }

            // ── DNG 高级选项事件 ──
            if (DngJxlEffortBox != null)
                DngJxlEffortBox.ValueChanged += (_, _) => RegenerateCommand();
            if (DngJxlDecodeSpeedBox != null)
                DngJxlDecodeSpeedBox.ValueChanged += (_, _) => RegenerateCommand();
            if (DngLinearCombo != null)
                DngLinearCombo.SelectionChanged += (_, _) =>
                {
                    // ⚠ 2026-09-21：选中「线性」时显示适用条件提示
                    //   （dngtool 对 Bayer 输入会明确拒绝 `-linear` ⇒ 必须让用户事先知道）
                    if (DngLinearHint != null)
                        DngLinearHint.IsVisible = DngLinearCombo.SelectedIndex == 1;
                    RegenerateCommand();
                };
            if (DngBitDepthCombo != null)
                DngBitDepthCombo.SelectionChanged += (_, _) => RegenerateCommand();
            if (DngHighlightCombo != null)
                DngHighlightCombo.SelectionChanged += (_, _) => RegenerateCommand();

            // RAW 引擎状态提示（dngtool）
            try
            {
                RawService.ClearCache();
                var rawOk = RawService.Detect();
                if (RawEngineStatus != null)
                {
                    RawEngineStatus.Text = rawOk
                        ? $"✅ dngtool 引擎: {Path.GetFileName(RawService.DetectedPath)} (支持 DNG 1.7 JXL 解码/编码)"
                        : "❌ 未检测到 dngtool — 请放入 PLAN/artifacts/ 目录";
                }
                if (DngCompressionCombo != null)
                    DngCompressionCombo.IsEnabled = rawOk && RawService.IsDngTool;
            }
            catch { }

            // 进度刷新定时器（每秒更新一次：完整模式 + 简洁模式）
            var progressTimer = new System.Timers.Timer(1000);
            progressTimer.Elapsed += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateProgressDisplay();
                    RefreshSimpleProgressIfActive();
                });
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
            if (!string.IsNullOrWhiteSpace(settings.JxlLibDir) && JxlLibDirBox != null)
                JxlLibDirBox.Text = settings.JxlLibDir;
            if (!string.IsNullOrWhiteSpace(settings.ExifToolPath) && ExifToolPathBox != null)
                ExifToolPathBox.Text = settings.ExifToolPath;
            if (!string.IsNullOrWhiteSpace(settings.WindowsArtifactsDir) && ArtifactsDirBox != null)
                ArtifactsDirBox.Text = settings.WindowsArtifactsDir;
            if (!string.IsNullOrWhiteSpace(settings.CacheDirectory) && CacheDirBox != null)
                CacheDirBox.Text = settings.CacheDirectory;
            if (PreserveInputStructure != null)
                PreserveInputStructure.IsChecked = settings.PreserveInputFolderStructure;
            if (ConcurrencyBox != null)
                ConcurrencyBox.Text = Math.Clamp(settings.MaxQueueSize, 1, 128).ToString();

            // ── PLAN 文件夹自动识别（便携包自动加载 ffmpeg 目录）──
            // 各外部工具（cjxl/djxl/cjpegli/exiftool/ultrahdr/jxr）的 PLAN 检测
            // 已内置于对应 Service.Detect() 中，此处仅处理 ffmpeg 目录。
            try
            {
                var planPath = PlatformServices.PlanFolderPath;
                if (planPath != null)
                {
                    var ffmpegInPlan = PlatformServices.FindPlanSubDir(planPath, "ffmpeg-full");
                    if (ffmpegInPlan != null
                        && string.IsNullOrWhiteSpace(AppSettingsService.Current.FfmpegDirectory))
                    {
                        AppSettingsService.Current.FfmpegDirectory = ffmpegInPlan;
                        AppSettingsService.Save();
                        if (FfmpegPathBox != null) FfmpegPathBox.Text = ffmpegInPlan;
                    }
                    if (LogText != null)
                        LogText.Text += $"[PLAN] 检测到便携组件包: {planPath}\n";
                }
            }
            catch { }

            // 启动时自动检测能力（异步，不阻塞 UI）
            _ = FullDetectionAsync();
        }

        /// <summary>
        /// 后台全量检测（完全异步，绝不阻塞 UI，每一步有独立超时保护）。
        /// 每步完成后通过 Dispatcher 增量更新 UI。
        /// </summary>
        private async Task FullDetectionAsync()
        {
            // 所有工作放到后台线程，主方法立即返回
            await Task.Run(async () =>
            {
                void Log(string msg) => Dispatcher.UIThread.Post(() =>
                {
                    if (LogText != null) LogText.Text += msg + "\n";
                });

                Log("正在检测 ffmpeg 能力与可用编码器...");

                // ── Step 1: 文件系统检测（并行化）──
                // ⚠ 不变式：**槽位数 == 被检测工具数 == `[detect]` 日志条数**，且**异常必须留名**。
                //   旧形态是 7 槽 / 6 工具 / 4 条日志：第 5 槽是 `efc06221` 删掉 UltrahdrService 时
                //   留下的**空任务**，而 djxl/jxr 两条**不打日志** ⇒ ①人读日志数不出"预热了几个工具"
                //   ②某个 Detect 抛异常被 `catch { }` 吞掉时**连"未找到"都不打**,面板只留一个 ❌、
                //   原因零记录。⇒ 统一走下面的 Mk：探测 + 必打一行 + 异常点名异常。
                System.Threading.Tasks.Task Mk(string name, Action probe, Func<bool> ok) =>
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { probe(); Log($"[detect] {name}: {(ok() ? "OK" : "未找到")}"); }
                        catch (Exception ex) { Log($"[detect] {name}: 探测抛异常 {ex.GetType().Name}: {ex.Message}（按未找到处理）"); }
                    });
                var detectTasks = new[]
                {
                    Mk("cjxl",      () => { CjxlService.ClearCache(); CjxlService.Detect(); },                        () => CjxlService.IsAvailable),
                    Mk("cjpegli",   () => { CjpegliService.ClearCache(); CjpegliService.Detect(); },                   () => CjpegliService.IsAvailable),
                    Mk("djxl",      () => { DjxlService.ClearCache(); DjxlService.Detect(); },                         () => DjxlService.IsAvailable),
                    Mk("exiftool",  () => { ExifToolService.ClearCache(); ExifToolService.Detect(m => Log(m.TrimEnd('\n'))); }, () => ExifToolService.IsAvailable),
                    Mk("JxrEncApp", () => { JxrService.ClearCache(); JxrService.Detect(); },                           () => JxrService.IsAvailable),
                    // avifenc 没有独立 Service，解析入口 `ResolveAvifencPath()` 本身无缓存 ⇒ 无 ClearCache 可做，
                    // 但**照样要占一个槽位、打一行日志**（否则"槽位=工具=日志"的不变式又被破坏）。
                    Mk("avifenc",   () => { },                                                                        () => PlatformServices.ResolveAvifencPath() != null),
                    Mk("dngtool",   () => { RawService.ClearCache(); RawService.Detect(); },                           () => RawService.IsAvailable),
                };
                await Task.WhenAll(detectTasks);

                // ── Step 2: ffmpeg 进程检测（串行，每项最多 8 秒）──
                var ffmpegPath = AppSettingsService.Current.FfmpegPath;
                if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
                {
                    Log("[detect] ffmpeg 已定位: " + ffmpegPath);
                    try
                    {
                        var t = Task.Run(() => FormatCapabilitiesService.InitializeAsync(ffmpegPath));
                        if (await Task.WhenAny(t, Task.Delay(8000)) == t) Log("[detect] ffmpeg 像素格式检测完成");
                        else Log("[detect] ⚠️ ffmpeg 像素格式检测超时（跳过）");
                    }
                    catch (Exception ex) { Log("[detect] ⚠️ ffmpeg 像素格式检测失败: " + ex.Message); }
                    try
                    {
                        var t = Task.Run(() => EncoderDetectionService.GetAllEncodersAsync(ffmpegPath));
                        if (await Task.WhenAny(t, Task.Delay(8000)) == t) Log("[detect] ffmpeg 编码器列表加载完成");
                        else Log("[detect] ⚠️ ffmpeg 编码器列表超时（跳过）");
                    }
                    catch (Exception ex) { Log("[detect] ⚠️ ffmpeg 编码器列表失败: " + ex.Message); }
                }
                else
                {
                    Log("[detect] ffmpeg 未找到（PATH 或 PLAN 均未检测到），跳过进程探测");
                }

                // ── Step 3: CPU / 外部工具版本 ──
                try { CpuFeatureService.Detect(); Log(CpuFeatureService.FullReport()); } catch { }
                try
                {
                    var fp = ExternalToolsDetector.ProbeFfmpeg(null, m => { if (LogText != null) LogText.Text += m; });
                    if (fp?.IsRunnable == true)
                        Log($"[ffmpeg] v{fp.Version} | SIMD: {string.Join(", ", fp.SimdFeatures)}");
                }
                catch { }
                try
                {
                    var tools = ExternalToolsDetector.ProbeAllTools(m => { if (LogText != null) LogText.Text += m; });
                    Log("── 外部工具检测 ──");
                    foreach (var t in tools)
                        Log($"  {t.StatusIcon} {t.Name}: {(t.IsAvailable ? "v" + t.Version : "未检测到")}");
                }
                catch (Exception ex) { Log("[tools] 探测失败: " + ex.Message); }

                // ── Step 4: GPU 硬件编码能力检测 ──
                try
                {
                    Log("── GPU 硬件编码能力检测 ──");
                    var gpuReport = await Services.GpuCapabilityService.DetectAsync();
                    if (gpuReport.Devices.Count > 0)
                    {
                        foreach (var dev in gpuReport.Devices)
                            Log($"  [GPU 设备] {dev.Description}: {(dev.IsAvailable ? "✅ 可用" : "❌ 不可用")}");
                    }
                    else
                    {
                        Log("  [GPU 设备] 未检测到任何硬件加速设备");
                    }
                    foreach (var kv in gpuReport.Encoders)
                    {
                        var icon = kv.Value.Availability switch
                        {
                            Services.GpuEncoderAvailability.Verified => "✅",
                            Services.GpuEncoderAvailability.DeviceFoundUntested => "⚡",
                            Services.GpuEncoderAvailability.CompiledNoDevice => "⚠️",
                            Services.GpuEncoderAvailability.Failed => "❌",
                            Services.GpuEncoderAvailability.NotCompiled => "⊘",
                            _ => "❓"
                        };
                        Log($"  {icon} {kv.Key}: {kv.Value.FriendlyName} — {kv.Value.Availability}");
                    }

                    // 延迟运行时验证（后台，不阻塞启动）
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Services.GpuCapabilityService.ValidateEncodersAsync();
                            var verifiedCount = gpuReport.Encoders.Values.Count(
                                e => e.Availability == Services.GpuEncoderAvailability.Verified);
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (LogText != null && verifiedCount > 0)
                                    LogText.Text += $"[GPU] 运行时验证完成: {verifiedCount} 个 GPU 编码器可用\n";
                            });
                        }
                        catch { }
                    });
                }
                catch (Exception ex) { Log($"[GPU] 检测失败: {ex.Message}"); }

                Log("[detect] 全部检测完成");

                // ── UI 刷新 ──
                Dispatcher.UIThread.Post(() =>
                {
                    try { _ = RefreshEncoderListAsync(); } catch { }
                    UpdateExifToolPanelState();
                    UpdateOptionAvailability();
                    RefreshPsPanelState();
                    RefreshToolsStatusBar();
                });
            });

            // 格式报告独立异步，不影响主检测
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(2000); await ReportFormatCapabilityStatusAsync(); } catch { }
            });
        }

        /// <summary>异步报告各格式的编码/封装/解码能力状态</summary>
        private async Task ReportFormatCapabilityStatusAsync()
        {
            try
            {
                var formats = new[] { "jpg", "png", "webp", "avif", "tiff", "jxl", "jxr", "dng", "gif", "apng", "bmp" };
                var ffmpegPath = AppSettingsService.Current.FfmpegPath;

                foreach (var fmt in formats)
                {
                    try
                    {
                        var status = await EncoderDetectionService.GetFormatStatusAsync(fmt, ffmpegPath);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (LogText != null)
                                LogText.Text += $"[format] {status}\n";
                        });
                    }
                    catch { /* 单个格式检测失败不影响其他 */ }
                }
            }
            catch { }
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
            var useAdvCodec = UseAdvancedCodec?.IsChecked ?? false;
            // Thumbnail: same gate as the collection side. Containers without a verified EXIF IFD1
            // write path hide the panel and reset the checkbox (see UpdateCodecPanelVisibility).
            var thumbOn = useAdvCodec && (EnableThumbnailCheck?.IsChecked ?? false);
            var thumbLongEdge = thumbOn ? (int)(ThumbnailSizeBox?.Value ?? 160) : 160;
            var thumbQuality = thumbOn ? (int)(ThumbnailQualityBox?.Value ?? 75) : 75;
            var autoTh = AutoThreadsCheck?.IsChecked ?? true;
            var singleTh = SingleThreadCheck?.IsChecked ?? false;
            int threads = singleTh ? 1 : autoTh ? Models.FfmpegOptions.ComputeAutoThreads() : (int)(ThreadsBox?.Value ?? 4);

            var backend = GetCurrentEncoderBackend();

            // DNG 输出: dngtool 编码命令预览 (非 ffmpeg 管线)
            if (fmt == "dng")
            {
                var dngOut = GetOutputPath(_inputPath, "dng");
                var useJxl = DngCompressionCombo?.SelectedIndex == 1;
                var jxlQ = ParseInt(DngJxlQualityBox?.Text, 0, 0, 100);
                var effort = (int)(DngJxlEffortBox?.Value ?? 7);
                var decSpeed = (int)(DngJxlDecodeSpeedBox?.Value ?? 1);
                var linear = DngLinearCombo?.SelectedIndex == 1;
                var cmd = useJxl
                    ? $"dngtool -e -i \"{_inputPath}\" -O \"{dngOut}\" -jxl" +
                      $"{(jxlQ > 0 ? $" -q {jxlQ}" : "")}" +
                      $" -effort {effort} -decode_speed {decSpeed}" +
                      (linear ? " -linear" : "")
                    : $"dngtool -e -i \"{_inputPath}\" -O \"{dngOut}\" -lossless" +
                      (linear ? " -linear" : "");
                if (CommandText != null)
                {
                    CommandText.Text = "# RAW → DNG 编码 (dngtool, 支持 DNG 1.7 JXL)" + Environment.NewLine +
                        $"# 压缩: {(useJxl ? $"JXL q={jxlQ} effort={effort} decodeSpeed={decSpeed}" : "无损 JPEG")} | 布局: {(linear ? "线性 DNG (无 CFA)" : "保留 CFA (Bayer)")}" + Environment.NewLine +
                        cmd;
                }
                return;
            }

            // JXL + 手动色彩空间 → 自动切 cjxl（ffmpeg libjxl 忽略 -color_primaries/-color_trc）
            if (fmt is "jxl" && backend != EncoderBackend.Cjxl
                && CjxlService.IsAvailable && IsColorManuallySpecified())
            {
                backend = EncoderBackend.Cjxl;
                if (LogText != null)
                    LogText.Text += "[jxl] 手动色彩空间 → 自动切换 cjxl 编码器\n";
            }
            else if (fmt is "jxl" && backend != EncoderBackend.Cjxl
                && !CjxlService.IsAvailable && IsColorManuallySpecified())
            {
                // 可用性诚实：cjxl 不可用时，ffmpeg 内置 libjxl 会忽略 -color_primaries/-color_trc，
                // 手动色彩空间无法兑现 → 明确告警，避免用户误以为色彩已应用（静默丢色）。
                if (LogText != null)
                    LogText.Text += "⚠️ [jxl] 手动色彩空间需 cjxl 兑现，但未检测到 cjxl；ffmpeg libjxl 会忽略色彩参数，手动色彩可能丢失。请安装 cjxl 或改用 auto 色彩。\n";
            }

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
                    CommandText.Text = BuildCjpegliPreviewCommand(_inputPath, outputPath, jpegliOpts)
                        + BuildThumbnailPreviewSuffix(thumbOn, thumbLongEdge, thumbQuality);
                }
                return;
            }

            if (backend == EncoderBackend.Cjxl)
            {
                var effort = useAdvCodec ? (int?)CjxlEffortBox?.Value ?? 7 : 7;
                var progressive = useAdvCodec ? (CjxlProgressiveCheck?.IsChecked ?? false) : false;
                var photonNoise = useAdvCodec ? (int)(CjxlPhotonNoiseBox?.Value ?? 0) : 0;
                // 预览只印数字 ISO；auto 那条由运行侧解析（cjxl 无 auto 取值，见下方 A-19 注释）
                var isJpegInput = IsJpegInput(_inputPath);
                var qualityVal = (int)(QualitySlider?.Value ?? 90);
                // 2026-10-02：与实跑共用同一真值（libjxl 官方曲线），消除预览与实跑的第二份公式
                var distance = Services.ColorMapping.ImageEncoderArgs.MapJxlDistance(qualityVal);
                var t = threads > 0 ? $" --num_threads={threads}" : "";

                var cmd = new System.Text.StringBuilder();
                cmd.Append("\"").Append(CjxlService.DetectedPath).Append("\" \"")
                   .Append(_inputPath).Append("\" \"").Append(outputPath).Append("\" -e ").Append(effort).Append(t);

                if (isJpegInput)
                {
                    // 无损重封装开关 + 未开启「保留 Ultra HDR 增益图」→ 直接封装 DCT 系数
                    // A-10：与采集侧共用 ReadJxlAdvancedCheckStates（面板收起时这两只不参与）
                    var jxlChecks = ReadJxlAdvancedCheckStates(fmt, useAdvCodec);
                    if (!jxlChecks.PreserveUltrahdr && jxlChecks.LosslessJpeg)
                    {
                        LockLosslessForJxl();
                        cmd.Append(" -d 0 --lossless_jpeg=1");
                    }
                    else
                    {
                        RestoreLosslessAndQuality();
                        if (LosslessCheck?.IsChecked ?? false)
                            cmd.Append(" -d 0");
                        else
                            cmd.Append(" -d ").Append($"{distance:F1}");
                    }
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
                // A-19（2026-09-26）：cjxl 没有 auto 这个取值（实测 --photon_noise_iso=auto ⇒ rc=1
                // "Error parsing flag"），真值是运行时从 EXIF 解析出的数字 ISO。预览不再印这条
                // 照着敲必然失败的占位串；也不在此起 exiftool 取真值 —— 本函数挂在每次控件变化上，
                // 为预览起进程等于每次改下拉框起一个进程。运行侧那条日志已点名解析结果。
                if (photonNoise > 0) cmd.Append(" --photon_noise_iso=").Append(photonNoise);

                if (CommandText != null)
                    CommandText.Text = cmd.ToString() + BuildThumbnailPreviewSuffix(thumbOn, thumbLongEdge, thumbQuality);

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

            // --- FFmpeg 后端：JPEG→JXL 无损重封装 ---
            // A-10（2026-09-26）：jbrd 重封装从此**只属于 cjxl 面板**（那只复选框就在 cjxl 面板里），
            // 而 ffmpeg libjxl 出口实测不写 jbrd box（见 FfmpegCommandBuilder 的 2026-09-19 更正）
            // ⇒ 这一侧不再自己算结论，只按采集侧同一个判定维持 LockLossless/Restore 的反向联动。
            if (fmt is "jxl" && IsJpegInput(_inputPath))
            {
                var jxlChecks = ReadJxlAdvancedCheckStates(fmt, useAdvCodec);
                if (jxlChecks.PreserveUltrahdr)
                {
                    RestoreLosslessAndQuality();
                    if (LogText != null)
                        LogText.Text += "[jxl] 已启用「保留增益图」→ 跳过无损重封装，使用常规编码\n";
                }
                else
                {
                    RestoreLosslessAndQuality();
                    if (LogText != null)
                        LogText.Text += "[jxl] FFmpeg libjxl 不支持 lossless_jpeg，可选择 cjxl 编码器启用极速模式\n";
                }
            }
            else
            {
                RestoreLosslessAndQuality();
            }

            // 唯一采集点：预览不再自己读控件（此前这份 opts 少读 AvifCpuUsed / Gif / cjpegli / DNG
            // 等二十来项，取消勾选后预览仍按开启画）。
            var opts = await CollectOptionsFromUiAsync(_inputPath, logNotices: false);
            if (opts == null) return;
            // A-10 之后上面那段联动与采集侧共用 ReadJxlAdvancedCheckStates ⇒ 两侧同式，
            // 原先这里那行「用预览算出的结论覆盖采集结果」已删除（它存在的唯一理由就是两侧不同式）。
            if (CommandText != null)
            {
                var args = "";
                WithEncoderOptionWarnings(() =>
                    args = Services.FfmpegCommandBuilder.BuildArguments(opts, _inputPath, outputPath));
                CommandText.Text = "ffmpeg " + args
                    + BuildThumbnailPreviewSuffix(opts.EnableThumbnail, opts.ThumbnailLongEdge, opts.ThumbnailQuality);
            }
        }

        /// <summary>
        /// App level thumbnail keys for the command preview. Only non default values are printed
        /// (same convention as the other previews); 160 / 75 mirror the FfmpegOptions defaults.
        /// </summary>
        private static string BuildThumbnailPreviewSuffix(bool enabled, int longEdge, int quality)
        {
            if (!enabled) return "";
            var sb = new System.Text.StringBuilder(" --thumbnail=true");
            if (longEdge != 160) sb.Append(" --thumbnail-size=").Append(longEdge);
            if (quality != 75) sb.Append(" --thumbnail-quality=").Append(quality);
            return sb.ToString();
        }

        // ── JXL 无损控件锁定/恢复 ──
        /// <summary>控件是否被 <see cref="LockLosslessForJxl"/> 锁上 —— 恢复时**只**撤销自己锁的那把。</summary>
        private bool _losslessLockedByJxl;

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
            _losslessLockedByJxl = true;
        }

        /// <summary>
        /// 撤销 <see cref="LockLosslessForJxl"/> 加的无损/质量锁定。
        /// <para>
        /// ⚠ P1-F 缺陷 2（2026-09-19 修）：本方法原先在**每一次** <c>RegenerateCommand</c> 都被调用，
        /// 判据是 <c>!IsEnabled &amp;&amp; SupportsLossless</c> ⇒ 把 <c>UpdateOptionAvailability</c> 刚为
        /// **纯无损格式**（png / tiff / apng，见其 <c>fmt3 is "png" or "tiff" or "apng"</c> 分支）
        /// 与**动图 JXL/AVIF 模式**（见 <c>UpdateCodecPanelVisibility</c>）设下的 <c>IsEnabled = false</c>
        /// 重新打开 —— 控件看起来能改、实际被强制，UI 对用户**说谎**（值本身仍被强制 ⇒ 产物是对的，
        /// 属 UI 状态一致性缺陷，不是输出缺陷）。
        /// </para>
        /// <para>
        /// 现在只撤销**自己**（JXL 无损重封装）那把锁：<c>_losslessLockedByJxl</c> 未置位 ⇒ 直接返回
        /// （格式级/模式级锁定一律不碰）；已置位 ⇒ 撤销前**复核**当前格式/模式是否本来就该锁。
        /// 恢复时机因此变成「切换格式 / 编码器 / 切换 JXL 保留增益图开关」等真正改变锁定条件的动作，
        /// 而不是「每次重生成命令」。
        /// </para>
        /// </summary>
        private void RestoreLosslessAndQuality()
        {
            if (!_losslessLockedByJxl) return;
            _losslessLockedByJxl = false;
            var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            var isAnimMode = ConversionModeCombo?.SelectedIndex == 1;
            // 与 UpdateOptionAvailability / UpdateCodecPanelVisibility 的锁定口径一致：这些场景下不得解开。
            bool formatLocks = fmt is "png" or "tiff" or "apng"
                || (isAnimMode && (fmt is "jxl" or "avif"));
            if (LosslessCheck != null && !formatLocks
                && _currentCapabilities?.SupportsLossless == true)
            {
                LosslessCheck.IsEnabled = true;
            }
            if (QualitySlider != null && !formatLocks
                && _currentCapabilities?.SupportsQuality == true)
            {
                QualitySlider.IsEnabled = true;
                if (QualityBox != null) QualityBox.IsEnabled = true;
            }
        }

        /// <summary>
        /// cjxl 光子噪声两只控件的可用性 —— **唯一判据点**（2026-09-30）。
        /// <para>
        /// **为什么无损时光子噪声必须不可选**（实测，891x981 16-bit，同 effort 同尺寸）：
        /// <c>-d 0</c> 单独 ⇒ djxl 回读与源**逐字节相同**；<c>-d 0 --photon_noise_iso=400</c> ⇒
        /// **99.46%** 的 16-bit 样本被改动（平均偏差 30875/65535），而 jxlinfo 对两者都报
        /// <c>(possibly) lossless</c>。噪声是解码端按帧头参数合成的，所以文件只大 10 B ——
        /// 用体积根本查不出来，产物却已经不再无损。"勾选无损"与"加合成噪声"是互斥诉求，
        /// 因此这里直接不给选，而不是让用户撞上一条会静默改像素的组合。
        /// </para>
        /// <para>
        /// 命令行侧的同一规则在 <c>CjxlService.BuildCjxlArguments</c>（distance 为 0 时省略该 flag 并点名），
        /// 本方法只负责不让 UI 摆出一个做不到的选项。**不清 <c>IsChecked</c>**：撤掉无损时用户的意图要能回来
        /// （与 <see cref="RestoreLosslessAndQuality"/> 的"只撤自己那把锁"同一取向）。
        /// </para>
        /// </summary>
        private void UpdateCjxlPhotonNoiseAvailability()
        {
            bool lossless = LosslessCheck?.IsChecked == true;
            bool autoIso = CjxlAutoPhotonNoiseCheck?.IsChecked == true;
            if (CjxlAutoPhotonNoiseCheck != null)
                CjxlAutoPhotonNoiseCheck.IsEnabled = !lossless;
            // 自动模式禁用手动 ISO 输入框（既有规则）；无损时连同手动 ISO 一起禁用
            if (CjxlPhotonNoiseBox != null)
                CjxlPhotonNoiseBox.IsEnabled = !lossless && !autoIso;
        }

        /// <summary>
        /// A-10（2026-09-26）：JXL 那两只复选框的**面板可见才生效**判定 —— 唯一采集点与命令预览共用这一处。
        /// <para>
        /// 旧形状的毛病：「jbrd 无损重封装」那只挂在 <c>JxlCjxlPanel</c> 下（只在高级编码 + cjxl 后端可见），
        /// 「保留 Ultra HDR 增益图」那只挂在 <c>JxlCodecPanel</c> 下（只在高级编码 + jxl 格式可见），
        /// 而采集式里前者**任何时候都读**、否决条件才看 <c>useAdvCodec</c> ⇒
        /// 面板收起时这两只的存量值照样决定走不走无损重封装（控件看不见却能改命令，
        /// 正是 <c>IsAnimationMode()</c> 注释点名的忌讳形状），且预览与入队两侧算出的结论不同。
        /// </para>
        /// <para>
        /// 规则（单一、可解释）：**所属面板不可见 ⇒ 该复选框不参与，回落到数据模型的出厂默认**
        /// （<c>FfmpegOptions.JxlLosslessJpeg</c> = <see cref="FfmpegOptions.DefaultJxlLosslessJpeg"/>
        /// 即**默认重封装**、<c>JxlPreserveUltrahdr = true</c> 保留增益图）。
        /// ⚠ 2026-09-30：这里的回落曾写成「不可见 ⇒ false」，而默认勾着的增益图框又在采集式里
        ///   无条件否决重封装 ⇒ 两道叠加让无损重封装在默认配置下**永远不可能生效**。
        /// 可见条件与 <c>UpdateCodecPanelVisibility</c> 的 jxl 分支
        /// 逐条同构：格式 jxl + 勾了高级编码；cjxl 那只还额外要求后端为 cjxl 且非动图模式。
        /// 后端**刻意在函数内自己读** <c>GetCurrentEncoderBackend()</c>（即下拉当前值，与面板可见性同源），
        /// 不吃调用方那份可能被「手动色彩 → 自动切 cjxl」改写过的局部值：那种情形下用户看到的
        /// 仍是 ffmpeg 面板，控件依旧不该参与。
        /// </para>
        /// </summary>
        private (bool LosslessJpeg, bool PreserveUltrahdr) ReadJxlAdvancedCheckStates(
            string fmt, bool useAdvCodec)
        {
            var jxlPanelVisible = fmt is "jxl" && useAdvCodec;
            // jbrd 那只自 2026-09-30 起挂在**共用面板** `JxlLosslessJpegPanel` 上（JPEG 与 JPEG XL
            // 两格都可能出现，同一只控件），所以「是否参与」直接问面板可见性 —— 由
            // `UpdateCodecPanelVisibility` 单点决定，这里再算一份就会与 XAML 那侧漂移。
            // 不可见 ⇒ 回落出厂默认（**默认重封装**），不是回落 false。
            var losslessPanelVisible = JxlLosslessJpegPanel?.IsVisible == true;
            return (
                LosslessJpeg: losslessPanelVisible
                    ? (JxlLosslessJpegCheck?.IsChecked ?? FfmpegOptions.DefaultJxlLosslessJpeg)
                    : FfmpegOptions.DefaultJxlLosslessJpeg,
                PreserveUltrahdr: !jxlPanelVisible || (JxlPreserveUltrahdrCheck?.IsChecked ?? true));
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
                    // 自动模式: 线程数留空 + 不可改 (运行时按并发任务数动态分配)
                    ThreadsBox.Value = null;
                    if (ThreadHintLabel != null)
                        ThreadHintLabel.Text = Services.LocalizationService.Instance["threads.auto.hint"];
                }
                else if (single)
                {
                    ThreadsBox.Value = 1;
                    if (ThreadHintLabel != null)
                        ThreadHintLabel.Text = Services.LocalizationService.Instance["threads.single.hint"];
                }
                else
                {
                    if (ThreadsBox.Value is null or < 1)
                        ThreadsBox.Value = 4;
                    if (ThreadHintLabel != null)
                        ThreadHintLabel.Text = Services.LocalizationService.Instance["threads.manual.hint"];
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
            var completed = _queueItems.Count(i => i.CompletedAt.HasValue || i.IsDeleted);
            var total = _queueItems.Count;
            if (QueueProgressLabel != null)
                QueueProgressLabel.Text = total > 0 ? $"队列: {completed} / {total}" : "队列: 0 / 0";

            // 已用时间：当前处理中任务
            var processing = _queueItems.FirstOrDefault(i => i.IsProcessing && i.StartedAt.HasValue);
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

            // 刷新进行中队列项的实时耗时显示（DurationText 每秒更新）
            foreach (var qi in _queueItems)
            {
                if (qi.StartedAt.HasValue && !qi.CompletedAt.HasValue)
                    qi.RefreshDuration();
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
            // ⚠ 2026-09-20：**格式变化也要刷新「DNG 专属选项」的可见性** ——
            //   该面板要求「RAW 模式 **且** 输出格式 = dng」；模式没变但格式变了同样要重算
            //   （本 handler 原先只调 UpdateOptionAvailability()，**不**触发 UpdatePanels ⇒ 会漏刷）。
            if (DngOptionsPanel != null && ConversionModeCombo != null)
                DngOptionsPanel.IsVisible = ConversionModeCombo.SelectedIndex == 2
                    && NormalizeFormat(FormatCombo?.SelectedItem as string) == "dng";
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
        /// 获取当前模式下的允许文件扩展名。
        /// 静态模式=启用图片扩展名; 动图模式=图片+视频; RAW 模式=RAW 相机文件。
        /// </summary>
        private string[] GetEnabledExtensionsForCurrentMode()
        {
            var mode = ConversionModeCombo?.SelectedIndex ?? 0;
            return mode switch
            {
                1 => AppSettingsService.Current.GetEnabledExtensionsIncludingVideo(),
                2 => RawService.RawExtensions.ToArray(),
                _ => AppSettingsService.Current.GetEnabledExtensions()
            };
        }

        private void ConversionMode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (FormatCombo == null || ConversionModeCombo == null) return;

            var previousFormat = FormatCombo.SelectedItem as string ?? "";
            var mode = ConversionModeCombo.SelectedIndex;
            var isAnimated = mode == 1;
            var isRaw = mode == 2;
            var targetFormats = mode switch
            {
                1 => AnimatedFormats,
                2 => new[] { "DNG" },
                _ => StillFormats
            };

            // 动图模式：控制面板可见性
            if (AnimationPanel != null)
                AnimationPanel.IsVisible = isAnimated;
            // 视频时长控制仅在动图模式下显示
            if (AnimationDurationPanel != null)
                AnimationDurationPanel.IsVisible = isAnimated;
            // RAW 模式专属面板
            if (RawPanel != null)
                RawPanel.IsVisible = isRaw;
            // ⚠⚠ 2026-09-20：**DNG 输出专属选项**只在「RAW 模式 **且** 输出格式 = dng」时显示。
            //   原先它们直接放在 RawPanel 里 ⇒ 选「RAW → jpg/avif」时仍显示
            //   ⇒ 用户会以为压缩/布局/位深/高光设置生效，实际对非 DNG 输出**完全无效**
            //   ⇒ 正是「UI 与真实不符」。
            if (DngOptionsPanel != null)
                DngOptionsPanel.IsVisible = isRaw
                    && NormalizeFormat(FormatCombo?.SelectedItem as string) == "dng";
            // 静态图片/动图通用选项（编码器/质量/色度/位深/高级色彩/无损/.png后缀等）
            // RAW 模式整体隐藏 —— 2026-08-14 UI 审查修复:
            //   原实现逐个 IsEnabled=false 导致 RAW 面板下方残留灰色质量条/无损/高级选项,
            //   用户要求 RAW 面板为独立全新面板而非静态面板上禁用内容
            if (StillOptionsPanel != null)
                StillOptionsPanel.IsVisible = !isRaw;

            // 更新 FormatCombo 选项列表
            // ⚠ CS8602（2026-09-20）：FormatCombo 来自 FindControl，静态分析认为可能为 null
            var fmtItems = FormatCombo?.Items;
            fmtItems?.Clear();
            if (fmtItems != null)
                foreach (var f in targetFormats)
                    fmtItems.Add(f);

            // 尝试保留之前的选中项（如果在新列表中）
            var idx = Array.IndexOf(targetFormats, previousFormat);
            if (FormatCombo != null)   // ⚠ CS8602（2026-09-20）：FindControl 的结果静态分析认为可空
            {
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

            // RAW 模式下压缩/质量由 DNG 面板控制；静态面板已整体隐藏（见上方 StillOptionsPanel）
            // （原逐个 IsEnabled/IsVisible 处理已移除，2026-08-14）

            RegenerateCommand();
        }

        /// <summary>
        /// 当前是否「动图模式」（ConversionModeCombo 索引 1）。
        /// ⚠ P1-E 任务 C（2026-09-19）：动图参数（fps / 循环 / 缩放 / 时长）**只在动图模式下**
        /// 读取，判据的**单一真值**就是本方法 —— 三处 opts 构造（RegenerateCommand /
        /// AddSingleToQueue / BuildCommand_Click）一律调它，禁止各自内联索引比较。
        /// 为什么必须收口：非动图模式下 AnimationPanel 是 IsVisible=false（见
        /// <see cref="ConversionMode_SelectionChanged"/>），控件里的残留文本用户看不见、
        /// 却能改变命令（静态 WebP 带上 fps= 滤镜；静态 JXL 因 AnimationFps.HasValue 被换成
        /// libjxl_anim）—— 属本仓最忌讳的"看起来配置过"。
        /// </summary>
        private bool IsAnimationMode() => (ConversionModeCombo?.SelectedIndex ?? 0) == 1;

        /// <summary>DNG 压缩方式切换: 显示/隐藏 JXL 质量与高级面板</summary>
        private void DngCompression_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var isJxl = DngCompressionCombo?.SelectedIndex == 1;
            if (DngJxlQualityPanel != null)
                DngJxlQualityPanel.IsVisible = isJxl;
            if (DngJxlAdvancedPanel != null)
                DngJxlAdvancedPanel.IsVisible = isJxl;
            RegenerateCommand();
        }

        private async Task RefreshEncoderListAsync()
        {
            if (EncoderCombo == null || FormatCombo == null) return;

            var fmt1 = NormalizeFormat(FormatCombo.SelectedItem as string);
            var isAnimMode = ConversionModeCombo?.SelectedIndex == 1;

            var encoders = await EncoderDetectionService.GetEncodersForFormatAsync(fmt1);

            // 可用性诚实原则：只向用户提供「当前环境真能兑现」的编码器，过滤掉不可用项
            // （如无对应 GPU 硬件的 nvenc/qsv、未检测到的外部工具），避免用户选中后运行时才失败
            // 或被静默回退到别的编码器 → 造成「实际编码器 ≠ 用户所选」。
            encoders = encoders.Where(e => e.IsAvailable).ToList();

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
        /// <summary>用户是否手动指定了色彩空间（简单模式 ColorSpace 或高级模式精确参数）</summary>
        private bool IsColorManuallySpecified()
        {
            // 简单模式: ColorSpace != auto
            var cs = ColorSpaceCombo?.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(cs)
                && !cs.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return true;
            // 高级模式: 勾选且**精确参数**非 auto
            // ⚠ 2026-10-04：`auto`（首项，= 不指定）**不算**"手动指定" —— 否则勾选后即便没动下拉
            //   也会被当成"用户指定了色彩"，正是"勾选/不勾选不应有策略差别"要消除的形状。
            if (UseAdvancedColor?.IsChecked == true
                && AutoToNull(ColorPrimariesCombo?.SelectedItem as string) != null)
                return true;
            return false;
        }

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
                // ⚠ 2026-09-22：**必须与 `UpdateExifToolPanelState()` 同一表达式**（`&& ExifToolService.IsAvailable`）——
                //   否则本处会把那边的 exiftool 条件**覆盖**掉，导致「缺 exiftool 仍可选『删除全部』」的旧缺陷复活。
                if (MetadataModeCombo != null)
                    MetadataModeCombo.IsEnabled = _currentCapabilities.SupportsMetadata && ExifToolService.IsAvailable;
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
                    ColorSpaceCombo.Items.Add("auto");
                    ColorSpaceCombo.Items.Add("sRGB");
                    ColorSpaceCombo.Items.Add("BT.709");
                    // Display P3: 广色域 SDR (Apple 生态标准), 所有格式均支持 (ICC/CICP)
                    ColorSpaceCombo.Items.Add("Display P3");
                    var hasHdr = _currentCapabilities.SupportedColorSpaces.Contains("BT.2020");
                    // JPEG 特例：HDR 无法由 JPEG 自身标定，但**开启 GainMap (Ultra HDR) 后可以**
                    // （SDR 底 + 增益图，在支持的设备上显示 HDR）⇒ HDR 目标项随开关出现/消失，
                    // 而不是让用户选了再被降级。
                    bool gainMapOn = (FormatCombo?.SelectedItem as string)?.ToLowerInvariant() is "jpg" or "jpeg"
                                     && HdrModeCombo?.SelectedIndex == 1;
                    if (gainMapOn) hasHdr = true;
                    if (hasHdr)
                    {
                        // P3 PQ: P3 色域 + PQ 曲线 (HDR 场景)
                        ColorSpaceCombo.Items.Add("P3 PQ");
                        ColorSpaceCombo.Items.Add("BT.2020 PQ");
                        ColorSpaceCombo.Items.Add("BT.2020 HLG");
                    }
                    ColorSpaceCombo.IsEnabled = _currentCapabilities.SupportedColorSpaces.Count > 0;
                    if (ColorSpaceCombo.Items.Count > 0) ColorSpaceCombo.SelectedIndex = 0;

                    // 更新 CICP 格式兼容性提示
                    UpdateCicpHint();
                }

                // TV/PC 范围: 仅 YUV 输出格式支持（AVIF）；不支持的格式整组隐藏（2026-08-16）
                var colorRangeSupported = _currentCapabilities.SupportsColorRange;
                if (ColorRangeCombo != null) ColorRangeCombo.IsVisible = colorRangeSupported;
                if (ColorRangeLabel != null) ColorRangeLabel.IsVisible = colorRangeSupported;
                if (!colorRangeSupported && ColorRangeCombo != null && ColorRangeCombo.SelectedIndex != 0)
                {
                    // 切换到不支持范围的格式时回到 auto，避免残留无效选择
                    ColorRangeCombo.SelectedIndex = 0;
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
            UpdateChromaBitDepthOptions();
            RegenerateCommand();
        }

        /// <summary>
        /// 按当前编码器调整色度/位深下拉的可用选项（2026-08-16 实测驱动）：
        /// - libsvtav1 / av1_qsv / av1_amf 仅支持 4:2:0、8/10-bit（ffmpeg 实测 pix_fmts）
        /// - libwebp 仅支持 4:2:0（yuv420p/yuva420p/bgra）
        /// - libaom-av1 / av1_nvenc 支持 4:4:4/4:2:2/4:2:0、8/10/12-bit
        /// 保留用户当前选择；不可用的选择自动回退 auto。
        /// </summary>
        private void UpdateChromaBitDepthOptions()
        {
            var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            // ⚠ P1-F 缺陷 1（显示侧）：这里原先读 **EncoderCombo 的显示串**（含硬件编码器的图标前缀
            //   "⚡ "）⇒ `StartsWith("av1_nvenc")` 对 "⚡ av1_nvenc — …" 恒假 ⇒ 按编码器过滤
            //   色度/位深选项的那几支永远不生效（av1_nvenc 会拿到 libaom 的选项集）。
            //   改用 GetCurrentEncoderName()（= EncoderInfo.ParseEncoderName，已剥图标）。
            var enc = GetCurrentEncoderName();
            var isSvt = enc.StartsWith("libsvt", StringComparison.OrdinalIgnoreCase);
            var isQsv = enc.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase);
            var isAmf = enc.StartsWith("av1_amf", StringComparison.OrdinalIgnoreCase);

            // ── 色度子采样 ──
            if (ChromaCombo != null)
            {
                var only420 = fmt == "webp" || (fmt == "avif" && (isSvt || isQsv || isAmf));
                var cur = ChromaCombo.SelectedItem as string;
                ChromaCombo.Items.Clear();
                ChromaCombo.Items.Add("auto");
                if (!only420)
                {
                    ChromaCombo.Items.Add("4:4:4");
                    ChromaCombo.Items.Add("4:2:2");
                }
                ChromaCombo.Items.Add("4:2:0");
                if (cur != null && ChromaCombo.Items.Contains(cur))
                    ChromaCombo.SelectedIndex = ChromaCombo.Items.IndexOf(cur);
                else
                    ChromaCombo.SelectedIndex = 0; // auto
            }

            // ── 位深（仅 AVIF 需要按编码器过滤；其他格式由 UpdateOptionAvailability 的 SupportedBitDepths 控制）──
            if (BitDepthCombo != null && fmt == "avif")
            {
                var cur = BitDepthCombo.SelectedItem as string;
                // 上限与**钳制实际用的那一条规则**同源（任务 #36）：这里原先自带一份
                // `(isLibaom || isNvenc) ? 12 : 10`，且**不含**"编码器留空"这一支 ⇒
                // 留空时下拉只给到 10，而命令里不写 -c:v ⇒ 实跑 libaom-av1、能出 12bit。
                var maxBd = Services.ColorMapping.ImageEncoderArgs.AvifMaxBitDepthForEncoder(enc);
                BitDepthCombo.Items.Clear();
                BitDepthCombo.Items.Add("auto");
                for (int bd = 8; bd <= maxBd; bd += 2)
                    BitDepthCombo.Items.Add(bd.ToString());
                if (cur != null && BitDepthCombo.Items.Contains(cur))
                    BitDepthCombo.SelectedIndex = BitDepthCombo.Items.IndexOf(cur);
                else
                    BitDepthCombo.SelectedIndex = 0; // auto
            }
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
                    ThreadHintLabel.Text = Services.LocalizationService.Instance["threads.encoder.single"];
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
            // JPEG→JXL 无损重封装那只：JPEG 与 JPEG XL 两格共用，先全隐，再由各自 case 打开
            if (JxlLosslessJpegPanel != null) JxlLosslessJpegPanel.IsVisible = false;
            if (JpegCodecPanel != null) JpegCodecPanel.IsVisible = false;
            if (JpegliCodecPanel != null) JpegliCodecPanel.IsVisible = false;
            if (JpegGainMapPanel != null) JpegGainMapPanel.IsVisible = false;
            if (HdrModeCombo != null) HdrModeCombo.IsVisible = fmt is "jpg" or "jpeg" or "avif";
            if (TiffCodecPanel != null) TiffCodecPanel.IsVisible = false;
            if (JxrCodecPanel != null) JxrCodecPanel.IsVisible = false;
            if (ThumbnailPanel != null) ThumbnailPanel.IsVisible = false;

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
                    // 有损 WebP 不显示无损压缩级别（2026-08-16 修复: 此前有损模式仍显示该控件误导用户）
                    else if (WebpLosslessPanel != null)
                        WebpLosslessPanel.IsVisible = LosslessCheck?.IsChecked != false;
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
                    // 面板每次按格式/后端重算可见性时同步重算光子噪声可用性：
                    // LockLosslessForJxl 那类**程序化**置勾在已是 true 时不会再触发
                    // IsCheckedChanged，只挂处理器会漏掉这一格。
                    UpdateCjxlPhotonNoiseAvailability();
                    // jbrd 那只：只有 cjxl 后端 + 非动图才可能兑现（ffmpeg 的 libjxl 出口实测**不写**
                    // jbrd 盒，见 FfmpegCommandBuilder 的 2026-09-19 更正；cjxl 也不支持动画）
                    if (JxlLosslessJpegPanel != null)
                        JxlLosslessJpegPanel.IsVisible = !isAnimMode && backend == EncoderBackend.Cjxl;
                    break;

                case "jpg": case "jpeg":
                    // jpg/jpeg 格式：根据编码器后端显示不同高级面板
                    if (backend == EncoderBackend.Cjpegli)
                    {
                        if (JpegliCodecPanel != null) JpegliCodecPanel.IsVisible = true;
                    }
                    else
                    {
                        if (JpegCodecPanel != null) JpegCodecPanel.IsVisible = true;
                    }
                    // 用户要求（2026-09-30）：JPEG 这一格也要能看到「JPEG→JPEG XL 无损快速重封装」
                    // 这只开关 —— 与 JPEG XL 那格是**同一只控件**（见 MainWindow.xaml 的
                    // JxlLosslessJpegPanel），所以不存在两份真值；它实际生效的条件仍是
                    // 「目标格式 jxl + 输入是 JPEG」（采集式里的 fmt/IsJpegInput 两道）。
                    if (JxlLosslessJpegPanel != null) JxlLosslessJpegPanel.IsVisible = true;
                    // Gain Map (纯 C# GainMapEncoder) 是任意 JPEG 编码器的附加选项:
                    // 选择 BT.2020 HDR 色彩空间 (PQ/HLG) 时显示, 与编码器后端无关
                    // SDR 内容 (sRGB/BT.709) headroom=1 无增益意义, 不显示
                    if (JpegGainMapPanel != null)
                        JpegGainMapPanel.IsVisible = HdrModeCombo?.SelectedIndex == 1
                                            && (FormatCombo?.SelectedItem as string)?.ToLowerInvariant() is "jpg" or "jpeg";
                    break;
                case "tiff": if (TiffCodecPanel != null) TiffCodecPanel.IsVisible = true; break;
                case "jxr": if (JxrCodecPanel != null) JxrCodecPanel.IsVisible = true; break;
            }

            // Thumbnail panel is capability driven: the container key goes through the same
            // normalization ThumbnailService uses when it writes (or refuses) the embed.
            // Unsupported containers hide the whole panel AND reset the checkbox, so a value that
            // can never be honored does not stay behind an invisible control.
            var thumbSupported = FormatCapabilitiesService.GetCapabilities(ThumbnailService.CapabilityKey(fmt))
                is { SupportsThumbnail: true };
            if (ThumbnailPanel != null) ThumbnailPanel.IsVisible = thumbSupported;
            if (EnableThumbnailCheck != null)
            {
                EnableThumbnailCheck.IsEnabled = thumbSupported;
                if (!thumbSupported) EnableThumbnailCheck.IsChecked = false;
            }
            if (ThumbnailSizeBox != null) ThumbnailSizeBox.IsEnabled = thumbSupported;
            if (ThumbnailQualityBox != null) ThumbnailQualityBox.IsEnabled = thumbSupported;
        }

        /// <summary>
        /// 根据当前 AVIF 编码器切换显示不同的高级选项面板
        /// </summary>
        private void UpdateAvifEncoderPanel()
        {
            // ⚠ P1-F 缺陷 1（显示侧）：同 UpdateChromaBitDepthOptions —— 原先读显示串，
            //   硬件编码器的 "⚡ " 前缀让 `StartsWith("av1_nvenc")` 恒假 ⇒ 面板回退到兜底的
            //   HwAvifPanel，用户看到的是**别人的**参数面板。改用已剥图标的 GetCurrentEncoderName()。
            var enc = GetCurrentEncoderName();
            var isLibaom = enc.StartsWith("libaom", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(enc);
            var isSvt = enc.StartsWith("libsvt", StringComparison.OrdinalIgnoreCase);
            var isNvenc = enc.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase);
            var isQsv = enc.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase);
            var isVaapi = enc.StartsWith("av1_vaapi", StringComparison.OrdinalIgnoreCase);
            var isAmf = enc.StartsWith("av1_amf", StringComparison.OrdinalIgnoreCase);

            // 隐藏所有子面板
            if (LibaomAvifPanel != null) LibaomAvifPanel.IsVisible = false;
            if (SvtAvifPanel != null) SvtAvifPanel.IsVisible = false;
            if (HwAvifPanel != null) HwAvifPanel.IsVisible = false;
            if (NvencAvifPanel != null) NvencAvifPanel.IsVisible = false;
            if (QsvAvifPanel != null) QsvAvifPanel.IsVisible = false;
            if (VaapiAvifPanel != null) VaapiAvifPanel.IsVisible = false;
            if (AmfAvifPanel != null) AmfAvifPanel.IsVisible = false;

            // 根据编码器显示对应面板
            if (isLibaom)
                { if (LibaomAvifPanel != null) LibaomAvifPanel.IsVisible = true; }
            else if (isSvt)
                { if (SvtAvifPanel != null) SvtAvifPanel.IsVisible = true; }
            else if (isNvenc)
                { if (NvencAvifPanel != null) NvencAvifPanel.IsVisible = true; }
            else if (isQsv)
                { if (QsvAvifPanel != null) QsvAvifPanel.IsVisible = true; }
            else if (isVaapi)
                { if (VaapiAvifPanel != null) VaapiAvifPanel.IsVisible = true; }
            else if (isAmf)
                { if (AmfAvifPanel != null) AmfAvifPanel.IsVisible = true; }
            else
                { if (HwAvifPanel != null) HwAvifPanel.IsVisible = true; } // 兜底
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
        /// 从 libaom AqModeCombo 解析真正的 aq-mode 值
        /// 0=默认(不传), 1=variance, 2=complexity
        /// </summary>
        private string? ParseAvifAqMode()
        {
            var idx = AvifAqModeCombo?.SelectedIndex ?? 0;
            return idx switch
            {
                1 => "variance",
                2 => "complexity",
                _ => null  // 0=默认，不传参数
            };
        }

        /// <summary>
        /// 从当前选中的硬件编码器预设下拉框获取预设级别 (1-7)
        /// </summary>
        private int GetAvifHwPresetLevel()
        {
            // ⚠ P1-F 缺陷 1（显示侧）：同 UpdateAvifEncoderPanel —— 这四支判据的编码器名
            //   （av1_nvenc/qsv/vaapi/amf）**全部**是 GPU 编码器 ⇒ 显示串恒带 "⚡ " 前缀
            //   ⇒ `StartsWith("av1_nvenc")` 恒假 ⇒ 每次都落到「旧面板兜底」读**隐藏的**
            //   HwPresetCombo，用户在自己那块面板上选的预设被静默替换。改用 GetCurrentEncoderName()。
            var enc = GetCurrentEncoderName();
            if (enc.StartsWith("av1_nvenc", StringComparison.OrdinalIgnoreCase))
                return (NvencPresetCombo?.SelectedIndex ?? 3) + 1;
            if (enc.StartsWith("av1_qsv", StringComparison.OrdinalIgnoreCase))
                return (QsvPresetCombo?.SelectedIndex ?? 3) + 1;
            if (enc.StartsWith("av1_vaapi", StringComparison.OrdinalIgnoreCase))
                return (VaapiPresetCombo?.SelectedIndex ?? 3) + 1;
            if (enc.StartsWith("av1_amf", StringComparison.OrdinalIgnoreCase))
                return (AmfPresetCombo?.SelectedIndex ?? 1) + 1;  // AMF only 3 options
            // 旧面板兜底
            var oldIdx = HwPresetCombo?.SelectedIndex ?? 1;
            return oldIdx + 1; // 0→1, 1→2, 2→3 (old mapping)
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

        // ── ICC 色彩管理 — 从 UI 读取选项 ──

        private Models.IccMode GetIccMode()
        {
            if (IccModeCarry?.IsChecked == true) return Models.IccMode.CarryIcc;
            if (IccModeBake?.IsChecked == true) return Models.IccMode.BakeToStandard;
            if (IccModeBakeOnly?.IsChecked == true) return Models.IccMode.BakeOnly;
            return Models.IccMode.None;
        }

        /// <summary>4 个策略单选→ColorStrategy（None→推荐, Carry→携带, Bake→仅CICP, BakeOnly→手动）。</summary>
        private Models.ColorStrategy GetColorStrategy()
        {
            if (IccModeCarry?.IsChecked == true) return Models.ColorStrategy.CarryIcc;
            if (IccModeBake?.IsChecked == true) return Models.ColorStrategy.BakeCicpOnly;
            if (IccModeBakeOnly?.IsChecked == true) return Models.ColorStrategy.Manual;
            return Models.ColorStrategy.Recommended;
        }

        /// <summary>
        /// 把 4 个策略单选设成指定策略（Recommended 对应 <c>IccModeNone</c> 单选）。
        /// 载入预设时用：单选由 GroupName 自动互斥，故只需勾选目标那一个（勾选会触发
        /// IccMode_Changed → MarkIccModeTouched/UpdateIccPanelVisibility/RegenerateCommand）。
        /// </summary>
        private void SetColorStrategyRadio(Models.ColorStrategy strategy)
        {
            switch (strategy)
            {
                case Models.ColorStrategy.CarryIcc:
                    if (IccModeCarry != null) IccModeCarry.IsChecked = true;
                    break;
                case Models.ColorStrategy.BakeCicpOnly:
                    if (IccModeBake != null) IccModeBake.IsChecked = true;
                    break;
                case Models.ColorStrategy.Manual:
                    if (IccModeBakeOnly != null) IccModeBakeOnly.IsChecked = true;
                    break;
                default:   // Recommended（枚举定义上即未知值的兜底）
                    if (IccModeNone != null) IccModeNone.IsChecked = true;
                    break;
            }
        }

        /// <summary>
        /// 色域映射（GMO）开关 → <c>FfmpegOptions.ColorGamutMap</c>（off 默认 | on）。
        /// 控件缺失/未勾选一律 off ⇒ 与历史行为（钳位硬裁切）逐字节相同，默认行为不因新增控件而改变。
        /// </summary>
        private string GetColorGamutMap() => ColorGamutMapCheck?.IsChecked == true ? "on" : "off";

        /// <summary>勾选变化：刷新命令预览（选项经 GetColorGamutMap 进入 FfmpegOptions）。</summary>
        private void ColorGamutMap_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
            => RegenerateCommand();

        private string? GetIccSourceSpace()
        {
            if (IccSourceSpaceCombo?.SelectedIndex > 0)
                return IccSourceSpaceCombo?.SelectedItem as string;
            return null; // auto
        }

        private string GetIccTargetSpace()
        {
            // 烘焙目标 = 快速色彩空间所选（支持 Display P3 / P3 PQ / BT.2020 PQ/HLG，不再降级为 SDR）。
            var cs = ColorSpaceCombo?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(cs) || cs == "auto")
                return "sRGB";
            return cs;
        }

        /// <summary>
        /// 根据 exiftool 是否可用，显示/隐藏 ExifTool 面板，
        /// 并根据当前元数据模式（保留/删除全部）启用/禁用其选项
        /// </summary>
        private void UpdateExifToolPanelState()
        {
            var exifAvailable = ExifToolService.IsAvailable;
            var isPreserveMode = GetMetadataMode() == Models.MetadataMode.PreserveAll;
            var loc = Services.LocalizationService.Instance;

            // ⚠⚠ 2026-09-22（管线审查 §3.5 修复）：**exiftool 缺失时不得把面板整个藏起来**。
            //   旧行为 = `ExifToolPanel.IsVisible = exifAvailable` + 提示语**只在可用时才设置**
            //   ⇒ 用户**得不到任何解释**；而「元数据模式」下拉当时**仍可选中「删除全部」**
            //   ⇒ 剥离**静默失效**：ffmpeg 侧仍用 `-map_metadata 0` 把源元数据整体复制，
            //     产物**原样带出 GPS**（已实机复现：缺 exiftool 时产物 GPS 与源逐字相同）。
            //   现行为：面板**保持可见**、**所有相关选项禁用**、**始终**用显式提示说明不可用。
            if (ExifToolPanel != null)
                ExifToolPanel.IsVisible = true;

            if (ExifToolHint != null)
                ExifToolHint.Text = !exifAvailable
                    ? loc["exiftool.missing"]
                    : (isPreserveMode ? loc["exiftool.detected"] : loc["exiftool.mode.stripall"]);

            // 元数据模式下拉：**格式能力** ∧ **exiftool 可用**（缺一不可）。
            // ⚠ 不可用时**强制回「保留」**（安全侧）—— 否则「删除全部」会留在选中态而实际不生效。
            if (MetadataModeCombo != null)
            {
                // ⚠ 能力对象可空（`FormatCapabilities?`）⇒ 未知时**不触碰** `IsEnabled`（与既有 `UpdateOptionAvailability` 同口径）。
                if (_currentCapabilities != null)
                    MetadataModeCombo.IsEnabled = exifAvailable && _currentCapabilities.SupportsMetadata;
                if (!exifAvailable && MetadataModeCombo.SelectedIndex != 0)
                    MetadataModeCombo.SelectedIndex = 0;
            }

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

            var mode = ConversionModeCombo?.SelectedIndex ?? 0;
            var isAnimationMode = mode == 1;
            var isRawMode = mode == 2;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = isAnimationMode ? "选择图片或视频文件" : isRawMode ? "选择 RAW 相机文件" : "选择图片文件",
                AllowMultiple = false,
                FileTypeFilter = isAnimationMode
                    ? AppSettingsService.Current.GetAnimationFilePickerFilter()
                    : isRawMode
                        ? new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType("RAW 相机文件")
                            {
                                Patterns = RawService.RawExtensions.Select(ext => "*" + ext).ToArray()
                            },
                            new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                        }
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
                if (MediaInfoText != null) MediaInfoText.Text = Services.LocalizationService.Instance["media.loading"];
                var info = await MediaInfoService.GetMediaInfoAsync(_inputPath);
                if (MediaInfoText != null) MediaInfoText.Text = info;
            }
        }

        private async void AddToQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // 如果有文件夹扫描结果，批量导入
            if (_selectedFiles.Count > 0)
            {
                foreach (var file in _selectedFiles)
                {
                    _selectedFileBaseDirs.TryGetValue(file, out var baseDir);
                    await AddSingleToQueue(file, baseDir);
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
                    await AddSingleToQueue(file, baseDir);
                }
                if (LogText != null) LogText.Text += $"已从列表批量添加 {_mediaFiles.Count} 个文件到队列\n";
                return;
            }

            if (string.IsNullOrWhiteSpace(_inputPath))
            {
                if (LogText != null) LogText.Text += "请先选择文件，再添加到队列\n";
                return;
            }

            await AddSingleToQueue(_inputPath);
        }

        /// <summary>
        /// 添加单个文件到队列。返回 true 表示成功添加。
        /// （2026-08-16 异步化：JXL 无损重封装需 await ffmpeg 能力检测，与预览一致）
        /// </summary>
        /// <summary>
        /// 唯一采集点：把高级模式的当前控件状态读成一份 FfmpegOptions。入队、命令预览、
        /// 简洁模式共用它 —— 任何一条路径都不再自己读控件（此前有 4 份各自独立的采集）。
        /// 返回 null = 该输入在当前转换模式下不合法（调用方跳过）。
        /// </summary>
        private async Task<Models.FfmpegOptions?> CollectOptionsFromUiAsync(string inputPath, bool logNotices = true)
        {
            // RAW 模式校验: 仅 RAW/DNG 文件可作为输入（DNG 编码需传感器数据）
            // 提前提示而非等队列执行失败 (2026-08-14 UI 审查修复)
            if ((ConversionModeCombo?.SelectedIndex ?? 0) == 2
                && !RawService.IsRawFile(inputPath))
            {
                if (logNotices && LogText != null)
                    LogText.Text += $"⚠️ 跳过: {Path.GetFileName(inputPath)} 不是 RAW/DNG 文件（RAW 模式仅支持相机原始文件）\n";
                return null;
            }

            // 注：队列本身无容量上限，"并行编码任务数"仅控制同时运行的任务数
            var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            var chroma = ChromaCombo?.SelectedItem as string ?? "auto";
            var bitdepthStr = BitDepthCombo?.SelectedItem as string ?? "auto";
            int? bitdepth = null;
            if (!bitdepthStr.Equals("auto", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(bitdepthStr, out var bd))
                bitdepth = bd;
            var encoderStr = EncoderCombo?.SelectedItem as string ?? "";
            var encoderName = EncoderInfo.ParseEncoderName(encoderStr);
            var encoderBackend = EncoderInfo.ParseBackend(encoderStr);

            // JXL + 手动色彩空间 → 自动切 cjxl（ffmpeg libjxl 忽略 -color_primaries/-color_trc）
            if (fmt is "jxl" && encoderBackend != EncoderBackend.Cjxl
                && CjxlService.IsAvailable && IsColorManuallySpecified())
            {
                encoderBackend = EncoderBackend.Cjxl;
                encoderName = "cjxl";
                if (logNotices && LogText != null)
                    LogText.Text += "[jxl] 手动色彩空间 → 自动切换 cjxl 编码器\n";
            }
            else if (fmt is "jxl" && encoderBackend != EncoderBackend.Cjxl
                && !CjxlService.IsAvailable && IsColorManuallySpecified())
            {
                // 可用性诚实：cjxl 不可用时 libjxl 会忽略手动色彩参数 → 明确告警，避免静默丢色。
                if (logNotices && LogText != null)
                    LogText.Text += "⚠️ [jxl] 手动色彩空间需 cjxl 兑现，但未检测到 cjxl；ffmpeg libjxl 会忽略色彩参数，手动色彩可能丢失。请安装 cjxl 或改用 auto 色彩。\n";
            }

            var autoThreads = AutoThreadsCheck?.IsChecked ?? true;
            var singleThread = SingleThreadCheck?.IsChecked ?? false;
            int threads = singleThread ? 1
                : autoThreads ? Models.FfmpegOptions.ComputeAutoThreads()
                : (int)(ThreadsBox?.Value ?? 4);
            // 2026-08-15 自适应: 自动模式标记 AutoThreads, 运行时按并发任务数
            // 动态重算 (每任务线程 = max(1, 核数/并发数), 任务合计吃满核)。
            // Threads 存基准值仅供命令预览, 实际执行由 QueueProcessor 覆盖。
            var adaptiveAuto = autoThreads && !singleThread;

            var useAdv = UseAdvancedColor?.IsChecked ?? false;
            var useAdvCodec = UseAdvancedCodec?.IsChecked ?? false;
            // Thumbnail (EXIF IFD1 embed): only collected while the advanced codec panel is open.
            var thumbOn = useAdvCodec && (EnableThumbnailCheck?.IsChecked ?? false);
            // A-10：两只 JXL 复选框按「所属面板可见才参与」取值，采集与命令预览共用同一个判定函数。
            var jxlChecks = ReadJxlAdvancedCheckStates(fmt, useAdvCodec);
            var options = new FfmpegOptions
            {
                Format = fmt,
                Quality = (int)(QualitySlider?.Value ?? 75),
                Chroma = chroma,
                BitDepth = bitdepth,
                ColorSpace = ColorSpaceCombo?.SelectedItem as string,
                // RAW 输出色域模式：与 `ColorGamutMap` 同口径**无条件读**（不挂在 useAdv 上）——
                // 它是"RAW 默认档如何取"这根独立轴的取值；默认 rec2020 与历史行为逐字节相同。
                RawColorTarget = RawColorTargetCombo?.SelectedItem as string,
                // 高级色彩参数（2026-08-15 修复: 此前入队路径遗漏导致仅预览生效）
                UseAdvancedColorParameters = useAdv,   // ⚠ 仅 UI 状态；语义判据一律走 HasExplicitColorParams
                // ⚠⚠ 2026-10-04 设计裁定：勾选与不勾选**不得**造成策略差别。三个下拉的首项 `auto`
                //   在此映射为 **null** ⇒「勾选但没动下拉」提交的值与「不勾选」完全相同（都无精确参数）。
                //   模式 1 允许 auto（走自动默认）；模式 2/3/4 必须手动指定（见 UpdateAdvancedColorValidation）。
                ColorPrimaries = useAdv ? AutoToNull(ColorPrimariesCombo?.SelectedItem as string) : null,
                ColorTrc = useAdv ? AutoToNull(ColorTrcCombo?.SelectedItem as string) : null,
                ColorMatrix = useAdv ? AutoToNull(ColorMatrixCombo?.SelectedItem as string) : null,
                ColorRange = useAdv ? GetColorRangeValue() : null,
                TonemapCurve = null, // tonemap 曲线内部固定 hable
                Encoder = encoderName, EncoderBackend = encoderBackend,
                Threads = threads,
                AutoThreads = adaptiveAuto,
                MetadataMode = GetMetadataMode(),
                Lossless = LosslessCheck?.IsChecked ?? false,
                PngPred = useAdvCodec && PngPredCombo?.SelectedIndex >= 0
                    ? _pngPredValues[Math.Min(PngPredCombo.SelectedIndex, _pngPredValues.Length - 1)]
                    : null,
                PngDpi = useAdvCodec && PngDpiBox?.Value > 0 ? (int?)PngDpiBox.Value : null,
                WebpPreset = useAdvCodec ? (WebpPresetCombo?.SelectedItem as string) : "picture",
                WebpCompressionLevel = useAdvCodec ? (int?)WebpCompressionBox?.Value : 4,
                AvifCpuUsed = useAdvCodec ? (int?)AvifCpuUsedBox?.Value : 4,
                AvifStillPicture = useAdvCodec ? AvifStillPictureCheck?.IsChecked : true,
                AvifRowMt = useAdvCodec ? AvifRowMtCheck?.IsChecked : true,
                AvifTune = useAdvCodec ? (AvifTuneCombo?.SelectedItem as string) : null,
                AvifPreset = GetAvifPresetValue(),
                AvifSvtPreset = useAdvCodec ? (int?)SvtPresetBox?.Value : 6,
                AvifSvtTune = useAdvCodec ? (SvtTuneCombo?.SelectedItem as string) : "VMAF (主观)",
                AvifHwPreset = useAdvCodec ? (HwPresetCombo?.SelectedItem as string) : null,
                AvifHwPresetLevel = useAdvCodec ? GetAvifHwPresetLevel() : 7,
                AvifAqMode = useAdvCodec ? ParseAvifAqMode() : "variance",
                AvifEnableCdef = useAdvCodec ? AvifCdefCheck?.IsChecked : true,
                AvifEnableIntrabc = useAdvCodec ? AvifIntrabcCheck?.IsChecked : true,
                AvifDenoiseLevel = useAdvCodec && AvifDenoiseBox?.Value > 0 ? (int?)AvifDenoiseBox.Value : null,
                // A-5：NVENC aq-strength 的官方区间是 [1,15]（实测 -h encoder=av1_nvenc 打印
                // from 1 to 15、default 8），传 0 是硬失败而非「关闭 AQ」（实测 out of range、退出码 127）
                // ⇒ 控件下限已改为 1；这里再钳一次，兜住「旧预设 JSON 里存着 0」那条回放路径。
                AvifNvencAqStrength = useAdvCodec
                    ? Math.Clamp((int?)AvifNvencAqBox?.Value ?? 8, 1, 15)
                    : 8,
                AvifNvencSpatialAq = useAdvCodec ? AvifNvencSpatialAqCheck?.IsChecked : true,
                AvifLowPower = useAdvCodec ? (AvifQsvLowPowerCheck?.IsChecked ?? AvifVaapiLowPowerCheck?.IsChecked) : false,
                // JXL effort: cjxl 后端用 cjxl 面板的 effort，ffmpeg 后端用 ffmpeg 面板的 effort
                // （2026-08-16 修复: 此前 CjxlEffortBox 仅预览生效，入队恒用 JxlEffortBox 默认值）
                JxlEffort = useAdvCodec
                    ? (encoderBackend == EncoderBackend.Cjxl
                        ? (int?)CjxlEffortBox?.Value ?? 7
                        : (int?)JxlEffortBox?.Value ?? 7)
                    : 7,
                JxlModular = useAdvCodec ? JxlModularCheck?.IsChecked : null,
                // A-10：jbrd 无损重封装只在「该复选框可见」时由用户决定（不可见 ⇒ 回落模型默认）。
                // 可见已蕴含后端是 cjxl ⇒ 原先那层 ffmpeg libjxl 能力检测不再需要（且实测该出口
                // 根本不写 jbrd box，见 FfmpegCommandBuilder 的 2026-09-19 更正）；
                // 否决条件也改读同一个判定结果，两侧不会再算出不同结论。
                // ⚠ 2026-09-30：增益图那只否决框**默认是勾着的**，旧写法无条件 `&& !PreserveUltrahdr`
                //   ⇒ 对**任何** JPEG 输入都把无损重封装永久否决了（这是"开关开着却没生效"的第一因）。
                //   两者真正互斥的只有「输入确实是 Ultra HDR JPEG 且用户要保留增益图」这一格，
                //   所以否决条件加上 Ultra HDR 实测判定；普通 JPEG 从此默认走重封装。
                JxlLosslessJpeg = fmt is "jxl" && IsJpegInput(inputPath)
                    && jxlChecks.LosslessJpeg
                    && !(jxlChecks.PreserveUltrahdr && await GainMapDecoder.IsUltraHdrManagedAsync(inputPath)),
                JxlPreserveUltrahdr = jxlChecks.PreserveUltrahdr,
                // ── cjxl 高级选项（2026-08-16 修复: 此前入队路径缺失导致仅预览生效）──
                CjxlProgressive = useAdvCodec ? (CjxlProgressiveCheck?.IsChecked ?? false) : false,
                CjxlPhotonNoiseIso = useAdvCodec && (CjxlAutoPhotonNoiseCheck?.IsChecked ?? false)
                    ? 0 : (useAdvCodec ? (int)(CjxlPhotonNoiseBox?.Value ?? 0) : 0),
                CjxlAutoPhotonNoise = useAdvCodec && (CjxlAutoPhotonNoiseCheck?.IsChecked ?? false),
                JpegHuffman = useAdvCodec ? (JpegHuffmanCombo?.SelectedItem as string) : "optimal",
                JpegDct = useAdvCodec ? (JpegDctCombo?.SelectedItem as string is "auto" ? null : JpegDctCombo?.SelectedItem as string) : null,
                JpegProgressiveId = useAdvCodec ? ParseJpegProgressiveId() : 0,
                JpegGainMap = HdrModeCombo?.SelectedIndex == 1,
                JpegGainMapQuality = ParseGainMapQuality(),
                JpegGainMapTargetNits = ParseGainMapNits(),
                // (libultrahdr hdr-cf 字段已移除)
                JpegGainMapDownsample = ParseGainMapDownsample(),
                JpegGainMapMultiChannel = ParseGainMapMultiChannel(),
                JpegGainMapBaseGamut = ParseGainMapBaseGamut(),
                TiffCompressionAlgo = useAdvCodec ? (TiffCompressionCombo?.SelectedItem as string) : "lzw",
                TiffDpi = useAdvCodec && TiffDpiBox?.Value > 0 ? (int?)TiffDpiBox.Value : null,
                // -- thumbnail (EXIF IFD1 embed), 160 / 75 are the FfmpegOptions defaults --
                EnableThumbnail = thumbOn,
                ThumbnailLongEdge = thumbOn ? (int)(ThumbnailSizeBox?.Value ?? 160) : 160,
                ThumbnailQuality = thumbOn ? (int)(ThumbnailQualityBox?.Value ?? 75) : 75,
                // ── cjpegli 高级选项（2026-08-16 修复: 此前入队路径完全缺失导致全部失效）──
                CjpegliChromaSubsampling = useAdvCodec ? (JpegliChromaCombo?.SelectedItem as string ?? "auto") : "auto",
                CjpegliProgressiveId = useAdvCodec ? JpegliProgressiveCombo?.SelectedIndex switch { 0 => -1, 1 => 0, 2 => 2, _ => -1 } : -1,
                CjpegliOptimize = useAdvCodec ? (JpegliOptimizeCheck?.IsChecked ?? true) : true,
                CjpegliAdaptiveQuant = useAdvCodec ? (JpegliAdaptiveQuantCheck?.IsChecked ?? true) : true,
                CjpegliEncoderBackend = useAdvCodec ? (JpegliEncoderBackendCombo?.SelectedIndex == 1 ? "sjpeg" : "libjpeg") : "libjpeg",
                CjpegliPsnrTarget = useAdvCodec ? (float)(JpegliPsnrBox?.Value ?? 0) : 0,
                CjpegliMultiThreadAvailable = false,
                StripExifGps = StripExifGpsCheck?.IsChecked ?? true,
                StripExifTime = StripExifTimeCheck?.IsChecked ?? false,
                StripExifCamera = StripExifCameraCheck?.IsChecked ?? false,
                StripExifAll = StripExifAllCheck?.IsChecked ?? false,
                StripXmp = StripXmpCheck?.IsChecked ?? false,
                AppendPngExtension = AppendPngExtCheck?.IsChecked ?? false,
                // 图片最长边限制
                EnableMaxDimension = EnableMaxDimensionCheck?.IsChecked ?? false,
                MaxDimension = (int?)MaxDimensionBox?.Value ?? 1920,
                // (IccMode 已由 ColorStrategy 取代)
                ColorStrategy = GetColorStrategy(),   // 色彩策略（4 单选直选）
                ColorGamutMap = GetColorGamutMap(),   // 色域映射（GMO）：off 默认（钳位）/ on（压缩）
                IccFilePath = null, // 新模式不使用外部 ICC
                // (源色彩空间已改为自动 CICP+ICC 检测)
                // (烘焙目标取主色域下拉，旧 IccTargetColorSpace 字段已移除)
                // 动图参数（⚠ P1-E 任务 C：只在动图模式下读取，否则静态 WebP/JXL 会带上
                // 隐藏面板里的残留 fps/scale —— 静态 JXL 甚至会被换成 libjxl_anim。理由见
                // RegenerateCommand 处的同一段注释）
                AnimationFps = IsAnimationMode() ? ParseOptionalInt(AnimationFpsBox?.Text, 1, 60) : null,
                AnimationLoop = IsAnimationMode() ? ParseInt(AnimationLoopBox?.Text, 0, -1, 999) : -1,
                GifPaletteOptimize = GifPaletteCheck?.IsChecked ?? true,
                GifDither = GifDitherCheck?.IsChecked ?? true,
                AnimationScaleW = IsAnimationMode() ? (ParseOptionalInt(AnimationScaleWBox?.Text, 0, 4096) ?? 0) : 0,
                AnimationDuration = IsAnimationMode() ? (ParseOptionalDouble(AnimationDurationBox?.Text, 0.1, 3600) ?? 0) : 0
            };

            // RAW 模式: DNG 压缩设置 → 独立 DNG 选项字段 (QueueProcessor.ProcessDngAsync 读取)
            if ((ConversionModeCombo?.SelectedIndex ?? 0) == 2 && DngCompressionCombo != null)
            {
                var useJxl = DngCompressionCombo.SelectedIndex == 1;
                var jxlQ = ParseInt(DngJxlQualityBox?.Text, 0, 0, 100);
                options.DngCompression = useJxl ? 1 : 0;
                options.DngJxlQuality = jxlQ;
                options.DngJxlEffort = (int)(DngJxlEffortBox?.Value ?? 7);
                options.DngJxlDecodeSpeed = (int)(DngJxlDecodeSpeedBox?.Value ?? 4);
                options.DngLinear = DngLinearCombo?.SelectedIndex == 1;
                options.DngBitDepth = DngBitDepthCombo?.SelectedIndex == 0 ? 8 : 16;
                options.DngHighlightMode = DngHighlightCombo?.SelectedIndex ?? 1;
                // 兼容旧字段（供其他路径读取）
                options.JxlModular = useJxl;
                options.Lossless = !useJxl || jxlQ <= 0;
                options.Quality = useJxl ? Math.Max(jxlQ, 0) : options.Quality;
            }

            // Gain Map 的 JPEG 渐进级别由 JpegProgressiveId **直接**决定（单一真值见
            // ColorMapping/GainMapJpegCodec.ProgressiveLevelOf，由引擎 GainMap 出口消费）。
            // ⚠ 2026-09-19 删除此处原先的 `CjpegliProgressiveId = JpegProgressiveId switch …`：
            //   GainMap 路径**从不读** CjpegliProgressiveId（底图/增益图恒由 GainMapEncoder 调
            //   cjpegli 编码）⇒ 那是死代码，用户设的渐进被静默丢弃（实测 0 与 1 产物 MD5 相同）。

            return options;
        }

        private async Task<bool> AddSingleToQueue(string inputPath, string? inputBaseDir = null)
        {
            var options = await CollectOptionsFromUiAsync(inputPath);
            if (options == null) return false;

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

        /// <summary>
        /// 公共 API：供 IPC 服务器或外部调用添加任务到队列（线程安全）。
        /// QueueProcessor.Add 使用 ConcurrentQueue 线程安全，可同步调用；
        /// UI 集合更新通过 Dispatcher 调度到 UI 线程。
        /// </summary>
        /// <param name="items">要入队的任务列表</param>
        /// <returns>成功入队的数量</returns>
        public int AddItemsToQueue(IReadOnlyList<Models.QueueItem> items)
        {
            if (items == null || items.Count == 0) return 0;

            var addedList = new List<Models.QueueItem>();
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.InputPath)) continue;
                // 线程安全入队（ConcurrentQueue）
                _queueProcessor.Add(item);
                addedList.Add(item);
            }

            // UI 集合更新调度到 UI 线程（异步，不阻塞）
            if (addedList.Count > 0)
            {
                var snapshot = addedList.ToArray();
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var item in snapshot)
                    {
                        _queueView.Add(item);
                        _queueItems.Add(item);
                    }
                    if (LogText != null) LogText.Text += $"[IPC] 已添加 {snapshot.Length} 项到队列\n";
                });
            }

            return addedList.Count;
        }

        public void StartProcessing()
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
                        qi.OutputPath = GetOutputPath(qi.InputPath, qi.Options.Format, qi.InputBaseDir);
                        var dir = Path.GetDirectoryName(qi.OutputPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        qi.Command = BuildQueueItemCommand(qi);
                        qi.Log += $"[cmd-preview] {qi.Command}\n";
                    }
                    catch { }
                }
            }

            _queueProcessor.Start(concurrency);
            _isQueueRunning = true;
            if (StopAfterCurrentCheck?.IsChecked == true)
            {
                _queueProcessor.StopAfterCurrentQueue();
                if (LogText != null) LogText.Text += "已设置：完成当前队列后停止\n";
            }
            if (ClearQueueButton != null)
                ClearQueueButton.IsEnabled = false;
            if (LogText != null) LogText.Text += $"队列开始，并行: {concurrency} 个任务\n";
        }

        private void StartQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            StartProcessing();
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
                if (item.IsDeleted)
                {
                    var idx = _queueItems.FindIndex(x => ReferenceEquals(x, item));
                    if (idx >= 0 && idx < _queueView.Count)
                    {
                        _queueView.RemoveAt(idx);
                        _queueItems.RemoveAt(idx);
                        return;
                    }

                    // 回退：通过**显示名**匹配清理残留项（同时清理 _queueItems 和 _queueView）
                    // ⚠ 2026-09-21：原先取 `Path.GetFileName(InputPath)`。RAW 预处理会把 `InputPath` 改写成
                    //   内部临时 TIFF ⇒ 「改写前加入的重复条目」(`a.CR2`) 与「改写后的本项」(`a_raw.tiff`)
                    //   会得到**两个不同名字** ⇒ 残留项**清不掉**。改用 `DisplayName`（源路径优先）后两侧一致。
                    var fname = item.DisplayName;
                    for (int j = _queueView.Count - 1; j >= 0; j--)
                    {
                        if (string.Equals(_queueView[j].DisplayName, fname, StringComparison.OrdinalIgnoreCase))
                        {
                            _queueView.RemoveAt(j);
                        }
                    }
                    for (int j = _queueItems.Count - 1; j >= 0; j--)
                    {
                        if (string.Equals(_queueItems[j].DisplayName, fname, StringComparison.OrdinalIgnoreCase))
                        {
                            _queueItems.RemoveAt(j);
                        }
                    }
                }
                // Status 变更由 INotifyPropertyChanged + DataTemplate 绑定自动反映到 UI
                // 若"仅显示报错"模式开启，刷新过滤（完整模式 + 简洁模式）
                if (ShowErrorsOnlyCheck?.IsChecked == true)
                    ApplyErrorOnlyFilter();
                if (SimpleErrorsOnlyCheck?.IsChecked == true)
                    RefreshSimpleErrorFilter();
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

        // ═══════════════════════════════════════════════
        // 托盘支持：公共访问器
        // ═══════════════════════════════════════════════

        /// <summary>队列是否正在运行</summary>
        public bool IsQueueRunning => _isQueueRunning;

        /// <summary>允许完全退出（托盘菜单 Exit 时置 true）</summary>
        public bool AllowFullExit
        {
            get => _allowFullExit;
            set => _allowFullExit = value;
        }

        /// <summary>获取正在处理的任务数量</summary>
        public int GetProcessingCount()
        {
            return _queueItems.Count(i => i.IsProcessing);
        }

        /// <summary>暂停队列（完成当前项后停止）</summary>
        public void PauseQueue()
        {
            if (_isQueueRunning)
            {
                _queueProcessor.StopAfterCurrentQueue();
                if (LogText != null) LogText.Text += "已请求：完成当前队列后停止\n";
            }
        }

        /// <summary>继续队列（从暂停恢复）</summary>
        public void ResumeQueue()
        {
            if (!_isQueueRunning)
            {
                int concurrency = GetConcurrencyValue();
                _queueProcessor.Start(concurrency);
                _isQueueRunning = true;
                if (LogText != null) LogText.Text += $"队列继续，并行: {concurrency} 个任务\n";
            }
        }

        private void DeleteSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (QueueList?.SelectedIndex is int idx and >= 0 && idx < _queueItems.Count)
            {
                var item = _queueItems[idx];
                if (item.IsProcessing)
                {
                    if (LogText != null) LogText.Text += "无法删除正在处理的任务\n";
                    return;
                }
                item.IsCancelled = true;
                _queueView.RemoveAt(idx);
                _queueItems.RemoveAt(idx);
                if (LogText != null) LogText.Text += $"已删除: {item.DisplayName}\n";
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
        internal static string BuildQueueItemCommand(QueueItem item)
        {
            var fmt = (item.Options.Format ?? "").ToLowerInvariant();
            var inputExt = Path.GetExtension(item.InputPath).ToLowerInvariant();

            // ── DNG 输出 (dngtool 编码) ──
            // D-1 2026-10-07: preview must call the SAME argument builder as the real
            // execution path (QueueProcessor.ProcessDngAsync -> RawService.EncodeToDngAsync).
            // Previously this branch kept its own copy of the judgement and had drifted
            // three ways vs the real one:
            //   1. missing `-4` for --dng-bit-depth 8 (bitDepth was not passed at all)
            //   2. missing `-jxlq` (JXL quality never emitted)
            //   3. emitted `-q {q}` -- the stale pre-2026-09-21 form that the real path
            //      deliberately abandoned as severely lossy (dngtool reads -q as demosaic
            //      quality, so it was silently reinterpreted as JXL quality 3).
            // Sharing the builder makes a second drift structurally impossible.
            if (fmt == "dng")
            {
                var useJxl = item.Options.DngCompression == 1;
                var jxlQ = item.Options.DngJxlQuality;
                // Same legacy-field compatibility shim as ProcessDngAsync.
                if (!useJxl && item.Options.JxlModular == true)
                {
                    useJxl = true;
                    jxlQ = item.Options.Lossless ? 0 : Math.Clamp(item.Options.Quality, 1, 100);
                }
                return "dngtool " + RawService.BuildEncodeToDngArguments(
                    item.InputPath, item.OutputPath,
                    compression: useJxl ? 1 : 0, jxlQuality: jxlQ,
                    linear: item.Options.DngLinear,
                    jxlEffort: item.Options.DngJxlEffort,
                    jxlDecodeSpeed: item.Options.DngJxlDecodeSpeed,
                    bitDepth: item.Options.DngBitDepth,
                    highlightMode: item.Options.DngHighlightMode,
                    threads: item.Options.Threads);
            }

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

            // ── cjxl 后端（仅当目标格式为 jxl 时，与真实分派一致）──
            if (item.Options.EncoderBackend == EncoderBackend.Cjxl && CjxlService.IsAvailable
                && EncoderBackendCompat.IsCompatibleWith(EncoderBackend.Cjxl, item.Options.Format))
            {
                return "cjxl " + CjxlService.BuildCjxlArguments(item.InputPath, item.OutputPath, item.Options,
                    forPreview: true);
            }

            // ── Gain Map (Ultra HDR) JPEG：纯 C# GainMapEncoder + cjpegli ──
            if (item.Options.JpegGainMap && (fmt == "jpg" || fmt == "jpeg"))
            {
                var q = item.Options.Quality;
                var nits = item.Options.JpegGainMapTargetNits;
                var gmq = item.Options.JpegGainMapQuality;
                var cj = CjpegliService.IsAvailable ? " + cjpegli" : "";
                return $"[纯托管] GainMapEncoder 色调映射{cj} + 增益图打包 (峰值 {nits}nits, q={q}, prog -p {Services.ColorMapping.GainMapJpegCodec.ProgressiveLevelOf(item.Options)} (-1=none))";
            }

            // ── cjpegli 后端（仅当目标格式为 jpg/jpeg 时，与真实分派一致）──
            if (item.Options.EncoderBackend == EncoderBackend.Cjpegli && CjpegliService.IsAvailable
                && EncoderBackendCompat.IsCompatibleWith(EncoderBackend.Cjpegli, item.Options.Format))
            {
                return "cjpegli " + CjpegliService.BuildCjpegliArguments(item.InputPath, item.OutputPath, item.Options);
            }

            // ── JXR 后端 (JxrEncApp 编码) ──
            // D-1 2026-10-07: the JXR backend never reaches ffmpeg. Both real exits --
            // QueueProcessor.ProcessJxrAsync and the colour-engine exit
            // RawColorPipeline.EncodeJxrViaJxrEncAppAsync -- decode to a BMP/TIFF
            // intermediate and hand it to JxrEncApp. The preview used to fall through to
            // the ffmpeg default, so a user reading `--dry-run` saw only `ffmpeg` and had
            // no way to learn that JxrEncApp (the actual encoder) would run.
            // The intermediate is a temp file whose name is only known at run time, so the
            // preview names it symbolically -- the point of the preview here is *which
            // encoder runs*, and that is now stated exactly.
            if (item.Options.EncoderBackend == EncoderBackend.Jxr
                && EncoderBackendCompat.IsCompatibleWith(EncoderBackend.Jxr, item.Options.Format))
            {
                if (!JxrService.IsAvailable)
                    return "[JXR] JxrEncApp.exe 未检测到，真实执行将失败 (JxrEncApp 未找到)";
                var jxrQuality = JxrService.ResolveQuality(item.Options.Quality, item.Options.Lossless);
                var jxrIntermediate = (item.Options.BitDepth ?? 16) > 8 ? "<tmp>/input.tiff" : "<tmp>/input.bmp";
                return "JxrEncApp " + JxrService.BuildArguments(jxrIntermediate, item.OutputPath, jxrQuality);
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

            // 与 ProcessJxlInputAsync 保持一致：JXL→JXL 时跳过 djxl 重构
            // （djxl 无法输出 .jxl），按原生码流重编码路径预览
            var effectiveType = jxlType;
            if (jxlType == JxlImageType.JpegReconstruction && targetFmt == "jxl")
                effectiveType = JxlImageType.NativeCodestream;

            if (effectiveType == JxlImageType.JpegReconstruction)
            {
                if (DjxlService.IsAvailable)
                    return $"djxl \"{inputPath}\" \"{outputPath}\"";
                else
                    return "ffmpeg " + FfmpegCommandBuilder.BuildArguments(options, inputPath, outputPath);
            }

            if (effectiveType == JxlImageType.NativeCodestream)
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
                            // djxl→cjpegli 管道（优先）或 PNM 中转
                            // 注：djxl v0.12+ 不再支持 PNG 输出，与实际执行一致用 pnm (PPM/PAM 自适应)
                            return $"djxl \"{inputPath}\" --output_format=pnm - | cjpegli "
                                + CjpegliService.BuildCjpegliArguments("-", outputPath, options);
                        }
                        if (targetFmt == "jxl" && CjxlService.IsAvailable)
                        {
                            // cjxl 需要文件输入 → PNM 中转
                            return $"[两步法] djxl 解码 → 临时PNM → cjxl "
                                + CjxlService.BuildCjxlArguments("<tmp.pnm>", outputPath, options,
                                    forPreview: true);
                        }
                    }

                    // 直接管道：djxl → ffmpeg
                    // ⚠ P7d：stdin 探不到位深/alpha ⇒ 必须拿真实源文件做探测替代，否则预览里看到的命令看不到实际输出像素。
                    // ⚠ 已知不一致（未修，只登记）：预览用 `--output_format=png -f image2pipe`，而实际执行
                    //   （QueueProcessor.PipeDjxlToFfmpegAsync）用 `ppm/pam + ppm_pipe/pam_pipe` 并注入高级色彩参数。
                    //   要真同源得把管道参数拼装收拢到一处（同一个 BuildFfmpegPipeArguments），属 P7a-3 后续。
                    var pipeArgs = FfmpegCommandBuilder.BuildArguments(options, "-", outputPath, inputPath);
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
                JxlPreserveUltrahdr = original.JxlPreserveUltrahdr,
                JpegGainMap = original.JpegGainMap,
                JpegGainMapQuality = original.JpegGainMapQuality,
                JpegGainMapTargetNits = original.JpegGainMapTargetNits,
                JpegGainMapDownsample = original.JpegGainMapDownsample,
                JpegGainMapMultiChannel = original.JpegGainMapMultiChannel,
                JpegGainMapBaseGamut = original.JpegGainMapBaseGamut,
                TiffCompressionAlgo = original.TiffCompressionAlgo,
                EnableThumbnail = original.EnableThumbnail,
                ThumbnailLongEdge = original.ThumbnailLongEdge,
                ThumbnailQuality = original.ThumbnailQuality,
            };
        }

        private void QueueList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (QueueList?.SelectedIndex is int idx and >= 0 && idx < _queueItems.Count)
            {
                var item = _queueItems[idx];
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

        /// <summary>
        /// 清空已选文件的唯一实现：完整模式的「清空文件」按钮与简洁模式覆盖层共用它
        /// （覆盖层的宗旨是"操作都落在高级模式上"，不允许自己维护第二套清空语义）。
        /// </summary>
        private void ClearSelectedMediaLists()
        {
            _mediaFiles.Clear();
            _selectedFiles.Clear();
            _selectedFileBaseDirs.Clear();
            UpdateMediaFileCount();
        }

        private void ClearMediaFiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ClearSelectedMediaLists();
        }

        private void ClearFiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _mediaFiles.Clear();
            _selectedFiles.Clear();
            _selectedFileBaseDirs.Clear();
            UpdateMediaFileCount();
        }

        private void MediaFileList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (MediaFileList?.SelectedItem is string path && !string.IsNullOrWhiteSpace(path))
            {
                var win = new ImageDetailWindow(path, null);
                win.Show(this);
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
            var cs = ColorSpaceCombo?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(cs) || cs == "auto")
            {
                ClearColorConflict();
                UpdateAdvancedColorControls();
                return;
            }

            // 自动填充高级参数以匹配快速选择（委托 ColorSpaceRegistry 单一来源，避免映射表重复漂移）
            var (primaries, trc, matrix) = ColorSpaceRegistry.FfmpegTokens(cs);

            if (primaries != null && ColorPrimariesCombo != null)
                SelectComboItem(ColorPrimariesCombo, primaries);
            if (trc != null && ColorTrcCombo != null)
                SelectComboItem(ColorTrcCombo, trc);
            if (matrix != null && ColorMatrixCombo != null)
                SelectComboItem(ColorMatrixCombo, matrix);

            // HDR (BT.2020 PQ/HLG / P3 PQ) → 自动位深联动: 探测源位深，匹配最优色深
            var isHdrCs = cs is "BT.2020 PQ" or "BT.2020 HLG" or "P3 PQ";
            if (isHdrCs && BitDepthCombo != null)
            {
                var currentBd = BitDepthCombo.SelectedItem as string;
                var sourceBd = ProbeCurrentSourceBitDepth();

                // 确定目标位深: 不低于10bit, 匹配源位深, 不超格式上限
                int targetBd = sourceBd switch
                {
                    <=8 => 10,              // 源≤8bit → 至少升到10bit
                    10 => 10,               // 源10bit → 保持10bit
                    12 => 12,               // 源12bit → 保持12bit
                    _ => 16                 // 源16bit+ → 保持16bit
                };
                int maxBd = _currentCapabilities?.SupportedBitDepths?.DefaultIfEmpty(8).Max() ?? 8;
                int effectiveBd = Math.Min(targetBd, maxBd);

                // 映射到选项
                var bdStr = effectiveBd switch { <=8 => "8", 10 => "10", 12 => "12", _ => "16" };
                if (currentBd == "auto" || currentBd == "8" || effectiveBd > (int.TryParse(currentBd, out var cb) ? cb : 8))
                    SelectComboItem(BitDepthCombo, bdStr);

                // 记录源位深用于冲突检测
                _lastProbedSourceBitDepth = sourceBd;
            }

            // ── Gain Map 联动: HDR+JPEG → 建议 RGB 增益图 ──
            if (isHdrCs)
            {
                var fmt = FormatCombo?.SelectedItem as string ?? "";
                if (fmt == "JPEG" && JpegGainMapTypeCombo != null
                    && UseAdvancedCodec?.IsChecked == true)
                {
                    // HDR+JPEG: RGB增益图色彩更准（若用户未手动选择则自动切 RGB）
                    if (JpegGainMapTypeCombo.SelectedIndex == 0)
                    {
                        JpegGainMapTypeCombo.SelectedIndex = 1;  // RGB 增益图
                        if (LogText != null)
                            LogText.Text += "[GainMap] HDR 输出建议使用 RGB 增益图以获得更优色彩\n";
                    }
                }
            }

            // Gain Map 面板可见性依赖 HDR 色彩
            UpdateGainMapPanelVisibility();

            DetectColorConflicts();
            UpdateAdvancedColorControls();

            // ── ICC 联动: 非 BT.709/sRGB → 默认烘焙到标准色域+ICC+CICP（尊重用户手动选择的 ICC 模式）──
            AutoLinkIccModeFromColorSpace(cs);

            // ── ICC 联动: 同步烘焙目标空间 + 更新模式2提示 ──
            UpdateIccCarryLabel();
        }

        /// <summary>用户手动切换 ICC 模式时标记（联动写入时由 _suppressIccAutoLink 隔道，不误标记）。</summary>
        private void MarkIccModeTouched()
        {
            if (!_suppressIccAutoLink) _iccModeTouched = true;
        }

        /// <summary>
        /// 色彩空间→ICC 模式联动：选中非标准色域(Display P3/P3 PQ/BT.2020 PQ/HLG)时，
        /// 默认切为 BakeToStandard（烘焙像素到该标准色域 + 携带 ICC + CICP）；回到 sRGB/BT.709/auto 时切回 None（纯 CICP）。
        /// 若用户已手动选择过 ICC 模式(_iccModeTouched)，则不再自动覆盖。
        /// </summary>
        private void AutoLinkIccModeFromColorSpace(string? cs)
        {
            if (_iccModeTouched) return;
            var spec = ColorSpaceRegistry.Resolve(cs);
            bool wantBake = spec != null && spec.Kind != ColorSpaceRegistry.GamutKind.Standard;
            _suppressIccAutoLink = true;
            try
            {
                if (wantBake && IccModeBake != null && IccModeBake.IsChecked != true)
                    IccModeBake.IsChecked = true;
                else if (!wantBake && IccModeNone != null
                         && (IccModeBake?.IsChecked == true || IccModeBakeOnly?.IsChecked == true))
                    IccModeNone.IsChecked = true;
            }
            finally { _suppressIccAutoLink = false; }
            UpdateIccPanelVisibility();
        }



        /// <summary>更新模式2(CarryIcc)的提示</summary>
        private void UpdateIccCarryLabel()
        {
            if (IccInfoLabel == null) return;
            var cs = ColorSpaceCombo?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(cs) || cs == "auto")
                cs = "sRGB（默认）";
            IccInfoLabel.Text = $"将保留源 ICC（如有），否则自动嵌入 {cs} 标准 ICC";
        }

        /// <summary>更新 CICP 格式兼容性提示</summary>
        private void UpdateCicpHint()
        {
            if (_currentCapabilities == null) return;
            var note = _currentCapabilities.CicpNote;
            if (!string.IsNullOrWhiteSpace(note) && IccInfoLabel != null
                && IccModeCarry?.IsChecked != true && IccModeBake?.IsChecked != true)
            {
                // 只在非 ICC 模式下显示 CICP 提示（ICC 模式下 IccInfoLabel 已有内容）
                IccInfoLabel.Text = $"📋 {note}";
                if (IccFilePanel != null) IccFilePanel.IsVisible = true;
            }
        }

        /// <summary>选择 ComboBox 中匹配的项</summary>
        private static void SelectComboItem(ComboBox combo, string value)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if ((combo.Items[i] as string) == value)
                { combo.SelectedIndex = i; return; }
            }
        }

        /// <summary>探测当前源文件位深（调用 ffprobe）</summary>
        private int ProbeCurrentSourceBitDepth()
        {
            if (string.IsNullOrWhiteSpace(_inputPath) || !File.Exists(_inputPath))
                return 8;
            try
            {
                var ffprobe = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(AppSettingsService.Current.FfmpegPath) ?? "",
                    "ffprobe.exe");
                if (!File.Exists(ffprobe)) return 8;
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=bits_per_raw_sample -of csv=p=0 \"{_inputPath}\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    RedirectStandardError = true,   // P2-3：stderr 必须排空（下方挂 errTask 异步读完）
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p == null) return 8;
                // ⚠ 先挂读取任务、再等退出（本仓范式，见 `FfmpegCommandBuilder.ProbeWithFfprobe`）：
                //   旧写法把同步读写在 `WaitForExit(3000)` **之前** ⇒ 子进程不关管道就永不返回，
                //   **超时（与超时后的 Kill）是死代码**。实测：畸形 `.jxl` 让 ffprobe 永不退出。
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(3000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    // `#26`：关闭 `#24` 留下的「Trace 可见性债」——本方法是 MainWindow 实例方法，
                    // 直接走 UI 的日志通道（`LogText`），`Trace` 保留作兜底（非 UI 线程时仍可抓到）。
                    var bdMsg = $"[bitdepth] ffprobe 位深探测超时（3s）⇒ 已终止：{System.IO.Path.GetFileName(_inputPath)}（按 8bit 处理）";
                    System.Diagnostics.Trace.WriteLine(bdMsg);
                    Dispatcher.UIThread.Post(() => { if (LogText != null) LogText.Text += bdMsg + "\n"; });
                    return 8;
                }
                var output = outTask.GetAwaiter().GetResult().Trim();
                errTask.GetAwaiter().GetResult();
                if (int.TryParse(output, out var bd) && bd > 0) return bd;
                return 8;
            }
            catch { return 8; }
        }

        private void UseAdvancedColor_IsCheckedChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (AdvancedColorPanel != null) AdvancedColorPanel.IsVisible = UseAdvancedColor?.IsChecked == true;
            // Gain Map 面板可见性依赖 HDR 色彩判断（高级参数中的 PQ/HLG 传输函数）
            UpdateGainMapPanelVisibility();
            DetectColorConflicts();
            RegenerateCommand();
        }

        /// <summary>根据「JPEG + HDR 色彩 (BT.2020 PQ/HLG)」刷新 Gain Map 面板可见性（任意 JPEG 编码器）</summary>
        private void UpdateGainMapPanelVisibility()
        {
            var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            if (fmt is not ("jpg" or "jpeg")) return;
            if (JpegGainMapPanel != null)
                JpegGainMapPanel.IsVisible = HdrModeCombo?.SelectedIndex == 1
                                    && (FormatCombo?.SelectedItem as string)?.ToLowerInvariant() is "jpg" or "jpeg";
        }

        private void UseAdvancedCodec_IsCheckedChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (AdvancedCodecPanel != null) AdvancedCodecPanel.IsVisible = UseAdvancedCodec?.IsChecked == true;
        }

        /// <summary>检测色彩选项冲突并在 UI 中报告</summary>
        private void DetectColorConflicts()
        {
            if (ColorConflictLabel == null) return;
            var conflicts = new List<string>();
            var cs = ColorSpaceCombo?.SelectedItem as string;
            var fmt = FormatCombo?.SelectedItem as string ?? "";

            // 冲突1: HDR→SDR
            if (cs is "BT.2020 PQ" or "BT.2020 HLG" or "P3 PQ")
            {
                if (fmt is "JPEG" or "WebP" or "GIF" or "BMP")
                    conflicts.Add($"⚠ HDR({cs})→{fmt}(SDR)：将自动应用色调映射降级");
            }

            // 冲突2: ICC烘焙激活时锁定手动色彩参数（双重转换锁定）
            var isBaking = IccModeBake?.IsChecked == true || IccModeBakeOnly?.IsChecked == true;
            if (isBaking)
            {
                // 烘焙模式下禁用高级色彩手动参数，避免双重转换
                if (ColorPrimariesCombo != null) ColorPrimariesCombo.IsEnabled = false;
                if (ColorTrcCombo != null) ColorTrcCombo.IsEnabled = false;
                if (ColorMatrixCombo != null) ColorMatrixCombo.IsEnabled = false;
                if (UseAdvancedColor?.IsChecked == true)
                    conflicts.Add("🔒 ICC 烘焙已激活 — 高级色彩参数已锁定，由烘焙目标空间控制");
            }
            else
            {
                // 恢复高级色彩参数
                if (ColorPrimariesCombo != null) ColorPrimariesCombo.IsEnabled = true;
                if (ColorTrcCombo != null) ColorTrcCombo.IsEnabled = true;
                if (ColorMatrixCombo != null) ColorMatrixCombo.IsEnabled = true;
            }

            // 冲突3: HDR + 目标格式位深限制
            if (cs is "BT.2020 PQ" or "BT.2020 HLG" or "P3 PQ")
            {
                var bdStr = BitDepthCombo?.SelectedItem as string;
                int.TryParse(bdStr, out var selBd);
                var fmtMaxBd = _currentCapabilities?.SupportedBitDepths?.DefaultIfEmpty(8).Max() ?? 8;

                if (fmtMaxBd <= 8)
                    conflicts.Add($"⚠ {fmt} 仅支持 8-bit，HDR 精度将损失（仍可继续）");
                else if (_lastProbedSourceBitDepth > fmtMaxBd)
                    conflicts.Add($"⚠ 源 {_lastProbedSourceBitDepth}-bit → 目标最大 {fmtMaxBd}-bit，将下采样");
                else if (selBd == 8 && _lastProbedSourceBitDepth > 8)
                    conflicts.Add($"⚠ 源 {_lastProbedSourceBitDepth}-bit，手动 8-bit 将损失精度");
            }

            // 烘焙目标现与输出色彩空间同源（均来自 ColorSpaceCombo），无需再校验二者不匹配。

            if (conflicts.Count > 0)
            {
                ColorConflictLabel.Text = string.Join("\n", conflicts);
                ColorConflictLabel.IsVisible = true;
                ColorConflictLabel.Foreground = Avalonia.Media.Brushes.Orange;
            }
            else { ClearColorConflict(); }
        }

        private void ClearColorConflict()
        {
            if (ColorConflictLabel != null)
            { ColorConflictLabel.Text = ""; ColorConflictLabel.IsVisible = false; }
        }

        /// <summary>仅「手动」策略：用户改三元组时实时校验 CICP 组合，非阻拦告警（仍按选择执行）。</summary>
        /// <summary>`auto`（下拉首项，= 不指定）归一为 <c>null</c>；其余原样返回。</summary>
        /// <remarks>
        /// 2026-10-04 设计裁定：模式 1 允许三个下拉取 `auto`（走自动默认）；模式 2/3/4 必须手动指定。
        /// 提交路径（`BuildOptionsFromUi`）与校验路径共用本函数 ⇒ "auto == 没指定"只有一处口径。
        /// </remarks>
        private static string? AutoToNull(string? v)
            => string.IsNullOrWhiteSpace(v) || v.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : v;

        private void UpdateAdvancedColorValidation()
        {
            if (ColorConflictLabel == null) return;
            var strategy = GetColorStrategy();
            // ── 模式 2/3/4：精确参数**必须手动指定**（2026-10-04 设计裁定）──
            //    模式 1（Recommended）允许 auto —— 由引擎按目标容器能力给自动默认
            //    （RAW→图片：能承载 BT.2020 ⇒ BT.2020，否则 sRGB/BT.709）。
            if (strategy != Models.ColorStrategy.Recommended)
            {
                var miss = new List<string>();
                if (AutoToNull(ColorPrimariesCombo?.SelectedItem as string) == null) miss.Add("原色");
                if (AutoToNull(ColorTrcCombo?.SelectedItem as string) == null) miss.Add("传递函数");
                if (AutoToNull(ColorMatrixCombo?.SelectedItem as string) == null) miss.Add("矩阵");
                if (miss.Count > 0)
                {
                    ColorConflictLabel.Text = $"⚠ 模式 2/3/4 需手动指定：{string.Join("、", miss)}（当前为 auto）";
                    ColorConflictLabel.IsVisible = true;
                    ColorConflictLabel.Foreground = Avalonia.Media.Brushes.Orange;
                    return;
                }
            }
            // 模式 4（Manual）沿用既有的三元组合法性校验
            if (strategy != Models.ColorStrategy.Manual) { ClearColorConflict(); return; }
            var warn = ColorSpaceRegistry.ValidateCicpTriple(
                AutoToNull(ColorPrimariesCombo?.SelectedItem as string),
                AutoToNull(ColorTrcCombo?.SelectedItem as string),
                AutoToNull(ColorMatrixCombo?.SelectedItem as string));
            if (warn != null)
            {
                ColorConflictLabel.Text = "⚠ " + warn + "（仍按你的选择执行）";
                ColorConflictLabel.IsVisible = true;
                ColorConflictLabel.Foreground = Avalonia.Media.Brushes.Orange;
            }
            else ClearColorConflict();
        }

        // ═══════════════════════════════════════════════
        //  ICC 色彩管理 — 事件处理
        // ═══════════════════════════════════════════════

        private void IccMode_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // 已通过 InitControls 中的事件 lambda 绑定处理
        }

        private void UpdateIccPanelVisibility()
        {
            var strategy = GetColorStrategy();
            // 源/烘焙目标子面板已废弃（源恒自动检测；目标取主色域下拉）→ 隐藏。
            if (IccBakePanel != null) IccBakePanel.IsVisible = false;
            if (IccFilePanel != null) IccFilePanel.IsVisible = strategy == Models.ColorStrategy.CarryIcc;
            if (strategy == Models.ColorStrategy.CarryIcc && IccInfoLabel != null)
                UpdateIccCarryLabel(); // 动态显示当前色彩空间对应的 ICC 类型

            // 携带/仅CICP：色彩空间锁定（源为准）；推荐/手动：按格式能力启用。
            if (ColorSpaceCombo != null)
            {
                bool locked = strategy is Models.ColorStrategy.CarryIcc or Models.ColorStrategy.BakeCicpOnly;
                ColorSpaceCombo.IsEnabled = !locked
                    && (_currentCapabilities is null || _currentCapabilities.SupportedColorSpaces.Count > 0);
            }

            // 仅CICP（策略3）：格式不支持 CICP 时禁用该单选；JPEG 且 GainMap 开启时例外可用。
            bool gainMapOn = HdrModeCombo?.SelectedIndex == 1
                && (FormatCombo?.SelectedItem as string)?.ToLowerInvariant() is "jpg" or "jpeg";
            if (IccModeBake != null)
                IccModeBake.IsEnabled = (_currentCapabilities?.SupportsCicp ?? true) || gainMapOn;

            UpdateIccPreview();
            UpdateIccCompatibility();
            DetectColorConflicts();
            // ⚠ 2026-10-04：策略切换（模式 1 ↔ 2/3/4）会改变「精确参数是否**必须手动指定**」的判定
            //   ⇒ 本函数被 4 个策略单选的 IsCheckedChanged 调用，在此一并刷新校验（单点接线，避免漏接）。
            UpdateAdvancedColorValidation();
        }

        private async void BrowseIcc_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 ICC 配置文件",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("ICC 配置文件") { Patterns = new[] { "*.icc", "*.icm" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });

            if (files != null && files.Count > 0)
            {
                _iccFilePath = files[0].Path.LocalPath;
                if (IccPathBox != null) IccPathBox.Text = _iccFilePath;

                // 解析 ICC 文件信息
                if (IccProfileService.IsValidIccProfile(_iccFilePath))
                {
                    var info = IccProfileService.ParseInfo(_iccFilePath);
                    if (IccInfoLabel != null)
                        IccInfoLabel.Text = info?.ToString() ?? "有效的 ICC 配置文件";
                }
                else
                {
                    if (IccInfoLabel != null)
                        IccInfoLabel.Text = Services.LocalizationService.Instance["icc.invalid.file"];
                }

                UpdateIccPreview();
                RegenerateCommand();
            }
        }

        private void ClearIcc_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _iccFilePath = null;
            if (IccPathBox != null) IccPathBox.Text = "";
            if (IccInfoLabel != null) IccInfoLabel.Text = "";
            UpdateIccPreview();
            RegenerateCommand();
        }

        private void IccHelp_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var msg = "🎨 ICC 色彩管理帮助\n\n" +
                "📎 嵌入 ICC：将 ICC 配置文件作为元数据写入输出文件，像素数据不变。\n" +
                "   需要查看设备支持 ICC 才能正确显示。\n\n" +
                "🔥 烘焙 ICC：将像素从源色彩空间转换到目标色彩空间（如 sRGB），\n" +
                "   所有设备均可正确显示，无需 ICC 支持。\n\n" +
                "🔥📎 烘焙+嵌入：转换像素并同时嵌入 ICC，双保险。\n\n" +
                "⚠️ 注意：\n" +
                "  - 显示器 ICC (mntr) 适合软校样，烘焙建议使用标准色彩空间 ICC\n" +
                "  - 烘焙会永久改变像素值，不可逆\n" +
                "  - AVIF/JXL 的 ICC 嵌入需要 FFmpeg ≥ 7.0 + lcms2";
            if (LogText != null) LogText.Text += msg + "\n";
        }

        private void IccSourceSpace_Changed(object? sender, SelectionChangedEventArgs e)
        {
            UpdateIccPreview();
            RegenerateCommand();
        }

        private void UpdateIccPreview()
        {
            if (IccPreviewText == null) return;

            var isBake = IccModeBake?.IsChecked == true || IccModeBakeOnly?.IsChecked == true;
            if (!isBake)
            {
                IccPreviewText.Text = "";
                return;
            }

            var srcName = IccSourceSpaceCombo?.SelectedIndex > 0
                ? IccSourceSpaceCombo?.SelectedItem as string ?? "auto"
                : "auto（从文件检测）";
            var dstName = GetIccTargetSpace();

            IccPreviewText.Text = $"转换预览:\n  {srcName}  ──zscale──▶  {dstName}";
        }

        private void UpdateIccCompatibility()
        {
            if (IccCompatPanel == null || IccCompatText == null) return;

            var isAnyActive = IccModeCarry?.IsChecked == true
                || IccModeBake?.IsChecked == true
                || IccModeBakeOnly?.IsChecked == true;

            if (!isAnyActive)
            {
                IccCompatPanel.IsVisible = false;
                return;
            }

            var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            var isEmbed = IccModeCarry?.IsChecked == true || IccModeBakeOnly?.IsChecked == true;

            var nativeFormats = new[] { "jpg", "jpeg", "png", "tiff" };
            var iccgenFormats = new[] { "avif", "jxl", "webp" };

            if (isEmbed)
            {
                if (Array.Exists(nativeFormats, f => f == fmt))
                    IccCompatText.Text = $"✅ {fmt.ToUpper()} — 原生支持 ICC 嵌入";
                else if (Array.Exists(iccgenFormats, f => f == fmt))
                    IccCompatText.Text = $"⚠️ {fmt.ToUpper()} — 通过 iccgen 滤镜嵌入（需 FFmpeg ≥ 7.0 + lcms2）";
                else
                    IccCompatText.Text = $"❌ {fmt.ToUpper()} — 不支持 ICC 嵌入";
            }
            else
            {
                IccCompatText.Text = $"🔥 烘焙模式 — 像素将被转换，无需 ICC 读取支持";
            }

            IccCompatPanel.IsVisible = true;
        }

        private void UpdateAdvancedColorControls()
        {
            // 委托 ColorSpaceRegistry 单一来源（修正旧硬编码对 BT.2020 HLG 误填 smpte2084 的 bug）。
            var cs = ColorSpaceCombo?.SelectedItem as string;
            var (p, t, m) = ColorSpaceRegistry.FfmpegTokens(cs);   // auto/未知 → null → 下方默认 sRGB
            SetComboSelection(ColorPrimariesCombo, p ?? "bt709");
            SetComboSelection(ColorTrcCombo, t ?? "iec61966-2-1");
            SetComboSelection(ColorMatrixCombo, m ?? "bt709");
            RegenerateCommand();
        }

        /// <summary>
        /// 高级色彩参数联动: 选择 primaries 时自动补全匹配的 trc/matrix。
        /// 仅在 trc/matrix 仍是旧值 (未手动改过) 时更新, 避免覆盖用户手动选择。
        /// </summary>
        private void AutoLinkColorParameters()
        {
            var prim = ColorPrimariesCombo?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(prim)) return;

            var trc = ColorTrcCombo?.SelectedItem as string;
            var mat = ColorMatrixCombo?.SelectedItem as string;

            switch (prim)
            {
                case "bt2020":
                    // BT.2020: 色域 bt2020 + PQ 曲线 + 非恒定亮度矩阵
                    if (trc is null or "" or "bt709" or "iec61966-2-1" or "smpte432")
                        SetComboSelection(ColorTrcCombo, "smpte2084");
                    if (mat is null or "" or "bt709" or "bt601")
                        SetComboSelection(ColorMatrixCombo, "bt2020nc");
                    break;
                case "smpte432":
                    // Display P3: 色域 P3 + sRGB 曲线 (P3D65 标准) + bt709 矩阵
                    // 若用户选了 PQ 则保留 (P3 PQ HDR 场景), 仅当 trc 仍是旧 SDR 值时切换
                    if (trc is null or "" or "bt709" or "bt470bg")
                        SetComboSelection(ColorTrcCombo, "iec61966-2-1");
                    if (mat is null or "" or "bt2020nc" or "bt601")
                        SetComboSelection(ColorMatrixCombo, "bt709");
                    break;
                case "bt709":
                    // BT.709: 标准 SDR
                    if (trc is null or "" or "smpte2084" or "arib-std-b67" or "iec61966-2-1" or "smpte432")
                        SetComboSelection(ColorTrcCombo, "bt709");
                    if (mat is null or "" or "bt2020nc" or "bt601")
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

        private async void BuildCommand_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_inputPath))
            {
                if (LogText != null) LogText.Text += "请先选择输入文件\n";
                return;
            }

            var fmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            var options = await CollectOptionsFromUiAsync(_inputPath, logNotices: true);
            if (options == null) return;
            var threads = options.Threads;

            _outputPath = GetOutputPath(_inputPath, options.Format);
            if (CommandText != null)
            {
                var isCjxl = fmt is "jxl" && CjxlService.IsAvailable
                    && (IsJpegInput(_inputPath) || options.EncoderBackend == EncoderBackend.Cjxl);
                if (isCjxl)
                {
                    // 2026-09-30：这里原先**手写**一条 `cjxl "…" "…" -d 0 -e N --lossless_jpeg=1`，
                    // 完全不看 `options.JxlLosslessJpeg`、不看质量档、也不看渐进/光子噪声/色彩标注
                    // ⇒ 用户点「生成命令」看到的是一条根本不会执行的、且恰好是那条永远没被激活的
                    // 无损重封装命令（预览与实跑两套口径）。改为与实跑同一个构造器。
                    CommandText.Text = "cjxl " + CjxlService.BuildCjxlArguments(
                        _inputPath, _outputPath, options, forPreview: true);
                }
                else
                {
                    var args = "";
                    WithEncoderOptionWarnings(() =>
                        args = FfmpegCommandBuilder.BuildArguments(options, _inputPath, _outputPath));
                    CommandText.Text = "ffmpeg " + args;
                }
            }
        }

        /// <summary>
        /// Runs one command-preview build while collecting the "advanced encoder option ignored /
        /// downgraded" notices into the GUI panel.
        /// <para>
        /// Why: those notices are emitted by the encoder argument builders
        /// (<c>ImageEncoderArgs</c> / <c>FfmpegCommandBuilder</c>) straight to
        /// <c>Console.WriteLine</c>. This app is a WinExe with no console in GUI mode, so before this
        /// they were visible to headless users only. The sink is AsyncLocal so concurrent queue items
        /// cannot cross-contaminate, and it is disposed at the end of each preview build.
        /// </para>
        /// <para>
        /// Dedup: one preview can rebuild the command several times (format/option linkage), so the
        /// panel shows each distinct notice once per generation.
        /// </para>
        /// </summary>
        private void WithEncoderOptionWarnings(Action build)
        {
            var seen = new List<string>();
            var prev = Services.ColorMapping.ImageEncoderArgs.WarnSink.Value;
            Services.ColorMapping.ImageEncoderArgs.WarnSink.Value = msg =>
            {
                var t = (msg ?? "").Trim();
                if (t.Length == 0) return;
                foreach (var s in seen) { if (string.Equals(s, t, StringComparison.Ordinal)) return; }
                seen.Add(t);
            };
            try { build(); }
            finally { Services.ColorMapping.ImageEncoderArgs.WarnSink.Value = prev; }
            ShowEncoderOptionWarnings(seen);
        }

        /// <summary>Shows (or clears) the aggregated advanced-encoder-option notices.</summary>
        private void ShowEncoderOptionWarnings(IReadOnlyList<string> notices)
        {
            if (EncoderOptionWarning == null || EncoderOptionWarningText == null) return;
            if (notices.Count == 0)
            {
                EncoderOptionWarning.IsVisible = false;
                EncoderOptionWarningText.Text = "";
                return;
            }
            var sb = new System.Text.StringBuilder();
            foreach (var n in notices) sb.Append(n).Append('\n');
            EncoderOptionWarningText.Text = sb.ToString().TrimEnd('\n');
            EncoderOptionWarning.Background = Avalonia.Media.Brush.Parse("#33FF9800");
            EncoderOptionWarning.IsVisible = true;
        }

        // `StopAfterCurrent_Click` 已移除，使用队列旁的复选框 `StopAfterCurrentCheck` 控制该行为。

        /// <summary>语言切换：中文 ↔ 英文</summary>
        private void LangToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Services.LocalizationService.Instance.ToggleLanguage();
            var newLang = Services.LocalizationService.Instance.CurrentLanguage;
            AppSettingsService.Current.Language = newLang;
            AppSettingsService.Save();
            RefreshLocCombos();   // ComboBox 选项是 C# 填充的，需手动刷新
            // 按钮文本由 {ext:Loc language} 绑定自动更新，语言切换后 LocBindingSource 触发 Item[] 刷新
            // 窗口标题也由 {ext:Loc app.title} 绑定自动更新
        }

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

        private void GpuToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var currentGpu = AppSettingsService.Current.GpuAcceleration;
            var newGpu = !currentGpu;
            AppSettingsService.Current.GpuAcceleration = newGpu;
            AppSettingsService.Save();

            if (GpuToggleBtn != null)
            {
                GpuToggleBtn.Content = newGpu ? "GPU" : "CPU";
                ToolTip.SetTip(GpuToggleBtn, newGpu
                    ? "GPU 加速已启用（ANGLE/D3D11）— 点击切换为 CPU 软件渲染（需重启）"
                    : "GPU 加速已禁用 — 点击切换为 GPU 硬件加速（需重启）");
            }

            if (LogText != null)
            {
                LogText.Text += newGpu
                    ? "[GPU] GPU 硬件加速已启用（DX11/Vulkan），重启后生效\n"
                    : "[GPU] GPU 硬件加速已禁用，将使用 CPU 软件渲染，重启后生效\n";
            }
        }

        /// <summary>
        /// 根据当前选中的 GPU 编码器状态，显示/隐藏 GPU 编码器警告面板。
        /// 当用户选择了一个不可用的 GPU 硬件编码器时，显示橙色警告提示。
        /// </summary>
        private void UpdateGpuEncoderWarning()
        {
            if (GpuEncoderWarning == null || GpuEncoderWarningText == null)
                return;

            var encStr = EncoderCombo?.SelectedItem as string ?? "";
            var encoderName = EncoderInfo.ParseEncoderName(encStr);
            var isGpu = EncoderDetectionService.IsGpuEncoderName(encoderName);

            if (!isGpu)
            {
                GpuEncoderWarning.IsVisible = false;
                return;
            }

            var status = Services.GpuCapabilityService.GetEncoderStatus(encoderName);
            if (status == null)
            {
                // GPU 检测尚未运行
                GpuEncoderWarning.IsVisible = true;
                GpuEncoderWarning.Background = Avalonia.Media.Brush.Parse("#33FFB300");
                GpuEncoderWarningText.Text = $"⚡ {encoderName} 为 GPU 硬件编码器，GPU 能力检测尚未完成...";
                if (GpuEncoderWarningHint != null)
                    GpuEncoderWarningHint.IsVisible = false;
                return;
            }

            switch (status.Availability)
            {
                case Services.GpuEncoderAvailability.Verified:
                    // GPU 编码器可用 → 隐藏警告
                    GpuEncoderWarning.IsVisible = false;
                    break;

                case Services.GpuEncoderAvailability.DeviceFoundUntested:
                    // 有设备但未运行时验证 → 浅黄色提示
                    GpuEncoderWarning.IsVisible = true;
                    GpuEncoderWarning.Background = Avalonia.Media.Brush.Parse("#33FFB300");
                    GpuEncoderWarningText.Text = $"⚡ {status.FriendlyName} — GPU 设备已检测，但运行时验证尚未完成";
                    if (GpuEncoderWarningHint != null)
                        GpuEncoderWarningHint.IsVisible = false;
                    break;

                case Services.GpuEncoderAvailability.CompiledNoDevice:
                    // 编译了但无设备 → 橙色警告
                    GpuEncoderWarning.IsVisible = true;
                    GpuEncoderWarning.Background = Avalonia.Media.Brush.Parse("#33FF9800");
                    GpuEncoderWarningText.Text = $"⚠️ {status.WarningMessage}";
                    if (GpuEncoderWarningHint != null)
                    {
                        GpuEncoderWarningHint.IsVisible = true;
                        GpuEncoderWarningHint.Text = Services.LocalizationService.Instance["gpu.advice.switch.cpu"];
                    }
                    break;

                case Services.GpuEncoderAvailability.Failed:
                    // 运行时验证失败 → 红色警告
                    GpuEncoderWarning.IsVisible = true;
                    GpuEncoderWarning.Background = Avalonia.Media.Brush.Parse("#33F44336");
                    GpuEncoderWarningText.Text = $"❌ {status.WarningMessage}";
                    if (GpuEncoderWarningHint != null)
                    {
                        GpuEncoderWarningHint.IsVisible = true;
                        GpuEncoderWarningHint.Text = Services.LocalizationService.Instance["gpu.advice.switch.cpu.list"];
                    }
                    break;

                case Services.GpuEncoderAvailability.NotCompiled:
                    // 未编译 → 灰色提示
                    GpuEncoderWarning.IsVisible = true;
                    GpuEncoderWarning.Background = Avalonia.Media.Brush.Parse("#339E9E9E");
                    GpuEncoderWarningText.Text = $"ℹ️ {status.WarningMessage}";
                    if (GpuEncoderWarningHint != null)
                    {
                        GpuEncoderWarningHint.IsVisible = true;
                        GpuEncoderWarningHint.Text = Services.LocalizationService.Instance["gpu.advice.ffmpeg.version"];
                    }
                    break;

                default:
                    GpuEncoderWarning.IsVisible = false;
                    break;
            }
        }

        /// <summary>刷新队列列表 ItemsSource 绑定，触发 DataTemplate 重新应用前景色</summary>
        private void RefreshQueueListBinding()
        {
            if (QueueList == null) return;
            var items = QueueList.ItemsSource;
            QueueList.ItemsSource = null;
            QueueList.ItemsSource = items;
        }

        // ═══════════════════════════════════════════════
        // 简洁模式 — 入口、退出、全部控制逻辑
        // ═══════════════════════════════════════════════

        private void EnterSimpleMode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (FullModePanel == null || SimpleModePanel == null) return;

            _simpleModeActive = true;
            FullModePanel.IsVisible = false;
            SimpleModePanel.IsVisible = true;

            // 绑定已选文件列表 —— 与完整模式共用同一份集合（覆盖层只是它的另一种显示）
            if (SimpleMediaList != null)
                SimpleMediaList.ItemsSource = _mediaFiles;

            // 绑定共享队列数据
            if (SimpleQueueList != null)
                SimpleQueueList.ItemsSource = _queueView;

            // 同步自动编码开关状态
            if (AutoEncodeToggle != null)
                AutoEncodeToggle.IsChecked = AppSettingsService.Current.SimpleModeAutoEncode;

            // 同步预设选择
            SyncPresetToSimpleMode();

            // 设置拖放支持（仅注册一次，重复进入简洁模式不会重复 AddHandler）
            if (SimpleDropZone != null && !_simpleDropHandlersAttached)
            {
                DragDrop.SetAllowDrop(SimpleDropZone, true);
                SimpleDropZone.AddHandler(DragDrop.DragEnterEvent, SimpleDragEnter);
                SimpleDropZone.AddHandler(DragDrop.DragLeaveEvent, SimpleDragLeave);
                SimpleDropZone.AddHandler(DragDrop.DragOverEvent, SimpleDragOver);
                SimpleDropZone.AddHandler(DragDrop.DropEvent, SimpleDrop);
                _simpleDropHandlersAttached = true;
            }

            // 更新计数
            UpdateSimpleCounts();
            UpdateSimpleProgressDisplay();
        }

        protected override void OnClosed(EventArgs e)
        {
            // 释放自动编码轮询定时器，避免窗口关闭后 Timer 及其 Elapsed 处理器泄漏
            // （隐藏到托盘不会触发 OnClosed，仅真正退出时执行）
            try
            {
                if (_autoEncodeTimer != null)
                {
                    _autoEncodeTimer.Stop();
                    _autoEncodeTimer.Elapsed -= OnAutoEncodeTick;
                    _autoEncodeTimer.Dispose();
                    _autoEncodeTimer = null;
                }
            }
            catch { }
            base.OnClosed(e);
        }

        private void SimpleReturn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (FullModePanel == null || SimpleModePanel == null) return;

            _simpleModeActive = false;
            SimpleModePanel.IsVisible = false;
            FullModePanel.IsVisible = true;

            // 已选文件与队列本来就是同一份集合（覆盖层无自己的内容源）⇒ 此处不再需要任何同步/合并动作。

            // 如果自动编码在运行，日志提示
            if (_autoEncodeTimer != null && _autoEncodeTimer.Enabled)
            {
                if (LogText != null)
                    LogText.Text += "[简洁模式] 已返回完整模式，自动编码保持运行\n";
            }
        }

        private void SimpleStartQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // 运行中再次点击会重启队列并中断当前任务，需要保护
            if (_queueProcessor.IsRunning)
            {
                if (LogText != null)
                    LogText.Text += "[简洁模式] 队列已在运行中，如需重启请先停止\n";
                return;
            }

            int concurrency = GetConcurrencyValue();
            _queueProcessor.RequeueStoppedAndFailed(_queueItems);
            _queueProcessor.Start(concurrency);
            _isQueueRunning = true;

            if (LogText != null)
                LogText.Text += "[简洁模式] 队列已启动\n";
            if (SimpleStatusLabel != null)
                SimpleStatusLabel.Text = Services.LocalizationService.Instance["queue.running"];
        }

        private void SimpleStopQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _queueProcessor.Stop();
            _isQueueRunning = false;

            if (LogText != null)
                LogText.Text += "[简洁模式] 队列已停止\n";
            if (SimpleStatusLabel != null)
                SimpleStatusLabel.Text = Services.LocalizationService.Instance["queue.stopped"];
        }

        private async void SimpleAddFiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片文件",
                AllowMultiple = true,
                FileTypeFilter = AppSettingsService.Current.GetImageFilePickerFilter()
            });

            if (files == null || files.Count == 0) return;

            int added = 0;
            foreach (var file in files)
            {
                var inputPath = file.Path.LocalPath;

                // 1) 加入高级模式的已选文件列表（去重；覆盖层显示的就是它）
                if (!_mediaFiles.Contains(inputPath))
                {
                    _mediaFiles.Add(inputPath);
                    added++;
                }

                // 2) 入队复用高级模式的唯一采集点，简洁模式不自带选项映射
                await AddSingleToQueue(inputPath);
            }

            UpdateMediaFileCount();   // 已选计数标签属完整模式，覆盖层改了同一份集合要一并刷新
            UpdateSimpleCounts();

            if (LogText != null)
                LogText.Text += $"[简洁模式] 已添加 {added} 个文件 → 已选列表 + 转换队列\n";

            // 自动编码检查
            if (AutoEncodeToggle?.IsChecked == true)
                CheckAutoEncode();
        }

        /// <summary>清空已选文件列表（不影响转换队列）</summary>
        private void SimpleClearMedia_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ClearSelectedMediaLists();
            UpdateSimpleCounts();
        }

        // ═══════════════════════════════════════════════
        // 简洁模式拖放处理
        // ═══════════════════════════════════════════════

        private void SimpleDragEnter(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Contains(DataFormat.File))
            {
                e.DragEffects = DragDropEffects.Copy;
                if (SimpleDropZone != null)
                    SimpleDropZone.BorderBrush = Avalonia.Media.Brushes.DodgerBlue;
            }
            else
                e.DragEffects = DragDropEffects.None;
        }

        private void SimpleDragLeave(object? sender, DragEventArgs e)
        {
            if (SimpleDropZone != null)
                SimpleDropZone.BorderBrush = Avalonia.Media.Brushes.Gray;
        }

        private void SimpleDragOver(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Contains(DataFormat.File))
                e.DragEffects = DragDropEffects.Copy;
            else
                e.DragEffects = DragDropEffects.None;
        }

        private async void SimpleDrop(object? sender, DragEventArgs e)
        {
            if (SimpleDropZone != null)
                SimpleDropZone.BorderBrush = Avalonia.Media.Brushes.Gray;

            if (!e.DataTransfer.Contains(DataFormat.File)) return;

            var items = e.DataTransfer.TryGetFiles();
            if (items == null) return;

            int added = 0;
            var enabledExts = new HashSet<string>(AppSettingsService.Current.GetEnabledExtensions());

            foreach (var item in items)
            {
                var path = item.TryGetLocalPath() ?? item.Path.LocalPath;
                if (string.IsNullOrWhiteSpace(path)) continue;

                // 跳过非启用格式
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (!enabledExts.Contains(ext)) continue;

                // 加入高级模式的已选文件列表（去重）
                if (!_mediaFiles.Contains(path))
                {
                    _mediaFiles.Add(path);
                    added++;
                }

                // 入队复用高级模式的唯一采集点
                await AddSingleToQueue(path);
            }

            UpdateMediaFileCount();   // 同 SimpleAddFiles_Click：共用一份集合 ⇒ 完整模式的计数标签也要跟着走
            UpdateSimpleCounts();

            if (LogText != null && added > 0)
                LogText.Text += $"[简洁模式] 拖放添加 {added} 个文件 → 已选列表 + 转换队列\n";

            if (AutoEncodeToggle?.IsChecked == true)
                CheckAutoEncode();
        }

        private void SimpleClearAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_queueProcessor.IsRunning)
            {
                if (LogText != null)
                    LogText.Text += "[简洁模式] 请先停止队列再清空\n";
                return;
            }

            // 清空已完成和失败的队列项
            int removed = 0;
            for (int i = _queueItems.Count - 1; i >= 0; i--)
            {
                if (_queueItems[i].Status != "处理中")
                {
                    _queueItems.RemoveAt(i);
                    if (i < _queueView.Count) _queueView.RemoveAt(i);
                    removed++;
                }
            }

            // 清空已选文件列表（走完整模式自己的那套清空语义）
            ClearSelectedMediaLists();

            UpdateSimpleCounts();

            if (LogText != null && removed > 0)
                LogText.Text += $"[简洁模式] 已清空 {removed} 个队列项 + 已选文件列表\n";
        }

        private void AutoEncodeToggle_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var enabled = AutoEncodeToggle?.IsChecked == true;
            AppSettingsService.Current.SimpleModeAutoEncode = enabled;
            AppSettingsService.Save();

            if (enabled)
            {
                // 启动轮询定时器
                if (_autoEncodeTimer == null)
                {
                    _autoEncodeTimer = new System.Timers.Timer(2000);
                    _autoEncodeTimer.Elapsed += OnAutoEncodeTick;
                }
                _autoEncodeTimer.Start();

                if (LogText != null)
                    LogText.Text += "[简洁模式] 自动编码已开启，队列有任务时自动开始\n";

                // 立即检查一次
                CheckAutoEncode();
            }
            else
            {
                _autoEncodeTimer?.Stop();

                if (LogText != null)
                    LogText.Text += "[简洁模式] 自动编码已关闭\n";
            }
        }

        private void SimpleErrorsOnly_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (SimpleQueueList == null) return;

            bool showErrorsOnly = (sender as CheckBox)?.IsChecked == true;

            if (showErrorsOnly)
                RefreshSimpleErrorFilter();
            else
                SimpleQueueList.ItemsSource = _queueView;
        }

        /// <summary>重建简洁模式"仅显示报错"过滤集合（队列状态变化时由 OnQueueItemUpdated 调用）</summary>
        private void RefreshSimpleErrorFilter()
        {
            if (SimpleQueueList == null) return;
            var filtered = new ObservableCollection<Models.QueueItem>(
                _queueView.Where(i => i.HasError || !i.IsCompleted));
            SimpleQueueList.ItemsSource = filtered;
        }

        private void SimpleViewDetail_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is QueueItem item)
            {
                var command = string.IsNullOrEmpty(item.Command)
                    ? BuildQueueItemCommand(item)
                    : item.Command;
                var win = new ProgressWindow(item, command);
                win.Show(this);
            }
        }

        // ═══════════════════════════════════════════════
        // 简洁模式 — 内部辅助方法
        // ═══════════════════════════════════════════════

        /// <summary>将当前的简洁模式预设 ComboBox 同步到主界面保存的预设</summary>
        private void SyncPresetToSimpleMode()
        {
            if (SimplePresetCombo == null || SimplePresetCombo.Items == null) return;

            if (_simpleActivePreset != null)
            {
                for (int i = 0; i < SimplePresetCombo.Items.Count; i++)
                {
                    if (SimplePresetCombo.Items[i] is PresetEntry pe &&
                        pe.Name == _simpleActivePreset.Name)
                    {
                        SimplePresetCombo.SelectedIndex = i;
                        return;
                    }
                }
            }

            if (SimplePresetCombo.Items.Count > 0)
            {
                SimplePresetCombo.SelectedIndex = 0;
                _simpleActivePreset = SimplePresetCombo.Items[0] as PresetEntry;
            }
        }

        /// <summary>简洁模式预设切换 → 自动应用到主界面参数控件</summary>
        private void SimplePresetCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!_simpleModeActive) return; // 仅简洁模式激活时生效
            if (SimplePresetCombo?.SelectedItem is PresetEntry preset)
            {
                _simpleActivePreset = preset;
                ApplyPresetData(preset.Data);
                _ = RefreshEncoderListAsync();
                UpdateOptionAvailability();
                // 指纹要在全部刷新之后抓：回显比对的是"用户实际看到的那份状态"
                _simpleAppliedSig = SimpleCoreSignature();
                UpdateSimpleCurrentStateLabel();
            }
        }

        /// <summary>自动编码轮询：检查是否有待处理任务且调度器空闲，是则自动启动</summary>
        private void OnAutoEncodeTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Dispatcher.UIThread.Post(() => CheckAutoEncode());
        }

        private void CheckAutoEncode()
        {
            // 自动编码仅限简洁模式（含从简洁模式返回后 timer 仍在运行的场景，
            // _simpleModeActive=false 时静默跳过，避免在完整模式误启动队列）
            if (!_simpleModeActive) return;
            if (_queueProcessor.IsRunning) return;

            var hasPending = _queueItems.Any(i =>
                i.IsPending && !i.IsCancelled);

            if (hasPending)
            {
                _queueProcessor.RequeueStoppedAndFailed(_queueItems);
                _queueProcessor.Start(GetConcurrencyValue());

                if (SimpleStatusLabel != null)
                    SimpleStatusLabel.Text = "自动编码运行中...";
                if (LogText != null)
                    LogText.Text += "[简洁模式] 自动编码已启动\n";
            }
        }

        /// <summary>更新简洁模式底部状态栏（进度 + 已用时间 + 预计剩余）</summary>
        private void UpdateSimpleProgressDisplay()
        {
            if (!_simpleModeActive) return;

            var total = _queueItems.Count;
            var completed = _queueItems.Count(i => i.CompletedAt.HasValue || i.IsDeleted);
            var processing = _queueItems.Count(i => i.IsProcessing);

            if (SimpleProgressLabel != null)
                SimpleProgressLabel.Text = $"队列: {completed}/{total}";
            if (SimpleStatusLabel != null && !_queueProcessor.IsRunning)
                SimpleStatusLabel.Text = total > 0 ? "就绪" : "队列为空";

            // 已用时间：当前处理中任务（与完整模式同口径）
            var cur = _queueItems.FirstOrDefault(i => i.IsProcessing && i.StartedAt.HasValue);
            if (cur != null && SimpleElapsedLabel != null)
            {
                var elapsed = DateTimeOffset.UtcNow - cur.StartedAt!.Value;
                SimpleElapsedLabel.Text = elapsed.TotalMinutes >= 1
                    ? $"已用: {elapsed.TotalMinutes:F1}m"
                    : $"已用: {elapsed.TotalSeconds:F0}s";
            }
            else if (SimpleElapsedLabel != null)
            {
                SimpleElapsedLabel.Text = "";
            }

            // 预计剩余：基于已完成任务的平均耗时（与完整模式同口径）
            if (SimpleEtaLabel != null)
            {
                var finishedItems = _queueItems.Where(i => i.StartedAt.HasValue && i.CompletedAt.HasValue).ToList();
                if (finishedItems.Count > 0 && total > completed)
                {
                    var avgSec = finishedItems.Average(i => (i.CompletedAt!.Value - i.StartedAt!.Value).TotalSeconds);
                    var etaSec = avgSec * (total - completed);
                    if (etaSec >= 3600)
                        SimpleEtaLabel.Text = $"预计剩余: {etaSec / 3600:F1}h";
                    else if (etaSec >= 60)
                        SimpleEtaLabel.Text = $"预计剩余: {etaSec / 60:F1}m";
                    else
                        SimpleEtaLabel.Text = $"预计剩余: {etaSec:F0}s";
                }
                else
                {
                    SimpleEtaLabel.Text = "";
                }
            }
        }

        /// <summary>更新简洁模式双列表计数标签</summary>
        private void UpdateSimpleCounts()
        {
            if (SimpleFileCount != null)
                SimpleFileCount.Text = $"{_mediaFiles.Count} 个文件";
            if (SimpleQueueCount != null)
                SimpleQueueCount.Text = $"{_queueView.Count} 项";
            UpdateSimpleCurrentStateLabel();
        }

        /// <summary>覆盖层的只读回显：把高级模式当前的生效设置原样显示出来（值取自同一批控件的当前显示串）。</summary>
        private void UpdateSimpleCurrentStateLabel()
        {
            if (SimpleCurrentStateLabel == null) return;
            var loc = Services.LocalizationService.Instance;
            var fmt = FormatCombo?.SelectedItem as string ?? "?";
            var parts = new List<string> { fmt, ConversionModeCombo?.SelectedItem as string ?? "?" };
            var enc = EncoderCombo?.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(enc)) parts.Add(enc!);
            if (QualitySlider != null)
                parts.Add("q" + Models.FfmpegOptions.FormatQualityForDisplay(
                    NormalizeFormat(fmt), (int)QualitySlider.Value, GetCurrentEncoderBackend()));
            var text = loc["simple.current.state"] + ": " + string.Join(" | ", parts);
            if (IsSimplePresetDrifted()) text += "  (" + loc["simple.preset.custom"] + ")";
            SimpleCurrentStateLabel.Text = text;
        }

        // 只比对回显里出现的那几根轴，且拿「应用预设那一刻」的指纹作参照 —— 不是拿预设 JSON 本身比对：
        // 后者会被 Build/Apply 尚未完全对称的旁路字段噪成「永不匹配」，那条账由 N1b / E 组去锁，
        // 不该由一个显示层回显天天喊假警。
        private string SimpleCoreSignature()
        {
            var p = BuildPresetData();
            var mode = p.ConversionMode ?? ModeOfPresetFormat(p.Format);
            return string.Join("|",
                (p.Format ?? "").Trim(),
                mode,
                (p.EncoderBackend ?? "Ffmpeg").Trim(),
                p.Quality,
                p.Lossless ? 1 : 0);
        }

        private bool IsSimplePresetDrifted()
            => _simpleAppliedSig.Length > 0 && SimpleCoreSignature() != _simpleAppliedSig;

        // 暴露给简洁模式进度刷新的公开入口（由每秒定时器调用）
        private void RefreshSimpleProgressIfActive()
        {
            if (_simpleModeActive)
            {
                Dispatcher.UIThread.Post(() => UpdateSimpleProgressDisplay());
            }
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
                Title = "选择 ffmpeg",
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
            // ⚠ 换 ffmpeg 目录 = 换了"环境"，凡是**以 ffmpeg 身份为隐含键**的静态缓存都必须一起失效。
            //   旧写法只清了 EncoderDetectionService 一份，导致：
            //     · GPU 硬件编码报告（`GpuCapabilityService._cachedReport`，缓存键里根本没有 ffmpeg 身份）
            //       ⇒ 换包后仍报旧 GPU 结论；它的 ClearCache 此前**全仓 0 调用点**。
            //     · 格式能力（`FormatCapabilitiesService._cache`，`SupportedColorSpaces` 是**累加**语义
            //       ⇒ 换能力更弱的 ffmpeg 也回不去）。
            //     · PLAN 定位（`PlatformServices._planScanned` 是一次性闩 ⇒ 插拔/换包后不重扫）。
            GpuCapabilityService.ClearCache();
            FormatCapabilitiesService.ClearCache();
            PlatformServices.ResetPlanFolderCache();
            PlatformServices.ResetToolLaunchabilityCache();   // 换包 ⇒ 旧 exe 的「起不来」判定不能再留着（否则修好了也不恢复）

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

        // ═══════════════════════════════════════════
        // v2.0 外部工具浏览 (3 项统一)
        // ═══════════════════════════════════════════

        private async void BrowseJxlLibDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "选择 JPEG XL 参考库目录 (含 cjxl/djxl/cjpegli)", AllowMultiple = false });
            if (folders == null || folders.Count == 0) return;
            var dir = folders[0].Path.LocalPath;
            AppSettingsService.Current.JxlLibDir = dir;
            AppSettingsService.Save();
            if (JxlLibDirBox != null) JxlLibDirBox.Text = dir;
            RefreshJxlServices();
            ValidateJxlLibDir(dir);
            RegenerateCommand();
        }

        private async void BrowseArtifactsDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "选择 Windows 构建产物目录 (Jxr/avifenc/dngtool/...)", AllowMultiple = false });
            if (folders == null || folders.Count == 0) return;
            var dir = folders[0].Path.LocalPath;
            AppSettingsService.Current.WindowsArtifactsDir = dir;
            AppSettingsService.Save();
            if (ArtifactsDirBox != null) ArtifactsDirBox.Text = dir;
            RefreshArtifactsServices();
            ValidateArtifactsDir(dir);
            RegenerateCommand();
        }

        private void ClearJxlLibDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            AppSettingsService.Current.JxlLibDir = null;
            AppSettingsService.Save();
            if (JxlLibDirBox != null) JxlLibDirBox.Text = "";
            RefreshJxlServices();
            RefreshToolsStatusBar();
            RegenerateCommand();
        }

        private void ClearArtifactsDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            AppSettingsService.Current.WindowsArtifactsDir = null;
            AppSettingsService.Save();
            if (ArtifactsDirBox != null) ArtifactsDirBox.Text = "";
            RefreshArtifactsServices();
            RefreshToolsStatusBar();
            RegenerateCommand();
        }

        // ── 旧事件处理（v2.0 已废弃，保留兼容 XAML 引用）──
        private void BrowseCjxl_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => BrowseJxlLibDir_Click(s, e);
        private void ClearCjxlPath_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ClearJxlLibDir_Click(s, e);
        private void BrowseAvifenc_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => BrowseArtifactsDir_Click(s, e);
        private void ClearAvifencPath_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ClearArtifactsDir_Click(s, e);
        private void BrowseUltrahdr_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => BrowseArtifactsDir_Click(s, e);
        private void ClearUltrahdrPath_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ClearArtifactsDir_Click(s, e);
        private void BrowseJxr_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => BrowseArtifactsDir_Click(s, e);
        private void ClearJxrPath_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ClearArtifactsDir_Click(s, e);

        // exiftool 仍独立保留（选择文件而非文件夹），保留原版完整实现
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
                    new FilePickerFileType("可执行文件") { Patterns = PlatformServices.ExeFilePickerPatterns },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });
            if (files != null && files.Count > 0)
            {
                var path = files[0].Path.LocalPath;
                AppSettingsService.Current.ExifToolPath = path;
                AppSettingsService.Save();
                if (ExifToolPathBox != null) ExifToolPathBox.Text = path;
                ExifToolService.ClearCache();   // 用户主动入口（浏览/清除/重新检测）⇒ 必须绕开「一次进程一遍」的门控
            ExifToolService.Detect(m => { if (LogText != null) LogText.Text += m; }, force: true);
                UpdateExifToolPanelState();
                if (LogText != null)
                    LogText.Text += ExifToolService.IsAvailable
                        ? $"✅ exiftool 路径已更新: {path}\n"
                        : $"⚠️ exiftool 路径已设置但无法使用: {path}\n";
                RegenerateCommand();
            }
        }

        private void ClearExifToolPath_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            AppSettingsService.Current.ExifToolPath = null;
            AppSettingsService.Save();
            if (ExifToolPathBox != null) ExifToolPathBox.Text = "";
            ExifToolService.ClearCache();   // 用户主动入口（浏览/清除/重新检测）⇒ 必须绕开「一次进程一遍」的门控
            ExifToolService.Detect(m => { if (LogText != null) LogText.Text += m; }, force: true);
            UpdateExifToolPanelState();
            if (LogText != null)
                LogText.Text += ExifToolService.IsAvailable
                    ? $"✅ exiftool 自动检测: {ExifToolService.DetectedPath}\n"
                    : "ℹ️ exiftool: 未检测到\n";
            RegenerateCommand();
        }

        // ═══════════════════════════════════════════════
        // Photoshop ACR 验证 (2026-08-15)
        // ═══════════════════════════════════════════════

        private async void BrowsePhotoshop_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 Photoshop.exe",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件") { Patterns = PlatformServices.ExeFilePickerPatterns },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });
            if (files != null && files.Count > 0)
            {
                var path = files[0].Path.LocalPath;
                AppSettingsService.Current.PhotoshopPath = path;
                AppSettingsService.Save();
                if (PhotoshopPathBox != null) PhotoshopPathBox.Text = path;
                PsRenderService.ClearCache();
                RefreshPsPanelState();
                if (LogText != null)
                    LogText.Text += PsRenderService.IsAvailable
                        ? $"✅ Photoshop 路径已更新: {path}\n"
                        : $"⚠️ Photoshop 路径已设置但无法使用: {path}\n";
            }
        }

        private void ClearPhotoshopPath_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            AppSettingsService.Current.PhotoshopPath = null;
            AppSettingsService.Save();
            if (PhotoshopPathBox != null) PhotoshopPathBox.Text = "";
            PsRenderService.ClearCache();
            RefreshPsPanelState();
            if (LogText != null)
                LogText.Text += PsRenderService.IsAvailable
                    ? $"✅ Photoshop 自动检测: {PsRenderService.DetectedPath}\n"
                    : "ℹ️ Photoshop: 未检测到\n";
        }

        /// <summary>从 AppSettings 加载 Photoshop 设置并刷新状态</summary>
        private void RefreshPsPanelState()
        {
            if (PhotoshopPathBox != null)
                PhotoshopPathBox.Text = AppSettingsService.Current.PhotoshopPath ?? "";

            PsRenderService.ClearCache();
            PsRenderService.Detect();
            if (PsToolsStatus != null)
            {
                PsToolsStatus.Children.Clear();
                PsToolsStatus.Children.Add(new TextBlock
                {
                    Text = PsRenderService.IsAvailable
                        ? $"✅ PS {Path.GetFileName(PsRenderService.DetectedPath)}"
                        : "❌ Photoshop (未检测到)",
                    FontSize = 10,
                    Foreground = Avalonia.Media.Brushes.Gray,
                    Margin = new Avalonia.Thickness(0, 0, 2, 0)
                });
            }
        }

        private void RefreshJxlServices()
        {
            CjxlService.ClearCache(); CjxlService.Detect();
            DjxlService.ClearCache(); DjxlService.Detect();
            CjpegliService.ClearCache(); CjpegliService.Detect();
        }

        private void RefreshArtifactsServices()
        {
            JxrService.ClearCache(); JxrService.Detect();
            RawService.ClearCache(); RawService.Detect();
        }

        private void ValidateJxlLibDir(string dir)
        {
            if (LogText == null) return;
            var found = new List<string>();
            if (File.Exists(Path.Combine(dir, PlatformServices.Cjxl))) found.Add("cjxl");
            if (File.Exists(Path.Combine(dir, PlatformServices.Djxl))) found.Add("djxl");
            if (File.Exists(Path.Combine(dir, PlatformServices.Cjpegli))) found.Add("cjpegli");
            LogText.Text += $"[jxl] 目录扫描: {(found.Count > 0 ? string.Join(", ", found) : "未检测到 JXL 工具")}\n";
            RefreshToolsStatusBar();
        }

        private void ValidateArtifactsDir(string dir)
        {
            if (LogText == null) return;
            var found = new List<string>();
            if (File.Exists(Path.Combine(dir, PlatformServices.JxrEnc))) found.Add("JxrEnc");
            if (File.Exists(Path.Combine(dir, PlatformServices.Avifenc))) found.Add("avifenc");
            if (File.Exists(Path.Combine(dir, PlatformServices.DngToolName))) found.Add("dngtool");
            LogText.Text += $"[artifacts] 目录扫描: {(found.Count > 0 ? string.Join(", ", found) : "未检测到")}\n";
            RefreshToolsStatusBar();
        }

        private void ToggleToolsPanel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (ToolsDetailPanel != null)
                ToolsDetailPanel.IsVisible = !ToolsDetailPanel.IsVisible;
            RefreshToolsStatusBar();
        }

        // ═══════════════════════════════════════════════
        // 缓存目录设置（折叠面板）
        // ═══════════════════════════════════════════════

        private void ToggleCachePanel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (CachePanel != null)
                CachePanel.IsVisible = !CachePanel.IsVisible;
        }

        // ═══════════ ⚙ 设置菜单（二级菜单）处理器（2026-09-16 新增）═══════════
        // 设计原则：菜单项只负责「改设置 + 保存 + 回显」，实际生效路径一律复用既有逻辑
        // （主题走 App.SetTheme、语言走 LocalizationService、缓存目录走既有选择器），
        // 避免出现第二份真值。

        /// <summary>渲染后端（手动选择 GPU）。需重启生效，故在日志里明确告知。</summary>
        private void RenderModeMenu_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.Tag is not string mode) return;
            AppSettingsService.Current.RenderingMode = mode;
            // "software" 与 GpuAcceleration=false 语义等价 ⇒ 两者同步，避免出现
            // 「菜单显示 CPU 软件渲染、而设置里 GPU 仍是开启」这种自相矛盾的状态。
            AppSettingsService.Current.GpuAcceleration = mode != "software";
            AppSettingsService.Save();
            SyncSettingsMenuChecks();
            if (LogText != null)
                LogText.Text += $"[渲染] 后端已设为 {DescribeRenderMode(mode)}，重启后生效\n";
        }

        private void ThemeMenu_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.Tag is not string tag) return;
            var isDark = tag == "dark";
            App.SetTheme(isDark);
            AppSettingsService.Current.ThemeMode = isDark ? 2 : 1;
            AppSettingsService.Save();
            if (ThemeToggleBtn != null) ThemeToggleBtn.Content = isDark ? "☀" : "🌙";
            SyncSettingsMenuChecks();
            RefreshQueueListBinding();   // 主题切换后队列列表 Foreground 需重算
        }

        private void LangMenu_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.Tag is not string lang) return;
            Services.LocalizationService.Instance.SetLanguage(lang);
            AppSettingsService.Current.Language = lang;
            AppSettingsService.Save();
            SyncSettingsMenuChecks();
            RefreshLocCombos();   // ComboBox 选项是 C# 填充的，需手动刷新
        }

        /// <summary>设置缓存目录（复用既有目录选择器逻辑，不另起一份真值）。</summary>
        private async void CacheDirMenu_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "选择缓存/临时文件目录",
                AllowMultiple = false
            });
            if (result.Count > 0)
            {
                var dir = result[0].Path.LocalPath;
                if (CacheDirBox != null) CacheDirBox.Text = dir;
                AppSettingsService.Current.CacheDirectory = dir;
                AppSettingsService.Save();
                SyncSettingsMenuChecks();
                if (LogText != null) LogText.Text += $"[缓存] 目录已设为 {dir}\n";
            }
        }

        /// <summary>在系统文件管理器中打开当前缓存目录。</summary>
        private void OpenCacheDirMenu_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var dir = Services.PlatformServices.GetTempDir();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                if (LogText != null) LogText.Text += "[缓存] 缓存目录不存在或不可用\n";
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true      // 必须：交给 shell 用默认文件管理器打开
                });
            }
            catch (Exception ex)
            {
                if (LogText != null) LogText.Text += $"[缓存] 打开失败：{ex.Message}\n";
            }
        }

        /// <summary>
        /// 按 Tag 在设置菜单里查项。
        /// ⚠ <c>MenuFlyout</c> 及其内部的 <c>MenuItem</c> **不在可视树**里，因此 XAML 的
        /// <c>x:Name</c> 不会生成字段、<c>FindControl</c> 也找不到（编译期就会报 CS0103）。
        /// 唯一可靠入口是按钮的 <c>Flyout</c> 属性。
        /// </summary>
        private MenuItem? FindMenuByTag(string tag)
        {
            var btn = this.FindControl<Button>("SettingsMenuBtn");
            if (btn?.Flyout is not MenuFlyout fly) return null;
            foreach (var top in fly.Items.OfType<MenuItem>())
                foreach (var sub in top.Items.OfType<MenuItem>())
                    if (sub.Tag as string == tag) return sub;
            return null;
        }

        // ═══════════ ComboBox 选项本地化（2026-09-16 新增）═══════════
        // 背景：XAML 里的 `<sys:String>中文</sys:String>` 是**元素内容**，标记扩展
        // `{ext:Loc}` 在那里不生效 ⇒ 这些选项切英文后仍是中文；而且 i18n 门禁的正则
        // 只匹配属性值，**也看不见它们**（等于一类隐形缺口）。
        // 这里改为 C# 填充，并登记到 _locCombos 以便语言切换时统一刷新。
        private readonly Dictionary<ComboBox, string[]> _locCombos = new();

        /// <summary>
        /// 用 Loc key 填充 ComboBox 选项，并登记以便语言切换时刷新。
        /// ⚠ **选项顺序必须与原 XAML 完全一致**——后端是用 <c>SelectedIndex</c> 取值的，
        /// 顺序变了会静默改变语义（界面选 A、实际跑 B），这是本项目反复出现过的坑。
        /// </summary>
        private void FillComboByLoc(ComboBox? combo, int defaultIndex, params string[] keys)
        {
            if (combo == null) return;
            _locCombos[combo] = keys;
            // ⚠ 首次填充时 ItemsSource 为空 ⇒ XAML 里写的 SelectedIndex 已被重置为 -1，
            //   必须显式给默认索引；否则选中项会丢，后端还可能拿到 -1 出错。
            var idx = combo.SelectedIndex >= 0 ? combo.SelectedIndex : defaultIndex;
            combo.ItemsSource = keys.Select(k => Services.LocalizationService.Instance[k]).ToList();
            combo.SelectedIndex = idx;
        }

        private static void ApplyLocCombo(ComboBox combo, string[] keys)
        {
            var loc = Services.LocalizationService.Instance;
            var idx = combo.SelectedIndex;
            combo.ItemsSource = keys.Select(k => loc[k]).ToList();
            combo.SelectedIndex = idx;   // 按索引保持选中项（顺序不变 ⇒ 语义不变）
        }

        /// <summary>语言切换后刷新所有已登记的 ComboBox 选项。</summary>
        private void RefreshLocCombos()
        {
            foreach (var kv in _locCombos) ApplyLocCombo(kv.Key, kv.Value);
        }

        /// <summary>
        /// 把当前设置回显到菜单勾选态。初始化时与每次改动后各调一次。
        /// ⚠ 只做回显，不写设置——避免「回显」反而把配置改掉。
        /// </summary>
        private void SyncSettingsMenuChecks()
        {
            try
            {
                var mode = (AppSettingsService.Current.RenderingMode ?? "auto").ToLowerInvariant();
                if (!AppSettingsService.Current.GpuAcceleration) mode = "software";
                foreach (var m in new[] { "auto", "angle", "vulkan", "software" })
                    FindMenuByTag(m)?.SetCurrentValue(MenuItem.IsCheckedProperty, m == mode);

                var dark = App.IsDarkMode();
                FindMenuByTag("dark")?.SetCurrentValue(MenuItem.IsCheckedProperty, dark);
                FindMenuByTag("light")?.SetCurrentValue(MenuItem.IsCheckedProperty, !dark);

                var lang = AppSettingsService.Current.Language ?? "zh-CN";
                var isZh = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                FindMenuByTag("zh-CN")?.SetCurrentValue(MenuItem.IsCheckedProperty, isZh);
                FindMenuByTag("en-US")?.SetCurrentValue(MenuItem.IsCheckedProperty, !isZh);

                var cache = AppSettingsService.Current.CacheDirectory;
                var cacheItem = FindMenuByTag("cachedir");
                if (cacheItem != null)
                {
                    // ⚠ 这里直接赋值会覆盖 XAML 里的 {ext:Loc} 绑定 ⇒ 必须靠
                    // 「语言切换后重新调用本方法」来保证它是新语言（LangMenu_Click 已如此）。
                    var loc = Services.LocalizationService.Instance;
                    cacheItem.Header = string.IsNullOrWhiteSpace(cache)
                        ? loc["settings.cache.set"]
                        : string.Format(loc["settings.cache.current"], cache);
                }
            }
            catch { /* 回显失败不影响功能 */ }
        }

        private static string DescribeRenderMode(string mode) => mode switch
        {
            "angle" => "GPU：ANGLE / D3D11",
            "vulkan" => "GPU：Vulkan",
            "software" => "CPU 软件渲染",
            _ => "自动（推荐）"
        };

        private async void BrowseCacheDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "选择缓存/临时文件目录",
                AllowMultiple = false
            });
            if (result.Count > 0)
            {
                var dir = result[0].Path.LocalPath;
                if (CacheDirBox != null) CacheDirBox.Text = dir;
                AppSettingsService.Current.CacheDirectory = dir;
                AppSettingsService.Save();
            }
        }

        private void ClearCacheDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (CacheDirBox != null) CacheDirBox.Text = "";
            AppSettingsService.Current.CacheDirectory = null;
            AppSettingsService.Save();
        }

        private void RedetectTools_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            AppSettingsService.Current.JxlLibDir = null;
            AppSettingsService.Current.WindowsArtifactsDir = null;
            AppSettingsService.Current.ExifToolPath = null;
            AppSettingsService.Current.PhotoshopPath = null;
            AppSettingsService.Save();
            if (JxlLibDirBox != null) JxlLibDirBox.Text = "";
            if (ArtifactsDirBox != null) ArtifactsDirBox.Text = "";
            if (ExifToolPathBox != null) ExifToolPathBox.Text = "";

            if (LogText != null) LogText.Text += "正在重新自动检测外部工具...\n";
            RefreshJxlServices();
            RefreshArtifactsServices();
            ExifToolService.ClearCache();   // 用户主动入口（浏览/清除/重新检测）⇒ 必须绕开「一次进程一遍」的门控
            ExifToolService.Detect(m => { if (LogText != null) LogText.Text += m; }, force: true);
            UpdateExifToolPanelState();
            RefreshPsPanelState();
            // ⚠ 旧写法到此就结束 ⇒ **顶部状态行不刷新**：用户按了"重新检测",折叠态的 ✅/❌ 与版本段
            //   还停在上一轮（`RefreshToolsStatusBar` 才是那 8 项的唯一出口）。
            //   另外"重新检测"= 环境可能变了 ⇒ GPU 报告与格式能力这两处以 ffmpeg 身份为隐含键的
            //   静态缓存必须一起作废。PLAN 目录的同理：`_planScanned` 是一次性闩,不复位
            //   就等于"整个便携包被挪了位置,重新检测仍去旧 PLAN 里找"。
            GpuCapabilityService.ClearCache();
            FormatCapabilitiesService.ClearCache();
            PlatformServices.ResetPlanFolderCache();
            PlatformServices.ResetToolLaunchabilityCache();   // 否则一次误判会在本进程里永久化（用户就是来重试的）
            RefreshToolsStatusBar();
            RegenerateCommand();
        }

        /// <summary>刷新顶部工具状态指示器（折叠态 + 展开态详情）</summary>
        private void RefreshToolsStatusBar()
        {
            // ── 折叠态：一行显示所有工具状态 ──
            if (ToolsStatusBar != null)
            {
                ToolsStatusBar.Children.Clear();
                // JXL 类
                AddToolStatus(CjxlService.IsAvailable, "cjxl");
                AddToolStatus(DjxlService.IsAvailable, "djxl");
                AddToolStatus(CjpegliService.IsAvailable, "cjpegli");
                AddSeparator();
                // exiftool
                AddToolStatus(ExifToolService.IsAvailable, "exiftool");
                AddSeparator();
                // artifacts 类
                AddToolStatus(JxrService.IsAvailable, "jxr");
                AddToolStatus(PlatformServices.ResolveAvifencPath() != null, "avifenc");
                AddToolStatus(RawService.IsAvailable, "dngtool");
            }

            // ── 展开态：3 列详细状态 ──
            PopulateCategoryTools(JxlToolsStatus, new[]
            {
                ("cjxl", CjxlService.IsAvailable),
                ("djxl", DjxlService.IsAvailable),
                ("cjpegli", CjpegliService.IsAvailable),
            });
            PopulateCategoryTools(ExifToolToolsStatus, new[]
            {
                ("exiftool", ExifToolService.IsAvailable),
            });
            PopulateCategoryTools(ArtifactsToolsStatus, new[]
            {
                ("jxr", JxrService.IsAvailable),
                ("avifenc", PlatformServices.ResolveAvifencPath() != null),
                ("dngtool", RawService.IsAvailable),
            });
            PopulateCategoryTools(PsToolsStatus, new[]
            {
                ("photoshop", PsRenderService.IsAvailable),
            });

            // 检测完成后显示紧凑状态栏
            if (ToolsCompactPanel != null) ToolsCompactPanel.IsVisible = true;
        }

        private void PopulateCategoryTools(StackPanel? panel, (string name, bool available)[] tools)
        {
            if (panel == null) return;
            panel.Children.Clear();
            foreach (var (name, ok) in tools)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"{(ok ? "✅" : "❌")} {name}",
                    FontSize = 10,
                    Foreground = Avalonia.Media.Brushes.Gray,
                    Margin = new Avalonia.Thickness(0, 0, 2, 0)
                });
            }
        }

        private void AddToolStatus(bool available, string name)
        {
            if (ToolsStatusBar == null) return;
            ToolsStatusBar.Children.Add(new TextBlock
            {
                Text = $"{(available ? "✅" : "❌")} {name}",
                FontSize = 10,
                Foreground = Avalonia.Media.Brushes.Gray,
                Margin = new Avalonia.Thickness(0, 0, 4, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
        }

        private void AddSeparator()
        {
            if (ToolsStatusBar == null) return;
            ToolsStatusBar.Children.Add(new TextBlock
            {
                Text = "│",
                FontSize = 10,
                Foreground = Avalonia.Media.Brushes.LightGray,
                Margin = new Avalonia.Thickness(2, 0, 2, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
        }

        // ⚠ P7f：UI 不再自带“avifenc 是否可用”的副本（曾经这里是第三份拷贝，且漏了手动 AvifencPath）。
        //   下面两处状态展示直接问全局唯一入口 PlatformServices.ResolveAvifencPath()。

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
                if (MediaInfoText != null) MediaInfoText.Text = Services.LocalizationService.Instance["media.loading"];
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
        /// <summary>判断当前选择的色彩空间是否为 HDR（BT.2020 PQ/HLG 或 P3 PQ 或高级参数 PQ/HLG 传输函数）。
        /// Gain Map (Ultra HDR) 仅在 HDR 色彩下有意义（记录 HDR 高光扩展），SDR 内容 headroom=1 无增益。</summary>
        /// <summary>GainMap 独立开关变化：切换子选项面板可见性 + 刷新色彩面板（仅CICP 例外）与命令。</summary>
        private void HdrMode_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var on = HdrModeCombo?.SelectedIndex == 1;
            if (JpegGainMapPanel != null)
                JpegGainMapPanel.IsVisible = on
                    && (FormatCombo?.SelectedItem as string)?.ToLowerInvariant() is "jpg" or "jpeg";
            UpdateIccPanelVisibility();
            // ── D-1 同族修复（2026-09-20）：刷新会**冲掉**用户已设的目标色域 / 位深 / 质量 ──────────
            // 缺陷：`UpdateOptionAvailability()` 会 `Items.Clear()` + 重建 `ColorSpaceCombo`（见 `UpdateOptionAvailability()` 内那几行）
            //   与 `BitDepthCombo` 并把 `SelectedIndex` 归 0，还会把 `QualitySlider.Value`
            //   设为该格式默认值（同见该方法）。⇒ 用户勾/取消「GainMap (Ultra HDR)」时**格式并未改变**，
            //   这三项却被重置（与 D-1 的「应用预设后三项留不住」**同族**，`§11.4` 登记的残留即此）。
            // 修法：**刷新前快照、刷新后回写** —— 与 `ApplyPresetData` 的 D-1 修法**同一模式**。
// ⚠ 只改**本处理器**：刷新函数本体与另外 5 个调用点（能力检测后的 UI 刷新回调、
//   格式切换 `FormatCombo_SelectionChanged`、动图模式 `ConversionMode_SelectionChanged`、
//   能力初始化 `InitializeCapabilitiesAsync`、简易预设 `SimplePresetCombo_SelectionChanged`）
//   行为**一字不变**（D-1 明确要求，避免动到「切格式 ⇒ 重建候选集」的既有语义）。
//   ⚠ 2026-09-21 改：原先此处写的是**绝对行号**（`:1275`/`:1851`/`:1948`/`:2611`/`:4604`）
//     ⇒ 插入任何行都会失效（`COLOR_TRAPS §十四`）⇒ 改为**具名 handler**。
            // ⚠ 回写一律走 `SetComboByValue`：值若不在新候选集里（例如**关掉** GainMap 后 HDR 色域项消失）
            //   就**保持刷新后的默认值**（= 今天的行为），绝不写进非法值。
            var keepColorSpace = ColorSpaceCombo?.SelectedItem as string;
            var keepBitDepth = BitDepthCombo?.SelectedItem as string;
            var keepQuality = QualitySlider?.Value ?? double.NaN;
            var keepFmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            UpdateOptionAvailability();   // 开关会改变 JPEG 的 HDR 能力⇒ 目标色域列表需重新过滤
            // ⚠ 顺序：色域先、位深后。HDR 色域会触发位深自动联动（`ColorSpaceCombo_SelectionChanged:3440`）
            //   ⇒ 后写位深 = 用户的显式位深是**最后一次写入**（与「用户选择即终态」的语义一致）。
            SetComboByValue(ColorSpaceCombo, keepColorSpace);
            SetComboByValue(BitDepthCombo, keepBitDepth);
            // ⚠ 纯无损格式（PNG/TIFF/APNG）的质量被**产品语义**锁定 100（见本文件中「PNG/TIFF/APNG 纯无损格式，强制勾选且锁定」那段），不得回写（同 D-1）。
            if (QualitySlider != null && !double.IsNaN(keepQuality)
                && keepFmt is not ("png" or "tiff" or "apng"))
            {
                QualitySlider.Value = keepQuality;
            }
            UpdateQualityLabel();
            RegenerateCommand();
        }

        private bool IsHdrColorSelected()
        {
            var cs = ColorSpaceCombo?.SelectedItem as string;
            if (cs is "BT.2020 PQ" or "BT.2020 HLG" or "P3 PQ") return true;
            if (UseAdvancedColor?.IsChecked == true)
            {
                var trc = ColorTrcCombo?.SelectedItem as string;
                if (trc is "smpte2084" or "arib-std-b67") return true;
            }
            return false;
        }

        /// <summary>解析增益图类型：0=灰度(1通道), 1=RGB(3通道)。回退到旧 CheckBox 语义。</summary>
        private bool ParseGainMapMultiChannel()
        {
            if (JpegGainMapTypeCombo != null)
                return JpegGainMapTypeCombo.SelectedIndex == 1;
            // 兼容：旧 CheckBox（XAML 已移除，此路径仅旧预设/旧二进制）
            return JpegGainMapMultiChannelCheck?.IsChecked ?? false;
        }

        /// <summary>解析底图色域：0=sRGB SDR 底（默认）, 1=Rec.2020 SDR 底（广色域）；HDR 始终由增益图承载。</summary>
        private string ParseGainMapBaseGamut()
            => JpegGainMapBaseCombo?.SelectedIndex == 1 ? "bt2020" : "srgb";

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
                0 => 1,    // 满分辨率
                2 => 4,    // 1/4
                3 => 8,    // 1/8
                4 => 16,   // 1/16
                _ => 2     // 1/2 (默认)
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
            if (current < 0) current = 75; // 从「跟随主图」切换到手动时，默认 75（⚠ 引号用「」：直引号会被 _probe-cjk-hardcode-scan.ps1 误计成硬编码文案，见该脚本口径②）
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

        private async void OpenPresetManager_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var win = new PresetManagerWindow();
            win.CurrentSettings = BuildPresetData();
            await win.ShowDialog(this);

            if (win.AppliedPreset != null)
            {
                ApplyPresetData(win.AppliedPreset);
                RegenerateCommand();
                if (LogText != null) LogText.Text += "[预设] 已应用预设配置\n";
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
                ColorRange = MapColorRangeValue(ColorRangeCombo?.SelectedItem as string),
                TonemapCurve = null, // tonemap 曲线内部固定 hable
                BitDepth = BitDepthCombo?.SelectedItem as string,
                AutoThreads = AutoThreadsCheck?.IsChecked ?? true,
                SingleThread = SingleThreadCheck?.IsChecked ?? false,
                ManualThreads = (int)(ThreadsBox?.Value ?? 4),
                MetadataMode = MetadataModeCombo?.SelectedIndex == 1 ? "StripAll" : "PreserveAll",
                Lossless = LosslessCheck?.IsChecked ?? false,
                UseAdvancedCodec = UseAdvancedCodec?.IsChecked ?? false,
                PngPred = PngPredCombo?.SelectedIndex >= 0
                    ? _pngPredValues[Math.Min(PngPredCombo.SelectedIndex, _pngPredValues.Length - 1)]
                    : null,
                PngDpi = PngDpiBox?.Value > 0 ? (int?)PngDpiBox.Value : null,
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
                JxlPreserveUltrahdr = JxlPreserveUltrahdrCheck?.IsChecked ?? true,
                JxlLosslessJpeg = JxlLosslessJpegCheck?.IsChecked ?? true,
                TiffCompressionAlgo = TiffCompressionCombo?.SelectedItem as string,
                // ── 编码器后端 ──
                EncoderBackend = GetCurrentEncoderBackend().ToString(),
                // ── Gain Map ──
                JpegGainMap = HdrModeCombo?.SelectedIndex == 1,
                JpegGainMapQuality = ParseGainMapQuality(),
                JpegGainMapTargetNits = ParseGainMapNits(),
                JpegGainMapMultiChannel = ParseGainMapMultiChannel(),
                JpegGainMapBaseGamut = ParseGainMapBaseGamut(),
                JpegGainMapDownsample = ParseGainMapDownsample(),
                // ── WebP 无损压缩级别 ──
                WebpCompressionLevel = (int?)WebpCompressionBox?.Value,
                // ── AVIF 扩展 ──
                AvifSvtPreset = (int?)SvtPresetBox?.Value,
                AvifSvtTune = SvtTuneCombo?.SelectedItem as string,
                AvifHwPreset = HwPresetCombo?.SelectedItem as string,
                AvifHwPresetLevel = GetAvifHwPresetLevel(),
                AvifRowMt = AvifRowMtCheck?.IsChecked,
                // ── libaom-av1 高级图像 ──
                AvifAqMode = ParseAvifAqMode(),
                AvifEnableCdef = AvifCdefCheck?.IsChecked,
                AvifEnableIntrabc = AvifIntrabcCheck?.IsChecked,
                AvifDenoiseLevel = AvifDenoiseBox?.Value > 0 ? (int?)AvifDenoiseBox.Value : null,
                // ── NVENC 高级 ──
                AvifNvencAqStrength = (int?)AvifNvencAqBox?.Value,
                AvifNvencSpatialAq = AvifNvencSpatialAqCheck?.IsChecked,
                // ── QSV/VAAPI ──
                AvifLowPower = AvifQsvLowPowerCheck?.IsChecked ?? AvifVaapiLowPowerCheck?.IsChecked,
                // ── cjpegli 扩展 ──
                CjpegliChromaSubsampling = JpegliChromaCombo?.SelectedItem as string,
                CjpegliProgressiveId = JpegliProgressiveCombo?.SelectedIndex switch { 1 => 0, 2 => 2, _ => -1 },
                CjpegliOptimize = JpegliOptimizeCheck?.IsChecked,
                CjpegliAdaptiveQuant = JpegliAdaptiveQuantCheck?.IsChecked,
                CjpegliEncoderBackend = JpegliEncoderBackendCombo?.SelectedIndex == 1 ? "sjpeg" : "libjpeg",
                CjpegliPsnrTarget = (float)(JpegliPsnrBox?.Value ?? 0),
                // ── cjxl 高级 ──
                CjxlProgressive = CjxlProgressiveCheck?.IsChecked ?? false,
                CjxlPhotonNoiseIso = (int)(CjxlPhotonNoiseBox?.Value ?? 0),
                CjxlAutoPhotonNoise = CjxlAutoPhotonNoiseCheck?.IsChecked ?? false,
                StripExifGps = StripExifGpsCheck?.IsChecked ?? true,
                StripExifTime = StripExifTimeCheck?.IsChecked ?? false,
                StripExifCamera = StripExifCameraCheck?.IsChecked ?? false,
                StripExifAll = StripExifAllCheck?.IsChecked ?? false,
                StripXmp = StripXmpCheck?.IsChecked ?? false,
                AppendPngExtension = AppendPngExtCheck?.IsChecked ?? false,
                // ── DNG 输出选项 ──
                DngCompression = DngCompressionCombo?.SelectedIndex == 1 ? 1 : 0,
                DngJxlQuality = ParseInt(DngJxlQualityBox?.Text, 0, 0, 100),
                DngLinear = DngLinearCombo?.SelectedIndex == 1,
                DngJxlEffort = (int)(DngJxlEffortBox?.Value ?? 7),
                DngJxlDecodeSpeed = (int)(DngJxlDecodeSpeedBox?.Value ?? 4),
                DngHighlightMode = DngHighlightCombo?.SelectedIndex ?? 1,
                DngBitDepth = DngBitDepthCombo?.SelectedIndex == 0 ? 8 : 16,
                // ── Snapshot backfill: axes BuildPresetData never wrote (2026-09-26) ──
                // Without these the round trip Build -> Apply silently dropped them, and since
                // simple mode now enqueues from the advanced-mode UI state, a preset that cannot
                // express an axis no longer reproduces the state it was saved from.
                ConversionMode = ConversionModeCombo?.SelectedIndex,
                EncoderName = EncoderCombo?.SelectedItem as string,
                ColorGamutMap = GetColorGamutMap(),
                RawColorTarget = RawColorTargetCombo?.SelectedItem as string,
                CjxlEffort = (int?)CjxlEffortBox?.Value,
                TiffDpi = TiffDpiBox?.Value > 0 ? (int?)TiffDpiBox.Value : null,
                EnableThumbnail = EnableThumbnailCheck?.IsChecked ?? false,
                ThumbnailLongEdge = ThumbnailSizeBox?.Value > 0 ? (int?)ThumbnailSizeBox.Value : null,
                ThumbnailQuality = ThumbnailQualityBox?.Value > 0 ? (int?)ThumbnailQualityBox.Value : null,
                AnimationFps = ParseOptionalInt(AnimationFpsBox?.Text, 1, 60),
                AnimationLoop = ParseInt(AnimationLoopBox?.Text, 0, -1, 999),
                AnimationScaleW = ParseOptionalInt(AnimationScaleWBox?.Text, 0, 4096) ?? 0,
                AnimationDuration = ParseOptionalDouble(AnimationDurationBox?.Text, 0.1, 3600) ?? 0,
                GifPaletteOptimize = GifPaletteCheck?.IsChecked ?? true,
                GifDither = GifDitherCheck?.IsChecked ?? true,
                EnableMaxDimension = EnableMaxDimensionCheck?.IsChecked ?? false,
                MaxDimension = (int?)MaxDimensionBox?.Value ?? 1920,
                // ── 色彩策略 ──
                // 必须**同时**写两个字段（实测缺陷，2026-09-19）：
                //   · `ColorStrategy` 是新预设的**真值**（PresetData.cs:144-145）；
                //   · `IccMode` 是**遗留**字段（PresetData.cs:142-143），保留它旧版本应用仍能读新预设（向后兼容）。
                // 此前只写 IccMode ⇒ 载入侧 FfmpegOptions.ApplyPresetData（FfmpegOptions.cs:135-138）先读
                // ColorStrategy（为空）再回退 FromIccMode(IccMode)，而两处对同名按钮语义互斥
                // （GetIccMode:2514-2520 vs GetColorStrategy:2523-2529）⇒ 中间两个模式往返错位：
                //   IccModeBake(BakeCicpOnly) 存 BakeToStandard → 载入 Recommended
                //   IccModeBakeOnly(Manual)   存 BakeOnly       → 载入 BakeCicpOnly
                // ⚠ 取值必须是 ColorStrategyMapper.TryParse 认得的**规范串**（PresetData.cs:144 自述格式
                //   recommended/carry/cicp-only/manual），**不能**用 `Enum.ToString()`：实测
                //   `BakeCicpOnly` 不在 TryParse 的别名表内（ColorStrategy.cs:56-71），ToString() 写出的
                //   "BakeCicpOnly" 会被 TryParse 拒绝 ⇒ 又回退 IccMode 迁移 ⇒ IccModeBake 仍然错位。
                ColorStrategy = GetColorStrategy() switch
                {
                    Models.ColorStrategy.CarryIcc => "carry-icc",
                    Models.ColorStrategy.BakeCicpOnly => "cicp-only",
                    Models.ColorStrategy.Manual => "manual",
                    _ => "recommended",
                },
                IccMode = GetIccMode().ToString(),
                IccFilePath = null, // 新模式不使用外部 ICC
                // (源色彩空间已改为自动 CICP+ICC 检测)
                // (烘焙目标取主色域下拉，旧 IccTargetColorSpace 字段已移除)
                Concurrency = GetConcurrencyValue(),
                MaxQueueSize = GetConcurrencyValue()
            };
        }

        private void ApplyPresetData(Models.PresetData p)
        {
            // Mode first: ConversionMode_SelectionChanged rebuilds FormatCombo's item list
            // (still / animated / RAW carry disjoint format sets), so a format written before the
            // mode would be dropped when that rebuild runs. null = preset predates the axis.
            var presetMode = p.ConversionMode
                ?? (string.IsNullOrWhiteSpace(p.Format) ? null : ModeOfPresetFormat(p.Format));
            if (presetMode.HasValue && ConversionModeCombo != null
                && ConversionModeCombo.SelectedIndex != presetMode.Value)
            {
                ConversionModeCombo.SelectedIndex = presetMode.Value;
            }
            SetComboByValue(FormatCombo, p.Format);
            if (QualitySlider != null && p.Quality >= 0) QualitySlider.Value = p.Quality;
            SetComboByValue(ChromaCombo, p.Chroma);
            SetComboByValue(BitDepthCombo, p.BitDepth);
            SetComboByValue(ColorSpaceCombo, p.ColorSpace);
            if (UseAdvancedColor != null) UseAdvancedColor.IsChecked = p.UseAdvancedColor;
            SetComboByValue(ColorPrimariesCombo, p.ColorPrimaries);
            SetComboByValue(ColorTrcCombo, p.ColorTrc);
            SetComboByValue(ColorMatrixCombo, p.ColorMatrix);
            SetColorRangeCombo(p.ColorRange);
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
            SetComboByValueOrIndex(PngPredCombo, p.PngPred, _pngPredValues);
            if (PngDpiBox != null && p.PngDpi.HasValue) PngDpiBox.Value = p.PngDpi.Value;
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
            if (JxlPreserveUltrahdrCheck != null) JxlPreserveUltrahdrCheck.IsChecked = p.JxlPreserveUltrahdr;
            if (JxlLosslessJpegCheck != null) JxlLosslessJpegCheck.IsChecked = p.JxlLosslessJpeg;
            // ── Gain Map (Ultra HDR) ──
            if (JpegGainMapTypeCombo != null)
                JpegGainMapTypeCombo.SelectedIndex = p.JpegGainMapMultiChannel ? 1 : 0;
            if (JpegGainMapBaseCombo != null)
                JpegGainMapBaseCombo.SelectedIndex =
                    string.Equals(p.JpegGainMapBaseGamut, "bt2020", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (JpegGainMapFollowMainCheck != null)
            {
                JpegGainMapFollowMainCheck.IsChecked = p.JpegGainMapQuality < 0;
                if (JpegGainMapQualityPanel != null)
                    JpegGainMapQualityPanel.IsVisible = p.JpegGainMapQuality >= 0;
                if (JpegGainMapQualityBox != null && p.JpegGainMapQuality >= 0)
                    JpegGainMapQualityBox.Text = p.JpegGainMapQuality.ToString();
            }
            if (JpegGainMapNitsBox != null && p.JpegGainMapTargetNits > 0)
                JpegGainMapNitsBox.Text = p.JpegGainMapTargetNits.ToString();
            if (JpegGainMapDownsampleCombo != null)
                JpegGainMapDownsampleCombo.SelectedIndex = p.JpegGainMapDownsample switch
                {
                    1 => 0, 2 => 1, 4 => 2, 8 => 3, 16 => 4, _ => 1
                };
            SetComboByValue(TiffCompressionCombo, p.TiffCompressionAlgo);
            if (TiffDpiBox != null && p.TiffDpi.HasValue) TiffDpiBox.Value = p.TiffDpi.Value;
            if (EnableThumbnailCheck != null) EnableThumbnailCheck.IsChecked = p.EnableThumbnail;
            if (ThumbnailSizeBox != null && p.ThumbnailLongEdge.HasValue) ThumbnailSizeBox.Value = p.ThumbnailLongEdge.Value;
            if (ThumbnailQualityBox != null && p.ThumbnailQuality.HasValue) ThumbnailQualityBox.Value = p.ThumbnailQuality.Value;
            // ── DNG 输出选项恢复 ──
            if (DngCompressionCombo != null)
                DngCompressionCombo.SelectedIndex = p.DngCompression == 1 ? 1 : 0;
            if (DngJxlQualityBox != null)
                DngJxlQualityBox.Text = p.DngJxlQuality.ToString();
            if (DngLinearCombo != null)
                DngLinearCombo.SelectedIndex = p.DngLinear ? 1 : 0;
            if (DngJxlEffortBox != null)
                DngJxlEffortBox.Value = Math.Clamp(p.DngJxlEffort, 1, 9);
            if (DngJxlDecodeSpeedBox != null)
                DngJxlDecodeSpeedBox.Value = Math.Clamp(p.DngJxlDecodeSpeed, 1, 4);
            if (DngHighlightCombo != null)
                DngHighlightCombo.SelectedIndex = Math.Clamp(p.DngHighlightMode, 0, 2);
            if (DngBitDepthCombo != null)
                DngBitDepthCombo.SelectedIndex = p.DngBitDepth <= 8 ? 0 : 1;
            // ── 编码器后端 ──
            // Exact preset selection first: one backend can expose several encoders, and the
            // backend-name substring match below cannot tell them apart.
            if (!string.IsNullOrWhiteSpace(p.EncoderName) && EncoderCombo?.Items != null)
            {
                for (int i = 0; i < EncoderCombo.Items.Count; i++)
                {
                    if (string.Equals(EncoderCombo.Items[i] as string, p.EncoderName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        EncoderCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(p.EncoderBackend) && EncoderCombo != null)
            {
                // 在 EncoderCombo 中查找包含后端名称的项
                for (int i = 0; i < EncoderCombo.Items!.Count; i++)
                {
                    var item = EncoderCombo.Items[i] as string ?? "";
                    if (item.Contains(p.EncoderBackend, StringComparison.OrdinalIgnoreCase)
                        || (p.EncoderBackend == "Ffmpeg" && item.Contains("FFmpeg")))
                    {
                        EncoderCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            // ── WebP 无损压缩级别 ──
            if (WebpCompressionBox != null && p.WebpCompressionLevel.HasValue)
                WebpCompressionBox.Value = p.WebpCompressionLevel.Value;
            // ── AVIF 扩展选项 ──
            if (SvtPresetBox != null && p.AvifSvtPreset.HasValue)
                SvtPresetBox.Value = p.AvifSvtPreset.Value;
            SetComboByValue(SvtTuneCombo, p.AvifSvtTune);
            SetComboByValue(HwPresetCombo, p.AvifHwPreset);
            if (AvifRowMtCheck != null && p.AvifRowMt.HasValue)
                AvifRowMtCheck.IsChecked = p.AvifRowMt.Value;
            // ── libaom-av1 高级图像 ──
            if (AvifAqModeCombo != null && !string.IsNullOrWhiteSpace(p.AvifAqMode))
                AvifAqModeCombo.SelectedIndex = p.AvifAqMode switch { "variance" => 1, "complexity" => 2, _ => 0 };
            if (AvifCdefCheck != null && p.AvifEnableCdef.HasValue) AvifCdefCheck.IsChecked = p.AvifEnableCdef.Value;
            if (AvifIntrabcCheck != null && p.AvifEnableIntrabc.HasValue) AvifIntrabcCheck.IsChecked = p.AvifEnableIntrabc.Value;
            if (AvifDenoiseBox != null && p.AvifDenoiseLevel.HasValue) AvifDenoiseBox.Value = p.AvifDenoiseLevel.Value;
            // ── NVENC ──
            if (NvencPresetCombo != null && p.AvifHwPresetLevel >= 1)
                NvencPresetCombo.SelectedIndex = Math.Clamp(p.AvifHwPresetLevel - 1, 0, 6);
            if (AvifNvencAqBox != null && p.AvifNvencAqStrength.HasValue) AvifNvencAqBox.Value = p.AvifNvencAqStrength.Value;
            if (AvifNvencSpatialAqCheck != null && p.AvifNvencSpatialAq.HasValue) AvifNvencSpatialAqCheck.IsChecked = p.AvifNvencSpatialAq.Value;
            // ── QSV/VAAPI ──
            if (QsvPresetCombo != null && p.AvifHwPresetLevel >= 1)
                QsvPresetCombo.SelectedIndex = Math.Clamp(p.AvifHwPresetLevel - 1, 0, 6);
            if (VaapiPresetCombo != null && p.AvifHwPresetLevel >= 1)
                VaapiPresetCombo.SelectedIndex = Math.Clamp(p.AvifHwPresetLevel - 1, 0, 6);
            if (AmfPresetCombo != null && p.AvifHwPresetLevel >= 1)
                AmfPresetCombo.SelectedIndex = Math.Clamp(p.AvifHwPresetLevel <= 2 ? 0 : p.AvifHwPresetLevel <= 5 ? 1 : 2, 0, 2);
            if (AvifQsvLowPowerCheck != null && p.AvifLowPower.HasValue) AvifQsvLowPowerCheck.IsChecked = p.AvifLowPower.Value;
            if (AvifVaapiLowPowerCheck != null && p.AvifLowPower.HasValue) AvifVaapiLowPowerCheck.IsChecked = p.AvifLowPower.Value;
            // ── cjpegli 扩展选项 ──
            SetComboByValue(JpegliChromaCombo, p.CjpegliChromaSubsampling);
            if (JpegliProgressiveCombo != null)
            {
                JpegliProgressiveCombo.SelectedIndex = p.CjpegliProgressiveId switch
                {
                    0 => 1,  // 基线
                    2 => 2,  // 渐进
                    _ => 0   // 自动
                };
            }
            if (JpegliOptimizeCheck != null && p.CjpegliOptimize.HasValue)
                JpegliOptimizeCheck.IsChecked = p.CjpegliOptimize.Value;
            if (JpegliAdaptiveQuantCheck != null && p.CjpegliAdaptiveQuant.HasValue)
                JpegliAdaptiveQuantCheck.IsChecked = p.CjpegliAdaptiveQuant.Value;
            // ── 2026-08-16 补齐: cjpegli 编码后端 / PSNR 目标 / cjxl 高级选项 / TIFF DPI ──
            if (JpegliEncoderBackendCombo != null)
                JpegliEncoderBackendCombo.SelectedIndex = p.CjpegliEncoderBackend == "sjpeg" ? 1 : 0;
            if (JpegliPsnrBox != null) JpegliPsnrBox.Value = (decimal)p.CjpegliPsnrTarget;
            if (CjxlProgressiveCheck != null) CjxlProgressiveCheck.IsChecked = p.CjxlProgressive;
            if (CjxlAutoPhotonNoiseCheck != null) CjxlAutoPhotonNoiseCheck.IsChecked = p.CjxlAutoPhotonNoise;
            if (CjxlPhotonNoiseBox != null) CjxlPhotonNoiseBox.Value = Math.Clamp(p.CjxlPhotonNoiseIso, 0, 3200);
            if (ConcurrencyBox != null) ConcurrencyBox.Text = Math.Clamp(p.MaxQueueSize, 1, 128).ToString();
            UpdateConcurrencyLabel();
            // ── Snapshot backfill replay (2026-09-26) ────────────────────────────────────────
            // Controls BuildPresetData now captures but this method never restored, so a preset
            // silently left them at whatever the user had. Placed before the color-strategy radio
            // block because handlers fired here (gain map enable, max dimension) rebuild panels and
            // can move that radio -- the strategy write must stay the last one.
            if (ColorGamutMapCheck != null && !string.IsNullOrWhiteSpace(p.ColorGamutMap))
                ColorGamutMapCheck.IsChecked =
                    string.Equals(p.ColorGamutMap, "on", StringComparison.OrdinalIgnoreCase);
            // RAW 输出色域模式：只在预设**带了值**时回写（与上面 ColorGamutMap 同口径）——
            // 旧预设无此字段 ⇒ 不动控件，避免把用户当前选择悄悄重置成 rec2020。
            // ⚠ 预设里可能是别名（bt2020/bt709/srgb）⇒ 先过规范化器再落到下拉项。
            if (RawColorTargetCombo != null && !string.IsNullOrWhiteSpace(p.RawColorTarget))
            {
                var wantRaw = Services.ColorSpaceRegistry.NormalizeRawColorTargetToken(p.RawColorTarget) ?? "rec2020";
                RawColorTargetCombo.SelectedItem = wantRaw is "auto" or "rec709" ? wantRaw : "rec2020";
            }
            if (CjxlEffortBox != null && p.CjxlEffort.HasValue)
                CjxlEffortBox.Value = Math.Clamp(p.CjxlEffort.Value, 1, 9);
            if (GifPaletteCheck != null) GifPaletteCheck.IsChecked = p.GifPaletteOptimize;
            if (GifDitherCheck != null) GifDitherCheck.IsChecked = p.GifDither;
            if (AnimationFpsBox != null && p.AnimationFps.HasValue)
                AnimationFpsBox.Text = p.AnimationFps.Value.ToString();
            if (AnimationLoopBox != null && p.AnimationLoop.HasValue)
                AnimationLoopBox.Text = p.AnimationLoop.Value.ToString();
            if (AnimationScaleWBox != null) AnimationScaleWBox.Text = p.AnimationScaleW.ToString();
            if (AnimationDurationBox != null && p.AnimationDuration > 0)
                AnimationDurationBox.Text = p.AnimationDuration.ToString("0.###");
            if (EnableMaxDimensionCheck != null) EnableMaxDimensionCheck.IsChecked = p.EnableMaxDimension;
            if (MaxDimensionBox != null && p.MaxDimension > 0) MaxDimensionBox.Value = p.MaxDimension;
            if (AppendPngExtCheck != null) AppendPngExtCheck.IsChecked = p.AppendPngExtension;
            if (HdrModeCombo != null) HdrModeCombo.SelectedIndex = p.JpegGainMap ? 1 : 0;
            SetComboByValue(JpegDctCombo, p.JpegDct);
            // ── 色彩策略单选恢复（2026-09-19 修复）──────────────────────────────────
            // 缺陷：保存侧已写规范串 ColorStrategy（BuildPresetData 内），但本方法此前**整块缺失**
            //   对 IccMode*/ColorStrategy 的引用（引用数 = 0）⇒「应用预设」后 4 个策略单选保持原样，
            //   下一次保存/编码用的是**旧策略**，预设形同虚设（违反验收面「四大模式正确」）。
            // 语义与 FfmpegOptions.ApplyPresetData（FfmpegOptions.cs:135-138）**逐条同构**：
            //   ① 优先规范串 ColorStrategyMapper.TryParse(p.ColorStrategy)（大小写/别名不敏感）；
            //   ② 否则用遗留 IccMode 迁移：Enum.TryParse<IccMode> + FromIccMode
            //      （None 与 BakeToStandard 同归 Recommended 是**有意的等价类**，不在此"修"）；
            //   ③ 两者都不可用 ⇒ **保持当前单选不变**，并在日志里**点名**两个字段的原始取值。
            //      本仓价值序：静默 < 响亮但看不懂 < 响亮且点名 ⇒ 绝不静默改写为 Recommended。
            // ⚠ 日志串**保持 ASCII**：`_probe-cjk-hardcode-scan.ps1` 的判据是「UI 面硬编码中文行
            //   不得增加」，而 `LogText.Text +=` 落进它认不出的 sink ⇒ fail-closed 计为 UI 面；
            //   本处是**新增**行且该锁余量为 0，用中文会立刻把它顶红。
            // ⚠ 位置：必须在所有 ComboBox 恢复（含 ColorSpaceCombo）**之后** —— 色域联动
            //   AutoLinkIccModeFromColorSpace 会改单选，本块须是最后一次写入。
            if (Models.ColorStrategyMapper.TryParse(p.ColorStrategy, out var presetStrategy))
            {
                SetColorStrategyRadio(presetStrategy);
            }
            else if (!string.IsNullOrWhiteSpace(p.IccMode)
                     && Enum.TryParse<Models.IccMode>(p.IccMode, true, out var presetIccMode))
            {
                SetColorStrategyRadio(Models.ColorStrategyMapper.FromIccMode(presetIccMode));
            }
            else if (LogText != null)
            {
                LogText.Text += "[preset] color strategy unresolved: ColorStrategy='"
                    + (p.ColorStrategy ?? "<null>") + "', IccMode='" + (p.IccMode ?? "<null>")
                    + "' -- current strategy radio kept unchanged\n";
            }
            UpdateOptionAvailability();

            // ── D-1 修复（2026-09-19）：刷新**之后**再落一次「随格式变化的控件」──────────────
            // 缺陷：`UpdateOptionAvailability()` 会 `Items.Clear()` + 重建 `ColorSpaceCombo` /
            //   `BitDepthCombo` 并把 `SelectedIndex` 归 0，还会把 `QualitySlider.Value` 设为该格式
            //   默认值 ⇒ 本方法上面写进去的**三项预设值（目标色域 / 位深 / 质量）**被**整体冲掉**
            //   （症状：「应用预设」后目标色域 / 位深 / 质量三项留不住，预设形同虚设）。
// 修法：把这三项**移到刷新之后**再写一次。**不修改刷新函数本体** ⇒ 另外 6 个调用点
//   （格式切换 `FormatCombo_SelectionChanged`、动图模式 `ConversionMode_SelectionChanged`、
//   能力初始化 `InitializeCapabilitiesAsync`、简易预设 `SimplePresetCombo_SelectionChanged`、
//   GainMap 开关 `HdrMode_Changed`）行为**一字不变**，避免动到「切格式 ⇒ 重建候选集」的既有语义。
//   ⚠ 2026-09-21 改：原先此处写的是**绝对行号**（`:1851`/`:1948`/`:2611`/`:4604`/`:5610`），
//     插入任何行都会失效（本仓 `COLOR_TRAPS §十四` 已立规「行号不是稳定标识符」）⇒ 改为**具名 handler**。
            // ⚠ 不得顺手改「PNG/TIFF/APNG 强制无损」那段——那是**既定产品语义**，
            //   被 `UiTestHost` 的 J9a / J14d-1 / L2a-L2d 三条锁住；故质量只在**非**纯无损格式上回写。
            // ⚠ 顺序：色域先、位深后。色域变更会触发 HDR 位深自动联动（`ColorSpaceCombo_SelectionChanged`），
            //   后写位深 = 预设的显式位深为**最后一次写入**（与「预设即终态」的语义一致）。
            // ⚠ 色域回写不会改坏 ICC 单选：`SetColorStrategyRadio` 已置 `_iccModeTouched = true`
            //   ⇒ `AutoLinkIccModeFromColorSpace` 提前返回（见该方法的早退分支）。
            var presetFmt = NormalizeFormat(FormatCombo?.SelectedItem as string);
            SetComboByValue(ColorSpaceCombo, p.ColorSpace);
            SetComboByValue(BitDepthCombo, p.BitDepth);
            if (QualitySlider != null && p.Quality >= 0
                && presetFmt is not ("png" or "tiff" or "apng"))
            {
                QualitySlider.Value = p.Quality;
            }

            UpdateExifToolPanelState();
            UpdateQualityLabel();
        }

        /// <summary>
        /// Conversion mode implied by a preset format string. The three modes carry disjoint format
        /// lists (see ConversionMode_SelectionChanged), so "GIF" only ever selects in animated mode
        /// and "DNG" only in RAW mode. Presets saved before the ConversionMode axis existed rely on
        /// this, otherwise their Format write misses silently against the rebuilt item list.
        /// </summary>
        private static int ModeOfPresetFormat(string? format)
        {
            var f = (format ?? "").Trim();
            if (f.Equals("DNG", StringComparison.OrdinalIgnoreCase)) return 2;
            if (AnimatedFormats.Any(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase))) return 1;
            return 0;
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

        // ── 色彩范围 (TV/PC) 显示文字 ↔ 规范值映射 ──
        // ComboBox 显示带说明（如 "auto（跟随输入）"），内部传递规范值 "auto"/"tv"/"pc"
        private static string MapColorRangeValue(string? display)
        {
            if (string.IsNullOrWhiteSpace(display)) return "auto";
            var t = display.Trim();
            if (t.StartsWith("tv", StringComparison.OrdinalIgnoreCase)) return "tv";
            if (t.StartsWith("pc", StringComparison.OrdinalIgnoreCase)) return "pc";
            return "auto";
        }

        private string GetColorRangeValue()
            => MapColorRangeValue(ColorRangeCombo?.SelectedItem as string);

        private void SetColorRangeCombo(string? value)
        {
            if (ColorRangeCombo == null) return;
            var v = MapColorRangeValue(value);
            for (int i = 0; i < ColorRangeCombo.Items!.Count; i++)
            {
                if (MapColorRangeValue(ColorRangeCombo.Items[i] as string) == v)
                {
                    ColorRangeCombo.SelectedIndex = i;
                    return;
                }
            }
            if (ColorRangeCombo.Items.Count > 0) ColorRangeCombo.SelectedIndex = 0;
        }

        /// <summary>通过值数组匹配设置 ComboBox 索引（用于显示文本≠原始值的情况，如 PNG 预测模式）</summary>
        private static void SetComboByValueOrIndex(ComboBox? combo, string? value, string[] valueArray)
        {
            if (combo == null || value == null) return;
            for (int i = 0; i < valueArray.Length && i < combo.Items!.Count; i++)
            {
                if (valueArray[i].Equals(value, StringComparison.OrdinalIgnoreCase))
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
                        if (MediaInfoText != null) MediaInfoText.Text = Services.LocalizationService.Instance["media.loading"];
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

        /// <summary>显示确认对话框（确定/取消），返回用户是否点击了确定</summary>
        private async Task<bool> ShowConfirmDialogAsync(string title, string message)
        {
            var tcs = new TaskCompletionSource<bool>();

            var dialog = new Window
            {
                Title = title,
                Width = 420,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new Grid
                {
                    RowDefinitions = new RowDefinitions("*,Auto"),
                    Margin = new Avalonia.Thickness(16),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message, FontSize = 13,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            [Grid.RowProperty] = 0,
                            Margin = new Avalonia.Thickness(0, 0, 0, 12)
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 8,
                            [Grid.RowProperty] = 1,
                            Children =
                            {
                                new Button { Content = "确定", Padding = new Avalonia.Thickness(12, 4), IsDefault = true },
                                new Button { Content = "取消", Padding = new Avalonia.Thickness(12, 4), IsCancel = true }
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

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
