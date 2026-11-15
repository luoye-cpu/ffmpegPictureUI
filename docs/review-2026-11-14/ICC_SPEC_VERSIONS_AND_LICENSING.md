# ICC 规范版本、工具链支持度与许可合规调研报告

**调研日期**：2026-11-14（周六，Asia/Shanghai）
**调研方式**：color.org / ISO / W3C / Adobe / Apple / Little CMS 官方页面 + 仓内只读实测（exiftool 13.61、ffmpeg git-2026-10-11、cjxl 0.12.0、avifenc 1.4.2）+ 仓内源码只读。
**范围**：本仓 `FfmpegGui` 把默认策略从"ICC 为主"切到"CICP 优先、不可达回退 ICC"前，需要一份可引用的第一手结论：ICC 有哪些可行版本、真实工具链认不认、把 ICC 规范文本/profile 用于"生成并嵌入输出图片"在法律上合规到什么程度。

> **声明**：本文为工程法律信息梳理，**不构成法律意见**。涉及商标、再分发、专利的最终判断，对外发布前应由有资质的法务复核。但本文每条结论都给了一手来源，不做泛泛建议。

---

## 0. 与旧档的关系（先读这段）

本仓 `docs/archive/` 下有两份 2026-09-20 的合规审计：`DISTRIBUTION_COMPLIANCE_AUDIT.md` 与 `LICENSE_TERMS_VERIFICATION.md`。按本仓规矩，归档档是"时间点读数"，不回改；本文在需要对照处单列"旧档结论 / 本次复核"两列。

| 旧档条目 | 与本次 ICC 调研的关系 |
|---|---|
| 旧档 §1.4 / §1.5：随包 ffmpeg 为 `--enable-nonfree` 构建不可再分发、发行包无 LICENSE 文件 | **本次复核仍成立**。实测 `publish/PLAN/ffmpeg-full/ffmpeg.exe -version` 的 configuration 行仍含 `--enable-gpl --enable-nonfree --enable-libfdk-aac`（见 §D.3）。但这是 ffmpeg 自身的分发问题，与 ICC 规范/profile 许可无关，本文不展开。 |
| 旧档 §1.2 / §4：Adobe DNG SDK 许可宽松、需补 §2 通知义务、DNG 专利许可要求 "This product includes DNG technology under license by Adobe." | **本次复核仍成立**。该声明已在仓内 `NOTICE` 第 1 条与 `THIRD-PARTY-NOTICES.md §4` 落地。与 ICC 无关。 |
| 旧档全文 | **对 ICC 规范文本版权、ICC profile 再分发、sRGB/Adobe RGB/Display P3/ProPhoto 商标均无专门论述**。唯一相关的是 `IccProfileBuilder.cs` 类注释里作者自己写的判断："外部 .icc 资源要么需人工下载、要么有分发许可问题……零第三方依赖、无许可负担"。本文是第一次把这件事拆开做一手核查。 |

---

## A. 版本清单与现状

### A.1 ICC.1（基础 profile 格式规范）的版本沿革

来源：color.org 官方规范页 <https://www.color.org/specifications/>、<https://www.color.org/v4spec/>、<https://www.color.org/v2spec/>；ISO catalogue <https://www.iso.org/standard/86740.html>（ISO 15076-1:2025 OBP）。访问日期均为 2026-11-14。

| 规范编号 | 发布年 | Profile 版本字节（offset 8–11） | 对应 ISO 标准 | 状态 | 主要新增能力 |
|---|---|---|---|---|---|
| Spec ICC.1:1998-09 | 1998 | 2.x | — | 被取代 | 首版 profile 文件格式（"File Format for Color Profiles"） |
| ICC.1A:1999-04 | 1999 | 2.x | — | 被取代 | 对 ICC.1:1998-09 的 addendum 2 |
| **ICC.1:2001-04** | 2001 | **2.4.0**（v2 末版） | — | 被取代（v2 冻结） | color.org 自述："Versions published prior to 2001 are known as v2"；v2 至今仍是最广泛部署的形态 |
| ICC.1:2003-09 | 2003 | 4.1.0 | — | 被取代 | v4 过渡版 |
| ICC.1:2004-04 / 2004-08 / 2004-10 | 2004 | 4.2.0 | **ISO 15076-1:2005**（首版国际标准） | 被取代 | 第一个提交 ISO 的版本；引入 perceptual intent reference medium gamut、lut-based A2B/B2A、`mluc`/`sf32`/`mAB `/`mBA ` 等 v4 类型 |
| **ICC.1:2010-12** | 2010-12 | **4.3.0** | **ISO 15076-1:2010**（第二版） | 被 ICC.1:2022 取代 | 与 v4.2 完全向后兼容；现行部署最广的 v4 版本 |
| **ICC.1:2022-05** | 2022-05 | **4.4.0** | **ISO 15076-1:2025**（第三版，2025 发布） | **现行** | color.org 与 ISO OBP 标题均写明 "Profile version 4.4.0.0"；技术上与 ICC.1:2010 向后兼容（PDF Foreword 原文："After further… [revisions to] ICC.1:2010, which are technically identical. This version, ICC.1:2022…"） |

**核对用户在任务里点名的几个说法**：

- **"v4.4"真实存在**。`archive.color.org/specification/ICC.1-2022-05.pdf` 标题逐字写 "Specification ICC.1:2022 (Profile version 4.4.0.0)"。不是传言。
- **"ICC.2:2019"真实存在**。color.org 当前规范页同时列出 `ICC.2-2019.pdf` 与 `ICC.1-2022-05.pdf`；<https://www.color.org/iccmax/index.xalter> 原文："The previous version, ICC.2:2019, is also published as ISO 20677-1."
- **"ICC.2:2023"真实存在**。同页原文："Specification ICC.2:2023 (iccMAX) … approved by the ICC on 12 October 2023."
- color.org 与 ISO catalogue 表述**一致**：ISO 15076-1:2025 OBP §1 原文："This document is based on ICC.1:2022"；"The second edition, ISO 15076-1:2010, was based on ICC revision 4.3"。两边没有打架。

### A.2 ICC.2（iccMAX，扩展架构）

来源：<https://www.color.org/iccmax/index.xalter>、<https://www.color.org/iccmax-status/>、<https://www.color.org/iccmax/ICC.2-2017.pdf>。访问日期 2026-11-14。

| 版本 | 状态 | 是什么 |
|---|---|---|
| ICC.2:2016-07 | 被取代 | iccMAX 首版（2016-07-29 ICC 批准） |
| ICC.2:2017 | 被取代 | 引入 multi-processing element（MPE）：栈式可编程变换、单段曲线、N 维 LUT、色彩外观模型、稀疏矩阵、tint array |
| ICC.2:2018 | 被取代 | — |
| **ICC.2:2019** | 被 ICC.2:2023 取代；被采纳为 **ISO 20677-1** | — |
| **ICC.2:2023** | **现行** | 2023-10-12 ICC 批准；扩展 ICC.1 的 profile 格式，面向高级印刷/色彩外观管理 |

**关键判断（见 §B）**：iccMAX 在真实 CMM 生态里几乎没有消费端。lcms2 官网自述只实现到 ICC 4.4，不提 ICC.2；Windows mscms/ColorSync 也不实现 MPE。本产品**不应该**输出 iccMAX profile。

### A.3 v2 与 v4 的技术差异（决定兼容性的部分）

来源：<https://www.color.org/icc_specs2.xalter>、`ICC.1:2022-05.pdf` §8.2/§9.2。

- **v2（≤4.3 之前的 v2.x）**：`desc` 标签用 `textDescriptionType`（ASCII，限 64 KB）；TRC 用 `curveType`/`paraType`；matrix/TRC profile 是最小形态。
- **v4（≥4.0）**：`desc` 改用 `multiLocalizedUnicodeType`（`mluc`，UTF-16BE 多语言）；新增 `chromaticAdaptationTag`（`chad`，v4 matrix profile **必需**）；PCS 白点强制 D50；CMM type / profile creator / MD5 profile ID 进入头。
- **v4.4（ICC.1:2022）相对 v4.3**：技术上向后兼容，主要是勘误与澄清（ISO OBP 原文 "technically identical"）。对 profile 生成方而言，v4.0.0 字节与 v4.4.0 字节在 matrix/TRC 这种最小 profile 上**对消费者没有可感知差别**。

---

## B. 工具链 × 容器支持度（决定本产品该出哪个版本）

### B.1 "版本 × 消费者"矩阵

| 消费者 | v2 matrix/TRC | v4 matrix/TRC | v4 LUT-based（mAB/mBA） | iccMAX / MPE | 一手依据 |
|---|---|---|---|---|---|
| **Little CMS（lcms2）** | ✅ | ✅（官网自称 "FULL IMPLEMENTATION of ICC specification 4.4, supports all kind of V2 and V4 profiles"） | ✅ | ❌（lcms2 官网不提 ICC.2；2.19 release notes 也只讲 ICC 4.4） | <https://littlecms.com/color-engine/>、<https://www.littlecms.com/blog/2026/04/17/lcms2-2.19/> |
| **Windows WIC / mscms** | ✅ | ✅（基础 matrix/TRC） | ⚠️ 部分（社区与博客实测 mscms 对复杂 CLUT/parametric 曲线/viewingConditions 解析不完整；wcsapi 更全） | ❌ | <https://learn.microsoft.com/windows/win32/wcs/>；中文社区实测 <https://www.cnblogs.com/suv789/p/18758554>（二手，标注） |
| **Windows Photos** | ⚠️ | ⚠️ | 同 WIC | ❌ | Microsoft Q&A 用户报告：Photos "cannot work with the Windows ICC color profile… uses sRGB as default"（<https://answers.microsoft.com/en-us/windows/forum/all/slideshow-decreasing-image-quality...>，2024-08，**用户报告非官方文档**）；Photos 基于 WIC，但 historically 色彩管理弱 |
| **macOS ColorSync / Preview** | ✅ | ✅（Apple 在 Mac OS X Tiger 时代就官方宣称 "the only operating system that fully supports the ICC version 4 standard"，<https://www.apple.com/pro/photo/colorsync.html>） | ❌ **LUT-based ICC profiles have never been supported on Apple platforms**——Apple 开发者论坛 2025-11-12 官方回复原文 | ❌ | <https://developer.apple.com/forums/thread/807131>（Apple 员工回复） |
| **Safari / Chrome / Firefox（图像）** | ✅ | ✅（EIZO 2019-10 实测表：Chrome 4 / Firefox 2 / Safari 4；Firefox 自 8.0/2011 起支持 v4） | ⚠️ 浏览器用 skcms/lcms，只做 matrix/TRC，不跑 LUT | ❌ | <https://www.eizo.com/library/management/color-management_icc-profiles/>；Firefox v4 历史见 HandWiki 引用 Mozilla bug 488800（二手） |
| **ffmpeg（zscale/iccgen）** | ✅（可写） | ✅（实测输出 v4.4.0，见 §D.2） | ✅（通过 lcms2） | ❌ | 仓内 ffmpeg `--enable-lcms2`；实测 `iccgen` 产物 ProfileVersion=4.4.0 |
| **exiftool 13.61** | ✅ 读 | ✅ 读 | ✅ 读 | ⚠️ 能读头但不解释 MPE | 仓内实测 |

### B.2 容器承载规范

来源：ICC 官方技术说明《Embedding ICC profiles》<https://archive.color.org/files/technotes/ICC-Technote-ProfileEmbedding.pdf>；WebP RFC 9649；W3C PNG Third Edition。

| 容器 | 承载位置 | 版本限制 | 字节上限 | 一手依据 |
|---|---|---|---|---|
| **PNG** | `iCCP` chunk（zlib 压缩的 profile 字节流 + profile name） | **第二版（ISO/IEC 15948:2003）只引用 v2，且 ICC 技术说明明说 "the restriction to ICC v2 is inadvertent"**；**第三版（W3C Recommendation 2025-06-24）澄清 iCCP 可容纳任何 ICC.1 版本（v2/v4）**。现实中主流解码器（libpng、Chrome、WIC）早已接受 v4 | 无硬上限（PNG chunk 本身 ≤ 2³¹ B）；profile name 建议 ≤ 80 B | <http://w3c.github.io/png/Implementation_Report_3e/>；<https://www.w3.org/news/2025/portable-network-graphics-png-specification-third-edition-is-now-a-w3c-recommendation/> |
| **JPEG / JFIF** | APP2 marker segments，签名 `ICC_PROFILE\0` + seq/total | 无版本限制 | 单段 ≤ 65 533 B；1 B seq + 1 B total ⇒ 最多 255 段 ⇒ 理论上限 ≈ 16.7 MB（ICC 技术说明原文 "The 1-byte chunk count limits the size of embeddable profiles to 16 707 345 bytes"） | <https://archive.color.org/files/technotes/ICC-Technote-ProfileEmbedding.pdf>；Exiv2 wiki <https://dev.exiv2.org/projects/exiv2/wiki/The_Metadata_in_JPEG_files> |
| **WebP** | `ICCP` chunk（在 VP8X 里，必须出现在图像数据 chunk 之前，至多一个） | 无版本限制；缺失时 SHOULD assume sRGB | RIFF chunk size 4 B，理论 ≤ 4 GB；实际 RIFF 单 chunk 上限 ~4 GB | RFC 9649 / Google WebP Container Spec <https://developers.google.cn/speed/webp/docs/riff_container> |
| **HEIF / AVIF** | `ispe`/`pixi` 同层的 `Prof` box（`colour_type=1` 时携带 ICC） | ICC 技术说明矩阵：AVIF 引用 **v4.3**，v2 与 v4 均 ✅ | ISO-BMFF box size 4 B（含 64 B extended size 机制），无实际压力 | <https://archive.color.org/files/technotes/ICC-Technote-ProfileEmbedding.pdf>；ISO/IEC 23008-12 |
| **JPEG XL** | container 帧级 `jxlp` box 外的 `JXL „jbrd`/Exif/XMP/ICC box；codestream 里也可内嵌 ICC（Annex B） | ISO/IEC 18181-1:2022 Annex B 定义 ICC profile 编码；无版本限制 | 无硬上限 | <https://www.iso.org/obp/ui/en/#!iso:std:77977:en>；libjxl API `JxlEncoderSetICCProfile` <https://libjxl.readthedocs.io/en/stable/api_encoder.html> |

### B.3 给本产品的建议

**结论：SDR 出口默认生成 ICC v4 matrix/TRC profile，宽色域/HDR 不需要不同版本；不要出 iccMAX。**

理由（区分"规范允许"与"工具实际认"）：

1. **规范允许 v4.4**，但真实生态里 v4.4 与 v4.0/v4.3 在 matrix/TRC 这种最小 profile 上**对消费者无差别**（技术上向后兼容）。本仓 `IccProfileBuilder.cs:204` 写 `{4,0,0,0}` = v4.0.0，ffmpeg iccgen 实测输出 v4.4.0——两者都能被 lcms2/WIC/ColorSync/Chrome 正确解释。
2. **为什么不退回 v2**：v2 的 `desc` 是 ASCII `textDescriptionType`，v4 强制 `mluc` 多语言；v4 还要求 `chad`（chromatic adaptation）。本仓自研 builder 已经写了 `chad` 与 `mluc`（`IccProfileBuilder.cs:140,277`），退回 v2 反而要拆 `chad`、改 `desc` 类型，收益为零。
3. **为什么不做 LUT-based（mAB/mBA）**：Apple ColorSync 官方说 LUT-based profile "从未被支持"（<https://developer.apple.com/forums/thread/807131>）；mscms 对复杂 CLUT 解析不完整。matrix/TRC 是唯一跨 macOS/Windows/浏览器/ffmpeg 全绿的形态。
4. **为什么不做 iccMAX**：lcms2 不实现 MPE、WIC 不实现、ColorSync 不实现、浏览器更不实现。iccMAX 面向专业印刷 CMM，本产品的消费者链路上没有一个能正确解释 MPE。
5. **宽色域/HDR**：Display P3 / BT.2020 PQ 只需要不同的 rXYZ/gXYZ/bXYZ 矩阵与 TRC 表（PQ 用 1024-entry `curv` LUT），profile 版本号字节仍然是 v4.x。仓内 `IccProfileBuilder.CurveFor` 已经这么做。HDR 路线优先走 CICP（`Rec2100PQ`/`Rec2100HLG`），ICC 只是回退。

---

## C. 许可与合规（分六层）

### C.1 规范文本本身的版权与使用条件

**ICC 自己发布的规范 PDF（color.org 域）= 类 BSD 开放许可。**

一手原文（ICC.1:2010-12 PDF 首页 Copyright notice，<https://www.color.org/specification/ICC1v43_2010-12.pdf>；ICC.2:2019 PDF 同位置）：

> "Copyright © 2010 International Color Consortium®
> Permission is hereby granted, free of charge, to any person obtaining a copy of this Specification (the "Specification") to exploit the Specification without restriction including, without limitation, the rights to use, copy, modify, merge, publish, distribute, and/or sublicense, copies of the Specification, and to permit persons to whom the Specification is furnished to do so, subject to the following conditions:
> Elements of this Specification may be the subject of intellectual property rights…"

**含义**：
- 可以在仓库内、文档里引用条款号、摘录数值表（色度矩阵、TRC 参数）、再分发 ICC 自己发布的 PDF——**只要保留版权声明**。
- ICC.1:2022（v4.4）首页同样的开放许可段（本文 §0 已抓 PDF 原文确认）。

**ISO 卖的 PDF（iso.org/webstore）= 受版权保护，不得复制/再分发。**

一手原文（<https://www.iso.org/copyright.html>）：

> "All content on ISO Online is copyright protected… Any use of the content, including copying of it in whole or in part… is prohibited and would require written permission from ISO. All ISO publications are also protected by copyright… Any unauthorized use such as copying, scanning or distribution is prohibited."

ISO 15076-1:2025 与 ICC.1:2022 是同一技术内容的**两个出版载体**：ICC 在 color.org 免费开放发布，ISO 在 webstore 卖。**本仓引用 ICC 版 PDF 合法；不要把 ISO 版 PDF 塞进仓库或随包分发。**仓内 `tools/std/` 现有的是 ITU-R BT 系列（cc-by 类），不涉及 ISO 标准 PDF 再分发问题。

### C.2 "产品声称输出符合 ICC vX"是否需要授权/认证

**不需要。**

- ICC 没有像 USB-IF / CEA 那样的 conformance logo 认证程序。color.org 有 <https://www.color.org/icclogos.xalter>，但那是"会员/支持者 logo 使用指南"，不是"产品通过 ICC 一致性测试"标志。
- 产品在 UI/日志/文档里写"输出符合 ICC.1:2022 / Profile version 4.4"是**对所采用标准的事实陈述**，不需要 ICC 授权。
- 但**不要**把 ICC logo 印在产品上或宣传物料上——那是商标使用，需另行书面许可（见 C.4）。

### C.3 profile 文件本身的版权与许可

这是最容易混的一层，必须分三种 profile 来源：

#### C.3.1 ICC 自有 profile（如 sRGB_ICC_v4_Appearance.icc）

一手原文（color.org Profile Library <https://registry.color.org/profile-library/> 与 FAQ <https://color.org/faqs.xalter>）：

> "This profile is made available by the International Color Consortium, and may be **copied, distributed, embedded, made, used, and sold without restriction**. Altered versions of this profile shall have the original identification and copyright information removed and shall not be misrepresented as the original profile."

sRGB Appearance profile 页面（<https://registry.color.org/rgb-registry/srgb_appearance>）进一步要求：保留 ICC copyright notice tag；未经书面许可不得在广告/宣传中使用 ICC 名义。

**含义**：把 ICC 自有 profile **嵌入到自己生成的图片里再对外分发**，被原文"embedded… without restriction"明确覆盖。

#### C.3.2 本仓自行生成的 profile（matrix/TRC，数据来自公开标准）

本仓现状（§D.4 实测）：
- `IccProfileBuilder.cs` 运行时用公开色度数据（IEC 61966-2-5 ProPhoto/ROMM、ITU-R BT.2100 PQ EOTF、BT.709/DCI-P3 原色）现场拼字节；
- `cprt` 标签写：`"Generated by FFmpegPictureUI (pure managed ICC writer; colorimetry from public standards: IEC 61966-2-5 / ITU-R BT.2100)."`；
- 版权归 `luoye-cpu` 所有（本仓 GPL-3.0），与 ICC/Adobe/Apple 无关。

**含义**：
- 色度数值本身来自 IEC/ITU 公开标准，不是 Adobe/Apple 的机密；
- profile 文件的版权是本项目自己的；
- 嵌入到输出图片里再分发 = 分发自己的作品，无第三方许可问题；
- `cprt` 标签已经正确署名（这点做对了）。

#### C.3.3 第三方随附 profile（sRGB IEC61966-2.1、Adobe RGB (1998)、Display P3、ProPhoto 等）

| Profile | 权利方 | 许可要点 | 一手依据 |
|---|---|---|---|
| sRGB（IEC 61966-2-1:1999） | IEC 标准；特征数据由 ICC 定义；HP+Microsoft 联合开发 | IEC 标准文本本身付费；但 **sRGB 色度数据是事实**，自己按 IEC 文档数值生成 profile 不侵权。ICC 注册的 sRGB profile 按 C.3.1 开放 | <https://registry.color.org/rgb-registry/srgb> |
| Adobe RGB (1998) | **Adobe Inc.** | Adobe 官方 PDF（<https://www.adobe.com/digitalimag/pdfs/AdobeRGB1998.pdf>）商标声明段："Adobe and the Adobe logo are either registered trademarks or trademarks of Adobe… The name 'Adobe RGB (1998)' also is used as a software product trademark for Adobe's implementation"。Adobe 发布的 .icc 文件有单独的 bundling license（<https://www.adobe.com/support/downloads/iccprofiles/icc_eula_win_dist.html>） | Adobe RGB 1998 PDF 首页 Trademark Information 段 |
| Display P3 | **Apple Inc.**（DCI-P3 色域 + D65 白点 + sRGB 传递） | DICOM CP 文档（<https://dicom.nema.org/medical/dicom/CP/cp2474_vp_addnewcolorspaces.pdf>）列 "Display-P3 — Apple, Inc. Display P3 Color Encoding"。Apple 未单独发布 Display P3 .icc 给第三方分发；Display P3 的特征数据在 Apple 文档里公开，但**直接拿 Apple 的 .icc 文件再分发**需看 Apple 条款 | DICOM CP 引用 Apple |
| ProPhoto RGB / ROMM | Kodak 原始；现为 IEC 61966-2-5:2003 国际标准 | 色度数值公开；IEC 标准文本付费。自己按 IEC 61966-2-5 数值生成 profile 合法 | IEC 61966-2-5 |

**关键区分**：
- "按公开标准数值自己拼一个 profile" ≠ "把 Adobe/Apple 发布的 .icc 文件复制到自己包里"。前者合法（本仓现状），后者要看 Adobe EULA / Apple 条款。
- 本仓 `pack.ps1` 实测**不随包分发任何 .icc/.icm 文件**（§D.5），profile 全部运行时生成——这是合规上最干净的路径，继续保持。

### C.4 商标

| 名称 | 权利人 | 本仓使用场景 | 风险 | 依据 |
|---|---|---|---|---|
| `ICC` / `International Color Consortium` | ICC（注册商标） | 仓内代码注释、UI 里"ICC v4"字样 | 低：描述性使用（说明输出格式），不暗示 ICC 背书。**不要**把 ICC logo 印到产品上 | <https://www.color.org/icclogos.xalter> |
| `sRGB` | IEC 标准名；HP/Microsoft 共同开发 | UI 下拉、日志、cprt 文案 | 低：行业通用描述性术语，CSS/HTML/所有图像软件都用 | IEC 61966-2-1 |
| `Adobe RGB` / `Adobe RGB (1998)` | **Adobe Inc.** | UI 下拉（`ColorSpaceRegistry.cs:132` Key="Adobe RGB"） | **中**：Adobe 自己声明这是商标。在 UI 下拉里写"Adobe RGB (1998)"是描述该色彩空间的事实，行业惯例允许；但不要暗示 Adobe 背书/赞助。建议在文档/NOTICE 加一行商标声明 | Adobe RGB 1998 PDF Trademark Information |
| `Display P3` / `P3` | DCI-P3 是 SMPTE 标准；**Display P3 这个名字是 Apple 的色彩编码名** | UI 下拉（`ColorSpaceRegistry.cs:72` Key="Display P3"） | 低-中：CSS Color Level 4 用 `display-p3` 作为通用 token，行业已通用；但"Display P3"四字源自 Apple。描述性使用 OK | DICOM CP；CSS Color 4 |
| `ProPhoto` | Kodak 原始（现为 IEC 61966-2-5） | UI 下拉（`ColorSpaceRegistry.cs:137` Key="ProPhoto RGB"） | 低：行业通用名；IEC 61966-2-5 是国际标准 | IEC 61966-2-5 |

**本仓现状**：`NOTICE` 第 2 条已有 Adobe 商标声明（针对 DNG SDK 那一行），但**没有**专门针对"Adobe RGB (1998)"色彩空间名的商标声明。建议补一句（见 C.6 文本骨架）。

### C.5 专利

**有一处明确专利声明，但本仓不踩。**

一手原文（ICC.1:2022 §0.10 Patent statement，本文已抓 PDF 原文）：

> "The International Color Consortium (ICC) draws attention to the fact that it is claimed that compliance with this document can involve the use of a patent concerning the **outputResponseTag**, (**support of the outputResponseTag is optional**), given in 9.2.36. … The holder of this patent right has assured the ICC that he is willing to negotiate licences under reasonable and non-discriminatory terms… Information may be obtained from: Eastman Kodak Company, 343 State Street, Rochester, NY 14650 USA."

ICC.1:2022 同时在 D2B0 tag 处有第二处专利提示（同样 FRAND）。

**本仓现状**：
- `IccProfileBuilder.Build` 只写 11 个 tag：`desc cprt wtpt bkpt rXYZ gXYZ bXYZ chad rTRC gTRC bTRC`——**不写 outputResponseTag，不写 D2B0**。
- 因此 Kodak 那个可选专利**不触发**。
- 其余 RGB matrix/TRC 与公开标准（BT.709/BT.2020/PQ）的色度数值：ITU-R 建议本身是免版税公开文档；未发现针对 matrix/TRC profile 生成的专利主张。

**检索口径**：本文通过 ICC.1:2022 规范前言的 Patent statement 段、ISO 15076-1:2010 §0.10、lcms2 源码与发行声明核查；未做正式 FTO（Freedom to Operate）专利检索。若本产品未来商业分发，应做正式专利检索——本文结论"未发现针对本仓所用 matrix/TRC 路线的专利主张"≠"无专利风险"。

### C.6 落地到本产品的具体动作清单

按风险从高到低：

| # | 动作 | 现状 | 建议 | 风险 |
|---|---|---|---|---|
| 1 | **不要随包分发任何第三方 .icc/.icm 文件** | ✅ 已是现状（`pack.ps1` 不复制 .icc） | 保持。`PLAN/iccs/` 目录（`IccProfileService.cs:440`）是用户自备目录，不要在仓库里塞 Adobe/Apple 的 .icc | 高（一旦塞了 Adobe RGB 1998.icc 再分发就触发 Adobe EULA） |
| 2 | **cprt 标签正确署名** | ✅ 已是现状（自研 builder 写 "Generated by FFmpegPictureUI…"；ffmpeg iccgen 写 "No copyright, use freely"） | 保持。不要把 cprt 改成 "Copyright Adobe" 之类 | 低 |
| 3 | **补一行色彩空间商标声明到 NOTICE / THIRD-PARTY-NOTICES.md** | ❌ 现 NOTICE 只声明 Adobe DNG 商标，没声明"Adobe RGB (1998)"色彩空间名 | 建议文本骨架见下 | 中（描述性使用风险低，但补一行零成本） |
| 4 | **不要把 ICC logo 印在产品 UI/官网** | 仓内未见 ICC logo 素材 | 保持 | 中 |
| 5 | **不要把 ISO 15076-1 / IEC 61966 标准 PDF 塞进仓库或随包** | ✅ 已是现状（`tools/std/` 只有 ITU-R BT.2390/BT.2446，ITU 文档允许公开获取） | 保持；如未来要参考 ICC.1:2022 原文，**用 color.org 域的开放许可 PDF**，不要镜像 ISO webstore PDF | 中 |
| 6 | **保留证据**：把本文档与 color.org 规范页 URL、ICC Profile Library 许可原文存档 | 本文即存档 | 把本文纳入 `docs/review-2026-11-14/` 即可 | 低 |

**建议的 NOTICE 文本骨架**（不要直接改仓库，由收口人决定是否落地）：

```
5. Color space names / 色彩空间名称

   "Adobe RGB (1998)" is a trademark or registered trademark of Adobe Inc.
   "Display P3" is a color encoding defined by Apple Inc.
   "ProPhoto RGB" / "ROMM" is defined by IEC 61966-2-5.
   "sRGB" is defined by IEC 61966-2-1.
   "ICC" and the ICC logo are trademarks of the International Color Consortium.

   FfmpegPictureUI uses these names descriptively to identify standard
   color encodings. It is not affiliated with, sponsored by, or endorsed
   by Adobe, Apple, Kodak/ICC, the IEC, or the International Color
   Consortium. No ICC profile files from Adobe, Apple, or the ICC are
   bundled with this product; all embedded ICC profiles are generated at
   runtime from public colorimetric data.
```

---

## D. 可执行的最小验证（仓内只读实测）

### D.1 工具版本

| 工具 | 版本 | 命令 |
|---|---|---|
| exiftool | 13.61 | `exiftool -ver` |
| ffmpeg | git-2026-10-11-edadf711a9，`--enable-lcms2 --enable-gpl --enable-nonfree`（注：nonfree 是旧档 D-1 问题，与本文无关） | `ffmpeg -version` |
| cjxl | v0.12.0 | `cjxl --version` |
| avifenc | 1.4.2 (libaom 3.15.0) | `avifenc --version` |

### D.2 ffmpeg iccgen 输出的 profile 版本字节（本次实测）

命令（临时件落 `tests/output/_tmp/icc-spec-survey/`，符合工作区边界）：

```powershell
ffmpeg -y -hide_banner -loglevel error `
  -f lavfi -i color=c=gray:s=2x2:r=1 -frames:v 1 `
  -vf "format=rgb48le,zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=bt709:t=iec61966-2-1:m=bt709,iccgen" `
  tests/output/_tmp/icc-spec-survey/iccgen_bt709.png
```

exiftool 读数：

```
[ICC-header]  ProfileCMMType     : Little CMS
[ICC-header]  ProfileVersion     : 4.4.0
[ICC-header]  ProfileClass       : Display Device Profile
[ICC-header]  ColorSpaceData     : RGB
[ICC_Profile] ProfileCopyright   : No copyright, use freely
[ICC_Profile] ProfileDescription : RGB built-in
[ICC-header]  ProfileCreator     : Little CMS
```

**结论**：ffmpeg iccgen（经 lcms2）当前输出 **v4.4.0**，cprt 写 "No copyright, use freely"——这是 lcms2 内置 sRGB/bt709 profile 的固定文案，无版权负担。

### D.3 仓内既有产物的 ICC 字段

对 `tests/output/_heicavif_probe/a16.jpg` 跑同一组 exiftool 参数，读数与 D.2 完全一致（CMM=Little CMS, Version=4.4.0, Copyright="No copyright, use freely", Description="RGB built-in"）——证实这是仓内 ffmpeg 链路一贯产物。

### D.4 自研 `IccProfileBuilder` 写的版本字节

源码只读：`src/FfmpegGui/Services/IccProfileBuilder.cs:204`：

```csharp
w.Write(new byte[] { 4, 0, 0, 0 });   // 8   version 4.0.0 (主.次.修订.无效)
```

ICC profile 头 offset 8–11 的版本字段是 BCD 编码：`bytes[8]`=主版本，`bytes[9]`=次版本（高 4 位）+ 修订（低 4 位），`bytes[10..11]`=保留。`{4,0,0,0}` 即 v4.0.0。exiftool 把它读作 "4.0"。

`IccProfileService.ParseInfo`（同文件 `:653`）按同一规则读回：`$"{bytes[8]}.{bytes[9]}.{bytes[10]}"`。

自研 builder 写的 11 个 tag（`:144-149`）：`desc cprt wtpt bkpt rXYZ gXYZ bXYZ chad rTRC gTRC bTRC`——**matrix/TRC 最小 profile，不含 outputResponseTag、不含 D2B0、不含 mAB/mBA**（见 §C.5 专利结论）。

### D.5 `pack.ps1` 是否随包分发 profile 文件

`pack.ps1:205-219` 随包法律文件清单：`LICENSE`、`NOTICE`、`THIRD-PARTY-NOTICES.md`、`licenses/` 目录。**没有任何 .icc/.icm 复制步骤**。PLAN 目录里也没有 `iccs/` 预置 profile（`IccProfileService.FindUserNamedIcc` 读的是用户自己配置的 `AppSettings.IccDirectory`，默认空）。

**结论**：本仓二进制分发物**不携带任何第三方 ICC profile**；所有嵌入图片的 ICC 字节都是运行时由 ffmpeg iccgen 或自研 builder 现场生成。这是合规上最干净的形态，继续保持。

### D.6 临时件清单

本次调研在 `tests/output/_tmp/icc-spec-survey/` 下生成了 2 个临时 PNG（`iccgen_bt709.png` 545 B、`iccgen_p3d65.png` 530 B），用于 §D.2 实测。按仓规 `tests/output/` 整树 gitignore，不影响仓库；后续 `_sweep-output.py` 会按龄清理。

---

## E. 两张汇总表

### E.1 版本 × 消费者支持度矩阵（汇总自 §B）

| Profile 版本 | lcms2 | Windows WIC/mscms | macOS ColorSync | Chrome/Firefox | ffmpeg iccgen | 本仓建议 |
|---|---|---|---|---|---|---|
| v2 matrix/TRC | ✅ | ✅ | ✅ | ✅ | ✅（可写） | 不主动出（v4 已是事实标准） |
| v4.0/v4.3 matrix/TRC | ✅ | ✅ | ✅ | ✅ | — | ✅ 本仓自研 builder 输出此形态 |
| v4.4 matrix/TRC | ✅ | ✅ | ✅ | ✅ | ✅（实测输出） | ✅ ffmpeg iccgen 输出此形态 |
| v4 LUT-based（mAB/mBA） | ✅ | ⚠️ 部分 | ❌ Apple 明确不支持 | ⚠️ 浏览器不跑 | ✅ | ❌ 不做 |
| iccMAX / MPE（ICC.2） | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ 不做 |

### E.2 许可层面 × 结论 × 依据（汇总自 §C）

| 层面 | 结论 | 一手依据 |
|---|---|---|
| ICC 规范 PDF（color.org 域） | 类 BSD 开放许可，可引用/摘录数值/再分发 PDF（保留版权声明） | ICC.1:2010-12 PDF Copyright notice；ICC.1:2022 PDF 同位置 |
| ISO 标准 PDF（iso.org webstore） | 受版权保护，不得复制/再分发；用 ICC 版 PDF 即可 | <https://www.iso.org/copyright.html> |
| "符合 ICC vX" 声称 | 不需要 ICC 授权/认证；无 conformance logo 程序 | color.org icclogos 页（仅支持者 logo） |
| ICC 自有 profile | 可复制/分发/嵌入/出售无限制（保留 cprt） | <https://registry.color.org/profile-library/> |
| 本仓自研 profile | 版权归本项目；cprt 已正确署名；嵌入输出图片合法 | `IccProfileBuilder.cs:133-134` |
| 第三方 .icc 文件 | 不要随包分发；Adobe RGB 1998 是 Adobe 商标，Display P3 是 Apple 色彩编码 | Adobe RGB 1998 PDF Trademark Information；DICOM CP |
| sRGB | IEC 61966-2-1 标准名，描述性使用 OK | <https://registry.color.org/rgb-registry/srgb> |
| 专利 | outputResponseTag 有 Kodak FRAND 专利但该 tag 可选；本仓不写 | ICC.1:2022 §0.10 |
| ICC logo | 不要印在产品上 | <https://www.color.org/icclogos.xalter> |

---

## F. 来源清单

访问日期除特别注明外均为 **2026-11-14**。

| # | 标题 | 机构 | URL |
|---|---|---|---|
| 1 | ICC.1 v4 Specification（历史版本列表） | International Color Consortium | <https://www.color.org/v4spec/> |
| 2 | ICC.1 v2 Specification（v2 历史版本列表） | ICC | <https://www.color.org/v2spec/> |
| 3 | Current ICC specifications（现行规范下载页） | ICC | <https://www.color.org/specifications/> |
| 4 | ICC.1:2022 (Profile version 4.4.0.0) PDF | ICC | <https://archive.color.org/specification/ICC.1-2022-05.pdf> |
| 5 | ICC.1:2010-12 (v4.3) PDF（含开放许可版权段） | ICC | <https://www.color.org/specification/ICC1v43_2010-12.pdf> |
| 6 | ICC.2:2019 PDF | ICC | <https://www.color.org/specification/ICC.2-2019.pdf> |
| 7 | iccMAX 介绍页（含 ICC.2:2023 批准日期） | ICC | <https://www.color.org/iccmax/index.xalter> |
| 8 | Current status of iccMAX | ICC | <https://www.color.org/iccmax-status/> |
| 9 | ICC.2:2017 PDF（MPE 扩展说明） | ICC | <https://www.color.org/iccmax/ICC.2-2017.pdf> |
| 10 | ISO 15076-1:2025 OBP（基于 ICC.1:2022） | ISO | <https://www.iso.org/obp/ui/#!iso:std:86740:en> |
| 11 | ISO 15076-1:2010 OBP（§0.10 专利声明） | ISO | <https://www.iso.org/obp/ui/en/#!iso:std:54754:en> |
| 12 | ICC Technote: Embedding ICC profiles（容器×版本矩阵） | ICC | <https://archive.color.org/files/technotes/ICC-Technote-ProfileEmbedding.pdf> |
| 13 | Profile Library（ICC 自有 profile 许可原文） | ICC Registry | <https://registry.color.org/profile-library/> |
| 14 | ICC FAQ: licensing terms for ICC-created profiles | ICC | <https://color.org/faqs.xalter> |
| 15 | sRGB Appearance profile terms of use | ICC Registry | <https://registry.color.org/rgb-registry/srgb_appearance> |
| 16 | sRGB 注册条目（= IEC 61966-2-1:1999） | ICC Registry | <https://registry.color.org/rgb-registry/srgb> |
| 17 | ICC Logos 使用指南 | ICC | <https://www.color.org/icclogos.xalter> |
| 18 | Little CMS 颜色引擎自述（ICC 4.4 全实现） | LittleCMS | <https://littlecms.com/color-engine/> |
| 19 | Little CMS 2.19 release notes | LittleCMS | <https://www.littlecms.com/blog/2026/04/17/lcms2-2.19/> |
| 20 | Apple: ColorSync 官方页（Mac OS X Tiger fully supports ICC v4） | Apple | <https://www.apple.com/pro/photo/colorsync.html> |
| 21 | Apple Developer Forums: LUT-based ICC profiles never supported on Apple | Apple | <https://developer.apple.com/forums/thread/807131> |
| 22 | EIZO: 浏览器 ICC profile 版本兼容实测（2019-10） | EIZO | <https://www.eizo.com/library/management/color-management_icc-profiles/> |
| 23 | W3C PNG Third Edition Implementation Report（iCCP 允许任何 ICC.1 版本） | W3C | <http://w3c.github.io/png/Implementation_Report_3e/> |
| 24 | W3C: PNG Third Edition 成为推荐标准（2025-06-24） | W3C | <https://www.w3.org/news/2025/portable-network-graphics-png-specification-third-edition-is-now-a-w3c-recommendation/> |
| 25 | WebP Container Spec（ICCP chunk 语义） | Google | <https://developers.google.cn/speed/webp/docs/riff_container> |
| 26 | RFC 9649 WebP Image Format | IETF | <https://www.rfc-editor.org/in-notes/rfc9649.pdf> |
| 27 | ISO/IEC 18181-1:2022 JPEG XL（Annex B ICC profile） | ISO | <https://www.iso.org/obp/ui/en/#!iso:std:77977:en> |
| 28 | libjxl Encoder API（JxlEncoderSetICCProfile） | libjxl | <https://libjxl.readthedocs.io/en/stable/api_encoder.html> |
| 29 | Adobe RGB (1998) Color Image Encoding PDF（商标声明） | Adobe | <https://www.adobe.com/digitalimag/pdfs/AdobeRGB1998.pdf> |
| 30 | Adobe ICC Profiles Bundling License | Adobe | <https://www.adobe.com/support/downloads/iccprofiles/icc_eula_win_dist.html> |
| 31 | DICOM CP: Display-P3 = Apple, Inc. | NEMA | <https://dicom.nema.org/medical/dicom/CP/cp2474_vp_addnewcolorspaces.pdf> |
| 32 | ISO Copyright 政策 | ISO | <https://www.iso.org/copyright.html> |
| 33 | Exiv2 wiki: JPEG APP2 ICC 分片 | Exiv2 | <https://dev.exiv2.org/projects/exiv2/wiki/The_Metadata_in_JPEG_files> |
| 34 | Windows WCS 文档 | Microsoft | <https://learn.microsoft.com/windows/win32/wcs/> |

**仓内只读证据**（非外部 URL）：
- `src/FfmpegGui/Services/IccProfileBuilder.cs`（v4.0.0 字节写入、11 tag 清单、cprt 文案）
- `src/FfmpegGui/Services/IccProfileService.cs`（ResolveNamedIcc 路由、IsValidIccProfile、ParseInfo 版本解析）
- `src/FfmpegGui/Services/ColorSpaceRegistry.cs`（注册的色彩空间 Key 清单）
- `pack.ps1`（随包法律文件清单，无 .icc）
- `THIRD-PARTY-NOTICES.md`、`NOTICE`（现行第三方声明）
- 仓内实测：`tests/output/_heicavif_probe/a16.jpg` exiftool 读数、`tests/output/_tmp/icc-spec-survey/iccgen_bt709.png` exiftool 读数
