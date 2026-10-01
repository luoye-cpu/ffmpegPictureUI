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
///     ├─ 【纯托管兜底】容器直读: ISO 21496-1 APP2 二进制元数据 + MPF/副图切割 (无需 exiftool)
///     ├─ exiftool 读取 hdrgm XMP 元数据 (GainMapMin/Max/Gamma/OffsetSDR/OffsetHDR/Channels)
///     ├─ exiftool 提取增益图 JPEG (MPImage2)
///     ├─ ffmpeg 解码原文件 → sRGB 8-bit 基础图 → gbrpf32le 线性
///     ├─ ffmpeg 解码增益图 → gbrpf32le (0..1)
///     ├─ C# 双线性插值放大增益图 + 应用增益公式 → HDR 线性像素
///     └─ 输出 gbrpf32le 线性 HDR (1.0 = SDR 白点)
///
/// 元数据/副图来源优先级 (ISO 21496-1 是规范权威, XMP 是 Adobe/Android 兼容补充):
///   1. ISO 21496-1 APP2 (纯托管解析, 无进程开销; 缺字段时由 XMP 补)
///   2. hdrgm XMP (需 exiftool; 无 ISO 的旧 Ultra HDR 文件)
///   副图切割: ISO 所在图像 → MPF entry[1] 偏移 → 顶层第二张 JPEG(SOI..EOI) → exiftool -MPImage2
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
        public float HdrCapacityMax = 0;  // log2 容量上限 (ISO alternateHdrHeadroom)
        public float HdrCapacityMin = 0;  // log2 容量下限 (ISO baseHdrHeadroom; SDR 基图恒 0 = 1×)
        public bool BaseRenditionIsHdr;   // true=基础图是 HDR (通常 false)
        /// <summary>
        /// useBaseColorSpace（ISO flags bit6 / XMP hdrgm:UseBaseRenderingColorSpace）：
        /// true = 增益图定义在**底图色域**内（标准写法，本项目产出恒 true）；
        /// false = 定义在 alternate(HDR) 色域，严格应用前需先进色域转换（此时本解码器按底图色域近似）。
        /// </summary>
        public bool UseBaseColorSpace = true;
        public int GainMapWidth;
        public int GainMapHeight;
        // ── hdrgm XMP 声明的「逻辑尺寸」(AndroidX JpegImageCodec 读法; 0=元数据未声明) ──
        // JPEG 解码尺寸受 8/16 像素对齐影响（可能大于逻辑尺寸），若按解码尺寸归一化增益图坐标
        // 会引入半个宏块的偏移（边缘增益错位），故声明尺寸存在时优先用它。
        public int MapPrimaryWidth;
        public int MapPrimaryHeight;
        public int MapSecondaryWidth;
        public int MapSecondaryHeight;
        public float MapImageRatio = 1f;
    }

    /// <summary>
    /// 解码 Ultra HDR JPEG → 线性 HDR 像素文件 (gbrpf32le rawvideo, 1.0 = SDR 白点)。
    /// </summary>
    /// <param name="inputPath">Ultra HDR JPEG 路径</param>
    /// <param name="outputRawPath">输出 gbrpf32le 线性 HDR 像素</param>
    /// <param name="log">日志回调</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成功返回 (宽度, 高度, 峰值亮度nits, 线性像素的 cjxl color_space 原色语义), 失败返回 null</returns>
    public static async Task<(int w, int h, float peakNits, string? jxlColorSpace)?> DecodeToLinearRawAsync(
        string inputPath, string outputRawPath,
        Action<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            // exiftool 不再是硬依赖：仅在 ISO 21496-1 元数据/MPF 不可用的文件上才需要（旧 Ultra HDR）。
            if (!ExifToolService.IsAvailable)
                log?.Invoke("[GainMap解码] ℹ️ exiftool 不可用，仅能读取带 ISO 21496-1 元数据的增益图文件\n");
            if (!CjpegliService.IsAvailable && !string.IsNullOrWhiteSpace(AppSettingsService.Current.FfmpegPath))
            {
                // cjpegli 仅用于测试对比, 解码不需要
            }

            var ffmpeg = AppSettingsService.Current.FfmpegPath;
            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"gmdecode_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // ── Step 0: 纯托管容器解析 (一次读文件 → ISO 21496-1 元数据 + 副图字节) ──
                //   JPEG 与 ISO-BMFF 的识别头互斥（`FF D8` vs `ftyp`）⇒ 可先后尝试，不会互相误判。
                JpegContainer? container = null;
                IsoBmffGainMapProbe.Result? bmff = null;
                try
                {
                    if (new FileInfo(inputPath).Length <= MaxManagedParseBytes)
                    {
                        var headBytes = await File.ReadAllBytesAsync(inputPath, ct);
                        container = JpegContainer.Parse(headBytes);
                        if (container == null) bmff = IsoBmffGainMapProbe.Probe(headBytes);
                    }
                    else
                        log?.Invoke("[GainMap解码] ℹ️ 文件过大，跳过纯托管容器解析（走 exiftool）\n");
                }
                catch { container = null; }
                var isoMeta = container?.IsoMetadata;

                // ── Step 1: 读取元数据 (ISO 优先, exiftool XMP 补全/兜底) ──
                var meta = isoMeta;
                // ISO-BMFF (AVIF/HEIC)：`tmap` item 载荷 = `u8 version` + ISO 21496-1 GainMapMetadata。
                // 该元数据是**独立分母**形式（与 JPEG APP2 载荷同构）⇒ 同一解析器可复用，只需跳过 version 字节。
                // ⚠ exiftool 对这类文件只给得出 `XMP-hdrgm:Version`（**无数值**）⇒ 本分支是这条路径的
                //   **唯一**元数据来源，绝不能指望 XMP 兜底。
                if (meta == null && bmff?.HasGainMap == true && bmff.TmapPayload != null)
                {
                    meta = JpegContainer.ParseIso21496(bmff.TmapPayload, 1, bmff.TmapPayload.Length - 1);
                    if (meta == null)
                    {
                        log?.Invoke("[GainMap解码] ❌ ISO-BMFF tmap 载荷解析失败（非预期版本/截断）\n");
                        return null;
                    }
                    // 方向：ISO 21496-1 的 flags 里**没有**「底图是否 HDR」位（libavif 不写 backwardDirection），
                    // 只能由 headroom 比较推出 —— base > alternate ⇒ 底图已是 HDR（增益图用于下映射到 SDR）。
                    meta.BaseRenditionIsHdr = meta.HdrCapacityMin > meta.HdrCapacityMax;
                    log?.Invoke($"[GainMap解码] 元数据来源: ISO-BMFF tmap item（{bmff.TmapPayload.Length} B，形式 {bmff.Form}）\n");
                }
                var xmpMeta = await ReadMetadataAsync(inputPath, log, ct);
                if (meta == null)
                {
                    meta = xmpMeta;
                    if (meta != null) log?.Invoke("[GainMap解码] 元数据来源: hdrgm XMP (exiftool)\n");
                }
                else if (isoMeta != null)
                {
                    log?.Invoke($"[GainMap解码] 元数据来源: ISO 21496-1 APP2 (纯托管)" +
                        (xmpMeta != null ? " + XMP 补全" : "") + "\n");
                    MergeFromXmp(meta, xmpMeta, log);
                }
                if (meta == null)
                {
                    log?.Invoke("[GainMap解码] ❌ 无法读取增益图元数据 (无 ISO 21496-1, 且 exiftool/hdrgm XMP 不可用)\n");
                    return null;
                }
                log?.Invoke($"[GainMap解码] 元数据: min={meta.GainMapMin:F3} max={meta.GainMapMax:F3} " +
                    $"gamma={meta.Gamma:F2} offsetSDR={meta.OffsetSdr:F4} offsetHDR={meta.OffsetHdr:F4} " +
                    $"Channels={meta.Channels} hdrBase={meta.BaseRenditionIsHdr}\n");

                // 反向方向（底图已是 HDR）：增益图的用途是把 HDR 下映射成 **SDR 预览**，本工具不需要它；
                // 而按 SDR 底解读会产出错图 ⇒ 明确点名并把决定权交回调用方（按普通 HDR 输入处理）。
                // ⚠ 仅对 ISO-BMFF 生效：JPEG 路径的历史行为（告警后仍按 SDR 底解读）**保持不变**，不动既有门禁口径。
                if (bmff != null && meta.BaseRenditionIsHdr)
                {
                    log?.Invoke("[GainMap解码] ⚠️ 增益图方向为「HDR 底图 → SDR」(backward)：底图本身已是 HDR，本工具不使用增益图\n");
                    return null;
                }

                // ── Step 2: 提取增益图 (ISO-BMFF 走 item 字节; 否则纯托管切割优先, exiftool 兜底) ──
                //   `gmFmtArgs` = 交给 ffmpeg 的**裸流解复用器**：ISO-BMFF 的增益图 item 没有容器头，
                //   只有 OBU / NAL 裸流（`-f obu` / `-f hevc`），不显式指定就会「探测不到流」而失败。
                var gmPath = Path.Combine(tempDir, "gainmap.jpg");
                string gmFmtArgs = "";
                bool gmOk;
                if (bmff?.HasGainMap == true)
                {
                    if (bmff.GainMapItemData == null || bmff.GainMapItemData.Length <= 100
                        || bmff.GainMapFfmpegFormat == null)
                    {
                        log?.Invoke("[GainMap解码] ❌ ISO-BMFF 增益图 item 不可用（字节缺失或类型无已知解复用器）\n");
                        return null;
                    }
                    gmPath = Path.Combine(tempDir,
                        bmff.GainMapFfmpegFormat == "obu" ? "gainmap.obu" : "gainmap.hevc");
                    await File.WriteAllBytesAsync(gmPath, bmff.GainMapItemData, ct);
                    gmFmtArgs = $"-f {bmff.GainMapFfmpegFormat} ";
                    gmOk = true;
                    log?.Invoke($"[GainMap解码] 增益图来源: ISO-BMFF item {bmff.GainMapItemId}（{bmff.GainMapCodec}，{bmff.GainMapItemData.Length} B）\n");
                }
                else
                {
                    gmOk = TryWriteSecondaryManaged(container, gmPath, log);
                    if (!gmOk)
                        gmOk = await ExtractGainMapJpegAsync(inputPath, gmPath, log, ct);
                }
                if (!gmOk)
                {
                    log?.Invoke("[GainMap解码] ❌ 增益图提取失败\n");
                    return null;
                }

                // ── Step 3: 解码基础图 (整个文件 ffmpeg 直接解码得 sRGB 基础图) → 线性 float ──
                var (baseW, baseH) = await ProbeSizeAsync(inputPath, ffmpeg, log, ct);
                if (baseW <= 0 || baseH <= 0)
                {
                    log?.Invoke("[GainMap解码] ❌ 无法获取基础图尺寸\n");
                    return null;
                }
                var baseRawPath = Path.Combine(tempDir, "base.rgba");
                // sRGB 编码值 → gbrpf32le (ffmpeg 解码 JPEG 输出 0..1 sRGB 值)
                // ⚠ ISO-BMFF（AVIF/HEIC）必须**显式 `-map 0:0` + `-frames:v 1`**：ffmpeg 的 mov 解复用器
                //   会把底图、**增益图**（乃至缩略图）各暴露成一条视频流，不写 `-map` 时的选择是隐式的；
                //   而 rawvideo **无头**，下游只做「长度下界」检查 ⇒ 错流/多帧会被静默放行。
                //   （`-map`/`-frames:v` 都是**输出侧**选项，必须放在 `-i` 之后。）
                var baseArgs = bmff != null
                    ? $"-y -i \"{inputPath}\" -map 0:0 -frames:v 1 -pix_fmt gbrpf32le -f rawvideo \"{baseRawPath}\""
                    : $"-y -i \"{inputPath}\" -pix_fmt gbrpf32le -f rawvideo \"{baseRawPath}\"";
                var baseExit = await RunFfmpegAsync(baseArgs, ffmpeg, log, ct);
                if (baseExit != 0 || !File.Exists(baseRawPath))
                {
                    log?.Invoke("[GainMap解码] ❌ 基础图解码失败\n");
                    return null;
                }

                // ── Step 4: 解码增益图 → float 0..1 ──
                var (gmW, gmH) = await ProbeSizeAsync(gmPath, ffmpeg, log, ct, gmFmtArgs);
                if (gmW <= 0 || gmH <= 0)
                {
                    log?.Invoke("[GainMap解码] ❌ 无法获取增益图尺寸\n");
                    return null;
                }
                meta.GainMapWidth = gmW;
                meta.GainMapHeight = gmH;
                var gmRawPath = Path.Combine(tempDir, "gm.rgba");
                var gmArgs = $"-y {gmFmtArgs}-i \"{gmPath}\" -frames:v 1 -pix_fmt gbrpf32le -f rawvideo \"{gmRawPath}\"";
                var gmExit = await RunFfmpegAsync(gmArgs, ffmpeg, log, ct);
                if (gmExit != 0 || !File.Exists(gmRawPath))
                {
                    log?.Invoke("[GainMap解码] ❌ 增益图解码失败\n");
                    return null;
                }

                // ── Step 5: 得到线性 HDR ──
                //   SDR 基图(sRGB 编码): 线性化 → 应用增益(上映射)
                //   HDR 基图(PQ 编码, BaseRenditionIsHDR): 基图本身已是 HDR，增益图描述 base→SDR 下映射
                //     → 取 HDR 像素无需应用增益，直接 PQ EOTF 后缩放到「1.0 = SDR 白点」约定
                // ⚠️ 整数溢出防线：JPEG SOF 尺寸上限 65535×65535 = 4.29e9 > int.MaxValue。
                //    `baseW * baseH` 按 int 相乘会溢出（变负或变小），令下面的长度自检**形同虚设**，
                //    畸形文件即可绕过守卫并在后续 `new byte[...]` 抛 OverflowException（只靠外层 catch 兜底）。
                //    ⇒ 全程 long 运算 + 单帧像素数上限（与 IsoBmffGainMapProbe 对 box 长度一律 long 校验同口径）。
                // 上限取 1 亿像素：×12 字节 = 1.2e9 < int.MaxValue ⇒ 后续 `new byte[...]`、平面跨步
                // 与 int 索引都**不可能**溢出；正常照片（含中画幅）远低于此。
                const long MaxDecodedPixels = 100_000_000L;
                long basePixels = (long)baseW * baseH;
                long gmPixels   = (long)gmW * gmH;
                if (basePixels <= 0 || basePixels > MaxDecodedPixels
                    || gmPixels <= 0 || gmPixels > MaxDecodedPixels)
                {
                    log?.Invoke($"[GainMap解码] ❌ 尺寸不可处理: base {baseW}x{baseH}, 增益图 {gmW}x{gmH}\n");
                    return null;
                }

                var baseBytes = await File.ReadAllBytesAsync(baseRawPath, ct);
                if (baseBytes.Length < basePixels * 12L)
                {
                    log?.Invoke("[GainMap解码] ❌ 像素数据不完整\n");
                    return null;
                }
                bool hdrBase = meta.BaseRenditionIsHdr;
                var baseRgb = PlanarToInterleaved(baseBytes, (int)basePixels);
                float[] hdrRgb;

                // 底图始终按 **SDR** 解读（本项目与 Ultra HDR 标准的底图定义）。
                // 第三方文件若声明 HDR 底（ISO backwardDirection / BaseRenditionIsHDR=True），
                // libultrahdr 自身亦不支持该方向 → 明确告警后仍按 SDR 底处理（不静默产出错图）。
                if (hdrBase)
                    log?.Invoke("[GainMap解码] ⚠️ 该文件声明 HDR 底图（BaseRenditionIsHDR=True），本工具不支持 HDR 底；按 SDR 底解读\n");
                if (!meta.UseBaseColorSpace)
                    log?.Invoke("[GainMap解码] ⚠️ useBaseColorSpace=False（增益图定义在 alternate 色域），已按底图色域近似应用\n");

                var gmBytes = await File.ReadAllBytesAsync(gmRawPath, ct);
                if (gmBytes.Length < gmPixels * 12L)
                {
                    log?.Invoke("[GainMap解码] ❌ 像素数据不完整\n");
                    return null;
                }
                // sRGB → 线性（底图无论 sRGB 还是 Rec.2020 均用 sRGB 传递编码，见 GainMapEncoder）
                SimdPixelOps.SrgbToLinearRgba(baseRgb);
                var gmRgb = PlanarToInterleaved(gmBytes, (int)gmPixels);
                // 应用增益 (双线性插值增益图 → 基础图分辨率)
                hdrRgb = ApplyGainMap(baseRgb, gmRgb, baseW, baseH, gmW, gmH, meta);

                // 输出 gbrpf32le (平面 G,B,R)
                // ⚠️ BlockCopy 是原始字节拷贝, 不能从交错 RGBA 抽取平面!
                //   必须逐像素跨步抽取 (此前用 BlockCopy 导致输出错乱)
                int basePx = (int)basePixels;                 // 已由上面的上限检查保证可安全转 int
                var outBytes = new byte[basePx * 12];          // 上限检查已保证 basePx*12 < int.MaxValue
                Span<byte> outSpan = outBytes;
                int planeStride = basePx * 4;
                for (int i = 0; i < basePx; i++)
                {
                    int o = i * 4;
                    // gbrpf32le 平面顺序 = G, B, R
                    BitConverter.TryWriteBytes(outSpan.Slice(o, 4), hdrRgb[o + 1]);                    // G
                    BitConverter.TryWriteBytes(outSpan.Slice(planeStride + o, 4), hdrRgb[o + 2]);      // B
                    BitConverter.TryWriteBytes(outSpan.Slice(planeStride * 2 + o, 4), hdrRgb[o]);      // R
                }

                await File.WriteAllBytesAsync(outputRawPath, outBytes, ct);
                // 峰值亮度：SDR 底 = 203 × 2^headroom（ISO alternateHdrHeadroom）
                // ⚠️ 权威值是 **HDRCapacityMax**（= alternateHdrHeadroom 的 log2），不是 GainMapMax：
                //    ISO 21496-1 允许 gainMapMax **大于** alternateHdrHeadroom（增益图可覆盖比实际
                //    所需更宽的增益范围）。第三方文件（Adobe 等）两者不等时用 GainMapMax 会**高估**
                //    峰值 ⇒ 传给 cjxl 的 --intensity-target 偏大、HDR 显得发灰。
                //    自家产物两者恒相等（见 GainMapEncoder.BuildXmpMetadata）⇒ 本改动不影响既有基线。
                float peakLog2 = meta.HdrCapacityMax > 0f ? meta.HdrCapacityMax : meta.GainMapMax;
                var peakNits = GainMapEncoder.KSdrWhiteNits * MathF.Pow(2f, peakLog2);
                // 线性像素的原色语义 = **底图自己的色域**（由底图 ICC 读出，JPEG 无 CICP 可用）：
                //   bt709→RGB_D65_SRG_Rel_Lin、bt2020→RGB_D65_202_Rel_Lin、smpte432→RGB_D65_P3_Rel_Lin。
                //   无 ICC 时按 JPEG 惯例视为 sRGB。
                string basePrim;
                try
                {
                    // JPEG：底图色域只能由**底图段 ICC** 表达（对齐 libultrahdr writeIccProfile）⇒ 走原判据。
                    // ISO-BMFF（AVIF/HEIC）：无此约定，底图色域由容器的 **CICP** 表达 ⇒ 直接读 ffprobe 的
                    //   color_primaries（`FfmpegCommandBuilder.ProbeInputColorMetadata` 是本项目该信息的单一真值源）。
                    if (bmff != null)
                    {
                        var pm = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath, log, ct).colorPrimaries;
                        basePrim = string.IsNullOrWhiteSpace(pm) || pm is "unspecified" or "unknown" ? "bt709" : pm!;
                    }
                    else
                    {
                        basePrim = DetectBasePrimaries(inputPath, log, ct) ?? "bt709";
                    }
                }
                catch { basePrim = "bt709"; }
                var jxlColorSpace = basePrim switch
                {
                    "bt2020" => "RGB_D65_202_Rel_Lin",
                    "smpte432" => "RGB_D65_P3_Rel_Lin",
                    _ => "RGB_D65_SRG_Rel_Lin",
                };
                log?.Invoke($"[GainMap解码] ✅ HDR 线性像素: {baseW}x{baseH} (1.0=SDR白点, 峰值 {peakNits:F0}nits, " +
                    $"底图原色={basePrim}, {jxlColorSpace})\n");
                return (baseW, baseH, peakNits, jxlColorSpace);
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
    //  纯托管容器解析 (ISO 21496-1 APP2 / MPF) — 无 exiftool 兜底
    // ═══════════════════════════════════════════

    /// <summary>纯托管容器直读的文件上限（超出则走 exiftool，避免大文件占内存）。</summary>
    internal const long MaxManagedParseBytes = 256L * 1024 * 1024;

    /// <summary>
    /// 读**底图（主图段）**的 ICC 并判定其原色 token（bt709 / smpte432 / bt2020）。
    /// JPEG 无 CICP 机制，底图色域只能由 ICC 表达（对齐 libultrahdr writeIccProfile(SRGB, cg)）。
    /// 无 ICC / exiftool 不可用 / 色度不匹配任何候选 → 返回 null（调用方按 sRGB 惯例处理）。
    /// </summary>
    /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
    /// <param name="ct">取消令牌（`#26`：`DecodeToLinearRawAsync` 持有它，经本跳才能到 `ExtractIccToTempFile`）</param>
    public static string? DetectBasePrimaries(string inputPath,
        Action<string>? log = null, CancellationToken ct = default)
    {
        string? tmpJpg = null;
        try
        {
            var data = File.ReadAllBytes(inputPath);
            var container = JpegContainer.Parse(data);
            var primary = container?.GetPrimaryImage();
            if (container == null || primary == null) return null;
            var primaryBytes = container.Slice(primary.Value);
            if (primaryBytes.Length < 4) return null;
            tmpJpg = Path.Combine(PlatformServices.GetTempDir(), $"gmbase_{Guid.NewGuid():N}.jpg");
            File.WriteAllBytes(tmpJpg, primaryBytes);
            var (iccPath, _) = IccProfileService.ExtractIccToTempFile(tmpJpg, log, ct);
            if (iccPath == null) return null;
            try { return IccProfileService.DetectIccPrimaries(iccPath); }
            finally { IccProfileService.TryDeleteIcc(iccPath); }
        }
        catch { return null; }
        finally { if (tmpJpg != null) { try { File.Delete(tmpJpg); } catch { } } }
    }

    /// <summary>纯托管解析容器（ISO 元数据 + 副图边界）；失败/过大返回 null。</summary>
    public static async Task<JpegContainer?> ParseContainerAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > MaxManagedParseBytes) return null;
            return JpegContainer.Parse(await File.ReadAllBytesAsync(path, ct));
        }
        catch { return null; }
    }

    /// <summary>纯托管判定：JPEG 是否为增益图 (Ultra HDR) 容器（无需 exiftool）。</summary>
    public static async Task<bool> IsUltraHdrManagedAsync(string path, CancellationToken ct = default)
        => (await ParseContainerAsync(path, ct))?.IsGainMapContainer() == true;

    /// <summary>
    /// 纯托管判定：AVIF/HEIC（ISO-BMFF）是否为**可解的**增益图容器。
    /// <para>
    /// 判据 = 存在 `tmap` 派生项 **且** 方向为「底图 SDR → 增益给 HDR」（正向）。
    /// </para>
    /// <para>
    /// ⚠ **反向**（底图已是 HDR、增益图用于下映射到 SDR）**返回 false 并点名**：那类文件的底图本身就是
    /// HDR，正确做法是按**普通 HDR 输入**处理（增益图只用于生成 SDR 预览，本工具不需要）；
    /// 若照正向套用增益会产出错图，静默返回 false 又会让用户以为增益图被吞 ⇒ 故必须点名。
    /// </para>
    /// </summary>
    public static async Task<bool> IsIsoBmffGainMapAsync(string path,
        Action<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            var bmff = await IsoBmffGainMapProbe.ProbeFileAsync(path, ct);
            if (bmff == null || !bmff.HasGainMap) return false;
            if (bmff.Form != "tmap" || bmff.TmapPayload == null)
            {
                log?.Invoke($"[GainMap解码] ⚠️ 检测到增益图，但承载形式为 {bmff.Form ?? "未知"}（尚未支持解码），按底图处理\n");
                return false;
            }
            var meta = JpegContainer.ParseIso21496(bmff.TmapPayload, 1, bmff.TmapPayload.Length - 1);
            if (meta == null)
            {
                log?.Invoke("[GainMap解码] ⚠️ ISO-BMFF 增益图元数据解析失败，按底图处理\n");
                return false;
            }
            if (meta.HdrCapacityMin > meta.HdrCapacityMax)
            {
                log?.Invoke("[GainMap解码] ⚠️ 增益图方向为「HDR 底图 → SDR」(backward)：底图本身已是 HDR，按普通 HDR 输入处理（不使用增益图）\n");
                return false;
            }
            log?.Invoke($"[GainMap解码] 检测到 ISO-BMFF 增益图（tmap item，{bmff.TmapPayload.Length} B），方向为「SDR 底图 → HDR」\n");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>ISO 21496-1 增益图元数据的 APP2 命名空间前缀（含结尾 NUL）。</summary>
    private const string IsoNamespace = "urn:iso:std:iso:ts:21496:-1";

    /// <summary>用 XMP（exiftool hdrgm）补全/校验 ISO 21496-1 元数据。</summary>
    /// <remarks>数值上 ISO 与 XMP 按规范必一致；不一致时保留 XMP（已有实战验证路径）并记日志。</remarks>
    private static void MergeFromXmp(GainMapMetadata iso, GainMapMetadata? xmp, Action<string>? log = null)
    {
        if (xmp == null) return;
        // XMP 独有语义：BaseRenditionIsHDR（ISO 二进制无该标志，仅能由 backwardDirection/增益符号推断）
        if (xmp.BaseRenditionIsHdr) iso.BaseRenditionIsHdr = true;
        if (!xmp.UseBaseColorSpace) iso.UseBaseColorSpace = false;
        if (iso.HdrCapacityMax == 0) iso.HdrCapacityMax = xmp.HdrCapacityMax;
        if (MathF.Abs(xmp.GainMapMax - iso.GainMapMax) > 0.02f
            || MathF.Abs(xmp.GainMapMin - iso.GainMapMin) > 0.02f)
        {
            log?.Invoke($"[GainMap解码] ⚠️ ISO 与 XMP 增益范围不一致 (ISO [{iso.GainMapMin:F3},{iso.GainMapMax:F3}] " +
                $"vs XMP [{xmp.GainMapMin:F3},{xmp.GainMapMax:F3}])，采用 XMP\n");
            iso.GainMapMin = xmp.GainMapMin;
            iso.GainMapMax = xmp.GainMapMax;
            if (xmp.Gamma > 0) iso.Gamma = xmp.Gamma;
            iso.OffsetSdr = xmp.OffsetSdr;
            iso.OffsetHdr = xmp.OffsetHdr;
            if (xmp.Channels > 0) iso.Channels = xmp.Channels;
        }
    }

    /// <summary>纯托管切割副图（增益图）写入文件；失败返回 false（调用方回退 exiftool）。</summary>
    private static bool TryWriteSecondaryManaged(JpegContainer? container, string outJpegPath, Action<string>? log)
    {
        try
        {
            var sec = container?.GetSecondaryImage();
            if (sec == null) return false;
            var bytes = container!.Slice(sec.Value);
            if (bytes.Length <= 100) return false;
            File.WriteAllBytes(outJpegPath, bytes);
            log?.Invoke($"[GainMap解码] 增益图来源: 纯托管容器切割 ({bytes.Length} B)\n");
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Ultra HDR JPEG 容器（纯托管、零依赖）：顶层拼接的多张 JPEG + ISO 21496-1 元数据 + MPF 条目。
    /// 结构对齐 GainMapEncoder.WriteJpegGainMapFile 与 Google libultrahdr 输出：
    ///   [主图: SOI … APP1(XMP) APP2(MPF) … SOS 扫描 EOI][副图: SOI APP2(ISO 21496-1) … EOI]
    /// </summary>
    public sealed class JpegContainer
    {
        /// <summary>顶层 JPEG 图像边界（含 SOI..EOI）与 SOF 尺寸。</summary>
        public readonly struct Img
        {
            public readonly int Start;
            public readonly int Length;
            public readonly int W;
            public readonly int H;
            public Img(int start, int length, int w, int h)
            { Start = start; Length = length; W = w; H = h; }
        }

        public readonly List<Img> Images = new();
        public GainMapMetadata? IsoMetadata;
        public int IsoImageIndex = -1;
        /// <summary>MPF IndividualImageArray 条目（attribute / size / offset）。</summary>
        public readonly List<(uint Attr, uint Size, uint Offset)> MpfEntries = new();
        /// <summary>MPF TIFF 头（'MM'/'II'）绝对位置 = MPF offset 计数基准（CIPA DC-X007）。</summary>
        public int MpfBaseOffset = -1;
        public bool MpfLittleEndian;

        private byte[] _data = Array.Empty<byte>();

        /// <summary>取某张图像的原始字节。</summary>
        public byte[] Slice(Img img)
        {
            var b = new byte[img.Length];
            Array.Copy(_data, img.Start, b, 0, img.Length);
            return b;
        }

        /// <summary>主图（底图 base rendition）= 顶层第一张 JPEG（携带 XMP/MPF 与底图 ICC）。</summary>
        public Img? GetPrimaryImage() => Images.Count > 0 ? Images[0] : null;

        /// <summary>副图（增益图）定位：ISO 所在图像 → MPF 非零偏移条目 → 顶层第二张。</summary>
        public Img? GetSecondaryImage()
        {
            if (Images.Count == 0) return null;
            if (IsoImageIndex >= 0 && IsoImageIndex < Images.Count) return Images[IsoImageIndex];
            if (MpfEntries.Count >= 2 && MpfBaseOffset >= 0)
            {
                for (int i = 1; i < MpfEntries.Count; i++)
                {
                    int abs = MpfBaseOffset + (int)MpfEntries[i].Offset;
                    if (abs > 0 && abs + 1 < _data.Length && _data[abs] == 0xFF && _data[abs + 1] == 0xD8)
                    {
                        foreach (var img in Images)
                            if (img.Start == abs) return img;
                    }
                }
            }
            return Images.Count >= 2 ? Images[1] : null;
        }

        /// <summary>
        /// 是否为增益图 (Ultra HDR) 容器：ISO 21496-1 元数据为权威判据；
        /// 无 ISO 时需同时满足 MPF ≥ 2 幅 + 副图为与主图同比例的严格降采样
        /// （避免将 MPO 3D/全景等多幅图 JPEG 误判）。
        /// </summary>
        public bool IsGainMapContainer()
        {
            if (IsoMetadata != null) return true;
            if (Images.Count < 2 || MpfEntries.Count < 2) return false;
            var pri = Images[0]; var sec = Images[1];
            if (pri.W <= 0 || sec.W <= 0 || sec.H <= 0) return false;
            if (sec.W >= pri.W || sec.H >= pri.H) return false;          // 增益图必为降采样
            double ar1 = (double)pri.W / pri.H, ar2 = (double)sec.W / sec.H;
            return ar1 > 0 && Math.Abs(ar1 - ar2) / ar1 < 0.05;
        }

        /// <summary>解析整个文件；无法定位任何 SOI 时返回 null。</summary>
        public static JpegContainer? Parse(byte[] data)
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
            var c = new JpegContainer { _data = data };
            int i = 0;
            while (i + 4 <= data.Length)
            {
                if (data[i] != 0xFF || data[i + 1] != 0xD8) { i++; continue; }

                int imgStart = i;
                int p = i + 2;
                int isoDataStart = -1, isoDataLen = 0;
                int mpfStart = -1, mpfLen = 0;
                int imgW = 0, imgH = 0;
                bool terminated = false;

                // 仅需 p+1 即可读标记字节（EOI 常落在文件末尾前 2 字节，不能要求 p+3）
                while (p + 1 < data.Length)
                {
                    if (data[p] != 0xFF) { p++; continue; }
                    byte m = data[p + 1];
                    if (m == 0xFF || m == 0x00) { p++; continue; }         // 填充字节 / 扫描内转义 00
                    if (m == 0xD9) { terminated = true; break; }           // EOI → 图像结束
                    if (m == 0x01 || (m >= 0xD0 && m <= 0xD8)) { p += 2; continue; }  // TEM / RSTn-SOI 等无长度标记
                    if (m == 0xDA) { p = SkipScanData(data, p + 2); continue; }       // SOS → 跳过熵编码段
                    if (m < 0xC0) { p += 2; continue; }                    // 其余无长度前缀标记 → 保守前进
                    if (p + 3 >= data.Length) break;                       // 段头不完整（截断）

                    int segLen = (data[p + 2] << 8) | data[p + 3];
                    if (segLen < 2) { p += 2; continue; }
                    int payload = p + 4, payloadLen = segLen - 2;
                    if (payload + payloadLen > data.Length) break;         // 截断

                    // SOF0..SOF15 (排除 C4=DHT / C8=reserved / CC=DAC) → 图像尺寸
                    if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC
                        && payloadLen >= 5 && imgW == 0)
                    {
                        imgH = (data[payload + 1] << 8) | data[payload + 2];
                        imgW = (data[payload + 3] << 8) | data[payload + 4];
                    }
                    if (m == 0xE2 && payloadLen > IsoNamespace.Length + 1)
                    {
                        // ISO 21496-1: "urn:iso:...:21496:-1\0" + 二进制元数据
                        if (data[payload] == (byte)'u' && MatchAscii(data, payload, IsoNamespace)
                            && data[payload + IsoNamespace.Length] == 0)
                        {
                            isoDataStart = payload + IsoNamespace.Length + 1;
                            isoDataLen = payload + payloadLen - isoDataStart;
                        }
                        // MPF: "MPF\0" + TIFF 头
                        else if (data[payload] == (byte)'M' && data[payload + 1] == (byte)'P'
                                 && data[payload + 2] == (byte)'F' && data[payload + 3] == 0)
                        {
                            mpfStart = payload + 4; mpfLen = payload + payloadLen - mpfStart;
                        }
                    }
                    p = payload + payloadLen;
                }

                if (!terminated) break;   // 末张图像未遇 EOI（流截断）
                c.Images.Add(new Img(imgStart, p + 2 - imgStart, imgW, imgH));   // 含 EOI
                int idx = c.Images.Count - 1;

                if (isoDataStart >= 0 && isoDataLen >= 5)
                {
                    var meta = ParseIso21496(data, isoDataStart, isoDataLen);
                    if (meta != null) { c.IsoMetadata = meta; c.IsoImageIndex = idx; }
                }
                if (mpfStart >= 0 && mpfLen >= 16 && c.MpfEntries.Count == 0)
                    c.ParseMpf(data, mpfStart, mpfLen);

                i = p + 2;   // 从 EOI 之后继续（下一张 SOI 或文件尾）
            }
            return c.Images.Count > 0 ? c : null;
        }

        /// <summary>跳过熵编码扫描数据：首个 FF xx（xx≠00 且 xx∉D0..D7）即下一标记。</summary>
        private static int SkipScanData(byte[] d, int from)
        {
            int p = from;
            while (p + 1 < d.Length)
            {
                if (d[p] == 0xFF && d[p + 1] != 0x00 && !(d[p + 1] >= 0xD0 && d[p + 1] <= 0xD7))
                    return p;
                p++;
            }
            return d.Length;
        }

        private static bool MatchAscii(byte[] d, int offset, string s)
        {
            if (offset + s.Length > d.Length) return false;
            for (int k = 0; k < s.Length; k++)
                if (d[offset + k] != (byte)s[k]) return false;
            return true;
        }

        // ── MPF (APP2 'MPF\0' → TIFF IFD → tag 0xB002 IndividualImageArray) ──
        private void ParseMpf(byte[] d, int tiffStart, int tiffLen)
        {
            int End = tiffStart + tiffLen;
            if (tiffStart + 8 > d.Length) return;
            bool le = d[tiffStart] == (byte)'I' && d[tiffStart + 1] == (byte)'I';
            if (!le && !(d[tiffStart] == (byte)'M' && d[tiffStart + 1] == (byte)'M')) return;
            MpfLittleEndian = le;
            int ifd = tiffStart + (int)Be32(d, tiffStart + 4, le);
            if (ifd + 2 > End) return;
            int count = Be16(d, ifd, le);
            int entry = ifd + 2;
            int mpEntryVal = -1; uint mpEntryCount = 0;
            for (int n = 0; n < count && entry + 12 <= End; n++, entry += 12)
            {
                int tag = Be16(d, entry, le);
                int type = Be16(d, entry + 2, le);
                uint cnt = Be32(d, entry + 4, le);
                if (tag == 0xB002)   // MPEntry: UNDEF, 16 字节/条目
                {
                    int size = type == 4 ? 4 : type is 3 or 2 ? 2 : 1;   // LONG/SHORT/BYTE-UNDEF
                    mpEntryCount = cnt * (uint)size;
                    mpEntryVal = mpEntryCount <= 4 ? entry + 8 : tiffStart + (int)Be32(d, entry + 8, le);
                }
            }
            if (mpEntryVal < 0 || mpEntryCount < 32 || mpEntryVal + mpEntryCount > End) return;
            // 图像条目: attribute(4) size(4) offset(4) reserved(4)，均相对 TIFF 头
            for (int o = mpEntryVal; o + 16 <= mpEntryVal + mpEntryCount; o += 16)
            {
                uint attr = Be32(d, o, le);
                uint size = Be32(d, o + 4, le);
                uint offs = Be32(d, o + 8, le);
                if (attr == 0 && size == 0 && offs == 0) break;   // 越界/零页保护
                MpfEntries.Add((attr, size, offs));
                if (offs == 0) MpfBaseOffset = tiffStart;         // 主图条目 → 确立基准
            }
            if (MpfBaseOffset < 0 && MpfEntries.Count > 0) MpfBaseOffset = tiffStart;
        }

        private static int Be16(byte[] d, int o, bool le)
            => le ? d[o] | (d[o + 1] << 8) : (d[o] << 8) | d[o + 1];
        private static uint Be32(byte[] d, int o, bool le)
            => le
                ? (uint)d[o] | ((uint)d[o + 1] << 8) | ((uint)d[o + 2] << 16) | ((uint)d[o + 3] << 24)
                : ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

        /// <summary>
        /// 解析 ISO 21496-1 二进制增益图元数据（对齐 libultrahdr decodeGainmapMetadata）。
        ///   [min_version u16][writer_version u16][flags u8]
        ///   flags: bit7 多通道 / bit6 useBaseColorSpace / bit2 backwardDirection(HDR 意图作基图) / bit3 公共分母
        ///   公共分母模式: [denom][baseHeadroomN][altHeadroomN] + 每通道 (min,max,gamma,baseOffset,altOffset 分子)
        ///   独立分母模式: [baseN baseD altN altD] + 每通道 5 对 (N,D)
        /// </summary>
        public static GainMapMetadata? ParseIso21496(byte[] d, int off, int len)
        {
            try
            {
                int end = off + len, p = off;
                if (p + 5 > end) return null;
                int minVersion = Be16(d, p, false); p += 2;
                if (minVersion != 0) return null;              // 仅 v0 定义
                p += 2;                                        // writer_version
                byte flags = d[p++];
                int channels = (flags & 0x80) != 0 ? 3 : 1;
                bool commonDenom = (flags & 0x08) != 0;

                var meta = new GainMapMetadata
                {
                    Channels = channels,
                    // ISO 无 BaseRenditionIsHDR 标志：HDR 意图作基图 → 向后方向，或增益全 ≤ 0
                    BaseRenditionIsHdr = (flags & 0x04) != 0,
                    UseBaseColorSpace = (flags & 0x40) != 0,   // bit6 = useBaseColorSpace
                };

                float rd(int n, int dd) => dd == 0 ? 0f : (float)((double)n / dd);

                if (commonDenom)
                {
                    if (p + 12 + channels * 20 > end) return null;
                    int dn = (int)Be32(d, p, false); p += 4;                       // 公共分母
                    int bhn = (int)Be32(d, p, false); p += 4;                       // baseHdrHeadroomN
                    int ahn = (int)Be32(d, p, false); p += 4;                       // alternateHdrHeadroomN
                    meta.HdrCapacityMin = rd(bhn, dn);
                    meta.HdrCapacityMax = rd(ahn, dn);
                    for (int c = 0; c < channels; c++)
                    {
                        int mn = (int)Be32(d, p, false); p += 4;                    // gainMapMinN
                        int xn = (int)Be32(d, p, false); p += 4;                    // gainMapMaxN
                        int gn = (int)Be32(d, p, false); p += 4;                    // gainMapGammaN
                        int sn = (int)Be32(d, p, false); p += 4;                    // baseOffsetN
                        int tn = (int)Be32(d, p, false); p += 4;                    // alternateOffsetN
                        if (c == 0)
                        {
                            meta.GainMapMin = rd(mn, dn);
                            meta.GainMapMax = rd(xn, dn);
                            meta.Gamma = rd(gn, dn) <= 0f ? 1f : rd(gn, dn);
                            meta.OffsetSdr = rd(sn, dn);
                            meta.OffsetHdr = rd(tn, dn);
                        }
                    }
                }
                else
                {
                    if (p + 16 + channels * 40 > end) return null;
                    int bhn = (int)Be32(d, p, false), bhd = (int)Be32(d, p + 4, false); p += 8;
                    int ahn = (int)Be32(d, p, false), ahd = (int)Be32(d, p + 4, false); p += 8;
                    meta.HdrCapacityMax = rd(ahn, ahd);
                    meta.HdrCapacityMin = rd(bhn, bhd);
                    for (int c = 0; c < channels; c++)
                    {
                        int mn = (int)Be32(d, p, false); int md = (int)Be32(d, p + 4, false); p += 8;
                        int xn = (int)Be32(d, p, false); int xd = (int)Be32(d, p + 4, false); p += 8;
                        int gn = (int)Be32(d, p, false); int gd = (int)Be32(d, p + 4, false); p += 8;
                        int sn = (int)Be32(d, p, false); int sd = (int)Be32(d, p + 4, false); p += 8;
                        int tn = (int)Be32(d, p, false); int td = (int)Be32(d, p + 4, false); p += 8;
                        if (c == 0)
                        {
                            meta.GainMapMin = rd(mn, md);
                            meta.GainMapMax = rd(xn, xd);
                            meta.Gamma = rd(gn, gd) == 0 ? 1f : rd(gn, gd);
                            meta.OffsetSdr = rd(sn, sd);
                            meta.OffsetHdr = rd(tn, td);
                        }
                    }
                }

                // HDR 基图推断：负 log2 增益上限 → 基图已是 HDR（由基图向 SDR 下映射）
                if (meta.GainMapMax <= 0f && meta.GainMapMin < 0f) meta.BaseRenditionIsHdr = true;
                if (meta.Gamma <= 0f) meta.Gamma = 1f;
                return meta;
            }
            catch { return null; }
        }

        /// <summary>
        /// 容量存储语义: ISO 21496-1 的 baseHdrHeadroom/alternateHdrHeadroom 分数值本身即
        /// log2(headroom)（libultrahdr 转 float 时取 2^(N/D) 得线性亮度比）；hdrgm XMP 的
        /// HDRCapacityMin/Max 亦以 log2 存储 → GainMapMetadata.HdrCapacity* 统一存对数值（=分数值）。
        /// </summary>
    }

    // ═══════════════════════════════════════════
    //  元数据读取
    // ═══════════════════════════════════════════

    /// <summary>读取 hdrgm XMP 元数据 (逐标签读取, 避免 -hdrgm:all 因 trailer 警告提前退出)</summary>
    /// <param name="log">日志回调（取消分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
    /// <param name="ct">取消令牌（`#26`：调用方 `DecodeToLinearRawAsync` 一直持有它却没往下传，本次接通）</param>
    public static async Task<GainMapMetadata?> ReadMetadataAsync(string path, Action<string>? log = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!ExifToolService.IsAvailable) return null;

            // ── 单次 -json 查询读取全部标签（此前 8 个标签逐个起 exiftool 进程，开销大）──
            // exiftool -json 输出形如 [{"SourceFile":"...","XMP-gainmap:GainMapMin":0.5,...}]
            var json = await ReadMetadataJsonAsync(path, log, ct);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var meta = new GainMapMetadata();
            bool found = false;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array || root.GetArrayLength() == 0)
                return null;
            var first = root[0];

            // 从 JSON 属性中查找标签（属性名形如 "XMP-gainmap:GainMapMin"）。
            //
            // ⚠️ 必须**限定命名空间**：exiftool 参数带 `-G` ⇒ 输出的是**全部**元数据组（EXIF/XMP/ICC…），
            //    而 EXIF 自身就有名为 `Gamma` 的标签（0xA500，相机 JPEG 极常见，典型值 2.2）。
            //    旧实现用 `EndsWith(":Gamma")` 做无组限制的后缀匹配，且取**第一个**命中 —— EXIF 组通常
            //    先于 XMP 组输出 ⇒ 对**无 ISO 21496-1 APP2、只有 XMP 的旧 Ultra HDR 文件**（Google 相机
            //    2023 年产物）会把相机的 EXIF:Gamma 当成**增益图 gamma** 应用 `gv^(1/2.2)`，整图增益错乱。
            //    `Channels` 同此风险。
            // ⇒ 规则：只有 gain map 的两个命名空间（XMP-gainmap / XMP-hdrgm，以及裸 XMP）算权威命中并
            //   立即返回；不带组名的同名属性仅作兜底（exiftool 不带 -G 时的形态），绝不采信 EXIF/其它组。
            string? GetTag(string tagName)
            {
                string? fallback = null;
                foreach (var prop in first.EnumerateObject())
                {
                    var nm = prop.Name;
                    int colon = nm.IndexOf(':');
                    bool hit;
                    if (colon > 0)
                    {
                        string ns = nm.Substring(0, colon);
                        hit = (ns.Equals("XMP-gainmap", StringComparison.OrdinalIgnoreCase)
                               || ns.Equals("XMP-hdrgm", StringComparison.OrdinalIgnoreCase)
                               || ns.Equals("XMP", StringComparison.OrdinalIgnoreCase))
                              && nm.Substring(colon + 1).Equals(tagName, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        hit = nm.Equals(tagName, StringComparison.OrdinalIgnoreCase);
                    }
                    if (!hit) continue;

                    string? v = ReadTagValue(prop.Value);
                    if (colon > 0) return v;      // 权威命名空间命中 ⇒ 立即返回
                    fallback ??= v;               // 无组名形 ⇒ 仅兜底
                }
                return fallback;
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
            var capMin = GetTag("HDRCapacityMin");
            if (capMin != null) { meta.HdrCapacityMin = ParseFirstFloat(capMin); found = true; }

            var ch = GetTag("Channels");
            if (ch != null && int.TryParse(ch, out var c) && c > 0)
            {
                meta.Channels = c;
                found = true;
            }

            var br = GetTag("BaseRenditionIsHDR");
            if (br != null)
                meta.BaseRenditionIsHdr = br.StartsWith("True", StringComparison.OrdinalIgnoreCase);

            var ubc = GetTag("UseBaseRenderingColorSpace");
            if (ubc != null)
                meta.UseBaseColorSpace = !ubc.StartsWith("False", StringComparison.OrdinalIgnoreCase);

            // MapPrimary / MapSecondary: "WxH" 形式（AndroidX 写入的声明尺寸）
            var mp = GetTag("MapPrimary");
            if (mp != null && TryParseSize(mp, out var pw, out var ph)) { meta.MapPrimaryWidth = pw; meta.MapPrimaryHeight = ph; }
            var ms = GetTag("MapSecondary");
            if (ms != null && TryParseSize(ms, out var sw, out var sh)) { meta.MapSecondaryWidth = sw; meta.MapSecondaryHeight = sh; }
            var mir = GetTag("MapImageRatio");
            if (mir != null) { var v = ParseFirstFloat(mir); if (v > 0) meta.MapImageRatio = v; }

            return found ? meta : null;
        }
        catch { return null; }
    }

    /// <summary>把 exiftool JSON 的标签值统一取成字符串（Number 取原始文本，避免双重格式化丢精度）。</summary>
    private static string? ReadTagValue(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => e.GetString(),
        System.Text.Json.JsonValueKind.Number => e.GetRawText(),
        System.Text.Json.JsonValueKind.True => "True",
        System.Text.Json.JsonValueKind.False => "False",
        _ => e.GetRawText(),
    };

    /// <summary>解析 "1920x1080" / "1920 1080" 形式尺寸对</summary>
    private static bool TryParseSize(string? value, out int w, out int h)
    {
        w = h = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(new[] { 'x', 'X', '*', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h) && w > 0 && h > 0;
    }

    /// <summary>一次 exiftool -json 查询全部标签（带 5 秒超时保护 + 调用方取消令牌）</summary>
    private static async Task<string?> ReadMetadataJsonAsync(string path, Action<string>? log = null,
        CancellationToken ct = default)
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
                p.BeginErrorReadLine();   // P2-3：stderr 不排空会在管道缓冲(~4KB)写满时把子进程堵死，我们等 stdout/WaitForExit 就永等（实测可复现）
            // `#26`：链接 CTS —— 调用方取消与原有 5s 超时**先到者生效**，原有超时行为不变。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token);
                return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                // 取消分支必须在**同一处**点名（不要退化成裸 `catch { }`）：有 sink 走 sink，没有退回 Trace。
                var why = ct.IsCancellationRequested ? "被取消" : "超时（5s）";
                var msg = $"[GainMap解码] exiftool 元数据读取{why} ⇒ 已终止：{Path.GetFileName(path)}（元数据按不可用处理）";
                if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
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
    /// <param name="log">日志回调（取消分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
    /// <param name="ct">取消令牌（`#26`：调用方 `DecodeToLinearRawAsync` 一直持有它却没往下传，本次接通）</param>
    public static async Task<bool> ExtractGainMapJpegAsync(string inputPath, string outputJpegPath,
        Action<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            if (!ExifToolService.IsAvailable) return false;
            var psi = new ProcessStartInfo
            {
                FileName = ExifToolService.DetectedPath!,
                Arguments = $"-b -MPImage2 \"{inputPath}\"",
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
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
            try
            {
                // `#26`：本方法原本**没有任何超时**（挂死只能靠调用方放弃），本次只接上调用方的取消令牌，
                // **不新增超时**（不扩大本次范围）：取消 ⇒ 杀树 + 点名 ⇒ 按既有兜底返回 false。
                await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
                var stderrTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(ct);
                await stderrTask;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                var msg = $"[GainMap解码] 增益图提取被取消 ⇒ 已终止：{Path.GetFileName(inputPath)}（增益图提取失败）";
                if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
                return false;
            }
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
        long px = (long)baseW * baseH;                 // 防溢出：见调用方的 MaxDecodedPixels 说明
        if (px <= 0 || px > int.MaxValue / 4) return Array.Empty<float>();
        int pixelCount = (int)px;
        var result = new float[pixelCount * 4];
        bool isGray = meta.Channels <= 1;

        // 逻辑尺寸优先用元数据声明值（AndroidX 写法）：JPEG 解码尺寸可能因宏块对齐大于逻辑尺寸，
        // 按解码尺寸归一化会令增益图坐标整体偏移（边缘/右侧增益错位）。
        int priW = meta.MapPrimaryWidth   > 0 ? meta.MapPrimaryWidth   : baseW;
        int priH = meta.MapPrimaryHeight  > 0 ? meta.MapPrimaryHeight  : baseH;
        int gW   = meta.MapSecondaryWidth > 0 ? meta.MapSecondaryWidth : gmW;
        int gH   = meta.MapSecondaryHeight> 0 ? meta.MapSecondaryHeight: gmH;

        // 预计算 gamma 校正后的 log 范围
        float gammaInv = meta.Gamma != 1.0f && meta.Gamma > 0 ? 1.0f / meta.Gamma : 1.0f;

        for (int y = 0; y < baseH; y++)
        {
            for (int x = 0; x < baseW; x++)
            {
                int o = (y * baseW + x) * 4;

                // 双线性采样增益图（坐标按逻辑尺寸归一，超出对齐区域的像素钳到增益图边缘）
                float gx = (Math.Min(x, priW - 1) + 0.5f) * gW / priW - 0.5f;
                float gy = (Math.Min(y, priH - 1) + 0.5f) * gH / priH - 0.5f;
                gx = Math.Clamp(gx, 0, gW - 1);
                gy = Math.Clamp(gy, 0, gH - 1);
                // ⚠️ 索引必须按**解码缓冲区尺寸** (gmW/gmH) 钳位，不能按元数据声明的逻辑尺寸 (gW/gH)：
                //    畸形或被篡改的文件可声明 MapSecondaryWidth/Height **大于**实际解码宽度，
                //    此时 gx 被钳到 gW-1 > gmW-1 ⇒ (y0*gmW + x0)*4 越界读（旧代码只钳了 x1/y1，
                //    x0/y0 漏钳，异常只能被外层 catch 吞成"解码失败"）。
                //    ⇒ 逻辑尺寸只用于归一化映射，安全钳位一律走真实缓冲区尺寸。
                int x0 = Math.Clamp((int)MathF.Floor(gx), 0, gmW - 1);
                int y0 = Math.Clamp((int)MathF.Floor(gy), 0, gmH - 1);
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

    /// <param name="log">日志回调（探测失败/取消的点名；null ⇒ 退回 Trace.WriteLine）</param>
    /// <param name="ct">取消令牌（`#26`：调用方 `DecodeToLinearRawAsync` 一直持有它却没往下传，本次接通）</param>
    /// <param name="inputFmtArgs">输入侧的 `-f <fmt> ` 前缀（裸流才需要；空串 = 由 ffprobe 自行探测容器）。
    /// ⚠ 必须放在**输入文件名之前**（`-f` 是输入选项），故由 <see cref="ImageGeometry.BuildProbeArguments"/> 拼在探测项之前。</param>
    /// <remarks>
    /// 2026-09-30：本方法曾是仓内**第二份**自写 `-show_entries stream=width,height`（= coded 尺寸），
    /// 与 <c>RawColorPipeline.ProbeSizeAsync</c>、<c>QualityAnalysisService.GetResolutionAsync</c> 各一份。
    /// 解码段（<c>baseArgs</c>）不写 <c>-noautorotate</c> ⇒ 带取向标签的 Ultra HDR 底图会旋正，
    /// 而 <c>base.rgba</c> 是**裸 rawvideo**、下游只做长度自检 ⇒ 同一条"coded 标注旋正像素"的缺陷链。
    /// 现统一转发到 <see cref="ImageGeometry"/>，返回**显示**尺寸。
    /// </remarks>
    private static Task<(int w, int h)> ProbeSizeAsync(string path, string ffmpeg,
        Action<string>? log = null, CancellationToken ct = default, string inputFmtArgs = "")
    {
        var ffprobe = PlatformServices.ResolveFfprobePath(ffmpeg)
            ?? Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
        if (!File.Exists(ffprobe)) ffprobe = ffmpeg.Replace("ffmpeg.exe", "ffprobe.exe");
        return ImageGeometry.DisplaySizeAsync(path, ffprobe, log, ct, inputFmtArgs);
    }

    private static async Task<int> RunFfmpegAsync(string args, string ffmpeg,
        Action<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
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
