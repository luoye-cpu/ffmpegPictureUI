using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FfmpegGui.Models;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>引擎决策结果类型。</summary>
public enum ColorAction { None, Map, CarryIcc, Reject }

/// <summary>执行后端（两只手 + LUT 兜底）。</summary>
public enum ColorBackend { None, FfmpegFilter, InProcess, Lut }

/// <summary>容器/编码器对色彩描述的能力（L5 标注与决策阶梯的依据；数值来自本项目实测）。</summary>
public sealed class FormatColorCaps
{
    public string Format = "";
    /// <summary>可携带任意 ICC（PNG iCCP / JPEG APP2 / TIFF / WebP ICCP / JXL cjxl -x icc_pathname）。</summary>
    public bool CanCarryArbitraryIcc;
    /// <summary>可写 CICP（AVIF/HEIF colr nclx、JXL 枚举、MP4）。</summary>
    public bool CanCicp;
    /// <summary>可由 ffmpeg iccgen 依帧标签生成 ICC（需要 primaries+trc 可命名；iccgen 不支持 HDR 曲线）。</summary>
    public bool CanGenerateIccFromCicp;
    /// <summary>支持的峰值位深（8/16/32）。</summary>
    public int MaxBitDepth = 8;
    /// <summary>是否 RGB 原生（无子采样；非 RGB 原生需在 RGB 域完成映射后再下采样）。</summary>
    public bool RgbNative;
    /// <summary>能否表达 HDR 曲线（PQ/HLG）。</summary>
    public bool CanHdr;

    /// <summary>
    /// 本容器在「**是否开启 GainMap**」这一**运行期条件**下的 HDR 承载能力。
    /// <para>
    /// ⚠ JPEG 族本身是 8-bit SDR 容器（<see cref="CanHdr"/> 未设 ⇒ false），但**开启 GainMap 后**
    /// 产物是 **Ultra HDR**（SDR 底片 + 增益层，ISO 21496-1 / XMP+MPF）⇒ **能表达 HDR**。
    /// 不翻真会让 <c>ColorIntentFactory</c> 对 HDR 源走「容器装不下 ⇒ 自动 hable tonemap 降 SDR」
    /// ⇒ **GainMap 出口白开**：用户开了增益图，却拿到一张纯 SDR（且没有任何点名）。
    /// </para>
    /// <para>
    /// ⚠⚠ **必须是纯函数**：<c>Caps</c> 是**静态共享字典**，这些对象被**所有任务复用** ⇒
    /// 运行期改 <see cref="CanHdr"/> 字段会污染并发任务（本仓已多次踩过同类「共享可变状态」）。
    /// </para>
    /// <para>
    /// ⚠ **位深前提不叠在这里**：GainMap 是「8-bit 底片 + 增益层」，**不依赖高位深**；
    /// 而 <see cref="HdrRequiresHighBitDepth"/> 只对 PNG/APNG 的 PQ/HLG 成立
    /// ⇒ 位深前提由调用方（<c>CanCarryHdrAt</c>）统一叠加，避免两处各判一次。
    /// </para>
    /// </summary>
    public bool CanHdrWith(bool gainMapEnabled) => CanHdr || (gainMapEnabled && SupportsGainMap);
    /// <summary>
    /// “未标注即隐含 sRGB”对本容器是否**成立且有规范/惯例依据**（PNG/JPEG/WebP ✅）。
    /// TIFF 故意置 false：其未标注行为在各查看器不一致 ⇒ 不得享“无标注直通”（规划 §1.3）。
    /// </summary>
    public bool UnlabeledMeansSrgb;
    /// <summary>能否**同时**携带 ICC 与写 CICP（JXL 在 E2 实测前保守为 false ⇒ 择一）。</summary>
    public bool CanHaveBothIccAndCicp = true;
    /// <summary>
    /// ★ **出口**（不是容器！）可写入的 CICP (primaries,transfer) 组合限制：
    ///   null = 自由 nclx；非空 = 只能取表内枚举。
    /// <para>
    /// ⚠ 任务 #38 把语义写进名字：这一位**曾经**叫 <c>CicpSpaceEnums</c> 并被当成"容器能力"读，
    ///   而 jxl 行填的其实是 **cjxl 的 `-x color_space=` 枚举宽度** —— 同一目标走 ffmpeg 的 libjxl
    ///   出口能写任意 nclx（实测：<c>-e Ffmpeg</c> + DCI-P3 ⇒ jxlinfo 读回「P3 primaries, sRGB transfer」，
    ///   而 <c>#37</c> 那 24 格 BT.2020 在 cjxl 出口上只能改走 <c>-x icc_pathname=</c>）。
    ///   把它当容器能力用的后果由 <c>#37</c> 量过：无条件按枚举剥 CICP 会把 ffmpeg 出口那条
    ///   **本来能交付**的路打成 0 字节产物。
    /// </para>
    /// <para>
    /// ⇒ 因此本位**只允许在外部编码器出口（cjxl / JxrEncApp）成立时参与判断**，
    ///   由 <see cref="CapsAcceptsCicp"/> 的第三个参数把关（该参数**没有默认值**，
    ///   编译器逼每个调用点表态；结构锁见 <c>verify-color-caps</c> 的出口维度段）。
    /// </para>
    /// </summary>
    public string[]? ExternalOutletCicpSpaceEnums;
    /// <summary>无 CICP 通路时的归因（S3 与日志用；TIFF = ContainerNonNormative）。</summary>
    public CicpUnavailableReason NoCicpReason = CicpUnavailableReason.ContainerNoCicp;
    /// <summary>
    /// 写 CICP 时的额外硬约束（E1 实测）：avifenc 在 --lossless 下要求 matrix 必须为 identity(0)，
    /// 传 6/9 会直接报错 → 标注生成器必须按此选 matrix。
    /// </summary>
    public bool LosslessForcesIdentityMatrix;
    /// <summary>
    /// 本容器能否承载**增益图（ISO 21496-1 / Adobe XMP + MPF）**。
    /// GainMap 是“输出结构”能力，不是色调曲线：目前只有 JPEG 族成立。
    /// ⚠ 不能靠 UI 的 IsVisible 当门禁——CLI/预设能绕过 UI（实测踩过）。
    /// </summary>
    public bool SupportsGainMap;
    /// <summary>
    /// 本容器的编码器**自己会落盘**由色彩元数据生成的 ICC（实测：mjpeg/PNG ✅、**libwebp ❌**）。
    /// <para>
    /// 用途：把传统管线既有的「sRGB 出口不**额外后置**嵌入 ICC」契约（<c>FfmpegCommandBuilder.Decision.cs</c>
    /// 的 <c>unlabeledIsFine</c>，2026-09-14 实测）搬进规划层，使引擎路径与它同口径。
    /// 契约实质不是"sRGB 就一律不写 ICC"，而是"**不为一张已经等价于容器默认语义的 ICC
    /// 再付一次后置嵌入的代价**"：jpg/png 的 ICC 由编码器顺手落盘（不额外花钱，历史产物一直带），
    /// webp 必须靠 exiftool 后置补（libwebp 丢弃 iccgen 的 ICC）⇒ 语义重复时省掉它。
    /// </para>
    /// <para>⚠ 与 <c>EncoderWritesFilterIcc</c>（传统裁决点的同名判据）是**同一件实测事实**，
    /// 此处登记进能力表以便规划层单点读取（禁止在调用方按格式名 if-else）。</para>
    /// </summary>
    public bool EncoderWritesGeneratedIcc = true;

    /// <summary>
    /// <see cref="CanHdr"/> 的能力**前提是位深 &gt;8**（PNG/APNG：PQ/HLG 只能由 16bit 承载）。
    /// AVIF/JXL 的 HDR 是原生的、不随位深失效 ⇒ false。
    /// <para>
    /// 用途：判断"容器在**本次目标位深**下能否承载 HDR"。8bit 的 PNG 装不下 PQ/HLG
    /// ⇒ 引擎不得声称"保持 HDR"（旧实现宣称 bt2020/smpte2084 而交付是 bt709/bt709）。
    /// </para>
    /// </summary>
    public bool HdrRequiresHighBitDepth;

    /// <summary>能力自检来源（规划 §5′.5 版本化：哪一项由哪个实验/版本确认）。</summary>
    public string VerifiedBy = "";
    /// <summary>
    /// 本容器是否有任何色彩管理通路（GIF/JXR/PPM = false）。
    /// 无通路时“无标注 + 像素等价 sRGB”是唯一合法结局（而不是 Reject）。
    /// </summary>
    public bool HasColorPath => CanCicp || CanCarryArbitraryIcc || CanGenerateIccFromCicp;
    /// <summary>确认时的工具链版本。</summary>
    public string VerifiedOn = "";
}

/// <summary>规划策略。</summary>
public sealed class PlanPolicy
{
    public FormatColorCaps Format = new() { Format = "png" };
    /// <summary>是否允许"用近似曲线凑"（legacy=true；引擎=false，宁可不映射）。</summary>
    public bool AllowApproximateCurve = true;
    /// <summary>倾向携带 ICC（零转换、零误差）：ProPhoto 等超广保真路径即此。</summary>
    public bool PreferCarryIcc;
    /// <summary>
    /// **超广色域保真 ICC 的绝对路径**（非空 ⇒ 本次必须走「像素零改动 + 仅靠 ICC 描述」）。
    /// 由 `ColorIntentFactory` 用 `FfmpegCommandBuilder.ResolveFaithfulUltraWideIcc` 判定 —— 与
    /// 传统管线的判定**同源**（同一个函数），保证两条管线在"哪些源走保真"上口径一致、可 A/B 对拍。
    /// ⚠ 2026-09-16 之前规划层**没有这个概念**（只有 `PreferCarryIcc` 这个倾向标志），
    /// 于是引擎会把 ProPhoto 当普通任务归一化 ⇒ 像素被改 + 源 ICC 丢失。
    /// </summary>
    public string? FaithfulIccPath;
    /// <summary>用户显式要求映射到目标空间（高级色彩/手动）。</summary>
    public bool ForceMapping;
    /// <summary>
    /// 目标是否由用户显式指定。**false = "看齐源/auto"**：此时按无损阶梯优先选
    /// “不改像素、携带源 ICC”（L0/L1），而不是“归一化到某个标准空间”（L2/L3 有量化代价）。
    /// </summary>
    public bool TargetExplicit;
    /// <summary>无损优先：在可选时永远先试“携带 ICC/直通”，避开不必要的重编码。</summary>
    public bool PreferCarry = true;
    /// <summary>目标位深（影响精度闸门与抖动）。</summary>
    public int TargetBitDepth = 8;
    /// <summary>是否允许 GMO（广→窄色域压缩）。false 时只允许超集容器或裁切告警。</summary>
    public bool AllowGamutCompression;
    /// <summary>输出是否 HDR。</summary>
    public bool HdrOutput;
    /// <summary>源是否为 RAW（唯一允许"无标签时假定 BT.2020"的场景，项目决策 P9）。</summary>
    public bool SourceIsRaw;

    // ── 策略层（规划 §1；由 ColorIntent.ToPolicy() 填充，默认值保持与旧调用兼容）──
    /// <summary>用户声明的四大策略之一。</summary>
    public ColorStrategy Strategy = ColorStrategy.Recommended;
    /// <summary>S1 携带范围（无损优先的分级开关）。</summary>
    public CarryScope CarryScope = CarryScope.All;
    /// <summary>允许在未标注容器上产出无标注结果（TIFF 类需显式开启）。</summary>
    public bool AllowUnlabeled;
    /// <summary>本次任务要生成 GainMap ⇒ 触发 §2 策略覆盖。</summary>
    public bool GainMapRequested;
    /// <summary>用户显式请求 HDR→SDR 色调映射。</summary>
    public bool ToneMapRequested;
    /// <summary>BT.709 编码口径（Std=标准定义式且对 bt709 目标禁 H2；Zimg=对齐历史产物）。</summary>
    public Transfer709Curve Transfer709 = Transfer709Curve.Std;
    /// <summary>
    /// 本次**编码**由引擎内的外部编码器出口完成（见 <see cref="ColorTransformPlan.ExternalEncoderExit"/>）。
    /// 由 <c>ColorIntentFactory</c> 按 <c>o.EncoderBackend</c> 置位。
    /// </summary>
    public bool ExternalEncoderExit;
}

/// <summary>一次色彩处理的完整计划（引擎 L3 输出；调用方只照做，不再自行判断）。</summary>
public sealed class ColorTransformPlan
{
    public ColorAction Action;
    public ColorBackend Backend;
    public ColorSpaceDescriptor? Src;
    public ColorSpaceDescriptor? Dst;
    /// <summary>线性域 RGB→RGB 矩阵（9 项行主序）；null 表示无需矩阵。</summary>
    public double[]? Matrix;
    /// <summary>是否需要重编码传递函数（源曲线 ≠ 目标曲线）。</summary>
    public bool NeedsCurveChange;
    public TransferCurve? SrcCurve;
    public TransferCurve? DstCurve;

    // ── L5 标注 ──
    public string? OutPrimaries, OutTransfer, OutMatrix;
    public bool AttachIcc;
    public string? IccPath;
    public string? IccPrimaries, IccTrc, IccMatrix;

    // ── 精度闸门 ──
    public bool NeedsDither;
    public bool Requires16BitIntermediate;
    /// <summary>
    /// **出口位深**（8 或 16）：由策略层真驱动管道输出位宽，而不只是标志位。
    /// 规则：ColorLoss=None（直通/携带）一律 16bit 承载，绝不因“目标是 8bit 容器”而降位丢信息；
    /// 仅当确实发生变换且容器上限为 8bit 时取 8（配合 NeedsDither 做有序抖动）。
    /// </summary>
    public int OutBitDepth = 16;
    public bool GamutOutsideDestination;      // 源色域超出目标 → 需要裁切/GMO 或改超集容器

    /// <summary>
    /// 本次映射是否启用**色域压缩（GMO）**：源色域超出目标 **且** 策略允许压缩
    /// （<see cref="PlanPolicy.AllowGamutCompression"/>）**且** 目标亮度系数可取到。
    /// <para>
    /// 判据与 <c>PlanCore</c> 里的 <c>needsGmo</c>（决定必须走 H1 进程内后端）**完全同源**，
    /// 且**单一赋值点就在那里** —— 因为 <see cref="ToSpec"/> 看不到 <see cref="PlanPolicy"/>，
    /// 决策不能在换算层重算（否则同一个判据会有两份实现，正是本项目反复修过的"多处不一致"）。
    /// </para>
    /// <para>
    /// true ⇒ <see cref="ToSpec"/> 会把 <see cref="TransformSpec.GamutMap"/> 打开，
    /// 内核在**矩阵之后、钳位之前**用 <c>ColorKernels.GamutMapScalar</c> 把越界色压回目标色域
    /// （去饱和），而不是 <c>Math.Clamp</c> 硬裁切。
    /// </para>
    /// </summary>
    public bool GamutMap;

    /// <summary>
    /// GMO 的保饱和度（0..1）：0 = 优先保亮度（更灰），1 = 优先保饱和度（更暗）。
    /// 取 0.3 —— 与 libjxl 内部 CMS 路径同值（<c>lib/jxl/cms/jxl_cms_internal.h</c>），不自行调参。
    /// </summary>
    public const double GamutMapPreserveSaturation = 0.3;

    public string Reason = "";
    public string Advice = "";

    /// <summary>
    /// 本次任务的**统一裁决**（<see cref="ColorVerdict"/>）：<see cref="Action"/> /
    /// <see cref="Reason"/> / <see cref="Advice"/> 是它的**兼容投影**（形状统一、值原样搬运）。
    /// <para>
    /// 由 <c>ColorStrategyPlanning.Plan(ColorIntent)</c> 在返回前**单一投影点**写入，覆盖
    /// <c>PlanWithPolicy</c> 的**所有**返回分支（<c>PlanFaithful</c> / 各 <c>PlanXxx</c> / <c>Reject</c>）。
    /// 直接调 <c>Plan(src, dst, policy)</c> / <c>PlanCore</c> 的路径（无策略层）**不**产出裁决 ⇒ 保持 null。
    /// </para>
    /// </summary>
    public ColorVerdict? Verdict;

    /// <summary>色调映射参数（仅 HDR→SDR 场景非 null）。与 <see cref="ToSpec"/> 一起构成决策→执行的唯一换算点。</summary>
    public ToneMapParams? Tone;

    /// <summary>
    /// 本次产物的**出口结构**是 GainMap（Ultra HDR JPEG，ISO 21496-1 + XMP/MPF）。
    /// <para>
    /// 由 <see cref="PlanPolicy.GainMapRequested"/> 装配 —— 单一赋值点在
    /// <c>ColorStrategyPlanning.Plan(ColorIntent)</c> 的返回前，覆盖**所有**返回分支
    /// （含 <c>PlanFaithful</c> / 各 <c>PlanXxx</c> 的 Reject）。执行层据此选 GainMap 出口
    /// （<c>RawColorPipeline</c> 调 <c>GainMapEncoder</c>，而不是 ffmpeg 编码器）。
    /// </para>
    /// <para>
    /// ⚠ 计划本身**不描述** GainMap 的像素口径：底图色域（sRGB / Rec.2020 SDR）、分段 Reinhard
    /// 色调映射、增益图降采样、MPF/XMP 打包全部由 <c>GainMapEncoder</c> 内部负责。
    /// 因此本计划里的 <see cref="Tone"/> 与标注字段对 GainMap 出口**不适用**，
    /// 执行层会清空标注（见 <c>RawColorPipeline</c> GainMap 分支的理由）。
    /// </para>
    /// </summary>
    public bool GainMap;

    /// <summary>
    /// 本次产物的**编码**由引擎内的**外部编码器出口**完成（当前唯一实现：cjxl / JXL）。
    /// <para>
    /// ⚠ <b>与 <see cref="Backend"/> 不是一回事，切勿混用</b>：<see cref="Backend"/> 回答
    /// 「**色彩算子在哪算**」（None / FfmpegFilter / InProcess / Lut），本字段回答
    /// 「**编码谁来干**」（ffmpeg 编码器 / 外部编码器进程）。二者可以同时成立
    /// （如 jxl+Cjxl：色彩在 InProcess 算，编码交给 cjxl.exe）。
    /// </para>
    /// <para>
    /// 由 <see cref="PlanPolicy.ExternalEncoderExit"/> 装配 —— 单一赋值点在
    /// <c>ColorStrategyPlanning.Plan(ColorIntent)</c> 的返回前（与 <see cref="GainMap"/> 同一处），
    /// 覆盖**所有**返回分支。执行层据此选 cjxl 出口（<c>RawColorPipeline</c> 把变换后的 raw
    /// 经 PPM/PAM 管道喂给 cjxl，而不是交给 ffmpeg 编码器）。
    /// </para>
    /// <para>
    /// ⚠ <b>为什么必须有这个位、而不能让执行层自己看扩展名+后端</b>：出口的选择是一次
    /// **决策**（它决定产物由谁编码、色彩标注走 `-x color_space=` 还是 `-x icc_pathname=`），
    /// 而本项目的执行层被约定为「只照计划做，不再自行判断」。若让 <c>RawColorPipeline</c>
    /// 自己读 options 判后端，就会出现第二份判据（路由一份、执行一份）—— 正是本项目反复修过的
    /// 「多处不一致」缺陷形状。
    /// </para>
    /// </summary>
    public bool ExternalEncoderExit;

    // ── 策略与损失归类（规划 §2/§3；contract 自检的断言对象）──
    /// <summary>用户声明的策略。</summary>
    public ColorStrategy DeclaredStrategy = ColorStrategy.Recommended;
    /// <summary>实际生效的策略（覆盖后）。contract 断言**只看这个**。</summary>
    public ColorStrategy EffectiveStrategy = ColorStrategy.Recommended;
    /// <summary>覆盖原因；非 None 时该格被 contract 的 S2/S3 断言豁免，但必须留下记录。</summary>
    public OverrideReason Override;
    /// <summary>色彩链损失（与编解码损失无关）。</summary>
    public ColorLoss ColorLoss = ColorLoss.None;
    /// <summary>编解码损失（由容器与是否无损推断）。</summary>
    public CodecLoss CodecLoss = CodecLoss.Unknown;
    /// <summary>实际采用的标注方式。</summary>
    public AnnotChoice Annot = AnnotChoice.None;
    /// <summary>未能写 CICP 的原因（诊断）。</summary>
    public CicpUnavailableReason CicpUnavailable;
    /// <summary>未标注但按隐含 sRGB 处理（§1.2 结局 2 / §1.3）。</summary>
    public bool UnlabeledAssumedSrgb;
    /// <summary>ICC 是否来自生成器（iccgen/命名生成）。S2/S3 必须为 false。</summary>
    public bool IccFromGenerator;
    /// <summary>
    /// 无损断言说明：ColorLoss==None 时“送入编码器的 raw 帧”必须与“源解码 raw 帧”逐字节相同；
    /// 输出文件层面的差异属 <see cref="CodecLoss"/>，不得归因色彩链（规划 §3）。
    /// </summary>
    public const bool RawFrameHashInvariant = true;
    /// <summary>
    /// 建议的超集目标（如 "BT.2020"）：非空表示源色域超出用户指定的窄目标，
    /// 改投该超集 + 标准 ICC 可做到**零削色**（无损优先的常规做法）。
    /// </summary>
    public string? SuggestedSuperset;

    /// <summary>单行日志（可观测性；shadow 对比也用它）。</summary>
    public string ToLogLine()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append($"[色彩] {DeclaredStrategy}{(Override != OverrideReason.None ? $"→{EffectiveStrategy}({Override})" : "")} " +
                $"act={Action} backend={Backend} src={Src?.Name ?? "-"} dst={Dst?.Name ?? "-"}");
        if (Matrix != null) sb.Append($" mtx=[{string.Join(",", Array.ConvertAll(Matrix, v => v.ToString("0.00000", ci)))}]");
        if (NeedsCurveChange) sb.Append($" curve={SrcCurve?.Name}→{DstCurve?.Name}");
        if (Tone is { Active: true }) sb.Append($" tone=({Tone.Mode},peak={Tone.PeakNits:F0}nit,hdr={Tone.Headroom:F2}×)");
        sb.Append($" loss=({ColorLoss},{CodecLoss}) annot={Annot}");
        if (CicpUnavailable != CicpUnavailableReason.None) sb.Append($" cicpUnavailable={CicpUnavailable}");
        if (OutPrimaries != null || AttachIcc)
            sb.Append($" label=(p={OutPrimaries ?? "-"},t={OutTransfer ?? "-"},icc={(AttachIcc ? Path.GetFileName(IccPath) : "none")})");
        if (NeedsDither) sb.Append(" dither=on");
        if (Requires16BitIntermediate) sb.Append(" bit>=16");
        if (!string.IsNullOrEmpty(Reason)) sb.Append($" 理由: {Reason}");
        return sb.ToString();
    }

    public bool IsOk => Action != ColorAction.Reject;

    /// <summary>
    /// 把计划翻译为内核输入 <see cref="TransformSpec"/>——**决策与执行之间唯一的换算处**，
    /// 调用方不得自己拼 spec（否则又会出现“三路径不一致”那类缺陷）。
    ///
    /// 值域约定：源编码值 → 线性后乘以 LinearScale，把“1.0 = 源峰值”换到“1.0 = SDR 参考白”，
    /// 以便色调映射按 headroom 压缩；无 tonemap 时 LinearScale 保持 1（不改变旧行为）。
    /// </summary>
    /// <summary>
    /// 曲线线性值 1.0 的亮度语义（nits）：PQ / HLG 由**曲线定义**决定，其余取描述符声明值。
    /// 与 <c>ColorIntentFactory.FixHdrNits</c> 同口径（声明值 &gt; SDR 参考白 ⇒ 尊重调用方，如
    /// <c>--color-hdr-peak</c> 指定的实际峰值）。
    /// <para>
    /// ⚠️ 为什么要在这里再兜一次底：PQ 编码的 1.0 **恒为** 10000 nits、HLG 恒为 1000 nits（BT.2100
    /// 名义峰值），这是曲线的定义而非标注。而 `ColorSpaceDescriptor.FromCicp` 的 nits 形参默认取
    /// **SDR 参考白 203** ⇒ 凡是**直接构造描述符**的调用点（不经 ColorIntentFactory 装配，如
    /// ServiceProbe 的 xcheck）都会拿到 203 ⇒ headroom=1 ⇒ 亮度域换算与 tone mapping 全部判成
    /// "无需动作"，错误的曝光就此静默通过。
    /// </para>
    /// </summary>
    private static double NitsOfLinearOneFor(TransferCurve c, double declared)
    {
        double nominal = c.Kind switch
        {
            TransferCurve.CurveKind.Pq => TransferCurve.PqPeakNits,
            TransferCurve.CurveKind.Hlg => Bt2100Ootf.NominalPeakNits,
            _ => TransferCurve.SdrWhiteNits,
        };
        return declared > TransferCurve.SdrWhiteNits ? declared : nominal;
    }

    public TransformSpec ToSpec()
    {
        // ── ⚠ 「像素零改动」的计划必须是**恒等 spec**（2026-09-17 实测踩到，缺陷已修）────────────
        // `CarryIcc` / `None` 两个 Action 的语义就是"不改像素、只保证标注"，但这类计划**不填**
        // `SrcCurve` / `DstCurve` / `Matrix`（`PlanCarry` 是提前 return 的分支，只置
        // Action/Backend/AttachIcc/IccPath），于是会落进下面 `?? TransferCurve.Linear` /
        // `?? TransferCurve.Srgb` 这两个**猜测式默认** ⇒ 得到"按线性解码、按 sRGB 编码"的 spec。
        // **实测证据**（512x384 的 Display P3 源，jxl + Cjxl 后端）：变换输出 `.rgb48.out` 与
        // 解码输出 `.rgb48` 逐样本比对，`out == lin2srgb(in)` 的平均绝对差 **0.0000**、均值
        // 0.600 → 0.713（整幅提亮），而计划自称"像素零改动"。
        // 该缺陷长期**不可达**：这两个 Action 在 `QueueProcessor.TryRunColorEngineAsync` 里一律
        // 走"直通"分支（由 ffmpeg 重编码，引擎根本不跑变换段）⇒ 谁也没执行过这类 spec。
        // 2026-09-17 的 cjxl 执行出口让引擎**真的执行**了这类计划，缺陷才暴露。
        // ⇒ 在这里按计划自己的声明短路成恒等（**同一条曲线 + 无矩阵** ⇒ 解码/编码相互抵消），
        //   而不是去猜一条曲线。⚠ 有真实曲线的计划（Map 类，`PlanCore` 一定填了 SrcCurve/DstCurve）
        //   走不到这一支，行为逐字节不变。
        if (Action is ColorAction.CarryIcc or ColorAction.None)
        {
            return new TransformSpec
            {
                SrcCurve = TransferCurve.Srgb,
                DstCurve = TransferCurve.Srgb,
                Matrix = null,
                Dither = DitherMode.None,
                ClampLinearBeforeEncode = true,
            };
        }
        var s = new TransformSpec
        {
            SrcCurve = SrcCurve ?? TransferCurve.Linear,
            DstCurve = DstCurve ?? TransferCurve.Srgb,
            Matrix = Matrix,
            Dither = NeedsDither ? DitherMode.Ordered4x4 : DitherMode.None,
            ClampLinearBeforeEncode = true,
            Tone = Tone,
        };
        // ── 亮度域换算：解码侧 LinearScale（源→中间域） + 编码侧 EncodeScale（中间域→目标域）─────
        // 中间工作域 = 「1.0 = SDR 参考白」（tonemap 的固有值域约定，见 ToneMapParams 的类注释）；
        // 源/目标各自的归一语义见 ColorSpaceDescriptor.NitsOfLinearOne（PQ=10000、HLG=1000、SDR=203）。
        //
        // ⚠️ 旧实现只在 **tonemap 激活时** 设 LinearScale，且**完全没有**编码侧换算 ⇒
        //   · SDR→PQ：sRGB 白（1.0 = 203nits）被 PqOetf(1.0) 直接编码成 **10000nits**（过曝 49×）；
        //   · PQ↔HLG：PQ 归一 1.0=10000、HLG 归一 1.0=1000，缺换算 ⇒ 亮度差 **10 倍**；
        //   · 无 tonemap 的 PQ→SDR：整体**暗 49×**（xcheck 的「PQ 归一约定不同」格即此症状，PSNR 7.3dB）。
        //   这些平时不显形，只因多数 HDR 任务走 H2（zscale 自己会换算）；一旦 GMO 或不可命名空间
        //   落到 H1 就会产出错曝光的图 —— 而 H1 恰恰是自称"精确"的那条路径。
        // ⇒ 两侧都换算后，SDR→SDR 恒为 1（不动既有基线），HDR→SDR + tonemap 亦与旧行为逐位一致。
        double refWhite = Tone is { Active: true } ? Math.Max(1.0, Tone.SdrWhiteNits)
                                                  : TransferCurve.SdrWhiteNits;
        // nits 语义以**曲线定义**为准（见 NitsOfLinearOneFor），不依赖调用方是否记得传参。
        double srcNits = Src == null ? 0.0 : NitsOfLinearOneFor(Src.Curve, Src.NitsOfLinearOne);
        double dstNits = Dst == null ? 0.0 : NitsOfLinearOneFor(Dst.Curve, Dst.NitsOfLinearOne);
        if (srcNits > 0 && Math.Abs(srcNits - refWhite) > 1e-9)
            s.LinearScale = srcNits / refWhite;
        if (dstNits > 0 && Math.Abs(dstNits - refWhite) > 1e-9)
            s.EncodeScale = refWhite / dstNits;
        // HLG 是 scene-referred：解码后需经 BT.2100 OOTF（场景光 → 显示光），PQ 不需要。
        // 名义峰值与 PQ 的 nits 归一同源：描述符的 NitsOfLinearOne（HLG=1000，见 ColorIntentFactory.FixHdrNits）。
        if (Src != null && Src.Curve.Kind == TransferCurve.CurveKind.Hlg)
        {
            s.SrcIsHlgScene = true;
            s.SrcHlgSystemGamma = Bt2100Ootf.SystemGamma(Src.NitsOfLinearOne);
        }
        if (Dst != null && Dst.Curve.Kind == TransferCurve.CurveKind.Hlg)
        {
            s.DstIsHlgScene = true;
            s.DstHlgSystemGamma = Bt2100Ootf.SystemGamma(Dst.NitsOfLinearOne);
        }
        // ── 色域映射（GMO）：决策已在 PlanCore 单点置位（见 GamutMap 的说明），此处只做换算 ──
        // 目标亮度系数取不到时**不猜**：保持 GamutMap=false（内核退回硬裁切）；
        // 此时 PlanCore 已把原因写进 Reason（"目标色域亮度系数不可得"），日志能看出为什么没压。
        if (GamutMap)
        {
            var y = LuminancesOf(Dst);
            if (y != null)
            {
                s.GamutMap = true;
                s.DstLuminances = y;
                s.GamutMapPreserveSaturation = GamutMapPreserveSaturation;
            }
        }
        return s;
    }

    /// <summary>
    /// 从**目标**描述符取亮度系数 [Yr, Yg, Yb] = 其 RGB→XYZ(D50) 矩阵的 **Y 行**。
    /// <para>
    /// <b>下标推导</b>（行主序 9 项，X/Y/Z 各一行）：
    /// <code>
    /// X = m[0]·R + m[1]·G + m[2]·B
    /// Y = m[3]·R + m[4]·G + m[5]·B   ← 亮度行（Y 即 CIE 亮度）
    /// Z = m[6]·R + m[7]·G + m[8]·B
    /// </code>
    /// ⇒ 下标 **3/4/5**。这与 <c>ColorKernels</c> 里矩阵乘法的 <c>m3/m4/m5</c> 是同一行
    /// （内核用 <c>gg = m3*r + m4*g + m5*b</c> 算输出 G —— 在 RGB→XYZ 里它就是 Y 行）。
    /// </para>
    /// <para>
    /// 语义与 libjxl 的 <c>primaries_luminances</c> 一致：矩阵由白点归一定义，(1,1,1) 的 Y 像应为 1，
    /// 因此三项之和 ≈ 1（这也是本项目 <c>ColorSpaceDescriptor</c> 矩阵的既有性质）。
    /// ⚠ 这里用的是 **D50 适应后**的 Y 行：白点适应是 XYZ 上的线性变换，Y 行会与 D65 原生的
    /// 亮度系数有差异。**实测**（Bradford 适应的 sRGB→XYZ(D50)）：
    /// D65 原生 [0.2126, 0.7152, 0.0722] vs 本项目 [0.222494, 0.716876, 0.060630]
    /// ⇒ 逐项差 **~1e-2**（红 +0.010、蓝 −0.012，不是 1e-3 量级）。
    /// 该差异只影响"向哪个同亮度灰混合"的权重（算子仍严格 no-op、仍把越界色压回 [0,1]），
    /// 且行和仍精确为 1（= 白点 Y），作为亮度权重完全成立；代价是与 libjxl 用 D65 系数
    /// 的逐值结果有约 1e-2 量级的权重差（**不是**逐值复刻，见报告的不确定点）。
    /// </para>
    /// 取不到（无矩阵 / 长度 ≠ 9 / 非有限 / 退化）⇒ 返回 null，调用方**不得猜**。
    /// </summary>
    public static double[]? LuminancesOf(ColorSpaceDescriptor? d)
    {
        if (d?.RgbToXyzD50 is not { Length: 9 } m) return null;
        double yr = m[3], yg = m[4], yb = m[5];
        if (!double.IsFinite(yr) || !double.IsFinite(yg) || !double.IsFinite(yb)) return null;
        // 行和必须≈1（白点 Y）：偏得太远说明矩阵不是"白点归一"的 RGB→XYZ，不能拿来当亮度权重
        if (Math.Abs(yr + yg + yb - 1.0) > 0.05) return null;
        return new[] { yr, yg, yb };
    }
}

/// <summary>
/// 色彩映射引擎（规划层）。**一个大脑**：所有"要不要映射、映射到什么、怎么标注、能否近似"的决策
/// 集中在此，调用方（ffmpeg 直接路径 / cjxl 管道 / ICC 烘焙 / GainMap / 预览 / 质量分析）只照做。
/// </summary>
public static partial class ColorMappingEngine
{
    /// <summary>JXL 的 color_space 枚举集（cjxl 无 SDR-BT.2020 枚举，实测）。</summary>
    private static readonly string[] JxlColorSpaceEnums = { "sRGB", "DisplayP3", "Rec2100PQ", "Rec2100HLG" };

    /// <summary>容器能力表（实测事实；新增格式在此登记，禁止在调用方各自 if-else）。</summary>
    /// <remarks>
    /// 未标注语义、JXL 枚举集、ICC/CICP 双写能力均来自本仓库实测；E1（AVIF rICC）与
    /// E2（JXL 双写）结论前，相关格保持保守值（规划 §5′.5 / §8 P3.5b 退出门禁）。
    /// </remarks>
    private static readonly Dictionary<string, FormatColorCaps> Caps = new(StringComparer.OrdinalIgnoreCase)
    {
        // exiftool 可后置写 ICC（PNG/JPEG/TIFF/WebP ✅），无 CICP
        // ⚠ `CanCicp = true`（2026-09-16 修正）：PNG **有** cICP 通路 —— 应用由
        // `PngCicpService` 在编码后**后置写入** cICP 块（实测产物字节 `09 12 00 01`：
        // primaries=9/transfer=18|16/matrix=0/full=1），且 `HasColorChunk` 能读回 hasCicp。
        // 旧值（默认 false）让规划层以为 PNG 无法表达 HDR ⇒ S2/S3 对 HDR→PNG **误拒**
        //（实测 verify-color-strategy 的 carry/cicp-only/manual × hdr-png 三格无输出）。
        // ⚠ `CanHaveBothIccAndCicp = true`（同日第二处修正）：PNG 的 iCCP（exiftool/iccgen 后置）
        // 与 cICP（PngCicpService 后置）是**不同的 chunk，可以共存** ⇒ 不该被迫"择一"。
        // 若误设 false，规划层会走"择一"分支：对 sRGB 目标选 CICP 而**丢掉 ICC**，
        // 观感上等于降级（老查看器只认 ICC）—— 实测 verify-color-wiring 的 (2)(6) 两格因此转红。
        // ⚠ `CanHdr = true`（同日第三处修正）：PNG 经 **cICP** 就能承载 PQ/HLG（两条管线的
        // HDR→PNG 产物实测都写着 `smpte2084,bt2020`）。若误设 false，会连带触发
        // `ColorIntentFactory` 的"HDR 源 + 容器不支持 HDR ⇒ 自动 hable tonemap" ⇒ 引擎把
        // HDR **降成 SDR**，而传统管线**保 HDR** ⇒ 两条管线对同一输入给出不同产物
        //（实测 recommended-hdr-png：引擎 53926B/ICC=True vs legacy 43821B/ICC=False）。
        ["png"]   = new() { Format = "png",   CanCarryArbitraryIcc = true,  CanGenerateIccFromCicp = true,  CanCicp = true, CanHdr = true, MaxBitDepth = 16, RgbNative = true,
                            HdrRequiresHighBitDepth = true,
                            UnlabeledMeansSrgb = true, CanHaveBothIccAndCicp = true, VerifiedBy = "exiftool iCCP + PngCicpService 实测 cICP（两 chunk 可共存、HDR 经 cICP 承载）" },
        ["apng"]  = new() { Format = "apng",  CanCarryArbitraryIcc = true,  CanGenerateIccFromCicp = true,  CanCicp = true, CanHdr = true, MaxBitDepth = 16, RgbNative = true,
                            HdrRequiresHighBitDepth = true,
                            UnlabeledMeansSrgb = true, CanHaveBothIccAndCicp = true, VerifiedBy = "exiftool iCCP + PngCicpService 实测 cICP（两 chunk 可共存、HDR 经 cICP 承载）" },
        ["jpg"]   = new() { Format = "jpg",   CanCarryArbitraryIcc = true,  CanGenerateIccFromCicp = true,  MaxBitDepth = 8,
                            UnlabeledMeansSrgb = true, CanHaveBothIccAndCicp = false, SupportsGainMap = true, VerifiedBy = "exiftool APP2" },
        ["jpeg"]  = new() { Format = "jpeg",  CanCarryArbitraryIcc = true,  CanGenerateIccFromCicp = true,  MaxBitDepth = 8,
                            UnlabeledMeansSrgb = true, CanHaveBothIccAndCicp = false, SupportsGainMap = true, VerifiedBy = "exiftool APP2" },
        ["jpegli"]= new() { Format = "jpegli",CanCarryArbitraryIcc = true,  CanGenerateIccFromCicp = true,  MaxBitDepth = 8,
                            UnlabeledMeansSrgb = true, CanHaveBothIccAndCicp = false, SupportsGainMap = true, VerifiedBy = "cjpegli/exiftool" },
        // TIFF 可携带 ICC，但“未标注即 sRGB”不成立（查看器行为不一致）⇒ UnlabeledMeansSrgb=false
        ["tiff"]  = new() { Format = "tiff",  CanCarryArbitraryIcc = true,  CanGenerateIccFromCicp = true,  MaxBitDepth = 16, RgbNative = true,
                            UnlabeledMeansSrgb = false, CanHaveBothIccAndCicp = false, NoCicpReason = CicpUnavailableReason.ContainerNonNormative,
                            VerifiedBy = "exiftool 后置写（须在元数据恢复之后）" },
        ["tif"]   = new() { Format = "tif",   CanCarryArbitraryIcc = true,  CanGenerateIccFromCicp = true,  MaxBitDepth = 16, RgbNative = true,
                            UnlabeledMeansSrgb = false, CanHaveBothIccAndCicp = false, NoCicpReason = CicpUnavailableReason.ContainerNonNormative,
                            VerifiedBy = "exiftool" },
        ["webp"]  = new() { Format = "webp",  CanCarryArbitraryIcc = true,  CanGenerateIccFromCicp = true,  MaxBitDepth = 8,
                            UnlabeledMeansSrgb = true, CanHaveBothIccAndCicp = false, EncoderWritesGeneratedIcc = false,
                            VerifiedBy = "exiftool ICCP；libwebp 丢弃 iccgen 的 ICC（实测）⇒ ICC 只能后置嵌入" },
        // JXL：exiftool 写不进 ICC，但 cjxl -x icc_pathname 可以；CICP 枚举仅 sRGB/DisplayP3/Rec2100PQ/HLG
        // E2 实测（cjxl+icc_pathname 与 color_space=DisplayP3 同给）：产物与“仅 ICC”**字节完全相同**，
        // jxlinfo 只报 584B ICC → **不能共存，ICC 胜出**（旧结论由此钉实）。
        // ✅ 2026-09-26（任务 #37）把 `CanGenerateIccFromCicp` 翻成 **true**：E2 那条证据证的是
        //   "**同一文件里 ICC 与 CICP 不可共存**"，从前被当成"**不能由 CICP 生成 ICC**"——两件事。
        //   本次实测（`tests/output/t35/probe-jxl-bt2020-icc.ps1`，按产品真实通路喂 **PPM**）：
        //   加 `-x icc_pathname=<iccgen 生成的 2020 ICC>` ⇒ jxlinfo 报 `584-byte ICC profile (lcms)`，
        //   解码回来的 PNG 色度 = 0.708/0.292、0.170/0.797、0.131/0.046（= BT.2020/D65）；
        //   不加 ⇒ jxlinfo 报 sRGB、解码 PNG **无** ICC；两次解码像素 rgb48 sha 相同 ⇒ ICC 不改像素。
        //   ⚠ 本位在现存消费者里与 `CanCarryArbitraryIcc` 是**或**关系（:707/:877/:917）⇒ 翻它本身不改变
        //     任何一格的结局；24 格 `jxl × BT.2020` 被拒的真凶是 `EnforceContainerAnnotationRules` 的
        //     **择一**（它留着容器写不出的 CICP、剥掉唯一能兑现的生成 ICC），已在那一处修。
        //     翻位是把表**改诚实**，不是当修复用。
        //     ⚠ 那条修法**必须按出口判**：`CicpSpaceEnums` 钉的是 **cjxl** 的 `-x color_space=` 宽度，
        //       而 ffmpeg 的 libjxl 出口能写任意 CICP（实测 `-e Ffmpeg` + DCI-P3 ⇒ jxlinfo 读回
        //       「Rec.2100 primaries, 709 transfer」，被 `verify-color-wiring` (10) 钉住）
        //       ⇒ 无条件按枚举剥 CICP 会把那条本来能交付的路打成 **0 字节产物**。
        //       本表**没有"后端/出口"这一维**就是这个坑的根（登记任务 #38）；现在由调用方
        //       把出口标志（`PlanPolicy.ExternalEncoderExit`）递进择一判据。
        ["jxl"]   = new() { Format = "jxl",   CanCarryArbitraryIcc = true,  CanCicp = true, CanGenerateIccFromCicp = true,  MaxBitDepth = 32, RgbNative = true, CanHdr = true,
                            UnlabeledMeansSrgb = false, CanHaveBothIccAndCicp = false, ExternalOutletCicpSpaceEnums = JxlColorSpaceEnums,
                            VerifiedBy = "E2：ICC 与 CICP 不可共存（ICC 胜出）；09-26 PPM 实测：生成 ICC 可写入并读回 BT.2020 色度、像素逐字节不变" },
        // E1 实测：avifenc 能把任意 ICC 写入 AVIF（内嵌直通 与 显式 --icc 均成功，584B ICC 读回）；
        // 且 --icc + --nclx P/T/M 产出的同一文件里 colr 的 nclx 与 ICC **共存**（exiftool 两者均可见）。
        // ⚠ --lossless 下 avifenc 强制 matrix=identity(0)，传 6/9 会报“Matrix coefficients have to be identity…”。
        ["avif"]  = new() { Format = "avif",  CanCarryArbitraryIcc = true,  CanCicp = true, CanGenerateIccFromCicp = true,  MaxBitDepth = 12, CanHdr = true,
                            UnlabeledMeansSrgb = false, CanHaveBothIccAndCicp = true, LosslessForcesIdentityMatrix = true,
                            VerifiedBy = "E1 实测：avifenc --icc ✅ / --icc+--nclx 共存 ✅；exiftool 不能后置写" },
        // ❗ heic/heif：**实测本工具链完全无法写入**（ffmpeg: Unknown format 'heif'，无 heif muxer；无 heifenc/libheif）
        // → 能力必须按“不可表达”给（全部保守），否则引擎会为 HEIF 产出“看似可行”的计划。
        ["heic"]  = new() { Format = "heic",  CanCarryArbitraryIcc = false, CanCicp = false, CanGenerateIccFromCicp = false, MaxBitDepth = 8,
                            UnlabeledMeansSrgb = false, CanHaveBothIccAndCicp = false,
                            VerifiedBy = "❌ 实测：无 heif muxer / 无 heifenc → 根无写入通路（产品侧任务会失败）" },
        ["heif"]  = new() { Format = "heif",  CanCarryArbitraryIcc = false, CanCicp = false, CanGenerateIccFromCicp = false, MaxBitDepth = 8,
                            UnlabeledMeansSrgb = false, CanHaveBothIccAndCicp = false,
                            VerifiedBy = "❌ 实测：同 heic，无写入通路" },
        // 只读/中间格式与少见目标：保守能力
        // ⚠ JXR（2026-09-17 接入引擎出口轮）：**能力位本轮有意不动**（仍 `CanCarryArbitraryIcc=false`）。
        //   实测补充（供下一轮裁定，不改本表）：exiftool **可以**把 ICC 写进 .jxr 且**逐字节读回**
        //   （584B / MD5 与源一致），写完 `JxrDecApp` 仍能解码；`EXIF:ColorSpace=Uncalibrated` 与
        //   `ImageDescription` 同样可读回。⇒ 旧注「JxrService 无 ICC 通路」描述的是**服务**（确实没有
        //   ICC 参数），**不等于容器不行**。之所以不直接翻成本表的能力位：翻它会同时改变 jxr 在
        //   S1/S2/S3/S4 的规划结局（并波及 legacy 专用管线 ProcessJxrAsync，那条路**没有** ICC 后置）
        //   ⇒ 属"能力扩张"，需要独立的评审与门禁，不在本轮（引擎出口）范围内。
        ["jxr"]   = new() { Format = "jxr",   CanCarryArbitraryIcc = false, CanCicp = false, MaxBitDepth = 16, RgbNative = true,
                            UnlabeledMeansSrgb = false, CanHaveBothIccAndCicp = false,
                            VerifiedBy = "保守（未翻位）：JxrService 无 ICC 参数；⚠ 实测容器本身可经 exiftool 携带 ICC（见上注）" },
        ["gif"]   = new() { Format = "gif",   CanCarryArbitraryIcc = false, CanCicp = false, MaxBitDepth = 8, RgbNative = true,
                            UnlabeledMeansSrgb = false, CanHaveBothIccAndCicp = false, VerifiedBy = "调色板，无法标注" },
        ["ppm"]   = new() { Format = "ppm",   CanCarryArbitraryIcc = false, CanCicp = false, MaxBitDepth = 16, RgbNative = true,
                            UnlabeledMeansSrgb = false, CanHaveBothIccAndCicp = false, VerifiedBy = "裸流中间格式" },
    };

    public static FormatColorCaps CapsFor(string? format)
    {
        var f = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (Caps.TryGetValue(f, out var c)) return c;
        return new FormatColorCaps { Format = f, MaxBitDepth = 8 };      // 未知格式：最保守能力
    }

    /// <summary>矩阵缓存（同一 src/dst 对反复出现，避免重复解算与求逆）。</summary>
    private static readonly Dictionary<string, double[]?> MatrixCache = new();
    private static readonly object CacheLock = new();

    /// <summary>按容器与是否请求无损推断**编解码**损失（与色彩链无关，规划 §3）。</summary>
    public static CodecLoss CodecLossOf(string? format, bool losslessRequested) => (format ?? "").ToLowerInvariant() switch
    {
        "gif" => CodecLoss.Palette,
        "png" or "apng" or "tiff" or "tif" or "ppm" => CodecLoss.Lossless,
        "jxl" => losslessRequested ? CodecLoss.Lossless : CodecLoss.VisuallyLossless,
        "jxr" => losslessRequested ? CodecLoss.Lossless : CodecLoss.VisuallyLossless,
        "webp" => losslessRequested ? CodecLoss.Lossless : CodecLoss.Lossy,
        "avif" or "heif" or "heic" => losslessRequested ? CodecLoss.Lossless : CodecLoss.Lossy,
        // ⚠ 更正（2026-09-19）：JPEG/jpegli **没有**无损通路 ⇒ 恒为 Lossy。
        //   此前写 `losslessRequested ? Lossless : Lossy` 是**谎报**：本工具链唯一无损 JPEG
        //   通路是 DNG 容器压缩（`--format dng --dng-compression 0`），`--lossless true` 配
        //   `--format jpg` 会被静默忽略（见 FfmpegCommandBuilder / ImageEncoderArgs 的告警）。
        "jpg" or "jpeg" or "jpegli" => CodecLoss.Lossy,
        _ => CodecLoss.Unknown,
    };

    /// <summary>
    /// 「这条 (primaries, transfer) 本次**写不写得进**出口标注」的唯一判据。
    /// <para>
    /// ⚠ 第三个参数**必须显式传**（任务 #38）：<see cref="FormatColorCaps.ExternalOutletCicpSpaceEnums"/>
    ///   钉的是**外部编码器出口**（cjxl 的 <c>-x color_space=</c>）的枚举宽度，**不是容器能力**。
    ///   给 <c>false</c>（ffmpeg 出口）⇒ 只要 <c>CanCicp</c> 就按"自由 nclx"放行。
    ///   刻意**不给默认值**：漏传就是编译不过，而不是悄悄沿用"容器 = cjxl 的限制"这个错前提。
    /// </para>
    /// </summary>
    public static bool CapsAcceptsCicp(FormatColorCaps caps, ColorSpaceDescriptor? d, bool externalEncoderOutlet)
    {
        if (!caps.CanCicp || d == null || !d.IsCicpNameable) return false;
        // 枚举集只约束外部出口；ffmpeg 的各容器出口写的是真 nclx（jxl 走 libjxl 时同理）
        if (caps.ExternalOutletCicpSpaceEnums == null || !externalEncoderOutlet) return true;   // 自由 nclx（AVIF/HEIF）
        // JXL：枚举名由 (primaries, transfer) 对决定
        bool hdr = d.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;
        if (!hdr)
        {
            if (d.PrimariesToken == "bt709" && d.Curve.CicpToken == "iec61966-2-1")
                return Array.IndexOf(caps.ExternalOutletCicpSpaceEnums, "sRGB") >= 0;
            if (d.PrimariesToken == "smpte432" && d.Curve.CicpToken == "iec61966-2-1")
                return Array.IndexOf(caps.ExternalOutletCicpSpaceEnums, "DisplayP3") >= 0;
            return false;                                                    // SDR-BT.2020 等无枚举
        }
        var want = d.Curve.Kind == TransferCurve.CurveKind.Pq ? "Rec2100PQ" : "Rec2100HLG";
        return d.PrimariesToken == "bt2020" && Array.IndexOf(caps.ExternalOutletCicpSpaceEnums, want) >= 0;
    }

    /// <summary>
    /// 核心决策（阶梯 L0..L4 + 后端代价 + 标注）。**不含策略约束**，由策略层（<c>ColorStrategyPlanning</c>）调用。
    /// 该方法**不做任何 IO**，可安全并发调用。
    /// </summary>
    public static ColorTransformPlan Plan(ColorSpaceDescriptor? src, ColorSpaceDescriptor? dst, PlanPolicy policy)
        => PlanCore(src, dst, policy);
    private static ColorTransformPlan PlanCore(ColorSpaceDescriptor? src, ColorSpaceDescriptor? dst, PlanPolicy policy)
    {
        policy ??= new PlanPolicy();
        var p = new ColorTransformPlan { Src = src, Dst = dst };

        // ── 无目标：看齐源（不映射），标注沿用源；若源有 ICC 且格式支持则携带 ──
        if (dst == null)
        {
            p.Action = ColorAction.None;
            p.Reason = "无目标色彩空间 → 不改像素，沿用源描述";
            if (src != null) SetLabelsFrom(p, src, policy, preferCarry: true);
            p.Reason += Transfer709Note(policy, null, p.Action, false);
            return p;
        }

        p.DstCurve = dst.Curve;
        p.SrcCurve = src?.Curve;

        // ── 源不可识别：绝不允许"猜一个色域写标签" ──
        if (src == null || !src.IsMappable)
        {
            p.Action = ColorAction.Reject;
            p.Reason = "源色彩无法确定（缺 ICC 且无 CICP 标签）→ 不映射、不写误导标签";
            p.Advice = "为源文件指定色彩空间，或先由源携带 ICC";
            p.Reason += Transfer709Note(policy, dst.TransferToken, p.Action, false);
            return p;
        }

        p.NeedsCurveChange = !src.SameCurveAs(dst);

        // ── 等价：完全跳过（性能护栏：绝不为"统一"而做无谓映射）──
        if (src.Equivalent(dst) && !policy.ForceMapping)
        {
            p.Action = ColorAction.None;
            p.Reason = "源=目标（矩阵与曲线等价）→ 直通，仅保证标注正确";
            SetLabelsFrom(p, dst, policy, preferCarry: src.IsIccDerived);
            p.Reason += Transfer709Note(policy, dst.TransferToken, p.Action, false);
            return p;
        }

        // ── 求线性域矩阵（经 XYZ_D50 复合，含白点适应）──
        var m = src.MatrixTo(dst);
        if (m == null)
        {
            p.Action = ColorAction.Reject;
            p.Reason = $"无法求出 src→dst 矩阵（色度未知、矩阵奇异或白点不守恒 dev={src.LastRowSumDeviation:E1}）";
            p.Advice = "改用可携带 ICC 的格式（PNG/TIFF/WebP/JXL），或补全色彩描述";
            p.Reason += Transfer709Note(policy, dst.TransferToken, p.Action, false);
            return p;
        }
        p.Matrix = MatrixIsIdentity(m) ? null : m;

        // ── 曲线可表达性：引擎能精确表达已注册曲线（含表曲线）；若一侧不可用且不允许近似 → 携带或拒绝 ──
        bool srcCurveOk = CurveUsable(src.Curve) && CurveUsable(dst.Curve);
        // H2（zscale）可用条：两侧 primaries 与曲线均能按 CICP 命名（否则 zscale 无法表达该空间）
        bool h2Possible = src.IsCicpNameable && dst.IsCicpNameable;
        // 口径黑名单（实测）：zimg 对 bt709=纯 γ2.4，与标准定义式分歧；bt470bg/smpte240m 未验证
        // ⇒ 除非用户显式要求对齐 zimg（Transfer709=Zimg），否则**强制走 H1**，避免“标签说 A、像素是 B”。
        bool zimgDivergent = ZimgDivergentTransfer(dst.TransferToken);
        if (zimgDivergent && policy.Transfer709 == Transfer709Curve.Std) h2Possible = false;
        // ── 口径落到像素（缺陷 #29 的前半边：`--color-709-curve` 在 H1 格子上曾经**静默无效**）──────
        // 上面那条只回答"这格交给谁算"；结构上必须走 H1 的格子（需 GMO / 曲线不可 CICP 命名 /
        // 已拿到 raw 像素）里，std 与 zimg 曾经拿到**逐字节相同**的产物（2026-09-26 实测四格 ×
        // {std,zimg} 全数 md5 相同）⇒ 用户显式选了口径，开关却没有作用对象。
        // 这里补上作用对象：Zimg 口径下把**出口编码曲线**换成 zimg 的实现形态（纯 γ(1/2.4)，
        // 见 <see cref="TransferCurve.Bt709Zimg"/>），于是 H1 与 H2（zscale=t=bt709 本就是 γ2.4）
        // 给出同一种像素 —— 这才是"对齐历史产物"的字面意思。
        // ⚠ 只有 **bt709** 有实测背书：<see cref="ZimgDivergentTransfer"/> 还收了 bt470bg/smpte240m，
        //   但它们的 zimg 形态**未验证** ⇒ 一律不换曲线（宁可不兑现，也不发明一条传递特性），
        //   改由 <see cref="Transfer709Note"/> 在裁决理由里点名"本格不适用 + 原因"（不许静默）。
        // ⚠ 出口**标注**一个字都不动（仍由目标描述符给 bt709）：CICP/ICC 的 bt709 = 定义式，
        //   像素与标签不同形是 zimg 口径的**既有属性**（H2 产物一直是这样），不是本次新引入的失配。
        bool zimgExitApplied = false;
        if (policy.Transfer709 == Transfer709Curve.Zimg)
        {
            var zimgExit = ZimgExitCurve(dst.TransferToken);
            if (zimgExit != null) { p.DstCurve = zimgExit.Value; zimgExitApplied = true; }
        }
        if (!srcCurveOk)
        {
            if (!policy.AllowApproximateCurve)
            {
                p.Action = policy.Format.CanCarryArbitraryIcc && src.SourceIccPath != null ? ColorAction.CarryIcc : ColorAction.Reject;
                p.IccPath = p.Action == ColorAction.CarryIcc ? src.SourceIccPath : null;
                p.Reason = "存在引擎无法精确表达的曲线（如 A2B/非中性 profile）→ " +
                           (p.Action == ColorAction.CarryIcc ? "携带源 ICC，不动像素" : "拒绝（格式也不能带 ICC）");
                p.Advice = p.Action == ColorAction.CarryIcc ? "" : "改用 PNG/TIFF/WebP/JXL 目标以携带 ICC";
                p.Reason += Transfer709Note(policy, dst.TransferToken, p.Action, zimgExitApplied);
                return p;
            }
            p.Reason = "曲线不精确但策略允许近似（legacy 兼容路径）";
        }

        // ── 色域包含关系与精度闸门 ──
        p.GamutOutsideDestination = !GamutContainedIn(src, dst);
        if (p.GamutOutsideDestination && !policy.AllowGamutCompression && !policy.ForceMapping)
        {
            // 项目既有策略：优先"归入超集容器 + 标注"，不做有损压缩
            p.Reason = "源色域超出目标且未开启 GMO → 由调用方选择超集/携带/裁切";
        }
        p.NeedsDither = policy.TargetBitDepth <= 8 && (p.Matrix != null || p.NeedsCurveChange);
        p.Requires16BitIntermediate = Needs16Bit(dst, policy);

        p.Action = ColorAction.Map;

        // ── 后端选择（代价模型；见方案 §2.1）──
        // 只有“需要 GMO（色域压缩）”时才强制走 H1 —— GMO 是引擎专有算子；
        // 色域越界本身不影响 zscale 能力（它做同一矩阵 + 钳位），不能因此放弃 H2。
        bool needsGmo = p.GamutOutsideDestination && policy.AllowGamutCompression;
        // ── GMO 的执行决策：**单点置位**（ToSpec 看不到 PlanPolicy，不能在换算层重算判据）──
        // 判据 = needsGmo（`GamutOutsideDestination && AllowGamutCompression`）**且**目标亮度系数可取。
        // 取不到就**不猜**：关掉映射退回硬裁切，并把原因写进 Reason（否则会变成"开了开关却没效果"的静默失效）。
        // ⚠ needsGmo=true 必然落在下面的 InProcess 分支（H2 分支的条件就是 `!needsGmo`）⇒ 此处置位不会
        //   与"交给 zscale"的后端选择打架。
        var gmoLuminances = needsGmo ? ColorTransformPlan.LuminancesOf(dst) : null;
        p.GamutMap = needsGmo && gmoLuminances != null;
        if (needsGmo && gmoLuminances == null)
            p.Reason += " ⚠️ 色域压缩未启用：目标色域亮度系数不可得（缺 RGB→XYZ(D50) 矩阵/退化）→ 退回硬裁切";
        if (p.Matrix == null && !p.NeedsCurveChange)
        {
            p.Action = ColorAction.None; p.Backend = ColorBackend.None;
            p.Reason = "仅需改标注（像素等价）";
        }
        else if (h2Possible && !needsGmo)
        {
            // 两侧都能按 CICP 精确命名 → 交给 zscale：省两次进程启动与 raw 管道带宽
            p.Backend = ColorBackend.FfmpegFilter;
            p.Reason += " 后端=H2(zscale 可精确命名两侧曲线，省进程与管道)";
        }
        else
        {
            // 曲线不可命名 / 需 GMO / 已在像素域 → 只能进引擎
            p.Backend = ColorBackend.InProcess;
            p.Reason += needsGmo ? " 后端=H1(需 GMO 色域压缩)" : " 后端=H1(进程内融合内核)";
            if (zimgDivergent && policy.Transfer709 == Transfer709Curve.Std)
                p.Reason += $" [口径黑名单: zimg 的 {dst.TransferToken} 实为纯 γ2.4，与标准定义式分歧 → 禁 H2]";
        }

        SetLabelsFrom(p, dst, policy, preferCarry: false);
        p.Reason += Transfer709Note(policy, dst.TransferToken, p.Action, zimgExitApplied, p.Backend);
        return p;
    }

    /// <summary>目标曲线是否与 zimg 实现分歧/未验证（这些目标不得交给 H2，除非显式对齐 zimg）。</summary>
    internal static bool ZimgDivergentTransfer(string? trcToken) => trcToken is "bt709" or "bt470bg" or "smpte240m";

    /// <summary>
    /// 该 CICP transfer 在 **zimg 口径**下有没有**实测背书**的替代出口曲线。
    /// <para>
    /// 只有 `bt709` 有（实测 zimg 把它实现成纯 γ(1/2.4)，与定义式差 ~28.1 LSB@8bit）。
    /// `bt470bg` 本仓已是纯幂 γ2.8（无"定义式 vs 纯幂"之分），`smpte240m` 的 zimg 形态未实测
    /// ⇒ 两者一律返回 null：**不换曲线、也不发明曲线**，由 <see cref="Transfer709Note"/> 点名"本格不适用"。
    /// </para>
    /// </summary>
    internal static TransferCurve? ZimgExitCurve(string? trcToken)
        => trcToken == "bt709" ? TransferCurve.Bt709Zimg : null;

    /// <summary>
    /// `--color-709-curve` 的**兑现情况点名**（缺陷 #29 的后半边：结构上换不了曲线的格子**不许静默**）。
    /// <para>
    /// 为什么只在 <c>Zimg</c> 出声：<c>Std</c> 就是本仓一直以来的出口曲线（ITU-R 定义式），
    /// 逐格播报"默认值已生效"只是日志噪声；而 CLI 的默认值就是 `"std"`
    /// （<c>FfmpegOptions.Color709Curve</c>），装配层**无从区分**"用户显式选了 std"与"用户没选"
    /// —— 要区分得让 CliParser 记录"是否被显式赋值"，不在本文件的归属内（已登记进本轮报告）。
    /// </para>
    /// <para>
    /// ⚠ 文案一律写成**单行**且以 `[...]` 起头：`_probe-cjk-hardcode-scan.ps1` 按**词法**分桶
    /// （一行的首个中文字面量以 <c>[</c> 开头 ⇒ 编码日志桶，不参与"UI 面不得增加"的判据），
    /// 折行或换前缀都会把这条诊断推进判据桶 ⇒ 棘轮立刻转红。
    /// </para>
    /// </summary>
    /// <param name="dstTrc">目标 CICP transfer token（无目标时传 null）。</param>
    /// <param name="action">本计划的最终 Action（非 Map ⇒ 根本没有出口编码）。</param>
    /// <param name="exitCurveApplied">出口曲线是否真的被换成 zimg 形态。</param>
    /// <param name="backend">像素由谁算（H1 用我们的曲线，H2 用 zscale —— 两者在 zimg 口径下同形，但归因不同）。</param>
    internal static string Transfer709Note(PlanPolicy policy, string? dstTrc, ColorAction action,
        bool exitCurveApplied, ColorBackend backend = ColorBackend.None)
    {
        if (policy.Transfer709 != Transfer709Curve.Zimg) return "";
        if (policy.GainMapRequested)
            return " [口径=zimg 未作用于本格：GainMap 出口的底图编码由 GainMapEncoder 自持，计划层的出口曲线不被消费]";
        if (exitCurveApplied && action == ColorAction.Map)
            return backend == ColorBackend.FfmpegFilter
                ? " [口径=zimg 已兑现(H2)：像素由 zscale 给出，其 t=bt709 本就是纯 γ(1/2.4) —— 本开关正是为此放行 H2（Std 侧由口径黑名单禁掉）]"
                : " [口径=zimg 已兑现(H1)：出口曲线改用 zimg 的 BT.709 形态（纯 γ(1/2.4)，与 ITU-R 定义式差 ~28.1LSB@8bit）；标注仍按目标描述符写 bt709 —— 与 zscale=t=bt709 的产物同形，即本开关的用途]";
        if (action != ColorAction.Map)
            return $" [口径=zimg 未作用于本格：本次 act={action} 不改像素 ⇒ 没有出口编码可换曲线]";
        if (ZimgDivergentTransfer(dstTrc))
            return $" [口径=zimg 未作用于本格：出口 trc={dstTrc} 的 zimg 实现形态未实测 ⇒ 不换曲线（宁可不兑现也不发明传递特性），本开关只在放行 H2 上生效]";
        return $" [口径=zimg 未作用于本格：出口 trc={dstTrc ?? "-"} 与 BT.709 编码口径无关（本开关只换 BT.709 的编码形态）]";
    }

    /// <summary>
    /// 单入口不变量检测（规划 §4）：找出命令行中**改变像素色彩**的滤镜入口。
    /// <b>iccgen / iccdetect / format= / 无色彩参数的 scale 不计</b>（它们是标注/格式工具，误计会错杀 H2 与 AVIF 标注路径）。
    /// 返回 null = 无违反；否则返回违反描述（包含命中的入口名）。
    /// </summary>
    public static string? FindPixelColorEntry(string? ffmpegArgs, ColorTransformPlan p)
    {
        if (string.IsNullOrEmpty(ffmpegArgs) || p == null) return null;
        var entries = System.Text.RegularExpressions.Regex.Matches(ffmpegArgs,
            "(?:^|[,\\s])(zscale|colorspace|colormatrix|tonemap|scale)\\s*=?([^,]*)");
        int pixelEntries = 0; string? first = null;
        foreach (System.Text.RegularExpressions.Match m in entries)
        {
            var filt = m.Groups[1].Value;
            var pars = m.Groups[2].Value;
            // 前置 '-' 的是 **CLI 标注选项**（如 -colorspace bt709 / -color_trc），不是滤镜 ⇒ 不改像素，不计入口
            int fi = m.Groups[1].Index;
            if (fi > 0 && ffmpegArgs[fi - 1] == '-') continue;
            bool isColor = filt switch
            {
                // zscale 只有带色彩参数时才是色彩入口（纯 w=h= 缩放不算）
                "zscale" => System.Text.RegularExpressions.Regex.IsMatch(pars, "(^|:)(p|t|m|pin|tin|min|r|rin)(=|:)"),
                "scale" => System.Text.RegularExpressions.Regex.IsMatch(pars, "(^|:)(in_range|out_range|colors|matrix|transfer|primaries)(=|:)"),
                "tonemap" or "colorspace" or "colormatrix" => true,
                _ => false,
            };
            if (!isColor) continue;
            pixelEntries++;
            first ??= filt + "=" + pars;
        }
        bool wantsH1 = p.Action == ColorAction.Map && p.Backend == ColorBackend.InProcess;
        bool noTransform = p.Action is ColorAction.None or ColorAction.CarryIcc or ColorAction.Reject;
        if (wantsH1 && pixelEntries > 0)
            return $"引擎(H1)已负责映射，但命令行仍含像素色彩入口 [{first}] → 双映射入口";
        if (p.Backend == ColorBackend.FfmpegFilter && pixelEntries > 1)
            return $"预期只有一条 H2 色彩链，实际命中 {pixelEntries} 处";
        if (noTransform && pixelEntries > 0)
            return $"计划为 {p.Action}（不改像素），但命令行含像素色彩入口 [{first}]";
        return null;
    }

    /// <summary>
    /// 曲线能否用于“精确映射”：参数形与解析形（PQ/HLG/线性）均可；
    /// ICC 表曲线（curv LUT）可正向 EOTF 但反向仅插值近似 → 当作可用但记日志（插值误差由表密度控制）。
    /// </summary>
    private static bool CurveUsable(TransferCurve c) => c.Kind switch
    {
        TransferCurve.CurveKind.Table => c.TableLength >= 64,       // 表太稀 → 反演误差不可控
        _ => true,
    };

    private static bool MatrixIsIdentity(double[] m)
    {
        for (int i = 0; i < 9; i++)
            if (Math.Abs(m[i] - (i % 4 == 0 ? 1.0 : 0.0)) > 1e-9) return false;
        return true;
    }

    /// <summary>目标是否需要 ≥16bit 中间（8bit 目标 + 曲线/矩阵变化会放大量化误差）。</summary>
    private static bool Needs16Bit(ColorSpaceDescriptor dst, PlanPolicy policy)
        => policy.TargetBitDepth > 8 || dst.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;

    /// <summary>
    /// 源色域是否被目标包含：**几何凸包判据**（源三原色 xy 全在目标三角形内，规划 §5′.3）。
    /// 旧的“矩阵元素全非负”判据已废弃：原色转换矩阵本就有负有正，它既会漏判也会误判。
    /// 几何无法判定时退回矩阵符号作保守提示。
    /// </summary>
    private static bool GamutContainedIn(ColorSpaceDescriptor src, ColorSpaceDescriptor dst)
    {
        bool? g = ColorSpaceDescriptor.ContainsGamut(src, dst);
        if (g.HasValue) return g.Value;
        var m = src.MatrixTo(dst);
        if (m == null) return true;                                  // 无法判定 → 不误报
        const double eps = -EquivalenceTolerances.ContainmentEpsilon;
        for (int i = 0; i < 9; i++) if (m[i] < eps) return false;
        return true;
    }

    private static bool ChromEqual(double[] a, double[] b)
        => Math.Abs(a[0] - b[0]) < 1e-4 && Math.Abs(a[1] - b[1]) < 1e-4;

    /// <summary>
    /// 按容器规则表产出标注：能携带 ICC 且需要精确描述 → 附 ICC；HDR 只能 CICP（iccgen 不支持 HDR 曲线）；
    /// 不能携带 ICC 又不能命名（如 ProPhoto→AVIF）→ 由调用方按 Reject/告警处理。
    /// </summary>
    private static void SetLabelsFrom(ColorTransformPlan p, ColorSpaceDescriptor dst, PlanPolicy policy, bool preferCarry)
    {
        var caps = policy.Format;
        bool hdr = dst.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;

        if (preferCarry && dst.SourceIccPath != null && caps.CanCarryArbitraryIcc && !hdr)
        {
            p.AttachIcc = true; p.IccPath = dst.SourceIccPath;
            return;
        }
        if (dst.IsCicpNameable)
        {
            p.OutPrimaries = dst.PrimariesToken;
            p.OutTransfer = dst.Curve.CicpToken;
            p.OutMatrix = MatrixTokenFor(dst.PrimariesToken);
            // SDR 且格式能生成/携带 ICC → 附标准 ICC（D2 双描述策略：ICC + CICP 并存）
            if (!hdr && (caps.CanCarryArbitraryIcc || caps.CanGenerateIccFromCicp))
            {
                // ICC 的 TRC 必须跟着**像素实际用的那条 trc**走（= OutTransfer，已含容器钳制）。
                // 旧写法无条件写死 "iec61966-2-1"：对 TIFF（capTrc 钳 SDR，像素也是 sRGB）是对的，
                // 但对 PNG+BT.709 目标就给 BT.709 编码的像素附了一张 sRGB-TRC 的 ICC（与 cICP 自相矛盾）。
                // ⚙ 曲线无法命名（CicpToken=null）时**不附自生成 ICC**：iccgen 也是按 CICP 三元组出图的，
                //   拿 sRGB 当占位属于“发明一条传递特性”，与当初写死是同一类错（契约矩阵拓出来的）。
                if (p.OutTransfer != null)
                {
                    // ── sRGB 出口 + 容器"未标注即 sRGB" + 该容器编码器**不**自己落盘 ICC ⇒ 不附 ICC ──
                    // 口径与传统管线的 `unlabeledIsFine`（`FfmpegCommandBuilder.Decision.cs:416-423`）
                    // **逐字同源**：`UnlabeledMeansSrgb && (p==bt709) && (t==iec61966-2-1)`，再叠上
                    // "这张 ICC 只能靠后置嵌入"这一条（webp）—— 因为 jpg/png 的 ICC 由编码器顺手落盘，
                    // 不额外付代价，历史产物也一直带（verify-decision-delivery:139 锁定 jpg 必须带 sRGB ICC）。
                    // 实测证据（2026-09-16 契约，本门禁 :159 锁定）：hdr/webp 的出口语义就是 sRGB
                    // ⇒ 附一张 sRGB ICC 只是重复描述，还多一次 exiftool 后置。
                    bool unlabeledIsFine = caps.UnlabeledMeansSrgb && !caps.EncoderWritesGeneratedIcc
                        && string.Equals(dst.PrimariesToken, "bt709", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(dst.Curve.CicpToken, "iec61966-2-1", StringComparison.OrdinalIgnoreCase);
                    if (unlabeledIsFine)
                    {
                        p.UnlabeledAssumedSrgb = true;
                        p.Reason += " 出口语义=该容器未标注默认（sRGB）且其编码器不落盘 ICC ⇒ 不附 ICC（无标注直通，省一次后置嵌入）";
                    }
                    else
                    {
                        p.AttachIcc = true;
                        p.IccPrimaries = dst.PrimariesToken;
                        p.IccTrc = p.OutTransfer;      // 钳位发生时它就是 iec61966-2-1 ⇒ TIFF 旧行为不变
                        p.IccMatrix = p.OutMatrix;
                    }
                }
            }
            return;
        }
        // 不可 CICP 命名（ProPhoto/Adobe/自定义 ICC）
        if (caps.CanCarryArbitraryIcc)
        {
            p.AttachIcc = true; p.IccPath = dst.SourceIccPath;         // 由调用方确保路径可得（命名生成或源携带）
            p.Reason += " 标注=仅 ICC（该空间无 CICP 名）";
        }
        else
        {
            p.Reason += " ⚠️ 该格式既不能携带任意 ICC 又不能命名此空间";
        }
    }

    private static string? MatrixTokenFor(string? primariesToken) => primariesToken switch
    {
        "bt2020" => "bt2020nc",
        "bt709" or "smpte432" or "smpte431" => "bt709",
        "bt470bg" or "smpte170m" or "bt470m" or "jedec-p22" => "smpte170m",
        _ => null,
    };

    /// <summary>带缓存的矩阵求解（供 H1 内核反复取用同一对）。</summary>
    public static double[]? CachedMatrix(ColorSpaceDescriptor? src, ColorSpaceDescriptor? dst)
    {
        if (src?.RgbToXyzD50 == null || dst?.RgbToXyzD50 == null) return null;
        var key = KeyOf(src) + ">" + KeyOf(dst);
        lock (CacheLock)
        {
            if (MatrixCache.TryGetValue(key, out var hit)) return hit;
            var m = src.MatrixTo(dst);
            MatrixCache[key] = m;
            return m;
        }
    }

    private static string KeyOf(ColorSpaceDescriptor d)
    {
        if (d.RgbToXyzD50 == null) return d.Name;
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(d.Name.Length + 64);
        sb.Append(d.Name).Append('|');
        foreach (var v in d.RgbToXyzD50) sb.Append(v.ToString("0.000000", ci)).Append(',');
        sb.Append('|').Append(d.Curve.Name);
        return sb.ToString();
    }

    /// <summary>清空缓存（测试/单例切换时用；矩阵本身确定性，正常无需清）。</summary>
    public static void ClearCache() { lock (CacheLock) MatrixCache.Clear(); }
}
