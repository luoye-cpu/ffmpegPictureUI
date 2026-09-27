# 最终验证脚本: 验证 3 类修复的关键用例
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = "$repoRoot\publish\build\FFmpegPictureUI-dev-x64-full\FfmpegGui.exe"
$ffprobe = (Get-ChildItem "$repoRoot\publish\PLAN\ffmpeg-full*\ffprobe.exe" | Select-Object -First 1).FullName
$base = "$repoRoot\tests\output\results\matrix\safe_sources"
$logFile = "C:\temp\final_verification.log"
Remove-Item $logFile -Force -ErrorAction SilentlyContinue

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$env:TEMP/_exec.out"; $e = "$env:TEMP/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

function Log($msg) {
    Write-Host $msg
    Add-Content -Path $logFile -Value $msg -Encoding UTF8
}

function TestCase($id, $src, $fmt, $enc, $cs, $bd) {
    $outDir = "C:\temp\final_verify\$id"
    if (Test-Path $outDir) { Remove-Item "$outDir\*" -Force -ErrorAction SilentlyContinue } else { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    $argStr = "--headless -i `"$src`" -o `"$outDir`" -f $fmt -e Ffmpeg -j 1 --threads 4"
    if ($cs) { $argStr += " --color-space `"$cs`"" }
    if ($bd) { $argStr += " --bit-depth $bd" }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Exec $exe $argStr | Out-String
    $sw.Stop()
    $ok = $r -match "完成 1 项, 失败 0 项"
    $probe = ""
    if ($ok) {
        $outFile = Get-ChildItem $outDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin @("stdout.log","stderr.log") } | Select-Object -First 1
        if ($outFile) { try { $probe = (Exec $ffprobe "-v error -select_streams v:0 -show_entries stream=pix_fmt,color_primaries,color_transfer -of csv=p=0 `"$($outFile.FullName)`"" | Out-String).Trim() } catch { $probe = "err" } }
    }
    $status = if ($ok) { "PASS" } else { "FAIL" }
    $line = "${id}: $status ($([math]::Round($sw.Elapsed.TotalSeconds, 1))s)"
    if ($ok) { $line += " $probe" } else { $errMsg = ($r -split "`n" | Where-Object { $_ -match "失败|失败" } | Select-Object -First 1); $line += " ERR: $errMsg" }
    Log $line
    return $ok
}

Log "=== 最终验证开始 $(Get-Date) ==="

# 三类修复验证
$failures = 0
$total = 0

# 1. HDR -> TIFF HDR 目标 (capTrc=SDR 但需完整 tonemap)
Log ""
Log "=== 类别1: HDR AVIF -> TIFF (HDR 目标) ==="
$src = "$repoRoot\tests\output\results\matrix\safe_sources\hdr_avif_s.avif"
if (TestCase "L2-233" $("$repoRoot\tests\output\results\matrix\safe_sources\hdr_avif_s.avif") "tiff" "Ffmpeg" "BT.2020 PQ" "16") { } else { $failures++ }
$total++

# 2. HDR JXL -> WebP (HDR JXL 管道丢失)
Log ""
Log "=== 类别2: HDR JXL -> WebP (HDR 管道) ==="
if (TestCase "L3-315" "$repoRoot\tests\output\results\matrix\safe_sources\hdr_jxl_s.jxl" "webp" "Ffmpeg" "sRGB" "8") { } else { $failures++ }
$total++

# 3. DNG HDR -> 各格式 (RAW HDR -> 各格式)
Log ""
Log "=== 类别3: DNG HDR -> obraz格式 ==="
$dngSrc = "D:\TEST2\RGBS6695-HDR\RGBS6695-HDR.dng"
if (TestCase "L4-404" "D:\TEST2\RGBS6695-HDR\RGBS6695-HDR.dng" "png" "Ffmpeg" "sRGB" "") { } else { $failures++ }
$total++

# 4. 其他原本失败的关键用例
Log ""
Log "=== 类别4: 回归验证 ==="
if (TestCase "avif_p3_8" "$repoRoot\tests\output\results\matrix\safe_sources\png_sdr_s.png" "avif" "Ffmpeg" "Display P3" "8") { } else { $failures++ }
$total++
if (TestCase "webp_p3" "$repoRoot\tests\output\results\matrix\safe_sources\png_sdr_s.png" "webp" "Ffmpeg" "Display P3" "8") { } else { $failures++ }
$total++
if (TestCase "webp_hdr_srgb" "$repoRoot\tests\output\results\matrix\safe_sources\hdr_avif_s.avif" "webp" "Ffmpeg" "sRGB" "8") { } else { $failures++ }
$total++
if (TestCase "16bit_avif" "$repoRoot\tests\output\results\matrix\safe_sources\tiff16_s.png" "avif" "Ffmpeg" "" "16") { } else { $failures++ }
$total++

Log ""
Log "═══════════════════════════════════════════════════════════"
Log "最终验证完成: PASS=$total FAIL=$failures TOTAL=$((total))"
if ($failures -eq 0) { Log "✅ 全部通过!" "Green" } else { Log "❌ 剩余 $failures 个失败" "Red" }
Log "日志文件: $logFile"
if ($failures -eq 0) { exit 0 } else { exit 1 }