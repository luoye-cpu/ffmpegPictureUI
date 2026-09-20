using System;
using System.Collections.Generic;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// **引擎路由拦截**的结构化原因（规划 <c>docs/COLOR_ENGINE_INTERFACE_PLAN.md</c> §5-D 裁定）。
/// <para>
/// ⚠ **与 <see cref="ColorVerdict"/> 不是一回事**：本类型答「**引擎为什么没接这个任务**」
/// （含义 = 该次转换**回退传统管线**，任务照样完成），不是「任务做不了」。
/// **不要**把两者混用 —— 混用会让调用方把「回退」误读为「失败」（这正是 S2 一度暂缓的原因）。
/// </para>
/// <para>
/// 之所以**不**并入 <see cref="ColorVerdict"/>：<see cref="VerdictKind.Reject"/> 的语义是
/// 「物理不可能 / 策略明确拒绝 ⇒ 不产出」，而路由拦截的语义是「本执行体不接 ⇒ 换个执行体」。
/// 两者都叫「拒」，结局却相反 ⇒ 必须是两个**同型但不混用**的类型。
/// </para>
/// <para>
/// 独立成文件（而不是并进 <c>ColorEngineRouter.cs</c>）：让「不许混用」这条边界在文件层面可见，
/// 与 <see cref="ColorVerdict"/> 的落法（单独 <c>ColorVerdict.cs</c>）一致。
/// </para>
/// </summary>
public sealed class EngineBlock
{
    /// <summary>稳定 Code（见 <see cref="EngineBlockCodes"/>），供程序判定与门禁断言。⚠ 禁止用 <see cref="Text"/> 当判据。</summary>
    public string Code = "";
    /// <summary>人读原因。**逐字等于** <c>ColorEngineRouter.BlockReason</c> 原有的那串文案（有门禁/日志在匹配它）。</summary>
    public string Text = "";
    /// <summary>
    /// 结构化替代路径（复用 <see cref="ColorAlternative"/>，不另造类型）。
    /// <para>⚠ **只在代码本来就知道替代时填**（如「该后端无出口 ⇒ 改用 -e Ffmpeg」）；拿不准的一律留空，
    /// 编一个替代就是把用户送进另一个必然失败的任务。</para>
    /// </summary>
    public IReadOnlyList<ColorAlternative> Alternatives = Array.Empty<ColorAlternative>();
}

/// <summary>
/// 路由拦截的**稳定 Code 词表**：与 <c>ColorEngineRouter</c> 的**每一条判据逐条对应**
/// （**不多不少**：9 条判据 ⇒ 9 个 Code）。
/// <para>
/// ⚠ 各成员只写**判据本身**（条件/成员名），**故意不写行号** —— 行号随文件增长必然漂移，
/// 本项目已为此修过多轮（规划 §1.2 的两次行号更正注）。要行号请看该轮实施报告。
/// </para>
/// <para>⚠ 全 <c>const string</c>：这些字符串是**契约**，改名等于破坏接口；新增只能追加。</para>
/// <para>⚠ 与 <see cref="DegradationCodes"/> **故意分家**：那套是「任务做得了但有损失」，这套是
/// 「引擎不接 ⇒ 换执行体」。二者即使同名也不得互相替换（判据打错 Code 会掩盖语义混淆）。</para>
/// </summary>
public static class EngineBlockCodes
{
    /// <summary><c>BlockReason</c> 的 <c>o == null</c> 分支。防御性（实际不可达：<c>Requested(null)=false</c>）。</summary>
    public const string OptionsNull = "OptionsNull";
    /// <summary><c>BlockReason</c> 的 <c>!ImageEncoderArgs.IsEngineEncodable(ext)</c> 分支（输出容器不在引擎编码出口白名单内）。</summary>
    public const string FormatNotSupported = "FormatNotSupported";
    /// <summary><c>BlockReason</c> 的 <c>inputIsAnimated</c> 分支（引擎按单帧处理会丢帧）。</summary>
    public const string AnimatedInput = "AnimatedInput";
    /// <summary><c>BlockReason</c> 的 <c>EncoderBackend != Ffmpeg</c> 分支（jxl+Cjxl 与 jxr 是既有例外）。</summary>
    public const string ExternalBackend = "ExternalBackend";
    /// <summary><c>UnconsumedOutputSettingItems</c> 的 <c>AnimationScaleW != 0</c> 项。</summary>
    public const string AnimationScaleUnsupported = "AnimationScaleUnsupported";
    /// <summary><c>UnconsumedOutputSettingItems</c> 的 <c>TonemapCurve</c> 非空项（旧 <c>--tonemap-curve</c>，请改用 <c>--color-tone-map</c>）。</summary>
    public const string LegacyTonemapCurve = "LegacyTonemapCurve";
    /// <summary><c>UnconsumedOutputSettingItems</c> 的 <c>JpegGainMap &amp;&amp; EnableMaxDimension &amp;&amp; MaxDimension &gt; 0</c> 项（未验证的新几何路径）。</summary>
    public const string GeometryWithGainMap = "GeometryWithGainMap";
    /// <summary><c>UnconsumedOutputSettingItems</c> 的 <c>JpegProgressiveId != 0</c> 项（ffmpeg 的 mjpeg 编码器无渐进能力）。</summary>
    public const string ProgressiveJpeg = "ProgressiveJpeg";
    /// <summary><c>UnconsumedOutputSettingItems</c> 的 <c>ImageEncoderArgs.UnsupportedEncoderSettings</c> 项（编码器不支持请求的设置）。</summary>
    public const string EncoderUnsupportedSetting = "EncoderUnsupportedSetting";
}
