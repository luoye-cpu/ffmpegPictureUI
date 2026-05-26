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
        public string? BitDepth { get; set; }
        public bool AutoThreads { get; set; } = true;
        public bool SingleThread { get; set; }
        public int ManualThreads { get; set; } = 4;
        public string? MetadataMode { get; set; }
        public bool Lossless { get; set; }
        public bool UseAdvancedCodec { get; set; }
        public string? PngPred { get; set; }
        public string? WebpPreset { get; set; }
        public int? AvifCpuUsed { get; set; }
        public string? AvifTune { get; set; }
        public string? AvifPreset { get; set; }
        public bool? AvifStillPicture { get; set; }
        public int? JxlEffort { get; set; }
        public bool? JxlModular { get; set; }
        public string? JpegHuffman { get; set; }
        public string? TiffCompressionAlgo { get; set; }
        // ExifTool 选择性剥离选项
        public bool StripExifGps { get; set; } = true;
        public bool StripExifTime { get; set; }
        public bool StripExifCamera { get; set; }
        public bool StripExifAll { get; set; }
        public bool StripXmp { get; set; }
        public int Concurrency { get; set; } = 2;
        public int MaxQueueSize { get; set; } = 16;

        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        public static PresetData FromJson(string json) =>
            JsonSerializer.Deserialize<PresetData>(json) ?? new PresetData();
    }
}
