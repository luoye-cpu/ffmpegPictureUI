# 项目目录结构规范化脚本
$root = "C:\PLAN\ffmpegPictureUI"
Set-Location $root
$report = @()

# ── 1. 清理根目录散落的临时目录 ──
$strayDirs = @("temp_e2e", "temp_headless_test", "temp_headless_test2", "output")
foreach ($d in $strayDirs) {
    if (Test-Path $d) {
        # 备份有价值文件到 tests/output/_migrated/
        $dest = "tests\output\_migrated\$d"
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item "$d\*" "$dest\" -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
        $report += "清理根目录散落目录: $d → tests/output/_migrated/$d"
    }
}

# ── 2. tests 顶层散落的测试脚本移入 tests/scripts ──
$strayTests = Get-ChildItem "tests" -File | Where-Object { $_.Name -like "*.ps1" }
foreach ($f in $strayTests) {
    Move-Item $f.FullName "tests\scripts\$($f.Name)" -Force
    $report += "移动测试脚本: tests/$($f.Name) → tests/scripts/"
}

# ── 3. tests 顶层散落的输出文件移入 tests/output ──
$strayOut = Get-ChildItem "tests" -File | Where-Object { $_.Name -like "*.txt" -or $_.Name -like "*.jsonl" -or $_.Name -like "*.log" }
foreach ($f in $strayOut) {
    Move-Item $f.FullName "tests\output\$($f.Name)" -Force
    $report += "移动测试输出: tests/$($f.Name) → tests/output/"
}

# ── 4. 确认 GainMapTestHost 的 PLAN 工具副本不误跟踪 ──
$report += ""
$report += "=== 整理后根目录 ==="
Get-ChildItem -Directory | Select-Object -ExpandProperty Name | ForEach-Object { $report += "  [D] $_" }
Get-ChildItem -File | Select-Object -ExpandProperty Name | ForEach-Object { $report += "  [F] $_" }

Set-Content "tests\output\_migrated\cleanup_report.txt" $report -Encoding UTF8
Write-Host $report