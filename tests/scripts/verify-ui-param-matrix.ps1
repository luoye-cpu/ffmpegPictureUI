# verify-ui-param-matrix.ps1 -- P1-B 门禁: UI 控件穷举 -> 命令串 token 级断言
#
# 为什么需要这一条:
#   P1-B 要求「MainWindow.xaml 里每一个输入型控件」都有断言覆盖: 默认值 / 每个可选值 /
#   改回默认后 token 消失。这些断言全部落在 UiTestHost 的 GroupJ_UiSweep。
#   本脚本是该分组的**门禁入口**: 它不重复断言逻辑, 只负责把宿主可复现地跑起来,
#   并**锁住分组真的跑了、而且全绿**。
#
# 与 verify-ui-host.ps1 的分工(两者都跑同一个宿主, 判据链相同):
#   verify-ui-host.ps1        : 全量宿主门禁(A..K 全组) + -SelfCheck 负控
#   verify-ui-param-matrix.ps1: 只认 P1-B 面(J 组), 额外**逐锚点**要求关键控件在册
#   verify-ui-strategy-map.ps1: 只认 P1-D 面(K 组), 额外要求 4 别名 + 4->3 折叠锚点
#
# 本门禁锁住的四件事:
#   1. 前置环境: 显式设 `FFMPEGGUI_PLAN_DIR`(按仓库根推导, 不写死绝对路径)。
#      裸跑会挂死 >13 min 不收敛(宿主侧已 fail-fast, 这里再从外部兜一层)。
#   2. 硬超时(默认 180 s)。`Start-Process -Wait` 没有 timeout 参数 => 子进程挂死会
#      静默拖死整轮门禁。本仓硬约束 §6: 长跑一律前台发起。
#   3. 判据 = 退出码 == 0 ∧ 汇总行存在 ∧ FAIL=0(缺一不可; `exit 0` 只说明"跑完了")。
#   4. **锚点在册**: 光看 `FAIL=0` 挡不住"分组被整个删掉/改名"这一类静默退化 =>
#      再逐条要求代表性断言名以 `[PASS] ` 前缀出现在输出里。这是本脚本比
#      verify-ui-host.ps1 更严的地方。
#
# 用法:
#   pwsh       -NoProfile -File tests/scripts/verify-ui-param-matrix.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/scripts/verify-ui-param-matrix.ps1
#   ... -TimeoutSec 300        # 覆盖超时(秒)
#   ... -SelfCheck             # 额外跑一次 UIHOST_NEGCTL=1, 要求它**必须转红**
#                              # => 证明本门禁的判读链(退出码 + 汇总行 + 锚点)不是空跑
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
    Write-Output "UIPARAM RESULT: pass=0 fail=1"
    exit 1
}
CK $true "PLAN dir exists"

# 2. 目标可执行文件(只认 Release 产物, 不做 Debug 回退)
$exe = Join-Path $repoRoot 'tests/UiTestHost/bin/Release/net11.0/win-x64/UiTestHost.exe'
if (-not (Test-Path $exe)) {
    Write-Output "  FAIL host exe exists: $exe"
    Write-Output "       build first: dotnet build tests/UiTestHost/UiTestHost.csproj -c Release"
    Write-Output "UIPARAM RESULT: pass=0 fail=1"
    exit 1
}
CK $true "host exe exists"

# 素材目录(宿主默认值是写死的绝对路径; 这里显式传参, 保持可复现)
$srcDir = Join-Path $repoRoot 'tests/output/sources'
CK (Test-Path $srcDir) "source dir exists ($srcDir)"

# 3. 运行级 GUID 临时目录(固定目录会被并发同名实例互删 => 假红)
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('uiparam_' + [guid]::NewGuid().ToString('N'))
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

# 解析宿主输出: 汇总行 + 计数。只匹配 **ASCII** 片段(`N PASS / N FAIL`),
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
# 为什么要**前缀**匹配而不是子串 Contains:
#   子串匹配会把「J1 出现在别人的 detail 里」也算命中 => 断言可以恒真。
#   前缀匹配要求该断言**自己**是那一行的开头, 与宿主 `Check()` 的打印格式一一对应。
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

# 6. 锚点在册: 挡住「GroupJ 被删掉/改名/整段注释掉」的静默退化。
#    基线(2026-09-19): GroupJ_UiSweep = 88 条; 这里只要求 >= 88 => 只增不减。
$jCount = Count-PassPrefix $s.Text 'J'
Write-Output "  [info] GroupJ PASS lines = $jCount (baseline 88)"
CK ($jCount -ge 88) "GroupJ assertion count >= 88 (actual $jCount)"

# 代表性锚点 = 每个控件族至少一条(格式/像素格式/质量/色域/zscale 目标/元数据/动图/几何/编码器特化)。
# 均为 ASCII 前缀; 中文后缀不参与匹配, 避免编码差异导致的零命中。
$anchors = @(
    'J1 PNG', 'J1 WebP', 'J1 AVIF', 'J1 JPEG', 'J1 TIFF',
    'J2 4:2:0', 'J2 4:2:2', 'J2 4:4:4',
    'J3 PNG',
    'J4 ',
    'J5 bt709', 'J5 bt2020', 'J5 smpte432',
    'J6 bt709', 'J6 smpte2084', 'J6 linear', 'J6 arib-std-b67',
    'J7 bt709', 'J7 bt2020nc', 'J7 gbr',
    'J8 sRGB', 'J8 BT.709', 'J8 Display P3', 'J8 P3 PQ',
    'J9a PNG', 'J9b WebP', 'J9c WebP',
    'J10 ',
    'J11a ', 'J11b ', 'J11c ', 'J11d ', 'J11e ',
    'J12a ', 'J12b ', 'J12c ',
    'J13a JPEG', 'J13b PNG', 'J13c MetadataModeCombo',
    'J14b photo', 'J14c-1', 'J14c-2', 'J14c-3',
    'J15a ', 'J15b fps', 'J15c fps', 'J15d scaleW', 'J15e duration', 'J15f ', 'J15g WebP'
)
$miss = @()
foreach ($a in $anchors) {
    if ((Count-PassPrefix $s.Text $a) -lt 1) { $miss += $a }
}
CK ($miss.Count -eq 0) "all $($anchors.Count) control-family anchors present (missing: $($miss -join ', '))"

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
Write-Output ("  [info] exit={0} timedOut={1} summary={2} pass={3} fail={4} groupJ={5} elapsed={6}s" -f `
    $r.ExitCode, $r.TimedOut, $s.HasSummary, $s.Pass, $s.Fail, $jCount, $elapsed)

if ($ok) {
    Write-Output "UIPARAM RESULT: pass=$($s.Pass) fail=0"
    exit 0
} else {
    $reason = 'unknown'
    if ($r.TimedOut) { $reason = "timeout ${TimeoutSec}s" }
    elseif (-not $s.HasSummary) { $reason = 'summary line missing' }
    elseif ($r.ExitCode -ne 0) { $reason = "exit code $($r.ExitCode)" }
    elseif ($s.Fail -ne 0) { $reason = "host FAIL=$($s.Fail)" }
    elseif (-not $ncOk) { $reason = 'selfcheck negative control did not turn red' }
    elseif ($fail -ne 0) { $reason = "local checks failed ($fail)" }
    Write-Output "UIPARAM FAIL: $reason"
    Write-Output "UIPARAM RESULT: pass=$($s.Pass) fail=$($s.Fail)"
    exit 1
}
