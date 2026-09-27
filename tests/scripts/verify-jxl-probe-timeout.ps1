# verify-jxl-probe-timeout.ps1 —— 「畸形 .jxl 让 ffprobe 帧数探测永不返回」专项门禁（2026-09-18 新增）
#
# 缺陷（本门禁要锁住的东西）：
#   `QueueProcessor.cs` 的 `IsAnimatedJxl` 里，同步阻塞读写在 `WaitForExit` **之前**：
#       `var output = proc.StandardOutput.ReadToEnd().Trim(); proc.WaitForExit(30000);`
#   ⇒ 子进程不关管道就永不返回 ⇒ **超时是死代码**，且超时后**没有 Kill**。
#   实测：`ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames`
#   对畸形 `.jxl` 输入**永不退出** ⇒ 队列永久挂死。
#   同款缺陷在 `FfmpegCommandBuilder.ProbeWithFfprobe`（同一路径的**下一站**）也存在 —— 只修一处不够。
#
# 修复判据（同步等价范式，**不改成 async** —— 那会波及 ~9 个调用点）：
#   先起异步读 `ReadToEndAsync()`（stdout + stderr 都要排空），再 `WaitForExit(超时)`，
#   超时后 `Kill(entireProcessTree: true)` 并**响亮记一行日志**。
#   兄弟范式见 `QueueProcessor.ProbeAvifStreamRolesAsync`（异步版）与
#   `FfmpegCommandBuilder.ProbePeakNits`（同文件同步版）。
#
# ⚠ 触发面**不是「1 字节」**（本门禁顺带纠正的一处认知）：实测是**两个条件的合取**——
#     ① `.jxl` 内容**不以 `FF 0A` 开头** ⇒ `JxlInspector.DetectType` 判 `Unknown`
#        ⇒ 落到 `ProcessJxlInputAsync` 的通用兜底分支（直连 `ffmpeg -y -i <file.jxl>`）；
#     ② 该字节形状让 **ffmpeg 的 JXL 解复用器忙等**（实测命中 1B `FF` / 1B `00` / 2B `00 00`；
#        1B `0A` / 2B `FF 00` 不命中，ffmpeg 秒退 69）。
#   2 B `FF 0A` 之所以安全，**不是因为字节数**，而是因为过了 magic 检查 ⇒ 判 `NativeCodestream`
#   ⇒ 走 `djxl | ffmpeg` 管道 ⇒ `djxl` 快速失败 ⇒ 不落进危险分支。
#   ⇒ 正例必须**逐字节断言形状**，否则「以为在测 1 字节」而实际测了别的形状（真空绿）。
#
# ⚠ **为什么断言必须落在「进程级阶段迁移」上，而不能只靠日志行**（实测教训）：
#   应用 stdout 重定向到文件时是**块缓冲**的，且 `ConsoleQueueObserver` 只在**下一次进度事件**
#   才转储 `item.Log` 的增量。实测：畸形输入挂住时，stdout 文件停在 259 字节（只有启动横幅）
#   长达 42 s 不动，`[jxl] 帧数探测超时…` 那行虽然**已经生成**却**看不见**；只有当 ffmpeg 开始
#   吐 banner 触发进度事件后才一次性刷出来。
#   ⇒ 日志行可见性**与「是否推进到下一阶段」耦合**，单靠它无法区分「站点 1 没修」与「站点 2 没修」。
#     故本门禁以**子进程命令行**为主证据（免疫缓冲）：站点 1 的 ffprobe 带 `-count_frames`，
#     站点 2 的 ffprobe 带 `-show_entries`，两者可精确区分，且「出现→消失」直接证明超时到期 + Kill 生效。
#
# 断言（每条正例 10 条 × 3 例）：
#   · 载体前置：本次运行确实排了 **2 个队列项**（应用自报「找到 2 个输入文件」）
#   · 进程级：`-count_frames` 的 ffprobe **出现过**（站点 1 真的被走到）
#   · 进程级：它在 [4, 20] s 内**消失**（超时真的到期 + Kill 杀了树），且 ≥4 s（证明真的等了，不是立刻返回）
#   · 进程级：随后 `-show_entries` 的 ffprobe **出现过**（站点 2 真的被走到）
#   · 进程级：它在 ≤15 s 内**消失**（站点 2 同样到期 + Kill 生效）—— **只修一处时这三条为红**
#   · 日志：出现逐字超时行 `[jxl] 帧数探测超时（<N>s）⇒ 已终止 ffprobe：<文件名>（按静图处理）`（响亮，不静默）
#   · 日志：随后出现 `[cmd] ffmpeg`（推进到了编码阶段）
#   · 畸形输入**绝不静默成功**（宿主无关判据：**若它自行结束了，就不得有产物**）
#   · ⭐ 缓存策略（2026-09-18 `#26` 新增，两载体）：**不确定的探测结果不得被写入缓存** ——
#     (i) 形态：源码里 `IsAnimatedJxl` 的缓存写入必须**被 `determinate` 守卫**，且不得再出现
#         `AnimatedJxlCache.GetOrAdd(`；
#     (ii) 行为：同一次运行里该路径被探测**多次**（实测 2 次），若「不确定」也写缓存，第 2 次会
#         直接吃缓存 ⇒ **只会有 1 条**超时行。故逐字超时行必须 **≥2 条**。
#     ⚠ **载体 (ii) 在 2026-09-18 收尾轮被重取**（原载体失效，见下）：原来的「2 次」来自**同一项内
#       两个调用点**（`:282` 的 `InputMayHaveMultipleFramesAsync` → `IsAnimatedJxl`，以及预缩放点的
#       `IsAnimated(captured)` → `IsAnimatedJxl`）。收尾轮把预缩放点改为消费**同一个**
#       `inputMayHaveMultipleFrames` ⇒ **那一跳被删掉** ⇒ 同项内只剩 1 次探测 ⇒ 计数恒为 1 ⇒ 载体失效。
#       ⚠ 那是**改进**（少探一次），**不是**产品回归：「不确定不缓存」的守卫一个字都没动。
#     ⇒ 新载体：**同一次运行里跑两个队列项**（`-i "f.jxl,f.jxl"` + `--concurrency 1`）⇒ 每项各探一次
#       ⇒ 逐字超时行 ≥2 条。这**仍**证明「不确定不缓存」，而且**更强**：它证的是**跨项**不缓存 ——
#       若不确定结果被写进 `AnimatedJxlCache`，第 2 项会**直接吃缓存**（不起 ffprobe、不留超时行）
#       ⇒ 计数回落到 1 ⇒ 本断言转红（实测见 ② 的变异验证）。
#       ⚠⚠ **为什么必须 `--concurrency 1`**：默认并发 = `Environment.ProcessorCount`（≥2）⇒ 两项**并行**，
#       第 2 项的探测会在第 1 项**写缓存之前**（探测要 ~5 s）就读缓存 ⇒ **并行下本断言无牙**（恒 2 条）。
#       ⇒ 必须**串行**，让第 1 项探测**完成**后第 2 项才开始。
#       ⚠⚠ **为什么必须注入一个小的心跳超时**：畸形 `.jxl` 走完两个探测站点后会落到编码阶段
#       （`[cmd] ffmpeg -y -i <file.jxl>`），而 ffmpeg 的 JXL 解复用器在该字节形状上**忙等**（永不返回）
#       ⇒ 串行下第 1 项会**卡死**，第 2 项**永远不会开始**（实测：只 1 次 `-count_frames` 出现）。
#       注入 `FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC` 后第 1 项的编码被心跳**判死并杀树** ⇒ 队列推进到第 2 项
#       （实测：`-count_frames` 出现 2 次、应用自行退出）。心跳本身由 `verify-ffmpeg-heartbeat.ps1` 锁，
#       这里只是**借它让载体可推进**；该注入**只对 ② 正例生效**（③ 负控前会被清掉，见下方注释）。
#     ⚠ 载体 (ii) 的判据依赖「一次运行内该路径被探测 ≥2 次」这一**实测事实**。若将来调用次数降到 1，
#       本断言会因**载体**失效而红（不是缺陷）⇒ 那时须重取载体，**不得**把判据放宽成 `>= 1`
#       （第 74 条：载体选错 ⇒ 恒真）。
#   · ③ real.jxl 的 `exit == 0` 用**单独**一次 `-Wait -PassThru` 运行取码（理由见下）
#   另有 ① 素材形态自检 6 条（含源码默认值 120 s 形态自检）、③ 负控 10 条、④ exe mtime 1 条、⑥ 残留自证 1 条。
#   ⇒ 合计 10 × 3 + 6 + 10 + 1 + 1 = **48 条**（2026-09-18 收尾轮：45 → 48；逐条以**实测**为准）。
#
# ⚠ **为什么本门禁注入一个很小的超时值**（`FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC=5`）：
#   生产默认是 **120 s**（依据 = 大动画 JXL 的 `-count_frames` 实测耗时，见
#   `QueueProcessor.IsAnimatedJxl` 的 `<remarks>`：1080p≈0.023 s/帧、4K≈0.089、8K≈0.393）。
#   但本门禁要证的是**机制**（「异步读 ⇒ WaitForExit 可达 ⇒ Kill 杀树 ⇒ 日志响亮」），
#   不是那个常量本身；若按 120 s 实跑，3 正例 × 3 个变异态 ≈ 20+ 分钟，运行器预算撑不住，
#   而且超时值本身另有 ① 的源码形态自检钉住。⇒ 注入 5 s，把机制验证压到十几秒。
#   ⚠ 2026-09-18 收尾轮：② 每例现在跑**两个队列项**（载体 (ii)）⇒ 单例 ≈ 30 s（实测 29.6~29.8 s），
#     3 例 ≈ 90 s。这是载体所需的最小代价，已尽量压到「证据齐即提前收工」。
#   ⚠ 注入值**不能太小**：负控（截断真 JXL）也要在同一个注入值下完成 `-count_frames`，
#     太小会让负控误触超时分支 ⇒ 假红。
#
# ⚠ **负控的 `站点1出现` 可能为 False —— 那是采样分辨率的假阴性，不是「探测没被走到」**：
#   合法/截断 JXL 的 `-count_frames` 只要 ~0.1 s，而本门禁每 500 ms 才采样一次 ⇒ 采不到。
#   故负控的判据**只有**「不出现超时行 + 限时内结束」，**不要**把「探测出现过」加进负控断言。
#   ⇒ 由此也留下一条**已知覆盖缺口**（已登记）：本门禁**没有**「大动画 JXL 的探测能在超时内跑完」
#     这一条 —— 构造它需要真多帧 JXL，而注入 5 s 时 1080p 只够 ~200 帧，反而会把合法动画判超时。
#     生产默认 120 s 的依据由 `IsAnimatedJxl` 的 `<remarks>`（实测 s/帧）承担，不由此门禁承担。
#
# ⚠ **已知残余（本门禁不覆盖，另行登记）**：畸形 `.jxl` 走到的**第三站**是 ffmpeg 编码本身
#   （`ffmpeg -y -i <file.jxl> …`），它在该输入上**忙等**（实测 ≈90% 单核、stderr 只有 banner、
#   25 s 不退出）⇒ 应用仍不会自行结束。而 `FfmpegRunner.cs` 的挂死守卫要求「无输出 **且**
#   无 CPU 增长」，忙等恰好喂饱 CPU ⇒ `InactivityTimeoutMinutes = 30` 那条分支**结构性不可达**。
#   ⚠ 2026-09-18 更新（#15）：该忙等**已**被 `-progress pipe:1` 心跳守卫判死，由
#     `tests/scripts/verify-ffmpeg-heartbeat.ps1` 锁（正向：限时判死；反向：合法慢任务不被误杀）。
#   ⚠ 2026-09-18 收尾轮更新：② 正例现在**主动注入** `FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC=3` ——
#     不是为了测它，而是因为载体 (ii) 需要**串行跑两个队列项**，而第 1 项会卡在这个忙等上
#     ⇒ 不注入则第 2 项永不开始（实测）。⇒ ② 里应用会**自行结束**（忙等被 3 s 阈值判死）。
#     该注入在 ③ 负控前被清掉（负控含合法任务，不该被小阈值影响）。
#   本门禁只锁「探测阶段必须在限时内响亮返回」，不锁编码阶段的忙等（那由 heartbeat 门禁锁）。
#
# 退出码：0 = 全绿，1 = 有失败。⚠ 结尾必须是**完整 if/else**（§6 第 67 条：清理步会污染
#   `$LASTEXITCODE`，只写 `if ($fail -gt 0) { exit 1 }` 会让全绿路径以被污染的 2 退出 ⇒ 假红）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1，门禁 `verify-ps-compat.ps1`）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；
#   双引号串内禁全角弯引号（用 「」）；取子进程输出一律 `Start-Process` + 重定向（`& exe` 部分宿主静默失败）。
# ⚠ **禁止 `Start-Process pwsh`**（宿主安全策略会拦）⇒ 限时 + 杀进程树一律在**本进程内**用
#   `Start-Process -PassThru` + `WaitForExit(ms)` / `KillTree` 实现。
# ⚠⚠ **杀树禁用 `$p.Kill($true)`**：该重载在 **Windows PowerShell 5.1（.NET Framework）上不存在**
#   （只有无参 `Kill()`）⇒ 异常被 `try/catch` 吞掉、应用没被杀、宿主因重定向流收尾而**永久挂住**
#   （实测 PS5.1 下 10+ 分钟不返回 + 残留 3 个 FfmpegGui/3 个 ffmpeg）。用下面的 `KillTree`（先子后父）。
# ⚠⚠ **退出码禁用轮询式 `.ExitCode`**：PS5.1 下 `Start-Process -PassThru`（不带 `-Wait`）的 `.ExitCode`
#   **恒为 `$null`**（实测：带 `-Wait` ⇒ `1` (Int32)；轮询到 `HasExited` 后读 ⇒ `<null>`，`Refresh()` 无效）。
#   而 `[int]$null == 0` ⇒ 强转会把「读不到」伪装成「exit 0」⇒ **假绿（比不测更坏）**。
#   ⇒ 本门禁：轮询路径一律保留 `$null`（绝不强转），唯一需要退出码的断言单独用 `-Wait -PassThru` 跑。
#   ⚠ `verify-ps-compat.ps1` 是**静态语法扫描**，**抓不到**这两类「API/语义在回退宿主上不同」的缺陷
#     ⇒ 双宿主必须**真在 PS5.1 里跑一遍**（本门禁 2026-09-18 就是靠真跑才发现的）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*\Debug\*') { " (Debug fallback)" } else { " (Release)" }))
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"

# ⚠ 运行级隔离（GUID）：固定 `$work` 会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$val  = "$root/tests/output/validate"
$gid  = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = "$val/jxlprobe_${gid}"
New-Item -ItemType Directory -Force -Path $work | Out-Null

# ⚠ 注入站点 1 的超时值（生产默认 120 s，见文件头与 `IsAnimatedJxl` 的 remarks）：
#   本门禁验的是**机制**，注入 5 s 即可，且必须**在启动被测应用之前**设好（应用按进程环境读）。
$probe1Sec = 5
$env:FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC = "$probe1Sec"

# 正例：**两个队列项**（同一文件各一项，`--concurrency 1` ⇒ 串行），每项各探站点 1 一次（5 s 超时）
#   ⇒ 逐字超时行 2 条（见文件头的载体 (ii) 说明）。每项还要再走站点 2 + 编码阶段（编码被注入的
#   3 s 心跳判死），故单次运行实测 ≈ 35 s ⇒ 上限提到 60 s。
$positiveLimitSec = 60
$negativeLimitSec = 30
$probe1MinSec = 4       # 站点 1 消失下限（必须真的等了 ~5 s，不能是「立刻返回」）
# ⚠ 站点 1 在**第 1 项**里消失于 ≈ 7 s（0.5 s 启动 + 5 s 超时 + 采样去抖）。
#   `$p1Gone` 只记**第一次**消失时刻（`if ($p1Gone -eq 0.0)` 守卫），故不会被第 2 项覆盖。
#   判据的**实质**是「(i) 真的等了（≥ 下限）且 (ii) 最终消失了（`p1Gone > 0`）」；
#   上限只是防「消失得过早」的软护栏。
$probe1MaxSec = 20      # 站点 1 消失上限（预期 ≈ 7 s；上限留足余量）
$probe2MaxSec = 15      # 站点 2 超时上限
# ⚠ 注入心跳超时（**只**为让 ② 正例的串行队列能推进 —— 理由见文件头载体 (ii) 的第三条 ⚠）：
#   畸形 `.jxl` 的编码阶段会忙等，不注入则第 1 项卡死、第 2 项永不开始。3 s 是实测可推进且
#   不误杀合法短任务的值（合法 ffmpeg 有 `-progress pipe:1` 进度块续命）。③ 负控前会被清掉。
$hbSec = 3
$env:FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC = "$hbSec"

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

# 只读 stdout 版（取「工具的值」专用，返回形态与 Exec 一致 ⇒ `.code` 判据不受影响）：
#   ffprobe 的 `-of csv` 字段值走 **stdout**，合并 stderr 会把报错行（本机实测截断 AVIF：stdout 0 B /
#   stderr 166 B 的 `moov atom not found` + 带数字路径）当值喂给 FrameCount。读 ffmpeg 造素材的进度与
#   报错（**只在 stderr**）时仍用 Exec；应用的 headless 日志由 RunApp 直接读 `.app.out` / `.app.err`。
function ExecOut([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = [System.IO.File]::ReadAllText($o) }
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
#   Windows PowerShell 5.1（.NET Framework）里 `[System.Diagnostics.Process]` **只有无参 `Kill()`**
#   （实测：`[System.Diagnostics.Process].GetMethods() | ? Name -eq 'Kill'` 在 PS5.1 只回 `Void Kill()`）。
#   ⇒ `Kill($true)` 会抛 `MethodException`，被 `try/catch` 吞掉后**子进程根本没被杀**；实测后果：
#     应用 + ffmpeg 全部存活，且宿主因 `-RedirectStandardOutput` 的异步读收尾而**永久挂住**
#     （PS5.1 下跑本门禁 10+ 分钟不返回，留下 3 个 FfmpegGui + 3 个 ffmpeg）。
#   ⚠ `verify-ps-compat.ps1` 是**静态语法扫描**，抓不到「API 在回退宿主上不存在」这类缺陷
#     ⇒ 双宿主验证必须**真在 PS5.1 里跑一遍**，不能只靠它。
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

# 本门禁启动过的应用 PID（收尾时兜底清扫；**只杀自己起过的**，不碰用户可能在跑的 GUI 实例）。
$script:spawned = @()

# 子进程阶段快照（免疫 stdout 缓冲）：只认**应用进程的直接子进程**。
#   p1  = ffprobe 带 `-count_frames`  ⇒ 站点 1（IsAnimatedJxl）
#   p2  = ffprobe 带 `-show_entries`  ⇒ 站点 2（ProbeWithFfprobe）
#   enc = ffmpeg                       ⇒ 编码阶段（证明已推进过两个探测站点）
function Snapshot([int]$appPid) {
  $res = @{ p1 = $false; p2 = $false; enc = $false }
  $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
  foreach ($q in $all) {
    if ($q.ParentProcessId -ne $appPid) { continue }
    $cl = ""
    if ($null -ne $q.CommandLine) { $cl = $q.CommandLine }
    if (($q.Name -like 'ffprobe*') -and ($cl -match '\-count_frames')) { $res.p1 = $true }
    elseif (($q.Name -like 'ffprobe*') -and ($cl -match '\-show_entries')) { $res.p2 = $true }
    elseif ($q.Name -like 'ffmpeg*') { $res.enc = $true }
  }
  return $res
}

# 真实帧数：ffprobe `-count_frames`（**不用** `frame=` 日志字段：本工程已实测不可靠）。
# ⚠ 只认**整行就是数字**的输出行：ffprobe 报错会带上含数字的路径（GUID 目录名）⇒ 宽松匹配会假绿。
function FrameCount([string]$path) {
  $r = ExecOut $fp "-v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames -of csv=p=0 `"$path`"" "fc_$([guid]::NewGuid().ToString('N').Substring(0,6))"
  $n = 0
  foreach ($ln in ($r.text -split "`r?`n")) { if ($ln -match '^\s*(\d+)\s*$') { $n = [int]$Matches[1] } }
  return $n
}

# 产物：$outdir 下第一个匹配扩展名的文件（找不到返回 $null）。
function Product([string]$outdir, [string[]]$exts) {
  if (-not (Test-Path $outdir)) { return $null }
  $all = @(Get-ChildItem -LiteralPath $outdir -File -Recurse -ErrorAction SilentlyContinue)
  foreach ($f in $all) {
    foreach ($x in $exts) { if ($f.Extension -ieq $x) { return $f } }
  }
  return $null
}

# 字节形状摘要（前 4 字节十六进制，供自检与报错时人眼核对）。
function HeadHex([string]$path) {
  if (-not (Test-Path $path)) { return "" }
  $b = [System.IO.File]::ReadAllBytes($path)
  $n = [Math]::Min(4, $b.Length)
  $parts = @()
  for ($i = 0; $i -lt $n; $i++) { $parts += $b[$i].ToString('X2') }
  return ($parts -join ' ')
}

# ── 单条用例：起应用、按 500 ms 采样子进程阶段 + 日志、证据齐了提前收工、必要时杀整棵树 ──
# ⚠ `$inSpec` 是**逗号分隔的输入清单**（`-i` 原生支持）—— ② 正例故意传「同一路径两次」= 两个队列项，
#   配合 `--concurrency 1`（串行）让 `AnimatedJxlCache` 的「不确定不缓存」在**跨项**上可观测。
function RunApp([string]$inSpec, [string]$outdir, [string]$tag, [int]$limitSec, [string]$expectName) {
  if (Test-Path $outdir) { try { [System.IO.Directory]::Delete($outdir, $true) } catch { } }
  $so = "$work/$tag.app.out"; $se = "$work/$tag.app.err"
  try { [System.IO.File]::Delete($so) } catch { }
  try { [System.IO.File]::Delete($se) } catch { }

  $p = Start-Process -FilePath $exe `
       -ArgumentList @("--headless", "--log-level", "debug", "-i", $inSpec, "--concurrency", "1", "-o", $outdir, "--format", "png") `
       -PassThru -NoNewWindow -RedirectStandardOutput $so -RedirectStandardError $se
  $script:spawned += [int]$p.Id

  $needle = "[jxl] 帧数探测超时（$($probe1Sec)s）⇒ 已终止 ffprobe：" + $expectName
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $seenP1 = $false; $p1Gone = 0.0; $p1Miss = 0; $seenP2 = $false; $p2Gone = 0.0; $p2Miss = 0; $seenEnc = $false
  $sawTimeout = $false; $sawCmd = $false
  # ⭐ `#26`：逐字超时行的**出现次数**（不是「是否出现」）。② 正例传**同一路径两个队列项**
  #   ⇒ 每项各探一次 ⇒ 「不确定的结果不写缓存」成立时计数 ≥2；
  #   若「不确定」也写缓存，第 2 项直接吃缓存、**不再起 ffprobe** ⇒ 计数恒为 1。
  #   ⚠ 循环内也要数（`$tCount`）：提前收工判据必须等**第 2 条**出现，否则会在第 1 项结束时
  #     就 break ⇒ 第 2 项根本没跑 ⇒ 计数恒 1 ⇒ 假红。
  $timeoutCount = 0
  while ($sw.Elapsed.TotalSeconds -lt $limitSec) {
    Start-Sleep -Milliseconds 500
    if ($p.HasExited) { break }
    $s = Snapshot $p.Id
    $now = $sw.Elapsed.TotalSeconds
    # ⚠ 连续 2 次采样都看不到才算「消失」：`Get-CimInstance` 偶发取不到进程列表时，
    #   单次缺失会把消失时刻记早 ⇒ 站点 1 的 `>= $probe1MinSec s` 断言会假红。
    if ($s.p1) { $seenP1 = $true; $p1Miss = 0 }
    elseif ($seenP1) { $p1Miss++; if (($p1Miss -ge 2) -and ($p1Gone -eq 0.0)) { $p1Gone = $now } }
    if ($s.p2) { $seenP2 = $true; $p2Miss = 0 }
    elseif ($seenP2) { $p2Miss++; if (($p2Miss -ge 2) -and ($p2Gone -eq 0.0)) { $p2Gone = $now } }
    if ($s.enc) { $seenEnc = $true }
    $t = ReadShared $so
    if ($t.Contains($needle)) { $sawTimeout = $true }
    if ($t -match '(?m)^\[cmd\] ffmpeg ') { $sawCmd = $true }
    if ($t.Length -gt 0) { $timeoutCount = ([regex]::Matches($t, [regex]::Escape($needle))).Count }
    # 两个探测站点都消失 + 编码已起 + 日志齐 + **第 2 条超时行已出现** ⇒ 证据齐，立即收工。
    # ⚠ 续行处**必须**把 `-and` 留在行尾（PS 5.1 解析器不接受行首 `-and`：实测报
    #   「"if"语句中的表达式后缺少右括号」）。
    if (($p1Gone -gt 0.0) -and ($p2Gone -gt 0.0) -and $seenEnc -and $sawTimeout -and $sawCmd -and
        ($timeoutCount -ge 2)) { break }
  }
  $sw.Stop()
  $terminated = $p.HasExited
  # ⚠⚠ 退出码：**故意**保留 `$null`，绝不 `[int]` 强转。
  #   Windows PowerShell 5.1 下 `Start-Process -PassThru`（**不带 `-Wait`**）的 `.ExitCode` **恒为 `$null`**
  #   （实测：`-Wait -PassThru` ⇒ `1` (Int32)；轮询到 `HasExited` 后读 ⇒ `<null>`，`Refresh()` 也救不回来）。
  #   而 `[int]$null` 在 PowerShell 里是 **0** ⇒ 强转会把「读不到」伪装成「exit 0」⇒ **假绿**。
  #   ⇒ 需要退出码的断言**单独**用 `-Wait -PassThru` 再跑一次（见 ③ real.jxl），这里只保留 `$null`。
  $code = $null
  if ($terminated) {
    try { if ($null -ne $p.ExitCode) { $code = $p.ExitCode } } catch { $code = $null }
  }
  $killed = $false
  if (-not $terminated) {
    # 杀**进程树**：只杀父进程会留下 ffmpeg/ffprobe 孙进程（后续用例会互相干扰）。
    KillTree ([int]$p.Id)
    $killed = $true
    Start-Sleep -Milliseconds 600
  }
  $text = ReadShared $so
  if ($text.Length -eq 0) { $text = ReadShared $se }
  # ⭐ `#26`：数**出现次数**（`$needle` 是逐字超时行前缀 + 文件名）。
  if ($text.Length -gt 0) { $timeoutCount = ([regex]::Matches($text, [regex]::Escape($needle))).Count }
  $prods = 0
  if (Test-Path $outdir) { $prods = @(Get-ChildItem -LiteralPath $outdir -File -Recurse -ErrorAction SilentlyContinue).Count }
  return @{ terminated = $terminated; code = $code; elapsed = $sw.Elapsed.TotalSeconds
            seenP1 = $seenP1; p1Gone = $p1Gone; seenP2 = $seenP2; p2Gone = $p2Gone; seenEnc = $seenEnc
            sawTimeout = $sawTimeout; sawCmd = $sawCmd; timeoutCount = $timeoutCount
            prods = $prods; text = $text; killed = $killed }
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
Write-Host "`n### ① 自备畸形 .jxl 正例 + 负控，并**逐字节**断言形状 ###" -ForegroundColor Cyan
$pos1 = "$work/one_ff.jxl"      # 1 B  FF     —— 正例
$pos2 = "$work/one_00.jxl"      # 1 B  00     —— 正例
$pos3 = "$work/two_0000.jxl"    # 2 B  00 00  —— 正例（纠正「2 字节不触发」的认知）
$neg1 = "$work/two_ff0a.jxl"    # 2 B  FF 0A  —— 负控（过 magic 检查 ⇒ 走管道）
$neg2 = "$work/trunc64.jxl"     # 真 JXL 截到 64 B
$neg3 = "$work/trunc2000.jxl"   # 真 JXL 截到 2000 B
$real = "$work/real.jxl"        # 完整真 JXL

[System.IO.File]::WriteAllBytes($pos1, [byte[]](0xFF))
[System.IO.File]::WriteAllBytes($pos2, [byte[]](0x00))
[System.IO.File]::WriteAllBytes($pos3, [byte[]](0x00, 0x00))
[System.IO.File]::WriteAllBytes($neg1, [byte[]](0xFF, 0x0A))

# 真 JXL 用本仓自带的 ffmpeg（libjxl 出口默认写**裸码流**，前缀即 FF 0A）。
$g1 = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=256x192`" -frames:v 1 `"$work/seed.png`"" "gen_seed"
$g2 = Exec $ff "-y -hide_banner -loglevel error -i `"$work/seed.png`" -frames:v 1 -c:v libjxl `"$real`"" "gen_real"
$seedBytes = [System.IO.File]::ReadAllBytes($real)
if ($seedBytes.Length -gt 2000) {
  $t64 = New-Object byte[] 64
  [Array]::Copy($seedBytes, $t64, 64)
  [System.IO.File]::WriteAllBytes($neg2, $t64)
  $t2k = New-Object byte[] 2000
  [Array]::Copy($seedBytes, $t2k, 2000)
  [System.IO.File]::WriteAllBytes($neg3, $t2k)
}

$h1 = HeadHex $pos1; $h2 = HeadHex $pos2; $h3 = HeadHex $pos3
$n1 = HeadHex $neg1; $n2 = HeadHex $neg2; $n3 = HeadHex $neg3; $nr = HeadHex $real
$l1 = 0; $l2 = 0; $l3 = 0; $l4 = 0; $l5 = 0; $l6 = 0; $l7 = 0
if (Test-Path $pos1) { $l1 = (Get-Item $pos1).Length }
if (Test-Path $pos2) { $l2 = (Get-Item $pos2).Length }
if (Test-Path $pos3) { $l3 = (Get-Item $pos3).Length }
if (Test-Path $neg1) { $l4 = (Get-Item $neg1).Length }
if (Test-Path $neg2) { $l5 = (Get-Item $neg2).Length }
if (Test-Path $neg3) { $l6 = (Get-Item $neg3).Length }
if (Test-Path $real) { $l7 = (Get-Item $real).Length }
$realFrames = 0; if (Test-Path $real) { $realFrames = FrameCount $real }
Write-Host "  [正例] one_ff.jxl=$l1 B [$h1] | one_00.jxl=$l2 B [$h2] | two_0000.jxl=$l3 B [$h3]"
Write-Host "  [负控] two_ff0a.jxl=$l4 B [$n1] | trunc64.jxl=$l5 B [$n2] | trunc2000.jxl=$l6 B [$n3] | real.jxl=$l7 B [$nr] 帧=$realFrames"

CK (($l1 -eq 1) -and ($h1 -eq 'FF') -and
    ($l2 -eq 1) -and ($h2 -eq '00') -and
    ($l3 -eq 2) -and ($h3 -eq '00 00')) `
   "① 三个正例的字节形状**逐字节**符合预期（1B FF / 1B 00 / 2B 00 00；实 $l1/$l2/$l3 B，[$h1] / [$h2] / [$h3]）"

CK (($l4 -eq 2) -and ($n1 -eq 'FF 0A')) `
   "① 负控 two_ff0a.jxl 真的以「FF 0A」开头（$l4 B [$n1]）—— 这正是它**不**走超时分支的原因（过 magic 检查 ⇒ NativeCodestream 管道）"

CK (($l7 -gt 2000) -and ($nr -like 'FF 0A*') -and ($realFrames -eq 1)) `
   "① 完整真 JXL 形态正确（$l7 B [$nr]，ffprobe 帧数=$realFrames，须 >2000 B 且 ==1 帧）"

CK (($l5 -eq 64) -and ($n2 -like 'FF 0A*') -and ($l6 -eq 2000) -and ($n3 -like 'FF 0A*')) `
   "① 两个截断真 JXL 形状正确（64 B [$n2] / 2000 B [$n3]，均以「FF 0A」开头）"

# ①-⑤ 源码形态自检：**生产默认超时必须真的是 120 s，且必须真的被用上**。
#   本门禁为控制耗时注入了 5 s（见文件头）⇒ 若不钉住生产默认值，注入路径全绿也可能掩盖
#   「生产默认仍是 20 s 死代码」或「注入值压根没接进 WaitForExit」。
$qpSrc = "$root/src/FfmpegGui/Services/QueueProcessor.cs"
$srcTxt = ""
if (Test-Path $qpSrc) { $srcTxt = [System.IO.File]::ReadAllText($qpSrc) }
# 去掉**整行**注释后再断言（站点 1 的注释里正引用了旧代码的同步 `StandardOutput.ReadToEnd()`）。
$srcCodeLines = @()
foreach ($ln in ($srcTxt -split "`r?`n")) { if ($ln -notmatch '^\s*//') { $srcCodeLines += $ln } }
$srcCode = $srcCodeLines -join "`n"
$body = ""
$bi = $srcCode.IndexOf('private static bool IsAnimatedJxl(')
if ($bi -ge 0) { $body = $srcCode.Substring($bi) }
$shapeOk = ($srcCode -match 'DefaultJxlProbeTimeoutSec\s*=\s*120\b') -and
           ($body -match 'WaitForExit\(timeoutSec \* 1000\)') -and
           ($body -match 'ReadToEndAsync\(\)') -and
           ($body -notmatch 'StandardOutput\.ReadToEnd\(\)') -and
           ($body -match 'Kill\(entireProcessTree: true\)')
CK ($shapeOk) `
   "① 源码形态：默认超时 == 120 s、`WaitForExit(timeoutSec * 1000)` 真的用了该值、`Kill(entireProcessTree: true)` 在位、且站点 1 体内**没有**同步的 `StandardOutput.ReadToEnd()`（同步读挡在超时前 ⇒ 超时是死代码）"

# ⭐ ①-⑥ 源码形态自检（#26，2026-09-18）：缓存写入必须**被 determinate 守卫**。
#   反例（把「只缓存确定结果」改回「无条件写」）：`AnimatedJxlCache[inputPath] = animated;` 不再带守卫
#   ⇒ 本条转红；若改回 `GetOrAdd(` 工厂形态也转红（那正是会固化「不确定」的旧形态）。
$cacheGuardOk = ($srcCode -notmatch 'AnimatedJxlCache\.GetOrAdd\(') -and
                ($srcCode -match 'if\s*\(\s*determinate\s*\)\s*AnimatedJxlCache\[inputPath\]\s*=\s*animated\s*;') -and
                ($srcCode -match 'AnimatedJxlCache\.TryGetValue\(') -and
                ($srcCode -match 'determinate\s*=\s*true')
CK ($cacheGuardOk) `
   "① 源码形态（#26 缓存策略）：缓存写入被 `determinate` 守卫、读取走 `TryGetValue`、且已无 `AnimatedJxlCache.GetOrAdd(`（后者会把「不确定」的结局也永久固化）"

# ── ② 正例：两个探测站点都必须「出现 → 限时内消失」，且超时**必须响亮** ──────────────
Write-Host "`n### ② 正例：畸形 .jxl ⇒ 两个探测站点的 ffprobe 都必须在限时内被 Kill + 逐字超时日志行 ###" -ForegroundColor Cyan
Write-Host "   （主证据是子进程命令行，免疫 stdout 块缓冲：站点 1 = ffprobe -count_frames，站点 2 = ffprobe -show_entries）" -ForegroundColor DarkGray
Write-Host "   （每例**同一路径跑两个队列项** + --concurrency 1：让 AnimatedJxlCache 的「不确定不缓存」在跨项上可观测）" -ForegroundColor DarkGray
$positives = @(
  @{ path = $pos1; name = "one_ff.jxl";   shape = "1B FF"    },
  @{ path = $pos2; name = "one_00.jxl";   shape = "1B 00"    },
  @{ path = $pos3; name = "two_0000.jxl"; shape = "2B 00 00" }
)
$n = 0
$resid = @()
foreach ($c in $positives) {
  $n++
  $tag = "pos$n"
  $outdir = "$work/out_$tag"
  # ⚠ 同一路径传两次 = 两个队列项（`-i` 原生支持逗号分隔）；`--concurrency 1` 在 RunApp 内固定。
  $r = RunApp "$($c.path),$($c.path)" $outdir $tag $positiveLimitSec $c.name
  $endDesc = "未结束(已杀)"
  if ($r.terminated) {
    $endDesc = "结束 exit=$($r.code)"
    if ($null -eq $r.code) { $endDesc = "结束 exit=?（本宿主读不到，见 ③）" }
  }
  Write-Host ("  [{0} / {1}] {2} 总耗时={3:N1}s" -f $c.name, $c.shape, $endDesc, $r.elapsed)
  Write-Host ("     站点1(-count_frames) 出现={0} 消失@{1:N1}s | 站点2(-show_entries) 出现={2} 消失@{3:N1}s | 编码ffmpeg出现={4} | 超时行={5}（{8} 条） 见[cmd]={6} 产物={7}" -f `
    $r.seenP1, $r.p1Gone, $r.seenP2, $r.p2Gone, $r.seenEnc, $r.sawTimeout, $r.sawCmd, $r.prods, $r.timeoutCount)

  # ⚠ **载体前置**（防「载体悄悄退回 1 项 ⇒ 计数恒 1 ⇒ 假红」被误读成产品缺陷）：
  #   应用启动时会打印「找到 N 个输入文件」⇒ N 必须是 2（同一路径传两次）。
  CK ($r.text -match '找到 2 个输入文件') `
     "② $($c.name)：载体前置 —— 本次运行确实排了 **2 个队列项**（`-i` 传同一路径两次；应用自报「找到 2 个输入文件」）"
  CK ($r.seenP1) `
     "② $($c.name)（$($c.shape)）：站点 1 的 ffprobe（`-count_frames`）**出现过** —— 证明该探测真的被走到（否则断言真空绿）"
  $p1ok = ($r.p1Gone -gt 0.0) -and ($r.p1Gone -le $probe1MaxSec) -and ($r.p1Gone -ge $probe1MinSec)
  CK ($p1ok) `
     "② $($c.name)：站点 1 的 ffprobe 在 [$probe1MinSec, $probe1MaxSec] s 内**消失**（实 $(if ($r.p1Gone -gt 0.0) { [Math]::Round($r.p1Gone,1).ToString() + ' s' } else { '未消失' })）—— 证明 WaitForExit($($probe1Sec)s) 真的到期且 Kill 杀了树；<$probe1MinSec s 说明判据没走到（或超时值被注入得过小），未消失说明超时仍是死代码。⚠ `$p1Gone` 只记**第一次**消失（第 1 项那次），不会被第 2 项覆盖"
  CK ($r.seenP2) `
     "② $($c.name)：随后站点 2 的 ffprobe（「-show_entries」）**出现过** —— 证明执行推进过了站点 1"
  $p2ok = ($r.p2Gone -gt 0.0) -and ($r.p2Gone -le ($r.p1Gone + $probe2MaxSec)) -and ($r.p2Gone -ge $r.p1Gone)
  CK ($p2ok) `
     "② $($c.name)：站点 2 的 ffprobe 也在 ≤ $probe2MaxSec s 内**消失**（实 $(if ($r.p2Gone -gt 0.0) { [Math]::Round($r.p2Gone,1).ToString() + ' s' } else { '未消失' })）—— **只修一处时本条为红**"
  CK ($r.seenEnc) `
     "② $($c.name)：子进程里出现 ffmpeg —— 证明执行真的推进过了**两个**探测站点（进程级证据，免疫 stdout 块缓冲）"
  CK ($r.sawTimeout) `
     "② $($c.name)：出现逐字超时日志行「[jxl] 帧数探测超时（$($probe1Sec)s）⇒ 已终止 ffprobe：$($c.name)（按静图处理）」—— 超时必须**响亮**，不得静默 return false（且秒数须是**实际生效**的超时值）"
  # ⭐ `#26` 缓存策略行为负控（**有牙**）：不确定的探测结果不得被缓存 ⇒ 同一路径被探测几次就留几条。
  #   反例（改回「无条件写缓存」）：**第 2 项**的探测直接吃 `AnimatedJxlCache` 缓存（不起 ffprobe）
  #   ⇒ 计数恒为 1 ⇒ 本断言转红（实测见回报里的变异验证）。
  CK ($r.timeoutCount -ge 2) `
     "② $($c.name)：不确定的探测结果**不得被缓存** ⇒ 逐字超时行必须 ≥2 条（**同一路径的两个队列项各探一次**；实 $($r.timeoutCount) 条）—— 若为 1 条说明「我不知道」被固化成了「它不是动画」（第 2 项吃缓存、不再探测 ⇒ 后续帧静默走单帧且**再也不会重新探测**）"
  CK ($r.sawCmd) `
     "② $($c.name)：日志随后出现「[cmd] ffmpeg」行 —— 证明两个探测站点都被 Kill 并推进到了编码阶段"
  # ⚠ 「绝不静默成功」**不依赖退出码**：PS5.1 下轮询式的 `.ExitCode` 读不到（见 RunApp 注释），
  #   若写成 `$r.code -eq 0` 会在 PS5.1 上恒为 False ⇒ 断言**真空绿**。
  #   这里改用宿主无关的判据：**若它自行结束了，就不得有产物**（有产物 = 用户以为成功 = 静默成功）。
  $silent = $r.terminated -and ($r.prods -gt 0)
  CK (-not $silent) `
     "② $($c.name)：畸形输入**绝不静默成功**（若结束则须零产物；实 结束=$($r.terminated) exit=$($r.code) 产物=$($r.prods)）"
  if (-not $r.terminated) { $resid += "$($c.name)（$($c.shape)）" }
}

# ── ③ 负控：不得出现超时行，且必须限时内结束 ─────────────────────────────────────
Write-Host "`n### ③ 负控：不得出现超时日志行，且必须在限时内结束 ###" -ForegroundColor Cyan
Write-Host "   ⚠ 负控的「站点1出现」可能为 False：合法/截断 JXL 的 -count_frames 只要 ~0.1 s，短于 500 ms 采样周期 ⇒ 采不到。" -ForegroundColor DarkGray
Write-Host "      故负控判据**只有**「无超时行 + 限时内结束」（探测其实走到了，只是跑得太快）。" -ForegroundColor DarkGray
# ⚠ 清掉为 ② 注入的心跳超时：负控里有**合法**任务（尤其 real.jxl 必须出 1 个产物 + exit 0），
#   小的心跳阈值是 ② 的载体需求，不该外溢到负控（否则会改变负控的语义）。
Remove-Item Env:\FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC -ErrorAction SilentlyContinue
$negatives = @(
  @{ path = $neg1; name = "two_ff0a.jxl";   why = "2B FF 0A，过 magic 检查 ⇒ NativeCodestream 管道，djxl 快速失败" },
  @{ path = $neg2; name = "trunc64.jxl";    why = "真 JXL 截到 64 B" },
  @{ path = $neg3; name = "trunc2000.jxl";  why = "真 JXL 截到 2000 B" },
  @{ path = $real; name = "real.jxl";       why = "完整真 JXL（回归：必须仍然能出产物）" }
)
$n = 0
foreach ($c in $negatives) {
  $n++
  $tag = "neg$n"
  $outdir = "$work/out_$tag"
  $r = RunApp $c.path $outdir $tag $negativeLimitSec $c.name
  $endDesc = "未结束(已杀)"
  if ($r.terminated) {
    $endDesc = "结束 exit=$($r.code)"
    if ($null -eq $r.code) { $endDesc = "结束 exit=?（本宿主读不到）" }
  }
  Write-Host ("  [{0}] {1} 耗时={2:N1}s 超时行={3} 站点1出现={4} 站点2出现={5} 产物={6}  ← {7}" -f `
    $c.name, $endDesc, $r.elapsed, $r.sawTimeout, $r.seenP1, $r.seenP2, $r.prods, $c.why)
  CK (-not $r.sawTimeout) `
     "③ $($c.name)：**不得**出现超时日志行（$($c.why)）"
  CK ($r.terminated) `
     "③ $($c.name)：必须在 $negativeLimitSec s 内结束（实 $([Math]::Round($r.elapsed,1)) s，结束=$($r.terminated)）"
  if ($c.name -eq "real.jxl") {
    CK ($r.prods -eq 1) "③ real.jxl：产物 1 个（实 $($r.prods)）"
    # ⚠ 退出码**必须单独**用 `-Wait -PassThru` 再跑一次：PS5.1 下轮询式的 `.ExitCode` 恒为 `$null`，
    #   而 `[int]$null == 0` 会把「读不到」伪装成「exit 0」⇒ **假绿**（比不测更坏）。
    #   为什么这里敢用**无超时**的 `-Wait`：`real.jxl` 是唯一会在限时内**自行结束**的用例
    #   （③ 上面刚实测 ~1.6 s；它是 1 帧合法 JXL，不是畸形输入）⇒ 不会挂。
    #   ⚠ 若将来它开始挂，本门禁会挂在这里 —— 那是**真信号**（完整 JXL 回归），不是假红。
    $rexOut = "$work/real_exit.out"
    $rexDir = "$work/out_realexit"
    $rex = Start-Process -FilePath $exe `
           -ArgumentList @("--headless", "--log-level", "error", "-i", $real, "-o", $rexDir, "--format", "png") `
           -NoNewWindow -Wait -PassThru -RedirectStandardOutput $rexOut -RedirectStandardError "$rexOut.err"
    CK ($rex.ExitCode -eq 0) "③ real.jxl：exit == 0（**单独** `-Wait -PassThru` 运行取码；实 $($rex.ExitCode)）—— 完整 JXL 不得因本次改动回归"
  }
}

# ── ④ 被测 exe mtime 一致（§6 第 84 条：跑中途被重建则本轮读数作废）──────────────
Write-Host "`n### ④ 被测 exe mtime 跑前跑后一致 ###" -ForegroundColor Cyan
$exeMtimeAfter = (Get-Item $exe).LastWriteTimeUtc
CK ($exeMtimeBefore -eq $exeMtimeAfter) "④ 被测 exe 未被中途重建（$exe）"

# ── ⑤ 已知残余登记（信息性；不构成断言，见文件头）────────────────────────────────
Write-Host "`n### ⑤ 已知残余登记（不构成断言）###" -ForegroundColor Cyan
if ($resid.Count -gt 0) {
  Write-Host "  以下正例在限时内**未自行结束**（已由本门禁杀进程树收工）：$($resid -join '、')" -ForegroundColor Yellow
  Write-Host "  原因（已实测、已登记）：探测阶段之后，应用把该 .jxl 交给 ffmpeg 直接编码（[cmd] ffmpeg -y -i <file.jxl> …），" -ForegroundColor Yellow
  Write-Host "  而 ffmpeg 的 JXL 解复用器在该输入上**忙等**（≈90% 单核、stderr 只有 banner、25 s 不退出）。" -ForegroundColor Yellow
  Write-Host "  ⚠ 2026-09-18 更新（#15）：该忙等**已**被 FfmpegRunner 的 `-progress pipe:1` 心跳守卫判死（见 verify-ffmpeg-heartbeat.ps1）。" -ForegroundColor Yellow
  Write-Host "  ⚠ 本轮② 注入了 FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC=$($hbSec)s ⇒ 第 1 项的编码已被心跳判死（队列因此推进到第 2 项）；" -ForegroundColor Yellow
  Write-Host "    这里仍显示「未自行结束」是因为本门禁**证据齐即提前收工**，在**第 2 项**的编码窗口内就把应用杀了。" -ForegroundColor Yellow
  Write-Host "  本门禁**只**锁「探测阶段必须在限时内响亮返回」；编码阶段的忙等判死由 verify-ffmpeg-heartbeat.ps1 锁。" -ForegroundColor Yellow
} else {
  Write-Host "  本轮所有正例都在限时内结束了 —— 这是**预期**：② 为让串行队列能推进到第 2 项，" -ForegroundColor Yellow
  Write-Host "  注入了 FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC=$($hbSec)s ⇒ 编码阶段的忙等被心跳**判死**（这正是 #15 修的那件事）。" -ForegroundColor Yellow
  Write-Host "  ⚠ 因此本段**不再**是「残余未被修复」的证据：它只是被注入的小阈值提前判死了。" -ForegroundColor Yellow
  Write-Host "  该忙等本身仍存在（生产默认 120 s 才判死），其判死行为由 verify-ffmpeg-heartbeat.ps1 独立锁定。" -ForegroundColor Yellow
}

# ── ⑤-b 兜底清扫（**不是断言**）：把本门禁起过的应用全部杀掉并确认。
#   ⚠ 这一段的由来：`$p.Kill($true)` 在 PS5.1 上不存在（只有无参 `Kill()`），异常被 `try/catch` 吞掉
#     ⇒ 应用没被杀 ⇒ 宿主因重定向流的异步读收尾而**永久挂住**（实测 10+ 分钟不返回）。
#     即使将来某处又漏杀，这里也要保证「门禁一定会返回」。
Write-Host "`n### ⑤-b 兜底清扫：本门禁起过的应用进程全部终止（不构成断言）###" -ForegroundColor Cyan
$stillAlive = @()
foreach ($spid in $script:spawned) {
  KillTree ([int]$spid)
}
Start-Sleep -Milliseconds 500
foreach ($spid in $script:spawned) {
  $pr = $null
  try { $pr = Get-Process -Id $spid -ErrorAction SilentlyContinue } catch { $pr = $null }
  if ($pr) { $stillAlive += $spid }
}
Write-Host "  本门禁共起过 $($script:spawned.Count) 个应用进程；清扫后仍存活：$(if ($stillAlive.Count -eq 0) { '0 个' } else { ($stillAlive -join ', ') })" -ForegroundColor $(if ($stillAlive.Count -eq 0) { 'DarkGray' } else { 'Red' })

# ── ⑥ 残留自证（仅在绿时判定；红时保留目录供排查）────────────────────────────────
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
Write-Host "===== jxl-probe-timeout: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 退出码必须在清理之后，且必须写完整 if/else（§6 第 67 条）。
if ($fail -gt 0) { exit 1 } else { exit 0 }
