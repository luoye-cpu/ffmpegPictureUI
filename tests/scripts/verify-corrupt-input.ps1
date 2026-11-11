# verify-corrupt-input.ps1 — 损坏/截断输入 E2E（批 2 模块 3，docs/TEST_MODULE_PLAN_2026-11-12.md）
# 盲区#3（2026-11-12 调研：grep truncat|corrupt 于 tests 0 命中——畸形 JXL 只覆盖挂死(#40)、
#   D5 只锁 >256MB ISO-BMFF 预检）的收口门禁。
#
# 产品行为（源码钉死，本门禁端到端兑现）：
#   · HEIC/AVIF 截断：QueueProcessor.cs:685-695 进解码器前定案——iloc extent 越过 EOF ⇒
#     日志逐字「[IsoBmff] ❌ 文件不完整（截断）：…」+「[IsoBmff] ❌ 拒绝解码：…」、
#     状态逐字「失败 (文件不完整：数据区越过文件末尾)」、ExitCode=1、抛 InvalidOperationException。
#   · PNG/JXL 走 ffmpeg/djxl：exit≠0 ⇒ 状态「失败 (退出码 N)」（QueueProcessor.cs:1437，半角括号）。
#   · 半成品清理：TryDiscardPartialOutput（:2911）对失败任务删除**新建**的半成品 ⇒ 输出目录零文件。
#   · 「不回退」策略：djxl 显式失败不静默改道 ffmpeg（QueueProcessor.cs:331/643 同族口径）。
#
# 载体：真 headless exe（verify-png-optimizer 模式）。夹具现场自造 + 字节级变异：
#   截断 50%（头部合法、载荷断尾）× 载荷污染（头部后 4KB 清零、长度不变——HEIC 的 iloc extent
#   不越 EOF ⇒ 预检放行，考验解码端 fail-closed）。
# ⚠ JXL 截断臂用**魔数完好**的截断件（FF 0A 前缀保住 ⇒ djxl 腿快失败）；坏魔数的忙等挂死是
#   第 40 条 verify-jxl-probe-timeout 的领地，本门禁不重复不僭越。
# ⚠ 启动用 ProcessStartInfo(UseShellExecute=$false)：提权会话 Start-Process 弹「打开文件-安全警告」
#   模态框阻塞（2026-11-12 实测，见 AGENTS §7 GA 条目）。
param([switch]$SelfCheck, [switch]$KeepWork)
$ErrorActionPreference = 'Stop'
$scriptsDir = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptsDir)
$exe = Join-Path $repoRoot 'src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe'
$ff = Join-Path $repoRoot 'publish/PLAN/ffmpeg-full/ffmpeg.exe'
$cjxl = Join-Path $repoRoot 'publish/PLAN/jxl/bin/cjxl.exe'
$heicSrc = Join-Path $repoRoot 'tests/data/synth_heic_alpha.heic'

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
    return ($b.Length -ge 8 -and $b[0] -eq 0x89 -and $b[1] -eq 0x50 -and $b[2] -eq 0x4E -and $b[3] -eq 0x47)
}
# 判读链助手（负控被测对象）：集中定义。
function Test-SummaryFailed1([string]$text) { $text.Contains('失败 1 项') }
function Test-OutdirClean([string]$dir) { (@(Get-ChildItem $dir -File -ErrorAction SilentlyContinue)).Count -eq 0 }

foreach ($need in @($exe, $ff, $cjxl, $heicSrc)) { if (-not (Test-Path $need)) { throw "缺被测件/夹具：$need" } }

$guid = [guid]::NewGuid().ToString('N').Substring(0, 12)
$work = Join-Path $repoRoot "tests/output/corrupt_input/$guid"
New-Item -ItemType Directory -Force -Path $work | Out-Null
Write-Output "  work=$work"

# ── 夹具：三格式基件 + 两种字节级变异 ────────────────────────────────────────
function Run-Tool([string]$file, [string]$argLine) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $file
    $psi.Arguments = $argLine   # ffmpeg/cjxl 自解析命令行；不用 ArgumentList（含引号路径会被按空格拆碎）
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi); $p.WaitForExit()
    return $p.ExitCode
}
$basePng = Join-Path $work 'base.png'
[void](Run-Tool $ff "-y -f lavfi -i testsrc2=size=256x256:rate=1 -frames:v 1 `"$basePng`"")
$baseJxl = Join-Path $work 'base.jxl'
[void](Run-Tool $cjxl "`"$basePng`" `"$baseJxl`" -q 90")
$baseHeic = Join-Path $work 'base.heic'
Copy-Item $heicSrc $baseHeic
CK ((Test-Path $basePng) -and (Test-PngMagic $basePng)) "基件 PNG 现造成功（$(if (Test-Path $basePng) { (Get-Item $basePng).Length } else { 0 })B）"
CK ((Test-Path $baseJxl) -and ((Get-Item $baseJxl -ErrorAction SilentlyContinue).Length -gt 64)) "基件 JXL 现造成功（cjxl，$(if (Test-Path $baseJxl) { (Get-Item $baseJxl).Length } else { 0 })B）"
CK ((Get-Item $baseHeic).Length -gt 1000) "基件 HEIC 就位（tests/data 副本，$((Get-Item $baseHeic).Length)B）"

function Truncate-50([string]$src, [string]$dst) {
    $b = [IO.File]::ReadAllBytes($src)
    [IO.File]::WriteAllBytes($dst, $b[0..([int]($b.Length / 2))])
}
function Zero-Payload([string]$src, [string]$dst, [int]$offset) {
    $b = [IO.File]::ReadAllBytes($src)
    for ($i = $offset; $i -lt [Math]::Min($b.Length, $offset + 4096); $i++) { $b[$i] = 0 }
    [IO.File]::WriteAllBytes($dst, $b)
}
Truncate-50 $basePng (Join-Path $work 'trunc.png')
Zero-Payload $basePng (Join-Path $work 'zero.png') 33        # PNG：越过 8B 签名+25B IHDR，砸第一块 IDAT 区
Truncate-50 $baseJxl (Join-Path $work 'trunc.jxl')           # FF 0A 魔数在前 2B，50% 截断保住它
Zero-Payload $baseJxl (Join-Path $work 'zero.jxl') 8         # JXL：魔数后 4KB 清零（长度不变）
Truncate-50 $baseHeic (Join-Path $work 'trunc.heic')         # iloc extent 越过 EOF ⇒ 预检定案
Zero-Payload $baseHeic (Join-Path $work 'zero.heic') 512     # mdat 腹地清零，box 长度不变 ⇒ 预检放行

function Run-Headless([string]$tag, [string]$inputPath, [string]$outdir) {
    # ⚠ 参数名不可用 $input/$args —— PowerShell 自动变量，撞名会把实参换成枚举器/空值
    #   （2026-11-12 实测：-i 收到空值 ⇒ 应用立刻 exit 2，stdout 零字节）。
    New-Item -ItemType Directory -Force -Path $outdir | Out-Null
    $logF = Join-Path $work "$tag.log"
    $outF = Join-Path $work "$tag.stdout"
    $errF = Join-Path $work "$tag.stderr"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    foreach ($a in @('--headless', '--log-level', 'Debug', '--log-file', $logF, '-i', $inputPath, '--format', 'png', '--output', $outdir, '--png-optimizer', 'off')) { [void]$psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEnd(); $se = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    [IO.File]::WriteAllText($outF, $so); [IO.File]::WriteAllText($errF, $se)
    return @{ code = $p.ExitCode; out = $so; err = $se; outdir = $outdir; log = $logF }
}
function Get-StatusLine([string]$text) {
    return ((($text -split "`r?`n") | Select-String '→ ' | Select-Object -Last 1).Line)
}

# ── 正控三臂：完好基件必须转换成功（防「夹具坏到连正常件都失败」的假绿）────────────
$rP1 = Run-Headless 'p_png' $basePng (Join-Path $work 'out_p1')
CK ($rP1.code -eq 0 -and (Test-PngMagic (Join-Path $rP1.outdir 'base.png'))) "正控 PNG→PNG: exit=0 产物魔数在（实得 $($rP1.code)）"
$rP2 = Run-Headless 'p_jxl' $baseJxl (Join-Path $work 'out_p2')
CK ($rP2.code -eq 0 -and (Test-PngMagic (Join-Path $rP2.outdir 'base.png'))) "正控 JXL→PNG: exit=0 产物魔数在（实得 $($rP2.code)）"
$rP3 = Run-Headless 'p_heic' $baseHeic (Join-Path $work 'out_p3')
CK ($rP3.code -eq 0 -and (Test-PngMagic (Join-Path $rP3.outdir 'base.png'))) "正控 HEIC→PNG: exit=0 产物魔数在（实得 $($rP3.code)）"

# ── 损坏六臂：exit≠0 + 失败计数如实 + 输出目录零产物 ──────────────────────────
$rC1 = Run-Headless 'c_png_trunc' (Join-Path $work 'trunc.png') (Join-Path $work 'out_c1')
CK ($rC1.code -ne 0) "C1 PNG 截断: exit≠0（实得 $($rC1.code)）"
CK (Test-SummaryFailed1 $rC1.out) "C1: 汇总「失败 1 项」"
CK ((Get-StatusLine $rC1.out) -match '^.*→ 失败') "C1: 状态以失败计（实得 $(Get-StatusLine $rC1.out)）"
CK (Test-OutdirClean $rC1.outdir) "C1: 输出目录零产物（半成品被清理）"

$rC2 = Run-Headless 'c_png_zero' (Join-Path $work 'zero.png') (Join-Path $work 'out_c2')
CK ($rC2.code -ne 0) "C2 PNG 载荷清零: exit≠0（实得 $($rC2.code)）"
CK (Test-SummaryFailed1 $rC2.out) "C2: 汇总「失败 1 项」"
CK (Test-OutdirClean $rC2.outdir) "C2: 输出目录零产物"

$rC3 = Run-Headless 'c_jxl_trunc' (Join-Path $work 'trunc.jxl') (Join-Path $work 'out_c3')
CK ($rC3.code -ne 0) "C3 JXL 截断（魔数完好）: exit≠0（实得 $($rC3.code)）"
CK (Test-SummaryFailed1 $rC3.out) "C3: 汇总「失败 1 项」"
CK ((Get-StatusLine $rC3.out) -match '^.*→ 失败') "C3: 状态以失败计（实得 $(Get-StatusLine $rC3.out)）"
# 逐字锁（2026-11-12 产品修复：英文 "failed: JXL input undecodable" 曾使 HasError=false ⇒ exit=0，
#   汇总/退出码自相矛盾——D6/D7 同族漏网，由本门禁 C3/C4 当场红出）：
CK ($rC3.out.Contains('失败 (JXL 输入不可解码：djxl 探针 exit 1)')) "C3: 修复后状态逐字（失败 (JXL 输入不可解码：djxl 探针 exit 1)）"
CK (Test-OutdirClean $rC3.outdir) "C3: 输出目录零产物"

$rC4 = Run-Headless 'c_jxl_zero' (Join-Path $work 'zero.jxl') (Join-Path $work 'out_c4')
CK ($rC4.code -ne 0) "C4 JXL 载荷清零: exit≠0（实得 $($rC4.code)）"
CK (Test-SummaryFailed1 $rC4.out) "C4: 汇总「失败 1 项」"
CK (Test-OutdirClean $rC4.outdir) "C4: 输出目录零产物"

$rC5 = Run-Headless 'c_heic_trunc' (Join-Path $work 'trunc.heic') (Join-Path $work 'out_c5')
CK ($rC5.code -eq 1) "C5 HEIC 截断: exit=1（预检拒绝，实得 $($rC5.code)）"
CK ($rC5.out.Contains('[IsoBmff] ❌ 文件不完整（截断）')) "C5: 拒绝播报逐字（IsoBmff 文件不完整（截断））"
CK ($rC5.out.Contains('失败 (文件不完整：数据区越过文件末尾)')) "C5: 状态逐字（失败 (文件不完整：数据区越过文件末尾)）"
CK (Test-OutdirClean $rC5.outdir) "C5: 输出目录零产物"

$rC6 = Run-Headless 'c_heic_zero' (Join-Path $work 'zero.heic') (Join-Path $work 'out_c6')
CK ($rC6.code -ne 0) "C6 HEIC 载荷清零（extent 完好）: exit≠0（实得 $($rC6.code)，解码端 fail-closed）"
CK (Test-SummaryFailed1 $rC6.out) "C6: 汇总「失败 1 项」"
CK (Test-OutdirClean $rC6.outdir) "C6: 输出目录零产物"

# ── 负控（内存，零进程）：判读链口径 ────────────────────────────────────────
$ncOk = $true
if ($SelfCheck) {
    Write-Output "  [selfcheck] judgement-chain teeth (in-memory)"
    $ncRed = 0
    if (-not (Test-SummaryFailed1 '═══ 队列处理完毕 — 完成 1 项, 失败 1 项')) { Write-Output '    (negctl) 正串不匹配 => 判读链坏!'; $ncRed++ }
    if (Test-SummaryFailed1 '═══ 队列处理完毕 — 完成 1 项, 失败 0 项') { Write-Output '    (negctl) 失败 0 项被判成失败 1 项 => 假红方向!'; $ncRed++ }
    $fake = Join-Path $work 'fake_dirty'
    New-Item -ItemType Directory -Force -Path $fake | Out-Null
    [IO.File]::WriteAllText((Join-Path $fake 'half.png'), 'x')
    if (Test-OutdirClean $fake) { Write-Output '    (negctl) 目录有残留件被判成干净 => 零产物判据没牙!'; $ncRed++ }
    if (-not (Test-OutdirClean (Join-Path $work 'out_c5'))) { Write-Output '    (negctl) 干净目录被判成脏 => 假红方向!'; $ncRed++ }
    CK ($ncRed -eq 0) "selfcheck: judgement chain works as designed ($ncRed defects)"
    $ncOk = ($ncRed -eq 0)
}

# ── 清理 + 汇总 ─────────────────────────────────────────────────────────────
if (-not $KeepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
else { Write-Output "  [keepwork] $work" }
Write-Output ""
Write-Output "===== corrupt-input: PASS=$($script:pass) FAIL=$($script:fail) ====="
if ($script:fail -gt 0 -or -not $ncOk) { exit 1 }
exit 0
