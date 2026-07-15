$ffmpeg = "C:\PLAN\ffmpegPictureUI\tools\test_color\bin\Release\net10.0\PLAN\ffmpeg-full\ffmpeg.exe"
$ffprobe = "C:\PLAN\ffmpegPictureUI\tools\test_color\bin\Release\net10.0\PLAN\ffmpeg-full\ffprobe.exe"
$d = "$env:TEMP\pipe2"
mkdir -Force $d | Out-Null
$total=0; $pass=0; $fail=0

Write-Host "╔══════════════════════════════════════════════════════╗" -F Cyan
Write-Host "║  全面管线实机测试 v2                                  ║" -F Cyan
Write-Host "╚══════════════════════════════════════════════════════╝" -F Cyan

# ═══ Source images ═══
Write-Host "`n═══ 创建测试源 ═══" -F Cyan
$src = @{}
$src.srgb8  = "$d\s_srgb8.png"
$src.srgb16 = "$d\s_srgb16.png"
$src.hdr10  = "$d\s_hdr10.png"
$src.bt709  = "$d\s_bt709.png"

$null=& $ffmpeg -y -f lavfi -i "color=c=red:size=64x64:duration=1" -frames:v 1 -update 1 -pix_fmt rgb24 $src.srgb8 2>&1
$null=& $ffmpeg -y -f lavfi -i "color=c=blue:size=64x64:duration=1" -frames:v 1 -update 1 -pix_fmt rgb48le $src.srgb16 2>&1
$null=& $ffmpeg -y -f lavfi -i "color=c=green:size=64x64:duration=1" -frames:v 1 -update 1 -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -pix_fmt gbrp10le $src.hdr10 2>&1
$null=& $ffmpeg -y -f lavfi -i "color=c=yellow:size=64x64:duration=1" -frames:v 1 -update 1 -color_primaries bt709 -color_trc bt709 -pix_fmt rgb24 $src.bt709 2>&1
Write-Host "  srgb8:$((Get-Item $src.srgb8).Length)B srgb16:$((Get-Item $src.srgb16).Length)B hdr10:$((Get-Item $src.hdr10).Length)B bt709:$((Get-Item $src.bt709).Length)B"

# ═══ Test function ═══
Function Test($Name, $FfmpegParams, $Ext) {
    $script:total++
    $out = "$d\$Name.$Ext"
    $cmd = "& `"$ffmpeg`" -y $FfmpegParams `"$out`""
    $ec=0; $sz=0
    try { Invoke-Expression $cmd 2>&1 | Out-Null; $ec=$LASTEXITCODE; if($null -eq $ec){$ec=0}; $sz=if(Test-Path $out){(Get-Item $out).Length}else{0} } catch { $ec=-1 }
    if ($ec -eq 0 -and $sz -gt 50) {
        $meta = & $ffprobe -v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer,color_space -of default=noprint_wrappers=1 $out 2>&1
        $prim="?"; $trc="?"; $csp="?"
        if($meta){ $meta -split "`n" | ForEach-Object { if($_ -match 'color_primaries=(.+)'){$prim=$Matches[1]} if($_ -match 'color_transfer=(.+)'){$trc=$Matches[1]} if($_ -match 'color_space=(.+)'){$csp=$Matches[1]} } }
        Write-Host "    OK $Name $([math]::Round($sz/1024,1))KB p=$prim t=$trc s=$csp" -F Green
        $script:pass++
    } else {
        Write-Host "    FAIL $Name exit=$ec size=$sz" -F Red
        $script:fail++
    }
}

$s1=$src.srgb8; $s2=$src.srgb16; $s3=$src.hdr10
# ═══ Phase 1: Core pipeline (srgb8 → all formats × all color spaces) ═══
Write-Host "`n═══ P1: 核心管线 (sRGB8→全格式×全色彩) ═══" -F Cyan

$fmts = @(
    @{n="jpg";  a="-i $s1 -q:v 5"; e="jpg"}
    @{n="png";  a="-i $s1 -compression_level 0 -update 1"; e="png"}
    @{n="webp"; a="-i $s1 -q:v 90"; e="webp"}
    @{n="avif"; a="-i $s1 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1"; e="avif"}
    @{n="jxl";  a="-i $s1 -c:v libjxl -distance 1.0 -effort 3"; e="jxl"}
    @{n="tiff"; a="-i $s1 -compression_algo raw"; e="tiff"}
)

$css = @(
    @{n="auto";     c=""}
    @{n="srgb";     c="-color_primaries bt709 -color_trc iec61966-2-1 -colorspace bt709"}
    @{n="bt709";    c="-color_primaries bt709 -color_trc bt709 -colorspace bt709"}
    @{n="bt2020pq"; c="-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc"}
    @{n="bt2020hlg";c="-color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc"}
)

foreach($f in $fmts){ foreach($c in $css){ Test "p1_$($f.n)_$($c.n)" "$($c.c) $($f.a)" $f.e } }

# ═══ P2: HDR pipeline ═══
Write-Host "`n═══ P2: HDR管线 (10-bit PQ→AVIF/JXL/PNG) ═══" -F Cyan
Test "p2_hdr_avif_a" "-i $s3 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1" "avif"
Test "p2_hdr_avif_pq" "-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -i $s3 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1" "avif"
Test "p2_hdr_jxl_a" "-i $s3 -c:v libjxl -distance 1.0 -effort 3" "jxl"
Test "p2_hdr_jxl_pq" "-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -i $s3 -c:v libjxl -distance 1.0 -effort 3" "jxl"
Test "p2_hdr_png_a" "-i $s3 -compression_level 0 -update 1" "png"

# ═══ P3: ICC modes (srgb→avif/jxl/png) ═══
Write-Host "`n═══ P3: ICC模式测试 ═══" -F Cyan
# Mode 1: None (strip metadata, CICP only)
Test "p3_none_avif" "-i $s1 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1 -map_metadata -1" "avif"
Test "p3_none_jxl" "-i $s1 -c:v libjxl -distance 1.0 -effort 3 -map_metadata -1" "jxl"
# Mode 2: CarryIcc (iccgen)
Test "p3_carry_avif" "-color_primaries bt709 -color_trc iec61966-2-1 -i $s1 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1 -vf iccgen -colorspace bt709 -map_metadata 0" "avif"
Test "p3_carry_jxl" "-color_primaries bt709 -color_trc iec61966-2-1 -i $s1 -c:v libjxl -distance 1.0 -effort 3 -vf iccgen -map_metadata 0" "jxl"
# Mode 3: BakeToStandard (zscale + iccgen)
Test "p3_bake_avif" "-i $s1 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1 -vf zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=bt709:t=iec61966-2-1:m=bt709,iccgen -colorspace bt709" "avif"
Test "p3_bake_jxl" "-i $s1 -c:v libjxl -distance 1.0 -effort 3 -vf zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=bt709:t=iec61966-2-1:m=bt709,iccgen" "jxl"
# Mode 4: BakeOnly (zscale, no ICC, strip metadata)
Test "p3_bakeonly_avif" "-i $s1 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1 -vf zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=bt709:t=iec61966-2-1:m=bt709 -map_metadata -1" "avif"

# ═══ P4: Bit depth edge cases ═══
Write-Host "`n═══ P4: 位深边界测试 ═══" -F Cyan
Test "p4_16to8_jpg" "-i $s2 -q:v 5" "jpg"
Test "p4_16to10_avif" "-i $s2 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1" "avif"
Test "p4_hlg_avif" "-color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc -i $s1 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1" "avif"

# ═══ P5: sRGB vs BT.709 trc distinction ═══
Write-Host "`n═══ P5: sRGB vs BT.709 传输函数验证 ═══" -F Cyan
Test "p5_srgb_avif" "-color_primaries bt709 -color_trc iec61966-2-1 -colorspace bt709 -i $s1 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1" "avif"
Test "p5_bt709_avif" "-color_primaries bt709 -color_trc bt709 -colorspace bt709 -i $s1 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1" "avif"
Test "p5_srgb_jxl" "-color_primaries bt709 -color_trc iec61966-2-1 -i $s1 -c:v libjxl -distance 1.0 -effort 3" "jxl"
Test "p5_bt709_jxl" "-color_primaries bt709 -color_trc bt709 -i $s1 -c:v libjxl -distance 1.0 -effort 3" "jxl"

# ═══ P6: Zscale baking (HDR↔SDR) ═══
Write-Host "`n═══ P6: Zscale烘焙测试 ═══" -F Cyan
Test "p6_srgb2pq" "-i $s1 -vf zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=bt2020:t=smpte2084:m=bt2020nc,format=gbrp10le -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -pix_fmt rgb48le -compression_level 0 -update 1" "png"
Test "p6_pq2srgb" "-i $s3 -vf zscale=pin=bt2020:tin=smpte2084:min=bt2020nc:p=bt709:t=iec61966-2-1:m=bt709 -color_primaries bt709 -color_trc iec61966-2-1 -colorspace bt709 -pix_fmt rgb24 -compression_level 0 -update 1" "png"

# ═══ P7: CICP always-on verification ═══
Write-Host "`n═══ P7: CICP始终启用验证 ═══" -F Cyan
Test "p7_cicp_avif_none" "-color_primaries bt709 -color_trc iec61966-2-1 -i $s1 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1 -map_metadata -1" "avif"
Test "p7_cicp_jxl_none" "-color_primaries bt709 -color_trc iec61966-2-1 -i $s1 -c:v libjxl -distance 1.0 -effort 3 -map_metadata -1" "jxl"

# ═══ P8: Conflict scenarios ═══
Write-Host "`n═══ P8: 冲突场景 ═══" -F Cyan
Test "p8_hdr_jpg" "-color_primaries bt2020 -color_trc smpte2084 -i $s1 -q:v 5 -colorspace bt2020nc" "jpg"
Test "p8_double_cvt" "-color_primaries bt709 -color_trc bt709 -i $s1 -c:v libaom-av1 -crf 30 -pix_fmt yuv420p10le -update 1 -vf zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=bt709:t=bt709:m=bt709 -colorspace bt709" "avif"

# ═══ Summary ═══
Write-Host "`n╔══════════════════════════════════════════════════════╗" -F Cyan
Write-Host "║  OK=$pass FAIL=$fail (共$total)                              ║" -F Cyan
Write-Host "║  Output: $d                                           ║" -F Cyan
Write-Host "╚══════════════════════════════════════════════════════╝" -F Cyan
