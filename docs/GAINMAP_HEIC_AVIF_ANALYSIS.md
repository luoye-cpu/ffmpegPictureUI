# HEIC / AVIF 的 GainMap 支持现状与规范分析

> 结论日期：2026-09-19/20 · 结论级别：**代码审查 + 真机实测**（非推断）
> 相关：`src/FfmpegGui/Services/GainMapDecoder.cs`、`IsoBmffGainMapProbe.cs`（新增）、
> `Services/ColorMapping/GainMapJpegCodec.cs`、`QueueProcessor.cs`
> 复现素材：仓库内 `tools/src/libavif/tests/data/seine_{hdr,sdr}_gainmap_srgb.avif`（**已入库，稳定夹具**）
> 诊断脚本：`tests/output/_heicavif_probe/isobmff_scan.py`（ISO-BMFF 结构扫描，只读）
> 门禁：`tests/scripts/verify-gainmap-isobmff.ps1`（**52/0**，已接线为第 49 条）
>
> ⚠ 本条原记 **29/0**，系早期读数；2026-10-02 按门禁自述核对（`verify-gainmap-isobmff.ps1:73`
> 明写「断言（共 52 条）：§0 前置 6 · §1 容器层 8 · §1b HEVC 解码路径 11 · …」）已更正。

---

## ★ 修复状态（2026-09-20 已落地）

本文第 0~4 节是**修复前**的现状分析（保留为问题记录）；**下列缺陷已修**：

| 缺陷 | 修复 | 证据 |
|---|---|---|
| **A** 增益图静默丢弃、无点名 | 新增 `IsoBmffGainMapProbe`（解析 `meta`→`iinf`/`iref`/`iloc`/`iprp`）识别 `tmap` 派生项；`GainMapDecoder` 增加 ISO-BMFF 分支（元数据取 `tmap` 载荷、增益图像素走 `ffmpeg -f obu`、底图显式 `-map 0:0 -frames:v 1`） | 门禁 §1/§3；`ServiceProbe gainmap-isobmff` **11/0**；与 libavif `avifgainmaputil printmetadata` **逐字段一致** |
| **A2** HEVC 增益图 item（HEIC 侧） | ✅ **已实现（2026-09-20 下午）**：`hvc1`/`hev1` item 是**长度前缀 NAL**（参数集在 `hvcC`），ffmpeg 的 `-f hevc` 只吃 **Annex-B** ⇒ 新增「**`hvcC` 驱动的长度前缀→Annex-B 转换**」（`IsoBmffGainMapProbe.ToAnnexB`，严格闭合）；**任何一步失败一律 fail-closed + 点名**，绝不给出 `-f hevc` 这个**错命令** | 门禁 **§1b**（11 条：前置/形态 2 + 正向 4 + 反控 1 + 负控 4）；**变异 F `PASS=42 FAIL=5` · 变异 G `PASS=43 FAIL=4` 已实测转红** |
| **B** 静态 GainMap AVIF 被判「动画」（引擎被绕过） | 动画判据从「数流条数」改为「**是否真有流带多帧**」+「**是否增益图容器**」两级（`nb_frames` + `IsoBmffGainMapProbe.HasTmapItemFast`） | 门禁 §2/§3；`verify-avif-anim-probe` **19/0**（锚点与行为均未回归） |
| **C** HDR 底 GainMap AVIF 直接失败 | ① 方向判定改由 **headroom 比较**推出（`base > alternate` ⇒ 底图已是 HDR）⇒ 反向文件**点名**并交回普通 HDR 路径；② **根因**：**CICP 优先于 EXIF/XMP `ColorSpace`**（`ffCicpUsable` 门控） | 门禁 §1/§3/**§3b**；`ServiceProbe color` 正负控双向；HDR 样本 **实跑产出** ✓ |

**C 的根因修复（2026-09-20 第二轮）**：`seine_hdr_gainmap_srgb.avif` 的 **CICP 说 `smpte2084`(PQ)**，
而 **`XMP-exif:ColorSpace` 说 `sRGB`** ⇒ 旧逻辑让 EXIF 覆盖 CICP ⇒ 判「SDR 输入」⇒ **不插 HDR 色调映射链**
⇒ `iccgen` 遇 **PQ 帧**硬失败（`Not yet implemented in FFmpeg`，**零产物**）。
**修法**：能写 CICP 的容器（AVIF/HEIC/JXL/MP4…）里 **CICP 才是权威色彩描述**；EXIF `ColorSpace` 只在
**CICP 不可用**（`unknown`/`unspecified`）时生效 ⇒ 相机 JPEG / PNG（实测 CICP 恒为 `unknown,unknown`）
行为**完全不变**（该分支存在的理由不能一刀切删掉）。修后 HDR 样本 → jpg **实跑产出** ✓。

**反向方向的设计取舍**：底图已是 HDR 时，增益图的用途是生成 **SDR 预览**，本工具不需要 ⇒ 按普通 HDR 输入处理，
不套用增益（照正向套用会产出错图）。该分支**点名**，不静默。

⚠⚠ **范围界定（措辞不得大于扫描面）**：**HEIC 与 AVIF 共用同一条 ISO-BMFF 识别路径**（按 **item 类型**判定，
不看扩展名）；**两条解码路径现在都已实测**（2026-09-20 下午更新）：
- AVIF 增益图：**识别 ✓ + 解码 ✓**（真实素材实测，与 libavif 工具逐字段一致）⇒ `av01` 走 `-f obu`。
- HEIC 增益图：**识别 ✓ + 解码 ✓** —— `hvc1`/`hev1` 走「**`hvcC` 驱动的长度前缀 → Annex-B 转换**」
  （2026-09-20 实现；转换失败一律 fail-closed + 点名，不再给出 `-f hevc` 这个错命令）。
  ⚠ **但覆盖用的是合成件**（`tests/data/synth_heic_gainmap.heic`，生成器
  `tests/scripts/_make-synth-heic-gainmap.py`）—— **真实 HEIC 增益图素材仍然缺失**（搜寻三线全断）。
  ⇒ 覆盖的是**解码路径**，**不代表**拿到了真素材，也**不代表**真实设备产物的**元数据语义**被验证过。
  ⚠ 本节**取代**上面 §A2 表格行里的「fail-closed（未实现解码）」—— 那是 09-20 上午的快照。
⇒ 仍未做：**① 真实 HEIC 增益图素材**（② `hvcC`→Annex-B 转换**已做**）。详见 `docs/HANDOVER.md §17.10`。

---

## 0. 一句话结论（**修复前**的现状快照）

| 问题 | 结论 |
|---|---|
| HEIC **输入**支持吗？ | ✅ **支持**（解码只读，`AppSettings.DecodeOnlyFormats = { "HEIC" }`）。两例真实 HEIC 实测 `→jpg`/`→png` 均成功 |
| HEIC 输入里的 **GainMap** 能识别并正确转换吗？ | ❌ **不能**。`GainMapDecoder` 是**纯 JPEG 容器**解码器，不认识 ISO-BMFF item（`tmap`/`dimg`/`auxl`）⇒ **静默丢弃增益图，且无任何点名** |
| AVIF 输入的 GainMap 能识别吗？ | ❌ **不能**（同上）。且额外踩中两个更严重的缺陷（见 §3） |
| AVIF GainMap 规范 | ISO 21496-1（元数据语义）+ HEIF ISO/IEC 23008-12 的 `tmap` 派生项 + `altr` 实体组；libavif 的具体布局见 §2 |

---

## 1. 为什么识别不了：`GainMapDecoder` 的容器假设

`GainMapDecoder.DecodeToLinearRawAsync` 的第一步是 `JpegContainer.Parse(...)`：

```csharp
// GainMapDecoder.cs:438
if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
```

- HEIC/AVIF 以 `....ftyp` 开头（实测 `00 00 00 18 66 74 79 70 6D 69 66 31`）⇒ 立刻返回 `null`。
- 兜底路径是 exiftool 的 `MPImage2` 标签（`QueueProcessor.cs:2333`）——`MPImage2` 是 **MPF/JPEG** 概念，AVIF/HEIC 里不存在。
- ⇒ `IsUltraHdrJpegAsync` 对 HEIC/AVIF 恒 `false`。

**没有任何代码读取 ISO-BMFF 的 item 结构**（全仓 grep `tmap`/`auxl`/`iref` 零命中，只有注释里出现 `AVIF/HEIF/JXL` 的能力表说明）。

---

## 2. AVIF GainMap 规范（含 libavif 的落地布局）

### 2.1 规范层级

| 层 | 标准 | 规定内容 |
|---|---|---|
| 元数据语义 | **ISO 21496-1**（*Digital photography — Gain map metadata for image conversion*，2025 正式版） | 增益图元数据的**二进制格式与语义**（min/max/gamma/offset/headroom、分母表示法） |
| 容器承载（HEIF 家族） | **ISO/IEC 23008-12 (HEIF)**，`'tmap'` = Tone Map Derived Image Item | AVIF 规范 §4.2.2 明确：`tmap` **as defined in HEIF**；base 与 `tmap` **should** 由 `'altr'` 实体组归组；**gain map item 应为 hidden item** |
| AVIF 自身 | AV1 Image File Format §4.2.2 | **不重复定义** `tmap`，只引用 HEIF + 加两条约束（`altr` 归组 / hidden） |

⚠ AVIF 规范正文**完全不提** ISO 21496-1 —— 编号引用在 "Terms defined by reference" 里。所以「AVIF GainMap 规范」= **ISO 21496-1 的元数据 + HEIF 的 `tmap` 容器**，两者缺一不可。

### 2.2 libavif 的实际布局（实测，权威）

对 `seine_hdr_gainmap_srgb.avif`（122961 B）用 `isobmff_scan.py` 解析：

```
ftyp: major=avif  compatible=[avif, mif1, miaf, MA1A, tmap]      ← 品牌里有 'tmap'

iinf (items):
  item_ID=1  type=av01     ← 底图 (base rendition)，title=Color
  item_ID=2  type=tmap     ← 色调映射派生项，载荷 = ISO 21496-1 元数据（仅 142 B）
  item_ID=3  type=av01     ← 增益图 (gain map)，title=GMap，8-bit 灰度
  item_ID=4  type=Exif
  item_ID=5  type=mime

iref:
  dimg: from 2(tmap) -> [1(底图), 3(增益图)]      ← 关键：tmap 的 dimg 指向两者
  cdsc: from 4(Exif) -> [1]
  cdsc: from 5(mime) -> [1]

iloc:  item 2 的 extent = (122819, 142)   ← tmap 载荷就是 ISO 21496-1 二进制块

ipco:  #1 ispe(400x300)  #2 pixi[10,10,10]  #4 colr nclx prim=1 trc=16 matrix=6 full=1
       #5 clli  #6 pixi[8,8,8]  #7 colr nclx prim=1 trc=13  #8 av1C  #9 colr nclx prim=2 trc=2
grpl/altr: 存在（base 与 tmap 归组）
```

`prim=1`=BT.709，`trc=16`=SMPTE 2084(PQ)，`trc=13`=sRGB ⇒ 该文件**底图是 PQ HDR**，增益图把 HDR 下映射到 SDR。

**对照 `seine_sdr_gainmap_srgb.avif`**：结构完全相同，仅 `#2 pixi[8,8,8]`（底图 8-bit）、`#4 trc=13`（底图 sRGB）⇒ 底图 SDR、增益图 SDR→HDR（**这才是 Ultra HDR 的标准方向**）。

### 2.3 元数据二进制格式（与 JPEG APP2 **同一格式**）

`tmap` 载荷 142 B 的开头：

```
00 00 | 00 00 | 00 | 80 | 00 00 00 0d | 00 00 00 0a | 00 00 00 00 | 00 00 00 01 | ff fc ...
 ^min_ver ^wrt_ver ^flags  ^commonDenom ^baseHeadroomN ^altHeadroomN ^gainMapMinN ^gainMapMaxN
```

⇒ 与 `GainMapDecoder.JpegContainer.ParseIso21496` 解析的 **JPEG APP2 载荷完全同构**
（`min_version u16 / writer_version u16 / flags u8 / 分母 / 每通道 5 个分数`）。
**这意味着解码逻辑可以复用，只需换一个「找到那块字节」的容器定位器。**

flags: `bit7` 多通道(3ch) · `bit6` useBaseColorSpace · `bit3` 公共分母 · `bit2` backwardDirection(HDR 底图)

### 2.4 增益公式（两条容器共用）

```
logBoost   = gainMapMin × (1 − g) + gainMapMax × g      // g = 增益图像素值 0..1
gainFactor = 2^logBoost
HDR_linear = (SDR_linear + offsetSDR) × gainFactor − offsetHDR
```
`gainMapMin/Max` 是 **log2 域**的 headroom，`headroom = hdrPeakNits / sdrWhiteNits`。
（`GainMapDecoder.ApplyGainMap` 已实现，可直接复用。）

### 2.5 ffmpeg 的现状（重要）

- `ffmpeg -h full | grep gain` **只有音频 replaygain / gain 滤镜** ⇒ ffmpeg **完全没有 gain map 概念**。
- mov 解复用器**知道** `tmap` 但**不实现**，只打一行日志：
  `[in#0] Derived Image item of type tmap is not implemented.`
- 它把底图与增益图当作**两条普通视频流**暴露：

```
Stream #0:0  av1  400x300  yuv444p10le(pc, smpte170m/bt709/smpte2084)  title=Color
Stream #0:1  av1  400x300  yuv444p  (pc, smpte170m/unknown/unknown)     title=GMap
```

⇒ **我们既没有 ffmpeg 侧支持可依赖，也不能指望 ffmpeg 帮我们拒绝。** 识别必须自己做（读 item 结构）。

---

## 3. 实测：三个真实缺陷（全部可复现）

素材：`tools/src/libavif/tests/data/seine_{sdr,hdr}_gainmap_srgb.avif`

### 缺陷 A —— 增益图静默丢弃，无点名（与 `gainmap-absent` 同族）

```
$ FfmpegGui.exe --headless --input seine_sdr_gainmap_srgb.avif --output out --format jpg
[1/1] ... → 已完成
[cmd] ffmpeg -y -i "...avif" -threads 0 -q:v 9 -map_metadata -1 -map_chapters -1 \
             -vf iccgen -colorspace bt709 -frames:v 1 "...jpg"
[色彩裁决] 裁决=Proceed
```

- 命令里**没有 `-map`** ⇒ ffmpeg 默认选「最好」的流 = 流 0（底图）。日志 `Stream mapping: Stream #0:0 -> #0:0`。
- **增益图流 1 被丢弃**，用户拿到的是纯 SDR 底图，**没有任何点名**，退出码 0、状态「已完成」。
- 违反项目约定「不支持须**响亮拒绝**」——与刚定案的 `--jpeg-gain-map` 配非 JPEG 目标同属一类。

### 缺陷 B —— 静态 GainMap AVIF 被误判为「动画」（**最严重**）

`IsAnimatedAvifAsync`（`QueueProcessor.cs:4489`）的判据是 **`videoCount >= 2`**：

```csharp
var videoCount = lines.Count(l => ... "video".Equals(...));
return (videoCount >= 2, true);
```

而 GainMap AVIF **恰好就是 2 条视频流**（Color + GMap）⇒ 被判「动画」。

**实测后果（app 自己的点名日志）**：

```
[色彩引擎] ⚠️ 本次由 .avif 专用输入管线 处理，色彩引擎尚未接入该路径（P7 逐条接通）
[色彩引擎] 本次不适用引擎（AnimatedInput：输入为动画，引擎按单帧处理会丢帧）⇒ 由传统管线处理
```

⇒ **色彩引擎被整体绕过**（对所有 GainMap AVIF！），并额外触发：

- `--format gif/webp` 走 **两步法**（实测 dry-run 输出 `[两步法] ffmpeg 分轨提取颜色+alpha → alphamerge`）；
  而 `ProbeAvifStreamRolesAsync` 按「帧数最多=颜色，帧数与颜色相同=alpha」选流 —— 两流**都是 1 帧**
  ⇒ 会把**增益图流当成 alpha 流**（`-map 0:1`）⇒ 产物错。
- `EnableMaxDimension` 预缩放被跳过。

> 与 `#136`「`IsAnimated` 把两条轴混在一起」是同一类：轴 A（输出容器能否多帧）vs 轴 B（输入是否真多帧）。
> 这里又多了第三条轴：**输入有第二条流 ≠ 输入多帧**（增益图/缩略图/alpha 都是第二流）。
> 注：HEIC 也有多流（实测 nokiatech 样本 = 主图 + 缩略图），但 HEIC 不走 avif 动画探测 ⇒ 未受影响。

### 缺陷 C —— HDR 底图的 GainMap AVIF 直接失败（无产物）

```
$ FfmpegGui.exe --headless --input seine_hdr_gainmap_srgb.avif --output out --format jpg
❌ [vf#0:0] Error while filtering: Not yet implemented in FFmpeg, patches welcome
❌ Conversion failed!
═══ 队列处理完毕 — 完成 1 项, 失败 1 项      ← 目录里没有产物
```

**因果链（已逐环实证）**：

1. 该文件带 `[XMP-exif] ColorSpace: sRGB`（源照片 EXIF 残留）。
2. `ProbeInputColorMetadataCore` 让 EXIF/ICC **压过** ffprobe：`hasIccSemantics=true` ⇒
   `colorTrc = iec61966-2-1`，**丢弃** ffprobe 的 `smpte2084`（`FfmpegCommandBuilder.cs:1291-1307 / 1319-1323`）。
3. app 因此判定「输入是 SDR」⇒ **不插入 HDR 色调映射链**。
4. 滤镜链只剩 `format=rgb48le,iccgen`，而帧实际带 **PQ** 传递特性。
5. `iccgen` 对 PQ 帧**硬失败**。

**环 5 的独立证明**（4 例对照，与位深无关，只看传递特性）：

| # | 输入 | 强制 trc | 结果 |
|---|---|---|---|
| H1 | PQ 源 | （自带 PQ） | ❌ `Not yet implemented` |
| H2 | PQ 源 | `bt709` | ✅ 成功 |
| H3 | 8-bit 源 | `smpte2084` | ❌ `Not yet implemented` |
| H4 | 8-bit 源 | （自带 sRGB） | ✅ 成功 |

⇒ **`iccgen` 只要帧的传递特性是 PQ 就失败**，与位深/像素格式无关。

**环 2→5 的因果闭合**（去掉病因 ⇒ 症状消失）：
用 `ffmpeg -map 0:0 -c copy -map_metadata -1` 重封装（同一 PQ 码流、去掉 EXIF/XMP）：

```
改前: -color_trc iec61966-2-1 -i ... -vf format=rgb48le,iccgen            ⇒ ❌ 失败、零产物
改后: -color_trc smpte2084     -i ... -vf format=yuv444p,tonemap=hable:...,
                                          zscale=...,format=rgb48le,iccgen ⇒ ✅ 成功产出
```

⚠ 缺陷 C 的触发条件是「**HDR 码流 + EXIF/XMP `ColorSpace=sRGB`**」，**与增益图无必然关系** ——
任何带该 EXIF 的 HDR AVIF/HEIC 都会踩。增益图文件只是**恰好**常见地带这个标签
（libavif 测试素材即如此；手机/修图软件导出的 HDR 文件保留源 EXIF 也很常见）。

---

## 4. 影响面小结

| 输入 | 现状 | 缺陷 |
|---|---|---|
| 普通 HEIC（单图/多图） | ✅ 正常（实测 2 例） | — |
| HEIC + GainMap（Apple adaptive HDR 等） | 增益图**静默丢弃** | A |
| AVIF SDR 底 + GainMap | 只出 SDR 底图，引擎被绕过 | A + B |
| AVIF → GIF/WebP（同上） | 走两步法、可能把增益图当 alpha | A + B |
| AVIF HDR 底 + GainMap | **直接失败，无产物** | A + B + C |
| 普通 HDR AVIF（带 EXIF ColorSpace=sRGB） | 直接失败，无产物 | C |

---

## 5. 修复建议（按风险从低到高，**均需配门禁 + 变异验证**）

> **状态（2026-09-20）**：**建议 1 / 建议 2 已落地**（见文首「修复状态」表）—— 且实现上比「拒绝」更进一步，
> 走的是**真识别 + 真解码**（正向方向），反向方向才点名交回。
> **建议 3 / 建议 4 未做**（属缺陷 C 的根因，独立于增益图，另行排期）。

### 建议 1（低风险，先做）：识别 + 响亮拒绝 / 点名
新增 `IsoBmffGainMapProbe`（读 `iinf` item 类型 + `iref` `dimg`/`auxl` + `auxC` URN 含 `21496`）：
- 命中 ⇒ **按现有契约响亮拒绝**（item 级 91 + 三条点名），文案指出「本工具链不解 AVIF/HEIC 增益图」；
- 或退一步：至少**点名警告**并说明「已按底图输出、HDR 增益被丢弃」。
- 复用点：`tmap` 载荷与 JPEG APP2 **同格式** ⇒ `ParseIso21496` 可原样复用；
  `ApplyGainMap` 也可复用 ⇒ 若将来要**真支持**，工作量主要在「容器定位 + 增益图 item 解码」。
- 廉价前置信号：`ftyp.compatible_brands` 含 `tmap`（**不足以单独判定**，只作快速筛）。

### 建议 2（中风险）：修 `IsAnimatedAvifAsync` 的判据
把「数流条数」换成「**是否真有流帧数 > 1**」（`-count_frames` 已在 `ProbeAvifStreamRolesAsync` 里用过）。
⚠ **这会动到 `verify-avif-anim-probe.ps1` 钉住的判据** —— 该脚本文件头明确写着
「切片 B **必须**同步改这条锚点（并重取判据），**不得**放宽」。故必须：
① 先读该门禁原文；② 改判据的同时改锚点；③ 补**负控**（GainMap AVIF 必须判「非动画」；真动画 AVIF 仍判「动画」）；④ 变异验证。

### 建议 3（中风险）：EXIF/ICC 不得压过 CICP
对**能写 CICP 的容器**（AVIF/HEIC/JXL/MP4），`hasIccSemantics` 不应覆盖 ffprobe 读到的
`color_primaries/transfer`（CICP 才是权威）；EXIF `ColorSpace` 只应作为 **JPEG/TIFF 等无 CICP 容器**的兜底。
⚠ 该规则被多条色彩门禁覆盖（`verify-color-strategy` / `verify-color-wiring` / `verify-color-engine-xcheck` …）⇒ 改动前须先跑一轮全量门禁取基线。

### 建议 4（独立，低风险）：`iccgen` 遇 PQ 应**响亮失败**而不是抛 ffmpeg 内部串
现状用户看到的是 `Not yet implemented in FFmpeg, patches welcome` —— 无信息量。
可在生成滤镜链前自检「trc 是 PQ/HLG ⇒ 必须已插入 tonemap」，否则点名拒绝。

---

## 6. 复现命令（全部只读/写 scratch）

```bash
# 素材（已在库内，无需下载）
tools/src/libavif/tests/data/seine_sdr_gainmap_srgb.avif
tools/src/libavif/tests/data/seine_hdr_gainmap_srgb.avif

# 结构扫描
python tests/output/_heicavif_probe/isobmff_scan.py <file.avif>

# 行为
FfmpegGui.exe --headless --input <f> --output <dir> --format jpg --log-level debug --log-file <log>
```
