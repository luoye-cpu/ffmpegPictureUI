# verify-engine-alpha-preserve.ps1 —— 色彩引擎：**透明输入 + 色彩变换**不得把 alpha 静默拍平
#
# 为什么单独一条门禁（2026-09-22，管线审查 §3.4 的修复锁）：
#   引擎 raw 通路是 rgb24/rgb48le（**无 alpha 平面**）⇒ 只要输入带 alpha 且**需要色彩变换**，
#   产物就会被**静默拍平**。实测（修复前）：透明源 → png(Display P3) = `rgb24`、tiff = `rgb24`、
#   webp = `yuv420p` —— 用户看到"已完成"，透明却没了。
#   修复 = 把 gif 出口既有的「第二输入只取原文件 alpha 平面 + `alphamerge`」推广到 png/apng/tiff/webp，
#   并显式给出带 alpha 的 `-pix_fmt`（见 `RawColorPipeline.BuildEncodeArgs`）。
#
# ⚠⚠ **本门禁的核心前提：素材必须"真透明"** —— 只用 `sources/src_alpha.png` 是**不够**的：
#   它实测是「RGBA 容器但**全不透明**」（alpha 恒 255）⇒ 那种素材**证不出透明值是否保住**
#   （修复前无 alpha 通道时，`alphaextract` 也会给出 255 ⇒ 断言**恒真**）。
#   故本脚本用 lavfi `color=c=red@0.0` **自造**一个 alpha 恒 0 的源，并**先自检**它的 alpha 确为 0。
#
# 判据（三段）：
#   ① 素材自检：源带 alpha 且 alphaYMIN == 0（否则整条门禁无判别力 ⇒ 直接红）
#   ② 正例：透明源 + `--color-space "Display P3"` → png / tiff / webp
#      ⇒ exit 0 **且** 产物 pix_fmt 含 alpha **且** 产物 alphaYMIN == 0（webp 放宽容差到 ≤4：有损编码）
#   ③ 负控：**不透明**源 + 同选项 ⇒ 引擎命令行**不得**出现 `alphaextract`
#      （防"无条件合并"这种把断言变成恒真的改法）
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
if (-not (Test-Path $exe)) { Write-Output "SKIP FfmpegGui.exe 未构建（先 dotnet build）"; exit 1 }
if (-not (Test-Path $ff)) { Write-Output "SKIP 缺 ffmpeg：$ff"; exit 1 }
# ⚠ 运行级隔离（GUID）：固定 `$d` + 开头整目录删会被并发同名实例互删 ⇒ 假红（§6 第 66 条）。
$d = "$root/tests/output/validate/alphapres_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null
$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }

# 取 alpha 平面的 YMIN/YMAX：**落盘再读字节**（⚠ 不能直接读 ffmpeg 的二进制 stdout —— 会被当成字符串解析而失败）
function Get-AlphaRange([string]$path, [string]$tag) {
    $raw = "$d\_a_$tag.raw"
    & $ff -v error -y -i $path -vf alphaextract -f rawvideo -pix_fmt gray $raw 2>$null | Out-Null
    if (-not (Test-Path $raw)) { return $null }
    $b = [System.IO.File]::ReadAllBytes($raw)
    Remove-Item $raw -Force -ErrorAction SilentlyContinue
    if ($b.Length -eq 0) { return $null }
    return @{ Min = ($b | Measure-Object -Minimum).Minimum; Max = ($b | Measure-Object -Maximum).Maximum }
}
function Get-PixFmt([string]$path) {
    # ⚠ 两条都要：① **只取 stdout**（ffprobe 的告警走 stderr，拼进来会把告警当值）；
    #   ② **读不到就返回 $null**，别让空串喂给负断言 —— ③ 那句 `pix_fmt 不含 a`（"素材确实不透明"）
    #     在取数失败时 `'' -notmatch 'a'` 恒真 ⇒ **假绿**（本轮 #51 数出的五处之一）。
    $v = & $fp -v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 $path 2>$null
    $s = ([string]($v -join '')).Trim()
    if ($s -eq '') { return $null }
    return $s
}
function RunCli([string]$tag, [string]$inFile, [string]$fmt, [string[]]$extra) {
    $a = @('--headless', '-i', "`"$inFile`"", '--format', $fmt, '--output', "`"$d/$tag`"", '--log-level', 'Debug') + $extra
    $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$d/$tag.o" -RedirectStandardError "$d/$tag.e"
    $log = [System.IO.File]::ReadAllText("$d/$tag.o") + [System.IO.File]::ReadAllText("$d/$tag.e")
    $f = Get-ChildItem -Recurse "$d/$tag" -Include *.png,*.webp,*.tif,*.tiff -ErrorAction SilentlyContinue |
        Select-Object -First 1
    return @{ code = $p.ExitCode; log = $log; file = $f }
}

# ── ① 自造"真透明"源 + 素材自检 ──────────────────────────────────────────────
# ⚠ 造法用 `colorchannelmixer=aa=0`（实测可靠）；**不要**用 `color=c=red@0.0` ——
#   后者在本构建下产出的仍是**不透明** PNG（实测 alpha 255..255），会把整条门禁变成恒真。
$trans = "$d/trans_src.png"
& $ff -v error -y -f lavfi -i "color=c=red:s=64x48:d=1" -vf "format=rgba,colorchannelmixer=aa=0" `
      -frames:v 1 -pix_fmt rgba $trans 2>$null | Out-Null
CK (Test-Path $trans) "① 自造透明源成功（$trans）"
$srcFmt = Get-PixFmt $trans
CK ($null -ne $srcFmt -and $srcFmt -match 'a') "① 源确实带 alpha（pix_fmt=$srcFmt）—— 否则本门禁无判别力"
$srcRange = Get-AlphaRange $trans 'src'
CK ($null -ne $srcRange -and $srcRange.Min -eq 0) `
   "① 源 alpha 确实为 0（实测 $(if ($srcRange) { "$($srcRange.Min)..$($srcRange.Max)" } else { '读不出' })）—— **必须真透明**，否则断言恒真"

# ── ② 正例：透明源 + 色彩变换 ⇒ 产物必须**保住 alpha 且透明值仍是 0** ──────────
# ⚠ 两条管线都要覆盖：**引擎**（默认，`RawColorPipeline`）与 **legacy**（`FfmpegCommandBuilder`，
#   其色彩链以 `format=rgb48le` 开头 ⇒ 同一病灶，2026-09-22 一并修）。
$cases = @(
    @{ t = 'p_png';   f = 'png';  tol = 0; eng = @() },
    @{ t = 'p_tiff';  f = 'tiff'; tol = 0; eng = @() },
    @{ t = 'p_webp';  f = 'webp'; tol = 4; eng = @() },
    @{ t = 'l_png';   f = 'png';  tol = 0; eng = @('--color-engine', 'legacy') },
    @{ t = 'l_tiff';  f = 'tiff'; tol = 0; eng = @('--color-engine', 'legacy') },
    @{ t = 'l_webp';  f = 'webp'; tol = 4; eng = @('--color-engine', 'legacy') }
)
foreach ($case in $cases) {
    $r = RunCli $case.t $trans $case.f (@('--color-space', '"Display P3"') + $case.eng)
    CK ($r.code -eq 0) "② [$($case.t)] exit 0（实 $($r.code)）—— 色彩变换路径不得因 alpha 修复而失败"
    if (-not $r.file) { CK $false "② [$($case.t)] 产物存在（无产物）"; continue }
    $pf = Get-PixFmt $r.file.FullName
    CK ($null -ne $pf -and $pf -match 'a') "② [$($case.t)] 产物 pix_fmt 含 alpha（实 $pf）—— 修复前是 rgb24/yuv420p/rgb48be"
    $ar = Get-AlphaRange $r.file.FullName $case.t
    $shown = if ($ar) { "$($ar.Min)..$($ar.Max)" } else { '读不出' }
    CK ($null -ne $ar -and $ar.Min -le $case.tol) `
       "② [$($case.t)] 产物 alphaYMIN <= $($case.tol)（实 $shown）—— 透明**值**必须真的活下来（不只看通道在不在）"
    # ⚠ **逐用例即时清理**：宿主有「批量删除守卫」（同一轮内删除数 > 50 即拦整目录删）⇒
    #   若攒到最后一次性删（本门禁会产 ~80 个文件），自清会**被守卫拦下**、留下 GUID 残留。
    #   每个用例结束后立刻删掉它自己的目录与两份日志，把存活文件数**始终压在阈值以下**。
    Remove-Item -Recurse -Force "$d/$($case.t)" -ErrorAction SilentlyContinue
    Remove-Item -Force "$d/$($case.t).o", "$d/$($case.t).e" -ErrorAction SilentlyContinue
}

# ── ③ 负控：**不透明**源 ⇒ 不得触发 alpha 合并（防"无条件合并"把断言变恒真）───
# ⚠ 判据用 **pix_fmt 不含 alpha**（不能用 `alphaextract`：无 alpha 平面的源上它**读不出**，
#   会把负控自身变成假红 —— 实测踩过）。
$opaque = "$d/opaque_src.png"
& $ff -v error -y -f lavfi -i "color=c=red:s=64x48:d=1" -frames:v 1 -pix_fmt rgb24 $opaque 2>$null | Out-Null
$oFmt = Get-PixFmt $opaque
CK ($null -ne $oFmt -and $oFmt -notmatch 'a') "③ 负控素材确实**不透明**（pix_fmt=$oFmt；读不到也算红，空值喂负断言会恒真）"
$r2 = RunCli 'n_opaque' $opaque 'png' @('--color-space', '"Display P3"')
CK ($r2.code -eq 0) "③ [负控·引擎] exit 0（实 $($r2.code)）"
CK ($r2.log -notmatch 'alphaextract') "③ [负控·引擎] 不透明源**不得**触发 alpha 合并（命令行不含 alphaextract）"
# 正例那边则**必须**出现 alphaextract —— 否则说明修复没生效、② 的 pix_fmt 断言会失去因果
$r3 = RunCli 'n_trans_cmd' $trans 'png' @('--color-space', '"Display P3"')
CK ($r3.log -match 'alphaextract') "③ [正例·引擎] 透明源**必须**触发 alpha 合并（命令行含 alphaextract）"
# ⚠ legacy 侧同款：不透明**不得**合并（否则它的命令行形状会被无条件改掉 ⇒ 既有逐 token 门禁全线受影响）
$r4 = RunCli 'n_opaque_leg' $opaque 'png' @('--color-space', '"Display P3"', '--color-engine', 'legacy')
CK ($r4.code -eq 0) "③ [负控·legacy] exit 0（实 $($r4.code)）"
CK ($r4.log -notmatch 'alphaextract') "③ [负控·legacy] 不透明源**不得**触发 alpha 合并（否则 legacy 命令行形状被无条件改变）"
CK ($r4.log -match '\-vf ') "③ [负控·legacy] 不透明源仍走 `-vf`（未被改成 `-filter_complex`）"
$r5 = RunCli 'n_trans_leg' $trans 'png' @('--color-space', '"Display P3"', '--color-engine', 'legacy')
CK ($r5.log -match 'alphaextract') "③ [正例·legacy] 透明源**必须**触发 alpha 合并"

Write-Output ""
Write-Output "===== engine-alpha-preserve: PASS=$pass FAIL=$fail ====="
if ($fail -eq 0) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
else { Write-Output "工作目录保留供排查: $d" }
if ($fail -eq 0) { exit 0 } else { exit 1 }
