# _gen-mode-split-reduce.ps1 —— 细分第二步：把原巨型文件**缩减**为「共享底座」的 partial 另一半。
#
# 与 `_gen-mode-split.ps1` 配对使用：
#   ① `_gen-mode-split.ps1 -Emit`  把 `Probe<Mode>` 及其独占 helper **逐字搬出**到 `Modes/<mode>.cs`；
#   ② 本脚本把原文件里的**这些成员删掉**、类改成 `partial`，只保留：
#        · 文件头（using / 类级注释）
#        · 共享 helper（被 >=2 个 Probe 调用，或非方法成员）
#        · `Run<Mode>` 转发入口（调用点零改动的关键）
#
# ⚠ 纪律：删除是**按成员边界整块删**，不做行级正则替换 ⇒ 不会误伤相邻注释或半个方法。
# ⚠ 验收：拆分前后逐 mode 读数必须**逐字一致**（由调用方在拆完后跑对拍）。

param(
    [Parameter(Mandatory = $true)][string]$GroupDir,
    [Parameter(Mandatory = $true)][string]$GroupFile,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$file = Join-Path $root (Join-Path $GroupDir $GroupFile)
$text  = [System.IO.File]::ReadAllText($file)
$lines = $text -split "`r?`n"

# 复用与切片器同一套定位逻辑
$nsIdx = -1; $clsIdx = -1; $openIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($nsIdx -lt 0 -and $lines[$i] -match '^\s*namespace\s+') { $nsIdx = $i }
    if ($clsIdx -lt 0 -and $lines[$i] -match '^\s*(internal|public)\s+static\s+class\s+') { $clsIdx = $i }
    if ($clsIdx -ge 0 -and $openIdx -lt 0 -and $lines[$i] -match '\{\s*$') { $openIdx = $i; break }
}
$closeIdx = -1
for ($i = $lines.Count - 1; $i -gt $openIdx; $i--) { if ($lines[$i] -match '^\s*\}\s*$') { $closeIdx = $i; break } }

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
        $code = [regex]::Replace($code, '"(?:\\.|[^"\\])*"', '""')
        $depth += ([regex]::Matches($code, '\{')).Count
        $depth -= ([regex]::Matches($code, '\}')).Count
        if ($depth -le 0 -and $j -gt $start -and $cur -match '\}\s*$') { $end = $j; break }
        if ($depth -eq 0 -and $cur -match '=>.*;\s*$') { $end = $j; break }
        $j++
    }
    if ($end -lt 0) { $end = $j }
    $head = $lines[$start]
    $mname = ([regex]::Match($head, '([A-Za-z_]\w*)\s*\(')).Groups[1].Value
    $members.Add([pscustomobject]@{ Start=$start; End=$end; Name=$mname;
        Text=(($lines[$start..$end]) -join "`r`n") }) | Out-Null
    $i = $end + 1
}

$probes  = @($members | Where-Object { $_.Name -like 'Probe*' })
$helpers = @($members | Where-Object { $_.Name -notlike 'Probe*' -and $_.Name -notlike 'Run*' })

# 要删的成员 = 所有 Probe* + 「只被一个 Probe 调用」的 helper（它们已随该 mode 搬走）
$dropNames = New-Object System.Collections.Generic.HashSet[string]
foreach ($p in $probes) { [void]$dropNames.Add($p.Name) }
$movedHelpers = @()
foreach ($h in $helpers) {
    $callers = @($probes | Where-Object { $_.Text -match ('\b' + [regex]::Escape($h.Name) + '\s*\(') })
    if ($callers.Count -eq 1) { [void]$dropNames.Add($h.Name); $movedHelpers += $h.Name }
}

Write-Output "class   = $(([regex]::Match($lines[$clsIdx],'class\s+(\w+)')).Groups[1].Value)"
Write-Output "members = $($members.Count)  probes=$($probes.Count)  helpers=$($helpers.Count)"
Write-Output "dropping (moved to Modes/*.cs):"
foreach ($n in ($dropNames | Sort-Object)) { Write-Output "  - $n" }
Write-Output "keeping (shared base):"
foreach ($m in $members) { if (-not $dropNames.Contains($m.Name)) { Write-Output "  + $($m.Name)" } }

if ($WhatIf) { Write-Output ''; Write-Output '(dry run)'; exit 0 }

# 重建：类体 = 保留成员（按原顺序）+ 类外内容（namespace 闭合、文件尾）
$keep = @($members | Where-Object { -not $dropNames.Contains($_.Name) } | Sort-Object Start)
$newBody = New-Object System.Collections.ArrayList
foreach ($k in $keep) { [void]$newBody.Add($k.Text) }

$clsLine = $lines[$clsIdx]
$partialClsLine = $clsLine -replace '(\bstatic\s+class\b)', 'static partial class'
$head = ($lines[0..($clsIdx - 1)]) -join "`r`n"

# NOTE: ASCII-only on purpose (see _probe-cjk-hardcode-scan.ps1 UI bucket, must not grow).
$out = $head + "`r`n" +
       "    // After the two-step mode split (_gen-mode-split.ps1 + _gen-mode-split-reduce.ps1),`r`n" +
       "    // this file keeps ONLY the shared base: helpers used by >=2 modes plus the Run*`r`n" +
       "    // forwarding entry points. Each Probe* body (and its exclusive helpers) was copied`r`n" +
       "    // VERBATIM into Modes/<mode>.cs; those files are the other half of the same class`r`n" +
       "    // (partial class), so every call site is unchanged.`r`n" +
       "    // Assertion count is unchanged: acceptance = per-mode readings identical.`r`n" +
       $partialClsLine + "`r`n    {`r`n" +
       (($newBody) -join "`r`n`r`n") + "`r`n    }`r`n" +
       "}`r`n"

[System.IO.File]::WriteAllText($file, $out)
$newLines = ($out -split "`r?`n").Count
Write-Output ''
Write-Output "rewrote $file : $($lines.Count) -> $newLines lines"
Write-Output "moved helpers: $($movedHelpers -join ', ')"
