# ═══════════════════════════════════════════════════════════
#  generate-sources.ps1 — 生成全格式测试素材库
#  输出: tests/output/sources/ (git 忽略)
# ═══════════════════════════════════════════════════════════
$ErrorActionPreference = "Stop"
$out = "tests/output/sources"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$ffmpeg = (Get-ChildItem "publish/PLAN/ffmpeg-full*/ffmpeg.exe" | Select-Object -First 1).FullName
$exif   = "publish/PLAN/exiftool/exiftool.exe"
if (-not $ffmpeg) { throw "ffmpeg 未找到" }

function Gen($name, $ffargs) {
    Write-Host "生成 $name ..." -ForegroundColor DarkGray
    & $ffmpeg -y -hide_banner -loglevel error @ffargs "$out/$name" 2>&1 | Out-Null
    if (-not (Test-Path "$out/$name")) { throw "生成失败: $name" }
    Write-Host "  ✅ $name ($([math]::Round((Get-Item "$out/$name").Length/1KB,1)) KB)" -ForegroundColor Green
}

# 静态单图 (需 -update 1 写入单张图片)
function GenStill($name, $lavfiSrc, [string[]]$extra) {
    Write-Host "生成 $name ..." -ForegroundColor DarkGray
    & $ffmpeg -y -hide_banner -loglevel error -f lavfi -i $lavfiSrc -frames:v 1 -update 1 @extra $out/$name 2>&1 | Out-Null
    if (-not (Test-Path "$out/$name")) { throw "生成失败: $name" }
    Write-Host "  ✅ $name ($([math]::Round((Get-Item "$out/$name").Length/1KB,1)) KB)" -ForegroundColor Green
}

# ── 静态图片 (多样色彩/纹理) ──
# testsrc2: 彩色条+滚动文字+运动 (好素材)
GenStill "src_8bit.png"    "testsrc2=size=512x384:duration=0.1"
GenStill "src_16bit.png"   "testsrc2=size=512x384:duration=0.1" @("-pix_fmt","rgb48le")
GenStill "src_alpha.png"   "color=red:size=256x256:rate=1" @("-vf","format=rgba,drawtext=text='A':fontsize=120:fontcolor=white:x=80:y=80")
GenStill "src_photo.jpg"   "testsrc2=size=512x384:duration=0.1" @("-q:v","2")
GenStill "src_gray.png"    "smptebars=size=256x256:rate=1" @("-vf","format=gray")
GenStill "src_16bit.tiff"  "testsrc2=size=512x384:duration=0.1" @("-pix_fmt","rgb48le","-c:v","tiff")
GenStill "src_8bit.tiff"   "testsrc2=size=512x384:duration=0.1" @("-c:v","tiff")
GenStill "src_webp_lossy.webp"  "testsrc2=size=512x384:duration=0.1" @("-c:v","libwebp","-quality","80")
# ⚠️ WebP 无损需 rgb24 像素格式, 否则 yuv420p 色度采样导致"无损"失真
GenStill "src_webp_lossless.webp" "testsrc2=size=512x384:duration=0.1" @("-c:v","libwebp","-lossless","1","-pix_fmt","rgb24")

# 高动态范围 (HDR) 素材: smpte2084 PQ + bt2020
GenStill "src_hdr_pq.png"  "testsrc2=size=512x384:duration=0.1" @("-pix_fmt","rgb48le","-color_primaries","bt2020","-color_trc","smpte2084","-colorspace","bt2020nc")
GenStill "src_hdr_hlg.png" "testsrc2=size=512x384:duration=0.1" @("-pix_fmt","rgb48le","-color_primaries","bt2020","-color_trc","arib-std-b67","-colorspace","bt2020nc")

# ── 动图素材 ──
Gen "anim_gif.gif"    @("-f","lavfi","-i","testsrc2=size=256x192:rate=10:duration=1","-vf","drawtext=text='%{n}':fontsize=40:fontcolor=white:x=100:y=80","-frames:v","10")
Gen "anim_webp.webp"  @("-f","lavfi","-i","testsrc2=size=256x192:rate=10:duration=1","-c:v","libwebp_anim","-loop","0","-frames:v","10")
Gen "anim_apng.png"   @("-f","lavfi","-i","testsrc2=size=256x192:rate=10:duration=1","-f","apng","-plays","0","-frames:v","10")

# ── AVIF 素材 (需要 libaom/libsvtav1) ──
GenStill "src_avif.avif"   "testsrc2=size=512x384:duration=0.1" @("-c:v","libaom-av1","-crf","30","-strict","experimental","-pix_fmt","yuv420p")
Gen "anim_avif.avif"  @("-f","lavfi","-i","testsrc2=size=256x192:rate=10:duration=1","-c:v","libaom-av1","-crf","35","-strict","experimental","-pix_fmt","yuv420p","-frames:v","8")

# ── JXL / JXR 素材 ──
$cjxl = "publish/PLAN/jxl/bin/cjxl.exe"
if (Test-Path $cjxl) {
    Write-Host "生成 JXL 素材 ..." -ForegroundColor DarkGray
    & $cjxl "$out/src_photo.jpg" "$out/src_lossless.jxl" -d 0 -e 3 --lossless_jpeg=1 2>&1 | Out-Null
    & $cjxl "$out/src_8bit.png" "$out/src_lossless_png.jxl" -d 0 -e 3 2>&1 | Out-Null
    & $cjxl "$out/src_8bit.png" "$out/src_lossy.jxl" -d 1.5 -e 5 2>&1 | Out-Null
    Write-Host "  ✅ JXL 素材" -ForegroundColor Green
}
$jxr = "publish/PLAN/artifacts/JxrEncApp.exe"
if (Test-Path $jxr) {
    Write-Host "生成 JXR 素材 ..." -ForegroundColor DarkGray
    # JxrEncApp 接受 BMP/TIF/HDR; 用 BMP 输入
    & $ffmpeg -y -hide_banner -loglevel error -f lavfi -i "testsrc2=size=512x384:duration=0.1" -frames:v 1 -update 1 "$out/src_jxr.bmp" 2>&1 | Out-Null
    & $jxr -i "$out/src_jxr.bmp" -o "$out/src.jxr" 2>&1 | Out-Null
    if (Test-Path "$out/src.jxr") { Write-Host "  ✅ JXR 素材" -ForegroundColor Green }
}

# ICC 配置文件 (色彩管理测试)
$icc = "C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm"
if (Test-Path $icc) {
    Copy-Item $icc "$out/srgb.icc" -Force
    Write-Host "  ✅ srgb.icc" -ForegroundColor Green
}

# 给 JPEG 嵌入 ICC + EXIF (元数据测试)
if (Test-Path "$out/srgb.icc") {
    & $exif "-icc_profile<=$out/srgb.icc" "-DateTimeOriginal=2026:01:15 10:30:00" "-Artist=TestUser" "-GPSLatitude=31.23" "-GPSLongitude=121.47" "$out/src_photo.jpg" 2>&1 | Out-Null
    Write-Host "  ✅ src_photo.jpg 已嵌 ICC+EXIF+GPS" -ForegroundColor Green
}

Write-Host "`n📦 素材库完成:" -ForegroundColor Cyan
Get-ChildItem $out -File | Select-Object Name, @{N='KB';E={[math]::Round($_.Length/1KB,1)}} | Format-Table -AutoSize
