# verify-engine-firstframe.ps1 —— 色彩引擎「多帧输入只取首帧」专项门禁
#   （2026-09-18 新增；2026-09-18 `#136` 切片 C **把 ②③④ 重指向「直调引擎」的探针载体**）
#
# 缺陷（本门禁要锁住的东西）：
#   `RawColorPipeline.cs` 的主解码命令 `-i <file> -vf ... -f rawvideo <tmp>` **没有 `-frames:v 1`**
#   ⇒ 多帧容器（动图 webp/gif/apng…）会被解成**一条 N 帧拼接的 rawvideo**（`rawLen = N × 缩放后单帧 × 6`）。
#   随后按 `h = px/w` **反推**高度 ⇒ `h = N × 单帧高`，而完整性检查 `(long)w*h*6 == rawLen` 在
#   `h = N × 单帧高` 下**恰好成立**（`128×960×6 == 10×128×96×6 == 737280`）⇒ 静默通过
#   ⇒ 产物把 10 帧**竖着叠成一张 128×960 的图**。
#   ⚠ 这不是「丢帧」（丢帧至少还是一张正常的图），是**产出一张用户看不出来的错图**。
#   ⚠ 2026-09-18 更正：本文件与 `RawColorPipeline.cs` 原先都把该等式写成 `10×256×192×6`（= 2949120）
#     —— 那是**假等式**（缩放是**逐帧**的 ⇒ 右式必须是**缩放后**的单帧 128×96）。机制描述不变，只改举例。
#
# 实测（2026-09-18，本机，两侧数字都取过）：
#   素材 anim.webp 256x192 **10 帧** + 最长边限制 128（引擎自己做缩放）
#     · 去掉 `-frames:v 1` ⇒ exit 0、产物 **128x960**、1 帧、日志亦报 128x960（本门禁转红）
#     · 加回 `-frames:v 1`  ⇒ exit 0、产物 **128x96**、1 帧、日志报 128x96（本门禁转绿）
#
# ⚠ **为什么 ②③④ 改用「直调引擎」的探针载体**（2026-09-18 `#136` 切片 C）：
#   本门禁原先用**完整 CLI**（`--headless -i anim.webp -o … -f webp --color-engine engine …`）喂动图，
#   靠"引擎被路由到"才走得到那条解码命令。而 `#136` 拆轴后「**动画输入** + **显式 `--color-engine engine`**」
#   会变成 **exit 90 硬失败、零产物**（`ColorEngineRouter.BlockReason` 的 `AnimatedInput` 分支 +
#   `ExplicitlyRequested` ⇒ `QueueProcessor` 直接 `return (true, 90, null)`）⇒ **CLI 载体整体失效**。
#   ⇒ 先换载体（本文件）、再改行为：②③④ 改为复读 `ServiceProbe firstframe <input> <outDir>` 的
#   **结构化行**（它绕过路由层直调 `RawColorPipeline.TransformFileAsync`，并**必须**传 `options` ——
#   否则 `RawColorPipeline.cs:94-95` 的 `scaleFilter` 恒为 null ⇒ 几何缩放段根本不跑）。
#   ⚠ ⑤ 负控**保留 CLI 路径**：单帧输入不是动画 ⇒ 引擎照旧可路由 ⇒ 它今天仍有效，
#     且能证明「单帧正常路径没被改坏」。
#
# ⚠⚠ **注释锁（2026-09-18 补；`#136` 切片 B2 之后，据 `#61` 全仓审计结论）**：
#   本门禁是**全仓唯一**「素材是**真动画**（10 帧）+ 断言**引擎被执行**」的组合。
#   B2 之后 `.webp` 走**真探测** ⇒ 动画输入命中 `BlockReason=AnimatedInput` ⇒ **app 级永远到不了引擎**
#   ⇒ ②③④ 的「直调探针」载体**不是可选优化，是唯一可行形态**。
#   ⇒ **禁止**把 ②③④ 改成 app 级（`--headless -i anim.webp …`）来「简化」本门禁 —— 那样只会得到
#      exit 90 / 零产物，或者**把断言改成「必须走 legacy」而悄悄丢掉「引擎被执行」这条覆盖**。
#   ⇒ 若将来真要让 app 级重新触达引擎，必须先有一条**能进引擎的动画载体**（**目前不存在**）；
#      在那之前，本门禁 ②③④ **只准用 `ServiceProbe firstframe`**。
#   ⚠ 附：`#61` 审计结论 = 全仓 **0 条真空 / 0 条「无守卫 ⇒ 静默绿」**；本文件是**唯一**需要这条注释锁的。
#
# ⚠ **更正一处错误注释**（2026-09-18，team-lead 已裁定）：本文件原先写「`QueueProcessor` 的**预缩放**
#   对动图**有意跳过**（`maxDimSkipAnimated = IsAnimated(captured)`）」。**这是错的** ——
#   `IsAnimated` 的输入扩展名清单是 `.gif / .apng / .jxl`，**不含 `.webp`** ⇒ 动图 `.webp` 会**进入**预缩放块。
#
# ⚠⚠ **历史读数（修复前，逐字保留，不改写历史）**：预缩放那条 ffmpeg 因 `image2` 固定文件名而**失败**
#   （`Invalid argument`，留半成品），随后走容忍分支「预缩放失败…将尝试原图继续」⇒ `captured.InputPath`
#   **未被改写** ⇒ 引擎才拿到原图、才走到本门禁要锁的那条解码命令。
#   ⇒ **当时「绿」是因为预缩放失败，不是因为被跳过。**
#
# ⚠⚠ **现势（`#136` 收尾轮起，2026-09-18）**：上面「不含 `.webp`」与「绿是因为预缩放失败」**都已过期** ——
#   · `IsAnimated` 的**输入侧**已补 `.webp`（读 `AnimatedWebpCache`，由 `IsAnimatedWebpAsync` 在确定时写）；
#   · 且**预缩放点已不再调 `IsAnimated`**：它改为消费 `ProcessAsync` 在路由判定前单点求值的
#     `inputMayHaveMultipleFrames`（即 `QueueProcessor.cs` 的 `maxDimSkipAnimated`）。
#   ⇒ 今天动图 `.webp` 是**被真正跳过**的，不再是「碰巧没坏」。
#   ⚠ 但**本门禁 ②③④ 走的是 `ServiceProbe firstframe`（直调引擎）**，与上面这条预缩放行为**无关** ——
#     本条只是机制记录，**不得**据它把 ②③④ 改回 app 级（见上面的注释锁）。
#
# 断言：
#   ① 素材确实 >1 帧（前提不成立必须**红**，不得静默给绿）；
#   ② 直调载体正例：探针 `firstframe-case` 的 `engine` == **128x96**、**且明确不等于 128x960**、
#      `stats`（引擎反推尺寸）== `engine`、`ref`（**单帧参照**，独立观测量）== `engine`、`log=scaled`；
#   ③ 日志自洽：编码步命令行（`firstframe-enc`）里的 `-video_size` == 产物尺寸，
#      且探针的**结构化行**里不得出现 128x960（自相矛盾的错图指纹；⚠ 只扫结构化行，
#      不扫整份 stdout —— 探针的 PASS 文案里自带 `128x960` 作说明 ⇒ 扫全文会**恒红**）；
#   ④ 解码步命令行（`firstframe-cmd`）真的带 `-frames:v 1` 且落在 `-i` **之后**（该参数是**输出侧**选项），
#      并**载体自检**：该行不得含 `-video_size`（含它说明取到的是编码步 ⇒ 位置断言会恒真，§6 第 74 条）；
#   ⑤ 负控（**CLI 路径**）：单帧 512x384 PNG + 同选项 ⇒ **128x96**，必须绿（证明单帧路径未被改坏）；
#   ⑥ 被测 exe 的 mtime 跑前跑后一致（§6 第 84 条：不一致则本轮读数作废）；
#   ⑦ 残留自证：探针的运行级目录必须自清 + 门禁的运行级 GUID 目录必须清零。
#
# 退出码：0 = 全绿，1 = 有失败。⚠ 结尾必须是**完整 if/else**（§6 第 67 条：清理步会污染
#   `$LASTEXITCODE`，只写 `if ($fail -gt 0) { exit 1 }` 会让全绿路径以被污染的 2 退出 ⇒ 假红）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1，门禁 `verify-ps-compat.ps1`）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；
#   双引号串内禁全角弯引号（用 「」）；取子进程输出一律 `Start-Process` + 重定向（`& exe` 部分宿主静默失败）。
# ⚠ **禁止 `Start-Process pwsh`**（宿主安全策略会拦 shell/LOLBin 目标）。
# ⚠ `Start-Process -PassThru`（**不带 `-Wait`**）的 `.ExitCode` 在 **PS 5.1 恒 `$null`**（实测）
#   ⇒ `[int]$null == 0` 会把「读不到」伪装成「exit 0」⇒ 假绿。本文件凡需退出码的调用**一律带 `-Wait`**
#   （探针与被测 CLI 都是短命进程，不会挂）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$pr  = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $pr)) { $pr = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"

# ⚠ 运行级隔离（GUID）：固定 `$work` 会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$val  = "$root/tests/output/validate"
$gid  = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = "$val/engfirst_${gid}"
New-Item -ItemType Directory -Force -Path $work | Out-Null

$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Host "PASS $msg" -ForegroundColor Green }
  else     { $script:fail++; Write-Host "FAIL $msg" -ForegroundColor Red }
}

# 取子进程输出一律走 Start-Process + 重定向（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）。
# 返回 @{ code = 退出码; text = stdout+stderr }
function Exec([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e)) }
}

# 真实帧数：ffprobe `-count_frames`（**不用** `frame=` 日志字段）。取不到（如单帧静图的空值）记 0。
# ⚠ 只认**整行就是数字**的输出行：ffprobe 的报错里会带上含数字的路径（GUID 目录名），
#   宽松匹配 `\d+` 会把路径里的数字当帧数 ⇒ 假绿。
# ⚠⚠ **`v:0` 陷阱的适用边界（2026-09-18 实测；team-lead 已裁定此前把它的严重度写重过，此处按实测收窄）**：
#   本函数用的 `-select_streams v:0` 对**本文件的素材**（webp 动画：只有 **1 条视频流**、**无音频**）
#   与 `-select_streams v`、甚至与「全流」**同值** ⇒ **此处无害**，因此**本行保持不变**（本轮只加注释）。
#   ⚠ 但**不得**把它抄到判据路径上 —— 两类容器会咬人：
#     · **多 item 容器**：AVIF 动画的 `v:0` 恒解析到**主图 item** ⇒ 帧数恒报 **1**（真值 10 也报 1）
#       ⇒ 在决策路径上等于**静默丢帧**（实测：流 0 报 1、全视频流是 `[1,10]`）。
#     · **带音频的容器**：mp4「1 视频帧 + 音频」的全流 packets 是 `1 + 45`（**音频贡献 45**）
#       ⇒ 「全流取 max」会把单帧文件判成 **Multiple（假 Multiple）**，「全流求和」更离谱（46/55）。
#   ⇒ **唯一正确形态**：`-select_streams v`（**滤掉音频**；不是 `v:0`，也不是全流）+ 然后**取 max**（**不得求和**）。
#   ⚠ 同一形态对 `-count_frames` 一样成立（全流 `1 + 44`）⇒ **换方法救不了**，两条都必须 `-select_streams v`。
function FrameCount([string]$path) {
  $r = Exec $fp "-v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames -of csv=p=0 `"$path`"" "probe_$(Split-Path $path -Leaf)"
  $n = 0
  foreach ($ln in ($r.text -split "`r?`n")) {
    if ($ln -match '^\s*(\d+)\s*$') { $n = [int]$Matches[1] }
  }
  return $n
}

# 尺寸 WxH（ffprobe csv）⇒ 字符串 "128x96"；探测失败返回 ""（同样只认整行 `W,H` 形态）。
function ImgSize([string]$path) {
  $r = Exec $fp "-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 `"$path`"" "size_$(Split-Path $path -Leaf)"
  $s = ""
  foreach ($ln in ($r.text -split "`r?`n")) {
    if ($ln -match '^\s*(\d+)\s*,\s*(\d+)\s*$') { $s = ($Matches[1] + "x" + $Matches[2]) }
  }
  return $s
}

# 取探针输出里**以某前缀开头**的那一行（返回前缀之后的部分；找不到返回 ""）。
# ⚠ 用 StartsWith 而不是 `-match`：结构化行的前缀是稳定契约，值里可能含正则元字符（Windows 路径）。
function Line1([string]$text, [string]$prefix) {
  foreach ($ln in ($text -split "`r?`n")) {
    if ($ln.StartsWith($prefix)) { return $ln.Substring($prefix.Length) }
  }
  return ""
}

# 从结构化行里取 `key=value`（值不含空白；找不到返回 ""）。
function Field([string]$line, [string]$key) {
  if ([string]::IsNullOrEmpty($line)) { return "" }
  $m = [regex]::Match($line, [regex]::Escape($key) + '=([^\s]+)')
  if ($m.Success) { return $m.Groups[1].Value }
  return ""
}

foreach ($need in @($exe, $pr, $ff, $fp)) {
  if (-not (Test-Path $need)) {
    try { [System.IO.Directory]::Delete($work, $true) } catch { }
    Write-Host "缺工具：$need" -ForegroundColor Red
    exit 1
  }
}
$exeMtimeBefore = (Get-Item $exe).LastWriteTimeUtc

# ── ① 素材自备 + 形态自检 ────────────────────────────────────────────────────────
Write-Host "`n### ① 自备 10 帧动画 webp 素材，并断言素材确实 >1 帧 ###" -ForegroundColor Cyan
$anim = "$work/anim.webp"
$gen = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=256x192:r=10:d=1`" -frames:v 10 -c:v libwebp -loop 0 -q:v 80 `"$anim`"" "gen_anim"
CK ((Test-Path $anim) -and ((Get-Item $anim).Length -gt 1000)) "素材已生成（gen exit=$($gen.code)，$((Get-Item $anim -ErrorAction SilentlyContinue).Length) B）"
$animFrames = FrameCount $anim
$animSize = ImgSize $anim
Write-Host "  [素材] $animSize 帧数=$animFrames"
CK ($animFrames -gt 1) "素材确实是多帧（ffprobe -count_frames = $animFrames，必须 >1；否则本门禁前提不成立）"
CK ($animSize -eq "256x192") "素材尺寸 256x192（实 $animSize）"

# ── ② 直调载体正例：ServiceProbe firstframe ⇒ 128x96（不是 128x960）────────────────
Write-Host "`n### ② 直调引擎正例：ServiceProbe firstframe（多帧 webp + 最长边 128）⇒ 128x96 ###" -ForegroundColor Cyan
# ⚠ 为什么不经 CLI：见文件头「为什么 ②③④ 改用直调载体」。探针**必须**带 options 才会走到几何缩放段。
$probe = Exec $pr (@('firstframe', ('"' + $anim + '"'), ('"' + $work + '"')) -join ' ') "probe_ff"
Write-Host "  [探针] exit=$($probe.code)"
foreach ($ln in ($probe.text -split "`r?`n")) {
  if ($ln.StartsWith('firstframe-') -and -not $ln.StartsWith('firstframe-out=')) { Write-Host "  $ln" -ForegroundColor DarkGray }
}
CK ($probe.code -eq 0) "② 探针退出码 0（实 $($probe.code)）"
$mPr = [regex]::Match($probe.text, 'PROBE RESULT: pass=(\d+) fail=0')
CK ($mPr.Success) "② 探针全绿（PROBE RESULT: pass=N fail=0，实得 $($mPr.Value)）"

$caseLine = Line1 $probe.text 'firstframe-case '
CK ($caseLine.Length -gt 0) "② 取到 firstframe-case 结构化行（判据不靠散文）"
$srcF  = Field $caseLine 'src'
$maxF  = Field $caseLine 'maxdim'
$engF  = Field $caseLine 'engine'
$stF   = Field $caseLine 'stats'
$refF  = Field $caseLine 'ref'
$logF  = Field $caseLine 'log'
$fv1F  = Field $caseLine 'framesv1'
$v1aiF = Field $caseLine 'v1afteri'
CK ($srcF -eq "256x192") "② 探针报告的输入尺寸 == 256x192（实 「$srcF」）"
CK ($maxF -eq "128") "② 探针用的最长边限制 == 128（实 「$maxF」）"
CK ($engF -eq "128x96") "② 产物尺寸 == 128x96（实 「$engF」）"
CK ($engF -ne "128x960") "② 产物尺寸明确**不等于** 128x960（10 帧竖叠的错图指纹；实 「$engF」）"
CK (($stF.Length -gt 0) -and ($stF -eq $engF)) "② Stats 反推尺寸 == 产物尺寸（stats 「$stF」 / engine 「$engF」）—— 自相矛盾不得出现"
# ⭐ 本门禁的**主判据**：产物 == **单帧参照**（探针用同一滤镜只取首帧缩放得到的独立观测量）。
#   缺 `-frames:v 1` 时另一条边会变成 N 倍 ⇒ 与参照不等。
CK (($refF.Length -gt 0) -and ($refF -eq $engF)) "② 产物 == **单帧参照**（ref 「$refF」 / engine 「$engF」）⇒ 解码步只取首帧；竖叠会得到 N 倍的另一条边"
CK ($logF -eq "scaled") "② 探针日志标记 log=scaled（实 「$logF」）⇒ 几何缩放段确实执行（否则尺寸断言无判别力）"
# ⚠ 只扫**结构化行**（`firstframe-*`），不扫探针的 PASS/FAIL 散文行：探针自己的断言文案里会**引用**
#   `128x960` 作为说明（"缺 -frames:v 1 时另一条边会变成 N 倍，如 128x96 → 128x960"）⇒
#   对整份 stdout 做 `-notmatch '128x960'` 会**恒红**（自指断言）。结构化行才是判据载体。
$struct = ""
foreach ($ln in ($probe.text -split "`r?`n")) { if ($ln.StartsWith('firstframe-')) { $struct += $ln + "`n" } }
CK ($struct -notmatch '128x960') "② 探针的**结构化行**里没有 128x960（自相矛盾的错图指纹不得出现）"

# ── ③ 日志自洽：编码步自报尺寸 == 产物尺寸 ─────────────────────────────────────────
Write-Host "`n### ③ 编码步命令行自报尺寸 == 产物尺寸 ###" -ForegroundColor Cyan
$encLine = Line1 $probe.text 'firstframe-enc '
CK ($encLine.Length -gt 0) "③ 取到编码步命令行（firstframe-enc）"
$mVs = [regex]::Match($encLine, '\-video_size (\d+x\d+)')
$vsF = ""
if ($mVs.Success) { $vsF = $mVs.Groups[1].Value }
CK ($vsF -eq $engF) "③ 引擎编码所用尺寸 == 产物尺寸（-video_size 「$vsF」 / engine 「$engF」）"

# ── ④ 解码步命令行真的带 -frames:v 1（且落在 -i 之后：它是输出侧选项）──────────────
Write-Host "`n### ④ 解码步命令行结构：-i <file> -frames:v 1 -vf ... ###" -ForegroundColor Cyan
$cmdLine = Line1 $probe.text 'firstframe-cmd '
CK ($cmdLine.Length -gt 0) "④ 取到解码步命令行（firstframe-cmd）"
# ⚠ **载体自检**（§6 第 74 条：载体选错 ⇒ 断言恒真）：解码步**不得**含编码特征。
#   引擎日志里有**两条** `-f rawvideo`（解码步 RawColorPipeline.cs:130-132，`-i` 在前；
#   编码步 BuildEncodeArgs `RawColorPipeline.cs:755` 的 `:797`，`-f rawvideo` 在前并带 `-video_size`）
#   ⇒ 若这里取到的是编码步，下面那条「-frames:v 1 在 -i 之后」会变成恒真断言。
CK ($cmdLine -notmatch '\-video_size') "④ 载体自检：该行**不含** `-video_size`（含它说明取到的是编码步 ⇒ 位置断言会恒真）"
CK ($cmdLine -match '\-f rawvideo') "④ 载体自检：该行确实是 `-f rawvideo` 形态的解码步"
CK ($cmdLine -match '\-i "[^"]*anim\.webp"') "④ 该行喂的确实是本门禁自造的 anim.webp（前提不成立则整条断言无意义）"
$iV1 = $cmdLine.IndexOf('-frames:v 1', [System.StringComparison]::Ordinal)
$iIn = $cmdLine.IndexOf('-i "', [System.StringComparison]::Ordinal)
CK (($iV1 -ge 0) -and ($iIn -ge 0) -and ($iV1 -gt $iIn)) `
   "④ 解码命令行带 「-frames:v 1」 且位于 「-i」 之后（下标 $iV1 > $iIn；限制**写出**帧数，放 -i 前会被当输入选项而无效）"

# ── ⑤ 负控：单帧 512x384 PNG + 同选项 ⇒ 128x96（必须绿）───────────────────────────
Write-Host "`n### ⑤ 负控（CLI 路径）：单帧 512x384 PNG + 同选项 ⇒ 128x96（正常路径不得被改坏）###" -ForegroundColor Cyan
# ⚠ 负控**故意保留完整 CLI**：单帧输入不是动画 ⇒ 引擎照旧可路由 ⇒ 该路径今天仍有效。
$single = "$work/single.png"
$gens = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=512x384`" -frames:v 1 `"$single`"" "gen_single"
CK ((Test-Path $single) -and ((Get-Item $single).Length -gt 1000)) "负控素材已生成（gen exit=$($gens.code)）"
$singleFrames = FrameCount $single
Write-Host "  [负控素材] $(ImgSize $single) 帧数=$singleFrames"
CK ($singleFrames -eq 1) "⑤ 负控素材确实是单帧（实 $singleFrames）—— 与正例的多帧形成对照"
$outB = "$work/outB"
$runB = Exec $exe "--headless -i `"$single`" -o `"$outB`" -f webp --color-engine engine --color-space BT.2020 --max-dimension-enabled true --max-dimension 128" "runB"
$prodB = "$outB/single.webp"
CK ($runB.code -eq 0) "⑤ 负控退出码 0（实 $($runB.code)）"
CK (Test-Path $prodB) "⑤ 负控产物已生成（$prodB）"
$sizeB = ImgSize $prodB
$framesB = FrameCount $prodB
Write-Host "  [负控产物] $sizeB 帧数=$framesB"
CK ($sizeB -eq "128x96") "⑤ 负控产物尺寸 == 128x96（实 $sizeB）—— 单帧正常路径未被改坏"
CK ($framesB -eq 1) "⑤ 负控产物帧数 == 1（实 $framesB）"

# ── ⑥ 被测 exe mtime 一致（§6 第 84 条：跑中途被重建则本轮读数作废）──────────────
Write-Host "`n### ⑥ 被测 exe mtime 跑前跑后一致 ###" -ForegroundColor Cyan
$exeMtimeAfter = (Get-Item $exe).LastWriteTimeUtc
CK ($exeMtimeBefore -eq $exeMtimeAfter) "⑥ 被测 exe 未被中途重建（$exe）"

# ── ⑦ 残留自证 ────────────────────────────────────────────────────────────────
Write-Host "`n### ⑦ 残留自证：探针的运行级目录自清 + 门禁的运行级目录清零 ###" -ForegroundColor Cyan
$probeDir = Line1 $probe.text 'firstframe-out='
CK ($probeDir.Length -gt 0) "⑦ 探针报告了运行级目录（firstframe-out=…）"
if ($probeDir.Length -gt 0) {
  $pd = $probeDir -replace '\\', '/'
  CK (-not (Test-Path $pd)) "⑦ 探针的运行级目录已自清（无残留）：$pd"
}
$mc = [regex]::Match($probe.text, 'firstframe-cleanup: 删除文件 (\d+)/(\d+)')
if ($mc.Success) {
  CK ($mc.Groups[1].Value -eq $mc.Groups[2].Value) "⑦ 探针按计数自清：删除 $($mc.Groups[1].Value)/$($mc.Groups[2].Value) 个文件（必须相等）"
} else {
  CK $false "⑦ 缺 firstframe-cleanup 行（探针自清未执行）"
}
# 清理一律用 **.NET API** 绕过宿主批量删除守卫（§6 第 57/67 条：守卫会抛 terminating error 并污染
# `$LASTEXITCODE`，`-EA SilentlyContinue` 与 try/catch 都挡不住）。本脚本按计数判定 ⇒ 跑绿自清。
try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
CK (-not (Test-Path $work)) "⑦ 运行级目录已清零：$work"

Write-Host ""
Write-Host "===== engine-firstframe: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 退出码必须在清理之后，且必须写完整 if/else（§6 第 67 条）。
if ($fail -gt 0) { exit 1 } else { exit 0 }
