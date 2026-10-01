# verify-ffmpeg-heartbeat.ps1 —— 「ffmpeg 忙等型挂死」心跳守卫专项门禁（2026-09-18 新增，任务 #15）
#
# ── 为什么需要它 ────────────────────────────────────────────────────────────────────
#   `FfmpegRunner` 的旧守卫是**双信号**：`lastActivityTicks`（有输出）+ `cpuAdvanced`
#   （累计 CPU 增长 ≥1 s/30 s）。但**忙等型**挂死（畸形 `.jxl` 让 ffmpeg 的 JXL 解复用器
#   空转）的特征恰恰是「**两个流都 0 字节** + **持续烧 CPU**」⇒ `cpuAdvanced` 每次都把
#   `lastActivityTicks` 重置 ⇒ `InactivityTimeoutMinutes = 30` 对这类挂死**永不可达**。
#
#   修法（已实现）：给 ffmpeg 追加 `-progress pipe:1`，由 `FfmpegRunner` **自己解析** stdout
#   的 `key=value` 块（**不上抛**给 logCallback，否则每 ~0.5 s 一块 × 11 行会淹没队列日志），
#   把「**进度块到达**」当活性信号；心跳模式下**故意忽略** `cpuAdvanced`。
#
# ── 判据为什么是「块到达」而不是「out_time 推进」──────────────────────────────────────
#   2026-09-18 实测（4K 单帧 libjxl `-effort 9`，本仓自带 ffmpeg，10.3 s）：
#     · **首个** progress 块在 t≈0.018 s 到达 —— 该块由 ffmpeg 的进度线程**周期性**发出；
#     · 整个编码里 `frame=` 恒 0、`out_time_us=` 恒 40000（**不推进**）；
#     · 块间隔 min 0.437 s / p50 0.544 s / max 0.574 s。
#   ⇒ 若把判据写成「`out_time` 停走 = 死」，**合法的单帧长编码会被误杀**。本门禁的 ③
#     就是专门钉这一条的（负控）。
#
# ── 本门禁覆盖的方向（② 正向 + ③/③-d/③-e 三条负控 + ⑤ 二分性，缺一不可）──────────────
#   ② **正向**：畸形 `.jxl`（1 B `00`）⇒ 忙等、0 块 ⇒ 必须在**限时内自行判死**
#      （不是被本门禁 Kill），逐字超时行 + `失败 (退出码 -124)` + 0 产物。
#      命中站点 D（`:3047` jxl-unknown）。
#   ③ **反向（决定性）**：合法 4K 单帧 libjxl `-effort 9`（djxl/cjxl 不可用 ⇒ 走 JXL 链
#      兜底站点 C，`:3036`）⇒ 注入的心跳阈值**远小于**它的总耗时，它**仍然不得被误杀**。
#      ⚠ 这一条是「负控必须有牙」的关键：注入 2 s、实测总耗时 ~15 s ⇒ 任何「按总墙钟
#        判死」的错误实现都会在这一条上红。同时断言日志里**一个 progress 块行都没有**
#        （证明心跳真的被内部消化、没有污染既有日志 —— 这是 team-lead 要求实测的那一条）。
#   ③-d **反向**：动图 JXL（4K × 48 帧）`--format apng` ⇒ 站点 B（`:2951` jxl-anim）。
#      实测总耗时 ~10 s（≈ 注入阈值的 5×）⇒ 牙口比 ③ 还大。
#      ⚠ 站点可达性**由日志行证明**（`[jxl] 动图 JXL 不进行 djxl→PNG 中间解码`），不靠耗时推断。
#   ③-e **反向**：JPEG 重构型 JXL（jbrd 容器）+ djxl 不可用 ⇒ 站点 A（`:2934` jxl-recon-no-djxl）。
#      实测总耗时 4.4–5.2 s，其中约 3–4 s 是**应用启动/收尾的固定开销**（4K jbrd 只比 512×384
#      jbrd 多 0.7 s）⇒ 牙口 ~2×，是本门禁里最薄的一条，故下界只钉 3 s（仍严格大于注入的 2 s）。
#      ⚠ 站点可达性同样**由日志行证明**（`[jxl] 输入类型: JPEG 重构` + `[djxl] 未检测到 djxl，回退到 ffmpeg`）。
#
# ── 已知残余（本门禁不覆盖）──────────────────────────────────────────────────────────
#   · ⚠ 2026-09-19（#15-b）**类级根因已修**：心跳改为 `RunAsync` 的**默认行为**（`bool heartbeat = true`，
#     opt-out）⇒ 全部 ffmpeg 调用点自动获得「进度块到达」活性信号，不再需要逐站点 opt-in。
#     JXL 链的 **4 处** `heartbeat: true` 字面量**保留**（它们现在是「文档锚点」：本门禁 ① 的站点集合
#     断言仍然钉住这 4 个点，防误删注释/误改实参）。⇒ ① 的「站点集合 == {A,B,C,D}」语义从
#     「**全部**开启心跳的站点」变为「**显式标注**的站点」；默认开启由 ① 的两条 #15-b 形态断言承担。
#   · ⚠ 2026-09-19（#15-b 收尾）**残余风险已钉成断言**：心跳默认开启后，生产阈值 120 s 由**全部**
#     调用点承担 ⇒ 若某个**合法**任务存在「> 120 s 无进度块」的窗口，它会被**误杀**（此前只 4 个
#     站点承担该阈值）。⑤ 用两条腿钉住它：
#       (i) N1 直接量**合法任务**的进度块节奏（平均块间隔 ≤ 1 s ⇒ 对 120 s 有 ≥120× 余量）；
#       (ii) N2 在**同一注入阈值**下断言二分（无块臂判死 ∧ 有块臂存活）。
#     ⚠ 仍未覆盖：**输入打开/探测阶段**的合法零进度窗口（本地文件为毫秒级，但本门禁无法证明其
#       上界）；以及「合法任务出现 >120 s 无块窗口」的**反例**无法构造（构造出来就是缺陷，不是测试）。
#     ⚠ 默认阈值本身由 ① 的**逐字防调参锁**（剥注释后的 `DefaultHeartbeatTimeoutSec = 120;`）钉住。
#   · stdout 承载数据的调用（`-f image2pipe … -`）由 `CanInjectProgressPipe` 自检**拒绝**
#     注入并响亮登记；本仓这类调用全部是手搓 Process.Start、不经 `FfmpegRunner`。
#
# ⚠ 双宿主：本脚本必须**真在 Windows PowerShell 5.1 与 pwsh 7 各跑一遍**
#   （`verify-ps-compat.ps1` 只是静态语法扫描，抓不到宿主运行时差异 —— 见 TESTING §6 第 91 条）。
#   ⇒ 因此：不用 `$p.Kill($true)`（PS5.1 无此重载），不用 `[int]$null` 强转，退出码一律改判**日志**。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp  = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$cjxl = "$root/publish/PLAN/jxl/bin/cjxl.exe"
$srcFile = "$root/src/FfmpegGui/Services/FfmpegRunner.cs"
$qpFile  = "$root/src/FfmpegGui/Services/QueueProcessor.cs"

# ⚠ 运行级隔离（GUID）：固定目录会被并发的同名实例互删 ⇒ 假红（§6 第 66 条）。
#   素材按 team-lead 口径放 %TEMP% 私有 GUID 目录（不写 tests/output）。
$work = Join-Path $env:TEMP ("ffmpeghb_" + [guid]::NewGuid().ToString('N'))
$emptyPlan = Join-Path $work 'emptyplan'; New-Item -ItemType Directory -Force -Path $emptyPlan | Out-Null
$emptyJxl  = Join-Path $work 'emptyjxl';  New-Item -ItemType Directory -Force -Path $emptyJxl  | Out-Null

# ⚠ 注入值必须**在启动被测应用之前**设好（应用按进程环境读）。
$probeSec     = 3        # 探测站点超时（把应用尽快推到编码阶段；本门禁不验它，见 #14 门禁）
$hbSec        = 2        # 心跳阈值：② 与 ③ 共用 —— ② 用它判死、③ 用它证「不被误杀」
$env:FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC = "$probeSec"
$env:FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC = "$hbSec"
# 让 djxl / cjxl 都不可用：① 手动目录置空 + ② 打掉 PLAN 便携包自动检测 + ④ 打掉系统扩展搜索根
#   ⚠⚠ 2026-09-22 实测教训：**只置空前两级是不够的，而且原注释低估了 ④ 的杀伤面** ——
#     `GetExtendedSearchPaths` 会把 `%LOCALAPPDATA%\Programs`、`C:\Program Files` 与**整个 PATH**
#     递归扫一遍，本机因此命中 `C:\Program Files\WindowsApps\LinYuSen.HDRImageViewer_1.0.31.0_x64__
#     phzzaxm6z2j1m\encoders\x64\djxl.exe`（别的包自带的私有二进制）⇒ `djxl: 可用` ⇒
#     ③/③-e 的「模拟真的生效」自检**按设计转红**（这条自检是有牙的，红得对）。
#     该文件其实**根本起不来**（实测 Access Denied ⇒ 产品侧已改为"起不来就不算可用"），
#     但**降级路径的门禁不能赌本机恰好没装过某个第三方应用** ⇒ 用 `FFMPEGGUI_EXT_SEARCH_DIRS`
#     显式把搜索根收口到空目录（该变量置空时=不覆盖，见 ExternalToolsDetector.GetExtendedSearchPaths）。
$emptyExt  = Join-Path $work 'emptyext';  New-Item -ItemType Directory -Force -Path $emptyExt  | Out-Null
$env:FFMPEGGUI_JXL_LIB_DIR = $emptyJxl
$env:FFMPEGGUI_PLAN_DIR    = $emptyPlan
$env:FFMPEGGUI_EXT_SEARCH_DIRS = $emptyExt

$negLimitSec = 90        # ③ / ③-e 合法慢任务上限（2026-09-24 单跑实测 ③ ≈16.0 s / ③-d ≈10.1 s / ③-e ≈10.3 s；整轮负载下会到 ~1.5–2×）
$posLimitSec = 40        # ② 忙等判死上限（实测 ~16 s：3 s 探测 ×2 + 2 s 心跳 + 启动/轮询余量）
$animLimitSec = 90       # ③-d 动图慢任务上限（实测 ~10 s）
# ③-d 的帧数探测超时：4K 素材的 `ffprobe -count_frames` 实测 30 帧 **2.84 s**（现 48 帧按线性 ~4.5 s），
#   基线 3 s 只剩 0.16 s 余量（慢机上会翻成「超时 ⇒ 按静图处理」）⇒ 本条单独放宽到 20 s。
#   ⚠ 站点 B 的可达性其实**不依赖**这个探测（`--format apng` 让 `IsAnimated` 直接返回 true），
#     放宽只是为了别在日志里留下一条 `帧数探测超时` 的噪声（本条另有一条断言禁止它）。
$animProbeSec = 20

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
  return @{ code = $p.ExitCode; text = ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e)) }
}

# ⚠ 共享读：`RedirectStandardOutput` 的目标文件在子进程存活期间被独占 ⇒ 直接 `ReadAllText` 会抛
#   「being used by another process」（实测：正控那一轮就是这么把日志读空的 ⇒ 全 0 假红）。
function ReadShared([string]$path) {
  if (-not (Test-Path $path)) { return "" }
  try {
    $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Open, `
          [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
    $t = $sr.ReadToEnd(); $sr.Close(); $fs.Close(); return $t
  } catch { return "" }
}

# ⚠⚠ 杀**进程树**：**不要**用 `$p.Kill($true)` —— 该重载只在 .NET Core / .NET 5+ 存在，
#   Windows PowerShell 5.1（.NET Framework）的 `[System.Diagnostics.Process]` **只有无参 `Kill()`**；
#   `Kill($true)` 抛的 MethodException 会被 `try/catch` 吞掉 ⇒ 子进程根本没被杀，
#   且宿主因异步重定向读收尾而**永久挂住**（实测 10+ 分钟不返回）。见 TESTING §6 第 91 条。
#   ⇒ 自己按 `ParentProcessId` 递归杀（先子后父），只用两个宿主都有的无参 `Kill()`。
#   ⚠ 形参名**不能**叫 `$pid`（PowerShell 只读自动变量）。
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

# ── 单条用例：起应用 → 轮询到自行结束或超限 → 记录「是否自行结束」（= 有没有被误杀/判死）──
#   `$envOver`：本用例专属的环境变量覆盖（**在起进程之前**设好；应用按进程环境读），跑完**原值还原**
#     —— 不还原的话上一条用例的覆盖会漏给下一条（如 ③-d 的 20 s 探测超时）。
#   `$fmt`：输出格式。默认 png；③ 用 jxl、③-d 用 apng（`IsAnimated` 对 gif/apng 直接判动图 ⇒ 站点 B）。
function RunApp([string]$inPath, [string]$outdir, [string]$tag, [int]$limitSec, [string[]]$extra, [hashtable]$envOver, [string]$fmt = "png") {
  if (Test-Path $outdir) { try { [System.IO.Directory]::Delete($outdir, $true) } catch { } }
  New-Item -ItemType Directory -Force -Path $outdir | Out-Null
  $so = "$work/$tag.app.out"; $se = "$work/$tag.app.err"
  try { [System.IO.File]::Delete($so) } catch { }
  try { [System.IO.File]::Delete($se) } catch { }
  $savedEnv = @{}
  if ($envOver) {
    foreach ($k in $envOver.Keys) {
      $savedEnv[$k] = [System.Environment]::GetEnvironmentVariable($k)
      [System.Environment]::SetEnvironmentVariable($k, [string]$envOver[$k])
    }
  }

  $argl = @("--headless", "--log-level", "debug", "-i", $inPath, "-o", $outdir, "--format", $fmt) + $extra
  $p = Start-Process -FilePath $exe -ArgumentList $argl -PassThru -NoNewWindow `
       -RedirectStandardOutput $so -RedirectStandardError $se
  $script:spawned += [int]$p.Id

  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $limitSec) {
    Start-Sleep -Milliseconds 300
    if ($p.HasExited) { break }
  }
  $sw.Stop()
  $selfEnded = $p.HasExited
  $killed = $false
  if (-not $selfEnded) {
    KillTree ([int]$p.Id)
    $killed = $true
    Start-Sleep -Milliseconds 600
  }
  foreach ($k in $savedEnv.Keys) { [System.Environment]::SetEnvironmentVariable($k, $savedEnv[$k]) }
  $text = ReadShared $so
  if ($text.Length -eq 0) { $text = ReadShared $se }
  $prods = @(Get-ChildItem -LiteralPath $outdir -File -Recurse -ErrorAction SilentlyContinue)
  return @{ selfEnded = $selfEnded; killed = $killed; elapsed = $sw.Elapsed.TotalSeconds
            prods = $prods.Count; text = $text }
}

foreach ($need in @($exe, $ff, $fp, $cjxl, $srcFile, $qpFile)) {
  if (-not (Test-Path $need)) { Write-Host "缺文件：$need" -ForegroundColor Red; exit 1 }
}
$exeMtimeBefore = (Get-Item $exe).LastWriteTimeUtc
Write-Host "被测 exe: $exe"
Write-Host "exe mtime(UTC) = $exeMtimeBefore"
Write-Host "运行级目录: $work"
Write-Host "注入: FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC=$probeSec  FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC=$hbSec"

# ══ ① 源码形态自检（钉住生产默认值 —— 见 TESTING §6 第 90 条）════════════════════════
Write-Host "`n### ① 源码形态自检 ###" -ForegroundColor Cyan
$src = [System.IO.File]::ReadAllText($srcFile)
$qp  = [System.IO.File]::ReadAllText($qpFile)
# ⚠ 结构自检前必须**剥掉整行注释**：心跳分支的说明文字里就写着「这里**故意**不看 cpuAdvanced」，
#   不剥的话那条断言会被自己的注释判红（实测踩到过）。
$srcCode = (($src -split "`r?`n") | Where-Object { $_.TrimStart() -notmatch '^//' }) -join "`n"

# ⚠ 必须断言在**剥注释后的代码**（`$srcCode`）上，且**逐字**钉住 `= 120;`：
#   ① 用 `$src` 的话，注释里写一句 `DefaultHeartbeatTimeoutSec = 120` 就能假绿；
#   ② 不钉 `;` 的话 `= 1200` / `= 120.5` 之类也会被 `\s*=\s*120` 前缀匹配 ⇒ 调参后仍绿。
#   ⇒ 这是「阈值可注入 ⇒ 必须钉住生产默认值」的**防调参锁**（TESTING §6 第 90 条）：
#     后人为了让新断言变绿而把 120 改小/改大，本条立刻转红。
CK ($srcCode -match 'DefaultHeartbeatTimeoutSec\s*=\s*120\s*;') `
   "① 生产默认心跳阈值被**逐字**钉为 `DefaultHeartbeatTimeoutSec = 120;`（剥注释后的代码）—— 阈值可注入 ⇒ 必须自检钉默认值，防后人调参"
CK ($src -match 'FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC') `
   "① 心跳阈值确实**可注入**（读 `FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC`）—— 本门禁的限时验证依赖它"
CK ($src -match '\-progress pipe:1') `
   "① `-progress pipe:1` 注入在位"
CK ($src -match '无进度心跳') `
   "① 判死文案是「**无进度心跳**」（不得沿用「既无输出也不消耗 CPU」—— 那句在忙等时是**假**的）"
# ⚠ `-progress` 的键里有 `stream_0_0_q` 这种**含数字**的 ⇒ 键形态过滤器必须允许数字，
#   否则那些行会漏进队列日志（实测：修前 21 行泄漏，由本门禁 ③ 抓到）。
CK ($src -match 'c < ''0'' \|\| c > ''9''') `
   "① 键形态过滤器允许**数字**（防 `stream_0_0_q=` 泄漏进日志）"

# 心跳分支必须是**独立**的一段，且**不得**被 cpuAdvanced 续命（否则忙等永不可达 —— 这正是 #15 的根因）
#   ⚠ 段尾**不能**用 `if (cpuAdvanced)` 定位 —— 那是**循环定义**：要断言的正是「这段里没有 cpuAdvanced」，
#     拿它当边界 ⇒ 只要把 `if (cpuAdvanced) continue;` 插在原来那句**之前**，段就被**截短**到突变行之前，
#     「不含 cpuAdvanced」反而**通过**（实测踩到：M2 变异下这条断言假绿）。
#   ⇒ 改用 `return HangExitCode`（心跳分支的**末句**）当段尾标记：它出现在分支内部，不参与被断言的属性。
#     并加两条**抽取器自检**（段内必须有「无进度心跳」、必须没有旧分支文案「既无输出也不消耗 CPU」），
#     防止段被截短或越界后「断言对象不是那段」而静默通过。
$hbAnchor = $srcCode.IndexOf('if (silent < heartbeatTimeoutTicks)')
$hbStart  = $srcCode.LastIndexOf('if (heartbeatActive)', $hbAnchor)
$hbEndMark = $srcCode.IndexOf('return HangExitCode', $hbAnchor)
$hbBlock  = ""
if (($hbStart -ge 0) -and ($hbEndMark -gt $hbAnchor) -and ($hbAnchor -gt $hbStart)) {
  $hbBlock = $srcCode.Substring($hbStart, ($hbEndMark + 'return HangExitCode'.Length) - $hbStart)
}

CK ($hbBlock.Length -gt 0) "① 能定位到心跳判死分支（结构自检的前提）"
CK (($hbBlock.Length -gt 0) -and ($hbBlock -match '无进度心跳')) `
   "① 抽取器自检：段内确实有**心跳专属**文案「无进度心跳」⇒ 抽取到的是心跳分支，不是别段"
CK (($hbBlock.Length -gt 0) -and ($hbBlock -notmatch '既无输出也不消耗 CPU')) `
   "① 抽取器自检：段**没有**越界到旧分支（不含旧文案「既无输出也不消耗 CPU」）⇒ 段尾标记有效"
CK (($hbBlock.Length -gt 0) -and ($hbBlock -notmatch 'cpuAdvanced')) `
   "① 心跳分支内**不出现** `cpuAdvanced` —— 忙等型挂死正是「0 块 + 烧 CPU」，让它续命就等于对它失明"
CK (($hbBlock.Length -gt 0) -and ($hbBlock -match 'process\.Kill\(true\)')) `
   "① 心跳判死走 `process.Kill(true)`（杀**进程树**，不留下 ffmpeg 孤儿）"
CK (($hbBlock.Length -gt 0) -and ($hbBlock -match 'return HangExitCode')) `
   "① 心跳判死返回 `HangExitCode`（显式失败，不静默）"

# ⭐ #15-b（2026-09-19，**类级根因**）：心跳必须是 **RunAsync 的默认行为**（opt-out），
#   而不是 4 个站点的 opt-in —— 否则**任何未显式开启的调用点**一旦忙等，旧双信号判据仍会挂死到
#   `InactivityTimeoutMinutes = 30` 之后（且该分支结构性不可达）。这正是 D⑨ 登记的「类级根因未修」。
#   ⚠ 反例（把签名改回 `bool heartbeat = false`）：本断言转红（实测见回报的变异验证）。
CK ($srcCode -match 'bool heartbeat\s*=\s*true') `
   "① 心跳是 **RunAsync 的默认行为**（「bool heartbeat = true」，opt-out）—— 类级堵住「未显式开启的调用点一旦忙等仍挂死」"
# ⭐ #15-b：心跳分支的活性判据必须**只**认「进度块到达」（`lastProgressTicks`），不得回退到「任意输出」
#   （`lastActivityTicks`）—— 否则任何一行 stdout/stderr 噪声都能给忙等续命（旧实现正是这条更弱的信号）。
#   ⚠ 反例（把 `lastProgressTicks` 换回 `lastActivityTicks`）：本断言转红（实测见回报的变异验证）。
CK (($hbBlock.Length -gt 0) -and ($hbBlock -match 'lastProgressTicks') -and ($hbBlock -notmatch 'lastActivityTicks')) `
   "① 心跳分支的活性**只**认进度块专属的 `lastProgressTicks`（且不碰 `lastActivityTicks`）—— 任意输出不得给忙等续命"

$runAsyncCount = ([regex]::Matches($qp, 'FfmpegRunner\.RunAsync')).Count
CK ($runAsyncCount -eq 24) "① QueueProcessor 里 `FfmpegRunner.RunAsync` 调用点仍为 24 处（实 $runAsyncCount）—— 未误伤"

# ⚠ 断言**站点集合**而不是计数（lead 要求）：计数只能说「有 N 个」，说不清「是不是这 N 个」——
#   把第 5 处误开、或把预期站点里的一个漏开，计数都可能凑巧还是 4。做法：
#     · 按 `FfmpegRunner.RunAsync` 把文件切段 ⇒ 第 k 个调用点的「前置窗口」= 第 k-1 段尾部，
#       「实参头部」= 第 k 段头部（只看头 300 字符，避免读到下一个调用点的实参）；
#     · 每个开启点必须**能归类**到 4 个预期站点之一（归类键 = 站点标签注释 `#15 心跳站点 <X>`）；
#     · 断言「开启点标签集合」== 预期 4 个 ⇒ 漏开（集合变小）与误开（多出未标签项）都能区分。
$segs = $qp -split 'FfmpegRunner\.RunAsync'
$expectSites = @('A', 'B', 'C', 'D')
$enabledTags = @()
$untaggedSeq = @()
for ($k = 1; $k -lt $segs.Count; $k++) {
  $after = $segs[$k]
  $head = $after.Substring(0, [Math]::Min(300, $after.Length))
  if ($head -notmatch 'heartbeat:\s*true') { continue }
  $before = $segs[$k - 1]
  $win = $before.Substring([Math]::Max(0, $before.Length - 500))
  $m = [regex]::Match($win, '#15 心跳站点 ([A-D])')
  if ($m.Success) { $enabledTags += $m.Groups[1].Value } else { $untaggedSeq += $k }
}
$dupTags = @($enabledTags | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
$enabledSet = @($enabledTags | Sort-Object)
$expectSet  = @($expectSites | Sort-Object)
CK ($untaggedSeq.Count -eq 0) `
   "① **每个**开启心跳的调用点都带站点标签（未标签的调用点序号：$(if ($untaggedSeq.Count -eq 0) { '无' } else { $untaggedSeq -join ',' })）—— 防「别处误开」"
CK ($dupTags.Count -eq 0) `
   "① 站点标签无重复（重复：$(if ($dupTags.Count -eq 0) { '无' } else { $dupTags -join ',' })）"
CK (($enabledSet -join ',') -eq ($expectSet -join ',')) `
   "① 开启心跳的**站点集合**恰为预期 4 个（预期 [$(($expectSet) -join ',')]；实 [$(($enabledSet) -join ',')]）—— 集合断言：漏开/误开都能区分，不只是计数"
$hbLiteral = ([regex]::Matches($qp, 'heartbeat:\s*true')).Count
CK ($hbLiteral -eq $enabledTags.Count) `
   "① 文件里 `heartbeat: true` 的出现次数与「已解析并已标签的开启点」数一致（实 $hbLiteral vs $($enabledTags.Count)）—— 防有开启点落在解析窗口之外（如注释里、或写在调用点外）"

# ══ 素材自备 ════════════════════════════════════════════════════════════════════════
Write-Host "`n### 素材 ###" -ForegroundColor Cyan
$badJxl = "$work/one_00.jxl"
[System.IO.File]::WriteAllBytes($badJxl, [byte[]](0x00))   # 不以 FF 0A 开头 ⇒ Unknown ⇒ 通用兜底
$badBytes = [System.IO.File]::ReadAllBytes($badJxl)
CK (($badBytes.Length -eq 1) -and ($badBytes[0] -eq 0)) `
   "素材：畸形 .jxl = 1 B `00`（**逐字节**自检：实 $($badBytes.Length) B [$($badBytes[0].ToString('X2'))]）"

# 4K 源 → 4K JXL（本仓 ffmpeg 的 libjxl 出口默认写**裸码流**，前缀即 FF 0A ⇒ NativeCodestream）
$g1 = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=3840x2160`" -frames:v 1 `"$work/src4k.png`"" "gen4k"
$g2 = Exec $ff "-y -hide_banner -loglevel error -i `"$work/src4k.png`" -frames:v 1 -c:v libjxl `"$work/real4k.jxl`"" "gen4kjxl"
$real4k = "$work/real4k.jxl"
$headBytes = [System.IO.File]::ReadAllBytes($real4k)[0..1]
CK (($headBytes[0] -eq 0xFF) -and ($headBytes[1] -eq 0x0A)) `
   "素材：4K 真 JXL 以 `FF 0A` 开头（实 [$($headBytes[0].ToString('X2')) $($headBytes[1].ToString('X2'))]）⇒ 走 NativeCodestream"
CK ((Get-Item $real4k).Length -gt 100000) `
   "素材：4K JXL 足够大（实 $((Get-Item $real4k).Length) B）⇒ 高 effort 编码才有可测的耗时"

# ── ③-d 素材：动图 JXL（4K × 48 帧）─────────────────────────────────────────────────
#   ⚠ 造法：把同一段**裸码流**重复拼接 —— JXL 的帧序列就是这么表示的（本仓 ffprobe 也认）。
#     帧数用 **ffprobe 第三方确认**，不靠我们自称。
$one4k = "$work/one4k.jxl"
$g3 = Exec $ff "-y -hide_banner -loglevel error -i `"$work/src4k.png`" -frames:v 1 -c:v libjxl -effort 3 `"$one4k`"" "gen4kjxl1"
#   ⚠ 48 帧（不是 30）：本臂的「负控有牙」判据要求跑得**远久于**注入阈值，而 2026-09-22 探测修快后
#     30 帧实测只有 5.9–6.3 s（贴着 6 s 的地板 ⇒ 抖动就能翻红）。加帧是**加判据的牙**，不是降阈值。
$anim4k = "$work/anim4k_48.jxl"
$oneBytes = [System.IO.File]::ReadAllBytes($one4k)
$msA = New-Object System.IO.MemoryStream
for ($i = 0; $i -lt 48; $i++) { $msA.Write($oneBytes, 0, $oneBytes.Length) }
[System.IO.File]::WriteAllBytes($anim4k, $msA.ToArray()); $msA.Close()
$fcA = (& $fp -v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames -of csv=p=0 $anim4k 2>$null | Out-String).Trim()
CK ($fcA -eq '48') `
   "素材：动图 JXL 的帧数经 ffprobe 确认 = 48（实 $fcA）—— 第三方证据（本仓 ffprobe），不是自称"
CK ((Get-Item $anim4k).Length -gt 1000000) `
   "素材：动图 JXL 足够大（实 $((Get-Item $anim4k).Length) B）⇒ 编码耗时可测"

# ── ③-e 素材：JPEG 重构型（jbrd）JXL，**8K 且高熵（noise）** ────────────────────────
#   ⚠ 只有 cjxl 能造出 jbrd（ffmpeg 的 libjxl 出口**不写** jbrd box），开关是 `--lossless_jpeg=1`。
#     这里用**直接路径**调 cjxl —— 上面把 `FFMPEGGUI_JXL_LIB_DIR/PLAN_DIR/EXT_SEARCH_DIRS` 置空只影响
#     **被测应用**的探测，不影响我们自己造素材。
#   ⚠⚠ 为什么刻意加 `noise`：本臂的「负控有牙」要求"合法任务跑得**远久于**注入阈值 2 s"，而这份耗时
#     **必须来自真实编码**。实测 png/`-compression_level 9` 单帧：4K **干净**渐变 1.0 s、4K **噪声** 3.0 s
#     （zlib 咬不动高熵数据），6K 干净也只有 2.0 s ⇒ 在**低熵**下"分辨率"不是杠杆，**熵**才是；
#     一旦熵已拉满，像素数就回到**线性**杠杆 ⇒ 本素材用「8K ∧ noise」。
#   ⚠⚠ 2026-09-23/24 本臂连续两次翻车 ⇒ 结论是**它不该自带墙钟判据**：
#     第一次：`elapsed -ge 3` 在整轮里量到 **2.8 s** 红。真因是这条下限**没有同夹具落盘读数背书**
#       （3 s 照上面"4K 噪声 ≈ 3.0 s"的估值写的）。⚠ 我第一版把它归因成"同日修掉『启动期 5 级链跑两遍』
#       把固定开销削快了"——**该归因是错的**：`ProbeAllTools` 全仓唯一调用点是 `MainWindow.xaml.cs:1248`
#       （GUI Step3），而 `RunApp` 起的是 headless（`Program.cs:267` 走 `EnsureAllDetected`），
#       两条根本不在一条路上；我当"前值"用的 3.8 s 还是 09-19 的**另一种素材**。完整登记见 §6 第 103 条。
#     第二次：按"做实工作量"升到 8K ⇒ 空闲机 10.3 s 全绿，**但整轮负载下 8.8 s 被真心跳判死**
#       （产物 0、日志含「无进度心跳」；同一时刻 ③/③-d 耗时也涨到单跑的 1.5–2 倍 ⇒ 是负载不是抖动）。
#       机制：站点 A 的目标是**单帧** png + zlib 9 ⇒ ffmpeg 一次编码几乎只发一统计块 ⇒
#       「无进度窗口」≈ 整个编码时长 ⇒ `阈值 > 窗口`（别误杀）与 `阈值 < 时长`（有牙）**互斥**，
#       加大工作量只会把两个数一起推大 ⇒ 这条臂上的墙钟判据原理上不可能稳。
#   ⇒ 维护者拍板（2026-09-24）：**本臂只保留站点 A 的覆盖**，"按总墙钟判死"这条能力交给
#     ③ / ③-d 两处 `elapsed -ge 6`（多帧任务、块流持续 ⇒ 与负载解耦；实测 16.0/10.1 s ⇒ 余量 2.7–3.8×）。
#     夹具回落到 4K 噪声以缩短无进度窗口。为防以后有人把 ③/③-d 那两条也顺手删掉，
#     下面用一条**形状锁**钉住"该能力仍有两处载体"（见 ③-e 段末）。
#   ⚠ 上面"4K 干净 1.0 s / 4K 噪声 3.0 s / 6K 干净 2.0 s"三档沿用自上一位作者的注释、本次未复测。
#   ⚠ 上限 `negLimitSec = 90` 仍宽松。目标格式必须是 png + `--quality 0`（约束见下方执行处）。
$srcNoisyJpg = "$work/src4k_noisy.jpg"
$g4 = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=3840x2160:duration=0.1`" -frames:v 1 -vf `"format=rgb48le,noise=alls=60:allf=u`" -q:v 2 `"$srcNoisyJpg`"" "gen4knoisy"
$jbrdNoisy = "$work/jbrd4k_noisy.jxl"
$g5 = Exec $cjxl "`"$srcNoisyJpg`" `"$jbrdNoisy`" --lossless_jpeg=1" "genjbrd"
CK ((Test-Path $jbrdNoisy) -and ($g5.code -eq 0) -and ((Get-Item $jbrdNoisy).Length -gt 1000)) `
   "素材：jbrd 素材已生成（cjxl `--lossless_jpeg=1`，exit=$($g5.code)，实 $((Get-Item $jbrdNoisy).Length) B）"
$jbHead = [System.IO.File]::ReadAllBytes($jbrdNoisy)[0..11]
CK ([System.Text.Encoding]::ASCII.GetString($jbHead, 4, 3) -eq 'JXL') `
   "素材：jbrd 素材是 ISOBMFF 容器（bytes 4..6 = `JXL`）—— 裸码流**不可能**被判 JPEG 重构，故这是必要条件"

# ══ ② 正向：忙等无心跳 ⇒ 限时判死（**自行**结束，不是被本门禁 Kill）════════════════════
Write-Host "`n### ② 正向：畸形 .jxl 忙等 ⇒ 心跳限时判死 ###" -ForegroundColor Cyan
$rPos = RunApp $badJxl "$work/out_pos" "pos" $posLimitSec @() $null
Write-Host ("  自行结束={0} 被门禁Kill={1} 耗时={2:N1}s 产物={3}" -f $rPos.selfEnded, $rPos.killed, $rPos.elapsed, $rPos.prods)
$needle = "[超时] ffmpeg 连续 $hbSec 秒**无进度心跳**"
CK ($rPos.selfEnded -and (-not $rPos.killed)) `
   "② 应用**自行**判死退出（未被本门禁 Kill；耗时 $([math]::Round($rPos.elapsed,1)) s ≤ 上限 $posLimitSec s）—— 忙等型挂死不再永不返回"
CK ($rPos.text.Contains($needle)) `
   "② 逐字超时行「$needle…」（**响亮**，不静默 return）"
CK ($rPos.text -match '失败 \(退出码 -124\)') `
   "② 终态为 `失败 (退出码 -124)`（HangExitCode 显式失败，与真实编码失败可区分）"
CK ($rPos.text -match '\[心跳\] 已追加') `
   "② 自检：日志确实打了「[心跳] 已追加」⇒ 本条不是真空绿（心跳真的开了）"
CK ($rPos.prods -eq 0) "② 忙等被判死 ⇒ 产物 0 个（实 $($rPos.prods)）—— 不得留下半成品"

# ══ ③ 反向（决定性）：慢但心跳 ⇒ 不得误杀 —— 站点 C（jxl-native-no-djxl, :3036）════════
Write-Host "`n### ③ 反向：合法 4K 单帧 libjxl -effort 9 慢任务（站点 C）⇒ **不得**误杀 ###" -ForegroundColor Cyan
$rNeg = RunApp $real4k "$work/out_neg" "neg" $negLimitSec @("--jxl-effort", "9") $null "jxl"
Write-Host ("  自行结束={0} 被门禁Kill={1} 耗时={2:N1}s 产物={3}" -f $rNeg.selfEnded, $rNeg.killed, $rNeg.elapsed, $rNeg.prods)
CK ($rNeg.text -match 'djxl: 不可用') `
   "③ 自检：djxl 真的不可用（否则会走 djxl→cjxl 管道、**根本到不了**启用心跳的 :3036 ⇒ 本条真空绿）"
CK ($rNeg.text -match '\[心跳\] 已追加') `
   "③ 自检：心跳确实开在这次运行上（`[心跳] 已追加`）"
CK ($rNeg.selfEnded -and (-not $rNeg.killed)) `
   "③ 合法慢任务**没有被误杀**（自行正常结束，未被门禁 Kill；耗时 $([math]::Round($rNeg.elapsed,1)) s）"
CK ($rNeg.elapsed -ge 6) `
   "③ **负控有牙**：实测总耗时 $([math]::Round($rNeg.elapsed,1)) s ≥ 6 s，**远大于**注入的心跳阈值 $hbSec s ⇒ 任何「按总墙钟判死」的错误实现都会在本条转红"
CK (-not ($rNeg.text -match '无进度心跳')) `
   "③ 日志**不含**「无进度心跳」⇒ 心跳判死没有对合法长任务开火"
CK ($rNeg.prods -eq 1) "③ 产物 1 个（实 $($rNeg.prods)）—— 合法任务仍然出结果"
CK ($rNeg.text -match '(?m)^\[cmd\] ffmpeg ') `
   "③ 自检：确实走到了 `[cmd] ffmpeg`（即启用心跳的那条 ffmpeg 路径）"
# ⚠ 这一组是 team-lead 要求「实测确认加 -progress 不干扰既有日志」的核心。
#   ⚠ 判据**不能**用 `^frame=`：应用跑的是**默认 loglevel**，ffmpeg 的 **stderr 统计行**本来就以
#     `frame=` 开头（实测 21 行）—— 用它会把「既有输出仍在」误判成「泄漏」。
#     只认 `-progress` **专属**的键：`progress=` / `out_time_us=` / `total_size=` / `stream_N_N_q=`
#     （实测：修实现前 `stream_0_0_q=` 会漏 21 行 —— 本门禁就是这么抓到那个 bug 的）。
$leak = ([regex]::Matches($rNeg.text, '(?m)^(progress|out_time_us|total_size|stream_\d+_\d+_q)=')).Count
CK ($leak -eq 0) `
   "③ 日志里 `-progress` **专属**块行 0 条（实 $leak）⇒ 心跳被 FfmpegRunner 内部消化，**没有污染既有日志**（实测，不是读码推断）"
CK ($rNeg.text -match '(?m)^frame=') `
   "③ 既有 **stderr 统计行**仍在（`^frame=` 命中）⇒ 心跳没有把原有输出吃掉（ProgressWindow 的既有解析依赖它）"
CK ($rNeg.text -notmatch '(?m)^\[cmd\] ffmpeg .*-progress') `
   "③ `[cmd] ffmpeg` 行**不含** `-progress`（调用点用的是自己的局部 args）⇒ 断言命令行的既有门禁零影响"

# ══ ③-d 反向：慢但心跳 ⇒ 不得误杀 —— 站点 B（jxl-anim, :2951）══════════════════════════
Write-Host "`n### ③-d 反向：动图 JXL（4K × 30 帧）--format apng（站点 B）⇒ **不得**误杀 ###" -ForegroundColor Cyan
#   ⚠ 站点可达性**不靠耗时推断**，靠站点 B 的**专属日志行**：`动图 JXL 不进行 djxl→PNG 中间解码`。
#     它在 `IsAnimated(item)` 为真、且 `DjxlService.IsAvailable` 分支**之前**打印 ⇒ 只有真进了
#     动图分支才会有这一行（进站点 C 会打「未检测到 djxl，使用 ffmpeg 直接处理 JXL」，不是这句）。
# ⚠ `--quality 0` 是给 apng 的 zlib 拉到最高级 ⇒ 这份"合法慢"由**真实编码工作量**提供。
#   2026-09-22 实测：探测扫整台机器被修快后，本条从 26.3 s 塌到 **5.97 s**（阈值 6 s ⇒ 红），
#   塌下去的那 20 s 从来不是编码时间。与 ③-e 同一处方，见那里的 ⚠⚠ 注释。
$rAnim = RunApp $anim4k "$work/out_anim" "anim" $animLimitSec @("--quality", "0") @{ FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC = "$animProbeSec" } "apng"
Write-Host ("  自行结束={0} 被门禁Kill={1} 耗时={2:N1}s 产物={3}" -f $rAnim.selfEnded, $rAnim.killed, $rAnim.elapsed, $rAnim.prods)
CK ($rAnim.text -match '\[jxl\] 输入类型: 原生') `
   "③-d 自检：输入被判为原生 JXL 码流（`[jxl] 输入类型: 原生`）"
CK ($rAnim.text -match '动图 JXL 不进行 djxl→PNG 中间解码') `
   "③-d 自检：**站点 B 真的被走到**（站点 B 专属日志行「动图 JXL 不进行 djxl→PNG 中间解码」）—— 不靠耗时推断"
CK (-not ($rAnim.text -match '未检测到 djxl，使用 ffmpeg')) `
   "③-d 自检：**不是**站点 C（站点 C 的行是「未检测到 djxl，使用 ffmpeg 直接处理 JXL」）—— 排除张冠李戴"
CK (-not ($rAnim.text -match '帧数探测超时')) `
   "③-d 自检：帧数探测**没有超时**（探测超时被放宽到 $animProbeSec s）—— 排除「探测超时 ⇒ 按静图」这条旁路"
CK ($rAnim.text -match '\[心跳\] 已追加') `
   "③-d 自检：心跳确实开在这次运行上（`[心跳] 已追加`）"
CK ($rAnim.selfEnded -and (-not $rAnim.killed)) `
   "③-d 合法动图慢任务**没有被误杀**（自行正常结束，未被门禁 Kill；耗时 $([math]::Round($rAnim.elapsed,1)) s）"
CK ($rAnim.elapsed -ge 6) `
   "③-d **负控有牙**：实测总耗时 $([math]::Round($rAnim.elapsed,1)) s ≥ 6 s（≈ 注入阈值 $hbSec s 的 3×以上）⇒ 「按总墙钟判死」的实现会在本条转红"
CK (-not ($rAnim.text -match '无进度心跳')) `
   "③-d 日志**不含**「无进度心跳」⇒ 心跳判死没有对合法动图长任务开火"
CK ($rAnim.prods -eq 1) "③-d 产物 1 个（实 $($rAnim.prods)）—— 合法动图任务仍然出结果"
CK ($rAnim.text -match '(?m)^\[cmd\] ffmpeg ') `
   "③-d 自检：确实走到了 `[cmd] ffmpeg`（即启用心跳的那条 ffmpeg 路径）"
$leakA = ([regex]::Matches($rAnim.text, '(?m)^(progress|out_time_us|total_size|stream_\d+_\d+_q)=')).Count
CK ($leakA -eq 0) `
   "③-d 日志里 `-progress` **专属**块行 0 条（实 $leakA）⇒ 心跳在多帧编码上同样被内部消化"
CK ($rAnim.text -notmatch '(?m)^\[cmd\] ffmpeg .*-progress') `
   "③-d `[cmd] ffmpeg` 行**不含** `-progress` 选项"

# ══ ③-e 反向：慢但心跳 ⇒ 不得误杀 —— 站点 A（jxl-recon-no-djxl, :2934）════════════════
Write-Host "`n### ③-e 反向：JPEG 重构型 JXL + djxl 不可用（站点 A）⇒ **不得**误杀 ###" -ForegroundColor Cyan
#   ⚠ 站点 A 与站点 C 共用同一句 `[cmd] ffmpeg`，区别在**入口**：A 的入口行是
#     「`[jxl] 输入类型: JPEG 重构`」+「`[djxl] 未检测到 djxl，回退到 ffmpeg`」（`回退到`，不是`使用`）。
#   ⚠⚠ 目标格式必须是 png + `--quality 0`（映射到 zlib `-compression_level 9`，见
#     `ImageEncoderArgs.MapPngCompression`），**不能**是 `--format jxl`：实测把目标改成 jxl 后
#     本条从站点 A 漂到站点 C（`使用 ffmpeg 直接处理 JXL`）⇒ 覆盖不到 A 的入口分支。
#   ⚠⚠ 2026-09-22：修掉「外部工具探测递归扫整台机器」后，本条从 20.7 s 掉到 **1.3 s** —— 那 20 s
#     本来就是探测开销、不是编码 ⇒ "跑得比注入阈值久"这条**负控**失去判据。必须由**真实编码工作量**
#     提供这份余量（下面那条「负控有牙」红得对，不许靠 lowering 阈值蒙过去）。
$rRecon = RunApp $jbrdNoisy "$work/out_recon" "recon" $negLimitSec @("--quality", "0") $null
Write-Host ("  自行结束={0} 被门禁Kill={1} 耗时={2:N1}s 产物={3}" -f $rRecon.selfEnded, $rRecon.killed, $rRecon.elapsed, $rRecon.prods)
CK ($rRecon.text -match '\[jxl\] 输入类型: JPEG 重构') `
   "③-e 自检：输入被判为 JPEG 重构型（`[jxl] 输入类型: JPEG 重构`）—— jbrd 容器经应用自己确认"
CK ($rRecon.text -match '\[djxl\] 未检测到 djxl，回退到 ffmpeg') `
   "③-e 自检：**站点 A 真的被走到**（站点 A 专属日志行「[djxl] 未检测到 djxl，回退到 ffmpeg」）"
CK (-not ($rRecon.text -match '未检测到 djxl，使用 ffmpeg')) `
   "③-e 自检：**不是**站点 C（C 的行是「使用 ffmpeg 直接处理 JXL」）—— 排除张冠李戴"
CK ($rRecon.text -match 'djxl: 不可用') `
   "③-e 自检：djxl 真的不可用（否则会走 `djxl 重构 JPEG`、**根本到不了**站点 A ⇒ 本条真空绿）"
CK ($rRecon.text -match '\[心跳\] 已追加') `
   "③-e 自检：心跳确实开在这次运行上（`[心跳] 已追加`）"
CK ($rRecon.selfEnded -and (-not $rRecon.killed)) `
   "③-e 合法慢任务**没有被误杀**（自行正常结束，未被门禁 Kill；耗时 $([math]::Round($rRecon.elapsed,1)) s）"
# ⚠ 「按总墙钟判死会被抓住」这条能力**不在本臂上**（单帧编码的无进度窗口 ≈ 时长 ⇒ 与阈值互斥，
#   见上方素材段的 2026-09-23/24 两次翻车记录）。它的载体是 ③ / ③-d 两条多帧臂。
#   这里用一条**形状锁**钉住"能力仍有两处载体"：谁把 ③/③-d 的 `elapsed -ge 6` 删了或调低，本条即红，
#   防止"把判据从一处搬走"最后变成"哪儿都没有判据"。⚠ 正则必须锚到 `CK (` 上：
#   本文件**注释里**也出现 `elapsed -ge 6` 这个串，裸数会多数。
$wallClockCarriers = @(Get-Content -LiteralPath $PSCommandPath -Raw |
    ForEach-Object { [regex]::Matches($_, '(?m)^\s*CK \(\$r(?:Neg|Anim)\.elapsed -ge 6\)') } | ForEach-Object { $_.Value })
Write-Host ("  [读数] ③-e 本次耗时 $([math]::Round($rRecon.elapsed,1)) s（**只作诊断打印**，不作判据）；墙钟判据载体数 = " + $wallClockCarriers.Count)
CK ($wallClockCarriers.Count -eq 2) `
   "③-e 能力移交锁：『按总墙钟判死必被抓住』必须由 ③/③-d 两条「elapsed -ge 6」承载（实 $($wallClockCarriers.Count)，须 2）"
CK (-not ($rRecon.text -match '无进度心跳')) `
   "③-e 日志**不含**「无进度心跳」⇒ 心跳判死没有对合法慢任务开火"
CK ($rRecon.prods -eq 1) "③-e 产物 1 个（实 $($rRecon.prods)）—— 合法任务仍然出结果"
$leakB = ([regex]::Matches($rRecon.text, '(?m)^(progress|out_time_us|total_size|stream_\d+_\d+_q)=')).Count
CK ($leakB -eq 0) `
   "③-e 日志里 `-progress` **专属**块行 0 条（实 $leakB）⇒ 站点 A 上同样没有污染既有日志"
CK ($rRecon.text -notmatch '(?m)^\[cmd\] ffmpeg .*-progress') `
   "③-e `[cmd] ffmpeg` 行**不含** `-progress` 选项"

# ══ ⑤ 心跳二分性（有进度块 ⇒ 存活 / 无进度块 ⇒ 判死）＋「合法无块窗口」的实测上界 ═══════
# ── 为什么需要这一节 ────────────────────────────────────────────────────────────────
#   #15-b 把心跳改成**默认行为**后，**新引入**一条残余风险：默认阈值（120 s）现在由**全部**调用点承担
#   ⇒ 若某个**合法**任务会出现「> 120 s 一个进度块都没有」的窗口，它会被**误杀**。
#   在此之前只有 JXL 链 4 个站点承担该阈值 ⇒ 风险面被放大了。本仓的规矩是：
#   「登记了残余风险就必须把它钉成可验证的断言，否则它会以『我们以为安全』的形式留下」（TESTING §6）。
#   ⇒ 本节的钉法有**两条腿**，缺一不可：
#     (i) **量化上界**（N1）：直接量**合法任务**的进度块节奏（本仓 ffmpeg，不经被测应用）
#         ⇒ 若平均块间隔 ≪ 120 s，则「合法 > 120 s 无块窗口」在**实测节奏**下不可能；
#     (ii) **二分性**（N2）：在**同一个**注入阈值下，无块臂**必须判死** ∧ 有块臂**必须存活**
#         ⇒ 证明判据对「有块/无块」是二分的，而不是「按总墙钟」或「按 CPU」。
#   ⚠ 为什么 (i) 必须直接量 ffmpeg 而不能只靠被测应用的日志：应用**故意不上抛**进度块
#     （每 ~0.5 s 一块 × 11 行会淹没队列日志，见 FfmpegRunner 的 OutputDataReceived）
#     ⇒ 从应用日志里**拿不到**块到达时刻。直接量是唯一能给出**定量**上界的手段。
Write-Host "`n### ⑤ 心跳二分性 ＋ 合法无块窗口的实测上界 ###" -ForegroundColor Cyan

# ── N1：合法任务的进度块节奏（直接调本仓 ffmpeg，`-progress pipe:1`，输出落盘后统计）──────
#   ⚠ 用**块数 / 总耗时**的**比值**判据，而不是「块到达时刻」：比值对机器负载/采样抖动免疫
#     （ffmpeg 的进度线程是**按时间**发块的，机器越慢块也按比例变少 ⇒ 比值稳定）。
#     「按时刻采样」在本机（与其它代理的门禁并发）会抖 ⇒ 判据会假红。
$rateOut = "$work/rate.out"; $rateErr = "$work/rate.err"
try { [System.IO.File]::Delete($rateOut) } catch { }
try { [System.IO.File]::Delete($rateErr) } catch { }
$swRate = [System.Diagnostics.Stopwatch]::StartNew()
$pRate = Start-Process -FilePath $ff `
  -ArgumentList @("-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                  "-i", "testsrc2=s=3840x2160", "-frames:v", "1", "-c:v", "libjxl", "-effort", "9",
                  "-progress", "pipe:1", "$work/rate.jxl") `
  -Wait -NoNewWindow -PassThru -RedirectStandardOutput $rateOut -RedirectStandardError $rateErr
$swRate.Stop()
$rateTxt = [System.IO.File]::ReadAllText($rateOut)
$rateBlocks = ([regex]::Matches($rateTxt, '(?m)^progress=continue\s*$')).Count
$rateEnd    = ([regex]::Matches($rateTxt, '(?m)^progress=end\s*$')).Count
$rateSec    = $swRate.Elapsed.TotalSeconds
$rateAvgGap = 999.0
if ($rateBlocks -gt 0) { $rateAvgGap = $rateSec / $rateBlocks }
Write-Host ("  合法慢任务(4K libjxl -effort 9): 耗时={0:N1}s continue块={1} 收尾块={2} 平均块间隔={3:N3}s 退出码={4}" -f `
  $rateSec, $rateBlocks, $rateEnd, $rateAvgGap, $pRate.ExitCode)
CK (($rateBlocks -ge 5) -and ($rateEnd -ge 1) -and ($rateTxt -match '(?m)^out_time_us=')) `
   "⑤-N1a 合法任务的进度流**形态自检**：continue 块 $rateBlocks 个（须 ≥5）、收尾块 $rateEnd 个（须 ≥1）、且含 out_time_us ⇒ 统计对象真的是进度流（防「空文件 ⇒ 块数 0 的真空红」与「非进度流 ⇒ 真空绿」）"
CK ($rateAvgGap -le 1.0) `
   "⑤-N1b 合法任务的**平均块间隔 $([math]::Round($rateAvgGap,3)) s ≤ 1.0 s**（实测，非定性）⇒ 生产默认 120 s 阈值对「合法任务的无块窗口」有 **≥ $([math]::Floor(120 / [math]::Max($rateAvgGap, 0.001)))× 余量** ⇒ 「合法任务 > 120 s 无进度块」在实测节奏下不可能"

# ── N2：二分性必须**成对**成立，且两臂**注入阈值相同** ────────────────────────────────
#   ⚠ 只看单臂会假绿：「无块臂判死」也可能是「按总墙钟判死」的实现凑巧过的（③ 的耗时下界就是为堵它）；
#     「有块臂存活」也可能是阈值其实没注入（比如 env 名写错）。⇒ 必须**同时**断言：
#       ① 两臂日志里解析出的注入阈值**相同**且 == 本门禁注入的 $hbSec（证明二分不是「两个阈值」造的假象）；
#       ② 同一阈值下**结局相反**（无块臂判死 ∧ 有块臂存活且出产物）。
$hbPos = ""; $hbNeg = ""
$mP = [regex]::Match($rPos.text, '连续 (\d+) 秒无进度块')
if ($mP.Success) { $hbPos = $mP.Groups[1].Value }
$mN = [regex]::Match($rNeg.text, '连续 (\d+) 秒无进度块')
if ($mN.Success) { $hbNeg = $mN.Groups[1].Value }
CK (($hbPos -eq "$hbSec") -and ($hbNeg -eq "$hbSec")) `
   "⑤-N2a 两臂的**注入阈值相同**且都 == $hbSec s（无块臂日志解析=$hbPos / 有块臂日志解析=$hbNeg）—— 从**各自日志**解析，证明二分性不是「两个不同阈值」造出来的假象"
$armDead  = $rPos.selfEnded -and (-not $rPos.killed) -and ($rPos.text -match '无进度心跳') -and ($rPos.prods -eq 0)
$armAlive = $rNeg.selfEnded -and (-not $rNeg.killed) -and ($rNeg.prods -eq 1) -and (-not ($rNeg.text -match '无进度心跳'))
CK ($armDead -and $armAlive) `
   "⑤-N2b **二分性**（同一 $hbSec s 阈值）：**无进度块**臂判死（$armDead：自行判死 + 零产物 + 逐字超时行）∧ **有进度块**臂存活（$armAlive：$([math]::Round($rNeg.elapsed,1)) s ≫ 阈值，仍出产物且无超时行）⇒ 判据对「有块/无块」二分，且不是「按总墙钟」或「按 CPU」判的"

# ══ 收尾：只清扫本门禁起过的进程；并做源码漂移自检 ═════════════════════════════════════
foreach ($spid in $script:spawned) {
  try { $pr = Get-Process -Id $spid -ErrorAction SilentlyContinue } catch { $pr = $null }
  if ($pr) { KillTree ([int]$spid) }
}
$exeMtimeAfter = (Get-Item $exe).LastWriteTimeUtc
if ($exeMtimeAfter -ne $exeMtimeBefore) {
  Write-Host "[WARN] 跑期间源码/exe 被改动 ⇒ 本轮读数作废（exe mtime $exeMtimeBefore → $exeMtimeAfter）" -ForegroundColor Yellow
  $script:fail++
}

# ── ④ 残留自证：全绿才清运行级目录（红时保留现场供取证）────────────────────────────
Write-Host "`n### ④ 残留自证 ###" -ForegroundColor Cyan
if ($fail -eq 0) {
  try { [System.IO.Directory]::Delete($work, $true) } catch { }
  CK (-not (Test-Path $work)) "④ 运行级目录已清零：$work"
} else {
  Write-Host "  有失败 ⇒ 保留现场：$work" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "===== ffmpeg-heartbeat: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($fail -ne 0) { exit 1 }
exit 0
