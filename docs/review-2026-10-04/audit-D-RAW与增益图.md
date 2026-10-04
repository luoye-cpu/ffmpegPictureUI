# 线 D 审查｜RAW 路径与增益图 / HDR（2026-10-04）

**审查基线**：工作区（HEAD `bdd4b0b` + 未提交改动）。
**审查范围（7 文件）**：`RawService.cs`、`QueueProcessor.cs`（RAW 预处理块 / 增益图段）、`GainMapEncoder.cs`、
`GainMapDecoder.cs`、`IsoBmffGainMapWriter.cs`、`IsoBmffGainMapProbe.cs`、`ColorMapping/GainMapJpegCodec.cs`
（及判定链路上的 `ColorIntentFactory.cs` / `FfmpegCommandBuilder.cs` / `ToneMapping.cs` / `ColorSpaceDescriptor.cs`）。
**取证纪律**：只读。除本报告外未改 `src/`、`tests/`、`docs/` 任何文件；未 `git add` / `commit`；
未运行 `tests/scripts/_run-step3-gates.ps1`（仓库锁）。探针与实跑均在 `%TEMP%`/`/tmp` 与 `tests/output/raw_samples/` 上进行。
**方法**：代码为唯一真值；每条发现附 `file:line` + 失败场景 + 实机证据。已复核 `RAW2TIF_QA_DEFECT_2026-10-03.md`、
`GAINMAP_VENDOR_ANNOTATION_AUDIT_2026-10-02.md`、`GAINMAP_HEIC_AVIF_ANALYSIS.md`、`AVIF_GAINMAP_ENCODER_2026-10-02.md`，
不重复已结案项；对「宣称已修但实未修」者点名。

---

## 0. 结论速览

| 级别 | 条数 | 摘要 |
|---|---|---|
| **P1** | 2 | ①增益图解码 → SDR 重编码：色彩引擎源描述符缺失 ⇒ 直通 ⇒ **不做色调映射**（高光截顶）；②RAW→PNG 默认 HDR 判据用 `BitDepth ?? 8`，与「跟随源」的实际交付位深矛盾 ⇒ 广色域被削 |
| **P2** | 1 | 多通道（Channels=3）增益图的 per-channel 元数据被丢弃，通道 0 范围套用三通道 |
| **P3** | 2 | HLG 名义峰值两处来源（硬编码 1000.0 vs 常量）；灰度增益恒用 BT.709 亮度权重 |
| 合计 | **5** | 7 个提问中 Q1/Q3/Q4/Q6/Q7 已核实无问题；Q2/Q5 各命中一条 |

---

## 1. 发现表

| 级别 | 位置 | 问题 | 为何错（失败场景） | 证据 | 门禁覆盖? |
|---|---|---|---|---|---|
| **P1** | `QueueProcessor.cs:509-511`（只写 `DecodedUltraHdrColorSpace`/`DecodedUltraHdrPeakNits`，**不写** `ColorSourceDecl*`）；`ColorIntentFactory.cs:85`（源描述符只认 `ColorSourceDecl*`）；`FfmpegCommandBuilder.cs:805` | Ultra HDR JPEG / AVIF / HEIC 增益图**解码 → 线性 HDR PFM → SDR 目标**这条路上，色彩引擎的**源描述符缺失**（退到 `AssumeByConvention` = `CodecDefault`），低于闸门 `MinConfidenceForMapping` ⇒ 计划**直通**，**不挂色调映射** | Ultra HDR JPEG / AVIF / HEIC 增益图 → `jpg`/`png`/`webp`（SDR 目标）时，解码出的线性 HDR（`1.0 = 203 nit`，峰值≈1000 nit）被**直接按 sRGB 曲线重编码**、`>1.0` 截顶 ⇒ 高光死白。与「同一 HDR 内容直接（PQ 源）→ jpg」走 Hable 色调映射的结果**不一致**：同一内容两条路 `YAVG` 125.5 vs 134.7。此缺陷与 2026-10-03 已修的 RAW 直通缺陷**形状完全相同**（当时给 RAW 补了 `ColorSourceDecl*`），只是**漏了增益图解码这条路** | 实跑日志（`/tmp/auditD/rt/avif2jpg.log`）：`[色彩引擎] 决策：None … 源描述置信度低（CodecDefault，按惯例假定）→ 不改像素`；命令 `-vf iccgen` 无 tonemap；`signalstats`：直接 `YAVG=125.481`/`YMAX=251`，往返 `YAVG=134.674`/`YMAX=255`（截顶）；直接路径日志 `tonemap=Hable … headroom=49.26` | ❌ **未覆盖**（无门禁断言「增益图解码 → SDR 重编码必须经色调映射」） |
| **P1** | `QueueProcessor.cs:619`（`int rawTargetBitDepth = captured.Options.BitDepth ?? 8;`）+ `:622-623`（`rawCanHdrTransfer`）；对照 `ColorIntentFactory.cs:185`（实际交付位深 `o.BitDepth ?? (meta.bitDepth>8 ? meta.bitDepth : 8)`） | RAW 默认三档的「能否承载 HDR 传递」判据用**用户请求位深 `?? 8`**，而实际交付位深是**跟随源**；对 `HdrRequiresHighBitDepth=true` 的 **PNG/APNG**，产出是 16-bit 却被判 SDR | `ARW → PNG`（未显式 `--bit-depth`）：实产 `rgb48le`（16-bit，`CanHdr=true`，**可**承载 HDR 传递），却被三档③标成 `bt709/iec61966-2-1`（SDR sRGB）⇒ 广色域 RAW 被削到 sRGB。与**代码自己的注释**（`:612`「①容器能承载 HDR 传递（avif / jxl / **png-16bit**…）⇒ Rec.2020 + PQ」）、**文档**（`RAW2TIF_QA_DEFECT_2026-10-03.md:255,280` 同口径）及**日志自述**（`位深=跟随源`）三处自相矛盾。仅当用户显式 `--bit-depth 16` 才得 `bt2020/smpte2084` | 实跑日志：`输出目标 bt709/iec61966-2-1（目标 png：能承载 HDR 传递=False … 位深=跟随源）` + 编码命令 `-pix_fmt rgb48le`；对照 `--bit-depth 16` ⇒ `输出目标 bt2020/smpte2084（…位深=16）` + `-color_trc smpte2084` | ❌ **未覆盖**（三档门禁只测 avif/tiff，未测 png 默认格） |
| **P2** | `GainMapDecoder.cs:768-775` / `:785-799`（循环内**仅 `if (c == 0)` 才赋值**）；`GainMapMetadata`（`:44-49` 为**标量**字段，无 per-channel 数组）；`GainMapDecoder.cs:1198-1211`（RGB 路径对三通道套用同一标量） | 多通道（`Channels=3`）增益图的 **per-channel** `gainMapMin/Max/Gamma/OffsetSdr/OffsetHdr` 被丢弃，**通道 0 的范围被套用到全部三通道** | 失败场景：第三方（libultrahdr 等）写出的三通道增益图若各通道 range 不同（ISO 21496-1 允许），解码后 R/G/B 用了通道 0 的增益区间 ⇒ 偏色。本仓自产恒写相同值（`GainMapEncoder.cs:850-857` 循环写同一份），故**自产往返不受影响**；libavif 官方样本 `seine_*` 三通道也恰相同，故当前**无实样可复现** ⇒ 定为 P2 | 代码：`ParseIso21496` 循环体 `if (c == 0) { meta.GainMapMin = …; }`（`:768/792`）；`ApplyGainMap` RGB 分支 `logBoost = meta.GainMapMin*(1-gv)+meta.GainMapMax*gv`（`:1205`，与 `c` 无关）。权威对照：`avifgainmaputil printmetadata` 显示规范携带 per-channel 值 | ❌ **未覆盖** |
| **P3** | `ColorSpaceDescriptor.cs:123`、`:154`（HLG 名义峰值硬编码字面量 `1000.0`）vs `ColorTransformPlan.cs:378`（用 `Bt2100Ootf.NominalPeakNits`） | HLG「线性 1.0 = 多少 nit」有**两个来源**：具名空间/ICC 路径写字面量 `1000.0`，规划层用常量 `Bt2100Ootf.NominalPeakNits` | 现值相同（均 = 1000，`Bt2100Ootf.cs:26`）⇒ **当前无行为差异**；但 `Bt2100Ootf` 的系统 γ 本就依赖 L_W（`:33,46,55,78`），若常量调整或引入其它 HLG 参考峰，两处会**静默漂移** ⇒ 与本仓「单一真值」纪律相悖 | `Bt2100Ootf.cs:26 NominalPeakNits = 1000.0`；`ColorSpaceDescriptor.cs:123` `… : 1000.0)`；`:154` 同 | N/A（无行为差异） |
| **P3** | `GainMapDecoder.cs:1234`（`BilinearGray` 恒用 BT.709 亮度权重 `0.2126/0.7152/0.0722`） | 灰度增益的亮度提取恒用 **BT.709** 权重，与底图原色无关；`ApplyGainMap` 也不接收底图 primaries ⇒ 底图为 Rec.2020 时无法切换权重 | 失败场景：底图是 **Rec.2020 SDR**（`wideBase=true`）且增益图三通道**非严格中性灰**时，用 BT.709 权重提亮度有偏差 ⇒ 轻微增益误差。增益图若为真中性灰（R=G=B）则无影响 ⇒ 影响面小 | 代码：`return 0.2126f * rgba[off] + 0.7152f * rgba[off+1] + 0.0722f * rgba[off+2];`（`:1234`）；`ApplyGainMap` 签名无 primaries 参数（`:1138-1139`） | ❌ **未覆盖** |

---

## 2. 已核实无问题（逐题）

**Q1 — RAW 默认输出目标三档判据正确；TIFF 例外的 16-bit 在「用户显式指定目标」时也生效、且可被用户覆盖。**
- 三档实装：`ColorPrimaries = (rawCanHdrTransfer || rawIsTiff) ? "bt2020" : "bt709"`；`ColorTrc = rawCanHdrTransfer ? "pq" : "srgb"`（`QueueProcessor.cs:629-630`）。实跑：ARW→AVIF `bt2020/smpte2084`（①）、ARW→TIFF `bt2020/iec61966-2-1`（②）、ARW→GIF `bt709/iec61966-2-1`（③）——三档全部命中。
- TIFF 16-bit 默认写在**用户判定之前**（`:594-596`），实测在「用户显式目标」格也生效：`--color-space sRGB` ⇒ 仍 `rgb48le`（16-bit）；且可覆盖：`--bit-depth 8` ⇒ `rgb24` + `BitDepthReduced` 降级（点名）。位深属容器/中间件保真、与目标色域轴正交，位置正确。

**Q2 — `ColorSourceDecl*`（输入侧）与 `ColorPrimaries/ColorTrc`（输出目标）分离彻底（RAW 路径）。**
- 输入声明**无条件写**（`QueueProcessor.cs:567-568`，在用户判定之外）；输出目标只在 `!rawUserChoseTarget` 时写（`:629-630`）。
- `BuildColorArgsSplit` 的 `ColorSourceDecl*` 分支**排在最前**（`FfmpegCommandBuilder.cs:822-826`），恒优先作 `-i` 前输入声明；`ColorPrimaries/ColorTrc` 归输出侧。实跑日志：`输入声明 bt2020/linear；输出目标 …` 双列分离，未见同一值兼作两用。
- 唯一缺口：**增益图解码路**根本没写 `ColorSourceDecl*`（见发现 1），但那是「缺失」而非「同一值两用」。

**Q3 — ISO-BMFF 增益图结构、tmap 载荷字节布局、`full_range` 只读继承、替代图 CICP 全部正确。**
- `dimg = [ItemTmap][ref_count=2][ItemBase][ItemGainMap]`（`IsoBmffGainMapWriter.cs:235`）；`altr = [group_id=6][num=2][tmap][base]`（`:246`）；`ipma` 1-based 关联、tmap 挂底图 ispe + 替代图 colr（`:117-132`）——探针 `gainmap-isobmff` 对合成件/libavif 官方样本均 **11/0**。
- tmap 载荷 = `u8 version(0)` + ISO 21496-1（`BuildTmapPayload:192-198`）；长度闭合 `1+21+ch×40`（实测 142，ch=3）。
- `ReadNclxFullRange`（`:292-303`）：`Length < 8+11` 前置 + **固定偏移 [18]**（`8+4+2+2+2`）——正确（旧「末字节」写法仅在恰 19 字节时等价）。`PatchColr` 改为**只读继承**底图原 range（`:268-285`）。实跑 `avifgm` **11/0**，产物 `colr.full_range = 0x00`（继承 ffmpeg 实产 tv），替代图 colr 同 range。
- 替代图 CICP：`U16(basePrimaries), U16(alternateTransfer=16=PQ), U16(baseMatrix)`（`:129-130`）——HDR 侧 PQ 正确。

**Q4 — JPEG Ultra HDR 的 APP2 / XMP / MPF 正确，增益图与底图色彩空间一致。**
- APP2 命名空间 `urn:iso:std:iso:ts:21496:-1\0` + ISO 二进制（`InsertIsoIntoSegment:788-801`）；XMP `hdrgm` 含 `UseBaseRenderingColorSpace=True`、`MapPrimary/MapSecondary`、`BaseRenditionIsHDR=False`、`GainMapMin=0`（`BuildXmpMetadata:868-926`）。
- MPF：`[MPF\0][MM TIFF 头][3 IFD 项][2×16 字节 ImageEntry]`，Image0=底图(attr 0x00030000)/Image1=增益图，偏移相对 `'MM'`（`mpfDataStart = baseHeaderLen + app1Total + 8`）——实跑 `gainmap` **6/0**、`ultrahdr` **4/0**（解码峰值 999.7 nit > 203）。
- `useBaseColorSpace` 一致性：`normHdr` 与 `sdrLinear` **两侧同步**乘底图原色矩阵后才算增益（`GainMapEncoder.cs:136-156`），故增益确在底图色域内 ⇒ 与 `flags bit6=1` 一致。

**Q5 — AVIF（av01→OBU）与 HEIC（hvc1→hvcC+Annex-B）解码路径正确；CICP 优先于 EXIF。**
- AVIF：`av01` 走 `-f obu`，缺 TD 时补 `0x12 0x00`（`IsoBmffGainMapWriter.cs:145-146`）；实跑 `avifgm` 11/0。
- HEIC：`hvc1/hev1` 由 `hvcC` 驱动「长度前缀 → Annex-B」（`ToAnnexB` 严格闭合，任一步断裂即 fail-closed 点名）；实跑合成件 `synth_heic_gainmap.heic` **11/0**，伪造件 `fakehevc.heic` **9/2**（点名 `length-prefix chain broken`，非静默）。
- CICP 优先：`GAINMAP_HEIC_AVIF_ANALYSIS.md:24-31` 记录「CICP 才是权威、EXIF `ColorSpace` 仅 CICP 不可用时生效」，与 `GainMapDecoder` 方向判定（`HdrCapacityMin > HdrCapacityMax`）一致；libavif 官方三样本（forward/backward/gammazero）实读 `direction` 与 `gamma` 均正确。

**Q6 — HDR 峰值/白点语义链正确，无 203/100 混淆。**
- `NitsOfLinearOne`：SDR=203 / PQ=10000 / HLG=1000（`ColorSpaceDescriptor.cs:122-124,153-155`）。
- 峰值优先级：**用户 `--color-hdr-peak` > 容器静态元数据（MaxCLL > BS.2086 mastering）> 名义兜底**（`ColorIntentFactory.cs:312-321`）。实跑 `peak` 探针：MaxCLL 600 ⇒ headroom 2.96（6/0）；用户 2000 覆盖元数据 600 ⇒ `intentSource=用户 --color-hdr-peak`（6/0）；BS.2086 4000、名义 10000、整帧实测 988 全部留痕（`f1.out` 9/0）。
- 203 与 100 **是两个量**且分用：`SdrWhiteNits=203`（参考白，headroom 分母）vs `SdrPeakNits=100.0`（仅 BT.2446-1 Method A 的 L_SDR）。`ApplyBt2446A` 用 `SdrWhiteNits` 做值域换算、用 `SdrPeakNits` 传 `ToneMapLuma`（`ToneMapping.cs:139,143`）——**未混用**。

**Q7 — RAW 边界正确；RAW→GIF 历史契约拒绝缺陷确已关闭。**
- RAW→RAW（`arw`→`dng`）：`isDngOutput` 跳过 RAW 预处理（`QueueProcessor.cs:527,536-537`），由 `ProcessDngAsync` 处理；入口守卫 `IsRawFile` 非 RAW 则**点名拒绝**（`QueueProcessor.cs:2861-2867`）——允许 RAW→DNG、拒绝普通图→DNG，正确。
- RAW→GIF：实跑产出带**具名降级**（`BitDepthReduced`/`GamutClipped`/`UnlabeledAssumedSrgb`），**不再被契约 Reject**——与 `ColorIntentFactory.cs:161-163`（`ColorFromRawDefault` 按 `caps.HasColorPath` 决定 `targetExplicit`，GIF 无色彩通路 ⇒ 留非显式 ⇒ 命中「钉成 sRGB」分支）一致。历史缺陷确已关闭。

---

## 3. 未覆盖（诚实边界）

| # | 未覆盖项 | 说明 |
|---|---|---|
| U1 | **发现 1 无门禁** | 现有门禁（`gainmap`/`ultrahdr`/`avifgm`）只验「能否解码出线性 HDR PFM」，**不验**「PFM → SDR 目标是否经色调映射」。建议加一条：Ultra HDR JPEG → jpg，断言引擎计划非 `None`（或对拍「直接 HDR 源 → jpg」的 PSNR/亮度）。 |
| U2 | **发现 2 无门禁** | 三档门禁只覆盖 avif/tiff，未覆盖 `png`/`apng` 默认格（`HdrRequiresHighBitDepth=true` 且位深跟随源）。建议加：ARW→PNG（无 `--bit-depth`）断言 `ColorTrc=pq`。 |
| U3 | **发现 3 无实样** | 仓库内（含 libavif 官方样本）**没有** per-channel 值不同的多通道增益图 ⇒ 无法实机复现偏色，仅由代码+规范推断。 |
| U4 | **真实 HEIC 增益图素材缺失** | 与 `GAINMAP_HEIC_AVIF_ANALYSIS.md:41-45` 自陈一致：HEIC 路径用**合成件**覆盖，不代表真实设备产物语义已验证。 |
| U5 | **未跑 `_run-step3-gates.ps1`** | 硬约束禁止（仓库锁）⇒ 本文所有「已核实」均为定点探针/实跑，非整轮门禁绿。 |

---

## 附：取证命令留痕

```bash
# 环境
export FFMPEGGUI_FFMPEG_DIR=C:/PLAN/ffmpegPictureUI/publish/PLAN/ffmpeg-full
SP=./tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe

# Q3/Q4/Q5：结构 + 兼容性
$SP gainmap        /tmp/auditD/src_hdr_pq.jpg          # 6/0
$SP ultrahdr       /tmp/auditD/src_hdr_pq.jpg          # 4/0（峰值 999.7 nit）
$SP avifgm         /tmp/auditD/src_hdr_pq.avif         # 11/0（full_range 0x00）
$SP gainmap-isobmff tests/data/synth_heic_gainmap.heic # 11/0（hvc1→hevc）
$SP gainmap-isobmff tests/data/synth_heic_gainmap_fakehevc.heic  # 9/2（fail-closed 点名）
$SP gainmap-isobmff tools/src/libavif/tests/data/seine_sdr_gainmap_srgb.avif  # 11/0 forward
$SP gainmap-isobmff tools/src/libavif/tests/data/seine_hdr_gainmap_srgb.avif  # 11/0 backward
./tools/src/libavif/build/Release/avifgainmaputil.exe printmetadata <sample>  # 权威对照

# Q6：峰值链
$SP peak tests/output/validate/peak/hdr10_maxcll.mp4 600        # 6/0
$SP peak tests/output/validate/peak/hdr10_maxcll.mp4 600 2000   # 6/0（用户优先）

# Q1/Q2/Q7：RAW 实跑
FF=./src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe
$FF --headless -i tests/output/raw_samples/Sony_ILCE7S.ARW -o out.tiff -f tiff --log-file t.log
$FF --headless -i tests/output/raw_samples/Sony_ILCE7S.ARW -o out.png  -f png  --log-file p.log
$FF --headless -i tests/output/raw_samples/Sony_ILCE7S.ARW -o out.png  -f png --bit-depth 16 --log-file p16.log

# 发现 1：往返 + 亮度统计
$FF --headless -i /tmp/auditD/src_hdr_pq.avif -o /tmp/auditD/rt/src_hdr_pq.jpg -f jpg --log-file rt/avif2jpg.log
publish/PLAN/ffmpeg-full/ffmpeg.exe -i <jpg> -vf "signalstats,metadata=print:key=lavfi.signalstats.YAVG" -f null -
```
