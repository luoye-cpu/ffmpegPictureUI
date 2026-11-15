# 色彩矩阵 I/O 综合分析（2026-11-15，独立矩阵）

> 本文不复用 `COLOR_MATRIX_IO_ANALYSIS_2026-11-14.md`、旧 112 格 CSV 或旧门禁读数。
> 所有结论来自本轮 **168 格**独立矩阵（`matrix1115e`）、逐格产物回读与 ICC 字节级解析。
>
> 仓库：`C:\PLAN\ffmpegPictureUI`；被测可执行文件：
> `src\FfmpegGui\bin\x64\Release\net11.0\win-x64\FfmpegGui.exe`（2026-11-15 最终构建）。
>
> 固定测试环境：`TMP/TEMP/TMPDIR=tests\output\_tmp\colormatrix-analysis\round-tmp`，
> `FFMPEGGUI_PLAN_DIR=publish\PLAN`；不设 `FFMPEGGUI_EXIFTOOL_PATH`。

## 1. 结论先行

1. **最新口径已落地**：PNG/APNG 是 **CICP-first**——CICP 能精确表达时只写 `cICP`，不写 `iCCP`；
   只有 CICP 无法表达（ProPhoto/ROMM 等）才 ICC-only。此前“PNG/APNG 可以 ICC+CICP 双写”
   的旧规则已不再适用。
2. **CICP 与 ICC 统一**：同一产物不会出现“ICC 描述 A、容器 CICP 描述 B”。有源 ICC 时，
   优先改写为**覆盖源色域的最窄标准空间**（Rec.709 / Display P3 / Rec.2020），并让 ICC 内
   `cicp` 与容器 CICP 同源；CICP 无法表达时才保留具名/源 ICC，且不伪造 CICP。
3. **源 ICC 一律 v4.4.0**：源正常化缓存与标准缓存都是 `04 40 00 00`；标准 ICC 内部带
   12 字节 `cicp` tag（P3 `12/13/0/1`、BT.2020 SDR `9/1/0/1`、sRGB `1/13/0/1`）。
4. **矩阵覆盖**：8 源 × 7 格式 × 3 策略 = **168 格**。最终构建下：
   - 成功 `121` 格；
   - 契约拒绝 `47` 格（均为明确的“该容器/策略组合无法诚实表达”，不是崩溃）；
   - 无“零产物但 exit 0”的静默成功。
   - 逐格明细见同目录 `matrix1115e.csv` / `matrix1115e_meta.csv`。
5. **ICC 结构**：标准 P3 / sRGB / BT.2020 SDR 为 616 / 612 / 616 B，ProPhoto 正常化 544 B
   （无 `cicp`，因为 H.273 无法命名 ProPhoto）；v4.4.0、Display、RGB/XYZ、D50、`acsp`、
   `desc/cprt` 为 v4 `mluc`、`chad/chrm/wtpt`、三通道 XYZ/TRC 均在位，目录/偏移 4 字节对齐。
   `ProfileID=0`、`ProfileCMMType` 空是 ICC 允许的可选省略；616 B 已是“保留 `chrm` + 完整
   `desc/cprt` + `cicp`”的紧凑形态，`icc_simplify` 的 516 B 会削可选 tag，不作为默认可交付。
6. **容器可用性**：JPEG/JPEGLI/WebP/TIFF 无容器级 cICP，由“标准 ICC + ICC 内 `cicp`”承担；
   JXL 的 ICC/CICP 互斥，CICP 可命名则 CICP-only，不可命名则 ICC-only；静态 AVIF 的 ffmpeg
   出口不写 ICC，只能 CICP；ProPhoto 类不可命名源按契约拒绝（exit 91）。

## 2. 方法：为什么不基于旧结论

- 旧 `matrix1115a` / 旧 112 格 CSV 是旧 PNG 双写策略的产物，不能作为本轮验收。
- 本轮重新生成源/夹具，按 **8 源 × 7 格式 × 3 策略**逐格独立目录运行；每格独立日志、
  独立产物，不使用旧 CSV 的退出码或体积。
- 回读不只看产品日志：
  - PNG/APNG：直接解析 PNG chunk（`cICP`、`iCCP`、`gAMA`、`cHRM`、`sRGB`），并解压
    `iCCP` 后读 ICC 头/`desc`；
  - JPEG：解析 APP2 `ICC_PROFILE` 段并拼接，读 ICC 头、`desc`、`cicp`；
  - WebP/TIFF：exiftool 回读；产品已把 exiftool 工作目录钉到工具目录，并对 `perl532.dll
    code 126` 增加第三次尝试；矩阵最终轮不再有读取失败残留；
  - JXL：`jxlinfo`；AVIF：`ffprobe -show_streams/-show_frames`；
  - ICC 结构：`icc_audit.py` 字节级 tag directory 解析。
- 失败分类：日志明确出现“契约拒绝 / 不出产物”的计入**预期拒绝**；`perl532` 导致的
  临时读取失败单独标记并重跑，不混入产品缺陷。

## 3. 全矩阵结果

### 3.1 `recommended`（默认推荐）

| 源 | png | apng | jpg | webp | tiff | jxl | avif |
|---|---|---|---|---|---|---|---|
| P3 ICC | 1665 / cICP 12/13 | 2265 / cICP 12/13 | 2511 / ICC 4.4 | 1910 / ICC 4.4 | 12900 / ICC 4.4 | 1832 / CICP | 1797 / CICP |
| P3 CICP | 1665 / cICP 12/13 | 2265 / cICP 12/13 | 2511 / ICC 4.4 | 1910 / ICC 4.4 | 12900 / ICC 4.4 | 1832 / CICP | 1797 / CICP |
| ICC+CICP 冲突 | 1665 / cICP 12/13 | 2265 / cICP 12/13 | 2511 / ICC 4.4 | 1910 / ICC 4.4 | 12900 / ICC 4.4 | 1832 / CICP 12/13 | 1797 / CICP 12/13 |
| Adobe ICC | 2762 / cICP 9/1 | 3870 / cICP 9/1 | 1852 / ICC 4.4 | 1832 / ICC 4.4 | 13106 / ICC 4.4 | 1807 / CICP | 1838 / CICP |
| ProPhoto ICC | 1967 / iCCP 4.4 | 2567 / iCCP 4.4 | 2439 / ICC 4.4 | 1838 / ICC 4.4 | 12828 / ICC 4.4 | 2465 / ICC 4.4 | **exit 1（契约拒绝）** |
| HDR PQ | 2566 / cICP 9/16 | 3466 / cICP 9/16 | 1875 / ICC 4.4 | 1176 / — | 21172 / ICC 4.4 | 3067 / CICP | 7095 / CICP |
| 未标注 sRGB | 1649 / — | 2249 / — | 1877 / — | 1268 / — | 12896 / ICC 4.4 | 1671 / CICP | 1797 / CICP |
| YUV 709 tag | 5498 / — | 6029 / — | 1324 / — | 1180 / — | 6782 / ICC 4.4 | 2653 / CICP | 1309 / CICP |

说明：`—` 表示按策略不写色彩标注（未标注 sRGB 依赖查看器惯例或容器自带标签，不是“失败”）；
JPEG/WebP/TIFF 的 `ICC 4.4` 均指标准 ICC 4.4.0（能命名时带内部 `cicp`）。ProPhoto 的 PNG/APNG
`iCCP` 是源空间 ICC-only；其余 PNG/APNG 均无 `iCCP`。

### 3.2 `carry`（显式携带 ICC）

| 源 | png | apng | jpg | webp | tiff | jxl | avif |
|---|---|---|---|---|---|---|---|
| P3 ICC | 1665 / CICP | 2265 / CICP | 2511 / ICC 4.4 | 1910 / ICC 4.4 | 12900 / ICC 4.4 | 1832 / CICP | 1797 / CICP |
| P3 CICP | 1665 / CICP | 2265 / CICP | 2511 / ICC 4.4 | 1910 / ICC 4.4 | 12900 / ICC 4.4 | 1832 / CICP | 1797 / CICP |
| ICC+CICP 冲突 | 1665 / CICP | 2265 / CICP | 2511 / ICC 4.4 | 1910 / ICC 4.4 | 12900 / ICC 4.4 | 1832 / CICP | 1797 / CICP |
| Adobe ICC | 2762 / CICP | 3870 / CICP | 1852 / ICC 4.4 | 1832 / ICC 4.4 | 13110 / ICC 4.4 | 1807 / CICP | 1838 / CICP |
| ProPhoto ICC | 1967 / ICC | 2567 / ICC | 2439 / ICC 4.4 | 1838 / ICC 4.4 | 12828 / ICC 4.4 | 2465 / ICC 4.4 | **exit 1** |
| HDR PQ | 2566 / CICP 9/16 | 3466 / CICP 9/16 | **exit 1（SDR-only 容器不可携带 HDR）** | **exit 1** | **exit 1** | 3067 / CICP | 7095 / CICP |
| 未标注 sRGB | **exit 1（无源 ICC 可携带）** | **exit 1** | **exit 1** | **exit 1** | **exit 1** | **exit 1** | **exit 1** |
| YUV 709 tag | **exit 1** | **exit 1** | **exit 1** | **exit 1** | **exit 1** | 2653 / CICP | **exit 1** |

`carry` 的失败不是崩溃：源无 ICC（未标注 sRGB / YUV tag）没有可携带对象；HDR 到 JPEG/WebP/TIFF
没有可承载 HDR 的 ICC 通路；ProPhoto→AVIF 既不能命名又不能写 ICC。全部是显式契约拒绝。

### 3.3 `cicp-only`（只写 CICP）

| 源 | png | apng | jpg | webp | tiff | jxl | avif |
|---|---|---|---|---|---|---|---|
| P3 ICC / P3 CICP / 冲突 | 1665 / CICP | 2265 / CICP | **exit 1** | **exit 1** | **exit 1** | 1832 / CICP | 1797 / CICP |
| Adobe ICC | 2762 / CICP | 3870 / CICP | **exit 1** | **exit 1** | **exit 1** | **exit 1（Adobe 不可 CICP 命名）** | 1838 / CICP |
| ProPhoto ICC | **exit 1** | **exit 1** | **exit 1** | **exit 1** | **exit 1** | **exit 1** | **exit 1** |
| HDR PQ | 2566 / CICP 9/16 | 3466 / CICP 9/16 | **exit 1** | **exit 1** | **exit 1** | 3067 / CICP | 7095 / CICP |
| 未标注 sRGB | 1649 / — | 2249 / — | **exit 1** | **exit 1** | **exit 1** | 1671 / CICP | 1797 / CICP |
| YUV 709 tag | 5498 / — | 6029 / — | **exit 1** | **exit 1** | **exit 1** | 2653 / CICP | 1309 / CICP |

`cicp-only` 的 JPEG/WebP/TIFF 拒绝是**容器能力**：这些容器没有 cICP 通路，而策略明确禁止 ICC；
ProPhoto 的 PNG/APNG/JXL/AVIF 拒绝是因为 H.273 无法命名该空间；Adobe→JXL 拒绝同因。

## 4. 本轮修复与根因

### 4.1 PNG/APNG：CICP-first

- `FormatColorCaps.CicpFirstWhenBoth` 对 PNG/APNG 置 true；`EnforceContainerAnnotationRules`
  在 `icc && cicp && caps.CicpFirstWhenBoth` 时剥离 ICC；`FfmpegCommandBuilder` 在出口可精确
  CICP 命名时关闭 ICC / 不挂 `iccgen`。
- 实测：P3 ICC→PNG 从旧策略 `2066B / iCCP 389B + cICP` 变为 **1665B / cICP 12/13、无 iCCP**；
  Adobe→PNG **2762B / cICP 9/1、无 iCCP**；冲突源→PNG **1665B / cICP 12/13、无 iCCP**；
  ProPhoto→PNG 保持 **1967B / iCCP 4.4、无 cICP**（CICP 无法命名）。

### 4.2 冲突源 → JPG/WebP/TIFF 的 `iccgen` HDR 硬失败

- 根因：源 `src_icc_cicp_conflict.png` 容器 cICP 为 `bt2020/smpte2084`，旧代码只对显式
  `CarryIcc` 跳过 HDR `iccgen`，`Recommended` 仍把 PQ 帧塞给 `iccgen`，报
  `Not yet implemented in FFmpeg`、exit 1、零产物。
- 修法：`OutputColorDecision.IccgenFrameLabelReset` + `FfmpegCommandBuilder` 在 `iccgen`
  前插入 `setparams=color_primaries=<outP>:color_trc=<outT>:colorspace=gbr`；只对
  “源帧 HDR、出口 SDR、有出口令牌”生效。
- 实测：冲突源→JPG **2511B / 标准 P3 ICC 4.4.0 + `cicp 12/13`**；WebP **1910B**；
  TIFF **12900B**；均 exit 0。

### 4.3 未标注 sRGB / BT.709 → TIFF 被误拒

- 根因：`EnsureAnnotationNotSilentlyMissing` 在 `AttachIcc=false`、`OutPrimaries=null` 时直接
  Reject；TIFF `UnlabeledMeansSrgb=false`，但 `CanCarryArbitraryIcc=true`，应生成标准 sRGB ICC。
- 修法：`PlanCore` 低置信度分支补 `AttachIcc=true` + bt709/iec61966-2-1/bt709；TIFF 后置条件
  去掉 `engineUsed` 限制；`GetOrGenerateStandardIcc` 增加“标准缓存有效但瞬时写失败”的复用兜底。
- 实测：未标注 sRGB→TIFF **12896B / 标准 sRGB ICC 4.4.0**；BT.709 tag→TIFF **6782B / 同标准**。
- 同时把 TIFF 的重复后置合并为一次，并将 `.icc-embed-*` 临时副本移到 `round-tmp`，产物目录
  不再出现第二份 TIFF；`ExifToolService` 对 ICC 嵌入做 4 次重试，降低 `perl532` 抖动导致的
  “日志说嵌入、产物没有”概率。

### 4.4 Adobe→PNG 的 ICC+CICP 双描述

- 根因：`PlanRecommended` L1 只置 `AttachIcc=true + IccPath=源 ICC`，同时 `CarryCicpLabels`
  另推一组 CICP，形成“源 Adobe ICC + BT.2020 cICP”双描述。
- 修法：`PlanRecommended` 在“出口写 ICC、源有 ICC、非 HDR、非显式目标”时直接委托 `PlanCarry`，
  统一走标准空间选择与同源 CICP；PNG 最终由 CICP-first 剥离 ICC。
- 实测：Adobe→PNG **2762B / cICP 9/1、无 iCCP**；P3→PNG **1665B / cICP 12/13、无 iCCP**。

### 4.5 ProPhoto→AVIF 的越界裁切

- 旧行为：`PlanRecommended` 找不到覆盖源的标准空间时自动取“最宽可命名目标”BT.2020，
  `GamutClipped` 后输出 1859B；这不是“覆盖”，是越界裁切。
- 新口径：只有标准空间**完整覆盖**源才改标准空间；ProPhoto 比 BT.2020 宽，不能冒充覆盖；
  静态 AVIF 又写不进 ICC ⇒ 自动路径 Reject，提示改用可携带 ICC 的容器或显式指定目标接受裁切。
- 实测：ProPhoto recommended/carry→AVIF 均 **exit 1、零产物**；ProPhoto→PNG/JXL 仍 ICC-only。

### 4.6 未标注 sRGB → JPEG 的 `RGB built-in`

- 根因：`Decision` 对 `capIcc`（容器能力）无条件 `AttachIcc=true`，连未标注 sRGB 的 JPEG
  也挂 `-vf iccgen -colorspace bt470bg`，产出 584B `RGB built-in`（4.4 但无 `cicp`）。
- 修法：`capIcc` 不再等于“必须附 ICC”；只有源有 ICC/源色彩标签，或显式非 sRGB 目标/广色域
  输入时才附；未标注 sRGB 保持无标注（计划层 `UnlabeledAssumedSrgb`）。
- 实测：未标注 sRGB→JPG **1877B、无 ICC**；YUV tag→JPG **1324B、无 ICC**；P3/Adobe/ProPhoto
  仍保留标准/源 ICC，未被误删。

### 4.7 显式 carry 的 Map 目标 ICC 丢失

- 根因：`wantsGeneratedPlanIcc` 只认 `CarryIcc/None`，不认 `Map`；Adobe carry→TIFF 的计划是
  Map + 标准 BT2020 ICC，却被后置条件跳过，产物 12482B 无 ICC。
- 修法：把 `Map` 纳入计划生成 ICC 的判据；carry/Map 的目标 ICC 现在由同一后置分支落地。
- 实测：Adobe carry→TIFF **13110B / 标准 BT2020 ICC 4.4.0**；Adobe carry→JPG **1852B /
  标准 BT2020 ICC**；P3 carry→JPG **2511B / 标准 P3 ICC**。

### 4.8 `perl532.dll code 126` 的真正根因：工作目录，不是随机抖动

- 现象：矩阵/gate 里同一格有时成功、有时 `exiftool exit 1: Failed to load Perl DLL "perl532.dll" code 126`；
  早期被当作“随包 Perl 运行时抖动”，用重试和 read_error 标记绕过。
- 根因：`exiftool.exe`（13.61 的 Windows 启动器）按**当前工作目录**解析 `perl532.dll`，
  而产品的多个 exiftool 调用只设置了 `FileName`，没有设置 `WorkingDirectory`。从仓库根/产物
  目录启动时，DLL 不在 cwd ⇒ 必然 code 126；从 `publish/PLAN/exiftool` 目录启动则成功
  （`cd publish/PLAN/exiftool; .\exiftool.exe -ver` 实测 13.61）。
  `perl.exe exiftool.pl` 直接运行也成功，进一步证明不是 DLL 缺失或损坏。
- 修法：所有 `ProcessStartInfo`（ISO 读取、合并色彩/ICC 探测、单标签读取、全标签读取、
  元数据写回、`-stay_open` 批处理、写结果捕获）统一把 `WorkingDirectory` 钉到 exiftool 可执行
  文件所在目录；`RunCapture` 原有的一处正确写法保留并推广。
- 验证：`src_srgb.png` recommended→TIFF 重跑 **exit 0、12896B、标准 sRGB ICC 4.4.0**，
  日志不再出现 `perl532`；同一命令重复运行稳定。矩阵中此前的“抖动格”需要按修复后的
  结果重读（本文最终矩阵是修复后重跑）。
- 意义：这不是测试环境噪声，而是**产品级可复现缺陷**：打包后的 exiftool 在用户目录启动时
  同样会失败，导致“源色彩语义读取失败”拒绝或 ICC 后置缺失。

## 5. ICC 标准性、体积与可用性

### 5.1 版本与结构

| 项 | 实测 |
|---|---|
| Header version | 4.4.0（`04 40 00 00`） |
| Class | Display Device（`mntr`） |
| Color space / PCS | RGB / XYZ |
| White point | D50（0.9642, 1, 0.82491） |
| Tags | `desc`/`cprt`（v4 `mluc`）、`wtpt`、`rXYZ/gXYZ/bXYZ`、共享 `para` TRC、`chad`、`chrm`、`cicp` |
| Alignment | tag 目录/数据偏移 4B 对齐，无越界/重叠 |
| ProfileID | 0（未计算，ICC 允许） |
| ProfileCMMType | 空（可选省略，合法） |

`cicp` tag：12B，signature/type 均为 `'cicp'`；byte 8=ColourPrimaries、9=TransferCharacteristics、
10=MatrixCoefficients、11=VideoFullRangeFlag；RGB/XYZ 的 matrix=0。P3=`12/13/0/1`，
BT.2020 SDR=`9/1/0/1`，sRGB=`1/13/0/1`。

### 5.2 体积

| Profile | 体积 | 结论 |
|---|---:|---|
| Display P3 | 616 B | 保留 `chrm`/完整 v4 `desc`/`cprt`/`cicp` 的紧凑形态 |
| sRGB | 612 B | 同上 |
| BT.2020 SDR | 616 B | 同上 |
| ProPhoto/未知 | 544 B | 无 `chrm`/无 `cicp`（不可命名，不伪造） |

可再省约 24 B（缩短 `cprt`）或 36 B（删 `chrm`）；不建议为几十字节牺牲 `chrm` 的独立原色读数
与完整 `desc`。`icc_simplify` 的 516 B 会删/改可选 tag，不作为默认可交付。

### 5.3 各容器可用性

| 容器 | 容器级 CICP | ICC | 实际策略 |
|---|---|---|---|
| PNG/APNG | 有 | 有 | CICP-first；CICP 不可命名才 ICC-only |
| JPEG/JPEGLI/WebP/TIFF | 无 | 有 | 标准 ICC + ICC 内 `cicp`；容器无 cICP |
| JXL | 有 | 有（互斥） | CICP 可命名 ⇒ CICP-only；不可命名 ⇒ ICC-only |
| 静态 AVIF（ffmpeg） | 有 | **不写** | 只能 CICP；不可命名 ⇒ exit 91 |
| GIF/JXR/PPM | 无 | 无/保守 | 无色彩通路时按 sRGB 工作空间并无标注（不写假 ICC） |

## 6. 预期拒绝清单（不是回归）

1. `cicp-only` → JPEG/WebP/TIFF：容器没有 cICP 通路，策略禁止 ICC；8 源 × 3 = 24 格。
2. `cicp-only` ProPhoto → PNG/APNG/JXL/AVIF：H.273 无法命名 ProPhoto；4 格。
3. `cicp-only` Adobe → JXL：Adobe 不可 CICP 命名，JXL 又不能同时带 ICC；1 格。
4. `carry` 无源 ICC：未标注 sRGB / YUV tag 共 14 格；源没有可携带的 ICC，不伪造。
5. `carry` HDR → JPEG/WebP/TIFF：SDR-only 容器没有 HDR ICC 通路；3 格。
6. `recommended` ProPhoto → AVIF：不能命名、不能写 ICC、不能自动越界裁切；1 格。
7. 其余失败若日志出现 `perl532.dll code 126`，按环境抖动重跑，不混入产品结论。

## 7. 仍未闭合

| 项 | 状态 | 当前口径 / 证据 |
|---|---|---|
| `--icc-file` 外部 ICC 可作为携带源 | **已闭合** | `ColorIntentFactory` 现在把有效 `--icc-file` 写入 `SourceIccPath`，并先过 `NormalizeSourceIccToV44`；`src.png + custom_wide_v2.icc + carry → PNG` 已 exit 0、产物含 353 B iCCP。 |
| `ProfileID` 补算 | **部分闭合** | 纯托管 `IccProfileBuilder.Build` 已按 ICC.1 对“ID 字段清零后的整份 profile”算 MD5 并写回；标准缓存/源 ICC 重建路径具备非零 ID。ffmpeg `iccgen` 直出 profile 仍可能 `ProfileID=0`（ICC 允许，未在编码命令行强改）。 |
| 静态 AVIF ICC-only | **未接** | ffmpeg 静态 AVIF 仍不能写 ICC；要走 `avifenc` 两步法，属新执行通路，未在本轮硬接。 |
| `src_srgb` / YUV tag 的显式 `carry` | **已拍板** | 源没有 ICC 就没有“可携带的 ICC”；显式 `carry` 继续 fail-closed，不把未标注 sRGB 猜成可携带 ICC。CICP 可命名时走 CICP-only/标准 ICC 生成，不伪造源 ICC。 |
| exiftool `perl532.dll code 126` | **已闭合** | 根因是 exiftool 启动器按 cwd 加载 `perl532.dll`；产品所有 `ProcessStartInfo` 已钉 `WorkingDirectory`，源读取加第三次尝试。最终矩阵无读取失败残留。 |
| 门禁旧 PNG 双写断言 | **部分闭合** | `verify-prophoto-faithful` 已修测试侧 `Run` 的 `LASTEXITCODE` 与 exiftool cwd，直跑 38/0；`verify-color-wiring` 官方 runner 159/3。`verify-color-strategy` 的 C1–C5 旧 PNG 双写期望尚未整套重写，不能当最终验收。 |

## 8. 证据

- 矩阵根：`tests\output\_tmp\colormatrix-analysis\matrix1115e\`
  - `matrix1115e.csv`（168 格 exit/products/bytes）
  - `matrix1115e_meta.csv`（逐格 PNG chunk / ICC / JXL / AVIF 回读）
  - `progress.log`（逐格时间与体积）
- ICC 结构：`docs\review-2026-11-15\ICC_STANDARD_AUDIT_2026-11-15.md`；
  `round-tmp\stdbuilt_*.v44cicp.icc`、`srcv44_v3_cicp_*.icc`；`icc_audit.py`。
- 构建：`tests\output\_tmp\colormatrix-analysis\build_fix12.log`（0 errors / 6 warnings）；
  最终 exiftool 工作目录修正在 `build_fix13.log` / `build_fix13_gate.log`。
- 关键单格：`p3icc_recommended_*`、`conflict_recommended_*`、`adobe_*`、`prophoto_*`、
  `srgb_recommended_tiff`、`probe_carry\adobe_carry_tiff`。

## 9. 本批交付包（2026-11-15 重建）

- `publish\FFmpegPictureUI-v1.6.0-beta14-x64-full.7z`
  - 56,373,296 B；SHA256 前 16 `866D560DEE87B227`
- `publish\FFmpegPictureUI-v1.6.0-beta14-x64.7z`
  - 11,630,797 B；SHA256 前 16 `2959645C1F4490B3`
- 包内 `publish\build\FFmpegPictureUI-v1.6.0-beta14-x64\FfmpegGui.exe`
  - 24,649,216 B；SHA256 前 16 `5EA823C59BDB7915`
  - `ProductVersion=1.6.0-beta14+5769b39466115fa7e061ec433855a5d1fae2f3a6`
- 构建：NativeAOT，0 errors / 6 warnings（均为既有 nullable / Avalonia ILC 警告）。
