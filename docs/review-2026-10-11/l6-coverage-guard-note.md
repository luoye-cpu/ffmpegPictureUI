# L6 · `_probe-test-group-coverage.ps1` 判据改造：子串取证 → 真实调用关系（2026-10-11）

时间：2026-10-11。范围：**单文件** `tests/scripts/_probe-test-group-coverage.ps1`。
未改 `_run-step3-gates.ps1`、未改 `src/**`、未改 `tests/**/*.cs`、无 git 写操作、未跑整轮门禁
（只直接跑本守卫）。宿主 = `pwsh` 7.6.6；`TMP/TEMP` 全程指向 `C:\PLAN\ffmpegPictureUI\tests\output\l6-tmp`。

## 0 结论一句话

旧判据 ③(b) 是「mode 名出现在 **任何** `tests/scripts/*.ps1` 的**全文文本**里」⇒ 注释、非清单脚本、
守卫自己的第 20 行注释都能作证 ⇒ 零调用点的组被算成已覆盖，实测 `PASS=8 FAIL=0` 全绿。
改成「**受管清单内的参数位置**才算调用点（AST 取证）」后，同一份仓库实跑
`PASS=18 FAIL=2`，**逐名点名 8 个零调用点 + 1 个只有非清单调用者**，退出码 1。

## 1 改前读数（逐字，取自改造前的同一条命令）

命令：

```bash
cd /c/PLAN/ffmpegPictureUI && \
  TMP=/c/PLAN/ffmpegPictureUI/tests/output/l6-tmp \
  TEMP=/c/PLAN/ffmpegPictureUI/tests/output/l6-tmp \
  pwsh -NoProfile -File tests/scripts/_probe-test-group-coverage.ps1
```

读数：

```
===== test-group-coverage =====
PASS ④-a 反控：解析到 ≥9 个组（实 17）—— 防组表被清空成恒绿
PASS ④-b 反控：解析到 ≥26 个运行器探针（实 26）—— 防 `$Probes` 被清空成恒绿
PASS ④-c 反控：组表 mode 并集 ≥46（实 66）
PASS ① 运行器 $Probes 的每个 mode 都有组认领（未认领：无）
PASS ② 组表里每个组都有工程目录（缺目录：无）
PASS ③ 组表每个 mode 都真实存在（三重取证 a/b/c 全不命中：无）
PASS ⑤-a 反控：解析到 ≥40 个 mode == 分派（实 66）—— 防正则失配导致恒绿
PASS ⑤ 组表每个 mode 都**真的被分派**（声明了却没实现：无）
ℹ      组表并集 = 66 个 mode；运行器 $Probes = 26 个
ℹ      仅运行器有（组表缺，应为 0）= 0
ℹ      仅组表有（由专项门禁脚本调用，正常）= 40：bench, bitdepth, faithful, firstframe, gainmap, gainmap-decode, gainmap-encode, gainmap-isobmff, gainmap-struct, gamut, icc, icc-dump, orientation, orientmeta, peak, peakmeasure, png3, pq, psnr, simdswitch, tooldetect, ui-background-visibility, ui-cli-strict, ui-color, ui-encoder-args, ui-encoder-linkage, ui-end-to-end, ui-format-command, ui-gainmap-toggle, ui-icc, ui-panel-visibility, ui-param-defects, ui-parameter-passing, ui-preset-roundtrip, ui-simple-mode, ui-startup-detect, ui-strategy-map, ui-sweep, ultrahdr, xcheck
===== test-group-coverage: PASS=8 FAIL=0 =====
EXITCODE=0
```

**汇总 40 条、点名 0 条**（只给了一句"由专项门禁脚本调用，正常"的推断，没有逐条取证）⇒ 正是本仓
「汇总数量必须与点名条数相等」要拦的形状。

## 2 改后读数（逐字）

同一条命令（改造后）：

```
===== test-group-coverage =====
PASS ④-a 反控：解析到 ≥9 个组（实 17）—— 防组表被清空成恒绿
PASS ④-b 反控：解析到 ≥26 个运行器探针（实 26）—— 防 $Probes 被清空成恒绿
PASS ④-c 反控：组表 mode 并集 ≥46（实 66）
PASS ④-d 反控：从运行器 AST 解析到 ≥50 条受管基线脚本（实 73）—— 防清单解析失配成空表恒绿
PASS ④-e 账目：运行器 $expectedScriptCount = 73 与 AST 实解析 73 条相等
PASS ⑧ $scriptList 每条都逐名在盘（幽灵条目：无，点名 0 条）
PASS ⑥-pre 有牙自证：合成件 2 组 / 6 项判据方向都对（全部符合（正例命中 / 注释与文案不作证））
PASS ④-f 反控：扫到 ≥40 条起测试宿主的命令（实 91）—— AST 取证失配/被绕过时拦死
PASS ④-g 反控：调用点索引覆盖 ≥26 个 mode（实 35）—— 防整表空转成恒绿
PASS ④-h 取证域完整：tests/scripts 下 .ps1 全部解析通过（失败：无，点名 0 个）
PASS ① 运行器 $Probes 的每个 mode 都有组认领（未认领：无，点名 0 条）
PASS ② 组表里每个组都有工程目录（缺目录：无，点名 0 条）
PASS ③ 组表每个 mode 名都真实存在（a/b/c 全不命中：无，点名 0 条）
PASS ⑤-a 反控：解析到 ≥40 个 mode == 分派（实 66）—— 防正则失配导致恒绿
PASS ⑤ 组表每个 mode 都**真的被分派**（声明了却没实现：无，点名 0 条）
WARN ⑥-a 仅非清单脚本调用：icc（组 metadata）—— 调用点 ensure-fixtures.ps1:L97(argv) 不在 $scriptList 内 ⇒ 不担保跑到
PASS ⑥-a 账目：第二档（仅非清单调用）汇总条数 == 逐名点名条数（1）
FAIL ⑥-d 每个已覆盖 mode 的调用者都在 $scriptList 内（只有非清单调用点：1 个，已逐名点名 1 条 ⇒ icc）
FAIL ⑥-b 零调用点：bench（组 diagnostics）—— 既不在 $Probes，也不在 $scriptList 任一条的参数位置上
FAIL ⑥-b 零调用点：bitdepth（组 encode）—— 既不在 $Probes，也不在 $scriptList 任一条的参数位置上
FAIL ⑥-b 零调用点：gainmap-decode（组 gainmap-decode）—— 既不在 $Probes，也不在 $scriptList 任一条的参数位置上
FAIL ⑥-b 零调用点：gainmap-encode（组 gainmap-encode）—— 既不在 $Probes，也不在 $scriptList 任一条的参数位置上
FAIL ⑥-b 零调用点：gainmap-struct（组 gainmap-struct）—— 既不在 $Probes，也不在 $scriptList 任一条的参数位置上
FAIL ⑥-b 零调用点：gamut（组 engine）—— 既不在 $Probes，也不在 $scriptList 任一条的参数位置上
FAIL ⑥-b 零调用点：pq（组 color）—— 既不在 $Probes，也不在 $scriptList 任一条的参数位置上
FAIL ⑥-b 零调用点：ultrahdr（组 gainmap）—— 既不在 $Probes，也不在 $scriptList 任一条的参数位置上
FAIL ⑥-c 每个声明的 mode 都有**真实调用点**（零调用点 8 个，已逐名点名 8 条）
PASS ⑦-a 账目：四档汇总 66 条 == 组表并集 66 条（不等即有点名被吞或重复计数）
PASS ⑦-b 账目：逐名点名 66 条 == 汇总 66 条
ℹ      组表并集 = 66 个 mode；运行器 $Probes = 26 个；受管基线 = 73 条（证据源 72 条，去掉守卫自己）
ℹ      档① 在 $Probes 里（运行器自己起宿主）= 26：avifgm, bt2446, colormath, …, verdict, wire
ℹ      档② 由 $scriptList 内脚本的参数位置调用 = 31：faithful<-verify-prophoto-faithful.ps1:L98(argv), …, xcheck<-verify-color-engine-xcheck.ps1:L77(argv)
ℹ      档③ 只被非清单脚本调用（不算已覆盖）= 1：icc
ℹ      档④ 零调用点（转红）= 8：bench, bitdepth, gainmap-decode, gainmap-encode, gainmap-struct, gamut, pq, ultrahdr
===== test-group-coverage: PASS=18 FAIL=2 =====
FINAL_EXIT=1
```

**点名条数 / 汇总条数**：零调用点档 点名 8 == 汇总 8（`⑥-c` 行内自证）；非清单档 点名 1 == 汇总 1
（`⑥-a` 行内自证）；四档 26 + 31 + 1 + 8 = **66 == 组表并集 66**（`⑦-a`），逐名点名合计亦 66（`⑦-b`）。
退出码 **1**（非零 = 有红，契约不变；末行格式 `===== test-group-coverage: PASS=n FAIL=m =====` 不变，
末行逐字：`===== test-group-coverage: PASS=18 FAIL=2 =====`，输出里无 ANSI 转义）。
耗时实测 **41.1 s**（清单 `ScriptTimeoutSec = 900` 预算内；原实现约 1 s，成本花在 115 个 .ps1 的 AST 解析上，
已加"整篇无 mode 名整 token 就跳过 AST"的预筛与线性宿主形状判据）。

## 3 新判据（写死在脚本头注释里，此处只摘口径）

证据只认三形态，全部由**宿主自己的 AST**（`…Language.Parser`）取，注释在 AST 里不是字符串节点：

- (A) `mode ∈ $Probes` —— 运行器把它作为 `-Arguments @($m)` 喂给 ServiceProbe（`_run-step3-gates.ps1:827`）。
- (B) `mode` 以**整 token、大小写敏感**出现在 `$scriptList` 内某条脚本里，且落在**起测试宿主的那条命令的参数子树**内。
  宿主 = `ServiceProbe|GainMapTestHost|UiTestHost|Tests.*` + `.exe|.dll`，或 `tests/**/bin/**` 下的 exe/dll；
  launcher 递归识别（体内 `Start-Process -FilePath <宿主>` 的本地函数，如 `Exec`/`RunTool`/`ProbeOut`/`Invoke-Exe`）。
- (C) `mode` 是**数据表字面量**（数组/哈希表元素，整串等于 mode 名），且该表**成链**喂进某条起宿主的命令
  （`modes=@('ui-format-command',…)` → `foreach ($mode in $modes[…])` → `Invoke-Exe $exe @($srcDir, $mode)`）。
  孤立数据表不算（实测挡掉了 `verify-jbrd-e2e.ps1:219 $want=@(…,'ultrahdr')` 这种**夹具键名表**、
  `verify-color-token-normalize.ps1:137 @{Alias='pq';…}` 这种 **CLI 色域 token 表**、
  `verify-encoder-knob-liveness.ps1:150 lo=@('--avif-tune','psnr')` 这种**产品选项值表**）。

明确**不再**是证据：注释（含守卫自己那句 `如 bitdepth/faithful/…`）、报告/文案命令的参数
（`Write-Host`/`Write-Output`/`Say`/`CK`/`Ok`/`No`/`Assert`/`Check` —— 按"自身体内不起进程"结构性剔除）、
非清单脚本、子串（`neg_bitdepth.png` / `--color-gamut-map` / `dng_bench_` / `BitDepth` 大小写不符）、
**守卫自己**（虽在清单第 69 条，但不作为自己的证据源）。

## 4 有牙自证（两条必需验证，都只跑本守卫）

### 4-1 已知零调用点 ⇒ 报成未覆盖

`bitdepth`、`gainmap-struct` 均出现在改后读数的 `FAIL ⑥-b` 行（逐字见上）。
独立复算（不受本守卫实现影响）：

```bash
# 全仓（含 src、含所有 115 个 .ps1）里 `bitdepth` 的**引号整 token** 形态只有分派/声明，0 处调用
grep -rn "'bitdepth'\|\"bitdepth\"" --include=*.ps1 --include=*.cs . \
  | grep -v "/obj/\|/bin/\|tests/output/\|docs/"
#   → tests/encode/Program.cs:58（分派）、tests/ServiceProbe/Program.cs:117（case）、tests/Tests.Shared/TestGroups.cs:33（声明）
# gainmap-encode|decode|struct 在 tests/scripts 下 0 处
grep -rn "gainmap-encode\|gainmap-decode\|gainmap-struct" tests/scripts/   # → NONE
```

### 4-2 构造"注释里提到未被调用的名字"的假覆盖场景 ⇒ 新判据不上当

守卫内置 `⑥-pre` 合成件（内存里 `Parser::ParseInput`，不落盘），fixture-1 同时含
**注释**（`# …（如 bitdepth/bench/gainmap-struct）…`）、**文案**（`Write-Host "本轮跑 bitdepth 与 bench"`、
`Write-Output "gainmap-struct 由专项门禁调用，正常"`）、**非成链表**（`$note = 'bench 已覆盖'`）
与**真 argv**（`Exec $probe "bitdepth in out"`）。断言方向：`bitdepth` 必须命中（来自 argv），
`bench`/`gainmap-struct` 必须不命中。实跑：`PASS ⑥-pre …（全部符合）`。

再做两次受控变异（各只跑本守卫，之后逐字节还原，md5 复核一致）：

| 变异 | 改动 | 读数 | 证明 |
|---|---|---|---|
| **M1** | 删掉 fixture-1 里唯一那行真 argv `Exec $probe "bitdepth in out"`（注释/文案仍在） | `FAIL ⑥-pre …：正例 bitdepth 没命中（尺子空转）`；`PASS=17 FAIL=2`，exit 1 | 注释 + 文案 + 孤立数据表**一条都作不了证** —— 这正是旧判据被骗的那三种形态 |
| **M2** | 把 `bench` 塞进真 argv：`Exec $probe "bitdepth bench in out"` | `FAIL ⑥-pre …：负例 bench 竟被作证（实为 argv:L9）`，exit 1 | 反方向不空转：判据确实认得"参数位置"，不是恒红 |
| **M3** | 让 `$Probes` 解析失配（比较名改成 `ProbesX`，实解析 0 条） | `FAIL ④-b …（实 0）` + `FAIL ④ 枚举为空（组 17 / 探针 0 / 清单 73）⇒ 拒绝判定，响亮判红`，exit 1 | 空枚举响亮判红，不静默返回绿 |
| **M4** | 让 `$scriptList` 解析失配（比较名改成 `scriptListX`，实解析 0 条） | `FAIL ④-d …（实 0）` + `FAIL ④-e …` + `FAIL ④ 枚举为空（组 17 / 探针 26 / 清单 0）⇒ 拒绝判定，响亮判红`，exit 1 | 同上，取证域塌了不能让绿继续 |

### 4-3 新旧判据在同一份语料上的直接对照

复刻旧 ③(b)（`tests/scripts/*.ps1` 全文拼接 + `[regex]::Escape($mode)` 子串）逐名看它怎么判：

```
OLD-SUBSTR-covered=True  bench           命中 7 处: _probe-test-group-coverage.ps1:39, :50, :394
OLD-SUBSTR-covered=True  bitdepth        命中 143 处: _lib-matrix.ps1:41, :384, :385
OLD-SUBSTR-covered=True  gainmap-encode  命中 1 处: _probe-test-group-coverage.ps1:51
OLD-SUBSTR-covered=True  gainmap-struct  命中 3 处: _probe-test-group-coverage.ps1:394, :396, :401
OLD-SUBSTR-covered=True  gamut           命中 83 处: _lib-axes.ps1:258, :316, :714
OLD-SUBSTR-covered=True  pq              命中 168 处: _lib-axes.ps1:266, :269, _lib-matrix.ps1:64
OLD-SUBSTR-covered=True  ultrahdr        命中 44 处: _run-step3-gates.ps1:962, :1170, probe-gainmap-tiff.ps1:1
OLD-SUBSTR-covered=True  icc             命中 972 处: _accept-ui-split.ps1:101, :232, _gen-ui-split.ps1:91
```

⇒ 旧判据把这 8 个**全部**判成"已覆盖"，而命中位置是注释/文案/夹具名/守卫自己的头注释与合成件；
新判据对同一批逐名转红。`gainmap-decode` 此行显示 False，仅因本次头注释写成
`gainmap-encode|decode|struct` 的合写形态，不构成反例。

## 5 双宿主兼容

`verify-ps-compat.ps1`（清单第 1 条）要求门禁脚本 PS 5.1 / PS 7 双宿主可跑。实测：

```bash
powershell -NoProfile -ExecutionPolicy Bypass -Command '… Parser::ParseFile(本脚本) …'   # PS5.1 parse errors = 0
TMP=… TEMP=… powershell -NoProfile -ExecutionPolicy Bypass -File tests/scripts/_probe-test-group-coverage.ps1
#   末行同样是 ===== test-group-coverage: PASS=17 FAIL=1 =====（该次跑在 ⑥-d 接线前），EXIT51=1
grep -nE '\?\?|\|\||&&|\?\[' tests/scripts/_probe-test-group-coverage.ps1   # 无 PS7 专属语法
grep -nP "[\x{201C}\x{201D}]" tests/scripts/_probe-test-group-coverage.ps1   # 无全角弯引号
```

## 6 本次踩到并写进注释的三个坑

1. **PS7 的 `ForEachStatementAst` 没有 `Collection` 属性**（实测 `GetProperties()` 只给
   `Body/Condition/Extent/Flags/Label/Parent/ThrottleLimit/Variable`）。第一版按 `Collection` 取集合表达式
   ⇒ 拿到 `$null` 静默空串 ⇒ 17 个 `ui-*` 的"分发表→宿主"成链判定整条断掉，被误判零调用点。
   现用 `Condition` + 取不到时按 Extent 偏移截正文兜底。
2. **宿主路径几乎都装在变量里**（`$pr`/`$probe`/`$exe`），对变量节点取"字面量片段"恒得空串 ⇒
   `Exec $pr 'peakmeasure'` 这类真调用位全部漏判。必须有 varMap 一层静态展开。
3. **`$Host` 是 PowerShell 只读自动变量**：`$host = …` 报"无法覆盖变量 Host"，且赋值失败后
   取到的是 `InternalHost` 对象 ⇒ 输出里"该组宿主"整列被污染。已改名 `$exeName`。
   另：`tests/…bin/…\.exe` 这类嵌套量词正则实测把守卫拖到 35 s+（回溯），换成线性 `Contains` 三段判定。

## 7 后果与欠账（要人定的事，本 lane 不动手）

1. **本守卫现在起是红的**（清单第 69 条），整轮 `_run-step3-gates.ps1` 会因它转红。这是修判据的**目的**，
   不是副作用；但要么补接线、要么按下面第 2/3 条处置，别用放宽判据解决。
2. 8 个零调用点逐名（每组宿主都已实现分派，缺的是"有人起它"）：
   `bench`（Tests.Diagnostics.exe / ServiceProbe `case "bench"`，需参数或计时，D1 §4 已记它不绑构建指纹）、
   `bitdepth`（Tests.Encode.exe）、`gamut`（Tests.Engine.exe，`RunGamut(args)` 需 `<jpg> <token>` 实参）、
   `gainmap-encode|decode|struct`（Tests.GainMap{Encode,Decode,Struct}.exe —— `verify-gainmap-host.ps1` 跑的还是旧单体 GainMapTestHost.exe）、
   `pq`（Tests.Color.exe）、`ultrahdr`（Tests.GainMap.exe）。
3. `icc`（Tests.Metadata.exe）唯一真调用点在 **非清单**的 `ensure-fixtures.ps1:L97`
   （`dotnet "ServiceProbe.dll" icc "Display P3"`），而运行器只在缺 `tests/output/validate/src_hdr.png`
   时才按需起它 ⇒ 正常机器上本轮不跑。现由 `⑥-d` 单独拦红（措辞与零调用点区分）。
4. **文档过期**（本 lane 无授权改 `docs/TESTING.md`）：`docs/TESTING.md:87` 仍写
   「结构锁全绿：`_probe-test-group-coverage` **8/0**」，本次改后实跑 **18/2**；`:447` 与
   `_run-step3-gates.ps1:1033-1038` 对第 69 条判据的描述（"③ 有据可查＝在 `$Probes`，或被某个专项门禁脚本调用"）
   也已与新实现不符（新实现把"被调用"收成 AST 参数位置，并新增 ⑥/⑦/⑧/⑥-pre）。这三处须由主循环同步。
5. 残留局限（如实标注）：(C) 的"成链"是**同文件一跳文本判据**（表变量名/foreach 变量名出现在起宿主命令文本里），
   跨函数传参或经 `$args` 间接拼接的调用位仍会漏认（漏认的方向是**转红**，不是假绿）；
   宿主识别按二进制名形状认，`dotnet <非宿主 dll>` 不误判。
