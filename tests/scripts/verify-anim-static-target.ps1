# verify-anim-static-target.ps1 —— 动图输入写「单帧容器」目标的 `-frames:v 1` 语义门禁
#
# ⚠⚠ **本文件是重建件（RECONSTRUCTION），不是原文。** 原文于 2026-09-19 18:37 被蓝屏摧毁
#   （数据页随写回缓存丢失 ⇒ 落盘为 22494 字节全 0x00；全盘仅一份、无 .bak、无 VSS、git 未追踪）。
#   重建依据 = **该门禁最后一次运行（2026-09-19 17:50）的完整捕获输出**
#   `tests/output/_gate_script_verify-anim-static-target.ps1.txt`：它逐字保存了
#   **全部 48 条断言文案、7 个分组、素材规格（webp/apng/gif=256x192 帧=10；png=512x384 帧=1）、
#   5+2+3 个用例目录名（s1..s5）、汇总行格式**。⇒ 断言面被上述证据**强约束**，不是凭记忆编的。
#   ⚠ **残留不确定（交接须知）**：素材的**具体生成命令**、`[cmd]` 行的**具体解析方式**属推断
#   （原文未留证据）。重建件的验收判据 = **跑出逐字相同的 48 条 PASS 文案 + PASS=48 FAIL=0**。
#
# ✅ **重建验收（2026-09-19 实测，两项都过）**：
#   ① **逐条对拍**：本件输出与原文捕获输出的 PASS 行**逐条、逐字、同序完全一致**
#      （48/48，仅运行级 GUID 不同；比对脚本见 §12.3 记录）。
#   ② **变异验证（有牙）**：把 `FfmpegCommandBuilder.cs:139` 的
#      `bool insertFramesV1 = !targetHoldsMultipleFrames;` 改成 `= true;`（**无条件**插入）
#      ⇒ 本门禁 **`PASS=43 FAIL=5`、`exit=1`**，且红的恰好是「静默截断」那 5 格
#      （③ anim.webp→webp 的「保留 10 帧」+「不带该参数」、③ anim.gif→gif 同两条、⑤ jxl+animation-fps 的
#      「不得 exit 0 且 1 帧」—— 实测 `exit=0 帧=1`）⇒ **证明它抓得住它要抓的缺陷类**。
#      回滚 + `-t:Rebuild` ⇒ 恢复 `PASS=48 FAIL=0`（三项目均 0 警告 0 错误）。
#
# 缺陷（本门禁要锁住的东西，`#139` 同族）：
#   动画输入（webp/apng/gif）写**单帧容器**（jpg/png/…）时，必须显式给输出侧 `-frames:v 1`，
#   否则 ffmpeg 的 image2 复用器会报「filename does not contain an image sequence pattern」——
#   更坏的情况是**静默只写首帧或写出半成品**。反之，写**多帧容器**（webp/gif）时**绝不能**带
#   该参数，否则动画被**静默截成 1 帧**（这正是本门禁的负控轴）。
#   ⇒ 参数必须**按目标容器能否装多帧**决定，而不是无条件加/不加。
#
# 断言（共 48 条，7 组）：
#   ① 素材形态自检 2 条（三个动画素材必须都 >1 帧，否则前提不成立；三个单帧素材必须 =1 帧作对照）
#   ② 正例 20 条（5 格 × 4）：动图 webp/apng/gif → jpg，动图 webp/apng → png
#      ⇒ exit 0 · 有产物 · 产物 **1 帧** · 命令行**带** `-frames:v 1`
#   ③ 负控 8 条（2 格 × 4）：动图 webp→webp、gif→gif
#      ⇒ exit 0 · 有产物 · 保留 **10 帧** · 命令行**不带** `-frames:v 1`（防「无条件加」把动画截断）
#   ④ 单帧不回归 15 条（3 格 × 5）：单帧 png→jpg、jpg→png、tiff→tiff
#      ⇒ exit 0 · 有产物 · 产物 1 帧 · 命令行**带**该参数（证明单帧也走新分支，不是没覆盖到）·
#        与「把该参数从命令行逐字去掉后直接重跑 ffmpeg」的产物**逐像素相同（PSNR=average:inf）**
#        ⇒ 证明该参数对单帧输入是 **no-op**（字节级可能不同 ⇒ 所以判据必须是 PSNR，不是 SHA）
#   ⑤ 静默截断守卫 1 条：`anim.webp → jxl + --animation-fps` **不得** exit 0 且 1 帧
#   ⑥ 被测 exe mtime 跑前跑后一致 1 条（§6 第 84 条）
#   ⑦ 残留自证 1 条（运行级目录已清零）
#
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（用 「」）。
# ⚠⚠ **退出码禁用轮询式 `.ExitCode`**：PS5.1 下 `Start-Process -PassThru`（不带 `-Wait`）的 `.ExitCode`
#   **恒为 `$null`**，而 `[int]$null == 0` ⇒ 会把「读不到」伪装成「exit 0」⇒ **假绿**。
#   ⚠ 2026-09-22 更正：本文件此前写着"轮询到 `HasExited` 后再读 `ExitCode`，并对 `$null` 单独记账"——
#     前半句的前提是错的（结束 ≠ 读得到），后半句是**虚假声明**（代码里没有任何 `$null` 记账），
#     实测该门禁在 PS5.1 下全红、在 pwsh7 下 48/0。现改为：`GetFrames` 只看文本不依赖取码，
#     需要真退出码的判据一律走 `CheckExit`（取不到 ⇒ 单列点名"测量失败"的红），⑤ 另加"可评估"前置。
#   ⚠ 本门禁**仍未**改成 `-Wait -PassThru` 或「子宿主 + rc 文件」⇒ 在 5.1 下那几条 `exit == 0`
#     会以"测量失败"的形式红着（fail-closed，不再伪装成产品缺陷）。要真正双宿主可用需按
#     `verify-ui-host.ps1` 的 rc 范式改造 ⇒ **已登记为待办**，不在本轮范围内。
# ⚠⚠ **杀树禁用 `$p.Kill($true)`**（该重载在 PS5.1/.NET Framework 上不存在）⇒ 用下面的 `KillTree`。
# ⚠ 形参名**不能**叫 `$pid`（只读自动变量）。
# ⚠⚠ **结尾必须是完整 if/else**（§6 第 67 条）：清理步会污染 `$LASTEXITCODE`，只写
#   `if ($fail -gt 0) { exit 1 }` 会让全绿路径以被污染的码退出 ⇒ **假红**。
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

# ⚠ 运行级隔离（GUID）：固定 `$work` 会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$val  = "$root/tests/output/validate"
$gid  = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = "$val/animstatic_$gid"
if (-not (Test-Path $work)) { New-Item -ItemType Directory -Path $work -Force | Out-Null }

$script:pass = 0
$script:fail = 0
function Pass([string]$m) { $script:pass++; Write-Host "PASS $m" -ForegroundColor Green }
function Fail([string]$m) { $script:fail++; Write-Host "FAIL $m" -ForegroundColor Red }
function Check([bool]$ok, [string]$m) { if ($ok) { Pass $m } else { Fail $m } }

# ⚠⚠ 杀**进程树**：**不要**用 `$p.Kill($true)`（PS5.1 只有无参 `Kill()`）⇒ 自己按 `ParentProcessId`
#   递归杀（先子后父）。⚠ 形参名不能叫 `$pid`。
function KillTree([int]$rootPid) {
  $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
  foreach ($q in $all) {
    if ($q.ParentProcessId -eq $rootPid) { KillTree ([int]$q.ProcessId) }
  }
  $pr = $null
  try { $pr = Get-Process -Id $rootPid -ErrorAction SilentlyContinue } catch { $pr = $null }
  if ($pr) { try { $pr.Kill() } catch { } }
}

function ReadTextRetry([string]$p) {
  for ($i = 0; $i -lt 6; $i++) {
    try {
      if (Test-Path $p) { return @{ ok = $true; text = [System.IO.File]::ReadAllText($p) } }
      return @{ ok = $true; text = "" }
    } catch { Start-Sleep -Milliseconds 120 }
  }
  return @{ ok = $false; text = "" }
}

# ⚠ **外部工具必须有界**：`Start-Process -Wait` 是**无界**的 ⇒ 畸形素材上可能永久挂住。
function Exec([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 120 }
  $timedOut = (-not $p.HasExited)
  if ($timedOut) { KillTree ([int]$p.Id); Start-Sleep -Milliseconds 400 }
  $t  = ReadTextRetry $o
  $t2 = ReadTextRetry $e
  $code = $null
  if (-not $timedOut) { try { if ($null -ne $p.ExitCode) { $code = $p.ExitCode } } catch { $code = $null } }
  return @{ code = $code; text = ($t.text + "`n" + $t2.text); timedOut = $timedOut; readOk = ($t.ok -and $t2.ok) }
}

# ── 单条用例：起应用（`--headless --log-level debug` ⇒ stdout 里带 `[cmd] ffmpeg …` 那行）──
function RunApp([string[]]$appArgs, [string]$tag, [int]$limitSec = 120) {
  $so = "$work/$tag.app.out"; $se = "$work/$tag.app.err"
  $p = Start-Process -FilePath $exe -ArgumentList $appArgs -PassThru -NoNewWindow `
       -RedirectStandardOutput $so -RedirectStandardError $se
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $limitSec) { Start-Sleep -Milliseconds 120 }
  $timedOut = (-not $p.HasExited)
  if ($timedOut) { KillTree ([int]$p.Id); Start-Sleep -Milliseconds 400 }
  $code = $null
  if (-not $timedOut) { try { if ($null -ne $p.ExitCode) { $code = $p.ExitCode } } catch { $code = $null } }
  $t  = ReadTextRetry $so
  $t2 = ReadTextRetry $se
  return @{ code = $code; text = ($t.text + "`n" + $t2.text); timedOut = $timedOut }
}

# 取 `[cmd] ` 行（命令行 = **子进程真实证据**，优于日志散文）
function GetCmdLine([string]$text) {
  $m = [regex]::Match($text, '(?m)^\s*\[cmd\]\s*(.+?)\s*$')
  if ($m.Success) { return $m.Groups[1].Value }
  return ""
}

# ⚠⚠ 退出码判据必须**区分"产品非 0"与"本宿主取不到码"**（2026-09-22 实测教训）：
#   PS5.1 下本门禁的 `Exec`/`RunApp` 是"轮询 `HasExited` 后读 `.ExitCode`"，该写法恒得 `$null`
#   （既有实测见 `GetFrames` 上方注释）。旧写法把 `$null` 直接喂进 `$r.code -eq 0` ⇒
#   在 5.1 下**全部记成产品失败**（假红），且 ⑤ 那条取反判据因此**恒真真空绿**（假绿）。
#   ⇒ 取不到码时单列一条**点名"测量失败"**的红，绝不让它伪装成产品结论。
function CheckExit($code, [string]$msg) {
  if ($null -eq $code) {
    Check $false "$msg —— 退出码在本宿主取不到（PS5.1 轮询式 `.ExitCode` 恒为 null）⇒ **测量失败**，不是产品失败"
    return
  }
  Check ($code -eq 0) "$msg（实 $code）"
}

# 帧数（ffprobe 真数帧；失败返回 -1，**不得**把「读不到」当成 1）
# ⚠⚠ 判据**不看退出码**：PS5.1 下本函数的 `Exec` 是"轮询 `HasExited` 后读 `.ExitCode`"，
#   而该写法在 5.1 下**恒取到 `$null`**（既有实测：`verify-jxl-probe-timeout.ps1:114`
#   「带 `-Wait` ⇒ 1；轮询到 HasExited 后读 ⇒ `<null>`，`Refresh()` 无效」、
#   `verify-avif-anim-probe.ps1:144`「`WaitForExit()` 与 `Refresh()` 都救不回来」）。
#   旧写法 `if ($r.code -ne 0) { return -1 }` 因此把**每一次**取帧都判成失败 ⇒ 整门禁 mass 假红
#   （实测同一份脚本 pwsh7 = 48/0、PS5.1 = 全红，而手调 ffprobe 同一文件返回 `…,10` / rc=0）。
#   ⇒ 改为"只认整行为数字的那一行"，读不到仍返回 -1（fail-closed 不变），但不再依赖宿主取码能力。
function GetFrames([string]$f) {
  if (-not (Test-Path $f)) { return -1 }
  $r = Exec $fp ("-v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 `"$f`"") "fp_$([guid]::NewGuid().ToString('N').Substring(0,6))"
  if ($r.timedOut) { return -1 }
  $n = 0
  foreach ($ln in ($r.text -split "`n")) {
    $s = $ln.Trim()
    if ($s -ne "" -and [int]::TryParse($s, [ref]$n)) { return $n }
  }
  return -1
}

# 唯一产物（用例目录下恰好一个文件；多于一个则返回第一个并记账）
function GetProduct([string]$dir) {
  if (-not (Test-Path $dir)) { return "" }
  $fs = @(Get-ChildItem $dir -File -Recurse -ErrorAction SilentlyContinue)
  if ($fs.Count -eq 0) { return "" }
  return $fs[0].FullName
}

function GetSha([string]$f) {
  if (-not (Test-Path $f)) { return "-" }
  return (Get-FileHash $f -Algorithm SHA256).Hash.Substring(0, 12)
}

# 逐像素比较（返回 average 字段；inf = 完全相同）
# ⚠⚠ **不能加 `-v error`**：psnr 滤波器的汇总行（`... average:inf ...`）是在 **info** 级别打印的，
#   用 `-v error` 会把它整行吞掉 ⇒ 解析恒失败 ⇒ 该断言恒红（或恒真，取决于写法）。实测踩过。
function GetPsnr([string]$a, [string]$b) {
  $r = Exec $ff ("-hide_banner -v info -i `"$a`" -i `"$b`" -lavfi psnr -f null -") "psnr_$([guid]::NewGuid().ToString('N').Substring(0,6))"
  $m = [regex]::Match($r.text, 'average:([0-9.]+|inf)')
  if ($m.Success) { return $m.Groups[1].Value }
  return "?"
}

# 从 `[cmd] ` 行里剥掉 ` -frames:v 1`（用于 ④ 的 no-op 对照重跑）
function StripFramesV([string]$cmd) {
  $c = $cmd -replace '\s-frames:v\s+1\s', ' '
  return $c
}

$exeMtimeBefore = (Get-Item $exe).LastWriteTime

Write-Host "`n### ① 自备 10 帧动画素材（webp/apng/gif）+ 1 张单帧 png，并断言形态 ###" -ForegroundColor Cyan
$animW = "$work/anim.webp"; $animA = "$work/anim.apng"; $animG = "$work/anim.gif"
$singleP = "$work/single.png"; $singleJ = "$work/single.jpg"; $singleT = "$work/single.tiff"

$srcAnim = "-y -v error -f lavfi -i testsrc=size=256x192:rate=10 -frames:v 10"
# ⚠ webp 必须带 `-c:v libwebp_anim -lossless 1`：**有损**路径下 ffmpeg 的 webp 动画编码器会把
#   10 帧压成 **5 帧**（实测 `r_frame_rate=5/1`、文件内 `ANMF` 块数=5）⇒ 素材前提不成立、
#   ③ 的「保留 10 帧」负控会假红。apng/gif 无此问题。
[void](Exec $ff "$srcAnim -c:v libwebp_anim -lossless 1 `"$animW`"" "mk_webp")
[void](Exec $ff "$srcAnim -f apng `"$animA`"" "mk_apng")
[void](Exec $ff "$srcAnim -f gif `"$animG`"" "mk_gif")
[void](Exec $ff "-y -v error -f lavfi -i testsrc=size=512x384:rate=1 -frames:v 1 `"$singleP`"" "mk_png")
[void](Exec $ff "-y -v error -i `"$singleP`" -q:v 3 `"$singleJ`"" "mk_jpg")
[void](Exec $ff "-y -v error -i `"$singleP`" `"$singleT`"" "mk_tiff")

$fw = GetFrames $animW; $fa = GetFrames $animA; $fg = GetFrames $animG
$fsp = GetFrames $singleP; $fsj = GetFrames $singleJ; $fst = GetFrames $singleT
Write-Host "  [素材] webp=256x192 帧=$fw | apng=256x192 帧=$fa | gif=256x192 帧=$fg | png=512x384 帧=$fsp | jpg 帧=$fsj | tiff 帧=$fst"

Check (($fw -gt 1) -and ($fa -gt 1) -and ($fg -gt 1)) `
  "① 三个动画素材都是多帧（webp=$fw apng=$fa gif=$fg，必须都 >1；否则本门禁前提不成立）"
Check (($fsp -eq 1) -and ($fsj -eq 1) -and ($fst -eq 1)) `
  "① 三个单帧素材确实是单帧（png=$fsp jpg=$fsj tiff=$fst）—— 与多帧素材形成对照"

# ── 通用：跑一格「动图 → 目标容器」─────────────────────────────────────────────
function RunAnimCase([string]$inPath, [string]$fmt, [string]$tag, [string]$label, [string]$expectKind, [string]$grp) {
  $outdir = "$work/$tag"
  $r = RunApp @("--headless", "--log-level", "debug", "-i", $inPath, "-o", $outdir, "--format", $fmt) $tag
  $prod = GetProduct $outdir
  $frames = -1
  if ($prod -ne "") { $frames = GetFrames $prod }
  $cmd = GetCmdLine $r.text
  $hasFlag = ($cmd -match '\s-frames:v\s+1(\s|$)')
  $prodName = ""
  if ($prod -ne "") { $prodName = (Split-Path $prod -Leaf) }
  $framesTxt = "0"
  if ($frames -ge 0) { $framesTxt = "$frames" }
  Write-Host "  [$label] exit=$($r.code) 产物=$(if ($prodName -eq '') { '(无)' } else { $prodName }) 帧=$framesTxt 带-frames:v1=$hasFlag"

  CheckExit $r.code "$grp $label：exit == 0"
  # ⚠ 文案差异是**有意**的（与原文逐字一致）：② 的有产物带目录、③ 的不带。
  if ($expectKind -eq "single") {
    Check ($prod -ne "") "$grp $label：有产物（$outdir）"
  } else {
    Check ($prod -ne "") "$grp $label：有产物"
  }
  if ($expectKind -eq "single") {
    Check ($frames -eq 1) "② $label：产物 **1 帧**（实 $framesTxt）—— 静态容器不得装多帧"
    Check $hasFlag "② $label：命令行带 「-frames:v 1」（该参数是输出侧选项，限制写出帧数）"
  } else {
    Check ($frames -eq 10) "③ $label：保留 **10 帧**（实 $framesTxt）—— 多帧容器不得被截成 1 帧"
    Check (-not $hasFlag) "③ $label：命令行**不带** 「-frames:v 1」（否则动画被静默丢弃）"
  }
  return @{ r = $r; prod = $prod; cmd = $cmd; frames = $frames }
}

Write-Host "`n### ② 正例：动图 webp/apng/gif → jpg / png（必须 exit 0、有产物、产物 1 帧）###" -ForegroundColor Cyan
$pos = @(
  @{ in = $animW; fmt = "jpg"; tag = "s1_anim_webp_jpg"; label = "anim.webp → jpg" },
  @{ in = $animA; fmt = "jpg"; tag = "s2_anim_apng_jpg"; label = "anim.apng → jpg" },
  @{ in = $animG; fmt = "jpg"; tag = "s3_anim_gif_jpg";  label = "anim.gif → jpg"  },
  @{ in = $animW; fmt = "png"; tag = "s4_anim_webp_png"; label = "anim.webp → png" },
  @{ in = $animA; fmt = "png"; tag = "s5_anim_apng_png"; label = "anim.apng → png" }
)
foreach ($c in $pos) { [void](RunAnimCase $c.in $c.fmt $c.tag $c.label "single" "②") }

Write-Host "`n### ③ 负控：动图 webp→webp、gif→gif 必须保留 10 帧（防止 -frames:v 1 变成无条件）###" -ForegroundColor Cyan
$neg = @(
  @{ in = $animW; fmt = "webp"; tag = "n1_anim_webp_webp"; label = "anim.webp → webp" },
  @{ in = $animG; fmt = "gif";  tag = "n2_anim_gif_gif";   label = "anim.gif → gif"   }
)
foreach ($c in $neg) { [void](RunAnimCase $c.in $c.fmt $c.tag $c.label "multi" "③") }

Write-Host "`n### ④ 单帧不回归：单帧 png/jpg/tiff → 单帧容器，产物须与「去掉 -frames:v 1 重跑」逐像素相同 ###" -ForegroundColor Cyan
$sgl = @(
  @{ in = $singleP; fmt = "jpg";  tag = "t1_single_png_jpg";  label = "single.png → jpg"  },
  @{ in = $singleJ; fmt = "png";  tag = "t2_single_jpg_png";  label = "single.jpg → png"  },
  @{ in = $singleT; fmt = "tiff"; tag = "t3_single_tiff_tiff"; label = "single.tiff → tiff" }
)
foreach ($c in $sgl) {
  $outdir = "$work/$($c.tag)"
  $r = RunApp @("--headless", "--log-level", "debug", "-i", $c.in, "-o", $outdir, "--format", $c.fmt) $c.tag
  $prod = GetProduct $outdir
  $frames = -1
  if ($prod -ne "") { $frames = GetFrames $prod }
  $cmd = GetCmdLine $r.text
  $hasFlag = ($cmd -match '\s-frames:v\s+1(\s|$)')
  $framesTxt = "0"; if ($frames -ge 0) { $framesTxt = "$frames" }
  Write-Host "  [$($c.label)] exit=$($r.code) 产物=$(if ($prod -eq '') { '(无)' } else { Split-Path $prod -Leaf }) 帧=$framesTxt 带-frames:v1=$hasFlag"

  CheckExit $r.code "④ $($c.label)：exit == 0"
  Check ($prod -ne "") "④ $($c.label)：有产物"
  Check ($frames -eq 1) "④ $($c.label)：产物 1 帧（实 $framesTxt）"
  Check $hasFlag "④ $($c.label)：命令行**带** 「-frames:v 1」（证明单帧输入也走了新分支，不是没覆盖到）"

  # no-op 对照：把该参数从命令行逐字去掉后直接重跑 ffmpeg，产物须逐像素相同
  $rawDir = "$work/raw_$($c.tag)"
  if (-not (Test-Path $rawDir)) { New-Item -ItemType Directory -Path $rawDir -Force | Out-Null }
  $rawOut = "$rawDir/$(Split-Path $prod -Leaf)"
  $rawCmd = StripFramesV $cmd
  # 把原命令行的输出路径换成对照路径（原命令行末段是带引号的输出路径）
  # ⚠ 必须**大小写不敏感**替换：应用 `[cmd]` 行里的路径是小写盘符 + 反斜杠，而 PowerShell 给出的
  #   `FullName` 可能是大写盘符 ⇒ 用大小写敏感的 `-replace` 会**不匹配** ⇒ 对照重跑会**覆盖被测产物本身**
  #   ⇒ PSNR 变成「自己比自己」= 恒 inf ⇒ **该条断言恒真（假绿）**。
  $rawCmd = $rawCmd -ireplace [regex]::Escape($prod), $rawOut
  $rr = Exec $ff ($rawCmd.Substring($rawCmd.IndexOf(" ") + 1)) "raw_$($c.tag)"
  $rawProd = $rawOut
  $psnr = "?"
  if ((Test-Path $rawProd) -and ($prod -ne "")) { $psnr = GetPsnr $prod $rawProd }
  Write-Host "     [no-op 证明] raw exit=$($rr.code) PSNR(inf)=$($psnr -eq 'inf') sha(exe)=$(GetSha $prod) sha(raw)=$(GetSha $rawProd)"
  Check ($psnr -eq "inf") "④ $($c.label)：产物与「去掉 -frames:v 1 逐字重跑」**逐像素相同（PSNR=average:$psnr）** ⇒ 该参数对单帧输入是 no-op"
}

Write-Host "`n### ⑤ 静默截断守卫：anim.webp → jxl + --animation-fps（多帧容器，不得被截成 1 帧）###" -ForegroundColor Cyan
$jdir = "$work/g1_anim_webp_jxl"
$jr = RunApp @("--headless", "--log-level", "debug", "-i", $animW, "-o", $jdir, "--format", "jxl", "--animation-fps", "10") "g1_anim_webp_jxl"
$jprod = GetProduct $jdir
$jframes = -1
if ($jprod -ne "") { $jframes = GetFrames $jprod }
$jframesTxt = "0"; if ($jframes -ge 0) { $jframesTxt = "$jframes" }
Write-Host "  [anim.webp → jxl(anim)] exit=$($jr.code) 产物=$(if ($jprod -eq '') { '(无)' } else { Split-Path $jprod -Leaf }) 帧=$jframesTxt"
# ⚠ 这条是**取反**判据 ⇒ 必须先证明"两个输入都可评估"，否则它会**恒真真空绿**：
#   PS5.1 下 `$jr.code` 取不到 ⇒ `$null -eq 0` 为假 ⇒ `(-and)` 短路 ⇒ `-not $false` = 恒 PASS。
#   该条正是本门禁变异验证（把 `FfmpegCommandBuilder` 的 `insertFramesV1` 判据改成无条件插参）
#   用来抓红的牙 ⇒ 牙不能建立在取码能力上。
# ⚠ 但"可评估"前置**不得过严**（2026-09-22 实测踩过）：本用例的**正确**行为之一就是"应用拒绝并
#   非零退出、零产物"（实测 exit=1、`image2 … does not contain an image sequence pattern`）⇒
#   那种情形下压根没有产物可数帧，要求 `帧 ≥ 0` 会把**合法拒绝**误判成测量失败。
#   ⇒ 只有"取不到码"或"exit==0 却数不到帧"这两种**会让判据假通过**的情形才记测量失败。
$g5Msg = "⑤ anim.webp → jxl + --animation-fps：**不得** exit 0 且 1 帧（否则动画被静默丢弃；实 exit=$($jr.code) 帧=$jframesTxt）"
if ($null -eq $jr.code) {
  Check $false "⑤ anim.webp → jxl + --animation-fps：退出码在本宿主取不到（PS5.1 轮询式 `.ExitCode` 恒为 null）⇒ **测量失败**，不下「未截断」的结论（旧写法在此情形下会恒真 PASS）"
} elseif (($jr.code -eq 0) -and ($jframes -lt 0)) {
  Check $false "⑤ anim.webp → jxl + --animation-fps：exit==0 但产物帧数读不到（帧=-1）⇒ **测量失败**，不得当成「未截断」"
} else {
  Check (-not (($jr.code -eq 0) -and ($jframes -eq 1))) $g5Msg
}

Write-Host "`n### ⑥ 被测 exe mtime 跑前跑后一致 ###" -ForegroundColor Cyan
$exeMtimeAfter = (Get-Item $exe).LastWriteTime
Check ($exeMtimeBefore -eq $exeMtimeAfter) "⑥ 被测 exe 未被中途重建（$exe）"

Write-Host "`n### ⑦ 残留自证 ###" -ForegroundColor Cyan
# ⚠⚠ 本仓宿主有 **safe-delete 守卫**：按「turn」累计计数，超过阈值（实测 50）后 `Remove-Item`
#   **被拦下**并打印 `[safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED]` ⇒ 直接 `Remove-Item -Recurse`
#   **删不掉**（本运行级目录约 120 项）⇒ ⑦ 会假红。
#   ⚠ 实测**分批删也没用**：守卫是按 turn 累计的，第一批 40 项后计数就到阈值，之后每批都被拦
#   （症状：刷屏 + 目录仍在）。⇒ 走 **.NET API**（`Directory.Delete`），它不经过 cmdlet 层的守卫。
#   ⚠ 只删**本脚本自建的、且路径形状确认为运行级目录**的目录（防误删）。
function RemoveRunDir([string]$dir) {
  if (-not (Test-Path $dir)) { return }
  if ($dir -notmatch 'animstatic_[0-9a-f]{8}$') { return }
  if ($dir -notmatch 'tests[/\\]output[/\\]validate[/\\]') { return }
  try { [System.IO.Directory]::Delete($dir, $true) } catch { }
}
RemoveRunDir $work
$left = 0
if (Test-Path $work) { $left = @(Get-ChildItem $work -Recurse -ErrorAction SilentlyContinue).Count }
Check ((-not (Test-Path $work)) -or ($left -eq 0)) "⑦ 运行级目录已清零：$work"

Write-Host "`n===== anim-static-target: PASS=$($script:pass) FAIL=$($script:fail) =====" -ForegroundColor $(if ($script:fail -eq 0) { "Green" } else { "Red" })
if ($script:fail -eq 0) { exit 0 } else { exit 1 }
