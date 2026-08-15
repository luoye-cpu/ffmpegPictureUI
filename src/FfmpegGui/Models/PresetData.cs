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
        public string? BitDepth { get; set; }
        public bool AutoThreads { get; set; } = true;
        public bool SingleThread { get; set; }
        public int ManualThreads { get; set; } = 4;
        public string? MetadataMode { get; set; }
        public bool Lossless { get; set; }
        public bool UseAdvancedCodec { get; set; }
        public string? PngPred { get; set; }
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
        /// <summary>JPEG→JXL 无损重封装（不解码，直接复制 DCT 系数）</summary>
        public bool JxlLosslessJpeg { get; set; }
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
        public bool StripExifGps { get; set; } = true;
        public bool StripExifTime { get; set; }
        public bool StripExifCamera { get; set; }
        public bool StripExifAll { get; set; }
        public bool StripXmp { get; set; }
        public int Concurrency { get; set; } = 2;
        public int MaxQueueSize { get; set; } = 16;
        // 动图参数
        public int? AnimationFps { get; set; }
        public int AnimationLoop { get; set; }
        public bool GifPaletteOptimize { get; set; } = true;
        public bool GifDither { get; set; } = true;
        public int AnimationScaleW { get; set; }
        public double AnimationDuration { get; set; }
        // ── ICC 色彩管理 ──
        public string? IccMode { get; set; }
        public string? IccFilePath { get; set; }
        public string? IccSourceColorSpace { get; set; }
        public string? IccTargetColorSpace { get; set; }

        public string ToJson() => JsonSerializer.Serialize(this, AppJsonContext.Default.PresetData);

        public static PresetData FromJson(string json) =>
            JsonSerializer.Deserialize(json, AppJsonContext.Default.PresetData) ?? new PresetData();
    }
}
