# verify-ui-host.ps1 -- UiTestHost 门禁入口 (2026-09-19 新增, P1-A)
#
# 为什么需要这一条:
#   `tests/UiTestHost` 是 Avalonia.Headless 宿主, 加载**真实 MainWindow** 并逐条断言
#   UI 交互 / 选项生效 / 参数传递, 是本仓**唯一**允许的 UI 验证手段(硬约束 §7.2:
#   禁止在用户实时桌面上弹窗 / 做鼠标自动化)。
#   => 它必须能被**可复现地**调用, 否则 P1-B..P1-D 全部无从落地。
#
# 本门禁锁住的三件事:
#   1. **前置环境**: 先设 `FFMPEGGUI_PLAN_DIR`(按仓库根推导, 不写死绝对路径)。
#      ⚠ 裸跑会挂死 >13 min 不收敛: `bin/.../PLAN` Junction 不存在 => `PlanFolderPath`
#      向上误命中 `<parent-dir>`(假阳性, 因仓库恰在该路径下) => 外部工具探测落入**无超时**的
#      扩展路径枚举(`C:\Program Files` + `%LocalAppData%\Programs` + 每个 PATH 目录),
#      栈恒在 `FileSystemEnumerator.MoveNext`。宿主侧已加 fail-fast, 本脚本再从外部兜一层。
#   2. **硬超时**(默认 120 s, `-TimeoutSec` 可覆盖)。`Start-Process -Wait` **没有** timeout
#      参数 => 一旦子进程挂死, 整轮门禁**静默挂死**。没有这一层, 未来任何人把它纳入门禁
#      都会把整轮拖死(本仓硬约束 §6: 长跑一律前台发起, 显式后台任务约 2 min 被宿主杀)。
#   3. **判据 = 退出码 == 0 ∧ 汇总行存在 ∧ FAIL=0**。
#      ⚠ 本仓教训: `exit 0` 可能只是"跑完了", 所以**必须同时**看汇总行; 反过来, 汇总行
#      存在但退出码非 0 也判红。两者缺一不可。
#
# 用法:
#   pwsh        -NoProfile -File tests/scripts/verify-ui-host.ps1
#   powershell  -NoProfile -ExecutionPolicy Bypass -File tests/scripts/verify-ui-host.ps1
#   ... -TimeoutSec 300        # 覆盖超时(秒)
#   ... -SelfCheck             # 额外跑一次宿主的负控开关(UIHOST_NEGCTL=1), 要求它**必须转红**
#                              # => 证明本门禁的判读链(退出码 + 汇总行)不是空跑
#
# 退出码: 0 = 全绿; 1 = 有失败(含超时 / 缺汇总行 / 非零退出码 / SelfCheck 未转红)
#
# ⚠ 双宿主兼容(PS 7 / PS 5.1): 禁 PS7+ 语法(三元 `? :` / `??` / `?.` / `&&` / `||`);
#   需要条件表达式一律用 `$(if (…) {…} else {…})`。双引号串内禁全角弯引号(用 「」)。
# ⚠ 代码串纯 ASCII: 所有**字符串字面量**(打印消息)不含中文, 解释性中文一律放注释。
param(
    [int]$TimeoutSec = 120,
    [switch]$SelfCheck
)

$ErrorActionPreference = 'Continue'

# ── 仓库根: 由脚本自身位置推导(tests/scripts/x.ps1 => 上两级) ────────────────────
$scriptDir = $PSScriptRoot
$testsDir  = Split-Path -Parent $scriptDir
$repoRoot  = Split-Path -Parent $testsDir

# ── 1. 前置环境: 显式指定 PLAN 目录(宿主 fail-fast 的同一契约) ───────────────────
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
    Write-Output "UIHOST RESULT: pass=0 fail=1"
    exit 1
}
CK $true "PLAN dir exists"

# ── 2. 目标可执行文件(只认 Release 产物, 不做 Debug 回退) ──────────────────────
$exe = Join-Path $repoRoot 'tests/UiTestHost/bin/Release/net11.0/win-x64/UiTestHost.exe'
if (-not (Test-Path $exe)) {
    Write-Output "  FAIL host exe exists: $exe"
    Write-Output "       build first: dotnet build tests/UiTestHost/UiTestHost.csproj -c Release"
    Write-Output "UIHOST RESULT: pass=0 fail=1"
    exit 1
}
CK $true "host exe exists"

# 素材目录(宿主默认值是写死的绝对路径; 这里显式传参, 保持可复现)
$srcDir = Join-Path $repoRoot 'tests/output/sources'
CK (Test-Path $srcDir) "source dir exists ($srcDir)"

# ── 3. 运行级 GUID 临时目录(固定目录会被并发同名实例互删 => 假红, §7.9) ────────
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('uihost_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

# ── 4. 带硬超时的子进程运行器 ──────────────────────────────────────────────────
# ⚠ 为什么走「子宿主 + rc 文件」而不是直接读 $p.ExitCode:
#   PS 5.1 下 `Start-Process -PassThru` 不带 `-Wait` 时 `.ExitCode` 恒 `$null`
#   => 判 `$rc -ne 0` 时 `$null -ne 0` 求值为 `$true` => **每条都记失败**(全轮假红)。
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

# 解析宿主输出: 汇总行 + 计数。⚠ 只匹配 **ASCII** 片段(`N PASS / N FAIL`),
# 不匹配中文, 以免宿主/宿主的输出编码差异导致静默零命中。
function Read-HostSummary([string]$outFile) {
    $text = ''
    if (Test-Path $outFile) { $text = [System.IO.File]::ReadAllText($outFile) }
    $m = [regex]::Match($text, '(\d+) PASS / (\d+) FAIL')
    $has = $m.Success
    $pN = -1; $fN = -1
    if ($has) { $pN = [int]$m.Groups[1].Value; $fN = [int]$m.Groups[2].Value }
    return @{ Text = $text; HasSummary = $has; Pass = $pN; Fail = $fN }
}

# ── 5. 主跑 ────────────────────────────────────────────────────────────────────
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
# ⚠ 超时击杀后**抑制汇总解析**: 被杀的进程留下半截输出, 若其中恰有汇总行
#   => 「挂死」会被当成「跑完了且干净」。改为打现场(末 5 行)做诊断。
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

# ── 6. 自检负控(-SelfCheck): 证明本门禁的判读链有牙 ──────────────────────────
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

# ── 7. 清理 + 判据 ────────────────────────────────────────────────────────────
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

$ok = ($fail -eq 0) -and (-not $r.TimedOut) -and $s.HasSummary -and ($s.Fail -eq 0) -and ($r.ExitCode -eq 0) -and $ncOk

Write-Output ""
Write-Output ("  [info] exit={0} timedOut={1} summary={2} pass={3} fail={4} elapsed={5}s" -f `
    $r.ExitCode, $r.TimedOut, $s.HasSummary, $s.Pass, $s.Fail, $elapsed)

if ($ok) {
    Write-Output "UIHOST RESULT: pass=$($s.Pass) fail=0"
    exit 0
} else {
    $reason = 'unknown'
    if ($r.TimedOut) { $reason = "timeout ${TimeoutSec}s" }
    elseif (-not $s.HasSummary) { $reason = 'summary line missing' }
    elseif ($r.ExitCode -ne 0) { $reason = "exit code $($r.ExitCode)" }
    elseif ($s.Fail -ne 0) { $reason = "host FAIL=$($s.Fail)" }
    elseif (-not $ncOk) { $reason = 'selfcheck negative control did not turn red' }
    Write-Output "UIHOST FAIL: $reason"
    Write-Output "UIHOST RESULT: pass=$($s.Pass) fail=$($s.Fail)"
    exit 1
}
