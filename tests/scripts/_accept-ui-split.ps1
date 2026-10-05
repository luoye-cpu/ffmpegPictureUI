# ═══════════════════════════════════════════════════════════════════════════════
# 【定位】**工具，不是门禁**（名字以 _ 开头 = 手工按需跑；门禁是 verify-* 前缀）。
#   正式门禁见 verify-ui-split-verbatim.ps1（第 70 条）与 verify-ui-split-union.ps1（第 71 条）。
#   本脚本保留的价值：让物理拆分这件事**可复现、可复核**，而不是一次性的手工搬运。
# _accept-ui-split.ps1 —— UI 拆分验收：新组 exe 逐 mode == 旧 UiTestHost 读数
#
# 判据（对 17 个 mode 每个都做）：
#   ① 新 exe 单独跑该 mode 的 **PASS 行集合** == 旧宿主全量跑里该组段的 PASS 行集合
#      （逐行**有序**比较；断言文本一字不差）
#   ② PASS/FAIL 计数一致
#   ③ `--list` 列出该组 mode
#   ④ 未知 mode ⇒ exit 2（fail-closed）
#
# ⚠ 旧宿主读数来自**拆分前**捕获的基线 tests/output/_uihost_baseline.txt
#   （UiTestHost/Program.cs 未被修改，故它是独立参照实现）。
# ═══════════════════════════════════════════════════════════════════════════════

param(
    [string]$Baseline = 'tests/output/_uihost_baseline.txt',
    [int]$TimeoutSec = 180
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

Write-Host "===== accept-ui-split =====" -ForegroundColor Cyan

# ── ⚠ 并发互斥：UI 测试共用全局态（AppSettingsService / 输出目录 / 临时目录），
#    与另一个 UI 宿主实例**同时跑**会互相污染 ⇒ 读数抖动（实测：`ui-param-defects`
#    单独跑 43/0，但在别的 UiTestHost 进程同时在跑时变成 36/1）。
#    本脚本在开始前**显式检查**并拒绝在争用下出结论（fail-closed，不给出被污染的绿）。
$rivals = @(Get-Process -Name 'UiTestHost','Tests.UiCore','Tests.UiMode','Tests.UiColor','Tests.UiSweep','Tests.UiCli' -ErrorAction SilentlyContinue)
if ($rivals.Count -gt 0) {
    Write-Host "FAIL 前置：无其它 UI 宿主在跑（发现：$((($rivals | ForEach-Object { "$($_.ProcessName)#$($_.Id)" }) -join ', '))）" -ForegroundColor Red
    Write-Host "      UI 测试共用全局态，并发会让读数抖动 ⇒ 拒绝在争用下判定。" -ForegroundColor Yellow
    Write-Host "===== accept-ui-split: PASS=0 FAIL=1 =====" -ForegroundColor Cyan
    exit 1
}

if (-not (Test-Path $Baseline)) { CK $false "基线文件存在（$Baseline）"; exit 1 }
$baseLines = Get-Content $Baseline -Encoding utf8

# ── 从基线里切出每组的断言行（归一成 PASS/FAIL <name>）────────────────────────
# ⚠⚠ 不能用 `########## X组:` 当分段依据来归**内容** —— 旧宿主里 `GroupR` 的
#     `########## R组:` 出现在文件前部，而 `GroupO` 的 `########## O组:` 头部位于
#     R 的实现**之后**（R 的定义在 :1247 先于 O 的 :1472，但 R 的**打印头**在
#     :1249、O 的在 :1474 —— 中间还夹着 O 的实现体）。实测该分段法把 O 的 7 条
#     误并进 N（N 桶 46 = 39 + 7）。
# ⇒ 改为**按断言名前缀归属**：`PASS A1 …` → A，`PASS P3 [cjxl] …` → P，依此类推。
#    这对本仓 17 个组的命名空间是**精确**的（A1..A6 / P0..P5 / J1..J16 / K1..K5 /
#    L1a..L1k / M0..M5 / N0..N7 / R1..R9 / E1..E8 / H1..H14 / I1..I6 / C1..C21 /
#    B1..B3 / D1 / F1 / G1 / O1..O3）。
$baseGroups = @{}
$noise = New-Object System.Collections.Generic.List[string]
foreach ($l in $baseLines) {
    # ⚠ 归一成 `PASS <原样断言名>`：**只取判定 + 名字**，名字里的组字母保留原样。
    #   不要用 `$m.Groups[0].Value.Length` 切片（会把组字母吃掉 ⇒ 旧侧 `PASS 1a …`
    #   对上新侧 `PASS L1a …`）；也不要把"归属桶"的字母拼进名字（旧侧会变成
    #   `PASS H1 H11 …` 而新侧是 `PASS B1 H11 …` —— 实测踩过两次）。
    $m = [regex]::Match($l, '^\s*\[(PASS|FAIL)\]\s+(.+?)\s*$')
    if (-not $m.Success) { continue }
    $verdict = $m.Groups[1].Value
    $name = $m.Groups[2].Value
    # 归属桶 = 名字的首字母
    $g = $name.Substring(0, 1)
    if ($g -notmatch '[A-Z]') { continue }
    # ⚠ 例外（**只改桶，不改名字**）：三条 `B…` 断言其实在 H/I 组内
    #   （`B1 H11 …` 在 H11 块尾、`B2 I3 …` 在 I3 块尾、`B3 I9 …` 在 I9 块尾），
    #   名字以 B 开头只因作者把它们编成"任务 B 的第 N 条检查"。
    #   若不特判，B 桶会多 3 条（8 而非 5），H/I 各少 1 条。
    if ($g -eq 'B' -and $name -match '^B1 H11') { $g = 'H' }
    elseif ($g -eq 'B' -and $name -match '^B2 I3') { $g = 'I' }
    elseif ($g -eq 'B' -and $name -match '^B3 I9') { $g = 'I' }
    if (-not $baseGroups.ContainsKey($g)) { $baseGroups[$g] = New-Object System.Collections.Generic.List[string] }
    $baseGroups[$g].Add("$verdict $name")
}
foreach ($k in @($baseGroups.Keys)) { $baseGroups[$k] = @($baseGroups[$k]) }

# ── mode → (工程, exe, 组字母) ────────────────────────────────────────────────
$modes = [ordered]@{
    'ui-format-command'        = @{ proj='ui-core';  exe='Tests.UiCore';  g='A' }
    'ui-parameter-passing'     = @{ proj='ui-core';  exe='Tests.UiCore';  g='B' }
    'ui-panel-visibility'      = @{ proj='ui-core';  exe='Tests.UiCore';  g='C' }
    'ui-encoder-linkage'       = @{ proj='ui-core';  exe='Tests.UiCore';  g='D' }
    'ui-preset-roundtrip'      = @{ proj='ui-core';  exe='Tests.UiCore';  g='N' }
    'ui-simple-mode'           = @{ proj='ui-mode';  exe='Tests.UiMode';  g='E' }
    'ui-background-visibility' = @{ proj='ui-mode';  exe='Tests.UiMode';  g='F' }
    'ui-end-to-end'            = @{ proj='ui-mode';  exe='Tests.UiMode';  g='G' }
    'ui-gainmap-toggle'        = @{ proj='ui-mode';  exe='Tests.UiMode';  g='O' }
    'ui-color'                 = @{ proj='ui-color'; exe='Tests.UiColor'; g='H' }
    'ui-icc'                   = @{ proj='ui-color'; exe='Tests.UiColor'; g='I' }
    'ui-strategy-map'          = @{ proj='ui-color'; exe='Tests.UiColor'; g='K' }
    'ui-encoder-args'          = @{ proj='ui-color'; exe='Tests.UiColor'; g='R' }
    'ui-sweep'                 = @{ proj='ui-sweep'; exe='Tests.UiSweep'; g='J' }
    'ui-param-defects'         = @{ proj='ui-sweep'; exe='Tests.UiSweep'; g='L' }
    'ui-cli-strict'            = @{ proj='ui-cli';   exe='Tests.UiCli';   g='M' }
    'ui-startup-detect'        = @{ proj='ui-cli';   exe='Tests.UiCli';   g='P' }
}

# ── 带硬超时的子进程运行器（%TEMP% 落盘，PS5.1/PS7 双兼容）───────────────────
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('accui_' + [guid]::NewGuid().ToString('N'))
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
    # ⚠ 读输出文件要**重试**：超时击杀后子进程可能仍持有句柄（Windows）
    #   ⇒ 直接 ReadAllText 会抛 "being used by another process"（实测踩过）。
    $txt = ''
    if (Test-Path $outF) {
        for ($k = 0; $k -lt 20; $k++) {
            try { $txt = [System.IO.File]::ReadAllText($outF); break }
            catch { Start-Sleep -Milliseconds 200 }
        }
    }
    return @{ ExitCode = $rc; TimedOut = $timedOut; Text = $txt; OutFile = $outF }
}

function Get-AssertLines([string]$text) {
    # ⚠ 归一成 `PASS <name>` / `FAIL <name>`（**两侧同口径**）。
    #   旧宿主逐行是 `[PASS] name`；若只给新侧加前缀、旧侧不加，就会把
    #   `[PASS] A1 JPEG` 与 `PASS A1 JPEG` 判成不同行（实测踩过：17 个 mode 全报 NO）。
    $r = New-Object System.Collections.Generic.List[string]
    foreach ($l in ($text -split "`r?`n")) {
        if ($l -match '^\s*\[PASS\]\s*(.*)$') { $r.Add('PASS ' + $Matches[1]) }
        elseif ($l -match '^\s*\[FAIL\]\s*(.*)$') { $r.Add('FAIL ' + $Matches[1]) }
    }
    return ,$r
}

# ══════════════ 表头 ══════════════
Write-Host ""
Write-Host ("{0,-26} {1,-10} {2,-9} {3,-9} {4}" -f 'mode', 'project', 'old', 'new', 'identical?')
Write-Host ("{0,-26} {1,-10} {2,-9} {3,-9} {4}" -f ('-'*26), ('-'*10), ('-'*9), ('-'*9), ('-'*10))

$allIdentical = $true
$results = New-Object System.Collections.Generic.List[object]

foreach ($mode in $modes.Keys) {
    $m = $modes[$mode]
    $exe = Join-Path $root "tests/$($m.proj)/bin/Release/net11.0/win-x64/$($m.exe).exe"
    if (-not (Test-Path $exe)) { CK $false "$mode : exe 存在（$exe）"; continue }

    $r = Invoke-Exe $exe @($srcDir, $mode) ($TimeoutSec * 1000)
    $newLines = Get-AssertLines $r.Text
    $oldLines = @($baseGroups[$m.g])

    # 逐行有序比较
    $same = ($newLines.Count -eq $oldLines.Count)
    if ($same) {
        for ($i = 0; $i -lt $oldLines.Count; $i++) {
            if ($newLines[$i] -cne $oldLines[$i]) { $same = $false; break }
        }
    }

    $oldPass = @($oldLines | Where-Object { $_ -like 'PASS *' }).Count
    $newPass = @($newLines | Where-Object { $_ -like 'PASS *' }).Count
    $oldFail = @($oldLines | Where-Object { $_ -like 'FAIL *' }).Count
    $newFail = @($newLines | Where-Object { $_ -like 'FAIL *' }).Count

    Write-Host ("{0,-26} {1,-10} {2,-9} {3,-9} {4}" -f $mode, $m.proj,
        "$oldPass/$oldFail", "$newPass/$newFail", $(if ($same) { 'YES' } else { 'NO' })) `
        -ForegroundColor $(if ($same) { 'Green' } else { 'Red' })

    $results.Add([pscustomobject]@{ Mode=$mode; Proj=$m.proj; Group=$m.g;
        OldPass=$oldPass; OldFail=$oldFail; NewPass=$newPass; NewFail=$newFail; Identical=$same })

    if (-not $same) {
        $allIdentical = $false
        Write-Host "  --- 首个差异 ---" -ForegroundColor Yellow
        $n = [Math]::Max($oldLines.Count, $newLines.Count)
        $shown = 0
        for ($i = 0; $i -lt $n -and $shown -lt 5; $i++) {
            $o = if ($i -lt $oldLines.Count) { $oldLines[$i] } else { '<none>' }
            $nw = if ($i -lt $newLines.Count) { $newLines[$i] } else { '<none>' }
            if ($o -cne $nw) { Write-Host "    [$i] old=[$o]"; Write-Host "        new=[$nw]"; $shown++ }
        }
    }
    if ($r.TimedOut) { Write-Host "    ⚠ 超时 ${TimeoutSec}s" -ForegroundColor Red; $allIdentical = $false }
}

Write-Host ""

# ══════════════ 判据 ══════════════
CK $allIdentical "① 17 个 mode 的断言行与旧宿主逐行一致（有序）"

# ⚠ 基线口径修正（实测）：按断言名精确归属后，各组真值是
#     A=7 B=5 C=19 D=1 E=30 F=2 G=1 H=15 I=12 J=96 K=27 L=43 M=58 N=39 O=7 P=12 R=35
#   合计 409。⚠ 与"按 `#### X组` 分段"得到的 N=46 / O=8 **不同**：那种分段把 O 的 7 条
#   误并进 N（O 的打印头位于 R 实现之后），且把 O 的守卫断言 `O1 HdrModeCombo 存在`
#   算成了一条（它只在控件为 null 时才触发，本机不触发）⇒ 真值应为 7。
$sumOld = ($results | Measure-Object -Property OldPass -Sum).Sum
$sumNew = ($results | Measure-Object -Property NewPass -Sum).Sum
CK ($sumNew -eq $sumOld) "② 断言总数一致（new=$sumNew old=$sumOld）"
CK ($sumNew -eq 409) "②b 断言总数 == 旧宿主全量 409（实 $sumNew）"
$sumFail = ($results | Measure-Object -Property NewFail -Sum).Sum
CK ($sumFail -eq 0) "②c 新组无 FAIL（实 $sumFail）"

# ══════════════ ③ --list ④ 未知 mode ⇒ exit 2 ══════════════
Write-Host ""
$expectList = @{
    'ui-core'  = @('ui-format-command','ui-parameter-passing','ui-panel-visibility','ui-encoder-linkage','ui-preset-roundtrip')
    'ui-mode'  = @('ui-simple-mode','ui-background-visibility','ui-end-to-end','ui-gainmap-toggle')
    'ui-color' = @('ui-color','ui-icc','ui-strategy-map','ui-encoder-args')
    'ui-sweep' = @('ui-sweep','ui-param-defects')
    'ui-cli'   = @('ui-cli-strict','ui-startup-detect')
}
$exeOf = @{ 'ui-core'='Tests.UiCore'; 'ui-mode'='Tests.UiMode'; 'ui-color'='Tests.UiColor'; 'ui-sweep'='Tests.UiSweep'; 'ui-cli'='Tests.UiCli' }
foreach ($p in $expectList.Keys) {
    $exe = Join-Path $root "tests/$p/bin/Release/net11.0/win-x64/$($exeOf[$p]).exe"
    $r = Invoke-Exe $exe @('--list') 60000
    # ⚠ 过滤掉宿主的前置信息行（`PLAN dir = …`）：它不是 mode 列表的一部分。
    $got = @(($r.Text -split "`r?`n") | ForEach-Object { $_.Trim() } |
             Where-Object { $_ -ne '' -and $_ -notmatch '^PLAN dir =' })
    $ok = ($r.ExitCode -eq 0) -and ($got.Count -eq $expectList[$p].Count)
    if ($ok) {
        for ($i = 0; $i -lt $got.Count; $i++) { if ($got[$i].Trim() -cne $expectList[$p][$i]) { $ok = $false } }
    }
    CK $ok "③ $p --list 逐项正确（exit=$($r.ExitCode)，$($got.Count) 项：$($got -join ', ')）"

    $r2 = Invoke-Exe $exe @('nosuchmode') 60000
    CK ($r2.ExitCode -eq 2) "④ $p nosuchmode ⇒ exit 2（实 $($r2.ExitCode)）"
}

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "===== accept-ui-split: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
