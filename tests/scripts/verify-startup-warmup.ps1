# verify-startup-warmup.ps1 —— 启动期"环境分析 / 预热"的锁（2026-09-23 新增，第 52 条）
#
# 锁的是什么（全部是 2026-09-23 实机复核出来的缺陷，不是假想）：
#   ① **检测了但不报告**：headless 的启动报告只有 6 行且**不含 djxl**，而它前面调的
#     `EnsureAllDetected` 明明检测了 6 个（含 `DjxlService`）。djxl 恰恰决定 JXL 走
#     "原生码流 / JPEG 重构 / GainMap 解码"哪条路，缺失只在任务日志里露一行 ⇒ 报告必须含它。
#     avifenc 两侧都不预热、不报告。
#   ② **空槽位 + 无声消失**：GUI Step1 是「7 槽 / 6 工具 / 4 条 `[detect]` 日志」——
#     第 5 槽是 `efc06221` 删掉 UltrahdrService 时留下的 `Task.Run(() => { try { } catch { } })`，
#     djxl/jxr 两条不打日志，而 6 个 `catch { }` 全吞 ⇒ 某工具探测抛异常时**连"未找到"都不打**。
#   ③ **同一条 5 级路径搜索在一次启动里被跑两遍**：Step1 刚 `ClearCache(); Detect();`，
#     Step3 的 `ProbeAllTools` 又无条件把 5 个 service 的 `Detect()` 各来一遍（含最坏
#     `ExtendedScanTotalBudgetSec = 60 s` 的扩展搜索）。
#   ④ **扫描窗口内的假阴**：`Detect()` 第一行就 `_detected = true; _detectedPath = null;`
#     ⇒ 并发读者拿到"已检测但不可用"，而队列分派正是读这个值决定走哪条路。
#   ⑤ **exiftool 每次调用重扫**：12 处 `if (_detectedPath == null) Detect();` 兜底 ⇒
#     "机器上真没装"时**每次调用**都付一次最多 60 s 的全机扫描。
#   ⑥ **换环境不失效**：`GpuCapabilityService.ClearCache` 全仓 **0 调用点**、
#     `FormatCapabilitiesService._cache` 无失效入口（`SupportedColorSpaces` 还是**累加**语义 ⇒
#     换能力更弱的 ffmpeg 也回不去）、`PlatformServices._planScanned` 是一次性闩；
#     `RedetectTools_Click` 按完不刷状态行 ⇒ 屏幕上还是上一轮的 ✅。
#   ⑦ **声明级数 ≠ 实现级数**：`ExifToolService` 注释写"三优先级"实为五级；`RawService` 漏了
#     artifacts 与 ffmpeg 同目录两级，而它的报错文案让用户往 `PLAN/artifacts/` 放 dngtool。
#     （同族前例：djxl 的「⑤ PATH」曾只有一句注释、没有代码。）
#
# 判据口径：结构锁（读源码文本，确定性、不受时序影响）+ 一次真跑（headless `--dry-run` 的报告行）。
#
# ⚠ 变异验证登记（2026-09-23，**13 例逐条注入 ⇒ 13/13 转红、无空转、无"变异没生效"**）：
#   harness 在会话内临时脚本里跑（**未落库**，故下面把每一例写全，下一个人可照做）：
#     M1  `Mk("djxl"` → `MkX("djxl"`                        ⇒ S2 红（槽位数 6）
#     M2  在 `detectTasks` 首位插回旧的 `Task.Run(() => { try { } catch { } }),` ⇒ S2b 红（空槽 1）
#     M3  `_ = CjxlService.DetectedPath;` → `CjxlService.Detect();` ⇒ S3 红（双扫回归）+ S3b 红
#     M4  去掉 DjxlService 某 getter 的 `lock (_gate)`        ⇒ S4 红（锁数 2 < 3）
#     M4b `DetectCore` → `DetectCoreX`                       ⇒ S4b 红
#     M5  GUI 三处 `, force: true);` → `);`                  ⇒ S5d 红（实 0）
#     M6/M7/M8  分别把 `GpuCapabilityService.ClearCache();` / `FormatCapabilitiesService.ClearCache();` /
#               `PlatformServices.ResetPlanFolderCache();` 全部替换成 `/*mut*/` ⇒ S6 / S6b / S6c 红
#     M9  `RefreshToolsStatusBar();` 全替换 ⇒ S6e 红
#     M10 声明链注释改成「手动路径 → PLAN → PATH」            ⇒ S7 红
#     M11 `Path.Combine(artifactsDir, …)` → `artifactsDirX`   ⇒ S7b「artifacts」那格红（**这条同时证明
#          S7b 不是"构造恒等"**：只改代码形态、注释里的 artifacts 一个字没动）
#     M12 注释掉 `Program.cs` 的 djxl 报告行 **并重建 Release exe** ⇒ S1 红（7≠8）+ S1b「djxl」红
#   ⚠ 两条踩过的坑，照做时别再踩：
#     ① 还原必须走**字节**（`ReadAllBytes`/`WriteAllBytes`）。用 `WriteAllText` 会把
#        `MainWindow.xaml.cs` 的 UTF-8 BOM 吃掉 ⇒ 文本逐字相同、门禁照绿、只有文件字节变了。
#     ② M12 之后要**重建全部四个宿主**并复测四份 `FfmpegGui.dll` 指纹一致，否则下一轮拿陈旧 exe 跑绿。
#
#   ⚠ 行为那部分是**必要的**：光有结构锁会退化成"字符串存在性"，⑤ 那种"函数存在但没人调"的
#     缺陷就抓不住 ⇒ 所以 S8 同时断言"新加的失效入口真的有调用点"。
# 退出码：0 = 全绿。⚠ 结尾必须是完整 if/else（§6 第 67 条：清理步会污染 $LASTEXITCODE）。
# ⚠ 双宿主兼容（PS 7 / 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（用 「」。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_PLAN_DIR   = "$root/publish/PLAN"
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"

$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
$fellBack = $false
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe"; $fellBack = $true }
Write-Output ("[gate] exe=" + $exe + $(if ($fellBack) { " (Debug fallback)" } else { " (Release)" }))
if (-not (Test-Path $exe)) { Write-Output "FAIL 缺被测 exe（不静默给绿）"; exit 1 }

$val = "$root/tests/output/validate"
$out = "$val/suwu_" + [guid]::NewGuid().ToString('N').Substring(0, 8)
New-Item -ItemType Directory -Force -Path $out | Out-Null

$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Host "PASS $msg" -ForegroundColor Green }
  else     { $script:fail++; Write-Host "FAIL $msg" -ForegroundColor Red }
}
function SrcOf([string]$rel) {
  $p = Join-Path $root ("src/FfmpegGui/" + $rel)
  if (-not (Test-Path $p)) { return "" }
  return [System.IO.File]::ReadAllText($p)
}
function CountOf([string]$text, [string]$pattern) {
  if ($text -eq "") { return -1 }
  return @([regex]::Matches($text, $pattern)).Count
}

# ── 行为：headless 跑一次 --dry-run，数报告 ──
$in = "$root/tests/output/sources/src_8bit.png"
$so = "$out/app.out"
$p = Start-Process -FilePath $exe -ArgumentList @('--headless','--dry-run','--log-level','debug','-i',$in,'-o',"$out/run",'--format','jpg') `
     -Wait -NoNewWindow -PassThru -RedirectStandardOutput $so -RedirectStandardError "$out/app.err"
$txt = ""
if (Test-Path $so) { $txt = [System.IO.File]::ReadAllText($so) + [System.IO.File]::ReadAllText("$out/app.err") }
$status = @($txt -split "`r?`n" | Where-Object { $_ -match '^\s{2}\S+: (✅|❌)$' })
Write-Host ("  [读数] headless 退出码=" + $p.ExitCode + "  状态行数=" + $status.Count)

CK ($p.ExitCode -eq 0) "S0 headless --dry-run 退出码 0（实 $($p.ExitCode)）"
CK ($status.Count -eq 8) "S1 headless 启动报告必须 8 行（实 $($status.Count)）—— 与 ProbeAllTools 的 8 项同名同数"
foreach ($name in @('ffmpeg','cjxl','djxl','cjpegli','exiftool','JxrEncApp','avifenc','dngtool')) {
  # 逐行匹配「两个空格 + 名字 + : + ✅/❌」⇒ 名字必须出现在**报告行**里，不是日志任意位置
  $hit = @($status | Where-Object { $_ -match ('^\s{2}' + [regex]::Escape($name) + ': (✅|❌)$') }).Count
  CK ($hit -eq 1) "S1b 报告含 $name（实 $hit 行）"
}

# ── 结构锁 ──
$mains = SrcOf 'MainWindow.xaml.cs'
$dets  = SrcOf 'Services/ExternalToolsDetector.cs'

# ② Step1 的槽位：必须 7 个 Mk(...)、且不存在空任务槽
$mk = CountOf $mains 'Mk\("(cjxl|cjpegli|djxl|exiftool|JxrEncApp|avifenc|dngtool)"'
CK ($mk -eq 7) "S2 GUI Step1 预热槽位 = 7 且逐个有名（实 $mk）—— 旧形态是 7 槽/6 工具/4 条日志"
$empty = CountOf $mains 'Task\.Run\(\(\) => \{ try \{ *\} catch'
CK ($empty -eq 0) "S2b 不得残留空任务槽 `Task.Run(() => { try {} catch {})`（实 $empty；那是删 UltrahdrService 留下的）"
$swallow = CountOf $mains 'catch \{ \} \}\),\s*$'
Write-Host ("  [读数] Step1 内裸 catch 空块形态数 = " + $swallow)

# ③ ProbeAllTools 不得再无条件重扫 5 个 service
# ⚠ 必须带 `(?m)`：[regex]::Matches 默认**不**多行，`^\s*Xxx\.Detect\(\);$` 只会匹配整串开头一行
#   ⇒ 计数恒为 0 ⇒ 这条锁变成"构造恒等"的假绿（本仓纪律：自检要能区分"构造恒等"与"独立参照"）。
$again = CountOf $dets '(?m)^\s*(CjxlService|DjxlService|CjpegliService|JxrService)\.Detect\(\);\s*$'
CK ($again -eq 0) "S3 `ProbeAllTools` 内不得有无条件 `Xxx.Detect();`（实 $again）—— 一条 5 级链一次启动跑两遍"
CK ($dets -match '_ = CjxlService\.DetectedPath;') "S3b 改为读 `DetectedPath`（懒检测）的写法在位"

# ④ 四个 service 的锁
foreach ($svc in @('CjxlService','DjxlService','CjpegliService','JxrService')) {
  $t = SrcOf ("Services/$svc.cs")
  $locks = CountOf $t 'lock \(_gate\)'
  CK ($locks -ge 3) "S4 $svc 的锁罩住 2 个 getter + Detect（`lock (_gate)` 实 $locks ≥ 3）"
  CK ($t -match 'private static void DetectCore\(') "S4b $svc 已拆出 DetectCore（Detect 只做加锁转发）"
}

# ⑤ exiftool 的一次性门控 + 可重扫入口
$et = SrcOf 'Services/ExifToolService.cs'
CK ($et -match 'bool force = false') "S5 exiftool `Detect(log, force)` 门控在位"
CK ($et -match 'if \(!force && _attempted\) return;') "S5b `_attempted` 短路在位（否则 12 处兜底 = 每次调用重扫 60s）"
CK ($et -match 'public static void ClearCache\(\)') "S5c 有 ClearCache ⇒ 用户主动入口能强制重扫"
$forced = CountOf $mains 'ExifToolService\.Detect\(m => \{ if \(LogText != null\) LogText\.Text \+= m; \}, force: true\);'
CK ($forced -ge 3) "S5d GUI 的 3 处用户主动入口都传 force:true（实 $forced）—— 否则门控会把重试吞掉"

# ⑥ 换环境时的失效面
$gpuCall = CountOf $mains 'GpuCapabilityService\.ClearCache\(\);'
CK ($gpuCall -ge 2) "S6 换 ffmpeg 目录 + 点重新检测都会清 GPU 报告（实 $gpuCall ≥ 2；此前全仓 0 调用点）"
$fmtCall = CountOf $mains 'FormatCapabilitiesService\.ClearCache\(\);'
CK ($fmtCall -ge 2) "S6b 格式能力缓存有失效调用（实 $fmtCall ≥ 2；累加语义不清就回不去）"
$planCall = CountOf $mains 'PlatformServices\.ResetPlanFolderCache\(\);'
CK ($planCall -ge 2) "S6c PLAN 一次性闩在「换 ffmpeg 目录」与「重新检测」两处都复位（实 $planCall ≥ 2）"
CK ((SrcOf 'Services/PlatformServices.cs') -match 'public static void ResetPlanFolderCache\(\)') "S6d ResetPlanFolderCache 定义在位"
CK ($mains -match 'RefreshToolsStatusBar\(\);\s*\r?\n\s*RegenerateCommand\(\);') "S6e 重新检测后刷新状态行（否则屏幕停在旧 ✅）"

# ⑦ 声明级数与实现对齐
CK ($et -match '五优先级') "S7 exiftool 注释已改称五级（原写「三优先级」）"
$raw = SrcOf 'Services/RawService.cs'
CK ($raw -match '手动路径 → PLAN → artifacts 目录 → ffmpeg/程序同目录 → PATH') "S7 RawService 的声明链在位（手动→PLAN→artifacts→ffmpeg/程序同目录→PATH）"
# ⚠ 必须逐锁**代码形态**，不能只 `-match 'artifacts'`：本文件通篇注释里就出现 artifacts ⇒
#   整级被删也不红，是"构造恒等"的假绿。⇒ 声明侧（S7）与实现侧（S7b 每一级）分开锁。
$rawLevels = @(
  @{n = 'manual';    p = '!string\.IsNullOrWhiteSpace\(manual\) && File\.Exists\(manual\)' },
  @{n = 'PLAN';      p = 'PlatformServices\.TryFindInPlanFolder\(PlatformServices\.DngToolName\)' },
  @{n = 'artifacts'; p = 'Path\.Combine\(artifactsDir, PlatformServices\.DngToolName\)' },
  @{n = 'ffmpeg 同目录'; p = 'Path\.Combine\(ffmpegDir, PlatformServices\.DngToolName\)' },
  @{n = '程序同目录'; p = 'Path\.Combine\(exeDir, PlatformServices\.DngToolName\)' },
  @{n = 'PATH';      p = 'PlatformServices\.TryFindInPath\(PlatformServices\.DngToolName' }
)
foreach ($lv in $rawLevels) {
  $c = CountOf $raw $lv.p
  CK ($c -eq 1) "S7b RawService 缺「$($lv.n)」这一级就红（该级代码形态实 $c，须 1）"
}

# ── ⑧「起不来 ⇒ 不可用」的收口：形状锁 + **两跑行为锁**（2026-09-23）──
#   背景：avifenc / dngtool 没有独立 Service，可用性一直只判 `!= null` / `File.Exists`
#     ⇒ 落进 WindowsApps 那种 ACL 拒绝启动的 exe 时**面板报 ✅、队列一跑就 InvalidApplication**
#     （且已产出的成品会被半成品清理删掉）。修法是把 `ProbeAndVersion` 已经产生的
#     `LaunchFailed` 结论记进 `PlatformServices` 的备忘录，解析入口与面板都查它。
$ps = SrcOf 'Services/PlatformServices.cs'
$etd = $dets
# ⚠ 计数一律先算进变量再拼消息：`"...$(CountOf $x 'pat\(')..."` 会把引号内的括号交给
#   双引号串的表达式解析 ⇒ ParserError（本行就是踩过之后改成这样的）。
$nMemo   = CountOf $ps 'IsToolUnlaunchable\('
$nRawFil = CountOf $raw 'if \(!Launchable\(cand\)\) continue;'
$nReset  = CountOf $mains 'PlatformServices\.ResetToolLaunchabilityCache\(\);'
CK ($nMemo -ge 2) "S8a 备忘录有 Record/Is/Reset 三件套（PlatformServices 里查询点实 $nMemo ≥ 2）"
CK (($ps -match 'foreach \(var cand in EnumerateAvifencCandidates\(\)\)') -and ($ps -match 'if \(IsToolUnlaunchable\(cand\)\) continue;')) `
   "S8b avifenc 解析入口按级过滤（起不来要续下一级，不是让整个 AVIF 通路消失）"
# ⚠ RawService 的收口必须在 `Detect()` **内部**探测，不能只查备忘录 —— 见该文件注释里那条
#   `_detected` 一次性缓存回流不进去的实测教训（备忘录式修法当时全绿却完全没生效）。
# ⚠ 并且只能用**单次** `CanLaunch`，不许换成 `ProbeExecutable`：后者会依次试 5 组参数 × 2 s
#   （最坏 ~10 s），而 `RawService.IsAvailable` 挂在 UI 线程调用点上
#   （`InitControls()` / `RefreshArtifactsServices()` ⇒ 启动与"重新检测/换目录"）⇒ 那是未声明的冻窗上界。
$rawHeavyProbe = CountOf $raw 'ExternalToolsDetector\.ProbeExecutable\('
CK (($raw -match 'foreach \(var cand in EnumerateCandidates\(\)\)') -and ($nRawFil -eq 1) `
    -and ($raw -match 'ExternalToolsDetector\.CanLaunch\(path\)') `
    -and ($raw -match 'PlatformServices\.RecordToolUnlaunchable\(path\)')) `
   "S8c RawService 在 Detect 里逐候选**验一次启动性**（候选枚举 + 唯一一处 !Launchable + 记反例 + CanLaunch 四形态齐备；!Launchable 实 $nRawFil 须 1）"
CK ($rawHeavyProbe -eq 0) "S8c2 RawService **不得**用 ProbeExecutable 做启动性判定（实 $rawHeavyProbe，须 0）—— 5 组参数×2 s 会冻住 UI 线程上的启动/重新检测"
CK (($etd -match 'if \(probe\.LaunchFailed\) PlatformServices\.RecordToolUnlaunchable\(path\);') -and ($etd -match 'else PlatformServices\.RecordToolLaunchable\(path\);')) `
   "S8d 记录点成对：`LaunchFailed` 记反例、否则撤销旧反例（修好权限后要能自己恢复）"
CK ($nReset -ge 2) "S8e 换目录 / 重新检测都会清备忘录（实 $nReset ≥ 2）"

# 行为锁：唯一候选是垃圾 .exe（非 PE）时必须报 ❌；同一套环境下只把候选换成起得来的真 PE 时必须 ✅。
# 缝用 `FFMPEGGUI_DNGTOOL_PATH`（AppSettingsService 字段级覆盖 ⇒ 确定性）。
# ⚠ 必须把**其余各级都掐成空目录**，否则"收口生效"根本测不出来：headless 报告问的是
#   `RawService.IsAvailable`（不是 ProbeAllTools 那一行），而它的**正确行为**是「跳过坏候选 ⇒ 继续下一级」
#   ⇒ 只要 PLAN 里还有好副本就照样 ✅（实测第一版就是把这条正确行为误判成 bug 报了红）。
#   ⇒ 两跑只让「候选文件起不起得来」这一个变量不同，其余环境逐字相同。
$fexe = "$root\src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe"
$garbage = "$out\not_a_pe.exe"
[System.IO.File]::WriteAllBytes($garbage, [byte[]](0x4D,0x5A,0x00,0x00,0x00,0x00,0x00,0x00))   # 只有 MZ 头，必起不来
$realFf = "$root\publish\PLAN\ffmpeg-full\ffmpeg.exe"
$emptyPlan = "$out/empty_plan"; $emptyArt = "$out/empty_art"; $emptyFf = "$out/empty_ff"
$emptyExt = "$out/empty_ext"
foreach ($d in @($emptyPlan, $emptyArt, $emptyFf, $emptyExt)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
# ⚠ `FFMPEGGUI_EXT_SEARCH_DIRS=''` **不等于**"没有扩展根"：本仓实测过空串/空段不会静默失效
#   （见 `_probe-tool-detect.ps1` 的对应判据），它会退回**真实系统根** ⇒ 这两跑各扫一遍
#   `C:\Program Files` 等，把本门禁从 1.2 s 拖到实测 **417.5 s**。⇒ 必须指到一棵真空目录上。
$isoEnv = [ordered]@{ 'FFMPEGGUI_PLAN_DIR' = $emptyPlan; 'FFMPEGGUI_ARTIFACTS_DIR' = $emptyArt;
                      'FFMPEGGUI_FFMPEG_DIR' = $emptyFf; 'FFMPEGGUI_EXT_SEARCH_DIRS' = $emptyExt }
function Report-Line([string]$envKey, [string]$envVal, [string]$tag) {
  $saved = @{}
  foreach ($k in $isoEnv.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); Set-Item -Path ('Env:' + $k) -Value $isoEnv[$k] }
  $saved[$envKey] = [Environment]::GetEnvironmentVariable($envKey)
  Set-Item -Path ('Env:' + $envKey) -Value $envVal
  $o = "$out/rep_$tag.out"
  try {
    $pp = Start-Process -FilePath $fexe -ArgumentList @('--headless','--dry-run','-i',$in,'-o',"$out/rep_$tag",'--format','jpg') `
         -Wait -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError "$out/rep_$tag.err"
  } catch { Write-Host "  [读数] $tag 起进程失败：$($_.Exception.Message)"; return '' }
  foreach ($k in $saved.Keys) {
    if ($null -ne $saved[$k]) { Set-Item -Path ('Env:' + $k) -Value $saved[$k] } else { Remove-Item ('Env:' + $k) -ErrorAction SilentlyContinue }
  }
  $tx = ''
  if (Test-Path $o) { $tx = [System.IO.File]::ReadAllText($o) + [System.IO.File]::ReadAllText("$out/rep_$tag.err") }
  # ⚠ 不能直接对整篇文本用 `(?m)^  dngtool: (✅|❌)$`：报告行以 **CRLF** 结尾，.NET 的 `$` 只认 `\n`
  #   ⇒ 匹配失败会被读成「行缺失」（本函数第一版就是这么假红的）。先按 `r?`n 切行再逐行判，与 S1 同口径。
  $v = ''
  foreach ($l in ($tx -split "`r?`n")) {
    if ($l -match '^  dngtool: (✅|❌)$') { $v = $Matches[1]; break }
  }
  Write-Host ("  [读数] $tag ⇒ dngtool 行 = " + $(if ($v -ne '') { "dngtool: $v" } else { '<缺失>' }))
  return $v
}
$vGarbage = Report-Line 'FFMPEGGUI_DNGTOOL_PATH' $garbage 'garbage'
CK ($vGarbage -eq '❌') "S8f 行为锁：手工路径指向**起不来的 .exe** ⇒ 面板必须报 dngtool ❌（实得 '$vGarbage'）—— 旧行为是 ✅ + 队列必失败"
if (Test-Path $realFf) {
  $vReal = Report-Line 'FFMPEGGUI_DNGTOOL_PATH' $realFf 'realpe'
  CK ($vReal -eq '✅') "S8g 对照组：把 dngtool 指向一个**起得来**的真 PE（借 ffmpeg.exe）⇒ 必须仍 ✅（实得 '$vReal'）—— 证明收口判的是「起不起得来」而不是「名字认不认识」"
} else {
  Write-Host "  [SKIP] S8g 缺 $realFf ⇒ 对照组不成立（**不给绿**，只跳过）"
}

# ── 汇总 ──
Write-Host ""
Write-Host "===== startup-warmup: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
if ($fail -eq 0) { exit 0 } else { exit 1 }
