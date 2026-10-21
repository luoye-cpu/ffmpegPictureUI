# verify-jxr-highbitdepth.ps1 —— JXR 高位深输入通路专项门禁（2026-10-21 新增）
#
# 缺陷（本门禁要锁住的东西）：
#   `ProcessJxrInputAsync` 的取帧只有两臂：BMP 默认 → 失败后 `TIF + -c 0`。
#   实测 Xbox Game DVR 导出的 JXR 真身是 **4×16bit IEEE 半浮点（线性 scRGB）+ planar alpha**，
#   而 jxrlib 的转换表里 `64bppRGBAHalf` 只有 →32bppRGBA / →128bppRGBAFloat 两条出边：
#     · BMP 写入器不认 64bpp ⇒ `JXRTestBmp.c` 的 `WriteBMPHeader` 尾支报 WMP_errUnsupportedFormat
#     · `-c 0`(24bppRGB) 缺表条目 ⇒ `JXRGluePFC.c:2291` 报同一个码
#   ⇒ **两臂同报 -106**，任务零产物；旧日志那句「TIF 解码成功 (scRGB→sRGB 转换完成)」在该
#   通路上压根没调转换函数（属误导，本批撤）。旧回退臂只对 `128bppRGBAFloat` 一档成立。
#
# 修复形态：新增「与源同格式的直通臂」`-c 25/28/23/10`（from==to ⇒ 不查表 ⇒ 不撞 -106，
#   也不让 jxrlib 顺手做传递函数与钳位）→ 托管读取（`JxrHighPrecisionDecode.TryReadTiff`）
#   → sRGB 编码 / Reinhard → **16-bit PAM** → 既有编码出口。JXR 目标档仍走 JxrEncApp：它拒收的**不是**
#   16-bit TIFF 而是**压缩** TIFF（ffmpeg 默认写 `259=32773` Adobe Deflate ⇒ rc=-106），补上
#   `-compression_algo raw -pred none` 后 16-bit RGB/RGBA TIFF 正常 rc=0（实测产物 exiftool 回读
#   `64-bit RGBA`、WIC 与 JxrDecApp 都能解）。⇒ 不退到 ffmpeg 的 libjxr（它零条私有 AVOption、
#   质量旗标实测全同字节 ⇒ 会把用户的质量/无损静默吞掉；JxrEncApp 的质量轴实测有效）。
#
# 两条**设计前提**（本门禁逐条实测，不靠注释）：
#   P1 swscale **不接受 `rgbaf32le` 作源** ⇒ 浮点 TIFF 中间件不能交给 ffmpeg 转手（④ 的前提锁）；
#   P2 ffmpeg 的 libjxr 解码器把 planar alpha 塌成 0（真值 0.5）⇒ 换解码器会丢 alpha，
#      所以 alpha 必须由本通路自己带到出口（② 的 alpha 对账）。
#
# 判据分组：
#   ① 夹具合成 + **三档分叉自检**（half/float/8bit 的 pix_fmt 必须互不相同，否则对照臂没分叉）；
#   ② HP 正例：half+png、float+jpg、Reinhard 对照、jxr 出口 + WIC 第三方回读；
#   ③ 回归：8bit JXR 必须仍走 BMP 臂（不得被顺手改道）；
#   ④ 前提锁 P1（`rgbaf32le` 转换不出去）+ P1b（16-bit FP TIFF 被 ffmpeg **静默**按整数读，
#      并禁止任何 ffmpeg 命令吃这个中间件——该缺席断言自带正控）；
#      P2（libjxr 解码丢 planar alpha）由 ② 的 alpha 读数承担，不在本节；
#   ⑤ 源码形态锁（曲线常数只此一份、旧谎言串 0 命中、四个 Arm 常量都在、指标行在）；
#   ⑥ 被测 exe mtime 前后一致 + 运行级目录残留自证。
#
# 退出码：0 = 全绿，1 = 有失败。结尾必须写**完整 if/else**（清理步会污染 $LASTEXITCODE）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号；
#   取子进程输出一律 Start-Process + 重定向；拼参数用单引号相加，不用「反引号+双引号」。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$jxrEnc = "$root/publish/PLAN/artifacts/JxrEncApp.exe"
$jxrDec = "$root/publish/PLAN/artifacts/JxrDecApp.exe"
$hpFile = "$root/src/FfmpegGui/Services/JxrHighPrecisionDecode.cs"
$simdFile = "$root/src/FfmpegGui/Services/SimdPixelOps.cs"
$qpFile = "$root/src/FfmpegGui/Services/QueueProcessor.cs"

# 运行级隔离目录：**放仓库 tests/output/**（.gitignore 已忽略）并把 TMP/TEMP 一起指过去。
# 理由（本机实测，非风格偏好）：本仓的通行环境里 ffmpeg 与宿主子进程对 %TEMP% 的写会被按进程拦成
# `Permission denied`（`[image2] Could not open file`），只有 .NET/Python 侧写得进去 ⇒ 若沿用
# %TEMP%，夹具根本造不出来、整门禁退化成"全红但什么都没测"。产品自己的 jxr 临时目录同理。
# GUID 子目录保留（并发同名实例互删会造假日志/假红）。
$work = Join-Path $root ("tests\output\jxr_hp_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:TMP = $work
$env:TEMP = $work

$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Host "PASS $msg" -ForegroundColor Green }
  else     { $script:fail++; Write-Host "FAIL $msg" -ForegroundColor Red }
}
function Exec([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e)) }
}
function ExecOut([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  if (-not (Test-Path $o)) { return @{ code = $p.ExitCode; text = "" } }
  return @{ code = $p.ExitCode; text = [System.IO.File]::ReadAllText($o) }
}
function PixFmt([string]$path) {
  $r = ExecOut $fp ('-v error -select_streams v:0 -show_entries stream=pix_fmt -of default=nw=1:nk=0 "' + $path + '"') ('pf_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
  foreach ($ln in ($r.text -split "`r?`n")) { if ($ln -match 'pix_fmt=([a-z0-9]+)') { return $Matches[1] } }
  return ""
}
function Product([string]$outdir, [string[]]$exts) {
  if (-not (Test-Path $outdir)) { return $null }
  foreach ($f in @(Get-ChildItem -LiteralPath $outdir -File -ErrorAction SilentlyContinue)) {
    foreach ($x in $exts) { if ($f.Extension -ieq $x) { return $f } }
  }
  return $null
}
# 从产品日志里取 HP 指标行（ASCII 键值缝，不靠中文散文）
function HpMetrics([string]$text) {
  foreach ($ln in ($text -split "`r?`n")) {
    if ($ln -match 'HP-METRICS') {
      $m = @{}
      foreach ($kv in ($ln -split ' ')) { if ($kv -match '^(.+?)=(.+)$') { $m[$Matches[1]] = $Matches[2] } }
      return $m
    }
  }
  return $null
}
# IEEE754 半浮点编码（仅正规格数，夹具只用精确可表示的值）
function HalfBits([single]$v) {
  $bits = [int]0
  if ($v -eq 0) { return 0 }
  $e = [Math]::Floor([Math]::Log($v, 2))
  $mant = $v / [Math]::Pow(2, $e) - 1.0
  $ee = [int]($e + 15); $mm = [int]($mant * 1024.0 + 0.5)
  return [int](($ee -shl 10) -bor $mm)
}

foreach ($need in @($exe, $ff, $fp, $jxrEnc, $jxrDec, $hpFile, $simdFile, $qpFile)) {
  if (-not (Test-Path $need)) { Write-Host "缺工具/源文件：$need" -ForegroundColor Red; exit 1 }
}
$exeMtimeBefore = (Get-Item $exe).LastWriteTimeUtc

# ── ① 夹具合成 + 三档分叉自检 ─────────────────────────────────────────────────────
Write-Host "`n### ① 合成 half/float/8bit 三档 JXR 夹具，并断言三档真的分叉 ###" -ForegroundColor Cyan
$W = 64; $H = 48
# HalfBits 自检：位型必须对上实测锚点（0x3C00 = 本机从真件 alpha 平面读到的「1.0」；
#   2.5 = 1.25×2^1 ⇒ exp 字段 16、尾数 0.25×1024=256 ⇒ 0x4100）
CK ((((HalfBits 0.5) -eq 0x3800) -and ((HalfBits 1.0) -eq 0x3C00) -and ((HalfBits 2.5) -eq 0x4100) -and ((HalfBits 0.75) -eq 0x3A00))) `
   "① HalfBits 与实测位型对锚（0.5=0x3800 / 1.0=0x3C00 / 2.5=0x4100 / 0.75=0x3A00）"

# 已知量：R 在奇数列取 2.5（>1.0），偶数列取 0.5；G=0.75 B=0.25；A 恒 0.5（ffmpeg 的 libjxr 会把它塌成 0）
$expectOver = ($W / 2) * $H          # 奇数列像素数 = 32 × 48
function Write-FloatRaw([string]$path, [bool]$half) {
  $fs = [System.IO.File]::Create($path); $bw = New-Object System.IO.BinaryWriter $fs
  for ($y = 0; $y -lt $H; $y++) {
    for ($x = 0; $x -lt $W; $x++) {
      # 奇数列给一条 **1.2..2.5 的高光斜坡**（不是单一值）：单一值经 Reinhard 会被映到正好 1.0，
      # 与钳位档的"满带计数"完全相同 ⇒ 对照臂没有牙（本机实测踩过：1536→1536）。
      # 斜坡才有"层次被保住"这件事可测；下限取 1.2 是为了让有损编码的量化不会把样本推回 1.0 以下。
      $r = 0.5; if (($x % 2) -eq 1) { $r = 1.2 + 1.3 * $x / ($W - 1) }
      $vals = @($r, 0.75, 0.25, 0.5)
      foreach ($v in $vals) {
        if ($half) { $bw.Write([uint16](HalfBits $v)) } else { $bw.Write([single]$v) }
      }
    }
  }
  $bw.Close(); $fs.Close()
}
$rawF = "$work/rgba_f32.bin"; $rawH = "$work/rgba_f16.bin"
Write-FloatRaw $rawF $false
Write-FloatRaw $rawH $true
$mkf = Exec $ff ('-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgbaf32le -s ' + $W + 'x' + $H + ' -i "' + $rawF + '" -c:v libjxr "' + "$work/hp_f32.jxr" + '"') "mkf32"
$mkh = Exec $ff ('-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgbaf16le -s ' + $W + 'x' + $H + ' -i "' + $rawH + '" -c:v libjxr "' + "$work/hp_f16.jxr" + '"') "mkf16"
$bmpSrc = "$work/sdr.bmp"
Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "testsrc=s=' + $W + 'x' + $H + '" -frames:v 1 "' + $bmpSrc + '"') "genbmp" | Out-Null
$mks = Exec $jxrEnc ('-i "' + $bmpSrc + '" -o "' + "$work/sdr8.jxr" + '"') "mksdr8"

$pfF32 = PixFmt "$work/hp_f32.jxr"; $pfF16 = PixFmt "$work/hp_f16.jxr"; $pfS8 = PixFmt "$work/sdr8.jxr"
Write-Host ("  [夹具] hp_f32=" + $pfF32 + "  hp_f16=" + $pfF16 + "  sdr8=" + $pfS8)
CK ((Test-Path "$work/hp_f32.jxr") -and (Test-Path "$work/hp_f16.jxr") -and (Test-Path "$work/sdr8.jxr")) `
   "① 三份夹具都造出来了（f32/f16/8bit；造不出则本门禁前提不成立）"
CK (($pfF32 -match 'af32') -and ($pfF16 -match 'af16') -and ($pfS8 -notmatch 'af32|af16|64|48')) `
   "① **三档分叉成立**：$pfF32 / $pfF16 / $pfS8 互不相同 ⇒ 直通臂 28/25/不适用 各自有活格子"

# ── ② HP 正例 ────────────────────────────────────────────────────────────────────
Write-Host "`n### ② 高位深直通：half→png、float→jpg、Reinhard 对照、jxr 出口 ###" -ForegroundColor Cyan
$cases = @(
  @{ jxr = "$work/hp_f16.jxr"; name = "f16"; fmt = "png"; extra = ""; wantArm = '25'; wantKind = 'HalfFloat16' },
  @{ jxr = "$work/hp_f32.jxr"; name = "f32"; fmt = "jpg"; extra = ""; wantArm = '28'; wantKind = 'Float32' }
)
$defaultSat = -1
foreach ($c in $cases) {
  $outdir = "$work/run_$($c.name)"
  New-Item -ItemType Directory -Force -Path $outdir | Out-Null
  $r = Exec $exe ('--headless --log-level debug -i "' + $c.jxr + '" -o "' + $outdir + '" --format ' + $c.fmt + $c.extra) "run_$($c.name)"
  $m = HpMetrics $r.text
  $p = Product $outdir @(".png", ".jpg", ".jpeg")
  Write-Host ("  [{0} → {1}] exit={2} 产物={3} 指标={4}" -f $c.name, $c.fmt, $r.code, `
    $(if ($null -ne $p) { $p.Name + ':' + $p.Length + 'B' } else { "(无)" }), `
    $(if ($null -ne $m) { ($m.Keys | Sort-Object | ForEach-Object { $_ + '=' + $m[$_] }) -join ' ' } else { "(无)" }))
  CK ($r.code -eq 0) "② $($c.name) → $($c.fmt)：exit == 0（实 $($r.code)）"
  CK ($null -ne $p) "② $($c.name) → $($c.fmt)：有产物（不是零产物）"
  CK (($r.text -notmatch 'Unsupported format') -and ($r.text -notmatch '退出码 -106')) `
     "② $($c.name) → $($c.fmt)：日志里**没有** -106/Unsupported（缺陷签名清零）"
  CK ($null -ne $m) "② $($c.name) → $($c.fmt)：HP-METRICS 指标行在（判据有可读的缝）"
  if ($null -ne $m) {
    CK ($m['arm'] -eq $c.wantArm) "② $($c.name)：走的是直通臂 -c $($m['arm'])（期望 $($c.wantArm)）"
    CK ($m['kind'] -eq $c.wantKind) "② $($c.name)：帧型 = $($m['kind'])（期望 $($c.wantKind)）"
    $peak = 0.0; [void][double]::TryParse($m['peak'], [ref] $peak)
    CK (($peak -gt 2.0) -and ($peak -lt 3.0)) "② $($c.name)：实测峰值 $peak 落在夹具声明区间 (2.0,3.0) ⇒ >1.0 的高光**没被解码链弄丢**"
    if ($m['overWhite'] -match '^(\d+)/(\d+)$') {
      $ow = [int]$Matches[1]; $rb = [int]$Matches[2]
      CK (($ow -eq $expectOver) -and ($rb -eq ($W * $H * 3))) "② $($c.name)：越白样本 $ow/$rb == 夹具算出的 $expectOver/$($W*$H*3)（独立计数对账，不是自证）"
    } else { CK $false "② $($c.name)：overWhite 读数形状不是 A/B" }
    $amin = 0.0; $amax = 0.0
    [void][double]::TryParse($m['alphaMin'], [ref] $amin); [void][double]::TryParse($m['alphaMax'], [ref] $amax)
    CK (($amin -gt 0.47) -and ($amax -lt 0.53)) "② $($c.name)：planar alpha 读到 [$amin,$amax]（夹具恒 0.5）—— 走 ffmpeg 解码会塌成 0"
  }
  if ($c.fmt -eq 'png') {
    $pf = PixFmt $p.FullName
    CK ($pf -match '^(rgba64|rgb48)') "② png 出口像素格式 = $pf（16-bit）⇒ 精度真的贯通到容器，不是在中间件就被打回 8-bit"
    $rawo = "$work/png_rgba.raw"
    Exec $ff ('-y -hide_banner -loglevel error -i "' + $p.FullName + '" -f rawvideo -pix_fmt rgba64le "' + $rawo + '"') "pngraw" | Out-Null
    if (Test-Path $rawo) {
      $b = [System.IO.File]::ReadAllBytes($rawo)
      $a0 = [System.BitConverter]::ToUInt16($b, 6)
      CK ([Math]::Abs($a0 - 32768) -le 1024) "② png 出口第 0 像素 alpha16 = $a0（夹具 0.5 ⇒ 期望 ≈32768）"
      $sat = 0; $n = $b.Length / 8
      # 阈值取 65500（= 真被钉死在白点）而不是 64000：后者会把"映到 0.98"也算满带，
      # 于是 Reinhard 档的降饱和被吞掉、对照臂失去分叉。
      for ($i = 0; $i -lt $n; $i++) { if ([System.BitConverter]::ToUInt16($b, $i * 8) -ge 65500) { $sat++ } }
      $defaultSat = $sat
      Write-Host ("    [满带 R 像素数 default 档] = $sat / $n")
    } else { CK $false "② png 无法解出 rgba64le 裸流（无法验 alpha/满带）" }
  }
}

# Reinhard 对照（同一个 half 夹具，两档各跑一次）
$dirTm = "$work/run_f16tm"; New-Item -ItemType Directory -Force -Path $dirTm | Out-Null
$rTm = Exec $exe ('--headless --log-level debug -i "' + "$work/hp_f16.jxr" + '" -o "' + $dirTm + '" --format png --color-tone-map reinhard') "run_f16tm"
$mTm = HpMetrics $rTm.text
$mapTm = "(无指标行)"
if ($null -ne $mTm) { $mapTm = [string]$mTm['map'] }
$pTm = Product $dirTm @(".png")
Write-Host ("  [Reinhard] exit=" + $rTm.code + " 指标=" + $(if ($null -ne $mTm) { ($mTm.Keys | Sort-Object | ForEach-Object { $_ + '=' + $mTm[$_] }) -join ' ' } else { "(无)" }))
CK (($rTm.code -eq 0) -and ($null -ne $mTm) -and ($mapTm -eq 'reinhard')) `
   "② `--color-tone-map reinhard` 确实被本通路消费（map=$mapTm，不是静默忽略）"
if ($null -ne $pTm) {
  $rawT = "$work/png_rgba_tm.raw"
  Exec $ff ('-y -hide_banner -loglevel error -i "' + $pTm.FullName + '" -f rawvideo -pix_fmt rgba64le "' + $rawT + '"') "tmraw" | Out-Null
  if (Test-Path $rawT) {
    $b = [System.IO.File]::ReadAllBytes($rawT); $n = $b.Length / 8; $satT = 0
    for ($i = 0; $i -lt $n; $i++) { if ([System.BitConverter]::ToUInt16($b, $i * 8) -ge 65500) { $satT++ } }
    Write-Host ("    [钉死白点的 R 像素] 默认档=$defaultSat  Reinhard档=$satT")
    CK (($defaultSat -eq $expectOver) -and ($satT -le [int]($defaultSat / 4))) `
       "② 对照臂有牙：钳位档 $defaultSat 个 R 钉死白点（==夹具奇数列 $($expectOver)），Reinhard 档只剩 $satT（≤ 1/4）⇒ 两档走的是不同分支"
  } else { CK $false "② Reinhard 档产物解不出裸流 ⇒ 对照无法判定" }
} else { CK $false "② Reinhard 档无产物 ⇒ 对照无法判定" }

# jxr 出口 + 第三方回读（JxrEncApp 读不了 16-bit TIFF/PNG ⇒ 该档走 ffmpeg libjxr）
$dirJ = "$work/run_jxr"; New-Item -ItemType Directory -Force -Path $dirJ | Out-Null
$rJ = Exec $exe ('--headless --log-level debug -i "' + "$work/hp_f16.jxr" + '" -o "' + $dirJ + '" --format jxr') "run_jxr"
$pJ = Product $dirJ @(".jxr")
Write-Host ("  [jxr 出口] exit=" + $rJ.code + " 产物=" + $(if ($null -ne $pJ) { $pJ.Name + ':' + $pJ.Length + 'B' } else { "(无)" }))
CK (($rJ.code -eq 0) -and ($null -ne $pJ)) "② JXR→JXR 高位档有产物（旧态是 -106 零产物）"
if ($null -ne $pJ) {
  $backTif = "$work/jxr_back.tif"
  $rd = Exec $jxrDec ('-i "' + $pJ.FullName + '" -o "' + $backTif + '" -c 22') "jxrback"
  CK (($rd.code -eq 0) -and (Test-Path $backTif)) "② 独立解码器 JxrDecApp 能解出该产物（第三方证明是真 JXR，不看自家日志）"
  $wic = "$work/wic_any.ps1"
@'
param([string]$Path)
Add-Type -AssemblyName PresentationCore
try {
  $d = [System.Windows.Media.Imaging.BitmapDecoder]::Create((New-Object System.Uri($Path)),
      [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
      [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
  $fr = $d.Frames[0]
  $fc = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($fr, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
  $buf = New-Object byte[] 16
  $fc.CopyPixels((New-Object System.Windows.Int32Rect 0, 0, 4, 1), $buf, 16, 0)
  Write-Output ("WICOK {0}x{1} a={2}" -f $fr.PixelWidth, $fr.PixelHeight, $buf[3])
} catch { Write-Output ("WICFAIL " + $_.Exception.Message) }
'@ | Set-Content -LiteralPath $wic -Encoding UTF8
  $wr = ExecOut powershell ('-NoProfile -ExecutionPolicy Bypass -File "' + $wic + '" -Path "' + $pJ.FullName + '"') "wic"
  if ($wr.text -match 'WICOK') {
    CK ($wr.text -match ("WICOK " + $W + "x" + $H)) "② Windows 自己的解码器也认这份 JXR（$($wr.text.Trim())）"
    if ($wr.text -match 'a=(\d+)') {
      $wa = [int]$Matches[1]
      CK (($wa -ge 120) -and ($wa -le 136)) "② WIC 回读的 alpha = $wa/255（夹具 0.5 ⇒ 期望 ≈128）⇒ 出口 alpha 与 OS 解码器同口径"
    } else { CK $false "② WIC 输出里没有 alpha 读数（无法对账）" }
  } else {
    Write-Host "  （WIC 回读不可用 ⇒ 本条登记未判定，不给绿灯：$($wr.text.Trim())）" -ForegroundColor Yellow
  }
}

# ── ②b 16-bit **整数**臂（-c 23）：像素必须原值搬运，不得当半浮点读、也不得再过一次 sRGB ──
# 为什么单列这一臂（2026-10-21 复核抓到的真缺陷）：`LoadBand` 曾把非 Float32 一律按 IEEE 半浮点读
#   ⇒ UInt16 臂的 32816/55/65315 变成 0/3/0（实测 maxdiff=65535）；修好读取后又发现它被
#   `FloatToSrgb16Scalar` **二次编码**（0.5007→0.7359，maxdiff=18817）。两处都只在**像素域**可见：
#   读数行（kind=UInt16、peak 正确）完全正常 ⇒ 只断言指标行会漏掉它。
Write-Host "`n### ②b 16-bit 整数臂：像素原值搬运（独立夹具值，逐字节对撞）###" -ForegroundColor Cyan
$rawU16 = "$work/rgba_u16.bin"
$fsU = [System.IO.File]::Create($rawU16); $bwU = New-Object System.IO.BinaryWriter $fsU
for ($y = 0; $y -lt $H; $y++) {
  for ($x = 0; $x -lt $W; $x++) {
    # 三个通道给互不相同的 16 位斜坡（含高位为 0 的样本）：半浮点误读与二次编码都会立刻炸开。
    $bwU.Write([uint16](($x * 1013) -band 0xFFFF))
    $bwU.Write([uint16](($y * 7919 + 0x0100) -band 0xFFFF))
    $bwU.Write([uint16]((0x1234 + $x * 17) -band 0xFFFF))
    $bwU.Write([uint16]65535)
  }
}
$bwU.Close(); $fsU.Close()
$u16Jxr = "$work/hp_u16.jxr"
$mkU = Exec $ff ('-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgba64le -s ' + $W + 'x' + $H + ' -i "' + $rawU16 + '" -c:v libjxr "' + $u16Jxr + '"') "mku16"
$pfU16 = PixFmt $u16Jxr
Write-Host ("  [夹具] hp_u16=" + $pfU16)
CK ($pfU16 -eq 'rgba64le') "②b 前置：整数夹具的 pix_fmt = rgba64le（实 $pfU16 ⇒ 才会选中 -c 23 臂）"
if ($pfU16 -eq 'rgba64le') {
  $outdirU = "$work/run_u16"
  New-Item -ItemType Directory -Force -Path $outdirU | Out-Null
  $rU = Exec $exe ('--headless --log-level debug -i "' + $u16Jxr + '" -o "' + $outdirU + '" --format png') "run_u16"
  $mU = HpMetrics $rU.text
  $pU = Product $outdirU @(".png")
  CK ($rU.code -eq 0 -and $null -ne $pU) "②b 整数臂：exit=0 且有产物（实 $($rU.code) / $(if ($null -ne $pU) {'有'} else {'无'})）"
  if ($null -ne $mU) {
    CK ($mU['arm'] -eq '23' -and $mU['kind'] -eq 'UInt16') "②b 走的是 -c 23 / UInt16 档（实 arm=$($mU['arm']) kind=$($mU['kind'])）"
    CK ($mU['map'] -eq 'int-copy') "②b 整数档 map=int-copy（实 $($mU['map'])）⇒ 不冒充做过 sRGB/Reinhard（已编码值只搬深度）"
  } else { CK $false "②b HP-METRICS 缺失 ⇒ 无读数缝" }
  if ($null -ne $pU) {
    $gotU = "$work/u16_got.raw"
    Exec $ff ('-y -hide_banner -loglevel error -i "' + $pU.FullName + '" -f rawvideo -pix_fmt rgba64le "' + $gotU + '"') "u16raw" | Out-Null
    if (Test-Path $gotU) {
      $a = [System.IO.File]::ReadAllBytes($rawU16); $b = [System.IO.File]::ReadAllBytes($gotU)
      $sameLen = ($a.Length -eq $b.Length)
      $maxDiff = -1; $diffCount = 0
      if ($sameLen) {
        for ($i = 0; $i -lt $a.Length; $i += 2) {
          $va = [int]$a[$i] -bor ([int]$a[$i + 1] -shl 8)
          $vb = [int]$b[$i] -bor ([int]$b[$i + 1] -shl 8)
          $d = [Math]::Abs($va - $vb); if ($d -gt $maxDiff) { $maxDiff = $d }; if ($d -ne 0) { $diffCount++ }
        }
      }
      CK ($sameLen -and $maxDiff -eq 0) "②b **像素逐样本相等**：UInt16 臂往返 maxdiff=$maxDiff、不同样本 $diffCount（期望 0/0 ⇒ 没被半浮点误读、没被二次 sRGB 编码）"
    } else { CK $false "②b 产物解不出裸流 ⇒ 像素判据无法执行" }
  }
}

# ── ③ 回归：8bit 源必须仍走 BMP 臂 ───────────────────────────────────────────────Write-Host "`n### ③ 回归：8-bit JXR 源不得被改道 ###" -ForegroundColor Cyan
$dirS = "$work/run_sdr8"; New-Item -ItemType Directory -Force -Path $dirS | Out-Null
$rS = Exec $exe ('--headless --log-level debug -i "' + "$work/sdr8.jxr" + '" -o "' + $dirS + '" --format jpg') "run_sdr8"
$pS = Product $dirS @(".jpg", ".jpeg")
Write-Host ("  [8bit] exit=" + $rS.code + " 产物=" + $(if ($null -ne $pS) { $pS.Name + ':' + $pS.Length + 'B' } else { "(无)" }))
CK (($rS.code -eq 0) -and ($null -ne $pS)) "③ 8bit JXR → jpg 仍 exit 0 且有产物"
CK (($rS.text -match '\[jxr\] Step 1: JxrDecApp') -and ($rS.text -notmatch 'HP-METRICS')) `
   "③ 8bit 档走的仍是**原 BMP 臂**且没有 HP 指标行（直通臂没有越界抢单）"

# ── ④ 设计前提锁 ─────────────────────────────────────────────────────────────────
Write-Host "`n### ④ 前提锁：为什么浮点中间件必须托管读，不能交给 ffmpeg ###" -ForegroundColor Cyan
$hpTif = Get-ChildItem -LiteralPath $work -Filter "hp28.tif" -ErrorAction SilentlyContinue
if ($null -eq $hpTif) {
  # 直接再造一份浮点 TIFF 中间件（JxrDecApp -c 28 的产物形态），不经产品
  $null = Exec $jxrDec ('-i "' + "$work/hp_f32.jxr" + '" -o "' + "$work/hp28.tif" + '" -c 28') "mkhp28"
  $hpTif = Get-ChildItem -LiteralPath $work -Filter "hp28.tif" -ErrorAction SilentlyContinue
}
if ($null -ne $hpTif) {
  $conv = Exec $ff ('-y -hide_banner -loglevel error -i "' + $hpTif.FullName + '" -frames:v 1 -pix_fmt rgb24 "' + "$work/prem.png" + '"') "premise"
  Write-Host ("    [ffmpeg 吃 rgbaf32le TIFF] exit=" + $conv.code + " 产物存在=" + (Test-Path "$work/prem.png"))
  CK (($conv.code -ne 0) -or (-not (Test-Path "$work/prem.png"))) `
     "④ P1 前提仍成立：ffmpeg/swscale **不能**把 rgbaf32le 中间件转出去（本通路因此必须托管读）。转绿=上游补了路径 ⇒ 本设计前提变了，要重审"
  # P1b：**半浮点 16-bit TIFF 是更危险的一档** —— ffmpeg 的 TIFF 解码器不认 SampleFormat=3+16bit，
  # 会**静默**按整数读（实测 R 均值 56 vs 真值 194 ⇒ 整幅暗 1.8 档，exit=0、无报错）。
  # ⇒ 判据不能只在"报错"上有牙，必须**禁止任何 ffmpeg 命令把这个中间件当输入**。
  $halfTif = "$work/prem_half.tif"
  $null = Exec $jxrDec ('-i "' + "$work/hp_f16.jxr" + '" -o "' + $halfTif + '" -c 25') "mkhalf"
  if (Test-Path $halfTif) {
    $hr = Exec $ff ('-y -hide_banner -loglevel error -i "' + $halfTif + '" -frames:v 1 -pix_fmt rgb24 "' + "$work/prem_half.png" + '"') "premisehalf"
    $pfHalf = PixFmt $halfTif
    Write-Host ("    [ffmpeg 吃 16bit-FP TIFF] exit=" + $hr.code + " 它读成的 pix_fmt=" + $pfHalf)
    CK (($hr.code -eq 0) -and ($pfHalf -match '^(rgba|bgra)64le$')) `
       "④ P1b 已实测确认：ffmpeg 把 16-bit FP TIFF **当成整数 64bpp 读**（exit=0 静默错读）⇒ 这条中间件永远不许交给 ffmpeg"
    $feed = @(Get-ChildItem -LiteralPath $work -Filter "run_*.out" -ErrorAction SilentlyContinue)
    $bad = 0; $cmdLines = 0
    foreach ($f in $feed) {
      $t = [System.IO.File]::ReadAllText($f.FullName)
      foreach ($ln in ($t -split "`r?`n")) {
        if ($ln -match '\[cmd\].*ffmpeg') {
          $cmdLines++
          if ($ln -match 'decoded_hp\d*\.tif') { $bad++ }
        }
      }
    }
    # ⚠ 缺席断言必须有正控：否则「0 命中」可能只是根本没跑到 ffmpeg 命令（空扫描恒真的那种假绿）。
    CK ($cmdLines -ge 3) "④ 扫描面里确实有 ffmpeg 命令行（实 $cmdLines 条）⇒ 上一条『0 命中』不是空扫描"
    CK ($bad -eq 0) "④ 产品日志里**没有任何** ffmpeg 命令把这个高位 TIFF 当输入（实命中 $bad 处 / 共 $cmdLines 条 ffmpeg 命令）⇒ 静默错读无路可走"
  } else { CK $false "④ 造不出 -c 25 半浮点 TIFF ⇒ P1b 无法判定" }
} else { CK $false "④ 造不出 -c 28 浮点 TIFF ⇒ 前提无法判定" }

# ── ⑤ 源码形态锁 ─────────────────────────────────────────────────────────────────
Write-Host "`n### ⑤ 源码形态锁 ###" -ForegroundColor Cyan
$hp = [System.IO.File]::ReadAllText($hpFile)
$simd = [System.IO.File]::ReadAllText($simdFile)
$qp = [System.IO.File]::ReadAllText($qpFile)
$dupConst = 0
foreach ($pat in @('1\.055', '0\.055', '0\.0031308', '1f / 2\.4')) { if ($hp -match $pat) { $dupConst++ } }
CK ($dupConst -eq 0) "⑤ 本通路**不自带**第二份 sRGB 曲线常数（命中 $dupConst 处；常数唯一来源 = SimdPixelOps）"
CK (($hp -match 'SimdPixelOps\.FloatToSrgb16Scalar') -and (($simd -split 'ushort FloatToSrgb16Scalar').Count -eq 2)) `
   "⑤ 16-bit 编码核存在且只此一份（SimdPixelOps.FloatToSrgb16Scalar 定义 1 处、被调用 ≥1 处）"
# ⚠ 判据必须带**发射点上下文**且要指名到那句谎的形状（`解码成功 ("…转换完成")`）：
#    裸串 `转换完成` 会命中本文件自己的引文，也会命中 `[preconv] 转换完成` 这种**无关的合法日志**
#    （实测踩过：初版正则就是把 :3517 那条打成的红）。
CK ($qp -notmatch '解码成功 \("[^"]*转换完成') `
   "⑤ 旧谎言的**发射点**（把 -c 0 出口断言成『scRGB→sRGB 转换完成』的日志行）已撤下"
CK ((($qp -match 'Arm64RgbAHalf') -or ($hp -match 'Arm64RgbAHalf')) -and ($hp -match 'Arm128RgbaFloat') `
    -and ($hp -match 'Arm64RgbaInt') -and ($hp -match 'Arm48RgbInt')) `
   "⑤ 四条直通臂常量都在（防被删空导致 ChoosePassthroughArm 恒 null ⇒ 空转假绿）"
CK ($qp -match 'HP-METRICS') "⑤ 指标行由 QueueProcessor 拼接（门禁吃的那条缝有生成点）"

# ── ⑥ mtime + 残留 ───────────────────────────────────────────────────────────────
Write-Host "`n### ⑥ 被测 exe mtime 与残留自证 ###" -ForegroundColor Cyan
$exeMtimeAfter = (Get-Item $exe).LastWriteTimeUtc
CK ($exeMtimeBefore -eq $exeMtimeAfter) "⑥ 被测 exe 未被中途重建（$exe）"
if ($fail -eq 0) {
  try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
  CK (-not (Test-Path $work)) "⑥ 运行级目录已清零：$work"
} else {
  Write-Host "  （有失败 ⇒ 保留目录供排查：$work）" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "===== jxr-highbitdepth: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($fail -gt 0) { exit 1 } else { exit 0 }
