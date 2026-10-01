# verify-webp-anim-probe.ps1 —— webp 动画探测的「合取判据 + 三态化」专项门禁（2026-09-18 新增，`#136` 切片 B）
#
# 缺陷（本门禁要锁住的东西）：
#   `QueueProcessor.InputMayHaveMultipleFrames` 在「输出不是 gif/apng」时**直接回落** `IsAnimated`，
#   而 `IsAnimated` 对 `.webp`/`.avif` **恒 false**（它不探测这两者的帧数）⇒
#   **多帧 webp + 非 gif/apng 目标**被判成单帧 ⇒ 引擎按单帧处理 ⇒ **静默丢帧**（`#43` 登记的「路由轴未保守化」）。
#   反向的那一格同样错：输出是 gif/apng 时把 `.webp/.avif` **无条件**当多帧 ⇒ 静态 webp 永远进不了引擎。
#   ⚠ **`#136` 收尾轮更新（2026-09-18）**：上面那句「`IsAnimated` 对 `.webp` 恒 false」**已过期** ——
#     `IsAnimated` 的**输入侧**已补 `.webp`（读 `AnimatedWebpCache`）⇒ 现在只剩 `.avif` 恒 false。
#     且**预缩放点已不再调 `IsAnimated`**：改为消费 `ProcessAsync` 单点求值的 `inputMayHaveMultipleFrames`。
#     本门禁的 ② 段新增**源码形态回归锁**钉住这两件事（见下方「② 回归锁（收尾轮）」）。
#
# 修复判据（`#136` 切片 B）：
#   ① **新增 webp 轴 B 探测**（`IsAnimatedWebpAsync`），判据是**合取式**：
#        · `ffprobe -show_entries format=format_name`（零解码）⇒ `webp_pipe` = 静态（单帧）；
#          `webp_anim` = 动画**容器** ⇒ **必须再跑第 2 步**；其余/报错/超时 ⇒ 不确定。
#        · `ffprobe -count_packets -show_entries stream=nb_read_packets`（**全流**）⇒
#          `max(包数) > 1` = 多帧、`== 1` = 单帧；解析不出 ⇒ 不确定。
#      ⚠ **为什么必须合取**（实测）：`webp_anim` 只是**容器**属性（`VP8X` 的动画标志位 `0x02` 置位
#        **且** ≥1 个 `ANMF`）。存在**合法但只有 1 帧**的形态（`VP8X(0x02)+ANIM+1×ANMF`）⇒
#        单用 `format_name` 会把它误判成多帧（本门禁的 `oneframe_anim.webp` 就是它）。
#      ⚠ 单用帧数也不够：`VP8X(0x02)` 置位却**没有** `ANMF` 是**非法**文件（ffprobe 报
#        `Invalid data found`）⇒ 那一路必须落「不确定」，不是"单帧"。
#   ② **全流、禁 `v:0`**：对多帧容器 `v:0` 恒为主图 item，会把多帧读成 1 帧（在决策路径上 = 静默丢帧）。
#   ③ **三态化**：返回 `(bool animated, bool determinate)`；不确定 ⇒ 保守按**多帧**。
#   ④ **超时 20 s** + 环境变量 `FFMPEGGUI_WEBP_PROBE_TIMEOUT_SEC`（夹紧 [1,600]，照 `ResolveJxlProbeTimeoutSec`）。
#      实现用「链接 CTS + 杀树看门狗」+ `Kill(entireProcessTree: true)`，**保持异步**。
#   ⑤ **两支统一 + 每项只求值一次**：`InputMayHaveMultipleFramesAsync` 的输入侧子句两支共用，
#      由 `ProcessAsync` 在**路由判定之前**单点求值，供 `ShouldRoute`、GainMap 专用分派、
#      `TryRunColorEngineAsync` 三处复用（三处必须是同一份判据）。
#      ⚠ `#136` 收尾轮新增**第 4 个**消费点 = **预缩放跳过判定**（`maxDimSkipAnimated`）：
#        它已由 `IsAnimated(captured)` 改为直接消费同一个变量（理由：预缩放要跳过的只是
#        「**输入**有多帧」；旧判据里「**输出**容器是 gif/apng」那一轴对预缩放点是多余的）。
#
# 断言：
#   ① 素材前提断言 6 条（**第一验收点**：实际帧数 + `format_name` **都先断言**，**不信任 `-frames:v`**）
#      · **前置**：12 个 ffprobe 探测**无超时、输出都读到**（`Exec` 有界：轮询 + 到点杀进程树，
#        因为判别性素材是**字节手术**造的 ⇒ 最可能让解码器不关管道而永不返回）
#      · still.webp          = `webp_pipe` + 1 包
#      · anim.webp           = `webp_anim` + 8 包（**实际**包数，不是"我们请求了 8"）
#      · oneframe_anim.webp  = `webp_anim` + **1** 包  ← 合取判据的**判别性**素材（字节手术构造）
#      · static_libwebp_anim = `webp_pipe` + 1 包      ← 负控材料（`libwebp_anim` + 静态源**造不出** `webp_anim`）
#      · 交叉断言（**布尔**，不写比值）：四个素材都满足 `(packets > 1) == (frames > 1)`
#   ② 源码形态自检 17 条（超时预算 / 合取两步 / 禁 `v:0` 与禁 `-count_frames` / **新锁的负控+反控** / 看门狗异步 / 两支统一 / 三态消费 / **回归锁「输入侧判据不得看输出容器」+负控+反控** / **收尾轮锁：`IsAnimated` 输入侧补 `.webp` 且读缓存 + 输出容器轴未被折进 + 预缩放点消费同一份判据**（各带负控与内存内反控））
#   ③ 行为（**端到端、双向**）18 条：
#      · **前置**：两组的子进程输出**都读到了**（`readOk`）—— 重定向文件在子进程退出瞬间可能仍被占用，
#        读失败**必须点名**，否则会伪装成「没有输出」⇒ 日志形状判据**假红**（本文件实测踩到过）
#      · still.webp          → png ⇒ **走引擎**（出现 `[色彩引擎] ✅`，真执行不是直通）
#      · anim.webp           → png ⇒ **命中 `AnimatedInput` 落 legacy**（出现 `本次不适用引擎（AnimatedInput：`，且**无** `✅`）
#      · oneframe_anim.webp  → png ⇒ **走引擎**（`✅`）← 合取判据的**行为证据**：只看 `format_name` 会把它拦掉
#      · static_libwebp_anim → png ⇒ **走引擎**（`✅`）← 负控材料按静态处理
#      · ⭐ anim.webp（8 帧）→ **webp** ⇒ 产物 packets **== 8**（**核心格**：上面 4 格是**单帧容器**，
#        **证明不了**「丢帧被修好」；只有「**能装多帧的目标**」才触到真缺陷）
#      · ⭐ anim.webp（8 帧）→ **gif**  ⇒ 产物 packets **== 8**（同上，第二个方向；⚠ 该格**不带**
#        `--color-space`：gif 不能携带 ICC ⇒ 带它会被 `色彩裁决` 以 `Reject` 拒绝、产物 0，与 B2 无关）
#      · webp 格**同时**命中 `AnimatedInput`（⇒ 保帧来自 legacy，不是引擎）
#      · ⭐ **③(c) 最长边预缩放（收尾轮新增，此前零覆盖）**：三格双向 + 一格专属，判据一律取
#        **证据行** `[MaxDimension] 检测到超限`（**不得**取产物尺寸：两态都 32x24 ⇒ 恒真）：
#        `anim.webp + dim` ⇒ **不含**（动图被跳过）；`still.webp + dim` ⇒ **含**（**负控**：证明这行能出现）；
#        `still.webp → gif + dim` ⇒ **含**（**丙 专属格**：旧判据因 fmt=gif 会跳过预缩放）
#      · ⭐ **③(d) 「热缓存」前提（2026-09-19 新增，此前唯一真盲区）**：`anim.webp + --encoder cjxl
#        --color-engine legacy` ⇒ 日志**含**「[cjxl] 动图/不支持格式（退出码 N），回退到 FFmpeg
#        libjxl_anim 编码器」（证明 `IsAnimated` 在该点返回 true ⇒ `AnimatedWebpCache` 是热的）；
#        负控 = `still.webp` 同参数 ⇒ **不含**该行，且**必须含**「[cjxl] 直接编码失败 (退出码 N)」
#        （自检：证明负控**同路径**，不是"路径不同才没有"）。共 5 条 = 1 前置 + 1 同路径自检
#        + 1 正例 + 1 负控自检 + 1 负控。
#   ④ 被测 exe mtime 跑前跑后一致 1 条（§6 第 84 条）
#   ⑤ 单独一次 `-Wait -PassThru` 取 still.webp 真退出码 1 条（避开 PS5.1 轮询路径的 `.ExitCode` 恒 `$null` 坑）
#   ⑥ 兜底清扫 + 残留自证 1 条
#   ⇒ 合计 6 + 17 + 18 + 1 + 1 + 1 = **44 条**（2026-09-18 收尾轮：29 → 39；2026-09-19 补 ③(d)：39 → 44）
#
# ⚠⚠ **已登记的覆盖缺口（措辞不得大于扫描面，`§6` 第 87 条）**：
#   **超时分支本身没有被行为实测。** 本门禁没有构造出让 `ffprobe` 在 webp 上**挂住**的素材
#   （与 `verify-avif-anim-probe.ps1` 的同款缺口一致：造一个"会挂住的假 ffprobe.exe"需要自备 PE 二进制）。
#   ⇒ 对「超时可达 + Kill 生效」只给**源码形态自检**（同 `§6` 第 90 条的既有做法），
#     **不**声称"已实测超时被触发"。
#
# ⚠⚠ **清理纪律（本仓实证教训，预防性写入）**：临时目录的清理**只能用运行时记录下来的精确路径**，
#   **禁止通配**（`Remove-Item g1_*` / `Get-ChildItem $env:TEMP -Filter 'x*' | Remove-Item`）。
#   根因：`%TEMP%` 是**共享命名空间**，前缀约定没有"归属"维度；一次通配清理会**必然**误删别的
#   actor 的中间物（本会话实测发生过：一个 `g1_*` 清理段删掉了另一个 actor 的两个工作目录）。
#   ⇒ 本门禁：① 语料一律建在**运行级 GUID 目录**里；② 清理用 `[System.IO.Directory]::Delete($true)`
#     删**那一个**已知路径，绝不按模式删。
#
# 退出码：0 = 全绿，1 = 有失败。⚠ 结尾必须是**完整 if/else**（`§6` 第 67 条：清理步会污染
#   `$LASTEXITCODE`，只写 `if ($fail -gt 0) { exit 1 }` 会让全绿路径以被污染的 2 退出 ⇒ 假红）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1，门禁 `verify-ps-compat.ps1`）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；
#   双引号串内禁全角弯引号（用 「」）；取子进程输出一律 `Start-Process` + 重定向。
# ⚠⚠ **双引号串内禁用反引号做 markdown 定界符**（本文件初版实测踩到，2026-09-18）：
#   ` ` ` 在双引号串里是**转义字符** ⇒ 写 `` `format_name` `` 时 `` `f `` 被解释成**换页符**、
#   `` `nb_read_packets` `` 的 `` `n `` 被解释成**换行**、`` `v:0` `` 的 `` `v `` 被解释成**垂直制表**
#   ⇒ 报错信息被静默吃掉字符（`format_name` 打印成 `ormat_name`），而**门禁照样全绿**。
#   更坏的是同一串里若有**未转义的 `"`**，字符串会**提前闭合**：后半截变成额外的**实参**，
#   被 `CK` 忽略的 `$args` 吞掉 ⇒ 既不报解析错（`verify-ps-compat` 抓不到）、也不报运行错，
#   **只在输出里少半句话**（本文件第 289 行初版即此形态）。
#   ⇒ 纪律：消息串里要引用标识符一律用 「」 或直接裸写，**不要**用反引号。
#   （反引号仅在三种**有意**场合使用：行尾续行、`` "`n" ``/`` "`r?`n" `` 换行、`` `" `` 转义引号。）
# ⚠ **禁止 `Start-Process pwsh`**（宿主安全策略会拦）⇒ 限时 + 杀进程树一律在**本进程内**实现。
# ⚠⚠ **形参名禁用 `$args` / `$pid`**：`$args` 是 PowerShell 自动变量，`-ArgumentList $args` 会拿到**空**
#   （本轮实测踩到：报 `Cannot validate argument on parameter 'ArgumentList'`）；`$pid` 是只读自动变量。
# ⚠⚠ **杀树禁用 `$p.Kill($true)`**：该重载在 Windows PowerShell 5.1（.NET Framework）上**不存在**
#   ⇒ 异常被 `try/catch` 吞掉、应用没被杀、宿主因重定向流收尾而**永久挂住**。用下面的 `KillTree`。
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

# ⚠ 运行级隔离（GUID）：固定 `$work` 会被**并发的同名实例**互删 ⇒ 假红（`§6` 第 66 条）。
$val  = "$root/tests/output/validate"
$gid  = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = "$val/webpprobe_${gid}"
New-Item -ItemType Directory -Force -Path $work | Out-Null

$appLimitSec = 60        # 实测每个用例 2~5 s 自行结束；60 s 是给足余量的上限（超了 = 真信号）
$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Host "PASS $msg" -ForegroundColor Green }
  else     { $script:fail++; Write-Host "FAIL $msg" -ForegroundColor Red }
}

# ⚠⚠ **读子进程输出必须带重试**（2026-09-18 实测踩到）：`Start-Process -RedirectStandardOutput` 的文件
#   在**子进程退出瞬间仍可能被占用** ⇒ `[System.IO.File]::ReadAllText` 抛 `IOException`
#   ⇒ `$text` 变成空串 ⇒ 日志形状判据**假红**。本文件实测到的形态：`anim.app.out` 被占用
#   ⇒ `③ anim→png` 误报红，**而同一次运行**的 `③(b)` 两格（同素材、同探测）却全绿 ⇒ **自相矛盾**
#   正是这种假红的指纹。⇒ ① 重试到可读为止；② 仍读不到**不得**静默当空串，必须**响亮**标出（`readOk=false`）。
function ReadTextRetry([string]$path) {
  for ($i = 0; $i -lt 20; $i++) {
    if (-not (Test-Path $path)) { return @{ ok = $true; text = "" } }   # 文件不存在 = 合法的空输出
    try { return @{ ok = $true; text = [System.IO.File]::ReadAllText($path) } }
    catch { Start-Sleep -Milliseconds 200 }
  }
  return @{ ok = $false; text = "" }
}

# ⚠⚠ **`Exec` 必须有界**（2026-09-18 补）：`Start-Process -Wait` 是**无界**的 —— 若 ffprobe/ffmpeg 在某个
#   畸形素材上**不关管道就永不返回**（本仓已实证过这一类：`IsAnimatedJxl` 挂死、`verify-jxl-probe-timeout.ps1`
#   的由来），门禁会**永久挂住**、连汇总行都不打。而本门禁的判别性素材 `oneframe_anim.webp` 是**字节手术**
#   造的（`VP8X + ANIM + 1×ANMF`）⇒ 正是这类风险最高的输入。
#   ⇒ 与 `RunApp` 同款：轮询 + 到点**杀进程树**；`timedOut` 必须作为**响亮**信号回传（**不得**静默当空输出）。
$execLimitSec = 30
function Exec([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $execLimitSec) { Start-Sleep -Milliseconds 150 }
  $timedOut = (-not $p.HasExited)
  if ($timedOut) { KillTree ([int]$p.Id); Start-Sleep -Milliseconds 500 }
  $t  = (ReadTextRetry $o)
  $t2 = (ReadTextRetry $e)
  $code = $null
  if (-not $timedOut) { try { if ($null -ne $p.ExitCode) { $code = $p.ExitCode } } catch { $code = $null } }
  return @{ code = $code; text = ($t.text + "`n" + $t2.text); readOk = ($t.ok -and $t2.ok); timedOut = $timedOut }
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

# ── 单条用例：起应用（webp 输入 → png，必然走「引擎路由判定」那一跳）──
#   ⚠ 用「轮询 + 到点杀树」而不是 `-Wait`：将来它开始挂住时不会把宿主拖死。
#   ⚠ **退出码不进判据**：PS5.1 下 `Start-Process -PassThru`（**不带 `-Wait`**）的 `.ExitCode` **恒 `$null`**，
#     而 `[int]$null == 0` ⇒ 强转会把「读不到」伪装成「exit 0」⇒ **假绿**。判据一律用宿主无关的
#     「产物数 + 日志形状」；需要真退出码的那一条**单独**用 `-Wait -PassThru` 另跑（见 ⑤）。
# ⚠ `$fmt` = **输出容器**（默认 `png`）。③(b) 的核心格必须换成**能装多帧**的容器（`webp`/`gif`）——
#   `png` 是**单帧容器**，取首帧本来就对，**证明不了**「静默丢帧被修好」（team-lead 裁定，2026-09-18）。
function RunApp([string]$inPath, [string]$tag, [string[]]$extra, [string]$fmt = "png") {
  $outdir = "$work/out_$tag"
  $so = "$work/$tag.app.out"; $se = "$work/$tag.app.err"
  $base = @("--headless", "--log-level", "debug", "-i", $inPath, "-o", $outdir, "--format", $fmt)
  $p = Start-Process -FilePath $exe -ArgumentList ($base + $extra) `
       -PassThru -NoNewWindow -RedirectStandardOutput $so -RedirectStandardError $se
  $script:spawned += [int]$p.Id
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $appLimitSec) { Start-Sleep -Milliseconds 300 }
  $sw.Stop()
  $terminated = $p.HasExited
  $code = $null
  if ($terminated) {
    try { if ($null -ne $p.ExitCode) { $code = $p.ExitCode } } catch { $code = $null }
  } else {
    KillTree ([int]$p.Id)
    Start-Sleep -Milliseconds 600
  }
  # ⚠ 必须**重试**读取：子进程刚退出时重定向文件仍可能被占用（见 `ReadTextRetry` 的注释）。
  $ro = (ReadTextRetry $so)
  $re = (ReadTextRetry $se)
  $readOk = ($ro.ok -and $re.ok)
  $text = $ro.text
  if ($text.Length -eq 0) { $text = $re.text }
  $prods = 0
  if (Test-Path $outdir) { $prods = @(Get-ChildItem -LiteralPath $outdir -File -Recurse -ErrorAction SilentlyContinue).Count }
  return @{ terminated = $terminated; code = $code; elapsed = $sw.Elapsed.TotalSeconds; text = $text; prods = $prods; readOk = $readOk }
}

# ── RIFF/WEBP 块工具（**只用于构造判别性素材**：1 帧动画容器）──────────────────────
function Get-RiffChunkList([byte[]]$b) {
  $list = New-Object System.Collections.Generic.List[object]
  $p = 12
  while ($p + 8 -le $b.Length) {
    $fc = [System.Text.Encoding]::ASCII.GetString($b, $p, 4)
    $sz = [System.BitConverter]::ToUInt32($b, $p + 4)
    $tot = 8 + $sz + ($sz % 2)
    $list.Add(@{ FourCC = $fc; Offset = $p; Total = $tot })
    $p += $tot
  }
  return $list
}
function Slice-Chunk([byte[]]$b, $chunk) {
  $r = New-Object byte[] $chunk.Total
  [Array]::Copy($b, $chunk.Offset, $r, 0, $chunk.Total)
  return $r
}
function Join-Two([byte[]]$a, [byte[]]$b) {
  $r = New-Object byte[] ($a.Length + $b.Length)
  [Array]::Copy($a, 0, $r, 0, $a.Length)
  [Array]::Copy($b, 0, $r, $a.Length, $b.Length)
  return $r
}
function Build-Riff([byte[]]$body) {
  $fs = 4 + $body.Length
  $r = New-Object byte[] (8 + $fs)
  [Array]::Copy([System.Text.Encoding]::ASCII.GetBytes('RIFF'), 0, $r, 0, 4)
  [Array]::Copy([System.BitConverter]::GetBytes([uint32]$fs), 0, $r, 4, 4)
  [Array]::Copy([System.Text.Encoding]::ASCII.GetBytes('WEBP'), 0, $r, 8, 4)
  [Array]::Copy($body, 0, $r, 12, $body.Length)
  return $r
}

# 取 `-of default=nw=1` 输出里某个 key 的值（取不到 ⇒ $null）。
function Get-EntryValue([string]$text, [string]$key) {
  foreach ($ln in ($text -split "`r?`n")) {
    $eq = $ln.IndexOf('=')
    if ($eq -lt 0) { continue }
    if ($ln.Substring(0, $eq).Trim().ToLowerInvariant() -ne $key.ToLowerInvariant()) { continue }
    return $ln.Substring($eq + 1).Trim()
  }
  return $null
}
function Probe-FormatName([string]$path, [string]$tag) {
  $r = Exec $fp "-v error -show_entries format=format_name -of default=nw=1 `"$path`"" $tag
  return @{ code = $r.code; value = (Get-EntryValue $r.text 'format_name'); timedOut = $r.timedOut; readOk = $r.readOk }
}
function Probe-Packets([string]$path, [string]$tag) {
  $r = Exec $fp "-v error -count_packets -show_entries stream=nb_read_packets -of default=nw=1 `"$path`"" $tag
  return @{ code = $r.code; value = (Get-EntryValue $r.text 'nb_read_packets'); timedOut = $r.timedOut; readOk = $r.readOk }
}
function Probe-Frames([string]$path, [string]$tag) {
  $r = Exec $fp "-v error -count_frames -show_entries stream=nb_read_frames -of default=nw=1 `"$path`"" $tag
  return @{ code = $r.code; value = (Get-EntryValue $r.text 'nb_read_frames'); timedOut = $r.timedOut; readOk = $r.readOk }
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

# ── ① 素材自备 + **前提断言**（第一验收点）────────────────────────────────────────
# ⚠ 素材配方**必须**：多帧用 `-c:v libwebp`（实测逐 req 精确）；**禁止** `libwebp_anim` + 静态源
#   （实测它会把与前一帧相同的帧**折叠掉** ⇒ 请求 2 帧实际只有 1 帧 ⇒ "多帧"用例静默退化成"静态"用例）。
# ⚠ 而"不信任 `-frames:v`"这条**不能只写在注释里**：下面每条都断言**实际**包数/帧数。
# ⚠⚠ **必须显式钉死质量参数 `-q:v 75`**（跨 build 可复现性，`gp-3` 实测，2026-09-18）：
#   同一配方 `-frames:v 100` + `libwebp_anim`，**不加** `-q:v` 时两个 ffmpeg build 落到的帧数
#   差 3.3×（build A = 28 帧 / 44 814 B，build B = 93 帧 / 145 352 B）；**加上** `-q:v 75` 后
#   两者逐字节相同（252 056 B / 100 帧 / 同 sha）。根因：编码器会**折叠量化后相同的连续帧**，
#   而"默认质量"随 build 变 ⇒ 折叠掉多少帧也随 build 变。
#   ⇒ 不钉质量的话，同一门禁在不同机器上造出的**素材前提**不同：轻则覆盖悄悄变化，重则
#     断言在另一台上红/绿不一致（另一种真空绿）。实测 `libwebp` 接受 `-q:v 75`（同源 8 帧素材
#     2274 B → 7938 B，确认参数真的生效），且 `libwebp_anim` 也接受。
Write-Host "`n### ① 自备 webp 素材 + 前提断言（实际帧数 + format_name 都先断言）###" -ForegroundColor Cyan
$still  = "$work/still.webp"
$anim   = "$work/anim.webp"
$one    = "$work/oneframe_anim.webp"
$negmat = "$work/static_libwebp_anim.webp"

Exec $ff "-y -v error -f lavfi -i `"testsrc2=s=64x48`" -frames:v 1 -q:v 75 -c:v libwebp `"$still`"" "gen_still" | Out-Null
Exec $ff "-y -v error -f lavfi -i `"testsrc2=s=64x48:rate=10`" -frames:v 8 -q:v 75 -c:v libwebp `"$anim`"" "gen_anim" | Out-Null
# 负控材料：`libwebp_anim` + **静态源** + 请求 2 帧 ⇒ 实测被折叠成 1 帧、且**不是** `webp_anim`。
Exec $ff "-y -v error -f lavfi -i `"color=c=red:s=64x48`" -frames:v 2 -q:v 75 -c:v libwebp_anim `"$negmat`"" "gen_negmat" | Out-Null

# 判别性素材：把 `anim.webp` **字节级**裁成 `VP8X + ANIM + 1×ANMF` ⇒ `webp_anim` 但只有 1 帧。
# ⚠ 这正是"单用 `format_name` 会误判成多帧"的那一格；合取判据必须把它判成 **Single**。
$oneBuilt = $false
if ((Test-Path $anim) -and ((Get-Item $anim).Length -gt 64)) {
  $b = [System.IO.File]::ReadAllBytes($anim)
  $chunks = Get-RiffChunkList $b
  $vp8x = $null; $animc = $null; $firstAnmf = $null
  foreach ($c in $chunks) {
    if ($c.FourCC -eq 'VP8X' -and $null -eq $vp8x) { $vp8x = $c }
    elseif ($c.FourCC -eq 'ANIM' -and $null -eq $animc) { $animc = $c }
    elseif ($c.FourCC -eq 'ANMF' -and $null -eq $firstAnmf) { $firstAnmf = $c }
  }
  if ($null -ne $vp8x -and $null -ne $animc -and $null -ne $firstAnmf) {
    $body = Join-Two (Slice-Chunk $b $vp8x) (Join-Two (Slice-Chunk $b $animc) (Slice-Chunk $b $firstAnmf))
    [System.IO.File]::WriteAllBytes($one, (Build-Riff $body))
    $oneBuilt = $true
  }
}
Write-Host "  构造 oneframe_anim.webp: $oneBuilt"

$pStill  = Probe-FormatName $still  "pf_still"
$pAnim   = Probe-FormatName $anim   "pf_anim"
$pOne    = Probe-FormatName $one    "pf_one"
$pNegMat = Probe-FormatName $negmat "pf_negmat"
$kStill  = Probe-Packets $still  "pk_still"
$kAnim   = Probe-Packets $anim   "pk_anim"
$kOne    = Probe-Packets $one    "pk_one"
$kNegMat = Probe-Packets $negmat "pk_negmat"
$fStill  = Probe-Frames $still  "fr_still"
$fAnim   = Probe-Frames $anim   "fr_anim"
$fOne    = Probe-Frames $one    "fr_one"
$fNegMat = Probe-Frames $negmat "fr_negmat"

# ⚠ **有界 `Exec` 的响亮出口**（见 `Exec` 的注释）：任何一个探测超时都必须**点名**，
#   不得静默当空输出（那会让下面的素材前提断言以「读不到值」的形式**假红**，掩盖真因）。
$probeTimeouts = @()
foreach ($pr in @(@('pf_still',$pStill),@('pf_anim',$pAnim),@('pf_one',$pOne),@('pf_negmat',$pNegMat),
                  @('pk_still',$kStill),@('pk_anim',$kAnim),@('pk_one',$kOne),@('pk_negmat',$kNegMat),
                  @('fr_still',$fStill),@('fr_anim',$fAnim),@('fr_one',$fOne),@('fr_negmat',$fNegMat))) {
  if ($pr[1].timedOut) { $probeTimeouts += $pr[0] }
  if (-not $pr[1].readOk) { $probeTimeouts += ($pr[0] + '(读失败)') }
}
CK ($probeTimeouts.Count -eq 0) `
   "① 前置：12 个 ffprobe 探测**都没有超时、输出都读到了**（问题项：$(if ($probeTimeouts.Count -eq 0) { '无' } else { $probeTimeouts -join '、' })；单次上限 $execLimitSec s）"

function SizeOf([string]$p) { if (Test-Path $p) { return (Get-Item $p).Length } else { return 0 } }
function IntOf($v) { $n = 0; if ([int]::TryParse("$v", [ref]$n)) { return $n } else { return -1 } }
$nStill = IntOf $kStill.value; $nAnim = IntOf $kAnim.value; $nOne = IntOf $kOne.value; $nNegMat = IntOf $kNegMat.value
$gStill = IntOf $fStill.value; $gAnim = IntOf $fAnim.value; $gOne = IntOf $fOne.value; $gNegMat = IntOf $fNegMat.value

Write-Host "  [素材] still.webp          = $(SizeOf $still) B  fmt=$($pStill.value)  packets=$($kStill.value)  frames=$($fStill.value)"
Write-Host "  [素材] anim.webp           = $(SizeOf $anim) B  fmt=$($pAnim.value)  packets=$($kAnim.value)  frames=$($fAnim.value)"
Write-Host "  [素材] oneframe_anim.webp  = $(SizeOf $one) B  fmt=$($pOne.value)  packets=$($kOne.value)  frames=$($fOne.value)"
Write-Host "  [素材] static_libwebp_anim = $(SizeOf $negmat) B  fmt=$($pNegMat.value)  packets=$($kNegMat.value)  frames=$($fNegMat.value)"

# ⚠⚠ **这里刻意不再断言 `code -eq 0`**（2026-09-18）：`Exec` 改成**有界**后就不能带 `-Wait`，而
#   **PS 5.1 下 `Start-Process -PassThru`（不带 `-Wait`）的 `.ExitCode` 恒 `$null`**（本仓已实测登记）
#   ⇒ 写 `code -eq 0` 会**恒假** ⇒ 4 条**假红**（本轮实测踩到）。等价且宿主无关的信号是
#   **「解析出了值」+「没超时」+「输出读到了」**：ffprobe 非 0 退出时 `-v error` 不产 `key=value` 行
#   ⇒ `value` 为 `$null` ⇒ 断言照样有牙。真需要退出码的地方（应用）**单独**用 `-Wait -PassThru` 另跑（见 ⑤）。
CK ((SizeOf $still) -gt 64 -and $pStill.value -eq 'webp_pipe' -and $nStill -eq 1) `
   "① still.webp 是**合法静态 webp**（fmt=$($pStill.value)、packets=$nStill、frames=$gStill）⇒ 它必须被判「Single」"
CK ((SizeOf $anim) -gt 64 -and $pAnim.value -eq 'webp_anim' -and $nAnim -eq 8) `
   "① anim.webp 是**真多帧**（fmt=$($pAnim.value)、packets=$nAnim、frames=$gAnim；**实际**包数 == 8，不是「我们请求了 8」）"
CK ($oneBuilt -and (SizeOf $one) -gt 64 -and $pOne.value -eq 'webp_anim' -and $nOne -eq 1) `
   "① oneframe_anim.webp 是**1 帧动画容器**（fmt=$($pOne.value)、packets=$nOne、frames=$gOne）—— 合取判据的**判别性**素材：只看「format_name」会把它误判成多帧"
CK ((SizeOf $negmat) -gt 32 -and $pNegMat.value -eq 'webp_pipe' -and $nNegMat -eq 1) `
   "① static_libwebp_anim.webp（libwebp_anim + 静态源 + 请求 2 帧）实测是 **$($pNegMat.value) / $nNegMat 帧** ⇒ 证明「禁 libwebp_anim + 静态源」这条配方纪律的**理由成立**（它造不出多帧）"
CK (($nStill -gt 1) -eq ($gStill -gt 1) -and ($nAnim -gt 1) -eq ($gAnim -gt 1) -and ($nOne -gt 1) -eq ($gOne -gt 1) -and ($nNegMat -gt 1) -eq ($gNegMat -gt 1)) `
   "① **交叉断言（布尔）**：四个素材都满足「(packets > 1) == (frames > 1)」（still $nStill/$gStill、anim $nAnim/$gAnim、one $nOne/$gOne、negmat $nNegMat/$gNegMat）—— 不写比值（实测比值是**材料属性**，1×~35.7×）"

# ── ② 源码形态自检 ──────────────────────────────────────────────────────────────
Write-Host "`n### ② 源码形态自检 ###" -ForegroundColor Cyan
$qpSrc = "$root/src/FfmpegGui/Services/QueueProcessor.cs"
$srcTxt = ""
if (Test-Path $qpSrc) { $srcTxt = [System.IO.File]::ReadAllText($qpSrc) }
# 去掉**整行**注释后再断言（方法的注释里正逐字引用旧写法/旧承诺）。
$srcCodeLines = @()
foreach ($ln in ($srcTxt -split "`r?`n")) { if ($ln -notmatch '^\s*//') { $srcCodeLines += $ln } }
$srcCode = $srcCodeLines -join "`n"

$tmoOk = ($srcCode -match 'DefaultWebpProbeTimeoutSec\s*=\s*20\b') -and
         ($srcCode -match 'FFMPEGGUI_WEBP_PROBE_TIMEOUT_SEC') -and
         ($srcCode -match 'Math\.Clamp\(sec,\s*1,\s*600\)')
CK ($tmoOk) `
   "② 源码形态（超时预算）：「DefaultWebpProbeTimeoutSec = 20」（与 DefaultAvifProbeTimeoutSec 同口径）、注入通道读「FFMPEGGUI_WEBP_PROBE_TIMEOUT_SEC」、且夹紧到 [1, 600]"

# ⚠⚠ **载体变更须知**：下面是**逐字签名锚点**。将来若把三态换成枚举/结构体或改方法名，`$wi` 会变 `-1`
#   ⇒ `$webpBody = ""` ⇒ 下面那条 `$conjOk` 会**转红**。⚠ 那是**真信号**（判据载体失效），不是假红：
#   必须**同步重取锚点**，**不得**把它放宽成"只要方法里出现 `format_name` 就算过"。
$wi = $srcCode.IndexOf('private static async Task<(bool animated, bool determinate)> IsAnimatedWebpAsync(')
$webpBody = ""
if ($wi -ge 0) {
  # ⚠ 必须**只取本方法体**（大括号配对）：`Substring($wi)` 取到文件尾会把后面 `RunFfprobeWithWatchdogAsync` /
  #   `ParseMaxNbReadPackets` 的内容也算进来 ⇒ 「禁 `-count_frames`」之类的**否定式**断言会**假红**。
  $ob = $srcCode.IndexOf('{', $wi)
  if ($ob -ge 0) {
    $depth = 0; $end = -1
    for ($i = $ob; $i -lt $srcCode.Length; $i++) {
      $ch = $srcCode[$i]
      if ($ch -eq '{') { $depth++ }
      elseif ($ch -eq '}') { $depth--; if ($depth -eq 0) { $end = $i; break } }
    }
    if ($end -gt $ob) { $webpBody = $srcCode.Substring($wi, $end - $wi + 1) }
  }
}
$conjOk = ($webpBody.Length -gt 0) -and
          ($webpBody -match 'ResolveWebpProbeTimeoutSec\(\)') -and
          ($webpBody -match 'format=format_name') -and
          ($webpBody -match '\-count_packets') -and
          ($webpBody -match 'nb_read_packets') -and
          ($webpBody -match 'ParseFormatName\(') -and
          ($webpBody -match 'ParseMaxNbReadPackets\(')
CK ($conjOk) `
   "② 源码形态（合取判据）：IsAnimatedWebpAsync 体内**两步都真的在**（format=format_name 与 -count_packets / nb_read_packets 都在），且用 ResolveWebpProbeTimeoutSec() + 两个解析器 —— 单步（只看容器名）会漏「1 帧动画容器」"

# ⚠⚠ **`v:0` 禁令的准确边界**（2026-09-18 更正；team-lead 已裁定此前把「禁 `v:0`」与「禁
#   `-select_streams`」**混成一条**是错的，且他本人已撤回那条错指令）：
#   **禁的是 `v:0`，不是 `-select_streams`。** 正确形态 = `-select_streams v`（**滤掉音频**）
#   **+ 取 max**（**不得求和**）。理由（实测）：mp4「1 视频帧 + 音频」的全流 packets 是 `1 + 45`
#   ⇒ 音频贡献 **45** ⇒ 「全流取 max」会把**单帧**文件判成 **Multiple（假 Multiple）**，求和更离谱。
#   ⚠ 本实现（webp 轴）**没有** `-select_streams` —— 对 `.webp`（**只有 1 条视频流、无音频**）
#     三种写法**同值** ⇒ **实现不必改**；但**规则必须写对**，否则将来复用到别的格式会假 Multiple。
#   ⇒ 断言只禁 `v:0`，**允许** `-select_streams v`（将来若加上它**不得**被判红 —— 那是**假红**）。
$v0Ban = ($webpBody.Length -gt 0) -and
         ($webpBody -notmatch 'v\s*:\s*0') -and
         ($webpBody -notmatch '\-count_frames')
CK ($v0Ban) `
   "② 源码形态（禁 v:0 / 零解码）：探测体内**无** v:0（对多帧容器恒读主图 item ⇒ 多帧被读成 1 帧；**允许** -select_streams v —— 滤掉音频才是正确形态）且**无** -count_frames（要真解码，代价无可靠上界）"

# ⚠ **新锁必须有牙且不得过宽**（2026-09-18 新增）：上面那条从「禁 -select_streams」收窄成「禁 v:0」，
#   属于**放宽** ⇒ 必须同时证明 ① 放宽后**仍有牙**（含 v:0 的形态照样判红）② **不过宽**（正确形态
#   `-select_streams v` 不得判红，否则将来正确的实现会**假红**）。
$okBody  = 'ffprobe -v error -select_streams v -count_packets -show_entries stream=nb_read_packets -of csv=p=0 x.webp'
$badBody = 'ffprobe -v error -select_streams v:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 x.webp'
CK ((-not ($okBody -match 'v\s*:\s*0')) -and (-not ($okBody -match '\-count_frames'))) `
   "② 负控（新锁**不过宽**）：含 -select_streams v 的**正确形态**不被判红 —— 否则将来正确实现会**假红**"
CK (($badBody -match 'v\s*:\s*0')) `
   "② 反控（新锁**有牙**）：含 v:0 的假体照样被判红 —— 放宽后锁没被放空"

$rg = $srcCode.IndexOf('private static async Task<(string stdout, string? failReason)> RunFfprobeWithWatchdogAsync(')
$wdBody = ""
if ($rg -ge 0) {
  $ob2 = $srcCode.IndexOf('{', $rg)
  if ($ob2 -ge 0) {
    $d2 = 0; $e2 = -1
    for ($i = $ob2; $i -lt $srcCode.Length; $i++) {
      $ch = $srcCode[$i]
      if ($ch -eq '{') { $d2++ }
      elseif ($ch -eq '}') { $d2--; if ($d2 -eq 0) { $e2 = $i; break } }
    }
    if ($e2 -gt $ob2) { $wdBody = $srcCode.Substring($rg, $e2 - $rg + 1) }
  }
}
$wdOk = ($wdBody.Length -gt 0) -and
        ($wdBody -match 'CancelAfter\(TimeSpan\.FromSeconds\(timeoutSec\)\)') -and
        ($wdBody -match '\.Token\.Register\(') -and
        ($wdBody -match 'Kill\(entireProcessTree: true\)') -and
        ($wdBody -match 'WaitForExitAsync\(\)') -and
        ($wdBody -notmatch '\.WaitForExit\(') -and
        ($wdBody -match 'ReadStdoutDrainingStderrAsync\(')
CK ($wdOk) `
   "② 源码形态（看门狗）：RunFfprobeWithWatchdogAsync 用 CancelAfter + Token.Register( + Kill(entireProcessTree: true)；**先挂异步读再等退出**（有 WaitForExitAsync()、**无**同步 .WaitForExit(）—— 同步读写在等待之前会让超时变成**死代码**"

# ⚠⚠ **精确计数断言**（两支统一 + 每项只求值一次）：
#   · 旧同步形态 `InputMayHaveMultipleFrames(` 作为**调用**必须**一处不剩**（它是"输出非 gif/apng 就跳过输入探测"的那条丢项路径）。
#   · `InputMayHaveMultipleFramesAsync(` 必须**恰好 2 处**：1 处**定义** + 1 处**调用**（单点求值，供三处消费）。
#   ⚠ 不得放宽成 `-ge 1` —— 放宽后「每项只求值一次」就抓不到了（多个调用点 = 同一项被探测多次 = 三处口径可能漂移）。
$oldSync = ([regex]::Matches($srcCode, [regex]::Escape('InputMayHaveMultipleFrames('))).Count
$newAsync = ([regex]::Matches($srcCode, [regex]::Escape('InputMayHaveMultipleFramesAsync('))).Count
CK ($oldSync -eq 0 -and $newAsync -eq 2) `
   "② 源码形态（两支统一 + 单点求值）：旧同步形态「InputMayHaveMultipleFrames(」**0 处**（实 $oldSync）、新形态「InputMayHaveMultipleFramesAsync(」**恰好 2 处**（1 定义 + 1 调用，实 $newAsync）—— 三处消费共用同一份判据"

$webpConsume = ([regex]::Matches($srcCode, [regex]::Escape('|| !webpProbeDeterminate'))).Count
CK ($webpConsume -ge 1) `
   "② 源码形态（三态消费）：webp 侧**有**调用点写了「|| !webpProbeDeterminate」（实 $webpConsume 处）—— 不确定 ⇒ 保守按多帧，绝不静默当静态"

# ── ② 回归锁（2026-09-18 新增）：**输入侧判据不得看输出容器** ────────────────────────
# ⚠⚠ 由来：这是一条**真回归**，且**只有整轮 42 条**能抓到（单跑任何一条单元门禁都全绿）。
#   原形 `if (fmt is not ("gif" or "apng")) return IsAnimated(item);` 是**分支选择器**（决定走哪一支），
#   **不是**「目标能装动画 ⇒ 判多帧」的判据。在「两支统一」时被误译成
#   `if (fmt is "gif" or "apng") return true;` ⇒ **任何 gif/apng 输出**都被判多帧 ⇒ 路由命中
#   `AnimatedInput` ⇒ 引擎被挡 ⇒ gif/apng 出口退回 legacy ⇒ 引擎 gif 出口的**调色板链 + alpha 合成**
#   全丢（**静默降质**，实测 32.33 vs 44.33 dB）。
#   ⚠ 函数名就是答案：`InputMayHaveMultipleFrames` 问的是**输入**，与输出格式**无关**。
#   ⇒ 本锁把「只有整轮能抓到」变成「单跑本门禁就能抓到」（源码形态、0 秒、不需要 exe）。
#   ⚠ 行为侧的正面锁**已存在**于 `verify-color-strategy.ps1` 的 `map-gif-*` 三格（静态 P3 PNG → gif
#     强制 Map ⇒ 引擎必须真执行 + 命令行含调色板链/alpha 合成）；本锁补的是**廉价版**，不是替代它。
# ⚠ 载体：**逐字签名锚点**。将来改名/改签名 ⇒ `$mfBody = ""` ⇒ 本锁**转红**（真信号，须同步重取锚点）；
#   **不得**放宽成"取到文件尾"（那会把后面 `IsAnimated` 里**合法**的轴 A 判据算进来 ⇒ 恒红）。
function InputJudgeIgnoresFormat([string]$body) {
  return (($body.Length -gt 0) -and
          ($body -notmatch 'Options\.Format') -and
          ($body -notmatch '\bfmt\b'))
}
$mfAnchor = 'private static async Task<bool> InputMayHaveMultipleFramesAsync('
$mi = $srcCode.IndexOf($mfAnchor)
$mfBody = ""
if ($mi -ge 0) {
  $ob3 = $srcCode.IndexOf('{', $mi)
  if ($ob3 -ge 0) {
    $d3 = 0; $e3 = -1
    for ($i = $ob3; $i -lt $srcCode.Length; $i++) {
      $ch = $srcCode[$i]
      if ($ch -eq '{') { $d3++ }
      elseif ($ch -eq '}') { $d3--; if ($d3 -eq 0) { $e3 = $i; break } }
    }
    if ($e3 -gt $ob3) { $mfBody = $srcCode.Substring($mi, $e3 - $mi + 1) }
  }
}
CK (InputJudgeIgnoresFormat $mfBody) `
   "② 回归锁（输入侧判据不得看输出容器）：InputMayHaveMultipleFramesAsync 体内**无** Options.Format / fmt —— 它是**输入**判据；把 gif/apng 折进来会让所有 gif/apng 输出被判多帧 ⇒ 引擎被挡 ⇒ 静默降质"
# 负控（**载体失效必须红**，不得静默变绿）：空体**不得**通过 —— 否则方法改名后本锁会假绿。
CK (-not (InputJudgeIgnoresFormat '')) `
   "② 负控（回归锁的载体失效必须红）：空体不通过 ⇒ 方法被改名/改签名时本锁**响亮转红**，不会静默假绿"
# 反控（**有牙**）：把「轴 A」短路加回**内存副本**，本锁必须抓到 —— **不动真实文件**（同本文件既有的内存内自变异做法）。
$mfMutant = $mfBody + "`n" + 'var fmt = item.Options.Format; if (fmt is "gif" or "apng") return true;'
CK (-not (InputJudgeIgnoresFormat $mfMutant)) `
   "② 反控（回归锁**有牙**）：把「轴 A」短路加回内存副本后立刻被判红 ⇒ 本锁不是恒真"

# ── ② 收尾轮回归锁（2026-09-18，`#136` 收尾）：`IsAnimated` 输入侧补 `.webp` + 预缩放点改判据 ──────
# ⚠⚠ 由来：这两处改动（A1 / 丙）**各自都没有行为门禁覆盖** ⇒ 用**源码形态锁**兜住（0 秒、不需要 exe）。
#   · **A1**：`IsAnimated` 的**输入侧**补 `.webp`（读 `AnimatedWebpCache`）。
#     ⚠ 两个方向**都要抓**：只抓「有没有 webp」会让 B2 同款误译 —— 把 `.webp` 写进
#     `fmt is "gif" or "apng"`（= **输出容器**轴）—— **照样通过**。故必须同时断言那一行**不含** webp。
#   · **丙**：预缩放跳过判定由 `IsAnimated(captured)` 改为消费 `ProcessAsync` 单点求值的
#     `inputMayHaveMultipleFrames`（预缩放要跳过的**只是**「输入有多帧」这一种情形）。
# ⚠ 载体同样是**逐字锚点 + 大括号配对抽体**（照上面 `$mfBody` 的范式）；改名/改签名 ⇒ 空体 ⇒ 响亮转红。
function IsAnimatedInputSideOk([string]$body) {
  if ($body.Length -eq 0) { return $false }
  $hasInputWebp = ($body -match 'inputExt\s*==\s*"\.webp"')
  $readsCache   = ($body -match 'AnimatedWebpCache\.TryGetValue\(')
  $fmtExpr      = [regex]::Match($body, 'fmt\s+is\s+[^;]+').Value
  $fmtLineClean = (($fmtExpr.Length -gt 0) -and ($fmtExpr -notmatch 'webp'))
  return ($hasInputWebp -and $readsCache -and $fmtLineClean)
}
$iaAnchor = 'private static bool IsAnimated(QueueItem item)'
$ii = $srcCode.IndexOf($iaAnchor)
$iaBody = ""
if ($ii -ge 0) {
  $ob4 = $srcCode.IndexOf('{', $ii)
  if ($ob4 -ge 0) {
    $d4 = 0; $e4 = -1
    for ($i = $ob4; $i -lt $srcCode.Length; $i++) {
      $ch = $srcCode[$i]
      if ($ch -eq '{') { $d4++ }
      elseif ($ch -eq '}') { $d4--; if ($d4 -eq 0) { $e4 = $i; break } }
    }
    if ($e4 -gt $ob4) { $iaBody = $srcCode.Substring($ii, $e4 - $ii + 1) }
  }
}
CK (IsAnimatedInputSideOk $iaBody) `
   "② 收尾轮锁 A1：IsAnimated 的**输入侧**确实补了 .webp（inputExt == \".webp\"）**且**读 AnimatedWebpCache，**且** fmt is 那一行**不含** webp —— 把输入侧折进输出容器轴正是 B2 真回归的形态指纹"
CK (-not (IsAnimatedInputSideOk '')) `
   "② 收尾轮负控 A1（载体失效必须红）：空体不通过 ⇒ IsAnimated 被改名/改签名时本锁**响亮转红**，不会静默假绿"
# 反控（**有牙**，两个方向各一条，**不动真实文件**）：
$iaMutantMove = $iaBody -replace 'if\s*\(inputExt == "\.webp"[^\r\n]*', 'if (fmt is "gif" or "apng" or "webp") return true;'
CK (-not (IsAnimatedInputSideOk $iaMutantMove)) `
   "② 收尾轮反控 A1（把 .webp **挪进** fmt 行 ⇒ 内存副本立刻判红）—— 证明「输入侧 vs 输出容器」这条区分有牙"
$iaMutantFold = $iaBody -replace 'fmt\s+is\s+"gif"\s+or\s+"apng"', 'fmt is "gif" or "apng" or "webp"'
CK (-not (IsAnimatedInputSideOk $iaMutantFold)) `
   "② 收尾轮反控 A1b（**只**把 fmt 行扩成 gif/apng/webp、输入侧原样保留 ⇒ 也必须判红）—— 证明输出容器轴这一条**单独**有牙，不是被输入侧掩盖"
function PrescaleConsumesSharedJudge([string]$code) {
  return (($code -match 'var\s+maxDimSkipAnimated\s*=\s*inputMayHaveMultipleFrames\s*;') -and
          (-not ($code -match 'var\s+maxDimSkipAnimated\s*=\s*IsAnimated\(')))
}
CK (PrescaleConsumesSharedJudge $srcCode) `
   "② 收尾轮锁 丙：预缩放跳过判定消费**同一个** inputMayHaveMultipleFrames（var maxDimSkipAnimated = inputMayHaveMultipleFrames;），且**已无** var maxDimSkipAnimated = IsAnimated( —— 预缩放要跳过的只是「输入有多帧」，不是「输出容器能装动画」"
$psMutant = $srcCode -replace 'var\s+maxDimSkipAnimated\s*=\s*inputMayHaveMultipleFrames\s*;', 'var maxDimSkipAnimated = IsAnimated(captured);'
CK (-not (PrescaleConsumesSharedJudge $psMutant)) `
   "② 收尾轮反控 丙（内存副本把该行改回 IsAnimated(captured) ⇒ 立刻判红）—— 证明本锁不是恒真"

# ── ③ 行为（**端到端、双向**）────────────────────────────────────────────────────
# ⚠ 用 `--color-space "Display P3"` 而不是裸跑：实测裸跑时计划是 `None`（直通）⇒ 引擎**不执行**像素变换
#   ⇒ 只会打 `计划为直通`。加上它之后计划变 `Map` ⇒ 引擎**真执行** ⇒ 出现 `[色彩引擎] ✅`。
#   ⇒ 这样"走引擎"的证据是**引擎真跑了**，不是"引擎被问了一句"。
Write-Host "`n### ③ 行为：端到端（走引擎 vs 命中 AnimatedInput 落 legacy）###" -ForegroundColor Cyan
$P3 = @("--color-space", "`"Display P3`"")
$rStill  = RunApp $still  "still"  $P3
$rAnim   = RunApp $anim   "anim"   $P3
$rOne    = RunApp $one    "one"    $P3
$rNegMat = RunApp $negmat "negmat" $P3

foreach ($pair in @(@('still', $rStill), @('anim', $rAnim), @('oneframe', $rOne), @('negmat', $rNegMat))) {
  $nm = $pair[0]; $r = $pair[1]
  Write-Host ("  [{0}] 结束={1} exit={2} 耗时={3:N1}s 产物={4}" -f $nm, $r.terminated, $r.code, $r.elapsed, $r.prods)
  foreach ($l in @(($r.text -split "`r?`n") | Where-Object { $_ -match '色彩引擎|色彩裁决' })) {
    Write-Host "      $($l.Trim())" -ForegroundColor DarkGray
  }
}

# ⚠⚠ **读失败必须响亮**（不得静默当空串）：见 `ReadTextRetry`。把它变成一条**点名**断言，
#   否则「重定向文件被占用」会伪装成「没有输出」⇒ 下面几条日志形状判据**假红**（本文件实测踩到过）。
CK ($rStill.readOk -and $rAnim.readOk -and $rOne.readOk -and $rNegMat.readOk) `
   "③ 前置：四个用例的子进程输出**都读到了**（读不到会由本断言点名，而不是让下面几条假红）"

function RanEngine($r) {
  return (($r.text -match '\[色彩引擎\] ✅') -and ($r.text -notmatch '不适用引擎（AnimatedInput'))
}
function BlockedAnimated($r) {
  return ($r.text -match '本次不适用引擎（AnimatedInput：') -and ($r.text -notmatch '\[色彩引擎\] ✅')
}

CK ($rStill.terminated -and ($rStill.prods -ge 1) -and (RanEngine $rStill)) `
   "③ still.webp（静态）→ png：**走引擎且引擎真执行**（出现「[色彩引擎] ✅」；实 结束=$($rStill.terminated) 产物=$($rStill.prods)）"
CK ($rAnim.terminated -and ($rAnim.prods -ge 1) -and (BlockedAnimated $rAnim)) `
   "③ anim.webp（8 帧）→ png：**命中 AnimatedInput 落 legacy**（出现「本次不适用引擎（AnimatedInput：输入为动画，引擎按单帧处理会丢帧）」、且**无**「[色彩引擎] ✅」）—— 这正是修复前**没有**发生的事（它以前会进引擎 ⇒ 丢帧）"
CK ($rOne.terminated -and ($rOne.prods -ge 1) -and (RanEngine $rOne)) `
   "③ oneframe_anim.webp（**1 帧**动画容器）→ png：**走引擎**（「[色彩引擎] ✅」）← 合取判据的**行为证据**：只看 format_name 的实现会在这里报 AnimatedInput（假阳性 ⇒ 白白挡掉一个静态任务）"
CK ($rNegMat.terminated -and ($rNegMat.prods -ge 1) -and (RanEngine $rNegMat)) `
   "③ static_libwebp_anim.webp（负控材料，实测 webp_pipe / 1 帧）→ png：**走引擎**（「[色彩引擎] ✅」）—— 负控方向：它**不是**动画，不得被拦"

# ── ③(b) **核心格**：动画 → **能装多帧的目标** ⇒ 产物帧数必须保住 ─────────────────────
# ⚠⚠ **为什么必须有这一格**（team-lead 裁定，2026-09-18）：上面 4 格**全是 `--format png`**，而
#   **PNG 是单帧容器 ⇒ 取首帧本来就对** ⇒ 那 4 格**证明不了**「静默丢帧被修好」。
#   ⇒ 必须测**能装多帧**的目标（webp/gif）：修复前引擎按单帧处理 ⇒ **产物只剩 1 帧**。
# ⚠ 判据取**精确相等**（不是 `> 1`）—— 已实测（2026-09-18，本机，仓库自带 ffmpeg）：输入 8 帧
#   （`-q:v 75`、7938 B）⇒ `--format webp` 产物 `webp_anim` **8/8**、`--format gif` 产物 `gif` **8/8**，
#   两格都**不折叠**（`testsrc2` 逐帧变化 ⇒ 输出侧 `libwebp_anim` 没有重复帧可折叠）⇒ `== 8` 不脆。
Write-Host "`n### ③(b) 核心格：动画 → 多帧容器（webp/gif）⇒ 产物帧数必须保住 ###" -ForegroundColor Cyan
# ⚠ **两格刻意不对称**：
#   · webp 格**带 `--color-space`**（计划变 `Map` ⇒ 引擎**真的会被问**）—— 这是**强格**：若输入探测
#     失效 ⇒ 引擎接手 ⇒ **产物只剩 1 帧**，本格立刻转红。
#   · gif 格**不带** `--color-space` —— gif **既不能携带 ICC 又不能表达 CICP** ⇒ 带它会被
#     `色彩裁决` 以 `Reject` 拒绝（**实测**：`❌ 契约拒绝（不出产物）`，产物 0）⇒ 那是**正确的**应用
#     行为、与 B2 无关，带上只会污染判据。裸重编码即可验证「多帧目标保帧」。
$rAnimWebp = RunApp $anim "anim2webp" $P3 "webp"
$rAnimGif  = RunApp $anim "anim2gif"  @()  "gif"
foreach ($pair in @(@('anim→webp', $rAnimWebp), @('anim→gif', $rAnimGif))) {
  $nm = $pair[0]; $r = $pair[1]
  Write-Host ("  [{0}] 结束={1} exit={2} 耗时={3:N1}s 产物={4}" -f $nm, $r.terminated, $r.code, $r.elapsed, $r.prods)
  foreach ($l in @(($r.text -split "`r?`n") | Where-Object { $_ -match '色彩引擎|色彩裁决' })) {
    Write-Host "      $($l.Trim())" -ForegroundColor DarkGray
  }
}
# ⚠ 取产物路径**只走运行级 GUID 目录**（`$work/out_anim2webp`），**不按模式找**（与清理纪律同理）。
function FirstProd([string]$dir) {
  if (-not (Test-Path $dir)) { return "" }
  $f = @(Get-ChildItem -LiteralPath $dir -File -Recurse -ErrorAction SilentlyContinue)
  if ($f.Count -eq 0) { return "" }
  return ($f[0].FullName.Replace('\', '/'))
}
$oAnimWebp = FirstProd "$work/out_anim2webp"
$oAnimGif  = FirstProd "$work/out_anim2gif"
$kOutWebp  = Probe-Packets $oAnimWebp "pk_out_webp"
$kOutGif   = Probe-Packets $oAnimGif  "pk_out_gif"
$nOutWebp  = IntOf $kOutWebp.value
$nOutGif   = IntOf $kOutGif.value
Write-Host ("  [产物] anim→webp = {0}  packets={1}（输入 {2}）" -f $oAnimWebp, $kOutWebp.value, $nAnim)
Write-Host ("  [产物] anim→gif  = {0}  packets={1}（输入 {2}）" -f $oAnimGif, $kOutGif.value, $nAnim)
CK ($rAnimWebp.readOk -and $rAnimGif.readOk) `
   "③(b) 前置：两格的子进程输出**都读到了**（同 ③ 的读失败纪律）"
CK ($rAnimWebp.terminated -and ($nOutWebp -eq $nAnim) -and $nAnim -gt 1) `
   "③(b) ⭐ anim.webp（$nAnim 帧）→ --format webp：产物 packets = **$nOutWebp == $nAnim**（帧数**精确保住**）—— 这一格才是「静默丢帧被修好」的证据（上面 4 格是 png，单帧容器取首帧本来就对）"
CK ($rAnimGif.terminated -and ($nOutGif -eq $nAnim) -and $nAnim -gt 1) `
   "③(b) anim.webp（$nAnim 帧）→ --format gif：产物 packets = **$nOutGif == $nAnim**（帧数**精确保住**）—— 第二个「能装多帧的目标」方向"
CK (BlockedAnimated $rAnimWebp) `
   "③(b) webp 格**命中 AnimatedInput 落 legacy**（引擎被挡下、不参与多帧编码）—— 与「产物帧数保住」同时成立 ⇒ 保帧来自 legacy 而非引擎。⚠ gif 格**不带** --color-space ⇒ 引擎本就不参与，故只对 webp 格断言这一条"

# ── ③(c) **最长边预缩放**：收尾轮改判据的行为覆盖（2026-09-18；此前**零覆盖**）────────────────
# ⚠ 为什么必须补（team-lead 裁定）：预缩放跳过判定已由 `IsAnimated(captured)` 改为消费
#   `inputMayHaveMultipleFrames` ⇒ 这是一处**行为外扩**：`静态输入 → gif/apng 目标` 从「跳预缩放」
#   变成「**跑**预缩放」（旧判据里「**输出**容器是 gif/apng」那一轴被去掉了）。而**今天没有任何门禁
#   覆盖这一格** ⇒ 不补就是一处**新的真空**。
# ⚠ 判据**只能取日志形状**：`[MaxDimension] 检测到超限` 逐字取自 `QueueProcessor.cs` 的
#   `captured.Log += $"[MaxDimension] 检测到超限，先使用 ffmpeg 缩放到临时文件再转码\n";`。
#   ⚠ **不得**用产物尺寸：两态产物**都是** 32x24（跳过 ⇒ 下游 legacy 自己 scale；不跳过 ⇒ 预缩放后再 scale）
#     ⇒ 尺寸判据**恒真**（§6 第 74 条）。这里正是「守卫要断言**证据行**，不能断言**结果**」。
#   ⚠ 也不得放宽成 `-match '\[MaxDimension\]'`：下游还有一条 `[MaxDimension] 命令含最长边缩放 …`（R1 插滤镜），
#     它**恰恰在"预缩放被跳过"时出现** ⇒ 放宽会让本段**反向假红/假绿**。
# ⚠ 三格构成**双向 + 一格专属**：
#   ① `anim.webp + dim` ⇒ **不含**（动图被跳过 = A1/丙 生效）
#   ② `still.webp + dim` ⇒ **含**（**负控**：证明这行**能**出现，否则 ① 是真空绿）
#   ③ `still.webp → gif + dim` ⇒ **含**（**丙 专属格**：旧判据在这里会**跳**预缩放，因为 fmt=gif）
$dimArgs = @("--max-dimension-enabled", "true", "--max-dimension", "32")
$rDimAnim  = RunApp $anim  "dim_anim"  $dimArgs
$rDimStill = RunApp $still "dim_still" $dimArgs
$rDimGif   = RunApp $still "dim_gif"   $dimArgs "gif"
foreach ($pair in @(@('anim->png+dim', $rDimAnim), @('still->png+dim', $rDimStill), @('still->gif+dim', $rDimGif))) {
  $nm = $pair[0]; $r = $pair[1]
  Write-Host ("  [{0}] 结束={1} exit={2} 耗时={3:N1}s 产物={4}" -f $nm, $r.terminated, $r.code, $r.elapsed, $r.prods)
  foreach ($l in @(($r.text -split "`r?`n") | Where-Object { $_ -match 'MaxDimension' })) {
    Write-Host "      $($l.Trim())" -ForegroundColor DarkGray
  }
}
CK ($rDimAnim.readOk -and $rDimStill.readOk -and $rDimGif.readOk) `
   "③(c) 前置：三格的子进程输出**都读到了**（读不到会由本断言点名，而不是让下面几条假红）"
$dimRan = '\[MaxDimension\] 检测到超限'
CK ($rDimStill.text -match $dimRan) `
   "③(c) **负控（守卫非真空）**：still.webp（静态）+ 最长边 32 ⇒ 日志**含**「[MaxDimension] 检测到超限」—— 证明这行**能**出现，否则下面那条『动图不含』就是真空绿"
CK ($rDimAnim.text -notmatch $dimRan) `
   "③(c) ⭐ anim.webp（$nAnim 帧）+ 最长边 32 ⇒ 日志**不含**「[MaxDimension] 检测到超限」（动图必须**跳过**预缩放；实 结束=$($rDimAnim.terminated) 产物=$($rDimAnim.prods)）"
CK ($rDimGif.text -match $dimRan) `
   "③(c) ⭐ **丙 专属格**：still.webp（**静态**）→ **--format gif** + 最长边 32 ⇒ 日志**含**「[MaxDimension] 检测到超限」—— 预缩放要跳过的是「**输入**有多帧」，不是「**输出**容器能装动画」（旧判据在这一格会跳过预缩放）"

# ── ③(d) **「热缓存」前提的行为锁**（2026-09-19 新增；此前是本门禁的唯一真盲区）──────
# ⚠ 为什么必须补（gp-3 审计发现）：`QueueProcessor.IsAnimated` 的 `.webp` 轴**只读**
#   `AnimatedWebpCache`（未命中 ⇒ false），而 `ProcessCjxlAsync` 的 `:1689` 依赖它判
#   「动图 ⇒ 回退」。若那个缓存在 `:1689` 执行时**是冷的** ⇒ 动画 webp 被判非动画 ⇒
#   走 PNG 中转 ⇒ **丢帧**。而改动前的 42 条门禁**无一条**覆盖「动画 webp → jxl 的 cjxl 中转回退」。
# ⚠ 可达性（**已实测**，2026-09-19，本机）：
#   · 两格都用 `--color-engine legacy` ⇒ `ColorEngineRouter.Requested` = false ⇒ `ShouldRoute` = false
#     ⇒ `!engineRoutable` ⇒ 落在 `QueueProcessor.cs:571`（`backend == Cjxl && !engineRoutable`）
#     ⇒ 进 `ProcessCjxlAsync` ⇒ cjxl 对 `.webp` 直编**必然失败**（日志「[cjxl] 直接编码失败 (退出码 1)」）
#     ⇒ 到 `:1689` 的 `IsAnimated(item)`。
#   ⇒ **两格同路径、同参数，只差输入素材**（8 帧动画 vs 静态）—— 这是最干净的对照。
#   ⚠ **不用** `--color-engine legacy` 时，静态 webp 会**走引擎**（`Requested` 默认 true）
#     ⇒ 与正例**不同路径** ⇒ 那样的负控只能证明「另一场景不含该行」，
#     **证明不了**「`IsAnimated` = false 时不打该行」。故两格都取 legacy
#     （实测：auto 组的静态格走的是引擎出口 `[色彩引擎] ✅`，不构成同路径对照）。
# ⚠ 为什么它锁得住「热缓存」：`AnimatedWebpCache` 由 `IsAnimatedWebpAsync` 写入，而后者由
#   `ProcessAsync:286` 的**单点求值**（`InputMayHaveMultipleFramesAsync`）触发，远早于 `:571` 的分派
#   ⇒ 到 `:1689` 时**必然已写**。若哪天有人把求值点挪到分派之后（或改成懒求值）⇒ 缓存变冷
#   ⇒ `IsAnimated` = false ⇒ **正例不再出现回退行 ⇒ 本段立刻转红** ✓
Write-Host "`n### ③(d) 「热缓存」前提：动画 webp 在 cjxl 回退点上必须仍被判为动画 ###" -ForegroundColor Cyan
$cjArgs = @("--encoder", "cjxl", "--color-engine", "legacy")
$rHotAnim  = RunApp $anim  "hot_anim"  $cjArgs "jxl"
$rHotStill = RunApp $still "hot_still" $cjArgs "jxl"
foreach ($pair in @(@('anim+cjxl', $rHotAnim), @('still+cjxl', $rHotStill))) {
  $nm = $pair[0]; $r = $pair[1]
  Write-Host ("  [{0}] 结束={1} exit={2} 耗时={3:N1}s 产物={4}" -f $nm, $r.terminated, $r.code, $r.elapsed, $r.prods)
  foreach ($l in @(($r.text -split "`r?`n") | Where-Object { $_ -match '\[cjxl\]' })) {
    Write-Host "      $($l.Trim())" -ForegroundColor DarkGray
  }
}
$hotRet  = '\[cjxl\] 动图/不支持格式（退出码 \d+），回退到 FFmpeg libjxl_anim 编码器'
$hotFail = '\[cjxl\] 直接编码失败 \(退出码 \d+\)，\.webp 不被 cjxl 直接支持'
CK ($rHotAnim.readOk -and $rHotStill.readOk) `
   "③(d) 前置：两格的子进程输出**都读到了**（读不到会由本断言点名，而不是让下面几条假红）"
CK (($rHotAnim.text -match '\[cjxl\] 直接编码 \(输入: \.webp') -and ($rHotStill.text -match '\[cjxl\] 直接编码 \(输入: \.webp')) `
   "③(d) 前置自检：两格**都进了 cjxl 直编分支**（日志都含「[cjxl] 直接编码 (输入: .webp」）—— 否则下面的对照**不是同路径**，断言会恒真"
CK ($rHotAnim.terminated -and ($rHotAnim.prods -ge 1) -and ($rHotAnim.text -match $hotRet)) `
   "③(d) ⭐ **正例**：anim.webp（$nAnim 帧）+ --encoder cjxl ⇒ 日志**含**「[cjxl] 动图/不支持格式（退出码 N），回退到 FFmpeg libjxl_anim 编码器」—— 证明「IsAnimated」在该点返回 **true**（「AnimatedWebpCache」**是热的**）"
CK ($rHotStill.text -match $hotFail) `
   "③(d) **负控自检**：still.webp（静态）+ 同参数 ⇒ 日志**含**「[cjxl] 直接编码失败 (退出码 N)，.webp 不被 cjxl 直接支持」—— 证明它**确实走到了同一个失败点**（不是路径不同才没有回退行）"
CK ($rHotStill.text -notmatch $hotRet) `
   "③(d) **负控**：still.webp（静态）+ 同参数 ⇒ 日志**不含**该回退行 ⇒ 证明上面那条**不是恒真**（「IsAnimated」= false 时确实不打）"

# ── ⑤ 单独取一次真退出码（回归钉）──────────────────────────────────────────────
# ⚠ PS5.1 下轮询路径的 `.ExitCode` 恒 `$null`（见 `RunApp` 注释），所以需要退出码的那**一条**
#   必须用 `-Wait -PassThru` **另跑一次**（照 `verify-avif-anim-probe.ps1` 的既有做法）。
Write-Host "`n### ⑤ 单独一次 -Wait -PassThru 取 still.webp 的真退出码（回归钉）###" -ForegroundColor Cyan
$rexOut = "$work/still_exit.out"
$rexDir = "$work/out_stillexit"
$rex = Start-Process -FilePath $exe `
       -ArgumentList @("--headless", "--log-level", "error", "-i", $still, "-o", $rexDir, "--format", "png") `
       -NoNewWindow -Wait -PassThru -RedirectStandardOutput $rexOut -RedirectStandardError "$rexOut.err"
CK ($rex.ExitCode -eq 0) "⑤ still.webp：exit == 0（**单独** -Wait -PassThru 运行取码；实 $($rex.ExitCode)）—— 正常静态路径不得因本次改动回归"

# ── ④ 被测 exe mtime 一致（§6 第 84 条：跑中途被重建则本轮读数作废）──────────────
Write-Host "`n### ④ 被测 exe mtime 跑前跑后一致 ###" -ForegroundColor Cyan
$exeMtimeAfter = (Get-Item $exe).LastWriteTimeUtc
CK ($exeMtimeBefore -eq $exeMtimeAfter) "④ 被测 exe 未被中途重建（$exe）"

# ── 兜底清扫（**不是断言**）────────────────────────────────────────────────────
Write-Host "`n### 兜底清扫：本门禁起过的应用进程全部终止（不构成断言）###" -ForegroundColor Cyan
foreach ($spid in $script:spawned) { KillTree ([int]$spid) }
Start-Sleep -Milliseconds 500
$stillAlive = @()
foreach ($spid in $script:spawned) {
  $pr = $null
  try { $pr = Get-Process -Id $spid -ErrorAction SilentlyContinue } catch { $pr = $null }
  if ($pr) { $stillAlive += $spid }
}
Write-Host "  本门禁共起过 $($script:spawned.Count) 个应用进程；清扫后仍存活：$(if ($stillAlive.Count -eq 0) { '0 个' } else { ($stillAlive -join ', ') })" -ForegroundColor $(if ($stillAlive.Count -eq 0) { 'DarkGray' } else { 'Red' })

# ── ⑥ 残留自证（仅在绿时判定；红时保留目录供排查）──────────────────────────────
Write-Host "`n### ⑥ 残留自证 ###" -ForegroundColor Cyan
if ($fail -eq 0) {
  # ⚠ 清理**只用运行时记录的精确路径**（`$work` 是本次运行级 GUID 目录），**禁止通配** —— 见文件头纪律。
  #    用 **.NET API** 绕过宿主批量删除守卫（`§6` 第 57/67 条）。
  try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
  CK (-not (Test-Path $work)) "⑥ 运行级目录已清零（精确路径，无通配）：$work"
} else {
  Write-Host "  （有失败 ⇒ 保留目录供排查：$work）" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "===== webp-anim-probe: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 退出码必须在清理之后，且必须写完整 if/else（§6 第 67 条）。
if ($fail -gt 0) { exit 1 } else { exit 0 }
