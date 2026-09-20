# _lib-oracle.ps1 —— 「输出文件正确性」穷举测试的**独立校验 oracle 库**（第二块地基）
#
# 定位：本文件**不是门禁**（不产 PASS/FAIL、不登记进 `_run-step3-gates.ps1`），
#       而是一层**外部工具封装**。oracle = 不依赖被测软件（FfmpegGui.exe）自身逻辑、
#       能独立判断产出图片是否正确的外部程序（ffmpeg / ffprobe / exiftool / libjxl 工具链 / …）。
#
# ⚠⚠ 设计铁律（本仓硬教训：`docs/TESTING.md §6 第 102 条` 与 `.workbuddy-ai/memory/COLOR_TRAPS.md §B.9`）：
#   **「读失败沿用上一轮的值」= 假绿。**
#   因此本库：
#     1. **不做任何结果缓存** —— 每次调用都真的去跑一次 oracle；
#     2. 每个函数都返回**带成功标记的结构化对象**（`Ok=$true/$false` + `Error='...'`），
#        调用方必须能区分「确定的值」与「不确定」；
#     3. 失败（工具缺失 / 超时 / 输出不可解析 / 文件不存在）一律 **Ok=$false 且 Error 非空**，
#        **绝不**返回看起来正常的默认值（例如「无 CICP」= 全 $null + `Ok=$true`，
#        而「读不出来」= `Ok=$false` —— 这两者**必须可区分**）；
#     4. 每个外部调用都有**参数化超时**（默认 60s），超时用独立的 `TimedOut=$true` 标记，
#        不与「正常失败」混淆。
#
# ⚠ 双宿主兼容（PS 5.1 / PS 7）：禁 `? :` / `??` / `??=` / `?.` / `?[` / `&&` / `||`；
#   本文件全部用 `$(if (…) {…} else {…})`。门禁：`verify-ps-compat.ps1`（会扫到本文件）。
# ⚠ 串里全部纯 ASCII（本仓 `_probe-cjk-hardcode-scan.ps1` 余量为 0）；解释性中文一律放注释。
# ⚠ 本文件**只在 dot-source 时定义函数**，无顶层副作用（不建目录、不起进程）。
#
# 工具路径解析顺序（**绝不硬编码本机绝对路径**，与 `src/FfmpegGui/Services/PlatformServices.cs`
# 的 `PlanSubDirs`(`:247`) / `TryFindInPlanFolder`(`:308`) 同一映射规则）：
#   ① 显式参数 `-Override <path>`；
#   ② 环境变量 `FFMPEGGUI_ORACLE_<NAME>`（指向 exe 全路径；设了但文件不存在 ⇒ **显式失败**，不静默回退）；
#   ③ 环境变量 `FFMPEGGUI_FFMPEG_DIR`（仓库既有约定，仅 ffmpeg/ffprobe）；
#   ④ 环境变量 `FFMPEGGUI_ORACLE_PLAN_DIR`（替换 PLAN 根）+ ⑤ 仓库根下的映射目录；
#   ⑥ 目录名带日期后缀的变体（如 `ffmpeg-full-2026.7.24`）按「名字包含」匹配。
#   找不到 ⇒ `$null`（调用方按**显式失败**处理，不得静默跳过）。
#
# 用法：
#   . "$PSScriptRoot/_lib-oracle.ps1"
#   $a = Test-OracleAvailable            # 前置检查
#   $r = Invoke-Psnr -Ref a.png -Dist b.png
#   if (-not $r.Ok) { throw $r.Error }
#
# 导出函数：
#   Get-ToolPath / Test-OracleAvailable / Set-OracleConfig / New-OracleTempDir / Remove-OracleTempRoot
#   Invoke-Psnr / Invoke-PsnrAfterTransform
#   Get-ColorMetadata / Get-GainMapMetadata / Get-StreamInfo / Get-JxlInfo
#   Invoke-PerceptualScore / Test-PngStructure

$script:OracleDefaultTimeoutSec = 60
$script:OracleRepoRoot = ''
$script:OracleTempRoot = ''

# 工具表：Exe=可执行名；Dirs=仓库根相对候选目录；Env=单工具路径覆盖；DirEnv=目录覆盖；Smoke=存活探测参数
# ⚠ 与 PlatformServices.PlanSubDirs 的对应：ffmpeg-full / jxl(+jxl/bin) / exiftool / artifacts。
#   pngcicp 不在 PLAN 内，是仓内 .NET 工具（tools/src/pngcicp/bin/<Config>/net11.0）。
$script:OracleToolMap = @{
    'ffmpeg'      = @{ Exe = 'ffmpeg.exe';   Dirs = @('publish/PLAN/ffmpeg-full'); Env = 'FFMPEGGUI_ORACLE_FFMPEG';   DirEnv = 'FFMPEGGUI_FFMPEG_DIR'; Smoke = '-hide_banner -version' }
    'ffprobe'     = @{ Exe = 'ffprobe.exe';  Dirs = @('publish/PLAN/ffmpeg-full'); Env = 'FFMPEGGUI_ORACLE_FFPROBE';  DirEnv = 'FFMPEGGUI_FFMPEG_DIR'; Smoke = '-hide_banner -version' }
    'exiftool'    = @{ Exe = 'exiftool.exe'; Dirs = @('publish/PLAN/exiftool');    Env = 'FFMPEGGUI_ORACLE_EXIFTOOL'; DirEnv = '';                      Smoke = '-ver' }
    'jxlinfo'     = @{ Exe = 'jxlinfo.exe';  Dirs = @('publish/PLAN/jxl/bin', 'publish/PLAN/jxl'); Env = 'FFMPEGGUI_ORACLE_JXLINFO';     DirEnv = ''; Smoke = '--help' }
    'djxl'        = @{ Exe = 'djxl.exe';     Dirs = @('publish/PLAN/jxl/bin', 'publish/PLAN/jxl'); Env = 'FFMPEGGUI_ORACLE_DJXL';        DirEnv = ''; Smoke = '--help' }
    'cjxl'        = @{ Exe = 'cjxl.exe';     Dirs = @('publish/PLAN/jxl/bin', 'publish/PLAN/jxl'); Env = 'FFMPEGGUI_ORACLE_CJXL';        DirEnv = ''; Smoke = '--help' }
    'butteraugli' = @{ Exe = 'butteraugli_main.exe'; Dirs = @('publish/PLAN/jxl/bin', 'publish/PLAN/jxl'); Env = 'FFMPEGGUI_ORACLE_BUTTERAUGLI'; DirEnv = ''; Smoke = '' }
    'ssimulacra2' = @{ Exe = 'ssimulacra2.exe';      Dirs = @('publish/PLAN/jxl/bin', 'publish/PLAN/jxl'); Env = 'FFMPEGGUI_ORACLE_SSIMULACRA2'; DirEnv = ''; Smoke = '' }
    'dngtool'     = @{ Exe = 'dngtool.exe';          Dirs = @('publish/PLAN/artifacts'); Env = 'FFMPEGGUI_ORACLE_DNGTOOL';  DirEnv = ''; Smoke = '' }
    'pngcicp'     = @{ Exe = 'pngcicp.exe';          Dirs = @('tools/src/pngcicp/bin/Release/net11.0', 'tools/src/pngcicp/bin/Debug/net11.0'); Env = 'FFMPEGGUI_ORACLE_PNGCICP'; DirEnv = ''; Smoke = '' }
}

# ═══════════════════════════════════════════════════════════════════════════
# 配置 / 路径
# ═══════════════════════════════════════════════════════════════════════════

# 仓库根：优先 env，其次从本文件所在目录向上找 `publish/PLAN` 或 `src/FfmpegGui`。
function Get-OracleRepoRoot {
    if ($script:OracleRepoRoot -ne '') { return $script:OracleRepoRoot }
    $envRoot = [System.Environment]::GetEnvironmentVariable('FFMPEGGUI_ORACLE_REPO_ROOT')
    if ((-not [string]::IsNullOrWhiteSpace($envRoot)) -and (Test-Path -LiteralPath $envRoot)) {
        $script:OracleRepoRoot = [System.IO.Path]::GetFullPath($envRoot)
        return $script:OracleRepoRoot
    }
    $start = ''
    if (-not [string]::IsNullOrEmpty($PSScriptRoot)) { $start = $PSScriptRoot }
    else { $start = (Get-Location).Path }
    $p = $start
    for ($i = 0; $i -lt 5; $i++) {
        if ((Test-Path -LiteralPath (Join-Path $p 'publish/PLAN')) -or (Test-Path -LiteralPath (Join-Path $p 'src/FfmpegGui'))) {
            $script:OracleRepoRoot = [System.IO.Path]::GetFullPath($p)
            return $script:OracleRepoRoot
        }
        $up = Split-Path -Parent $p
        if ([string]::IsNullOrEmpty($up)) { break }
        $p = $up
    }
    $script:OracleRepoRoot = [System.IO.Path]::GetFullPath($start)
    return $script:OracleRepoRoot
}

# 覆盖库级默认（仓库根 / 超时秒数）。返回生效值，便于调用方回显。
function Set-OracleConfig {
    param(
        [string]$RepoRoot = '',
        [int]$TimeoutSec = 0
    )
    if (-not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        $script:OracleRepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
    }
    if ($TimeoutSec -gt 0) { $script:OracleDefaultTimeoutSec = $TimeoutSec }
    return [pscustomobject]@{
        RepoRoot   = (Get-OracleRepoRoot)
        TimeoutSec = $script:OracleDefaultTimeoutSec
    }
}

# 0 = 用库级默认（60s）；>0 = 本次调用覆盖。
function Resolve-OracleTimeout {
    param([int]$TimeoutSec = 0)
    if ($TimeoutSec -gt 0) { return $TimeoutSec }
    return $script:OracleDefaultTimeoutSec
}

# 运行级 GUID 临时目录（⚠ 固定目录会被并发同名实例互删 ⇒ 假红）。
function New-OracleTempDir {
    param([string]$Prefix = 'oracle')
    if ($script:OracleTempRoot -eq '') {
        $script:OracleTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ($Prefix + '_' + [guid]::NewGuid().ToString('N'))
    }
    $d = Join-Path $script:OracleTempRoot ([guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    return $d
}

function Remove-OracleTempRoot {
    if (($script:OracleTempRoot -ne '') -and (Test-Path -LiteralPath $script:OracleTempRoot)) {
        Remove-Item -Recurse -Force -LiteralPath $script:OracleTempRoot -ErrorAction SilentlyContinue
    }
    $script:OracleTempRoot = ''
}

# 在单个候选目录里找 exe；目录名带日期后缀的变体按「名字包含」匹配（对齐 FindPlanSubDir）。
function Resolve-OracleDirExe {
    param([string]$Dir, [string]$Exe)
    if ([string]::IsNullOrWhiteSpace($Dir)) { return $null }
    $direct = Join-Path $Dir $Exe
    if (Test-Path -LiteralPath $direct -PathType Leaf) { return [System.IO.Path]::GetFullPath($direct) }
    $leaf = Split-Path -Leaf $Dir
    $parent = Split-Path -Parent $Dir
    if ([string]::IsNullOrWhiteSpace($leaf) -or [string]::IsNullOrWhiteSpace($parent)) { return $null }
    if (-not (Test-Path -LiteralPath $parent)) { return $null }
    $cands = @()
    try {
        $cands = @(Get-ChildItem -LiteralPath $parent -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name.IndexOf($leaf, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 } |
            Sort-Object Name)
    }
    catch { return $null }
    foreach ($c in $cands) {
        $p = Join-Path $c.FullName $Exe
        if (Test-Path -LiteralPath $p -PathType Leaf) { return $p }
    }
    return $null
}

# 1) Get-ToolPath —— 返回 exe 全路径；找不到返回 $null（**显式失败**，不抛异常、不返回空串）。
function Get-ToolPath {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Override = ''
    )
    $key = $Name.ToLowerInvariant()
    if (-not $script:OracleToolMap.ContainsKey($key)) {
        Write-Warning ('Get-ToolPath: unknown oracle name: ' + $Name)
        return $null
    }
    if (-not [string]::IsNullOrWhiteSpace($Override)) {
        if (Test-Path -LiteralPath $Override -PathType Leaf) { return [System.IO.Path]::GetFullPath($Override) }
        return $null
    }
    $spec = $script:OracleToolMap[$key]

    $envVal = [System.Environment]::GetEnvironmentVariable($spec.Env)
    if (-not [string]::IsNullOrWhiteSpace($envVal)) {
        if (Test-Path -LiteralPath $envVal -PathType Leaf) { return [System.IO.Path]::GetFullPath($envVal) }
        # 设了 env 却指向不存在的文件：按显式失败处理，**不**静默回退到目录搜索
        Write-Warning ('Get-ToolPath: ' + $spec.Env + ' points to a missing file: ' + $envVal)
        return $null
    }

    if ($spec.DirEnv -ne '') {
        $dv = [System.Environment]::GetEnvironmentVariable($spec.DirEnv)
        if (-not [string]::IsNullOrWhiteSpace($dv)) {
            $cand = Join-Path $dv $spec.Exe
            if (Test-Path -LiteralPath $cand -PathType Leaf) { return [System.IO.Path]::GetFullPath($cand) }
        }
    }

    $dirs = @()
    $planEnv = [System.Environment]::GetEnvironmentVariable('FFMPEGGUI_ORACLE_PLAN_DIR')
    if (-not [string]::IsNullOrWhiteSpace($planEnv)) {
        foreach ($d in $spec.Dirs) {
            $rel = $d -replace '^publish/PLAN/?', ''
            $dirs += (Join-Path $planEnv $rel)
        }
    }
    $root = Get-OracleRepoRoot
    foreach ($d in $spec.Dirs) { $dirs += (Join-Path $root $d) }
    foreach ($d in $dirs) {
        $hit = Resolve-OracleDirExe -Dir $d -Exe $spec.Exe
        if ($null -ne $hit) { return $hit }
    }
    return $null
}

# ═══════════════════════════════════════════════════════════════════════════
# 子进程执行（带硬超时）
# ═══════════════════════════════════════════════════════════════════════════

# ⚠ `Start-Process -Wait` **没有** timeout 参数（无限等 ⇒ 挂死整轮）；本库改用 .NET Process
#   + `WaitForExit(ms)`，并**先**起异步读，避免管道缓冲写满造成的死锁。
#   返回：@{ Ok; ExitCode; TimedOut; StdOut; StdErr; Combined; Error }
#   ⚠ `Ok=$true` 只表示「进程正常结束且拿到了退出码」，**不代表 oracle 判定成功**。
function Invoke-ExeCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string]$Arguments = '',
        [int]$TimeoutSec = 60,
        [string]$WorkingDirectory = ''
    )
    $r = [pscustomobject]@{
        Ok = $false; ExitCode = $null; TimedOut = $false
        StdOut = ''; StdErr = ''; Combined = ''; Error = ''
    }
    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        $r.Error = 'executable not found: ' + $FilePath
        return $r
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    try { $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
    try { $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8 } catch { }
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) { $psi.WorkingDirectory = $WorkingDirectory }

    $p = $null
    try {
        $p = New-Object System.Diagnostics.Process
        $p.StartInfo = $psi
        [void]$p.Start()
    }
    catch {
        $r.Error = 'failed to start process: ' + $_.Exception.Message
        if ($null -ne $p) { try { $p.Dispose() } catch { } }
        return $r
    }
    $outTask = $null
    $errTask = $null
    try { $outTask = $p.StandardOutput.ReadToEndAsync() } catch { }
    try { $errTask = $p.StandardError.ReadToEndAsync() } catch { }
    $exited = $false
    try { $exited = $p.WaitForExit($TimeoutSec * 1000) } catch { }
    if (-not $exited) {
        $r.TimedOut = $true
        try { $p.Kill() } catch { }
        try { [void]$p.WaitForExit(5000) } catch { }
    }
    $stdout = ''
    $stderr = ''
    if ($null -ne $outTask) { try { $stdout = [string]$outTask.Result } catch { } }
    if ($null -ne $errTask) { try { $stderr = [string]$errTask.Result } catch { } }
    if (-not $r.TimedOut) {
        try { $r.ExitCode = $p.ExitCode } catch { $r.ExitCode = $null }
    }
    try { $p.Dispose() } catch { }
    $r.StdOut = $stdout
    $r.StdErr = $stderr
    $r.Combined = ($stdout + "`n" + $stderr)
    if ($r.TimedOut) { $r.Error = ('TIMEOUT after ' + [string]$TimeoutSec + 's') }
    elseif ($null -eq $r.ExitCode) { $r.Error = 'exit code unavailable' }
    else { $r.Ok = $true }
    return $r
}

# ═══════════════════════════════════════════════════════════════════════════
# 2) Test-OracleAvailable —— 前置检查：哪些 oracle 真的可用
# ═══════════════════════════════════════════════════════════════════════════

# 判据：Found（找到 exe）且 SmokeOk（存活探测：退出码 0 **或**有输出）。
# ⚠ 不把「找不到」当 SKIP：`Unavailable` 非空 ⇒ `Ok=$false`，调用方必须显式处理。
function Test-OracleAvailable {
    param(
        [int]$TimeoutSec = 0,
        [switch]$SkipSmoke
    )
    $to = Resolve-OracleTimeout $TimeoutSec
    $rows = @()
    $avail = @()
    $unavail = @()
    foreach ($k in ($script:OracleToolMap.Keys | Sort-Object)) {
        $spec = $script:OracleToolMap[$k]
        $p = Get-ToolPath -Name $k
        $found = ($null -ne $p)
        $smokeOk = $false
        $smokeExit = $null
        $timedOut = $false
        $note = ''
        if (-not $found) {
            $note = 'not found'
        }
        elseif ($SkipSmoke) {
            $smokeOk = $true
            $note = 'smoke skipped'
        }
        else {
            $r = Invoke-ExeCapture -FilePath $p -Arguments $spec.Smoke -TimeoutSec $to
            $smokeExit = $r.ExitCode
            $timedOut = $r.TimedOut
            if ($r.TimedOut) { $note = 'smoke TIMEOUT' }
            elseif ($r.Error -ne '') { $note = $r.Error }
            elseif (($r.ExitCode -eq 0) -or ($r.Combined.Trim() -ne '')) { $smokeOk = $true; $note = 'ok' }
            else { $note = 'smoke produced no output' }
        }
        $rows += [pscustomobject]@{
            Name = $k; Found = $found; Path = $p; SmokeOk = $smokeOk
            SmokeExit = $smokeExit; TimedOut = $timedOut; Note = $note
        }
        if ($found -and $smokeOk) { $avail += $k } else { $unavail += $k }
    }
    return [pscustomobject]@{
        Ok          = ($unavail.Count -eq 0)
        RepoRoot    = (Get-OracleRepoRoot)
        TimeoutSec  = $to
        Tools       = $rows
        Available   = $avail
        Unavailable = $unavail
    }
}

# ═══════════════════════════════════════════════════════════════════════════
# 3/4) PSNR（ffmpeg -lavfi psnr）
# ═══════════════════════════════════════════════════════════════════════════

# 通用核心：跑 `-i Ref -i Dist -lavfi <graph> -f null -` 并解析 `average:` 数值。
# ⚠ `inf` ⇒ Psnr=+Infinity 且 IsIdentical=$true（逐像素完全相同）。
# ⚠ 有 PSNR 行但 ffmpeg 退出码非 0 ⇒ 仍判 Ok=$false（不让「半截输出」伪装成绿）。
function Invoke-PsnrCore {
    param(
        [string]$Ref,
        [string]$Dist,
        [string]$FilterGraph,
        [int]$TimeoutSec = 0
    )
    $to = Resolve-OracleTimeout $TimeoutSec
    $out = [pscustomobject]@{
        Ok = $false; Psnr = $null; IsIdentical = $false
        Ref = $Ref; Dist = $Dist; FilterGraph = $FilterGraph
        TimedOut = $false; ExitCode = $null; Error = ''
    }
    if (-not (Test-Path -LiteralPath $Ref -PathType Leaf)) { $out.Error = 'reference file not found: ' + $Ref; return $out }
    if (-not (Test-Path -LiteralPath $Dist -PathType Leaf)) { $out.Error = 'distorted file not found: ' + $Dist; return $out }
    $ff = Get-ToolPath -Name 'ffmpeg'
    if ($null -eq $ff) { $out.Error = 'oracle ffmpeg not found (see Test-OracleAvailable)'; return $out }
    $argStr = '-hide_banner -nostdin -loglevel info -i "' + $Ref + '" -i "' + $Dist + '" -lavfi "' + $FilterGraph + '" -f null -'
    $r = Invoke-ExeCapture -FilePath $ff -Arguments $argStr -TimeoutSec $to
    $out.TimedOut = $r.TimedOut
    $out.ExitCode = $r.ExitCode
    if ($r.TimedOut) { $out.Error = $r.Error; return $out }
    if ($r.Error -ne '') { $out.Error = $r.Error; return $out }
    $m = [regex]::Match($r.Combined, 'average:([^\s]+)')
    if (-not $m.Success) {
        $out.Error = 'PSNR summary line not found in ffmpeg output (exit=' + [string]$r.ExitCode + ')'
        return $out
    }
    $tok = $m.Groups[1].Value
    if (($tok -eq 'inf') -or ($tok -eq '+inf')) {
        $out.Psnr = [double]::PositiveInfinity
        $out.IsIdentical = $true
    }
    elseif ($tok -eq '-inf') { $out.Psnr = [double]::NegativeInfinity }
    elseif (($tok -eq 'nan') -or ($tok -eq '-nan')) { $out.Psnr = [double]::NaN }
    else {
        $d = 0.0
        if ([double]::TryParse($tok, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) {
            $out.Psnr = $d
        }
        else {
            $out.Error = "cannot parse PSNR token '" + $tok + "'"
            return $out
        }
    }
    if ($r.ExitCode -ne 0) {
        $out.Error = 'ffmpeg exited with code ' + [string]$r.ExitCode
        return $out
    }
    $out.Ok = $true
    return $out
}

function Invoke-Psnr {
    param(
        [Parameter(Mandatory = $true)][string]$Ref,
        [Parameter(Mandatory = $true)][string]$Dist,
        [int]$TimeoutSec = 0
    )
    return Invoke-PsnrCore -Ref $Ref -Dist $Dist -FilterGraph 'psnr' -TimeoutSec $TimeoutSec
}

# 变换后比较：默认把 `-Filter` 作用在 **Ref** 上再与 Dist 比 PSNR。
#   生成图：`[0:v]<Filter>[__oracle_t0];[1:v][__oracle_t0]psnr`
# `-Target Dist` 则反过来。`-FullGraph` 时 `-Filter` 被**原样**当整张 `-lavfi` 用（调用方自己接 psnr）。
function Invoke-PsnrAfterTransform {
    param(
        [Parameter(Mandatory = $true)][string]$Ref,
        [Parameter(Mandatory = $true)][string]$Dist,
        [Parameter(Mandatory = $true)][string]$Filter,
        [ValidateSet('Ref', 'Dist')][string]$Target = 'Ref',
        [switch]$FullGraph,
        [int]$TimeoutSec = 0
    )
    $g = $Filter
    if (-not $FullGraph) {
        if ($Target -eq 'Ref') { $g = '[0:v]' + $Filter + '[__oracle_t0];[1:v][__oracle_t0]psnr' }
        else { $g = '[1:v]' + $Filter + '[__oracle_t0];[0:v][__oracle_t0]psnr' }
    }
    return Invoke-PsnrCore -Ref $Ref -Dist $Dist -FilterGraph $g -TimeoutSec $TimeoutSec
}

# ═══════════════════════════════════════════════════════════════════════════
# exiftool 输出解析（`-s -G1` 的 `[Group]  Tag  : Value` 行）
# ═══════════════════════════════════════════════════════════════════════════

# ⚠ 不用正则解析行（`\s?:` 之类会被 `verify-ps-compat.ps1` 的三元探针误伤）；用 IndexOf。
# ⚠ exiftool 是 Perl 打包版，stderr 常有 locale 警告 ⇒ 只解析 stdout。
function ConvertFrom-ExifTagLines {
    param([string]$Text)
    $h = @{}
    if ([string]::IsNullOrEmpty($Text)) { return $h }
    foreach ($line in ($Text -split "`n")) {
        $s = $line.TrimEnd("`r")
        if (-not $s.StartsWith('[')) { continue }
        $i = $s.IndexOf(']')
        if ($i -lt 1) { continue }
        $grp = $s.Substring(1, $i - 1)
        $rest = $s.Substring($i + 1)
        $c = $rest.IndexOf(':')
        if ($c -lt 0) { continue }
        $tag = $rest.Substring(0, $c).Trim()
        $val = $rest.Substring($c + 1).Trim()
        if ($tag -eq '') { continue }
        if (-not $h.ContainsKey($tag)) { $h[$tag] = $val }
        $h['__group:' + $tag] = $grp
    }
    return $h
}

function Convert-ToDoubleOrNull {
    param($Value)
    if ($null -eq $Value) { return $null }
    $s = ([string]$Value).Trim()
    if ($s -eq '') { return $null }
    $d = 0.0
    if ([double]::TryParse($s, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    return $null
}

function Get-FirstNonEmptyLine {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    foreach ($l in ($Text -split "`n")) {
        $t = $l.Trim()
        if ($t -ne '') { return $t }
    }
    return ''
}

function Get-JsonField {
    param($Obj, [string]$Name)
    if ($null -eq $Obj) { return $null }
    $p = $Obj.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    $v = $p.Value
    if ($null -eq $v) { return $null }
    $s = [string]$v
    if (($s -eq '') -or ($s -eq 'N/A')) { return $null }
    return $s
}

# ═══════════════════════════════════════════════════════════════════════════
# 5) Get-ColorMetadata（exiftool 首选；JXL 自动改走 jxlinfo）
# ═══════════════════════════════════════════════════════════════════════════

# ⚠⚠ 关键区分：**「无 CICP」与「读不出来」必须可区分**。
#   · 文件正常但确实没有 cICP chunk ⇒ 全字段 $null / HasIcc=$false 且 **Ok=$true**（这是**确定的答案**）；
#   · 工具缺失 / 超时 / 解析不出 / 文件不存在 ⇒ **Ok=$false + Error 非空**。
function Get-ColorMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutSec = 0
    )
    $to = Resolve-OracleTimeout $TimeoutSec
    $out = [pscustomobject]@{
        Ok = $false; Path = $Path; Source = ''
        Primaries = $null; Transfer = $null; Matrix = $null; Range = $null
        IccProfileName = $null; IccProfileDescription = $null; HasIcc = $false
        TimedOut = $false; ExitCode = $null; Error = ''
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $out.Error = 'file not found: ' + $Path; return $out }

    if ($Path -imatch '\.jxl$') {
        $out.Source = 'jxlinfo'
        $j = Get-JxlInfo -Path $Path -TimeoutSec $to
        $out.TimedOut = $j.TimedOut
        if (-not $j.Ok) { $out.Error = 'jxlinfo: ' + $j.Error; return $out }
        $out.HasIcc = $j.HasIcc
        $cs = [string]$j.ColorSpace
        if ($j.HasIcc) {
            # 内嵌 ICC 时 jxlinfo 不报 primaries/transfer，只报 profile 描述 ⇒ 原样给出，不猜。
            $out.IccProfileDescription = $cs
        }
        else {
            # 从 jxlinfo 的 Color space 行里挑出 primaries / transfer 两个 token（不做任何映射/换算）
            foreach ($part in ($cs -split ',')) {
                $t = $part.Trim()
                if (($t.IndexOf('primaries') -ge 0) -and ($null -eq $out.Primaries)) { $out.Primaries = $t }
                if (($t.IndexOf('transfer') -ge 0) -and ($null -eq $out.Transfer)) { $out.Transfer = $t }
            }
        }
        $out.Ok = $true
        return $out
    }

    $out.Source = 'exiftool'
    $ex = Get-ToolPath -Name 'exiftool'
    if ($null -eq $ex) { $out.Error = 'oracle exiftool not found (see Test-OracleAvailable)'; return $out }
    $argStr = '-s -G1 -ColorPrimaries -TransferCharacteristics -MatrixCoefficients -VideoFullRangeFlag ' +
        '-ICC_Profile -ICC_Profile:ProfileName -ICC_Profile:ProfileDescription "' + $Path + '"'
    $r = Invoke-ExeCapture -FilePath $ex -Arguments $argStr -TimeoutSec $to
    $out.TimedOut = $r.TimedOut
    $out.ExitCode = $r.ExitCode
    if ($r.TimedOut) { $out.Error = $r.Error; return $out }
    if ($r.Error -ne '') { $out.Error = $r.Error; return $out }
    if ($r.ExitCode -ne 0) {
        $out.Error = 'exiftool exit code ' + [string]$r.ExitCode + ' :: ' + (Get-FirstNonEmptyLine $r.StdErr)
        return $out
    }
    $tags = ConvertFrom-ExifTagLines -Text $r.StdOut
    $out.Primaries = $tags['ColorPrimaries']
    $out.Transfer = $tags['TransferCharacteristics']
    $out.Matrix = $tags['MatrixCoefficients']
    $out.Range = $tags['VideoFullRangeFlag']
    $out.IccProfileName = $tags['ProfileName']
    $out.IccProfileDescription = $tags['ProfileDescription']
    $out.HasIcc = ($tags.ContainsKey('ICC_Profile') -or $tags.ContainsKey('ProfileDescription') -or $tags.ContainsKey('ProfileName'))
    $out.Ok = $true
    return $out
}

# ═══════════════════════════════════════════════════════════════════════════
# 6) Get-GainMapMetadata（exiftool -G1 -XMP:all）
# ═══════════════════════════════════════════════════════════════════════════

function Get-GainMapMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutSec = 0
    )
    $to = Resolve-OracleTimeout $TimeoutSec
    $out = [pscustomobject]@{
        Ok = $false; Path = $Path; HasGainMap = $false
        GainMapMin = $null; GainMapMax = $null; HdrCapacityMax = $null
        Version = $null; Semantic = $null
        TimedOut = $false; ExitCode = $null; Error = ''
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $out.Error = 'file not found: ' + $Path; return $out }
    $ex = Get-ToolPath -Name 'exiftool'
    if ($null -eq $ex) { $out.Error = 'oracle exiftool not found (see Test-OracleAvailable)'; return $out }
    $argStr = '-s -G1 -XMP:all "' + $Path + '"'
    $r = Invoke-ExeCapture -FilePath $ex -Arguments $argStr -TimeoutSec $to
    $out.TimedOut = $r.TimedOut
    $out.ExitCode = $r.ExitCode
    if ($r.TimedOut) { $out.Error = $r.Error; return $out }
    if ($r.Error -ne '') { $out.Error = $r.Error; return $out }
    if ($r.ExitCode -ne 0) {
        $out.Error = 'exiftool exit code ' + [string]$r.ExitCode + ' :: ' + (Get-FirstNonEmptyLine $r.StdErr)
        return $out
    }
    $tags = ConvertFrom-ExifTagLines -Text $r.StdOut
    $semantic = $tags['DirectoryItemSemantic']
    $hasHdrgm = $false
    foreach ($k in $tags.Keys) {
        if ($k.StartsWith('__group:') -and ([string]$tags[$k]).StartsWith('XMP-hdrgm')) { $hasHdrgm = $true; break }
    }
    $hasSemantic = ($null -ne $semantic) -and (([string]$semantic).IndexOf('GainMap') -ge 0)
    $out.Semantic = $semantic
    $out.Version = $tags['Version']
    $out.GainMapMin = Convert-ToDoubleOrNull $tags['GainMapMin']
    $out.GainMapMax = Convert-ToDoubleOrNull $tags['GainMapMax']
    $out.HdrCapacityMax = Convert-ToDoubleOrNull $tags['HDRCapacityMax']
    $out.HasGainMap = ($hasHdrgm -or $hasSemantic -or $tags.ContainsKey('GainMapMax') -or $tags.ContainsKey('GainMapMin'))
    $out.Ok = $true
    return $out
}

# ═══════════════════════════════════════════════════════════════════════════
# 7) Get-StreamInfo（ffprobe）
# ═══════════════════════════════════════════════════════════════════════════

# HasAlpha：按 pix_fmt 名字判定（带 alpha 的采样布局）；pal8 等「可能有 tRNS」的不猜 ⇒ $false。
function Test-PixFmtHasAlpha {
    param([string]$PixFmt)
    if ([string]::IsNullOrWhiteSpace($PixFmt)) { return $false }
    return ($PixFmt -imatch '^(ya[0-9]+|yuva[0-9a-z]*|rgba[0-9a-z]*|bgra[0-9a-z]*|argb[0-9a-z]*|abgr[0-9a-z]*|gbrap[0-9a-z]*)$')
}

function Get-StreamInfo {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutSec = 0
    )
    $to = Resolve-OracleTimeout $TimeoutSec
    $out = [pscustomobject]@{
        Ok = $false; Path = $Path
        PixFmt = $null; Width = $null; Height = $null; Frames = $null; HasAlpha = $false
        ColorPrimaries = $null; ColorTransfer = $null; ColorSpace = $null; ColorRange = $null
        TimedOut = $false; ExitCode = $null; Error = ''
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $out.Error = 'file not found: ' + $Path; return $out }
    $fp = Get-ToolPath -Name 'ffprobe'
    if ($null -eq $fp) { $out.Error = 'oracle ffprobe not found (see Test-OracleAvailable)'; return $out }
    $argStr = '-v error -select_streams v:0 -show_entries ' +
        'stream=pix_fmt,width,height,nb_frames,color_primaries,color_transfer,color_space,color_range ' +
        '-of json "' + $Path + '"'
    $r = Invoke-ExeCapture -FilePath $fp -Arguments $argStr -TimeoutSec $to
    $out.TimedOut = $r.TimedOut
    $out.ExitCode = $r.ExitCode
    if ($r.TimedOut) { $out.Error = $r.Error; return $out }
    if ($r.Error -ne '') { $out.Error = $r.Error; return $out }
    if ($r.ExitCode -ne 0) {
        $out.Error = 'ffprobe exit code ' + [string]$r.ExitCode + ' :: ' + (Get-FirstNonEmptyLine $r.StdErr)
        return $out
    }
    if ([string]::IsNullOrWhiteSpace($r.StdOut)) {
        $out.Error = 'ffprobe produced no JSON :: ' + (Get-FirstNonEmptyLine $r.StdErr)
        return $out
    }
    $j = $null
    try { $j = $r.StdOut | ConvertFrom-Json }
    catch { $out.Error = 'cannot parse ffprobe JSON: ' + $_.Exception.Message; return $out }
    $streams = @($j.streams)
    if (($streams.Count -lt 1) -or ($null -eq $streams[0])) { $out.Error = 'ffprobe JSON has no video stream'; return $out }
    $s = $streams[0]
    $out.PixFmt = Get-JsonField $s 'pix_fmt'
    $out.ColorPrimaries = Get-JsonField $s 'color_primaries'
    $out.ColorTransfer = Get-JsonField $s 'color_transfer'
    $out.ColorSpace = Get-JsonField $s 'color_space'
    $out.ColorRange = Get-JsonField $s 'color_range'
    $w = Get-JsonField $s 'width'
    if ($null -ne $w) { $out.Width = [int]$w }
    $h = Get-JsonField $s 'height'
    if ($null -ne $h) { $out.Height = [int]$h }
    $f = Get-JsonField $s 'nb_frames'
    if ($null -ne $f) { $out.Frames = [int]$f }
    $out.HasAlpha = (Test-PixFmtHasAlpha $out.PixFmt)
    if ($null -eq $out.PixFmt) { $out.Error = 'ffprobe JSON has no pix_fmt'; return $out }
    $out.Ok = $true
    return $out
}

# ═══════════════════════════════════════════════════════════════════════════
# 8) Get-JxlInfo（jxlinfo；⚠ exiftool 读不出 JXL 的 ICC，必须走它）
# ═══════════════════════════════════════════════════════════════════════════

function Get-JxlInfo {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutSec = 0
    )
    $to = Resolve-OracleTimeout $TimeoutSec
    $out = [pscustomobject]@{
        Ok = $false; Path = $Path
        Width = $null; Height = $null; BitDepth = $null; ColorSpace = $null
        HasIcc = $false; IsContainer = $false; Lossless = $null; Raw = ''
        TimedOut = $false; ExitCode = $null; Error = ''
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $out.Error = 'file not found: ' + $Path; return $out }
    $jx = Get-ToolPath -Name 'jxlinfo'
    if ($null -eq $jx) { $out.Error = 'oracle jxlinfo not found (see Test-OracleAvailable)'; return $out }
    $r = Invoke-ExeCapture -FilePath $jx -Arguments ('"' + $Path + '"') -TimeoutSec $to
    $out.TimedOut = $r.TimedOut
    $out.ExitCode = $r.ExitCode
    if ($r.TimedOut) { $out.Error = $r.Error; return $out }
    if ($r.Error -ne '') { $out.Error = $r.Error; return $out }
    $out.Raw = $r.StdOut
    if ($r.ExitCode -ne 0) {
        $out.Error = 'jxlinfo exit code ' + [string]$r.ExitCode + ' :: ' + (Get-FirstNonEmptyLine $r.StdErr)
        return $out
    }
    $text = $r.StdOut + "`n" + $r.StdErr
    $m = [regex]::Match($text, 'JPEG XL image, ([0-9]+)x([0-9]+)')
    if (-not $m.Success) { $out.Error = 'cannot parse jxlinfo image line'; return $out }
    $out.Width = [int]$m.Groups[1].Value
    $out.Height = [int]$m.Groups[2].Value
    $mb = [regex]::Match($text, '([0-9]+)-bit')
    if ($mb.Success) { $out.BitDepth = [int]$mb.Groups[1].Value }
    $mc = [regex]::Match($text, 'Color space: (.*)')
    if ($mc.Success) {
        $cs = $mc.Groups[1].Value.Trim()
        $out.ColorSpace = $cs
        if ($cs.IndexOf('ICC profile') -ge 0) { $out.HasIcc = $true }
    }
    if ($text.IndexOf('file format container') -ge 0) { $out.IsContainer = $true }
    if ($text.IndexOf('possibly) lossless') -ge 0) { $out.Lossless = $null }
    elseif ($text.IndexOf('lossless') -ge 0) { $out.Lossless = $true }
    elseif ($text.IndexOf('lossy') -ge 0) { $out.Lossless = $false }
    $out.Ok = $true
    return $out
}

# ═══════════════════════════════════════════════════════════════════════════
# 9) Invoke-PerceptualScore（butteraugli + ssimulacra2）
# ═══════════════════════════════════════════════════════════════════════════

# butteraugli：越小越好；ssimulacra2：越大越好（100 上限）。
# Ok 仅在**两者都**解析成功时为 $true；各自失败原因分别落在 ButteraugliError / Ssimulacra2Error。
function Invoke-PerceptualScore {
    param(
        [Parameter(Mandatory = $true)][string]$Ref,
        [Parameter(Mandatory = $true)][string]$Dist,
        [int]$TimeoutSec = 0
    )
    $to = Resolve-OracleTimeout $TimeoutSec
    $out = [pscustomobject]@{
        Ok = $false; Ref = $Ref; Dist = $Dist
        Butteraugli = $null; Ssimulacra2 = $null
        ButteraugliError = ''; Ssimulacra2Error = ''
        TimedOut = $false; Error = ''
    }
    if (-not (Test-Path -LiteralPath $Ref -PathType Leaf)) { $out.Error = 'reference file not found: ' + $Ref; return $out }
    if (-not (Test-Path -LiteralPath $Dist -PathType Leaf)) { $out.Error = 'distorted file not found: ' + $Dist; return $out }
    $pair = '"' + $Ref + '" "' + $Dist + '"'

    $b = Get-ToolPath -Name 'butteraugli'
    if ($null -eq $b) { $out.ButteraugliError = 'oracle butteraugli not found' }
    else {
        $rb = Invoke-ExeCapture -FilePath $b -Arguments $pair -TimeoutSec $to
        if ($rb.TimedOut) { $out.TimedOut = $true; $out.ButteraugliError = $rb.Error }
        elseif ($rb.Error -ne '') { $out.ButteraugliError = $rb.Error }
        else {
            $d = Convert-ToDoubleOrNull (Get-FirstNonEmptyLine $rb.StdOut)
            if ($null -eq $d) { $out.ButteraugliError = 'cannot parse butteraugli output (exit=' + [string]$rb.ExitCode + ')' }
            else { $out.Butteraugli = $d }
        }
    }

    $ss = Get-ToolPath -Name 'ssimulacra2'
    if ($null -eq $ss) { $out.Ssimulacra2Error = 'oracle ssimulacra2 not found' }
    else {
        $rs = Invoke-ExeCapture -FilePath $ss -Arguments $pair -TimeoutSec $to
        if ($rs.TimedOut) { $out.TimedOut = $true; $out.Ssimulacra2Error = $rs.Error }
        elseif ($rs.Error -ne '') { $out.Ssimulacra2Error = $rs.Error }
        else {
            $d2 = Convert-ToDoubleOrNull (Get-FirstNonEmptyLine $rs.StdOut)
            if ($null -eq $d2) { $out.Ssimulacra2Error = 'cannot parse ssimulacra2 output (exit=' + [string]$rs.ExitCode + ')' }
            else { $out.Ssimulacra2 = $d2 }
        }
    }

    $errs = @()
    if ($out.ButteraugliError -ne '') { $errs += ('butteraugli: ' + $out.ButteraugliError) }
    if ($out.Ssimulacra2Error -ne '') { $errs += ('ssimulacra2: ' + $out.Ssimulacra2Error) }
    $out.Error = ($errs -join '; ')
    $out.Ok = (($null -ne $out.Butteraugli) -and ($null -ne $out.Ssimulacra2))
    return $out
}

# ═══════════════════════════════════════════════════════════════════════════
# 10) Test-PngStructure（**自己按 PNG 规范读字节**，不调外部工具）
# ═══════════════════════════════════════════════════════════════════════════

# 断言（v2，2026-09-19 加固 P3-D）：
#   ① PNG 8 字节签名 89 50 4E 47 0D 0A 1A 0A；
#   ② 首 chunk 必须是 IHDR **且长度恰好 13**（旧版是 `-ge 13`，偏松）；
#   ③ 末 chunk 必须是 IEND 且长度 0；
#   ④ **逐 chunk 校验 CRC-32/IEEE**（多项式 0xEDB88320 反射式；校验范围 = chunk type 的 4 字节
#      + chunk data，**不含**长度字段；与文件里末尾 4 字节大端无符号比较）。
#      ⚠ 旧版**完全不校验 CRC** ⇒ IDAT 数据被翻转的 PNG 依然判 Ok=$true（真实缺口，本版补上）。
#   ⑤ IEND 之后**不得**有尾随字节（旧版在 IEND 处 break，尾部垃圾被静默忽略）；
#   ⑥ IHDR 字段合法性：宽高 > 0；colorType 只能是 0/2/3/4/6 且 bitDepth 组合合法；
#      Compression(偏移 10) == 0；Filter(偏移 11) == 0；Interlace(偏移 12) 只能是 0/1；
#   ⑦ 每个 chunk 的 (length + type + data + crc) 都不得越过文件末尾（边界自洽）。
# 背景见 `tests/scripts/verify-png-signature.ps1`：只写 `-c:v apng` 不给 `-f apng` 时
#   ffmpeg 会产出**没有签名/IHDR/IEND 的裸块流**，而退出码为 0（静默损坏）。
#
# ⚠ 失败一律**显式记账**：不抛异常、不返回看起来正常的默认值、**也绝不沿用上一次调用的值**
#   —— 每次调用都新建返回对象；读文件失败 ⇒ 所有读数字段保持 $null / 0 且 Error 非空。
function Test-PngStructure {
    param([Parameter(Mandatory = $true)][string]$Path)
    $out = [pscustomobject]@{
        Ok = $false; Path = $Path
        HasSignature = $false; HasIhdr = $false; HasIend = $false
        ChunkCount = 0; ChunkTypes = @(); IendLength = $null
        Width = $null; Height = $null; BitDepth = $null; ColorType = $null
        CrcChecked = 0; CrcFailChunk = $null; CrcFailOffset = $null
        CrcExpected = $null; CrcActual = $null; TrailingBytes = 0
        IhdrLength = $null; Compression = $null; Filter = $null; Interlace = $null
        Error = ''
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $out.Error = 'file not found: ' + $Path; return $out }
    $b = $null
    try { $b = [System.IO.File]::ReadAllBytes($Path) }
    catch { $out.Error = 'read failed: ' + $_.Exception.Message; return $out }
    if ($null -eq $b) { $out.Error = 'read returned no bytes: ' + $Path; return $out }
    if ($b.Length -lt 8) { $out.Error = 'file too short (' + [string]$b.Length + ' bytes)'; return $out }

    $sig = @(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    for ($i = 0; $i -lt 8; $i++) {
        if ($b[$i] -ne $sig[$i]) {
            $out.Error = 'bad PNG signature (first byte 0x' + $b[0].ToString('X2') + ')'
            return $out
        }
    }
    $out.HasSignature = $true

    $types = New-Object System.Collections.ArrayList
    $len = [long]$b.Length
    $pos = [long]8
    $bad = ''
    $crcChecked = 0
    while (($pos + 8) -le $len) {
        $cl = [long]$b[$pos] * 16777216 + [long]$b[$pos + 1] * 65536 + [long]$b[$pos + 2] * 256 + [long]$b[$pos + 3]
        $t = [System.Text.Encoding]::ASCII.GetString($b, [int]($pos + 4), 4)
        $dataStart = $pos + 8
        $next = $dataStart + $cl + 4
        if ($next -gt $len) {
            $bad = 'chunk ' + $t + ' at offset ' + [string]$pos + ' overruns file (declared ' + [string]$cl +
                ' bytes, only ' + [string]($len - $dataStart) + ' left)'
            break
        }
        # ── CRC-32/IEEE：范围 = chunk type(4) + chunk data，不含 length ──────────
        $calc = Get-PngCrc32 -Bytes $b -Start ([int]($pos + 4)) -Count ([int](4 + $cl))
        $crcOff = [int]($dataStart + $cl)
        $stored = [long]$b[$crcOff] * 16777216 + [long]$b[$crcOff + 1] * 65536 + [long]$b[$crcOff + 2] * 256 + [long]$b[$crcOff + 3]
        if ($calc -ne $stored) {
            $out.CrcFailChunk = $t
            $out.CrcFailOffset = [int]$pos
            $out.CrcExpected = $stored
            $out.CrcActual = $calc
            $bad = 'CRC mismatch in chunk ' + $t + ' at offset ' + [string]$pos + ' (crc field at offset ' + [string]$crcOff +
                '): stored 0x' + $stored.ToString('X8') + ', computed 0x' + $calc.ToString('X8')
            break
        }
        $crcChecked++
        [void]$types.Add($t)
        if ($t -eq 'IHDR') {
            $out.IhdrLength = $cl
            if ($cl -ne 13) {
                $bad = 'IHDR length is ' + [string]$cl + ' at offset ' + [string]$pos + ', expected exactly 13'
                break
            }
            $ds = [int]$dataStart
            $out.Width = [int]([long]$b[$ds] * 16777216 + [long]$b[$ds + 1] * 65536 + [long]$b[$ds + 2] * 256 + [long]$b[$ds + 3])
            $out.Height = [int]([long]$b[$ds + 4] * 16777216 + [long]$b[$ds + 5] * 65536 + [long]$b[$ds + 6] * 256 + [long]$b[$ds + 7])
            $out.BitDepth = [int]$b[$ds + 8]
            $out.ColorType = [int]$b[$ds + 9]
            $out.Compression = [int]$b[$ds + 10]
            $out.Filter = [int]$b[$ds + 11]
            $out.Interlace = [int]$b[$ds + 12]
            $ihdrErr = Get-PngIhdrFieldError -Width $out.Width -Height $out.Height -BitDepth $out.BitDepth `
                -ColorType $out.ColorType -Compression $out.Compression -Filter $out.Filter -Interlace $out.Interlace
            if ($ihdrErr -ne '') { $bad = $ihdrErr; break }
        }
        if ($t -eq 'IEND') {
            $out.IendLength = $cl
            $out.TrailingBytes = [int]($len - $next)
            if ($out.TrailingBytes -gt 0) {
                $bad = 'IEND at offset ' + [string]$pos + ' is followed by ' + [string]$out.TrailingBytes + ' trailing byte(s)'
                break
            }
            $pos = $next
            break
        }
        $pos = $next
        if ($types.Count -gt 100000) { $bad = 'chunk count exceeds sanity limit'; break }
    }

    $out.ChunkCount = $types.Count
    $out.ChunkTypes = @($types)
    $out.CrcChecked = $crcChecked
    if ($bad -ne '') { $out.Error = $bad; return $out }
    if ($types.Count -lt 1) { $out.Error = 'no chunks found after signature'; return $out }
    if ($types[0] -ne 'IHDR') { $out.Error = 'first chunk is ' + [string]$types[0] + ', expected IHDR'; return $out }
    $out.HasIhdr = $true
    if ($types[$types.Count - 1] -ne 'IEND') { $out.Error = 'IEND not found (file truncated)'; return $out }
    $out.HasIend = $true
    if ($out.IendLength -ne 0) { $out.Error = 'IEND length is ' + [string]$out.IendLength + ', expected 0'; return $out }
    $out.Ok = $true
    return $out
}

# ── PNG 用的 CRC-32/IEEE 原语（内部助手，不在导出清单内）──────────────────────
# ⚠⚠ 实测坑（本文件 2026-09-19 踩过，第一次自检 21 条红）：
#   PowerShell 的十六进制字面量按 **Int32 有符号**解析 —— `0xFFFFFFFF` 是 **Int32 -1**，
#   `0xEDB88320` 是 **Int32 -306674912**（不是 Int64 的无符号值）。
#   ⇒ `-band 0xFFFFFFFF` 退化成 `-band -1`（**空操作**）；`-bxor 0xFFFFFFFF` 退化成 **64 位取反**；
#      多项式在 `-bxor` 时**符号扩展** ⇒ CRC 全错，且与同样错误的构造器「自洽」而假绿。
#   ⇒ 必须用 `L` 后缀或十进制常量强制 Int64。下面两个常量即为此而设。
# ⚠ 全程用 [long] 而非 [uint32]：PS 5.1 下 [uint32] 的 -bxor/-shr 结果类型不稳定。
# ⚠ 这张表是**常量**（不是「上一次调用的结果」），与「本库不做结果缓存」的铁律不冲突。
$script:OraclePngMask32 = [long]4294967295   # 0xFFFFFFFFL
$script:OraclePngPoly = [long]3988292384     # 0xEDB88320L, reflected CRC-32/IEEE polynomial
$script:OraclePngCrcTable = $null

function Get-PngCrcTable {
    if ($null -ne $script:OraclePngCrcTable) { return $script:OraclePngCrcTable }
    $mask = $script:OraclePngMask32
    $poly = $script:OraclePngPoly
    $t = New-Object 'long[]' 256
    for ($n = 0; $n -lt 256; $n++) {
        $c = [long]$n
        for ($k = 0; $k -lt 8; $k++) {
            if (($c -band 1) -ne 0) { $c = ($poly -bxor ($c -shr 1)) -band $mask }
            else { $c = $c -shr 1 }
        }
        $t[$n] = $c
    }
    $script:OraclePngCrcTable = $t
    return $t
}

# 增量 CRC：从 $Crc 起，继续吃 $Bytes[$Start .. $Start+$Count-1]。
# 调用方约定：初值 0xFFFFFFFF；结束时再 -bxor 0xFFFFFFFF。
function Update-PngCrc {
    param([long]$Crc, [byte[]]$Bytes, [int]$Start, [int]$Count)
    $mask = $script:OraclePngMask32
    $t = Get-PngCrcTable
    $c = $Crc -band $mask
    for ($i = 0; $i -lt $Count; $i++) {
        $idx = [int](($c -bxor [long]$Bytes[$Start + $i]) -band 0xFF)
        $c = ($t[$idx] -bxor ($c -shr 8)) -band $mask
    }
    return $c
}

# 一整段字节的 PNG CRC（返回 0..0xFFFFFFFF 的 Int64）。
function Get-PngCrc32 {
    param([byte[]]$Bytes, [int]$Start, [int]$Count)
    $mask = $script:OraclePngMask32
    $c = Update-PngCrc -Crc $mask -Bytes $Bytes -Start $Start -Count $Count
    return (($c -bxor $mask) -band $mask)
}

# IHDR 字段合法性；合法返回 ''，否则返回点名字段的错误串。
function Get-PngIhdrFieldError {
    param(
        [int]$Width, [int]$Height, [int]$BitDepth, [int]$ColorType,
        [int]$Compression, [int]$Filter, [int]$Interlace
    )
    if ($Width -le 0) { return 'IHDR width is ' + [string]$Width + ', expected > 0' }
    if ($Height -le 0) { return 'IHDR height is ' + [string]$Height + ', expected > 0' }
    if (($ColorType -ne 0) -and ($ColorType -ne 2) -and ($ColorType -ne 3) -and ($ColorType -ne 4) -and ($ColorType -ne 6)) {
        return 'IHDR color type is ' + [string]$ColorType + ', expected one of 0/2/3/4/6'
    }
    $allowed = @()
    if ($ColorType -eq 0) { $allowed = @(1, 2, 4, 8, 16) }
    elseif ($ColorType -eq 2) { $allowed = @(8, 16) }
    elseif ($ColorType -eq 3) { $allowed = @(1, 2, 4, 8) }
    else { $allowed = @(8, 16) }
    if ($allowed -notcontains $BitDepth) {
        $list = (($allowed | ForEach-Object { [string]$_ }) -join '/')
        return 'IHDR bit depth ' + [string]$BitDepth + ' is invalid for color type ' + [string]$ColorType + ' (allowed ' + $list + ')'
    }
    if ($Compression -ne 0) { return 'IHDR compression method is ' + [string]$Compression + ', expected 0' }
    if ($Filter -ne 0) { return 'IHDR filter method is ' + [string]$Filter + ', expected 0' }
    if (($Interlace -ne 0) -and ($Interlace -ne 1)) { return 'IHDR interlace method is ' + [string]$Interlace + ', expected 0 or 1' }
    return ''
}
