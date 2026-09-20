# 色彩引擎「裁决接口」修复方案

> 2026-09-16 · 触发：`HANDOVER.md` 🔴【待实现】第 3 项「把 S2/S3 的 Reject 结局搬进传统管线」
> 状态：**S1 + S3 已实施**（零行为变更，门禁逐一持平）；S2 **暂缓**（语义冲突，见 §7）；S4–S7 未开始。
> ⚠ 待拍板：§5 的 A（`S3+jpg` 该 Reject 还是 `ProceedWithDegradation`）。
> **2026-09-17 更新**：原并列的「B（`gif/jxr/ppm` 语义收窄）」**已不再是待拍板项** ——
> 裁定结果与后续实现见 §5-B 的 2026-09-17 更新段（**GIF 接入引擎；`jxr`/`ppm` 维持不变；
> "不得收窄成 Reject"这条原裁定仍有效**）。A 项**仍待拍板**，未动。

---

## 0. 结论先行

**这是引擎侧问题，不是传统管线的问题。** 引擎把「做不到」表达成了**三种形状各异的字符串**，
且**没有任何结构化的「损失 / 替代」表达** ⇒ 调用方（传统管线、GUI、headless）**无法消费**，
只能各自猜。表现为"两条管线结局不一致"，实质是**引擎的裁决接口不可用**。

**因此不建议把 Reject 搬进传统管线**（那是让第二个消费者去适配一个坏接口）；
建议**修引擎接口**，让两条管线消费**同一个结构化裁决**。

---

## 1. 问题定性

### 1.1 现象（实测，非推断）

同一输入在两条管线上的结局不一致，且**落在常见用例**上：

| 格 | 引擎（默认） | 传统管线（`--color-engine legacy`） |
|---|---|---|
| S2 携带 + HDR 源 → jpg / webp / tiff | **Reject** | **产出**（且 `preserveSource` 跳过 tonemap ⇒ 产物很可能是**偏色/过曝**的） |
| S3 仅CICP + P3 源 → jpg / webp | **Reject** | **产出但无任何色彩标注**（色彩语义**静默丢失**） |
| GainMap × 非 JPEG | Reject + 建议 | 不适用（该组合在路由层就被拦） |
| `!caps.HasColorPath`（gif / jxr / ppm） | **装配不出意图**（返回 `null`） | 产出 |

> **2026-09-17 更新（本行已部分过时，保留原文以便追溯）**：**GIF 已接入引擎编码出口**
> （AVIF 更早已接入）⇒ 上表最后一行的 `gif` **不再**"装配不出意图"：`ColorIntentFactory`
> 的放行范围收窄为**只放 gif**（`ColorIntentFactory.cs:41` 的 `!caps.HasColorPath && fmt != "gif"`），
> `jxr` / `ppm` **仍**返回 `null` 走传统管线。
> ⚠ **§5-B 的原裁定仍然有效**：那条针对的是"**不要为了统一而把 gif/jxr/ppm 一律变成 `Reject`**"
> —— 这条**没有被推翻**；本次做的是**把 gif 接进引擎**（方向相反：不是收窄成拒绝，而是扩大引擎覆盖）。
> 详见 §5-B 的更新段。

### 1.2 根因：引擎有**三个拒绝面**，三种形状

| # | 位置 | 表达方式 | 能给出什么 |
|---|---|---|---|
| ① | `ColorMapping/ColorIntentFactory.cs:26`（**2026-09-17 起为 `:41`**，见下方注） | **返回 `null`** + `out string? reason` | 只有一句原因；**连意图都没有** ⇒ 调用方无从进一步询问 |
| ② | `ColorMapping/ColorEngineRouter.cs:119-203`（`BlockReason`）/ `:245-254`（`UnconsumedOutputSettings`） | **`EngineBlock?`**（2026-09-17 S2 起；`null` = 可走） | `Code`（稳定词表）+ `Text`（原一句话）+ `Alternatives`；与①③**同型但语义不混用**（见 §5-D） |
| ③ | `ColorMapping/ColorStrategyPlanning.cs:677` `Reject(p, reason, advice)` | `ColorTransformPlan` 且 `Action = ColorAction.Reject` | `Reason` + `Advice`（**自由文本**）；`SuggestedSuperset` 是唯一的**结构化替代**先例 |

> **2026-09-17 注（②③ 的行号已核对并更正）**：本表 ②③ 两行原写 `:50-69`/`:89-106`、
> `:495-505`，是 2026-09-16 的取值；此后两次接入（AVIF/GIF/JXL）让这两个文件长了不少
> ⇒ 原行号**已漂**（按当前文件读，`:50-69` 落在 `ExplicitlyRequested`/`GamutMapRequested`/`LegacyGamutMapConflict`
> 一带、`:495-505` 落在 `PlanManual` 的「ICC/CICP 择一」块，**都不是**所述函数）。上表已改为**当前实测**行号。
> ①（`:41`）的更正见上一段注。**结构结论（三个拒绝面、三种形状）不受影响** —— 漂的只是行号。

> **2026-09-17 注（S2 后 ② 的行号**再次**漂移）**：S2 把 `BlockReason` 的返回类型改为 `EngineBlock?`
> 并拆出 `UnconsumedOutputSettingItems`（结构化真值）⇒ ② 的两处行号随之下移：
> **原行号 `:91-116`（`BlockReason`）/ `:153-172`（`UnconsumedOutputSettings`）**
> ⇒ **S2 后为 `:119-203` / `:245-254`**（本表当前取值）。表达方式列亦由 `string?` 改为 `EngineBlock?`
> —— 这是 §5-D 裁定后的**有意**变更，不是漂移。② 的**语义**（「引擎不可走 ⇒ 回退传统管线」）
> 与三个拒绝面的结构结论**均未变**。

> **2026-09-17 注（① 的返回 `null` 已不再覆盖 gif）**：`ColorIntentFactory` 的"返回 `null`"判据
> 从 `!caps.HasColorPath` 收窄为 `!caps.HasColorPath && fmt != "gif"`（行号亦由 `:26` 变为 `:41`）
> ⇒ **GIF 现在会装配出意图**（`Action=Map`、`UnlabeledAssumedSrgb=true`、`Out*` 全空，不写假标注）。
> `jxr` / `ppm` 仍走 ① 的 `null` 路径。本表其余各行（②③）与"三个拒绝面"的结构结论**不受影响**。


`ColorAction` 只有 `{ None, Map, CarryIcc, Reject }`（`ColorTransformPlan.cs:11`）——
**二元终局**：要么做，要么拒。它**无法表达**「能做，但会失去 X」。

### 1.3 这个模型缺陷如何变成"两条管线不一致"

- 引擎**只能拒**（没有"降级但可做"这个选项）⇒ S3+jpg 这类"契约不可完全兑现、但产出是可能的"格，
  引擎被迫给 `Reject`；
- 传统管线**读不到裁决**：`FfmpegCommandBuilder.EffectiveColorStrategy` 只取了
  `plan.EffectiveStrategy`（策略来源），**丢弃了 `plan.Action`** ⇒ 引擎的 Reject 传不下去；
- 且成本闸 `declared != CarryIcc ⇒ 直接返回声明值` ⇒ **S3 的 Reject 压根没被计算过**；
- ⇒ 传统管线**结构上只会产出**（`DecideOutputColor` 没有 Reject 出口），于是"不一致"。

### 1.4 为什么"把 Reject 搬进传统管线"不是解法

1. **会移除功能**：传统管线是**明确登记的逃生门**（A/B 与排查用）。且引擎已是默认
   ⇒ 这些格对普通用户**本来就已经 Reject**；分歧只在显式 `legacy` 时出现。
   把它也改成 Reject = **抽掉逃生门唯一的价值**（拿不到产物）。
2. **治不了静默**：真正的问题是 S3+P3→jpg 产出**无标注**文件、S2+HDR→jpg 产出**偏色**文件，
   而用户只看到一行 INFO。**诚实 ≠ 拒绝**。
3. **会复制坏接口**：让第二个消费者去适配三种形状的字符串，是把缺陷扩散而非收敛。

---

## 2. 目标

1. **单一裁决**：任意「选项 + 输入 + 输出」组合，引擎给出**一个**结构化裁决对象。
2. **能表达"可做但有损"**：区分「物理不可能」/「策略拒绝」/「可做但会失去 X」。
3. **可消费**：两条管线、GUI、headless 都消费同一个对象，**不各写一份解释逻辑**。
4. **可断言**：门禁能断言**裁决本身**（比现在断言产物更本质），且能区分两种消费者。

---

## 3. 方案

### P0 — 引入统一裁决模型 `ColorVerdict`

新增 `src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs`：

```csharp
public enum VerdictKind
{
    Proceed,                 // 契约可完全兑现
    ProceedWithDegradation,  // 契约不可完全兑现，但**能产出且损失已知**（新能力）
    Reject,                  // 物理不可能，或策略明确拒绝
}

/// <summary>一处**结构化**的损失：Code 供程序判定/本地化，Text 供人读。</summary>
public readonly record struct ColorDegradation(string Code, string Text);

/// <summary>一条**结构化**的替代路径：改什么、改成什么、为什么。</summary>
public readonly record struct ColorAlternative(AlternativeKind Kind, string Value, string Why);

public enum AlternativeKind { Format, Strategy, Target, Option }

public sealed class ColorVerdict
{
    public VerdictKind Kind;
    public ColorAction Action;              // 复用现有枚举；Reject 时无意义
    public string Reason = "";              // 为什么是这个裁决
    public string Advice = "";              // 人读的建议（保留，兼容既有日志）
    public IReadOnlyList<ColorDegradation> Degradations = Array.Empty<ColorDegradation>();
    public IReadOnlyList<ColorAlternative> Alternatives = Array.Empty<ColorAlternative>();
}
```

**稳定 Code 词表**（供 GUI 本地化与门禁断言，不得用自由文本当判据）：
`NoCicpCarrier` · `NoIccCarrier` · `HdrNoIccCarryPath` · `ContainerCannotCarryIcc` ·
`UnlabeledAssumedSrgb` · `GamutClipped` · **`GamutCompressed`** · `BitDepthReduced` · `GainMapNonJpeg` ·
`GeometryWithGainMap` · `NoColorPath` · `AnimatedNotSupported` · `EncoderUnsupportedSetting` ·
**`ToneMapNotAppliedToGainMap`**。

> ⚠ **本清单必须与源码同集**（**唯一真值 = `ColorMapping/ColorVerdict.cs` 的 `DegradationCodes`**）。
> 核对锚点：`grep -c "public const string" src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs` ⇒ **14**。
> **加码/删码必须同步改本清单**，否则文档会与契约脱节。
> ⚠ 2026-09-17 补登 **两个漏项**：`GamutCompressed`（GMO 那轮新增，**从未登记进本清单**）与 `ToneMapNotAppliedToGainMap`（GainMap 出口不消费请求的 tonemap）。
> ⚠ **词表 > 实际生产**：`GainMapNonJpeg` / `GeometryWithGainMap` **全仓从未被生产**，且语义属 **`Reject` 理由** ⇒ **不得**登记成 `Degradations`（它们是"拒"的理由，不是"已产出的损失"）。

### P0b — `Alternative.Value` 词表（**S5 与 GUI 一键修正的前置，必须先定**）

`AlternativeKind` 只说了"改什么类别"，`Value` 的**取值域**必须定死，否则 GUI 无法消费、
门禁无法断言（`S1+S3` 实施时已因此把**策略类替代一律留空**）。定案：

| `AlternativeKind` | `Value` 取值域 | 例 |
|---|---|---|
| `Format` | **容器扩展名小写**，与 `--format` 同词表 | `"png"` `"avif"` `"jxl"` |
| `Strategy` | **`ColorStrategy` 枚举成员名**（PascalCase，与 CLI 词表 `recommended/carry/cicp-only/manual` 有一一映射） | `"Recommended"` `"CarryIcc"` `"BakeCicpOnly"` `"Manual"` |
| `Target` | **命名色彩空间名**（`ColorSpaceRegistry` 的 `Name`）或 CICP token | `"sRGB"` `"Display P3"` `"BT.2020"` / `"smpte2084"` |
| `Option` | **CLI 选项名去掉前导 `--`**（kebab-case） | `"color-tone-map"` `"jpeg-gain-map"` |

⚠ **不得把能力表判为"无写入通路"的格式列进 `Alternatives`** —— 那是把用户送进必然失败的任务
（实例：`heic`/`heif`，`FormatColorCaps` 实测全 false）。

### P1 — 三个拒绝面收口到同型

| 面 | 现状 | 改为 |
|---|---|---|
| ① `ColorIntentFactory.FromOptions` | `!HasColorPath ⇒ return null` | **不再因"无色彩通路"返回 null**：把 `HasColorPath=false` 作为**能力事实**记进意图，由规划层给 `Reject + Alternative(Format,…)`。`null` 只保留给**真装配失败**（options 为空、探测抛异常） |
| ② `ColorEngineRouter.BlockReason` | 返回 `string?` | 返回 **`ColorVerdict?`**（`null` = 可走）。语义不变（仍是路由前置检查：格式白名单 / 后端 / 动画 / 几何 / 渐进 JPEG / 编码器设置），只是**形状统一** |

> **2026-09-17 更正（S2 实施，方向已由 §5-D 裁定）**：上表 ② 的 `ColorVerdict?` **未采纳** ——
> 按 §5-D，`BlockReason` 改为返回**独立但同型**的 `EngineBlock?`（`Code`/`Text`/`Alternatives`），
> **不并入 `ColorVerdict`**：前者是「引擎不接 ⇒ 回退传统管线」，后者是「任务做不了 ⇒ 不产出」，
> 塞进同一个 `VerdictKind.Reject` 会让调用方把「回退」误读为「失败」。
> 本文档其余部分（含「三个拒绝面、三种形状」的结构结论）**不受影响**；上表原文保留以存历史。
| ③ `ColorStrategyPlanning.Reject(...)` | 写 `Action/Reason/Advice` | 构造 `ColorVerdict` 并挂到 `ColorTransformPlan.Verdict` |

`ColorTransformPlan` 保留 `Action/Reason/Advice` 作为**兼容投影**（= `Verdict` 的字段），
避免一次性改动所有读取点。

### P2 — 逐格判定：`Reject` 还是 `ProceedWithDegradation`

**这是本方案的核心判断。⚠ 2026-09-16 已裁定，且推翻了本文档初稿的一处判断（见下「初稿的错在哪」）。**

| 格 | 现状 | **裁定** | 理由 |
|---|---|---|---|
| **S3 + jpg/webp**（无 CICP 通路，源非 sRGB 等价） | Reject | **保持 Reject**，但补 `Alternatives(Strategy, "Recommended"/"CarryIcc")` + `(Format, …)` | ⚠ **初稿倾向 `ProceedWithDegradation`，是错的**：S3 的语义是"不要 ICC"，而 jpg **必须有 ICC 才有色彩语义** ⇒ 若"降级产出"，得到的是**P3 像素被当作 sRGB 解读**的**错误产物**。警告文案救不了一个错的产物 ⇒ **拒得对** |
| S2 + HDR 源 → jpg | Reject | **保持 Reject** | 物理不可能：JPEG 无法表达 HDR，且 S2 禁止改像素 ⇒ 二者矛盾。`Alternatives` 结构化（改 AVIF/JXL；或改 S1/S4 + tonemap） |
| GainMap × 非 JPEG | Reject | **保持 Reject** | 结构不可能（JPEG 的 APP2/XMP+MPF）+ `Alternative(Format, "jpg")` |
| GainMap + 最长边缩放 | 路由拦截 | **保持** | 未验证的新几何路径；改为 `Verdict` 形状 + `Alternative(Option, "…")` |
| `!HasColorPath`（gif/jxr/ppm） | 装配 null → 走 legacy | **不改**（见 §5-B） | 容器不能**携带**色彩信息 ≠ 不能**转换**。改成 Reject 会让"输出 GIF/JXR"整体不可用 |
| ⤷ **同上（2026-09-17 修订）** | **gif 现已接入引擎**（`jxr`/`ppm` 不变） | **裁定"不改"仍有效**（仍是"**不得收窄成 Reject**"）；被修订的只是"走 legacy"这句 | 见 §5-B 的 2026-09-17 更新段：方向相反（扩大引擎覆盖，不是收窄成拒绝） |
| HDR 源 + 容器不支持 HDR + 未请求 tonemap | Reject | **保持 Reject** | 规则正确；补 `Alternative(Option, "color-tone-map")` |
| S2 + 容器不能携带 ICC | Reject | **保持 Reject** | 契约不可兑现 |

#### ⚠ 初稿的错在哪 —— `ProceedWithDegradation` 的**正确用武之地**

初稿把 `ProceedWithDegradation` 当成"把 Reject 降级成产出"，**方向反了**。
正确用法：**给「引擎本来就会产出、但确实丢了东西」的格补上结构化损失** —— 这才是
「可用性诚实」的真正收益，且**不改变任何行为**（`Verdict` 目前无人消费）。

| 已产出的格 | 实际损失 | 应填 `Degradation` |
|---|---|---|
| 出口位深 16 → 8（容器上限） | 精度 | `BitDepthReduced` |
| 源色域超出目标 ⇒ **硬裁切**（未开 GMO） | 色域 | `GamutClipped` |
| 源色域超出目标 ⇒ **GMO 压缩**（`--color-gamut-map on`） | 色域（**去饱和**，非裁切） | `GamutCompressed` |
| 产物无标注但源按隐含 sRGB 处理 | 无（语义正确） | `UnlabeledAssumedSrgb` |
| 源 ICC 被丢弃（容器不能携带） | 描述精度 | `NoIccCarrier` / `ContainerCannotCarryIcc` |
| **GainMap 出口不消费请求的 tonemap** | **请求的曲线未生效**（实际走编码器内部的分段 Reinhard） | **`ToneMapNotAppliedToGainMap`** |
| 引擎无法执行该格式 ⇒ 回退传统管线 | **色彩决策不由引擎做** | `NoColorPath`（由路由侧给） |

> ⚠ `GamutClipped` 与 `GamutCompressed` **互斥**（同一像素不可能既被裁切又被压缩）：由 `ColorTransformPlan.GamutMap` 决定登记哪一个；**两者都不登记更糟** —— 一次真实的去饱和会被裁决报成 `Proceed`（无任何损失）。
> ⚠⚠ **登记前必须先问「系统是不是本来就打算这么干」—— 若是，就**不是**损失。**
> 实例：GainMap 的 tonemap 登记条件是 `p.GainMap && it.ToneMapRequested && **!it.ToneMapAuto**`。那个 `!ToneMapAuto` 是**必需的假阳性防护**：自动兜底（HDR 源 + 容器不支持 HDR ⇒ 内部默认 hable）的**目的**就是"把 HDR 压成可看的 SDR 底图"，而 GainMap 出口的分段 Reinhard **正是干这件事** ⇒ 目的已达成、不构成损失。
> 少了它，**每一次 HDR→JPEG GainMap 都会被报成"有损失"** ⇒ `ProceedWithDegradation` 变成噪声 ⇒ 用户很快就不再相信它。
> ⚠ **登记降级不改变「产出 / 不产出」**：`CollectDegradations` 只在**已产出**的格上跑（见 `ProjectVerdict`）⇒ 它只把 `Kind` 从 `Proceed` 翻成 `ProceedWithDegradation`。这是 S5′ 的硬边界。

⇒ `Kind = ProceedWithDegradation` **只在"产出正确、但有可量化损失"时使用**；
凡"产出会错"的一律 `Reject`。

### P3 — 单一消费接口（"决策层统一"的真正落地）

```csharp
// 两条管线唯一的入口（不再各写一份）
public static ColorVerdict Decide(FfmpegOptions o, string outputPath, bool inputIsAnimated,
                                  out ColorTransformPlan? plan);
```

消费规则（**两条管线共用**）：

| `Verdict.Kind` | 传统管线（legacy） | 引擎 |
|---|---|---|
| `Proceed` | 照常产出 | 照常产出 |
| `ProceedWithDegradation` | **产出 + 明确告警**（逐条列出 `Degradations`） | 产出 + 同一告警 |
| `Reject` | **中止**，输出 `Reason` + `Advice` + `Alternatives` | 同（显式 `engine` 时硬失败；`auto` 时若"引擎不可走但传统可走"仍按现有回退语义） |

⇒ 「把 Reject 搬进传统管线」被**替换**为「两条管线消费同一裁决」——
不再是"要不要让 legacy 也拒"，而是"**同一模型、两种消费**"。

### P4 — 门禁与 UI 连带

1. **`verify-color-strategy.ps1`**：
   - 现有 `:106` 的注释「传统管线不做契约拒绝 ⇒ expectReject 一律 `$false`」**作废**；
   - 新增**裁决 parity 断言**：同一输入的 `Verdict.Kind` 两条管线**必须相同**
     （比现在只比产物 CICP/ICC 更本质）；
   - legacy 的 `expectReject` 由**读裁决**决定，不再硬编码。
2. **`ServiceProbe` 新增 `verdict` 模式**：纯函数级断言 `Kind/Reason/Degradations/Alternatives`，
   每个 Code 一条；**必须含负控**（例如"同格改一个选项 ⇒ Kind 必须改变"）。
3. **GUI**：`Degradations` 驱动提示（如"此设置会失去色彩标注"），`Alternatives` 驱动一键修正。
4. **headless**：`Reject` ⇒ 非零退出码 + 打印 `Alternatives`（保留现有行为并结构化）。

---

## 4. 实施顺序与风险

| 步 | 内容 | 风险 | 门禁要求 |
|---|---|---|---|
| **S1** | 落 `ColorVerdict` + 兼容投影（**不改行为**） | 低 | 全部门禁数字**不变** | ✅ 已实施 |
| **S3** | 面③ `Reject` 构造 `Verdict` | 低 | `contract` 断言形状更新 | ✅ 已实施 |
| **S2′** | 面② **重新定义**：路由拦截**不并入** `ColorVerdict`，改为独立的 `EngineBlock { Code, Text, Alternatives? }`（见 §5-D） | 低 | `wire` 断言形状更新 | ⏳ |
| **S5′** | **给「已产出但有损」的格补 `Degradations`**（见 §3 P2 末表）；不改「产出 / 不产出」 | 低 | 纯新增 ⇒ 既有门禁数字**不变**；新增 `verdict` 探针 mode | ✅ 已实施（2026-09-17） |
| **S6′** | 传统管线消费裁决：先只消费 `Degradations`（**告警**），`Reject` 的消费**暂不做**（见 §5-E） | 中 | `strategy` 新增裁决级断言 | ⏳ **待用户确认**（§5「仍待你确认」唯一一条） |
| **S7** | GUI/headless 接入 `Degradations`/`Alternatives` | 低 | `strategy` 新增**日志级**断言 | ✅ 已实施（2026-09-17，58/0） |
| ~~S4~~ | ~~收窄 `FromOptions` 的 `null` 语义~~ | — | **已裁定取消**（见 §5-B） | ❌ 取消 |

**总风险**：原初稿的 S4/S5 会改变**用户可见结局**。**裁定后已消除** —— 保留的步骤
（S5′/S6′/S7）**均不改变"产出 / 不产出"**，只增加**结构化的损失告知**。

---

## 5. 裁定记录（2026-09-16，lead 自裁；均为**维持现状**，可随时推翻）

> **裁定原则**：凡"**产出会错**"的一律 `Reject`；凡"**产出正确但有可量化损失**"才用 `ProceedWithDegradation`。

### A. `S3（仅CICP）+ jpg/webp` ⇒ **保持 `Reject`**（**推翻本文档初稿的倾向**）

初稿倾向 `ProceedWithDegradation`，理由是"用户要求与容器能力冲突，告知并让其决定"。**该判断是错的**：
S3 的语义是"不要 ICC"，而 jpg **必须有 ICC 才有色彩语义** ⇒ "降级产出"得到的是
**P3 像素被当作 sRGB 解读**的产物 ⇒ **色彩是错的**。**警告文案救不了一个错的产物** ⇒ 拒得对。
`Alternatives` 改为结构化：`(Strategy,"Recommended")` / `(Strategy,"CarryIcc")` /
`(Format,"avif"|"jxl"|"png")`。

### B. `gif / jxr / ppm` ⇒ **不改**（**取消原 S4**）

`HasColorPath=false` 的含义是"容器不能**携带**色彩信息"，**不等于不能转换**。
把 `FromOptions` 的 `null` 收窄成 Reject ⇒ **"输出 GIF/JXR"整体不可用** ⇒ 明确不可接受。
⇒ 保持"返回 null → 走传统管线"；**改为在「诚实提示」上补漏**（现状提示只覆盖三类，
`PNG→GIF` / `PNG→AVIF` 这类不打印）。

> **2026-09-17 更新（本节被**部分修订**，原文保留）**：
> - **仍有效（未被推翻）**：上面那条裁定针对的是"**不要为了统一而把 `gif/jxr/ppm` 一律收窄成 `Reject`**"
>   —— 该结论**继续有效**，且**已经落地**：`ColorIntentFactory.cs:41` 仍是 `!caps.HasColorPath && fmt != "gif"`，
>   `jxr` / `ppm` 仍返回 `null` 走传统管线，**没有任何一格变成 Reject**。
> - **被修订**：原文"保持**返回 null** → 走传统管线"这句里的 **`gif` 部分不再成立**。
>   GIF 于 2026-09-17 **接入引擎编码出口**（`ColorEngineRouter` 白名单 + `ImageEncoderArgs.BuildGifFilterChain`
>   /`BuildGifOptions` + `RawColorPipeline` 的 gif 出口，含"第二输入补 alpha"），
>   ⇒ 它现在**装配得出意图**，规划层有唯一诚实结局：`Action=Map` 到 sRGB 工作空间、
>   `UnlabeledAssumedSrgb=true`（降级码 `DegradationCodes.UnlabeledAssumedSrgb`）、`Out*` 全空（**不写假标注**）。
> - **方向区别（关键）**：原裁定防的是"**收窄成拒绝**"；本次做的是"**扩大引擎覆盖**"——**方向相反**。
>   ⇒ 二者不矛盾：**GIF 既没有变成 Reject，也不再落传统管线**。
> - AVIF 更早（同为 2026-09-17）已按同一路径接入，本节原文当时未同步，一并更正。
> - 实现细节与门禁见 `docs/HANDOVER.md` 与 `tests/scripts/verify-color-strategy.ps1` 的 ③c 段。


### C. `Alternatives` 的"一键应用" ⇒ **暂不做**，但词表必须先定（已定案，见 §3 P0b）

GUI 一键修正依赖 `Alternative.Value` 词表；是否做按钮属交互设计，留待 S7。

### D. S2 的方向 ⇒ **路由拦截不并入 `ColorVerdict`**

`BlockReason` 的"拒"= 「引擎不可走 ⇒ 回退传统管线」，**不是任务级终局** ⇒ 塞进 `VerdictKind.Reject` 会混淆。
⇒ 保留其为**谓词**，另加**同型但不混用**的 `EngineBlock { Code, Text, Alternatives? }`，
供 GUI 解释"为什么没走引擎"。
**（2026-09-17 已按本条实施，见 §7「S2 的收口」。）**

### E. S6 的边界 ⇒ **已由维护者裁定：传统管线也消费 `Reject`**（2026-09-17，**推翻本节原裁定**）

**原裁定（保留以存历史）**：只消费 `Degradations`（告警），`Reject` 的消费**暂不做** —— 理由是"把 `Reject` 搬进传统管线会移除逃生门的产出能力"。

**维护者裁定（2026-09-17）**：
> 「**拒绝**，用自研管线完成色彩映射。」

⇒ **传统管线消费与引擎同一份裁决**：
- `Verdict.Kind == Reject` ⇒ 传统管线**不产出**（非零退出 + 打印 `Reason`/`Advice`/`Alternatives`）；
- `Verdict.Kind == ProceedWithDegradation` ⇒ 产出 + 逐条打 `降级（{Code}）`；
- `Verdict.Kind == Proceed` ⇒ 不变。

**⚠ 一条必须同时守住的边界**（§5-B 的裁定**继续有效**，不被本条推翻）：
本次改的是「**传统管线要听裁决**」，**不是**「把更多格变成 `Reject`」。
`gif` / `jxr` / `ppm` 这类 `HasColorPath=false` 的格式**不得**被收窄成 `Reject` ——
它们的含义是"容器不能**携带**色彩信息"，**不等于不能转换**。⇒ **一格都不许新增 `Reject`。**

### ✅ 已裁定（原「⏳ 仍待你确认」，2026-09-17 结案）

**传统管线在 `Reject` 格上该不该也拒绝？** ⇒ **应当拒绝**（维护者裁定，见上 §5-E）。
事实依据：引擎已是默认 ⇒ 那些格对普通用户**本来就已 `Reject`**；分歧只在显式 `--color-engine legacy` 时出现，
而 legacy 此刻会产出**错误产物**（如 S3+P3→jpg 的 P3-as-sRGB）。⇒ 这是**修 bug**，不是移除功能。
**代价已知并接受**：显式 `--color-engine legacy` 的可见结局会变。

---

## 6. 明确不做的事

1. **不把 Reject 无条件搬进传统管线** —— 那是让第二个消费者适配坏接口（见 §1.4）。
2. **不在 S1–S3 阶段改任何行为** —— 先统一形状，再逐格改语义，保证每步可回滚。
3. **不新增第三个决策面** —— `ColorVerdict` 只是**既有三个面的统一表达**，
   裁决逻辑仍留在 `ColorStrategyPlanning`（单一裁决点不破）。
4. **不用自由文本当门禁判据** —— 断言一律打 `Degradation.Code` / `AlternativeKind`。

---

## 7. 实施进展与技术债

| 步 | 状态 | 说明 |
|---|---|---|
| **S1** | ✅ 已实施（2026-09-16） | 新建 `ColorMapping/ColorVerdict.cs`（108 行）；API 与 Code 词表与本文档 §3 P0 **逐字一致** |
| **S3** | ✅ 已实施（2026-09-16） | `ColorTransformPlan.Verdict`；`ColorStrategyPlanning.cs:41` 为**唯一投影点**（覆盖所有返回分支，含各 `PlanXxx` 与其提前 Reject）；8 处 `Reject` 补 `Alternatives` |
| **S2** | ✅ 已实施（2026-09-17） | 见下「S2 的收口」（`wire` **58 → 71/0**）；**判据/顺序/文案三不变**，只加结构化 |
| **S5′** | ✅ 已实施（2026-09-17） | `ColorStrategyPlanning.CollectDegradations`（`:115`）+ `ProjectVerdict`（`:76`）生产 6 个降级码；`ServiceProbe verdict` **36/0**（含 `Kind == ProceedWithDegradation ⇔ Degradations.Count > 0` 的双向不变式 + 「不得出现未登记 Code」锁） |
| **S6′** | ⏳ **待用户确认** | 见 §5-E 与「仍待你确认」——本文档**唯一未决项** |
| **S7** | ✅ 已实施（2026-09-17） | `QueueProcessor.cs` 成为裁决的**真消费者**：`:812` 打 `block.Code`；`:757` 新增 `LogAlternatives` 助手（`:813`/`:817`/`:844` 用）；`:834-836` 逐条打 `降级（{Code}）：{Text}`；`:828` 把 `裁决={Kind}` 写进 `item.Command`。门禁 `verify-color-strategy` **58/0**（新增 `verdict-降级码-gif` + 负控 `verdict-降级码负控-png` + `verdict-替代方案-jpg`）。⚠ 动工前实测：`src/FfmpegGui` 里**除 `Services/ColorMapping/` 之外没有任何 `Verdict` 引用** ⇒ 结构化损失信息此前**从未到达用户**（`:808` 拿到 `plan` 却只读 `IsOk`/`Advice`/`Action`）。⚠ **已知小缺口**：`:583` 在直通格（`engUsed==false`）用 legacy 命令行**覆盖 `item.Command`** ⇒ GUI 列表丢 `裁决=`（**日志行仍在**，属显示层，未修） |
| ~~S4~~ | ❌ 已取消 | 见 §5-B |

**验证**：构建 **0/0**；**13 个专项门禁 + 17 个探针 mode 逐一持平**（探针合计 **379/0**，
contract **107** · wire **37** · engine 32）。**零行为变更**已达成 —— 本轮**不产生** `ProceedWithDegradation`。

> ⚠ **上面这段"本轮"指 S1/S3 那轮**（2026-09-16），其数字是**当时的实测值**，保留以存历史、**不随后续轮次更新**。
> **当前**基线见本节表格与「S2 的收口」（**最新实测，2026-09-19**：探针合计 **525** = **20 个 mode**（`geometry` **52** 条、`verdict` 41 条、`wire` **72** 条；**`runner` 2 → 8**，见下方溯源）；运行器 `_run-step3-gates.ps1` 脚本清单 **48 条**；⚠ **2026-09-19 P4-A 接线 42 → 48（+6）**）。
> ⚠⚠ **运行器条数每加一个门禁脚本就变**（2026-09-18 当天被改了 **九** 次：32→33→34→35→36→37→38→39→40；最新六条 = `verify-anim-static-target.ps1` / `verify-jxl-probe-timeout.ps1` / `verify-ffmpeg-heartbeat.ps1` / `_probe-sync-read-before-wait-scan.ps1` / `verify-jxr-anim-input.ps1` / `_probe-ct-chain-closure-scan.ps1`）⇒ 改 `tests/scripts/**` 后**必须重新数**，**不要拿旧值推算**。
> ✅ **2026-09-18 更正**：本行原写「脚本清单 **34 条**」且带「**当前** / 最新实测」标签 —— 那是**过期数字自称是当前值**（改完没同步）。
> ⚠ **运行器退出码已于 2026-09-18 修复**（此前尾部无 `exit` ⇒ 吞掉子门禁非零退出码）⇒ 现在**可以**用运行器退出码当判据；当天首次全量 37 条全绿 = `EXITCODE=0`。
> ⚠ **溯源**：501 → 519 的 **+18** = `geometry` 35 → 52（**+17**，批次 1「R1 顺序统一」轮）+ `wire` 71 → 72（**+1**，来源未溯源）。批次 1 曾报 518（漏算 `wire` 的 +1），**以 519 为准**。
> ⇒ **2026-09-19 追加（不改上面的溯源链，只在其后追加）**：**519 → 525** 的 **+6** = **`runner` 2 → 8**（#20 心跳判据锁：非 ffmpeg 子进程不再被注入 `-progress pipe:1`，2026-09-19）。
> ⚠ **门禁断言数也是漂移源，同样不得沿用旧值**：`verify-ffmpeg-heartbeat.ps1` 断言数 **62**（2026-09-18 首轮）
> → **64**（2026-09-19 #15-b：心跳改默认开 + 活性收紧为进度块专属，+2）→ **68**（2026-09-19 收尾：+⑤ 二分性
> 与「合法无块窗口实测上界」4 条）。**当前实测 68/0**（pwsh 7.6.6 与 PS5.1 各一次，均 exit 0）；
> 同轮 `verify-ps-compat.ps1` **13/0**、`verify-jxl-probe-timeout.ps1` **48/0**（未回归）。
> 脚本**条数**不变（`verify-ffmpeg-heartbeat.ps1` 仍是清单内**一个**条目 —— 断言数变化不改清单条数；⚠ 清单总数另计：**2026-09-19 P4-A 接线后为 48 条**）。
> ⚠ 测 `ipc` 必须先设 `FFMPEGGUI_FFMPEG_DIR`，否则该 mode 只得 8 ⇒ 总数 523（= 525 − 2）。

### ✅ S2 的收口（2026-09-17 实施；原「语义冲突」已按 §5-D 解决）

**原暂缓原因**：`ColorEngineRouter.BlockReason` 的"拒"是「**引擎不可走 ⇒ 回退传统管线**」，
而 `ColorStrategyPlanning.Reject` 的"拒"是「**任务做不了**」。两者塞进同一个 `VerdictKind.Reject` 会混淆。

**收口**（= §5-D 的裁定，不新增第三个决策面）：保留 `BlockReason` 作**谓词**，新增
**同型但不混用**的 `ColorMapping/EngineBlock.cs`（`EngineBlock { Code, Text, Alternatives }`，
`BlockReason` 返回 `EngineBlock?`，`null` = 可走）。

- **Code 词表（9 条，逐条对应判据，不多不少）**：`OptionsNull` · `FormatNotSupported` ·
  `AnimatedInput` · `ExternalBackend` · `AnimationScaleUnsupported` · `LegacyTonemapCurve` ·
  `GeometryWithGainMap` · `ProgressiveJpeg` · `EncoderUnsupportedSetting`
  （⚠ 与 `DegradationCodes` **故意分家**，即使同名也不得互相替换）。
- **多项同时命中**（`UnconsumedOutputSettings` 那一段会聚合多条）：`Code` 取**列表首项**
  （= `Text` 里第一个原因），顺序即判据的固定顺序 ⇒ 确定性、可断言；全部原因仍逐字留在 `Text` 里。
- **`Text` 逐字未改**（有门禁/日志在匹配它）；**判据与顺序也未改**（含"`backend != Ffmpeg` 那道门
  在**白名单检查之后**"这个已验证的结构）。`UnconsumedOutputSettings` 仍是公开的**字符串**投影，
  真值下移到 `UnconsumedOutputSettingItems`（带 `Code`/`Alternatives`）。
- **`Alternatives` 只填"代码本来就知道"的 5 处**（`ExternalBackend` → `Option/encoder`；
  `AnimationScaleUnsupported` → `Option/animation-scale-w`；`LegacyTonemapCurve` → `Option/color-tone-map`；
  `GeometryWithGainMap` → `Option/max-dimension-enabled`；`ProgressiveJpeg` → `Option/jpeg-progressive`）；
  **留空 4 处**（`OptionsNull`/`FormatNotSupported`/`AnimatedInput`/`EncoderUnsupportedSetting`，
  理由逐条写在 Router 内注释：前者无替代可给、`AnimatedInput` 拿不到输入路径、
  `EncoderUnsupportedSetting` 是三子情形的聚合串且为传统管线共用真值）。
- ⚠ **`Alternatives` 跨所有命中的判据聚合**（不是只给首项那条的替代）。这是 `Code` 能保持**单值**的
  **前提**：lead 裁定"GUI 一键修正要的是**全部可行动替代**"—— 而 `Alternatives` 是列表，正是它承载了
  这个需求（本条即该裁定的落地；裁定同时否掉了"把 `Code` 改成列表"的方向）。单条判据命中时与
  "只给首项"等价，多因命中时才分叉。
- **验证**：`ServiceProbe wire` **58 → 71/0**（新增 13 条：9 个 Code 的正向断言 + 多因取首项 +
  多因 Alternatives 聚合 + Text 逐字锁 + Alternatives 双向 + 代表格式负控）；
  `verify-color-wiring` **99/0** 持平（跑前/跑后脚本 mtime 未变 ⇒ 无并发编辑）。
  变异验证（三条，均已回滚并重建 0/0）：① `ProgressiveJpeg` 判据恒假 ⇒ wire **67/4**；
  ② 可走时也返回非 null ⇒ wire **57/14**（含 7 个代表格式的负控全红）；
  ③ `Alternatives` 只取首项（不聚合）⇒ wire **70/1**。
  ⚠ 既有 `BlockReason` 断言已由"匹配 Text 自由文本"改为**打 `Code`**。

> 原「⚠ S2 的语义冲突（暂缓原因）」小节已由本小节取代（上文保留其结论，实现不再暂缓）。

### 本轮留下的技术债（S5 一并收）

1. **`EnsureAnnotationNotSilentlyMissing` 的兜底 Reject 未走 `Reject(...)` 助手**（内联置
   `p.Action = ColorAction.Reject`）⇒ 该格 `Alternatives` 为空。改造它需动 `AttachIcc`/`Annot` 的赋值顺序，
   与"零行为变更"冲突 ⇒ 留给 S5。
2. **`PlanCore` 内部 2 处内联 Reject**（源不可识别 / 矩阵求不出）同样不走助手 ⇒ `Alternatives` 为空。
3. **`Verdict` 只在 `Plan(ColorIntent)` 产出**：直接调 `ColorMappingEngine.Plan(src, dst, policy)`
   的公开重载路径**没有 `Verdict`**（保持 `null`）。若 S4/S6 要断言"任意入口都有裁决"，这里是缺口。
4. **`Advice` 文案与 `Alternatives` 的一致性已修**（2026-09-16）：三处 `Advice` 原先写 `AVIF/HEIF/JXL`，
   而 `heif` 能力表实测**无写入通路** ⇒ 已把 HEIF 从文案去掉（推荐不可用格式属误导；门禁不匹配该文案，改动安全）。
5. **策略类替代（`AlternativeKind.Strategy`）一律留空**：本轮 §3 P0b 词表尚未落地，不擅自造值域。
   P0b 定案后由 S5 补齐（**GUI 一键修正依赖它**）。

## 附：本方案的证据来源

- `ColorMapping/ColorIntentFactory.cs:41`（面①，`null` + `reason`；2026-09-17 由 `:26` 更正）
- `ColorMapping/ColorEngineRouter.cs:91-116, 153-172`（面②，`string?`；2026-09-17 由 `:50-69, 89-106` 更正）
- `ColorMapping/ColorStrategyPlanning.cs:677`（面③，`Reject`；2026-09-17 由 `:495-505` 更正）
- `ColorMapping/ColorTransformPlan.cs:11`（`ColorAction` 二元终局）、`:63`（`HasColorPath`）
- `Services/FfmpegCommandBuilder.Decision.cs`（`EffectiveColorStrategy` 只取策略、丢弃 `Action`）
- `tests/scripts/verify-color-strategy.ps1:106`（"已知差异"的**登记**而非解决）
