# verify-cli-strict.ps1 -- P1-E 门禁: CLI 取值口径(D4/D5/D6) + 宿主临时目录卫生(任务B)
#                        + 动图参数粘性(任务C)
#
# 为什么需要这一条(而不是复用 verify-ui-param-defects.ps1):
#   P1-E 的三项结论 + 任务 B/C 的回归锁全部落在 UiTestHost 的 GroupM_CliStrict 与
#   B1/B2/B3 三个当场断言上。既有三条 UI 门禁只逐条要求 J/K/L 组的锚点在册 =>
#   它们对「GroupM 被整段删掉 / 改名 / 注释掉」以及「任务B/C 的锁被删掉」是**盲的**
#   (那时 FAIL=0 仍然成立, 只是断言数变少)。本脚本就是这些锚点的门禁。
#
# 本门禁锁住的四件事:
#   1. 前置环境: 显式设 FFMPEGGUI_PLAN_DIR(按仓库根推导, 不写死绝对路径)。
#      裸跑会挂死 >13 min 不收敛(宿主侧已 fail-fast, 这里再从外部兜一层)。
#   2. 硬超时(默认 180 s)。Start-Process -Wait 没有 timeout 参数 => 子进程挂死会
#      静默拖死整轮门禁。本仓硬约束 §6: 长跑一律前台发起。
#   3. 判据 = 退出码 == 0 且 汇总行存在 且 FAIL=0 且 锚点在册(缺一不可)。
#   4. **任务B 的外部验收**: 宿主跑完后 tests/output/sources/ 里**不得**出现任何 _*.png。
#      这一条独立于宿主自报(宿主自己的 M4b 只是它自己的视角)。
#
# 用法:
#   pwsh       -NoProfile -File tests/scripts/verify-cli-strict.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/scripts/verify-cli-strict.ps1
#   ... -TimeoutSec 300        # 覆盖超时(秒)
#   ... -SelfCheck             # 额外跑一次 UIHOST_NEGCTL=1, 要求它**必须转红**
#
# 退出码: 0 = 全绿; 1 = 有失败(含超时 / 缺汇总行 / 非零退出码 / 锚点缺失 / 语料目录被污染 / SelfCheck 未转红)
#
# 双宿主兼容(PS 7 / PS 5.1): 禁 PS7+ 语法(三元 ? : / ?? / ?. / && / ||);
#   需要条件表达式一律用 $(if (...) {...} else {...})。
# 代码串纯 ASCII: 所有**字符串字面量**(打印消息 / 锚点)不含中文, 解释性中文一律放注释。
param(
    [int]$TimeoutSec = 180,
    [switch]$SelfCheck
)

$ErrorActionPreference = 'Continue'

# 仓库根: 由脚本自身位置推导(tests/scripts/x.ps1 => 上两级)
$scriptDir = $PSScriptRoot
$testsDir  = Split-Path -Parent $scriptDir
$repoRoot  = Split-Path -Parent $testsDir

# 1. 前置环境: 显式指定 PLAN 目录(宿主 fail-fast 的同一契约)
$planDir = Join-Path $repoRoot 'publish/PLAN'
$env:FFMPEGGUI_PLAN_DIR = $planDir
Write-Output "repo root          = $repoRoot"
Write-Output "FFMPEGGUI_PLAN_DIR = $env:FFMPEGGUI_PLAN_DIR"

$pass = 0; $fail = 0
function CK($ok, $msg) {
    if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
    else     { $script:fail++; Write-Output "  FAIL $msg" }
}

if (-not (Test-Path $planDir)) {
    Write-Output "  FAIL PLAN dir exists: $planDir"
    Write-Output "CLISTRICT RESULT: pass=0 fail=1"
    exit 1
}
CK $true "PLAN dir exists"

# 2. 目标可执行文件(只认 Release 产物, 不做 Debug 回退)
$exe = Join-Path $repoRoot 'tests/UiTestHost/bin/Release/net11.0/win-x64/UiTestHost.exe'
if (-not (Test-Path $exe)) {
    Write-Output "  FAIL host exe exists: $exe"
    Write-Output "       build first: dotnet build tests/UiTestHost/UiTestHost.csproj -c Release"
    Write-Output "CLISTRICT RESULT: pass=0 fail=1"
    exit 1
}
CK $true "host exe exists"

# 素材目录(宿主默认值是写死的绝对路径; 这里显式传参, 保持可复现)
$srcDir = Join-Path $repoRoot 'tests/output/sources'
CK (Test-Path $srcDir) "source dir exists ($srcDir)"

# 3. 运行级 GUID 临时目录(固定目录会被并发同名实例互删 => 假红)
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('clistrict_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

# 4. 带硬超时的子进程运行器
# 为什么走「子宿主 + rc 文件」而不是直接读 $p.ExitCode:
#   PS 5.1 下 Start-Process -PassThru 不带 -Wait 时 .ExitCode 恒 $null
#   => 判 $rc -ne 0 时 $null -ne 0 求值为 $true => **每条都记失败**(全轮假红)。
#   退出码经**文件**回传可彻底绕开这一点。
function Invoke-HostWithTimeout([string]$exePath, [string[]]$hostArgs, [int]$timeoutMs, [hashtable]$envOverride) {
    $tag    = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $rcFile = Join-Path $work ("rc_$tag.txt")
    $outF   = Join-Path $work ("out_$tag.txt")
    $errF   = Join-Path $work ("err_$tag.txt")

    $q = @()
    foreach ($a in $hostArgs) { $q += ("'" + ($a -replace "'", "''") + "'") }
    $inner = "& '" + ($exePath -replace "'", "''") + "' " + ($q -join ' ') +
             "; [System.IO.File]::WriteAllText('" + ($rcFile -replace "'", "''") + "', [string]`$LASTEXITCODE)"

    $saved = @{}
    foreach ($k in $envOverride.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process')
        [Environment]::SetEnvironmentVariable($k, [string]$envOverride[$k], 'Process')
    }

    $p = $null
    try {
        $p = Start-Process -FilePath ((Get-Process -Id $PID).Path) `
                -ArgumentList @('-NoProfile', '-Command', $inner) `
                -NoNewWindow -PassThru `
                -RedirectStandardOutput $outF -RedirectStandardError $errF
    } catch {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
        return @{ ExitCode = -1; TimedOut = $false; OutFile = $outF; ErrFile = $errF; StartError = $_.Exception.Message }
    }

    $finished = $false
    try { $finished = $p.WaitForExit($timeoutMs) } catch { $finished = $false }

    $timedOut = $false
    if (-not $finished) {
        $timedOut = $true
        # PS 5.1 没有 $p.Kill($true) 重载 => 用 taskkill /T 杀整棵树, 再补无参 Kill 兜底
        try { & taskkill.exe /PID $p.Id /T /F 2>$null | Out-Null } catch { }
        try { $null = $p.WaitForExit(5000) } catch { }
        if (-not $p.HasExited) { try { $p.Kill() } catch { } }
    } else {
        try { $p.WaitForExit() } catch { }   # 排空异步输出流(否则可能读到半截产物)
    }

    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }

    $rc = -1
    if (Test-Path $rcFile) { try { $rc = [int]([System.IO.File]::ReadAllText($rcFile).Trim()) } catch { $rc = -1 } }
    return @{ ExitCode = $rc; TimedOut = $timedOut; OutFile = $outF; ErrFile = $errF; StartError = '' }
}

# 解析宿主输出: 汇总行 + 计数。只匹配 **ASCII** 片段(N PASS / N FAIL),
# 不匹配中文, 以免宿主输出编码差异导致静默零命中。
function Read-HostSummary([string]$outFile) {
    $text = ''
    if (Test-Path $outFile) { $text = [System.IO.File]::ReadAllText($outFile) }
    $m = [regex]::Match($text, '(\d+) PASS / (\d+) FAIL')
    $has = $m.Success
    $pN = -1; $fN = -1
    if ($has) { $pN = [int]$m.Groups[1].Value; $fN = [int]$m.Groups[2].Value }
    return @{ Text = $text; HasSummary = $has; Pass = $pN; Fail = $fN }
}

# 统计以 `[PASS] <prefix>` 开头的行数。
# 为什么是**前缀**而不是子串 Contains: 子串会把「前缀出现在别人的 detail 里」也算命中
# => 断言可以恒真。前缀匹配要求该断言**自己**是那一行的开头。
function Count-PassPrefix([string]$text, [string]$prefix) {
    $n = 0
    $needle = '[PASS] ' + $prefix
    foreach ($ln in ($text -split "`r?`n")) {
        if ($ln.StartsWith($needle)) { $n++ }
    }
    return $n
}

# 5. 主跑
Write-Output ""
Write-Output "===== run: UiTestHost (timeout=${TimeoutSec}s) ====="
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$r = Invoke-HostWithTimeout $exe @($srcDir) ($TimeoutSec * 1000) @{}
$sw.Stop()
$elapsed = [int]$sw.Elapsed.TotalSeconds

if ($r.StartError -ne '') {
    Write-Output "  FAIL start child host: $($r.StartError)"
    CK $false "child host started"
} else {
    CK (-not $r.TimedOut) "no timeout (elapsed ${elapsed}s, limit ${TimeoutSec}s)"
}

$s = Read-HostSummary $r.OutFile
# 超时击杀后**抑制汇总解析**: 被杀的进程留下半截输出, 若其中恰有汇总行
# => 「挂死」会被当成「跑完了且干净」。改为打现场(末 5 行)做诊断。
if ($r.TimedOut) {
    $tail = ''
    if (Test-Path $r.OutFile) { $tail = ((Get-Content $r.OutFile -Tail 5 -ErrorAction SilentlyContinue) -join "`n") }
    $s = @{ Text = "[TIMEOUT] summary parsing suppressed; tail:`n$tail"; HasSummary = $false; Pass = -1; Fail = -1 }
}
Write-Output "----- UiTestHost stdout -----"
if ($s.Text -ne '') { Write-Output $s.Text } else { Write-Output "(empty)" }
$errText = ''
if (Test-Path $r.ErrFile) { $errText = [System.IO.File]::ReadAllText($r.ErrFile) }
if ($errText.Trim() -ne '') { Write-Output "----- stderr -----"; Write-Output $errText }
Write-Output "----- end UiTestHost -----"

CK ($s.HasSummary) "summary line present (exit 0 alone is not enough)"
CK ($s.HasSummary -and $s.Fail -eq 0) "summary FAIL=0"
CK ($r.ExitCode -eq 0) "exit code == 0 (actual $($r.ExitCode))"

# 6. 锚点在册: 挡住「GroupM / B1-B3 被删掉/改名/整段注释掉」的静默退化。
#    基线(2026-09-19): GroupM_CliStrict = 45 条 => 只要求 >= 45(只增不减)。
#    ⚠ 2026-09-19 严格化轮新增 13 条(M2a/M2b x 6 轴 = 12 条, M2f 分层语义守卫 = 1 条) => 预期 58 条。
#      首次整轮跑通后把下面两处 45 收紧到实测值(本会话内完成, 不留给后人)。
$mCount = Count-PassPrefix $s.Text 'M'
Write-Output "  [info] GroupM PASS lines = $mCount (baseline 45, expected 58 after 2026-09-19)"
CK ($mCount -ge 45) "GroupM assertion count >= 45 (actual $mCount)"

# 代表性锚点: D4 / D5 / D6 三族各至少一条 + 任务B(B1..B3) + 任务C(M5a..M5f)。
# 均为 ASCII 前缀; 中文后缀不参与匹配, 避免编码差异导致的零命中。
$anchors = @(
    'M1a ', 'M1b ', 'M1c ', 'M1d ', 'M1e ',
    'M2a color-strategy', 'M2b color-strategy', 'M2a icc-mode', 'M2b icc-mode',
    'M2a color-engine', 'M2a color-gamut-map', 'M2a jpeg-gain-map-base-gamut',
    'M2a bit-depth', 'M2a threads', 'M2a lossless', 'M2a color-hdr-peak',
    # 2026-09-19（维护者裁决第 2 项：严格化）：6 个新收口字符串轴 + 分层语义守卫。
    # 只钉 M2a/M2b 各 1 条（不逐轴全钉）：本清单要挡的是「整段被删/改名/注释掉」，
    # 而 GroupM 的计数基线本身是"只增不减"，逐轴全钉只会让清单随实现细节膨胀。
    'M2a chroma', 'M2b chroma', 'M2a color-range', 'M2a color-709-curve',
    'M2a color-carry-scope', 'M2a color-tone-map', 'M2a jpeg-huffman', 'M2f ',
    'M2c ', 'M2d ', 'M2e ',
    'M3a ', 'M3b ', 'M3c ', 'M3d ', 'M3e ', 'M3f ',
    'M4a ', 'M4b ', 'M4c _h11_bt2020.png', 'M4c _prophoto_src.png',
    'M4c _p3_8bit.png', 'M4c _k5_same_out.png',
    'M5a ', 'M5b ', 'M5c ', 'M5d ', 'M5e ', 'M5f ',
    'B1 H11', 'B2 I3', 'B3 I9'
)
$miss = @()
foreach ($a in $anchors) {
    if ((Count-PassPrefix $s.Text $a) -lt 1) { $miss += $a }
}
CK ($miss.Count -eq 0) "all $($anchors.Count) strict-mode anchors present (missing: $($miss -join ', '))"

# 7. 任务B 的外部验收: 宿主跑完后共享语料目录不得出现任何 _*.png。
#    为什么不信宿主自报: 宿主自己的 M4b 是"它自己的视角"; 这一条从**外部**独立取证,
#    而且它同时也覆盖了「宿主被超时杀掉后留下垃圾」的窗口(那时 M4 根本没跑到)。
$leftovers = @(Get-ChildItem -Path $srcDir -Filter '_*.png' -File -ErrorAction SilentlyContinue)
if ($leftovers.Count -gt 0) {
    Write-Output "  [info] leftovers: $(($leftovers | ForEach-Object { $_.Name }) -join ', ')"
}
CK ($leftovers.Count -eq 0) "shared corpus dir has no _*.png leftovers (actual $($leftovers.Count))"

# 8. 自检负控(-SelfCheck): 证明本门禁的判读链有牙
$ncOk = $true
if ($SelfCheck) {
    Write-Output ""
    Write-Output "===== selfcheck: UIHOST_NEGCTL=1 must turn RED ====="
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $r2 = Invoke-HostWithTimeout $exe @($srcDir) ($TimeoutSec * 1000) @{ UIHOST_NEGCTL = '1' }
    $sw2.Stop()
    $s2 = Read-HostSummary $r2.OutFile
    Write-Output ("  [selfcheck] exit={0} timedOut={1} summary={2} pass={3} fail={4} elapsed={5}s" -f `
        $r2.ExitCode, $r2.TimedOut, $s2.HasSummary, $s2.Pass, $s2.Fail, [int]$sw2.Elapsed.TotalSeconds)
    CK ((-not $r2.TimedOut) -and $s2.HasSummary -and ($s2.Fail -ge 1) -and ($r2.ExitCode -ne 0)) `
       "negative-control switch => summary shows FAIL and exit code != 0"
    $ncOk = ((-not $r2.TimedOut) -and $s2.HasSummary -and ($s2.Fail -ge 1) -and ($r2.ExitCode -ne 0))
}

# 9. 清理 + 判据
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

$ok = ($fail -eq 0) -and (-not $r.TimedOut) -and $s.HasSummary -and ($s.Fail -eq 0) -and ($r.ExitCode -eq 0) -and $ncOk

Write-Output ""
Write-Output ("  [info] exit={0} timedOut={1} summary={2} pass={3} fail={4} groupM={5} leftovers={6} elapsed={7}s" -f `
    $r.ExitCode, $r.TimedOut, $s.HasSummary, $s.Pass, $s.Fail, $mCount, $leftovers.Count, $elapsed)

if ($ok) {
    Write-Output "CLISTRICT RESULT: pass=$($s.Pass) fail=0"
    exit 0
} else {
    $reason = 'unknown'
    if ($r.TimedOut) { $reason = "timeout ${TimeoutSec}s" }
    elseif (-not $s.HasSummary) { $reason = 'summary line missing' }
    elseif ($r.ExitCode -ne 0) { $reason = "exit code $($r.ExitCode)" }
    elseif ($s.Fail -ne 0) { $reason = "host FAIL=$($s.Fail)" }
    elseif ($leftovers.Count -ne 0) { $reason = "shared corpus dir polluted ($($leftovers.Count) files)" }
    elseif (-not $ncOk) { $reason = 'selfcheck negative control did not turn red' }
    elseif ($fail -ne 0) { $reason = "local checks failed ($fail)" }
    Write-Output "CLISTRICT FAIL: $reason"
    Write-Output "CLISTRICT RESULT: pass=$($s.Pass) fail=$($s.Fail)"
    exit 1
}
