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
# ⚠ 2026-10-31 执行面迁移：本门禁的断言跨**两个 exe、三个 mode**（见下面的 $arms）——
#   迁移前它是「起一次单体跑全量 430 条、再用 [PASS] 前缀挑 M / B1..B3」。
#   M 族住 ui-cli 的 `ui-cli-strict` mode；B1/B2/B3 三条「中间产物不得落在 SrcDir」的守卫
#   原先跟随 H / I 两族（它们的断言体在 UiHost 的 H/I 段里），而拆分后
#   `ui-color/Program.cs:61-62` 把 H → mode `ui-color`、I → mode `ui-icc` ⇒ 必须跑第三个 mode。
$exeCli   = Join-Path $repoRoot 'tests/projects/ui-cli/bin/Release/net11.0/win-x64/Tests.UiCli.exe'
$exeColor = Join-Path $repoRoot 'tests/projects/ui-color/bin/Release/net11.0/win-x64/Tests.UiColor.exe'
foreach ($e in @($exeCli, $exeColor)) {
    if (-not (Test-Path $e)) {
        Write-Output "  FAIL host exe exists: $e"
        Write-Output "       build first: dotnet build ffmpegPictureUI.sln -c Release"
        Write-Output "CLISTRICT RESULT: pass=0 fail=1"
        exit 1
    }
}
CK $true "host exe exists (ui-cli + ui-color)"

# 素材目录(宿主默认值是写死的绝对路径; 这里显式传参, 保持可复现)
$srcDir = Join-Path $repoRoot 'tests/output/sources'
CK (Test-Path $srcDir) "source dir exists ($srcDir)"

# 3. 运行级 GUID 临时目录(固定目录会被并发同名实例互删 => 假红)
# ⚠ 本机沙箱按进程拦 ffmpeg/宿主写 %TEMP%（`Permission denied` ⇒ exit -13）⇒ 工作目录与 TMP
#   一起放进仓库产物区；否则 H11 等依赖 TempDir 的臂会批量假红（2026-10-25 实测）。
$work = Join-Path $repoRoot ('tests/output/clistrict_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$env:TMP = $work; $env:TEMP = $work; $env:TMPDIR = $work

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

# 5. 主跑（**三臂**：本组判据跨两个 exe / 三个 mode）
#    实测条数（2026-10-31）：ui-cli-strict 60、ui-color 15、ui-icc 12 ⇒ 合并 87。
#    必须把三臂 stdout **拼起来**再做锚点判定：任一臂汇总缺失 / FAIL!=0 / exit!=0 / 超时都算红。
$arms = @(
    @{ Exe = $exeCli;   Mode = 'ui-cli-strict'; Label = 'M 族 (CLI 严格模式 60 条)' },
    @{ Exe = $exeColor; Mode = 'ui-color';     Label = 'H 族 + B1 守卫 (15 条)' },
    @{ Exe = $exeColor; Mode = 'ui-icc';       Label = 'I 族 + B2/B3 守卫 (12 条)' }
)
$merged    = New-Object System.Text.StringBuilder
$allClean  = $true
$armPasses = 0
$elapsed   = 0
foreach ($arm in $arms) {
    Write-Output ""
    Write-Output ("===== run: {0} {1} — {2} (timeout=${TimeoutSec}s) =====" -f
        (Split-Path $arm.Exe -Leaf), $arm.Mode, $arm.Label)
    $swA = [System.Diagnostics.Stopwatch]::StartNew()
    $ra  = Invoke-HostWithTimeout $arm.Exe @($srcDir, $arm.Mode) ($TimeoutSec * 1000) @{}
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
    if ($sa.Pass -gt 0) { $armPasses += $sa.Pass }
    [void]$merged.AppendLine($sa.Text)
}
$allText = $merged.ToString()

# 6. 锚点在册: 挡住「GroupM / B1-B3 被删掉/改名/整段注释掉」的静默退化。
#    条数自 2026-10-31 起为**精确等值**：M 族 60（2026-09-19 预期 58，之后又增 2 条；
#    本批在 `ui-cli-strict` mode 下实测确认 60）。下界式（>= N）对「等于当前实测」的集合没有牙：
#    少跑 N 条仍是绿 —— 那正是执行面迁移最容易出的退化，故一并收紧。
$mExpected = 60
$mCount = Count-PassPrefix $allText 'M'
Write-Output "  [info] GroupM PASS lines = $mCount (expected $mExpected)"
CK ($mCount -eq $mExpected) "GroupM assertion count == $mExpected (actual $mCount)"

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
    if ((Count-PassPrefix $allText $a) -lt 1) { $miss += $a }
}
CK ($miss.Count -eq 0) "all $($anchors.Count) strict-mode anchors present (missing: $($miss -join ', '))"

# 7. 任务B 的外部验收: 宿主跑完后共享语料目录不得出现任何 _*.png。
#    为什么不信宿主自报: 宿主自己的 M4b 是"它自己的视角"; 这一条从**外部**独立取证,
#    而且它同时也覆盖了「宿主被超时杀掉后留下垃圾」的窗口(那时 M4 根本没跑到)。
#    ⚠ 覆盖面：这条只对**本门禁实际跑过的三个 mode**取证（ui-cli-strict / ui-color / ui-icc），
#      不是「整台宿主的任何 mode 都不落残留」——后者需要逐 mode 起一次，属独立一批。
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
    # 逐臂验证：三条臂各自的判读链都得有牙（只验一臂的话，另两臂的汇总解析器坏了也不报警）。
    $ncOk = $true
    foreach ($arm in $arms) {
        $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
        $r2 = Invoke-HostWithTimeout $arm.Exe @($srcDir, $arm.Mode) ($TimeoutSec * 1000) @{ UIHOST_NEGCTL = '1' }
        $sw2.Stop()
        $s2 = Read-HostSummary $r2.OutFile
        Write-Output ("  [selfcheck] {0}: exit={1} timedOut={2} summary={3} pass={4} fail={5} elapsed={6}s" -f `
            $arm.Mode, $r2.ExitCode, $r2.TimedOut, $s2.HasSummary, $s2.Pass, $s2.Fail, [int]$sw2.Elapsed.TotalSeconds)
        $armOk = ((-not $r2.TimedOut) -and $s2.HasSummary -and ($s2.Fail -ge 1) -and ($r2.ExitCode -ne 0))
        CK $armOk "negative-control switch on '$($arm.Mode)' => summary shows FAIL and exit code != 0"
        if (-not $armOk) { $ncOk = $false }
    }
}

# 9. 清理 + 判据
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

$ok = ($fail -eq 0) -and $allClean -and $ncOk

Write-Output ""
Write-Output ("  [info] arms={0} allClean={1} armPasses={2} groupM={3} leftovers={4} elapsed={5}s" -f `
    $arms.Count, $allClean, $armPasses, $mCount, $leftovers.Count, $elapsed)

if ($ok) {
    Write-Output "CLISTRICT RESULT: pass=$armPasses fail=0"
    exit 0
} else {
    $reason = 'unknown'
    if (-not $allClean) { $reason = 'one or more arms not clean (timeout / summary missing / exit!=0 / FAIL!=0)' }
    elseif ($leftovers.Count -ne 0) { $reason = "shared corpus dir polluted ($($leftovers.Count) files)" }
    elseif (-not $ncOk) { $reason = 'selfcheck negative control did not turn red' }
    elseif ($fail -ne 0) { $reason = "local checks failed ($fail)" }
    Write-Output "CLISTRICT FAIL: $reason"
    Write-Output "CLISTRICT RESULT: pass=$armPasses fail=$fail"
    exit 1
}
