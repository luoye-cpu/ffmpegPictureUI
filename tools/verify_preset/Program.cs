using System;
using FfmpegGui.Models;

// 预设 JSON 往返测试
var preset = new PresetData
{
    Format = "avif",
    Quality = 85,
    Chroma = "4:4:4",
    BitDepth = "10",
    MetadataMode = "PreserveAll",
    StripExifGps = true,
    StripExifTime = false,
    StripExifCamera = true,
    StripExifAll = false,
    StripXmp = false,
    UseAdvancedCodec = true,
    JxlEffort = 7,
    Concurrency = 4,
    MaxQueueSize = 16
};

var json = preset.ToJson();
Console.WriteLine("=== 导出 JSON ===");
Console.WriteLine(json);

var loaded = PresetData.FromJson(json);
Console.WriteLine("\n=== 导入验证 ===");

bool ok = true;
void Check<T>(string name, T expected, T actual)
{
    var pass = Equals(expected, actual);
    Console.WriteLine($"{(pass ? "✅" : "❌")} {name}: {actual} (expected: {expected})");
    if (!pass) ok = false;
}

Check("Format", "avif", loaded.Format);
Check("Quality", 85, loaded.Quality);
Check("BitDepth", "10", loaded.BitDepth);
Check("MetadataMode", "PreserveAll", loaded.MetadataMode);
Check("StripExifGps", true, loaded.StripExifGps);
Check("StripExifCamera", true, loaded.StripExifCamera);
Check("StripExifTime", false, loaded.StripExifTime);
Check("StripExifAll", false, loaded.StripExifAll);
Check("StripXmp", false, loaded.StripXmp);
Check("JxlEffort", 7, loaded.JxlEffort);
Check("Concurrency", 4, loaded.Concurrency);

Console.WriteLine($"\n{(ok ? "✅ 预设导出/导入 JSON 往返全部通过" : "❌ 存在失败项")}");
return ok ? 0 : 1;
