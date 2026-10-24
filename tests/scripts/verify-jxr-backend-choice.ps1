# ═══════════════════════════════════════════════════════════
#  verify-jxr-backend-choice.ps1 — 第 82 条（2026-10-25 接线）
#  被测：JXR 输入旁路对 `-e/--encoder` 的**兑现性**。
#
#  缺陷（本门禁要锁住的东西）：
#    `ProcessJxrInputAsync`（以及 JXL 旁路）旧写法只看工具是否可用：
#      useCjxl    = CjxlService.IsAvailable && target==jxl
#      useCjpegli = CjpegliService.IsAvailable && target in jpg/jpeg/jpegli
#    ⇒ 用户显式传 `-e Ffmpeg` 时，JPG 仍走 cjpegli、JXL 仍走 cjxl，产物逐字节与默认臂相同，
#      日志也没有一句“你的选择被忽略了”。JXR 目标则无论 `-e` 是什么都固定用 JxrEncApp（设计如此：
#      ffmpeg libjxr 无质量 AVOption），但同样**静默**。
#  修复：JPG/JXL 按 `Options.EncoderBackend` 真分叉；JXR 的固定执行体在分派前**响亮播报**一次。
#
#  判据分组：
#    ① 夹具自备 + 两档真分叉（bgr24 走引擎 / bgra 走旁路）；
#    ② JPG：默认 Ffmpeg 臂与 `-e Cjpegli` 臂的日志与产物哈希都必须不同；
#    ③ JXL：默认 Cjxl 臂与 `-e Ffmpeg` 臂的日志与产物哈希都必须不同；
#    ④ JXR：`-e Ffmpeg` 仍出片，但覆盖警告必须**恰好出现一次**且点名 JxrEncApp（引擎/旁路两条都测）；
#    ⑤ 兼容性守卫仍在：`-f jpg -e Cjxl` 必须被拒绝且零产物（新增分支不得绕过它）。
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

$work = Join-Path $root ("tests\output\jxr_backend_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:TMP = $work; $env:TEMP = $work; $env:TMPDIR = $work

foreach ($need in @(@('被测 exe', $exe), @('ffmpeg', $ff), @('ffprobe', $fp), @('JxrEncApp', $jxrEnc))) {
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
function SHA16([string]$path) {
  return (Get-FileHash $path -Algorithm SHA256).Hash.Substring(0, 16)
}

# ── ① 夹具：8-bit 无 alpha（引擎路）/ 8-bit alpha（旁路）──────────────────────
Write-Output "`n### ① 夹具与两档分叉 ###"
$pngS = "$work/s8.png"; $bmpS = "$work/s8.bmp"; $s8 = "$work/s8.jxr"
$null = Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "testsrc2=s=64x64" -frames:v 1 "' + $pngS + '"') 'gen_s_png'
$null = Exec $ff ('-y -hide_banner -loglevel error -i "' + $pngS + '" -pix_fmt bgr24 "' + $bmpS + '"') 'gen_s_bmp'
$rS = Exec $jxrEnc ('-i "' + $bmpS + '" -o "' + $s8 + '"') 'gen_s_jxr'

$pngA = "$work/a8.png"; $bmpA = "$work/a8.bmp"; $a8 = "$work/a8.jxr"
$null = Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "color=red:s=64x64" -vf "format=rgba,colorchannelmixer=aa=0.5" -frames:v 1 "' + $pngA + '"') 'gen_a_png'
$null = Exec $ff ('-y -hide_banner -loglevel error -i "' + $pngA + '" -pix_fmt bgra "' + $bmpA + '"') 'gen_a_bmp'
$rA = Exec $jxrEnc ('-i "' + $bmpA + '" -o "' + $a8 + '"') 'gen_a_jxr'

$pfS = PixFmt $s8; $pfA = PixFmt $a8
Write-Output ("  [fixture] s8=$pfS  a8=$pfA  (JxrEncApp rc=$($rS.code)/$($rA.code))")
CK (($rS.code -eq 0) -and ($rA.code -eq 0) -and (Test-Path $s8) -and (Test-Path $a8)) `
   "① 两份 JXR 夹具都造出来了"
CK (($pfS -eq 'bgr24') -and ($pfA -eq 'bgra')) `
   "① 两档真分叉：s8=bgr24（引擎）/ a8=bgra（旁路，实 $pfS / $pfA）"

# ── ② JPG：默认 Ffmpeg 臂 vs 显式 Cjpegli 臂 ────────────────────────────────
Write-Output "`n### ② JPG 后端分叉 ###"
$outJDef = "$work/jpg_default"; $outJCj = "$work/jpg_cjpegli"
New-Item -ItemType Directory -Force -Path $outJDef, $outJCj | Out-Null
$rJDef = Exec $exe ('--headless --log-level debug -i "' + $a8 + '" -o "' + $outJDef + '" --format jpg') 'run_jpg_default'
$rJCj  = Exec $exe ('--headless --log-level debug -i "' + $a8 + '" -o "' + $outJCj  + '" --format jpg -e Cjpegli') 'run_jpg_cjpegli'
$pJDef = FirstProduct $outJDef '.jpg'; $pJCj = FirstProduct $outJCj '.jpg'
Write-Output ("  default: exit={0}  {1}" -f $rJDef.code, $(if ($null -ne $pJDef) { $pJDef.Length.ToString() + 'B' } else { '无产物' }))
Write-Output ("  cjpegli: exit={0}  {1}" -f $rJCj.code, $(if ($null -ne $pJCj) { $pJCj.Length.ToString() + 'B' } else { '无产物' }))
CK (($rJDef.code -eq 0) -and ($null -ne $pJDef) -and ($rJCj.code -eq 0) -and ($null -ne $pJCj)) `
   "② 两臂都出片"
CK (($rJDef.text -match '后端: Ffmpeg') -and ($rJDef.text -match 'Step 2: ffmpeg BMP → JPG') -and ($rJDef.text -notmatch 'PPM pipe → cjpegli')) `
   "② 默认臂真用 Ffmpeg（日志：后端 Ffmpeg + ffmpeg BMP → JPG，且无 cjpegli 管道）"
CK (($rJCj.text -match '后端: Cjpegli') -and ($rJCj.text -match 'PPM pipe → cjpegli')) `
   "② 显式臂真用 cjpegli（日志：后端 Cjpegli + PPM pipe → cjpegli）"
CK ((SHA16 $pJDef.FullName) -ne (SHA16 $pJCj.FullName)) `
   "② 两臂产物哈希不同（不是同一执行体换了个标签）"

# ── ③ JXL：默认 Cjxl 臂 vs 显式 Ffmpeg 臂 ───────────────────────────────────
Write-Output "`n### ③ JXL 后端分叉 ###"
$outXDef = "$work/jxl_default"; $outXFf = "$work/jxl_ffmpeg"
New-Item -ItemType Directory -Force -Path $outXDef, $outXFf | Out-Null
$rXDef = Exec $exe ('--headless --log-level debug -i "' + $a8 + '" -o "' + $outXDef + '" --format jxl') 'run_jxl_default'
$rXFf  = Exec $exe ('--headless --log-level debug -i "' + $a8 + '" -o "' + $outXFf  + '" --format jxl -e Ffmpeg') 'run_jxl_ffmpeg'
$pXDef = FirstProduct $outXDef '.jxl'; $pXFf = FirstProduct $outXFf '.jxl'
Write-Output ("  default: exit={0}  {1}" -f $rXDef.code, $(if ($null -ne $pXDef) { $pXDef.Length.ToString() + 'B' } else { '无产物' }))
Write-Output ("  ffmpeg : exit={0}  {1}" -f $rXFf.code, $(if ($null -ne $pXFf) { $pXFf.Length.ToString() + 'B' } else { '无产物' }))
CK (($rXDef.code -eq 0) -and ($null -ne $pXDef) -and ($rXFf.code -eq 0) -and ($null -ne $pXFf)) `
   "③ 两臂都出片"
CK (($rXDef.text -match '后端: Cjxl') -and ($rXDef.text -match 'PPM pipe → cjxl')) `
   "③ 默认臂真用 cjxl（日志：后端 Cjxl + PPM pipe → cjxl）"
CK (($rXFf.text -match '后端: Ffmpeg') -and ($rXFf.text -match 'Step 2: ffmpeg BMP → JXL') -and ($rXFf.text -notmatch 'PPM pipe → cjxl')) `
   "③ 显式 Ffmpeg 臂真用 ffmpeg（日志：ffmpeg BMP → JXL，且无 cjxl 管道）"
CK ((SHA16 $pXDef.FullName) -ne (SHA16 $pXFf.FullName)) `
   "③ 两臂产物哈希不同"

# ── ④ JXR 目标：覆盖警告恰好一次（引擎路 + 旁路各一格）───────────────────────
Write-Output "`n### ④ JXR 目标的后端覆盖播报 ###"
$outJB = "$work/jxr_bypass_ffmpeg"; $outJE = "$work/jxr_engine_ffmpeg"
New-Item -ItemType Directory -Force -Path $outJB, $outJE | Out-Null
$rJB = Exec $exe ('--headless --log-level debug -i "' + $a8 + '" -o "' + $outJB + '" --format jxr -e Ffmpeg') 'run_jxr_bypass_ffmpeg'
$rJE = Exec $exe ('--headless --log-level debug -i "' + $s8 + '" -o "' + $outJE + '" --format jxr -e Ffmpeg') 'run_jxr_engine_ffmpeg'
$pJB = FirstProduct $outJB '.jxr'; $pJE = FirstProduct $outJE '.jxr'
$nJB = ([regex]::Matches($rJB.text, 'Ffmpeg 后端不生效')).Count
$nJE = ([regex]::Matches($rJE.text, 'Ffmpeg 后端不生效')).Count
Write-Output ("  bypass: exit={0} 警告={1} 产物={2}" -f $rJB.code, $nJB, $(if ($null -ne $pJB) { $pJB.Length.ToString() + 'B' } else { '无' }))
Write-Output ("  engine: exit={0} 警告={1} 产物={2}" -f $rJE.code, $nJE, $(if ($null -ne $pJE) { $pJE.Length.ToString() + 'B' } else { '无' }))
CK (($rJB.code -eq 0) -and ($null -ne $pJB) -and ($rJB.text -match 'JxrEncApp 重新编码')) `
   "④ 旁路：`-e Ffmpeg` 仍出片，且日志点名 JxrEncApp 重新编码"
CK ($nJB -eq 1) "④ 旁路：覆盖警告恰好 1 次（实 $nJB）"
CK (($rJE.code -eq 0) -and ($null -ne $pJE) -and ($rJE.text -match 'JxrEncApp 命令行')) `
   "④ 引擎路：`-e Ffmpeg` 仍出片，且日志点名 JxrEncApp 命令行"
CK ($nJE -eq 1) "④ 引擎路：覆盖警告恰好 1 次（实 $nJE）"

# ── ⑤ 兼容性守卫仍在：跨族后端必须被拒绝 ──────────────────────────────────
Write-Output "`n### ⑤ 兼容性守卫回归 ###"
$outBad = "$work/jpg_cjxl_bad"; New-Item -ItemType Directory -Force -Path $outBad | Out-Null
$rBad = Exec $exe ('--headless --log-level debug -i "' + $a8 + '" -o "' + $outBad + '" --format jpg -e Cjxl') 'run_jpg_cjxl'
$pBad = FirstProduct $outBad '.jpg'
CK (($rBad.code -ne 0) -and ($null -eq $pBad) -and ($rBad.text -match '不适用于目标格式')) `
   "⑤ `-f jpg -e Cjxl` 仍被兼容性守卫拒绝且零产物（实 exit=$($rBad.code)）"

Write-Output ''
Write-Output ("PASS=$script:pass FAIL=$script:fail  work=$work")
if ($script:fail -eq 0) { exit 0 } else { exit 1 }
