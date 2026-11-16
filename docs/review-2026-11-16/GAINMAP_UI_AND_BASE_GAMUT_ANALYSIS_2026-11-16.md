# Gain Map UI 与底图色域分析（2026-11-16）

**范围**：AVIF Gain Map 主面板显隐、Gain Map 能力三处真值、Rec.2020 底图是否把颜色压到窄色域，以及 sRGB / P3 / Rec.2020 / ProPhoto / 源自带 SDR 底图 / 两图合成等输入下的实际表现。

**基线**：工作树 `HEAD=6593af8`，`main` ahead 8；本轮未修改源码，未提交。`MainWindow.xaml.cs`、`RawColorPipeline.cs`、`QueueProcessor.cs`、`UiTestHost/Program.cs`、`verify-color-strategy.ps1`、`verify-gainmap-isobmff.ps1` 仍有未提交修改，本文读的是**当前工作树**，不是已推送的 beta17 包。运行时探针使用 `src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe`（2026-11-16 17:33 构建），不等于 beta17 包内 13:52 的旧 exe。

---

## 1. 结论

1. **AVIF Gain Map 不是“后端不可用”**。`GainMapEncoder.FormatCarriesGainMap`、`ColorTransformPlan` 的 AVIF caps、`QueueProcessor` 专用分派都支持 AVIF；CLI 对 HDR 输入产出了带 `tmap` 的 AVIF（宽底与 sRGB 底都可）。
2. **问题在 UI 暴露**：AVIF 下 `HdrModeCombo` 可见、可以选 “GainMap”，但 `JpegGainMapPanel` 被三处 `jpg/jpeg` 条件同时隐藏；`BuildOptions` 仍会把隐藏面板里的默认值（sRGB 底、灰度、ISO、跟随主图、1000 nits、half 降采样）读进任务。用户看到的是“有 GainMap 开关、没有 JPEG 同款设置”。
3. **三处能力真值不一致**：实际分派/规划真值（`GainMapEncoder.FormatCarriesGainMap` / `ColorTransformPlan.Caps`）AVIF=true；`FormatCapabilitiesService` 的 AVIF 条目 `SupportsGainMap` 却是 false，且源码中没有读取点（当前只有 `ThumbnailService` 消费 `GetCapabilities`），属于**第三份 dead data**，不是运行时闸门，但会误导后续 UI/测试。
4. **Rec.2020 底图没有把 HDR 输入压到窄色域**：HDR sRGB/P3/Rec.2020 六格全部产出 `tmap`；Rec.2020 底图 AVIF 标签为 `bt2020/bt709/bt2020nc`，JPEG 底图 ICC 为 `bt2020/sRGB`。SDR 像素矩阵中 Rec.2020→Rec.2020 基本恒等（平均绝对差 4.65/255），Rec.2020→sRGB 才发生预期裁切。观察到的那条 `GamutClipped ... SDR(bt709)` 是**规划层目标口径**，不是底图像素的实际结果。
5. **但有两个真实颜色问题**：① SDR 输入走 AVIF GainMap 时，`headroom ≤ 1` 短路成普通 AVIF，底图只写了 `color_space`，`color_primaries/transfer` 为 unknown；若底图选 Rec.2020，查看器可能按 sRGB 解释，表现为“变窄/变灰”。② AVIF 宽底图的 CICP 把 `trc` 写成 `bt709`（1），但 `BuildBaseBgra8` 实际用的是 sRGB 曲线；JPEG 同场景 ICC 写的是 sRGB。两者口径不一致，属标签/曲线错配，不是色域压缩。

---

## 2. UI 真值表

| 格式 | `HdrModeCombo` | `JpegGainMapPanel` | 实际可配置项 | 后端 GainMap |
|---|---|---|---|---|
| JPEG / JPEGLI | 可见（`fmt is jpg/jpeg`） | 可见（选 GainMap 时） | 类型、metadata、底图色域、质量、nits、降采样 | APP2/XMP/MPF，支持 |
| AVIF | **可见**（`fmt is ... or avif`） | **隐藏**（三处条件都只含 jpg/jpeg） | 无；`BuildOptions` 读隐藏控件默认值 | ISO-BMFF `tmap`，支持 |
| JXL | **不可见**（`HdrModeCombo` 条件不含 jxl） | 隐藏 | 主面板完全无入口 | `jhgm`，支持 |
| 两图合成（`GainMapModePanel`） | 独立面板 | 独立 `GainMapFormatCombo`（JPEG/AVIF/JXL） | 格式、底图色域 sRGB/Rec.2020 等 | `GainMapComposeService` 支持 AVIF/JXL |

### 2.1 AVIF 为什么看起来“不可用”

- `UpdateCodecPanelVisibility`：`HdrModeCombo.IsVisible = fmt is "jpg" or "jpeg" or "avif"`（约 2790 行）；但同函数只在 `case "jpg"/"jpeg"` 里打开 `JpegGainMapPanel`（约 2889–2891 行）。
- `UpdateGainMapPanelVisibility`：`if (fmt is not ("jpg" or "jpeg")) return;`（约 4419–4422 行）；`HdrMode_Changed` 同样只对 jpg/jpeg 打开面板（约 6436–6441 行）。
- `UpdateIccPanelVisibility` 的 `gainMapOn` 也只算 jpg/jpeg（约 4598–4601 行）。
- `BuildOptions` 的 `JpegGainMap = HdrModeCombo?.SelectedIndex == 1` **不看格式**（约 3388 行）；`JpegGainMapBaseGamut = ParseGainMapBaseGamut()`（约 3394 行）从隐藏下拉读取，默认 index 0=`srgb`（约 6481–6482 行）。
- `JpegGainMapPanel` 的 XAML 默认：类型灰度（`JpegGainMapTypeCombo` 索引 0）、metadata `iso`（索引 0）、底图 sRGB（索引 0）、质量跟随主图（`JpegGainMapFollowMainCheck IsChecked=True`）、目标 1000 nits、降采样 half（索引 1）。见 `MainWindow.xaml:810-865`。
- `HdrModeCombo` 由 `FillComboByLoc(..., "hdr.mode.traditional", "hdr.mode.gainmap")` 填充（约 1109 行），所有格式共享同一组选项；格式切换时**没有**把 `HdrModeCombo.SelectedIndex` 复位。因此“JPEG 上开过 GainMap → 切到 AVIF”会保留 `JpegGainMap=true`，但面板消失，用户既无法确认也无法修改。

### 2.2 AVIF 实际能产出什么

用与 UI `BuildOptions` 等价的 CLI 参数（HDR 输入、`--hdr-mode gainmap`、默认 sRGB 底）已产出：

- `tests/output/_tmp/gainmap-avif-ui-probe-20261116-1/src_hdr.avif`，6,527 B；
- `ffprobe`：Stream Group `Gain Map`，第二路 256×192 灰度 AV1；
- ASCII 盒统计 `tmap: 2`，`CompatibleBrands` 含 `tmap`；
- 底图 `yuv420p(tv, bt709/bt709/iec61966-2-1)`。

→ **HDR 输入 + AVIF 主面板选 GainMap，后端可以出 tmap**；不是编码器不可用。

### 2.3 AVIF + XMP 会失败，而且面板隐藏导致无法自救

`GainMapEncoder` 对 AVIF 明确拒绝 `metadata=xmp`（`GainMapEncoder.cs:387-392`：AVIF 只有 `tmap`/ISO 21496-1，没有 XMP-only 形态）。实测：

```text
FfmpegGui.exe --headless -i src_rec2020_pq.avif -o <run>/out -f avif \
  --hdr-mode gainmap --quality 90 --color-engine auto \
  --jpeg-gain-map-metadata xmp
```

结果 `exit=1`，日志末尾：

```text
[GainMap] ERROR: AVIF has no XMP-only gain-map form (the tmap derived item carries ISO 21496-1 only)
[色彩引擎] ❌ 执行失败：InvalidOperationException: GainMapEncoder 编码失败
```

用户在 JPEG 上选过 XMP/both 后切到 AVIF，`JpegGainMapMetadata` 会原样带过去；AVIF 的 metadata 下拉又不可见，无法改成 ISO。这是“AVIF GainMap 不可用”的一个真实可复现路径。

---

## 3. 后端能力真值

| 真值源 | AVIF | JPEG | JXL | 是否运行时实际读取 |
|---|---|---|---|---|
| `GainMapEncoder.FormatCarriesGainMap`（`GainMapEncoder.cs:117-118`） | true | true | true | 是；`QueueProcessor` 分派、契约拒绝判据 |
| `ColorTransformPlan.Caps["avif"].SupportsGainMap`（`ColorTransformPlan.cs:680-695`） | true | true | true | 是；`ColorStrategyPlanning` 按 `FormatColorCaps.SupportsGainMap` 判定 |
| `FormatCapabilitiesService` AVIF 条目（`FormatCapabilitiesService.cs:126-139`） | **未设，默认 false** | true（约 86 行） | true（约 200 行） | **未检索到读取点**；当前消费者只有 `ThumbnailService.GetCapabilities` |
| UI 主面板 | HdrMode 可见、设置隐藏 | 完整 | 主面板无 HdrMode | 由 `MainWindow.xaml.cs` 的条件决定 |

- `GainMapEncoder` 的容器出口：AVIF 走 `EncodeAvifGainMapAsync`，写 ISO-BMFF `tmap`（约 382–400 行）；JXL 走 `jhgm`；JPEG 走 APP2/XMP/MPF。
- `QueueProcessor` GainMap 专用分派明确含 `avif`/`jxl`（约 1285–1297 行）；但同文件约 1369–1371 行的拒绝文案仍写“JPEG 族是唯一能承载 GainMap 的容器”，与 `FormatCarriesGainMap` 不一致。该文案被 `verify-color-matrix-full` 锁定，属于文案/UI 诚实性问题，不是分派逻辑。
- `FfmpegOptions.cs:425` 的 `JpegGainMap` 注释仍写“仅 JPEG 输出”，也是旧文案。

**结论**：AVIF GainMap 的运行时真值链是 `FormatCarriesGainMap` + `ColorTransformPlan.Caps`；`FormatCapabilitiesService` 的 false 是**死数据**，但它是第三份真值，应该同批改成 true 或删除该字段，避免后续 UI 误用。

---

## 4. Rec.2020 底图：实际像素与标签

### 4.1 代码路径

`GainMapEncoder` 内部：

- `wideBase = baseGamut is "bt2020" or "bt2100"`（约 239 行）；
- `baseTok = wideBase ? "bt2020" : "bt709"`，`baseName` 同理（约 288–289 行）；
- `ResolveGamutMatrix(...)` 求源→底图线性矩阵；`SimdPixelOps.ApplyPrimariesMatrix` 对 `normHdr` 与 `sdrLinear` 分别施加（约 290–317 行）；
- `BuildBaseBgra8` 把 `sdrLinear` 用 **sRGB gamma** 落成 8-bit 字节（约 319–327 行）；
- AVIF 出口 `EncodeAvifGainMapAsync` 宽底 `primaries=bt2020`、`trc=bt709`、`matrix=bt2020nc`（约 1093–1095 行）；最终 `IsoBmffGainMapWriter.Build` 写 `cPrim=9, cTrc=1, cMtx=9`（约 1165–1167 行）；
- JPEG 出口宽底附 ICC：`bt2020` primaries、`iec61966-2-1` trc、`bt2020nc` matrix（约 444–459 行）。

`RawColorPipeline.TransformGainMapAsync` 在编码前清空计划层 `AttachIcc/IccPath/IccPrimaries/IccTrc/IccMatrix/OutPrimaries/OutTransfer/OutMatrix`（约 387–397 行），注释明确“底图 ICC 由编码器自己写”；所以计划层的 `SDR(bt709)` 目标不会覆盖底图标签。

### 4.2 运行时回读

HDR 源 `src_hdr.avif`（512×384、`gbrp12le`、`bt2020/smpte2084`、双 stream）：

| 输出 | 大小 | 底图 CICP / ICC | tmap |
|---|---|---|---|
| AVIF 默认 sRGB 底 | 6,527 B | `yuv420p(tv, bt709/bt709/iec61966-2-1)` | 2 |
| AVIF Rec.2020 底 | 6,438 B | `yuv420p(tv, bt2020/bt709/bt2020nc)` | 2 |
| JPEG Rec.2020 底 | 16,143 B | ICC `bt2020/sRGB`；ICC-cicp `BT.2020, BT.2100 / sRGB or sYCC / Identity` | APP2/XMP/MPF（非 tmap） |

**AVIF 宽底的 `trc=bt709` 与 `BuildBaseBgra8` 的 sRGB 曲线不一致**；JPEG 宽底 ICC 写的是 sRGB 曲线。二者是同一底图字节、不同容器标签，属于可测的标签/曲线错配（不是“压到窄色域”）。

### 4.3 像素矩阵（SDR 合成源，隔离色域映射）

自造 512×384 彩条 `pattern_sdr.png`，以 `avifenc --lossless --yuv 444` 分别写 CICP 1/13/0（sRGB）、12/13/0（P3）、9/1/0（Rec.2020）作为输入；以 `--hdr-mode gainmap --jpeg-gain-map-base-gamut srgb|bt2020` 跑 AVIF 出口。SDR 输入全部命中 `headroom ≤ 1` 短路，输出普通 AVIF（无 tmap），但底图色域矩阵阶段完整执行。

比较方式：把输出底图解码为 RGB24，取 32 个色块中心，与源 `pattern_sdr.png` 原始码值比较（**不是**经 ICC 色彩管理后的视觉误差）。

| 输入 | 底图 | tmap | 输出底图 CICP | 平均绝对差 / 最大差 |
|---|---|---|---|---|
| sRGB | sRGB | 无 | `bt709`，primaries/transfer unknown | 1.10 / 6 |
| sRGB | Rec.2020 | 无 | `bt2020nc`，primaries/transfer unknown | 29.35 / 164 |
| P3 | sRGB | 无 | `bt709`，unknown | 10.03 / 73 |
| P3 | Rec.2020 | 无 | `bt2020nc`，unknown | 19.34 / 136 |
| Rec.2020 | sRGB | 无 | `bt709`，unknown | 18.03 / 128 |
| Rec.2020 | Rec.2020 | 无 | `bt2020nc`，unknown | **4.65 / 14** |

关键读数：

- **sRGB→sRGB** 基本恒等；**Rec.2020→Rec.2020** 也基本恒等（4.65/255，主要是 8-bit/编码量化）。
- **Rec.2020→sRGB** 平均差 18、最大 128：这才是宽源进窄底图的真实裁切/去饱和，属预期。
- **sRGB→Rec.2020** 平均差 29：这是 sRGB 源映射到更宽底图后的码值变化（不是压缩）；P3→Rec.2020 同理。
- 因此没有“选 Rec.2020 反而压到极窄色域”的像素级证据；恰好相反，Rec.2020 底对宽源是保持/扩张，只有选 sRGB 底时宽源才会被裁。

> 注意：SDR 输出底图的 `color_primaries`/`transfer` 在 ffprobe 里是 `unknown`；只有 `color_space` 被写出。这是 SDR 短路路径的真实缺陷，见 §4.4。

### 4.4 两个真实颜色问题

**A. SDR 短路 AVIF 的底图标签不完整（P1）**

`GainMapEncoder.cs:1148-1153` 在 `maxLog2Gain - minLog2Gain <= 0` 时直接 `File.WriteAllBytesAsync(outputPath, baseBytes)`，**不经过** `IsoBmffGainMapWriter.Build`；`baseBytes` 由 `EncodeAvifFileAsync` 用 ffmpeg 产出，实测只落 `color_space`，`primaries/transfer` 为 unknown。若 `baseGamut=bt2020`，产物是 `bt2020nc` + unknown primaries/transfer；查看器很可能按 sRGB/未知解释，表现为**底图变窄、变灰**。这不是像素被压，而是**标签没写全**。

**B. AVIF 宽底的 trc 标签与字节曲线不一致（P2）**

AVIF `cTrc=1`（BT.709）而 `BuildBaseBgra8` 用 sRGB 曲线；JPEG ICC 用 `iec61966-2-1`。同一底图字节在不同出口的传递函数标签不一致。影响是亮部/暗部渲染略有差异，不构成色域压缩，但属于“标签声称了没实现的曲线”。

### 4.5 `GamutClipped` 告警为什么是误导

JPEG Rec.2020 底探针的 stdout 里有：

```text
[色彩引擎] ⚠️ 降级（GamutClipped）：源色域超出目标 源（CICP 标签） -> SDR(bt709/iec61966-2-1) ⇒ 越界颜色被裁切或压缩（ColorLoss=ToneMapped）
```

但产物底图 ICC 实际是 `bt2020/sRGB`，AVIF 同类产物是 `bt2020/bt709/bt2020nc`。代码上 `RawColorPipeline` 编码前清空计划标签，底图标签由 `GainMapEncoder` 按 `baseGamut` 重写；因此这条告警是**规划层仍把目标当成 SDR bt709**产生的，不是实际裁切。

- 该告警只在 JPEG 那次探针出现；AVIF 探针 stdout 没有这条。
- 根因在 `ColorStrategyPlanning` 的 `p.GamutOutsideDestination` 判据：计划目标由源推导，不包含 `JpegGainMapBaseGamut`；不是 `GainMapEncoder` 的像素结果。
- 修复方向：把 GainMap 底图色域纳入 `ColorIntent`/`PlanPolicy`，或在 `GainMapRequested` 时用底图目标计算 `Dst`；不要仅删日志，否则计划层继续与执行层不一致。

---

## 5. 输入矩阵（含 HDR）

### 5.1 HDR sRGB / P3 / Rec.2020 × 底图 sRGB / Rec.2020

用同一彩条以 `avifenc --cicp <primaries>/16/0 --depth 10 --lossless` 生成 PQ 输入；每格跑 AVIF GainMap，全部 `exit=0`：

| 输入（PQ） | 底图 | tmap | 底图 CICP |
|---|---|---|---|
| sRGB (1/16/0) | sRGB | 有 | `bt709/iec61966-2-1/bt709` |
| sRGB | Rec.2020 | 有 | `bt2020/bt709/bt2020nc` |
| P3 (12/16/0) | sRGB | 有 | `bt709/iec61966-2-1/bt709` |
| P3 | Rec.2020 | 有 | `bt2020/bt709/bt2020nc` |
| Rec.2020 (9/16/0) | sRGB | 有 | `bt709/iec61966-2-1/bt709` |
| Rec.2020 | Rec.2020 | 有 | `bt2020/bt709/bt2020nc` |

- 所有 HDR 格都有 `Stream Group: Gain Map`，增益图 256×192 灰度；
- 宽底标签完整，底图 512×384 `yuv420p`；
- 没有一格在“选 Rec2020”时退回 `bt709` 底图，说明**底图色域选择对 HDR 主路径生效**；
- 这些 HDR 格未做逐像素色彩管理对比（底图经过 Reinhard 色调映射，不能直接与源码值比较）；色域结论由 SDR 矩阵的 primaries 阶段 + HDR 标签共同支撑。

**Legacy 对照**：同一 HDR Rec.2020 源 + `--color-engine legacy` + Rec.2020 底，`exit=0`，产物 1,210 B，仍为 `tmap`，底图 `bt2020/bt709/bt2020nc`。说明 AVIF GainMap 不依赖 engine/auto 分支。

### 5.2 源自带 SDR 底图（Ultra HDR / HEIC）

`tests/data/synth_heic_grid_aux_gainmap.heic`（64×48）→ AVIF GainMap：

| 底图 | 产物 | 底图 CICP | tmap |
|---|---|---|---|
| sRGB | 1,702 B | `bt709/iec61966-2-1/bt709` | 有 |
| Rec.2020 | 1,624 B | `bt2020/bt709/bt2020nc` | 有 |

`GainMapDecoder` 把源底图搬到 `baseRawPath`，`GainMapJpegCodec.ResolveEncodeParams` 对已解码 Ultra HDR 把 `SrcPrim` 取为底图原色（`GainMapJpegCodec.cs:206-224`）；主路径未传 `sourceBasePrimaries` 的缺口在**这个单输入配对场景**通常不构成错映射，因为 `SrcPrim` 已经是底图原色。两图合成才是 HDR/SDR 各自原色不同的场景，见 §5.4。

### 5.3 ProPhoto / Adobe（ICC-only）输入

- 以 `tests/output/_tmp/named_prophoto-rgb.v3.v44.icc` + `avifenc --icc` 生成 `src_prophoto.avif`（CICP unknown，ICC 为 ProPhoto RGB）；
- **相对路径输入失败（exit 90）**：`exiftool: File not found - tests/output/...`；绝对路径输入成功；
- 绝对路径下 SDR 短路为普通 AVIF，无 tmap；底图标签仍是 primaries/transfer unknown（同 §4.4），底图 `color_space` 分别 `bt709` / `bt2020nc`；
- `GainMapEncoder` 有具名空间矩阵路径：`ResolveGamutMatrix` 在 `primariesApproximated=true` 时优先 `ColorSpaceRegistry.LinearMatrix(spaceName, baseName)`（约 288–295 行），`GainMapJpegCodec.ResolveEncodeParams` 会把 `SourceSpaceName`/`PrimariesApproximated` 传下去（约 226–240 行）。ProPhoto 的 **HDR** 输入未生成成功（缺少 ProPhoto HDR 夹具），本项只验证到 SDR/ICC 入口与路径缺陷；不能据此宣称 ProPhoto HDR 已通过。

### 5.4 两图合成（SDR 底图 + HDR 参考）

代码审计：

- `GainMapModePanel` 有独立 `GainMapFormatCombo`（JPEG/AVIF/JXL，`MainWindow.xaml` 约 1209 行）与 `GainMapBaseGamutCombo`（sRGB/Rec.2020，约 1287 行）；
- `GainMapComposeService.ComposeAsync` 对 `hdrPrim` 与 `sdrPrim` **分别**传 `sourcePrimaries` / `sourceBasePrimaries`，并传各自的 `SpaceName` 与 `PrimariesApproximated`（约 151–165 行）；
- `GainMapEncoder` 对 `sdrHasOwnPrim` 分支分别求 HDR→base 与 SDR→base 矩阵（约 288–299 行），两侧独立施加。

**未做运行时合成**：CLI 没有 `GainMapCompose` 的解析入口，合成是 GUI 专属路径；当前结论是**代码路径正确、未取得运行时像素证据**。从设计上，它比主面板多传了 `sourceBasePrimaries`，不存在“把 HDR 原色当 SDR 底图原色”的已知缺口。

### 5.5 JXL

- `GainMapEncoder.FormatCarriesGainMap` 含 `jxl`；`ColorTransformPlan.Caps` JXL `SupportsGainMap=true`；`GainMapComposeService` 可在合成面板选 JXL；
- 但主面板 `HdrModeCombo` 可见性不含 `jxl`（`MainWindow.xaml.cs:2790`），所以**主单输入路径没有 JXL GainMap UI**；
- `GainMapEncoder` 的 JXL 出口 v1 **只允许 sRGB 底**（宽底直接拒绝，`GainMapEncoder.cs:411-417`）。若将来把主面板 JXL 打开，必须同时禁用/隐藏 Rec.2020 底图选项，否则会得到“选了宽底→编码失败”。

---

## 6. 修复建议（按优先级）

### P1 — UI 暴露与状态隔离

1. **建立单一 `GainMapUiFormats` 判据**：以 `GainMapEncoder.FormatCarriesGainMap`（jpeg/jpegli/jpg/avif/jxl）为容器真值，UI 只做一次归一化；不要在每个函数里重复 `is "jpg" or "jpeg"`。
2. **AVIF 主面板显示 GainMap 设置**：把 `JpegGainMapPanel` 对 AVIF 打开；`UpdateCodecPanelVisibility`、`UpdateGainMapPanelVisibility`、`HdrMode_Changed`、`UpdateIccPanelVisibility` 四处统一调用同一 helper。
3. **AVIF 专属字段裁剪**：metadata 下拉对 AVIF 隐藏/禁用并强制 `iso`（`tmap` 只承载 ISO 21496-1）；类型、底图色域、质量、nits、降采样保留。`BuildOptions` 在 AVIF 下必须把 `JpegGainMapMetadata` 归一到 `iso`，不能把 JPEG 的 `xmp/both` 带过去。
4. **格式切换清理隐藏状态**：切到 AVIF/JXL 时，把不适用字段复位；至少对 `JpegGainMapMetadata` 做“当前格式支持集”校验，避免“面板看不见、任务却继承非法值”的静默失败。
5. **补 UI 断言**：AVIF + GainMap → 面板可见；AVIF metadata 下拉不存在/禁用；JPEG XMP → 切 AVIF 后自动回 ISO；JXL 主面板要么无 HdrMode、要么宽底选项禁用。现有 `verify-ui-host` 五件套没有覆盖 AVIF GainMap 面板，建议加到 `GroupC`/`GroupO`。

### P1 — SDR AVIF 底图标签

6. **SDR 短路也写完整 CICP**：`EncodeAvifGainMapAsync` 在 `maxLog2Gain - minLog2Gain <= 0` 分支不能直接返回 ffmpeg 的 `baseBytes`；应复用 `IsoBmffGainMapWriter`/等价的 `colr` 重写，或让 `EncodeAvifFileAsync` 产出带完整 `primaries/transfer` 的 AVIF。至少保证 `bt2020nc` 时 `primaries=bt2020`、`transfer=iec61966-2-1`（与字节曲线一致），而不是 unknown。
7. **统一宽底 trc 口径**：AVIF 宽底把 `cTrc` 改为 sRGB（13），或把 `BuildBaseBgra8` 的宽底字节改为 BT.709 曲线；不能一边用 sRGB 字节、一边写 BT.709 标签。JPEG 保持当前 ICC `bt2020/sRGB`。

### P1 — 计划层与执行层同口径

8. **把 GainMap 底图色域纳入计划**：`ColorIntent`/`PlanPolicy`/`ColorTransformPlan` 增加 `GainMapBaseGamut`（`srgb`/`bt2020`）；`ColorStrategyPlanning` 对 `GainMapRequested` 用底图目标计算 `Dst`，不要仍按 SDR bt709 推导。
9. **消除误导性 `GamutClipped`**：若源是 HDR→SDR 底图，应报“HDR→SDR 色调映射 + 底图色域映射”，而不是“源超出 SDR(bt709) 被裁切”；或在 GainMap 模式下不登记 `GamutClipped`，由 `GainMapEncoder` 的矩阵/越界日志作为真实损失来源。
10. **补回归测试**：同源同参数，断言计划目标 = 底图目标；`baseGamut=bt2020` 时 stdout 不得出现 `SDR(bt709)` 的 GainMap 告警；同时断言产物 CICP/ICC 仍为 bt2020。

### P2 — 第三份真值与文案

11. `FormatCapabilitiesService` AVIF `SupportsGainMap` 改为 true（与 `ColorTransformPlan.Caps` / `FormatCarriesGainMap` 同批），或删除该字段；当前它没有读取点，但保留 false 会让未来 UI 复用者重新引入“AVIF 不可用”。
12. `QueueProcessor.cs:1369-1371` 的“JPEG 族是唯一能承载 GainMap 的容器”改为“JPEG 族、AVIF(tmap)、JXL(jhgm) 均可承载”；同步 `FfmpegOptions.cs:425` 的“仅 JPEG 输出”注释。该文案被 `verify-color-matrix-full` 锁定，必须同批改测试。
13. ProPhoto/ICC-only 路径：在 `ProbeInputIccDescriptor`/`ResolveEncodeParams` 前把输入路径规范成绝对路径，避免 CLI 相对路径下 exiftool 报 `File not found`；GUI 路径通常绝对，但这是可复现入口缺陷。

### P3 — JXL 与主路径 `sourceBasePrimaries`

14. 主面板 JXL GainMap 若产品上要开放，`HdrModeCombo` 增加 jxl，并同时禁用宽底；若不开放，应在 UI/文档明确“JXL GainMap 仅在两图合成入口”，不要让后端能力与 UI 看起来矛盾。
15. `RawColorPipeline` / legacy `ProcessGainMapJpegAsync` 未传 `sourceBasePrimaries`：对单输入 Ultra HDR 通常无害（`SrcPrim` 已是底图原色），但若未来支持“HDR 参考与 SDR 底图原色不同”的单输入封装，应把 `sourceBasePrimaries` 与 `sourceBaseSpaceName` 一并传下去；两图合成已正确传参，可作为参照。

---

## 7. 证据与限制

### 已完成的运行时证据

- AVIF HDR GainMap 默认 sRGB 底：6,527 B，`tmap: 2`，增益图 256×192；
- AVIF HDR Rec.2020 底：6,438 B，`tmap: 2`，底图 `bt2020/bt709/bt2020nc`；
- JPEG HDR Rec.2020 底：16,143 B，ICC `bt2020/sRGB`，ICC-cicp `BT.2020/BT.2100 / sRGB / Identity`；
- SDR 矩阵 6 格（sRGB/P3/Rec.2020 × sRGB/Rec.2020 底）：全部 exit=0，全部无 tmap（SDR 短路），底图标签见 §4.3；
- HDR 矩阵 6 格（sRGB/P3/Rec.2020 PQ × sRGB/Rec.2020 底）：全部 exit=0，全部有 tmap；
- Legacy AVIF HDR Rec.2020 底：exit=0，tmap，宽底标签正确；
- 源自带 SDR 底图 HEIC × 2 底：exit=0，均有 tmap，宽底标签正确；
- ProPhoto ICC-only：相对路径 exit=90（exiftool File not found），绝对路径 exit=0（SDR 短路无 tmap）；
- AVIF + `metadata=xmp`：exit=1，`GainMapEncoder` 明确拒绝。

产物目录：

- `tests/output/_tmp/gainmap-avif-ui-probe-20261116-1/`
- `tests/output/_tmp/gainmap-base-probe-20261116/`
- `tests/output/_tmp/gainmap-color-matrix-20261116/`（含 `run_*`、`decoded_base.png`、`stdout.txt`）

### 未完成/未验证

- **真实 GUI 鼠标点击**：没有对 AVIF 面板做桌面级点击验证；本文的 UI 结论来自当前工作树代码与 headless 等价参数。`HdrModeCombo` 在 AVIF 下可见是代码事实，但“用户实际看到的面板布局”仍建议用 `UiTestHost` 或桌面会话补一次。
- **HDR 逐像素色彩管理对比**：HDR 底图经 Reinhard 映射，未做 ICC/色度坐标级对比；色域结论由 SDR matrix + HDR 标签支撑。
- **两图合成运行时**：只做代码审计，CLI 无入口；未取得合成 AVIF 的像素/tmap 证据。
- **ProPhoto HDR**：未生成 HDR ProPhoto 夹具；只验证 SDR/ICC 入口与路径问题。
- **JXL 主面板**：未跑 JXL GainMap 运行时；代码确认 jhgm 支持、主面板无 HdrMode。
- **源 `synth_heic_grid_aux_gainmap.heic` 的底图原色**：输出标签证明宽底切换生效，但未逐像素核对“源 SDR 底图”与输出底图的一致性；`GainMapDecoder` 的 `baseRawPath` 搬移路径已读代码，未做逐字节对撞。

---

## 8. 一句话回答

**AVIF Gain Map 的后端是通的，真正的问题是主面板只给了“GainMap”开关却没给 JPEG 同款设置；Rec.2020 底图在 HDR 输入上没有把颜色压窄，实际压窄只发生在“宽源 + sRGB 底”以及“SDR 短路后底图 CICP 缺失、查看器按 sRGB 解释”这两种情况；计划层的 `SDR(bt709)` 告警是目标口径错误，不是像素结果。**
