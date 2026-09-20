# verify-jxl-codestream-route.ps1 —— JXL **裸码流**输入的路由门禁（2026-09-18 新增）
#
# 缺陷（本门禁要锁住的东西）：
#   `JxlInspector.DetectType` 里裸码流的 magic 判定（`head[0]==0xFF && head[1]==0x0A`）原本写在
#   `!ReadExact(fs, head, 12)` 那一支里 ⇒ **只对 < 12 字节的文件可达**。而 1×1 的 JXL 裸码流就有 60 B
#   ⇒ 该支是**死代码**：真实裸码流落到容器检查（字节 4..8 不是 "JXL "）⇒ 判 `Unknown`。
#   后果 = **静默换编码器**：用户显式 `--format jxl --encoder cjxl` / `--format jpg --encoder cjpegli`
#   被忽略，实际跑 ffmpeg 的 libjxl / mjpeg（产物本身是对的，所以用户看不出来）。
#   ⚠ **可达性放大**：cjxl 的 `--container` 默认 0（不写容器），而 app 的 cjxl 出口不传 `--container`
#   ⇒ **app 自己产出的 .jxl 就是裸码流** ⇒ app 的产物回灌时也拿不到 app 自己的 cjxl 出口。
#
# 修复：把 magic 检查挪到长度检查**之前**（先读 2 B 判 `FF 0A`，再判 `full12`）。
#
# 实测（2026-09-18，本机，两侧都取过数；固定二进制快照，跑前后 mtime 一致）：
#   命令                                             | 修前                                    | 修后
#   -------------------------------------------------|-----------------------------------------|----------------------------------
#   裸码流 + `--format jxl --encoder cjxl`           | `[cmd] ffmpeg`（libjxl）8549 B          | `[cjxl] JPEG XL encoder` 9290 B
#   裸码流 + `--format jpg --encoder cjpegli`        | `[cmd] ffmpeg` 24142 B / profile=Baseline | `[cjpegli]` 管道 11684 B / profile=Progressive
#   容器 .jxl + `--format jxl --encoder cjxl`（负控） | `已完成 (djxl→cjxl 管道)` 9290 B        | 同左（**逐字不变**）
#   jbrd 容器 → jpg（负控）                          | `已完成 (djxl 重构 JPEG)` 23875 B       | 同左（**逐字不变**）
#
# ⚠ **验收条目的修正（实测）**：任务里写的「`--format jpg --encoder cjpegli` ⇒ 产物 `codec_name` 不得是
#   `mjpeg`」**不可达** —— cjpegli 本身就是 JPEG 编码器，其产物 ffprobe **恒报 `mjpeg`**（修前修后都是）。
#   真实可判别的 ffprobe 差异是 `profile`：**修前 `Baseline` / 修后 `Progressive`**。
#   ⇒ 本门禁用 `profile=Progressive` + 我方稳定终态串「已完成 (cjpegli 管道)」+「不得出现 [cmd] ffmpeg」上锁。
#
# 素材自备（不依赖别的门禁的遗留产物；全部现造）：
#   src.png              ffmpeg -f lavfi -i "testsrc2=s=512x384" -frames:v 1
#   self_photo.jpg       ffmpeg -f lavfi -i "testsrc2=s=512x384" -frames:v 1 -q:v 2
#   codestream_lossy.jxl cjxl src.png out.jxl -q 90                     # 默认不写容器 ⇒ 裸码流（head FF 0A）
#   container_lossy.jxl  cjxl src.png out.jxl -q 90 --container=1       # 容器（head 00 00 00 0C 'JXL '）
#   jbrd.jxl             cjxl self_photo.jpg out.jxl --lossless_jpeg=1  # 容器 + jbrd
#
# 断言分组：① 素材形态自检（含「裸码流 ≥12 B」这一死分支判据）② 正例×3 ③ 负控×2 ④ 降级路径 ⑤ 二进制新鲜度 ⑥ 残留自证
# 退出码：0 = 全绿，1 = 有失败。⚠ 结尾必须是**完整 if/else**（§6 第 67 条：清理步会污染 `$LASTEXITCODE`）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（用 「」）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$env:FFMPEGGUI_PLAN_DIR = "$root/publish/PLAN"

# ⚠ 只用 Release 产物：**不做 Debug 回退** —— 本门禁是行为断言（「产物换了编码器」），
#   用异构/陈旧二进制跑出来的读数不可信，宁可拒绝跑。
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$cjxl = "$root/publish/PLAN/jxl/bin/cjxl.exe"
$jxlinfo = "$root/publish/PLAN/jxl/bin/jxlinfo.exe"

# ⚠ 运行级隔离（GUID）：固定 `$work` 会被并发的同名实例互删 ⇒ 假红（§6 第 66 条）。
$val = "$root/tests/output/validate"
$gid = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = "$val/jxlcs_${gid}"
New-Item -ItemType Directory -Force -Path $work | Out-Null

$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Host "PASS $msg" -ForegroundColor Green }
  else     { $script:fail++; Write-Host "FAIL $msg" -ForegroundColor Red }
}

# 取子进程输出一律走 Start-Process + 重定向（`& exe` 在部分宿主静默失败 ⇒ 空输出 ⇒ 假红）。
function Exec([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e)) }
}

# 跑一次 headless 队列，返回 @{ code; text; dir; prod; prodPath }
function RunCli([string]$tag, [string]$inFile, [string]$extra) {
  $outDir = "$work/out_$tag"
  if (Test-Path $outDir) { [System.IO.Directory]::Delete($outDir, $true) }
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  $r = Exec $exe "--headless --log-level debug -i `"$inFile`" -o `"$outDir`" $extra" "run_$tag"
  $prod = Get-ChildItem $outDir -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
  $pp = ""
  if ($null -ne $prod) { $pp = $prod.FullName }
  return @{ code = $r.code; text = $r.text; dir = $outDir; prod = $prod; prodPath = $pp }
}

# ffprobe：codec_name / profile / WxH / 帧数（帧数走 -count_frames，**不用** frame= 日志字段）
function ImgMeta([string]$path) {
  if (($path -eq "") -or (-not (Test-Path $path))) { return @{ raw = ""; codec = ""; profile = ""; size = ""; frames = -1 } }
  $r = Exec $fp "-v error -select_streams v:0 -count_frames -show_entries stream=codec_name,profile,width,height,nb_read_frames -of csv=p=0 `"$path`"" "probe_$(Split-Path $path -Leaf)"
  $raw = ""
  foreach ($ln in ($r.text -split "`r?`n")) {
    $t = $ln.Trim()
    if ($t -match '^[A-Za-z0-9_]*,\s*[^,]*,\s*\d+\s*,\s*\d+\s*,\s*\d+$') { $raw = $t }
  }
  $codec = ""; $profile = ""; $size = ""; $frames = -1
  if ($raw -ne "") {
    $f = $raw -split ','
    $codec = $f[0].Trim(); $profile = $f[1].Trim(); $size = ($f[2].Trim() + "x" + $f[3].Trim())
    $n = 0
    if ([int]::TryParse($f[4].Trim(), [ref]$n)) { $frames = $n }
  }
  return @{ raw = $raw; codec = $codec; profile = $profile; size = $size; frames = $frames }
}

# 前 N 字节的十六进制（素材形态自检用）
function HeadHexN([string]$path, [int]$n) {
  if ((-not (Test-Path $path)) -or ($path -eq "")) { return "<缺文件>" }
  $b = [System.IO.File]::ReadAllBytes($path)
  if ($b.Length -lt $n) { return ("<短文件:" + $b.Length + "B>") }
  return (($b[0..($n - 1)] | ForEach-Object { $_.ToString('X2') }) -join ' ')
}
function Sha256Of([string]$path) {
  if (($path -eq "") -or (-not (Test-Path $path))) { return "" }
  return (Get-FileHash $path -Algorithm SHA256).Hash
}
function Sha8([string]$h) { if ($h.Length -ge 16) { return $h.Substring(0, 16) } return "<无>" }

$missing = @()
foreach ($need in @($exe, $ff, $fp, $cjxl, $jxlinfo)) { if (-not (Test-Path $need)) { $missing += $need } }
if ($missing.Count -gt 0) {
  Write-Host "缺工具/产物（本门禁不做 Debug 回退，也不静默给绿）：" -ForegroundColor Red
  $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
  exit 1
}

$mtBefore = (Get-Item $exe).LastWriteTime
Write-Host "exe=$exe"
Write-Host "exe-mtime=$mtBefore"

# ── ① 素材自备 + 形态自检 ────────────────────────────────────────────────────────
Write-Host "`n### ① 自备素材 + 形态自检 ###" -ForegroundColor Cyan
$srcPng = "$work/src.png"; $srcJpg = "$work/self_photo.jpg"
$genPng = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=512x384`" -frames:v 1 `"$srcPng`"" "gen_png"
$genJpg = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=512x384`" -frames:v 1 -q:v 2 `"$srcJpg`"" "gen_jpg"
CK ((Test-Path $srcPng) -and ((Get-Item $srcPng).Length -gt 1000)) "源 PNG 已生成（exit=$($genPng.code)）"
CK ((Test-Path $srcJpg) -and ((Get-Item $srcJpg).Length -gt 1000)) "源 JPEG 已生成（exit=$($genJpg.code)）"

$cs = "$work/codestream_lossy.jxl"; $ct = "$work/container_lossy.jxl"; $jb = "$work/jbrd.jxl"
$g1 = Exec $cjxl "`"$srcPng`" `"$cs`" -q 90" "cj_cs"
$g2 = Exec $cjxl "`"$srcPng`" `"$ct`" -q 90 --container=1" "cj_ct"
$g3 = Exec $cjxl "`"$srcJpg`" `"$jb`" --lossless_jpeg=1" "cj_jb"
CK ((Test-Path $cs) -and ($g1.code -eq 0)) "裸码流素材已生成（cjxl 默认不写容器；exit=$($g1.code)）"
CK ((Test-Path $ct) -and ($g2.code -eq 0)) "容器素材已生成（--container=1；exit=$($g2.code)）"
CK ((Test-Path $jb) -and ($g3.code -eq 0)) "jbrd 素材已生成（--lossless_jpeg=1；exit=$($g3.code)）"

$csLen = 0; if (Test-Path $cs) { $csLen = (Get-Item $cs).Length }
$csHead = HeadHexN $cs 12
Write-Host "  [素材] 裸码流 head= $csHead  长度= $csLen B"
# ★ 死分支的判据：裸码流必须 **≥12 B** —— 修前正因为它 ≥12 B 才落进容器检查而判 Unknown。
CK ($csLen -ge 12) "★ 裸码流素材长度 ≥ 12 B（实 $csLen B）—— 这正是修前那条 magic 支**不可达**的原因"
CK ((HeadHexN $cs 3) -eq "FF 0A F8") "裸码流 head 以 FF 0A 开头（实 $(HeadHexN $cs 3)）"
CK ((HeadHexN $ct 12) -eq "00 00 00 0C 4A 58 4C 20 0D 0A 87 0A") "容器素材 head == ISOBMFF 签名（实 $(HeadHexN $ct 12)）"
$ji = Exec $jxlinfo "`"$jb`"" "ji"
CK ($ji.text -match 'bitstream reconstruction data available') "jbrd 素材经 jxlinfo 确认含重构数据（第三方证据）"
CK ((ImgMeta $cs).size -eq "512x384") "裸码流素材尺寸 512x384（实 $((ImgMeta $cs).size)）"

# ── ② 正例1：裸码流 + --format jxl --encoder cjxl ⇒ 必须走 cjxl 出口 ────────────
Write-Host "`n### ② 正例1：裸码流 + jxl + cjxl ###" -ForegroundColor Cyan
$rA = RunCli "A_cs_cjxl" $cs "--format jxl --encoder cjxl"
CK ($rA.code -eq 0) "② 退出码 0（实 $($rA.code)）"
CK ($rA.text -match '\[cjxl\] JPEG XL encoder') "② 日志出现 cjxl 自己的 banner「[cjxl] JPEG XL encoder」"
CK ($rA.text -notmatch '\[cmd\] ffmpeg') "② 日志**没有**「[cmd] ffmpeg」（修前的回退指纹）"
CK ($rA.text -match '已完成 \(djxl→cjxl 管道\)') "② 终态为「已完成 (djxl→cjxl 管道)」（我方稳定字符串）"
$mA = ImgMeta $rA.prodPath
$lenA = 0; if ($null -ne $rA.prod) { $lenA = $rA.prod.Length }
Write-Host "  [产物] $($mA.raw)  $lenA B"
CK ($mA.codec -eq "jpegxl") "② 产物 codec_name == jpegxl（实 $($mA.codec)）"

# ── ③ 正例2：裸码流 + --format jpg --encoder cjpegli ⇒ 必须走 cjpegli 管道 ──────
Write-Host "`n### ③ 正例2：裸码流 + jpg + cjpegli ###" -ForegroundColor Cyan
$rB = RunCli "B_cs_cjpegli" $cs "--format jpg --encoder cjpegli"
CK ($rB.code -eq 0) "③ 退出码 0（实 $($rB.code)）"
CK ($rB.text -match '\[cjpegli\] Encoding') "③ 日志出现 cjpegli 自己的编码行「[cjpegli] Encoding」"
CK ($rB.text -notmatch '\[cmd\] ffmpeg') "③ 日志**没有**「[cmd] ffmpeg」（修前的回退指纹）"
CK ($rB.text -match '已完成 \(cjpegli 管道\)') "③ 终态为「已完成 (cjpegli 管道)」（我方稳定字符串）"
$mB = ImgMeta $rB.prodPath
$lenB = 0; if ($null -ne $rB.prod) { $lenB = $rB.prod.Length }
Write-Host "  [产物] $($mB.raw)  $lenB B"
# ⚠ 这里**不能**用 codec_name 判别（cjpegli 产物就是 JPEG ⇒ 恒为 mjpeg，修前修后相同）；
#   真实可判别的 ffprobe 属性是 profile：修前 Baseline / 修后 Progressive（实测）。
CK ($mB.codec -eq "mjpeg") "③ 产物确是 JPEG（codec_name=mjpeg；⚠ 这条**不是**判别项，只是形态自检）"
CK ($mB.profile -eq "Progressive") "③ 产物 profile == Progressive（修前是 Baseline ⇒ 这是可判别的差异；实 $($mB.profile)）"

# ── ④ 正例3：app 自产 .jxl 回灌 ⇒ 必须走到 app 自己的 cjxl 出口 ────────────────
Write-Host "`n### ④ 正例3：app 自产 .jxl 回灌 ###" -ForegroundColor Cyan
$rC1 = RunCli "C1_make_jxl" $srcPng "--format jxl --encoder cjxl"
CK (($rC1.code -eq 0) -and ($rC1.prodPath -ne "")) "④ app 产出 .jxl 成功（exit=$($rC1.code)）"
$madeJxl = "$work/app_made.jxl"
if ($rC1.prodPath -ne "") { Copy-Item $rC1.prodPath $madeJxl -Force }
$madeHead = HeadHexN $madeJxl 3
CK ($madeHead -eq "FF 0A F8") "④ ★ app 自产的 .jxl 是**裸码流**（head= $(HeadHexN $madeJxl 12)）⇒ 可达性放大成立"
$rC2 = RunCli "C2_reingest" $madeJxl "--format jxl --encoder cjxl"
CK ($rC2.code -eq 0) "④ 回灌退出码 0（实 $($rC2.code)）"
CK ($rC2.text -match '\[cjxl\] JPEG XL encoder') "④ 回灌时走到 app 的 cjxl 出口（出现 cjxl banner）"
CK ($rC2.text -notmatch '\[cmd\] ffmpeg') "④ 回灌时**没有**落回 ffmpeg"
CK ($rC2.text -match '已完成 \(djxl→cjxl 管道\)') "④ 回灌终态为「已完成 (djxl→cjxl 管道)」"

# ── ⑤ 负控1：容器 .jxl 行为不得变（仍走 djxl→cjxl 管道）────────────────────────
Write-Host "`n### ⑤ 负控1：容器 .jxl（行为逐字不变）###" -ForegroundColor Cyan
$rD = RunCli "D_container_cjxl" $ct "--format jxl --encoder cjxl"
CK ($rD.code -eq 0) "⑤ 退出码 0（实 $($rD.code)）"
CK ($rD.text -match '\[jxl\] 输入类型: 原生 JXL 码流') "⑤ 容器仍判「原生 JXL 码流」（判型未被 magic 前置改动带偏）"
CK ($rD.text -match '\[cjxl\] JPEG XL encoder') "⑤ 仍走 cjxl 出口"
CK ($rD.text -match '已完成 \(djxl→cjxl 管道\)') "⑤ 终态仍为「已完成 (djxl→cjxl 管道)」"
CK ($rD.text -notmatch '\[cmd\] ffmpeg') "⑤ 仍不落 ffmpeg"
$mD = ImgMeta $rD.prodPath
$lenD = 0; if ($null -ne $rD.prod) { $lenD = $rD.prod.Length }
Write-Host "  [产物] $($mD.raw)  $lenD B"
# ★ 收敛锁：修好后「裸码流」与「容器」走**同一条** djxl→cjxl 路径、同像素同参数 ⇒ 产物应**逐字节相同**。
#   若哪天 magic 前置又把两者分叉（或容器路径被改坏），这条会立刻红。
$hA = Sha256Of $rA.prodPath; $hD = Sha256Of $rD.prodPath
CK (($hA -ne "") -and ($hA -eq $hD)) "⑤ ★ 裸码流产物与容器产物 SHA256 相同（两者已收敛到同一路径；$((Sha8 $hA)) vs $((Sha8 $hD))）"

# ── ⑥ 负控2：jbrd 容器仍判 JpegReconstruction（不得把 NativeCodestream 判过头）──
Write-Host "`n### ⑥ 负控2：jbrd 容器仍判 JPEG 重构 ###" -ForegroundColor Cyan
$rE = RunCli "E_jbrd" $jb "--format jpg"
CK ($rE.code -eq 0) "⑥ 退出码 0（实 $($rE.code)）"
CK ($rE.text -match '\[jxl\] 输入类型: JPEG 重构') "⑥ 仍判「JPEG 重构」（jbrd 优先）"
CK ($rE.text -notmatch '\[jxl\] 输入类型: 原生 JXL 码流') "⑥ **没有**被误判成「原生 JXL 码流」（不得判过头）"
CK ($rE.text -match '已完成 \(djxl 重构 JPEG\)') "⑥ 终态仍为「已完成 (djxl 重构 JPEG)」（无损还原，不解码像素）"

# ── ⑦ 降级路径：裸码流 + djxl 不可用 ⇒ 必须响亮且点名原因，不得假装用了外部编码器 ──
Write-Host "`n### ⑦ 降级：裸码流 + djxl 不可用 ###" -ForegroundColor Cyan
# 模拟法：把 PLAN 目录指向空目录（`FFMPEGGUI_PLAN_DIR` 在 PlanFolderPath 里**优先于**程序目录），
# 并把 `FFMPEGGUI_JXL_LIB_DIR` 也指向空目录（DjxlService 的 ① 手动路径分支）。
$emptyPlan = "$work/emptyplan"; $emptyJxl = "$work/emptyjxl"
New-Item -ItemType Directory -Force -Path $emptyPlan | Out-Null
New-Item -ItemType Directory -Force -Path $emptyJxl | Out-Null
$oldJxlLib = $env:FFMPEGGUI_JXL_LIB_DIR
$env:FFMPEGGUI_PLAN_DIR = $emptyPlan
$env:FFMPEGGUI_JXL_LIB_DIR = $emptyJxl
$rF = RunCli "F_cs_nodjxl" $cs "--format jpg --encoder cjpegli"
$env:FFMPEGGUI_PLAN_DIR = "$root/publish/PLAN"
if ($null -eq $oldJxlLib) { Remove-Item Env:FFMPEGGUI_JXL_LIB_DIR -ErrorAction SilentlyContinue }
else { $env:FFMPEGGUI_JXL_LIB_DIR = $oldJxlLib }
# ★ 先自检「模拟真的生效」——否则这条降级断言会**真空绿**
CK ($rF.text -match 'djxl: 不可用') "⑦ 自检：日志确实报「djxl: 不可用」（模拟生效，否则本条是真空绿）"
CK ($rF.text -match '\[djxl\] 未检测到 djxl') "⑦ 降级**响亮**：日志明确打出「[djxl] 未检测到 djxl」"
CK ($rF.text -match '\[cmd\] ffmpeg') "⑦ 降级目标明确：确实回退到 ffmpeg（有 [cmd] ffmpeg 行）"
CK ($rF.text -notmatch '已完成 \(cjpegli 管道\)') "⑦ **不得**假装用了外部编码器（终态不得是「已完成 (cjpegli 管道)」）"
CK ($rF.text -notmatch '已完成 \(djxl→cjxl 管道\)') "⑦ 同上：终态也不得是 djxl→cjxl 管道"
$st = ($rF.text -split "`r?`n" | Select-String -Pattern '已完成 \(' | Select-Object -Last 1)
$stLine = "<未捕获>"
if ($null -ne $st) { $stLine = $st.Line.Trim() }
Write-Host "  [降级] 终态=$stLine"

# ── ⑧ 二进制新鲜度（跑期间 exe 被换 ⇒ 本轮读数作废）────────────────────────────
Write-Host "`n### ⑧ 二进制新鲜度 ###" -ForegroundColor Cyan
$mtAfter = (Get-Item $exe).LastWriteTime
CK ($mtBefore -eq $mtAfter) "⑧ 跑期间 FfmpegGui.exe mtime 未变（前=$mtBefore 后=$mtAfter；变了则本轮读数作废）"

# ── ⑨ 残留自证 ────────────────────────────────────────────────────────────────
Write-Host "`n### ⑨ 残留自证 ###" -ForegroundColor Cyan
# 清理一律用 .NET API 绕过宿主批量删除守卫（§6 第 57/67 条：守卫会抛 terminating error 并污染 $LASTEXITCODE）。
try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
CK (-not (Test-Path $work)) "⑨ 运行级目录已清零：$work"

Write-Host ""
Write-Host "===== jxl-codestream-route: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($fail -gt 0) { exit 1 } else { exit 0 }
