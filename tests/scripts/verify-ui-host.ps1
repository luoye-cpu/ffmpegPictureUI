# verify-ui-host.ps1 -- UiTestHost 门禁入口 (2026-09-19 新增, P1-A)
#
# 为什么需要这一条:
#   `tests/projects/UiTestHost` 是 Avalonia.Headless 宿主, 加载**真实 MainWindow** 并逐条断言
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
# ⚠ 2026-10-31 执行面迁移：单体 `UiTestHost.exe` → 5 个拆分件，逐件跑**该组全部 mode**
#   （argv 只给素材目录、不给 mode ⇒ 宿主跑整组，见 tests/projects/Tests.Shared/TestGroups.cs）。
#   迁移前的判据是「单体一组汇总行 430 PASS / 0 FAIL」；拆分后变成 5 条汇总行 ⇒ 必须
#   ① 每件都有汇总行且 FAIL=0/exit=0/不超时；② 逐件条数精确；③ 合计锁 430。
#   只做 ②③ 而不做 ① 的话，某件静默没跑（汇总行缺失）也会因合计被别的件补上而变绿？
#   不会——②是精确等值，缺一条就红。但 ① 仍保留：它区分「跑了但少几条」与「根本没跑」。
# ⚠ 2026-11-09（R3）：J 组增 J17(8)+J18(8)=16 条 ⇒ ui-sweep 141 → **157**（=J114+L43）、
#   合计 430 → **446**（=75+53+89+157+72）。与 verify-ui-param-matrix.ps1 的 $jExpected=114、
#   verify-ui-split-union.ps1 的 $expectedOldCount=446 同批抬表（三把尺同一来历）。
# ⚠ 2026-11-10（color-matrix-options 批 T7）：J 组增 J19(8)（out-matrix 采集映射，
#   OutputMatrixCombo = --color-out-matrix 的 GUI 承接）⇒ ui-sweep 157 → **165**（=J122+L43）、
#   合计 446 → **454**（=75+53+89+165+72）。与 verify-ui-param-matrix.ps1 的 $jExpected=122、
#   verify-ui-split-union.ps1 的 $expectedOldCount=454 同批抬表（三把尺同一来历）。
# ⚠ 2026-11-11（CMX 一致性批 CMX-3）：J19 段按 CMX-2 能力过滤行为重写并新增 4 条（J19i~J19l）
#   ⇒ ui-sweep 165 → **169**（=J126+L43）、合计 454 → **458**（=75+53+89+169+72）。改写动因 =
#   CMX-2 的 RefillOutMatrixComboForFormat 落地后旧「11 档/固定 index」断言全数失效（基线轮
#   实测 ui-sweep 116/6）；新读数 = ui-sweep mode 126 PASS / 0 FAIL（J126，实测日志
#   tests/output/_tmp/cmx3-gate/j19-after.log）。与 verify-ui-param-matrix.ps1 的
#   $jExpected=126、verify-ui-split-union.ps1 的 $expectedOldCount=458 同批抬表（三把尺同一来历）。
# ⚠ 2026-11-12（测试模块化批 2 模块 2，取消路径 G2）：G 组增 G2(6)（慢任务停止：点停止→已停止
#   +子进程死+无半截产物+无已完成项，UiTestHost/Program.cs GroupG）⇒ ui-mode 53 → **59**（=G 组旧值+6）、
#   合计 458 → **464**（=75+59+89+169+72）。与 verify-ui-param-matrix.ps1 的 $jExpected=126（J 组不动）、
#   verify-ui-split-union.ps1 的 $expectedOldCount=464、_module-registry.json locks 同批抬表（三把尺同一来历）。
$arms = @(
    @{ Name = 'ui-core';  Exe = Join-Path $repoRoot 'tests/projects/ui-core/bin/Release/net11.0/win-x64/Tests.UiCore.exe';  Expected = 75  },
    @{ Name = 'ui-mode';  Exe = Join-Path $repoRoot 'tests/projects/ui-mode/bin/Release/net11.0/win-x64/Tests.UiMode.exe';  Expected = 59  },
    @{ Name = 'ui-color'; Exe = Join-Path $repoRoot 'tests/projects/ui-color/bin/Release/net11.0/win-x64/Tests.UiColor.exe'; Expected = 89  },
    @{ Name = 'ui-sweep'; Exe = Join-Path $repoRoot 'tests/projects/ui-sweep/bin/Release/net11.0/win-x64/Tests.UiSweep.exe'; Expected = 169 },
    @{ Name = 'ui-cli';   Exe = Join-Path $repoRoot 'tests/projects/ui-cli/bin/Release/net11.0/win-x64/Tests.UiCli.exe';     Expected = 72  }
)
$aggExpected = 464
foreach ($arm in $arms) {
    if (-not (Test-Path $arm.Exe)) {
        Write-Output "  FAIL host exe exists ($($arm.Name)): $($arm.Exe)"
        Write-Output "       build first: dotnet build ffmpegPictureUI.sln -c Release"
        Write-Output "UIHOST RESULT: pass=0 fail=1"
        exit 1
    }
}
CK $true "host exe exists (5 拆分件: ui-core / ui-mode / ui-color / ui-sweep / ui-cli)"

# 素材目录(宿主默认值是写死的绝对路径; 这里显式传参, 保持可复现)
$srcDir = Join-Path $repoRoot 'tests/output/sources'
CK (Test-Path $srcDir) "source dir exists ($srcDir)"

# ── 3. 运行级 GUID 临时目录(固定目录会被并发同名实例互删 => 假红, §7.9) ────────
# ⚠ 落在 tests/output 下（不是 %TEMP%，AGENTS §3）：本机沙箱按进程拦 %TEMP% 写 ⇒
#   宿主/ffmpeg 会批量 exit -13 假红；同时产物只有一份，全部留在产物区受 --runs 清扫管辖。
$work = Join-Path $repoRoot ('tests/output/ui-host/' + [guid]::NewGuid().ToString('N'))
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

    # ⚠ 必须 Dispose：Start-Process 的 -RedirectStandardOutput/-Error 句柄由**本进程**持有，
    #   WaitForExit 不会释放它们 ⇒ 紧接着 ReadAllText 会偶发 IOException「being used by another process」
    #   （2026-10-31 迁到 tests/output 后连中两次：exit=0 但 summary 读空 ⇒ 假红）。
    if ($null -ne $p) { try { $p.Dispose() } catch { } }
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }

    $rc = -1
    if (Test-Path $rcFile) { try { $rc = [int]([System.IO.File]::ReadAllText($rcFile).Trim()) } catch { $rc = -1 } }
    return @{ ExitCode = $rc; TimedOut = $timedOut; OutFile = $outF; ErrFile = $errF; StartError = '' }
}

# 解析宿主输出: 汇总行 + 计数。⚠ 只匹配 **ASCII** 片段(`N PASS / N FAIL`),
# 不匹配中文, 以免宿主/宿主的输出编码差异导致静默零命中。
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

# ── 5. 主跑（逐件，5 臂） ──────────────────────────────────────────────────────
$allClean  = $true
$aggPass   = 0
$aggFail   = 0
$elapsed   = 0
foreach ($arm in $arms) {
    Write-Output ""
    Write-Output ("===== run: {0} 全组 mode (timeout=${TimeoutSec}s) =====" -f $arm.Name)
    $swA = [System.Diagnostics.Stopwatch]::StartNew()
    $ra  = Invoke-HostWithTimeout $arm.Exe @($srcDir) ($TimeoutSec * 1000) @{}
    $swA.Stop()
    $armSec = [int]$swA.Elapsed.TotalSeconds
    $elapsed += $armSec

    if ($ra.StartError -ne '') {
        Write-Output "  FAIL start child host: $($ra.StartError)"
        CK $false "$($arm.Name) child host started ($($ra.StartError))"
        $allClean = $false
        continue
    }
    CK (-not $ra.TimedOut) "$($arm.Name) no timeout (elapsed ${armSec}s, limit ${TimeoutSec}s)"

    $sa = Read-HostSummary $ra.OutFile
    # ⚠ 超时击杀后**抑制汇总解析**: 被杀的进程留下半截输出, 若其中恰有汇总行
    #   => 「挂死」会被当成「跑完了且干净」。改为打现场(末 5 行)做诊断。
    if ($ra.TimedOut) {
        $tail = ''
        if (Test-Path $ra.OutFile) { $tail = ((Get-Content $ra.OutFile -Tail 5 -ErrorAction SilentlyContinue) -join "`n") }
        $sa = @{ Text = "[TIMEOUT] summary parsing suppressed; tail:`n$tail"; HasSummary = $false; Pass = -1; Fail = -1 }
    }
    Write-Output "----- $($arm.Name) stdout -----"
    if ($sa.Text -ne '') { Write-Output $sa.Text } else { Write-Output "(empty)" }
    $errText = Read-TextShared $ra.ErrFile
    if ($errText.Trim() -ne '') { Write-Output "----- stderr -----"; Write-Output $errText }
    Write-Output "----- end $($arm.Name) -----"

    # ① 汇总行必须存在（exit 0 单独不算数：宿主可能压根没跑到汇总点）
    CK ($sa.HasSummary) "$($arm.Name) summary line present (exit 0 alone is not enough)"
    CK ($sa.HasSummary -and $sa.Fail -eq 0) "$($arm.Name) summary FAIL=0"
    CK ($ra.ExitCode -eq 0) "$($arm.Name) exit code == 0 (actual $($ra.ExitCode))"
    # ② 逐件条数**精确等值**（下界式对「等于当前实测」的集合没有牙：少跑 N 条仍绿）
    CK ($sa.HasSummary -and $sa.Pass -eq $arm.Expected) `
       "$($arm.Name) assertion count == $($arm.Expected) (actual $($sa.Pass))"

    if (-not ($sa.HasSummary -and ($sa.Fail -eq 0) -and ($ra.ExitCode -eq 0) -and (-not $ra.TimedOut))) { $allClean = $false }
    if ($sa.Pass -gt 0) { $aggPass += $sa.Pass }
    if ($sa.Fail -gt 0) { $aggFail += $sa.Fail }
}

# ③ 合计锁：迁移前单体一条汇总行 = 430；拆分后逐件合计必须**仍是 430**。
#    这条挡的是「逐件都绿、但整体覆盖面缩水」——逐件条数锁已覆盖绝大部分，
#    合计锁是第二道：若有人同时改小某件期望值又删了断言，单看逐件会自洽而合计露馅。
Write-Output ""
Write-Output "  [info] aggregate: $aggPass PASS / $aggFail FAIL (expected $aggExpected PASS)"
CK ($aggPass -eq $aggExpected) "aggregate assertion count == $aggExpected (actual $aggPass)"

# ── 6. 自检负控(-SelfCheck): 证明本门禁的判读链有牙 ──────────────────────────
$ncOk = $true
if ($SelfCheck) {
    Write-Output ""
    Write-Output "===== selfcheck: UIHOST_NEGCTL=1 must turn RED ====="
    # 逐件验证：5 个拆分件各自的判读链都得有牙。只验一件的话，其余四件的汇总解析器
    # 坏了也不报警（而这条门禁的产出正是「5 条汇总行」）。
    $ncOk = $true
    foreach ($arm in $arms) {
        $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
        $r2 = Invoke-HostWithTimeout $arm.Exe @($srcDir) ($TimeoutSec * 1000) @{ UIHOST_NEGCTL = '1' }
        $sw2.Stop()
        $s2 = Read-HostSummary $r2.OutFile
        Write-Output ("  [selfcheck] {0}: exit={1} timedOut={2} summary={3} pass={4} fail={5} elapsed={6}s" -f `
            $arm.Name, $r2.ExitCode, $r2.TimedOut, $s2.HasSummary, $s2.Pass, $s2.Fail, [int]$sw2.Elapsed.TotalSeconds)
        $armOk = ((-not $r2.TimedOut) -and $s2.HasSummary -and ($s2.Fail -ge 1) -and ($r2.ExitCode -ne 0))
        CK $armOk "negative-control switch on '$($arm.Name)' => summary shows FAIL and exit code != 0"
        if (-not $armOk) { $ncOk = $false }
    }
}

# ── 7. 清理 + 判据 ────────────────────────────────────────────────────────────
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

$ok = ($fail -eq 0) -and $allClean -and $ncOk -and ($aggPass -eq $aggExpected)

Write-Output ""
Write-Output ("  [info] arms={0} allClean={1} aggregate={2}/{3} elapsed={4}s" -f `
    $arms.Count, $allClean, $aggPass, $aggExpected, $elapsed)

if ($ok) {
    Write-Output "UIHOST RESULT: pass=$aggPass fail=0"
    exit 0
} else {
    $reason = 'unknown'
    if (-not $allClean) { $reason = 'one or more split hosts not clean (timeout / summary missing / exit!=0 / FAIL!=0)' }
    elseif ($aggPass -ne $aggExpected) { $reason = "aggregate $aggPass != $aggExpected" }
    elseif (-not $ncOk) { $reason = 'selfcheck negative control did not turn red' }
    elseif ($fail -ne 0) { $reason = "local checks failed ($fail)" }
    Write-Output "UIHOST FAIL: $reason"
    Write-Output "UIHOST RESULT: pass=$aggPass fail=$aggFail"
    exit 1
}
