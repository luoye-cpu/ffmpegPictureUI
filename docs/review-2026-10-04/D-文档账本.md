# D 线｜文档与账本一致性审查（2026-10-04）

**审查基线**：HEAD = `bdd4b0b`（2026-10-03 05:15），审查对象为其后**全部在工作区未提交**的改动。
**审查范围**：本批改动的文档部分（2 个 CHANGELOG、`docs/TESTING.md`、`docs/JXL_RAW_16BIT_ADVISORY_2026-10-02.md`、
新增 `docs/RAW2TIF_QA_DEFECT_2026-10-03.md`、新增 `docs/review-2026-10-03/` 7 文件），以及「文档宣称 vs 代码实际」的对账。
**取证纪律**：未修改 `src/` `tests/` `docs/` 任何既有文件；未 `git add` / `commit`。脚本与构建取证均在 `%TEMP%` 副本中进行
（`C:/Users/20210/AppData/Local/Temp/cjkprobe-663`、`/cs8602-57`）。审查后 `git status` 除新增本报告目录外无任何变化。

---

## 1. 结论速览

| # | 级别 | 不一致项 | 位置 |
|---|---|---|---|
| 1 | **P0** | 「**整轮 65/65 全绿**」**与日志矛盾**：四窗实际只跑 **30/65**，每份日志自己都写「**不构成基线**」 | `RAW2TIF_QA_DEFECT_2026-10-03.md:327-328` |
| 2 | **P0** | **AVIF 增益图 `colr.full_range` 修复（用户可见的行为变更）未进任何 CHANGELOG**；中英两边都没有 | `CHANGELOG.md:22-36` / `CHANGELOG.en.md:25-44` vs 代码 `IsoBmffGainMapWriter.cs:82,268` |
| 3 | **P0** | 「`verify-release-package` ⇒ **16/0**」**证据缺失**：`tests/output/**` 全文检索 0 命中，它也不在 65 条清单里 | `RAW2TIF_QA_DEFECT_2026-10-03.md:329` |
| 4 | **P1** | 同一份文档**两组互斥的三档口径**：§附二 5.1 把 `webp/jpg/jpegli` 与 `tiff` 同列 B 档；§7.1 把它们划入 bt709+sRGB。实装是后者 | `:256` vs `:282`（代码 `QueueProcessor.cs:606-612`） |
| 5 | **P1** | §附二 5.2 仍判「**TIFF 钳成 bt709 + sRGB**」，与 §7.1「TIFF = 唯一例外 Rec.2020+SDR」直接冲突，且未标注已废止 | `:269` vs `:281` |
| 6 | **P1** | §7.6 的 `--color-space` 用户意图被覆盖修复**未进 CHANGELOG**（CHANGELOG 只陈述了事后应然） | `QueueProcessor.cs:566-575` vs `CHANGELOG.md:28` |
| 7 | **P1** | README §1.5 点名的**「未被登记归因」中文文案 8 组仍在源码里硬编码，0 条进 locale json** —— 该主张至今成立 | 见 §5 表（8 条 file:line 全命中） |
| 8 | **P2** | `.workbuddy/memory/MEMORY.md` 的 CJK 门槛仍记 **821**，实际已是 **822** | `MEMORY.md:32-34` |
| 9 | **P2** | `HANDOVER-修复交接.md:122` 仍写「CsUi ≤ **832**」，与 TESTING.md 的 822 冲突 | `:122` vs `docs/TESTING.md:2495` |
| 10 | **P2** | README.md / README.en.md 版本号仍是 **v1.6.0**，未随 beta4 更新（中英彼此一致，与 CHANGELOG/csproj 不一致） | `README.md:5,214` / `README.en.md:5,221` |
| 11 | **P2** | §7.3 声称改动规模「**6 文件 / +119 −21**」，实测 `src/FfmpegGui/**` = **+150 / −43**，且表内只列了 4 个文件 | `:297` |
| 12 | **P2** | CJK 脚本**信息桶基线脱节**：实测 C# 总计 1359 vs 基线 1320（余量 **−39**）、TAG 487 vs 459、其余 872 vs 861 | `out.txt:6-8` 实测 |
| 13 | **P2** | CJK 脚本注释块**时序倒挂**：「2026-09-26 第八次抬基线」段落排在「2026-10-04 抬基线」之后 | `_probe-cjk-hardcode-scan.ps1:438-441` |
| 14 | **P2** | 两个 CHANGELOG 的版本头日期仍是 **(2026-10-01)**，而本批新增内容落在 **2026-10-03**，未另起条目也未改日期 | `CHANGELOG.md:15` / `CHANGELOG.en.md:16` |
| 15 | **P2** | `git tag` **无 beta4**（最新 `1.6.0BETA3`/`1.6.0`）；且 tag 命名 `1.6.0BETA3` 与包名 `v1.6.0-beta4` 风格不一 | `git tag -l` |
| 16 | **P2** | M1/M2 变异验证、`pack.ps1 -Mode all exit=0`、**均未在日志中留痕** ⇒ 不可事后核验 | 见 §4 表 |

> 说明：§7.5 自陈疑点「`FfmpegOptions.Format` 判可能为空的原因尚未查明」——**仍是遗留**（文档未更新），
> 但本轮实测已把它**显著收窄**到一个具体位置，详见 §4.3。这是本次唯一「文档落后于可获得证据」的项。

---

## 2. CHANGELOG 中英对照

两边均为在 `v1.6.0-beta4` 现有条目下**新增同一节**，结构 1:1 对齐：

| 项 | CHANGELOG.md | CHANGELOG.en.md | 一致? |
|---|---|---|---|
| 节标题 | `:22` 🔧 行为变更（RAW 默认输出目标，2026-10-03） | `:25` 🔧 Behaviour change (RAW default output target, 2026-10-03) | ✅ |
| 条目 1 三档 A（HDR 传递容器） | `:25` avif / jxl / png-16bit ⇒ Rec.2020 + PQ | `:29` avif / jxl / 16-bit png ⇒ Rec.2020 + PQ | ✅ |
| 条目 1 三档 B（TIFF 例外） | `:26` Rec.2020 + SDR（bt2020 原色 + sRGB 曲线，经 2020 ICC） | `:30-31` Rec.2020 + SDR (BT.2020 primaries + sRGB curve, generated BT.2020 ICC) | ✅ |
| 条目 1 三档 C（其余） | `:27` webp/jpg/jpegli/gif/jxr/ppm ⇒ bt709 + sRGB | `:32` webp/jpg/jpegli/gif/jxr/ppm ⇒ bt709 + sRGB | ✅ |
| 条目 1 尾句（用户目标优先） | `:28` 用户显式选择仍以用户为准 + 受 `GetFormatColorCapabilities` 钳制 | `:33-34` explicitly chosen target still wins + clamped | ✅ |
| 条目 2 直通缺陷机制 | `:29-31` BuildColorArgsSplit 输入声明 / DecideOutputColor 输出语义 / dngtool 线性中间件 | `:35-38` 同构表述 | ✅ |
| 条目 2 修复手段 | `:32-34` 新增运行时字段 `ColorSourceDeclPrimaries/Trc`；源描述符改用 `FromCicp(...)` ⇒ `CicpTag` 恰达闸门 | `:40-42` 同（含 `MinConfidenceForMapping` gate） | ✅ |
| 条目 2 实测值 | `:35-36` TIF `bt2020/iec61966-2-1`；AVIF `yuv420p12le + bt2020 + smpte2084` | `:43-44` 逐字一致 | ✅ |
| 版本号 / 日期 | `:15` `v1.6.0-beta4 (2026-10-01)` | `:16` `v1.6.0-beta4 (2026-10-01)` | ✅（但见 P2-14） |
| 行数 | +16 | +21 | 差异纯因英文折行，**无语义漂移** |

**结论：中英双语本轮完全对齐，无缺项、无多出项、无技术值漂移。** 这是本次唯一干净的一项。

### 2.1 CHANGELOG 宣称 vs 本轮实际代码

| 文档宣称 | 代码实测 | 判定 |
|---|---|---|
| RAW 默认三档（A/B/C） | `QueueProcessor.cs:606-612`：`ColorPrimaries = (rawCanHdrTransfer \|\| rawIsTif) ? "bt2020" : "bt709"`；`ColorTrc = rawCanHdrTransfer ? "pq" : "srgb"` | ✅ 佐证 |
| 新增运行时字段 `ColorSourceDeclPrimaries/Trc` | `FfmpegOptions.cs:185-194`（含「不要加进 PresetData」约定） | ✅ 佐证 |
| `BuildColorArgsSplit` 最前新增分支 | `FfmpegCommandBuilder.cs:813-823`（排在最前的 `ColorSourceDecl*` 分支） | ✅ 佐证 |
| 源描述符改用 `FromCicp(...)` ⇒ `CicpTag` 达阈值 | `ColorIntentFactory.cs`（`git diff` +29/−5，含 `FromCicp` 调用与描述符名 `"RAW 中间件（dngtool 去马赛克线性输出）"`） | ✅ 佐证 |
| 「用户显式选择目标色域时仍以用户为准」 | `QueueProcessor.cs:566-575` 新增 `rawUserChoseTarget`，明确保住 GIF 例外的 `Target==null` 路径 | ✅ 佐证 |
| **（无记载）** AVIF 增益图 `colr.full_range` 修复 | `IsoBmffGainMapWriter.cs:82` 默认值 `true→false`；`:268-290` `PatchColr` 改为**只读继承**底图原 colr 位并回传；新增 `ReadNclxFullRange` | ❌ **未登记（P0-2）** |
| **（无记载）** HDR 判据由 `CanHdrWith(JpegGainMap)` 改用 `CanHdr` | `QueueProcessor.cs:600`：jpeg+GainMap 不再算 HDR 档 | ❌ 未登记（P2，措辞细节） |
| **（无记载）** `--color-space`（非高级模式）此前会被 RAW 默认块覆盖 | `QueueProcessor.cs:566-575` 旧判据只查 `UseAdvancedColorParameters`+`ColorPrimaries/Trc` | ❌ 未登记（P1-6） |

> **P0-2 重点说明**：`baseFullRange` 默认值翻转 + `PatchColr` 改只读继承，改变的是**每条 AVIF 增益图产物主图的 range 标签**
> （`0x80` full → 继承 ffmpeg 实产的 `0x00` tv）。这是典型的用户可见行为变更，却只出现在 `docs/TESTING.md:237-243`
> 的 `avifgm` 新判据说明里，**中英两个 CHANGELOG 一个字都没有**。即「能力/修复增长未登记」。

---

## 3. `docs/TESTING.md` 改动 —— 实测核对

作者改的是「CsUi 判据行」，位置 `docs/TESTING.md:2494-2499`：

- 判据值：`XAML ≤ 13` 且 `CsUi ≤ **822**`（原 832 → 误抬 840 → 复核改回 821 → 本轮抬 **822**）
- 成因说明：`ColorIntentFactory` 的 `FromCicp(..., "RAW 中间件（dngtool 去马赛克线性输出）")` 描述符名，非 UI 文案但按 ②-c fail-closed 必落 UI 桶

### 3.1 实测取证

语料校验：`src/FfmpegGui` 下 **83 个 .cs + 7 个 .xaml/axaml**（排除 bin/obj），**临时副本与仓库文件清单逐字 diff 一致**。
脚本：`pwsh -NoProfile -File <temp>/tests/scripts/_probe-cjk-hardcode-scan.ps1`，`EXITCODE=0`。

```
── 1. 口径② 实测（判据：不得增加） ──
  ℹ XAML/AXAML 含中文行 = 13（基线 13，余量 0）
  ℹ C# 含中文字面量行 = 1359（基线 1320，余量 -39）
  ℹ   其中 [tag] 编码日志 = 487（基线 459，余量 -28）
  ℹ   其中 其余（…） = 872（基线 861，余量 -11）
  ℹ     其中 **UI 面**（判据只看这一项）= 822（基线 822，余量 0）
  ℹ     其中 诊断 sink（…） = 50
  PASS XAML/AXAML 硬编码中文行 13 ≤ 基线 13
  PASS C# **UI 面**文案 822 ≤ 基线 822
  PASS ②-c 分桶自洽：诊断 50 + UI 822 = 其余 872
===== cjk-hardcode: PASS=14 FAIL=0 =====
```

### 3.2 判定

| 文档写死的数 | 实测 | 结论 |
|---|---|---|
| `XAML ≤ 13` | 13 | ✅ 未过时，余量 0 |
| `CsUi ≤ 822` | **822** | ✅ **未过时；TESTING.md 与脚本 `$BASE_CS_UI=822`、实测三者精确一致** |
| 脚本基线 `$BASE_XAML=13` | 13 | ✅ |
| 「余量仍 0」⇒ 14/0 | PASS=14 FAIL=0，exit=0 | ✅ |

**本项通过。** 但顺带发现两个脚本侧问题（P2-12 / P2-13）：

- **信息桶基线已脱节**：脚本头仍写 `$BASE_CS=1320 / $BASE_CS_TAG=459 / $BASE_CS_OTHER=861`（`_probe-cjk-hardcode-scan.ps1:438-441`），
  实测 1359 / 487 / 872，脚本自己打印**负余量**。这三桶已按 ②-b 退出判据（不判红），故**不是门禁失效**，但账本描述与实测不符。
- **注释块时序倒挂**：「2026-09-26 第八次抬基线（832→837）」的段落被排到了「2026-10-04 抬基线（821→822）」**之后**，
  紧接着 `$BASE_CS_UI = 822`。读者按行序会读成 837 是最后一跳 ⇒ 误导。

---

## 4. `docs/RAW2TIF_QA_DEFECT_2026-10-03.md` 数字证据核对表

| # | §7.5 声称 | 可复现? | 证据（实测命令 / 输出摘要） |
|---|---|---|---|
| 1 | 四项目重建 **0 警告 0 错误** | ✅ **复现** | `dotnet build ffmpegPictureUI.sln -c Release --no-incremental` → `已成功生成。0 个警告 0 个错误`（55 s） |
| 2 | 探针 **25/25 = 669 pass / 0 fail / 1 skip** | ✅ **精确复现** | 对 `tests/output/t84/w3a-2026-10-04.log` 正则求和：25 个 mode，`Σpass=669 Σfail=0 Σskip=1`；mode 清单含 `avifgm`（第 25 个） |
| 3 | 受管色彩子集 **7/7 全绿** | ✅ **复现** | `subset-color-2026-10-04.log`：7 条脚本全 `exit=0` —— `color-wiring 142/0`、`color-strategy 78/0`、`colorfmt PASS=2 FAIL=0 WARN=4`、`prophoto-faithful 27/0`、`gainmap-engine 140/0`、`color-engine-xcheck 全通过`；`decision-delivery 87/0` 取自 `subset2-2026-10-04.log`。**7 个数字全部对上** |
| 4 | L3 五套 **17/0 · 100/0 · 28/0 · 13/0 · 19/0** | ✅ **精确复现** | `l3pkg-2026-10-04.log` 与 `l3pkg2-2026-10-04.log` 完全一致，且每行均取到 `SUBJ(被测) = v1.6.0-beta4 (L3 出货包)` |
| 5 | **整轮 65/65 全绿**（10/10/5/5 分四窗，非零退出合计 0） | ❌ **不可复现，且与日志矛盾** | 四窗日志 `w1(10) + w2(10) + w3a(5) + w3b(5)` 唯一并集 **= 30 条**；每份日志结尾 **自陈**「运行器汇总（-Scripts 子集 10/65 条 ⇒ **不构成基线**）」。后续补跑 `w12b(20)+w34b(10)`、`w12c(20)+w34c(10)`，与四窗并集仍为 **30/65**。⇒ 文档把「子集全绿」写成了「整轮 65/65」 |
| 6 | `pack.ps1 -Mode all` ⇒ **exit=0** | ⚠ **间接佐证，退出码未留痕** | `pack-2026-10-04.log` / `pack2-2026-10-04.log` 显示 `[app]` 与 `[full]` 两版均「打包完成」并出 `FFmpegPictureUI-v1.6.0-beta4-x64{-full}.7z` ⇒ 与 `-Mode all` 行为一致；但日志**未记录 exit code**，无法直接验证 `exit=0` |
| 7 | `verify-release-package` ⇒ **16/0** | ❌ **证据缺失** | `grep -rln 'verify-release-package' tests/output/` → **0 命中**；该脚本也**不在** 65 条门禁清单中（`_run-step3-gates.ps1` 清单里只有 `verify-package-smoke.ps1`）。日志里出现的 `PASS=16` 属 `verify-color-caps` / `verify-png3-interop`，是别的脚本另一个轮次 |
| 8 | CJK UI 面 821 → 822 ⇒ **14/0** | ✅ **复现** | 见 §3.1 实测输出 `PASS=14 FAIL=0`、exit=0 |
| 9 | `_lib-matrix` 行号锚 +11 漂移 ⇒ **8/0** | ✅ 行号对齐 / ⚠ 计数未独立复跑 | 4 条 pin 与当前代码**逐字对齐**：`FfmpegCommandBuilder.cs:977 / 987 / 1019 / 2061` 全部 OK（`_lib-matrix.ps1:390,405,631,652`）。`8/0` 的条数未另行实跑，登记为「未验证」 |
| 10 | 变异验证 **M1**（ColorTrc 恒 srgb）/ **M2**（去掉 `|| rawIsTiff`）两次均原样撤回 | ⚠ **未验证** | `round-rawtier-2026-10-04.log` 中检索 `变异|mutation|M1|M2|撤回` **0 命中**；无留痕 ⇒ 不可事后核验（不否认其发生） |
| 11 | **5 条 CS8602 已清零**（全量 `--no-incremental` = 0 警告） | ✅ **计数精确复现** | 见 §4.3 |
| 12 | §7.7 仍未做：提交 / tag / push | ✅ **属实** | `git status`：20 个已跟踪文件仍未提交；`git tag` 最新为 `1.6.0BETA3` / `1.6.0`，**无 beta4 标签** |

### 4.1 日志路径核查（正面结果）

Guest 提到的 `tests/output/t84/*.log` **路径真实存在**，本轮相关日志共 16 份，包括全部四窗 + 补跑 + 色彩子集 + 出包 + L3 复跑。
（初检时 `ls | head -50` 截断导致遗漏，全文检索后确认齐备 —— 记此以免误报。）

### 4.2 内部口径冲突（P1-4 / P1-5）

| 处 | 口径 | 互斥对象 |
|---|---|---|
| `:256` 附二 §5.1 B 档：「不能承载 HDR 传递、但能带任意 ICC：**`tiff` / `webp` / `jpg` / `jpegli`** ⇒ Rec.2020 + SDR」 | 把 webp/jpg/jpegli 与 tiff 同档 | `:282` §7.1「其余（webp / jpg / jpegli / gif / jxr / ppm…）⇒ bt709 + sRGB」、`CHANGELOG.md:27`、实装 `QueueProcessor.cs:606-612` |
| `:269` 附二 §5.2 判定：「**TIFF 按已定口径钳成 `bt709 + sRGB`**」 | TIFF 落到 bt709 | `:281` §7.1「TIFF（唯一例外）⇒ Rec.2020 + SDR」、实装同上 |

两处都**没有被标注为「已被 §7.1 取代」**。附二写在用户裁定之前属可理解的时间顺序，但同一读者在同一份 373 行文档里
会读到两组互斥结论，且没有一处「本大纲以后面的为准」的交叉引用 ⇒ 须报。

`CanHdr` 语义本身是**自洽**的：`:264-265` 说明 `CanHdr` 只管「HDR 传递」、Rec.2020 由 `CanCarryArbitraryIcc` /
`CanGenerateIccFromCicp` 承担，与代码 `QueueProcessor.cs:600` 用 `rawCaps.CanHdr`（不叠 GainMap）一致 ✅。

### 4.3 自陈疑点：「`FfmpegOptions.Format` 为何被判可能为空」

**文档状态**：`:340-341` 仍写「看起来非空…**尚未查明** ⇒ 登记为待单独追查的疑点」。
全文检索 review 目录下所有文档与本批改动，只有这一处提及 ⇒ **确属遗留、本轮未解决**。

**但本轮实测把它收窄到了一个具体位置**（新证据，文档应予更新）：

```
实验（%TEMP% 副本）：把 QueueProcessor.cs 中 13 处 `Format!.` 的 `!` 全部去掉后重建
  → 恰好 = 5 条 CS8602：QueueProcessor.cs(749,36) (762,36) (779,36) (785,37) (1040,43)
  → 加回 `!` 后 = 0 警告 0 错误          （⇒ 「5 条」这一计数精确成立）
再实验：在该方法内插入探针 `var _csProbe = captured.Options.Format.Length;`
  → 位于 `await RawService.PreProcessAsync(...)`(QueueProcessor.cs:545) **之前**的那条 ⇒ **不告警**
  → 位于该 await **之后**的那条 ⇒ **告警**；插入后原 5 条告警全部消失
     （首次解引用后 Roslyn 把该 slot 学会为 not-null）
```

⇒ 结论定位：**可空状态是在 `QueueProcessor.cs:545` 那条 `await` 之后被 Roslyn 判为 maybe-null 的** ——
该 await 把一个会改写 `captured` 的回调 lambda（`s => { captured.Log += s; ... }`）交给了外部方法。
这与「声明所致」无关：`FfmpegOptions.cs:145` 声明为 `public string Format { get; set; } = "jpg";`（非空 + 有初值），
`Models/QueueItem.cs:11` 的 `Options` 同样非空且有初值，`:158-160` 遍历的是 `List<QueueItem>`（元素非空）。

⚠ 定性说明：这是**经验定位**（两个对照实验互相印证），不是编译器内部机制的证明；但其兄弟结论 ——
`!` 不产 IL、行为零改变、现值确为 0 警告 —— 已由 §4 第 1 项独立确证。建议把 `:340-341` 的
「尚未查明」改写为上述定位，而不要再当作完全未知的悬案。

### 4.4 改动规模登记口径（P2-11）

`:297` 称「6 文件 / **+119 −21**」。实测（`git diff --numstat src/FfmpegGui`）：

| 文件 | ± |
|---|---|
| `Models/FfmpegOptions.cs` | +13 / −0 |
| `Services/FfmpegCommandBuilder.cs` | +11 / −0 |
| `Services/ColorMapping/ColorIntentFactory.cs` | +29 / −5 |
| `Services/QueueProcessor.cs` | +55 / −27 |
| **表列 4 文件小计** | **+108 / −32** |
| `Services/ColorMapping/ImageEncoderArgs.cs`（注释性质） | +3 / −3 |
| `Services/IsoBmffGainMapWriter.cs`（P0-2 修复，**不属于** RAW 三档） | +39 / −8 |
| **src 全部 6 文件** | **+150 / −43** |

⇒ 「6 文件」把不属于本改动的 `IsoBmffGainMapWriter`（+39/−8）算进来了；而表格正文只列 **4** 个文件。
没有一个口径能得出 +119 / −21。

---

## 5. `docs/review-2026-10-03/` 分线报告交叉一致性

### 5.1 README §1.5 —— 「未被登记归因」文案逐条 grep（P1-7）

README `:76-88` 列出这批文案，主张它们在本批里悄悄进来了且没被归因。**当前代码中全部仍在，且 0 条进入 locale json**：

| 文案（节选） | 现状 grep 结果 | 是否进 locale |
|---|---|---|
| `JPEG 族与 AVIF 可承载 GainMap…` | 命中 `ColorStrategyPlanning.cs:290` | ❌ `Resources/Locales/*.json` 0 命中 |
| `增益图（GainMap）不适用于 {pol.Format.Format}…` | 命中 `ColorStrategyPlanning.cs:285`（另 `QueueProcessor.cs:868` 同族） | ❌ |
| `E1 实测：avifenc --icc ✅ / --icc+--nclx 共存 ✅…` | 命中 `ColorTransformPlan.cs:589`（`:579` 注释同内容） | ❌ |
| ` ⇒ 不回退 PPM 管道…` | 命中 `RawColorPipeline.cs:544` | ❌ |
| ` ⇒ 仍走 PAM/PPM 管道…` | 命中 `RawColorPipeline.cs:521` | ❌ |
| `cjxl 出口：PNG 中转落盘失败…` | 命中 `RawColorPipeline.cs:543` | ❌ |
| `带 alpha` | 命中 `RawColorPipeline.cs:520` | ❌ |
| `（PPM/PAM 不携带元数据，且 exiftool 补写 JXL 必然失败）\n` | 命中 `RawColorPipeline.cs:522` | ❌ |

⇒ README §1.5 的**主张至今成立**。本轮只做了一件事（把基线由 840 改回 821、再抬到 822），
既没有把上述文案搬进 locale，也没有补那句自陈「`+11` 逐行明细未做」（`_probe-cjk-hardcode-scan.ps1:311-312` 仍挂着这句 ⚠）。

⚠ 需公允表述：其中 `RawColorPipeline.cs` 那几条语义上是**诊断/裁决文案**，按 README `:90` 自己也承认是
②-c fail-closed 分桶的误判。**所以这不该被解读成「有人违规硬编码了 UI 文案」**，准确说法是：
「§1.5 指出的事实未被处理（既未归因、也未搬 locale、也未收紧分桶规则）」。

### 5.2 README 的 file:line 引用是否还对得上

| README 引用 | 现状 | 判定 |
|---|---|---|
| `ImageEncoderArgs.cs:69-77`（P1-3 反函数） | `:69` 仍是 `MapJxlDistanceInverse` 的 `<summary>` | ✅ |
| `ColorIntentFactory.cs:47-54`（源描述符） | `:47` 仍是「源描述符：外部 ICC → 内嵌 ICC → 容器 CICP…」 | ✅ |
| `RawService.cs:175`（dngtool 命令行） | 逐字命中 `-d -T -o 0 -q 3 -W -H {highlightMode} -6 …` | ✅ |
| `IsoBmffGainMapWriter.cs:78`（原 `bool baseFullRange = true`） | 已漂移：该参数现为 `bool baseFullRange = false`，在 **`:82`** | ❌ 漂移 |
| `IsoBmffGainMapWriter.cs:124` / `:259-260`（PatchColr 两处） | 已漂移：`PatchColr` 现 **`:268`**，签名也已改为返回 `bool`；新增 `ReadNclxFullRange` 在 `:292` | ❌ 漂移 |
| `QueueProcessor.cs:562-578`（旧 ColorTrc=linear 块） | 该区域已被本轮整体重写，`:562` 现是「中间件是 dngtool 的去马赛克线性输出…」新注释 | ❌ 已重写 |

漂移是**必然且合理**的（本批正是修改了这些文件），但 README 作为被 `JXL_RAW_16BIT_ADVISORY` 显式引用的凭证
（该表新增的 G 行指向 `README.md §3.2`），其行号引用应当加时间戳或改为引用符号名。

### 5.3 与后续文档的结论冲突

| 处 | 主张 | 与本轮现状冲突? |
|---|---|---|
| `README.md:20` P0-1「✅ 主复核人实证 + 变异验证」 | 基线应 840→**821** | 本轮已执行且再抬到 **822** ⇒ README 建议已过期，未回填（P2） |
| `README.md:22` P0-2 AVIF colr.full_range | 建议 `GainMapEncoder.cs:621` 补传参数 **或** 默认值改 false | 本轮采用了**改默认值 + PatchColr 只读继承**这条路（`IsoBmffGainMapWriter.cs:82,268`）⇒ README 建议已落地，未回填状态 |
| `README.md:215` 建议 4（登记 dngtool 公式） | 应写进 `JXL_RAW_16BIT_ADVISORY`「刻意未动」清单 | ✅ **本轮已执行**：`docs/JXL_RAW_16BIT_ADVISORY_2026-10-02.md:31` 新增 G 行，并指向 §3.2 —— **这条闭环了** |
| `README.md:24` P1-5 假绿 6 条 | 「子线 C 核实，**未实跑**」 | 本批 8 个 ps1 有改动；本线未重跑门禁 ⇒ **未验证**（不在本次必查范围） |
| `HANDOVER-修复交接.md:122` | 「CJK 门禁：XAML ≤13 / CsUi ≤**832**」 | ❌ 与现行 **822** 冲突（P2-9）。但该文件自称 2026-10-03 07:46 的**快照**，建议加「已过期」标注而非改内容 |

### 5.4 文档本身的质量问题（P2）

`README.md` 存在明显的生成损坏/乱码，例如：
`:14`「三个 Directions 的 brace **^{\Large僭}** ── 一句话总结」、
`:16`「owning 新写**土地使用权触发器** 660 行新文件…**一绿色发展位存差不齐**」、
`:36`「唯一唠话杂声皆因 estimator 一致」、`:43`「不能 variance 一致」、`:90`「(ExceptionІ 性质」。
这些句子已影响可读性，且 `README` 是被 `JXL_RAW_16BIT_ADVISORY` 引用的凭证文件 ⇒ 建议重生成或修复。

---

## 6. 版本号一致性表

| 位置 | 值 | 与 `v1.6.0-beta4` 一致? |
|---|---|---|
| `CHANGELOG.md:15` | `### v1.6.0-beta4 (2026-10-01)` | ✅（日期偏旧，见 P2-14） |
| `CHANGELOG.en.md:16` | `### v1.6.0-beta4 (2026-10-01)` | ✅ 与中文件一致 |
| `src/FfmpegGui/FfmpegGui.csproj:19` | `<Version>1.6.0-beta4</Version>` | ✅ |
| `FfmpegGui.csproj:25-26` | `AssemblyVersion/FileVersion = 1.6.0.0` | ⚠ 字面不同，但 .NET AssemblyVersion 不含预发布后缀，属**规范使然，非缺陷** |
| `README.md:5` 与 `:214` | **v1.6.0**（2026-09-28） | ❌ 未升到 beta4 |
| `README.en.md:5` 与 `:221` | **v1.6.0**（2026-09-28） | ❌ 同上（但中英彼此 ✅ 一致） |
| `publish/` 目录与 7z | `FFmpegPictureUI-v1.6.0-beta4-x64` / `-full` | ✅（另留 beta1~3 与 `v1.6.0-x64` 旧产物） |
| `tests/output/t84/*2026-10-04*.log` 的 SUBJ | `v1.6.0-beta4 (L3 出货包) ProductVersion=1.6.0-beta4+bdd4b0b…` | ✅ |
| `docs/TESTING.md` | 文中**未出现**版本号 | — 无冲突 |
| `docs/RAW2TIF_QA_DEFECT_2026-10-03.md:363` | 复现命令里的 exe 路径 `…-v1.6.0-beta4-x64-full/…` | ✅ |
| `.workbuddy/memory/MEMORY.md:32-34` | CJK 门槛仍记 **821** | ❌ 实际 **822**（P2-8） |
| `git tag` | 最新 `1.6.0BETA3` / `1.6.0`，**无 beta4** | ❌ 未打（§7.7 自陈属实）；另 **tag 命名风格** `1.6.0BETA3` vs 包名 `v1.6.0-beta4` 不统一 |

---

## 7. 建议（按性价比排序）

1. **改 §7.5 的「整轮 65/65 全绿」** 为准确表述：「四窗子集 30/65 全绿（每份日志自陈不构成基线）；未跑全量」。
   或补跑剩下 35 条再声称 65/65。**这是本次唯一会误导读者相信已全面回归的表述。**
2. **给两个 CHANGELOG 补一条**「AVIF 增益图 `colr.full_range` 修复」（`avifgm` mode + `baseFullRange` 默认值翻转 +
   `PatchColr` 只读继承），中英同步。**这是本批唯一的用户可见行为变更未登记项。**
3. **处理 `:256` 与 `:269` 两处过期口径**：显式标注「已被 §7.1 用户裁定取代」，并把附二 §5.1 的 B 档收窄为「仅 `tiff`」，
   与 CHANGELOG / 代码对齐。
4. **更新 `:340-341` 的 CS8602 疑点**为 §4.3 的定位（await 边界重置），并把「尚未查明」改为「已定位到位点，待确认是否为 Roslyn 预期行为」；
   同时**附上那两条对照实验**（去 `!` ⇒ 5 条；await 前后探针 ⇒ 前不告警、后告警）——它们是可复现证据，值得写进文档。
5. **补齐 CJK 侧账本**：把 `MEMORY.md:32-34` 的 821 → **822**；同步 `_probe-cjk-hardcode-scan.ps1` 里三个信息桶基线
   （1320/459/861 → 1359/487/872）或注明「已按 ②-b 退出判据、不再维护」；把倒挂的注释块排回时序。
6. **README / README.en.md 升到 v1.6.0-beta4**（或显式声明「README 只记稳定版 lineage」并在 CHANGELOG 里同步该约定）。
7. **`verify-release-package` 补一次留痕**：它目前跑完没有任何日志落盘（`tests/output` 0 命中），与「无留痕 = 未发生」的本仓口径冲突。
8. **`pack.ps1` / M1/M2 变异验证补日志**：把 exit code 与变异的「转红→转绿」输出落到 `tests/output/t84/*.log`，便于事后核验。
9. §7.6 的 `--color-space` 覆盖修复：在 CHANGELOG「用户显式选择仍以用户为准」那句补一句「此前非高级模式的 `ColorSpace`
   会被 RAW 默认块覆盖，本轮修复」，否则读者无法知道这条路径曾经是坏的。
10. review README 的乱码文本与已漂移的 file:line 引用（改用符号名或加取样日期）择机修复。

---

## 附：本次取证命令留痕

```bash
git status --short && git log -1 --format="%H %ad %s" --date=iso
git diff --numstat src/FfmpegGui/ tests/ServiceProbe/
dotnet build ffmpegPictureUI.sln -c Release --no-incremental          # → 0 警告 0 错误
# CJK 门禁（%TEMP% 副本，语料与仓库逐文件 diff 一致：83 .cs + 7 .xaml）
pwsh -NoProfile -File <temp>/tests/scripts/_probe-cjk-hardcode-scan.ps1   # → PASS=14 FAIL=0, exit=0
# CS8602 复现（%TEMP% 副本，去 13 处 `!`）→ 恰 5 条 CS8602 @ QueueProcessor.cs 749/762/779/785/1040
# 探针插入实验 → await QueueProcessor.cs:545 之前不告警 / 之后告警
# 日志校核：tests/output/t84/{w1,w2,w3a,w3b,w12b,w12c,w34b,w34c,pack*,l3pkg*,subset*,round-rawtier}-2026-10-04.log
```
