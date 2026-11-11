using System.Text.Json;

namespace FfmpegGui.Models
{
    public class PresetData
    {
        public string? Format { get; set; }
        public int Quality { get; set; }
        public string? Chroma { get; set; }
        public string? ColorSpace { get; set; }
        public bool UseAdvancedColor { get; set; }
        public string? ColorPrimaries { get; set; }
        public string? ColorTrc { get; set; }
        public string? ColorMatrix { get; set; }
        /// <summary>输出色彩范围: auto/tv/pc</summary>
        public string? ColorRange { get; set; }
        /// <summary>
        /// 输出矩阵模式（C-TOKEN@1，color-matrix-options 计划）：null = auto（不指定，
        /// 旧预设回放不动）。取值域与 <see cref="FfmpegOptions.ColorOutMatrix"/> 一致
        /// （auto/rgb/bt709/bt601/bt470bg/smpte170m/smpte240m/fcc/bt2020nc/ycgco/xyb）；
        /// bt601 别名与大小写由落模型时的 setter 归一兜底折为规范 token。
        /// </summary>
        public string? ColorOutMatrix { get; set; }
        /// <summary>HDR→SDR tonemap 曲线: hable/mobius/reinhard</summary>
        public string? TonemapCurve { get; set; }
        public string? BitDepth { get; set; }
        public bool AutoThreads { get; set; } = true;
        public bool SingleThread { get; set; }
        public int ManualThreads { get; set; } = 4;
        public string? MetadataMode { get; set; }
        public bool Lossless { get; set; }
        public bool UseAdvancedCodec { get; set; }
        public string? PngPred { get; set; }
        /// <summary>
        /// PNG 出口优化器档位（png-optimizer-integration T4；png-default-oxipng C-DEFOPT@1 加 default 档）：
        /// off|default|max|zopfli15|zopfli30。取值域与 <see cref="FfmpegOptions.PngOptimizer"/> 一致。
        /// <para>
        /// <b>初值 = "off"（2026-11-11 beta12 后审查修复批 C3）</b>：旧预设 JSON 里根本没有这个键 ⇒
        /// 反序列化不会覆盖属性初值 ⇒ 旧预设读出来就是 "off"。这是「旧预设回放 ⇒ 零漂移（等价于接入
        /// oxipng 之前的产物）」的最小实现——本类没有 schema/版本位，其它轴靠"null = 缺席 ⇒ 不覆盖"
        /// 约定，而本轴的"缺席"语义恰恰是 off（不是"保持控件现状"，否则旧预设会停在当前默认 max）。
        /// 新预设由 GUI 采集/CLI 显式写入 ⇒ 保存时该键恒非 null；新任务的出厂默认 max 在
        /// <see cref="FfmpegOptions.PngOptimizer"/>，两处不冲突。
        /// </para>
        /// <para>
        /// ⚠ 不要改回 null 默认：null 会让 <see cref="FfmpegOptions.ApplyPresetData"/> 走"不覆盖"
        /// 分支 ⇒ 回放旧预设停在当前默认 max、回不到 off（本批审查复现的缺陷）；
        /// GUI 回放路径（MainWindow.SetComboByValueOrIndex 对 null 早退）同理会留 max。
        /// </para>
        /// </summary>
        public string? PngOptimizer { get; set; } = "off";
        public int? PngDpi { get; set; }
        public string? WebpPreset { get; set; }
        public int? AvifCpuUsed { get; set; }
        public string? AvifTune { get; set; }
        public string? AvifPreset { get; set; }
        public bool? AvifStillPicture { get; set; }
        public int? JxlEffort { get; set; }
        public bool? JxlModular { get; set; }
        public string? JpegHuffman { get; set; }
        public string? JpegDct { get; set; }
        public int JpegProgressiveId { get; set; }
        public bool JxlPreserveUltrahdr { get; set; } = true;
        /// <summary>JPEG→JXL 无损重封装（不解码，直接复制 DCT 系数）。默认值引
        /// <see cref="FfmpegOptions.DefaultJxlLosslessJpeg"/>，与数据模型同一处。</summary>
        public bool JxlLosslessJpeg { get; set; } = FfmpegOptions.DefaultJxlLosslessJpeg;
        public string? TiffCompressionAlgo { get; set; }
        // ── Gain Map (Ultra HDR) JPEG ──
        /// <summary>是否启用 Gain Map（需 cjpegli + BT.2020 HDR 色彩空间）</summary>
        public bool JpegGainMap { get; set; }
        /// <summary>Gain Map 压缩质量 (0-100)，-1=跟随主图</summary>
        public int JpegGainMapQuality { get; set; } = -1;
        /// <summary>目标显示器亮度 (nit)</summary>
        public int JpegGainMapTargetNits { get; set; } = 1000;
        /// <summary>增益图类型：false=灰度(1通道), true=RGB(3通道)</summary>
        public bool JpegGainMapMultiChannel { get; set; }
        /// <summary>增益图下采样因子：1=满, 2=1/2, 4=1/4, 8=1/8, 16=1/16</summary>
        public int JpegGainMapDownsample { get; set; } = 2;
        /// <summary>底图色域（均为 SDR 底）："srgb"(默认) / "bt2020"(Rec.2020 广色域底)</summary>
        public string? JpegGainMapBaseGamut { get; set; }
        /// <summary>元数据标记方式："iso"(默认) / "xmp" / "both"；null 按 "iso" 处理</summary>
        public string? JpegGainMapMetadata { get; set; }
        // ── 编码器后端选择 ──
        /// <summary>编码器后端名称: Ffmpeg/Cjpegli/Cjxl/Ultrahdr/Jxr</summary>
        public string? EncoderBackend { get; set; }
        /// <summary>DNG 压缩方式: 0=无损 JPEG, 1=JPEG XL。默认 JXL（最大压缩度）</summary>
        public int DngCompression { get; set; } = 1;
        /// <summary>DNG JXL 质量 (0=无损, 1-100=有损)。默认 0=无损</summary>
        public int DngJxlQuality { get; set; } = 0;
        /// <summary>DNG 布局: false=保留 CFA, true=线性 DNG。默认保留 CFA</summary>
        public bool DngLinear { get; set; } = false;
        /// <summary>DNG JXL 编码努力 (1-9)。默认 7=压缩率与速度平衡</summary>
        public int DngJxlEffort { get; set; } = 7;
        /// <summary>DNG JXL 解码速度提示 (DNG 规范 1-4)。默认 1=最高压缩率</summary>
        public int DngJxlDecodeSpeed { get; set; } = 1;
        /// <summary>DNG 高光模式 (0=裁剪, 1=恢复, 2=blend)。默认 1</summary>
        public int DngHighlightMode { get; set; } = 1;
        /// <summary>DNG 位深 (8/16)。默认 16</summary>
        public int DngBitDepth { get; set; } = 16;
        /// <summary>DNG 镜头校正（源 RAW 三表 → 规范 opcode）。默认 false：它改渲染像素</summary>
        public bool DngLensCorrection { get; set; } = false;
        /// <summary>WebP 无损压缩级别 (0-6)</summary>
        public int? WebpCompressionLevel { get; set; }
        /// <summary>输出文件名追加 .png 后缀（仅 JXL/AVIF）</summary>
        public bool AppendPngExtension { get; set; }
        // ── AVIF 扩展选项 ──
        /// <summary>SVT-AV1 preset 值 (0-13)</summary>
        public int? AvifSvtPreset { get; set; }
        /// <summary>SVT-AV1 tune 类型</summary>
        public string? AvifSvtTune { get; set; }
        /// <summary>硬件编码器预设: 快速/平衡/高质量</summary>
        public string? AvifHwPreset { get; set; }
        /// <summary>硬件编码器预设级别 (1-7)</summary>
        public int AvifHwPresetLevel { get; set; } = 4;
        /// <summary>AVIF 行级多线程</summary>
        public bool? AvifRowMt { get; set; }
        // ── libaom-av1 高级图像选项 ──
        /// <summary>自适应量化模式: variance/complexity</summary>
        public string? AvifAqMode { get; set; }
        /// <summary>约束方向增强滤波器(CDEF)</summary>
        public bool? AvifEnableCdef { get; set; }
        /// <summary>帧内块复制(适合截图/UI)</summary>
        public bool? AvifEnableIntrabc { get; set; }
        /// <summary>降噪颗粒合成等级 (0-50)</summary>
        public int? AvifDenoiseLevel { get; set; }
        // ── NVENC 高级选项 ──
        /// <summary>自适应量化强度 (0-15)</summary>
        public int? AvifNvencAqStrength { get; set; }
        /// <summary>空间自适应量化</summary>
        public bool? AvifNvencSpatialAq { get; set; }
        // ── QSV/VAAPI 选项 ──
        /// <summary>低功耗模式</summary>
        public bool? AvifLowPower { get; set; }
        // ── cjpegli 扩展选项 ──
        /// <summary>cjpegli 色度子采样: 444/422/420/440</summary>
        public string? CjpegliChromaSubsampling { get; set; }
        /// <summary>cjpegli 渐进模式: -1=自动, 0=基线, 2=渐进</summary>
        public int CjpegliProgressiveId { get; set; } = -1;
        /// <summary>cjpegli 优化 Huffman</summary>
        public bool? CjpegliOptimize { get; set; }
        /// <summary>cjpegli 自适应量化</summary>
        public bool? CjpegliAdaptiveQuant { get; set; }
        /// <summary>cjpegli 编码后端: libjpeg/sjpeg</summary>
        public string? CjpegliEncoderBackend { get; set; }
        /// <summary>cjpegli PSNR 目标 (0=禁用)</summary>
        public float CjpegliPsnrTarget { get; set; }
        // ── cjxl 高级选项 ──
        /// <summary>cjxl 渐进式解码</summary>
        public bool CjxlProgressive { get; set; }
        /// <summary>cjxl 光子噪声 ISO (0=禁用)</summary>
        public int CjxlPhotonNoiseIso { get; set; }
        /// <summary>cjxl 自动读取 EXIF ISO</summary>
        public bool CjxlAutoPhotonNoise { get; set; }
        /// <summary>TIFF DPI (0=不写入)</summary>
        public int? TiffDpi { get; set; }
        // ExifTool 选择性剥离选项
        public bool StripExifGps { get; set; } = false;   // 默认不勾选=全保留（与 FfmpegOptions 同一个默认，见其注释）
        public bool StripExifTime { get; set; }
        public bool StripExifCamera { get; set; }
        public bool StripExifAll { get; set; }
        public bool StripXmp { get; set; }
        // 缩略图（EXIF IFD1）
        public bool EnableThumbnail { get; set; }
        /// <summary>缩略图长边像素 (null=沿用默认 160)</summary>
        public int? ThumbnailLongEdge { get; set; }
        /// <summary>缩略图质量 1..100 (null=沿用默认 75)</summary>
        public int? ThumbnailQuality { get; set; }
        public int Concurrency { get; set; } = 2;
        public int MaxQueueSize { get; set; } = 16;
        // 动图参数
        public int? AnimationFps { get; set; }
        // Nullable: 0 (once) and -1 (infinite) are both meaningful and the box ships *empty*,
        // so a preset that never expressed this axis must not overwrite it with the schema default.
        public int? AnimationLoop { get; set; }
        public bool GifPaletteOptimize { get; set; } = true;
        public bool GifDither { get; set; } = true;
        public int AnimationScaleW { get; set; }
        public double AnimationDuration { get; set; }
        // ── ICC 色彩管理 ──
        /// <summary>[遗留] 旧 ICC 模式字符串，仅为兼容旧预设；新预设用 <see cref="ColorStrategy"/>。</summary>
        public string? IccMode { get; set; }
        /// <summary>色彩策略字符串（recommended/carry/cicp-only/manual）。</summary>
        public string? ColorStrategy { get; set; }
        public string? IccFilePath { get; set; }
        public string? IccSourceColorSpace { get; set; }
        public string? IccTargetColorSpace { get; set; }
        
        // ── 图片最长边限制 (缩放) ──
        /// <summary>启用最长边限制功能</summary>
        public bool EnableMaxDimension { get; set; } = false;
        /// <summary>最长边像素限制 (默认 1920)</summary>
        public int MaxDimension { get; set; } = 1920;

        // ── Preset completeness (2026-09-26): simple mode is now a pure overlay that enqueues
        // with the advanced-mode UI state, so a preset has to describe that state completely.
        // These nullable axes are the ones the schema never had. null = "an old preset predates
        // this axis": ApplyPresetData then leaves that control untouched, except ConversionMode,
        // which is derived from Format (ModeOfPresetFormat) because the three modes carry
        // disjoint format lists. ──
        /// <summary>ConversionModeCombo index: 0=still 1=animation 2=RAW</summary>
        public int? ConversionMode { get; set; }
        /// <summary>EncoderCombo selection text (one backend can expose several encoders)</summary>
        public string? EncoderName { get; set; }
        /// <summary>Color gamut mapping: "on" / "off"</summary>
        public string? ColorGamutMap { get; set; }
        /// <summary>
        /// RAW 输出色域模式：null/"rec2020"（默认，Rec.2020 表达优先）| "auto"（按内容判定）|
        /// "rec709"（强制窄色域 / 最大兼容）。⚠ 仅对 RAW 输入生效；语义见
        /// <see cref="FfmpegOptions.RawColorTarget"/>。
        /// </summary>
        public string? RawColorTarget { get; set; }
        // ── R3（2026-11-09）：CLI 8 个色彩开关的 GUI 承接（tone-map / engine 两轴优先）──
        /// <summary>
        /// HDR→SDR 色调映射算子：null（旧预设，= auto）/ "auto"（不指定，引擎按 HDR→SDR
        /// 契约自动采用 Hable）/ "none"（显式不映射）/ "reinhard" / "hable" / "mobius" /
        /// "bt2446a"。取值域与 CLI <c>--color-tone-map</c> 一致；语义见
        /// <see cref="FfmpegOptions.ColorToneMap"/> 与 <see cref="FfmpegOptions.ColorToneMapExplicit"/>。
        /// </summary>
        public string? ColorToneMap { get; set; }
        /// <summary>
        /// 色彩引擎执行路径：null（旧预设，= auto）/ "auto"（默认路由：引擎可走则走，不可走
        /// 按格式归属回传统管线）/ "engine"（显式引擎，不可走时硬失败）/ "legacy"（传统管线）。
        /// 取值域与 CLI <c>--color-engine</c> 一致；语义见 <see cref="FfmpegOptions.ColorEngine"/>。
        /// </summary>
        public string? ColorEngine { get; set; }
        /// <summary>cjxl panel effort; branches with JxlEffort by backend</summary>
        public int? CjxlEffort { get; set; }

        public string ToJson() => JsonSerializer.Serialize(this, AppJsonContext.Default.PresetData);

        public static PresetData FromJson(string json) =>
            JsonSerializer.Deserialize(json, AppJsonContext.Default.PresetData) ?? new PresetData();
    }
}
