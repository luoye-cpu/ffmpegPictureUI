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
function Run-Queue([string]$inFile, [string]$outDir, [string[]]$extra = @()) {
  if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue }
  New-Item -ItemType Directory -Path $outDir -Force | Out-Null
  $log = Join-Path $work ("run_" + (Split-Path $outDir -Leaf) + ".txt")
  $argv = @('--headless', '-i', $inFile, '-o', $outDir, '-f', 'avif', '-q', '60') + $extra
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

Write-Host "`n===== PASS=$pass FAIL=$fail SKIP=$skip =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# 跑绿自清（跑红保留 GUID 目录供排查）—— 必须在 exit 之前，且用显式 if/else 给出退出码（§6 第 66/67 条）。
if ($fail -eq 0) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
if ($fail -gt 0) { exit 1 } else { exit 0 }
