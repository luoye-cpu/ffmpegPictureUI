# verify-module-registry.ps1 — 机读模块账本门禁（批 1，docs/TEST_MODULE_PLAN_2026-11-12.md §2 批 1）
# 账本 = tests/scripts/_module-registry.json（能力域→承载门禁→判据档位→断言数锁 的单一真值）。
#
# 判据四组：
#   ① 覆盖锁（不落后针）：CliParser.Parse switch 窗口内提取的每个 case 字面量，必须与账本
#      surface=named 条目的 names+aliases 双向差集为空 —— 新增选项不登记覆盖 = 整轮红；
#      账本里的幽灵名同样红。
#   ② 档位不撒谎针：tier=E2E/E2E-UI 的条目，其选项字面量必须真出现在 carrier 门禁文件或其
#      执行面语料（_lib-*/ServiceProbe 源/ui 宿主源）里；tier=blind 必须 carriers 空且 blindReason
#      非空 —— 档位标错 = 红。
#   ③ carrier 在册锁：每个 carrier.gate 必须在 runner $scriptList 在册 —— 指向幽灵门禁 = 红。
#   ④ 结构锁：schema 枚举/唯一 id/locks 数值与三把尺源内字面量对账（verify-ui-host 458+五件、
#      param-matrix $jExpected=126、union $expectedOldCount=458）/ src 活见证（±3 行窗口命中选项名）。
#
# ⚠ 本门禁不跑任何 exe（只读源码/JSON/PS1）⇒ 不进 STALE 闸（runner :717-860 的裁定，2026-11-12 批）。
# 负控 -SelfCheck：三个内存变异（删条目/换 carrier 到不含字面量的在册门禁/carrier 指向清单外脚本）
#   各必须被对应判据抓红 —— 形态对齐 verify-color-out-matrix 的 $ncOk 约定（门禁自身不因负控转红）。
param([switch]$SelfCheck)
$ErrorActionPreference = 'Stop'
$scriptsDir = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptsDir)
$regPath = Join-Path $scriptsDir '_module-registry.json'
$cliPath = Join-Path $repoRoot 'src/FfmpegGui/CliParser.cs'
$runnerPath = Join-Path $scriptsDir '_run-step3-gates.ps1'

$script:pass = 0
$script:fail = 0
function CK([bool]$cond, [string]$name) {
    if ($cond) { $script:pass++; Write-Output "  PASS  $name" }
    else { $script:fail++; Write-Output "  FAIL  $name" }
}

# ── 判据函数（参数化，正向与负控复用）─────────────────────────────────────────
function Get-CoverageViolations($reg, [string[]]$caseLits) {
    # ① 双向差集：提取集 vs 账本 named 面
    $bad = @()
    $regNames = @()
    foreach ($e in @($reg.entries)) {
        if ($e.kind -eq 'option' -and $e.surface -eq 'named') {
            if (-not $e.names -or @($e.names).Count -eq 0) { $bad += "条目 $($e.id)：names 为空"; continue }
            $regNames += @($e.names) + @($e.aliases)
        }
    }
    $regSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$regNames)
    $caseSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$caseLits)
    foreach ($c in $caseSet) { if (-not $regSet.Contains($c)) { $bad += "CLI 具名选项 '$c' 未登记覆盖（不落后针）" } }
    foreach ($r in $regSet) { if (-not $caseSet.Contains($r)) { $bad += "账本幽灵名 '$r'（不在 Parse switch 提取集内）" } }
    return ,$bad
}
function Get-EntryCliCorpus($e) {
    # ② 的语料必须**逐条目**构建（lib + 该条目自己的 carrier）：
    #   全局共享语料会让「A 条目的字面量经 B 条目的 carrier 文件」漏进来 ⇒ 档位针失去牙
    #   （2026-11-12 首轮负控实测：M2 变异抓不到，正是全局语料的漏洞）。
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.Append($corpusLib)
    foreach ($c in @($e.carriers)) {
        if ($c.kind -in @('cli', 'ui', 'host')) {
            $p = Join-Path $scriptsDir $c.gate
            if (Test-Path $p) { [void]$sb.Append([IO.File]::ReadAllText($p)) }
        }
    }
    return $sb.ToString()
}
function Get-TierViolations($reg, $corpusCli, $corpusUi, $corpusProbe, $corpusHost) {
    # ② 档位不撒谎：E2E/E2E-UI ⇒ 字面量在语料；blind ⇒ carriers 空 + blindReason 非空
    $bad = @()
    foreach ($e in @($reg.entries)) {
        $lits = @()
        if ($e.kind -eq 'option') { $lits = @($e.names) + @($e.aliases) }
        switch ($e.tier) {
            'E2E' {
                if (@($e.carriers).Count -eq 0) { $bad += "$($e.id)：tier=E2E 但 carriers 空"; continue }
                if ($lits.Count -eq 0) { continue }   # capability 条目无字面量，字面量查证不适用（carrier 在册仍由③管）
                $text = Get-EntryCliCorpus $e
                foreach ($c in @($e.carriers)) { if ($c.kind -eq 'host') { $text = "$text`n$corpusHost" } }
                $hit = $false
                foreach ($l in $lits) { if ($l -and $text.Contains($l)) { $hit = $true; break } }
                if (-not $hit) { $bad += "$($e.id)：tier=E2E 但字面量 $($lits -join '/') 不在 carrier 语料（档位撒谎）" }
            }
            'E2E-UI' {
                if (@($e.carriers).Count -eq 0) { $bad += "$($e.id)：tier=E2E-UI 但 carriers 空"; continue }
                if ($lits.Count -eq 0) { continue }
                $hit = $false
                foreach ($l in $lits) { if ($l -and $corpusUi.Contains($l)) { $hit = $true; break } }
                if (-not $hit) { $bad += "$($e.id)：tier=E2E-UI 但字面量不在 ui 宿主语料" }
            }
            'static' {
                if (@($e.carriers).Count -eq 0) { $bad += "$($e.id)：tier=static 但 carriers 空"; continue }
                $hit = $false
                foreach ($l in $lits) { if ($l -and $corpusProbe.Contains($l)) { $hit = $true; break } }
                if (-not $hit) { $bad += "$($e.id)：tier=static 但字面量不在 _probe-* 语料" }
            }
            'blind' {
                if (@($e.carriers).Count -ne 0) { $bad += "$($e.id)：tier=blind 但 carriers 非空（假 carrier）" }
                if (-not $e.blindReason) { $bad += "$($e.id)：tier=blind 但缺 blindReason" }
            }
            default { $bad += "$($e.id)：未知 tier '$($e.tier)'" }
        }
    }
    return ,$bad
}
function Get-CarrierViolations($reg, [System.Collections.Generic.HashSet[string]]$slSet) {
    # ③ carrier 在册
    $bad = @()
    foreach ($e in @($reg.entries)) {
        foreach ($c in @($e.carriers)) {
            if (-not $c.gate) { $bad += "$($e.id)：carrier 缺 gate"; continue }
            if (-not $slSet.Contains($c.gate)) { $bad += "$($e.id)：carrier '$($c.gate)' 不在 runner 清单（幽灵门禁）" }
        }
    }
    return ,$bad
}
function Get-StructureViolations($reg, $cliLines, $locksSrc) {
    # ④ schema/唯一 id/src 活见证/locks 对账
    $bad = @()
    $ids = @()
    foreach ($e in @($reg.entries)) {
        $ids += $e.id
        if (-not $e.id) { $bad += '存在无 id 条目' }
        if ($e.kind -notin @('option', 'capability', 'blindspot')) { $bad += "$($e.id)：未知 kind '$($e.kind)'" }
        if ($e.domain -notin @($reg.domains)) { $bad += "$($e.id)：domain '$($e.domain)' 不在 domains 表" }
    }
    $dup = $ids | Group-Object | Where-Object Count -gt 1
    if ($dup) { $bad += "重复 id：$(($dup.Name) -join ',')" }
    # src 活见证（kind=option 且 surface=named：±3 行窗口内必须命中主名）
    foreach ($e in @($reg.entries)) {
        if ($e.kind -ne 'option' -or $e.surface -ne 'named') { continue }
        if ($e.src -notmatch '^(.+):(\d+)$') { $bad += "$($e.id)：src 形态非法 '$($e.src)'"; continue }
        $f = Join-Path $repoRoot $Matches[1]
        if (-not (Test-Path $f)) { $bad += "$($e.id)：src 文件不存在 $f"; continue }
        $ln = [int]$Matches[2]
        $lo = [Math]::Max(0, $ln - 4); $hi = [Math]::Min($cliLines.Count - 1, $ln + 2)
        $winText = ($cliLines[$lo..$hi] -join "`n")
        $hit = $false
        foreach ($n in (@($e.names) + @($e.aliases))) { if ($n -and $winText.Contains($n)) { $hit = $true; break } }
        if (-not $hit) { $bad += "$($e.id)：src 见证失效（$($e.src) ±3 行内无 $($e.names -join '/')）—— CliParser 改了行号就同批更新账本" }
    }
    # locks 与三把尺源内字面量对账
    if ($locksSrc.aggExpected -ne $reg.locks.'ui-host-arms'.value) { $bad += "locks.ui-host-arms.value=$($reg.locks.'ui-host-arms'.value) ≠ verify-ui-host `$aggExpected=$($locksSrc.aggExpected)" }
    if ($locksSrc.jExpected -ne $reg.locks.'param-matrix-j'.value) { $bad += "locks.param-matrix-j.value ≠ `$jExpected=$($locksSrc.jExpected)" }
    if ($locksSrc.unionExpected -ne $reg.locks.'ui-split-union'.value) { $bad += "locks.ui-split-union.value ≠ `$expectedOldCount=$($locksSrc.unionExpected)" }
    $gotMembers = @($reg.locks.'ui-host-arms'.members.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) | Sort-Object
    $wantMembers = $locksSrc.armsMembers | Sort-Object
    if (($gotMembers -join ';') -ne ($wantMembers -join ';')) { $bad += "locks.ui-host-arms.members 与 verify-ui-host `$arms 不符：账本[$($gotMembers -join ';')] vs 源[$($wantMembers -join ';')]" }
    return ,$bad
}

# ── 语料与提取 ────────────────────────────────────────────────────────────────
$reg = Get-Content $regPath -Raw -Encoding UTF8 | ConvertFrom-Json
CK ($reg.version -ge 1) "registry loaded (version=$($reg.version), entries=$(@($reg.entries).Count))"
$cliLines = [System.IO.File]::ReadAllLines($cliPath)
$cliText = [System.IO.File]::ReadAllText($cliPath)

# Parse switch 窗口：anchor = Parse 的 `switch (arg.ToLowerInvariant())` → PrintHelp 定义行。
#   ApplyExtraOption 的 switch 是 (key.ToLower...) ⇒ 不会被此 anchor 命中（透传面不进判据①，notes 已记）。
$a1 = $cliText.IndexOf('switch (arg.ToLowerInvariant())')
$a2 = $cliText.IndexOf('public static void PrintHelp()')
CK ($a1 -ge 0 -and $a2 -gt $a1) "CliParser parse-switch window located ($a1..$a2)"
$caseLits = @()
if ($a1 -ge 0 -and $a2 -gt $a1) {
    $win = $cliText.Substring($a1, $a2 - $a1)
    $caseLits = [regex]::Matches($win, 'case\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
}
CK ($caseLits.Count -ge 40) "case literals extracted: $($caseLits.Count)（首版实测 43 = 33 具名选项 + 别名去重；泛型 case var 不在此列；若此数变 ⇒ CliParser 选项面变了，账本同批更新）"

# runner $scriptList（AST 提取，防幽灵 carrier）
$tok = $null; $err = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($runnerPath, [ref]$tok, [ref]$err)
$slLits = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($a in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
    if ($a.Left -isnot [System.Management.Automation.Language.VariableExpressionAst]) { continue }
    if ($a.Left.VariablePath.UserPath -ne 'scriptList') { continue }
    foreach ($s in $a.Right.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true)) {
        [void]$slLits.Add($s.Value)
    }
}
CK ($slLits.Count -ge 100) "scriptList literals extracted from runner: $($slLits.Count)"

# 语料：_lib-* / _probe-* / ServiceProbe 源 / ui 宿主源 / carrier 门禁文本
$sb = [System.Text.StringBuilder]::new()
Get-ChildItem $scriptsDir -Filter '_lib-*.ps1' | ForEach-Object { [void]$sb.Append([IO.File]::ReadAllText($_.FullName)) }
$corpusLib = $sb.ToString()
$sb2 = [System.Text.StringBuilder]::new()
Get-ChildItem $scriptsDir -Filter '_probe-*.ps1' | ForEach-Object { [void]$sb2.Append([IO.File]::ReadAllText($_.FullName)) }
$corpusProbe = $sb2.ToString()
$sb3 = [System.Text.StringBuilder]::new()
Get-ChildItem (Join-Path $repoRoot 'tests/projects') -Filter '*.cs' -Recurse | ForEach-Object { [void]$sb3.Append([IO.File]::ReadAllText($_.FullName)) }
$corpusUi = $sb3.ToString()
$corpusHost = $corpusUi   # ServiceProbe 源也在 tests/projects 下，host 臂语料同源（保守：字面量在宿主源即认）

function Build-CliCorpus($reg) {
    # （保留给负控 M2 的旧签名兼容：现按逐条目语料判 ②，此处仅拼 lib，不再全局共享）
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.Append($corpusLib)
    return $sb.ToString()
}

# 三把尺源内字面量
$uiHostText = [IO.File]::ReadAllText((Join-Path $scriptsDir 'verify-ui-host.ps1'))
$unionText = [IO.File]::ReadAllText((Join-Path $scriptsDir 'verify-ui-split-union.ps1'))
$pmText = [IO.File]::ReadAllText((Join-Path $scriptsDir 'verify-ui-param-matrix.ps1'))
$aggM = [regex]::Match($uiHostText, '\$aggExpected\s*=\s*(\d+)')
$jM = [regex]::Match($pmText, '\$jExpected\s*=\s*(\d+)')
$unM = [regex]::Match($unionText, '\$expectedOldCount\s*=\s*(\d+)')
$armMs = [regex]::Matches($uiHostText, "Name\s*=\s*'([^']+)';\s*Exe[^;]+;\s*Expected\s*=\s*(\d+)")
$armsMembers = @($armMs | ForEach-Object { "$($_.Groups[1].Value)=$($_.Groups[2].Value)" })
$locksSrc = @{
    aggExpected   = if ($aggM.Success) { [int]$aggM.Groups[1].Value } else { -1 }
    jExpected     = if ($jM.Success) { [int]$jM.Groups[1].Value } else { -1 }
    unionExpected = if ($unM.Success) { [int]$unM.Groups[1].Value } else { -1 }
    armsMembers   = $armsMembers
}
CK ($locksSrc.armsMembers.Count -eq 5) "verify-ui-host arms parsed: $($armsMembers -join ', ')"

# ── 正向四组 ─────────────────────────────────────────────────────────────────
$corpusCli = Build-CliCorpus $reg
$v1 = Get-CoverageViolations $reg $caseLits
CK ($v1.Count -eq 0) "① 覆盖锁双向差集为空（$(if ($v1.Count) { $v1 -join ' | ' } else { 'CLI 具名选项全覆盖，无幽灵名' })）"
$v2 = Get-TierViolations $reg $corpusCli $corpusUi $corpusProbe $corpusHost
CK ($v2.Count -eq 0) "② 档位不撒谎（$(if ($v2.Count) { $v2 -join ' | ' } else { 'E2E/E2E-UI/static/blind 全部如实' })）"
$v3 = Get-CarrierViolations $reg $slLits
CK ($v3.Count -eq 0) "③ carrier 全部在册（$(if ($v3.Count) { $v3 -join ' | ' } else { '无幽灵门禁' })）"
$v4 = Get-StructureViolations $reg $cliLines $locksSrc
CK ($v4.Count -eq 0) "④ 结构锁（schema/src 见证/locks 对账；$(if ($v4.Count) { $v4 -join ' | ' } else { '全部一致' })）"

# ── 负控（内存变异，零进程）────────────────────────────────────────────────
#   形态对齐 verify-color-out-matrix 的 $ncOk 约定：负控臂「必须红」本身是 PASS；不红 = 判据没牙 = 整体红。
$ncOk = $true
if ($SelfCheck) {
    Write-Output "  [selfcheck] three in-memory mutations must each be caught"
    $ncRed = 0
    $reg2 = $reg | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $firstOpt = @($reg2.entries | Where-Object { $_.kind -eq 'option' -and $_.surface -eq 'named' })[0]
    $reg2.entries = @($reg2.entries | Where-Object { $_.id -ne $firstOpt.id })
    $m1 = Get-CoverageViolations $reg2 $caseLits
    if ($m1.Count -ge 1) { $ncRed++ } else { Write-Output "    (negctl) M1 删条目未被①抓红 => 覆盖锁没牙!" }
    $reg3 = $reg | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $e3 = $reg3.entries | Where-Object { $_.id -eq 'cli.compose-gainmap' }
    $e3.carriers = @(@{ gate = 'verify-ps-compat.ps1'; kind = 'cli' })   # 在册但绝不含 --compose-gainmap 字面量
    $cc3 = Build-CliCorpus $reg3   # 负控轮语料 = lib（逐条目构建在 Get-EntryCliCorpus 内完成）
    $m2 = Get-TierViolations $reg3 $cc3 $corpusUi $corpusProbe $corpusHost
    $m2hit = @($m2 | Where-Object { $_ -like 'cli.compose-gainmap*' })
    if ($m2hit.Count -ge 1) { $ncRed++ } else { Write-Output "    (negctl) M2 carrier 换到无字面量门禁未被②抓红 => 档位针没牙!" }
    $reg4 = $reg | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $e4 = $reg4.entries | Where-Object { $_.id -eq 'cli.format' }
    $e4.carriers = @(@{ gate = 'verify-not-a-registered-gate.ps1'; kind = 'cli' })
    $m3 = Get-CarrierViolations $reg4 $slLits
    $m3hit = @($m3 | Where-Object { $_ -like 'cli.format*' })
    if ($m3hit.Count -ge 1) { $ncRed++ } else { Write-Output "    (negctl) M3 幽灵 carrier 未被③抓红 => 在册锁没牙!" }
    CK ($ncRed -ge 3) "selfcheck: 3/3 mutations caught ($ncRed) — 判据有牙"
    $ncOk = ($ncRed -ge 3)
}

# ── 汇总（runner 零断言闸契约：PASS=n 形状；fail>0 或负控没牙 ⇒ exit 1）─────────────
Write-Output ""
Write-Output "===== module-registry: PASS=$($script:pass) FAIL=$($script:fail) ====="
if ($script:fail -gt 0 -or -not $ncOk) { exit 1 }
exit 0
