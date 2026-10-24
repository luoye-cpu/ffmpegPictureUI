# ═══════════════════════════════════════════════════════════
#  verify-dng-nonraw-input.ps1 — 第 81 条（2026-10-25 接线）
#  被测：`-f dng` + 非 RAW/DNG 输入的**统一前置拒绝**。
#
#  缺陷（本门禁要锁住的东西）：
#    同一件事（DNG 需要传感器 RAW 数据，普通图片反推不出 CFA）在旧代码里按路由分叉成三种结果：
#      · 8-bit 无 alpha JXR + 默认 Dng 后端 ⇒ ProcessDngAsync 给出清楚拒绝（“仅 RAW/DNG…”）
#      · 8-bit 无 alpha JXR + `-e Ffmpeg`    ⇒ ffmpeg `Error initializing the muxer … Invalid argument (-22)`
#      · alpha/16-bit JXR（专用输入管线）    ⇒ 同样只得到 ffmpeg -22
#    用户无法从后两种错误判断“是输入类型不支持”还是“工具坏了”。
#  修复：在**任何路由之前**判一次；所有输入类/后端共用同一句话、零产物。
#
#  判据分组：
#    ① 夹具自备 + 形态自检（8-bit 无 alpha / 带 alpha 的 JXR 必须真的分叉）；
#    ② 负控 6 格：PNG/JXR × 默认/-e Ffmpeg ⇒ exit=1、零产物、同一句拒绝、且不得再出现 ffmpeg -22；
#    ③ 正控：DNG→DNG 仍能走通（证明前置校验没有把合法输入一起拒掉）；
#    ④ 回归：JXR→PNG 仍成功（证明前置校验只作用于 dng 目标）。
#
#  退出码：0 = 全绿，1 = 有失败。结尾必须写完整 if/else（清理步会污染 $LASTEXITCODE）。
#  ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；取子进程输出一律
#    Start-Process + 重定向；拼参数用单引号相加。
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
$dngIn = "$root/tests/fixtures/raw_src_8bit.dng"

$work = Join-Path $root ("tests\output\dng_nonraw_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:TMP = $work; $env:TEMP = $work; $env:TMPDIR = $work

foreach ($need in @(@('被测 exe', $exe), @('ffmpeg', $ff), @('ffprobe', $fp),
                    @('JxrEncApp', $jxrEnc), @('DNG 正控夹具', $dngIn))) {
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
function SK([string]$msg) { Write-Output "SKIP $msg" }
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
function Products([string]$dir) {
  if (-not (Test-Path $dir)) { return @() }
  return @(Get-ChildItem $dir -File -Recurse -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -notlike '*.log' -and $_.Name -notlike '*.jsonl' })
}

# ── ① 夹具：8-bit 无 alpha / 带 alpha 各一件（JxrEncApp 只吃未压缩 BMP）──────────
Write-Output "`n### ① 夹具与形态自检 ###"
$png8 = "$work/s8.png"; $bmp8 = "$work/s8.bmp"; $s8 = "$work/s8.jxr"
$null = Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "testsrc2=s=64x64" -frames:v 1 "' + $png8 + '"') 'gen_s8_png'
$null = Exec $ff ('-y -hide_banner -loglevel error -i "' + $png8 + '" -pix_fmt bgr24 "' + $bmp8 + '"') 'gen_s8_bmp'
$rS8 = Exec $jxrEnc ('-i "' + $bmp8 + '" -o "' + $s8 + '"') 'gen_s8_jxr'

$pngA = "$work/a8.png"; $bmpA = "$work/a8.bmp"; $a8 = "$work/a8.jxr"
$null = Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "color=red:s=64x64" -vf "format=rgba,colorchannelmixer=aa=0.5" -frames:v 1 "' + $pngA + '"') 'gen_a8_png'
$null = Exec $ff ('-y -hide_banner -loglevel error -i "' + $pngA + '" -pix_fmt bgra "' + $bmpA + '"') 'gen_a8_bmp'
$rA8 = Exec $jxrEnc ('-i "' + $bmpA + '" -o "' + $a8 + '"') 'gen_a8_jxr'

$pfS8 = PixFmt $s8; $pfA8 = PixFmt $a8
Write-Output ("  [fixture] s8=$pfS8  a8=$pfA8  (JxrEncApp rc=$($rS8.code)/$($rA8.code))")
CK (($rS8.code -eq 0) -and ($rA8.code -eq 0) -and (Test-Path $s8) -and (Test-Path $a8)) `
   "① 两份 JXR 夹具都造出来了"
CK (($pfS8 -eq 'bgr24') -and ($pfA8 -eq 'bgra')) `
   "① 夹具真分叉：s8=bgr24 / a8=bgra（实 $pfS8 / $pfA8）"

# ── ② 负控：非 RAW + `-f dng`，所有路由都必须同一句拒绝、零产物 ──────────────
Write-Output "`n### ② 非 RAW + -f dng 统一拒绝 ###"
$msg = '仅 RAW/DNG 文件可编码为 DNG'
$cases = @(
  @{ name = 's8-default'; input = $s8;  extra = '' },
  @{ name = 's8-ffmpeg';  input = $s8;  extra = ' -e Ffmpeg' },
  @{ name = 'a8-default'; input = $a8;  extra = '' },
  @{ name = 'a8-ffmpeg';  input = $a8;  extra = ' -e Ffmpeg' },
  @{ name = 'png-default'; input = $png8; extra = '' },
  @{ name = 'png-ffmpeg';  input = $png8; extra = ' -e Ffmpeg' }
)
foreach ($c in $cases) {
  $out = "$work/dng_$($c.name)"; New-Item -ItemType Directory -Force -Path $out | Out-Null
  $r = Exec $exe ('--headless --log-level debug -i "' + $c.input + '" -o "' + $out + '" --format dng' + $c.extra) ('run_' + $c.name)
  $prod = @(Products $out)
  Write-Output ("  [{0}] exit={1} 产物={2} 文案={3}" -f $c.name, $r.code, $prod.Count, $(if ($r.text -match $msg) { '命中' } else { '缺失' }))
  CK (($r.code -eq 1) -and ($prod.Count -eq 0)) "② $($c.name)：exit=1 且零产物（实 $($r.code) / $($prod.Count)）"
  CK ($r.text -match $msg) "② $($c.name)：给出统一的「$msg」文案"
  CK ($r.text -match '失败 \(非 RAW 输入\)') "② $($c.name)：状态点名「非 RAW 输入」"
  CK (($r.text -notmatch 'Error initializing the muxer') -and ($r.text -notmatch 'Invalid argument')) `
     "② $($c.name)：不再落到 ffmpeg 的 muxer -22 噪音"
}

# ── ③ 正控：DNG→DNG 仍可走通（防前置校验把合法输入一起拒掉）──────────────────
Write-Output "`n### ③ 正控：DNG→DNG ###"
$outDng = "$work/dng_positive"; New-Item -ItemType Directory -Force -Path $outDng | Out-Null
$rPos = Exec $exe ('--headless --log-level debug -i "' + $dngIn + '" -o "' + $outDng + '" --format dng') 'run_dng_positive'
$pPos = @(Products $outDng | Where-Object { $_.Extension -eq '.dng' })
Write-Output ("  [DNG→DNG] exit={0} 产物={1}" -f $rPos.code, $pPos.Count)
CK (($rPos.code -eq 0) -and ($pPos.Count -eq 1) -and ($pPos[0].Length -gt 0)) `
   "③ DNG→DNG 仍成功（exit=0，1 个非空 .dng）"
CK ($rPos.text -match '\[dng\] 编码 DNG') "③ 正控确实进了 DNG 编码路径（日志有「[dng] 编码 DNG」）"
CK ($rPos.text -notmatch $msg) "③ 正控没有被前置校验误伤（无「$msg」）"

# ── ④ 回归：JXR→PNG 仍成功（前置校验只作用于 dng 目标）────────────────────────
Write-Output "`n### ④ 回归：JXR→PNG ###"
$outPng = "$work/png_regress"; New-Item -ItemType Directory -Force -Path $outPng | Out-Null
$rReg = Exec $exe ('--headless --log-level info -i "' + $a8 + '" -o "' + $outPng + '" --format png') 'run_png_regress'
$pReg = @(Products $outPng | Where-Object { $_.Extension -eq '.png' })
CK (($rReg.code -eq 0) -and ($pReg.Count -eq 1) -and ($pReg[0].Length -gt 0)) `
   "④ JXR→PNG 仍成功（exit=0，1 个非空 .png）"

Write-Output ''
Write-Output ("PASS=$script:pass FAIL=$script:fail  work=$work")
if ($script:fail -eq 0) { exit 0 } else { exit 1 }
