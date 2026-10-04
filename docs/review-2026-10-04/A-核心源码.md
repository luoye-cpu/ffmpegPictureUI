# A 线 · 核心产品源码复查（2026-10-04）

审查对象：HEAD `bdd4b0b` 之后**工作区未提交**的 6 个产品源码文件
（`FfmpegOptions.cs` +13 / `ColorIntentFactory.cs` +34 / `ImageEncoderArgs.cs` +6 /
`FfmpegCommandBuilder.cs` +11 / `IsoBmffGainMapWriter.cs` +47 / `QueueProcessor.cs` +82−?）。

铁律执行：未改动 `src/`、`tests/`、`docs/` 下任何既有文件，未 `git add`/`git commit`；
"改坏验证"全部在 `/tmp/rv`（源码副本）与 `/tmp/rvout`（产物）中进行。
复查前后 `git status --short` 一致（20 项 M + 2 项 ??）。

---

## 〇、结论速览

| 级别 | 条目 | 一句话 | 证据 |
|---|---|---|---|
| **P1-1** | `rawUserChoseTarget` **丢维**：非高级模式的 `ColorPrimaries/ColorTrc` 不再算"用户已选" | §7.6 补了 `ColorSpace` 一维，却**删掉了旧判据原有的** `ColorPrimaries/ColorTrc` 一维 ⇒ CLI `--color-primaries/--color-trc` 的显式目标被 RAW 默认块**静默覆盖** | 端到端实测：要求 bt709，交付 bt2020；两产物 MD5 不同 |
| **P1-2** | CS8602"清零"的**根因是错的**，修法不健康 | 5 条告警是**本轮自己造的**（`QueueProcessor.cs:606` 的 `?? ""`）；13 处 `!` 里 **8 处是无效改动**；§7.5"病因尚未查明"结论不成立 | 三重建造对照实验：HEAD=0 警告 / 去 `!`=5 警告 / 去掉 `?? ""`=**0 警告** |
| **P1-3** | 置信度"**恰好等于**闸门阈值"确有其事 | `CicpTag=2` == `MinConfidenceForMapping=2`，靠 `>=` 才成立；枚举任一调整即静默翻转 | 枚举 + 比较点代码核对 |
| P2-4 | `rawFmtTok` 归一化是**第二份**口径 | 与 `ColorTransformPlan.cs:617`（`CapsFor` 内）逐字重复 | 代码比对 |
| P2-5 | `ReadNclxFullRange` 取"最后一个字节" | 应用固定偏移 `[18]`；盒长 >19 会读错字节 | 代码 + 盒结构推算 |
| P2-6 | 新源描述符分支存在**静默 fail-open** | `FromCicp` 返回 null 时不点名、不日志，直接退回老路 ⇒ 又是"直通" | 代码 |
| P2-7 | `rawCaps != null` 是**死判据** | `CapsFor` 对未知格式返回保守 caps，**永不返回 null** | 代码 |
| P2-8 | 注释口径与实现不符 | "由 `RawService`/`QueueProcessor` 在预处理时写入" —— 全仓只有 `QueueProcessor` 写 | 全仓 grep |
| P2-9 | `BuildColorArgsSplit` 新分支用 `\|\|` | 只设 Primaries 时返回 `trc=null`；RAW 路径当前恒成对设置 ⇒ 潜在，未触发 | 代码 |
| ✅ | **P0-2（AVIF 增益图 full_range）真修好了** | 端到端实测产物主图 colr 末字节 `0x00`；变异验证探针转红（5 FAIL） | 实跑 `ServiceProbe avifgm` |
| ✅ | **P1-3（JXL distance 注释失真）真修到位** | 注释改为"近似互逆"+ 锚点移到**非互逆点 q75** 并钉死 `rt75==74` | 代码 + 计算复核 |
| ✅ | **P1-4（dngtool 旧公式）已登记** | `JXL_RAW_16BIT_ADVISORY` 新增 G 行，明写"刻意未动"+ 取证出处 | diff |
| ✅ | 三档目标实测正确 | avif→bt2020/pq、tiff\|tif→bt2020/srgb、webp→bt709/srgb | headless 实跑 |
| ✅ | `ColorSourceDecl*` 确实不进 `PresetData` | `PresetData` 无此字段；`ApplyPresetData` 不写它；GUI/CLI 各自 new/DeepClone | 代码 + 全仓 grep |
| ✅ | tmap colr 回填路径打通 | `Build():99` 回填 → `:130` 使用；实测 tmap fr=0x00 | 代码 + 实测 |

**未发现 P0。** 上轮 P0-2 是本批改动里唯一一条 P0，已实测闭环。

---

## 1. P1-1｜`rawUserChoseTarget` 丢维：用户显式目标被静默覆盖

`src/FfmpegGui/Services/QueueProcessor.cs:576-580`

```csharp
bool rawUserChoseTarget = captured.Options.UseAdvancedColorParameters
    ? (!string.IsNullOrWhiteSpace(captured.Options.ColorPrimaries)
       || !string.IsNullOrWhiteSpace(captured.Options.ColorTrc))
    : (!string.IsNullOrWhiteSpace(captured.Options.ColorSpace)
       && !captured.Options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase));
```

### 1.1 与旧判据的差集（代码可证）

| | 旧判据（HEAD） | 新判据 |
|---|---|---|
| 高级模式 | `CP` 或 `CT` 非空 ⇒ 算用户已选 | 同 |
| **非高级模式** | `CP` 空 **且** `CT` 空 ⇒ 才算未选（即 **CP/CT 任一非空即算已选**） | **只看 `ColorSpace`**，`CP/CT` 完全不参与 |

⇒ §7.6 的修法是"补一维"，但补的同时**把另一维删掉了**。净效果：非高级模式下的显式 `CP/CT` 从"被尊重"变成"被覆盖"。

### 1.2 可达性（不是死路径）

- `CliParser.cs:650-651`：`--color-primaries` / `--color-trc` **只赋值，不置 `UseAdvancedColorParameters`**；
  `--help:342-343` 明确宣传这两个开关 ⇒ 是**对外承诺的 CLI 面**。
- `FfmpegOptions.ApplyPresetData:47-49`：`UseAdvancedColorParameters = p.UseAdvancedColor` 与 `ColorPrimaries/ColorTrc` 三条**各自独立** ⇒ 预设也能造出 `UseAdvancedColor=false + CP/CT 非空`。
- （GUI 不可达：`MainWindow.xaml.cs:2875-2876` 只在 `useAdv` 时写 CP/CT。）

### 1.3 实测（端到端，真 dngtool + 真 DNG 输入）

用仓内 `publish/PLAN/artifacts/dngtool.exe`，输入取 `tests/output/audit-jxr-dng/c_cfa.dng`
（`.dng` 在 `RawService.RawExtensions` 内），跑**当前工作区**构建的 `FfmpegGui.exe --headless`：

```
A) -f tiff --color-primaries bt709 --color-trc srgb
   [RAW] 未指定高级色彩参数 ⇒ 输入声明 bt2020/linear；输出目标 bt2020/iec61966-2-1
                                                          ↑ 用户要 bt709，被覆盖成 bt2020
B) -f tiff --color-space sRGB
   [RAW] 用户已指定目标色域（sRGB）⇒ 保留用户配置        ↑ §7.6 修的那条，正确
```

产物对比（同一输入、同一容器，只差用户表达方式）：

```
A/in.tiff  15,244,828 B  md5 1ac894b278ee020e64f8225ad7f65e6e
B/in.tiff  15,241,086 B  md5 bc923cda4d4c50bdcc9d818d39d9170d   ← 差 3,742 B
```

⇒ 不是"日志措辞问题"，是**交付物真的不同**：用户点名要的 bt709 原色没拿到，还拿不到任何提示。
这正是 §7.6 自己判定的缺陷形状（"违背用户选了就用用户的"）。

### 1.4 建议

判据与"是否高级模式"**无关**，三个轴应并列：

```csharp
bool rawUserChoseTarget =
    !string.IsNullOrWhiteSpace(captured.Options.ColorPrimaries)
 || !string.IsNullOrWhiteSpace(captured.Options.ColorTrc)
 || (!string.IsNullOrWhiteSpace(captured.Options.ColorSpace)
     && !captured.Options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase));
```

并补一条探针：CLI 三轴（`-space` / `-primaries` / `-trc`）分别给值 ⇒ 日志必须出现"用户已指定目标色域"，且 `ColorPrimaries` 不被改写。

---

## 2. P1-2｜CS8602 "5 条 → 0 条"的修法不健康（根因已查实）

§7.5 自陈："`FfmpegOptions.Format` 声明看着非空却被分析器判可能为空，原因尚未查明"，
并在 **13 处**加 `!`。实测结论：**原因可以查明，且是本轮改动自己引入的。**

### 2.1 三次建造对照（全部在 `/tmp/rv` 副本上，`--no-incremental` 全量重编）

| 实验 | 源码状态 | 结果 |
|---|---|---|
| E0 | 6 个文件全部回退到 `bdd4b0b`（`git show`） | **0 警告 0 错误** |
| E1 | 工作区版本，**去掉全部 13 个 `!`** | **5 条 CS8602**（`QueueProcessor.cs` 749 / 762 / 779 / 785 / 1040） |
| E2 | E1 基础上，只把 `:606` 的 `?? ""` 去掉 | **0 警告 0 错误** |

E2 的具体改动只有一行：

```csharp
- var rawFmtTok = (captured.Options.Format ?? "").Trim().TrimStart('.').ToLowerInvariant();
+ var rawFmtTok = captured.Options.Format.Trim().TrimStart('.').ToLowerInvariant();
```

### 2.2 根因

`FfmpegOptions.Format` 声明为 `public string Format { get; set; } = "jpg";`（`FfmpegOptions.cs:145`），
`QueueItem.Options` 为 `new FfmpegOptions()`（`QueueItem.cs:11`）⇒ **本来就是非空**，分析器从未凭声明判它为空。

真正的触发点是本轮新增的 `QueueProcessor.cs:606` 里的 **`?? ""`**：
Roslyn 见到 `x ?? ""` 会**反推**"`x` 可能为空"，并把该 maybe-null 状态沿同一方法流**向后传播**，
于是 `:606` 之后所有 `captured.Options.Format.<解引用>` 全部报 CS8602。

用插值探针二分确认了翻转点（在 `/tmp/rv` 插入 `_ = captured.Options.Format.Length;`）：

```
:540  P1  → 不告警（not-null）
:561  P2  → 不告警
:571  P3  → 不告警          ← 都在 :606 之前
:601  Q1  → 不告警
:610  Q2  → ★告警           ← 紧跟 `rawFmtTok = (… ?? "")` 之后
（后续各点因 Q2 处"解引用后推断非空"而不再告警）
```

同一形状在本文件早已有先例（`:1570`、`:2920`、`:3665`、`:3863` 都是 `(…Format ?? "")`），
只是它们之后的解引用点少，才没暴露。

### 2.3 判定

- **不是"掩盖真实空引用路径"**：`Format` 确实不可能为 null，`!` 不产 IL，运行期零改变 —— 这点作者说得对。
- **但修法仍应判 P1**，三条理由：
  1. **自造告警再用 `!` 压掉**：真正该修的是 `:606` 那行（或复用 `rawCaps.Format`），不是 13 个使用点。
  2. **13 处里 8 处是无效改动**（`:408/:468/:476/:489/:527/:786/:1715/:1716` 从未告警），污染 diff，
     并给后人留下"这些点有空风险"的错误暗示。
  3. **§7.5 的根因结论写错了**（"尚未查明"），会误导后续会话继续沿着"分析器为何判 Format 空"去查。

### 2.4 建议（最小改动）

```csharp
// CapsFor 已做同一套归一化（ColorTransformPlan.cs:617），且 FormatColorCaps 自带 Format 字段
bool rawIsTiff = rawCaps.Format is "tiff" or "tif";
```

这一行同时消掉 P1-2（不再需要任何 `!`）与 P2-4（不再写第二份归一化），
然后把 13 处 `!` **全部撤掉**（留着只会掩盖下一次真实告警）。

---

## 3. P1-3｜置信度"恰好等于"闸门阈值（已核实，属设计脆弱）

作者自陈："`FromCicp` ⇒ 置信度 `CicpTag` **恰好等于**闸门阈值 `MinConfidenceForMapping` ⇒ 放行映射"。逐条核对：

```
ColorIntent.cs:15      CicpTag = 2
ColorIntent.cs:148     MinConfidenceForMapping = DescriptorConfidence.CicpTag   // = 2
ColorSpaceDescriptor.cs:90   FromCicp(...) ⇒ Confidence = DescriptorConfidence.CicpTag   // = 2
ColorStrategyPlanning.cs:504/514/544   src.Confidence >= MinConfidenceForMapping    // 2 >= 2 ⇒ true
ColorStrategyPlanning.cs:718           src.Confidence <  MinConfidenceForMapping    // 2 <  2 ⇒ false
```

⇒ **成立，但完全靠 `>=` 而不是 `>`**。即：只要有人 (a) 在 `CicpTag` 之上再插一个枚举值（`CodecDefault` 与 `CicpTag` 之间任一处），
或 (b) 把 `MinConfidenceForMapping` 抬到 `NamedIcc`，这条 RAW 路径就会**零告警地退回"直通"**——
也就是本轮花大力气修掉的那个缺陷，且**没有任何判据会红**（现有 25 个探针都不覆盖"枚举序"）。

### 建议

1. 在 `ColorIntentFactory.cs:86` 显式注明依赖，并**显式抬高一档**：RAW 中间件是我们自己产出的文件，
   语义确定性高于"读到的容器标签"，理应给 `NamedIcc`（或直接给 `IccMetric`），而不是停在阈值线上。
2. 加一条**枚举序锁**：`MinConfidenceForMapping < DescriptorConfidence.NamedIcc` 且
   `FromCicp(...).Confidence >= MinConfidenceForMapping` 的单元断言，让"枚举被重排"这件事转红。
3. 至少补注释：`// ⚠ 本路径依赖 CicpTag >= MinConfidenceForMapping；改枚举序必须先改这里。`

---

## 4. P2-4｜`rawFmtTok` 是第二份归一化口径

`QueueProcessor.cs:606` 写的 `(… ?? "").Trim().TrimStart('.').ToLowerInvariant()`
与 `ColorTransformPlan.cs:617`（`CapsFor` 内部）**逐字相同**。本仓反复踩的坑型就是"同一份口径多处各写一遍"。
修法见 §2.4（直接用 `rawCaps.Format`）。

## 5. P2-5｜`ReadNclxFullRange` 读"最后一个字节"不稳

`IsoBmffGainMapWriter.cs:294`（新增）：

```csharp
if (colrBox == null || colrBox.Length < 8 + 11) return null;
if (Encoding.ASCII.GetString(colrBox, 8, 4) != "nclx") return null;
return (colrBox[colrBox.Length - 1] & 0x80) != 0;
```

边界本身是对的（盒长 <19 ⇒ null、非 nclx ⇒ null、越界读已用 `Length>=19` 挡住），
但 **`Length - 1` 假定"盒恰好 19 字节"**。colr 不是 FullBox，nclx 内容固定 11 字节，
正常写入方确实是 19；一旦遇到 20 字节以上的 colr（padding / 非规范写出器），就会把**最后一个字节**当 range 位读。
改成固定偏移更稳：

```csharp
return (colrBox[8 + 4 + 6] & 0x80) != 0;   // 8 头 + 4 colour_type + 2+2+2 = 18
```

关于"多个 colr 盒"：`PatchColr` 只处理**第一个** colr 并 `return fr`，其余 colr 原样保留（旧代码同形，非本轮新增）。
`fr` 初值取兜底、被第一个 nclx 型 colr 覆盖；第一个 colr 若是 ICC 型（`own=null`）⇒ 用兜底并**整体重写成 nclx**
（丢弃 ICC 引用，旧代码同形）。建议：若底图带 ICC 型 colr，现在的"改写成 nclx"值得单独点名/日志，别静默。

## 6. P2-6｜新源描述符分支静默 fail-open

`ColorIntentFactory.cs:84-89`：

```csharp
if (src == null && !string.IsNullOrWhiteSpace(o.ColorSourceDeclTrc))
{
    src = ColorSpaceDescriptor.FromCicp(o.ColorSourceDeclPrimaries, o.ColorSourceDeclTrc, "RAW 中间件…");
    if (src != null) FixHdrNits(src);
}
```

`FromCicp` 在 **primaries / trc / colorants 任一不认识时返回 null**（`ColorSpaceDescriptor.cs:73-81`）。
当前 `QueueProcessor` 恒写 `"bt2020"/"linear"`，都能解析（已实测），但一旦将来换了 token：
`src` 保持 null ⇒ **不点名、不写日志**，直接落到下面 `AssumeByConvention` ⇒ 置信度 `CodecDefault`
⇒ 又变回"直通"（本轮修掉的原缺陷）。本仓最忌的"静默丢弃不点名"。
建议：`FromCicp` 返回 null 时写一条 `[RAW] ⚠️ 输入声明 token 无法解析（xxx/yyy），退回文件探测` 的日志。

## 7. P2-7 / P2-8 / P2-9（小）

- **`rawCaps != null` 恒真**（`QueueProcessor.cs:604`）：`CapsFor` 对未知格式返回
  `new FormatColorCaps { Format = f, MaxBitDepth = 8 }`，永不返回 null（`ColorTransformPlan.cs:615-621`）。死判据，可删。
- **注释与实现不符**（`ColorIntentFactory.cs:83`）：称 `ColorSourceDecl*` "由 `RawService`/`QueueProcessor` 在预处理时写入"，
  全仓 grep 只有 `QueueProcessor.cs:567-568` 一处写。改注释或补 `RawService`。
- **`BuildColorArgsSplit` 新分支用 `\|\|`**（`FfmpegCommandBuilder.cs:818`）：只设 `Primaries` 时返回 `trc=null`。
  下游 `FfmpegCommandBuilder.Decision.cs:117` 起对 null 是安全的（实测未触发，RAW 路径恒成对设置），
  但建议注释写明"期望成对出现"，或改成"两者都在位才走本分支"。

---

## 8. 上轮遗留项

| 上轮条目 | 本轮状态 | 证据 |
|---|---|---|
| **P0-2** AVIF 增益图 `colr.full_range` | ✅ **真修好** | 见 §9 |
| **P1-3** `MapJxlDistanceInverse` 注释失真 / 锚点选在互逆点 | ✅ **真修到位** | 见 §10 |
| **P1-4** `dngtool.cpp:1392` 旧 distance 公式 | ✅ **已登记（刻意未修）** | `git diff docs/JXL_RAW_16BIT_ADVISORY_2026-10-02.md` 新增 "G" 行：明写"刻意未动（2026-10-03 复查登记）"、公式内容、分歧后果、取证出处。`dngtool.cpp:1391-1392` 公式本身未动（行号 1391，与上轮记的 1392 差 1） |

---

## 9. ✅ AVIF 增益图 full_range（原 P0-2）实测闭环

### 9.1 实跑仓内探针（我亲手跑，非采信提交说明）

```
dotnet build tests/ServiceProbe/ServiceProbe.csproj -c Release     → 0 警告 0 错误
ServiceProbe.exe avifgm
PASS ① 底图 colr=tv(0x00) + 兜底 true  ⇒ 只读继承 0x00
PASS ① 底图 colr=full(0x80) + 兜底 false ⇒ 仍 0x80（双向，不是一律写 tv）
PASS ① 底图无 colr ⇒ 补 0x00
PASS ② Build 的 baseFullRange 默认值 = False
PASS ③ 前提：ffmpeg 裸底图 colr.full_range 全为 tv（实 1 个 nclx）
PASS ③ 产品 AVIF 增益图写出成功（669 B）
PASS ③ 产物含 3 个 nclx colr（底图/增益图/tmap）
PASS ③ **核心**：产物底图 colr.full_range = 0x00
PASS ③ tmap 替代图 colr 与底图同 range = 0x00
PROBE RESULT: pass=11 fail=0
```

结论：**tv-range 底图 → 走增益图组装 → 产物主图 colr 末字节 = `0x00`**，与 ffmpeg 实产一致。原 P0-2 已消除。

### 9.2 变异验证（在 `/tmp/rv` 副本改坏，仓库未动）

把 `PatchColr` 的只读继承去掉 + 默认值改回 `true`：

```
FAIL ① tv + 兜底 true ⇒ 实 0x80
FAIL ① full + 兜底 false ⇒ 实 0x00
FAIL ② 默认值 ⇒ 实 True
FAIL ③ **核心**：产物底图 colr.full_range ⇒ 实 0x80
FAIL ③ tmap ⇒ 实 0x80
PROBE RESULT: pass=6 fail=5
```

⇒ 探针**有判别力**，不是假绿；端到端那一条也真的会红。

### 9.3 回填路径已 grep 确认

`IsoBmffGainMapWriter.cs:99` `baseFullRange = PatchColr(...)` → `:130` 给 tmap 挂 colr 时用的**正是这个回填值**。
路径打通，无"改了默认值却没接上"的问题。

### 9.4 `GainMapEncoder.cs:621` 是否仍需显式传参

调用点仍是 `IsoBmffGainMapWriter.Build(baseBytes, gmBytes, tmapPayload, cPrim, cTrc, cMtx);`
——**没传** `baseFullRange`。但这次**不是"掩盖漏传"**：语义已从"调用方决定 range"改成
"只读继承底图原 colr，`baseFullRange` 退化为**无 nclx 时的兜底**"。默认值 `false`（tv）只是兜底取值，
实际取值由 `ReadNclxFullRange` 决定（实测双向都对）。可以不显式传，**但建议把参数名改成 `baseFullRangeFallback`**
（文档注释已改、形参名未改），避免下次又有人以为是"决定值"。

---

## 10. ✅ `ImageEncoderArgs.cs`（+6）独立审查 —— 与上轮 P1-3 有关，已修到位

两处注释改动：

1. 正函数注释（`:56`）："逐字节等长 … q75→**2.35**" → "**同档** … q75→**2.4**（2.35 取整成发送值 2.4）"。
   复核：q75 ⇒ `0.1 + 25×0.09 = 2.35` ⇒ `Round(2.35, 1, AwayFromZero) = 2.4` ⇒ 实际发出 `2.4`。
   **旧注释与发出值矛盾的问题已消除。**
2. 反函数注释（`:70-71`）："**严格**互逆" → "**近似**互逆，**不是**严格互逆"，并点名
   `q75 → 2.4 → 74 ≠ 75`；`q30/q90 恰为互逆点`。
   复核：`Inverse(6.4)=30` ✓、`Inverse(1.0)=90` ✓、`Inverse(2.4)=74 ≠ 75` ✓。**注释与代码一致了。**

同时确认上轮建议的另一半也做了 —— 探针锚点已挪到**非互逆点**
（`tests/ServiceProbe/Program.cs:3397-3404`）：

```csharp
int rt75 = MapJxlDistanceInverse(MapJxlDistanceForward(75));
int rt30 = MapJxlDistanceInverse(MapJxlDistanceForward(30));
Check(rt75 == 74 && rt30 == 30, "…非互逆点 q75 ⇒ 74（差 1 属量化）；互逆点 q30 ⇒ 30");
```

⇒ P1-3 **已闭环**（注释诚实 + 锚点有判别力 + 保留互逆点作对照）。

---

## 11. ✅ 已核实"没问题"的部分（说明我跑过/查过什么）

| 项 | 方法 | 结论 |
|---|---|---|
| `ColorSourceDecl*` **不进 `PresetData`** | 读 `PresetData.cs` 全文（185 行，无此字段）+ `ApplyPresetData`（`FfmpegOptions.cs:40-141`，不写它）+ 全仓 grep `ColorSourceDecl`（只有 `FfmpegOptions` 声明、`ColorIntentFactory:84-86`、`FfmpegCommandBuilder:818-821`、`QueueProcessor:567-568/618/625`） | ✅ 成立，无遗漏序列化点 |
| 预设回放不会带脏值 | GUI `MainWindow.xaml.cs:3016` 每个文件 `CollectOptionsFromUiAsync()` 新建；CLI `CliParser.cs:462` 每个输入 `baseOptions.DeepClone()` | ✅ 不会跨队列项串味 |
| `DeepClone` 不会丢字段 | `FfmpegOptions.cs:33-37` 走 `AppJsonContext` 全量序列化 ⇒ `ColorSourceDecl*` 会被带上（RAW 路径下正是期望行为） | ✅ |
| `BuildColorArgsSplit` 分支覆盖所有入口 | 该方法是 `-i` 前声明的**唯一出口**（`FfmpegCommandBuilder.Decision.cs:117` 单调用点），GUI/CLI/预设最终都汇入 `BuildArguments` ⇒ 都在新分支之后 | ✅ 无绕过路径；新分支插在 `DecodedUltraHdrColorSpace` 之后、高级模式之前，顺序正确 |
| `rawCanHdrTransfer` 用 `CanHdr`（不叠 GainMap） | `QueueProcessor.cs:604` 明写 `rawCaps.CanHdr`；对照 `ColorTransformPlan.cs:51` `CanHdrWith(gm)=CanHdr \|\| (gm && SupportsGainMap)` | ✅ 与口径一致（Ultra HDR 底图保持 SDR） |
| `rawIsTiff` 大小写/格式串 | `-f tif` / `-f TIF` / `-f tiff` 三种 headless 实跑，日志均为 `TIFF 例外=True` | ✅ 无 `tif`/`tiff` 陷阱 |
| 三档默认实测 | avif ⇒ `bt2020/smpte2084`；tiff/tif ⇒ `bt2020/iec61966-2-1`；webp ⇒ `bt709/iec61966-2-1` | ✅ 与 §7.1 口径表逐项一致 |
| 输入声明恒写（用户选了目标也写） | 实测 B 组日志："用户已指定目标色域 ⇒ 保留用户配置；**输入声明已按线性中间件补齐为 bt2020/linear**" | ✅ §7.6 修法①生效 |
| 四项目重建 | `dotnet build` Release，`0 警告 0 错误`（与 §7.5 自陈一致） | ✅ |
| 变异/改坏只在副本 | 全部在 `/tmp/rv` 与 `/tmp/rvout`；复查后 `git status --short` 与复查前逐项一致 | ✅ |

---

## 12. 给你的建议（按处置顺序）

1. **修 P1-1（一行）**：把 `rawUserChoseTarget` 的三轴并列（§1.4），并补一条 CLI 三轴探针 ——
   这是本批改动里唯一有真实用户可见后果的缺陷，且正是本轮自己要修的那一类。
2. **修 P1-2（两行）**：`:606` 改用 `rawCaps.Format`，撤掉全部 13 处 `!`；同时订正
   `docs/RAW2TIF_QA_DEFECT_2026-10-03.md` §7.5 的"病因尚未查明"（已查明，且是自造的）。
3. **补 P1-3 的枚举序锁**（一条单元断言 + 一句注释），把"恰好等于阈值"这份脆弱性钉住。
4. 顺手清 P2：`ReadNclxFullRange` 改固定偏移；删死判据 `rawCaps != null`；订正"由 RawService 写入"的注释；
   新源描述符分支解析失败时点名。
5. 可选：`IsoBmffGainMapWriter.Build` 形参 `baseFullRange` → `baseFullRangeFallback`，避免语义回退。
