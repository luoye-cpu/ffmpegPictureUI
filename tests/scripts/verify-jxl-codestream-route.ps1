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

# 只读 stdout 版（取「工具的值」专用，返回形态与 Exec 一致 ⇒ `.code` 判据不受影响）：
#   ffprobe 的 `-of csv` 字段值走 **stdout**，合并 stderr 会把报错行当值喂给 ImgMeta 的 codec/profile/
#   尺寸/帧数解析（本机实测 ffprobe 失败时 stdout 0 B、stderr 全是 `moov atom not found` + 带数字路径）。
#   产品 headless 日志（RunCli 的 `Exec $exe`）与 ffmpeg/cjxl 的报错**两条流都要看** ⇒ 那里的合并是故意的。
function ExecOut([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = [System.IO.File]::ReadAllText($o) }
}

# 跑一次 headless 队列，返回 @{ code; text; dir; prod; prodPath }
function RunCli([string]$tag, [string]$inFile, [string]$extra) {
  $outDir = "$work/out_$tag"
  if (Test-Path $outDir) { [System.IO.Directory]::Delete($outDir, $true) }
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  $r = Exec $exe "--headless --log-level debug -i `"$inFile`" -o `"$outDir`" $extra" "run_$tag"
  # ⚠ 计数要**全枚举**：`Select-Object -First 1` 之后拿到的永远 ≤1，"0 产物"与"≥2 产物"会一起被读成 1。
  #   `prod` 仍取第一个（既有的 ② ~ ⑥ 段按它取路径，语义不变），新增的 `prodCount` 供 ⑪ 段判"干净拒绝"。
  $prods = @(Get-ChildItem $outDir -Recurse -File -ErrorAction SilentlyContinue)
  $prod = $null
  if ($prods.Count -ge 1) { $prod = $prods[0] }
  $pp = ""
  if ($null -ne $prod) { $pp = $prod.FullName }
  return @{ code = $r.code; text = $r.text; dir = $outDir; prod = $prod; prodPath = $pp; prodCount = $prods.Count }
}

# ffprobe：codec_name / profile / WxH / 帧数（帧数走 -count_frames，**不用** frame= 日志字段）
function ImgMeta([string]$path) {
  if (($path -eq "") -or (-not (Test-Path $path))) { return @{ raw = ""; codec = ""; profile = ""; size = ""; frames = -1 } }
  $r = ExecOut $fp "-v error -select_streams v:0 -count_frames -show_entries stream=codec_name,profile,width,height,nb_read_frames -of csv=p=0 `"$path`"" "probe_$(Split-Path $path -Leaf)"
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
# jxlinfo 的判定文本走 stdout（本机实测：stdout 有内容、stderr 空）⇒ 同一条判据改用只读取数。
# ⚠ 这一类不在扫描器的变量白名单里（它只认 $et/$exif*/$fp/$ffprobe*），扫描器看不见 ⇒ 顺手收掉。
$ji = ExecOut $jxlinfo "`"$jb`"" "ji"
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
# 把 `FFMPEGGUI_JXL_LIB_DIR` 也指向空目录（DjxlService 的 ① 手动路径分支），
# 并且**必须**再用 `FFMPEGGUI_EXT_SEARCH_DIRS` 收掉探测的第 ④ 级（系统扩展搜索根）。
# ⚠⚠ 2026-09-22 实测：漏掉第 ④ 级时本条**必红**（而且红得对）—— ④ 会把 `C:\Program Files`
#   连根递归，本机命中别的包自带的私有 `WindowsApps\...\djxl.exe` ⇒ `djxl: 可用` ⇒
#   「模拟真的生效」自检按设计转红。该文件实测 Access Denied 起不来（产品侧已改为
#   "起不来就不算可用"），但**降级判据不能赌本机装过什么应用** ⇒ 三级一起置空才是"工具不存在"。
$emptyPlan = "$work/emptyplan"; $emptyJxl = "$work/emptyjxl"; $emptyExt = "$work/emptyext"
New-Item -ItemType Directory -Force -Path $emptyPlan | Out-Null
New-Item -ItemType Directory -Force -Path $emptyJxl | Out-Null
New-Item -ItemType Directory -Force -Path $emptyExt | Out-Null
$oldJxlLib = $env:FFMPEGGUI_JXL_LIB_DIR
$oldExtDirs = $env:FFMPEGGUI_EXT_SEARCH_DIRS
$env:FFMPEGGUI_PLAN_DIR = $emptyPlan
$env:FFMPEGGUI_JXL_LIB_DIR = $emptyJxl
$env:FFMPEGGUI_EXT_SEARCH_DIRS = $emptyExt
$rF = RunCli "F_cs_nodjxl" $cs "--format jpg --encoder cjpegli"
$env:FFMPEGGUI_PLAN_DIR = "$root/publish/PLAN"
if ($null -eq $oldJxlLib) { Remove-Item Env:FFMPEGGUI_JXL_LIB_DIR -ErrorAction SilentlyContinue }
else { $env:FFMPEGGUI_JXL_LIB_DIR = $oldJxlLib }
if ($null -eq $oldExtDirs) { Remove-Item Env:FFMPEGGUI_EXT_SEARCH_DIRS -ErrorAction SilentlyContinue }
else { $env:FFMPEGGUI_EXT_SEARCH_DIRS = $oldExtDirs }
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
# ── ⑩ 编码侧（2026-09-30 补）：app 自己有没有真的发出 --lossless_jpeg=1 ──────────────
# 排在 ⑨ 那段「运行级目录清零」之前（⑨ 是 teardown，不是编号顺序）。
# 此前**全仓零覆盖**：`--lossless_jpeg` 在 95 个门禁脚本里只出现在"手工调 cjxl 造素材"的地方，
# 没有任何一条判据问过 **app 发没发** ⇒ 它被三道独立失效永久掐死（模型默认 false + 采集式
# 「面板不可见⇒false」+ 与一只**默认勾着**的「保留 Ultra HDR 增益图」无条件相与）却全绿。
# 掐死态实测：默认产物是 `-d 3.8` 的**有损**像素重编码（893 kB，源 JPEG 3,209 kB）⇒ 用户以为拿到无损。
$jbJpg = "$work/enc_src.jpg"
$null = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i gradients=s=320x240:c0=0x305090:c1=0xd0a040:n=6,format=rgb24 -frames:v 1 -q:v 3 $jbJpg" "jbgen"
if (-not (Test-Path $jbJpg)) { CK $false "⑩ JPEG 素材生成失败 ⇒ 本段判据全部作废" }
else {
  $jbSrcKB = (Get-Item $jbJpg).Length
  $jbOut = "$work/jb_on"; New-Item -ItemType Directory -Force -Path $jbOut | Out-Null
  $jb = Exec $exe "--headless -i $jbJpg --format jxl --output $jbOut --log-level Debug" "jbrd"
  $jbFile = @(Get-ChildItem $jbOut -Recurse -File | Where-Object { $_.Extension -eq '.jxl' })
  CK ($jbFile.Count -eq 1) "⑩ 默认路径产出恰好一个 .jxl（实得 $($jbFile.Count)）"
  if ($jbFile.Count -eq 1) {
    # 取 jxlinfo 的**值**必须走只读 stdout 版：合并取数会把 stderr 的报错行当成值喂给判据
    #   （`_probe-stderr-merge-scan.ps1` 的债账基线是 0 ⇒ 这里用 Exec 会当场报红）。
    $jbInfo = (ExecOut $jxlinfo ('"' + $jbFile[0].FullName + '"') "jbinfo").text
    CK ($jb.text -match '--lossless_jpeg=1') `
       "⑩ app **默认**就发出 --lossless_jpeg=1（开关默认启用，不再被采集式掐死）"
    CK ($jbInfo -match 'reconstruction data') `
       "⑩ 产物真是 jbrd 比特流重建而非像素重编码（jxlinfo 独立读回）"
    CK ($jbFile[0].Length -lt $jbSrcKB) `
       "⑩ 重封装产物小于源 JPEG（$($jbFile[0].Length) B < $jbSrcKB B）"
  }
  # 负控：显式关掉 ⇒ 不再发 --lossless_jpeg=1（此时引擎不再被 `ImageEncoderArgs` 拦，回到管道像素路线，
  # 所以判据只能是"没有 =1"，不是"必须有 =0"——管道输入的 isJpegInput 恒 false，本就不该发任何一侧）。
  $jbOff = "$work/jb_off"; New-Item -ItemType Directory -Force -Path $jbOff | Out-Null
  $r2 = Exec $exe "--headless -i $jbJpg --format jxl --output $jbOff --jxl-lossless-jpeg false --log-level Debug" "jboff"
  CK ($r2.text -notmatch '--lossless_jpeg=1') `
     "⑩ 负控：显式 --jxl-lossless-jpeg false ⇒ 不再发 --lossless_jpeg=1（证明上面三条不是恒发造成的假绿）"
  # 负控：非 JPEG 输入不得被这个默认值误挡出色彩引擎（ImageEncoderArgs 只看布尔、看不到输入格式）
  $jbPng = "$work/enc_src.png"
  $null = Exec $ff "-y -hide_banner -loglevel error -i $jbJpg -frames:v 1 $jbPng" "jbpng"
  $jbP = "$work/jb_png"; New-Item -ItemType Directory -Force -Path $jbP | Out-Null
  $r3 = Exec $exe "--headless -i $jbPng --format jxl --output $jbP --log-level Debug" "jbpngrun"
  CK ($r3.text -match 'cjxl 命令行' -and $r3.text -notmatch '--lossless_jpeg=1') `
     "⑩ 负控：PNG 输入仍走引擎 cjxl 出口、不被无损重封装误拦（有效值已与「输入是 JPEG」相与）"
  # 结构锁：JPEG 与 JPEG XL 两格**共用同一只控件**（各放一只 = 两个真值，是本仓反复修的形状）
  $xamlSrc = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/MainWindow.xaml")
  $nChk = [regex]::Matches($xamlSrc, 'x:Name="JxlLosslessJpegCheck"').Count
  $nPnl = [regex]::Matches($xamlSrc, 'x:Name="JxlLosslessJpegPanel"').Count
  CK ($nChk -eq 1 -and $nPnl -eq 1) `
     "⑩ 结构锁：那只开关在 XAML 里恰好一份（chk=$nChk panel=$nPnl；出现 2 就是两个真值）"
}

# ── ⑪ jbrd × 引擎拦截格（EncoderUnsupportedSetting）：2026-09-30 补，本格此前**从未被真跑驱动** ──
# 为什么这一格需要判据：`_lib-reject.ps1` 的登记表里它只活在 `-Precondition` 的描述串中，
#   而全库的实跑夹具只有 webp `--chroma 4:4:4` 那一对 ⇒ 「引擎拒绝 jbrd」这件事没有一条读数。
#   更要紧的是它与 09-30 那次默认翻转直接耦合：拦截判据 `ImageEncoderArgs.UnsupportedEncoderSettings`
#   只看 `opts.JxlLosslessJpeg` 这一个布尔、拿不到输入路径 ⇒ 归一（QueueProcessor 的 jbrd 有效值归一）
#   一旦失效，**所有 PNG→JXL 都会被整批挡出色彩引擎**，而引擎侧一切照常（只是静默走 legacy）。
# 实测（本机 Release FfmpegGui.exe，取证目录 tests/output/t73/，DLL SHA256 前 16 = F5B2111365C3D664；
#   下面四条臂的读数由本段自身每次跑出，取证轮：G1 rc=1/0 产物、G2 rc=0/1 产物、G3 rc=0/1 产物、G4 rc=0/1 产物）：
#   ① JPEG 输入 + `--encoder ffmpeg --color-engine engine` ⇒ rc=1、0 产物、
#      日志 `[色彩引擎] ❌ 按要求应走引擎，但本次不可走（EncoderUnsupportedSetting）`
#   ② 同一格不加 `--color-engine`（默认 auto）⇒ rc=0、有产物、
#      日志 `[色彩引擎] 本次不适用引擎（EncoderUnsupportedSetting：…）⇒ 由传统管线处理`
#   ③ PNG 输入、其余选项逐字相同 ⇒ 日志**不含** EncoderUnsupportedSetting、正常出产物
#   ④ JPEG 输入、只把 `--jxl-lossless-jpeg` 换成 false ⇒ 同样不含、正常出产物
$blkJpg = "$work/blk_src.jpg"
$blkPng = "$work/blk_src.png"
$null = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i gradients=s=200x150:c0=0x204070:c1=0xc07030:n=4,format=rgb24 -frames:v 1 -q:v 3 $blkJpg" "blkgen"
$null = Exec $ff "-y -hide_banner -loglevel error -i $blkJpg -frames:v 1 $blkPng" "blkpng"
if ((Test-Path $blkJpg) -and (Test-Path $blkPng)) {
  # ⚠ 四条臂**共用同一段选项串**，只换「输入扩展名」或「开关值」⇒ 每两条之间只有一个变量，
  #   差别本身就是一票判据（不需要改源码做变异）：
  #     G1(jpg,true) vs G3(png,true)  —— 唯一差别是输入 ⇒ 证明拦截判据**吃输入路径**（有效值归一在干活）
  #     G1(jpg,true) vs G4(jpg,false) —— 唯一差别是开关 ⇒ 证明拦截判据**吃那个值**，
  #                                       不是「jxl + ffmpeg 后端恒被拦」这种恒真
  $blkOpts = '--format jxl --encoder ffmpeg --color-engine engine --jxl-lossless-jpeg true'
  # ① 正控：显式要求引擎 ⇒ 必须**响亮拒绝且不出产物**（静默出产物就等于"引擎悄悄换了语义"）
  $rBlkA = RunCli "G1_jpg_engine_forced" $blkJpg $blkOpts
  CK ($rBlkA.text -match '按要求应走引擎，但本次不可走（EncoderUnsupportedSetting') `
     "⑪ 正控 G1：JPEG 输入 + 开关开 + 强制引擎 ⇒ 点名「按要求应走引擎，但本次不可走（EncoderUnsupportedSetting）」"
  CK ($rBlkA.prodCount -eq 0 -and $rBlkA.code -ne 0) `
     "⑪ 正控 G1：同一格是**干净拒绝**（0 产物 + rc≠0，实测 rc=$($rBlkA.code)），不是「拒绝文案照打、产物照出」"
  # ② auto ⇒ 回落传统管线，但 jbrd 做不到那件事必须**同时**被点名（第 ⑩ 段修的 #10 的牙）
  $rBlkB = RunCli "G2_jpg_engine_auto" $blkJpg '--format jxl --encoder ffmpeg --jxl-lossless-jpeg true'
  CK ($rBlkB.text -match '本次不适用引擎（EncoderUnsupportedSetting：') `
     "⑪ auto 归属 G2：引擎不可走 ⇒ 「本次不适用引擎（EncoderUnsupportedSetting：…）」＋回落传统管线"
  CK ($rBlkB.code -eq 0 -and $rBlkB.prodCount -eq 1) `
     "⑪ auto 归属 G2：默认档不硬失败（rc=$($rBlkB.code)、产物 $($rBlkB.prodCount) 个）"
  CK ($rBlkB.text -match '无损重封装\(jbrd\)开着') `
     "⑪ auto 归属 G2：回落到的 ffmpeg-libjxl **也**做不到 jbrd ⇒ 必须另有那句点名（锚「无损重封装(jbrd)开着」）"
  # ③ 差分臂 A（关键）：非 JPEG 输入 + 开关照开 ⇒ **不该**被拦。
  #    它是 ①② 的反真空臂：若判据改成"只看开关默认值"，PNG→JXL 会整批被挡出引擎而 ①② 仍全绿。
  $rBlkC = RunCli "G3_png_engine_forced" $blkPng $blkOpts
  CK ($rBlkC.text -notmatch 'EncoderUnsupportedSetting') `
     "⑪ 差分臂 G3（与 G1 只差输入扩展名）：PNG 输入即使强制引擎也**不得**命中 EncoderUnsupportedSetting"
  CK ($rBlkC.code -eq 0 -and $rBlkC.prodCount -eq 1) `
     "⑪ 差分臂 G3：同一格正常交付（rc=$($rBlkC.code)、产物 $($rBlkC.prodCount) 个）—— 否则上一句是「整条路都没跑」的恒真"
  # ④ 差分臂 B：JPEG 输入 + 开关关 ⇒ 也不该被拦（证明 G1 的红是**这个值**造成的，不是格式/后端本身）
  $rBlkD = RunCli "G4_jpg_engine_off" $blkJpg '--format jxl --encoder ffmpeg --color-engine engine --jxl-lossless-jpeg false'
  CK ($rBlkD.text -notmatch 'EncoderUnsupportedSetting') `
     "⑪ 差分臂 G4（与 G1 只差开关值）：同一 JPEG 输入关掉开关 ⇒ 不再命中 EncoderUnsupportedSetting"
  CK ($rBlkD.code -eq 0 -and $rBlkD.prodCount -eq 1) `
     "⑪ 差分臂 G4：关掉后走引擎出口并正常交付（rc=$($rBlkD.code)、产物 $($rBlkD.prodCount) 个）"
} else {
  CK $false "⑪ 素材生成失败（jpg/png 缺其一）⇒ 本段八条判据全部作废，不接受静默跳过"
}

# ── ⑫ jbrd 往返的**文件级**判据（2026-10-01 补）：JPEG→JXL→JPEG 必须逐字节回到原文件 ──────
# 为什么还要加这一段：⑩ 只证明"正向产物里有 reconstruction 盒"（jxlinfo 读回），
#   而"无损互转换"这句话真正的承诺是**反向能拿回原来那张 JPEG**。此前全仓没有任何一条判据
#   比较过往返文件与原文件的字节（`tests/output/t76/jbrd-e2e.ps1` 首次实测时才建立）。
#   判据用字节/像素哈希，不读自家日志措辞 ⇒ 换了实现也照样成立。
# 反真空臂：同一素材显式关掉 jbrd ⇒ 往返必须**不再**逐字节相同（否则上面的"相同"是恒真）。
$rtJpg = "$work/rt_src.jpg"
$null = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i gradients=s=512x384:c0=0x2060a0:c1=0xd07030:n=6,noise=alls=8:allf=t,format=rgb24 -frames:v 1 -q:v 3 $rtJpg" "rtgen"
if (Test-Path $rtJpg) {
  $rtSrcHash = (Get-FileHash $rtJpg -Algorithm SHA256).Hash
  $a = RunCli "K1_fwd" $rtJpg "--format jxl"
  $aOk = ($a.prodCount -eq 1)
  CK $aOk "⑫ 正向（JPEG→JXL）产出唯一产物（实 $($a.prodCount) 个，rc=$($a.code)）"
  if ($aOk) {
    $b = RunCli "K2_rev" $a.prodPath "--format jpg"
    if ($b.prodCount -ne 1) {
      CK $false "⑫ 反向（JXL→JPEG）产出唯一产物（实 $($b.prodCount) 个，rc=$($b.code)）⇒ 往返无从比对"
    } else {
      $rtBack = (Get-FileHash $b.prodPath -Algorithm SHA256).Hash
      CK ($rtBack -eq $rtSrcHash) "⑫ 往返产物与源 JPEG **逐字节相同**（源/回前 16 位 $($rtSrcHash.Substring(0,16)) / $($rtBack.Substring(0,16)))"
    }
  }
  $c = RunCli "K3_fwd_off" $rtJpg "--format jxl --jxl-lossless-jpeg false"
  $cOk = ($c.prodCount -eq 1)
  CK $cOk "⑫ 反真空正向（关掉 jbrd）产出唯一产物（实 $($c.prodCount) 个）"
  if ($cOk) {
    $d = RunCli "K4_rev_off" $c.prodPath "--format jpg"
    if ($d.prodCount -ne 1) { CK $false "⑫ 反真空反向产出唯一产物（实 $($d.prodCount) 个）⇒ 该臂无从判别" }
    else { CK ((Get-FileHash $d.prodPath -Algorithm SHA256).Hash -ne $rtSrcHash) "⑫ 反真空：关掉 jbrd 后往返**不再**逐字节相同 ⇒ 上面那条判据有牙" }
  }
} else {
  CK $false "⑫ 往返夹具生成失败 ⇒ 本段四条不给绿灯"
}

try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
CK (-not (Test-Path $work)) "⑨ 运行级目录已清零：$work"

Write-Host ""
Write-Host "===== jxl-codestream-route: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($fail -gt 0) { exit 1 } else { exit 0 }
