using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FfmpegGui.Models
{
    /// <summary>
    /// NativeAOT 兼容的 JSON 序列化上下文（Source Generator）。
    /// 反射序列化在裁剪/AOT 下会抛 NotSupportedException，必须使用源生成。
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(PresetData))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class AppJsonContext : JsonSerializerContext
    {
    }
}
