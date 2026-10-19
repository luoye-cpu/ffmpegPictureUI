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
