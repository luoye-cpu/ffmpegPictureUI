# ═══════════════════════════════════════════════════════════
#  verify-jpg-webp-matrix.ps1 — 第 84 条（2026-10-25 接线）
#  被测：JPG / WebP 出口的 **YUV 转换矩阵** 必须与容器/解码器的假设一致（BT.601）。
#
#  缺陷（本门禁要锁住的东西）：
#    `-colorspace bt709` 对 mjpeg/libwebp **不只是标签**：swscale 真按 BT.709 转 Y′CbCr，
#    但 JPEG/JFIF 没有矩阵字段、WebP 没有 CICP（且 libwebp 丢 iccgen 的 ICC），标准解码器
#    （libjpeg / libwebp / Windows / Chrome）固定按 BT.601 解 ⇒ 饱和色偏。
#    真件实测（2560×1600 HDR 源 → 默认档）：纯红色块 255,0,0 解回 232,0,1(JPG)/234,0,1(WebP)；
#    整图 PSNR 30.34/30.78 dB。改 bt470bg 后回到 254/255，PSNR 33.35/34.33 dB。
#    修复：legacy 的 capM 与引擎的转换矩阵统一取 `ColorSpaceRegistry.ContainerYuvMatrix`。
#
#  判据分组：
#    ① 夹具自备（8-bit RGB / 16-bit RGBA 三色块）+ 自检（块心颜色确实是 253/254）
#    ② legacy 四格（8/16-bit × jpg/webp）：命令写 `-colorspace bt470bg`、不写 bt709；产物像素回原色
#    ③ 引擎两格（`--color-space "Display P3"` 触发真色彩变换）：编码行必须写 bt470bg
#       （引擎只在不改像素的直通计划下不接管编码 ⇒ 必须造一个真变换的格子，否则测的是 legacy）
#    ④ 负控：同素材直接 `-colorspace bt709` 编码 ⇒ 红块必须明显偏离（≥10 级）⇒ 证明②有牙
#    ⑤ 回归：16-bit → AVIF 色块仍正确；显式 Display P3 → JPG 的实际产物包含 P3 ICC
#    ⑥ 引擎范围：JPEG pc/tv 的原生 Y 码值兑现；WebP VP8 的 pc 请求明确归 tv，产物与 tv 相同。
#    ⑦ PNG opaque ICC：无目标时 ICC 和 RGB 样本逐字节携带；显式 sRGB 走 CMS，以独立数值作判据。
#    ⑧ 坏 --icc-file：即使输入自带好 ICC 也必须 exit 91，不落图像产物。
#
#  退出码：0 = 全绿，1 = 有失败。结尾必须写完整 if/else。
#  ⚠ 双宿主兼容：禁三元/`??`/`?.`/`&&`/`||`；取子进程输出一律 Start-Process + 重定向。
# ═══════════════════════════════════════════════════════════
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$env:FFMPEGGUI_PLAN_DIR = "$root/publish/PLAN"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$et = "$root/publish/PLAN/exiftool/exiftool.exe"

$work = Join-Path $root ("tests\output\jpgwebp_matrix_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$roundTmp = Join-Path $root 'tests/output/_tmp/round-tmp'
New-Item -ItemType Directory -Force -Path $roundTmp | Out-Null
$env:TMP = $roundTmp; $env:TEMP = $roundTmp; $env:TMPDIR = $roundTmp

foreach ($need in @(@('被测 exe', $exe), @('ffmpeg', $ff), @('ffprobe', $fp), @('exiftool', $et))) {
  if (-not (Test-Path $need[1])) {
    Write-Output ("FAIL 缺 {0}: {1}" -f $need[0], $need[1])
    Write-Output 'PASS=0 FAIL=1'
    exit 1
  }
}

$script:pass = 0; $script:fail = 0
function CK([bool]$ok, [string]$msg) {
  if ($ok) { $script:pass++; Write-Output "PASS $msg" }
  else     { $script:fail++; Write-Output "FAIL $msg" }
}
function Exec([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work\_$tag.out"; $e = "$work\_$tag.err"
  Remove-Item $o, $e -Force -ErrorAction SilentlyContinue
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  $so = ''; $se = ''
  if (Test-Path $o) { $so = [System.IO.File]::ReadAllText($o) }
  if (Test-Path $e) { $se = [System.IO.File]::ReadAllText($e) }
  return @{ code = $p.ExitCode; text = ($so + "`n" + $se) }
}
function FirstProduct([string]$dir, [string]$ext) {
  if (-not (Test-Path $dir)) { return $null }
  return Get-ChildItem $dir -File -ErrorAction SilentlyContinue |
         Where-Object { $_.Extension -eq $ext } | Select-Object -First 1
}
# 解码到 rgb24（**ffmpeg 默认解码口径 = BT.601**，对 JPEG/WebP 就是标准解码器的假设）
function Pixels([string]$img, [string]$name) {
  $raw = "$work\_$name.rgb"
  $r = Exec $ff ('-v error -i "' + $img + '" -frames:v 1 -f rawvideo -pix_fmt rgb24 "' + $raw + '"') ('dec_' + $name)
  if ($r.code -ne 0 -or -not (Test-Path -LiteralPath $raw)) { return $null }
  $b = [System.IO.File]::ReadAllBytes($raw)
  if ($b.Length -ne (64 * 48 * 3)) { return $null }
  $red = @([int]$b[(32 * 48 + 8) * 3], [int]$b[(32 * 48 + 8) * 3 + 1], [int]$b[(32 * 48 + 8) * 3 + 2])
  $grn = @([int]$b[(32 * 48 + 24) * 3], [int]$b[(32 * 48 + 24) * 3 + 1], [int]$b[(32 * 48 + 24) * 3 + 2])
  $blu = @([int]$b[(32 * 48 + 40) * 3], [int]$b[(32 * 48 + 40) * 3 + 1], [int]$b[(32 * 48 + 40) * 3 + 2])
  return @{ red = $red; green = $grn; blue = $blu }
}
function DominantOk([int[]]$v, [int]$dom) {
  $others = @()
  for ($i = 0; $i -lt 3; $i++) { if ($i -ne $dom) { $others += $v[$i] } }
  if ($v[$dom] -ge 246 -and $others[0] -le 8 -and $others[1] -le 8) { return $true }
  return $false
}
function LogText([string]$log) {
  if (-not (Test-Path $log)) { return '' }
  return [System.IO.File]::ReadAllText($log)
}
function CmdLine([string]$log) {
  $t = LogText $log
  foreach ($line in ($t -split "`r?`n")) {
    if ($line.StartsWith('[cmd] ffmpeg')) { return $line }
  }
  return ''
}
function EngineCmdLine([string]$log) {
  $t = LogText $log
  foreach ($line in ($t -split "`r?`n")) {
    if ($line -match 'ffmpeg' -and $line -match '\-video_size') { return $line }
  }
  return ''
}
function RunApp([string]$in, [string]$dir, [string]$fmt, [string]$extra) {
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $log = "$dir\log.txt"
  $a = '--headless -i "' + $in + '" -o "' + $dir + '" -f ' + $fmt + ' -q 100 --log-file "' + $log + '" --log-level Debug ' + $extra
  $r = Exec $exe $a ('app_' + [guid]::NewGuid().ToString('N'))
  return @{ code = $r.code; log = $log }
}

# ── ① 夹具：纯色块（红/绿/蓝各 16px 宽）+ 自检 ──
$sw8 = "$work\sw8.png"; $sw16 = "$work\sw16.png"
$graph = '-f lavfi -i color=red:s=16x64:r=1 -f lavfi -i color=lime:s=16x64:r=1 -f lavfi -i color=blue:s=16x64:r=1 -filter_complex "[0][1][2]hstack=3" -frames:v 1 -y'
$mk8 = Exec $ff ($graph + ' -pix_fmt rgb24 "' + $sw8 + '"') 'mk8'
$mk16 = Exec $ff ($graph + ' -pix_fmt rgba64be "' + $sw16 + '"') 'mk16'
CK (($mk8.code -eq 0) -and (Test-Path $sw8)) '① 8-bit RGB 三色块夹具生成'
CK (($mk16.code -eq 0) -and (Test-Path $sw16)) '① 16-bit RGBA 三色块夹具生成'
$p8 = Pixels $sw8 'sw8'
CK ($null -ne $p8 -and (DominantOk $p8.red 0) -and (DominantOk $p8.green 1) -and (DominantOk $p8.blue 2)) `
   '① 夹具自检：8-bit 三块解回红/绿/蓝（证明取样点与解码口径正确）'
$p16 = Pixels $sw16 'sw16'
CK ($null -ne $p16 -and (DominantOk $p16.red 0) -and (DominantOk $p16.green 1) -and (DominantOk $p16.blue 2)) `
   '① 夹具自检：16-bit 三块解回红/绿/蓝'

# ── ② legacy 四格：命令口径 + 像素口径 ──
$legacyCases = @(
  @('jpg8',  'jpg',  $sw8),
  @('webp8', 'webp', $sw8),
  @('jpg16', 'jpg',  $sw16),
  @('webp16','webp', $sw16)
)
foreach ($c in $legacyCases) {
  $tag = $c[0]; $fmt = $c[1]; $src = $c[2]
  $dir = "$work\legacy_$tag"
  $r = RunApp $src $dir $fmt ''
  $ext = '.' + $fmt
  $prod = FirstProduct $dir $ext
  CK (($r.code -eq 0) -and ($null -ne $prod)) ("② {0}: 转换成功且有产物（rc={1}）" -f $tag, $r.code)
  $cmd = CmdLine $r.log
  # 输入声明（`-i` 之前的 -colorspace rgb/…）在前，输出侧在最后 ⇒ 判据取**最后一个**
  $seen = @()
  foreach ($m in [regex]::Matches($cmd, '\-colorspace\s+(\S+)')) { $seen += $m.Groups[1].Value }
  $last = 'none'
  if ($seen.Count -gt 0) { $last = $seen[$seen.Count - 1] }
  CK ($last -eq 'bt470bg') ("② {0}: 输出侧矩阵 = bt470bg（命令内全量：{1}）" -f $tag, ($seen -join ','))
  CK (-not ($cmd -match '\-colorspace bt709')) ("② {0}: 命令不再写 bt709" -f $tag)
  if ($null -ne $prod) {
    $px = Pixels $prod.FullName ("px_$tag")
    CK ($null -ne $px -and (DominantOk $px.red 0) -and (DominantOk $px.green 1) -and (DominantOk $px.blue 2)) `
       ("② {0}: 标准解码器解回三原色（红={1} 绿={2} 蓝={3}）" -f $tag, `
        ($px.red -join ','), ($px.green -join ','), ($px.blue -join ','))
  }
}

# ── ③ 引擎两格：必须造一个**真色彩变换**的计划（P3 目标），否则引擎不接管编码 ──
$engineCases = @(@('eng_jpg', 'jpg'), @('eng_webp', 'webp'))
foreach ($c in $engineCases) {
  $tag = $c[0]; $fmt = $c[1]
  $dir = "$work\$tag"
  $r = RunApp $sw16 $dir $fmt '--color-space "Display P3"'
  $ext = '.' + $fmt
  $prod = FirstProduct $dir $ext
  CK (($r.code -eq 0) -and ($null -ne $prod)) ("③ {0}: 引擎路径转换成功（rc={1}）" -f $tag, $r.code)
  $cmd = EngineCmdLine $r.log
  CK ($cmd -match '\-colorspace bt470bg') ("③ {0}: 引擎编码行写 bt470bg（找到引擎行={1}）" -f $tag, ($cmd.Length -gt 0))
  CK (-not ($cmd -match '\-colorspace bt709')) ("③ {0}: 引擎编码行不写 bt709" -f $tag)
}

# ── ④ 负控：同素材直接按 bt709 编码 ⇒ 红块必须明显偏离（证明尺子有牙） ──
$neg = "$work\neg_bt709.jpg"
$rn = Exec $ff ('-y -i "' + $sw8 + '" -c:v mjpeg -q:v 2 -pix_fmt yuvj420p -colorspace bt709 "' + $neg + '"') 'neg'
$pn = Pixels $neg 'neg'
CK (($rn.code -eq 0) -and ($null -ne $pn) -and ($pn.red[0] -le 240)) `
   ("④ 负控：bt709 编码的红块明显偏离（红={0}，期望 ≤240）⇒ ②的判据不是恒真" -f $(if ($null -ne $pn) { $pn.red -join ',' } else { 'n/a' }))

# ── ⑤ 回归：同一张 16-bit 图 → AVIF 仍正确（nclx 能携带矩阵） ──
$dirA = "$work\avif16"
$ra = RunApp $sw16 $dirA 'avif' ''
$prodA = FirstProduct $dirA '.avif'
CK (($ra.code -eq 0) -and ($null -ne $prodA)) ("⑤ AVIF: 转换成功（rc={0}）" -f $ra.code)
if ($null -ne $prodA) {
  $pva = Pixels $prodA.FullName 'avif'
  CK ($null -ne $pva -and (DominantOk $pva.red 0) -and (DominantOk $pva.green 1) -and (DominantOk $pva.blue 2)) `
     ("⑤ AVIF: 三原色正确（红={0}）⇒ 本修复未波及能携带矩阵的容器" -f ($pva.red -join ','))
}
# 未标注的默认 JPG 不承诺生成 ICC；③ 的显式 Display P3 目标不能由 JPEG 容器命名，须嵌入真实 ICC。
$jpgP3 = FirstProduct "$work\eng_jpg" '.jpg'
$ri = if ($null -ne $jpgP3) { Exec $et ('-G1 -s -ProfileDescription -ColorSpaceData "' + $jpgP3.FullName + '"') 'icc_p3_jpg' } else { $null }
CK ($null -ne $ri -and $ri.code -eq 0 -and $ri.text -match '\[ICC_Profile\]\s+ProfileDescription\s*:\s*(?:Display P3\b|smpte432/sRGB\b)' -and
    $ri.text -match '\[ICC-header\]\s+ColorSpaceData\s*:\s*RGB\b') `
   '⑤ 显式 Display P3 的 JPG 实际嵌有 RGB Display P3 ICC（不以命令行标记代替读回）'

# ── ⑥ 引擎范围：同一 RGB 黑白片，直接读原生 Y 平面；JPEG 容器不标范围，不能只看 ffprobe 标签 ──
$bw = "$work\black_white.png"
$bwGraph = '-f lavfi -i color=black:s=32x64:r=1,format=rgb24 -f lavfi -i color=white:s=32x64:r=1,format=rgb24 -filter_complex "[0][1]hstack" -frames:v 1 -y'
$mkBw = Exec $ff ($bwGraph + ' -pix_fmt rgb24 "' + $bw + '"') 'mk_bw'
CK ($mkBw.code -eq 0 -and (Test-Path -LiteralPath $bw)) '⑥ RGB 黑白范围夹具生成'
function NativeLuma([string]$img, [string]$tag) {
  $rawY = "$work\_$tag.y"
  $r = Exec $ff ('-v error -i "' + $img + '" -map 0:v:0 -frames:v 1 -vf extractplanes=y -pix_fmt gray -f rawvideo "' + $rawY + '"') ('y_' + $tag)
  if ($r.code -ne 0 -or -not (Test-Path -LiteralPath $rawY)) { return $null }
  $y = [System.IO.File]::ReadAllBytes($rawY)
  if ($y.Length -ne (64 * 64)) { return $null }
  return @{ black = [int]$y[(32 * 64 + 16)]; white = [int]$y[(32 * 64 + 48)] }
}
foreach ($fmt in @('jpg', 'webp')) {
  $arms = @{}
  foreach ($range in @('pc', 'tv')) {
    $tag = "range_${fmt}_$range"; $dir = "$work\$tag"
    $r = RunApp $bw $dir $fmt ('--color-engine engine --color-space "Display P3" --chroma 4:2:0 --color-range ' + $range)
    $prod = FirstProduct $dir ('.' + $fmt)
    $y = if ($null -ne $prod) { NativeLuma $prod.FullName $tag } else { $null }
    $arms[$range] = @{ product = $prod; log = (LogText $r.log) }
    $expected = if ($fmt -eq 'jpg' -and $range -eq 'pc') { @(0, 255) } else { @(16, 235) }
    CK ($r.code -eq 0 -and $null -ne $y -and [Math]::Abs($y.black - $expected[0]) -le 3 -and [Math]::Abs($y.white - $expected[1]) -le 3) `
       ("⑥ {0}/{1}: 原生 Y 黑白码值={2}/{3}，期望 {4}/{5} ±3（rc={6}）" -f $fmt, $range, $y.black, $y.white, $expected[0], $expected[1], $r.code)
    CK ((EngineCmdLine $r.log) -match '(?:^|\s)-colorspace bt470bg(?:\s|$)') ("⑥ {0}/{1}: 真引擎编码且仍用 BT.601" -f $fmt, $range)
  }
  if ($fmt -eq 'jpg') {
    CK ($arms.tv.log -match '\[jpeg\].*--color-range tv.*-strict unofficial') '⑥ JPEG tv 明示 JFIF 非标准有限范围风险'
  } else {
    $pcFile = $arms.pc.product; $tvFile = $arms.tv.product
    CK ($null -ne $pcFile -and $null -ne $tvFile -and (Get-FileHash -LiteralPath $pcFile.FullName).Hash -eq (Get-FileHash -LiteralPath $tvFile.FullName).Hash) `
       '⑥ WebP pc/tv 产物逐字节相同（同为 VP8 有限范围）'
    CK ($arms.pc.log -match '\[webp\].*--color-range pc ignored.*using tv') '⑥ WebP pc 明示未兑现、归一为 tv'
  }
}

# ── ⑦ 合成 opaque ICC：RGB scanner v2.1，只有 A2B0/A2B1（mft2），没有矩阵/TRC 兜底 ──
# 2×2×2 CLUT：以 sRGB D50 矩阵的列顺序 G、B、R 把 (R,G,B)→(G,B,R) 当作线性 RGB 再写 XYZ PCS。
# 用固定字节而非依赖 gitignored 的 jpegli/sKCms 样本；ICC 内容与下方独立 oracle 同时可复现。
$lutIcc = "$work\opaque_a2b.icc"
$lut64 = @'
AAACXAAAAAACEAAAc2NuclJHQiBYWVogB+oAAQABAAAAAAAAYWNzcE1TRlQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAPbWAAEAAAAA0y0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFZGVzYwAAAMAAAABtY3BydAAAATAAAAAfd3RwdAAAAVAAAAAUQTJCMAAAAWQAAAB8QTJCMQAAAeAAAAB8ZGVzYwAAAAAAAAATVGVzdCBSR0IgQTJCIGN5Y2xlAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHRleHQAAAAAU3ludGhldGljIHRlc3QgcHJvZmlsZQAAWFlaIAAAAAAAAPbWAAEAAAAA0y1tZnQyAAAAAAMDAgAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAgACAAD//wAA//8AAP//AAAAAAAAMUpbwwxuN9EcewHJaRt4Pg42ElAHwltqQ5pjhWfYSiIkPV0ze2yAAGmgAAD//wAA//8AAP//bWZ0MgAAAAADAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAAAAAAADFKW8MMbjfRHHsByWkbeD4ONhJQB8JbakOaY4Vn2EoiJD1dM3tsgABpoAAA//8AAP//AAD//w==
'@
[System.IO.File]::WriteAllBytes($lutIcc, [Convert]::FromBase64String($lut64))
# 直接给三块非饱和 RGB8 样本，避免 YUV 转换改变 oracle 输入。
$swatch = @(40,112,190, 176,44,94, 72,184,36)
$swatchRaw = [byte[]]::new(48 * 64 * 3)
for ($i = 0; $i -lt $swatchRaw.Length; $i++) { $swatchRaw[$i] = $swatch[3 * [int][Math]::Floor(($i % 144) / 48) + ($i % 3)] }
$lutRgb = "$work\opaque_a2b.rgb"; $lutPng = "$work\opaque_a2b.png"
[System.IO.File]::WriteAllBytes($lutRgb, $swatchRaw)
$mkLut = Exec $ff ('-v error -f rawvideo -pix_fmt rgb24 -video_size 48x64 -i "' + $lutRgb + '" -frames:v 1 -pix_fmt rgb24 "' + $lutPng + '"') 'mk_lut_png'
if ($mkLut.code -eq 0) {
  # 副作用：只写 ICC；判据用下方独立二进制读回。
  $null = Exec $et ('-overwrite_original "-icc_profile<=' + $lutIcc + '" "' + $lutPng + '"') 'embed_lut'
}
# ExifTool 把原始 ICC 写到新文件（不让 Exec 将二进制 stdout 当文本），再做字节哈希。
function ReadIccHash([string]$img, [string]$tag) {
  $icc = "$work\_$tag.icc"
  $r = Exec $et ('-b -ICC_Profile -W "' + $icc + '" "' + $img + '"') ('extract_' + $tag)
  if ($r.code -ne 0) {
    $script:fail++; Write-Host "FAIL ICC reader failed for $tag (rc=$($r.code)); this is not proof of ICC absence"
    return ''
  }
  if (Test-Path -LiteralPath $icc) {
    if ((Get-Item -LiteralPath $icc).Length -lt 132) {
      $script:fail++; Write-Host "FAIL $tag has a short/invalid extracted ICC; it is not an untagged artifact"
      return ''
    }
    return (Get-FileHash -LiteralPath $icc -Algorithm SHA256).Hash
  }
  return ''
}
$lutHash = '93BB813BCE0C8724B7741A5AF3F75534919F4724E966EB5D19445E4AB89E3A7E'
$srcHash = if ($mkLut.code -eq 0 -and (Test-Path -LiteralPath $lutPng)) { ReadIccHash $lutPng 'lut_source' } else { '' }
CK ($srcHash -eq $lutHash) '⑦ 合成 PNG 内嵌的 A2B0/A2B1 ICC 字节与固定夹具一致'
if ($srcHash -eq $lutHash) {
  $pngExtra = '--color-engine engine --png-optimizer off'
  $carryDir = "$work\icc_carry"
  $carry = RunApp $lutPng $carryDir 'png' ($pngExtra + ' --color-strategy carry')
  $carryPng = FirstProduct $carryDir '.png'
  $carryHash = if ($null -ne $carryPng) { ReadIccHash $carryPng.FullName 'lut_carry' } else { '' }
  CK ($carry.code -eq 0 -and $carryHash -eq $srcHash) ("⑦ PNG→PNG carry: 产物内 ICC SHA-256 与源相同（rc={0}, SHA-256={1}）" -f $carry.code, $carryHash)
  $carryPx = if ($null -ne $carryPng) { Pixels $carryPng.FullName 'lut_carry' } else { $null }
  CK ($null -ne $carryPx -and (Get-FileHash -LiteralPath "$work\_lut_carry.rgb").Hash -eq (Get-FileHash -LiteralPath $lutRgb).Hash) `
     '⑦ carry 的全部 RGB 样本逐字节不变（包含不限于块心）'

  $cmsDir = "$work\icc_cms"
  $cms = RunApp $lutPng $cmsDir 'png' ($pngExtra + ' --color-strategy recommended --color-space sRGB')
  $cmsPng = FirstProduct $cmsDir '.png'
  $cmsHash = if ($null -ne $cmsPng) { ReadIccHash $cmsPng.FullName 'lut_cms' } else { '' }
  CK ($cms.code -eq 0 -and $cmsHash -ne '' -and $cmsHash -ne $srcHash -and (LogText $cms.log) -match 'CMS target profile written') `
     ("⑦ 显式 sRGB: Windows CMS 成功，PNG 写入目标 ICC 而非续贴源 ICC（rc={0}）" -f $cms.code)
  # 独立 oracle：Pillow 12.3/ImageCms 相对色度，把上面固定 ICC 的 3 个 RGB8 样本转到 createProfile('sRGB')。
  # 固定结果，不以被测 Windows CMS 的读数作参考；±3 留给 CMM 插值/16→8 量化差异。
  $want = @(177,224,110, 115,163,217, 221,105,145)
  $cmsPx = if ($null -ne $cmsPng) { Pixels $cmsPng.FullName 'lut_cms' } else { $null }
  $got = @(); $maxErr = 256
  if ($null -ne $cmsPx) {
    $got = $cmsPx.red + $cmsPx.green + $cmsPx.blue
    $maxErr = 0
    for ($i = 0; $i -lt $want.Length; $i++) { $maxErr = [Math]::Max($maxErr, [Math]::Abs($got[$i] - $want[$i])) }
  }
  CK ($cms.code -eq 0 -and $maxErr -le 3) ("⑦ CMS 像素对撞独立 Pillow oracle（RGB={0}，期望={1}，最大误差={2} ≤3）" -f ($got -join ','), ($want -join ','), $maxErr)

  # ── ⑧ 明确坏源 ICC：截断到合法头但保留原声明长度，不能偷偷改用 PNG 内的好 ICC ──
  $badIcc = "$work\invalid_a2b.icc"
  [System.IO.File]::WriteAllBytes($badIcc, [byte[]]([System.IO.File]::ReadAllBytes($lutIcc)[0..131]))
  $badDir = "$work\icc_invalid"
  $bad = RunApp $lutPng $badDir 'png' ($pngExtra + ' --log-format JsonLines --icc-file "' + $badIcc + '"')
  $badProducts = @(Get-ChildItem -LiteralPath $badDir -Recurse -Force -File | Where-Object { $_.FullName -ne $bad.log })
  $events = @((LogText $bad.log) -split "`r?`n" | Where-Object { $_.Trim() } | ForEach-Object { ConvertFrom-Json $_ })
  $done = @($events | Where-Object c -eq 'item_completed')
  CK ($bad.code -eq 1 -and $done.Count -eq 1 -and $done[0].code -eq 91 -and $badProducts.Count -eq 0 -and (LogText $bad.log) -match '\[color-contract\] ICC_EXPLICIT_INVALID:') `
     ("⑧ 坏 ICC 明确拒绝（进程码={0}，任务码={1}，非日志文件={2}）" -f $bad.code, $done[0].code, $badProducts.Count)
}

# PNG fixture writer: alter only ancillary chunks; IDAT remains identical.
function PngChunk([string]$kind, [byte[]]$data) {
  $len = [BitConverter]::GetBytes([uint32]$data.Length); [Array]::Reverse($len)
  $payload = [byte[]]([Text.Encoding]::ASCII.GetBytes($kind) + $data)
  $crc = [uint32]::MaxValue
  foreach ($b in $payload) {
    $crc = $crc -bxor $b
    for ($i = 0; $i -lt 8; $i++) {
      $mask = if (($crc -band 1) -eq 1) { [uint32]3988292384 } else { [uint32]0 }
      $crc = ($crc -shr 1) -bxor $mask
    }
  }
  $sum = [BitConverter]::GetBytes([uint32]($crc -bxor [uint32]::MaxValue)); [Array]::Reverse($sum)
  return ,([byte[]]($len + $payload + $sum))
}
function WithProfile([string]$path, [byte[]]$icc, [byte[]]$cicp = @(12,13,0,1), [byte]$iccMethod = 0, [string]$plainPath = '') {
  if (-not $plainPath) { $plainPath = $plainSwatch }
  $plain = [IO.File]::ReadAllBytes($plainPath)
  $chunks = PngChunk 'cICP' $cicp
  if ($icc.Length -gt 0) {
    $m = [IO.MemoryStream]::new()
    $z = [IO.Compression.DeflateStream]::new($m, [IO.Compression.CompressionMode]::Compress, $true)
    $z.Write($icc, 0, $icc.Length); $z.Dispose()
    $a = 1; $b = 0
    foreach ($v in $icc) { $a = ($a + $v) % 65521; $b = ($b + $a) % 65521 }
    $check = [BitConverter]::GetBytes([uint32]($b * 65536L + $a)); [Array]::Reverse($check)
    $payload = [byte[]]([Text.Encoding]::ASCII.GetBytes('Source profile') + @(0,$iccMethod,0x78,0x9c) + $m.ToArray() + $check)
    $m.Dispose()
    $chunks += PngChunk 'iCCP' $payload
  }
  [IO.File]::WriteAllBytes($path, [byte[]]($plain[0..32] + $chunks + $plain[33..($plain.Length - 1)]))
}
function PngCicpBytes([string]$path) {
  $b = [IO.File]::ReadAllBytes($path)
  for ($i = 8; $i + 12 -le $b.Length;) {
    $len = $b[$i] * 16777216 + $b[$i+1] * 65536 + $b[$i+2] * 256 + $b[$i+3]
    if ($i + 12 + $len -gt $b.Length) { return '' }
    if ([Text.Encoding]::ASCII.GetString($b, $i+4, 4) -eq 'cICP' -and $len -eq 4) { return ($b[($i+8)..($i+11)] -join '/') }
    $i += 12 + $len
  }
  return ''
}
$plainSwatch = "$work\opaque_plain.png"
$plainMade = Exec $ff ('-v error -f rawvideo -pix_fmt rgb24 -video_size 48x64 -i "' + $lutRgb + '" -frames:v 1 "' + $plainSwatch + '"') 'mk_plain_png'
CK ($plainMade.code -eq 0 -and (Test-Path -LiteralPath $plainSwatch)) '⑨ RGB PNG 兼容性对照夹具生成'
if ($plainMade.code -eq 0 -and (Test-Path -LiteralPath $plainSwatch)) {
  $original = [IO.File]::ReadAllBytes($lutIcc)
  $badTag = [byte[]]$original.Clone(); $badTag[136] = 255 # first tag starts beyond the profile
  $reverseOnly = [byte[]]$original.Clone(); $reverseOnly[168] = 66; $reverseOnly[170] = 65; $reverseOnly[180] = 66; $reverseOnly[182] = 65
  foreach ($arm in @('reference','broken_tag','reverse_only','bad_iccp_wrapper')) {
    $profile = switch ($arm) { 'broken_tag' { $badTag }; 'reverse_only' { $reverseOnly }; 'bad_iccp_wrapper' { $original }; default { @() } }
    $in = "$work\compat_${arm}.png"
    $method = if ($arm -eq 'bad_iccp_wrapper') { 1 } else { 0 }
    WithProfile $in ([byte[]]@($profile)) @(12,13,0,1) $method
    $dir = "$work\compat_${arm}_out"
    $r = RunApp $in $dir 'png' '--color-engine engine --color-strategy carry --png-optimizer off'
    $prod = FirstProduct $dir '.png'
    $p = if ($prod) { Pixels $prod.FullName "compat_$arm" } else { $null }
    $same = $null -ne $p -and (Get-FileHash -LiteralPath "$work\_compat_$arm.rgb").Hash -eq (Get-FileHash -LiteralPath $lutRgb).Hash
    $c = if ($prod) { PngCicpBytes $prod.FullName } else { '' }
    CK ($r.code -eq 0 -and $same -and $c -eq '12/13/0/1') ("⑨ {0}: 成功输出、RGB 样本和真实 P3/sRGB 保持（rc={1}, cICP={2}）" -f $arm,$r.code,$c)
    if ($arm -ne 'reference') {
      $outHash = if ($prod) { ReadIccHash $prod.FullName "compat_$arm" } else { '' }
      $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([byte[]]$profile)).Replace('-','')
      CK ((LogText $r.log) -match 'ICC_SOURCE_IGNORED' -and $outHash -ne $hash) ("⑨ {0}: 输入 ICC 警告且不回贴废弃字节" -f $arm)
    }
  }
  $badCicp = "$work\bad_cicp.png"
  WithProfile $badCicp @() @(9,16,9,1) # CRC-valid, but PNG samples cannot be coded as BT.2020 YUV.
  $dir = "$work\bad_cicp_out"
  $r = RunApp $badCicp $dir 'png' '--color-engine engine --png-optimizer off'
  $prod = FirstProduct $dir '.png'
  $p = if ($prod) { Pixels $prod.FullName 'bad_cicp' } else { $null }
  CK ((PngCicpBytes $badCicp) -eq '9/16/9/1' -and $r.code -eq 0 -and $p -and
      (Get-FileHash -LiteralPath "$work\_bad_cicp.rgb").Hash -eq (Get-FileHash -LiteralPath $lutRgb).Hash -and
      (PngCicpBytes $prod.FullName) -eq '' -and (ReadIccHash $prod.FullName 'bad_cicp_out') -eq '' -and
      (LogText $r.log) -match 'CICP_SOURCE_IGNORED') '⑨ PNG 的无效 YUV cICP 只警告；像素不变，也不重新标成 HDR/BT.2020'
  $narrow = "$work\narrow_rgb.png"
  WithProfile $narrow @() @(12,13,0,0)
  $dir = "$work\narrow_rgb_out"
  $r = RunApp $narrow $dir 'png' '--color-engine engine --png-optimizer off'
  $prod = FirstProduct $dir '.png'
  $narrowPx = if ($prod) { Pixels $prod.FullName 'narrow_rgb' } else { $null }
  $got = if ($narrowPx) { $narrowPx.red + $narrowPx.green + $narrowPx.blue } else { @() }
  $want = @($swatch | ForEach-Object { [int][Math]::Floor(($_ - 16) * 255.0 / 219 + .5) })
  CK ($r.code -eq 0 -and $prod -and (PngCicpBytes $narrow) -eq '12/13/0/0' -and
      (PngCicpBytes $prod.FullName) -eq '12/13/0/1' -and ($got -join '/') -eq ($want -join '/') -and
      (LogText $r.log) -notmatch 'CICP_SOURCE_IGNORED') '⑨ 合法窄范围 RGB PNG 不是坏标签：按 16–235 扩展到满范围，再写 RGB/full 标签'

  # 预缩放支路必须在缩放/中转前消费窄范围；否则后续文件变成 PC 标签、像素仍是 limited code values。
  $scaleRaw = "$work\narrow_scale.rgb"; $scalePng = "$work\narrow_scale_source.png"
  $scaleSource = [byte[]]::new(96 * 72 * 3)
  for ($y = 0; $y -lt 72; $y++) {
    for ($x = 0; $x -lt 96; $x++) {
      $k = ($y * 96 + $x) * 3
      if ($x -lt 24) { $scaleSource[$k]=235; $scaleSource[$k+1]=16; $scaleSource[$k+2]=16 }
      elseif ($x -lt 48) { $scaleSource[$k]=16; $scaleSource[$k+1]=235; $scaleSource[$k+2]=16 }
      else { $scaleSource[$k]=16; $scaleSource[$k+1]=16; $scaleSource[$k+2]=235 }
    }
  }
  [IO.File]::WriteAllBytes($scaleRaw, $scaleSource)
  $mkScale = Exec $ff ('-v error -f rawvideo -pix_fmt rgb24 -video_size 96x72 -i "' + $scaleRaw + '" -frames:v 1 -pix_fmt rgb24 "' + $scalePng + '"') 'mk_narrow_scale'
  CK ($mkScale.code -eq 0 -and (Test-Path -LiteralPath $scalePng)) '⑨a 96x72 RGB 色块窄范围预缩放夹具生成'
  if (Test-Path -LiteralPath $scalePng) {
    WithProfile $scalePng @() @(12,13,0,0) 0 $scalePng
    $scaledDir = "$work\narrow_scaled_out"
    $scaledRun = RunApp $scalePng $scaledDir 'png' '--color-engine legacy --png-optimizer off --max-dimension-enabled true --max-dimension 48'
    $scaledProduct = FirstProduct $scaledDir '.png'
    # 96x72 with longest edge=48 is 48x36; the ordinary 48x64 fixture reader does not apply here.
    $scaledRaw = "$work\_narrow_scaled.rgb"; $scaledGot = @(); $scaledDecodeOk = $false
    if ($scaledProduct) {
      $scaledDecode = Exec $ff ('-v error -i "' + $scaledProduct.FullName + '" -frames:v 1 -f rawvideo -pix_fmt rgb24 "' + $scaledRaw + '"') 'decode_narrow_scaled'
      if ($scaledDecode.code -eq 0 -and (Test-Path -LiteralPath $scaledRaw)) {
        $scaledBytes = [IO.File]::ReadAllBytes($scaledRaw)
        $scaledDecodeOk = $scaledBytes.Length -eq (48 * 36 * 3)
        if ($scaledDecodeOk) {
          foreach ($sampleX in @(6,18,36)) {
            $sampleAt = (18 * 48 + $sampleX) * 3
            $scaledGot += @([int]$scaledBytes[$sampleAt], [int]$scaledBytes[$sampleAt+1], [int]$scaledBytes[$sampleAt+2])
          }
        }
      }
    }
    $scaledWant = @(255,0,0, 0,255,0, 0,0,255)
    CK ($scaledRun.code -eq 0 -and $scaledProduct -and $scaledDecodeOk -and (PngCicpBytes $scaledProduct.FullName) -eq '12/13/0/1' -and
        ($scaledGot -join '/') -eq ($scaledWant -join '/') -and (LogText $scaledRun.log) -match '预缩放完成') `
        ("⑨b 窄范围 PNG 最长边预缩放先扩样本再标满范围（RGB=$($scaledGot -join '/')；期望=$($scaledWant -join '/')；cICP=$(if($scaledProduct){PngCicpBytes $scaledProduct.FullName}else{'<missing>'})）")
  } else { CK $false '⑨b 预缩放正控：源 PNG 缺失' }
  $degradedDir = "$work\icc_no_cms"
  $r = RunApp $lutPng $degradedDir 'jpg' '--color-engine legacy'
  $prod = FirstProduct $degradedDir '.jpg'
  CK ($r.code -eq 0 -and $null -ne $prod -and (LogText $r.log) -match 'ICC_SOURCE_UNDESCRIBED' -and (ReadIccHash $prod.FullName 'compat_no_cms') -eq '') '⑨ 合法旧 LUT 缺少本次 CMS 路线：自动转换不拒、不伪造 ICC'
}

# CM-11: an opaque source profile must never bypass an explicit output-matrix rejection.
# The source-warning policy remains separate: auto and BT.601 redundant may still transcode.
function RunContract([string]$inputPath,[string]$tag,[string]$fmt,[string]$extra) {
  $dir = Join-Path $work $tag
  New-Item -ItemType Directory -Path $dir | Out-Null
  $json = Join-Path $work ($tag + '.jsonl')
  $r = Exec $exe ('--headless -i "' + $inputPath + '" -o "' + $dir + '" -f ' + $fmt +
      ' -q 100 --png-optimizer off --log-level Debug --log-format JsonLines --log-file "' + $json + '" ' + $extra) ('contract_' + $tag)
  $events = @(); $ok = $false
  if (Test-Path -LiteralPath $json -PathType Leaf) {
    try {
      $lines = @([IO.File]::ReadAllLines($json) | Where-Object { $_.Trim() })
      $ok = $lines.Count -gt 0
      foreach ($line in $lines) {
        $event = ConvertFrom-Json -InputObject $line -ErrorAction Stop
        if ($null -eq $event -or -not ($event.c -is [string]) -or -not ($event.m -is [string])) { $ok = $false }
        $events += $event
      }
    } catch { $ok = $false }
  }
  $images = @(Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction SilentlyContinue |
      Where-Object { $_.Extension -match '^\.(png|jpg|jpeg|avif|jxl|webp|tiff)$' })
  return @{ Code=$r.code; JsonOk=$ok; Events=$events; Done=@($events | Where-Object { $_.c -ceq 'item_completed' });
      Images=$images; Text=(($events | ForEach-Object { $_.m }) -join "`n"); Dir=$dir }
}
function ContractOutcome($r,[int]$process,[int]$item,[string]$level,[int]$count) {
  return ($r.Code -eq $process -and $r.JsonOk -and $r.Done.Count -eq 1 -and
      ($r.Done[0].code -is [int] -or $r.Done[0].code -is [long]) -and $r.Done[0].code -eq $item -and
      $r.Done[0].l -ceq $level -and $r.Done[0].p -eq 1 -and $r.Done[0].tot -eq 1 -and
      $r.Images.Count -eq $count -and @($r.Images | Where-Object { $_.Length -le 0 }).Count -eq 0)
}
if ($srcHash -eq $lutHash -and (Test-Path -LiteralPath $plainSwatch -PathType Leaf)) {
  $normalReject = RunContract $plainSwatch 'cm11_plain_reject' 'jpg' '--color-out-matrix bt709'
  $opaqueReject = RunContract $lutPng 'cm11_opaque_reject' 'jpg' '--color-out-matrix bt709'
  foreach ($arm in @(@{Name='plain';Run=$normalReject},@{Name='opaque';Run=$opaqueReject})) {
    $r = $arm.Run
    $rejectedLines = @($r.Text -split "`r?`n" | Where-Object { $_ -match '^\[color-out-matrix\] bt709 is not carried by jpg \(.*\) => contract rejection, exit 91, no output$' })
    CK ((ContractOutcome $r 1 91 'error' 0) -and $rejectedLines.Count -eq 1 -and
        $r.Text -notmatch '\[color-contract\] ICC_SOURCE_UNDESCRIBED:|降级（SourceIccUnverified）|\[color-out-matrix\] jpg: bt709 ->') `
        ("CM11 {0}: unsupported output M is process 1 / one task 91 / zero new images, never landed" -f $arm.Name)
  }
  $opaqueAuto = RunContract $lutPng 'cm11_opaque_auto' 'jpg' ''
  $opaque601 = RunContract $lutPng 'cm11_opaque_redundant' 'jpg' '--color-out-matrix bt470bg'
  foreach ($arm in @(@{Name='auto';Run=$opaqueAuto},@{Name='601';Run=$opaque601})) {
    CK ((ContractOutcome $arm.Run 0 0 'info' 1) -and $arm.Run.Text -match '\bICC_SOURCE_UNDESCRIBED\b') `
        ("CM11 {0}: opaque source fallback remains usable and keeps its source warning" -f $arm.Name)
  }
  $autoRgb = if ($opaqueAuto.Images.Count -eq 1) { Pixels $opaqueAuto.Images[0].FullName 'cm11_auto' } else { $null }
  $redundantRgb = if ($opaque601.Images.Count -eq 1) { Pixels $opaque601.Images[0].FullName 'cm11_601' } else { $null }
  CK ($autoRgb -and $redundantRgb -and
      (Get-FileHash -LiteralPath "$work\_cm11_auto.rgb").Hash -ceq (Get-FileHash -LiteralPath "$work\_cm11_601.rgb").Hash) `
      'CM11 redundant BT.601 preserves every decoded RGB sample relative to opaque auto'
  $matrixLines = @($opaque601.Text -split "`r?`n" | Where-Object { $_ -match '\[color-out-matrix\] ' })
  CK ($matrixLines.Count -eq 1 -and $matrixLines[0] -match 'recorded, no pixel effect' -and
      $opaqueAuto.Text -notmatch '\[color-out-matrix\] ') 'CM11 redundant has exactly one truthful matrix result; auto has zero explicit-matrix noise'
  if ($opaque601.Images.Count -eq 1) {
    $native = Exec $fp ('-v error -select_streams v:0 -show_entries stream=color_space -of default=nw=1:nk=1 "' + $opaque601.Images[0].FullName + '"') 'cm11_native_matrix'
    CK ($native.code -eq 0 -and $native.text.Trim() -ceq 'bt470bg') 'CM11 JPEG representation is still the decoder-defined BT.601 matrix'
  } else { CK $false 'CM11 JPEG representation cannot be probed without its one expected product' }
  # Carried M describes only output sample storage; it cannot turn an unknown LUT source into a known RGB space.
  $opaqueAvif = RunContract $lutPng 'cm11_opaque_carried' 'avif' '--encoder Ffmpeg --color-out-matrix bt709 --bit-depth 8 --chroma 4:4:4'
  CK ((ContractOutcome $opaqueAvif 0 0 'info' 1) -and $opaqueAvif.Text -match '\bICC_SOURCE_UNDESCRIBED\b' -and
      $opaqueAvif.Text -notmatch 'Windows CMS|iccgen=|zscale=|\[avif\] avifenc') `
      'CM11 carried AVIF: real FFmpeg route succeeds without inventing a source-space transform'
  if ($opaqueAvif.Images.Count -eq 1) {
    $avifTags = Exec $fp ('-v error -select_streams v:0 -show_entries stream=codec_name,pix_fmt,color_primaries,color_transfer,color_space -of json "' + $opaqueAvif.Images[0].FullName + '"') 'cm11_carried_tags'
    $avifFacts = $null
    if ($avifTags.code -eq 0) { try { $avifFacts = ConvertFrom-Json $avifTags.text -ErrorAction Stop } catch { } }
    $stream = @($avifFacts.streams)[0]
    CK ($avifTags.code -eq 0 -and $stream.codec_name -ceq 'av1' -and $stream.pix_fmt -ceq 'yuv444p' -and
        $stream.color_space -ceq 'bt709' -and $stream.color_primaries -in @($null,'unknown','unspecified') -and
        $stream.color_transfer -in @($null,'unknown','unspecified')) `
        'CM11 carried AVIF: actual YUV444/BT.709 storage, P/T stay unspecified; encoding M does not claim source P/T'
  } else { CK $false 'CM11 carried AVIF has no unique new artifact to probe' }
} else { CK $false 'CM11 requires independently verified opaque ICC and plain source fixtures' }

# XYB owns a LUT profile: PreserveAll must not overwrite it with the PNG's matrix/TRC ICC.
$xybSource = "$work\xyb_p3_source.png"
$p3ProfileHash = if ($jpgP3) { ReadIccHash $jpgP3.FullName 'xyb_source_profile' } else { '' }
if ($p3ProfileHash -and (Test-Path -LiteralPath $plainSwatch)) {
  Copy-Item -LiteralPath $plainSwatch -Destination $xybSource
  $xybEmbed = Exec $et ('-overwrite_original "-icc_profile<=' + "$work\_xyb_source_profile.icc" + '" "' + $xybSource + '"') 'make_xyb_source'
  $xybRun = RunApp $xybSource "$work\xyb_p3_out" 'jpg' '-e cjpegli --color-out-matrix xyb'
  $xybProduct = FirstProduct "$work\xyb_p3_out" '.jpg'
  $xybHash = if ($xybProduct) { ReadIccHash $xybProduct.FullName 'xyb_output_profile' } else { '' }
  $hasLut = $false; $xybV44 = $false; $xybIdOk = $false
  if ($xybHash) {
    $profileBytes = [IO.File]::ReadAllBytes("$work\_xyb_output_profile.icc")
    $xybV44 = ($profileBytes[8..11] -join '/') -eq '4/64/0/0'
    $idInput = [byte[]]$profileBytes.Clone()
    [Array]::Clear($idInput,44,4); [Array]::Clear($idInput,64,4); [Array]::Clear($idInput,84,16)
    $md5 = [Security.Cryptography.MD5]::Create()
    try { $xybIdOk = [BitConverter]::ToString($md5.ComputeHash($idInput)) -eq [BitConverter]::ToString($profileBytes,84,16) }
    finally { $md5.Dispose() }
    $count = [long]$profileBytes[128]*16777216 + [long]$profileBytes[129]*65536 + [long]$profileBytes[130]*256 + $profileBytes[131]
    if ($count -gt 0 -and $count -le 4096 -and 132+12*$count -le $profileBytes.Length) {
      for ($i=0; $i -lt $count; $i++) {
        if ([Text.Encoding]::ASCII.GetString($profileBytes,132+12*$i,4) -ceq 'A2B0') { $hasLut = $true }
      }
    }
  }
  CK ($xybEmbed.code -eq 0 -and $xybRun.code -eq 0 -and $xybProduct -and $xybHash -and $xybHash -ne $p3ProfileHash -and $hasLut -and $xybV44 -and $xybIdOk) `
    '⑩ P3 PNG→XYB JPEG preserves the encoder-owned ICC v4.4 A2B LUT with a valid Profile ID, never the source RGB matrix profile'
} else { CK $false '⑩ XYB source profile fixture unavailable' }

Write-Output ("work={0}" -f $work)
Write-Output ("PASS={0} FAIL={1}" -f $script:pass, $script:fail)
if ($script:fail -eq 0) { exit 0 } else { exit 1 }
