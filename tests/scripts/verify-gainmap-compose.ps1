# ═══════════════════════════════════════════════════════════
#  verify-gainmap-compose.ps1 — 第 80 条（2026-10-24 接线）
#  被测：**两图 GainMap 合成**（SDR 底图 + HDR 参考 → Ultra HDR JPEG / AVIF tmap）。
#
#  为什么单独一条：本模式的两个输入轴（角色判定 / 尺寸一致）与"自造底图"的旧出口
#  完全不同 —— 旧出口从 HDR 单图造 Reinhard 底图，坏也只会"底图不好看"；本模式若判错角色，
#  会把 SDR 图当 HDR 解（整图错色）或把 HDR 图当底图（增益全负），且**用户无从察觉**。
#
#  判据分四层（每层都要有牙，不靠编码器自报）：
#    ① 夹具自检：两张图**真的**是 256×192、HDR 那份**真的**标着 PQ/BT.2020；
#       若夹具生成器静默降级（如 ffmpeg 忽略 -color_trc），这里先红 —— 否则后面全在自我欺骗。
#    ② 容器结构：JPEG 的 XMP hdrgm（Channels / 容量 / 量程 / 二阶图尺寸）、MPF 索引，
#       并用 `_readback-gainmap-compose.py` **独立走出**次级图偏移（SOI/EOI/文件尾），
#       不采信 exiftool 的 "NumberOfImages" 一家之言。
#       ⚠ 已知读数（不是缺陷）：exiftool 13.59 对我们这类"MPF 追加在 EOI 之后"的次级图
#         会打 `[minor] Error reading GainMap image/jpeg from trailer`；同文件 MPEntry 指向的
#         字节确实是完整 JPEG（ffprobe 解得出、SOI/EOI/尾部对得上）。本闸按**结构**判，不按该警告判。
#    ③ 语义解耦：`HDRCapacityMax`（显示容量 = peak/203）与 `GainMapMax`（实测量化上沿）
#       必须是**两个独立读数** —— 固定 peak=1000 时容量恒为 2.30，而量程随内容变。
#       若两者又绑回同一个数，"暗底图高光被钳顶"的旧缺陷会原样复活。
#    ④ 负控：尺寸不一致 / 角色放反 / 输出 png —— 三条都必须**点名拒绝且零产物**。
#       提示语按"是否含关键判据词"判，不判整句（文案可改，判据不随文案漂移）。
#    ⑤ 编码器显式选择：`--gainmap-encoder mjpeg|libsvtav1` 必须**日志点名**实际编码器；
#       跨族取值（JPEG + AV1 名字）与词表外取值两条负控必须在建队时就拒、零产物。
#
#  底图保真：主 JPEG 解回 rgb24 与 SDR 夹具比 PSNR（实测 28.97 dB @ 默认质量 ⇒ 阈值 26），
#  同时与一张**无关的同尺寸图**比（实测 4.16 dB ⇒ 阈值 <15）——后者证明这条尺子会分叉。
# ═══════════════════════════════════════════════════════════
$ErrorActionPreference = 'Continue'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$ffp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$ex  = "$root/publish/PLAN/exiftool/exiftool.exe"
$rd  = "$root/tests/scripts/_readback-gainmap-compose.py"
$app = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $app)) { $app = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$d = "$root/tests/output/results/gainmap-compose"
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $d | Out-Null
$tmp = "$d/_tmp"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
# 子进程（ffmpeg/dngtool/exiftool/产品 exe）临时件一律落产物区，别碰 %TEMP%
$env:TMP = $tmp; $env:TEMP = $tmp; $env:TMPDIR = $tmp

foreach ($p in @(@('ffmpeg', $ff), @('ffprobe', $ffp), @('exiftool', $ex),
                 @('读手', $rd), @('被测 exe（Release 与 Debug 均无）', $app))) {
    if (-not (Test-Path $p[1])) {
        Write-Output "FAIL 缺 $($p[0]): $($p[1])"
        Write-Output 'PASS=0 FAIL=1'
        exit 1
    }
}
$py = (Get-Command py -ErrorAction SilentlyContinue).Source
if (-not $py) {
    Write-Output 'FAIL 缺 python 启动器: py'
    Write-Output 'PASS=0 FAIL=1'
    exit 1
}

$script:pass = 0; $script:fail = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { $script:pass++; Write-Output "PASS $msg" }
    else     { $script:fail++; Write-Output "FAIL $msg" }
}
# Start-Process 才能拿到 WinExe 的退出码（`& exe` 在 PS 里对 WinExe 不等）
function Exec([string]$file, [string]$argStr) {
    $o = "$tmp/_o.txt"; $e = "$tmp/_e.txt"
    Remove-Item $o, $e -Force -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e
    $so = if (Test-Path $o) { [System.IO.File]::ReadAllText($o) } else { '' }
    $se = if (Test-Path $e) { [System.IO.File]::ReadAllText($e) } else { '' }
    return ,@([int]$p.ExitCode, ($so + "`n" + $se))
}
function Exif([string]$file) {
    return (Exec $ex "-a -G1 -s `"$file`"")[1]
}
# 从 exiftool 文本里取某个标签的**第一个**值（行形如 `[XMP-hdrgm]  GainMapMax : 1.76`）
function TagVal([string]$text, [string]$tag) {
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match "^\s*\[[^\]]+\]\s+$([regex]::Escape($tag))\s*:\s*(.*)$") { return $Matches[1].Trim() }
    }
    return ''
}
function TagAll([string]$text, [string]$tag) {
    $out = @()
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match "^\s*\[[^\]]+\]\s+$([regex]::Escape($tag))\s*:\s*(.*)$") { $out += $Matches[1].Trim() }
    }
    return $out
}
function Probe([string]$file) {
    return (Exec $ffp "-v error -select_streams v:0 -show_entries stream=width,height,color_primaries,color_transfer -of csv=p=0 `"$file`"")[1].Trim()
}
function RunCli([string]$argStr) { return Exec $app $argStr }

# ── ① 夹具（现场生成，链外零依赖）────────────────────────────────────────────
$sdr      = "$d/sdr.png"        # SDR 底图（无色彩块 ⇒ 按 sRGB 默认语义，本仓 SDR 槽接受）
$hdr      = "$d/hdr.png"        # HDR 参考（PQ + BT.2020，亮度抬到 SDR 之上）
$sdrSmall = "$d/sdr-small.png"  # 尺寸不一致负控
$sdrOther = "$d/sdr-other.png"  # 底图保真尺子的对照臂（同尺寸、内容无关）

$g1 = Exec $ff "-y -v error -f lavfi -i `"testsrc2=s=256x192:d=1`" -frames:v 1 -vf `"scale=out_color_matrix=bt709:out_range=full,format=rgb24`" -color_primaries bt709 -color_trc iec61966-2-1 -colorspace bt709 `"$sdr`""
$g2 = Exec $ff "-y -v error -f lavfi -i `"testsrc2=s=256x192:d=1`" -frames:v 1 -vf `"scale=out_color_matrix=bt709:out_range=full,format=rgb24,colorlevels=rimax=0.4:gimax=0.4:bimax=0.4,format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=bt2020:t=smpte2084:r=full,format=rgb48be`" -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc `"$hdr`""
$g3 = Exec $ff "-y -v error -f lavfi -i `"testsrc2=s=128x96:d=1`" -frames:v 1 -vf `"scale=out_color_matrix=bt709:out_range=full,format=rgb24`" `"$sdrSmall`""
$g4 = Exec $ff "-y -v error -f lavfi -i `"smptebars=s=256x192:d=1`" -frames:v 1 -vf `"format=rgb24`" `"$sdrOther`""
CK ($g1[0] -eq 0 -and $g2[0] -eq 0 -and $g3[0] -eq 0 -and $g4[0] -eq 0) `
    "夹具生成：四份全成功（rc=$($g1[0])/$($g2[0])/$($g3[0])/$($g4[0])）"

$pSdr = Probe $sdr; $pHdr = Probe $hdr
CK ($pSdr -match '^256,192,') "夹具成立：SDR 为 256×192（实测 $pSdr）"
CK ($pHdr -match '^256,192,' -and $pHdr -match 'smpte2084' -and $pHdr -match 'bt2020') `
    "夹具成立：HDR 为 256×192 且带 PQ/BT.2020（实测 $pHdr）"
CK ((Probe $sdrSmall) -match '^128,96,') "负控夹具成立：小图为 128×96（实测 $(Probe $sdrSmall)）"
CK (-not ($pSdr -match 'smpte2084')) "夹具成立：SDR 那份**不带** HDR 传递（实测 $pSdr）"

# ── ② JPEG 三通道主臂（peak=1000 ⇒ 容量固定 2.30，量程随内容）────────────────
$jDir = "$d/jpg-mc"
$r = RunCli "--headless --compose-gainmap -i `"$sdr,$hdr`" -o `"$jDir`" -f jpg --gainmap-peak 1000 --gainmap-metadata both"
CK ($r[0] -eq 0) "JPEG 三通道：CLI 成功（rc=$($r[0])）"
$jOut = "$jDir/sdr_gainmap.jpg"
$jLen = if (Test-Path $jOut) { (Get-Item $jOut).Length } else { 0 }
CK ($jLen -gt 0) "JPEG 三通道：产物在位（$jLen B）"

$jx = Exif $jOut
CK ((TagVal $jx 'MapPrimary') -eq '256x192') "JPEG 三通道：MapPrimary=256x192（实测 $(TagVal $jx 'MapPrimary')）"
CK ((TagVal $jx 'MapSecondary') -eq '128x96') "JPEG 三通道：MapSecondary=128x96（降采样 1/2 生效，实测 $(TagVal $jx 'MapSecondary')）"
CK ((TagVal $jx 'Channels') -eq '3') "JPEG 三通道：XMP Channels=3（实测 $(TagVal $jx 'Channels')）"
CK ((TagVal $jx 'HDRCapacityMax') -eq '2.30') `
    "JPEG 三通道：HDRCapacityMax=2.30 = log2(1000/203)（实测 $(TagVal $jx 'HDRCapacityMax')）"
$jGainMax = TagVal $jx 'GainMapMax'
CK ($jGainMax -ne '' -and $jGainMax -ne (TagVal $jx 'HDRCapacityMax')) `
    "JPEG 三通道：GainMapMax 与容量**解耦**（量程 $jGainMax vs 容量 $(TagVal $jx 'HDRCapacityMax')）"
CK ((TagVal $jx 'GainMapMin') -match '^-[0-9]') `
    "JPEG 三通道：GainMapMin 为负（底图暗于 HDR，实测 $(TagVal $jx 'GainMapMin')）"

$mpf = (Exec $py "`"$rd`" mpf `"$jOut`"")
CK ($mpf[0] -eq 0) "JPEG 三通道：MPF 结构自洽（独立读手 rc=$($mpf[0])）"
CK ($mpf[1] -match 'MPFImages=2' -and $mpf[1] -match 'SecondarySOI=1' -and
    $mpf[1] -match 'SecondaryEOI=1' -and $mpf[1] -match 'SecondaryAtTail=1') `
    "JPEG 三通道：次级图是**可定位的完整 JPEG**（SOI/EOI/文件尾）"

# 底图保真：主 JPEG 解回 rgb24 vs SDR 夹具；对照臂 = 无关同尺寸图
$dec = Exec $ff "-y -v error -i `"$jOut`" -frames:v 1 -pix_fmt rgb24 -f rawvideo `"$d/base-dec.rgb`""
$ref = Exec $ff "-y -v error -i `"$sdr`" -frames:v 1 -pix_fmt rgb24 -f rawvideo `"$d/base-ref.rgb`""
$oth = Exec $ff "-y -v error -i `"$sdrOther`" -frames:v 1 -pix_fmt rgb24 -f rawvideo `"$d/base-other.rgb`""
CK ($dec[0] -eq 0 -and $ref[0] -eq 0 -and $oth[0] -eq 0) "底图保真：三份 rgb24 解码成功"
$ps = (Exec $py "`"$rd`" psnr `"$d/base-ref.rgb`" `"$d/base-dec.rgb`"")
$pc = (Exec $py "`"$rd`" psnr `"$d/base-other.rgb`" `"$d/base-dec.rgb`"")
$psDb = if ($ps[1] -match 'PSNR=([0-9.]+)') { [double]$Matches[1] } else { 0 }
$pcDb = if ($pc[1] -match 'PSNR=([0-9.]+)') { [double]$Matches[1] } else { 99 }
CK ($psDb -ge 26.0) "底图保真 ≥26 dB（实测 $psDb dB）"
CK ($pcDb -lt 15.0) "底图保真尺子会分叉：无关图仅 $pcDb dB（<15）"

# ── ③ JPEG 单通道臂（开关必须真的改通道数，不是死旋钮）──────────────────────
$mDir = "$d/jpg-mono"
$r = RunCli "--headless --compose-gainmap -i `"$sdr,$hdr`" -o `"$mDir`" -f jpg --gainmap-peak 1000 --gainmap-multichannel false --gainmap-metadata both"
$mOut = "$mDir/sdr_gainmap.jpg"
CK ($r[0] -eq 0 -and (Test-Path $mOut)) "JPEG 单通道：CLI 成功且产物在位（rc=$($r[0])）"
$mx = Exif $mOut
CK ((TagVal $mx 'Channels') -eq '1') "JPEG 单通道：XMP Channels=1（实测 $(TagVal $mx 'Channels')）"
CK ((TagVal $mx 'HDRCapacityMax') -eq '2.30') "JPEG 单通道：容量仍为 2.30（实测 $(TagVal $mx 'HDRCapacityMax')）"

# ── ④ AVIF tmap 臂 ───────────────────────────────────────────────────────────
$aDir = "$d/avif-mc"
$r = RunCli "--headless --compose-gainmap -i `"$sdr,$hdr`" -o `"$aDir`" -f avif --gainmap-peak 1000"
CK ($r[0] -eq 0) "AVIF：CLI 成功（rc=$($r[0])）"
$aOut = "$aDir/sdr_gainmap.avif"
$aLen = if (Test-Path $aOut) { (Get-Item $aOut).Length } else { 0 }
CK ($aLen -gt 0) "AVIF：产物在位（$aLen B）"
$ax = Exif $aOut
CK ((TagVal $ax 'CompatibleBrands') -match 'tmap') "AVIF：CompatibleBrands 含 tmap（实测 $(TagVal $ax 'CompatibleBrands')）"
$ext = @(TagAll $ax 'ImageSpatialExtent')
CK ($ext -contains '256x192' -and $ext -contains '128x96') `
    "AVIF：主图与增益图两个 extent 都在（实测 $($ext -join ' / ')）"

# ── ⑤ 峰值 auto 臂（默认路径不崩、容量仍写成 peak/203）──────────────────────
$oDir = "$d/jpg-auto"
$r = RunCli "--headless --log-level debug --compose-gainmap -i `"$sdr,$hdr`" -o `"$oDir`" -f jpg --gainmap-metadata both"
$oOut = "$oDir/sdr_gainmap.jpg"
$oLen = if (Test-Path $oOut) { (Get-Item $oOut).Length } else { 0 }
CK ($r[0] -eq 0 -and $oLen -gt 0) "峰值 auto：CLI 成功且产物在位（rc=$($r[0]), $oLen B）"
CK ($r[1] -match '内容实测') "峰值 auto：日志点名「内容实测」（不是静默回退 1000）"
# 容量必须与前一步实测的那个峰值同源：capacity == log2(N/203)（N≤203 ⇒ 0.00）
$peakN = if ($r[1] -match '峰值：(\d+) nits（内容实测）') { [int]$Matches[1] } else { -1 }
$wantCap = if ($peakN -le 203) { 0.0 } else { [math]::Round([math]::Log($peakN / 203.0, 2), 2) }
$oCap = TagVal (Exif $oOut) 'HDRCapacityMax'
$oCapNum = if ($oCap -match '^-?[0-9.]+$') { [double]$oCap } else { -99 }
CK ($peakN -gt 0 -and [math]::Abs($oCapNum - $wantCap) -le 0.011) `
    "峰值 auto：容量与实测峰值同源（实测 ${peakN} nits ⇒ 期望 $wantCap，XMP $oCap）"

# ── ⑥ 负控：三种「看起来能跑、其实不该跑」的输入 ────────────────────────────
function AssertRejected([string]$tag, [string]$argStr, [string]$keyword, [int]$wantRc, [string]$outDir) {
    $res = RunCli $argStr
    $art = @()
    if (Test-Path $outDir) {
        $art = @(Get-ChildItem $outDir -Recurse -File -ErrorAction SilentlyContinue |
                 Where-Object { $_.Name -match '_gainmap\.' })
    }
    CK ($res[0] -eq $wantRc) "$tag：退出码 $wantRc（实测 $($res[0])）"
    CK ($art.Count -eq 0) "$tag：零产物（实测 $($art.Count) 个 *_gainmap.*）"
    CK ($res[1] -match [regex]::Escape($keyword)) "$tag：拒绝原因点名「$keyword」"
}

$n1 = "$d/neg-size"
AssertRejected '尺寸不一致' "--headless --compose-gainmap -i `"$sdrSmall,$hdr`" -o `"$n1`" -f jpg" '尺寸不一致' 1 $n1
$n2 = "$d/neg-role"
AssertRejected '角色放反' "--headless --compose-gainmap -i `"$hdr,$sdr`" -o `"$n2`" -f jpg" 'HDR 传递' 1 $n2
$n3 = "$d/neg-png"
AssertRejected '输出 png' "--headless --compose-gainmap -i `"$sdr,$hdr`" -o `"$n3`" -f png" '只支持 -f jpg / jpeg / avif' 2 $n3

# ── ⑦ 编码器显式选择（面板「大类/小类」落到引擎的那一个词）──────────────────
#   默认路径会按 cjpegli 可用性自动选，**不可复现**；这里钉死显式值，要求：
#   ① 日志点名「真的用了这个编码器」（引擎自报，不看产物猜）；
#   ② 产物结构仍成立（mjpeg = JPEG 主臂；libsvtav1 = AVIF tmap 臂）；
#   ③ 跨族取值（JPEG + AV1 编码器）建队即拒、零产物 —— 不许"先跑再失败"。
$eDir = "$d/enc-mjpeg"
$r = RunCli "--headless --log-level debug --compose-gainmap -i `"$sdr,$hdr`" -o `"$eDir`" -f jpg --gainmap-peak 1000 --gainmap-encoder mjpeg"
$eOut = "$eDir/sdr_gainmap.jpg"
CK ($r[0] -eq 0 -and (Test-Path $eOut)) "编码器 mjpeg：CLI 成功且产物在位（rc=$($r[0])）"
CK ($r[1] -match 'JPEG encoder: ffmpeg mjpeg') "编码器 mjpeg：日志点名实际编码器（不是静默回落）"
# 本臂不传 --gainmap-metadata ⇒ 走默认 ISO-only。MapPrimary 是 XMP 字段，这一刻读不到是**正确**的；
# 「MPF 仍成立」改由独立读手走偏移证明（不依赖承载形态）。
$empf = (Exec $py "`"$rd`" mpf `"$eOut`"")
CK ($empf[0] -eq 0 -and $empf[1] -match 'MPFImages=2' -and $empf[1] -match 'SecondaryAtTail=1') `
    "编码器 mjpeg（默认 ISO-only）：MPF 仍能独立定位次级图（rc=$($empf[0])）"

$vDir = "$d/enc-svtav1"
$r = RunCli "--headless --log-level debug --compose-gainmap -i `"$sdr,$hdr`" -o `"$vDir`" -f avif --gainmap-peak 1000 --gainmap-encoder libsvtav1"
$vOut = "$vDir/sdr_gainmap.avif"
CK ($r[0] -eq 0 -and (Test-Path $vOut)) "编码器 libsvtav1：CLI 成功且产物在位（rc=$($r[0])）"
CK ($r[1] -match 'AVIF encoder: libsvtav1') "编码器 libsvtav1：日志点名实际编码器（不是静默回落）"
CK ((TagVal (Exif $vOut) 'CompatibleBrands') -match 'tmap') `
    "编码器 libsvtav1：仍是 AVIF tmap（实测 $(TagVal (Exif $vOut) 'CompatibleBrands')）"

$n4 = "$d/neg-encoder-pair"
AssertRejected '编码器跨族' "--headless --compose-gainmap -i `"$sdr,$hdr`" -o `"$n4`" -f jpg --gainmap-encoder libaom-av1" '不匹配' 2 $n4
$n5 = "$d/neg-encoder-unknown"
AssertRejected '编码器词表外' "--headless --compose-gainmap -i `"$sdr,$hdr`" -o `"$n5`" -f jpg --gainmap-encoder bogus" '--gainmap-encoder 取值无效' 2 $n5
$n6 = "$d/neg-encoder-nocompose"
AssertRejected '编码器缺合成模式' "--headless -i `"$sdr`" -o `"$n6`" -f jpg --gainmap-encoder mjpeg" '只在 --compose-gainmap 下有意义' 2 $n6

# ── ⑧ 元数据承载三态（JPEG）+ AVIF CRF 生效 ─────────────────────────────
#   判据读**字节**，不读编码器自报：
#     · ISO 21496-1 APP2 的唯一命名空间串 `urn:iso:std:iso:ts:21496:-1`；
#     · XMP hdrgm 的 `hdrgm:` 前缀。
#   三态各少一样 / 多一样都判红 —— 这正是「选了却没兑现」与「没选却硬塞」的两种形态。
#   MPF 在三种承载下都必须仍能独立走出次级图（它是「第二张图在哪」的唯一索引）。
function HasAscii([string]$path, [string]$needle) {
    if (-not (Test-Path $path)) { return $false }
    $s = [System.Text.Encoding]::Latin1.GetString([System.IO.File]::ReadAllBytes($path))
    return $s.Contains($needle)
}

foreach ($carrier in @('iso', 'xmp', 'both')) {
    $cDir = "$d/meta-$carrier"
    $r = RunCli "--headless --log-level debug --compose-gainmap -i `"$sdr,$hdr`" -o `"$cDir`" -f jpg --gainmap-peak 1000 --gainmap-encoder mjpeg --gainmap-metadata $carrier"
    $cOut = "$cDir/sdr_gainmap.jpg"
    CK ($r[0] -eq 0 -and (Test-Path $cOut)) "元数据 $carrier：CLI 成功且产物在位（rc=$($r[0])）"
    CK ($r[1] -match "gain-map metadata carrier: $carrier") "元数据 $carrier：日志点名承载方式（不是静默默认）"
    $hasIso = HasAscii $cOut 'urn:iso:std:iso:ts:21496:-1'
    $hasXmp = HasAscii $cOut 'hdrgm:'
    $wantIso = $carrier -ne 'xmp'
    $wantXmp = $carrier -ne 'iso'
    CK ($hasIso -eq $wantIso) "元数据 $carrier：ISO APP2 命中=$hasIso（期望 $wantIso）"
    CK ($hasXmp -eq $wantXmp) "元数据 $carrier：XMP hdrgm 命中=$hasXmp（期望 $wantXmp）"
    $cmpf = (Exec $py "`"$rd`" mpf `"$cOut`"")
    CK ($cmpf[0] -eq 0 -and $cmpf[1] -match 'MPFImages=2' -and $cmpf[1] -match 'SecondaryAtTail=1') `
        "元数据 $carrier：MPF 仍能独立定位次级图（rc=$($cmpf[0])）"
}
# XMP-only 仍须让 XMP 读手读出容量：否则「写了 hdrgm 串」不等于「可被消费」
$cXmp = "$d/meta-xmp/sdr_gainmap.jpg"
CK ((TagVal (Exif $cXmp) 'HDRCapacityMax') -eq '2.30') `
    "元数据 xmp：exiftool 能读出 HDRCapacityMax=2.30（实测 $(TagVal (Exif $cXmp) 'HDRCapacityMax')）"

# 默认（不传 --gainmap-metadata）必须是 ISO-only —— 2026-10-25 用户指定的默认
$dDef = "$d/meta-default"
$r = RunCli "--headless --log-level debug --compose-gainmap -i `"$sdr,$hdr`" -o `"$dDef`" -f jpg --gainmap-peak 1000 --gainmap-encoder mjpeg"
$dOut = "$dDef/sdr_gainmap.jpg"
CK ($r[0] -eq 0 -and (Test-Path $dOut)) "元数据默认：CLI 成功且产物在位（rc=$($r[0])）"
CK ($r[1] -match 'gain-map metadata carrier: iso') "元数据默认：日志点名 iso（不是 both）"
CK ((HasAscii $dOut 'urn:iso:std:iso:ts:21496:-1')) "元数据默认：ISO APP2 在"
CK (-not (HasAscii $dOut 'hdrgm:')) "元数据默认：XMP hdrgm 不在（默认是 ISO-only）"
$dmpf = (Exec $py "`"$rd`" mpf `"$dOut`"")
CK ($dmpf[0] -eq 0 -and $dmpf[1] -match 'MPFImages=2') "元数据默认：MPF 仍能走出次级图（rc=$($dmpf[0])）"

# AVIF：质量滑块必须真的改 CRF（同一对输入，只动 -q）
$qHi = "$d/crf-high"; $qLo = "$d/crf-low"
$rHi = RunCli "--headless --log-level debug --compose-gainmap -i `"$sdr,$hdr`" -o `"$qHi`" -f avif --gainmap-peak 1000 --gainmap-encoder libaom-av1 -q 99"
$rLo = RunCli "--headless --log-level debug --compose-gainmap -i `"$sdr,$hdr`" -o `"$qLo`" -f avif --gainmap-peak 1000 --gainmap-encoder libaom-av1 -q 30"
$oHi = "$qHi/sdr_gainmap.avif"; $oLo = "$qLo/sdr_gainmap.avif"
CK ($rHi[0] -eq 0 -and $rLo[0] -eq 0 -and (Test-Path $oHi) -and (Test-Path $oLo)) `
    "AVIF 质量：两臂都成功（rc=$($rHi[0])/$($rLo[0])）"
CK ($rHi[1] -match 'CRF 1,' -and $rLo[1] -match 'CRF 44,') `
    "AVIF 质量：-q 99 ⇒ CRF 1、-q 30 ⇒ CRF 44（实测 $(($rHi[1] | Select-String 'CRF \d+' -AllMatches).Matches.Value -join '|') / $(($rLo[1] | Select-String 'CRF \d+' -AllMatches).Matches.Value -join '|')）"
$lenHi = if (Test-Path $oHi) { (Get-Item $oHi).Length } else { 0 }
$lenLo = if (Test-Path $oLo) { (Get-Item $oLo).Length } else { 0 }
CK ($lenLo -lt $lenHi) "AVIF 质量：低质量臂更小（$lenLo B < $lenHi B）"

$n7 = "$d/neg-meta-avif-xmp"
AssertRejected 'AVIF + XMP 元数据' "--headless --compose-gainmap -i `"$sdr,$hdr`" -o `"$n7`" -f avif --gainmap-metadata xmp" '没有 XMP 形态' 2 $n7
$n8 = "$d/neg-meta-unknown"
AssertRejected '元数据词表外' "--headless --compose-gainmap -i `"$sdr,$hdr`" -o `"$n8`" -f jpg --gainmap-metadata bogus" '--gainmap-metadata 取值无效' 2 $n8
$n9 = "$d/neg-meta-nocompose"
AssertRejected '元数据缺合成模式' "--headless -i `"$sdr`" -o `"$n9`" -f jpg --gainmap-metadata iso" '只在 --compose-gainmap 下有意义' 2 $n9

Write-Output "PASS=$script:pass FAIL=$script:fail"
if ($script:fail -gt 0) { exit 1 }
exit 0
