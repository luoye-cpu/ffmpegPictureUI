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

    // ═══════════════════════════════════════════════════════════════════════
    //  F-5（2026-10-11）：offset 的**唯一声明处**
    //  ─────────────────────────────────────────────────────────────────────
    //  参照不变量（备忘 Q1 三条一手条形）：**元数据声明的 offset 与前向实际施加的 offset 是同一个数**。
    //    · libavif：默认分数 {1,64}（src/gainmap.c:21-24 avifGainMapSetEncodingDefaults），
    //      前向**就**用它做除法（gainmap.c:712-713），色域转换出负值时放大 offset 后
    //      **再写回声明**（gainmap.c:653-665 调整、:745-752 写回）⇒ 声明的仍是前向用的那份数。
    //    · libultrahdr 两趟：kSdrOffset/kHdrOffset = 1e-7（gainmapmath.h:549-550）既是前向
    //      computeGain 的加数（gainmapmath.cpp:773-775），又被写进元数据（jpegr.cpp:1038-1040）。
    //    · libultrahdr 一趟：声明 0（jpegr.cpp:727-728）且 encodeGain 不用（:758-771）⇒ 同样自洽。
    //  本仓解侧（GainMapDecoder.ApplyGainMap）与 XMP/ISO 一直按 1/64 声明并施加 ⇒
    //  与参照同形、且改动面最小的那支是「**前向按声明施加 1/64**」（备忘裁①），
    //  不是把声明改成 0（那等于解侧一起改，且丢掉 LA 的互操作默认值）。
    //  误差量级（备忘 Q6）：修复前 (boost−1)·offsetSdr，上界 (headroom−1)/64
    //  ⇒ 1000nits 12.45 nits / 4000nits 59.33 nits（旧的 15.6/82.4 头条已被验证 lane 判死）。
    // ═══════════════════════════════════════════════════════════════════════
    internal const int KOffsetSdrN = 1, KOffsetSdrD = 64;   // ISO 21496-1 baseOffset（= 分母侧 / SDR 侧）
    internal const int KOffsetHdrN = 1, KOffsetHdrD = 64;   // ISO 21496-1 alternateOffset（= 分子侧 / HDR 侧）
    /// <summary>前向分母侧施加的 offset —— 与写进 ISO/XMP 声明的 baseOffset 同一个式子。</summary>
    internal static readonly float KOffsetSdr = (float)KOffsetSdrN / KOffsetSdrD;
    /// <summary>前向分子侧施加的 offset —— 与写进 ISO/XMP 声明的 alternateOffset 同一个式子。</summary>
    internal static readonly float KOffsetHdr = (float)KOffsetHdrN / KOffsetHdrD;
    /// <summary>1/64 的浮点写法；仅**兼容**既有引用，真值以上面两个 readonly 为准（= 1f/64f）。</summary>
    public const float KOffset = 0.015625f;

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
    /// <param name="baseGamut">
    /// 底图（base rendition）色域："srgb"（默认）或 "bt2020"。两者**均为 SDR**（sRGB 传递 + 203nits 白点），
    /// HDR 信息全部由增益图承载 —— 对齐 libultrahdr 的 writeIccProfile(UHDR_CT_SRGB, cg) 与
    /// ultrahdr_color_gamut = {BT709, P3, BT2100}。底图色域不等于源色域时，**像素必须做线性域
    /// primaries 矩阵映射**（参考实现亦先 convertYuv 再编码），绝不只改标签。
    /// </param>
    /// <param name="sourcePrimaries">传入线性像素的 primaries（CICP token，如 bt709/smpte432/bt2020）。</param>
    /// <param name="log">日志回调</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="jpegProgressiveLevel">
    /// 底图与增益图 JPEG 的 cjpegli 渐进级别（`-p N`，取值 0..2）。`-1`（默认）= **不传** `-p`，
    /// 沿用 cjpegli 自身默认（= 2，即渐进）。⚠ 只对 **cjpegli 后端**有效：回退 ffmpeg mjpeg 时
    /// 无法表达渐进（ffmpeg 的 mjpeg 编码器**没有**该能力，见 `ImageEncoderArgs` 的说明），
    /// 此时会记一条日志点名，绝不静默当没选。
    /// <para>
    /// ⚠ **必须是最后一个形参**：`QueueProcessor` 的专用 GainMap 管线用**位置实参**传
    /// <paramref name="log"/>/<paramref name="ct"/>（`QueueProcessor.cs:1895`），插在它们之前会破坏该调用点。
    /// </para>
    /// </param>
    /// <summary>
    /// 由输出路径判定增益图**容器**：`.avif` ⇒ <c>"avif"</c>（ISO-BMFF `tmap` 派生项），其余 ⇒ <c>"jpeg"</c>
    /// （Ultra HDR APP2 + XMP + MPF）。
    /// <para>
    /// ⚠ **单一真值**：两个调用点（`RawColorPipeline` 的引擎出口、`QueueProcessor` 的专用管线）**都**必须
    /// 用它，不允许各自按 <c>Options.Format</c> 另写一份映射 —— 两套映射必然漂移（本仓反复修过这类缺陷）。
    /// </para>
    /// </summary>
    public static string ContainerOf(string outputPath)
        => string.Equals(Path.GetExtension(outputPath), ".avif", StringComparison.OrdinalIgnoreCase) ? "avif" : "jpeg";

    // ── 增益图元数据承载的**取值域唯一真值**（2026-10-27 与 origin/main 合流时收口）──
    // UI 下拉的词表、CLI 两个开关的校验、模型两条路（单输入 / 两图合成）的默认值、以及本编码器
    // 入口处的裁决，都必须读下面四个成员；任何一处再各写一份 "iso"/"xmp"/"both" 字面量就是第二副本
    //（本仓为 JXL distance 的五份副本付过代价）。合流前 CLI 与模型各有一份，已并到此处。
    public const string GainMapMetadataDefault = "iso";
    public static readonly string[] GainMapMetadataModes = { "iso", "xmp", "both" };

    /// <summary>这一档要不要写 ISO 21496-1（JPEG 的 APP2 / AVIF 的 tmap）？只有 `xmp` 不写。</summary>
    public static bool MetadataWritesIso(string? mode)
        => !string.Equals((mode ?? "").Trim().ToLowerInvariant(), "xmp", StringComparison.Ordinal);

    /// <summary>这一档要不要写 hdrgm XMP？只有 `iso` 不写。</summary>
    public static bool MetadataWritesXmp(string? mode)
        => !string.Equals((mode ?? "").Trim().ToLowerInvariant(), "iso", StringComparison.Ordinal);

    /// <summary>取值域单点裁决：合法返回 null，非法返回点名原因（CLI 与 UI 都问这里，不各自写 switch 表）。
    /// 注意这里**不判**组合合法性：`xmp` 与三通道不互斥——Ultra HDR v1.1 的 hdrgm 表没有通道数字段，
    /// 但逐通道参数恒相等时单值即适用全部通道（参考实现只拒"各通道参数不同"的 XMP 编码，
    /// `jpegr.cpp:1120-1127` 的 `!are_all_channels_identical()`），本编码器逐通道参数恒相同。</summary>
    public static string? ValidateGainMapMetadata(string? mode)
    {
        string m = (mode ?? "").Trim().ToLowerInvariant();
        if (Array.IndexOf(GainMapMetadataModes, m) < 0)
            return $"unknown gain-map metadata carrier '{mode}' (expected {string.Join(" | ", GainMapMetadataModes)})";
        return null;
    }

    /// <returns>成功返回 true</returns>
    public static async Task<bool> EncodeAsync(
        float[] hdrRgbaLinear, int w, int h, string outputPath,
        float hdrPeakNits = 1000f, float sdrWhiteNits = KSdrWhiteNits,
        bool multiChannel = false, float baseQuality = 1.5f, float gainMapQuality = 1.5f,
        int downsample = 4, string baseGamut = "srgb", string? sourcePrimaries = null,
        Action<string>? log = null, CancellationToken ct = default,
        int jpegProgressiveLevel = -1, string container = "jpeg", string? sourceBaseRaw = null,
        bool inputIsSdrWhiteUnit = false,
        // 外部 SDR 底图（两图合成）用：底图**自己的**原色。null ⇒ 沿用旧契约（底图已处于
        // sourcePrimaries，与 HDR 同步转一次）。给了值时，底图与 HDR 各按自己的原色转到 base 色域。
        string? sourceBasePrimaries = null,
        // 两图合成用：增益量程**按这一对实测**，而不是取 log2(peak/203)。
        // 底图若没在自己的高光处达到 SDR 白点（独立调色的 SDR 成片很常见），
        // 需要的增益会超过 peak/203 —— 按名义值写会静默钳顶（实测 59% 通道被截）。
        bool measureGainRange = false,
        // 编码器显式选择（两图合成面板/CLI 暴露；null = 沿用旧优先级：cjpegli 可用则用，否则 mjpeg）。
        // ⚠ 显式请求却不可用 ⇒ **失败**，不静默回落（回落会让用户以为选的是 cjpegli）。
        // (a) JPEG 出口：`cjpegli` | `mjpeg`；(b) AVIF 出口：`libaom-av1` | `libsvtav1`。
        string? jpegEncoder = null,
        string? avifEncoder = null,
        // 元数据承载（JPEG 出口）：`iso` = 只写 APP2 ISO 21496-1；`xmp` = 只写 XMP hdrgm（Ultra HDR v1）；
        // `both` = 两者并存。AVIF 的 tmap **只**承载 ISO 21496-1 ⇒ `xmp` 在这里没有可交付形态，
        // 显式请求即失败（不静默改写成 ISO）。
        // ⚠ 这里的 `"both"` 是**API 兜底**，不是产品默认：产品两条路（`FfmpegOptions.JpegGainMapMetadata`
        //   与 `GainMapComposeMetadata`）的默认都取 `GainMapMetadataDefault`（iso），且三个产品调用点
        //   （QueueProcessor / RawColorPipeline / GainMapComposeService）一律显式传参。兜底保留历史口径
        //   是因为测试宿主里仍有省略该参数直接调本方法的用例，它们的产物形状是既读数的基线——
        //   动它属"改判据的输入"，不在本次合流范围内（账本 §52.2 记的正是这一处双默认）。
        string gainMapMetadata = "both",
        // AVIF 子图的 CRF（0-63，越小越好）；-1 = 沿用历史固定值 18。
        // 底图与增益图各一个：增益图是低对比度数据图，和底图共用同一 CRF 并不合理，但历史口径就是 18，
        // 所以只有调用方显式给了才改。
        int baseCrf = -1, int gainMapCrf = -1,
        // 源色域**没有 CICP 命名**时（ProPhoto / Adobe RGB / ColorMatch…），探测给的是近似 token 并登记
        // primariesApproximated（见 FfmpegCommandBuilder.ColorMetadata）。两者同时为真 ⇒ 底图矩阵改由
        // 该**具名空间**求（registry 同一套色度 + Bradford），不再把近似 token 当源的真实原色。
        // 都不给 ⇒ 走 CICP token 路径，逐字节行为与历史一致。
        string? sourceSpaceName = null, bool primariesApproximated = false,
        // 两图合成的 SDR 底图那一侧的同名一对：它的原色**未必等于** HDR 侧，近似登记也必须各自独立
        // （一张 ProPhoto 的 SDR 成片配 bt2020/PQ 的 HDR 参考时，把底图当 bt709 解释就是错色交付）。
        string? sourceBaseSpaceName = null, bool sourceBasePrimariesApproximated = false)
    {
        try
        {
            string metadata = (gainMapMetadata ?? "").Trim().ToLowerInvariant();
            string? metadataReject = ValidateGainMapMetadata(metadata);
            if (metadataReject != null)
            {
                log?.Invoke($"[GainMap] ERROR: {metadataReject}.\n");
                return false;
            }
            // ── 帧长守卫（2026-10-21 审查第 6 项）──
            // 本方法内四处 `int pixelCount = w * h`（:146/:349/:407/:653）与它们的 `*3`/`*4` 乘积全是 int。
            // 回绕成**负数**时会被 `ArrayPool.Rent` / `new T[]` 响亮拒收（实测 ArgumentOutOfRangeException /
            // OverflowException，见下），但回绕成**正数**时缓冲只是变小、循环只是少跑 ⇒ 交出一张小图而不自知。
            // 所以守卫按最紧的下游需求 `w*h*4 ≤ int.MaxValue` 来设，而不是只挡负数。
            long pixelCountCheck = (long)w * h;
            if (pixelCountCheck > int.MaxValue / 4L)
            {
                log?.Invoke($"[GainMap] ❌ 尺寸 {w}x{h} 的帧长 {pixelCountCheck} 超出 int 索引可承载上限" +
                    $"（本管线需 w*h*4 ≤ {int.MaxValue}）⇒ 拒绝，不回绕成小图处理\n");
                return false;
            }

            var ffmpeg = AppSettingsService.Current.FfmpegPath;
            bool cjpegliAvail = CjpegliService.IsAvailable;
            bool ffmpegAvail = !string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(ffmpeg);
            if (!cjpegliAvail && !ffmpegAvail)
            {
                log?.Invoke("[GainMap] ⚠️ cjpegli 与 ffmpeg(mjpeg) 均不可用，无法编码 Gain Map\n");
                return false;
            }

            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"gainmap_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // ── 亮度参数 ──
                float headroom = hdrPeakNits / sdrWhiteNits;
                float maxLog2Gain = MathF.Log2(Math.Max(headroom, 1.0f));
                // 显示用的容量（HDRCapacityMax / alternateHdrHeadroom）**始终**是 peak/203；
                // 增益量程（GainMapMax / 量化区间）在 measureGainRange=true 时会被实测值替换。
                // 两者在 ISO 21496-1 里本就是不同字段（gainMapMax 可大于或小于 headroom）。
                float hdrCapacityLog2 = maxLog2Gain;
                // 增益区间的**下沿**：默认 0（= 自造底图时的「只上映射」历史口径，逐字节不变）；
                // 底图来自源时才由实测填入负值（同一份 offset、同一份 stored 分母 ⇒ 与像素严格自洽）。
                float minLog2Gain = 0f;

                bool wideBase = string.Equals(baseGamut, "bt2020", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(baseGamut, "bt2100", StringComparison.OrdinalIgnoreCase);
                log?.Invoke($"[GainMap] 参数: HDR峰值 {hdrPeakNits:F0}nits, SDR白点 {sdrWhiteNits:F0}nits, headroom {headroom:F2} ({maxLog2Gain:F2} log2)" +
                    (wideBase ? ", 底图=Rec.2020 (SDR, 广色域)" : ", 底图=sRGB (SDR)") + "\n");

                // ── 归一化 HDR 到 SDR 白点相对空间 ──
                // 输入约定: 线性值 1.0 = hdrPeakNits (zscale npl 语义)
                // normHdr = 物理亮度 / SDR白点 → SDR 内容(≤白点) → ≤1.0, HDR 高光 → >1.0
                int pixelCount = w * h;
                float peakOverWhite = hdrPeakNits / Math.Max(sdrWhiteNits, 1f);
                // 单位换算只在输入是「1.0 = 峰值」时 needed；PFM 路（增益图输入再出口）已是
                // 「1.0 = SDR 白点」⇒ 再乘一次会把整图抬 peak/white 倍，暗部端点因此永远测不出来。
                float normScale = inputIsSdrWhiteUnit ? 1f : peakOverWhite;
                float[] normHdr = BufferPool.RentFloat(pixelCount * 4);
                float[] sdrLinear = BufferPool.RentFloat(pixelCount * 4);
                try
                {
                // 归一化 (2026-08-15: SIMD 化前已是简单乘, JIT 自动向量化; 保持现状)
                for (int i = 0; i < pixelCount * 4; i++)
                    normHdr[i] = hdrRgbaLinear[i] * normScale;

                // ── 1. 底图来源：**源自己的 SDR 底图**（A 路配对重封装）或分段 Reinhard（B/普通路）──
                //   为什么必须留这一支：本编码器若自造底图，`hdr/sdr` 恒 ≥1（Reinhard 只上映射），
                //   于是源声明的 **<1 暗部端点**（例：OnePlus `local.hdr.linear.mask` 的 −42/256 ⇒ 0.891×）
                //   在结构上无法写进 ISO 的 gainMapMin ⇒ 双端点被抹成一个（T12/R3 的动机）。
                //   分母仍走 `sdrStored`（底图真正交付的字节），与 F-3 同口径，不引入新偏差。
                bool baseFromSource = false;
                if (!string.IsNullOrEmpty(sourceBaseRaw) && File.Exists(sourceBaseRaw))
                {
                    baseFromSource = LoadSourceBaseLinear(sourceBaseRaw!, sdrLinear, pixelCount, log);
                    if (baseFromSource)
                        log?.Invoke("[GainMap] 底图=源 SDR 底图（配对重封装）⇒ 允许 <1 的暗部端点进入 gainMapMin\n");
                    else
                        log?.Invoke("[GainMap] ⚠️ 源底图不可用 ⇒ 回落自造 Reinhard 底图（<1 端点将不可表达）\n");
                }
                if (!baseFromSource)
                {
                    SimdPixelOps.ReinhardToSdr(normHdr, sdrLinear, headroom);
                    log?.Invoke("[GainMap] 色调映射完成 (分段 Reinhard, SIMD)\n");
                }

                // ── 1b. 底图色域映射（线性域 primaries 矩阵）──
                // 增益图元数据声明 useBaseColorSpace=True ⇒ 增益必须在**底图色域**内计算，
                // 所以 HDR 侧与 SDR 侧都要先映射到底图原色（参考实现同理：先 convertYuv 到目标 gamut）。
                // 映射后像素仍是线性、仍以 1.0 = SDR 白点为约定，仅原色基改变。
                // ⚠ 两图合成时底图的原色**未必等于** HDR 的源原色（典型：HDR=bt2020、SDR 底图=sRGB）。
                //   旧契约把底图当成 sourcePrimaries 再转一次 —— 两图输入下那会把 sRGB 底图当
                //   bt2020 解释并二次转换（实测 sRGB(76,178,102) 交付成 (0,184,88)）。
                //   ⇒ 底图有自己原色时，两侧各用各的矩阵；未给时逐字节保持旧行为（A 路契约）。
                string baseTok = wideBase ? "bt2020" : "bt709";
                string baseName = wideBase ? "BT.2020" : "sRGB";
                bool sdrHasOwnPrim = !string.IsNullOrWhiteSpace(sourceBasePrimaries);
                string sdrPrimTok = sdrHasOwnPrim ? sourceBasePrimaries! : (sourcePrimaries ?? "");
                // 源→底图矩阵按"具名空间 → CICP token → 同空间恒等"三级求（ResolveGamutMatrix）。
                // 具名空间只在校准登记为近似时才优先（ProPhoto 被近似成 bt709 的那一类），否则维持
                // token 路径 ⇒ 有 CICP 命名的素材（bt709/smpte432/bt2020…）逐字节行为不变。
                var (gamutMtx, gamutFrom, gamutIdentity) = ResolveGamutMatrix(
                    sourcePrimaries, primariesApproximated ? sourceSpaceName : null, baseTok, baseName);
                var (sdrGamutMtx, sdrFrom, sdrIdentity) = sdrHasOwnPrim
                    ? ResolveGamutMatrix(sdrPrimTok,
                        sourceBasePrimariesApproximated ? sourceBaseSpaceName : null, baseTok, baseName)
                    : (gamutMtx, gamutFrom, gamutIdentity);
                if (gamutMtx != null)
                    SimdPixelOps.ApplyPrimariesMatrix(normHdr, gamutMtx);
                if (sdrGamutMtx != null)
                    SimdPixelOps.ApplyPrimariesMatrix(sdrLinear, sdrGamutMtx);
                if (gamutMtx != null && (sdrGamutMtx != null || sdrIdentity))
                    log?.Invoke(sdrHasOwnPrim
                        ? $"[GainMap] 底图色域映射: HDR {gamutFrom} → {baseTok}、SDR {sdrFrom} → {baseTok}（各自独立矩阵）\n"
                        : $"[GainMap] 底图色域映射: {gamutFrom} → {baseTok}（线性域 3x3 矩阵，HDR/SDR 两侧同步）\n");
                // 危险缺席（求不出矩阵 ⇒ 只会改标签不会改像素）**两侧同口径点名**。
                // 旧写法只在 wideBase 分支打、且把"同空间恒等"这种良性情况也报成色偏风险，
                // 而默认 srgb 底图上的真危险完全静默 —— 两个方向都错位（2026-10-28 实测）。
                if (!gamutIdentity && gamutMtx == null)
                    log?.Invoke($"[GainMap] ⚠️ 未能求出 {gamutFrom} → {baseTok} 矩阵，HDR 侧将不映射（仅改标签，存在色偏风险）\n");
                if (!sdrIdentity && sdrGamutMtx == null)
                    log?.Invoke($"[GainMap] ⚠️ 未能求出 {sdrFrom} → {baseTok} 矩阵，底图将不映射（仅改标签，存在色偏风险）\n");
                if (gamutIdentity && sdrIdentity)
                    log?.Invoke($"[GainMap] 底图色域：源已在 {baseTok} 内 ⇒ 恒等，无需原色映射\n");

                // ── 2. 底图（始终 SDR：sdrLinear → sRGB gamma → 8bit 字节 → PNG）──
                //   ⚠ 先**落成字节**：这份字节就是交付给解码器的底图，F-3 的增益分母必须由它反推
                //   （参照：libultrahdr 底图侧「Hard clip out-of-gamut values」发生在存储之前
                //    jpegr.cpp:2172-2177，而增益分母采自这张已存好的 sdr_intent jpegr.cpp:871-885；
                //    libavif 更直白——gainmap.c:676-697 直接 avifGetRGBAPixel(baseRgbImage,…) 读存储像素）。
                var basePath = Path.Combine(tempDir, "base.jpg");
                var basePng = Path.Combine(tempDir, "base.png");
                byte[] baseBgra = BuildBaseBgra8(sdrLinear, pixelCount);
                await RunFfmpegRawToPngAsync(baseBgra, w, h, pixelCount * 4, "bgra", basePng, ffmpeg, log, ct);

                // ── 3. 增益图计算 + 降采样 + PNG（容器无关）──
                //   log2(HDR/SDR) ∈ [0, maxLog2Gain] → [0,255]（上映射；两侧均已在底图色域内）
                //   ⚠ F-3：分母用 **sdrStored**（底图真正交付的值），**不是**钳前的 sdrLinear。
                float[] sdrStored = BufferPool.RentFloat(pixelCount * 4);
                StoredBaseLinearFromBgra(baseBgra, sdrStored, pixelCount);
                BufferPool.ReturnByte(baseBgra);
                // 双侧区间的下沿只在「底图=源」时才需要实测：用**真正交付的底图字节**当分母，
                // 与量化器/声明三处同源（F-3、F-5 的「声明==使用」不变）。
                if (baseFromSource)
                {
                    // ⚠ 量尺：只有两图合成（measureGainRange）才把单色下沿改成亮度统计。
                    //   既有 A 路（单输入配对重封装）保持逐通道旧口径 ⇒ 输出逐字节不变；
                    //   那条路径的单色下沿口径偏差属独立一批（改它会动既有门禁读数）。
                    var (p01, absMin, measured) = MeasureMinLog2Gain(normHdr, sdrStored, pixelCount,
                        luminance: measureGainRange && !multiChannel);
                    minLog2Gain = measured ? p01 : 0f;
                    if (!measured)
                        // 纯 ASCII：与 [vendor-hdr] 那批诊断同一口径（多行拼接的**续行**不含 sink，
                        // 会被 cjk 棘轮算进「UI 面」桶 ⇒ 白抬一次基线；一条写完就归到诊断桶）。
                        log?.Invoke("[GainMap] WARN: fewer than 100 valid samples => the 1% percentile is not trustworthy;"
                            + " gainMapMin stays 1.0 (cannot measure != there is no shadow band)\n");
                    log?.Invoke($"[GainMap] 暗部端点：1% 分位 log2 gain = {minLog2Gain:F5}"
                        + $"（线性 {MathF.Pow(2f, minLog2Gain):F4}×，量尺={(multiChannel ? "逐通道" : "亮度")}）⇒ 写进 gainMapMin；"
                        + $"绝对最小值 = {absMin:F5}（{MathF.Pow(2f, absMin):F6}×，按规范排除最低 1% 不进声明）\n");

                    // 量程实测（两图合成）：底图若没达到自己的 SDR 白点，实际需要的增益会超过
                    // log2(peak/203)。仍按名义值写 ⇒ 超出部分被量化端静默钳顶。
                    if (measureGainRange)
                    {
                        var (p9999, absMax, measuredMax) = MeasureMaxLog2Gain(normHdr, sdrStored, pixelCount, multiChannel);
                        if (measuredMax)
                        {
                            maxLog2Gain = p9999;
                            log?.Invoke($"[GainMap] 增益量程：99.99% 分位 log2 gain = {p9999:F5}"
                                + $"（绝对最大 {absMax:F5}；显示容量仍为 {hdrCapacityLog2:F5} = peak/203）"
                                + $" ⇒ 量化区间 [{minLog2Gain:F5}, {maxLog2Gain:F5}]\n");
                        }
                        else
                        {
                            log?.Invoke("[GainMap] WARN: too few valid samples => gain range stays at the nominal"
                                + " log2(peak/203); a darker SDR base may clip highlights\n");
                        }
                    }
                }
                byte[] gainMapPixels = ComputeGainMap(normHdr, sdrStored, w, h, multiChannel, maxLog2Gain, minLog2Gain);
                BufferPool.ReturnFloat(sdrStored);
                byte[] gainMapScaled = RescaleGainMap(gainMapPixels, w, h, multiChannel, downsample, out int gmW, out int gmH);
                BufferPool.ReturnByte(gainMapPixels);
                var gmPath = Path.Combine(tempDir, "gainmap.jpg");
                var gmPng = Path.Combine(tempDir, "gainmap.png");
                await WriteGrayOrRgbPngAsync(gainMapScaled, gmW, gmH, multiChannel, gmPng, ffmpeg, log, ct);
                BufferPool.ReturnByte(gainMapScaled);

                // ── 4a. AVIF 出口：ISO-BMFF `tmap` 派生项（原生容器组装）──────────────
                // ⚠ 与 JPEG 出口**共用**上面全部像素阶段（归一化/Reinhard/底图色域映射/增益图计算），
                //   只换容器与元数据承载方式 ⇒ 两条出口的增益语义必然一致（不允许各算一遍）。
                if (string.Equals(container, "avif", StringComparison.OrdinalIgnoreCase))
                {
                    // AVIF 的增益图只能由 tmap 派生项承载 ISO 21496-1；没有「XMP-only 增益图」这一标准形态。
                    if (metadata == "xmp")
                    {
                        log?.Invoke("[GainMap] ERROR: AVIF has no XMP-only gain-map form (the tmap derived item"
                                  + " carries ISO 21496-1 only) => refusing instead of silently rewriting the carrier.\n");
                        return false;
                    }
                    if (metadata == "both")
                        log?.Invoke("[GainMap] NOTE: AVIF gain map is carried by tmap/ISO 21496-1 only;"
                                  + " the XMP part of 'both' is not written (no standard reader path in AVIF).\n");
                    return await EncodeAvifGainMapAsync(basePng, gmPng, w, h, gmW, gmH, multiChannel,
                        maxLog2Gain, minLog2Gain, hdrCapacityLog2, wideBase, outputPath, ffmpeg, log, ct,
                        avifEncoder, baseCrf, gainMapCrf);
                }

                // ── 4b. JPEG 出口（Ultra HDR：XMP + MPF + ISO 21496-1 APP2）──────────
                var baseOk = await EncodeJpegFileAsync(basePng, basePath, baseQuality, jpegProgressiveLevel,
                    ColorMapping.GainMapJpegCodec.BaseJpegChromaSubsampling, log, ct, jpegEncoder);
                if (!baseOk)
                {
                    log?.Invoke("[GainMap] ❌ Base JPEG 编码失败\n");
                    return false;
                }
                log?.Invoke($"[GainMap] Base JPEG: {Math.Round(new FileInfo(basePath).Length / 1024.0)} KB\n");

                // 底图色域必须由底图自身的色彩描述表达（JPEG 无 CICP 机制）：
                // 对齐参考实现 writeIccProfile(UHDR_CT_SRGB, cg) —— 无论 sRGB 还是广色域底都附对应 ICC。
                // ICC 由 iccgen(lcms2) 生成（实测其 BT.2020 色度与 libultrahdr kRec2020 逐位一致）。
                {
                    var baseIcc = IccProfileService.GetOrGenerateStandardIcc(basePath,
                        wideBase ? "bt2020" : "bt709", "iec61966-2-1", wideBase ? "bt2020nc" : "bt709", log, ct);
                    if (baseIcc != null)
                    {
                        var iccExit = await ExifToolService.EmbedIccProfileFromFileAsync(baseIcc, basePath, log ?? (_ => { }));
                        log?.Invoke(iccExit == 0
                            ? $"[GainMap] 底图已附 {(wideBase ? "Rec.2020 SDR" : "sRGB")} ICC（{Path.GetFileName(baseIcc)}）\n"
                            : $"[GainMap] ⚠️ 底图 ICC 嵌入失败（退出码 {iccExit}），查看器可能按错误色域解读底图\n");
                    }
                    else
                    {
                        log?.Invoke("[GainMap] ⚠️ 无法生成底图 ICC（需 ffmpeg/iccgen）；底图色域仅由惯例推断\n");
                    }
                }

                // F-4（2026-10-11）：增益图 JPEG 的色度采样必须与**声明的通道数成套**。
                //   参照：RGB888 增益图的采样因子全 1×1（jpegencoderhelper.cpp:41-42，应用点 :189-192
                //   ⇒ 色度**零降采样**）。本仓 multiChannel=true 时逐通道增益写进 R/G/B 三个分量，
                //   过 4:2:0 就等于把 G/B 的增益按色度下采样摧毁 ⇒ 「flags 说多通道、数据是 4:2:0」=说谎。
                //   参照的读侧「信流不信 flag」（jpegr.cpp:2843-2844 按 SOF numComponents 定布局）
                //   **救不了写侧**：声明与交付一致是生产者的义务。
                var gmOk = await EncodeJpegFileAsync(gmPng, gmPath, gainMapQuality, jpegProgressiveLevel,
                    ColorMapping.GainMapJpegCodec.GainMapChromaSubsampling(multiChannel), log, ct, jpegEncoder);
                if (!gmOk)
                {
                    log?.Invoke("[GainMap] ❌ 增益图 JPEG 编码失败\n");
                    return false;
                }
                log?.Invoke($"[GainMap] 增益图: {gmW}x{gmH}, {Math.Round(new FileInfo(gmPath).Length / 1024.0)} KB"
                    + $"（通道数 {(multiChannel ? 3 : 1)}、色度采样 "
                    + $"{ColorMapping.GainMapJpegCodec.GainMapChromaSubsampling(multiChannel)}，与 tmap/ISO 声明成套）\n");

                // ── 4. 打包 (XMP + MPF + ISO 21496-1) ──
                byte[] baseJpeg = File.ReadAllBytes(basePath);
                // ⚠⚠ 2026-09-20（裁定：**SDR ⇒ 普通 JPEG**）：**headroom ≤ 1（增益区间为 0）时不写增益图**。
                //   理由：`--jpeg-gain-map` 的语义是「为 **HDR** 内容提供可上映射的增益」；
                //   SDR 输入没有可承载的 HDR 增益 ⇒ 原先仍会写出一张 `GainMapMin=GainMapMax=0` 的
                //   **假 Ultra HDR**（自称 Ultra HDR、实际零增益）—— 属本仓最忌讳的「**宣称≠交付**」。
                //   ⇒ 直接以**底图 JPEG** 作产物（= 普通 JPEG，像素与原先完全一致），并**点名**为何未启用。
                //   ⚠ 判据用**数值**（区间**宽度** `max-min <= 0`）而非字符串比 `"0.00"`；
                //     自造底图时下沿恒 0 ⇒ 与改动前的 `maxLog2Gain <= 0` 逐字等价；
                //     双侧（底图=源）时只有「上沿=下沿」才算零增益（纯下压也是一张真增益图）。
                if (maxLog2Gain - minLog2Gain <= 0f)
                {
                    File.WriteAllBytes(outputPath, baseJpeg);
                    log?.Invoke("[GainMap] ⚠️ 输入为 SDR（headroom ≤ 1，增益区间为 0）⇒ 无可承载的 HDR 增益，已按**普通 JPEG** 输出（未写入增益图）。需要 Ultra HDR 请改用 HDR 输入。\n");
                    return true;
                }
                byte[] gainMapJpeg = File.ReadAllBytes(gmPath);
                log?.Invoke($"[GainMap] gain-map metadata carrier: {metadata}"
                          + (metadata == "both" ? " (XMP hdrgm + ISO 21496-1 APP2)" : "") + "\n");
                if (!WriteJpegGainMapFile(baseJpeg, gainMapJpeg, w, h, gmW, gmH, multiChannel,
                        maxLog2Gain, hdrCapacityLog2, minLog2Gain, outputPath, metadata))
                {
                    // 与「SDR 输入 ⇒ 普通 JPEG」的短路不同：那条是**主动决定**并点名说明；
                    // 这条是**没能**写出增益图 ⇒ 必须宣告失败，不允许留下"自称 Ultra HDR"的产物。
                    log?.Invoke("[GainMap] ❌ Ultra HDR 封装失败：底图 JPEG 中定位不到 SOS，" +
                                "无法插入 XMP/MPF ⇒ 未写出增益图（也未产出「假 Ultra HDR」）\n");
                    return false;
                }
                log?.Invoke($"[GainMap] ✅ Ultra HDR JPEG: {Math.Round(new FileInfo(outputPath).Length / 1024.0)} KB\n");
                return true;
                }
                finally
                {
                    BufferPool.ReturnFloat(normHdr);
                    BufferPool.ReturnFloat(sdrLinear);
                }
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

    /// <summary>
    /// SDR 基图色调映射（单调，与 SimdPixelOps.SegmentedReinhardScalar 语义一致）：
    /// y≤1.0（SDR 内容，1.0=SDR 白点 203nits）直通；y>1.0（HDR 高光）单调钳位到 1.0。
    /// ⚠️ 旧 reinhard*=2 归一化产生非单调曲线(高光暗环)，已修正为单调钳位。
    /// 注：本方法为参考实现（活跃路径走 SimdPixelOps.ReinhardToSdr）。
    /// </summary>
    private static float SegmentedReinhardMap(float y, float headroom)
    {
        return y <= 1.0f ? y : 1.0f;
    }

    // ═══════════════════════════════════════════
    //  增益图计算
    // ═══════════════════════════════════════════

    /// <summary>
    /// 逐像素增益比计算 (2026-08-15: 灰度分支 SIMD 加速; 2026-10-11: F-5/F-3 收口)。
    /// 灰度: gain = log2(亮度(HDR)/亮度(SDR)); RGB: 三通道独立。
    /// 映射 [0, maxLog2Gain] → [0, 255] (Reinhard 保证增益 ≥ 1)。
    /// <para>
    /// ⚠ **两侧操作数的口径**（本方法的两个入参都由调用方 <see cref="EncodeAsync"/> 按参照喂）：
    /// <list type="bullet">
    /// <item><b>分母</b> <paramref name="sdr"/> = 底图**真正交付**的值（钳位+8-bit 量化后回线性，
    /// 见 StoredBaseLinearFromBgra）—— 参照的增益分母恒等于「解码器将从底图读回的那个数」（备忘 Q3）。</item>
    /// <item><b>公式</b> = <see cref="SimdPixelOps.Log2Gain"/>，**含声明的 offset**（备忘 Q1 裁①），
    /// 灰度支路与 RGB 支路调的是同一个式子 ⇒ 「同一份数学只有一份实现」。</item>
    /// <item><b>量程</b> <paramref name="maxLog2Gain"/> = **声明的** headroom（log2），
    /// 无内容统计遍 ⇒ 本仓是**一趟**形态 ⇒ 取整口径必须跟参照一趟的 encodeGain 一样是**截断**（F-2，
    /// 见 SimdPixelOps.LogGainToByteScalar 的前置条件注释）。</item>
    /// </list>
    /// </para>
    /// <remarks>
    /// internal 而非 private：批次 C 的进程内锁（tests/projects/GainMapTestHost `gainmath` 模式，C1/C3 段）
    /// 必须能直接量到产品实际使用的这份数学，而不是在测试里再写一遍（双拷贝=本仓禁例）。
    /// </remarks>
    internal static byte[] ComputeGainMap(float[] hdr, float[] sdr, int w, int h,
        bool multiChannel, float maxLog2Gain, float minLog2Gain = 0f)
    {
        int pixelCount = w * h;
        int channels = multiChannel ? 3 : 1;
        byte[] gain = BufferPool.RentByte(pixelCount * channels);

        // 灰度: SIMD 批量 (亮度点积 + log2 多项式)
        if (!multiChannel)
        {
            SimdPixelOps.ComputeGainMapGray(hdr, sdr, gain, maxLog2Gain, KOffsetHdr, KOffsetSdr, minLog2Gain);
            return gain;
        }

        // RGB: 三通道独立 (标量; 通道数×log2 无向量收益)
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            int off = i * 3;
            bool floorAtOne = minLog2Gain >= 0f;
            gain[off] = LogGainToByte(SimdPixelOps.Log2Gain(hdr[o], sdr[o], KOffsetHdr, KOffsetSdr, floorAtOne), maxLog2Gain, minLog2Gain);
            gain[off + 1] = LogGainToByte(SimdPixelOps.Log2Gain(hdr[o + 1], sdr[o + 1], KOffsetHdr, KOffsetSdr, floorAtOne), maxLog2Gain, minLog2Gain);
            gain[off + 2] = LogGainToByte(SimdPixelOps.Log2Gain(hdr[o + 2], sdr[o + 2], KOffsetHdr, KOffsetSdr, floorAtOne), maxLog2Gain, minLog2Gain);
        }
        return gain;
    }

    /// <summary>
    /// log2(gain) → 8-bit: 映射 [0, maxLog2Gain] → [0, 255]。
    /// ⚠ **转发到 SimdPixelOps.LogGainToByteScalar，不留第二份实现**（F-2 收口 2026-10-11）：
    /// 取整口径=**截断**（与参照一趟 encodeGain 同形，一手锚
    /// tools/src/libultrahdr/lib/src/gainmapmath.cpp:770），要改 +0.5 必须整体转两趟
    /// （实测 min/max 进声明，形如 gainmapmath.cpp:788 的 affineMapGain）——
    /// 前置条件逐条写在 SimdPixelOps.LogGainToByteScalar 的注释里，本处不重复一份口径说明。
    /// </summary>
    private static byte LogGainToByte(float logGain, float maxLog2Gain, float minLog2Gain = 0f)
        => SimdPixelOps.LogGainToByteScalar(logGain, maxLog2Gain, minLog2Gain);

    /// <summary>
    /// 读解码器产出的**源 SDR 底图**裸流（gbrpf32le，值是 0..1 的 sRGB 编码值）→ 线性 RGBA 交错，
    /// 填进 <paramref name="sdrLinear"/>（约定与 Reinhard 输出一致：1.0 = SDR 白点）。
    /// <para>不新造格式解析：平面→交错复用解码侧那一份 <c>GainMapDecoder.PlanarToInterleaved</c>
    /// （双拷贝=本仓禁例），线性化复用 <see cref="SimdPixelOps.SrgbToLinearRgba"/>。</para>
    /// </summary>
    private static bool LoadSourceBaseLinear(string rawPath, float[] sdrLinear, int pixelCount, Action<string>? log)
    {
        try
        {
            long need = (long)pixelCount * 12L;                  // 3 平面 × float32
            var fi = new FileInfo(rawPath);
            // ⚠ 必须**精确**相等：旧实现只查下界 ⇒ 超长裸流（更大尺寸的底图）会被静默按前 N 个
            //   float 切开，拼出一张错位的底图且一行日志都没有（2026-10-24 实测）。
            if (fi.Length != need)
            {
                log?.Invoke($"[GainMap] ⚠️ 源底图裸流长度不符（{fi.Length} B ≠ {need} B，"
                    + $"应为 {pixelCount} 像素 × 3 通道 × 4B）⇒ 回落 Reinhard\n");
                return false;
            }
            float[] rgba = GainMapDecoder.PlanarToInterleaved(File.ReadAllBytes(rawPath), pixelCount);
            SimdPixelOps.SrgbToLinearRgba(rgba.AsSpan(0, pixelCount * 4));
            Array.Copy(rgba, sdrLinear, (long)pixelCount * 4);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[GainMap] ⚠️ 源底图读取失败（{ex.GetType().Name}）⇒ 回落 Reinhard\n");
            return false;
        }
    }

    /// <summary>
    /// 实测 log2 增益的**下沿**（供 A 路写 gainMapMin）。分母必须是 `sdrStored`
    /// （底图真正交付的字节）而不是钳前 float —— 与 F-3 同一条口径，否则「声明」与「像素」再次脱节。
    /// <para>⚠ 取 **1% 分位**而不是全图最小值：规范原文即建议「为增强鲁棒性可排除最低/最高 1% 像素值」。
    /// 实测教训（2026-10-15，OnePlus 分块 HEIC → Ultra HDR）：用全图极值会得到 −9.85 log2（0.0011×），
    /// 比源自己声明的 −0.164（0.891×）夸张 60 倍 ⇒ 极少数黑位/边界像素就能把整张图的暗部映射拖没，
    /// 而且是「声明 ≠ 绝大多数像素」的谎。返回 (分位值, 绝对最小值) 两个数，便于把尾巴问题看出来。</para>
    /// </summary>
    private static (float p01, float absMin, bool measured) MeasureMinLog2Gain(
        float[] hdr, float[] sdrStored, int pixelCount, bool luminance)
    {
        const int Bins = 4096;
        const float Lo = -16f, Hi = 16f;
        var hist = new int[Bins];
        long total = 0;
        float absMin = 0f;

        void Accumulate(float g)
        {
            if (g <= -10f || g >= 10f) return;              // 不可表达哨兵/异常值不进统计
            if (g < absMin) absMin = g;
            int b = (int)((g - Lo) / (Hi - Lo) * Bins);
            if (b < 0) b = 0; else if (b >= Bins) b = Bins - 1;
            hist[b]++; total++;
        }

        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            if (!luminance)
            {
                for (int c = 0; c < 3; c++)
                    Accumulate(SimdPixelOps.Log2Gain(hdr[o + c], sdrStored[o + c], KOffsetHdr, KOffsetSdr, floorAtOne: false));
            }
            else
            {
                // 单色增益图存的是**亮度**增益（ComputeGainMapGray），下沿必须用同一把尺子。
                // 逐通道取分位会得到远低于亮度分位的值（实测 -6.02 vs -0.000），
                // 把 8-bit 量程无谓放大 4 倍多。
                float hLum = 0.2126f * hdr[o] + 0.7152f * hdr[o + 1] + 0.0722f * hdr[o + 2];
                float sLum = 0.2126f * sdrStored[o] + 0.7152f * sdrStored[o + 1] + 0.0722f * sdrStored[o + 2];
                Accumulate(SimdPixelOps.Log2Gain(hLum, sLum, KOffsetHdr, KOffsetSdr, floorAtOne: false));
            }
        }
        float p01 = 0f;
        // ⚠ 审查（2026-10-15）补的前置：样本不足 100 个时 `total/100 == 0` ⇒ 直方图**第一个非空 bin**
        //   就满足 `run >= want` ⇒ p01 会落到网格下界（≈-16 log2 = 0.000015×），把一个"测不出来"
        //   写成一个荒谬的下沿声明 ⇒ 元数据说谎。规范说的"排除最低 1%"本来就要求样本量足够，
        //   不够就不声明下沿（p01 保持 0 ⇒ minContentBoost=1，与"取不到"同一出口）。
        if (total >= 100)
        {
            long want = total / 100;                        // 最低 1% 分位
            long run = 0;
            for (int b = 0; b < Bins; b++)
            {
                run += hist[b];
                if (run >= want) { p01 = Lo + (b + 0.5f) * (Hi - Lo) / Bins; break; }
            }
        }
        // 第三个返回值 = 样本量够不够谈「1% 分位」（<100 时 total/100==0，第一个非空 bin 就会命中
        //   ⇒ 分位会落到网格下界 -16，等于把「测不出来」写成一条荒谬的下沿声明）。不够就不声明下沿。
        return (Math.Min(p01, 0f), Math.Min(absMin, 0f), total >= 100);
    }

    /// <summary>
    /// 实测 log2 增益的**上沿**（两图合成的量化量程）。同一把尺子：单色 = 亮度增益，多通道 = 逐通道。
    /// <para>取 **99.99% 分位**而不是全图最大值：一个接近 0 的底图分母就能把比率推到 2^20 量级，
    /// 把 8-bit 的步长从 1.7% 拉到 10% 以上（牺牲整张图的精度去迁就万分之一像素）。
    /// 返回绝对最大值便于把尾巴问题看出来；超出声明量程的像素由量化端钳到 max（日志点名）。</para>
    /// </summary>
    private static (float p9999, float absMax, bool measured) MeasureMaxLog2Gain(
        float[] hdr, float[] sdrStored, int pixelCount, bool multiChannel)
    {
        const int Bins = 8192;
        const float Lo = -16f, Hi = 16f;
        var hist = new int[Bins];
        long total = 0;
        float absMax = 0f;

        void Accumulate(float g)
        {
            if (g <= -10f || g >= 10f) return;
            if (g > absMax) absMax = g;
            int b = (int)((g - Lo) / (Hi - Lo) * Bins);
            if (b < 0) b = 0; else if (b >= Bins) b = Bins - 1;
            hist[b]++; total++;
        }

        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            if (multiChannel)
            {
                for (int c = 0; c < 3; c++)
                    Accumulate(SimdPixelOps.Log2Gain(hdr[o + c], sdrStored[o + c], KOffsetHdr, KOffsetSdr, floorAtOne: false));
            }
            else
            {
                float hLum = 0.2126f * hdr[o] + 0.7152f * hdr[o + 1] + 0.0722f * hdr[o + 2];
                float sLum = 0.2126f * sdrStored[o] + 0.7152f * sdrStored[o + 1] + 0.0722f * sdrStored[o + 2];
                Accumulate(SimdPixelOps.Log2Gain(hLum, sLum, KOffsetHdr, KOffsetSdr, floorAtOne: false));
            }
        }
        float p9999 = 0f;
        if (total >= 100)
        {
            long want = Math.Max(total / 10000, 1);         // 最高 0.01% 分位（样本少时即最大值）
            long run = 0;
            for (int b = Bins - 1; b >= 0; b--)
            {
                run += hist[b];
                if (run >= want) { p9999 = Lo + (b + 0.5f) * (Hi - Lo) / Bins; break; }
            }
        }
        return (Math.Max(p9999, 0f), Math.Max(absMax, 0f), total >= 100);
    }

    // ═══════════════════════════════════════════
    //  降采样
    // ═══════════════════════════════════════════

    /// <summary>
    /// 增益图降采样 (默认 1/4 分辨率), ceiling 除法确保不丢右/下边缘像素, 块均值。
    /// <para>
    /// F-2（2026-10-11）关于这里**第二段截断**的裁定：参照在本处**没有对应物**——它是**比例点采样**
    /// （jpegr.cpp:759 / :872 的 sdr_sample_pixel_fn(..., mMapDimensionScaleFactor, x, y)），
    /// 不平均 ⇒ 天然只有一次量化。所以「块均值再截断」不是「对齐参照」的问题，参照既不禁它也不禁改 round
    /// （备忘 Q2 例外①：改 round 或改点采样都不违反参照，改点采样离参照更近）。
    /// 本轮**保持截断不动**，理由与 LogGainToByteScalar 同源：本仓是一趟形态，取整口径要跟趟数一起改；
    /// 单点换成 +0.5 会得到「声明端点仍是定值、字节序列却与参照一趟产物不等价」的半改状态（审查纪律禁止）。
    /// </para>
    /// </summary>
    private static byte[] RescaleGainMap(byte[] src, int w, int h, bool multiChannel,
        int factor, out int outW, out int outH)
    {
        outW = (w + factor - 1) / factor;
        outH = (h + factor - 1) / factor;
        int channels = multiChannel ? 3 : 1;
        byte[] dst = BufferPool.RentByte(outW * outH * channels);

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

    /// <summary>
    /// 底图字节生成器（**唯一**的一份「线性 SDR → 交付字节」实现）：
    /// 线性 RGBA → sRGB gamma → bgra8 raw（池化数组，调用方负责 ReturnByte），再由
    /// <see cref="RunFfmpegRawToPngAsync"/> 落成 PNG。
    /// F-3（2026-10-11）：增益分母由**这份字节**反推（见 <see cref="StoredBaseLinearFromBgra"/>），
    /// 所以本方法与底图 PNG 必须是同一次调用产出的同一份数据 ⇒ 不许再写第二份转换
    /// （本仓刚因 contract 双拷贝踩过「全绿但零执行」）。
    /// pix_fmt=bgra 字节序 B,G,R,A（不是 RGBA）→ 通道需重排。
    /// 2026-08-15: FloatToSrgb8 SIMD 加速（128 像素分块，避开 AVX2 gather 陙阱）。
    /// </summary>
    private static byte[] BuildBaseBgra8(float[] srcRgba, int pixelCount)
    {
        var raw = BufferPool.RentByte(pixelCount * 4);
            // SIMD 批量转换: 线性→sRGB 8-bit (R/G/B 三通道)
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
                        rBuf[k] = srcRgba[(baseIdx + k) * 4 + c];
                    SimdPixelOps.FloatToSrgb8(rBuf[..chunk], bBuf[..chunk]);
                    for (int k = 0; k < chunk; k++)
                        raw[(baseIdx + k) * 4 + dstC] = bBuf[k];
                }
            }
            // alpha = 255
            for (int i = 0; i < pixelCount; i++)
                raw[i * 4 + 3] = 255;
        return raw;
    }

    /// <summary>
    /// 底图**真正交付的那份字节**（sRGB 8-bit BGRA）→ 线性 float（RGBA 交错）。
    ///
    /// <para>
    /// 这是 F-3（2026-10-11）的分母唯一来源：增益分母必须等于**解码器将从底图读回的那个值**，
    /// 而不是钳位前的 float。三处一手依据（备忘 Q3）：
    /// ① libultrahdr 底图侧「Hard clip out-of-gamut values」发生在**存储之前**
    ///    （jpegr.cpp:2172-2177 → clampPixelFloat，钳 [0,1] 见 gainmapmath.h:559-563）；
    /// ② 增益分母采自这张已钳已量化的底图（两趟 jpegr.cpp:871-885 用
    ///    <code>sdr_sample_pixel_fn(sdr_intent,…)</code> 采样，一趟同形 :759-772），
    ///    且默认 <code>sdrGamutConversionFn = identityConversion</code>（jpegr.cpp:609-622）
    ///    ⇒ **base 侧根本不过矩阵**，">1 被钳"这一支在参照架构里结构上不存在；
    /// ③ libavif 更直白：分母直接读存储像素（gainmap.c:676-697 avifGetRGBAPixel(baseRgbImage,…)）。
    /// </para>
    /// <para>
    /// 本仓的矩阵在底图侧**也会**过（2026-09 的 useBaseColorSpace 收口），越界是真实存在的
    /// （2020 源 → sRGB 底，验证 lane 复算越界率 32.4%、max 1.6605）⇒ 分母必须回灌钳位+量化后的值，
    /// 否则「编码时以为底图是 1.29、解码时读回的是 1.00」⇒ 增益算小 ⇒ 高光恢复偏暗（FIX_PLAN F-6/F-3）。
    /// </para>
    /// <para>
    /// 与解侧**同源**：GainMapDecoder 对底图做的正是 SimdPixelOps.SrgbToLinearRgba(存储 8-bit)，
    /// 本方法用同一个反函数 ⇒ 「分母 == 解码器读到的值」是构造出来的恒等，不是近似。
    /// 唯一残差：底图字节由 <see cref="SimdPixelOps.FloatToSrgb8"/> 生成，SIMD 路由开启时与标量
    /// 差 ≤1 LSB（既有 D28 锁守这一格），1 LSB ≈ 0.4% 码值，比本项修掉的 (boost−1)·offset 量级小一个数量级。
    /// </para>
    /// </summary>
    internal static void StoredBaseLinearFromBgra(byte[] bgra, float[] outRgba, int pixelCount)
    {
        const float inv255 = 1f / 255f;
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            // 字节序 B,G,R,A（pix_fmt=bgra）
            outRgba[o + 0] = SimdPixelOps.SrgbToLinearScalar(bgra[o + 2] * inv255);
            outRgba[o + 1] = SimdPixelOps.SrgbToLinearScalar(bgra[o + 1] * inv255);
            outRgba[o + 2] = SimdPixelOps.SrgbToLinearScalar(bgra[o + 0] * inv255);
            outRgba[o + 3] = bgra[o + 3] * inv255;
        }
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
            await RunFfmpegRawToPngAsync(gainPixels, w, h, pixelCount * 3, "rgb24", pngPath, ffmpeg, log, ct);
        }
        else
        {
            // 灰度 → 复制为 RGB (避免 cjpegli 灰度兼容问题)
            var rgb = BufferPool.RentByte(pixelCount * 3);
            try
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = gainPixels[i];
                }
                await RunFfmpegRawToPngAsync(rgb, w, h, pixelCount * 3, "rgb24", pngPath, ffmpeg, log, ct);
            }
            finally { BufferPool.ReturnByte(rgb); }
        }
    }

    private static async Task RunFfmpegRawToPngAsync(byte[] raw, int w, int h, int exactBytes, string pixFmt,
        string pngPath, string ffmpeg, Action<string>? log, CancellationToken ct)
    {
        var rawPath = pngPath + ".raw";
        // 池化数组长度可能 > 请求；严格只写 exactBytes，避免脏尾（rawvideo 按帧读取，此处仍按精确写入以防万一）
        await using (var fs = new FileStream(rawPath, FileMode.Create, FileAccess.Write))
        {
            await fs.WriteAsync(raw.AsMemory(0, exactBytes), ct);
        }
        var args = $"-y -f rawvideo -pix_fmt {pixFmt} -s {w}x{h} -i \"{rawPath}\" -frames:v 1 -update 1 \"{pngPath}\"";
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
        using var p = Process.Start(psi)!;
        // 2026-09-21: this path previously MISSED the process-priority call, so the UI
        // "process priority" setting had no effect on this ffmpeg invocation.
        PlatformServices.SetSafePriority(p, AppSettingsService.Current.FfmpegPriority);
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        try { File.Delete(rawPath); } catch { }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg PNG 转换失败 (exit {p.ExitCode})");
    }

    /// <summary>把 PNG 编成 JPEG：优先 cjpegli（--distance），回退 ffmpeg mjpeg（-q:v 由 distance 换算）。
    /// 使 GainMap 不绑定特定 JPEG 编码器（符合“直接用所选 JPEG 编码器实现”）。
    /// <para>
    /// <paramref name="progressiveLevel"/> 是 cjpegli 的 `-p N`（0=sequential / 2=progressive）；
    /// `-1` = **不传**该参数、沿用 cjpegli 自身默认。⚠ 回退 ffmpeg mjpeg 时该选项**无法表达**
    /// （ffmpeg 的 mjpeg 编码器没有渐进能力）⇒ 记一条日志点名，绝不静默当没选。
    /// </para>
    /// </summary>
    private static async Task<bool> EncodeJpegFileAsync(string pngPath, string jpgPath,
        float distance, int progressiveLevel, string chromaSubsampling,
        Action<string>? log, CancellationToken ct, string? encoder = null)
    {
        bool useCjpegli;
        if (string.IsNullOrWhiteSpace(encoder))
        {
            // 旧语义（未显式选择）：有 cjpegli 就用，否则回退 mjpeg（并在下面点名渐进不可达）。
            useCjpegli = CjpegliService.IsAvailable;
        }
        else if (encoder.Equals("cjpegli", StringComparison.OrdinalIgnoreCase))
        {
            if (!CjpegliService.IsAvailable)
            {
                log?.Invoke("[GainMap] ERROR: JPEG encoder 'cjpegli' was requested but the tool is not available"
                          + " => refusing (no silent fallback to ffmpeg mjpeg).\n");
                return false;
            }
            useCjpegli = true;
        }
        else if (encoder.Equals("mjpeg", StringComparison.OrdinalIgnoreCase))
        {
            useCjpegli = false;
        }
        else
        {
            log?.Invoke($"[GainMap] ERROR: unknown JPEG encoder '{encoder}' (expected cjpegli | mjpeg).\n");
            return false;
        }

        if (useCjpegli)
        {
            log?.Invoke("[GainMap] JPEG encoder: cjpegli\n");
            var psi = new ProcessStartInfo
            {
                FileName = CjpegliService.DetectedPath!,
                // 命令行组装与色度采样的取值都在 GainMapJpegCodec（单一真值，见其 F-4 注释）
                Arguments = ColorMapping.GainMapJpegCodec.CjpegliCommandArguments(
                    pngPath, jpgPath, distance, progressiveLevel, chromaSubsampling),
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            // 2026-09-21: same as above - the external cjpegli encoder also needs the priority.
            PlatformServices.SetSafePriority(p, AppSettingsService.Current.FfmpegPriority);
            p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 && File.Exists(jpgPath) && new FileInfo(jpgPath).Length > 0;
        }
        // 回退 ffmpeg mjpeg：butteraugli distance(0.5-6) → 质量滑块 → -q:v（2-31，越小越好）。
        log?.Invoke("[GainMap] JPEG encoder: ffmpeg mjpeg\n");
        // ⚠ mjpeg 无渐进能力 ⇒ 已请求渐进级别时必须点名（否则用户以为设了渐进、拿到的却是基线）。
        // ⚠ 文案必须纯 ASCII：`_probe-cjk-hardcode-scan.ps1` 的 C# UI 面余量为 0（新增中文代码串必红）。
        if (progressiveLevel >= 0)
            log?.Invoke($"[GainMap] WARN: JPEG progressive level {progressiveLevel} requested, but cjpegli is unavailable"
                      + " => the ffmpeg mjpeg fallback has no progressive support; output is baseline (option not applied).\n");
        var ffmpegPath = AppSettingsService.Current.FfmpegPath;
        float qSlider = Math.Clamp(100f - distance * 4f, 1f, 100f);
        int qv = (int)Math.Round(2 + (100 - qSlider) * 29.0 / 100.0);
        var fpsi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-y -i \"{pngPath}\" -q:v {qv} \"{jpgPath}\"",
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var fp = Process.Start(fpsi)!;
        fp.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        fp.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        fp.BeginOutputReadLine();
        fp.BeginErrorReadLine();
        await fp.WaitForExitAsync(ct);
        return fp.ExitCode == 0 && File.Exists(jpgPath) && new FileInfo(jpgPath).Length > 0;
    }

    /// <summary>
    /// AVIF 出口：底图与增益图各编一张**单图** AVIF，再交给 <see cref="IsoBmffGainMapWriter"/>
    /// 组装成带 `tmap` 派生项的三 item 容器。
    /// <para>
    /// 为什么必须自行组装：ffmpeg 的 `avif` muxer **不提供**增益图授权（实测其 AVOption 只有
    /// `movie_timescale` / `loop`），libavif CLI 的增益图选项又被编译期开关门控 ⇒ 没有可用的现成命令。
    /// </para>
    /// <para>
    /// ⚠ 与 JPEG 出口**共用**全部像素阶段（归一化 / Reinhard / 底图色域映射 / 增益图计算），
    /// 本方法只负责「容器 + 元数据承载」，两条出口的增益语义因此必然一致。
    /// </para>
    /// </summary>
    private static async Task<bool> EncodeAvifGainMapAsync(string basePng, string gmPng, int w, int h, int gmW, int gmH,
        bool multiChannel, float maxLog2Gain, float minLog2Gain, float hdrCapacityLog2, bool wideBase, string outputPath,
        string ffmpeg, Action<string>? log, CancellationToken ct, string? avifEncoder = null,
        int baseCrf = -1, int gainMapCrf = -1)
    {
        var tempDir = Path.GetDirectoryName(basePng)!;
        var baseAvif = Path.Combine(tempDir, "base.avif");
        var gmAvif = Path.Combine(tempDir, "gainmap.avif");
        // -1 = 历史固定值（底图与增益图都 18）；显式值夹到 AV1 的 CRF 量程 0-63。
        int baseCrfN = baseCrf >= 0 ? Math.Clamp(baseCrf, 0, 63) : 18;
        int gainCrfN = gainMapCrf >= 0 ? Math.Clamp(gainMapCrf, 0, 63) : 18;

        // 底图色域与 JPEG 出口同口径（wideBase ⇒ Rec.2020 SDR，否则 sRGB）；
        // AVIF 有 CICP 机制（`colr` 盒），由 ffmpeg 按下列参数写入，不需要 ICC 兜底。
        string primaries = wideBase ? "bt2020" : "bt709";
        string trc = wideBase ? "bt709" : "iec61966-2-1";
        string matrix = wideBase ? "bt2020nc" : "bt709";

        if (!await EncodeAvifFileAsync(basePng, baseAvif, ffmpeg, "yuv420p", primaries, trc, matrix, log, ct, avifEncoder, baseCrfN))
        {
            log?.Invoke("[GainMap] ❌ 底图 AVIF 编码失败\n");
            return false;
        }

        // 增益图的**交付平面数**必须与 tmap 声明的**参数集数**成套（F-4/F-8，2026-10-11）：
        //   multiChannel=true  ⇒ 3 平面（yuv444p）。参照：RGB 增益图采样因子全 1×1
        //     （libultrahdr jpegencoderhelper.cpp:41-42），libavif 的辅助图也是
        //     altPlaneCount==1⇒YUV400 否则 YUV444（write.c:1062）⇒ 没有「多通道却降色度」这一支。
        //   multiChannel=false ⇒ 1 平面（gray=YUV400，参照单通道增益图=单分量灰度 jpegencoderhelper.cpp:27-28）。
        // ⚠ 旧写法**恒** gray：multiChannel=true 时 ffmpeg 把 R/G/B 三套增益混成一个 luma，
        //   而 tmap 照旧置 bit7 ⇒ 「声明 3 通道、数据 1 平面」=说谎（且逐通道增益已被摧毁）。
        //   兜底也只在**同一套语义**内兜：单通道编不出 gray ⇒ 退 yuv444p 并把参数**复制成套**
        //   （libavif gainmap.c:743-752 的 singleChannel 手法：ch0 复制给 c1/c2），
        //   绝不退 4:2:0（既毁逐通道数据又与任何声明都不成套），也不许把声明改口去迁就交付
        //   （「不许把降级当修复」）。
        string gmPixFmt = multiChannel ? "yuv444p" : "gray";
        int declaredChannels = multiChannel ? 3 : 1;
        if (!await EncodeAvifFileAsync(gmPng, gmAvif, ffmpeg, gmPixFmt, null, null, null, log, ct, avifEncoder, gainCrfN))
        {
            if (multiChannel)
            {
                log?.Invoke("[GainMap] ❌ 增益图 AVIF 编码失败：multiChannel=true 必须交付 3 平面 yuv444p，"
                          + "不退 4:2:0、也不退单平面 gray（那会写出「声明多通道、数据非逐通道」的容器）\n");
                return false;
            }
            log?.Invoke("[GainMap] ⚠️ 单色 gray 的 AVIF 编码失败 ⇒ 退回 yuv444p 三平面，"
                      + "并把 tmap 参数复制成套（声明随之为 3 参数集；不退 4:2:0）\n");
            if (!await EncodeAvifFileAsync(gmPng, gmAvif, ffmpeg, "yuv444p", null, null, null, log, ct, avifEncoder, gainCrfN))
            {
                log?.Invoke("[GainMap] ❌ 增益图 AVIF 编码失败\n");
                return false;
            }
            declaredChannels = 3;   // 参数成套复制（三组同值 ⇒ 与单通道施加逐字节等价）
        }

        byte[] baseBytes, gmBytes;
        try
        {
            baseBytes = await File.ReadAllBytesAsync(baseAvif, ct);
            gmBytes = await File.ReadAllBytesAsync(gmAvif, ct);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[GainMap] ❌ 读取 AVIF 中间产物失败: {ex.Message}\n");
            return false;
        }

        // ⚠ SDR 输入（增益区间宽度为 0）⇒ 与 JPEG 出口同口径：**不写增益图**，直接以底图 AVIF 作产物并点名。
        //   绝不产出「自称带增益图、实际零增益」的假 HDR 容器（本仓最忌讳的「宣称 ≠ 交付」）。
        //   判据用**宽度**：自造底图时下沿恒 0 ⇒ 与改动前 `maxLog2Gain <= 0` 逐字等价；
        //   底图=源时「只下压」也是一张真增益图，不许被当成零增益丢掉。
        if (maxLog2Gain - minLog2Gain <= 0f)
        {
            await File.WriteAllBytesAsync(outputPath, baseBytes, ct);
            log?.Invoke("[GainMap] ⚠️ 输入为 SDR（headroom ≤ 1，增益区间为 0）⇒ 已按普通 AVIF 输出（未写入 tmap 增益图）。需要 HDR 请改用 HDR 输入。\n");
            return true;
        }

        // tmap 载荷 = u8 version(0) + ISO 21496-1 GainMapMetadata —— 与 JPEG APP2 载荷**逐字节同构**，
        // 故直接复用同一个生成器（单一真值；在写出器里再写一份必然漂移）。
        // ⚠ 这里的通道数取 **declaredChannels**（上面按**实际交付的平面数**定），不是参数 multiChannel
        //   —— 否则又会写出「声明与交付脱节」（F-4）。写出器还会拿 pixi 平面数复核一次。
        var tmapPayload = IsoBmffGainMapWriter.BuildTmapPayload(
            BuildIso21496Metadata(declaredChannels, maxLog2Gain, minLog2Gain, hdrCapacityLog2));
        // 底图 CICP：ffmpeg 不采纳 -color_primaries（实测写出 unspecified）⇒ 由写出器重写 colr。
        // sRGB 底 = (1 bt709, 13 iec61966-2-1, 1 bt709)；Rec.2020 SDR 底 = (9, 1 bt709, 9 bt2020nc)。
        int cPrim = wideBase ? 9 : 1;
        int cTrc = wideBase ? 1 : 13;
        int cMtx = wideBase ? 9 : 1;
        var built = IsoBmffGainMapWriter.Build(baseBytes, gmBytes, tmapPayload, cPrim, cTrc, cMtx);
        if (!built.Ok)
        {
            log?.Invoke($"[GainMap] ❌ AVIF 增益图容器组装失败：{built.FailReason}\n");
            return false;
        }
        await File.WriteAllBytesAsync(outputPath, built.Data!, ct);
        log?.Invoke($"[GainMap] ✅ AVIF 增益图 (tmap)：底图 {built.BaseItemBytes} B + 增益图 {built.GainMapItemBytes} B + tmap {built.TmapPayloadBytes} B，合计 {Math.Round(new FileInfo(outputPath).Length / 1024.0)} KB"
            + $"（增益图交付 pix_fmt={gmPixFmt}、tmap 声明 {declaredChannels} 参数集 —— 两者由写出器复核成套）\n");
        return true;
    }

    /// <summary>
    /// 把 PNG 编成**单图 AVIF**（ffmpeg libaom-av1）。
    /// <paramref name="primaries"/> 为 null ⇒ 不写 `colr` 语义（增益图不需要）。
    /// </summary>
    private static async Task<bool> EncodeAvifFileAsync(string pngPath, string avifPath, string ffmpeg,
        string pixFmt, string? primaries, string? trc, string? matrix,
        Action<string>? log, CancellationToken ct, string? encoder = null, int crf = 18)
    {
        // 显式选择只接受**软件、CRF 语义**的 AV1 编码器：硬件 AV1 与增益图子图的精度/参数不匹配，
        // 不给"选了却悄悄换掉"的机会（不可用即失败，见调用方）。
        string enc = string.IsNullOrWhiteSpace(encoder) ? "libaom-av1" : encoder!;
        if (!enc.Equals("libaom-av1", StringComparison.OrdinalIgnoreCase)
            && !enc.Equals("libsvtav1", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"[GainMap] ERROR: AVIF encoder '{enc}' is not supported for gain-map sub-images"
                      + " (allowed: libaom-av1 | libsvtav1).\n");
            return false;
        }
        enc = enc.Equals("libsvtav1", StringComparison.OrdinalIgnoreCase) ? "libsvtav1" : "libaom-av1";
        log?.Invoke($"[GainMap] AVIF encoder: {enc} (CRF {crf}, pix_fmt {pixFmt})\n");
        // ⚠⚠ 2026-10-07 修复（D-② 同类点）：三个 token **必须**先过 `ColorSpaceRegistry` 的规范化器。
        //   本仓契约见 `ColorSpaceRegistry.cs:368-387`：ffmpeg 只认**自己的**常量名，而面向用户的词表是
        //   友好名（`srgb`/`pq`/`hlg`）与 zimg 风格名（`bt2020ncl`/`ycgcocn`）；不规范化直接拼 ⇒
        //   `Unable to parse "<opt>" option value "<token>"` ⇒ **退出码 -22、零产物**。
        //   实测（2026-10-07）：`-color_primaries srgb` ⇒ `[Eval] Undefined constant … in 'srgb'`、零产物；
        //   过规范化器后 `srgb → bt709` ⇒ rc=0、产物正常。
        //   ⚠ 与 `RawColorPipeline.WriteRgb48ToPngAsync` 是**同一处口径缺口的两条通路** —— 两处都收口，
        //     避免"修了一条、另一条还漏"（本仓反复记载的形态）。
        var np = ColorSpaceRegistry.NormalizePrimariesToken(primaries);
        var nt = ColorSpaceRegistry.NormalizeTrcToken(trc);
        var nm = ColorSpaceRegistry.NormalizeMatrixToken(matrix);
        var colorArgs = np != null
            ? $" -color_primaries {np}"
              + (nt != null ? $" -color_trc {nt}" : "")
              + (nm != null ? $" -colorspace {nm}" : "")
            : "";
        var args = $"-y -i \"{pngPath}\" -frames:v 1 -c:v {enc} -crf {crf} -b:v 0 -pix_fmt {pixFmt}"
                 + $"{colorArgs} -f avif \"{avifPath}\"";
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
        using var p = Process.Start(psi)!;
        PlatformServices.SetSafePriority(p, AppSettingsService.Current.FfmpegPriority);
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data + "\n"); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 && File.Exists(avifPath) && new FileInfo(avifPath).Length > 0;
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
    /// 源→底图的线性域原色矩阵，三级求解：<br/>
    /// ① **具名空间**（源色域没有 CICP 命名、探测登记了近似时唯一可信的一条，走 registry 的
    ///    <see cref="ColorSpaceRegistry.LinearMatrix"/>，不在此另写矩阵公式）；<br/>
    /// ② CICP token（<see cref="ColorSpaceRegistry.LinearMatrixBetweenPrimaries"/>）；<br/>
    /// ③ 同空间 ⇒ 恒等（不做映射，也**不该**告警）。<br/>
    /// 返回 (矩阵, 依据的源名, 是否恒等)；矩阵为 null 且非恒等 = 谁都命名不了 ⇒ 调用方必须点名。
    /// </summary>
    private static (double[]? Mtx, string From, bool Identity) ResolveGamutMatrix(
        string? token, string? spaceName, string baseTok, string baseName)
    {
        if (!string.IsNullOrWhiteSpace(spaceName))
        {
            var nm = ColorSpaceRegistry.LinearMatrix(spaceName, baseName);
            if (nm != null) return (nm, spaceName!, false);
        }
        bool same = string.Equals((token ?? "").Trim(), baseTok, StringComparison.OrdinalIgnoreCase);
        if (same) return (null, token ?? "", true);
        var m = ColorSpaceRegistry.LinearMatrixBetweenPrimaries(token, baseTok);
        if (m != null) return (m, token ?? "", false);
        return (null, string.IsNullOrWhiteSpace(token) ? "(未探到原色)" : token!, false);
    }

    /// <summary>构建最终 Ultra HDR JPEG 文件。
    /// 结构:
    ///   [Base JPEG: SOI][APP1: XMP][APP2: MPF][Base 其余 (DQT/SOF/DHT/SOS/扫描/EOI)]
    ///   [增益图 JPEG: SOI][APP1: XMP(hdrgm 属性形式)][APP2: ISO 21496-1][增益图其余]
    /// </summary>
    /// <param name="metadata">
    /// <c>iso</c> = 只写次图的 APP2 ISO 21496-1；<c>xmp</c> = 只写 hdrgm XMP，底图（元素形式）
    /// 与次图（属性形式）各一份；<c>both</c> = 两种承载都写。MPF 在三种形态下都必须写：
    /// 它是「第二张图在哪」的唯一索引，少了它两种承载都找不到增益图。
    /// </param>
    /// <returns>true=已写出完整 Ultra HDR；false=**未能**写出增益图（调用方必须宣告失败，不得报成功）。</returns>
    private static bool WriteJpegGainMapFile(byte[] baseJpeg, byte[] gainMapJpeg,
        int baseW, int baseH, int gmW, int gmH, bool multiChannel,
        float maxLog2Gain, float hdrCapacityLog2, float minLog2Gain, string outputPath,
        string metadata = "both")
    {
        bool writeIso = MetadataWritesIso(metadata);
        bool writeXmp = MetadataWritesXmp(metadata);
        // 1. 定位 Base JPEG 的 SOS (FFDA) 标记
        int sosIndex = FindSosIndex(baseJpeg);
        if (sosIndex < 0)
        {
            // ❌ 定位不到 SOS ⇒ 无法在扫描数据之前插入 XMP/MPF ⇒ **根本写不出 Ultra HDR**。
            //    旧实现此时静默写出一张**普通 JPEG**，而调用方照样打 "✅ Ultra HDR" —— 这正是本仓
            //    最忌讳的「宣称≠交付」（对比 EncodeAsync 中 maxLog2Gain<=0 的 SDR 短路：那条会**点名**）。
            //    ⇒ 返回 false，由调用方宣告失败；绝不产出「自称 Ultra HDR 的普通 JPEG」。
            return false;
        }

        // 2. Base 头部段 = [SOI ... SOS 之前], 不含 SOS
        int baseHeaderLen = sosIndex;
        // SOS 段: marker(2) + length(2) + 参数
        int sosLen = 2 + ((baseJpeg[sosIndex + 2] << 8) | baseJpeg[sosIndex + 3]);
        int baseScanStart = sosIndex + sosLen;
        int baseScanLen = baseJpeg.Length - baseScanStart; // 含 EOI

        // 3. 次图先装配**自己**那几段，顺序不能反到后面：底图 XMP 的 Item:Length 与 MPF 的次图长度
        //    说的都是「次图在盘上的总字节数」，而总数取决于它自己带几段。
        //    [SOI][APP1: hdrgm XMP(属性形式)][APP2: ISO 21496-1][增益图数据(去 SOI)]
        //    ⚠ 次图那份 XMP 不是装饰，是**参考读手唯一的入口**：libultrahdr 的 `decodeJPEGR` 只把
        //      **增益图**的 ISO/XMP 交给 `parseGainMapMetadata`（jpegr.cpp:1495-1500 —— 底图那份它只用
        //      来取 Item 目录），而它的 XMP 读手按**属性**取数（jpegrutils.cpp:770-800 报
        //      "could not find attribute hdrgm:…"）。⇒ 只写底图（beta9 出货形状、元素形式）时，
        //      `xmp` 档产物对参考读手等于「没有元数据」——实测第三方读数
        //      「received no valid buffer to parse gainmap metadata」（门禁 D5e）。
        //      底图那份**照旧保留**：自家解码器与 exiftool 读它（#80 钉的 MapPrimary/MapSecondary/
        //      Channels 就在底图），两处数值同源于 FormatHdrgmNine ⇒ 不会写出互相矛盾的声明。
        byte[] iso = writeIso
            ? BuildIso21496Metadata(multiChannel, maxLog2Gain, minLog2Gain, hdrCapacityLog2)
            : Array.Empty<byte>();
        byte[] gainMapWithIso = writeIso ? InsertIsoIntoSegment(gainMapJpeg, iso) : gainMapJpeg;
        byte[] secXmp = writeXmp
            ? BuildSecondaryXmpMetadata(maxLog2Gain, minLog2Gain, hdrCapacityLog2)
            : Array.Empty<byte>();
        byte[] gainMapFinal = writeXmp ? InsertXmpIntoImage(gainMapWithIso, secXmp) : gainMapWithIso;

        // 4. XMP (含 Item:Length = 增益图完整大小)
        //   ⚠ 审查（2026-10-15）抓到的阻断缺陷：这里曾经**没传** `minLog2Gain` ⇒ 形参默认 0
        //     ⇒ XMP 写 `hdrgm:GainMapMin=0.0`，而上面 APP2 的 ISO 写的是真实的负下沿
        //     ⇒ 同一份文件的两处声明互相矛盾：按 XMP 读的消费者（Adobe/Android 一部分栈）会把
        //        暗部下沿当不存在，而自家 `MergeXmpIntoMetadata` 也立刻报「ISO 与 XMP 增益范围不一致」。
        byte[] xmp = writeXmp
            ? BuildXmpMetadata(baseW, baseH, gmW, gmH, multiChannel, maxLog2Gain, hdrCapacityLog2,
                gainMapFinal.Length, minLog2Gain)
            : Array.Empty<byte>();

        // 5. MPF
        // 布局: [SOI][XMP APP1?][MPF APP2][Base其余]
        int app1Total = writeXmp ? 2 + 2 + xmp.Length : 0;      // marker + length + data
        int mpfDataLen = 86;                                   // BuildMpf 固定长度
        int app2Total = 2 + 2 + mpfDataLen;                    // marker + length + data
        int primarySize = baseHeaderLen + app1Total + app2Total + sosLen + baseScanLen;
        int gmAbsOffset = primarySize;
        int secondaryLen = gainMapFinal.Length;                // 含次图自带的 XMP 与 ISO 两段
        // 增益图偏移: 相对 MPF TIFF 头 'MM' 位置 (libultrahdr/AndroidX 标准语义)
        // libultrahdr: secondary_image_offset = primary_image_size - pos - 8
        //   pos = APP2 marker 位置; pos+8 = marker(2)+len(2)+'MPF\0'(4) = 'MM' 位置
        int mpfDataStart = baseHeaderLen + app1Total + 8;
        int gmOffset = gmAbsOffset - mpfDataStart;
        byte[] mpf = BuildMpf(gmOffset, secondaryLen, primarySize);

        // 6. 组装
        using var ms = new MemoryStream();
        // Base: SOI + [XMP] + MPF + 其余 (DQT/SOF/DHT/SOS/扫描/EOI)
        ms.Write(baseJpeg, 0, baseHeaderLen);                  // 含 SOI
        if (writeXmp) WriteAppSegment(ms, 0xE1, xmp);          // APP1 XMP
        WriteAppSegment(ms, 0xE2, mpf);                        // APP2 MPF
        ms.Write(baseJpeg, sosIndex, baseJpeg.Length - sosIndex); // SOS 起含 EOI
        // 增益图: 完整 (已含 SOI + [XMP] + ISO + 数据 + EOI)
        ms.Write(gainMapFinal, 0, gainMapFinal.Length);

        File.WriteAllBytes(outputPath, ms.ToArray());
        return true;
    }

    private static void WriteAppSegment(Stream s, byte marker, byte[] payload)
    {
        // JPEG 段长字段只有 16 位 ⇒ payload 上限 65533。当前 XMP(~1.2KB)/MPF(86B) 远小于此，
        // 但旧代码无任何防御：将来扩字段会**静默写出坏段**（长度回绕），比抛错难查得多 ⇒ 显式断言。
        if (payload.Length + 2 > 0xFFFF)
            throw new InvalidOperationException(
                $"JPEG APP segment 过长：{payload.Length + 2} > 65535（段长字段只有 16 位）");

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
        while (i + 1 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            byte m = jpeg[i + 1];
            if (m == 0xFF) { i++; continue; }                        // 0xFF 填充字节
            if (m == 0x00) { i += 2; continue; }                     // 字节填充（紧随 0xFF 的 0x00）
            if (m == 0xD8) { i += 2; continue; }                     // SOI（异常嵌套，跳过）
            // ⚠️ **独立标记（无 16 位长度字段）**：TEM(0x01) 与 RSTn(0xD0–0xD7)。
            //    旧实现把它们后面两个字节当成段长 ⇒ 跳位错乱 ⇒ 在正常的渐进/带 restart 标记的
            //    JPEG 上找不到 SOS 而返回 -1（该失败又被上层静默成"仅写 Base"）。
            if (m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
            if (m == 0xDA) return i;                                 // SOS
            if (i + 3 >= jpeg.Length) return -1;                     // 截断
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
    /// 底图始终 SDR（BaseRenditionIsHDR=False）⇒ 增益为上映射：
    ///   gainMapMin = 0、gainMapMax = +log2(headroom)；
    ///   baseHdrHeadroom = log2(1.0) = 0、alternateHdrHeadroom = log2(headroom)。
    /// ⚠️ 分母必须与 N = log2×100 匹配（旧写法负值时用分母 1 → 第三方解出 -230而非 -2.3）。
    /// </summary>
    /// <remarks>
    /// 可见性为 <c>internal</c>：<see cref="IsoBmffGainMapWriter.BuildTmapPayload"/> 需要把同一份
    /// ISO 21496-1 载荷（JPEG APP2 与 ISO-BMFF tmap **逐字节同构**）用于 AVIF 出口 —— 单一真值，
    /// 不允许在写出器里再写一份（两份必然漂移）。
    /// </remarks>
    internal static byte[] BuildIso21496Metadata(bool multiChannel, float maxLog2Gain, float minLog2Gain = 0f,
        float? hdrCapacityLog2 = null)
        => BuildIso21496Metadata(multiChannel ? 3 : 1, maxLog2Gain, minLog2Gain, hdrCapacityLog2);

    /// <summary>
    /// 按**声明的参数集数**（1 或 3）构建 ISO 21496-1 载荷。
    /// <para>
    /// F-4/F-8（2026-10-11）：bit7 的语义按参照取「**参数集数**」而非「图像平面数」
    /// （libultrahdr gainmapmetadata.cpp:127-135 写的是
    /// <code>channelCount = allChannelsIdentical() ? 1u : 3u</code>，并自陈规范歧义；
    /// 读回 <code>channelCount = ((flags &amp; kIsMultiChannelMask) != 0) * 2 + 1</code> :221）。
    /// 但**交付的平面数与声明必须成套**：声明 3 参数集 ⇒ 数据必须真是逐通道（JPEG 侧 4:4:4 三分量、
    /// AVIF 侧 3 平面）；灰度件要么声明 1，要么把参数**复制成套**（libavif gainmap.c:743-752 的
    /// <code>gainMapMinLog2[singleChannel ? 0 : c]</code> 就是这一手）。调用方负责别写出
    /// 「声明 3 而数据单通道/4:2:0」的组合（写出器 <see cref="IsoBmffGainMapWriter.Build"/> 现在会拦）。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">declaredChannels 不是 1 或 3。</exception>
    internal static byte[] BuildIso21496Metadata(int declaredChannels, float maxLog2Gain, float minLog2Gain = 0f,
        float? hdrCapacityLog2 = null)
    {
        if (declaredChannels != 1 && declaredChannels != 3)
            throw new ArgumentOutOfRangeException(nameof(declaredChannels), declaredChannels,
                "ISO 21496-1 的通道数只能是 1（单参数集）或 3（逐通道参数集）");
        // log2 值用精确分数: log2 × 100 / 100
        int gainLog2N = (int)MathF.Round(maxLog2Gain * 100f);
        const int Log2D = 100;
        const int gammaN = 1, gammaD = 1;
        // F-5：这里写的分数 = 前向施加的那对 offset（KOffsetSdrN/D、KOffsetHdrN/D，同一个声明处），
        //       不许在此另写常量 ⇒ 「声明==使用」由符号本身保证。
        // 增益区间（log2）：默认 [0, +max]（底图由本编码器自造 Reinhard ⇒ 只会上映射）；
        // 当调用方**原样保留源底图**时，源声明的 <1 暗部端点会给出 minLog2Gain<0 ⇒ 双侧区间。
        int gainMinN = (int)MathF.Round(minLog2Gain * 100f);
        int gainMaxN = gainLog2N;
        // base/alternate headroom（均为 log2）：SDR 底 → base = log2(1.0) = 0，alternate = 显示容量。
        // ⚠ 显示容量与**增益量程**是两件事（ISO 21496-1 同名字段本就分开）：
        //   两图合成时量程按实测写（可能 > 或 < peak/203），容量仍按峰值声明。
        const int baseHeadroomN = 0, baseHeadroomD = 1;
        int alternateHeadroomN = (int)MathF.Round((hdrCapacityLog2 ?? maxLog2Gain) * 100f);
        const int alternateHeadroomD = Log2D;

        int channels = declaredChannels;
        byte flags = 0;
        if (channels == 3) flags |= 0x80;   // multi-channel（3 组参数，见上面 F-4 注释）
        flags |= 0x40;                      // useBaseColorSpace (增益图在 base 色彩空间)

        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        WriteBe16(bw, 0);                   // min_version
        WriteBe16(bw, 0);                   // writer_version
        bw.Write(flags);

        // baseHdrHeadroom / alternateHdrHeadroom (非 common denominator 模式)
        WriteBe32Bytes(bw, unchecked((uint)baseHeadroomN)); WriteBe32Bytes(bw, (uint)baseHeadroomD);
        WriteBe32Bytes(bw, unchecked((uint)alternateHeadroomN)); WriteBe32Bytes(bw, (uint)alternateHeadroomD);

        // 每通道: gainMapMin, gainMapMax, gamma, baseOffset, alternateOffset
        for (int c = 0; c < channels; c++)
        {
            WriteBe32Bytes(bw, unchecked((uint)gainMinN)); WriteBe32Bytes(bw, (uint)Log2D);
            WriteBe32Bytes(bw, unchecked((uint)gainMaxN)); WriteBe32Bytes(bw, (uint)Log2D);
            WriteBe32Bytes(bw, (uint)gammaN); WriteBe32Bytes(bw, (uint)gammaD);
            WriteBe32Bytes(bw, unchecked((uint)KOffsetSdrN)); WriteBe32Bytes(bw, (uint)KOffsetSdrD);
            WriteBe32Bytes(bw, unchecked((uint)KOffsetHdrN)); WriteBe32Bytes(bw, (uint)KOffsetHdrD);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// 构建 hdrgm XMP 元数据 (APP1 段数据, 含命名空间)。
    /// 对齐 libultrahdr generateXmpForPrimaryImage: GainMap item 写 Item:Length。
    /// 底图始终 SDR ⇒ BaseRenditionIsHDR=False、GainMapMin=0、GainMapMax=+log2(headroom)，
    /// 与 ISO 二进制声明保持一致（两者不一致时本软件解码以 ISO 为准并告警）。
    /// 底图色域（sRGB / Rec.2020 SDR）由底图自己的 ICC 描述，XMP 不携带 primaries 字段。
    /// </summary>
    /// <summary>hdrgm 规范九项的**取值与格式唯一真值**。底图的元素形式与次图的属性形式都从这里取数：
    /// 分两处就地格式化就是第二副本，一旦漂移，按底图读的消费者与按次图读的消费者会拿到不同的增益量程
    ///（比"缺字段"难查得多，本仓为 JXL distance 的五份副本付过代价）。</summary>
    private readonly record struct HdrgmNine(string Version, string GainMapMin, string GainMapMax,
        string Gamma, string OffsetSdr, string OffsetHdr, string HdrCapacityMin, string HdrCapacityMax,
        string BaseRenditionIsHdr);

    private static HdrgmNine FormatHdrgmNine(float maxLog2Gain, float minLog2Gain, float hdrCapacityLog2)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new HdrgmNine(
            "1.0",
            minLog2Gain.ToString("0.00", inv),
            maxLog2Gain.ToString("0.00", inv),
            // gamma 恒 1.0：施加侧从未按 gamma 变形（ readonly 常量与 ISO 载荷同值）
            "1.0",
            // F-5：声明的 offset 就是前向施加的那两个 readonly（单一声明处 ⇒「声明==使用」不可能漂移）
            KOffsetSdr.ToString("0.000000", inv),
            KOffsetHdr.ToString("0.000000", inv),
            // HDRCapacityMin 恒 0：底图就是 SDR 白点，与参考实现一致
            "0",
            hdrCapacityLog2.ToString("0.00", inv),
            "False");
    }

    private static byte[] BuildXmpMetadata(int baseW, int baseH, int gmW, int gmH, bool multiChannel,
        float maxLog2Gain, float hdrCapacityLog2, long gainMapLength, float minLog2Gain = 0f)
    {
        // ⚠ GainMapMax = **量程**（量化区间上沿，可与 peak/203 不同）；HDRCapacityMax = 显示容量。
        //    两者分开后，两图合成的暗底图（需要 > peak/203 的增益）不再被写成错误容量。
        // 下沿随 APP2 一起声明：XMP 与 ISO 二进制**必须同源**，否则第三方按 XMP 读会以为无下映射
        //（`hdrgm:GainMapMin` / `HDRCapacityMin` 与 `gainMapMin` / `baseHdrHeadroom` 同一套数）。
        var nine = FormatHdrgmNine(maxLog2Gain, minLog2Gain, hdrCapacityLog2);
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
            "<hdrgm:Version>" + nine.Version + "</hdrgm:Version>" +
            "<hdrgm:UseBaseRenderingColorSpace>True</hdrgm:UseBaseRenderingColorSpace>" +
            // AndroidX JpegImageCodec 读法：声明两幅图的「逻辑尺寸」与比例（解码尺寸可能因宏块对齐更大，
            // 增益图坐标必须按逻辑尺寸归一，否则边缘增益错位）。
            $"<hdrgm:MapPrimary>{baseW}x{baseH}</hdrgm:MapPrimary>" +
            $"<hdrgm:MapSecondary>{gmW}x{gmH}</hdrgm:MapSecondary>" +
            "<hdrgm:MapImageRatio>1.0</hdrgm:MapImageRatio>" +
            "<hdrgm:BaseRenditionIsHDR>" + nine.BaseRenditionIsHdr + "</hdrgm:BaseRenditionIsHDR>" +
            $"<hdrgm:GainMapMin>{nine.GainMapMin}</hdrgm:GainMapMin>" +
            $"<hdrgm:GainMapMax>{nine.GainMapMax}</hdrgm:GainMapMax>" +
            $"<hdrgm:Gamma>{nine.Gamma}</hdrgm:Gamma>" +
            $"<hdrgm:OffsetSDR>{nine.OffsetSdr}</hdrgm:OffsetSDR>" +
            $"<hdrgm:OffsetHDR>{nine.OffsetHdr}</hdrgm:OffsetHDR>" +
            "<hdrgm:HDRCapacityMin>" + nine.HdrCapacityMin + "</hdrgm:HDRCapacityMin>" +
            $"<hdrgm:HDRCapacityMax>{nine.HdrCapacityMax}</hdrgm:HDRCapacityMax>" +
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

    /// <summary>次图（增益图）自己的 hdrgm XMP —— **属性形式**，字段与顺序照参考实现
    ///（`generateXmpForSecondaryImage()`，jpegrutils.cpp:915-940）。参考读手只认属性形式，
    /// 数值与底图那份同源于 <see cref="FormatHdrgmNine"/>。</summary>
    private static byte[] BuildSecondaryXmpMetadata(float maxLog2Gain, float minLog2Gain,
        float hdrCapacityLog2)
    {
        var nine = FormatHdrgmNine(maxLog2Gain, minLog2Gain, hdrCapacityLog2);
        string xmp =
            "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999-02-22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\"" +
            $" hdrgm:Version=\"{nine.Version}\"" +
            $" hdrgm:GainMapMin=\"{nine.GainMapMin}\"" +
            $" hdrgm:GainMapMax=\"{nine.GainMapMax}\"" +
            $" hdrgm:Gamma=\"{nine.Gamma}\"" +
            $" hdrgm:OffsetSDR=\"{nine.OffsetSdr}\"" +
            $" hdrgm:OffsetHDR=\"{nine.OffsetHdr}\"" +
            $" hdrgm:HDRCapacityMin=\"{nine.HdrCapacityMin}\"" +
            $" hdrgm:HDRCapacityMax=\"{nine.HdrCapacityMax}\"" +
            $" hdrgm:BaseRenditionIsHDR=\"{nine.BaseRenditionIsHdr}\"/>" +
            "</rdf:RDF></x:xmpmeta>" +
            "<?xpacket end=\"w\"?>";
        var ns = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        var xmpBytes = Encoding.UTF8.GetBytes(xmp);
        var payload = new byte[ns.Length + xmpBytes.Length];
        Array.Copy(ns, 0, payload, 0, ns.Length);
        Array.Copy(xmpBytes, 0, payload, ns.Length, xmpBytes.Length);
        return payload;
    }

    /// <summary>把 APP1 XMP 插到**次图** SOI 之后（与底图同一命名空间前缀、同一写段器）。
    /// 位置只在 SOI 与 SOS 之间才有意义，读者按 marker 命名空间取段，不靠位置。</summary>
    private static byte[] InsertXmpIntoImage(byte[] jpeg, byte[] xmpPayload)
    {
        var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);                       // SOI
        WriteAppSegment(ms, 0xE1, xmpPayload);      // APP1 XMP（hdrgm 属性形式）
        ms.Write(jpeg, 2, jpeg.Length - 2);         // 其余（可能已含 APP2 ISO）
        return ms.ToArray();
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
