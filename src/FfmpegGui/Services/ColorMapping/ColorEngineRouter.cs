using System;
using System.IO;
using FfmpegGui.Models;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 引擎路由判据（A2 的前置件）：**是否把这次转换交给自研色彩引擎执行**。
///
/// 之所以做成纯函数：QueueProcessor 的默认 ffmpeg 分支有两处调用点，控制流插入应当只问一个问题
/// "能不能走引擎"，判据本身必须单点、可断言，不能散落在两处各写一份（本项目反复修过"三处不一致"）。
///
/// 硬约束（来自 <see cref="RawColorPipeline"/> 的实际能力，不是猜测）：
///  • 编码出口覆盖 png/apng/tiff/tif/webp/jpg/jpeg/**avif**/**gif**/**jxl**/**jxr**（见 <c>BuildEncodeArgs</c> 的 switch
///    与 <see cref="ImageEncoderArgs.IsEngineEncodable"/>；**该清单的唯一真值在
///    <see cref="ImageEncoderArgs.IsEngineEncodable"/>，本类不再自持副本** —— 2026-09-17 合并）；
///    **DNG** 仍走外部编码器，引擎不能替代（DNG 结构上需要传感器 Bayer 数据）。
///    ⚠ **JXR 于 2026-09-17 接入**（本条此前写作"JXR/DNG 仍走外部编码器"）：
///    本工具链的 ffmpeg **没有 jxr 编码器**（实测 `-encoders` 无 jxr）⇒ 不存在 ffmpeg 编码出口这条替代路径，
///    jxr 恒走 **JXR 执行出口**（`ColorTransformPlan.ExternalEncoderExit` ⇒ `RawColorPipeline` 的
///    BMP/TIFF 中转 + `JxrEncApp`，见 <c>EncodeJxrViaJxrEncAppAsync</c>）。
///    ⚠ 出口内部任何一环不满足（JxrEncApp 缺失 / 中转失败）一律**显式失败**，绝不静默改道。
///    ⚠ **JXL 的两个后端都已接入**（2026-09-17 第二轮补齐；本条曾写作"JXL 只接入了一半"）：
///    ① <c>backend=Ffmpeg</c>（ffmpeg 的 libjxl 吃 rawvideo，与 avif/gif 同型）⇒ 走引擎的常规编码段；
///    ② <c>backend=Cjxl</c>（不吃 rawvideo、色彩语义靠 <c>-x color_space=</c>/<c>-x icc_pathname=</c>）
///       ⇒ 走引擎的 **cjxl 执行出口**（`ColorTransformPlan.ExternalEncoderExit`，由 `ColorIntentFactory`
///       按 <c>fmt=="jxl" && backend==Cjxl</c> 置位；出口实现见 <see cref="RawColorPipeline"/>）。
///       ⚠ 本机装了 cjxl 时 ② 正是**默认后端** ⇒ 这条不是理论情形。
///       ⚠ 出口内部任何一环不满足（cjxl 缺失 / 要 ICC 拿不到 / 语义落不进枚举）一律**显式失败**，
///         **绝不回退** ffmpeg 的 libjxl（回退会静默换掉用户选的编码器与其全部参数）。
///    AVIF 于 **2026-09-17** 接入：编码参数由 <see cref="ImageEncoderArgs.BuildAvifOptions"/>
///    单点给出（与传统管线同一份），GIF→AVIF 的 <c>avifenc.exe</c> 专用管线**不在**出口内。
///    GIF 于 **2026-09-17** 接入：调色板链 + <c>-loop</c> 由 <see cref="ImageEncoderArgs.BuildGifFilterChain"/>
///    / <see cref="ImageEncoderArgs.BuildGifOptions"/> 单点给出；⚠ 透明 GIF 依赖第二输入取 alpha
///    （引擎 raw 通路无 alpha），细节见 <see cref="RawColorPipeline"/>。
///  • 引擎是**单帧**处理；动画输入（gif/动 apng/动 avif/动 webp）必须由既有动画分支处理。
///  • 任何判据不满足 ⇒ 走传统管线，并在日志写明原因（不静默改变执行路径）。
///
/// ⚠ **2026-09-16 起默认走引擎**（`FfmpegOptions.ColorEngine = "engine"`），
/// `"legacy"` 降级为**显式逃生门**（A/B 对拍与故障排查用）。
/// </summary>
public static class ColorEngineRouter
{
    /// <summary>
    /// 是否把这次转换交给自研色彩引擎执行。**2026-09-16 起默认 = 是**（`ColorEngine="auto"`，
    /// 用户指令「清理传统管线，以自研管线为准」）。只有显式 <c>ColorEngine="legacy"</c> 才回退传统管线。
    /// <para>
    /// 切默认的依据：A/B 实测 **8-bit 源上两条管线逐字节等价**（png/tiff 逐字节相同、jpg PSNR=inf），
    /// 16-bit 源残余差异为舍入级（png/tiff 85.8dB）。见 `docs/TESTING.md §6` 第 48/49 条 ——
    /// 这正是原注释要求的"默认值切换需先有 A/B 数据"。
    /// </para>
    /// <para>
    /// ⚠ <b>2026-09-18：<c>--jpeg-gain-map</c> 恒请求引擎（legacy 开关不能把它踢出去）</b>。
    /// 维护者裁定「GainMap 软件应该有一套自实现的管线，这一套自实现管线融合进引擎作为 GainMap 后续的
    /// 完整实现」⇒ 引擎是 GainMap 的**唯一实现**。
    /// <list type="bullet">
    ///   <item><b>为什么 GainMap 不受 legacy 开关管辖</b>：GainMap（Ultra HDR / ISO 21496-1 / MPF）是
    ///     <b>输出结构</b>能力 —— 决定"产物里有没有增益图与 ISO 元数据"，而 <c>--color-engine</c> 选的是
    ///     「色彩像素怎么算」的执行策略（引擎算子 vs 传统链）。两者不是同一维度 ⇒ 用后者关掉前者
    ///     属于把两个正交的开关绑在一起，用户"要 GainMap"的意图不该被一个色彩执行体选择抹掉。</item>
    ///   <item><b>连带效果（预期且正确）</b>：<c>ShouldRoute</c> 对 GainMap 恒真 ⇒
    ///     <c>QueueProcessor</c> 里「GainMap 专用管线」的分派条件（含 <c>!ShouldRoute(...)</c>）恒假 ⇒
    ///     该专用分派**永不触发**，其回退体成为死代码（删除属下一阶段，本增量不动）。
    ///     这也顺带消解了"显式 <c>engine</c> 却走专用管线且日志说'尚未接入'"的反向不诚实。</item>
    ///   <item><b>仍然会走传统管线的 GainMap 情形</b>：<c>BlockReason</c> 非 null 时 ⇒ <c>ShouldRoute=false</c>
    ///     ⇒ 专用分派照旧接手。即本改动只把"引擎**能**走 GainMap"变成必然，不改变"引擎走不了"时的归属。
    ///     <para>
    ///     ⚠ **2026-09-18 实测：`ShouldRoute` 不是恒真**
    ///     （<c>ShouldRoute = Requested &amp;&amp; BlockReason == null</c>）
    ///     ⇒ <c>QueueProcessor</c> 的 `!ShouldRoute(...)` 专用分派**并未**因此恒假。当前残余触发点**只有一个**：
    ///     <list type="bullet">
    ///       <item><b>动画输入 + GainMap（仍在）</b>：<c>BlockReason</c> 返回 <c>AnimatedInput</c> ——
    ///         <c>QueueProcessor.InputMayHaveMultipleFramesAsync</c> 的输入侧子句对
    ///         <c>.gif/.apng</c> 输入**恒 true**（容器可能承载动画）⇒ 走专用管线。
    ///         ⚠ `#136` 切片 B 起该判据不再"输出非 gif/apng 时回落 <c>IsAnimated</c>"：
    ///           <c>.webp/.avif</c> 改为**真探测**（三态 + 不确定⇒保守按多帧），
    ///           而 <c>.gif/.apng</c> 的结论**未变**（仍是恒 true）⇒ 本条的触发条件与实测结论**仍然成立**。
    ///         实测（<c>anim_gif.gif</c> + <c>--format jpg --jpeg-gain-map true</c>，auto/engine/legacy 三格）：
    ///         exit 0、7241B、日志 `本次由 GainMap 专用管线（引擎不可走）` + `[GainMap] Step 1`，引擎决策行 **0 条**。
    ///         ⇒ 这是 **Part B（删专用分派 + 回退体）的第二条前置条件**：不先处置它，直接删分派会让该格
    ///         静默产出一个**没有增益图的普通 JPEG**（规划层不拦它 —— 动画拦截在**路由层**）。</item>
    ///       <item><b>「GainMap + 最长边几何缩放」（已消解）</b>：原返回 <c>GeometryWithGainMap</c>；
    ///         2026-09-18 该拦截已作为**死拦截**删除（判据恒假，几何已由上游预缩放中继完成）
    ///         ⇒ 该组合现在 <c>ShouldRoute=true</c>、由引擎 GainMap 出口产出真 Ultra HDR。
    ///         完整实测与删除理由见 <see cref="UnconsumedOutputSettingItems"/> 的删除记录。</item>
    ///       <item><b>「GainMap + --jpeg-progressive」（已消解）</b>：原返回 <c>ProgressiveJpeg</c>；
    ///         2026-09-19 该拦截对 GainMap **判据不成立**（GainMap 底图/增益图恒由 cjpegli 编码，
    ///         而 cjpegli **支持** `-p 0..2`）⇒ 已收窄为 `JpegProgressiveId > 0 &amp;&amp; !gainMapJpeg`
    ///         ⇒ 该组合现在 <c>ShouldRoute=true</c>，由引擎 GainMap 出口按单一真值
    ///         `GainMapJpegCodec.ProgressiveLevelOf` 下发 `-p N`。
    ///         实测：`--jpeg-progressive 1 --jpeg-gain-map true --color-engine engine`
    ///         放行前 exit 90 / 0 产物，放行后 exit 0 / SOF2 真渐进 Ultra HDR。
    ///         完整依据见 <see cref="UnconsumedOutputSettingItems"/> 的追加注释。</item>
    ///     </list>
    ///     </para></item>
    /// </list>
    /// </para>
    /// </summary>
    public static bool Requested(FfmpegOptions? o)
        => o != null && (o.JpegGainMap
            || !string.Equals(o.ColorEngine, "legacy", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 用户是否**显式**要求走引擎（`--color-engine engine`，与默认值 `auto` 区分开）。
    /// <para>
    /// 区分二者的用途在 <c>QueueProcessor.TryRunColorEngineAsync</c>：
    ///  • **显式 `engine`** 而不可走 ⇒ **硬失败并点名**（可用性诚实：绝不静默回退到别的执行体）；
    ///  • **默认 `auto`** 而不可走 ⇒ 该格式本就不在引擎的编码出口内（JXR/DNG/动画），
    ///    传统管线是它的正常归属 ⇒ 记一条说明后继续走。
    ///    ⚠ 2026-09-17（cjxl 出口轮）更正：此处曾把「JXL 的 **Cjxl 后端子情形**」也列为不可走，
    ///      现已接入 ⇒ 不再是本条的举例（JXR/DNG/动画仍是）。
    /// ⚠ 不分这两种情况会让默认切引擎后**所有非引擎格式硬失败**（实测 avif/gif 全红、退出码 90）。
    /// </para>
    /// </summary>
    public static bool ExplicitlyRequested(FfmpegOptions? o)
        => o != null && string.Equals(o.ColorEngine, "engine", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 用户是否请求**色域映射（GMO）**：只认字面量 <c>on</c>（口径与 <c>ColorIntentFactory</c> 装配
    /// <c>AllowGamutCompression</c> 时完全一致 —— 该判据全仓只有这一份实现）。
    /// </summary>
    public static bool GamutMapRequested(FfmpegOptions? o)
        => o != null && string.Equals(o.ColorGamutMap?.Trim(), "on", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// **显式** <c>--color-engine legacy</c> + <c>--color-gamut-map on</c> 的结构性冲突
    /// （返回 null = 无冲突）。
    /// <para>
    /// 为什么必须拒绝而不是继续跑：GMO（色域压缩）是**引擎专有算子**（<c>ColorKernels.GamutMapScalar</c>，
    /// 见 <c>ColorTransformPlan.GamutMap</c> 与后端选择处的 <c>needsGmo ⇒ H1</c>）；传统链只有
    /// <c>Math.Clamp</c> 硬裁切，**没有任何可兑现该开关的算子**。实测（2026-09-17）：legacy 下
    /// `--color-gamut-map on` 与 `off` 的产物**逐字节相同**（27148B / `57C1B004F4123EAA`），
    /// 且日志里 0 条 gamut 相关行 —— 用户显式开了开关、什么都没发生、也无任何告知，
    /// 正是本项目最忌讳的「看起来配置过」。拒绝优于静默。
    /// </para>
    /// <para>
    /// ⚠ **只对显式 legacy 报错**。默认 <c>auto</c> 下「引擎不可走 ⇒ 回退传统管线」时用户并没有选 legacy，
    /// 传统管线是该格式的正常归属 ⇒ 只**告警**（见 <c>QueueProcessor</c> 的同名判定），
    /// 否则默认切引擎后所有非引擎格式（JXR/DNG/动画）都会硬失败。
    /// （2026-09-17 cjxl 出口轮更正：此处曾把「JXL 的 Cjxl 后端子情形」一并列出，现已接入。）
    /// （2026-09-17 JXR 出口轮更正：JXR 亦已接入 ⇒ 只剩 DNG/动画。）
    /// </para>
    /// </summary>
    public static string? LegacyGamutMapConflict(FfmpegOptions? o)
    {
        if (o == null) return null;
        if (!string.Equals(o.ColorEngine, "legacy", StringComparison.OrdinalIgnoreCase)) return null;
        if (!GamutMapRequested(o)) return null;
        return "显式 --color-engine legacy 与 --color-gamut-map on 冲突：传统色彩链只有硬裁切（钳位），"
             + "没有 GMO 色域压缩算子，该开关在传统链上不可能被兑现。"
             + "请改用 --color-engine engine（或 auto）以启用色域压缩，或把 --color-gamut-map 设回 off。";
    }

    /// <summary>
    /// 不可走引擎时返回**结构化**原因（<see cref="EngineBlock"/>）；可以走则返回 <c>null</c>。
    /// <para>
    /// ⚠ **2026-09-17（S2）**：返回类型由 <c>string?</c> 改为 <see cref="EngineBlock"/>（规划 §5-D 裁定）。
    /// **判据、判据顺序、文案三者一个字都没改** —— 原字符串**逐字**搬进 <see cref="EngineBlock.Text"/>，
    /// 只是把「为什么」另外结构化出一份 <see cref="EngineBlock.Code"/>（供 GUI/CLI 解释与门禁断言）。
    /// </para>
    /// <para>
    /// ⚠ 语义仍是「**引擎不可走 ⇒ 回退传统管线**」，**不是**任务级终局 ⇒ 返回类型**不是**
    /// <see cref="ColorVerdict"/>（见 <see cref="EngineBlock"/> 的说明）。
    /// </para>
    /// </summary>
    public static EngineBlock? BlockReason(FfmpegOptions? o, string outputPath, bool inputIsAnimated)
    {
        if (o == null) return new EngineBlock
        {
            Code = EngineBlockCodes.OptionsNull,
            Text = "options 为空",
        };
        if (!Requested(o)) return null;                       // 显式 legacy ⇒ 不算阻塞（走传统管线是本分）
        var ext = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        // ⚠ **2026-09-17：白名单合并为单一真值。** 此前这里与 `ImageEncoderArgs.IsEngineEncodable`
        //   各写一份 `is "png" or … or "gif"` 字面量（avif 轮遗留），而后者**全仓没有任何调用者**
        //   —— 正是"两处口径漂移"的温床（本项目反复修过同类）。二者问的是**同一个问题**
        //   （"引擎的编码出口覆盖哪些格式"），不是两件事 ⇒ 真值放在 `ImageEncoderArgs`
        //   （编码出口 `BuildEncoderOptions` 的 switch 就在那里，路由只是**消费方**），
        //   此处改为调用它。语义等价（`ext` 已归一化，`IsEngineEncodable` 再做一次 trim/strip-dot/lower 是幂等的）。
        bool supported = ImageEncoderArgs.IsEngineEncodable(ext);
        if (!supported) return new EngineBlock
        {
            Code = EngineBlockCodes.FormatNotSupported,
            Text = $"引擎编码出口不支持 .{ext}（DNG 由外部编码器负责）",
            // ⚠ 故意**不给** Alternatives：本分支只知道"ext 不在出口白名单内"，
            //   不知道该改成哪个**具体**容器（§3 P0b 要求 Alternative.Value 是具体值），
            //   凭空推一个格式就是把用户送进另一个可能同样不可行的任务。留空由 GUI 显示原文案。
        };
        if (inputIsAnimated) return new EngineBlock
        {
            Code = EngineBlockCodes.AnimatedInput,
            Text = "输入为动画，引擎按单帧处理会丢帧",
            // ⚠ 故意**不给** Alternatives：本方法拿不到**输入路径**，无法区分"动画"是来自
            //   `--animation-fps`（去掉即可）还是来自输入文件本身（去掉选项也没用）；
            //   要判清必须复刻 `QueueProcessor.IsAnimated` 的口径 —— 那是**第二份口径**，
            //   正是本项目反复修过的"两处判据漂移"，故宁可不给。
            //   ⚠ `#136` 收尾轮：`IsAnimated` 的 `.webp` 子句已改为读**同一份**预计算缓存
            //     （`AnimatedWebpCache`，由 `IsAnimatedWebpAsync` 在确定时写）⇒ 「第二份**探测**」已消除；
            //     但**语义差异仍在** —— `IsAnimated` 仍含「**输出**容器是 gif/apng」这一轴，
            //     共享缓存消不掉它 ⇒ 上面的结论**不变**（仍不给 Alternatives）。
        };
        // ── 外部编码器后端：**默认拦，jxl+Cjxl 例外**（2026-09-17 接入 cjxl 出口）─────────────
        // 原判据「只要不是 Ffmpeg 就拦」当时是对的：引擎只有 ffmpeg 编码出口，替外部编码器做决断
        // 必然丢掉用户选的那套参数（静默换编码器）。**本次例外的前提是出口真的存在了** ——
        // `RawColorPipeline` 新增 cjxl 出口（变换后的 raw → PPM/PAM 管道 → cjxl，色彩语义经
        // `-x color_space=` / `-x icc_pathname=` / `--intensity_target=` 表达）。
        // 判据与装配层**同源同形**（`ColorIntentFactory`：`fmt=="jxl" && backend==Cjxl` ⇒ 计划位置位），
        // 这里必须逐字一致，否则会出现「路由说能走、计划位没置」的两套口径。
        //
        // ⚠ **故意不查 cjxl 是否可用**（不写成 `&& CjxlService.IsAvailable`）：这正是 P7f 的结论 ——
        //   把可用性写进路由条件，会让"工具缺失"变成**静默改道**（回落到传统管线，用户看到"已完成"
        //   却不知道编码器换了）。现在的形态是：路由照常放行 ⇒ 出口内部发现 cjxl 缺失 ⇒ **显式失败**。
        //   可用性诚实：宁可失败并点名，也不静默换执行体。
        if (o.EncoderBackend != FfmpegGui.Services.EncoderBackend.Ffmpeg
            && !(ext == "jxl" && o.EncoderBackend == FfmpegGui.Services.EncoderBackend.Cjxl)
            && ext != "jxr")   // JXR：JxrEncApp 是本工具链**唯一**的 jxr 编码器 ⇒ 出口内恒由它编码
            return new EngineBlock
            {
                Code = EngineBlockCodes.ExternalBackend,
                Text = $"用户选了外部编码器后端 {o.EncoderBackend}，引擎不得替它做决断",
                // 本分支在**白名单检查之后** ⇒ ext 一定在引擎出口内 ⇒ "改用 Ffmpeg 后端"必然可走
                // （剩下的只有 UnconsumedOutputSettings 那类参数拦截，与本条无关）。
                Alternatives = new[]
                {
                    new ColorAlternative(AlternativeKind.Option, "encoder",
                        "引擎出口只覆盖 Ffmpeg（以及 jxl+Cjxl / jxr 两个专用出口）；"
                        + "改用 -e Ffmpeg 即可交引擎执行"),
                },
            };
        var unconsumed = UnconsumedOutputSettingItems(o);
        if (unconsumed.Count != 0)
        {
            // 多个原因时 Code 取**列表首项**（= 文案里第一个原因），顺序即下方判据的固定顺序 ⇒ 确定性、可断言。
            // 全部原因仍逐字出现在 Text 里（文案一个字都没改）。
            var alts = new System.Collections.Generic.List<ColorAlternative>();
            foreach (var it in unconsumed) alts.AddRange(it.Alternatives);
            var texts = new string[unconsumed.Count];
            for (int i = 0; i < unconsumed.Count; i++) texts[i] = unconsumed[i].Text;
            return new EngineBlock
            {
                Code = unconsumed[0].Code,
                Text = $"引擎编码出口尚未承接这些输出参数：{string.Join("、", texts)}",
                Alternatives = alts,
            };
        }

        // ✅ **2026-09-16 起超广色域保真路径已接入引擎** —— 原先在此的 🔴【待实现】拦截已移除。
        // 接入方式：`ColorIntentFactory` 用**与传统管线同一个** `ResolveFaithfulUltraWideIcc` 判定，
        // 结果经 `PlanPolicy.FaithfulIccPath` 进规划层；`PlanFaithful` 给出「像素零改动 + 仅靠 ICC 描述」
        // （`Action=CarryIcc`、不写 CICP），执行层把 `CarryIcc` 与 `None` 同等处理（引擎不参与变换），
        // ICC 由既有的后置附着机制嵌入。验证：`verify-prophoto-faithful` **26/0**。
        return null;
    }

    /// <summary>
    /// 引擎编码出口**尚未承接**的输出参数清单（返回 null = 全部可承接）。
    /// <para>
    /// **2026-09-16 起**：编码器参数（质量 / 无损 / PNG pred+DPI / WebP preset+压缩级别 /
    /// TIFF 压缩算法+DPI / JPEG huffman+dct / 色度子采样）已由
    /// <see cref="ImageEncoderArgs"/> 统一承接；**最长边几何缩放**已由
    /// <see cref="RawColorPipeline"/> 在解码步承接（复用传统管线的
    /// <c>FfmpegCommandBuilder.BuildMaxDimensionScaleFilter</c>），二者均不再列于此。
    /// </para>
    /// <para>
    /// **2026-09-17 起 AVIF 亦由 <see cref="ImageEncoderArgs"/> 承接**
    /// （<see cref="ImageEncoderArgs.BuildAvifOptions"/> 整体搬自传统管线，同一份代码），
    /// 故 AVIF 的 crf / cpu-used / tune / preset / 高级选项不再构成阻塞项。
    /// </para>
    /// <para>
    /// **2026-09-16 起**：<c>--icc-file</c>（外部 ICC，语义 = 判定**源**色彩空间）已由
    /// <see cref="ColorIntentFactory"/> 承接（优先级高于源内嵌 ICC 与 CICP 标签），亦不再列于此。
    /// </para>
    /// <para>
    /// 本清单只剩**结构上不属于引擎**的项：动画（引擎是单帧）、以及本管线本就不支持的参数
    /// （渐进 JPEG、旧 <c>--tonemap-curve</c>）。
    /// ⚠ **2026-09-18**：原列于此的「GainMap + 最长边几何缩放」已随该拦截被删除而移除
    /// （它是一段判据恒假的**死拦截**，见下方删除记录）。
    /// </para>
    /// <para>
    /// **2026-09-16 起 GainMap 已接入引擎**：`plan.GainMap` 为真时 <see cref="RawColorPipeline"/>
    /// 走 GainMap 出口（zscale 线性化 → GainMapEncoder → XMP/MPF 打包），与旧专用管线
    /// <c>QueueProcessor.ProcessGainMapJpegAsync</c> 共用同一份解码串与参数公式
    /// （<see cref="GainMapJpegCodec"/>）。非 JPEG 格式的 GainMap 请求由**规划层**拒绝
    /// （<c>ColorStrategyPlanning</c> 按 <see cref="FormatColorCaps.SupportsGainMap"/>），路由不再拦。
    /// </para>
    /// <para>
    /// ⚠ **2026-09-18：`GainMap + 最长边缩放` 的拦截已删除**（原判据为
    /// <c>o.JpegGainMap &amp;&amp; o.EnableMaxDimension &amp;&amp; o.MaxDimension &gt; 0</c> ⇒ 往本清单加一条
    /// <c>EngineBlockCodes.GeometryWithGainMap</c>）。
    /// 它是一段**死拦截**：上游 <c>QueueProcessor.cs:421-470</c> 的预缩放中继（**所有后端共用**、
    /// 在格式分发之前）已把超限输入替换成缩过的临时件 ⇒ 引擎侧的
    /// <c>BuildMaxDimensionScaleFilter</c> 恒返回 null ⇒ 该判据**恒假**。
    /// 完整实测数字、以及「为什么故意**保留** <c>RawColorPipeline</c> 的同源 <c>throw</c>」，
    /// 见 <see cref="UnconsumedOutputSettingItems"/> 里的删除记录。
    /// </para>
    /// <para>
    /// ⚠ **2026-09-17（S2）**：本方法仍是**字符串**投影（公开 API 与既有调用点不动），
    /// 但其**唯一真值**已下移到 <see cref="UnconsumedOutputSettingItems"/>（带 <c>Code</c>/<c>Alternatives</c>）；
    /// 本方法逐字拼出与改动前**完全相同**的串（连分隔符「、」与顺序都不变）。
    /// </para>
    /// </summary>
    public static string? UnconsumedOutputSettings(FfmpegOptions? o)
    {
        var items = UnconsumedOutputSettingItems(o);
        if (items.Count == 0) return null;
        var texts = new string[items.Count];
        for (int i = 0; i < items.Count; i++) texts[i] = items[i].Text;
        return string.Join("、", texts);
    }

    /// <summary>
    /// 一条"引擎出口尚未承接"的**结构化**原因：<see cref="Code"/> 供程序判定/门禁断言，
    /// <see cref="Text"/> 与改动前 <c>UnconsumedOutputSettings</c> 的对应片段**逐字相同**。
    /// </summary>
    private readonly record struct UnconsumedSetting(
        string Code, string Text, IReadOnlyList<ColorAlternative> Alternatives);

    /// <summary>
    /// <see cref="UnconsumedOutputSettings"/> 的**结构化真值**（顺序 = 判据的固定顺序 ⇒
    /// 多项同时命中时 <c>BlockReason</c> 的 <c>Code</c> 取首项，确定性可断言）。
    /// </summary>
    private static IReadOnlyList<UnconsumedSetting> UnconsumedOutputSettingItems(FfmpegOptions? o)
    {
        var items = new System.Collections.Generic.List<UnconsumedSetting>(5);
        if (o == null) return items;
        var fmt = (o.Format ?? "").Trim().ToLowerInvariant();
        if (o.AnimationScaleW != 0)
            items.Add(new UnconsumedSetting(EngineBlockCodes.AnimationScaleUnsupported, "动画缩放",
                new[]
                {
                    new ColorAlternative(AlternativeKind.Option, "animation-scale-w",
                        "把动画缩放设回 0（不做缩放）⇒ 引擎编码出口可承接"),
                }));
        if (!string.IsNullOrWhiteSpace(o.TonemapCurve))
            items.Add(new UnconsumedSetting(EngineBlockCodes.LegacyTonemapCurve,
                $"旧 --tonemap-curve={o.TonemapCurve}（请改用 --color-tone-map）",
                new[]
                {
                    new ColorAlternative(AlternativeKind.Option, "color-tone-map",
                        "旧 --tonemap-curve 未接入引擎；改用引擎支持的 --color-tone-map"),
                }));
        // ════════════════════════════════════════════════════════════════════════════════════
        // ⚠⚠ **2026-09-18：`GeometryWithGainMap` 拦截已删除（Part C）** —— 此处只留删除记录。
        // ════════════════════════════════════════════════════════════════════════════════════
        // 【删除前它长什么样】判据 `o.JpegGainMap && o.EnableMaxDimension && o.MaxDimension > 0`
        //   ⇒ 往本清单加一条 `EngineBlockCodes.GeometryWithGainMap`，文案「GainMap + 最长边几何缩放
        //   （GainMap 出口不承接几何缩放：底图与增益图必须同几何）」+ 替代 `Option/max-dimension-enabled`。
        // 【为什么删 —— 它是一段「死拦截」】`QueueProcessor.cs:421-470` 有一道**所有后端共用**的预缩放中继
        //   （「在格式分发前插入」）：单帧输入超限时会先用 ffmpeg 缩到临时 PNG/JXL 并**替换
        //   `captured.InputPath`** ⇒ 引擎与专用管线看到的**已经是缩过的输入**
        //   ⇒ `BuildMaxDimensionScaleFilter` 恒返回 null ⇒ 本判据**恒假**、
        //   `RawColorPipeline.cs:228-234` 那个 `throw` 在产品里**不可达** —— 本拦截**从未真正生效**。
        //   实测（2026-09-18；删前/删后各一次，两次读数**完全相同**）：
        //     `--color-engine engine|legacy --jpeg-gain-map true --max-dimension-enabled true --max-dimension 128`
        //     ⇒ 退出码 0、产物 4621B、`images=2` / `iso min=0 max=2.3` / `mpf[1]…soi=True` / 探针 6/0
        //     ⇒ 与专用管线产物**同尺寸、同为真 Ultra HDR**（引擎日志 `[色彩引擎] ✅ 128x96 变换 1ms`）。
        //   ⇒ **删除是一次零行为变化的清理**：几何已由上游中继完成，引擎照常产出。
        // 【故意保留的东西】`RawColorPipeline.cs:228-234` 的同源 `throw` **保留**：它是同一条限制的
        //   **防御性守卫** —— 万一将来某条路径绕过上游中继，它会**响亮失败**，而不是静默产出一个
        //   底图与增益图尺寸对不上的容器。**删拦截 ≠ 删这条守卫。**
        // 【连带影响 —— 对 Part B 是必要条件】删掉本拦截后，该组合的 `ShouldRoute` 变为 **true**
        //   ⇒ 它不再落进 `QueueProcessor` 的 GainMap 专用分派 ⇒ 专用分派此刻的**残余触发点只剩
        //   `AnimatedInput`**（实测见 `Requested()` 的 XML 注释）。这正是 Part B（删专用分派 + 回退体）
        //   必须先有本步的理由：若 Part B 先删分派而本拦截还在 ⇒ 该组合 `ShouldRoute=false`
        //   ⇒ `auto` 下 `TryRunColorEngineAsync` 早退 ⇒ 落到传统分支 `BuildArguments`；而**规划层并不
        //   Reject 这个组合**（`PlanWithPolicy` 只看 `SupportsGainMap`，几何拦截在**路由层**）
        //   ⇒ `legacyReject=false` ⇒ **产出一个没有增益图的普通 JPEG**（静默降级）。
        //   ⚠ `EngineBlockCodes.GeometryWithGainMap` 常量**保留**在词表里（供历史日志/外部引用），
        //     但它在本方法里**不再被生产**。

        // ffmpeg 的 mjpeg 编码器**没有**渐进 JPEG 能力（实测 `-h encoder=mjpeg` 无该项；
        // 传统管线也从不发 `-progressive`）⇒ 显式选了它必须出声，不能静默当没选。
        // ⚠ 判据必须是 `> 0` 而**不是** `!= 0`：`JpegProgressiveId` 的取值域是
        //   `-1=自动 / 0=基线 / 1=渐进`（见 `MainWindow.ParseJpegProgressiveId`）⇒ `!= 0` 会把
        //   「自动」也判成「要渐进」，既误拦引擎、又打出一条"你选了渐进"的错误文案。
        //   2026-09-19 实测：改前 `--jpeg-progressive=-1` 触发本拦截；改后不再触发。
        // ⚠⚠ 2026-09-19 追加（放行 GainMap）：**GainMap 组合必须放行** —— 上面那句
        //   「ffmpeg 的 mjpeg 编码器无此能力」对 GainMap **不成立**：GainMap（Ultra HDR JPEG）
        //   的底图与增益图**恒由 cjpegli 编码**（`GainMapEncoder.EncodeJpegFileAsync`，
        //   `GainMapEncoder.cs:448-470`），而 cjpegli **支持** `-p 0..2`
        //   （实测：`-p 0` ⇒ SOF1 顺序、`-p 2` ⇒ SOF2 渐进，二者字节不同）。
        //   拦它的后果是**硬失败**（显式 engine ⇒ exit 90、零产物）—— 拒绝了一条**其实存在**的能力；
        //   非显式则回退专用管线（当时也不承接 ⇒ 静默得渐进底图，正是 R7 那个缺陷）。
        //   ⇒ 放行，由引擎 GainMap 出口（`RawColorPipeline.cs:324-338`）经**单一真值**
        //     `GainMapJpegCodec.ProgressiveLevelOf` 下发 `-p N`。
        //   ⚠ 放行**不等于**静默：cjpegli 不可用时 `GainMapEncoder` 会回退 ffmpeg mjpeg 并**点名**
        //     「无渐进能力、产物为基线」（`GainMapEncoder.cs:473-476`）。
        //   实测（2026-09-19，放行前后各一次）：`--jpeg-progressive 1 --jpeg-gain-map true
        //     --color-engine engine` ⇒ 放行前 exit 90 / 0 产物；放行后 exit 0 / 真渐进 Ultra HDR（SOF2）。
        //   `EngineBlockCodes.ProgressiveJpeg` 常量**保留**（非 GainMap 的 jpg 仍会生产它）。
        bool gainMapJpeg = o.JpegGainMap && (fmt == "jpg" || fmt == "jpeg");
        if (o.JpegProgressiveId > 0 && !gainMapJpeg)
            items.Add(new UnconsumedSetting(EngineBlockCodes.ProgressiveJpeg,
                "JPEG 渐进（ffmpeg 的 mjpeg 编码器无此能力；请改用 cjpegli 后端，或把该项设回自动）",
                new[]
                {
                    new ColorAlternative(AlternativeKind.Option, "jpeg-progressive",
                        "把 JPEG 渐进设回自动（0）（ffmpeg 的 mjpeg 编码器无渐进能力）⇒ 引擎编码出口可承接"),
                }));
        // 编码器侧的真实能力差异（如 libwebp 无 4:4:4）—— 单一真值在 ImageEncoderArgs
        // ⚠ 这一项**故意不给** Alternatives：`UnsupportedEncoderSettings` 是**三**个子情形
        //   （webp 4:4:4 / avif+libsvtav1 4:4:4 / jxl 无损重封装）的聚合串，且它是**传统管线共用**的
        //   单一真值 ⇒ 要给出"具体替代"必须把它也拆成结构化项（另一处改动，超出 S2 范围且扩大爆炸半径）。
        var encBad = ImageEncoderArgs.UnsupportedEncoderSettings(fmt, o);
        if (encBad != null)
            items.Add(new UnconsumedSetting(EngineBlockCodes.EncoderUnsupportedSetting, encBad,
                Array.Empty<ColorAlternative>()));
        return items;
    }

    /// <summary>综合判定：既被请求、又未被阻塞 ⇒ 走引擎。</summary>
    public static bool ShouldRoute(FfmpegOptions? o, string outputPath, bool inputIsAnimated)
        => Requested(o) && BlockReason(o, outputPath, inputIsAnimated) == null;
}
