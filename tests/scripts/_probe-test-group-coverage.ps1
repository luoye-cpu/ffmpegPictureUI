# ═══════════════════════════════════════════════════════════════════════════════
# _probe-test-group-coverage.ps1 —— 测试组覆盖一致性（2026-10-05 物理拆分时新增）
#
# 【为什么必须有】
#   拆分后「哪些 mode 归哪个组」是**手工维护**的表（`tests/Tests.Shared/TestGroups.cs`），
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
#
# 【口径说明】③ 只要求"名字真实存在"，**不要求**有人跑它 —— "有没有人跑"是 ⑥ 的职责。
#   两者分开是有意的：拼错 mode 名（③ 抓）与"实现了却没人跑"（⑥ 抓）是两类不同的失效，
#   合成一条就会像 2026-10-10 审查那样，让守卫自己的注释替零调用点作证。
#   ⚠ 本次接线（2026-10-11）的实跑读数与逐名清单见 `docs/review-2026-10-11/l6-coverage-guard-note.md`：
#     当期零调用点 8 个（`bench`、`bitdepth`、`gamut`、`gainmap-encode`、`gainmap-decode`、`gainmap-struct`、
#     `pq`、`ultrahdr`）+ 只有非清单调用点 1 个（`icc`，唯一真调用点在 `ensure-fixtures.ps1:L97`）。
#   **2026-10-12（L-7）**：`bench` 已接进 `$Probes` ⇒ 零调用点剩 **7** 个（`bitdepth`、`gamut`、
#     `gainmap-encode`、`gainmap-decode`、`gainmap-struct`、`pq`、`ultrahdr`；实跑 `PASS=18 FAIL=2`，
#     档① 由 26 变 27）。本守卫**仍是红的**，直到剩下这些被接进 `$Probes` 或某条受管门禁 —— 这是它
#     **要抓的东西**，不是误伤；放宽判据来消红 = 把 D1 §0 第 2 条重新埋回去。
#     ⚠ `docs/TESTING.md:87` 记的「`_probe-test-group-coverage` 8/0」与 `_run-step3-gates.ps1:1033-1038`
#       对第 69 条判据的复述都已过期，须由主循环同步（本 lane 范围只这一只脚本）。
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
    # 拆分工程（`Join-Path $root "tests/$proj/bin/Release/…/$($modes[$proj].exe).exe"`）里
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

$groupsCs   = 'tests/Tests.Shared/TestGroups.cs'
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
foreach ($m in [regex]::Matches($gText, '\["(?<g>[a-z0-9\-]+)"\]\s*=\s*new\[\]\s*\{(?<modes>[^}]*)\}')) {
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
$probe = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
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
$exe = Join-Path $root "tests/ui-core/bin/Release/net11.0/win-x64/$($modes.exe).exe"
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
if (Test-Path 'tests/ServiceProbe/Program.cs') { $spProgram = Get-Content 'tests/ServiceProbe/Program.cs' -Raw }
function Get-GroupOf([string]$mode) {
    foreach ($g in $groupMap.Keys) { if ($groupMap[$g] -ccontains $mode) { return $g } }
    return '?'
}
function Get-GroupHost([string]$gname) {
    $cj = @(Get-ChildItem "tests/$gname" -Filter '*.csproj' -File -EA SilentlyContinue)
    foreach ($c in $cj) {
        $mm = [regex]::Match((Get-Content $c.FullName -Raw), '<AssemblyName>([^<]+)</AssemblyName>')
        if ($mm.Success) { return ($mm.Groups[1].Value + '.exe') }
    }
    return "tests/$gname 的宿主 exe（未取到 AssemblyName）"
}
function Get-OwnerHint([string]$mode) {
    $g = Get-GroupOf $mode
    $exeName = Get-GroupHost $g
    $inSp = $false
    if ($spProgram -match ('case "' + [regex]::Escape($mode) + '"')) { $inSp = $true }
    $touched = @()
    foreach ($s in $evidenceScripts) {
        if (-not $rawText.ContainsKey($s)) { continue }
        if ($rawText[$s] -match ('tests/' + [regex]::Escape($g) + '[/\\"'' .]')) { $touched += $s }
    }
    $parts = @()
    if ($inSp) { $parts += 'ServiceProbe.exe 分派了该 mode ⇒ 本该进运行器 `$Probes`，或由 `$scriptList` 内某条起 ServiceProbe 的清单脚本带上（形态参照 verify-color-peak.ps1 的 RunTool 参数表）' }
    $parts += ('该组宿主 = ' + $exeName)
    if (@($touched).Count) { $parts += ('`$scriptList` 内引用过该组目录的脚本 = ' + (($touched | Select-Object -First 4) -join ', ')) }
    else { $parts += ('`$scriptList` 内没有任何脚本引用过 tests/' + $g) }
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
    if (-not (Test-Path "tests/$g")) { $missingProj += $g }
}
$missingLine = '无'
if (@($missingProj).Count) { $missingLine = ($missingProj -join ', ') }
CK (@($missingProj).Count -eq 0) "② 组表里每个组都有工程目录（缺目录：$missingLine，点名 $(@($missingProj).Count) 条）"

# ── ③ 组表里每个 mode 名都必须**真实存在**（防拼错；"有没有人跑"归 ⑥）────────
$groupProgram = @{}
foreach ($g in $groupMap.Keys) {
    $pf = "tests/$g/Program.cs"
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
        # 真实分派形态： mode == "xxx"（进了分派链），而非仅出现在 `--list`/注释/字符串里
        if ($prog -match ('mode\s*==\s*"' + [regex]::Escape($mode) + '"')) { $dispatchParsed++; continue }
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

# ── 信息行：两边的计数与差集（逐名，不压成总数）────────────────────────────
Write-Host ""
Write-Host ("ℹ      组表并集 = {0} 个 mode；运行器 `$Probes` = {1} 个；受管基线 = {2} 条（证据源 {3} 条，去掉守卫自己）" -f $union.Count, $probes.Count, $scriptListAll.Count, $evidenceScripts.Count)
Write-Host ("ℹ      档① 在 `$Probes` 里（运行器自己起宿主）= {0}：{1}" -f @($tierProbes).Count, ($tierProbes -join ', '))
Write-Host ("ℹ      档② 由 `$scriptList` 内脚本的参数位置调用 = {0}：{1}" -f @($tierManaged).Count, (($tierManaged | ForEach-Object { $_ + '<-' + ((Get-Evidence $_).Managed | Select-Object -First 1) }) -join ', '))
Write-Host ("ℹ      档③ 只被非清单脚本调用（不算已覆盖）= {0}：{1}" -f @($tierUnmanaged).Count, ($tierUnmanaged -join ', '))
Write-Host ("ℹ      档④ 零调用点（转红）= {0}：{1}" -f @($tierNone).Count, ($tierNone -join ', '))

Write-Host "===== test-group-coverage: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
