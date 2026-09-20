using System;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 传递函数（曲线）的统一表达与实现 —— 色彩映射引擎的数学底座。
///
/// 规范形与 Google skcms / libultrahdr 的 <c>TransferFunction</c> **完全同构**（7 参数），
/// 便于三方互测与数值对照：
///
///   linear = c·x + f                    , |x| &lt;  d
///   linear = (a·x + b)^g + e            , |x| ≥  d
///
/// 该形式覆盖全部 SDR 曲线（sRGB / BT.709 / BT.470M(2.2) / BT.470BG(2.8) / ProPhoto(1.8+线性趾) /
/// ColorMatch / DCI-P3(2.6) / Adobe(2.2)），且**反函数有闭式解**（无需迭代，见 <see cref="FromLinear"/>）。
/// PQ 与 HLG 无法用该式表达，单独用 <see cref="CurveKind.Pq"/> / <see cref="CurveKind.Hlg"/> 的解析式，
/// 两者正反向同样都是闭式。
///
/// ⚠️ 设计约束（本项目的历史教训）：
/// 1. 曲线必须精确。绝不允许"1.8 用 sRGB 凑"这类近似替代 —— 那是中间调整体错。
///    不可精确表达时，调用方要么走本类（可表达任意上列曲线），要么放弃映射改携带 ICC。
/// 2. 所有锚点值都写进单元测试（ServiceProbe curve），改常数必须过锚点。
/// </summary>
public readonly struct TransferCurve
{
    public enum CurveKind : byte
    {
        /// <summary>7 参数规范形（含纯 gamma）。</summary>
        Parametric = 0,
        /// <summary>SMPTE ST 2084 / ITU-R BT.2100 PQ（绝对亮度，1.0 = 10000 nits）。</summary>
        Pq = 1,
        /// <summary>ITU-R BT.2100 HLG 系统级 OETF/EOTF（相对亮度，1.0 = 参考白）。</summary>
        Hlg = 2,
        /// <summary>线性（无变换）。</summary>
        Linear = 3,
        /// <summary>ICC <c>curv</c> LUT 型曲线（表无法用 7 参数式表达时）：编码轴等距采样→线性值。</summary>
        Table = 4,
    }

    public readonly CurveKind Kind;
    // 规范形参数（Kind=Parametric 时有效）
    public readonly double G, A, B, C, D, E, F;
    /// <summary>该曲线等价的 CICP transfer token；无法用 CICP 命名（1.8/2.6 等）时为 null。</summary>
    public readonly string? CicpToken;
    /// <summary>可读名（日志/诊断用）。</summary>
    public readonly string Name;
    /// <summary>Kind=Table 时的 LUT：下标=编码值等距采样，元素=归一化线性值（单调递增）。</summary>
    public readonly float[]? Lut;

    private TransferCurve(CurveKind kind, double g, double a, double b, double c, double d, double e, double f,
        string? cicp, string name)
    {
        Kind = kind; G = g; A = a; B = b; C = c; D = d; E = e; F = f;
        CicpToken = cicp; Name = name; Lut = null;
    }

    private TransferCurve(float[] lut, string name)
    {
        Kind = CurveKind.Table; G = 1; A = 1; B = 0; C = 0; D = 0; E = 0; F = 0;
        CicpToken = null; Name = name; Lut = lut;
    }

    /// <summary>
    /// 由 ICC <c>curv</c> 表（或任何等距编码轴采样）构造曲线。<paramref name="lut"/> 元素为归一化线性值。
    /// 非单调表（查表反演会歧义）→ 返回 null，由上层走携带 ICC 而不是映射。
    /// </summary>
    public static TransferCurve? FromTable(IReadOnlyList<float> lut, string name = "curv-LUT")
    {
        if (lut.Count < 2) return null;
        for (int i = 1; i < lut.Count; i++)
            if (lut[i] < lut[i - 1] - 1e-6f) return null;         // 非单调 → 不可逆
        var copy = new float[lut.Count];
        for (int i = 0; i < lut.Count; i++) copy[i] = Math.Clamp(lut[i], 0f, float.MaxValue);
        return new TransferCurve(copy, name);
    }

    /// <summary>
    /// 由 7 参数规范形直接构造曲线（供 ICC <c>para</c> functionType 0–4 解析使用）。
    /// 参数含义与 <see cref="ToLinear"/> 一致：linear = c·x+f (x&lt;d) 否则 (a·x+b)^g+e。
    /// </summary>
    public static TransferCurve FromParametric(double g, double a, double b, double c, double d, double e, double f,
        string name = "parametric")
        => new(CurveKind.Parametric, g, a, b, c, d, e, f, null, name);

    /// <summary>
    /// 目标曲线在 toe/幂段拐点处是否存在台阶（ITU 取整系数的固有性质）。
    /// 自测对这类曲线放宽容差时依赖此标记，而不是无条件放宽。
    /// </summary>
    public bool HasItuRoundingKink => Kind == CurveKind.Parametric && HasToeSegment
        && (CicpToken == "bt709" || CicpToken == "smpte240m");

    /// <summary>表长度（Kind≠Table 时为 0）。</summary>
    public int TableLength => Lut?.Length ?? 0;

    // ═══════════════════════════════════════════════════════════════════
    //  已注册曲线（数值来源见每条注释；锚点断言在 ServiceProbe curve）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>sRGB / IEC 61966-2-1：g=2.4, a=1/1.055, b=0.055/1.055, c=1/12.92, d=0.04045。</summary>
    public static readonly TransferCurve Srgb = new(CurveKind.Parametric,
        2.4, 1.0 / 1.055, 0.055 / 1.055, 1.0 / 12.92, 0.04045, 0.0, 0.0, "iec61966-2-1", "sRGB");

    /// <summary>
    /// BTS.709/BT.2020 SDR：α=1.099 β=0.099，γ=1/0.45，toe 斜率 4.5，拐点 d=0.08125。
    /// ⚠️ 该定义里线性段是幂段的**切线**（标准系数取整后不完全相切），在 code≈0.0813 处
    /// 留有 ~21 LSB（0.03%）的台阶——这是 ITU 定义本身的性质，lcms2/libzimg 同样保留，
    /// 8bit 下仅 0.26 LSB 不可见。影响：表插值与解析式在拐点附近可能选不同分支（实测差 ≤10 LSB），
    /// 自测对带台阶的曲线放宽到 24 LSB（见 ServiceProbe selftest）。
    /// </summary>
    public static readonly TransferCurve Bt709 = new(CurveKind.Parametric,
        1.0 / 0.45, 1.0 / 1.099, 0.099 / 1.099, 1.0 / 4.5, 0.08125, 0.0, 0.0, "bt709", "BT.709");

    /// <summary>BT.470-6 System M（gamma 2.2 纯幂）—— Adobe RGB (1998) 的传递函数。</summary>
    public static readonly TransferCurve Gamma22 = new(CurveKind.Parametric,
        2.2, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, "bt470m", "gamma2.2");

    /// <summary>BT.470-6 System B/G（gamma 2.8 纯幂）。</summary>
    public static readonly TransferCurve Gamma28 = new(CurveKind.Parametric,
        2.8, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, "bt470bg", "gamma2.8");

    /// <summary>ProPhoto ROMM：Γ=1.8 + 线性趾（V=16·L，L≤1/512），白点 D50。</summary>
    public static readonly TransferCurve ProPhoto = new(CurveKind.Parametric,
        1.8, 1.0, 0.0, 1.0 / 16.0, 16.0 / 512.0, 0.0, 0.0, null, "ProPhoto(1.8+toe)");

    /// <summary>ColorMatch RGB：Γ=1.8 纯幂（无线性趾），白点 D50。</summary>
    public static readonly TransferCurve Gamma18 = new(CurveKind.Parametric,
        1.8, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, null, "gamma1.8");

    /// <summary>DCI-P3 / SMPTE RP 431-2 影院绿屏：Γ=2.6 纯幂（zscale 无法命名）。</summary>
    public static readonly TransferCurve Gamma26 = new(CurveKind.Parametric,
        2.6, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, null, "gamma2.6(DCI)");

    /// <summary>BTV/SMTE 240M γ=0.44 曲线（与 BT.709 同理，保留标准取整拐点）。</summary>
    public static readonly TransferCurve Smpte240M = new(CurveKind.Parametric,
        1.0 / 0.44, 1.0 / 1.1115, 0.1115 / 1.1115, 1.0 / 4.0, 0.07611, 0.0, 0.0, "smpte240m", "SMPTE.240M");

    /// <summary>线性。</summary>
    public static readonly TransferCurve Linear = new(CurveKind.Linear,
        1, 1, 0, 0, 0, 0, 0, "linear", "Linear");

    /// <summary>PQ（ST 2084）。1.0 = 10000 nits。</summary>
    public static readonly TransferCurve Pq = new(CurveKind.Pq,
        0, 0, 0, 0, 0, 0, 0, "smpte2084", "PQ(ST2084)");

    /// <summary>HLG 系统级（BT.2100）。1.0 = 参考白。</summary>
    public static readonly TransferCurve Hlg = new(CurveKind.Hlg,
        0, 0, 0, 0, 0, 0, 0, "arib-std-b67", "HLG(BT.2100)");

    // ST 2084 常数（SMPTE ST 2084:2014）
    private const double PqM1 = 2615.0 / 16384.0;
    private const double PqM2 = 2523.0 / 4096.0 * 128.0;
    private const double PqC1 = 3424.0 / 4096.0;
    private const double PqC2 = 2413.0 / 4096.0 * 32.0;
    private const double PqC3 = 2392.0 / 4096.0 * 32.0;

    // HLG 系统级常数（ITU-R BT.2100-2 Table 5）
    private const double HlgA = 0.17883277;
    private const double HlgB = 0.28466892;
    private const double HlgC = 0.55991073;

    /// <summary>PQ 满量程 nits（1.0 编码 = 10000 nits）。</summary>
    public const double PqPeakNits = 10000.0;
    /// <summary>SDR 参考白（BT.2100 / libultrahdr kSdrWhiteNits）。</summary>
    public const double SdrWhiteNits = 203.0;

    // ═══════════════════════════════════════════════════════════════════
    //  正向 / 反向（全部闭式，无迭代）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>编码值 → 线性光（EOTF 语义）。参数形曲线对负输入做符号镜像外推（矩阵混色会产生负值）。</summary>
    public double ToLinear(double x)
    {
        switch (Kind)
        {
            case CurveKind.Linear: return x;
            case CurveKind.Table: return TableToLinear(x);
            case CurveKind.Pq: return PqEotf(x);            // PQ 定义域非负（负值归黑）
            case CurveKind.Hlg: return HlgEotf(x);
            default:
            {
                double s = Math.Sign(x), ax = Math.Abs(x);
                if (ax < D) return s * (C * ax + F);          // 线性趾段
                double t = A * ax + B;
                if (t <= 0.0) return s * E;
                return s * (Math.Pow(t, G) + E);
            }
        }
    }

    /// <summary>线性光 → 编码值（OETF 语义），<see cref="ToLinear"/> 的**闭式**反函数（无需迭代）。</summary>
    public double FromLinear(double l)
    {
        switch (Kind)
        {
            case CurveKind.Linear: return l;
            case CurveKind.Table: return TableFromLinear(l);
            case CurveKind.Pq: return PqOetf(l);
            case CurveKind.Hlg: return HlgOetf(l);
            default:
            {
                double s = Math.Sign(l), al = Math.Abs(l);
                double linAtToe = C * D + F;                  // 趾段在线性域的端点
                if (C != 0.0 && D > 0.0 && al < linAtToe) return s * ((al - F) / C);
                double base_ = Math.Pow(Math.Max(al - E, 0.0), 1.0 / G);
                return s * ((base_ - B) / A);
            }
        }
    }

    /// <summary>本曲线是否有线性趾段（ProPhoto/sRGB/BT.709 有；纯 gamma 无）。</summary>
    public bool HasToeSegment => Kind == CurveKind.Parametric && C != 0.0 && D > 0.0;

    // ── 批量（Span）：RGBA 交错只动 RGB 三通道，alpha 不变 ──

    /// <summary>就地：RGBA 交错 float 的编码值 → 线性。</summary>
    public void ToLinearRgba(Span<float> rgba)
    {
        if (Kind == CurveKind.Linear) return;
        for (int i = 0; i + 4 <= rgba.Length; i += 4)
        {
            rgba[i] = (float)ToLinear(rgba[i]);
            rgba[i + 1] = (float)ToLinear(rgba[i + 1]);
            rgba[i + 2] = (float)ToLinear(rgba[i + 2]);
        }
    }

    /// <summary>就地：RGBA 交错 float 的线性 → 编码值。</summary>
    public void FromLinearRgba(Span<float> rgba)
    {
        if (Kind == CurveKind.Linear) return;
        for (int i = 0; i + 4 <= rgba.Length; i += 4)
        {
            rgba[i] = (float)FromLinear(rgba[i]);
            rgba[i + 1] = (float)FromLinear(rgba[i + 1]);
            rgba[i + 2] = (float)FromLinear(rgba[i + 2]);
        }
    }

    /// <summary>就地：平面数组 编码值 → 线性。</summary>
    public void ToLinearPlane(Span<float> plane)
    {
        if (Kind == CurveKind.Linear) return;
        for (int i = 0; i < plane.Length; i++) plane[i] = (float)ToLinear(plane[i]);
    }

    /// <summary>就地：平面数组 线性 → 编码值。</summary>
    public void FromLinearPlane(Span<float> plane)
    {
        if (Kind == CurveKind.Linear) return;
        for (int i = 0; i < plane.Length; i++) plane[i] = (float)FromLinear(plane[i]);
    }

    // ── Table 曲线：等距编码轴 + 线性插值；反演走二分查找（表必须单调，由 FromTable 保证）──

    private double TableToLinear(double x)
    {
        var t = Lut!;
        double u = Math.Clamp(x, 0.0, 1.0) * (t.Length - 1);
        int i0 = (int)u;
        if (i0 >= t.Length - 1) return t[t.Length - 1];
        double fr = u - i0;
        return t[i0] + (float)(t[i0 + 1] - t[i0]) * fr;
    }

    private double TableFromLinear(double l)
    {
        var t = Lut!;
        if (l <= t[0]) return t[0] == 0f && l <= 0f ? 0.0 : 0.0;
        if (l >= t[t.Length - 1]) return 1.0;
        int lo = 0, hi = t.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (t[mid] <= l) lo = mid; else hi = mid;
        }
        double span = t[hi] - t[lo];
        double fr = span <= 0 ? 0 : (l - t[lo]) / span;
        return (lo + fr) / (t.Length - 1);
    }

    private static double PqEotf(double x)
    {
        if (x <= 0.0) return 0.0;
        double p = Math.Pow(x, 1.0 / PqM2);
        double num = Math.Max(p - PqC1, 0.0);                 // ← 符号：p 小于 c1 时为 0（黑）
        double den = PqC2 - PqC3 * p;                          // ← 分母 (c2 − c3·p)，不可写反
        if (den <= 0.0) return 0.0;
        return Math.Pow(num / den, 1.0 / PqM1);
    }

    private static double PqOetf(double l)
    {
        if (l <= 0.0) return 0.0;
        double y = Math.Pow(l, PqM1);
        // E = [(c1 + c2·L^m1) / (1 + c3·L^m1)]^m2  —— 必须用 c2/c3（c4/c5 是 EOTF 逆变体的常数）。
        // 实测踩过：这里误用 PqC4/PqC5 → L=1 编码出 1e-6 而非 1.0（全图变黑），由往返锚点抓出。
        return Math.Pow((PqC1 + PqC2 * y) / (1.0 + PqC3 * y), PqM2);
    }

    private static double HlgEotf(double x)
    {
        if (x <= 0.5) return x * x / 3.0;
        return (Math.Exp((x - HlgC) / HlgA) + HlgB) / 12.0;
    }

    private static double HlgOetf(double l)
    {
        if (l <= 1.0 / 12.0) return Math.Sqrt(Math.Max(3.0 * l, 0.0));
        return HlgA * Math.Log(Math.Max(12.0 * l - HlgB, 1e-12)) + HlgC;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  查询
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 按曲线等价类比较。容差 1e-4：ICC 用 s15Fixed16 存参数（量化 ~3e-5）且各家取整不同，
    /// 过紧会把“同一个 sRGB 曲线”判为不同（实测踩过：lcms2 存 0.9479 vs 我们的 0.94787）。
    /// </summary>
    public bool Equivalent(TransferCurve other)
    {
        if (Kind != other.Kind) return false;
        const double tol = 1e-4;
        return Kind switch
        {
            CurveKind.Linear => true,
            CurveKind.Pq => true,
            CurveKind.Hlg => true,
            CurveKind.Table => ReferenceEquals(Lut, other.Lut),      // 表曲线仅当同一实例时视为等价
            _ => Math.Abs(G - other.G) < tol && Math.Abs(A - other.A) < tol && Math.Abs(B - other.B) < tol
                 && Math.Abs(C - other.C) < tol && Math.Abs(D - other.D) < tol && Math.Abs(E - other.E) < tol
                 && Math.Abs(F - other.F) < tol,
        };
    }

    /// <summary>CICP transfer token → 曲线（含 ffmpeg/zscale 别名）。未知返回 null。</summary>
    public static TransferCurve? ForCicpTransfer(string? token)
    {
        var t = (token ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        return t switch
        {
            "iec61966-2-1" or "srgb" or "13" => Srgb,
            "bt709" or "709" or "1" or "bt2020-10" or "bt2020-12" or "14" or "15" => Bt709,
            "bt470m" or "gamma22" or "4" => Gamma22,
            "bt470bg" or "gamma28" or "5" or "bt601-6-625" => Gamma28,
            "smpte170m" or "6" => Gamma22,          // BT.601-525/170M 与 2.2 同族（业界按 gamma2.2 处理）
            "smpte240m" or "7" => Smpte240M,
            "linear" or "8" => Linear,
            "smpte2084" or "pq" or "16" => Pq,
            "arib-std-b67" or "hlg" or "18" => Hlg,
            "log100" or "9" or "log316" or "10" => null,   // 不常用且无本项目素材，明确不支持
            _ => null,
        };
    }

    /// <summary>具名色彩空间（Registry 的 SourceTransfer 字段值）→ 曲线。未知返回 null。</summary>
    public static TransferCurve? ForSourceTransfer(string? transferName)
    {
        var t = (transferName ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "gamma1.8" or "1.8" => Gamma18,
            "gamma2.2" or "2.2" => Gamma22,
            "gamma2.6" or "2.6" => Gamma26,
            "smpte2084" or "pq" => Pq,
            "arib-std-b67" or "hlg" => Hlg,
            "iec61966-2-1" or "srgb" => Srgb,
            "bt709" => Bt709,
            "linear" => Linear,
            _ => ForCicpTransfer(t),
        };
    }

    /// <summary>8-bit 输入用的 256 项 EOTF 表（1KB，L1 常驻）——热路径优先用表，避免逐像素 pow。</summary>
    public float[] BuildEotfTable8()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = (float)ToLinear(i / 255.0);
        return t;
    }

    /// <summary>10-bit 输入用的 1024 项 EOTF 表（4KB）。</summary>
    public float[] BuildEotfTable(int codePoints)
    {
        if (codePoints < 2) codePoints = 1024;
        var t = new float[codePoints];
        for (int i = 0; i < codePoints; i++) t[i] = (float)ToLinear(i / (double)(codePoints - 1));
        return t;
    }

    public override string ToString() => Name;
}
