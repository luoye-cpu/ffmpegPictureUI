# 归档一次性诊断脚本到 _archived 子目录
$root = "C:\PLAN\ffmpegPictureUI"
Set-Location $root
$scriptsDir = "tests\scripts"
$archivedDir = "$scriptsDir\_archived_diag"
New-Item -ItemType Directory -Force -Path $archivedDir | Out-Null

# 一次性诊断/临时调试脚本（无长期价值，归档保留）
$diagnostic = @(
    "catch-dng.ps1", "debug-exec-jxlwebp.ps1", "diag-cat1.ps1", "diag-catch-dng.ps1",
    "diag-deep.ps1", "diag-detail.ps1", "diag-dng-dry.ps1", "diag-dng-manual.ps1",
    "diag-dng.ps1", "diag-failures.ps1", "diag-final.ps1", "diag-hdrjxl-webp.ps1",
    "diag-hdrjxl-webp3.ps1", "diag-jxl-webp-final.ps1", "diag-jxl-webp-final2.ps1",
    "diag-jxl-webp-v2.ps1", "diag-jxl-webp.ps1", "diag-jxl-webp2.ps1",
    "diag-tiff-dry.ps1", "diag3.ps1", "diag4.ps1", "exec-avif-p3.ps1",
    "sim-final.ps1", "sim-jxl-webp.ps1", "test-jxl-pipe-ffmpeg.ps1",
    "test-jxl-pipe.ps1", "verify-fix.ps1", "verify-jxl-webp-exec.ps1",
    "verify-key-fixes.ps1", "run_maxdim_tests.ps1", "run_maxdim_tests2.ps1",
    "run_maxdim_tests3.ps1"
)

$moved = 0
foreach ($f in $diagnostic) {
    $src = Join-Path $scriptsDir $f
    if (Test-Path $src) {
        Move-Item $src "$archivedDir\$f" -Force
        $moved++
    }
}
Write-Host "已归档 $moved 个诊断脚本到 tests/scripts/_archived_diag/"

# 保留的正式测试脚本
Write-Host ""
Write-Host "=== tests/scripts 保留的正式脚本 ==="
Get-ChildItem $scriptsDir -File | Where-Object { $_.DirectoryName -eq (Resolve-Path $scriptsDir) } | Select-Object -ExpandProperty Name