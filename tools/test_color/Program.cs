using System.Diagnostics;
using FfmpegGui.Models;
using FfmpegGui.Services;

// ═══════════════════════════════════════════════════════════
// FFmpegPictureUI 色彩管理端到端测试
// ═══════════════════════════════════════════════════════════

var planDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PLAN");
var ffmpegDir = Path.Combine(planDir, "ffmpeg-full");
var ffmpegPath = Path.Combine(ffmpegDir, "ffmpeg.exe");
var ffprobePath = Path.Combine(ffmpegDir, "ffprobe.exe");
var testDir = Path.Combine(Path.GetTempPath(), $"color_test_{DateTime.Now:yyyyMMdd_HHmmss}");
Directory.CreateDirectory(testDir);

Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine("  FFmpegPictureUI 色彩管理测试");
Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine($"  FFmpeg: {ffmpegPath}");
Console.WriteLine($"  测试目录: {testDir}");
Console.WriteLine();

if (!File.Exists(ffmpegPath))
{
    Console.WriteLine("❌ FFmpeg 未找到！请确保 PLAN/ffmpeg-full/ 目录存在。");
    Console.WriteLine($"  查找路径: {ffmpegPath}");
    return 1;
}

// 创建测试用 settings.json 使 AppSettingsService 能定位 ffmpeg
var settingsJson = $$"""
{
  "FfmpegDirectory": "{{ffmpegDir.Replace("\\", "\\\\")}}",
  "FfmpegPath": "{{ffmpegPath.Replace("\\", "\\\\")}}"
}
""";
var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
File.WriteAllText(settingsPath, settingsJson);
Console.WriteLine($"  已创建 settings.json: {settingsPath}");
Console.WriteLine();

// ═══════════════════════════════════════════════
// Step 1: 创建测试源图片
// ═══════════════════════════════════════════════
Console.WriteLine("═══ Step 1: 创建测试源图片 ═══");

// 1a: 标准 sRGB 8-bit PNG
var srcSrgbPng = Path.Combine(testDir, "src_srgb.png");
await RunFfmpeg(ffmpegPath, $"-y -f lavfi -i \"color=c=red:size=64x64,format=rgb24\" -frames:v 1 \"{srcSrgbPng}\"");
Console.WriteLine($"  ✅ sRGB PNG: {Path.GetFileName(srcSrgbPng)}");

// 1b: 带有 BT.709 色彩元数据的 PNG
var srcBt709Png = Path.Combine(testDir, "src_bt709.png");
await RunFfmpeg(ffmpegPath, $"-y -f lavfi -i \"color=c=blue:size=64x64,format=rgb24\" -color_primaries bt709 -color_trc bt709 -colorspace bt709 -frames:v 1 \"{srcBt709Png}\"");
Console.WriteLine($"  ✅ BT.709 PNG: {Path.GetFileName(srcBt709Png)}");

// 1c: 带有 BT.2020 HDR 元数据的 10-bit PNG
var srcHdrPng = Path.Combine(testDir, "src_hdr2020.png");
await RunFfmpeg(ffmpegPath, $"-y -f lavfi -i \"color=c=green:size=64x64,format=gbrp10le\" -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -pix_fmt rgb48le -frames:v 1 \"{srcHdrPng}\"");
Console.WriteLine($"  ✅ HDR BT.2020 PNG: {Path.GetFileName(srcHdrPng)}");

Console.WriteLine();

// ═══════════════════════════════════════════════
// Step 2: 测试色彩参数构建（不实际编码）
// ═══════════════════════════════════════════════
Console.WriteLine("═══ Step 2: 色彩参数构建测试 ═══");

int testCount = 0;
int passCount = 0;
int failCount = 0;

void TestCase(string name, FfmpegOptions opts, string input, string? expectedContains = null, params string[] expectedNotContains)
{
    testCount++;
    try
    {
        var args = FfmpegCommandBuilder.BuildArguments(opts, input, Path.Combine(testDir, $"out_{testCount}.{opts.Format}"));
        bool pass = true;
        var reasons = new List<string>();

        if (expectedContains != null && !args.Contains(expectedContains, StringComparison.OrdinalIgnoreCase))
        {
            pass = false;
            reasons.Add($"缺少: {expectedContains}");
        }
        foreach (var not in expectedNotContains)
        {
            if (args.Contains(not, StringComparison.OrdinalIgnoreCase))
            {
                pass = false;
                reasons.Add($"不应包含: {not}");
            }
        }

        if (pass)
        {
            passCount++;
            Console.WriteLine($"  ✅ [{name}]");
        }
        else
        {
            failCount++;
            Console.WriteLine($"  ❌ [{name}] {string.Join("; ", reasons)}");
            Console.WriteLine($"     cmd: ffmpeg {args}");
        }
    }
    catch (Exception ex)
    {
        failCount++;
        Console.WriteLine($"  ❌ [{name}] 异常: {ex.Message}");
    }
}

// Test 1: sRGB→JPEG，默认色彩
TestCase("sRGB→JPEG default", new FfmpegOptions { Format = "jpg", Quality = 80 }, srcSrgbPng);

// Test 2: sRGB→JPEG，BT.709 色彩空间
TestCase("sRGB→JPEG BT.709", new FfmpegOptions { Format = "jpg", Quality = 80, ColorSpace = "BT.709" }, srcSrgbPng,
    "bt709");

// Test 3: sRGB→PNG，高级色彩参数
TestCase("sRGB→PNG advanced", new FfmpegOptions
{
    Format = "png", Quality = 100, Lossless = true,
    UseAdvancedColorParameters = true,
    ColorPrimaries = "bt709", ColorTrc = "iec61966-2-1"
}, srcSrgbPng, "bt709");

// Test 4: sRGB→AVIF，BT.2020（应该触发自动 zscale）
TestCase("sRGB→AVIF BT.2020 zscale", new FfmpegOptions
{
    Format = "avif", Quality = 80, ColorSpace = "BT.2020", Chroma = "4:2:0", BitDepth = 10
}, srcSrgbPng, "zscale=");

// Test 5: HDR→AVIF，直通（不应触发 tonemap）
TestCase("HDR→AVIF passthrough", new FfmpegOptions
{
    Format = "avif", Quality = 80, Chroma = "4:2:0", BitDepth = 10
}, srcHdrPng, null, "tonemap");

// Test 6: HDR→JPEG（应触发 HDR→SDR tonemap）
TestCase("HDR→JPEG tonemap", new FfmpegOptions
{
    Format = "jpg", Quality = 80
}, srcHdrPng, "tonemap");

// Test 7: sRGB→JXL (FFmpeg libjxl)
TestCase("sRGB→JXL FFmpeg", new FfmpegOptions
{
    Format = "jxl", Quality = 90, JxlEffort = 5
}, srcSrgbPng, "-distance");

// Test 8: PNG→WebP 无损（应使用 rgba 像素格式）
TestCase("PNG→WebP lossless rgba", new FfmpegOptions
{
    Format = "webp", Quality = 100, Lossless = true
}, srcSrgbPng, "rgba");

// Test 9: ICC Bake sRGB→AdobeRGB
TestCase("ICC Bake sRGB→AdobeRGB", new FfmpegOptions
{
    Format = "png", Quality = 100, Lossless = true,
    IccMode = IccMode.BakeToStandard,
    IccSourceColorSpace = "sRGB",
    IccTargetColorSpace = "Adobe RGB"
}, srcSrgbPng, "zscale=");

// Test 10: ICC Embed 模式（JPEG格式，应走 exiftool 后处理）
TestCase("ICC Embed JPEG", new FfmpegOptions
{
    Format = "jpg", Quality = 80,
    IccMode = IccMode.CarryIcc
}, srcSrgbPng);

// Test 11: ICC Bake+Embed AVIF（有外部 ICC 时应跳过 iccgen）
// 注意：由于没有实际 .icc 文件，这里只测试无外部 ICC 文件的 iccgen 路径
TestCase("ICC gen AVIF no external", new FfmpegOptions
{
    Format = "avif", Quality = 80, BitDepth = 10,
    IccMode = IccMode.CarryIcc
}, srcSrgbPng, "iccgen");

Console.WriteLine();
Console.WriteLine($"  参数构建测试: {passCount}/{testCount} 通过, {failCount} 失败");

// ═══════════════════════════════════════════════
// Step 3: 实际编码测试
// ═══════════════════════════════════════════════
Console.WriteLine();
Console.WriteLine("═══ Step 3: 实际编码测试 ═══");

int encodePass = 0;
int encodeFail = 0;

async Task TestEncode(string name, FfmpegOptions opts, string input)
{
    var output = Path.Combine(testDir, $"enc_{name}.{opts.Format}");
    try
    {
        var args = FfmpegCommandBuilder.BuildArguments(opts, input, output);
        var exitCode = await RunFfmpeg(ffmpegPath, args);

        if (exitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 100)
        {
            // 用 ffprobe 验证色彩元数据
            var meta = await ProbeColorMeta(ffprobePath, output);
            var sizeKb = new FileInfo(output).Length / 1024;
            Console.WriteLine($"  ✅ [{name}] {sizeKb}KB | primaries={meta.p ?? "N/A"} trc={meta.t ?? "N/A"} space={meta.s ?? "N/A"}");
            encodePass++;
        }
        else
        {
            Console.WriteLine($"  ❌ [{name}] 退出码={exitCode}, 文件大小={(File.Exists(output) ? new FileInfo(output).Length : 0)}字节");
            encodeFail++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ❌ [{name}] 异常: {ex.Message}");
        encodeFail++;
    }
}

// 编码测试
await TestEncode("srgb_jpg", new FfmpegOptions { Format = "jpg", Quality = 80 }, srcSrgbPng);
await TestEncode("srgb_png", new FfmpegOptions { Format = "png", Quality = 100, Lossless = true }, srcSrgbPng);
await TestEncode("srgb_webp", new FfmpegOptions { Format = "webp", Quality = 90 }, srcSrgbPng);
await TestEncode("srgb_avif", new FfmpegOptions { Format = "avif", Quality = 80, BitDepth = 10 }, srcSrgbPng);
await TestEncode("srgb_jxl", new FfmpegOptions { Format = "jxl", Quality = 90, JxlEffort = 3 }, srcSrgbPng);
await TestEncode("srgb_tiff", new FfmpegOptions { Format = "tiff", Quality = 0, Lossless = true }, srcSrgbPng);
await TestEncode("bt709_jpg", new FfmpegOptions { Format = "jpg", Quality = 80, ColorSpace = "BT.709" }, srcBt709Png);

// ICC 嵌入测试（FFmpeg AVIF + iccgen）
await TestEncode("avif_iccgen", new FfmpegOptions { Format = "avif", Quality = 80, BitDepth = 10, IccMode = IccMode.CarryIcc }, srcSrgbPng);

// BT.2020 测试
await TestEncode("srgb_avif_bt2020", new FfmpegOptions { Format = "avif", Quality = 80, BitDepth = 10, ColorSpace = "BT.2020" }, srcSrgbPng);

// WebP 无损
await TestEncode("srgb_webp_lossless", new FfmpegOptions { Format = "webp", Quality = 100, Lossless = true }, srcSrgbPng);

// GIF 测试
await TestEncode("srgb_gif", new FfmpegOptions { Format = "gif", Quality = 80 }, srcSrgbPng);

Console.WriteLine();
Console.WriteLine($"  编码测试: {encodePass}/{encodePass + encodeFail} 通过");

// ═══════════════════════════════════════════════
// Step 4: ICC 烘焙实际测试
// ═══════════════════════════════════════════════
Console.WriteLine();
Console.WriteLine("═══ Step 4: ICC 色彩烘焙测试 ═══");

// 使用 zscale 滤镜做 sRGB→bt709 的实际烘焙测试
await TestEncode("bake_srgb_png", new FfmpegOptions
{
    Format = "png", Quality = 100, Lossless = true,
    IccMode = IccMode.BakeToStandard,
    IccSourceColorSpace = "sRGB",
    IccTargetColorSpace = "sRGB"
}, srcSrgbPng);

// ═══════════════════════════════════════════════
// Step 5: 外部工具测试
// ═══════════════════════════════════════════════
Console.WriteLine();
Console.WriteLine("═══ Step 5: 外部工具可用性检查 ═══");

CheckTool("cjxl.exe", Path.Combine(planDir, "jxl", "bin", "cjxl.exe"));
CheckTool("djxl.exe", Path.Combine(planDir, "jxl", "bin", "djxl.exe"));
CheckTool("cjpegli.exe", Path.Combine(planDir, "jxl", "bin", "cjpegli.exe"));
CheckTool("JxrEncApp.exe", Path.Combine(planDir, "artifacts", "JxrEncApp.exe"));
CheckTool("JxrDecApp.exe", Path.Combine(planDir, "artifacts", "JxrDecApp.exe"));
CheckTool("ultrahdr_app.exe", Path.Combine(planDir, "artifacts", "ultrahdr_app.exe"));
CheckTool("avifenc.exe", Path.Combine(planDir, "artifacts", "avifenc.exe"));
CheckTool("exiftool.exe", Path.Combine(planDir, "exiftool", "exiftool.exe"));
CheckTool("ffprobe.exe", Path.Combine(planDir, "ffmpeg-full", "ffprobe.exe"));

static void CheckTool(string name, string path)
{
    if (File.Exists(path))
        Console.WriteLine($"  ✅ {name}");
    else
        Console.WriteLine($"  ❌ {name} (未找到: {path})");
}

// ═══════════════════════════════════════════════
// Step 6: 外部编码器实际测试
// ═══════════════════════════════════════════════
Console.WriteLine();
Console.WriteLine("═══ Step 6: 外部编码器测试 ═══");

// cjxl 有损编码
var cjxlPath = Path.Combine(planDir, "jxl", "bin", "cjxl.exe");
if (File.Exists(cjxlPath))
{
    var cjxlOut = Path.Combine(testDir, "ext_cjxl.jxl");
    var cjxlExit = await RunTool(cjxlPath, $"\"{srcSrgbPng}\" \"{cjxlOut}\" -d 1.0 -e 3");
    Console.WriteLine($"  {(cjxlExit == 0 && File.Exists(cjxlOut) ? "✅" : "❌")} cjxl: exit={cjxlExit}, size={(File.Exists(cjxlOut) ? new FileInfo(cjxlOut).Length / 1024 : 0)}KB");
}

// cjpegli 编码
var cjpegliPath = Path.Combine(planDir, "jxl", "bin", "cjpegli.exe");
if (File.Exists(cjpegliPath))
{
    var cjpegliOut = Path.Combine(testDir, "ext_cjpegli.jpg");
    var cjpegliExit = await RunTool(cjpegliPath, $"\"{srcSrgbPng}\" \"{cjpegliOut}\" --distance 5.0");
    Console.WriteLine($"  {(cjpegliExit == 0 && File.Exists(cjpegliOut) ? "✅" : "❌")} cjpegli: exit={cjpegliExit}, size={(File.Exists(cjpegliOut) ? new FileInfo(cjpegliOut).Length / 1024 : 0)}KB");
}

// JxrEncApp 编码
var jxrPath = Path.Combine(planDir, "artifacts", "JxrEncApp.exe");
if (File.Exists(jxrPath))
{
    // JxrEncApp 需要 BMP 输入
    var bmpInput = Path.Combine(testDir, "jxr_input.bmp");
    await RunFfmpeg(ffmpegPath, $"-y -i \"{srcSrgbPng}\" -pix_fmt bgr24 \"{bmpInput}\"");
    if (File.Exists(bmpInput))
    {
        var jxrOut = Path.Combine(testDir, "ext_jxr.jxr");
        var jxrExit = await RunTool(jxrPath, $"-i \"{bmpInput}\" -o \"{jxrOut}\" -q 0.90");
        Console.WriteLine($"  {(jxrExit == 0 && File.Exists(jxrOut) ? "✅" : "❌")} JxrEncApp: exit={jxrExit}, size={(File.Exists(jxrOut) ? new FileInfo(jxrOut).Length / 1024 : 0)}KB");
    }
}

// ═══════════════════════════════════════════════
// Step 7: ffprobe 色彩元数据验证
// ═══════════════════════════════════════════════
Console.WriteLine();
Console.WriteLine("═══ Step 7: 输出文件色彩元数据验证 ═══");

// 检查编码后的 AVIF 和 JXL 文件色彩元数据
foreach (var file in Directory.GetFiles(testDir, "enc_*.*"))
{
    var meta = await ProbeColorMeta(ffprobePath, file);
    var name = Path.GetFileName(file);
    if (!string.IsNullOrEmpty(meta.p) || !string.IsNullOrEmpty(meta.t))
        Console.WriteLine($"  📊 {name}: primaries={meta.p ?? "?"}, trc={meta.t ?? "?"}, space={meta.s ?? "?"}, bd={meta.bd}");
}

// ═══════════════════════════════════════════════
// Summary
// ═══════════════════════════════════════════════
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine($"  测试完成！");
Console.WriteLine($"  参数构建: {passCount}/{testCount} 通过");
Console.WriteLine($"  实际编码: {encodePass}/{encodePass + encodeFail} 通过");
Console.WriteLine($"  输出目录: {testDir}");
Console.WriteLine("═══════════════════════════════════════════");

return (failCount + encodeFail) > 0 ? 1 : 0;

// ── 辅助函数 ──

static async Task<int> RunFfmpeg(string ffmpeg, string args)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return -1;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
    catch { return -1; }
}

static async Task<int> RunTool(string tool, string args)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return -1;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
    catch { return -1; }
}

static async Task<(string? p, string? t, string? s, int bd)> ProbeColorMeta(string ffprobe, string file)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            Arguments = $"-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer,color_space,bits_per_raw_sample,pix_fmt -of csv=p=0 \"{file}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return (null, null, null, 0);
        var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
        await p.WaitForExitAsync();
        var parts = output.Split(',');
        string? primaries = parts.Length > 0 && !string.IsNullOrEmpty(parts[0]) && parts[0] != "unknown" && parts[0] != "N/A" ? parts[0] : null;
        string? trc = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) && parts[1] != "unknown" && parts[1] != "N/A" ? parts[1] : null;
        string? space = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) && parts[2] != "unknown" && parts[2] != "N/A" ? parts[2] : null;
        int bd = 0;
        if (parts.Length > 3) int.TryParse(parts[3], out bd);
        if (bd == 0 && parts.Length > 4)
        {
            // 从 pix_fmt 推断
            var pf = parts[4];
            if (pf.Contains("10")) bd = 10;
            else if (pf.Contains("12")) bd = 12;
            else if (pf.Contains("16") || pf.Contains("48")) bd = 16;
            else bd = 8;
        }
        return (primaries, trc, space, bd);
    }
    catch { return (null, null, null, 0); }
}
