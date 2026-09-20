# 全量回归汇总（色彩管线改动后必跑）
# 逐个调用既有 verify-*.ps1，只回显各自的 PASS/FAIL 汇总行与失败明细行
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$scripts = @(
  "verify-color-strategy.ps1",
  "verify-format-regression.ps1",
  "verify-widegamut-regression.ps1",
  "verify-webp-hdr-fix.ps1",
  "verify-tiff-icc.ps1",
  "verify-gainmap-managed.ps1",
  "verify-prophoto-faithful.ps1",
  "verify-decision-delivery.ps1",
  "verify-remaining.ps1"
)
$totals = @{}
foreach ($s in $scripts) {
  $p = Join-Path "tests/scripts" $s
  if (-not (Test-Path $p)) { Write-Host "SKIP $s (不存在)" -ForegroundColor Yellow; continue }
  Write-Host "`n########## $s ##########" -ForegroundColor Cyan
  $out = pwsh -NoProfile -File $p 2>&1 | Out-String
  # 汇总行
  $sum = ($out -split "`n" | Select-String -Pattern "=====|PASS=|RESULT|FAIL" | ForEach-Object { $_.Line.Trim() })
  $sum | ForEach-Object { Write-Host "  $_" }
  $totals[$s] = $out
}
Write-Host "`n=========== 总结 ===========" -ForegroundColor Cyan
foreach ($k in $totals.Keys) {
  $t = $totals[$k]
  $f = ([regex]::Matches($t, "(?m)^\s*FAIL|失败 \d+|error CS")).Count
  $line = ($t -split "`n" | Select-String -Pattern "=====.*PASS=" | Select-Object -Last 1)
  Write-Host ("{0,-34} {1}  {2}" -f $k, $(if ($line) { $line.Line.Trim() } else { "(无汇总行)" }), $(if ($f -gt 0) { "<< 有 FAIL 明细" } else { "" }))
}
