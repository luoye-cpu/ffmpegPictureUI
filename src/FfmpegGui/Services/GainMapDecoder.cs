using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services;

/// <summary>
/// 纯托管 JPEG Gain Map (Ultra HDR) 解码器 — 与 GainMapEncoder 对称, 零外部依赖。
///
/// 管线:
///   Ultra HDR JPEG
///     ├─ exiftool 读取 hdrgm XMP 元数据 (GainMapMin/Max/Gamma/OffsetSDR/OffsetHDR/Channels)
///     ├─ exiftool 提取增益图 JPEG (MPImage2)
///     ├─ ffmpeg 解码原文件 → sRGB 8-bit 基础图 → gbrpf32le 线性
///     ├─ ffmpeg 解码增益图 → gbrpf32le (0..1)
///     ├─ C# 双线性插值放大增益图 + 应用增益公式 → HDR 线性像素
///     └─ 输出 gbrpf32le 线性 HDR (1.0 = SDR 白点)
///
/// 增益公式 (ISO 21496-1 / Adobe hdrgm):
///   gain = 增益图像素值 (0..1)
///   logBoost = GainMapMin × (1-gain) + GainMapMax × gain
///   gainFactor = 2^logBoost
///   HDR_linear = (SDR_linear + OffsetSDR) × gainFactor − OffsetHDR
///
/// 灰度模式 (Channels=1): 单通道增益应用于 RGB 三通道 (亮度增益)
/// RGB 模式 (Channels=3): 三通道独立增益
/// </summary>
public static class GainMapDecoder
{
    /// <summary>解析后的 Gain Map 元数据</summary>
    public sealed class GainMapMetadata
    {
        public float GainMapMin;
        public float GainMapMax;
        public float Gamma = 1.0f;
        public float OffsetSdr;
        public float OffsetHdr;
        public int Channels = 1;          // 1=灰度, 3=RGB
        public float HdrCapacityMax = 0;
        public bool BaseRenditionIsHdr;   // true=基础图是 HDR (通常 false)
        public int GainMapWidth;
        public int GainMapHeight;
    }

    /// <summary>
    /// 解码 Ultra HDR JPEG → 线性 HDR 像素文件 (gbrpf32le rawvideo, 1.0 = SDR 白点)。
    /// </summary>
    /// <param name="inputPath">Ultra HDR JPEG 路径</param>
    /// <param name="outputRawPath">输出 gbrpf32le 线性 HDR 像素</param>
    /// <param name="log">日志回调</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成功返回 (宽度, 高度), 失败返回 null</returns>
    public static async Task<(int w, int h)?> DecodeToLinearRawAsync(
        string inputPath, string outputRawPath,
        Action<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            if (!ExifToolService.IsAvailable)
            {
                log?.Invoke("[GainMap解码] ⚠️ exiftool 不可用\n");
                return null;
            }
            if (!CjpegliService.IsAvailable && !string.IsNullOrWhiteSpace(AppSettingsService.Current.FfmpegPath))
            {
                // cjpegli 仅用于测试对比, 解码不需要
            }

            var ffmpeg = AppSettingsService.Current.FfmpegPath;
            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"gmdecode_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // ── Step 1: 读取元数据 ──
                var meta = await ReadMetadataAsync(inputPath, log);
                if (meta == null)
                {
                    log?.Invoke("[GainMap解码] ❌ 无法读取 hdrgm 元数据\n");
                    return null;
                }
                log?.Invoke($"[GainMap解码] 元数据: min={meta.GainMapMin:F3} max={meta.GainMapMax:F3} " +
                    $"gamma={meta.Gamma:F2} offsetSDR={meta.OffsetSdr:F4} offsetHDR={meta.OffsetHdr:F4} " +
                    $"Channels={meta.Channels}\n");

                // ── Step 2: 提取增益图 JPEG ──
                var gmJpegPath = Path.Combine(tempDir, "gainmap.jpg");
                if (!await ExtractGainMapJpegAsync(inputPath, gmJpegPath, log))
                {
                    log?.Invoke("[GainMap解码] ❌ 增益图提取失败\n");
                    return null;
                }

                // ── Step 3: 解码基础图 (整个文件 ffmpeg 直接解码得 sRGB 基础图) → 线性 float ──
                var (baseW, baseH) = await ProbeSizeAsync(inputPath, ffmpeg);
                if (baseW <= 0 || baseH <= 0)
                {
                    log?.Invoke("[GainMap解码] ❌ 无法获取基础图尺寸\n");
                    return null;
                }
                var baseRawPath = Path.Combine(tempDir, "base.rgba");
                // sRGB 编码值 → gbrpf32le (ffmpeg 解码 JPEG 输出 0..1 sRGB 值)
                var baseArgs = $"-y -i \"{inputPath}\" -pix_fmt gbrpf32le -f rawvideo \"{baseRawPath}\"";
                var baseExit = await RunFfmpegAsync(baseArgs, ffmpeg, log, ct);
                if (baseExit != 0 || !File.Exists(baseRawPath))
                {
                    log?.Invoke("[GainMap解码] ❌ 基础图解码失败\n");
                    return null;
                }

                // ── Step 4: 解码增益图 → float 0..1 ──
                var (gmW, gmH) = await ProbeSizeAsync(gmJpegPath, ffmpeg);
                if (gmW <= 0 || gmH <= 0)
                {
                    log?.Invoke("[GainMap解码] ❌ 无法获取增益图尺寸\n");
                    return null;
                }
                meta.GainMapWidth = gmW;
                meta.GainMapHeight = gmH;
                var gmRawPath = Path.Combine(tempDir, "gm.rgba");
                var gmArgs = $"-y -i \"{gmJpegPath}\" -pix_fmt gbrpf32le -f rawvideo \"{gmRawPath}\"";
                var gmExit = await RunFfmpegAsync(gmArgs, ffmpeg, log, ct);
                if (gmExit != 0 || !File.Exists(gmRawPath))
                {
                    log?.Invoke("[GainMap解码] ❌ 增益图解码失败\n");
                    return null;
                }

                // ── Step 5: 应用增益 → 线性 HDR ──
                var baseBytes = await File.ReadAllBytesAsync(baseRawPath, ct);
                var gmBytes = await File.ReadAllBytesAsync(gmRawPath, ct);
                if (baseBytes.Length < baseW * baseH * 12L || gmBytes.Length < gmW * gmH * 12L)
                {
                    log?.Invoke("[GainMap解码] ❌ 像素数据不完整\n");
                    return null;
                }

                // gbrpf32le planar → 交错 RGBA float
                var baseRgb = PlanarToInterleaved(baseBytes, baseW * baseH);
                var gmRgb = PlanarToInterleaved(gmBytes, gmW * gmH);

                // sRGB → 线性 (基础图是 sRGB 编码; 2026-08-15: SIMD 加速)
                SimdPixelOps.SrgbToLinearRgba(baseRgb);

                // 应用增益 (双线性插值增益图 → 基础图分辨率)
                var hdrRgb = ApplyGainMap(baseRgb, gmRgb, baseW, baseH, gmW, gmH, meta);

                // 输出 gbrpf32le (平面 G,B,R)
                // ⚠️ BlockCopy 是原始字节拷贝, 不能从交错 RGBA 抽取平面!
                //   必须逐像素跨步抽取 (此前用 BlockCopy 导致输出错乱)
                var outBytes = new byte[baseW * baseH * 12];
                Span<byte> outSpan = outBytes;
                for (int i = 0; i < baseW * baseH; i++)
                {
                    int o = i * 4;
                    int po = i * 4;
                    // gbrpf32le 平面顺序 = G, B, R
                    BitConverter.TryWriteBytes(outSpan.Slice(po, 4), hdrRgb[o + 1]);              // G
                    BitConverter.TryWriteBytes(outSpan.Slice(baseW * baseH * 4 + po, 4), hdrRgb[o + 2]);  // B
                    BitConverter.TryWriteBytes(outSpan.Slice(baseW * baseH * 8 + po, 4), hdrRgb[o]);      // R
                }

                await File.WriteAllBytesAsync(outputRawPath, outBytes, ct);
                log?.Invoke($"[GainMap解码] ✅ HDR 线性像素: {baseW}x{baseH} (1.0=SDR白点)\n");
                return (baseW, baseH);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke($"[GainMap解码] 异常: {ex.Message}\n");
            return null;
        }
    }

    // ═══════════════════════════════════════════
    //  元数据读取
    // ═══════════════════════════════════════════

    /// <summary>读取 hdrgm XMP 元数据 (逐标签读取, 避免 -hdrgm:all 因 trailer 警告提前退出)</summary>
    public static async Task<GainMapMetadata?> ReadMetadataAsync(string path, Action<string>? log = null)
    {
        try
        {
            if (!ExifToolService.IsAvailable) return null;

            // ── 单次 -json 查询读取全部标签（此前 8 个标签逐个起 exiftool 进程，开销大）──
            // exiftool -json 输出形如 [{"SourceFile":"...","XMP-gainmap:GainMapMin":0.5,...}]
            var json = await ReadMetadataJsonAsync(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var meta = new GainMapMetadata();
            bool found = false;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array || root.GetArrayLength() == 0)
                return null;
            var first = root[0];

            // 从 JSON 属性中查找标签（属性名可能带命名空间前缀，如 "XMP-gainmap:GainMapMin"）
            string? GetTag(string tagName)
            {
                foreach (var prop in first.EnumerateObject())
                {
                    if (prop.Name.EndsWith($":{tagName}", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                    {
                        return prop.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                            System.Text.Json.JsonValueKind.Number => prop.Value.GetRawText(),
                            System.Text.Json.JsonValueKind.True => "True",
                            System.Text.Json.JsonValueKind.False => "False",
                            _ => prop.Value.GetRawText(),
                        };
                    }
                }
                return null;
            }

            var min = GetTag("GainMapMin");
            if (min != null) { meta.GainMapMin = ParseFirstFloat(min); found = true; }
            var max = GetTag("GainMapMax");
            if (max != null) { meta.GainMapMax = ParseFirstFloat(max); found = true; }
            var gamma = GetTag("Gamma");
            if (gamma != null) { meta.Gamma = ParseFirstFloat(gamma); found = true; }
            var offsetSdr = GetTag("OffsetSDR");
            if (offsetSdr != null) { meta.OffsetSdr = ParseFirstFloat(offsetSdr); found = true; }
            var offsetHdr = GetTag("OffsetHDR");
            if (offsetHdr != null) { meta.OffsetHdr = ParseFirstFloat(offsetHdr); found = true; }
            var capMax = GetTag("HDRCapacityMax");
            if (capMax != null) { meta.HdrCapacityMax = ParseFirstFloat(capMax); found = true; }

            var ch = GetTag("Channels");
            if (ch != null && int.TryParse(ch, out var c) && c > 0)
            {
                meta.Channels = c;
                found = true;
            }

            var br = GetTag("BaseRenditionIsHDR");
            if (br != null)
                meta.BaseRenditionIsHdr = br.StartsWith("True", StringComparison.OrdinalIgnoreCase);

            return found ? meta : null;
        }
        catch { return null; }
    }

    /// <summary>一次 exiftool -json 查询全部标签（带 5 秒超时保护）</summary>
    private static async Task<string?> ReadMetadataJsonAsync(string path)
    {
        try
        {
            if (!ExifToolService.IsAvailable) return null;
            var psi = new ProcessStartInfo
            {
                FileName = ExifToolService.DetectedPath!,
                Arguments = $"-json -G \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token);
                return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return null;
            }
        }
        catch { return null; }
    }

    private static float ParseFirstFloat(string value)
    {
        // 值可能是 "0.5" 或 "0.5 0.5 0.5"
        var parts = value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return 0;
        return float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>提取增益图 JPEG (exiftool -b -MPImage2, 二进制输出重定向到文件)</summary>
    public static async Task<bool> ExtractGainMapJpegAsync(string inputPath, string outputJpegPath,
        Action<string>? log = null)
    {
        try
        {
            if (!ExifToolService.IsAvailable) return false;
            var psi = new ProcessStartInfo
            {
                FileName = ExifToolService.DetectedPath!,
                Arguments = $"-b -MPImage2 \"{inputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            // ⚠️ ppl-exiftool (Perl 启动器) 实测怪癖:
            //   进程退出后 stdout 管道数据丢失 (读 0 字节) → 必须进程启动后
            //   立即开始读 stdout, 且不能先等 WaitForExit。
            //   先用 MemoryStream 立即读 (FileStream 打开慢会错过窗口), 再落盘。
            using var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms);
            var stderrTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            await stderrTask;
            if (p.ExitCode != 0 || ms.Length <= 100) return false;
            await File.WriteAllBytesAsync(outputJpegPath, ms.ToArray());
            return File.Exists(outputJpegPath) && new FileInfo(outputJpegPath).Length > 100;
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════
    //  增益应用 (灰度 / RGB 双模式)
    // ═══════════════════════════════════════════

    /// <summary>
    /// 应用增益图到线性基础图。
    /// 灰度 (Channels=1): 单通道增益 (取亮度或任一通道) 应用于 RGB;
    /// RGB (Channels=3): 三通道独立增益。
    /// 增益图双线性插值放大到基础图分辨率。
    /// </summary>
    internal static float[] ApplyGainMap(float[] baseRgba, float[] gmRgba, int baseW, int baseH,
        int gmW, int gmH, GainMapMetadata meta)
    {
        int pixelCount = baseW * baseH;
        var result = new float[pixelCount * 4];
        bool isGray = meta.Channels <= 1;

        // 预计算 gamma 校正后的 log 范围
        float gammaInv = meta.Gamma != 1.0f && meta.Gamma > 0 ? 1.0f / meta.Gamma : 1.0f;

        for (int y = 0; y < baseH; y++)
        {
            for (int x = 0; x < baseW; x++)
            {
                int o = (y * baseW + x) * 4;

                // 双线性采样增益图
                float gx = (x + 0.5f) * gmW / baseW - 0.5f;
                float gy = (y + 0.5f) * gmH / baseH - 0.5f;
                gx = Math.Clamp(gx, 0, gmW - 1);
                gy = Math.Clamp(gy, 0, gmH - 1);
                int x0 = (int)MathF.Floor(gx), y0 = (int)MathF.Floor(gy);
                int x1 = Math.Min(x0 + 1, gmW - 1), y1 = Math.Min(y0 + 1, gmH - 1);
                float fx = gx - x0, fy = gy - y0;

                int g00 = (y0 * gmW + x0) * 4;
                int g01 = (y0 * gmW + x1) * 4;
                int g10 = (y1 * gmW + x0) * 4;
                int g11 = (y1 * gmW + x1) * 4;

                if (isGray)
                {
                    // 灰度: 取亮度 (BT.709) 作为增益 (三通道均值取亮度)
                    float gv = BilinearGray(gmRgba, g00, g01, g10, g11, fx, fy);
                    if (meta.Gamma != 1.0f) gv = MathF.Pow(gv, gammaInv);
                    float logBoost = meta.GainMapMin * (1.0f - gv) + meta.GainMapMax * gv;
                    float gainFactor = MathF.Pow(2f, logBoost);

                    for (int c = 0; c < 3; c++)
                    {
                        float sdr = baseRgba[o + c];
                        result[o + c] = (sdr + meta.OffsetSdr) * gainFactor - meta.OffsetHdr;
                    }
                    result[o + 3] = baseRgba[o + 3];
                }
                else
                {
                    // RGB: 三通道独立增益
                    for (int c = 0; c < 3; c++)
                    {
                        float gv = BilinearChannel(gmRgba, g00, g01, g10, g11, fx, fy, c);
                        if (meta.Gamma != 1.0f) gv = MathF.Pow(gv, gammaInv);
                        float logBoost = meta.GainMapMin * (1.0f - gv) + meta.GainMapMax * gv;
                        float gainFactor = MathF.Pow(2f, logBoost);
                        float sdr = baseRgba[o + c];
                        result[o + c] = (sdr + meta.OffsetSdr) * gainFactor - meta.OffsetHdr;
                    }
                    result[o + 3] = baseRgba[o + 3];
                }
            }
        }
        return result;
    }

    /// <summary>双线性插值单通道 (处理 RGB 交错数据, 取指定通道)</summary>
    private static float BilinearChannel(float[] rgba, int g00, int g01, int g10, int g11,
        float fx, float fy, int c)
    {
        float v00 = rgba[g00 + c];
        float v01 = rgba[g01 + c];
        float v10 = rgba[g10 + c];
        float v11 = rgba[g11 + c];
        return v00 * (1 - fx) * (1 - fy) + v01 * fx * (1 - fy) + v10 * (1 - fx) * fy + v11 * fx * fy;
    }

    /// <summary>双线性插值灰度增益 (BT.709 亮度加权, 兼容灰度编码为 RGB 3 通道的情形)</summary>
    private static float BilinearGray(float[] rgba, int g00, int g01, int g10, int g11,
        float fx, float fy)
    {
        float L(int off)
        {
            return 0.2126f * rgba[off] + 0.7152f * rgba[off + 1] + 0.0722f * rgba[off + 2];
        }
        float v00 = L(g00), v01 = L(g01), v10 = L(g10), v11 = L(g11);
        return v00 * (1 - fx) * (1 - fy) + v01 * fx * (1 - fy) + v10 * (1 - fx) * fy + v11 * fx * fy;
    }

    // ═══════════════════════════════════════════
    //  工具函数
    // ═══════════════════════════════════════════

    /// <summary>gbrpf32le planar → RGBA 交错</summary>
    internal static float[] PlanarToInterleaved(byte[] planar, int pixelCount)
    {
        var rgba = new float[pixelCount * 4];
        var gPlane = new float[pixelCount];
        var bPlane = new float[pixelCount];
        var rPlane = new float[pixelCount];
        Buffer.BlockCopy(planar, 0, gPlane, 0, pixelCount * 4);
        Buffer.BlockCopy(planar, pixelCount * 4, bPlane, 0, pixelCount * 4);
        Buffer.BlockCopy(planar, pixelCount * 8, rPlane, 0, pixelCount * 4);
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            rgba[o] = rPlane[i];
            rgba[o + 1] = gPlane[i];
            rgba[o + 2] = bPlane[i];
            rgba[o + 3] = 1f;
        }
        return rgba;
    }

    /// <summary>RGBA 交错 → gbrpf32le planar (G,B,R)</summary>
    /// <summary>sRGB 编码值 → 线性</summary>
    internal static float SrgbToLinear(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    private static async Task<(int w, int h)> ProbeSizeAsync(string path, string ffmpeg)
    {
        try
        {
            var ffprobe = PlatformServices.ResolveFfprobePath(ffmpeg)
                ?? Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
            if (!File.Exists(ffprobe)) ffprobe = ffmpeg.Replace("ffmpeg.exe", "ffprobe.exe");
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (0, 0);
            var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            var parts = output.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                return (w, h);
        }
        catch { }
        return (0, 0);
    }

    private static async Task<int> RunFfmpegAsync(string args, string ffmpeg,
        Action<string>? log, CancellationToken ct)
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
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }
}
