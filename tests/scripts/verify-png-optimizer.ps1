# verify-png-optimizer.ps1 —— PNG 出口优化器（oxipng）**端到端判据面**（png-optimizer-integration 批次 2 · T7，2026-11-10 新增）
#
# 背景：批次 1（T2 工具随包 / T3 PngOptimizerService 执行链 / T4 CLI 参数面 / T5 headless 冒烟）与
#   批次 2 T6（ServiceProbe `pngopt` 六 case 判据宿主）已交付；本门禁是 T7 接线——把 T6 的六 case
#   调进清单受管执行，并补三类 ServiceProbe 之外**只能从端到端侧面拿到**的独立断言。
#
# 本门禁锁四件事：
#   §1 六 case 正控（ServiceProbe 六次独立进程）：pixel(8) / ihdr(9) / chunk(12) / argv(4) / off(8) /
#      timeout(6) —— 每条判据一行 `PASS|FAIL pngopt-<case>-NN`；条数**精确锁**（合计 47，不足=静默缩水红）；
#      FAIL>0 红；SKIP>0 红（probe 侧 fail-closed 语义：缺件 = 判不了 = 门禁红，不静默绿）。
#   §2 独立补充断言（ServiceProbe 进程内直调拿不到的端到端面）：
#      a. 压缩比正读数：headless CLI off 档 vs max 档 vs default 档同素材 PNG→PNG，断言 P_max < P_off
#         且 P_default < P_off（oxipng 对 ffmpeg -compression_level 2 直出有大压缩空间，T5 实测 -27.2%；
#         default 档 png-default-oxipng 批 T1 实证）+ default 档像素 md5 等价（oxipng 保守集只重压 IDAT
#         不改像素 ⇒ 与 off 直出参照臂解码帧逐位一致，ffmpeg -f md5 对撞）。
#      b. 播报逐字对撞三类（模板从 PngOptimizerService.cs 逐字抄死，数字段参数化）：
#         成功（headless max 档 stdout 正则 + new<orig；C-DEFOPT@1 后 default 执行档同走分支①，
#         播报「档位 default」模板同批入锁——对撞器档位参数化，两形态都要有牙）；超时（ServiceProbe timeout case 的
#         pngopt-timeout-04 行「」引号内播报原文 == 硬编码模板，逐字符）；工具缺失
#         （FFMPEGGUI_PLAN_DIR/FFMPEGGUI_ARTIFACTS_DIR 指空目录注入 ⇒ ResolveOxipngPath 四路全空 ⇒
#         逐字「未检测到 oxipng」播报 + exit 0 + 产物正常——工具缺失不判任务失败，R5.5）。
#      c. off 负控：①消费 probe off case 三条 sha 读数行（pngopt-off-04/06/08「产物逐字节不变
#         （sha256 对撞）」——按代码实况，零播报行是 03/05/07）；②headless off 档 stdout/stderr/logfile
#         零 `^\[pngopt\]` 行（Debug 级都没有才是真零播报）+ 同输入重复跑 sha256 稳定。
#      d. 旧预设回放（2026-11-11 beta12 后审查修复批 C3）：旧 schema 预设 JSON（**无 PngOptimizer 键**）
#         回放 ⇒ 档位 off（零漂移 = 接入 oxipng 前的行为），且必须与「显式 max 预设臂」可区分；
#         不传参的默认仍是 max（新默认不变）。落盘位置 = exe 同级 presets/（产品便携约定），
#         用完在 finally 删除并回收空目录。
#      a-clean. C1（去整文件副本）清理保证：run-dir 内零 `pngopt-tmp/out` 临时件残留。
#   §3 -SelfCheck 负控（内存变异，不跑真实进程，秒级）：四个变异各打各的判据——①伪 stdout 注入
#      FAIL 行 ⇒ 解析器必须检出；②伪 stdout 条数少一条 ⇒ 精确计数锁必须红；③播报对撞器喂篡改
#      文案 ⇒ 必须红；④SKIP 行注入 ⇒ fail-closed 必须红。$ncOk = 四变异全被检出。
#
# 素材：全部现场自造（ffmpeg lavfi testsrc2 512x512 RGBA），不消费 benchmark 轮次产物（AGENTS §3）。
# 时长：timeout case 真跑 600s（24MP 噪声 × zopfli30，T6 实证确定性触发）⇒ 单跑总时长 ~620-680s，
#   在 runner 900s 单步预算内。
# ⚠ `-SkipTimeoutCase` **只供调试/并行取证**：跳过 timeout case（真跑 600s）与依赖它的 §2b2 播报对撞。
#   受管运行 `_run-step3-gates.ps1` 只给本脚本传 `-SelfCheck` ⇒ 恒跑 timeout case；**不得**把本开关
#   写进运行器（否则 600s 超时分支失去唯一的端到端判据）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（用 「」）。
param([switch]$SelfCheck, [switch]$SkipTimeoutCase)
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
# env 钉扎（照 verify-gainmap-jxl；FFMPEGGUI_FFMPEG_DIR/FFMPEGGUI_PLAN_DIR 让 probe 与 headless 同源解析）
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$env:FFMPEGGUI_PLAN_DIR = "$root/publish/PLAN"
$env:LC_ALL = $null
$gid = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = "$root/tests/output/pngopt-gate/$gid"
New-Item -ItemType Directory -Force -Path $work | Out-Null
# TMP 钉到产物区 round-tmp（AGENTS §3：子进程继承，零 %TEMP%；Windows 风格绝对路径）
$tmpRoot = "$root/tests/output/_tmp/round-tmp"
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
$env:TMP = $tmpRoot; $env:TEMP = $tmpRoot; $env:TMPDIR = $tmpRoot

# ⚠ 只用 Release 产物（本门禁是行为断言；异构/陈旧二进制的读数不可信）
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
$pr  = "$root/tests/projects/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$oxi = "$root/publish/PLAN/artifacts/oxipng.exe"
$pngcicp = "$root/tools/src/pngcicp/bin/Release/net11.0/pngcicp.exe"
$exiftool = "$root/publish/PLAN/exiftool/exiftool.exe"
$iccSrc = "$root/tests/output/corpus/gainmap-iso21496/DSC00037.jpg"

$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
  else     { $script:fail++; Write-Output "  FAIL $msg" }
}
# 遗留孙进程会继承重定向句柄（AGENTS：ReadAllText 抛 IOException ⇒ summary=False 假红）
# ⇒ 容错读：普通读失败就等 200ms 重试，末次用 FileShare.ReadWrite 强读。
function Read-TextShared([string]$path) {
  for ($i = 0; $i -lt 10; $i++) {
    try { return [System.IO.File]::ReadAllText($path) } catch { Start-Sleep -Milliseconds 200 }
  }
  $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
  try { $sr = New-Object System.IO.StreamReader($fs); return $sr.ReadToEnd() } finally { $fs.Dispose() }
}
function ExecOut([string]$file, [string[]]$argList, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argList -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = (Read-TextShared $o); outPath = $o; errPath = $e }
}
function Sha256Hex([string]$path) {
  if (-not (Test-Path $path)) { return "<缺文件>" }
  return (Get-FileHash $path -Algorithm SHA256).Hash.ToUpper()
}
# ServiceProbe pngopt 六 case 的运行期判据条数（C-PROBE@1；与 verify-probe-mirror-parity 的
# 源码调用点锁 29 互补——本表锁的是**运行时展开条数**，两把尺方向不同）。
$caseOrder = @('pixel', 'ihdr', 'chunk', 'argv', 'off', 'timeout')
$caseExpect = @{ pixel = 8; ihdr = 9; chunk = 12; argv = 4; off = 8; timeout = 6 }
$caseTotal = 47
if ($SkipTimeoutCase) {
  # 调试/并行取证臂（受管运行不传）：timeout case 真跑 600s ⇒ 既吃满单步预算，又会在并行线重建时
  # 干扰 §4 的「exe/probe mtime 起轮前后一致」。跳过时精确计数锁同步收缩为 41（8+9+12+4+8）。
  $caseOrder = @('pixel', 'ihdr', 'chunk', 'argv', 'off')
  $caseTotal = 41
  Write-Output "⚠ -SkipTimeoutCase 调试臂：timeout case（真跑 600s）与 §2b2 播报对撞未执行（受管运行恒全跑）"
}
# ServiceProbe pngopt 单 case 的解析器（正向臂与 §3 内存变异负控**共用同一判读链**——
# 变异测的就是真实判读逻辑，不是复制粘贴的第二份）。
function Read-PngoptRun([string]$text, [string]$case) {
  $lines = @($text -split "`r?`n")
  $caseLines = @()
  $failLines = @()
  $skipLines = @()
  $probeLine = ""
  $pPass = -1; $pFail = -1; $pSkip = -1
  foreach ($ln in $lines) {
    if ($ln -match ('^(?:PASS|FAIL) pngopt-' + $case + '-\d{2} ')) { $caseLines += $ln }
    if ($ln -match '^FAIL ') { $failLines += $ln }
    if ($ln -match '^SKIP ') { $skipLines += $ln }
    if ($ln -match '^PROBE RESULT: pass=(\d+) fail=(\d+)(?: skip=(\d+))?') {
      $probeLine = $ln; $pPass = [int]$Matches[1]; $pFail = [int]$Matches[2]
      if ($Matches.Count -ge 4 -and $Matches[3]) { $pSkip = [int]$Matches[3] } else { $pSkip = 0 }
    }
  }
  return @{ caseLines = $caseLines; failLines = $failLines; skipLines = $skipLines; probeLine = $probeLine; pPass = $pPass; pFail = $pFail; pSkip = $pSkip }
}
# 成功播报对撞器（模板从 PngOptimizerService.cs:162 逐字抄死，数字段参数化、档位参数化——
#   C-DEFOPT@1 后 max|default 两执行档都会走到分支①，两形态都要有牙）：
#   log($"[pngopt] 已压缩（档位 {m}）：{origSize} → {newSize} 字节（-{pct:F1}%，耗时 {sw.Elapsed.TotalSeconds:F1}s）")
$reSuccessFmt = '^\[pngopt\] 已压缩（档位 {0}）：(\d+) → (\d+) 字节（-(\d+\.\d)%，耗时 (\d+\.\d)s）\s*$'
function Test-SuccessBroadcast([string[]]$lines, [string]$mode = 'max') {
  $re = [string]::Format($script:reSuccessFmt, $mode)
  foreach ($ln in $lines) {
    if ($ln -match $re) {
      return @{ ok = $true; orig = [int64]$Matches[1]; new = [int64]$Matches[2]; pct = [double]$Matches[3]; line = $ln }
    }
  }
  return @{ ok = $false; orig = -1; new = -1; pct = -1.0; line = "" }
}
# headless 臂调用器：--log-level Debug 必须（T5 实证：Info 级收尾播报无落盘通道，Debug 级
# OnItemProgress 实时转储到 stdout）；播报判读一律以捕获的 stdout 为准（--log-file 不收 [pngopt] 行）。
function RunHeadless([string]$tag, [string]$mode, [hashtable]$envOverride) {
  $outDir = "$work/out_$tag"
  if (Test-Path $outDir) { [System.IO.Directory]::Delete($outDir, $true) }
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  $logF = "$work/$tag.logfile"
  $saved = @{}
  foreach ($k in $envOverride.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $envOverride[$k]) }
  try {
    $p = Start-Process -FilePath $script:exe `
      -ArgumentList @('--headless', '--log-level', 'Debug', '--log-file', $logF, '-i', $script:src, '--format', 'png', '--output', $outDir, '--png-optimizer', $mode) `
      -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$work/$tag.stdout" -RedirectStandardError "$work/$tag.stderr"
  } finally {
    foreach ($k in $envOverride.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
  }
  $prods = @(Get-ChildItem $outDir -Recurse -File -ErrorAction SilentlyContinue)
  $prod = $null
  if ($prods.Count -ge 1) { $prod = $prods[0] }
  return @{ code = $p.ExitCode; stdout = (Read-TextShared "$work/$tag.stdout"); stderr = (Read-TextShared "$work/$tag.stderr"); logFile = $logF; prod = $prod; prodCount = $prods.Count }
}
# 显式附加参数版 headless 臂（§2d 旧预设回放用：--preset / --png-optimizer 组合不同，不能沿用 RunHeadless
# 的固定 --png-optimizer 形态）。进程/日志约定与 RunHeadless 相同；额外回报临时件残留数。
function RunHeadlessArgs([string]$tag, [string[]]$extraArgs) {
  $outDir = "$work/out_$tag"
  if (Test-Path $outDir) { [System.IO.Directory]::Delete($outDir, $true) }
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  $logF = "$work/$tag.logfile"
  $p = Start-Process -FilePath $script:exe `
    -ArgumentList (@('--headless', '--log-level', 'Debug', '--log-file', $logF, '-i', $script:src, '--format', 'png', '--output', $outDir) + $extraArgs) `
    -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$work/$tag.stdout" -RedirectStandardError "$work/$tag.stderr"
  $prods = @(Get-ChildItem $outDir -Recurse -File -ErrorAction SilentlyContinue)
  $prod = $null
  if ($prods.Count -ge 1) { $prod = $prods[0] }
  return @{ code = $p.ExitCode; stdout = (Read-TextShared "$work/$tag.stdout"); stderr = (Read-TextShared "$work/$tag.stderr"); logFile = $logF; prod = $prod; prodCount = $prods.Count }
}

# ── §0 前置 fail-closed（缺件即红并点名，不静默给绿） ─────────────────────────────
Write-Output "`n### 0) 前置自检（fail-closed） ###"
$missing = @()
foreach ($need in @($exe, $pr, $ff, $oxi, $pngcicp, $exiftool, $iccSrc)) { if (-not (Test-Path $need)) { $missing += $need } }
if ($missing.Count -gt 0) {
  Write-Host "  ❌ 缺工具/产物/语料（本门禁不做 Debug 回退，也不静默给绿）：" -ForegroundColor Red
  $missing | ForEach-Object { Write-Output "    $_" }
  if ($missing -contains $iccSrc) {
    Write-Output "    ↳ ICC 语料缺失：按 AGENTS §7 取回（第三方实拍件，许可见 verify-gainmap-engine §13；落在工作区内 tests/output/corpus/，整树 gitignore 永不随仓分发）"
  }
  try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
  exit 1
}
CK ($true) "§0 前置件齐（FfmpegGui.exe/ServiceProbe.exe/ffmpeg/oxipng 惯例位/pngcicp/exiftool/ICC 语料）"
$mtExe = (Get-Item $exe).LastWriteTime
$mtPr = (Get-Item $pr).LastWriteTime
Write-Output "  exe=$exe"
Write-Output "  exe-mtime=$mtExe / probe-mtime=$mtPr"

# ── 素材自造（512x512 testsrc2 RGBA，-compression_level 0 大文件给 oxipng 留压缩空间） ──
$src = "$work/src-512.png"
& $ff -y -hide_banner -loglevel error -f lavfi -i "testsrc2=s=512x512:rate=1" -frames:v 1 -pix_fmt rgba -c:v png -compression_level 0 $src 2> "$work/src.err"
$srcOk = (Test-Path $src) -and ((Get-Item $src).Length -gt 0)
CK ($srcOk) "§0 素材 512x512 testsrc2 RGBA 生成（$((Get-Item $src -ErrorAction SilentlyContinue).Length) B，compression_level 0）"
if (-not $srcOk) {
  Write-Output "  素材生成失败 ⇒ §2 端到端臂无法执行；§1 六 case 仍可跑（probe 自造素材）"
}

# ── §1 六 case 正控（六次独立进程；timeout case 真跑 600s 放最后） ────────────────
Write-Output "`n### 1) ServiceProbe pngopt 六 case ###"
$runResults = @{}
$grandCount = 0
foreach ($case in $caseOrder) {
  $r = ExecOut $pr @('pngopt', $case) "sp_$case"
  $runResults[$case] = $r
  $parsed = Read-PngoptRun $r.text $case
  $exp = [int]$caseExpect[$case]
  $grandCount += $parsed.caseLines.Count
  Write-Output ("  [case {0}] exit={1} pass={2} fail={3} skip={4} prefixLines={5} (expect {6})" -f $case, $r.code, $parsed.pPass, $parsed.pFail, $parsed.pSkip, $parsed.caseLines.Count, $exp)
  CK ($r.code -eq 0) "§1 $case exit=0（实 $($r.code)）"
  CK (($parsed.probeLine -ne "") -and ($parsed.pPass -eq $exp) -and ($parsed.pFail -eq 0)) "§1 $case PROBE RESULT == pass=$exp fail=0（实 pass=$($parsed.pPass) fail=$($parsed.pFail)，行「$($parsed.probeLine)」）"
  CK ($parsed.failLines.Count -eq 0) "§1 $case FAIL 行 = 0（实 $($parsed.failLines.Count)）"
  CK ($parsed.skipLines.Count -eq 0) "§1 $case SKIP 行 = 0（fail-closed：缺件=判不了=门禁红；实 $($parsed.skipLines.Count)）"
  CK ($parsed.caseLines.Count -eq $exp) "§1 $case pngopt-<case>-NN 前缀行精确 $exp 条（实 $($parsed.caseLines.Count)；条数不足=静默缩水）"
}
CK ($grandCount -eq $caseTotal) "§1 六 case 判据条数合计 == $caseTotal（pixel 8 / ihdr 9 / chunk 12 / argv 4 / off 8 / timeout 6；实 $grandCount）"

# ── §2 独立补充断言（端到端侧面） ─────────────────────────────────────────────────
Write-Output "`n### 2) 独立补充断言 ###"
if ($srcOk) {
  # a. 压缩比正读数：off 档 vs max 档同素材（headless PNG 编码 -compression_level 2 直出，
  #    oxipng max 档对它有大压缩空间；T5 实测 B04 -27.2%）
  $rOffA = RunHeadless 'off_a' 'off' @{}
  $rMaxA = RunHeadless 'max_a' 'max' @{}
  $pOff = 0; $pMax = 0
  if ($null -ne $rOffA.prod) { $pOff = $rOffA.prod.Length }
  if ($null -ne $rMaxA.prod) { $pMax = $rMaxA.prod.Length }
  CK (($rOffA.code -eq 0) -and ($null -ne $rOffA.prod) -and ($pOff -gt 0)) "§2a off 档 exit=0 且产物非空（exit=$($rOffA.code)，$($rOffA.prodCount) 件 $pOff B）"
  CK (($rMaxA.code -eq 0) -and ($null -ne $rMaxA.prod) -and ($pMax -gt 0)) "§2a max 档 exit=0 且产物非空（exit=$($rMaxA.code)，$($rMaxA.prodCount) 件 $pMax B）"
  CK (($pOff -gt 0) -and ($pMax -gt 0) -and ($pMax -lt $pOff)) "§2a 压缩比正读数 P_max < P_off（$pMax < $pOff，-$([math]::Round((1 - $pMax / $pOff) * 100, 1))%）"

  # a-def. default 档端到端（png-default-oxipng 批 C-GATE-U@1①）：同素材 default 档出口四判据——
  #     exit=0 产物非空 ｜ 像素 md5 等价（oxipng 保守集 --nb --nc --np --ng --interlace keep 只重压
  #     IDAT 不改像素 ⇒ 与 off 直出参照臂解码帧逐位一致）｜ 压缩比正读数 P_default < P_off ｜
  #     播报「档位 default」逐字模板命中 + 数字自洽 new<orig。
  $rDefA = RunHeadless 'def_a' 'default' @{}
  $pDef = 0
  if ($null -ne $rDefA.prod) { $pDef = $rDefA.prod.Length }
  CK (($rDefA.code -eq 0) -and ($null -ne $rDefA.prod) -and ($pDef -gt 0)) "§2a-def default 档 exit=0 且产物非空（exit=$($rDefA.code)，$($rDefA.prodCount) 件 $pDef B）"
  $md5Off = ""; $md5Def = ""
  if ($null -ne $rOffA.prod) { $md5Off = (@(& $ff -hide_banner -loglevel error -i $rOffA.prod.FullName -f md5 - 2>$null) | Select-Object -First 1) }
  if ($null -ne $rDefA.prod) { $md5Def = (@(& $ff -hide_banner -loglevel error -i $rDefA.prod.FullName -f md5 - 2>$null) | Select-Object -First 1) }
  CK (($md5Off -ne "") -and ($md5Off -eq $md5Def)) "§2a-def default 档像素 md5 等价（off 参照 $md5Off == default $md5Def）"
  CK (($pDef -gt 0) -and ($pOff -gt 0) -and ($pDef -lt $pOff)) "§2a-def 压缩比正读数 P_default < P_off（$pDef < $pOff，-$([math]::Round((1 - $pDef / $pOff) * 100, 1))%）"
  $defLines = @($rDefA.stdout -split "`r?`n")
  $hitDef = Test-SuccessBroadcast $defLines 'default'
  CK ($hitDef.ok -and ($hitDef.new -lt $hitDef.orig)) "§2a-def 播报「档位 default」模板命中 + 数字自洽 new<orig（「$($hitDef.line)」）"

  # b1. 成功播报逐字对撞（headless max 档 stdout；模板见 $reSuccess 注释）
  $maxLines = @($rMaxA.stdout -split "`r?`n")
  $hit = Test-SuccessBroadcast $maxLines
  CK ($hit.ok) "§2b1 成功播报正则命中（「$($hit.line)」）"
  CK ($hit.ok -and ($hit.new -lt $hit.orig)) "§2b1 播报数字自洽 new<orig（$($hit.new) < $($hit.orig)，-$($hit.pct)%）"

  # b2. 超时播报逐字对撞：ServiceProbe timeout case stdout 的 pngopt-timeout-04 行
  #     「」引号内播报原文 == 门禁硬编码模板（PngOptimizerService.cs:133 逐字，m=zopfli30/600s 展开）
  if (-not $SkipTimeoutCase) {
    $timeoutMsg = '[pngopt] ⚠️ 优化超时（600s 上限），保留原样（档位 zopfli30；大图建议改用 max 档）'
    $tLines = @($runResults['timeout'].text -split "`r?`n")
    $t04 = ""
    foreach ($ln in $tLines) { if ($ln -match '^PASS pngopt-timeout-04 ') { $t04 = $ln; break } }
    CK ($t04 -ne "") "§2b2 pngopt-timeout-04 行在且 PASS（「$t04」）"
    $quoted = ""
    if ($t04 -match '「([^」]+)」') { $quoted = $Matches[1] }
    CK ($quoted -eq $timeoutMsg) "§2b2 超时播报原文逐字 == 硬编码模板（实「$quoted」；len 实$($quoted.Length) / 模板$($timeoutMsg.Length)）"
  } else {
    Write-Output "  §2b2 调试臂：跳过 timeout 播报对撞（-SkipTimeoutCase；受管运行不传此开关 ⇒ 恒真跑）"
  }

  # b3. 工具缺失臂：FFMPEGGUI_PLAN_DIR/FFMPEGGUI_ARTIFACTS_DIR 指空目录 ⇒ ResolveOxipngPath 四路全空
  #     （①显式键：settings 无 PngOptimizerPath 实测空；②artifacts：env 空目录盖住用户 settings 的
  #     publish/PLAN/artifacts；③PLAN：env 空目录盖住 exe 同目录 PLAN/；④ffmpeg 同目录：ffmpeg-full
  #     实测无 oxipng.exe）。播报逐字 + exit 0 + 产物正常（R5.5：工具缺失不判任务失败，但必须出声）。
  $emptyPlan = "$work/empty-plan"
  New-Item -ItemType Directory -Force -Path $emptyPlan | Out-Null
  $noLeak = (@(Get-ChildItem $emptyPlan -Recurse -Filter 'oxipng*' -ErrorAction SilentlyContinue).Count -eq 0)
  CK ($noLeak) "§2b3 空目录注入前提自证（$emptyPlan 内无 oxipng*）"
  if ($noLeak) {
    $rMiss = RunHeadless 'miss' 'max' @{ 'FFMPEGGUI_PLAN_DIR' = $emptyPlan; 'FFMPEGGUI_ARTIFACTS_DIR' = $emptyPlan; 'FFMPEGGUI_FFMPEG_DIR' = "$root/publish/PLAN/ffmpeg-full" }
    $missMsg = '[pngopt] 未检测到 oxipng，已跳过优化（期望位置 PLAN/artifacts/oxipng.exe，或在设置中指定路径）'
    $missHit = $false
    foreach ($ln in (@($rMiss.stdout -split "`r?`n"))) { if ($ln -match ('^' + [regex]::Escape($missMsg) + '\s*$')) { $missHit = $true; break } }
    CK ($missHit) "§2b3 工具缺失播报逐字命中（env PLAN_DIR+ARTIFACTS_DIR=空目录 ⇒ 四路解析全空）"
    CK ($rMiss.code -eq 0) "§2b3 工具缺失不判任务失败 exit=0（实 $($rMiss.code)；R5.5）"
    CK (($null -ne $rMiss.prod) -and ($rMiss.prod.Length -gt 0)) "§2b3 产物正常非空（$($rMiss.prodCount) 件 $($rMiss.prod.Length) B）"
  } else {
    CK ($false) "§2b3 空目录注入前提自证失败（空目录内出现 oxipng*，env 注入不可信）"
  }

  # c. off 负控
  # c1. 消费 probe off case 三条 sha 读数行（pngopt-off-04/06/08；零播报行是 03/05/07——
  #     idx 从 3 起、每形态先零播报后 sha，按 Program.cs PngOptCaseOff 实况）
  $offText = $runResults['off'].text
  foreach ($n in @('04', '06', '08')) {
    $m = [regex]::Match($offText, '(?m)^PASS pngopt-off-' + $n + ' .*sha256 对撞')
    CK ($m.Success) "§2c1 probe off case sha 读数行 pngopt-off-$n 在且 PASS（sha256 对撞）"
  }
  # c2. headless off 档：stdout/stderr/logfile 三处零 ^\[pngopt\] 行（Debug 级都没有才是真零播报；
  #     行首锚定防工作目录名 pngopt-gate 撞串假红）+ 同输入重复跑 sha256 稳定
  $rOffB = RunHeadless 'off_b' 'off' @{}
  $pngoptZero = $true
  foreach ($rr in @($rOffA, $rOffB)) {
    foreach ($txt in @($rr.stdout, $rr.stderr)) {
      if ($txt -and ($txt -match '(?m)^\[pngopt\]')) { $pngoptZero = $false }
    }
    if (Test-Path $rr.logFile) {
      $lg = Read-TextShared $rr.logFile
      if ($lg -match '(?m)^\[pngopt\]') { $pngoptZero = $false }
    }
  }
  CK ($pngoptZero) "§2c2 headless off 两臂 stdout/stderr/logfile 零 ^[pngopt] 行（Debug 级都没有=真零播报）"
  $shaA = ""; $shaB = ""
  if ($null -ne $rOffA.prod) { $shaA = Sha256Hex $rOffA.prod.FullName }
  if ($null -ne $rOffB.prod) { $shaB = Sha256Hex $rOffB.prod.FullName }
  CK (($shaA -ne "") -and ($shaA -eq $shaB)) "§2c2 off 档同输入重复跑 sha256 稳定（$($shaA.Substring(0, [math]::Min(16, $shaA.Length)))… == $($shaB.Substring(0, [math]::Min(16, $shaB.Length)))…）"

  # a-clean. C1（2026-11-11 beta12 后审查修复批：去整文件副本）的清理保证 —— run-dir 内不得残留
  #   pngopt-tmp/out 临时件（旧实现每张 PNG 产 tmpIn + tmpOut 两件；新实现只有 tmpOut 且 finally 删）
  $tmpLeftover = @(Get-ChildItem $work -Recurse -File -ErrorAction SilentlyContinue |
                   Where-Object { $_.Name -match '\.pngopt-(tmp|out)-' })
  CK ($tmpLeftover.Count -eq 0) "§2a-clean run-dir 零 pngopt 临时件残留（实 $($tmpLeftover.Count) 件：$(($tmpLeftover | ForEach-Object { $_.Name }) -join ', ')）"

  # d. 旧预设回放（C3，2026-11-11 beta12 后审查修复批）：
  #    旧 schema 预设 JSON（**无 PngOptimizer 键**）⇒ 回放后档位 = off（零漂移：等价于接入 oxipng 之前），
  #    必须与「显式 max 预设臂」可区分；不传参的默认仍为 max（新默认不变）。
  #    落盘位置 = exe 同级 presets/（产品便携约定的唯一预置目录，PresetManagerService.PresetsDir），
  #    用完在 finally 删除；目录空则回收（不污染 bin）。
  $presetDir = Join-Path (Split-Path $exe) 'presets'
  $presetName = "pngopt-gate-old-$gid"
  $presetFile = Join-Path $presetDir "$presetName.json"
  $presetMade = $false
  try {
    New-Item -ItemType Directory -Force -Path $presetDir | Out-Null
    # 旧预设：png 管线常见轴齐全、但**故意没有 PngOptimizer 键**（= beta12 之前导出的预设形态）
    [IO.File]::WriteAllText($presetFile,
      '{"Format":"PNG","Quality":92,"Chroma":"auto","BitDepth":"auto","ColorSpace":"auto","MetadataMode":"PreserveAll","AutoThreads":true,"Concurrency":2,"MaxQueueSize":16}')
    $presetMade = Test-Path $presetFile
  } catch {
    $presetMade = $false
    try { if (Test-Path $presetFile) { Remove-Item $presetFile -Force -ErrorAction SilentlyContinue } } catch { }
  }
  CK $presetMade "§2d 前提：旧预设 JSON 已落 exe 同级 presets/（$presetFile）"
  if ($presetMade) {
    try {
      $presetText = Read-TextShared $presetFile
      CK (-not ($presetText -match '"PngOptimizer"')) "§2d 前提：旧预设 JSON 无 PngOptimizer 键（字段缺席，不是显式 null）"
      $rP  = RunHeadlessArgs 'c3_old'     @('--preset', $presetName)
      $rPo = RunHeadlessArgs 'c3_old_off' @('--preset', $presetName, '--png-optimizer', 'off')
      $rPm = RunHeadlessArgs 'c3_old_max' @('--preset', $presetName, '--png-optimizer', 'max')
      $rNd = RunHeadlessArgs 'c3_noarg'   @()
      $pOldB = 0; if ($null -ne $rP.prod)  { $pOldB = $rP.prod.Length }
      $pOffB = 0; if ($null -ne $rPo.prod) { $pOffB = $rPo.prod.Length }
      $pMaxB = 0; if ($null -ne $rPm.prod) { $pMaxB = $rPm.prod.Length }
      $pNoB  = 0; if ($null -ne $rNd.prod) { $pNoB  = $rNd.prod.Length }
      $oldBroadcast = @($rP.stdout -split "`r?`n" | Where-Object { $_ -match '^\[pngopt\]' }).Count
      CK (($rP.code -eq 0) -and ($null -ne $rP.prod) -and ($pOldB -gt 0)) "§2d 旧预设臂 exit=0 且产物非空（exit=$($rP.code)，$($rP.prodCount) 件 $pOldB B）"
      CK ($oldBroadcast -eq 0) "§2d 旧预设字段缺席 ⇒ off（零 ^[pngopt] 行；实 $oldBroadcast 行）"
      $shaOld = ""; if ($null -ne $rP.prod)  { $shaOld = Sha256Hex $rP.prod.FullName }
      $shaOldOff = ""; if ($null -ne $rPo.prod) { $shaOldOff = Sha256Hex $rPo.prod.FullName }
      CK (($shaOld -ne "") -and ($shaOld -eq $shaOldOff)) "§2d 旧预设臂产物 == 显式 off 臂产物（零漂移，sha256 对撞 ${shaOld}）"
      $pmLines = @($rPm.stdout -split "`r?`n")
      $pmHit = Test-SuccessBroadcast $pmLines 'max'
      $shaOldMax = ""; if ($null -ne $rPm.prod) { $shaOldMax = Sha256Hex $rPm.prod.FullName }
      CK ($pmHit.ok -and ($pMaxB -gt 0) -and ($pOldB -gt 0) -and ($pMaxB -lt $pOldB) -and ($shaOld -ne $shaOldMax)) `
        "§2d 显式 max 预设臂仍走 max（播报命中 + 产物 $pMaxB B < 旧预设臂 $pOldB B + sha 不同）⇒ 两臂可区分"
      $ndLines = @($rNd.stdout -split "`r?`n")
      $ndHit = Test-SuccessBroadcast $ndLines 'max'
      CK ($ndHit.ok -and ($pNoB -gt 0) -and ($pOffB -gt 0) -and ($pNoB -lt $pOffB)) `
        "§2d 不传参默认仍为 max（新默认不变：播报命中 + 产物 $pNoB B < off $pOffB B）"
    } finally {
      if (Test-Path $presetFile) { Remove-Item $presetFile -Force -ErrorAction SilentlyContinue }
      try {
        if ((Get-ChildItem $presetDir -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) {
          Remove-Item $presetDir -Force -ErrorAction SilentlyContinue
        }
      } catch { }
    }
  } else {
    CK ($false) "§2d 旧预设夹具落盘失败（presets/ 不可写）⇒ 本条无法判（fail-closed）"
  }
} else {
  CK ($false) "§2 素材生成失败 ⇒ 端到端补充断言整段无法执行（fail-closed 红；§1 六 case 已独立判读）"
}

# ── §3 -SelfCheck 负控（内存变异，不跑真实进程，秒级） ───────────────────────────
# 形态对齐 verify-color-out-matrix / verify-ui-param-matrix：$ncOk 初值 true + if ($SelfCheck) 内重算；
# 四个变异各打各的判据，共用 §1 的 Read-PngoptRun / Test-SuccessBroadcast 判读链 ⇒ 变异测的是真牙。
Write-Output ""
Write-Output "===== -SelfCheck negative-control arm (memory mutations, must all be DETECTED) ====="
$ncOk = $true
if ($SelfCheck) {
    # 变异①：全绿伪 stdout 注入一行 FAIL ⇒ 解析器必须检出（FailLines>0）
    $good8 = @(1..8 | ForEach-Object { "PASS pngopt-pixel-0$_ 档位 max 合成读数 $_" })
    $fake1 = ($good8 -join "`n") + "`nFAIL pngopt-pixel-99 注入的伪失败行`nPROBE RESULT: pass=8 fail=1"
    $p1 = Read-PngoptRun $fake1 'pixel'
    $nc1 = ($p1.failLines.Count -ge 1)
    CK $nc1 "selfcheck 变异① 伪 FAIL 行注入被解析器检出（failLines=$($p1.failLines.Count)）"
    if (-not $nc1) { $ncOk = $false }

    # 变异②：伪 stdout 条数少一条（7 条 + pass=7）⇒ 精确计数锁必须红（7 ≠ 8）
    $good7 = @(1..7 | ForEach-Object { "PASS pngopt-ihdr-0$_ 档位 max 合成读数 $_" })
    $fake2 = ($good7 -join "`n") + "`nPROBE RESULT: pass=7 fail=0"
    $p2 = Read-PngoptRun $fake2 'ihdr'
    $nc2 = ($p2.caseLines.Count -ne [int]$caseExpect['ihdr'])
    CK $nc2 "selfcheck 变异② 条数少一条被精确计数锁检出（实 $($p2.caseLines.Count) ≠ 期 $($caseExpect['ihdr'])）"
    if (-not $nc2) { $ncOk = $false }

    # 变异③：播报对撞器喂篡改文案（「已压缩」→「已压缩 succeeded」）⇒ 必须不命中
    #     （max 与 default 两执行档形态各验一次——C-DEFOPT@1 后两档都走分支①，对撞器两形态都要有牙）
    $fake3 = @('[pngopt] 已压缩 succeeded（档位 max）：1000 → 900 字节（-10.0%，耗时 1.0s）')
    $p3 = Test-SuccessBroadcast $fake3
    $nc3 = (-not $p3.ok)
    CK $nc3 "selfcheck 变异③ 篡改播报文案被对撞器拒绝（max 形态，ok=$($p3.ok)；逐字对撞有牙）"
    if (-not $nc3) { $ncOk = $false }
    $fake3d = @('[pngopt] 已压缩 succeeded（档位 default）：1000 → 900 字节（-10.0%，耗时 1.0s）')
    $p3d = Test-SuccessBroadcast $fake3d 'default'
    $nc3d = (-not $p3d.ok)
    CK $nc3d "selfcheck 变异③ 篡改播报文案被对撞器拒绝（default 形态，ok=$($p3d.ok)；逐字对撞有牙）"
    if (-not $nc3d) { $ncOk = $false }

    # 变异④：伪 stdout 注入 SKIP 行 + PROBE RESULT skip=1 ⇒ fail-closed 必须检出（SkipLines>0）
    $fake4 = "PASS pngopt-off-01 哨兵在位`nSKIP pngopt off: oxipng 缺件 ⇒ fail-closed`nPROBE RESULT: pass=1 fail=0 skip=1"
    $p4 = Read-PngoptRun $fake4 'off'
    $nc4 = ($p4.skipLines.Count -ge 1) -and ($p4.pSkip -ge 1)
    CK $nc4 "selfcheck 变异④ SKIP 行注入被 fail-closed 检出（skipLines=$($p4.skipLines.Count)，probeSkip=$($p4.pSkip)）"
    if (-not $nc4) { $ncOk = $false }

    CK ($ncOk) "selfcheck 四变异全部被检出（判读链有牙）⇒ ncOk=$ncOk"
}

# ── §4 新鲜度 + 清理自证 ─────────────────────────────────────────────────────────
Write-Output "`n### 4) 新鲜度 + 清理 ###"
$mtExeAfter = (Get-Item $exe).LastWriteTime
$mtPrAfter = (Get-Item $pr).LastWriteTime
CK (($mtExeAfter -eq $mtExe) -and ($mtPrAfter -eq $mtPr)) "§4 exe/probe mtime 起轮前后一致（$mtExe / $mtPr）⇒ 读数打在同一个二进制上"
try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
CK (-not (Test-Path $work)) "§4 run-dir 已清零：$work"

Write-Output ""
if ($SkipTimeoutCase) {
  Write-Output "===== pngopt-gate(-SkipTimeoutCase 调试臂，timeout case 未跑): PASS=$pass FAIL=$fail ====="
} else {
  Write-Output "===== pngopt-gate: PASS=$pass FAIL=$fail ====="
}
$allOk = ($fail -eq 0) -and $ncOk
if ($allOk) { exit 0 } else { exit 1 }