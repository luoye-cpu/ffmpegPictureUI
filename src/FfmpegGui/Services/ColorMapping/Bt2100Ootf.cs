using System;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// ITU-R BT.2100-2 Table 5 的 **HLG 参考 OOTF**（场景线性 → 显示线性 nits）。
///
/// 常数与公式逐字核对自建议书正文（非二手转述）：
///  - `F_D = OOTF[E] = α · Y_S^(γ−1) · E`（R/G/B 同式）
///  - `Y_S = 0.2627 R_S + 0.6780 G_S + 0.0593 B_S`（BT.2100 的 BT.2020 亮度系数；见 Note 5i）
///  - `α` = "the variable for user gain in cd/m². It represents L_W, the nominal peak
///    luminance of a display for achromatic pixels." ⇒ **α = L_W（nits）**
///  - `γ` = 系统伽马，= 1.2 @ L_W = 1000 cd/m²；Note 5f：`γ = 1.2 + 0.42·Log10(L_W/1000)`
///    （建议书允许四舍五入到 3 位有效数字）
///  - 显示 EOTF 是合成式 `F_D = OOTF[OETF⁻¹[max(0,(1−β)E′+β)]]`，`β = 3(L_B/L_W)^(1/γ)`（黑位提升）
///  - **Note 5h：制作与交换期间不得钳位负值/超白**（保处理余量）⇒ 本类的输入值域不设 [0,1] 限制。
///
/// 逆 OOTF 由正式**代数反解**得到（不另抄公式）：
///  `Y_D = α·Y_S^γ ⇒ Y_S = (Y_D/α)^(1/γ)`，`R_S = R_D / (α·Y_S^(γ−1))`。
/// </summary>
public static class Bt2100Ootf
{
    /// <summary>BT.2100 场景亮度系数（Table 5 / Note 5i）。</summary>
    public const double YsR = 0.2627, YsG = 0.6780, YsB = 0.0593;
    /// <summary>建议书的名义显示峰值（γ 公式的归一点）。</summary>
    public const double NominalPeakNits = 1000.0;
    /// <summary>L_W = 1000 cd/m² 时的系统伽马（建议书 Table 5 明示）。</summary>
    public const double GammaAtNominal = 1.2;

    /// <summary>系统伽马 γ = 1.2 + 0.42·Log10(L_W/1000)（Note 5f）。L_W≤0 时退回名义值。</summary>
    public static double SystemGamma(double nominalPeakNits)
        => nominalPeakNits <= 0 ? GammaAtNominal
           : GammaAtNominal + 0.42 * Math.Log10(nominalPeakNits / NominalPeakNits);

    /// <summary>归一化线性场景亮度 Y_S（BT.2020 系数）。</summary>
    public static double SceneLuminance(double rs, double gs, double bs)
        => YsR * rs + YsG * gs + YsB * bs;

    /// <summary>
    /// 正向 OOTF：场景线性（归一，可含负值/超白）→ **显示线性 nits**。
    /// α = L_W，故消色差名义峰值 (1,1,1) 精确落在 L_W nits。
    /// Y_S ≤ 0 时按 0 处理（黑到黑）；负 Y_S 的极端饱和色保留符号，避免 NaN。
    /// </summary>
    public static void Apply(ref double r, ref double g, ref double b, double nominalPeakNits)
    {
        double lw = nominalPeakNits <= 0 ? NominalPeakNits : nominalPeakNits;
        double ys = SceneLuminance(r, g, b);
        double k = Scale(ys, SystemGamma(lw), lw);
        r *= k; g *= k; b *= k;
    }

    /// <summary>反向 OOTF：显示线性 nits → 场景线性（归一）。</summary>
    public static void ApplyInverse(ref double rd, ref double gd, ref double bd, double nominalPeakNits)
    {
        double lw = nominalPeakNits <= 0 ? NominalPeakNits : nominalPeakNits;
        double gamma = SystemGamma(lw);
        double yd = SceneLuminance(rd, gd, bd);
        double ys = SignedPow(yd / lw, 1.0 / gamma);          // Y_S = (Y_D/α)^(1/γ)
        // R_S = R_D / (α · Y_S^(γ−1))；Scale 的第二参是 γ（它内部算 alpha·ys^(γ−1)）
        double inv = 1.0 / Scale(ys, gamma, lw);
        if (!double.IsFinite(inv)) inv = 0.0;                 // 黑→黑（γ>1 时 ys→0 使因子→∞）
        rd *= inv; gd *= inv; bd *= inv;
    }

    /// <summary>α·Y_S^(γ−1) 的公共因子（含 Y_S≤0 与非整指数的安全处理）。</summary>
    private static double Scale(double ys, double exp, double alpha)
    {
        if (ys == 0.0) return exp > 1.0 ? 0.0 : (exp == 1.0 ? alpha : double.PositiveInfinity);
        return alpha * SignedPow(ys, exp - 1.0);
    }

    private static double SignedPow(double x, double p)
        => x >= 0 ? Math.Pow(x, p) : -Math.Pow(-x, p);

    /// <summary>诊断/门禁用：消色差单值的正向映射（Y_S=E ⇒ F = α·E^γ）。</summary>
    public static double ApplyAchromatic(double e, double nominalPeakNits)
    {
        double lw = nominalPeakNits <= 0 ? NominalPeakNits : nominalPeakNits;
        return SignedPow(e, SystemGamma(lw)) * lw;
    }
}
