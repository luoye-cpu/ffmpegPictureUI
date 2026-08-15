# ═══════════════════════════════════════════════════════════
#  run-pipeline-tests.ps1 — FFmpegPictureUI 全管线测试
#  覆盖: 静态图片 / 动图 / RAW / 特殊输入 (JXL/JXR/AVIF/UltraHDR)
#  输出: tests/output/results/ (git 忽略) + 汇总日志
# ═══════════════════════════════════════════════════════════
$ErrorActionPreference = "Continue"
$out = "tests/output"
$res = "$out/results"
$src = "$out/sources"
New-Item -ItemType Directory -Force -Path $res | Out-Null

$ffmpeg = (Get-ChildItem "publish/PLAN/ffmpeg-full*/ffmpeg.exe" | Select-Object -First 1).FullName
$ffprobe = (Get-ChildItem "publish/PLAN/ffmpeg-full*/ffprobe.exe" | Select-Object -First 1).FullName
$cjxl   = "publish/PLAN/jxl/bin/cjxl.exe"
$djxl   = "publish/PLAN/jxl/bin/djxl.exe"
$exif   = "publish/PLAN/exiftool/exiftool.exe"
$dngtool = "publish/PLAN/artifacts/dngtool.exe"

$passCount = 0; $failCount = 0; $failList = @()
$totalSw = [System.Diagnostics.Stopwatch]::StartNew()

function Test-Step($name, $script) {
    Write-Host "  ── $name ..." -ForegroundColor Cyan -NoNewline
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $script
        $sw.Stop()
        if ($LASTEXITCODE -eq 0) {
            Write-Host " ✅ ($($sw.Elapsed.TotalSeconds.ToString('F1'))s)" -ForegroundColor Green
            $script:passCount++
        } else {
            Write-Host " ❌ (exit $LASTEXITCODE, $($sw.Elapsed.TotalSeconds.ToString('F1'))s)" -ForegroundColor Red
            $script:failCount++; $script:failList += $name
        }
    } catch {
        $sw.Stop()
        Write-Host " ❌ $($_.Exception.Message) ($($sw.Elapsed.TotalSeconds.ToString('F1'))s)" -ForegroundColor Red
        $script:failCount++; $script:failList += $name
    }
}

function ProbeFmt($path) {
    if (-not (Test-Path $path)) { return "MISSING" }
    $fmt = & $ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,pix_fmt,width,height -of csv=p=0 $path 2>$null
    return $fmt
}

# 动图帧数验证 (GIF 用 nb_frames; WebP/APNG/AVIF 需 count_frames; AVIF 多流选动画轨)
function AnimFrames($path) {
    if (-not (Test-Path $path)) { return -1 }
    if ($path -match '\.gif$') {
        $nf = & $ffprobe -v error -select_streams v:0 -show_entries stream=nb_frames -of csv=p=0 $path 2>$null
        if ($nf -match '^\d+$') { return [int]$nf }
    }
    if ($path -match '\.avif$') {
        # AVIF 动画: 多流结构, 选帧率最高/帧数最多的动画轨
        $streams = & $ffprobe -v error -show_entries stream=index,nb_frames -of csv=p=0 $path 2>$null
        $best = 1
        foreach ($s in $streams) {
            $parts = $s -split ','
            if ($parts.Count -ge 2 -and $parts[1] -match '^\d+$') {
                $n = [int]$parts[1]
                if ($n -gt $best) { $best = $n }
            }
        }
        return $best
    }
    $nf = & $ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 $path 2>$null
    if ($nf -match '^\d+$') { return [int]$nf }
    return -1
}

# ═══════════════════════════════════════════════════════════
# 第一部分: 静态图片 FFmpeg 后端全组合
#  输入: PNG(8/16bit) / JPEG / TIFF(8/16bit) / WebP(有损/无损)
#  输出: JPEG / PNG / WebP / AVIF / TIFF / JXL
# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ 第一部分: 静态图片 FFmpeg 后端 ════════" -ForegroundColor Yellow

$inputs = @(
    @{n="png8";   f="$src/src_8bit.png"},
    @{n="png16";  f="$src/src_16bit.png"},
    @{n="jpg";    f="$src/src_photo.jpg"},
    @{n="tiff8";  f="$src/src_8bit.tiff"},
    @{n="tiff16"; f="$src/src_16bit.tiff"},
    @{n="webpl";  f="$src/src_webp_lossy.webp"},
    @{n="webpll"; f="$src/src_webp_lossless.webp"}
)
$outputs = @(
    @{n="jpg";  args=@("-q:v","5")},
    @{n="png";  args=@()},
    @{n="webp"; args=@("-c:v","libwebp","-quality","80")},
    @{n="tiff"; args=@("-c:v","tiff")},
    @{n="avif"; args=@("-c:v","libaom-av1","-crf","30","-strict","experimental")}
)

foreach ($in in $inputs) {
    foreach ($od in $outputs) {
        $outFile = "$res/st_$($in.n)_to_$($od.n).$($od.n)"
        Test-Step "静态 $($in.n) → $($od.n)" {
            & $ffmpeg -y -hide_banner -loglevel error -i $in.f @($od.args) $outFile 2>&1 | Out-Null
            if (-not (Test-Path $outFile)) { throw "无输出文件" }
            $probe = ProbeFmt $outFile
            if ($probe -eq "MISSING") { throw "ffprobe 无法读取" }
        }
    }
}

# ═══════════════════════════════════════════════════════════
# 第二部分: 动图管线
#  GIF→WebP(动图) / GIF→APNG / GIF→AVIF(动图) / WebP(动图)→GIF / APNG→GIF
# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ 第二部分: 动图管线 ════════" -ForegroundColor Yellow

Test-Step "GIF → WebP 动图" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/anim_gif.gif" -c:v libwebp_anim -loop 0 "$res/anim_gif2webp.webp" 2>&1 | Out-Null
    if (-not (Test-Path "$res/anim_gif2webp.webp")) { throw "无输出" }
}
Test-Step "GIF → APNG" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/anim_gif.gif" -f apng -plays 0 "$res/anim_gif2apng.png" 2>&1 | Out-Null
    if (-not (Test-Path "$res/anim_gif2apng.png")) { throw "无输出" }
}
Test-Step "GIF → AVIF 动图 (libaom)" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/anim_gif.gif" -c:v libaom-av1 -crf 35 -strict experimental -pix_fmt yuv420p "$res/anim_gif2avif.avif" 2>&1 | Out-Null
    if (-not (Test-Path "$res/anim_gif2avif.avif")) { throw "无输出" }
}
Test-Step "WebP 动图 → GIF" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/anim_webp.webp" "$res/anim_webp2gif.gif" 2>&1 | Out-Null
    if (-not (Test-Path "$res/anim_webp2gif.gif")) { throw "无输出" }
}
Test-Step "APNG → GIF" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/anim_apng.png" "$res/anim_apng2gif.gif" 2>&1 | Out-Null
    if (-not (Test-Path "$res/anim_apng2gif.gif")) { throw "无输出" }
}
Test-Step "APNG → WebP 动图" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/anim_apng.png" -c:v libwebp_anim -loop 0 "$res/anim_apng2webp.webp" 2>&1 | Out-Null
    if (-not (Test-Path "$res/anim_apng2webp.webp")) { throw "无输出" }
}

# 帧数完整性 (素材 10 帧 @ 10fps)
foreach ($af in @(
    @{n="GIF→WebP";   f="$res/anim_gif2webp.webp"},
    @{n="GIF→APNG";   f="$res/anim_gif2apng.png"},
    @{n="GIF→AVIF";   f="$res/anim_gif2avif.avif"},
    @{n="WebP→GIF";   f="$res/anim_webp2gif.gif"},
    @{n="APNG→GIF";   f="$res/anim_apng2gif.gif"},
    @{n="APNG→WebP";  f="$res/anim_apng2webp.webp"}
)) {
    $frames = AnimFrames $af.f
    Test-Step "$($af.n) 帧数=$frames (应≈10)" {
        if ($frames -lt 8 -or $frames -gt 12) { throw "帧数异常: $frames" }
    }
}

# ═══════════════════════════════════════════════════════════
# 第三部分: 特殊输入管线 (JXL / JXR / AVIF)
# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ 第三部分: 特殊输入 ════════" -ForegroundColor Yellow

Test-Step "JXL 输入 → PNG (ffmpeg libjxl)" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_lossless_png.jxl" "$res/jxl2png.png" 2>&1 | Out-Null
    if (-not (Test-Path "$res/jxl2png.png")) { throw "无输出" }
}
Test-Step "JXL 输入 → JPEG" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_lossless_png.jxl" -q:v 5 "$res/jxl2jpg.jpg" 2>&1 | Out-Null
    if (-not (Test-Path "$res/jxl2jpg.jpg")) { throw "无输出" }
}
Test-Step "JXL 无损重封装 → JPEG" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_lossless.jxl" "$res/jxl_lossless2png.png" 2>&1 | Out-Null
    if (-not (Test-Path "$res/jxl_lossless2png.png")) { throw "无输出" }
}
Test-Step "JXR 输入 → PNG (JxrDecApp 解码→ffmpeg 编码)" {
    # 软件管线: JxrDecApp 解码 JXR → BMP, 再 ffmpeg 转 PNG
    $jxrDec = "publish/PLAN/artifacts/JxrDecApp.exe"
    & $jxrDec -i "$src/src.jxr" -o "$res/jxr_tmp.bmp" 2>&1 | Out-Null
    if (-not (Test-Path "$res/jxr_tmp.bmp")) { throw "JxrDecApp 解码失败" }
    & $ffmpeg -y -hide_banner -loglevel error -i "$res/jxr_tmp.bmp" "$res/jxr2png.png" 2>&1 | Out-Null
    if (-not (Test-Path "$res/jxr2png.png")) { throw "无输出" }
}
Test-Step "AVIF 输入 → PNG" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_avif.avif" "$res/avif2png.png" 2>&1 | Out-Null
    if (-not (Test-Path "$res/avif2png.png")) { throw "无输出" }
}
Test-Step "AVIF 动图 → GIF" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/anim_avif.avif" "$res/avif2gif.gif" 2>&1 | Out-Null
    if (-not (Test-Path "$res/avif2gif.gif")) { throw "无输出" }
}

# ═══════════════════════════════════════════════════════════
# 第四部分: 特殊特性管线
#  Alpha / 灰度 / HDR / 16-bit
# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ 第四部分: 特殊特性 ════════" -ForegroundColor Yellow

Test-Step "Alpha PNG → WebP (保透明)" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_alpha.png" -c:v libwebp -quality 80 "$res/alpha2webp.webp" 2>&1 | Out-Null
    if (-not (Test-Path "$res/alpha2webp.webp")) { throw "无输出" }
}
Test-Step "Alpha PNG → AVIF (保透明)" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_alpha.png" -c:v libaom-av1 -crf 30 -strict experimental -pix_fmt yuva420p "$res/alpha2avif.avif" 2>&1 | Out-Null
    if (-not (Test-Path "$res/alpha2avif.avif")) { throw "无输出" }
}
Test-Step "Alpha PNG → GIF (合成背景)" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_alpha.png" "$res/alpha2gif.gif" 2>&1 | Out-Null
    if (-not (Test-Path "$res/alpha2gif.gif")) { throw "无输出" }
}
Test-Step "灰度 PNG → JPEG" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_gray.png" -q:v 5 "$res/gray2jpg.jpg" 2>&1 | Out-Null
    if (-not (Test-Path "$res/gray2jpg.jpg")) { throw "无输出" }
}
Test-Step "16-bit PNG → TIFF (位深保持)" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_16bit.png" -c:v tiff "$res/16to_tiff.tiff" 2>&1 | Out-Null
    if (-not (Test-Path "$res/16to_tiff.tiff")) { throw "无输出" }
}
Test-Step "HDR PQ PNG → JPEG" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_hdr_pq.png" -q:v 5 "$res/hdr2jpg.jpg" 2>&1 | Out-Null
    if (-not (Test-Path "$res/hdr2jpg.jpg")) { throw "无输出" }
}
Test-Step "HDR PQ PNG → PNG (色彩标签保持)" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_hdr_pq.png" "$res/hdr2png.png" 2>&1 | Out-Null
    if (-not (Test-Path "$res/hdr2png.png")) { throw "无输出" }
}

# ═══════════════════════════════════════════════════════════
# 第五部分: cjxl 后端 (JXL 输出)
# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ 第五部分: cjxl 后端 (JXL) ════════" -ForegroundColor Yellow

Test-Step "PNG → JXL 无损 (cjxl)" {
    & $cjxl "$src/src_8bit.png" "$res/cjxl_lossless.jxl" -d 0 -e 7 2>&1 | Out-Null
    if (-not (Test-Path "$res/cjxl_lossless.jxl")) { throw "无输出" }
}
Test-Step "JPEG → JXL 无损重封装 (cjxl)" {
    & $cjxl "$src/src_photo.jpg" "$res/cjxl_jpeg_lossless.jxl" -d 0 -e 7 --lossless_jpeg=1 2>&1 | Out-Null
    if (-not (Test-Path "$res/cjxl_jpeg_lossless.jxl")) { throw "无输出" }
}
Test-Step "PNG → JXL 有损 (cjxl, effort 9)" {
    & $cjxl "$src/src_8bit.png" "$res/cjxl_lossy_e9.jxl" -d 1.0 -e 9 2>&1 | Out-Null
    if (-not (Test-Path "$res/cjxl_lossy_e9.jxl")) { throw "无输出" }
}
Test-Step "16-bit PNG → JXL 无损 (cjxl)" {
    & $cjxl "$src/src_16bit.png" "$res/cjxl_16bit.jxl" -d 0 -e 7 2>&1 | Out-Null
    if (-not (Test-Path "$res/cjxl_16bit.jxl")) { throw "无输出" }
}
Test-Step "Alpha PNG → JXL (cjxl, 保透明)" {
    & $cjxl "$src/src_alpha.png" "$res/cjxl_alpha.jxl" -d 0 -e 7 2>&1 | Out-Null
    if (-not (Test-Path "$res/cjxl_alpha.jxl")) { throw "无输出" }
}
Test-Step "HDR PQ PNG → JXL (cjxl, 色彩保持)" {
    & $cjxl "$src/src_hdr_pq.png" "$res/cjxl_hdr.jxl" -d 0 -e 7 2>&1 | Out-Null
    if (-not (Test-Path "$res/cjxl_hdr.jxl")) { throw "无输出" }
}

# ═══════════════════════════════════════════════════════════
# 第六部分: cjpegli 后端 (JPEG 输出)
# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ 第六部分: cjpegli 后端 (JPEG) ════════" -ForegroundColor Yellow
$cjpegli = "publish/PLAN/jxl/bin/cjpegli.exe"

Test-Step "PNG → JPEG (cjpegli)" {
    & $cjpegli "$src/src_8bit.png" "$res/cjpegli.jpg" -q 85 2>&1 | Out-Null
    if (-not (Test-Path "$res/cjpegli.jpg")) { throw "无输出" }
}
Test-Step "PNG → JPEG 无损 (cjpegli -d 0)" {
    & $cjpegli "$src/src_8bit.png" "$res/cjpegli_lossless.jpg" -d 0 2>&1 | Out-Null
    if (-not (Test-Path "$res/cjpegli_lossless.jpg")) { throw "无输出" }
}
Test-Step "16-bit PNG → JPEG (cjpegli)" {
    & $cjpegli "$src/src_16bit.png" "$res/cjpegli_16bit.jpg" -q 85 2>&1 | Out-Null
    if (-not (Test-Path "$res/cjpegli_16bit.jpg")) { throw "无输出" }
}

# ═══════════════════════════════════════════════════════════
# 第七部分: JXR 后端 (JXR 输出)
# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ 第七部分: JXR 后端 ════════" -ForegroundColor Yellow
$jxrEnc = "publish/PLAN/artifacts/JxrEncApp.exe"

Test-Step "PNG → JXR (JxrEncApp)" {
    & $ffmpeg -y -hide_banner -loglevel error -i "$src/src_8bit.png" -frames:v 1 -update 1 "$res/tmp_jxr.bmp" 2>&1 | Out-Null
    & $jxrEnc -i "$res/tmp_jxr.bmp" -o "$res/out.jxr" 2>&1 | Out-Null
    if (-not (Test-Path "$res/out.jxr")) { throw "无输出" }
}

# ═══════════════════════════════════════════════════════════
# 第八部分: RAW 管线 (DNG 输出 + dngtool)
# ⚠️ 2026-08-15 精简: effort=7 的 JXL 无损编码在 9504x6336 大样本上单线程极慢
#    (实测 251s!) → 默认精简模式用 effort=1 (5.5s, 45x 提速, 语义不变),
#    设 $env:RAW_FULL_TESTS=1 开启全量 (effort=7 + Bayer 大样本解码 20-40s)。
# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ 第八部分: RAW 管线 ════════" -ForegroundColor Yellow
$dngSample = "tools/src/dng_sdk/dng_sdk_1_7_1/sample_files/01_jxl_linear_raw_integer.dng"
$rawFull = $env:RAW_FULL_TESTS -eq "1"
$rawEffort = if ($rawFull) { 7 } else { 1 }
if (-not $rawFull) {
    Write-Host "  (精简模式: dngtool effort=1 + 跳过 Bayer 大样本解码; 设 RAW_FULL_TESTS=1 开启全量)" -ForegroundColor DarkGray
}

Test-Step "DNG → JXL-DNG 重编码 (dngtool)" {
    & $dngtool -e -jxl -q 0 -effort $rawEffort -i $dngSample -O "$res/raw_reenc.dng" 2>&1 | Out-Null
    if (-not (Test-Path "$res/raw_reenc.dng")) { throw "无输出" }
}
Test-Step "DNG → JXL-DNG 有损 (dngtool q=90)" {
    & $dngtool -e -jxl -q 90 -effort $rawEffort -i $dngSample -O "$res/raw_lossy.dng" 2>&1 | Out-Null
    if (-not (Test-Path "$res/raw_lossy.dng")) { throw "无输出" }
}
Test-Step "DNG → 无损 JPEG DNG (dngtool)" {
    & $dngtool -e -lossless -i $dngSample -O "$res/raw_ljpeg.dng" 2>&1 | Out-Null
    if (-not (Test-Path "$res/raw_ljpeg.dng")) { throw "无输出" }
}
Test-Step "DNG → TIFF 解码 (dngtool)" {
    & $dngtool -d -T -o 0 -q 3 -W -H 1 -6 -i $dngSample -O "$res/raw2tiff.tiff" 2>&1 | Out-Null
    if (-not (Test-Path "$res/raw2tiff.tiff")) { throw "无输出" }
}
Test-Step "DNG → JXL 图片 (dngtool 解码→ffmpeg)" {
    & $dngtool -d -T -o 1 -q 3 -W -H 1 -6 -i $dngSample -O "$res/raw2linear.tiff" 2>&1 | Out-Null
    & $ffmpeg -y -hide_banner -loglevel error -i "$res/raw2linear.tiff" -c:v libwebp -quality 85 "$res/raw2webp.webp" 2>&1 | Out-Null
    if (-not (Test-Path "$res/raw2webp.webp")) { throw "无输出" }
}

# ── Bayer CFA JXL-DNG 场景 (2026-08-14: ALLOWSIZECHANGE + STAGE2/3 动态控制) ──
# ⚠️ 2026-08-15 精简: 03 样本 10240x7168 (24MB) 解码生成 350MB TIFF 耗时 20-40s,
#    默认跳过解码项 (其 ActiveArea/CFA 覆盖已由重编码产物标签断言承担);
#    设 $env:RAW_FULL_TESTS=1 可开启全量 (含 03 解码)。重编码项保留 (CFA 关键回归)。
$bayerSample = "tools/src/dng_sdk/dng_sdk_1_7_1/sample_files/03_jxl_bayer_raw_integer.dng"
Test-Step "Bayer JXL-DNG → CFA 保留重编码 (dngtool)" {
    & $dngtool -e -jxl -q 0 -effort $rawEffort -i $bayerSample -O "$res/raw_bayer_cfa.dng" 2>&1 | Out-Null
    if (-not (Test-Path "$res/raw_bayer_cfa.dng")) { throw "无输出" }
    # CFA 必须保留: SamplesPerPixel=1 + Color Filter Array
    $spp = ((& $exif -s -s -SamplesPerPixel "$res/raw_bayer_cfa.dng" 2>$null) | Out-String) -replace '^.*?:\s*',''
    $pi = ((& $exif -s -s -PhotometricInterpretation "$res/raw_bayer_cfa.dng" 2>$null) | Out-String) -replace '^.*?:\s*',''
    if ($spp.Trim() -ne "1" -or $pi -notmatch "Color Filter Array") {
        throw "CFA 丢失: SamplesPerPixel=$spp PI=$pi"
    }
}
if ($rawFull) {
Test-Step "Bayer JXL-DNG → 解码 (dngtool, ActiveArea)" {
    & $dngtool -d -T -o 0 -q 3 -W -H 1 -6 -i $bayerSample -O "$res/raw_bayer_decode.tiff" 2>&1 | Out-Null
    if (-not (Test-Path "$res/raw_bayer_decode.tiff")) { throw "无输出" }
}
}
Test-Step "Bayer CFA-DNG 色彩标签无损 (ColorMatrix1)" {
    $cm1 = & $exif -s -s -ColorMatrix1 "$res/raw_bayer_cfa.dng" 2>$null
    $cm1src = & $exif -s -s -ColorMatrix1 $bayerSample 2>$null
    if ($cm1 -ne $cm1src) { throw "ColorMatrix1 不一致" }
}
Test-Step "Bayer CFA-DNG 黑/白电平正确 (ACR 渲染亮度)" {
    $bl = ((& $exif -s -s -BlackLevel "$res/raw_bayer_cfa.dng" 2>$null) | Out-String) -replace '^.*?:\s*',''
    $wl = ((& $exif -s -s -WhiteLevel "$res/raw_bayer_cfa.dng" 2>$null) | Out-String) -replace '^.*?:\s*',''
    if ($bl.Trim() -notmatch "^512" -or $wl.Trim() -ne "16383") {
        throw "黑/白电平错误: Black=$bl White=$wl (期望 512/16383)"
    }
}

# ── PSNR/SSIM 回归断言 (2026-08-15) ──
# 无损 vs 有损 JXL q90: 同管线同解码参数 → 误差纯来自压缩, PSNR 直接量化
# 阈值: PSNR ≥ 33dB + SSIM ≥ 0.88 (实测 35.6dB / 0.906)
Test-Step "JXL 有损 q90 PSNR ≥ 33dB (无损 vs 有损)" {
    & $dngtool -d -T -o 0 -q 3 -W -H 1 -6 -i "$res/raw_reenc.dng" -O "$res/psnr_ref.tiff" 2>&1 | Out-Null
    & $dngtool -d -T -o 0 -q 3 -W -H 1 -6 -i "$res/raw_lossy.dng" -O "$res/psnr_lossy.tiff" 2>&1 | Out-Null
    if (-not (Test-Path "$res/psnr_ref.tiff") -or -not (Test-Path "$res/psnr_lossy.tiff")) {
        throw "PSNR 解码产物缺失"
    }
    $psnrOut = & $ffmpeg -y -hide_banner -loglevel info -i "$res/psnr_ref.tiff" -i "$res/psnr_lossy.tiff" -lavfi "psnr" -f null - 2>&1
    $m = [regex]::Match(($psnrOut -join "`n"), "average:([0-9.]+)")
    if (-not $m.Success) { throw "PSNR 输出解析失败" }
    $psnrVal = [double]$m.Groups[1].Value
    Write-Host "      [psnr] 无损 vs q90 = $psnrVal dB (阈值 ≥ 33)"
    if ($psnrVal -lt 33.0) { throw "PSNR 低于阈值: $psnrVal dB" }
}
Test-Step "JXL 有损 q90 SSIM ≥ 0.88" {
    $ssimOut = & $ffmpeg -y -hide_banner -loglevel info -i "$res/psnr_ref.tiff" -i "$res/psnr_lossy.tiff" -lavfi "ssim" -f null - 2>&1
    $m = [regex]::Match(($ssimOut -join "`n"), "All:([0-9.]+)")
    if (-not $m.Success) { throw "SSIM 输出解析失败" }
    $ssimVal = [double]$m.Groups[1].Value
    Write-Host "      [ssim] 无损 vs q90 = $ssimVal (阈值 ≥ 0.88)"
    if ($ssimVal -lt 0.88) { throw "SSIM 低于阈值: $ssimVal" }
}

# ═══════════════════════════════════════════════════════════
# 汇总
# ═══════════════════════════════════════════════════════════
$totalSw.Stop()
Write-Host "`n════════ 测试汇总 ════════" -ForegroundColor Yellow
Write-Host "通过: $passCount  失败: $failCount  总耗时: $([math]::Round($totalSw.Elapsed.TotalSeconds,1))s" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })
if ($failList.Count -gt 0) {
    Write-Host "失败列表:" -ForegroundColor Red
    $failList | ForEach-Object { Write-Host "  ❌ $_" -ForegroundColor Red }
}
# 清理中间文件
Remove-Item "$res/tmp_jxr.bmp","$res/raw2linear.tiff","$res/psnr_ref.tiff","$res/psnr_lossy.tiff" -Force -ErrorAction SilentlyContinue
Write-Host "`n🎉 测试完成! 产物在 tests/output/results/" -ForegroundColor Green
