# 线 A：色彩引擎「决策层」正确性审查（2026-10-04）

> **审查范围（只审决策正确性）**：`ColorIntentFactory.cs` · `ColorStrategyPlanning.cs` · `ColorTransformPlan.cs` · `ColorIntent.cs` · `ColorVerdict.cs` · `EngineBlock.cs` · `ColorScheduler.cs` · `ColorEngineRouter.cs`
> **方法**：以代码为准（本仓注释多次与实现不符，逐条以 grep/读码复核）；对每条发现给 `file:line` + 可直接读到的代码 + 具体失败场景。
> **取证手段**：`ServiceProbe.exe contract`（137/0）· `verdict`（41/0）· `plan`（39/0）；全仓 grep 计数；未修改任何 `src/`、`tests/` 文件，未 `git add/commit`。
> **结论**：**P0 = 0，P1 = 0**（本轮未发现会导致错误交付的确定缺陷）；P2 = 2，P3 = 9。另有 1 条「若可达即 P1」的候选（F1）因无法构造输入，按 P2 登记。

---

## ① 发现表（按级别排序）

| 级别 | 位置(file:line) | 问题 | 为什么错（失败场景） | 证据/复现 | 是否已有门禁 |
|---|---|---|---|---|---|
| **P2** | `ColorIntentFactory.cs:116` | 源描述符用 `ColorSpaceDescriptor.FromCicp(...)`（**总是** `CicpTag`），而非识别层专为该场景写的 `ColorSpaceIdentify.FromCicpTokens(...)`（缺项按惯例补全并降为 `CodecDefault`）。后者**全仓零调用**（死代码）。 | 源容器**只带了一半 CICP 标签**时整体丢弃该标签。例：某文件 `color_transfer=smpte2084`、`color_primaries=unknown`（ffprobe 两字段独立解析，见 `FfmpegCommandBuilder.cs:1670-1674`，各自过滤 unknown→null）⇒ `FromCicp(null,"smpte2084")` 因 `GetPrimaries(null)=null` 返回 null ⇒ 落到 `AssumeByConvention`（`ColorIntentFactory.cs:122`）得到 **sRGB/BT.2020-SDR、CodecDefault** ⇒ `srcHdr=false` ⇒ 自动 HDR→SDR tonemap 不触发、HDR 守卫不触发 ⇒ **PQ 像素被当 SDR 处理/无标注产出**（查看器偏暗）。用 `FromCicpTokens` 会得到「bt709(假定)/smpte2084、CodecDefault」，至少保住 transfer。 | 读码：`ColorIntentFactory.cs:114-124`；`ColorSpaceIdentify.cs:144-159`（`FromCicpTokens` 声明，doc 自称「无 ICC 时的**主路径**」）；grep `FromCicpTokens` ⇒ 仅声明处 1 命中。 | **无**。`contract`/`verdict` 网格的源恒为「双标签齐全」或「完全无标签」，从不构造半标签源。 |
| **P2** | `ColorStrategyPlanning.cs:194` | 出口档白名单 `pol.Format.Format is "png" or "apng" or "tiff" or "jxl"` **漏了别名 `tif`**（能力表 `ColorTransformPlan.cs:551` 有 `tif` 条目；`ImageEncoderArgs.cs:927` 的引擎白名单也含 `tif`）。 | 同一逻辑任务按别名给出**不同的「请求≠交付」播报**。例：`--bit-depth 10 -f tif` + 带 ICC 的 sRGB 源 ⇒ S1 走 L1 携带（`Action=CarryIcc`），`TargetBitDepth=10`；因 `"tif"` 不在名单，`outlet=pol.TargetBitDepth=10` ⇒ `outlet==TargetBitDepth` ⇒ **不播报**；而 `-f tiff` 同输入 `outlet=16` ⇒ 播报 `BitDepthRaised`。实际交付档都是 rgb48le(16) ⇒ **`tif` 静默交付 16bit 却对外说 10bit**（违反「请求≠交付必须出声」）。 | 读码：`ColorStrategyPlanning.cs:193-196`（`outlet` 取法）对比 `ColorTransformPlan.cs:551`（`["tif"]` 能力位）与 `ImageEncoderArgs.cs:927`（`tif` 可编码）。`verify-color-strategy.ps1:488-503` 的 F1/F3 只测 `jxl`/`webp`。 | **无**。`verdict` 网格 `TargetBitDepth=8` 且格式集 `{png,jpg,tiff,webp,avif,jxl,gif,heic}` **不含 `tif`**；`verify-color-strategy` 的 ③f 亦不含 `tif`。 |
| **P3** | `ColorStrategyPlanning.cs:414` + `:510`/`:520` | S2 覆盖归因 `OverrideReason.ContainerCannotCarryIcc` 与实际结局不符。`ResolveStrategy` 对**任何** `!CanCarryArbitraryIcc` 的 S2 任务都返回该原因（doc/注释称「结局为 Reject」），但 `PlanCarry` 在源无 ICC 时可走「隐含 sRGB 无标注直通」（`Action=None`）或「继承 CICP」——**不是 Reject**。 | 例：`--color-strategy carry-icc -f gif` + 仅 CICP 标签的 sRGB 源 ⇒ `Action=None`、`UnlabeledAssumedSrgb=true`、`Override=ContainerCannotCarryIcc`。归因说「容器带不了 ICC ⇒ S2 兑现不了」，实际 S2 已合法兑现（零改动无标注）。且 `contract` 的 `ex = p.Override != None` 会**豁免**该格，使「S2 的 None 必须是两种零改动形态之一」这条断言在该格失效。 | 读码：`ColorStrategyPlanning.cs:414`（返回原因）· `:514-524`（结局）· `ColorIntent.cs:65-66`（枚举 doc「S2 无法兑现」）· `:411-415`（注释「结局为 Reject」）。 | 部分（`contract` 断言存在，但被 Override 豁免）。 |
| **P3** | `FfmpegCommandBuilder.Decision.cs:528-532` | `EffectiveColorStrategy` 的成本闸判据 `if (declared != CarryIcc) return declared;` 及其理由「`ResolveStrategy` 的第一行就是 `declared != CarryIcc ⇒ 原样返回`」**为假**：`ResolveStrategy` 的**第一行是 GainMap 覆盖**（`ColorStrategyPlanning.cs:403`），它对**任何**非 `Recommended` 策略（含 S3/S4）生效。 | 当 GainMap 任务落到传统管线（唯一残余触发：`BlockReason=AnimatedInput`，如 `anim.gif --format jpg --jpeg-gain-map true --color-strategy cicp-only`）时，`EffectiveColorStrategy` 返回**声明的 S3**，而同一任务 `ResolveLegacyVerdictPlan → Plan().EffectiveStrategy` 是 **Recommended**（`GainMapRequiresBaseMapping` 覆盖）⇒ 两条管线策略不一致（正是本仓反复修的「多处不一致」形状）。**注**：`ProcessGainMapJpegAsync` 自身不消费 `EffectiveColorStrategy`（实测 grep），故当前无可见产物差异 ⇒ 定性为潜在分叉。 | 读码：`Decision.cs:528-532` vs `ColorStrategyPlanning.cs:403-409`；`QueueProcessor.cs:2392-2517`（GainMap 专用管线未用策略）。 | **无**。`contract` 只断言规划层，不比对 `EffectiveColorStrategy` 与 `Plan().EffectiveStrategy`。 |
| **P3** | `ColorStrategyPlanning.cs:865` | `CarryCicpLabels` 读 `p.ExternalEncoderExit` 作为 `CapsAcceptsCicp` 的「外部出口」参数——而**本文件自己的注释**（`ColorStrategyPlanning.cs:1024-1027`）明确警告：「该位是 `Plan` 在 `PlanWithPolicy` 返回**之后**才装配的（:40），在本函数里**恒为 false**」。正确值应是 `pol.ExternalEncoderExit`（调用点 4 处：`:469/:497/:713/:725`）。 | 对**外部出口（cjxl）**，`CarryCicpLabels` 会按「自由 nclx」放行一条 cjxl 枚举写不出的 CICP。可达路径需「低置信度 + 非 sRGB 源」：`PlanRecommended` 闸门支（`:718`）对 `AssumeByConvention(true)`（RAW 假定 = `bt2020/bt709`）会写入 `OutPrimaries=bt2020`，且该支 `AttachIcc=false` ⇒ `EnforceContainerAnnotationRules` 的「择一」分支（要求 `icc && cicp`）**不触发** ⇒ 错误 CICP 不被纠正 ⇒ cjxl 出口「计划未提供 ICC ⇒ 拒绝」/0 字节。**当前可达性窄**（RAW 预处理块恒写 `ColorSourceDeclTrc=linear`，见 `QueueProcessor.cs:567-568` ⇒ 走不到该闸门支），故列 P3。 | 读码：`ColorStrategyPlanning.cs:863-869`（`p.ExternalEncoderExit`）对比 `:1021-1029`（Enforce 的同款警告）与 `:504`（同一语义处用的是 `pol.ExternalEncoderExit`）。 | **无**（无脚本构造「低置信度 BT.2020 源 + jxl+cjxl」格）。 |
| **P3** | `ColorStrategyPlanning.cs:766` | `if (pol.Strategy == ColorStrategy.BakeCicpOnly && !it.GainMapRequested) StripIcc(core);` 是**不可达分支**。 | 证明：`pol.Strategy == declared`（`ColorIntent.ToPolicy()` 直接赋值）；`PlanRecommended` 仅在 `eff==Recommended` 时被调用（`PlanWithPolicy:310-316` 的 `_` 支）；`declared==BakeCicpOnly` 时 `eff==Recommended` 的唯一可能是 GainMap 覆盖（`ResolveStrategy:403-406`）⇒ 此时 `it.GainMapRequested==true` ⇒ `!it.GainMapRequested` 恒 false。⇒ 该 `StripIcc` 永不执行。无功能影响，但读者会误以为「S3 经覆盖后仍剥 ICC」受保护。 | 读码 + 逻辑推导；`contract` 的 `S3+GainMap` 格走的是 `PlanRecommended`（eff=Recommended），从不进入本行。 | **无**。 |
| **P3** | `ColorTransformPlan.cs:330` | `public string? SuggestedSuperset;` **三处全无**：声明后再无赋值、无读取（`src/`、`tests/` 全仓 grep 仅命中声明行）。 | doc 宣称「非空表示源色域超出用户指定的窄目标，改投该超集 + 标准 ICC 可做到零削色」——这是**不存在的接口**。任何按此字段做决策/展示的调用方恒得 null。 | `grep -rn SuggestedSuperset src/ tests/` ⇒ 仅 `ColorTransformPlan.cs:330`。 | **无**。 |
| **P3** | `ColorTransformPlan.cs:132/149/157` + `ColorIntent.cs:239/245` | `PlanPolicy` 死字段簇：`PreferCarryIcc`（仅声明+doc 引用）、`PreferCarry`（`ColorIntent.ToPolicy():245` 赋值，**无读取**）、`SourceIsRaw`（`ToPolicy():239` 赋值，**无读取**）。连带 `ColorIntent.SourceIsRaw` 只被拷进这个死字段。 | 死接口制造虚假信心：读者以为 S1 携带倾向/RAW 源语义由这些位驱动，实际由 `CarryScope`（`ColorStrategyPlanning.cs:700`）与 `RawService.IsRawFile`（`ColorIntentFactory.cs:122`）各自决定。 | `grep -rn "\bPreferCarryIcc\b\|\bPreferCarry\b\|\bSourceIsRaw\b" src/`（见上）。 | **无**。 |
| **P3** | `ColorVerdict.cs:68/72/106/108/110/112/114` + `EngineBlock.cs:38-39` | 词表死项：`DegradationCodes` 中 `NoCicpCarrier`/`HdrNoIccCarryPath`/`GainMapNonJpeg`/`GeometryWithGainMap`/`NoColorPath`/`AnimatedNotSupported`/`EncoderUnsupportedSetting` **生产代码零产出**（15 个 Code 里 7 个死）。`EngineBlockCodes.GeometryWithGainMap` 亦已停用（`ColorEngineRouter.cs:352-379` 明写「不再被生产」），但 `EngineBlock.cs:38-39` 仍称「9 条判据 ⇒ 9 个 Code（不多不少）」，实为 8 条活判据。 | 契约词表 > 生产，门禁无法用「新 Code 未被断言」把新通道挡住（`verdict` 的 `filled` 只登记 7 个，其余被 `unexpected` 检查**强制**为「永不产生」）。 | `grep -c "DegradationCodes.<X>" src/` ⇒ 上述 7 项均为 0；`EngineBlock.cs:38-39` vs `ColorEngineRouter.cs:352-379`。 | 部分（`verdict` 的 `unexpected.Count==0` 事实上把它们锁死为「不许产生」，但没有任何断言说明它们为何保留）。 |
| **P3** | `ColorStrategyPlanning.cs:582-592` | S3（`BakeCicpOnly`）的**映射支路**缺置信度闸门：`PlanCore(src, target, pol)` 直接调用，未检查 `src.Confidence >= MinConfidenceForMapping`。对比：`PlanRecommended:718`、`PlanCarry:504/514`、`PlanCicpOnly` 自身的 `!CanCicp` 支 `:544` 都有闸门。 | 设计声明「低于 `CicpTag` 不许改像素映射」。**当前不可达**：唯一的 `CodecDefault` 源来自 `AssumeByConvention`（sRGB 或 BT.2020/bt709），而 `NameableSupersetFor` 对二者都会取到「等价或包含」的目标 ⇒ `src.Equivalent(target)` ⇒ 走 `:576` 的 `Action=None`，不进入映射支。⇒ 属结构性不一致（若 `AssumeByConvention`/`FromCicpTokens` 将来产出别的 `CodecDefault` 源，或缺置信度源经其它途径进入 S3，则静默改像素）。 | 读码：`:562-592`；`AssumeByConvention` 输出（`ColorSpaceIdentify.cs:162-169`）与 `NameableSupersetFor`（`:888-903`）逐一枚举验证等价。 | **无**。 |
| **P3** | `ColorIntent.cs:135-148` | `EquivalenceTolerances` 6 个常量中 **4 个零消费**：`MatrixToIdentity`、`MatrixElements`、`CurveParams`、`Chromaticity` 均无读取点（仅 `ContainmentEpsilon`、`MinConfidenceForMapping` 被用）。而类注释称「probe 有专测，禁止散落魔数」。 | 与 2026-10-02 可用性审计 §3 P2-2 同一项，**本轮复核仍未修**（等价判定仍在 `ColorSpaceDescriptor.cs:208`（1e-5）、`:182`（5e-3）、`ColorSpaceIdentify.cs:32`（2e-3）等处散落魔数）。 | `grep -rn "EquivalenceTolerances\.<X>"` ⇒ 上述 4 项 0 命中。 | **无**。 |

> **关于「已知项」的说明**：`it.Manual` 从未被 `ColorIntentFactory` 装配（全仓唯一 `ManualIntent.Create` 调用在 `tests/ServiceProbe/Program.cs:1558/1781/2429/2505`），导致 `ResolveStrategy:405` 的 S4-GainMap 分支、`PlanManual:608/612` 的手动字段支、`BuildManualTarget:851` 均为死代码——该项已在 `COLOR_MATRIX_USABILITY_AUDIT_2026-10-02.md` §3 P2-1 登记（**至今未修**），本报告不重复展开，仅确认「S4 在生产中只经 `it.Target` 生效」这一结论**仍然成立**。

---

## ② 已核实无问题（实际查过并确认正确的点）

1. **`PlanFaithful` 的 `AttachIcc` 不会落到「容器带不了 ICC」**：`ResolveFaithfulUltraWideIcc`（`FfmpegCommandBuilder.cs:1345-1363`）把格式白名单钉死为 `png/apng/jpg/jpeg/jpegli/tiff/tif/webp/jxl`，全部 `CanCarryArbitraryIcc=true`；且 `strategy==BakeCicpOnly` 直接返回 null。**查法**：读该函数 + 对照能力表。
2. **`EnforceContainerAnnotationRules` 的「择一」用的是正确出口标志**：调用点传 `pol.ExternalEncoderExit`（`ColorStrategyPlanning.cs:358`），不是 `p.ExternalEncoderExit`。**查法**：读 `:1029-1065` 及其调用点（该文件注释自证曾踩过这个坑）。
3. **`ClassifyAnnot` 与能力表一致**：`Annot==IccAndCicp` 仅在 `AttachIcc && OutPrimaries!=null`；`contract` 的 `V(p.Annot != AC.IccAndCicp || caps.CanHaveBothIccAndCicp)` 覆盖此点（137/0）。且 `ClassifyAnnot` 在 `EnforceContainerAnnotationRules` **之后**执行（`:358` → `:360`），顺序正确。
4. **7 个「活」降级码的登记与动作一致**：`BitDepthReduced/GamutClipped/GamutCompressed/UnlabeledAssumedSrgb/NoIccCarrier/ContainerCannotCarryIcc/ToneMapNotAppliedToGainMap` 均只在已产出格登记，且与 `Action`/`GamutMap`/`UnlabeledAssumedSrgb` 判据同源（`CollectDegradations` 全段）。**查法**：`verdict` 41/0（含 224 格自洽性 + 双向覆盖面）。
5. **`GamutCompressed` 与 `GamutClipped` 互斥**、`ColorLoss` 与 `GamutMap` 同源：`ClassifyColorLoss:1093-1099` 用 `p.GamutMap`（PlanCore 单点）而非 `pol.AllowGamutCompression`。`verdict` ②′ 有正/负控。
6. **`ResolveStrategy` 的 HDR 覆盖②（`HdrNoIccCarryPath`）结局正确**：`PlanCarry:460-481` 要么继承 CICP（`Action=None`）要么 Reject，不改像素；`contract` 的 `HLG × tiff/webp/gif ⇒ Reject` 与 `HLG × png/apng ⇒ 不 Reject` 覆盖。
7. **GainMap 覆盖保留 `DeclaredStrategy`**：`ResolveStrategy` 只返回 `eff`，`PlanWithPolicy:318-319` 原样写 `declared`/`eff`；`contract:1775-1776` 断言。
8. **`CanCarryHdrAt` 的位深前提正确**：`CanHdr && (!HdrRequiresHighBitDepth || bitDepth>8)`（`ColorStrategyPlanning.cs:967-968`），8bit PNG 不会被误判为「保持 HDR」。`contract` 有 `HLG × png/apng` 正向断言。
9. **`GainMap`/`ExternalEncoderExit` 的装配是单一赋值点**：均在 `Plan(ColorIntent)` 返回前统一置位（`:34`/`:40`），覆盖所有返回分支（含各 `Reject`）。读码确认无第二处赋值。
10. **`CapsAcceptsCicp` 的第三参数强制显式**：无默认值，所有调用点都表态；`Enforce` 用 `pol.ExternalEncoderExit`、`PlanCarry`/`PlanCicpOnly`/`NameableSupersetFor` 亦然（`CarryCicpLabels` 除外，见 F5/P3）。
11. **`EnsureAnnotationNotSilentlyMissing` 的兜底方向正确**：非 Reject 行若既无 ICC 又无 CICP，要么显式记 `UnlabeledAssumedSrgb`（且限定 `pixelsUntouched || !HasColorPath` 且 `IsSrgbEquivalent(effective)`），要么 Reject；不会静默产出无标注广色域行。
12. **`ColorScheduler`**：纯性能调度（Dop/TileRows/Streaming 只影响并行与分块），不参与色彩决策；`ForItem`/`TileRowsFor`/`RunBands` 边界（0/负/超限）处理正确。无决策正确性风险。

---

## ③ 未覆盖（有路径但无判据）

1. **`tif` 别名出口档播报**（F2）：`verify-color-strategy.ps1` ③f 只测 `jxl`/`webp`；`verdict` 网格不含 `tif`。⇒ 该别名差异无人钉。
2. **`BitDepthRaised`**：不在 `ProbeVerdict` 的 `filled` 白名单，且网格 `TargetBitDepth=8` 使其**永不被产生**（`unexpected` 检查强制）。仅 `verify-color-strategy` F1（jxl）与 `verify-color-matrix-full` 覆盖部分格式。
3. **半标签 CICP 源**（F1）：`contract`/`verdict` 的源集合（`sRGB仅标签/sRGB带ICC/P3带ICC/HDR-PQ/HDR-HLG/Adobe带ICC/无标签`）全部双标签齐全或全无，无「仅 primaries」或「仅 transfer」夹具。
4. **`EffectiveColorStrategy` ↔ `Plan().EffectiveStrategy` 等价性**（F4）：无任何脚本比对这两者；GainMap 覆盖对 S3/S4 的差异无从发现。
5. **`CarryCicpLabels` 在外部出口下的 CICP 可写性**（F5）：无「低置信度非 sRGB 源 + jxl + Cjxl」格。
6. **`PlanPolicy` 死字段与 `SuggestedSuperset`**：无门禁检测「声明了但零消费」的字段（本仓纪律要求「接线或删除」）。
7. **`apng` / `jpeg` / `jpegli` / `jxr` / `ppm` / `heif` 的策略×容器矩阵**：`contract`/`verdict` 的格式集为 `{png,jpg,tiff,webp,avif,jxl,gif,heic}`，其余 6 个格式的 S1..S4 结局无格断言（`jxr` 尤其特殊：`HasColorPath=false` 且 `ExternalEncoderExit` 恒 true）。
8. **`ResolveStrategy` 的 S3/S4 非 GainMap 覆盖**：`contract` 的 `s3n` 负控只针对 `GainMapRequiresBaseMapping`；对「S3/S4 是否可能被其它原因覆盖」无断言（当前实现无，但无绊线）。
9. **S3 映射支路的置信度**（F10）：无「`CodecDefault` 源 + S3 + 可命名目标」夹具。

---

## ④ 建议（按性价比）

1. **补半标签 CICP 夹具并改用 `FromCicpTokens`**（F1）——这是唯一有「错误交付」嫌疑的一条；顺带把死代码接活或删除。
2. **`tif` 加入出口档白名单**（F2），并在 `verify-color-strategy` ③f 加一格 `tif --bit-depth 10` 的负控。
3. **`CarryCicpLabels` 改传 `pol.ExternalEncoderExit`**（F5）——同文件已为 `Enforce` 修过同一个坑，属遗漏。
4. **处置死字段/死码簇**（`SuggestedSuperset`、`PreferCarry`、`PreferCarryIcc`、`SourceIsRaw`、7 个死 `DegradationCodes`、`GeometryWithGainMap` 计数文案）——「接线或删除」二选一。
5. **修正 `EffectiveColorStrategy` 成本闸的理由与判据**（F4）：至少把理由改成「非 GainMap 时恒等」，或直接对 GainMap 任务也走规划层。

---

*本报告全程只读，未修改任何 `src/`、`tests/` 文件，未执行构建，未 `git add/commit`。*
