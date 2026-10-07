# 测试覆盖率审计：CLI/UI 选项组合盲区盘点

> **日期**：2026-10-07 ｜ **审计员**：coverage-auditor（team lead 委派，共享任务 `task-2`）
> **性质**：只读审计。**未改动** `src/`、`tests/` 或任何被测文件。
> **唯一写入**：本文件 `docs/TEST_COVERAGE_AUDIT_2026-10-07.md`。
> **统计脚本**：一次性统计器落在 `tests/output/_audit_probe_scan.ps1`（该目录被 `.gitignore:78` 的 `output/` 覆盖，
> 不进版本控制，不污染仓库）。生成 `tests/output/_audit_scan.csv`。
>
> ⚠ **口径声明（先读这条再看表）**：本审计把「覆盖」分三级，**不混用**：
> - **L3 端到端（真跑断言）** = 门禁脚本里以**可执行行**（`ACTIVE`）把该选项喂给真 `FfmpegGui.exe` 并断言**产物**。
> - **L1 解析层** = `ui-cli-strict`（`tests/ui-cli/uicliGroup.cs`）里 `CliParser.Parse` / `BuildBaseOptions` 的进程内断言。
> - **L0 声明** = 只出现在**轴登记表/矩阵定义/注释/帮助文本**里。**L0 不是覆盖**，本文一律不计入「已覆盖」。

---

## 0. 摘要：本次审计推翻/修正的三个前置假设

| # | Lead 初筛的说法 | 实测结论 | 证据 |
|---|---|---|---|
| 1 | `--preset`(1) `--settings`(1) `--no-overwrite`(1) 有命中 | **命中全在注释里**：三者 `ps1_ACTIVE` = **0 / 0 / 0**。`--settings` 的 11 条"命中"是**子串假阳性**（`AppSettingsService`、`AppSettings` 等），按词边界计为 0 条真 CLI 用法 | 见 §1 命令 A |
| 2 | `--color-carry-scope`(1) `--color-709-curve`(2) `--color-range`(3) `--color-hdr-peak`(2) 命中少 ⇒ 疑似缺口 | **实际有 L1 解析层覆盖**，只是不在 `.ps1` 里而在 `tests/ui-cli/uicliGroup.cs`（`ui-cli-strict` mode）。Lead 的 SimpleMatch 只扫了 `tests/scripts/*.ps1` ⇒ **系统性漏掉整个 C# 测试面**（41 个 live `.cs`） | 见 §2 |
| 3 | 权威选项面 ≈50 个（`--help`） | `--help` **只列 49 个**；`CliParser.cs` 的 `ApplyExtraOption` switch 另有 **112 个 case key**（含 `--no-overwrite`、`--jpeg-gain-map-*`、`--raw-color-target` 之外的一大批）。**`--help` 文本确实不全** | 见 §1 命令 B |

**最高价值结论（一句话）**：本仓的**组合覆盖基础设施非常强但几乎全部不受管** ——
`_lib-axes.ps1`（197 条 L1 单轴用例）+ `_lib-matrix.ps1`（171 条 L2 两两 + 5 张 L3 语义矩阵 4624 条）
只被 `verify-oracle-matrix.ps1` 执行，而那条**有意不入清单**（≈2.5 h 长跑，`TESTING.md:5244`、`:3524`）。
⇒ **72 条受管门禁里，没有任何一条执行 L1/L2/L3 组合用例**。
这不是"没人写过用例"，而是**"用例写了、每一天都在腐烂、没人跑"**。

---

## 1. 可复现的统计命令

### 命令 A —— ACTIVE（真跑）vs COMMENT（注释）分类器
```powershell
cd C:\PLAN\ffmpegPictureUI
# 生成 tests/output/_audit_scan.csv
& pwsh -NoProfile -File tests\output\_audit_probe_scan.ps1 -Tokens @(
  '--hdr-mode','--color-sdr-white','--color-sdr-peak','--color-carry-scope',
  '--color-allow-unlabeled','--no-overwrite','--preset','--settings','--log-format',
  '--auto-threads','--color-709-curve','--color-hdr-peak','--color-range')
```
分类器逻辑：逐行剥掉**行内 `#` 注释**（含单/双引号状态机，不做完整 PS 词法，保守）；
整行以 `#` 开头 ⇒ `COMMENT`；剥注释后仍含 token ⇒ `ACTIVE`。
**0 行 `ACTIVE` ⇒ 该选项从未被任何脚本真跑。**

### 命令 B —— 权威 CLI 选项面
```powershell
# ① 帮助文本（权威但对高级表不全）
& src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe --help
# ② 源码补齐：switch 的全部 case key
$c = Get-Content -Raw src\FfmpegGui\CliParser.cs
([regex]::Matches($c,'case\s+"([a-z0-9\-]+)"\s*:') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique).Count   # => 112
```

### 命令 C —— 受管清单（72 条）与全量（112 个 .ps1）
```powershell
$c = Get-Content -Raw tests\scripts\_run-step3-gates.ps1
$m = [regex]::Match($c,'(?ms)^\$scriptList\s*=\s*@\((.*?)\)\r?\n\$actualScriptCount')
$managed = [regex]::Matches($m.Groups[1].Value,"'([^']+\.ps1)'") | ForEach-Object { $_.Groups[1].Value }
$managed.Count                                                                        # => 72
(Get-ChildItem tests\scripts -Filter *.ps1 -File).Count                               # => 112
# 未受管 = 40（与 TESTING.md:426-452 台账逐类相符：A 库 5 / B 编排 13 / C 取证 16 / D 其他 6）
```

### 命令 D —— ACTIVE / 词边界统计（本文主表口径）
```powershell
$names = Get-Content tests\output\_managed_names.txt   # 由命令 C 导出
$paths = $names | ForEach-Object { "tests\scripts\$_" } | Where-Object { Test-Path $_ }
$allcs = Get-ChildItem tests -Recurse -Filter *.cs |
         Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\output\\' } |
         ForEach-Object { $_.FullName }
foreach ($o in @('hdr-mode','no-overwrite','log-format','auto-threads','strip-time')) {
  $pat = [regex]::Escape($o) + '(?![\w\-])'
  $act = Select-String -Path $paths -Pattern $pat | Where-Object { -not $_.Line.TrimStart().StartsWith('#') }
  "$o : ps1_ACTIVE=$($act.Count)  cs=$((Select-String -Path $allcs -Pattern $pat).Count)"
}
```

### 命令 E —— 组合用例的规模（L1/L2/L3）
```powershell
& pwsh -NoProfile -Command "& './tests/scripts/_lib-axes.ps1'   -SelfTest" | Select-String 'AXES RESULT'
#   => AXES RESULT: pass=19 fail=0      （60 轴登记项 / 197 条 L1 用例）
& pwsh -NoProfile -Command "& './tests/scripts/_lib-matrix.ps1' -SelfTest" | Select-String 'MATRIX RESULT|matrix '
#   => pairwise-cases=171, matrices=5
#      color-core raw=2028 folded=1872 ｜ encoder-core 1040/780 ｜ metadata-core 832/768
#      gainmap-core 780/720 ｜ bitdepth-container 676/507   ⇒ L3 合计 raw=5356
#   => MATRIX RESULT: pass=38 fail=0
```

---

## 2. 主表：维度 / src 取值数 / 已覆盖 / 盲区 / 证据

> 「已覆盖」列格式 = `L3 端到端数 + L1 解析层数`。**L0（仅声明）不计入。**
> 证据一律 `脚本名:行号`；`无` = 命令 A/D 实测 0 行 ACTIVE。

### 2.1 选项级（Lead 点名的 13 个 + 审计新增的高危项）

| 选项 | src 取值数 | L3 端到端 | L1 解析层 | L0 仅声明 | 盲区 | 证据（`脚本/文件:行号`） |
|---|---|---|---|---|---|---|
| `--hdr-mode` | 2 (`gainmap`/`traditional`) | **0** | **0** | 0 | **完全零覆盖**（连注释都没有） | `无`（`_accept-ui-split.ps1:218` 仅提及控件名 `HdrModeCombo`，非本选项） |
| `--color-sdr-white` | 连续 | **0** | **0** | ✓ 轴登记 | **零 CLI 覆盖**；只有常量 203 的间接断言 | L0：`_lib-axes.ps1:250`；恒等断言 `ColorGroup.cs:240`（测常量非选项） |
| `--color-sdr-peak` | 连续 | **0** | **0** | ✓ 轴登记 | **零 CLI 覆盖**；`CoreGroup.cs:1454` 断言的是"透传"，但经 **对象赋值** (`:1424`) 非 CLI | L0：`_lib-axes.ps1:251`；`CoreGroup.cs:1424,1454` |
| `--color-carry-scope` | 3 + 数字别名 0/1/2 | **0** | **1** | ✓ | 无端到端；解析层含全部 6 个合法写法 | L1：`uicliGroup.cs:216`、非法值 `:170`（`"alll"` ⇒ ArgumentException） |
| `--color-allow-unlabeled` | 2 | **0** | **0** | ✓ 轴登记 | **零覆盖**（TIFF 未标注语义的允许位） | L0：`_lib-axes.ps1:223` |
| `--no-overwrite` | 2 (`--overwrite` 反向) | **0** | **0** | 0 | **完全零覆盖**；**2026-10-05 P0 数据损失修复** | `无`。唯一命中 `_probe-cjk-hardcode-scan.ps1:352` 是**注释**（引述拒绝文案） |
| `--preset` | 运行时预设库 | **0**（CLI 名） | 0 | 0 | CLI `--preset <name>` 本身零覆盖；预设**载入管线**有覆盖 | L0：`_lib-axes.ps1:695` 注释；间接 `ui-core/uicoreGroup.cs:410,431`（走 `ApplyPresetData`，非 CLI 标志） |
| `--settings` | 文件路径 | **0**（CLI 名） | 0 | 0 | CLI `--settings <file>` 零覆盖；`AppSettingsService` 行为有覆盖 | L0：`_lib-axes.ps1:695`；行为面 `CoreGroup.cs:2145`、`TESTING.md:715` |
| `--log-format` | 2 (`Text`/`JsonLines`) | **0** | **0** | ✓ 轴登记 | **零覆盖**；`JsonLines` 分支（`ConsoleQueueObserver.cs:203`）从未被执行 | L0：`_lib-axes.ps1:210` |
| `--auto-threads` | 2 | **0** | **0** | ✓ 轴登记 | **零覆盖**；`QueueProcessor.cs:263` 的 `max(1, 核数/并发)` 分支无断言 | L0：`_lib-axes.ps1:207` |
| `--color-709-curve` | 2 (`std`/`zimg`) | **0** | **1**（含大小写归一 + 非法值） | ✓ | 有解析层；**端到端字节级锁在 C# 组里**（非 .ps1） | L1：`uicliGroup.cs:212-213,169`；端到端 `GamutFit.cs:289`（"#29 端到端行为锁"注释头，断言体在同文件后续行） |
| `--color-hdr-peak` | 连续 (0/100/1000/4000/10000) | **1**（多条真跑） | 1 | ✓ | 覆盖良好（元数据 vs 用户值优先级） | L3：`verify-color-peak.ps1:84`（`intentPeak=1500.00` 用户覆盖元数据 600）、`:85`；L1：`uicliGroup.cs:161` |
| `--color-range` | 3 (`auto`/`tv`/`pc`) | **0** | **1**（3 值全测 + 非法值） | ✓ | 有解析层；`auto`＝"不下发"语义只由矩阵**声明**（L0） | L1：`uicliGroup.cs:210-211,168`、分层语义 `:256`；L0 语义：`_lib-matrix.ps1:58,1321` |
| **`--strip-time`** | 2 | **0** | **0** | ✓ 矩阵轴 | **零覆盖**（`ps1` 与 `cs` 均为 0 命中） | L0：`_lib-matrix.ps1:622` 列为 `metadata-core` 轴；`无` 任何执行证据 |
| **`--strip-camera`** | 2 | **0** | **0** | ✓ 矩阵轴 | **零覆盖** | 同上 |
| **`--strip-all-exif`** | 2 | **0** | **0** | ✓ 矩阵轴 | **零覆盖** | 同上 |
| **`--strip-xmp`** | 2 | **0** | **0** | ✓ 矩阵轴 | **零覆盖** | 同上 |
| `--jpeg-gain-map-downsample` | 5 | **0** | **0** | ✓ 轴+矩阵轴 | **零覆盖**（Ultra HDR 降采样档） | L0：`_lib-axes.ps1` 轴登记、`_lib-matrix.ps1:641`；`无` |
| `--jpeg-gain-map-quality` | 连续（-1 为合法哨兵） | **0** | **0** | ✓ 轴登记 | **零覆盖** | L0：`_lib-axes.ps1`；`无` |
| `--jpeg-gain-map-target-nits` | 3 | **0** | **0** | ✓ 轴登记 | **零覆盖** | L0：`_lib-axes.ps1`；`无` |

**统计命令**：命令 A + 命令 D。**"L0 仅声明"的存在正是不该把命中数当覆盖的直接证明** ——
`_lib-axes.ps1` 是 **A 类共享库**（`TESTING.md:431`，**有意不入清单**），它只**登记取值域**、不执行任何用例。

### 2.2 格式 × 编码器

| 维度 | src 取值数 | L3 覆盖 | 盲区 | 证据 |
|---|---|---|---|---|
| 输出格式 | 10 (`jpg/png/apng/webp/avif/jxl/jxr/tiff/gif/dng`) | **9**（`dng` 见下行） | 见下 | 主矩阵 `verify-usability-e2e.ps1:184`（6 目标）＋ 其余门禁补齐 |
| ├ jpg | — | ✓ | — | `verify-metadata-privacy.ps1:93` 等 8 脚本 |
| ├ png | — | ✓ | — | `verify-prophoto-faithful.ps1`、`verify-png3-interop.ps1` 等 9 脚本 |
| ├ apng | — | ✓ | — | `verify-png-signature.ps1`、`verify-anim-static-target.ps1` |
| ├ webp | — | ✓ | — | `verify-webp-hdr-fix.ps1`、`verify-decision-delivery.ps1` |
| ├ avif | — | ✓ | — | `verify-decision-delivery.ps1`、`verify-prophoto-faithful.ps1` |
| ├ jxl | — | ✓（最强） | — | `verify-jxl-codestream-route.ps1`、`_probe-jxl-input-target.ps1` |
| ├ jxr | — | ✓ | — | `verify-jxr-anim-input.ps1:62`、`verify-package-smoke.ps1:130` |
| ├ tiff | — | ✓ | — | `verify-tiff-icc.ps1`、`verify-orientation-rewrap.ps1`、`_probe-raw-default-tier.ps1` |
| ├ gif | — | ✓ | — | `verify-anim-static-target.ps1`、`verify-webp-anim-probe.ps1` |
| └ **dng** | — | **0**（产品 CLI） | **`-f dng` 零产品路径覆盖**。`run-pipeline-tests.ps1:350-385` 调的是 **`dngtool.exe` 直连**，**绕过产品** | `无`（命令 D：`-f dng` ACTIVE=0） |
| 编码器 `-e/--encoder` | 5 | **3**（`ffmpeg`/`cjxl`/`cjpegli`） | **`Jxr`、`Dng` 零覆盖** | 见下 |

**编码器逐项证据**（受管 72 条内）：
```powershell
Select-String -Path $paths -Pattern '--encoder' | Where-Object { -not $_.Line.TrimStart().StartsWith('#') }
```
| 编码器 | 受管门禁里的真跑证据行 |
|---|---|
| `Ffmpeg` | `verify-jxl-codestream-route.ps1:350,358,373` |
| `Cjxl` | `verify-jxl-codestream-route.ps1:173,200,206,214`；`verify-webp-anim-probe.ps1:720` |
| `Cjpegli` | `verify-jxl-codestream-route.ps1:185,254`；`verify-gainmap-engine.ps1:607` |
| **`Jxr`** | **无**（`--encoder Jxr` 在受管 72 条内 0 命中；格式 `jxr` 靠**后缀推断**而非显式后端选择） |
| **`Dng`** | **无** |

> ⚠ **`--encoder Jxr` / `--encoder Dng` 的"零覆盖"性质要说清**：格式级 `-f jxr` 有覆盖（后缀推断走 Jxr 后端），
> 但**显式后端选择这条路**（`CliParser` → `EncoderDetectionService.IsCompatible` 的兼容性判定、
> 以及"后端与目标格式不匹配时报错"的那条分支）**无任何断言**。`EncoderDetectionService.cs:14-21` 定义 5 个后端，
> 其中 2 个的显式选择路径未被跑过。

### 2.3 色彩：策略 × 引擎 × 位深

| 维度 | src 取值数 | 覆盖 | 盲区 | 证据 |
|---|---|---|---|---|
| 色彩策略 | 4 (`Recommended`/`CarryIcc`/`BakeCicpOnly`/`Manual`) | **4/4** | 无 | `uicliGroup.cs:189-192`（逐值解析）；端到端 `verify-color-strategy.ps1`（71→77 条） |
| `--icc-mode` 兼容别名 | 4 ⇒ 3 策略（`None`≡`BakeToStandard`↦Recommended，**有意等价类**） | **4/4** | 无 | `uicliGroup.cs:193-196`；等价类说明 `CliParser.cs:943-944` |
| 色彩引擎 | 3 (`auto`/`engine`/`legacy`) | **3/3** | 无 | `uicliGroup.cs:197-198`；`verify-color-strategy.ps1:174`(parity 双跑) |
| 位深 | 4 (`8/10/12/16`) | 解析 ✓ / 交付见下 | AVIF/P3 高位深有专项；**`dng-bit-depth`** 无 | `uicliGroup.cs:158`；`verify-color-strategy.ps1` F4/F5（`TESTING.md:213`） |
| 色域词表 `--color-space` | 13 | 多数 | 见 §3 | L0：`_lib-axes.ps1` `color-space` 13 值 |
| `--color-primaries` | 3 | 部分 | — | L0 轴登记 |
| `--color-trc` | 5 | 部分 | — | L0 轴登记 |
| `--color-matrix` | 5 | 部分 | `smpte428` **有意不收录** | `_run-step3-gates.ps1:17-27`（colormath 变异验证记录） |
| `--color-gamut-map` | 2 | ✓ | 无 | `verify-gamut-map.ps1`（36/0） |
| `--raw-color-target` | 3 | ✓ | 无 | `_probe-raw-default-tier.ps1`、`_probe-gui-control-wiring.ps1` |

### 2.4 HDR：PQ/HLG × tonemap 4 × 峰值三件套

| 维度 | src 取值数 | 覆盖 | 盲区 | 证据 |
|---|---|---|---|---|
| HDR 传输 PQ | 1 | ✓ | — | `verify-color-peak.ps1:67`（`trc=smpte2084` 自检） |
| HDR 传输 HLG | 1 | ✓ | — | `verify-hlg-route.ps1:226`（`TESTING.md:226`：arib-std-b67 自检） |
| **tonemap `hable`** | 1 | ✓ | — | `verify-color-tonemap.ps1:81`（`tonemap=hable:peak=$hr`） |
| **tonemap `mobius`** | 1 | ✓ | — | `verify-color-tonemap.ps1:82`、亚拐点直通 `:77` |
| **tonemap `reinhard`** | 1 | **仅经 `--tonemap-curve`** | **`--color-tone-map reinhard` 无端到端**；`verify-color-wiring.ps1:152` 用的是 `--tonemap-curve`（旧轴，且被 `_lib-reject.ps1:357` 标注为**结构上不被引擎消费**） | `verify-color-wiring.ps1:152`；`_lib-reject.ps1:357-362` |
| **tonemap `bt2446a`** | 1 | **1**（进程内 bt2446 探针 12/0） | `--color-tone-map bt2446a` **CLI 端到端**未跑（仅解析层 `uicliGroup.cs:217` + 探针 `bt2446`） | `_run-step3-gates.ps1:43`（`$Probes` 含 `bt2446`）；`uicliGroup.cs:217` |
| `--color-hdr-peak` | 5 档 | ✓ | — | `verify-color-peak.ps1:84,85` |
| `--color-sdr-white` | 连续 | **0** | **零覆盖** | 见 §2.1 |
| `--color-sdr-peak` | 连续 | **0** | **零覆盖** | 见 §2.1 |
| `--hdr-mode` | 2 | **0** | **零覆盖**（HDR 落地方式二选一） | 见 §2.1 |

### 2.5 动图：5 容器 × 4 控制

| 维度 | src 取值数 | 覆盖 | 盲区 | 证据 |
|---|---|---|---|---|
| 动图容器 | 5 (`gif`/`webp`/`apng`/`avif`/`jxl`) | 5/5 探测 | 控制轴见下 | `verify-gif-avif-framelist.ps1`、`verify-webp-anim-probe.ps1`、`verify-avif-anim-probe.ps1`、`verify-anim-static-target.ps1`、`verify-jxr-anim-input.ps1` |
| `--animation-fps` | 5 | **1**（且是**负控**） | **正控缺席**：唯一命中是"**不得** exit 0 且 1 帧"的守卫 | `verify-anim-static-target.ps1:320,334` |
| `--animation-loop` | 4 (`-1`/`0`/`n`) | 解析层 ✓ | 端到端无；哨兵 `-1` 语义未断言产物 | L1：`uicliGroup.cs:130-132`（空格≡内联形式） |
| `--animation-duration` | 3 | **0** | 零覆盖 | L0：`_lib-axes.ps1` |
| `--animation-scale-w` | 3 | 解析层（负控） | 端到端无 | L1：`uicliGroup.cs:141-143` |
| `--gif-palette` / `--gif-dither` | 2 / 2 | **0** | 零覆盖 | L0：`_lib-axes.ps1` |

### 2.6 GainMap 四开关

| 维度 | src 取值数 | 覆盖 | 盲区 | 证据 |
|---|---|---|---|---|
| `--jpeg-gain-map` | 2 | **✓ 最强**（35 条 ACTIVE，7 脚本） | — | `verify-gainmap-managed.ps1`、`verify-gainmap-engine.ps1`、`verify-gainmap-memory.ps1`、`verify-jbrd-e2e.ps1` |
| `--jpeg-gain-map-base-gamut` | 2 (`srgb`/`bt2020`) | ✓（3 条 ACTIVE） | 别名组（`rec2020`/`bt2100`/`2020`）未逐一跑 | `verify-gainmap-engine.ps1`（`TESTING.md:218`：bt2020 红原色 0.70799 0.29201 vs sRGB 0.64 0.33） |
| `--jpeg-gain-map-multi-channel` | 2 | ✓（2 条 ACTIVE） | — | `verify-gainmap-engine.ps1`、`verify-gainmap-thirdparty.ps1`（XMP `Channels` 3 vs 1） |
| `--jpeg-progressive` | 3 态 | ✓（8 条 ACTIVE） | — | `verify-gainmap-engine.ps1`（§10 三态 × 双路径，33 条） |
| `--jpeg-gain-map-downsample` | 5 | **0** | 零覆盖 | L0：`_lib-matrix.ps1:641` |
| `--jpeg-gain-map-quality` | 连续（`-1` 哨兵） | **0** | 零覆盖 | L0：`_lib-axes.ps1` |
| `--jpeg-gain-map-target-nits` | 3 | **0** | 零覆盖 | L0：`_lib-axes.ps1` |

### 2.7 几何：max-dimension × orientation 8 态 × 首帧

| 维度 | src 取值数 | 覆盖 | 盲区 | 证据 |
|---|---|---|---|---|
| `--max-dimension` | 5 | ✓ | — | `verify-geometry-engine.ps1`（23/0）、`verify-geometry-orientation.ps1`（18/0） |
| `--max-dimension-enabled` | 2 | ✓ | — | 同上 |
| **EXIF Orientation 8 态** | **8**（1–8 含镜像 `2/4/5/7`） | **1**（只有 `8`） | **7/8 未覆盖**：`2`/`3`/`4`/`5`/`6`/`7` 全无夹具 | 全部夹具都是 `Orientation=8`：`verify-geometry-orientation.ps1:135`、`verify-orientation-rewrap.ps1:122`、`GainMapDecodeGroup.cs:450`、`GeometryGroup.cs:809` |
| 首帧（多帧取首帧） | — | ✓ | — | `verify-engine-firstframe.ps1` |
| 取向 + 重封装 | — | ✓ | — | `verify-orientation-rewrap.ps1:193`（旋正后须丢弃/归一） |

> ⚠ **Orientation 覆盖的量化（已按源码订正，不凭直觉）**：`ImageGeometry.ApplyRotation`
> （`ImageGeometry.cs:42-46`）的实际实现只有**一条判据**：
> ```csharp
> if (Math.Abs(rotationDeg) % 180 != 90) return (codedW, codedH);   // 0 / ±180 / 未知 ⇒ 原样
> return (codedH, codedW);                                          // ±90 ⇒ 交换宽高
> ```
> ⇒ **它只区分"交换 / 不交换"两态，不区分镜像**。已有夹具只覆盖 **`Orientation=8`**；
> `Orientation=6` 在**注释里**被点名（`:39-40` 自述"ffprobe 对 8 报 90、对 6 报 -90，两者都要交换"），
> 但**没有夹具**。⇒ 未覆盖的是两条**行为等价类**而非"7 个独立分支"：
> ① **`6`（= -90 ⇒ 交换）**：与 `8` 走同一 return，但**符号相反** —— 当前实现用 `Math.Abs` 抹平了符号，
> 该"抹平"本身**零断言**（改成不取绝对值就会让 6 静默失效）；
> ② **`2/3/4`（= 0/±180 ⇒ 不交换）** 与 **`5/7`（镜像，φ=90 但需翻转）** ——
> `5/7` 的**像素翻转语义在本函数里根本不存在**，属产品能力的真实边界（是"缺功能"还是"有意不实现"需 P3 先读
> `QueueProcessor` 的取向处理链再定，本轮**不下结论**，只登记"零夹具"）。

### 2.8 元数据：strip 6 种 × preserve × exiftool 编辑器

| 维度 | src 取值数 | 覆盖 | 盲区 | 证据 |
|---|---|---|---|---|
| `--strip-gps` | 2 | ✓ | — | `verify-metadata-privacy.ps1:100`（负控 `--no-strip-gps`） |
| `--no-strip-gps` | — | ✓ | — | `verify-metadata-privacy.ps1:93,100` |
| `--strip-metadata` | 2 | ✓ | — | `verify-metadata-privacy.ps1:119,127,196` |
| `--preserve-metadata` | 2 | ✓ | — | `verify-metadata-privacy.ps1:135,142,181` |
| **`--strip-time`** | 2 | **0** | **零覆盖** | L0 轴：`_lib-matrix.ps1:622` |
| **`--strip-camera`** | 2 | **0** | **零覆盖** | 同上 |
| **`--strip-all-exif`** | 2 | **0** | **零覆盖** | 同上 |
| **`--strip-xmp`** | 2 | **0** | **零覆盖** | 同上 |
| exiftool 编辑器 ~90 字段 | ~90 | 少量 | 面太大，本轮不逐字段量化 | `ExifToolService.cs`（`MetadataCategory` 枚举 `:1014`） |

**这 4 个 strip 开关是真实接线、真实落地的功能**（非死代码），证据：
```
CliParser.cs:246,250,254,258   → 置位
CliParser.cs:669-672           → 映射进 FfmpegOptions
ExifToolService.cs:433,475,530,541,685,801  → 消费（--XMP:all / 删除表达式）
QueueProcessor.cs:299,1866,2466 → 参与"是否需要后处理"判据
```
⇒ **真实功能 + 零断言 = 最优的缺陷温床**（改坏了没有任何东西会响）。

### 2.9 输出行为：no-overwrite / preserve-structure / dry-run / 退出码 / 半成品清理

| 维度 | src 取值数 | 覆盖 | 盲区 | 证据 |
|---|---|---|---|---|
| `--no-overwrite` | 2 | **0** | **零覆盖**（P0 修复） | `无`（唯一命中 `_probe-cjk-hardcode-scan.ps1:352` 为注释） |
| `--overwrite` | 2 | **0** | 零覆盖 | `无` |
| `--preserve-structure` | 2 | **1** | 仅"能产出"级 | `verify-usability-e2e.ps1:247` |
| `--dry-run` | 2 | **✓** | — | `verify-startup-warmup.ps1`（8 行报告相等）、`verify-color-token-normalize.ps1` |
| 退出码 `2`（取值非法） | — | ✓（部分） | 仅"CLI 参数错误"这一路 | `TESTING.md:978`（`Program.cs:282-286`）；`verify-gainmap-engine.ps1`（base-gamut 非法 ⇒ exit 2） |
| 退出码 `98`（UI 宿主缺 PLAN） | 1 | ✓ | — | `TESTING.md:43` |
| **半成品清理** | — | **0** | **零覆盖**：非零退出码但已产出半截文件时不清理 | `无` |
| `--concurrency`/`-j` | 3 | 部分（2 条） | — | `verify-jxl-probe-timeout.ps1` |
| `--threads` | 4 | **0** | 零覆盖 | L0：`_lib-axes.ps1` |
| `--log-file` | — | ✓（20 条 ACTIVE，6 脚本） | — | `verify-metadata-privacy.ps1` 等 |

---

## 3. 「有意不覆盖」清单 —— **不要当缺口报**

> 这一节是给后续写测试的人**避雷**用的。以下"未覆盖"是**裁定过的设计**，不是遗漏。

| 项 | 裁定 | 依据 |
|---|---|---|
| `_lib-axes.ps1` / `_lib-color-assets.ps1` / `_lib-matrix.ps1` / `_lib-oracle.ps1` / `_lib-reject.ps1` | **A 类 = 共享库，永远不该进清单**（进去会被当门禁跑 ⇒ 撞"零断言 ⇒ 红"闸） | `TESTING.md:431`、`:231` |
| `verify-oracle-matrix.ps1` | **有意不入列**：L1/L2/L3 执行层，≈2.5 h 长跑，只手工跑 | `TESTING.md:5244`、`:3524`、`_run-step3-gates.ps1:970` |
| `_probe-*` 16 条（C 类一次性取证） | 绝大多数无 `PASS/FAIL` 契约 ⇒ 接进去等于改判据形状 | `TESTING.md:437-445` |
| `final-verify.ps1` / `regtest-fix.ps1` / `verify-remaining.ps1` | **D 类已腐坏**（指向不存在的 `publish/build/`）⇒ **不是缺口，是待归档** | `TESTING.md:448-452` |
| `verify-tiff-icc.ps1` / `verify-webp-hdr-fix.ps1` / `verify-colorfmt-matrix.ps1` | **表格型：无 PASS/FAIL 契约**（有意保持人工读数） | `TESTING.md:220,223`、`:5425` |
| `tooldetect` 探针 | **有意不进 `$Probes`**（环境依赖会翻转判据） | `TESTING.md:322-324` |
| `smpte428` 色彩矩阵 | **故意不收录**（重复对白名单） | `_run-step3-gates.ps1:17-27` |
| `peak` 探针 | 需文件参数 ⇒ 不进默认清单；已由 `verify-color-peak.ps1` 覆盖 | `_run-step3-gates.ps1:74-76` |
| `JpegGainMapBaseHdr` / `jpeg-gain-map-base-hdr` | **契约已从产品移除**（src 零引用）；门禁断言其为 **no-op** | `TESTING.md:217` |
| legacy 侧裁决"有意去重"（引擎路径可能一行不打） | **不是漏打** | `TESTING.md:272-276` |
| `--color-gamut-map` 的 `off` 又名"默认" | 语义等价类，**不是两个取值** | `CliParser.cs` 注释；`_lib-matrix.ps1:58` |

⚠ **与"有意不覆盖"相邻但性质相反的一类**，必须区分并**当作缺口**：
`_probe-settings-clone-coverage.ps1` 的教训（`TESTING.md:443-445`、`_run-step3-gates.ps1:1096-1110`）——
它 2026-09-15 就写好且**能抓缺陷**，但**不在清单 ⇒ 没人跑 ⇒ 期间真的又漏了一个 `RenderingMode`**。
⇒ 判据：**"能判红产品"的脚本不入清单 = 缺口**；**"无契约/无牙"的脚本不入清单 = 有意**。

---

## 4. 未覆盖组合清单（按「抓到真缺陷的概率」排序）

> **这一节是给 P3 直接消费的。** 每条给出：为什么值得测 / 预期抓什么类别的缺陷 / 最小测试设计。

### 🥇 Top 10

| # | 组合 | 为什么值得测 | 预期抓到的缺陷类别 | 最小测试设计 |
|---|---|---|---|---|
| **1** | `--no-overwrite` × 目标已存在 | **2026-10-05 才修的 P0 数据损失**（`CliParser.cs:162-169` 自述"重跑一次就静默毁掉上次产物、退出码 0"）。修复点 `QueueProcessor.cs:348-358` 是**新代码**，且默认**仍是覆盖** ⇒ 一个默认值写反就回到 P0 | **静默数据损失**：`outputExistedBefore` 判据取错、`AllowOverwriteExisting` 默认值被改、`--overwrite` 反向失效 | 3 格：① 预置产物 → `--no-overwrite` ⇒ **exit≠0** 且既有文件**字节不变**；② 同格不传 ⇒ 覆盖（负控，防断言恒真）；③ `--no-overwrite` 后接 `--overwrite` ⇒ 后者生效（last-wins 口径，与 `--preserve-metadata` 同规）。判据用**产物哈希**，不用退出码文字 |
| **2** | 4 个细分 strip 开关 × 容器 | `--strip-time/camera/all-exif/xmp` 是**真实接线**（`ExifToolService.cs:433-801` 六处消费）但 **`ps1` 与 `cs` 命中均为 0**。其中 `StripExifTime` 参与 `QueueProcessor.cs:2466` 的"是否后处理"判据 ⇒ 改坏了**不会有任何东西响** | **隐私泄露**（该删的没删）、**误删**（不该删的删了）、`StripExifAll` 与 `StripExifGps` 的**优先级倒置** | 4 开关 × 2 容器（jpg 走 exiftool 后处理 / png 绕过后处理，`TESTED` 已知 png 会绕过 `-map_metadata 0`，见 `verify-metadata-privacy.ps1:130` 的注释）。每格**正控 + 负控**：断言目标类别**消失**且**其他类别仍在**（后者防"一律清空"式假绿）。复用 `verify-metadata-privacy.ps1` 的"GPS/ImageDescription/MeteringMode 有无"读数法 |
| **3** | `--hdr-mode` gainmap vs traditional × 容器 | **唯一一个 `ps1`+`cs` 双零命中**的 HDR 开关，而它是**用户-facing 的二选一真值**（`CliParser.cs:891-900`），且与 `--jpeg-gain-map` **写同一个持久位**（`HdrMode` 是 `JpegGainMap` 的强类型视图） | **两个真值漂移**（同一开关两种写法给出不同结果）、**装不下 HDR 的容器静默降级**（枚举注释明说"不得静默降级"）、last-wins 口径失效 | 4 格：`--hdr-mode gainmap|traditional` × `--jpeg-gain-map true|false` 两两组合，断言 `options.JpegGainMap` / `HdrMode` **一致**；再加 2 格**顺序对调**（`--jpeg-gain-map true --hdr-mode traditional` vs 反向）断言**后出现者生效**。全部可在 `ui-cli-strict` 层做（进程内，秒级），无需素材 |
| **4** | Orientation `2/3/4/5/6/7`（7 个无夹具态） | 现有夹具**全是 8**。`ImageGeometry.ApplyRotation`（`ImageGeometry.cs:42-46`）是按 `Math.Abs(rot)%180==90` 分派的**两态函数**，但它的注释**自己点名了 `6`**（":39-40 ffprobe 对 8 报 90、对 6 报 **-90**"）⇒ 作者知道符号会反，靠 `Math.Abs` 抹平，**而这处抹平零断言**。取向缺陷历史代价已知（"产物按行错位重排"、查看器二次旋转） | **符号抹平失效**（去掉 `Math.Abs` ⇒ `6` 静默不交换，产物朝向错）、**显示尺寸口径在 6/8 之间倒置**、**镜像态 `5/7` 的行为未定义**（可能"缺功能"而非缺陷，须先定性） | 最小集 = **3 格**（不用全 8 态，避免把"未实现"当成"坏"）：`6`（-90）、`8`（+90）、`3`（180）。非方夹具（coded 64×32，复用 `GeometryGroup.cs:809`）。判据：`ProbeSizeAsync` 报的**显示尺寸**轴向。⚠ **打标必须带 `-n`**（否则 exiftool 按字符串查表静默写错，`GainMapDecodeGroup.cs:445` 实测坑）。进阶：先跑 `5/7` 观察**当前行为**并把它**如实登记成契约**（是"拒绝"还是"不翻转"），再决定是否加锁 |
| **5** | `--color-sdr-white` / `--color-sdr-peak` | 两者都经 `ColorIntentFactory.cs:349-350` 落进 `ToneMapParams`，再进 `ColorTransformPlan.cs:431`（`Math.Max(1.0, Tone.SdrWhiteNits)`）与 `ToneMapping.cs:139`（`k = SdrWhiteNits/PeakNits`）。**`--color-sdr-peak` 的默认值语义微妙**：`FfmpegOptions.cs:600` 默认 **100**，而 `ToneMapping.cs:53` 注释强调"与 SdrWhiteNits=203 是两个不同的量，不可混用" ⇒ **混用是设计者自己标注的风险** | **两个量混用**（把 peak 当 white）、**除零/负值**（`Math.Max(1.0, …)` 之外的分母没有保护）、非法取值未拒绝 | ① 解析层：`--color-sdr-white 100` ⇒ `ColorSdrWhiteNits==100`；`--color-sdr-peak 300` ⇒ `==300`；`0`/负数/`abc` 的**当前行为**必须显式登记（`ParseDoubleStrict` 会拒绝 `abc`，但 `0` 与负数的语义未定义）。② 端到端：HDR 源 → `--color-tone-map bt2446a` + `--color-sdr-peak 300`，断言 `Tone.SdrPeakNits==300` **且** headroom 变化方向正确（对照 `CoreGroup.cs:1454` 已有的透传断言，把它从"对象赋值"升级为"CLI 驱动"） |
| **6** | **L1/L2/L3 组合用例的执行接线**（元缺口） | `_lib-axes.ps1` **197 条 L1** + `_lib-matrix.ps1` **171 条 L2 + 4624 条 L3** 已写好且自检全绿（19/0、38/0），但**执行层 `verify-oracle-matrix.ps1` 有意不入清单** ⇒ **72 条受管门禁里没有一条跑组合用例**。`_probe-matrix-structure.ps1` 只做**结构与行锚**自检，**不执行任何用例**。这正是 `TESTING.md:443-445` 记载的那类"能判红产品却没人跑" | **轴表与产品漂移**（`_lib-axes.ps1` 的 `-Source <file>:<line>` 锚点腐烂）、**用例 Args 与实际 CLI 语法脱节**（用例只因没人跑才"全绿"） | **不要**把 2.5 h 全量塞进每轮。最小可行：在受管清单里新增一条**秒级冒烟**，跑 `verify-oracle-matrix.ps1 -Layer L1 -Smoke`（3 条代表用例）或 `-Layer L1 -MaxCases 12`，并把 **`AXES RESULT`/`MATRIX RESULT` 的 pass 下限**钉住（防"整段没跑 ⇒ exit 0"假绿，同 `verify-simd-switch-fallback.ps1` 的 `pass>=25` 手法）。⚠ 该脚本 **fail-closed**（"没有产物一律记 FAIL"），冒烟档要选**能产出**的格，否则会引入假红 |
| **7** | `-e Jxr` / `-e Dng` 显式后端 × 兼容性判定 | **比"少测两个后端"更严重**：`EncoderBackendCompat.IsCompatibleWith`（`EncoderDetectionService.cs:29-44`）是一个 **5 分支 switch**，源码注释自述它存在是为了"防止产出**扩展名与真实内容不符的错标文件**（例如 backend=Cjxl 但 format=avif，会写出内容为 JXL 的 `.avif`）"，且"CLI 默认后端推断、队列真实分派、dry-run 命令预览**三处共用**此逻辑"。⚠ **该函数在全部 41 个 live 测试 `.cs` 里 `IsCompatibleWith`/`InferDefaultBackend`/`EncoderBackendCompat` 命中均为 0**（命令：`Select-String -Path $allcs -Pattern 'IsCompatibleWith\|InferDefaultBackend\|EncoderBackendCompat'` ⇒ 空） | **错标文件**（后端与格式不匹配 ⇒ 内容/扩展名不符，正是注释点名的缺陷形态）、**`_ => true` 的兜底分支**吞掉未来新增后端、`InferDefaultBackend` 的"工具不可用则回退 Ffmpeg"（`:55-57`）在工具缺席时报错而非回退 | **纯进程内、不跑编码**（秒级）：`IsCompatibleWith` 做 5 后端 × 10 格式 = 50 格，**两侧都断言** —— 标出应为 `true` 的格（`Cjpegli×{jpg,jpeg,jpegli}`、`Cjxl×jxl`、`Jxr×jxr`、`Dng×dng`、`Ffmpeg×任意`）与应为 `false` 的格（`Cjxl×avif` 等）。⚠ 单侧断言会被"恒返回 true"蒙混 ⇒ **必须同时存在 true 与 false 的期望格**（同 `_lib-axes.ps1` B2/B3 负控的思路）。再加 3 格 `InferDefaultBackend` 在工具缺席下的回退 |
| **8** | `--log-format JsonLines` | `ConsoleQueueObserver.cs:203` 有 `if (_logFormat == LogFormat.JsonLines)` 的**完整分支**（`:236` 是 `Text` 分支），**从未被执行**。日志格式是自动化消费者的契约（门禁自己也 grep 日志） | **JSON 转义错误**（中文/引号/换行未转义 ⇒ 下游解析崩）、**JsonLines 下判据行消失**（门禁依赖的 `[cmd]`/`[色彩裁决]` 是否仍被打）、**默认值写反** | 1 格即可见效：真跑 `--log-format JsonLines --log-file <f>`，断言 ① 每行都是**合法 JSON**（`ConvertFrom-Json` 逐行）② 关键锚点载荷（`[cmd]` 的 exe+args）**未丢失** ③ 对照 `Text` 档同样能取到 ④ 非法值 `--log-format YAML` ⇒ ArgumentException（现在是否拒绝需先实测登记） |
| **9** | `--jpeg-gain-map-downsample` / `-quality` / `-target-nits` | 三个都是 `_lib-matrix.ps1:641` 的 `gainmap-core` **矩阵轴**（即矩阵设计者认为它们值得笛卡尔覆盖），但**执行层不受管**且**受管门禁 0 命中**。Ultra HDR 是产品主卖点之一 | **参数未透传到编码器**（`ImageEncoderArgs` 里漏拼）、**downsample 档位取值被静默归一**、`-quality` 的 `-1` 哨兵语义（`CliParser.cs:901-902` 自述"用默认 75"）落错 | 复用 `verify-gainmap-engine.ps1` 的**第三方可证**手法（`exiftool` 读 XMP hdrgm 字段 / 读底图 ICC 色度），而不是读产品日志散文。3 格 × 2 值，断言 XMP/编码器命令行里**真的出现**对应参数值。`-quality -1` 单独一格断言落到 75 |
| **10** | `--auto-threads` × `-j` 并发 | `QueueProcessor.cs:259-263` 的 `max(1, 核数/并发)` 是**运行时分支**；`MainWindow.xaml.cs:1425,2871,2876` 还有 GUI 侧的第二套算法与 `adaptiveAuto = autoThreads && !singleThread`。`--auto-threads` 在受管门禁 **0 命中** | **线程超订阅**（每任务线程 × 并发 > 核数）、**GUI 与 CLI 两套算法漂移**、`--auto-threads` + 单线程的交互（`:1774-1776` 会强制取消勾选） | 解析层 + 轻量端到端：断言 `--auto-threads` ⇒ `AutoThreads==true` 且最终命令行里 `-threads` 的值 == `ComputeAutoThreads()`（**单一真值**）；再加 `-j 1` / `-j 16` 两格断言不超订阅。⚠ 别断言具体核数（环境相关 ⇒ 假红） |

### 🥈 次级（值得做，但优先级低于 Top 10）

| # | 组合 | 一句话理由 |
|---|---|---|
| 11 | `--preset <name>` CLI 名（非 `ApplyPresetData`） | 预设**库查找**、**未知预设名**、`--settings` 与 `--preset` 的**优先级**均无断言；`TESTING.md:715` 只说 `--settings` 点名的文件"绝不回退" |
| 12 | `--color-allow-unlabeled` | TIFF"未标注语义不统一"是 `ColorStrategy.cs` 注释里明写的**需要显式开启**的危险位，零覆盖 |
| 13 | `None`/`Manual` 策略 × 5 引擎出口的 `VerifyRequiresEngine` 支路 | `ColorEngineRouter.cs:102` 的 `VerifyRequiresEngine` 只在 `--color-engine engine` 下生效，未见逐出口断言 |
| 14 | `-f dng` 产品路径（非 `dngtool` 直连） | `run-pipeline-tests.ps1:350-385` 全在调 `dngtool.exe` 本身 ⇒ **产品封装那层**（`DngService`）无覆盖 |
| 15 | 半成品清理 | 非零退出码 + 已产出半截文件**不清理**，会污染后续"目标已存在"判据（与 #1 耦合） |
| 16 | `--gif-palette` / `--gif-dither` / `--threads` | 轴表登记齐全、执行层 0 命中；gif 调色板是"色彩能不能交付"的常见坑 |
| 17 | `--color-tone-map reinhard` 端到端 | `verify-color-wiring.ps1:152` 用的是**旧轴** `--tonemap-curve`，而 `_lib-reject.ps1:357` 明确记载该轴**结构上不被引擎消费** ⇒ `reinhard` 缺**有效**端到端 |
| 18 | `--icc-file` 外部 ICC 判定源 | 轴表有 2 值；"用外部 ICC 判定**源**色彩空间"是 `IccProfileService` 的独立通路 |
| 19 | exiftool 编辑器 ~90 字段 | 面太大，建议按 `MetadataCategory`（`ExifToolService.cs:1014`）**分组抽样**，不做全笛卡尔 |
| 20 | `--animation-duration` / `-loop` 端到端 | 解析层有（空格≡内联），但"循环次数/时长真的写进容器的 `-loop`/`-t`"无产物断言 |

---

## 5. 方法学与局限（诚实登记）

1. **`ACTIVE` 判据是保守近似**：分类器只处理行内 `#` 前置注释（含引号状态机），**不做完整 PowerShell 词法**。
   已知局限：含 `#` 的字符串字面量会被当成注释起始 ⇒ 可能把真代码误判成 `COMMENT`（只会**低估**覆盖，
   不会高估）。**本文所有"零覆盖"结论都用两条独立命令复核过**（命令 A 的 CSV + 命令 D 的词边界统计），
   两者一致。
2. **`-Tokens` 数组不能经 `pwsh -File` 传**（会被折叠成一个参数）⇒ 命令 A 的复现要走 `-Command "& './…' -Tokens @(…)"`。
   这是本仓 `_run-step3-gates.ps1:455` 登记过的同一个坑（`-Scripts a,b` 形态）。
3. **本轮未逐字段清点 exiftool 的 ~90 个编辑器字段**（第 19 项）。这是一个**登记在案的未量化项**，不是"已覆盖"。
4. **未运行任何门禁**：本审计是**静态 + 定点读数**，所有"已覆盖"结论来自**读到的断言行**，
   不是"我跑过它所以它是绿的"。基线读数由 `baseline-runner` 负责（共享任务分工）。
5. **`tests/output/` 下的两份陈旧副本**（`tests/output/worktree_salvage/e6ede830/files/src/FfmpegGui/CliParser.cs`）
   **不参与**统计 —— 命令 D 已用 `-notmatch '\\output\\'` 显式排除。若有人直接 grep `tests/**/*.cs` 会撞上它。
6. **`--help` 与源码的差异方向已知**：`--help` **少列**高级表（保守），未发现**多列**（不存在的选项）。
7. **⚠ 本轮自我订正一次（留痕，不隐藏）**：本报告初稿在 Top 10 #4 写过"`ApplyRotation` 有单独的 `flip` 分支
   ⇒ 该分支零覆盖"——核源码后**发现是错的**：`ImageGeometry.cs:42-46` **只有两态**（交换/不交换），
   没有镜像分支。已按源码事实改写 §2.7 与 Top 10 #4。
   ⇒ 这条留在这里是**方法学证据**：即使带着"该读源码"的纪律，**直觉仍会替代码补出一个不存在的分支**。
   本报告其余"零覆盖"结论均已用命令 A + 命令 D **两条独立命令**复核，但**读者若要引用某一格，仍应自己打开那一行**。

---

## 6. 结语：本次审计最该被记住的一句

本仓在**单个轴的深度**上做得非常好（色彩管线 81 项、GainMap 117 项、几何 23 项，夹具自检、
负控、变异验牙一应俱全），但在**轴的组合**与**"新修的 P0 是否被锁住"**这两件事上系统性薄弱：
组合用例的**三层地基（197+171+4624 条）已建好却全部悬空**，而近期的 P0 修复
（`--no-overwrite`、`--raw-color-target` 的 GUI 接线）中只有后者补了锁。
⇒ **P3 的最高杠杆动作不是"再多写一条 verify"，而是**：
① 给 `--no-overwrite` 加行为锁（Top 10 #1，P0 且零覆盖）；
② 把 L1/L2 组合用例以**秒级冒烟**接进受管清单（Top 10 #6，一次性让 368 条已存在的用例开始承重）。
