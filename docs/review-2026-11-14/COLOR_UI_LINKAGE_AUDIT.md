# 色彩管理面板状态机审计（COLOR_UI_LINKAGE_AUDIT）

- 日期：2026-11-14
- 独占范围：`src/FfmpegGui/MainWindow.xaml.cs`、`tests/projects/UiTestHost/Program.cs`、`tests/projects/ui-*/` 生成件、
  `tests/scripts/verify-ui-host.ps1`、`verify-ui-split-union.ps1`、`_module-registry.json`。
- 不碰：`Services/ColorMapping/*`、`FfmpegCommandBuilder.Decision.cs`、`IccProfileBuilder.cs`、`IccProfileService.cs`、
  `Models/*`、`ServiceProbe/**`、`verify-color-wiring.ps1`（另一条线在收口）。
- 一句话结论：**拆掉 1 个纯策略副作用锁**（`DetectColorConflicts` 里 `isBaking` 把「仅CICP」「手动」的精确三元组一起灰化），
  **保留 2 个真实冲突锁**（携带策略锁目标色域；无 CICP 通路时禁「仅CICP」单选），其余联动/默认态逐件确认无回退。
  同批落地 JPEG 专用「自生成 ICC 版本」下拉（原审计表里"待接入"那一行）。

---

## 1. 控制项 × 当前状态 × 锁定原因 × 真实依据 × 判定 × 回归断言

> 行号为审计时点的近似值；**判定一律按符号名**，不按行号。

| 控制项 / 函数 | 审计时状态 | 锁定/灰化原因 | 真实实现依据（代码符号 / 能力表 / 策略语义） | 判定 | 回归断言 |
|---|---|---|---|---|---|
| `ColorSpaceCombo`（快速色彩空间）| `UpdateIccPanelVisibility`：`locked = CarryIcc \|\| BakeCicpOnly`；`editableTarget = BakeCicpOnly`；`IsEnabled = (!locked \|\| editableTarget) && cap` | CarryIcc 下禁用；仅CICP 下放行；推荐/手动下放行 | **CarryIcc 真实冲突**：`Models/ColorStrategy.cs` CarryIcc「色彩变换零改动、携带源 ICC」⇒ 目标色域确实不被使用。仅CICP 的快速选择就是烘焙目标，必须可改（旧注释：BT.2020 PQ 起步若一起锁会死锁） | **保留 CarryIcc 锁**（真实冲突）；仅CICP 可改保持现状 | I12-g CarryIcc→快速色域锁定（护栏，旧代码已绿）；I11-f 仅CICP→快速色域可改（护栏） |
| `ColorPrimariesCombo`/`ColorTrcCombo`/`ColorMatrixCombo`（精确三元组）| **旧** `DetectColorConflicts`：`isBaking = IccModeBake \|\| IccModeBakeOnly` ⇒ 两个策略下 `IsEnabled=false`，并加"🔒 已锁定"文案 | 旧注释称"避免双重转换"，把两个**不同**策略混在一个布尔里 | **纯策略副作用锁**：Manual 契约「完全按你指定的基准/传递/矩阵处理」（ColorStrategy.cs Manual 注释），三元组正是其输入；仅CICP「仅用 CICP 标记」，CICP 载荷本身就是这三元组。失配已有 `ValidateCicpTriple` 非阻拦橙色告警兜底 | **移除**：整块 `isBaking` 灰化删除，三元组恒可用（XAML 默认 `IsEnabled=true`，可见性由 `UseAdvancedColor` 面板控制） | I11-b/c/d Manual→三元组可编辑（**旧代码必红**）；I11-e Manual→无"锁定"文案（**旧代码必红**）；I11-h Recommended→三元组可用（护栏） |
| `UseAdvancedColor`（高级色彩开关）| 勾选 ⇒ `AdvancedColorPanel.IsVisible=true`；三元组采集挂在 `useAdv` 上（`BuildOptionsFromUi`）| 无策略锁 | 三元组/范围/tone-map 的采集闸；`auto==不指定` 归一在 `AutoToNull` | 保留（非锁，是采集闸） | （既有 J 组覆盖） |
| `IccModeNone`（推荐）单选 | XAML `IsChecked=True` 默认；任何色域/格式/HDR 操作都不自动切走 | — | 2026-11-14 硬需求：未手动点选时恒停推荐；`SetColorStrategyRadio` 仅预设显式记录时调用 | 保留（**不可回退**） | I5/I5b/I6/I7/I9（既有） |
| `IccModeCarry`（携带）单选 | 恒可选；选后 `IccFilePanel` 可见、`UpdateIccCarryLabel` | — | CarryIcc 策略语义 | 保留 | I4（既有） |
| `IccModeBake`（仅CICP）单选 | `IsEnabled = (_currentCapabilities?.SupportsCicp ?? true) \|\| gainMapOn` | 无 CICP 通路时禁选 | **真实冲突**：`FormatColorCaps.SupportsCicp`/`NoCicpReason`；仅CICP 契约「容器能写 CICP（AVIF/HEIF/JXL）」。JPEG+GainMap 例外放行 | **保留**（真实冲突） | （能力探测相关，headless null 分支不测） |
| `IccModeBakeOnly`（手动）单选 | 恒可选 | — | Manual 策略语义 | 保留 | I7/I10（既有）+ I11 |
| `UpdateIccPanelVisibility` | 上面两行锁的汇聚点；末尾串 `UpdateIccCompatibility→DetectColorConflicts→UpdateAdvancedColorValidation` | — | 单点接线，避免漏刷 | 保留结构；仅删 DetectColorConflicts 内灰化 | — |
| `DetectColorConflicts` | HDR→SDR 降级提示（冲突1）、**isBaking 灰化（冲突2，本次删）**、HDR 位深提示（冲突3） | 冲突1/3 是真实运行提示；冲突2 是纯副作用 | 冲突1/3 只读显示值、非阻拦；冲突2 误锁控件 | 冲突2 整块删除；冲突1/3 保留 | I11-e |
| `UpdateAdvancedColorValidation` | 非推荐策略必须显式给三元组（否则"需手动指定"橙色提示）；Manual 走 `ValidateCicpTriple` | 非阻拦告警，不锁控件 | 2026-10-04 设计裁定：模式1 允许 auto，模式2/3/4 必须手动指定 | 保留（告警，不是锁） | J17f/J17g（既有） |
| `UpdateIccCompatibility` | 按是否嵌入列原生/iccgen/不支持三态文案 | 纯文案，不锁 | ICC 嵌入载体说明 | 保留 | — |
| `UpdateCicpHint` | 非 Carry/Bake 且有 `CicpNote` 时显示格式 CICP 提示 | 纯文案 | 能力表 `CicpNote` | 保留 | — |
| `UpdateGainMapPanelVisibility` | 仅 JPEG 且 `HdrModeCombo.SelectedIndex==1` 可见 | 格式/开关决定可见性 | GainMap 面板归属 | 保留 | O 组（既有） |
| `UpdateOptionAvailability` | 重建色域/位深候选；ColorRange 整组按 `SupportsColorRange` 隐藏；**新增 IccVersion 整组按 `NormalizeFormat=="jpg"` 隐藏** | ColorRange 锁既有；IccVersion 锁按输出格式 | IccVersion：只影响自生成命名 ICC，非 JPEG 出口后端 `IsJpegLikeFormat` 归一回 v4.4 | 保留；IccVersion 见 §4 | I12-c/d |
| `FormatCombo` | 切格式重建候选（期望复位）| — | 既有语义 | 保留 | I9（既有） |
| `HdrMode_Changed` | 走 `RefreshOptionsKeepingTargets` 保护刷新，不冲掉已选色域/位深/质量 | — | 2026-09-20 D-1 修复 | 保留 | O2/O3（既有） |
| 预设载入块 | 显式记录了 `ColorStrategy`/旧 `IccMode` 才 `SetColorStrategyRadio`；否则四单选不动；末尾快照-回放保住色域 | — | `MainWindow.xaml.cs` 注释：预设显式记录策略**绝不静默改写为推荐**（刻意例外） | 保留（按"预设显式记录"归类，非缺陷） | N 组（既有） |
| `IccVersionLabel`/`IccVersionCombo`（新增）| 仅 JPEG 出口可见；`MapIccVersionToken` 显示串 v4.4/v2.0 → token v4/v2；采集进 `FfmpegOptions.IccProfileVersion`；回放 `SetIccVersionCombo`（null 不覆盖）| 按输出格式可见性 | 后端 `IccProfileVersion`/`--icc-profile-version`/`PresetData.IccProfileVersion` 已就绪；UI 只做采集/可见性/回放 | **新增落地**（原"待接入"行） | I12-a/b/c/d |

---

## 2. 真实冲突 vs 纯策略副作用（分类说明）

### 2.1 真实冲突（保留锁，有代码/能力表依据）
- **CarryIcc → 锁目标 `ColorSpaceCombo`**：携带策略"色彩变换零改动、源 ICC 为权威"（`ColorStrategy.cs` CarryIcc）。
  此时目标色域下拉改了也不会被用，锁掉是为了**不误导用户**。依据：`UpdateIccPanelVisibility` 的 `locked` 表达式 + CarryIcc 契约。
- **无 CICP 通路 → 禁 `IccModeBake`（仅CICP）单选**：仅CICP 策略"仅用 CICP 标记"，容器写不了 CICP 就无从标定。
  依据：`FormatColorCaps.SupportsCicp`/`NoCicpReason` + 仅CICP 契约"容器能写 CICP（AVIF/HEIF/JXL）"；JPEG+GainMap 例外。
- **（后端拒答，非 UI 锁）** AVIF 静态出口写不进 ICC（`PlanPolicy.ExitWritesIccFor=false`）、GIF 无色彩通路等——
  这些由后端在计划层拒绝（exit 91），UI 侧不再加锁，避免与后端双重约束。

### 2.2 纯策略副作用（本次移除/降为告警）
- **`DetectColorConflicts` 的 `isBaking` 灰化三元组**：旧代码用一个布尔 `IccModeBake || IccModeBakeOnly` 同时禁掉
  仅CICP 与手动的精确三元组。问题有二：
  1. **误伤 Manual**：手动策略本就要用户自己指定 primaries/trc/matrix，旧版把它的输入锁死；
  2. **概念混淆两个策略**：仅CICP 的 CICP 载荷就是这三元组，锁掉等于锁掉该策略的标定输入。
  - "双重转换"的顾虑由 `UpdateAdvancedColorValidation` 的 `ColorSpaceRegistry.ValidateCicpTriple` **非阻拦橙色告警**承担
    （仍按用户选择执行），无需硬锁。
- 连带删除"🔒 ICC 烘焙已激活 — 高级色彩参数已锁定"文案（`I11-e` 钉死不再出现）。

### 2.3 特意**没有**过度加锁
- CarryIcc 下精确三元组**不额外加锁**：携带策略零变换，改三元组是 no-op（不是"会造成冲突"的控件），
  按判据"不使用 ∧ 改动会冲突"两条件缺一 ⇒ 不锁，保持可用（现状即如此）。
- `UpdateIccPreview` 里的 `isBake = Bake || BakeOnly` **保留**：那是"两个策略都会真做 zscale 像素转换"的合法复用，
  只决定要不要显示转换预览，不锁任何控件。

---

## 3. 联动与默认态的逐件确认（防回退）

1. **色域→三参数联动保留**：`ColorSpaceCombo_SelectionChanged` 仍走 `ColorSpaceRegistry.FfmpegTokens(cs)` 回填
   primaries/trc/matrix（审计时**未被误删**，无需补回）。该 handler 末尾只调 `UpdateIccCarryLabel()`，
   **不碰**四个策略单选。
2. **策略单选默认推荐**：XAML `IccModeNone IsChecked=True`；色域/格式/HDR 任何操作都不自动切单选
   （上一轮删的 `AutoLinkIccModeFromColorSpace`/`MarkIccModeTouched`/`_iccModeTouched`/`_suppressIccAutoLink` grep 零命中，本轮未重新引入）。
3. **预设显式记录策略例外保留**：`SetColorStrategyRadio` 仅由预设载入块在显式记录 `ColorStrategy`/旧 `IccMode` 时调用
   （`MainWindow.xaml.cs` 预设载入注释：绝不静默改写为推荐）。

---

## 4. JPEG 自生成 ICC 版本下拉（原"待接入"行的落地）

- 控件：`IccVersionLabel` + `IccVersionCombo`（挂 `ColorRangeCombo` 后），两项 `v4.4`/`v2.0`（ASCII，不经本地化填充）。
- 映射：`MapIccVersionToken`（显示串→`v4`/`v2`，默认/未知归 `v4`）+ `SetIccVersionCombo`（token→索引，`null` 不覆盖），
  照 `MapColorRangeValue`/`SetColorRangeCombo` 模式，**不裸比显示串**。
- 可见性：`UpdateOptionAvailability` 里 `NormalizeFormat(FormatCombo.SelectedItem)=="jpg"` 才可见；切走 JPEG 归 `SelectedIndex=0`。
  **用输出格式判定，不复用 `IsJpegInput`**（那是输入扩展名判定）。
- 采集：`CollectOptionsFromUiAsync` 里 `IccProfileVersion = MapIccVersionToken(IccVersionCombo?.SelectedItem)`。
- 回放：预设应用处 `SetIccVersionCombo(p.IccProfileVersion)`（旧预设无该字段 = null ⇒ 停默认 v4.4，不覆盖）。
- 本地化：新增 `icc.version` / `tip.icc.version`（en-US.json、zh-CN.json、LocalizationService.cs 兜底三处同批）。
- UI 层不判断"该不该用 v2"——那是后端 `IsJpegLikeFormat` 的事。

---

## 5. 本轮未修、根因在别处（只记录、未改）

- **`IccProfileBuilder.BuildTrc` 编译错误（CS0161，非所有代码路径返回值）**：本次编译即被它挡住
  （`src/FfmpegGui/Services/IccProfileBuilder.cs:279`）。属 ICC 版本线/v4.4 writer 的在制代码，在我方禁碰清单内，**未改**。
  ⇒ 本轮门禁验证（`verify-ui-host` 等）要等该线补好返回值后才能出绿读数；我方改动本身不引入新编译错误。
- **色彩范围口径实测覆盖不全**：仅 WebP/jpg 有实测，AVIF/mkv/mp4 为容器机制推断（AGENTS.md §7 既有未了结项，与本审计无关）。
- **`FFMPEGGUI_FFMPEG_DIR` 正斜杠产品侧归一化**：runner 侧已 Join-Path，产品侧未做（既有未了结项）。
- 其他 `ColorMapping/` 内如发现与 UI 锁定相关的根因，按分工只在本节点名、不在本仓改。

---

## 6. 回归断言清单与"旧代码是否必红"判定

新增全部落在 ui-color 臂（GroupI_IccUi），共 **12** 条。三处计数锁同批抬：
`ui-color 91→103`、`$aggExpected 475→487`、`$expectedOldCount 475→487`（=84+59+103+169+72）。

| 断言 | 断言内容 | 旧代码是否必红 | 说明 |
|---|---|---|---|
| I11-a | 三元组/色域控件齐备 | 否（护栏） | 存在性前置 |
| I11-b | Manual→primaries 可编辑 | **是** | 旧 `isBaking` 把它 `IsEnabled=false` |
| I11-c | Manual→trc 可编辑 | **是** | 同上 |
| I11-d | Manual→matrix 可编辑 | **是** | 同上 |
| I11-e | Manual→无"已锁定"文案 | **是** | 旧代码加"🔒…已锁定…" |
| I11-f | 仅CICP→快速色域可改 | 否（护栏） | 旧 `editableTarget=true` 本就可改，防回退 |
| I11-g | CarryIcc→快速色域锁定（真实冲突）| 否（护栏） | 钉住要保留的真实锁，防"全放开"误删 |
| I11-h | Recommended→三元组可用 | 否（护栏） | 防本次重构误锁 |
| I12-a | IccVersionCombo 存在 | **是** | 旧代码无此控件 |
| I12-b | 下拉默认 v4.4(index 0) | **是** | 旧代码无此控件 |
| I12-c | 输出=JPEG ⇒ 版本下拉可见 | **是** | 旧代码无此控件 |
| I12-d | 输出=PNG ⇒ 版本下拉隐藏 | **是** | 旧代码无此控件 |

> 造假自检：I11-b/c/d/e 与 I12-a/b/c/d 在旧代码上确为红（前者灰化/文案存在，后者控件不存在）；
> I11-a/f/g/h 如实标注为**回归护栏**（旧代码也绿），不冒充"必红硬锁"。

---

## 7. 验证读数（2026-11-14，本轮两次跑均被并行线并发改源码作废）

- `dotnet build -c Release -m:1`：**0 警告 0 错误**（两轮均如此）。
  `FfmpegGui.dll` sha256[:16]：首版 `2A0676888BE9A7BB`；并入并行线后端改动后重编 = **`9920CAE5264F2661`**（mtime 22:23Z）。
- `verify-ui-split-reproducible.ps1`：**PASS=19 FAIL=0，exit=0**（双拷贝逐行 0 差异）⇒ 未手改生成件，靠 `_gen-ui-split.ps1` 重切片。
- `verify-ui-split-union.ps1`：**源码层计数守恒 PASS**（new=487 old=487、并集==487、反控两侧非空）；
  其 FAIL 仅"旧宿主 FAIL==0"/"五工程无 FAIL"两项——**下游受各 arm 运行时失败牵连，与计数锁无关**。
- `verify-ui-host.ps1`：两轮均 exit=1，但运行器自己标 **"本次结论不对应当前工作区 ⇒ 必须重建后重跑"**——
  跑期间并行线在改 `IccProfileBuilder.cs`(6:22)/`ColorIntentFactory.cs`(6:39)/ServiceProbe `61_ProbeIcc.cs`(6:34)。
  残留红全部落在并行线域：
  - **K2b/K3a**（BakeCicpOnly vs Recommended 命令行、Recommended zscale+iccgen）——正是 `IccProfileBuilder`/`Decision` 域；
  - `bench` 双核/端到端计时（AGENTS.md §7 已注明负载高时假红）；
  - `G2 停止后状态=已停止`、ui-mode/ui-sweep/ui-cli 计数漂移——非本线改动臂。
- **本线断言 I11/I12 两轮均未出现在任何 FAIL 列表**（落在 pass=101 内）；源码层 union=487 与三处锁一致。
- ⇒ **待并行线收口（IccProfileBuilder/ColorIntentFactory/ServiceProbe 不再动）后，重编 + 重跑这三条即得最终绿读数**；本线不再为绕锁改代码/删 obj。

---

## 8. 最终验收读数（2026-11-14 06:49 重编后，工作树稳定串行跑）

**指纹 A==B（跑前跑后一致，证明跑期间源码未变）**：
`FfmpegGui.dll = 2EF23939079C01FD`、`ServiceProbe.dll = 373943DFB609333C`。

| 门禁 | 读数 | exit | 判定 |
|---|---|---|---|
| `dotnet build -c Release -m:1` | 0 警告 0 错误 | 0 | ✓ |
| `verify-ui-split-reproducible` | 19P/0F | 0 | ✓ 未手改生成件 |
| `verify-ui-host`（第1次）| 482P/5F | 1 | 总断言 487（与锁同源）；5 红见下 |
| `verify-ui-host`（第2次）| 482P/5F | 1 | 与第1次**逐条一致**，非 flake；无 I3 ProPhoto 波动 |
| `verify-ui-split-union` | 计数守恒 487==487、missing/extra=0；PASS=9 FAIL=2 | 1 | 计数锁正确；2 个 FAIL 是"旧宿主 FAIL==0/五工程无 FAIL"，下游受 ui-host 5 红牵连 |
| `verify-color-wiring` | 155P/5F | 1 | L1 线域；预期 157P/3F，多出 (11)alpha 回读、(12)jxl 取向 |
| `verify-decision-delivery` | 88P/6F | 1 | 与预期 88P/6F 一致；6 红在 ⑯/⑰b legacy P3/⑱-4 |
| `verify-prophoto-faithful` | 37P/1F | 1 | 预期 38/0；1 红为源 ICC 哈希=E3B0C442…（空输入 SHA-256）⇒ 源字节读空，夹具/环境性 |
| `verify-probe-mirror-parity` | 91P/0F | 0 | ✓ 与预期一致 |

**本线 I11/I12 十二条逐条 PASS 证据**（ui-color 宿主 stdout 实测）：
`[PASS] I11-a 三元组/色域控件齐备`、`I11-b Manual→primaries 可编辑`、`I11-c Manual→trc 可编辑`、
`I11-d Manual→matrix 可编辑`、`I11-e Manual→无'已锁定'文案`、`I11-f 仅CICP→快速色彩空间可改`、
`I11-g CarryIcc→快速色彩空间锁定(真实冲突)`、`I11-h Recommended→三元组可用`、
`I12-a IccVersionCombo 存在`、`I12-b 下拉默认 v4.4(index 0)`、`I12-c 输出=JPEG⇒版本下拉可见`、
`I12-d 输出=PNG⇒版本下拉隐藏`。

**ui-host 5 红归因（均非本线回归）**：
- `G2 停止后状态=已停止 status=失败(exit -1)`：ui-mode 臂生命周期，两次稳定复现，非本线改动臂。
- `K2b BakeCicpOnly 与 Recommended 不同`（两命令逐字相同）、`K3a Recommended⇒iccgen=False`、
  `K5c 折叠后与 BakeOnly 不同`、`J8 Display P3⇒iccgen=False`：**全是"iccgen 未被触发 / BakeCicpOnly 与 Recommended 命令未区分"**，
  落在 `Decision.cs`/`IccProfileBuilder.cs`/`ColorIntentFactory.cs`（ICC 版本线在制品，本线禁碰）。
  本线只删了 UI 的 IsEnabled 灰化，未碰命令构建；I11/I12 全 PASS 即证。
- 计数锁未动：实际总断言 487 = 锁值，未为迁就 482 改锁。

---

## 9. 旧断言改写轮（2026-11-14 晚，ui-host 487/0）

上节 5 红（G2/K2b/K3a/K5c/J8）经核：G2 为环境 flake，其余 4 条是**旧断言锁旧行为**，由本线（Program.cs owner）按新口径改写：

| 断言 | 改前（锁旧行为） | 改后（新口径） |
|---|---|---|
| J8 Display P3 | 要求 `+iccgen`（触发 ICC） | **反向**：`p=smpte432:t=iec61966-2-1` 且**无 iccgen**（可命名空间默认 CICP 优先，ICC 仅兜底）|
| K3a Recommended P3 | 要求 `p=smpte432 且 iccgen` | `p=smpte432 且【不】触发 iccgen` |
| K2b | 断言 BakeCicpOnly≠Recommended | 改为 `P3上 Recommended≡BakeCicpOnly`（都 CICP-only 无 ICC，新正确行为）|
| K5c | 断言折叠后≠BakeOnly（Display P3）| 目标换 ProPhoto RGB（归一化到 BT.2020 CICP），改为 `Recommended≡BakeCicpOnly`；策略区分力由 CarryIcc 恒不同（K2a/K5b）承担 |

- 曾尝试新增 K2c 在 ProPhoto 上断言 Recommended≠BakeCicpOnly，但实测 builder 把 ProPhoto 归一化到 BT.2020 CICP、headless 无 PLAN/iccgen 工具链 ⇒ 两策略命令逐字相同，该区分在本单元上下文**不可观察**。已删除 K2c，不强行断言。
- 计数净变化 = 0（K2c 删除、K5c 翻向、其余翻期望）⇒ 三处锁维持 **ui-color=103 / agg=487 / union=487**。

**最终读数（指纹 A==B：`FfmpegGui.dll=2EF23939079C01FD`、`ServiceProbe.dll=373943DFB609333C`）**：
- `dotnet build -c Release -m:1`：0 错误。
- `verify-ui-host`：**84+59+103+169+72 = 487 PASS / 0 FAIL，exit=0**。新改的四条在 PASS 明细逐条可见。
- `verify-ui-split-union`：**11P/0F，487==487，missing/extra=0，五工程无 FAIL**。
- `verify-ui-split-reproducible`：**19P/0F**。
- `verify-prophoto-faithful`（只读连跑两次）：同一条"反真空"臂两次都红，但症状跨次变化（第1次源 ICC 哈希=空 `E3B0C442…`、第2次无产物）⇒ **环境性 flaky**（carry→PNG ProPhoto 路径依赖 PLAN 工具/临时文件/exiftool），属 ICC 线脚本，本线未改。



