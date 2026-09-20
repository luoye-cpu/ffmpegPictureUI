# verify-geometry-engine.ps1 —— 色彩引擎「最长边几何缩放」路径的专项门禁（2026-09-17 新增）
#
# 背景（为什么需要独立一条）：
#   `QueueProcessor.cs:429-496` 在**路由之前**就用 `FfmpegCommandBuilder.BuildMaxDimensionScaleFilter`
#   把超限图预缩放到临时文件，并在 `:487` 把 `captured.InputPath` 改写成缩放产物 ⇒ 引擎拿到时**已不超限**
#   ⇒ `RawColorPipeline.cs:94-129` 那段几何缩放**永远不执行**（默认路径上的死代码）。
#   一旦 legacy 的预缩放执行体被删除，那段就变成**承重路径**，而它此前**零覆盖**。
#
# 本门禁只跑 ServiceProbe 的 `geometry` 模式（它**直调引擎**、不经 QueueProcessor ⇒ 才走得到那段），
# 并对探针输出的**结构化行**做独立复读（不只看 `PROBE RESULT`，避免探针自身逻辑写错时一起假绿）：
#   ① 五个用例的产出尺寸与 Stats 反推尺寸都必须等于实测期望（含纵向分支与**非整数边**分支）；
#   ② 负控（不超限）尺寸必须不变、且日志**不得**出现 `几何缩放（最长边限制）`；
#   ③ 引擎缩放尺寸 == QueueProcessor 预缩放尺寸（同滤镜实测对照）⇒ 决定能否安全删掉预缩放；
#   ④ 边界坑：512x384 用 `scale=-2:32` 实测 **42x32**，自己算 42.67 会得 43/44 ⇒ 必须按解码产物长度反推；
#   ⑤ 探针自清（运行级 GUID 目录必须消失）；
#   ⑥ **像素级**删预缩放安全：**非恒等**计划（sRGB→Display P3）下，「今天(预缩放→映射)」vs
#      「删后(引擎内缩放→映射)」的 PSNR/最大差 ⇒ 这是唯一能证明删掉预缩放不改像素的地方。
#      ⚠ 读码结论（本段用像素背书）：两条路径**顺序相同**（都是 缩放→映射，见 RawColorPipeline.cs:92-97
#      把 scaleFilter 塞进解码 `-vf`），差别只在今天多一次「缩放产物落 PNG → 再解码」的量化往返
#      ⇒ 就**引擎出口**而言，删预缩放几乎不改像素。
#      ⚠ 真正的**反序**写在 legacy 路径（FfmpegCommandBuilder.cs:121 色彩链 / :535 scale ⇒ 映射→缩放），
#      但**今天它是休眠的**：预缩放已把输入换成缩放产物 ⇒ :535 的 BuildMaxDimensionScaleFilter
#      返回 null ⇒ :535 从不被调用。⇒ **删预缩放会唤醒它**，非引擎通路的顺序随即翻转为 映射→缩放
#      （2×2 实测 36.47dB / 14417；HDR→SDR 复测 35.96dB / 12227）。⇒ 删预缩放**不是**纯引擎侧改动
#      （详见 docs/TESTING.md §6 第 71 条）。
#
# 退出码：0 = 全绿，1 = 有失败。⚠ 与 _run-step3-gates.ps1 的约定一致（脚本自身不带 gate 前缀）。
#
# ⚠ 双宿主兼容（PS 7 与 PS 5.1，门禁：verify-ps-compat.ps1）：
#   禁止三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁止全角弯引号（用 「」）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$pr  = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $pr)) { $pr = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
foreach ($need in @($pr, $ff)) { if (-not (Test-Path $need)) { Write-Output "缺工具：$need"; exit 1 } }

# ⚠ 运行级隔离（GUID）：固定目录会被**并发同名实例**互删 ⇒ 假红（TESTING §6 第 66 条）。
#   探针自己按计数自清（它把 `geometry_<guid8>` 建在传入的根目录下）；本脚本只留一个日志文件，
#   并**断言探针那个目录真的消失**（残留自证）。
$val = "$root/tests/output/validate"
$gid = [guid]::NewGuid().ToString('N').Substring(0, 8)
$logf = "$val/geoeng_${gid}_probe.txt"
New-Item -ItemType Directory -Force -Path $val | Out-Null
$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }

Write-Host "`n### 1) 直调引擎（绕过 QueueProcessor 预缩放）⇒ 几何缩放段真的跑到 ###" -ForegroundColor Cyan
$p = Start-Process -FilePath $pr -ArgumentList @('geometry', ('"' + $val + '"')) -NoNewWindow -Wait -PassThru `
     -RedirectStandardOutput $logf
$log = [System.IO.File]::ReadAllText($logf)
# 先把探针输出原样打出来：确认脚本真的有输出（PS 注释写成 // 会静默 0 行 ⇒ 假绿）
Write-Output $log
CK ($p.ExitCode -eq 0) "探针退出码 0（实得 $($p.ExitCode)）"

$m = [regex]::Match($log, 'PROBE RESULT: pass=(\d+) fail=0')
CK ($m.Success) "探针全绿（PROBE RESULT: pass=N fail=0，实得 $($m.Value)）"
if ($m.Success) { CK ([int]$m.Groups[1].Value -ge 35) "断言条数 ≥35（实 $($m.Groups[1].Value)）—— 条数掉了说明有用例被跳过" }

Write-Host "`n### 2) 逐用例：产出尺寸 + Stats 反推尺寸 + 日志证据 ###" -ForegroundColor Cyan
# 期望值全部是 2026-09-17 的**实测**（探针自备 512x384 / 384x512 / 512x300 三种源）。
$cases = @(
    @{ id = 'A-landscape-128'; src = '512x384'; max = '128';  exp = '128x96';  mark = 'scaled' },
    @{ id = 'B-landscape-32';  src = '512x384'; max = '32';   exp = '32x24';   mark = 'scaled' },
    @{ id = 'C-portrait-32';   src = '384x512'; max = '32';   exp = '24x32';   mark = 'scaled' },
    @{ id = 'D-fractional-100'; src = '512x300'; max = '100'; exp = '100x58';  mark = 'scaled' },
    @{ id = 'E-negative';      src = '512x384'; max = '1024'; exp = '512x384'; mark = 'none' }
)
foreach ($c in $cases) {
    $pat = 'geometry-case id=' + [regex]::Escape($c.id) + ' src=' + [regex]::Escape($c.src) `
         + ' maxdim=' + $c.max + ' expect=' + [regex]::Escape($c.exp) `
         + ' engine=' + [regex]::Escape($c.exp) + ' stats=' + [regex]::Escape($c.exp) + ' log=' + $c.mark
    CK ($log -match $pat) "② $($c.id)：engine 与 stats 两条独立读数均为 $($c.exp)，日志标记=$($c.mark)"
}
# 负控的正面对照：不超限那条必须**不出现**缩放日志（「该跑没跑」与「不该跑却跑了」都要抓）
CK ($log -notmatch 'geometry-case id=E-negative .*log=scaled') "② E-negative 负控：不超限 ⇒ 未打缩放日志"
CK ($log -notmatch 'FAIL ') "② 探针内部无任何 FAIL 行"

Write-Host "`n### 3) 边界坑：自己算会漂开（42.67 → 43/44）⇒ 只能按解码产物长度反推 ###" -ForegroundColor Cyan
CK ($log -match 'geometry-boundary .*actual=42x32 .*naiveRound=43 naiveEvenUp=44') `
   "③ 512x384 用 scale=-2:32 实测 42x32（自己算 42.67 得 43/44）"

Write-Host "`n### 4) 引擎缩放 vs QueueProcessor 预缩放：尺寸必须逐例一致 ###" -ForegroundColor Cyan
foreach ($id in @('A-landscape-128', 'B-landscape-32', 'C-portrait-32', 'D-fractional-100')) {
    $pat = 'geometry-prescale id=' + [regex]::Escape($id) + ' engine=(\d+x\d+) prescale=(\d+x\d+)'
    $mm = [regex]::Match($log, $pat)
    if ($mm.Success) {
        CK ($mm.Groups[1].Value -eq $mm.Groups[2].Value) `
           "④ $id：引擎 $($mm.Groups[1].Value) == 预缩放 $($mm.Groups[2].Value)（同滤镜）"
    } else { CK $false "④ $id：缺 geometry-prescale 行（对照未执行）" }
}
CK ($log -match 'geometry-prescale id=E-negative' -eq $false) "④ E-negative：不超限 ⇒ 预缩放不执行（无对照行，符合预期）"

Write-Host "`n### 6) 删预缩放的像素级安全：非恒等计划下「今天 vs 删后」 ###" -ForegroundColor Cyan
# 唯一能回答「删掉预缩放会不会改像素」的地方。计划必须是**非恒等**（sRGB→Display P3），否则测不到顺序差异。
CK ($log -match '像素对照：计划为非恒等（Action=Map') "⑥ 像素对照用的是非恒等计划（恒等计划下本段无意义）"
CK ($log -match '像素对照：缩放归属正确') "⑥ 缩放归属正确（今天=预缩放、引擎不缩放；删后=引擎缩放）"
$mp = [regex]::Match($log, 'geometry-pixels prescale-vs-engine psnr=([\d.]+)dB maxDiff=(\d+)/65535')
if ($mp.Success) {
    CK ([double]$mp.Groups[1].Value -ge 40.0) `
       "⑥ 今天(预缩放→映射) vs 删后(引擎内缩放→映射) PSNR = $($mp.Groups[1].Value)dB（≥40）"
    CK ([int]$mp.Groups[2].Value -le 3000) `
       "⑥ 最大单通道差 = $($mp.Groups[2].Value)/65535（≤3000）⇒ 差异只来自一次 PNG 量化往返"
} else { CK $false "⑥ 缺 geometry-pixels 行（像素对照未执行）" }

Write-Host "`n### 5) 残留自证：探针的 GUID 运行级目录必须自清 ###" -ForegroundColor Cyan
$mo = [regex]::Match($log, 'geometry-out=(\S+)')
CK ($mo.Success) "⑤ 探针报告了运行级目录（geometry-out=…）"
if ($mo.Success) {
    $pout = $mo.Groups[1].Value -replace '\\', '/'
    CK (-not (Test-Path $pout)) "⑤ 运行级目录已消失（无残留）：$pout"
}
$mc = [regex]::Match($log, 'geometry-cleanup: 删除文件 (\d+)/(\d+)')
if ($mc.Success) {
    CK ($mc.Groups[1].Value -eq $mc.Groups[2].Value) `
       "⑤ 按计数自清：删除 $($mc.Groups[1].Value)/$($mc.Groups[2].Value) 个文件（必须相等）"
} else { CK $false "⑤ 缺 geometry-cleanup 行（自清未执行）" }

Write-Host ""
Write-Host "===== geometry-engine: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
Remove-Item -Force $logf -ErrorAction SilentlyContinue
exit $(if ($fail -eq 0) { 0 } else { 1 })
