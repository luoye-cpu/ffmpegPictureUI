# verify-ui-strategy-map.ps1 -- P1-D 门禁: 四大模式 UI 映射逐值验证 + 4->3 折叠
#
# 术语先对齐(本仓最易搞错的一处):
#   「四大模式」= `Models.ColorStrategy` 的 4 个值
#                 Recommended=0 / CarryIcc=1 / BakeCicpOnly=2 / Manual=3
#   **不是** UI 的「静态图 / 动画 / RAW」三值(那是输出形态),
#   **也不是** `FfmpegOptions.ColorEngine`(那是色彩引擎开关)。
#   本仓 UI 当前的真实结构 = `MainWindow.xaml` 里 **4 个 ColorStrategy 单选**
#   (控件名 IccModeNone / IccModeCarry / IccModeBake / IccModeBakeOnly),
#   经由 `MainWindow.GetColorStrategy()` 读出; 4->3 折叠发生在
#   `Models.ColorStrategyMapper.FromIccMode`(旧 IccMode 4 值 -> 新 ColorStrategy 3 个非 Manual 值):
#       None            -> Recommended
#       BakeToStandard  -> Recommended     <== 折叠点: 与 None 同值
#       CarryIcc        -> CarryIcc
#       BakeOnly        -> BakeCicpOnly
#   折叠的**硬要求**: None 与 BakeToStandard 必须产出**逐 token 完全相同**的命令串;
#   同时 CarryIcc / BakeCicpOnly 与它们必须**不同**(这是负控 —— 若都相同, 说明场景没有
#   区分力, "折叠" 断言会退化成恒真)。
#
# 本门禁锁住的五件事:
#   1. 前置环境: 显式设 `FFMPEGGUI_PLAN_DIR`(按仓库根推导, 不写死绝对路径)。
#   2. 硬超时(默认 180 s)。`Start-Process -Wait` 没有 timeout 参数。
#   3. 判据 = 退出码 == 0 ∧ 汇总行存在 ∧ FAIL=0(缺一不可)。
#   4. **锚点在册**: 逐条要求 K1..K7 的代表性断言名以 `[PASS] ` 前缀出现 ——
#      光看 FAIL=0 挡不住 "整组被删掉/改名" 的静默退化。
#   5. **CLI 4 别名 + 折叠 + 负控三类锚点必须同时在册**: 缺任一类即判红。
#
# 用法:
#   pwsh       -NoProfile -File tests/scripts/verify-ui-strategy-map.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/scripts/verify-ui-strategy-map.ps1
#   ... -TimeoutSec 300        # 覆盖超时(秒)
#   ... -SelfCheck             # 额外跑一次 UIHOST_NEGCTL=1, 要求它**必须转红**
#
# 退出码: 0 = 全绿; 1 = 有失败(含超时 / 缺汇总行 / 非零退出码 / 锚点缺失 / SelfCheck 未转红)
#
# 双宿主兼容(PS 7 / PS 5.1): 禁 PS7+ 语法(三元 `? :` / `??` / `?.` / `&&` / `||`);
#   需要条件表达式一律用 `$(if (...) {...} else {...})`。
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
    Write-Output "UISTRAT RESULT: pass=0 fail=1"
    exit 1
}
CK $true "PLAN dir exists"

# 2. 目标可执行文件(只认 Release 产物, 不做 Debug 回退)
$exe = Join-Path $repoRoot 'tests/UiTestHost/bin/Release/net11.0/win-x64/UiTestHost.exe'
if (-not (Test-Path $exe)) {
    Write-Output "  FAIL host exe exists: $exe"
    Write-Output "       build first: dotnet build tests/UiTestHost/UiTestHost.csproj -c Release"
    Write-Output "UISTRAT RESULT: pass=0 fail=1"
    exit 1
}
CK $true "host exe exists"

$srcDir = Join-Path $repoRoot 'tests/output/sources'
CK (Test-Path $srcDir) "source dir exists ($srcDir)"

# 3. 运行级 GUID 临时目录(固定目录会被并发同名实例互删 => 假红)
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('uistrat_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

# 4. 带硬超时的子进程运行器
# 为什么走「子宿主 + rc 文件」而不是直接读 $p.ExitCode:
#   PS 5.1 下 `Start-Process -PassThru` 不带 `-Wait` 时 `.ExitCode` 恒 $null
#   => 判 `$rc -ne 0` 时 `$null -ne 0` 求值为 $true => **每条都记失败**(全轮假红)。
#   退出码经**文件**回传可彻底绕开这一点。
# 返回 @{ ExitCode; TimedOut; OutFile; ErrFile }; ExitCode 永不返回 $null(缺文件 => -1)。
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

# 解析宿主输出: 汇总行 + 计数。只匹配 **ASCII** 片段(`N PASS / N FAIL`)。
function Read-HostSummary([string]$outFile) {
    $text = ''
    if (Test-Path $outFile) { $text = [System.IO.File]::ReadAllText($outFile) }
    $m = [regex]::Match($text, '(\d+) PASS / (\d+) FAIL')
    $has = $m.Success
    $pN = -1; $fN = -1
    if ($has) { $pN = [int]$m.Groups[1].Value; $fN = [int]$m.Groups[2].Value }
    return @{ Text = $text; HasSummary = $has; Pass = $pN; Fail = $fN }
}

# 统计以 `[PASS] <prefix>` 开头的行数(前缀匹配, 不用子串 Contains:
# 子串匹配会把 "K1 出现在别人 detail 里" 也算命中 => 断言恒真)。
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
# 超时击杀后**抑制汇总解析**: 半截输出里若恰有汇总行 => 「挂死」会被当成「跑完且干净」。
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

# 6. 锚点在册。基线(2026-09-19): GroupK_StrategyMap = 27 条 => 只要求 >= 27(只增不减)。
$kCount = Count-PassPrefix $s.Text 'K'
Write-Output "  [info] GroupK PASS lines = $kCount (baseline 27)"
CK ($kCount -ge 27) "GroupK assertion count >= 27 (actual $kCount)"

# 6a. UI 四单选 -> 四策略 的逐值锚点(证明 UI 真实结构 = 4 个 ColorStrategy 单选)
$uiAnchors = @(
    'K1 IccModeNone', 'K1 IccModeCarry', 'K1 IccModeBake', 'K1 IccModeBakeOnly',
    'K3a Recommended', 'K3b CarryIcc', 'K3c BakeCicpOnly', 'K3d '
)
# 6b. CLI 4 别名(两条独立路径: K4 走 CliParser.Parse, K7 走 ColorStrategyMapper.TryParse)
$cliAnchors = @(
    'K4 recommended', 'K4 carry-icc', 'K4 cicp-only', 'K4 manual',
    'K7 recommended', 'K7 carry-icc', 'K7 cicp-only', 'K7 manual',
    'K7 '
)
# 6c. 4->3 折叠 + 负控(None 与 BakeToStandard 逐 token 相同; 且与另两个不同)
$foldAnchors = @(
    'K2a CarryIcc', 'K2b BakeCicpOnly',
    'K5a None', 'K5b ', 'K5c ', 'K5d ',
    'K6 None', 'K6 BakeToStandard', 'K6 CarryIcc', 'K6 BakeOnly'
)

$miss = @()
foreach ($a in ($uiAnchors + $cliAnchors + $foldAnchors)) {
    if ((Count-PassPrefix $s.Text $a) -lt 1) { $miss += $a }
}
CK ($miss.Count -eq 0) "all anchors present (missing: $($miss -join ', '))"
CK ((Count-PassPrefix $s.Text 'K5a ') -ge 1) "fold anchor K5a present (None == BakeToStandard, token-exact)"
CK ((Count-PassPrefix $s.Text 'K7 ') -ge 1) "CLI negative control anchor K7 present (illegal value => false)"

# 7. 自检负控(-SelfCheck): 证明本门禁的判读链有牙
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

# 8. 清理 + 判据
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

$ok = ($fail -eq 0) -and (-not $r.TimedOut) -and $s.HasSummary -and ($s.Fail -eq 0) -and ($r.ExitCode -eq 0) -and $ncOk

Write-Output ""
Write-Output ("  [info] exit={0} timedOut={1} summary={2} pass={3} fail={4} groupK={5} elapsed={6}s" -f `
    $r.ExitCode, $r.TimedOut, $s.HasSummary, $s.Pass, $s.Fail, $kCount, $elapsed)

if ($ok) {
    Write-Output "UISTRAT RESULT: pass=$($s.Pass) fail=0"
    exit 0
} else {
    $reason = 'unknown'
    if ($r.TimedOut) { $reason = "timeout ${TimeoutSec}s" }
    elseif (-not $s.HasSummary) { $reason = 'summary line missing' }
    elseif ($r.ExitCode -ne 0) { $reason = "exit code $($r.ExitCode)" }
    elseif ($s.Fail -ne 0) { $reason = "host FAIL=$($s.Fail)" }
    elseif (-not $ncOk) { $reason = 'selfcheck negative control did not turn red' }
    elseif ($fail -ne 0) { $reason = "local checks failed ($fail)" }
    Write-Output "UISTRAT FAIL: $reason"
    Write-Output "UISTRAT RESULT: pass=$($s.Pass) fail=$($s.Fail)"
    exit 1
}
