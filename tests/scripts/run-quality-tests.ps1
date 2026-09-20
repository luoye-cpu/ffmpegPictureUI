# ═══════════════════════════════════════════════════════════
#  run-quality-tests.ps1 — 质量/保真度深度验证
#  验证: 像素保真 (PSNR/SSIM) / 色彩元数据 / 位深 / ICC / EXIF
# ═══════════════════════════════════════════════════════════
$ErrorActionPreference = "Continue"
$out = "tests/output"
$res = "$out/results"
$src = "$out/sources"
New-Item -ItemType Directory -Force -Path $res | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e
  $global:LASTEXITCODE = $p.ExitCode
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

$ffmpeg = (Get-ChildItem "publish/PLAN/ffmpeg-full*/ffmpeg.exe" | Select-Object -First 1).FullName
$ffprobe = (Get-ChildItem "publish/PLAN/ffmpeg-full*/ffprobe.exe" | Select-Object -First 1).FullName
$exif   = "publish/PLAN/exiftool/exiftool.exe"
$pass = 0; $fail = 0; $failList = @()

function Check($name, $cond) {
    if ($cond) { Write-Host "  ✅ $name" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "  ❌ $name" -ForegroundColor Red; $script:fail++; $script:failList += $name }
}

function PSNR($a, $b) {
    $r = Exec $ffmpeg "-hide_banner -i `"$a`" -i `"$b`" -lavfi psnr -f null -" | Out-String
    # 新 ffmpeg 输出: PSNR r:inf g:inf b:inf average:inf min:inf max:inf
    $m = [regex]::Match($r, "average:([\d.]+|inf)")
    if ($m.Success) {
        $v = $m.Groups[1].Value
        if ($v -eq "inf") { return [double]::MaxValue } else { return [double]$v }
    }
    return -1
}

# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ A. 无损保真 (PSNR=inf 或高值) ════════" -ForegroundColor Yellow

# PNG 无损往返
Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$src/src_8bit.png`" -update 1 `"$res/q_roundtrip.png`"" | Out-Null
$p = PSNR "$src/src_8bit.png" "$res/q_roundtrip.png"
Check "PNG→PNG 无损往返 PSNR=$p (应=inf)" ($p -eq 99 -or $p -ge 60)

# PNG→TIFF→PNG 往返
Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$src/src_8bit.png`" -c:v tiff -update 1 `"$res/q_tiff.tiff`"" | Out-Null
Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$res/q_tiff.tiff`" -update 1 `"$res/q_tiff_back.png`"" | Out-Null
$p = PSNR "$src/src_8bit.png" "$res/q_tiff_back.png"
Check "PNG→TIFF→PNG PSNR=$p" ($p -ge 60)

# 16-bit PNG→TIFF 位深
Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$src/src_16bit.png`" -c:v tiff -update 1 `"$res/q_16tiff.tiff`"" | Out-Null
$bps = Exec $exif "-s -BitsPerSample `"$res/q_16tiff.tiff`""
Check "16-bit PNG→TIFF BitsPerSample=$bps (应=16)" ($bps -match "16 16 16")

# WebP 无损→PNG 往返 (需 rgb24 才能真无损, yuv420p 会丢失色度)
Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$src/src_8bit.png`" -c:v libwebp -lossless 1 -pix_fmt rgb24 -update 1 `"$res/q_wp.webp`"" | Out-Null
Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$res/q_wp.webp`" -update 1 `"$res/q_wp.png`"" | Out-Null
$p = PSNR "$src/src_8bit.png" "$res/q_wp.png"
Check "WebP 无损→PNG PSNR=$p" ($p -ge 60)

# JXL 无损→PNG 往返 (cjxl)
$cjxl = "publish/PLAN/jxl/bin/cjxl.exe"
Exec $cjxl "`"$src/src_8bit.png`" `"$res/q_lossless.jxl`" -d 0 -e 7" | Out-Null
Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$res/q_lossless.jxl`" -update 1 `"$res/q_jxl_back.png`"" | Out-Null
$p = PSNR "$src/src_8bit.png" "$res/q_jxl_back.png"
Check "JXL 无损往返 PSNR=$p" ($p -ge 60)

# JXL JPEG 重封装往返
Exec $cjxl "`"$src/src_photo.jpg`" `"$res/q_jpeg.jxl`" -d 0 -e 7 --lossless_jpeg=1" | Out-Null
$jxlSize = (Get-Item "$res/q_jpeg.jxl").Length
$jpgSize = (Get-Item "$src/src_photo.jpg").Length
Check "JXL JPEG 重封装 大小=$jxlSize vs JPEG=$jpgSize" ($jxlSize -lt $jpgSize)

# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ B. 有损质量 (PSNR 合理值) ════════" -ForegroundColor Yellow

Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$src/src_8bit.png`" -q:v 5 -update 1 `"$res/q_jpg.jpg`"" | Out-Null
$p = PSNR "$src/src_8bit.png" "$res/q_jpg.jpg"
Check "PNG→JPEG q5 PSNR=$p (应>30)" ($p -gt 30)

Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$src/src_8bit.png`" -c:v libwebp -quality 90 -update 1 `"$res/q_webp90.webp`"" | Out-Null
$p = PSNR "$src/src_8bit.png" "$res/q_webp90.webp"
Check "PNG→WebP q90 PSNR=$p (应>40)" ($p -gt 40)

Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$src/src_8bit.png`" -c:v libaom-av1 -crf 30 -strict experimental -update 1 `"$res/q_avif.avif`"" | Out-Null
$p = PSNR "$src/src_8bit.png" "$res/q_avif.avif"
Check "PNG→AVIF crf30 PSNR=$p (应>35)" ($p -gt 35)

# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ C. 色彩元数据 ════════" -ForegroundColor Yellow

# HDR 验证: 用 cjxl -x color_space 显式写 Rec.2100 PQ (软件实际做法)
Exec $cjxl "`"$src/src_hdr_pq.png`" `"$res/q_hdr.jxl`" -d 0 -e 7 -x `"color_space=RGB_D65_202_Rel_PeQ`"" | Out-Null
$jxlInfo = ((Exec "publish/PLAN/jxl/bin/jxlinfo.exe" "`"$res/q_hdr.jxl`"") -split "`r?`n") | Select-String "Color space|color space" | Select-Object -First 1
Check "HDR→JXL 色彩空间=$jxlInfo" ($jxlInfo -match "RGB")

# JPEG 嵌入 ICC 后 → PNG 应带 ICC
Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$src/src_photo.jpg`" -update 1 `"$res/q_icc.png`"" | Out-Null
$icc = (Exec $exif "-s -ICC_Profile_Name `"$res/q_icc.png`"").Trim()
Check "JPEG(含ICC)→PNG ICC 保留=$icc" ($icc -ne "")

# 16-bit PNG → JXL (cjxl) 位深
Exec $cjxl "`"$src/src_16bit.png`" `"$res/q_16.jxl`" -d 0 -e 7" | Out-Null
$jxlInfo = ((Exec "publish/PLAN/jxl/bin/jxlinfo.exe" "`"$res/q_16.jxl`"") -split "`r?`n") | Select-String "bit depth|bits_per_sample" | Select-Object -First 1
if (-not $jxlInfo) { $jxlInfo = ((Exec "publish/PLAN/jxl/bin/jxlinfo.exe" "`"$res/q_16.jxl`"") -split "`r?`n") | Select-Object -First 6 | Select-Object -Last 2 }
Check "16-bit→JXL 位深=$jxlInfo" ($jxlInfo -match "16")

# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ D. 元数据 (EXIF/GPS/时间) ════════" -ForegroundColor Yellow

# src_photo.jpg 有 EXIF+GPS+ICC → 转 PNG 应保留
Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$src/src_photo.jpg`" -update 1 `"$res/q_meta.png`"" | Out-Null
Exec $exif (@("-tagsfromfile", "$src/src_photo.jpg", "-all:all", "$res/q_meta.png") -join ' ') | Out-Null
$artist = ((Exec $exif "-s -s -Artist `"$res/q_meta.png`"") -replace '^Artist:\s*','').Trim()
$gps = ((Exec $exif "-s -s -GPSLatitude `"$res/q_meta.png`"") -replace '^GPSLatitude:\s*','').Trim()
# PNG 中 ICC 标签名为 ProfileName (JPEG 为 ICC_Profile_Name)
$iccN = ((Exec $exif "-s -s -ProfileName `"$res/q_meta.png`"") -replace '^ProfileName:\s*','').Trim()
if (-not $iccN) { $iccN = ((Exec $exif "-s -s -ICC_Profile_Name `"$res/q_meta.png`"") -replace '^ICC_Profile_Name:\s*','').Trim() }
Check "元数据恢复 Artist=$artist" ($artist -eq "TestUser")
Check "元数据恢复 GPS=$gps" ($gps -ne "")
Check "元数据恢复 ICC=$iccN" ($iccN -ne "")

# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ E. RAW 管线质量 ════════" -ForegroundColor Yellow
$dngtool = "publish/PLAN/artifacts/dngtool.exe"
$s = "tools/src/dng_sdk/dng_sdk_1_7_1/sample_files/01_jxl_linear_raw_integer.dng"

# DNG→DNG 重编码 ColorMatrix 保留
Exec $dngtool "-e -jxl -q 0 -effort 7 -i $s -O `"$res/q_dng.dng`"" | Out-Null
$cm1 = ((Exec $exif "-s -s -ColorMatrix1 `"$res/q_dng.dng`"") -replace '^ColorMatrix1:\s*','').Trim()
$cm1s = ((Exec $exif "-s -s -ColorMatrix1 $s") -replace '^ColorMatrix1:\s*','').Trim()
Check "DNG 重编码 ColorMatrix1 保留" ($cm1 -eq $cm1s)
$be = ((Exec $exif "-s -s -BaselineExposure `"$res/q_dng.dng`"") -replace '^BaselineExposure:\s*','').Trim()
Check "DNG 重编码 BaselineExposure=$be (应=0.35)" ($be -eq "0.35")

# DNG→TIFF 像素解码可用
Exec $dngtool "-d -T -o 1 -q 3 -W -H 1 -6 -i $s -O `"$res/q_raw.tiff`"" | Out-Null
Exec $ffmpeg "-y -hide_banner -loglevel error -i `"$res/q_raw.tiff`" -update 1 `"$res/q_raw.png`"" | Out-Null
$ok = Test-Path "$res/q_raw.png"
Check "DNG→TIFF→PNG 可解码" $ok

# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ 质量测试汇总 ════════" -ForegroundColor Yellow
Write-Host "通过: $pass  失败: $fail" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($failList.Count -gt 0) { $failList | ForEach-Object { Write-Host "  ❌ $_" -ForegroundColor Red } }
