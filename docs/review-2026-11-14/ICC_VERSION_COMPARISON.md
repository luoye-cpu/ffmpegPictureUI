# ICC 各版本能力与选型对比

**调研日期**：2026-11-14
**配套文档**：本文是 `ICC_SPEC_VERSIONS_AND_LICENSING.md`（版本沿革 + 许可合规）的姊妹篇。那份讲"有哪些版本、合不合规"，本文讲"各版本能干什么、本产品该选哪个"。需要复述版本号/许可条款时只给结论并指回那份文档的 §A/§C。
**范围**：v2 (ICC.1:2001-04 / 2.4.0) · v4.0 (ICC.1:2004-04/10) · v4.3 (ICC.1:2010-12) · v4.4 (ICC.1:2022-05) · ICC.2 (iccMAX，2019/2023)。

> **声明**：工程技术对比，不构成法律意见。

---

## 1. 主对比表（版本 × 维度）

| 维度 | **v2** (2.4.0, 2001) | **v4.0** (2004) | **v4.3** (2010-12) | **v4.4** (2022-05) | **ICC.2** (iccMAX, 2019/2023) |
|---|---|---|---|---|---|
| **① 矩阵/TRC 表达力** | `rXYZ/gXYZ/bXYZ` + `rTRC/gTRC/bTRC`（`curveType`/`para`）；`desc` 用 `textDescriptionType`（ASCII，≤64 KB）；PCS 白点可为 D50 或 D65（v2 历史上常写错）；**无 `chad`** | 同矩阵能力；`desc` 改 `mluc`（UTF-16BE 多语言）；**强制 `chad`**（chromatic adaptation tag）；PCS 白点**强制 D50**；`para` 扩展到 functionType 0–4 | 与 v4.0 结构相同；勘误澄清，无新 tag 类型 | 与 v4.3 结构相同；新增 CICP tag（见 ④） | 完全包含 v4.4 矩阵/TRC；另加 MPE 路径 |
| **② LUT 表达力** | `A2B0/A2B1/A2B2` + `B2A0/B2A1/B2A2` 用 `lut8Type`/`lut16Type`；A→B 与 B→A **不能互逆**（v2 著名缺陷，proofing 场景翻车） | 新 `mAB `/`mBA `（`lutAToBType`/`lutBToAType`）：A/B/M 三段曲线 + 可选矩阵 + 可选 CLUT；**A→B 与 B→A 可精确互逆**；支持 round-trip proofing | 同 v4.0；勘误 | 同 v4.3 | 在 mAB/mBA 之上加 MPE（见 ③） |
| **③ MPE / 多进程元素** | ❌ 无 | ❌ 无 | ❌ 无 | ❌ 无（CICP tag 是数据 tag，不是可编程变换） | ✅ **仅 ICC.2 有**：栈式可编程变换计算器、单段曲线、N 维 LUT（整数编码）、色彩外观模型（CIECAM02/16）、稀疏矩阵、tint array。来源：ICC.2:2017 PDF 原文 |
| **④ HDR 能力** | ❌ 无原生 HDR 表达。PQ/HLG 曲线只能采样成 `curv` LUT 塞进 TRC（本仓现状）；容器侧无 CICP 标记时消费者无法知道这是 PQ | ❌ 同 v2。v4.0/v4.3 规范里**没有 CICP tag**，HDR 只能靠 TRC 采样 + 容器侧 nclx/cICP | ❌ 同 v4.0。v4.3 (2010) 时 PQ/HLG 还没成为主流（SMPTE ST 2084 是 2014） | ✅ **v4.4 首次纳入 CICP tag**（`cicp` tag + `cicpType`），直接携带 primaries/transfer/matrix 三个 CICP 枚举，消费者不必解 TRC 采样表就能认出 PQ/HLG。来源：color.org v4spec 页原文 "It includes a Coding Independent Code Points (CICP) tag and type for video signal identification, which supports HDR processing"。v4.4 之后又加了 **Headroom Adaptive Gain Curve (HAGC) amendment**（替换早期 ADGC tag）与 **CICP amendment**（primaries=unspecified 时指向 profile 内 primaries） | 理论上最优雅：Max Derhak 2019 演讲《Thoughts on HDR Colour Management Using iccMAX》提出用 MPE 表达 HLG/PQ + 可插拔全局 tone mapping；但**无主流 CMM 实现**（lcms2 不实现 MPE，见 ⑥） |
| **⑤ 宽色域能力** | matrix/TRC 可表达 Display P3/BT.2020/ProPhoto 的原色矩阵（数学上无限制）；但 v2 的 D65/D50 混乱 + 无 chad 导致跨 CMM 结果不一致 | matrix/TRC 可表达；chad 强制后跨 CMM 一致 | 同 v4.0 | 同 v4.3；CICP tag 让 Display P3/BT.2020 可被消费者直接枚举 | 同 v4.4；MPE 可做色域压缩（gamut mapping） |
| **⑥ 容器/查看器兼容** | PNG iCCP 第二版只引用 v2（inadvertent）；JPEG APP2/WebP ICCP/AVIF/JXL 均无版本限制。lcms2/WIC/ColorSync/浏览器全绿 | 全绿（matrix/TRC 形态）；LUT 形态在 Apple ColorSync **不支持**（Apple 开发者论坛 2025-11 原文："LUT-based ICC profiles have never been supported on Apple platforms"——注意限定是**显示类 profile**，输入/打印类不受此限） | 同 v4.0 | 同 v4.3；CICP tag 是 v4.4 新增，**旧 CMM（lcms2 < 2.16 左右、旧 WIC）会忽略未知 tag**，不影响 matrix/TRC 主体解析 | ❌ lcms2 官网自述只实现到 ICC 4.4，不提 ICC.2；WIC/ColorSync/浏览器均不实现 MPE。**iccMAX profile 在真实消费链路上等于无标注** |
| **⑦ profile 体积（实测）** | 最小 matrix/TRC ≈ **500–700 B**（lcms test0.icc=568 B）；LUT-based 打印 profile 可达 **数百 KB**（lcms test1=557 KB、test2=654 KB） | parametric/matrix ≈ **580–600 B**（skcms Display_P3_parametric=584 B、sRGB_parametric=596 B）；LUT 形态 ≈ **2.6 KB**（skcms Display_P3_LUT=2612 B、sRGB_LUT=2624 B，是 parametric 的 ~4.5×） | 同 v4.0 | CICP HDR profile ≈ **8.7 KB**（skcms Rec2020_PQ_cicp.icc=8708 B，v4.4.0，CMM=jxl）；ICC 官方 sRGB v4 appearance profile=63.9 KB（含 perceptual LUT） | MPE 会膨胀到 **数十 KB 起**（栈式程序 + N 维 LUT + 外观模型参数）；无实测样本（仓内无 iccMAX 测试件），结构性估算 |
| **⑧ 本产品选型** | 不主动出（v4 已是事实标准；退回 v2 要拆 chad、改 desc 类型，零收益） | ✅ **本仓自研 builder 当前形态**（`IccProfileBuilder.cs:204` 写 `{4,0,0,0}`） | ✅ ffmpeg iccgen 实测输出 v4.4.0（不是 v4.3）；v4.3 与 v4.4 在 matrix/TRC 上对消费者无差别 | ✅ 未来若要在 ICC 里直接携带 HDR 元数据（不依赖容器 CICP），可升级到 v4.4 + CICP tag；当前不需要 | ❌ 不做（无消费端） |

---

## 2. 关键判断展开

### 2.1 v4.0 vs v4.3：差异比想象的小

ISO 15076-1:2025 OBP §1 原文："The second edition, ISO 15076-1:2010, was based on ICC revision 4.3, which is **fully backward compatible with 4.2**."（<https://www.iso.org/obp/ui/#!iso:std:86740:en>）

也就是说：
- **v4.0（2004）→ v4.3（2010）**：中间经历 v4.1/v4.2 小修订，v4.3 相对 v4.2 是勘误+澄清，**没有新 tag 类型、没有新变换结构**。
- 对 profile 生成方而言，v4.0.0 字节与 v4.3.0 字节在 matrix/TRC 这种最小 profile 上**对消费者没有可感知差别**。
- 本仓自研 builder 写 v4.0.0、ffmpeg iccgen 写 v4.4.0——两者都能被 lcms2/WIC/ColorSync/Chrome 正确解释，**不需要为了"对齐版本号"而改自研 builder**。

### 2.2 HDR：v4.4 之前 ICC 没有原生 HDR 表达

这是本轮最需要澄清的点：

- **v2/v4.0/v4.3 规范里没有 CICP tag**。PQ (SMPTE ST 2084, 2014) 与 HLG (ARIB STD-B67, 2015) 出现时，ICC.1 还停在 v4.3 (2010)。
- 在 v4.4 之前，HDR 内容的色彩标注**只能靠两条路**：
  1. **容器侧 CICP**：PNG cICP chunk、HEIF nclx box、JXL color encoding、WebP/AVIF 的色彩标记——这是视频/图像容器自己的机制，不依赖 ICC profile。
  2. **把 PQ/HLG 曲线采样成 `curv` LUT 塞进 rTRC/gTRC/bTRC**：本仓 `IccProfileBuilder.CurveFor` 对 BT.2020 PQ/P3 PQ 就是这么做的（1024-entry LUT，`IccProfileBuilder.cs:75-79`）。但消费者看到一个"gamma 2.0+ 的 RGB profile"并不知道这是 PQ 还是 HLG 还是 BT.1886——除非容器侧同时给了 CICP。
- **v4.4 (2022) 才把 CICP tag 纳入规范**，让 ICC profile 自己就能携带 primaries/transfer/matrix 枚举。skcms/libjxl 的 `Rec2020_PQ_cicp.icc`（8708 B，v4.4.0，描述 "RGB_D65_202_Rel_PeQ"）就是这种形态的实例。
- **v4.4 之后的 amendments**（color.org v4spec 页"Amendments since ICC.1:2022"段）：
  - **Headroom Adaptive Gain Curve (HAGC) tag and type**：用于 HDR-to-SDR tone mapping，替换早期 ADGC tag；Dolby 提交了专利声明（<https://registry.color.org/patent-registry/files/2025-01-24-dolby.pdf>）。
  - **Amendment to CICP tag**：允许 CICP 里 primaries=unspecified 时指向 profile 内编码的 primaries。
- **ICC.2/iccMAX 理论上能更优雅地处理 HDR**（Max Derhak 2019 演讲），但没有消费端。

**本产品 HDR 出口该靠 CICP 还是 ICC？**
→ **靠 CICP（容器侧），ICC 只做 SDR 回退描述。** 理由：
1. 仓内现状已是如此：`IccProfileService.GetOrGenerateStandardIcc` 对 `smpte2084`/`arib-std-b67` 直接返回 null（`:338-339`），注释写"iccgen 不支持 HDR 传递……HDR 必须走 CICP 枚举"。
2. `PlanPolicy.ExitWritesIccFor`（`ColorIntentFactory.cs:27`）：`fmt is not ("avif" or "webm")`——AVIF/WebM 静态出口不写 ICC，只走 CICP。
3. 即使升级到 v4.4 + CICP tag，旧消费者（lcms2 老版本、旧 WIC）会忽略未知 tag，等于没写；而容器侧 CICP（PNG cICP、HEIF nclx、JXL color encoding）是现代浏览器/系统都认的。
4. 本仓自研 builder 当前写 PQ 用 1024-entry `curv` LUT（约 +4 KB），这在 SDR 回退路径上够用；不需要为了 HDR 去写 v4.4 CICP tag。

### 2.3 LUT-based 的兼容性陷阱：Apple 的限定

Apple 开发者论坛 2025-11-12 官方回复（<https://developer.apple.com/forums/thread/807131>）原文：

> "LUT-based ICC profiles have never been supported on Apple platforms. Previously you may have been able to provide LUT-based ICC profiles to our routines but it was a bug that our routines were not properly checking parameters and raising errors for LUT-based ICC profiles. This has been fixed in recent updates…"

**注意限定**：这是针对**显示类 profile（mntr）**的系统色彩管理路径。输入类（scnr）/打印类（prtr）profile 在专业软件里仍走 LUT。本产品输出的是**嵌入图片的显示类/色彩空间类 profile**，不是给显示器装的校准 profile——所以这条限制对"图片嵌入 ICC"的实际影响是：**Apple Photos/Preview/Safari 在解释图片内嵌的 LUT-based profile 时可能出错或回退 sRGB**。matrix/TRC profile 在全平台都稳。

### 2.4 体积实测汇总（本轮新量）

| Profile 形态 | 实测体积 | 样本 | 版本 |
|---|---|---|---|
| 最小 matrix/TRC（ffmpeg iccgen bt709，PNG 内嵌） | 545 B PNG（ICC 本体约 500 B） | `tests/output/_tmp/icc-spec-survey/iccgen_bt709.png` | 4.4.0 |
| parametric sRGB（skcms） | 596 B | `tools/src/libjxl/third_party/skcms/profiles/mobile/sRGB_parametric.icc` | 4.0.0 |
| parametric Display P3（skcms） | 584 B | `…/mobile/Display_P3_parametric.icc` | 4.0.0 |
| LUT-based Display P3（skcms） | 2612 B | `…/mobile/Display_P3_LUT.icc` | 4.0.0 |
| LUT-based sRGB（skcms） | 2624 B | `…/mobile/sRGB_LUT.icc` | — |
| CICP HDR profile（skcms Rec2020 PQ） | 8708 B | `…/misc/Rec2020_PQ_cicp.icc` | 4.4.0 |
| ICC 官方 sRGB v4 appearance（含 perceptual LUT） | 63928 B | `…/misc/sRGB_ICC_v4_beta.icc` | — |
| 完整打印 LUT profile（lcms 测试件） | 557–654 KB | `tools/src/jpegli/third_party/lcms/plugins/test_profiles/test1.icc` / `test2.icc` | — |

**结论**：matrix/TRC profile 在 0.5–1 KB 量级，对 JPEG APP2 分片、PNG iCCP、WebP ICCP、AVIF Prof box、JXL ICC box 都无压力。LUT-based 跳到 2.6 KB（display）或数百 KB（print），ICC.2 MPE 会更大——但本产品不需要。

---

## 3. 选型结论表（场景 → 建议）

| 场景 | 建议版本 | 理由 | 风险 |
|---|---|---|---|
| SDR 出口（PNG/JPEG/WebP/JXL/AVIF 可写 ICC 时） | **v4 matrix/TRC**（自研 builder v4.0.0 或 ffmpeg iccgen v4.4.0 均可） | 全平台绿；0.5 KB；chad/mluc 已写对 | 无 |
| 宽色域 SDR（Display P3 / Adobe RGB / ProPhoto） | **v4 matrix/TRC**（1024-entry `curv` LUT 仅用于 PQ/HLG 传递，SDR 宽色域用 `para` type3） | 矩阵能表达任意原色；Apple/mscms 都认 matrix | 无 |
| HDR 出口（PQ/HLG） | **容器侧 CICP，不依赖 ICC**；若必须带 ICC 标注则 v4 matrix/TRC + 1024-entry PQ/HLG `curv` LUT | v4.4 CICP tag 太新，旧消费者忽略；容器 CICP 是现代浏览器/系统都认的 | 旧消费者可能把 PQ 当 gamma~2.0 显示（过亮）——这是 CICP 缺失时的固有问题，不是 v4 能解决的 |
| 打印/CMYK/Proofing | ❌ 本产品不涉及 | — | — |
| 专业色彩外观管理（CIECAM/gamut compression） | ❌ 不做 iccMAX | 无消费端 | — |
| 退回 v2 | ❌ 不建议 | 要拆 chad、改 desc 为 ASCII textDescriptionType、PCS 白点不再强制 D50——收益为零，反而引入 v2 跨 CMM 不一致的老问题 | — |
| 统一自研 builder 到 v4.4.0 | ✅ **已采纳（2026-11-14 用户拍板）** | 全容器统一 v4.4；与 lcms2/iccgen 一致；未来加 cicp 时不必再改版本位。代价见 §6.6 实测 | 改版本字节后旧缓存按 `WriterVersion` 自动作废，无兼容风险 |
| **JPEG 之外全量用 v4.4** | ✅ **已采纳（用户拍板）**；JPEG 默认 v4.4，用户可选 v2 兼容档 | 见 §6 决策分析 | 见 §6.7 JPEG 默认争议（证据指向 v2 更稳，决定选 v4.4 统一性优先） |

---

## 4. 给本产品的落地建议

### 4.1 要改的点（按优先级）

1. **不需要改自研 builder 的版本字节**（`IccProfileBuilder.cs:204` 的 `{4,0,0,0}`）。v4.0.0 与 v4.4.0 在 matrix/TRC 形态下对所有消费者等价。把"要不要升 v4.4"留到"真要写 CICP tag 携带 HDR 元数据"那天再做。
2. **HDR 路径继续走 CICP，不要试图用 ICC profile 表达 PQ/HLG 语义**。仓内 `IccProfileService.GetOrGenerateStandardIcc` 对 smpte2084/arib-std-b67 返回 null 的决策正确，保持。
3. **不要引入 LUT-based（mAB/mBA）profile 生成路径**。Apple ColorSync 对显示类 LUT profile 的限制（§2.3）意味着图片内嵌 LUT profile 在 Apple 生态可能被回退 sRGB。matrix/TRC 是全平台唯一稳的形态。

### 4.2 不要改的点

1. **不要做 iccMAX/ICC.2**。lcms2 不实现 MPE、WIC/ColorSync/浏览器不实现 MPE，生成出来等于无标注。
2. **不要退回 v2**。v4 的 chad/mluc/D50 强制是互操作性改进，退回 v2 是开倒车。
3. **不要为了"版本号对齐"而统一自研 builder 与 ffmpeg iccgen 的版本字节**。两者都能被正确消费，差异无工程意义。
4. **不要在 AVIF/WebM 出口强行写 ICC**。`PlanPolicy.ExitWritesIcc=false` 是对的（AGENTS.md §7："AVIF 静态出口写不进 ICC（PlanPolicy.ExitWritesIcc=false）：可 CICP 命名 ⇒ 零改动携带；不可命名 ⇒ S2 携带契约拒绝"）。

### 4.3 未来若要升级到 v4.4 + CICP tag 的触发条件

只有当以下**全部**满足时才值得做：
- lcms2 ≥ 2.16（已支持 CICP tag 解析）+ WIC 也跟上；
- 本产品需要在**不支持容器 CICP 的旧格式**（如某些老旧 JPEG 工作流）里携带 HDR 标注；
- 已经实测过 Apple Photos/Preview/Chrome 能正确解释带 CICP tag 的 v4.4 profile。

在此之前，v4.0.0 自研 + v4.4.0 ffmpeg iccgen 的组合就是最优解。

---

## 5. 与上一份报告的交叉引用

- 版本沿革表（ICC.1:1998→2022、ICC.2:2016→2023、对应 ISO 号）→ 见 `ICC_SPEC_VERSIONS_AND_LICENSING.md` §A.1/A.2
- 许可合规（ICC 规范 PDF 开放许可、ISO PDF 版权、ICC 自有 profile "embedded without restriction"、Adobe/Apple 商标、outputResponseTag 专利）→ 见那份 §C
- 容器承载规范（PNG iCCP 历史限制与第三版澄清、JPEG APP2 分片、WebP ICCP、AVIF Prof、JXL Annex B）→ 见那份 §B.2
- 仓内实测读数（exiftool 13.61、ffmpeg git-2026-10-11 --enable-lcms2、cjxl 0.12.0、avifenc 1.4.2）→ 见那份 §D

本文新增的实测：skcms/lcms 测试件体积对比（§1⑦、§2.4）、v4.4 CICP tag 纳入规范的时间点（§2.2）、Apple LUT-based 限制的限定条件（§2.3）。

---

## 6. 已裁决：全量 v4.4（2026-11-14 用户拍板）

> **本节状态**：原分析结论为"不建议一刀切"，证据见 §6.2–§6.5。用户在 2026-11-14 基于统一性/未来性考虑拍板：**全量自生成 ICC 走 v4.4；JPEG 族额外给用户可选 v2.0 兼容档（默认仍为 v4.4）**。代码已落地验通（`FfmpegGui.dll=2EF23939079C01FD`、`ServiceProbe.dll=373943DFB609333C`）。
>
> **本节保留原证据链作为决策输入，不删除、不改写结论**——那是这次拍板的依据，删了就成了"当初就同意"的假记录。原建议态的文字与已采纳决定并列呈现，读者可自行复核。

### 6.1 裁决结果

| 项 | 决定 |
|---|---|
| 自生成 ICC 默认版本 | **v4.4**（全容器统一） |
| JPEG 族额外选项 | 用户可选 **v2.0** 兼容档；默认仍 v4.4 |
| 源图自带 ICC | 原样携带，不重写版本位（原证据 5 维持） |
| ffmpeg `iccgen` 产物 | 已是 v4.4.0，不变 |
| HDR | 仍走容器侧 CICP；ICC 不主动写 `cicpTag`（原方案 C 未触发，见 §6.5） |

### 6.2 原分析证据（保留，供复核；以下为"建议不升"时的输入）

> ⚠️ 本节五条证据是原分析"不建议一刀切"的依据。用户拍板后这些证据**仍然成立**（它们描述的是技术事实），但不再决定产品方向。

**证据 1：lcms2 只按主版本=4 走解析路径，不区分 4.0/4.3/4.4。**

仓内 vendored lcms2 源码 `tools/src/jpegli/third_party/lcms/src/cmssamp.c` 里版本判断原文：

```c
if ((cmsGetEncodedICCversion(hProfile) >= 0x4000000) && ...)
```

即 lcms2 把所有 `0x04000000`–`0x04FFFFFF` 的 profile 都走同一条 v4 解析路径。`cmsvirt.c` 里 lcms2 自己生成的内置 profile（sRGB/Adobe RGB/XYZ/Lab 等）现在统一 `cmsSetProfileVersion(hICC, 4.4)`——这是 lcms2 作者的选择，不是"4.4 比 4.0 多了什么解析能力"。仓内实测 ffmpeg iccgen 输出 4.4.0 正是因为 lcms2 现在这么写。

**含义（原分析）**：把自研 builder 的 `{4,0,0,0}` 改成 `{4,4,0,0}`，lcms2 解析路径完全不变。**用户拍板后此含义不变**——升版本位不带来解析端变化，但带来统一标识与未来性。

**证据 2：v4.4 的新 tag 都是可选 tag，不写就不增加字节。**

v4.4 相对 v4.3 的新增物（color.org v4spec 页原文）：
- `cicpTag` + `cicpType`（CICP 枚举）
- `metadataTag` + `dictType`（键值对元数据）
- Headroom Adaptive Gain Curve（HAGC）tag + type（HDR-to-SDR tone mapping）
- CICP amendment（primaries=unspecified 时指向 profile 内 primaries）

这些都是**可选 tag**。ICC profile 的 tag 目录是变长的：写了哪个 tag 就进目录，不写就不进。版本位本身只占 offset 8–11 共 4 字节。

**体积实测对照**（§2.4）：
- ffmpeg iccgen bt709（v4.4.0，无 cicp tag）：ICC 本体约 500 B
- skcms sRGB_parametric（v4.0.0）：596 B
- skcms Rec2020_PQ_cicp（v4.4.0，**带 cicp tag**）：8708 B

**含义（原分析）**：版本位从 4.0 改 4.4 不增字节；真正让体积从 0.5 KB 跳到 8.7 KB 的是**写了 cicp tag 本身**，不是版本号。

**证据 3：旧 CMM 对未知 tag 的处理是"忽略"，不是"报错"——但写了 cicp tag 反而有风险。**

ICC 规范规定 CMM 遇到不认识的 tag 必须跳过（tag 目录是自描述的，未知 tag 不影响已知 tag 解析）。所以：
- 一个 v4.4 profile 带了 `cicpTag`，旧 lcms2（< 2.16）/旧 WIC 会忽略它，按 matrix/TRC 正常解析——**不会更糟**。
- **但**：如果这个 profile 的 TRC 是 PQ 曲线（1024-entry LUT），旧消费者看到一个"gamma~2.0 的 RGB display profile"会按 SDR 解释，把 PQ 信号当 gamma 2.0 显示——**过亮**。这正是仓内 `IccProfileService.GetOrGenerateStandardIcc` 对 smpte2084/arib-std-b67 返回 null 的原因。
- 写 `cicpTag` 的目的就是让新消费者知道"这是 PQ"。但旧消费者还是不知道。**所以 cicpTag 是"新消费者的额外提示"，不是"旧消费者的修复"**。

**证据 4：JPEG 与其他容器在版本位这件事上没有差异。**

JPEG APP2 分片（`ICC_PROFILE\0` + seq/total）只是字节流的容器，不关心 profile 内部版本位。PNG iCCP、WebP ICCP、AVIF Prof、JXL ICC box 同理——它们都是"把 ICC profile 字节流原样塞进去"，版本位是 profile 内部的事。

JPEG 的特殊性在别处：APP2 分片上限 ~16.7 MB；JPEG 生态最老（见 §6.7）；JPEG 是照片格式，源图自带 ICC 的概率最高。

**证据 5：源 ICC 应原样携带，不要重写版本位。**

- 仓内 `IccProfileService.GetFaithfulIcc`（`:414`）对 ProPhoto RGB 走"faithful path"——源 profile 字节原样复制到输出，不重写；
- 重写版本位会丢掉源 profile 的私有 tag、厂商意图、`dmnd`/`dmdd` 设备描述、MD5 profile ID；
- **"版本位升级"本身不产生任何能力提升**（证据 1：lcms2 不区分）。

**此证据在裁决后仍然有效**：源 ICC 原样携带不变。

### 6.3 容器 × 版本 × 收益/风险表（原分析表，已被裁决覆盖）

| 容器 | 写 v4.0.0 | 写 v4.4.0（无 cicp tag） | 写 v4.4.0（带 cicp tag） | 原分析裁决 | 现裁决状态 |
|---|---|---|---|---|---|
| PNG iCCP | 体积 ~600 B；全平台绿 | 体积 ~600 B；全平台绿；lcms2 解析路径不变 | 体积 ~8.7 KB；新消费者知道 HDR，旧消费者忽略 tag 但仍按 TRC 误判 SDR | v4.0.0 即可；HDR 走 PNG cICP chunk | **已覆盖**：PNG 出口写 v4.4，不写 cicp |
| WebP ICCP | 同上 | 同上 | 同上 | v4.0.0 即可；WebP 有色彩配置标记 | **已覆盖**：WebP 出口写 v4.4，不写 cicp |
| JPEG APP2 | 同上 | 同上 | 同上 | v4.0.0 即可；JPEG 无原生 CICP，HDR 场景若必须带 ICC 才考虑 v4.4+cicp | **已覆盖**：JPEG 默认 v4.4，用户可选 v2 兼容档；不写 cicp |
| AVIF `Prof` box | 仓内 `ExitWritesIcc=false`，不写 ICC | — | — | 走 nclx/cICP，不写 ICC | 不变（不写 ICC） |
| JXL ICC box | 体积 ~600 B；libjxl 全支持 | 同上 | libjxl 自己就产 v4.4+cicp（skcms Rec2020_PQ_cicp.icc 实测） | 跟随编码器；本产品不手动构造 JXL 内嵌 ICC | 不变（跟随编码器） |
| 源图自带 ICC | **原样携带**，不重写版本位 | — | — | 任何版本位都不重写 | **不变**（原样携带） |

### 6.4 三个可选方案（原分析表，现裁决状态）

| 方案 | 做法 | 代价 | 收益 | 原分析裁决 | 现裁决状态 |
|---|---|---|---|---|---|
| **A. 保持现状（v4.0 自研 + v4.4 iccgen 共存）** | 自研 builder 写 v4.0.0；ffmpeg iccgen 出 v4.4.0；源 ICC 原样携带 | 零改动 | 全平台绿；体积最小 | ✅ 原分析推荐 | ❌ **未采纳**（用户选择统一） |
| **B. 全量升 v4.4** | 自研 builder 版本位改 `{4,4,0,0}`；所有自生成 profile 标 v4.4 | 改版本字节；旧缓存按 `WriterVersion` 自动作废 | 统一标识；与 lcms2/iccgen 一致；未来加 cicp 时不必再改版本位 | ❌ 原分析认为无实际收益 | ✅ **已采纳**（用户拍板） |
| C. 特定出口升 v4.4+cicp | 只在"JPEG HDR 兜底"出口生成 v4.4+cicp tag | 要写 `cicpType` 字节（约 8.7 KB）；要实测消费者解释 | 新消费者能正确识别 HDR 传递函数 | ⚠️ 条件触发 | ⏸️ **未触发**（HDR 仍走容器 CICP；原触发条件转为回退判据，见 §6.5） |

### 6.5 原方案 C 触发条件 → 转为"将来若要回退 v4.4"的判据

原分析列出的"何时才该写 cicpTag"四条件，在全量 v4.4 落地后**不废弃**，而是反过来作为"何时该停止 v4.4 默认、回退到 v2 兼容档"的判据：

1. 目标容器**无法携带 CICP 标记**（JPEG 是典型）；
2. 输出内容**确实是 HDR**（PQ/HLG），且不能转 SDR；
3. **实测发现**主流消费者（Chrome/Safari/Windows Photos/ffmpeg 链路）对带 cicpTag 的 v4.4 profile 有解析问题（旧 CMM 忽略 tag 导致按 TRC 误判 SDR ⇒ 过亮）；
4. 旧消费者忽略 `cicpTag` 时的退化行为在产品上**不可接受**。

若以上全部命中，才考虑：(a) JPEG HDR 出口回退 v2 兼容档，或 (b) 停止在 ICC 内写 cicpTag（当前本来就没写）。当前未命中任何一条。

### 6.6 落地后的实测代价（真实读数）

代码落地后实测，自生成 profile 体积与精度如下：

**v4.4 自生成 profile：实测 544 B（10 tag）**

| Tag | 形态 | 字节 |
|---|---|---|
| `desc` | mluc | 54 |
| `cprt` | mluc | 78 |
| `rTRC/gTRC/bTRC` | `para` type3（三通道共享一份） | 32 |
| `rXYZ/gXYZ/bXYZ` + `wtpt` | 4×XYZ | 80 |
| `chad` | s15Fixed16Array | 44 |
| 头 + tag 目录 + 对齐 | — | ~256 |

理论最小 488–510 B，实测 544 B。差额归因：`cprt`/`desc` 的标准文案开销（多语言 mluc 头部）+ tag 目录按 4 字节对齐——**不是 tag 缺失**。

**v2.0 兼容档：实测 432 B（9 tag）**

- `rTRC` 用 `curv` `count=1`，gamma 以 u8Fixed8 编码 = 461/256 ≈ 1.8008；
- 理论最小 388 B，实测 432 B。差额 = v2 `desc` 用 `desc` 类型（ASCII 67 字节 + UTF-16 备用字符串双字段）开销。

**v2 兼容档的精度代价（必须量化）**

lcms 256×256 渐变 ProPhoto→sRGB 实测：
- v2 单值 `curv` 档 vs v4.4 `para` 档：**最大单通道差 = 3/255，差异通道占比 2.79%**；
- 来源：gamma 量化（u8Fixed8 粒度 1/256 ≈ 0.004）+ v2 单值 `curv` 无线性趾（linear toe）；
- 落点：**仅暗部端点几个码阶**，中间调与亮部无差。
- **结论**：v2 兼容档定位是"老软件兜底"，精度代价在暗部不可见区域；**v4.4 主路径精度不变**。

**v2 触发规则**：u8Fixed8 粒度扫描最佳 gamma，逐点 max|差| ≤ 0.5/255 ⇒ 单值 `curv`；否则落 **256 点 LUT**（`curv count=256`）。

### 6.7 JPEG 默认版本争议（证据 vs 决定）

> **本节是"证据说 A、决定选 B"的如实记录，不是"我们也认同 v4.4"。**

**合规核查给出的证据**（指向"JPEG 默认应 v2"）：
- JPEG 消费端最老：Windows Photo Viewer 历史上对 v4 profile 解析不完整；Firefox 70（2019）之前仅正确解释 v2；
- 行业事实标准 sRGB IEC61966-2.1 profile 本体即 v2.1，Adobe RGB (1998) 发行 profile 也是 v2.1——JPEG 生态里"大家都用 v2"不是偶然；
- v2 `desc` 是 ASCII，老软件读 profile 名称不崩；v4 `mluc` 在极老解析器上可能显示乱码。

**用户拍板的决定**：JPEG 默认 v4.4，v2 作为用户手动可选的兼容档。

**这个决定的代价**：
- 遇到极老 JPEG 查看器时，v4.4 profile 可能被忽略回退 sRGB（与 v2 被正确解释相比，色域可能偏移）；
- 用户若发现"老软件颜色不对"，需手动切 v2 档。

**本记录的立场**：证据指向 v2 更稳，决定选 v4.4 是统一性优先。此选择由用户做出，本文如实并列两边，不背书、不修饰。

---

## 7. 来源清单（本轮新增）

访问日期均为 2026-11-14。上一份报告已列的来源不重复。

| # | 标题 | 机构 | URL |
|---|---|---|---|
| 1 | ICC.1 v4 Specification（含"ICC.1:2022 includes CICP tag"与"Amendments since ICC.1:2022"段） | ICC | <https://www.color.org/v4spec.xalter> |
| 2 | The Perceptual Reference Medium gamut (PRMG)（v4 perceptual intent 参考介质） | ICC | <https://www.color.org/v4_prmg/> |
| 3 | ICC Experts Conference: ICC Version 4 Model（mAB/mBA round-trip 改进） | ICC | <https://www.color.org/CMSystemsandICCV4.pdf> |
| 4 | ICC Rendering Intents 2002（v2 lut8/lut16 vs v4 mAB/mBA 历史） | ICC | <https://www.color.org/render.pdf> |
| 5 | Adaptive Gain Curve Tag (ADGC) | ICC HDR WG | <https://www.color.org/groups/hdr/ICC.1_Adaptive_Gain_Curve.pdf> |
| 6 | Headroom Adaptive Gain Curve Amendment | ICC | <https://media.color.org/icc1_amendment_headroom_adaptive_gain_curve.pdf> |
| 7 | Dolby 关于 ADGC 的专利声明 | ICC Patent Registry | <https://registry.color.org/patent-registry/files/2025-01-24-dolby.pdf> |
| 8 | ICC HDR Symposium 2025（HDR metadata：CICP/MDCV/CCV/CLL） | ICC | <https://www.color.org/events/hk2025/03-Bonnier-ICC_Color_Symposium.pdf> |
| 9 | Thoughts on HDR Colour Management Using iccMAX (Max Derhak) | ICC | <https://www.color.org/hdr/11-Max_Derhak.pdf> |
| 10 | HDR Imaging: Is there a role for ICC? (Luke Wallis 2019) | ICC | <https://www.color.org/groups/displays/20190215_High_Dynamic_Range_imaging_and_Hybrid_Log-Gamma.pdf> |
| 11 | ICC.2:2023 PDF（MPE/CLUTElement 结构） | ICC | <https://www.color.org/specification/ICC.2-2023.pdf> |
| 12 | Apple Developer Forums: LUT-based ICC profiles never supported on Apple | Apple | <https://developer.apple.com/forums/thread/807131> |
| 13 | ISO 15076-1:2025 OBP（"v4.3 fully backward compatible with 4.2"） | ISO | <https://www.iso.org/obp/ui/#!iso:std:86740:en> |

**仓内实测样本**（非外部 URL）：
- `tools/src/libjxl/third_party/skcms/profiles/mobile/{Display_P3_LUT,Display_P3_parametric,sRGB_LUT,sRGB_parametric}.icc`
- `tools/src/libjxl/third_party/skcms/profiles/misc/{Rec2020_PQ_cicp,Rec2020_HLG_cicp,sRGB_ICC_v4_beta}.icc`
- `tools/src/jpegli/third_party/lcms/plugins/test_profiles/test{0,1,2,3,5}.icc`
- `src/FfmpegGui/Services/ColorMapping/ColorIntentFactory.cs:27`（`ExitWritesIccFor` 现状）
- `tests/output/_tmp/icc-spec-survey/iccgen_bt709.png`（上一轮临时件，本轮复用）
- `tools/src/jpegli/third_party/lcms/src/cmssamp.c`（lcms2 版本判断 `cmsGetEncodedICCversion >= 0x4000000`，只按主版本走路径）
- `tools/src/jpegli/third_party/lcms/src/cmsvirt.c`（lcms2 内置 profile 统一 `cmsSetProfileVersion(hICC, 4.4)`）
