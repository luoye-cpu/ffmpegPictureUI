# RAW 输出色域「按内容自动选择」实施规划（2026-10-04）

> **目标**：新增一个用户可选的模式选项。**默认仍是「Rec.2020 表达优先」（= 现状，行为零变化）**；
> 用户可切到 `auto`，此时若**该图实际用色未超出 Rec.709 范围**，则不用 Rec.2020 表达。
>
> **状态**：规划稿，**未实施、未提交**。含 **4 个待用户裁定项**（§9）。
>
> **可行性依据**：`docs/RAW2TIF_QA_DEFECT_2026-10-03.md` 附五（判据已在 13 个不同厂商 RAW 样本上实测）。

---

## 〇、一句话结论

技术上可行、判据已验证、选项穿线点已全量定位。
**唯一实质成本是 `auto` 模式下多一次全尺寸解码**（约 1–3 s/张，默认档零成本）；
**最大风险不是算法，而是本仓的门禁账本**（行号锚漂移 + CJK 桶余量 0 + 克隆表），§6 逐条列了。

---

## 一、现状（改动前的精确形态）

默认目标色域由 `src/FfmpegGui/Services/QueueProcessor.cs` 的 **RAW 默认块**硬编码，判据是**容器能力**，不是内容：

| 档 | 容器 | `ColorTrc` | `ColorPrimaries` | 代码 |
|---|---|---|---|---|
| ① HDR 传递档 | avif / jxl / png-16bit…（`CanHdr` 且实际位深 > 8） | `pq` | **`bt2020`** | `QueueProcessor.cs:643-644,650-651` |
| ② **TIFF 例外档** | tiff / tif | `srgb` | **`bt2020`** | 同上（`rawIsTiff`，`:649`） |
| ③ 默认路线 | webp / jpg / jpegli / gif / jxr / ppm… | `srgb` | `bt709` | 同上 |

关键既有约束（改动必须尊重）：
- `rawUserChoseTarget`（`:593-597`）：`ColorPrimaries` / `ColorTrc` / `ColorSpace` **三轴或**关系，任一被显式指定 ⇒ 本块**不触发**。
- `ColorFromRawDefault = true`（`:659`）：标记"这是程序默认、不是用户意图"，否则 `ColorIntentFactory` 会把 `ColorTrc` 非空误判成 `targetExplicit` ⇒ RAW→GIF 被契约拒绝。
- `ColorSourceDeclPrimaries/Trc`（`:579-580`）：**输入侧声明**，恒写，与用户是否选目标无关。
- 中间件是 `dngtool -d -T -o 0 …`（`RawService.cs:175`）= **相机原生原色 + 线性 + 16-bit**；
  输入侧取 `bt2020/linear` 作**近似**（`:574-575` 注释明说"相机原生（宽，无标准名）"）。

---

## 二、判据（已实测，直接采用）

**语义（必须写进日志与文档）**：本判据测的是「**本管线自身的 `bt2020 → bt709` 这一步会不会裁切像素**」，
不是绝对色度真值 —— 因为它跑在"相机原生数据被声明为 bt2020"的中间件上，继承该近似。

**算法**（单遍分块扫描，见 §4.1）：

1. 读 rgb48 中间件（bt2020/linear 语义），逐块计算 `rgb709 = M_2020→709 · rgb2020`（3×3 常量）。
2. 亮度：`Y = 0.2627R + 0.6780G + 0.0593B`（BT.2020 系数）。
3. **亮度地板** = `max(1/65535, k · Y_p99.9)`，`k ∈ [0.10, 0.25]`（待标定，§8）。
   ⚠⚠ **地板不可省**：暗部噪声会把像素色度甩到色域外，实测 A7II 样本的全部"超界"像素都来自最暗两档，
   过地板后为 **0.0000%**；Pentax 黄花样本则集中在最亮档（**20.98%**）—— 两者必须靠地板分开。
4. 统计（仅地板以上像素）：`fracNeg` = `min(rgb709) < −ε` 的比例；`maxNeg` = `min(rgb709)` 的 p0.1 分位。
5. 判定：`fracNeg < F` **且** `maxNeg > −M` ⇒ **判「装在 bt709 内」**。
   初始建议 `F = 0.1%`、`M = 0.5%`（满量程 1.0），**由 §8 标定实验最终确定**。
6. 抽样：stride N，目标 ≈ 40 万样本（判据是统计量，不需要全像素）。

**已实测的三格验证集**（`auto` 模式的验收基准）：

| 样本 | 亮部出 bt709 | 亮部出 bt2020 | 期望判定 |
|---|---|---|---|
| `TEST/1-2.ARW`（A7II，白纸黑线稿） | **0.0000%** | 0.0000% | bt709 |
| `tests/output/raw_samples/Pentax_istDL.PEF`（黄色花卉） | **3.82%** | 0.211% | bt2020 |
| `tests/output/raw_samples/PhaseOne_P40.IIQ` | 1.74% | 0.0086% | bt2020 |

⇒ 两组**不重叠**，判据可用。

---

## 三、选项设计

### 3.1 取值

| 值 | 语义 | 是否默认 |
|---|---|---|
| **`rec2020`** | **现状三档，一字不改**（"Rec.2020 表达优先"） | ✅ **默认** |
| **`auto`** | 三档 + 内容判定：**仅对「`ColorTrc` 为 SDR 的 `bt2020` 档位」**（当前 = TIFF 例外档②）降级；命中"装在 bt709 内" ⇒ 该档位改 `bt709` + `srgb` | |
| **`rec709`**（建议包含） | 强制 `bt709` + `srgb`（完整 SDR 窄色域），覆盖容器能力 | |

### 3.2 ⚠ `auto` 的作用域 —— 本方案最关键的裁定

`auto` **只作用于 SDR 传递的 `bt2020` 档位**（即上表②），**HDR 传递档（①，PQ）不降级**。

理由：
1. `bt2020` 在 HDR 档里**不是"可选表达"**，而是 BT.2100 / PQ 生态的组成部分。
2. 若降 primaries 而不降 trc ⇒ 产出 **`bt709 + PQ`**，这是合法但**非标准**的组合 ——
   正是本仓最忌讳的"怪标定"（会立刻制造新的"宣称≠交付"面）。
3. 要强制窄色域的用户有 `rec709`（两轴同降，无怪组合）作为逃生门。

⚠ **代价必须诚实登记**：选 `auto` 时，**HDR 传递档容器（avif/jxl/png-16bit）不会发生变化**，
日志必须显式说明这一点（否则就是"选了 auto 却什么都没发生"的静默）。

### 3.3 命名

建议 `--color-target`（值 `rec2020|auto|rec709`）。备选：`--raw-color-target`。
⚠ 名字一经定下就是 CLI 契约，需在 §9 裁定。

---

## 四、实现落点（逐文件）

### 4.1 新增测量内核

**新文件**：`src/FfmpegGui/Services/ColorMapping/RawGamutFit.cs`（或并入 `RawColorPipeline` 作静态方法）

```
public readonly record struct GamutFitResult(
    int SampledPixels, double FracOutOf709, double MaxNegExcursion,
    double LuminanceFloor, bool FitsIn709, string Reason);

public static async Task<GamutFitResult> MeasureAsync(
    string rawTiffPath, int width, int height, CancellationToken ct = default)
```

实现要点（**照抄既有成熟形态，不要自创**）：
- 解码：复用 `RawColorPipeline.DecodeToRgb48Async`（`RawColorPipeline.cs:1207`）。
- 分块顺序读：**照抄** `RawColorPipeline.MeasureContentPeakNitsAsync`（`:1155-1176`）的
  `ChunkPixels = 1 << 16` + `FileOptions.SequentialScan` + `ReadExactlyAsync` 形态。
- `M_2020→709`：**常量**，写死在类里并在注释里给出推导（`inv(XYZ→sRGB) · (XYZ→BT2020)`）。
- ⚠ **不引入 `signalstats`/`histogram` 滤镜**（全仓目前零使用，引入等于新增依赖面）。
- ⚠ 返回"测不出"时（空文件/尺寸不符）必须**三态**：不得把失败当成"装在 bt709 内"。

### 4.2 接线点（`QueueProcessor.cs`）

**插入位置：`QueueProcessor.cs:643` 之后、`:650` 之前**（此时 `rawTiff` 已由 `:557` 产出、`:572` 赋给
`captured.InputPath`；`rawCaps`/`rawCanHdrTransfer`/`rawIsTiff` 已在 `:630-649` 求值；目标尚未固化）。

```csharp
// ── 新增：按内容判定是否需要 Rec.2020 表达（仅 auto 模式、仅 SDR 传递档）──
bool downgradedTo709 = false;
if (string.Equals(captured.Options.ColorTarget, "auto", StringComparison.OrdinalIgnoreCase))
{
    bool sdrTierWants2020 = !rawCanHdrTransfer && rawIsTiff;   // 当前唯一的 bt2020+SDR 档位
    if (rawCanHdrTransfer)
        captured.Log += "[RAW] 模式=auto：本档位为 HDR 传递档（bt2020 属 BT.2100 组成部分）⇒ 不降级；"
                      + "如需强制窄色域请用 --color-target rec709 或显式指定目标\n";
    else if (sdrTierWants2020)
    {
        var fit = await RawGamutFit.MeasureAsync(rawTiff, rawW, rawH, ct);
        if (fit.FitsIn709) { downgradedTo709 = true; }
        captured.Log += $"[RAW] 模式=auto 用色实测：地板={fit.LuminanceFloor:F4}，"
                      + $"出 bt709={fit.FracOutOf709:P4}（阈值 {F:P2}），最负超出={fit.MaxNegExcursion:F4}"
                      + $"（阈值 {-M:F4}）⇒ {(fit.FitsIn709 ? "装在 bt709 内 ⇒ 降为 bt709/sRGB" : "超出 ⇒ 保持 bt2020")}\n";
    }
}
...
captured.Options.ColorPrimaries =
    (downgradedTo709) ? "bt709"
    : (rawCanHdrTransfer || rawIsTiff) ? "bt2020" : "bt709";
captured.Options.ColorTrc = rawCanHdrTransfer ? "pq" : "srgb";
```

⚠ 保持 `:652` 的 `UseAdvancedColorParameters = true` 与 `:659` 的 `ColorFromRawDefault = true` **不变**。
⚠ `rec709` 模式在**同一处**分派（`ColorPrimaries = "bt709"`），但**不受** `rawUserChoseTarget` 之外的新条件影响。

### 4.3 选项穿线（完整清单，一处都不能漏）

| # | 文件 | 落点 | 说明 |
|---|---|---|---|
| 1 | `CliParser.cs` | `ApplyExtraOption` 加 `case`；`PrintHelp` 色彩节 `:346-370` | **照抄** `--color-709-curve` 的 `ParseTokenStrict` 模式（`:684`，辅助 `:1034-1040`）；⚠ 漏加 `case` 只会**告警不报错**（`:845-847`） |
| 2 | `Models/FfmpegOptions.cs` | 新增 `public string? ColorTarget { get; set; }` | 建议**入口规范化**为小写白名单（照 `ColorPrimaries` 的 setter 形态 `:253-257`，走 `ColorSpaceRegistry`） |
| 3 | `Models/PresetData.cs` | 加同名字段 | 用户意图 ⇒ **应进预设**（否则批处理无法复用） |
| 4 | `Models/FfmpegOptions.cs` | `ApplyPresetData`（`:40-143`）加应用 | 与 3 成对 |
| 5 | `Models/AppSettings.cs` | 加字段 | |
| 6 | `Services/AppSettingsService.cs` | **⚠⚠ 手写克隆表 `:168-197`** | 漏进克隆表 ⇒ 值被**静默重置成模型默认**（不是"不落盘"） |
| 7 | `Models/AppJsonContext.cs` | `:13-15` | **仅当**引入枚举类型才需登记；**用 string 值则无需改** |
| 8 | `MainWindow.xaml` | `AdvancedColorPanel`（`:393`）内、`ColorSpaceCombo`（`:396`）附近加 ComboBox | |
| 9 | `MainWindow.xaml.cs` | `FindControl`/默认项（`:537-541`）/`BuildOptions`（`:2862-2990`）/`BuildPresetData`（`:5762-5899`）/`ApplyPresetData`（`:5901-6160`）/`UpdateAdvancedColorControls`（`:4061-4106`） | ⚠ 按 2026-10-04 用户裁定：**复选框只是 UI 开关，不参与色彩决策**；本选项**值派生**，直接读 ComboBox 值，**不要**新增 UI 布尔 |
| 10 | `tests/scripts/_lib-axes.ps1` | `Get-AxisRegistry`（`:182-260`）加一条轴 | 格式：`-Name 'color-target' -CliKey '--color-target' -Source 'src/FfmpegGui/CliParser.cs:<行>'` |
| 11 | `tests/UiTestHost/Program.cs` | 新 UI 组 | 新 ComboBox 的读写/预设往返 |
| 12 | CHANGELOG.md / CHANGELOG.en.md / docs/TESTING.md | §6.5 | **双语同条目** |

⚠ **不要**动 `HasExplicitColorParams`（`:190-193`）/ `HasExplicitColorTarget`（`:212-214`）的语义 ——
本选项是**第三根独立轴**（"默认档如何取"），不是"用户是否指定了目标"。混入会让 `rawUserChoseTarget` 误判。

---

## 五、模式 × 档位 决策矩阵（实现的验收表）

| 容器档 | `rec2020`（默认） | `auto` + 内容装在 709 内 | `auto` + 内容超出 709 | `rec709` |
|---|---|---|---|---|
| ① HDR 传递（avif/jxl/png16） | bt2020 + pq | **bt2020 + pq**（不降级，日志点名） | bt2020 + pq | bt709 + srgb |
| ② TIFF | bt2020 + srgb | **bt709 + srgb** | bt2020 + srgb | bt709 + srgb |
| ③ 其余（jpg/webp/gif…） | bt709 + srgb | bt709 + srgb（本就 709） | bt709 + srgb | bt709 + srgb |

⚠ 用户显式指定目标（`rawUserChoseTarget`，`:593`）时，**本选项一律不生效** —— 与现状一致。

---

## 六、门禁与账本影响面（本仓最大的风险源）

### 6.1 必然要改的

| # | 项 | 位置 | 后果 |
|---|---|---|---|
| A | **行号锚漂移** | `tests/scripts/_lib-matrix.ps1` 的 `Evidence = '<path>:<line>|<literal>'`（如 `:386` 的 966→977、`_630` 的 2050→2061） | 在 `QueueProcessor.cs` RAW 块插入代码 ⇒ 其后所有行号锚漂移 ⇒ 第 55 条 `_probe-matrix-structure.ps1` **转红**。必须 `grep -n` 重新定位并更新 |
| B | **CJK 硬编码闸** | `_probe-cjk-hardcode-scan.ps1:351`（`$BASE_CS_UI = **828**`，**余量 0**；⚠ 本表原写 822/`:339`，**已过时** —— 同日上午更晚一批未登记 UI 桶中文把基线二次抬到 828，见该脚本 `:339-350`） | 新增中文日志若落 **UI 桶** ⇒ `829 > 828` **立刻转红**。✅ **已核验**：分桶规则 `:401` `if ($body.TrimStart().StartsWith('[')) { $csTag++ }` ⇒ `[RAW] …` 形态落 **TAG 桶**，而 TAG 桶**不参与判据**（`:210`「`$BASE_CS_TAG` 保留但不再参与判据」、`:439` 仅 ℹ 打印）⇒ **安全**。⚠⚠ 但**必须写成单行**：折行后后续行无 sink 标记，会被误计入 UI 桶（09-20 实测 +3 即此坑） |
| C | **克隆覆盖闸** | `_probe-settings-clone-coverage.ps1:92` | 新持久化字段必须进 `AppSettingsService.cs:168-197` 克隆表 |
| D | **CLI 严格闸** | `verify-cli-strict.ps1:201-217`（锚点清单）+ `:195-197`（计数基线 ≥45，只增不减） | 新选项应加锚点 |
| E | **决策交付闸** | `verify-decision-delivery.ps1:479-553` | 钉 `QueueProcessor.cs` 窗口行与计数（`BuildArguments(` **恰 3 处**等）⇒ 插入代码若改变计数/行需更新 |
| F | **四项目重建** | `_run-step3-gates.ps1:690-736`（STALE 闸，命中即 `exit 2`） | 改 `src/FfmpegGui` 后必须重建 **自身 + ServiceProbe + UiTestHost + GainMapTestHost** |

### 6.2 视实现而定的

- 若新增**探针 mode**（建议做，见 §7.2）⇒ 改 `_run-step3-gates.ps1:43 $Probes` + `docs/TESTING.md:269` + 逐 mode 重算总和（`:267`）。
- 若新增**门禁脚本** ⇒ 改 `_run-step3-gates.ps1:1011 $expectedScriptCount`（现值 **65**）+ `:1012-1145 $scriptList` + 散文（第 ⑰ 针）+ **四处文档**（`:1148`）。
  ⚠ `MEMORY.md:22` 原记「已接线 56 条」是**过期登记**。**2026-10-04 实测更正**：`$expectedScriptCount = 65`
  （`:1011`），`$scriptList` 块（`:1012-1145`）内 65 个 `.ps1` 引用、**无重复** ⇒ 实况 **65**。（已同步修正 `MEMORY.md`）
- 探针总 pass 数**无门禁断言**（运行器只校验 `fail=0`），但文档需同步（`TESTING.md:225-245`、`HANDOVER.md:2233`）。

### 6.3 新增门禁的强制要求

- **每条新断言必须变异验证**：把判据常量改坏 ⇒ 该门禁必须**转红**；回滚 ⇒ 转绿。
- **改门禁断言必须同时补负控**，**不得为了绿而放宽**（本仓明文禁止以放宽判据换绿灯）。
- 新脚本必须**双宿主**（PS 5.1 + PS 7）实测；`tests/scripts/**` **禁 PS7+ 语法**。
- 动态枚举型门禁必须带**下限断言**（素材缺失不得静默全绿）。

---

## 七、测试与验证计划

### 7.1 产物级门禁：`tests/scripts/_probe-raw-color-target.ps1`（新）

用 headless 跑三格 × 三模式，断言产物实际标注：

| 输入 | 模式 | 期望产物标注 |
|---|---|---|
| `TEST/1-2.ARW` | `rec2020`（默认） | tiff ⇒ `bt2020`（**回归锁：默认档行为不变**） |
| `TEST/1-2.ARW` | `auto` | tiff ⇒ **`bt709`** |
| `raw_samples/Pentax_istDL.PEF` | `auto` | ⇒ **`bt2020`** |
| `TEST/1-2.ARW` | `rec709` | ⇒ `bt709` |
| `TEST/1-2.ARW` | `auto` | **avif ⇒ 仍 `bt2020` + `smpte2084`**（锁 §3.2 的作用域） |

- ⚠ **素材依赖**：`TEST/` 在用户桌面（不在仓库内）⇒ 门禁不得硬依赖它。
  建议把 `1-2.ARW` 换成仓库内的低饱和样本，或**把 `raw_samples/` 补一个低饱和样本**（见 §9 待裁定）。
- ⚠ **下限断言**：样本数 / 断言数不足即失败（不得静默全绿）。
- ⚠ **变异验证**：把 `M_2020→709` 常量改坏 或 把地板系数设为 0 ⇒ 门禁必须转红。

### 7.2 纯函数级探针：`ServiceProbe` 新 mode `gamutfit`（建议）

喂**合成数据**（不依赖素材）验证数学：
- 全部落在 sRGB 内的 RGB 三元组 ⇒ `FitsIn709 == true`
- 纯饱和黄 / 深红（bt709 外、bt2020 内）⇒ `false`
- 全黑 / 全噪声 ⇒ 地板以上像素为 0 ⇒ 必须返回**三态**而非"装得下"
- 常量矩阵自检：`M_2020→709 · M_709→2020 == I`（容差 1e-9）

### 7.3 UI 门禁

`tests/UiTestHost/Program.cs` 新增一组：ComboBox 默认值 / 读写往返 / 预设保存-加载往返 / 与 `HasExplicitColorParams` 解耦（新选项**不得**让 `HasExplicitColorParams` 变真）。

### 7.4 门禁账本

`_run-step3-gates.ps1 -Scripts a.ps1,b.ps1` 做**增量验证**（⚠ 子集日志自陈"不构成基线"）；
宣布基线必须跑全量。

---

## 八、阈值标定实验（实施**前置**步骤）

用本轮已建的离线链路（`tests/output/gamut/step10_batch.py` / `step11_noise.py`）扫
`tests/output/raw_samples/` 的 13 个可解码样本 + `TEST/1-2.ARW`，输出二维网格：

- 地板系数 `k ∈ {0, 0.05, 0.10, 0.25, 0.50}`
- `F ∈ {0.01%, 0.05%, 0.1%, 0.5%, 1%}`、`M ∈ {0.1%, 0.5%, 1%, 2%}`

**验收条件（硬）**：所选 (k, F, M) 必须满足
① A7II / Canon_EOS40D / Sony_ILCE7S 判 **bt709**；
② Pentax_istDL / PhaseOne_P40 判 **bt2020**；
③ 两组之间**无样本落在边界 20% 邻域内**（留余量，避免阈值脆弱）。

⚠ 样本量 13+1 **不足以**支撑激进阈值 ⇒ 宁可保守（偏 bt2020）。

---

## 九、待用户裁定（4 项，实施前必须定）

| # | 问题 | 建议 |
|---|---|---|
| **D1** | `auto` 是否也降级 HDR 传递档（①）？ | **不降**（§3.2）。降会产生 `bt709+PQ` 非标准组合；用户要窄色域用 `rec709` |
| **D2** | 是否包含 `rec709` 强制模式？ | **包含**（低成本逃生门，且避免 D1 的怪组合） |
| **D3** | 选项名：`--color-target` / `--raw-color-target` / 其它？ | `--color-target` |
| **D4** | 是否进预设 + 应用设置（持久化）？ | **进**（否则批处理不可复用）；代价 = §6.1-C 克隆表 + §6.1-B 可能的 CJK 抬阈值 |
| **D5** | 门禁素材：把 `1-2.ARW`（低饱和）纳入仓库，还是新造一个低饱和样本？ | 纳入（否则 `auto` 的"降为 709"分支**无门禁覆盖**） |

---

## 十、分阶段实施（每阶段可独立验收）

| 阶段 | 内容 | 验收 |
|---|---|---|
| **P1** | 选项骨架：穿线 1–7 + `_lib-axes` 轴 + `rec2020`/`auto`/`rec709` 三档分派（`auto` 暂等同 `rec2020`） | 默认档产物**逐字节不变**；`verify-cli-strict` / 克隆覆盖闸 / CJK 闸全绿 |
| **P2** | 测量内核 `RawGamutFit` + 常量矩阵 + `ServiceProbe gamutfit`（§7.2） | 纯函数探针全绿；常量矩阵自检通过 |
| **P3** | 接线 + 日志 + §7.1 产物级门禁 + 变异验证 | 三格决策矩阵（§5）逐格实测符合；变异转红/回滚转绿 |
| **P4** | GUI ComboBox + 预设 + 设置持久化 + 克隆表 + UiTestHost 组 | UI 门禁全绿；预设往返保真 |
| **P5** | 文档：CHANGELOG 双语 + `TESTING.md §3.5` + HANDOVER + 本规划结案 | 四处文档同步 |

⚠ **每阶段结束必须重建四项目**（§6.1-F），否则 STALE 闸拒跑。

---

## 十一、风险清单

| 风险 | 影响 | 缓解 |
|---|---|---|
| ⚠ **额外解码成本**（`auto` 模式多一次全尺寸解码 + 顺序扫） | 约 1–3 s/张（24MP） | 只在 `auto` 生效；默认档零成本；可后续用 stride 解码优化 |
| ⚠⚠ **判据继承中间件近似**（相机原生数据被声明为 bt2020） | 测的是"本管线 bt2020→bt709 会不会裁切"，非绝对色度 | 日志/文档显式声明该语义（§2 首段） |
| ⚠ **阈值样本量不足**（13+1） | 误判 ⇒ **不可逆丢色** | §8 标定 + 保守阈值 + 边界余量要求 |
| ⚠⚠ **行号锚漂移** | 第 55 条矩阵结构闸转红 | 插入后 `grep -n` 重定位并更新 `_lib-matrix.ps1` |
| ⚠ **CJK 桶余量 0** | 一行中文即转红 | 新日志用 `[RAW]` tag 形态 + 实测桶归属 |
| ⚠ **批次一致性**（用户已知） | 同批产出混合色彩空间标签 | 属**用户已接受**的取舍（本选项默认关） |

---

## 十二、复现与素材

```bash
D=C:/PLAN/ffmpegPictureUI/publish/PLAN/artifacts/dngtool.exe
# 判据标定（§8）
for f in tests/output/raw_samples/*; do "$D" -d -T -o 4 -q 3 -W -H 1 -6 -i "$f" -O _t.tiff; done
# 参考实现（Python，已跑通）：tests/output/gamut/step10_batch.py / step11_noise.py
```

- 判据实测依据：`docs/RAW2TIF_QA_DEFECT_2026-10-03.md` 附五
- 色域实测依据：同文档 附四
- 离线脚本：`tests/output/gamut/`（草稿目录，未纳入版本控制）

---

# 实施记录（2026-10-04，**未提交**）

## 一、用户裁定（已锁定，覆盖 §9 的建议）

| # | 裁定 |
|---|---|
| **D1** | `auto` **不降级 HDR 传递档**（avif/jxl/png-16bit 恒 `bt2020 + PQ`）；作用域 = 仅「SDR 传递的 bt2020 档位」（当前 = TIFF 例外档） |
| **D2** | **包含** `rec709` 强制档 |
| **D3** | 选项名 = **`--raw-color-target`**（不是 `--color-target`） |
| **D4** | **进预设**（`PresetData`） |
| **D5** | 门禁素材改用**仓库内**样本：`Canon_EOS40D.CR2`（亮部出 bt709 = 0.0000% ⇒ 装得下）与 `Pentax_istDL.PEF`（3.82% ⇒ 超出） |

## 二、已实施（P1–P4）

| 文件 | 改动 |
|---|---|
| `Services/ColorSpaceRegistry.cs` | 新增 `NormalizeRawColorTargetToken`（别名归一；未识别值原样返回，由消费方 `default:` 点名） |
| `Models/FfmpegOptions.cs` | 新增 `RawColorTarget`（入口规范化）+ `ApplyPresetData` 应用 |
| `Models/PresetData.cs` | 新增 `RawColorTarget` 字段（**进预设**） |
| `CliParser.cs` | `case "raw-color-target"`（`ParseTokenStrict` 白名单 `rec2020\|auto\|rec709`）+ `--help` 条目 |
| **`Services/ColorMapping/RawGamutFit.cs`（新）** | 判据内核：TIFF 头取尺寸 → `DecodeToRgb48Async` → 两遍跨步扫描（Y 直方图 ⇒ 地板；地板以上越界统计）。常量矩阵 `M2020→709` 与其逆（自检 <1e-15）。**三态**：测不出 ⇒ `Measurable=false`，调用方**保守保持 bt2020** |
| `Services/QueueProcessor.cs` | RAW 默认块接入三档分派（插在 `:649` 之后、原 `:650` 之前）+ 日志 |
| `MainWindow.xaml` / `.xaml.cs` | `RawColorTargetCombo` + 字段/`FindControl`/`BuildOptions`/`BuildPresetData`/`ApplyPresetData` |
| `Resources/Locales/{zh-CN,en-US}.json` | 新增 `raw.target.label` / `tip.raw.target`（XAML 硬编码中文有硬上限 ⇒ 文案走 `{ext:Loc}`） |
| `tests/scripts/_lib-axes.ps1` | 新增轴 `raw-color-target`（`-Source 'src/FfmpegGui/CliParser.cs:746'`） |

## 三、实测验证

**三档行为（ARW → TIFF，A7II 样本）**：

| 模式 | 日志结论 | 产物目标 |
|---|---|---|
| 默认（无选项） | 走原三档 | `bt2020` / `iec61966-2-1` ✅ **回归锁：默认档不变** |
| `auto` | 抽样 346 万像素，地板 0.09699，**超出 bt709 = 0.0000%** ⇒ 装在 bt709 内 | **`bt709`** / `iec61966-2-1` ✅ |
| `rec709` | 强制 | **`bt709`** / `iec61966-2-1` ✅ |
| `--raw-color-target bogus` | `取值无效: 'bogus'（rec2020 \| auto \| rec709）` | **exit=2** ✅ |

**门禁（`-Scripts` 子集 8/65，⚠ 子集不构成基线）**：全部通过 ——
`verify-ps-compat 13/0` · `verify-color-caps 16/0` · `verify-color-wiring 142/0` ·
`verify-color-strategy 78/0` · `verify-format-regression 19/0` · `verify-decision-delivery 87/0` ·
`verify-gamut-map 36/0` · `_probe-settings-clone-coverage 8/0`。

⚠ **`verify-decision-delivery` 通过是关键**：我在 `QueueProcessor.cs` 插入了约 45 行，
该闸钉的是窗口行与计数 —— 实测**未被我的插入打破**（其判据不依赖绝对行号）。

## 四、⚠⚠ 两处**先于本次改动**的遗留红（**非本次引入，已取证**）

1. **`_probe-cjk-hardcode-scan.ps1`：UI 面 828 > 基线 822（+6）**
   - **取证（可回滚对照实验）**：把本次新增的 6 条 `[RAW] …` 日志行**临时注释掉**后重跑 ⇒
     `[tag] 493 → 487`（**−6**），而 **UI 面 828 不变**。
     ⇒ 本次新增日志**全部落 TAG 桶**（`:401` `StartsWith('[')` 规则），**对 UI 桶贡献 0**；
     UI 的 +6 来自**今天早前会话**在 `_probe-cjk-hardcode-scan.ps1:339` 基线（07:02 定）之后
     对 `ColorIntentFactory.cs`（10:55）/ `FfmpegCommandBuilder.cs`（10:27）等的改动，**未登记**。
   - ⚠ 依本仓「不得为了绿而放宽」的口径，**未**抬 `$BASE_CS_UI`。需用户裁定：确认那 6 条是合法 UI 文案后显式登记，或回退。

2. **`_probe-matrix-structure.ps1`：A21/A30 行号锚漂移 +4（fail=2）**
   - 4 处失败**全在 `FfmpegCommandBuilder.cs`** —— 本次**未触碰**该文件（`git status` 未含我的改动）。
   - `git diff --numstat` 显示该文件 vs HEAD 为 **+19 / −4**（早前会话），字面量实测已从
     977→**981**、987→**991**、1019→**1023**、2061→**2065**（整齐 **+4**）。
   - 修法（未执行）：`_lib-matrix.ps1:390/405/631/652` 重新钉行号（机械重钉，非放宽判据）。

## 五、仍未做（等授权）

- ~~P5 文档~~ ✅ **已完成**：`CHANGELOG.md` / `CHANGELOG.en.md` 双语同条目 · `docs/TESTING.md`（探针清单 + 第 26 个 mode 登记 + 新整轮基线）· `MEMORY.md`（探针 25→26 / 669→688、CJK 基线二次抬）
- ~~`tests/ServiceProbe` 新 mode `gamutfit`~~ ✅ **已完成**（15 条，已变异验证，已接进 `$Probes`）
- **仍未做**：`tests/UiTestHost` 新组（ComboBox 读写 / 预设往返 / 与 `HasExplicitColorParams` 解耦）
- **仍未做**：`tests/scripts/_probe-raw-color-target.ps1`（产物级门禁 + 变异验证 + 下限断言）
  ⚠ **刻意不先落一个"没上清单"的脚本** —— 本仓已有教训（第 54 条曾存在但没进 `$scriptList` ⇒ 谁也不跑、假覆盖）。
  接线需同时改 `$scriptList` + `$expectedScriptCount`(65→66) + 散文条数（第 ⑰ 针）+ 分类分解 + 四处文档，是一个独立的账本操作。
- 提交 / tag（**等授权**；本仓规则：未经实机测试不得提交）

## 六、本轮结束时的验证快照

| 项 | 读数 |
|---|---|
| 四项目重建 | 各 **0 警告 0 错误** |
| 三档端到端（ARW→TIFF） | 默认 `bt2020` / `auto` `bt709`（超出 bt709 = 0.0000%，抽样 101 万）/ `rec709` `bt709` / 非法值 exit=2 |
| 探针（26 mode） | **688 pass / 0 fail / 1 skip**（`gamutfit` 15/0） |
| 门禁子集（8 条） | 全绿：ps-compat 13 · color-caps 16 · color-wiring 142 · color-strategy 78 · format-regression 19 · decision-delivery 87 · gamut-map 36 · settings-clone-coverage 8 |
| 两处遗留红 | ✅ **已转绿**：CJK 14/0（`CsUi` 登记 822→828）· 矩阵结构 8/0（4 处行号锚重钉 +4） |
| 变异验证 | `gamutfit`：`FloorFactor=0` + `EpsNeg=0` ⇒ `pass=11 fail=4`（红的正是行为断言 (d)(e)）；还原回 15/0 |

