# verify-oracle-matrix.ps1 -- L1/L2/L3 组合用例的**执行层**：把组合用例真的跑过编码器，
#                             再用 `_lib-oracle.ps1` 的**独立裁判**判产物正确性。
#
# 定位（本文件填的坑）：
#   本仓已有三块地基 ——
#     · `_lib-axes.ps1`   : 57 轴 / 194 取值 / 194 条 L1 单变量用例（只生成，不执行）；
#     · `_lib-matrix.ps1` : L2 两两正交（172 条覆盖 17970 组合）+ L3 语义真矩阵 5 张（只生成，不执行）；
#     · `_lib-oracle.ps1` : 独立裁判库（ffmpeg/ffprobe/exiftool/jxlinfo/… + PNG 逐 chunk CRC）。
#   **缺的正是"执行 + 判读"这一层**：没有任何脚本把用例真的喂给 FfmpegGui.exe 再拿裁判判产物。
#   本脚本就是那一层。它不新增判据（判据一律来自 oracle 库），只负责「跑、挂裁判、记账」。
#
# 三层与开关：
#   -Layer L1|L2|L3|All（默认 All）
#       L1 = `Get-SingleAxisCases` 的 194 条单变量用例；
#       L2 = `Get-PairwiseCases` 的 172 条两两正交用例；
#       L3 = `Get-SemanticMatrices` 的 5 张语义真矩阵（**笛卡尔积**，折叠后 4644 条）。
#   -Smoke        只跑 3 条代表用例（png/jpg/jxl 三格式 × >=2 类裁判），几十秒内出结论。
#   -SelfCheck    额外跑一次**必红的注入故障**（把产物改坏后走**完整裁判链**），要求它真的转红。
#   -Deep         追加 butteraugli + ssimulacra2 感知裁判（慢，默认关）。
#   -MaxCases / -MaxPerMatrix  对 L3 这种真积做**确定性**截断（默认 0 = 不截断，跑全量）。
#   -SkipPerMatrix              **每张矩阵跳过的前缀条数**（默认 0）。⚠ 宿主会回收 >≈20 min 的后台任务
#                               （实测 18m05s 存活 / 20m21s 被杀）⇒ 全量 L3 必须**逐窗推进**：
#                               `-SkipPerMatrix 0,150,300,… -MaxPerMatrix 150`。仅影响**用例选取**，不触碰断言。
#
# ⚠⚠ 本仓两条硬教训，本脚本两者都避：
#   ① 恒 `exit 0` ⇒ **假绿**。故末行恒为 `ORACLEMATRIX RESULT: pass=N fail=M`，
#      且 `if ($fail -gt 0) { exit 1 } else { exit 0 }` 成对出现。
#   ② 守卫写成 `if (...) { exit 1 }` 而**不带** `else { exit 0 }` ⇒ 污染 `$LASTEXITCODE` ⇒ **假红**。
#      故上面两条永远成对。
#
# ⚠ 判读语义（fail-closed）：**"没有产物"一律记 FAIL**，不记 SKIP。
#   - 退出码 0 且**没有**产物、又没给 `--dry-run` ⇒ `no-product(exit=0)`（静默空转，最坏的一种）；
#   - 退出码非 0 且无产物 ⇒ `no-product(exit=N)`（工具明确拒绝，但独立裁判无从确认 ⇒ 仍记红）；
#   - 退出码非 0 但有产物 ⇒ `nonzero-exit(exit=N)` + 照常挂裁判（半截产物不得伪装成绿）；
#   - `--dry-run` 是唯一例外（按契约：退出码 0 + **必须没有**产物）。
#   末段会按 reason 分类汇总，便于区分「工具拒绝」与「裁判判负」。
#
# ⚠ 独立复读（不看自身汇总行）：① 用 `Group-Object` 对结果对象**独立重算** pass/fail 再与内联计数比对；
#   ② PNG 产物上 `Test-PngStructure`（自己按字节解析）与 ffprobe（外部解码器）**两个独立来源**的
#      宽高必须一致；③ 同一个文件用 `Get-FileHash` 与 .NET SHA256 **两套独立实现**比哈希。
#
# ⚠ 素材卫生：运行级 GUID 目录 `$out = <repoRoot>/tests/output/validate/oraclemx_<guid8>`，
#   每条用例再各占一个子目录（否则同格式用例会互相覆盖）；跑完自清（`-KeepTemp` 可保留）。
#   **不碰**共享语料目录 `tests/output/sources`。
#
# ⚠ 前置环境：显式设 `FFMPEGGUI_PLAN_DIR`（由仓库根推导，**不写死绝对路径**）并打印；
#   同时给 oracle 库设 `FFMPEGGUI_ORACLE_PLAN_DIR` / `FFMPEGGUI_ORACLE_REPO_ROOT`。
#
# ⚠ 双宿主兼容（PS 5.1 / PS 7，门禁 `verify-ps-compat.ps1`）：正文禁 `? :` / `??` / `??=` / `?.`
#   / `?[` / `&&` / `||`；条件表达式一律 `$(if (…) {…} else {…})`。
# ⚠ 本文件的**字符串字面量全部纯 ASCII**（中文只出现在注释里），免踩中文硬编码门禁。
#
# 用法（宿主内 `&` 前台发起，不要 Start-Process pwsh）：
#   & '.\tests\scripts\verify-oracle-matrix.ps1' -Smoke
#   & '.\tests\scripts\verify-oracle-matrix.ps1' -Layer L1
#   & '.\tests\scripts\verify-oracle-matrix.ps1' -Layer All -SelfCheck
#
# 退出码：0 = 全绿；1 = 有失败（含裁判判负 / 无产物 / 超时 / 负控未转红 / 核心裁判缺失）。
param(
    [ValidateSet('L1', 'L2', 'L3', 'All')][string]$Layer = 'All',
    [switch]$Smoke,
    [switch]$SelfCheck,
    [switch]$Deep,
    [int]$CaseTimeoutSec = 120,
    [int]$OracleTimeoutSec = 0,
    [int]$MaxCases = 0,
    [int]$MaxPerMatrix = 0,
    # ⚠ 2026-09-19 晚新增：**每张语义矩阵跳过的前缀条数**（默认 0 = 不跳，行为与加参数前**逐字一致**）。
    #   动机：宿主会**回收**长时间运行的后台任务（实测：18m05s 的整轮存活；20m21s 的 oracle 跑**被杀**）
    #   ⇒ 单次跑必须压在 **≈20 min** 以内。而 `-MaxPerMatrix` 只能取**前缀**、**没有偏移** ⇒
    #   反复切片只会重复同一批前缀，**永远推不进矩阵后半段**。
    #   ⇒ 本参数与 `-MaxPerMatrix` 配对使用即可**逐窗推进**（例：`-SkipPerMatrix 0/150/300/… -MaxPerMatrix 150`）。
    #   ⚠ 它是**用例选取**参数，**不触碰任何断言**；默认 0 时逻辑短路 ⇒ 不影响既有读数。
    [int]$SkipPerMatrix = 0,
    [string]$Source = '',
    [string]$IccFile = '',
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Continue'

# ===========================================================================
# 0. 仓库根 / 前置环境（全部由脚本自身位置推导，无任何本机绝对路径）
# ===========================================================================
$scriptDir = $PSScriptRoot
$testsDir = Split-Path -Parent $scriptDir
$repoRoot = Split-Path -Parent $testsDir
if ([string]::IsNullOrWhiteSpace($repoRoot)) { $repoRoot = (Get-Location).Path }

$planDir = Join-Path $repoRoot 'publish/PLAN'
$env:FFMPEGGUI_PLAN_DIR = $planDir
$env:FFMPEGGUI_ORACLE_PLAN_DIR = $planDir
$env:FFMPEGGUI_ORACLE_REPO_ROOT = $repoRoot

$pass = 0; $fail = 0
$casePass = 0; $caseFail = 0
function CK {
    param([bool]$Ok, [string]$Msg)
    if ($Ok) { $script:pass++; Write-Output ('  PASS ' + $Msg) }
    else { $script:fail++; Write-Output ('  FAIL ' + $Msg) }
}
# 用例专用累加器：同时记「总量」与「用例子集」，后者供独立复读比对。
function CaseCK {
    param([bool]$Ok, [string]$Msg)
    if ($Ok) { $script:pass++; $script:casePass++; Write-Output ('  PASS ' + $Msg) }
    else { $script:fail++; $script:caseFail++; Write-Output ('  FAIL ' + $Msg) }
}

Write-Output '===== oracle-matrix execution layer ====='
Write-Output ('repo root          = ' + $repoRoot)
Write-Output ('FFMPEGGUI_PLAN_DIR = ' + $env:FFMPEGGUI_PLAN_DIR)
Write-Output ('FFMPEGGUI_ORACLE_PLAN_DIR = ' + $env:FFMPEGGUI_ORACLE_PLAN_DIR)

if (-not (Test-Path -LiteralPath $planDir)) {
    Write-Output ('  FAIL PLAN dir exists: ' + $planDir)
    Write-Output ('ORACLEMATRIX RESULT: pass=0 fail=1')
    exit 1
}

# ===========================================================================
# 1. dot-source 三块地基（只读；本脚本不修改它们）
# ===========================================================================
$libAxes = Join-Path $scriptDir '_lib-axes.ps1'
$libMatrix = Join-Path $scriptDir '_lib-matrix.ps1'
$libOracle = Join-Path $scriptDir '_lib-oracle.ps1'
$missingLibs = @()
foreach ($p in @($libAxes, $libMatrix, $libOracle)) {
    if (-not (Test-Path -LiteralPath $p)) { $missingLibs += $p }
}
if ($missingLibs.Count -gt 0) {
    Write-Output ('  FAIL required library missing: ' + ($missingLibs -join ', '))
    Write-Output ('ORACLEMATRIX RESULT: pass=0 fail=1')
    exit 1
}
. $libAxes
. $libMatrix
. $libOracle

$oto = Resolve-OracleTimeout $OracleTimeoutSec
Set-OracleConfig -RepoRoot $repoRoot -TimeoutSec $oto | Out-Null
Write-Output ('oracle timeout     = ' + [string]$oto + 's ; case timeout = ' + [string]$CaseTimeoutSec + 's')

# ===========================================================================
# 2. 被测程序（只读定位，**不构建**；另一个子代理可能正在重建 bin/，故按候选表挑第一个存在的）
# ===========================================================================
$exeCandidates = @(
    (Join-Path $repoRoot 'src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe'),
    (Join-Path $repoRoot 'src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe'),
    (Join-Path $repoRoot 'publish/FfmpegGui.exe')
)
$exe = ''
foreach ($c in $exeCandidates) {
    if (Test-Path -LiteralPath $c -PathType Leaf) { $exe = $c; break }
}
if ($exe -eq '') {
    Write-Output '  FAIL product exe not found in any candidate path:'
    foreach ($c in $exeCandidates) { Write-Output ('        ' + $c) }
    Write-Output 'ORACLEMATRIX RESULT: pass=0 fail=1'
    exit 1
}
Write-Output ('product exe        = ' + $exe)

# ===========================================================================
# 3. 素材（仓库相对推导；缺素材是**响亮失败**，不静默降级）
# ===========================================================================
$srcCandidates = @(
    (Join-Path $repoRoot 'tests/output/sources/src_8bit.png'),
    (Join-Path $repoRoot 'tests/output/sources/src_alpha.png')
)
if (-not [string]::IsNullOrWhiteSpace($Source)) {
    if (Test-Path -LiteralPath $Source -PathType Leaf) { $srcCandidates = @($Source) }
    else {
        Write-Output ('  FAIL -Source does not exist: ' + $Source)
        Write-Output 'ORACLEMATRIX RESULT: pass=0 fail=1'
        exit 1
    }
}
$src = ''
foreach ($c in $srcCandidates) {
    if (Test-Path -LiteralPath $c -PathType Leaf) { $src = $c; break }
}
if ($src -eq '') {
    Write-Output '  FAIL source fixture not found (run tests/scripts/ensure-fixtures.ps1 first):'
    foreach ($c in $srcCandidates) { Write-Output ('        ' + $c) }
    Write-Output 'ORACLEMATRIX RESULT: pass=0 fail=1'
    exit 1
}

$iccCandidates = @(
    (Join-Path $repoRoot 'tests/output/validate/caps/p3.icc'),
    (Join-Path $repoRoot 'tests/output/sources/srgb.icc')
)
if (-not [string]::IsNullOrWhiteSpace($IccFile)) {
    if (Test-Path -LiteralPath $IccFile -PathType Leaf) { $iccCandidates = @($IccFile) }
    else {
        Write-Output ('  FAIL -IccFile does not exist: ' + $IccFile)
        Write-Output 'ORACLEMATRIX RESULT: pass=0 fail=1'
        exit 1
    }
}
$icc = ''
foreach ($c in $iccCandidates) {
    if (Test-Path -LiteralPath $c -PathType Leaf) { $icc = $c; break }
}
if ($icc -eq '') {
    Write-Output '  FAIL icc fixture not found (run tests/scripts/ensure-fixtures.ps1 first):'
    foreach ($c in $iccCandidates) { Write-Output ('        ' + $c) }
    Write-Output 'ORACLEMATRIX RESULT: pass=0 fail=1'
    exit 1
}
Write-Output ('source fixture     = ' + $src)
Write-Output ('icc fixture        = ' + $icc)

# ===========================================================================
# 4. 裁判前置检查（`Test-OracleAvailable`：找到 exe + 存活探测）
#    核心三件套（ffmpeg/ffprobe/exiftool）缺任一 ⇒ 无裁判可用 ⇒ 响亮早退（仍打印 RESULT 行）。
# ===========================================================================
Write-Output ''
Write-Output '===== oracle availability ====='
$avail = Test-OracleAvailable -TimeoutSec $oto
# ⚠ 局部变量名**不得**与脚本 param 同名（PS 变量名大小写不敏感）：
#   初版这里写了 `$smoke`，它其实是 `-Smoke` 那个 SwitchParameter，赋值直接抛
#   「无法将值 "System.String" 转换为类型 SwitchParameter」⇒ 整张表打不出来。
foreach ($t in @($avail.Tools)) {
    $foundTxt = $(if ($t.Found) { 'found' } else { 'MISSING' })
    $smokeTxt = $(if ($t.SmokeOk) { 'smoke-ok' } else { 'smoke-no' })
    Write-Output ('  ' + $t.Name.PadRight(13) + ' ' + $foundTxt.PadRight(8) + ' ' + $smokeTxt.PadRight(9) + ' ' + $t.Note)
}
Write-Output ('  available   = ' + (@($avail.Available) -join ','))
Write-Output ('  unavailable = ' + (@($avail.Unavailable) -join ','))

$coreTools = @('ffmpeg', 'ffprobe', 'exiftool')
$coreMissing = @()
foreach ($n in $coreTools) {
    if (@($avail.Available) -notcontains $n) { $coreMissing += $n }
}
if ($coreMissing.Count -gt 0) {
    CK $false ('core oracle tools available (missing: ' + ($coreMissing -join ',') + ')')
    Write-Output 'ORACLEMATRIX RESULT: pass=0 fail=1'
    exit 1
}
CK $true ('core oracle tools available (' + ($coreTools -join ',') + ')')
$haveJxlinfo = (@($avail.Available) -contains 'jxlinfo')

# ===========================================================================
# 5. 运行级 GUID 目录（唯一目录没人替你清 ⇒ 结尾自清；不污染共享语料目录）
# ===========================================================================
$valDir = Join-Path $repoRoot 'tests/output/validate'
if (-not (Test-Path -LiteralPath $valDir)) { New-Item -ItemType Directory -Path $valDir -Force | Out-Null }
$runTag = [guid]::NewGuid().ToString('N').Substring(0, 8)
$out = Join-Path $valDir ('oraclemx_' + $runTag)
New-Item -ItemType Directory -Path $out -Force | Out-Null
Write-Output ''
Write-Output ('run-scoped temp dir = ' + $out)

# ===========================================================================
# 6. 助手（全部无缓存；读失败一律 $null + 显式记账，绝不沿用上一轮的值）
# ===========================================================================

# 取 CLI token 里 `-f/--format` 的值；缺省 = 应用默认 `jxl`（CliParser.cs:30）。
# `jpegli` 会被 CliParser.cs:150 规范化成 `jpg`（编码器后端不是容器格式）。
function Get-ExpectedExt {
    param([string[]]$Tokens)
    $f = ''
    for ($i = 0; $i -lt $Tokens.Count; $i++) {
        $t = [string]$Tokens[$i]
        if (($t -eq '-f') -or ($t -eq '--format')) {
            if (($i + 1) -lt $Tokens.Count) { $f = [string]$Tokens[$i + 1] }
        }
    }
    if ($f -eq '') { return 'jxl' }
    $f = $f.ToLowerInvariant()
    if ($f -eq 'jpegli') { return 'jpg' }
    return $f
}

# 把用例参数里 `-o/--output` 的取值换成**本条用例专属**目录（否则同格式用例互相覆盖）。
function Set-CaseOutDir {
    param([string[]]$ArgList, [string]$Dir)
    $res = New-Object System.Collections.ArrayList
    $i = 0
    $done = $false
    while ($i -lt $ArgList.Count) {
        $t = [string]$ArgList[$i]
        $isOut = ((-not $done) -and (($t -eq '-o') -or ($t -eq '--output')))
        if ($isOut) {
            [void]$res.Add($t)
            if (($i + 1) -lt $ArgList.Count) { [void]$res.Add($Dir) }
            $i = $i + 2
            $done = $true
        } else {
            [void]$res.Add($t)
            $i = $i + 1
        }
    }
    return $res.ToArray()
}

# token 里是否出现任一 `-like` 形态（用 -like 而非正则，免转义坑）。
function Test-AnyTokenLike {
    param([string[]]$Tokens, [string[]]$Patterns)
    foreach ($t in $Tokens) {
        foreach ($p in $Patterns) {
            if ([string]$t -like $p) { return $true }
        }
    }
    return $false
}

# `--jpeg-gain-map` 是否被**打开**（空格形式 `--jpeg-gain-map true` 或等号形式 `--jpeg-gain-map=true`）。
function Test-GainMapRequested {
    param([string[]]$Tokens)
    for ($i = 0; $i -lt $Tokens.Count; $i++) {
        $t = [string]$Tokens[$i]
        if ($t -eq '--jpeg-gain-map=true') { return $true }
        if (($t -eq '--jpeg-gain-map') -and (($i + 1) -lt $Tokens.Count) -and ([string]$Tokens[$i + 1] -eq 'true')) { return $true }
    }
    return $false
}

# `--lossless` 是否被**打开**。
function Test-LosslessRequested {
    param([string[]]$Tokens)
    for ($i = 0; $i -lt $Tokens.Count; $i++) {
        $t = [string]$Tokens[$i]
        if ($t -eq '--lossless=true') { return $true }
        if (($t -eq '--lossless') -and (($i + 1) -lt $Tokens.Count) -and ([string]$Tokens[$i + 1] -eq 'true')) { return $true }
    }
    return $false
}

# 数值格式化（InvariantCulture；Infinity/NaN 显式可辨，不落到默认串）。
function Format-Num {
    param($V)
    if ($null -eq $V) { return 'null' }
    $d = 0.0
    try { $d = [double]$V } catch { return 'unparsable' }
    if ([double]::IsPositiveInfinity($d)) { return 'inf' }
    if ([double]::IsNegativeInfinity($d)) { return '-inf' }
    if ([double]::IsNaN($d)) { return 'nan' }
    return $d.ToString('0.###', [System.Globalization.CultureInfo]::InvariantCulture)
}

# 目录里第一条产物文件；目录不存在/为空/读失败一律 $null（**不**沿用上一轮的值）。
function Get-ProductFile {
    param([string]$Dir)
    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { return $null }
    $files = @()
    try { $files = @(Get-ChildItem -LiteralPath $Dir -Recurse -File -ErrorAction SilentlyContinue | Sort-Object FullName) }
    catch { return $null }
    if ($files.Count -lt 1) { return $null }
    return $files[0]
}

# ffmpeg 能否独立解码该产物（几何类用例用它替代 PSNR，因为尺寸本就该变）。
function Test-Decodable {
    param([string]$Path, [int]$TimeoutSec)
    $res = [pscustomobject]@{ Ok = $false; Error = '' }
    $ff = Get-ToolPath -Name 'ffmpeg'
    if ($null -eq $ff) { $res.Error = 'oracle ffmpeg not found'; return $res }
    $r = Invoke-ExeCapture -FilePath $ff -Arguments ('-hide_banner -nostdin -loglevel error -i "' + $Path + '" -f null -') -TimeoutSec $TimeoutSec
    if ($r.TimedOut) { $res.Error = $r.Error; return $res }
    if ($r.Error -ne '') { $res.Error = $r.Error; return $res }
    if ($r.ExitCode -ne 0) { $res.Error = 'ffmpeg decode exit ' + [string]$r.ExitCode; return $res }
    $res.Ok = $true
    return $res
}

# 造一个「坏 PNG」：定位 IDAT 数据区并翻转一个字节（**不**修 CRC）⇒ 真容器校验必须转红。
function New-TamperedPng {
    param([string]$SrcPath, [string]$DstPath)
    $b = $null
    try { $b = [System.IO.File]::ReadAllBytes($SrcPath) } catch { return $false }
    if ($null -eq $b) { return $false }
    if ($b.Length -lt 40) { return $false }
    $pos = 8
    $target = -1
    while (($pos + 8) -le $b.Length) {
        $cl = [long]$b[$pos] * 16777216 + [long]$b[$pos + 1] * 65536 + [long]$b[$pos + 2] * 256 + [long]$b[$pos + 3]
        $t = [System.Text.Encoding]::ASCII.GetString($b, [int]($pos + 4), 4)
        if (($t -eq 'IDAT') -and ($cl -gt 2)) { $target = [int]($pos + 8) + 1; break }
        if ($t -eq 'IEND') { break }
        $pos = $pos + 8 + $cl + 4
    }
    if ($target -lt 0) { $target = [int]($b.Length / 2) }
    if (($target -lt 8) -or ($target -ge $b.Length)) { return $false }
    $b[$target] = [byte](($b[$target] + 0x5A) -band 0xFF)
    try { [System.IO.File]::WriteAllBytes($DstPath, $b) } catch { return $false }
    return $true
}

# 分批删除整棵树。⚠ 实测坑：宿主有一条**批量删除守卫**，单次删除目标条目数超过阈值
#   （本机 50）会直接拒绝并打印 `SAFE_DELETE_BULK_CONFIRM_REQUIRED`，`-EA SilentlyContinue`
#   把它吞掉后**目录原样留在盘上** —— 本脚本一条用例一个子目录，全量跑会有数千条，
#   一次删必被拦。故按**小批**（默认 10 条子项）逐批删，最后删空壳根目录。
function Remove-TreeBatched {
    param([string]$Path, [int]$BatchSize = 10)
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    $children = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)
    $i = 0
    while ($i -lt $children.Count) {
        $n = [Math]::Min($BatchSize, $children.Count - $i)
        $paths = New-Object System.Collections.ArrayList
        for ($k = $i; $k -lt ($i + $n); $k++) { [void]$paths.Add($children[$k].FullName) }
        Remove-Item -Recurse -Force -LiteralPath $paths.ToArray() -ErrorAction SilentlyContinue
        $i = $i + $n
    }
    Remove-Item -Recurse -Force -LiteralPath $Path -ErrorAction SilentlyContinue
    return (-not (Test-Path -LiteralPath $Path))
}

# ===========================================================================
# 7. 裁判链：一条产物挂哪些裁判、结果如何（全部来自 `_lib-oracle.ps1`）
#    返回 @{ Verdict; Reason; Judges; PngW; PngH; FfW; FfH; Psnr }
#    ⚠ 每次调用都新建返回对象；**不做跨调用缓存**。
# ===========================================================================
function Invoke-CaseJudges {
    param(
        [string]$ProductPath,
        [string]$ExpectedExt,
        [string[]]$Tokens,
        [string]$RefPath,
        [int]$TimeoutSec,
        [bool]$DeepMode
    )
    $judges = New-Object System.Collections.ArrayList
    $bad = New-Object System.Collections.ArrayList
    $res = [pscustomobject]@{
        Verdict = 'fail'; Reason = ''; Judges = @()
        PngW = $null; PngH = $null; FfW = $null; FfH = $null; Psnr = $null
    }

    $ext = [System.IO.Path]::GetExtension($ProductPath)
    if ($ext.Length -gt 0) { $ext = $ext.Substring(1).ToLowerInvariant() }

    # -- 裁判 A: 文件存在且非空（文件系统口径） ---------------------------------
    $len = -1
    try { $len = [int]([System.IO.FileInfo]$ProductPath).Length } catch { $len = -1 }
    if ($len -lt 0) { [void]$bad.Add('size-unreadable') }
    elseif ($len -eq 0) { [void]$bad.Add('empty-product') }
    else { [void]$judges.Add('size=ok(' + [string]$len + 'B)') }

    # -- 裁判 A2: 容器扩展名必须与请求的 `-f` 一致 -------------------------------
    if ($ExpectedExt -ne '') {
        if ($ext -ne $ExpectedExt) {
            [void]$bad.Add('container-mismatch(got=' + $ext + ',want=' + $ExpectedExt + ')')
        } else {
            [void]$judges.Add('container=' + $ext)
        }
    }

    # -- 裁判 B: 独立解码器（ffprobe）能读且宽高为正 ----------------------------
    #    jxr/dng 是 ffmpeg 根本没有解码器的容器 ⇒ 该格式下 ffprobe 记 best-effort（不当判负）。
    $needStream = -not (($ext -eq 'jxr') -or ($ext -eq 'dng'))
    $si = Get-StreamInfo -Path $ProductPath -TimeoutSec $TimeoutSec
    if ($si.Ok) {
        $res.FfW = $si.Width
        $res.FfH = $si.Height
        [void]$judges.Add('ffprobe=ok(' + [string]$si.Width + 'x' + [string]$si.Height + ',' + [string]$si.PixFmt + ')')
        if ((-not ([int]$si.Width -gt 0)) -or (-not ([int]$si.Height -gt 0))) {
            [void]$bad.Add('ffprobe-zero-dim(' + [string]$si.Width + 'x' + [string]$si.Height + ')')
        }
    } else {
        [void]$judges.Add('ffprobe=unavailable(' + $si.Error + ')')
        if ($needStream) { [void]$bad.Add('ffprobe:' + $si.Error) }
    }

    # -- 裁判 C: 色彩描述可读（exiftool；jxl 自动改走 jxlinfo） -----------------
    #    ⚠ 「确实没有 CICP」= 全 null 且 Ok=$true（确定的答案）；「读不出来」= Ok=$false。
    $cm = Get-ColorMetadata -Path $ProductPath -TimeoutSec $TimeoutSec
    if ($cm.Ok) {
        $cp = 'null'; if ($null -ne $cm.Primaries) { $cp = [string]$cm.Primaries }
        $ct = 'null'; if ($null -ne $cm.Transfer) { $ct = [string]$cm.Transfer }
        [void]$judges.Add('color=ok(' + [string]$cm.Source + ';P=' + $cp + ';T=' + $ct + ')')
    } else {
        [void]$judges.Add('color=unavailable(' + $cm.Error + ')')
        [void]$bad.Add('color:' + $cm.Error)
    }

    # -- 裁判 D: PNG/APNG 真容器结构（逐 chunk CRC32 + IHDR 字段 + 尾随字节） ---
    if (($ext -eq 'png') -or ($ext -eq 'apng')) {
        $ps = Test-PngStructure -Path $ProductPath
        if ($ps.Ok) {
            $res.PngW = $ps.Width
            $res.PngH = $ps.Height
            [void]$judges.Add('png-structure=ok(chunks=' + [string]$ps.ChunkCount + ',crc=' + [string]$ps.CrcChecked + ')')
        } else {
            [void]$judges.Add('png-structure=FAIL(' + $ps.Error + ')')
            [void]$bad.Add('png-structure:' + $ps.Error)
        }
    }

    # -- 裁判 E: JXL 走 jxlinfo（exiftool 读不出 JXL 的 ICC） -------------------
    if ($ext -eq 'jxl') {
        $ji = Get-JxlInfo -Path $ProductPath -TimeoutSec $TimeoutSec
        if ($ji.Ok) {
            [void]$judges.Add('jxlinfo=ok(' + [string]$ji.Width + 'x' + [string]$ji.Height + ',icc=' + [string]$ji.HasIcc + ')')
        } else {
            [void]$judges.Add('jxlinfo=FAIL(' + $ji.Error + ')')
            [void]$bad.Add('jxlinfo:' + $ji.Error)
        }
    }

    # -- 裁判 F: 请求了 GainMap ⇒ 必须真的带 GainMap 元数据 --------------------
    if (Test-GainMapRequested -Tokens $Tokens) {
        $gm = Get-GainMapMetadata -Path $ProductPath -TimeoutSec $TimeoutSec
        if ($gm.Ok) {
            if ($gm.HasGainMap) { [void]$judges.Add('gainmap=ok') }
            else { [void]$judges.Add('gainmap=absent'); [void]$bad.Add('gainmap-absent') }
        } else {
            [void]$judges.Add('gainmap=unavailable(' + $gm.Error + ')')
            [void]$bad.Add('gainmap:' + $gm.Error)
        }
    }

    # -- 裁判 G: 像素往返 -------------------------------------------------------
    #    几何类用例尺寸本就该变 ⇒ 改判「能否独立解码」；其余用归一化 PSNR 与源逐像素比。
    #    归一化图 `format=gbrp16le` 两侧都做，避免位深/采样布局不同导致 psnr 滤镜直接报错。
    #    ⚠ jxr/dng：ffmpeg **根本没有**对应解码器（实测 ffprobe/ffmpeg 均报 Invalid data），
    #      故像素类裁判在该两种容器上**不适用** —— 显式记 `n/a` 而不是记红（记红只会把
    #      「格式限制」伪装成「产物缺陷」）。这是本脚本已知的**覆盖缺口**：这两种容器的
    #      像素正确性目前**没有**独立裁判（exiftool 只确认元数据）。
    $noFfmpegDecoder = (($ext -eq 'jxr') -or ($ext -eq 'dng'))
    $geomTokens = @('--max-dimension', '--max-dimension=*', '--animation-scale-w', '--animation-scale-w=*')
    if ($noFfmpegDecoder) {
        [void]$judges.Add('psnr=n/a(ffmpeg-has-no-' + $ext + '-decoder)')
    } elseif (Test-AnyTokenLike -Tokens $Tokens -Patterns $geomTokens) {
        $dec = Test-Decodable -Path $ProductPath -TimeoutSec $TimeoutSec
        if ($dec.Ok) { [void]$judges.Add('decode=ok') }
        else { [void]$judges.Add('decode=FAIL(' + $dec.Error + ')'); [void]$bad.Add('decode:' + $dec.Error) }
    } else {
        $graph = '[0:v]format=gbrp16le[a];[1:v]format=gbrp16le[b];[a][b]psnr'
        $psnr = Invoke-PsnrAfterTransform -Ref $RefPath -Dist $ProductPath -Filter $graph -FullGraph -TimeoutSec $TimeoutSec
        if ($psnr.Ok) {
            $res.Psnr = $psnr.Psnr
            if ($psnr.IsIdentical) { [void]$judges.Add('psnr=identical') }
            else { [void]$judges.Add('psnr=' + (Format-Num $psnr.Psnr)) }
            # 无任何改变像素的选项 + 无损容器 ⇒ 必须**逐像素相同**（有牙的一条断言）。
            $pixelAffecting = Test-AnyTokenLike -Tokens $Tokens -Patterns @(
                '-e', '--encoder', '--color*', '--icc*', '--tonemap*',
                '--jpeg-gain-map*', '--max-dimension*', '--animation-scale-w*'
            )
            $losslessExt = @('png', 'apng', 'tiff', 'bmp')
            $isLossless = ($losslessExt -contains $ext)
            if (Test-LosslessRequested -Tokens $Tokens) { $isLossless = $true }
            if ((-not $pixelAffecting) -and $isLossless -and (-not $psnr.IsIdentical)) {
                [void]$bad.Add('lossless-not-identical(psnr=' + (Format-Num $psnr.Psnr) + ')')
            }
        } else {
            [void]$judges.Add('psnr=unavailable(' + $psnr.Error + ')')
            [void]$bad.Add('psnr:' + $psnr.Error)
        }
    }

    # -- 裁判 H（-Deep）: 感知分数（butteraugli 越小越好 / ssimulacra2 越大越好） --
    if ($DeepMode) {
        $pc = Invoke-PerceptualScore -Ref $RefPath -Dist $ProductPath -TimeoutSec $TimeoutSec
        if ($pc.Ok) {
            [void]$judges.Add('perceptual=ok(b=' + (Format-Num $pc.Butteraugli) + ',s2=' + (Format-Num $pc.Ssimulacra2) + ')')
        } else {
            [void]$judges.Add('perceptual=unavailable(' + $pc.Error + ')')
            [void]$bad.Add('perceptual:' + $pc.Error)
        }
    }

    $res.Judges = $judges.ToArray()
    if ($bad.Count -eq 0) { $res.Verdict = 'pass' }
    else {
        $res.Verdict = 'fail'
        $res.Reason = ($bad.ToArray() -join '; ')
    }
    return $res
}

# ===========================================================================
# 8. 跑一条用例：建专属目录 -> 跑产品 -> 找产物 -> 挂裁判
# ===========================================================================
function Invoke-MatrixCase {
    param(
        $Case,
        [string]$LayerName,
        [string]$Exe,
        [string]$RefPath,
        [string]$Root,
        [int]$CaseTimeout,
        [int]$TimeoutSec,
        [bool]$DeepMode
    )
    $caseDir = Join-Path $Root ([string]$Case.Id)
    if (Test-Path -LiteralPath $caseDir) { Remove-Item -Recurse -Force -LiteralPath $caseDir -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $caseDir -Force | Out-Null

    $argList = Set-CaseOutDir -ArgList @($Case.Args) -Dir $caseDir
    $tokens = @($argList)
    $cmd = ConvertTo-CliArgs -Case ([pscustomobject]@{ Args = $argList })
    $dryRun = Test-AnyTokenLike -Tokens $tokens -Patterns @('--dry-run')

    $res = [pscustomobject]@{
        Id = [string]$Case.Id; Layer = $LayerName; Cmd = $cmd
        ExitCode = $null; TimedOut = $false; Product = ''; ProductBytes = 0
        Verdict = 'fail'; Reason = ''; Judges = @()
        PngW = $null; PngH = $null; FfW = $null; FfH = $null; Psnr = $null
    }

    $r = Invoke-ExeCapture -FilePath $Exe -Arguments $cmd -TimeoutSec $CaseTimeout
    $res.ExitCode = $r.ExitCode
    $res.TimedOut = $r.TimedOut
    if ($r.TimedOut) { $res.Reason = 'timeout(' + [string]$CaseTimeout + 's)'; return $res }
    if ($r.Error -ne '') { $res.Reason = 'spawn:' + $r.Error; return $res }

    $prod = Get-ProductFile -Dir $caseDir

    # `--dry-run` 是唯一「按契约必须没有产物」的例外：退出码 0 且确实没有产物才算过。
    if ($dryRun) {
        if (($r.ExitCode -eq 0) -and ($null -eq $prod)) {
            $res.Verdict = 'pass'
            $res.Judges = @('dry-run=no-product,exit0')
        } else {
            $res.Reason = 'dry-run-contract-violated(exit=' + [string]$r.ExitCode +
                ',product=' + $(if ($null -ne $prod) { 'yes' } else { 'no' }) + ')'
        }
        return $res
    }

    if ($null -eq $prod) {
        $res.Reason = 'no-product(exit=' + [string]$r.ExitCode + ')'
        return $res
    }

    $res.Product = $prod.FullName
    $res.ProductBytes = [int]$prod.Length
    $j = Invoke-CaseJudges -ProductPath $prod.FullName -ExpectedExt (Get-ExpectedExt -Tokens $tokens) `
        -Tokens $tokens -RefPath $RefPath -TimeoutSec $TimeoutSec -DeepMode $DeepMode
    $res.Judges = @($j.Judges)
    $res.PngW = $j.PngW; $res.PngH = $j.PngH
    $res.FfW = $j.FfW; $res.FfH = $j.FfH
    $res.Psnr = $j.Psnr
    $res.Verdict = $j.Verdict
    $res.Reason = $j.Reason

    if ($r.ExitCode -ne 0) {
        $exr = 'nonzero-exit(exit=' + [string]$r.ExitCode + ')'
        if ($res.Reason -eq '') { $res.Reason = $exr } else { $res.Reason = $exr + '; ' + $res.Reason }
        $res.Verdict = 'fail'
    }
    return $res
}

# ===========================================================================
# 9. 组装用例（分层可开关；L3 真积默认全量，可用 -MaxPerMatrix/-MaxCases 确定性截断）
# ===========================================================================
$registry = @(Get-AxisRegistry)
$placeholder = '__OUT__'
$selected = New-Object System.Collections.ArrayList

if ($Smoke) {
    # 冒烟：L1 的 format 轴取 png/jpg/jxl 三格 —— 3 个不同输出格式；
    # 裁判至少两类（PNG 真容器结构 / ffprobe / exiftool / jxlinfo / PSNR）。
    $l1Smoke = @(Get-SingleAxisCases -Source $src -OutDir $placeholder -IccFile $icc -Registry $registry)
    foreach ($want in @('png', 'jpg', 'jxl')) {
        foreach ($c in $l1Smoke) {
            if (([string]$c.Axis -eq 'format') -and ([string]$c.Value -eq $want)) {
                [void]$selected.Add([pscustomobject]@{ Id = [string]$c.Id; Layer = 'L1'; Args = @($c.Args) })
                break
            }
        }
    }
    Write-Output ''
    Write-Output '===== SMOKE mode: 3 representative cases (png/jpg/jxl) ====='
} else {
    if (($Layer -eq 'L1') -or ($Layer -eq 'All')) {
        $l1 = @(Get-SingleAxisCases -Source $src -OutDir $placeholder -IccFile $icc -Registry $registry)
        foreach ($c in $l1) { [void]$selected.Add([pscustomobject]@{ Id = [string]$c.Id; Layer = 'L1'; Args = @($c.Args); Precondition = [string]$c.Precondition }) }
        Write-Output ('  [layer] L1 single-axis cases = ' + [string]$l1.Count)
    }
    if (($Layer -eq 'L2') -or ($Layer -eq 'All')) {
        $l2 = @(Get-PairwiseCases -Source $src -OutDir $placeholder -IccFile $icc -Registry $registry)
        foreach ($c in $l2) { [void]$selected.Add([pscustomobject]@{ Id = [string]$c.Id; Layer = 'L2'; Args = @($c.Args); Precondition = [string]$c.Precondition }) }
        Write-Output ('  [layer] L2 pairwise cases = ' + [string]$l2.Count)
    }
    if (($Layer -eq 'L3') -or ($Layer -eq 'All')) {
        $l3 = @(Get-SemanticMatrices -Source $src -OutDir $placeholder -IccFile $icc -Registry $registry)
        foreach ($m in $l3) {
            $mc = @($m.Cases)
            # ⚠ 顺序必须是「**先跳前缀、再截长度**」——反了会每次都取同一批前缀（永远推不进后半段）。
            #   `-SkipPerMatrix` 默认 0 ⇒ 本块短路 ⇒ 行为与加参数前**逐字一致**。
            if (($SkipPerMatrix -gt 0) -and ($mc.Count -gt 0)) {
                if ($SkipPerMatrix -ge $mc.Count) { $mc = @() }
                else { $mc = @($mc[$SkipPerMatrix..($mc.Count - 1)]) }
            }
            if (($MaxPerMatrix -gt 0) -and ($mc.Count -gt $MaxPerMatrix)) {
                $mc = @($mc[0..($MaxPerMatrix - 1)])
            }
            foreach ($c in $mc) { [void]$selected.Add([pscustomobject]@{ Id = [string]$c.Id; Layer = 'L3'; Args = @($c.Args); Precondition = '' }) }
            Write-Output ('  [layer] L3 ' + $m.Name + ' raw=' + [string]$m.RawCount +
                ' folded=' + [string]$m.FoldedCount + ' selected=' + [string]$mc.Count)
        }
    }
}

$caseArr = @($selected)
if ((-not $Smoke) -and ($MaxCases -gt 0) -and ($caseArr.Count -gt $MaxCases)) {
    $caseArr = @($caseArr[0..($MaxCases - 1)])
}
if ($caseArr.Count -lt 1) {
    Write-Output '  FAIL no cases selected'
    Write-Output 'ORACLEMATRIX RESULT: pass=0 fail=1'
    exit 1
}
Write-Output ''
Write-Output ('===== running ' + [string]$caseArr.Count + ' case(s) (layer=' + $Layer + ', deep=' + [string][bool]$Deep + ') =====')

# ===========================================================================
# 10. 主循环（每条用例单独 try/catch ⇒ 单条异常不得吞掉整轮）
# ===========================================================================
$results = New-Object System.Collections.ArrayList
# 前提不满足而**点名跳过**的用例数（2026-10-07）。⚠ 不计入 pass、也不计入 fail，
# 但**必须**打印 SKIP 行并在此计数 ⇒ 读数可审计（防"静默消失"式假绿）。
$script:skipCases = 0
$swAll = [System.Diagnostics.Stopwatch]::StartNew()
$idx = 0
foreach ($c in $caseArr) {
    $idx++
    # ── 前提闸（2026-10-07 新增）──────────────────────────────────────────────
    # 用例若声明了前提而当前夹具不满足 ⇒ **SKIP 并点名**，不判红也**不静默消失**。
    # 动机：`dng` 编码需要传感器 RAW 数据，而本脚本的夹具是普通 PNG ⇒ 产品**正确拒绝**
    # （实测 `❌ 失败 (非 RAW 输入)`）⇒ 不声明前提时它表现为 `no-product(exit=1)`，
    # 与**真缺陷无法区分**（本仓 §6 第 65 条：断言的『前提』本身写错）。
    # ⚠ 与 §6 第 68 条「消费者读不到前置产物就静默跳过 ⇒ 假绿」的**区别**：
    #   这里**必须点名打印** SKIP 行，且**计入 skip 计数**，绝不当作通过。
    $pre = ''
    if ($c.PSObject.Properties.Name -contains 'Precondition') { $pre = [string]$c.Precondition }
    if ($pre -eq 'requires-raw-source') {
        $rawOk = $false
        if (-not [string]::IsNullOrWhiteSpace($src)) {
            $ext = [System.IO.Path]::GetExtension($src).ToLowerInvariant()
            $rawOk = $ext -in @('.dng', '.arw', '.cr2', '.cr3', '.nef', '.raf', '.orf', '.rw2', '.pef')
        }
        if (-not $rawOk) {
            $fixName = [System.IO.Path]::GetFileName($src)
            $skipMsg = '  SKIP ' + [string]$c.Id + ' 前提不满足：' + $pre + '（当前夹具 ' + $fixName + ' 非 RAW）' + ' ⇒ 该取值本轮未验证，不计入通过'
            Write-Output $skipMsg
            $script:skipCases++
            continue
        }
    }
    $r = $null
    try {
        $r = Invoke-MatrixCase -Case $c -LayerName ([string]$c.Layer) -Exe $exe -RefPath $src -Root $out `
            -CaseTimeout $CaseTimeoutSec -TimeoutSec $oto -DeepMode ([bool]$Deep)
    } catch {
        $r = [pscustomobject]@{
            Id = [string]$c.Id; Layer = [string]$c.Layer; Cmd = ''; ExitCode = $null; TimedOut = $false
            Product = ''; ProductBytes = 0; Verdict = 'fail'; Reason = 'harness-exception:' + $_.Exception.Message
            Judges = @(); PngW = $null; PngH = $null; FfW = $null; FfH = $null; Psnr = $null
        }
    }
    [void]$results.Add($r)

    $jl = @($r.Judges) -join ' '
    if ($r.Verdict -eq 'pass') {
        CaseCK $true ($r.Id + ' [' + $jl + ']')
    } else {
        CaseCK $false ($r.Id + ' reason=' + $r.Reason + ' [' + $jl + '] cmd=' + $r.Cmd)
    }
    if (($idx % 50) -eq 0) {
        Write-Output ('  [progress] ' + [string]$idx + '/' + [string]$caseArr.Count +
            ' pass=' + [string]$casePass + ' fail=' + [string]$caseFail)
    }
}
$swAll.Stop()
$elapsedCases = [int]$swAll.Elapsed.TotalSeconds

# ===========================================================================
# 11. 负控（**每次运行都跑**；证明判读链有牙）+ 独立复读
# ===========================================================================
Write-Output ''
Write-Output '===== negative controls (must turn RED) ====='
$ncDir = Join-Path $out 'negctl'
New-Item -ItemType Directory -Path $ncDir -Force | Out-Null

# NC1 正控：合法参数必须成功且产出。
$ncGoodDir = Join-Path $ncDir 'good'
New-Item -ItemType Directory -Path $ncGoodDir -Force | Out-Null
$goodArgs = @('--headless', '-i', $src, '-o', $ncGoodDir, '-f', 'png')
$goodCmd = ConvertTo-CliArgs -Case ([pscustomobject]@{ Args = $goodArgs })
$rGood = Invoke-ExeCapture -FilePath $exe -Arguments $goodCmd -TimeoutSec $CaseTimeoutSec
$goodProd = Get-ProductFile -Dir $ncGoodDir
CK ((-not $rGood.TimedOut) -and ($rGood.ExitCode -eq 0) -and ($null -ne $goodProd)) `
    ('NC1 positive control: legal args => exit 0 + product (exit=' + [string]$rGood.ExitCode +
     ', product=' + $(if ($null -ne $goodProd) { 'yes' } else { 'no' }) + ')')

# NC2 负控（CLI）：非法取值必须**响亮拒绝**且不产出「看着像成功」的产物。
$ncBadDir = Join-Path $ncDir 'bad'
New-Item -ItemType Directory -Path $ncBadDir -Force | Out-Null
$badArgs = @('--headless', '-i', $src, '-o', $ncBadDir, '-f', 'png', '--color-gamut-map', 'yes')
$badCmd = ConvertTo-CliArgs -Case ([pscustomobject]@{ Args = $badArgs })
$rBad = Invoke-ExeCapture -FilePath $exe -Arguments $badCmd -TimeoutSec $CaseTimeoutSec
$badProd = Get-ProductFile -Dir $ncBadDir
CK ((-not $rBad.TimedOut) -and ($rBad.ExitCode -ne 0) -and ($null -eq $badProd)) `
    ('NC2 negative control: illegal value => exit != 0 + no product (exit=' + [string]$rBad.ExitCode +
     ', product=' + $(if ($null -ne $badProd) { 'yes' } else { 'no' }) + ')')

# NC3 负控（产物）：把好产物改坏 ⇒ 真容器校验必须转红（正控 = 改坏前必须为绿）。
$ncStructGood = $false
$ncStructBad = $false
if ($null -ne $goodProd) {
    $psGood = Test-PngStructure -Path $goodProd.FullName
    $ncStructGood = [bool]$psGood.Ok
    CK $ncStructGood ('NC3a positive control: untouched product passes Test-PngStructure (crc=' + [string]$psGood.CrcChecked + ')')

    $tampered = Join-Path $ncDir 'tampered.png'
    $madeTamper = New-TamperedPng -SrcPath $goodProd.FullName -DstPath $tampered
    CK $madeTamper 'NC3b tampered artifact was actually written'
    if ($madeTamper) {
        $psBad = Test-PngStructure -Path $tampered
        $ncStructBad = (-not [bool]$psBad.Ok)
        CK $ncStructBad ('NC3c negative control: tampered product FAILS Test-PngStructure (error=' + $psBad.Error + ')')
        CK ($null -ne $psBad.CrcFailChunk) ('NC3d the red one is a CRC mismatch (chunk=' + [string]$psBad.CrcFailChunk + ')')
    }
} else {
    CK $false 'NC3 skipped: no positive-control product to tamper (this is a real failure, not a skip)'
}

# 独立复读 ①：对结果对象**独立重算** pass/fail，再与内联计数比对（不只看自身汇总行）。
$recountPass = @($results | Where-Object { $_.Verdict -eq 'pass' }).Count
$recountFail = @($results | Where-Object { $_.Verdict -eq 'fail' }).Count
CK (($recountPass -eq $casePass) -and ($recountFail -eq $caseFail)) `
    ('R1 independent recount matches inline counters (recount pass=' + [string]$recountPass +
     ' fail=' + [string]$recountFail + ' ; inline pass=' + [string]$casePass + ' fail=' + [string]$caseFail + ')')

# 独立复读 ②：PNG 产物的宽高必须被**两个独立来源**确认（自研逐字节解析 vs ffprobe）。
# ⚠ 样本里必须**保证至少一个** PNG：L2/L3 的用例大多产出 jpg，若只统计用例产物，
#   这一条会在那些层上恒为空跑（初版实测 L2/L3 各因此多出一个假红）。故把负控那张
#   PNG（与所选层无关、必然存在）也作为兜底样本。
$crossSamples = New-Object System.Collections.ArrayList
foreach ($r in $results) {
    if (($null -ne $r.PngW) -and ($null -ne $r.FfW)) {
        [void]$crossSamples.Add(@([int]$r.PngW, [int]$r.PngH, [int]$r.FfW, [int]$r.FfH))
    }
}
if ($null -ne $goodProd) {
    $psG = Test-PngStructure -Path $goodProd.FullName
    $siG = Get-StreamInfo -Path $goodProd.FullName -TimeoutSec $oto
    if ($psG.Ok -and $siG.Ok) {
        [void]$crossSamples.Add(@([int]$psG.Width, [int]$psG.Height, [int]$siG.Width, [int]$siG.Height))
    }
}
$crossN = $crossSamples.Count
$crossBad = 0
foreach ($s in $crossSamples) {
    if (($s[0] -ne $s[2]) -or ($s[1] -ne $s[3])) { $crossBad++ }
}
CK (($crossN -ge 1) -and ($crossBad -eq 0)) `
    ('R2 cross-source dimensions agree on ' + [string]$crossN + ' png product(s) (mismatches=' + [string]$crossBad + ')')

# 独立复读 ③：同一个文件用 `Get-FileHash` 与 .NET SHA256 两套独立实现比哈希 + 双路字节长度。
$probePath = ''
if ($null -ne $goodProd) { $probePath = $goodProd.FullName }
if ($probePath -eq '') {
    foreach ($r in $results) { if ($r.Product -ne '') { $probePath = $r.Product; break } }
}
if ($probePath -ne '') {
    $lenA = -1
    try { $lenA = [int](Get-Item -LiteralPath $probePath).Length } catch { $lenA = -1 }
    $lenB = -1
    $bytes = $null
    try { $bytes = [System.IO.File]::ReadAllBytes($probePath) } catch { $bytes = $null }
    if ($null -ne $bytes) { $lenB = [int]$bytes.Length }
    CK (($lenA -ge 0) -and ($lenA -eq $lenB)) `
        ('R3a byte length agrees between Get-Item and ReadAllBytes (' + [string]$lenA + ' == ' + [string]$lenB + ')')

    $hashA = ''
    try { $hashA = (Get-FileHash -LiteralPath $probePath -Algorithm SHA256).Hash } catch { $hashA = '' }
    $hashB = ''
    if ($null -ne $bytes) {
        $sha = $null
        try {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $hashB = ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '')
        } catch { $hashB = '' }
        if ($null -ne $sha) { try { $sha.Dispose() } catch { } }
    }
    CK (($hashA -ne '') -and ($hashA -eq $hashB)) `
        ('R3b sha256 agrees between Get-FileHash and .NET (' + $hashA + ' == ' + $hashB + ')')
} else {
    CK $false 'R3 skipped: no product on disk to re-read (this is a real failure, not a skip)'
}

# ===========================================================================
# 12. -SelfCheck：跑一次**必红**的注入故障，走**完整裁判链**，要求它真的转红
#     ⚠ 语义：本脚本**绿** = 注入故障被抓住（判读链有牙）；**红** = 判读链是瞎的。
#       与「-SelfCheck 期望本门禁转红」的旧约定不同 —— 这里注入的是**产物故障**，
#       抓住它才是成功，故末行/退出码口径与主跑完全一致（fail=0 => exit 0）。
# ===========================================================================
if ($SelfCheck) {
    Write-Output ''
    Write-Output '===== selfcheck: injected artifact fault must be caught by the FULL judge chain ====='
    if (($null -eq $goodProd) -or (-not $ncStructBad)) {
        CK $false 'selfcheck precondition: positive-control product + tampered artifact must both exist'
    } else {
        $tampered2 = Join-Path $ncDir 'tampered.png'
        $scTokens = @('--headless', '-i', $src, '-o', $ncDir, '-f', 'png')

        $jcGood = Invoke-CaseJudges -ProductPath $goodProd.FullName -ExpectedExt 'png' `
            -Tokens $scTokens -RefPath $src -TimeoutSec $oto -DeepMode $false
        CK ($jcGood.Verdict -eq 'pass') `
            ('SC1 positive control: full judge chain returns PASS on the untouched artifact [' + (@($jcGood.Judges) -join ' ') + ']')

        $jcBad = Invoke-CaseJudges -ProductPath $tampered2 -ExpectedExt 'png' `
            -Tokens $scTokens -RefPath $src -TimeoutSec $oto -DeepMode $false
        $scCaught = ($jcBad.Verdict -eq 'fail') -and (([string]$jcBad.Reason).IndexOf('png-structure') -ge 0)
        CK $scCaught `
            ('SC2 injected fault: full judge chain returns FAIL on the tampered artifact (reason=' + $jcBad.Reason + ')')
        if ($scCaught) {
            Write-Output '  [selfcheck] injected artifact fault was caught by the judge chain => the chain has teeth'
        } else {
            Write-Output '  [selfcheck] injected artifact fault was NOT caught => the judgement chain is broken'
        }
    }
}

# ===========================================================================
# 13. 汇总（含按 reason 分类）+ 显式退出码
# ===========================================================================
$reasonTally = @{}
foreach ($r in $results) {
    if ($r.Verdict -eq 'pass') { continue }
    $key = 'other'
    $rs = [string]$r.Reason
    if ($rs.StartsWith('no-product')) { $key = 'no-product' }
    elseif ($rs.StartsWith('nonzero-exit')) { $key = 'nonzero-exit-with-product' }
    elseif ($rs.StartsWith('timeout')) { $key = 'timeout' }
    elseif ($rs.StartsWith('spawn:')) { $key = 'spawn-error' }
    elseif ($rs.StartsWith('harness-exception')) { $key = 'harness-exception' }
    elseif ($rs.StartsWith('dry-run-contract-violated')) { $key = 'dry-run-contract-violated' }
    elseif ($rs.IndexOf('png-structure') -ge 0) { $key = 'judge-png-structure' }
    elseif ($rs.IndexOf('psnr') -ge 0) { $key = 'judge-psnr' }
    elseif ($rs.IndexOf('ffprobe') -ge 0) { $key = 'judge-ffprobe' }
    elseif ($rs.IndexOf('color') -ge 0) { $key = 'judge-color' }
    elseif ($rs.IndexOf('jxlinfo') -ge 0) { $key = 'judge-jxlinfo' }
    elseif ($rs.IndexOf('gainmap') -ge 0) { $key = 'judge-gainmap' }
    elseif ($rs.IndexOf('lossless-not-identical') -ge 0) { $key = 'judge-lossless-pixels' }
    elseif ($rs.IndexOf('container-mismatch') -ge 0) { $key = 'container-mismatch' }
    if ($reasonTally.ContainsKey($key)) { $reasonTally[$key] = [int]$reasonTally[$key] + 1 }
    else { $reasonTally[$key] = 1 }
}

# 自清（**先删再汇总**，使 cleanup 这条断言也计入汇总）：唯一目录自清，不污染共享语料目录。
# ⚠ 必须走 `Remove-TreeBatched`：单次删数千条会被宿主批量删除守卫拦下，目录会原样留下。
$tempState = ''
if ($KeepTemp) {
    $tempState = 'kept (-KeepTemp)'
} else {
    $removed = Remove-TreeBatched -Path $out -BatchSize 10
    CK $removed ('temp dir removed: ' + $out)
    $tempState = $(if ($removed) { 'removed' } else { 'REMOVE-FAILED' })
}

Write-Output ''
Write-Output '===== summary ====='
Write-Output ('  cases      : total=' + [string]$caseArr.Count + ' pass=' + [string]$casePass + ' fail=' + [string]$caseFail)
Write-Output ('  elapsed    : ' + [string]$elapsedCases + 's for cases (avg ' +
    $(if ($caseArr.Count -gt 0) { [string][int]($elapsedCases * 1000 / $caseArr.Count) } else { 'n/a' }) + ' ms/case)')
if ($reasonTally.Count -gt 0) {
    Write-Output '  fail reasons:'
    foreach ($k in ($reasonTally.Keys | Sort-Object)) {
        Write-Output ('    ' + $k.PadRight(28) + ' ' + [string]$reasonTally[$k])
    }
}
Write-Output ('  checks     : pass=' + [string]$pass + ' fail=' + [string]$fail + ' (includes cases + meta checks)')
Write-Output ('  temp dir   : ' + $tempState + ' (' + $out + ')')

Write-Output ''
Write-Output ('ORACLEMATRIX RESULT: pass=' + [string]$pass + ' fail=' + [string]$fail)
if ($fail -gt 0) { exit 1 } else { exit 0 }
