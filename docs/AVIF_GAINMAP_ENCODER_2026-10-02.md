# AVIF GainMap 编码实现记录（2026-10-02）

> 本文记录「原生 AVIF 增益图编码」的首个可用增量：容器写出器 + 三处**实测得出**的容器硬要求 + 三路第三方验证读数。
> 状态：**写出器与编码路径已落地并通过验证；产品侧接线（能力表/路由）与 HDR 模式开关尚未做**。

---

## 1. 已落地代码

| 文件 | 变更 |
|---|---|
| `src/FfmpegGui/Services/IsoBmffGainMapWriter.cs` | **新增**（约 470 行）。纯托管 ISO-BMFF 容器写出器：输入「底图 AVIF + 增益图 AVIF + ISO 21496-1 tmap 载荷」，输出带 `tmap` 派生项的三 item 容器 |
| `src/FfmpegGui/Services/GainMapEncoder.cs` | ① `BuildIso21496Metadata` 由 `private` 改 `internal`（供写出器复用同一份载荷，**单一真值**）② `EncodeAsync` 新增 `container` 形参（**必须是最后一个**，`QueueProcessor.cs:1895` 按位置传 `log/ct`）③ 新增 `EncodeAvifGainMapAsync` / `EncodeAvifFileAsync` ④ 像素阶段（归一化/Reinhard/底图色域映射/增益图计算）抽到容器分支**之前**，两条出口共用 |

构建：`dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release --no-restore` ⇒ **0 警告 0 错误**。

### 关键设计
- **载荷复用**：tmap 载荷 = `u8 version(0)` + `BuildIso21496Metadata(...)`，与 JPEG APP2 载荷**逐字节同构**（对照 libavif `write.c:1027-1040` 与 libultrahdr `avifultrahdr.cpp:502`）。
- **两遍偏移**：`meta` 长度依赖 `iloc`，`iloc` 偏移依赖 `meta` 结束位置 ⇒ 先零偏移量长、再填真值；两遍长度不一致即**报错**（不静默继续）。
- **容器无关的像素阶段**：AVIF 与 JPEG 出口共用同一套增益语义，不允许各算一遍。

---

## 2. 三处容器硬要求（**全部由实测得出，非查规范推得**）

| # | 要求 | 缺它的症状 | 证据 |
|---|---|---|---|
| 1 | `dimg` **必须写 `reference_count`** | `avifdec` 报 `box[dimg] for 'tmap' item 2 must have exactly 2 entries with distinct ids` | 首版按「无 count」写 ⇒ 立即红；补 `from(2)+count(2)+to[1,3]` 后消失 |
| 2 | `meta` **必须含 `grpl`→`altr` 实体组**（`entity_id=[tmap, base]`） | 容器可解析、主图可解，但 `avifdec --info` 报 `Gain map: Absent` | 逐盒对比样本发现样本多出 `grpl`；补上后立即变为 `Gain map: 64x48 pixels, 8 bit, YUV400, … Base Headroom 0.00 (SDR), Alternate Headroom 1.30 (HDR)` |
| 3 | 底图 `colr` **必须由写出器重写** | ffmpeg 的 `avif` muxer **不采纳** `-color_primaries/-color_trc` ⇒ 写出 `primaries=2/transfer=2`（unspecified），与像素不符 | 实测 ffmpeg 产物 `colr` 载荷 = `6e636c78 0002 0002 0001 00`；写出器改由调用方给 CICP 三元组 |

**附带**：`tmap` 项需带一个描述**替代图（HDR 侧）**的独立 `colr`（transfer 默认 16=PQ）——与 libultrahdr v2 `avifultrahdr.cpp:506-520` 同口径；libavif 据此报告 `Alternate image`。

---

## 3. 三路独立验证（同一份产物 `out3.avif`，64×48，1274–1313 B）

| 验证方 | 读数 |
|---|---|
| **产品自身探测器** `ServiceProbe gainmap-isobmff` | `PROBE RESULT: pass=11 fail=0`；`form=tmap primary=1 tmap=2 gainmapItem=3`；`codec=av01 ffmpegFmt=obu tmapPayload=142`；`载荷长度闭合 1+21+ch×40` ✓ |
| **libavif 1.4.2** `avifdec --info` | `Gain map: 64x48 pixels, 8 bit, YUV400, Full Range, Matrix Coeffs. 2, Base Headroom 0.00 (SDR), Alternate Headroom 1.30 (HDR)` + `Alternate image` |
| **Google libultrahdr v2.0.2** `ultrahdr_app -m 1 -P -j` | `Ultra HDR Image: Yes` + `--maxContentBoost 2.42364 2.42369 2.42497` / `--minContentBoost …` / `--gamma …` / `--offsetSdr/Hdr 0.015625` / `--hdrCapacityMin 1` / `--hdrCapacityMax 2.46229` / `--useBaseColorSpace 1` |
| `ffprobe` | 2 条 av1 流（64×48，nb_frames=1） |

> ⚠ `avifdec` 会打印 `ERROR: Failed to decode image: No codec available` —— **该构建未编入 AV1 解码器**，对 libavif 自家样本**同样报此错** ⇒ 非产物缺陷，不可据此判红。

---

## 4. Google Ultra HDR 最新实现（libultrahdr v2.x）要点

- **v2.0.0（2025-08-07）**：新增 **HEIF/HEIC 与 AVIF 的完整编码/解码**（ISO 21496-1 增益元数据）；架构重构为 `UltraHdr` 基类 + `JpegR` / `HeifUltraHdr` / `AvifUltraHdr` 三个子类；C-API 启用 `UHDR_CODEC_HEIF` / `UHDR_CODEC_AVIF`。
- **v2.0.1（2025-08-10）**：随构建自动应用 **libheif 的 ISO 21496-1 补丁（PR #1503）**；`ultrahdr_app` 按输出扩展名自动选容器。
- **v2.0.2（2025-08-13）**：cmake 修复。
- **实现方式**：`avifultrahdr.cpp`（740 行）**不手工组装 box**，而是把容器授权**委托给 libheif**（`heif_context_encode_gain_map_image`）。其增益图用 `heif_colorspace_monochrome`（单色），tmap 项另建 nclx 描述 HDR 侧；`clli` 与 tmap 的 ICC **仍是 TODO**（`:504-505`）。
- ⇒ **本仓的价值定位**：本项目走**纯托管**路线（与 JPEG 出口一致，不引入 libheif 构建依赖），因而必须自行实现 libheif 承担的那部分容器授权 —— 本记录 §2 的三条硬要求就是该部分的实测结论。
- ⚠ 本机 `tools/src/libultrahdr/build/Release/ultrahdr_app.exe` 是 **v2.0.2**，**可作 AVIF 增益图的第三方验证器**（现有 `verify-gainmap-thirdparty.ps1` 已在用它的 `-m 1 -P -j` 形态）。

---

## 5. 产品侧接线（✅ 已完成，2026-10-02 第二批）

| 改动 | 位置 |
|---|---|
| 能力表：avif 置 `SupportsGainMap = true` | `ColorMapping/ColorTransformPlan.cs`（avif 条目） |
| 拒绝文案：不再宣称「JPEG 族是唯一」 | `ColorMapping/ColorStrategyPlanning.cs`（`SupportsGainMap` 闸的 Reject） |
| 容器单一真值：新增 `GainMapEncoder.ContainerOf(outputPath)` | `GainMapEncoder.cs` |
| 引擎出口传容器 | `ColorMapping/RawColorPipeline.cs`（`EncodeAsync` 调用点） |
| 专用管线传容器 | `QueueProcessor.cs`（`EncodeAsync` 调用点） |
| 专用管线**分派条件加 avif** | `QueueProcessor.cs`（原仅 `jpg`/`jpeg`）—— 不加这一支，`--color-engine legacy` 下 avif+增益图会**静默丢掉增益图** |

### 端到端验证（产品真实产物，非合成件）

```
FfmpegGui.exe --headless -i tests/output/sources/src_hdr_pq.png -o <dir> -f avif --jpeg-gain-map true
⇒ src_hdr_pq.avif (6450 B)
```

| 验证方 | 读数 |
|---|---|
| 自家探测器 | `pass=11 fail=0`；`form=tmap`；`tmapPayload=62`（**channels=1**）；`capMax=2.3` |
| libavif 1.4.2 | `Gain map: 256x192 pixels, 8 bit, YUV400, Base Headroom 0.00 (SDR), Alternate Headroom 2.30 (HDR)`（底图 512×384） |
| **Google libultrahdr v2.0.2** | `Ultra HDR Image: Yes`；`--maxContentBoost 4.92458`；`--hdrCapacityMax 4.92458`；`--useBaseColorSpace 1` |

### 回归与变异验证

- 四工程重建通过，四份 `FfmpegGui.dll` SHA256 前 16 位**完全一致**（`19102d3772feef35`）。
- 回归探针：`colormath 12/0` · `matrix 15/0` · `verdict 41/0` · `plan 39/0` · `decision 4/0` · `curve 44/0`。
- ⚠ `contract` 曾因**编码旧契约的断言**（`GainMap × avif：必须 Reject+建议`）转红 ⇒ 已把 avif 移入**正向**断言、负控保留 6 格式（png/tiff/webp/jxl/gif/heic），**未放宽任何判据**；现 `contract 137/0`。
- **变异验证**：把 avif 的 `SupportsGainMap` 改回 `false` ⇒ 两条新断言同时转红（`135/2`）；还原后回 `137/0`。

## 6. HDR 实现模式开关（✅ 已完成，2026-10-02 第三批）

**设计决策（关键）**：`HdrImplementationMode { Traditional, GainMap }` 做成对既有持久位
`FfmpegOptions.JpegGainMap` 的**强类型视图**，**不是第二个存储字段**：

```csharp
public HdrImplementationMode HdrMode
{
    get => JpegGainMap ? HdrImplementationMode.GainMap : HdrImplementationMode.Traditional;
    set => JpegGainMap = value == HdrImplementationMode.GainMap;
}
```

理由：`JpegGainMap` 是**已持久化**的位（旧 CLI / 旧预设 / 旧 AppSettings 都在写），且全仓有 ~20 个读取点。
若另起存储字段，就必须在多处同步两个真值 —— 本仓反复修过「两套映射必然漂移」这类缺陷。
视图方案 ⇒ **单一真值、零同步风险、旧输入自动兼容、20 个既有消费者无需改动**。

| 改动 | 位置 |
|---|---|
| 新枚举 `HdrImplementationMode` | `Models/ColorStrategy.cs` |
| `HdrMode` 视图属性 | `Models/FfmpegOptions.cs` |
| CLI `--hdr-mode <gainmap\|traditional>`（`ParseTokenStrict` 封闭值域） | `CliParser.cs` |
| CLI 帮助行 | `CliParser.cs` |
| UI：`CheckBox` → 两项 `ComboBox`（`HdrModeCombo`），14 处引用同步 | `MainWindow.xaml` / `.xaml.cs` |
| 下拉项本地化填充 | `MainWindow.xaml.cs`（`FillComboByLoc`） |
| 可见性扩展到 **avif** | `MainWindow.xaml.cs`（原仅 `jpg`/`jpeg`） |
| 本地化键 `hdr.mode.traditional` / `hdr.mode.gainmap` + 提示语改口 | `zh-CN.json` / `en-US.json` |
| UI 测试同步到新控件（GroupO 7 条断言） | `tests/UiTestHost/Program.cs` |

### 验证（CLI 三态 + UI 宿主）

| 场景 | 读数 |
|---|---|
| `--hdr-mode bogus` | **`exit=2`** + `错误: --hdr-mode 取值无效: 'bogus'（gainmap \| traditional）` |
| `--hdr-mode gainmap`（AVIF 出口） | 产物 6450 B，探测器 `hasGainMap=True form=tmap`，`pass=11 fail=0` |
| `--hdr-mode traditional`（AVIF 出口） | 产物 5247 B，`hasGainMap=False`（原生 PQ/HLG） |
| `UiTestHost` | **405 PASS / 0 FAIL**（GroupO 的 O1/O2a-c/O3a-c 七条全过，含开/关两态） |

### ⚠ 本轮踩到并已处置的两个坑

1. **中文串门禁 UI 面被顶穿**（`848 > 基线 840`）：根因是 `IsoBmffGainMapWriter.cs` 里 **8 行多行字符串的续行**
   其首字面量不以 `[` 开头 ⇒ 被计入 **UI 桶**（该门禁按「该行第一个命中字面量是否以 `[` 开头」分桶）。
   处置：续行合并为单行、内层 `err` 串改 ASCII 技术细节、`what` 标签改 `"base"`/`"gainmap"` ⇒ 回到 **840 = 基线（余量 0）**。
   ⇒ **教训：新增中文诊断串必须保证「该行首个字面量」以 `[` 开头，多行拼接尤其危险。**
2. **A30 结构锁的行号锚漂移**：在 `Models/ColorStrategy.cs` 插入枚举 ⇒ 该文件所有行号 **+26**，
   使 `_lib-matrix.ps1` 的 4 条 `Evidence` 锚（49/50/51/52）失效。已平移为 **75/76/77/78**（+1 处注释 47-53 → 73-79）⇒ `38/0`。

### 本轮新发现的**既存**文档债（非本轮引入，未修）

`tests/scripts/_lib-axes.ps1` 的 `-Source 'src/FfmpegGui/CliParser.cs:<行号>'` 字段**早已整体失效**：
例 `--chroma` 记 `542`、实际在 **615**（差 73 行）；`--threads` 记 `574`、实际 **729**。
且该字段**不被任何门禁校验**（`_lib-axes -SelfTest` 只查 A1–A15 的结构自洽）⇒ 属纯文档债。
⚠ 本轮一度按「+2/+9」平移过它，**已回退**（给已错的值再添噪声不如不动），并在此登记。

## 7. 未做（下一批）

1. **审计问题修补**（未开始）：`verify-colorfmt-matrix.ps1` 补 PASS/FAIL 计数（须连带摘 `$tableOnly` + ⑬ 针 4→3）；探针段补零断言闸；`pq` mode 接线；容差收口；过期读数（`GAINMAP_HEIC_AVIF_ANALYSIS.md:8` 的 29/0 → 52/0）。
2. **门禁扩展**：给 `verify-gainmap-isobmff.ps1` 加「AVIF **编码**产物」的端到端臂（现只覆盖解码）；把 `ultrahdr_app -m 1 -P -j` 纳入 AVIF 的第三方判据。
3. **整轮门禁**：本批按「改哪儿测哪儿」只跑了**受影响**的门禁（清单见 §8），**未跑 65 条整轮** ⇒ 出包前必须补跑。
4. **HEIC/HEIF 出口**：能力表仍按「不可表达」给（无 heif muxer）⇒ 不在本批范围。

## 8. 本批已跑的门禁（增量验证清单）

| 门禁 | 读数 | 触发原因 |
|---|---|---|
| `_probe-cjk-hardcode-scan.ps1` | **14/0** | 新增中文字面量（AVIF 写出器 + UI 文案） |
| `_probe-matrix-structure.ps1` | **8/0** | `Models/ColorStrategy.cs` 行号漂移 |
| `_lib-matrix.ps1 -SelfTest` | **38/0** | 同上（4 条 Evidence 锚已平移） |
| `_lib-axes.ps1 -SelfTest` | **19/0** | CliParser 新增参数（已回退该文件的平移） |
| `verify-ps-compat.ps1` | **13/0** | 改过 `_lib-matrix.ps1` |
| `UiTestHost.exe` | **405/0** | 控件由 CheckBox 换为 ComboBox |
| `ServiceProbe` contract / verdict / plan / decision | **137/0 · 41/0 · 39/0 · 4/0** | 能力表 `SupportsGainMap` 变更 |
| `ServiceProbe` colormath / matrix / curve | **12/0 · 15/0 · 44/0** | 回归复核 |

---

*记录时间 2026-10-02 · 写出器 + 产品接线 + HDR 模式开关均经三路/端到端验证；审计修补与整轮门禁未做。*
