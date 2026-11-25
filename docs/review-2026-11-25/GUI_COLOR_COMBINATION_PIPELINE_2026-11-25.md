# HEIC→JXL 组合失败根因与 GUI 色彩组合测试管线重构

日期：2026-11-25

被测产品：`FfmpegGui.dll sha256[:16] = EA8F812E7C50C9FF`

最终组合证据：`tests/output/_tmp/gui-color-combinations/74cc0f34e8604d6f8b6c02f13d334aa4/`

## 1. 结论

这次截图中的失败不是孤立的 HEIC 特判问题，而是旧测试模型与真实产品模型分裂的必然结果：

- 旧测试主要验证单控件取值、手写参数表和精选转换样例，没有验证真实 GUI 控件联动后的组合。
- 输入形态缺少 Tile Grid 派生流组，导致“普通 PNG/YUV 全绿、真实 HEIC 组合在编码器启动前被拒绝”。
- 主命令、cjxl/cjpegli 外部管道和 engine 各有独立命令拼接入口；只修或只测其中一段，会漏掉另一段的 `-vf` / `-filter_complex` 冲突。
- GPU 编码器在后台完成运行时验证，启动期和终态的 GUI 域不同；在启动期采集选项会得到不完整甚至错误的覆盖清单。
- `exit 0`、文件非空和命令字符串正确都不能证明色彩像素正确，必须有独立解码基准。

因此，本次不是再补一个 HEIC 样例，而是把色彩测试改成四层：**活 GUI 域采集 → 离散全集闭包裁决 → 三族实际命令闭包 → 独立像素基准**。

## 2. 截图失败的真实复现与产品根因

原始 HEIC 不在本机可访问路径中，因此用现有生成器派生了主图即为 Tile Grid、无增益图的独立夹具：

- `tests/data/synth_heic_tilegrid_plain.heic`
- 12,371 B；4 个 tile；每块 64×48；组尺寸 128×96；`yuv420p(tv)`。
- 该夹具把 Tile Grid 与增益图特殊路由分离，避免用另一种输入形态掩盖问题。

修改前，该夹具在显式 YUV 输入矩阵下稳定复现：

- exit 91，输出 0 B；
- `COLOR_INPUT_MATRIX_GROUP_UNSUPPORTED`。

修改前证据：`tests/output/_tmp/gui-combination-repro/1be00b60ca9c4d60a91c9b495b1ee05e/`。

产品侧有两层缺陷：

1. `FfmpegCommandBuilder.InputMatrix.cs` 仅凭输入属于派生流组，就把合法 YUV 矩阵判成 `Reject`。实际上 ffmpeg 已把 Tile Grid 合成一个普通 YUV 帧，仍需要矩阵系数转换；不同的是滤镜传输方式，而不是矩阵是否合法。
2. 仅删除预检拒绝仍不够。主链和 legacy→cjxl/cjpegli 管道会再追加一个简单 `-vf`，与派生组已有的复杂滤镜图冲突，ffmpeg 报 `Simple and complex filtering cannot be used together for the same stream.`

## 3. 产品修复

只改三处统一逻辑，没有添加 HEIC/JXL 文件名特判，也没有为门禁放宽产品断言：

- `src/FfmpegGui/Services/FfmpegCommandBuilder.InputMatrix.cs:57-62`
  - 合法 YUV 矩阵在派生流组上同样返回 `Convert`。
- `src/FfmpegGui/Services/FfmpegCommandBuilder.cs:121`
  - 输入矩阵 RGB sink 通过 `EnsureGroupForm()` 与组标签重置接入组感知主链。
- `src/FfmpegGui/Services/QueueProcessor.cs:8032`
  - cjxl/cjpegli 外部管道的首个矩阵滤镜统一走 `FfmpegCommandBuilder.ChainArgs(...)`，不再私写 `-vf`。

## 4. 新测试管线

### 4.1 活 GUI 域，不再维护另一份简化 GUI 模型

`tests/projects/UiHostShared/GuiCombinationCoverage.cs` 直接操作真实 `MainWindow`，调用真实 `CollectOptionsFromUiAsync` 与编码器刷新逻辑：

- 从活窗口采集格式/编码器、目标模板、输入矩阵、range、engine、输出矩阵、chroma、bit-depth、ICC 版本和 lossless 域。
- range 通过产品自己的 `MapColorRangeValue` 从本地化显示值映射到内部 token。
- JPEG 的 `BT.601` / `SMPTE 170M` 在能力缓存中按归一 token 去重，但 GUI 实际选项计数仍保留两项。
- RGB 输出矩阵锁定 chroma=auto 的状态单记为 `UiLockedStates`，不伪装成可执行命令。
- 非 Recommended 且缺 P/T/M 的橙色告警不再被测试静默剪除；记录后仍送真实构建器。
- 先执行与产品相同的 `GpuCapabilityService.DetectAsync()` 和 `ValidateEncodersAsync()`，终态采集完成后才允许生成报告，`GpuDomainFinalized=true` 是硬条件。

终态之所以重要：启动期曾采到 `av1_qsv`，最终稳定域则包含 `mjpeg_qsv`。最终 15 个格式/编码器状态中，JPEG 三态各 23 个目标模板，其余 12 态各 29 个，因此目标模板总数是：

\[
12\times29+3\times23=417
\]

这也解释了早期非终态报告中的 435 为什么不是最终口径。

### 4.2 离散全集闭包裁决

全集轴为：输入形态 × 格式/编码器 × 目标模板 × 输入矩阵 × 策略 × 输出矩阵 × chroma × engine × range × tone-map × gamut-map × bit-depth × ICC 版本 × lossless。

每个决定性单元调用产品自己的输入矩阵、输出矩阵和策略裁决；不参与该裁决的轴按其实时域大小做乘法闭包。最终报告还记录实际决策单元数与 resolver 调用数，避免把乘法闭包误读为逐组合启动转换。

闭包必须严格满足：

\[
N_{all}=N_{planned}+N_{uiLocked}+N_{inputRejected}+N_{outputRejected}
\]

GUI 告警是重叠统计，不重复加到全集。

### 4.3 三族实际命令闭包

全集裁决之外，另对三族组合逐格调用真实 `BuildArguments`，并单独调用 `BuildPipeColorArgs` 覆盖外部编码器私有入口：

- 核心族：输入 × 格式/编码器 × 目标模板 × engine × 输入矩阵 × range × 输出矩阵。
- 策略族：输入 × 格式/编码器 × 目标模板 × tone-map × 策略 × gamut-map。
- 几何族：输入 × 格式/编码器 × chroma × bit-depth × 输出矩阵 × 输入矩阵。

每族都必须满足：

\[
N_{enumerated}=N_{commands}+N_{explicitReject}+N_{uiLocked}
\]

任何新增 `continue`、静默跳过或漏测都会让等式变红。派生组上合法矩阵还必须满足：命令含 `-filter_complex`，且不存在独立简单 `-vf`。

### 4.4 真实像素独立基准

`tests/scripts/_lib-gui-color-combinations.ps1` 执行 36 次真实 JXL 转换，其中 18 个 HEIC 产物逐一与独立 ffmpeg 解码基准比较 MD5。

基准不再按 engine 名称猜测精度，而是按产品日志证明的实际中间精度选择：

- 无 rgb48le 中间件：直接 rgb24 基准；
- 有 rgb48le 中间件：独立 rgb48le→rgb24 基准。

同矩阵、同精度的所有路由必须完全一致，不同矩阵必须产生不同结果，防止“矩阵被忽略也全绿”。

## 5. 最终受管读数

最终证据目录：`74cc0f34e8604d6f8b6c02f13d334aa4`。

### 5.1 离散全集

- 格式/编码器状态：15；目标模板：417；`GpuDomainFinalized=true`。
- 离散全集：101,965,824。
- 可执行计划：78,642,432。
- GUI 锁定：16,174,080。
- 输入矩阵契约拒绝：7,149,312。
- 输出矩阵契约拒绝：0（GUI 已过滤不承载项）。
- 四者相加严格等于 101,965,824。
- GUI 告警：30,233,088，为重叠统计。

### 5.2 实际命令

- 核心族 85,968 + 策略族 23,832 + 几何族 6,576 = 116,376。
- 实际命令 107,278 + 明确契约拒绝 7,586 + GUI 锁定 1,512 = 116,376。
- 外部编码器管道：256 条。
- 告警状态：7,992 条，仍进入真实命令构建。
- 失败：0。

### 5.3 真实转换与像素

- 真实转换：36；独立像素比较：18；失败：0。
- `PASS=54` 仅表示这 36 次转换和 18 次像素比较，不是全部组合断言的替代计数。

六组独立基准全部一致：

| 矩阵 | 实际精度路由 | 路由数 | MD5 |
|---|---|---:|---|
| bt709 | rgb24-direct | 4 | `2b984b6824771365b6c42b972142cdb2` |
| bt709 | rgb48le→rgb24 | 2 | `7320776f15c009cb207225a58524d7e4` |
| bt2020nc | rgb24-direct | 4 | `d244e5c6081419a3b3d60cf036b40b98` |
| bt2020nc | rgb48le→rgb24 | 2 | `b7f2871e1f14f67b3ae68fb01b08d64e` |
| smpte170m | rgb24-direct | 4 | `be293883c11deb3afa1c0263354c8a72` |
| smpte170m | rgb48le→rgb24 | 2 | `806ecec3e26f3ee3154c615d00f37ce2` |

### 5.4 构建与旧门禁回归

- 全解决方案 Release 重建：0 警告、0 错误。
- 最终受管新门禁：PASS，176.6 s；runner 锁、单步超时、STALE、双漂移、留痕均生效。
- `contract`：164/0。
- `verify-color-wiring`：162/0。
- `verify-color-out-matrix`：327/0，含负控。
- `verify-ui-split-reproducible`：19/0。
- `verify-ui-split-union`：11/0，五件合计 520，与旧宿主 520 一致。
- `verify-module-registry`：12/0。
- 未运行 105 条全量基线；本轮是明确选择器的定向受管验证。

## 6. 覆盖边界：不能把色彩全集说成所有 GUI 控件全集

本次完成的是**静态图片色彩相关 GUI 离散选项**的组合闭包，输入形态为普通 RGBA PNG 与主图 Tile Grid HEIC。

以下不在本门禁的统一笛卡尔积内：

- codec 质量、速度、线程、metadata、动画、RAW 等非色彩控件；
- 自由数值输入的全部取值；
- 全部输入文件形态与全部像素内容；
- 对每个离散组合都执行一次外部编码或像素比较。

因此，101,965,824 是色彩离散组合的闭包裁决数，107,278 是三族实际命令数，36/18 是真实执行/像素 oracle 数，三者不可混用。

若要把同一保证扩展到字面意义上的所有 GUI 控件，必须按域复用本次结构：活窗口采域、控件联动归一、拒绝/锁定/告警闭包、所有命令构建入口、独立结果 oracle。不能靠继续向一个手写参数表追加精选样例。

## 7. 受管登记与交付状态

- 新门禁：`tests/scripts/verify-gui-color-combinations.ps1`。
- 判据正文：`tests/scripts/_lib-gui-color-combinations.ps1`，平铺登记，runner 的 `pwsh -File` 入口保持不变。
- 受管清单已登记为第 105 条，超时 1200 s；`_module-registry.json` 与 `docs/TESTING.md` 生成索引已同步。
- RC3 两个既有安装包没有在本轮重打包，仍包含截图缺陷；本报告不构成新版本或新包发布声明。
