# 色彩矩阵完整修复推进计划 · 2026-11-19

> 基线：`b085facabeb4ceece785dc39d49447e2d6ba57a1`。依据：[当前审查](COLOR_MATRIX_PIPELINE_AUDIT.md) 与 2026-11-17 ICC/RAW 修复方案。本文是实施计划，不代表产品代码已修复。

## 0. 目标与不可妥协的验收语义

完整修复不是把日志改绿，而是让以下五层只有一份真值、并能被独立回读证明：

1. 用户意图：显式/自动、输入矩阵、输出矩阵、范围、色度、位深、策略、后端。
2. 源事实：实际像素布局、P/T/M、range、ICC 身份、是否本程序生成的中间件。
3. 解码合同：第一次 YUV→RGB 使用的矩阵和范围；RGB/灰度/调色板/分块流不可误归为 YUV。
4. 变换与出口合同：像素变换、编码器能力、CICP/ICC/range/chroma/bit-depth 是否兑现。
5. 交付结果：像素 oracle、容器回读、任务码、唯一且真实的播报。

每个修复批都必须：旧代码红态证据 → 产品代码修复 → 独立 oracle/回读 → 定向门禁转绿 → 负控仍转红；不以源码字符串或规划对象自证。

## 1. 工作包与依赖

### WP-00：冻结基线与测试夹具（先做，不改产品）

**目的**：把每个问题收敛成最小可辨识格，避免修复后用错 oracle。

- 记录产品/ServiceProbe DLL 指纹；每轮产物使用 `tests/output/_tmp/<GUID>`。
- 建立 4 类夹具：
  - 8-bit YUV 601/709、limited/full；
  - 10-bit AVIF BT.2020 NC full-range；
  - RGB24、gray、pal8、奇数尺寸 RGB；
  - 带 ICC+nclx 的 RGB PNG/AVIF、DNG RAW→linear Rec.2020。
- 独立参考：FFmpeg/zimg 只用于参考链；YUV 范围/矩阵另用公式或原生平面读回；ICC LUT 继续使用独立 LittleCMS/Pillow oracle，产品不得调用 Python。
- 新增/扩展门禁前先保存当前红态，不调整阈值掩盖问题。

**完成条件**：每个 P1 有正控、错误矩阵负控、错误范围负控；所有日志绑定同一 DLL 指纹。

### WP-01：输入解码合同（依赖 WP-00）

**覆盖**：CM-01、CM-02、CM-03，以及 JXR 输入侧、DNG provenance 的入口部分。

**准确定位与改法**：

1. 在 `FfmpegCommandBuilder` 的统一 probe 中按字段名读取 `color_range`，不能按 CSV 位置或 `yuv*` 前缀猜。probe 失败必须是 `Unknown`，不能默认为 TV。
2. 新增 `InputMatrixDirective`/`InputDecodeContract` 单一对象，至少包含：`Required`、规范矩阵 token、可执行 `scale` token、实际 range、像素模型、分块/多流状态、ReasonCode、是否已消费。
3. 分离三类 token：
   - 产物/输入声明的 `-colorspace` token；
   - `scale=in_color_matrix` 接受的 Y′CbCr token；
   - RGB/GBR identity 的像素模型。禁止把 `gbr` 归一为 `rgb` 后送给 scale。
4. 只有真实 Y′CbCr 单流才插入 `scale=in_color_matrix`；RGB、gray、pal8、grouped/tiled、probe unknown 要分别进入“忽略并播报 / 显式拒绝 / auto 回退”策略，不得使用 `!isRgb ⇒ YUV`。
5. H1 `RawColorPipeline` 复用同一 decode contract；顺序固定为“输入矩阵/范围 → format=rgb48le → 尺寸缩放”。不要把 predecode 滤镜塞入 `PixelChains`，以免 JXR/目标色域链被误判。
6. 最长边预缩放必须在输入矩阵消费之后，或把矩阵与尺寸合成同一可证明顺序；对 64×48 YUV、32×24 缩放做 601/709 A/B。
7. JXR 专用 `dec.PixelChains` 之外补齐输入矩阵消费；不能只写 `-colorspace` 声明。
8. RAW 队列在 `captured.InputPath` 改成临时 TIFF 前写入结构化 `ColorSourceProvenance`；`ColorIntentFactory` 只信任 `RawIntermediate/UltraHdrDecoded`，不再以当前扩展名判定自产中间件。

**验收**：
- 满幅 AVIF 正确保持 PC；TV 负控必须不同；
- YUV `gbr` 不再 FFmpeg -22，或明确在规划层 91 拒绝；
- H1/legacy/JXR 矩阵差异与独立参考一致；
- gray/pal8/RGB 只报 no-op，不插 YUV scale；
- DNG 日志不再“source has no ICC/CICP assuming sRGB”，像素与独立 linear Rec.2020 参考一致。

### WP-02：规划与出口能力合同（依赖 WP-01）

**覆盖**：CM-04、CM-05、CM-06，以及 AVIF/GIF/具体编码器边界。

1. `ResolveOutMatrixCapability` 的结果必须和执行器能力绑定：`Carried/Redundant/Rejected/Unapplicable` 不能仅由格式和 token 决定，还要考虑 encoder backend、ICC 专用 avifenc、lossless、chroma、bit-depth、range。
2. AVIF ICC/avifenc：
   - 若 avifenc/lossless 不能承载非 identity M，不能静默把 M 改 0；必须明确拒绝或记录可验证降级，并将最终 CICP/ICC 视为实际结果；
   - `--cicp`、`--range`、`--chroma`、ICC 的承诺必须逐项回读；
   - RGB identity 与 YUV matrix 两种表示不能用同一 `FromPlan(rgbSource)` 无条件覆盖。
3. H1 FFmpeg 出口显式消费 `ColorRange`：AVIF、JPEG、WebP 分别实现具体 encoder 的 range 契约；无法兑现时 91 或清晰降级，不能仅切 pix_fmt。
4. JPEG/WebP “Redundant” 只有在命令、像素格式、range 和解码像素均与 auto 相同才成立；不能因同一矩阵 token 就宣称字节/像素不变。
5. GIF→AVIF 先区分静态与动画、实际 avifenc 版本和 M；历史“固定 BT.601”不能作为当前总规则。先做动态 A/B，再决定拒绝/承载语义。
6. ICC 后置重封装前后必须做 ICC/nclx/alpha/sample-transform/tmap 保真检查；若 ffmpeg remux 会丢 ICC，则绕过该回退或让任务显式警告/失败。

**验收**：AVIF ICC `bt709/rgb`、有损/无损、pc/tv、4:2:0/4:4:4 各有独立矩阵；FFmpeg AVIF/JPEG/WebP 的 pc/tv A/B 读回真实标签和码值；不把硬件自报能力算成通过。

### WP-03：执行收尾与错误契约（依赖 WP-01/02）

1. `QueueProcessor` 的 `outMatrixConsumed` 在 engine 成功分支真实赋值，但必须来自执行结果 `Consumed/Reported/Rejected`，不能只看 `engUsed`。
2. 执行层、规划层、收尾兜底只允许一条矩阵播报；成功承载、冗余、未承载、拒绝互斥，门禁要求恰好一条。
3. cjpegli/cjxl 与普通 FFmpeg 统一任务终局：契约拒绝设置 `item.ExitCode=91`，JSONL `item_completed.code=91`，进程聚合仍按现有 1；零产物保持不变。
4. 引入稳定 ASCII 原因码，如 `COLOR_MATRIX_REJECTED`、`COLOR_MATRIX_NOT_CONSUMED`、`COLOR_ICC_LOST_ON_REMUX`；中文仅作为展示层。
5. 对冲突容器（stream/frame P/T/M 不一致）不要假定唯一规范优先级：先检测并警告/拒绝，禁止静默选择 stream 值覆盖实际 frame 语义。

### WP-04：GUI/CLI/预设接线（可与 WP-03 部分并行，最终依赖 WP-02）

- GUI 预设应用先恢复 backend/encoder，再重建输出矩阵候选，最后应用显式矩阵；候选不足时保留 pending token，切回兼容 backend 后重新应用。
- 区分“旧预设字段缺失”和“新预设显式 auto”；不要以 `null` 同时表达两种语义。
- 完整测试 save→切 backend/format→apply，不只反射 helper；CLI 预设与 GUI 共享同一模型但不共享 UI 时序。
- 动画回退/预览 clone 补齐 `ColorOutMatrix`、`ColorMatrix`、range 等字段，并以实际 `[cmd]` 和最终产物回读验收。
- GUI 下拉只能表示静态格式/backend 能力；输入源冲突、GIF 两步、硬件特殊能力必须在执行前给出动态校验或明确警告。

### WP-05：独立 LUT/CMS 慢路（依赖既有 ICC 方案 WP-01/02，优先级低于矩阵 P1）

- 先完成 ICC 身份三态：Absent、Valid MatrixTrc、Valid LUT/Unsupported、Malformed/Unreadable；合法 LUT 不能再被报成“无 ICC”。
- S1 安全收口：LUT 可携带则原样携带；需要映射但没有 CMS 时明确 91，绝不回退 sRGB。
- S2 完整功能：独立 parser + 3D CLUT/PCS/意图/BPC，LittleCMS/Pillow 参考对撞；不将 CLUT 拟合成 3×3。
- GainMap 是第二入口，必须独立覆盖 carry 与底图映射。

## 2. 建议实施批次

### Batch A：输入合同（最高优先级）

修改范围：`FfmpegCommandBuilder`、`RawColorPipeline`、`QueueProcessor` RAW/JXR 分支、probe/模型。先不动 AVIF 专用出口。验收 CM-01/02/03/07 的 6–10 个格。

### Batch B：AVIF/JPEG/WebP 出口合同

修改范围：`AvifSampleTransform`、`RawColorPipeline` 编码参数、`ImageEncoderArgs`、legacy redundant 分支。验收 CM-04/05/06，并修正过时 A7 判据。

### Batch C：状态码与唯一播报

修改 `QueueProcessor`、cjpegli/cjxl services、JSONL 门禁。验收 CM-08/09；此批不允许因日志改动掩盖 Batch A/B 失败。

### Batch D：GUI/预设与动画回退

修改 `MainWindow.xaml.cs`、preset model/apply、QueueProcessor 回退 clone、UI 定向组。验收完整 save/apply 和 CLI/GUI 一致性。

### Batch E：ICC 安全收口与 CMS

先 S1 携带/拒绝，再 S2 CMS；每批都用独立 profile oracle。不要与 A/B 同批改矩阵判据。

## 5. Batch A 当前实施进度（2026-11-19）

已落地但尚未提交的局部改动：

- `FfmpegOptions` 新增运行时 `ColorSourceProvenance`，并在 `DeepClone()` 中显式复制；`[JsonIgnore]` 防止污染 settings/preset。
- `QueueProcessor` 的 RAW/UltraHDR 中间件替换路径写入 provenance；`ColorIntentFactory` 优先信任 provenance，不再仅以临时 TIFF/PFM 扩展名判断自产中间件。
- `FfmpegCommandBuilder` 的 ffprobe 读取改为 JSON 按字段名读取 `color_range`；输入声明矩阵与可执行 `scale=in_color_matrix` token 分离，`gbr` 不再送入 scale。
- H1 `RawColorPipeline` 已加入显式输入矩阵/实际 range 的预解码滤镜，顺序为矩阵范围 → `format=rgb48le` → 几何缩放。
- `verify-color-token-normalize.ps1` 新增 E5–E12：真实 full/limited YUV、矩阵像素 A/B、gbr 负控、RGB/gray/pal8 no-op。

实际验证：

- `dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release -m:1 --no-restore`：0 警告、0 错误。
- 新增门禁已修正 ffprobe 调用后，E5a/E5b 已通过（真实读回 `pc`/`tv`）；脚本在后续产品格运行时间过长被 lead 停止，尚未取得 E6–E12 完整汇总。
- 未运行全量或 runner；JXR 专用输入矩阵接线、Batch A 的正式清单登记和完整受管超时运行仍待后续批次收口。

本节只记录实施进度，不改变前述工作包依赖和 Definition of Done。

## 3. 当前建议的执行方式

Batch A 已开始实施，当前工作树包含未提交的输入合同、provenance 和定向判据改动。后续应先完成 JXR 专用链与 E6–E12 受管超时验证，再进入 Batch B；共享的 `QueueProcessor` 接线继续串行整合，避免并发覆盖。每个批次结束后只跑 1–3 条受管门禁，确认后再进入下一批。

## 4. 通过标准

- P1 全部有正控/负控，且能证明真实像素和真实容器交付；
- 任务码 91、进程码 1、JSONL 字段语义稳定；
- 任何“计划已承载”都必须有产物回读证据；
- `matrix-full` 的 NUM 参照必须从源独立生成，不能从产物自参照；
- 只跑定向门禁，不跑全量基线，除非用户另行明确要求；
- 每批报告实际命令、产物指纹、门禁结果和未覆盖组合。
