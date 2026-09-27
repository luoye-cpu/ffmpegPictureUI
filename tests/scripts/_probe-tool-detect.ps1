# _probe-tool-detect.ps1 —— **外部工具探测**的开销 / 诚实性 / 可注入性探针（2026-09-22 新增）
#
# 为什么需要这条门禁（三个都是实测缺陷，不是假想）：
#   ① 开销：`GetExtendedSearchPaths` 把 `%LOCALAPPDATA%\Programs`、`C:\Program Files` 与
#      **PATH 的每个目录**都列为根，再交给 `FindToolInDirectory` 以 `AllDirectories` 递归。
#      实测本机进程 PATH 里出现过裸 `C:\Users\20210`（用户配置文件根目录）⇒ 递归遍历**永不返回**：
#      60 s 时句柄已 **9 万**、单核满载、RSS 935 MB；一次「PLAN 指向空目录」的降级路径实测挂 **29 分钟**。
#      ⇒ 修法：`ExternalToolsDetector.EnumerateFilesSafe`（显式栈 + **跳过重解析点** + 按目录收口的
#      单根预算 + 子目录不可访问只跳过该目录，不丢整棵子树已收集的命中）。
#   ② 诚实性：`ChooseBestExecutable` 末尾的「探测全失败仍返回 list[0]」会把**已证实起不来**的文件
#      报成可用工具。实测两条真实命中形态：
#        (a) `C:\Program Files\WindowsApps\LinYuSen.HDRImageViewer_...\encoders\x64\djxl.exe`
#            —— 别的包自带的私有二进制，普通令牌 `Process.Start` = **Access Denied**；
#        (b) `C:\Windows\Prefetch\EXIFTOOL.EXE-65513778.pf` —— 被 `*exiftool.exe*` 命中
#            （实测 `*.exe` / `*exiftool*.exe` 都**不**命中，只有无扩展名约束的通配命中）。
#      ⇒ 面板 ✅ 而任务必失败。修法：`ProbeExecutable` 区分「起不来」与「超时」，兜底不得返回起不来的候选。
#   ③ 可注入性：断言「工具缺失 ⇒ 响亮降级」的门禁原先只能置空 `PLAN_DIR`/`JXL_LIB_DIR`，
#      覆盖不到第 ④ 步 ⇒ **判据随本机装过什么第三方应用而翻转**（① 与 ② 合起来就是本轮两条门禁红）。
#      ⇒ 修法：`FFMPEGGUI_EXT_SEARCH_DIRS` 显式收口搜索根（置空=不覆盖，行为与既有一致）。
#
# 断言本体在 ServiceProbe 的 `tooldetect` 分支（那里能直调产品代码；本脚本只做编排 + 汇总）。
# 用法：_probe-tool-detect.ps1 [每根预算秒]（默认 20）
# 退出码：0 = 全绿；1 = 有失败。**必须有 PASS/FAIL 汇总行**（运行器按它分类，静默会被记成"无契约"）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$budget = if ($args.Count -gt 0) { $args[0] } else { "20" }

$pr = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
$fellBack = $false
if (-not (Test-Path $pr)) {
    $pr = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe"; $fellBack = $true
}
# ⚠ 一律打印**被测二进制**（TESTING.md 第 84 条建议 ①：Debug 回退是静默的 ⇒ 读数不可追溯）
Write-Output ("[gate] exe=" + $pr + $(if ($fellBack) { " (Debug fallback)" } else { " (Release)" }))
if (-not (Test-Path $pr)) { Write-Output "FAIL 缺 ServiceProbe（不静默给绿）"; exit 1 }

# 探针的**对照组**需要真能起来的 djxl ⇒ 把 PLAN 指到仓库自带的工具链
$env:FFMPEGGUI_PLAN_DIR    = "$root/publish/PLAN"
$env:FFMPEGGUI_FFMPEG_DIR  = "$root/publish/PLAN/ffmpeg-full"
# ⚠ 刻意**不设** `FFMPEGGUI_EXT_SEARCH_DIRS`：探针的 ③ 自己保存/还原它，外部预设会污染 ③ 的读数。

$so = "$root/tests/output/_probe-tool-detect.out"
$se = "$root/tests/output/_probe-tool-detect.err"
$p = Start-Process -FilePath $pr -ArgumentList @('tooldetect', "$budget") -Wait -NoNewWindow -PassThru `
     -RedirectStandardOutput $so -RedirectStandardError $se
$text = ""
try { $text = [System.IO.File]::ReadAllText($so) + "`n" + [System.IO.File]::ReadAllText($se) } catch { }
$text -split "`r?`n" | Where-Object { $_ -ne "" } | ForEach-Object { Write-Output $_ }

$m = [regex]::Match($text, 'PROBE RESULT: pass=(\d+) fail=(\d+)(?: skip=(\d+))?')
if (-not $m.Success) {
    Write-Output "FAIL 探针没有汇总行（exit=$($p.ExitCode) ⇒ 视为**未完成**，不给绿）"
    Write-Output "===== tool-detect: PASS=0 FAIL=1 SKIP=0 ====="
    exit 1
}
$np = [int]$m.Groups[1].Value; $nf = [int]$m.Groups[2].Value
$ns = 0
if ($m.Groups[3].Success) { $ns = [int]$m.Groups[3].Value }
# ⚠ SKIP 必须**显式出现在汇总里**（本仓既有教训：静默 SKIP = 断言无声消失 = 假绿通道）。
# ── 自清（HANDOVER §3.4 登记的"往 tests/output 根落两个文件、不自清"）────────────────────
# 本脚本用 Start-Process 的 -Redirect 生成 $so/$se ⇒ **不是** PowerShell `*.ps1.err.txt` 那套命名，
# 所以 `verify-png-structure.ps1` 的 `*-out.txt` 清理循环覆盖不到它们（那里刻意排除 `*.err.txt`，
# 而 `verify-cli-strict` 的验收只管 `_*.png` 残留）⇒ 取完读数就自己删。
# ⚠ 位置在两行汇总之后、`exit 0` 之前；且**不参与**任何 `&&` 链（计数型命令命中 0 时退出码是 1，
#   塞进退出链会把"没删干净"变成"本轮判红"，那是另一种假信号）。
Remove-Item -LiteralPath $so, $se -Force -EA SilentlyContinue
Write-Output ("===== tool-detect: PASS=$np FAIL=$nf SKIP=$ns =====")
if ($nf -eq 0 -and $p.ExitCode -eq 0) { exit 0 }
Write-Output "FAIL 探针判红（fail=$nf / exit=$($p.ExitCode)）"
exit 1
