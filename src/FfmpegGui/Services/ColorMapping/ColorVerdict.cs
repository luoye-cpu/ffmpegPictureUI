using System;
using System.Collections.Generic;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 一次色彩处理的**裁决类别**（规划 §3 P0）。
/// <para>
/// 三者语义互斥且穷尽：
/// <list type="bullet">
///   <item><see cref="Proceed"/>：契约可**完全**兑现，照常产出；</item>
///   <item><see cref="ProceedWithDegradation"/>：契约**不可完全**兑现，但能产出且损失已知（新能力，S5 才会产生）；</item>
///   <item><see cref="Reject"/>：物理不可能，或策略明确拒绝 ⇒ 不产出。</item>
/// </list>
/// </para>
/// <para>
/// ⚠ **不要**把 <c>ColorEngineRouter.BlockReason</c> 的「引擎不可走 ⇒ 回退传统管线」当成 <see cref="Reject"/>：
/// 前者是**路由前置条件不满足**（不是任务级终局，回退后任务照样完成），后者是**任务做不了**。
/// 两者混用会让调用方把「回退」误读为「失败」。该收口属 S2，本轮不做。
/// </para>
/// </summary>
public enum VerdictKind { Proceed, ProceedWithDegradation, Reject }

/// <summary>替代路径的**类别**：改格式 / 改策略 / 改目标空间 / 改选项。</summary>
public enum AlternativeKind { Format, Strategy, Target, Option }

/// <summary>
/// 一处**结构化**损失：<see cref="Code"/> 供程序判定/本地化与门禁断言，<see cref="Text"/> 供人读。
/// <para>⚠ 禁止用 <see cref="Text"/> 当判据（文案会改，Code 不会）。</para>
/// </summary>
public readonly record struct ColorDegradation(string Code, string Text);

/// <summary>一条**结构化**替代路径：改什么（<see cref="Kind"/>）、改成什么（<see cref="Value"/>）、为什么（<see cref="Why"/>）。</summary>
public readonly record struct ColorAlternative(AlternativeKind Kind, string Value, string Why);

/// <summary>
/// **统一裁决**：任意「选项 + 输入 + 输出」组合，引擎给出**一个**结构化裁决对象（规划 §3 P0）。
/// <para>
/// 由 <c>ColorStrategyPlanning.Plan(ColorIntent)</c> 在返回前**单一投影点**写入
/// （<see cref="ColorTransformPlan.Verdict"/>），覆盖 <c>PlanWithPolicy</c> 的**所有**返回分支
/// （含 <c>PlanFaithful</c> / 各 <c>PlanXxx</c> / <c>Reject</c>）。
/// </para>
/// <para>
/// <see cref="Action"/> / <see cref="Reason"/> / <see cref="Advice"/> 是
/// <see cref="ColorTransformPlan"/> 对应字段的**兼容投影**：形状统一，值原样搬运，既有读取点与日志不受影响。
/// </para>
/// </summary>
public sealed class ColorVerdict
{
    public VerdictKind Kind;
    /// <summary>复用既有枚举；语义仍是「计划要做什么」。</summary>
    public ColorAction Action;
    /// <summary>为什么是这个裁决。</summary>
    public string Reason = "";
    /// <summary>人读建议（保留自由文本，兼容既有日志/门禁文案匹配）。</summary>
    public string Advice = "";
    public IReadOnlyList<ColorDegradation> Degradations = Array.Empty<ColorDegradation>();
    public IReadOnlyList<ColorAlternative> Alternatives = Array.Empty<ColorAlternative>();
}

/// <summary>
/// 损失的**稳定 Code 词表**（规划 §3 P0）：供 GUI 本地化、门禁断言与调用方分支使用。
/// <para>⚠ 全 <c>const string</c>：这些字符串是**契约**，改名等于破坏接口；新增只能追加。</para>
/// </summary>
public static class DegradationCodes
{
    /// <summary>容器无 CICP 通路，产物将失去 CICP 标注。</summary>
    public const string NoCicpCarrier = "NoCicpCarrier";
    /// <summary>容器无 ICC 通路，产物将失去 ICC 标注。</summary>
    public const string NoIccCarrier = "NoIccCarrier";
    /// <summary>HDR 源且无「携带 HDR ICC」通路 ⇒ 只能靠 CICP 或降 SDR。</summary>
    public const string HdrNoIccCarryPath = "HdrNoIccCarryPath";
    /// <summary>容器不能携带 ICC（能力表判定的结构限制）。</summary>
    public const string ContainerCannotCarryIcc = "ContainerCannotCarryIcc";
    /// <summary>未标注被按隐含 sRGB 处理（有惯例依据，但不是显式描述）。</summary>
    public const string UnlabeledAssumedSrgb = "UnlabeledAssumedSrgb";
    /// <summary>源色域超出目标 ⇒ 发生裁切/压缩。</summary>
    public const string GamutClipped = "GamutClipped";
    /// <summary>
    /// 源色域超出目标 ⇒ 已按 **GMO（色域压缩）**处理：越界色被向"同亮度灰"混合去饱和，而不是硬裁切。
    /// <para>
    /// 与 <see cref="GamutClipped"/> 互斥（同一像素不可能既被裁切又被压缩）：由
    /// <c>ColorTransformPlan.GamutMap</c> 决定登记哪一个。去饱和是一次**可感知**的颜色改动，
    /// 必须作为「已发生的损失」出现在裁决里，否则 <c>Kind</c> 会误报 <c>Proceed</c>。
    /// </para>
    /// </summary>
    public const string GamutCompressed = "GamutCompressed";
    /// <summary>出口位深低于中间精度（8bit 容器 + 有损变换）。</summary>
    public const string BitDepthReduced = "BitDepthReduced";
    /// <summary>请求了 GainMap 但容器非 JPEG 族 ⇒ 结构不可能。</summary>
    public const string GainMapNonJpeg = "GainMapNonJpeg";
    /// <summary>GainMap 与几何缩放同用（未验证的新几何路径）。</summary>
    public const string GeometryWithGainMap = "GeometryWithGainMap";
    /// <summary>容器无任何色彩管理通路（GIF/JXR/PPM）。</summary>
    public const string NoColorPath = "NoColorPath";
    /// <summary>动画源 + 当前不支持的处理路径。</summary>
    public const string AnimatedNotSupported = "AnimatedNotSupported";
    /// <summary>编码器不支持请求的设置。</summary>
    public const string EncoderUnsupportedSetting = "EncoderUnsupportedSetting";
    /// <summary>请求的 tonemap 在 GainMap 出口不被消费（压暗由编码器内部分段 Reinhard 自持）⇒ 已产出，但该选项未生效。</summary>
    public const string ToneMapNotAppliedToGainMap = "ToneMapNotAppliedToGainMap";
}
