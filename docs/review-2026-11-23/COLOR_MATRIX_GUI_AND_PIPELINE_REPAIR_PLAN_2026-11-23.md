# 色彩矩阵：GUI 可用性与残留缺陷 —— 完整修复计划（2026-11-23）

> 状态：**计划稿，尚未实施任何代码改动，未运行任何测试。**
> 来源：对 `docs/review-2026-11-21/` 三份文档与当前源码的静态阅读（只读）。
> 证据等级：【已确认】= 读到源码/文档原文；【推断】= 由代码结构推出，需用例确认；【报告】= 来自 REPAIR_RESULT 自述，未独立复现。

## 0. 硬约束（用户 2026-11-23 明确）

1. **任何情况下不得运行 104 条全量基线。** 只跑与本次更改对应的定向批次。
2. 不提交、不出包；保留工作树内既有未提交改动。
3. 每个修复项必须带「红 → 绿」证据：先写能复现的定向用例（应红），再修（应绿），并在同一份最终 DLL 上复验。
4. 测试脚本判据不得为了通过而放宽；若判据本身过时，须单列说明并保留原始失败输出（沿用 11-21 task18 的做法）。

## 1. 缺陷清单（本计划覆盖范围）

| ID | 类别 | 缺陷 | 位置 | 证据 |
|---|---|---|---|---|
| G1 | GUI 锁定 | 输出矩阵下拉只按「格式 + 后端」过滤，不看具体编码器。AVIF 选 libsvtav1/qsv/amf/nvenc 时 rgb 档仍显示（注释自认 libsvtav1 无 gbrp） | `ColorStrategyPlanning.cs` ~L1987–1993；`MainWindow.xaml.cs` `RefillOutMatrixComboForFormat` L7424 | 【已确认】过滤维度；【推断】出口层拒绝路径是否完整 |
| G2 | GUI 联动 | 输出矩阵与色度、位深无联动（rgb 档与 4:2:0/4:2:2 不冲突提示） | `MainWindow.xaml.cs` | 【推断】grep 未命中联动代码 |
| G3 | GUI 联动 | 输出矩阵变更不触发高级色彩校验；与手动 CICP / ICC 烘焙的冲突只在队列规划层 exit 91 拒绝 | `MainWindow.xaml.cs` L686–696 | 【已确认】事件行为；冲突场景待验证 |
| G4 | GUI 校验 | 手动 CICP 三元组不一致仅警告，仍按用户选择执行 | `UpdateAdvancedColorValidation` L4707 | 【报告/早读摘要】需复核是否应改为阻断 |
| G5 | 一致性 | 预设/旧任务回放含 NVENC 12 位：GUI 静默回 auto，命令行/队列显式拒绝，两边不一致 | `UpdateChromaBitDepthOptions` L2817–2820 | 【推断】 |
| G6 | 能力 | GUI 的「可用」是静态判断；QSV AV1 旧构建实测失败（-40），最终构建未复测，也无前置拒绝；AMF/VAAPI 未跑 | 报告硬件记录 | 【报告】 |
| G7 | 文档 | `MainWindow.xaml.cs` L2805–2806 注释称其余编码器「仍只到 12」，实际 NVENC/SVT/QSV/AMF 上限为 10 | `ImageEncoderArgs.cs` L899–904 | 【已确认】 |
| G8 | 体验 | 切换格式无提示地重置位深/色彩空间 | `UpdateOptionAvailability` L2609 | 【已确认】 |
| P1 | 管线 | 显式 12 位的拒绝只覆盖 NVENC（`ValidateAvifRequestedBitDepth` L907–916）；libsvtav1/QSV/AMF/VAAPI/未知编码器经命令行或预设请求 12 位时，按我读到的代码会被 `Math.Min` 静默降到 10 | `ImageEncoderArgs.cs` L899–919；`FfmpegCommandBuilder.cs` `ClampAvifBd` | 【推断】**必须先写红用例确认** |
| P2 | 覆盖 | Adobe ICC 夹具缺失；6 项动画 AVIF、B1 的 3 项回读 SKIP；LEGACY 13 格 TRC 差异无像素对拍；matrix-full 仅跑 quick | REPAIR_RESULT | 【报告】 |
| P3 | 可追溯 | 初始工作树 untracked 文件只有哈希，不能全树恢复；D 批次首跑 84 通过/10 红，靠改脚本判据转绿 | REPAIR_RESULT | 【报告】 |

> 更正：先前说「GUI 里显式 12 位会被静默降到 10」不成立（下拉本就不提供 12）；该风险只在命令行/预设路径，已并入 P1/G5。

## 2. 修复设计

### 2.1 单一事实源：编码器能力表（解 G1、G2、G5、G6、P1、G7）
- 新增（或在 `ImageEncoderArgs.cs` 就近扩展）一个 `AvifEncoderCapability`：按 `ClassifyAvifBackend(encoder)` 给出
  `MaxBitDepth`、`SupportedChroma`（420/422/444）、`SupportsRgbGbrp`、`Verified`（是否有实机证据）。
- GUI 下拉、`ValidateAvifRequestedBitDepth`、`ClampAvifBd`、`AvifMaxBitDepthForEncoder` 全部读这张表，禁止再各写一份三元式（11-21 报告已记录过一次这类复制漂移）。
- 初始取值只写**已有证据**的格子：libaom 12；NVENC 10（11-21 实测）；libsvtav1/qsv/amf 10（编码器自报表）；其余 10 且 `Verified=false`。
- `ResolveOutMatrixCapability` 增加编码器参数：AVIF 且编码器不支持 gbrp 时 rgb 档返回 Rejected（G1）。
- `ValidateAvifRequestedBitDepth` 改为：显式请求位深 > 表内上限（且 < 16）一律抛 `COLOR_BIT_DEPTH_REJECTED`，不再只限 NVENC（P1）。**保留 16 位 Sample Transform 的独立判定。**
- 未验证编码器（QSV AV1）：`Verified=false` 时在 GUI 以非阻断提示标注，并在队列端对「显式高级请求」前置拒绝或明确警告，二选一，待用户决定（见 §5 问题 1）。

### 2.2 GUI 联动与锁定（解 G2、G3、G4、G8）
- 输出矩阵变更、编码器变更、色度变更、位深变更、ICC 模式变更、高级色彩开关变更，统一触发一个 `RevalidateColorControls()`：
  1. 重建输出矩阵条目（2.1 的能力表）；
  2. 若当前矩阵为 rgb，则色度下拉仅保留 4:4:4（或把冲突色度标为不可选）；
  3. 若 ICC 模式为烘焙/携带且与所选矩阵冲突，锁定矩阵下拉并显示原因；
  4. 刷新高级校验。
- 锁定一律「置灰 + 悬浮/内联说明原因」，禁止静默回 auto；若必须回 auto，在状态栏/提示行给出一句话（G8、G5）。
- G4：手动 CICP 三元组不一致由「仅警告」改为：警告 + 需用户勾选确认才允许入队（避免静默产出错误色彩）。**行为变更，需用户确认**（§5 问题 2）。
- G5：预设/旧任务回放遇到当前能力表不支持的值，弹出一次性提示列出被改动的字段，与队列端的显式拒绝口径一致。

### 2.3 注释与文档（G7）
- 修正 `MainWindow.xaml.cs` L2805–2806 注释；在 `ImageEncoderArgs.cs` 能力表上方写明各格子的证据来源与日期。

### 2.4 覆盖与可追溯（P2、P3）
- 补夹具或把缺夹具项改为「显式 SKIP 并列出原因与补齐条件」，不得计入通过数。
- LEGACY 13 格：做**方向性**像素对拍（允许已知 BT.709 分段 vs 纯 gamma 的误差上限，沿用 L5 ≤3 码口径），不要求逐码一致。
- matrix-full 只在涉及矩阵条目表变更时跑**受影响格**，不跑完整表。
- 修复前先对本次将改动的文件做哈希快照，写入报告，解决 P3 的归因问题。

## 3. 实施顺序

1. 先写红用例（不改产品代码）：
   - R1 AVIF + libsvtav1 + 显式 12 位 → 期望拒绝（验证 P1）。
   - R2 AVIF + libsvtav1/qsv/amf + 输出矩阵 rgb → 期望下拉不含 rgb / 队列拒绝（G1）。
   - R3 输出矩阵 rgb + 4:2:0 → 期望 GUI 联动不可选（G2）。
   - R4 输出矩阵 + ICC 烘焙并存 → 期望 GUI 层提示/锁定（G3）。
   - R5 预设含 NVENC 12 位回放 → 期望提示与队列口径一致（G5）。
   - R6 下拉位深/色度选项集合 与能力表逐编码器一致（防再漂移）。
2. 确认哪些用例**真的红**；不红的项从计划里删除或降级，并如实记录（P1 尤其如此）。
3. 实现 2.1 → 2.2 → 2.3。
4. 复验：只跑下列**对应批次**，沿用 11-21 的批次命名，具体命令以 `tests/scripts` 现有脚本为准，实施前逐个核对参数（含 11-21 task18 修正过的 `--chroma 4:2:0` 与 ICC 日志判据）：
   - ICC 探针、输出矩阵（受影响格）、决策/交付门禁（`verify-decision-delivery.ps1` 单项，含 ⑯）、快速网格（仅涉及矩阵条目表时）。
   - 硬件：RTX 4060 NVENC 8/10 位回归 + 12 位拒绝；QSV 仅在最终构建上**复测一次**并如实记录结果（AMF/VAAPI 无设备则标「未运行」）。
5. 产出 `COLOR_MATRIX_..._REPAIR_RESULT_2026-11-23.md`：逐项改动、命令、红/绿原始输出、DLL 哈希、SKIP 清单与原因。

## 4. 完成判据（全部满足才算完成）

- R1–R6 在修复前红（或有记录说明为何无需）、修复后绿，且为同一份最终 DLL。
- 产品与测试宿主 0 警告 0 错误。
- 未运行 104 全量基线；报告中明确列出「只跑了哪些批次」。
- GUI 手动点验：AVIF × {libaom, svtav1, nvenc, qsv, amf} × {rgb, 709, 601} × {4:4:4, 4:2:0} × 位深切换，截图留证（目前**没有任何真实 GUI 交互证据**）。
- SKIP/未运行项逐条列出，不计入通过。

## 5. 用户决策（2026-11-23 已确认）

1. **QSV AV1 只做理论实现**：能力表按编码器自报能力填写并标 `Verified=false`，不做实机复测、不新增前置拒绝；GUI 仅以非阻断文字标注「未经实机验证」。QSV 相关验收项记为「未运行（按用户决定仅理论实现）」，不计入通过。
2. **CICP 三元组不一致维持仅警告**：不新增阻断或确认入队（G4 关闭，不改行为）。
3. **切换格式时只对「新格式不允许」的项重置**：新格式仍允许的位深/色彩空间/色度/输出矩阵选择予以保留；仅被新格式（及当前编码器）不允许的项回 auto，并给出一行提示（G8 改为此方案）。

## 6. 本计划的已知局限

- 以上缺陷均来自静态阅读；`UpdateIccPanelVisibility`、`AutoLinkColorParameters` 的细节未逐行核对。
- 目录级 grep 在挂载盘上会超时，部分「未发现联动代码」的结论可能漏检，已在表中标【推断】。
- P1 是否真实存在取决于 R1 是否变红。
