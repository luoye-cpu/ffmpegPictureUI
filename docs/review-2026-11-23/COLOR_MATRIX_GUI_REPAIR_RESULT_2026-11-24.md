# 色彩矩阵与 GUI 修复结果（2026-11-24）

> 结论：本轮识别出的色彩矩阵、GUI 联动、ICC/cICP 交付和测试取证缺陷已修复，并完成同一最终候选上的定向编译与回归。**不能据此宣称“绝对可用”**：真实 Windows 桌面接管未获授权，QSV/AMF/VAAPI 等硬件编码没有实机证据，且按约束未运行 104 条全量基线。

## 1. 最终候选与证据

- 仓库：`C:\PLAN\ffmpegPictureUI`。
- 最终全解决方案构建：`dotnet build ffmpegPictureUI.sln -c Release -m:1 --nologo`，**0 警告 / 0 错误**。
- `FfmpegGui.dll` SHA-256 前 16：`26DD5EDA9066695F`。
- `ServiceProbe.dll` SHA-256 前 16：`C4FB35488BBDA385`。
- UI DLL 前 16：core `E93B9A3D7C14236E`、mode `D4086EE8CBA31DC0`、color `1EB077F65EE0BAAB`、sweep `5E15364183430D82`、cli `C747E0A9565866A5`。
- 证据根：`tests/output/_tmp/color-matrix-targeted-20261124-2fce3c5004dc4d56b9bdbbca4e1155f1/`。
- 最终受管日志：`round3/runner-targeted-round3-12.log`、`round3/runner-targeted-followup-3.log`、`round3/runner-targeted-tonemap-final.log`。
- 未提交、未打包；工作树中其他既有修改保留。

## 2. 本轮完成的产品修复

### 2.1 GUI 联动、预设与提示

- AVIF 输出矩阵按编码器与 `ResolveOutMatrixCapability` 同源过滤；普通 JPEG 不再把 BT.2020 色域误当成原生 PQ/HLG 支持，HDR 仍由 GainMap 通路负责。
- rgb 矩阵锁定不适用的色度选择；位深保持可用。格式、编码器及 auto→auto 异步刷新后，合法选择保留，非法选择回 auto 并明确点名被重置项。
- 修复能力重建时 `Items.Clear()` 触发 SelectionChanged、误把重置提示当成“用户已确认”而清掉的问题。程序化重建与用户手动改选现已分离。
- NVENC 12-bit 预设终端回放确定性回 auto、提示位深变化，不生成 12-bit 命令；测试不再依赖当轮是否探测到实体 NVENC。
- 硬件未验证项仅用于控件测试，不构成 QSV/AMF/VAAPI/NVENC 实机编码通过证据。

### 2.2 色彩曲线与 legacy 通路

- 修复 BT.2020/BT.709 入口被 zimg `tin=bt709` 按纯 γ2.4 反解的问题。默认 `std` 档先按 BT.709-6 定义式反解到线性，再做色度/矩阵；普通显式目标转换及超广色域共用滤镜均已接线。
- `--color-709-curve zimg` 历史兼容档保持旧行为。
- legacy 参照原先与 legacy 产品共用错误的 `tin=bt709`，形成“同错互证”并误判正确 engine。参照已改用 DNG 常数的独立定义式；26 dB 阈值没有放宽。
- SVT 12-bit 共享预检有真实变异证据：屏蔽预检会生成 10-bit AVIF，恢复后同一请求进程退出 1、任务退出 91、零产物；未以静态检查代替缺陷复现。

### 2.3 ICC/cICP 与元数据交付

- PNG 源 ICC carry 时，仅在 ICC 的实际矩阵/TRC 可命名且与既有 cICP 逐项一致时保留双写；失配、非法或重复 cICP 仍剥除，源 ICC 字节不变。
- `PngCicpService` 使用同一字节缓冲复核 cICP，避免两次读取之间的竞态。
- 对可证明没有描述性元数据的 PNG/JPEG，跳过“0 项描述性复制”及无意义 ExifTool 写入；没有隐私清理请求时才跳过，ICC carry、RAW 字段和后续色彩交付仍执行。未知格式、读取失败及隐私清理请求保持保守路径。
- 单张真实 PNG 冒烟转换：RC=0、目录只有 1 个最终产物、`*_exiftool_tmp` 为 0；完整矩阵的临时文件总账也为 0。

### 2.4 取证链修正

- 矩阵产物选择排除 `*_exiftool_tmp`，避免把无标签临时件当成最终 PNG/JXR。
- `color-strategy`、`color-matrix-full`、`color-wiring` 的独立 ExifTool 读回补齐随包 `exiftool_files` PATH；持续读回失败显式转红，不再折叠成“没有 ICC”。
- JXR legacy 旧日志已证明目标 ICC 实际写入并逐字节验证，因此没有误翻能力位或重复添加 ICC 后置。
- 动画 AVIF 改为现场生成 4 帧 GIF 后走产品 GIF→AVIF 通路，不再接受静帧或素材缺失充当动画证据。

## 3. 最终定向验收

以下均为受影响子集，**不是 104 条全量基线**。

| 门禁 | 最终结果 |
|---|---:|
| `verify-color-wiring` | 162 PASS / 0 FAIL |
| `verify-color-strategy` | 87 PASS / 0 FAIL / 3 SKIP |
| `verify-decision-delivery` | 109 PASS / 0 FAIL / 0 SKIP |
| `verify-color-engine-xcheck` | exit 0；跨仓 14/0；2 格明示白名单 |
| `verify-ui-host` | 520 PASS / 0 FAIL；五臂负控均转红 |
| `verify-ui-param-matrix` | 126 PASS / 0 FAIL；负控转红 |
| `verify-ui-split-union` | 11 PASS / 0 FAIL；并集 520/520 |
| `verify-ui-split-reproducible` | 19 PASS / 0 FAIL |
| `verify-probe-mirror-parity` | 93 PASS / 0 FAIL |
| `verify-color-matrix-full` | 29 PASS / 0 FAIL；84 主格 + 48 legacy 格 |
| `verify-color-out-matrix` | 241 PASS / 0 FAIL |
| `verify-module-registry` | 12 PASS / 0 FAIL |
| `verify-color-peak` | 25 PASS / 0 FAIL / 0 SKIP |
| `verify-color-tonemap` | 16 PASS / 0 FAIL；BT.2390 明示 SKIP |

补充证据：

- UI-core 在修复后连续 **5/5 轮 RC=0**，此前可随机复现的 QSV/AMF 候选与 NVENC/N13 提示红已消失。
- 最终 12 项批次的 11 个功能门禁通过，唯一脚本红为 wiring 的 ExifTool PATH；修复后 wiring 复跑 162/0。
- wiring + peak + tonemap 补充批运行器总 RC=0，性能预检该轮 37/0。
- BT.2390 文案更正后的 tonemap 门脚本仍为 16/0、exit 0；同批运行器因性能预检 3.8× 而总 RC=1。保留这一红读数，不用前一轮绿掩盖。

## 4. 未计入通过、保守拒绝与边界

- `color-strategy` 的 3 个 SKIP：legacy 16-bit 合理拒绝、无共同产物可比较，未计 PASS。
- xcheck 的 2 格白名单：PQ→sRGB 且未启用 tone-map 时，高光裁切与 zimg 约定不同。实际 Hable/Mobius、PQ 和 peak 通路已分别由 tonemap 16/0、peak 25/0 验证。
- BT.2390：`tools/std/bt2390-12.txt` 已核对，正文没有可直接实现的 EETF/Hermite 曲线定义。产品对 `bt2390` 显式拒绝，不静默换成其他曲线；标准算子使用已实现的 `bt2446a`。
- 少数 engine JXR 跨色域组合仍是明确、干净拒绝，legacy 同格可成功。这是保守能力边界，不应写成“所有组合均已支持”。
- 预检另有 geometry `r1-s2-shape` SKIP=1（素材不带 CICP，S2 形态不可达）及 selftest SKIP=1（宿主不能定位 Avalonia IAssetLoader）；均不计 PASS。
- 性能预检存在负载敏感读数：同一最终候选曾 37/0，也曾因 3.8× < 4× 得 36/1。阈值未改，不能把性能稳定性标成绝对保证。
- QSV/AMF/VAAPI 没有实机编码证据；其他硬件候选测试也不能替代实际编码与解码回读。
- 真实 Windows GUI 点验未完成：PIP 初始化报 `PIP_REQUIRES_MAIN_SESSION`，主桌面接管随后授权超时。没有截图，不把 UI 自动化称为人工桌面点验。
- 未运行 104 条全量基线，未提交、未出包。

## 5. 主要改动位置

- 产品：`MainWindow.xaml(.cs)`、`Services/ColorSpaceRegistry.cs`、`Services/FfmpegCommandBuilder.Decision.cs`、`Services/PngCicpService.cs`、`Services/QueueProcessor.cs`、中英文资源。
- 测试单体：`UiTestHost/Program.cs`、`ServiceProbe/Parts/16_ProbePlan.cs`；五个 UI 拆分件均由 `_gen-ui-split.ps1` 重新生成。
- 判据/取证：`verify-color-matrix-full.ps1`、`verify-color-strategy.ps1`、`verify-color-wiring.ps1`、`verify-color-tonemap.ps1`、`verify-decision-delivery.ps1`、`verify-color-out-matrix.ps1` 及登记锁表。
- 交接续页：`docs/review-2026-11-23/HANDOFF_2026-11-24.md`。
