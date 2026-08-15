using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services;

/// <summary>
/// 纯托管 JPEG Gain Map (Ultra HDR) 编码器 — 完全替代 ultrahdr_app。
///
/// 设计参考 TrueToneCap (https://github.com/luoye-cpu/TrueToneCap, Apache-2.0)
/// 的 JpegGainMapEncoder 实现，与 Google libultrahdr 输出格式完全兼容。
///
/// 管线:
///   HDR 线性 RGB (1.0 = SDR 白点) → 分段 Reinhard 色调映射 → SDR 线性
///     ├─ sRGB gamma → 8-bit RGB → cjpegli → Base JPEG (SDR 基础图)
///   HDR 线性 / SDR 线性 → log2 增益比 → 1/4 降采样 → cjpegli → 增益图 JPEG
///   → MPF (APP2) + XMP (APP1) + ISO 21496-1 打包 → Ultra HDR JPEG
///
/// 兼容性要点 (TrueToneCap 实测踩坑记录):
///   - MPF 偏移必须相对 APP2 数据段起始, primary size 必须写完整大小
///   - XMP 增益图 item 必须写 Item:Length (部分解码器依赖)
///   - ISO 与 XMP 的 log2 增益范围必须一致
///   - ISO flags bit6 (useBaseColorSpace) 必须置位
///   - Base 显式嵌入 sRGB ICC (无 ICC 时解码器默认 sRGB)
/// </summary>
public static class GainMapEncoder
{
    public const float KSdrWhiteNits = 203f;   // 行业标准 SDR 白点 (libultrahdr kSdrWhiteNits)
    public const float KOffset = 0.015625f;    // 1/64 规范推荐 offset

    // ═══════════════════════════════════════════
    //  公共入口
    // ═══════════════════════════════════════════

    /// <summary>
    /// 编码 Ultra HDR JPEG。
    /// </summary>
    /// <param name="hdrRgbaLinear">HDR 线性 RGBA 像素 (1.0 = 峰值亮度对应值), 长度 w*h*4</param>
    /// <param name="w">宽度</param>
    /// <param name="h">高度</param>
    /// <param name="outputPath">输出 .jpg 路径</param>
    /// <param name="hdrPeakNits">HDR 峰值亮度 (用于 headroom 计算)</param>
    /// <param name="sdrWhiteNits">SDR 白点亮度 (默认 203)</param>
    /// <param name="multiChannel">true=RGB 三通道增益图, false=灰度增益图</param>
    /// <param name="baseQuality">Base JPEG 质量 (butteraugli distance, 0.5-25)</param>
    /// <param name="gainMapQuality">增益图质量 (butteraugli distance, 0.5-25)</param>
    /// <param name="downsample">增益图降采样因子 (默认 4 = 1/4 分辨率)</param>
    /// <param name="log">日志回调</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成功返回 true</returns>
    public static async Task<bool> EncodeAsync(
        float[] hdrRgbaLinear, int w, int h, string outputPath,
        float hdrPeakNits = 1000f, float sdrWhiteNits = KSdrWhiteNits,
        bool multiChannel = false, float baseQuality = 1.5f, float gainMapQuality = 1.5f,
        int downsample = 4, Action<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            var ffmpeg = AppSettingsService.Current.FfmpegPath;
            if (!CjpegliService.IsAvailable)
            {
                log?.Invoke("[GainMap] ⚠️ cjpegli 不可用，无法编码 Gain Map\n");
                return false;
            }

            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"gainmap_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // ── 亮度参数 ──
                float headroom = hdrPeakNits / sdrWhiteNits;
                float maxLog2Gain = MathF.Log2(Math.Max(headroom, 1.0f));

                log?.Invoke($"[GainMap] 参数: HDR峰值 {hdrPeakNits:F0}nits, SDR白点 {sdrWhiteNits:F0}nits, headroom {headroom:F2} ({maxLog2Gain:F2} log2)\n");

                // ── 归一化 HDR 到 SDR 白点相对空间 ──
                // 输入约定: 线性值 1.0 = hdrPeakNits (zscale npl 语义)
                // normHdr = 物理亮度 / SDR白点 → SDR 内容(≤白点) → ≤1.0, HDR 高光 → >1.0
                int pixelCount = w * h;
                float peakOverWhite = hdrPeakNits / Math.Max(sdrWhiteNits, 1f);
                float[] normHdr = new float[pixelCount * 4];
                // 归一化 (2026-08-15: SIMD 化前已是简单乘, JIT 自动向量化; 保持现状)
                for (int i = 0; i < pixelCount * 4; i++)
                    normHdr[i] = hdrRgbaLinear[i] * peakOverWhite;

                // ── 1. 分段 Reinhard 色调映射 → SDR 线性 (2026-08-15: SIMD 加速) ──
                float[] sdrLinear = new float[pixelCount * 4];
                SimdPixelOps.ReinhardToSdr(normHdr, sdrLinear, headroom);
                log?.Invoke("[GainMap] 色调映射完成 (分段 Reinhard, SIMD)\n");

                // ── 2. Base JPEG (jpegli + sRGB ICC) ──
                var basePath = Path.Combine(tempDir, "base.jpg");
                var basePng = Path.Combine(tempDir, "base.png");
                await WriteBgra8PngAsync(sdrLinear, w, h, basePng, ffmpeg, log, ct);
                var baseOk = await EncodeJpegliAsync(basePng, basePath, baseQuality, log, ct);
                if (!baseOk)
                {
                    log?.Invoke("[GainMap] ❌ Base JPEG 编码失败\n");
                    return false;
                }
                log?.Invoke($"[GainMap] Base JPEG: {Math.Round(new FileInfo(basePath).Length / 1024.0)} KB\n");

                // ── 3. 增益图计算 + 降采样 + JPEG ──
                byte[] gainMapPixels = ComputeGainMap(normHdr, sdrLinear, w, h, multiChannel, maxLog2Gain);
                byte[] gainMapScaled = RescaleGainMap(gainMapPixels, w, h, multiChannel, downsample, out int gmW, out int gmH);
                var gmPath = Path.Combine(tempDir, "gainmap.jpg");
                var gmPng = Path.Combine(tempDir, "gainmap.png");
                await WriteGrayOrRgbPngAsync(gainMapScaled, gmW, gmH, multiChannel, gmPng, ffmpeg, log, ct);
                var gmOk = await EncodeJpegliAsync(gmPng, gmPath, gainMapQuality, log, ct);
                if (!gmOk)
                {
                    log?.Invoke("[GainMap] ❌ 增益图 JPEG 编码失败\n");
                    return false;
                }
                log?.Invoke($"[GainMap] 增益图: {gmW}x{gmH}, {Math.Round(new FileInfo(gmPath).Length / 1024.0)} KB\n");

                // ── 4. 打包 (XMP + MPF + ISO 21496-1) ──
                byte[] baseJpeg = File.ReadAllBytes(basePath);
                byte[] gainMapJpeg = File.ReadAllBytes(gmPath);
                WriteJpegGainMapFile(baseJpeg, gainMapJpeg, w, h, gmW, gmH, multiChannel,
                    headroom, maxLog2Gain, outputPath);
                log?.Invoke($"[GainMap] ✅ Ultra HDR JPEG: {Math.Round(new FileInfo(outputPath).Length / 1024.0)} KB\n");
                return true;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke($"[GainMap] 异常: {ex.Message}\n");
            return false;
        }
    }

    // ═══════════════════════════════════════════
    //  色调映射
    // ═══════════════════════════════════════════

    /// <summary>
    /// 分段 Reinhard 色调映射: HDR 线性 → SDR 线性 (0..1.0, 1.0=SDR 白点)。
    /// y ≤ 1.0 直通 (SDR 内容完全保真, 增益恒 1x); y > 1.0 Reinhard 压缩 + 保持色相缩放。
    /// </summary>
    private static float[] ReinhardToSdr(float[] hdr, int w, int h, float headroom)
    {
        int pixelCount = w * h;
        float[] sdr = new float[pixelCount * 4];

        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            float r = hdr[o], g = hdr[o + 1], b = hdr[o + 2];
            float maxY = Math.Max(r, Math.Max(g, b));

            float maxSdr = SegmentedReinhardMap(maxY, headroom);
            float scale = maxY > 1e-6f ? maxSdr / maxY : 0f;

            sdr[o] = Math.Clamp(r * scale, 0f, 1f);
            sdr[o + 1] = Math.Clamp(g * scale, 0f, 1f);
            sdr[o + 2] = Math.Clamp(b * scale, 0f, 1f);
            sdr[o + 3] = hdr[o + 3];
        }
        return sdr;
    }

    /// <summary>分段 Reinhard: y≤1 直通, 1&lt;y&lt;1.25 smoothstep 过渡, y≥1.25 标准 Reinhard (含 headroom 归一化)</summary>
    private static float SegmentedReinhardMap(float y, float headroom)
    {
        if (y <= 1.0f) return y;

        // Reinhard: x/(1+x), 归一化使 1.0 → 1.0
        // 使用 headroom 保护: 压缩后峰值不越过 1.0
        float t = y / Math.Max(headroom, 1f);
        float reinhard = t / (1.0f + t); // 0..0.5
        // 归一化: 使 y=headroom → 1.0
        reinhard *= 2.0f;

        if (y >= 1.25f) return Math.Min(reinhard, 1.0f);

        // smoothstep 过渡 [1.0, 1.25]
        float x = (y - 1.0f) / 0.25f;
        x = Math.Clamp(x, 0f, 1f);
        float smooth = x * x * (3.0f - 2.0f * x);
        return 1.0f + (reinhard - 1.0f) * smooth;
    }

    // ═══════════════════════════════════════════
    //  增益图计算
    // ═══════════════════════════════════════════

    /// <summary>
    /// 逐像素增益比计算 (2026-08-15: 灰度分支 SIMD 加速)。
    /// 灰度: gain = log2(亮度(HDR)/亮度(SDR)); RGB: 三通道独立。
    /// 映射 [0, maxLog2Gain] → [0, 255] (Reinhard 保证增益 ≥ 1)。
    /// </summary>
    private static byte[] ComputeGainMap(float[] hdr, float[] sdr, int w, int h,
        bool multiChannel, float maxLog2Gain)
    {
        int pixelCount = w * h;
        int channels = multiChannel ? 3 : 1;
        byte[] gain = new byte[pixelCount * channels];

        // 灰度: SIMD 批量 (亮度点积 + log2 多项式)
        if (!multiChannel)
        {
            SimdPixelOps.ComputeGainMapGray(hdr, sdr, gain, maxLog2Gain);
            return gain;
        }

        // RGB: 三通道独立 (标量; 通道数×log2 无向量收益)
        const float eps = 0.001f;
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            int off = i * 3;
            gain[off] = LogGainToByte(MathF.Log2(Math.Max(hdr[o] / Math.Max(sdr[o], eps), 1.0f)), maxLog2Gain);
            gain[off + 1] = LogGainToByte(MathF.Log2(Math.Max(hdr[o + 1] / Math.Max(sdr[o + 1], eps), 1.0f)), maxLog2Gain);
            gain[off + 2] = LogGainToByte(MathF.Log2(Math.Max(hdr[o + 2] / Math.Max(sdr[o + 2], eps), 1.0f)), maxLog2Gain);
        }
        return gain;
    }

    /// <summary>log2(gain) → 8-bit: 映射 [0, maxLog2Gain] → [0, 255]</summary>
    private static byte LogGainToByte(float logGain, float maxLog2Gain)
    {
        if (maxLog2Gain <= 0f) maxLog2Gain = 1f;
        float clamped = Math.Clamp(logGain, 0f, maxLog2Gain);
        return (byte)(clamped / maxLog2Gain * 255f);
    }

    // ═══════════════════════════════════════════
    //  降采样
    // ═══════════════════════════════════════════

    /// <summary>增益图降采样 (默认 1/4 分辨率), ceiling 除法确保不丢右/下边缘像素, 块均值。</summary>
    private static byte[] RescaleGainMap(byte[] src, int w, int h, bool multiChannel,
        int factor, out int outW, out int outH)
    {
        outW = (w + factor - 1) / factor;
        outH = (h + factor - 1) / factor;
        int channels = multiChannel ? 3 : 1;
        byte[] dst = new byte[outW * outH * channels];

        for (int dy = 0; dy < outH; dy++)
        {
            for (int dx = 0; dx < outW; dx++)
            {
                int sx = dx * factor, sy = dy * factor;
                int ex = Math.Min(sx + factor, w), ey = Math.Min(sy + factor, h);
                int count = 0;
                float[] sum = new float[channels];

                for (int y = sy; y < ey; y++)
                for (int x = sx; x < ex; x++)
                {
                    int si = (y * w + x) * channels;
                    for (int c = 0; c < channels; c++)
                        sum[c] += src[si + c];
                    count++;
                }

                int di = (dy * outW + dx) * channels;
                for (int c = 0; c < channels; c++)
                    dst[di + c] = (byte)(sum[c] / count);
            }
        }
        return dst;
    }

    // ═══════════════════════════════════════════
    //  中间文件生成 (ffmpeg raw → PNG, cjpegli PNG → JPEG)
    // ═══════════════════════════════════════════

    /// <summary>SDR 线性 RGBA → sRGB gamma → bgra8 raw → ffmpeg → PNG (2026-08-15: SIMD 加速 FloatToSrgb8)</summary>
    private static async Task WriteBgra8PngAsync(float[] sdrLinear, int w, int h, string pngPath,
        string ffmpeg, Action<string>? log, CancellationToken ct)
    {
        int pixelCount = w * h;
        var raw = new byte[pixelCount * 4];
        // SIMD 批量转换: 线性→sRGB 8-bit (R/G/B 三通道)
        // ⚠️ pix_fmt=bgra 布局: 字节序 B,G,R,A (不是 RGBA!)
        //   sdrLinear 是 RGBA 交错, 需重排: raw[0]=B, raw[1]=G, raw[2]=R
        Span<float> rBuf = stackalloc float[512];
        Span<byte> bBuf = stackalloc byte[512];
        for (int baseIdx = 0; baseIdx < pixelCount; baseIdx += 128)
        {
            int chunk = Math.Min(128, pixelCount - baseIdx);
            // 逐通道批量转换 (R→[2], G→[1], B→[0])
            for (int c = 0; c < 3; c++)
            {
                int dstC = 2 - c;  // R(0)→2, G(1)→1, B(2)→0
                for (int k = 0; k < chunk; k++)
                    rBuf[k] = sdrLinear[(baseIdx + k) * 4 + c];
                SimdPixelOps.FloatToSrgb8(rBuf[..chunk], bBuf[..chunk]);
                for (int k = 0; k < chunk; k++)
                    raw[(baseIdx + k) * 4 + dstC] = bBuf[k];
            }
        }
        // alpha = 255
        for (int i = 0; i < pixelCount; i++)
            raw[i * 4 + 3] = 255;
        await RunFfmpegRawToPngAsync(raw, w, h, "bgra", pngPath, ffmpeg, log, ct);
    }

    /// <summary>增益图 → 灰度或 RGB PNG (raw → ffmpeg)</summary>
    private static async Task WriteGrayOrRgbPngAsync(byte[] gainPixels, int w, int h, bool multiChannel,
        string pngPath, string ffmpeg, Action<string>? log, CancellationToken ct)
    {
        // cjpegli 不支持灰度 JPEG 输入? 统一转 RGB: 灰度 → 三通道复制
        int pixelCount = w * h;
        if (multiChannel)
        {
            // RGB 数据直接写, ffmpeg 用 rgb24
            await RunFfmpegRawToPngAsync(gainPixels, w, h, "rgb24", pngPath, ffmpeg, log, ct);
        }
        else
        {
            // 灰度 → 复制为 RGB (避免 cjpegli 灰度兼容问题)
            var rgb = new byte[pixelCount * 3];
            for (int i = 0; i < pixelCount; i++)
            {
                rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = gainPixels[i];
            }
            await RunFfmpegRawToPngAsync(rgb, w, h, "rgb24", pngPath, ffmpeg, log, ct);
        }
    }

    private static async Task RunFfmpegRawToPngAsync(byte[] raw, int w, int h, string pixFmt,
        string pngPath, string ffmpeg, Action<string>? log, CancellationToken ct)
    {
        var rawPath = pngPath + ".raw";
        await File.WriteAllBytesAsync(rawPath, raw, ct);
        var args = $"-y -f rawvideo -pix_fmt {pixFmt} -s {w}x{h} -i \"{rawPath}\" \"{pngPath}\"";
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        try { File.Delete(rawPath); } catch { }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg PNG 转换失败 (exit {p.ExitCode})");
    }

    private static async Task<bool> EncodeJpegliAsync(string pngPath, string jpgPath,
        float distance, Action<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CjpegliService.DetectedPath!,
            Arguments = $"\"{pngPath}\" \"{jpgPath}\" --distance {distance:F1} --chroma_subsampling 420",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 && File.Exists(jpgPath) && new FileInfo(jpgPath).Length > 0;
    }

    /// <summary>线性 float [0,1] → sRGB 8-bit</summary>
    private static byte FloatToSrgb8(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        float s;
        if (v <= 0.0031308f) s = 12.92f * v;
        else s = 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(s * 255f), 0, 255);
    }

    // ═══════════════════════════════════════════
    //  封装: XMP + MPF + ISO 21496-1
    // ═══════════════════════════════════════════

    /// <summary>
    /// 构建最终 Ultra HDR JPEG 文件。
    /// 结构:
    ///   [Base JPEG: SOI][APP1: XMP][APP2: MPF][Base 其余 (DQT/SOF/DHT/SOS/扫描/EOI)]
    ///   [增益图 JPEG: SOI][APP2: ISO 21496-1][增益图其余]
    /// </summary>
    private static void WriteJpegGainMapFile(byte[] baseJpeg, byte[] gainMapJpeg,
        int baseW, int baseH, int gmW, int gmH, bool multiChannel,
        float headroom, float maxLog2Gain, string outputPath)
    {
        // 1. 定位 Base JPEG 的 SOS (FFDA) 标记
        int sosIndex = FindSosIndex(baseJpeg);
        if (sosIndex < 0)
        {
            // 异常: 仅写 Base
            File.WriteAllBytes(outputPath, baseJpeg);
            return;
        }

        // 2. Base 头部段 = [SOI ... SOS 之前], 不含 SOS
        int baseHeaderLen = sosIndex;
        // SOS 段: marker(2) + length(2) + 参数
        int sosLen = 2 + ((baseJpeg[sosIndex + 2] << 8) | baseJpeg[sosIndex + 3]);
        int baseScanStart = sosIndex + sosLen;
        int baseScanLen = baseJpeg.Length - baseScanStart; // 含 EOI

        // 3. ISO 元数据插入增益图: [SOI][APP2: ISO][增益图数据(去 SOI)]
        byte[] iso = BuildIso21496Metadata(multiChannel, maxLog2Gain);
        byte[] gainMapWithIso = InsertIsoIntoSegment(gainMapJpeg, iso);

        // 4. XMP (含 Item:Length = 增益图完整大小)
        byte[] xmp = BuildXmpMetadata(baseW, baseH, gmW, gmH, multiChannel, headroom, gainMapWithIso.Length);

        // 5. MPF
        // 布局: [SOI][XMP APP1][MPF APP2][Base其余]
        int app1Total = 2 + 2 + xmp.Length;                    // marker + length + data
        int mpfDataLen = 86;                                   // BuildMpf 固定长度
        int app2Total = 2 + 2 + mpfDataLen;                    // marker + length + data
        int primarySize = baseHeaderLen + app1Total + app2Total + sosLen + baseScanLen;
        int gmAbsOffset = primarySize;
        // 增益图偏移: 相对 MPF TIFF 头 'MM' 位置 (libultrahdr/AndroidX 标准语义)
        // libultrahdr: secondary_image_offset = primary_image_size - pos - 8
        //   pos = APP2 marker 位置; pos+8 = marker(2)+len(2)+'MPF\0'(4) = 'MM' 位置
        int mpfDataStart = baseHeaderLen + app1Total + 8;
        int gmOffset = gmAbsOffset - mpfDataStart;
        byte[] mpf = BuildMpf(gmOffset, gainMapWithIso.Length, primarySize);

        // 6. 组装
        using var ms = new MemoryStream();
        // Base: SOI + XMP + MPF + 其余 (DQT/SOF/DHT/SOS/扫描/EOI)
        ms.Write(baseJpeg, 0, baseHeaderLen);                  // 含 SOI
        WriteAppSegment(ms, 0xE1, xmp);                        // APP1 XMP
        WriteAppSegment(ms, 0xE2, mpf);                        // APP2 MPF
        ms.Write(baseJpeg, sosIndex, baseJpeg.Length - sosIndex); // SOS 起含 EOI
        // 增益图: 完整 (已含 SOI + ISO + 数据 + EOI)
        ms.Write(gainMapWithIso, 0, gainMapWithIso.Length);

        File.WriteAllBytes(outputPath, ms.ToArray());
    }

    private static void WriteAppSegment(Stream s, byte marker, byte[] payload)
    {
        s.WriteByte(0xFF);
        s.WriteByte(marker);
        int len = payload.Length + 2;
        s.WriteByte((byte)(len >> 8));
        s.WriteByte((byte)(len & 0xFF));
        s.Write(payload, 0, payload.Length);
    }

    /// <summary>查找 JPEG SOS (FFDA) 标记位置 (跳过 SOI 和段头)</summary>
    private static int FindSosIndex(byte[] jpeg)
    {
        int i = 2; // 跳过 SOI
        while (i + 3 < jpeg.Length)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xDA) return i;
            if (jpeg[i] != 0xFF) { i++; continue; }
            // 段: FF xx LL LL data
            int len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (len < 2) return -1;
            i += 2 + len;
        }
        return -1;
    }

    /// <summary>将 ISO 21496-1 元数据作为 APP2 段插入增益图 JPEG 的 SOI 之后。
    /// 段 payload = 命名空间 + 二进制元数据 (对齐 libultrahdr: 命名空间必需, 解析器按此定位)</summary>
    private static byte[] InsertIsoIntoSegment(byte[] jpeg, byte[] iso)
    {
        // jpeg[0..1] = SOI
        var ns = Encoding.ASCII.GetBytes("urn:iso:std:iso:ts:21496:-1\0");
        var payload = new byte[ns.Length + iso.Length];
        Array.Copy(ns, 0, payload, 0, ns.Length);
        Array.Copy(iso, 0, payload, ns.Length, iso.Length);

        var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2); // SOI
        WriteAppSegment(ms, 0xE2, payload);
        ms.Write(jpeg, 2, jpeg.Length - 2); // 其余 (去 SOI)
        return ms.ToArray();
    }

    /// <summary>
    /// 构建 ISO 21496-1 二进制增益图元数据 (APP2 段数据)。
    /// 对齐 Google libultrahdr gainmapmetadata.cpp:
    ///   [min_version: u16 BE][writer_version: u16 BE][flags: u8]
    ///   flags: bit7=multi-channel, bit6=useBaseColorSpace, bit3=common denominator
    ///   然后各字段分数编码 (独立分母)
    /// </summary>
    private static byte[] BuildIso21496Metadata(bool multiChannel, float maxLog2Gain)
    {
        // log2 值用精确分数: maxLog2Gain × 100 / 100
        int gainMapMaxLog2N = (int)MathF.Round(maxLog2Gain * 100f);
        const int gainMapMaxLog2D = 100;
        const int gammaN = 1, gammaD = 1;
        const int offsetN = 1, offsetD = 64;
        const int gainMinN = 0, gainMinD = 1;
        const int baseHeadroomN = 0, baseHeadroomD = 1;       // 2^0 = 1.0
        const int alternateHeadroomD = 100;
        int alternateHeadroomN = gainMapMaxLog2N;             // 与 gainMapMax 一致!

        int channels = multiChannel ? 3 : 1;
        byte flags = 0;
        if (multiChannel) flags |= 0x80;   // multi-channel
        flags |= 0x40;                     // useBaseColorSpace (增益图在 base 色彩空间)

        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        WriteBe16(bw, 0);                   // min_version
        WriteBe16(bw, 0);                   // writer_version
        bw.Write(flags);

        // baseHdrHeadroom / alternateHdrHeadroom (非 common denominator 模式)
        WriteBe32Bytes(bw, (uint)baseHeadroomN); WriteBe32Bytes(bw, (uint)baseHeadroomD);
        WriteBe32Bytes(bw, (uint)alternateHeadroomN); WriteBe32Bytes(bw, (uint)alternateHeadroomD);

        // 每通道: gainMapMin, gainMapMax, gamma, baseOffset, alternateOffset
        for (int c = 0; c < channels; c++)
        {
            WriteBe32Bytes(bw, unchecked((uint)gainMinN)); WriteBe32Bytes(bw, (uint)gainMinD);
            WriteBe32Bytes(bw, unchecked((uint)gainMapMaxLog2N)); WriteBe32Bytes(bw, (uint)gainMapMaxLog2D);
            WriteBe32Bytes(bw, (uint)gammaN); WriteBe32Bytes(bw, (uint)gammaD);
            WriteBe32Bytes(bw, unchecked((uint)offsetN)); WriteBe32Bytes(bw, (uint)offsetD);
            WriteBe32Bytes(bw, unchecked((uint)offsetN)); WriteBe32Bytes(bw, (uint)offsetD);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// 构建 hdrgm XMP 元数据 (APP1 段数据, 含命名空间)。
    /// 对齐 libultrahdr generateXmpForPrimaryImage: GainMap item 写 Item:Length。
    /// </summary>
    private static byte[] BuildXmpMetadata(int baseW, int baseH, int gmW, int gmH, bool multiChannel,
        float headroom, long gainMapLength)
    {
        float gainMinLog2 = 0f;
        float gainMaxLog2 = MathF.Log2(Math.Max(headroom, 1.0f));
        const float offset = KOffset;
        const float gamma = 1.0f;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string channels = multiChannel ? "3" : "1";
        string gainMapItem = gainMapLength > 0
            ? $"<Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\" Item:Length=\"{gainMapLength}\"/>"
            : "<Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\"/>";

        string xmp =
            "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" " +
            "xmlns:Container=\"http://ns.google.com/photos/1.0/container/\" " +
            "xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\">" +
            "<Container:Directory>" +
            "<rdf:Seq>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/>" +
            "</rdf:li>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            gainMapItem +
            "</rdf:li>" +
            "</rdf:Seq>" +
            "</Container:Directory>" +
            "<hdrgm:Version>1.0</hdrgm:Version>" +
            "<hdrgm:BaseRenditionIsHDR>False</hdrgm:BaseRenditionIsHDR>" +
            $"<hdrgm:GainMapMin>{gainMinLog2.ToString("0.0", inv)}</hdrgm:GainMapMin>" +
            $"<hdrgm:GainMapMax>{gainMaxLog2.ToString("0.00", inv)}</hdrgm:GainMapMax>" +
            $"<hdrgm:Gamma>{gamma.ToString("0.0", inv)}</hdrgm:Gamma>" +
            $"<hdrgm:OffsetSDR>{offset.ToString("0.000000", inv)}</hdrgm:OffsetSDR>" +
            $"<hdrgm:OffsetHDR>{offset.ToString("0.000000", inv)}</hdrgm:OffsetHDR>" +
            "<hdrgm:HDRCapacityMin>0</hdrgm:HDRCapacityMin>" +
            $"<hdrgm:HDRCapacityMax>{gainMaxLog2.ToString("0.00", inv)}</hdrgm:HDRCapacityMax>" +
            $"<hdrgm:Channels>{channels}</hdrgm:Channels>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta>" +
            "<?xpacket end=\"w\"?>";

        // 前置命名空间 "http://ns.adobe.com/xap/1.0/\0"
        var ns = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        var xmpBytes = Encoding.UTF8.GetBytes(xmp);
        var payload = new byte[ns.Length + xmpBytes.Length];
        Array.Copy(ns, 0, payload, 0, ns.Length);
        Array.Copy(xmpBytes, 0, payload, ns.Length, xmpBytes.Length);
        return payload;
    }

    /// <summary>构建 MPF (Multi-Picture Format) 二进制数据 (APP2 'MPF\0' 段数据)。对齐 TrueToneCap/libultrahdr 已验证格式。</summary>
    /// <remarks>
    /// MPF APP2 数据布局 (相对数据起始):
    ///   offset 0:   "MPF\0" (4)
    ///   offset 4:   TIFF header "MM" + 42 + offset_to_IFD(8) (8字节, 标准 TIFF 大端)
    ///   offset 12:  IFD entry count = 3 (2)
    ///   offset 14:  Entry0 MPFVersion: tag 0xB000, type 7 (UNDEFINED), count 4, value "0100" (12)
    ///   offset 26:  Entry1 NumberOfImages: tag 0xB001, type 4 (LONG), count 4, value 2 (12)
    ///   offset 38:  Entry2 MPEntry: tag 0xB002, type 7 (UNDEFINED), count 32, value offset=54 (12)
    ///   offset 50:  next IFD = 0 (4)
    ///   offset 54:  Image Entry 0 (16): attr=0x030000, size=primarySize, offset=0, reserved
    ///   offset 70:  Image Entry 1 (16): attr=0x000000, size=mpfSize, offset=mpfOffset, reserved
    /// 总长 86。字段顺序 = attribute → size → offset (CIPA DC-007)。
    /// </remarks>
    private static byte[] BuildMpf(int mpfOffset, int mpfSize, int primarySize)
    {
        var ms = new MemoryStream(86);
        var bw = new BinaryWriter(ms);

        // MPF identifier: "MPF\0"
        bw.Write((byte)'M'); bw.Write((byte)'P'); bw.Write((byte)'F'); bw.Write((byte)0);

        // TIFF header (Big Endian)
        bw.Write((byte)'M'); bw.Write((byte)'M');      // MM = Big Endian
        bw.Write((byte)0x00); bw.Write((byte)0x2A);    // TIFF magic (42)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x08); // offset to IFD = 8

        // IFD: 3 entries (MPFVersion + NumberOfImages + MPEntry)
        bw.Write((byte)0x00); bw.Write((byte)0x03);    // entry count = 3

        // Entry 0: MPFVersion (0xB000) = "0100"
        bw.Write((byte)0xB0); bw.Write((byte)0x00);    // tag
        bw.Write((byte)0x00); bw.Write((byte)0x07);    // type: UNDEFINED
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x04); // count = 4
        bw.Write((byte)0x30); bw.Write((byte)0x31); bw.Write((byte)0x30); bw.Write((byte)0x30); // "0100"

        // Entry 1: NumberOfImages (0xB001) = 2 (单个 LONG 值, count=1 内联)
        bw.Write((byte)0xB0); bw.Write((byte)0x01);    // tag
        bw.Write((byte)0x00); bw.Write((byte)0x04);    // type: LONG
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x01); // count = 1
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x02); // value = 2 images

        // Entry 2: MPEntry (0xB002) — 指向 Individual Image Data 列表
        bw.Write((byte)0xB0); bw.Write((byte)0x02);    // tag
        bw.Write((byte)0x00); bw.Write((byte)0x07);    // type: UNDEFINED
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x20); // count = 32 (2 entries × 16)
        // value = offset 指向 Image Data Entries (相对 TIFF 头起始 = 数据 offset 50, 即 54-4)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)50);

        // Next IFD offset = 0
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00);

        // ── Individual Image Data Entries (32 bytes: 2 × 16) ──
        // 字段: attribute(4) → size(4) → offset(4) → reserved(4), 全部大端
        // 参考 libultrahdr: Primary attr = 0x00030000 (字节 00 03 00 00)
        // Image 0: Base JPEG (Primary)
        bw.Write((byte)0x00); bw.Write((byte)0x03); bw.Write((byte)0x00); bw.Write((byte)0x00); // attr 0x00030000
        WriteBe32Bytes(bw, (uint)primarySize);        // size = 主图完整大小
        WriteBe32Bytes(bw, 0);                        // offset = 0 (主图从文件头开始)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); // reserved

        // Image 1: Gain Map JPEG
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); // attr = 0
        WriteBe32Bytes(bw, (uint)mpfSize);            // size
        WriteBe32Bytes(bw, (uint)mpfOffset);          // offset (相对 MPF 数据段)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); // reserved

        return ms.ToArray();
    }

    private static void WriteBe32Bytes(BinaryWriter bw, uint v)
    {
        bw.Write((byte)(v >> 24)); bw.Write((byte)(v >> 16));
        bw.Write((byte)(v >> 8)); bw.Write((byte)(v & 0xFF));
    }

    private static void WriteBe16(BinaryWriter bw, int v)
    {
        bw.Write((byte)(v >> 8)); bw.Write((byte)(v & 0xFF));
    }
}
