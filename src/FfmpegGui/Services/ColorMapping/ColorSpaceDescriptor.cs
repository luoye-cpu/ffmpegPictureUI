using System;
using System.Globalization;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 统一的色彩空间描述符（色彩映射引擎的中间表示）。
///
/// 设计要点：**一切映射经由 XYZ(D50) 复合**
///   M(dst←src) = inv(dst.RGB→XYZ_D50) · src.RGB→XYZ_D50
/// 这样"CICP 命名的空间"（由 H.273 色度解算）与"任意 ICC 描述的空间"（直接取 rXYZ/gXYZ/bXYZ）
/// 可以用同一条路径处理，无需为每种来源写一套矩阵逻辑；ICC 的 D50 PCS 天然把白点色适应包含在内。
///
/// ⚠️ 两条硬规则（本项目的历史教训）：
/// 1. CICP token 一律查 <see cref="ColorSpaceRegistry.GetPrimaries"/> 规范原色表，
///    **绝不可**从"归一化 Spec 的 OutPrimaries"反查（那是输出标签，不是源色度）。
/// 2. 无法确定原色/曲线时宁可返回 null（上层走携带 ICC 或告警），
///    **绝不允许**"只改标签"或用近似曲线凑数。
/// </summary>
public sealed class ColorSpaceDescriptor
{
    /// <summary>展示名（日志/诊断），例如 "sRGB"、"BT.2020 PQ"、"ICC:Adobe RGB (1998)"。</summary>
    public string Name = "(unset)";

    /// <summary>RGB→XYZ(D50) 矩阵，9 项行主序。映射计算的唯一依据。</summary>
    public double[]? RgbToXyzD50;

    /// <summary>原色色度 xy（已知时给出，用于色域比较/诊断；由 ICC 反解时可为 null）。</summary>
    public double[]? Rp, Gp, Bp, Wp;

    /// <summary>传递函数（编码↔线性）。必填 —— 没有曲线就无法安全映射。</summary>
    public TransferCurve Curve;

    /// <summary>等价的 CICP primaries token；不能命名则 null。</summary>
    public string? PrimariesToken;

    /// <summary>
    /// 由 **ICC 度量值**（色度矩阵 + 曲线）命中的已知空间**注册名**（<c>ColorSpaceRegistry</c> 认得的名字）。
    /// 为何需要单独存：`Name` 是给人看的组合串（含曲线与误差），不能当 key；而 `sourceGamutName`
    /// 一类字段必须是注册名，否则“超广色域保真 / 归一 BT.2020 / 底图原色”这些判断会静默失效
    /// ——本仓库生成的标准 ICC 描述就是“RGB built-in”，按描述文字根本认不出空间（实测）。
    /// </summary>
    public string? KnownSpaceName;

    /// <summary>等价的 CICP transfer token；不能命名则 null。</summary>
    public string? TransferToken => Curve.CicpToken;

    /// <summary>该描述符是否源自 ICC 度量值（比容器标签更权威）。</summary>
    public bool IsIccDerived;

    /// <summary>
    /// 描述的**置信度**（规划 §5′.1）：低于 <see cref="EquivalenceTolerances.MinConfidenceForMapping"/>
    /// 时引擎**不得改像素映射**，只能直通/携带（S4 由用户担保例外）。
    /// </summary>
    public DescriptorConfidence Confidence = DescriptorConfidence.None;

    /// <summary>
    /// 线性值 1.0 的亮度语义（nits）：SDR = 203（参考白）；PQ 归一线性 = 10000；HLG = 1000。
    /// 供 tone mapping / gain map 计算 headroom，不参与色度映射。
    /// </summary>
    public double NitsOfLinearOne = TransferCurve.SdrWhiteNits;

    /// <summary>来源 ICC 文件路径（阶梯②"携带而不映射"时使用；引擎不负责删除）。</summary>
    public string? SourceIccPath;

    // ═══════════════════════════════════════════════════════════════════
    //  构造：三种来源
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 由 CICP (primaries, transfer) tokens 构造。任一 token 不认识 → 返回 null（不猜）。
    /// </summary>
    public static ColorSpaceDescriptor? FromCicp(string? primariesToken, string? transferToken,
        string? name = null, double nitsOfLinearOne = TransferCurve.SdrWhiteNits)
    {
        var def = ColorSpaceRegistry.GetPrimaries(primariesToken);
        if (def == null) return null;
        var curve = TransferCurve.ForCicpTransfer(transferToken);
        if (curve == null) return null;
        var m = ColorSpaceRegistry.ColorantsD50(primariesToken);
        if (m == null) return null;
        return new ColorSpaceDescriptor
        {
            Name = name ?? $"{def.Token}/{curve.Value.Name}",
            RgbToXyzD50 = m,
            Rp = def.R, Gp = def.G, Bp = def.B, Wp = def.W,
            Curve = curve.Value,
            PrimariesToken = def.Token,
            NitsOfLinearOne = nitsOfLinearOne,
            Confidence = DescriptorConfidence.CicpTag,
        };
    }

    /// <summary>
    /// 由具名色彩空间（Registry 的 Key，如 "ProPhoto RGB"/"Adobe RGB"/"Display P3"/"BT.2100 PQ"）构造。
    /// 色度取该空间**自身**色度；曲线取 SourceTransfer（源编码曲线），未记录时退 OutTrc。
    /// 无定义或曲线未知 → null。
    /// </summary>
    public static ColorSpaceDescriptor? FromNamedSpace(string? displayName)
    {
        var chr = ColorSpaceRegistry.NamedSpaceChromaticities(displayName);
        if (chr == null) return null;
        var m = ColorSpaceRegistry.NamedSpaceColorantsD50(displayName);
        if (m == null) return null;

        string? trc = ColorSpaceRegistry.NamedSpaceSourceTransfer(displayName);
        // 具名空间的曲线需按空间名特殊处理：ProPhoto 的源传递在 Registry 里只记为 "gamma1.8"，
        // 但 ProPhoto 实际是 1.8 + 线性趾（16·L），不能当成纯幂 1.8。
        var curve = KeyCurveFor(displayName) ?? TransferCurve.ForSourceTransfer(trc) ?? TransferCurve.ForCicpTransfer(trc);
        curve ??= TransferCurve.ForCicpTransfer(ColorSpaceRegistry.Resolve(displayName)?.OutTrc);
        if (curve == null) return null;

        bool hdr = curve.Value.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;
        return new ColorSpaceDescriptor
        {
            Name = $"{displayName} (named, {curve.Value.Name})",
            RgbToXyzD50 = m,
            Rp = chr.Value.R, Gp = chr.Value.G, Bp = chr.Value.B, Wp = chr.Value.W,
            Curve = curve.Value,
            PrimariesToken = null,                      // 具名空间不等于任何 CICP token（不可用于 H2）
            Confidence = DescriptorConfidence.NamedIcc,
            NitsOfLinearOne = hdr ? (curve.Value.Kind == TransferCurve.CurveKind.Pq
                                        ? TransferCurve.PqPeakNits : 1000.0)
                                  : TransferCurve.SdrWhiteNits,
        };
    }

    /// <summary>特定具名空间的曲线覆盖（不能仅靠 SourceTransfer 字符串区分“1.8 纯幂”与“ProPhoto 1.8+趾”）。</summary>
    private static TransferCurve? KeyCurveFor(string? displayName)
    {
        var n = (displayName ?? "").Trim().ToLowerInvariant();
        if (n.StartsWith("prophoto") || n.StartsWith("romm")) return TransferCurve.ProPhoto;
        if (n.StartsWith("colormatch")) return TransferCurve.Gamma18;
        if (n.StartsWith("dci-p3") || n == "dci p3") return TransferCurve.Gamma26;
        return null;
    }

    /// <summary>
    /// 由 ICC 的 rXYZ/gXYZ/bXYZ（列向量，即 RGB→XYZ_D50）与曲线构造。<paramref name="rgbToXyzD50"/> 行主序 9 项。
    /// </summary>
    public static ColorSpaceDescriptor FromIcc(double[] rgbToXyzD50, TransferCurve curve, string name,
        string? sourceIccPath = null, double[]? wtptD50 = null)
    {
        return new ColorSpaceDescriptor
        {
            Name = name,
            RgbToXyzD50 = rgbToXyzD50,
            Curve = curve,
            IsIccDerived = true,
            Confidence = DescriptorConfidence.IccMetric,
            SourceIccPath = sourceIccPath,
            // ICC 的白点即 (1,1,1) 的 XYZ 像 = 矩阵列和；无需额外 wtpt 也成立
            NitsOfLinearOne = curve.Kind == TransferCurve.CurveKind.Pq ? TransferCurve.PqPeakNits
                              : curve.Kind == TransferCurve.CurveKind.Hlg ? 1000.0
                              : TransferCurve.SdrWhiteNits,
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  使用
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>能否参与映射：必须持有完整的 RGB→XYZ(D50) 矩阵（曲线为表型时仍可映射，精度由表保证）。</summary>
    public bool IsMappable => RgbToXyzD50 is { Length: 9 };

    /// <summary>
    /// 求 src(=本描述符) 线性 RGB → dst 线性 RGB 的 3×3 矩阵（经 XYZ_D50 复合）。
    /// 任一矩阵缺失/奇异 → null（调用方必须放弃映射，不得只改标签）。
    /// </summary>
    public double[]? MatrixTo(ColorSpaceDescriptor? dst)
    {
        if (dst == null || RgbToXyzD50 == null || dst.RgbToXyzD50 == null) return null;
        var inv = ColorSpaceRegistry.InvertMatrix(dst.RgbToXyzD50);
        if (inv == null) return null;
        var m = ColorSpaceRegistry.MultiplyMatrices(inv, RgbToXyzD50);
        // 自洽校验：行和≈1（白点守恒）。偏离过大说明色度/适算错，宁可不映射。
        // 阀值取 5e-3：源于各空间自身 D50 定义的固有差异（如 ProPhoto 标称 D50 的 Z/Y=0.8251
        // 与 ICC PCS 的 0.8249 / 由 D65 适应得出的 0.8244 相差 ~7e-4，再乘以矩阵放大→ 行和偏差可达 ~1.4e-3）。
        for (int r = 0; r < 3; r++)
        {
            double s = m[r * 3] + m[r * 3 + 1] + m[r * 3 + 2];
            if (double.IsNaN(s) || double.IsInfinity(s) || Math.Abs(s - 1.0) > 5e-3) { LastRowSumDeviation = Math.Abs(s - 1.0); return null; }
        }
        LastRowSumDeviation = 0;
        return m;
    }

    /// <summary>最近一次 <see cref="MatrixTo"/> 失败时的行和偏差（诊断用，便于日志说清“为什么不映射”）。</summary>
    public double LastRowSumDeviation { get; private set; }

    /// <summary>曲线等价（可跳过重编码）。</summary>
    public bool SameCurveAs(ColorSpaceDescriptor? other) => other != null && Curve.Equivalent(other.Curve);

    /// <summary>
    /// 是否为 zscale/ffmpeg 可精确命名的空间（决定 H2 后端可行性）。
    /// 要求：primaries token 与 transfer token 都存在且非表曲线。
    /// </summary>
    public bool IsCicpNameable => PrimariesToken != null && Curve.CicpToken != null;

    /// <summary>等价判定（同原色矩阵 + 同曲线）→ 可完全跳过映射。</summary>
    public bool Equivalent(ColorSpaceDescriptor? other)
    {
        if (other == null) return false;
        if (!SameCurveAs(other)) return false;
        if (ReferenceEquals(RgbToXyzD50, other.RgbToXyzD50)) return true;
        if (RgbToXyzD50 == null || other.RgbToXyzD50 == null) return false;
        for (int i = 0; i < 9; i++)
            if (Math.Abs(RgbToXyzD50[i] - other.RgbToXyzD50[i]) > 1e-5) return false;
        return true;
    }

    /// <summary>
    /// 由 RGB→XYZ(D50) 的**列**导出各原色色度 xy（列向量归一化）：返回 [rx,ry,gx,gy,bx,by]；无矩阵返回 null。
    /// 原色三角形在色度图上的位量只取决于方向，因此列和归一即得 xy。
    /// </summary>
    public double[]? PrimariesXyD50()
    {
        if (RgbToXyzD50 is not { Length: 9 } m) return null;
        var xy = new double[6];
        for (int c = 0; c < 3; c++)
        {
            double x = m[c], y = m[3 + c], z = m[6 + c], s = x + y + z;
            if (Math.Abs(s) < 1e-12) return null;
            xy[c * 2] = x / s; xy[c * 2 + 1] = y / s;
        }
        return xy;
    }

    /// <summary>
    /// **几何包含判据**（规划 §5′.3，取代“矩阵元素全非负”的错判）：
    /// 源三原色的 xy 是否全部落在目标原色三角形内（同侧法，边界松弛 ε）。
    /// 两者均在 D50 空间比较：白点适应是对 XYZ 的可逆线性变换，在色度平面上诱导射影变换，
    /// 而射影变换保持“点在三角形内”关系 ⇒ 结果与在未适应空间判定一致。
    /// 返回 null = 无法判定（缺矩阵/退化原色）。
    /// </summary>
    public static bool? ContainsGamut(ColorSpaceDescriptor? src, ColorSpaceDescriptor? dst)
    {
        var a = src?.PrimariesXyD50(); var b = dst?.PrimariesXyD50();
        if (a == null || b == null) return null;
        // 目标三角形必须非退化
        if (Math.Abs(SignedArea(b[0], b[1], b[2], b[3], b[4], b[5])) < 1e-9) return null;
        for (int i = 0; i < 3; i++)
        {
            double px = a[i * 2], py = a[i * 2 + 1];
            if (!PointInTriangle(px, py, b, EquivalenceTolerances.ContainmentEpsilon)) return false;
        }
        return true;
    }

    private static double SignedArea(double x1, double y1, double x2, double y2, double x3, double y3)
        => (x2 - x1) * (y3 - y1) - (y2 - y1) * (x3 - x1);

    /// <summary>
    /// 同侧法内点测试：点到三边的叉积必须**同号或为零**（边界松弛 slack）。
    /// ⚠ 不能用严格不等式：源原色与目标原色**重合**时（如 sRGB⊆sRGB、P3 与 2020 共用蓝原色）
    ///   会有两条边叉积恰好为 0，严格判据会把它们当外点而误判为不包含（实测踩过）。
    /// </summary>
    private static bool PointInTriangle(double px, double py, double[] t, double slack)
    {
        double d1 = SignedArea(t[0], t[1], t[2], t[3], px, py);
        double d2 = SignedArea(t[2], t[3], t[4], t[5], px, py);
        double d3 = SignedArea(t[4], t[5], t[0], t[1], px, py);
        bool neg = d1 <= slack && d2 <= slack && d3 <= slack;
        bool pos = d1 >= -slack && d2 >= -slack && d3 >= -slack;
        return neg || pos;
    }

    /// <summary>诊断字符串（含矩阵，便于日志核对；不含敏感信息）。</summary>
    public string ToDiagnosticString()
    {
        if (RgbToXyzD50 == null) return $"{Name} [无矩阵]";
        var ci = CultureInfo.InvariantCulture;
        return $"{Name} curve={Curve.Name} icc={IsIccDerived} " +
               $"XYZd50=[{string.Join(",", Array.ConvertAll(RgbToXyzD50, v => v.ToString("0.000000", ci)))}]";
    }

    public override string ToString() => $"{Name} [{Curve.Name}]";
}
