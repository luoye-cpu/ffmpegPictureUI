# GainMap 容器/标注层审查 · lane B2（HEAD=c18dc4a，只读）

范围：`IsoBmffGainMapWriter.cs`(697) / `IsoBmffGainMapProbe.cs`(690) / `ExifToolService.cs` 增益图相关段 /
`CjpegliService.cs` / `GainMapEncoder.cs` 的 JPEG 容器段（B1 报的两条从容器侧独立复核）。
权威参照 = 本机 checkout：`tools/src/libavif`、`tools/src/libultrahdr`（版本较新，含 `mif3`/`sato`/extended-pixi）。
**本 lane 未跑任何计时**；所有"实测"均为只读解析或 exiftool 读操作。

---

## 0 一页结论

| 级 | 结论 | 标签 | 证据行（文件:行 + 关键字节/码） |
|---|---|---|---|
| P1 | **截断/解析不出 `meta` 的 ISO-BMFF 被报成"确定无增益图"**（`Determinate` 仍为 true），与同一文件里 `Result.Determinate` 的契约相反；写侧同形态是响亮失败 ⇒ 两把尺 | 真缺陷-静默失效 | `IsoBmffGainMapProbe.cs:156-167` `if (!ReadBox(...)) break;` → `if (metaOff < 0) { return r; }`；对照 `IsoBmffGainMapWriter.cs:381` `err = $"{what} 缺 meta 盒"` |
| P1 | **JPEG 侧写的是 Google Ultra HDR 族、但不是参考实现那一版**：hdrgm 全字段写进**主图** XMP，副图**无 XMP**；主图**无** version-only ISO 块 —— 参考实现三处都标 Required | 真缺陷-标准（族内不同版） | `GainMapEncoder.cs:880-935`（BuildXmpMetadata 含全部 hdrgm）；实测产物段表：`image1: APP2 ICC / APP1 XMP / APP2 MPF … image2: SOI APP2 ISO …`（无 image2 APP1）；参照 `jpegrutils.cpp:876`(primary 只 Container:Directory)、`:911`(secondary 才写 hdrgm)、`jpegr.cpp:1074,1088` |
| P2 | **AVIF 增益图 item 未标 hidden**：`infe` flags 恒 0；libavif 对同一 item 写 flags=1；本仓文档自己写明"应为 hidden item" | 真缺陷-标准（影响面未判定） | `IsoBmffGainMapWriter.cs:306` `FullBox("infe", 2, 0, ...)`；`write.c:1949,3396`；实测 libavif 样本 `infe v2 flags1 id3 type[av01]`；`docs/GAINMAP_HEIC_AVIF_ANALYSIS.md:84` |
| P2 | **ipma 把每个属性都标 essential**（libavif 只标 av1C），且 `a>0x7F` 时**静默夹成 0x7F**（丢 essential + 指错属性），不是 fail-closed | 真缺陷-标准(轻) + 界检缺陷 | `IsoBmffGainMapWriter.cs:328` `w.Write((byte)(a > 0x7F ? 0x7F : (a \| 0x80)));`；`write.c:2943`(ispe essential=FALSE) vs `:3024`(av1C TRUE) |
| P2 | **AVIF 写出侧没有任何第二读者判据**：`avifgm③` 用裸子串扫 `nclx`（非盒遍历）且只比 colr；容器结构（item ids / dimg 顺序与计数 / altr / infe flags / ipma 关联）**一条断言都没有** ⇒ B3 的 N7 在容器侧成立，且我能给出具体幻影 | 覆盖缺口（真） | `tests/ServiceProbe/Program.cs:669-676` `n.Count>=3` + `:683-691` FindNclx 子串扫描；`read.c:5725` `if (dimgItemIDs[0] != colorItem->id) continue;`（顺序颠倒 ⇒ libavif 静默当无增益图，我们无判据会红） |
| P3 | 读/写界检不同尺 3 处：probe 的 `meta` 子盒起点硬编 8 字节（不支持 64-bit size 头）、`ReadBig` 返回 int 且只读 4 字节（8 字节 offset/length 静默截高半）、多 `meta` 时写侧取第一个读侧取最后一个 | 真缺陷-静默失效（低可达） | `IsoBmffGainMapProbe.cs:170` `metaOff + 8 + 4`；`:687` `i < 4`；`:160` 无 break；对照 `IsoBmffGainMapWriter.cs:620-626`(hdr=16)、`:691-696`(long/8 字节)、`:380`(break) |
| P3 | probe 注释称 14496-12 的 `SingleItemTypeReferenceBox` **没有** `reference_count` —— 规范与参照都相反：libavif 读侧无条件读、写侧无条件写 | 文档过期（依据错） | `IsoBmffGainMapProbe.cs:403-409`；`read.c:3374-3376` `AVIF_CHECKERR(avifROStreamReadU16(&s, &referenceCount)...)`；`write.c:3440`；实测样本 dimg 字节 `00 02 00 02 00 01 00 03` |
| P3 | 读侧比参考实现宽：接受 `dimg` 只有 1 个目标的 tmap（自家合成件即此形态），libavif 要求恰好 2 且底图第一 | 口径分歧（未判定谁对） | 实测 `synth_heic_gainmap.heic` dimg 字节 `00 02 00 01 00 03`（count=1, to=[3]）；`read.c:5722` |
| P3 | AVIF 增益图产物**不携带任何 Exif/XMP item**（libavif 样本有 item4=Exif、item5=mime XMP + 两条 cdsc） | 能力缺口（未点名） | 实测 libavif 样本 `iinf: 1 av01/2 tmap/3 av01/4 Exif/5 mime`；写出器 `Build()` 只搬两个 av01 item + tmap 载荷 |
| P3 | B1 的两条容器侧复核：①XMP 的 RDF 命名空间 URI 确实拼错（**成立**，但实测 exiftool 13.59 仍完整解析 ⇒ 影响限于严格 RDF 工具链）；②hdrgm 主/副图错位（**成立**，见上 P1） | 真缺陷-标准(轻) | `GainMapEncoder.cs:899` `xmlns:rdf="http://www.w3.org/1999-02-22-rdf-syntax-ns#"` vs `jpegrutils.cpp:886` 正确值；实测 exiftool 对本仓产物读出 `XMP-GContainer DirectoryItemLength:1454` + 全套 `XMP-hdrgm` |
| P3 | 拒绝增益图时的文案仍写"它依赖 JPEG 的 APP2/XMP+MPF 结构"，而 avif(tmap) 已在同一文件里被支持 | 文档过期 | `QueueProcessor.cs:1005`（`gainMapOnUnsupportedTarget`）对比 `:942`（分支已含 `avif`） |
| — | 写侧**未发现**盒字节层错误：ftyp/iinf/iref(dimg)/grpl(altr)/iloc/ipco/ipma 的 size、字段宽度、NUL 终止、entry_count、两遍偏移法均与 libavif 产物逐字段同形（altr 组号 6、实体顺序 [tmap, base]、dimg=[base, gainmap] 都对） | 正向确认 | `IsoBmffGainMapWriter.cs:219-251` vs 实测样本 `grpl/altr 6,2,2,1`、`dimg bytes[00 02 00 02 00 01 00 03]` |
| — | tmap 载荷 = `u8 version(0)` + ISO 21496-1 GainMapMetadata(min_ver2+writer_ver2+flags1+16+ch×40) ⇒ **与 libavif 解析器逐字节兼容**（1 通道 62 B、3 通道 142 B）；flags 位（0x80 多通道 / 0x40 useBaseColorSpace）与 libultrahdr 掩码一致 | 正向确认 | `IsoBmffGainMapWriter.cs:192-198`、`GainMapEncoder.cs:843-872`；`write.c:977-1000` avifGainMapMetadataSize；实测 `tmapPayload=142`、产物 `APP2 len=91 = 2+28+61` |
| 未测 | AVIF 容器的第三方读数（avifdec 本机不存在）、exiftool 写侧对 Ultra HDR 容器的影响（`_exiftool_tmp` 创建被拦）、`--jpeg-gain-map` 后置元数据复制是否替换 Container:Directory、gray 回退 yuv420p 的实际通道标注 | 未判定（§5 逐项写清所需证据） | 见 §5 |

---

## 1 方法与被推翻的假设

**方法**
1. 先 `ls tools/src` 定盘可用参照：`aom / dcraw / dng_sdk / jpegli / jxrlib / libavif / libjxl / libraw / libultrahdr / pngcicp / zlib` ⇒ 容器侧权威取 **libavif/src/{write,read}.c** 与 **libultrahdr/lib/src/{jpegr,jpegrutils,multipictureformat,gainmapmetadata}.cpp**。
2. 逐盒把写侧代码与参照代码对读（不靠文档），再用**只读 perl 盒遍历**打三份真件：
   - `tools/src/libavif/tests/data/seine_sdr_gainmap_srgb.avif`（第三方产物的 tmap 形状）
   - `tests/output/validate/gmisobmff_1993c426/plain.avif`（ffmpeg 裸产 AVIF，属性集合的实测来源）
   - `tests/data/synth_heic_gainmap.heic`（自家合成件）
3. JPEG 侧用 **exiftool 13.59（只读）** 复核真产物 `tests/output/validate/l3_gainmap3p/o_gm_hdr/src_hdr_pq.jpg`（12964 B，v-beta7 期产品输出），并 perl 打段表。
4. 判"不存在"前都做正向命中：例如 `grep -rn "HDRM\|UHDM" tools/src/libultrahdr docs/` **零命中**，对照组 `grep "21496"` 命中 `jpegr.cpp:63` ⇒ 该族在本机参照里不存在。

**被推翻的假设（含我自己的与前人的）**
- ✗ 旧假设「Google Ultra HDR 用 APP1 + `HDRM`/`UHDM` 魔数」——参照与文档零命中；真族 = **APP1 XMP + APP2 MPF + APP2 ISO 21496-1**（`jpegr.cpp:1060-1100` 结构注释逐行）。
- ✗ 旧假设（probe 注释）「ISO 14496-12 的 SingleItemTypeReferenceBox 无 `reference_count`」——`read.c:3375` 无条件读该字段，libavif 写侧 `write.c:3440` 无条件写；样本盒 `size=16` 就是 `from+count+to[2]`。
- ✗ 我进场时的假设「tmap 载荷可能少了 `minimum_version`/`writer_version` 前缀」——实际这两字段在 **GainMapMetadata 内部**（ISO 21496-1 C.2.2），写侧已含 ⇒ 兼容；`IsoBmffGainMapProbe.cs:36` 的 142 B 分解口径正确。
- ✗ 假设「ffmpeg 的 avif 产物可能带 irot/imir 等未支持属性，essential 全标会触发第三方丢 item」——实测 `plain.avif` 的 ipco 只有 `ispe/pixi/av1C/colr`，全是 libavif 已知类型 ⇒ 该风险当前不可达（但界检缺陷仍在，见 §4-F2）。
- ✗ 假设「AVIF 写出侧有第三方判据」——`verify-gainmap-thirdparty.ps1` 只处理 JPEG（`--format jpg`），`verify-gainmap-isobmff.ps1` 的 §1 全部打在 **libavif 自家样本**（读侧），写侧只有 `avifgm③` 的 nclx 子串比对 ⇒ 单向锁成立。

---

## 2 旧主张复验表

| # | 旧主张（出处） | 复验读数 | 裁决 |
|---|---|---|---|
| 1 | 「`dimg` 必须写 `reference_count`，缺它 avifdec 报 `must have exactly 2 entries with distinct ids`」`docs/AVIF_GAINMAP_ENCODER_2026-10-02.md:28` | 报错串在 `read.c:5722` 逐字命中；`write.c:3440` 写该字段；写侧 `IsoBmffGainMapWriter.cs:235` 亦写 ⇒ 与样本字节全等 | **成立**（且规范本就要求，见 #3） |
| 2 | 「`meta` 必须含 `grpl`→`altr`，缺它容器可解但 `avifdec --info` 报 `Gain map: Absent`」同上:29 | 写侧 `:246-247` 有 grpl/altr，组号 6 + 实体 `[tmap, base]` 与实测 libavif 样本 `grpl/altr 6,2,2,1` 一致 | **成立（实现侧一致）**；"缺它报 Absent" 属 avifdec 读数 ⇒ **未复现**（本机无 avifdec，见 §5） |
| 3 | 「`IsoBmffGainMapProbe.ParseIref` 的两种读法对本形态都收敛到正确候选集」`IsoBmffGainMapWriter.cs:234` + `Probe:403-409` | 对规范件（恒带 count）启发式恒正确 ✓；对合成件（count=1→to=[3]）✓；但**规范陈述错**（count 是必需字段，不是"libavif 多写"） | **实现结论成立、依据陈述过期** ⇒ 文档过期(轻) |
| 4 | 「auxiliary image 与 item_id 关联、`colr`/`ispe` 一致性」`Probe:24`/`Analysis:84` | 写侧 tmap 复用底图 ispe（`:122-123`，libavif 同形：`write.c:2936-2943` 用 altImageMetadata 尺寸 = 底图尺寸 ✓）；`altr`/`hidden` 中 **hidden 未实现**（§0 P2）；`colr` 一致性只保证 range 位继承（`:268-285`），**不校验** irot/imir 一致性——libavif 会校验并拒绝（`read.c:5990-5999`），但 ffmpeg 实产无这两属性 ⇒ 当前不可达 | **部分成立**，hidden 一条不成立 |
| 5 | 「`mpf`/`iref`(`rhcc`/`targ`) 关系类型」审查提纲 | 写侧只用 `dimg`；读侧 `ParseIref` 泛收所有类型但只消费 `dimg`。`rhcc`/`targ` 在 libavif 读侧无对应分支（只认 `thmb/auxl/cdsc/dimg/prem`，`read.c:3392-3412`），libultrahdr 侧不适用（JPEG） | **未复现＝本机参照不产出该形态**；对本仓无缺陷判据 ⇒ 记未判定（需 libheif 或 ISO 23008-12 原文） |
| 6 | 「各家标注族：Google(Ultra HDR) / Apple(HDRI) / HEIF `gnra`」`docs/GAINMAP_VENDOR_ANNOTATION_AUDIT_2026-10-02.md:23-58` | 文档 §1 表格逐条可复算（ISO APP2 判据、Apple 的 `HDRGainMap`+`apdi`、Apple 件无 ISO）✓；`gnra` 未在本机任何参照里出现（libultrahdr `heifultrahdr.cpp` 走 tmap） | **成立（族系划分准确）**；`gnra` 一条 ⇒ 未判定 |
| 7 | 「HEAD 实际写的是哪一族的哪一版」 | JPEG = Google 族的 **XMP(主图, 字段超集) + MPF + ISO(副图)** 版；AVIF = libavif tmap 版（缺 hidden / 缺 Exif+XMP item） | 见 §3 |
| 8 | 「读回与写侧同口径」 | JPEG：有 ultrahdr_app + exiftool 两个独立读者（历史 + 我本次 exiftool 复读）✓；AVIF：只有自家 probe + nclx 子串 ⇒ **单向锁成立** | 真（容器侧） |
| 9 | 项目记忆「JXL：ICC 后置无效、EXIF 后置有效」（A5 称未复现） | 容器侧只补一条同形状事实：本机 exiftool.exe **任何写操作**都被拦（实测 `Error creating file: ...\a.jpg_exiftool_tmp`，改跑仓内 `exiftool_files/perl.exe` 同样被拦）⇒ 该记忆的两半在本窗口**都不可复现**，不是否证 | **未判定**（需允许 exiftool 建同目录临时文件的环境） |
| 10 | 项目记忆「ffmpeg 会造悬空缩略指针」 | 同上：写侧被拦，未复现 | **未判定** |
| 11 | 「`--jpeg-gain-map` 后置元数据/缩略图阶段不碰增益图容器」 | 无守卫：`ExifToolService.cs` 全文对"增益图/Ultra HDR/MPF"**零命中**（正向对照：同文件 `MPF` 无、`XMP` 有多处），而 `QueueProcessor.cs:1104-1105` 对所有 exit 0 产物无条件 `RestoreMetadataAsync`，`CopyMetadataSafeAsync` 带 `-XMP:all` 复制（`ExifToolService.cs:658`）⇒ 源自带 XMP（尤其源本身是 Ultra HDR）时会把源的 Container:Directory/hdrgm 装进我们的主图 | **真缺陷-静默失效（可达，字节后果未测）** |
| 12 | B3 的 N7「门禁格子缺口」 | 容器侧给出具名空格与幻影（§0 P2 行 + §4-F6）：现有一切判据在 dimg 顺序颠倒/altr 缺失/infe flags 错 三种改法下都不红，而 libavif 会静默丢增益图 | **B3 判断成立**，且可加强 |

---

## 3 容器逐族核对

### 3.1 ISO-BMFF / AVIF `tmap` 族（写侧逐盒对参照）

| 盒 | 写侧（本仓） | 参照（libavif 实测 + 源码） | 判定 |
|---|---|---|---|
| `ftyp` | `avif`+minor 0+compat{avif,mif1,miaf,MA1A,**tmap**}（`:204-211`） | 实测 `brands=avif,avif,mif1,miaf,MA1A,tmap`；读侧 `read.c:4939` 需要 `tmap` 品牌才解析派生项 | ✓ 一致 |
| `meta` | FullBox v0，子盒顺序 hdlr,pitm,iinf,iref,iprp,grpl,**iloc**（`:251`） | 参照顺序 hdlr,pitm,**iloc**,iinf,iref,iprp,grpl | ✓ 顺序非规范强制 |
| `hdlr` | v0 + predef 0 + `pict` + 12 保留 + **单 NUL**（`:219`，33 B） | 参照 40 B（带名字串） | ✓ 合法（name 可为空串） |
| `pitm` | v0 u16=1（`:220`） | 实测 `pitm ver0 id1` | ✓ |
| `iinf`/`infe` | v0 entry_count u16=3；`infe` **v2 flags=0**、id(u16)+prot(u16)+type(4cc)+NUL（`:222-229,305-306`） | 实测：`infe v2 flags0 id1[Color]`、`flags0 id2[tmap]`、**`flags1 id3[av01]`**、`id4[Exif]`、`id5[mime]` | **flags 不一致（hidden）**；结构一致 |
| `iref`/`dimg` | `from=2, count=2, to=[1,3]`（`:235`） | 实测字节 `00 02 00 02 00 01 00 03`；`write.c:1951-1958` 注释「color 第一、gain map 第二」 | ✓ **逐字节同形**，顺序符合 libavif `read.c:5725` 的硬要求 |
| `grpl`/`altr` | group_id=6, num=2, entity=[2,1]（`:246`） | 实测 `grpl/altr 6,2,2,1` | ✓（含硬编组号 6，规范只要求不与 item_ID 冲突，`14496-12 8.18.3.3`，见 `write.c:3519-3527`） |
| `iloc` | v0，off4/len4/base0/idx0，3 条目，extent=(绝对偏移,长度)（`:332-354`） | 实测 `iloc v0 off4 len4 base0 idx0`；绝对偏移同形 | ✓ |
| `ipco` | 底图属性原样搬 + 增益图属性原样搬 + tmap 追加 colr（`:113-132`） | 实测样本 `ispe\|pixi\|av1C\|colr(base)\|colr(tmap p1 t16 m6 r1)\|colr(gainmap p2 t2 m6 r1)`；`write.c:2930-2943` tmap 用 altImageMetadata | ✓ 形状一致（我方 tmap colr = prim 底图值 + transfer=16 + matrix=底图 + range=继承，见 `:129-130`）；**pixi 对 tmap 可选**（`write.c:2947-2951`）⇒ 我方省略合法 |
| `ipma` | v0 窄索引，**每个 assoc 都置 0x80**（`:308-330`），条目按 1,2,3 递增 | 参照：ispe/pixi/colr essential=FALSE，**只有 av1C TRUE**；条目同样递增（`read.c:3013` 强制递增，我方满足） | **essential 位过度声明**（libavif 容忍；`a1lx` 才禁 essential，我方从不写）+ >127 静默夹值 |
| `mdat` | `ftyp,meta,mdat` 三盒，两遍算长度并有自校（`:149-168`） | 实测同三盒 | ✓ 两遍法与"长度必须一致"的断言是正确防御 |
| 增益图裸流 TD | 首字节非 TD 时前置 `12 00`（`:145-146`） | 实测 libavif 样本增益图起始即 TD | ✓ 合法且必要（OBU 类型判定 `(b>>3)&0xF==2` 正确） |

**净结论**：AVIF 侧的**尺寸字段、NUL 终止、entry_count 宽度、64-bit size 处理（写侧不支持也不需要）、pad/对齐**未发现错；两处与参照的**标注级**偏差（hidden、essential）+ 一处能力缺失（无 Exif/XMP item）。

### 3.2 Google Ultra HDR（JPEG）族

实测产物段表（`l3_gainmap3p/o_gm_hdr/src_hdr_pq.jpg`，12964 B）：
```
image1: SOI | APP2 ICC_PROFILE(600) | …DHT… | APP1 XMP(1371) | APP2 MPF(88) | SOS … EOI(11508)
image2: SOI(11510) | APP2 urn:iso:std:iso:ts:21496:-1(91) | … | SOS … EOI(12962)
```
- MPF 偏移公式与 libultrahdr **逐字同式**：`primary_size - pos - 8`（`GainMapEncoder.cs:745-750` vs `jpegr.cpp:1361`）⇒ 第二读者 exiftool 实测 `MPImage2 Start=11510 Length=1454`，与文件尺寸自洽 ✓。
- MPF 属性位与参照**完全一致**：primary `0x00030000`、secondary `0`（`multipictureformat.h:63-64`：`kMPEntryAttributeTypePrimary=0x030000` / `FormatJpeg=0`）；大端 `MM` = 参照默认（`USE_BIG_ENDIAN_IN_MPF=true`）；总长 86 = `calculateMpfSize()` 同式 ✓。
- **差异 3 处**（都属"同一族不同版"）：
  1. 主图 XMP 写成 hdrgm **字段超集**，参照把字段放**副图**（`jpegrutils.cpp:876` vs `:911`）；
  2. 副图**无 XMP**（参照 `jpegr.cpp:1088` 标 Required）；
  3. 主图**无 version-only ISO 块**（参照 `jpegr.cpp:1074` 标 Required，libultrahdr 只写命名空间 + 4 个零字节）。
  4. 附加：无 `APP0 JFIF`（`EnsureJfifHeaderAsync` 只在元数据阶段命中 JPEG 时补，本产物未补 ⇒ 与参照 `jpegr.cpp:1295` 的"SOI→APP0(JFIF)→…"不同）。
- `Item:Length` = 副图完整长度 ✓ 与参照 `generateXmpForPrimaryImage(secondary_image_length)` 同语义（参照也把它写在**主图** XMP，见 `jpegrutils.cpp:904-906`）。
- RDF 命名空间 URI 拼错（`:899`）⇒ 见 §0/§4-F5。

### 3.3 Apple / HDRI / `gnra` 族（读侧）

`GainMapDecoder.cs:150-160` 的三源优先级（ISO → XMP → Apple HDRGainMap）与 audit 文档 §1/§3b 一致；本 lane 未重复解码侧。HEIF `gnra`：**未判定**（本机参照零命中；`grep -rn "gnra" tools/src/libavif tools/src/libultrahdr` 空，正向对照 `tmap` 命中）。

---

## 4 新发现

**F1（P1）probe 把"读不动"合并进"没有"**
`IsoBmffGainMapProbe.cs:156-167`：顶层盒遍历任何一步 `ReadBox` 失败即 `break`，`metaOff<0` 时**直接 return 未置不确定**的 `Result`（`IsIsoBmff=true, Determinate=true, HasGainMap=false`）。同文件 `:55` 的契约是「`Determinate=false` ⇒ 调用方不得当成没有」，但截断件走的正是 `Determinate=true`。下游 `GainMapDecoder.cs:109/436` 把该结果当"无增益图"，HDR 源于是按 SDR 底解读。写侧同形态是**点名失败**（`IsoBmffGainMapWriter.cs:381/404`）。补：`iinf`/`iloc`/`pitm` 任一缺失也归入同一路径。⇒ 真缺陷-静默失效。修法口径：只有"确认是 ISO-BMFF 且 meta 完整解析后确无 tmap/aux 痕迹"才允许 `HasGainMap=false`；否则 `Determinate=false`。

**F2（P2）写侧 `ipma` 索引夹值把错标变静默**
`IsoBmffGainMapWriter.cs:328`：`a > 0x7F ? 0x7F : (a | 0x80)` —— 属性数 >127 时不是 fail-closed，而是**关联到第 127 号属性**且丢掉 essential 位。参照是断言（`write.c:3505` `AVIF_ASSERT_OR_RETURN(association->property_index <= MAX_PROPERTY_INDEX)`）。可达性低（ffmpeg 单图 4 个属性），但形状与"不允许静默错标"冲突。另：essential 位全置与参照不符（ispe/pixi/colr 参照为 FALSE）。

**F3（P2）AVIF 产物零元数据 item**
参照样本有 `Exif`+`mime(XMP)` 两个 item 及两条 `cdsc` iref；写侧只有 3 个 item ⇒ 转 AVIF 增益图时照片元数据（含拍摄字段）结构性无处安放。是否被 `RestoreMetadataAsync`→exiftool 事后补上**未测**；若补得上，则写侧"不写"只是低效；若补不上（exiftool 对 AVIF item 写入能力，A5 线已疑），则与"隐私/携带"承诺冲突。

**F4（P3）读侧三处与写侧不同尺**（§0 已列，代码事实）：`metaOff+8+4` 硬编盒头 / `ReadBig` 只读 4 字节并返回 int（**8 字节 offset 被截成高 4 字节**，随后靠越界检查兜住 ⇒ 表现为"不确定"而非错图，但 >4 GiB 件的合法 tmap 永远读不到）/ 多 `meta` 取最后一个。另 `ParseIloc` 的 `p += 2`（data_reference_index）与 extent 循环**无界检**，靠外层 `try/catch` 变成"解析异常"（`:296-301`）——不崩但降级为"不知道"。

**F5（P3）RDF URI 拼错的影响面校准**（对 B1 F6 的容器侧裁决）
`GainMapEncoder.cs:899` 的 `http://www.w3.org/1999-02-22-rdf-syntax-ns#` 确为拼错（正确 `…/1999/02/22-rdf-syntax-ns#`，参照 `jpegrutils.cpp:886`）。**但**实测 exiftool 13.59 对本仓真产物完整读出 `XMP-GContainer DirectoryItem{Semantic,Mime,Length}` 与 13 个 `XMP-hdrgm` 字段 ⇒ 该域主流读者按标签名扫描、不校验命名空间 ⇒ B1 的"第三方读不到"这条**过强**；准确表述是"严格 RDF/XMP 工具链与规范校验器会判非法包"。修复成本 1 字符，仍应修。

**F6（P2）AVIF 写出侧的具名门禁空格**（对 B3 N7 的加强）
现能红的只有：`avifgm①②`（PatchColr 反射 + 默认值）、`avifgm③`（端到端 nclx 比对）。缺的断言 = dimg 的 `count/顺序`、altr 存在与实体顺序、infe `flags`、ipma 关联数、item 数量。三者任一被改坏，门禁全绿而 libavif `read.c:5725` 静默丢增益图。另 `Program.cs:683-691` `FindNclx` 是**子串扫描**（"nclx" 若出现在 AV1 负载会误计），`n.Count>=3` 因此可被虚高满足；建议改为按盒遍历（probe 里已有 `ReadBox`，属重复实现而非新能力）。

**F7（P2）元数据后置对 Ultra HDR 无守卫**
`ExifToolService.cs` 全文无增益图/MPF 相关判据（正向对照：`XMP` 命中 10+ 处），而 `QueueProcessor.cs:1104-1105` 无条件跑 `RestoreMetadataAsync`；`CopyMetadataSafeAsync`(`ExifToolService.cs:658`) 的 `-XMP:all` 会把**源**的 XMP 组复制进产物。若源本身是 Ultra HDR（本工具自己的输出再加工即中招），源 Container:Directory 的 `Item:Length` 与 hdrgm 数值会盖掉本次编码写下的（尺寸/降采样/headroom 都可能不同）⇒ 标签与字节两说。字节后果**未测**（exiftool 写侧被拦，§5）。缓解事实：MPF 的 `offset` 是相对 `'MM'` 的差值 ⇒ 主图前部增删字节**不会**破坏副图定位（数学上不变量，`:745-750`），真正风险只在 XMP 内容被替换。

**F8（P3）探针无缓存、幂等但过度取数**
`Probe`/`HasTmapItemFast` 是纯函数（同输入同输出 ✓，失败不缓存但返回 null ⇒ 调用方走 `Indeterminate`，`QueueProcessor.cs:5535-5539` ✓ 保守正确）。但 `HasTmapItemFast`(:118-127) 为拿一个布尔**整档读 + 全 Probe**，其中 `SliceItem` 会复制增益图裸流、HEVC 分支还要做 Annex-B 重建 ⇒ 与后续 `GainMapDecoder` 的再次整档读叠加（同文件被读并解析 2 次）。属性能项，归 C lane；容器侧只记"判据本身无缓存 ⇒ 无陈旧缓存风险"。

**F9（未判定）gray 回退后的通道声明**
`GainMapEncoder.cs:581-586`：单色 AVIF 编不出时退回 `yuv420p`，元数据仍声明 1 通道（`tmap` 的 `is_multichannel=0`）。实测 ffmpeg 单图 AVIF 的 `pixi n3` + `av1C` 决定第三方看到的平面数（`read.c:6045-6060` 用 `av1C.monochrome`）⇒ 声明与实际平面可能两说。已点名（不属静默），但语义正确性需第三方读数。

---

## 5 未证边界（逐项写清"需要什么证据"）

| 未证项 | 现状 | 所需证据 |
|---|---|---|
| AVIF tmap 容器能被第三方读出 | **未测**：本机无 `avifdec`（find 全仓只有 `publish/PLAN/artifacts/avifenc.exe`）；文档 `AVIF_GAINMAP_ENCODER_2026-10-02.md:41` 的 "libavif 1.4.2 avifdec --info … Gain map: 64x48 …" 读数**本机不可复现**（未复现≠否证） | 带 AV1 解码器的 `avifdec.exe`（`avifdec --info` 不需建文件，绕开写拦）或 libheif `heif-info`；跑一次即可同时定 hidden/essential 的实际后果 |
| essential 全置是否让某个真实读者丢 item | 未判定：libavif 只禁 `a1lx`（`read.c:3067-3080`）且实测 ffmpeg 属性全为已知类型 ⇒ 无受害读者样本 | libheif / Android `ImageDecoder` 对同一文件的读数，或一个带未支持属性（如 `rdoc`/自定义 uuid）的 AVIF |
| exiftool 元数据阶段对 Ultra HDR 的字节后果（F7） | **未测**：`exiftool.exe` 与仓内 `exiftool_files/perl.exe` 均报 `Error creating file: …_exiftool_tmp`（实测两次）；bash `cp` 可写，说明拦的是"该 exe 在 Temp 建文件" | 允许 exiftool 建同目录临时文件的环境；跑 `-overwrite_original -Comment=x` + `-TagsFromFile <带 XMP 的源> -XMP:all` 两次，然后读回 MPF/Container:Directory 与副图字节数 |
| 严格 XMP/RDF 校验器对拼错命名空间的裁决（F5） | 未测（exiftool 宽松已实测） | Adobe XMP Toolkit / `libxmp` / `exiftool -validate` 或 rdflib 一次解析读数 |
| `HDRM`/`UHDM` 族是否存在于业界其它版本 | 本机参照**零命中**（对照命中 `urn:iso` 在 `jpegr.cpp:63`）⇒ 就本仓可比对的权威而言该说法不成立 | Android/CTA 的 Ultra HDR 规范文本（本机无） |
| HEIF `gnra` / `rhcc` / `targ` 与增益图的真实关系 | 未判定：本机参照不产出这些类型，libavif 读侧无分支 | ISO 23008-12 原文或 libheif 源码 |
| `verify-gainmap-thirdparty.ps1` 当前能否跑 | **不能**（实测 `src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe` 不存在，脚本 `:33` 设计为 exit 2 拒绝跑 = 响亮）；最后一次成功运行的产物在 `tests/output/validate/l3_gainmap3p/`（Oct 10 00:53），本 lane 复用其真产物做 exiftool 复读 | 重新构建 Release win-x64 后跑一次；顺带可测 §5 第 3 行 |
| `ultrahdr_app.exe -P` 的输出格式（判据 A2 的 `Ultra HDR Image: Yes`） | 未测（exe 在位 `tools/src/libultrahdr/build/Release/ultrahdr_app.exe`，但 `-P` 需产品产物；本 lane 不重跑门禁以免与并发 agent 抢 CPU） | 单次 `ultrahdr_app -m 1 -P -j <l3_gainmap3p 的真产物>` 读数 |
| gray AVIF 是否可用（F9） | 未测 | `ffmpeg -pix_fmt gray -f avif` 单次功能验证（非计时） |

**其他诚实声明**：本 lane 未跑门禁脚本（避免与并发 agent 争 CPU），所以 §0/§4 里所有"某断言会/不会红"都是**代码级推演 + 参照源码事实**，未做变异实验；标 "真缺陷" 者均给出可被变异证伪的具体行号。
