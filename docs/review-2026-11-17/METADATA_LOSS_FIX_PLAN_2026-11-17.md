# 元数据丢失完整修复方案（EXIF/GPS/XMP + ICC/色彩描述）

- 文档状态：**规划稿（未落地）**。本文件只定义实现方案、代码接缝、验收与出包口径；
  不改产品源码、不提交、不出包、不移动 `1.6.0BETA17` tag。
- 当前基线：`HEAD=a3de42a18a2333c0af4db5e541e62eac770036de`；
  `tag 1.6.0BETA17^{commit}=957cfa8aa8c947e9cad533d085ec84877883ac07`（未移动）。
- 上游诊断：2026-11-17 本会话按 `HEAD=a3de42a` 核对的根因，代码锚点见 §1。
- 修复边界：本轮只做**元数据保真与失败可见性**；不转 beta18、不重指 tag、不扩大成色彩管线全量重构。
- 关键原则：**像素可以继续成功，元数据不能假称成功。** 任何“恢复完成”必须有可回读证据；
  没有证据就只能标“未恢复/降级”，并进入 warning 条目。

---

## 0. 一句话结论

“元数据丢失”不是一条链路，而是两条：

1. **ICC/色彩描述丢失**：`42a0880` 把 exiftool 色彩读取失败从 fail-closed 改成
   `AssumeByConvention`，但 `AssumeByConvention` 的语义是“按惯例继续”，不是“源没有 ICC”。
   下游仍把 `ProbeInputIccDescriptor()==null` 当成 `sourceHasIcc=false`，于是 CarryIcc 不回带源 ICC，
   输出可能只剩自生成 ICC/假定标签。
2. **EXIF/GPS/XMP 丢失**：`RestoreMetadataCoreAsync` 在 `protectColorMetadata=true` 时，
   exiftool 不可用或复制失败会直接 `return`，`RestoreMetadataViaFfmpegAsync` 永远不执行；
   主 ffmpeg 命令又已经在 `strategy != CarryIcc` 时剥掉源元数据，后置恢复成为唯一补回机会。

`a3de42a` 只把失败日志前缀改成 `[warn]`，不是删除者；它让第二条更可见。

---

## 1. 根因矩阵与代码锚点

### 1.1 两类丢失的区分

| 类型 | 用户看到的现象 | 首选嫌疑 | 关键判据 |
|---|---|---|---|
| ICC/色彩描述丢失 | 文件能打开，颜色不对；广色域被当 sRGB；`exiftool -icc_profile` 为空或 ICC 不是源 ICC | `ColorIntentFactory` 的 convention fallback + `IccCarryPolicy` 误判 | 日志含 `exiftool color metadata read failed ... assuming ...` |
| EXIF/GPS/XMP 丢失 | 拍摄时间、机型、GPS、标题、作者等消失；像素正常 | `RestoreMetadataCoreAsync` 在色彩保护模式下跳过 ffmpeg 回退 | 日志含 `[warn] [metadata] exiftool 未检测到...` 且紧跟 `[ffmpeg-meta] 已跳过回退（色彩保护模式）` |
| 两者同时发生 | 颜色不对且拍摄字段也没了 | 同一张图同时命中 A/B | 两条日志都在；常见于 app 包/ exiftool `code 126` |

### 1.2 当前代码锚点（按现文件核对）

| 位置 | 现行为 | 问题 |
|---|---|---|
| `src/FfmpegGui/Services/ColorMapping/ColorIntentFactory.cs:158-177` | `meta.exifReadFailed` 时仍 `src = ColorSpaceIdentify.AssumeByConvention(sourceIsRaw)`，只加 warning | 与 `FfmpegCommandBuilder.ColorMetadata.exifReadFailed` 的注释“调用方不得走 AssumeByConvention”直接冲突；把“读取失败”降格成“源无 ICC” |
| `src/FfmpegGui/Services/FfmpegCommandBuilder.cs:1804-1806` | `exifReadFailed` 文档明确：true 时“无 ICC”不是可信结论 | 代码没有把这个不变量贯彻到 `ColorIntentFactory` / `QueueProcessor` |
| `src/FfmpegGui/Services/QueueProcessor.cs:3049` | `sourceHasIcc = ProbeInputIccDescriptor(...) != null` | `null` 同时表示“源没有 ICC”和“源 ICC 读不出”；CarryIcc 会静默不回带 |
| `src/FfmpegGui/Services/ColorMapping/IccCarryPolicy.cs:44-57` | 入参 `bool sourceHasIcc`，`false` 直接 `Skip` | 没有“不可读 ⇒ 失败”的第三态，无法 fail-closed |
| `src/FfmpegGui/Services/FfmpegCommandBuilder.cs:848-871` | `stripAllMeta = strategy != CarryIcc`；非 CICP `-map_metadata -1`；CICP `-map_metadata 0` + `-map_metadata:s:v -1` | `strategy` 是**声明值**；若生效策略被规划层升格成 CarryIcc，命令行仍按声明值剥掉元数据（T5 口径分歧） |
| `src/FfmpegGui/Services/QueueProcessor.cs:3155-3163` | exiftool 复制失败 + `protectColorMetadata` ⇒ 补 RAW 字段后 `return` | 不跑 ffmpeg 回退；主命令已剥掉的 EXIF/GPS/XMP 无第二次机会 |
| `src/FfmpegGui/Services/QueueProcessor.cs:3180-3193` | exiftool 不可用 ⇒ 先打“使用 ffmpeg 回退”，随后 `protectColorMetadata` 直接 `return` | **日志承诺与实际行为相反**；`RestoreMetadataViaFfmpegAsync` 不可达 |
| `src/FfmpegGui/Services/QueueProcessor.cs:3262-3318` | ffmpeg 回退 = 双输入 `-map_metadata 1 -map_metadata:s:v 1:s:v -c copy` | 会把源全局/流元数据一起带回，可能把源 ICC/色彩标签盖到已转换像素上；不能无验证地用于色彩保护模式 |
| `src/FfmpegGui/Services/ExifToolService.cs:871-947` | `CopyMetadataSafeAsync` 只复制描述性组，排除 ICC/ColorSpace/ColorPrimaries 等 | 方向正确；但返回值只有 `int`，调用方把 `rc==0` 当成功，`0 image files updated` 只打日志 |
| `src/FfmpegGui/Services/ExifToolService.cs:397-410` | 检测到 exiftool 时把 `exiftool_files` 注入 PATH | 只覆盖检测点；写调用路径没有每次确认；`code 126` 仍可能从 `RunCaptureAsync` 直接返回 |
| `src/FfmpegGui/Models/QueueItem.cs:190-205` | `[warn]`/`⚠`/`NOT FOUND` 行会自动 `MarkWarning` | 现有 UI 黄色条目机制可复用；但“元数据未恢复”需要稳定、可断言的原因文本，不能只靠日志前缀 |

### 1.3 这不是“oxipng 删除元数据”

`PngOptimizerService` 只改日志前缀；此前的独立审查已证明 `oxipng -o max` 后 iCCP/cICP 字节不变。
本方案不把 oxipng 列入修复面。

---

## 2. 修复契约：三类元数据分开处理

### 2.1 分类

| 类 | 内容 | 转换后处理 |
|---|---|---|
| **色彩描述** | ICC Profile、CICP primaries/TRC/matrix、cHRM/gAMA、GainMap XMP、ColorSpace 标签 | 由色彩管线**单一真值**决定；不得让源描述覆盖已转换/已映射像素 |
| **描述性元数据** | EXIF 拍摄字段、GPS、XMP（非 GainMap）、IPTC、MakerNotes、版权/作者/标题 | `PreserveAll` 时尽量保留；安全复制必须排除色彩标签 |
| **结构/几何** | Orientation、PixelXDimension/YDimension、Thumbnail、StripOffsets/Tile* | 解码已旋正/已缩放 ⇒ 不得原样回带；按现有排除表继续排除 |

### 2.2 `MetadataMode` 语义

- `PreserveAll`：**描述性元数据尽力保留**；色彩描述始终由色彩管线负责。
  “尽力”不等于“静默成功”：无 exiftool / 复制失败 / 0 项落地 ⇒ `MarkWarning` + 明确“未恢复”。
- `StripAll`：现有剥离逻辑不变；仍按 `StripDescriptiveMetadataAsync` 处理 PNG/APNG/JXL 的隐私残留。
- 不新增 `PreserveAllStrict` 开关；避免 UI/CLI/预设三处扩面。若后续需要“失败即失败”，再单独立项。

### 2.3 决策矩阵（`protectColorMetadata` 下）

| exiftool | 复制结果 | 本方案行为 |
|---|---|---|
| 可用 | `rc=0` 且 `updated>0` | 成功；保留描述性元数据，排除色彩标签 |
| 可用 | `rc=0` 但 `updated==0` | 视为 **Partial**：不回称“恢复完成”；尝试安全回退/警告 |
| 可用 | `rc≠0` / 异常 | 先重试 `code 126`；仍失败 ⇒ 安全回退或 **Failed**，不得直接 `return` 后假装完成 |
| 不可用 | — | **不跑当前的双输入 `-map_metadata` 回退**；按 SKU 口径记 `NotAttempted` + warning；若实现安全回退且可验证，才允许回退 |
| 不可用（app 档） | — | 明确“本 SKU 不随附 exiftool，描述性元数据未恢复”；不写“使用 ffmpeg 回退”这种不实文案 |

---

## 3. 设计

### 3.1 新增单一结果模型

在 `QueueProcessor` 内部新增（不跨程序集暴露）：

```csharp
private enum MetadataRestoreOutcome
{
    Preserved,      // 有证据：描述性元数据已落地
    Partial,        // 部分落地 / 0 项落地 / 仅白名单字段
    NotAttempted,   // exiftool 缺失且无安全回退（app 档）
    Failed          // 尝试过但失败；产物保留像素，元数据未恢复
}

private readonly record struct MetadataRestoreResult(
    MetadataRestoreOutcome Outcome,
    string Reason,
    bool ColorProtected);
```

`QueueProcessor` 只允许在最后统一写日志/标 warning，禁止在各分支各写一套“完成/跳过”。

### 3.2 `RestoreMetadataCoreAsync` 重构

保留 `StripAll` 分支不动（`2867-2921`）。`PreserveAll` 分支改成：

```text
1. 计算 protectColorMetadata（保留现有 userSpecifiedColor / encoderProtectsColor / TIFF 特例）。
2. 若 ExifToolService.IsAvailable：
   a. CopyMetadataSafeAsync（protect=true）或 CopyMetadataAsync（protect=false）。
   b. 返回值改为 MetadataCopyResult（rc + UpdatedCount + NoWriteApplied + Failure）。
   c. rc==0 && updated>0 ⇒ Preserved。
   d. rc==0 && updated==0 ⇒ Partial，进入 3。
   e. rc!=0 / 异常 ⇒ 重试 code 126；仍失败 ⇒ 进入 3。
3. 若允许安全回退（见 §3.3）：
   a. 生成临时文件，执行安全回退。
   b. 回读验证：描述性标签存在、源 ICC/色彩标签未被带回、目标色彩标签仍等于管线裁决。
   c. 验证通过才 TryAtomicReplace；否则保留原产物 + Failed/Partial。
4. 若不允许安全回退：
   a. app 档且 exiftool 缺失 ⇒ NotAttempted + warning。
   b. full 档但 exiftool 失败 ⇒ Failed + warning。
5. 最后统一写：
   - Preserved ⇒ `[exiftool] 元数据恢复完成（已回读 N 项）`
   - Partial/NotAttempted/Failed ⇒ `[warn] [metadata] ...`
```

必须删除的三个早退点：

- `3155-3163` 的 `protectColorMetadata` 直接 `return`；
- `3170-3175` 的异常分支直接 `return`；
- `3188-3192` 的“跳过回退”直接 `return`。

它们改成“记录 outcome → 进入统一回退链/统一收尾”，不再各自终止。

### 3.3 安全 ffmpeg 回退（只在可验证时启用）

**不要直接把现有 `RestoreMetadataViaFfmpegAsync` 打开。** 它现在会把源全局/流元数据整体映射，
包括源 ICC/色彩标签，正好与色彩保护模式冲突。

新增（或给现有方法加 `safeMode`）：

```csharp
private async Task<MetadataRestoreResult> RestoreMetadataViaFfmpegSafeAsync(
    QueueItem item, string outputPath, bool protectColorMetadata)
```

命令骨架仍是双输入 + `-c copy`，但：

- 只映射**描述性**全局/流元数据；对色彩保护模式：
  - `-map_metadata 1`；
  - `-map_metadata:s:v 1:s:v` 仅用于 EXIF/GPS/XMP 所在流；
  - 追加显式清空色彩键（按实际 ffmpeg 版本验证键名）：
    `-metadata color_primaries= -metadata transfer_characteristics= -metadata matrix_coefficients= -metadata color_range=`
    以及 `-metadata:s:v:0` 同组键；ICC 一律不通过 ffmpeg 回带。
- 回读验证必须至少包含：
  - `ExifToolService` 读回描述性标签（Artist/GPS/XMP 等）>0；
  - `ProbeInputColorMetadata` / `ffprobe` 读回色彩标签等于**管线裁决的目标值**，不是源值；
  - 对能读 ICC 的格式，输出 ICC 不是源 ICC（CarryIcc 例外：源 ICC 本来就是应携带值）。
- 验证失败 ⇒ **不替换原产物**，记 `Failed`，保留已完成像素产物。
- 格式白名单：先只对 `jpg/jpeg/png/apng/webp/tiff` 做；`avif/jxl` 走容器原生 carrier 或直接 `Failed`，
  不在本批用 `-c copy` 猜。

如果实现成本超过一个批次：**降级为“只修文案 + 只做 exiftool 安全复制”**，
即 app 档明确 `NotAttempted`，full 档 exiftool 失败明确 `Failed`。这仍然是完整修复，
因为它消除了“假回退/假完成”；安全 ffmpeg 回退作为 P1 可选增强。

### 3.4 ICC 可用性三态

新增（建议放在 `ColorMapping/ColorSpaceDescriptor.cs` 或 `IccCarryPolicy.cs` 旁）：

```csharp
public enum SourceIccStatus
{
    Present,     // 源真有 ICC，且可解析/可携带
    Absent,      // 源确实没有 ICC（可信结论）
    Unreadable   // 读取失败/超时/code 126，不能当作 Absent
}
```

改动点：

- `FfmpegCommandBuilder.ColorMetadata` 增加 `SourceIccStatus iccStatus`，不再只靠 `iccDescriptor==null`。
- `ProbeInputColorMetadataCore`：
  - exiftool 读取成功且 ICC 解析成功 ⇒ `Present`；
  - 读取成功、确认无 ICC ⇒ `Absent`；
  - `exifReadFailed` 或 ICC 解析失败 ⇒ `Unreadable`。
- `ProbeInputIccDescriptor` 保留兼容，但新增 `ProbeInputIccStatus` 供 QueueProcessor 使用。
- `IccCarryPolicy.Decide` 增加第三态：
  - `declared=CarryIcc` + `Unreadable` ⇒ `FailClosed`；
  - `Absent` ⇒ `Skip`（由生成 ICC/命名路径接管）；
  - `Present` ⇒ `CarryIccOnly` / `CarryWithCicp`。
- `QueueProcessor.cs:3049` 的 `sourceHasIcc = ... != null` 改为读 `iccStatus`。
  - `Unreadable` + `effCarry` ⇒ `ExitCode=91`、删除本次新建产物、`Status="失败 (源 ICC 读取失败)"`。
  - `Unreadable` + 非 CarryIcc ⇒ 继续，但 `MarkWarning("源 ICC 读取失败，输出按惯例假定色域，未携带源 ICC")`。
- `FfmpegCommandBuilder.Decision.cs:574` 的 PNG/APNG `carrySourceIccHere` 同步改三态；
  `Unreadable + CarryIcc` 必须在裁决层就拒绝或明确 `AttachIcc=false`，不能先剥掉再寄望后置恢复。
- `ColorIntentFactory`：
  - `meta.exifReadFailed` 且没有外部/自产/标签来源时，`AssumeByConvention` 仍可保留（用户已要求“允许按惯例但提示”），
    但必须把 `src.SourceIccStatus=Unreadable` 传下去，不能只加 warning 后当 `Absent`。
  - `CarryIcc` 的意图装配遇到 `Unreadable` ⇒ 在规划层直接拒绝（或由 `IccCarryPolicy` 的 `FailClosed` 收口），
    不允许生成“假定 sRGB + 回带空 ICC”的计划。

### 3.5 T5：命令行元数据映射按生效策略，不按声明值

`FfmpegCommandBuilder.cs:175` 的 `var strategy = options.ColorStrategy;` 改为：

```csharp
var strategy = EffectiveColorStrategy(options, inputPath);
```

理由：

- `EffectiveColorStrategy` 只对 `CarryIcc` 做规划层覆盖，成本闸已在 `:724` 控制；
- 若规划层把 Recommended 升格成 CarryIcc，命令行必须停止剥源元数据，否则后置恢复失败就丢 ICC；
- 与 `QueueProcessor.cs:3004` 的 `effCarry` 判定同源，避免两套口径。

注意：

- `EffectiveColorStrategy` 内部有 `_inEffectiveStrategy` 防重入；`BuildArguments` 调用它时不要再次触发
  `ColorIntentFactory` 的递归装配。若发现递归，改为由调用方传入 `effectiveStrategy` 参数，不在
  `BuildArguments` 内重复装配。
- 该改动只影响 `-map_metadata` 分支，不应改变像素链；用 `verify-color-wiring` 的
  palette/ICC 断言做负控。

### 3.6 exiftool 健壮性

1. **每次调用前注入 PATH**：`RunCaptureAsync` 入口调用
   `EnsureExifToolDllDirOnPath(_detectedPath!)`；不要只在 `ResolveSafeExifToolPath` 调用一次。
2. **`code 126` 重试**：
   - `ReadColorTagsResultAsync` 已有一次重试；写路径没有。
   - 给 `RunWriteAsync` / `RunWriteCaptureAsync` 增加 `RunWithCode126Retry`：
     首次失败且 stderr 含 `code 126` ⇒ 重新注入 PATH，再跑一次；第二次仍失败 ⇒ 返回明确失败。
   - 为避免改所有调用点，可在 `RunCaptureAsync` 返回 `(Rc, Stdout, Stderr)`，
     由写包装器判断 stderr；读路径继续用现有 `ReadColorTagsResultAsync` 的重试。
3. **复制结果携带 `UpdatedCount`**：
   - `CopyMetadataSafeAsync` / `CopyMetadataAsync` 返回 `MetadataCopyResult`，而不是裸 `int`。
   - `QueueProcessor` 只有 `rc==0 && UpdatedCount>0` 才认 `Preserved`；
     `UpdatedCount==0` ⇒ `Partial`（现有 `ReportCopyOutcome` 只打日志，是 F-13 同族缺口）。
4. **PNG 坏 `eXIf` 复位**：保留 `PngExifResetArg`，继续在 safe copy 里使用。
5. **AVIF/JXL**：不要依赖 exiftool 后置写 ICC/EXIF。按容器能力：
   - AVIF：avifenc 的 `--icc/--exif/--xmp`（若当前版本支持）；
   - JXL：cjxl 的 `--exif/--xmp` / ICC 携带路径；
   - 无原生 carrier ⇒ `Failed`/`NotAttempted`，不静默。

### 3.7 warning 与 UI

现有机制已够用，不新增开关：

- `QueueItem.MarkWarning` + `WarningText` 已能把条目标黄；
- `[warn]`/`⚠` 行会自动 `MarkWarning`（`QueueItem.cs:190-205`）；
- 执行列表已有“仅显示警告条目”勾选（`42a0880` 落地）。

本方案只要求：

1. 元数据结果必须在**统一收尾**显式 `MarkWarning`，不依赖某条日志恰好带 `[warn]`；
2. warning 文本稳定、可机读，建议三种：
   - `metadata restore skipped: exiftool unavailable (app SKU; color protection)`
   - `metadata restore partial: exiftool reported 0 tags written`
   - `metadata restore failed: exiftool exit <n>; descriptive metadata not restored`
   - ICC 类：`source ICC unreadable; output uses assumed color space, source ICC not carried`
3. `item.Command` / 状态列保留“转换完成”，但 warning 文本必须让用户看见“元数据未恢复”；
   不把像素成功改写成失败（除非 CarryIcc 的 fail-closed 分支，明确 `失败 (源 ICC 读取失败)`）。

### 3.8 包档口径

`PACKAGING_SPEC.md §6.1` 已明确：app 档不随附 exiftool，降级行为是“跳过元数据复制，产物不携源 EXIF/XMP，不影响像素”。
因此本方案**默认不改 app 档工具集**，只修“假回退/假完成”：

- app 档 exiftool 缺失 ⇒ `NotAttempted`，日志写“本 SKU 不随附 exiftool，描述性元数据未恢复”；
- 不再写“使用 ffmpeg 回退方案恢复元数据”然后跳过；
- `PACKAGING_SPEC §6.1` 表格的“降级路径”补一句“UI/日志标黄并明确未恢复”。

如果产品决定让 app 档也保真，另立 SKU 决策：随附 `PLAN/exiftool` + `exiftool_files`，
更新 `pack.ps1` 清单、`THIRD-PARTY-NOTICES`、包体/许可审查；不在本方案默认范围内。

---

## 4. 文件级改动清单

| 文件 | 改动 | 对应验收 |
|---|---|---|
| `src/FfmpegGui/Services/QueueProcessor.cs` | 重构 `RestoreMetadataCoreAsync`（2864-3194）；新增 `MetadataRestoreResult`；删除三处早退；`sourceHasIcc` 改三态；统一 warning；`RestoreMetadataViaFfmpegSafeAsync`（P1） | §6 的 M-01～M-08 |
| `src/FfmpegGui/Services/ColorMapping/ColorIntentFactory.cs` | `meta.exifReadFailed` 时保留 warning，但写 `SourceIccStatus=Unreadable`；不得只按 `null` 判定 Absent | M-09、M-10 |
| `src/FfmpegGui/Services/ColorMapping/IccCarryPolicy.cs` | 增加 `SourceIccStatus` 入参与 `FailClosed` outcome；保留 `ShouldCarrySourceIcc` 兼容包装 | M-09 |
| `src/FfmpegGui/Services/ColorMapping/ColorSpaceDescriptor.cs` | 增加 `SourceIccStatus` 字段（或旁路结果类型） | M-09 |
| `src/FfmpegGui/Services/FfmpegCommandBuilder.cs` | `strategy` 改生效策略（T5）；`ColorMetadata` 增加 `iccStatus`；`ProbeInputColorMetadataCore` 填三态 | M-11 |
| `src/FfmpegGui/Services/ExifToolService.cs` | `RunCaptureAsync` 每次注入 PATH；写路径 `code 126` 重试；复制结果带 `UpdatedCount` | M-12、M-13 |
| `src/FfmpegGui/Services/FfmpegCommandBuilder.Decision.cs` | `:574` 的 `ShouldCarrySourceIcc` 改用 `ProbeInputIccStatus`；`Unreadable + CarryIcc` 走拒绝/`AttachIcc=false` 的明确分支，不再用 `Descriptor==null` 猜 Absent | M-09、M-11 |
| `src/FfmpegGui/Models/QueueItem.cs` | 仅复用 `MarkWarning`；如需要，增加 `MetadataRestoreState` 供 UI 绑定（可选） | M-14 |
| `pack.ps1` | 当前 `20→19` 作为临时补丁保留；本批改为**显式清单闸**（见 §7），不再用裸文件数 | M-15 |
| `PACKAGING_SPEC.md` | §6.1 补“未恢复”文案；不改变 app 档工具集 | M-15 |
| `CHANGELOG.md` / `CHANGELOG.en.md` | 记录元数据保真修复；不写“所有格式全保留” | §7 |
| `tests/scripts/verify-metadata-restore.ps1` | 新增门禁（见 §6） | M-01～M-14 |
| `tests/scripts/_run-step3-gates.ps1` | 门禁登记四处：`$scriptList`、`$expectedScriptCount`、散文条数、`Test-RunnerShape` | §6.3 |
| `tests/projects/Tests.Shared/TestGroups.cs` | 若新门禁使用 ServiceProbe mode，补组表 | §6.3 |

---

## 5. 分阶段执行

### P0：止血与真实性（必须先做）

目标：不再出现“日志说回退、实际没回退”和“复制 0 项却报完成”。

1. `RestoreMetadataCoreAsync` 删除三处早退，统一 outcome；
2. `CopyMetadataSafeAsync` / `CopyMetadataAsync` 返回 `UpdatedCount`；
3. app 档 exiftool 缺失改 `NotAttempted` + warning；full 档失败改 `Failed` + warning；
4. 保留 `a3de42a` 的 `[warn]` 前缀，不撤销 warning 机制。

P0 验收：app 包任意格式 + 默认 `PreserveAll` ⇒ 任务仍成功，但日志/条目明确“元数据未恢复”；
full 包 exiftool 正常 ⇒ 描述性元数据保留；`--strip-metadata` 仍剥离。

### P1：ICC 三态与 T5

目标：ICC 不再被“读取失败”误判成“没有 ICC”，CarryIcc 不再静默丢源 ICC。

1. `SourceIccStatus` 三态；
2. `IccCarryPolicy` `FailClosed`；
3. `ColorIntentFactory` 传 `Unreadable`；
4. `QueueProcessor` 的 `sourceHasIcc` 改三态；
5. `FfmpegCommandBuilder` 用 `EffectiveColorStrategy` 决定 `-map_metadata`。

P1 验收：源 ICC 损坏/不可读 + `--color-strategy carry` ⇒ exit 91/90、零产物、日志点名；
`--color-strategy recommended` ⇒ 成功 + warning + 输出不含“源 ICC 已携带”的错误宣称。

### P2：安全回退与容器原生 carrier

目标：在能验证的格式上尽量补回描述性元数据；不能验证的格式明确降级。

1. `RestoreMetadataViaFfmpegSafeAsync`（白名单格式：jpg/jpeg/png/apng/webp/tiff）；
2. 回读验证（描述性标签 >0、色彩标签未被源覆盖、ICC 不串）；
3. AVIF/JXL 走 avifenc/cjxl 原生 carrier，失败即 `Failed`；
4. 若实现超预算，P2 可降级为“只修文案 + 只修 exiftool”，不阻塞 P0/P1 出包。

### P3：打包与文档

1. `pack.ps1` 显式清单闸；
2. `PACKAGING_SPEC` 降级文案；
3. `docs/INDEX` 登记本计划；
4. `CHANGELOG` + 出包说明；
5. 不移动 tag、不转 beta18。

---

## 6. 测试与门禁

### 6.1 夹具

放在 `tests/fixtures/metadata/`（小体量、可跟踪）：

| 夹具 | 内容 |
|---|---|
| `src-exif-gps-xmp.jpg` | EXIF Artist/DateTimeOriginal/Make/Model + GPS + XMP Title |
| `src-p3-icc.png` | Display P3 ICC + iCCP + cICP |
| `src-p3-icc.tiff` | Display P3 ICC + TIFF 描述性字段 |
| `src-no-icc.jpg` | 无 ICC、无 CICP，仅 EXIF |
| `src-broken-icc.png` | 合法 PNG + 截断/损坏 ICC（用于 Unreadable 负控） |
| `src-lut-icc.tif` | LUT ICC（与色彩管线 LUT 计划共用，不要求本批映射） |

### 6.2 用例矩阵

| ID | 场景 | 期望 |
|---|---|---|
| M-01 | full 包 + exiftool 正常 + jpg/png/webp/tiff，`PreserveAll` | EXIF/GPS/XMP 保留；色彩标签由管线决定；不出现“0 项落地仍报完成” |
| M-02 | app 包 + 无 exiftool + 同上 | 任务成功；`MarkWarning` 含“metadata restore skipped ... app SKU”；无“ffmpeg 回退/恢复完成”假文案 |
| M-03 | full 包 + 伪造 exiftool 返回 126 | 写路径重试 1 次；仍失败 ⇒ `Failed` + warning；产物保留像素，不静默完成 |
| M-04 | full 包 + exiftool 返回 0 项落地 | `Partial` + warning；不得 `Preserved` |
| M-05 | 源 ICC 损坏 + `--color-strategy carry` | exit 91/90、零产物、`source ICC unreadable` 日志 |
| M-06 | 源 ICC 损坏 + `--color-strategy recommended` | 成功 + warning + 输出不自称源 ICC；生成 ICC/CICP 合法 |
| M-07 | `--strip-metadata` png/apng/jxl | GPS/XMP/EXIF 全无；cICP/iCCP 不被误删 |
| M-08 | CarryIcc + PNG/AVIF（ICC+CICP 双写） | `IccCarryPolicy` 返回 `CarryWithCicp`；输出 ICC+CICP 都在 |
| M-09 | Recommended 但规划层升格 CarryIcc（L1） | `-map_metadata` 不再剥源 ICC；或后置 exiftool 成功携带；不得零标注 |
| M-10 | GainMap 源带源 ICC/XMP | 底图 ICC 不被源 ICC 覆盖；源 XMP 不混入（`excludeGainMapXmp`） |
| M-11 | 源 ICC 读取失败但 ffprobe 有 CICP | `Unreadable` 不降格成 Absent；走标签路径 + warning |
| M-12 | `code 126` 连续 3 次 | 最终 `Failed`，日志含 stderr 摘要；不得把失败当“源无 ICC” |
| M-13 | 复制成功 `updated=1` | `Preserved`，不回退 |
| M-14 | 仅显示警告条目 | 元数据 warning 条目出现在过滤结果中 |
| M-15 | app/full 包完整性 | app 19 文件清单逐项在位；full 必检 exiftool/ffmpeg/jxl/artifacts；不因 `minFiles` 漂移误判 |

### 6.3 门禁登记

新增 `tests/scripts/verify-metadata-restore.ps1`：

- 直接调用产品 headless + `publish/PLAN/exiftool` 回读；
- app 档用例用包内 exe，full 档用例用 full 包；
- 失败时把 stdout/stderr/回读 JSON 落到 `tests/output/metadata-restore/<run-id>/`。

登记四处（同批，不得漏）：

1. `_run-step3-gates.ps1` 的 `$scriptList` 增加 `verify-metadata-restore.ps1`；
2. `$expectedScriptCount` 104 → 105；
3. runner 里复述条数的散文陈述同步 105；
4. `_probe-test-group-coverage.ps1` / `Tests.Shared/TestGroups.cs`：若使用 ServiceProbe mode，补组表；
   不新增 mode 则只登记脚本。

另：

- `verify-color-wiring` 增加 M-05/M-06 的 ICC 三态断言；
- `verify-decision-delivery` 保留现有 ICC 断言，不因本批改口径；
- `verify-release-package` 增加 app/full 显式清单断言；
- `verify-package-gui-starts` 不因本批改判据。

---

## 7. 打包、版本与出包口径

### 7.1 `pack.ps1`：从“文件数阈值”改为“显式清单”

当前工作树的 `20→19` 是**临时补丁**，不是最终方案：下一次 app 档增删任何一个文件都会再次误判。
改为：

```powershell
$requiredAppFiles = @(
  "FfmpegGui.exe",
  "av_libglesv2.dll",
  "libHarfBuzzSharp.dll",
  "libSkiaSharp.dll",
  "LICENSE",
  "NOTICE",
  "THIRD-PARTY-NOTICES.md",
  "PLAN/artifacts/oxipng.exe",
  "Resources/Locales/en-US.json",
  "Resources/Locales/zh-CN.json",
  # + 9 份 licenses/* 由循环/清单生成
)
```

- 逐项 `Test-Path`，缺一项即抛出；`$minFiles` 仅保留为**次级下限**，不再作为唯一判据。
- full 档同样从 `$minFiles=560` 改为“必需目录 + 必需工具文件”清单：
  `PLAN/ffmpeg-full/ffmpeg.exe`、`PLAN/exiftool/exiftool.exe`、`PLAN/jxl/bin/cjxl.exe`、
  `PLAN/artifacts/oxipng.exe` 等；`560` 只作兜底下限。
- app 档若决定随附 exiftool，清单加 `PLAN/exiftool/exiftool.exe` + `exiftool_files/perl532.dll`，
  并同步 `THIRD-PARTY-NOTICES.md` 与包体评估；默认不在本方案内。

### 7.2 版本与 tag

- 不转 beta18；版本位保持 `1.6.0-beta17`。
- `tag 1.6.0BETA17` 不移动，仍指 `957cfa8`。
- 重出的包 `ProductVersion` 会带新提交哈希；Release 说明必须写：
  “固定 beta17 重出；源提交 `<new-commit>`；内容与 tag `1.6.0BETA17`（`957cfa8`）不同。”
- `AGENTS.md §8.1` 的“重出必升版本 + 新打 tag”与本次裁定冲突：
  按用户已拍板的“不转 beta18、固定 beta17、tag 不移动”执行，并在出包说明中显式登记豁免；
  不回改 `AGENTS.md` 历史，另写更正条目。

---

## 8. 风险与回退

| 风险 | 缓解 |
|---|---|
| 安全 ffmpeg 回退把源 ICC/色彩标签带回，造成“像素已转、标签仍旧” | 只允许白名单格式；回读验证；失败不替换原产物；负控 M-10/M-11 |
| `UpdatedCount==0` 被误判成功 | 返回值带 `UpdatedCount`；`Preserved` 必须有 >0 证据 |
| T5 改 `EffectiveColorStrategy` 引入递归/额外 exiftool 启动 | 复用 `_inEffectiveStrategy` 防重入；成本闸只对 CarryIcc 生效；性能读数记录 |
| app 档 warning 过多，用户以为转换失败 | 状态保持“转换完成”，warning 明确“元数据未恢复”；不删像素产物 |
| exiftool `code 126` 重试导致重复写临时件 | `CleanStaleExifToolTemp` 已有；重试前再清一次；失败保留原产物 |
| 新增门禁导致全量条数/组表漂移 | 四处同批登记；只跑针对性门禁，不跑 `-FullBaseline` |
| 包清单变更误伤旧包 | 只改 `pack.ps1` 的完整性闸；旧包删除按 AGENTS §1“只留最新一档” |

回退策略：

- P0/P1 各成独立提交，出问题可单批 revert（按 §8.6 用 revert，不改写历史）；
- 安全 ffmpeg 回退是 P2 可选增强，失败可关闭而不影响 P0/P1 的真实性；
- 不改版本位和 tag，回退不涉及重出。

---

## 9. 验收清单（全部满足才叫完成）

- [ ] M-01～M-15 全绿，且读数绑定 `FfmpegGui.dll` 指纹（非 AOT 构建）或 exe sha16 + ProductVersion（AOT 包）。
- [ ] app 包默认路径不再出现“使用 ffmpeg 回退”后跳过；条目明确“元数据未恢复”。
- [ ] full 包 exiftool 正常路径：EXIF/GPS/XMP 有回读证据，不把 `0 updated` 报成成功。
- [ ] `--color-strategy carry` + 源 ICC 不可读：exit 91/90、零产物、日志点名。
- [ ] `--color-strategy recommended` + 源 ICC 不可读：成功但 warning，输出不自称源 ICC。
- [ ] `--strip-metadata` 隐私剥离不回归。
- [ ] `verify-metadata-restore` 与登记四处一致；变更链路门禁 rc=0。
- [ ] `pack.ps1` 显式清单闸通过；app 19 文件、full 必检工具在位。
- [ ] `PACKAGING_SPEC §6.1`、`CHANGELOG`、`docs/INDEX` 同批更新。
- [ ] `tag 1.6.0BETA17` 未移动；版本位仍 `1.6.0-beta17`；出包说明写明“重出/源提交/与 tag 不同”。

---

## 10. 需要拍板的两点

1. **app 档是否继续不随附 exiftool**：
   默认按 `PACKAGING_SPEC §6.1` 的既有 SKU 口径“不随附 + 明确降级”；
   若要求 app 档也保留 EXIF/GPS/XMP，则加 exiftool 并更新包清单/许可/体积，属另一条 SKU 决策。
2. **`AGENTS.md §8.1` 豁免**：
   “固定 beta17 重出、tag 不移动”与“重出必升版本 + 新打 tag”冲突；
   用户已口头拍板前者，但需要在出包说明/更正条目里留一句正式豁免。

---

## 11. 本方案不做

- 不转 beta18、不移动 tag、不改版本位；
- 不引入独立 CMS / 外部色彩进程；
- 不重构整个 `RestoreMetadataCoreAsync` 之外的队列架构；
- 不把 app 档改成 full 档工具集（除非 §10.1 拍板）；
- 不重写历史账本；带日期文档不回改，只写更正条目；
- 不承诺 AVIF/JXL 的 EXIF/ICC 全保留；按容器能力给 `Failed`/`NotAttempted`，不静默。

---

## 附录 A：日志断言串（供门禁/人工排查）

```text
exiftool color metadata read failed ... assuming ... (possible misidentification)
[warn] [metadata] exiftool 未检测到...
[ffmpeg-meta] 已跳过回退（色彩保护模式）
[warn] [exiftool] 元数据恢复警告: 退出码 ...
[exiftool] ⚠️ 本次复制 **0 项落地**
source ICC unreadable; output uses assumed color space, source ICC not carried
metadata restore skipped: exiftool unavailable (app SKU; color protection)
metadata restore failed: exiftool exit <n>; descriptive metadata not restored
```

## 附录 B：与已有审查结论的关系

- `42a0880` 的 convention fallback **保留**（用户要求允许按惯例 + 运行中提示），
  但必须把它标成 `Unreadable`，不能当 `Absent`；CarryIcc 遇到它要 fail-closed。
- `a3de42a` 的 warning 前缀 **保留**；它只是可见性修复，不是根因。
- `PACKAGING_SPEC §6.1` 的 app 档“无 exiftool”是既有 SKU 决策，本方案默认不改工具集，
  只修“假回退/假完成”。
- `oxipng` 不进入本方案；已有审查证明它不改写 iCCP/cICP。
