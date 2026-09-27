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
  "verify-decision-delivery.ps1"
)
# ⚠ 2026-09-27：原列表末位 `verify-remaining.ps1` 已随三条腐坏诊断脚本一起
#   `git mv` 到 `tests/scripts/_archived_diag/`（它硬写 `publish\build\FFmpegPictureUI-dev-x64-full\`，
#   而 `pack.ps1` 的模板恒带 `v$Version` ⇒ 那个目录任何版本都不会产生；且它自身**没有 `PASS=` 汇总行**，
#   本聚合器抓不到它的空 ⇒ 正是"看起来跑了、其实什么都没判"的形状）。
$totals = @{}
foreach ($s in $scripts) {
  $p = Join-Path "tests/scripts" $s
  # ⚠ 清单里的脚本**不存在**不是"跳过"而是**故障**：静默 SKIP 会让聚合器少跑一条而汇总照绿
  #   （本仓 §6 反复登记过的同一形状）⇒ 计入失败明细并在总结里点名。
  if (-not (Test-Path $p)) {
    Write-Host "  FAIL $s 清单里列了但不存在（不允许当 SKIP）" -ForegroundColor Red
    $totals[$s] = "FAIL missing-script"
    continue
  }
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
