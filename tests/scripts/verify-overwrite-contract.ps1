# verify-overwrite-contract.ps1 — 防覆盖契约 E2E（批 2 模块 1，docs/TEST_MODULE_PLAN_2026-11-12.md）
# 盲区#1（2026-11-12 调研：grep no-overwrite|AllowOverwrite 于 tests 全 0 命中）的收口门禁。
#
# 产品语义（代码钉死，本门禁端到端兑现）：
#   默认档 = 覆盖（CliParser.cs:70 AllowOverwrite=true，既有行为全链路 -y）；
#   --no-overwrite = 存在即拒绝：QueueProcessor.cs:385-400 在任何 ffmpeg 启动前 return，
#     日志逐字「[跳过] 目标文件已存在且已指定 --no-overwrite ⇒ 不覆盖：<path>」、
#     ExitCode=1、状态逐字「失败（跳过：目标已存在且未允许覆盖）」。
#     ⚠ 状态词必须以「失败」开头（QueueItem.IsFailure 按 StartsWith("失败") 计入失败与退出码，
#       写成「跳过（…）」会不计失败 + exit 0 —— 源码注释实测踩坑，本门禁负控 NC1 锁这个契约）。
#   退出码：Program.cs:373 failed==0?0:1。
#
# 形态参照 verify-png-optimizer（真 headless exe + GUID 隔离 + sha 对撞 + CK 计数）。
# ⚠ 启动用 ProcessStartInfo(UseShellExecute=$false)：提权会话 Start-Process(ShellExecute) 会弹
#   「打开文件 - 安全警告」模态框永久阻塞（2026-11-12 实测，exe 无 Zone.Identifier；见 AGENTS §7 GA 条目）。
param([switch]$SelfCheck, [switch]$KeepWork)
$ErrorActionPreference = 'Stop'
$scriptsDir = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptsDir)
$exe = Join-Path $repoRoot 'src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe'
$ff = Join-Path $repoRoot 'publish/PLAN/ffmpeg-full/ffmpeg.exe'

# 裸跑自钉 TMP（runner 起轮时钉同一根；AGENTS §4）
$roundTmp = Join-Path $repoRoot 'tests/output/_tmp/round-tmp'
New-Item -ItemType Directory -Force -Path $roundTmp | Out-Null
$env:TMP = $roundTmp; $env:TEMP = $roundTmp; $env:TMPDIR = $roundTmp
$env:FFMPEGGUI_PLAN_DIR = Join-Path $repoRoot 'publish/PLAN'

$script:pass = 0
$script:fail = 0
function CK([bool]$cond, [string]$name) {
    if ($cond) { $script:pass++; Write-Output "  PASS  $name" }
    else { $script:fail++; Write-Output "  FAIL  $name" }
}
function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($path))) -replace '-', '') } finally { $sha.Dispose() }
}
function Test-PngMagic([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    $b = [IO.File]::ReadAllBytes($path)
    if ($b.Length -lt 8) { return $false }
    return ($b[0] -eq 0x89 -and $b[1] -eq 0x50 -and $b[2] -eq 0x4E -and $b[3] -eq 0x47 -and $b[4] -eq 0x0D -and $b[5] -eq 0x0A -and $b[6] -eq 0x1A -and $b[7] -eq 0x0A)
}
# 判读链助手（负控 NC1/NC2 的被测对象）：集中定义，负控锁它们的判读口径。
function Test-MatchesSkipStatus([string]$text) { $text.Contains('失败（跳过：目标已存在且未允许覆盖）') }
function Test-ProductReplaced([string]$oldSha, [string]$newSha) { $oldSha -ne $newSha }

foreach ($need in @($exe, $ff)) { if (-not (Test-Path $need)) { throw "缺被测件：$need（先 dotnet build -c Release / 确认 publish/PLAN 在位）" } }

$guid = [guid]::NewGuid().ToString('N').Substring(0, 12)
$work = Join-Path $repoRoot "tests/output/overwrite_contract/$guid"
New-Item -ItemType Directory -Force -Path $work | Out-Null
Write-Output "  work=$work"
Write-Output "  exe=$exe"

# 素材：lavfi 现造 256x256 PNG（不消费任何在库产物）
$src = Join-Path $work 'in.png'
$pFf = New-Object System.Diagnostics.ProcessStartInfo
$pFf.FileName = $ff
$pFf.Arguments = "-y -f lavfi -i testsrc2=size=256x256:rate=1 -frames:v 1 `"$src`""
$pFf.UseShellExecute = $false; $pFf.CreateNoWindow = $true
$pF = [System.Diagnostics.Process]::Start($pFf); $pF.WaitForExit()
CK ($pF.ExitCode -eq 0 -and (Test-Path $src)) "输入 PNG 现造成功（lavfi testsrc2 256x256）"

function Run-Headless([string]$tag, [string]$outdir, [string[]]$extra) {
    New-Item -ItemType Directory -Force -Path $outdir | Out-Null
    $logF = Join-Path $work "$tag.log"
    $outF = Join-Path $work "$tag.stdout"
    $errF = Join-Path $work "$tag.stderr"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    foreach ($a in @('--headless', '--log-level', 'Debug', '--log-file', $logF, '-i', $src, '--format', 'png', '--output', $outdir, '--png-optimizer', 'off')) { [void]$psi.ArgumentList.Add($a) }
    foreach ($a in $extra) { [void]$psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEnd(); $se = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    [IO.File]::WriteAllText($outF, $so); [IO.File]::WriteAllText($errF, $se)
    return @{ code = $p.ExitCode; out = $so; err = $se; log = $logF; outdir = $outdir }
}
function Seed-Junk([string]$outdir) {
    # 判然不同的 junk 内容（非 PNG 字节）：防「恰好同字节」假绿；拒绝路径在 ffmpeg 前，junk 不会被读。
    New-Item -ItemType Directory -Force -Path $outdir | Out-Null
    $p = Join-Path $outdir 'in.png'
    [IO.File]::WriteAllText($p, "OVERWRITE-CONTRACT-JUNK-SEED-$([guid]::NewGuid().ToString('N'))")
    return (Get-Sha256 $p)
}

# ── 臂 1：--no-overwrite 遇既有产物 = 拒绝、旧产物字节不动、零改名件 ───────────────
$seed1 = Seed-Junk (Join-Path $work 'out1')
$r1 = Run-Headless 'nc_ovw' (Join-Path $work 'out1') @('--no-overwrite')
CK ($r1.code -eq 1) "臂1 no-overwrite: exit=1（实得 $($r1.code)）"
$prod1 = Join-Path $work 'out1/in.png'
CK (-not (Test-ProductReplaced $seed1 (Get-Sha256 $prod1))) "臂1: 旧产物字节不动（sha 对撞相等）"
CK ((@(Get-ChildItem (Join-Path $work 'out1') -File)).Count -eq 1) "臂1: 目录内无第二文件（无自动改名件）"
CK ($r1.out.Contains('[跳过] 目标文件已存在且已指定 --no-overwrite ⇒ 不覆盖：')) "臂1: 拒绝播报逐字在 stdout"
CK (Test-MatchesSkipStatus $r1.out) "臂1: 状态逐字「失败（跳过：目标已存在且未允许覆盖）」"
CK ($r1.out.Contains('失败 1 项')) "臂1: 汇总如实计入失败（失败 1 项）"
CK ($r1.out.Contains('不擅自删除')) "臂1: 既有文件不清理兜底播报在（不擅自删除）"

# ── 臂 2：--overwrite 真覆盖 ─────────────────────────────────────────────────
$seed2 = Seed-Junk (Join-Path $work 'out2')
$r2 = Run-Headless 'ow_yes' (Join-Path $work 'out2') @('--overwrite')
$prod2 = Join-Path $work 'out2/in.png'
CK ($r2.code -eq 0) "臂2 overwrite: exit=0（实得 $($r2.code)）"
CK (Test-ProductReplaced $seed2 (Get-Sha256 $prod2)) "臂2: junk 被真替换（sha 对撞不等）"
CK (Test-PngMagic $prod2) "臂2: 产物是合法 PNG（魔数在）"
CK ($r2.out.Contains('失败 0 项')) "臂2: 汇总失败 0 项"

# ── 臂 3：默认档钉死 = 覆盖（防默认被「顺手」改成保护而无人知晓）─────────────────
$seed3 = Seed-Junk (Join-Path $work 'out3')
$r3 = Run-Headless 'ow_def' (Join-Path $work 'out3') @()
$prod3 = Join-Path $work 'out3/in.png'
CK ($r3.code -eq 0) "臂3 默认档: exit=0（实得 $($r3.code)）"
CK (Test-ProductReplaced $seed3 (Get-Sha256 $prod3)) "臂3: 默认档真覆盖（默认=覆盖契约钉死）"
CK (Test-PngMagic $prod3) "臂3: 产物是合法 PNG（魔数在）"
CK ($r3.out.Contains('失败 0 项')) "臂3: 汇总失败 0 项"

# ── 退出码对账（两尺互证：进程码 ↔ stdout 失败计数）────────────────────────────
CK (($r1.code -eq 1) -and ($r2.code -eq 0) -and ($r3.code -eq 0)) "退出码三臂对账（1/0/0）"

# ── 负控（内存，零进程）─────────────────────────────────────────────────────
#   NC1：判读链对「跳过（…）」变体必须不匹配 —— 若未来有人把产品状态词松成「跳过（…）」
#        （源码注释警告过的坑：不计失败 + exit 0），本门禁的 Test-MatchesSkipStatus 必须红。
#   NC2：sha 判读两个方向都得住（same ⇒ 未替换；diff ⇒ 替换），防「恰好同字节」假绿。
$ncOk = $true
if ($SelfCheck) {
    Write-Output "  [selfcheck] judgement-chain teeth (in-memory)"
    $ncRed = 0
    if (-not (Test-MatchesSkipStatus '→ 失败（跳过：目标已存在且未允许覆盖）')) {
        Write-Output '    (negctl) 正串不匹配 => 判读链坏!'; $ncRed++
    }
    if (Test-MatchesSkipStatus '→ 跳过（目标已存在且未允许覆盖）') {
        Write-Output '    (negctl) 「跳过（…）」变体被放过 => 失败前缀契约失守（QueueProcessor.cs:392 注释坑）!'; $ncRed++
    }
    if (Test-ProductReplaced 'AA' 'AA') { Write-Output '    (negctl) sha same 判成替换 => 假绿方向!'; $ncRed++ }
    if (-not (Test-ProductReplaced 'AA' 'BB')) { Write-Output '    (negctl) sha diff 判成未替换 => 假红方向!'; $ncRed++ }
    CK ($ncRed -eq 0) "selfcheck: judgement chain works as designed ($ncRed defects)"
    # 期望：4 项自证全按设计工作（$ncRed==0）；若有人改判读口径 ⇒ $ncOk=false ⇒ 整体红。
    $ncOk = ($ncRed -eq 0)
}

# ── 清理 + 汇总 ─────────────────────────────────────────────────────────────
if (-not $KeepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
else { Write-Output "  [keepwork] $work" }
Write-Output ""
Write-Output "===== overwrite-contract: PASS=$($script:pass) FAIL=$($script:fail) ====="
if ($script:fail -gt 0 -or -not $ncOk) { exit 1 }
exit 0
