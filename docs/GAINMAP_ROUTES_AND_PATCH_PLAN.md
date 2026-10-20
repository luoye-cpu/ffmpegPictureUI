# GainMap 各代实现差异与本仓修补方案（2026-10-20）

本档只写**办法**：规范怎么规定的（一手出处见 §1–§3 的行内引用）、我们差在哪、逐条怎么改、每条改完用什么判据验收。
讨论性内容、撤回与未证边界在 `docs/FIX_PLAN_2026-10-14.md` 的对应账目里。

## 0. 结论摘要

1. **读侧最大的洞在 Apple 早期 HEIF 辅助图**：我们的探测判据只认 `auxC` 的 URN 里含 `21496`
   （[IsoBmffGainMapProbe.cs:470](file:///c:/PLAN/ffmpegPictureUI/src/FfmpegGui/Services/IsoBmffGainMapProbe.cs#L470)），
   而 iPhone 12 起的 HDR Gain Map 用的 URN 是 `urn:com:apple:photo:2020:aux:hdrgainmap`，**不含 21496**
   ⇒ 这类文件被报成「无增益图」，静默按 SDR 走。这是"检测不到"而不是"检测到了不支持"，属于可用性不诚实。
2. **ISO 21496-1 的二进制布局我们读写两侧都与参考实现一致**（flags 位、公共分母、1/3 通道、逐通道数组），
   逐条对齐见 §1 —— 这块不用改。
3. **XMP 侧两处我上一轮怀疑的"不一致"被一手证据洗清**：参考实现自己就写 `HDRCapacityMin="0"`、
   `kJpegrVersion = "1.0"` ⇒ 我们与参考实现同形，不改。
4. **写侧能力比生态窄**：Android API 36 已有 `HEIC_ULTRAHDR`（HEIF + ISO/CD 21496-1），iOS 18 起相机写
   Adaptive HDR HEIC，MediaInfo 25.07 已能解析 **PNG/HEIF** 里的 ISO 21496-1 增益图元数据；
   我们只写 JPEG(MPF+XMP+ISO) 与 AVIF(tmap)，HEIC 明确无写通路（本机实测 ffmpeg 无 heif muxer、无 heifenc）。
5. **AVIF 的增益图在参考库里仍是实验特性**（libavif 1.1.0 起 `AVIF_ENABLE_EXPERIMENTAL_GAIN_MAP`，
   官方 changelog 自述"Files created now might not decode in a future version"）
   ⇒ 我们自写 tmap 的正确性只能靠"常驻第三方读者判据"盯住，这条已经落地（见 §4 P1-1）。
6. **"标记方法"已按维护者裁决做成运行期三档**（`iso` 默认 / `xmp` / `both`，§6.4）：参考实现是编译期开关、
   上游默认 ISO-only；`xmp` 与三通道**不互斥**（hdrgm 用单值表达"适用于全部通道"，本实现逐通道参数恒相同）。
   落地时一并修了 §4 P0-3（底图 ISO 裸桩）、P0-4（次图 XMP）与 P0-5（字段集/序列化形式）。

## 1. 字段对照：ISO 21496-1 二进制元数据

参考实现 = 本机 `tools/src/libultrahdr`（一手代码，不是转述）。

| 语义 | ISO 二进制（libultrahdr 写侧） | 我们读侧 | 我们写侧 | 判定 |
| --- | --- | --- | --- | --- |
| 版本 | `min_version u16` + `writer_version u16`，读侧要求 `min_version==0`（`gainmapmetadata.cpp:123-125, 206-217`） | `ParseIso21496` 同样要求 `minVersion==0` 否则返回 null | 同 | 一致 |
| 通道数 | `flags bit7 = 多通道`，`channelCount = ((flags&0x80)!=0)*2+1` ⇒ **1 或 3**（`gainmapmetadata.h:20`、`gainmapmetadata.cpp:221`） | `(flags & 0x80) != 0 ? 3 : 1` | 按 `declaredChannels` | 一致 |
| 用底图色彩空间 | `flags bit6`（`kUseBaseColorSpaceMask = 1u<<6`） | `(flags & 0x40) != 0` → `UseBaseColorSpace` | 恒置 | 一致 |
| 方向（base 是 HDR） | `flags bit2 = backwardDirection`（`gainmapmetadata.cpp:139-140, 231`） | `(flags & 0x04) != 0` → `BaseRenditionIsHdr` | 从不写（只写 SDR base） | 一致（写侧是能力选择，不是偏差） |
| 公共分母 | `flags bit3 = useCommonDenominator` ⇒ 先 1 个 u32 分母，其后每个量只写分子（`gainmapmetadata.cpp:143-159, 160-170`） | **两条分支都实现了**（`commonDenom` 时 `p + 12 + channels*20`，否则 `p + 16 + channels*40`） | 写独立分母形式（合法） | 一致 |
| headroom / min / max / gamma / 两侧 offset | 全部是**有理数** `N/D`（s32/u32 大端），顺序 = baseHdrHeadroom、alternateHdrHeadroom、逐通道(min,max,gamma,baseOffset,alternateOffset) | 同序解析，逐通道落数组（F-8 修过"只留通道 0"的旧写法） | 同 | 一致 |
| 施加空间 | 标准术语 `gain map application space` = **线性 RGB**（ISO 21496-1:2025 术语页） | 解码走 `RGB_D65_202_Rel_Lin` 等线性色彩空间名后施加 | 同 | 一致 |

**规范自身的未定项**（不是我们的偏差，但要在设计时避开）：libultrahdr 在 `gainmapmetadata.cpp:127-131`
留了 TODO —— 草案说通道数指"增益图的通道数"，但色调映射在 RGB 空间做，灰度增益图实际也常有 3 组参数。
⇒ 我们**读**时按 flags 推 1/3、**写**时按 `declaredChannels` 与交付平面数成套复核（写出器复核 pix_fmt），
不要再加"第三种解释"。

## 2. 容器关联机制矩阵

| 承载 | 关联方式 | 元数据放哪 | 我们 |
| --- | --- | --- | --- |
| JPEG Ultra HDR | `APP1 XMP` + `APP2 MPF` + 副图；GContainer 可**不带 MPF**（MediaInfo 25.07 明确支持两种） | XMP `hdrgm:*` + 副图上的 `APP2 urn:iso:std:iso:ts:21496:-1` | ✅ 写 MPF+XMP+ISO；读 MPF/ISO/Apple XMP |
| HEIC / HEIF（标准） | `tmap` 派生项 + `dimg` 引用 | `tmap` 载荷 = `u8 version` + ISO 元数据 | ✅ 读（`form=tmap`，含 hvc1/av01）；写侧 HEIC 无通路 |
| HEIC / HEIF（ISO 草案辅助图） | `ipco/auxC`，URN 含 `21496` | 随辅助图 | ⚠ 识别为 `aux-21496`，**不解码**（`Determinate=false`） |
| HEIC / HEIF（Apple 早期） | `ipco/auxC`，URN = `urn:com:apple:photo:2020:aux:hdrgainmap` | Apple 私有（MakerApple 33 `HDRGainMapVersion` / 48 `DisplayHeadroom`，或 XMP `ns.apple.com/HDRGainMap/1.0/`） | ❌ **完全不识别** ⇒ 报"无增益图"（P0-1） |
| 纯 track 型 HEIC（mov 包装） | 无 `meta` ⇒ 无 item 结构 | — | ✅ 明确"无 item 结构"，不猜（`IsoBmffGainMapProbe.cs:173`） |
| AVIF | `tmap` 派生项（libavif 侧为实验特性） | 同 ISO | ✅ 写 + 读；§4 P1-1 已钉第三方读者判据 |
| PNG / JXL | 生态已有实现（MediaInfo 解析 PNG 的 ISO 元数据；ACR 可给 JXL/PNG 嵌增益图，base 可为 SDR 或 HDR） | ISO 元数据 | ❌ 未支持（登记，不擅自写，见 P2-1） |

## 3. 读侧生态矩阵（决定"我们写出去的东西谁能看懂"）

| 环境 | 能读什么 |
| --- | --- |
| iOS 18+ / macOS 15+ | Ultra HDR（JPEG）+ Adaptive HDR（ISO 形态 HEIC）；<18 只认 Apple 协议 |
| Android 14+ | Ultra HDR JPEG；API 36 起 `ImageFormat.HEIC_ULTRAHDR` = HEIF(ISO/IEC 23008-12:2024) + ISO/CD 21496-1 |
| Safari | JPG 支持 ISO 与 legacy Android/Apple 三种编码；AVIF 带/不带 ISO 增益图；HEIF 支持 Apple 增益图；JXL 不支持增益图；PNG 走 PQ |
| 工具 | MediaInfo 25.07 解析 PNG/HEIF 的 ISO 21496-1 与 XMP（Adobe/UltraHDR）增益图；exiftool **不解析** Apple 的 `HDRGainMap` 命名空间（我们因此有纯托管读路径） |

## 4. 修补清单

### P0-1 识别 Apple 早期 HEIF 辅助图（把"看不见"变成"点名且未决"）

- 改点：`IsoBmffGainMapProbe.ParseIprp` 的 `auxC` 分支 —— 除 `21496` 外，URN 含 `hdrgainmap`
  （大小写无关）时置 `sawAuxApple`，判定链新增 `Form = "aux-apple-hdrgainmap"`、`HasGainMap = true`、
  `Determinate = false`、`FailReason` 点名 Apple 且明写"不得按无增益图静默走 SDR"。
- 连带改点：`GainMapDecoder` 两处 ——
  ① 厂商尾注的探测条件从 `bmff?.HasGainMap != true` 改成"标准承载**可用**才独占"
  （`bmff is null || HasGainMap != true || Form != "tmap"`），否则"检测到但用不了"会把唯一可用通路一起挡掉；
  ② Step 2 的分支条件加 `&& Form == "tmap"`，让辅助图形式落到尾注/兜底分支而不是 `return null`。
- 验收判据（必须做出来才算完）：`verify-gainmap-isobmff.ps1` 新增一节，构造一个含
  `meta/iprp/ipco/auxC(urn:com:apple:photo:2020:aux:hdrgainmap)` 的合成件，跑
  `ServiceProbe gainmap-isobmff <件>`，断言打印 `hasGainMap=True`、`form=aux-apple-hdrgainmap`、
  `determinate=False`；再加一支**反控**：同结构但 URN 换成 `urn:com:example:other` ⇒ 必须仍是
  `hasGainMap=False`（证明新判据不是"看见 auxC 就报"）。
- 状态：**补丁已写好但尚未提交**（98 行，存 `tests/output/gainmap-apple-aux-20261020.patch`）。
  在隔离工作树上验过 **HEAD + 补丁 = 0 警告 0 错误**；行为验收**未完成**——我先做的最小合成件没有被探针识别
  （`hasGainMap=False`，与真件 `form=tmap` 对照说明是合成件结构不够，原因未定位），
  而主树此刻因并发会话在写的 `CliParser.cs:475` 语法不过而整体编不动 ⇒ 不在无法构建的树上盲提产品代码。
- 复现命令：
  `git worktree add --detach tests/output/_wt3 HEAD` → 拷入补丁的两个文件 →
  `dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release` → `dotnet build tests/ServiceProbe/ServiceProbe.csproj -c Release`。

### P0-2 拿一份真 Apple HEIC 样本，把"点名未决"做成"能读能施加"

- 为什么必须样本：施加公式与参数来源在 Apple 侧不是 ISO 那套（MakerApple 33/48、8-bit 单通道 YUV400 辅助图），
  没有样本就只能猜，猜出来的增益施加比"不做"更坏（会改像素）。
- 样本来源候选：`tools/src/libultrahdr/tests/data/apple_gainmap_old.jpg` / `apple_gainmap_new.jpg`（**JPEG**，本机已有，
  可先把 JPEG 侧的 Apple 读路径判据补上）；HEIC 侧需要 iPhone 拍摄或 libheif 测试集。
- 验收判据：同 §3c 的形状 —— 正向"识别 + 元数据非空 + 施加后峰值 > 底图峰值"，负控"普通 HEIC 不得走增益图路径"。

### P0-3 底图缺 ISO 21496-1「版本裸桩」（已实现 + 已验收，补丁待落主树）

- 一手出处（参考实现 libultrahdr v2.0.2，本仓 `tools/src/libultrahdr` 干净检出，`git status` 无改动）：
  结构声明 [jpegr.cpp:1074-1078](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegr.cpp#L1074)
  把底图这一列标成 **`(Required, ISO 21496-1 metadata, version only)`**，写入代码在
  [jpegr.cpp:1278-1292](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegr.cpp#L1278)：
  `APP2` + 段长 `2+28+4` + `urn:iso:std:iso:ts:21496:-1\0` + `minimum_version(0000)` + `writer_version(0000)`。
  数值只在**次图**那一根里（同文件 :1394-1404）。
- 我们差在哪：组装段只写了 XMP 与 MPF（[GainMapEncoder.cs:1003-1006](file:///c:/PLAN/ffmpegPictureUI/src/FfmpegGui/Services/GainMapEncoder.cs#L1003)），
  ISO 只进次图（同文件 :976-977）。
- 实测（字节级，`tests/output/_gm_jpeg_layout2.py`，两份不同批次产物同形）：
  `tests/output/heic-bt2020/head/gm_2020_jpg/synth_heic_gainmap.jpg` = `#1 ICC/DQT/SOF/DHT/XMP/MPF/SOS`（**无 ISO**），
  `#2 ISO(full,61B)`；全文件 `urn:iso…` 计数 = **1**。
- 后果边界：我们自己的读侧不受影响（元数据从次图取，参考实现也从次图取，见 :1499-1500）；
  受损的是**只看底图就判定"这是 21496-1 增益图"**的消费者。具体哪家会这样，**未判定**（本机没有可跑的 Android/JPEGLib 栈），
  所以这条按"规范 Required 而我们不写"立项，不按"某机型坏"立项。
- 改动（3 处，全在 `GainMapEncoder.cs` 的组装段）：`IsoVersionOnlyStub()` + 主图写 `APP2` +
  `primarySize` / `mpfDataStart` 同步 +34 字节（漏一处就会写出错位的 MPF）。
- 验收判据：`verify-gainmap-engine.ps1` 新增 **§1b** 三条 ——
  ①b-1 底图 SOS 之前必须存在"段长=34 且命名空间命中"的 APP2，并打印 off 与 4 字节版本读数；
  ①b-2 **内存变异臂**：把这根桩从字节里摘掉后，同一把尺必须判无（不落盘、不重建）；
  ①b-3 全文件命名空间计数**恰好 2**（底图桩 + 次图全量），防重复插入与防某一侧丢。
- 读数（同一支门禁脚本，只换被测二进制）：打了补丁的工作树件 **PASS=143 FAIL=0**；
  主树未打补丁件 **PASS=141 FAIL=2**，红的正是 ①b-1 与 ①b-3 —— 判据有牙且其余 141 条无回归。
- MPF 独立复核（exiftool，第三方读者）：补丁件 `MPImageStart=11546`（== 实测次图 SOI 位置）、
  `MPImageLength=1523`（== 13069−11546）、`NumberOfImages=2` ⇒ +34 字节的偏移账是对的。
- 补丁文件：`tests/output/gainmap-base-iso-stub-20261020.patch`（未落主树：主树被并发写者的
  `src/FfmpegGui/CliParser.cs` 占着，12 个文件未提交，不能替他们构建/提交）。

### P0-4 次图缺 XMP（已实现，随元数据方式档一起落）

- 参考实现把 XMP 写**两处**：底图 [jpegr.cpp:1252-1263](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegr.cpp#L1252)、
  次图 [jpegr.cpp:1381-1391](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegr.cpp#L1381)；
  ISO 同样写两处（桩 + 全量）。改之前的形状是"一人一份"：底图只有 XMP，次图只有 ISO。
- 真实文件支持"次图也该带 XMP"：`tools/src/libultrahdr/tests/data/apple_gainmap_new.jpg` 的 `#2` 就是一根
  `APP1[XMP](len=758)`（Apple aux 那一代），实测 `urn:com:apple:photo:2020:aux:hdrgainmap` 命中 1 次、`hdrgm`/`urn:iso` 命中 0 次。
- 实现要点（这笔账必须一起改，漏一处就写出错的长度）：次图多出 `2+2+29+len(xmpSec)` 字节 ⇒
  底图 XMP 的 `Container:Item Item:Length` 与 MPF 的 secondary size 都要用**含次图 XMP 的**总长；
  组装顺序因此倒过来——先算次图总长（参考同序，[jpegr.cpp:1164-1170](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegr.cpp#L1164)），
  再生成底图 XMP。次图那份 XMP **不带** `Container:Directory`（`BuildXmpMetadata(withDirectory:false)`），
  否则 `Item:Length` 自指。
- 验收判据：`verify-gainmap-engine.ps1` §1c 里两条"声明长度 == 实测次图长度"（both/xmp 各一条）+ 三档指纹矩阵。
  实测读数 `both：声明 2603 == 实测 2603`、`xmp：声明 2510 == 实测 2510`。

### P0-5 hdrgm XMP 的字段集与序列化形式（2026-10-20 结案：两处都已证伪并修）

**权威原文**：Ultra HDR 图片格式 v1.1（Android Developers，已抓存 `tests/output/spec_ultrahdr_zh.html`，
358,656 B；解析脚本 `tests/output/_parse_spec.py`）。

- hdrgm 表**恰好九项**：`Version`（文本，必填，1.0）、`BaseRenditionIsHDR`（布尔，必须 False）、
  `GainMapMin`（实数或有序实数数组）、`GainMapMax`（同上，**必填**）、`Gamma`（同上）、
  `OffsetSDR` / `OffsetHDR`（同上）、`HDRCapacityMin`（实数）、`HDRCapacityMax`（实数，**必填**）。
  原文对前五项的类型写的是「单个 Real 或有序 Real 数组……一个适用于所有通道的项，或分别针对 R/G/B 三个项」
  ⇒ **多通道靠数组或单值表达，规范里没有通道数字段**。
- 原文关键词检索：`UseBaseRenderingColorSpace`、`hdrgm:Channels`、`MapPrimary`、`MapSecondary`、
  `MapImageRatio` **全部 0 命中**（原始 HTML 与可见文本两条路都查了）；参考实现的字段清单同样只有九项
  （[jpegrutils.cpp:455-463](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegrutils.cpp#L455)），
  且它只在 `AttributeName` 里认这九个名字（[:178-210](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegrutils.cpp#L178)）。
- 原文另两条定位：①「此元数据存储在增益图像的 XMP 数据包中」；②格式信号 = **主要图片**的 XMP 里存在
  `hdrgm:Version="1.0"`。我们的实现：次图写全量、底图也写全量（底图那份同时承载 Container:Directory，
  多写九项不违反规范，且让只读底图 XMP 的消费者也能拿到数值；参考实现只在底图写 Version + Directory）。

**本轮抓到并修掉的两处**（都由判据逼出来，不是读代码猜的）：

1. **五个自造字段**（`hdrgm:Channels` / `UseBaseRenderingColorSpace` / `MapPrimary` / `MapSecondary` /
   `MapImageRatio`）已从模板删除。原先的"通道数"语义改由两层承载：ISO 的 flags bit7（有 ISO 时）、
   以及增益图 image 自身的通道（XMP-only 时）。判据：engine ⑤b 用 exiftool 先做正控
   （同一份 XMP 读得到 `GainMapMax=2.30`），再断言五个名字命中数 == 0。
2. **序列化形式**：我们原来把 hdrgm 写成 `<hdrgm:Version>1.0</hdrgm:Version>` **子元素**；规范示例与
   参考实现都是 `rdf:Description` 上的**属性**（`hdrgm:Version="1.0"`）。exiftool 两种都读得出，所以此前
   一直没暴露；第三方判据 D5e（xmp+RGB、无 ISO）让 libultrahdr 直读时报
   `xml parse error, could not find attribute hdrgm:Version`。改成属性后 D5e 通过
   （`Ultra HDR Image: Yes  max=2.46229`）。

**`xmp` + RGB 不再拒绝**：参考实现拒绝的是"各通道参数**不相同**"的 XMP 编码
（[jpegr.cpp:1120-1127](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegr.cpp#L1120)
的 `!are_all_channels_identical()`），本编码器的逐通道参数**恒相同**（`BuildIso21496Metadata` 成套复制），
于是 XMP 单值与 ISO 三参数集逐值等价 ⇒ 这是合法组合。判据（engine §1c）不只测"能产出"：
把两条臂的增益图解码成 rgb24 后逐像素比通道 —— 单通道臂 0/49152 像素三通道有差，
xmp+RGB 臂 48896/49152 有差（99.5%），证明 RGB 臂交付的是**真逐通道数据**、不是静默灰度。
自家解码器同步补了 XMP-only 的通道推断（没有 ISO flags 时按增益图 SOF 分量数取 1/3；
本实现的"灰度"增益图实际也编成 3 分量 YCbCr、三通道相等，逐通道施加与灰度施加逐值等价）。

### P1-3 段序与参考不同（只登记，不判红）

- 参考声明的标准序（[jpegr.cpp:1295](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegr.cpp#L1295)）：
  `SOI → APP0(JFIF) → APP1(XMP) → APP2(ICC) → APP2(ISO) → DQT/SOF/DHT → APP2(MPF) → SOS`。
  实测我们：`SOI → APP2(ICC) → DQT → SOF → DHT → APP1(XMP) → [P0-3 后：APP2(ISO 桩)] → APP2(MPF) → SOS`。
- 为什么暂不判红：按 marker 扫描的解析器不受位置影响，而"有没有按位置取 APP1 的消费者"本机无一手证据 ⇒
  **未判定**。等 P0-4 一起做时再决定是否重排（重排会动 `baseHeaderLen` 的切分口径，属另一笔账）。

### P1-1 AVIF 增益图的第三方读者常驻判据（已完成）

- 判据已落在 `verify-gainmap-isobmff.ps1` 的 §3f-9：`avifdec --info` 必须读出 `Alternate Headroom`
  且文件里存在 `trc=16` 的表示；读者缺席时转 **WARN「未判」**，不退化成"只读两个读者还说三读者同向"。
- 本轮实测：`Alternate Headroom=1.30`、Transfer Char 序列 `1/16` ⇒ 我们自写的 tmap 能被 libavif 理解。
- 后续：把 `avifdec` 的版本号也打进这行（libavif 实验特性会变格式，版本漂移要能被事后归因）。

### P1-2 能力表的 HEIC 写通路 tripwire 扩展

- 现状：`verify-color-caps.ps1` 的 (h) 用三条实测（无 heif muxer / 直接写出失败 / 无 heifenc）支撑
  "heic/heif 全保守"，并已写明"若工具链具备写入能力必须重测并更新能力表"。
- 新事实（本机一手）：`tools/src/libultrahdr` 里**存在 HEIF API**（`lib/include/ultrahdr/heifultrahdr.h`）
  且 `build/libheif` 有工程文件，但本机没有 `heifenc.exe` / `libheif.dll`，ffmpeg 也没有 heif muxer
  ⇒ 能力表**维持保守是对的**，但 tripwire 应该把"libultrahdr 的 heif API 已进树"这一条也纳入：
  将来有人构建出 libheif+编码器，判据必须当场变红提醒重测。
- 验收判据：在 (h) 段加第 4 个条件（`heifultrahdr.h` 在位 ⇒ 打印它，并在 `heifenc.exe`/`libheif.dll`
  任一出现时把"能力表需重测"从提示升级为判红）。

### P2-1 PNG / JXL 增益图：先量生态再决定

- 依据：MediaInfo 已解析 PNG 的 ISO 21496-1 元数据；ACR 能给 AVIF/JXL/PNG/HEIF 嵌增益图且 base 可为 HDR。
- 做法：先做**读侧**兼容（我们已有 `ParseIso21496`，PNG 侧只是找 `sRGB`/自定义块的位置），
  写侧等能力表与第三方读者判据都齐了再开。不在没有第二读者认可前写出去。

## 5. 本轮验证状态

| 项 | 状态 |
| --- | --- |
| ISO 二进制字段读写对齐（§1 全表） | **已核**（本机 libultrahdr 一手代码 vs 我们的 `ParseIso21496` 逐条比） |
| `HDRCapacityMin=0`、`hdrgm:Version=1.0` | **已核为与参考实现一致**（上一轮的怀疑撤回） |
| Apple HEIF aux 不识别 | **已核**（代码只匹配 `21496`） |
| P0-1 补丁编译 | **已核**：隔离工作树 HEAD+补丁 = 0 警 0 错 |
| P0-1 补丁行为 | **未核**（合成件未触达分支，原因未定位；主树因并发写者的 `CliParser.cs:475` 编不动） |
| P1-1 第三方读者判据 | **已核并落地**（70/0，含两支变异验牙） |
| P1-2 / P0-2 / P2-1 | **未做**（P0-2 卡在样本） |
| P0-3 底图 ISO 裸桩 | **已实现并验收**：工作树构建 0 警 0 错；门禁 §1b/§1c 红绿对撞（见下一行）；MPF 由 exiftool 独立复核 |
| P0-4 次图 XMP | **已实现并验收**：§1c 两条"声明长度==实测次图长度"闭合（both 2358/2358、xmp 2265/2265；属性化后长度较旧元素形式缩短） |
| 元数据方式三档（默认 iso） | **已实现并验收（2026-10-20 二次复验）**：`verify-gainmap-engine` **162/0**（新增：xmp+RGB 的逐通道数据判据、⑤b 九项字段锁）；`verify-gainmap-thirdparty` **22/0**（D5 两处声明 `XMP[-0.29,1.30] == ISO[-0.290,1.300]`、D5c 默认档无 XMP、D5d 自造字段 0 命中、D5e libultrahdr 直读 xmp+RGB `max=2.46229`）；`ui-cli` **72/0**；`ui-mode` **52/1**（O6a–g 全 PASS；唯一红 G1 是工作树嵌在第 10 层、宿主只向上找 8 层的既有环境限制，把同一份构建产物放浅路径后 G1 **1/0**） |
| UI 联动的实机读数 | **已核**：G 组（GUI 驱动真实转换）先前在隔离工作树红，机制是宿主 `InitExternalTools` 只从 exe 目录向上找 8 层、工作树嵌在第 10 层；把同一份构建产物放到浅路径后 `tools-root=C:\PLAN\ffmpegPictureUI` 被找到，G1 PASS ⇒ 归因成立 |
| AVIF 档位提示 | **已核**：`--format avif --jpeg-gain-map-metadata both` 的日志出现「元数据方式「both」对 AVIF 出口无效：tmap 只承载 ISO 21496-1 二进制，XMP 不写」 |
| P0-5 XMP 字段集/序列化 | **已核并修**：规范九项、五个自造字段绝迹；hdrgm 改为属性形式（第三方 D5e 直读通过） |
| P1-3 段序 | **只登记**（消费者是否按位置取段：未判定） |
| 「标记方法两种/三种」与是否加下拉 | 见 §6；结论已被维护者裁决更新：**做三档、默认 iso**（原"不加下拉"的判词作废） |

## 6. 元数据标记方法到底几种？要不要在 JPEG 高级色彩选项加下拉

### 6.1 事实：参考实现把它做成**编译期**开关，不是逐文件选项

- `UHDR_WRITE_XMP` / `UHDR_WRITE_ISO` 是 CMake 选项，且**上游默认已经是"只写 ISO"**：
  [CMakeLists.txt:135-136](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/CMakeLists.txt#L135)
  （`UHDR_WRITE_XMP` 默认 `FALSE`，`UHDR_WRITE_ISO` 默认 `TRUE`）。
- 编译期常数 [jpegr.cpp:51-60](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegr.cpp#L51)，
  加 `static_assert` 只禁止"两者都不写" [jpegr.cpp:65-66](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegr.cpp#L65)
  ⇒ 合法组合是 **XMP-only / ISO-only / 双写** 三档，构建时定死，编码 API 里没有运行期选择。
- 本机 `tools/src/libultrahdr/build/CMakeCache.txt:264-267` 实测就是 `UHDR_WRITE_ISO:BOOL=ON` + `UHDR_WRITE_XMP:BOOL=OFF`；
  `build/libheif/src/libheif-build/CMakeCache.txt:44` 的 `CMAKE_CXX_FLAGS` 里带 `-DUHDR_WRITE_ISO`（libheif 侧同档）。

### 6.2 XMP-only 对本实现不是"严格更弱"的一档（2026-10-20 按规范原文更正）

- 多通道增益图元数据：参考实现只拒绝**各通道参数不相同**的 XMP 编码
  （[jpegr.cpp:1120-1127](file:///c:/PLAN/ffmpegPictureUI/tools/src/libultrahdr/lib/src/jpegr.cpp#L1120)
  的 `!are_all_channels_identical()`）；参数相同时允许，并在 XMP 里写**单值**（`[0]`）应用全部通道。
  本实现的逐通道参数恒相同（`BuildIso21496Metadata` 成套复制）⇒ XMP 单值与 ISO 三参数集逐值等价。
- `use_base_cg`：XMP 写不出"增益图在 alternate 空间"的声明；本实现的增益图恒在底图色彩空间
  （ISO flags bit6 恒置）⇒ 不受这条影响。
- 所以对本实现，两份声明的**数值**完全同源；三档的差别只剩"哪些读者认哪个命名空间"。
  历史判词"XMP-only 会静默缩小声明能力"只对"逐通道参数不同 / alternate 空间"的编码成立，不适用于本仓。

### 6.3 "旧标准"（Apple aux）我们**写不出来**，不能放进写侧下拉

- Apple 那一代的载体是 HEIF/MakerApple（`urn:com:apple:photo:2020:aux:hdrgainmap`，8-bit 单通道辅助图），
  本机无 HEIF 写通路（ffmpeg 无 heif muxer、无 `heifenc.exe`/`libheif.dll`，见 §4 P1-2 的实测）。
- 下拉里放一个必然失败或必然静默降级的选项，违反本仓「编码器不可用应显式报错而非静默回退」的诚实原则
  ⇒ Apple aux 只该出现在**读侧**与探针的 `Form` 标签里（P0-1 正在做这件事）。

### 6.4 结论（2026-10-20 更新：维护者已裁决"做开关，默认 iso"）

我原先的判词是"不加下拉"，理由是：①参考实现把它当构建期能力；②最大兼容解就是双写；③Apple aux 写不出。
维护者的裁决取代其中第 ②条：**三档都做，默认从"两份都写"改成"只写 ISO"**（与 libultrahdr 上游默认一致），
`xmp` 档保留给"只认 hdrgm 的老栈"，`both` 档保留最大兼容解。落地口径：

- 取值域只有一处定义：`GainMapEncoder.GainMapMetadataModes`（`iso` / `xmp` / `both`），UI 下拉顺序与 CLI 别名都从它取。
- 组合合法性只有一个裁决点：`ValidateGainMapMetadata(mode)` —— 取值域之外一律点名拒绝；
  `xmp` + RGB **合法**（hdrgm 用单值表达"适用于全部通道"，本实现的逐通道参数恒相同；
  参考实现也只拒绝各通道参数不相同的 XMP 编码）。UI 不再灰化"增益图类型"。
- 默认值变更本身有锁：`ui-cli` 里断言 `new FfmpegOptions().JpegGainMapMetadata == "iso"`。
- AVIF 出口与此开关无关（tmap 承载的就是 ISO 二进制），选了非默认档会**点名**"对 AVIF 无效"。
- P0-5 已结案：XMP 字段集收紧到规范九项，且改为 `rdf:Description` 属性形式（详见 §4 P0-5）。

