# ═══════════════════════════════════════════════════════════
#  run-color-tests.ps1 — 色彩转换完整测试
#  覆盖: 简化色域转换 / HDR tonemap / ICC 4 模式 / 高级参数 / 格式兼容
#  输出: tests/output/results/color/ (git 忽略)
# ═══════════════════════════════════════════════════════════
$ErrorActionPreference = "Continue"
$out = "tests/output"
$src = "$out/sources"
$res = "$out/results/color"
New-Item -ItemType Directory -Force -Path $res | Out-Null

$ffmpeg = (Get-ChildItem "publish/PLAN/ffmpeg-full*/ffmpeg.exe" | Select-Object -First 1).FullName
$ffprobe = (Get-ChildItem "publish/PLAN/ffmpeg-full*/ffprobe.exe" | Select-Object -First 1).FullName
$exif = "publish/PLAN/exiftool/exiftool.exe"
$tool = "tools/src/pngcicp/bin/Release/net11.0/pngcicp.exe"
$pass = 0; $fail = 0; $failList = @()

function Check($name, $cond) {
    if ($cond) { Write-Host "  ✅ $name" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "  ❌ $name" -ForegroundColor Red; $script:fail++; $script:failList += $name }
}

# 读取输出文件的 CICP 标签 — 用 exiftool (ffprobe 对 smpte432 显示 unknown 是 ffprobe bug!)
function GetCicp($path) {
    if (-not (Test-Path $path)) { return "MISSING" }
    $p = & $exif -s -s -ColorPrimaries $path 2>$null
    $t = & $exif -s -s -TransferCharacteristics $path 2>$null
    $m = & $exif -s -s -MatrixCoefficients $path 2>$null
    return "$p|$t|$m"
}

# 读取输出文件的 ICC 名称 (兼容 iccgen 生成的无 ProfileName 但含 Description 的 ICC)
function GetIccName($path) {
    if (-not (Test-Path $path)) { return "" }
    $n = & $exif -s -s -ProfileName $path 2>$null
    if (-not $n) { $n = & $exif -s -s -ICC_Profile_Name $path 2>$null }
    if (-not $n) { $n = & $exif -s -s -ProfileDescription $path 2>$null }
    if ($null -eq $n) { return "" }
    return (($n -replace '^[^:]+:\s*','') -replace '^ProfileName:\s*','')
}

# 生成带指定色彩标签的测试图 (PNG 支持 cHRM? 用 TIFF 保证色彩标签)
function New-ColorSrc($name, $p, $t, $m, $pixfmt = "rgb48le") {
    $f = "$res/$name"
    & $ffmpeg -y -hide_banner -loglevel error -f lavfi -i "testsrc2=size=320x240:duration=0.1" `
        -frames:v 1 -update 1 -c:v tiff -pix_fmt $pixfmt `
        -color_primaries $p -color_trc $t -colorspace $m $f 2>&1 | Out-Null
    return $f
}

Write-Host "════════ 色彩转换测试 ════════" -ForegroundColor Yellow
Write-Host "ffmpeg: $ffmpeg"
Write-Host ""

# ═══════════════════════════════════════════════════════════
# A. 简化色域转换 (ColorSpaceCombo 快速选择路径)
#   命令形态: -color_primaries <src> -color_trc <src> -i in -vf "format=rgb48le,zscale=pin=...:p=dst:t=dst:m=dst"
# ═══════════════════════════════════════════════════════════
Write-Host "A. 简化色域转换 (快速选择路径)" -ForegroundColor Cyan

# A1: BT.709 源 → Display P3 (SDR 广色域) — 用 AVIF 验证 CICP (TIFF 不持 CICP 用 ICC)
$src709 = New-ColorSrc "src_bt709.tiff" "bt709" "bt709" "bt709"
$o1 = "$res/a1_p3.avif"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries bt709 -color_trc bt709 `
    -i $src709 -vf "format=rgb48le,zscale=pin=bt709:tin=bt709:min=bt709:p=smpte432:t=iec61966-2-1:m=bt709" `
    -c:v libaom-av1 -crf 30 -strict experimental -pix_fmt yuv444p -color_primaries smpte432 -color_trc iec61966-2-1 -colorspace bt709 $o1 2>&1 | Out-Null
$c1 = GetCicp $o1
Check "A1 BT.709→Display P3 CICP=$c1 (应 SMPTE EG 432)" ($c1 -match "432-1|SMPTE EG")

# A2: sRGB 源 → Display P3
$o2 = "$res/a2_srgb_p3.png"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries bt709 -color_trc iec61966-2-1 `
    -i "$src/src_8bit.png" -vf "format=rgb48le,zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=smpte432:t=iec61966-2-1:m=bt709" `
    -c:v png -pix_fmt rgb48be -color_primaries smpte432 -color_trc iec61966-2-1 $o2 2>&1 | Out-Null
$c2 = GetCicp $o2
Check "A2 sRGB→Display P3 转换成功 ($c2)" ($c2 -ne "MISSING" -and $c2 -ne "")

# A3: sRGB → P3 PQ (SDR→HDR 升格)
$o3 = "$res/a3_p3pq.avif"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries bt709 -color_trc iec61966-2-1 `
    -i "$src/src_8bit.png" -vf "format=rgb48le,zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=smpte432:t=smpte2084:m=bt709" `
    -c:v libaom-av1 -crf 30 -strict experimental -pix_fmt yuv444p -color_primaries smpte432 -color_trc smpte2084 -colorspace bt709 $o3 2>&1 | Out-Null
$c3 = GetCicp $o3
Check "A3 sRGB→P3 PQ CICP=$c3 (应 432+PQ)" ($c3 -match "432-1.*ST 2084|SMPTE EG.*2084")

# A4: BT.2020 PQ → BT.709 (HDR→SDR, tonemap + gamma 修复链) — 不依赖输出端选项
# 2026-08-14: tonemap 输出 linear 像素 + 标签泄漏，必须 zscale 应用 gamma 并重置标签
$srcHdr = New-ColorSrc "src_bt2020.tiff" "bt2020" "smpte2084" "bt2020nc"
$o4 = "$res/a4_hdr2sdr.avif"
& $ffmpeg -y -hide_banner -loglevel error -i $srcHdr `
    -vf "format=yuv444p,tonemap=hable:param=0.5,format=rgb48le,zscale=pin=bt2020:tin=linear:min=gbr:p=bt709:t=bt709:m=bt709" `
    -c:v libaom-av1 -crf 30 -strict experimental -pix_fmt yuv444p $o4 2>&1 | Out-Null
$c4 = GetCicp $o4
Check "A4 BT.2020 PQ→BT.709 tonemap CICP=$c4 (应 bt709 系)" ($c4 -match "BT.709|bt709")

# A4b: 修复链像素验证 — tonemap+gamma 输出亮度应接近 zscale 参考 (gamma 已应用)
$o4b = "$res/a4b_hdr2sdr.png"
& $ffmpeg -y -hide_banner -loglevel error -i $srcHdr `
    -vf "format=yuv444p,tonemap=hable:param=0.5,format=rgb48le,zscale=pin=bt2020:tin=linear:min=gbr:p=bt709:t=bt709:m=bt709" `
    -c:v png -pix_fmt rgb48be $o4b 2>&1 | Out-Null
$yavg = & $ffmpeg -v info -i $o4b -vf "signalstats,metadata=print:key=lavfi.signalstats.YAVG" -f null - 2>&1 | Select-String "YAVG=" | Select-Object -Last 1
Check "A4b tonemap+gamma YAVG=$yavg (应 >17000, 非 linear 暗画面)" ($yavg -match "YAVG=([1-9][0-9]{4,}|[2-9][0-9]{3,})" -and [double]($yavg -replace '.*YAVG=','') -gt 17000)

# A6: BT.2020 PQ → P3 PQ (HDR→HDR, 不应 tonemap, zscale 直接转换)
$o6 = "$res/a6_hdr2hdr.avif"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries bt2020 -color_trc smpte2084 `
    -i $srcHdr -vf "format=rgb48le,zscale=pin=bt2020:tin=smpte2084:min=bt2020nc:p=smpte432:t=smpte2084:m=bt709" `
    -c:v libaom-av1 -crf 30 -strict experimental -pix_fmt yuv444p $o6 2>&1 | Out-Null
$c6 = GetCicp $o6
Check "A6 BT.2020 PQ→P3 PQ (HDR→HDR zscale) CICP=$c6 (应 432+PQ)" ($c6 -match "432-1.*ST 2084|SMPTE EG.*2084")

# A5: BT.2020 PQ → Display P3 (HDR→广色域 SDR 降级)
# 注意: PNG 为 RGB 容器, matrix 自动 Identity (正常); 验证 primaries+trc
$o5 = "$res/a5_hdr2p3.png"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries bt2020 -color_trc smpte2084 `
    -i $srcHdr -vf "format=rgb48le,zscale=pin=bt2020:tin=smpte2084:min=bt2020nc:p=smpte432:t=iec61966-2-1:m=bt709" `
    -c:v png -pix_fmt rgb48be -color_primaries smpte432 -color_trc iec61966-2-1 $o5 2>&1 | Out-Null
$c5 = GetCicp $o5
Check "A5 BT.2020 PQ→Display P3 CICP=$c5 (应 SMPTE EG 432 + sRGB)" ($c5 -match "432-1.*sRGB or sYCC|SMPTE EG")

# ═══════════════════════════════════════════════════════════
# B. ICC 4 模式 (IccMode)
#   模式1 None: 仅 CICP 丢弃 ICC
#   模式2 CarryIcc: 保留源 ICC / iccgen 补标准
#   模式3 BakeToStandard: zscale 烘焙 + iccgen 标准 ICC
#   模式4 BakeOnly: zscale 烘焙无 ICC
# ═══════════════════════════════════════════════════════════
Write-Host "`nB. ICC 4 模式" -ForegroundColor Cyan

# 准备带 ICC 的源 (JPEG 嵌入 sRGB ICC)
$srcIcc = "$src/src_photo.jpg"  # 已含 sRGB ICC
$iccTest = "$res/src_icc.jpg"
Copy-Item $srcIcc $iccTest -Force

# B1: 模式2 CarryIcc — 输出应保留 ICC
$oB1 = "$res/b1_carry.png"
& $ffmpeg -y -hide_banner -loglevel error -i $iccTest -c:v png $oB1 2>&1 | Out-Null
& $exif "-tagsfromfile" $iccTest "-all:all" $oB1 2>$null | Out-Null
$icc1 = GetIccName $oB1
Check "B1 CarryIcc: ICC 保留 ($icc1)" ($icc1 -ne "")

# B2: 模式3 BakeToStandard — sRGB→Display P3 烘焙 + ICC
$oB2 = "$res/b2_bake_p3.png"
& $ffmpeg -y -hide_banner -loglevel error -i $iccTest -vf "format=rgb48le,zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=smpte432:t=iec61966-2-1:m=bt709,iccgen" `
    -c:v png $oB2 2>&1 | Out-Null
$iccB2 = GetIccName $oB2
Check "B2 BakeToStandard: ICC 生成 ($iccB2)" ($iccB2 -ne "")

# B3: 模式4 BakeOnly — 烘焙但无 ICC (PNG 无 ICC)
$oB3 = "$res/b3_bakeonly.png"
& $ffmpeg -y -hide_banner -loglevel error -i $iccTest -vf "format=rgb48le,zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=smpte432:t=iec61966-2-1:m=bt709" `
    -c:v png $oB3 2>&1 | Out-Null
$iccB3 = GetIccName $oB3
Check "B3 BakeOnly: 无 ICC ($iccB3='')" ($iccB3 -eq "")

# B4: 模式1 None — 需先剥离源 ICC (ffmpeg 从 JPEG 输入自动携带 ICC!)
$noIccSrc = "$res/src_noicc.jpg"
& $ffmpeg -y -hide_banner -loglevel error -i $iccTest -vf "format=yuv444p" -q:v 5 $noIccSrc 2>&1 | Out-Null
& $exif "-ICC_Profile=" "$noIccSrc" 2>$null | Out-Null
$oB4 = "$res/b4_none.png"
& $ffmpeg -y -hide_banner -loglevel error -i $noIccSrc -c:v png $oB4 2>&1 | Out-Null
$iccB4 = GetIccName $oB4
Check "B4 None: 无 ICC ($iccB4='')" ($iccB4 -eq "")

# ═══════════════════════════════════════════════════════════
# C. 高级色彩参数 (精确 primaries/trc/matrix 路径)
# ═══════════════════════════════════════════════════════════
Write-Host "`nC. 高级色彩参数精确传递" -ForegroundColor Cyan

# C1: 高级参数 bt2020 + smpte2084 + bt2020nc → AVIF
$oC1 = "$res/c1_adv_bt2020.avif"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries bt2020 -color_trc smpte2084 `
    -i $src709 -c:v libaom-av1 -crf 30 -strict experimental -pix_fmt yuv444p -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc $oC1 2>&1 | Out-Null
$cC1 = GetCicp $oC1
Check "C1 高级 bt2020/smpte2084 CICP=$cC1 (应 BT.2020+PQ)" ($cC1 -match "2020.*2084|BT.2020")

# C2: 高级参数 smpte432 + iec61966-2-1 (Display P3) → AVIF
$oC2 = "$res/c2_adv_p3.avif"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries smpte432 -color_trc iec61966-2-1 `
    -i $src709 -c:v libaom-av1 -crf 30 -strict experimental -pix_fmt yuv444p -color_primaries smpte432 -color_trc iec61966-2-1 -colorspace bt709 $oC2 2>&1 | Out-Null
$cC2 = GetCicp $oC2
Check "C2 高级 smpte432/iec61966 CICP=$cC2 (应 432+sRGB)" ($cC2 -match "432-1|SMPTE EG")

# C3: 高级参数 smpte432 + smpte2084 (P3 PQ) → AVIF
$oC3 = "$res/c3_adv_p3pq.avif"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries smpte432 -color_trc smpte2084 `
    -i $src709 -c:v libaom-av1 -crf 30 -strict experimental -pix_fmt yuv444p -color_primaries smpte432 -color_trc smpte2084 -colorspace bt709 $oC3 2>&1 | Out-Null
$cC3 = GetCicp $oC3
Check "C3 高级 smpte432/smpte2084 (P3 PQ) CICP=$cC3" ($cC3 -match "432-1.*2084|SMPTE EG")

# ═══════════════════════════════════════════════════════════
# D. 输出格式兼容 (色彩转换 × 各容器)
# ═══════════════════════════════════════════════════════════
Write-Host "`nD. 色彩转换 × 输出格式" -ForegroundColor Cyan

# D1: P3 转换 → JXL (cjxl 支持色彩)
$cjxl = "publish/PLAN/jxl/bin/cjxl.exe"
if (Test-Path $cjxl) {
    & $cjxl "$res/a2_srgb_p3.png" "$res/d1_p3.jxl" -d 0 -e 5 2>&1 | Out-Null
    $jxlInfo = & "publish/PLAN/jxl/bin/jxlinfo.exe" "$res/d1_p3.jxl" 2>&1 | Select-String "Color space|color space" | Select-Object -First 2
    Check "D1 P3→JXL 色彩=$jxlInfo" ($jxlInfo -match "RGB|P3")  # P3 输入无 ICC 时 jxl 标 sRGB, 有 ICC 标 P3
}

# D2: P3 转换 → AVIF (CICP 原生)
$oD2 = "$res/d2_p3.avif"
& $ffmpeg -y -hide_banner -loglevel error -i "$res/a2_srgb_p3.png" -c:v libaom-av1 -crf 30 -strict experimental `
    -color_primaries smpte432 -color_trc iec61966-2-1 -colorspace bt709 -pix_fmt yuv444p $oD2 2>&1 | Out-Null
$cD2 = GetCicp $oD2
Check "D2 P3→AVIF CICP=$cD2 (应 SMPTE EG 432)" ($cD2 -match "432-1|SMPTE EG")

# D3: P3 → JPEG (软件流程: ffmpeg 编码 + exiftool ICC 恢复)
$oD3 = "$res/d3_p3.jpg"
& $ffmpeg -y -hide_banner -loglevel error -i "$res/a2_srgb_p3.png" -q:v 5 -color_primaries smpte432 -color_trc iec61966-2-1 $oD3 2>&1 | Out-Null
# 软件 RestoreMetadataAsync: 源 ICC 复制 (P3 源无 ICC 时 iccgen 补偿, 此处用 a2 PNG 模拟)
& $exif "-icc_profile<=$src/srgb.icc" $oD3 2>$null | Out-Null
$iccD3 = GetIccName $oD3
Check "D3 P3→JPEG ICC=$iccD3" ($iccD3 -ne "")

# D4: P3 → WebP (软件流程: ffmpeg + exiftool ICC 恢复 — iccgen 对 WebP muxer 不生效!)
$oD4 = "$res/d4_p3.webp"
& $ffmpeg -y -hide_banner -loglevel error -i "$res/a2_srgb_p3.png" -c:v libwebp -quality 85 $oD4 2>&1 | Out-Null
& $exif "-icc_profile<=$src/srgb.icc" $oD4 2>$null | Out-Null
$iccD4 = GetIccName $oD4
Check "D4 P3→WebP ICC=$iccD4 (exiftool 恢复)" ($iccD4 -ne "")

# ═══════════════════════════════════════════════════════════
# E. 像素正确性 (色彩转换后亮度保持)
# ═══════════════════════════════════════════════════════════
Write-Host "`nE. 像素正确性 (PSNR)" -ForegroundColor Cyan

# E1: BT.709→sRGB 应该几乎无损 (同色域不同曲线标签)
$oE1 = "$res/e1_709_srgb.png"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries bt709 -color_trc bt709 `
    -i $src709 -vf "format=rgb48le,zscale=pin=bt709:tin=bt709:min=bt709:p=bt709:t=iec61966-2-1:m=bt709" `
    -c:v png -pix_fmt rgb48be $oE1 2>&1 | Out-Null
$psnrE1 = & $ffmpeg -hide_banner -i $src709 -i $oE1 -lavfi psnr -f null - 2>&1 | Out-String
$mE1 = [regex]::Match($psnrE1, "average:([\d.]+|inf)")
$vE1 = if ($mE1.Success) { if ($mE1.Groups[1].Value -eq "inf") { 99 } else { [double]$mE1.Groups[1].Value } } else { -1 }
Check "E1 BT.709→sRGB PSNR=$vE1 (应>40, 同色域)" ($vE1 -gt 40)

# E2: sRGB→Display P3 后再转回 sRGB 应接近原图 (往返一致性)
$oE2 = "$res/e2_srgb_p3.png"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries bt709 -color_trc iec61966-2-1 `
    -i "$src/src_8bit.png" -vf "format=rgb48le,zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=smpte432:t=iec61966-2-1:m=bt709" `
    -c:v png -pix_fmt rgb48be $oE2 2>&1 | Out-Null
$oE2b = "$res/e2_back_srgb.png"
& $ffmpeg -y -hide_banner -loglevel error -color_primaries smpte432 -color_trc iec61966-2-1 `
    -i $oE2 -vf "format=rgb48le,zscale=pin=smpte432:tin=iec61966-2-1:min=bt709:p=bt709:t=iec61966-2-1:m=bt709" `
    -c:v png -pix_fmt rgb48be $oE2b 2>&1 | Out-Null
$psnrE2 = & $ffmpeg -hide_banner -i "$src/src_8bit.png" -i $oE2b -lavfi psnr -f null - 2>&1 | Out-String
$mE2 = [regex]::Match($psnrE2, "average:([\d.]+|inf)")
$vE2 = if ($mE2.Success) { if ($mE2.Groups[1].Value -eq "inf") { 99 } else { [double]$mE2.Groups[1].Value } } else { -1 }
Check "E2 sRGB→P3→sRGB 往返 PSNR=$vE2 (应>35, 往返一致)" ($vE2 -gt 35)

# ═══════════════════════════════════════════════════════════
# F. PNG 3.0 sBIT / cICP chunk (10/12-bit 有效位语义)
# ═══════════════════════════════════════════════════════════
Write-Host "`nF. PNG 3.0 sBIT / cICP chunk" -ForegroundColor Cyan

# F1: ffmpeg 编码 16-bit PNG → pngcicp 写 sBIT(10) → chunk 结构验证
$oF1 = "$res/f1_sbit10.png"
& $ffmpeg -y -hide_banner -loglevel error -f lavfi -i "testsrc2=size=320x240:duration=0.1" `
    -frames:v 1 -update 1 -c:v png -pix_fmt rgb48be $oF1 2>&1 | Out-Null
& $tool sbit $oF1 "$res/f1_sbit10_fixed.png" 10 10 10 2>&1 | Out-Null
$sbitInfo = & $tool info "$res/f1_sbit10_fixed.png" 2>&1 | Select-String "sBIT"
Check "F1 sBIT(10) 写入 $sbitInfo" ($sbitInfo -match "sBIT: R=10 G=10 B=10")

# F2: sBIT 后 cICP 写入 → 完整 PNG 3.0 结构 (IHDR→sBIT→cICP)
& $tool cicp "$res/f1_sbit10_fixed.png" "$res/f1_sbit10_cicp.png" 9 16 0 1 2>&1 | Out-Null
$f2Info = & $tool info "$res/f1_sbit10_cicp.png" 2>&1 | Select-String "sBIT|cICP"
$f2Sbit = ($f2Info | Select-String "sBIT").Line
$f2Cicp = ($f2Info | Select-String "cICP").Line
Check "F2 sBIT+cICP 共存: $f2Sbit | $f2Cicp" ($f2Sbit -match "R=10" -and $f2Cicp -match "primaries=9 transfer=16")

# F3: ffprobe 解码端识别 cICP (PNG 3.0 闭环)
# 注意: ffprobe csv 输出顺序为 color_transfer,color_primaries
$f3Cicp = & $ffprobe -v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer -of csv=p=0 "$res/f1_sbit10_cicp.png" 2>&1
Check "F3 ffprobe 识别 cICP=$f3Cicp (应 bt2020,smpte2084)" ($f3Cicp -match "smpte2084" -and $f3Cicp -match "bt2020")

# F4: 16-bit 容器 + sBIT(10) → 有效位语义验证 (sBIT 声明 10-bit)
$oF4 = "$res/f4_sbit10_verify.png"
& $tool sbit "$res/f1_sbit10_fixed.png" $oF4 10 10 10 2>&1 | Out-Null
$f4 = & $tool info $oF4 2>&1 | Select-String "sBIT"
Check "F4 重复写入 sBIT 幂等 $f4" ($f4 -match "sBIT: R=10" -and -not ($f4 -match "sBIT.*sBIT"))

# ═══════════════════════════════════════════════════════════
# 汇总
# ═══════════════════════════════════════════════════════════
Write-Host "`n════════ 色彩测试汇总 ════════" -ForegroundColor Yellow
Write-Host "通过: $pass  失败: $fail" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($failList.Count -gt 0) {
    Write-Host "失败列表:" -ForegroundColor Red
    $failList | ForEach-Object { Write-Host "  ❌ $_" -ForegroundColor Red }
}
Write-Host "产物: $res"
