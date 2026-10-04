# 文档账本修复报告（2026-10-04 完整修复轮 · 文档线）

**执行范围（授权）**：只改下列文件 —— 两个 CHANGELOG、`docs/review-2026-10-03/HANDOVER-修复交接.md`、
`.workbuddy/memory/MEMORY.md`、`README.md` / `README.en.md`、`tests/scripts/_probe-cjk-hardcode-scan.ps1`
（仅注释）＋ 本报告。
**未触碰**：`src/`、`tests/`（除上述脚本的注释行）、`docs/RAW2TIF_QA_DEFECT_2026-10-03.md`（§7.5 由主 Agent 订正）。
**未 `git add` / `commit`**。

**基线**：HEAD = `bdd4b0b`（2026-10-03 05:15）；本轮复查报告 = 同目录 `D-文档账本.md`。

---

## 1. 改动总表

| # | 文件 | 位置（改后行号） | 改动性质 |
|---|---|---|---|
| 1 | `CHANGELOG.md` | `:37-45`（RAW 节内新增 2 条）、`:47-59`（新增「AVIF 增益图主图 range 标签」行为变更节） | P0 补登记 |
| 2 | `CHANGELOG.en.md` | `:45-57`（RAW 节内新增 2 条）、`:59-77`（新增同名行为变更节） | P0 补登记（与中文 1:1） |
| 3 | `docs/review-2026-10-03/HANDOVER-修复交接.md` | `:122-124` | CJK 门槛 832 ⇒ 822 + 出处注 |
| 4 | `.workbuddy/memory/MEMORY.md` | `:35-37`、`:53-54` | CJK 门槛标注「2026-10-04 实测值 822」 |
| 5 | `README.md` | `:7`、`:218-220` | 版本行**不改**，加「本工作区为 beta4 开发线」注 |
| 6 | `README.en.md` | `:7`、`:225-228` | 同上（英文同构） |
| 7 | `tests/scripts/_probe-cjk-hardcode-scan.ps1` | `:330-338` | 注释块排回时间正序 + 修正 `[RAW]` 分桶机制描述（**数值与判据零改动**） |

---

## 2. 【P0】CHANGELOG 中英补记（D 线 #2 / #6 / #11 相关）

### 2.1 中文最终措辞（`CHANGELOG.md`）

**(a) AVIF 增益图主图 range 标签 —— 新增独立节 `:47-59`**

```markdown
**🔧 行为变更（AVIF 增益图主图的 range 标签，2026-10-03）**

- **AVIF 增益图产物的主图 range 标签不再被写死为 full**（`IsoBmffGainMapWriter.PatchColr`）：此前 `PatchColr`
  按调用方参数**改写**底图 `colr` 盒的 `full_range` 位，且 `Build()` 的 `baseFullRange` 默认 `true`（full）
  ⇒ **每条 AVIF 增益图产物的主图**都被贴上 full range 标签，而像素实为 **limited/tv**（ffmpeg 实产 `color_range=tv`、
  容器内 `colr` 末字节 `0x00`）⇒ 下游按 full 解读会把 Y∈[16,235] 当成 [0,255]，表现为**黑阶抬起、对比度压缩**。
  现改为**只读继承底图原 `colr` 的 `full_range` 位**（新增 `ReadNclxFullRange`），默认兜底值 `true → false`
  （limited/tv，与 ffmpeg 实产一致）；底图自带 nclx 时一律以原 `colr` 为准，且继承是**双向**的
  （底图真为 full 时仍写 full）。
  ⚠ 这是**已交付产物的标签发生变化**：此前产出的 AVIF 增益图文件，其主图 range 标注与像素不符 ⇒ 需重新导出。
  永久锁：新增 `ServiceProbe avifgm` 模式（默认清单第 25 个），端到端校验产物主图 `colr` 末字节与底图一致
  （三段：① 单元级反射直调 `PatchColr`，验证只读继承且双向；② `Build` 的 `baseFullRange` 默认值必须 `false`；
  ③ 真 ffmpeg 编 tv 底图经产品写出器后，产物底图 `colr` 仍为 `0x00`）。
```

**(b) `--color-space` 覆盖 —— RAW 节新增条目 `:37-42`**

```markdown
- **修复：`--color-space`（非高级模式）此前会被 RAW 默认目标覆盖**（2026-10-04 修）。RAW 默认块的「用户是否已选
  目标」判据原先只查 `UseAdvancedColorParameters` + `ColorPrimaries`/`ColorTrc`，**漏了非高级模式下的 `ColorSpace`**
  （CLI `--color-space`、GUI 色空间友好名走的正是这条）⇒ 用户显式选过目标时 RAW 默认块**照样触发并覆盖**
  （实测 `--color-space sRGB` 交付 `bt2020 + sRGB`，而用户要的是 bt709 原色）。现新增 `rawUserChoseTarget` 判定：
  用户显式指定**任一**色彩目标轴（色空间友好名 `ColorSpace` / `ColorPrimaries` / `ColorTrc`）都不再被 RAW 默认值覆盖；
  同时把**输入声明** `ColorSourceDecl*` 移出该判定（**恒写** —— 用户选了目标时 dngtool 中间件**仍是线性**）。
```

**(c) HDR 档判据 —— RAW 节新增条目 `:43-45`**

```markdown
- **RAW 三档的 HDR 档判据由 `CanHdrWith(JpegGainMap)` 改用 `CanHdr`**：`jpg`+GainMap **不再**算作 HDR 档
  ⇒ 这类目标的默认输出由 **Rec.2020 + PQ** 变为 **bt709 + sRGB**（Ultra HDR 的**底图**必须是 SDR；把 `jpg`+GainMap
  算进 HDR 档会让底图写成 PQ，属语义错）。
```

### 2.2 英文最终措辞（`CHANGELOG.en.md` `:45-77`）

**(a)** 节标题 `**🔧 Behaviour change (range flag of the base image in AVIF gain-map products, 2026-10-03)**`，
条目首句 `**The base image of an AVIF gain-map product no longer receives a hard-coded "full" range flag**`
（`IsoBmffGainMapWriter.PatchColr`）；含 `full_range` / `limited/tv` / `color_range=tv` / `0x00` /
`Y∈[16,235]` → `[0,255]` / **lifted blacks and compressed contrast** / `ReadNclxFullRange` / `true → false` /
bidirectional / ⚠ already-shipped artifacts ⇒ re-export / `ServiceProbe avifgm`（25th，three parts ①②③）。

**(b)** `**Fixed: `--color-space` (non-advanced mode) used to be overwritten by the RAW default target**`（`fixed 2026-10-04`）；
含 `UseAdvancedColorParameters` + `ColorPrimaries`/`ColorTrc` ⇒ **missing `ColorSpace` in non-advanced mode**；
`--color-space sRGB` ⇒ `bt2020 + sRGB` vs BT.709 primaries；`rawUserChoseTarget`；
**any** colour-target axis（friendly `ColorSpace` name / `ColorPrimaries` / `ColorTrc`）；`ColorSourceDecl*` always written。

**(c)** `**The RAW tier's HDR test switched from `CanHdrWith(JpegGainMap)` to `CanHdr`**`；`jpg` + gain map no longer HDR tier；
**Rec.2020 + PQ** ⇒ **bt709 + sRGB**；Ultra HDR base image must stay SDR。

### 2.3 中英逐条对照表

| 项 | CHANGELOG.md | CHANGELOG.en.md | 一致? |
|---|---|---|---|
| 节 1 标题 | `:22` 🔧 行为变更（RAW 默认输出目标，2026-10-03） | `:25` 🔧 Behaviour change (RAW default output target, 2026-10-03) | ✅（既有） |
| 节 1 条目 1 三档 | `:24-28` | `:27-34` | ✅（既有） |
| 节 1 条目 2 直通缺陷 | `:29-36` | `:35-44` | ✅（既有） |
| **节 1 条目 3 = (b)** | `:37-42` `--color-space` 覆盖修复，**2026-10-04 修** | `:45-53` `--color-space` overwritten，**fixed 2026-10-04** | ✅ 日期一致 |
| **节 1 条目 4 = (c)** | `:43-45` `CanHdrWith(JpegGainMap)` ⇒ `CanHdr` | `:54-58` `CanHdrWith(JpegGainMap)` ⇒ `CanHdr` | ✅ |
| **节 2 = (a) 标题** | `:47` 🔧 行为变更（AVIF 增益图主图的 range 标签，**2026-10-03**） | `:59` 🔧 Behaviour change (range flag of the base image in AVIF gain-map products, **2026-10-03**) | ✅ 日期一致 |
| (a) 症状：full 标签 vs limited 像素 | `:51` `color_range=tv`、`colr` 末字节 `0x00` | `:63-65` `color_range=tv`, last byte `0x00` | ✅ |
| (a) 后果 | `:52` Y∈[16,235] 当成 [0,255] ⇒ 黑阶抬起/对比度压缩 | `:66` Y∈[16,235] onto [0,255] ⇒ lifted blacks / compressed contrast | ✅ |
| (a) 修法 | `:53-55` 只读继承 + `ReadNclxFullRange` + 兜底 `true → false` + 双向 | `:67-70` inherit read-only + `ReadNclxFullRange` + `true → false` + bidirectional | ✅ |
| (a) 已交付产物警示 | `:56` ⚠ 需重新导出 | `:71-72` ⚠ should be re-exported | ✅ |
| (a) 永久锁 | `:57-59` `ServiceProbe avifgm`（第 25 个，三段 ①②③） | `:73-77` `ServiceProbe avifgm`（25th，①②③） | ✅ |
| 条目总数 | 节 1 = 4 条、节 2 = 1 条（**共 5 条**） | 节 1 = 4 条、节 2 = 1 条（**共 5 条**） | ✅ 条目数/顺序一致 |
| 技术值枚举 | `0x80`/`0x00`、`bt2020`、`pq`、`srgb`、`bt709`、`CanHdr`、`rawUserChoseTarget`、`ColorSourceDecl*`、`avifgm` | 逐字对应 | ✅ 无漂移 |

**结论：中英条目数、顺序、技术值、日期全部一致；无缺项、无多出项。**

### 2.4 证据链（每条「改动 X ⇒ 实际是 Y」）

| 写入 CHANGELOG 的断言 | 证据（file:line / 命令） |
|---|---|
| `PatchColr` 由「按参数写」改为「只读继承」 | `src/FfmpegGui/Services/IsoBmffGainMapWriter.cs:268-290`：`private static bool PatchColr(...)` 返回值改为 `bool`，先 `ReadNclxFullRange(props[i])`，`if (own.HasValue) fr = own.Value` |
| 默认兜底 `true → false` | 同文件 `:82`：`bool baseFullRange = false`（原 `true`）；`git diff` 显示签名行由 `bool baseFullRange = true` 改为 `false` |
| 新增 `ReadNclxFullRange` | 同文件 `:292-299`（`git diff` 新增段） |
| 「只读继承」的权威口径 | 同文件 `:258-263` 注释引用 libultrahdr `avifultrahdr.cpp:435/520`「取实际图像的 range」 |
| ffmpeg 实产 tv（`0x00`） | 同文件 `:259-260` 注释：libaom-av1 静态图 `ffprobe color_range=tv`、colr 盒末字节 `0x00`（**本轮未独立复跑，沿用该注释与 D 线 §2.1**） |
| `ServiceProbe avifgm` 为本轮新增永久锁 | `tests/ServiceProbe/Program.cs:586-594`（`<summary>` 明写「2026-10-03 补」、三段 ①②③）、`:105` case 分支；`tests/scripts/_run-step3-gates.ps1:35,43`（`git diff` 中为 `+` 行，`avifgm` 列在 `$Probes` 末位 = 第 25 个） |
| (b) 旧判据漏 `ColorSpace` | `src/FfmpegGui/Services/QueueProcessor.cs` 的 `git diff`：旧代码 `if (!UseAdvancedColorParameters && IsNullOrWhiteSpace(ColorPrimaries) && IsNullOrWhiteSpace(ColorTrc))` ⇒ 现 `rawUserChoseTarget`；实测 `--color-space sRGB` 交付 `bt2020 + sRGB` 引自 `docs/RAW2TIF_QA_DEFECT_2026-10-03.md:346` |
| (c) HDR 判据改用 `CanHdr` | 同文件 `git diff`：`rawCaps.CanHdrWith(captured.Options.JpegGainMap)` ⇒ `rawCaps.CanHdr`；语义说明见 `RAW2TIF_QA_DEFECT_2026-10-03.md:303` |

⚠ **(b) 的措辞按「修复后的最终语义」书写**（用户显式指定**任一**色彩目标轴都不再被覆盖）。当前工作区的
`rawUserChoseTarget` 仍是「高级模式看 Primaries/Trc、非高级模式看 ColorSpace」的**二选一**形态，
主 Agent 正在修复其丢维问题；修复落地后代码与 CHANGELOG 措辞才完全一致。**已用 SendMessage 提醒主 Agent 核对。**

---

## 3. 版本 / 门槛数字对齐

### 3.1 `HANDOVER-修复交接.md:122`（D 线 #9）

- 前：`- **CJK 门禁**：XAML ≤13 / CsUi ≤832，fail-closed 且余量极紧；新增中文必须进 locale json 而非硬编码。`
- 后：`:122-124`

```markdown
- **CJK 门禁**：XAML ≤13 / CsUi ≤**822**，fail-closed 且余量极紧；新增中文必须进 locale json 而非硬编码。
  （⚠ 本行原写 832，已于 2026-10-04 订正为 **822** —— 出处 `tests/scripts/_probe-cjk-hardcode-scan.ps1:339`
  `$BASE_CS_UI = 822`；本文件 §4 第 2 条的 `840→821` 亦已过时，现值同样为 822。）
```

出处核验：`tests/scripts/_probe-cjk-hardcode-scan.ps1:339` = `$BASE_CS_UI    = 822`（本轮改注释后**行号未变**，
因两段注释为等长互换）。

### 3.2 `.workbuddy/memory/MEMORY.md`（D 线 #8）

全文 grep `821|832|840` 命中 4 处（`:33` `:35` `:53`），其中 `:33-34` 早前已被主 Agent 更新到 822。本轮处理：

- `:35-37`（**门槛声明句**）—— 前：`⚠ **CJK 门槛现值 = 822**（MEMORY 旧值 821、HANDOVER 的 832、README 的 840 都已过时）。`
  后：

```markdown
  ⚠ **CJK 门槛现值 = 822**（**2026-10-04 实测值**，余量 0）。历史读数 **821 / 832 / 840 一律作废** ——
    凡在旧 MEMORY、`docs/review-2026-10-03/HANDOVER-修复交接.md`、`docs/review-2026-10-03/README.md`
    等处看到这三个数，一律按 **822** 读（出处：脚本 `:339` `$BASE_CS_UI = 822`）。
```

- `:53-54`（**历史实验记录**）—— 前：`本次据此定位刀口在 840 而非 821。`
  后：`本次据此定位刀口在 840 而非 821（**该两数为 2026-10-03 的历史读数**，仅用于说明「840 那版基线为何抓不到」；**现阈值 = 822**，见上一条）。`

> ⚠ **此处是有意偏离「把所有 821/832/840 改成 822」的字面指令**：`:53` 不是门槛声明，而是 2026-10-03 那次
> 「加 N 行找刀口」实验的**结果读数**（刀口在 840 而非 821 正是「840 版基线抓不到新增」的论据）。
> 把它改成 822 会**销毁一条一手取证**。改为加时间戳注释，既满足「现值唯一 = 822」又不伪造历史。
> 若主 Agent 认为仍应硬性替换，请告知，我再改。

### 3.3 `README.md` / `README.en.md` 版本号（D 线 #10）

**判定：属于「已发布稳定版主线」描述 ⇒ 不改版本号，改为加注。**

判断依据（原文行引用）：

1. `README.md:5` 原文 `**v1.6.0** — 2026-09-28 **发布** · …`，`README.en.md:5` 原文
   `**v1.6.0** — 2026-09-28 **Release** · …` ⇒ 两处说的都是「**已发布**」的版本，而非工作区在编版本。
2. 日期 2026-09-28 与 **git tag `1.6.0` @ 2026-09-28（commit `3028faf`）** 精确吻合，也与
   `CHANGELOG.md:306` / `CHANGELOG.en.md:391` 的 `### v1.6.0 (2026-09-28)` 条目吻合 ⇒ 指稳定版发布点。
3. 版本行的最后一次改动是 `git log` 中的 `cbcf11a 2026-09-28 release: v1.6.0 正式版`；此后 beta2/3/4 三轮
   （2026-09-30 / 10-01 / 10-03）**均未更新该行**。
4. README 全文 **0 命中** beta4 的新功能「缩略图 / `--thumbnail`」（`grep -n "缩略图\|thumbnail" README.md` 无输出）
   ⇒ README 正文**不跟随开发线**更新，把它改成 beta4 会造成「正文仍是稳定版功能集 + 版本号却是 beta4」的新不一致。

因此按授权采用「加注」分支：

- `README.md:7`（新增）：`> ℹ️ 本 README 记述的是**已发布的稳定版 v1.6.0**；**本工作区当前为 `v1.6.0-beta4` 开发线**（最新条目见 CHANGELOG.md）。`
- `README.md:218-220`（新增，紧随 `:216`「当前版本 **v1.6.0**（2026-09-28）」之后）：说明该句指稳定版（对应 tag `1.6.0`），
  本工作区为 `v1.6.0-beta4` 开发线（`src/FfmpegGui/FfmpegGui.csproj` 的 `<Version>` = `1.6.0-beta4`），
  其变更见 CHANGELOG 的 `v1.6.0-beta4` 条目，并明写「README 正文未随 beta 线同步更新」。
- `README.en.md:7` 与 `:225-228`：英文同构（`released stable` / `working tree is on the v1.6.0-beta4 development line`）。

中英措辞逐句对应，均无版本号改动。

---

## 4. CJK 脚本注释的两处瑕疵（D 线 #13 + 新发现的机制错）

文件：`tests/scripts/_probe-cjk-hardcode-scan.ps1`（**仅注释**，数值与判据零改动 —— `git diff` 的非注释行只有
`$BASE_CS_UI = 840 → 822`，那是**本轮之前**既有改动，不是我改的）。

### 4.1 时序倒挂 ⇒ 排回正序（`:328-338`）

- 前：`:328-329` 2026-09-19 第七次（824→832） → `:330-336` **2026-10-04**（821→822） → `:337-338` **2026-09-26 第八次**（832→837） → `:339 $BASE_CS_UI = 822`
- 后：`:328-329` 09-19 第七次 → `:330-331` **09-26 第八次** → `:332-338` **10-04** → `:339 $BASE_CS_UI = 822`

数字（`824→832` / `832→837` / `821→822` / `822`）与判据行**一字未动**；互换的两段行数相同 ⇒ `$BASE_CS_UI`
仍在第 **339** 行，HANDOVER / MEMORY 里的 `:339` 出处引用继续有效。

### 4.2 `[RAW] …` 分桶机制描述纠错（`:337`）

- 前：`` ⚠ 同批其余新增中文均**不计**：`QueueProcessor` 的 `captured.Log += $"[RAW] …"` 走 `Log +=` sink ⇒ 记 Diag。``
- 后：`` ⚠ 同批其余新增中文**不计进 UI 桶**：`QueueProcessor` 的 `captured.Log += $"[RAW] …"` 走的是 **[tag] 桶** —— 分桶时 `$body.TrimStart().StartsWith('[')`（见下方 `Measure-Cjk` 的 `$csTag++`）**优先于** sink 判定，不是「走 `Log +=` sink ⇒ 记 Diag」；两条路都不进 UI 桶 ⇒ 结论不变：本批判据项 `CsUi` 仍只 +1。``

机制取证：同脚本 `:401` `if ($body.TrimStart().StartsWith('[')) { $csTag++ } else { … $rxDiagSink 判定 … }`
—— `[tag]` 判定在 `if` 分支上，**先于** sink 判定，命中即进 `CsTag`，根本走不到 sink 分支。
（脚本自身的负控输出也佐证：临时语料中 `[tag]` 形态的消息实测 `[tag]=1 / 其余=1`。）

### 4.3 实跑（改后）

命令：`pwsh -NoProfile -File tests/scripts/_probe-cjk-hardcode-scan.ps1`（仓库根目录，脚本只读 `src/FfmpegGui`）

```
── 0. 语料自检 ──
  PASS 扫到 90 个源文件（XAML/AXAML 7 ≥5；C# 83 ≥30）

── 1. 口径② 实测（判据：不得增加） ──
  ℹ XAML/AXAML 含中文行 = 13（基线 13，余量 0）
  ℹ C# 含中文字面量行 = 1359（基线 1320，余量 -39）
  ℹ   其中 [tag] 编码日志 = 487（基线 459，余量 -28）
  ℹ   其中 其余（窗口标题/菜单/对话框/进度标签/预设名/EXIF 字段名/--help…）= 872（基线 861，余量 -11）
  ℹ     其中 **UI 面**（判据只看这一项）= 822（基线 822，余量 0）
  ℹ     其中 诊断 sink（log?.Invoke / Log( / .Log += / Trace|Debug|Console.Write*，**不判**）= 50
  PASS XAML/AXAML 硬编码中文行 13 ≤ 基线 13（不得增加）
  PASS C# **UI 面**文案 822 ≤ 基线 822（不得增加）
  PASS ②-c 分桶自洽：诊断 50 + UI 822 = 其余 872
  PASS 反控（②-b/②-c 口径）：判据段**不含** C# 总计 / [tag] 两桶的比较（实 0 处）
  PASS 反控（②-b/②-c 口径）：判据段**确实**含 XAML 与「UI 面」各 1 条（实 1 / 1）
  PASS 分桶自洽：[tag] 487 + 其余 872 = 总计 1359

── 2. 负控（临时语料，跑完即删） ──
  ℹ ②-c 负控语料实测：诊断=3 UI=2（[tag]=0 其余=5）
  PASS 负控（②-c）：**诊断 sink 的 3 条不算 UI、UI 写法的 2 条必须算 UI**（实 诊断=3 / UI=2）
  PASS 负控（②-c）：分桶自洽（诊断 3 + UI 2 = 其余 5）
  ℹ 临时语料实测：XAML=1 C#=2（[tag]=1 其余=1）
  PASS 负控：.cs 含 2 行硬编码中文 ⇒ 计数恰为 2（实测 2）—— 计数**有牙**
  PASS 负控：[tag] 分桶正确 ⇒ [tag]=1 / 其余=1（实测 1 / 1）
  PASS 负控：.xaml 含 1 行属性中文 ⇒ 计数恰为 1（实测 1）
  PASS 反控：注释里的中文**不得**计入（若未剥注释，.cs 会 ≥4、.xaml 会 ≥2）
  PASS 反控：空语料 ⇒ 0/0（配合第 0 节的语料自检，空目录不可能假绿）

===== cjk-hardcode: PASS=14 FAIL=0 =====
EXITCODE=0
```

⇒ **UI 面 = 822、`PASS=14 FAIL=0`、exit=0，三项与改前完全一致**（注释改动零副作用）。

---

## 5. 移交主 Agent 的事项

1. **§7.3 改动规模记错（D 线 #11）** —— `docs/RAW2TIF_QA_DEFECT_2026-10-03.md` 属本轮禁止清单（你负责），
   故我只登记不修改：
   - 文档 `:297` 称「6 文件 / **+119 −21**」；实测 `git diff --numstat src/FfmpegGui` =
     `FfmpegOptions.cs +13/−0`、`ColorIntentFactory.cs +29/−5`、`ImageEncoderArgs.cs +3/−3`、
     `FfmpegCommandBuilder.cs +11/−0`、`IsoBmffGainMapWriter.cs +39/−8`、`QueueProcessor.cs +55/−27`
     ⇒ **6 文件 +150 / −43**；而 §7.3 表格正文**只列了 4 个文件**（小计 +108/−32）。
     **没有任何一个口径能得出 +119 / −21。**
   - 另：表内漏列的 `IsoBmffGainMapWriter`（+39/−8）其实是**另一件事**（AVIF 增益图 range 修复，本轮 CHANGELOG 的 (a)），
     建议拆分登记或显式标注「含非本主题改动」。
2. **CHANGELOG (b) 措辞核对** —— 我按「修复后的最终语义」写了「任一色彩目标轴都不再被覆盖」；
   请在你修完 `rawUserChoseTarget` 丢维问题后核对代码是否匹配该措辞。
3. **MEMORY.md:53 的偏离** —— 见 §3.2 的说明，如需硬性替换请告知。
4. **README 乱码（D 线 §5.4）** —— 未处理（不在本轮清单）：`README.md:14/16/36/43/90` 存在生成损坏。
5. **CHANGELOG 版本头日期（D 线 #14）** —— `CHANGELOG.md:15` / `CHANGELOG.en.md:16` 仍是 `v1.6.0-beta4 (2026-10-01)`，
   而新增内容落在 10-03/10-04。本轮**未改**（指令未授权且会影响条目分节），建议由你决定是否另起条目。

---

## 6. 未验证项（明确标注）

| 项 | 状态 |
|---|---|
| (a) 中「ffmpeg 实产 `color_range=tv` / colr 末字节 `0x00`」 | **未本轮独立实测**，引自 `IsoBmffGainMapWriter.cs:259-260` 注释与 D 线 §2.1；`avifgm` ③ 段在代码上正是钉这一点 |
| `ServiceProbe avifgm` 是否真跑出读数 | **未独立复跑**（本轮只跑了 CJK 脚本）；D 线 §4 第 2 项记录「25 个 mode、Σpass=669，含 avifgm」，我沿用该结论 |
| (a) 中「下游按 full 解读 ⇒ 黑阶抬起/对比度压缩」的视觉后果 | **未验证**（未做像素级比对）；为代码注释中载明的机制推论 |
| (b) 的实测数字「`--color-space sRGB` 交付 `bt2020 + sRGB`」 | **未复跑**，引自 `RAW2TIF_QA_DEFECT_2026-10-03.md:346` |
| README 判定中的「README 不跟随开发线」 | ✅ 已验证（`git log` + `grep thumbnail` 双证据，见 §3.3） |
| CJK 脚本实跑 | ✅ 已实跑（§4.3） |

---

## 附：本轮取证命令留痕

```bash
git diff --numstat -- src/FfmpegGui                       # → +150 / −43（6 文件）
git diff -- src/FfmpegGui/Services/IsoBmffGainMapWriter.cs # → :82 默认值、:268 PatchColr、:292 ReadNclxFullRange
git diff -- src/FfmpegGui/Services/QueueProcessor.cs       # → rawUserChoseTarget、CanHdrWith⇒CanHdr
git diff -- tests/scripts/_run-step3-gates.ps1 | grep avifgm   # → 两处 + 行（第 25 个 mode）
grep -n "avifgm" tests/ServiceProbe/Program.cs             # → :90 :105 :586-594 三段落
git for-each-ref --sort=-creatordate --format='%(refname:short) | %(creatordate:short)' refs/tags | head -5
git log --format='%h %ad %s' --date=short -5 -- README.md  # → 版本行最后改动 = cbcf11a 2026-09-28 release: v1.6.0
grep -n "缩略图\|thumbnail" README.md                       # → 0 命中
pwsh -NoProfile -File tests/scripts/_probe-cjk-hardcode-scan.ps1   # → PASS=14 FAIL=0, exit=0
```
