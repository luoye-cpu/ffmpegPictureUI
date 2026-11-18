# 色彩管线完整修复方案（LUT ICC 扩展 + 前序未完成项收口，ICC.1:2022 / Profile v4.4.0.0）

> 文件名沿用 `COLOR_PIPELINE_LUT_ICC_IMPLEMENTATION_PLAN_2026-11-17.md`（历史链接不改）；内容已扩为完整修复方案。

- 文档状态：**规划稿（未落地）**。本文件只定义实现方案、代码接缝、验收与门禁，不改产品源码、不提交、不出包、不动 `1.6.0BETA17` tag。
- 上游输入：`docs/review-2026-11-17/COLOR_PIPELINE_INDEPENDENT_AUDIT_2026-11-17.md`（5 HIGH + 1 MEDIUM）。
- 被否决的旧方案：`docs/review-2026-11-17/COLOR_PIPELINE_FIX_PLAN_2026-11-17.md`（745 行）。其 **引擎外挂独立 CMS 慢路**（`IccLutTransform` / 外部 CMS 进程 / 独立库）已被用户明确否决；本文件保留其 F-01～F-06 映射、WP 分层、门禁与 oracle 方向，但架构改为 **矩阵引擎同一 H1 内核内新增 canonical LUT 分支**。
- 覆盖范围：本文件是**完整修复方案**：§0–§14 是 LUT ICC 主体；§15 把 2026-11-16 修复批与 2026-11-17 独立审查的未完成项一并纳入；§11 的执行波次同时覆盖两者。
- 已拍板：D1–D5 见 §0.1；D6 未单独回答，按建议默认保留源语言。
- 规范依据：ICC.1:2022-05（Profile version 4.4.0.0），<https://archive.color.org/specification/ICC.1-2022-05.pdf>。本文件中的条款号与字节布局均按该原文核对；V-09 的三条规范事实已于 2026-11-17 复核（附录 F），无“未取到”项。

---

## 0. 一句话结论

**能做，而且不需要第二套 CMS。** 把源 LUT ICC 统一成两层 canonical 表达：

1. **执行视图**：从 `mft1` / `mft2` / `mAB` 解析出“有序阶段链”——各类型顺序不同：`mft` 是 `matrix → 输入表 → CLUT → 输出表`，`mAB` 是 `A 曲线 → CLUT → M 曲线 → matrix → B 曲线`。固定选择 **A2B1（media-relative colorimetric）**，输出 PCS（XYZ 或 Lab）→ 经 `inv(dst.RgbToXyzD50)` 映射到本产品标准 matrix/TRC 目标空间。
2. **携带视图**：保留 LUT 表/CLUT 的原始字节，**只重写 ICC 外壳**到合法 v4.4.0.0（header、tag table、`desc`/`cprt` 转 `mluc`、`wtpt`/`chad` 校验、profile ID 重算、4 字节对齐/连续/共享 offset）。不做 LUT 重采样，因此 LUT 数据无损。

执行体仍是 `RawColorPipeline.TransformFileAsync` 内的 `ColorKernels` 逐像素内核；`ColorBackend.Lut` 只作**路由标签**，不新开进程、不引入 lcms2/transicc、不写第二套管线。

GUI 侧新增“ICC 优先（双写）”勾选（WP-14），不是 CLI-only；在支持 ICC+CICP 的载体上勾选即双写，不支持 CICP 的载体退化为 ICC-only 并在 ToolTip/日志明示。

两个真实 LUT profile 已逐字节证明可统一到同一模型：

| Profile | 大小 | 类/空间/PCS | 版本 | A2B1 实际结构 | 结论 |
|---|---:|---|---|---|---|
| `sRGB_ICC_v4_Appearance.icc` | 63,868 B | `mntr` / `RGB` / `Lab ` | v4.3.0.0 | `mAB` 436 B：2×2×2 CLUT + 非恒等 3×4 matrix + `para` 曲线 | 可执行 canonical A2B1 |
| `MartiMaria_browsertest_A2B.icc` | 30,036 B | `scnr` / `RGB` / `XYZ ` | v2.1.0.0 | `mft2` 29,554 B：17³ CLUT + 恒等 matrix + 2 项输入/输出表；A2B0/A2B1/A2B2 共享同一 offset+size | 可执行 canonical A2B1（共享数据合法） |

### 0.1 已拍板决策（2026-11-17）

| # | 问题 | 决策 | 对方案的影响 |
|---|---|---|---|
| D1 | “ICC 优先（双写）”的 UI 落点 | **以 GUI 为准**：色彩面板新增“ICC 优先（双写）”勾选，必须真实可用（可见、可启用、可持久化、可回读）；不做 CLI-only。在支持 CICP+ICC 的载体上，选“ICC 优先”也必须 ICC+CICP 双写，不能退化成只写 ICC | WP-07 只保留语义；新增 WP-14 做控件、预设、CLI、locale、UI 门禁；`FfmpegOptions`/`PresetData`/`AppSettings` 增加持久位 |
| D2 | A2B0 回退开关 | **不给** | 不新增 `--icc-lut-allow-perceptual`；仅 A2B0 只能 Carry，显式 Map 一律 `IccLutPerceptualOnly` |
| D3 | LUT + GainMap | **拒绝** | `IccLutGainMapUnsupported`；GainMap 底图不走 LUT；不做“仅携带” |
| D4 | `IccPreferred` 与 `BakeCicpOnly` 同时命中 | **`BakeCicpOnly` 赢** | 日志点名冲突；GUI 在 `BakeCicpOnly` 下禁用/灰化勾选并给原因 |
| D5 | canonical header intent 保留原值 | **不允许** | 按实际执行的 A2B tag 写 intent：A2B1→1、A2B0→0、A2B2→2、A2B3→3；无 A2B→0；原文差异记 `IccIntentNormalized` |
| D6 | `desc`/`cprt` 本地化记录 | 未单独回答；按建议**保留源语言** | `IccV44Rewriter` 保留源 `mluc` 语言记录；若后续要求统一 `en/US` 再改 |

> D6 是唯一未明确回答项。本方案按“保留源语言”执行，并在 `IccV44Rewriter` 的 `LocalizationSource` 记账；不因此阻塞开工。

---

## 1. 范围、成功定义与不做

### 1.1 本方案要解决的问题

| Finding | 级别 | 现象（审查报告已闭环） | 本方案对应 |
|---|---|---|---|
| F-01 | HIGH | 两个真实 LUT ICC 被识别为“无 ICC”；携带策略 PNG/JXL/AVIF/WebP 全部 exit 91、零产物；推荐策略把 LUT 源降成 sRGB | 身份层 + canonical LUT + 携带/映射分流 |
| F-02 | HIGH | 显式 `--icc-file` 缺失/合法 LUT 外部 ICC 被静默忽略；三组 P3 PNG 1393 B 同 SHA256 | 显式 ICC fail-closed + 合法 LUT 正控 |
| F-03 | HIGH | 同一源有合法 LUT ICC + P3 CICP 时 P3 CICP 胜出、LUT ICC 被丢弃且无警告 | ICC > CICP（LUT 路径）+ 冲突抑制与告警 |
| F-04 | HIGH | H1 Map 分支不消费 `--color-matrix`；同源两矩阵产物同 SHA256 | `InputMatrixDirective` 单一真值，legacy/H1 共用 |
| F-05 | HIGH | RAW 自产线性中间件 provenance 失效；临时 TIFF 不满足 `IsRawFile` ⇒ 退回 sRGB | `ColorSourceProvenance` 运行时真值 |
| F-06 | MEDIUM | exiftool `WorkingDirectory` 在工具目录但参数是相对路径；`ServiceProbe wire=100/1` | 公共路径绝对化 + 全入口审计 |

### 1.2 成功定义（可验收）

| # | 成功条件 | 判据 |
|---|---|---|
| S1 | 两个真实 LUT profile 被识别为 `Valid` + `LutA2BRelative`，不再返回 null | `ServiceProbe icc-profile` 对两文件逐字段输出 |
| S2 | 两者都能以 A2B1 映射到 sRGB / Display P3，像素与独立 oracle 在容差内 | `verify-color-icc-lut.ps1` |
| S3 | 携带时产出合法 v4.4.0.0 外壳，LUT tag 数据逐字节不变 | `verify-color-icc-rewrite.ps1`：LUT payload SHA256 相等、tag table 合规、profile ID 可复算 |
| S4 | 合法 LUT ICC 可携带；不能映射时明确拒绝或仅携带，绝不静默 sRGB | 负控 + 日志原因码 |
| S5 | 显式 `--icc-file` 缺失/不可读/非法 ⇒ exit 91、零产物、`ExplicitIcc*` | `verify-color-wiring.ps1` 改写断言 |
| S6 | ICC（含 LUT）与 CICP 冲突时 ICC 优先、冲突 CICP 抑制并告警 | `ServiceProbe wire` + 日志断言 |
| S7 | F-04 / F-05 / F-06 全部闭环 | 三条门禁 |
| S8 | 门禁登记四处一致（清单/常量/散文/组表） | `_run-step3-gates.ps1` 形态自检 + `verify-probe-mirror-parity` |
| S9 | GUI“ICC 优先（双写）”真实可用：可见、可启用、可持久化、可回读，且与 CLI 对等 | `verify-ui-icc-annotation.ps1` + 真实桌面复核（V-01） |
| S10 | 前序未完成项按 §15 逐项处置，未验证不写成 PASS | §15 台账 + 发布说明 |

### 1.3 明确不做（v1 边界）

- 不执行 `B2A` / `mBA`（只解析、保留、携带；不用于源→PCS）。
- 不执行 `mpet`（只识别；按 ICC 8.10 把“CMM 不支持该处理元素”的 D2B/B2D tag 视为不支持，回退 A2B1）。
- 不执行 `D2B` / `B2D`（v1 一律按 8.10 视为不支持）。
- 不支持非 RGB / n-channel 映射（可识别、可携带，不可映射）。
- 不支持 `lut8Type`（`mft1`）+ PCSXYZ（规范未定义 8-bit PCSXYZ；拒绝映射）。
- 不支持 HDR LUT（PQ/HLG 设备空间、float32 PCS、ICC.2/iccMAX）。
- 不引入 lcms2 / transicc / 外部 CMS 进程；不 import Python（oracle 只在测试侧独立进程）。
- 不写 ICC.2 / iccMAX；不写 DeviceLink。
- 不改版本位、不转 beta18、不移动 tag。

### 1.4 本方案的两类工作

| 类 | 内容 | 章节 | 发布阻塞 |
|---|---|---|---|
| A. 色彩管线主体 | F-01～F-06 + LUT ICC 扩展 + GUI 双写 + AVIF/JXL 出口 | §2–§14 | 是 |
| B. 前序未完成项 | 2026-11-16 修复批的未验证/LOW/发布纪律；2026-11-17 独立审查的未覆盖；LUT 规范已复核（附录 F）；GainMap UI 并行线接口 | §15 | 分级：V-01/V-02/V-03/V-05/V-06/V-11 阻塞重出；其余按 §15.2 标注 |

`§15` 的 V 工作包不重复 F-01～F-06 的修复内容（那些由 WP-00～WP-12 覆盖），只收口“修复之外还没做完的部分”。

> **合规口径（关键）**：“遵守 ICC v4.4 全系规范”不等于“执行所有可选构造”。本方案的定义是：**凡是接受的构造，按 v4.4 解析/校验/执行；凡是不执行的构造，按 v4.4 的 8.10 回退或明确拒绝，并记录原因；凡是我们写出的 profile，必须是合法 v4.4。** 这是 CMM 允许的行为，也是诚实的行为。

---

## 2. 架构：为什么是“内核分支”而不是“外挂 CMS”

### 2.1 旧方案被否决的根因

旧方案把 LUT 当成“另一条慢路”：独立 `IccLutTransform`、独立执行器、独立缓存。后果是：

- 两条像素路径（matrix/TRC 与 LUT）各写一套输入解码、裁剪、抖动、8-bit/16-bit 出口，**必然分叉**；
- 携带/映射/直通三条策略在两条路径上各判一次，`IccCarryPolicy` 的单一真值被破坏；
- 需要第二套进程/库生命周期、第二套临时文件清理、第二套日志与失败码；
- 用户明确否决。

### 2.2 本方案的单一执行体

```
源文件
  → FfmpegCommandBuilder.ProbeInputColorMetadata（一次合并探测）
  → IccProfileAnalyzer（身份/结构/事实，不执行）
  → ColorIntentFactory（描述符 + 意图）
  → ColorMappingEngine.PlanWithPolicy
       ├─ 无 LUT：现有 PlanCore（matrix/TRC 快路）
       └─ 有 LUT：IccLutPlanRouter（Action=Map / CarryIcc / Reject）
  → QueueProcessor.TryRunColorEngineAsync
       └─ RawColorPipeline.TransformFileAsync（同一 H1 进程）
            └─ ColorKernels：LUT 分支逐像素（48→48 / 48→8 / RGBA8 / 流式共用）
```

### 2.3 两条“canonical”的含义

| 视图 | 用途 | 是否改变 LUT 字节 |
|---|---|---|
| **执行视图** `CanonicalLutTransform` | 内核逐像素求值；固定 A2B1；PCS→XYZ D50→目标线性 | 只读，不写 |
| **携带视图** canonical v4.4 ICC | 输出产物里附着的源 ICC；供查看器/后续 CMM 使用 | 保留 LUT tag 原始字节，只重写外壳 |

> 这是对“统一转为相对标准的 LUT ICC”的采用口径：**相对标准 = 固定 A2B1 的 media-relative colorimetric 解释 + 目标标准空间**；不是把 A2B0 的 LUT 数据搬进 A2B1 槽（那是语义撒谎），也不是把 LUT 重采样成矩阵。

---

## 3. 事实基线

### 3.1 两个真实 profile 的逐字节结构（本轮 `probe_icc_struct3.py` 核对）

**A. `sRGB_ICC_v4_Appearance.icc`**

- 63,868 B；SHA256 `eb7536f90e734be7fd7a8868967f85391858b290d5c1f80fb48130a187aade5b`
- `mntr` / `RGB` / `Lab `；v4.3.0.0（`04300000`）；header intent=0；profile ID `df1132a1746e97b07ad85719be711e08`
- 9 tags：`desc` mluc 80 B、`cprt` mluc 114 B、`wtpt` XYZ 20 B、`chad` sf32 44 B、`A2B0` `mAB` 29,724 B、`A2B1` `mAB` 436 B、`B2A0` `mBA` 32,760 B、`B2A1` `mBA` 436 B、`rig0` sig 12 B
- tag 数据连续、4 字节对齐、无部分重叠、无空洞；last tag end = 63,868

**A2B1（`mAB`，436 B）**：

| 段 | offset | 大小 | 内容 |
|---|---:|---:|---|
| header | 0 | 32 | input=3, output=3；5 个 offset：B@148、matrix@32、M@196、CLUT@80、A@316 |
| matrix | 32 | 48 | 12 × s15Fixed16，3×3 非恒等 + 3 offsets |
| CLUT | 80 | 20 + 48 | grid 2×2×2；precision=2；数据 2×2×2×3×2 = 48 B |
| B curves | 148 | 48 | 3 × `para` type 0（16 B/条，identity） |
| M curves | 196 | 120 | 3 × `para` type 4（40 B/条） |
| A curves | 316 | 120 | 3 × `para` type 4（40 B/条） |

> 物理布局是 **header(B/matrix/M/CLUT/A offsets) → matrix → CLUT → B → M → A**，不是“A→CLUT→M→matrix→B”的顺序扫描。处理顺序才是 A→CLUT→M→matrix→B。
>
> A2B1 的 matrix 是 **3×3 非恒等 + 3 offsets**；ICC.1:2022 §10.12.5 的 `mAB` matrix 是 3×4 仿射变换，**规范未要求恒等**（与 `mft1`/`mft2` 的 identity 约束不同），因此这是合法正控，不是 malformed。

**A2B0**：CLUT @80 为 17×17×17、precision=2（29,478 B 数据）；B @29,580（3×`para`0 identity 48 B）；M @29,628（48 B）；A @29,676（48 B）；总 29,724 B。

**B. `MartiMaria_browsertest_A2B.icc`**

- 30,036 B；SHA256 `4e6508181299faffe3373bb1d6e587f6131d300e8902960dc0949a41902a871a`
- `scnr` / `RGB` / `XYZ `；v2.1.0.0（`02100000`）；header intent=0；profile ID 全 0
- 6 tags：`desc`（v2 `desc` 类型）212 B、`cprt`（`text`）41 B、`wtpt` XYZ 20 B、`A2B0`/`A2B1`/`A2B2` **均指向同一** `mft2`（offset 480、size 29,554）
- 共享 offset+size 合法（7.3 允许多个 tag 指向完全相同的 offset+size）；“部分重叠”才是违规。朴素校验器把共享判成 overlap 是误报，必须按“完全相同 offset+size”放行。

**A2B1（`mft2`，29,554 B）**：

| 字段 | 值 |
|---|---|
| 0–3 | `mft2` |
| 8 / 9 | input=3 / output=3 |
| 10 | grid=17（单值，各维相同） |
| 11 | padding=0 |
| 12–47 | 3×3 matrix（identity） |
| 48–49 | input entries=2 |
| 50–51 | output entries=2 |
| 52–63 | input tables 3×2×2 = 12 B（三条 0→1 线性斜坡） |
| 64–29,541 | CLUT 17³×3×2 = 29,478 B |
| 29,542–29,553 | output tables 3×2×2 = 12 B |
| 合计 | 52 + 12 + 29,478 + 12 = 29,554 B |

> 该 profile 的变换几乎全部由 17³ CLUT 承载；matrix 恒等、输入/输出表只有 2 项。

### 3.2 代码接缝（行号会漂移，以函数名为锚）

| 文件 | 接缝 | 现状 | 本方案动作 |
|---|---|---|---|
| `ColorSpaceIdentify.cs` | `FromIccFile` | 只读 matrix/TRC，LUT 返回 null | 改走 `IccProfileAnalyzer`；LUT 返回带 `IccFacts` 的描述符 |
| 同上 | `IdentifyIcc` | `d==null` 时退 `GuessColorSpace(descriptionHint)` | LUT 用 `$"{desc} [LUT]"`，不猜空间 |
| `IccProfileService.cs` | `ExtractIccBytesToTemp` / `ExtractIccFromBase64` | `NormalizeSourceIccToV44` 返回 null 即 `TryDelete(tmp)`，LUT 原始字节在识别前被销毁 | 先保留原始字节到内容寻址路径，再做身份分析；返回 typed result |
| 同上 | `NormalizeSourceIccToV44` | v4.4 快返回条件含 `HasProfileId` 与 `IsCleanV44Tags`；`space==null` 返回 null | LUT 走 shell 重写；`HasProfileId` 允许 0；`chad` 按规范条件化 |
| 同上 | `ExtractIccToTempFile` | 旧两步法 | 保留供非合并路径；统一走 analyzer |
| `IccProfileBuilder.cs` | `Build` / `BuildCore` | 只构建 matrix/TRC；`StampProfileId` 只清零 84–99 | 新增 shell 重写器；profile ID 按 v4.4 清零 flags(44–47)+intent(64–67)+ID(84–99) |
| 同上 | `IsCleanV44Tags` | 要求 `chad` 恒存在 | 改为 `desc/cprt` mluc +（`wtpt` 非 D50 时才要求 `chad`） |
| `ColorSpaceDescriptor.cs` | `IsMappable` / `Clone` / `Equivalent` | 只认 9 元素矩阵 | 增加 `IccFacts`；`IsMappable` 加 LUT；`Equivalent` 对 LUT 必须不等价 |
| `ColorIntentFactory.cs` | `:83-105` 外部 ICC | `catch { /* 静默忽略 */ }`；合法 LUT `FromIccFile` 返回 null | fail-closed + LUT 描述符；显式 ICC 由队列前置闸门统一判定 |
| 同上 | `:117-136` 自产中间件 | `RawService.IsRawFile(inputPath)` | 改读 `ColorSourceProvenance` |
| 同上 | `:277-290` 目标补轴 | 用 `src.PrimariesToken`/`TransferToken` | LUT 源不得把名义 linear 带进目标 |
| 同上 | `:418-423` srcHdr | 直接读 `src.Curve.Kind` | 先判 `HasLut` |
| `IccCarryPolicy.cs` | `:44-57` | 只接受 `declared==CarryIcc` + bool | 改 `IccCarryCapability` |
| `ColorTransformPlan.cs` | `PlanCore` / `ToSpec` | `dst==null` 先返回；`!IsMappable` Reject；`CarryIcc/None` 返回恒等 sRGB | LUT 分支前置；`ToSpec` 拷贝 LUT |
| `ColorKernels.cs` | `TransformSpec.IsIdentity` | 无 LUT 字段 | 加 `Lut`/`PcsToDstMatrix`；`IsIdentity` 加 `Lut==null` |
| 同上 | `TransformRgb48leCore` / `TransformRgb48leToRgb8` / `TransformRgba8` / 流式 | 三条 8-bit 出口 | 共用同一 LUT 逐像素函数 |
| `RawColorPipeline.cs` | `:100` H1 `vf` | 只 `format=rgb48le` + scale | 显式矩阵时前置 `scale=in_color_matrix` |
| 同上 | `EncodeAvifViaAvifencAsync` | `plan.AttachIcc` → `IccPath`；无则 `IccPrimaries/IccTrc` 生成；`NormalizeSourceIccToV44` | 接受 canonical LUT；LUT carry 用 `--cicp 2/2/0` + `--icc` |
| `QueueProcessor.cs` | `TryRunColorEngineAsync` | `Action=None/CarryIcc` 直通返回 `(false,0,plan)` | LUT carry 后置嵌入；map 走 `TransformFileAsync` |
| 同上 | `:3049` `sourceHasIcc` | bool 形状 | 改 `IccCarryCapability` |
| `FfmpegCommandBuilder.cs` | `ProbeInputIccDescriptor` / `ProbeCache` | 返回 `iccDescriptor?.Clone()`；`TryDeleteIcc` 会删临时件 | 返回稳定 facts/路径；canonical 路径受保护 |
| 同上 | `:2005-2045` ICC 段 | `ExtractIccFromBase64` → `IdentifyIcc` → `TryDeleteIcc` | 分析结果进缓存；不得删 canonical |
| 同上 | `:2079+` CICP 优先 | LUT ICC 也会被 CICP 顶掉 | LUT 路径 ICC 优先；冲突告警 |
| `GainMapJpegCodec.cs` | `ResolveEncodeParams :195-327` | 自己探测 `iccNeeded`；LUT null 时走 sRGB 惯例 | 复用同一 analyzer；LUT+GainMap 明确拒绝（D3），不携带不映射 |
| `ExifToolService.cs` | 多个进程站点 | `WorkingDirectory` 工具目录、参数相对 | 全部 `QuoteAbsArg`；保留 PATH 机制 |

### 3.3 勘误（必须记录，避免复用错误结论）

1. **`mAB`/`mBA` 不是顺序存储**。旧探针 `probe_icc_struct.py`、`probe_icc_struct2.py` 把 `mAB` 的曲线集按顺序扫描，且 `mft2` 偏移算错，结果不可用。以 `tests/output/_tmp/icc-plan-20261117/probe_icc_struct3.py` 为准；落地时把它提升为 `tests/scripts/_probe-icc-structure.py`（入库、承重探针）。
2. **`mft1`/`mft2` 的处理顺序是 `matrix → 1D input tables → CLUT → 1D output tables`**，不是 `A→CLUT→M→matrix→B`。matrix 在字节 12–47；A2B 的输入是设备空间，所以 matrix 规范上应为恒等（`mft1`/`mft2` 的 matrix 仅在输入是 PCSXYZ 时非恒等）。
3. **`mAB` 处理顺序是 `A curves → CLUT → M curves → matrix → B curves`**；物理布局由 32 B 头里的 5 个 offset 指出，通常为 B、matrix、M、CLUT、A。**解析必须按 offset 取段，不能顺序扫描。**
4. **`mBA` 处理顺序与 A2B 相反**：`B' curves → matrix → M' curves → CLUT → A' curves`；物理头同样是 5 offset（12–31 依次 B/matrix/M/CLUT/A）。
5. **Lab PCS 有两套 16-bit 编码**：`mAB`/`mBA` 用现代 16-bit（`L=raw/65535*100`、`a/b=raw/257-128`）；`mft2` 的 Lab 用 legacy 16-bit（`L=raw*100/65280`、`a/b=raw/256-128`，仅 `lut16Type`/`namedColor2Type`）。不能一套公式走到底。
6. **`mft1` 的 8-bit PCSXYZ 未定义**；`lut8Type` + PCSXYZ 必须拒绝映射。
7. **`IccProfileBuilder.StampProfileId` 只清零 84–99**，未清零 flags（44–47）与 intent（64–67），算出的 profile ID 不符合 v4.4 的 MD5 口径；本方案必须修正。
8. **`IsCleanV44Tags` 要求 `chad` 恒存在**，与 8.2“仅 adopted white ≠ D50 时必需”不符；这会把一个合法、干净、无 `chad` 的 v4.4 LUT profile 逼进 matrix/TRC 重建路径，从而被丢弃。

---

## 4. ICC v4.4 合规边界（实现 / 识别但拒绝 / 不支持）

**原则：输出严格，输入宽容但记账。**

- **输出严格**：我们写出的 canonical profile 必须满足 v4.4 的结构与必需 tag 规则；不写“看起来像”的 profile。
- **输入宽容但记账**：对输入 profile 的非致命偏差（例如 A2B 的 `mft` matrix 非恒等、`wtpt` 偏离 D50）不直接拒绝，但必须写入 `Degradations`/日志，并在测试里锁住“我们确实看到了”。

### 4.1 三档表

| 构造 | 档 | 行为 |
|---|---|---|
| Header / tag table / 必需 tag / profile ID / PCS 编码 | **实现** | 解析、校验、重写 |
| `mft1`（`lut8Type`）+ PCSLAB | **实现** | 解析、执行（8-bit Lab） |
| `mft1` + PCSXYZ | **识别但拒绝映射** | 解析结构；`IccLutXyz8Bit`；可携带不可映射 |
| `mft2`（`lut16Type`）+ PCSXYZ / PCSLAB | **实现** | 解析、执行（XYZ u1Fixed15；Lab legacy 16-bit） |
| `mAB` A2B0/A2B1/A2B2/A2B3 | **实现**（只执行 A2B1） | 解析全部；执行所选 A2B1；其余保留/携带 |
| `mBA` B2A0..3 | **识别但不执行** | 解析结构；`LutB2AOnly` 时仅携带 |
| `curv` / `para` 0–4 | **实现** | 1D 曲线求值 |
| `mpet` + `cvst`/`matf`/`clut`/`bACS`/`eACS` | **识别但不执行** | 按 8.10 视为“CMM 不支持该处理元素”；有 A2B1 则回退 A2B1，无则 `IccLutMpeUnsupported` |
| `D2B` / `B2D` | **识别但不执行** | 同上；v1 一律视为不支持 |
| 非 RGB / n-channel（input/output ≠ 3） | **识别但拒绝映射** | `IccLutNonRgb`；可携带不可映射 |
| DeviceLink / namedColor / 非 `mntr`/`scnr`/`prtr` | **不支持** | `ValidButUnsupported`；拒绝（非 RGB 像素语义不成立） |
| ICC.2 / iccMAX / float32 PCS | **不支持** | 解析到版本/类型即拒绝 |
| `cicp` tag | **实现（保留/校验）** | 不因 LUT 而伪造；LUT profile 默认不新增 `cicp` |

### 4.2 关键条款清单（实现时逐条对照）

| 条款 | 内容 | 实现点 |
|---|---|---|
| 6.3.4 | PCSXYZ 16-bit u1Fixed15（`raw/32768`，0x8000=1.0，最大 1.999969482421875）；PCSLAB 16-bit `L=raw/65535*100`、`a/b=raw/257-128`（0x8080=0）；8-bit PCSLAB `L=raw/255*100`、`a/b=raw-128` | `IccPcsCodec` |
| 6.3.4（legacy） | legacy 16-bit PCSLAB 仅 `lut16Type`/`namedColor2Type`：`L=raw*100/65280`、`a/b=raw/256-128`；转现代编码 ×`65535/65280`，超范围按分量裁剪 | `IccPcsCodec.DecodeLab16Legacy` |
| 7.2 | header 128 B；profile size、CMM、version、class、space、PCS、date、`acsp`、platform、flags、manufacturer、model、attributes、intent、illuminant、creator、profile ID、reserved | `IccHeader` |
| 7.3 | tag table：条目顺序任意；必须连续、唯一、无空洞；重复签名禁止；允许多 tag 指向完全相同 offset+size（共享）；禁止部分重叠；offset 4 字节对齐；size 不含尾部 padding；profile size 覆盖最后一个 tag 的 pad | `IccTagTable` |
| 8.2 | 必需 tag：`desc`、`cprt`、`wtpt`（DeviceLink 除外）；`chad` 仅 adopted white ≠ PCS D50 时必需；v4.4 `desc`/`cprt` 必须 `mluc`；`cicp` 可选（12 B：primaries/transfer/matrix/full range，其余保留；仅 RGB/YCbCr/XYZ 且 Input/Display；RGB/XYZ 时 matrix 必须 0） | `IccV44Rewriter` |
| 8.10 | 优先级 a) D2B/B2D（CMM 不支持某处理元素则该 tag 视为不支持）；b) 对应 intent 的 B2A/A2B；c) A2B0/B2A0；d) matrix/TRC。`D2B1`/`B2D1`=media-relative；`D2B0`=perceptual；`D2B2`=saturation；`D2B3`=ICC-absolute | `IccIntentSelector` |
| 10.6 | `curveType`（`curv`）：0–3 `curv`、4–7 reserved、8–11 count；n=0 identity；n=1 u8Fixed8 gamma（`y=x^gamma`）；n>1 u16 表线性插值 | `IccCurve` |
| 10.18 | `parametricCurveType`（`para`）：8–9 function type；参数 s15Fixed16；type 0 (g,16 B)、1 (g,a,b,24 B)、2 (g,a,b,c,28 B)、3 (g,a,b,c,d,32 B)、4 (g,a,b,c,d,e,f,40 B)；输出裁剪 [0,1]；复数/未定义域拒绝该 tag 或按合法域校验 | `IccCurve` |
| 10.11 | `lut8Type`（`mft1`）：8/9 channels；12–47 matrix；48 起 256 项输入表；CLUT `g^in × out` 8-bit；输出表 256 项 | `IccLutParser.ParseMft1` |
| 10.10 | `lut16Type`（`mft2`）：8/9 channels；10 grid（单值）；12–47 matrix；48–49 input entries；50–51 output entries；52 起数据；entries 2–4096；CLUT `g^in × out` 16-bit | `IccLutParser.ParseMft2` |
| 10.12 | `lutAToBType`（`mAB`）：8 input u8、9 output u8；12–31 五个 offset（B、matrix、M、CLUT、A）；处理顺序 A→CLUT→M→matrix→B；A 曲线数=in、M/B=out；matrix 3×4 s15Fixed16，`Y=M·X+offset`，裁剪 [0,1]；**规范未要求 matrix 恒等**（§10.12.5）；CLUT 20 B 头（16 B grid + precision + padding），**grid 各维可不同**（§10.12.3/Table 46）；A/B/M 是 curve set（无 count，按通道数顺序、4 字节对齐、可共享 offset，每条是完整 `curv`/`para`） | `IccLutParser.ParseMab` |
| 10.13 | `lutBToAType`（`mBA`）：处理顺序 B'→matrix→M'→CLUT→A'；B/M 曲线数=in、A=out；matrix 3×4 s15Fixed16，按 §10.13.3 执行/裁剪；**matrix 仅在 output/M=3 时允许**；仅 PCS XYZ/Lab | `IccLutParser.ParseMba`（只解析） |
| 10.16 | `multiProcessElementsType`（`mpet`）：8–9 in u16、10–11 out u16、12–15 n u32、16 起 position table（8 B/项）、数据 16+8n；元素按 position table 顺序执行、元素间不裁剪；支持 `cvst`/`matf`/`clut`/`bACS`/`eACS`；未定义元素 ⇒ 该 tag 不得使用 | `IccLutParser.ParseMpet`（只识别） |
| 10.16.2 | `cvst`（10.16.2.2）/ `matf`（10.16.2.3）/ `clut`（10.16.2.4）（MPE 内） | `IccLutParser.ParseMpet`（只识别） |
| 10.3 | `cicpType` | `IccCicpTag` |
| 10.15 | `multiLocalizedUnicodeType`（`mluc`） | `IccV44Rewriter` |
| 7.2.18 | profile ID：允许为 0；非 0 按 RFC 1321 MD5，计算时 flags、intent、profile ID 三处临时置零；版本字段 `04 40 00 00` | `IccV44Rewriter.ComputeProfileId` |

> 注意：ICC.1:2022 的 Introduction 有一句把 `lutAToBType`/`lutBToAType` 说成 10.10/10.11，与目录和正文标题不一致；引用一律以目录/正文标题为准（`mAB`=10.12、`mBA`=10.13、`mft2`=10.10、`mft1`=10.11）。这是本轮实际踩过的坑，写进规划防止再次被带偏。

### 4.3 规范事实复核（2026-11-17，ICC.1:2022 原文）

以下三条已从 ICC.1:2022 原文取到明确句，不再作为“待复核”：

| # | 事实 | 原文结论 | 对实现的影响 |
|---|---|---|---|
| C1 | `mAB`/`mBA` 的 matrix 在 PCSLAB 下是否必须恒等 | **不要求恒等**。§10.12.5（`mAB`）定义 3×4 仿射矩阵 `Y=M·X+offset`，只要求输入/输出裁剪到 [0,1]；没有“identity unless PCSXYZ”句。该句只出现在 §10.10（`mft2`）与 §10.11（`mft1`）。§10.13.3（`mBA`）另有“matrix 仅在 output/M=3 时允许”的结构约束。 | 按 §10.12.5 执行非恒等 matrix；不要按 `mft2` 的 identity 规则拒绝。真实 `sRGB_ICC_v4_Appearance` A2B1 就是非恒等，直接作为正控。`mBA` 只解析，不执行。 |
| C2 | `mAB` CLUT grid 是否允许各维不同 | **允许**。§10.12.3：`variable number of grid points in each dimension`；Table 46：16 B，前 i 项有效，未用项 0。`mft2` 是单值 `g`（Table 40：`identical for each side`），`mft1` 同。 | `IccClut` 用 `int[] Grid`，按 i 维插值；不要假设各维相等。`mft2` 仍用单值 g 展开。合成 `2×3×4` grid 正控。 |
| C3 | `mft2` input/output entries 上限 | **2–4096（含）**。§10.10：`minimum of two and a maximum of 4 096`，输入/输出表同；`mft1` 固定 256（§10.11）。 | 解析/插值接受 2..4096；不要只认 256/17³；2 项与 4096 项各一条正控。 |

> 这三条是 V-09 的内容；原文已取到，报告可写“已确认（ICC.1:2022 §...）”。附录 F 保留引文与章节。

---

## 5. Canonical LUT 执行模型

### 5.1 数据模型（有序阶段，不是固定字段）

```csharp
enum IccLutStageKind { Matrix3x3, CurveSet, Clut, Matrix3x4 }

sealed class IccLutStage
{
    public IccLutStageKind Kind;
    public double[]? Matrix9;      // Matrix3x3: 9 项行主序
    public double[]? Matrix12;     // Matrix3x4: 12 项行主序（3×3 + 3 offsets）
    public IccCurveSet? Curves;    // 1..3 条（in 或 out 通道数）
    public IccClut? Clut;          // IccClut: int[] Grid（按输入通道数；mAB/mBA 各维可变，mft1/mft2 由单值 g 展开）、int Precision、byte[]/ushort[] Data
}

sealed class CanonicalLutTransform
{
    public int InputChannels;      // v1 = 3
    public int OutputChannels;     // v1 = 3
    public IccPcsEncoding Pcs;     // XyzU1Fixed15 / Lab16Modern / Lab16Legacy / Lab8
    public List<IccLutStage> Stages;   // 已按**处理顺序**排好
    public string SourceTagSignature;  // "A2B1"
    public int SourceTagOffset, SourceTagSize;
    public string SourceProfileSha256;
}
```

**为什么用有序阶段**：`mft1/mft2` 是 `matrix → input → CLUT → output`；`mAB` 是 `A → CLUT → M → matrix → B`；`mBA` 是 `B → matrix → M → CLUT → A`。用命名固定字段必然有人把 `mAB` 的 matrix 放到 CLUT 前面。阶段链让“解析器读出的顺序”就是“执行器跑的顺序”，单一真值。

### 5.2 各类型的阶段映射

| 类型 | 阶段链（按处理顺序） | 说明 |
|---|---|---|
| `mft1` / `mft2` | `Matrix3x3? → CurveSet(input) → Clut? → CurveSet(output)` | A2B 的 matrix 规范上应为恒等；非恒等照用+记账 |
| `mAB` 组合 `B only` | `CurveSet(B)` | A/M/CLUT/matrix 全缺省 |
| `mAB` `M + Matrix + B` | `CurveSet(M) → Matrix3x4 → CurveSet(B)` | 无 A/CLUT |
| `mAB` `A + CLUT + B` | `CurveSet(A) → Clut → CurveSet(B)` | 无 M/matrix |
| `mAB` `A + CLUT + M + Matrix + B` | `CurveSet(A) → Clut → CurveSet(M) → Matrix3x4 → CurveSet(B)` | 全链 |
| `mBA` | `CurveSet(B) → Matrix3x4? → CurveSet(M) → Clut? → CurveSet(A)` | **v1 只解析，不执行** |
| `mpet` | 元素序列（`cvst`/`matf`/`clut`/`bACS`/`eACS`） | **v1 只识别**；不是 canonical 执行模型 |

> `mft1`/`mft2` 的 matrix 在 device→PCS 时按 §10.10/§10.11 **应为恒等**；非恒等是源 profile 不合规，按“照用+记账”处理。`mAB` 的 matrix 按 §10.12.5 是**合法仿射**，不要求恒等；两者不能共用同一条 identity 校验。

### 5.3 PCS 解码

| 编码 | 解码 | 使用方 |
|---|---|---|
| `XyzU1Fixed15` | `v = raw / 32768.0`；X/Y/Z ∈ [0, 1.999969482421875] | `mft2` + PCSXYZ；`mAB`/`mBA` + PCSXYZ |
| `Lab16Modern` | `L = raw/65535*100`；`a = raw/257 - 128`；`b = raw/257 - 128` | `mAB`/`mBA` + PCSLAB |
| `Lab16Legacy` | 先 `raw' = raw * 65535/65280`，再按 modern 解码；超范围按分量裁剪 | `mft2` + PCSLAB（仅 `lut16Type`/`namedColor2Type`） |
| `Lab8` | `L = raw/255*100`；`a = raw - 128`；`b = raw - 128` | `mft1` + PCSLAB |

Lab → XYZ D50 用标准 CIELAB 反变换（D50 白点 `0.9642, 1.0000, 0.8249`）；XYZ 直接使用。**不要**把 Lab 值直接当 XYZ 送进目标矩阵。

### 5.4 曲线求值

- `curv`：n=0 identity；n=1 `y = x^gamma`（gamma = u8Fixed8/256）；n>1 在 0..1 的 n 点表上线性插值（段宽 `1/(n-1)`）。
- `para`：按 type 0–4 求值；输入/输出裁剪 [0,1]；`a` 为 0 或负数导致复数/除零时，按“该 tag 不合法”拒绝（`IccLutMalformed`），不做静默替代。
- 曲线集：按通道数顺序；允许共享 offset；每条曲线 4 字节对齐；没有 count 前缀。

### 5.5 CLUT 求值

- 3D CLUT：v1 用 **四面体插值（tetrahedral）**，不用三线性。理由：LittleCMS 对 3D CLUT 默认四面体；用同一插值才能把 oracle 容差收到 ≤1 LSB/8-bit；四面体对中性轴更稳。
- 1D 表：线性插值。
- 边界：`x∈[0,1]`，`x=1` 时取最后一格，不越界。
- 性能：每像素 3 次坐标缩放 + 8 个格点读取 + 四面体权重；目标 < 2× 现有矩阵路径（以 1920×1080 基准计时，非门禁硬阈值）。

### 5.6 目标映射

```text
PCS (XYZ D50) → PcsToDstMatrix（3×3，= inverse(dst.RgbToXyzD50)）→ 目标线性 RGB
→ ClampLinearBeforeEncode（SDR v1 硬裁到 [0,1]）
→ EncodeScale（按目标 nits）
→ dst.Curve.FromLinear
→ clamp [0,1] → 抖动（仅 8-bit 出口）→ 量化
```

- `PcsToDstMatrix` 必须是 `inverse(dst.RgbToXyzD50)`；`dst.RgbToXyzD50` 缺失 ⇒ Reject。
- SDR v1 不接 Tone / HLG / GMO。LUT 源 + HDR 目标、LUT + GainMap、LUT + GMO 一律拒绝（见 §8）。
- 输出 8-bit 时沿用现有抖动；输出 16-bit 时不抖动。
- **LUT 分支只替换“源编码 → 目标线性”前半段；后半段（EncodeScale→目标曲线→裁剪→抖动→量化）完全复用现有实现**。这是 48→48、48→8、RGBA8、流式四条出口不漂移的唯一办法。

### 5.7 意图选择与 8.10

| 情形 | 选择 | 结果 |
|---|---|---|
| `D2B1` 存在且 MPE 元素全部支持 | 8.10 a) 选 D2B1 | **v1 不执行 MPE** ⇒ 按“CMM 不支持该处理元素”视为 D2B1 不支持，回退 A2B1 |
| `D2B1` 存在但含未定义元素 | 8.10 a) 视为不支持 | 回退 A2B1（不是 malformed） |
| `A2B1` 存在 | 8.10 b) media-relative | **执行 A2B1**（canonical 固定） |
| `A2B1` 缺失、`A2B0` 存在 | 8.10 c) perceptual | 默认**拒绝映射**（`IccLutPerceptualOnly`），可携带；不做静默回退 |
| `A2B1` 缺失、仅 `A2B2`/`A2B3` | 非 media-relative | 拒绝映射（`IccLutPerceptualOnly` / `IccLutNoA2B1`） |
| 仅 `B2A` | 8.10 b) 反向 | `LutB2AOnly`；可携带、不可映射 |
| 仅 matrix/TRC | 8.10 d) | 现有快路；不走 LUT |
| 仅 `mpet` | 8.10 a) 不支持 | `IccLutMpeUnsupported`；可携带、不可映射 |

> **header 的 rendering intent 只是默认提示，不是 tag 类型。** 不能因为 header intent=0 就去执行 A2B0；也不能因为 header intent=1 就假定 A2B1 存在。选择只看 tag 是否存在 + 8.10。

### 5.8 v1 的限制（写进代码常量与日志）

- 只执行 `A2B1`；只执行 3→3；只执行 SDR；只执行 `mft1`(Lab)、`mft2`(XYZ/Lab)、`mAB`(A2B1)。
- `mBA`、`mpet`、`D2B`/`B2D`、非 RGB、`mft1`+XYZ、ICC.2：只识别/拒绝，不执行。
- 不实现黑点补偿（BPC）；不实现 perceptual/saturation/ICC-absolute 的执行。
- 不做 LUT 重采样、不做 LUT→矩阵拟合。

---

## 6. Canonical ICC v4.4 外壳重写

### 6.1 原则

**保留 LUT payload 字节，只重写外壳。** 具体：

- 所有 tag 数据块默认**逐字节复制**；只有 `desc`/`cprt` 在需要时转 `mluc`，`chad` 在需要时补。
- 不解析、不重编码 LUT tag 数据；不改变 CLUT grid、precision、曲线参数、matrix 值。
- 重写会改变 tag 数据的**偏移**（因为 `desc`/`cprt` 长度可能变化、tag table 重排），但**不改变数据内容**。
- `mft2` 29,554 B 不是 4 的倍数（29,554 mod 4 = 2），重写时必须补 2 字节 pad，且 `size` 仍写 29,554（size 不含尾部 padding）。

### 6.2 Header

| 字节 | 字段 | 动作 |
|---:|---|---|
| 0–3 | profile size | 重算，覆盖到最后一个 tag 的 pad |
| 4–7 | preferred CMM | 保留 |
| 8–11 | version | `04 40 00 00` |
| 12–15 | device class | 保留（`mntr`/`scnr`/`prtr`） |
| 16–19 | data colour space | 保留（v1 要求 `RGB `） |
| 20–23 | PCS | 保留（`XYZ ` / `Lab `） |
| 24–35 | date/time | **保留源值**（canonical 需要确定性/可复现；不改写成构建时间）；源为 0 时写固定基准值 |
| 36–39 | `acsp` | 保留/校验 |
| 40–43 | platform | 保留 |
| 44–47 | flags | 保留（只在算 profile ID 时临时清零） |
| 48–51 | manufacturer | 保留 |
| 52–55 | model | 保留 |
| 56–63 | attributes | 保留 |
| 64–67 | rendering intent | **按实际可用的 A2B tag 写**：A2B1→1、A2B0→0、A2B2→2、A2B3→3；无 A2B→0；原文差异记 `IccIntentNormalized`（D5） |
| 68–79 | illuminant XYZ | 保留/校验 D50 |
| 80–83 | creator | 保留 |
| 84–99 | profile ID | 重算（v4.4 MD5 口径）；失败可写 0 |
| 100–127 | reserved | 0 |

> D5：**不允许保留原 intent**。header intent 必须与 canonical 实际可用的 A2B tag 一致：A2B1→1、A2B0→0、A2B2→2、A2B3→3；无 A2B→0。原文不同则记 `IccIntentNormalized`，不静默保留。header intent 与“我们实际执行的 intent”不一致就是撒谎：没有 A2B1 却写 1，或只看 header 的读者会被误导。

### 6.3 Tag table（7.3）

算法：

1. 读源 tag table（offset 0 的 4 B count，随后 12 B/项：signature、offset、size）。
2. 校验：count 合理、每项 offset+size 在 profile 内、offset 4 字节对齐、无重复签名。
3. 共享判定：**完全相同 (offset,size) 的多个 tag 视为共享**，保留共享；只允许“完全相同”，任何部分重叠 ⇒ `Malformed`。
4. 分配新 offset：从 128 + 4 + 12*count 之后开始，按 4 字节对齐依次放；顺序按签名升序（合法且可复现；v4.4 不要求顺序）。
5. `desc`/`cprt`：重编码为 mluc（若已是 mluc则复制）；`chad`：按 §6.5 决定是否补；其余 tag：整块复制。
6. 写 count + 条目；每个 tag 的 offset 指向新位置；size 保持原 size（不含 pad）。
7. profile size = 最后一个 tag 的 `offset + size` 向上取 4 字节对齐。
8. 重算 profile ID。

**不允许**：把共享的 `mft2` 复制成 3 份（会把 30 KB 变 90 KB）；把两个不同签名的 tag 指到同一 offset 除非它们的 payload 字节完全相同。

### 6.4 `desc` / `cprt` → `mluc`

- v4.4 要求 `desc`、`cprt` 为 `mluc`。
- 源是 v2 `desc`（`descType`）/`text`（`textType`）时，解析出字符串，重编码为 mluc：
  - `mluc` header：0–3 `mluc`、4–7 reserved、8–11 record count、12–15 record size（12）。
  - 每条 record：16–17 language（u16）、18–19 country（u16）、20–23 length（u32）、24–27 offset（u32，从 tag 起点）。
  - 字符串 UTF-16BE；长度 = 字符数×2。
- 语言/国家：优先保留源（v2 `desc` 有 language code），否则 `en`/`US`。
- `desc` 的 ASCII 描述长度上限 79（v2 语义）；mluc 无此限制，但保持原字符串。
- 源已是 mluc：整块复制，不改。

### 6.5 `wtpt` / `chad` / `cicp`

- `wtpt`：必需；必须是 XYZType（20 B：`XYZ ` + reserved + 3×s15Fixed16）。缺失 ⇒ `Malformed`。值偏离 D50 只告警，不改。
- `chad`：仅当 adopted white ≠ PCS D50 时必需。
  - 源有 `chad`：保留（sf32，44 B）。
  - 源无 `chad` 且 `wtpt` 与 D50 的差超过容差（建议 1e-4）：补一个 Bradford 适应的 `chad`，并在 `Degradations` 记 `IccChadSynthesized`。
  - 源无 `chad` 且 `wtpt` ≈ D50：不补。**不要**为了“看起来完整”恒写 `chad`。
- `cicp`：可选。**LUT profile 默认不新增 `cicp`**——`cicp` 描述的是编码空间的三元组，而 LUT profile 的编码空间由 LUT 本身承载，凭 profile 里的 matrix/TRC 推不出可靠的 primaries/transfer。只有源已有合法 `cicp` 时才保留（并校验 12 B 结构）。
- `rig0`：保留（`sig ` 12 B）。它是 legacy tag，但 v4.4 不禁止；删掉反而改变行为。
- 未知/私有 tag：整块复制；只要结构合法（offset/size 在界内、4 字节对齐、无部分重叠）就保留。

### 6.6 profile ID

按 v4.4：

```text
copy = profile bytes
copy[44..47] = 0     // flags
copy[64..67] = 0     // rendering intent
copy[84..99] = 0     // profile ID
id = MD5(copy)       // RFC 1321
write id to 84..99
```

- 允许写 0（表示未计算）；本方案要求计算，失败才写 0 并记 `IccProfileIdUnavailable`。
- 现有 `IccProfileBuilder.StampProfileId` 只清零 84–99，未清零 flags 44–47 与 intent 64–67；而 `WriteHeader` 的 intent 恒为 1，所以自生成 ICC 的 ID 不符合 v4.4，**必须修**。
- 校验：对 canonical 输出独立复算一次，必须逐字节相等。

### 6.7 无损判据

| 判据 | 方法 |
|---|---|
| LUT tag 数据逐字节不变 | 对每个 LUT tag（`A2B0..3`、`B2A0..3`、`D2B*`/`B2D*`）取 `SHA256(payload)`，源与 canonical 必须相等 |
| CLUT grid/precision 不变 | 解析后的 `grid[i]`、`precision` 相等 |
| 曲线参数不变 | 解析后的 `para` type 与参数逐项相等 |
| tag table 合规 | 连续、唯一、无空洞、无部分重叠、4 字节对齐、profile size 覆盖 pad |
| profile ID 可复算 | 独立 MD5 与写入值相等 |
| 必需 tag | `desc`/`cprt`/`wtpt` 在；`chad` 条件正确 |

### 6.8 生命周期与缓存

- 原始字节：`PlatformServices.GetTempDir()/icc_raw_<sha256[..16]>.icc`；只读、内容寻址。
- canonical：`PlatformServices.GetTempDir()/icc_v44_<sha256[..16]>.icc`；内容寻址、可复用、**不得被单个任务删除**。
- `IccProfileService.TryDeleteIcc` 的保护名单增加 canonical 前缀；`IsSharedNamedIcc` 增加内容寻址判定。
- 缓存键：`fullPath|mtime|size|sha256`；`IccProfileFactsCache` 进程内 `ConcurrentDictionary`。
- 避免每图重复分析：`ProbeCache`（TTL 10s）与 `IccProfileFactsCache` 共用同一份 facts；`ExtractIccFromBase64` 不再“分析即删”。

---

## 7. 代码落地：工作包（WP）

> 以下 WP 是实施单元，不是本轮动作。每个 WP 必须带“改哪些文件 / 新增哪些类型 / 验收哪条”。文件路径以工作树为准。

### WP-00 身份层与事实模型

**新增**

- `src/FfmpegGui/Services/ColorMapping/IccProfileFacts.cs`
  - `IccProfileStatus { Absent, Valid, Unreadable, Malformed }`
  - `IccTransformKind { MatrixTrc, LutA2BRelative, LutA2BPerceptualOnly, LutB2AOnly, LutMpeOnly, ValidButUnsupported }`
  - `IccProfileFacts { Status, Kind, Sha256, Size, Version, DeviceClass, DataSpace, Pcs, HeaderIntent, HasA2B0..3, HasB2A0..3, HasD2B, HasB2D, SelectedTag, SelectedIntent, CanonicalPath, RawPath, ReasonCode, Degradations, Lut }`
- `src/FfmpegGui/Services/ColorMapping/IccProfileAnalyzer.cs`
  - `Analyze(string path, Action<string>? log) -> IccProfileFacts`
  - `AnalyzeBytes(byte[] bytes, ...)`
  - 只读、无副作用；结构校验 + tag 分类 + 选择 A2B1 + 调 `IccLutParser`。
- `src/FfmpegGui/Services/ColorMapping/IccProfileFactsCache.cs`

**改**

- `IccProfileService`：`ExtractIccBytesToTemp` / `ExtractIccFromBase64` 改返回 `IccExtractionResult { Facts, RawPath, CanonicalPath, Description, ReasonCode }`；不再在 `NormalizeSourceIccToV44 == null` 时直接删原始字节。
- `ColorSpaceIdentify.FromIccFile`：改走 analyzer；LUT 返回带 facts 的描述符；`IdentifyIcc` 的 LUT 分支不调 `GuessColorSpace`。
- `ColorSpaceDescriptor`：加 `IccFacts? IccFacts`；`HasLut`；`IsMappable`；`Clone` 复制 facts（深拷贝 LUT）；`Equivalent`/`SameCurveAs` 对 LUT 必须 false（除非同一 canonical path+sha256）。

**验收**

- `ServiceProbe icc-profile` 对两个真实 profile 输出：`Valid`、`LutA2BRelative`、版本、类/空间/PCS、tag 列表、A2B0/1/2/3、B2A0/1、selected=A2B1、payload sha256。
- 对“无 ICC / 坏 ICC / 非 RGB / B2A-only / MPE-only / mft1+XYZ”各出对应 `Kind`/`ReasonCode`，不返回 null 丢原因。

### WP-01 提取链路“先保字节、再分析”

**改**

- `IccProfileService.ExtractIccToTempFile` / `ExtractIccFromBase64`：先写 raw 内容寻址文件，再 `Analyze`；`NormalizeSourceIccToV44` 只在 matrix/TRC 路径使用。
- `ExtractIccBytesToTemp` 的 fail-closed 语义：**显式 ICC** 由队列前置闸门处理；**内嵌 ICC** 解析失败时保留 raw、记 `IccEmbeddedMalformed`，继续走 CICP/惯例并告警（不因内嵌坏 profile 直接把任务打死）。
- `NormalizeSourceIccToV44`：返回 `IccCanonicalizationResult`（`Path`/`Facts`/`ReasonCode`），不再返回裸 `string?`。
- 快返回条件修正：`HasProfileId` 允许 0；`IsCleanV44Tags` 的 `chad` 条件化；**LUT profile 不走 matrix/TRC 重建**，走 shell 重写。

**验收**

- F-01 原始字节在两个真实 profile 上被保留；`icc_raw_*.icc` 与源 SHA256 相等。
- 一个合法 v4.4 LUT profile（含/不含 `chad`、profile ID=0）都能走 shell/原样路径，不被 `space==null` 丢弃。

### WP-02 Canonical LUT 解析器

**新增**

- `src/FfmpegGui/Services/ColorMapping/Lut/IccLutParser.cs`
- `.../IccLutStage.cs`、`.../IccClut.cs`、`.../IccCurveSet.cs`、`.../IccCurve.cs`、`.../IccPcsCodec.cs`
- `.../IccLutException.cs`（带 reason code，不用裸 `InvalidDataException`）

**要点**

- `mft1`：数据从 48；输入表 256×in；CLUT `g^in × out`；输出表 256×out；matrix 12–47。
- `mft2`：数据从 52；input entries @48、output entries @50；entries **2–4096 含**（§10.10）；CLUT `g^in × out` 16-bit；matrix 12–47；matrix 在 device→PCS 时按 §10.10 应为恒等，非恒等按“照用+记账”。
- `mAB`：按 12–31 的 5 个 offset 取段；A 曲线数=in、M/B=out；CLUT 20 B 头，**grid 读前 i 个 u8，各维可不同**（§10.12.3/Table 46），数据量 `∏Grid[j] × out × precision`；matrix 3×4，**非恒等合法**（§10.12.5），按仿射执行；curve set 无 count、4 字节对齐、可共享 offset。
- `mBA`：按 12–31 的 5 个 offset 取段；B/M=in、A=out；只解析；matrix 仅在 output/M=3 时允许（§10.13.3）。
- `mpet`：8–9 in、10–11 out、12–15 n、16 起 position table；只识别元素类型，不执行；遇未定义元素 ⇒ 该 tag 不支持（不是 malformed）。
- 所有 offset/size 做“在 tag 内、4 字节对齐、不越界”校验；失败抛 `IccLutException` 带具体 code。

**验收**

- 两个真实 profile 的解析结果与 §3.1 表逐项一致（offset、大小、grid、precision、曲线类型/参数、matrix 值）。
- 三个规范正控：`mAB` 非恒等 matrix（真实 A2B1 已是）；`mAB` 各维不同 grid（合成 `2×3×4`）；`mft2` entries=2 与 4096 各一条（真实 MartiMaria 是 2 项，4096 用合成）。
- `probe_icc_struct3.py` 的 JSON 作为独立对照；C# 解析器与 Python 解析器对同一文件输出同构结果。

### WP-03 v4.4 外壳重写器

**新增**

- `src/FfmpegGui/Services/ColorMapping/Icc/IccV44Rewriter.cs`
- `.../IccTagTable.cs`、`.../IccMluc.cs`、`.../IccHeader.cs`

**改**

- `IccProfileBuilder.StampProfileId`：按 §6.6 清零 flags+intent+ID。
- `IccProfileService.IsCleanV44Tags`：`chad` 条件化。
- `IccProfileBuilder` 增加 `RewriteShell(byte[] source, ...)` 入口（不改变现有 matrix/TRC 构建行为）。

**验收**

- `ServiceProbe icc-rewrite`：两个真实 profile → canonical；LUT payload SHA256 相等；tag table 合规；profile ID 独立复算相等；`mft2` 尾部 pad 正确；共享 offset 保留（MartiMaria 仍是一个 29,554 B 块被 3 个 tag 引用）。
- 负控：把共享 offset 拆成 3 份的变异体被门禁判红；把 LUT payload 改 1 字节的变异体被门禁判红。

### WP-04 规划层 LUT 路由

**新增**

- `src/FfmpegGui/Services/ColorMapping/IccLutPlanRouter.cs`

**改**

- `ColorTransformPlan`：加 `CanonicalLutTransform? Lut`、`double[]? PcsToDstMatrix`、`string? LutSelectedTag`、`int LutSelectedIntent`、`string? LutReasonCode`。
- `ColorStrategyPlanning.PlanWithPolicy`：在 `ResolveStrategy` 之后、GainMap/HDR 门与 `FaithfulIccPath` / 策略分派之前，若 `it.Source?.HasLut == true` 则 `return IccLutPlanRouter.Plan(...)`；这样 LUT+GainMap、LUT+HDR 由路由器给出**专属** reason（`IccLutGainMapUnsupported`/`IccLutHdrUnsupported`），而不是被通用门先吞掉。`PlanCore` 再加一道 `src.HasLut` 守卫（防直接调用绕过）。
- `ColorTransformPlan.ToSpec`：`Map` 分支拷贝 `Lut`/`PcsToDstMatrix`；`CarryIcc`/`None` 分支保持恒等 spec（`Lut=null`）。
- `ColorTransformPlan.SetLabelsFrom`：LUT 源不写名义 `OutPrimaries`/`OutTrc`（除非目标标准空间可命名）。

**路由矩阵**

| 条件 | Action | Backend | 结果 |
|---|---|---|---|
| LUT 源 + `Target == null` | `CarryIcc` | `None` | 附着 canonical v4.4 ICC；像素零改动 |
| LUT 源 + 显式目标（sRGB/P3/标准空间） | `Map` | `Lut`（路由标签） | `Lut` + `PcsToDstMatrix` + `DstCurve` |
| LUT 源 + 显式目标但目标不可命名/无 matrix | `Reject` | `None` | `IccLutTargetUnsupported`，exit 91 |
| LUT 源 + 无 A2B1 | `CarryIcc` 或 `Reject` | `None` | `IccLutPerceptualOnly`/`IccLutNoA2B1`；映射拒绝，携带可 |
| LUT 源 + 非 RGB | `CarryIcc` 或 `Reject` | `None` | `IccLutNonRgb`；映射拒绝 |
| LUT 源 + `mft1`+PCSXYZ | `Reject`（映射） | `None` | `IccLutXyz8Bit` |
| LUT 源 + GainMap | `Reject` | `None` | `IccLutGainMapUnsupported` |
| LUT 源 + HDR 目标/HLG/PQ | `Reject` | `None` | `IccLutHdrUnsupported` |
| LUT 源 + `AllowGamutCompression` | `Reject`（v1） | `None` | `IccLutGmoUnsupported`；不假装能做 GMO |
| LUT 源 + 出口不能带 ICC（JXL ffmpeg/WebM，carry 语义） | `Reject` | `None` | `IccLutCarrierLacksIcc` |
| LUT 源 + `BakeCicpOnly` + 目标可命名 | `Map` | `Lut` | 像素映射到目标；剥离 ICC、只写 CICP；合法 |

**验收**

- 两个真实 profile × {carry, recommended, manual-sRGB, manual-P3} × {png16, png8, avif, jxl} 的 `ServiceProbe plan` 逐组合断言 Action/Backend/`Lut`/`PcsToDstMatrix`/ReasonCode。
- 负控：A2B1 缺失 → 不得静默 A2B0；B2A-only → 不得映射；non-RGB → 不得映射。

### WP-05 执行内核 LUT 分支

**改**

- `ColorKernels.cs`：
  - `TransformSpec` 加 `CanonicalLutTransform? Lut`、`double[]? PcsToDstMatrix`。
  - `IsIdentity` 加 `&& Lut == null`（否则 `RawColorPipeline` 不会设 `SrcCodeMax`、会整趟跳过内核）。
  - `TableFastPathOperatorsAllow` 对 `Lut != null` 返回 false。
  - `TransformRgb48leCore`：在 fast path 之前短路到 `EvalLutPixel`。
  - `TransformRgb48leToRgb8`、`TransformRgba8`、`TransformStreamedAsync` 的 8-bit/流式出口共用同一 `EvalLutPixel`，只共用后半段 `EncodeScale → dstCurve → clamp → dither → quantize`。
  - `EvalLutPixel`：`x = code / SrcCodeMax`；阶段链；PCS 解码；`PcsToDstMatrix`；clamp；返回目标线性 RGB。
- `RawColorPipeline.cs`：
  - `:179` `if (!spec.IsIdentity)` 不变；LUT 时 `SrcCodeMax = ResolveSrcCodeMax(inputPath)`。
  - `TablesFor` 在 `spec.Lut != null` 时返回 `(null, null)`。
  - `TransformWholeFrameAsync` / `TransformStreamedAsync` 不改流程，只由 kernel 分流。
  - H1 `vf`：见 WP-08（F-04）。

**验收**

- `ServiceProbe matrix` 扩展 LUT 模式：单像素/小图回归；48→48、48→8、RGBA8、流式四条出口对同一输入输出同像素（逐字节）。
- `verify-color-icc-lut.ps1`：两个真实 profile × sRGB/P3 × 16/8-bit，与 oracle ≤2 LSB/16-bit（详见 §10）。
- 负控：把 `IsIdentity` 的 `Lut == null` 删掉 ⇒ 内核被跳过、产物与源逐字节相同 ⇒ 门禁必须红。

### WP-06 意图/策略/CICP 优先级（F-02/F-03）

**改**

- `ColorIntentFactory`：
  - 保留现有 `FromOptions(o, inputPath, out string? reason)` 签名（3 个调用点不破坏）；新增 `FromOptions(o, inputPath, out ColorIntentFailure? failure)` 重载，内部共用一份实现；现有调用点改为读 `failure?.Code`。
  - 显式 `--icc-file` 分支：不再 `catch {}` 静默；返回 `ColorIntentFailure`（`ExplicitIccMissing/Unreadable/NotProfile/Unsupported`）。
  - 显式 ICC 的分析结果由 `IccProfileFactsCache` 提供，避免二次分析。
  - LUT 源：`Curve` 只作名义值（建议 `Linear`）；`PrimariesToken=null`；`RgbToXyzD50=null`；`SourceIccPath=canonical`；`IsIccDerived=true`；`Confidence=IccMetric`。
  - `:277-290` 目标补轴：`src.HasLut` 时不把名义 `PrimariesToken`/`TransferToken` 带进目标；目标必须有显式两轴或可命名空间，否则 Reject。
  - `:418-423` `srcLinearHdr`/`srcHdr`：先判 `HasLut`，LUT 名义曲线不参与 HDR 判定。
- `FfmpegCommandBuilder.ProbeInputColorMetadataCore`：
  - ICC 段：`ExtractIccFromBase64` 返回 facts；`IdentifyIcc` 用 facts；`TryDeleteIcc` 不删 canonical。
  - `ApplyIccSemantics`：LUT ICC 置 `hasIccSemantics=true`、`sourceGamutName="ICC LUT"`（或 null），不得把描述文字猜成 sRGB。
  - CICP 优先段：`isLutIcc` 时 **ICC 优先**，容器 CICP 只在“与 ICC 的 `cicp` tag 语义一致”时保留；冲突则抑制并 `intentWarnings.Add("source LUT ICC conflicts with container CICP; ICC wins, CICP suppressed")`。
- `QueueProcessor`：新增 `ExplicitIccGate`（在任何 engine/legacy 路由之前）；失败 → exit 91、零产物、日志 `ExplicitIcc*`。
- `IccCarryPolicy`：入参改 `IccCarryCapability { CanCarryArbitraryIcc, CanCarryCicp, CanBoth, ExitWritesIcc, CanCarryLut }`；不再用 `sourceHasIcc` bool。

**验收**

- F-02：缺失/坏 ICC 三组 → exit 91、零产物、同 reason；合法 LUT 外部 ICC → 正常映射/携带。
- F-03：同一源 LUT ICC + P3 CICP → ICC 胜出、CICP 抑制、日志有警告；产物带 canonical LUT ICC（或目标 ICC），不出现“不同源生成相同 sRGB ICC”的旧现象。
- `QueueProcessor:3049` 的 `sourceHasIcc` bool 改为 capability；RAW 改写前后语义一致。

### WP-07 携带/标注：ICC-only 与双写

**语义**

- `AnnotChoice.IccOnly` 是**结果**，不是用户偏好。用户在“ICC 优先”语义下要求 ICC 为权威；在支持 CICP+ICC 的载体上，只要 ICC 空间可 CICP 命名，就必须 **ICC+CICP 双写**，不得只写 ICC。
- 只有两种情形允许 `IccOnly`：
  1. ICC 空间不可 CICP 命名（例如 LUT carry 且 profile 无 `cicp` tag）；
  2. 载体不能写 CICP（JPEG/WebP/TIFF 等）。
- `BakeCicpOnly` 仍是“剥 ICC、只写 CICP”；但 LUT 映射到可命名目标时，像素已落到目标空间，CICP 合规。
- 新增 `IccAnnotationMode { Auto, IccPreferred }`（`ColorIntent`/`PlanPolicy` 字段；**进 `PresetData` 持久位**，因为 D1 要求 GUI 状态可保存/回读）。`IccPreferred` 只影响“是否同时写 CICP”，不影响像素。
  - 旧 `IccMode.BakeToStandard`（若仍从旧预设迁入）映射到 `Recommended + IccPreferred`，而不是纯 `Recommended`；这样“选了 ICC”不会退化成 CICP-only。
  - **UI（D1，必做，不是可选项）**：四个策略单选不动；色彩面板新增“ICC 优先（双写）”勾选。控件实现、显隐/禁用矩阵、预设往返、CLI 对等、locale 见 WP-14。
  - **优先级（D4）**：`BakeCicpOnly` 赢；`IccPreferred` 在 `BakeCicpOnly` 下不生效，GUI 应禁用并给 tooltip；日志 `annot=conflict(bake-cicp-only wins)`。
  - **header intent（D5）**：canonical 不保留原 intent；按 WP-03 的 `IccIntentSelector` 重写，原文差异记 `IccIntentNormalized`。
- 判定顺序：`Annot = ClassifyAnnot(p)`；若 `IccPreferred && p.AttachIcc && targetNameable && caps.CanBoth` ⇒ 强制 `OutPrimaries/OutTransfer`，使 `ClassifyAnnot` 得 `IccAndCicp`；日志 `annot=icc+cicp (icc-preferred)`。

**验收**

- 双写正控：PNG/AVIF/JXL 上 `IccPreferred` + 可命名目标 ⇒ 产物同时有 ICC 与 CICP，且两者语义一致（`ServiceProbe decision` 的 `Annot` 字段 + 产物回读）。
- 例外：LUT carry（无 `cicp` tag）⇒ ICC only，AVIF `--cicp 2/2/0`；日志点名“ICC 空间不可 CICP 命名，CICP 置 unspecified”。
- 负控：把例外路径改成“伪造 P3 CICP” ⇒ 门禁红。
- GUI 正控：勾选后真实产物双写；取消后回到原行为；预设保存/载入后状态保持（WP-14）。
- 负控（D4）：`BakeCicpOnly` + 勾选 ⇒ 仍只写 CICP，且日志点名冲突；GUI 勾选应禁用而非静默忽略。

### WP-08 F-04 `InputMatrixDirective`

**新增**

- `src/FfmpegGui/Services/ColorMapping/InputMatrixDirective.cs`
  - `Applicability { NotApplicable, Applicable, Unsupported }`
  - `MatrixToken`、`RangeToken`、`SourceIsRgb`、`ReasonCode`、`ConsumedBy`（`legacy`/`h1`/`none`）。

**改**

- 计算单点：`FfmpegCommandBuilder.BuildInputMatrixDirective(options, probeMeta)`（或 `ColorMappingEngine` 静态入口），legacy 与 H1 都调它。
- legacy `scale=in_color_matrix` 分支：改用 directive；`declMatrix != null && options.ColorMatrix != null` 的旧条件不再各写一份。
- H1 `vf`：`Applicable && !SourceIsRgb` ⇒ 在 `format=rgb48le` 前插 `scale=in_color_matrix=<token>:<range>`；RGB 源只告警，不插。
- auto 路由：H1 不可消费（`Unsupported`/`SourceIsRgb`）且用户没显式要求 engine ⇒ 预检回退 legacy；显式 engine ⇒ exit 91、零产物。
- 日志：`input_declared`（用户给了什么）与 `input_matrix_consumed`（哪个后端消费了什么）分开。

**验收**

- `verify-color-matrix-full.ps1` 增 H1 两档 × engine/legacy × 直通 ffmpeg 参考：
  - 旧红基线：engine 两档均 4472 B、SHA256 `A7190016…`；legacy 10131/10560 B；直通 4069/4176 B。
  - 修复后：engine H1 两档必须分别接近直通/legacy 的对应矩阵产物（像素容差内），且两档之间可区分；不得再同 SHA256。
- 日志断言：`input_matrix_consumed=scale=in_color_matrix,bt470bg` / `bt709` 各一条；RGB 源出现 `input_matrix_not_applicable`。

### WP-09 F-05 `ColorSourceProvenance`

**新增**

- `src/FfmpegGui/Models/ColorSourceProvenance.cs`
  - `Origin { None, UserInput, RawIntermediate, UltraHdrIntermediate, Pipe }`
  - `OriginalInputPath`、`IntermediatePath`、`DeclaredPrimaries`、`DeclaredTrc`、`Producer`、`CreatedUtc`

**改**

- `FfmpegOptions`：加运行时字段 `ColorSourceProvenance? ColorSourceProvenance`（**不进 `PresetData`**）；`DeepClone`（JSON 往返）自动带上；`CloneOptionsForFfmpeg` / `MainWindow.CloneOptionsForFfmpegCommand` 显式复制。
- `QueueProcessor`：在改写 `captured.InputPath` 的同一处写 provenance（RAW 中间件、UltraHDR 解码、stdin/pipe）；`SourceInputPath` 仍在改写前保存。
- `ColorIntentFactory`：先读 provenance；`RawService.IsRawFile(inputPath)` 只作“直接 RAW 输入”的回退；`isSelfMadeIntermediate` 改为 `provenance?.Origin is RawIntermediate or UltraHdrIntermediate`。
- 兼容：`ColorSourceDeclPrimaries/Trc` 保留为 legacy 命令行构建的镜像；同一处写入，门禁断言两者一致。

**验收**

- `verify-raw-provenance.ps1`：
  - 正控：RAW → 线性 bt2020 中间件 → PNG；产物像素必须与“直接把中间件按 bt2020/linear 映射”的参考一致，不得退回 sRGB。
  - 负控：用户手写 `ColorSourceDecl*` 但没有 provenance 的 TIFF ⇒ 不得被当成自产中间件。
  - F-05 旧证据：`default_png_pixels.rgb48` 与 `intermediate_png_pixels.rgb48` 曾同 SHA256 `E35438…`；修复后必须不同（或至少与参考一致）。
- 覆盖：stdin、legacy、engine、GainMap 四条路径各一条断言。

### WP-10 F-06 exiftool 绝对路径

**改**

- `ExifToolService`：所有进程站点统一 `QuoteAbsArg`：
  - 源、目标、`-TagsFromFile`、`-o`、`-stay_open` 的 arg 文件、ICC 文件、批量列表文件。
  - `WorkingDirectory` 仍指向工具目录（`exiftool_files`/`perl532.dll` 需要）；PATH 注入保留。
  - `-charset filename=UTF8`：先按现状跑 CJK/空格/方括号用例；有失败再加，不凭猜测加。
- 审计同批：ffmpeg/dngtool/jxlinfo 的相对路径参数；至少断言“调用方 CWD 不是仓库根”时仍能找到输入/输出。

**验收**

- `verify-color-wiring.ps1` 增 F-06 用例：CWD 设为 `tests/output/_tmp/...`，输入/输出为相对路径；`ReadColorTagsResultAsync`、`CopyMetadataAsync`、`EmbedIccProfileFromFileAsync` 必须成功；`ServiceProbe wire` 不再出现 `100/1`。
- 带空格/CJK/方括号路径各一条。
- `perl532.dll code 126` 不计入产品失败（环境问题），但日志必须能与 F-06 区分。

### WP-11 GainMap 第二入口

**改**

- `GainMapJpegCodec.ResolveEncodeParams`：`iccNeeded` 改由 `IccProfileFacts` 判定；LUT 描述符不再走“无 CICP 也无可用 ICC ⇒ sRGB 惯例”。
- LUT + GainMap v1：**明确 `IccLutGainMapUnsupported`（拒绝）**（D3）；不做“仅携带不映射”，避免半对半错。GainMap 底图仍按 GainMap 契约处理，不走 LUT。

**验收**

- `verify-gainmap-isobmff` 增 LUT 源负控；断言不出现静默 sRGB；reason code 可见。

### WP-12 出口接线（AVIF / JXL / PNG / 其它）

**改**

- `RawColorPipeline.EncodeAvifViaAvifencAsync`：
  - `plan.AttachIcc` 时 `IccPath` 可为 canonical LUT 或目标标准 ICC；`NormalizeSourceIccToV44` 必须接受 canonical LUT（快返回）。
  - `plan.AttachIcc && OutPrimaries==null` ⇒ `--cicp 2/2/0`（unspecified），不伪造。
  - 有 `OutPrimaries` ⇒ `--cicp <p>/<t>/<m>` + `--icc`；ICC 与 CICP 同源。
  - 硬件 AV1 编码器 `ExitWritesIcc=false`：LUT carry ⇒ `IccLutCarrierLacksIcc`；LUT map 到可命名目标 ⇒ CICP-only 允许。
  - 16-bit sato 与 ICC 可并存；不改 `--depth` 逻辑。
- JXL：
  - cjxl 出口：canonical LUT ICC 走 ICC 参数；CICP 走 `color_space`（若该出口支持）；双写与 WP-07 同规则。
  - ffmpeg JXL 出口：不写 ICC；LUT carry 拒绝；LUT map 到可命名目标 CICP-only。
- PNG/APNG：
  - `iCCP` + `cICP`；LUT carry ⇒ iCCP only（无 `cicp` tag 时不写 cICP）；LUT map 到可命名目标 ⇒ 双写。
  - `PngCicpService` 的 `BakeCicpOnly` 语义不变；新增 `IccPreferred` 断言。
- 其它：JPEG/WebP/TIFF ICC-only；GIF/JXR LUT map 到 sRGB + `UnlabeledAssumedSrgb`（不写假标注），LUT carry 拒绝；WebM 无 ICC，carry 拒绝。
- `FfmpegCommandBuilder.Decision.cs` 的 png 分支：`carrySourceIccHere` 对 LUT 描述符为 true；`IccPreferred` 时不因 CICP 可写而清掉 ICC。

**验收**

- 载体矩阵 §9 每格一条 `verify-color-icc-lut.ps1` 断言；AVIF 正控检查 `--icc` 与 `--cicp` 同现且值一致。
- AVIF 负控：把 `--cicp 2/2/0` 改成伪造 `12/13/0` ⇒ 门禁红。

### WP-13 测试、门禁、Oracle

见 §10。这里只列文件：

- 新增 `tests/scripts/verify-color-icc-lut.ps1`
- 新增 `tests/scripts/verify-color-icc-rewrite.ps1`
- 新增 `tests/scripts/verify-raw-provenance.ps1`
- 新增 `tests/scripts/_probe-icc-structure.py`（由 `probe_icc_struct3.py` 提升、入库）
- 新增 `tests/scripts/_make-icc-lut-reference.py`
- 新增 `tests/projects/Tests.Shared` 的 mode 组登记
- 新增 `tests/fixtures/color/icc/`（两个真实 profile 副本 + README + SHA256）
- 新增 `tests/fixtures/color/icc-lut-reference.json`

### WP-14 GUI / 预设 / CLI 接线（D1 的落地）

**目标**：让“ICC 优先（双写）”成为一个真实可用的 GUI 控件，而不是只存在于 CLI 的隐藏开关。判定标准：用户能在界面上看到、能改、改完真的改变产物、重启/预设后仍然记得、切格式不会留下错误状态。

**新增/修改文件**

| 文件 | 改动 |
|---|---|
| `src/FfmpegGui/MainWindow.xaml` | 色彩面板新增 `CheckBox x:Name="IccPreferredCheck"`，Content `{ext:Loc color.icc.preferred}`，ToolTip `{ext:Loc color.icc.preferred.tip}`；放在策略单选组下方、ICC 文件行上方 |
| `src/FfmpegGui/MainWindow.xaml.cs` | `GetIccPreferred()` / `SetIccPreferred(bool)`；`IccPreferred_Changed` 更新 `_options.IccAnnotationMode` 并刷新预览/命令；`UpdateIccPanelVisibility` 增加 `IccPreferredCheck.IsEnabled` 与 ToolTip 原因；预设保存/载入、`CloneOptionsForFfmpegCommand` 复制 |
| `src/FfmpegGui/Models/FfmpegOptions.cs` | `IccAnnotationMode IccAnnotationMode { get; set; } = Auto;`；`DeepClone`/preset 迁移带上 |
| `src/FfmpegGui/Models/PresetData.cs` | `IccAnnotationMode` 持久位（旧预设缺省 = Auto） |
| `src/FfmpegGui/Models/AppSettings.cs` + `Services/SettingsService.cs` | 新增 `IccAnnotationMode`（默认 Auto）并同批进 `Save()` 的 clone 表；不依赖预设也记住 GUI 上次选择 |
| `src/FfmpegGui/CliParser.cs` | `--icc-annotation auto / icc-preferred`；未知值报错，不静默回退 |
| `src/FfmpegGui/Services/ColorMapping/ColorIntent.cs` / `ColorIntentFactory.cs` | 读 `IccAnnotationMode`；`IccPreferred` 只影响 `Annot`，不影响 `Target`/像素 |
| `src/FfmpegGui/Resources/Locales/zh-CN.json` / `en-US.json` | `color.icc.preferred`、`.tip`、`.disabled.bake`、`.disabled.no-icc`、`.disabled.hw-av1` 等 |
| `src/FfmpegGui/Models/PresetData.cs` 的迁移 | 旧 `IccMode.BakeToStandard` → `Recommended + IccPreferred`，旧 `BakeOnly` → `BakeCicpOnly`（保持原义） |

**显隐/启用矩阵（必须真实可用）**

| 当前策略 | PNG/APNG | JPEG/WebP/TIFF | AVIF 软件 avifenc | AVIF 硬件 AV1 | JXL cjxl | GIF/JXR/WebM |
|---|---|---|---|---|---|---|
| `CarryIcc` | 启用 | 启用（ICC only，无 CICP） | 启用 | **禁用**（硬件不写 ICC） | 启用 | 禁用（载体无 ICC） |
| `Recommended` | 启用 | 启用 | 启用 | **禁用** | 启用 | 禁用 |
| `Manual` | 启用 | 启用 | 启用 | **禁用** | 启用 | 禁用 |
| `BakeCicpOnly` | **禁用**（D4 赢） | **禁用** | **禁用** | **禁用** | **禁用** | **禁用** |

- 禁用原因必须写进 ToolTip，不能只灰掉：`BakeCicpOnly 已选择，ICC 优先不生效` / `该格式不能携带 ICC` / `硬件 AV1 编码器不写 ICC，请切回软件 avifenc`。
- **JPEG/WebP/TIFF**：勾选后是 ICC-only（载体无 CICP），ToolTip 要写明“该格式只能写 ICC”，不能把它伪装成双写。
- **`CarryIcc` + 硬件 AV1**：不是靠勾选禁用解决；策略层仍按 `ExitWritesIcc=false` 判定，不能携带源 ICC ⇒ exit 91 / 零产物或明确的降级告警，**不静默换编码器**。
- 切换格式/策略时先重算 enablement，再重算预览；不允许“上一格式的勾选状态”泄漏到下一格式（这与 UI 冻结修复里 `ColorSpaceCombo` 回 auto 是同类问题）。
- `IccPreferredCheck.IsChecked` 在禁用时保留用户值（不清零），恢复可用时回到用户上次选择；预设保存的是用户值，不是当前 enablement。

**CLI 对等**

- `--icc-annotation icc-preferred` 与 GUI 勾选等价；GUI 是主入口，CLI 是 headless 对等入口。
- `--icc-annotation auto` 恢复默认；不传 = auto。
- `BakeCicpOnly` + `icc-preferred` ⇒ 只写 CICP，日志 `annot=conflict(bake-cicp-only wins)`；退出码不因此变红。
- 与旧 `--icc-mode` 共存：`--icc-mode` 决定策略，`--icc-annotation` 只决定 ICC 优先；`BakeOnly`/`BakeCicpOnly` 下后者被 D4 覆盖，日志点名。

**旧位迁移**

- `ColorStrategy` 是唯一真值；`IccMode` 只作旧预设/旧 CLI 的兼容镜像。保存时按 `ColorStrategy` 写 `IccMode` 的兼容值，载入时优先 `ColorStrategy`。
- 现有 `GetIccMode` 与 `GetColorStrategy` 对 `Bake`/`BakeOnly` 的命名是交叉的（UI 的 `IccModeBake`=仅 CICP，`IccModeBakeOnly`=手动；旧枚举 `IccMode.BakeOnly` 又是 `BakeCicpOnly`）。不要继续让两个字段各写各的；预设回放断言必须锁住 `Recommended/CarryIcc/BakeCicpOnly/Manual` 四态与旧 `IccMode` 的对应。

**验收**

- 新增 `tests/scripts/verify-ui-icc-annotation.ps1`：
  - 四策略 × 六类载体的 enablement 矩阵（上表逐格）；
  - 勾选/取消后 `ServiceProbe decision` 的 `Annot` 字段 / 产物回读确实不同（PNG/AVIF/JXL 各一条正控）；
  - 预设保存→载入→重启后状态一致；
  - `BakeCicpOnly` 负控：勾选仍只写 CICP，日志点名；
  - 硬件 AV1 负控：`ExitWritesIcc=false` 时禁用，不静默换编码器。
- 真实桌面复核（V-01）：鼠标点击勾选、切格式、切策略、收起/展开高级面板，界面不卡死、不残留错误状态。
- 与 `verify-color-wiring` 的 CLI 断言同批：GUI/CLI 产物 SHA256 或标签逐值一致（同一输入、同一选项）。

**依赖/冲突**：WP-14 只碰 UI/预设/CLI；`ColorIntentFactory` 的 `IccAnnotationMode` 读取由 WP-06 先定义字段，WP-14 消费；`MainWindow.xaml.cs` 的预设段与 `f0fcb17` 的 GainMap UI 改动可能冲突，先落 WP-14 的字段，再 rebase。

---

## 8. 意图选择、回退与拒绝矩阵

### 8.1 源侧

| 源事实 | Kind | 可映射 | 可携带 | 默认动作 |
|---|---|---|---|---|
| 有 A2B1（`mAB`/`mft2`/`mft1`+Lab），RGB 3→3 | `LutA2BRelative` | 是 | 是 | Map（显式目标）或 Carry（无目标） |
| 仅 A2B0（无 A2B1） | `LutA2BPerceptualOnly` | 否（v1） | 是 | Carry；显式映射 ⇒ Reject `IccLutPerceptualOnly` |
| 仅 B2A | `LutB2AOnly` | 否 | 是 | Carry；映射 ⇒ Reject |
| 仅 MPE/D2B | `LutMpeOnly` | 否 | 是 | Carry；映射 ⇒ Reject |
| 非 RGB / n-channel | `ValidButUnsupported` | 否 | 否（像素语义不成立） | Reject |
| `mft1`+PCSXYZ | `ValidButUnsupported` | 否 | 是 | Carry；映射 ⇒ Reject `IccLutXyz8Bit` |
| 结构坏 | `Malformed` | 否 | 否 | 内嵌：告警+回退 CICP/惯例；显式：fail-closed |
| 无 ICC | `Absent` | — | — | 现有 CICP/惯例路径 |

> `LutA2BPerceptualOnly` 是“无 A2B1”的聚合类；A2B0 / A2B2 / A2B3 的具体原因由 `ReasonCode` 区分（`IccLutPerceptualOnly` / `IccLutNoA2B1`）。

### 8.2 目标侧

| 目标 | 允许 LUT Map | 说明 |
|---|---|---|
| sRGB / Display P3 / Adobe RGB / BT.2020 SDR（有 `RgbToXyzD50`） | 是 | `PcsToDstMatrix` 可求 |
| 具名但无矩阵 | 否 | `IccLutTargetUnsupported` |
| HDR（PQ/HLG） | 否（v1） | `IccLutHdrUnsupported` |
| GainMap | 否（v1） | `IccLutGainMapUnsupported` |
| GMO | 否（v1） | `IccLutGmoUnsupported` |
| 无色彩通路容器（GIF/JXR） | 是（钉 sRGB） | `UnlabeledAssumedSrgb`；不写假标注 |

### 8.3 策略侧

| 策略 | LUT 源行为 |
|---|---|
| `Recommended` | 能无损携带就携带；显式目标则 Map；不能 Map 且不能 Carry ⇒ Reject |
| `CarryIcc` | 只 Carry canonical v4.4；不 Map；载体不能带 ICC ⇒ Reject |
| `BakeCicpOnly` | Map 到可命名目标 + 只写 CICP；不能 Map ⇒ Reject；不静默 sRGB |
| `Manual` | 按显式目标 Map；目标缺轴/不可映射 ⇒ Reject；不猜 |
| `FaithfulIccPath` | LUT 源不参与“超广色域保真 ICC”路径；若同时命中，以 LUT 路由为准并告警 |

---

## 9. 载体矩阵（ICC / CICP / 双写）

| 格式 | ICC | CICP | 可双写 | LUT Carry | LUT Map→可命名目标 | LUT Map→不可命名 |
|---|---|---|---|---|---|---|
| PNG/APNG | 是（iCCP） | 是（cICP） | 是 | ICC only（无 `cicp` tag 时） | ICC+CICP | ICC only + 告警 |
| AVIF（avifenc 静态） | 是（`--icc`） | 是（`--cicp`） | 是 | ICC + `--cicp 2/2/0` | ICC+CICP | ICC + `2/2/0` |
| AVIF（硬件 AV1） | 否 | 是 | 否 | **Reject** | CICP only（允许） | **Reject** |
| JXL（cjxl） | 是 | 是（若出口支持） | 是 | ICC（+CICP 若有 `cicp` tag） | ICC+CICP 或 ICC | ICC only |
| JXL（ffmpeg） | 否 | 是 | 否 | **Reject** | CICP only（允许） | **Reject** |
| JPEG | 是 | 否 | 否 | ICC only | ICC（标准目标） | **Reject**（无 ICC 可写时） |
| WebP | 是 | 否 | 否 | ICC only | ICC | **Reject** |
| TIFF | 是 | 否 | 否 | ICC only | ICC | **Reject** |
| GIF/JXR | 否 | 否 | 否 | **Reject** | sRGB + `UnlabeledAssumedSrgb` | **Reject** |
| WebM | 否 | 是 | 否 | **Reject** | CICP only（允许） | **Reject** |

> “ICC only”在本表的含义：**ICC 是权威描述；CICP 只在不可命名/载体不支持时缺省**。不是“用户选了 ICC 就禁止 CICP”。这正是 WP-07 的语义。

### 9.1 AVIF 具体接线

`RawColorPipeline.EncodeAvifViaAvifencAsync` 现状（行号会漂移）：

- `plan.AttachIcc` ⇒ `plan.IccPath`；没有则按 `plan.IccPrimaries/IccTrc` 生成标准 ICC。
- `cicpUnavailable = plan.AttachIcc && string.IsNullOrWhiteSpace(plan.OutPrimaries)` ⇒ `--cicp 2/2/0`。
- `NormalizeSourceIccToV44(iccFile)` 失败即抛。
- 最终写 `--depth`、`--lossless/-q`、`--cicp`、`--icc`。

LUT 接入：

1. **Carry**：`plan.AttachIcc=true`、`plan.IccPath=canonical LUT`、`plan.IccPrimaries/Trc=null`、`plan.OutPrimaries/Transfer=null`。
   - `--icc canonical`；`--cicp 2/2/0`。
   - 若 canonical LUT 的 profile 内含合法 `cicp` tag，则把 `OutPrimaries/OutTransfer` 设为该 tag 的值，`--cicp` 写同值 ⇒ 真正的双写；否则 `2/2/0` 是“未指定”，不是伪造。
2. **Map**：`plan.AttachIcc=true`、`plan.IccPath=目标标准 ICC`（或 null + `IccPrimaries/IccTrc` 让 AVIF 函数生成）、`plan.OutPrimaries/Transfer=目标 tokens`。
   - `--icc` + `--cicp <p>/<t>/<m>`；ICC 与 CICP 同源。
3. **BakeCicpOnly**：`plan.AttachIcc=false`、`plan.OutPrimaries/Transfer=目标 tokens`；`--cicp` only。
4. **硬件 AVIF**：`ExitWritesIcc=false`；Carry ⇒ Reject；Map ⇒ CICP only（允许）。
5. `NormalizeSourceIccToV44` 对 canonical LUT：若已是干净 v4.4 LUT，直接返回；不得走 matrix/TRC 重建。

---

## 10. 测试、门禁与 Oracle

### 10.1 新增门禁（104 → 108）

> 现值以 `_run-step3-gates.ps1` 为准；本文件写作时登记为 104。加/删/改名必须同批改四处：`$scriptList`、`$expectedScriptCount`、runner 散文条数、`Tests.Shared/TestGroups.cs`；并同步 `_module-registry.json` 与 `docs/TESTING.md` 生成索引。

| 门禁 | 覆盖 | 关键断言 |
|---|---|---|
| `verify-color-icc-lut.ps1` | LUT 身份/映射/携带/载体矩阵 | 两个真实 profile × {png16,png8,avif,jxl} × {carry,recommended,manual-sRGB,manual-P3}；像素 vs oracle；ICC/CICP 存在性；reason code；负控（A2B0-only、B2A-only、non-RGB、mft1+XYZ、GainMap）；另加 jpeg/webp/tiff/gif/webm/jxr 的标注存在性（不逐像素） |
| `verify-color-icc-rewrite.ps1` | v4.4 外壳重写 | version=`04 40 00 00`；tag table 连续/唯一/对齐/无部分重叠；`desc/cprt` mluc；`chad` 条件；profile ID 独立复算；LUT payload SHA256 相等；共享 offset 保留；`mft2` pad |
| `verify-raw-provenance.ps1` | F-05 | RAW 中间件不再退回 sRGB；手写 `ColorSourceDecl*` 负控；stdin/legacy/engine/GainMap 四路 |
| `verify-ui-icc-annotation.ps1` | D1/WP-14 GUI 双写 | 四策略 × 六类载体 enablement 矩阵；勾选/取消产物差异；预设保存→载入→重启；`BakeCicpOnly` 冲突；硬件 AV1 禁用；GUI/CLI 逐值一致 |

扩展：

| 门禁 | 改动 |
|---|---|
| `verify-color-wiring.ps1` | 旧“坏 ICC 不采信且回落源=目标”断言改为 **exit 91/零产物/`ExplicitIcc*`**；合法 LUT 外部 ICC 正控；F-06 相对路径用例 |
| `verify-color-matrix-full.ps1` | F-04 H1 两档 × engine/legacy × 直通参考 + 日志断言 |
| `verify-decision-delivery.ps1` | LUT 新出口/新 reason 的计数 |
| `verify-color-strategy.ps1` | 新退化码/策略矩阵计数 |

### 10.2 ServiceProbe 新/扩模式

| mode | 输入 | 输出 | 断言数登记 |
|---|---|---|---|
| `icc-profile` | ICC 路径 | facts JSON（status/kind/version/class/space/PCS/tags/A2B0..3/B2A0..3/D2B/B2D/selected/reason/sha256） | 逐字段 |
| `icc-lut` | ICC 路径 + 目标 + 若干 RGB 三元组 | canonical 解析结果 + PCS + 目标 RGB | 逐点；另含 `mAB` 非恒等 matrix、`mAB` grid `2×3×4`、`mft2` entries 2/4096 三条合成正控 |
| `icc-rewrite` | ICC 路径 | canonical 路径 + tag table + profile ID + payload hash | 逐项 |
| `provenance` | 模拟 options + provenance | `ColorIntentFactory` 分类 + 目标/告警 | 逐案 |

登记链：`Tests.Shared/TestGroups.cs` 组表 → `_probe-test-group-coverage.ps1` 认领 → `verify-probe-mirror-parity` 逐 mode 钉死断言数（**先写断言、再数、再登记**，不能先填数）。

### 10.3 Oracle

**三个独立参考，各管一件事：**

| Oracle | 工具 | 管什么 | 容差 |
|---|---|---|---|
| A. 端到端解释 | Pillow 12.3.0 `ImageCms` / LittleCMS，独立进程 | 我们对 profile 的**解释**是否与 lcms 一致（tag/offset/PCS/A2B1） | ≤1 LSB/8-bit（约 257 LSB/16-bit） |
| B. 算术精度 | 独立 NumPy canonical chain（按规范文本实现，不抄 C#） | CLUT 插值/曲线/matrix/PCS 的**数值** | ≤2 LSB/16-bit/通道；报组内最大误差与具体 patch |
| C. 结构 | `_probe-icc-structure.py`（独立字节解析） | offset/tag table/共享/pad/曲线集 | 逐字节相等 |

**A 的关键设计**：用**我们自己生成的 sRGB v4.4 profile** 作为 lcms 的输出 profile，这样唯一变量是源 LUT 解释；`buildTransformFromOpenProfiles(lutProfile, ourSrgb, "RGB", "RGB", renderingIntent=1)`，对同一组 RGB patch 出 8-bit 结果，与产品 8-bit 出口逐像素比。

**B 的关键设计**：NumPy 参考使用与产品相同的**四面体插值**（否则数值不可比），但由不同作者按规范独立实现；用它验证 16-bit 出口。

**C 的关键设计**：`probe_icc_struct3.py` 提升为入库探针；`probe_icc_struct.py`/`probe_icc_struct2.py` 标记为错误版本，不得复用。

**已知的 16-bit 限制**：Pillow `ImageCms` 只出 8-bit 模式；不要把 8-bit 上采样到 16-bit 当成 16-bit 精度证据。16-bit 精度只由 B 承担。

### 10.4 测试向量

- 两个真实 profile 各一份；副本放 `tests/fixtures/color/icc/`，带 SHA256。
- patch 集：
  - 17³ 网格（4913 点）覆盖 RGB 立方体；
  - 中性轴 0..255 每 8 一档；
  - 6 个饱和角 + 6 个近中性；
  - 8-bit 白/黑顶码（验证 `SrcCodeMax`：255、0、1、254）。
- 输出目标：sRGB（v4.4 自生成）、Display P3（v4.4 自生成）。
- 位深：8-bit 与 16-bit 各一组。

### 10.5 验证纪律

- 不无选择器跑全量 104/108；裸跑把 `TMP/TEMP/TMPDIR` 钉到 `tests/output/_tmp/<round>`。
- 正式读数绑定 `FfmpegGui.dll`/`ServiceProbe.dll` 指纹；`*.exe` 哈希相同不代表同一份 IL。
- 不把 JXR PATH 假红、exiftool `perl532.dll code 126`、旧断言过期算产品失败；但必须在报告里点名。
- 红态留证：变异体/负控跑出的红日志另存，不用“改断言”抹掉。

---

## 11. 实施顺序与并行拆分

### 11.1 依赖图

```
WP-00 身份层 ──┬─ WP-01 提取保字节 ── WP-02 LUT 解析 ── WP-03 外壳重写 ─┐
               │                                                        ├─ WP-12 出口接线
               └─ WP-04 描述符/规划 ── WP-05 内核 ── WP-06 意图/策略 ────┘
WP-07 ICC-only 双写（依赖 WP-04/06/12）
WP-14 GUI / 预设 / CLI（依赖 WP-06/07/12）
WP-08 F-04（独立）
WP-09 F-05（独立）
WP-10 F-06（独立）
WP-11 GainMap（依赖 WP-04/06）
WP-13 测试/门禁（依赖全部）
V-01～V-12 前序遗留（§15；按轨 E/F，不阻塞 A–D 的代码路径）
```

### 11.2 并行轨（互斥文件，避免冲突）

| 轨 | 文件所有权 | 内容 |
|---|---|---|
| A. ICC 核心 | `IccProfileFacts.cs`、`IccProfileAnalyzer.cs`、`IccProfileFactsCache.cs`、`IccProfileService.cs`、`IccProfileBuilder.cs`、`ColorSpaceIdentify.cs`、`ColorSpaceDescriptor.cs` | WP-00/01/03 |
| B. LUT 模型 | `Services/ColorMapping/Lut/**`（全新目录） | WP-02；只新增，不改产品既有文件 |
| C. F-04/F-05/F-06 | `InputMatrixDirective.cs`、`ColorSourceProvenance.cs`、`ExifToolService.cs`、`FfmpegCommandBuilder.cs` 的矩阵段、`RawColorPipeline.cs` 的 H1 `vf` 段、`FfmpegOptions.cs`、`QueueProcessor.cs` 的 RAW 改写段 | WP-08/09/10 |
| D. 集成 | `ColorIntentFactory.cs`、`ColorStrategyPlanning.cs`、`ColorTransformPlan.cs`、`ColorKernels.cs`、`RawColorPipeline.cs` 的 AVIF 段、`QueueProcessor.cs` 的 engine 段、`IccCarryPolicy.cs` | WP-04/05/06/12；必须在 A+B 之后 |
| E. 门禁/文档 | `tests/scripts/**`、`tests/projects/Tests.Shared/**`、`_module-registry.json`、`docs/TESTING.md` | WP-13 + V-11；可与 A–D 并行写骨架，读数最后绑 |
| F. GUI/预设 | `MainWindow.xaml(.cs)`、`FfmpegOptions.cs`、`PresetData.cs`、`CliParser.cs`、`Resources/Locales/**` | WP-14；依赖 WP-06 的字段，可与 D 并行 |
| G. 前序遗留验证 | `tests/scripts/**`（只读/验证脚本）、`tests/output/**` | V-01～V-12；不改产品源码的项优先，需改的项按最小 patch 进 C/D |

> C 轨与 D 轨都碰 `RawColorPipeline.cs`/`QueueProcessor.cs`，必须在不同区段并以小 patch 合入；若同一时段冲突，C 轨先落，D 轨 rebase。A 轨与 D 轨都碰 `ColorSpaceDescriptor`/`ColorIntentFactory`，由 A 轨先定义字段，D 轨只消费。

### 11.3 建议波次

0. **波 0（开工前，不写产品代码）**：V-09 规范已复核（附录 F，结论转 WP-02/WP-05 正控）；V-06 发布口径/§8.1 豁免记录；V-01 GUI 基线复现（真桌面 + 日志/帧时间；仓外 worktree 已获许可）。
1. **波 1（并行）**：A（WP-00/01）、B（WP-02 解析器）、C（F-04/F-06，F-05 先落字段）、F（WP-14 的字段/locale/控件骨架）。
2. **波 2（并行）**：A（WP-03 重写器）、B（WP-02 测试向量/结构对撞）、C（F-05 接线）、F（WP-14 预设/CLI 接线）。
3. **波 3**：D（WP-04/05/06）串行，因为同一内核/规划文件；F 只消费 D 的字段，不反向改 D。
4. **波 4（并行）**：D（WP-12 出口）、F（WP-14 收口 + `verify-ui-icc-annotation`）、E（门禁骨架 + ServiceProbe 模式）。
5. **波 5**：E 收口（计数登记、镜像门禁、Oracle 对标、红态留证）+ V-01/V-02/V-03/V-05/V-06/V-11 的发布阻塞项。

---

## 12. 风险与开放问题

### 12.1 高风险

| # | 风险 | 影响 | 缓解 |
|---|---|---|---|
| R1 | **`SrcCodeMax` 与 LUT 输入域不一致** | 全局 scale 错，所有 LUT 映射偏亮/偏暗 | 用 8-bit/16-bit 各一条 ramp 正控：LUT 输入 `x=code/SrcCodeMax` 必须与 oracle 的 `v/255`（8-bit）/`v/65535`（16-bit）一致；不一致则给 LUT 单独的 `LutInputCodeMax`，不硬套 `SrcCodeMax` |
| R2 | **CLUT 插值方法不一致** | 与 lcms 对不上，oracle 假红/假绿 | v1 固定四面体；NumPy 参考用同法；lcms 端到端容差 ≤1 LSB/8-bit |
| R3 | **`mAB` Lab + 非恒等 matrix 的规范合法性** | 可能被误判 malformed | ICC.1:2022 §10.12.5 已确认 3×4 仿射 matrix 合法（无 identity 约束）；解析/执行非恒等；§3.1 的真实 A2B1 就是非恒等正控，另合成一条不同系数覆盖 Lab/XYZ 两侧 |
| R4 | **共享 tag 被复制成多份** | 体积膨胀、结构不等价 | 重写器按 (offset,size) 去重；门禁断言 MartiMaria 仍 3 tag → 1 块 |
| R5 | **8-bit 出口绕过 LUT** | 8-bit 产物静默错 | `TransformRgb48leToRgb8`/`TransformRgba8`/流式共用 `EvalLutPixel`；四条出口逐字节一致性门禁 |
| R6 | **`IsIdentity` 漏 `Lut`** | 内核整趟跳过，产物与源相同 | 负控删掉 `Lut==null` 必须红 |
| R7 | **canonical 临时件被 `TryDeleteIcc` 删** | 计划里路径失效 | 内容寻址 + 保护名单；并发两任务复用同一 canonical 的测试 |

### 12.2 已拍板决策与规范事实

D1–D6 见 §0.1，不再作为开放问题。三条规范事实已于 2026-11-17 取到 ICC.1:2022 原文（详见 §4.3 与附录 F），**无剩余待复核项**：

| # | 事实 | 原文结论 | 影响 |
|---|---|---|---|
| C1 | `mAB`/`mBA` matrix 在 PCSLAB 下是否必须恒等 | **不要求**（§10.12.5；identity 约束只在 §10.10/§10.11 的 `mft1`/`mft2`） | 非恒等 matrix 按仿射执行，不拒绝；真实 A2B1 非恒等直接作正控 |
| C2 | `mAB` CLUT grid 是否允许各维不同 | **允许**（§10.12.3；Table 46 前 i 项有效） | `IccClut.Grid` 为 `int[]`；合成 `2×3×4` 正控 |
| C3 | `mft2` input/output entries 范围 | **2–4096 含**（§10.10） | 接受 2..4096；2 项与 4096 项各一条正控 |

> 原文引文见附录 F；报告可写“已确认（ICC.1:2022 §...）”，不再写“未取到规范原文”。

---

## 13. 验收标准（Definition of Done）

- [ ] 两个真实 profile 在 `ServiceProbe icc-profile` 下均为 `Valid` + `LutA2BRelative`，A2B1 结构逐项与 §3.1 一致。
- [ ] 两者在 sRGB / Display P3 目标下，8-bit 与 16-bit 产物均通过 oracle（A ≤1 LSB/8-bit；B ≤2 LSB/16-bit）。
- [ ] 携带产物为合法 v4.4：version、tag table、必需 tag、profile ID、LUT payload SHA256、共享 offset、pad 全部通过。
- [ ] 规范正控：`mAB` 非恒等 matrix（真实 A2B1）、`mAB` 各维不同 grid（合成 `2×3×4`）、`mft2` entries=2 与 4096 各一条；解析/执行与 oracle 一致。
- [ ] 显式 `--icc-file` 缺失/坏/非 ICC → exit 91、零产物、`ExplicitIcc*`；合法 LUT → 正常。
- [ ] LUT ICC + CICP 冲突 → ICC 胜出、CICP 抑制、日志警告；产物不出现“不同源同 sRGB ICC”。
- [ ] F-04：H1 两档矩阵产物可区分且与直通/legacy 参考一致；日志 `input_matrix_consumed`。
- [ ] F-05：RAW 中间件不再退回 sRGB；手写声明负控不被误认。
- [ ] F-06：相对路径/CWD 不在仓库根时 exiftool 仍成功；`wire=100/1` 不再出现。
- [ ] 104 → 108 门禁登记四处一致；ServiceProbe 四个新 mode 在镜像门禁里逐名钉死。
- [ ] GUI（D1）：`IccPreferredCheck` 四策略 × 六类载体 enablement 矩阵逐格通过；预设保存/载入/重启往返；真实桌面点击不卡死、不残留状态。
- [ ] GUI/CLI 对等：同一输入、同一选项下 GUI 与 `--icc-annotation icc-preferred` 的产物标签/SHA256 逐值一致。
- [ ] D4：`BakeCicpOnly` + 勾选仍只写 CICP，日志点名冲突；硬件 AV1 下勾选禁用且不静默换编码器。
- [ ] D5：canonical header intent 按 A2B tag 重写，原文差异有 `IccIntentNormalized`；不再保留原 intent。
- [ ] 前序遗留：V-01/V-02/V-03/V-05/V-06/V-11 的发布阻塞项全绿；其余 V 项有明确处置（修/验/接受/推迟），未写成 PASS。
- [ ] 发布纪律：`RELEASE_GATES.md §6` 已记录“固定 beta17 重出”的豁免依据与源提交；tag 未移动。
- [ ] 红态留证：每个负控/变异体的红日志另存，未用改断言抹掉。
- [ ] 未改版本位、未转 beta18、未移动 `1.6.0BETA17` tag。

---

## 14. 发布约束

- 本方案不落地产品源码；实施批次由其他 agent 执行。
- 不转 beta18；后续按 beta17 重出；`1.6.0BETA17` tag **不移动、不重指**。
- **§8.1 豁免依据**：用户本轮已明确“不转 beta18、固定 beta17 重出”。这句话本身作为 `AGENTS.md §8.1` 的豁免依据，写入 `RELEASE_GATES.md §6`，不再要求用户重复确认；本文件不改 §8 正文。
- 重出时包内/CHANGELOG 必须显式标“重出、源提交 `<hash>`、内容 ≠ `957cfa8`”；包名/版本位仍为 `1.6.0-beta17`。
- **重出最低门槛**：V-01（GUI 真桌面/格式切换）、V-02（app 包首启与 oxipng 归因，若包内声称随附 oxipng）、V-03（GATE-04 CHANGELOG trace）、V-05（f0fcb17 若被声称为已验证）、V-06（发布纪律）、V-11（104→108 门禁登记）全绿。
- **不阻塞重出、但必须如实标注**：V-04（ProPhoto/HDR/AVIF 未测组合）、V-07（像素参考缺口）、V-08（ServiceProbe/Oracle）、V-10（GainMap UI 并行线）、V-12（AVIF/JXL 未标注 SDR 口径）——未收口项在发布说明/报告中写“未验证”，不得写成 PASS。V-09 已复核（附录 F），不在未收口列。
- 发布前跑变更链路门禁并绑定 `FfmpegGui.dll`/`ServiceProbe.dll` 指纹；红态禁止随包发布。
- 出包前清理 `publish/` 下 exiftool 旁路 `*.png_exiftool_tmp`（FMT-01 接受为已知残留，但不随包分发）；BUILD-03 的 5 条构建警告接受为存量，不因它阻塞出包。

---

## 15. 前序未完成项收口

### 15.1 来源与口径

本节把前面几轮已经发生、但没有在 LUT 主体里收口的事项并进来。来源逐份点名：

| 来源 | 未完成内容 |
|---|---|
| `docs/review-2026-11-16/CHANGES_48H_FIX_RESULT_2026-11-16.md` §6/§7 | 未验证 11 项；beta17 tag/包/代码三方；`AGENTS.md §8.1` 豁免；FMT-01/BUILD-03/GATE-04/BUILD-08；`f0fcb17` 未自证 |
| `docs/review-2026-11-16/CHANGES_48H_REVIEW_RESULT_2026-11-16.md` §8 | 未验证 15 项（GUI 真桌面、UI 负控、CPU/GPU、首启、基准、ProPhoto/HDR/AVIF、APNG、runner rc 等） |
| `docs/review-2026-11-17/COLOR_PIPELINE_INDEPENDENT_AUDIT_2026-11-17.md` §7 | 未裁决/未覆盖 4 项（PQ/HLG 像素参考、ServiceProbe 超时/判据、UI 隐藏禁用矩阵、LUT/RAW/H1/exiftool 修复由其他 agent 接手） |
| 本文件 §4.3、§12.1 | LUT 规范 3 项已按 ICC.1:2022 复核（附录 F）；高风险 R1–R7 |
| `docs/review-2026-11-16/UI_FREEZE_FIX_2026-11-16.md` §5 | 真实桌面 GPU 帧时间、日志长度 vs Probe 3s、`ColorSpaceCombo` 回 auto、日志复制交互 |
| `docs/review-2026-11-16/GAINMAP_UI_AND_BASE_GAMUT_ANALYSIS_2026-11-16.md` §未完成 | GainMap 真实点击、HDR 逐像素、两图合成、ProPhoto HDR、JXL runtime、HEIC 底图（并行线，不认领） |

口径：**“未验证”不等于“失败”，但也绝不写成 PASS。** 每项必须有 ID、现状、处置（修 / 验 / 接受 / 推迟）、验收或门禁、是否阻塞重出。F-01～F-06 的修复本身已由 WP-00～WP-12 覆盖，本节不重复列；本节只列“修复之外还没做完的部分”。已经收口的不再列：`MA-01` 的两条红门禁（`verify-decision-delivery` / `verify-probe-mirror-parity`）已由 48h 修复批修绿；`verify-ui-split-union` 的 7P/4F 已定性为旧宿主测量失效并已修绿；**V-09 的 LUT 规范三条已按 ICC.1:2022 原文复核并落入 §4.3/附录 F**（只保留三条正控待实施）。

### 15.2 遗留台账

| ID | 来源 | 事项 | 现状 | 处置 | 验收 / 门禁 | 重出阻塞 |
|---|---|---|---|---|---|---|
| V-01 | 48h 修复 §6.1/§6.2；audit §7.3；UI 冻结 §5；GainMap UI | GUI 真桌面：格式切换卡死、GPU 帧时间、日志长度 vs `Probe` 3s、`ColorSpaceCombo` 回 auto、日志复制；UI-01/02/03/05/06 旧负控；隐藏/禁用矩阵；GainMap 面板真实点击 | headless 59 绿；真桌面未复现；旧 worktree 已建（HEAD `957cfa8`，干净），待 cherry-pick 测试提交；高级面板收起值未矩阵；GainMap 无桌面点击 | 验 + 修（复现则修）；旧负控 cherry-pick 到 `957cfa8`（已获用户许可；worktree 已建，路径/清理见 §15.6）；新增 `_probe-ui-format-switch.ps1`（取证探针，不入 104→108 门禁计数，但入 `tests/scripts` 探针归属清单），矩阵并入 `verify-ui-icc-annotation` | 菜单收起/界面刷新/UI 线程无 >500ms 阻塞；旧红新绿；四策略 × 六类载体矩阵；GainMap 点击可操作 | **是**（GUI 可用） |
| V-02 | 48h 修复 §6.3/§6.4/§6.5/§6.6；BUILD-08 | `CpuFeatureService.FullReport()` stdout；GPU Verified 矩阵；干净首启；BUILD-04 appstage oxipng 两态归因 | headless 无 `--diagnose`；GPU 行缺；`%APPDATA%` 污染；`publish/PLAN` 在搜索路径 | 补 `--diagnose`（最小产品改动）+ 隔离环境复测 + 真 GPU 矩阵 | stdout 可锁定；GPU 矩阵逐项；首启无 ffmpeg/settings 无预警；app 档 oxipng 真生效 | 首启/oxipng **是**；CPU/GPU 否 |
| V-03 | 48h 修复 §6.7；GATE-04 | beta16 五项微基准；beta17 5 warnings；beta15 contract 164；包哈希；CHANGELOG trace | 原始日志缺；5 warnings 侧证；contract 164 vs 102 口径不同；GATE-04 推迟 | 重跑并绑定 DLL 指纹；补 CHANGELOG zh/en；包哈希入册 | 读数可指日志；CHANGELOG 有 trace；包哈希与 `FfmpegGui.dll` 指纹同段 | **是**（GATE-04） |
| V-04 | 48h 修复 §6.8；audit §7.1 | ProPhoto 非 D65→AVIF `--cicp 2/2/0`；HDR PQ/HLG AVIF 双写 ICC/CICP 一致；exiftool iCCP 后 cICP；APNG 帧；runner rc | 缺夹具/未实测 | 验；补夹具；扩 `verify-color-wiring` / AVIF 回读 | 逐值/逐帧/逐块；runner rc 与单跑一致 | 否（beta17）；是（AVIF/HDR 全支持声明） |
| V-05 | 48h 修复 §7.4 | `f0fcb17` 自证：AVIF SDR CICP 逐值；GainMap 面板全格式显隐矩阵 | 仅间接覆盖；无自身断言 | 并行线 owner 补断言；本批只做接口/回归 | 逐值；全格式矩阵；否则发布说明写“包含但未自证” | **是**（若声称已验证） |
| V-06 | 48h 修复 §7.1/§7.2；GATE-04；FMT-01；BUILD-03 | beta17 tag/包/代码三方；§8.1 豁免；CHANGELOG trace；FMT-01 tmp；BUILD-03 5 warnings | tag→`957cfa8`；包在 `957cfa8`；main 在其后；§8.1 冲突；tmp 残留 | 固定 beta17 重出；tag 不动；`RELEASE_GATES §6` 记录豁免；清 tmp；5 warnings 接受 | `_release-gate-check.ps1` exit 0；包/CHANGELOG 标源提交与差异；tag 未动 | **是** |
| V-07 | audit §7.1；GainMap UI §未完成；48h review §8.9 | PQ/HLG 显式映射；透明 ProPhoto alpha；GainMap HDR 重建；HDR 底图色度；两图合成；JXL GainMap；HEIC 底图；`COLOR_MATRIX_9X9` 取值 | 只有路由/标签/结构；无像素参考；9x9 未重推导 | 验；NumPy/Pillow oracle；补夹具；重推导 9x9 | 像素容差；逐值；9x9 与 CSV 一致 | 否（beta17）；是（HDR/GainMap 像素正确声明） |
| V-08 | audit §7.2 | `ServiceProbe decision` 185.2s；`verdict` 判据分歧；`gainmap-isobmff` 负控素材 | 超时记录；判据分歧；素材缺 | 验；根因/超时配置；固定判据；补负控 | 稳定 rc/耗时；判据可复现；负控红 | 否 |
| V-09 | 本文件 §4.3；§12.1 R1–R7 | `mAB` matrix 非恒等合法性；`mAB` CLUT grid 各维；`mft2` entries 2–4096；`SrcCodeMax` 输入域；共享 tag；8-bit 出口；`IsIdentity`；canonical 临时件 | 已按 ICC.1:2022 原文复核（§10.12.5、§10.12.3/Table 46、§10.10）；真实 A2B1 非恒等 | 复核结论落入 WP-02/WP-05；补三条合成正控（非恒等 matrix、`2×3×4` grid、entries 2/4096）；不再核原文 | 附录 F 引文；三条正控红绿；报告不再写“未取到” | 否 |
| V-10 | GainMap UI 分析 | AVIF `HdrModeCombo` 可见但 JXL 主面板缺同款；真实鼠标点击；ProPhoto HDR；JXL runtime；HEIC 底图 | 代码/headless；无桌面点击 | 并行线接口；本批不认领产品改动；`UiTestHost` 补矩阵 | 面板显隐矩阵；点击可操作；HDR 像素参考 | 否（beta17）；是（GainMap GUI 完整声明） |
| V-11 | 本文件 §10；WP-13 | 104→108；ServiceProbe 4 modes；`TestGroups.cs`；`_module-registry.json`；`docs/TESTING.md`；新 scripts；镜像 parity | 104；新 gates 未登记 | WP-13 同批改四处 + 生成索引 + 镜像 | `verify-probe-mirror-parity` 逐名计数；`verify-module-registry`；runner shape | **是** |
| V-12 | 48h 修复 §6.9 | AVIF/JXL 未标注 SDR 口径；PNG/APNG `UnlabeledMeansSrgb=true` 已收窄 | AVIF/JXL 各按各 | 产品口径单独拍板；本方案不改 | 有明确条目；不混用 | 否 |

### 15.3 处置优先级

**P0（重出 beta17 前必须收口）**

- V-01：GUI 真桌面/格式切换；如果复现，先修 UI 线程/探测，再谈出包。
- V-02：app 包首启与 oxipng 归因；包内声称“随附 oxipng”就必须隔离复测。
- V-03：GATE-04 CHANGELOG trace；没有 trace 的重出包不可归因。
- V-05：若发布说明声称 `f0fcb17` 已验证，必须先有它自己的断言。
- V-06：发布纪律、tag 不动、§8.1 豁免记录、FMT-01 清理。
- V-11：104→108 门禁登记；否则新增门禁是“黑户”。

**P1（完整方案前必须收口）**

- V-04：ProPhoto/HDR/AVIF/exiftool/APNG/runner 的未测组合。
- V-07：PQ/HLG、透明 ProPhoto、GainMap HDR 的像素参考；`COLOR_MATRIX_9X9` 重推导。
- V-08：ServiceProbe 超时/判据/负控素材。
- V-10：GainMap UI 并行线；如果它先落地，本批只做接口回归。

> V-09 已于 2026-11-17 按 ICC.1:2022 原文复核并落入附录 F，不再是 P1 待收口项；其结论转成 WP-02/WP-05 的三条合成正控。

**P2（口径/记录，不阻塞）**

- V-12：AVIF/JXL 未标注 SDR 口径；单独拍板，不混进 LUT 方案。

### 15.4 与 LUT 主体的边界

- **不重复修**：F-01～F-06 由 WP-00～WP-12 修；V 项只做验证、发布、并行线接口和规范复核。
- **不静默扩权**：V-02 的 `--diagnose`、V-01 的 UI 修复属于“最小产品改动”，必须单独列工作包与提交，不塞进 LUT 内核提交；V-10 的 GainMap 面板由并行线认领，本批只做接口回归，不代改。
- **不把未验证写成 PASS**：任何 V 项在报告里只能写“已验/未验/接受/推迟”，并带原因；发布说明同样。
- **红态留证**：V-01/V-02/V-03/V-04/V-05/V-11 的负控或修前基线，红日志另存，不用改断言抹掉。

### 15.5 与发布门槛的关系

- **重出最低门槛**见 §14；P0 全绿才允许 `pack.ps1`。
- **完整方案门槛**见 §13；P0+P1 全绿才允许在报告里写“色彩管线完整修复完成”。
- P2 不影响出包，但必须在发布说明里点名“AVIF/JXL 未标注 SDR 口径仍按各出口自有规则”。

### 15.6 仓外 worktree 许可（V-01）

用户已于 2026-11-17 明确许可仓外 worktree。V-01 旧负控实施时按以下边界执行；**worktree 已于 2026-11-17 创建并核对**（`HEAD=957cfa8`，`git status -uno` 干净），尚未 cherry-pick 测试提交：

- **已建路径**：`C:\PLAN\ffmpegPictureUI-audit-old-957cfa8`（与仓库同级；只用于 detached `957cfa8` 对照，不放任何交付产物）。
- **创建**（已执行，rc=0）：`git -C C:\PLAN\ffmpegPictureUI worktree add --detach C:\PLAN\ffmpegPictureUI-audit-old-957cfa8 957cfa8`。
- **已核**：`git -C <wt> rev-parse HEAD` = `957cfa8aa8c947e9cad533d085ec84877883ac07`；`git -C <wt> status --porcelain=v1 -uno` 为空。
- **内容**：只 cherry-pick 旧负控所需的测试/断言提交；不复制当前工作树未提交文件，不把 `docs/review-2026-11-17/` 带过去。
- **清理**：`git -C C:\PLAN\ffmpegPictureUI worktree remove --force C:\PLAN\ffmpegPictureUI-audit-old-957cfa8`；随后 `Test-Path` 必须为 `False`。若因占用/权限失败，先停，不做递归 `Remove-Item`，在回复里点名路径与占用原因。
- **证据**：负控日志写回仓库 `tests/output/_tmp/...`，worktree 本身不产出交付物。

---

## 附录 A：ICC v4.4 → 实现点对照（速查）

| 规范 | 实现类/方法 |
|---|---|
| 7.2 header | `IccHeader.Read/Write` |
| 7.3 tag table | `IccTagTable.Read/Write` |
| 8.2 必需 tag | `IccV44Rewriter.ValidateRequired` |
| 8.10 优先级 | `IccIntentSelector.Select` |
| 10.6 `curveType`（`curv`） | `IccCurve.ParseCurv` |
| 10.18 `parametricCurveType`（`para`） | `IccCurve.ParsePara` |
| 10.11 `lut8Type`（`mft1`） | `IccLutParser.ParseMft1` |
| 10.10 `lut16Type`（`mft2`） | `IccLutParser.ParseMft2` |
| 10.12 `lutAToBType`（`mAB`） | `IccLutParser.ParseMab` |
| 10.13 `lutBToAType`（`mBA`） | `IccLutParser.ParseMba` |
| 10.16 `multiProcessElementsType`（`mpet`） | `IccLutParser.ParseMpet` |
| 10.3 `cicpType` | `IccCicpTag` |
| 10.15 `multiLocalizedUnicodeType`（`mluc`） | `IccV44Rewriter` |
| 6.3.4 PCS | `IccPcsCodec` |
| 7.2.18 profile ID | `IccV44Rewriter.ComputeProfileId` |

## 附录 B：字段/偏移速查

**`mft1`**：`mft1`(0–3)、reserved(4–7)、in(8)、out(9)、pad(10–11)、matrix(12–47)、input tables(48 起 256×in)、CLUT(`g^in×out`)、output tables(256×out)。8-bit PCSXYZ 未定义。

**`mft2`**：`mft2`(0–3)、reserved(4–7)、in(8)、out(9)、grid(10)、pad(11)、matrix(12–47)、input entries(48–49)、output entries(50–51)、数据(52 起)：input tables(`in×entries×2`)、CLUT(`g^in×out×2`)、output tables(`out×entries×2`)。entries 2–4096；grid 单值。

**`mAB`**：`mAB `(0–3)、reserved(4–7)、in u8(8)、out u8(9)、pad(10–11)、B offset(12–15)、matrix offset(16–19)、M offset(20–23)、CLUT offset(24–27)、A offset(28–31)、数据(32 起)。处理顺序 A→CLUT→M→matrix→B；A=in 条、M/B=out 条；matrix 3×4 s15Fixed16，**规范未要求恒等**（§10.12.5）；CLUT 头 20 B（grid 16 B + precision 1 B + pad 3 B），**grid 前 i 项有效、各维可不同**（§10.12.3/Table 46）。

**`mBA`**：同 32 B 头（B/matrix/M/CLUT/A offsets 12–31）；处理顺序 B→matrix→M→CLUT→A；B/M=in、A=out；仅 PCS XYZ/Lab；matrix 仅在 output/M=3 时允许（§10.13.3）。

**`mpet`**：`mpet`(0–3)、reserved(4–7)、in u16(8–9)、out u16(10–11)、n u32(12–15)、position table(16–15+8n)、数据(16+8n 起)。

**`cvst`**：`cvst`(0–3)、reserved(4–7)、p u16(8–9)、q u16(10–11)、p 条 curve position(12–11+8p)、数据(12+8p 起)；q=p；曲线 4 字节对齐、可共享。

**`matf`**：`matf`(0–3)、reserved(4–7)、p(8–9)、q(10–11)、float32 `q(p+1)` 个(12 起)，顺序 `[e11..e1p, e21..e2p, ..., eq1..eqp, e1..eq]`。

**`clut`（MPE）**：`clut`(0–3)、reserved(4–7)、p(8–9)、q(10–11)、grid 16 B(12–27)、float32 数据(28 起)。

**`bACS`/`eACS`**：channels n(8–9)；p=q。

## 附录 C：公式

- PCSXYZ 16-bit：`X = raw / 32768`；0x8000 = 1.0；max = 1.999969482421875。
- PCSLAB 16-bit modern：`L = raw/65535×100`；`a = raw/257 − 128`；`b = raw/257 − 128`；0x8080 → 0。
- PCSLAB 16-bit legacy：`L = raw×100/65280`；`a = raw/256 − 128`；`b = raw/256 − 128`；0xFF00 → 100。
- PCSLAB 8-bit：`L = raw/255×100`；`a = raw − 128`；`b = raw − 128`。
- `mAB` matrix：`Y = M·X + offset`；结果裁剪 [0,1]。
- 目标：`PcsToDst = inverse(dst.RgbToXyzD50)`；`linearDst = PcsToDst · XYZ_D50`。
- profile ID：`MD5(profile with flags[44..47]=0, intent[64..67]=0, id[84..99]=0)`。

## 附录 D：两个真实 profile 的期望 facts

```json
{
  "sRGB_ICC_v4_Appearance.icc": {
    "status": "Valid",
    "kind": "LutA2BRelative",
    "version": "4.3.0.0",
    "deviceClass": "mntr",
    "dataSpace": "RGB",
    "pcs": "Lab",
    "headerIntent": 0,
    "profileId": "df1132a1746e97b07ad85719be711e08",
    "tags": ["desc","cprt","wtpt","chad","A2B0","A2B1","B2A0","B2A1","rig0"],
    "selectedTag": "A2B1",
    "selectedIntent": "media-relative",
    "a2b1": {
      "type": "mAB",
      "size": 436,
      "grid": [2,2,2],
      "precision": 2,
      "matrix": "non-identity",
      "curves": {"A":"3×para4","M":"3×para4","B":"3×para0"}
    }
  },
  "MartiMaria_browsertest_A2B.icc": {
    "status": "Valid",
    "kind": "LutA2BRelative",
    "version": "2.1.0.0",
    "deviceClass": "scnr",
    "dataSpace": "RGB",
    "pcs": "XYZ",
    "headerIntent": 0,
    "profileId": "00000000000000000000000000000000",
    "tags": ["desc","cprt","wtpt","A2B0","A2B1","A2B2"],
    "selectedTag": "A2B1",
    "selectedIntent": "media-relative",
    "shared": "A2B0/A2B1/A2B2 → mft2 @480 size 29,554",
    "a2b1": {
      "type": "mft2",
      "size": 29554,
      "grid": 17,
      "matrix": "identity",
      "inputEntries": 2,
      "outputEntries": 2
    }
  }
}
```

## 附录 E：本方案相对旧方案的变化

| 维度 | 旧方案 | 本方案 |
|---|---|---|
| 架构 | 引擎外挂独立 CMS 慢路 | 同一 H1 内核新增 LUT 分支；`ColorBackend.Lut` 仅路由标签 |
| 数据 | 独立 `IccLutTransform` 重写 LUT | 保留 LUT payload，只重写 v4.4 外壳 |
| 身份 | `Absent/Valid/Unreadable/Malformed` + `MatrixTrc/LutA2B/LutB2AOnly/ValidButUnsupported` | 增加 `LutA2BPerceptualOnly`/`LutMpeOnly`；facts 增加 A2B/B2A/D2B 位、canonical/raw path、reason/degradations |
| 意图 | 相对/感知未分 | 固定 A2B1；A2B0 默认拒绝；8.10 显式化 |
| 门禁 | 104→106 | 104→108（LUT 三条 + GUI 一条） |
| 规范 | 只覆盖 LUT 查表 | 覆盖 header/tag table/PCS/必需 tag/profile ID/mpet 识别/8.10 回退 |
| UI | 旧方案明确“不改真实 UI 面板的显隐/禁用逻辑” | D1 以 GUI 为准：WP-14 新增“ICC 优先（双写）”勾选 + 显隐/禁用矩阵 + 预设/CLI + `verify-ui-icc-annotation` |
| 前序遗留 | 只覆盖 F-01～F-06 | §15 增加 V-01～V-12：48h 修复批未验证/LOW/发布纪律、独立审查未覆盖、UI/GainMap 并行线、LUT 规范已复核（附录 F） |
| 发布 | 未明确 | 不转 beta18；按 beta17 重出；tag 不动；`RELEASE_GATES §6` 记录 §8.1 豁免 |

## 附录 F：ICC.1:2022 规范原文复核（V-09，2026-11-17）

来源：ICC.1:2022（Profile version 4.4.0.0），`https://archive.color.org/specification/ICC.1-2022-05.pdf`。以下为原文摘录，只保留与三条事实相关的句子。

### F.1 `mAB` matrix 不要求恒等

- §10.12.5 `Matrix`：`The matrix is organized as a 3 × 4 array...`；`The matrix is used to convert data to a different colour space...`；`The range of input values X1, X2 and X3 is 0,0 to 1,0. The resultant values Y1, Y2 and Y3 shall be clipped to the range 0,0 to 1,0 and used as inputs to the "B" curves.`
- 同节没有 `identity` 约束。`The matrix shall be an identity matrix unless the input is in the PCSXYZ colour space` 只出现在 §10.10 `lut16Type` 与 §10.11 `lut8Type`。
- 结论：`mAB`/`mBA` 的 3×4 仿射 matrix **不因 PCSLAB 而必须恒等**；真实 `sRGB_ICC_v4_Appearance` A2B1 的非恒等 matrix 是合法正控。`mBA` 另受 §10.13.3 约束（`The matrix is permitted only if the number of output channels, or "M" curves, is 3`），但 v1 只解析 `mBA`。

### F.2 `mAB` CLUT grid 各维可不同

- §10.12.3 `CLUT`：`The CLUT is organized as an i-dimensional array with a variable number of grid points in each dimension...`
- Table 46：`0 to 15 | 16 | Number of grid points in each dimension. Only the first i entries are used... Unused entries shall be set to 00h.`
- 对照：§10.10 `lut16Type` Table 40 的 grid 字段是 `Number of CLUT grid points (identical for each side) (g)`——单值；`mft1` 同。
- 结论：`mAB`/`mBA` 的 CLUT grid 是 `int[i]`，允许各维不同；`mft1`/`mft2` 是单值 g，各维相同。

### F.3 `mft2` input/output entries 2–4096

- §10.10 `lut16Type`：`Each input table consists of a minimum of two and a maximum of 4 096 ulnt16Number integers.`；`Each output table consists of a minimum of two and a maximum of 4 096 ulnt16Number integers.`
- 对照：§10.11 `lut8Type` 的输入/输出表固定 256 项。
- 结论：`mft2` 接受 2..4096（含端点）；`mft1` 固定 256。

### F.4 对实现的落地

| 事实 | 实现动作 | 正控 |
|---|---|---|
| F.1 | `IccLutParser.ParseMab` 不校验 matrix 恒等；`ColorKernels.EvalLutPixel` 执行 3×4 仿射；`mBA` 只解析 | 真实 A2B1（非恒等）+ 合成非恒等 |
| F.2 | `IccClut.Grid` 为 `int[]`；CLUT 插值按各维独立步长；`mft1`/`mft2` 由单值 g 展开为 `int[in]` | 合成 `2×3×4` grid |
| F.3 | `ParseMft2` 接受 2..4096；2 项与 4096 项都线性插值 | MartiMaria（2 项）+ 合成 4096 项 |

**未取到的内容**：无。三条均已取到原文；不再使用“未取到规范原文”的措辞。

---

*本文件是规划，不是实现记录。任何实施批次必须按 §13 的 DoD 与 §10 的验证纪律单独出报告；不得把本文件中的“期望”写成“已验证”。*
