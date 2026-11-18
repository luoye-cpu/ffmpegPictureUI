using FfmpegGui.Models;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 「显式携带(CarryIcc)时是否回带源 ICC」的**单一真值**（MA-04；收口 COLOR-01 / COLOR-07）。
/// <para>
/// 历史上这一判定散在三处、各写一份等价 if：
/// <list type="number">
/// <item><c>FfmpegCommandBuilder.Decision</c> 的 pngCicpFirst 无条件 <c>AttachIcc=false</c> 剥掉 iCCP
/// （不看 DeclaredStrategy）⇒ P3→PNG legacy carry 只剩 cICP、丢 iCCP（COLOR-01）。</item>
/// <item><c>QueueProcessor</c> exiftool 后置用 <c>AttachIcc / IccPrimaries / IccPath</c> 的**字段形状**
/// 推断“计划自生成 ICC”，把“用户显式要带源 ICC”误判进去 ⇒ legacy 直连 WebP carry 既无 ICC 也无 CICP
/// （COLOR-07，广色域像素被查看器按 sRGB 洗白）。</item>
/// <item>引擎 PNG 后置双写（iCCP + cICP）。</item>
/// </list>
/// 三处口径必须收敛到本函数；**禁止**在调用方再写一份等价判断。
/// </para>
/// <para>判定只认三件事：用户是否显式选了 CarryIcc、源是否真有 ICC 可带、本出口写不写得了 ICC。
/// <see cref="ColorTransformPlan.IccPath"/> / <see cref="ColorTransformPlan.IccPrimaries"/> 的**字段形状
/// 不是**判定依据——legacy 裁决计划与引擎计划填充这两个字段的形状不同（legacy 在显式目标下会
/// 选“生成标准 ICC”而不是“带源 ICC”），靠形状推断必然漏一路。</para>
/// </summary>
public static class IccCarryPolicy
{
    /// <summary>携带源 ICC 的兑现方式。</summary>
    public enum CarryIccOutcome
    {
        /// <summary>不回带源 ICC（CICP 优先 / 计划自生成标准 ICC / 源无 ICC 可带 / 出口写不了 ICC）。</summary>
        Skip,
        /// <summary>回带源 ICC；容器不能与 CICP 共存（webp/jpg）⇒ ICC-only。</summary>
        CarryIccOnly,
        /// <summary>回带源 ICC；容器允许 ICC+CICP 共存（png/avif）⇒ 双写（cICP 由各自后置步骤补）。</summary>
        CarryWithCicp,
        /// <summary>
        /// 历史 fail-closed 保留位，**不再由 Decide 返回**。2026-11-18 维护者定案：
        /// 源 ICC 存在性不可判定只做警告，不阻断产出（避免坏 profile 把整条任务打停）。
        /// </summary>
        FailClosed,
    }

    /// <summary>
    /// 本次出口是否应当回带/保留源 ICC（三态版本；生产路径应优先使用它）。
    /// <para>Unreadable 只回 <see cref="CarryIccOutcome.Skip"/>：不拿未读出的 ICC 冒充源描述，
    /// 也不拒绝产出；警告由调用方按「源 ICC 未验证」登记。</para>
    /// </summary>
    public static CarryIccOutcome Decide(
        ColorStrategy declared, SourceIccStatus sourceIccStatus,
        bool containerCanCarryIcc, bool containerCanBothIccAndCicp)
    {
        if (declared != ColorStrategy.CarryIcc) return CarryIccOutcome.Skip;
        if (sourceIccStatus == SourceIccStatus.Unreadable) return CarryIccOutcome.Skip;
        if (sourceIccStatus != SourceIccStatus.Present || !containerCanCarryIcc)
            return CarryIccOutcome.Skip;
        return containerCanBothIccAndCicp ? CarryIccOutcome.CarryWithCicp : CarryIccOutcome.CarryIccOnly;
    }

    /// <summary>兼容旧调用：bool 只能表达“有/无”，无法表达 Unreadable；新代码改用三态重载。</summary>
    public static CarryIccOutcome Decide(
        ColorStrategy declared, bool sourceHasIcc,
        bool containerCanCarryIcc, bool containerCanBothIccAndCicp)
        => Decide(declared,
            sourceHasIcc ? SourceIccStatus.Present : SourceIccStatus.Absent,
            containerCanCarryIcc, containerCanBothIccAndCicp);

    /// <summary>是否回带源 ICC（<see cref="CarryIccOutcome.Skip"/> 以外的任一枚举）。</summary>
    public static bool ShouldCarrySourceIcc(
        ColorStrategy declared, SourceIccStatus sourceIccStatus,
        bool containerCanCarryIcc, bool containerCanBothIccAndCicp)
        => Decide(declared, sourceIccStatus, containerCanCarryIcc, containerCanBothIccAndCicp)
            is CarryIccOutcome.CarryIccOnly or CarryIccOutcome.CarryWithCicp;

    public static bool ShouldCarrySourceIcc(
        ColorStrategy declared, bool sourceHasIcc,
        bool containerCanCarryIcc, bool containerCanBothIccAndCicp)
        => Decide(declared, sourceHasIcc, containerCanCarryIcc, containerCanBothIccAndCicp)
            is CarryIccOutcome.CarryIccOnly or CarryIccOutcome.CarryWithCicp;
}
