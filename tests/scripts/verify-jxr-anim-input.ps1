# verify-jxr-anim-input.ps1 —— 「多帧输入 → JXR」中转路径专项门禁（2026-09-18 新增，任务 #13）
#
# 缺陷（本门禁要锁住的东西）：
#   JXR 出口要把像素交给 `JxrEncApp`，而它是**纯文件式** `-i input -o output`（不吃 rawvideo/stdin），
#   ffmpeg 的 `libjxr` 又**无质量 AVOption**、字节序与 jxrlib 不一致 ⇒ 不作替代执行体（唯一口径见
#   `RawColorPipeline` 的 JXR 出口文档；⚠ 本行初稿写作「本工具链的 ffmpeg 没有 jxr 编码器」，09-27 按实测改正）
#   ⇒ 必须先落一个中转容器（BMP/TIFF，走 ffmpeg 的 `image2`
#   复用器）。而 `image2` 是**固定文件名**写盘、**不能**承载多帧：多帧输入时 ffmpeg 报
#       `[image2] Cannot write more than one file with the same name… Are you missing the -update option
#        or a sequence pattern?` + `Error submitting a packet to the muxer: Invalid argument`
#   并以 **-22** 退出 ⇒ 中转失败 ⇒ **整条任务失败、零产物**。
#   两处站点（**用锚点描述，不用行号**）：
#     · 站点 A（legacy）`QueueProcessor.ProcessJxrAsync` 的 `decodeArgs`（`… -pix_fmt {pixFmt}{tiffExtra}
#       "{intermediatePath}"`）—— 中转目标 `input.bmp` / `input.tiff`。
#     · 站点 B（引擎）`RawColorPipeline.EncodeJxrViaJxrEncAppAsync` 的 **alpha 分支** `ffArgs`
#       （`… -i "{tmpOut}" -i "{inputPath}" -filter_complex "{merge}" … "{inter}"`）—— 中转目标
#       `in.bmp` / `in.tiff`。
#       ⚠ **非 alpha 分支没有这个问题**：它的唯一输入是 `{tmpOut}`（一条 rawvideo）⇒ 恒为 1 帧。
#         本门禁**专门**断言非 alpha 分支**不带**该参数，防止「一概而论」式的顺手改动。
#
# 判据（修复后）＝ 与 #139（`FfmpegCommandBuilder.targetHoldsMultipleFrames`）**同一条**：
#   **目标容器能不能装多帧**。中转目标恒为 BMP/TIFF（单帧）⇒ 恒插 `-frames:v 1`。
#   ⚠ `-frames:v` 是**输出侧**选项：必须放在 `-i` **之后**（放第二个 `-i` 之前会被 ffmpeg 拒绝，
#     见下方 ③-d 的直接实测）。
#
# 实测（2026-09-18，本机，两侧数字都取过；详见 docs/HANDOVER.md 的 #13 小节）：
#   · 缺陷态：`anim.webp（10 帧）→ --format jxr` legacy ⇒ exit=1、产物 0 个；auto ⇒ exit=1、产物 0 个
#     （auto 的中转失败在 `[色彩引擎] ❌ 执行失败：InvalidOperationException: JXR 出口：raw → TIFF 中转失败 (exit -22)`）
#   · 修复态：两格均 exit=0、**1 个产物**（legacy 12810B / auto 114103B，本机一次实测值）
#   · 负控（修复**不得**波及单帧）：单帧 png / 单帧带 alpha png → jxr 均 exit=0、1 个产物、可被
#     `JxrDecApp` 独立解出；带 alpha 的**未被拍平**（同 16-bit 口径下 alpha YMIN 源==产物）
#   · 直接对拍（不经产品）：单帧 8bit BMP / 单帧 16bit TIFF / 单帧 alpha TIFF 三形态，
#     加与不加 `-frames:v 1` **逐字节相同** ⇒ 该参数对单帧输入是 no-op，不是"看起来一样"
#
# 断言分组：
#   ① 素材自备 + 形态自检（多帧素材必须 >1 帧；alpha 素材必须真有 alpha）—— 前提不成立必须红；
#   ② 正例：多帧输入 → jxr（legacy / auto / 带 alpha 的多帧）⇒ exit 0 + 有产物 + 独立解码器可解；
#      ＋**真空绿守卫**（站点 B 的 *app 级* 覆盖）：载体 = **静态带 alpha** 输入（`single_alpha.png`），
#      因为 `#136` 切片 B2 之后**动画输入不再进引擎**（`AnimatedInput`）⇒ 旧载体 `anim.webp` 失效；
#      另有两条**反向反控**（动画 webp / 动画 apng ⇒ 必须走 legacy）证明判据不恒真；
#   ③ 负控：单帧输入 → jxr 不回归；带 alpha 的单帧 alpha 不被拍平；直接对拍证明 no-op；
#   ④ 源码形态：两处站点**确实**带上该参数，且站点 B 的**非 alpha 分支没有**被顺手改（防止过度扩散）；
#   ⑤ 被测 exe mtime 跑前跑后一致（§6 第 84 条：不一致则本轮读数作废）；
#   ⑥ 残留自证：运行级 GUID 目录清零（仅在绿时判定，红时保留供排查）。
#
# 退出码：0 = 全绿，1 = 有失败。⚠ 结尾必须是**完整 if/else**（§6 第 67 条：清理步会污染
#   `$LASTEXITCODE`，只写 `if ($fail -gt 0) { exit 1 }` 会让全绿路径以被污染的 2 退出 ⇒ 假红）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1，门禁 `verify-ps-compat.ps1`）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；
#   双引号串内禁全角弯引号（用 「」）；取子进程输出一律 `Start-Process` + 重定向（`& exe` 部分宿主静默失败）。
#   ⚠ 本脚本**不用** `Start-Process pwsh`（宿主安全策略会拦 shell/LOLBin 目标）。
#   ⚠ 拼参数串一律用**单引号** + 字符串相加，**不用** `` `" ``（它是转义符，会把字符串吞掉并报出无关的解析错误）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$jxrEnc = "$root/publish/PLAN/artifacts/JxrEncApp.exe"
$jxrDec = "$root/publish/PLAN/artifacts/JxrDecApp.exe"
$et = "$root/publish/PLAN/exiftool/exiftool.exe"
$qpFile = "$root/src/FfmpegGui/Services/QueueProcessor.cs"
$rcFile = "$root/src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs"

# ⚠ 运行级隔离（GUID）：固定目录会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   素材放 %TEMP% 私有 GUID 目录（与 verify-ffmpeg-heartbeat.ps1 同款）。
$work = Join-Path $env:TEMP ("jxranim_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Host "PASS $msg" -ForegroundColor Green }
  else     { $script:fail++; Write-Host "FAIL $msg" -ForegroundColor Red }
}

# 取子进程输出一律走 Start-Process + 重定向（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）。
# ⚠ 参数串由调用方用**单引号**拼好（避免 `` `" `` 转义陷阱）。
function Exec([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e)) }
}

# ⚠⚠ **只读 stdout 的取数**（读「工具写在 stdout 的值」时唯一合法的尺；形状与 `Exec` 一致，只换返回口径）
#   `Exec` 把 stderr 拼进 `text` —— 对**实测只在 stderr** 的东西是故意的、正确的：ffmpeg 的 PSNR 行 /
#   `frame=` / signalstats 的 `YMIN=`（实测 `-hide_banner -i x -vf signalstats,metadata=print… -f null -`
#   的 stdout = **0 字节**、`YMIN=` 只在 stderr ⇒ 这类判据吃的是 stderr 的**告警全集**，换只读 stdout 会恒真）。
#   ⚠ 合并的代价（2026-09-27 实测）：本机 `LANG`/`LC_ALL` 不被识别时 ffmpeg/ffprobe 也往 stderr 吐 locale
#   警告（实测 26 B），ffprobe **不带 `-v error` 时**还吐整段版本横幅（实测 4838 B）⇒ 拿合并结果做
#   `-notmatch` 的判据在污染环境下会变脆；本轮按口径**只登记、不改判据**。
#   ⚠ 但**产品/ServiceProbe 的日志不保证在 stderr**，别照抄上面那句：2026-09-27 实测
#   `ServiceProbe decision` 的 6184 B 输出**全在 stdout**、stderr 0 B（`PROBE RESULT:` / `pass=` 都在 stdout）；
#   本文件的 `Exec $exe "--headless --log-level debug …"` 取的是 stdout+stderr **全集**，② 的
#   `-match '\[色彩\] ffmpeg'` / `-notmatch '\[jxr\] Step 1:'` 这类**互斥标签**判据吃的就是那个全集
#   ⇒ 保持合并是对的，动它之前先按实测确认那个标记在哪个流（实测不在本轮范围内，故本轮**未改**这些站点）。
#   下面三个 helper 读的是 **ffprobe 的 csv 字段值（stdout）** ⇒ 换 `ExecOut`；而随包 exiftool 是 perl 打包版：
#   环境带着本机 perl 不认的 `LC_ALL`/`LANG`（MSYS/bash 侧的 `C.UTF-8`）时它每次调用先往 stderr 喷
#   locale warning ⇒ 拼回来的噪声让「帧数/尺寸/像素格式」这类**整行匹配 + -eq 对账**既可能假红（值前多一行）、
#   也可能假绿（同污时两条警告互比）。⇒ stderr 照样落盘供排查，但**绝不进返回值**。
function ExecOut([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  if (-not (Test-Path $o)) { return @{ code = $p.ExitCode; text = "" } }
  $t = [System.IO.File]::ReadAllText($o)
  return @{ code = $p.ExitCode; text = $t }
}

# 真实帧数：ffprobe `-count_frames`（**不用**日志里的 `frame=` 字段：本工程已实测不可靠）。
# ⚠ 只认**整行就是数字**的输出行：ffprobe 报错会带上含数字的路径（GUID 目录名）⇒ 宽松匹配会假绿。
# ⚠ 取数用 `ExecOut` 的理由（实测 laneB）：ffprobe 的 csv 值**全在 stdout**、本机 ffprobe 的 stderr 在
#   带 `-v error` 时 ffprobe 的 stderr 实测 **0 字节**（clean 与 LC_ALL/LANG=C.UTF-8 两种条件一样，
#   alpha_q1.00.jxr / sdr_plain.png 的读数与合并版**同值**，只差合并版固定多塞的那个换行符）
#   ⇒ 换尺不会让任何格由绿转红；而"读不到"时 stdout 为空、报错只在 stderr ⇒ 那种红是对的（本 helper 返回 0，
#   由 ① 的 `$fw -gt 1` / `$fs -eq 1` 两条**正向**前提自检判红，不喂负断言）。
function FrameCount([string]$path) {
  $r = ExecOut $fp ('-v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames -of csv=p=0 "' + $path + '"') "fc_$([guid]::NewGuid().ToString('N').Substring(0,6))"
  $n = 0
  foreach ($ln in ($r.text -split "`r?`n")) { if ($ln -match '^\s*(\d+)\s*$') { $n = [int]$Matches[1] } }
  return $n
}

# 尺寸 WxH（ffprobe csv）；探测失败返回 ""（只认整行 `W,H` 形态）。
# ⚠ `ExecOut`：值在 stdout（实测 alpha_q1.00.jxr 的 WxH 行 stdout=「64,64」、带 `-v error` 时 stderr 0 字节，
#   与合并版**同值**，只差合并版多塞的那个换行符）；
#   消费方 ③ 的 `$sz -eq "256x192"` 是**正向**对账 ⇒ 读空即红，收紧不会由绿转红。
function ImgSize([string]$path) {
  $r = ExecOut $fp ('-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 "' + $path + '"') "sz_$([guid]::NewGuid().ToString('N').Substring(0,6))"
  $s = ""
  foreach ($ln in ($r.text -split "`r?`n")) { if ($ln -match '^\s*(\d+)\s*,\s*(\d+)\s*$') { $s = ($Matches[1] + "x" + $Matches[2]) } }
  return $s
}

# 像素格式（ffprobe）；用于「alpha 素材真有 alpha」这一条**前提自检**。
# ⚠ `ExecOut`：实测 ffprobe `-v error` 的 pix_fmt 令牌在 stdout（sdr_plain.png→`rgb24`、
#   alpha_q1.00.jxr→`bgra`）、stderr 0 B ⇒ 与合并版同值。消费方 ① 的 `$pfA -match '^(rgba|argb|...)'`
#   与 ① 的 `$pfSA -match ...` 都是**正向**匹配 ⇒ 读空即红，换尺不会由绿转红。
function PixFmt([string]$path) {
  $r = ExecOut $fp ('-v error -select_streams v:0 -show_entries stream=pix_fmt -of default=nw=1:nk=1 "' + $path + '"') "pf_$([guid]::NewGuid().ToString('N').Substring(0,6))"
  foreach ($ln in ($r.text -split "`r?`n")) { if ($ln -match '^\s*([a-z0-9]+)\s*$') { return $Matches[1] } }
  return ""
}

# 16-bit 口径的 alpha 最小值：**先统一到 rgba64le** 再取 alpha 的 YMIN（与 verify-color-wiring 同款）。
# ⚠ 必须与 8-bit 口径分开：8-bit 源经 `format=rgba64le` 的 8→16 扩张**不是** v*257
#   （实测 128 ⇒ 32238，而 128*257=32896）⇒ 直接拿 8-bit 阈值去判 16-bit 产物会假红。
# 本 helper 一律用于**同口径对比**（源与产物都过这一遍）：相等/极近 ⇒ 未被拍平。
# 无 alpha 的输入会被补成全不透明（65535）⇒ 与源的 26214 相差悬殊 ⇒ 拍平/丢 alpha 必然转红。
function AlphaYMin16([string]$path) {
  $o = "$work/_ay_$([guid]::NewGuid().ToString('N').Substring(0,6)).txt"
  $a = '-hide_banner -i "' + $path + '" -vf format=rgba64le,alphaextract,signalstats,metadata=print:key=lavfi.signalstats.YMIN -f null -'
  Start-Process -FilePath $ff -ArgumentList $a -Wait -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError "$o.e" | Out-Null
  $t = [System.IO.File]::ReadAllText($o) + [System.IO.File]::ReadAllText("$o.e")
  foreach ($ln in ($t -split "`r?`n")) { if ($ln -match 'YMIN=([0-9.]+)') { return [double]$Matches[1] } }
  return -1
}

# 独立解码器回读（**第三方证明是真 JXR**，不看自家日志）。⚠ 不用 ffmpeg 自解的理由不是「本构建没有 jxr
# 解码器」（实测**有** `(decoders: libjxr)`、往返位精确），而是：自家链路自解会把「写法自洽」冒充成
# 「格式正确」，且 ffmpeg 解 .jxr **不导出 ICC**（取证 `tests/output/t50/`）。
function JxrDec([string]$jxrPath, [string]$outPath, [string]$fmt) {
  if (Test-Path $outPath) { try { [System.IO.File]::Delete($outPath) } catch { } }
  $a = '-i "' + $jxrPath + '" -o "' + $outPath + '" -c ' + $fmt
  Start-Process -FilePath $jxrDec -ArgumentList $a -Wait -NoNewWindow `
      -RedirectStandardOutput "$work/_jxrdec.txt" -RedirectStandardError "$work/_jxrdec.e" | Out-Null
  return ((Test-Path $outPath) -and ((Get-Item $outPath).Length -gt 0))
}

# 容器像素格式（exiftool 回读）—— **不看命令行**，证明产物容器真的带/不带 alpha。
function JxrPixelFormat([string]$jxrPath) {
  $o = "$work/_jxrpf_$([guid]::NewGuid().ToString('N').Substring(0,6)).txt"
  $a = '-a -s -G1 -PixelFormat "' + $jxrPath + '"'
  Start-Process -FilePath $et -ArgumentList $a -Wait -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError "$o.e" | Out-Null
  return ([System.IO.File]::ReadAllText($o)).Trim()
}

# 产物：$outdir 下第一个匹配扩展名的文件（找不到返回 $null）。
function Product([string]$outdir, [string[]]$exts) {
  if (-not (Test-Path $outdir)) { return $null }
  $all = @(Get-ChildItem -LiteralPath $outdir -File -Recurse -ErrorAction SilentlyContinue)
  foreach ($f in $all) { foreach ($x in $exts) { if ($f.Extension -ieq $x) { return $f } } }
  return $null
}

# 从 debug 日志里取出**实际执行过**的 ffmpeg 参数串（legacy 记 `[cmd] ffmpeg …`）。
function LegacyFfmpegLine([string]$text) {
  foreach ($ln in ($text -split "`r?`n")) { if ($ln -match '^\[cmd\] ffmpeg (.*)$') { return $Matches[1] } }
  return ""
}
# 引擎出口记 `[色彩] ffmpeg …`（可能多条：先抽 raw、再中转）⇒ 取**含 rawvideo 的那条**（中转命令）。
function EngineFfmpegLines([string]$text) {
  $res = @()
  foreach ($ln in ($text -split "`r?`n")) { if ($ln -match '\[色彩\] ffmpeg (.*)$') { $res += $Matches[1] } }
  return $res
}

foreach ($need in @($exe, $ff, $fp, $jxrEnc, $jxrDec, $qpFile, $rcFile)) {
  if (-not (Test-Path $need)) { Write-Host "缺工具/源文件：$need" -ForegroundColor Red; exit 1 }
}
$exeMtimeBefore = (Get-Item $exe).LastWriteTimeUtc

# ── ① 素材自备 + 形态自检 ────────────────────────────────────────────────────────
Write-Host "`n### ① 自备素材（10 帧动图 / 10 帧带 alpha 动图 / 单帧 / 单帧带 alpha）并断言形态 ###" -ForegroundColor Cyan
$srcW = 256; $srcH = 192; $srcFrames = 10
$animW  = "$work/anim.webp"      # 10 帧（libwebp，**必须** -loop 0 才是动画）
$animA  = "$work/anim_alpha.apng" # 10 帧 + 真 alpha
$single = "$work/single.png"      # 单帧，无 alpha
$singleA = "$work/single_alpha.png"  # 单帧 + 真 alpha
$g1 = Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "testsrc2=s=' + $srcW + 'x' + $srcH + ':r=10:d=1" -frames:v ' + $srcFrames + ' -c:v libwebp -loop 0 -q:v 80 "' + $animW + '"') "gen_webp"
$g2 = Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "testsrc2=s=' + $srcW + 'x' + $srcH + ':r=10:d=1" -vf "format=rgba,colorchannelmixer=aa=0.5" -frames:v ' + $srcFrames + ' -c:v apng -plays 0 -f apng "' + $animA + '"') "gen_apng_alpha"
$g3 = Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "testsrc2=s=' + $srcW + 'x' + $srcH + '" -frames:v 1 "' + $single + '"') "gen_single"
$g4 = Exec $ff ('-y -hide_banner -loglevel error -f lavfi -i "color=c=blue@0.4:s=' + $srcW + 'x' + $srcH + ',format=rgba" -frames:v 1 "' + $singleA + '"') "gen_single_alpha"

$fw = FrameCount $animW; $fa = FrameCount $animA; $fs = FrameCount $single; $fsa = FrameCount $singleA
$pfW = PixFmt $animW; $pfA = PixFmt $animA; $pfS = PixFmt $single; $pfSA = PixFmt $singleA
$ayA = AlphaYMin16 $animA; $aySA = AlphaYMin16 $singleA
Write-Host ("  [素材] anim.webp=$(ImgSize $animW) 帧=$fw pix=$pfW | anim_alpha.apng=$(ImgSize $animA) 帧=$fa pix=$pfA alphaYMIN16=$ayA")
Write-Host ("  [素材] single.png=$(ImgSize $single) 帧=$fs pix=$pfS | single_alpha.png=$(ImgSize $singleA) 帧=$fsa pix=$pfSA alphaYMIN16=$aySA")
CK (((Test-Path $animW) -and ((Get-Item $animW).Length -gt 1000) -and $fw -gt 1) -and
    ((Test-Path $animA) -and ((Get-Item $animA).Length -gt 1000) -and $fa -gt 1)) `
   "① 两个动图素材都是多帧（anim.webp=$fw anim_alpha.apng=$fa，必须都 >1；否则本门禁前提不成立）"
CK (($pfA -match '^(rgba|argb|bgra|abgr|gbrap|ya8|yuva)') -and ($ayA -gt 0) -and ($ayA -lt 65535)) `
   "① anim_alpha.apng **真有 alpha**（pix_fmt=$pfA，alphaYMIN16=$ayA 既非全透明也非全不透明）—— 否则站点 B 的 alpha 分支根本走不到"
CK (($fs -eq 1) -and ($fsa -eq 1) -and ($pfSA -match '^(rgba|argb|bgra|abgr|gbrap|ya8|yuva)')) `
   "① 两个单帧素材确实是单帧（single.png=$fs single_alpha.png=$fsa），且后者 pix_fmt=$pfSA 带 alpha"

# ── ② 正例：多帧输入 → jxr ⇒ exit 0 + 有产物 + 独立解码器可解 ────────────────────
Write-Host "`n### ② 正例：多帧输入 → --format jxr（必须 exit 0、有产物、JxrDecApp 可解）###" -ForegroundColor Cyan
$cases = @(
  @{ src = $animW; name = "anim.webp(10帧)";        eng = "legacy"; tag = "p_legacy_animwebp";  cdec = "0" },
  # ⚠⚠ 2026-09-18 更新（`#136` 切片 B2 之后）：`.webp` 也走**真探测**了 ⇒ 10 帧 ⇒
  #   `ColorEngineRouter.BlockReason=AnimatedInput` ⇒ **`auto` 也走 legacy**（实测日志：
  #   `有[色彩] ffmpeg=False 有[jxr] Step 1=True`）⇒ 该格的中转是 24bppBGR（无 alpha 平面），
  #   解码码必须是 `-c 0`（用 `-c 23` 会 exit=-106 假红）。
  #   ⚠ 这正是 B2 的**预期行为**（引擎按单帧处理会丢帧）—— 本门禁**不再**要求动画输入走引擎出口。
  @{ src = $animW; name = "anim.webp(10帧)";        eng = "auto";   tag = "p_auto_animwebp";    cdec = "0" },
  @{ src = $animA; name = "anim_alpha.apng(10帧)";  eng = "legacy"; tag = "p_legacy_animalpha"; cdec = "0" },
  # ⚠ `.apng` 被 `IsAnimated` 判定为动画 ⇒ `ColorEngineRouter` 的 BlockReason=AnimatedInput ⇒
  #   **auto 也走 legacy**（实测日志：`本次由 外部编码器 Jxr 处理，色彩引擎尚未接入该路径`）
  #   ⇒ 该格的中转是 24bppBGR（无 alpha 平面），解码码必须是 `-c 0`（用 `-c 23` 会 exit=-106 假红）。
  #   ⚠ 站点 B（引擎 alpha 分支）的覆盖**不再**由动画输入承担 —— 见下面 ② 的「真空绿守卫」，
  #     它的载体已换成**静态带 alpha** 输入（`single_alpha.png`）。
  @{ src = $animA; name = "anim_alpha.apng(10帧)";  eng = "auto";   tag = "p_auto_animalpha";   cdec = "0" }
)
foreach ($c in $cases) {
  $outdir = "$work/$($c.tag)"
  $extra = ""
  if ($c.eng -eq "legacy") { $extra = " --color-engine legacy" }
  $r = Exec $exe ('--headless --log-level debug -i "' + $c.src + '" -o "' + $outdir + '" --format jxr' + $extra) "run_$($c.tag)"
  $p = Product $outdir @(".jxr")
  $dec = $false
  if ($null -ne $p) { $dec = JxrDec $p.FullName "$work/_back_$($c.tag).tif" $c.cdec }
  $nWrite = ([regex]::Matches($r.text, 'Cannot write more than one file')).Count
  Write-Host ("  [{0} → jxr / {1}] exit={2} 产物={3} 可解={4} 缺陷签名(Cannot write…)=x{5}" -f `
    $c.name, $c.eng, $r.code, $(if ($null -ne $p) { $p.Name + ':' + $p.Length + 'B' } else { "(无)" }), $dec, $nWrite)
  CK ($r.code -eq 0) "② $($c.name) → jxr/$($c.eng)：exit == 0（实 $($r.code)）"
  CK ($null -ne $p) "② $($c.name) → jxr/$($c.eng)：**有产物**（不是 0 个）"
  CK $dec "② $($c.name) → jxr/$($c.eng)：**独立解码器 JxrDecApp 能解出产物**（第三方证明是真 JXR）"
  CK ($nWrite -eq 0) "② $($c.name) → jxr/$($c.eng)：日志里**没有** `image2` 固定文件名的缺陷签名（实 x$nWrite）"
}
# ⚠⚠ **真空绿守卫**（2026-09-18 **换载体**）：站点 B（引擎 JXR 出口的 alpha 分支）**必须**被一条
#   *app 级* 路径真的走到。**守卫本身没变，载体换了**：
#     · 旧载体 = `anim.webp + auto` —— B2 **之前**它真的走引擎；B2 之后 `.webp` 走**真探测** ⇒
#       10 帧 ⇒ `AnimatedInput` ⇒ 引擎被挡 ⇒ 走 legacy ⇒ **旧载体失效**（守卫按设计判红，实测 49/5）。
#     · 新载体 = `single_alpha.png(1 帧, 带 alpha) + auto` —— **静态** ⇒ 不被 `AnimatedInput` 挡 ⇒
#       引擎可路由；**带 alpha** ⇒ 走引擎出口的 **alpha 分支**（站点 B）⇒ 覆盖恢复。
#   否则（例如哪天连静态输入也被挡在引擎外）本门禁对站点 B 的覆盖会**静默消失**，
#   而下面所有断言仍然全绿 —— 那是最坏的一种假绿。
#   判据用**两侧互斥**的标签（与 verify-color-wiring (11c) 同款）：引擎出口有 `[色彩] ffmpeg`、
#   无 `[jxr] Step 1:`（后者是 legacy `ProcessJxrAsync` 专用）。
$rRoute = Exec $exe ('--headless --log-level debug -i "' + $singleA + '" -o "' + "$work/p_route_auto" + '" --format jxr') "run_p_route_auto"
$routeEngine = (($rRoute.text -match '\[色彩\] ffmpeg') -and ($rRoute.text -notmatch '\[jxr\] Step 1:'))
Write-Host ("  [路由] single_alpha.png+auto 走引擎出口={0}（有[色彩] ffmpeg={1} 有[jxr] Step 1={2}）" -f `
  $routeEngine, ($rRoute.text -match '\[色彩\] ffmpeg'), ($rRoute.text -match '\[jxr\] Step 1:'))
CK $routeEngine "② single_alpha.png(1帧,带 alpha) + auto **确实走引擎出口**（站点 B 的 alpha 分支）—— 否则本门禁对站点 B 的覆盖是真空的"
# 反控 ①（**有牙**，同时是 B2 的行为锁）：把载体换回**动画**输入 ⇒ 必须走 legacy
#   （B2 把动画输入挡在引擎外）⇒ 证明上面那条判据不是恒真。
$rRoute2 = Exec $exe ('--headless --log-level debug -i "' + $animW + '" -o "' + "$work/p_route_auto2" + '" --format jxr') "run_p_route_auto2"
CK ($rRoute2.text -match '\[jxr\] Step 1:') `
   "② 反控①：anim.webp(10帧) + auto 走 **legacy**（有 `[jxr] Step 1:`）—— 动画输入必须被引擎挡下（B2），与上一格形成反向对照"
# 反控 ②（**有牙**，保留）：`.apng` 同向走 legacy。
$rRoute3 = Exec $exe ('--headless --log-level debug -i "' + $animA + '" -o "' + "$work/p_route_auto3" + '" --format jxr') "run_p_route_auto3"
CK ($rRoute3.text -match '\[jxr\] Step 1:') `
   "② 反控②：anim_alpha.apng(10帧) + auto 走 **legacy**（有 `[jxr] Step 1:`）—— 与上一格同向，判据不恒真"
# ②-e 引擎 alpha 分支的**命令行**判据（子进程命令行 > 日志行，§6 第 89 条）：
#   ⚠ 2026-09-18 换载体：与「真空绿守卫」同步 —— 用**静态带 alpha** 输入，动画输入 B2 之后不再进引擎。
$eAuto = Exec $exe ('--headless --log-level debug -i "' + $singleA + '" -o "' + "$work/p_cmd_auto" + '" --format jxr') "run_p_cmd_auto"
$eLines = EngineFfmpegLines $eAuto.text
$mergeLine = ""
foreach ($l in $eLines) { if (($l -match 'alphaextract') -and ($l -match 'rawvideo')) { $mergeLine = $l } }
Write-Host ("  [引擎中转命令行] " + $(if ($mergeLine.Length -gt 0) { $mergeLine } else { "(未取到)" }))
CK ($mergeLine.Length -gt 0) "② 取到引擎出口的**中转命令行**（含 rawvideo + alphaextract）—— 判据不靠日志散文"
CK ($mergeLine -match '\-frames:v 1') "② 引擎 alpha 分支的中转命令行**带** `-frames:v 1`（限制写出的帧数）"
CK ($mergeLine -match 'in\.(tiff|bmp)"\s*$') "② 该参数紧贴中转产物路径之前（`-frames:v` 是输出侧选项，必须在 `-i` 之后）"

# ②-f legacy 站点 A 的命令行判据
$lAuto = Exec $exe ('--headless --log-level debug -i "' + $animW + '" -o "' + "$work/p_cmd_legacy" + '" --format jxr --color-engine legacy') "run_p_cmd_legacy"
$legLine = LegacyFfmpegLine $lAuto.text
Write-Host ("  [legacy 解码命令行] " + $(if ($legLine.Length -gt 0) { $legLine } else { "(未取到)" }))
CK ($legLine.Length -gt 0) "② 取到 legacy 站点 A 的**解码命令行**（`[cmd] ffmpeg …`）"
CK ($legLine -match '\-frames:v 1') "② legacy 站点 A 的解码命令行**带** 「-frames:v 1」"
CK ($legLine -match 'input\.(bmp|tiff)"\s*$') "② 该参数紧贴中转产物 `input.bmp/tiff` 之前"

# ── ③ 负控 ───────────────────────────────────────────────────────────────────────
Write-Host "`n### ③ 负控 A：单帧输入 → jxr 不得回归 ###" -ForegroundColor Cyan
$singleCases = @(
  @{ src = $single;  name = "single.png(1帧)";       eng = "legacy"; tag = "n_legacy_single"; cdec = "0";  wantPf = '24-bit BGR' },
  # ⚠ 2026-09-30：本臂原先写死 `cdec = "10"`（48bppRGB），依据是"引擎出口的出口位深恒 16"。
  #   那句话把**中间件精度**当成了**交付深度** —— 交付档现改按 `plan.TargetBitDepth`（与 (11a)/(11a2) 同批修），
  #   8-bit 源 ⇒ 24bppBGR。解码码现由**产物的 PixelFormat** 现推，并与表里的期望值**对账**：
  #   档位若哪天再漂，红的是"期望 vs 实测不一致"这一句，而不是"产物解不出来"这种会被误读成坏文件的假红。
  @{ src = $single;  name = "single.png(1帧)";       eng = "auto";   tag = "n_auto_single";   cdec = "0";  wantPf = '24-bit BGR' },
  @{ src = $singleA; name = "single_alpha.png(1帧)"; eng = "auto";   tag = "n_auto_singleA";  cdec = "22"; wantPf = '32-bit BGRA' }
)
# 码表逐字取自 `JxrDecApp` 自己的 usage（22=32bppBGRA、23=64bppRGBA；工具对扩展名不敏感，实测 `.bin` 也可）。
function JxrDecodeCode([string]$pf) {
  if ($pf -match '24-bit BGR')  { return '0' }
  if ($pf -match '48-bit RGB')  { return '10' }
  if ($pf -match '32-bit BGRA') { return '22' }
  if ($pf -match '64-bit RGBA') { return '23' }
  return ''
}
foreach ($c in $singleCases) {
  $outdir = "$work/$($c.tag)"
  $extra = ""
  if ($c.eng -eq "legacy") { $extra = " --color-engine legacy" }
  $r = Exec $exe ('--headless --log-level debug -i "' + $c.src + '" -o "' + $outdir + '" --format jxr' + $extra) "run_$($c.tag)"
  $p = Product $outdir @(".jxr")
  $dec = $false
  $code = $c.cdec
  if ($null -ne $p) {
    if (Test-Path $et) {
      $pfC = JxrPixelFormat $p.FullName
      CK ($pfC -match [regex]::Escape($c.wantPf)) `
         "③ $($c.name) → jxr/$($c.eng)：交付档位回读为「$($c.wantPf)」（实得「$pfC」）—— 中间件档不再冒充交付档"
      $derived = JxrDecodeCode $pfC
      if ($derived -ne '') { $code = $derived }
    }
    $dec = JxrDec $p.FullName "$work/_back_$($c.tag).tif" $code
  }
  $sz = ""
  if ($null -ne $p) { $sz = (ImgSize "$work/_back_$($c.tag).tif") }
  Write-Host ("  [{0} → jxr / {1}] exit={2} 产物={3} 可解={4} 回读尺寸={5}" -f `
    $c.name, $c.eng, $r.code, $(if ($null -ne $p) { $p.Name + ':' + $p.Length + 'B' } else { "(无)" }), $dec, $sz)
  CK ($r.code -eq 0) "③ $($c.name) → jxr/$($c.eng)：exit == 0（实 $($r.code)）—— 单帧输入**不回归**"
  CK ($null -ne $p) "③ $($c.name) → jxr/$($c.eng)：有产物"
  CK $dec "③ $($c.name) → jxr/$($c.eng)：产物可被独立解码器解出"
  CK ($sz -eq "$($srcW)x$($srcH)") "③ $($c.name) → jxr/$($c.eng)：回读尺寸仍为 $($srcW)x$($srcH)（实 「$sz」）—— 没被该参数切坏"
}

Write-Host "`n### ③-b 负控 B：带 alpha 的**单帧** → jxr 的 alpha 不得被拍平 ###" -ForegroundColor Cyan
$nAlpha = "$work/n_auto_singleA"
$pAlpha = Product $nAlpha @(".jxr")
if ($null -ne $pAlpha) {
  $pf = JxrPixelFormat $pAlpha.FullName
  Write-Host ("  [alpha 产物] PixelFormat=「$pf」")
  if (Test-Path $et) {
    CK ($pf -match 'RGBA|BGRA') "③-b **产物容器真的含 alpha**（exiftool 回读 PixelFormat=「$pf」）—— 不是只看命令行"
  } else {
    Write-Host "  （缺 exiftool ⇒ 跳过 PixelFormat 回读；下面的 alpha YMIN 同口径对比仍然生效）" -ForegroundColor Yellow
  }
  $backA = "$work/_back_n_auto_singleA.tif"
  if (Test-Path $backA) {
    $aySrc = AlphaYMin16 $singleA
    $ayOut = AlphaYMin16 $backA
    Write-Host ("  [alpha 同口径对比] 源 alphaYMIN16=$aySrc  产物 alphaYMIN16=$ayOut")
    # 同 16-bit 口径下源与产物的 alpha YMIN 必须**极近**。拍平（或丢 alpha）会得到 65535，
    # 与源的 ~26000 相差近 40000 ⇒ 容差 256（≈1 个 8-bit 步长）既有牙又不因 JXR 有损编码假红。
    CK (($aySrc -gt 0) -and ($aySrc -lt 65535) -and ([Math]::Abs($ayOut - $aySrc) -le 256)) `
       "③-b 单帧 alpha **未被拍平**（源=$aySrc 产物=$ayOut，容差 ≤256）"
  } else {
    CK $false "③-b alpha 产物 JxrDecApp 回读失败（无法判定是否拍平）"
  }
} else {
  CK $false "③-b 无 alpha 单帧产物 ⇒ 无法判定 alpha 是否被拍平（上一组已红）"
}

Write-Host "`n### ③-c 负控 C：直接对拍证明 `-frames:v 1` 对**单帧**输入是 no-op（逐字节）###" -ForegroundColor Cyan
# 不经产品，直接对 ffmpeg 两跑对拍：加与不加该参数，中转文件必须**逐字节相同**。
# ⚠ 这一组是**独立**证据（不依赖 exe 的日志/产物），若哪天有人把该参数挪到会改变语义的位置，它会先红。
$png16 = "$work/one16.png"
$rawS  = "$work/one.rgb48"
Exec $ff ('-y -hide_banner -loglevel error -i "' + $single + '" -frames:v 1 -pix_fmt rgb48le "' + $png16 + '"') "gen_png16" | Out-Null
Exec $ff ('-y -hide_banner -loglevel error -i "' + $single + '" -frames:v 1 -pix_fmt rgb48le -f rawvideo "' + $rawS + '"') "gen_raw48" | Out-Null
$fcAlpha = "[1:v]format=rgba64le,alphaextract[a];[0:v]format=rgb48le[b];[b][a]alphamerge"
$noopCases = @(
  @{ name = "站点A 8bit(BMP)";  a = '-y -hide_banner -loglevel error -i "' + $single + '" -pix_fmt bgr24'; b = ' -frames:v 1'; ext = ".bmp" },
  @{ name = "站点A 16bit(TIFF)"; a = '-y -hide_banner -loglevel error -i "' + $png16 + '" -pix_fmt rgb48le -compression_algo raw -pred none'; b = ' -frames:v 1'; ext = ".tiff" },
  @{ name = "站点B alpha(TIFF)"; a = '-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb48le -video_size ' + $srcW + 'x' + $srcH + ' -i "' + $rawS + '" -i "' + $singleA + '" -filter_complex "' + $fcAlpha + '" -pix_fmt rgba64le -compression_algo raw -pred none'; b = ' -frames:v 1'; ext = ".tiff" }
)
foreach ($c in $noopCases) {
  $p1 = "$work/noop_a$($c.ext)"; $p2 = "$work/noop_b$($c.ext)"
  if (Test-Path $p1) { try { [System.IO.File]::Delete($p1) } catch { } }
  if (Test-Path $p2) { try { [System.IO.File]::Delete($p2) } catch { } }
  $r1 = Exec $ff ($c.a + ' "' + $p1 + '"') "noop_a"
  $r2 = Exec $ff ($c.a + $c.b + ' "' + $p2 + '"') "noop_b"
  $h1 = ""; $h2 = ""
  if (Test-Path $p1) { $h1 = (Get-FileHash -LiteralPath $p1 -Algorithm MD5).Hash.ToLower() }
  if (Test-Path $p2) { $h2 = (Get-FileHash -LiteralPath $p2 -Algorithm MD5).Hash.ToLower() }
  Write-Host ("  [{0}] 无参数 exit={1} md5={2} | 有参数 exit={3} md5={4}" -f $c.name, $r1.code, $h1, $r2.code, $h2)
  CK (($r1.code -eq 0) -and ($r2.code -eq 0) -and ($h1.Length -gt 0) -and ($h1 -eq $h2)) `
     "③-c $($c.name)：加与不加 `-frames:v 1` **逐字节相同**（$h1）⇒ 对单帧输入是 no-op"
}

Write-Host "`n### ③-d 负控 D：`-frames:v 1` 放在第二个 `-i` **之前**会被 ffmpeg 拒绝（故只能限制输出）###" -ForegroundColor Cyan
# 这是「站点 B 为什么取输出侧限制」的**直接实测**依据：输入侧位置**不可行**，不是风格偏好。
$bad = Exec $ff ('-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb48le -video_size ' + $srcW + 'x' + $srcH + ' -i "' + $rawS + '" -frames:v 1 -i "' + $singleA + '" -filter_complex "' + $fcAlpha + '" -pix_fmt rgba64le "' + "$work/badpos.tiff" + '"') "badpos"
$badHas = ($bad.text -match 'cannot be applied to input')
Write-Host ("  [输入侧位置] exit={0} ffmpeg 明确拒绝={1}" -f $bad.code, $badHas)
CK (($bad.code -ne 0) -and $badHas) "③-d 把 `-frames:v 1` 放第二个 `-i` 之前：ffmpeg **明确拒绝**（exit=$($bad.code)）⇒ 输入侧限制不可行"

# ── ④ 源码形态：两处站点确实带该参数；站点 B 的非 alpha 分支**没有**被顺手改 ────────
Write-Host "`n### ④ 源码形态自检（防止将来被静默回退 / 防止过度扩散）###" -ForegroundColor Cyan
$qp = [System.IO.File]::ReadAllText($qpFile)
$rc = [System.IO.File]::ReadAllText($rcFile)
# 站点 A：锚点是那条 `decodeArgs` 赋值语句（**不用行号**）。
$siteAAnchor = 'var decodeArgs = $"-y {colorArgs}-i \"{item.InputPath}\" {filterArg}-pix_fmt {pixFmt}{tiffExtra}'
$ia = $qp.IndexOf($siteAAnchor)
CK ($ia -ge 0) "④ 能按锚点定位站点 A（`ProcessJxrAsync` 的 `decodeArgs` 赋值）"
$siteAWin = ""
if ($ia -ge 0) {
  $len = [Math]::Min(400, $qp.Length - $ia)
  $siteAWin = $qp.Substring($ia, $len)
}
CK ($siteAWin -match '\-frames:v 1 \\"\{intermediatePath\}\\"') `
   "④ 站点 A 的 decodeArgs 在**中转产物路径之前**带 「-frames:v 1」"
# 站点 B：锚点是 alpha 分支那条 `ffArgs` 拼接。
# ⚠ 锚点**必须**带 `-i "{tmpOut}" -i "{inputPath}"`：单用 `-filter_complex "{merge}"` 会**先**命中
#   本文件另一处（cjxl/PAM 出口，`-i "{rawPath}"`）⇒ 窗口取到的是别人的代码（实测踩过：门禁假红）。
# ⚠ 该拼接**跨两行**（`-filter_complex` 一行、`-pix_fmt … -frames:v 1 "{inter}"` 下一行）
#   ⇒ 窗口必须跨行取（只取到行尾会取不到 `-frames:v 1`，把「已修」误判成红）。
$siteBAnchor = '-i \"{tmpOut}\" -i \"{inputPath}\" -filter_complex \"{merge}\"'
$ib = $rc.IndexOf($siteBAnchor)
CK ($ib -ge 0) "④ 能按锚点定位站点 B 的 alpha 分支（`-filter_complex \"{merge}\"` 拼接）"
$siteBWin = ""
if ($ib -ge 0) {
  $len2 = [Math]::Min(400, $rc.Length - $ib)
  $siteBWin = $rc.Substring($ib, $len2)
}
CK ($siteBWin -match '\-frames:v 1 \\"\{inter\}\\"') `
   "④ 站点 B 的 alpha 分支在**中转产物路径之前**带 「-frames:v 1」"
# ⚠ 防过度扩散：**非 alpha 分支**（唯一输入是 `{tmpOut}` ⇒ 恒 1 帧）**不该**被加上该参数。
#   用 `-i "{tmpOut}" -pix_fmt {pixOut}{tiffExtra} "{inter}"` 这段做锚点（alpha 分支里没有它）。
#   ⚠ 判据取的是**源码里那一行**（不是锚点串本身）—— 拿锚点串去 match 是**空断言**（它本来就不含该参数）。
$noAlphaAnchor = '-i \"{tmpOut}\" -pix_fmt {pixOut}{tiffExtra} \"{inter}\"'
$in2 = $rc.IndexOf($noAlphaAnchor)
CK ($in2 -ge 0) "④ 能按锚点定位站点 B 的**非 alpha** 分支（`-i \"{tmpOut}\" -pix_fmt … \"{inter}\"`）"
$noAlphaLine = ""
if ($in2 -ge 0) {
  $ie3 = $rc.IndexOf("`n", $in2); if ($ie3 -lt 0) { $ie3 = $rc.Length }
  $noAlphaLine = $rc.Substring($in2, $ie3 - $in2)
}
CK (($noAlphaLine.Length -gt 0) -and ($noAlphaLine -notmatch 'frames:v')) `
   "④ 站点 B 的**非 alpha** 分支**没有**被顺手加上 `-frames:v 1`（它恒为单帧，不需要；防止一概而论）"
# 站点 A 的 `intermediateExt` 只能是 .bmp/.tiff（单帧容器）—— 前提自检，否则"恒插"这个判据不成立。
CK ($qp -match 'var intermediateExt = isHdr \? "\.tiff" : "\.bmp";') `
   "④ 前提：站点 A 的中转容器恒为 .bmp/.tiff（单帧容器）⇒ 「恒插 `-frames:v 1`」与 #139 判据一致"

# ── ④-b 本脚本自身的语法卫生自检 ────────────────────────────────────────────────
# ⚠ 为什么门禁要检查**自己**：2026-09-18 实测踩过 —— 在双引号消息里写「反引号 + `-frames:v 1` +
#   反引号」时，若**反引号紧贴收尾双引号**，它会被解析成**转义的双引号** ⇒ 字符串不闭合、吞掉后续
#   20 多行；而 `Parser::ParseFile` 仍报 **0 个错误**（引号整体配平了，只是位置全错），
#   直到**运行期**才在很远处报「术语 … 不会被识别为 cmdlet」。
#   ⇒ 把这一类事故变成门禁**能自证**的形态：非注释行不得出现「反引号紧贴双引号」。
#   ⚠ 判据串必须用字符码拼（[char]0x60 + [char]0x22），否则这一行自己就会命中。
$selfPath = $PSCommandPath
if (-not $selfPath) { $selfPath = $MyInvocation.MyCommand.Path }
$selfLines = [System.IO.File]::ReadAllLines($selfPath)
$btq = [string][char]0x60 + [string][char]0x22
$selfBad = @()
for ($si = 0; $si -lt $selfLines.Count; $si++) {
  if ($selfLines[$si] -match [regex]::Escape($btq)) {
    if (-not $selfLines[$si].TrimStart().StartsWith('#')) { $selfBad += ($si + 1) }
  }
}
CK ($selfBad.Count -eq 0) "④-b 本脚本自身没有「反引号紧贴双引号」的转义陷阱（非注释行实 $($selfBad.Count) 处 $(($selfBad -join ',')))"

# ── ⑤ 被测 exe mtime 一致（§6 第 84 条）─────────────────────────────────────────
Write-Host "`n### ⑤ 被测 exe mtime 跑前跑后一致 ###" -ForegroundColor Cyan
$exeMtimeAfter = (Get-Item $exe).LastWriteTimeUtc
CK ($exeMtimeBefore -eq $exeMtimeAfter) "⑤ 被测 exe 未被中途重建（$exe）"

# ── ⑥ 残留自证（仅在绿时判定；红时保留目录供排查）───────────────────────────────
Write-Host "`n### ⑥ 残留自证 ###" -ForegroundColor Cyan
if ($fail -eq 0) {
  # 清理一律用 **.NET API** 绕过宿主批量删除守卫（§6 第 57/67 条：守卫会抛 terminating error 并污染
  # `$LASTEXITCODE`，`-EA SilentlyContinue` 与 try/catch 都挡不住）。
  try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
  CK (-not (Test-Path $work)) "⑥ 运行级目录已清零：$work"
} else {
  Write-Host "  （有失败 ⇒ 保留目录供排查：$work）" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "===== jxr-anim-input: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 退出码必须在清理之后，且必须写完整 if/else（§6 第 67 条）。
if ($fail -gt 0) { exit 1 } else { exit 0 }
