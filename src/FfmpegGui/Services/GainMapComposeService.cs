using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FfmpegGui.Models;
using FfmpegGui.Services.ColorMapping;

namespace FfmpegGui.Services;

/// <summary>
/// 两图 GainMap 合成：**SDR 底图 + HDR 参考** → 标准 Ultra HDR JPEG / AVIF tmap。
///
/// <para>与单输入 GainMap 出口（<see cref="GainMapEncoder.EncodeAsync"/> 的 A 路）的区别只在**输入轴**：
/// 这里两张图各自探测、各自定角色，底图按**自己的原色**转进底图色域，增益量程按这一对**实测**。
/// 像素阶段仍全部复用同一个编码器（单一真值，不另写一份增益数学）。</para>
///
/// <para><b>入参契约（不满足就拒绝，绝不回落）</b>：
/// ① 两张图显示尺寸一致（旋转后）；② SDR 槽不带 HDR 传递（PQ/HLG）；③ HDR 槽必须是 PQ/HLG；
/// ④ 底图裸流必须精确 = w×h×12 字节。回落类兜底（如 Reinhard）在这里是**错误**：
/// 两图模式下用户明确给了底图，换一张自造底图就是静默换内容。</para>
/// </summary>
public static class GainMapComposeService
{
    private static readonly HashSet<string> HdrTrc = new(StringComparer.OrdinalIgnoreCase)
    {
        "smpte2084", "arib-std-b67"
    };

    /// <summary>SDR 底图允许的传递函数（身份不明的 PNG/JPEG 按 sRGB 处理，这是它们的默认语义）。</summary>
    private static readonly HashSet<string> SdrTrc = new(StringComparer.OrdinalIgnoreCase)
    {
        "iec61966-2-1", "bt709", "srgb", "unknown", ""
    };

    /// <summary>
    /// 合成一对。返回 false 时**不留下产物**（自检失败会删掉半成品）——调用方据此标失败。
    /// </summary>
    public static async Task<bool> ComposeAsync(
        string sdrPath, string hdrPath, string outputPath,
        FfmpegOptions options, Action<string>? log = null, CancellationToken ct = default)
    {
        var ffmpeg = AppSettingsService.Current.FfmpegPath;
        var ffprobe = AppSettingsService.Current.FfprobePath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            log?.Invoke("[GainMap合成] ❌ 未找到 ffmpeg\n");
            return false;
        }
        if (!File.Exists(sdrPath)) { log?.Invoke($"[GainMap合成] ❌ SDR 底图不存在: {sdrPath}\n"); return false; }
        if (!File.Exists(hdrPath)) { log?.Invoke($"[GainMap合成] ❌ HDR 参考不存在: {hdrPath}\n"); return false; }

        var tempDir = PlatformServices.CreateTempSubDir("gainmapcompose");
        try
        {
            // ── 1-2. 尺寸与角色：与 UI 的入队校验**共用同一份实现**（ProbePairAsync），
            //         避免界面说通过、执行时换一套判据。 ──
            var probe = await ProbePairAsync(sdrPath, hdrPath, log, ct);
            if (!probe.Ok) return false;
            int w = probe.Width, h = probe.Height;
            long pixelCountL = (long)w * h;
            if (pixelCountL > int.MaxValue / 4L)
            {
                log?.Invoke($"[GainMap合成] ❌ 像素数过大（{w}×{h}）\n");
                return false;
            }
            int pixelCount = (int)pixelCountL;
            long needBytes = pixelCountL * 12L;                // gbrpf32le 三平面
            string sdrPrim = probe.SdrPrimaries;
            string hdrPrim = probe.HdrPrimaries;
            log?.Invoke($"[GainMap合成] 输入：SDR {w}×{h} {sdrPrim}/{probe.SdrTrc}"
                + $" → HDR {hdrPrim}/{probe.HdrTrc}（{probe.MetaPeakSource ?? "无静态峰值"}）\n");

            // ── 3. HDR 解线性（1.0 = npl），峰值 auto 时按内容实测后重解一次 ──
            int userPeak = options.GainMapComposePeakNits;
            double npl = userPeak > 0
                ? userPeak
                : (probe.MetaPeakNits > 0 ? probe.MetaPeakNits : 1000.0);
            string hdrRaw = Path.Combine(tempDir, "hdr.rgba");
            if (!await RunFfmpegAsync(GainMapJpegCodec.LinearDecodeArgs(hdrPath, hdrRaw, npl, hdrPrim), log, ffmpeg, ct))
                return false;
            if (!ExactLength(hdrRaw, needBytes, log, "HDR 解线性裸流")) return false;

            int peakNits = userPeak > 0 ? userPeak : (int)Math.Round(npl);
            if (userPeak <= 0)
            {
                var bytes = await File.ReadAllBytesAsync(hdrRaw, ct);
                double measured = MeasureMaxNits(bytes, pixelCount) * npl;
                measured = Math.Clamp(measured, 100.0, 10000.0);
                int measuredInt = (int)Math.Round(measured);
                if (Math.Abs(measuredInt - npl) / npl > 0.01)
                {
                    log?.Invoke($"[GainMap合成] 峰值 auto：按内容实测 {measuredInt} nits（首解 npl={npl:F0}），重解一次\n");
                    if (!await RunFfmpegAsync(GainMapJpegCodec.LinearDecodeArgs(hdrPath, hdrRaw, measuredInt, hdrPrim), log, ffmpeg, ct))
                        return false;
                    if (!ExactLength(hdrRaw, needBytes, log, "HDR 解线性裸流")) return false;
                }
                peakNits = measuredInt;
            }
            log?.Invoke($"[GainMap合成] 峰值：{peakNits} nits（{(userPeak > 0 ? "用户指定" : "内容实测")}）\n");

            var hdrRgba = GainMapJpegCodec.GbrPlanarToRgba(await File.ReadAllBytesAsync(hdrRaw, ct), pixelCount);

            // ── 4. SDR 底图裸流（sRGB 编码值，**自己的**原色；由编码器转到 base 色域）──
            string baseRaw = Path.Combine(tempDir, "base.rgba");
            if (!await RunFfmpegAsync(
                    $"-y -i \"{sdrPath}\" -frames:v 1 -pix_fmt gbrpf32le -f rawvideo \"{baseRaw}\"",
                    log, ffmpeg, ct))
                return false;
            if (!ExactLength(baseRaw, needBytes, log, "SDR 底图裸流")) return false;

            // ── 5. 编码 ──
            // 质量口径与专用 GainMap 管线同源（QueueProcessor）：底图用 Quality，增益图用 JpegGainMapQuality。
            float baseDist = Math.Clamp((100f - options.Quality) * 0.25f, 0.5f, 6.0f);
            int gmq = options.JpegGainMapQuality >= 0 ? options.JpegGainMapQuality : 75;
            float gmDist = Math.Clamp((100f - gmq) * 0.25f, 0.5f, 6.0f);
            int progressive = GainMapJpegCodec.ProgressiveLevelOf(options);
            string baseGamut = string.Equals(options.JpegGainMapBaseGamut, "bt2020", StringComparison.OrdinalIgnoreCase)
                ? "bt2020" : "srgb";

            // 面板选出的**具体编码器**按出口容器分派：AVIF 侧只认 AV1 编码器，
            // JPEG 侧只认 cjpegli/mjpeg（互不接受对方的词表；显式值不可用由引擎点名失败，不静默换）。
            bool isAvif = string.Equals(GainMapEncoder.ContainerOf(outputPath), "avif", StringComparison.OrdinalIgnoreCase);
            string? jpegEnc = isAvif ? null : options.GainMapComposeEncoder;
            string? avifEnc = isAvif ? options.GainMapComposeEncoder : null;

            // 质量口径：JPEG ⇒ butteraugli distance（滑块 → distance）；AVIF ⇒ CRF（滑块 → CRF，
            // 与主面板同一映射 `MapAvifCrfForward`）。两条出口各用各的量纲，绝不把一个当另一个传。
            int baseCrf = FfmpegOptions.MapAvifCrfForward(options.Quality);
            int gainCrf = FfmpegOptions.MapAvifCrfForward(gmq);
            string metadata = string.IsNullOrWhiteSpace(options.GainMapComposeMetadata)
                ? GainMapEncoder.GainMapMetadataDefault
                : options.GainMapComposeMetadata.Trim().ToLowerInvariant();

            log?.Invoke($"[GainMap合成] 编码：{w}×{h}，底图色域={baseGamut}，"
                + $"{(options.GainMapComposeMultiChannel ? "三通道" : "单通道")}增益，"
                + $"降采样 1/{Math.Max(options.JpegGainMapDownsample, 1)}，"
                + (isAvif
                    ? $"底图 CRF {baseCrf}/增益图 CRF {gainCrf}，元数据 {metadata}\n"
                    : $"底图 d={baseDist:F2}/增益图 d={gmDist:F2}，元数据 {metadata}\n"));

            bool ok = await GainMapEncoder.EncodeAsync(
                hdrRgba, w, h, outputPath,
                hdrPeakNits: peakNits, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
                multiChannel: options.GainMapComposeMultiChannel,
                baseQuality: baseDist, gainMapQuality: gmDist,
                downsample: Math.Max(options.JpegGainMapDownsample, 1),
                baseGamut: baseGamut, sourcePrimaries: hdrPrim,
                log: log, ct: ct,
                jpegProgressiveLevel: progressive,
                container: GainMapEncoder.ContainerOf(outputPath),
                sourceBaseRaw: baseRaw, inputIsSdrWhiteUnit: false,
                sourceBasePrimaries: sdrPrim, measureGainRange: true,
                jpegEncoder: jpegEnc, avifEncoder: avifEnc,
                gainMapMetadata: metadata, baseCrf: baseCrf, gainMapCrf: gainCrf);
            if (!ok)
            {
                TryDelete(outputPath);
                return false;
            }

            // ── 6. 回读自检：容器能解、尺寸对、底图在。失败 ⇒ 不留半成品 ──
            string reconRaw = Path.Combine(tempDir, "recon.rgba");
            string baseOut = Path.Combine(tempDir, "base-out.rgba");
            var dec = await GainMapDecoder.DecodeToLinearRawAsync(outputPath, reconRaw, log, ct, null, baseOut);
            if (dec == null || dec.Value.w != w || dec.Value.h != h)
            {
                log?.Invoke("[GainMap合成] ❌ 回读自检失败：产物无法按增益图解码，已删除\n");
                TryDelete(outputPath);
                return false;
            }
            if (dec.Value.baseRawPath == null)
            {
                log?.Invoke("[GainMap合成] ❌ 回读自检失败：产物里没有可读的 SDR 底图，已删除\n");
                TryDelete(outputPath);
                return false;
            }
            var baseLen = new FileInfo(dec.Value.baseRawPath).Length;
            if (baseLen != needBytes)
            {
                log?.Invoke($"[GainMap合成] ❌ 回读自检失败：底图裸流 {baseLen} B ≠ {needBytes} B，已删除\n");
                TryDelete(outputPath);
                return false;
            }
            log?.Invoke($"[GainMap合成] ✅ 完成：{Path.GetFileName(outputPath)}"
                + $"（回读 {dec.Value.w}×{dec.Value.h}，峰值 {dec.Value.peakNits:F0} nits，底图可读）\n");
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            log?.Invoke("[GainMap合成] 已取消\n");
            return false;
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            log?.Invoke($"[GainMap合成] ❌ 异常：{ex.GetType().Name}: {ex.Message}\n");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// 一对输入的探测结论。<see cref="Ok"/>=false 时 <see cref="RejectReason"/> 是给用户看的整句
    /// （已带 <c>[GainMap合成]</c> 前缀与换行）——界面直接显示，执行侧直接写日志。
    /// </summary>
    public sealed class PairProbe
    {
        public bool Ok;
        public string RejectReason = "";
        public int Width;
        public int Height;
        public string SdrPrimaries = "bt709";
        public string HdrPrimaries = "bt709";
        public string SdrTrc = "";
        public string HdrTrc = "";
        public double MetaPeakNits;
        public string? MetaPeakSource;
    }

    /// <summary>
    /// 探测并校验一对输入。**界面与执行侧共用这一份**：若各写一套，必然漂移成
    /// 「界面说可以、执行时说不行」（本仓反复修过这类缺陷）。
    /// </summary>
    public static async Task<PairProbe> ProbePairAsync(
        string sdrPath, string hdrPath, Action<string>? log = null, CancellationToken ct = default)
    {
        var r = new PairProbe();
        var ffprobe = AppSettingsService.Current.FfprobePath;
        var (sw, sh) = await ImageGeometry.DisplaySizeAsync(sdrPath, ffprobe, log, ct);
        var (hw, hh) = await ImageGeometry.DisplaySizeAsync(hdrPath, ffprobe, log, ct);
        if (sw <= 0 || sh <= 0 || hw <= 0 || hh <= 0)
        {
            r.RejectReason = $"[GainMap合成] ❌ 尺寸探测失败（SDR {sw}×{sh} / HDR {hw}×{hh}）\n";
            log?.Invoke(r.RejectReason);
            return r;
        }
        if (sw != hw || sh != hh)
        {
            r.RejectReason = $"[GainMap合成] ❌ 两张图尺寸不一致：SDR {sw}×{sh} vs HDR {hw}×{hh}"
                + "（本模式不缩放、不裁切 —— 请先统一尺寸）\n";
            log?.Invoke(r.RejectReason);
            return r;
        }

        var sdrMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(sdrPath, log, ct);
        var hdrMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(hdrPath, log, ct);
        string sdrTrc = (sdrMeta.colorTrc ?? "").Trim();
        string hdrTrc = (hdrMeta.colorTrc ?? "").Trim();
        if (HdrTrc.Contains(sdrTrc))
        {
            r.RejectReason = $"[GainMap合成] ❌ SDR 槽的文件带 HDR 传递（{sdrTrc}）：两图模式要求底图是 SDR 渲染\n";
            log?.Invoke(r.RejectReason);
            return r;
        }
        if (!SdrTrc.Contains(sdrTrc))
        {
            r.RejectReason = $"[GainMap合成] ❌ SDR 槽的传递函数是 {sdrTrc}，本模式只接受 sRGB/BT.709 编码的 SDR 底图"
                + "（线性/对数/HLG 底图会与 sRGB 解码约定打架）\n";
            log?.Invoke(r.RejectReason);
            return r;
        }
        if (!HdrTrc.Contains(hdrTrc))
        {
            r.RejectReason = $"[GainMap合成] ❌ HDR 槽没有可识别的 HDR 传递（探测到 {hdrTrc}）；"
                + "需要 PQ(smpte2084) 或 HLG(arib-std-b67)\n";
            log?.Invoke(r.RejectReason);
            return r;
        }

        r.Ok = true;
        r.Width = sw;
        r.Height = sh;
        r.SdrTrc = sdrTrc;
        r.HdrTrc = hdrTrc;
        r.SdrPrimaries = ResolvePrimaries(sdrMeta);
        r.HdrPrimaries = ResolvePrimaries(hdrMeta);
        r.MetaPeakNits = hdrMeta.peakNits;
        r.MetaPeakSource = hdrMeta.peakSource;
        return r;
    }

    private static string ResolvePrimaries(FfmpegCommandBuilder.ColorMetadata m)
    {
        var p = m.colorPrimaries;
        if (!string.IsNullOrWhiteSpace(p) && p != "unspecified" && p != "unknown") return p!;
        var fromGamut = ColorSpaceRegistry.FfmpegTokens(m.sourceGamutName).primaries;
        return string.IsNullOrWhiteSpace(fromGamut) ? "bt709" : fromGamut;
    }

    /// <summary>解出的线性值以 1.0 = npl ⇒ 内容绝对峰值 = max(R,G,B) × npl。</summary>
    private static double MeasureMaxNits(byte[] raw, int pixelCount)
    {
        int floats = pixelCount * 3;
        if (raw.Length < (long)floats * 4L) return 0;
        var f = new float[floats];
        Buffer.BlockCopy(raw, 0, f, 0, floats * 4);
        float max = 0f;
        for (int i = 0; i < floats; i++)
        {
            float v = f[i];
            if (v > max) max = v;
        }
        return max;
    }

    private static bool ExactLength(string path, long need, Action<string>? log, string what)
    {
        long len = new FileInfo(path).Length;
        if (len == need) return true;
        log?.Invoke($"[GainMap合成] ❌ {what}长度不符：{len} B ≠ {need} B（w×h×3×4B）\n");
        return false;
    }

    private static async Task<bool> RunFfmpegAsync(string args, Action<string>? log, string ffmpeg, CancellationToken ct)
    {
        int rc = await FfmpegRunner.RunAsync(args, log, ffmpeg, ct);
        if (rc != 0)
        {
            log?.Invoke($"[GainMap合成] ❌ ffmpeg 失败（rc={rc}）\n");
            return false;
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
