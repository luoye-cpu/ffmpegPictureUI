# Apple `auxC` HEIC 增益图解码：缺口定位与实施方案（草案）

> 状态：草案（2026-11-02，只读调研 + 探针执行；**本批未实施**，`src/` 无对应改动）
> 登记：2026-11-02 由 `_draft_auxc-heic-design.md` 改名为本文件名并登记进 `docs/INDEX.md` §1（引用 0 ⇒ 位置可动）。
> ⚠ **可复现性边界**：§9 的复现命令引用的取数脚本都落在 `tests/output/_tmp/`（gitignore，受 `_sweep-output.py --runs` 管辖）
> ⇒ **不入仓**，只能在 2026-11-02 那台机器/那一轮里跑。要长期复现须先按 AGENTS §1 把用到的探针改写成
> `tests/scripts/_probe-auxc-*.py` 并登记（本批不做）。**本文件的结构层读数（box 树 / `iref` / `ipma` / 瓦片可解性）不依赖那些脚本**，
> 三条 `sha256[:16]` 与字节数在 §7.4 已登记，可对着素材重算核对。
> 素材：`C:\PLAN\gainmap-corpora\toGainMapHDR\DJI_{1_0226_D,1_0616_D,1_0927_D}.heic`（DJI FC8282，2.98/4.54/3.88 MB）
> **不入仓**：用户个人实拍素材，只登记字节数与 `sha256[:16]`（AGENTS §1「语料库在仓外，故意不入仓」）。
> 探针：`tests/projects/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe`（2026-11-02 20:09）
> 临时脚本全部落 `tests/output/_tmp/`（未入 `tests/scripts`，那里平铺且须登记 —— AGENTS §1/§7）

---

## 0. 一句话结论

三件 DJI HEIC 的增益图**结构上就是「`grid` 分块 + 每瓦片 `hvc1`」**，与本仓已打通的 `tmap`+`grid` 路径（参照件 `tests/data/synth_heic_grid_gainmap.heic`，iPhone 形态的合成件）**同构**；
缺的只有三处小口：① 探针把 `auxC` 只「认出」不成「可解态」（无处安放 item id、无 `auxl` 反查）；
② `grid` 载荷共 8 B（`00 00 05 05` + 两个 u16 宽高）**后 4 B 未被读取**（`flags=0` 却写了宽高）
⇒ 整幅算成 6144×4608 而非 6000×4500；
③ Apple 元数据只有 `[0, +headroom]` 一种口径，而 DJI 件是**以 128 为 1.0× 的双向口径**（见 §5）。
瓦片切出 / Annex-B 转换 / 解码拼接三层**全部可直接复用既有函数**，不需要新的解码器。

整体置信：**高**（结构层一手读数齐全）；增益换算口径：**低置信**（无 Apple 官方段落，仅有实测分布 + Apple 自家 JPEG 对照）。

---

## 1. 探针现状读数（原样）

TMP 根：`C:\PLAN\ffmpegPictureUI\tests\output\_tmp\auxc-4729ae12`（AGENTS §4：不钉根则 ffmpeg 子进程写 `%TEMP%` 被拦 `exit -13`）。

### 1.1 `ServiceProbe gainmap-isobmff <file>`（三件完全同形）

```text
isobmff=True brands=heic,mif1,MiHE,miaf,MiHB,heic
determinate=False failReason=[IsoBmff] Apple 早期辅助图形式（auxC URN 含 hdrgainmap，非 ISO 21496-1）⇒ 尚未支持解码；不得按「无增益图」静默走 SDR
hasGainMap=True form=aux-apple-hdrgainmap primary=37 tmap=-1 gainmapItem=-1
codec=- ffmpegFmt=- tmapPayload=-1 gmData=-1
grid=False rows=0 cols=0 gridW=0 gridH=0 tileW=0 tileH=0 tiles=0
PROBE RESULT: pass=2 fail=5   exit=1
```

FAIL 项：`解析确定` / `承载形式为 tmap` / `增益图 item 已定位（实 -1）` / `有已知裸流解复用器（实 -）` / `tmap 载荷已切出`。

### 1.2 `ServiceProbe gainmap-stats <file>`（三件同形）

```text
[GainMap解码] Apple HDRGainMap: headroom=3.482202 (log2)
[GainMap解码] 元数据来源: Apple HDRGainMap XMP (纯托管)
[GainMap解码] 元数据: min=0.000 max=3.482 gamma=1.00 offsetSDR=0.0000 offsetHDR=0.0000 Channels=1 hdrBase=False
[GainMap解码] ❌ 增益图提取失败
stats decoded=null
```

⇒ **元数据侧早就通了**（`TryReadAppleXmpMetadata`，`GainMapDecoder.cs:1223`），断的是「提字节」那一步：
`Form != "tmap"` 使 `GainMapDecoder.cs:310` 的分支整体不进入，落到 `else` 的 MPF/次图切法 ⇒ 无 MPF ⇒ `gmOk=false` ⇒ `GainMapDecoder.cs:363`。

### 1.3 exiftool 侧佐证（`publish/PLAN/exiftool/exiftool.exe -a -G1`）

```text
[QuickTime]     AuxiliaryImageType : urn:com:apple:photo:2020:aux:hdrgainmap
[IFD0]          Make : DJI    Model : FC8282
[Apple]         HDRHeadroom : 0        ← Apple 的 maker note 字段，本件为 0（不可用）
[Apple]         HDRGain     : 0        ← 同上
[XMP-HDRGainMap] HDRGainMapVersion  : 0.2.0.0   (131072 = 0x20000 = v2.0)
[XMP-HDRGainMap] HDRGainMapHeadroom : 3.482202  (log2 ⇒ 2^3.482202 ≈ 11.17×)
```

⚠ 结论：`Apple:HDRHeadroom` / `Apple:HDRGain` 在 DJI 件上**是 0**，唯一有效元数据是 XMP 的 `HDRGainMapHeadroom`。
（这与仓内既有注释一致：exiftool 不解析 `XMP-HDRGainMap` 命名空间 ⇒ 必须走纯托管 `TryReadAppleXmpMetadata`。）

---

## 2. box 树 dump（`DJI_1_0226_D.heic`；另两件仅 mdat 偏移不同）

dump 脚本：`tests/output/_tmp/_dump-auxc-items.py`（ipco 1-based 展开 + 逐 item `ipma` 关联）、
`_dump-auxc-iloc2.py`（按 nibble 打包读 iloc，与 C# `ParseIloc` 同读法）、`_probe-iref-raw.py`（**iref 原始字节**）。
落盘证据：`tests/output/_tmp/auxc-4729ae12/dump_0226.txt`。

```text
ftyp @0     size=36    major=heic compatible=heic,mif1,MiHE,miaf,MiHB,heic
meta @36    size=4667
  hdlr @48    size=33
  dinf @81    size=36
  pitm @117   size=14   primary=37
  iinf @131   size=1671 count=77   {hvc1:72, grid:2, Exif:1, mime:2}
  iref @1802  size=236  （见 §2.1）
  iprp @2038  size=1393 （ipco 10 项 + ipma 74 条，见 §2.2/§2.3）
  idat @3431  size=24   payload 16 B = 0000050517701194 0000050517701194
  iloc @3455  size=1248 ver=1 count=77（见 §2.4）
mdat @4703
```

非瓦片项：`37=grid`（主图）、`38=Exif`、`39=mime`、`76=grid`（增益图）、`77=mime`。

### 2.1 `iref`：原始字节（这一节纠正一处旧误读）

```text
iref size=236 version=0 first16=0000005464696d670025002400010002
dimg size=84 from=37 body=0025 0024 0001 0002 ... 0024   count=0x0024=36  targets=[1..36]
cdsc size=14 from=38 body=0026 0001 0025                count=1  targets=[37]
cdsc size=14 from=39 body=0027 0001 0025                count=1  targets=[37]
dimg size=84 from=76 body=004c 0024 0028 ... 004b        count=0x0024=36  targets=[40..75]
auxl size=14 from=76 body=004c 0001 0025                count=1  targets=[37]
cdsc size=14 from=77 body=004d 0001 004c                count=1  targets=[76]
```

**逐字读法**：每个 `iref` 子盒 = `from_item_ID(u16)` + `reference_count(u16)` + N×`to_item_ID(u16)`。
⇒ `dimg from=37` 与 `dimg from=76` **各有 36 个目标**，恰好等于 6×6。

> ⚠ **更正**：本任务早期的 dump 脚本把 `count` 字段又当成第一个 target 读了一遍，产出「`dimg from=37 n_targets=37 to=[36,1..35]`」
> 与「`auxl from=76 to=[1,37]`」。**原始字节证明**：`auxl` 只有 1 个目标（=37），`dimg` 各 36 个目标，`item 36` 属**色彩网格**（`dimg from=37` 的最后一个瓦片），
> 与增益网格无关。⇒ **不存在「37 目标需收敛」的问题**，`ParseGridGainMap` 的「瓦片数 ≠ rows×cols」判据（`IsoBmffGainMapProbe.cs:806-812`）在本族文件上**原样成立**。

语义：`auxl from=76 to=[37]` = 「item 76 是 item 37（主图 grid）的辅助图」；`cdsc` 是 Exif/mime 的「描述所指向的项」。

### 2.2 `ipco`（1-based）

| # | 属性 | 读数 |
|---|---|---|
| 1 | `colr` | `prof` / `mntr` / `RGB ` + Apple ICC 548 B（`raw=70726f66000002186170706c04...`） |
| 2 | `clli` | size=12（content maxCLL/maxFALL） |
| 3 | `ispe` | 1024×768（瓦片） |
| 4 | `ispe` | 6000×4500（整幅） |
| 5 | `irot` | size=9（angle=0） |
| 6 | `pixi` | 3 ch / [8,8,8] |
| 7 | `pixi` | **1 ch / [8]** ← 增益图 |
| 8 | **`auxC`** | `aux_type='urn:com:apple:photo:2020:aux:hdrgainmap'` |
| 9 | `hvcC` | len=104 `010370000000b00000000000...`（色彩瓦片） |
| 10 | `hvcC` | len=103 `010408000000bfc800000000...`（**增益瓦片**） |

### 2.3 `ipma`（v0，flags=0，1 字节关联，essential=bit7；entries=74）

```text
item 1 / 2 / 36 (hvc1) : *ispe(1024x768); *colr; clli; *hvcC[9]      ← 色彩瓦片（带 colr）
item 37 (grid)         : *colr; clli; ispe(6000x4500); *irot; pixi 3ch
item 40/41/75 (hvc1)   : *ispe(1024x768); *hvcC[10]                  ← 增益瓦片（无 colr/clli）
item 76 (grid)         : ispe(6000x4500); *irot; pixi 1ch; *auxC[8]  ← **auxC 挂在 grid 上，不在瓦片上**
```

⇒ 关键结构判断：**`auxC` 是 grid 派生项的属性，不是瓦片的**。瓦片只继承 `hvcC`。这也是为什么本仓现有的
`ParseIprp`（只置 `sawAuxApple` 布尔）够「认出」，却拿不到「哪个 item」。

### 2.4 `iloc`（ver=1；`bytes[4:6]=4400` ⇒ off=4/len=4/base=0/idx=0，nibble 打包；count=77）

```text
item 1   method=0 base=0 extents=[(9471, 42298)]
item 2   method=0 base=0 extents=[(51769, 30490)]
item 36  method=0 base=0 extents=[(2480891, 90092)]
item 37  method=1 base=0 extents=[(0, 8)]      ← 色彩 grid 载荷在 meta/idat
item 38  method=0 base=0 extents=[(4719, 1180)]
item 39  method=0 base=0 extents=[(5899, 3127)]
item 40  method=0 base=0 extents=[(2570983, 2268)]   ← 0616: 176812；0927: 77609
item 76  method=1 base=0 extents=[(8, 8)]      ← 增益 grid 载荷在 meta/idat
item 77  method=0 base=0 extents=[(9026, 445)]
```

`method=1` ⇒ `SliceItem` 的 `case 1`（`IsoBmffGainMapProbe.cs:734-745`）已经在覆盖范围（2026-11-02 的改动），无需新增。
两件的 grid 载荷偏移恒为 `(0,8)` / `(8,8)`，长度恒 8 B。

### 2.5 `idat` payload（16 B）

```text
0000 0505 1770 1194   0000 0505 1770 1194
ver  flags r-1 c-1  ??  ?? 
```

- `ver=0`、`flags=0x00`、`rows-1=5`（6 行）、`cols-1=5`（6 列）；
- 尾巴 4 B `0x1770 0x1194` = **两个 u16**：6000 / 4500（**按 u32 读 = 393220500，无意义**）；
- 与 ISO/IEC 23008-12 grid v0 冲突：v0 只在 `flags & 1` 时才跟 `u32 outputWidth/u32 outputHeight`。
  ⇒ DJI 在 `flags=0` 下写了 4 字节"16 位语义"的宽高 —— **非标但可判定**（数值与 `ispe` 6000×4500 完全一致）。

现有 `TryParseGridPayload`（`IsoBmffGainMapProbe.cs:886-904`）对 `flags=0` **忽略尾巴**，随后
`if (gw <= 0 || gh <= 0) { gw = cols * tw; gh = rows * th; }`（`IsoBmffGainMapProbe.cs:867`）
⇒ 整幅算成 **6144×4608**。这不报错，但增益图与底图（6000×4500）几何不一致，采样会整体错位 ~2.4%。
⚠ 但 §4.2 证明后果比「2.4% 错位」严重：画外 144 列 / 108 行被编码成 **0**，按 6144×4608 铺满后
0 垫会**进成品**（右侧/下侧暗边），并把 §5.5 的偏低分位判别拉到 0 ⇒ 误判成另一种增益口径。
⇒ C7 是**正确性**修复，不是精度打磨。

### 2.6 XMP（三件相同）

```xml
<HDRGainMap:HDRGainMapVersion>131072</HDRGainMap:HDRGainMapVersion>     <!-- 0x20000 = v2.0 -->
<HDRGainMap:HDRGainMapHeadroom>3.482202</HDRGainMap:HDRGainMapHeadroom> <!-- log2 -->
```

命名空间 `http://ns.apple.com/HDRGainMap/1.0/`。**没有** `GainMapMin/GainMapMax/OffsetSDR/OffsetHDR`。

---

## 3. 与已支持形态的对照

参照件 `tests/data/synth_heic_grid_gainmap.heic`（15,569 B，生成器 `tests/scripts/_make-synth-heic-gridgainmap.py`）：

```text
pitm primary=1；iinf count=7 {hvc1:5, tmap:1, grid:1}；非瓦片：2=tmap, 3=grid
iref: dimg from=2 to=[1,3]（tmap→[主图, grid]）; dimg from=3 to=[4,5,6,7]
ipco: [1] hvcC len=109; [2] ispe 64x48; [3] ispe 128x96
ipma: item 2 (tmap) / item 3 (grid) 均只挂 ispe 128x96
idat payload(154) 内含 ISO 21496-1 参数集；grid 载荷 154 B
```

| 维度 | DJI 实拍（本任务 3 件） | 合成件（iPhone `tmap`+`grid` 形态） |
|---|---|---|
| 增益图「声纳」 | `auxC` URN `…aux:hdrgainmap` + `auxl 76→[37]` | `tmap` 派生项 + `dimg tmap→[底图, grid]` |
| auxC 挂载对象 | **`grid` item 76**（无 hvcC/colr，`pixi` 1ch） | 无 auxC |
| 增益网格 | 6×6，瓦片 1024×768，整幅 6000×4500（**全分辨率**） | 2×2，瓦片 64×48，整幅 128×96（半/四分分辨率） |
| 瓦片编解码 | `hvc1`，`hvcC[10]`（与色彩瓦片 `hvcC[9]` 不同串） | `hvc1`，单 `hvcC` |
| `iloc` | ver=1、4400；瓦片 method=0；两 grid method=1（idat，各 8 B） | ver=1；同理 |
| 元数据 | Apple XMP（headroom=3.482202 / version=131072） | grid 载荷内嵌 ISO 21496-1 |
| grid 载荷 | 8 B（`00 00 05 05` + 2×u16 宽高） | 154 B（含 21496 参数集） |
| 色彩属性 | 主 grid 带 `colr`（Apple ICC 548 B）+ `clli` | 无 `colr` |
| 增益取值 | 127..225，众数 133，均值 135.5 | — |

⇒ **同构度高**：`ParseGridGainMap`（`IsoBmffGainMapProbe.cs:782`）与 `DecodeStitchGainMapTilesAsync`
（`GainMapDecoder.cs:652`）的输入契约（`GainMapTiles` / `GridRows/Cols` / `TileWidth/Height` / `GridWidth/Height` / `GainMapFfmpegFormat`）
对本族文件**逐项可满足**。差异只在「入口怎么找到 grid」与「grid 载荷尾巴」与「元数据口径」。

---

## 4. 瓦片可解性实测（一手）

脚本 `tests/output/_tmp/_probe-auxc-tiles.py`（iloc nibble 读法 + `hvcC` → Annex-B + ffmpeg 单瓦片解码）：

```text
=== DJI_1_0226_D.heic  idat=[3439,3455)
grid item 37: payload(8)=0000050517701194 -> ver=0 flags=00 rows=6 cols=6 trailing u16=6000x4500
grid item 76: payload(8)=0000050517701194 -> ver=0 flags=00 rows=6 cols=6 trailing u16=6000x4500
item 36: lengthSize=4 paramsets=3 annexB=90170 B  decoded 786432 B rc=0   ← 色彩瓦片（底图亮度）
item 40: lengthSize=4 paramsets=3 annexB=2345 B   decoded 786432 B rc=0
item 41: lengthSize=4 paramsets=3 annexB=1889 B   decoded 786432 B rc=0
item 44: lengthSize=4 paramsets=3 annexB=76918 B  decoded 786432 B rc=0
item 75: lengthSize=4 paramsets=3 annexB=456 B    decoded 786432 B rc=0
```

逐瓦片灰度统计（`tests/output/_tmp/_stat-gray.py`，本轮直读落盘的 `.gray`）：

```text
item36 (色彩瓦片) : min=0   p50=61  p99=123 max=153 mean=54.21  mode=0     ← 底图暗部瓦片
item40 (增益)     : min=128 p50=130 p99=131 max=133 mean=130.39 mode=130   ← 平场
item41 (增益)     : min=129 p50=131 p99=131 max=132 mean=130.55 mode=131   ← 平场
item44 (增益)     : min=130 p50=151 p99=204 max=225 mean=155.32 mode=132   ← 高光瓦片，触及 225
item75 (增益, 末块): min=0   p50=133 p99=133 max=134 mean=98.22  mode=133   ← 见 §4.2
```

- `786432 = 1024 × 768 × 1`（8-bit 单通道）⇒ 与 `pixi 1ch/[8]`、`ispe 1024×768` 三方对齐；
- `lengthSize=4`、3 个参数集 ⇒ 走 `TryParseHvcC`（`IsoBmffGainMapProbe.cs:628`）+ `ToAnnexB`（`:667`）即可，
  **不需要新解复用器**：`hvc1` → `GainMapFfmpegFormat="hevc"`（`IsoBmffGainMapProbe.cs:870`）；
- `item 36` 属色彩网格（带 `colr`、`hvcC[9]`），解出来是底图亮度 ⇒ 再次确认增益瓦片 = 40..75。

### 4.1 整幅拼接 + 直方图（`_probe-auxc-mosaic.py`）

```text
DJI_1_0226_D: mosaic 6144x4608 -> crop 6000x4500
  quantiles {1:129, 5:130, 25:133, 50:133, 75:133, 90:142, 99:182} mean 135.47  min 127 max 225
  at<=128: 0.00%   hist(127..140): 17, 716, 1077359, 1812395, 1629642, 672380,
                   17744540, 278087, 215315, 245901, 190281, 122948, 87822, 77211
DJI_1_0616_D: quantiles {1:129, 5:129, 25:133, 50:133, 75:133, 90:135, 99:163} mean 133.83  min 126 max 217
```

⇒ **裁到 6000×4500 之后**取值域 126/127..225、众数 133、**无 0**、无 ≥240（`at<=128` 为 0.00%，即 1.0× 以下几乎不存在）。

### 4.2 画外区域被编码为 0（本轮新发现，影响两件事）

末块 item 75 覆盖 `x∈[5120,6143] × y∈[3840,4607]`，而图只有 6000×4500 ⇒ 画内仅 `x<880, y<660`。实测：

```text
item75.gray zeros=204752（占该块 26.0%）
  nonzero bbox: x 0..879, y 0..663      ← 与 6000x4500 推出的画内窗口 880×660 **逐像素吻合**
```

⇒ 两条硬结论：
1. **整幅尺寸确实是 6000×4500**，由像素边界独立证实（不依赖 `idat` 尾部两个 u16 的解读）；
2. **画外 0 垫必须靠 crop 去掉**。若按 `cols*tw × rows*th = 6144×4608` 出图，垫零会进入成品（暗边），
   而且 **§5.5 的零点判别会把 0 读成「A 口径」**（本族件里 0 只出现在画外垫）。⇒ C7 是正确性问题，不是精度问题。

### 4.3 底图亮度（`_probe-base-lum.py`，解色彩瓦片量 sRGB 亮度 0..255）

```text
item 1 : mean=164.0 p1=156.5 p50=164.2 p95=169.5 max=174.46   ← 天空一片窄带
item 2 : mean=165.5 p1=157.5 p50=166.2 p95=170.5 max=174.46
item 18: mean=154.9 p1=0.0   p50=179.7 p95=189.3 max=195.10
item 36: mean=54.0  p1=0.0   p50=60.5  p95=109.2 max=148.48
```

⇒ 底图是**正常 SDR 分布**（中位 60~180），不是 HDR/PQ 底。这一点是判定增益零点语义的关键（§5）。

---

## 5. 增益换算：三条候选口径与实测判别（**低置信，需真机复核**）

### 5.1 仓内既有的施加公式（一手）

```csharp
// GainMapDecoder.cs:32-34（文件头说明）
//   logBoost = GainMapMin × (1-gain) + GainMapMax × gain
//   HDR_linear = (SDR_linear + OffsetSDR) × gainFactor − OffsetHDR
```

即 `gainFactor = 2^logBoost`，`g = gainmap/255`。这与 ISO 21496-1 的 `gainMapMin/gainMapMax` 线性映射一致。
⇒ **任何口径都只需落到 `(GainMapMin, GainMapMax)` 两个数上，施加端一行不用改。**

现有 Apple 路径给的是（`GainMapDecoder.cs:1266-1279`）：

```csharp
GainMapMin = 0f,
GainMapMax = headroomLog2,
HdrCapacityMin = 0f,
HdrCapacityMax = headroomLog2,
```

### 5.2 三个候选

| 口径 | 公式 | 中点（g≈0.5） | DJI 实测域 127..225 的倍率 |
|---|---|---|---|
| A「0 基」= 仓内现状 | `min=0, max=+h` ⇒ `2^(h·g)` | 3.52× | 3.33× .. 4.20×（g=1 才到 11.17×） |
| B「对称/128 基」 | `min=−h, max=+h` ⇒ `2^(h(2g−1))` | 1.01× | 0.99× .. 6.33× |
| C「正向仿射」（heic.fyi 口径） | `1+(2^h−1)·g` | 6.09× | 6.07× .. 9.97×（g=1 才到 11.17×） |

### 5.3 用实测数据判别

- 底图某亮部瓦片（item 1，实测 p50=164.2 ⇒ 164/255 = 0.643）⇒ 线性 ≈ **0.371**（已是接近 SDR 白的亮部）。
- 该区域增益值 133 ⇒ g=0.5216。
  - **A**：`2^(3.4822×0.5216)=2^1.816=3.52×` ⇒ 0.371×3.52 = **1.31 > 1.0**（需要 1.31× SDR 白）⇒ 整片天空被裁白；
    而全域 p50=133 意味着**中位像素就是 3.5×**，等于整幅过曝 ⇒ 与「底图本身就是正常 SDR 照片」自相矛盾。
  - **C**：`1+10.17×0.5216 = 6.31×` ⇒ 更糟（中位 6.3×）。
  - **B**：`2^(3.4822×0.0431)=1.11×` ⇒ 0.412，合理；全域 max 225 ⇒ **6.34×**，低于声明容量 `2^3.4822=11.17×`
    —— 与本仓 `GainMapDecoder.cs:529-534` 那句「`gainMapMax` 允许大于 `alternateHdrHeadroom`（增益图可覆盖更宽范围）」
    的语义一致（内容峰值 57% 容量）。

⇒ 对 DJI 件，**只有 B 自洽**；A/C 在算术上被否证（不是"更好看"，是整幅过曝）。

### 5.4 但 Apple 自家的 JPEG 增益图是 A 口径（一手对照）

`C:\PLAN\gainmap-corpora\libavif\apple_gainmap_{old,new}.jpg`（libavif / libultrahdr 测试素材，Apple 产），
脚本 `tests/output/_tmp/_probe-apple-jpg-gainhist.py` 解出内嵌增益图（主图 384×512 / 增益图 192×256 灰度）：

```text
soi[1]: (192,256) mode=L n=49152 min=0 p1=0 p50=2 p99=97 max=180
        at0=9368  at1=4284  at127=1  at128=2  at129=8  at255=0
```

⇒ Apple 自家 JPEG 件：**19% 像素恰为 0**、中位 2、无 128 附近堆积 ⇒ **明确是 A 口径（0 = 1.0×）**。

### 5.5 结论与实现取舍

两种口径**都能在真实素材上出现**（Apple JPEG = A；DJI HEIC = B），因此实现**不能写死一种**。
建议：在解码出增益图后按**偏低分位**判别，并把判别结果打进日志（可复核、可门禁）：

```text
p = percentile(gain, 0.005)          // 0.5% 分位
p <= 8  ⇒ 口径 A（min=0,   max=+h）   // Apple 件：p=0
p >= 16 ⇒ 口径 B（min=−h,  max=+h）   // DJI 件：p=129
其余    ⇒ 口径 A + 一行 WARN（不确定但保证不比现状更差）
```

实测两份素材的判别余量极大（0 vs 129），阈值 8/16 落在两者中间且离两侧都远。

**前置条件（必须写进实现）**：判别必须在 **crop 之后**的缓冲区上算。§4.2 证明 DJI 件画外垫 0，
若按 6144×4608 出图，`p0.5% = 0` ⇒ 判别式直接误判成 A 口径（与 C7 是同一个根因）。

**风险登记**：真 iPhone 12–14 的 `auxC` HEIC 若也用 A 口径，判别式会正确给出 A（因为 A 口径件必有 0 值）；
反之若某件恰无 0 值（全画面都有增益）会被误判成 B —— 已知误判面，写进 §8 待证。

> ⚠ 不采用的做法：把 `TryReadAppleXmpMetadata` 的 `GainMapMin` 全局从 0 改成 −h。那会连带改掉
> 已经在跑的 Apple **JPEG** 路径（本仓既有基线），而 §5.4 证明 JPEG 路径必须保持 A。
> ⇒ 口径判别必须发生在「拿到增益图像素之后」，作为**解码期**决策（直接改写 `meta.GainMapMin`，不新增字段；
> 见 §6.2 D4），而不是元数据解析期的常量。

---

## 6. 代码改动点（逐条：文件 : 行号，方法，新增逻辑）

> **行号基准 = `eff6e73`（HEAD）**。注意：本任务只读未改代码，但 `git status` 在 2026-11-02 21:02–21:13 出现了
> **另一路并行改动**（`ImageGeometry.cs` 的 `clap` 裁切 + `GainMapDecoder.cs` 的底图尺寸对账，共 105 行新增，
> 非本任务所为；同时新增未跟踪件 `tests/data/gainmap_hevc_16x16.heic`）。换算关系：`GainMapDecoder.cs`
> 在 `:451` 之后整体下移 **+29**、`:1650` 之后再 **+30** ⇒ 本节对 `GainMapDecoder.cs` 的行号若与工作树不符，
> 按此换算。`IsoBmffGainMapProbe.cs` / `Program.cs`（1231–1268 段）不受影响。

### 6.1 `src/FfmpegGui/Services/IsoBmffGainMapProbe.cs`

| # | 位置 | 现状 | 改动 |
|---|---|---|---|
| C1 | `Result` 类（`GainMapItemId` 等字段旁，约 `:100-170`） | 无 aux 语义字段 | 新增 `public int AuxItemId = -1;`（承载 auxC 的项）、`public int AuxBaseItemId = -1;`（`auxl` 目标 = 被辅助项） |
| C2 | `ParseIprp`（`:524-566`，签名 `ref bool sawAux21496, ref bool sawAuxApple`） | `auxC` 只置布尔 | 改为 `ref List<int> auxCProps`（收集 ipco 里 auxC 的 **1-based 属性索引**）；保留两个布尔做向后兼容判据 |
| C3 | 新增 `ResolveAuxCItems(ipcoProps, ipmaMap, auxCProps)` | 无 | 遍历 `ipma`，把「关联了 auxC 属性的非编解码项」收成 `auxItems`（本族 = `{76}`）；若同时有多个，取**类型为 grid 的**优先，否则第一个并记 WARN |
| C4 | 判定 2b 分支（`:365-372`） | 只 `Form=aux-apple-hdrgainmap; Determinate=false` | 改为：若 `auxItems` 解析出唯一项且其 `infe` 类型 ∈ {`grid`,`hvc1`,`hev1`,`av01`} ⇒ `r.GainMapItemId = auxItemId`；`r.AuxBaseItemId = auxl(from=auxItemId)`；`Form="aux-apple-hdrgainmap"`；然后**走与 tmap-grid 完全相同的下游**（见 C5）。解析不出 ⇒ 保留现有的 `Determinate=false` + 原文案（诚实降级） |
| C5 | 新方法 `ParseAuxAppleGainMap(...)`（或直接复用 `ParseGridGainMap`） | — | 若 item 类型 = `grid` ⇒ 调 `ParseGridGainMap`（`:782`，**零改动复用**）；`Determinate=true` 由其中的成功路径决定 |
| C6 | `ParseGridGainMap` 尾段（`:859-877`） | `gw<=0 \|\| gh<=0 ⇒ cols*tw / rows*th` | 不变（作为兜底），真正修在 C7 |
| C7 | `TryParseGridPayload`（`:886-904`） | `flags&1==0` ⇒ 不读宽高 | 新增容错：`flags&1==0 && p.Length >= q+4` 时，把 **尾部两个 u16** 读成 w/h（`Be16(p,q)`/`Be16(p,q+2)`），仅当 `w>0 && h>0 && w<=rows*tw && h<=rows*th` 才采纳（越界就不采纳，退回 C6 兜底）；命中时在 `Result` 里记 `GridSizeFromPayloadTail=true` 供探针日志点名。**依据**：§4.2 的垫零边界（非零 bbox x 0..879 / y 0..663）独立证实真尺寸 6000×4500；不修则垫 0 入成品且误伤 §5.5 判别 |
| C8 | `SliceItem`（`:722`）、`ToAnnexB`（`:667`）、`TryParseHvcC`（`:628`）、`TryFindHvcC`（`:599`） | 已支持 method 0/1、`lengthSize`、参数集前置 | **不改**（本族实测 `lengthSize=4`、3 参数集、method 1 全在覆盖内） |

新增失败文案统一以 `[IsoBmff] ` 开头（诊断桶约定，见 `:318-320` 注释与 `_probe-cjk-hardcode-scan.ps1` 口径②）。

### 6.2 `src/FfmpegGui/Services/GainMapDecoder.cs`

| # | 位置 | 现状 | 改动 |
|---|---|---|---|
| D1 | `:197` 厂商尾注兜底条件 | `bmff.Form != "tmap"` ⇒ 也问 vendor | 改为 `!(bmff.Form is "tmap" or "aux-apple-hdrgainmap" && bmff.HasGainMap)`；否则 DJI 件会白跑一遍 vendor 探针（无害但与语义不符） |
| D2 | Step 2 提取闸（`:310`） | `if (bmff?.HasGainMap == true && bmff.Form == "tmap")` | 改为 `&& bmff.Form is "tmap" or "aux-apple-hdrgainmap"`；grid 分支（`:312-330`）原样复用（`GainMapIsGrid` 已由 C5 置位） |
| D3 | Step 2 日志（`:327-329`） | 只报 grid 尺寸 | 追加 `form` 与（C7 命中时）`grid 宽高取自载荷尾部 2×u16` 的点名 |
| D4 | `:507-509`（`gmRgb = PlanarToInterleaved(gmBytes, ...)` 之后、`ApplyGainMap(...)` 之前） | 无 | 新增 `ApplyAppleGainOffset(meta, gmBytes, gmPixels, log)`：按 §5.5 的 0.5 % 分位判别（在 `gmBytes` 的 `gbrpf32le` **平面**上取样即可，值域天然 0..1，与 `ApplyGainChannel` 的 `(b/255)` 同口径，见 `:1602`）；判定为 B 时置 `meta.GainMapMin = −H`（写 `GainMapMinC[0..2]`，setter 自动成套，见 `:81`），A 时保持 `0`；打一行 `[GainMap解码] Apple 增益口径: 0 基/对称（p0.5=N）`。**必须放在 `:509` 之前**：`ApplyGainMap` 内部直接读 `meta.GainMapMinC/MaxC`（`:1621`），之后改无效 |
| D5 | `TryReadAppleXmpMetadata`（`:1223-1281`） | 给 `min=0, max=+h` | **不改**（保持 A 为默认；B 由 D4 按像素判别后覆盖），并在注释里写明「口径判别在解码期，见 D4」 |
| D6 | `IsIsoBmffGainMapAsync`（`:754-786`） | `bmff.Form != "tmap"` ⇒ false + 播报 | 改为：`aux-apple-hdrgainmap` 且 `Determinate && (GainMapTiles?.Count > 0)` ⇒ 返回 true（元数据来自 XMP，不走 `ParseIso21496`）；日志点名 `Apple auxC（grid 6x6）` |

> `GainMapMetadata`（`GainMapDecoder.cs:66-121`）无需新字段：`ApplyAppleGainOffset` 只写 `GainMapMin/Max`，
> 施加端 `ApplyGainChannel`（`:1617-1622`，唯一逆式 `logBoost = min·(1−g) + max·g`，`g=(b/255)^(1/γ)`）
> 天然吃得住负 min：`g=0.5` ⇒ `2^0=1`，`g<0.5` ⇒ 衰减 —— 与「对称口径」语义精确对应。
> 另：`ApplyGainMap` 的逻辑尺寸优先取 `meta.MapSecondaryWidth`（`:1544`），Apple 路径不设该字段 ⇒ 走 `gmW`，不受影响。
> 灰度支路走 `BilinearGray`（BT.709 亮度），单通道 Apple 件三通道同值 ⇒ 结果与逐通道一致。

### 6.3 `tests/projects/ServiceProbe/Program.cs`

| # | 位置 | 现状 | 改动 |
|---|---|---|---|
| P1 | `:1261-1268`（`expect == "auxapple"` 块） | 断言 `!Determinate` / `GainMapItemData == null` | 拆成两支：`auxapple`（**旧：只有 auxC、无 auxl/无 grid** ⇒ 仍 `Determinate=false`，保留为负控）与新增 `auxapplegrid`（真形态 ⇒ `Determinate=true`、`GainMapItemId` 命中、`GainMapTiles.Count == rows*cols`、`codec=hvc1`、`ffmpegFmt=hevc`、`AuxBaseItemId == 主图 grid`） |
| P2 | 打印行（`:1251-1258`） | 无 aux 字段 | `gainmapItem=` 后追加 `auxBase=`；`grid=` 行追加 `gridSizeFromTail=` |
| P3 | `usage`（`:1231`） | 缺 `auxapplegrid` | 同步 |

### 6.4 一图流（改动后数据路径）

```text
meta/iprp/ipco[8]=auxC(hdrgainmap)
  └─ ipma: item 76 → auxC  ──► auxItems={76}
       ├─ item 76 类型 = grid ──► ParseGridGainMap(:782)   [零改动]
       │     ├─ SliceItem(76, method=1, idat (8,8))        [零改动]
       │     ├─ TryParseGridPayload → 6×6 + 尾部 u16 6000×4500  [C7]
       │     ├─ dimg 76→[40..75] = 36 块（== 6×6）          [零改动]
       │     └─ 每块 hvcC[10] → ToAnnexB → GainMapTiles     [零改动]
       └─ auxl 76→[37] ⇒ AuxBaseItemId=37（主图 grid）      [C4]
GainMapDecoder: Form 闸放宽 [D2] → gmGrid 分支 → DecodeStitchGainMapTilesAsync(:652) [零改动]
              → 增益口径判别 [D4] → Step 5 施加（公式不变）
```

---

## 7. 验收判据草案（`tests/scripts/verify-gainmap-isobmff.ps1` 新增 §1e + 同批改动）

### 7.1 必须同批改的既有断言（否则新改动一来就红）

1. `:424-426` §1c 两条断言钉的是 `determinate=False` / `gmData=-1`。**新分支后**：
   - 若 §1c 的夹具仍造「只有 `auxC`、无 `auxl`、无 grid」（当前 `New-AuxFixture` 就是），`Determinate` 仍应为 false
     ⇒ 断言可保留，但文案要改成「**只有 auxC 无法定位 item** ⇒ 仍不可解」；
   - 新增 `auxapplegrid` 夹具覆盖可解形态。
2. 文件头条数散文（`:83-89`，现「共 92 条」）+ `§1c Apple auxC 识别 4` ⇒ 加 `§1e 7`；
3. 变异登记表（`:21-49`）追加 2 条变异（见 7.3）。

### 7.2 新增 §1e（7 条断言，期望值全部来自本任务实测/合成件实测）

夹具 `tests/data/synth_heic_auxc_gainmap.heic`（见 §7.4）：2×2 瓦片 64×48，主 grid 128×96，**grid 载荷声明 120×90**
（这样「尾部 u16」与「cols×tw」两种读法给出不同答案，判据才有牙）。

| # | 断言 | 期望 |
|---|---|---|
| 1 | `gainmap-isobmff <auxc件> auxapplegrid` 的 `PROBE RESULT` | `pass=<N> fail=0` |
| 2 | 形态 | `hasGainMap=True form=aux-apple-hdrgainmap determinate=True` |
| 3 | 定位 | `gainmapItem=3`（auxC 所在 grid）、`auxBase=1`（`auxl` 目标）、`codec=hvc1 ffmpegFmt=hevc` |
| 4 | 网格 | `grid=True rows=2 cols=2 tileW=64 tileH=48 tiles=4` **且** `gridW=120 gridH=90`（**若 C7 未实现则为 128×96 ⇒ 本条转红**，正是要钉的那颗牙） |
| 5 | 端到端 `gainmap-stats` | `decoded!=null`；日志含 `增益口径: 对称`（夹具的增益图做成 128 附近起始，p0.5≈128）；`峰值 ≈ 2^headroom`（容差 5%） |
| 6 | 无垫零（§4.2 那颗牙的另一面） | 产物增益图尺寸 = `120×90`，且 `p0.5% >= 16`（证明画外垫被裁掉；未裁则 p0 会被 0 拉低） |
| 7 | 负控（有牙） | 同结构但**删掉 `auxl`** ⇒ `determinate=False` 且 `failReason` 点名 `auxl`（证明"能解"不是恒真） |

另加 1 条回归：`synth_heic_grid_gainmap.heic`（tmap 形态）在改动后 §1d 全部仍绿 —— 证明放宽 Form 闸没伤 tmap 路径。

### 7.3 变异验证（每颗牙都要能被拔掉）

- **变异 J**：把 C7 的「尾部 u16」分支注释掉 ⇒ §1e-4 与 §1e-6 必须转红（`gridW=128` + 产物 128×96），其余仍绿；
- **变异 K**：把 C4 的 `auxl` 反查改成返回 −1 ⇒ §1e-3 与 §1e-7 转红；
- 两次都记录 `PASS=/FAIL=` 实测数（本仓规矩：新断言无变异验证不算数，见 `:21-49`）。

⚠ 现基线 `PASS=84 FAIL=8`（`:45` 变异 I 那条记录）—— 新增 §1e 后基线数字要同批更新，并重跑整条门禁核数。

### 7.4 夹具策略

**可以做，且推荐自造**（复用 `_make-synth-heic-gridgainmap.py` 的既有骨架）：

- 该脚本已经会：`ffmpeg` 编 64×48 HEVC 瓦片、拼 `ftyp/meta(pitm,iinf,iref,iprp/idat,iloc)`、写 `iloc v1`。
  新增只需三件事：① `ipco` 加一个 `auxC`（URN 串 46 B）；② `iref` 增 `auxl from=增益grid to=[主grid]`；
  ③ `ipma` 把 auxC 挂到增益 grid 项、且**不**给它 `colr`。
- 体积：≈ 16–18 KB（现有 grid 件 15,569 B ⇒ 加 46 B 后同量级）。
- 许可：全部自造字节，**无第三方素材**，直接入 `tests/data/`（与现有三个 `synth_heic_*` 同待遇）。
- 需两个变体：`synth_heic_auxc_gainmap.heic`（正向）与 `synth_heic_auxc_noauxl.heic`（负控，删 `auxl`）；
  生成器统一 `tests/scripts/_make-synth-heic-auxc.py`，并在门禁 §0 做「夹具齐备」自检（缺则 fail-closed，不静默跳）。

**DJI 实拍件不入仓**：3 件共 11.41 MB、DJI 版权、且是用户个人素材（口径同 `verify-gainmap-isobmff.ps1` 文件头 §3d/§3e 对真实件的处理）。
替代做法：在文档登记指纹与读数（本文件 §1/§2/§4 即证据），必要时提供可选的 `-CorpusRoot` 手跑臂（不入整轮门禁）。

```text
DJI_1_0226_D.heic  2,983,708 B  sha256[:16]=B486C86B1BA38789
eff6e73 2026-11-02
DJI_1_0616_D.heic  4,544,334 B  sha256[:16]=25BAE5246E74995F
DJI_1_0927_D.heic  3,878,638 B  sha256[:16]=6E3F8F497BEAF5D0
```

---

## 8. 待证项 / 风险

1. **Apple 官方公式仍无一手段落**：`developer.apple.com/documentation/appkit/applying-apple-hdr-effect-to-your-photos` 正文由 JS 渲染，`fetch_content` 取回空正文 ⇒ 只能到二手（heic.fyi）+ 一手代码片段（gist）+ 自家 JPEG 实证。
2. **真 iPhone 12–14 的 `auxC` HEIC 未拿到**（仓内/语料库 0 件）：判别式（§5.5）在「全画面都有增益、无 0 值」的 A 口径件上会误判为 B。缓解：日志点名 + 真机件到位后加一条门禁臂。
   （已确认的部分：Apple 自家 **JPEG** 件（libavif/libultrahdr 测试素材）确实是 A 口径，§5.4。）
3. **DJI 的 grid v0 非标写法**：`flags=0` 却带宽高。C7 的容错是否为"过度宽容"（会不会把合法 v0 件的垃圾尾巴读成尺寸）？
   缓解：只在 `w<=rows*tw && h<=rows*th` 时采纳，且探针日志显式点名来源。
4. **`Apple:HDRHeadroom=0`**：DJI 的 maker note 字段为 0，若某些件 XMP 也缺 headroom，会走 `TryReadAppleXmpMetadata` 的 v1.0 隐式 8× 兜底（`:1253-1263`）—— 对 DJI 是否成立未知。
5. **`irot`/`clli` 未参与几何**：主 grid 带 `irot`（angle=0），若真机件 angle≠0，底图与增益图对齐需另行核对（本族 3 件角均为 0，暂不处理）。
6. **未做渲染色验证**：本任务只到「解出增益图 + 算倍率」，没有把 DJI 件跑一遍完整转换去目视检查 HDR 输出（需改 `src/` 才能跑，本轮禁止）。⇒ 口径 B 的"自洽"是算术论据，不是视觉背书。

### 证据等级对照（供复核）

| 结论 | 等级 | 依据 |
|---|---|---|
| 三件探针现状 `pass=2 fail=5`、`form=aux-apple-hdrgainmap` | **确定** | 本轮 ServiceProbe 重跑（§1.1），`eff6e73` |
| `auxC` 挂在 grid item 76、`auxl 76→37`、瓦片 = 40..75（36 块） | **确定** | `iref` 原始字节 §2.1 + `_probe-iref-raw.py` |
| 真尺寸 6000×4500、画外垫 0 | **确定** | §4.2 像素边界与 `ispe` 双向吻合 |
| 瓦片可解（`hvc1` → Annex-B → ffmpeg，每块 786432 B） | **确定** | §4 实测 rc=0 |
| 缺口只有 C1–C7 / D1–D6 那三类别 | **高置信** | 代码逐行读过（§6）+ 结构 dump 同构对比（§3） |
| Apple 增益口径 = 对称（min=−h） | **低置信** | 仅靠算术反证 A/C（§5.3）+ 无官方段落；需真机件复核 |
| Apple JPEG = 0 基 | **高置信** | §5.4 一手直方图（19% 像素恰为 0） |

---

## 9. 复现命令

```powershell
$env:TEMP=$env:TMP=$env:TMPDIR='C:\PLAN\ffmpegPictureUI\tests\output\_tmp\auxc-<guid>'
$sp = 'tests\projects\ServiceProbe\bin\Release\net11.0\win-x64\ServiceProbe.exe'
& $sp gainmap-isobmff 'C:\PLAN\gainmap-corpora\toGainMapHDR\DJI_1_0226_D.heic'
& $sp gainmap-stats   'C:\PLAN\gainmap-corpora\toGainMapHDR\DJI_1_0226_D.heic'

python tests/output/_tmp/_probe-iref-raw.py            'C:\PLAN\gainmap-corpora\toGainMapHDR\DJI_1_0226_D.heic'
python tests/output/_tmp/_dump-auxc-items.py           'C:\PLAN\gainmap-corpora\toGainMapHDR\DJI_1_0226_D.heic'
python tests/output/_tmp/_dump-auxc-iloc2.py           'C:\PLAN\gainmap-corpora\toGainMapHDR\DJI_1_0226_D.heic'
python tests/output/_tmp/_probe-auxc-tiles.py          'C:\PLAN\gainmap-corpora\toGainMapHDR\DJI_1_0226_D.heic' <tmp>
python tests/output/_tmp/_probe-auxc-mosaic.py         <tmp> 'C:\PLAN\gainmap-corpora\toGainMapHDR\DJI_1_0226_D.heic'
python tests/output/_tmp/_probe-apple-jpg-gainhist.py  'C:\PLAN\gainmap-corpora\libavif\apple_gainmap_old.jpg'
python tests/output/_tmp/_stat-gray.py <tmp>\item75.gray            # 逐瓦片统计 / 垫零取证
```

改动落地后的验收（AGENTS §4）：`dotnet build ffmpegPictureUI.sln -c Release` +
`pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1`（**整轮**，裸跑单条仅调试用）。
