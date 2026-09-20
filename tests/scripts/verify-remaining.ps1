# 验证剩余问题的修复状态 (三类关键用例)
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = "$repoRoot\publish\build\FFmpegPictureUI-dev-x64-full\FfmpegGui.exe"
$ffprobe = (Get-ChildItem "$repoRoot\publish\PLAN\ffmpeg-full*\ffprobe.exe" | Select-Object -First 1).FullName
$base = "$repoRoot\tests\output\results\matrix\safe_sources"
$log = "C:\temp\verify_remaining.log"
Remove-Item $log -Force -ErrorAction SilentlyContinue

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$env:TEMP/_exec.out"; $e = "$env:TEMP/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

function Log($msg) { Write-Host $msg; Add-Content -Path $log -Value $msg -Encoding UTF8 }
function TestCase($id, $src, $fmt, $enc, $cs, $bd) {
    $outDir = "C:\temp\verify_remaining\$id"
    if (Test-Path $outDir) { Remove-Item "$outDir\*" -Force -ErrorAction SilentlyContinue } else { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    $argList = @("--headless","-i",$src,"-o",$outDir,"-f",$fmt,"-e",$enc,"-j","1","--threads","4")
    if ($cs) { $argList += @("--color-space",$cs) }
    if ($bd) { $argList += @("--bit-depth",$bd) }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $r = (Exec $exe ("-i `"$src`" -o `"$outDir`" -f $fmt -e $enc -j 1 --threads 4" + $(if ($cs) { " --color-space `"$cs`"" }) + $(if ($bd) { " --bit-depth $bd" }))) | Out-String
    $sw.Stop()
    $ok = $r -match "完成 1 项, 失败 0 项"
    $probe = ""
    if ($ok) {
        $outFile = Get-ChildItem $outDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin @("stdout.log","stderr.log") } | Select-Object -First 1
        if ($outFile) { try { $probe = ((Exec $ffprobe "-v error -select_streams v:0 -show_entries stream=pix_fmt,color_primaries,color_transfer -of csv=p=0 `"$($outFile.FullName)`"") | Out-String).Trim() } catch { $probe = "err" } }
    }
    $status = if ($ok) { "PASS" } else { "FAIL" }
    $line = "${id}: $status ($([math]::Round($sw.Elapsed.TotalSeconds,1))s)"
    if ($ok) { $line += " $probe" } else { $line += " ERR: $($r -split "`n" | Where-Object { $_ -match "失败|失败" } | Select-Object -First 1)" }
    Log $line
}

# 类别1: DNG HDR -> 各格式 (原 L4-403~407)
$dng = "D:\TEST2\RGBS6695-HDR\RGBS6695-HDR.dng"
Log "=== DNG HDR -> 各格式 ==="
TestCase "dng_jpg" $dng "jpg" "Ffmpeg" "sRGB" ""
TestCase "dng_png" $dng "png" "Ffmpeg" "sRGB" ""
TestCase "dng_webp" $dng "webp" "Ffmpeg" "sRGB" ""
TestCase "dng_avif" $dng "avif" "Ffmpeg" "sRGB" ""
TestCase "dng_tiff" $dng "tiff" "Ffmpeg" "sRGB" ""
TestCase "dng_jxl" $dng "jxl" "Cjxl" "sRGB" ""

# 类别2: HDR JXL -> WebP (原 L3-315)
Log ""
Log "=== HDR JXL -> WebP ==="
TestCase "hdrjxl_webp" "$base\hdr_jxl_s.jxl" "webp" "Ffmpeg" "sRGB" "8"

# 类别3: HDR JXL -> PNG (原 L3-311 也检查)
TestCase "hdrjxl_png" "$base\hdr_jxl_s.jxl" "png" "Ffmpeg" "sRGB" "16"

Log ""
Log "=== 完成 ==="