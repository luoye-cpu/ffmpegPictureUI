using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>抖动模式。</summary>
public enum DitherMode { None, Ordered4x4 }

/// <summary>
/// 一次像素变换的完整描述（引擎 L4 输入）。由 <see cref="ColorMappingEngine.Plan"/> 产出，
/// 内核本身不做任何色彩决策 —— 决策只有一处（大脑），执行可以有很多处（手）。
/// </summary>
public sealed class TransformSpec
{
    public TransferCurve SrcCurve;
    public TransferCurve DstCurve;
    /// <summary>线性域 RGB→RGB 矩阵（9 项行主序）；null = 恒等。</summary>
    public double[]? Matrix;
    /// <summary>
    /// 线性域亮度缩放：把源线性值乘以此系数后再处理。
    /// 用途：把 "1.0 = 源峰值" 与 "1.0 = 参考白" 两种约定互相换算（GainMap / HDR 归一需要）。
    /// 1.0 = 不缩放。
    /// </summary>
    public double LinearScale = 1.0;
    /// <summary>
    /// **编码前**的目标域换算系数（与 <see cref="LinearScale"/> 成对）：把内核的中间工作域
    /// （1.0 = 参考白，见 <see cref="ToneMapParams"/> 的值域约定）换到目标曲线的归一域
    /// （1.0 = 目标的 NitsOfLinearOne）。1.0 = 不缩放。
    /// <para>
    /// 缺了它 ⇒ SDR→PQ 把 203nits 的白编码成 10000nits（**过曝 49×**）、PQ↔HLG 亮度差 **10×**、
    /// 无 tonemap 的 PQ→SDR 整体**暗 49×**。
    /// </para>
    /// </summary>
    public double EncodeScale = 1.0;
    public DitherMode Dither = DitherMode.None;
    /// <summary>编码前是否把线性值钳到 [0,1]（SDR 输出 true；HDR 中间过程 false）。</summary>
    public bool ClampLinearBeforeEncode = true;
    /// <summary>
    /// 色调映射（HDR→SDR）。null/非激活 = 完全不动值域。
    /// 作用于 EOTF+LinearScale 之后、矩阵之前（此时线性值可 >1，编码域表驱动快路径不适用）。
    /// </summary>
    public ToneMapParams? Tone;

    /// <summary>
    /// 色域映射（GMO，广→窄色域**压缩**而非硬裁切）。
    /// true = 在 **3×3 矩阵之后、钳位之前**对**目标色域**的线性 RGB 施加
    /// <see cref="ColorKernels.GamutMapScalar"/>（libjxl 标量参考的逐行移植）。
    /// <para>⚠ 顺序是正确性的核心，两条都不能挪：</para>
    /// <list type="bullet">
    ///   <item><b>必须在矩阵之后</b>：算子要的是**目标色域**的线性 RGB 与**目标**的亮度系数；
    ///         矩阵之前仍是源色域坐标，判据（哪个分量越界）与"同亮度灰"都不是同一个空间。</item>
    ///   <item><b>必须在钳位之前</b>：钳位会把越界分量裁到 0/1，此后算子无从知道"越了多少"，
    ///         只剩硬裁切 —— 那正是本算子要取代的行为。</item>
    /// </list>
    /// </summary>
    public bool GamutMap;
    /// <summary>
    /// 色域映射的保饱和度（0..1）：0 = 优先保亮度（越界色更灰），1 = 优先保饱和度（更暗）。
    /// libjxl 内部 CMS 路径取 0.3（<c>jxl_cms_internal.h</c>），本项目同值。
    /// </summary>
    public double GamutMapPreserveSaturation = 0.3;
    /// <summary>
    /// **目标色域**的亮度系数 [Yr, Yg, Yb]（= 目标 RGB→XYZ 矩阵的 **Y 行**，行主序下标 3/4/5）。
    /// null 或长度 ≠ 3 ⇒ <see cref="GamutMap"/> 不生效（**不猜**系数）。
    /// </summary>
    public double[]? DstLuminances;

    /// <summary>色域映射是否真的可执行（开关打开**且**目标亮度系数可用）。</summary>
    public bool GamutMapActive => GamutMap && DstLuminances is { Length: 3 };

    /// <summary>
    /// HLG 是 scene-referred：曲线 EOTF⁻¹ 解码得到的是【场景相对线性光】(0-1)，
    /// 需经 BT.2100 OOTF（<see cref="Bt2100Ootf"/>）转为显示线性光；PQ 解码直接是显示光，无需此步。
    /// true = 源侧需在解码后施加正向 OOTF（场景光 → 显示光）。
    /// </summary>
    public bool SrcIsHlgScene;
    /// <summary>源侧系统伽马 γ（由名义峰值 L_W 经 <see cref="Bt2100Ootf.SystemGamma"/> 求得）。</summary>
    public double SrcHlgSystemGamma;
    /// <summary>true = 目标侧需在编码前施加逆向 OOTF（显示光 → 场景光）。</summary>
    public bool DstIsHlgScene;
    /// <summary>目标侧系统伽马 γ。</summary>
    public double DstHlgSystemGamma;

    /// <summary>曲线与矩阵都无变化 → 无需处理（调用方应直接跳过内核）。</summary>
    public bool IsIdentity => Matrix == null && SrcCurve.Equivalent(DstCurve)
        && Math.Abs(LinearScale - 1.0) < 1e-12 && Math.Abs(EncodeScale - 1.0) < 1e-12
        && (Tone == null || !Tone.Active)
        // 色域映射是一次真实的像素改动 ⇒ 开着就绝不能判为 identity（否则调用方会整趟跳过内核）
        && !GamutMapActive;
}

/// <summary>
/// 色域映射（GMO）算子参数：**目标色域**的亮度系数 + 保饱和度。
/// 由 <see cref="TransformSpec.DstLuminances"/> / <see cref="TransformSpec.GamutMapPreserveSaturation"/>
/// 换算而来（每个内核入口换算一次，热循环里只读常量）。
/// </summary>
public readonly struct GamutMapParams
{
    /// <summary>目标色域红原色的亮度系数（RGB→XYZ 的 Y 行第 0 项）。</summary>
    public readonly float Yr;
    /// <summary>目标色域绿原色的亮度系数（Y 行第 1 项）。</summary>
    public readonly float Yg;
    /// <summary>目标色域蓝原色的亮度系数（Y 行第 2 项）。</summary>
    public readonly float Yb;
    /// <summary>保饱和度（0..1）。</summary>
    public readonly float PreserveSaturation;

    public GamutMapParams(double yr, double yg, double yb, double preserveSaturation)
    {
        Yr = (float)yr; Yg = (float)yg; Yb = (float)yb;
        PreserveSaturation = (float)preserveSaturation;
    }

    /// <summary>系数是否可用：三项均有限（NaN/∞ 会污染整幅图 ⇒ 宁可退回裁切）。</summary>
    public bool IsValid => float.IsFinite(Yr) && float.IsFinite(Yg) && float.IsFinite(Yb);
}

/// <summary>
/// 色彩映射执行内核（H1，进程内）。融合单趟遍历：解码 → 线性域矩阵 → 编码 → 量化/抖动。
///
/// 精度与带宽策略（方案 §3.2/§3.3）：
///  • 中间量一律 float（线性域），矩阵系数降为 float 常量；
///  • 8-bit 源走 256 项表（L1 常驻）；16-bit 源走解析式 pow（避免 256KB 大表打爆缓存）；
///  • 映射阶段**不钳位**（矩阵混色允许负值/>1），只在编码前钳位；
///  • 抖动默认有序 4x4（确定性，便于逐位比对与回归）。
/// </summary>
public static class ColorKernels
{
    // 4x4 Bayer 有序抖动矩阵（原始阈值 ∈ [0,1)，满幅 1 LSB；用前必须减去 BayerCenter 才居中）
    private static readonly float[] Bayer4 =
    {
        0f / 16, 8f / 16, 2f / 16, 10f / 16,
       12f / 16, 4f / 16, 14f / 16,  6f / 16,
        3f / 16, 11f / 16, 1f / 16,  9f / 16,
       15f / 16, 7f / 16, 13f / 16,  5f / 16,
    };

    /// <summary>
    /// <see cref="Bayer4"/> 的**均值** = (0+1+…+15)/16 = 7.5/16 = 0.46875。抖动量必须减这个值才真正居中：
    /// t = Bayer4[p] − BayerCenter = (2p − 15)/32 ⇒ t ∈ [−0.46875, +0.46875]，均值 0，且 |t| &lt; 0.5。
    /// （表内 n/16 与 7.5/16 都是 2 的幂的商 ⇒ float 下逐值精确，t 无附加舍入误差。）
    /// <para>⚠ 旧写法减 <c>0.5f/4f</c>（= 2/16）⇒ t ∈ [−2/16, +13/16]、均值 +5.5/16 = <b>+0.344 LSB</b>，
    /// 16 个相位里有 6 个（p ≥ 10 ⇒ t ≥ 0.5）会把**本可 1:1 精确表示**的码值抬 +1：8-bit 源经
    /// <c>format=rgb48le</c> 升位后码值恒为 257 的整数倍，<see cref="EncodeTo8"/> 里 code16/65535×255
    /// 恰为整数 ⇒ 抖动纯粹是"花钱买损失"（纯色与平滑渐变上长出 Bayer 斑）。</para>
    /// <para>居中后对任意精确可表示（整数）码值 v：Round(v + t) == v 对**全部 16 个相位**成立 ——
    /// 离最近的 .5 进位边界还有 0.5 − 0.46875 = 0.03125 LSB 余量，远大于 double 中间量的 ~1e-13 误差。</para>
    /// <para>两条 8-bit 出口（<see cref="TransformRgba8"/> 与 <see cref="TransformRgb48leToRgb8"/>）共用此常量。</para>
    /// </summary>
    private const float BayerCenter = 7.5f / 16f;

    /// <summary>由系统伽马反解名义峰值 L_W：γ = 1.2 + 0.42·Log10(L_W/1000) 的代数逆。</summary>
    private static double NitsOfGamma(double gamma)
        => gamma <= 0 ? Bt2100Ootf.NominalPeakNits
           : Bt2100Ootf.NominalPeakNits * Math.Pow(10.0, (gamma - Bt2100Ootf.GammaAtNominal) / 0.42);

    // ═══════════════════════════════════════════════════════════════════
    //  线性域 float 路径（RGBA 交错，alpha 不动）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>就地：编码值 float RGBA → 线性（含可选亮度缩放）。负值保留（HDR/矩阵混色需要）。</summary>
    public static void DecodeToLinearInPlace(Span<float> rgba, in TransformSpec spec, float[]? eotfTable8 = null)
    {
        var cv = spec.SrcCurve;
        double scale = spec.LinearScale;
        bool scaleOn = Math.Abs(scale - 1.0) > 1e-12;
        if (cv.Kind == TransferCurve.CurveKind.Linear && !scaleOn) return;
        // HLG 是 scene-referred：场景光 → 显示光必须在 LinearScale **之前**做
        // （α=L_W 使结果为 nits 绝对值，且 Apply 对输入缩放敏感）。见 TransformRgb48leCore 的同一说明。
        bool srcHlg = spec.SrcIsHlgScene;
        double srcHlgPeak = srcHlg ? NitsOfGamma(spec.SrcHlgSystemGamma) : 0;
        for (int i = 0; i + 4 <= rgba.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
                rgba[i + c] = (float)cv.ToLinear(rgba[i + c]);
            if (srcHlg)
            {
                double r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                Bt2100Ootf.Apply(ref r, ref g, ref b, srcHlgPeak);
                rgba[i] = (float)(r / srcHlgPeak);
                rgba[i + 1] = (float)(g / srcHlgPeak);
                rgba[i + 2] = (float)(b / srcHlgPeak);
            }
            if (scaleOn)
                for (int c = 0; c < 3; c++) rgba[i + c] *= (float)scale;
        }
    }

    /// <summary>就地：线性 float RGBA → 编码值（可选钳位）。复用 <see cref="SimdPixelOps.FloatToSrgb8"/> 仅当目标为 sRGB。</summary>
    public static void EncodeFromLinearInPlace(Span<float> rgba, in TransformSpec spec)
    {
        var cv = spec.DstCurve;
        bool clamp = spec.ClampLinearBeforeEncode;
        double encScale = spec.EncodeScale;
        bool encScaleOn = Math.Abs(encScale - 1.0) > 1e-12;
        double dstNits = spec.DstIsHlgScene ? NitsOfGamma(spec.DstHlgSystemGamma) : 0;
        for (int i = 0; i + 4 <= rgba.Length; i += 4)
        {
            // 编码前的目标域换算：中间域(1.0=参考白) → 目标归一域（与主循环同序：GMO 之后、钳位之前）
            if (encScaleOn)
                for (int c = 0; c < 3; c++) rgba[i + c] *= (float)encScale;
            if (spec.DstIsHlgScene)
            {
                // HLG 编码的是场景光：先把线性显示光**换算到 nits 绝对值域**（ApplyInverse 要求 nits
                // 输入 —— 旧写法直接把归一值喂进去，少乘一个 dstNits ⇒ 整条 OOTF 错位），
                // 再经逆向 OOTF（显示光→场景光），输出即归一场景光。
                double r = rgba[i] * dstNits, g = rgba[i + 1] * dstNits, b = rgba[i + 2] * dstNits;
                Bt2100Ootf.ApplyInverse(ref r, ref g, ref b, dstNits);
                rgba[i] = (float)r; rgba[i + 1] = (float)g; rgba[i + 2] = (float)b;
            }
            for (int c = 0; c < 3; c++)
            {
                float v = rgba[i + c];
                if (clamp) v = Math.Clamp(v, 0f, 1f);
                rgba[i + c] = (float)cv.FromLinear(v);
            }
        }
    }

    /// <summary>就地：线性域 3×3 矩阵映射（复用 SimdPixelOps 的唯一实现，避免两份系数逻辑）。</summary>
    public static void ApplyMatrix(Span<float> rgba, double[]? m9)
    {
        if (m9 == null) return;
        SimdPixelOps.ApplyPrimariesMatrix(rgba, m9);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  色域映射（GMO）：广色域 → 窄色域的**压缩**（去饱和），不是硬裁切
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单像素色域映射（标量参考实现）—— 逐行移植 libjxl
    /// <c>lib/jxl/cms/tone_mapping.h</c> 的 <c>GamutMapScalar</c>（**不自行发明算法**）。
    ///
    /// <para><b>语义</b>（libjxl 原文注释）：把越界像素与"目标色域下**同亮度**的灰"混合，
    /// 混到刚好所有分量非负；若仍有分量 &gt;1 再按最大分量归一（<paramref name="p"/>.PreserveSaturation
    /// 决定"先保饱和"还是"先保亮度"的取舍权重）。</para>
    ///
    /// <para><b>输入域</b>：**目标色域**的线性 RGB（即 3×3 矩阵之后），可含负值/&gt;1；
    /// 输出保证落在 [0,1]（由 <c>max_clr = max(1, r, g, b)</c> 归一保证）。</para>
    ///
    /// <para><b>⚠ 必须保留的性质（门禁负控依据）</b>：分量全落 [0,1] 时本算子**严格 no-op**。推演：
    /// 亮度 <c>luminance = Yr·r+Yg·g+Yb·b</c> 是三个 [0,1] 分量的凸组合（Y 行和 = 1）⇒
    /// <c>luminance ∈ [min, max] ⊆ [0,1]</c>。于是
    /// <list type="number">
    ///   <item>对非最大分量：<c>val − luminance ≤ 0</c> ⇒ <c>gray_mix_saturation = max(0, val/(val−lum)) = 0</c>
    ///         （分子 ≥0、分母 &lt;0 ⇒ 比值 ≤0）；对最大分量：<c>val − luminance ≥ 0</c> ⇒ 该项**不更新**。
    ///         故 <c>gray_mix_saturation</c> 恒为 0。</item>
    ///   <item><c>gray_mix_luminance = max(0, (val−1)/(val−lum))</c>：<c>val ≤ 1</c> 且 <c>val−lum ≥ 0</c> ⇒ 比值 ≤0
    ///         ⇒ 恒为 0（<c>val == lum</c> 时 libjxl 用 1.0 做除数，得 <c>val−1 ≤ 0</c>，同样 ≤0）。</item>
    ///   <item>⇒ <c>gray_mix = clamp(0.3·(0−0)+0, 0, 1) = 0</c> ⇒ 三个分量按 <c>0·(lum−val)+val = val</c> **逐值不变**（IEEE：0·x 精确为 0）。</item>
    ///   <item>⇒ <c>max_clr = max(1, …) = 1</c>、<c>normalizer = 1</c> ⇒ 再乘 1 仍逐值不变。</item>
    /// </list>
    /// 全程无近似：不越界的像素开/关映射得到**逐字节相同**的结果。</para>
    /// </summary>
    public static void GamutMapScalar(ref float r, ref float g, ref float b, in GamutMapParams p)
    {
        float luminance = p.Yr * r + p.Yg * g + p.Yb * b;

        // ⚠ 顺序依赖（libjxl 原文如此，不可"优化"成两趟）：gray_mix_luminance 用的是**当轮**
        //   的 gray_mix_saturation，而 gray_mix_saturation 在同一轮里刚被第 idx 个分量更新过。
        float grayMixSaturation = 0f;
        float grayMixLuminance = 0f;
        for (int idx = 0; idx < 3; idx++)
        {
            float val = idx == 0 ? r : idx == 1 ? g : b;
            float valMinusGray = val - luminance;
            // libjxl 对 val == luminance 显式改用除数 1.0（避免除零）
            float invValMinusGray = 1f / (valMinusGray == 0f ? 1f : valMinusGray);
            float valOverValMinusGray = val * invValMinusGray;
            if (valMinusGray < 0f)
                grayMixSaturation = Math.Max(grayMixSaturation, valOverValMinusGray);
            grayMixLuminance = Math.Max(grayMixLuminance,
                valMinusGray <= 0f ? grayMixSaturation : (valOverValMinusGray - invValMinusGray));
        }

        float grayMix = Math.Clamp(p.PreserveSaturation * (grayMixSaturation - grayMixLuminance)
                                   + grayMixLuminance, 0f, 1f);
        r = grayMix * (luminance - r) + r;
        g = grayMix * (luminance - g) + g;
        b = grayMix * (luminance - b) + b;

        float maxClr = Math.Max(1f, Math.Max(r, Math.Max(g, b)));
        float normalizer = 1f / maxClr;
        r *= normalizer; g *= normalizer; b *= normalizer;
    }

    /// <summary>就地：对线性 float RGBA 缓冲做色域映射（alpha 不动）。spec 未启用时零开销直接返回。</summary>
    public static void GamutMapInPlace(Span<float> rgba, in TransformSpec spec)
    {
        if (!spec.GamutMapActive) return;
        var y = spec.DstLuminances!;
        var p = new GamutMapParams(y[0], y[1], y[2], spec.GamutMapPreserveSaturation);
        if (!p.IsValid) return;
        for (int i = 0; i + 4 <= rgba.Length; i += 4)
            GamutMapScalar(ref rgba[i], ref rgba[i + 1], ref rgba[i + 2], in p);
    }

    /// <summary>
    /// 完整一趟：编码值 → 线性 → 矩阵 → **色域映射** → 编码值。调用方给 RGBA float（0..1 编码域）。
    /// </summary>
    public static void TransformRgba(Span<float> rgba, in TransformSpec spec)
    {
        if (spec.IsIdentity) return;
        DecodeToLinearInPlace(rgba, spec);
        ApplyMatrix(rgba, spec.Matrix);
        GamutMapInPlace(rgba, spec);        // 矩阵之后、编码（含钳位）之前 —— 理由见 TransformSpec.GamutMap
        EncodeFromLinearInPlace(rgba, spec);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  8-bit BGRA/RGBA 路径（表加速；供预览与缩略图）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 8-bit RGBA → 8-bit RGBA（就地或跨缓冲）。源 EOTF 用 256 项表，目标 OETF 用 65536 项表。
    /// </summary>
    /// <param name="originX">本缓冲首像素在**整幅图**中的绝对 x（拖动相位用，保证 tile 切分结果逐字节一致）。</param>
    /// <param name="originY">同上，绝对 y。</param>
    /// <exception cref="NotSupportedException">
    /// <c>spec.ClampLinearBeforeEncode == false</c>：本内核的编码出口只有 8-bit 表这一条，
    /// 而表的定义域就是线性 [0,1]（见 <see cref="BuildEncodeLut16"/>）⇒ 做不到"保留线性余量"，
    /// 于是显式失败，不再静默钳位（与 <c>RawColorPipeline.TablesFor</c> 的"不钳位不建表"同一约定）。
    /// </exception>
    public static void TransformRgba8(ReadOnlySpan<byte> src, Span<byte> dst, in TransformSpec spec,
        float[] srcEotfTable, int width, int height, byte[]? dstLut16 = null, int originX = 0, int originY = 0)
    {
        // 先拒后算：无论表是调用方给的还是这里自建的，本路径都必然把线性值钳到 [0,1]。
        if (!spec.ClampLinearBeforeEncode)
            throw new NotSupportedException(
                "TransformRgba8 的编码出口是定义域 [0,1] 的 8-bit 查找表，无法兑现 ClampLinearBeforeEncode=false（保留线性余量）的承诺；不钳位请走解析式路径（TransformRgb48le / EncodeFromLinearInPlace）。");
        int n = Math.Min(src.Length, dst.Length);
        bool hasMtx = spec.Matrix != null;
        var m = spec.Matrix;
        float m0 = 1, m1 = 0, m2 = 0, m3 = 0, m4 = 1, m5 = 0, m6 = 0, m7 = 0, m8 = 1;
        if (hasMtx && m != null)
        {
            m0 = (float)m[0]; m1 = (float)m[1]; m2 = (float)m[2];
            m3 = (float)m[3]; m4 = (float)m[4]; m5 = (float)m[5];
            m6 = (float)m[6]; m7 = (float)m[7]; m8 = (float)m[8];
        }
        // 目标曲线 → 256×256 量化用查表（16bit 中间，一次生成，误差 ≤0.5/65535）
        byte[] encLut = dstLut16 ?? BuildEncodeLut16(spec.DstCurve, clamp: true);
        // 色域映射参数：每个缓冲换算一次，热循环只读常量（不启用时 gamut=false，循环里零开销）
        bool gamut = spec.GamutMapActive;
        var gm = default(GamutMapParams);
        if (gamut)
        {
            var y = spec.DstLuminances!;
            gm = new GamutMapParams(y[0], y[1], y[2], spec.GamutMapPreserveSaturation);
            gamut = gm.IsValid;
        }

        for (int i = 0; i + 4 <= n; i += 4)
        {
            float r = srcEotfTable[src[i + 0]];
            float g = srcEotfTable[src[i + 1]];
            float b = srcEotfTable[src[i + 2]];
            if (hasMtx)
            {
                float rr = m0 * r + m1 * g + m2 * b;
                float gg = m3 * r + m4 * g + m5 * b;
                float bb = m6 * r + m7 * g + m8 * b;
                r = rr; g = gg; b = bb;
            }
            // 色域映射：目标色域线性值上、量化/钳位之前（理由见 TransformSpec.GamutMap）
            if (gamut) GamutMapScalar(ref r, ref g, ref b, in gm);
            if (spec.Dither == DitherMode.Ordered4x4)
            {
                // 相位必须取**绝对像素坐标**：用缓冲内相对坐标会让每个 tile 的 y 相位从 0 重新开始，
                // tile 拼接处会出现可见条纹（也让“同图不同切分”不可比较）。
                int px = i >> 2;
                int ax = originX + (px % Math.Max(1, width));
                int ay = originY + (px / Math.Max(1, width));
                float t = Bayer4[(ax & 3) + ((ay & 3) << 2)] - BayerCenter;   // t ∈ [−7.5/16, +7.5/16]，均值 0
                // ⚠ 抖动量必须加在**码域**，不能加在线性域：sRGB OETF 趾段斜率 12.92 会把
                //   “±0.469 档”的意图放大成实测 **6 档**（旧写法 `r += t/255f` 改动 213/768 个码值，
                //   最低受损源码值 = 2 ⇒ 全在暗部，比不抖更差）。判据与取证：`contract` 的 C13、
                //   docs/COLOR_MATRIX_AUDIT_2026-09-24.md §4.3。
                // 这里走解析式 `EncodeTo8` 而**不是**查表：`encLut` 的值域已经是整数 8bit，
                //   拿不到分数码值 ⇒ 加不上 t；用同一函数也就与主力 8-bit 出口共用一副量化口径。
                dst[i + 0] = EncodeTo8(null, spec.DstCurve, r, true, t);
                dst[i + 1] = EncodeTo8(null, spec.DstCurve, g, true, t);
                dst[i + 2] = EncodeTo8(null, spec.DstCurve, b, true, t);
                dst[i + 3] = src[i + 3];
                continue;
            }
            dst[i + 0] = LookupEncode(encLut, r);
            dst[i + 1] = LookupEncode(encLut, g);
            dst[i + 2] = LookupEncode(encLut, b);
            dst[i + 3] = src[i + 3];
        }
    }

    /// <summary>
    /// 编码查找表：16-bit 线性索引（0..65535）→ 8-bit 编码值。表长 65536（64KB，L2 常驻）。
    /// 只在反复处理同一条曲线时生成一次（调用方缓存）。
    /// <para><b>定义域固定是线性 [0,1]</b>（索引 65535 ↔ 1.0），且值域是 <see cref="byte"/>
    /// —— 线性 &gt;1 无论钳不钳都只会落到 255，"保留 HDR 线性余量"在这条表路径上根本没有承载物。
    /// 因此 <paramref name="clamp"/> 为 false 时**直接拒绝建表**（以前是一枚死参数：两分支同式、
    /// 静默钳位，让调用方误以为承诺兑现了）。需要不钳位请走解析式快路径
    /// （16-bit 的 TransformRgb48le 重载在建表处就按 clamp 决定用表还是用 pow）。</para>
    /// </summary>
    /// <exception cref="NotSupportedException">clamp == false：本表做不到，交回调用方决定（失败优于装成功）。</exception>
    public static byte[] BuildEncodeLut16(TransferCurve dst, bool clamp)
    {
        if (!clamp)
            throw new NotSupportedException(
                "BuildEncodeLut16 的表定义域是线性 [0,1]、值域是 8-bit，无法表达 ClampLinearBeforeEncode=false 的线性余量。");
        var t = new byte[65536];
        for (int i = 0; i < 65536; i++)
        {
            double e = dst.FromLinear(i / 65535.0);   // i/65535 ∈ [0,1]，无需再钳（旧的恒等三元已删）
            t[i] = (byte)Math.Clamp(Math.Round(e * 255.0), 0, 255);
        }
        return t;
    }

    /// <summary>
    /// 线性 → 8-bit 码值（查表）。**本步必然钳位**：表的定义域就是线性 [0,1]，
    /// 所以这里不带 clamp 开关 —— 不钳位的 spec 走不到这里（<see cref="TransformRgba8"/> 入口已拒绝）。
    /// </summary>
    private static byte LookupEncode(byte[] lut16, float linear)
    {
        float v = Math.Clamp(linear, 0f, 1f);
        int idx = (int)(v * 65535f + 0.5f);
        return lut16[idx < 0 ? 0 : (idx > 65535 ? 65535 : idx)];
    }

    // ═══════════════════════════════════════════════════════════════════
    //  16-bit RGB 打包（rgb48le）流式路径 —— 统一管线的主力
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// rgb48le（每通道 16-bit LE）变换：**解析式参考路径**（逐通道 pow）。
    /// 作为表驱动快路径的对照基准与兜底（表不可用时使用）。
    /// </summary>
    public static void TransformRgb48le(ReadOnlySpan<byte> src, Span<byte> dst, in TransformSpec spec, int pixelCount)
        => TransformRgb48leCore(src, dst, spec, pixelCount, null, null);

    /// <summary>
    /// 16-bit 定义域的精确查找表。⚠️ 不是“近似”：输入量化到 16-bit 后只有 65536 个可能值，
    /// 把每个值的曲线结果预先算好是**无损的**（只受 f32/u16 存储舍入影响，≤ 0.5/65535）。
    /// 意义：消除每像素 6 次 pow（实测单核 8.8→预计 50+ MPix/s）。
    /// 仅适用于 ClampLinearBeforeEncode=true 的 SDR 输出；HDR（线性可 >1）仍走解析式。
    /// </summary>
    public sealed class CurveTables
    {
        /// <summary>编码值(0..65535) → 线性 f32。</summary>
        public float[] Eotf = Array.Empty<float>();
        /// <summary>线性(0..65535 量化) → 编码 u16。</summary>
        public ushort[] Oetf = Array.Empty<ushort>();
    }

    public static CurveTables BuildTables(TransferCurve curve)
    {
        var t = new CurveTables { Eotf = new float[65536], Oetf = new ushort[65536] };
        for (int i = 0; i < 65536; i++) t.Eotf[i] = (float)curve.ToLinear(i / 65535.0);
        for (int i = 0; i < 65536; i++)
            t.Oetf[i] = (ushort)Math.Clamp((long)Math.Round(curve.FromLinear(i / 65535.0) * 65535.0), 0, 65535);
        return t;
    }

    /// <summary>表驱动快路径（clamp 必需）；表为 null 时自动回退解析式。</summary>
    public static void TransformRgb48le(ReadOnlySpan<byte> src, Span<byte> dst, in TransformSpec spec,
        int pixelCount, CurveTables? srcTab, CurveTables? dstTab)
        => TransformRgb48leCore(src, dst, spec, pixelCount,
            spec.ClampLinearBeforeEncode ? srcTab : null, spec.ClampLinearBeforeEncode ? dstTab : null);

    private static void TransformRgb48leCore(ReadOnlySpan<byte> src, Span<byte> dst, in TransformSpec spec,
        int pixelCount, CurveTables? srcTab, CurveTables? dstTab)
    {
        var s = MemoryMarshal.Cast<byte, ushort>(src);
        var d = MemoryMarshal.Cast<byte, ushort>(dst);
        var srcC = spec.SrcCurve; var dstC = spec.DstCurve;
        double scale = spec.LinearScale;
        bool hasMtx = spec.Matrix != null;
        var m = spec.Matrix;
        float m0 = 1, m1 = 0, m2 = 0, m3 = 0, m4 = 1, m5 = 0, m6 = 0, m7 = 0, m8 = 1;
        if (hasMtx && m != null)
        {
            m0 = (float)m[0]; m1 = (float)m[1]; m2 = (float)m[2];
            m3 = (float)m[3]; m4 = (float)m[4]; m5 = (float)m[5];
            m6 = (float)m[6]; m7 = (float)m[7]; m8 = (float)m[8];
        }
        float[]? eotf = srcTab?.Eotf; ushort[]? oetf = dstTab?.Oetf;
        // 色调映射需要 [0,1] 以外的线性值域（headroom>1），而表快路径的 OETF 定义域就是 [0,1]
        // → 一旦启用 tonemap，强制走解析式路径（HDR 任务不是吞名大户，牺牲这点速度是对的）。
        bool toneOn = spec.Tone != null && spec.Tone.Active;
        var tone = spec.Tone;
        // HLG 源需在解码后施加 OOTF（查表表达不了）⇒ 有 HLG 源时禁用表快路径
        // 色域映射要在**矩阵之后、钳位之前**动值（见 TransformSpec.GamutMap），而表快路径把钳位
        // 隐式塞在 EncodeLerp 里 ⇒ 开了映射就必须走解析式，否则算子会被整段跳过（静默失效）。
        bool gamut = spec.GamutMapActive;
        var gm = default(GamutMapParams);
        if (gamut)
        {
            var y = spec.DstLuminances!;
            gm = new GamutMapParams(y[0], y[1], y[2], spec.GamutMapPreserveSaturation);
            gamut = gm.IsValid;
        }
        bool fast = eotf != null && oetf != null && Math.Abs(scale - 1.0) < 1e-12 && !toneOn
                    && !spec.SrcIsHlgScene && !gamut;
        bool clamp = spec.ClampLinearBeforeEncode;

        if (fast)
        {
            // 热路径：3 次 f32 查表 + 3×3 乘加 + 3 次 u16 查表；零 pow、零除法。
            // （fast 仅在 ClampLinearBeforeEncode=true 时成立，因此线性值先钳到 [0,1] 再查表）
            var eF = eotf!; var oF = oetf!;
            for (int i = 0; i < pixelCount; i++)
            {
                int o = i * 3;
                float r = eF[s[o + 0]], g = eF[s[o + 1]], b = eF[s[o + 2]];
                if (hasMtx)
                {
                    float rr = m0 * r + m1 * g + m2 * b;
                    float gg = m3 * r + m4 * g + m5 * b;
                    float bb = m6 * r + m7 * g + m8 * b;
                    r = rr; g = gg; b = bb;
                }
                d[o + 0] = EncodeLerp(oF, r);
                d[o + 1] = EncodeLerp(oF, g);
                d[o + 2] = EncodeLerp(oF, b);
            }
            return;
        }

        // HLG 是 scene-referred：解码得到的是**场景光**，必须先经 BT.2100 OOTF 换到**显示光**，
        // 再归一化回「1.0 = 源峰值」域，之后才做 LinearScale（换到「1.0 = SDR 白」）。
        // ⚠ 顺序不可颠倒：OOTF 的 α=L_W 使结果成为 nits 绝对值，且 Apply 对输入缩放敏感
        //   （输出随 s^γ 变化）⇒ 先乘 scale 会把 L_W 的语义搞错。
        bool srcHlg = spec.SrcIsHlgScene;
        double srcHlgPeak = srcHlg ? NitsOfGamma(spec.SrcHlgSystemGamma) : 0;
        // 编码侧（与解码侧对称）：目标域换算 + HLG 目标的逆向 OOTF。旧实现在 48-bit 主循环里
        // **两者都缺** ⇒ 目标为 PQ/HLG 时产出错曝光（过曝 49× / 差 10×）。
        double encScale = spec.EncodeScale;
        bool encScaleOn = Math.Abs(encScale - 1.0) > 1e-12;
        bool dstHlg = spec.DstIsHlgScene;
        double dstHlgPeak = dstHlg ? NitsOfGamma(spec.DstHlgSystemGamma) : 0;

        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 3;
            float r = (float)srcC.ToLinear(s[o + 0] / 65535.0);
            float g = (float)srcC.ToLinear(s[o + 1] / 65535.0);
            float b = (float)srcC.ToLinear(s[o + 2] / 65535.0);
            if (srcHlg)
            {
                double nr = r, ng = g, nb = b;
                Bt2100Ootf.Apply(ref nr, ref ng, ref nb, srcHlgPeak);
                r = (float)(nr / srcHlgPeak); g = (float)(ng / srcHlgPeak); b = (float)(nb / srcHlgPeak);
            }
            r *= (float)scale; g *= (float)scale; b *= (float)scale;
            if (toneOn && tone != null) ToneMap.ApplyRgb(ref r, ref g, ref b, tone, dstC);
            if (hasMtx)
            {
                float rr = m0 * r + m1 * g + m2 * b;
                float gg = m3 * r + m4 * g + m5 * b;
                float bb = m6 * r + m7 * g + m8 * b;
                r = rr; g = gg; b = bb;
            }
            // ⚠ 色域映射必须在这里：**矩阵之后**（此刻是目标色域的线性 RGB）、**钳位之前**
            //   （钳位一旦发生，越界信息就没了，只剩硬裁切）。详见 TransformSpec.GamutMap。
            if (gamut) GamutMapScalar(ref r, ref g, ref b, in gm);
            // ── 编码前的目标域换算（GMO 之后、钳位之前）：中间域(1.0=参考白) → 目标归一域 ──
            if (encScaleOn)
            {
                r *= (float)encScale; g *= (float)encScale; b *= (float)encScale;
            }
            if (dstHlg)
            {
                // HLG 编码的是**场景光**：先把目标域线性值换算成 nits 绝对值（ApplyInverse 要求
                // nits 输入 ⇒ 少乘 dstHlgPeak 会让整条 OOTF 错位），经逆向 OOTF（显示光→场景光）
                // 后再交回 HlgOetf。
                double nr = r * dstHlgPeak, ng = g * dstHlgPeak, nb = b * dstHlgPeak;
                Bt2100Ootf.ApplyInverse(ref nr, ref ng, ref nb, dstHlgPeak);
                r = (float)nr; g = (float)ng; b = (float)nb;
            }
            if (clamp)
            {
                r = Math.Clamp(r, 0f, 1f); g = Math.Clamp(g, 0f, 1f); b = Math.Clamp(b, 0f, 1f);
            }
            d[o + 0] = ToU16(dstC.FromLinear(r));
            d[o + 1] = ToU16(dstC.FromLinear(g));
            d[o + 2] = ToU16(dstC.FromLinear(b));
        }
    }

    /// <summary>
    /// 整帧**内容峰值**实测：线性域 max(R,G,B) 的最大值（已乘 <see cref="TransformSpec.LinearScale"/>，
    /// 因此结果就落在“1.0 = SDR 参考白”域里，调用方 ×SdrWhiteNits 即得 nits）。
    ///
    /// 口径为何长这样（不是随手选的）：
    ///  • 与 <see cref="ToneMap.ApplyRgb"/> 的压缩对象一致——它压的就是 max 通道，测错对象会让 headroom 与算子不对应；
    ///  • 与 MaxCLL 的定义一致（单像素最大光），而不是 luma/面积平均；
    ///  • **不做矩阵混色**：混色产生的越界值是色域不匹配的证据，不是“内容有多亮”的证据。
    /// 表可用时用表（无 pow），否则回退解析式；两条路径必须同值（由探针钉住）。
    /// </summary>
    public static double MeasurePeakLinearRgb48(ReadOnlySpan<byte> src, int pixelCount,
        in TransformSpec spec, CurveTables? srcTab)
    {
        var s = MemoryMarshal.Cast<byte, ushort>(src);
        float scale = (float)spec.LinearScale;
        double mx = 0;
        // HLG 的 OOTF 依赖**三元组**（Y_S = 0.2627R+0.678G+0.0593B），不是逐通道查表 ⇒ 表路径不可用。
        bool srcHlg = spec.SrcIsHlgScene;
        double srcHlgPeak = srcHlg ? NitsOfGamma(spec.SrcHlgSystemGamma) : 0;
        var eotf = srcHlg ? null : srcTab?.Eotf;
        if (eotf != null)
        {
            for (int i = 0, o = 0; i < pixelCount; i++, o += 3)
            {
                float v = Math.Max(eotf[s[o]], Math.Max(eotf[s[o + 1]], eotf[s[o + 2]]));
                if (v > mx) mx = v;
            }
        }
        else
        {
            var c = spec.SrcCurve;
            for (int i = 0, o = 0; i < pixelCount; i++, o += 3)
            {
                float r = (float)c.ToLinear(s[o] / 65535.0);
                float g = (float)c.ToLinear(s[o + 1] / 65535.0);
                float b = (float)c.ToLinear(s[o + 2] / 65535.0);
                if (srcHlg)
                {
                    double nr = r, ng = g, nb = b;
                    Bt2100Ootf.Apply(ref nr, ref ng, ref nb, srcHlgPeak);
                    r = (float)(nr / srcHlgPeak); g = (float)(ng / srcHlgPeak); b = (float)(nb / srcHlgPeak);
                }
                float v = Math.Max(r, Math.Max(g, b));
                if (v > mx) mx = v;
            }
        }
        return mx * scale;
    }

    /// <summary>
    /// rgb48le → **rgb24（8-bit）** 并带 1 LSB 有序抖动。用于 8-bit 目标：避免"16bit 变换后交给编码器盲降位"带来的轮频带。
    /// 拖动相位取**绝对像素坐标**（由 <paramref name="width"/> 与 <paramref name="originIndex"/> 推得），
    /// 因此行带切分/DOP 变化不会改变结果（由 selftest 的 tile 一致性断钉住）。
    /// </summary>
    public static void TransformRgb48leToRgb8(ReadOnlySpan<byte> src, Span<byte> dst, in TransformSpec spec,
        int pixelCount, CurveTables? srcTab, CurveTables? dstTab, int width, int originIndex = 0)
    {
        var s = MemoryMarshal.Cast<byte, ushort>(src);
        var m9 = spec.Matrix;
        float m0 = 1, m1 = 0, m2 = 0, m3 = 0, m4 = 1, m5 = 0, m6 = 0, m7 = 0, m8 = 1;
        if (m9 != null)
        {
            m0 = (float)m9[0]; m1 = (float)m9[1]; m2 = (float)m9[2];
            m3 = (float)m9[3]; m4 = (float)m9[4]; m5 = (float)m9[5];
            m6 = (float)m9[6]; m7 = (float)m9[7]; m8 = (float)m9[8];
        }
        var eF = srcTab?.Eotf; var oF = dstTab?.Oetf;
        // 契约：CurveTables 的定义域同样是线性 [0,1]（EncodeLerp 在两端饱和）⇒ 不钳位的 spec 必须由
        //   调用方送 null 表来回退解析式（现由 RawColorPipeline.TablesFor 把这条约定钉住），别指望本内核替你钳。
        bool tbl = eF != null && oF != null;
        bool clamp = spec.ClampLinearBeforeEncode;
        // 上面那条契约现在是**硬**的：靠注释守约已经被证明不可靠（同一形状的静默钳在这条链上出现过两次），
        //   所以"带着表却不钳位"这种组合直接失败，而不是等调用方记得传 null。
        if (tbl && !clamp)
            throw new NotSupportedException(
                "CurveTables 的定义域是线性 [0,1]，与 ClampLinearBeforeEncode=false 互斥 ⇒ 请送 null 表走解析式（与 TablesFor 同规则）");
        bool dither = spec.Dither == DitherMode.Ordered4x4;
        int w = Math.Max(1, width);
        // ⚠⚠ 2026-10-05 修（P0：8-bit 出口**静默丢弃**色调映射等四个算子）：
        //   本路径此前**只**读 `SrcCurve` + 矩阵 + `GamutMap` + `Clamp`，**从不读**
        //   `spec.Tone` / `spec.LinearScale` / `spec.EncodeScale` / `spec.SrcIsHlgScene`
        //   ⇒ 8-bit 出口（`-f gif`/`-f jpg`/`--bit-depth 8` 等**默认可达**路径）上：
        //     · HDR→SDR **不做色调映射**（高光截顶、整体偏亮约 57%）；
        //     · HLG 源**不做 BT.2100 OOTF**；
        //     · 源/目标 nits 换算（LinearScale/EncodeScale）**整段跳过**。
        //   实测（同一 HDR 源、`--bit-depth 8`）：`--color-tone-map` 取
        //   `hable` / `none` / `reinhard` 三者产物**逐字节相同（同一 SHA）**；
        //   而 `--bit-depth 16` 下 `reinhard` 明显不同 ⇒ 差异被隔离到本内核。
        //   更糟的是日志仍打「tonemap … 激活=是」（那行读的是 `spec.Tone`，不是内核真做了什么）
        //   ⇒ **既错又骗**。现按 16-bit 兄弟实现（`TransformRgb48leCore`）的**同一顺序**补齐四件事，
        //   顺序不可颠倒（OOTF 是 nits 绝对值 ⇒ 必须在 LinearScale 之前；tonemap 在缩放之后、
        //   矩阵之前；EncodeScale 在 GMO 之后、钳位之前）。
        double scale = spec.LinearScale;
        bool scaleOn = Math.Abs(scale - 1.0) > 1e-12;
        bool srcHlg = spec.SrcIsHlgScene;
        double srcHlgPeak = srcHlg ? NitsOfGamma(spec.SrcHlgSystemGamma) : 0;
        bool toneOn = spec.Tone != null && spec.Tone.Active;
        var tone = spec.Tone;
        double encScale = spec.EncodeScale;
        bool encScaleOn = Math.Abs(encScale - 1.0) > 1e-12;
        bool dstHlg = spec.DstIsHlgScene;
        double dstHlgPeak = dstHlg ? NitsOfGamma(spec.DstHlgSystemGamma) : 0;
        // ⚠ 表快路径的前提被破坏时必须回退解析式（与 16-bit 的 `fast` 判据同源）：
        //   表把「解码」与「编码」都换成了查表，而下面这几个算子（OOTF/tonemap/缩放）
        //   在表里表达不了 ⇒ 只要它们启用，就必须走解析式，否则又会静默跳过。
        if (tbl && (scaleOn || toneOn || encScaleOn || srcHlg || dstHlg))
        {
            eF = null; oF = null; tbl = false;
        }
        // 色域映射参数（本路径是 8-bit 产物的主力出口，必须与 16-bit 路径同算子同顺序）
        bool gamut = spec.GamutMapActive;
        var gm = default(GamutMapParams);
        if (gamut)
        {
            var y = spec.DstLuminances!;
            gm = new GamutMapParams(y[0], y[1], y[2], spec.GamutMapPreserveSaturation);
            gamut = gm.IsValid;
        }

        for (int i = 0, o = 0, p = 0; i < pixelCount; i++, o += 3, p += 3)
        {
            float r, g, b;
            if (tbl)
            {
                r = eF![s[o + 0]]; g = eF[s[o + 1]]; b = eF[s[o + 2]];
            }
            else
            {
                r = (float)spec.SrcCurve.ToLinear(s[o + 0] / 65535.0);
                g = (float)spec.SrcCurve.ToLinear(s[o + 1] / 65535.0);
                b = (float)spec.SrcCurve.ToLinear(s[o + 2] / 65535.0);
            }
            // ── 与 16-bit 主循环**同顺序**的四步（2026-10-05 补齐；此前整段缺失）──
            //  ① HLG 源：场景光 → 显示光（BT.2100 OOTF），再归一化回「1.0 = 源峰值」域。
            //     ⚠ 必须在 ② 之前：OOTF 的 α=L_W 使结果是 nits 绝对值，先缩放会搞错 L_W 语义。
            if (srcHlg)
            {
                double nr = r, ng = g, nb = b;
                Bt2100Ootf.Apply(ref nr, ref ng, ref nb, srcHlgPeak);
                r = (float)(nr / srcHlgPeak); g = (float)(ng / srcHlgPeak); b = (float)(nb / srcHlgPeak);
            }
            //  ② 源域 → 中间工作域（1.0 = SDR 参考白）
            if (scaleOn) { r *= (float)scale; g *= (float)scale; b *= (float)scale; }
            //  ③ 色调映射（在缩放之后、矩阵之前；GMO 需要的是目标域线性值，故 tonemap 先做）
            if (toneOn && tone != null) ToneMap.ApplyRgb(ref r, ref g, ref b, tone, spec.DstCurve);
            float rr = m0 * r + m1 * g + m2 * b;
            float gg = m3 * r + m4 * g + m5 * b;
            float bb = m6 * r + m7 * g + m8 * b;
            // 色域映射：矩阵之后（目标色域线性 RGB）、钳位之前（见 TransformSpec.GamutMap）
            if (gamut) GamutMapScalar(ref rr, ref gg, ref bb, in gm);
            //  ④ 中间域 → 目标归一域（GMO 之后、钳位之前，与 16-bit 同序）
            if (encScaleOn)
            {
                rr *= (float)encScale; gg *= (float)encScale; bb *= (float)encScale;
            }
            //  ⑤ HLG 目标：显示光 → 场景光（逆向 OOTF）
            if (dstHlg)
            {
                double nr = rr * dstHlgPeak, ng = gg * dstHlgPeak, nb = bb * dstHlgPeak;
                Bt2100Ootf.ApplyInverse(ref nr, ref ng, ref nb, dstHlgPeak);
                rr = (float)nr; gg = (float)ng; bb = (float)nb;
            }
            if (clamp)
            {
                rr = Math.Clamp(rr, 0f, 1f); gg = Math.Clamp(gg, 0f, 1f); bb = Math.Clamp(bb, 0f, 1f);
            }
            float t = 0f;
            if (dither)
            {
                int ax = (originIndex + i) % w, ay = (originIndex + i) / w;
                t = Bayer4[(ax & 3) + ((ay & 3) << 2)] - BayerCenter;   // 居中：t ∈ [−0.46875, +0.46875] LSB，均值 0
            }
            dst[p + 0] = EncodeTo8(oF, spec.DstCurve, rr, clamp, t);
            dst[p + 1] = EncodeTo8(oF, spec.DstCurve, gg, clamp, t);
            dst[p + 2] = EncodeTo8(oF, spec.DstCurve, bb, clamp, t);
        }
    }

    /// <summary>
    /// 线性 → 8-bit 码值。⚠ 解析式分支必须先换算到 **16-bit 码域**，再走统一的 /65535×255：
    /// 旧写法把 clamp 分支写成 <c>Math.Round(0..1)</c>（忘了乘 65535），中间调 0.6 被当成码 1，
    /// 整幅输出变为近黑。该缺陷**只在无表路径**暴露（有表时走 EncodeLerp），
    /// 所以只测表路径的内核断言抓不到 ⇒ selftest 现在要求“表路径 ≡ 解析路径”。
    /// </summary>
    private static byte EncodeTo8(ushort[]? oetf, TransferCurve curve, float linear, bool clamp, float t8)
    {
        double e = clamp ? Math.Clamp(curve.FromLinear(linear), 0.0, 1.0) : curve.FromLinear(linear);
        double code16 = oetf != null ? EncodeLerp(oetf, linear) : Math.Round(e * 65535.0);
        double v = code16 / 65535.0 * 255.0 + t8;
        return (byte)Math.Clamp((long)Math.Round(v), 0, 255);
    }

    /// <summary>gbrp16le（平面 G,B,R）→ 交错 rgb48le（同一缓冲内可原地不行，需两缓冲）。</summary>
    public static void PlanarGbr16ToRgbPacked16(ReadOnlySpan<ushort> gbr, Span<ushort> rgb, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            rgb[i * 3 + 0] = gbr[pixelCount * 2 + i];
            rgb[i * 3 + 1] = gbr[i];
            rgb[i * 3 + 2] = gbr[pixelCount + i];
        }
    }

    /// <summary>rgb48le → 平面 gbrp16le（供 ffmpeg 直接编码，避免二次转换）。</summary>
    public static void RgbPacked16ToPlanarGbr16(ReadOnlySpan<ushort> rgb, Span<ushort> gbr, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            gbr[i] = rgb[i * 3 + 1];
            gbr[pixelCount + i] = rgb[i * 3 + 2];
            gbr[pixelCount * 2 + i] = rgb[i * 3 + 0];
        }
    }

    private static ushort ToU16(double encoded)
        => (ushort)Math.Clamp((long)Math.Round(encoded * 65535.0), 0, 65535);

    /// <summary>线性值 → 表索引（钳到 [0,65535]）；JIT 会内联，避开 Math.Clamp 的类型转换开销。</summary>
    private static int ClampIdx(float linear)
    {
        int i = (int)(linear * 65535f + 0.5f);
        return i < 0 ? 0 : (i > 65535 ? 65535 : i);
    }

    /// <summary>
    /// OETF 查表 + 线性插值。
    /// ⚠️ 不能直接“把线性值量化成 1/65535 当索引”：sRGB/BT.709 趾段 OETF 斜率高达 12.92，
    /// 量化误差会被放大到 ~13 LSB（实测：表驱动与解析式最大差 6–17/65535）。
    /// 插值后误差降到曲线曲率量级（≪0.1 LSB），且仍零 pow。
    /// </summary>
    private static ushort EncodeLerp(ushort[] oetf, float linear)
    {
        float idxF = linear * 65535f;
        if (idxF <= 0f) return oetf[0];
        if (idxF >= 65535f) return oetf[65535];
        int i0 = (int)idxF;
        float fr = idxF - i0;
        int v = oetf[i0] + (int)((oetf[i0 + 1] - oetf[i0]) * fr + 0.5f);
        return (ushort)(v < 0 ? 0 : (v > 65535 ? 65535 : v));
    }

    /// <summary>
    /// 纯托管参考实现（供自测与"引擎 vs 自身表实现"一致性核对）：单像素 RGBA float。
    /// </summary>
    public static (float R, float G, float B) TransformPixelReference(float r, float g, float b, in TransformSpec spec)
    {
        var src = spec.SrcCurve; var dst = spec.DstCurve;
        double lr = src.ToLinear(r) * spec.LinearScale;
        double lg = src.ToLinear(g) * spec.LinearScale;
        double lb = src.ToLinear(b) * spec.LinearScale;
        if (spec.Matrix is { Length: 9 } m)
        {
            double rr = m[0] * lr + m[1] * lg + m[2] * lb;
            double rg = m[3] * lr + m[4] * lg + m[5] * lb;
            double rb = m[6] * lr + m[7] * lg + m[8] * lb;
            lr = rr; lg = rg; lb = rb;
        }
        // 色域映射：矩阵之后、钳位之前（与融合内核同序同类型 —— 用 float 走同一个 GamutMapScalar，
        // 保证"同一数学的两个入口不分叉"，否则参考实现会与内核差出算子量级的偏差）
        if (spec.GamutMapActive)
        {
            var y = spec.DstLuminances!;
            var gp = new GamutMapParams(y[0], y[1], y[2], spec.GamutMapPreserveSaturation);
            if (gp.IsValid)
            {
                float fr = (float)lr, fg = (float)lg, fb = (float)lb;
                GamutMapScalar(ref fr, ref fg, ref fb, in gp);
                lr = fr; lg = fg; lb = fb;
            }
        }
        // 编码前的目标域换算（与内核同序：GMO 之后、钳位之前）
        lr *= spec.EncodeScale; lg *= spec.EncodeScale; lb *= spec.EncodeScale;
        if (spec.DstIsHlgScene)
        {
            // 与主循环同式：先换到 nits 绝对值域，再逆向 OOTF（显示光→场景光）
            double dn = NitsOfGamma(spec.DstHlgSystemGamma);
            lr *= dn; lg *= dn; lb *= dn;
            Bt2100Ootf.ApplyInverse(ref lr, ref lg, ref lb, dn);
        }
        if (spec.ClampLinearBeforeEncode)
        {
            lr = Math.Clamp(lr, 0, 1); lg = Math.Clamp(lg, 0, 1); lb = Math.Clamp(lb, 0, 1);
        }
        return ((float)dst.FromLinear(lr), (float)dst.FromLinear(lg), (float)dst.FromLinear(lb));
    }
}
