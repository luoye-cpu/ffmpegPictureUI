using System;
using System.Text.Json;

namespace FfmpegGui.Models
{
    /// <summary>
    /// 元数据处理模式
    /// </summary>
    public enum MetadataMode
    {
        PreserveAll,  // 保留所有元数据
        StripAll      // 删除所有元数据
    }

    /// <summary>
    /// ICC 色彩处理模式（CICP 始终启用，以下控制 ICC 文件的处理方式）
    /// </summary>
    public enum IccMode
    {
        /// <summary>模式1: 默认 — 输出不包含 ICC（输入端即使有 ICC 也丢弃），仅保留 CICP 标记</summary>
        None = 0,
        /// <summary>模式2: 携带 ICC — 源文件有 ICC 则保留，否则嵌入匹配色域的标准 ICC</summary>
        CarryIcc = 1,
        /// <summary>模式3: 烘焙 + 嵌入标准 ICC — 像素转换到标准色彩空间，输出嵌入标准 ICC</summary>
        BakeToStandard = 2,
        /// <summary>模式4: 仅烘焙 — 像素转换到标准色彩空间，输出不携带 ICC</summary>
        BakeOnly = 3
    }

    public class FfmpegOptions
    {
        /// <summary>深度克隆（通过 JSON 序列化保证所有字段拷贝）</summary>
        public FfmpegOptions DeepClone()
        {
            var json = JsonSerializer.Serialize(this, AppJsonContext.Default.FfmpegOptions);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.FfmpegOptions)!;
        }

        /// <summary>从 PresetData 应用预设到当前 FfmpegOptions 实例</summary>
        public void ApplyPresetData(PresetData p)
        {
            if (p.Format != null) Format = p.Format;
            if (p.Quality >= 0) Quality = p.Quality;
            if (p.Chroma != null) Chroma = p.Chroma;
            if (p.BitDepth != null) BitDepth = string.IsNullOrEmpty(p.BitDepth) ? null : int.TryParse(p.BitDepth, out var bd) ? bd : BitDepth;
            if (p.ColorSpace != null) ColorSpace = p.ColorSpace;
            UseAdvancedColorParameters = p.UseAdvancedColor;
            if (p.ColorPrimaries != null) ColorPrimaries = p.ColorPrimaries;
            if (p.ColorTrc != null) ColorTrc = p.ColorTrc;
            if (p.ColorMatrix != null) ColorMatrix = p.ColorMatrix;
            if (p.ColorRange != null) ColorRange = p.ColorRange;
            if (p.TonemapCurve != null) TonemapCurve = p.TonemapCurve;
            AutoThreads = p.AutoThreads;
            Threads = p.ManualThreads;
            if (!string.IsNullOrWhiteSpace(p.MetadataMode) && Enum.TryParse<MetadataMode>(p.MetadataMode, true, out var mm))
                MetadataMode = mm;
            Lossless = p.Lossless;
            // 注：预设的 UseAdvancedCodec（高级编码面板开关）无对应的 FfmpegOptions 字段，
            // 其效果由下方各具体编码子选项（PngPred/JpegHuffman/JxlEffort 等）携带，故此处不需赋值。
            // （旧代码误将其赋给 UseAdvancedColorParameters，会错误开启高级色彩参数，已移除）

            if (p.PngPred != null) PngPred = p.PngPred;
            if (p.PngDpi.HasValue) PngDpi = p.PngDpi;
            if (p.WebpPreset != null) WebpPreset = p.WebpPreset;
            if (p.AvifCpuUsed.HasValue) AvifCpuUsed = p.AvifCpuUsed;
            if (p.AvifStillPicture.HasValue) AvifStillPicture = p.AvifStillPicture;
            if (p.AvifRowMt.HasValue) AvifRowMt = p.AvifRowMt;
            if (p.JxlEffort.HasValue) JxlEffort = p.JxlEffort;
            if (p.JxlModular.HasValue) JxlModular = p.JxlModular;
            if (p.JpegHuffman != null) JpegHuffman = p.JpegHuffman;
            if (p.JpegDct != null) JpegDct = p.JpegDct;
            JpegProgressiveId = p.JpegProgressiveId;
            JxlLosslessJpeg = p.JxlLosslessJpeg;
            JxlPreserveUltrahdr = p.JxlPreserveUltrahdr;

            if (p.TiffCompressionAlgo != null) TiffCompressionAlgo = p.TiffCompressionAlgo;
            if (p.TiffDpi.HasValue) TiffDpi = p.TiffDpi;
            if (p.AvifTune != null) AvifTune = p.AvifTune;
            if (p.WebpCompressionLevel.HasValue) WebpCompressionLevel = p.WebpCompressionLevel;
            if (p.AvifSvtPreset.HasValue) AvifSvtPreset = p.AvifSvtPreset;
            if (p.AvifSvtTune != null) AvifSvtTune = p.AvifSvtTune;
            if (p.AvifAqMode != null) AvifAqMode = p.AvifAqMode;
            if (p.AvifEnableCdef.HasValue) AvifEnableCdef = p.AvifEnableCdef;
            if (p.AvifEnableIntrabc.HasValue) AvifEnableIntrabc = p.AvifEnableIntrabc;
            if (p.AvifDenoiseLevel.HasValue) AvifDenoiseLevel = p.AvifDenoiseLevel;
            if (p.AvifNvencAqStrength.HasValue) AvifNvencAqStrength = p.AvifNvencAqStrength;
            if (p.AvifNvencSpatialAq.HasValue) AvifNvencSpatialAq = p.AvifNvencSpatialAq;
            if (p.AvifLowPower.HasValue) AvifLowPower = p.AvifLowPower;
            if (p.AvifHwPresetLevel > 0) AvifHwPresetLevel = p.AvifHwPresetLevel;
            if (p.CjpegliChromaSubsampling != null) CjpegliChromaSubsampling = p.CjpegliChromaSubsampling;
            CjpegliProgressiveId = p.CjpegliProgressiveId;
            if (p.CjpegliOptimize.HasValue) CjpegliOptimize = p.CjpegliOptimize.Value;
            if (p.CjpegliAdaptiveQuant.HasValue) CjpegliAdaptiveQuant = p.CjpegliAdaptiveQuant.Value;
            if (p.CjpegliEncoderBackend != null) CjpegliEncoderBackend = p.CjpegliEncoderBackend;
            CjpegliPsnrTarget = p.CjpegliPsnrTarget;
            if (p.CjxlProgressive) CjxlProgressive = p.CjxlProgressive;
            if (p.CjxlPhotonNoiseIso > 0) CjxlPhotonNoiseIso = p.CjxlPhotonNoiseIso;
            if (p.CjxlAutoPhotonNoise) CjxlAutoPhotonNoise = p.CjxlAutoPhotonNoise;

            StripExifGps = p.StripExifGps;
            StripExifTime = p.StripExifTime;
            StripExifCamera = p.StripExifCamera;
            StripExifAll = p.StripExifAll;
            StripXmp = p.StripXmp;

            if (p.JpegGainMap) JpegGainMap = p.JpegGainMap;
            if (p.JpegGainMapQuality >= 0) JpegGainMapQuality = p.JpegGainMapQuality;
            if (p.JpegGainMapTargetNits > 0) JpegGainMapTargetNits = p.JpegGainMapTargetNits;
            JpegGainMapDownsample = p.JpegGainMapDownsample;
            JpegGainMapMultiChannel = p.JpegGainMapMultiChannel;
            if (!string.IsNullOrEmpty(p.JpegGainMapBaseGamut)) JpegGainMapBaseGamut = p.JpegGainMapBaseGamut;

            if (p.DngCompression >= 0) DngCompression = p.DngCompression;
            if (p.DngJxlQuality >= 0) DngJxlQuality = p.DngJxlQuality;
            DngLinear = p.DngLinear;
            if (p.DngJxlEffort > 0) DngJxlEffort = p.DngJxlEffort;
            if (p.DngJxlDecodeSpeed > 0) DngJxlDecodeSpeed = p.DngJxlDecodeSpeed;
            if (p.DngHighlightMode >= 0) DngHighlightMode = p.DngHighlightMode;
            if (p.DngBitDepth > 0) DngBitDepth = p.DngBitDepth;

            if (p.AnimationFps.HasValue) AnimationFps = p.AnimationFps;
            if (p.AnimationLoop.HasValue) AnimationLoop = p.AnimationLoop.Value;
            GifPaletteOptimize = p.GifPaletteOptimize;
            GifDither = p.GifDither;
            if (p.AnimationScaleW > 0) AnimationScaleW = p.AnimationScaleW;
            if (p.AnimationDuration > 0) AnimationDuration = p.AnimationDuration;

            if (!string.IsNullOrWhiteSpace(p.EncoderBackend) && Enum.TryParse<Services.EncoderBackend>(p.EncoderBackend, true, out var eb))
                EncoderBackend = eb;

            // 图片最长边限制
            EnableMaxDimension = p.EnableMaxDimension;
            if (p.MaxDimension > 0) MaxDimension = p.MaxDimension;

            if (!string.IsNullOrWhiteSpace(p.ColorStrategy) && ColorStrategyMapper.TryParse(p.ColorStrategy, out var cs))
                ColorStrategy = cs;
            else if (!string.IsNullOrWhiteSpace(p.IccMode) && Enum.TryParse<IccMode>(p.IccMode, true, out var icc))
                ColorStrategy = ColorStrategyMapper.FromIccMode(icc);   // 旧预设 IccMode 迁移
            if (p.IccFilePath != null) IccFilePath = p.IccFilePath;
        }

        public string Format { get; set; } = "jpg";
        public int Quality { get; set; } = 75;
        /// <summary>色度子采样: auto/4:4:4/4:2:2/4:2:0（默认 auto 跟随输入/格式能力）</summary>
        public string Chroma { get; set; } = "auto";
        /// <summary>
        /// 位深：null = auto（不指定，由编码器自行判断）
        /// </summary>
        public int? BitDepth { get; set; } = null;
        /// <summary>
        /// <see cref="BitDepth"/> 被容器上限**就地钳制之前**的那一档 = 用户真正请求的位深；null = 未指定或从未被钳。
        /// <para>
        /// 为什么需要它（任务 #35）：<c>FfmpegCommandBuilder.Decision</c> 里
        /// `if (options.BitDepth.Value > capBd) options.BitDepth = capBd;` 是本仓唯一的位深事实源，
        /// 就地改写会把"用户要 10"这个事实**擦掉** ⇒ 规划层拿到的目标档已经是 8，于是
        /// `--bit-depth 10 -f webp`（请求 10、交付 8）这类**真实降位**成了零播报。
        /// 播报本身仍只由规划层 <c>CollectDegradations</c> ①b 登记（本字段只补它缺的那半个事实）。
        /// </para>
        /// ⚠ 不是用户可选项：预设 DTO（<c>PresetData</c>）里没有它，也不进简洁模式；
        ///   值由钳制点就地补记 ⇒ CLI/GUI/预设回放三种入口都会得到正确的请求档。
        /// </summary>
        public int? BitDepthRequested { get; set; } = null;
        public string? ColorSpace { get; set; }
        public bool UseAdvancedColorParameters { get; set; } = false;
        /// <summary>
        /// ⚠⚠ **本组色彩三元组是「程序按 RAW 预处理默认值注入」的，不是用户显式指定**（2026-09-20 新增）。
        /// <para>
        /// 为什么需要它：<c>ColorIntentFactory</c> 在 <see cref="UseAdvancedColorParameters"/> 为 true 时，
        /// 用「<see cref="ColorTrc"/> 非空」判定 **`targetExplicit`**（= 用户显式指定了目标色彩）。
        /// 而 RAW 预处理（<c>QueueProcessor</c>）会注入 `ColorPrimaries` + `ColorTrc=linear`，
        /// 这两个值被**误当成用户意图** ⇒ `it.Target` 非 null ⇒ 规划层「**无色彩通路容器**（GIF/JXR）
        /// ⇒ 把目标钉成 sRGB」那条**不生效** ⇒ `Reject`
        /// （实测：**RAW → GIF 无路可走**，而普通 PNG → GIF 可以）。
        /// </para>
        /// <para>
        /// ⚠ 只影响**意图装配**（`targetExplicit` / 目标描述符），**不影响输出标注** ——
        /// 后者走另一条独立的线（<c>FfmpegCommandBuilder.EffectiveOutputColorTokens</c>），
        /// 仍按本组值写 CICP ⇒ 「RAW 默认色域按目标 HDR 能力取值」那条功能**不受影响**。
        /// </para>
        /// </summary>
        public bool ColorFromRawDefault { get; set; } = false;
        // ── 精确色彩三元组：**在属性入口规范化**（单一真值，2026-09-16）──
        //  这些值会原样拼进 ffmpeg 的 `-color_primaries/-color_trc/-colorspace`，而 ffmpeg 只认
        //  自己的常量名；CLI 面向用户宣传的却是友好名（`--color-trc srgb|pq|hlg`）、GUI 历史词表里
        //  还混有 zimg 风格名（bt2020ncl/ycgcocn）。不规范化 ⇒ `Unable to parse` ⇒ 退出码 -22、零产物。
        //  入口只有一个 ⇒ CLI / GUI / 预设 JSON / 程序内注入四条路径一次覆盖，模型内恒为规范名。
        private string? _colorPrimaries;
        private string? _colorTrc;
        private string? _colorMatrix;
        public string? ColorPrimaries
        {
            get => _colorPrimaries;
            set => _colorPrimaries = Services.ColorSpaceRegistry.NormalizePrimariesToken(value);
        }
        public string? ColorTrc
        {
            get => _colorTrc;
            set => _colorTrc = Services.ColorSpaceRegistry.NormalizeTrcToken(value);
        }
        public string? ColorMatrix
        {
            get => _colorMatrix;
            set => _colorMatrix = Services.ColorSpaceRegistry.NormalizeMatrixToken(value);
        }
        /// <summary>
        /// 输出色彩范围: null/"auto" = 自动（RGB 输入→pc），"tv" = limited（视频范围），"pc" = full（全范围）
        /// </summary>
        public string? ColorRange { get; set; }
        /// <summary>HDR→SDR tonemap 曲线: null/"hable"(默认)/"mobius"/"reinhard"。仅对 HDR 降级生效。</summary>
        public string? TonemapCurve { get; set; }
        public int Threads { get; set; } = ComputeAutoThreads();
        /// <summary>
        /// 自动线程模式 (2026-08-15): 运行时按并发任务数动态分配。
        /// 每任务线程 = max(1, ProcessorCount / 并发数) — 所有任务合计吃满核数,
        /// 任务数超过核数时每任务 1 线程。手动/单线程模式为 false (用 Threads 固定值)。
        /// </summary>
        public bool AutoThreads { get; set; } = false;
        public MetadataMode MetadataMode { get; set; } = MetadataMode.PreserveAll;
        public string? Encoder { get; set; }
        /// <summary>编码器后端类型（由 UI 设置，供 QueueProcessor 调度）</summary>
        public Services.EncoderBackend EncoderBackend { get; set; } = Services.EncoderBackend.Ffmpeg;
        public bool Lossless { get; set; } = false;
        // 高级编码器私有选项
        public string? PngPred { get; set; }
        public int? PngDpi { get; set; }
        public string? WebpPreset { get; set; }
        /// <summary>WebP 无损模式压缩级别 (0-6, 0=最快)</summary>
        public int? WebpCompressionLevel { get; set; }
        public int? AvifCpuUsed { get; set; }
        public bool? AvifStillPicture { get; set; }
        public bool? AvifRowMt { get; set; }
        public int? JxlEffort { get; set; }
        public bool? JxlModular { get; set; }
        /// <summary>
        /// JPEG→JXL 无损重封装模式：不解码像素，直接复制 DCT 系数，速度极快且完全保留原图质量
        /// </summary>
        public bool JxlLosslessJpeg { get; set; } = false;
        /// <summary>cjxl 渐进式解码 (--progressive)</summary>
        public bool CjxlProgressive { get; set; } = false;
        /// <summary>cjxl 光子噪声 ISO (0=禁用, 100-3200)</summary>
        public int CjxlPhotonNoiseIso { get; set; } = 0;

        /// <summary>自动从输入图片 EXIF 元数据读取 ISO 用于光子噪声（每张图独立读取）</summary>
        public bool CjxlAutoPhotonNoise { get; set; } = false;
        /// <summary>JXL 处理 Ultra HDR JPEG 时保留增益图（解码后重新编码），false=无损重封装忽略增益图</summary>
        public bool JxlPreserveUltrahdr { get; set; } = true;
        /// <summary>解码的 Ultra HDR 输出色彩空间提示（线性 HDR 为 "RGB_D65_SRG_Rel_Lin"），用于 cjxl -x color_space=</summary>
        public string? DecodedUltraHdrColorSpace { get; set; }
        /// <summary>解码的 Ultra HDR 线性像素峰值亮度 (nits) = 203 × 2^GainMapMax，用于 cjxl --intensity_target 与 ffmpeg tonemap</summary>
        public float DecodedUltraHdrPeakNits { get; set; }

        /// <summary>
        /// 【运行时推导，非用户选项】超广色域「像素原样 + 仅靠 ICC 描述」保真路径的 ICC 绝对路径。
        /// 由色彩管线在探测到源为 ProPhoto/ROMM（色域超出 BT.2020，zscale/iccgen/CICP 均无法正确命名）
        /// 且能解析到命名 ICC 时设置：此时跳过像素转换与 iccgen，编码完成后由队列把该 ICC 附着到输出。
        /// null = 不适用（走原有 BT.2020 归一化）。
        /// </summary>
        public string? FaithfulIccPath { get; set; }

        // ── DNG 输出选项（dngtool 编码）──
        /// <summary>DNG 压缩方式: 0=无损 JPEG, 1=JPEG XL (DNG 1.7)。默认 JXL（最大压缩度）</summary>
        public int DngCompression { get; set; } = 1;
        /// <summary>DNG JXL 质量 (0=无损, 1-100=有损)。默认 0=无损（最好画质）</summary>
        public int DngJxlQuality { get; set; } = 0;
        /// <summary>DNG 输出布局: false=保留 CFA (Bayer, 可重新去马赛克), true=线性 DNG (无 CFA, 体积更小)。默认保留 CFA（信息无损）</summary>
        public bool DngLinear { get; set; } = false;
        /// <summary>DNG JXL 编码努力 (effort 1-9, 默认 7=压缩率与速度平衡)</summary>
        public int DngJxlEffort { get; set; } = 7;
        /// <summary>DNG JXL 解码速度提示 (DNG 规范 1-4, 默认 1=最高压缩率)</summary>
        public int DngJxlDecodeSpeed { get; set; } = 1;
        /// <summary>DNG 高光模式 (LibRaw -H, 0=裁剪, 1=高光恢复, 2=无裁剪/blend)。默认 1=高光恢复</summary>
        public int DngHighlightMode { get; set; } = 1;
        /// <summary>DNG 位深: 8/16 (dngtool -4/-6 即 8/16bit, 默认 16 保留原始位深)</summary>
        public int DngBitDepth { get; set; } = 16;
        public string? JpegHuffman { get; set; }
        /// <summary>JPEG DCT 算法: "int" / "fastint" / "float"</summary>
        public string? JpegDct { get; set; }
        /// <summary>JPEG 渐进模式: -1=自动, 0=基线, 1=渐进 (Gain Map / mjpeg 路径)</summary>
        public int JpegProgressiveId { get; set; } = 0;  // 0=Baseline (Gain Map 推荐基线)
        // ── Gain Map (Ultra HDR) JPEG 选项 ──
        /// <summary>是否启用 Gain Map（仅 JPEG 输出；纯 C# GainMapEncoder + 所选 JPEG 编码器(cjpegli/mjpeg)实现，无 libultrahdr 依赖）</summary>
        public bool JpegGainMap { get; set; } = false;
        /// <summary>Gain Map 压缩质量 (0-100)，-1 表示使用主 Quality 值</summary>
        public int JpegGainMapQuality { get; set; } = -1;
        /// <summary>目标显示器亮度 (nit)：默认 1000（显式指定）；置 0 = auto，优先读源 HDR 静态元数据
        /// (MaxCLL / mastering max_luminance)，无元数据时仍按 1000。</summary>
        public int JpegGainMapTargetNits { get; set; } = 1000;
        /// <summary>增益图下采样因子: 1=满分辨率, 2=1/2(默认), 4=1/4, 8=1/8</summary>
        public int JpegGainMapDownsample { get; set; } = 2;
        /// <summary>多通道增益图: true=RGB, false=灰度(默认,更小体积)</summary>
        public bool JpegGainMapMultiChannel { get; set; } = false;
        /// <summary>
        /// GainMap 底图（base rendition）色域："srgb"（默认）或 "bt2020"。
        /// 两者均为 **SDR** 底（sRGB 传递 + 203nits 白点），HDR 完全由增益图承载；
        /// 选 bt2020 时底图像素由源原色映射到 Rec.2020 并附对应 SDR ICC
        /// （对齐 libultrahdr writeIccProfile(UHDR_CT_SRGB, cg) / ultrahdr_color_gamut = BT709|P3|BT2100）。
        /// </summary>
        public string JpegGainMapBaseGamut { get; set; } = "srgb";
        public string? TiffCompressionAlgo { get; set; }
        public int? TiffDpi { get; set; }
        public string? AvifTune { get; set; }
        public string? AvifPreset { get; set; }
        /// <summary>SVT-AV1 编码器 preset 值 (0-13, null=不指定)</summary>
        public int? AvifSvtPreset { get; set; }
        /// <summary>SVT-AV1 编码器 tune 类型</summary>
        public string? AvifSvtTune { get; set; }
        /// <summary>硬件编码器质量预设: 快速/平衡/高质量</summary>
        public string? AvifHwPreset { get; set; }
        /// <summary>硬件编码器预设数值 (1-7, 用于 NVENC p1-p7 / QSV veryfast-veryslow / VAAPI compression_level)</summary>
        public int AvifHwPresetLevel { get; set; } = 4;
        // ── libaom-av1 高级图像选项 ──
        /// <summary>自适应量化模式: null=默认, "variance"=适合照片, "complexity"=适合混合内容</summary>
        public string? AvifAqMode { get; set; }
        /// <summary>约束方向增强滤波器 (CDEF): null=默认开启, false=关闭提速但边缘可能有振铃</summary>
        public bool? AvifEnableCdef { get; set; }
        /// <summary>帧内块复制 (适合截图/UI/文字): null=默认开启, false=关闭</summary>
        public bool? AvifEnableIntrabc { get; set; }
        /// <summary>降噪+颗粒合成 (0=关闭): 对噪点多的照片先降噪后合成颗粒,大幅减小体积</summary>
        public int? AvifDenoiseLevel { get; set; }
        // ── NVENC 硬件编码器高级选项 ──
        /// <summary>NVENC 自适应量化强度 (0-15, 默认8): 值越大平坦区域压缩越激进</summary>
        public int? AvifNvencAqStrength { get; set; }
        /// <summary>NVENC 空间自适应量化: null=默认开启, false=关闭(均匀QP,可能更大文件)</summary>
        public bool? AvifNvencSpatialAq { get; set; }
        // ── QSV/VAAPI 硬件编码器选项 ──
        /// <summary>QSV/VAAPI 低功耗模式: true=固定功能硬件(质量可能下降但省电), null=默认关闭</summary>
        public bool? AvifLowPower { get; set; }

        // ── cjpegli / jpegli 专属高级选项 ──
        /// <summary>色度子采样: "auto"/"444"/"422"/"420"/"440"（默认 auto 跟随输入）</summary>
        public string CjpegliChromaSubsampling { get; set; } = "auto";
        /// <summary>渐进模式: -1=自动, 0=基线, 2=渐进（2026-08-16 实测渐进压缩率更高: 体积小 5-38%，默认 2）</summary>
        public int CjpegliProgressiveId { get; set; } = 2;
        /// <summary>Huffman 表优化</summary>
        public bool CjpegliOptimize { get; set; } = true;
        /// <summary>自适应量化</summary>
        public bool CjpegliAdaptiveQuant { get; set; } = true;
        /// <summary>编码器后端: "libjpeg" / "sjpeg"</summary>
        public string CjpegliEncoderBackend { get; set; } = "libjpeg";
        /// <summary>PSNR 目标 (sjpeg, 0=禁用)</summary>
        public float CjpegliPsnrTarget { get; set; } = 0;
        /// <summary>多线程是否可用（运行时检测，默认 false）</summary>
        public bool CjpegliMultiThreadAvailable { get; set; } = false;

        // ── ExifTool 隐私清理选项（仅在 exiftool 可用时生效）──
        /// <summary>删除 GPS 位置信息（默认开启，与 UI/PresetData 一致）</summary>
        public bool StripExifGps { get; set; } = true;
        /// <summary>删除拍摄时间日期</summary>
        public bool StripExifTime { get; set; } = false;
        /// <summary>删除相机/镜头型号与拍摄参数</summary>
        public bool StripExifCamera { get; set; } = false;
        /// <summary>删除全部 EXIF 数据</summary>
        public bool StripExifAll { get; set; } = false;
        /// <summary>删除 XMP 元数据</summary>
        public bool StripXmp { get; set; } = false;

        // ── 色彩策略（取代旧 IccMode；源色彩恒自动检测，不再手动）──
        /// <summary>色彩策略（推荐/携带/仅CICP/手动），色彩处理总控。</summary>
        public ColorStrategy ColorStrategy { get; set; } = ColorStrategy.Recommended;
        
        // ── 自研色彩引擎开关（三值语义，2026-09-16）──
        /// <summary>
        /// 色彩执行路径。**默认 `auto`**（用户指令「清理传统管线，以自研管线为准」）。
        /// <list type="bullet">
        /// <item><c>auto</c>（默认）：优先走自研引擎（`ColorStrategyPlanning` 决策 + `RawColorPipeline` 执行）；
        /// 该次任务**不在引擎能力内**时（JXR/DNG 输出、动画输入、GainMap+最长边几何缩放）
        /// **优雅回退传统管线**并记一条说明 —— 那些格式本就是传统管线的正常归属。
        /// ⚠ 2026-09-17 更正：清单里原先还写着 **AVIF** 与裸 **GainMap**，二者均已接入引擎
        /// （AVIF 见 `ImageEncoderArgs.BuildAvifOptions`；GainMap 见 `RawColorPipeline` 的 GainMap 出口），
        /// 只剩「GainMap + 最长边缩放」这条组合仍被显式拦（底图与增益图必须同几何）。
        /// ⚠ 同日第二次更正：**GIF** 与 **JXL（Ffmpeg 子情形）** 亦已接入（GIF 见
        /// `ImageEncoderArgs.BuildGifOptions`；JXL 见 `BuildJxlOptions`）。
        /// ⚠ 同日第三次更正（cjxl 出口轮）：**JXL 的 Cjxl 后端**也已接入（`ColorTransformPlan.ExternalEncoderExit`
        /// ⇒ `RawColorPipeline` 的 cjxl 执行出口）⇒ 本条清单**不再含任何 JXL 项**。⚠ 出口内 cjxl 缺失等
        /// 失败一律**显式失败**，不回退 ffmpeg 的 libjxl。</item>
        /// ⚠ 同日第四次更正（JXR 出口轮）：**JXR 也已接入**（同一计划位 ⇒ `RawColorPipeline` 的
        /// BMP/TIFF 中转 + `JxrEncApp` 执行出口）⇒ 本条清单只剩 **DNG/动画** 与「GainMap + 最长边缩放」。
        /// ⚠ 本工具链的 ffmpeg **有** `libjxr`（09-26 构建实测，取证 `tests/output/t50/`），但它**无质量
        /// AVOption** 且字节序与 jxrlib 不一致 ⇒ 不作回退项（唯一口径见 `RawColorPipeline` 的 JXR 出口文档）；
        /// JXR 出口内失败**没有可回退的替代编码器**，同样一律显式失败。</item>
        /// <item><c>engine</c>：**显式要求**引擎。不可走时**硬失败并点名原因**（绝不静默回退，可用性诚实）。</item>
        /// <item><c>legacy</c>：**显式**回退传统 ffmpeg 色彩链。仅用于 A/B 对拍与故障排查
        /// （其 4 个策略只有两个布尔近似：无 Reject、无超集归一）。</item>
        /// </list>
        /// <para>
        /// 切默认的依据：A/B 实测 **8-bit 源上两条管线逐字节等价**（png/tiff 逐字节相同、jpg PSNR=inf），
        /// 16-bit 源残余为舍入级（png/tiff 85.8dB）。见 `docs/TESTING.md §6` 第 48/49/50 条。
        /// </para>
        /// </summary>
        public string ColorEngine { get; set; } = "auto";
        /// <summary>BT.709 编码口径：std=ITU-R BT.709-6 定义式（默认，对 bt709 目标禁 zscale）；zimg=对齐历史产物（纯 γ2.4）。</summary>
        public string Color709Curve { get; set; } = "std";
        /// <summary>S1（推荐）携带范围：none | unnameable | all（默认 all，无损优先）。</summary>
        public string ColorCarryScope { get; set; } = "all";
        /// <summary>允许在未标注语义不统一的容器（TIFF 类）产出无标注结果（需显式开启）。</summary>
        public bool ColorAllowUnlabeled { get; set; }
        /// <summary>HDR→SDR 色调映射算子：none（默认，不映射）| reinhard | hable | mobius | bt2446a（ITU-R BT.2446-1 §4 Method A）。
        /// bt2390 已核对现行正文无 EETF 定义→不可选；需与“降 SDR”意图同时成立。</summary>
        public string ColorToneMap { get; set; } = "none";
        /// <summary>
        /// 源色域超出目标时的处理方式：off（**默认**，与历史行为逐字节相同：钳位=硬裁切）| on（GMO 色域压缩）。
        /// <para>
        /// on 时引擎在**目标色域**线性 RGB 上施加 libjxl 的标量色域映射
        /// （<c>ColorKernels.GamutMapScalar</c>）：越界像素向"同亮度灰"混合去饱和，再按最大分量归一，
        /// 而不是把越界分量直接裁到 0/1。只在**源色域超出目标**（<c>GamutOutsideDestination</c>）时生效；
        /// 未越界的像素**逐字节不变**（算子在不越界输入上是严格 no-op）。
        /// </para>
        /// <para>⚠ 只在自研引擎路径（H1 进程内）有意义：传统 ffmpeg/zscale 链只有钳位，没有 GMO 算子。</para>
        /// </summary>
        public string ColorGamutMap { get; set; } = "off";
        /// <summary>Method A 的 SDR 目标峰值 L_SDR（nits；建议书默认 100，与参考白 203 是两个量）。</summary>
        public double ColorSdrPeakNits { get; set; } = 100;
        /// <summary>内容峰值亮度 nits（&gt;0 = 用户指定，优先一切）。0 = auto，优先级：
        /// 容器 HDR 静态元数据（MaxCLL→BS.2086 mastering）→ 整帧实测内容像素峰值（引擎路径）
        /// → 源曲线名义峰值（PQ=10000/HLG=1000，仅兜底）；绝不猜 1000。
        /// 来源会写进日志（<c>[tonemap: … 峰值来源=…]</c> 与 <c>[色彩] 峰值来源=整帧实测…</c>）。</summary>
        public double ColorHdrPeakNits { get; set; }
        /// <summary>SDR 目标白点 nits（默认 203，BT.2100）。</summary>
        public double ColorSdrWhiteNits { get; set; } = 203;
        /// <summary>用户选择的 ICC 文件绝对路径（.icc / .icm）</summary>
        public string? IccFilePath { get; set; }
        /// <summary>输出文件名后追加 .png 后缀（仅 JXL/AVIF 生效，解决某些软件不识别的兼容性问题）</summary>
        public bool AppendPngExtension { get; set; } = false;

        // ── 图片最长边限制 (缩放) ──
        /// <summary>启用最长边限制功能</summary>
        public bool EnableMaxDimension { get; set; } = false;
        /// <summary>最长边像素限制 (默认 1920，1-7680)</summary>
        public int MaxDimension { get; set; } = 1920;

        // ── 动图参数 ──
        /// <summary>帧率 (FPS)，null=不指定</summary>
        public int? AnimationFps { get; set; }
        /// <summary>循环次数: 0=无限, -1=不循环, >0=指定次数</summary>
        public int AnimationLoop { get; set; } = -1;
        /// <summary>GIF 调色板优化</summary>
        public bool GifPaletteOptimize { get; set; } = true;
        /// <summary>GIF 抖动处理</summary>
        public bool GifDither { get; set; } = true;
        /// <summary>动图缩放宽度 (0=保持原始)</summary>
        public int AnimationScaleW { get; set; } = 0;
        /// <summary>视频/动图最大时长（秒），0=不限制。仅对视频输入生效，防止生成超大动图</summary>
        public double AnimationDuration { get; set; } = 0;

        public static int ComputeAutoThreads()
        {
            int total = Environment.ProcessorCount;
            if (total >= 12) return Math.Max(1, total - 4);
            if (total > 4)   return Math.Max(1, total - 2);
            return Math.Max(1, total - 1);
        }

        /// <summary>
        /// 自适应线程分配 (2026-08-15): 多任务并行时每任务应分到的线程数。
        /// 规则: max(1, 总核数 / 并发任务数) — 所有任务合计吃满核数;
        ///       并发数 ≥ 核数时每任务 1 线程 (任务并行本身已占满核)。
        /// 并发=1 → 全部核给单个任务 (多线程)。
        /// </summary>
        public static int ComputeAdaptiveThreads(int concurrency)
        {
            int total = Environment.ProcessorCount;
            int per = concurrency > 0 ? total / concurrency : total;
            return Math.Max(1, per);
        }

        /// <summary>
        /// 各格式视觉无损默认质量值 (0-100)
        /// </summary>
        public static int GetDefaultQuality(string format) => format.ToLower() switch
        {
            "png" => 100,
            "apng" => 100,
            "webp" => 95,
            "avif" => 90,
            "jxl" => 90,
            "tiff" => 0,
            "jpegli" => 92,
            "gif" => 90,
            _ => 92 // jpg 默认
        };

        /// <summary>butteraugli distance 0-25（扩展范围，同质量下 cjpegli 输出略小于 mjpeg）</summary>
        public static double MapJpegliDistance(int quality) =>
            Math.Round((100 - quality) * 25.0 / 100.0, 1);

        // ── 正反映射：滑块 0-100 ↔ 各格式实际编码参数 ──

        // JPEG q:v 2-31（整数，越小质量越高）
        public static int MapJpegQualityForward(int quality) => (int)Math.Round(2 + (100 - quality) * 29.0 / 100.0);
        public static int MapJpegQualityInverse(double qv) => (int)Math.Round(100 - (Math.Clamp(qv, 2, 31) - 2) * 100.0 / 29.0);

        // JPEGli distance 0-25（1 位小数）
        public static double MapJpegliDistanceForward(int quality) => MapJpegliDistance(quality);
        public static int MapJpegliDistanceInverse(double d) => (int)Math.Round(100 - Math.Clamp(d, 0, 25) * 100.0 / 25.0);

        // PNG compression_level 0-9（整数，越大压缩越狠）
        public static int MapPngLevelForward(int quality) => (int)Math.Round((100 - quality) * 9.0 / 100.0);
        public static int MapPngLevelInverse(double level) => (int)Math.Round(100 - Math.Clamp(level, 0, 9) * 100.0 / 9.0);

        // WebP q:v 0-100（与滑块同尺度）
        public static int MapWebpQualityForward(int quality) => quality;
        public static int MapWebpQualityInverse(double qv) => (int)Math.Clamp(qv, 0, 100);

        // AVIF CRF 0-63（整数，越小质量越高）
        public static int MapAvifCrfForward(int quality) => (int)Math.Round((100 - quality) * 63.0 / 100.0);
        public static int MapAvifCrfInverse(double crf) => (int)Math.Round(100 - Math.Clamp(crf, 0, 63) * 100.0 / 63.0);

        // JXL distance 0-15（1 位小数）
        public static double MapJxlDistanceForward(int quality) => Math.Round((100 - quality) * 15.0 / 100.0, 1);
        public static int MapJxlDistanceInverse(double d) => (int)Math.Round(100 - Math.Clamp(d, 0, 15) * 100.0 / 15.0);

        /// <summary>滑块值 → 格式实际参数文本（用于输入框显示）</summary>
        public static string FormatQualityForDisplay(string fmt, int quality, Services.EncoderBackend backend = Services.EncoderBackend.Ffmpeg) => fmt.ToLower() switch
        {
            "jpg" or "jpeg" => backend == Services.EncoderBackend.Cjpegli
                ? MapJpegliDistanceForward(quality).ToString("F1")
                : MapJpegQualityForward(quality).ToString(),
            "png" => MapPngLevelForward(quality).ToString(),
            "webp" => MapWebpQualityForward(quality).ToString(),
            "avif" => MapAvifCrfForward(quality).ToString(),
            "jxl" => MapJxlDistanceForward(quality).ToString("F1"),
            "tiff" => "N/A",
            _ => quality.ToString(),
        };

        /// <summary>格式实际参数 → 滑块值（用户输入解析用）</summary>
        public static int ParseQualityFromDisplay(string fmt, double value, Services.EncoderBackend backend = Services.EncoderBackend.Ffmpeg) => fmt.ToLower() switch
        {
            "jpg" or "jpeg" => backend == Services.EncoderBackend.Cjpegli
                ? MapJpegliDistanceInverse(value)
                : MapJpegQualityInverse(value),
            "png" => MapPngLevelInverse(value),
            "webp" => MapWebpQualityInverse(value),
            "avif" => MapAvifCrfInverse(value),
            "jxl" => MapJxlDistanceInverse(value),
            _ => (int)Math.Clamp(value, 0, 100),
        };

        /// <summary>质量参数标签名</summary>
        public static string GetQualityLabel(string format, int quality, Services.EncoderBackend backend = Services.EncoderBackend.Ffmpeg) => format.ToLower() switch
        {
            "jpg" or "jpeg" => backend == Services.EncoderBackend.Cjpegli
                ? $"质量: {quality}% → distance {MapJpegliDistanceForward(quality):F1}"
                : $"质量: {quality}% → q:v {MapJpegQualityForward(quality)}",
            "png" => $"压缩: {quality}% → level {MapPngLevelForward(quality)}",
            "webp" => $"质量: {quality}% → q:v {MapWebpQualityForward(quality)}",
            "avif" => $"质量: {quality}% → CRF {MapAvifCrfForward(quality)}",
            "jxl" => $"质量: {quality}% → distance {MapJxlDistanceForward(quality):F1}",
            "tiff" => "质量: 不适用 (无损格式)",
            _ => $"质量: {quality}%"
        };
    }
}