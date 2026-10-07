# ═══════════════════════════════════════════════════════════════════════════════
# _accept-ui-split-union.ps1 —— UI 拆分验收（**并集判据**，权威口径）
#
# 【为什么用并集而不是"每组各对一次"】
#   5 个工程的 mode 划分**不是**断言名的划分：个别断言跑在别的组的代码块里
#   （例：`B1 H11 …`/`B2 I3 …`/`B3 I9 …` 是"任务 B"编号的 TempDir 断言，
#   却写在 H/I 组的块尾）。⇒ 逐工程按组字母算期望值必然对不上，
#   而**唯一正确的判据**是：
#       ① 旧宿主全量的断言名集合  ==  5 个工程各 mode 输出断言名的**并集**
#       ② 差集两个方向都必须为空（missing = 0 且 extra = 0）
#       ③ 并集计数 == 旧宿主汇总行里的 409
#
# 【步骤】
#   1. 跑一次旧 UiTestHost（未修改，仍在仓里）→ 取它的断言名集合 + 汇总行
#   2. 逐个 mode 跑 5 个工程 → 并集
#   3. 比对 + 报 diff
#
# ⚠ 并发互斥：UI 测试共用全局态，与别的 UI 宿主实例同时跑会抖动 ⇒ 开头显式拒绝。
# ═══════════════════════════════════════════════════════════════════════════════

param(
    [int]$TimeoutSec = 240,
    [switch]$SkipOldHost
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$env:FFMPEGGUI_PLAN_DIR = (Join-Path $root 'publish/PLAN')
$srcDir = Join-Path $root 'tests/output/sources'

$pass = 0; $fail = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "PASS $msg" -ForegroundColor Green; $script:pass++ }
    else     { Write-Host "FAIL $msg" -ForegroundColor Red;   $script:fail++ }
}

Write-Host "===== accept-ui-split-union =====" -ForegroundColor Cyan

# ── 并发互斥（fail-closed，不给被污染的绿）────────────────────────────────────
# ⚠ 先**等**一小段（前一次跑剩的实例/被超时击杀的孤儿可能正在退出），
#   再判定。若仍在跑则拒绝出结论 —— UI 测试共用全局态，并发读数会抖动。
$rivalNames = @('UiTestHost','Tests.UiCore','Tests.UiMode','Tests.UiColor','Tests.UiSweep','Tests.UiCli')
$rivals = @()
for ($try = 0; $try -lt 12; $try++) {
    $rivals = @(Get-Process -Name $rivalNames -ErrorAction SilentlyContinue)
    if ($rivals.Count -eq 0) { break }
    if ($try -eq 0) { Write-Host "waiting for prior UI hosts to exit: $((($rivals | ForEach-Object { "$($_.ProcessName)#$($_.Id)" }) -join ', '))" }
    Start-Sleep -Seconds 5
}
if ($rivals.Count -gt 0) {
    Write-Host "FAIL 前置：无其它 UI 宿主在跑（发现：$((($rivals | ForEach-Object { "$($_.ProcessName)#$($_.Id)" }) -join ', '))）" -ForegroundColor Red
    Write-Host "      UI 测试共用全局态，并发会让读数抖动 ⇒ 拒绝在争用下判定。" -ForegroundColor Yellow
    Write-Host "===== ui-split-union: PASS=0 FAIL=1 =====" -ForegroundColor Cyan
    exit 1
}
CK $true "0 前置：仓库安静（无其它 UI 宿主在跑）"

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('accuni_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

function Invoke-Exe([string]$exePath, [string[]]$exeArgs, [int]$timeoutMs) {
    $tag  = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $outF = Join-Path $work "o_$tag.txt"
    $errF = Join-Path $work "e_$tag.txt"
    $rcF  = Join-Path $work "r_$tag.txt"
    $q = @(); foreach ($a in $exeArgs) { $q += ("'" + ($a -replace "'", "''") + "'") }
    $inner = "& '" + ($exePath -replace "'", "''") + "' " + ($q -join ' ') +
             "; [System.IO.File]::WriteAllText('" + ($rcF -replace "'", "''") + "', [string]`$LASTEXITCODE)"
    $p = Start-Process -FilePath ((Get-Process -Id $PID).Path) -ArgumentList @('-NoProfile', '-Command', $inner) `
            -NoNewWindow -PassThru -RedirectStandardOutput $outF -RedirectStandardError $errF
    $finished = $false
    try { $finished = $p.WaitForExit($timeoutMs) } catch { }
    $timedOut = $false
    if (-not $finished) {
        $timedOut = $true
        try { & taskkill.exe /PID $p.Id /T /F 2>$null | Out-Null } catch { }
        try { $null = $p.WaitForExit(5000) } catch { }
        if (-not $p.HasExited) { try { $p.Kill() } catch { } }
    }
    $rc = -1
    if (Test-Path $rcF) { try { $rc = [int]([System.IO.File]::ReadAllText($rcF).Trim()) } catch { $rc = -1 } }
    # ⚠ 读输出要重试（超时击杀后句柄可能未释放）
    $txt = ''
    if (Test-Path $outF) {
        for ($k = 0; $k -lt 20; $k++) {
            try { $txt = [System.IO.File]::ReadAllText($outF); break } catch { Start-Sleep -Milliseconds 200 }
        }
    }
    return @{ ExitCode = $rc; TimedOut = $timedOut; Text = $txt }
}

# 从输出里取**断言名集合**（`[PASS] name` / `[FAIL] name :: detail` ⇒ 只留 name）
function Get-Names([string]$text) {
    $s = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($l in ($text -split "`r?`n")) {
        $m = [regex]::Match($l, '^\s*\[(PASS|FAIL)\]\s+(.*?)(?:\s+::\s+.*)?$')
        if ($m.Success) { [void]$s.Add($m.Groups[2].Value.Trim()) }
    }
    return $s
}

# ══════════════ 1) 旧宿主（参照实现，未被修改）══════════════
$oldNames = New-Object 'System.Collections.Generic.HashSet[string]'
$oldSummary = ''
$oldFail = -1
if (-not $SkipOldHost) {
    $oldExe = Join-Path $root 'tests/UiTestHost/bin/Release/net11.0/win-x64/UiTestHost.exe'
    if (-not (Test-Path $oldExe)) { CK $false "旧宿主 exe 存在（$oldExe）"; exit 1 }
    Write-Host "run: UiTestHost (reference implementation) ..."
    $r = Invoke-Exe $oldExe @($srcDir) ($TimeoutSec * 1000)
    $oldNames = Get-Names $r.Text
    $oldSummary = (@(($r.Text -split "`r?`n") | Where-Object { $_ -match 'PASS / \d+ FAIL' })) -join ''
    $mf = [regex]::Match($r.Text, '(\d+) PASS / (\d+) FAIL')
    if ($mf.Success) { $oldFail = [int]$mf.Groups[2].Value }
    Write-Host "  old host: $oldSummary ; names=$($oldNames.Count) ; exit=$($r.ExitCode)"
    # ⚠ 2026-10-07：旧宿主**未改**，故它的断言名数仍是 409（含那条恒假红的 J1z）。
    #   这是"参照实现"的基线，**不随拆分侧改动而变** —— 见下方 §3 的改名白名单。
    CK ($oldNames.Count -eq 409) "1a 旧宿主断言名数 == 409（实 $($oldNames.Count)）"
    CK ($oldFail -eq 0) "1b 旧宿主 FAIL == 0（实 $oldFail）"
} else {
    Write-Host "run: UiTestHost SKIPPED (-SkipOldHost)" -ForegroundColor Yellow
}

# ══════════════ 2) 5 个工程的并集 ══════════════
$modes = [ordered]@{
    'ui-core'  = @{ exe='Tests.UiCore';  modes=@('ui-format-command','ui-parameter-passing','ui-panel-visibility','ui-encoder-linkage','ui-preset-roundtrip') }
    'ui-mode'  = @{ exe='Tests.UiMode';  modes=@('ui-simple-mode','ui-background-visibility','ui-end-to-end','ui-gainmap-toggle') }
    'ui-color' = @{ exe='Tests.UiColor'; modes=@('ui-color','ui-icc','ui-strategy-map','ui-encoder-args') }
    'ui-sweep' = @{ exe='Tests.UiSweep'; modes=@('ui-sweep','ui-param-defects') }
    'ui-cli'   = @{ exe='Tests.UiCli';   modes=@('ui-cli-strict','ui-startup-detect') }
}

$newNames = New-Object 'System.Collections.Generic.HashSet[string]'
$perMode = [ordered]@{}
Write-Host ""
Write-Host ("{0,-26} {1,-9} {2,-9} {3}" -f 'mode','project','PASS','FAIL')
Write-Host ("{0,-26} {1,-9} {2,-9} {3}" -f ('-'*26),('-'*9),('-'*9),('-'*4))

foreach ($proj in $modes.Keys) {
    $exe = Join-Path $root "tests/$proj/bin/Release/net11.0/win-x64/$($modes[$proj].exe).exe"
    if (-not (Test-Path $exe)) { CK $false "$proj exe 存在（$exe）"; continue }
    foreach ($mode in $modes[$proj].modes) {
        $r = Invoke-Exe $exe @($srcDir, $mode) ($TimeoutSec * 1000)
        $ns = Get-Names $r.Text
        foreach ($n in $ns) { [void]$newNames.Add($n) }
        $np = @(($r.Text -split "`r?`n") | Where-Object { $_ -match '^\s*\[PASS\]' }).Count
        $nf = @(($r.Text -split "`r?`n") | Where-Object { $_ -match '^\s*\[FAIL\]' }).Count
        $perMode[$mode] = @{ Pass=$np; Fail=$nf; Timeout=$r.TimedOut }
        Write-Host ("{0,-26} {1,-9} {2,-9} {3}" -f $mode, $proj, $np, $nf) `
            -ForegroundColor $(if ($nf -eq 0 -and -not $r.TimedOut) { 'Green' } else { 'Red' })
        if ($nf -gt 0) {
            foreach ($fl in @(($r.Text -split "`r?`n") | Where-Object { $_ -match '^\s*\[FAIL\]' })) {
                Write-Host "      $fl" -ForegroundColor Red
            }
        }
    }
}

Write-Host ""
Write-Host "union of new projects = $($newNames.Count) names"
$sumPerModePass = ($perMode.Values | Measure-Object -Property Pass -Sum).Sum
Write-Host "sum of per-mode PASS lines = $sumPerModePass (⚠ 会 > 并集：个别断言行跨 mode 重复出现)"
Write-Host ""

# ══════════════ 3) 判据 ══════════════
$newFailTotal = ($perMode.Values | Measure-Object -Property Fail -Sum).Sum
CK ($newFailTotal -eq 0) "2a 5 个工程全部 mode 无 FAIL（实 $newFailTotal）"

# ── 2026-10-07：一处**有意**的断言**改名 + 语义修正**（必须在账上，不能靠放宽判据消化）──
#   `J1z` 原本是 `Check("J1z 硬件编码器可选（前置）", hw != null, "no hardware encoder item
#   in this environment")` —— 把**环境前置**写成了硬 `FAIL`（失败文案自己就写着
#   "in this environment"）⇒ 环境不满足时**恒定假红**，并把判据 ② 一起带红。
#   现改为：
#     · 无硬件项 ⇒ `Skip(...)`（点名、计 skip、不判红）；
#     · 有硬件项 ⇒ 断言**不变量**：`-c:v` 是单个 token 且不含 `⚡`/空格/`—`
#       （这正是 J1z 当初登记的缺陷形态：图标前缀污染 `-c:v`）。
#   ⚠ 第一版硬编码 `== "av1_nvenc"` ⇒ 在**本机当场红**（实得 `av1_amf`）——
#     本机 GPU 检测先命中 amd 项，而 `av1_amf` 同样是合法 token ⇒
#     **锁了"某台机器的取值"而不是"不变量"**（本仓 §6 第 116 条同族）。已改为形态判据。
#   ⇒ 对集合的影响是**一对一替换**（旧名出、新名入）⇒ **总数不变（仍 409）**。
#   ⚠ 这**不是**放宽：`missing` 只白名单这一条旧名、`extra` 只白名单这一条新名，
#     两侧各自**恰好命中 1 条**（`3a2`/`3b2` 两个反控），其余任何差集照常红。
#   ⚠ 断言**数量**不变（旧 1 条 → 新 1 条，Skip 与 PASS 互斥）⇒ union 计数守恒。
$renamedPairs = @(
    @{ Old = 'J1z 硬件编码器可选（前置）'; New = 'J1z 硬件编码器名解析（`-c:v` 为单个 token、不含图标/空格）' }
)
$expectedOldCount = 409
# 一对一替换 ⇒ 新旧总数相同（不是 408）
$expectedNewCount = $expectedOldCount

if (-not $SkipOldHost) {
    $renamedOld = @($renamedPairs | ForEach-Object { $_.Old })
    $renamedNew = @($renamedPairs | ForEach-Object { $_.New })
    $missing = @($oldNames | Where-Object { -not $newNames.Contains($_) -and $_ -notin $renamedOld } | Sort-Object)
    $extra   = @($newNames | Where-Object { -not $oldNames.Contains($_) -and $_ -notin $renamedNew } | Sort-Object)
    $waivedOld = @($oldNames | Where-Object { -not $newNames.Contains($_) -and $_ -in $renamedOld } | Sort-Object)
    $waivedNew = @($newNames | Where-Object { -not $oldNames.Contains($_) -and $_ -in $renamedNew } | Sort-Object)
    CK ($missing.Count -eq 0) "3a missing（旧有新无、且**不在**已登记改名白名单内）== 0（实 $($missing.Count)）$(if ($missing.Count) { '：' + ($missing -join ' ⧉ ') })"
    CK ($extra.Count -eq 0)   "3b extra（新有旧无、且**不在**已登记改名白名单内）== 0（实 $($extra.Count)）$(if ($extra.Count) { '：' + ($extra -join ' ⧉ ') })"
    CK ($waivedOld.Count -eq $renamedPairs.Count) "3a2 白名单的**旧名**恰好命中 $($renamedPairs.Count) 条（实 $($waivedOld.Count)）—— 防白名单过期后变成""什么都放过"""
    CK ($waivedNew.Count -eq $renamedPairs.Count) "3b2 白名单的**新名**恰好命中 $($renamedPairs.Count) 条（实 $($waivedNew.Count)）—— 防""只删不加""的静默丢断言"
    CK ($newNames.Count -eq $oldNames.Count) "3c 并集计数 == 旧宿主计数（一对一改名 ⇒ 总数守恒）（new=$($newNames.Count) old=$($oldNames.Count)）"
    CK ($newNames.Count -eq $expectedNewCount) "3d 并集计数 == $expectedNewCount（实 $($newNames.Count)）"

    # 反控：判据段必须真的读到非空集合
    CK ($oldNames.Count -gt 0 -and $newNames.Count -gt 0) "4  反控：两侧集合都非空（防路径错导致空比全绿）"

    if ($missing.Count -or $extra.Count) {
        Write-Host ""
        if ($missing.Count) { Write-Host "--- missing ---" -ForegroundColor Yellow; $missing | ForEach-Object { Write-Host "  $_" } }
        if ($extra.Count)   { Write-Host "--- extra ---"   -ForegroundColor Yellow; $extra   | ForEach-Object { Write-Host "  $_" } }
    }
    if ($waivedOld.Count -or $waivedNew.Count) {
        Write-Host ""
        Write-Host "--- 已登记改名（有意差异，非缺陷）---" -ForegroundColor DarkGray
        $waivedOld | ForEach-Object { Write-Host "  (旧) $_" -ForegroundColor DarkGray }
        $waivedNew | ForEach-Object { Write-Host "  (新) $_" -ForegroundColor DarkGray }
    }
}

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "===== ui-split-union: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
