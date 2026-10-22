# ═══════════════════════════════════════════════════════════════════════════════
# _probe-test-group-coverage.ps1 —— 测试组覆盖一致性（2026-10-05 物理拆分时新增）
#
# 【为什么必须有】
#   拆分后「哪些 mode 归哪个组」是**手工维护**的表（`tests/projects/Tests.Shared/TestGroups.cs`），
#   而运行器 `_run-step3-gates.ps1` 另有一份**独立**的默认探针清单（`$Probes`，现 **27** 个；
#   2026-10-12 由 26 接进 `bench` = L-7 性能看守）。
#   两者一旦漂移，就会出现「运行器要跑某个 mode，但**没有任何组**认领它」
#   ⇒ 该 mode **永远不被任何组跑到** = **假覆盖**（看板上一切正常，实际没人跑）。
#   本仓有第 54 条的前车之鉴：`_probe-settings-clone-coverage.ps1` **存在但没上清单** ⇒ 谁也不跑。
#
# 【判据】
#   ① **运行器 `$Probes` 的每一个 mode 都必须至少被一个组认领**（否则转红并点名）。
#   ② `TestGroups` 里出现的每个组名都必须有**对应的工程目录**（否则转红）——
#      防「表里写了组、工程没建」这种"宣称≠交付"。
#   ③ `TestGroups` 里每个组声明的 mode 名必须**真实存在**（防拼错 mode 名）——取证三选一：
#      (a) 在 `$Probes` 默认清单里；(b) 有**真实调用点**（判据见 ⑥）；
#      (c) 是该组 `Program.cs` 里真实出现的引号字面量。
#   ④ **反控**：判据段本身必须真的读到非空的组表、探针清单、受管脚本清单，且调用点索引
#      非空（防被清空 / 解析失配导致恒绿）。**任何一处枚举为空 ⇒ 响亮判红并 exit 1。**
#   ⑤ 声明的 mode 必须**真的被分派**（`mode == "<mode>"`），堵"声明了却没实现"。
#   ⑥ ★ 2026-10-11 重写（审查 `docs/review-2026-10-10/lanes/D1-coverage-ledger.md` §3-C 第 2 条）：
#      **每个声明的 mode 都必须有真实调用点**。权威清单 = 运行器里的 `$scriptList`（受管基线）
#      与 `$Probes`（探针 mode）。取证用**宿主自己的 AST**（`…Language.Parser`），不再是全文子串：
#        (A) mode ∈ `$Probes` ⇒ 运行器自己把它作为 `-Arguments @($m)` 喂给 ServiceProbe 宿主；
#        (B) 它出现在 `$scriptList` **内**某条脚本里，且位于**起测试宿主的那条命令的参数子树**内。
#            宿主按**二进制名形状**认：`ServiceProbe.exe|dll`、`GainMapTestHost.*`、`UiTestHost.*`、
#            `Tests.*.exe|dll`，以及拆分工程的 `tests/**/bin/**….exe|dll` 路径形态
#            （刻意不用 `tests/.*bin/.*\.exe` 这种嵌套量词正则 —— 实测会把本守卫拖进回溯灾难）。
#            launcher 递归识别 —— 自身体内 `Start-Process -FilePath <宿主>` 的本地函数
#            （`Exec`/`RunTool`/`ProbeOut`/`Invoke-Exe`…）也算，调用点实参是宿主路径时同样算；
#            ⚠ 起**产品** `FfmpegGui.exe` / `ffmpeg` / `ffprobe` / `exiftool` 的命令**不是**测试宿主
#            （否则 `ffmpeg -lavfi "psnr"` 的滤镜名、`--avif-tune psnr` 的选项值都会替 mode 作证）；
#        (C) 它是**数据表字面量**（数组/哈希表元素，整串等于 mode 名），且该表在同文件里**真的
#            成链喂进了某条起宿主的命令**（`modes=@('ui-format-command',…)` →
#            `foreach ($mode in $modes[…])` → `Invoke-Exe $exe @($srcDir, $mode)`）；孤立数据表不算
#            —— 实测挡掉了"夹具键名表"与"CLI 色域 token 表"两类冒名顶替。
#      ⇒ 以下**都不是**证据（这正是被修掉的假绿面）：
#        · **注释** —— 注释不是 AST 字符串节点（旧判据 ③(b) 命中过本守卫**自己第 20 行的注释**）；
#        · **报告/文案命令的参数** —— `Write-Host`/`Write-Output`/`Say`/`CK`/`Ok`/`No`/`Assert`/`Check`
#          这类自身不起宿主的命令，其字符串参数只是散文；
#        · **非清单脚本** —— `ensure-fixtures.ps1`/`run-color-tests.ps1` 等没上 73 条基线的生产者，
#          单独点名成第二档（⑥-a WARN + ⑥-d 拦红：有调用者但**不受管**⇒ 不能算已覆盖）；
#        · **子串** —— `neg_bitdepth.png`／`--color-gamut-map`／`dng_bench_`／`BitDepth`(大小写不符)
#          一律不命中（整 token、大小写敏感）；
#        · **守卫自己** —— 本脚本虽是清单第 69 条，但**不作为自己的证据源**（否则它写什么就真什么）。
#      ⇒ 零调用点的 mode **逐名点名**，并给出"它本该被谁调用"（该组宿主 exe / 分派了它的 ServiceProbe，
#        以及 `$scriptList` 内引用过该组目录的候选收口脚本）。
#   ⑥-pre **有牙自证**（拿合成源码在内存里验这把尺子本身，不落盘、不起进程）：
#        正例（真 argv / 真成链数据表）**必须命中**，负例（同名只出现在注释/文案/孤立表里）**必须不命中**；
#        任一反向 ⇒ 转红。理由：判据一旦哪天失配（宿主正则被改松、launcher 识别被改坏、索引空转），
#        ⑥ 会整片翻转，而翻转为假绿时没人看得见。
#   ⑦ **账目自证**：各档"汇总条数"必须与"逐名点名的条数"相等，且四档相加 == 组表并集条数
#      （不相等就判红；本仓铁律「汇总数量必须与点名条数相等」，不许把红压成总数）。
#   ⑧ **清单逐名在盘**：`$scriptList` 里点名却不存在的脚本 ⇒ 红（取证域塌了不能让绿继续）。
#   ⑨ **mode 派生域**（2026-10-16 可见性普查）：全集**从代码派生**（`Program.cs` 里的 `case "<mode>":`
#      且其后 400 字符内真调用 `Probe*/Dump*(` ⇒ 实测 57 个），不再只信手抄的组表/`$Probes`；
#      「派生全集 − 已认领」必须逐名落进**豁免表**（只收零调用点的手工工具，每行写 类别｝理由｝复验日）。
#      判据：⑨-a 有调用点却没进账 = 0（**修法 = 并进组表，不是加进豁免表**） ｜ ⑨-b 门外 mode 全部登记
#      ｜ ⑨-c 豁免表无陈既行 ｜ ⑨-d 表行数 == 实际挨住的个数。
#   ⑩ **可达性域**：ServiceProbe 里「含断言、但任何派发 mode 都到不了」的方法必须为 0。
#      与第 76 条的分工：**⑤ 防「再抄一份」，⑩ 防「抄了但没人能跑到」**。
#      ⚠ 口径（2026-10-19 修）：扫描器**不跳过方法体**、且**表达式体定义（`=>`）也算方法** ⇒
#        实扫 106 块 / 7 条 `=>`（旧形状是"整行排除 =>"+"跳着走"，只取到 74 块 ⇒ 那一类整类隐形，
#        而 `static void W() => ProbeX();` 还是 BFS 的一条边，漏了它连"经包装转发的真探针"都会误判成孤儿）。
#        反控三条都在 ⑩-pre 里（合成件 8 项方向判据：表达式体死断言必须抓到、经 `=>` 包装可达必须放行）。
#   ⑪ **归属域**：`tests/scripts` 里不在 `$scriptList` 的脚本，必须「被在册脚本点名」或落进
#      一个**有理由的族 / 显式登记表**；⑪-d 只 WARN（有判据却不进轮的欠账），⑪-e 做账目守恒。
#   ⑨-pre / ⑩-pre / ⑪-pre **反控**：合成输入走**同一份**分类器，方向判据必须逐项符合。
#
# 【口径说明】③ 只要求"名字真实存在"，**不要求**有人跑它 —— "有没有人跑"是 ⑥ 的职责。
#   两者分开是有意的：拼错 mode 名（③ 抓）与"实现了却没人跑"（⑥ 抓）是两类不同的失效，
#   合成一条就会像 2026-10-10 审查那样，让守卫自己的注释替零调用点作证。
#   ⚠ 本次接线（2026-10-11）的实跑读数与逐名清单见 `docs/review-2026-10-11/l6-coverage-guard-note.md`：
#     当期零调用点 8 个（`bench`、`bitdepth`、`gamut`、`gainmap-encode`、`gainmap-decode`、`gainmap-struct`、
#     `pq`、`ultrahdr`）+ 只有非清单调用点 1 个（`icc`，唯一真调用点在 `ensure-fixtures.ps1:L97`）。
#   **2026-10-12（L-7）**：`bench` 已接进 `$Probes` ⇒ 零调用点剩 **7** 个（`bitdepth`、`gamut`、
#     `gainmap-encode`、`gainmap-decode`、`gainmap-struct`、`pq`、`ultrahdr`；实跑 `PASS=18 FAIL=2`，
#     档① 由 26 变 27）。本守卫**当时仍是红的** —— 那是它**要抓的东西**，不是误伤；放宽判据来消红
#     = 把 D1 §0 第 2 条重新埋回去。
#   ✅ **2026-10-15/16 全部接线 ⇒ 本守卫转绿，现值 `PASS=20 FAIL=0`**（上面那句"仍是红的"自此失效，
#     读数归属本轮：档① 27 / 档② 40 / 档③ 0 / 档④ 0；组表并集 67、`$Probes` 27、受管基线 76）。
#     七条各自的落点（由 ⑥ 的逐名点名打印，不是手写）：`bitdepth`←verify-format-regression:78、
#     `gamut`←verify-widegamut-regression:83、`pq`←verify-color-tonemap:94、`ultrahdr`←verify-gainmap-managed:385、
#     `gainmap-encode/decode/struct`←verify-gainmap-host §4b（拆分件由"生成物"身份改回**被真跑**）。
#     ⚠ 上面两条过期 cross-ref（`docs/TESTING.md` 的 `8/0`、运行器第 69 条复述里的 `$Probes` 26 个）
#       属**历史链**（当日为真），已按本仓规矩保留原文 + 就地加现值指针，不改写数字。
#   ✅ **2026-10-16 晚些时候（⑨⑪⑩ 落地）现值 `PASS=37 FAIL=0`**：上面那句 `20/0`、`档② 40`、`并集 67`
#     是被 ⑨-a 判红之前的旧账 —— ⑨-a 当场抓出 **3 个「每轮真在被执行却没进账」的 mode**
#     （`memadmit`←verify-gainmap-memory:234,341、`tonemapcurve`←verify-color-tonemap:36、
#     `color`←verify-gainmap-isobmff:484,485），三个都并进 `["ServiceProbe"]` 组
#     ⇒ 现 档① 27 / 档② 43 / 档③ 0 / 档④ 0，组表并集 70、门外 7 个（全部逐名登记在豁免表）。
#     ⚠ 本守卫第一版把 `color` 当成「零调用点的手工工具」写进了豁免表，**⑨-a 直接判红**把真相逼出来：
#     它的调用者在**在册**门禁里，且 stdout 喂给§3b 两条 CK ⇒ 按「有调用点就必须进账」并组、删豁免行。
#     同轮其余两域读数：⑩ 含断言的孤儿方法 = 0（方法块 74、闭包可达 76）｜
#     ⑪ 清单外 40 攫 = 被点名 7 + 族内 20 + 显式表 13 + 未归属 0（⑪-d WARN 8 攫，见价值普查文档）。
# ═══════════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))

$pass = 0; $fail = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "PASS $msg" -ForegroundColor Green; $script:pass++ }
    else     { Write-Host "FAIL $msg" -ForegroundColor Red;   $script:fail++ }
}

# ── AST 遍历小工具（谓词里只用 `$n`，不夹带外层变量：转成委托后取不到外层作用域）──
function Find-Assign($n)  { if (-not $n) { return @() }; return @($n.FindAll({ param($x) $x -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) }
function Find-Cmd($n)     { if (-not $n) { return @() }; return @($n.FindAll({ param($x) $x -is [System.Management.Automation.Language.CommandAst] }, $true)) }
function Find-Func($n)    { if (-not $n) { return @() }; return @($n.FindAll({ param($x) $x -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) }
function Find-Foreach($n) { if (-not $n) { return @() }; return @($n.FindAll({ param($x) $x -is [System.Management.Automation.Language.ForEachStatementAst] }, $true)) }
function Find-SLit($n)    { if (-not $n) { return @() }; return @($n.FindAll({ param($x) $x -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true)) }
function Find-XLit($n)    { if (-not $n) { return @() }; return @($n.FindAll({ param($x) $x -is [System.Management.Automation.Language.ExpandableStringExpressionAst] }, $true)) }
function Find-AnyLit($n)  { $a = @(Find-SLit $n); $b = @(Find-XLit $n); return @($a + $b) }
function Find-Var($n)    { if (-not $n) { return @() }; return @($n.FindAll({ param($x) $x -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) }

$T_VAR   = [System.Management.Automation.Language.VariableExpressionAst]
$T_PARAM = [System.Management.Automation.Language.CommandParameterAst]

function Get-PsAst([string]$path) {
    $tk = $null; $er = $null
    $a = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tk, [ref]$er)
    return @{ Ast = $a; Errors = @($er) }
}
function Get-LitText($node) {
    if ($node -is $T_PARAM) { return '' }
    if ($node -is [System.Management.Automation.Language.StringConstantExpressionAst]) { return $node.Value }
    return $node.Extent.Text          # 可展开串取原文：里面的 `$xxx` 由调用方按 varMap 再补一层
}
function Get-Fragments($node) {
    if (-not $node) { return '' }
    $parts = @()
    foreach ($s in (Find-AnyLit $node)) { $parts += (Get-LitText $s) }
    return ($parts -join ' ')
}
# 把表达式解析成"可能出现的宿主路径文本"：字面量原文 + 其中变量在本文件里的静态取值
function Resolve-NodeText($node, [hashtable]$vm) {
    $txt = Get-Fragments $node
    foreach ($v in (Find-Var $node)) {
        $k = $v.VariablePath.UserPath
        if ($vm.ContainsKey($k)) { $txt = $txt + ' ' + $vm[$k] }
    }
    return $txt
}
# 测试宿主判据：**按宿主二进制的形状**认，不按"路径里出现过 tests/"这种宽松组合认
#   （宽松版会被 `ffmpeg.exe` + `tests/output/…png` 两个无关片段拼出假宿主 ⇒ 假绿）
$script:HostRx = @(
    'ServiceProbe\.(exe|dll)',
    'GainMapTestHost\.(exe|dll)',
    'UiTestHost\.(exe|dll)',
    'Tests\.[A-Za-z0-9_.]+\.(exe|dll)'
)
function Test-HostFragment([string]$txt) {
    if ([string]::IsNullOrEmpty($txt)) { return $false }
    foreach ($rx in $script:HostRx) { if ($txt -match $rx) { return $true } }
    # 拆分工程（`Join-Path $root "tests/projects/$proj/bin/Release/…/$($modes[$proj].exe).exe"`）里
    # 宿主名是插值的，认不出 `Tests.X.exe` 字面 ⇒ 补一条**线性**形状判据。
    # ⚠ 刻意用 Contains 而不是 `tests/.*bin/.*\.exe` 这种正则：后者在长文本上是嵌套量词，
    #   实测会把本守卫拖进回溯灾难（一次实跑 35 s+ 起不来）。
    $low = $txt.ToLowerInvariant()
    if ($low.Contains('tests/') -or $low.Contains('tests\')) {
        if ($low.Contains('/bin/') -or $low.Contains('\bin\')) {
            if ($low.Contains('.exe') -or $low.Contains('.dll')) { return $true }
        }
    }
    return $false
}

# 把一段字面量列表按"整 token、大小写敏感"折成调用点条目（同一 文件:行:形态 只记一次）
function Get-EvidenceRows($nodeList, [string]$kind, [string]$forScript, [string[]]$modeList, [hashtable]$seen) {
    $out = @()
    if (-not $nodeList) { return $out }
    foreach ($lit in $nodeList) {
        $txt = Get-LitText $lit
        if ([string]::IsNullOrEmpty($txt)) { continue }
        foreach ($f in @($txt -split '[\s,;|]+' | ForEach-Object { $_.Trim('"', "'", '`', '(', ')', '@', '{', '}', '[', ']', ':') } | Where-Object { $_ })) {
            if ($modeList -cnotcontains $f) { continue }          # 整 token + 大小写敏感 ⇒ 子串不作证
            $key = $f + '|' + $forScript + '|L' + $lit.Extent.StartLineNumber + '|' + $kind
            if ($seen.ContainsKey($key)) { continue }
            $seen[$key] = $true
            $out += @{ Mode = $f; Script = $forScript; Line = $lit.Extent.StartLineNumber; Kind = $kind }
        }
    }
    return $out
}

# 预筛：整篇文本里是否出现**任一** mode 名的整 token 形态（只为省 AST 时间；
# 命中判据本身仍由 AST 决定，这里宽松一点不会漏证据）
function Test-AnyModeToken([string]$text, [string[]]$modeList) {
    if ([string]::IsNullOrEmpty($text)) { return $false }
    foreach ($m in $modeList) {
        if ($text -match ('(?i)(?<![\w\-])' + [regex]::Escape($m) + '(?![\w\-])')) { return $true }
    }
    return $false
}

$groupsCs   = 'tests/projects/Tests.Shared/TestGroups.cs'
$runnerPs1  = 'tests/scripts/_run-step3-gates.ps1'
$scriptsDir = 'tests/scripts'
$selfName   = Split-Path -Leaf $PSCommandPath

Write-Host "===== test-group-coverage =====" -ForegroundColor Cyan

# ── 前置（fail-closed：缺文件即红，不 SKIP）──────────────────────────────────
foreach ($p in @($groupsCs, $runnerPs1)) {
    if (-not (Test-Path $p)) { CK $false "前置：$p 存在（缺它无法判定覆盖）"; Write-Host "===== test-group-coverage: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan; exit 1 }
}

# ── 读组表：从 TestGroups.cs 解析 `["组名"] = new[] { "m1", "m2" },` ──────────
$gText = Get-Content $groupsCs -Raw
$groupMap = @{}
# ⚠ 组名字符集从 `[a-z0-9\-]+` 放宽到 `[A-Za-z0-9\-]+`（2026-10-14，镜像 5 工程删除那一批）：
#   那 31 个 mode 改由**目录本名就叫 `tests/projects/ServiceProbe`** 的组认领，而本门禁的契约是
#   "组名 = 工程目录名"（见 ② 的 `tests/<组名>`）。旧字符集静默漏掉带大写的键 ⇒
#   整组不可见，表现为 ① "20 个 mode 无组认领" + ④-c 并集塌到 35 —— 是**解析失配**不是覆盖丢失。
#   失配方向在这里是"转红"（不是假绿），但红得没有信息量，故修正则而不是改组名。
foreach ($m in [regex]::Matches($gText, '\["(?<g>[A-Za-z0-9\-]+)"\]\s*=\s*new\[\]\s*\{(?<modes>[^}]*)\}')) {
    $modes = [regex]::Matches($m.Groups['modes'].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    $groupMap[$m.Groups['g'].Value] = @($modes)
}

# ── 读运行器的权威清单（AST，不用正则去数数组：数组里夹了大量注释行，旧正则命中 0）─
$rinfo = Get-PsAst (Resolve-Path $runnerPs1).Path
$runnerAst = $rinfo.Ast
if (-not $runnerAst -or @($rinfo.Errors).Count -gt 0) {
    CK $false "前置：$runnerPs1 能被宿主解析（错误 $(@($rinfo.Errors).Count) 条）—— 解析不了就无法取证"
    Write-Host "===== test-group-coverage: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
    exit 1
}

$scriptListAll = @()
$expectedCount = 0
foreach ($a in (Find-Assign $runnerAst)) {
    if ($a.Left -isnot $T_VAR) { continue }
    $vn = $a.Left.VariablePath.UserPath
    if ($vn -eq 'scriptList') {
        foreach ($el in (Find-SLit $a.Right)) { if ($el.Value -like '*.ps1') { $scriptListAll += $el.Value } }
    }
    if ($vn -eq 'expectedScriptCount') {
        [void][int]::TryParse((($a.Right.Extent.Text) -replace '[^\d]', ''), [ref]$expectedCount)
    }
}
$scriptListAll = @($scriptListAll | Sort-Object -Unique)
# 证据源 = 受管基线去掉守卫自己（它不许给自己的判据作证）
$evidenceScripts = @($scriptListAll | Where-Object { $_ -ne $selfName })

$probes = @()
if ($runnerAst.ParamBlock) {
    foreach ($p in $runnerAst.ParamBlock.Parameters) {
        if ($p.Name.VariablePath.UserPath -eq 'Probes' -and $p.DefaultValue -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
            $probes = @(($p.DefaultValue.Value -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
    }
}

# ── ④ 反控：判据段必须真的读到东西（空枚举一律响亮判红 + 退出）──────────────
$union = @($groupMap.Values | ForEach-Object { $_ } | Sort-Object -Unique)
CK ($groupMap.Count -ge 9)  "④-a 反控：解析到 ≥9 个组（实 $($groupMap.Count)）—— 防组表被清空成恒绿"
CK ($probes.Count -ge 26)   "④-b 反控：解析到 ≥26 个运行器探针（实 $($probes.Count)）—— 防 `$Probes` 被清空成恒绿"
CK ($union.Count -ge 46)    "④-c 反控：组表 mode 并集 ≥46（实 $($union.Count)）"
CK ($scriptListAll.Count -ge 50) "④-d 反控：从运行器 AST 解析到 ≥50 条受管基线脚本（实 $($scriptListAll.Count)）—— 防清单解析失配成空表恒绿"
CK ($expectedCount -eq $scriptListAll.Count) "④-e 账目：运行器 `$expectedScriptCount = $expectedCount 与 AST 实解析 $($scriptListAll.Count) 条相等"
if ($groupMap.Count -lt 1 -or $probes.Count -lt 1 -or $scriptListAll.Count -lt 1) {
    CK $false "④ 枚举为空（组 $($groupMap.Count) / 探针 $($probes.Count) / 清单 $($scriptListAll.Count)）⇒ 拒绝判定，响亮判红"
    Write-Host "===== test-group-coverage: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
    exit 1
}

# ── ⑧ 受管清单逐名在盘（幽灵条目 ⇒ 红；这条是 ⑥ 取证域完整性的前提）─────────
$ghostScripts = @()
foreach ($s in $scriptListAll) { if (-not (Test-Path "$scriptsDir/$s")) { $ghostScripts += $s } }
$ghostLine = '无'
if (@($ghostScripts).Count) { $ghostLine = ($ghostScripts -join ', ') }
CK (@($ghostScripts).Count -eq 0) "⑧ `$scriptList` 每条都逐名在盘（幽灵条目：$ghostLine，点名 $(@($ghostScripts).Count) 条）"

# ── ⑥ 取证：扫 tests/scripts 全部 .ps1，按 AST 建"真实调用点"索引 ────────────
$managedSet = @{}
foreach ($s in $scriptListAll) { $managedSet[$s] = $true }

$callIdx = @{}          # mode -> list of @{Script;Line;Kind}
$rawText = @{}          # script -> 原文（给"该组宿主被谁起过"回读用）
$hostCmdTotal = 0       # 反控：全仓起宿主的命令条数
$scanErrors = @()

# ── 取证主体：给一段 AST + 文件名，返回该文件里的"真实调用点"条目 ──────────────
#   返回 @{ Rows=@( @{Mode;Script;Line;Kind} ); HostCmds=<起宿主的命令条数> }
#   抽成函数是为了让下方"有牙自证"能拿**合成源码**在内存里验这把尺子本身。
function Get-CallSites($ast, [string]$tag, [string[]]$modeList) {
    $rows = @(); $nHost = 0
    if (-not $ast) { return @{ Rows = $rows; HostCmds = 0 } }

    # (1) 变量 -> 字面量片段（一层静态展开就够：宿主路径都写成 $x = "$root/tests/…/N.exe"）
    $varMap = @{}
    foreach ($a in (Find-Assign $ast)) {
        if ($a.Left -isnot $T_VAR) { continue }
        $k = $a.Left.VariablePath.UserPath
        $f = Get-Fragments $a.Right
        if (-not $f) { continue }
        if ($varMap.ContainsKey($k)) { $varMap[$k] = $varMap[$k] + ' ' + $f } else { $varMap[$k] = $f }
    }

    # (2) 本地函数角色：launcher（体内起进程）/ 是否起的是**宿主**
    $fds = @(Find-Func $ast)
    $funcLaunch = @{}; $funcHost = @{}; $funcParams = @{}
    foreach ($fd in $fds) {
        $pn = @()
        foreach ($par in $fd.Parameters) { $pn += $par.Name.VariablePath.UserPath }
        if (-not $funcParams.ContainsKey($fd.Name)) { $funcParams[$fd.Name] = $pn }
    }
    for ($round = 0; $round -lt 3; $round++) {
        foreach ($fd in $fds) {
            $isLaunch = $false; $launchHost = $false
            foreach ($c in (Find-Cmd $fd.Body)) {
                $cn = $c.GetCommandName()
                if (-not $cn) { $isLaunch = $true; continue }              # & $exe / . $x ⇒ 起外部命令
                $leaf = Split-Path $cn -Leaf
                if ($cn -match '^(Start-Process|Start-Job|Invoke-Expression|Invoke-Item)$') {
                    $isLaunch = $true
                    $els = @($c.CommandElements)
                    for ($i = 0; $i -lt $els.Count; $i++) {
                        if ($els[$i] -is $T_PARAM -and $els[$i].ParameterName -eq 'FilePath' -and ($i + 1) -lt $els.Count) {
                            $vv = $els[$i + 1]
                            $fp = Resolve-NodeText $vv $varMap
                            $pname = ''
                            if ($vv -is $T_VAR) { $pname = $vv.VariablePath.UserPath }
                            if ((Test-HostFragment $fp) -and -not (@($funcParams[$fd.Name]) -ccontains $pname)) { $launchHost = $true }
                        }
                    }
                }
                if ($funcLaunch.ContainsKey($leaf) -and $funcLaunch[$leaf]) {
                    $isLaunch = $true
                    if ($funcHost.ContainsKey($leaf) -and $funcHost[$leaf]) { $launchHost = $true }
                }
            }
            $funcLaunch[$fd.Name] = $isLaunch
            $funcHost[$fd.Name]   = $launchHost
        }
    }

    # (3) 找出"起测试宿主"的那条命令（宿主可来自闭包变量，也可来自调用点实参）
    $hostCmds = @()
    foreach ($c in (Find-Cmd $ast)) {
        $cn = $c.GetCommandName()
        $isHost = $false
        $els = @($c.CommandElements)
        if (-not $cn) {
            $isHost = Test-HostFragment (Resolve-NodeText $c $varMap)
        } elseif ($cn -eq 'Start-Process') {
            # `-FilePath` 是宿主 ⇒ 起宿主；`-FilePath dotnet/pwsh` + `-ArgumentList` 里带宿主
            # dll/exe（`ensure-fixtures.ps1` 的 `dotnet "ServiceProbe.dll" icc …` 形态）同样算。
            if (Test-HostFragment (Resolve-NodeText $c $varMap)) { $isHost = $true }
        } else {
            $leaf = Split-Path $cn -Leaf
            if ($funcLaunch.ContainsKey($leaf) -and $funcLaunch[$leaf]) {
                if ($funcHost.ContainsKey($leaf) -and $funcHost[$leaf]) { $isHost = $true }
                else {
                    for ($j = 1; $j -lt $els.Count; $j++) {                 # 跳过命令名本身
                        if ($els[$j] -is $T_PARAM) { continue }
                        if (Test-HostFragment (Resolve-NodeText $els[$j] $varMap)) { $isHost = $true; break }
                    }
                }
            }
        }
        if ($isHost) { $hostCmds += $c }
    }
    $nHost = @($hostCmds).Count

    # (4) 数据表成链：表变量（或从表取值的 foreach 循环变量）出现在某条起宿主命令的文本里
    $hostText = ''
    foreach ($c in $hostCmds) { $hostText = $hostText + ' ' + $c.Extent.Text }
    $tainted = @{}
    foreach ($a in (Find-Assign $ast)) {
        if ($a.Left -isnot $T_VAR) { continue }
        $vn = $a.Left.VariablePath.UserPath
        if ($hostText -match ('\$' + [regex]::Escape($vn) + '\b')) { $tainted[$vn] = $true }
    }
    foreach ($fe in (Find-Foreach $ast)) {
        $lv = $fe.Variable.VariablePath.UserPath
        if (-not ($hostText -match ('\$' + [regex]::Escape($lv) + '\b'))) { continue }
        # `foreach ($mode in $modes[$proj].modes)` ⇒ 把集合表达式里的根变量算进同一条链。
        # ⚠ PS7 的 ForEachStatementAst **没有** `Collection` 属性（实测属性名是 `Condition`），
        #   取不到就退回用 Extent 偏移区间截正文 —— 别拿属性名当保证。
        $collTxt = ''
        if ($fe.Condition) { $collTxt = $fe.Condition.Extent.Text }
        if ([string]::IsNullOrEmpty($collTxt)) {
            $from = $fe.Variable.Extent.EndOffset - $fe.Extent.StartOffset
            $len  = $fe.Body.Extent.StartOffset - $fe.Variable.Extent.EndOffset
            if ($len -gt 0 -and ($from + $len) -le $fe.Extent.Text.Length) {
                $collTxt = $fe.Extent.Text.Substring($from, $len)
            }
        }
        foreach ($mm in [regex]::Matches($collTxt, '\$\{?([A-Za-z_][A-Za-z0-9_]*)')) {
            $tainted[$mm.Groups[1].Value] = $true
        }
    }

    # (5) 落证据：**只遍历"起宿主命令的参数子树"与"已成链的数据表子树"**里的字面量。
    #     注释在 AST 里不是字符串节点、报告/文案命令不在子树内 ⇒ "散文骗到判据"结构性进不来。
    $localSeen = @{}
    foreach ($c in $hostCmds) { $rows += @(Get-EvidenceRows (Find-AnyLit $c) 'argv' $tag $modeList $localSeen) }
    foreach ($a in (Find-Assign $ast)) {
        if ($a.Left -isnot $T_VAR) { continue }
        if (-not $tainted.ContainsKey($a.Left.VariablePath.UserPath)) { continue }
        $rows += @(Get-EvidenceRows (Find-AnyLit $a.Right) 'table' $tag $modeList $localSeen)
    }
    return @{ Rows = $rows; HostCmds = $nHost }
}

$allPs = @(Get-ChildItem $scriptsDir -Filter '*.ps1' -File | Sort-Object Name)
foreach ($fi in $allPs) {
    $tag = $fi.Name
    if ($tag -eq $selfName) { continue }                 # ⚠ 守卫自己不作为证据源（D1 §3-C 第 2 条）
    $rawText[$tag] = (Get-Content $fi.FullName -Raw)
    # 快速预筛：整篇里连一个 mode 名的整 token 都没有 ⇒ 不必做 AST（多数脚本如此，省 30 s+）
    if (-not (Test-AnyModeToken $rawText[$tag] $union)) { continue }
    $info = Get-PsAst $fi.FullName
    $ast = $info.Ast
    if (-not $ast) { if ($managedSet.ContainsKey($tag)) { $scanErrors += "$tag(取不到 AST)" }; continue }
    if (@($info.Errors).Count -gt 0) {
        if ($managedSet.ContainsKey($tag)) { $scanErrors += "$tag($(@($info.Errors).Count) 处解析错误)" }
        else { $scanErrors += "（非清单）$tag" }
        continue
    }
    $got = Get-CallSites $ast $tag $union
    $hostCmdTotal += $got.HostCmds
    foreach ($r in $got.Rows) {
        if (-not $callIdx.ContainsKey($r.Mode)) { $callIdx[$r.Mode] = @() }
        $callIdx[$r.Mode] += @{ Script = $r.Script; Line = $r.Line; Kind = $r.Kind }
    }
}


# ── ⑥-pre 有牙自证：拿**合成源码**在内存里验这把尺子本身 ────────────────────
#   为什么必须自带这一条（本仓铁律"枚举为空必须拦"的推广）：⑥ 的判据是"参数位置上的整 token"，
#   一旦 AST 取证哪天失配（宿主正则被改松、launcher 识别被改坏、`Get-CallSites` 返回空表），
#   ⑥ 会**安静地**把所有 mode 判成零调用点（假红一片，随后被人放宽），或反过来全判有（假绿）。
#   ⇒ 每条判据都配**正例 + 负例**，正例必须命中、负例必须不命中，任何一边反了就转红。
$teethCases = @(
    @{
        Name = 'argv 是**内联数组**且宿主走 dotnet 间接（ensure-fixtures 的真实形态）'
        ExpectHit  = @('color')
        ExpectMiss = @('pq')
        Src = @'
$prb = "$root/tests/projects/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
$fx = "$root/tests/fixtures"
Write-Host "顺带提一句 pq，这段只是文案"
$null = Start-Process -FilePath "dotnet" -ArgumentList @("`"$prb`"", 'color', "$fx/a.png", 'png', $prb) `
        -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$val/_iccc.txt"
'@
    }
    @{
        Name = 'argv 是内联数组但 -FilePath 直接就是宿主'
        ExpectHit  = @('gamutfit')
        ExpectMiss = @('icc-dump')
        Src = @'
$prb = "$root/tests/projects/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
$names = @('icc-dump')
Write-Output "孤立表里的 icc-dump 不作证"
Start-Process -FilePath $prb -ArgumentList @('gamutfit', 'out') -Wait -NoNewWindow | Out-Null
'@
    }
    @{
        Name = 'argv 位（含 D1 §3-C 点名的"守卫自己的注释"形态）'
        ExpectHit  = @('bitdepth')                    # 真被执行：Exec $probe "bitdepth …"
        ExpectMiss = @('bench', 'gainmap-struct')     # 只出现在注释/文案里 ⇒ 不许作证
        Src = @'
# 专项门禁脚本直接调用的（如 bitdepth/bench/gainmap-struct），它们不在 $Probes 默认清单里，但确实有人跑
function Exec([string]$file, [string]$argStr) {
    Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow | Out-Null
}
Write-Host "本轮跑 bitdepth 与 bench，别担心"
Write-Output "gainmap-struct 由专项门禁调用，正常"
$note = 'bench 已覆盖'
$probe = "$root/tests/projects/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
Exec $probe "bitdepth in out"
'@
    }
    @{
        Name = '数据表成链（分发表喂给宿主才算，孤立表不算）'
        ExpectHit  = @('ui-format-command', 'ui-panel-visibility')
        ExpectMiss = @('ui-sweep')
        Src = @'
function Invoke-Exe([string]$exePath, [string[]]$exeArgs) {
    Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile', '-Command', $exePath) -Wait | Out-Null
}
$modes = @{ exe = 'Tests.UiCore'; list = @('ui-format-command', 'ui-panel-visibility') }
$exe = Join-Path $root "tests/projects/ui-core/bin/Release/net11.0/win-x64/$($modes.exe).exe"
foreach ($mm in $modes.list) { Invoke-Exe $exe @($mm) }
$orphan = @{ dead = @('ui-sweep') }
'@
    }
)
$teethBad = @()
$teethChecked = 0
foreach ($tc in $teethCases) {
    $tk2 = $null; $er2 = $null
    $sAst = [System.Management.Automation.Language.Parser]::ParseInput($tc.Src, [ref]$tk2, [ref]$er2)
    if (-not $sAst -or @($er2).Count -gt 0) { $teethBad += "$($tc.Name)：合成件解析不过($(@($er2).Count) 错)"; continue }
    $ml = @(@($tc.ExpectHit) + @($tc.ExpectMiss))
    $sGot = Get-CallSites $sAst 'SYNTH' $ml
    $sIdx = @{}
    foreach ($r in @($sGot.Rows)) { if (-not $sIdx.ContainsKey($r.Mode)) { $sIdx[$r.Mode] = @() }; $sIdx[$r.Mode] += ($r.Kind + ':L' + $r.Line) }
    foreach ($h in @($tc.ExpectHit)) {
        $teethChecked++
        if (-not $sIdx.ContainsKey($h)) { $teethBad += "$($tc.Name)：正例 $h 没命中（尺子空转）" }
    }
    foreach ($x in @($tc.ExpectMiss)) {
        $teethChecked++
        if ($sIdx.ContainsKey($x)) { $teethBad += "$($tc.Name)：负例 $x 竟被作证（实为 $(@($sIdx[$x]) -join ',')）" }
    }
}
if (@($teethCases).Count -lt 1) { $teethBad += "自证用例表为空（判据尺子没被验过）" }
$teethLine = '全部符合（正例命中 / 注释与文案不作证）'
if (@($teethBad).Count) { $teethLine = (@($teethBad) -join ' ｜ ') }
CK (@($teethBad).Count -eq 0) "⑥-pre 有牙自证：合成件 $(@($teethCases).Count) 组 / $teethChecked 项判据方向都对（$teethLine）"

# ── ④-f/g/h 反控：取证管道自己必须出活 ────────────────────────────────────
CK ($hostCmdTotal -ge 40) "④-f 反控：扫到 ≥40 条起测试宿主的命令（实 $hostCmdTotal）—— AST 取证失配/被绕过时拦死"
CK ($callIdx.Count -ge 26) "④-g 反控：调用点索引覆盖 ≥26 个 mode（实 $($callIdx.Count)）—— 防整表空转成恒绿"
$scanErrLine = '无'
if (@($scanErrors).Count) { $scanErrLine = (@($scanErrors) -join ', ') }
CK (@($scanErrors).Count -eq 0) "④-h 取证域完整：tests/scripts 下 .ps1 全部解析通过（失败：$scanErrLine，点名 $(@($scanErrors).Count) 个）"

function Get-Evidence([string]$mode) {
    $mg = @(); $um = @()
    if ($callIdx.ContainsKey($mode)) {
        foreach ($h in $callIdx[$mode]) {
            $label = $h.Script + ':L' + $h.Line + '(' + $h.Kind + ')'
            if ($managedSet.ContainsKey($h.Script)) { $mg += $label } else { $um += $label }
        }
    }
    $mg = @($mg | Select-Object -Unique)
    $um = @($um | Select-Object -Unique)
    if ($probes -ccontains $mode) { return @{ Tier = 'probes'; Managed = $mg; Unmanaged = $um } }
    if (@($mg).Count)   { return @{ Tier = 'managed';   Managed = $mg; Unmanaged = $um } }
    if (@($um).Count)   { return @{ Tier = 'unmanaged'; Managed = $mg; Unmanaged = $um } }
    return @{ Tier = 'none'; Managed = $mg; Unmanaged = $um }
}

$spProgram = ''
if (Test-Path 'tests/projects/ServiceProbe/Program.cs') { $spProgram = Get-Content 'tests/projects/ServiceProbe/Program.cs' -Raw }
function Get-GroupOf([string]$mode) {
    foreach ($g in $groupMap.Keys) { if ($groupMap[$g] -ccontains $mode) { return $g } }
    return '?'
}
function Get-GroupHost([string]$gname) {
    $cj = @(Get-ChildItem "tests/projects/$gname" -Filter '*.csproj' -File -EA SilentlyContinue)
    foreach ($c in $cj) {
        $mm = [regex]::Match((Get-Content $c.FullName -Raw), '<AssemblyName>([^<]+)</AssemblyName>')
        if ($mm.Success) { return ($mm.Groups[1].Value + '.exe') }
    }
    return "tests/projects/$gname 的宿主 exe（未取到 AssemblyName）"
}
function Get-OwnerHint([string]$mode) {
    $g = Get-GroupOf $mode
    $exeName = Get-GroupHost $g
    $inSp = $false
    if ($spProgram -match ('case "' + [regex]::Escape($mode) + '"')) { $inSp = $true }
    $touched = @()
    foreach ($s in $evidenceScripts) {
        if (-not $rawText.ContainsKey($s)) { continue }
        if ($rawText[$s] -match ('tests/projects/' + [regex]::Escape($g) + '[/\\"'' .]')) { $touched += $s }
    }
    $parts = @()
    if ($inSp) { $parts += 'ServiceProbe.exe 分派了该 mode ⇒ 本该进运行器 `$Probes`，或由 `$scriptList` 内某条起 ServiceProbe 的清单脚本带上（形态参照 verify-color-peak.ps1 的 RunTool 参数表）' }
    $parts += ('该组宿主 = ' + $exeName)
    if (@($touched).Count) { $parts += ('`$scriptList` 内引用过该组目录的脚本 = ' + (($touched | Select-Object -First 4) -join ', ')) }
    else { $parts += ('`$scriptList` 内没有任何脚本引用过 tests/projects/' + $g) }
    return ($parts -join ' ；')
}

# ── ① 运行器的每个 mode 都必须被某组认领 ───────────────────────────────────
$unowned = @($probes | Where-Object { $_ -cnotin $union })
$unownedLine = '无'
if (@($unowned).Count) { $unownedLine = ($unowned -join ', ') }
CK (@($unowned).Count -eq 0) "① 运行器 `$Probes` 的每个 mode 都有组认领（未认领：$unownedLine，点名 $(@($unowned).Count) 条）"

# ── ② 组表里的每个组都必须有对应工程目录 ───────────────────────────────────
$missingProj = @()
foreach ($g in $groupMap.Keys) {
    # 工程目录名 = tests/<组名>（与 TestGroups 的键同名，见 docs/TEST_SPLIT_TEMPLATE.md）
    if (-not (Test-Path "tests/projects/$g")) { $missingProj += $g }
}
$missingLine = '无'
if (@($missingProj).Count) { $missingLine = ($missingProj -join ', ') }
CK (@($missingProj).Count -eq 0) "② 组表里每个组都有工程目录（缺目录：$missingLine，点名 $(@($missingProj).Count) 条）"

# ── ③ 组表里每个 mode 名都必须**真实存在**（防拼错；"有没有人跑"归 ⑥）────────
$groupProgram = @{}
foreach ($g in $groupMap.Keys) {
    $pf = "tests/projects/$g/Program.cs"
    if (Test-Path $pf) { $groupProgram[$g] = Get-Content $pf -Raw }
}
$unregistered = @()
foreach ($kv in $groupMap.GetEnumerator()) {
    foreach ($mode in $kv.Value) {
        if ($probes -ccontains $mode) { continue }                              # (a) 在 `$Probes`
        $ev = Get-Evidence $mode
        if ($ev.Tier -eq 'managed' -or $ev.Tier -eq 'unmanaged') { continue }   # (b) 有真实调用点
        if ($groupProgram.ContainsKey($kv.Key) -and
            $groupProgram[$kv.Key] -match ('"' + [regex]::Escape($mode) + '"')) { continue }  # (c)
        $unregistered += "$($kv.Key)/$mode"
    }
}
$unregLine = '无'
if (@($unregistered).Count) { $unregLine = ($unregistered -join ', ') }
CK (@($unregistered).Count -eq 0) "③ 组表每个 mode 名都真实存在（a/b/c 全不命中：$unregLine，点名 $(@($unregistered).Count) 条）"

# ── ⑤ 声明的 mode 必须**真的被分派**（堵"声明了却没实现"）───────────────────
$notDispatched = @()
$dispatchParsed = 0
foreach ($kv in $groupMap.GetEnumerator()) {
    if (-not $groupProgram.ContainsKey($kv.Key)) { continue }
    $prog = $groupProgram[$kv.Key]
    foreach ($mode in $kv.Value) {
        # 真实分派形态有两种，都必须认：
        #   (i)  拆分工程的 `mode == "xxx"`；
        #   (ii) ServiceProbe 的 `case "xxx":`（switch 分派）—— 2026-10-14 那 31 个 mode 改由
        #        `["ServiceProbe"]` 认领后，本条按旧单一形态把它们全判成"声明了却没实现"。
        #        上面 Get-OwnerHint 早就认 `case "mode"`（:508），两处口径此前不一致。
        if ($prog -match ('mode\s*==\s*"' + [regex]::Escape($mode) + '"') -or
            $prog -match ('(?m)^[^\S\r\n]*case\s+"' + [regex]::Escape($mode) + '"\s*:')) { $dispatchParsed++; continue }
        $notDispatched += "$($kv.Key)/$mode"
    }
}
CK ($dispatchParsed -ge 40) "⑤-a 反控：解析到 ≥40 个 `mode ==` 分派（实 $dispatchParsed）—— 防正则失配导致恒绿"
$ndLine = '无'
if (@($notDispatched).Count) { $ndLine = ($notDispatched -join ', ') }
CK (@($notDispatched).Count -eq 0) "⑤ 组表每个 mode 都**真的被分派**（声明了却没实现：$ndLine，点名 $(@($notDispatched).Count) 条）"

# ── ⑥ ★ 真实调用关系：每个声明的 mode 必须**有人跑**（逐名点名）─────────────
$tierProbes = @(); $tierManaged = @(); $tierUnmanaged = @(); $tierNone = @()
foreach ($m in $union) {
    $ev = Get-Evidence $m
    if ($ev.Tier -eq 'probes')      { $tierProbes   += $m }
    elseif ($ev.Tier -eq 'managed') { $tierManaged  += $m }
    elseif ($ev.Tier -eq 'unmanaged') { $tierUnmanaged += $m }
    else                            { $tierNone     += $m }
}

Write-Host ""
Write-Host '### ⑥ 调用点账（判据 = 运行器 `$scriptList` + `$Probes` 的参数位置；注释/文案/子串/非清单脚本/守卫自己都不作证）###' -ForegroundColor Cyan

# 第二档：有调用点，但调用者不在受管基线里 ⇒ 不算已覆盖（读者会误以为已接）
$umNamed = 0
foreach ($m in $tierUnmanaged) {
    $ev = Get-Evidence $m
    Write-Host ("WARN ⑥-a 仅非清单脚本调用：{0}（组 {1}）—— 调用点 {2} 不在 `$scriptList` 内 ⇒ 不担保跑到" -f $m, (Get-GroupOf $m), (($ev.Unmanaged | Select-Object -First 3) -join ' ')) -ForegroundColor Yellow
    Write-Host ("      本该被谁调用：{0}" -f (Get-OwnerHint $m)) -ForegroundColor Yellow
    $umNamed++
}
if ($umNamed -ne @($tierUnmanaged).Count) {
    CK $false "⑥-a 账目：第二档汇总 $(@($tierUnmanaged).Count) 条 != 逐名点名 $umNamed 条"
} else {
    CK ($umNamed -eq @($tierUnmanaged).Count) "⑥-a 账目：第二档（仅非清单调用）汇总条数 == 逐名点名条数（$umNamed）"
}
# 判据本体：调用者不在受管基线内 ⇒ 本轮**不担保**跑到 ⇒ 也拦红（与零调用点区分措辞）
$umListLine = '无'
if (@($tierUnmanaged).Count) { $umListLine = ($tierUnmanaged -join ', ') }
CK (@($tierUnmanaged).Count -eq 0) "⑥-d 每个已覆盖 mode 的调用者都在 `$scriptList` 内（只有非清单调用点：$(@($tierUnmanaged).Count) 个，已逐名点名 $umNamed 条 ⇒ $umListLine）"

# 第三档：零调用点 ⇒ 响亮判红，逐名点名 + 该被谁调用
$zeroNamed = 0
foreach ($m in $tierNone) {
    Write-Host ("FAIL ⑥-b 零调用点：{0}（组 {1}）—— 既不在 `$Probes`，也不在 `$scriptList` 任一条的参数位置上" -f $m, (Get-GroupOf $m)) -ForegroundColor Red
    Write-Host ("      本该被谁调用：{0}" -f (Get-OwnerHint $m)) -ForegroundColor Red
    $zeroNamed++
}
if (@($tierNone).Count -ne $zeroNamed) {
    CK $false "⑥-b 账目：零调用点汇总 $(@($tierNone).Count) 条 != 逐名点名 $zeroNamed 条"
} else {
    CK (@($tierNone).Count -eq 0) "⑥-c 每个声明的 mode 都有**真实调用点**（零调用点 $(@($tierNone).Count) 个，已逐名点名 $zeroNamed 条）"
}

# ── ⑦ 账目自证：四档相加 == 并集，且各档点名条数 == 汇总条数 ────────────────
$sumTiers = @($tierProbes).Count + @($tierManaged).Count + @($tierUnmanaged).Count + @($tierNone).Count
$namedAll = @($tierProbes).Count + @($tierManaged).Count + $umNamed + $zeroNamed
CK ($sumTiers -eq $union.Count) "⑦-a 账目：四档汇总 $sumTiers 条 == 组表并集 $($union.Count) 条（不等即有点名被吞或重复计数）"
CK ($namedAll -eq $sumTiers) "⑦-b 账目：逐名点名 $namedAll 条 == 汇总 $sumTiers 条"

# ══════════════════════════════════════════════════════════════════════════
# ⑨⑩⑪ 可见性账（2026-10-16 价值普查的后续；账见 `docs/review-2026-10-16/test-value-audit.md` 第二轮）
#
# 【为什么要这三节】①–⑧ 的「全集」全是**手抄的**（组表、`$Probes`、`$scriptList`）⇒
#   「差集为空」这类判据在**全集自己缩水**时恒绿。实测代价：`memadmit` 与 `tonemapcurve`
#   **每轮真在被在册门禁执行**（`verify-gainmap-memory.ps1:234,341`、`verify-color-tonemap.ps1:36`），
#   却因为没写进组表而四档全不进，而 ⑦-a「四档相加 == 组表并集」照样成立
#   ⇒ 两份手抄互相对表，谁都不对代码。三条共同形状：**派生全集 − 已认领 = 显式豁免表**，
#   且豁免表自己也要被看着（新增未登记 ⇒ 红；已归位还留着 ⇒ 也红）。
#
# 【F 条：这三把尺自己也要正控/反控】我这一轮连着三次栽在尺子上，都记在这儿，别再犯第四次：
#   ① 把 `tonemapcurve` 判成死分支（grep 只搜独立引号形 `"x"`，没搜「引号内开头 + 空格 + 参数」）；
#   ② 把变量拼出来的 exe 路径判成「未被执行」（正解：以整轮日志里该宿主的汇总行为准）；
#   ③ ⑩ 的第一版把全集定义成「任何 `case "x":` 标签」⇒ CliParser 的 `png/bt2020/srgb` 混进来，
#      **尺一变宽反而把 42 个有调用点的 mode 读成零调用点**。⇒ 派发 mode 的定义与 #76 对齐：
#      `case "x":` 之后 400 字符内出现 `Probe*/Dump*(` 才算数（实测 57 个）。
#   ⇒ 下面每节都配内存合成件反控（⑨-pre/⑩-pre/⑪-pre）与「确知存在」的正控，不落盘、不起进程。
# ══════════════════════════════════════════════════════════════════════════

# 共用分类器：真实账与合成件反控走同一份逻辑（否则反控测的不是上线那把尺）
function Split-OffBook([string[]]$universe, [string[]]$claimed, [hashtable]$callIdx, [hashtable]$exempt) {
    $mustClaim = @(); $unregistered = @(); $stale = @(); $okExempt = @()
    $cl = @($claimed | Sort-Object -Unique)
    foreach ($it in $universe) {
        if ($cl -contains $it) { continue }
        $ev = @()
        if ($callIdx.ContainsKey($it)) { $ev = @($callIdx[$it]) }
        if ($ev.Count -gt 0) { $mustClaim += $it; continue }
        if ($exempt.ContainsKey($it)) { $okExempt += $it } else { $unregistered += $it }
    }
    foreach ($ky in $exempt.Keys) { if ($universe -notcontains $ky) { $stale += $ky } }
    return @{ MustClaim = $mustClaim; Unregistered = $unregistered; Stale = $stale; OkExempt = $okExempt }
}

# ── ⑨ mode 派生域：全集 = ServiceProbe 的 case 派发表（不是手抄组表）─────────
$repoRoot = (Get-Location).Path
$spPath = Join-Path $repoRoot 'tests/projects/ServiceProbe/Program.cs'
$spText = ''
if (Test-Path $spPath) { $spText = [System.IO.File]::ReadAllText($spPath) }
$dispatchAll = @()
foreach ($mmd in [regex]::Matches($spText, '(?m)^[ \t]*case\s+"(?<m>[a-z0-9\-]+)"\s*:')) {
    $dfrom = $mmd.Index + $mmd.Length
    if ($dfrom + 400 -ge $spText.Length) { continue }
    if ($spText.Substring($dfrom, 400) -notmatch '\b(Probe|Dump)[A-Za-z0-9_]+[ \t]*\(') { continue }
    $dv = $mmd.Groups['m'].Value
    if ($dispatchAll -notcontains $dv) { $dispatchAll += $dv }
}
CK ($dispatchAll.Count -ge 50) ('⑨ 前置：从 case 表派生出派发 mode ' + $dispatchAll.Count + ' 个（≥50 ⇒ 全集取自代码，不取自手抄表）')
$posMiss = @()
foreach ($pc in @('contract', 'vendor-hdr', 'settings', 'thumbnail')) {
    if ($dispatchAll -notcontains $pc) { $posMiss += $pc }
}
$pmLine = '无'
if (@($posMiss).Count) { $pmLine = ($posMiss -join ', ') }
CK (@($posMiss).Count -eq 0) ('⑨ 正控：四个确知存在的派发 mode 必须都被全集取到（缺：' + $pmLine + '）—— 全集漏取会让下面的差集判据整体失真')

$outside = @($dispatchAll | Where-Object { ($union + $probes) -notcontains $_ } | Sort-Object)
$outIdx = @{}
foreach ($fi2 in $allPs) {
    $tag2 = $fi2.Name
    if ($tag2 -eq $selfName) { continue }
    $txt2 = ''
    if ($rawText.ContainsKey($tag2)) { $txt2 = $rawText[$tag2] } else { $txt2 = (Get-Content $fi2.FullName -Raw) }
    if (@($outside).Count -eq 0) { break }
    if (-not (Test-AnyModeToken $txt2 $outside)) { continue }
    $info2 = Get-PsAst $fi2.FullName
    if (-not $info2.Ast -or @($info2.Errors).Count -gt 0) { continue }
    foreach ($r2 in (Get-CallSites $info2.Ast $tag2 $outside).Rows) {
        if (-not $outIdx.ContainsKey($r2.Mode)) { $outIdx[$r2.Mode] = @() }
        $outIdx[$r2.Mode] += @{ Script = $r2.Script; Line = $r2.Line; Kind = $r2.Kind; Managed = [bool]$managedSet.ContainsKey($r2.Script) }
    }
}
# 豁免表：只有「没有任何调用点的手工工具」才有资格进来；每行必须写 类别 ｜ 理由 ｜ 复验日
$modeExempt = [ordered]@{
    'ipc-server'     = '驻留端诊断工具 ｜ 往返用例进门禁会挂死整条门禁（docs/TESTING.md:1115 明文保留） ｜ 复验 2026-10-16'
    'ipc-client'     = '跨进程客户端诊断 ｜ 与 ipc-server 同一保留理由 ｜ 复验 2026-10-16'
    'ipc-raw-client' = '跨进程客户端诊断（环境对照） ｜ 同上 ｜ 复验 2026-10-16'
    'ipc-submit'     = '手工提交工具（向驻留端投任务） ｜ 无自动调用者 ｜ 复验 2026-10-16'
    'piperaw-server' = '环境对照（自包含裸管道） ｜ 明文保留为诊断工具 ｜ 复验 2026-10-16'
    'piperaw-client' = '环境对照（自包含裸管道） ｜ 明文保留为诊断工具 ｜ 复验 2026-10-16'
    'tonemap'        = '手工对照工具 ｜ 宽形 grep 的 28 处命中逐行核过全是 ffmpeg 的 tonemap= 滤镜名与门禁文件名，不是 mode 调用点 ｜ 复验 2026-10-16'
}
$split9 = Split-OffBook $dispatchAll (@($union + $probes)) $outIdx $modeExempt
$mcLine = '无'
if (@($split9.MustClaim).Count) {
    $mcArr = @()
    foreach ($mk in $split9.MustClaim) {
        $firstEv = @($outIdx[$mk]) | Where-Object { $_.Managed } | Select-Object -First 1
        $mcArr += ($mk + '<-' + $firstEv.Script + ':L' + $firstEv.Line)
    }
    $mcLine = ($mcArr -join ', ')
}
CK (@($split9.MustClaim).Count -eq 0) ('⑨-a 每轮真在被执行却没进账的 mode = 0（实 ' + @($split9.MustClaim).Count + '：' + $mcLine + '）—— 修法是把名字并进 TestGroups 对应组，不是加进豁免表')
$urLine = '无'
if (@($split9.Unregistered).Count) { $urLine = ($split9.Unregistered -join ', ') }
CK (@($split9.Unregistered).Count -eq 0) ('⑨-b 门外 mode 全部逐名登记（未登记 ' + @($split9.Unregistered).Count + ' 个：' + $urLine + '）—— 新增一个既不进账也不登记的 mode 在这里红')
$stLine = '无'
if (@($split9.Stale).Count) { $stLine = ($split9.Stale -join ', ') }
CK (@($split9.Stale).Count -eq 0) ('⑨-c 豁免表里没有已消失/已归位的陈旧行（陈旧 ' + @($split9.Stale).Count + ' 条：' + $stLine + '）—— 防豁免表变成第二份没人读的登记表')
CK (@($split9.OkExempt).Count -eq $modeExempt.Count) ('⑨-d 账目：豁免表 ' + $modeExempt.Count + ' 行 == 实际按住的 ' + @($split9.OkExempt).Count + ' 个（不等即有点名被吞或表多行）')
$outLine = '无'
if (@($outside).Count) { $outLine = ($outside -join ', ') }
Write-Host ('ℹ      ⑨ 派生全集 ' + $dispatchAll.Count + ' ｜ 已认领（$Probes ∪ 组表）' + @(@($union + $probes) | Sort-Object -Unique).Count + ' ｜ 门外 ' + @($outside).Count + ' 个 = ' + $outLine)

# ── ⑨-pre 反控：合成输入验 Split-OffBook 的四个方向 ─────────────────────────
$synA = Split-OffBook @('m_has', 'm_exempt') @('claimed') @{ m_has = @(@{ Script = 'verify-x.ps1'; Line = 7; Managed = $true }) } @{ m_exempt = 'r' }
$synB = Split-OffBook @('m_plain') @('claimed') @{} @{ m_gone = 'r' }
$teeth9 = @()
if (@($synA.MustClaim).Count -ne 1 -or $synA.MustClaim[0] -ne 'm_has') { $teeth9 += 'A 有调用点的门外 mode 没被判成必须进账' }
if (@($synA.Unregistered).Count -ne 0) { $teeth9 += 'A 已登记豁免行被判成未登记' }
if (@($synB.MustClaim).Count -ne 0) { $teeth9 += 'B 无调用点的 mode 被误判成必须进账' }
if (@($synB.Stale).Count -ne 1 -or $synB.Stale[0] -ne 'm_gone') { $teeth9 += 'B 陈旧豁免行没被抓住' }
if (@($synB.Unregistered).Count -ne 1) { $teeth9 += 'B 未登记项没被判红' }
$t9Line = '全部符合（有调用点⇒必进账 / 已登记⇒放行 / 未登记⇒红 / 表多行⇒红）'
if ($teeth9.Count) { $t9Line = ($teeth9 -join ' | ') }
CK ($teeth9.Count -eq 0) ('⑨-pre 有牙自证：合成件 2 组 5 项方向判据（' + $t9Line + '）')

# ── ⑩ 可达性域：活在执行文件里、任何派发 mode 都到不了的断言 ────────────────
#   与 #76 的分工：**⑤ 防「再抄一份」，⑩ 防「抄了但没人能跑到」**；
#   还抓住「把断言搬进一个没人调的 helper」这种规避（镜像归零后剩下的同族失效形态）。
function Get-MethodBlocks([string]$text) {
    $lss = $text -split "`r?`n"
    $res = @()
    $kk = 0
    while ($kk -lt $lss.Count) {
        $ln2 = $lss[$kk]
        $isSig = ($ln2 -match '^[ \t]+(?:(?:private|internal|public|static|async)[ \t]+)+[^\n;=]*?\b([A-Za-z][A-Za-z0-9_]*)[ \t]*\(')
        if (-not $isSig) { $kk++; continue }
        $nm = $Matches[1]
        # 表达式体定义（2026-10-19 补）：旧尺子整行排除 `=>` ⇒ 这一**整类**从来没进过 ⑩ 的域
        #   （实测漏掉 `Be`/`Tok`/`Fmt`/`NitsOf`/`ReadBe16`/`ScopeAllows`/`IsEvenGuard` 七条），
        #   「0 孤儿」于是只是这条口径下的结论；而 `static void W() => ProbeX();` 这种包装还兼作
        #   BFS 的一条边 ⇒ 漏它不仅看不见"死在表达式体里的断言"，还会把经它转发的真探针误判成不可达。
        #   收尾规则：取到第一个以 `;` 结尾的行（单行体就是自己）；取不到就不臆造方法体。
        if ($ln2 -match '=>') {
            $mm3 = $kk; $end3 = -1
            while ($mm3 -lt $lss.Count -and $mm3 -le $kk + 14) {
                if ($lss[$mm3].Trim().EndsWith(';')) { $end3 = $mm3; break }
                $mm3++
            }
            if ($end3 -lt 0) { $kk++; continue }
            $body3 = (($lss[$kk..$end3] -join "`n") + "`n")
            $res += [pscustomobject]@{ Name = $nm; A = $kk + 1; B = $end3 + 1; Expr = $true; Text = $body3 }
            $kk++   # 不跳过整段方法体：跳着走会把后一条定义连同嵌套局部函数一起吞掉（实测只取到 74/101 条签名）
            continue
        }
        $jj = $kk; $opened = $false
        while ($jj -lt $lss.Count -and $jj -le $kk + 6) {
            if ($lss[$jj].Contains('{')) { $opened = $true; break }
            $tt = $lss[$jj].Trim()
            if ($tt.EndsWith(';') -or $tt.EndsWith(',')) { break }
            $jj++
        }
        if (-not $opened) { $kk++; continue }
        $dep = 0; $mm2 = $jj; $body = ''
        while ($mm2 -lt $lss.Count) {
            $line2 = $lss[$mm2]
            $body += $line2 + "`n"
            foreach ($ch in $line2.ToCharArray()) {
                if ($ch -eq '{') { $dep++ } elseif ($ch -eq '}') { $dep-- }
            }
            if ($dep -le 0) { break }
            $mm2++
        }
        $res += [pscustomobject]@{ Name = $nm; A = $kk + 1; B = $mm2 + 1; Expr = $false; Text = $body }
        $kk++   # 不跳过整段方法体：跳着走会把后一条定义连同嵌套局部函数一起吞掉（实测只取到 74/101 条签名）
    }
    return $res
}
function Get-Reachable([object[]]$blks, [string[]]$seedList) {
    $byN = @{}
    foreach ($b in $blks) { if (-not $byN.ContainsKey($b.Name)) { $byN[$b.Name] = $b } }
    $seen2 = @{}
    $qq = New-Object System.Collections.Generic.Queue[string]
    foreach ($s in ($seedList | Sort-Object -Unique)) { $qq.Enqueue($s) }
    while ($qq.Count -gt 0) {
        $cn2 = $qq.Dequeue()
        if ($seen2.ContainsKey($cn2)) { continue }
        $seen2[$cn2] = $true
        if (-not $byN.ContainsKey($cn2)) { continue }
        foreach ($c2 in [regex]::Matches($byN[$cn2].Text, '\b([A-Z][A-Za-z0-9_]{3,})[ \t]*\(')) {
            $nx = $c2.Groups[1].Value
            if (-not $seen2.ContainsKey($nx) -and $byN.ContainsKey($nx)) { $qq.Enqueue($nx) }
        }
    }
    return $seen2
}
function Get-DispatchSeeds([string]$text) {
    $sd = @('Main')
    foreach ($m3 in [regex]::Matches($text, '(?m)^[ \t]*case\s+"[a-z0-9\-]+"\s*:')) {
        $f3 = $m3.Index + $m3.Length
        if ($f3 + 400 -ge $text.Length) { continue }
        foreach ($c3 in [regex]::Matches($text.Substring($f3, 400), '\b((?:Probe|Dump|Run|Handle)[A-Za-z0-9_]+)[ \t]*\(')) {
            $sd += $c3.Groups[1].Value
        }
    }
    return $sd
}
$judgePat = '(?m)(?<![\w.])(?:Check|Ok|No|CK)[ \t]*\('
$blk10 = Get-MethodBlocks $spText
$exprBlks10 = @($blk10 | Where-Object { $_.Expr })
$reach10 = Get-Reachable $blk10 (Get-DispatchSeeds $spText)
$orphan10 = @($blk10 | Where-Object { (-not $reach10.ContainsKey($_.Name)) -and [regex]::IsMatch($_.Text, $judgePat) })
CK ($blk10.Count -ge 60) ('⑩ 前置：扫到方法块 ' + $blk10.Count + ' 个（≥60 ⇒ 扫描器自己没瞎；「0 孤儿」才是结论而不是「没扫到」）')
$ebLine = '无'
if ($exprBlks10.Count) { $ebLine = (@($exprBlks10 | ForEach-Object { $_.Name }) -join ', ') }
CK ($exprBlks10.Count -ge 1) ('⑩ 前置：扫到的块里表达式体（=>）必须 ≥1 条（实 ' + $exprBlks10.Count + '：' + $ebLine + '）—— 这一类塌成 0 ⇒ 整类又变成不可见，「0 孤儿」立刻退回成下界')
$knownMiss = @()
foreach ($kn in @('Main', 'ProbeContract', 'ProbeVendorHdr', 'ProbeIpcSubmitToRunning')) {
    $hitK = @($blk10 | Where-Object { $_.Name -eq $kn })
    if ($hitK.Count -eq 0) { $knownMiss += ($kn + '（没扫到）') }
    elseif (-not $reach10.ContainsKey($kn)) { $knownMiss += ($kn + '（扫到但闭包到不了）') }
}
$kmLine = '无'
if (@($knownMiss).Count) { $kmLine = ($knownMiss -join ', ') }
CK (@($knownMiss).Count -eq 0) ('⑩ 正控：四个确知可达的方法必须既被扫到又在闭包内（实 ' + $kmLine + '）—— 其中 ProbeIpcSubmitToRunning 走的是跨行 case 体，专治「种子漏了 switch 里的调用」这种假孤儿')
$orLine10 = '无'
if ($orphan10.Count) { $orLine10 = (@($orphan10 | ForEach-Object { $_.Name + ':' + $_.A + '（断言 ' + ([regex]::Matches($_.Text, $judgePat)).Count + ' 处）' }) -join ', ') }
CK ($orphan10.Count -eq 0) ('⑩ 含断言却任何派发 mode 都到不了的方法 = 0（实 ' + $orphan10.Count + '：' + $orLine10 + '）—— 这就是活在执行文件里的镜像：要么接上，要么删掉，不许留着')

# ── ⑩-pre 反控：合成源文本上，四种方向都必须按预期 ─────────────────────────
$syn10 = @'
class T {
    static void Main() { ProbeA(); WrapExpr(); }
    static void ProbeA() { Check("a", true); Helper(); }
    static void Helper() { Check("h", true); }
    static void WrapExpr() => ProbeB();
    static void ProbeB() { Check("b", true); }
    static void DeadEnd() { Check("d", true); }
    static void DeadExpr() => Check("de", true);
    static void EmptyNoCaller() { DoSomethingElse(); }
}
'@
$blkSyn = Get-MethodBlocks $syn10
$reachSyn = Get-Reachable $blkSyn (Get-DispatchSeeds $syn10)
$orphSyn = @($blkSyn | Where-Object { (-not $reachSyn.ContainsKey($_.Name)) -and [regex]::IsMatch($_.Text, $judgePat) })
$teeth10 = @()
$t10n = 0
$t10n++; if ($blkSyn.Count -lt 8) { $teeth10 += ('合成件只扫到 ' + $blkSyn.Count + '/8 个方法') }
$t10n++; $orNames = (@($orphSyn | ForEach-Object { $_.Name } | Sort-Object) -join ',')
if ($orNames -ne 'DeadEnd,DeadExpr') { $teeth10 += ('孤儿应恰好是 DeadEnd,DeadExpr，实 ' + $orNames) }
foreach ($must in @('Main', 'ProbeA', 'Helper', 'WrapExpr', 'ProbeB')) {
    $t10n++
    if (-not $reachSyn.ContainsKey($must)) { $teeth10 += ($must + ' 应可达却判不可达（ProbeB 只能经表达式体包装 WrapExpr 到达 ⇒ 闭包漏了 =>）') }
}
$t10n++; if ($reachSyn.ContainsKey('EmptyNoCaller')) { $teeth10 += 'EmptyNoCaller 无人调用却判可达' }
$t10Line = '全部符合（不可达+含断言⇒抓到 / 经一跳可达⇒放行 / 经表达式体包装可达⇒放行 / 无人调用且无断言⇒不算）'
if ($teeth10.Count) { $t10Line = ($teeth10 -join ' | ') }
CK ($teeth10.Count -eq 0) ('⑩-pre 有牙自证：合成件 ' + $t10n + ' 项方向判据（' + $t10Line + '）')

# ── ⑪ 归属域：清单外的脚本必须「被在册脚本点名」或「落进一个有理由的族」──────
$offFamilies = @(
    @{ Pat = '^_lib-';       Why = '库/共享底座：由在册门禁 dot-source，本身不是门禁' }
    @{ Pat = '^_gen-';       Why = '生成器：被可复现锁（第 74 条）当执行对象' }
    @{ Pat = '^_probe-|^probe-'; Why = '一次性取证工具：起被测二进制但不进轮（取证用）' }
    @{ Pat = '^ensure-|^generate-'; Why = '生产者：造 tests/output 下的夹具' }
    @{ Pat = '^_accept';     Why = '脚手架：人工验收脚本，判据与 union 重叠' }
)
$offExplicit = [ordered]@{
    'verify-oracle-matrix.ps1'     = '手工长跑（5015 臂 ≈2.5 h，远超 ScriptTimeoutSec ⇒ 运行器注释里登记了不入清单的理由）'
    'verify-color-matrix-full.ps1' = '手工长跑（全层网格 360 臂；同上一条理由）'
    'verify-release-package.ps1'   = '发布套件（L3/打包期手工跑，产物级判据在 pack 之后才有意义）'
    'run-full-matrix-test.ps1'     = '旧一次性驱动（包装 matrix-full；待逐支证明判据已被覆盖后再删）'
    'run-full-matrix-test-safe.ps1' = '旧一次性驱动（同上，带安全阀）'
    'run-color-tests.ps1'          = '旧一次性驱动（判据已被 verify-color-* 覆盖 ⇒ 待证后删）'
    'run-color-regress.ps1'        = '旧一次性驱动（同上）'
    'run-quality-tests.ps1'        = '旧一次性驱动（同上）'
    'run-thread-tests.ps1'         = '旧一次性驱动（同上）'
    'run-ps-verify.ps1'            = '旧一次性驱动（PS 兼容性由 verify-ps-compat 覆盖 ⇒ 待证后删）'
    'archive-diagnostics.ps1'      = '脚手架（诊断留档）'
    'cleanup-structure.ps1'        = '脚手架（结构清理）'
    'insert_cicp.ps1'              = '脚手架（历史一次性 PNG cICP 注入）'
    '_check-ps-parse.ps1'          = '单文件 PS 解析取数器（账本 §36/§40 手跑用；判据本体在 verify-ps-compat.ps1，本支只喂它自己的手跑复现）'
}
$managedBlob = ''
foreach ($f5 in $scriptListAll) {
    if (-not $rawText.ContainsKey($f5)) { continue }
    foreach ($ln5 in ($rawText[$f5] -split "`r?`n")) {
        if ($ln5.Trim().StartsWith('#')) { continue }
        $managedBlob += $ln5 + "`n"
    }
}
$named11 = @(); $fam11 = @(); $expl11 = @(); $orph11 = @()
foreach ($fi5 in $allPs) {
    $f5n = $fi5.Name
    if ($f5n -eq '_run-step3-gates.ps1') { continue }
    if ($managedSet.ContainsKey($f5n)) { continue }
    $stem5 = $f5n -replace '\.ps1$', ''
    $isNamed = ($managedBlob.Contains($f5n)) -or ($managedBlob -match ('(?<![\w\-.])' + [regex]::Escape($stem5) + '(?![\w\-])'))
    if ($isNamed) { $named11 += $f5n; continue }
    if ($offExplicit.Contains($f5n)) { $expl11 += $f5n; continue }
    $famHit = $null
    foreach ($fam in $offFamilies) { if ($f5n -match $fam.Pat) { $famHit = $fam; break } }
    if ($famHit) { $fam11 += $f5n; continue }
    $orph11 += $f5n
}
$or11Line = '无'
if (@($orph11).Count) { $or11Line = ($orph11 -join ', ') }
CK (@($orph11).Count -eq 0) ('⑪-a 清单外脚本要么被在册脚本点名、要么落进有理由的族/显式表（未归属 ' + @($orph11).Count + ' 支：' + $or11Line + '）—— 新写一支脚本不登记就在这里红')
$stale11 = @($offExplicit.Keys | Where-Object { -not (Test-Path (Join-Path $scriptsDir $_)) })
$badList11 = @($offExplicit.Keys | Where-Object { $managedSet.ContainsKey($_) })
$sl11Line = '无'
if (@($stale11).Count) { $sl11Line = ($stale11 -join ', ') }
CK (@($stale11).Count -eq 0) ('⑪-b 显式归属表里没有已不在盘的行（陈旧 ' + @($stale11).Count + ' 条：' + $sl11Line + '）')
$bl11Line = '无'
if (@($badList11).Count) { $bl11Line = ($badList11 -join ', ') }
CK (@($badList11).Count -eq 0) ('⑪-c 已进受管清单的脚本必须从归属表删掉（漏删 ' + @($badList11).Count + ' 条：' + $bl11Line + '）')
$debt11 = @()
foreach ($k6 in @($expl11 + $fam11 + $named11)) {
    $p6 = Join-Path $scriptsDir $k6
    if (-not (Test-Path $p6)) { continue }
    $t6 = [System.IO.File]::ReadAllText($p6)
    $j6 = ([regex]::Matches($t6, $judgePat)).Count
    if ($j6 -eq 0) { continue }
    if ($managedSet.ContainsKey($k6)) { continue }
    $debt11 += ('{0}（判据 {1} 处）' -f $k6, $j6)
}
if (@($debt11).Count) {
    Write-Host ('WARN ⑪-d 有判据却不进整轮的清单外脚本 = ' + @($debt11).Count + ' 支：' + (($debt11 | Select-Object -First 6) -join ', ') + ' ⇒ 要么接进清单，要么按 A-1 的先证明再逐支删（本条只警告不判红：历史上有意的长跑例外）') -ForegroundColor Yellow
}
$acc11 = @($named11).Count + @($fam11).Count + @($expl11).Count + @($orph11).Count
$offTotal11 = @($allPs).Count - @($scriptListAll).Count - 1
CK ($acc11 -eq $offTotal11) ('⑪-e 账目：清单外 ' + $offTotal11 + ' 支 == 被点名 ' + @($named11).Count + ' + 族内 ' + @($fam11).Count + ' + 显式表 ' + @($expl11).Count + ' + 未归属 ' + @($orph11).Count + '（实 ' + $acc11 + '；不等即有点名被吞或重复计数）')
Write-Host ('ℹ      ⑪ 清单外 ' + $offTotal11 + ' 支 = 被在册脚本点名 ' + @($named11).Count + ' ｜ 族归属 ' + @($fam11).Count + ' ｜ 显式登记 ' + @($expl11).Count + ' ｜ 未归属 ' + @($orph11).Count)

# ── ⑪-pre 反控：换一批合成输入，验证 ⑪ 用的还是同一把分类尺 ────────────────
$syn11a = Split-OffBook @('s_plain') @() @{} @{ s_plain = '手工工具' }
$syn11b = Split-OffBook @('s_new') @() @{} @{}
$teeth11 = @()
if (@($syn11a.OkExempt).Count -ne 1) { $teeth11 += '已登记的清单外脚本应放行' }
if (@($syn11b.Unregistered).Count -ne 1) { $teeth11 += '新增一支未登记的脚本没被判红' }
$t11Line = '全部符合（已登记⇒放行 / 新增未登记⇒红）'
if ($teeth11.Count) { $t11Line = ($teeth11 -join ' | ') }
CK ($teeth11.Count -eq 0) ('⑪-pre 有牙自证：合成件 2 项方向判据（' + $t11Line + '）')
# ── 信息行：两边的计数与差集（逐名，不压成总数）────────────────────────────
Write-Host ""
Write-Host ("ℹ      组表并集 = {0} 个 mode；运行器 `$Probes` = {1} 个；受管基线 = {2} 条（证据源 {3} 条，去掉守卫自己）" -f $union.Count, $probes.Count, $scriptListAll.Count, $evidenceScripts.Count)
Write-Host ("ℹ      档① 在 `$Probes` 里（运行器自己起宿主）= {0}：{1}" -f @($tierProbes).Count, ($tierProbes -join ', '))
Write-Host ("ℹ      档② 由 `$scriptList` 内脚本的参数位置调用 = {0}：{1}" -f @($tierManaged).Count, (($tierManaged | ForEach-Object { $_ + '<-' + ((Get-Evidence $_).Managed | Select-Object -First 1) }) -join ', '))
Write-Host ("ℹ      档③ 只被非清单脚本调用（不算已覆盖）= {0}：{1}" -f @($tierUnmanaged).Count, ($tierUnmanaged -join ', '))
Write-Host ("ℹ      档④ 零调用点（转红）= {0}：{1}" -f @($tierNone).Count, ($tierNone -join ', '))

Write-Host "===== test-group-coverage: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
