# C — 门禁脚本可靠性复查（2026-10-04 轮）

- **复查对象**：HEAD `bdd4b0b` 之后**全在工作区（unstaged）**的那批改动中的门禁/测试脚本部分
  （`tests/scripts/**` 8 个 ps1 + `tests/ServiceProbe/Program.cs`）。
- **基线**：上一轮 `docs/review-2026-10-03/README.md` §1（P0-1）、§3.3（六条假绿）、
  `docs/review-2026-10-03/C-门禁脚本可靠性.md`（S1–S5 / M1–M12 / L1–L5）、`HANDOVER-修复交接.md`。
- **方法**：**不采信提交说明与既有结论**。每条锁都做「改坏→转红、改回→转绿」的**变异验证**，
  全部在 `C:/Users/20210/AppData/Local/Temp/rev-*` 的**副本/镜像**上跑（junction 挂 `publish/PLAN`、
  `bin/Release`、`tools/...`；`tests/output/**` 用镜像内的独立目录），**仓库零改动**。
- **宿主**：pwsh **7.6.6**（实跑）；PS **5.1.26100.9549**（静态解析）。

---

## 〇、结论速览

| 级别 | 条目 | 结论 | 变异验证 |
|---|---|---|---|
| ✅ | **P0-1 CJK 门禁基线**（上轮放宽 19 行） | **已修好**：`$BASE_CS_UI` 840 → **822**，实测 **822**，**余量真的 = 0**，刀口 = +1 行 | ✅ 有（二分到 +1） |
| ✅ | 六条假绿中的 **6/6** | **全部真的修到位**（不是"改个注释"） | ✅ 每条都有 NEW/OLD 对照变异 |
| ⚠ P1 | `verify-colorfmt-matrix` 只修了一半 | M2（参照缺失 fail-closed）已修；**M3 结构性绿灯仍在**（基线 `PASS=2 FAIL=0 WARN=4`，⚠ 不计 FAIL） | ✅ 有 |
| ⚠ P1 | `verify-encoder-knob-liveness` 新闸 **余量 0** | `$minLive = 19` = 当前实测值 ⇒ 任一臂正常波动即红；形态与刚治好的 CJK 同款 | ✅ 有（纯色夹具实测掉到 18 ⇒ 红） |
| ⚠ P2 | CJK 新归因注释**机制写错**（结论方向对） | `[RAW] …` 走的是 **[tag] 桶**，不是注释说的「`Log +=` sink ⇒ Diag」 | ✅ 有（分桶实测定案） |
| ⚠ P2 | 上轮 M4/M5/M6/M10/L1/L2 **未返工** | `jbrd:240` 空值恒真、`jbrd:163` `$null.FullName`、`gainmap3p:141` 裸否定式、`orientation` 三处 `throw`、`package-smoke` 无显式 `exit 0` | —（本轮 diff 未涉及） |
| ✅ | `_lib-matrix.ps1` 4 个行号锚 **+11** | 977 / 987 / 1019 / 2061 **全部命中预期行且字面量唯一**；全库 26 个 pin 审计 **25 OK + 1 条是 B4 负控（故意错行）** | ✅ 有（自写审计脚本） |
| ✅ | **PS5.1 静态解析** 9 个受检文件 | **9/9 PARSEOK、0 解析错误**，且无裸 `&&` / `||` / `??` / `??=` / `?.` / `?[` / 三元 | ✅ 有（真 5.1 宿主解析） |
| ✅ | `tests/ServiceProbe/Program.cs`（+129） | 新增 `avifgm` 子命令（P0-2 AVIF `colr.full_range` 的永久锁）+ JXL 反函数往返锚点（P1-3）；**无 fail-open**，临时目录 `finally` 清理 | 部分（未实跑，见 §六） |
| ✅ | `_run-step3-gates.ps1`（+10） | `avifgm` 接进默认 `$Probes`（第 25 个 mode），mode 名 == ServiceProbe 子命令 ⇒ 接线自洽 | ✅ 有（读码核对分发逻辑） |

---

## 一、CJK 硬编码门禁专章（最高优先级）

### 1.1 当前基线与实测读数

- 基线：`tests/scripts/_probe-cjk-hardcode-scan.ps1:339` → **`$BASE_CS_UI = 822`**（上轮是 840）。
- 归因注释：`:300`（2026-10-03 复核更正段，作废旧的 837→840 说法）、`:330-337`（2026-10-04 抬基线 +1 段）。

实跑 `pwsh -File tests/scripts/_probe-cjk-hardcode-scan.ps1`（仓库原位、**只读**）：

| 桶 | 基线 | 实测（当前工作区 src） | 实测（HEAD `bdd4b0b` src） | 余量 |
|---|---|---|---|---|
| XAML/AXAML（判据） | 13 | **13** | 13 | 0 |
| **C# UI 面（唯一判据桶）** | **822** | **822** | **821** | **0** |
| C# 总计（ℹ） | 1320 | 1359 | 1355 | −39 |
| [tag] 桶（ℹ） | 459 | 487 | 487 | −28 |
| 其余桶（ℹ） | 861 | 872 | 868 | −11 |
| 诊断 sink（ℹ，不判） | — | 50 | 47 | — |

⇒ `PASS=14 FAIL=0 / exit=0`。**余量真的是 0**（上轮是 19）。HEAD 语料实测 821 与本轮工作区 822 的差 = +1，
与作者登记的那一行中文描述符名**完全吻合**（见 §1.4）。

### 1.2 刀口值（变异验证，二分定位）

变异方式：在**临时副本**（`/tmp/rev-cjk/wt`，`src/FfmpegGui` 全量副本 + 脚本副本）里新增
`src/FfmpegGui/_MutCjkProbe.cs`，注入 N 行中文 UI 字面量（`public const string S1 = "中文界面文案第1条";`）。

| 变异 | UI 面读数 | 结果 |
|---|---|---|
| 无变异（当前工作区） | 822 | `PASS=14 FAIL=0`，**exit=0** |
| **+1 行** | **823** | `PASS=13 FAIL=1`，**exit=1** ← 刀口 |
| +2 行 | 824 | `PASS=13 FAIL=1`，exit=1 |
| +19 行 | 841 | `PASS=13 FAIL=1`，exit=1 |
| 删除临时文件（改回） | 822 | `PASS=14 FAIL=0`，exit=0 ✔ 转绿 |

**刀口 = 822/823**，即「新增 1 行硬编码中文 UI 即红」。三者关系：
**刀口 823 − 实测 822 = 1；基线 822 − 实测 822 = 0（余量 0）；脚本自述「余量 0」成立**。

### 1.3 与旧基线的对照（证明上轮的 19 行免罚额度确实被收回）

同一变异、把脚本副本的基线改回 840 再跑：

| 变异（+N 行） | 旧基线 840 | 新基线 822 |
|---|---|---|
| +18 行（UI=840） | `PASS=14 FAIL=0`，**exit=0**（一声不响） | `PASS=13 FAIL=1`，**exit=1** |
| +19 行（UI=841） | `PASS=13 FAIL=1`，exit=1 | `PASS=13 FAIL=1`，exit=1 |

⇒ **旧基线静默吸收 18 行，新基线第 1 行就红**。P0-1 可以关闭。

### 1.4 归因注释复核

注释 `:330-337` 称该 +1 = `ColorIntentFactory` 里
`ColorSpaceDescriptor.FromCicp(..., "RAW 中间件（dngtool 去马赛克线性输出）")` 的 `name` 实参。
**实测结论：成立。**

| 变异 | UI 面 |
|---|---|
| 无变异 | 822 |
| 把 `"RAW 中间件（dngtool 去马赛克线性输出）"` 改成 ASCII | **821** |
| 改回 | 822 |

（`grep -n 'RAW 中间件（dngtool'` 在 `src/FfmpegGui` 内唯一命中 `ColorIntentFactory.cs:87`，其余命中都是 `//` 注释行 ⇒ 注释给的复核方式有效。）

### 1.5 ⚠ 但新注释里有一处**机制写错**（P2）

注释 `:336` 称：「同批其余新增中文均**不计**：`QueueProcessor` 的 `captured.Log += $"[RAW] …"` 走 `Log +=` sink ⇒ 记 Diag。」

**实测：不成立（机制层面）。** 分桶代码（`:413-418`）先判 `$body.TrimStart().StartsWith('[')` ⇒
`[RAW]` 前缀的行进的是 **[tag] 桶**，根本走不到 sink 判断；只有它的**续行**（不以 `[` 开头）才靠 sink 判成 Diag。

实测证据（副本里注入临时 `.cs`，看哪个桶 +1）：

| 注入行 | TAG | OTHER | DIAG | UI |
|---|---|---|---|---|
| 基线 | 487 | 872 | 50 | 822 |
| `a.Log += "[RAW] 中文标签测试";`（有 `[` 前缀 + sink） | **488** | 872 | 50 | 822 |
| `a.Log += "中文无括号前缀测试";`（无 `[` 前缀 + sink） | 487 | **873** | **51** | 822 |

⇒ **结论方向没错**（这两类都不进 UI 桶、不影响判据），但**机制描述是错的**，与上轮被抓到的
「+16 来自 `tests/ServiceProbe`」是同款「归因看起来很具体、其实不成立」。建议把 `:336` 改成
「`[RAW] …` 按 `StartsWith('[')` 落 [tag] 桶；其续行按 sink 落 Diag」。

### 1.6 其余观察

- **ℹ 桶基线未同步**：`$BASE_CS` 1320 / `$BASE_CS_TAG` 459 / `$BASE_CS_OTHER` 861 相对实测 1359/487/872
  已「超基线 39/28/11」。按 ②-c 口径它们**不参与判据**（有 `:428-433` 反控守着），但脚本会打印
  「余量 −39」，读者容易误判。建议按实测一并对齐，或在这几行显式标注「ℹ 桶不维护」。
- **注释时序倒挂**：2026-10-04 段（`:330`）被插在 2026-09-19（`:327`）与 2026-09-26（`:338`）两段之间，
  且 `:315` 刚说完「改回 **821**」、`:339` 却是 **822**。建议把 2026-10-04 段挪到 2026-10-03 更正段之后。

---

## 二、六条门禁假绿复核表（逐条实跑 + 变异）

> 变异一律在 `/tmp/rev-gates/m/` 镜像里做：NEW = 当前工作区脚本；OLD = `git show bdd4b0b:` 取回的上一版。
> 同一变异同时打在 NEW 与 OLD 上，对比退出码与 FAIL 数 —— 这就是「修复有没有牙」的判据。
> 各脚本**基线（未变异）实跑均全绿**：orientation `exit=0 PASS=28`（50s）、package-smoke `exit=0 PASS=17`（56s）、
> knob `exit=0 PASS=19 未判=1`（153s）、colorfmt `exit=0 PASS=2 WARN=4`（41s）。

| # | 上轮条目 | 本轮改动 | 变异设计 | NEW（修复后） | OLD（修复前） | 判定 |
|---|---|---|---|---|---|---|
| **1** | `verify-orientation-rewrap.ps1:136-141` ffprobe 读空 ⇒ `$dimProd=''` ⇒ 「已旋正」+「标签已剥」**双假绿** | 新增 `$dimOk` 空值前置（`$dimProdN` 形如 `N,N`）+ **归一化**（avif 那格实测吐 `480,640,`）+ 每条判据都合取 `$dimOk`，并新增「源码流尺寸读得到」 | 强制 `$dimProd = ''`（模拟读取失败），打在 NEW/OLD 各一份 | **exit=1，PASS=11 FAIL=17**：6 臂 × 3 条全部点名判红 | exit=1 但 **PASS=19 FAIL=1**：5 条 drop 臂的 **10 条判据全假绿**，只有 jbrd 臂因 `'' -eq '640,480'` 偶然为假才转红 | ✅ **修好**（上轮那 10 条假绿现在全红并点名） |
| **2** | `verify-orientation-rewrap.ps1:161-165` B 段依赖 gitignored 夹具，缺则 `continue`+SKIP，0 断言仍全绿 | 夹具缺失 ⇒ `CK $false` **判红并点名**；末尾新增 **B 段条数下限 ≥6** | 把 B 段两个夹具路径改成不存在 | **exit=1，FAIL=3**：`B 8bit：夹具在位`、`B 16bit：夹具在位`、`B 段断言条数下限 实得 2 条` | **exit=0，PASS=14 FAIL=0**，B 段一条断言都没有 | ✅ **修好**（这是本批最干净的一条证据） |
| **3** | `verify-package-smoke.ps1:95/99/100/129` 4 条**裸否定式**日志断言，日志为空恒真 | 新增 `CmdOf`（两趟取命令行，先认 `[cmd]`/`命令行:` 再退化）+ `Seen`，B4/C1/C2/E3 全部与「取到了」**合取** | `Run` 里把 `$log` 恒置空（模拟日志捕获失败） | **exit=1，PASS=11 FAIL=6**：B4/C1/C2/E3 全部 FAIL，detail 里打「**FAIL 前置：cjxl/ffmpeg 命令行没取到 ⇒ 否定式判据无从成立**」 | exit=1 但 **PASS=15 FAIL=2**：只有肯定式的 B1/E2 红，**B4/C1/C2/E3 四条照旧 PASS** | ✅ **修好**（4 条假绿全部转红） |
| **4** | `verify-encoder-knob-liveness.ps1:226-232` 全 `undecidable` 仍 exit 0 | 新增两条闸：`活臂数 ≥ $minLive(19)`、`未判 ≤ 已判臂数/3`，都计入 `$nFail` 并进 `$hard` | 把 `Rec` 里所有 `live` 强制改成 `undecidable`（= 夹具整体失去判别力的退化形状） | **exit=1，FAIL=2**：`活臂数下限：活=0 < 19`、`未判比例上限：未判=20 > 7` | **exit=0，PASS=0 FAIL=0，未判=20** —— 20 条全判不出仍绿灯 | ✅ **修好** |
| **5** | `verify-encoder-knob-liveness.ps1:209`「崩溃即通过」（`$honest = rc≠0 -or 播报`） | 新增 `crashed` 态（输出全空 / 退出码非 0 非 1 / rc≠0 却无播报无拒绝证据）⇒ 记 **FAIL**，另加 `$rejectEvidence`（`Unable to parse|Error setting option|不支持…`） | N 段每臂把 `$z` 换成 `code=-2147450750, log=''`（进程根本没起来） | **exit=1，FAIL=4**：3 条 N 臂记 `[crashed]` 并点名；`活=16 < 19` 又红一次 | **exit=0，PASS=19 FAIL=0**：3 条崩溃臂全记 `[live]` ⇒ **崩溃即通过实锤** | ✅ **修好** |
| **6** | `jbrd:29` / `gainmap3p:38` / `knob:40`（含 `orientation` 同形）`T76_EXE/T77_EXE/T79_EXE` 在版本护栏**之后**覆盖 `$exe`，不复验不打 SUBJ | 覆盖后**重跑存在性 + ProductVersion + 与 `PKG_VER` 匹配**三道，并补打 `SUBJ(被测,覆盖后)` | ① `PKG_VER=1.6.0-beta4` + `T76_EXE=<beta3 包内 FfmpegGui.exe>`；② `T*_EXE=<不存在的路径>`；③ 合法覆盖（beta4 配 beta4） | ① **exit=2** 并点名：`期望 1.6.0-beta4* 实得 1.6.0-beta3+…`（orientation/jbrd/gainmap3p/knob 四个文件**全部**如此）<br>② **exit=2**：`覆盖的被测二进制不在位 ⇒ 拒绝跑`<br>③ **exit=0，PASS=28**，打印 `SUBJ(被测,覆盖后)` ✔ | ① **exit=0，PASS=20 FAIL=0**：拿 **beta3 二进制跑完全程**，首行 SUBJ 仍写着 `v1.6.0-beta4` —— 版本错配 100% 静默绕过<br>② jbrd/knob 直接 `throw 缺工具:…`（无任何护栏信息），gainmap3p 靠下游 `G0` 拦（exit=2 但信息无关） | ✅ **修好**（含「改回→转绿」：合法覆盖放行） |

### 2.1 附：第 4/5 条的一个**新风险**（P1）

`verify-encoder-knob-liveness.ps1` 的 `$minLive = 19` 是**照当前实测值写死**的（基线实测 `活=19`），
即 **余量 0** —— 与刚治好的 CJK 是同款形状。实测副作用：把夹具换成纯色 PNG（脚本头自陈的退化形状）后
`活` 掉到 **18** ⇒ 新闸立刻判红（`FAIL  活臂数下限：活=18 < 19`）。这次红的**原因本身是真的**
（gif 臂真 inert），但说明这条闸**没有为正常的臂数波动留任何余量**，换机/换 ffmpeg 版本极易假红。
建议：`$minLive` 取 `实测 − 2`（或按 `$wantArms` 的比例算，如 `≥ 已判臂数的 2/3`），并把取值依据写进注释。

### 2.2 附：colorfmt 只修了一半（P1）

| 变异 | NEW | OLD |
|---|---|---|
| 参照 ICC `$refIcc` 指到不存在的路径 | **exit=1**，0 s：`❌ 参照 P3 ICC 不在位…` + `PASS=0 FAIL=1 WARN=0` | **exit=0**（41 s）：`参照 P3 ICC md5 = 无参照`，其余与基线**逐字相同**（`PASS=2 FAIL=0 WARN=4`） |

M2 已修好（fail-closed）。但**基线本身就是 `PASS=2 FAIL=0 WARN=4`**：6 格里 4 格是 `⚠ 有 ICC 但非 P3 参照`，
而 `⚠` **不计入 FAIL**（`:72` / 汇总行）。⇒ 上轮 M3 说的「表格型 ⇒ 结构上永不可能失败」**只修了一半**：
现在只有"参照缺失"会红，"每格都是 ⚠"仍然绿（且 `PASS=2` 使运行器的零断言闸也不拦）。
**建议**：加一条 `⚠ 条数上限`（如 `WARN ≤ 2`）或 `PASS 下限`，否则这条锁仍有结构性绿灯通道。
✔ 好消息：参照 ICC 的生产者 `_probe-jpg-p3-icc.ps1` **在 65 条清单内且前置于** `verify-colorfmt-matrix.ps1`
（`_run-step3-gates.ps1:1015`、调序注释 `:923`）⇒ fail-closed 不会在受管整轮里误伤。

### 2.3 上轮其它条目**本轮未返工**（P2，本轮 diff 未涉及）

- `verify-jbrd-e2e.ps1:240`（M4）：`Pix` 读不到时两侧同空 ⇒ `-eq` 恒真 PASS（同文件 `:244-245` 有护栏，本条漏）。
- `verify-jbrd-e2e.ps1:163-164`（M5）：`$made['ultrahdr'] = $null.FullName` ⇒ `Contains` 为 True，"集合完整"被绕（靠下游侥幸转红）。
- `verify-gainmap-thirdparty.ps1:141`（M6）：`($eb.text -notmatch 'MPImage')` 仍是裸否定式（本轮只动了 `:38` 的环境变量护栏）。
- `verify-orientation-rewrap.ps1:58/72/106`（M10）：三处 `throw` 仍在，`$ErrorActionPreference='Stop'` ⇒ 一格炸掉整脚本、不打印汇总行（同批 jbrd 已返工，本文件没有）。
- `verify-package-smoke.ps1`（L1）：仍只有 `if ($bad -gt 0) { exit 1 }`，无显式 `exit 0`。
- `verify-encoder-knob-liveness.ps1`（L2）：`nocmd` 仍打印 `WARN` 前缀却计入 `$nFail`（输出行写 WARN、实际会 exit 1）。

---

## 三、`_lib-matrix.ps1` 行号锚 +11 漂移

作者声称 966/976/1008/2050 → **977/987/1019/2061**。逐行核对 + 唯一性核对：

| 锚 | 该行实际内容（截断） | 命中 | 全文件唯一 |
|---|---|---|---|
| `FfmpegCommandBuilder.cs:977` | `return (null, options.JpegGainMap ? null : "iec61966-2-1", "bt709", 8, true);` | ✅ | ✅ 1 处 |
| `FfmpegCommandBuilder.cs:987` | `return (null, "iec61966-2-1", "bt709", 8, true);` | ✅ | ✅ 1 处 |
| `FfmpegCommandBuilder.cs:1019` | `return (null, null, null, ColorMapping.ImageEncoderArgs.AvifMaxBitDepth(options), false);` | ✅ | ✅ 1 处（`AvifMaxBitDepth(options)` 另在 `:510` 出现，但整行字面量唯一） |
| `FfmpegCommandBuilder.cs:2061` | `bool exiftoolFormats = fmt is "jpg" or "jpeg" or "png" or "tiff" or "webp";` | ✅ | ✅ 1 处 |

**全库 pin 审计**（自写脚本抽取 `_lib-matrix.ps1` 里全部 `file:line|字面片段`，共 **26 条**，逐条读源文件比对）：

- **25 条 OK**（含本次 4 条新锚），每条字面量 `uniq=1`；
- **1 条"漂移"= `:1251` 的 `ColorStrategy.cs:75|IccMode.BakeOnly …`（实际在 :76）**
  ⇒ 经核对这是 **B4 负控**（`_lib-matrix.ps1:1246-1255`：*negative control: evidence literal on a wrong line turns validator red*），
  **故意写错**用来证明 A21 按行号比对、不是全文件 `Contains`。**不是缺陷。**
- 运行器侧佐证：`pwsh -File tests/scripts/_lib-matrix.ps1 -SelfTest` ⇒ **47 行输出、`MATRIX RESULT: pass=38 fail=0`、exit 0**，
  其中 `B4 … turns validator red (fail=3)` 转红 ⇒ 行号锚校验器本身有牙。

⇒ **`_lib-matrix` 的 +11 抬号属实、复核到位**（作者声称成立）。

### 3.1 结构性改进建议（行号锚这个设计本身）

现状：源码一插行 ⇒ pin 全漂 ⇒ 人工 `grep -n` 重排（本次 4 条、上一次 4 条、`CliParser.cs` 那条已漂了
2011→2041→2055→2050 四次）。**每次漂移都要人工修一次，且修错了只会由 A21 在自检里报出来（要跑到自检才发现）。**

建议（按性价比）：

1. **让它自己会修**：`_lib-matrix.ps1` 增加 `-Repin` 开关 —— 对每条 Evidence，按 `字面片段` 在目标文件里
   **唯一搜索**到实际行号并回写（找不到或不唯一 ⇒ 报红、绝不盲改）。日常 `git diff` 后跑一次即可，
   pin 演进注释可自动生成骨架。**这是最省人工的一条。**
2. **降级为"锚点 + 上下文"**：Evidence 写成 `file:line|字面片段|前一行的 8 字符指纹`，校验时若行号不符但
   （行号 ±N 内 + 上下文指纹吻合）⇒ 报 `DRIFT-WARN`（可见、不静默），让人一眼看到漂了多少。
3. **给易漂文件加"结构性替身"**：`FfmpegCommandBuilder.cs` 这类超大文件的 pin，改成钉**方法名 + 该方法的
   Nth 个 return**（用简单解析而非行号），对"文件头部插分支"这类漂移天然免疫。
4. **CI 前置**：把 `_lib-matrix.ps1 -SelfTest` 接到 `src/FfmpegGui/**` 改动的 pre-commit / 门禁首步，
   漂移在**提交前**就红，而不是等下一轮人工复查。

---

## 四、PS5.1 语法静态检查

方法：**真 5.1 宿主**（`powershell.exe` = `5.1.26100.9549`）调
`[System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$toks, [ref]$errs)`；
同时用 token 流扫 PS7+ 语法（**排除** `StringLiteral / StringExpandable / HereString* / Comment` token，
即"出现在字符串里是允许的"）。

| 文件 | 解析 | tokens | errs | PS7+ 语法体检 |
|---|---|---|---|---|
| `_lib-matrix.ps1` | **PARSEOK** | 10258 | 0 | CLEAN |
| `_probe-cjk-hardcode-scan.ps1` | **PARSEOK** | 2099 | 0 | CLEAN |
| `_run-step3-gates.ps1` | **PARSEOK** | 5827 | 0 | CLEAN |
| `verify-colorfmt-matrix.ps1` | **PARSEOK** | 767 | 0 | CLEAN |
| `verify-encoder-knob-liveness.ps1` | **PARSEOK** | 2708 | 0 | CLEAN |
| `verify-gainmap-thirdparty.ps1` | **PARSEOK** | 1593 | 0 | CLEAN |
| `verify-jbrd-e2e.ps1` | **PARSEOK** | 3801 | 0 | CLEAN |
| `verify-orientation-rewrap.ps1` | **PARSEOK** | 2224 | 0 | CLEAN |
| `verify-package-smoke.ps1` | **PARSEOK** | 1670 | 0 | CLEAN |

⇒ **9/9 PASS（0 FAIL）**，无裸 `&&` / `||` / `??` / `??=` / `?.` / `?[` / 三元 `?:`。
（`&&` 仅出现在 `verify-encoder-knob-liveness.ps1:106` 的 `-join ' && '` 字符串内，允许。）

**遗留（与上轮同一条，未变）**：静态兼容 ≠ 双宿主可用。运行器取自身进程路径作宿主
（`:820-823`）⇒ 按 `pwsh -File` 跑时整轮都在 PS7 下，**PS5.1 分支从未真正执行过**。
凡**匹配中文**的判据（`package-smoke` 的 `传输错误/超时或已取消`、`knob` 的 `命令行`、`jbrd` 的 `增益图/降级`）
在 PS5.1 下仍有编码风险 ⇒ 建议按上轮 §五，在 PS5.1 下各跑一次 `verify-jbrd-e2e` 与 `verify-encoder-knob-liveness` 核对读数。

---

## 五、`tests/ServiceProbe/Program.cs`（+129）

### 5.1 新增了什么

1. **新子命令 `avifgm`**（`:105` 接线、`:588-700` 实现）。
   用途：给 **P0-2（AVIF 增益图 `colr.full_range` 恒写 full）** 上永久锁。三段：
   - **① 单元级**（反射直调 `internal IsoBmffGainMapWriter.PatchColr`）：底图 `colr=tv(0x00)` + 兜底 `true`
     ⇒ 必须**只读继承** `0x00`（旧代码在此硬写 `0x80`）；反向用例（底图 `0x80` + 兜底 `false` ⇒ 仍 `0x80`）证明继承是双向、不是一律写 tv；
     底图无 `colr` ⇒ 用兜底值补 `0x00`。
   - **②** 反射断言 `Build` 的 `baseFullRange` **默认值必须是 `false`**。
   - **③ 端到端**：真 ffmpeg 编 `yuv420p` 底图（不传 `-color_range`）⇒ 先断言 ffmpeg 裸底图 nclx `Fr & 0x80 == 0`；
     再走产品 `GainMapEncoder.EncodeAsync(container:"avif")` ⇒ 断言产物底图 colr `Fr == 0x00`、tmap 替代图同 range。
2. **两个私有工具**：`NclxColrBox(byte fullRange)`（造 19 B 的 nclx 盒）、`FindNclx(byte[])`（扫全部 nclx）。
3. **`usage` 串同步更新**（`:89`）。
4. **JXL 反函数往返锚点（P1-3）**（`:3396-3407`）：新增
   `Check(rt75 == 74 && rt30 == 30)`，注释明说 q75 是**非互逆点**（→2.4→74，差 1）、q30 是互逆点，
   把"近似互逆 ±1"这个**诚实性质**钉住，并把锚点从"只落在互逆点上"挪到有判别力的位置。
   ⇒ 上轮 P1-3 的处置方向正确；若日后有人把它"修"成严格互逆，这条会红，逼一次有意识决策。
5. 运行器侧：`_run-step3-gates.ps1:43` 把 `avifgm` 接进默认 `$Probes`（第 25 个 mode）。
   mode 名直接作为 `ServiceProbe` 的子命令传入（`:825-827` `Invoke-StepWithTimeout -FilePath $pr -Arguments @($m)`），
   未注册的 mode 会被打 `⚠未执行(未知 mode 或无 PROBE RESULT 汇总)` ⇒ **接线自洽，不会静默空跑**。

### 5.2 fail-open 形状审查

| 检查项 | 结论 |
|---|---|
| 异常吞掉按成功算？ | **无**。`main` 的 `catch`（`:179-188`）打 `FAIL exception: …` 并 `_fail++`，退出码 `_fail==0?0:1`。反射调用失败（`GetMethod` 返回 null / `Invoke` 抛 `TargetInvocationException`）都会落到这里 ⇒ **fail-closed**。 |
| 类型/方法找不到时 | `Check(t != null …)` + `if (t == null) return;`、`Check(pm != null …)` + `if (pm == null) return;` ⇒ 至少 1 条 FAIL，不会"0 断言退出 0"。✅ |
| 断言是否为真断言 | 是。①②③ 都是**值级**断言（字节位 / 默认值 / 产物字节），不是"日志说了这句话"。`bc.Count == 1 && (bc[0].Fr & 0x80) == 0` 有短路保护，不会空引用静默过。 |
| ffmpeg 缺席 | `Skip("avifgm ③：ffmpeg 未定位（① ② 已跑，本条不产出读数）")` —— 走 **`Skip()`**（计数 + 打印 `SKIP`，不记 PASS），符合本仓"静默 SKIP = 假绿通道"的教训。且 ①② 不依赖 ffmpeg ⇒ 缺件时锁仍有覆盖。 |
| 临时文件清理 | `work` = `PlatformServices.GetTempDir()/avifgm_<guid>`，`try { … } finally { Directory.Delete(work, true) }`。⚠ `catch { }` 是空吞（清理失败无声），属**best-effort**，可接受但建议打个 `⚠ 清理失败` 行。 |
| `RunCap` 忽略退出码 | `RunCap`（`:709-716`）返回 void、不看 rc。⚠ **不是 fail-open**（后续 `rawN`/`Check` 会因产物缺失转红），但建议把 rc 打进 detail，便于排查。 |
| CJK 门禁是否受影响 | **不受影响**：`_probe-cjk-hardcode-scan.ps1` 语料是 `$root/src/FfmpegGui`，**不扫 `tests/ServiceProbe`**（本轮实测 90 个源文件 / 83 个 .cs，与上轮一致）。 |

### 5.3 建议

- `finally` 的空 `catch {}` 改成打印一行 `⚠ avifgm 临时目录清理失败：<path>`（静默失败是本仓明令要避免的形状）。
- `RunCap` 顺手返回 rc 并写进 `avifgm ③前提` 的 detail。
- ①② 用反射调 `private static` —— 若日后 `PatchColr` 改签名，`GetMethod` 会返回 null ⇒ 只有 1 条 FAIL 且信息是
  "找得到 private static PatchColr"，建议把**签名**也打进 detail（`期望 (List<byte[]>, int, int, int, bool) -> bool`），排查更快。

---

## 六、建议处置顺序

1. **P1｜`verify-colorfmt-matrix` 补完**：加 `⚠` 条数上限或 PASS 下限（§2.2）。这条目前仍留着结构性绿灯通道。
2. **P1｜`$minLive = 19` 留余量**（§2.1）：改成按臂数比例算或 `实测 − 2`，避免换机假红。
3. **P2｜CJK `:336` 归因改写**（§1.5）：`[RAW]` 走 [tag] 桶而非 sink；顺手把 `:315/339` 的 821/822 时序理顺，
   并把三个 ℹ 桶基线按实测对齐（或标注"不维护"）。
4. **P2｜收口上轮未返工的 6 条**（§2.3）：`jbrd:240` 空值、`jbrd:163` `$null.FullName`、`gainmap3p:141` 裸否定式、
   `orientation` 三处 `throw`、`package-smoke` 无显式 `exit 0`、`knob` 的 `nocmd` 前缀措辞。
5. **结构性｜行号锚自动 repin**（§3.1 建议 1）：给 `_lib-matrix.ps1` 加 `-Repin`，并提交前跑 `-SelfTest`。
6. **双宿主｜PS5.1 实跑**（§四遗留）：至少 `verify-jbrd-e2e` 与 `verify-encoder-knob-liveness` 各跑一次核对中文判据读数。
7. **ServiceProbe**（§5.3）：三处小改（清理告警、rc 入 detail、反射签名入 detail）。

> **已确认可关闭**：上轮 P0-1（CJK 基线）与 P1-5（六条假绿）**全部修好且有变异证据**。

---

## 七、取证环境（可复现 / 已清理）

- 临时目录（**仓库外**）：`C:/Users/20210/AppData/Local/Temp/rev-cjk/`（CJK 语料副本：HEAD 树 + 工作区树）、
  `C:/Users/20210/AppData/Local/Temp/rev-gates/`（门禁镜像 + 12 个变异体 + 4 个 runner）。
  镜像用 **junction** 挂 `src/FfmpegGui/bin/Release/net11.0/win-x64`、`publish/PLAN`、`publish/build`、
  `tools/src/libultrahdr/build/Release`；`tests/output/{sources,validate}` 是镜像内的**独立副本**
  ⇒ 脚本的所有写操作都落在 Temp 里。
- 变异体清单：`_mut_orient_dim{,_old}.ps1`、`_mut_orient_fix{,_old}.ps1`、`_mut_pkg_log{,_old}.ps1`、
  `_mut_knob_crash{,_old}.ps1`、`_mut_knob_solid{,_old}.ps1`、`_mut_knob_alldec{,_old}.ps1`、`_mut_cfmt_ref{,_old}.ps1`
  （OLD 版由 `git show bdd4b0b:tests/scripts/<f>` 导出）。
- 实跑统计：orientation ×5（≈50 s/次）、package-smoke ×3（≈60 s/次）、colorfmt ×4（≈41 s/次）、
  knob ×7（≈160 s/次）、CJK ×9（≈3 s/次）、`_lib-matrix -SelfTest` ×1（6 s）。
- **全过程对 `src/`、`tests/`（仓库内）、`docs/` 既有文件零改动**；未执行任何 `git add` / `git commit`；
  唯一新增文件是本报告（`docs/review-2026-10-04/C-门禁脚本可靠性.md`）。
- 全程所有证据文件已落盘在 Temp（本报告中每条结论都对应一份 `rev-gates/*-result.txt`）；复查结束后临时目录已清理。
