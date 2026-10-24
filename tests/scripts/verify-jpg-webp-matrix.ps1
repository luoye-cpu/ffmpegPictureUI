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
#    ⑤ 回归：同一张 16-bit 图 → AVIF 仍正确（nclx 能携带矩阵，不受本修复影响）
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
$env:TMP = $work; $env:TEMP = $work; $env:TMPDIR = $work

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
  $null = Exec $ff ('-v error -i "' + $img + '" -frames:v 1 -f rawvideo -pix_fmt rgb24 "' + $raw + '"') ('dec_' + $name)
  if (-not (Test-Path $raw)) { return $null }
  $b = [System.IO.File]::ReadAllBytes($raw)
  if ($b.Length -lt (64 * 48 * 3)) { return $null }
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
# 颜色描述不能被这次改动丢掉：legacy JPG 仍须带 ICC
$jpg8 = FirstProduct "$work\legacy_jpg8" '.jpg'
if ($null -ne $jpg8) {
  $ri = Exec $et ('-icc_profile:all "' + $jpg8.FullName + '"') 'icc'
  CK ($ri.text -match 'Profile Description') '⑤ 修复后 JPG 仍携带 ICC（矩阵修正没有拿掉色彩描述）'
}

Write-Output ("work={0}" -f $work)
Write-Output ("PASS={0} FAIL={1}" -f $script:pass, $script:fail)
if ($script:fail -eq 0) { exit 0 } else { exit 1 }
