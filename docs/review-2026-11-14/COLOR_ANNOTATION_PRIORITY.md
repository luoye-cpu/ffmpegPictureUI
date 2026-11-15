# 默认推荐路径的色彩标注优先级：CICP 优先 / ICC 兜底（2026-11-14）

## 1. 背景与目标

`ColorStrategy.Recommended`（默认策略）的标注逻辑，旧口径是「ICC 优先 / ICC+CICP 双描述」：
目标可 CICP 命名时，先写 CICP 三元组，再在 SDR 且出口能写 ICC 时**附一张自生成 ICC**
（`ColorTransformPlan.SetLabelsFrom` 旧注释自称「D2 双描述策略」）。

本次把它改写为 **CICP 优先、CICP 不可达才 ICC 兜底**，且每一次回退都可解释
（计划 `Reason` + `CicpUnavailable` 枚举 + 日志 + UI 文案），不允许静默丢色彩信息。
显式策略 `CarryIcc / BakeCicpOnly / Manual` 语义零漂移；旧预设 / 旧 CLI 兼容入口
（`ColorStrategyMapper.FromIccMode` / `TryParse`）未动。

唯一落点仍是 `ColorTransformPlan.SetLabelsFrom`（`ColorTransformPlan.cs:1094`）。

## 2. 优先级阶梯（命中即停）

按 `SetLabelsFrom` 实际顺序。**新增（2026-11-14 二轮）：ICC 只有在「CICP 无法正确承载」时才启用**——
目标空间能精确 CICP 命名且本出口真能写 CICP ⇒ 绝不携带/附源 ICC，直接落阶梯②只写 CICP。
顶部先算 `bool cicpCanCarry = IsPreciselyCicpNameable(dst) && CapsAcceptsCicp(caps,dst,ExternalEncoderExit)`。

1. **阶梯① L1 零改动携带**（`preferCarry && dst.SourceIccPath != null && CanCarryArbitraryIcc
   && ExitWritesIcc && !hdr && !cicpCanCarry`）：源 ICC 是权威——但**仅当 CICP 无法正确承载时**。
   先 `AttachIcc=true / IccPath=源 ICC`；仅当容器 `CanHaveBothIccAndCicp` 且目标
   `IsPreciselyCicpNameable` 且 `CapsAcceptsCicp(...)` 时，才把 CICP 作**附加**标签；
   否则只靠源 ICC，Reason 明写「源 ICC 保留为唯一权威描述」。
2. **阶梯② CICP 优先**（`cicpCanCarry`，= `IsPreciselyCicpNameable(dst) && CapsAcceptsCicp(caps,dst,ExternalEncoderExit)`）：
   只写 `OutPrimaries/OutTransfer/OutMatrix`，**不附自生成/源 ICC**。
   Reason 明写「CICP 优先（容器可承载 + 空间可精确命名）⇒ 只写 CICP，不附自生成 ICC」。
   若 `dst.SourceIccPath != null`（即本格正是因为 `!cicpCanCarry` 才跳过了阶梯①携带），**不静默丢源 ICC**：
   追加 Reason「源 ICC 未携带——目标空间可精确 CICP 命名且本出口可写 CICP，CICP 已足以正确承载」
   + Advice「如需原样保留源 ICC，请选『携带 ICC』策略（CarryIcc）」。
   HDR（PQ/HLG）同走此路，另记 `CicpUnavailable=HdrNoIccGenerator`（iccgen 本就不支持 HDR）。
3. **阶梯③ CICP 不可达 ⇒ ICC 兜底**：先判**具体**为什么不可达，再兜底：
   - `!dst.IsCicpNameable` ⇒ `SpaceNotNameable`（ProPhoto/Adobe/自定义矩阵）；
   - `!caps.CanCicp` ⇒ `caps.NoCicpReason`（TIFF=`ContainerNonNormative`，其余=`ContainerNoCicp`）；
   - `IsPreciselyCicpNameable=false`（ICC 派生往返不等价）⇒ `SpaceNotNameable`；
   - `!CapsAcceptsCicp`（容器有 nclx 通路但外部编码器枚举宽度写不出，如 cjxl `color_space=`）
     ⇒ **新增档 `OutletCannotExpress`**（`ColorIntent.cs:44-55`）。
   - webp 的 `unlabeledIsFine`（bt709+iec61966-2-1 && `UnlabeledMeansSrgb` && `!EncoderWritesGeneratedIcc`）
     逐字保留 ⇒ `UnlabeledAssumedSrgb`，不附冗余 ICC。
   - 否则 `CanCarryArbitraryIcc && ExitWritesIcc` ⇒ `AttachIcc=true`；有源 ICC 走源路径（先按实测色度/TRC 重写等价 v4.4.0，
     能命名 H.273 空间时还在 ICC 内写 `cicp` tag，作为容器 CICP 之外的第二份标注）；
     否则 `CanGenerateIccFromCicp && dst.IsCicpNameable` ⇒ 由 CICP 三元组生成（iccgen；标准 ICC 同样写 `cicp` tag）。
4. **两者皆不可达** ⇒ 沿用 `EnsureAnnotationNotSilentlyMissing`（`ColorStrategyPlanning.cs:984`）
   的 fail-closed：像素等价 sRGB 且未改动/无色彩通路才 `UnlabeledAssumedSrgb`，否则 Reject。
   已确认该门**无需改动**：CICP-only 由 `OutPrimaries != null` 放行。

### 「精确命名」判据

新增私有 helper `IsPreciselyCicpNameable`（`ColorTransformPlan.cs:1210`）：
`FromCicp` 构造的目标天然 true（矩阵取注册表理论值）；ICC 派生描述符才做一次
`FromCicp(primaries, trc)` 往返 `Equivalent` 复核。不另造容差常数——`ColorSpaceIdentify.RefineKnownSpace`
已把 ICC 矩阵吸附到理论值（容差 2e-3），往返等价是其同源判据。拿不到等价证据 ⇒ 判「不可精确命名」走 ICC 兜底。

### 枚举新增档

`CicpUnavailableReason` 新增 `OutletCannotExpress`（容器有 CICP 通路但本出口写不出），
与 `ContainerNoCicp`（容器无通路）严格区分——避免日志说假话（容器明明有 nclx）。
全仓无 `switch` 穷举该枚举；播报点唯一在 `ColorTransformPlan.ToSpec:415`（`cicpUnavailable=<枚举名>`）。

## 3. 回退触发条件与播报落点

| 触发 | 枚举 | Reason 片段 | 播报落点 |
|---|---|---|---|
| 源 ICC 可携带 | —（附加 CICP 或纯 ICC） | 「源 ICC 保留…不以其粗糙 CICP 命名替换」；先按实测色度/TRC 重写等价 v4.4.0 | `ToSpec` + QueueProcessor 后置 |
| png/apng/avif/jxl(ffmpeg) 可写 CICP | —（Out* 已写） | 「CICP 优先…只写 CICP」 | `ToSpec`；PNG 由 `PngCicpService.DecideLabel` 写 cICP chunk |
| jpg/jpeg/tiff 无 CICP 通路 | `ContainerNoCicp` / `ContainerNonNormative` | 「CICP 不可达…⇒ 由 CICP 三元组生成标准 ICC 兜底」 | `ToSpec:415`；jpg iccgen 后置 |
| ProPhoto/Adobe 不可命名 | `SpaceNotNameable` | 「该空间无 CICP 名…」 | 同上 |
| cjxl 枚举宽度写不出 | `OutletCannotExpress` | 「容器有 CICP 通路，但本出口写不出该空间」 | 同上 |
| HDR（PQ/HLG） | `HdrNoIccGenerator` | 「iccgen 不支持 PQ/HLG ⇒ 不附 ICC」 | 同上 |
| webp sRGB 等价 | —（UnlabeledAssumedSrgb） | 「未标注默认即 sRGB ⇒ 不附 ICC」 | `ToSpec` |

## 4. 显式策略为何零漂移

- `PlanCarry`（`ColorStrategyPlanning.cs:542`）/ `PlanCicpOnly`（:635）的标签结局本次**未动**
  （`PlanCarry` 不调 `SetLabelsFrom`；`PlanCicpOnly` 的 `StripIcc` 在新旧两路径结果一致）。
- **`PlanManual`（:704）显式补回「能共存容器上的双标定」**：因为 `SetLabelsFrom` 把默认改成 CICP-only 后，
  png/apng/avif（`CanHaveBothIccAndCicp=true`）不进择一分支（修改前 :739 / 现 :754）、`AttachIcc` 直接继承 `SetLabelsFrom` 结果，
  会把 Manual 从旧的「CICP + ICC 双标定」悄悄变成「只 CICP」——而 `manual.tip` 明写「同时写入 CICP 与 ICC」，
  这正是红线。故在 `PlanCore` 之后、择一分支之前补一段：当 `CanHaveBothIccAndCicp && ExitWritesIcc
  && (CanCarry||CanGenerate) && !hdr && core.OutTransfer!=null` 时恢复 `AttachIcc=true`，
  `IccPrimaries=core.OutPrimaries / IccTrc=core.OutTransfer（跟随像素实际 trc，不写死 sRGB）/ IccMatrix=core.OutMatrix`。
  HDR（PQ/HLG）iccgen 不支持 ⇒ 仍只 CICP、不附 ICC。**结论：Manual 仍是双标定，只是这条双标定由 PlanManual
  自己显式保证，而不再靠 SetLabelsFrom 的默认**——显式语义一字未改，只改了它由谁来保证。
- `CarryCicpLabels`（`ColorStrategyPlanning.cs:1071`）归因从「一律 SpaceNotNameable」细化为三档具体枚举——
  不改是否写标签，只改原因枚举的精确度。
- `Models/ColorStrategy.cs` 的 `FromIccMode` / `TryParse` 兼容入口未动；UI 在四个单选都没勾时
  返回 `Recommended`（`MainWindow.xaml.cs:2857-2859`），故改 `PlanRecommended/SetLabelsFrom` 即改「默认」，
  UI 侧无需额外接线；CLI 不传 `--color-strategy`、旧预设回放、AppSettings 水合同走 Recommended。

## 5. 旧默认 → 新默认的不兼容面

以下产物的**标注**会变（像素不变）：

- **png/apng（sRGB/可命名 SDR 目标）**：旧 = cICP + 自生成 iCCP 双描述；新 = **只有 cICP，无 iCCP**。
  查看器若只读 iCCP，会退回「未标注即 sRGB」——对 sRGB 目标结论不变，但对 BT.2020/P3 目标，
  新产物靠 cICP 表达（png 的 cICP 已由 `PngCicpService` 实测可写）。
- **源带 ICC 且空间可精确 CICP 命名（二轮新增，2026-11-14）**：旧 L1 只要源带 ICC 就携带（哪怕 P3 能被
  cICP 精确表达）；新口径下 P3/Display P3/BT.2020-SDR 这类**可精确命名**的源 → png/apng/avif/jxl 默认产物
  **不再带源 iCCP**，只写 cICP。日志会显式打「源 ICC 未携带——目标空间可精确 CICP 命名…CICP 已足以正确承载」
  并给 Advice 指向 CarryIcc。**对 sRGB 等价内容结论不变**（本就只有 cICP）；谁依赖 iCCP 的旧查看器会看到差异。
  决策层另有独立的 `DecideOutputColor` 收口语（见 §6），把 `:527` D2 广色域重挂 iccgen 那条也一并关掉。
- **avif 静态出口**：旧= nclx（`ExitWritesIcc=false`，实测事实未改）；新同 nclx，不附 ICC（本就落不了盘）。
- **jpg/jpeg/tiff**：无 CICP 通路 ⇒ 仍 ICC 兜底，与旧一致；只是 Reason/枚举更具体。
- **webp sRGB**：仍 `UnlabeledAssumedSrgb`（逐字保留），与旧一致。

可能看到差异的人：对比 png 产物字节、或用只认 iCCP 不认 cICP 的旧查看器看 png P3/BT.2020 产物者。

## 6. 改动的锁 / 新增的负控

### 修改的既有锁
- `tests/projects/ServiceProbe/Parts/17_ProbeContract.cs` C7b：原断言「std/zimg 都附 bt709 ICC
  （`IccTrc==bt709`）」改为「std/zimg 都只写 CICP=bt709、`IccTrc==null`、`AttachIcc==false`」。
  理由：这是默认口径变更（png 可写 CICP ⇒ CICP-only），不是放宽判据；「标注不随 std/zimg 口径漂移」
  的本意保留（两支逐值一致）。
- `tests/scripts/verify-color-wiring.ps1` (2)：**硬锁**——本格是 P3→sRGB png 的引擎 Map 路径，
  断言日志**必须**含「CICP 优先」**且**不含「已嵌入目标 ICC」（旧双描述策略会打后者 ⇒ 旧代码在此必红；
  不接受"计划为直通"，因为本格是 Map 不是直通）；(6)：正控（cICP=bt2020/bt709、transfer=1）+
  负控（产物字节级无 iCCP chunk、日志无「已嵌入目标 ICC」）。
- `ColorStrategyPlanning.CarryCicpLabels`：归因三档（见 §4）。

### 新增的负控（计划级，`24_ProbeWire.cs`）
- ④a png+sRGB 可写 CICP ⇒ `OutPrimaries=bt709/OutTransfer=iec61966-2-1` 且 `AttachIcc==false`
  （谁退回双描述即红）。
- ② jpg 无 CICP 通路 ⇒ `AttachIcc=true / IccPrimaries=bt709 / OutPrimaries==null /
  CicpUnavailable!=None`。
- ③ ProPhoto 不可命名 ⇒ `AttachIcc=true / OutPrimaries==null / CicpUnavailable==SpaceNotNameable`
  （不得懒塞 ContainerNoCicp）。
- ③b **BT.2020 走 cjxl 外部出口**（`ExternalEncoderExit=true`）：BT.2020 本身可 CICP 命名，但 cjxl
  `color_space=` 枚举只有 sRGB/DisplayP3/PQ/HLG 写不出 ⇒ `CicpUnavailable==OutletCannotExpress`
  且 `AttachIcc==true`（ICC 兜底）。这是新增枚举档 `OutletCannotExpress` 的**唯一触发探针**——
  证明它区别于 `SpaceNotNameable`（空间有 CICP 名）与 `ContainerNoCicp`（容器有 nclx 通路）。
- 既有 ProPhoto→jxl（`CapsAcceptsCicp(...,true)==false` + `AttachIcc && Action==Map`）保留，
  作为「出口枚举宽度 → ICC 兜底 + 执行体拒绝门」的反向证明。

### 二轮新增（2026-11-14：L1 收紧——源 ICC 只在 CICP 无法承载时才启用）

**代码改动（三处产品代码 + 一处 latent bug 修复）**：
- `ColorTransformPlan.SetLabelsFrom`：顶部算 `cicpCanCarry`；阶梯①进闸加 `&& !cicpCanCarry`；
  阶梯②在 `dst.SourceIccPath != null` 时追加 Reason「源 ICC 未携带…」+ Advice 指 CarryIcc。
  `IsPreciselyCicpNameable` 由 private 升为 internal（供 PlanCore 复用）。
- `ColorStrategyPlanning.PlanCore` L1 携带块（无显式目标路径，旧 :878）：同样加 `srcCicpCanCarry` 闸门——
  `IsPreciselyCicpNameable(src) && CapsAcceptsCicp(...)` ⇒ `AttachIcc=false / IccPath=null`，像素仍零改动、只写 CICP，
  Reason 打「源 ICC 未携带」。**这是默认无目标路径真正走的 L1**（引擎日志「L1 无损优先 carry-scope=All」）。
- `FfmpegCommandBuilder.DecideOutputColor`：在 Faithful 行后、`RunIccgen` 派生前加 **Recommended 专属收口**：
  `strategy==Recommended && d.CanWriteContainerCicp && outP/outT 非空 && !frameIsHdr && trc!=linear` ⇒ `d.AttachIcc=false`
  （RunIccgen/NeedsPostEmbedIcc 从 AttachIcc 派生故自动翻假）。**关掉的是 `:527` D2 广色域重挂 iccgen**
  （P3→png 旧逻辑会被它挂一张 iccgen 生成的 P3 ICC）。只收 Recommended；CarryIcc(preserveSource)/
  BakeCicpOnly(stripIcc)/Manual 不受影响。ProPhoto 不可命名 ⇒ outP 空 ⇒ 收口自然豁免，仍附 ICC 兜底。
- `ColorStrategyPlanning.CollectDegradations` NoIccCarrier 分两支：`OutPrimaries!=null`（CICP 已精确承载）
  ⇒ 信息项「已改用等价 CICP 描述」；`OutPrimaries==null` ⇒ 保留原「丢失源 ICC 描述精度」降级。
- 顺手修 `IccProfileBuilder.BuildTrc` parametric 分支漏 return 的 latent 编译错误（本轮重编才暴露）。

**新增负控**：
- 计划级 `24_ProbeWire.cs` **④c**：P3 源(带 ICC)+png+auto ⇒ `AttachIcc==false && IccPath==null && IccPrimaries==null
  && OutPrimaries==smpte432 && CicpUnavailable==None`（旧 L1 会 AttachIcc=true ⇒ 必红）。
  **反负控④d**：ProPhoto 不可精确命名 ⇒ L1 仍 `AttachIcc==true / IccPath!=null`（防砍成"永不用 ICC"）。
- 产物级 `verify-color-wiring.ps1` **(13)**：P3-ICC 源 `a2_srgb_p3.png`→png 默认，扫产物字节 `iCCP` 四字节魔数
  **必须不存在**；日志必须含「源 ICC 未携带」、不得再出现「后置嵌入源 ICC」；ffprobe cICP 必须 = smpte432。
- verdict `19_ProbeVerdict.cs`：gif→png 负控改判——sRGB 可精确 CICP 承载 ⇒ png 不附 ICC、只写 CICP，
  `ContainerCannotCarryIcc` 仍消失、`NoIccCarrier` 作为信息项登记。

**产物链路核实（约束 3）**：`QueueProcessor.effCarry`（:2745）只在 `Action==CarryIcc && AttachIcc=true` 才回带源 ICC；
新口径下 cicpCanCarry 走阶梯②/AttachIcc=false ⇒ effCarry=false；`CopyMetadataSafeAsync`（:775）排除 `--ICC_Profile`；
`PngCicpService.TryStripChunksSupersededByCicp`（:195）只剥 gAMA/cHRM/sRGB。**实测** `a2_srgb_p3.png`→png 默认产物
chunk 清单 = IHDR/pHYs/cICP/IDAT/IEND，**无 iCCP**（36123B；ffprobe = gbr/iec61966-2-1/smpte432）。

未新增 `verify-*.ps1`（负控全部并入既有探针，门禁条数仍 104，无需改 `_run-step3-gates.ps1` 四处登记）。

## 7. 验证读数（绑定产物指纹）

- 构建：`dotnet build ffmpegPictureUI.sln -c Release -m:1` ⇒ 0 错误。
- 被测产物指纹（sha256 前 16，DLL 非 exe，win-x64 下 apphost 实际加载的那两份）：
  - `src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.dll` = **`2EF23939079C01FD`**（含 v4.4 ICC writer 迁移 + L1 收紧 + Decision 收口；
    上一轮 L1 收紧版为 `2A0676888BE9A7BB`，一轮 CICP 优先版为 `658b52004c4c0d6f`）；
  - `tests/projects/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.dll` = **`373943DFB609333C`**（含 v4.4 迁移探针 + ④c/④d + verdict 负控改判；
    上一轮为 `38DFD226AB7E751C`，一轮为 `82d3e5bf1efa5cfa`）。
  - 注：`bin/Release/net11.0/FfmpegGui.dll`（不带 win-x64）是陈旧中间件，勿用作指纹。
- `verify-probe-mirror-parity`（逐名钉死每 mode 的 `Check()` 调用位数）：**PASS=91 FAIL=0**。
  登记值：verdict=**36**、wire=**106**、faithful=**12**、icc=**14**，总量实取 **985** == 登记表各 mode 之和 985。
  （verdict +1 = 19_ProbeVerdict 改判；wire +7 = 本批④c/④d + 上一轮未同批登记的④a/②/③/③b；
  faithful/icc 由 ICC v4.4 迁移线自行抬表。）
- 针对性门禁（非全量，`-Scripts` 指定）：
  - `wire` 运行 **105/0**（mirror-parity 静态登记 **106**；新增 ④c/④d 两条）；`verdict` 运行 **42/0**（静态登记 **36**，含 NoIccCarrier 新分支断言）。
  - `verify-color-wiring` PASS=157 FAIL=3——3 条红均为环境性：(10) JXL icc_pathname/exiftool 后置、(11) 中转目录堆积；
    **新增 (13) 五条断言全在 157 PASS 内**（P3-ICC 源→png 默认：无 iCCP chunk、日志含「源 ICC 未携带」、无「后置嵌入」、cICP=smpte432）。
    旧 (2)「CICP 优先且无已嵌入目标 ICC」与 (6) 字节级无 iCCP 仍在 PASS 内。
  - `verify-decision-delivery` pass=88 fail=6——红全在 ⑰b legacy + 12-bit AVIF（exit=91「16-bit AVIF 需要引擎、
    legacy 不承载 Sample Transform」，位深/引擎能力门，与本轮 ICC 改动无关；⑰ 引擎路径全 PASS）。
  - 门禁条数仍 104（未新增 verify-*.ps1）。
- 环境性红（与本次标注改动无关，跨次运行波动，按规矩不改阈值/不修）：
  thumbnail T11/T12/T17、bench 单核 MPix/s 计时、JXR 中转目录堆积；
  decision-delivery 的 ⑯ 降档命名 / ⑰b legacy 12-bit AVIF 出货 / ⑱-4 PQ 命令行形状均为 HEAD 既有。

## 8. 未了结 / 判据缺口

- **exiftool 抖动——已定位机制并加固，但残余窗口未根除（如实记录，不再调参）**：
  - 机制（不是"间歇/偶发"一句带过）：随包 exiftool 是 perl PAR 打包壳，`perl532.dll` 在
    `exiftool_files/` 下在位，但快速 `Start-Process` spawn 时偶发
    `Failed to load Perl DLL "perl532.dll" code 126`（=ERROR_MOD_NOT_FOUND），此时 **stdout 全空**，
    `IccRedChroma`/`HasIcc` 把空读误判成"产物无 ICC"。实测空 stdout 率 **4/12 ≈ 33%**；
    夹具本身有 718 字节 ICC（手工 `-b -icc_profile` 实证 0.67999/0.32001）。
  - 加固（仅取数可靠性，不改判据/断言语义）：`verify-decision-delivery.ps1` 的 `ExifTool` helper
    改为**仅 stdout 空才重试**、6 次递增退避（250→500→800→1000→1200→1500 ms），脚本开头加
    `ExifToolWarmup`（`exiftool -ver` 预热，把首个 spawn 的 DLL 加载窗口挪出读数调用）。
    红线守住：最终仍空 ⇒ 返回空 ⇒ 断言按失败处理，绝不把"读不到"洗成通过。⑱ 段经 `ExifNum`(:992) 本就调该 helper，无需另接。
  - **残余窗口（未根除）**：连跑两次 ⑭ 段全绿，但同一次运行里 **`hdr/jpg8` 的 ICC 色度读**
    （走同一 helper、6 次退避约 5 s 仍偶有一次空读）与 **wire F-12**（C# ServiceProbe 自己的
    exiftool 调用，不在 ps1 helper 覆盖内）仍偶发空读 ⇒ perl DLL 失败窗口可超过 ~5 s。
    **该 gate 的 ⑭ 直连/引擎产物 ICC 读取已稳定绿；其余 exiftool 读数仍存在未根除的环境窗口，
    验收结论以 ⑭ 段 + 计划级/字节级负控（contract 161、wire 103、wiring (6) 字节级）为准。**
- `OutletCannotExpress` 端到端（真实 cjxl 出码读 nclx）仍靠计划层探针 ③b 与既有 ProPhoto→jxl 覆盖；
  未单独跑 cjxl 二进制断言产物字节（该出口宽度已在 `ExternalOutletCicpSpaceEnums` 钉死）。

## 9. 四模式对照表（UI 单选 → 策略 → 标注结局）

四个单选与 `ColorStrategy` 的映射在 `MainWindow.xaml.cs:2852-2858`（`GetColorStrategy`）：
`IccModeCarry→CarryIcc`、`IccModeBake→BakeCicpOnly`、`IccModeBakeOnly→Manual`、其余（含全不选）→`Recommended`。

| 维度 | Recommended（`IccModeNone`，默认） | CarryIcc（`IccModeCarry`） | BakeCicpOnly（`IccModeBake`） | Manual（`IccModeBakeOnly`） |
|---|---|---|---|---|
| **触发条件** | 用户**未点过**任何单选时的默认；本轮后**永不被别的操作隐式带入/切走**（§10） | 用户显式点选「携带 ICC」；不被隐式切 | 用户显式点选「仅 CICP」；不被隐式切 | 用户显式点选「手动」；不被隐式切（连旧的反向联动也删了） |
| **计划入口** | `PlanRecommended`→`SetLabelsFrom`（`ColorTransformPlan.cs:1094`）四阶梯 | `PlanCarry`（`ColorStrategyPlanning.cs:542`），**不调 SetLabelsFrom** | `PlanCicpOnly`（:635），`StripIcc` | `PlanManual`（:704）+ `PlanCore` |
| **像素处理** | 默认即映射到目标（除非阶梯①直通）；`TargetExplicit`/`ForceMapping` 由目标推断 | **源 ICC 优先标准空间**：等价标准空间（sRGB/P3/Rec.2020 SDR）⇒ 像素零改动；标准空间更宽（如 Adobe RGB→Rec.2020）⇒ 由 `ColorMappingEngine.Plan` 做无削色矩阵映射，Action=`Map`；源色域超出所有候选（ProPhoto）⇒ 保留源 ICC、零改动（L0） | 按目标映射，`StripIcc` 剥掉源 ICC | 按用户给的三元组映射；不可共存容器上 CICP 优先择一（:754） |
| **CICP 落点** | 阶梯②命中即写 `OutPrimaries/OutTransfer/OutMatrix`（png cICP）；不可达走③ | **强制双写**：标准空间已给出目标 CICP 时不再用源 CICP 覆盖目标标签；`EnforceContainerAnnotationRules` 对 `DeclaredStrategy==CarryIcc && !CanHaveBothIccAndCicp` **契约拒绝**（不剥 CICP、不交付半承诺）。png/apng ⇒ 标准 ICC + 目标 CICP；jxl/avif（当前 ffmpeg 出口）⇒ 拒绝并点名；jpeg/webp/tiff（无 CICP 通路）⇒ 仅 ICC，CICP 不可能 | **不写 ICC**（`StripIcc`） | 写 `OutPrimaries/OutTransfer/OutMatrix`（用户三元组）；不可共存容器上 :754 择一（用户给了三元组⇒CICP 优先，否则 ICC 优先） |
| **ICC 落点** | **仅当 CICP 无法正确承载时才启用**（四类：空间不可命名 / CICP 三元组不等价 / 容器无 CICP 通路 / 出口枚举写不出）；可精确命名 + 出口可写 CICP 时**不携源 ICC、不附生成 ICC**，日志打「源 ICC 未携带」+ Advice 指 CarryIcc；决策层 `DecideOutputColor` 另收一道口把 `:527` D2 广色域重挂 iccgen 关掉 | **强制附 ICC**：源有 ICC 且被标准空间覆盖 ⇒ 用 `NameableSupersetFor`/`NarrowestStandardGamutFor` 选最窄覆盖空间，托管的 `IccProfileBuilder` 生成标准 ICC（P3 544B 等）；源无 ICC 但有可命名 CICP ⇒ 按源令牌生成标准 ICC；源无 ICC 但判定隐含 sRGB ⇒ 生成标准 sRGB ICC。PNG/APNG 走纯托管 iCCP，不依赖 exiftool；`ExitWritesIcc=false` 或容器不能同时承载 ICC+CICP ⇒ Reject，不降级成 CICP-only | **不写 ICC**（`StripIcc`） | 能共存容器（png/apng/avif）`AttachIcc=true`，`IccTrc=core.OutTransfer`（跟随像素实际 trc，**不写死 sRGB**） |
| **HDR（PQ/HLG）** | 走阶梯② CICP，`HdrNoIccGenerator` 不附 ICC | HDR 源 ICC 原样携带；CICP 继承源标注 | 只 CICP，无 ICC | **不附 ICC**（iccgen 不支持 PQ/HLG） |
| **已知边界** | AVIF 静态 `ExitWritesIcc=false`⇒只 nclx；webp sRGB 等价⇒`UnlabeledAssumedSrgb`；cjxl 枚举宽度⇒`OutletCannotExpress` | GIF 无色彩通路/容器不写 ICC 时降级；`CanCarryArbitraryIcc` 假 ⇒ 不携带；**不可共存容器（JXL/TIFF/JPEG/WebP）上显式 CarryIcc 若要求两者 ⇒ Reject，不再剥 CICP 留 ICC** | **有 CICP 通路**：png/apng/avif/jxl（`CanCicp=true`）；**无 CICP 通路**：JPEG/WebP/TIFF（`CanCicp=false`）——其中 JPEG/WebP `UnlabeledMeansSrgb=true` 凭「未标注即隐含 sRGB」允许无操作直通，**TIFF `UnlabeledMeansSrgb=false`**（`NoCicpReason=ContainerNonNormative`）⇒ 不满足等价 sRGB 时需显式 `AllowUnlabeled` 否则拒绝+建议；不可命名空间走 `NameableSupersetFor` 落超集，找不到则 Reject | TIFF `ContainerNonNormative` CICP；不可共存容器上 :754 CICP 优先择一 |

**CarryIcc 的合法结局**（`PlanCarry`，2026-11-14 用户口径定稿）：① 源有 ICC 且被标准空间覆盖 ⇒ 取“最窄覆盖标准空间”（sRGB/Display P3/Rec.2020 SDR）；等价时零像素改动，更宽时做无削色矩阵映射，并写标准 ICC + 目标 CICP；② 源有 ICC 但超出所有候选（ProPhoto 超出 BT.2020）⇒ 保留源 ICC，走 L0 超广色域保真，不伪造 bt2020 标签；③ 无源 ICC 但源可命名 CICP / 判定隐含 sRGB ⇒ 按源令牌或 sRGB 生成标准 ICC，并同写 CICP；④ 其余（源不可确定/容器不能承载/出口不写 ICC）⇒ 拒绝，不静默降级。`CarryCicpLabels`（:1167）归因细化为 `SpaceNotNameable / ContainerNoCicp / OutletCannotExpress` 三档，不改是否写标签。

> ⚠ **两个不同函数，条件不同，勿混**：Recommended 阶梯①（`SetLabelsFrom` 里的源 ICC 携带分支）附 CICP 的
> 条件是 `CanHaveBothIccAndCicp && IsPreciselyCicpNameable && CapsAcceptsCicp` 三者同时成立；
> 而 CarryIcc 策略走 `PlanCarry`→`CarryCicpLabels`，**只判 `CapsAcceptsCicp`**，共存与否由后续
> `EnforceContainerAnnotationRules` 判：显式 CarryIcc 在 `!CanHaveBothIccAndCicp` 时**拒绝**（不再择一剥 CICP），
> Recommended 才保留择一。这是两条独立路径，不是同一格。

> 📎 **ICC 版本脚注**：上表中凡**产品自生成** ICC 的落点（Recommended 兜底阶梯③、Manual 双标定里自生成那份），
> 版本一律 = **v4.4（`ICC.1:2022` / 4.4.0.0）**；JPEG 出口可由用户选降到 v2.0 兼容档（默认仍 v4.4，
> 非 JPEG 出口后端归一回 v4.4）。**携带 ICC（CarryIcc）与源 ICC 保留路径永远用源文件自带的版本字节，不重写。**
> 详见 §9a。

## 9a. 自生成 ICC 的版本策略（v4.4 默认，JPEG 可选 v2.0）

**现状（2026-11-14 已落地）**：所有产品自生成的 ICC 一律写 **v4.4**（profile version `{4,0x40,0,0}`，
即 `ICC.1:2022` / 4.4.0.0）。这与 CICP 优先 / ICC 兜底的口径**正交**——版本号升的是"自生成 ICC 写出来长什么样"，
不是"什么时候写 ICC"。

### 为什么 cprt 必须从 v2 `textType` 升到 v4 `mluc`
只把版本字节从 v2 改成 v4、而 `cprt`（copyright tag）仍用 v2 `textType`，会产出一张
**「4.4 头 + v2 tag」的混合非法 profile**：v4 规范要求所有 tag 用 v4 类型（`mluc` / `desc` 用 `mluc`）。
lcms / exiftool 对这种混合件只是"容忍"（能读不报错），不是"合规"——严格校验器（如 Adobe ICC 验证器、
部分浏览器）会拒收或降级。故 writer 同步把 `cprt` 从 `textType` 改为 `mluc`，版本字节与 tag 类型一起升。

### 四模式的版本落点
| 模式 | ICC 版本来源 | 说明 |
|---|---|---|
| Recommended（阶梯③兜底） | **v4.4**（自生成） | CICP 无法承载时才写；writer 版本字节 `{4,0x40,0,0}` |
| CarryIcc | **源文件自带版本**（原样字节携带） | 不重写、不转码；`verify-prophoto-faithful` 的"源 ICC 与产物逐字节相同"断言仍绿 |
| BakeCicpOnly | 不写 ICC | `StripIcc` |
| Manual（双标定里自生成那份） | **v4.4**（自生成） | `IccTrc=core.OutTransfer`，版本同 Recommended 兜底 |

### JPEG 出口的 v2.0 兼容档（用户可选）
- **默认仍是 v4.4**；JPEG/JPEG 族额外提供用户可选 v2.0 兼容档（UI 下拉只在 JPEG 出口可见）。
- 非 JPEG 出口即使用户选了 v2，后端也经 `IsJpegLikeFormat` 单点闸**归一回 v4.4**——
  v2 `curv` 单值 gamma 精度不足（见下），且 PNG/AVIF/JXL 的 ICC 消费端按 v4 写更安全。
- **v2 精度代价**（实测）：lcms 256×256 渐变 ProPhoto→sRGB，v2 `curv count=1`（gamma u8Fixed8 ≈ 1.8008）
  与 v4.4 `para` 参数化 TRC 比，**最大单通道差 3/255、差异通道占比 2.79%**——来源是 gamma 量化（u8Fixed8
  只能取 ~1.8 这个离散档）+ v2 单值无线性趾段。v4.4 `para` 写精确参数，无此量化损失。

### 实测产物字节
- 自生成 v4.4 = **544 B**（10 tag；`desc` mluc 54 + `cprt` mluc 78 + TRC `para` 32（三通道共享）
  + 4×XYZ 80 + `chad` 44 + 头/目录）。理论最小 488–510，差额是文案与对齐开销。
- v2 兼容档 = **432 B**（9 tag；`rTRC` = `curv count=1`、gamma u8Fixed8 ≈ 1.8008）。

### 红线（v4.4 迁移没有破坏的）
- **源 ICC 永远原样字节携带、不重写版本**：CarryIcc 与 L1 携带路径逐字节复制源 profile。
- **默认口径没有回退**：ICC 仍只在 CICP 无法正确承载时启用（§2 阶梯①的 `!cicpCanCarry` 闸门不动）；
  v4.4 迁移**没有**让源 ICC 重新变成默认携带项。

## 10. 策略单选不再自动联动（本轮 UI 改造）

### 规则
用户**未手动点过四个单选时，始终停在 `Recommended`**；不随**选色彩空间 / 切输出格式 / HDR 开关 / 加载预设**
自动切到 `CarryIcc / BakeCicpOnly / Manual`。显式点选的语义保持不变。

### 删掉了什么（`MainWindow.xaml.cs`）
- **删方法** `AutoLinkIccModeFromColorSpace(cs)` 及其在 `ColorSpaceCombo_SelectionChanged` 末尾的调用（旧 :4017）。
  旧逻辑：选非标准色域（P3 / BT.2020 PQ / HLG，`spec.Kind != Standard`）⇒ 把 `IccModeBake.IsChecked=true`
  （=仅 CICP）；回 sRGB ⇒ 若当前是 `IccModeBake` **或 `IccModeBakeOnly`（手动）** 就切回 `IccModeNone`——
  连用户已选的手动都能被反向覆盖。
- **删方法** `MarkIccModeTouched()`；**删死字段** `_iccModeTouched`、`_suppressIccAutoLink`（原 :325）。
  四个 radio 的 `IsCheckedChanged` 处理器去掉 `MarkIccModeTouched()` 调用，保留 `UpdateIccPanelVisibility(); RegenerateCommand();`。
- **保留** `UpdateIccCarryLabel()`（:4022）——它只同步 CarryIcc 提示标签文案，不写单选选中态。
- 全文件对 `IccMode*.IsChecked =` 的赋值现在只剩 `SetColorStrategyRadio`（:2865，仅预设恢复调用）。
  `UpdateOptionAvailability` 只动 `IsEnabled`/面板可见性，不碰选中态。

### 预设那处刻意例外
`SetColorStrategyRadio` 只被预设载入调用（:6681-6689）。沿用 `:6674-6675` 既有规矩「绝不静默改写为 Recommended」：
预设**显式记录**了 `ColorStrategy`（`TryParse`）或遗留 `IccMode`（`FromIccMode` 迁移）时仍按记录恢复——那是用户
当初的显式选择；两者都不可用 ⇒ **保持当前单选不变**并在日志点名两个字段原始值。这是「加载预设也不许自动切」
的唯一例外，理由：强制回推荐会让带策略的旧预设变成谎报（与 `:6668-6675` 既有产品语义一致）。

### 断言 ↔ 表格对号（分两类：硬锁 vs 回归护栏）

**本次变更的硬证据只有 I5/I5b（旧逻辑必红）；I6/I7/I9 是回归护栏（旧/新都绿，防未来重引入联动）。**

| 断言（`UiTestHost/Program.cs` I 组） | 钉住表中哪格 | 类别 | 旧逻辑为何该类 |
|---|---|---|---|
| **I5** 选 Display P3 后 `IccModeNone==true`、其余全 false、`GetColorStrategy()==Recommended` | Recommended 行「不被隐式带入」 | **硬锁（旧必红）** | 旧 `AutoLink` 在非标准色域分支把 `IccModeBake.IsChecked=true` ⇒ `IccModeNone==true` 必然失败 |
| **I5b** 选 BT.2020 PQ（及 HLG）后同上 | 同上（HDR 色域也不自动切） | **硬锁（旧必红）** | 旧逻辑同样把 `IccModeBake` 置 true ⇒ 失败 |
| **I6** 选 sRGB 后仍 `Recommended` | 删掉的反向联动（回 sRGB 切回 None） | **回归护栏（旧也绿）** | 旧逻辑「先切 Bake、回 sRGB 再切回 None」最终态也是 None ⇒ I6 在旧代码上同样通过；它防的是未来有人把反向联动加回来 |
| **I7** 显式点 `IccModeBakeOnly`（手动）后再选 P3、再选 sRGB，手动保持不动 | Manual 行「显式语义不被覆盖」 | **回归护栏（旧也绿）** | 旧逻辑有 `_iccModeTouched` 早退（用户真点过就 return）⇒ 旧代码下 BakeOnly 本就不会被切；它防的是未来有人删了 touched 早退、把手动也联动掉 |
| **I9** 切格式 png→jpg→avif 后未点选仍 `Recommended` | Recommended 行「切格式不自动切」 | **回归护栏（旧也绿）** | 旧代码切格式经 `UpdateOptionAvailability` 重建 ComboBox 归零 → `AutoLink("auto")` 不进非标准分支，本就不改单选；它钉住格式通路零隐式写入 |

实测：`tests/projects/ui-color` 拆分件 **91 PASS / 0 FAIL**（原 89，+2：I5b/I9）。

### HDR 开关触发面的可行性判定（第 4 条）
核查 `HdrMode_Changed`（`MainWindow.xaml.cs:6141`）：它调 `RefreshOptionsKeepingTargets()`，按其注释
（:6150-6151）**保留用户已选的色域/位深/质量**，只重过滤候选集，**不会把 ColorSpaceCombo 改成 BT.2020 PQ/HLG**。
⇒ HDR 开关**不经非标准色域通路**，旧 `AutoLink` 不会因此点火把策略切成 Bake。**结论：无法构造「旧逻辑必红」的
HDR 硬锁**，故不新增 I10（不硬凑）；HDR 通路的「不隐式切」由「全文件只剩 `SetColorStrategyRadio` 写选中态」这一
静态事实 + I9 的格式通路护栏共同覆盖，未来若有人在 `HdrMode_Changed` 里加单选写入，I9 类护栏与全文件扫描会暴露。

### 计数同批抬表（三处，锁同源）
ui-color 89→**91**、合计 473→**475**（=84+59+91+169+72）。改动点：
`verify-ui-host.ps1`（ui-color Expected + `$aggExpected`）、`verify-ui-split-union.ps1`（`$expectedOldCount`）、
`_module-registry.json`（`ui-host-arms` value/members.ui-color、`ui-split-union` value）。
`uicolorGroup.cs` 为 `_gen-ui-split.ps1` 从单体切片的生成件（未手改，重跑生成器）；
`verify-ui-split-reproducible`（两次生成逐字节对撞）19/0、`verify-ui-split-union` 3c/3d 475==475、missing/extra=0。
门禁条数仍 104（未新增 `verify-*.ps1`）。
