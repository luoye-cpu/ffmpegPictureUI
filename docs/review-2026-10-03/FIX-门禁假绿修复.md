# FIX-门禁假绿修复：复核 + 变异验证报告

- **生成时间**：2026-10-03
- **范围**：`tests/scripts/` 下 6 个门禁脚本（P1-5 六条假绿）
- **依据**：`docs/review-2026-10-03/HANDOVER-修复交接.md` §2「P1-5」列出的 5 类修复点
- **被测二进制**：`publish/build/FFmpegPictureUI-v1.6.0-beta4-x64-full/FfmpegGui.exe`
  （`$env:PKG_VER=1.6.0-beta4`；colorfmt-matrix 走 Release 产物，无 PKG_VER）
- **版本错配覆盖件**：`publish/build/FFmpegPictureUI-v1.6.0-beta3-x64-full/FfmpegGui.exe`（ProductVersion `1.6.0-beta3+…`）
- **宿主**：pwsh 7.6.6 运行；PS5.1（5.1.26100.9549）做静态解析
- **纪律**：所有变异均在原地改坏 → 跑 → **改回原样**收尾。收尾后 `git diff --stat` 与复核前快照**逐文件一致**（11 文件 / +286 / -37），未动 `src/**`、未 `git add/commit/stash`。

---

## 0. 一句话结论

**5 类修复均已落地且有牙**：逐条「改坏修复点 → 该假绿转红 / 旧写法 → 假绿」的实测读数见 §2，汇总见 §3。
6 文件 **PS5.1 静态解析 6/6 通过**，无 PS7+ 禁用 token（§4）。

**两处与脚本内注释自陈读数的偏差**（不影响修复成立，仅据实登记）：
- `verify-orientation-rewrap.ps1` S1：注释写旧写法「0 FAIL」，**实测 1 FAIL**（jbrd keep 臂的「像素没动」把 `$dimProd` 置空这一路兜住了；真正假绿的是 5 条 drop 臂的 10 条断言）。见 §2.1。
- `verify-colorfmt-matrix.ps1` M2：注释写旧写法「WARN=6」，**实测 WARN=4 / PASS=2**（png、avif 两格 CICP=smpte432 已记 PASS，不是 WARN）。见 §2.6。

---

## 1. 修复点清单（复核确认已落地）

| # | 文件 | 声称修复 | 实际改动（行） | 落地 |
|---|---|---|---|---|
| 1a | `verify-orientation-rewrap.ps1` | ffprobe/jxlinfo 读空 ⇒ `$dimProd=''` ⇒ 断言假绿 | 新增 `$dimSrc` 归一化 + `$dimOk` 前置，A 段每条判据与 `$dimOk` 合取 | ✅ |
| 1b | 同上 | B 段缺夹具时 SKIP、0 断言仍全绿、无下限 | 夹具缺失改 `CK $false`；新增 `B 段断言条数下限 ≥ 6` | ✅ |
| 2 | `verify-package-smoke.ps1` | 4 条裸否定式日志断言（B4/C1/C2/E3），日志空则恒真 | 新增 `CmdOf` + `Seen`，4 条否定式与「取到了命令行/日志」合取 | ✅ |
| 3a | `verify-encoder-knob-liveness.ps1` | 全 `undecidable` 仍 exit 0、无下限 | 新增「活臂数下限 ≥ 19」+「未判比例 ≤ 已判 1/3」，计入 `$nFail` | ✅ |
| 3b | 同上 | `$honest = rc≠0 -or 播报` ⇒「崩溃即通过」 | 新增 `crashed` 桶（输出全空 / 异常退出码 / rc≠0 无拒绝证据）⇒ FAIL | ✅ |
| 4 | `verify-jbrd-e2e.ps1` / `verify-gainmap-thirdparty.ps1` / `verify-encoder-knob-liveness.ps1` | `$exe` 版本护栏**之后**被 `T76_EXE`/`T77_EXE`/`T79_EXE` 覆盖、不复验不打 SUBJ | 覆盖后重跑存在性 + ProductVersion 校验，补打 `SUBJ(被测,覆盖后)` | ✅ |
| 5 | `verify-colorfmt-matrix.ps1` | 参照 ICC 缺失降级 WARN、不计 FAIL | 参照缺失改 fail-closed（`❌ …不在位` + `FAIL=1` + exit 1） | ✅ |

---

## 2. 逐文件变异验证（实测读数）

> 每条给出：**基线（修复态，正常）** → **改坏** → **改回**。
> 「改坏」按修复点取：读空注入 / 动态枚举砍空 / 命令行捕获失效 / 崩溃形状 / 覆盖块退回单行 / 参照指向不存在路径。

### 2.1 `verify-orientation-rewrap.ps1`（PASS=28 FAIL=0，B 段 6 条）

**S1（读空双假绿）** — 变异：在 `$dimProd` 计算后插入 `$dimProd = ''`（模拟 ffprobe/jxlinfo 读取失败）。

| 状态 | 读数 | exit |
|---|---|---|
| 修复态 + 置空 | `PASS=11 FAIL=17`（5 drop 臂×3 + jbrd 臂×2，全部点名「产物码流尺寸读得到」） | **1** |
| 旧写法（去 `$dimOk` 前置）+ 置空 | `PASS=21 FAIL=1`（10 条 drop 断言假绿；仅 jbrd keep 臂「像素没动」兜住 1 FAIL） | 1 |
| 改回修复写法（正常） | `PASS=28 FAIL=0` | 0 |

**S2（B 段无下限）** — 变异：`$dpiCases = @()`（动态枚举掉成 0 格）。

| 状态 | 读数 | exit |
|---|---|---|
| 修复态 + 空枚举 | `FAIL  B 段断言条数下限（≥ 6…）   实得 0 条`，`PASS=21 FAIL=1` | **1** |
| 旧写法（`$minB=0`，无下限）+ 空枚举 | `PASS=22 FAIL=0`（B 段 0 断言仍全绿） | 0 |
| 改回（正常） | `PASS=28 FAIL=0` | 0 |

**S5（T76_EXE 护栏绕过）** — 变异：覆盖块退回 `if ($env:T76_EXE) { $exe = $env:T76_EXE }`；环境 `PKG_VER=1.6.0-beta4` + `T76_EXE=<beta3 包 exe>`。

| 状态 | 读数 | exit |
|---|---|---|
| 修复态 | `FAIL T76_EXE 覆盖件的版本串与 PKG_VER 期望不符：期望 1.6.0-beta4* 实得 1.6.0-beta3+…` | **2** |
| 旧写法 | `PASS=28 FAIL=0`，**无** `FAIL T76_EXE` 行；首行 SUBJ 仍写 beta4 却实际打在 beta3 上 ⇒ 假绿 | 0 |
| 改回（正常） | `PASS=28 FAIL=0` | 0 |

### 2.2 `verify-package-smoke.ps1`（PASS=17 FAIL=0）

**S3（裸否定式）** — 变异：`CmdOf` 返回 `''`（命令行捕获口径失效）+ `$e.log=''`（日志空）+ 4 条断言退回裸否定式。

| 状态 | 读数 | exit |
|---|---|---|
| 变异态（旧裸否定式 + 捕获失效） | `PASS=17 FAIL=0`，B4/C1/C2/E3 **全 PASS** ⇒ 假绿 | 0 |
| 修复态（保留捕获失效） | `PASS=13 FAIL=4`，B4/C1/C2/E3 各 FAIL 并点名「cjxl/ffmpeg 命令行没取到」「日志为空 ⇒ 否定式判据无从成立」 | **1** |
| 改回（正常） | `PASS=17 FAIL=0` | 0 |

### 2.3 `verify-encoder-knob-liveness.ps1`（活=19，PASS=19 FAIL=0）

**M7（崩溃即通过）** — 变异：把 `--jpeg-dct float` 臂的 `$z` 换成 `code=-2147450750 / log=''`（崩溃形状）。

| 状态 | 读数 | exit |
|---|---|---|
| 修复态 + 崩溃形状 | 该臂 `[crashed] rc=-2147450750 …输出全空=True 异常退出码=True` ⇒ FAIL；`PASS=18 FAIL=2`（crashed 1 + 活臂数下限 18<19 1） | **1** |
| 旧写法（`$honest = rc≠0 -or 播报`）+ 崩溃形状 | 该臂 `[live]` ⇒ PASS；`PASS=19 FAIL=0`（崩溃即通过） | 0 |
| 改回（正常） | `PASS=19 FAIL=0` | 0 |

**S4（全未判仍绿 / 无下限）** — 变异：`$minLive=25` + `$maxUndecidable=-1`（把两条闸都逼到必触发）。

| 状态 | 读数 | exit |
|---|---|---|
| 修复态（闸触发） | `FAIL 活臂数下限：活=19 < 25` + `FAIL 未判比例上限：未判=1 > …1/3（=-1）`，`PASS=19 FAIL=2` | **1** |
| 改回（正常） | `PASS=19 FAIL=0` | 0 |

**S5（T79_EXE 护栏绕过）** — 变异：覆盖块退回单行；`PKG_VER=1.6.0-beta4` + `T79_EXE=<beta3 包 exe>`。

| 状态 | 读数 | exit |
|---|---|---|
| 修复态 | `FAIL T79_EXE 覆盖件的版本串与 PKG_VER 期望不符：期望 1.6.0-beta4* 实得 1.6.0-beta3+…` | **2** |
| 旧写法 | `PASS=17 FAIL=3`，**无** `FAIL T79_EXE` 行；`G0 被测=…v1.6.0-beta3-x64-full\FfmpegGui.exe`（SUBJ 仍写 beta4）⇒ 读数打在异构二进制上 | 1 |
| 改回（正常） | `PASS=19 FAIL=0` | 0 |

### 2.4 `verify-jbrd-e2e.ps1`（PASS=100 FAIL=0）

**S5（T76_EXE 护栏绕过）** — 变异：覆盖块退回单行；`PKG_VER=1.6.0-beta4` + `T76_EXE=<beta3 包 exe>`。

| 状态 | 读数 | exit |
|---|---|---|
| 修复态 | `FAIL T76_EXE 覆盖件的版本串与 PKG_VER 期望不符：期望 1.6.0-beta4* 实得 1.6.0-beta3+…` | **2** |
| 旧写法 | `PASS=92 FAIL=6`，**无** `FAIL T76_EXE` 行（与 beta4 基线 100/0 明显不同 ⇒ 证明实际跑在 beta3 上）⇒ 护栏被绕过 | 1 |
| 改回（正常） | `PASS=100 FAIL=0` | 0 |

### 2.5 `verify-gainmap-thirdparty.ps1`（PASS=13 FAIL=0）

**S5（T77_EXE 护栏绕过）** — 变异：覆盖块退回单行；`PKG_VER=1.6.0-beta4` + `T77_EXE=<beta3 包 exe>`。

| 状态 | 读数 | exit |
|---|---|---|
| 修复态 | `FAIL T77_EXE 覆盖件的版本串与 PKG_VER 期望不符：期望 1.6.0-beta4* 实得 1.6.0-beta3+…` | **2** |
| 旧写法 | `PASS=13 FAIL=0`，**无** `FAIL T77_EXE` 行；`G0 被测二进制: …v1.6.0-beta3-x64-full\FfmpegGui.exe`（SUBJ 仍写 beta4）⇒ 假绿 | 0 |
| 改回（正常） | `PASS=13 FAIL=0` | 0 |

### 2.6 `verify-colorfmt-matrix.ps1`（PASS=2 FAIL=0 WARN=4）

**M2（参照 ICC 缺失降级）** — 变异：`$refIcc` 指向不存在路径（模拟 `tests/output/` 被 `git clean -xdf` 清掉）+ 退回旧降级写法（`$refMd5 = if (Test-Path…) {…} else { "无参照" }`）。

| 状态 | 读数 | exit |
|---|---|---|
| 变异态（旧降级写法 + 参照缺失） | `PASS=2 FAIL=0 WARN=4`（ICC 臂失去判别力仍绿；png/avif 两格 CICP=smpte432 记 PASS，非 WARN） | 0 |
| 修复态（fail-closed + 参照缺失） | `❌ 参照 P3 ICC 不在位：…\p3.icc` + `PASS=0 FAIL=1 WARN=0` | **1** |
| 改回（参照复原 + 正常） | `PASS=2 FAIL=0 WARN=4` | 0 |

---

## 3. 汇总表

| 文件 | 修复点 | 基线读数 | 改坏后读数 | 改回后读数 | 结论 |
|---|---|---|---|---|---|
| `verify-orientation-rewrap.ps1` | S1 读空假绿 | PASS=28 FAIL=0 (0) | 修复态 17 FAIL / 旧写法 1 FAIL(10 条假绿) | PASS=28 FAIL=0 (0) | ✅ 有牙 |
| 同上 | S2 B 段无下限 | 6 条 | 修复态 0 条→FAIL / 旧写法 0 断言仍绿 | 6 条 (0) | ✅ 有牙 |
| 同上 | S5 T76_EXE 护栏 | — | 修复态 exit 2 点名 / 旧写法 exit 0 假绿 | exit 0 | ✅ 有牙 |
| `verify-package-smoke.ps1` | S3 裸否定式 ×4 | PASS=17 FAIL=0 (0) | 修复态 FAIL=4 / 旧写法 FAIL=0 假绿 | PASS=17 FAIL=0 (0) | ✅ 有牙 |
| `verify-encoder-knob-liveness.ps1` | M7 崩溃即通过 | 活=19 (0) | 修复态 crashed/FAIL / 旧写法 live/PASS | 活=19 (0) | ✅ 有牙 |
| 同上 | S4 全未判仍绿 | 活=19 (0) | 修复态 FAIL=2（下限+比例） | 活=19 (0) | ✅ 有牙 |
| 同上 | S5 T79_EXE 护栏 | — | 修复态 exit 2 点名 / 旧写法 exit 0(读在 beta3) | exit 0 | ✅ 有牙 |
| `verify-jbrd-e2e.ps1` | S5 T76_EXE 护栏 | PASS=100 (0) | 修复态 exit 2 点名 / 旧写法 PASS=92(读在 beta3) | PASS=100 (0) | ✅ 有牙 |
| `verify-gainmap-thirdparty.ps1` | S5 T77_EXE 护栏 | PASS=13 (0) | 修复态 exit 2 点名 / 旧写法 exit 0 假绿 | PASS=13 (0) | ✅ 有牙 |
| `verify-colorfmt-matrix.ps1` | M2 参照缺失降级 | PASS=2 FAIL=0 WARN=4 (0) | 修复态 FAIL=1 exit 1 / 旧写法 FAIL=0 假绿 | PASS=2 FAIL=0 WARN=4 (0) | ✅ 有牙 |

> 括号内为 exit code。所有「改回」读数均在变异文件复原后重跑取得（非复用基线）。

---

## 4. PS5.1 静态解析与禁用语法

- **解析**：用 Windows PowerShell 5.1 的 `[System.Management.Automation.Language.Parser]::ParseFile` 逐个解析 6 文件 ⇒ **6/6 `PARSE-OK`，0 error**。
- **禁用 token**：对 6 文件 token 流过滤 `AndAnd` / `OrOr` / `QuestionQuestion` / `QuestionDot` / `QuestionLBracket` ⇒ **0 命中**。
- 文本 grep 命中的 `&&` / `||` 全部落在**注释或字符串字面量内**（合法）：
  - `verify-jbrd-e2e.ps1:262/293` 的 `"src=[…] || rt=[…]"`（双引号串内）；
  - `verify-encoder-knob-liveness.ps1:102` 的 `-join ' && '`（单引号串内）；
  - 其余为文件头「双宿主兼容」注释自身。
- 结论：PS5.1 解析无误 ⇒ 不会出现「整脚本不执行 = 假绿」。

---

## 5. 残留风险与未决项

1. **`verify-orientation-rewrap.ps1` S1 注释读数偏差**：注释称旧写法「0 FAIL」，实测 1 FAIL（jbrd keep 臂兜住）。即该缺陷的真实形状是「5 条 drop 臂的 10 条断言假绿」，而非「三条互斥判据同时绿」。**修复本身正确且有牙**（修复态 17 FAIL）；建议后续把注释措辞收敛为「drop 臂部分假绿」。
2. **`verify-colorfmt-matrix.ps1` 注释读数偏差**：注释称旧写法 WARN=6，实测 WARN=4/PASS=2。同上，仅注释措辞问题。
3. **`verify-colorfmt-matrix.ps1` 参照 ICC 的可用性**：`$refIcc` 是另一条门禁 `_probe-jpg-p3-icc.ps1` 的遗留产物、落在被 `.gitignore` 排除的 `tests/output/` 下 ⇒ **干净 checkout / `git clean -xdf` 后本门禁必然 fail-closed 变红**（需先跑 `_probe-jpg-p3-icc.ps1` 生成参照）。这是 fail-closed 的**有意代价**，但会在新机上表现为「一上来就红」，需在门禁清单里登记前置依赖。
4. **`verify-colorfmt-matrix.ps1` 的 WARN 分支仍是已知假警报**：`⚠ 有 ICC 但非 P3 参照` 判据是「与参照逐字节相等」，而实测产物 ICC 是 D50 适应后的 Display P3、只是与参照不同字节 ⇒ 待把判据改为「色度是否为 P3」后才可升格 FAIL。**未擅自升格**（维持原裁定）。
5. **S5 护栏的校验强度**：三处 `T*_EXE` 护栏只在 `$env:PKG_VER` 存在时比对版本串；无 `PKG_VER`（默认 Release 档）时仅要求 ProductVersion 非空，不校验其与 Release 产物一致，也不校验 SHA/内容。属设计取舍（`T*_EXE` 本意就是迭代时换被测件），但**不能防「指向任意一份带版本串的异构 exe」**。
6. **`verify-encoder-knob-liveness.ps1` 的硬编码阈值**：`$minLive=19` 取自「本轮实测活=19」；若臂集合或夹具发生正常变化（真删一臂/加一臂），可能误伤。`$abnormalRc` 只把「非 0 且非 1」视为异常退出码，若产品将来用其它退出码表示拒答会被误判 `crashed`。
7. **`verify-package-smoke.ps1` 的 `CmdOf` 依赖日志措辞**：两趟取法认 `[cmd]` / `… 命令行:` 前缀；产品改措辞时 `Seen` 会转红（fail-closed，方向正确），但会表现为「前置没取到」而非产品缺陷，需人工区分。

---

## 6. 复现命令（供复核）

```powershell
$root='C:\PLAN\ffmpegPictureUI'
$env:PKG_VER='1.6.0-beta4'      # colorfmt-matrix 不需要
# 正常档（修复态）
pwsh -NoProfile -File "$root\tests\scripts\verify-orientation-rewrap.ps1"
pwsh -NoProfile -File "$root\tests\scripts\verify-package-smoke.ps1"
pwsh -NoProfile -File "$root\tests\scripts\verify-encoder-knob-liveness.ps1"
pwsh -NoProfile -File "$root\tests\scripts\verify-jbrd-e2e.ps1"
pwsh -NoProfile -File "$root\tests\scripts\verify-gainmap-thirdparty.ps1"
pwsh -NoProfile -File "$root\tests\scripts\verify-colorfmt-matrix.ps1"

# 护栏变异（修复态应 exit 2）
$env:T76_EXE="$root\publish\build\FFmpegPictureUI-v1.6.0-beta3-x64-full\FfmpegGui.exe"
pwsh -NoProfile -File "$root\tests\scripts\verify-jbrd-e2e.ps1"   # ⇒ exit 2 + FAIL T76_EXE …
```

- 日志留存：`tests/output/p15/*.log`（base-* / mut-* / fix-* / restore-*，被 `.gitignore` 排除，不入库）。
- 本次复核结束时 `git diff --stat` 与复核前快照**逐文件一致**，未新增/删除任何受版本控制的改动。
