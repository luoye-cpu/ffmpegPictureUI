# _gen-mode-split.ps1 —— 把「一个组一个巨型 Group.cs」细分成「一个 mode 一个文件」
#
# 动机（用户原话）：「测试应该是一个个项目，这样可以单独测试部分项目避免一点更改就需要跑一大堆测试」。
#   2026-10-05 的物理拆分做到了「一个**子系统**一个工程」，但工程内部仍是**单个巨型 .cs**
#   （实测 core = 2437 行 / 7 mode；diagnostics = 1200+ 行 / 8 mode）
#   ⇒ 改一个 mode 仍要碰整份文件。本工具做**第二级细分**：按 mode 切片。
#
# ⚠⚠ 纪律（沿用 2026-10-05 拆分的既定规则，见 docs/TEST_SPLIT_TEMPLATE.md）：
#   ① 方法体**逐字复制**（不改逻辑、不改字符串、不改缩进）；
#   ② **不新增/不删除**任何断言 ⇒ 拆分后各 mode 读数之和必须与拆分前**逐字一致**；
#   ③ 共享 helper（Check/Skip/Find 等）**只此一份** —— 拆出后统一留在 `*Group.Shared.cs`
#      并把原类改成 `partial class`（同一类的另一半），调用点零改动；
#   ④ 每个 mode 一个文件，文件名 = mode 名（`Modes/<mode>.cs`）。
#
# 用法：
#   pwsh -File tests/scripts/_gen-mode-split.ps1 -GroupDir tests/core -GroupFile CoreGroup.cs -WhatIf
#   pwsh -File tests/scripts/_gen-mode-split.ps1 -GroupDir tests/core -GroupFile CoreGroup.cs -Emit

param(
    [Parameter(Mandatory = $true)][string]$GroupDir,
    [Parameter(Mandatory = $true)][string]$GroupFile,
    [switch]$WhatIf,
    [switch]$Emit
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$file = Join-Path $root (Join-Path $GroupDir $GroupFile)
if (-not (Test-Path -LiteralPath $file)) { throw "not found: $file" }

$text  = [System.IO.File]::ReadAllText($file)
$lines = $text -split "`r?`n"

# ── 1) 定位 namespace / class / 类体范围 ──
$nsIdx = -1; $clsIdx = -1; $openIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($nsIdx -lt 0 -and $lines[$i] -match '^\s*namespace\s+') { $nsIdx = $i }
    if ($clsIdx -lt 0 -and $lines[$i] -match '^\s*(internal|public)\s+static\s+class\s+') { $clsIdx = $i }
    if ($clsIdx -ge 0 -and $openIdx -lt 0 -and $lines[$i] -match '\{\s*$') { $openIdx = $i; break }
}
if ($nsIdx -lt 0 -or $clsIdx -lt 0 -or $openIdx -lt 0) { throw 'cannot locate namespace/class/opening brace' }
$closeIdx = -1
for ($i = $lines.Count - 1; $i -gt $openIdx; $i--) { if ($lines[$i] -match '^\s*\}\s*$') { $closeIdx = $i; break } }
if ($closeIdx -lt 0) { throw 'cannot locate class closing brace' }

$nsLine  = $lines[$nsIdx]
$clsLine = $lines[$clsIdx]
$className = ([regex]::Match($clsLine, 'class\s+([A-Za-z_]\w*)')).Groups[1].Value
if (-not $className) { throw "cannot parse class name from: $clsLine" }

# 文件头（using 段）= namespace 之前的全部内容，逐字保留
$header = ($lines[0..($nsIdx - 1)]) -join "`r`n"

# ── 2) 切顶层成员（花括号配平；对 `=>` 单行体特判）──
$members = New-Object System.Collections.ArrayList
$i = $openIdx + 1
while ($i -lt $closeIdx) {
    $l = $lines[$i]
    if ($l -match '^\s*$' -or $l -match '^\s*//') { $i++; continue }
    if ($l -notmatch '\(') { $i++; continue }
    $start = $i; $depth = 0; $j = $i; $end = -1
    while ($j -lt $closeIdx) {
        $cur  = $lines[$j]
        $code = $cur -replace '//.*$', ''
        # 去掉字符串字面量，避免把字符串里的花括号算进配平
        $code = [regex]::Replace($code, '"(?:\\.|[^"\\])*"', '""')
        $depth += ([regex]::Matches($code, '\{')).Count
        $depth -= ([regex]::Matches($code, '\}')).Count
        if ($depth -le 0 -and $j -gt $start -and $cur -match '\}\s*$') { $end = $j; break }
        if ($depth -eq 0 -and $cur -match '=>.*;\s*$') { $end = $j; break }
        $j++
    }
    if ($end -lt 0) { $end = $j }
    # 成员名（方法名）
    $head = $lines[$start]
    $mname = ([regex]::Match($head, '([A-Za-z_]\w*)\s*\(')).Groups[1].Value
    $members.Add([pscustomobject]@{
        Start = $start; End = $end; Name = $mname
        Text  = (($lines[$start..$end]) -join "`r`n")
    }) | Out-Null
    $i = $end + 1
}

Write-Output "group   = $GroupDir / $GroupFile"
Write-Output "class   = $className"
Write-Output "members = $($members.Count)"

# ── 3) 分派表：mode -> 其 Probe 方法 ──
# ⚠⚠ 判据必须取**权威来源** `Program.cs` 的分派行，**不能**在本文件里正则匹配 `Run\w+`
#   —— 实测踩过：`RunCapture` 这类**被调用的 helper** 也匹配 `Run*`，于是 `diagnostics`
#   凭空多出 `Capture`/`Cap`/`Task`/`Cli` 四个**伪 mode**（本仓 §6 第 87 条同族：
#   判据的"扫描面"比它的"措辞"大）。权威形态（`Program.cs`）：
#     if (mode == "" || mode == "<m>") <Cls>.Run<X>(args);
$progPath = Join-Path $root (Join-Path $GroupDir 'Program.cs')
$modeMap = [ordered]@{}
if (Test-Path -LiteralPath $progPath) {
    $prog = [System.IO.File]::ReadAllText($progPath)
    $rxDisp = 'mode\s*==\s*""\s*\|\|\s*mode\s*==\s*"([a-z0-9\-]+)"\s*\)\s*[A-Za-z_]\w*\.(Run\w+)\s*\('
    foreach ($mm in [regex]::Matches($prog, $rxDisp)) { $modeMap[$mm.Groups[1].Value] = $mm.Groups[2].Value }
}
if ($modeMap.Count -eq 0) {
    # 退化：只认**类体内**带 `=>` 的 Run* 转发（不认被调用的 helper）
    foreach ($m in $members) {
        if ($m.Name -like 'Run*' -and $m.Text -match '=>') {
            $target = ([regex]::Match($m.Text, '=>\s*([A-Za-z_]\w*)\s*\(')).Groups[1].Value
            if ($target) { $modeMap[$m.Name.Substring(3)] = $target }
        }
    }
}
Write-Output ''
Write-Output "dispatch source = $(if (Test-Path -LiteralPath $progPath) { 'Program.cs (authoritative)' } else { 'fallback' })"
Write-Output 'mode -> probe method:'
foreach ($k in $modeMap.Keys) { Write-Output ("  {0,-20} -> {1}" -f $k, $modeMap[$k]) }

# Probe -> 它独占的 helper（被>=1 个 Probe 调用、且不被其他 Probe 调用的 private static）
$probes = @($members | Where-Object { $_.Name -like 'Probe*' })
$helpers = @($members | Where-Object { $_.Name -notlike 'Probe*' -and $_.Name -notlike 'Run*' })
Write-Output ''
Write-Output "probes = $($probes.Count) ; candidate helpers = $($helpers.Count)"

$assign = @{}   # helper name -> probe name（独占时）
foreach ($h in $helpers) {
    $callers = @($probes | Where-Object { $_.Text -match ('\b' + [regex]::Escape($h.Name) + '\s*\(') })
    if ($callers.Count -eq 1) { $assign[$h.Name] = $callers[0].Name }
    Write-Output ("  helper {0,-24} callers={1} {2}" -f $h.Name, $callers.Count,
                  $(if ($callers.Count -eq 1) { '=> ' + $callers[0].Name } else { '(shared, stays)' }))
}

if ($WhatIf -or -not $Emit) {
    Write-Output ''
    Write-Output '(dry run — pass -Emit to write files)'
    exit 0
}

# ── 4) 落盘 ──
$modesDir = Join-Path $root (Join-Path $GroupDir 'Modes')
New-Item -ItemType Directory -Path $modesDir -Force | Out-Null

$written = @()
foreach ($p in $probes) {
    $mode = ($modeMap.Keys | Where-Object { $modeMap[$_] -eq $p.Name } | Select-Object -First 1)
    if (-not $mode) { $mode = $p.Name -replace '^Probe', '' }
    $fname = ($mode.ToLowerInvariant()) + '.cs'
    $own = @($helpers | Where-Object { $assign[$_.Name] -eq $p.Name })
    $body = New-Object System.Collections.ArrayList
    foreach ($o in $own) { [void]$body.Add($o.Text) }
    [void]$body.Add($p.Text)
    # ⚠⚠ 本注释**必须纯 ASCII**：`_probe-cjk-hardcode-scan.ps1` 的「UI 面」桶**不得增加**
    #   （维护者 2026-09-18 裁定，已抬升 6 次基线 ⇒ 生产线新增中文注释会当场红）。
    $content = $header + "`r`n" + $nsLine + "`r`n{`r`n" +
               "    // Generated by _gen-mode-split.ps1: the mode body was copied VERBATIM out of`r`n" +
               "    // $GroupFile (logic / strings / indentation unchanged).`r`n" +
               "    // Motive: split one subsystem further into one-file-per-mode so that a change`r`n" +
               "    // touches only that mode, not the whole monolith.`r`n" +
               "    // The class became ``partial`` so all call sites (Program.cs -> $className.Run*())`r`n" +
               "    // stay byte-identical. Assertion count MUST NOT change: acceptance = per-mode`r`n" +
               "    // readings identical before vs after the split.`r`n" +
               "    // NOTE: ASCII-only on purpose (see _probe-cjk-hardcode-scan.ps1 UI bucket).`r`n" +
               "    internal static partial class $className`r`n" +
               "    {`r`n" +
               (($body | ForEach-Object { $_ }) -join "`r`n`r`n") + "`r`n" +
               "    }`r`n" +
               "}`r`n"
    $outPath = Join-Path $modesDir $fname
    [System.IO.File]::WriteAllText($outPath, $content)
    $written += [pscustomobject]@{ File = $fname; Mode = $mode; Probe = $p.Name; OwnHelpers = $own.Count; Lines = ($content -split "`r?`n").Count }
}
Write-Output ''
Write-Output "--- written $($written.Count) file(s) to $modesDir ---"
$written | ForEach-Object { Write-Output ("  {0,-22} probe={1,-24} helpers={2,-3} lines={3}" -f $_.File, $_.Probe, $_.OwnHelpers, $_.Lines) }
