# verify-gif-avif-framelist.ps1 —— P2-4 / P2-2 回归锁（GIF→AVIF 两步法的帧表命令行 + 队列失败计数）
#
# 为什么需要它：
#   P2-4  两步法曾把每帧的**绝对路径**拼进 avifenc 命令行。Windows 的 CreateProcess 命令行总长上限是
#         32767 字符 ⇒ 默认临时目录下一帧约 98 字符，**约 330 帧就满**，420 帧的普通 GIF 实测直接
#         ErrorStartingProcess「文件名或扩展名太长」（一个都跑不出来）。现在改传相对文件名 +
#         WorkingDirectory（一帧 11 字符 ⇒ 约 2900 帧），并对超限做**显式失败**预检。
#   P2-2  失败项的 _failedCount 自增曾挂在“算得出耗时”的分支里；专项通路在子方法内就发布终态
#         （CompletedAt 还没写）⇒ 一条失败任务被汇总成「完成 1 项, 失败 0 项」且按 Info 级别打印。
#
# 用法：pwsh -NoProfile -File tests/scripts/verify-gif-avif-framelist.ps1
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ff = "$env:FFMPEGGUI_FFMPEG_DIR/ffmpeg.exe"
$fp = "$env:FFMPEGGUI_FFMPEG_DIR/ffprobe.exe"
$avifenc = "$root/publish/PLAN/artifacts/avifenc.exe"
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$work` 会让**并发的同名实例**互删 `_exec.out/_exec.err`
#   与 `out420`/`out3000` 等中间产物 ⇒ 日志类断言齐红（与 `verify-color-wiring` 当年**同一形态**，§6 第 66 条）。
#   本脚本**在必跑清单内** ⇒ 自并发是**真实**风险，故隔离。
#   ⚠ 已核实**不是生产者**：全仓 `grep gifavif` 只命中本脚本自身 + 运行器注释 + 旧产物 ⇒ **没有第二方读它**。
#   ⚠ 代价已实测：GUID 化后 `Make-Gif` 的跨运行复用失效，每次都要重造素材 —— 3000 帧 **0.1 s** / 420 帧 **0.03 s**，
#     可忽略（若不实测，很容易误以为这里很贵而放弃隔离）。
$work = "$root/tests/output/gifavif_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Path $work -Force | Out-Null
# 沙箱按进程拦 %TEMP% 写（ffmpeg 子进程被拒 ⇒ 像素路 exit -13 假红，见 §6 沙箱一条）⇒ 本运行目录兼作 TMP/TEMP。
$env:TMP = $work; $env:TEMP = $work

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$work/_exec.out"; $e = "$work/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# 只读 stdout 版（取「工具的值」专用）：ffprobe 的 `-of csv` 帧数走 **stdout**。下面的 AvifFrames 会把
#   每一行 `-replace '[^0-9]',''` 再取最大值 ⇒ 合并进来的 stderr 里任何带数字的文本（本机实测 ffprobe
#   失败时 stdout 0 B / stderr 166 B，含报错与带数字的路径）都会变成一个**看似合法的帧数**去和 420 比。
#   读 ffmpeg 造素材的进度/报错（**只在 stderr**）时仍用 Exec。
function ExecOut([string]$file, [string]$argStr){
  $o = "$work/_exec.out"; $e = "$work/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return [System.IO.File]::ReadAllText($o)
}

$script:pass = 0; $script:fail = 0; $script:skip = 0
function CK([bool]$ok, [string]$what) {
  if ($ok) { $script:pass++; Write-Host "PASS $what" -ForegroundColor Green }
  else { $script:fail++; Write-Host "FAIL $what" -ForegroundColor Red }
}
function SKIP([string]$what) { $script:skip++; Write-Host "SKIP $what" -ForegroundColor Yellow }

foreach ($need in @($exe, $ff, $fp)) { if (-not (Test-Path $need)) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue; Write-Host "缺 $need" -ForegroundColor Red; exit 1 } }
if (-not (Test-Path $avifenc)) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue; SKIP "avifenc.exe 不在位 ⇒ 两步法无法执行，本门禁整批跳过（不给假绿）"; Write-Host "===== PASS=$pass FAIL=$fail SKIP=$skip =====" ; exit 0 }

# ── 素材：按帧数现造小 GIF（已存在则复用，避免每次重跑都抽帧） ──────────────────
function Make-Gif([string]$name, [int]$frames) {
  $p = Join-Path $work $name
  if ((Test-Path $p) -and ((Get-Item $p).Length -gt 1000)) { return $p }
  $dur = [math]::Ceiling($frames / 25.0)
  $null = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=64x64:r=25:d=$dur`" -frames:v $frames -pix_fmt pal8 `"$p`""
  return $p
}
$gif420 = Make-Gif "frames420.gif" 420
$gif3000 = Make-Gif "frames3000.gif" 3000

# ── 跑一次 headless 队列，回传 stdout 文本与退出码 ───────────────────────────────
# ⚠ 成功项的 item.Log 只在 Debug 级别才刷出（失败项才按 Error 刷）⇒ 要看 avifenc 回显必须 --log-level debug。
function Run-Queue([string]$inFile, [string]$outDir, [string[]]$extra = @(), [string]$fmt = 'avif', [int]$q = 60) {
  if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue }
  New-Item -ItemType Directory -Path $outDir -Force | Out-Null
  $log = Join-Path $work ("run_" + (Split-Path $outDir -Leaf) + ".txt")
  $argv = @('--headless', '-i', $inFile, '-o', $outDir, '-f', $fmt, '-q', "$q") + $extra
  $p = Start-Process -FilePath $exe -ArgumentList $argv `
        -NoNewWindow -Wait -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
  return @{ code = $p.ExitCode; text = [System.IO.File]::ReadAllText($log) }
}

# 动图 AVIF 的真实帧数：ffprobe 对 -select_streams v:0 只报主图 1 帧（实测），必须看全部视频流取最大值。
function AvifFrames([string]$path) {
  $rows = (ExecOut $fp "-v error -select_streams v -count_frames -show_entries stream=nb_read_frames -of csv=p=0 `"$path`"") -split "`r?`n"
  $n = 0
  foreach ($r in $rows) { $v = 0; if ([int]::TryParse(($r -replace '[^0-9]', ''), [ref]$v)) { if ($v -gt $n) { $n = $v } } }
  return $n
}

# ── ① 420 帧：旧写法在这条上必炸（ErrorStartingProcess），现在要真出 420 帧产物 ──
Write-Host "`n── ① 420 帧 GIF → 动图 AVIF（P2-4 主证）──" -ForegroundColor Cyan
$r1 = Run-Queue $gif420 (Join-Path $work "out420") @('--log-level', 'debug')
$out420 = Join-Path $work "out420/frames420.avif"
CK ($r1.code -eq 0) "420 帧转换退出码 0（旧写法这里是 Win32「文件名或扩展名太长」）"
CK (Test-Path $out420) "420 帧产物已生成"
if (Test-Path $out420) {
  $n420 = AvifFrames $out420
  CK ($n420 -eq 420) "产物按流实测 $n420 帧 == 源 420 帧（不是只编了主图）"
} else { SKIP "产物缺失，帧数断言无从执行（不给假绿）" }
CK ($r1.text -match '已完成 \(avifenc 两步法\)') "终态为「已完成 (avifenc 两步法)」"

# ── ② 日志回显必须是摘要：整条命令行有 4 万+ 字符，灌进 item.Log 会把日志冲爆 ──
Write-Host "`n── ② avifenc 命令行回显为摘要（防日志被几十万字节的参数淹没）──" -ForegroundColor Cyan
$echoLines = @($r1.text -split "`n" | Where-Object { $_ -match 'avifenc .*共 420 帧' })
CK ($echoLines.Count -ge 1) "日志里有一条带「共 420 帧」摘要的 avifenc 回显"
$longOnes = @($r1.text -split "`n" | Where-Object { $_.Length -gt 400 -and $_ -match 'f_0[0-9]{3}\.png' })
CK ($longOnes.Count -eq 0) "没有把逐帧文件名整列写进日志（>400 字符的 avifenc 行 $($longOnes.Count) 条）"
# P7h 行为锁（不依赖源码措辞）：420 帧 ≥4 帧，一旦传 -j 1 就是段错误 0xC0000005。
$badJobs = @($r1.text -split "`n" | Where-Object { $_ -match 'avifenc .*-j 1( |$)' })
CK ($echoLines.Count -ge 1 -and $badJobs.Count -eq 0) "avifenc 命令行里没有出现 -j 1（≥4 帧必段错误，P7h；以拿到回显行为前提，避免真空绿）"

# ── ③ 3000 帧：超出命令行上限必须“显式失败 + 说清怎么办”，而不是 Win32 那句 ──
Write-Host "`n── ③ 3000 帧超出上限 ⇒ 显式失败（P2-4 预检 + P2-2 失败计数）──" -ForegroundColor Cyan
$r2 = Run-Queue $gif3000 (Join-Path $work "out3000")
CK ($r2.code -ne 0) "退出码非 0"
CK ($r2.text -match '帧数超出 avifenc 命令行上限') "终态写明「帧数超出 avifenc 命令行上限」"
CK ($r2.text -match '超过 Windows 上限 32767') "日志给出字符数与上限"
CK ($r2.text -match '可行做法') "日志给出可执行的处置建议"
CK ($r2.text -notmatch 'ErrorStartingProcess|文件名或扩展名太长') "不再出现 Win32 的「文件名或扩展名太长」"
$leftovers = @(Get-ChildItem (Join-Path $work "out3000") -Recurse -File -ErrorAction SilentlyContinue)
CK ($leftovers.Count -eq 0) "失败后不在输出目录留下半成品（P1-4 口径一致）"
# P2-2 锁：失败必须计进汇总（旧写法这里恒为「失败 0 项」）
CK ($r2.text -match '完成 1 项, 失败 1 项') "汇总按「完成 1 项, 失败 1 项」计数（P2-2：失败计数与耗时解耦）"
CK ($r2.text -match '❌ \[1/1\].*帧数超出 avifenc 命令行上限') "失败项以错误级（❌）打印，不是 Info"

# ── ④ 对照组：一项成功一项失败，汇总必须报「完成 2 项, 失败 1 项」 ──────────────
Write-Host "`n── ④ 混合队列（成功 + 失败）汇总计数 ──" -ForegroundColor Cyan
$mix = Join-Path $work "mix"
if (Test-Path $mix) { Remove-Item $mix -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path "$mix/src" -Force | Out-Null
Copy-Item $gif420 "$mix/src/a.gif" -Force
Copy-Item $gif3000 "$mix/src/b.gif" -Force
$logm = Join-Path $work "run_mix.txt"
$pm = Start-Process -FilePath $exe -ArgumentList @('--headless', '-i', "$mix/src", '-o', "$mix/dst", '-f', 'avif', '-q', '60') `
      -NoNewWindow -Wait -PassThru -RedirectStandardOutput $logm -RedirectStandardError "$logm.err"
$tm = [System.IO.File]::ReadAllText($logm)
CK ($tm -match '完成 2 项, 失败 1 项') "混合队列汇总「完成 2 项, 失败 1 项」（成功项未被吞）"
CK ($pm.ExitCode -eq 1) "混合队列退出码 1（存在失败项）"

# ── ⑤ 自我设定覆盖必须点名：恒定帧率抹平不均延时 + 自设质量下限（2026-10-21 补）───────────
# 缺陷形状：两条两步法出口（gif→avif / avif→{gif,webp}）都用**恒定帧率**重建（`-framerate N`，
#   无 --animation-fps 时为写死的 33），而源的不均延时在 PNG 中间件处丢失；gif→avif 另有
#   `Math.Max(20, quality)` 把用户的质量下限抬高。修前两者都一字不提（实测 -q 5 静默成 -q 20）。
# 本段同时锁**探针不写死 v:0**：本工具链 avifenc 产的动画 AVIF 有 4 条流，v:0 只有 1 帧（主图），
#   探 v:0 会把"延时不均"洗成"无延时可抹平"⇒ 必须按 nb_frames 选**帧数最多**的那条流。
Write-Host "`n── ⑤ 恒定帧率/质量下限必须点名（含 AVIF 多流选流臂）──" -ForegroundColor Cyan
$ufd = Join-Path $work "uneven_src"
New-Item -ItemType Directory -Path $ufd -Force | Out-Null
foreach ($c in @('red', 'green', 'blue')) {
  # ⚠ `${c}:` 必须带花括号：PowerShell 会把 `$c:s` 解析成「作用域 c 的变量 s」（空值）⇒ 滤镜串被削坏、PNG 一个都造不出来。
  $null = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"color=c=${c}:s=64x64:d=1`" -frames:v 1 `"$ufd/$c.png`""
}
# 逐帧 duration：120 / 480 / 200 ms（末帧重复一次，让前 3 帧都有后继 ⇒ 3 段可测差值）
$lst = Join-Path $ufd "list.txt"
$u = ($ufd -replace '\\', '/')
$lstLines = @("file '$u/red.png'", 'duration 0.12',
              "file '$u/green.png'", 'duration 0.48',
              "file '$u/blue.png'", 'duration 0.20',
              "file '$u/blue.png'")
# ⚠ concat 清单必须 **LF**：Set-Content 在 Windows 上写 CRLF，`\r` 会被并进文件名/时长 token。
[System.IO.File]::WriteAllText($lst, ($lstLines -join "`n") + "`n")
$unevenGif = Join-Path $ufd "uneven.gif"
$null = Exec $ff "-y -hide_banner -loglevel error -f concat -safe 0 -i `"$lst`" -fps_mode vfr `"$unevenGif`""
$up = (ExecOut $fp "-v error -select_streams 0 -show_entries frame=pts_time -of csv=p=0 `"$unevenGif`"") -split "`r?`n" | Where-Object { $_ -ne '' }
CK ($up.Count -eq 4) "夹具自检：不等延时 GIF 实测 $($up.Count) 帧时间戳（期望 4）"
if ($up.Count -ge 2) {
  $d1 = [math]::Round(([double]$up[1] - [double]$up[0]) * 1000); $d2 = [math]::Round(([double]$up[2] - [double]$up[1]) * 1000)
  CK ($d1 -eq 120 -and $d2 -eq 480) "夹具自检：前两段延时实测 $d1/$d2 ms（期望 120/480，否则本段无牙）"
} else { SKIP "夹具时间戳不足 ⇒ 无牙判据不执行（不给假绿）" }

# 臂 A：gif→avif，-q 5（低于本仓自设下限 20）
$rA = Run-Queue $unevenGif (Join-Path $work "uneven_a") @('--log-level', 'debug') 'avif' 5
CK ($rA.text -match '质量 5 被抬到 20') "臂A：质量下限被抬时**点名**（-q 5 ⇒ 日志出现「质量 5 被抬到 20」）"
CK ($rA.text -match '源逐帧时长不均' -and $rA.text -match '120\.\.480ms') "臂A：不均延时被抹平时**点名**（实测 120..480ms）"
CK ($rA.text -match '恒定帧率是本出口的现有能力边界') "臂A：点名时写清这是能力边界，不是逐帧保真"

# 臂 B（正控）：把同一份延时编成**动画 AVIF**（4/16/8 @ timescale 10 ⇒ 400/1600ms），
#   再走 avif→webp。这条只有在**按 nb_frames 选流**时才可能报警（v:0 只有主图 1 帧）。
$ufr = Join-Path $work "uneven_frames"
New-Item -ItemType Directory -Path $ufr -Force | Out-Null
$null = Exec $ff "-y -hide_banner -loglevel error -i `"$unevenGif`" -fps_mode passthrough -start_number 1 `"$ufr/f_%04d.png`""
$ufiles = @(Get-ChildItem $ufr -Filter 'f_*.png' | Sort-Object Name)
CK ($ufiles.Count -ge 3) "臂B 前置：从不等 GIF 抽出 $($ufiles.Count) 帧 PNG（需 ≥3）"
$unevenAvif = Join-Path $work "uneven_delay.avif"
if ($ufiles.Count -ge 3) {
  $null = Exec $avifenc "-j 4 --timescale 10 --duration 4 `"$($ufiles[0].FullName)`" --duration 16 `"$($ufiles[1].FullName)`" --duration 8 `"$($ufiles[2].FullName)`" `"$unevenAvif`""
  CK (Test-Path $unevenAvif) "臂B 前置：不等延时 AVIF 已生成"
} else { SKIP "臂B 前置不足 ⇒ 跳过多流选流臂（不给假绿）" }
if (Test-Path $unevenAvif) {
  # 前提自锁：v:0（主图）只有 1 帧 —— 若构建变了导致 v:0 就有多帧，本臂的"选流"意义要重新评估
  $v0n = ((ExecOut $fp "-v error -select_streams v:0 -show_entries stream=nb_frames -of csv=p=0 `"$unevenAvif`"") -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -First 1)
  CK ("$v0n".Trim() -eq '1') "臂B 前提：v:0（主图）实测只有 1 帧（实 $("$v0n".Trim())）⇒ 写死 v:0 必洗白"
  $rB = Run-Queue $unevenAvif (Join-Path $work "uneven_b") @('--log-level', 'debug') 'webp' 5
  CK ($rB.text -match '源逐帧时长不均' -and $rB.text -match '400\.\.1600ms') "臂B：AVIF 输入的不均延时同样点名（实测 400..1600ms ⇒ 选中的是动画流，不是主图）"
  CK ($rB.text -notmatch '被抬到') "臂B：avif→webp 不设质量下限 ⇒ 不得出现「被抬到」（-q 5 原样下发）"
}

# 臂 C（负控）：臂 A 的产物是**恒定 33fps** 重建的 ⇒ 延时已均匀，本段判据必须一声不响。
#   没有这条，臂 A/B 的"有报警"可能来自任何无条件打印（判据无区分力）。
$flatAvif = Join-Path $work "uneven_a/uneven.avif"
if (Test-Path $flatAvif) {
  $rC = Run-Queue $flatAvif (Join-Path $work "flat_c") @('--log-level', 'debug') 'webp' 5
  CK ($rC.text -match '颜色 \d+ 帧') "臂C 正控：负控臂确实跑到选流/编码段（日志有颜色帧数）"
  CK (($rC.text -split "`n" | Where-Object { $_ -match '时长不均|被抬到' }).Count -eq 0) "臂C 负控：恒定帧率源的产物**不得**报延时不均（判据有区分力）"
} else { SKIP "负控输入缺失（臂A 产物不在）⇒ 不执行（不给假绿）" }

# ── ⑥ 输入侧「绕过引擎」的点名必须与真实分派支逐条对齐（2026-10-22 普查抓到的假陈述）──────
# 缺陷形状（实测）：旧名单写的是 `inputExt is ".jxl" or ".jxr" or ".avif"`，可 `.avif` **只有** → gif/webp
#   才真的走两步法专用管线；`.avif → png` 由引擎执行 ⇒ 同一份日志里既有「本次由 .avif 专用输入管线 处理，
#   色彩引擎尚未接入该路径」又有「[色彩引擎] 决策：…」＝**宣称没接、其实接了**。
#   另一头 `.gif → avif`（avifenc 两步法）**根本不在名单里** ⇒ 那条绕过是静默的。
#   ⇒ 本段用「同容器、不同输出」的判别对钉住两个方向：该点名的一定要点名，不该点名的绝对不许说。
Write-Host "`n── ⑥ 绕过引擎的点名与真实分派支对齐（avif→png 不许谎报 / gif→avif 不许静默）──" -ForegroundColor Cyan
$e6gif = Join-Path $work "e6_gif2avif"
$r6a = Run-Queue $unevenGif $e6gif @('--log-level', 'debug') 'avif' 60
CK (($r6a.text -match '\.gif 专用输入管线') -and ($r6a.text -match '尚未接入该路径')) `
   "⑥ .gif → .avif：绕过引擎必须**点名**（旧名单漏了 .gif ⇒ 修复前该红：静默绕过）"
$e6a2g = Join-Path $work "e6_avif2gif"
$r6b = Run-Queue $unevenAvif $e6a2g @('--log-level', 'debug') 'gif' 60
CK (($r6b.text -match '\.avif 专用输入管线') -and ($r6b.text -match '尚未接入该路径')) `
   "⑥ .avif → .gif：真绕过 ⇒ 必须点名（与上一臂同形，判据不是只对着新加的那条写）"
# 判别对必须用**静图** AVIF：动画 AVIF → png 会被 `ShouldRoute` 的 AnimatedInput 拦下（引擎本来就不该按
#   单帧处理动画）⇒ 拿它当"引擎应当执行"的正控会假红（本机第一次跑就撞上了）。
$redPng = Join-Path $ufd "red.png"
$stillDir = Join-Path $work "e6_still"
$r6p0 = Run-Queue $redPng $stillDir @() 'avif' 60
$stillAvif = Join-Path $stillDir "red.avif"
CK (((Test-Path $stillAvif)) -or ($r6p0.code -eq 0)) "⑥ 前置：由静图 PNG 造出静图 AVIF 夹具（$stillAvif 在盘=$([bool](Test-Path $stillAvif))）"
$e6a2p = Join-Path $work "e6_avif2png"
$r6c = Run-Queue $stillAvif $e6a2p @('--log-level', 'debug') 'png' 60
CK ($r6c.text -match '\[色彩引擎\] 决策') "⑥ 正控：静图 .avif → .png 这一臂**确实**由引擎执行（日志有决策行）"
CK ($r6c.text -notmatch '尚未接入该路径') `
   "⑥ 静图 .avif → .png：**不得**谎报「引擎尚未接入」（旧写法必红：引擎明明跑了却报没接）"
$qpFile6 = Join-Path $root 'src/FfmpegGui/Services/QueueProcessor.cs'
$qp6 = [System.IO.File]::ReadAllText($qpFile6)
CK (($qp6 -match 'inputBypassedEngine') -and ($qp6 -notmatch 'inputExt is "\.jxl" or "\.jxr" or "\.avif"')) `
   "⑥ 形态锁（防再抄）：判据抽成 inputBypassedEngine，且 `.avif` 不再作为**裸项**混在输入名单里"
CK ((($qp6 -split 'ProcessAvifToGifWebpAsync').Count - 1) -ge 1) `
   "⑥ 上一条负断言的扫描面非空（同文件确有 ProcessAvifToGifWebpAsync ⇒ 不是读错文件的空命中）"

Write-Host "`n===== PASS=$pass FAIL=$fail SKIP=$skip =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# 跑绿自清（跑红保留 GUID 目录供排查）—— 必须在 exit 之前，且用显式 if/else 给出退出码（§6 第 66/67 条）。
if ($fail -eq 0) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
if ($fail -gt 0) { exit 1 } else { exit 0 }
