# verify-color-out-matrix.ps1 —— 显式输出矩阵模式（color-matrix-options 批，C-GATE@1）的端到端判据面。
#
# 真值书: .codeartsdoer/plans/color-matrix-options/plan.md §2.2 C-GATE@1 + §3.1 T7(:394-407)。
# 断言事实依据: T1 扫描报告 tests/output/_tmp/color-matrix-scan-20261110/report.md §7 校准清单 C1-C13
#   + T6 实测取证 tests/output/_tmp/color-matrix-t6-probe/（8 案 + delivery_evidence.md 移交注意事项 9 条）。
#
# 判据面（7 组；正向臂 + 负控）:
#   A. avif nclx 读回（引擎通路 = RawColorPipeline.BuildEncodeArgs，pixfmt-switch 兑现）:
#      显式 bt2020nc ⇒ 引擎真转 YUV（yuv420p）+ ffprobe 读回 color_space=bt2020nc（AV1/H.273 枚举 9）;
#      auto/rgb 档对照 ⇒ gbrp 直喂 ⇒ color_space=gbr（nclx matrix=0，T6 A3 案语义）。
#      ⚠ 任务书原文「matrix_coefficients=10」是笔误：H.273 bt2020nc=9、bt2020cl=10，T1 §4 表为 9 —— 判据按 9/语义词锚定。
#      ⚠ 引擎通路需要「真转色彩」场景（Display P3 目标 ⇒ H1 Map）：直通（None）时走 legacy ffmpeg 命令，
#        那是孤立标签路径（T6 移交注意事项 3），本门禁另锁其孤立点名（A7）。
#   B. jxl XYB: 显式 xyb ⇒ cjxl 确认播报逐字 + 规划层 Reason 逐字 + jxlinfo 产物合法 + 命令行无 xyb 旗标
#      （cjxl v0.12.0 无显式开关，T1 §2）。⚠ jxlinfo 标准输出无 XYB 字段（实测 g8/g9 两臂 jxlinfo -v 同形、
#      均报 "Color space: RGB"——XYB 是 VarDCT 编码内部色空间，容器层不可区分，T1 §2 同口径）⇒ 复用 T6 的
#      播报/命令行级读法（T6 delivery_evidence §4 ② 同法）。auto 对照 = 确认播报缺席（auto 零播报）。
#   C. png fail-closed 负控: 显式 bt709 ⇒ 进程 exit=1 + 任务级「失败 (退出码 91)」+ 模板 3 逐字 + 零产物
#      （CM-REQ-05；exit 91 是任务级契约码——QueueProcessor legacyReject 消费点 :1380；headless 进程码
#      语义 = failed==0 ? 0 : 1，Program.cs:369，任务级 91 由日志承载）。
#   D. webp fail-closed 负控 + 冗余: 显式 bt709 ⇒ exit 91 契约 + 零产物 + 模板 3 逐字；
#      显式 bt470bg / smpte170m（bt601 归一成员）⇒ 产物哈希与 auto 档逐字节一致（C1/C3 校准 Redundant
#      = CM-REQ-08 auto 零行为变化的直接证据）+ 模板 2 逐字（{2}=bt470bg，T6 移交注意事项 1）。
#   E. jpg 冗余: 显式 bt470bg ⇒ 与 auto 的 ffmpeg 命令行剥路径后逐字相同（Redundant 定义级证据）
#      + 全帧 PSNR=inf 级像素一致。⚠ 不用裸哈希：jpg 出口 iccgen 嵌入的 ICC 含创建时间戳 ⇒
#      每轮产物字节必然不同（webp sRGB 无 ICC 才可裸哈希）——实测 g14/g15 命令行同、sha 异的根因。
#   F. cjpegli --xyb（C9 接线）+ 播报: 显式 xyb ⇒ cjpegli 命令行尾含 ` --xyb` + 模板 1 逐字；
#      auto 对照 = 命令行无 --xyb 且零播报; 显式 bt709 ⇒ Rejected 经异常路径收场（exit≠0、零产物、
#      点名逐字）——⚠ 按 T6 移交注意事项 2：cjxl/cjpegli 的 Rejected 走异常路径、进程码非 91，
#      文本里的 "exit 91" 是 C-REPORT@1 模板常量的一部分（契约文本），不是进程退出码断言。
#   G. 模板常量结构锁: C-REPORT@1 三常量逐字在场 + 全 ASCII（_probe-cjk-hardcode-scan 的 EmitWarn 硬约束）
#      + CMX-1 模板 4（OutMatrixReportConflict）逐字在场 + 全 ASCII（同 G 组口径）。
#   H. CLI 归一: `--color-out-matrix bt601` ⇒ 日志以 smpte170m 记账（C-TOKEN@1 别名归一，T3 面）。
#      ⚠ H 组在 T7 批以独立判据登记；本文件现状无 H 组 CK（T7 实测 H 的归一读数已由 D4 webp bt601
#      臂覆盖——别名归一进族的端到端证据在 D 组，T7 交付时合并）。
#   I. CMX 一致性批（2026-11-11，CMX-1/CMX-2 落地后的判据面，A1 严口径）:
#      I0 广色域源构造（运行时 avifenc --cicp，产物落本轮 GUID 区 sources/；ffprobe 读回 primaries 逐一断言）
#      I1 跨族向下拒绝（BT.2020 源 + avif + 显式 bt709 ⇒ 模板 4 逐字 + Reason 中文同义句 + 任务级 exit 91 + 零产物）
#      I2 跨族向上拒绝（BT.709 源 + avif + 显式 bt2020nc ⇒ 模板 4 逐字 + 零产物）
#      I3/I4 601 族互通双向（smpte170m 源 + bt470bg / bt470bg 源 + smpte170m ⇒ Carried 出产物 + Reason 承载播报）
#      I5 601 像素对拍（--chroma 4:2:0 强制 YUV 转换后两臂 PSNR=inf⇒99；601 族系数相同非平凡兑现）
#      I6 无标注源同值 Carried（闸不误伤：src_8bit sRGB 语义 + avif + bt709 ⇒ 出产物 + Reason 承载）。
#      ⚠ 实测登记：CMX-1 判定表第 5 行「Reason 点名『源 primaries 未标注，显式矩阵以用户值为准』」
#        在 CLI 端到端**不可达**——ColorSpaceIdentify.NormalizePrimaries 把无标注 primaries 归一为
#        bt709（sRGB 惯例假定，P9）⇒ 推导链恒非 null；具名 ICC 空间（ProPhoto）走 S1 推荐落点
#        （dst=BT.2020）⇒ 推导 bt2020nc 亦非 null。推导 null 的真执行证明在 CMX-1 selfcheck ⑤
#        （Add-Type 反射，16/0，tests/output/_tmp/cmx1-consistency/）。
#      I7 auto 早退回归（CM-REQ-08 绝对基线）：webp auto sha256[:16] == 9B6B2B1587840E05（D1 相对
#        一致之外加绝对锚；CMX-1 校验闸在 Carried 分支入口、auto 早退在更前面 ⇒ 读数证明零影响）
#        + BT.2020 源 auto 零播报（闸对 auto 不触发）。
#   -SelfCheck: 负控臂（反向判读，预期红）——证明判读链有牙（对齐 verify-ui-param-matrix 的
#     -SelfCheck 契约：负控转红是预期，正向臂照常全绿、整体 exit 0）。CMX 批加第二臂：
#     跨族拒绝（BT.2020 源 + avif + bt709）反向判读「exit==0 且有产物」期望被真实行为推翻。
#
# ⚠ 判读锚定教训（本轮实测）：产物目录名 = tests/output/color-out-matrix/<guid>/ 与播报前缀
#   `[color-out-matrix] ` 同串——Debug 日志回显的 ffmpeg/cjxl/cjpegli 命令行必含产物路径，
#   裸串 `-notmatch 'color-out-matrix'` 会把「产物路径在场」误判成「播报在场」（首轮 4 臂假红）。
#   ⇒ auto 零播报判据一律锚定**播报行形态** `\[color-out-matrix\] `（闭合方括号+空格；产物路径
#   是 `color-out-matrix\` 或 `/color-out-matrix/`，永不带 `] `）。
#
# house 契约: 末行 `PASS=n FAIL=m` + fail ⇒ exit 1；产物落 tests/output/color-out-matrix/<guid>/
#   （AGENTS §3：一轮一目录）；TMP/TEMP/TMPDIR 钉 tests/output/_tmp/round-tmp（AGENTS §4）；
#   被测 exe 打印（2026-09-21 第 84 条口径：回退是静默的 ⇒ 必须点名 Release/Debug）。
# 双宿主: 禁 PS7+ 语法；代码串纯 ASCII（中文进注释）。
param(
    [int]$TimeoutSec = 240,
    [switch]$SelfCheck,
    [switch]$KeepWork   # 调试档：保留产物区（正常轮自删；AGENTS §3 一轮一目录不受影响）
)
$ErrorActionPreference = 'Continue'
$scriptDir = $PSScriptRoot
$testsDir  = Split-Path -Parent $scriptDir
$repoRoot  = Split-Path -Parent $testsDir
Set-Location $repoRoot

# ── TMP 钉扎（AGENTS §4；本机沙箱按进程拦子进程向仓库外写） ──────────────────────
$roundTmp = Join-Path $repoRoot 'tests/output/_tmp/round-tmp'
New-Item -ItemType Directory -Force -Path $roundTmp | Out-Null
foreach ($k in @('TMP','TEMP','TMPDIR')) { [Environment]::SetEnvironmentVariable($k, $roundTmp, 'Process') }

$pass = 0; $fail = 0
function CK($ok, $msg) {
    if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
    else     { $script:fail++; Write-Output "  FAIL $msg" }
}

# ── 被测 exe + 工具（2026-09-21 第 84 条口径：点名被测件与构建类型） ──────────────
$exe = "$repoRoot/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
$buildKind = 'Release'
if (-not (Test-Path $exe)) { $exe = "$repoRoot/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe"; $buildKind = 'Debug (fallback)' }
Write-Output "[gate] exe=$exe ($buildKind)"
$ff = "$repoRoot/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$repoRoot/publish/PLAN/ffmpeg-full/ffprobe.exe"
$jxlinfo = "$repoRoot/publish/PLAN/jxl/bin/jxlinfo.exe"
foreach ($need in @($exe, $ff, $fp, $jxlinfo)) {
    if (-not (Test-Path $need)) { Write-Output "  FAIL tool missing: $need"; Write-Output "OUTMATRIX RESULT: pass=0 fail=1"; exit 1 }
}
CK $true "tools present (exe/ffmpeg/ffprobe/jxlinfo)"

$src = "$repoRoot/tests/output/sources/src_8bit.png"
if (-not (Test-Path $src)) { Write-Output "  FAIL source missing: $src（先跑 ensure-fixtures.ps1）"; Write-Output "OUTMATRIX RESULT: pass=0 fail=1"; exit 1 }
CK $true "source present ($src)"

# ── 产物区（AGENTS §3：一轮一 GUID 目录；跑完自删，红轮证据按 --runs 清扫管辖保留） ──
$work = Join-Path $repoRoot ('tests/output/color-out-matrix/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

# ── helpers（形态全部取自在册门禁，本文件不复明发明尺） ─────────────────────────
# Read-TextShared: 孙进程遗留句柄容错读（verify-ui-param-matrix 同款；AGENTS §7 文件锁假红教训）。
function Read-TextShared([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    for ($i = 0; $i -lt 25; $i++) {
        try {
            $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
                  [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            $sr = New-Object System.IO.StreamReader($fs)
            $t = $sr.ReadToEnd(); $sr.Dispose()
            return $t
        } catch { Start-Sleep -Milliseconds 200 }
    }
    return ''
}
# Find-Prod: 产物查找（-Include 与无通配路径的组合在部分宿主下不命中 ⇒ 用 Extension 过滤，
#   探测轮实测：probe3 probe4 两次 -Include 假阴性、本形态命中）。
function Find-Prod([string]$dir) {
    $f = Get-ChildItem -Recurse -LiteralPath $dir -File -ErrorAction SilentlyContinue |
         Where-Object { $_.Extension -match '^\.(avif|png|webp|jpg|jpeg|jxl)$' } | Select-Object -First 1
    if ($f) { $f.FullName } else { $null }
}
# Run: headless 一臂。⚠ FfmpegGui.exe 是 GUI 子系统（WinExe）⇒ pwsh `&` 调用不等待且
#   $LASTEXITCODE 不设置（探测轮实测 rc 文件空、[int]'' 假读 0）⇒ 用 Start-Process -Wait -PassThru
#   （verify-decision-delivery RunCli 在册形态），退出码从进程对象读（-Wait 在位 ⇒ 双宿主均有值）。
#   ArgumentList 手动给含空格元素加内嵌引号（decision-delivery 同款，探测轮已验证）。
function Run([string]$tag, [string]$fmt, [string[]]$extra, [string]$srcFile = '') {
    $o = Join-Path $work $tag
    if (Test-Path $o) { Remove-Item -Recurse -Force $o -ErrorAction SilentlyContinue }
    # CMX 批：I 组多源臂（avifenc --cicp 构造的 CICP 标注源）⇒ 可选源覆盖（默认夹具 src_8bit.png）。
    $useSrc = $src
    if ($srcFile -ne '') { $useSrc = $srcFile }
    $a = @('--headless', '-i', "`"$useSrc`"", '--format', $fmt, '--output', "`"$o`"", '--log-level', 'Debug') + $extra
    $aQ = @(); foreach ($x in $a) { if ($x -match '\s') { $aQ += ('"' + $x + '"') } else { $aQ += $x } }
    $child = Start-Process -FilePath $exe -ArgumentList $aQ -Wait -NoNewWindow -PassThru `
             -RedirectStandardOutput (Join-Path $work "$tag.o") -RedirectStandardError (Join-Path $work "$tag.e")
    $log = (Read-TextShared (Join-Path $work "$tag.o")) + (Read-TextShared (Join-Path $work "$tag.e"))
    $prod = Find-Prod $o
    return @{ code = $child.ExitCode; log = $log; file = $prod; dir = $o }
}
# ffprobe 读回 color_space/color_range/pix_fmt（decision-delivery Cicp 同款，只读 stdout）。
function ProbeCs([string]$f) {
    $o = Join-Path $work '_cs.txt'
    Start-Process -FilePath $fp -ArgumentList @('-v','error','-select_streams','v:0','-show_entries',
        'stream=color_space,color_range,pix_fmt','-of','csv=p=0', $f) -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o | Out-Null
    return ((Read-TextShared $o).Trim())
}
# 全帧 PSNR（decision-delivery PsnrTwo 同款；inf ⇒ 99）。
function PsnrTwo([string]$prod, [string]$ref) {
    $o = Join-Path $work '_psnr.txt'
    $graph = 'format=rgb24[a];[1:v]format=rgb24[b];[a][b]psnr'
    Start-Process -FilePath $ff -ArgumentList @('-hide_banner','-i',"`"$prod`"",'-i',"`"$ref`"",'-lavfi',"`"$graph`"",'-f','null','-') `
        -Wait -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError (Join-Path $work '_psnr.e') | Out-Null
    $t = (Read-TextShared $o) + (Read-TextShared (Join-Path $work '_psnr.e'))
    if ($t -match 'average:\s*inf') { return 99.0 }
    $m = [regex]::Match($t, 'average:\s*([0-9.]+)')
    if ($m.Success) { return [double]$m.Groups[1].Value }
    return -1.0
}
# ffmpeg 命令行抽取：Debug 日志的命令行行（ffmpeg `[cmd] ffmpeg ` / cjpegli `[cjpegli] cjpegli.exe` /
#   cjxl `[cjxl] cjxl.exe`），剥所有引号路径段（产物路径含 GUID 必不同）⇒ 比中段参数序列。
function CmdLineOf([string]$log) {
    $ln = ($log -split "`r?`n" | Select-String '\[cmd\] ffmpeg |\[cjpegli\] cjpegli\.exe|\[cjxl\] cjxl\.exe' |
           Select-Object -First 1)
    if (-not $ln) { return '' }
    return ($ln.Line -replace '"[^"]*"', '<P>').Trim()
}
function LogHit([string]$log, [string]$pat, [int]$n = 2) {
    return @(($log -split "`r?`n") | Select-String $pat | Select-Object -First $n | ForEach-Object { $_.Line.Trim() })
}

# C-REPORT@1 三常量（G 组结构锁源）：ColorStrategyPlanning.cs（ColorMappingEngine 类）。
$planningCs = "$repoRoot/src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs"
$planText = [System.IO.File]::ReadAllText($planningCs)

Write-Output ""
Write-Output "===== A avif nclx (engine path, pixfmt-switch) ====="

# A1: 显式 bt709 ⇒ 引擎真转 YUV + nclx 兑现（H.273 枚举 1）。
#   ⚠ CMX 批改锚（2026-11-11，A1 严口径落地后的必然适配）：原锚 bt2020nc 与 Display P3 目标
#   （smpte432 ⇒ MatrixTokenFor 推导 bt709）构成**跨族**（bt2020nc vs bt709）⇒ CMX-1 一致性闸
#   Rejected（exit 91 零产物）——基线轮实测 A1 群 5 红。显式值改同族 bt709（同值 Carried），
#   「引擎通路 pixfmt-switch 兑现」的验证语义不变（bt709 同属 H.273 YUV 族、同驱动 -colorspace
#   落地 + YUV 转换；readback bt709 = H.273 枚举 1）。跨族行为本身由 I 组接管（I1/I2）。
$r = Run 'a1_bt709' 'avif' @('--color-engine','engine','--color-space','Display P3','--color-out-matrix','bt709')
$cs1 = if ($r.file) { ProbeCs $r.file } else { '' }
CK ($r.code -eq 0 -and ($null -ne $r.file)) "A1 avif x bt709 (engine, Display P3 target): exit=$($r.code), produced=$($null -ne $r.file)"
CK ($cs1 -match 'bt709') "A1 nclx readback color_space=bt709 (H.273 enum 1; CMX same-value Carried; full='$cs1')"
CK ($cs1 -match 'yuv') "A1 pix_fmt really switched to YUV (pixfmt-switch landed; full='$cs1')"

# A2: 模板 1 落地播报逐字（引擎 EmitOutMatrixReport：-colorspace <token>；CMX 同值 bt709 锚）
$hit = LogHit $r.log '\[color-out-matrix\] avif: bt709 -> -colorspace bt709' 1
CK ($hit.Count -ge 1) "A2 template-1 landed verbatim: '[color-out-matrix] avif: bt709 -> -colorspace bt709'"
# A2b: 规划层 Reason 载体（C-REPORT@1 通道既有约定：Reason 进任务详情/日志）
$hit2 = LogHit $r.log '用户 --color-out-matrix=bt709 已由 avif 出口承载' 1
CK ($hit2.Count -ge 1) "A2b planning-layer Reason verbatim (ColorTransformPlan.Reason channel)"

# A3: auto 对照 = gbrp 直喂 ⇒ nclx=gbr（T6 A3 案基线；T1 §6.5 引擎 auto 基线）
$rAuto = Run 'a3_auto' 'avif' @('--color-engine','engine','--color-space','Display P3')
$csA = if ($rAuto.file) { ProbeCs $rAuto.file } else { '' }
CK ($rAuto.code -eq 0 -and ($null -ne $rAuto.file)) "A3 avif auto (engine): exit=$($rAuto.code), produced=$($null -ne $rAuto.file)"
CK ($csA -match 'gbr') "A3 auto baseline readback = gbr (gbrp feed => nclx matrix=0; full='$csA')"

# A4: 显式 rgb ⇒ gbrp 直喂（outMatrixRgbCarried 否决 YUV 改写）⇒ nclx=gbr（T6 A2/A3 案）
$rRgb = Run 'a4_rgb' 'avif' @('--color-engine','engine','--color-space','Display P3','--color-out-matrix','rgb')
$csR = if ($rRgb.file) { ProbeCs $rRgb.file } else { '' }
CK ($rRgb.code -eq 0 -and ($null -ne $rRgb.file)) "A4 avif x rgb (engine): exit=$($rRgb.code), produced=$($null -ne $rRgb.file)"
CK ($csR -match 'gbr') "A4 rgb readback = gbr (nclx matrix_coefficients=0 via gbrp feed; full='$csR')"
$hit = LogHit $rRgb.log '\[color-out-matrix\] avif: rgb -> nclx matrix_coefficients=0 \(identity, RGB 4:4:4 gbrp feed\)' 1
CK ($hit.Count -ge 1) "A5 template-1 landed verbatim (rgb => nclx matrix_coefficients=0 description)"

# A6: auto 档零播报（C-REPORT@1「auto 不播报」+ CM-REQ-08 零噪声）
CK ($rAuto.log -notmatch '\[color-out-matrix\] ') "A6 auto arm has ZERO [color-out-matrix] lines (silence contract)"

# A7: legacy 直通孤立点名（T6 移交注意事项 3：判据未命中 ⇒ -colorspace 被忽略，仍写 flag + 点名）。
#   ⚠ CMX 批改锚：显式值 bt2020nc → **bt709**（同值）。原 bt2020nc 在 CMX-1 闸下与 src_8bit.png
#   （无标注 ⇒ NormalizePrimaries 归一 sRGB ⇒ 推导 bt709）构成跨族 ⇒ 规划层直接拒绝、到不了
#   出口（基线轮实测 A7 群 2 红）⇒ 「legacy 直通孤立点名」的验证语义改由同值 bt709 承载——
#   Carried 过闸、flag 写入、直通不改像素 ⇒ 孤立点名逐字保留。
$rL = Run 'a7_legacy' 'avif' @('--color-engine','legacy','--color-out-matrix','bt709')
$csL = if ($rL.file) { ProbeCs $rL.file } else { '' }
$hit = LogHit $rL.log 'no YUV -pix_fmt was forced by the chroma/bit-depth criteria' 1
CK ($hit.Count -ge 1) "A7 legacy direct-pass orphan-tag advisory emitted verbatim (T6 known-boundary note 2; CMX same-value bt709)"
CK ($rL.code -eq 0 -and ($null -ne $rL.file)) "A7 legacy arm still produced output (flag written, auto-same shape; exit=$($rL.code); prod=$($null -ne $rL.file))"

# A8: avif x ycgco ⇒ Rejected（T1 C4 校准）+ 模板 3 逐字（规划层发）
$rY = Run 'a8_ycgco' 'avif' @('--color-out-matrix','ycgco')
$hit = LogHit $rY.log 'ycgco is not carried by avif \(swscale conversion for ycgco proven impossible on the input side; output side unverified => rejected conservatively\) => contract rejection, exit 91, no output' 1
CK ($hit.Count -ge 1) "A8 avif x ycgco template-3 verbatim (C4: rejected conservatively)"
CK ($rY.code -ne 0 -and ($null -eq $rY.file)) "A8 avif x ycgco: exit=$($rY.code) != 0, no output (plan-level Reject)"

Write-Output ""
Write-Output "===== B jxl XYB ====="

# B1: 显式 xyb（默认 backend=Cjxl）⇒ 确认播报逐字（C8：cjxl v0.12.0 无显式开关）
$rJ = Run 'b1_xyb' 'jxl' @('--color-out-matrix','xyb')
$hit = LogHit $rJ.log "\[cjxl\] \[color-out-matrix\] xyb confirmed: cjxl's VarDCT mode carries XYB" 1
CK ($rJ.code -eq 0 -and ($null -ne $rJ.file)) "B1 jxl x xyb (cjxl backend): exit=$($rJ.code), produced=$($null -ne $rJ.file)"
CK ($hit.Count -ge 1) "B1 cjxl xyb-confirmed advisory verbatim (C8: implicit VarDCT carrier, no flag exists)"
$hit = LogHit $rJ.log '用户 --color-out-matrix=xyb 已由 jxl 出口承载' 1
CK ($hit.Count -ge 1) "B1b planning-layer Reason verbatim (jxl xyb Carried)"

# B2: jxlinfo 产物合法（XYB 编码内部表示在容器层不可区分——T1 §2；判据=有效 JXL + 与 auto 同形读数）
if ($rJ.file) {
    $ji = Join-Path $work '_ji_xyb.txt'
    Start-Process -FilePath $jxlinfo -ArgumentList @('-v', $rJ.file) -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $ji | Out-Null
    $jiT = Read-TextShared $ji
    CK ($jiT -match 'JPEG XL image') "B2 jxlinfo parses xyb product as valid JXL (container-level read; XYB not visible at container layer per T1 section-2)"
} else { CK $false "B2 jxlinfo on xyb product (product missing)" }

# B3: auto 对照 = 确认播报缺席（auto 零播报）
$rJA = Run 'b3_auto' 'jxl' @()
CK ($rJA.log -notmatch '\[color-out-matrix\] ') "B3 jxl auto has ZERO [color-out-matrix] lines (auto silence)"

# B4: jxl x bt709 ⇒ Rejected（RgbNative）+ 模板 3 逐字
$rJB = Run 'b4_bt709' 'jxl' @('--color-out-matrix','bt709')
$hit = LogHit $rJB.log 'bt709 is not carried by jxl \(JXL is an RGB-native container \(RgbNative\); no YUV carrier\) => contract rejection, exit 91, no output' 1
CK ($hit.Count -ge 1) "B4 jxl x bt709 template-3 verbatim (RgbNative rejection)"
CK ($rJB.code -ne 0 -and ($null -eq $rJB.file)) "B4 jxl x bt709: exit=$($rJB.code) != 0, no output"

Write-Output ""
Write-Output "===== C png fail-closed (plan-level Rejected => exit 91) ====="

# C1: 显式 bt709 ⇒ 任务级退出码 91 + 模板 3 逐字 + 零产物
$rP = Run 'c1_png_bt709' 'png' @('--color-out-matrix','bt709')
$hit = LogHit $rP.log '失败 \(退出码 91\)' 1
CK ($hit.Count -ge 1) "C1 png x bt709: task-level exit code 91 verbatim in log ('失败 (退出码 91)')"
$hit = LogHit $rP.log 'bt709 is not carried by png \(RGB-native/palette container has no YUV matrix concept\) => contract rejection, exit 91, no output' 1
CK ($hit.Count -ge 1) "C1 template-3 verbatim (png Rejected by planning layer)"
CK ($rP.code -ne 0 -and ($null -eq $rP.file)) "C1 png x bt709: process exit=$($rP.code) != 0, ZERO output (CM-REQ-05 fail-closed)"
$hit = LogHit $rP.log '\[色彩裁决\] 裁决=Reject' 1
CK ($hit.Count -ge 1) "C1 decision verdict = Reject (queue-level contract path)"

# C2: png x rgb ⇒ Redundant + 模板 2 逐字（{2}=RGB）
$rPR = Run 'c2_png_rgb' 'png' @('--color-out-matrix','rgb')
$hit = LogHit $rPR.log '\[color-out-matrix\] rgb on png: container is already RGB; recorded, no pixel effect' 1
CK ($rPR.code -eq 0 -and ($null -ne $rPR.file)) "C2 png x rgb (Redundant): exit=$($rPR.code), produced=$($null -ne $rPR.file)"
CK ($hit.Count -ge 1) "C2 template-2 verbatim ({2}=RGB; recorded, no pixel effect)"

Write-Output ""
Write-Output "===== D webp fail-closed + redundant hash-parity (CM-REQ-08) ====="

# D1: webp auto 基线哈希
$rWA = Run 'd1_webp_auto' 'webp' @()
$shaAuto = if ($rWA.file) { (Get-FileHash $rWA.file -Algorithm SHA256).Hash } else { '' }
CK ($rWA.code -eq 0 -and ($null -ne $rWA.file)) "D1 webp auto baseline: exit=$($rWA.code), produced=$($null -ne $rWA.file)"
CK ($rWA.log -notmatch '\[color-out-matrix\] ') "D1b webp auto has ZERO [color-out-matrix] lines"

# D2: 显式 bt470bg ⇒ 哈希与 auto 逐字节一致（C1 校准 Redundant）+ 模板 2 逐字（{2}=bt470bg）
$rWB = Run 'd2_webp_bt470bg' 'webp' @('--color-out-matrix','bt470bg')
$shaB = if ($rWB.file) { (Get-FileHash $rWB.file -Algorithm SHA256).Hash } else { '' }
$hit = LogHit $rWB.log '\[color-out-matrix\] bt470bg on webp: container is already bt470bg; recorded, no pixel effect' 1
CK ($rWB.code -eq 0 -and ($null -ne $rWB.file)) "D2 webp x bt470bg (Redundant): exit=$($rWB.code), produced=$($null -ne $rWB.file)"
CK ($hit.Count -ge 1) "D2 template-2 verbatim ({2}=bt470bg per T6 handover note 1)"
CK ($shaAuto -ne '' -and $shaAuto -eq $shaB) "D2 webp bt470bg hash == auto hash (byte-identical: sha16 $(if($shaAuto){$shaAuto.Substring(0,16)}else{'-'}))"

# D3: 显式 smpte170m（601 族）⇒ 同哈希（C3 校准：601 族 Redundant）
$rWS = Run 'd3_webp_smpte170m' 'webp' @('--color-out-matrix','smpte170m')
$shaS = if ($rWS.file) { (Get-FileHash $rWS.file -Algorithm SHA256).Hash } else { '' }
CK ($shaAuto -ne '' -and $shaAuto -eq $shaS) "D3 webp smpte170m hash == auto hash (601-family redundant; sha16 $(if($shaS){$shaS.Substring(0,16)}else{'-'}))"

# D4: 显式 bt601（别名）⇒ 归一 smpte170m 后同哈希（C-TOKEN@1 别名归一，T3 面）
$rW6 = Run 'd4_webp_bt601' 'webp' @('--color-out-matrix','bt601')
$sha6 = if ($rW6.file) { (Get-FileHash $rW6.file -Algorithm SHA256).Hash } else { '' }
CK ($shaAuto -ne '' -and $shaAuto -eq $sha6) "D4 webp bt601 hash == auto hash (bt601 normalized to smpte170m; sha16 $(if($sha6){$sha6.Substring(0,16)}else{'-'}))"

# D5: 显式 bt709 ⇒ exit 91 契约 + 模板 3 逐字 + 零产物
$rW7 = Run 'd5_webp_bt709' 'webp' @('--color-out-matrix','bt709')
$hit = LogHit $rW7.log 'bt709 is not carried by webp \(WebP container has no matrix annotation; conversion would mismatch the decoder.s fixed BT\.601 assumption \(per 2026-11-03 colorRange policy\)\) => contract rejection, exit 91, no output' 1
CK ($hit.Count -ge 1) "D5 webp x bt709 template-3 verbatim (2026-11-03 colorRange policy anchor)"
CK ($rW7.code -ne 0 -and ($null -eq $rW7.file)) "D5 webp x bt709: exit=$($rW7.code) != 0, ZERO output"

Write-Output ""
Write-Output "===== E jpg redundant (cmdline-parity + pixel parity) ====="

# E1: 显式 bt470bg vs auto：ffmpeg 命令行剥路径后逐字相同（Redundant 定义级证据）。
#   ⚠ 不用裸哈希：jpg 出口 iccgen 的 ICC 含创建时间戳 ⇒ 每轮字节必不同（探测轮实测根因）。
$rJA2 = Run 'e1_jpg_auto' 'jpg' @()
$rJB2 = Run 'e1_jpg_bt470bg' 'jpg' @('--color-out-matrix','bt470bg')
$cmdA = CmdLineOf $rJA2.log
$cmdB = CmdLineOf $rJB2.log
CK ($rJA2.code -eq 0 -and ($null -ne $rJA2.file)) "E1 jpg auto: exit=$($rJA2.code), produced=$($null -ne $rJA2.file)"
CK ($rJB2.code -eq 0 -and ($null -ne $rJB2.file)) "E1 jpg x bt470bg: exit=$($rJB2.code), produced=$($null -ne $rJB2.file)"
CK ($cmdA -ne '' -and $cmdA -eq $cmdB) "E1 ffmpeg cmdline (path-stripped) identical between auto and bt470bg (Redundant = same-value landing)"
$hit = LogHit $rJB2.log '\[color-out-matrix\] bt470bg on jpg: container is already bt470bg; recorded, no pixel effect' 1
CK ($hit.Count -ge 1) "E1b template-2 verbatim on jpg ({2}=bt470bg)"
# E2: 像素级一致（全帧 PSNR=inf 级）——同命令行同源 ⇒ 逐像素相同
if ($rJA2.file -and $rJB2.file) {
    $psnr = PsnrTwo $rJB2.file $rJA2.file
    CK ($psnr -ge 50.0) "E2 jpg bt470bg vs auto pixel parity: PSNR=$psnr dB >= 50 (99 = inf, byte-equal pixels)"
} else { CK $false "E2 pixel parity skipped (missing product)" }

# E3: jpg x bt709 ⇒ Rejected + 模板 3 逐字 + 零产物
$rJ7 = Run 'e3_jpg_bt709' 'jpg' @('--color-out-matrix','bt709')
$hit = LogHit $rJ7.log 'bt709 is not carried by jpg \(conversion coefficients would deviate from the decoder.s fixed BT\.601 assumption \(2026-10-25 measured 255,0,0 -> 232,0,1\); JFIF has no matrix annotation\) => contract rejection, exit 91, no output' 1
CK ($hit.Count -ge 1) "E3 jpg x bt709 template-3 verbatim (BT.601 decoder-assumption anchor)"
CK ($rJ7.code -ne 0 -and ($null -eq $rJ7.file)) "E3 jpg x bt709: exit=$($rJ7.code) != 0, ZERO output"

Write-Output ""
Write-Output "===== F cjpegli --xyb (C9 wiring) + Rejected-by-exception boundary ====="

# F1: 显式 xyb ⇒ cjpegli 命令行尾含 --xyb + 模板 1 逐字（C9 接线的两层证据）
$rC = Run 'f1_cjpegli_xyb' 'jpg' @('-e','cjpegli','--color-out-matrix','xyb')
$cmdC = CmdLineOf $rC.log
CK ($rC.code -eq 0 -and ($null -ne $rC.file)) "F1 jpg(cjpegli) x xyb: exit=$($rC.code), produced=$($null -ne $rC.file)"
CK ($cmdC -match '--xyb') "F1 cjpegli command line contains --xyb (C9 wiring evidence)"
$hit = LogHit $rC.log 'xyb -> cjpegli --xyb \(encoder-internal feature' 1
CK ($hit.Count -ge 1) "F1 template-1 landed verbatim (cjpegli --xyb description)"

# F2: auto 对照 = 命令行无 --xyb + 零播报（CM-REQ-08：cjpegli auto 命令行逐字节不变）
$rCA = Run 'f2_cjpegli_auto' 'jpg' @('-e','cjpegli')
$cmdCA = CmdLineOf $rCA.log
CK ($cmdCA -ne '' -and $cmdCA -notmatch '--xyb') "F2 cjpegli auto command line has NO --xyb"
CK ($rCA.log -notmatch '\[color-out-matrix\] ') "F2 cjpegli auto has ZERO [color-out-matrix] lines"

# F3: 显式 bt709 ⇒ Rejected 经异常路径（T6 移交注意事项 2：进程码非 91 是已知边界，不是契约失败）
#   判据 = exit != 0 + 零产物 + 出口点名逐字（文本含模板常量的 "exit 91" 契约措辞）。
$rCB = Run 'f3_cjpegli_bt709' 'jpg' @('-e','cjpegli','--color-out-matrix','bt709')
$hit = LogHit $rCB.log 'bt709 is not carried by jpg \(cjpegli encodes from RGB directly; no YUV conversion point\) => contract rejection, exit 91, no output' 1
CK ($hit.Count -ge 1) "F3 cjpegli x bt709 exit-layer advisory verbatim (cjpegli Rejected; text carries contract wording)"
CK ($rCB.code -ne 0 -and ($null -eq $rCB.file)) "F3 cjpegli x bt709: exit=$($rCB.code) != 0, ZERO output (exception-path termination; process code is NOT 91 by design - T6 boundary note 2)"

Write-Output ""
Write-Output "===== G template constants structural lock (C-REPORT@1) ====="

# G1-G3: 三常量逐字在场（源码文本级；T6 EmitWarn 消费、本门禁端到端已验模板 1/2/3）
$const1 = 'OutMatrixReportLanded = "\[color-out-matrix\] \{0\}: \{1\} -> \{2\}"'
$const2 = 'OutMatrixReportRedundant = "\[color-out-matrix\] \{0\} on \{1\}: container is already \{2\}; recorded, no pixel effect"'
$const3 = 'OutMatrixReportRejected = "\[color-out-matrix\] \{0\} is not carried by \{1\} \(\{2\}\) => contract rejection, exit 91, no output"'
CK ([regex]::Matches($planText, $const1).Count -eq 1) "G1 OutMatrixReportLanded constant verbatim in ColorStrategyPlanning.cs (exactly 1)"
CK ([regex]::Matches($planText, $const2).Count -eq 1) "G2 OutMatrixReportRedundant constant verbatim (exactly 1)"
CK ([regex]::Matches($planText, $const3).Count -eq 1) "G3 OutMatrixReportRejected constant verbatim (exactly 1)"
# G4: 三常量全 ASCII（取常量定义行的右值段，逐字符 < 128）
$okAscii = $true
foreach ($cn in @('OutMatrixReportLanded','OutMatrixReportRedundant','OutMatrixReportRejected')) {
    $m = [regex]::Match($planText, ($cn + ' = "([^"]*)"'))
    if (-not $m.Success) { $okAscii = $false; continue }
    foreach ($ch in $m.Groups[1].Value.ToCharArray()) { if ([int]$ch -ge 128) { $okAscii = $false } }
}
CK $okAscii "G4 all three template constants are pure ASCII (EmitWarn hard constraint)"
# G5/G6（CMX 批）：模板 4（跨维冲突拒绝）逐字在场 + 全 ASCII——I1/I2 端到端消费它的实例化文本，
#   这里锁常量本体（与 G1-G4 同口径；CMX-1 落地 :1354）。
$const4 = 'OutMatrixReportConflict = "\[color-out-matrix\] \{0\} conflicts with derived matrix \{1\} for \{2\} output => contract rejection, exit 91, no output"'
CK ([regex]::Matches($planText, $const4).Count -eq 1) "G5 OutMatrixReportConflict constant verbatim (CMX-1 template-4; exactly 1)"
$m4 = [regex]::Match($planText, ('OutMatrixReportConflict = "([^"]*)"'))
$ascii4 = $m4.Success
if ($ascii4) { foreach ($ch in $m4.Groups[1].Value.ToCharArray()) { if ([int]$ch -ge 128) { $ascii4 = $false } } }
CK $ascii4 "G6 template-4 constant is pure ASCII (EmitWarn hard constraint; CMX-1)"

Write-Output ""
Write-Output "===== I CMX cross-family consistency (A1 strict gate; CMX-1/CMX-2 batch) ====="

# ── I0 广色域源构造（运行时；产物落本轮 GUID 区 sources/——AGENTS §3 一轮一目录） ──
# ffmpeg 的 PNG 编码器不写 cICP（实测 chunk 零标注）⇒ CICP 标注源用 avifenc --cicp P/T/M
# （nclx colr box 原生可写；H.273：primaries 9=bt2020 / 1=bt709 / 6=smpte170m / 5=bt470bg）。
# 像素体用夹具 src_8bit.png（avifenc 吃 png 输入）。先 ffprobe 读回 primaries 逐一断言。
$avifenc = "$repoRoot/publish/PLAN/artifacts/avifenc.exe"
if (-not (Test-Path $avifenc)) { Write-Output "  FAIL tool missing: $avifenc"; Write-Output "OUTMATRIX RESULT: pass=$pass fail=$($fail+1)"; if (-not $KeepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }; exit 1 }
CK $true "I0 tool present (avifenc, PLAN artifacts)"
$isrc = Join-Path $work 'sources'
New-Item -ItemType Directory -Force -Path $isrc | Out-Null
$srcBt2020 = Join-Path $isrc 'src_bt2020.avif'
$srcBt709  = Join-Path $isrc 'src_bt709.avif'
$srcSm170  = Join-Path $isrc 'src_smpte170m.avif'
$srcBg601  = Join-Path $isrc 'src_bt470bg.avif'
# --cicp P/T/M（avifenc 文档序；transfer 取该 primaries 的历史配对：1=bt709 / 13=170M / 4=gamma2.8）
& $avifenc -q 70 -s 9 --cicp 9/1/9  $src $srcBt2020 *> $null
& $avifenc -q 70 -s 9 --cicp 1/13/1 $src $srcBt709  *> $null
& $avifenc -q 70 -s 9 --cicp 6/13/6 $src $srcSm170  *> $null
& $avifenc -q 70 -s 9 --cicp 5/4/5  $src $srcBg601  *> $null
function ProbePrimaries([string]$f) {
    $o = Join-Path $work '_pri.txt'
    Start-Process -FilePath $fp -ArgumentList @('-v','error','-select_streams','v:0','-show_entries',
        'stream=color_primaries','-of','csv=p=0', $f) -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o | Out-Null
    return ((Read-TextShared $o).Trim())
}
foreach ($p in @(@($srcBt2020,'bt2020'), @($srcBt709,'bt709'), @($srcSm170,'smpte170m'), @($srcBg601,'bt470bg'))) {
    $pri = ProbePrimaries $p[0]
    CK ($pri -eq $p[1]) "I0 source $($p[1]) primaries readback == $($p[1]) (avifenc --cicp nclx; full='$pri')"
}

# ── I1 跨族向下拒绝（BT.2020 源 + avif + 显式 bt709；CMX-1 判定表第 7 行核心格） ──
#   推导链：avif 直通（无目标色彩空间）沿用源 primaries=bt2020 ⇒ MatrixTokenFor ⇒ bt2020nc；
#   显式 bt709 与之跨族 ⇒ Rejected + 模板 4 + Reason 中文同义句 + 任务级 exit 91 + 零产物。
$rI1 = Run 'i1_xdown' 'avif' @('--color-out-matrix','bt709') $srcBt2020
$hit = LogHit $rI1.log 'bt709 conflicts with derived matrix bt2020nc for avif output => contract rejection, exit 91, no output' 1
CK ($hit.Count -ge 1) "I1 template-4 verbatim (bt709 vs derived bt2020nc; cross-family down)"
$hit = LogHit $rI1.log '显式值与目标 primaries 推导矩阵 bt2020nc 不一致 ⇒ 契约拒绝' 1
CK ($hit.Count -ge 1) "I1 Reason zh-synonym verbatim (CMX-1 cmxZh channel)"
CK ($rI1.code -ne 0 -and ($null -eq $rI1.file)) "I1 cross-family: exit=$($rI1.code) != 0, ZERO output (task-level exit 91 contract)"
$hit = LogHit $rI1.log '失败 \(退出码 91\)' 1
CK ($hit.Count -ge 1) "I1 task-level exit code 91 verbatim in log"

# ── I2 跨族向上拒绝（BT.709 源 + avif + 显式 bt2020nc；同闸反向） ──
$rI2 = Run 'i2_xup' 'avif' @('--color-out-matrix','bt2020nc') $srcBt709
$hit = LogHit $rI2.log 'bt2020nc conflicts with derived matrix bt709 for avif output => contract rejection, exit 91, no output' 1
CK ($hit.Count -ge 1) "I2 template-4 verbatim (bt2020nc vs derived bt709; cross-family up)"
CK ($rI2.code -ne 0 -and ($null -eq $rI2.file)) "I2 cross-family: exit=$($rI2.code) != 0, ZERO output"

# ── I3/I4 601 族互通双向（CMX-1 判定表第 6 行 cmxSame601；--chroma 4:2:0 强制 YUV 让转换真发生） ──
$rI3 = Run 'i3_601_fwd' 'avif' @('--chroma','4:2:0','--color-out-matrix','bt470bg') $srcSm170
CK ($rI3.code -eq 0 -and ($null -ne $rI3.file)) "I3 601 same-family fwd (smpte170m src + bt470bg): exit=$($rI3.code), produced=$($null -ne $rI3.file)"
$hit = LogHit $rI3.log '用户 --color-out-matrix=bt470bg 已由 avif 出口承载' 1
CK ($hit.Count -ge 1) "I3 Reason Carried verbatim (601 interop fwd)"
$rI4 = Run 'i4_601_rev' 'avif' @('--chroma','4:2:0','--color-out-matrix','smpte170m') $srcBg601
CK ($rI4.code -eq 0 -and ($null -ne $rI4.file)) "I4 601 same-family rev (bt470bg src => derived smpte170m + smpte170m): exit=$($rI4.code), produced=$($null -ne $rI4.file)"
$hit = LogHit $rI4.log '用户 --color-out-matrix=smpte170m 已由 avif 出口承载' 1
CK ($hit.Count -ge 1) "I4 Reason Carried verbatim (601 interop rev)"

# ── I5 601 像素对拍（601 族系数相同 ⇒ 像素等价；PSNR=inf ⇒ 99。不裸哈希：avif 容器元数据
#   可能带非确定性位——E 组 jpg iccgen 时间戳同族教训，判像素不判字节） ──
if ($rI3.file -and $rI4.file) {
    $psnrI = PsnrTwo $rI3.file $rI4.file
    CK ($psnrI -ge 50.0) "I5 601 family pixel parity (bt470bg vs smpte170m arm): PSNR=$psnrI dB >= 50 (99 = inf, byte-equal pixels)"
} else { CK $false "I5 pixel parity skipped (missing product)" }

# ── I6 无标注源同值 Carried（闸不误伤；CMX-1 判定表第 4 行的端到端可达形态） ──
#   src_8bit.png 无任何色彩 chunk ⇒ NormalizePrimaries 归一 sRGB ⇒ 推导 bt709 ⇒ 显式 bt709 同值
#   Carried。⚠ 「Reason 点名『源 primaries 未标注…』」的端到端不可达（NormalizePrimaries coalesce
#   + ProPhoto 走 S1 推荐落点，见文件头 I 组注记）；推导 null 真执行由 CMX-1 selfcheck ⑤ 单元级覆盖。
$rI6 = Run 'i6_unlabeled_same' 'avif' @('--color-out-matrix','bt709')
CK ($rI6.code -eq 0 -and ($null -ne $rI6.file)) "I6 unlabeled-source same-value: exit=$($rI6.code), produced=$($null -ne $rI6.file)"
$hit = LogHit $rI6.log '用户 --color-out-matrix=bt709 已由 avif 出口承载' 1
CK ($hit.Count -ge 1) "I6 Reason Carried verbatim (gate passes same-value; no false rejection)"

# ── I7 auto 早退回归（CM-REQ-08 绝对基线；CMX-1 校验闸在 Carried 入口、auto 早退在更前面） ──
CK ($shaAuto -ne '' -and $shaAuto.Substring(0,16) -eq '9B6B2B1587840E05') "I7 webp auto sha16 == 9B6B2B1587840E05 (absolute baseline; CMX gate zero-impact on auto; sha16 $(if($shaAuto){$shaAuto.Substring(0,16)}else{'-'}))"
$rI7 = Run 'i7_auto_bt2020' 'avif' @() $srcBt2020
CK ($rI7.code -eq 0 -and ($null -ne $rI7.file) -and $rI7.log -notmatch '\[color-out-matrix\] ') "I7 bt2020-src auto: exit=$($rI7.code), produced=$($null -ne $rI7.file), ZERO [color-out-matrix] lines (early-return precedes the gate)"


Write-Output ""
Write-Output "===== -SelfCheck negative-control arm (must turn RED) ====="
# 负控 = 反向判读（预期红）：断言 png x bt709「exit=0 且有产物」（与 C1 已证真实行为相反）。
#   若该判读没红 = 判读链坏了 = 真红。形态对齐 verify-ui-param-matrix 的 -SelfCheck：
#   负控转红是预期结果，正向臂读数不受影响，整体 exit 0（runner 第 18 针契约：整轮多跑负控）。
$ncOk = $true
if ($SelfCheck) {
    Write-Output "===== selfcheck: reversed judgement on png x bt709 must turn RED ====="
    $ncRed = 0
    $rN = Run 'nc_png_bt709' 'png' @('--color-out-matrix','bt709')
    # 反向判读 = 「exit==0 且有产物」（C1 已证真实行为相反）。反向判读被真实行为**推翻**（exit!=0 /
    #   零产物）⇒ 负控红 ⇒ 计牙；反向判读通过（exit==0）⇒ 判读链坏 ⇒ ncOk=false 整体红。
    if ($rN.code -ne 0) { $ncRed++ } else { Write-Output "    (negctl) reversed check 'exit==0' PASSED => judgement chain is broken!" }
    if ($null -eq $rN.file) { $ncRed++ } else { Write-Output "    (negctl) reversed check 'product exists' PASSED => judgement chain is broken!" }
    CK ($ncRed -ge 1) "selfcheck negative-control turned RED as expected ($ncRed reversed judgement(s) failed)"
    $ncOk = ($ncRed -ge 1)

    # ── CMX 批第二负控臂：跨族拒绝反向判读（I1 的镜像）──
    #   反向判读 = 「exit==0 且有产物」；真实行为 = 规划层 Rejected（I1 已证 exit 91 零产物）
    #   ⇒ 反向判读必须被推翻（负控红）。若这条反向判读通过 ⇒ 跨族闸没牙（静默放行）⇒ 整体红。
    Write-Output "===== selfcheck: cross-family reversed judgement (BT.2020 src x bt709) must turn RED ====="
    $ncRed2 = 0
    $rN2 = Run 'nc_xdown' 'avif' @('--color-out-matrix','bt709') $srcBt2020
    if ($rN2.code -ne 0) { $ncRed2++ } else { Write-Output "    (negctl) reversed check 'exit==0' PASSED => cross-family gate is broken!" }
    if ($null -eq $rN2.file) { $ncRed2++ } else { Write-Output "    (negctl) reversed check 'product exists' PASSED => cross-family gate is broken!" }
    CK ($ncRed2 -ge 1) "selfcheck cross-family negative-control turned RED as expected ($ncRed2 reversed judgement(s) failed)"
    $ncOk = $ncOk -and ($ncRed2 -ge 1)
}

# ── 清理 + 汇总（house 契约：末行 PASS=n FAIL=m + fail => exit 1） ─────────────────
if (-not $KeepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
else { Write-Output "[keepwork] product area kept: $work" }

Write-Output ""

$allOk = ($fail -eq 0) -and $ncOk
Write-Output "OUTMATRIX RESULT: pass=$pass fail=$fail"
if (-not $allOk) { exit 1 }
exit 0