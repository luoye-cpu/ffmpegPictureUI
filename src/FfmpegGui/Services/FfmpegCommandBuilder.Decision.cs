using System;
using System.Collections.Generic;
using FfmpegGui.Models;

namespace FfmpegGui.Services
{
    /// <summary>
    /// <see cref="FfmpegCommandBuilder"/> 的**单一裁决点**（技术路线步骤 3）。
    ///
    /// 为什么必须有它（历史缺陷，全部有命令行原文为证）：
    ///  • 「输出像素到底是什么色彩语义」此前散在三处各算一遍：
    ///    ① <c>BuildArguments</c> 的 tonemap / 目标 zscale / D1 超广归一 / iccgen 四段（顺序耦合的局部变量）；
    ///    ② <c>EffectiveOutputColorTokens</c>（PNG cICP 后置、UI 宣称值）；
    ///    ③ <c>ColorIntentFactory</c>（色彩引擎的目标描述符）。
    ///    ②③ 只会「能力钳制 + 目标映射」，**看不到①里发生的 tonemap** ⇒ HDR 源被压成 SDR 像素后，
    ///    宣称值仍是 bt2020/smpte2084，标签与像素完全相反（`ServiceProbe decision` 首轮实测 3 格）。
    ///  • 修法不是在②里补一段“如果 tonemap 就……”的第二套推断，而是把决定**只算一次**：
    ///    本类型即那次计算的产物，①按它写命令行，②③按它报宣称值。
    ///
    /// 契约：<b>命令行是唯一执行体，本类型不得产生任何命令行不会用到的字段</b>；
    /// 新需求要改色彩行为时，只改这里（并让 <c>ServiceProbe decision</c> 与交付门禁重新验收）。
    /// </summary>
    public static partial class FfmpegCommandBuilder
    {
        /// <summary>一次任务的全部色彩输出决定（像素链 + 出口语义 + 标注取舍）。</summary>
        public sealed class OutputColorDecision
        {
            // ── 输入侧（-i 之前的声明；保真路径下为 null=不写任何 CICP）──
            public string? DeclPrimaries;
            public string? DeclTrc;
            /// <summary>
            /// 输入矩阵声明（`-i` 前的 `-colorspace`）。**2026-09-16 新增** —— 此前矩阵只被写进
            /// <see cref="MatrixForOutput"/> 当"输出标签"，于是「精确色彩参数」里的矩阵
            /// **对输入解读毫无作用**（实测：无标签 YUV 源下 matrix=bt2020nc 与 bt709 产物 md5 相同、PSNR=inf）。
            /// 语义分工：本字段 = 如实声明源用什么矩阵（影响 YUV→RGB 解读）；
            /// <see cref="MatrixForOutput"/> = 产物应当标注什么（受容器能力 <see cref="CapMatrix"/> 约束）。
            /// </summary>
            public string? DeclMatrix;

            // ── 容器能力（唯一来源 GetFormatColorCapabilities，null=不约束）──
            public string? CapPrimaries;
            public string? CapTrc;
            public string? CapMatrix;
            public int CapMaxBitDepth = 16;
            public bool CapUseIcc;

            // ── 执行体：按顺序写入 -vf 的色彩链（tonemap / 目标 zscale / D1 归一 / iccgen 前置）──
            public readonly List<string> PixelChains = new();
            /// <summary>链跑完后输出的 YUV 矩阵（-colorspace 取值；null=沿用输入矩阵）。</summary>
            public string? MatrixForOutput;
            /// <summary>iccgen 是否要写进命令行（需附 ICC 且目标非 HDR）。</summary>
            public bool RunIccgen;
            /// <summary>iccgen 前是否要先 `format=rgb48le`（高位深 YUV 输入：iccgen 仅在 RGB 域工作）。</summary>
            public bool IccgenNeedsRgb;

            // ── 出口语义（= 宣称值：PNG cICP、UI 文案、引擎目标都读这三个）──
            public string? OutPrimaries;
            public string? OutTrc;
            /// <summary>
            /// **源容器**是否自带 CICP 标签（与出口语义无关，是源的事实；S2「只携带不推断」需要它）。
            /// </summary>
            public bool SourceHasCicp;
            /// <summary>像素是否已被 HDR→SDR 色调映射（true ⇒ 出口语义必为 SDR，绝不再报 PQ/HLG）。</summary>
            public bool TonemappedToSdr;
            /// <summary>无任何色彩链 ⇒ 像素原样（出口语义 = 源语义）。</summary>
            public bool PixelsUnchanged = true;
            /// <summary>超广色域保真路径：像素不动，仅附着命名 ICC。</summary>
            public bool Faithful;
            public string? FaithfulIccPath;

            /// <summary>
            /// 本次任务**实际生效**的策略（= 计划层 <c>ColorTransformPlan.EffectiveStrategy</c>，
            /// 见 <see cref="EffectiveColorStrategy"/>）。与用户声明值可能不同（S2 的三种覆盖）。
            /// </summary>
            public Models.ColorStrategy EffectiveStrategy;

            // ── 标注取舍 ──
            /// <summary>输出应当携带 ICC（iccgen 生成，或队列后置嵌入）。</summary>
            public bool AttachIcc;
            /// <summary>该容器的编码器能否把 iccgen 生成的 ICC 真正写进文件（实测表见 <see cref="EncoderWritesFilterIcc"/>）。</summary>
            public bool EncoderWritesFilterIcc;
            /// <summary>需要 ICC 而编码器会把它丢掉 ⇒ 队列必须用 exiftool 后置嵌入（否则输出零标注而像素是广色域）。</summary>
            public bool NeedsPostEmbedIcc;
            /// <summary>容器有 CICP 通路（PNG cICP / AVIF nclx / JXL 枚举）⇒ 可写 CICP。</summary>
            public bool CanWriteContainerCicp;
            /// <summary>无 ICC、无 CICP，但按容器惯例可安全地按隐含 sRGB 解读。</summary>
            public bool ImplicitSrgb;

            /// <summary>一行摘要（诊断/日志用；不参与决策）。</summary>
            public string Short()
                => $"出口={OutPrimaries ?? "—"}/{OutTrc ?? "—"} 直通={PixelsUnchanged} tonemapSDR={TonemappedToSdr} "
                   + $"附ICC={AttachIcc}(编码器落盘={EncoderWritesFilterIcc},后置={NeedsPostEmbedIcc}) "
                   + $"CICP={CanWriteContainerCicp} 隐式sRGB={ImplicitSrgb} 链数={PixelChains.Count}";
        }

        /// <summary>
        /// 计算本次任务的输出色彩决定（<b>唯一裁决点</b>）。
        /// ⚠ 两个已文档化的 side channel（均为旧行为，移进本函数是为了不让“判定位置”也分成两套）：
        ///  • <c>options.BitDepth</c> 超容器上限时被钳到上限（像素链 NeedsHdrToSdrTonemap 要读它，
        ///    所以必须在链判定**前**完成，与旧 BuildArguments 的位置一致）；
        ///  • <c>assignFaithfulPath=true</c> 时把保真 ICC 路径写回 <c>options.FaithfulIccPath</c>（队列后置步骤的开关）。
        /// </summary>
        /// <param name="assignFaithfulPath">
        /// true（<c>BuildArguments</c> 用）：把解析出的保真 ICC 路径写回 <c>options.FaithfulIccPath</c>，
        /// 队列后置步骤据此附着。false（只读宣称值时用）：只算不写，避免为“取一个宣称值”而改动调用方的 options。
        /// </param>
        public static OutputColorDecision DecideOutputColor(FfmpegOptions options, string inputPath,
            bool assignFaithfulPath = false)
        {
            var d = new OutputColorDecision();
            var fmt = (options.Format ?? "").ToLowerInvariant();
            // PNG/TIFF/APNG/JXL 是 RGB 原生格式：-colorspace（YUV 矩阵）会与其色彩块冲突，
            // 但 -color_primaries/-color_trc 仍需保留以正确解释输入色彩空间。
            bool isRgbNativeFmt = fmt is "png" or "tiff" or "apng" or "jxl";

            // ── 输入声明（简化模式=如实描述实际输入；高级模式=用户声明）──
            var (inputPrimaries, inputTrc, inputMatrix) = BuildColorArgsSplit(options, inputPath);

            // ── 点三：超广色域「像素原样 + 仅靠 ICC 描述」保真路径（必须在写 CICP 标签/任何像素转换前判定）──
            // ProPhoto/ROMM 色域超出 BT.2020：归一会削色；zscale 又无法命名其 primaries 与 1.8 传递
            // （旧行为按 sRGB 曲线线性化 + 矩阵转 BT.2020 → 像素与标签双错）。存在命名 ICC 时，
            // 业界正确做法是像素一帧不动 + 附着该 ICC（Photoshop/darktable/ICC 惯例）。
            d.FaithfulIccPath = options.FaithfulIccPath;
            if (string.IsNullOrWhiteSpace(d.FaithfulIccPath))
            {
                var fpm = ProbeInputColorMetadata(inputPath);
                d.FaithfulIccPath = ResolveFaithfulUltraWideIcc(options, fpm.sourceGamutName, fpm.colorTrc, fmt);
                if (assignFaithfulPath) options.FaithfulIccPath = d.FaithfulIccPath;
            }
            d.Faithful = !string.IsNullOrWhiteSpace(d.FaithfulIccPath);
            if (d.Faithful)
            {
                // 不写任何 CICP 标记（写 bt709 会与附着的 ProPhoto ICC 冲突；bt2020 则是削色后的假标签），
                // 且其后的「目标色域转换」与「D1 超广归一」均跳过（像素保持原样）。
                inputPrimaries = null; inputTrc = null; inputMatrix = null;
            }
            d.DeclPrimaries = inputPrimaries;
            d.DeclTrc = inputTrc;
            d.DeclMatrix = inputMatrix;      // 输入矩阵声明（`-i` 前）；与下面的输出标签职责分离
            // 输出 YUV 矩阵：链会改写它（与旧 BuildArguments 里的 outputColorSpace 局部变量同义）
            var outMatrix = inputMatrix;

            // ── 容器能力（含位深硬约束：与 BuildArguments 同一处施加，避免两侧口径不同）──
            var (capP, capT, capM, capBd, capIcc) = GetFormatColorCapabilities(fmt, options);
            d.CapPrimaries = capP; d.CapTrc = capT; d.CapMatrix = capM;
            d.CapMaxBitDepth = capBd; d.CapUseIcc = capIcc;
            if (options.BitDepth.HasValue && options.BitDepth.Value > capBd)
            {
                // 就地钳制会**擦掉用户的请求档** ⇒ 先把被钳掉的那一档记进 `BitDepthRequested`，
                // 规划层的 ①b 才能播"请求 10 ⇒ 交付 8"（任务 #35：webp 54 格 / avif 17 格零播报的根因）。
                // 取**更深**的那次：同一 options 被重入时，第二次进来读到的已是上一次的出口档，
                // 用 `??=` 会把它当成请求档、越记越浅。
                if (options.BitDepthRequested == null || options.BitDepth.Value > options.BitDepthRequested.Value)
                    options.BitDepthRequested = options.BitDepth;
                options.BitDepth = capBd;
            }

            var meta = string.IsNullOrEmpty(inputPath) || inputPath == "-"
                ? new ColorMetadata() : ProbeInputColorMetadata(inputPath);
            // ★ 策略判定必须取**计划层的实际生效策略**，不是用户声明值（见 EffectiveColorStrategy 的说明）：
            //   S2「携带」在规划层有三种显式覆盖，声明值与生效值可能不同；直接读声明值会让传统管线
            //   漏掉覆盖后的语义（GainMap 要求底图映射、HDR 无 ICC 携带通路、容器不能携带 ICC）。
            var strategy = EffectiveColorStrategy(options, inputPath);
            bool preserveSource = strategy == Models.ColorStrategy.CarryIcc;
            bool stripIcc = strategy == Models.ColorStrategy.BakeCicpOnly;
            // ⚠ 2026-10-04：语义判据改**值派生**（不再读 UI 布尔 `UseAdvancedColorParameters`）
            //   —— 勾选/不勾选不得有策略差别；命令行设了精确参数同样生效。
            // ⚠⚠ 必须用 **`HasExplicitColorTarget`（只认 CP/CT）**，**不能**用 `HasExplicitColorParams`
            //   （含 `ColorMatrix`）：下面的 `hasExplicitTarget` 是**互斥**形状（`!advanced && …`），
            //   而 `--color-matrix` 是**输入声明**、不是目标轴 ⇒ 算进来会让
            //   `--color-matrix X --color-space Y` 把 `ColorSpace` 目标整条丢掉
            //   （2026-10-04 实机确证：HDR(PQ) 源→png 变 `决策：None`、产物仍 `smpte2084/bt2020`）。
            bool advanced = options.HasExplicitColorTarget;
            bool hasExplicitTarget = !advanced
                && !string.IsNullOrWhiteSpace(options.ColorSpace)
                && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase);

            // 非 CICP 格式 + 非 sRGB 目标 → 需 iccgen 携带 ICC（JPEG/WebP 走 iccgen）
            // TIFF 通过元数据复制保留 ICC（capUseIcc=false，因 iccgen 不支持高位深/HDR）
            if (capIcc && !stripIcc) d.AttachIcc = true;
            // ── 携带策略(CarryIcc): 保留源 ICC（源无则由 iccgen 补匹配标准 ICC）──
            // 下面各转换块（tonemap / 目标色域 / D1 归一）均以 preserveSource 跳过，实现「不转换、保源」。
            if (preserveSource) d.AttachIcc = true;   // 源无 ICC 时补标准 ICC；有则由 QueueProcessor 回带源 ICC

            d.CanWriteContainerCicp = fmt is "png" or "apng" or "avif" or "jxl";
            d.EncoderWritesFilterIcc = EncoderWritesFilterIcc(fmt);

            // 出口语义的**基线**：像素未转换时 = 源语义（仍受能力约束）。
            // ⚠ 但“源语义来自 ICC”且该 ICC 空间**无法用 CICP 命名**（ProPhoto/Adobe 等）时，
            //   容器默认标签会被报成 bt709——那不是输出语义，写出去就是假标签（实测回归过）。
            // fromSource：出口语义是否**来自源容器自带的 CICP 标签**（既非用户指定的目标、也非 ICC 推断）
            // ——S2「只携带不推断」的判据，任何改变像素的链跑过后都必须置假。
            bool fromSource = false;
            string? outP, outT;
            bool baselineNameable = true;
            if (meta.hasIccSemantics && !string.IsNullOrWhiteSpace(meta.sourceGamutName))
            {
                var tkName = ColorSpaceRegistry.FfmpegTokens(meta.sourceGamutName);
                baselineNameable = tkName.primaries != null && tkName.trc != null;
            }
            if (advanced && !string.IsNullOrWhiteSpace(options.ColorTrc))
            {
                // 高级模式：出口语义 = 用户声明（能力约束优先）
                outP = capP ?? options.ColorPrimaries ?? meta.colorPrimaries;
                outT = capT ?? options.ColorTrc;
            }
            else if (hasExplicitTarget)
            {
                var tg = MapSimplifiedColorSpace(options.ColorSpace!);
                outP = capP ?? tg.primaries;
                outT = capT ?? tg.trc;
            }
            else
            {
                fromSource = Tagged(meta.colorPrimaries) && Tagged(meta.colorTrc);
                if (!baselineNameable) { outP = null; outT = null; }
                else { outP = capP ?? meta.colorPrimaries; outT = capT ?? meta.colorTrc; }
            }
            d.SourceHasCicp = fromSource;

            bool skipAutoBt2020Zscale = false;   // 已完成色彩转换 ⇒ 不再自动归一
            bool skipHdrTonemap = false;         // 已插入 tonemap/转换链 ⇒ 不再二次处理

            // ── HDR→SDR 色彩降级 ──
            if (!skipHdrTonemap && !preserveSource)
            {
                var needsTonemap = NeedsHdrToSdrTonemap(options, inputPath, inputPrimaries, inputTrc);
                // 修复：TIFF 也是 RGB 原生格式，但 HDR→TIFF(SDR) 必须 tonemap 转 SDR 色彩。
                // 原条件 `!isRgbNativeFmt` 使 TIFF/PNG 跳过 tonemap，导致 HDR 源直写 SDR 目标色彩错误。
                // 判定：非 RGB 原生格式 或 (需要 tonemap 的 TIFF/PNG SDR 目标)。
                bool tiffNeedsTonemap = (fmt is "tiff" or "tif") && needsTonemap;
                // PNG 不再按位深豁免：needsTonemap 已综合“目标是否 SDR”与“能否标注”，
                // 旧的 `<= 8` 门与上游短路重复，会把 16-bit PNG 的 HDR→SDR 再漏掉。
                bool pngNeedsTonemap = (fmt is "png" or "apng") && needsTonemap;
                // ── 任务 #44（2026-09-27）：JXL 也补上这条许可，否则 `isRgbNativeFmt` 的豁免**只**咬住 JXL ──
                // `isRgbNativeFmt` 的立项理由是「RGB 原生容器不该写 `-colorspace`（YUV 矩阵声明）」
                //（见其定义处那句注释），而它被**复用**成了「跳过 tonemap 链」的判据 ⇒ TIFF/PNG 各自补了
                // 许可支，**只剩 JXL 没有**：`NeedsHdrToSdrTonemap` 对 avif/jxl/jxr 的豁免早已在 #43 改成
                // 「仅未指定目标时保留 HDR 直通」，于是 JXL + 显式 SDR 出现 `needsTonemap=true` 而链为空
                // ⇒ 两个后果（均有实测）：
                //   ① `--encoder ffmpeg`：`d.RunIccgen` 仍为真（`frameIsHdr` 看的是**目标** trc，不是帧），
                //      而帧上的 trc 还是 `smpte2084` ⇒ PQ 帧塞给 iccgen ⇒ `Error while filtering:
                //      Not yet implemented` ⇒ exit=1、零产物（#43 在 avif 上修掉的同一个形状）；
                //   ② cjxl 直编旁路：闸门读 `PixelChains.Count` ⇒ 恒 0 ⇒ 永不改道，用户的显式目标被静默吃掉。
                // 判据本身不动（`NeedsHdrToSdrTonemap` 一支字节未改 ⇒ PNG 支/avif 支语义不变），这里只是
                // 让 JXL 与 avif 走**同一条**结论。auto / HDR 目标（P3 PQ）两列 `needsTonemap=false`
                // ⇒ 本支不介入，产物必须逐字节不变（`verify-decision-delivery` ⑰b 反向锁同口径）。
                bool jxlNeedsTonemap = fmt is "jxl" && needsTonemap;
                if (needsTonemap && (!isRgbNativeFmt || tiffNeedsTonemap || pngNeedsTonemap || jxlNeedsTonemap))
                {
                    // tonemap 滤镜内部自带色彩空间转换（HDR→linear→SDR），无需外部 zscale 链。
                    // 原实现 zscale=t=linear→tonemap→zscale 存在两个问题：
                    //   ① zscale (libzimg) 对 YUV 4:2:0 输入要求宽高可被子采样因子整除，
                    //      奇数尺寸（如 1921x1081）报 code 1027。
                    //   ② 中间 zscale 依赖帧色彩标签，无标签时报 3074 (no path between colorspaces)。
                    // format=yuv444p 前缀：4:4:4 无子采样约束，任意尺寸可用；也避免 4:2:0 色度损失。
                    //
                    // 2026-08-14 修复（实测驱动）:
                    //   tonemap 滤镜输出为 linear light 像素，帧色彩标签泄漏为 unspecified/linear，
                    //   直接输出会导致：① 容器 CICP 标签错误（PNG 实测 cICP=primaries=2,transfer=8）
                    //                    ② 像素未应用 gamma（实测 YAVG 12416 vs 正确 gamma 编码 30770，
                    //                       linear→gamma 关系精确吻合，画面明显过暗）
                    //   修复链: tonemap → RGB → zscale 应用目标 gamma + primaries 转换 → 正确像素+标签。
                    var tmSrcP = inputPrimaries ?? "bt2020";  // tonemap 保持输入 primaries，需转换到目标
                    var tmDstP = "bt709";
                    // ── SDR 出口的**传递曲线默认值** = iec61966-2-1（sRGB 分段），不是 bt709（任务 #26②，2026-09-26）──
                    // 为什么原来那个 "bt709" 是**双重不诚实**（实测，取证见 docs/COLOR_MATRIX_FIX_PLAN 的 #31 段
                    //   与 `tests/output/_diag_31cur/`）：
                    //   ① **像素**：zimg 把 `t=bt709` 实现成**纯 γ(1/2.4)**，而 ITU-R BT.709 的定义式是
                    //      **分段**（线性段 0.018/4.5，与 sRGB 的 0.04045/12.92 不同）⇒ 写 `t=bt709` 得到的
                    //      既不是标准 BT.709 也不是 sRGB；
                    //   ② **标签**：链末把这个 token 直接交出去（`outT = tmDstT`）⇒ PNG 写 `cICP.transfer=1`、
                    //      其他容器写 `-color_trc bt709` ⇒ **标签声称的是那条它根本没实现的曲线**。
                    //      而 CICP/H.273 里**没有**"纯 γ2.4"这个编号 ⇒ 这条缺陷**无法靠改标签收口**，
                    //      只能把**像素**提到标签水位（维护者 2026-09-26 定样 A + "图像文件选 sRGB"）。
                    // ⇒ 选 `iec61966-2-1` 的三个理由：zimg **确实**实现了它的分段形式（`ColorSpaceRegistry` 已登记
                    //   "有 sRGB 分段、无 BT.709 分段"）；它有正式编号（CICP transfer=13）⇒ 标签兑现得了；
                    //   且引擎侧 `SdrTwinOf` 的 SDR 候选表**本来就把它排第一** ⇒ 两条管线在此收敛
                    //   （修复前实测：引擎格像素=sRGB、标签=cICP 1/1 + sRGB iCCP，两边各错一半）。
                    // ⚠ 这条只改**未指定目标时的默认值**：`capT`（TIFF/JPEG/WebP 的 SDR 硬钳）与用户显式目标
                    //   （`hasExplicitTarget` / 高级 trc）仍然优先，不改它们的语义。
                    var tmDstT = "iec61966-2-1";
                    // 首先应用格式能力约束（TIFF/JPEG/WebP 目标恒为 SDR）
                    tmDstP = capP ?? tmDstP;
                    tmDstT = capT ?? tmDstT;
                    if (hasExplicitTarget)
                    {
                        // 简化模式：目标为所选色彩空间（NeedsHdrToSdrTonemap 已排除 HDR 目标）
                        // 但格式能力约束仍优先（如 TIFF 目标恒为 SDR trc）
                        var target = MapSimplifiedColorSpace(options.ColorSpace!);
                        tmDstT = capT ?? target.trc ?? "bt709";
                        tmDstP = capP ?? target.primaries ?? "bt709";
                    }
                    else if (advanced && !string.IsNullOrWhiteSpace(options.ColorTrc))
                    {
                        // 高级参数模式：目标为所选 trc/primaries（能力约束优先）
                        tmDstT = capT ?? options.ColorTrc!;
                        tmDstP = capP ?? options.ColorPrimaries ?? "bt709";
                    }
                    // 矩阵同样受能力约束
                    var tmDstM = capM ?? "bt709";
                    // 注：目标能力不满足时由 **UI 选项过滤 + 契约层 Reject** 处理，不在此静默改写后记日志
                    // （“降级告警”会把未实现伪装成能力限制，也会掩盖真正该拒绝的组合）。
                    // P4: tonemap 曲线可选（hable 默认保留 param=0.5 观感；mobius/reinhard 用算法默认）。
                    var tmCurve = string.IsNullOrWhiteSpace(options.TonemapCurve) ? "hable" : options.TonemapCurve!.Trim().ToLowerInvariant();
                    var tmTone = tmCurve == "hable" ? "hable:param=0.5" : tmCurve;
                    d.PixelChains.Add(
                        $"format=yuv444p,tonemap={tmTone},format=rgb48le," +
                        $"zscale=pin={tmSrcP}:tin=linear:min=gbr:p={tmDstP}:t={tmDstT}:m={tmDstM}");
                    // tonemap 末级 zscale 已把像素矩阵转为 tmDstM；同步更新输出矩阵标记。
                    // 否则沿用源 RGB 的 inputMatrix="gbr"（RGB PNG 探测值）→ 输出 -colorspace gbr
                    // 被 libwebp_anim/mjpeg 等 YUV 编码器拒绝（"Undefined constant ... in 'gbr'" → Invalid argument → 0 字节）。
                    outMatrix = tmDstM;
                    // ★ 本链的出口语义即这串 zscale 的 p=/t=：宣称值必须跟着改（旧实现在此静默漏掉，
                    //   造成“像素已 tonemap 成 SDR、宣称仍写 PQ”的标签/像素相反缺陷）。
                    outP = tmDstP; outT = tmDstT;
                    d.TonemappedToSdr = true; d.PixelsUnchanged = false; d.SourceHasCicp = false;
                    skipHdrTonemap = true;
                }
            }

            // ── 简化模式目标色域转换（SDR→HDR / SDR→SDR 色域映射）──
            // 当用户选择非 auto 目标色域且未使用 ICC 烘焙/高级参数时，
            // 添加 zscale 滤镜将像素从「实际输入色彩」转换到「目标色彩」。
            // zscale 输出帧自动携带目标 primaries/trc 标签，编码器会传递到输出文件。
            // 注意：HDR→SDR 不在此处理（由 NeedsHdrToSdrTonemap 使用 tonemap 曲线）。
            if (!skipAutoBt2020Zscale && !d.Faithful && !preserveSource && hasExplicitTarget)
            {
                var targetParams = MapSimplifiedColorSpace(options.ColorSpace!);
                if (targetParams.primaries != null && targetParams.trc != null)
                {
                    // 应用格式色彩能力约束：受限格式 (JPEG/WebP/TIFF) 钳制目标到支持范围
                    var dstP = capP ?? targetParams.primaries!;
                    var dstT = capT ?? targetParams.trc!;
                    var dstM = capM ?? targetParams.matrix ?? "bt709";

                    // 实际输入色彩（来自 BuildColorArgsSplit 的 -i 前声明）
                    var srcP = inputPrimaries ?? "bt709";
                    var srcT = inputTrc ?? "iec61966-2-1";
                    var srcM = outMatrix ?? "bt709";

                    // 判断源是否为 HDR（PQ/HLG）→ 若目标是 SDR，交给 tonemap 处理
                    var srcIsHdr = srcT.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
                                || srcT.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
                    var dstIsHdr = dstT.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
                                || dstT.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);

                    // 仅在源≠目标时添加 zscale（避免无意义转换）
                    // 排除 HDR→SDR（由 tonemap 处理，避免高光裁剪）
                    if ((!string.Equals(srcP, dstP, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(srcT, dstT, StringComparison.OrdinalIgnoreCase))
                        && !(srcIsHdr && !dstIsHdr))
                    {
                        // format=rgb48le 前缀（RGB 域转换）：规避 libzimg 两个问题——
                        //  ① 对 YUV 4:2:0 输入的尺寸整除要求（奇数尺寸 1027 错误）
                        //  ② RGB 输入时无色彩标签导致的 3074 (no path between colorspaces)
                        // RGB 域无子采样约束，任意尺寸可用；16-bit 精度避免色度损失。
                        // 修复：输出为 YUV 4:2:0 时，先确保宽高为偶数（libzimg 要求 yuv420p 宽高可被 2 整除）。
                        // 通过 scale=ceil(iw/2)*2:ceil(ih/2)*2 在 zscale 前插入偶数化。
                        // 偶数化判据按**当前**的 YUV 矩阵（含 tonemap 链已改写后的值；为空则不动尺寸）
                        var needsEven = outMatrix == "bt709" || outMatrix?.StartsWith("bt2020") == true;
                        var preScale = needsEven ? "scale=ceil(iw/2)*2:ceil(ih/2)*2:flags=lanczos," : "";
                        d.PixelChains.Add($"format=rgb48le,{preScale}zscale=pin={srcP}:tin={srcT}:min={srcM}:p={dstP}:t={dstT}:m={dstM}");

                        // zscale 已完成色彩转换：输出矩阵与出口语义都以它为准
                        outMatrix = dstM;
                        outP = dstP; outT = dstT;
                        d.PixelsUnchanged = false; d.SourceHasCicp = false;
                        skipHdrTonemap = true;
                    }
                }
            }

            // ── 默认(看齐源)超广色域归一化 (D1) ──
            // 无显式目标色彩空间时，若源为超广色域(Adobe RGB/ProPhoto，zscale/iccgen 不能命名)，
            // 将像素归入最接近的可表达空间并声明：受限格式(仅 SDR)→转 sRGB；否则→BT.2020。
            if (!skipAutoBt2020Zscale && !d.Faithful && !skipHdrTonemap && !preserveSource
                && !advanced
                && (string.IsNullOrWhiteSpace(options.ColorSpace)
                    || options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase)))
            {
                var pm = ProbeInputColorMetadata(inputPath);
                if (pm.hasIccSemantics && ColorSpaceRegistry.IsUltraWide(pm.sourceGamutName))
                {
                    bool fmtAllowsWide = capP == null
                        || capP.Contains("2020") || capP == "smpte432" || capP == "smpte431";
                    bool uwHlg = pm.colorTrc == "arib-std-b67";
                    bool uwHdr = pm.colorTrc == "smpte2084" || uwHlg;   // 源为 HDR 则归一保留 HDR 传递
                    string? ultraFilter; string np, nt, nm;
                    if (fmtAllowsWide)
                    {
                        // 归入标准 BT.2020，传递函数感知 HDR（HDR→PQ/HLG，SDR→bt709）。
                        ultraFilter = ColorSpaceRegistry.BuildUltraWideToBt2020Filter(pm.sourceGamutName, uwHdr, uwHlg);
                        (np, nt, nm) = ColorSpaceRegistry.UltraWideNormalizeTokens(pm.sourceGamutName, uwHdr, uwHlg);
                    }
                    else
                    {
                        // 受限格式(仅 SDR) → 转 sRGB。
                        ultraFilter = ColorSpaceRegistry.BuildGamutTransformFilter(pm.sourceGamutName, "sRGB");
                        var (tp, tt, tm) = ColorSpaceRegistry.FfmpegTokens("sRGB");
                        np = tp!; nt = tt!; nm = tm!;
                    }
                    if (ultraFilter != null)
                    {
                        d.PixelChains.Add(ultraFilter);
                        // 归一后的语义就是这条链的出口标签（旧实现只改局部变量，宣称值取不到 ⇒ 同类漏报）
                        outP = np; outT = nt;
                        outMatrix = nm;
                        d.AttachIcc = true;       // 生成目标标准 ICC，保证广色域正确声明
                        d.PixelsUnchanged = false; d.SourceHasCicp = false;
                        skipAutoBt2020Zscale = true; skipHdrTonemap = true;
                        // 下游 iccgen 的“输入是否 HDR / 是否广色域”判据改用归一后的语义
                        inputPrimaries = np; inputTrc = nt;
                    }
                }
            }

            // ── 非 CICP 格式 + 非 sRGB → 自动 ICC 嵌入 ──
            // JPEG/WebP 不支持 CICP，若输出非 sRGB，需 iccgen 补充 ICC
            // TIFF 已改为元数据复制 (capUseIcc=false)，不走 iccgen
            bool isNonCicpFormat = fmt is "jpg" or "jpeg" or "webp";
            bool isNonSrgb = options.ColorSpace != null
                          && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase)
                          && !options.ColorSpace.Equals("sRGB", StringComparison.OrdinalIgnoreCase);
            if (isNonCicpFormat && isNonSrgb && !d.AttachIcc && !stripIcc) d.AttachIcc = true;

            // D2: 广色域/超广色域输出用「标准 ICC + CICP」完整描述（BakeOnly 除外，用户显式不要 ICC）。
            // CICP 已由 -color_primaries/-color_trc 与 zscale 帧标签全程携带；此处对非 sRGB/BT.709
            // 的广色域额外强制 iccgen 生成匹配的标准色域 ICC（PNG/AVIF/JXL/TIFF 可同时带 cICP+iCCP）。
            var outTargetSpec = ColorSpaceRegistry.Resolve(options.ColorSpace);
            bool outIsWideGamut = inputPrimaries is "smpte432" or "smpte431" or "bt2020"
                || outTargetSpec is { Kind: ColorSpaceRegistry.GamutKind.Wide or ColorSpaceRegistry.GamutKind.UltraWide };
            // iccgen 不支持 HDR 传递(smpte2084/arib，实测 "Not yet implemented")：有效目标 trc（格式钳制优先）为 HDR
            // 时跳过 iccgen，HDR 由 cICP/CICP 表达。修复：旧代码对 HDR 广色域目标仍 iccgen → 编码失败或产出错误 ICC。
            // SDR-only 格式(JPEG/WebP/TIFF)的 capTrc 已钳 SDR（且 HDR 目标经 tonemap 转 SDR）→ frameIsHdr=false，iccgen 正常。
            // ⚠ 上面那句"HDR 源经 tonemap 转 SDR"对 avif/jxl/jxr **只在用户显式指定了目标时才成立**：
            //   `NeedsHdrToSdrTonemap` 对这三个格式在「未指定目标」时保留 HDR 直通（那时 frameIsHdr 仍为真、
            //   由 cICP/CICP 表达，本来就不该来这条分支）。任务 #43 的缺陷正是这条豁免**曾经无条件**，
            //   于是 legacy 链把 PQ 帧直接塞给 iccgen ⇒ "Not yet implemented" ⇒ 非零退出 + 零产物。
            //   把那条豁免改回无条件 = 复发 #43；判据锁在 `verify-decision-delivery` ⑰b（四条，含 auto 列的
            //   "不许出现 tonemap"这条反向锁）。
            var effDstTrc = capT
                ?? (advanced ? options.ColorTrc : outTargetSpec?.OutTrc)
                ?? inputTrc;
            bool frameIsHdr = effDstTrc is "smpte2084" or "arib-std-b67";
            if (!d.AttachIcc && !stripIcc && outIsWideGamut && !frameIsHdr) d.AttachIcc = true;

            if (d.Faithful) d.AttachIcc = false;   // 保真路径：iccgen 只能产出 BT.709/2020 等可命名空间的 ICC，
                                                   // 与未转换的源像素失配；改由队列后置附着命名 ICC。
            // ═══════════════════════════════════════════════════
            //  ICC 嵌入 — iccgen 自动生成标准 ICC
            //  关键修复：iccgen 滤镜仅在 RGB 域工作（对 10-bit+ YUV 输入报
            //  "Not yet implemented"）。因此 iccgen 前必须确保像素为 RGB 8/16-bit。
            //  HDR 源（PQ/HLG YUV）需要先 tonemap → RGB，否则 iccgen 崩溃。
            // ═══════════════════════════════════════════════════
            d.RunIccgen = d.AttachIcc && !frameIsHdr && !stripIcc;
            if (d.RunIccgen)
            {
                var probeMeta = GetCachedProbe(inputPath);
                bool inputIsHighBdYuv = !probeMeta.isRgb && probeMeta.bitDepth > 8;
                if (inputIsHighBdYuv)
                {
                    // HDR YUV 源 → iccgen 前强制 RGB 转换（iccgen 仅支持 RGB 输入）
                    // 若还需 tonemap，tonemap 链已在前面处理过，这里只负责格式转换。
                    d.IccgenNeedsRgb = true;
                }
            }
            // 编码器把 iccgen 的 ICC 丢掉（实测唯一已知的容器是 WebP）⇒ 队列必须后置嵌入同一张 ICC。
            // 例外：出口语义本就是 sRGB、而该容器“未标注即按 sRGB 解读”⇒ 不嵌也是对的，
            //   嵌了只会给每张图多添几十~几百字节的无用 ICC（并把“无标注直通”这一契约结局弄成双轨）。
            bool unlabeledIsFine = FfmpegGui.Services.ColorMapping.ColorMappingEngine.CapsFor(fmt).UnlabeledMeansSrgb
                && (outP == null || outP == "bt709") && (outT == null || outT == "iec61966-2-1");
            // 不可命名时生成不了标准 ICC（GetOrGenerateStandardIcc 要的是成对令牌），后置无意义。
            d.NeedsPostEmbedIcc = d.RunIccgen && d.EncoderWritesFilterIcc == false && !preserveSource
                && !unlabeledIsFine && outP != null && outT != null;

            d.OutPrimaries = outP; d.OutTrc = outT;
            d.MatrixForOutput = outMatrix;
            d.EffectiveStrategy = strategy;
            d.ImplicitSrgb = !d.AttachIcc && !d.CanWriteContainerCicp && !d.Faithful
                && (outP == null || outP == "bt709") && (outT == null || outT == "iec61966-2-1");
            return d;
        }

        /// <summary>源容器标签判据：非空且不是 unspecified/unknown（这些等于没标）。</summary>
        private static bool Tagged(string? v)
            => !string.IsNullOrWhiteSpace(v) && v is not ("unspecified" or "unknown");

        // ══════════════════════════════════════════════════════════════════════════════
        //  传统管线的**策略来源**：计划层 EffectiveStrategy（唯一真值）
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>重入保护：装配色彩意图时 <see cref="ColorMapping.ColorIntentFactory.FromOptions"/>
        /// 会回调 <see cref="EffectiveOutputColorTokens"/>（→ <see cref="DecideOutputColor"/>）取目标语义，
        /// 若那条路径再算一次策略就会无限递归。重入层用声明值即可（它只影响目标语义，不影响策略覆盖）。</summary>
        [ThreadStatic] private static bool _inEffectiveStrategy;

        private static readonly Dictionary<string, (DateTime Time, Models.ColorStrategy S)> StrategyCache = new();
        private static readonly object StrategyCacheLock = new();
        private const double StrategyCacheTtlSeconds = 10.0;   // 与 GetCachedProbe 同口径
        private const int StrategyCacheMax = 256;

        /// <summary>
        /// 传统管线（<see cref="DecideOutputColor"/> / <see cref="BuildArguments"/>）应当遵守的
        /// **实际生效策略** —— 与色彩引擎走**同一份规划层裁决**（<c>ColorMappingEngine.Plan</c> 的
        /// <c>EffectiveStrategy</c>），而不是直接读用户声明的 <see cref="FfmpegOptions.ColorStrategy"/>。
        ///
        /// 为什么必须同源：S2「携带」在规划层有**三种显式覆盖**（<c>ColorStrategyPlanning.ResolveStrategy</c>）：
        ///   ① 生成 GainMap（功能定义要求底图 tone-map + 色域映射）⇒ 覆盖为 S1/S4，**必须改像素**；
        ///   ② HDR 源无「携带 HDR ICC」通路 ⇒ 仍是 S2，但结局只能是标注继承；
        ///   ③ 容器不能携带任意 ICC ⇒ 仍是 S2，但结局是 Reject。
        /// 传统管线此前只看声明值 ⇒ 覆盖①之后仍按「零改动保源」跑（像素该映射却不映射），
        /// 于是"同一条策略"在两条管线上结局不一致（本项目反复修过的"三处不一致"类缺陷）。
        /// 改为从计划层推导后，覆盖自动跟着生效，S3 的「禁 iccgen」同样以生效策略为准。
        ///
        /// 约定：装配失败（<see cref="ColorMapping.ColorIntentFactory.FromOptions"/> 返回 null）或规划层抛错时
        /// **回退到声明值**（宁可保持旧行为，也不猜一个策略出来）。结果按 10s TTL 缓存，避免每次
        /// 取标签都重新启动 exiftool 抽取 ICC（<see cref="DecideOutputColor"/> 每个任务会被调用 2~4 次）。
        ///
        /// **成本**：非 S2 策略零开销（见下方成本闸）；S2 首次调用付一次意图装配（A/B 实测中位
        /// **+250ms/图**，主要是 exiftool 抽 ICC），同图后续调用命中 10s 缓存 ⇒ 0。
        /// </summary>
        public static Models.ColorStrategy EffectiveColorStrategy(FfmpegOptions options, string inputPath)
        {
            var declared = options.ColorStrategy;
            if (_inEffectiveStrategy) return declared;      // 重入 ⇒ 声明值（见 _inEffectiveStrategy 说明）

            // 成本闸：只有 **S2「携带」** 可能被规划层覆盖（`ColorStrategyPlanning.ResolveStrategy` 的第一行
            // 就是 `declared != CarryIcc ⇒ 原样返回`）⇒ 其余三种策略的生效值**恒等于**声明值，
            // 无需为"取一个必然相同的值"去装配意图（那要启动一次 exiftool 抽 ICC，实测 ~250ms/图）。
            // ⚠ 若将来给其它策略加了覆盖，必须同步放宽本判据（判据与 ResolveStrategy 是成对的）。
            if (declared != Models.ColorStrategy.CarryIcc) return declared;

            var key = $"{inputPath}\u0001{options.Format}\u0001{declared}\u0001{options.JpegGainMap}";
            lock (StrategyCacheLock)
            {
                if (StrategyCache.TryGetValue(key, out var hit)
                    && (DateTime.UtcNow - hit.Time).TotalSeconds < StrategyCacheTtlSeconds)
                    return hit.S;
            }

            _inEffectiveStrategy = true;
            Models.ColorStrategy eff;
            try
            {
                var intent = ColorMapping.ColorIntentFactory.FromOptions(options, inputPath, out _);
                if (intent == null) eff = declared;
                else
                {
                    // GainMap 是**输出结构**请求，规划层看不到 options（GainMapRequested 此前无装配点）⇒
                    // 由传统管线在此补上这一位，否则覆盖①永不触发、S2+GainMap 会静默产出"未映射的底图"。
                    intent.GainMapRequested = options.JpegGainMap;
                    eff = ColorMapping.ColorMappingEngine.Plan(intent).EffectiveStrategy;
                }
            }
            catch { eff = declared; }
            finally { _inEffectiveStrategy = false; }

            lock (StrategyCacheLock)
            {
                if (StrategyCache.Count >= StrategyCacheMax) StrategyCache.Clear();
                StrategyCache[key] = (DateTime.UtcNow, eff);
            }
            return eff;
        }

        /// <summary>
        /// **S6：传统管线应当消费的完整裁决**（与色彩引擎**同一份** <c>ColorMappingEngine.Plan</c>）。
        ///
        /// <para>
        /// 与 <see cref="EffectiveColorStrategy"/> 的关系：后者只取 <c>EffectiveStrategy</c>（一个策略枚举），
        /// 本方法取**整份计划**（含 <c>ColorTransformPlan.Verdict</c>）—— 调用方据此执行
        /// 「Reject ⇒ 不产出 / ProceedWithDegradation ⇒ 产出 + 逐条降级日志 / Proceed ⇒ 照常」。
        /// 规划层是**唯一真值**，两条管线不允许各有一套结局（规划 §3 P3「单一消费接口」）。
        /// </para>
        ///
        /// <para>
        /// ⚠ **故意不设 <see cref="_inEffectiveStrategy"/>**（与 <c>QueueProcessor.TryRunColorEngineAsync</c>
        /// 的调用点逐字一致）：该标志会让意图装配内部的 <see cref="EffectiveOutputColorTokens"/> →
        /// <see cref="DecideOutputColor"/> → <see cref="EffectiveColorStrategy"/> 退化为「声明值」，
        /// 从而**改变目标令牌**、让本方法算出的计划与引擎那份不一致 —— 那正是本项要消灭的东西。
        /// 不设它时嵌套深度最多两层（内层置位后外层返回声明值），不会无限递归。
        /// </para>
        ///
        /// <para>
        /// **代价**：一次意图装配（含一次 exiftool 抽 ICC，A/B 实测中位 +250ms/图）。**故意不缓存**：
        /// 计划依赖的选项远多于 <c>EffectiveColorStrategy</c> 那个 4 字段缓存键（ColorSpace / ColorTrc /
        /// IccFilePath / BitDepth / ToneMap / EncoderBackend / Lossless / …），用一个不全的键缓存**裁决**
        /// 会引入「同一个源在 10s 内换目标却拿到旧裁决」这类静默错误 —— 那比 250ms 昂贵得多。
        /// 传统管线如今是显式逃生门，这点开销可接受。
        /// </para>
        ///
        /// <returns>
        /// 不可装配时返回 <c>null</c>（例如 DNG/PPM/BMP 这类**连色彩通路都没有**的出口 ——
        /// <see cref="ColorMapping.ColorIntentFactory.FromOptions"/> 对它们返回 null），
        /// 此时调用方**保持既有行为**（传统管线是该格式的正常归属，不猜裁决）。
        /// </returns>
        /// </summary>
        public static ColorMapping.ColorTransformPlan? ResolveLegacyVerdictPlan(FfmpegOptions options, string inputPath)
        {
            try
            {
                var intent = ColorMapping.ColorIntentFactory.FromOptions(options, inputPath, out _);
                if (intent == null) return null;
                // 与 <see cref="EffectiveColorStrategy"/> 同款：GainMap 是**输出结构**请求，
                // 规划层看不到 options ⇒ 必须由传统管线补上这一位（否则覆盖①不触发）。
                intent.GainMapRequested = options.JpegGainMap;
                return ColorMapping.ColorMappingEngine.Plan(intent);
            }
            catch { return null; }
        }

        /// <summary>
        /// <c>-vf iccgen</c> 生成的 ICC 能否被该容器的**编码器**写进文件。
        /// 实测（2026-09-14，ffmpeg-full 2026.9.2 + exiftool 13.59；同一 Display-P3 源、同一 iccgen 链）：
        ///   png ✅ iCCP / jpg ✅ APP2 / tiff ✅ ICC Profile 标签（ChromaticityChannel1=0.67999 0.32001 = P3）
        ///   webp ❌ **RIFF 内根本没有 ICCP 块**（13250B 产物里搜不到 "ICCP"）；
        ///          用 exiftool 对同一文件后置嵌入同一 ICC 后 ✅ 可读回 ⇒ 队列必须补这一步。
        /// 未列入的容器（avif/jxl/gif/…）：本表只登记**已实测会丢弃**者，其余按“编码器自己处理”不额外后置，
        /// 避免凭猜测多写一遍 ICC（AVIF 的 ICC 由 libaom 写、JXL 走 cjxl icc_pathname，均有各自通路）。
        /// </summary>
        private static bool EncoderWritesFilterIcc(string fmt)
            => fmt is not ("webp");

        /// <summary>
        /// 本任务**输出像素的实际色彩语义**（CICP 令牌），与 <see cref="BuildArguments"/> **同源**：
        /// 就是 <see cref="DecideOutputColor"/> 的出口语义（含 tonemap/目标转换/D1 归一之后的结果）。
        /// 用途：PNG 的 cICP 后置补写（本构建的编码器已自写，此处退化为兜底）、UI 宣称文案、
        /// 色彩引擎的目标描述符（<c>ColorIntentFactory</c>）——三者必须与命令行同一份答案。
        /// 不确定时返回 (null,null,false) —— 宁可不写，也不能写出与像素失配的标签。
        /// </summary>
        public static (string? primaries, string? trc, bool sourceHasCicp) EffectiveOutputColorTokens(
            FfmpegOptions options, string inputPath)
        {
            try
            {
                var d = DecideOutputColor(options, inputPath);
                return (d.OutPrimaries, d.OutTrc, d.SourceHasCicp);
            }
            catch { return (null, null, false); }
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  #44：外部工具旁路（cjxl 直接编码）的「交付语义 vs 用户目标」对账判据
        //  ⚠ 本段加在文件**尾部**：`_lib-matrix.ps1` 的取证锚点按行号钉 `:144/:147/:155`，
        //    在前面插任何一行都会让 4 条门禁无因转红。
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 裁决点要求的**出口语义**是否与「cjxl 直编路线将要交付的语义」不一致。
        ///
        /// 为什么判据必须是**语义差**而不是链的数量（#44 的实测教训）：
        ///  • 直编路线**永不**改像素 —— cjxl 对自带色彩元数据的输入忽略 `-x`（`tests/output/t44/cjxl_x_probe2.log`），
        ///    而 `-x` 本来也**只写声明、不转像素**。所以它交付的就是**输入侧声明的那份语义**
        ///    = <see cref="OutputColorDecision.DeclPrimaries"/> / <see cref="OutputColorDecision.DeclTrc"/>。
        ///  • 用户显式指定目标时，出口语义 <see cref="OutputColorDecision.OutPrimaries"/>/
        ///    <see cref="OutputColorDecision.OutTrc"/> 已经被换成目标令牌 ⇒ 两者不等就是「目标兑现不了」。
        ///  • 用链数量做判据在这一族**恒不触发**：#43 之后 avif 靠 tonemap 链兑现目标，而 jxl 曾被
        ///    <c>isRgbNativeFmt</c> 复用出的豁免挡掉链（现由 <c>jxlNeedsTonemap</c> 补上，见上面
        ///    「HDR→SDR 色彩降级」那段），**判据不该依赖那条链存不存在** —— 否则同类豁免再次漏网时
        ///    闸门又静默失效（正是这一格的历史症状）。
        ///
        /// ⚠ **两侧都必须命名才断不一致**（fail-closed 的方向是"不改道"而非"改道"）：任一侧为空/未标签时
        ///   无法断言两份语义等价，此时维持既有直编路线 —— 与 §#44「不可命名就不猜」以及
        ///   <c>ColorSpaceRegistry.MapPrimariesTransferToCjxl</c> 返回 null 时交由 ICC/上游处理的口径一致。
        ///   实测这条保住了 JPEG 输入的 **auto 格**（无标签 jpeg 两侧都取不到令牌 ⇒ 不改道，
        ///   `-d 0 --lossless_jpeg=1` 的 DCT 无损重封装性能路照旧；见 `tests/output/t44/jpgchk/ls_auto`
        ///   的 `链数=0` + 直接编码行）。JPEG + **显式目标**仍会改道，但那是既有 `链数>0` 那半判据的结论
        ///   （zscale 链本来就在），本判据不额外扩大改道面。
        /// </summary>
        public static bool DirectEncodeTargetDiffers(OutputColorDecision? d)
        {
            if (d == null) return false;
            var dp = (d.DeclPrimaries ?? "").Trim().ToLowerInvariant();
            var dt = (d.DeclTrc ?? "").Trim().ToLowerInvariant();
            var op = (d.OutPrimaries ?? "").Trim().ToLowerInvariant();
            var ot = (d.OutTrc ?? "").Trim().ToLowerInvariant();
            if (dp.Length == 0 || dt.Length == 0 || op.Length == 0 || ot.Length == 0) return false;
            return dp != op || dt != ot;
        }
    }
}
