using FfmpegGui.Models;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 源色彩描述的**置信度**（规划 §5′.1）。低置信度不允许改像素映射，只能直通/携带。
/// </summary>
public enum DescriptorConfidence
{
    /// <summary>完全无描述（既无 ICC 也无 CICP）。</summary>
    None = 0,
    /// <summary>按容器/格式惯例**假定**（如未标注 PNG 假定 sRGB）。</summary>
    CodecDefault = 1,
    /// <summary>来自容器 CICP 标签（primaries/transfer）。</summary>
    CicpTag = 2,
    /// <summary>来自具名空间（Registry 已知空间）。</summary>
    NamedIcc = 3,
    /// <summary>来自 ICC 度量值（rXYZ/gXYZ/bXYZ + TRC 实测解算）—— 最权威。</summary>
    IccMetric = 4,
}

/// <summary>**色彩处理链**损失（与编解码损失分离记录，规划 §3）。</summary>
public enum ColorLoss
{
    /// <summary>零改动（L0 直通 / L1 携带）。判据见 <see cref="ColorTransformPlan.RawFrameHashInvariant"/>。</summary>
    None,
    /// <summary>仅线性域映射 + 出口量化（L2 超集归一，理论 ≤0.5 LSB@目标位深）。</summary>
    RoundTripQuantization,
    /// <summary>源色域超出目标且未压缩 → 裁切。</summary>
    Clipping,
    /// <summary>GMO 色域压缩（有意的感知折衷）。</summary>
    GamutCompression,
    /// <summary>HDR→SDR 色调映射。</summary>
    ToneMapped,
}

/// <summary>**编解码**损失（不属于色彩链，不得混入门禁判定，规划 §3）。</summary>
public enum CodecLoss { Unknown, Lossless, VisuallyLossless, Lossy, Palette }

/// <summary>输出实际采用的标注方式（规划 §1.1 容器限定）。</summary>
public enum AnnotChoice { None, IccAndCicp, IccOnly, CicpOnly, UnlabeledAssumedSrgb }

/// <summary>为何没能写 CICP（诊断用，规划 §1.1）。</summary>
public enum CicpUnavailableReason
{
    None,
    /// <summary>容器根本没有 CICP 通路（PNG/JPEG/WebP/GIF）。</summary>
    ContainerNoCicp,
    /// <summary>容器有相关字段但非规范/查看器行为不一致（TIFF）。</summary>
    ContainerNonNormative,
    /// <summary>目标空间无法用 CICP 命名（ProPhoto/Adobe/自定义 ICC）。</summary>
    SpaceNotNameable,
    /// <summary>HDR 曲线无法生成 ICC（iccgen 不支持 PQ/HLG）—— 标注侧限制。</summary>
    HdrNoIccGenerator,
}

/// <summary>策略被覆盖的原因（规划 §2；contract 自检据此豁免对应格）。</summary>
public enum OverrideReason
{
    None,
    /// <summary>生成 GainMap 的功能定义本身要求底图 tone-map + 色域映射 ⇒ S2 不适用，按 S1/S4 执行。</summary>
    GainMapRequiresBaseMapping,
    /// <summary>HDR 通路无"携带 HDR ICC"能力（iccgen 不支持 HDR）⇒ S2 降为标注继承，不映射。</summary>
    HdrNoIccCarryPath,
    /// <summary>目标容器不能携带 ICC ⇒ S2 无法兑现（E1 未通过前的 AVIF 即此）。</summary>
    ContainerCannotCarryIcc,
    /// <summary>源无 ICC 可携带 ⇒ S2 退化为 L0 直通或 Reject。</summary>
    SourceHasNoIcc,
}

/// <summary>
/// S1（推荐）在"能携带 ICC"时是否优先直通携带（零量化损失）而非归一化（规划 §9 唯一开关）。
/// </summary>
public enum CarryScope
{
    /// <summary>从不优先携带 ⇒ 一律按 L2/L3 归一到标准可表达空间（保守兼容旧行为）。</summary>
    None = 0,
    /// <summary>仅**无法 CICP 命名**的空间（ProPhoto / ColorMatch 等）携带源 ICC。</summary>
    Unnameable = 1,
    /// <summary>凡源有 ICC 且容器可携带 ⇒ 携带（**默认**，无损优先）。</summary>
    All = 2,
}

/// <summary>
/// BT.709 编码口径（实测分歧）：zscale 把 `t=bt709` 实现为纯 γ1/2.4（拟合 RMS 7e-5），
/// 而 BT.709-6 定义式（α/β）与同构建 lcms2 生成的 BT.709 ICC 一致。
/// </summary>
public enum Transfer709Curve
{
    /// <summary>**默认**：按 ITU-R BT.709-6 定义式，保证像素↔ICC↔CICP 自洽；并对 bt709 目标禁用 H2。</summary>
    Std = 0,
    /// <summary>对齐 zimg/历史产物（纯 γ2.4）：允许 H2；仅用于对照与回退。</summary>
    Zimg = 1,
}

/// <summary>
/// 用户手动色彩意图。<see cref="FieldsFromUi"/> 为 false 的字段**必须为 null**，
/// 以杜绝"取消勾选高级色彩后残留值仍流入管道"的历史缺陷。
/// </summary>
public sealed class ManualIntent
{
    /// <summary>具名目标空间（如 "Display P3" / "ProPhoto RGB"）。</summary>
    public string? Space;
    /// <summary>CICP primaries token（高级色彩三元组）。</summary>
    public string? Primaries;
    /// <summary>CICP transfer token。</summary>
    public string? Transfer;
    /// <summary>CICP matrix token。</summary>
    public string? Matrix;
    /// <summary>字段是否来自"已启用"的 UI/CLI 输入（false 表示应当忽略残留值）。</summary>
    public bool FieldsFromUi;

    /// <summary>仅当来自启用的输入时才有内容（供调用方构造时使用，防呆）。</summary>
    public bool HasAny => FieldsFromUi && (Space != null || Primaries != null || Transfer != null);

    /// <summary>由 UI/CLI 读取点构造；<paramref name="enabled"/>=false 时全部字段强制 null。</summary>
    public static ManualIntent Create(bool enabled, string? space, string? primaries, string? transfer, string? matrix)
        => enabled
            ? new ManualIntent
            {
                FieldsFromUi = true,
                Space = string.IsNullOrWhiteSpace(space) ? null : space.Trim(),
                Primaries = string.IsNullOrWhiteSpace(primaries) ? null : primaries.Trim(),
                Transfer = string.IsNullOrWhiteSpace(transfer) ? null : transfer.Trim(),
                Matrix = string.IsNullOrWhiteSpace(matrix) ? null : matrix.Trim(),
            }
            : new ManualIntent { FieldsFromUi = false };

    public override string ToString() => FieldsFromUi
        ? $"manual(space={Space ?? "-"},p={Primaries ?? "-"},t={Transfer ?? "-"})"
        : "manual(未启用)";
}

/// <summary>等价判定阈值集中处（规划 §5′.2）；probe 有专测，禁止散落魔数。</summary>
public static class EquivalenceTolerances
{
    /// <summary>|MatrixTo(dst) − I|∞ ≤ 此值 ⇒ 判为同原色（跳矩阵）。</summary>
    public const double MatrixToIdentity = 1e-5;
    /// <summary>逐元素比较两个 RGB→XYZ(D50) 的容差（等价判定）。</summary>
    public const double MatrixElements = 1e-5;
    /// <summary>曲线参数逐项容差（ICC s15Fixed16 量化决定了不能更严）。</summary>
    public const double CurveParams = 1e-4;
    /// <summary>原色色度 xy 逐项容差 ⇒ 判为同原色。</summary>
    public const double Chromaticity = 1e-4;
    /// <summary>几何包含判据的边界松弛。</summary>
    public const double ContainmentEpsilon = 1e-6;
    /// <summary>映射前置条件：源置信度下限（S4 例外，由用户担保）。</summary>
    public const DescriptorConfidence MinConfidenceForMapping = DescriptorConfidence.CicpTag;
}

/// <summary>
/// 色彩意图（管线唯一的顶层输入，规划 §1）。由 QueueProcessor 从 <c>FfmpegOptions</c> 构造；
/// 引擎据此产出唯一 <see cref="ColorTransformPlan"/>，四条执行路径共同遵守（"简化模式只简化 UI"）。
/// </summary>
public sealed class ColorIntent
{
    /// <summary>用户声明的策略（S1..S4）。</summary>
    public ColorStrategy Strategy = ColorStrategy.Recommended;
    /// <summary>手动字段（未启用时必须为 null 内容）。</summary>
    public ManualIntent? Manual;

    public ColorSpaceDescriptor? Source;
    /// <summary>用户/策略指定的目标空间；null = auto（看齐源）。</summary>
    public ColorSpaceDescriptor? Target;
    /// <summary>目标是否由用户显式指定（S4 或"选了目标色域下拉"）。</summary>
    public bool TargetExplicit;

    public FormatColorCaps Format = ColorMappingEngine.CapsFor("png");
    public int TargetBitDepth = 8;
    public bool HdrOutput;
    public bool SourceIsRaw;
    /// <summary>允许 GMO 色域压缩（否则越界只允许超集或裁切告警）。</summary>
    public bool AllowGamutCompression;
    /// <summary>本次任务要生成 GainMap（UltraHDR JPEG）⇒ 触发 §2 策略覆盖。</summary>
    public bool GainMapRequested;
    /// <summary>
    /// 本次**编码**由引擎内的**外部编码器出口**完成（当前唯一实现：cjxl / JXL）。
    /// 由 `ColorIntentFactory` 按 `o.EncoderBackend` 置位 —— 判据是
    /// 「目标格式 jxl **且** 后端 Cjxl」（cjxl 不吃 rawvideo，必须走 PPM/PAM 管道）。
    /// ⚠ 与 <c>ColorTransformPlan.Backend</c>（色彩算子在哪算）不是一回事，见该字段的注释。
    /// </summary>
    public bool ExternalEncoderExit;
    /// <summary>用户显式请求 HDR→SDR 色调映射。</summary>
    public bool ToneMapRequested;
    /// <summary>
    /// 超广色域保真 ICC 的绝对路径（非空 ⇒ 走「像素零改动 + 仅靠 ICC 描述」）。
    /// 由 `ColorIntentFactory` 用 `FfmpegCommandBuilder.ResolveFaithfulUltraWideIcc` 判定
    ///（与传统管线**同一个函数**）—— 2026-09-16 接入引擎。
    /// </summary>
    public string? FaithfulIccPath;
    /// <summary>
    /// 色调映射算子是**自动**采用的（非用户显式指定）。当前唯一来源：HDR 源 + 容器不支持 HDR
    /// ⇒ 自动用内部默认曲线 `hable`（对齐传统链的既有行为，见 `ColorIntentFactory`）。
    /// 用途：让决策日志能区分"用户要的"与"系统兜的"，避免看起来像静默降级。
    /// </summary>
    public bool ToneMapAuto;
    /// <summary>请求的色调映射算子（None = 不映射；与 ToneMapRequested 同时成立才生效）。</summary>
    public ToneMapMode ToneMap = ToneMapMode.None;
    /// <summary>内容峰值 nits（0 = 未知 → 由源曲线峰值推断，不猜 1000）。</summary>
    public double HdrPeakNits;
    /// <summary>
    /// <see cref="HdrPeakNits"/> 的来源（"用户 --color-hdr-peak" / "MaxCLL" / "BS.2086 mastering"）。
    /// 峰值直接决定 headroom（压暗程度），日志必须能说清它是读来的还是猜的（见 <c>ColorStrategyPlanning</c>）。
    /// </summary>
    public string? HdrPeakSource;
    /// <summary>SDR 目标白点 nits（默认 203，BT.2100）。</summary>
    public double SdrWhiteNits = FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits;
    /// <summary>Method A 的 SDR 目标峰值 L_SDR（nits，建议书默认 100）。
    /// 与 SdrWhiteNits（参考白 203）是两个不同的量，不可混用。</summary>
    public double SdrPeakNits = 100.0;
    /// <summary>允许在未标注容器上产出"隐含 sRGB"的无标注结果（TIFF 需此开关）。</summary>
    public bool AllowUnlabeled;
    /// <summary>S1 携带范围（默认 All = 无损优先）。</summary>
    public CarryScope CarryScope = CarryScope.All;
    /// <summary>是否允许"用近似曲线凑数"（legacy=true；引擎=false）。</summary>
    public bool AllowApproximateCurve;
    /// <summary>目标是否请求无损编码（影响 <see cref="CodecLoss"/> 归类）。</summary>
    public bool TargetLossless;
    /// <summary>BT.709 编码口径（默认 Std；Zimg 仅用于与历史产物对照）。</summary>
    public Transfer709Curve Transfer709 = Transfer709Curve.Std;
    /// <summary>日志前缀（文件名等），便于并行队列里对应回任务。</summary>
    public string LogTag = "";

    /// <summary>转换为规划层策略对象（保持与既有 <see cref="PlanPolicy"/> 兼容）。</summary>
    public PlanPolicy ToPolicy() => new()
    {
        Format = Format,
        TargetBitDepth = TargetBitDepth,
        HdrOutput = HdrOutput,
        SourceIsRaw = SourceIsRaw,
        AllowGamutCompression = AllowGamutCompression,
        AllowApproximateCurve = AllowApproximateCurve,
        TargetExplicit = TargetExplicit || Strategy == ColorStrategy.Manual,
        ForceMapping = Strategy == ColorStrategy.Manual && (Manual?.HasAny ?? false) && TargetExplicit,
        // 携带倾向由 CarryScope 决定（§9 唯一开关）；S2 的"强制携带"在策略层单独处理，不走此标志
        PreferCarry = Strategy != ColorStrategy.BakeCicpOnly && CarryScope != CarryScope.None,
        Strategy = Strategy,
        CarryScope = CarryScope,
        AllowUnlabeled = AllowUnlabeled,
        GainMapRequested = GainMapRequested,
        ToneMapRequested = ToneMapRequested,
        FaithfulIccPath = FaithfulIccPath,
        Transfer709 = Transfer709,
        ExternalEncoderExit = ExternalEncoderExit,
    };

    /// <summary>按容器与是否请求无损推断**编解码**损失（与色彩链无关）。</summary>
    public CodecLoss InferCodecLoss() => ColorMappingEngine.CodecLossOf(Format.Format, TargetLossless);
}
