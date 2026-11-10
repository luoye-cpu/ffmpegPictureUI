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
# R3 来历（2026-11-09）：新增 J17 ColorConflictLabel 显示逻辑 8 条（J17a~J17h）。
#   改前 J=98（2026-10-31 迁移读数）；J17 断言写进 UiTestHost/Program.cs 的
#   GroupJ_UiSweep（改断言改单体），由 _gen-ui-split.ps1 重新切片进 ui-sweep，
#   本脚本同批抬 $jExpected 98 -> 106 并在 $anchors 登记 J17 逐条锚点。
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
# ⚠ 2026-10-31 执行面迁移：本门禁判的是 **J 族**，而 J 组只住在拆分件 `ui-sweep`
#   （组表见 tests/projects/Tests.Shared/TestGroups.cs；`Tests.UiSweep.exe <素材目录> ui-sweep`
#   只跑 J，输出契约与拆分前的 UiTestHost 逐字一致 ⇒ 下面的解析函数无需改动）。
#   迁移前它是「起一次单体跑全量 430 条、再用 [PASS] J 前缀挑自己那族」。
$exe     = Join-Path $repoRoot 'tests/projects/ui-sweep/bin/Release/net11.0/win-x64/Tests.UiSweep.exe'
$exeMode = 'ui-sweep'
if (-not (Test-Path $exe)) {
    Write-Output "  FAIL host exe exists: $exe"
    Write-Output "       build first: dotnet build tests/projects/ui-sweep/ui-sweep.csproj -c Release"
    Write-Output "UIPARAM RESULT: pass=0 fail=1"
    exit 1
}
CK $true "host exe exists"

# 素材目录(宿主默认值是写死的绝对路径; 这里显式传参, 保持可复现)
$srcDir = Join-Path $repoRoot 'tests/output/sources'
CK (Test-Path $srcDir) "source dir exists ($srcDir)"

# 3. 运行级 GUID 临时目录(固定目录会被并发同名实例互删 => 假红)
# ⚠ 落在 tests/output 下（不是 %TEMP%）：AGENTS §3 规定产物只有一份、临时件根钉在产物区，
#   受 `py tests/scripts/_sweep-output.py --runs --max-age-days` 年龄清扫管辖；跑完本脚本仍自删。
$work = Join-Path $repoRoot ('tests/output/ui-param-matrix/' + [guid]::NewGuid().ToString('N'))
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

    # ⚠ 必须 Dispose：Start-Process 的 -RedirectStandardOutput/-Error 句柄由**本进程**持有，
    #   WaitForExit 不会释放它们 ⇒ 紧接着 ReadAllText 会偶发 IOException「being used by another process」
    #   （2026-10-31 迁到 tests/output 后连中两次：exit=0 但 summary 读空 ⇒ 假红）。
    if ($null -ne $p) { try { $p.Dispose() } catch { } }
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }

    $rc = -1
    if (Test-Path $rcFile) { try { $rc = [int]([System.IO.File]::ReadAllText($rcFile).Trim()) } catch { $rc = -1 } }
    return @{ ExitCode = $rc; TimedOut = $timedOut; OutFile = $outF; ErrFile = $errF; StartError = '' }
}

# 解析宿主输出: 汇总行 + 计数。只匹配 **ASCII** 片段(`N PASS / N FAIL`),
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
Write-Output "===== run: Tests.UiSweep $exeMode (timeout=${TimeoutSec}s) ====="
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$r = Invoke-HostWithTimeout $exe @($srcDir, $exeMode) ($TimeoutSec * 1000) @{}
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
#    2026-10-31 执行面迁移时实测 = **98** 条（在 ui-sweep mode 下逐条 [PASS] J 计数）。
#    旧值是 88（2026-09-19 单体读数，已陈旧）。这里由「>= 88」收紧为 **精确等值**：
#    下界式只增不减对「等于当前实测」的断言没有任何牙 —— 少跑 10 条也仍是绿。
#    2026-11-09（R3）98 → **106**：新增 J17 ColorConflictLabel 显示逻辑 8 条
#    （J17a~J17h，UiTestHost/Program.cs GroupJ_UiSweep 末段；改断言改单体后由
#    _gen-ui-split.ps1 重新切片进 ui-sweep，本门禁与拆分件同批抬表）。
#    背景：该组件此前 verify-ui-* 零显示断言（勘察 2026-11-09），补组同批把
#    $jExpected 抬到新实测值并在 $anchors 登记 J17 逐条锚点。
#    2026-11-09（R3 同日第二批）106 → **114**：新增 J18 tone-map/engine 采集映射 8 条
#    （J18a~J18h；ColorToneMapCombo/ColorEngineCombo 两控件是 CLI 色彩开关的 GUI 承接，
#    断言载体 = CollectOptionsFromUiAsync 产出的 options 字段——这两个开关没有 ffmpeg
#    token 形态，J 组常规的命令串模式不适用，另有 J18g 命令串不变负控）。
#    2026-11-10（color-matrix-options 批 T7）114 → **122**：新增 J19 out-matrix 采集映射 8 条
#    （J19a~J19h；OutputMatrixCombo = --color-out-matrix 的 GUI 承接，C-GUI@1 基本区控件。
#    断言载体同 J18 = options.ColorOutMatrix 字段；条目经 FillComboByLoc 本地化渲染 ⇒ 按
#    SelectedIndex 语义选档（C-TOKEN@1 表序）；语义锁 = bt601→smpte170m 别名归一（J19c）、
#    不受 useAdv 门控（J19f，与 Chroma 同采集模式）、预设回放 null 早退 + bt601 归位（J19g）。
#    改断言改单体（UiTestHost/Program.cs GroupJ_UiSweep 末段）后由 _gen-ui-split.ps1 重新
#    切片进 ui-sweep，本门禁与 verify-ui-host.ps1（ui-sweep 165 / 合计 454）、
#    verify-ui-split-union.ps1（$expectedOldCount 454）同批抬表——三把尺同一来历。）
#    2026-11-11（CMX 一致性批 CMX-3）122 → **126**：J19 段按 CMX-2 能力过滤行为重写并新增
#    4 条（J19i~J19l：avif 过滤集条数与在集判定 / ycgco·fcc·xyb 被过滤判定 / jpg(cjpegli)
#    backend 感知过滤集 / 切格式失效重置 auto）。改写动因 = CMX-2 的
#    RefillOutMatrixComboForFormat 落地后旧断言全数失效（基线轮实测 ui-sweep 116/6：J19a
#    count=2 = png 过滤集、J19c-f 越界采集 null、J19g 归位 auto）。新读数 = ui-sweep mode
#    126 PASS / 0 FAIL（tests/output/_tmp/cmx3-gate/j19-after.log）；改断言改单体后
#    _gen-ui-split.ps1 重跑，verify-ui-split-reproducible 19/0（重跑两次逐字节对撞）；
#    本门禁与 verify-ui-host.ps1（ui-sweep 169 / 合计 458）、verify-ui-split-union.ps1
#    （$expectedOldCount 458）同批抬表——三把尺同一来历。）
$jExpected = 126
$jCount = Count-PassPrefix $s.Text 'J'
Write-Output "  [info] GroupJ PASS lines = $jCount (expected $jExpected)"
CK ($jCount -eq $jExpected) "GroupJ assertion count == $jExpected (actual $jCount)"

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
    'J15a ', 'J15b fps', 'J15c fps', 'J15d scaleW', 'J15e duration', 'J15f ', 'J15g WebP',
    'J17a ', 'J17b ', 'J17c ', 'J17d ', 'J17e ', 'J17f ', 'J17g ', 'J17h ',
    'J18a ', 'J18b ', 'J18c ', 'J18d ', 'J18e ', 'J18f ', 'J18g ', 'J18h ',
    'J19a ', 'J19b ', 'J19c ', 'J19d ', 'J19e ', 'J19f ', 'J19g ', 'J19h ',
    'J19i ', 'J19j ', 'J19k ', 'J19l '
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
    $r2 = Invoke-HostWithTimeout $exe @($srcDir, $exeMode) ($TimeoutSec * 1000) @{ UIHOST_NEGCTL = '1' }
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
