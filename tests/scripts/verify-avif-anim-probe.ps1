# verify-avif-anim-probe.ps1 —— AVIF 动画探测的「三态化 + 保守按多帧」专项门禁（2026-09-18 新增，`#136` 切片 A）
#
# 缺陷（本门禁要锁住的东西）：
#   `QueueProcessor.IsAnimatedAvifAsync` 原本**把「不知道」当成「不是」** —— 四条失败路径
#   （ffprobe 缺失 / `proc == null` / 被取消 / `ExitCode != 0` / 抛异常）**全部 `return false`**，
#   且**没有任何超时**（只有调用方的取消令牌）。
#   ⇒ 动画 AVIF + 非 gif/apng 目标时，一次探测抖动就把「不知道」固化成「静态」⇒ 引擎按单帧处理 ⇒ **静默丢帧**。
#   （这是 `#16`「探测失败语义三态化」在 avif 这条上的未修切片。）
#
# 修复判据（`#136` 切片 A）：
#   ① **补超时**：`DefaultAvifProbeTimeoutSec = 20`（与同链兄弟站点 `ProbeAvifStreamRolesAsync` 的
#      `WaitForExit(20000)` 同口径）+ 环境变量 `FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC` 覆盖（夹紧 [1,600]，
#      照 `ResolveJxlProbeTimeoutSec` 的风格）。超时 ⇒ `Kill(entireProcessTree: true)` + **点名**日志。
#      ⚠ 实现用「链接 CTS + 杀树看门狗」（与同步版 `ProbeAvifStreamRolesAsync` **同机制**）：
#        超时/取消 ⇒ Kill ⇒ 管道关闭 ⇒ 阻塞中的读**必然**返回。**保持异步**，不改回同步 `WaitForExit(int)`。
#   ② **三态化**：返回 `(bool animated, bool determinate)`。`determinate = true` **仅当** ffprobe
#      **正常退出** **且** 输出可解析出流信息；超时/取消/起不来/异常/输出不可解析 ⇒ `false`。
#   ③ **裁定（team-lead）：不确定 ⇒ 保守按「多帧」** —— 两个调用点都消费 `determinate`
#      （`:428` 的 `maxDimSkipAnimated` ⇒ 跳过预缩放；`:2690` 的 `isAnimatedAvif` ⇒ 走两步法）。
#      依据 = `InputMayHaveMultipleFrames` 的 XML 注释自述政策：「宁可回退传统管线，也不冒『静默只保留首帧』的风险」。
#
# 断言（共 19 条）：
#   ① 素材形态自检 2 条（`still.avif` 恰好 1 条视频流；`trunc.avif` 32 B 且 ffprobe **非 0 退出** —— 证明
#      「不确定」的触发器真是 ffprobe 失败，而不是别的原因）
#   ① 源码形态自检 5 条（默认 20 s / 注入通道与夹紧 / 异步 + `CancelAfter` + `Register` + `Kill` /
#      **点名三处**消费第三态（预缩放跳过 / 两步法入口 / 引擎路由输入侧子句）+ 反控证明点名式载体有牙）
#   ② 行为 8 条（**双向**）：静态 AVIF ⇒ 确定 ⇒ 走单命令 + **不**打不确定行（**不误伤**）；
#      截断 AVIF ⇒ 不确定 ⇒ **点名**日志 + 走两步法 + **不**出现「输入为静态 AVIF」+ 零产物（**有牙**）
#   ③ 注入值不误伤 1 条（`FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC=1` 下静态 AVIF 仍走单命令）
#   ④ 被测 exe mtime 跑前跑后一致 1 条（§6 第 84 条）
#   ⑦ 单独一次 `-Wait -PassThru` 取 `still.avif` 真退出码 1 条
#   ⑥ 残留自证 1 条
#
# ⚠⚠ **退出码的宿主陷阱（本轮实测，已写进 `§6` 第 97 条）**：Windows PowerShell 5.1 下
#   `Start-Process -PassThru`（**不带 `-Wait`**）的 `.ExitCode` **恒为 `$null`**，而 `[int]$null == 0`
#   ⇒ 写成 `$r.code -eq 0` 会在回退宿主上**假绿**、写成 `code -ne 0` 会**假红**（本轮首跑正是这样红的）。
#   实测：轮询到 `HasExited` 后读 ⇒ `<null>`；`WaitForExit()` 与 `Refresh()` **都救不回来**。
#   ⇒ 判据一律用宿主无关的「**产物数 + 日志形状**」；确需退出码的那一条**单独** `-Wait -PassThru` 另跑（见 ⑦）。
#
# ⚠⚠ **已登记的覆盖缺口（措辞不得大于扫描面，第 87 条）**：
#   **超时分支本身没有被行为实测。** 我**没能**构造出让 `ffprobe -show_entries stream=index,codec_type`
#   在 AVIF 上**挂住**的素材（与 JXL 那次的 1 B `FF` 不同）：
#     · 1 B / 截断 AVIF ⇒ ffprobe **秒退**（exit 1，`moov atom not found`）⇒ 走的是「异常退出」分支，不是超时分支；
#     · `mkfifo`（Git-Bash/MSYS）建出的 FIFO 在 .NET 侧 `Test-Path` 为 **False** ⇒ 不可用作输入；
#     · 「造一个会挂住的假 `ffprobe.exe`」需要自备 PE 二进制，本门禁不引入构建依赖。
#   ⇒ 本门禁对「超时可达 + Kill 生效」只给**源码形态自检**（同 `§6` 第 90 条对 JXL 默认值的既有做法），
#     **不**声称「已实测超时被触发」。要行为实测需另找素材（另立任务）。
#
# 退出码：0 = 全绿，1 = 有失败。⚠ 结尾必须是**完整 if/else**（§6 第 67 条：清理步会污染
#   `$LASTEXITCODE`，只写 `if ($fail -gt 0) { exit 1 }` 会让全绿路径以被污染的 2 退出 ⇒ 假红）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（用 「」）；
#   取子进程输出一律 `Start-Process` + 重定向。
# ⚠ **禁止 `Start-Process pwsh`**（宿主安全策略会拦）⇒ 限时 + 杀进程树一律在**本进程内**用
#   `Start-Process -PassThru` + `WaitForExit(ms)` / `KillTree` 实现。
# ⚠⚠ **杀树禁用 `$p.Kill($true)`**：该重载在 **Windows PowerShell 5.1（.NET Framework）上不存在**
#   （只有无参 `Kill()`）⇒ 异常被 `try/catch` 吞掉、应用没被杀、宿主因重定向流收尾而**永久挂住**。
#   用下面的 `KillTree`（先子后父）。⚠ 形参名**不能**叫 `$pid`（只读自动变量）。
# ⚠⚠ **退出码禁用轮询式 `.ExitCode`**：PS5.1 下 `Start-Process -PassThru`（不带 `-Wait`）的 `.ExitCode`
#   **恒为 `$null`**，而 `[int]$null == 0` ⇒ 强转会把「读不到」伪装成「exit 0」⇒ **假绿**。
#   ⇒ 本门禁的两个用例都会**自行结束**（实测 2~5 s）⇒ 一律用 `-Wait -PassThru` 取码，并保留 `$null` 判据。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"

# ⚠ 运行级隔离（GUID）：固定 `$work` 会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$val  = "$root/tests/output/validate"
$gid  = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = "$val/avifprobe_${gid}"
New-Item -ItemType Directory -Force -Path $work | Out-Null

$appLimitSec = 45        # 两个用例实测都在 5 s 内自行结束；45 s 是给足余量的上限（超了 = 真信号）
$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Host "PASS $msg" -ForegroundColor Green }
  else     { $script:fail++; Write-Host "FAIL $msg" -ForegroundColor Red }
}

# 取子进程输出一律走 Start-Process + 重定向（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）。
function Exec([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  $t = ""
  if (Test-Path $o) { $t = [System.IO.File]::ReadAllText($o) }
  $t2 = ""
  if (Test-Path $e) { $t2 = [System.IO.File]::ReadAllText($e) }
  return @{ code = $p.ExitCode; text = ($t + "`n" + $t2) }
}

# ⚠ 共享读：`RedirectStandardOutput` 的目标文件在子进程存活期间被独占 ⇒ 直接 `ReadAllText` 会抛
#   「being used by another process」。必须显式 FileShare.ReadWrite 打开。
function ReadShared([string]$path) {
  if (-not (Test-Path $path)) { return "" }
  try {
    $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Open, `
          [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
    $t = $sr.ReadToEnd()
    $sr.Close(); $fs.Close()
    return $t
  } catch { return "" }
}

# ⚠⚠ 杀**进程树**：**不要**用 `$p.Kill($true)` —— 那个重载只在 .NET Core / .NET 5+ 存在，
#   Windows PowerShell 5.1（.NET Framework）里 `[System.Diagnostics.Process]` **只有无参 `Kill()`**。
#   ⇒ 自己按 `ParentProcessId` 递归杀（先子后父），只用两个宿主都有的无参 `Kill()`。
#   ⚠ 形参名**不能**叫 `$pid`：那是 PowerShell 的只读自动变量。
function KillTree([int]$rootPid) {
  $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
  foreach ($q in $all) {
    if ($q.ParentProcessId -eq $rootPid) { KillTree ([int]$q.ProcessId) }
  }
  $pr = $null
  try { $pr = Get-Process -Id $rootPid -ErrorAction SilentlyContinue } catch { $pr = $null }
  if ($pr) { try { $pr.Kill() } catch { } }
}

$script:spawned = @()

# ── 单条用例：起应用（AVIF → webp，必然走 `ProcessAvifToGifWebpAsync` 的 Step 0 探测）──
#   ⚠ 用 `-Wait -PassThru`：两个用例都会自行结束 ⇒ 退出码可靠（避开 PS5.1 的 `$null` 坑）。
#     为防「将来它开始挂住」把宿主拖死，外面套一个 Job 式的限时？—— 不用：`-Wait` 无超时，
#     所以这里改用「轮询 + 到点杀树」，退出码则**只在 `HasExited` 为真时**读（仍保留 `$null`）。
function RunApp([string]$inPath, [string]$tag) {
  $outdir = "$work/out_$tag"
  $so = "$work/$tag.app.out"; $se = "$work/$tag.app.err"
  $p = Start-Process -FilePath $exe `
       -ArgumentList @("--headless", "--log-level", "debug", "-i", $inPath, "-o", $outdir, "--format", "webp") `
       -PassThru -NoNewWindow -RedirectStandardOutput $so -RedirectStandardError $se
  $script:spawned += [int]$p.Id
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $appLimitSec) { Start-Sleep -Milliseconds 300 }
  $sw.Stop()
  $terminated = $p.HasExited
  # ⚠⚠ **退出码不得进判据**：Windows PowerShell 5.1 下 `Start-Process -PassThru`（**不带 `-Wait`**）的
  #   `.ExitCode` **恒为 `$null`**，而 `[int]$null == 0` ⇒ 强转会把「读不到」伪装成「exit 0」⇒ **假绿**。
  #   本轮实测（PS5.1）：轮询到 `HasExited` 后读 ⇒ `<null>`；`WaitForExit()` 与 `Refresh()` **都救不回来**。
  #   ⇒ 这里只保留原值供**人眼看**；判据一律用宿主无关的「产物数 + 日志形状」。
  #   需要真退出码的那一条（still.avif 的回归）**单独**用 `-Wait -PassThru` 再跑一次（见 ⑦）。
  $code = $null
  if ($terminated) {
    try { if ($null -ne $p.ExitCode) { $code = $p.ExitCode } } catch { $code = $null }
  } else {
    KillTree ([int]$p.Id)
    Start-Sleep -Milliseconds 600
  }
  $text = ReadShared $so
  if ($text.Length -eq 0) { $text = ReadShared $se }
  $prods = 0
  if (Test-Path $outdir) { $prods = @(Get-ChildItem -LiteralPath $outdir -File -Recurse -ErrorAction SilentlyContinue).Count }
  return @{ terminated = $terminated; code = $code; elapsed = $sw.Elapsed.TotalSeconds; text = $text; prods = $prods }
}

foreach ($need in @($exe, $ff, $fp)) {
  if (-not (Test-Path $need)) {
    Write-Host "缺工具：$need" -ForegroundColor Red
    exit 1
  }
}
$exeMtimeBefore = (Get-Item $exe).LastWriteTimeUtc
Write-Host "被测 exe: $exe"
Write-Host "exe mtime(UTC) = $exeMtimeBefore"
Write-Host "运行级目录: $work"

# ── ① 素材自备 + 形态自检 ────────────────────────────────────────────────────────
Write-Host "`n### ① 自备 AVIF 素材 + 源码形态自检 ###" -ForegroundColor Cyan
$still = "$work/still.avif"     # 合法静态 AVIF（1 帧 / 1 条视频流）—— 负控方向（确定 ⇒ 按静态）
$trunc = "$work/trunc.avif"     # 截断到 32 B —— 正例方向（不确定 ⇒ 按多帧）

$g1 = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=64x48`" -frames:v 1 -c:v libaom-av1 -crf 30 -strict experimental -pix_fmt yuv420p `"$still`"" "gen_still"
$stillOk = Test-Path $still
$stillBytes = 0
if ($stillOk) { $stillBytes = (Get-Item $still).Length }
if ($stillOk -and $stillBytes -gt 64) {
  $b = [System.IO.File]::ReadAllBytes($still)
  $t = New-Object byte[] 32
  [Array]::Copy($b, $t, 32)
  [System.IO.File]::WriteAllBytes($trunc, $t)
}
$truncBytes = 0
if (Test-Path $trunc) { $truncBytes = (Get-Item $trunc).Length }

# ffprobe 对两条素材的真实反应（**这是「不确定触发器」的判据载体**，必须先钉住）
$ps1 = Exec $fp "-v error -show_entries stream=index,codec_type -of csv=p=0 `"$still`"" "probe_still"
$pt1 = Exec $fp "-v error -show_entries stream=index,codec_type -of csv=p=0 `"$trunc`"" "probe_trunc"
$stillStreams = @(($ps1.text -split "`r?`n") | Where-Object { $_ -match '^\s*\d+\s*,\s*\w+\s*$' })
Write-Host "  [素材] still.avif = $stillBytes B（ffprobe exit=$($ps1.code)，流行数=$($stillStreams.Count)）"
Write-Host "         trunc.avif = $truncBytes B（ffprobe exit=$($pt1.code)）"

CK (($stillBytes -gt 64) -and ($ps1.code -eq 0) -and ($stillStreams.Count -eq 1) -and ($stillStreams[0] -match ',\s*video\s*$')) `
   "① still.avif 是**合法静态 AVIF**（$stillBytes B；ffprobe exit=$($ps1.code)，恰好 1 条流且是 video）—— 它必须被判「确定」"
CK (($truncBytes -eq 32) -and ($pt1.code -ne 0)) `
   "① trunc.avif 截到 32 B 且 ffprobe **非 0 退出**（实 exit=$($pt1.code)）—— 证明下面的「不确定」触发器真是 ffprobe 失败，而不是别的原因（否则断言真空绿）"

# ① 源码形态自检（5 条）：本门禁对「超时可达 + Kill 生效」只给形态判据（见文件头的覆盖缺口登记）。
$qpSrc = "$root/src/FfmpegGui/Services/QueueProcessor.cs"
$srcTxt = ""
if (Test-Path $qpSrc) { $srcTxt = [System.IO.File]::ReadAllText($qpSrc) }
# 去掉**整行**注释后再断言（该方法的注释里正逐字引用旧写法与旧承诺）。
$srcCodeLines = @()
foreach ($ln in ($srcTxt -split "`r?`n")) { if ($ln -notmatch '^\s*//') { $srcCodeLines += $ln } }
$srcCode = $srcCodeLines -join "`n"

$tmoOk = ($srcCode -match 'DefaultAvifProbeTimeoutSec\s*=\s*20\b') -and
         ($srcCode -match 'FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC') -and
         ($srcCode -match 'Math\.Clamp\(sec,\s*1,\s*600\)')
CK ($tmoOk) `
   "① 源码形态（超时预算）：`DefaultAvifProbeTimeoutSec = 20`（与 `ProbeAvifStreamRolesAsync` 同口径）、注入通道读 `FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC`、且夹紧到 [1, 600]"

$avifBody = ""
# ⚠⚠ **载体变更须知（给 `#136` 切片 B）**：下面是**逐字签名锚点**。切片 B 若把三态换成
#   枚举 / 结构体（例如 `InputFrames` 三态）或改方法名，`$ai` 会变 `-1` ⇒ `$avifBody = ""` ⇒
#   下面那条 `$mechOk` 会**转红**。⚠ 那是**真信号**（判据载体失效），**不是**假红：
#   切片 B **必须**同步改这条锚点（并重取判据），**不得**把它放宽成「只要方法里出现 `Kill` 就算过」。
#   好消息：它红得响亮（`$avifBody.Length -gt 0` 直接判否），不会静默降级成真空绿。
$ai = $srcCode.IndexOf('private static async Task<(bool animated, bool determinate)> IsAnimatedAvifAsync(')
if ($ai -ge 0) {
  # ⚠ 必须**只取本方法体**：若用 `Substring($ai)` 一直取到文件尾，会把后面 `ProbeAnimatedJxl` 里的
  #   同步 `proc.WaitForExit(timeoutSec * 1000)` 也算进来 ⇒ `-notmatch '\.WaitForExit\('` 恒为假 ⇒ **假红**（实测踩到）。
  #   用**大括号配对**截取（本方法内没有未配对的 `{}`：插值串里的花括号、`new[] { '\n', '\r' }` 都是成对的）。
  $ob = $srcCode.IndexOf('{', $ai)
  if ($ob -ge 0) {
    $depth = 0; $end = -1
    for ($i = $ob; $i -lt $srcCode.Length; $i++) {
      $ch = $srcCode[$i]
      if ($ch -eq '{') { $depth++ }
      elseif ($ch -eq '}') { $depth--; if ($depth -eq 0) { $end = $i; break } }
    }
    if ($end -gt $ob) { $avifBody = $srcCode.Substring($ai, $end - $ai + 1) }
  }
}
$mechOk = ($avifBody.Length -gt 0) -and
          ($avifBody -match 'ResolveAvifProbeTimeoutSec\(\)') -and
          ($avifBody -match 'CancelAfter\(TimeSpan\.FromSeconds\(timeoutSec\)\)') -and
          ($avifBody -match '\.Token\.Register\(') -and
          ($avifBody -match 'Kill\(entireProcessTree: true\)') -and
          ($avifBody -match 'WaitForExitAsync\(\)') -and
          ($avifBody -notmatch '\.WaitForExit\(')
CK ($mechOk) `
   "① 源码形态（超时机制）：返回类型是 `Task<(bool animated, bool determinate)>`；体内用了 `ResolveAvifProbeTimeoutSec()` + `CancelAfter(TimeSpan.FromSeconds(timeoutSec))` + `Token.Register(` + `Kill(entireProcessTree: true)`；且**保持异步**（有 `WaitForExitAsync()`、**无**同步 `.WaitForExit(`）"

# ⚠⚠ **载体变更须知 —— 已按本纪律重取（`#136` 切片 B，2026-09-18）**：
#   旧形态是**精确计数** `-eq 2`（钉「两个调用点都消费了 `determinate`」）。切片 B 把
#   `InputMayHaveMultipleFrames` 拆出**输入侧子句** `InputHasMultipleFramesAsync`，其 avif 分支
#   **新增了第三个**消费点（`QueueProcessor.cs` 的 `return avifProbeAnimated || !avifProbeDeterminate;`）
#   ⇒ 计数变 3 ⇒ 旧断言**转红**。这正是本注释**预告过**的「代码可能是变好的」那一格。
#   ⇒ 按预告的纪律**重取载体**：**不**钉计数，改成**点名三个消费点**的锚点式断言（逐点逐字）。
#   ⚠ **仍然不得放宽成 `-ge`**：下面逐点断言，缺任何一处即红；并由**反控**证明这条点名式载体**有牙**。
$consumeSites = @(
  @{ Name = '预缩放跳过（maxDimSkipAnimated）'; Re = 'maxDimSkipAnimated\s*=\s*avifProbeAnimated\s*\|\|\s*!avifProbeDeterminate;' },
  @{ Name = '两步法入口（isAnimatedAvif）';     Re = 'bool\s+isAnimatedAvif\s*=\s*avifProbeAnimated\s*\|\|\s*!avifProbeDeterminate;' },
  @{ Name = '引擎路由输入侧子句（return）';     Re = 'return\s+avifProbeAnimated\s*\|\|\s*!avifProbeDeterminate;' }
)
$consumeMissing = @()
foreach ($cs in $consumeSites) { if (-not ($srcCode -match $cs.Re)) { $consumeMissing += $cs.Name } }
CK ($consumeMissing.Count -eq 0) `
   "① 源码形态（三态消费，**点名三处**）：预缩放跳过 / 两步法入口 / 引擎路由输入侧子句 **都**写了「|| !avifProbeDeterminate」（缺：$(if ($consumeMissing.Count -eq 0) { '无' } else { $consumeMissing -join '、' })）—— 不确定 ⇒ 保守按多帧，绝不静默当静态"
# 反控（证明点名式载体**仍有牙**）：把「预缩放跳过」那一处的第三态删掉 ⇒ 必须至少缺一个点名点。
$consumeMutant = $srcCode -replace 'maxDimSkipAnimated\s*=\s*avifProbeAnimated\s*\|\|\s*!avifProbeDeterminate;', 'maxDimSkipAnimated = avifProbeAnimated;'
$consumeMutantMissing = 0
foreach ($cs in $consumeSites) { if (-not ($consumeMutant -match $cs.Re)) { $consumeMutantMissing++ } }
CK ($consumeMutantMissing -ge 1) `
   "① 反控（点名式载体**有牙**）：把「预缩放跳过」那处的第三态删掉 ⇒ 点名式断言会判红（实缺 $consumeMutantMissing 处）—— 证明重取后**没有**被放空"

$indetCount = ([regex]::Matches($avifBody, [regex]::Escape('Indeterminate('))).Count
CK ($indetCount -ge 6) `
   "① 源码形态（三态出口）：四条「不确定」来源全部收敛到同一个 `Indeterminate(...)` 出口（实 $indetCount 处调用，含定义处 1 处，应为 ≥6）—— 第三态**不得**静默吞掉"

# ── ② 行为（双向）：确定 ⇒ 按静态（不误伤）；不确定 ⇒ 按多帧（有牙）────────────────
Write-Host "`n### ② 行为：静态 AVIF（确定⇒单命令）vs 截断 AVIF（不确定⇒两步法）###" -ForegroundColor Cyan
$rStill = RunApp $still "still"
$rTrunc = RunApp $trunc "trunc"
Write-Host ("  [still.avif] 结束={0} exit={1} 耗时={2:N1}s 产物={3}" -f $rStill.terminated, $rStill.code, $rStill.elapsed, $rStill.prods)
Write-Host ("  [trunc.avif] 结束={0} exit={1} 耗时={2:N1}s 产物={3}" -f $rTrunc.terminated, $rTrunc.code, $rTrunc.elapsed, $rTrunc.prods)
$stillAvifLines = @($rStill.text -split "`r?`n" | Where-Object { $_ -match '\[avif' })
$truncAvifLines = @($rTrunc.text -split "`r?`n" | Where-Object { $_ -match '\[avif' })
foreach ($l in $stillAvifLines) { Write-Host "     still: $l" -ForegroundColor DarkGray }
foreach ($l in $truncAvifLines) { Write-Host "     trunc: $l" -ForegroundColor DarkGray }

CK ($rStill.terminated -and ($rStill.prods -ge 1)) `
   "② still.avif：自行结束且产物 ≥1（实 结束=$($rStill.terminated) 产物=$($rStill.prods)）—— 本次改动不得让正常静态路径回归（退出码见 ⑦）"
CK ($rStill.text -match '\[avif→webp\] 输入为静态 AVIF') `
   "② still.avif：**确定** ⇒ 按静态 ⇒ 出现逐字「[avif→webp] 输入为静态 AVIF（单视频流），走普通 ffmpeg 单命令路径」"
$stillBad = @($stillAvifLines | Where-Object { $_ -match '动画探测.*⇒ 不确定' })
CK ($stillBad.Count -eq 0) `
   "② still.avif：**不得**出现任何「动画探测…⇒ 不确定」行（实 $($stillBad.Count) 条）—— 确定的结果不得被误报成不确定（**不误伤**方向）"

CK ($truncAvifLines.Count -ge 1) `
   "② trunc.avif：出现 `[avif]` 前缀的日志（实 $($truncAvifLines.Count) 条）—— 否则下面的逐字断言真空绿"
CK ($rTrunc.text -match '动画探测异常退出（exit=\d+）\s*⇒\s*不确定：trunc\.avif（按多帧处理）') `
   "② trunc.avif：**不确定** ⇒ 有一条**点名**日志（文件名 + 原因 + 「按多帧处理」）—— 第三态不得静默吞掉"
CK ($rTrunc.text -match '\[avif→webp\] Step1:') `
   "② trunc.avif：真的按**多帧**走了两步法（出现「[avif→webp] Step1:」）"
$truncBad = @($rTrunc.text -split "`r?`n" | Where-Object { $_ -match '输入为静态 AVIF' })
CK ($truncBad.Count -eq 0) `
   "② trunc.avif：**不得**出现「输入为静态 AVIF」（实 $($truncBad.Count) 条）—— 这正是修复前的缺陷形态（把「不知道」当「不是」⇒ 静默丢帧）"
CK ($rTrunc.terminated -and ($rTrunc.prods -eq 0)) `
   "② trunc.avif：自行结束且**零产物**（实 结束=$($rTrunc.terminated) 产物=$($rTrunc.prods)）—— 畸形输入**绝不静默成功**（有产物 = 用户以为成功）。⚠ 判据刻意**不**用退出码：PS5.1 下轮询路径读不到（见 `RunApp` 注释），写成 `code -ne 0` 会在回退宿主上**真空绿**"

# ── ③ 注入通道不误伤：把超时压到 1 s，静态 AVIF 仍必须走单命令 ─────────────────────
Write-Host "`n### ③ 注入 FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC=1：静态 AVIF 仍走单命令（注入通道不得误伤正常路径）###" -ForegroundColor Cyan
$env:FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC = "1"
$rInj = RunApp $still "inj"
Remove-Item Env:FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC -ErrorAction SilentlyContinue
Write-Host ("  [still.avif @1s] 结束={0} exit={1} 耗时={2:N1}s 产物={3}" -f $rInj.terminated, $rInj.code, $rInj.elapsed, $rInj.prods)
CK (($rInj.prods -ge 1) -and ($rInj.text -match '\[avif→webp\] 输入为静态 AVIF')) `
   "③ 注入 1 s 后静态 AVIF 仍产出 ≥1 且仍走单命令（实 产物=$($rInj.prods)）—— 注入通道不误伤正常路径"
# ⚠ 措辞不得大于扫描面：本条**不**证明「超时被真的触发过」（见文件头的覆盖缺口登记）。

# ── ⑦ 单独取一次真退出码（回归钉）：PS5.1 下轮询路径的 `.ExitCode` 恒为 `$null`（见 `RunApp` 注释），
#   所以需要退出码的那**一条**必须用 `-Wait -PassThru` **另跑一次**（照 `verify-jxl-probe-timeout.ps1`
#   对 `real.jxl` 的既有做法）。⚠ 敢用**无超时**的 `-Wait`：still.avif 是唯一会在限时内**自行结束**的用例
#   （上面刚实测 ~1.6 s）。若将来它开始挂住，本门禁会挂在这里 —— 那是**真信号**，不是假红。
Write-Host "`n### ⑦ 单独一次 `-Wait -PassThru` 取 still.avif 的真退出码（回归钉）###" -ForegroundColor Cyan
$rexOut = "$work/still_exit.out"
$rexDir = "$work/out_stillexit"
$rex = Start-Process -FilePath $exe `
       -ArgumentList @("--headless", "--log-level", "error", "-i", $still, "-o", $rexDir, "--format", "webp") `
       -NoNewWindow -Wait -PassThru -RedirectStandardOutput $rexOut -RedirectStandardError "$rexOut.err"
CK ($rex.ExitCode -eq 0) "⑦ still.avif：exit == 0（**单独** `-Wait -PassThru` 运行取码；实 $($rex.ExitCode)）—— 正常静态路径不得因本次改动回归"

# ── ④ 被测 exe mtime 一致（§6 第 84 条：跑中途被重建则本轮读数作废）──────────────
Write-Host "`n### ④ 被测 exe mtime 跑前跑后一致 ###" -ForegroundColor Cyan
$exeMtimeAfter = (Get-Item $exe).LastWriteTimeUtc
CK ($exeMtimeBefore -eq $exeMtimeAfter) "④ 被测 exe 未被中途重建（$exe）"

# ── ⑤ 兜底清扫（**不是断言**）：把本门禁起过的应用全部杀掉并确认 ──────────────────
Write-Host "`n### ⑤ 兜底清扫：本门禁起过的应用进程全部终止（不构成断言）###" -ForegroundColor Cyan
foreach ($spid in $script:spawned) { KillTree ([int]$spid) }
Start-Sleep -Milliseconds 500
$stillAlive = @()
foreach ($spid in $script:spawned) {
  $pr = $null
  try { $pr = Get-Process -Id $spid -ErrorAction SilentlyContinue } catch { $pr = $null }
  if ($pr) { $stillAlive += $spid }
}
Write-Host "  本门禁共起过 $($script:spawned.Count) 个应用进程；清扫后仍存活：$(if ($stillAlive.Count -eq 0) { '0 个' } else { ($stillAlive -join ', ') })" -ForegroundColor $(if ($stillAlive.Count -eq 0) { 'DarkGray' } else { 'Red' })

# ── ⑥ 残留自证（仅在绿时判定；红时保留目录供排查）────────────────────────────────
Write-Host "`n### ⑥ 残留自证 ###" -ForegroundColor Cyan
if ($fail -eq 0) {
  # 清理一律用 **.NET API** 绕过宿主批量删除守卫（§6 第 57/67 条）。
  try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
  CK (-not (Test-Path $work)) "⑥ 运行级目录已清零：$work"
} else {
  Write-Host "  （有失败 ⇒ 保留目录供排查：$work）" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "===== avif-anim-probe: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 退出码必须在清理之后，且必须写完整 if/else（§6 第 67 条）。
if ($fail -gt 0) { exit 1 } else { exit 0 }
