# 增益图标注形态调研 + AVIF GainMap 实现复核（2026-10-02）

> 触发问题：① AVIF GainMap 实现是否正确；② 各相机厂商是否也存在「ISO 标记不明」的问题；③ 是否存在多种不同的标注情况。
> 方法：本机原始厂商样本（**非**输出目录里的再编码件）+ 自家探针 + Google 官方 libultrahdr v2.0.2 + libavif 交叉验证 + **产物对拍**。
> 证据分级：🟢实测 ｜ 🔵读码 ｜ ⚠未定论

---

## 0. 一页结论

| 问题 | 结论 |
|---|---|
| AVIF GainMap **编码**是否正确 | ✅ **正确**。三路独立验证：自家探测器 `11/0` · libavif `Gain map: 256x192 YUV400, Alternate Headroom 2.30 (HDR)` · **Google libultrahdr v2.0.2 `Ultra HDR Image: Yes`**（`maxContentBoost 4.92458`） |
| 其他厂商是否也有「ISO 标记不明」 | ✅ **有，而且更严重**。Apple 用**自家专有 XMP**，**完全没有 ISO 21496-1** |
| 是否多种标注情况 | ✅ **至少 3 种**：ISO 21496-1 二进制 / Adobe-Google XMP / **Apple 专有 XMP** |
| 我们的解码器能否读 Apple 件 | ❌ **不能**，且**静默**（产物与其「剥掉增益图」版本**逐字节相同**，却报「已完成」） |
| Google 官方能否读 Apple 件 | ✅ **能**（`Ultra HDR Image: Yes`，`maxContentBoost 23.1475`）⇒ 是**我们的覆盖缺口**，非业界不可能 |

⚠ **另有一处未能隔离的疑点**（见 §4）：我们**自家**的 Ultra HDR 文件回灌时，产物与剥离版也逐字节相同 ⇒ **解码侧增益图疑似未生效**。未能确认成因，**不作结论**。

---

## 1. 实测：业界三种标注方案

对**原始厂商样本**（`tools/src/libultrahdr/tests/data/`、`tools/src/libavif/tests/data/`）逐件解剖：

| 方案 | 判据 | 样本 | 读数 |
|---|---|---|---|
| **ISO 21496-1 二进制** | JPEG `APP2` 含 `urn:iso:std:iso:ts:21496:-1`；ISO-BMFF 含 `tmap` 派生项 | 15 个 `*.avif`（seine/color_grid 系列） | `tmap` 🟢 |
| **Adobe / Google XMP** | `http://ns.adobe.com/hdr-gain-map/1.0/`（`hdrgm:` 前缀），**无 ISO** | `seine_sdr_gainmap_srgb.jpg`、`paris_exif_xmp_gainmap_{big,little}endian.jpg`、`paris_exif_xmp_icc_gainmap_bigendian.jpg` | `XMP only` 🟢 |
| **Apple 专有 XMP** | `http://ns.apple.com/HDRGainMap/1.0/`（`HDRGainMap:`）+ `http://ns.apple.com/pixeldatainfo/1.0/`（`apdi:`），**无 ISO、无 Adobe 命名空间** | `apple_gainmap_new.jpg`、`apple_gainmap_old.jpg` | 见 §2 🟢 |
| （对照）普通 HDR | 无任何增益图标记 | `colors_hdr_*.avif`、`seine_hdr_*.avif` | `no-tmap` 🟢 |

### Apple 的实际标注（逐字取自 `apple_gainmap_new.jpg` 的 XMP）

```xml
<rdf:Description rdf:about=""
    xmlns:apdi="http://ns.apple.com/pixeldatainfo/1.0/"
    xmlns:HDRGainMap="http://ns.apple.com/HDRGainMap/1.0/">
  <apdi:NativeFormat>1278226488</apdi:NativeFormat>
  <apdi:AuxiliaryImageType>urn:com:apple:photo:2020:aux:hdrgainmap</apdi:AuxiliaryImageType>
  <apdi:StoredFormat>1278226488</apdi:StoredFormat>
```

**Apple 件的段结构**（`apple_gainmap_new.jpg`，50824 B）：
`APP0 JFIF` → `APP1 Exif` → **`APP2 MPF`（88 B，增益图作为第二幅）** → `APP2 ICC` → … → `SOS`
⇒ **没有 `APP2 ISO 21496-1`**，也没有 `hdrgm` XMP。增益量（`HDRGainMapHeadroom`）写在 Apple 自己的 XMP 里。

### 官方参考实现确实支持 Apple 方案

`tools/src/libultrahdr/lib/src/jpegrutils.cpp:475-476`：
```cpp
const string XMPXmlHandler::kAppleMapVersion  = "HDRGainMapVersion";
const string XMPXmlHandler::kAppleMapHeadroom = "HDRGainMapHeadroom";
```
⇒ Apple 方案不是"野路子"，是**官方显式支持**的第二套命名空间。

---

## 2. 实测：我们的解码器对三种方案的覆盖

探针 `ServiceProbe gainmap <file>`（读侧直读）：

| 样本 | 读数 | 关键行 |
|---|---|---|
| 自家产物（ISO APP2 + XMP） | **6/0** ✅ | — |
| **Apple 专有 XMP** | **4/2** ❌ | `IsGainMapContainer()=true` · 副图定位成功 · **`ISO 21496-1 元数据 = null`** · **ISO 增益区间 = 0** |
| **Adobe/Google XMP-only** | **3/3** ❌ | **`IsGainMapContainer()=false`** · `ISO 元数据 = null` |
| paris XMP-only | **3/3** ❌ | 同上 |

⇒ Apple 件**能被认出是增益图容器、副图也定位到了**，但**读不出增益元数据** ⇒ 无法还原 HDR。
Adobe/Google XMP-only 件连容器判定都是 false。

---

## 3. 🟢 **决定性产物对拍**：Apple 件被静默降级

**方法**：把 Apple 件的增益图剥掉（`exiftool -MPF:all= -XMP:all=`），分别喂给产品，比对产物。

```
原    件 (50824 B，带 Apple 增益图) → PNG
剥离版 (50734 B，无增益图)          → PNG
产物 SHA256 前 20 位：61788ab01aaec4102589  ==  61788ab01aaec4102589
```

⇒ **逐字节相同** ⇒ **产品完全忽略 Apple 的增益图，静默输出 SDR 底图，且报「已完成」**。

**对照**：同一件喂 Google 官方 libultrahdr v2.0.2：
```
Ultra HDR Image: Yes
--maxContentBoost 23.1475   --minContentBoost 1   --useBaseColorSpace 0
```
⇒ 官方**能**读出增益（`maxContentBoost 23.15`，即约 4.5 档动态范围被丢弃）。

### 影响面（按重要性）

1. **iPhone 默认拍摄格式**：iOS 17+ 的「HEIF/JPEG + 增益图」就是 Apple 这套标注 ⇒ 用户拿 iPhone 照片进来，**HDR 被静默丢掉**。
2. 违背本项目自定的「**不得静默降级**」口径 —— 与既有的 `SDR 输入 ⇒ 普通 JPEG 必须点名` 同类，但这里是**反方向**（有 HDR 却没还原）。
3. 修法有官方参照：`jpegrutils.cpp:475-476` 已给出字段名（`HDRGainMapVersion` / `HDRGainMapHeadroom`），照抄即可。

---

## 3b. ✅ **Apple 方案已实现**（2026-10-02 第四批）

**落点**：`GainMapDecoder.cs` 新增 `TryReadAppleXmpMetadata`（纯托管，不经 exiftool），并在解码路径的
XMP 兜底之后接入（`DecodeToLinearRawAsync` 内 `meta == null` 分支）。

**为什么必须纯托管**：实测 exiftool **不解析** Apple 命名空间（`-G1 -a -s` 只给 `Apple:HDRGain` /
`Apple:HDRHeadroom`，**无** `XMP-HDRGainMap:*`）⇒ 既有 exiftool 路径对该格式**结构性拿不到字段**。

**推导规则**（已写入代码注释）：`HDRGainMapHeadroom` 是 **log2** ⇒
`GainMapMin=0`、`GainMapMax=HdrCapacityMax=Headroom`、`Gamma=1`、`Offset*=0`、`Channels=1`。

### 验证：数值与 Google 官方参照**精确吻合**

| | 读数 |
|---|---|
| 实现前（Apple 件） | `decoded = null` ⇒ **静默降级为 SDR** |
| 实现后 | `decoded w=384 h=512 peakNits=4698.9`，`jxlCs=RGB_D65_P3_Rel_Lin` |
| **对拍** | `4698.9 / 203（SDR 白点）= 23.15` —— **等于 libultrahdr 报的 `maxContentBoost 23.1475`** ✅ |

### 回归（全变体）

| 变体 | 结果 |
|---|---|
| **Apple new**（iOS 17+ 当前格式） | ✅ **4698.9**（新能力） |
| 自家 Ultra HDR JPEG | ✅ 999.7（未变） |
| 自家 AVIF 增益图 | ✅ 999.7（未变） |
| libavif 原生 AVIF | ✅ 499.8（未变） |
| 普通 HDR PNG（负控） | ✅ `null`（正确：无增益图） |
| ⚠ **Apple old** | ✅ **已补**（见 §3c）—— 旧版 v1.0 无 headroom 字段，按**隐式 8×** 处理 |
| ⚠ **Adobe/Google XMP-only**（`seine_sdr_gainmap_srgb.jpg`） | ❌ `null` —— **已查清：参考实现也拒的畸形件，不是我们的缺口**（见 §3d） |

### §3c Apple **v1.0**（旧格式）也已支持

**症状**：`apple_gainmap_old.jpg` 原本 `decoded=null`。
**根因（实测）**：该件 XMP 只有 `<HDRGainMap:HDRGainMapVersion>65536</HDRGainMap:HDRGainMapVersion>`
（0x10000 = **v1.0**），**`HDRGainMapHeadroom` 0 命中** ⇒ 没有 headroom 可读。
**依据（外部参照）**：Google libultrahdr v2.0.2 对同一件报 **`maxContentBoost 8`**（= 2³）
⇒ **官方也是按隐式 8× 处理的**。
**修法**：Apple 命名空间存在但无 headroom 字段 ⇒ 按 `log2(8) = 3` 处理（并点名）。
**验证**：`decoded peakNits=1624.0`；`1624.0 / 203 = 8.0` —— **精确等于官方的 `maxContentBoost 8`** ✅

| 变体 | 我们的解码峰值 | ÷203（SDR 白点） | 官方 `maxContentBoost` |
|---|---|---|---|
| Apple **v1.0** | **1624.0** | **8.0** | **8** ✓ |
| Apple **v2.0** | 4698.9 | 23.15 | 23.1475 ✓ |

### §3d Adobe/Google XMP-only：**不是我们的缺口**（已查清，勿再追）

`seine_sdr_gainmap_srgb.jpg`（Google Pixel 4a 产物）：`urn:iso:std:iso:ts:21496` **全文件 0 命中**；
`hdrgm:GainMapMin/Max` 以**属性**形式书写 ⇒ exiftool 不暴露。
**决定性对照**：Google 官方 `ultrahdr_app -m 1 -P -j <该件>` ⇒
**`xml parse error, could not find attribute hdrgm:GainMapMax`** —— **参考实现自己也读不了**。
⇒ 这是参考实现也拒绝的畸形/边缘件。**不要为它改产品代码**；门禁纳入样本时要剔除它。

中文串门禁：新增 6 条全部进 **TAG 桶**（`[GainMap解码]` 前缀）⇒ **UI 面 849 未变**。

---

## 4. ✅ **原疑点已推翻**（2026-10-02 当日隔离完成）

**上一版在此处登记「自家件解码疑似未生效」——已证伪，是我测法的错。**

**错在哪**：我用 `exiftool -MPF:all= -XMP:all=` 做「剥离」，但它只删**指针**（MPF/XMP 段），
增益图的**字节仍物理留在文件尾部** ⇒ 解码器照样找得到并解码。实测剥离版探针仍给出
`decoded w=512 h=384 peakNits=999.7`（与原件同值）⇒ 两份产物相同是**必然**，与解码能力无关。

**正确的隔离手段**：新增 `ServiceProbe ultrahdr <file>`（本批新增），把三段分开观测——
① 容器检测 ② 元数据读取 ③ 线性 HDR 解码。读数：

| 文件 | ① 检测 | ② 元数据 | ③ 解码 |
|---|---|---|---|
| 自家 Ultra HDR JPEG | ✓ | ✓ | **✓ 512x384, peak 999.7** |
| 自家 AVIF 增益图（**修复前**） | ✓ | ✗ | **✗ null** ← 见 §4b |
| 自家 AVIF 增益图（**修复后**） | ✓ | ✗ | **✓ 512x384, peak 999.7** |
| libavif 原生 AVIF | ✓ | ✗ | ✓ 400x300, peak 499.8 |
| **Apple 件** | ✓ | **✗ null** | **✗ null** ← 见 §3 |

> ⚠ ② 对所有 ISO-BMFF 文件都是 null —— `ReadMetadataAsync` 是 exiftool/XMP 路径，**不覆盖 ISO-BMFF**
> （BMFF 的元数据由容器路径 `IsoBmffGainMapProbe` + `ParseIso21496` 直接读，故 ③ 仍成功）。属公开 API 的口径不一致，非产品缺陷。

## 4b. 🟢 **修复：增益图 item 缺 Temporal Delimiter**（本批发现并已修）

**症状**：自家 AVIF 增益图喂回产品 ⇒ `DecodeToLinearRawAsync` 返回 **null**，日志：
```
[probe] 显示尺寸探测无结果：gainmap.obu（ffprobe: [av1_frame_merge] Missing Temporal Delimiter.
        [libdav1d] No sequence header available ...）
[GainMap解码] ❌ 无法获取增益图尺寸
```
**根因（实测字节）**：ffmpeg 的 `avif` muxer 写出的 `av01` item **不带 TD**——

| | 增益图 item 首字节 | OBU 类型 |
|---|---|---|
| 我们（修复前） | `0a` | 1 = OBU_SEQUENCE_HEADER |
| libavif 参考 | `12` | **2 = OBU_TEMPORAL_DELIMITER** |

而 ffmpeg 自己的 `-f obu` 解复用器**要求** TD。**底图不受影响**（底图由 ffmpeg 直接读容器，不经 `-f obu`）⇒ 只有增益图这一路断。

**修法**：`IsoBmffGainMapWriter.Build` 在组装前对增益图 item 补齐 `12 00`（若首 OBU 不是 TD）。
**验证**：解码恢复（`peak 999.7`）；且**三方兼容全部保持**——自家 `11/0` · libavif `Gain map: 256x192 YUV400, Alternate Headroom 2.30 (HDR)` · Google libultrahdr `Ultra HDR Image: Yes`。

## 4c. 📐 **Apple 方案的完整规格**（下一步实现依据）

Apple 的 XMP 只给两个字段（**与标准字段语义不同**，须推导）：

```xml
<HDRGainMap:HDRGainMapVersion>131072</HDRGainMap:HDRGainMapVersion>   <!-- 0x20000 = v2.0 -->
<HDRGainMap:HDRGainMapHeadroom>4.532783</HDRGainMap:HDRGainMapHeadroom>
```

`4.532783` 是 **log2**：`2^4.532783 = 23.1475` —— **精确等于** libultrahdr 报的 `maxContentBoost 23.1475` ✅（可作实现后的对拍锚）。

**映射**（SDR 底图 + 单色增益图的通例）：
```
GainMapMin = 0,  GainMapMax = HDRGainMapHeadroom
Gamma = 1,  OffsetSDR = OffsetHDR = 0
HdrCapacityMin = 0,  HdrCapacityMax = HDRGainMapHeadroom
Channels = 1,  UseBaseColorSpace = true
```

⚠ **实现上的一个已知障碍**：exiftool **不解析** Apple 的 XMP 命名空间（`-G1 -a -s` 只给出
`Apple:HDRGain` / `Apple:HDRHeadroom`，**没有** `XMP-HDRGainMap:*`）⇒ 走 exiftool 的
`ReadMetadataAsync` 拿不到这两个字段。**必须走纯托管的 XMP 原文扫描**
（`JpegContainer.Parse` 已有 APP1 XMP 定位能力），不能指望 exiftool。


---

## 5. 建议（按优先级）

1. **【P1】补 Apple 方案支持**：在 `GainMapDecoder` 增加 `HDRGainMap` / `apdi` 命名空间的读取（`HDRGainMapVersion` / `HDRGainMapHeadroom`），
   并**必须同时补「检测到了但读不出元数据 ⇒ 点名」**（现在 Apple 件正是"看得见、算不出、不说"）。参照 `jpegrutils.cpp:475-476`。
2. **【P1】隔离 §4 疑点**：给 `ServiceProbe` 加一条能直接观测「解码侧是否应用了增益图」的判据（例如对拍 PFM 中间件或断言 `DecodedUltraHdrColorSpace`），
   否则这个洞既无法证实也无法回归。**注意 headless 不回显 `item.Log`，不能靠日志判定。**
3. **【P2】把三种方案的样本纳入门禁**：`apple_gainmap_{new,old}.jpg`、`seine_sdr_gainmap_srgb.jpg`（XMP-only）、`paris_*` 已有样本在库，
   直接加「必须识别 + 必须点名（若不支持）」的正/负断言，成本极低。
4. **【P2】探针 `gainmap` mode 的覆盖面**：它只以 ISO 21496-1 为权威判据，对 XMP-only/Apple 件**必然红** ⇒ 不能当"第三方通用验证器"，
   应在注释与门禁清单里写明适用范围（避免下次误判为产品缺陷）。

---

## 6. 复现命令

```powershell
$env:FFMPEGGUI_FFMPEG_DIR = "C:/PLAN/ffmpegPictureUI/publish/PLAN/ffmpeg-full"
$P = ".\tests\ServiceProbe\bin\Release\net11.0\win-x64\ServiceProbe.exe"

# 三种方案的读侧覆盖（自家 6/0 · Apple 4/2 · XMP-only 3/3）
& $P gainmap tools/src/libultrahdr/tests/data/apple_gainmap_new.jpg
& $P gainmap tools/src/libavif/tests/data/seine_sdr_gainmap_srgb.jpg
& $P gainmap /tmp/aviftest/ctl/src_hdr_pq.jpg

# Google 官方对 Apple 件的判读（Ultra HDR Image: Yes）
& tools/src/libultrahdr/build/Release/ultrahdr_app.exe -m 1 -P -j tools/src/libultrahdr/tests/data/apple_gainmap_new.jpg

# 决定性对拍：剥掉增益图后产物是否变化
& .\publish\PLAN\exiftool\exiftool.exe -MPF:all= -XMP:all= -o stripped.jpg apple_gainmap_new.jpg
# 两份分别经产品转 PNG，比对 SHA256
```

---

*记录时间 2026-10-02 · 编码侧已三路验证正确；解码侧发现 Apple 方案静默丢失（已确证）与自家件未生效（未定论）。*
