# verify-module-registry.ps1 — 机读模块账本门禁（批 1，docs/TEST_MODULE_PLAN_2026-11-12.md §2 批 1）
# 账本 = tests/scripts/_module-registry.json（能力域→承载门禁→判据档位→断言数锁 的单一真值）。
#
# 判据六组：
#   ① 覆盖锁（不落后针）：CliParser.Parse switch 窗口内提取的每个 case 字面量，必须与账本
#      surface=named 条目的 names+aliases 双向差集为空 —— 新增选项不登记覆盖 = 整轮红；
#      账本里的幽灵名同样红。
#   ② 档位不撒谎针：tier=E2E/E2E-UI 的条目，其选项字面量必须真出现在 carrier 门禁文件或其
#      执行面语料（_lib-*/ServiceProbe 源/ui 宿主源）里；tier=blind 必须 carriers 空且 blindReason
#      非空 —— 档位标错 = 红。
#   ③ carrier 在册锁：每个 carrier.gate 必须在 runner $scriptList 在册 —— 指向幽灵门禁 = 红。
#   ④ 结构锁：schema 枚举/唯一 id/locks 数值与三把尺源内字面量对账（verify-ui-host 458+五件、
#      param-matrix $jExpected=126、union $expectedOldCount=458）/ src 活见证（±3 行窗口命中选项名）。
#   ⑤ C4 超时表锁（2026-11-12 批 5）：locks."script-timeouts".table 的每个键必须在 runner
#      $scriptList 在册（幽灵键=红）、每个值 ≥ 全局默认 900（表只许**抬**不许紧——低于默认 =
#      该门禁在整轮里被表**收紧**了超时 ⇒ 负载下假红面，= 红）、且 runner 里必须存在装载点 +
#      查表点（registry-timeouts 读数与 scriptTimeoutTable.ContainsKey 各恰好 1 处——装载点被删
#      ⇒ 整轮静默退化成全默认，无任何读数可见）。
#   ⑥ TESTING.md 覆盖索引段（2026-11-13 批 5·剩余项②）：`docs/TESTING.md` 的
#      `<!-- module-index:begin/end -->` 段必须与账本**渲染结果逐字节相等**——手工改生成段、或
#      改账本却不重生成 ⇒ 红。**段位置固定在文末（§九）**：`docs/HANDOVER.md:3984` 记载两条门禁
#      按行号引用本文（`_probe-test-group-coverage.ps1` 的 :1115、`_probe-cjk-hardcode-scan.ps1`
#      的 :1828），中间插入会把那些锚点整体挪位 ⇒ 只许追加在文末。
#
# ⚠ 本门禁不跑任何 exe（只读源码/JSON/PS1）⇒ 不进 STALE 闸（runner :717-860 的裁定，2026-11-12 批）。
# 重生成模式：`-EmitIndex`（手工跑；写 docs/TESTING.md 的索引段，写完后照常跑正向判据自证）。
# 负控 -SelfCheck：五个内存变异（删条目/换 carrier 到不含字面量的在册门禁/carrier 指向清单外脚本/
#   删 C4 超时表锁/改账本后不比索引段）各必须被对应判据抓红 —— 形态对齐 verify-color-out-matrix 的
#   $ncOk 约定（门禁自身不因负控转红）。
param([switch]$SelfCheck, [switch]$EmitIndex)
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
function Get-TimeoutTableViolations($reg, [System.Collections.Generic.HashSet[string]]$slSet, [string]$runnerText) {
    # ⑤ C4 per-script 超时表锁（2026-11-12 批 5）
    $bad = @()
    $stLock = $reg.locks.'script-timeouts'
    if ($null -eq $stLock) { $bad += 'locks.script-timeouts 缺失（C4 单一真值被删 ⇒ runner fail-closed 拒跑）'; return ,$bad }
    if ($null -eq $stLock.table -or @($stLock.table.PSObject.Properties).Count -eq 0) { $bad += 'locks.script-timeouts.table 缺失或为空'; return ,$bad }
    foreach ($prop in @($stLock.table.PSObject.Properties)) {
        if (-not $slSet.Contains($prop.Name)) { $bad += "script-timeouts 键 '$($prop.Name)' 不在 runner 清单（幽灵键）" }
        if ([int]$prop.Value -lt 900) { $bad += "script-timeouts '$($prop.Name)=$($prop.Value)' 低于全局默认 900（表只许抬不许紧——低于默认 = 给该门禁开倒车）" }
    }
    # runner 侧装载点 + 查表点必须在（读的是 runner 自己的原文；token 拼接防自满）
    $cOk = ([regex]::Matches($runnerText, [regex]::Escape('registry-' + 'timeouts=OK'))).Count
    $cFail = ([regex]::Matches($runnerText, [regex]::Escape('registry-' + 'timeouts=FAIL'))).Count
    $cLk = ([regex]::Matches($runnerText, 'scriptTimeoutTable\.ContainsKey')).Count
    if ($cOk -ne 1 -or $cFail -ne 1) { $bad += "runner C4 装载点读数 = OK:$cOk FAIL:$cFail（各须 1：装载点被删 ⇒ 表静默失效成全默认）" }
    if ($cLk -ne 1) { $bad += "runner C4 查表点 = $cLk（须 1：查表点被删 ⇒ 表值永不生效）" }
    return ,$bad
}
# ── ⑥ 索引段渲染 + 漂移（2026-11-13 批 5·剩余项②）──────────────────────────────
#   单一真值 = 账本；索引是**生成物**。渲染必须**确定性**（否则漂移闸会随机红）：
#   ① 分组按 `domains` 声明序、组内按 id 序数排序；② carrier 按 gate 名排序；
#   ③ 不用 `Sort-Object` 的当前文化排序（本机 LC_ALL 会影响）⇒ 显式 [StringComparer]::Ordinal。
#   ④ 换行符（2026-11-13）：渲染用**本文档现存的** EOL（CRLF 优先，否则 LF），不硬编码 CRLF ——
#      `core.autocrlf` 因机器而异，硬编码会在 checkout 成 LF 的树上恒红（且「重生成」也治不好）。
$idxBegin = '<!-- module-index:begin -->'
$idxEnd = '<!-- module-index:end -->'
$testingPath = Join-Path $repoRoot 'docs/TESTING.md'
function Get-IndexHead([string]$s, [int]$max) {
    # 取「首句」（第一个全角括号/分号/冒号之前），再按 max 截断 —— 规则写在段头，读者不用猜
    if ([string]::IsNullOrWhiteSpace($s)) { return '' }
    $t = ($s -replace "`r?`n", ' ').Trim()
    foreach ($sep in @('（', '；', '：')) {
        $i = $t.IndexOf($sep)
        if ($i -gt 0) { $t = $t.Substring(0, $i); break }
    }
    if ($t.Length -gt $max) { $t = $t.Substring(0, $max) + '…' }
    return $t
}
function Get-TestingIndexBlock($reg, [string]$Eol = "`r`n") {
    $es = @($reg.entries)
    $kindCnt = @{}; $tierCnt = @{}
    foreach ($e in $es) { $kindCnt[[string]$e.kind] = 1 + [int]$kindCnt[[string]$e.kind] }
    foreach ($e in $es) { $tierCnt[[string]$e.tier] = 1 + [int]$tierCnt[[string]$e.tier] }
    $ord = [System.StringComparer]::Ordinal
    $kNames = [string[]]@($kindCnt.Keys); [Array]::Sort($kNames, $ord)
    $tNames = [string[]]@($tierCnt.Keys); [Array]::Sort($tNames, $ord)
    $kindStr = (($kNames | ForEach-Object { "$_ $($kindCnt[$_])" }) -join ' · ')
    $tierStr = (($tNames | ForEach-Object { "$_ $($tierCnt[$_])" }) -join ' · ')
    $out = New-Object System.Collections.Generic.List[string]
    $out.Add($idxBegin)
    $out.Add('> **机器拥有段（勿手改）**：本段由 `tests/scripts/verify-module-registry.ps1 -EmitIndex` 从')
    $out.Add('> `tests/scripts/_module-registry.json`（能力域 → 承载门禁 → 判据档位 的单一真值）生成；')
    $out.Add('> 手工改动本段 ⇒ 门禁 ⑥ 与账本渲染结果逐字节对撞判红（改账本就重跑 `-EmitIndex`）。')
    $out.Add(('> 现量 **{0} 条**（{1}；{2}）。' -f $es.Count, $kindStr, $tierStr))
    $out.Add('> 读法：`承载` = carrier 门禁（括号内为 carrier 形态 cli/ui/host）；`说明` = capability 的 desc 首句（≤60 字，全文见账本）；blind 条目给理由全文；`—` = 无。')
    foreach ($dom in @($reg.domains)) {
        $grp = [object[]]@($es | Where-Object { $_.domain -eq $dom })
        if ($grp.Count -eq 0) { continue }
        if ($grp.Count -gt 1) {
            # 序数排序：① 不用 Sort-Object（走当前文化，'-' 之类的权重随区域设置变 ⇒ 生成物不稳定）；
            #   ② 也不用 [Array]::Sort(keys, items, cmp) —— 2026-11-13 实测 **keys 排了、items 没动**
            #   （object[] 装 PSObject 时静默不同步）⇒ 用**索引表**再按索引取值（int[] 实测同步）。
            $keys = [string[]]@($grp | ForEach-Object { [string]$_.id })
            $ordIdx = [int[]](0..($grp.Count - 1))
            [Array]::Sort($keys, $ordIdx, [System.Collections.IComparer]$ord)
            $grp = @(foreach ($i in $ordIdx) { $grp[$i] })
        }
        $out.Add('')
        $out.Add("### $dom（$($grp.Count) 条）")
        foreach ($e in $grp) {
            $gateStr = '—'
            $cs = @($e.carriers)
            if ($cs.Count -gt 0) {
                $names = [string[]]@($cs | ForEach-Object { [string]$_.gate })
                [Array]::Sort($names, $ord)
                $parts = foreach ($gn in $names) {
                    $k = @($cs | Where-Object { $_.gate -eq $gn })[0].kind
                    ('`{0}`（{1}）' -f $gn, $k)
                }
                $gateStr = ($parts -join ' / ')
            }
            $tail = ''
            if ($e.kind -eq 'option') {
                $nm = @(@($e.names) + @($e.aliases)) | Where-Object { $_ }
                if (@($nm).Count -gt 0) { $tail = ' · 名 ' + ((@($nm) | ForEach-Object { ('`{0}`' -f $_) }) -join ' / ') }
            } elseif ($e.kind -eq 'blindspot') {
                $r = ([string]$e.blindReason) -replace "`r?`n", ' '
                $tail = ' · 理由：' + $r
            } else {
                $d = Get-IndexHead ([string]$e.desc) 60
                if ($d) { $tail = ' · 说明：' + $d }
            }
            $out.Add(('- `{0}` — {1} · {2} · 承载 {3}{4}' -f $e.id, $e.kind, $e.tier, $gateStr, $tail))
        }
    }
    $out.Add('')
    $out.Add($idxEnd)
    return ($out -join $Eol)
}
function Get-IndexDrift([string]$testingText, [string]$genBlock) {
    # ⑥ 漂移：抽取现存段（含两个标记行）与生成结果做**大小写敏感**逐字节比较
    $bad = @()
    if ([string]::IsNullOrEmpty($testingText)) { return ,@('docs/TESTING.md 读不到或为空') }
    $i0 = $testingText.IndexOf($idxBegin)
    if ($i0 -lt 0) { return ,@('docs/TESTING.md 缺 `<!-- module-index:begin -->`（索引段被删 ⇒ 跑 -EmitIndex 重生成）') }
    $i1 = $testingText.IndexOf($idxEnd, $i0)
    if ($i1 -lt 0) { return ,@('docs/TESTING.md 索引段缺 `<!-- module-index:end -->` 结束标记') }
    $actual = $testingText.Substring($i0, $i1 - $i0 + $idxEnd.Length)
    if ($actual -cne $genBlock) {
        # 只说"不一致"等于让人手工 diff 7000 行 ⇒ 必须给段内首个差异行
        $a = $actual -split "`r?`n"; $g = $genBlock -split "`r?`n"
        $n = [Math]::Min($a.Count, $g.Count); $ln = -1
        for ($i = 0; $i -lt $n; $i++) { if ($a[$i] -cne $g[$i]) { $ln = $i; break } }
        if ($ln -lt 0) { $ln = $n }
        $av = if ($ln -lt $a.Count) { $a[$ln] } else { '<段尾>' }
        $gv = if ($ln -lt $g.Count) { $g[$ln] } else { '<段尾>' }
        $bad += ('索引段与账本渲染不一致（段内第 {0} 行）：现存[{1}] vs 生成[{2}] —— 手工改生成段 = 红；改账本就跑 -EmitIndex' -f ($ln + 1), $av, $gv)
    }
    return ,$bad
}

# ── 语料与提取 ────────────────────────────────────────────────────────────────
$reg = Get-Content $regPath -Raw -Encoding UTF8 | ConvertFrom-Json
CK ($reg.version -ge 1) "registry loaded (version=$($reg.version), entries=$(@($reg.entries).Count))"

# ⑥ 重生成模式（手工跑；不进门禁清单）。写盘 = 只碰两个标记之间的段，其余字节原样保留；
#   缺段则**追加在文末**（§九）—— 中间插入会让按行号引用本文的两条门禁锚点失效。
# EOL 自适应（2026-11-13）：渲染一律用**本文档现存的换行风格**（本机 = CRLF；但 `core.autocrlf` 的
#   配置因机器而异，checkout 成 LF 的树若硬按 CRLF 渲染 ⇒ 漂移闸在别人机器上恒红且「重生成」也治不好。
#   做法：文内出现 CRLF ⇒ 用 CRLF；否则 LF。生成物与文件同风格 ⇒ 闸在哪台机器上都可比。
$testingText0 = [System.IO.File]::ReadAllText($testingPath)
$idxEol = if ($testingText0.Contains("`r`n")) { "`r`n" } else { "`n" }
$idxGen = Get-TestingIndexBlock $reg $idxEol
if ($EmitIndex) {
    $t0 = $testingText0
    $j0 = $t0.IndexOf($idxBegin)
    if ($j0 -ge 0) {
        $j1 = $t0.IndexOf($idxEnd, $j0)
        if ($j1 -lt 0) { Write-Output '  FAIL  索引段缺 `<!-- module-index:end -->` ⇒ 拒绝写（先手工补结束标记）'; exit 1 }
        $t1 = $t0.Substring(0, $j0) + $idxGen + $t0.Substring($j1 + $idxEnd.Length)
    } else {
        $hdr = "---$idxEol$idxEol## 九、模块覆盖索引（2026-11-13，机器生成；手工改本段 ⇒ 门禁 ⑥ 判红）$idxEol$idxEol"
        $tail = if ($t0.EndsWith($idxEol)) { $idxEol } else { $idxEol + $idxEol }
        $t1 = $t0 + $tail + $hdr + $idxGen + $idxEol
    }
    [System.IO.File]::WriteAllText($testingPath, $t1, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output ('index-emit=OK（{0} 条 → docs/TESTING.md 共 {1} B）' -f @($reg.entries).Count, (Get-Item $testingPath).Length)
}
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

# ── 正向六组 ─────────────────────────────────────────────────────────────────
$corpusCli = Build-CliCorpus $reg
$v1 = Get-CoverageViolations $reg $caseLits
CK ($v1.Count -eq 0) "① 覆盖锁双向差集为空（$(if ($v1.Count) { $v1 -join ' | ' } else { 'CLI 具名选项全覆盖，无幽灵名' })）"
$v2 = Get-TierViolations $reg $corpusCli $corpusUi $corpusProbe $corpusHost
CK ($v2.Count -eq 0) "② 档位不撒谎（$(if ($v2.Count) { $v2 -join ' | ' } else { 'E2E/E2E-UI/static/blind 全部如实' })）"
$v3 = Get-CarrierViolations $reg $slLits
CK ($v3.Count -eq 0) "③ carrier 全部在册（$(if ($v3.Count) { $v3 -join ' | ' } else { '无幽灵门禁' })）"
$v4 = Get-StructureViolations $reg $cliLines $locksSrc
CK ($v4.Count -eq 0) "④ 结构锁（schema/src 见证/locks 对账；$(if ($v4.Count) { $v4 -join ' | ' } else { '全部一致' })）"
$runnerText = [IO.File]::ReadAllText($runnerPath)
$v5 = Get-TimeoutTableViolations $reg $slLits $runnerText
CK ($v5.Count -eq 0) "⑤ C4 超时表锁（键在册/只许抬/装载+查表点在位；$(if ($v5.Count) { $v5 -join ' | ' } else { "$(@($reg.locks.'script-timeouts'.table.PSObject.Properties).Count) 条：$((@($reg.locks.'script-timeouts'.table.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" })) -join ', ')" })）"
$testingText = [IO.File]::ReadAllText($testingPath)
$v6 = Get-IndexDrift $testingText $idxGen
CK ($v6.Count -eq 0) "⑥ TESTING.md 索引段与账本一致（$(if ($v6.Count) { $v6 -join ' | ' } else { "段 $($idxGen.Length) B；$((@($idxGen -split "`r?`n")).Count) 行；$(@($reg.entries).Count) 条" })）"

# ── 负控（内存变异，零进程）────────────────────────────────────────────────
#   形态对齐 verify-color-out-matrix 的 $ncOk 约定：负控臂「必须红」本身是 PASS；不红 = 判据没牙 = 整体红。
$ncOk = $true
if ($SelfCheck) {
    Write-Output "  [selfcheck] five in-memory mutations must each be caught"
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
    $reg5 = $reg | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $reg5.locks.PSObject.Properties.Remove('script-timeouts')          # M4：C4 键整块删除
    $m4 = Get-TimeoutTableViolations $reg5 $slLits $runnerText
    $m4hit = @($m4 | Where-Object { $_ -like '*script-timeouts 缺失*' })
    if ($m4hit.Count -ge 1) { $ncRed++ } else { Write-Output "    (negctl) M4 删 C4 键未被⑤抓红 => 超时表锁没牙!" }
    # M5：改账本却不重生成索引段（= 漂移闸存在的全部理由）。变异选「换域」而不是改 desc：
    #   域变了 ⇒ 分组行与条目所在组都变 ⇒ 渲染结果必变，且不依赖条目文案的现状。
    $reg6 = $reg | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $mv = @($reg6.entries | Where-Object { $_.domain -ne 'matrix' })[0]
    $mv.domain = 'matrix'
    $m5 = Get-IndexDrift $testingText (Get-TestingIndexBlock $reg6)
    if (@($m5).Count -ge 1) { $ncRed++ } else { Write-Output "    (negctl) M5 账本漂移未被⑥抓红 => 索引漂移闸没牙!" }
    CK ($ncRed -ge 5) "selfcheck: 5/5 mutations caught ($ncRed) — 判据有牙"
    $ncOk = ($ncRed -ge 5)
}

# ── 汇总（runner 零断言闸契约：PASS=n 形状；fail>0 或负控没牙 ⇒ exit 1）─────────────
Write-Output ""
Write-Output "===== module-registry: PASS=$($script:pass) FAIL=$($script:fail) ====="
if ($script:fail -gt 0 -or -not $ncOk) { exit 1 }
exit 0
