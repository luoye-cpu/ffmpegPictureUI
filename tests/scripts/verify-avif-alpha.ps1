# ═══════════════════════════════════════════════════════════
#  verify-avif-alpha.ps1 — 第 83 条（2026-10-25 接线）
#  被测：JXR（含 alpha）→ AVIF 的**透明通道交付**。
#
#  缺陷（本门禁要锁住的东西）：
#    ffmpeg 的 AVIF muxer 实测丢 alpha：即使显式 `-pix_fmt yuva420p`，产物仍只有单条
#    `av1/yuv420p` 流（alpha 平面被丢）。旧 JXR 旁路对所有目标一律走 ffmpeg 单命令
#    ⇒ `a8.jxr → .avif` 的产物静默变成不透明图，日志却报“已完成”。
#  修复：源含 alpha 且目标 avif 时，JXR 旁路改走 `ffmpeg → RGBA PNG → avifenc`；
#    avifenc 会写标准 alpha 辅助流（第二条 av1/gray 轨道）。8-bit 进 8-bit、16-bit 进 10-bit。
#
#  判据分组：
#    ① 夹具自备 + 形态自检（无 alpha / 8-bit alpha / 16-bit alpha 三档真分叉）；
#    ② 8-bit alpha：产物必须有 2 条流（color yuv420p + alpha gray），alpha 值≈128；
#    ③ 16-bit alpha：color yuv420p10le + alpha gray10le，alpha 值≈128（保高位深出口）；
#    ④ 无 alpha 负控：仍只有 1 条流，且不得走 avifenc 分支（证明新分支只对 alpha 生效）；
#    ⑤ 尺子分叉：同样的 RGBA PNG 直接交 ffmpeg 编码只有 1 条流 —— 证明“2 条流”不是
#       AVIF 容器的默认形态，而是本修复刻意走 avifenc 的结果。
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
$jxrEnc = "$root/publish/PLAN/artifacts/JxrEncApp.exe"
$avifenc = "$root/publish/PLAN/artifacts/avifenc.exe"

$work = Join-Path $root ("tests\output\avif_alpha_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:TMP = $work; $env:TEMP = $work; $env:TMPDIR = $work

foreach ($need in @(@('被测 exe', $exe), @('ffmpeg', $ff), @('ffprobe', $fp),
                    @('JxrEncApp', $jxrEnc), @('avifenc', $avifenc))) {
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
  $o = "$work/_$tag.out"; $e = "$work/_$tag.err"
  Remove-Item $o, $e -Force -ErrorAction SilentlyContinue
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  $so = ''; $se = ''
  if (Test-Path $o) { $so = [System.IO.File]::ReadAllText($o) }
  if (Test-Path $e) { $se = [System.IO.File]::ReadAllText($e) }
  return @{ code = $p.ExitCode; text = ($so + "`n" + $se) }
}
function PixFmt([string]$path) {
  $r = Exec $fp ('-v error -select_streams v:0 -show_entries stream=pix_fmt -of default=nw=1:nk=1 "' + $path + '"') ('pf_' + [guid]::NewGuid().ToString('N'))
  return $r.text.Trim()
}
function FirstProduct([string]$dir, [string]$ext) {
  if (-not (Test-Path $dir)) { return $null }
  return Get-ChildItem $dir -File -Recurse -ErrorAction SilentlyContinue |
         Where-Object { $_.Extension -eq $ext } | Select-Object -First 1
}
function Streams([string]$path) {
  $r = Exec $fp ('-v error -show_entries stream=index,codec_name,pix_fmt -of csv=p=0 "' + $path + '"') ('st_' + [guid]::NewGuid().ToString('N'))
  return @($r.text -split "`r?`n" | Where-Object { $_.Trim() -match '^\d+,' })
}
function AlphaRange([string]$avifPath, [string]$rawPath) {
  $null = Exec $ff ('-v error -i "' + $avifPath + '" -map 0:1 -f rawvideo -pix_fmt gray "' + $rawPath + '"') ('alpha_' + [guid]::NewGuid().ToString('N'))
  if (-not (Test-Path $rawPath)) { return @{ min = -1; max = -1; n = 0 } }
  $bytes = [System.IO.File]::ReadAllBytes($rawPath)
  if ($bytes.Length -eq 0) { return @{ min = -1; max = -1; n = 0 } }
  $mn = 255; $mx = 0
  foreach ($b in $bytes) {
    if ($b -lt $mn) { $mn = $b }
    if ($b -gt $mx) { $mx = $b }
  }
  return @{ min = $mn; max = $mx; n = $bytes.Length }
}

# ── ① 夹具：无 alpha / 8-bit alpha(128) / 16-bit alpha(32768) ─────────────────
Write-Output "`n### ① 夹具与三档分叉 ###"
$pngS = "$work/s8.png"; $bmpS = "$work/s8.bmp"; $s8 = "$work/s8.jxr"
$null = Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "testsrc2=s=64x64" -frames:v 1 "' + $pngS + '"') 'gen_s_png'
$null = Exec $ff ('-y -hide_banner -loglevel error -i "' + $pngS + '" -pix_fmt bgr24 "' + $bmpS + '"') 'gen_s_bmp'
$rS = Exec $jxrEnc ('-i "' + $bmpS + '" -o "' + $s8 + '"') 'gen_s_jxr'

$pngA = "$work/a8.png"; $bmpA = "$work/a8.bmp"; $a8 = "$work/a8.jxr"
$null = Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "color=red:s=64x64" -vf "format=rgba,colorchannelmixer=aa=0.5" -frames:v 1 "' + $pngA + '"') 'gen_a_png'
$null = Exec $ff ('-y -hide_banner -loglevel error -i "' + $pngA + '" -pix_fmt bgra "' + $bmpA + '"') 'gen_a_bmp'
$rA = Exec $jxrEnc ('-i "' + $bmpA + '" -o "' + $a8 + '"') 'gen_a_jxr'

$raw16 = "$work/u16a.bin"; $u16a = "$work/u16a.jxr"
$fs = [System.IO.File]::Create($raw16); $bw = New-Object System.IO.BinaryWriter $fs
for ($y = 0; $y -lt 64; $y++) {
  for ($x = 0; $x -lt 64; $x++) {
    $bw.Write([uint16](($x * 1013) -band 0xFFFF))
    $bw.Write([uint16](($y * 7919 + 0x0100) -band 0xFFFF))
    $bw.Write([uint16]((0x1234 + $x * 17) -band 0xFFFF))
    $bw.Write([uint16]32768)
  }
}
$bw.Close(); $fs.Close()
$rU = Exec $ff ('-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgba64le -s 64x64 -i "' + $raw16 + '" -c:v libjxr "' + $u16a + '"') 'gen_u16a_jxr'

$pfS = PixFmt $s8; $pfA = PixFmt $a8; $pfU = PixFmt $u16a
Write-Output ("  [fixture] s8=$pfS  a8=$pfA  u16a=$pfU  (JxrEncApp rc=$($rS.code)/$($rA.code), libjxr rc=$($rU.code))")
CK (($rS.code -eq 0) -and ($rA.code -eq 0) -and ($rU.code -eq 0) -and (Test-Path $s8) -and (Test-Path $a8) -and (Test-Path $u16a)) `
   "① 三份 JXR 夹具都造出来了"
CK (($pfS -eq 'bgr24') -and ($pfA -eq 'bgra') -and ($pfU -eq 'rgba64le')) `
   "① 三档真分叉：bgr24 / bgra / rgba64le（实 $pfS / $pfA / $pfU）"

# ── ② 8-bit alpha → AVIF ───────────────────────────────────────────────────
Write-Output "`n### ② 8-bit alpha → AVIF ###"
$outA = "$work/avif_a8"; New-Item -ItemType Directory -Force -Path $outA | Out-Null
$rA2 = Exec $exe ('--headless --log-level debug -i "' + $a8 + '" -o "' + $outA + '" --format avif') 'run_avif_a8'
$pA2 = FirstProduct $outA '.avif'
$stA = @(); if ($null -ne $pA2) { $stA = Streams $pA2.FullName }
$rgA = AlphaRange $pA2.FullName "$work/a8_alpha.gray"
Write-Output ("  exit={0} 产物={1}B 流数={2} alpha={3}..{4} ({5} B)" -f $rA2.code, $(if ($null -ne $pA2) { $pA2.Length } else { 0 }), $stA.Count, $rgA.min, $rgA.max, $rgA.n)
foreach ($ln in $stA) { Write-Output ("    stream: " + $ln) }
CK (($rA2.code -eq 0) -and ($null -ne $pA2) -and ($pA2.Length -gt 0)) "② 出片成功（exit=0，非空 .avif）"
CK ($rA2.text -match '\[jxr→avif\]') "② 走了专用 alpha 出口（日志有「[jxr→avif]」）"
CK ($rA2.text -match 'avifenc') "② 执行体是 avifenc（日志点名）"
CK ($stA.Count -eq 2) "② 产物有 2 条流（color + alpha，实 $($stA.Count)）"
CK (($stA.Count -ge 1) -and ($stA[0] -match ',yuv420p$')) "② color 流是 yuv420p（实 $(if ($stA.Count -ge 1) { $stA[0] } else { '缺失' })）"
CK (($stA.Count -ge 2) -and ($stA[1] -match ',gray$')) "② alpha 流是 av1/gray（实 $(if ($stA.Count -ge 2) { $stA[1] } else { '缺失' })）"
CK (($rgA.n -eq 4096) -and ($rgA.min -ge 126) -and ($rgA.max -le 130)) `
   "② alpha 值保持 0.5（实测 $($rgA.min)..$($rgA.max)，期望 126..130）"

# ── ③ 16-bit alpha → AVIF ──────────────────────────────────────────────────
Write-Output "`n### ③ 16-bit alpha → AVIF ###"
$outU = "$work/avif_u16a"; New-Item -ItemType Directory -Force -Path $outU | Out-Null
$rU2 = Exec $exe ('--headless --log-level debug -i "' + $u16a + '" -o "' + $outU + '" --format avif') 'run_avif_u16a'
$pU2 = FirstProduct $outU '.avif'
$stU = @(); if ($null -ne $pU2) { $stU = Streams $pU2.FullName }
$rgU = AlphaRange $pU2.FullName "$work/u16a_alpha.gray"
Write-Output ("  exit={0} 产物={1}B 流数={2} alpha={3}..{4}" -f $rU2.code, $(if ($null -ne $pU2) { $pU2.Length } else { 0 }), $stU.Count, $rgU.min, $rgU.max)
foreach ($ln in $stU) { Write-Output ("    stream: " + $ln) }
CK (($rU2.code -eq 0) -and ($null -ne $pU2) -and ($pU2.Length -gt 0)) "③ 出片成功（exit=0，非空 .avif）"
CK (($stU.Count -eq 2) -and ($stU[0] -match ',yuv420p10le$') -and ($stU[1] -match ',gray10le$')) `
   "③ 两条流都是 10-bit：color=yuv420p10le / alpha=gray10le（实 $($stU -join ' | ')）"
CK (($rgU.n -eq 4096) -and ($rgU.min -ge 126) -and ($rgU.max -le 130)) `
   "③ 16-bit alpha 仍为 0.5（实测 $($rgU.min)..$($rgU.max)，期望 126..130）"

# ── ④ 无 alpha 负控：不得走新分支，仍只有 1 条流 ─────────────────────────────
Write-Output "`n### ④ 无 alpha 负控 ###"
$outS = "$work/avif_s8"; New-Item -ItemType Directory -Force -Path $outS | Out-Null
$rS2 = Exec $exe ('--headless --log-level debug -i "' + $s8 + '" -o "' + $outS + '" --format avif') 'run_avif_s8'
$pS2 = FirstProduct $outS '.avif'
$stS = @(); if ($null -ne $pS2) { $stS = Streams $pS2.FullName }
Write-Output ("  exit={0} 流数={1} 日志含 jxr→avif={2}" -f $rS2.code, $stS.Count, $(if ($rS2.text -match '\[jxr→avif\]') { '是' } else { '否' }))
CK (($rS2.code -eq 0) -and ($null -ne $pS2) -and ($pS2.Length -gt 0)) "④ 无 alpha 源仍出片"
CK ($stS.Count -eq 1) "④ 无 alpha 源仍只有 1 条流（实 $($stS.Count)）"
CK ($rS2.text -notmatch '\[jxr→avif\]') "④ 无 alpha 源不得走 alpha 专用分支"

# ── ⑤ 尺子分叉：同一张 RGBA PNG 直接交 ffmpeg 只有 1 条流 ────────────────────
Write-Output "`n### ⑤ 对照：ffmpeg 直编丢 alpha ###"
$ctrl = "$work/ffmpeg_ctrl.avif"
$rC = Exec $ff ('-y -v error -i "' + $pngA + '" -c:v libaom-av1 -pix_fmt yuva420p "' + $ctrl + '"') 'ctrl_ffmpeg'
$stC = @(); if (Test-Path $ctrl) { $stC = Streams $ctrl }
Write-Output ("  ffmpeg 直编: rc={0} 流数={1}" -f $rC.code, $stC.Count)
CK (($rC.code -eq 0) -and ($stC.Count -eq 1)) `
   "⑤ 对照臂成立：ffmpeg 直编该 RGBA PNG 只有 1 条流（实 $($stC.Count)）⇒ 本闸的 2 条流不是容器默认"

Write-Output ''
Write-Output ("PASS=$script:pass FAIL=$script:fail  work=$work")
if ($script:fail -eq 0) { exit 0 } else { exit 1 }
