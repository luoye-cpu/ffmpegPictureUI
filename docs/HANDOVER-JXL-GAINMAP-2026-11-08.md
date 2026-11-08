# 交接：给 GainMap 加入 JPEG XL（HDR 增益图）支持 — 2026-11-08

> 本文件是**任务交接**，不是账本读数。用户 2026-11-08 要求"停止当前操作、写一份交接文档给其他 agent"。
> 阅读者：接手的 agent。你没有看过上文对话，本文件自解释。
> 标注约定：**[已验证]** = 我这次用 Read/Grep 亲自复核；**[Explore 报告]** = 来自已完成的子代理、我未逐行复核；**[记忆·时点]** = 11~15 天前的项目记忆，动手前须回代码核实；**[未跑到]** = 关键缺口，必须补。

---

## 0. 一句话现状

JXL 目前**只走原生 HDR（CICP + intensity_target），没有任何 gain-map 通路**；而项目自己的书面规范
（`docs/GAINMAP_ROUTES_AND_PATCH_PLAN.md` **P2-1**）已经把"给 JXL 写增益图"**明确挂起**，条件是"先量生态、
有第二读者能读懂才开写侧"。决定这件事可不可行的那一路**一手 web 调研没跑成**（子代理失败），这是接手第一件要补的事。

---

## 1. 用户诉求（拆解）

用户原话："查询 JPEG XL 相关的情况，给 GainMap 相关加入 JPEG XL 内容，JPEG XL 中也允许额外使用 GainMap 进行 HDR 实现。"

拆成三层：
1. **调研** JPEG XL 对 gain-map HDR 的真实支持情况（现状盘点 + 一手依据）。
2. **给 GainMap 功能加入 JXL 出口**（现在 GainMap 只出 JPEG/AVIF）。
3. 用户的前提假设："JXL 里也允许额外用 gain map 做 HDR" —— **这个前提本身待证**（见 §4）。
   注意：JXL **原生就支持 HDR**（高位深 + PQ/HLG），gain-map 在 JXL 里是"兼容性回退"路线，不是唯一 HDR 手段。

---

## 2. 已确证的代码事实

### 2.1 GainMap 侧（现有写出器 + 能力表）

- **[已验证]** `src/FfmpegGui/Services/ColorMapping/ColorTransformPlan.cs`
  - `SupportsGainMap` 字段声明 `:89`；注释 `:85-88`：GainMap 是"输出结构"能力，非色调曲线；
    "⚠ 不能靠 UI 的 IsVisible 当门禁——CLI/预设能绕过 UI（实测踩过）"。
  - `CanHdrWith(bool gainMapEnabled) => CanHdr || (gainMapEnabled && SupportsGainMap)` `:51`。
  - 能力表里 `SupportsGainMap = true` 的**只有** `jpg` `:557` / `jpeg` `:559` / `jpegli` `:561` / `avif` `:603`。
    **`jxl` 条目不在此列** ⇒ `Caps["jxl"].SupportsGainMap` 取默认 `false`（当前硬拦）。
  - `avif` 的增益图承载 `:599-604` 注释：走 ISO-BMFF `tmap` 派生项（纯托管 `IsoBmffGainMapWriter` 组装），
    三路独立验证（自家探测器 / libavif / Google libultrahdr v2.0.2）。**这是 JXL 若要写增益图最接近的先例形状。**

- **[记忆·时点]** 现有 GainMap 出口：JPEG（MPF + hdrgm XMP + ISO 21496-1 APP2，三档 `iso|xmp|both`）、
  AVIF（`tmap` + ISO 21496-1）。HEIC **只有解码、无写通路**（策略层 exit 91 拒）。写出器主名 `GainMapEncoder.cs`
  （`EncodeAsync(..., sourceBaseRaw, inputIsSdrWhiteUnit)`）。`--hdr-mode gainmap` 是运行期档。
  动手前须回代码复核这些类/参数是否仍在原位（记忆 11~15 天前）。

### 2.2 JXL 侧（现状）— [Explore 报告]，来自已完成的子代理 a100，**逐行未复核**

- **[Explore 报告]** 单一 cjxl 参数构建器：`src/FfmpegGui/Services/CjxlService.cs` 的 `BuildCjxlArguments`（约 :391-604）。
  HDR 相关分支：ICC 分支发 `-x icc_pathname=…` + `--intensity_target`（ICC 优先、早返回）；否则发 `-x color_space=` + `--intensity_target=`。
  优先级：plan override（`colorSpaceOverride`/`intensityTargetOverride`）→ `DecodedUltraHdrColorSpace` + 真实 peak nits → 探测 `hdrMeta` → options 兜底。
- **[Explore 报告]** JXL HDR = **原生 CICP**，非 gain map。`ColorSpaceRegistry.cs` 映射：
  sRGB/709→`sRGB`、P3→`DisplayP3`、P3-PQ→`Rec2100PQ`、2020-PQ/HLG→`Rec2100PQ/HLG`；SDR BT.2020/DCI-P3/ProPhoto→`null`（只能带 ICC）。
  cjxl color_space 枚举白名单在 `ColorMapping/ColorTransformPlan.cs`（约 :526）：`sRGB/DisplayP3/Rec2100PQ/Rec2100HLG`。
- **[Explore 报告]** **零处 jxl 引用**在 `GainMapEncoder.cs` / `GainMapComposeService.cs` / `IsoBmffGainMapWriter.cs`；
  gain-map 出口在 `RawColorPipeline.cs`（约 :55-56 → `TransformGainMapAsync` :293，仅 JPEG）。⇒ **确证无 JXL 增益图通路**。
- **[Explore 报告]** 路由挂点（`QueueProcessor.cs`）：`.jxl` 输入旁路 `ProcessJxlInputAsync`；cjxl 后端 `ProcessCjxlAsync`；
  像素重映射改道 `PipeFfmpegToCjxlAsync`（PPM/PAM 管道，因为**输入自带 ICC/CICP 时 `-x` 会被 cjxl 静默忽略**——此点与
  [[libjxl-cjxl-ppm]] 记忆一致）。引擎侧出口 `RawColorPipeline.EncodeJxlViaCjxlAsync`（约 :446）。
- **[已验证 + Explore]** 随包工具链：`publish/PLAN/jxl/bin/` 下 cjxl.exe / djxl.exe / jxlinfo.exe / jxltran.exe；
  **[Explore 报告]** 执行验证版本 `cjxl v0.12.0`（libjxl 0.12.0，CHANGELOG `[0.12.0] - 2026-07-01`，vendored 源在 `tools/src/libjxl/`）。
  ⚠ 若真要用 libjxl 的 gain-map 能力，**必须先确认 0.12 这个版本到底带不带**（见 §4）。

---

## 3. 项目自己的书面口径（**最关键的约束，先读**）

`docs/GAINMAP_ROUTES_AND_PATCH_PLAN.md` —— **[已验证]** —— 早就登记了 JXL 增益图这件事：

- §2 承载矩阵 :57：`PNG / JXL` 行标 **"❌ 未支持（登记，不擅自写，见 P2-1）"**；备注"生态已有实现
  （MediaInfo 解析 PNG 的 ISO 元数据；**ACR 可给 JXL/PNG 嵌增益图，base 可为 SDR 或 HDR**）"。
  ⚠ 这里点名的 **ACR** = `dngtool -rdp`（Adobe DNG SDK，见 AGENTS §7"ACR 路线只覆盖 DNG"），是**潜在的 JXL 增益图写入手段**。
- §3 读侧生态矩阵 :65：**"Safari ... JXL 不支持增益图"**；:66 MediaInfo 25.07 解析 PNG/HEIF 的 ISO 21496-1，未提 JXL。
- **P2-1 :266-270（原文口径）**：
  > PNG / JXL 增益图：先量生态再决定。
  > 做法：先做**读侧**兼容（已有 `ParseIso21496`，PNG 侧只是找 `sRGB`/自定义块的位置），
  > 写侧等**能力表与第三方读者判据都齐了**再开。**不在没有第二读者认可前写出去。**

⇒ **本仓的既有纪律 = 找不到一个"不依赖我方日志的第二读者能读懂"，就不许落地 JXL 增益图写侧。**
这一条与用户记忆 [[absence-claims]]（降级/缺席断言要先证探针活着、先做正控、收口要有独立读者）同族。
接手时若真要落地，第一步不是写写出器，而是**先量"谁会读 JXL 增益图"**。

---

## 4. 未决 / 没跑到的（必须补的一手）

**决定可行性的那路 web 调研失败了**（子代理 a0e7 "The request could not be completed"，未产出结论）。
需要重新调研、拿到一手依据才能回答的问题（对应任务里"查询 JPEG XL 相关的情况"）：

1. **libjxl / cjxl 0.12 到底能不能写 gain map？** 稳定版 cjxl/djxl CLI 有没有相关开关？
   若 0.12 不带，是哪个更高版本才进的（搜 libjxl releases/CHANGELOG/PRs 关键词 `gain map` / `gainmap` / `ISO 21496` / `tone map`）？
   —— 本仓锁定 0.12（AGENTS §7"随包 10-11 件不动"2026-10-30 裁决），若需升级工具链属独立决策、要另申请。
2. **JXL 里增益图怎么存**：是像 AVIF 那样 ISO-BMFF `tmap` 派生项 + ISO 21496-1？还是 JXL 特有的 box / extra channel / `jxltran`/ACR 手段？
3. **有没有真实的第二读者**（浏览器 / 图像编辑器 / OS / MediaInfo / libultrahdr）能读 JXL 增益图？
   只有我方自产自读 = 按 P2-1 不许开写侧。
4. **原生 HDR vs 增益图 HDR 的定位**：JXL 已能原生 PQ/HLG + intensity_target；引入 gain map 是为兼容只认 SDR 的读者，
   这条价值是否成立、与现有原生路线如何并存不打架，要向用户讲清并拍板。

（可复用记忆 [[libjxl-cjxl-ppm]]、[[cjxl-hdr-iccgen-sdr-hdr-cicp-sdr-2020-icc]]：cjxl 色彩表达约束——
iccgen 不支持 HDR trc、HDR 必须走 CICP 枚举、`-x` 在输入带 ICC/CICP 时被忽略须走 PPM 管道等，这些坑对 gain-map 路同样适用。）

---

## 5. 若确认可行，扩展点清单（改动落点）

按现有形状，加一个"JXL 增益图出口"至少要同步动这几处（**动手前先补 §4，再按 S3 派 Plan 出方案**）：

- **能力表**：`ColorTransformPlan.cs` 的 `Caps["jxl"].SupportsGainMap = true`（+ `VerifiedBy` 要有真实读数出处，P2-1 要求"能力表齐"）。
- **写出器**：新写一个 JXL 增益图写出（类比 `IsoBmffGainMapWriter`），或经 ACR/`jxltran`/更高版 libjxl——取决于 §4 结论。
- **管线分叉**：`RawColorPipeline.cs` :55-56 的 `plan.GainMap` 出口目前仅 JPEG，需扩到 jxl；`BuildCjxlArguments`/`EncodeJxlViaCjxlAsync` 接线。
- **路由**：`QueueProcessor.cs` 对 jxl 后端的增益图放行分支（勿与现有原生 HDR-CICP 路撞车）。
- **CLI + UI + locales**：`CliParser.cs`（如把 `--hdr-mode gainmap` / `--jpeg-gain-map-metadata` 的旋钮扩到 jxl）、UI 面板下拉、中英文案；
  ⚠ UI 的 IsVisible **不能当门禁**（ColorTransformPlan :87 已点名，CLI/预设会绕过）。
- **门禁登记（AGENTS §2 四处同批）**：新增/改动判据要进 `_run-step3-gates.ps1` 的 `$scriptList` + `$expectedScriptCount` + runner 散文陈述 + `TestGroups.cs` 组表；
  新 ServiceProbe mode 要 `_probe-test-group-coverage.ps1` 认领。承重探针必须 tracked 入库。
  判据必须带**第二读者 + 对照臂真分叉 + 负控**（P2-1 精神 + [[judge-design]]）。

---

## 6. 给接手 agent 的建议下一步

1. **先重跑 §4 的 web 一手调研**（libjxl gain-map 支持版本 + cjxl 0.12 能力 + 第二读者）。这是可行性总闸，没它一切白搭。
2. 拿结论回来先判：**当前 0.12 工具链 + 现有生态**能不能写出"别人读得懂"的 JXL 增益图。
   - 若"能" → 按 [[design-stance]] 与 P2-1，先补读侧兼容，再提写侧方案，向用户摊开候选（原生 HDR vs gain-map vs 两者）。
   - 若"否/需升级 libjxl" → 如实报告，升级工具链属独立决策、须单独申请（AGENTS §7 随包件不动裁决）。
3. 方案定前按 S3 派 Plan，不空想。
4. 严格遵守全局 §W 工作区边界：一切写盘落 `C:\PLAN\ffmpegPictureUI\` 内，外部素材落 `tests/output/_tmp/<批次>/corpus/`。

---

## 7. 环境 / 纪律提醒（本仓特有）

- 验收配方（AGENTS §4）：`pwsh7`、清 `LC_ALL`、`dotnet build ffmpegPictureUI.sln -c Release` → 门禁 `_run-step3-gates.ps1`（**95 条**，2026-11-04 从 94 增至 95）。
- 按模块只跑被改动那一块（全局 §T1/T2），不默认跑全量；起轮后不动 `src/` 与运行器。
- 子进程 TMP 须钉 `tests/output/_tmp/round-tmp`，否则 `verify-color-wiring` 类批量假红（AGENTS §4）。
- 相关既有账本可读：`docs/GAINMAP_ROUTES_AND_PATCH_PLAN.md`（本文 §3 的母文件）、`docs/AVIF_GAINMAP_ENCODER_2026-10-02.md`（最接近的先例）、
  `docs/IMPLEMENT_PLAN_2026-10-11.md`、归档 `docs/archive/JXL_ISSUES_HANDOVER.md` / `JXL_RAW_16BIT_ADVISORY_2026-10-02.md`。

---

## 8. 本次会话已做 / 未做（诚实交代）

- 已做：读项目记忆（GainMap/JXL 6 条）→ 建 3 个任务 → 派 3 个子代理并行调研 → **1 个（JXL 代码地图）完成**、**2 个（web 可行性、GainMap 代码地图）失败** →
  我亲自 Read/Grep 复核了能力表 `SupportsGainMap` 现状与 `GAINMAP_ROUTES_AND_PATCH_PLAN.md` 的 P2-1/§2/§3 书面口径 → 写成本交接文档。
- 未做（用户已叫停，未继续）：web 一手调研未补成、未定方案、**未改任何产品代码、未跑任何构建/门禁**。
- 本文件**未**去动承重文档 `docs/HANDOVER.md`；若需要进 `docs/INDEX.md` 目录图，由接手者按 §6 口径判断（本文件属"时间点交接"）。
