# 图像管线审查报告（2026-09-22）

> 审查对象：**产品侧**的图像处理管线（不是测试基础设施）。
> 方法：**先读登记再读代码**（本仓有「三次白跑」的教训）——逐条发现都先与
> `docs/HANDOVER.md`、`docs/TESTING.md §6`、`.workbuddy-ai/memory/COLOR_TRAPS.md`、`.workbuddy-ai/memory/MEMORY.md` 对账，
> 标注 `【新发现】/【已登记】/【已结案】/【假阳性（已更正）】`。
> ⚠ 本仓纪律：**不把「合法且点名的拒绝」当缺陷**；**无代码证据不下结论**。

---

## 1. 管线主干（从代码核实）

`QueueProcessor.ProcessAsync` 的每项流程：

| # | 阶段 | 关键位置 |
|---|---|---|
| 1 | 入队（`QueueItem` = 输入 + `FfmpegOptions`）| `Services/QueueProcessor.cs` |
| 2 | **最长边预缩放（可选）**：仅源超限时，产出 `scaledInput` 供后续替代输入 | `:596-609` |
| 3 | **通路分派**：按 `输入 × 输出` 组合，**专用通路优先** | `:645-711` |
| 4 | **主分支**：`TryRunColorEngineAsync`（引擎插入点；不可走 ⇒ 回退 legacy）| `:716` |
| 5 | **后置（两路共用）**：`RestoreMetadataAsync` → `ApplyColorPostStepsAsync` | `:835` / `:839` |

**专用通路清单**（9 条）：Gif→Avif（avifenc 两步法）· Avif→Gif/WebP · Jxl 输入 · Jxr 输入 · GainMap ·
Cjxl 后端 · Cjpegli 后端 · Jxr 后端 · Dng 后端。

### 1.1 后置步骤的内部顺序（`ApplyColorPostStepsAsync`）

`元数据恢复` → `sBIT`（`:1210`）→ `cICP`（`:1226`）→ `引擎 ICC`（`:1303`）→ `legacy ICC`（`:1331`）→
`TIFF 目标 ICC`（`:1336`）→ `超广色域保真 ICC`（`:1383`）。

✅ **顺序依赖已被显式登记**：sBIT 段注释写明「**必须在元数据恢复之后执行**（exiftool 复制元数据可能改写 chunk）」。
✅ **PNG chunk 插入位置正确**：`PngCicpService.cs:177` 遇到 `IDAT`/`IEND` 即停止扫描（色彩 chunk 必在 IDAT 之前）。

---

## 2. 覆盖矩阵：**专用通路 × 色彩后置**（本次新做的量化）

⚠ 关键机制：`ColorPostOptions` / `ColorPostProbeInput`（即「同源登记」）**只在 `QueueProcessor.cs:1165-1166` 赋值** ——
也就是**只有命令行由 `FfmpegCommandBuilder` 产出的通路**才有完整色彩后置；
**9 条专用通路都不登记** ⇒ 它们落进「**裁决点未参与**（不猜标签、不补 ICC）」分支（`:854-860`）。

这是**有意设计**（宁可显式记录也不猜标签），但必须逐通路确认**产物自己有没有写色彩描述**。
下表按**方法真实结尾**（花括号配对）定界，逐通路统计：

| 专用通路 | 范围 | 元数据恢复 | 色彩描述写入 |
|---|---|---|---|
| Gif→Avif（avifenc 两步法）| `:3089-3238` | ✅ | ⚠ **无** |
| Avif→Gif/WebP | `:2968-3086` | ✅ | ⚠ **无** |
| Jxl 输入 | `:3241-3422` | ✅ | ⚠ **无** |
| Jxr 输入 | `:3428-3610` | ✅ | ⚠ **无** |
| GainMap | `:2026-2170` | ✅ | ✅ `GainMapEncoder` |
| Cjxl 后端 | `:1818-1969` | ✅ | ✅ `icc_pathname` + `IccProfileService` |
| Cjpegli 后端 | `:1972-2019` | ✅ | ⚠ **无** |
| Jxr 后端 | `:2176-2306` | ✅ | ✅ `WriteJxrHdrMetadataAsync` |
| Dng 后端 | `:2429-2480` | ✅ | ⚠ **无** |

⇒ **9/9 都做元数据恢复**；**4/9 自己写色彩描述**。
⚠ **待确认（需运行验证，不是结论）**：那 5 条「不写」的通路里，哪些是**容器/编码器本来就会写**（如 GIF 无 ICC 通路、DNG 由 dngtool 负责），
哪些是**真的没人写**（**Gif→Avif 两步法**与 **Cjpegli** 最可疑）—— 见 §5。

---

## 3. 本次**新发现**

### 3.1 ⚠ `PngCicpService` 的「已知缺口」自述**已过期**（已修）

- **原文**（`:12-14`）：「⚠ 已知缺口：生产路径（QueueProcessor）只调 `TryInsertSbit`，**未调** `TryInsertCicp`
  ⇒ HDR(PQ/HLG) PNG 目前**没有任何色彩描述**（iccgen 又不支持 HDR ICC）」
- **实测**：生产路径**确实**会调 —— `QueueProcessor.cs:1278` 先 `PngCicpService.DecideLabel(...)`，
  `:1282-1283` 的 `WriteCicp` 分支即调 `TryInsertCicp` ⇒ **该缺口已闭合**。
- **危害**：读者会据旧文以为 HDR PNG 仍无色彩描述（**误导**），甚至据此重做已完成的工作。
- **处置**：✅ **已更正**（改为「该缺口已闭合」+ 保留更正记录；`cref` 解析通过、编译 0 警告）。

### 3.2 ✅ 「附 ICC」**不存在非预期的多真源**（干净结论）

初查时 `AttachIcc` 的赋值点看起来有 3 处（`ColorStrategyPlanning` / `ColorTransformPlan` / `FfmpegCommandBuilder.Decision`）
+ `RawColorPipeline` 的 2 处改写，像是多真源。**逐点核实后**：

| 产出方 | 判定 |
|---|---|
| `ColorStrategyPlanning.cs`（`:322`/`:378`/`:521`/`:585`/`:707`）| 规划层，**一个决策者多分支** ✓ |
| `ColorTransformPlan.SetLabelsFrom()`（`:791`，含 `:798`/`:833`/`:845`）| **同一规划层内的标注辅助方法**（多分支、单入口）✓ |
| `FfmpegCommandBuilder.Decision.cs`（`:164`/`:167`/`:363`/`:379`/`:394`）| **legacy 管线自己的决策** —— 两条管线的预期差异 ✓ |
| `RawColorPipeline.cs:307`（GainMap 出口）| ✅ **执行体声明自己实际写了什么**：底图 ICC 由 `GainMapEncoder` 写，若保留 `AttachIcc` 会让后置步骤**用计划层目标空间覆盖底图 ICC（错标）**，且改写 APP2 可能**破坏 MPF 偏移**（增益图定位依赖它）|
| `RawColorPipeline.cs:516`（cjxl 出口）| ✅ 同款：cjxl 已写 ICC，若不清空，后置会再用 exiftool 嵌一次 —— 对 JXL **必然失败**（实测退出码 1）⇒ 用户会看到一条**假告警** |

⇒ 两处改写**不是二次决策**，而是「**执行体 → 后置步骤**」的声明式约定 ✓。
⚠ 该约定「**是约定而非类型强制**」**已在 `TESTING.md` 登记**（「将来新增读 `plan` 标注的后置步骤必须同样尊重」）⇒ **无需重报** `【已登记】`。

### 3.3 ✅ 其它自述缺口的核查（**确认不是过期**，避免假阳性）

| 自述 | 位置 | 实测 | 判定 |
|---|---|---|---|
| 「已知缺口：Ultra HDR JPEG 作为输入没有**入队期**标志，仍按普通档预留」| `QueueProcessor.cs:108` | 检测发生在**处理期**（`:418-423` 的 `IsUltraHdrJpegAsync`），不在 `Add` ⇒ 该陈述**仍准确** | ✅ 不过期 |
| 「尚未实现：cLLI / mDCV」| `PngCicpService.cs:7` | 全文件除该注释外 **0** 处 ⇒ 确实未实现 | ✅ 准确 |
| 「未实现：bt2390，绝不凭记忆填常数」| `ColorMapping/ToneMapping.cs:32` | 引擎有枚举但 `Apply()` 原样返回（策略层当"未就绪"）⇒ 准确 | ✅ 准确 |

### 3.4 ⚠⚠【新发现 · 已实机复现】透明输入 + **需要色彩变换** ⇒ alpha 被**静默拍平**（png / tiff / webp）

> ✅ **2026-09-22 已修（引擎路径）**：把 gif 出口的「第二输入取 alpha + `alphamerge`」推广到 png/apng/tiff/webp
> 并显式给出带 alpha 的 `-pix_fmt`。验证：真透明源（`alpha=0..0`）经 Display P3 ⇒
> png/tiff = **`rgba`**、webp = **`argb`**，且 **alpha 仍为 `0..0`** ✓（修复前为 `rgb24`/`yuv420p`，无 alpha 通道）。
> ✅ **2026-09-22 亦已修（legacy 路径）**：它的色彩链以 `format=rgb48le` 开头（无 alpha）⇒ 同一病灶；
> 修法同款（第二输入取 alpha + `alphamerge`），⚠ **只对带 alpha 的源生效** ⇒ 不透明源命令行**逐字不变**。
> 验证：legacy + 真透明源 + P3 ⇒ png/tiff = `rgba`、webp = `argb`，alpha `0..0` ✓。
> ⚠ **avif 仍不在范围内**（其 alpha 是独立辅助流，引擎出口已登记为「已知不支持」）。
> 详见 `HANDOVER §37.2/§37.3`；两条路径均已纳入门禁 `verify-engine-alpha-preserve.ps1`（**29/0**）。

**现象**：带 alpha 的输入（`tests/output/sources/src_alpha.png`，实测 `pix_fmt=rgba`）在**不触发色彩变换**时，
两条管线都保住 alpha（产物 `rgba`）；但一旦**需要色彩变换**（`--color-space "Display P3"`），
**两条管线都丢 alpha，且无任何点名**：

| 用例 | 产物 `pix_fmt` | 判定 |
|---|---|---|
| 透明源 → png（默认策略，**无**变换）| `rgba` | ✅ 保住 |
| 透明源 → png（`--color-engine legacy`，**无**变换）| `rgba` | ✅ 保住 |
| **透明源 → png（Display P3）** | **`rgb24`** | ⚠ **丢 alpha** |
| **透明源 → tiff（Display P3）** | **`rgb24`** | ⚠ **丢 alpha** |
| **透明源 → webp（Display P3）** | **`yuv420p`** | ⚠ **丢 alpha** |
| **透明源 → png（Display P3，legacy）** | **`rgb48be`** | ⚠ **丢 alpha** |

**机制（代码证据）**：
- 引擎解码**恒为无 alpha**：`RawColorPipeline.cs:97` 的 `vf = "format=rgb48le"`（无缩放时）⇒ `rgb24`/`rgb48le` 都**没有 alpha 平面**；
- 引擎的编码参数**除 jpg 外一律不给 `-pix_fmt`**：`ImageEncoderArgs.MapEnginePixFmt` 在 `:609` 对非 jpg **直接 `return null`** ⇒ 编码器只能按"无 alpha 的输入帧"推断；
- 引擎里**只有 gif 分支**补 alpha：`RawColorPipeline.BuildEncodeArgs` 的 `:873` 是 `if (ext == "gif" && o != null)`；
  （另有 **cjxl 出口** `:471`、**JXR 出口** `:590` 两处 `hasAlpha` 分支 —— 说明**作者知道这个机制**，但**未覆盖 png/tiff/webp/avif 主出口**）。
- ⚠ 而 gif 分支自己的注释**已经写明**这个后果：「引擎 raw 通路是 rgb24/rgb48le（**无 alpha 平面**），
  直接喂进去会把透明**静默拍平**」⇒ **机制已知、修法已知（第二输入取 alpha + alphamerge），只是没接到这几个出口上**。

**与登记对账**：
| 出口 | 登记状态 |
|---|---|
| **AVIF** 丢 alpha | ✅ **已登记**（`HANDOVER.md:137`：引擎 avif 出口 `hasAlpha` **硬编码 false**；且 ffmpeg 侧也丢）|
| **GIF** 透明 | ✅ 已处理（第二输入补 alpha，`HANDOVER.md:23`）|
| **png / tiff / webp（有色彩变换时）** | ⚠ **未登记** —— 本报告**首次记录** |

**影响**：**真缺陷（静默数据丢失）**。透明 PNG/WebP/TIFF 一旦需要色彩变换（广色域、ICC 策略、HDR 等常见场景），
产物**静默丢失透明通道**，用户看到"已完成"。
⚠ **且无门禁覆盖**：现有 `verify-*-anim-probe` / `G1` 等端到端用例用的是**不透明**素材或**不触发变换**的组合 ⇒ 所以一直没红。

**建议修法**（与 gif 出口同款，最小改动）：在 `BuildEncodeArgs` 里把 gif 分支的
「**第二输入只取原文件的 alpha 平面 + `alphamerge`**」推广到 `png/apng/tiff/webp/avif`（当**源确实带 alpha** 时），
并把「源是否带 alpha」作为**入队期/计划期**已知量传进来（⚠ 不要在编码期做文件嗅探）。
⚠ 本条的**触发条件已实测**，但**修法需你确认**（属产品行为：是否要让色彩变换路径支持 alpha）。

---

## 3bis. 本次并行审查（4 条轴）的结果与**我的核实**

> ⚠ 下表里标注「待逐条复现」的轴 C 两条，**已在 §3.5 / §3.6 给出实机结论**（一条确认、一条推翻）—— 请连着读。

| 轴 | Agent 结论 | 我的核实 |
|---|---|---|
| **A** 引擎主链顺序与契约 | 报「引擎主出口丢 alpha」+ 3 条 | ✅ **采纳并扩宽**（见 §3.4：实机复现，且 **legacy 也丢**；触发条件是**色彩变换**而非"引擎"）|
| **B** legacy 与引擎的分歧 | 6 条（含 legacy 无超集归一、无 Reject、`skipGenericPixFmt` 语义等）| ⏳ 与已登记项（`MEMORY.md §6`「legacy 的 4 模式无人守」）**高度重叠** ⇒ 逐条对账后并入，未发现**新的**产品缺陷 |
| **C** 元数据 / ICC / 隐私 | 3 条（`--strip-metadata` + png/apng + exiftool 缺失 ⇒ 静默保留 GPS；carry 策略下 jpg/webp/tiff/gif/jxr 无清理；GainMap+cjpegli 底图 ICC 被覆盖）| ⏳ **待逐条复现**（⚠ 隐私类优先级最高，但**必须实机确认**才写进结论）|
| **D** 出口覆盖与拒绝路径 | 报「alpha 静默丢失」+ 若干 | ✅ 与 §3.4 **互相印证**（两条独立轴都指向同一处）|

⚠ **纪律**：agent 的结论**一律要自己复核**（本次轴 A 的原始表述「引擎主出口丢 alpha」就被我实测**修正**为
「**两条管线、触发条件是色彩变换**」）—— 这正印证了本仓「**信任但核实**」的规矩。

### 3.5 ⚠⚠【新发现 · 已实机复现】**exiftool 不可用时 `--strip-metadata` 静默失效 ⇒ GPS 泄漏**

> ✅ **2026-09-22 已修**：① **UI** —— exiftool 不可用时面板**保持可见**、`MetadataModeCombo` **禁用并强制回「保留」**、
> 5 个 `Strip*Check` 禁用、提示**始终**显示（新增本地化 key）；② **队列（CLI/headless）** —— 剥离开关被请求而工具缺失 ⇒
> **点名**（4 行 ASCII），其中**显式选「删除全部」⇒ 拒绝该项**（`ExitCode=-1`、**不出产物**），仅个别开关为真 ⇒ 点名警告不失败。
> 验证：UI **6 条新断言** + **变异 M13** 闭环；实机 A/B（缺工具 ⇒ `exit=1` + 点名 + 无产物；正常态 ⇒ `exit=0` + 已剥离 + 0 误报）。
> 详见 `HANDOVER §37.1`。

**A/B 实测**（源：带 GPS + 描述的 PNG，`GPS=39 deg 54' 15.12"` / `Description=HELLO`）：

| 场景 | 产物 GPS | 产物描述 | 判定 |
|---|---|---|---|
| 基线：**exiftool 在位** + `--strip-metadata` | **无** | **无** | ✅ 正常剥离 |
| **exiftool 被临时移走** + `--strip-metadata` | **`39 deg 54' 15.12"`** | **`HELLO`** | ⚠⚠ **静默泄漏** |

**证据链**：
- 应用**知道**工具缺失（日志首行 `exiftool: ❌`）；
- 但 ffmpeg 命令行仍是 **`-map_metadata 0`**（`[cmd] ffmpeg … -map_metadata 0 …`）⇒ **源元数据整体复制**；
- 且**没有任何点名**（日志里没有"剥离失败/已跳过"一类提示），任务照常报 **成功**（`exit=0`）。

⇒ **真缺陷（静默隐私承诺失效）**：用户在缺 exiftool 的机器上选「剥离元数据」，**GPS 会原样落盘**而界面无任何提示。
⚠ **与已登记的隐私工作对账**：`verify-metadata-privacy` 门禁**存在**（7 条断言，含 GPS 负控），
但它**依赖 exiftool 在位** ⇒ **覆盖不到本情形** ✓ 这是本条此前没被发现的原因。

**建议修法**：把「剥离开关生效**需要** exiftool」变成**显式前置条件** ——
`MetadataMode.StripAll`（或任一 strip 开关）为真而 exiftool 不可用 ⇒ **要么在入队期就红/拒绝**，
**要么至少把 `-map_metadata 0` 降级为 `-map_metadata -1`**（宁可少带元数据，也不静默带出 GPS），并**点名**。

### 3.6 ✅ 另一条被我的 A/B **推翻**的指控（假阳性）

轴 C 另报「`--color-strategy carry-icc --strip-metadata` 对 jpg/webp/tiff/gif/jxr **无任何清理**」。
**实测**（源：带 GPS **且带 ICC** 的 PNG —— ⚠ 源**无** ICC 时会被**契约拒绝**（`exit 91`，「S2 且源无 ICC ⇒ 不生成 ICC、不改像素」），那是**合法且点名的拒绝**，不是缺陷）：

| 用例 | 产物 GPS | 判定 |
|---|---|---|
| `carry-icc` + `--strip-metadata` → **jpg** | **无** | ✅ 正常剥离 |
| `carry-icc` + `--strip-metadata` → **webp** | **无** | ✅ 正常剥离 |
| `carry-icc` + `--strip-metadata` → **tiff** | **无** | ✅ 正常剥离 |
| （对照）`--strip-metadata` → jpg | **无** | ✅ 正常剥离 |

⇒ **该指控不成立**（静态读码得出的"未清理"结论被实机推翻）✓

---

## 4. 我自己的**方法学更正**（两次假阳性，都已核实）

本次用「定长窗口（160 行）」统计各专用通路的调用，**两次串进下一个方法**，得出两个**错误**结论，随后按**方法真实结尾**（花括号配对）复核并推翻：

| 误判 | 真值 |
|---|---|
| 「`ProcessCjpegliAsync` 里出现了 `GainMapEncoder`」| ❌ 那是**下一个方法的文档注释**（`:2024`）|
| 「`ProcessJxrInputAsync` **不做**任何后置」| ❌ 它在 `:3594` **确实**调 `RestoreMetadataAsync` |

⇒ **纪律**：按方法统计调用**必须用花括号配对定界**，不能用定长窗口（会串到相邻方法）。

---

## 5. 无法仅靠读代码判定的（**需运行验证**）

1. **sBIT 与 cICP 的写入顺序对第三方查看器是否有影响**（规范允许并存，优先级 `cICP=1 > iCCP=2` 需实机核对）。
2. **并发下临时目录/中间产物的隔离**是否覆盖**全部** 9 条专用通路（本次只做了静态检查）。
3. **§3.4 的修法方向**：把 gif 出口的 alpha 补法推广到 png/apng/tiff/webp/avif 时，
   「源是否带 alpha」应从**哪里**取（计划期已知量？入队期？）—— ⚠ 属**产品行为决策**，需确认。
4. **§3.5 的修法选择**：「缺 exiftool 时」是**入队期红/拒绝**，还是**降级为 `-map_metadata -1` + 点名** —— 属产品行为决策。
5. ⏳ **轴 C 的第 3 条未测**：GainMap + `--encoder-backend cjpegli` + PreserveAll + 源带 ICC ⇒ 底图 ICC 是否被覆盖
   （需 GainMap 素材 + 显式后端，本次未构造）。

---

## 6. 与既有登记的对账结论

- **新增 2 条产品缺陷（均已实机复现）**：
  - §3.4 **透明输入 + 色彩变换 ⇒ alpha 静默拍平**（png/tiff/webp；AVIF 已知、GIF 已处理）；
  - §3.5 **exiftool 不可用时 `--strip-metadata` 静默失效 ⇒ GPS 泄漏**（⚠ 既有 `verify-metadata-privacy` 门禁**依赖 exiftool 在位** ⇒ 覆盖不到）。
- **新增 1 条文档缺陷（已修）**：§3.1 —— `PngCicpService` 的「已知缺口」自述过期。
- **1 条指控经实机推翻**：§3.6（carry 策略下"无清理"）—— 假阳性。
- 其余初查疑点（`AttachIcc` 多真源、两处「已知缺口」、两条通路"缺后置"）**经逐条核实后**分别归为
  **设计约定（已登记）** 或 **假阳性**。
- ⚠ 本报告的**覆盖矩阵**（§2）、**两条新缺陷**（§3.4/§3.5）与**待运行验证清单**（§5）是本次的**新增产出**，此前无同类登记。
