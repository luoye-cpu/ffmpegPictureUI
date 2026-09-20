namespace FfmpegGui.Models
{
    /// <summary>
    /// 色彩策略（取代旧的 <see cref="IccMode"/>，作为用户-facing 的色彩处理总控）。
    /// 与「目标色域下拉(ColorSpace)」「高级色彩参数」「GainMap 独立开关」协同：
    /// 策略决定「是否转换 + 如何标定(ICC/CICP)」，下拉/高级参数决定「目标色域」。
    ///
    /// 面向用户的按钮显示简写 + 悬浮提示（提示只讲处理类型，不讲具体映射色域）：
    /// - Recommended「推荐」: 自动归一处理, 并智能选择合适的色彩标定方式。
    /// - CarryIcc「携带」:    不做转换, 完整保留源文件的色彩信息与配置文件。
    /// - BakeCicpOnly「仅CICP」: 转换色彩并剥离 ICC 配置文件, 仅用 CICP 标记。
    /// - Manual「手动」:      完全按你指定的色彩空间与参数进行处理与标定。
    /// </summary>
    public enum ColorStrategy
    {
        /// <summary>策略1 推荐：按无损阶梯自动决策（能携带就携带、否则归入包含源的最窄可命名超集），
        /// 并按容器能力选标注：支持 CICP → ICC+CICP 双描述；不支持 → 以 ICC 为主并记原因。
        /// HDR 源在支持的格式保 HDR，JPEG 经 GainMap 保 HDR；降 SDR 必须显式请求 tonemap。</summary>
        Recommended = 0,

        /// <summary>策略2 携带：**色彩变换零改动**（ColorLoss=None），携带源 ICC；
        /// 编解码损失（JPEG/WebP 等有损）与本策略无关，另记 CodecLoss。
        /// 三种合法结局（由引擎契约固定，`ServiceProbe contract` 机器校验）：
        /// ① 源有 ICC 且容器可携带 → 附着源 ICC；
        /// ② 源无 ICC 但可判定隐含 sRGB/等价、且容器未标注语义明确（PNG/JPEG/WebP）→ 无标注直通；
        /// ③ 其余 → 拒绝并给出建议。
        /// **本策略永不映射、永不生成 ICC**；生成 ICC 请用策略1/4。功能性需求可覆盖本策略（如 GainMap 底图必须映射、
        /// HDR 无 ICC 携带通路），覆盖时会在日志与计划里写明原因。</summary>
        CarryIcc = 1,

        /// <summary>策略3 仅CICP：不写 ICC（**禁 iccgen**），仅用 CICP 标记。
        /// 前提：容器能写 CICP（AVIF/HEIF/JXL）；源不可命名时先归入该容器可表达的最窄超集（包含源⇒零削色）。
        /// 对无 CICP 通路的容器（PNG/JPEG/WebP/TIFF）：仅当“未标注即隐含 sRGB”对该内容确实成立时
        /// 允许无操作直通，否则拒绝+建议（TIFF 未标注行为不统一，需显式开启允许项）。</summary>
        BakeCicpOnly = 2,

        /// <summary>策略4 手动：完全遵循用户指定的色彩空间 + 基准/传递/矩阵(联动可微调)，
        /// 转换像素并 CICP+ICC 标定。含 ProPhoto RGB 保真(超 BT.2020，仅 ICC)。</summary>
        Manual = 3
    }

    /// <summary>ColorStrategy 与旧 IccMode 的迁移/映射工具。</summary>
    public static class ColorStrategyMapper
    {
        /// <summary>旧 IccMode -> 新 ColorStrategy（用于旧预设/设置迁移）：
        /// None->Recommended、CarryIcc->CarryIcc、BakeToStandard->Recommended、BakeOnly->BakeCicpOnly。</summary>
        public static ColorStrategy FromIccMode(IccMode mode) => mode switch
        {
            IccMode.CarryIcc => ColorStrategy.CarryIcc,
            IccMode.BakeOnly => ColorStrategy.BakeCicpOnly,
            IccMode.BakeToStandard => ColorStrategy.Recommended,
            _ => ColorStrategy.Recommended,   // None 及未知
        };

        /// <summary>CLI/预设字符串 -> ColorStrategy（大小写/别名不敏感）。</summary>
        public static bool TryParse(string? s, out ColorStrategy strategy)
        {
            strategy = ColorStrategy.Recommended;
            if (string.IsNullOrWhiteSpace(s)) return false;
            switch (s.Trim().ToLowerInvariant().Replace("_", "-"))
            {
                case "recommended": case "recommend": case "auto": case "推荐": case "0":
                    strategy = ColorStrategy.Recommended; return true;
                case "carry": case "carry-icc": case "carryicc": case "携带": case "1":
                    strategy = ColorStrategy.CarryIcc; return true;
                case "cicp-only": case "cicponly": case "bake-cicp": case "仅cicp": case "2":
                    strategy = ColorStrategy.BakeCicpOnly; return true;
                case "manual": case "expert": case "手动": case "3":
                    strategy = ColorStrategy.Manual; return true;
                default: return false;
            }
        }
    }
}
