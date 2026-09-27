using System;
using FfmpegGui.Models;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// FfmpegOptions → <see cref="ColorIntent"/> 的**唯一装配点**（P7 接线 A1）。
///
/// 存在的原因：CLI/UI/预设的色彩选项此前"只解析、无消费"（8 个开关全如此），
/// 本函数把它们集中翻译成引擎输入，避免第二套解释逻辑（本项目反复修过"三处不一致"）。
///
/// 设计约束：
///  • 任何不确定 ⇒ 返回 null（调用方走 legacy），**绝不半吊子地用引擎跑一半**；
///  • 不做像素/容器判断之外的决策——策略、损失归类、标注取舍仍由 <see cref="ColorMappingEngine.Plan(ColorIntent)"/> 决定。
/// </summary>
public static class ColorIntentFactory
{
    /// <summary>把选项装配为意图；不可装配时返回 null 并在 <paramref name="reason"/> 给出原因。</summary>
    public static ColorIntent? FromOptions(FfmpegOptions o, string inputPath, out string? reason)
    {
        reason = null;
        if (o == null) { reason = "options 为空"; return null; }

        var fmt = (o.Format ?? "").Trim().ToLowerInvariant();
        var caps = ColorMappingEngine.CapsFor(fmt);
        // ── 无色彩通路容器（`HasColorPath=false`）的放行范围（2026-09-17 更新）──────────────
        // `HasColorPath = CanCicp || CanCarryArbitraryIcc || CanGenerateIccFromCicp`。为 false 意味着
        // **产物里放不下任何色彩声明**（GIF 是调色板、PPM/BMP 是裸像素…）。此前一律 return null ⇒
        // 这类格式的**像素**永远拿不到色彩管理（P3/HDR 源转 GIF 会把颜色搞错）。
        //
        // **GIF 例外放行**（HANDOVER 待实现第 2 项的下一格式）：放行后规划层有**唯一诚实**的结局 ——
        // 见 `ColorStrategyPlanning` 的 S1 分支「容器根本没有任何色彩管理通路 ⇒ 映射到 sRGB 工作空间
        // 并无标注输出」：`Action=Map`、`UnlabeledAssumedSrgb=true`、`Out*` 全空（**不写假标注**）。
        // 也就是说放行不会产出"标了 bt709 其实不是"的产物，只是把像素映射到业界惯例的 sRGB 工作空间。
        // 若源描述置信度低（无 ICC/无标签 ⇒ CodecDefault），更早的置信度闸门会把它压成 `Action=None`
        // 直通 ⇒ 连像素都不动（引擎只在日志里说明，重编码仍由 ffmpeg 完成）。
        //
        // ⚠ **只放 gif / jxr**：DNG 需要传感器 Bayer 数据（结构上不该接入），PPM/BMP/HEIC 无任何接入计划。
        //   连带放行会让它们以"引擎说能跑"的姿态落到没有出口的路径上（或被硬失败），比拒绝更糟。
        //   ⚠ 2026-09-17（JXR 轮）把 **jxr** 加进例外：它此前正是"需要新增执行出口"的那一类，
        //     现在出口已存在（`RawColorPipeline.EncodeJxrViaJxrEncAppAsync`）⇒ 与 gif **同款**结局：
        //     容器无色彩通路 ⇒ 规划层把目标钉成 sRGB + 记 `UnlabeledAssumedSrgb`（唯一诚实的映射终点）。
        //     ⚠ 这是**引擎能否接手**的门，不是能力表 —— jxr 的能力位本轮**未改**（见下）。
        if (!caps.HasColorPath && fmt != "gif" && fmt != "jxr")
        { reason = $"{fmt} 无色彩通路（既不能写 CICP 也不能带 ICC）"; return null; }

        // ── 源描述符：外部 ICC（用户显式指定源）→ 源内嵌 ICC（度量识别）→ 容器 CICP 标签 → 业界惯例假定 ──
        // 容器探测只做一次（FfmpegCommandBuilder 内部有 10s 缓存），既供标签也供 HDR 峰值。
        var meta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath);
        ColorSpaceDescriptor? src = null;
        // ── 外部 ICC（`--icc-file`）：语义 = **用户显式指定源色彩空间**，优先级最高（2026-09-16 接入引擎）──
        // 依据：① 该选项的 help 与 legacy 口径都写明「用外部 ICC 文件判定**源**色彩空间」；
        //       ② 无 ICC / 无标签的源在引擎里只能落到「惯例假定 sRGB」（置信度 CodecDefault），
        //          而 CodecDefault 被规划层的置信度闸门挡住不许改像素 ⇒ 用户即便知道源是 P3 也无从表达。
        //          外部 ICC 的置信度是 IccMetric（最高）⇒ 映射被放行。
        // ⚠ **不把外部 ICC 当作 SourceIccPath 往下传**：SourceIccPath 会触发「携带源 ICC」类计划
        //   （CarryIcc / SetLabelsFrom 的 preferCarry），而队列侧目前只对**源文件内嵌**的 ICC 做附着，
        //   外部 ICC 文件不会被嵌入 ⇒ 会留下「计划说携带了 ICC、产物里其实没有」的静默失配。
        //   因此这里只取其**度量结果**（色度矩阵 + 曲线 + 命名空间）作为源描述符，ICC 的落盘归属另说。
        if (!string.IsNullOrWhiteSpace(o.IccFilePath))
        {
            try
            {
                if (IccProfileService.IsValidIccProfile(o.IccFilePath))
                {
                    src = ColorSpaceIdentify.FromIccFile(o.IccFilePath,
                        IccProfileService.ParseInfo(o.IccFilePath)?.Description);
                    if (src != null)
                    {
                        src.SourceIccPath = null;
                        src.Name = $"{src.Name} [--icc-file]";
                    }
                }
            }
            catch { /* 外部 ICC 不可读/不可解析：退回源内嵌 ICC 与标签路径（不静默改用近似空间） */ }
        }
        if (src == null)
        {
            try
            {
                var (iccPath, iccDesc) = IccProfileService.ExtractIccToTempFile(inputPath);
                src = ColorSpaceIdentify.FromIccFile(iccPath, iccDesc);
                IccProfileService.TryDeleteIcc(iccPath);
            }
            catch { /* 无 ICC 或解析失败：继续走标签路径 */ }
        }

        bool srcFromTag = false;
        if (src == null)
        {
            src = ColorSpaceDescriptor.FromCicp(meta.colorPrimaries, meta.colorTrc,
                meta.sourceGamutName ?? "源（CICP 标签）");
            srcFromTag = src != null;
        }
        if (src == null)
        {
            src = ColorSpaceIdentify.AssumeByConvention(RawService.IsRawFile(inputPath));
            if (src == null) { reason = "源色彩语义无法确定（无 ICC、无标签、惯例假定失败）"; return null; }
        }
        // HDR 曲线必须明确“1.0 = 多少 nits”：FromCicp 默认按 SDR 参考白（203），会让
        // PQ/HLG 源的 headroom 算成 1 ⇒ 色调映射被判定“未激活”（实测：wire 断言曾因此从
        // 另一条分支蹭到通过）。这里按曲线修正，不依赖调用方记得传参。
        FixHdrNits(src);

        // ── GainMap 请求位（2026-09-16 补）────────────────────────────────────────
        // ⚠ 此前**从未装配**（全仓只有 `ColorIntent` 的字段声明与 `ToPolicy` 的传递）⇒
        // `ResolveStrategy` 的 S2 覆盖①（GainMap 必须映射底图，故 S2 不适用）是**死代码**：
        // 声明 carry 的 GainMap 任务在计划层不会被覆盖成 Recommended/Manual。
        // 传统管线侧由 `FfmpegCommandBuilder.EffectiveColorStrategy` 自行补过这一位；
        // 这里补上后，**两条管线与契约探针拿到的是同一份意图**（单一真值）。

        // ── 目标描述符：与 BuildArguments **同源**（EffectiveOutputColorTokens），避免两套映射 ──
        var (tp, tt, _) = FfmpegCommandBuilder.EffectiveOutputColorTokens(o, inputPath);
        // ⚠⚠ 2026-09-20：**RAW 预处理注入的默认值不算「用户显式指定目标」**（`ColorFromRawDefault`）。
        //   不排除它 ⇒ `it.Target` 非 null ⇒ 规划层「无色彩通路容器（GIF/JXR）⇒ 把目标钉成 sRGB」**不生效**
        //   ⇒ 实测 **RAW → GIF 被契约拒绝**（而普通 PNG → GIF 正常）。
        //   ⚠ 只影响**本判据**（意图装配）；**输出标注**走 `EffectiveOutputColorTokens`（另一条线），
        //     不受影响 ⇒ 「RAW 默认色域按目标 HDR 能力取值」那条功能保留。
        bool targetExplicit = !o.ColorFromRawDefault
            && (!o.UseAdvancedColorParameters
                ? !string.IsNullOrWhiteSpace(o.ColorSpace)
                  && !o.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrWhiteSpace(o.ColorTrc));
        ColorSpaceDescriptor? dst = null;
        if (targetExplicit)
        {
            dst = ColorSpaceDescriptor.FromCicp(tp, tt, "目标");
            if (dst == null) { reason = $"目标语义 {tp}/{tt} 无法命名"; return null; }
            FixHdrNits(dst);
        }

        var it = new ColorIntent
        {
            Strategy = o.ColorStrategy,
            Source = src,
            Target = dst,
            TargetExplicit = targetExplicit,
            Format = caps,
            // ── 位深（2026-09-16 与 legacy 口径对齐）──
            // 用户显式指定优先；**未指定时跟随源位深**（>8 才提升，否则按 8），再按容器上限钳制。
            // 旧写法 `o.BitDepth ?? 8` 会让 16-bit 源在 PNG/TIFF 上被**静默降位**到 8-bit
            //（A/B 实测：legacy `rgb48be` 1184448B vs 引擎 `rgb24` 592505B，PSNR 51.5dB 即 8-bit 量化噪声）。
            // legacy 的对应口径见 `FfmpegCommandBuilder.EffectiveBitDepth()`：
            //   Math.Min(detected > 8 ? detected : 8, capMaxBitDepth)
            TargetBitDepth = Math.Min(o.BitDepth ?? (meta.bitDepth > 8 ? meta.bitDepth : 8), caps.MaxBitDepth),
            // ⚠ 与上面一行**不是同一个数**：上面是"实际交付档"（已被 Decision 的 capBd 与此处的
            //   caps.MaxBitDepth 各钳一次），这里是**用户请求档**（钳制前）。两者的差 ⇒ ①b 播报。
            //   `?? o.BitDepth` 兜的是"意图装配早于钳制"那条顺序 —— 那时 o.BitDepth 本身就还是请求档。
            //   null（用户未指定 ⇒ 跟随源位深）**刻意不当请求档**：那不是"请求 ≠ 交付"。
            RequestedBitDepth = o.BitDepthRequested ?? o.BitDepth,
            SourceIsRaw = RawService.IsRawFile(inputPath),
            AllowUnlabeled = o.ColorAllowUnlabeled,
            // ── 色域压缩（GMO）开关（2026-09-17 接入引擎）────────────────────────────────
            // ⚠ 此前 `AllowGamutCompression` **从未被装配**（全仓只有字段声明 + ToPolicy 透传）
            //   ⇒ 恒 false ⇒ 规划层永远只走"硬裁切"，色域映射是一段不可达的死代码。
            // 口径 = CLI/GUI 的 `--color-gamut-map` 取值（off 默认 | on）；只认这两个字面量
            // （非法值在 CLI 解析处就报错，见 CliParser.ApplyExtraOption 的 color-gamut-map 分支）。
            // ⚠ 判据**单一来源** = `ColorEngineRouter.GamutMapRequested`：同一字面量比对此前在
            //   路由层与装配层各写一份，任何一处口径漂移都会变成「开关在规划层生效、在路由层不生效」
            //   这类两处不一致缺陷（本项目反复修过）。
            AllowGamutCompression = ColorEngineRouter.GamutMapRequested(o),
            TargetLossless = o.Lossless,
            GainMapRequested = o.JpegGainMap,      // 见上：补上后 S2 覆盖①（GainMap）不再死代码
            ToneMapRequested = false,
            ToneMap = ToneMapMode.None,
            // ── 外部编码器出口位（2026-09-17 接入 cjxl 出口）───────────────────────────
            // 判据 = 「目标格式 jxl」**且**「后端 Cjxl」。为什么必须是**这两个一起**：
            //   · `jxl` + `Ffmpeg` ⇒ ffmpeg 内置 libjxl **吃 rawvideo** ⇒ 走普通编码出口（已在白名单内），
            //     绝不能也塞进外部管道（多一个进程、还丢掉 ffmpeg 侧参数）；
            //   · `jxl` + `Cjxl`   ⇒ cjxl **不吃 rawvideo**（要文件或 PPM/PAM），色彩语义靠
            //     `-x color_space=`/`-x icc_pathname=`/`--intensity_target=` —— ffmpeg 编码参数**表达不了**
            //     ⇒ 必须由本出口接管。
            // 本机装了 cjxl 时 Cjxl 正是 `--format jxl` 的**默认后端**（`EncoderDetectionService.cs:55`）
            // ⇒ 这不是理论分支，而是最常见的那条。
            // ⚠ 判据放在装配层（这里）而不是执行层：出口选择是一次**决策**，执行层被约定为「只照计划做」
            //   （见 `ColorTransformPlan.ExternalEncoderExit` 的注释）。
            //   ⚠ 2026-09-17（JXR 轮）追加 `jxr`：`JxrEncApp` 是**纯文件式** `-i/-o`（不吃 rawvideo/stdin），
            //     且 ffmpeg 的 `libjxr` 无质量 AVOption、字节序与 jxrlib 不一致 ⇒ 不能当替代执行体
            //     （唯一口径见 `RawColorPipeline` 的 JXR 出口文档）⇒ 不存在「普通编码出口」
            //     这条替代路径 ⇒ jxr **恒**走本出口（不必像 jxl 那样再判后端）。
            ExternalEncoderExit = (fmt == "jxl" && o.EncoderBackend == EncoderBackend.Cjxl) || fmt == "jxr",
        };
        // ── 超广色域保真（2026-09-16 接入引擎）──────────────────────────────────────
        // 语义 = **像素零改动 + 仅靠 ICC 描述**（ProPhoto/ROMM 等超出 BT.2020 的色域：
        // 归一必削色，且 zscale 无法命名其 primaries 与 1.8 传递）。
        // 判定复用**传统管线的同一个函数** `ResolveFaithfulUltraWideIcc` ⇒ 两条管线口径一致、可 A/B 对拍。
        // ⚠ 此前规划层没有这个概念 ⇒ 引擎会把 ProPhoto 当普通任务**归一化**：
        //   实测像素被改、源 ICC 丢失、还写上误导性 CICP `bt709,bt709`（prophoto-faithful 26/0 → 13/13）。
        it.FaithfulIccPath = FfmpegCommandBuilder.ResolveFaithfulUltraWideIcc(
            o.ColorStrategy, o.ColorSpace, meta.sourceGamutName, meta.colorTrc, fmt);
        if (Enum.TryParse<CarryScope>(o.ColorCarryScope, true, out var cs)) it.CarryScope = cs;
        if (string.Equals(o.Color709Curve, "zimg", StringComparison.OrdinalIgnoreCase))
            it.Transfer709 = Transfer709Curve.Zimg;

        // ── 色调映射：字符串 → 算子；未知/无出处者不装配（交回 legacy） ──
        var tm = (o.ColorToneMap ?? "none").Trim().ToLowerInvariant();
        bool srcHdr = src.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;
        bool dstSdr = dst == null || (dst.Curve.Kind != TransferCurve.CurveKind.Pq
                                      && dst.Curve.Kind != TransferCurve.CurveKind.Hlg);
        // ── **HdrOutput 此前从未被装配**（只有字段声明与 ToPolicy 传递）——2026-09-16 修复 ──
        // 后果：规划层的 HDR 守卫 `srcHdr && !pol.HdrOutput && !ToneMapRequested ⇒ Reject`
        // 对**显式 HDR 目标**也成立 ⇒「P3-PQ 源 + `--color-space "BT.2020 PQ"` → png」被误拒，
        // 报「HDR 源 → SDR 目标需显式请求色调映射」，而目标其实是 HDR。
        //（`ColorStrategyPlanning` 只在 `it.Target == null` 的 auto 分支里补 HdrOutput ⇒ 显式目标走不到。）
        // 实测：`verify-widegamut-regression` 的 `p3pq-png-pq` 是唯一红格。
        if (!dstSdr) it.HdrOutput = true;
        switch (tm)
        {
            case "" or "none": break;
            case "reinhard": it.ToneMap = ToneMapMode.ReinhardSegmented; break;
            case "hable": it.ToneMap = ToneMapMode.Hable; break;
            case "mobius": it.ToneMap = ToneMapMode.Mobius; break;
            case "bt2446a": it.ToneMap = ToneMapMode.Bt2446A; break;
            case "bt2390": reason = "bt2390 无可靠出处（现行 BT.2390-12 正文不含 EETF），不走引擎"; return null;
            default: reason = $"未知 tone-map 值 '{tm}'"; return null;
        }
        // 只有"源是 HDR 且目标是 SDR"才算真的请求了映射；否则视为未请求（避免无意义压暗）。
        if (it.ToneMap != ToneMapMode.None)
        {
            // ── ⚠ 2026-09-18：GainMap 出口**跳过**这道装配门（修一处误伤）────────────────────
            // 实测（改动前）：`--color-tone-map hable` + **SDR 源** + GainMap ⇒ 引擎 exit 90 不产出
            // （"tone-map=hable 需要 HDR 源（当前 sRGB）"），而 legacy 产出 —— 是**误伤**，因为：
            //   ① 拒绝发生在**意图装配层**，连 GainMap 出口都没进；
            //   ② `RawColorPipeline`（GainMap 出口）明写「计划层的 tonemap 参数**不适用于** GainMap」：
            //      底图压暗由 `GainMapEncoder` 内部的**分段 Reinhard** 自持（峰值取自源曲线/元数据/用户），
            //      ⇒ 这个选项在 GainMap 出口**根本不会被消费**，产出不会因此出错。
            // 为什么**不**照 legacy 抄（legacy 是"静默忽略一个自相矛盾的选项"）：静默忽略同样不可接受。
            // 本实现 = 「产出 + 诚实告知」：这里放行（不 return null），由
            // `ColorStrategyPlanning.CollectDegradations` 登记 `DegradationCodes.ToneMapNotAppliedToGainMap`
            // ⇒ 裁决为 `ProceedWithDegradation`，用户从日志能读到"该选项未生效"。
            // ⚠ 判据**只加在 GainMap 上**：非 GainMap 格的这道门（HDR 才需要 tonemap）**一个字都不放宽**
            //   —— 放宽它会让"HDR→SDR 需要显式请求 tonemap"的检查失效（真正的静默压暗会溜过去）。
            if (!o.JpegGainMap)
            {
                if (!srcHdr) { reason = $"tone-map={tm} 需要 HDR 源（当前 {src.Curve.Name}）"; return null; }
                if (!dstSdr) { reason = $"tone-map={tm} 与 HDR 目标矛盾"; return null; }
            }
            it.ToneMapRequested = true;
        }
        else if (srcHdr && dstSdr && (targetExplicit || !caps.CanHdr))
        {
            // ── HDR 源 + SDR 结局 ⇒ 自动使用内部默认曲线 hable（2026-09-16）──
            // 为什么必须自动：GUI 已按设计**不再暴露** tonemap 曲线（"内部固定 hable"，见 MainWindow 各处的
            // `TonemapCurve = null` 注释），既有行为由传统链的内部默认承担。默认切引擎后若仍按规划层
            // "需显式请求"拒绝，**GUI 用户转 HDR→JPEG 将无路可走**（实测 verify-format-regression 16 项全红）。
            // 这不是"静默降级"：日志会写明是**自动**采用的曲线与来源（ToneMapAuto ⇒ 决策 Reason 里标注），
            // 用户显式给 --color-tone-map 时仍以其为准。
            // ⚠ 触发条件必须**两支**（2026-09-16 第二次修正）：
            //   ① `targetExplicit`：用户**显式**指定了 SDR 目标 ⇒ 他要的就是 SDR，必须映射；
            //   ② `!caps.CanHdr`：目标是 auto 但容器**承载不了** HDR（如 JPEG）⇒ 只能降 SDR。
            //   漏掉①会回归：HDR 源 + 显式 sRGB 目标 + png（`CanHdr=true`）会落到规划层的
            //   "HDR 源 → SDR 目标需显式请求 tonemap" ⇒ **拒绝**（实测 hdr8-png-tgt 转红）。
            //   漏掉②会让 GUI 的 HDR→JPEG 无路可走。
            //   反之（auto + 容器可承载 HDR，如 PNG/JXL/AVIF）⇒ **保留 HDR、不自动映射**。
            // ⚠⚠ **2026-09-20 实测：② 这里不能用 `CanHdrWith(JpegGainMap)`**（曾这么改过，**已回滚**）。
            //   那样改的后果：把 JPEG+GainMap 判成"能承载 HDR" ⇒ 不自动 tonemap ⇒ 规划层置 `HdrOutput`
            //   ⇒ 要求 JPEG 标注 bt2020/smpte2084，而 **JPEG 既无 CICP 通路、ICC 也表达不了 PQ**
            //   ⇒ 引擎 `Reject`（「不产出无标注结果」）⇒ 三格 `*-hdr-jpggm` 由 **63/0 掉到 58/5**。
            //   根因：**本判据问的是「容器能否承载 HDR *标注*」**，而 GainMap 的 HDR 走的是
            //   「**底图 tonemap 成 SDR + 增益层**承载 HDR」这条**完全不同的机制**
            //   ⇒ 自动 tonemap 恰恰是 GainMap 出口**需要**的（`CollectDegradations` 只登记
            //     `ToneMapNotAppliedToGainMap` 这条降级，不改变产出）。
            //   ⇒ **保持 `caps.CanHdr` 不变**。「JPEG+GainMap 算能表达 HDR」只在
            //     **RAW 源色域默认值**那个场景使用（`CanHdrWith`，见 `QueueProcessor` 的 RAW 预处理）。
            it.ToneMap = ToneMapMode.Hable;
            it.ToneMapRequested = true;
            it.ToneMapAuto = true;
        }
        // 峰值优先级：用户显式 > 容器 HDR 静态元数据（MaxCLL 优先、其次 BS.2086 mastering，
        // 见 FfmpegCommandBuilder.ProbePeakNits）> 无（→ 策略层回退源曲线名义峰值）。
        // 两者都无时保持 0 ⇒ PQ 名义 10000 使 headroom=49、画面明显偏暗，因此来源必须一路带到日志。
        if (o.ColorHdrPeakNits > 0)
        {
            it.HdrPeakNits = o.ColorHdrPeakNits;
            it.HdrPeakSource = "用户 --color-hdr-peak";
        }
        else if (meta.peakNits > 0)
        {
            it.HdrPeakNits = meta.peakNits;
            it.HdrPeakSource = meta.peakSource ?? "容器 HDR 静态元数据";
        }
        if (o.ColorSdrWhiteNits > 0) it.SdrWhiteNits = o.ColorSdrWhiteNits;
        if (o.ColorSdrPeakNits > 0) it.SdrPeakNits = o.ColorSdrPeakNits;
        _ = srcFromTag;   // 供后续置信度策略参考（当前由描述符自身 Confidence 承担）
        return it;
    }

    /// <summary>把 HDR 曲线的“1.0 = nits”修正为名义峰值（PQ=10000、HLG=1000，BT.2100）。</summary>
    private static void FixHdrNits(ColorSpaceDescriptor? d)
    {
        if (d == null || d.NitsOfLinearOne > TransferCurve.SdrWhiteNits) return;
        if (d.Curve.Kind == TransferCurve.CurveKind.Pq) d.NitsOfLinearOne = TransferCurve.PqPeakNits;
        else if (d.Curve.Kind == TransferCurve.CurveKind.Hlg) d.NitsOfLinearOne = Bt2100Ootf.NominalPeakNits;
    }
}
