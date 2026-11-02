# verify-ui-param-defects.ps1 -- P1-F 门禁: UI->命令 参数传递缺陷 4 项(修 + 现状锁)
#
# 为什么需要这一条(而不是复用 verify-ui-param-matrix.ps1):
#   P1-F 的四项结论全部落在 UiTestHost 的 GroupL_ParamDefects。param-matrix 门禁只
#   逐条要求 **J 组** 的控件族锚点在册 => 它对「GroupL 被整段删掉 / 改名 / 注释掉」
#   是**盲的**(那时 FAIL=0 仍然成立, 只是断言数变少)。本脚本就是 GroupL 的锚点门禁。
#
# 本门禁锁住的四件事:
#   1. 前置环境: 显式设 FFMPEGGUI_PLAN_DIR(按仓库根推导, 不写死绝对路径)。
#      裸跑会挂死 >13 min 不收敛(宿主侧已 fail-fast, 这里再从外部兜一层)。
#   2. 硬超时(默认 180 s)。Start-Process -Wait 没有 timeout 参数 => 子进程挂死会
#      静默拖死整轮门禁。本仓硬约束 §6: 长跑一律前台发起。
#   3. 判据 = 退出码 == 0 且 汇总行存在 且 FAIL=0(缺一不可; exit 0 只说明"跑完了")。
#   4. 锚点在册: 4 个缺陷族(L1..L4)各有代表性断言名以 [PASS] 前缀出现;
#      另含 J14d 的 P1-F 反转锚点(J14d-2/J14d-4/J14d-5)。
#
# 用法:
#   pwsh       -NoProfile -File tests/scripts/verify-ui-param-defects.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/scripts/verify-ui-param-defects.ps1
#   ... -TimeoutSec 300        # 覆盖超时(秒)
#   ... -SelfCheck             # 额外跑一次 UIHOST_NEGCTL=1, 要求它**必须转红**
#
# 退出码: 0 = 全绿; 1 = 有失败(含超时 / 缺汇总行 / 非零退出码 / 锚点缺失 / SelfCheck 未转红)
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
    Write-Output "UIPARAMDEF RESULT: pass=0 fail=1"
    exit 1
}
CK $true "PLAN dir exists"

# 2. 目标可执行文件(只认 Release 产物, 不做 Debug 回退)
# ⚠ 2026-10-31 执行面迁移：本门禁的两族断言都住在拆分件 `ui-sweep` 里，但分属**两个 mode**
#   （见下面的 $arms）—— 迁移前它是「起一次单体跑全量 430 条、再用 [PASS] 前缀挑自己那两族」。
$exe = Join-Path $repoRoot 'tests/projects/ui-sweep/bin/Release/net11.0/win-x64/Tests.UiSweep.exe'
if (-not (Test-Path $exe)) {
    Write-Output "  FAIL host exe exists: $exe"
    Write-Output "       build first: dotnet build tests/projects/ui-sweep/ui-sweep.csproj -c Release"
    Write-Output "UIPARAMDEF RESULT: pass=0 fail=1"
    exit 1
}
CK $true "host exe exists"

# 素材目录(宿主默认值是写死的绝对路径; 这里显式传参, 保持可复现)
$srcDir = Join-Path $repoRoot 'tests/output/sources'
CK (Test-Path $srcDir) "source dir exists ($srcDir)"

# 3. 运行级 GUID 临时目录(固定目录会被并发同名实例互删 => 假红)
# ⚠ 落在 tests/output 下（不是 %TEMP%）：AGENTS §3 规定产物只有一份、临时件根钉在产物区。
$work = Join-Path $repoRoot ('tests/output/ui-param-defects/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

# 4. 带硬超时的子进程运行器
# 为什么走「子宿主 + rc 文件」而不是直接读 $p.ExitCode:
#   PS 5.1 下 Start-Process -PassThru 不带 -Wait 时 .ExitCode 恒 $null
#   => 判 $rc -ne 0 时 $null -ne 0 求值为 $true => **每条都记失败**(全轮假红)。
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

    # ⚠ 必须 Dispose：Start-Process 的 -RedirectStandardOutput/-Error 句柄由**本进程**持有，
    #   WaitForExit 不会释放它们 ⇒ 紧接着 ReadAllText 会偶发 IOException「being used by another process」
    #   （2026-10-31 迁到 tests/output 后连中两次：exit=0 但 summary 读空 ⇒ 假红）。
    if ($null -ne $p) { try { $p.Dispose() } catch { } }
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }

    $rc = -1
    if (Test-Path $rcFile) { try { $rc = [int]([System.IO.File]::ReadAllText($rcFile).Trim()) } catch { $rc = -1 } }
    return @{ ExitCode = $rc; TimedOut = $timedOut; OutFile = $outF; ErrFile = $errF; StartError = '' }
}

# 解析宿主输出: 汇总行 + 计数。只匹配 **ASCII** 片段(N PASS / N FAIL),
# 不匹配中文, 以免宿主输出编码差异导致静默零命中。
# ⚠ 共享模式读（不是 ReadAllText）：宿主的**孙进程**（ffmpeg / cjpegli / dngtool 的探测进程）会
#   继承重定向句柄，宿主已退出但孙进程还活着 1~2 秒时，ReadAllText 抛 IOException「being used by
#   another process」⇒ 输出被读成空 ⇒ 汇总行判「missing」⇒ **整条门禁假红**。
#   2026-10-31 实测：Tests.UiColor 退出后 ffmpeg(PID 82716) 仍存活 ~1 s 并持有该句柄。
#   用 FileShare.ReadWrite 打开可绕开独占，另带 25×200 ms 重试兜住「文件尚未出现」。
function Read-TextShared([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    for ($i = 0; $i -lt 25; $i++) {
        try {
            $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
                  [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            $sr = New-Object System.IO.StreamReader($fs)
            $txt = $sr.ReadToEnd()
            $sr.Dispose()
            return $txt
        } catch { Start-Sleep -Milliseconds 200 }
    }
    return ''
}

function Read-HostSummary([string]$outFile) {
    $text = Read-TextShared $outFile
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

# 5. 主跑（**两臂**：本组判据跨两个 mode）
#    2026-10-31 执行面迁移：拆分后这两族在同一个 exe 的两个 mode 里 ——
#      · `ui-param-defects` ⇒ L 族（P1-F 缺陷族，实测 43 条）
#      · `ui-sweep`         ⇒ J14d 族（TIFF 压缩算法 / DPI 的 P1-F 反转锚点，实测 9 条）
#    必须跑两次、把 stdout **拼起来**再做锚点判定：任一臂汇总缺失 / FAIL!=0 / exit!=0 / 超时都算红。
$arms = @(
    @{ Mode = 'ui-param-defects'; Label = 'L 族 (P1-F 缺陷)' },
    @{ Mode = 'ui-sweep';         Label = 'J14d 族 (TIFF / DPI 反转锚点)' }
)
$merged   = New-Object System.Text.StringBuilder
$allClean = $true
$elapsed  = 0
foreach ($arm in $arms) {
    Write-Output ""
    Write-Output ("===== run: Tests.UiSweep {0} — {1} (timeout=${TimeoutSec}s) =====" -f $arm.Mode, $arm.Label)
    $swA = [System.Diagnostics.Stopwatch]::StartNew()
    $ra  = Invoke-HostWithTimeout $exe @($srcDir, $arm.Mode) ($TimeoutSec * 1000) @{}
    $swA.Stop()
    $armSec = [int]$swA.Elapsed.TotalSeconds
    $elapsed += $armSec

    if ($ra.StartError -ne '') {
        Write-Output "  FAIL start child host: $($ra.StartError)"
        CK $false "arm '$($arm.Mode)' child host started ($($ra.StartError))"
        $allClean = $false
        continue
    }
    CK (-not $ra.TimedOut) "arm '$($arm.Mode)' no timeout (elapsed ${armSec}s, limit ${TimeoutSec}s)"

    $sa = Read-HostSummary $ra.OutFile
    # 超时击杀后**抑制汇总解析**: 被杀的进程留下半截输出, 若其中恰有汇总行
    # => 「挂死」会被当成「跑完了且干净」。改为打现场(末 5 行)做诊断。
    if ($ra.TimedOut) {
        $tail = ''
        if (Test-Path $ra.OutFile) { $tail = ((Get-Content $ra.OutFile -Tail 5 -ErrorAction SilentlyContinue) -join "`n") }
        $sa = @{ Text = "[TIMEOUT] summary parsing suppressed; tail:`n$tail"; HasSummary = $false; Pass = -1; Fail = -1 }
    }
    Write-Output "----- $($arm.Mode) stdout -----"
    if ($sa.Text -ne '') { Write-Output $sa.Text } else { Write-Output "(empty)" }
    $errText = Read-TextShared $ra.ErrFile
    if ($errText.Trim() -ne '') { Write-Output "----- stderr -----"; Write-Output $errText }
    Write-Output "----- end $($arm.Mode) -----"

    CK ($sa.HasSummary) "arm '$($arm.Mode)' summary line present (exit 0 alone is not enough)"
    CK ($sa.HasSummary -and $sa.Fail -eq 0) "arm '$($arm.Mode)' summary FAIL=0"
    CK ($ra.ExitCode -eq 0) "arm '$($arm.Mode)' exit code == 0 (actual $($ra.ExitCode))"
    if (-not ($sa.HasSummary -and ($sa.Fail -eq 0) -and ($ra.ExitCode -eq 0) -and (-not $ra.TimedOut))) { $allClean = $false }
    [void]$merged.AppendLine($sa.Text)
}
$allText = $merged.ToString()

# 6. 条数锁：挡住「L 族 / J14d 族被删掉、改名、整段注释掉」的静默退化。
#    L 族基线 43（2026-09-19 单体读数，2026-10-31 在 ui-param-defects mode 下实测仍 43）；
#    J14d 族实测 9 条（旧门禁靠「单体一次跑全量」顺带覆盖，没有独立计数 ⇒ 本批补上，
#    否则第二臂整段消失也仍是绿）。两者都由下界式收紧为**精确等值**。
$lExpected    = 43
$j14dExpected = 9
$lCount    = Count-PassPrefix $allText 'L'
$j14dCount = Count-PassPrefix $allText 'J14d'
Write-Output "  [info] GroupL PASS lines = $lCount (expected $lExpected)；J14d PASS lines = $j14dCount (expected $j14dExpected)"
CK ($lCount -eq $lExpected) "GroupL assertion count == $lExpected (actual $lCount)"
CK ($j14dCount -eq $j14dExpected) "J14d assertion count == $j14dExpected (actual $j14dCount)"

# 代表性锚点 = 4 个缺陷族每族至少一条 + J14d 的 P1-F 反转锚点。
# 均为 ASCII 前缀; 中文后缀不参与匹配, 避免编码差异导致的零命中。
$anchors = @(
    'L1a av1_nvenc', 'L1a av1_amf', 'L1a mjpeg_nvenc', 'L1a mjpeg ', 'L1a libaom-av1',
    'L1b av1_nvenc', 'L1c ', 'L1d ',
    'L1e-0 ', 'L1f ', 'L1g ', 'L1h ', 'L1i ', 'L1j ', 'L1k ',
    'L2a ', 'L2b ', 'L2c ', 'L2d ', 'L2e ', 'L2f ', 'L2g ', 'L2h ', 'L2i ', 'L2j ', 'L2k ',
    'L3a ', 'L3b raw', 'L3b lzw', 'L3b deflate', 'L3b packbits', 'L3c ', 'L3d ',
    'L4a ', 'L4b ', 'L4c ', 'L4d ', 'L4e ', 'L4f ',
    'J14d-2 lzw', 'J14d-2 deflate', 'J14d-2 packbits', 'J14d-4 ', 'J14d-5 '
)
$miss = @()
foreach ($a in $anchors) {
    if ((Count-PassPrefix $allText $a) -lt 1) { $miss += $a }
}
CK ($miss.Count -eq 0) "all $($anchors.Count) defect anchors present (missing: $($miss -join ', '))"

# 7. 自检负控(-SelfCheck): 证明本门禁的判读链有牙
$ncOk = $true
if ($SelfCheck) {
    Write-Output ""
    Write-Output "===== selfcheck: UIHOST_NEGCTL=1 must turn RED ====="
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    # 逐臂验证：两臂各自的判读链都得有牙（只验一臂的话，另一臂的汇总解析器坏了也不报警）。
    $ncOk = $true
    foreach ($arm in $arms) {
        $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
        $r2 = Invoke-HostWithTimeout $exe @($srcDir, $arm.Mode) ($TimeoutSec * 1000) @{ UIHOST_NEGCTL = '1' }
        $sw2.Stop()
        $s2 = Read-HostSummary $r2.OutFile
        Write-Output ("  [selfcheck] {0}: exit={1} timedOut={2} summary={3} pass={4} fail={5} elapsed={6}s" -f `
            $arm.Mode, $r2.ExitCode, $r2.TimedOut, $s2.HasSummary, $s2.Pass, $s2.Fail, [int]$sw2.Elapsed.TotalSeconds)
        $armOk = ((-not $r2.TimedOut) -and $s2.HasSummary -and ($s2.Fail -ge 1) -and ($r2.ExitCode -ne 0))
        CK $armOk "negative-control switch on '$($arm.Mode)' => summary shows FAIL and exit code != 0"
        if (-not $armOk) { $ncOk = $false }
    }
}

# 8. 清理 + 判据
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

$ok = ($fail -eq 0) -and $allClean -and $ncOk

Write-Output ""
Write-Output ("  [info] arms={0} allClean={1} groupL={2} j14d={3} elapsed={4}s" -f `
    $arms.Count, $allClean, $lCount, $j14dCount, $elapsed)

if ($ok) {
    Write-Output "UIPARAMDEF RESULT: pass=$(($lCount + $j14dCount)) fail=0"
    exit 0
} else {
    $reason = 'unknown'
    if (-not $allClean) { $reason = 'one or more arms not clean (timeout / summary missing / exit!=0 / FAIL!=0)' }
    elseif (-not $ncOk) { $reason = 'selfcheck negative control did not turn red' }
    elseif ($fail -ne 0) { $reason = "local checks failed ($fail)" }
    Write-Output "UIPARAMDEF FAIL: $reason"
    Write-Output "UIPARAMDEF RESULT: pass=$(($lCount + $j14dCount)) fail=$($fail)"
    exit 1
}
