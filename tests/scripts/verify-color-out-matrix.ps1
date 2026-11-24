# verify-color-out-matrix.ps1 —— 显式输出矩阵模式（color-matrix-options 批，C-GATE@1）的端到端判据面。
#
# 真值书: .codeartsdoer/plans/color-matrix-options/plan.md §2.2 C-GATE@1 + §3.1 T7(:394-407)。
# 断言事实依据: T1 扫描报告 tests/output/_tmp/color-matrix-scan-20261110/report.md §7 校准清单 C1-C13
#   + T6 实测取证 tests/output/_tmp/color-matrix-t6-probe/（8 案 + delivery_evidence.md 移交注意事项 9 条）。
#
# 判据面（10 组：A–J；正向臂 + 负控）:
#   A. avif nclx 读回（引擎通路 = RawColorPipeline.BuildEncodeArgs，pixfmt-switch 兑现）:
#      显式 bt2020nc ⇒ 引擎真转 YUV（yuv420p）+ ffprobe 读回 color_space=bt2020nc（AV1/H.273 枚举 9）;
#      auto/rgb 档对照 ⇒ gbrp 直喂 ⇒ color_space=gbr（nclx matrix=0，T6 A3 案语义）。
#      ⚠ 任务书原文「matrix_coefficients=10」是笔误：H.273 bt2020nc=9、bt2020cl=10，T1 §4 表为 9 —— 判据按 9/语义词锚定。
#      ⚠ 引擎通路需要「真转色彩」场景（Display P3 目标 ⇒ H1 Map）：直通（None）时走 legacy ffmpeg 命令，
#        那是孤立标签路径（T6 移交注意事项 3），本门禁另锁其孤立点名（A7）。
#      A9/A10（2026-11-13 用户裁决「RGB 进 ⇒ RGB 无损出」）：RGB 源 + 无损 ⇒ gbrp 族且 PSNR=inf；
#        显式 rgb + 4:4:4 也不得写 yuv444p（负控：yuv444p 再标 identity 实测 11.96 dB 静默错色）。
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
#   J. 显式矩阵在**非承载出口**的「正常跑通」（2026-11-11 修复批 A1；维护者裁决：不要 fail-closed）:
#      J1–J4 GIF→AVIF 两步法（avifenc，分支先于引擎插入点）：601 族（smpte170m/bt470bg）与 rgb 走
#        `--cicp` **真兑现**（nclx 读回逐一断言；601 族与默认 1/13/6 逐像素 PSNR=inf、rgb 走 gbrp
#        identity——裸跑探针 tests/output/_tmp/beta12-fix-color/avifenc-cicp/）；bt709 等**不能安全
#        携带**（avifenc RGB→YUV 恒 libyuv BT.601，声明别的矩阵=解码端错系数反变换，实测 43.76→42.26 dB）
#        ⇒ exit 0 + 产物 + 显式播报「该出口无矩阵承载…未生效」。
#      J5 auto 对照 = 零 `[color-out-matrix] ` 行（负控：播报只在显式值在位时出现）。
#      J6–J8 bmp/ppm（无色彩通路 ⇒ ColorIntentFactory 返回 null ⇒ 不进规划层）：显式 bt709 ⇒
#        exit 0 + 产物非空 + 「无矩阵承载/未生效」播报；auto 对照零播报。
#      J9 ppm × rgb（Redundant 格）⇒ 契约模板 2 **恰好一行**（出口层与收尾兜底不重复播报）。
#      ⚠ 未削弱既有五格 fail-closed 判据（C1 png / D5 webp / E3 jpg / A8 avif-ycgco / B4 jxl 逐字保留）。
#   K. AVIF 16-bit Sample Transform（2026-11-13）: K0 真 16-bit 源；K1 avifenc --depth 12,4
#      双 item（gbrp12le + yuv444p）；K2 产品回读 PNG 像素 md5 bit-exact（编码 + 识别/解码双向）。
#   L. AVIF ICC：容器 nclx 描述编码采样，ICC v4.4 的 cicp 描述 RGB；各臂独立校验结构、Profile ID 和四轴。
#      原位变异分别证明 tag 表、checksum、ICC cicp 及 nclx 的判据可独立咬住错误。
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

# ── 2026-11-24 定向纯函数预检：P1/G1 的产品契约必须与真实出口同口径 ──
# 运行时条数分别锁 33/15，防探针整段未执行却被 fail=0 的假绿吞掉。
$prFocused = "$repoRoot/tests/projects/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $prFocused)) { $prFocused = "$repoRoot/tests/projects/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
CK (Test-Path $prFocused) "focused ServiceProbe host present"
if (Test-Path $prFocused) {
    $pAvifCaps = Start-Process -FilePath $prFocused -ArgumentList 'avifcaps' -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$work/probe-avifcaps.out" -RedirectStandardError "$work/probe-avifcaps.err"
    $avifCapsText = Read-TextShared "$work/probe-avifcaps.out"
    CK ($pAvifCaps.ExitCode -eq 0 -and $avifCapsText -match 'PROBE RESULT: pass=33 fail=0') "ServiceProbe avifcaps = 33/0"

    $pOutMatrixEnc = Start-Process -FilePath $prFocused -ArgumentList 'outmatrixenc' -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$work/probe-outmatrixenc.out" -RedirectStandardError "$work/probe-outmatrixenc.err"
    $outMatrixEncText = Read-TextShared "$work/probe-outmatrixenc.out"
    CK ($pOutMatrixEnc.ExitCode -eq 0 -and $outMatrixEncText -match 'PROBE RESULT: pass=15 fail=0') "ServiceProbe outmatrixenc = 15/0"
}

# Find-Prod: 产物查找（-Include 与无通配路径的组合在部分宿主下不命中 ⇒ 用 Extension 过滤，
#   探测轮实测：probe3 probe4 两次 -Include 假阴性、本形态命中）。
function Find-Prod([string]$dir) {
    $f = Get-ChildItem -Recurse -LiteralPath $dir -File -ErrorAction SilentlyContinue |
         Where-Object { $_.Extension -match '^\.(avif|png|webp|jpg|jpeg|jxl|bmp|ppm|gif)$' } | Select-Object -First 1
    if ($f) { $f.FullName } else { $null }
}
# Run: headless 一臂。⚠ FfmpegGui.exe 是 GUI 子系统（WinExe）⇒ pwsh `&` 调用不等待且
#   $LASTEXITCODE 不设置（探测轮实测 rc 文件空、[int]'' 假读 0）⇒ 用 Start-Process -Wait -PassThru
#   （verify-decision-delivery RunCli 在册形态），退出码从进程对象读（-Wait 在位 ⇒ 双宿主均有值）。
#   ArgumentList 手动给含空格元素加内嵌引号（decision-delivery 同款，探测轮已验证）。
function Run([string]$tag, [string]$fmt, [string[]]$extra, [string]$srcFile = '', [switch]$JsonLines) {
    $o = Join-Path $work $tag
    if (Test-Path $o) { Remove-Item -Recurse -Force $o -ErrorAction SilentlyContinue }
    # CMX 批：I 组多源臂（avifenc --cicp 构造的 CICP 标注源）⇒ 可选源覆盖（默认夹具 src_8bit.png）。
    $useSrc = $src
    if ($srcFile -ne '') { $useSrc = $srcFile }
    $a = @('--headless', '-i', $useSrc, '--format', $fmt, '--output', $o, '--log-level', 'Debug') + $extra
    $jsonFile = Join-Path $work "$tag.jsonl"
    # Every arm has an independent machine-readable task sink; stdout is not a terminal oracle.
    $JsonLines = $true
    $a += @('--log-format', 'JsonLines', '--log-file', $jsonFile)
    $aQ = @(); foreach ($x in $a) { if ($x -match '\s') { $aQ += ('"' + $x + '"') } else { $aQ += $x } }
    $child = Start-Process -FilePath $exe -ArgumentList $aQ -Wait -NoNewWindow -PassThru `
             -RedirectStandardOutput (Join-Path $work "$tag.o") -RedirectStandardError (Join-Path $work "$tag.e")
    $log = (Read-TextShared (Join-Path $work "$tag.o")) + "`n" + (Read-TextShared (Join-Path $work "$tag.e"))
    $events = @(); $jsonOk = $false
    if ($JsonLines) {
        # 只读 JSONL sink：启动/探测诊断可独立写 stdout/stderr，不把它们误当观察者事件。
        $lines = @((Read-TextShared $jsonFile) -split "`r?`n" | Where-Object { $_.Trim() })
        $jsonOk = $lines.Count -gt 0
        foreach ($line in $lines) {
            try {
                $event = ConvertFrom-Json -InputObject $line -ErrorAction Stop
                if ($null -eq $event -or -not ($event.c -is [string]) -or -not ($event.m -is [string])) { $jsonOk = $false }
                $events += $event
            } catch { $jsonOk = $false }
        }
        $log = ($events | ForEach-Object { $_.m }) -join "`n"
    }
    $prod = Find-Prod $o
    $terminal = @($events | Where-Object { $_.c -ceq 'item_completed' })
    $images = @(Get-ChildItem -LiteralPath $o -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -match '^\.(avif|png|webp|jpg|jpeg|jxl|bmp|ppm|gif)$' })
    $oneTerminal = $jsonOk -and $terminal.Count -eq 1 -and
        ($terminal[0].code -is [int] -or $terminal[0].code -is [long]) -and $terminal[0].p -eq 1 -and $terminal[0].tot -eq 1
    $consistent = ($child.ExitCode -eq 0 -and $oneTerminal -and $terminal[0].code -eq 0 -and
        $terminal[0].l -ceq 'info' -and $images.Count -eq 1 -and $images[0].Length -gt 0) -or
        ($child.ExitCode -ne 0 -and $oneTerminal -and $terminal[0].code -ne 0 -and
        $terminal[0].l -ceq 'error' -and $images.Count -eq 0)
    CK $consistent "$tag process/task terminal/new-artifact contract (process=$($child.ExitCode); item=$($terminal.code); terminals=$($terminal.Count); images=$($images.Count))"
    return @{ code = $child.ExitCode; log = $log; file = $prod; dir = $o; events = $events; jsonOk = $jsonOk }
}
# ffprobe 读回 color_space/color_range/pix_fmt（decision-delivery Cicp 同款，只读 stdout）。
function ProbeCs([string]$f) {
    $o = Join-Path $work '_cs.txt'
    Start-Process -FilePath $fp -ArgumentList @('-v','error','-select_streams','v:0','-show_entries',
        'stream=color_space,color_range,pix_fmt','-of','csv=p=0', $f) -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o | Out-Null
    return ((Read-TextShared $o).Trim())
}
# K 组（AVIF 16-bit Sample Transform）：两条视频流的 pix_fmt 清单（base / extension 各一行）。
function ProbeVStreams([string]$f) {
    $o = Join-Path $work '_vstreams.txt'
    Start-Process -FilePath $fp -ArgumentList @('-v','error','-select_streams','v','-show_entries',
        'stream=index,pix_fmt','-of','csv=p=0', $f) -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o | Out-Null
    return @((Read-TextShared $o) -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
}
# 16-bit 像素 md5（统一 rgb48le）；K 组解码回读的 bit-exact 判据。
function Md5Rgb48([string]$f) {
    $o = Join-Path $work '_md5.txt'
    $p = Start-Process -FilePath $ff -ArgumentList @('-hide_banner','-loglevel','error','-i',"`"$f`"",
        '-map','0:v:0','-frames:v','1','-pix_fmt','rgb48le','-f','md5','-') -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o
    $hash = (Read-TextShared $o).Trim()
    if ($p.ExitCode -eq 0 -and $hash -match '^MD5=[0-9a-f]{32}$') { return $hash }
    return ''
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
function OneMatrixResult([string]$log, [string]$expected) {
    $lines = @($log -split "`r?`n" | Where-Object { $_ -match '\[color-out-matrix\] ' })
    return ($lines.Count -eq 1 -and $lines[0].Trim() -match $expected)
}
function Rejected91($r) {
    $done = @($r.events | Where-Object { $_.c -ceq 'item_completed' })
    $files = @(Get-ChildItem -LiteralPath $r.dir -Recurse -File -ErrorAction SilentlyContinue)
    return ($r.jsonOk -and $r.code -eq 1 -and $done.Count -eq 1 -and
        ($done[0].code -is [int] -or $done[0].code -is [long]) -and $done[0].code -eq 91 -and
        $done[0].l -ceq 'error' -and $done[0].p -eq 1 -and $done[0].tot -eq 1 -and $files.Count -eq 0)
}

# 独立 ISO-BMFF 容器读回：只遍历 avifenc 的 meta 子树，不在 mdat/编码位流里搜索 ASCII。
function Be([byte[]]$b, [int]$offset, [int]$width) {
    [long]$value = 0
    for ($j = 0; $j -lt $width; $j++) { $value = ($value * 256) + $b[$offset + $j] }
    return $value
}
function Boxes([byte[]]$b, [int]$start, [int]$end) {
    $items = @()
    for ($at = $start; $at -lt $end; $at += [int]$size) {
        if ($end - $at -lt 8) { return }
        $size = Be $b $at 4
        if ($size -lt 8 -or $size -gt ($end - $at)) { return }
        $items += @{ type = [Text.Encoding]::ASCII.GetString($b, $at + 4, 4); start = ($at + 8); end = ($at + [int]$size) }
    }
    return $items
}
function BoxOf([byte[]]$b, [int]$start, [int]$end, [string]$type) {
    $found = @(Boxes $b $start $end | Where-Object { $_.type -ceq $type })
    if ($found.Count -eq 1) { return $found[0] }
}
# ICC.1 Profile ID: hash the entire declared profile with flags, rendering intent and ID zeroed.
function IccIdDigest([byte[]]$profile) {
    $digestInput = [byte[]]$profile.Clone()
    [Array]::Clear($digestInput, 44, 4)
    [Array]::Clear($digestInput, 64, 4)
    [Array]::Clear($digestInput, 84, 16)
    $md5 = [Security.Cryptography.MD5]::Create()
    try { return ,($md5.ComputeHash($digestInput)) } finally { $md5.Dispose() }
}
function IccS15([byte[]]$b, [int]$offset) {
    $u = [long](Be $b $offset 4)
    if ($u -ge 2147483648L) { $u -= 4294967296L }
    return ($u / 65536.0)
}
function IccCurveAt([byte[]]$b, $tag, [double]$x) {
    if ($tag.Type -ceq 'curv') {
        $count = Be $b ($tag.Off + 8) 4
        if ($count -eq 0) { return $x }
        if ($count -eq 1) { return [Math]::Pow($x, (Be $b ($tag.Off + 12) 2) / 256.0) }
        $position = $x * ($count - 1); $lo = [int][Math]::Floor($position)
        $hi = [Math]::Min($lo + 1, $count - 1); $fraction = $position - $lo
        $y0 = (Be $b ($tag.Off + 12 + 2 * $lo) 2) / 65535.0
        $y1 = (Be $b ($tag.Off + 12 + 2 * $hi) 2) / 65535.0
        return $y0 * (1 - $fraction) + $y1 * $fraction
    }
    $kind = Be $b ($tag.Off + 8) 2
    $g = IccS15 $b ($tag.Off + 12)
    if ($kind -eq 0) { return [Math]::Pow($x, $g) }
    $a = IccS15 $b ($tag.Off + 16); $bb = IccS15 $b ($tag.Off + 20)
    if ($kind -eq 1) {
        $cut = -$bb / $a
        if ($x -lt $cut) { return 0.0 }
        return [Math]::Pow($a * $x + $bb, $g)
    }
    $c = IccS15 $b ($tag.Off + 24)
    if ($kind -eq 2) {
        $cut = -$bb / $a
        if ($x -lt $cut) { return $c }
        return [Math]::Pow($a * $x + $bb, $g) + $c
    }
    $cut = IccS15 $b ($tag.Off + 28)
    if ($kind -eq 3) {
        if ($x -lt $cut) { return $c * $x }
        return [Math]::Pow($a * $x + $bb, $g)
    }
    $e = IccS15 $b ($tag.Off + 32); $f = IccS15 $b ($tag.Off + 36)
    if ($x -lt $cut) { return $c * $x + $f }
    return [Math]::Pow($a * $x + $bb, $g) + $e
}
function WriteIccS15([byte[]]$b, [int]$offset, [double]$value) {
    $raw = [long][Math]::Round($value * 65536.0, [MidpointRounding]::AwayFromZero)
    if ($raw -lt 0) { $raw += 4294967296L }
    for ($i = 3; $i -ge 0; $i--) {
        $b[$offset + $i] = [byte]($raw -band 255)
        $raw = [long][Math]::Floor($raw / 256.0)
    }
}
function WriteIccU16([byte[]]$b, [int]$offset, [int]$value) {
    $b[$offset] = [byte](($value -shr 8) -band 255)
    $b[$offset + 1] = [byte]($value -band 255)
}
function MutateIccCurve([byte[]]$b, $tag) {
    if ($tag.Type -ceq 'curv') {
        $count = Be $b ($tag.Off + 8) 4
        if ($count -eq 0) { return $false }
        if ($count -eq 1) {
            $offset = $tag.Off + 12; $old = Be $b $offset 2
            $new = if ($old -le 65407) { $old + 128 } else { $old - 128 }
            WriteIccU16 $b $offset $new
            return $true
        }
        $index = [int][Math]::Round(($count - 1) / 2.0)
        $offset = $tag.Off + 12 + 2 * $index; $old = Be $b $offset 2
        $new = if ($old -le 45535) { $old + 20000 } else { $old - 20000 }
        WriteIccU16 $b $offset $new
        return $true
    }
    if ($tag.Type -ceq 'para') {
        $gamma = IccS15 $b ($tag.Off + 12)
        WriteIccS15 $b ($tag.Off + 12) ($gamma + 0.5)
        return $true
    }
    return $false
}
function ProfileMatchesP3Target($profile) {
    if ($null -eq $profile -or -not $profile.idOk -or $profile.cicp -cne '12/13/0/1') { return $false }
    $expected = @{
        wtpt = @(0.9642,1.0,0.8249)
        rXYZ = @(0.51512,0.24120,-0.00105)
        gXYZ = @(0.29198,0.69225,0.04189)
        bXYZ = @(0.15710,0.06657,0.78407)
    }
    foreach ($name in $expected.Keys) {
        for ($i = 0; $i -lt 3; $i++) {
            $actual = [double]$profile.xyz[$name][$i]
            if ([double]::IsNaN($actual) -or [double]::IsInfinity($actual) -or
                [Math]::Abs($actual - $expected[$name][$i]) -gt 0.003) { return $false }
        }
    }
    foreach ($name in @('rTRC','gTRC','bTRC')) {
        for ($i = 0; $i -lt $profile.curveSamples.Count; $i++) {
            $actual = [double]$profile.trc[$name][$i]
            if ([double]::IsNaN($actual) -or [double]::IsInfinity($actual) -or
                [Math]::Abs($actual - $profile.curveExpected[$i]) -gt 0.001) { return $false }
        }
    }
    return $true
}
function ReadRgbIcc44([byte[]]$b) {
    $size = $b.Length
    $ascii = [Text.Encoding]::ASCII
    if ($size -lt 132 -or $size % 4 -ne 0 -or (Be $b 0 4) -ne $size -or (Be $b 8 4) -ne 0x04400000 -or
        $ascii.GetString($b, 36, 4) -cne 'acsp' -or $ascii.GetString($b, 12, 12) -cne 'mntrRGB XYZ ' -or
        (Be $b 64 4) -gt 3 -or @($b[100..127] | Where-Object { $_ -ne 0 }).Count -ne 0) { return }
    $count = Be $b 128 4; $tableEnd = 132 + 12 * $count
    if ($count -lt 1 -or $tableEnd -gt $size) { return }
    $tags = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    for ($entry = 132; $entry -lt $tableEnd; $entry += 12) {
        $sig = $ascii.GetString($b, $entry, 4)
        $off = Be $b ($entry + 4) 4; $len = Be $b ($entry + 8) 4
        if ($tags.ContainsKey($sig) -or $off -lt $tableEnd -or $off % 4 -ne 0 -or $len -lt 8 -or
            $off + $len -gt $size -or (Be $b ($off + 4) 4) -ne 0) { return }
        $tags.Add($sig, [pscustomobject]@{ Off = $off; Len = $len; Entry = $entry; Type = $ascii.GetString($b, $off, 4) })
    }
    # Shared tag data is allowed (e.g. the three TRCs), but partially overlapping intervals are not.
    $last = $null
    foreach ($tag in @($tags.Values | Sort-Object Off, Len)) {
        if ($last -and $tag.Off -lt $last.Off + $last.Len -and
            ($tag.Off -ne $last.Off -or $tag.Len -ne $last.Len)) { return }
        $last = $tag
    }
    foreach ($name in @('wtpt','rXYZ','gXYZ','bXYZ')) {
        if (-not $tags.ContainsKey($name) -or $tags[$name].Type -cne 'XYZ ' -or $tags[$name].Len -ne 20) { return }
    }
    foreach ($name in @('rTRC','gTRC','bTRC')) {
        if (-not $tags.ContainsKey($name)) { return }
        $tag = $tags[$name]
        if ($tag.Len -lt 12) { return }
        if ($tag.Type -ceq 'curv') { $need = 12 + 2 * (Be $b ($tag.Off + 8) 4) }
        elseif ($tag.Type -ceq 'para') {
            $curve = Be $b ($tag.Off + 8) 2
            if ($curve -gt 4 -or (Be $b ($tag.Off + 10) 2) -ne 0) { return }
            $need = @(16,24,28,32,40)[$curve]
        } else { return }
        if ($tag.Len -ne $need) { return }
    }
    foreach ($name in @('desc','cprt')) {
        if (-not $tags.ContainsKey($name)) { return }
        $tag = $tags[$name]
        if ($tag.Type -cne 'mluc' -or $tag.Len -lt 28 -or (Be $b ($tag.Off + 12) 4) -ne 12) { return }
        $records = Be $b ($tag.Off + 8) 4; $dataStart = 16 + 12 * $records
        if ($records -lt 1 -or $dataStart -gt $tag.Len) { return }
        for ($i = 16; $i -lt $dataStart; $i += 12) {
            $strLen = Be $b ($tag.Off + $i + 4) 4; $strOff = Be $b ($tag.Off + $i + 8) 4
            if ($strLen % 2 -ne 0 -or $strOff -lt $dataStart -or $strOff + $strLen -gt $tag.Len) { return }
        }
    }
    $cicp = ''
    if ($tags.ContainsKey('cicp')) {
        $tag = $tags['cicp']
        if ($tag.Type -cne 'cicp' -or $tag.Len -ne 12) { return }
        $cicp = $b[($tag.Off + 8)..($tag.Off + 11)] -join '/'
    }
    $xyz = @{}
    foreach ($name in @('wtpt','rXYZ','gXYZ','bXYZ')) {
        $off = $tags[$name].Off + 8
        $xyz[$name] = @((IccS15 $b $off), (IccS15 $b ($off + 4)), (IccS15 $b ($off + 8)))
    }
    $curveSamples = @(0.012,0.035,0.07,0.18,0.35,0.5,0.75,0.95)
    $curveExpected = @()
    foreach ($x in $curveSamples) {
        $curveExpected += $(if ($x -le 0.04045) { $x / 12.92 } else { [Math]::Pow(($x + 0.055) / 1.055, 2.4) })
    }
    $trc = @{}
    foreach ($name in @('rTRC','gTRC','bTRC')) {
        $values = @()
        foreach ($x in $curveSamples) { $values += (IccCurveAt $b $tags[$name] $x) }
        $trc[$name] = $values
    }
    $id = [BitConverter]::ToString($b, 84, 16)
    return @{ id = $id; idOk = ($id -cne ('00-' * 15 + '00') -and $id -ceq [BitConverter]::ToString((IccIdDigest $b)))
        cicp = $cicp; cicpTag = $tags['cicp']; xyz = $xyz; trc = $trc
        curveSamples = $curveSamples; curveExpected = $curveExpected; tags = $tags }
}
function ReadJpegIcc([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    if ($b.Length -lt 4 -or (Be $b 0 2) -ne 0xffd8) { return }
    $chunks = @{}; $total = 0
    for ($i = 2; $i + 4 -le $b.Length;) {
        if ($b[$i++] -ne 255) { return }
        while ($i -lt $b.Length -and $b[$i] -eq 255) { $i++ }
        if ($i + 2 -ge $b.Length) { return }
        $marker = $b[$i++]; if ($marker -in 0xda,0xd9) { break }
        $n = Be $b $i 2
        if ($n -lt 2 -or $i + $n -gt $b.Length) { return }
        if ($marker -eq 0xe2 -and $n -gt 16 -and [Text.Encoding]::ASCII.GetString($b,$i+2,12) -ceq "ICC_PROFILE`0") {
            $part = $b[$i+14]; $count = $b[$i+15]
            if ($part -lt 1 -or $part -gt $count -or ($total -ne 0 -and $total -ne $count) -or $chunks.ContainsKey($part)) { return }
            $total = $count; $chunks[$part] = [byte[]]$b[($i+16)..($i+$n-1)]
        }
        $i += $n
    }
    if ($total -le 0 -or $chunks.Count -ne $total) { return }
    $m = [IO.MemoryStream]::new()
    try {
        for ($j=1; $j -le $total; $j++) { $part=$chunks[[byte]$j]; $m.Write($part,0,$part.Length) }
        return ,($m.ToArray())
    } finally { $m.Dispose() }
}

function PrimaryAvifProperties([byte[]]$b, [long]$itemId = -1) {
    $meta = BoxOf $b 0 $b.Length 'meta'
    if (-not $meta -or $meta.end - $meta.start -lt 4) { return }
    $pitm = BoxOf $b ($meta.start + 4) $meta.end 'pitm'
    $iprp = BoxOf $b ($meta.start + 4) $meta.end 'iprp'
    if (-not $pitm -or -not $iprp -or $pitm.end - $pitm.start -lt 6) { return }
    $pv = $b[$pitm.start]
    $idWidth = 2 + 2 * $pv
    if ($pv -gt 1 -or $pitm.end - $pitm.start -ne 4 + $idWidth) { return }
    $primary = Be $b ($pitm.start + 4) $idWidth
    if ($itemId -ge 0) { $primary = $itemId }
    $ipco = BoxOf $b $iprp.start $iprp.end 'ipco'
    $ipma = BoxOf $b $iprp.start $iprp.end 'ipma'
    if (-not $ipco -or -not $ipma -or $ipma.end - $ipma.start -lt 8) { return }
    $props = @(Boxes $b $ipco.start $ipco.end)
    $version = $b[$ipma.start]; $flags = Be $b ($ipma.start + 1) 3
    if ($version -gt 1 -or $flags -gt 1) { return }
    $idWidth = 2 + 2 * $version; $assocWidth = 1 + $flags; $indexMask = (1 -shl (8 * $assocWidth - 1)) - 1
    $entries = Be $b ($ipma.start + 4) 4; $q = $ipma.start + 8
    $found = $false; $selected = @()
    for ($i = 0; $i -lt $entries; $i++) {
        if ($ipma.end - $q -lt $idWidth + 1) { return }
        $id = Be $b $q $idWidth; $q += $idWidth
        $n = $b[$q]; $q++
        if ($ipma.end - $q -lt $n * $assocWidth) { return }
        if ($id -eq $primary) {
            if ($found) { return }
            $found = $true
            for ($j = 0; $j -lt $n; $j++) {
                $index = (Be $b ($q + $j * $assocWidth) $assocWidth) -band $indexMask
                if ($index -gt $props.Count) { return }
                if ($index -gt 0) { $selected += $props[$index - 1] }
            }
        }
        $q += $n * $assocWidth
    }
    if ($found -and $q -eq $ipma.end) { return $selected }
}
function ReadAvifColor([byte[]]$b) {
    $nclx = @(); $icc = @()
    foreach ($box in @(PrimaryAvifProperties $b | Where-Object { $_.type -ceq 'colr' })) {
        $p = $box.start
        if ($box.end - $p -lt 4) { continue }
        $kind = [Text.Encoding]::ASCII.GetString($b, $p, 4)
        if ($kind -ceq 'nclx') {
            $value = if ($box.end - $p -eq 11 -and ($b[$p + 10] -band 127) -eq 0) {
                '{0}/{1}/{2}/{3}' -f (Be $b ($p + 4) 2), (Be $b ($p + 6) 2), (Be $b ($p + 8) 2), ($b[$p + 10] -shr 7)
            } else { '' }
            $nclx += @{ value = $value; sampleOffset = $p + 8 }
        } elseif ($kind -cin @('prof','rICC')) {
            $bytes = [byte[]]::new($box.end - $p - 4)
            [Array]::Copy($b, $p + 4, $bytes, 0, $bytes.Length)
            $icc += @{ bytes = $bytes; profile = (ReadRgbIcc44 $bytes) }
        }
    }
    return @{ nclx = $nclx; icc = $icc }
}
function HasSampleTransform([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    $meta = BoxOf $b 0 $b.Length 'meta'
    if (-not $meta -or $meta.end - $meta.start -lt 4) { return $false }
    $iinf = BoxOf $b ($meta.start + 4) $meta.end 'iinf'
    $iref = BoxOf $b ($meta.start + 4) $meta.end 'iref'
    # avifenc 小图固定使用 16-bit item ID（iinf v0 / infe v2 / iref v0）。
    if (-not $iinf -or -not $iref -or $iinf.end - $iinf.start -lt 6 -or $iref.end - $iref.start -lt 4 -or
        $b[$iinf.start] -ne 0 -or $b[$iref.start] -ne 0) { return $false }
    $sato = @(); $av01 = @()
    foreach ($entry in @(Boxes $b ($iinf.start + 6) $iinf.end | Where-Object { $_.type -ceq 'infe' })) {
        if ($entry.end - $entry.start -lt 12 -or $b[$entry.start] -ne 2) { continue }
        $id = Be $b ($entry.start + 4) 2
        $type = [Text.Encoding]::ASCII.GetString($b, $entry.start + 8, 4)
        if ($type -ceq 'sato') { $sato += $id }
        if ($type -ceq 'av01') { $av01 += $id }
    }
    if ($sato.Count -ne 1 -or $av01.Count -ne 2 -or $av01[0] -eq $av01[1]) { return $false }
    $ref = BoxOf $b ($iref.start + 4) $iref.end 'dimg'
    if (-not $ref -or $ref.end - $ref.start -ne 8) { return $false }
    $a = Be $b ($ref.start + 4) 2; $z = Be $b ($ref.start + 6) 2
    return ((Be $b $ref.start 2) -eq $sato[0] -and (Be $b ($ref.start + 2) 2) -eq 2 -and
        $a -ne $z -and $a -in $av01 -and $z -in $av01)
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
CK ($hit.Count -ge 1 -and (OneMatrixResult $r.log '\[color-out-matrix\] avif: bt709 -> -colorspace bt709$')) "A2 exactly one template-1 result: '[color-out-matrix] avif: bt709 -> -colorspace bt709'"
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

# A9: RGB 源 + 无损 ⇒ 像素面必须保持 gbrp 族（2026-11-13 用户裁决）。
#   负控历史：默认 yuv* 档在编码前已做 RGB→YUV（含色度抽样），"编码器无损"救不回色度；
#   yuv444p 再被标 identity 会静默错色（同目录 app_legacy_444_rgb 实测 PSNR 11.96 dB）。
$rLls = Run 'a9_avif_lossless_rgb' 'avif' @('--lossless','true')
$csLls = if ($rLls.file) { ProbeCs $rLls.file } else { '' }
CK ($rLls.code -eq 0 -and ($null -ne $rLls.file)) "A9 avif RGB source + lossless: exit=$($rLls.code), produced=$($null -ne $rLls.file)"
CK ($csLls -eq 'gbrp,pc,gbr') "A9 readback = 8-bit planar RGB, full range and AV1 identity (full='$csLls')"
$ps9 = if ($rLls.file) { PsnrTwo $rLls.file $src } else { -1 }
CK ($ps9 -ge 99) "A9 RGB lossless is bit-exact vs source (PSNR=$ps9; inf folds to 99)"
$md59src = Md5Rgb48 $src
$md59out = if ($rLls.file) { Md5Rgb48 $rLls.file } else { '' }
CK ($md59src -ne '' -and $md59src -ceq $md59out) "A9 independent decoded full-frame RGB hash matches (source=$md59src; AVIF=$md59out)"

# A10: 显式 rgb + 4:4:4 + 无损 ⇒ 也不得写 yuv444p（legacy 直通同样钉 gbrp；负控 11.96 dB）
$r444 = Run 'a10_avif_rgb444' 'avif' @('--color-engine','legacy','--color-out-matrix','rgb','--chroma','4:4:4','--lossless','true')
$cs444 = if ($r444.file) { ProbeCs $r444.file } else { '' }
CK ($r444.code -eq 0 -and ($null -ne $r444.file)) "A10 avif explicit rgb + 4:4:4: exit=$($r444.code), produced=$($null -ne $r444.file)"
CK ($cs444 -match 'gbr') "A10 readback = gbr (identity carried by gbrp, not yuv444p; full='$cs444')"
$ps10 = if ($r444.file) { PsnrTwo $r444.file $src } else { -1 }
CK ($ps10 -ge 99) "A10 explicit rgb + 4:4:4 is bit-exact vs source (PSNR=$ps10; inf folds to 99)"
$hit = LogHit $r444.log '\[rgb\] avif: explicit rgb matrix -> gbrp' 1
CK ($hit.Count -ge 1) "A10 [rgb] advisory landed verbatim"

# A11: legacy 静态 AVIF + 显式 YUV 像素面（420/10bit）+ 无 --color-out-matrix。
#   旧路径默认用 swscale/libaom 的 BT.601 系数转换，但不写 -colorspace ⇒ nclx matrix 落
#   unspecified(2)；补 smpte170m 后必须读回显式 BT.601，且命令行真的带矩阵旗标（非事后改标签）。
$r11 = Run 'a11_avif_legacy_yuv601' 'avif' @('--color-engine','legacy','--bit-depth','10','--chroma','4:2:0','--color-range','pc')
$cs11 = if ($r11.file) { ProbeCs $r11.file } else { '' }
CK ($r11.code -eq 0 -and ($null -ne $r11.file)) "A11 legacy AVIF YUV420/10 auto produced (exit=$($r11.code))"
CK ($cs11 -ceq 'yuv420p10le,pc,smpte170m') "A11 legacy AVIF nclx matrix is explicit BT.601/SMPTE170M (full='$cs11')"
CK ((CmdLineOf $r11.log) -match ' -colorspace smpte170m ') "A11 legacy command carries -colorspace smpte170m (not a tag-only patch)"

# A12: 同一 legacy 路径的 auto 对照仍是 gbrp identity ⇒ 补矩阵只准落在真实 YUV 像素面，
#   不许把 RGB/gbrp 误标成 BT.601（A11 的负控）。
$r12 = Run 'a12_avif_legacy_auto_gbr' 'avif' @('--color-engine','legacy')
$cs12 = if ($r12.file) { ProbeCs $r12.file } else { '' }
CK ($r12.code -eq 0 -and ($null -ne $r12.file)) "A12 legacy AVIF auto produced (exit=$($r12.code))"
CK ($cs12 -ceq 'gbrp,pc,gbr') "A12 legacy AVIF auto stays gbrp identity (full='$cs12')"
CK ((CmdLineOf $r12.log) -notmatch ' -colorspace ') "A12 legacy auto gbrp arm has NO -colorspace flag"

# A6: auto 档零播报（C-REPORT@1「auto 不播报」+ CM-REQ-08 零噪声）
CK ($rAuto.log -notmatch '\[color-out-matrix\] ') "A6 auto arm has ZERO [color-out-matrix] lines (silence contract)"

# A7: legacy 直通也必须实际落地同族 bt709；没有显式 chroma/位深，不能只写孤立的 -colorspace。
$rL = Run 'a7_legacy' 'avif' @('--color-engine','legacy','--color-out-matrix','bt709')
$csL = if ($rL.file) { ProbeCs $rL.file } else { '' }
CK ($rL.code -eq 0 -and ($null -ne $rL.file)) "A7 legacy arm produced output (exit=$($rL.code); prod=$($null -ne $rL.file))"
CK ($csL -match '^yuv[^,]*,(?:pc|tv),bt709$') "A7 legacy AVIF readback is YUV with BT.709, not a bare matrix flag (full='$csL')"
CK (OneMatrixResult $rL.log '\[color-out-matrix\] avif: bt709 -> -colorspace bt709$') "A7 legacy has exactly one truthful BT.709 result"

# A8: avif x ycgco ⇒ Rejected（T1 C4 校准）+ 模板 3 逐字（规划层发）
$rY = Run 'a8_ycgco' 'avif' @('--color-out-matrix','ycgco')
$hit = LogHit $rY.log 'ycgco is not carried by avif \(swscale conversion for ycgco proven impossible on the input side; output side unverified => rejected conservatively\) => contract rejection, exit 91, no output' 1
CK ($hit.Count -ge 1) "A8 avif x ycgco template-3 verbatim (C4: rejected conservatively)"
CK ($rY.code -ne 0 -and ($null -eq $rY.file)) "A8 avif x ycgco: exit=$($rY.code) != 0, no output (plan-level Reject)"

Write-Output ""
Write-Output "===== B jxl XYB ====="

# B1: 显式 xyb（默认 backend=Cjxl）⇒ 确认播报逐字（C8：cjxl v0.12.0 无显式开关）
$rJ = Run 'b1_xyb' 'jxl' @('--color-out-matrix','xyb')
CK ($rJ.code -eq 0 -and ($null -ne $rJ.file)) "B1 jxl x xyb (cjxl backend): exit=$($rJ.code), produced=$($null -ne $rJ.file)"
CK (OneMatrixResult $rJ.log "\[color-out-matrix\] xyb confirmed: cjxl's VarDCT mode carries XYB") 'B1 exactly one cjxl xyb-confirmed result (implicit VarDCT carrier)'
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
$rJB = Run 'b4_bt709' 'jxl' @('--color-out-matrix','bt709') -JsonLines
$hit = LogHit $rJB.log 'bt709 is not carried by jxl \(JXL is an RGB-native container \(RgbNative\); no YUV carrier\) => contract rejection, exit 91, no output' 1
CK ($hit.Count -ge 1) "B4 jxl x bt709 template-3 verbatim (RgbNative rejection)"
CK (Rejected91 $rJB) 'B4 engine JXL JSONL: one failed task code 91; process 1; no output'
$rJLegacy = Run 'b4_legacy_cjxl_bt709' 'jxl' @('-e','cjxl','--color-engine','legacy','--color-out-matrix','bt709') -JsonLines
CK (Rejected91 $rJLegacy) 'B4 direct cjxl JSONL: one failed task code 91; process 1; no output'
CK (OneMatrixResult $rJLegacy.log '\[color-out-matrix\] bt709 is not carried by jxl \(cjxl backend is RGB-native; no YUV carrier\) => contract rejection, exit 91, no output$') 'B4 direct cjxl: exactly one backend-specific rejection result'

Write-Output ""
Write-Output "===== C png fail-closed (plan-level Rejected => exit 91) ====="

# C1: 显式 bt709 ⇒ 任务级退出码 91 + 模板 3 逐字 + 零产物
$rP = Run 'c1_png_bt709' 'png' @('--color-out-matrix','bt709') -JsonLines
CK (Rejected91 $rP) 'C1 JSONL: exactly one item_completed with numeric code 91; process 1 and zero output files'
CK (OneMatrixResult $rP.log '\[color-out-matrix\] bt709 is not carried by png .*=> contract rejection, exit 91, no output$') 'C1 exactly one rejection result; no false success'
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
CK ($hit.Count -ge 1 -and (OneMatrixResult $rWB.log '\[color-out-matrix\] bt470bg on webp: container is already bt470bg; recorded, no pixel effect$')) "D2 exactly one template-2 result ({2}=bt470bg)"
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
CK ($hit.Count -ge 1 -and (OneMatrixResult $rJB2.log '\[color-out-matrix\] bt470bg on jpg: container is already bt470bg; recorded, no pixel effect$')) "E1b exactly one template-2 result on jpg ({2}=bt470bg)"
# E2: 像素级一致（全帧 PSNR=inf 级）——同命令行同源 ⇒ 逐像素相同
if ($rJA2.file -and $rJB2.file) {
    $psnr = PsnrTwo $rJB2.file $rJA2.file
    $md5AutoJpg = Md5Rgb48 $rJA2.file
    $md5ExplicitJpg = Md5Rgb48 $rJB2.file
    CK ($psnr -ge 50.0 -and $md5AutoJpg -ne '' -and $md5AutoJpg -ceq $md5ExplicitJpg) "E2 jpg bt470bg vs auto is bit-exact after RGB decode (PSNR=$psnr; $md5AutoJpg vs $md5ExplicitJpg)"
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

# F3: 直接 cjpegli 的异常路径也必须兑现 JSONL 任务码 91，不可只在文案里提到 91。
$rCB = Run 'f3_cjpegli_bt709' 'jpg' @('-e','cjpegli','--color-engine','legacy','--color-out-matrix','bt709') -JsonLines
CK (OneMatrixResult $rCB.log '\[color-out-matrix\] bt709 is not carried by jpg \(cjpegli encodes from RGB directly; no YUV conversion point\) => contract rejection, exit 91, no output$') 'F3 direct cjpegli: exactly one backend-specific rejection result'
CK (Rejected91 $rCB) 'F3 cjpegli JSONL: one failed task code 91; process 1; no output'

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
if (-not (Test-Path $avifenc)) { Write-Output "  FAIL tool missing: $avifenc"; Write-Output "OUTMATRIX RESULT: pass=$pass fail=$($fail+1)"; Write-Output "[keepwork] failed product area kept: $work"; exit 1 }
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
Write-Output "===== J explicit matrix on non-carrier exits (2026-11-11 fix batch A1) ====="
# ── J 组口径（2026-11-11 修复批 A1）：维护者裁决「不要 fail-closed，要能正常跑通」 ──────────────
# 缺陷形状（审查 P1-1/P1-2）：显式 --color-out-matrix 在两类出口上**静默失效**（exit 0 + 产物 + 零播报）：
#   ① GIF→AVIF 两步法（avifenc；该分支先于引擎插入点 ⇒ 规划层根本不跑）；
#   ② bmp/ppm/dng/webm（无色彩通路 ⇒ ColorIntentFactory 返回 null ⇒ 不进规划层）。
# 修复口径：能安全携带的**真兑现**；不能的**显式播报**（exit 0、产物照出；绝不改成 exit 91/零产物）。
#  GIFI avifenc --cicp 评估（裸跑探针 tests/output/_tmp/beta12-fix-color/avifenc-cicp/，非整轮验收）：
#    · 601 族（smpte170m=6 / bt470bg=5）与 avifenc 默认 nclx 1/13/6 **逐像素 PSNR=inf** ⇒ 安全携带；
#    · rgb（identity=0）+ `-y 444` ⇒ 产物 gbrp（identity 直存），帧 PSNR 38.60→43.48 dB ⇒ 真兑现；
#    · bt709/bt2020nc 等：RGB→YUV 恒 libyuv BT.601 系数，声明别的矩阵 = 解码端按错系数反变换
#      （实测 bt709 43.76→42.26 dB）⇒ 不携带、显式播报（产物照出）。
#  bmp/ppm：容器无矩阵标注位、规划层不参与 ⇒ 一律显式播报「该出口无矩阵承载…未生效」。
#  ⚠ 负控（播报只在显式值在位时出现）：J5/J8 auto 臂必须**零** `[color-out-matrix] ` 行。

# GIF 夹具（ensure-fixtures.ps1 生成；缺则本组臂全红并点名，不静默给绿）
$gifSrc = "$repoRoot/tests/output/sources/anim_gif.gif"
CK (Test-Path $gifSrc) "J0 GIF fixture present ($gifSrc)"

# J1: GIF→AVIF + 显式 smpte170m（601 族同值）⇒ avifenc --cicp 1/13/6 兑现 + nclx 读回
$rJ1 = Run 'j1_gif2avif_smpte170m' 'avif' @('--color-out-matrix','smpte170m') $gifSrc
$csJ1 = if ($rJ1.file) { ProbeCs $rJ1.file } else { '' }
CK ($rJ1.code -eq 0 -and ($null -ne $rJ1.file) -and ((Get-Item $rJ1.file).Length -gt 0)) "J1 GIF->AVIF two-step x smpte170m: exit=$($rJ1.code), produced non-empty"
$hit = LogHit $rJ1.log '\[color-out-matrix\] avif\(two-step\): smpte170m -> avifenc --cicp 1/13/6' 1
CK ($hit.Count -ge 1) "J1 carrier landing line verbatim (avifenc --cicp 1/13/6; 601 family = encoder's own BT.601 coefficients)"
CK ($csJ1 -match 'smpte170m') "J1 nclx readback color_space=smpte170m (explicit value really landed; full='$csJ1')"

# J2: bt470bg（601 族另一成员）⇒ 标签按显式值落地，像素与默认逐像素相同（探针 PSNR=inf）
$rJ2 = Run 'j2_gif2avif_bt470bg' 'avif' @('--color-out-matrix','bt470bg') $gifSrc
$csJ2 = if ($rJ2.file) { ProbeCs $rJ2.file } else { '' }
CK ($rJ2.code -eq 0 -and ($null -ne $rJ2.file)) "J2 GIF->AVIF two-step x bt470bg: exit=$($rJ2.code), produced=$($null -ne $rJ2.file)"
CK ($csJ2 -match 'bt470bg') "J2 nclx readback color_space=bt470bg (601 family label landed; full='$csJ2')"

# J3: rgb（identity）⇒ --cicp 1/13/0 + `-y 444` ⇒ 产物 gbrp（identity 直存、无 YUV 反变换）
$rJ3 = Run 'j3_gif2avif_rgb' 'avif' @('--color-out-matrix','rgb') $gifSrc
$csJ3 = if ($rJ3.file) { ProbeCs $rJ3.file } else { '' }
CK ($rJ3.code -eq 0 -and ($null -ne $rJ3.file)) "J3 GIF->AVIF two-step x rgb: exit=$($rJ3.code), produced=$($null -ne $rJ3.file)"
CK ($csJ3 -match 'gbr') "J3 identity matrix landed as gbrp (nclx matrix_coefficients=0; full='$csJ3')"

# J4: bundled avifenc uses the chosen matrix for native YUV, not just the nclx label.
$rJ4 = Run 'j4_gif2avif_bt709' 'avif' @('--color-out-matrix','bt709') $gifSrc
$csJ4 = if ($rJ4.file) { ProbeCs $rJ4.file } else { '' }
CK ($rJ4.code -eq 0 -and ($null -ne $rJ4.file)) "J4 GIF->AVIF two-step x bt709: exit=$($rJ4.code), produced=$($null -ne $rJ4.file)"
CK (OneMatrixResult $rJ4.log '\[color-out-matrix\] avif\(two-step\): bt709 -> avifenc --cicp 1/13/1$') 'J4 exactly one BT.709 result'
CK ($csJ4 -match '^yuv[^,]*,[^,]*,bt709$') "J4 YUV, nclx BT.709 (full='$csJ4')"
function NativeFrameHash([string]$file) {
    if (-not $file) { return '' }
    $o = & $ff -hide_banner -v error -i $file -map 0:v:0 -frames:v 1 -f md5 -
    if ($LASTEXITCODE -eq 0 -and $o -match '^MD5=[0-9a-f]{32}$') { return [string]$o }
    return ''
}
$j601 = NativeFrameHash $rJ1.file; $j709 = NativeFrameHash $rJ4.file
CK ($j601 -and $j709 -and $j601 -cne $j709) 'J4 primary native YUV codes differ from the BT.601 control (not a tag-only change)'
# v:0 sees only the primary image. Inspect the timed colour track with all video streams visible.
function Read-SequenceYuv([string]$f, [string]$matrix) {
    if (-not $f) { return $null }
    $o = Join-Path $work '_sequence.json'
    $probe = Start-Process -FilePath $fp -ArgumentList @('-v','error','-select_streams','v','-show_entries',
        'stream=index,pix_fmt,color_space,nb_frames','-of','json',"`"$f`"") -Wait -PassThru -NoNewWindow -RedirectStandardOutput $o
    if ($probe.ExitCode -ne 0) { return $null }
    $stream = @(((Read-TextShared $o) | ConvertFrom-Json).streams | Where-Object {
        [int]$_.nb_frames -gt 1 -and $_.pix_fmt -like 'yuv*'
    })
    if ($stream.Count -ne 1 -or $stream[0].color_space -cne $matrix) { return $null }
    $hashFile = Join-Path $work '_sequence.md5'
    $decode = Start-Process -FilePath $ff -ArgumentList @('-v','error','-i',"`"$f`"",'-map',"0:$($stream[0].index)",
        '-pix_fmt',$stream[0].pix_fmt,'-f','framemd5','-') -Wait -PassThru -NoNewWindow -RedirectStandardOutput $hashFile
    $rows = @((Read-TextShared $hashFile) -split "`r?`n" | Where-Object { $_ -match '^\s*\d+,.+,[\s]*[0-9a-f]{32}\s*$' })
    if ($decode.ExitCode -ne 0 -or $rows.Count -ne [int]$stream[0].nb_frames) { return $null }
    @{ Frames=$rows.Count; Hashes=@($rows | ForEach-Object { ($_ -split ',')[-1].Trim() }) }
}
$seq601 = Read-SequenceYuv $rJ1.file 'smpte170m'; $seq709 = Read-SequenceYuv $rJ4.file 'bt709'
CK ($seq601 -and $seq709 -and $seq601.Frames -eq $seq709.Frames -and
    (($seq601.Hashes -join ':') -cne ($seq709.Hashes -join ':'))) 'J4 timed colour track: matrix readback and native sample A/B across all decoded frames'
if ($rJ4.file) {
    $back601 = Run 'j4_back_601' 'gif' @('--color-matrix','bt470bg') $rJ4.file
    $back709 = Run 'j4_back_709' 'gif' @('--color-matrix','bt709') $rJ4.file
    function DecodedGifHash([string]$path) {
        if (-not $path) { return $null }
        $o = Join-Path $work '_gifback.md5'
        $p = Start-Process -FilePath $ff -ArgumentList @('-v','error','-i',"`"$path`"",'-pix_fmt','rgb24','-f','framemd5','-') `
            -Wait -PassThru -NoNewWindow -RedirectStandardOutput $o
        if ($p.ExitCode -ne 0) { return $null }
        return @((Read-TextShared $o) -split "`r?`n" | Where-Object { $_ -match '^\s*\d+,.+,[\s]*[0-9a-f]{32}\s*$' } |
            ForEach-Object { ($_ -split ',')[-1].Trim() })
    }
    $gif601 = @(DecodedGifHash $back601.file); $gif709 = @(DecodedGifHash $back709.file)
    CK ($back601.code -eq 0 -and $back709.code -eq 0 -and $seq709 -and
        $gif601.Count -eq $seq709.Frames -and $gif709.Count -eq $seq709.Frames -and
        ($gif601 -join ':') -cne ($gif709 -join ':') -and
        $back601.log -match '-map 0:\d+ -vf "scale=in_color_matrix=bt470bg:' -and
        $back709.log -match '-map 0:\d+ -vf "scale=in_color_matrix=bt709:') 'J4 reverse animation: input matrix reaches the mapped colour track and changes decoded GIF pixels'
} else { CK $false 'J4 reverse animation has no AVIF input' }

# J5: auto 负控——不传显式矩阵 ⇒ 两步法零 `[color-out-matrix] ` 播报（CM-REQ-08 零噪声）
$rJ5 = Run 'j5_gif2avif_auto' 'avif' @() $gifSrc
CK ($rJ5.code -eq 0 -and ($rJ5.log -notmatch '\[color-out-matrix\] ')) "J5 GIF->AVIF auto: exit=$($rJ5.code), ZERO [color-out-matrix] lines (negative control)"

# J6: bmp × bt709（无色彩通路）⇒ exit 0 + 产物非空 + 「该出口无矩阵承载（bmp）…未生效」播报
$rJ6 = Run 'j6_bmp_bt709' 'bmp' @('--color-out-matrix','bt709')
CK ($rJ6.code -eq 0 -and ($null -ne $rJ6.file) -and ((Get-Item $rJ6.file).Length -gt 0)) "J6 bmp x bt709: exit=$($rJ6.code), produced non-empty (runs normally, NOT fail-closed)"
$hit = LogHit $rJ6.log '该出口无矩阵承载（bmp）：显式值 bt709 未生效' 1
CK ($hit.Count -ge 1) "J6 bmp no-carrier broadcast verbatim"

# J7: ppm × bt709 ⇒ 同上（ppm 与 bmp 同族、但历史上只有 rgb 档侥幸有播报）
$rJ7 = Run 'j7_ppm_bt709' 'ppm' @('--color-out-matrix','bt709')
CK ($rJ7.code -eq 0 -and ($null -ne $rJ7.file)) "J7 ppm x bt709: exit=$($rJ7.code), produced=$($null -ne $rJ7.file)"
$hit = LogHit $rJ7.log '该出口无矩阵承载（ppm）：显式值 bt709 未生效' 1
CK ($hit.Count -ge 1) "J7 ppm no-carrier broadcast verbatim"

# J8: bmp auto 负控 ⇒ 零 `[color-out-matrix] ` 行
$rJ8 = Run 'j8_bmp_auto' 'bmp' @()
CK ($rJ8.code -eq 0 -and ($rJ8.log -notmatch '\[color-out-matrix\] ')) "J8 bmp auto: exit=$($rJ8.code), ZERO [color-out-matrix] lines (negative control)"

# J9: ppm × rgb（Redundant 格）⇒ 契约模板 2 **恰好一行**（出口层与任务收尾兜底不重复播报，计划 §2.3 第 5 条）
$rJ9 = Run 'j9_ppm_rgb' 'ppm' @('--color-out-matrix','rgb')
$omLines = @(($rJ9.log -split "`r?`n") | Select-String '\[color-out-matrix\] ' | ForEach-Object { $_.Line })
CK ($rJ9.code -eq 0 -and ($omLines.Count -eq 1)) "J9 ppm x rgb (Redundant): exit=$($rJ9.code), exactly ONE [color-out-matrix] line (got $($omLines.Count))"
$hit = LogHit $rJ9.log '\[color-out-matrix\] rgb on ppm: container is already RGB; recorded, no pixel effect' 1
CK ($hit.Count -ge 1) "J9 template-2 verbatim on the non-planning bmp/ppm family"

Write-Output ""
Write-Output "===== K avif 16-bit sample transform (encode + decode, 2026-11-13) ====="
# K1: 16-bit 源 → avif 无损 → 容器必须两条 av01（base + extension），
#     base 为 gbrp12le、extension 为 yuv444p（avifenc --depth 12,4 实测布局）。
# K2: 把同一产物喂回产品解码 → 16-bit rgb48be PNG；像素 md5 与源逐字节相等。
#   ⚠ 只在 K2 同时通过时才证明"编码正确 + 软件识别/解码正确"；K1 单过只能证明编码器产出了双 item。
$src16 = Join-Path $work 'src16.png'
$genArgs = @('-y','-hide_banner','-loglevel','error','-f','lavfi','-i',
    "nullsrc=s=512x384,format=rgb48le,geq=r='random(1)*65535':g='random(2)*65535':b='random(3)*65535'",
    '-frames:v','1','-pix_fmt','rgb48be', $src16)
$gen = Start-Process -FilePath $ff -ArgumentList $genArgs -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $work 'k0_gen.o') `
        -RedirectStandardError (Join-Path $work 'k0_gen.e')
CK ($gen.ExitCode -eq 0 -and (Test-Path $src16)) "K0 true 16-bit source generated (ffmpeg geq; low bits nonzero)"

$rK1 = Run 'k1_avif16_lossless' 'avif' @('--lossless','true','--bit-depth','16','--color-engine','engine') $src16
$vsK1 = if ($rK1.file) { ProbeVStreams $rK1.file } else { @() }
CK ($rK1.code -eq 0 -and ($null -ne $rK1.file)) "K1 avif 16-bit lossless: exit=$($rK1.code), produced=$($null -ne $rK1.file)"
CK ($vsK1.Count -eq 2) "K1 container has exactly 2 video streams (base + extension); got $($vsK1.Count): $($vsK1 -join ' | ')"
CK ($vsK1.Count -eq 2 -and $vsK1[0] -match '^\d+,gbrp12le$') "K1 base pix_fmt = gbrp12le (12-bit RGB base; full='$($vsK1 -join ' | ')')"
CK ($vsK1.Count -eq 2 -and $vsK1[1] -match '^\d+,yuv444p$') "K1 extension pix_fmt = yuv444p (extension item; full='$($vsK1 -join ' | ')')"
# K1 直接校验 sato 派生项对两个 av01 item 的引用；K2 再证明不是只读 12-bit base。
CK ($rK1.file -and (HasSampleTransform $rK1.file)) "K1 ISO-BMFF sato item references both distinct av01 items through dimg"
CK ($rK1.log -notmatch '出口位深降为 12bit（本次目标 16bit）') "K1 no false 16->12 degradation broadcast (sato fulfills 16-bit)"

$rK2 = @{ code = -1; log = ''; file = $null }
if ($rK1.file) {
    $rK2 = Run 'k2_avif16_roundtrip' 'png' @('--bit-depth','16','--color-engine','engine') $rK1.file
}
CK ($rK2.code -eq 0 -and ($null -ne $rK2.file)) "K2 16-bit AVIF decoded by product: exit=$($rK2.code), produced=$($null -ne $rK2.file)"
if ($rK2.file) {
    $md5K0 = Md5Rgb48 $src16
    $md5K2 = Md5Rgb48 $rK2.file
    CK ($md5K0 -ne '' -and $md5K0 -ceq $md5K2) "K2 pixel md5 bit-exact after AVIF roundtrip (src=$md5K0, dec=$md5K2)"
} else {
    CK $false "K2 pixel md5 bit-exact after AVIF roundtrip (no decoded product)"
}
$hitK2 = LogHit $rK2.log '\[avif16\].*(Sample Transform|sato|重建)' 1
CK ($hitK2.Count -ge 1) "K2 sample-transform recognition landed (sato + dimg, not 12-bit base only)"

Write-Output ""
# K3: reject ambiguous dimg, advertised precision, and unrelated altr color inheritance before guessing.
if ($rK1.file -and (HasSampleTransform $rK1.file)) {
    $originalK = [IO.File]::ReadAllBytes($rK1.file)
    $metaK = BoxOf $originalK 0 $originalK.Length 'meta'
    $irefK = if ($metaK) { BoxOf $originalK ($metaK.start+4) $metaK.end 'iref' } else { $null }
    $iinfK = if ($metaK) { BoxOf $originalK ($metaK.start+4) $metaK.end 'iinf' } else { $null }
    $pitmK = if ($metaK) { BoxOf $originalK ($metaK.start+4) $metaK.end 'pitm' } else { $null }
    $grplK = if ($metaK) { BoxOf $originalK ($metaK.start+4) $metaK.end 'grpl' } else { $null }
    $dimgK = if ($irefK) { BoxOf $originalK ($irefK.start+4) $irefK.end 'dimg' } else { $null }
    $altrK = if ($grplK) { BoxOf $originalK $grplK.start $grplK.end 'altr' } else { $null }
    $infeK = if ($iinfK -and $iinfK.end - $iinfK.start -ge 6) { @(Boxes $originalK ($iinfK.start+6) $iinfK.end | Where-Object { $_.type -ceq 'infe' }) } else { @() }
    $satoIdsK = @(); $av01IdsK = @()
    foreach ($entryK in $infeK) {
        if ($entryK.end - $entryK.start -lt 12 -or $originalK[$entryK.start] -ne 2) { continue }
        $idK = Be $originalK ($entryK.start+4) 2
        $typeK = [Text.Encoding]::ASCII.GetString($originalK,$entryK.start+8,4)
        if ($typeK -ceq 'sato') { $satoIdsK += $idK }
        if ($typeK -ceq 'av01') { $av01IdsK += $idK }
    }
    $pitmIdK = if ($pitmK -and $pitmK.end - $pitmK.start -eq 6 -and $originalK[$pitmK.start] -eq 0) { Be $originalK ($pitmK.start+4) 2 } else { -1 }
    $dimgFromK = if ($dimgK -and $dimgK.end - $dimgK.start -eq 8) { Be $originalK $dimgK.start 2 } else { -1 }
    $dimgCountK = if ($dimgK -and $dimgK.end - $dimgK.start -eq 8) { Be $originalK ($dimgK.start+2) 2 } else { -1 }
    $baseItemK = if ($dimgCountK -eq 2) { Be $originalK ($dimgK.start+4) 2 } else { -1 }
    $extensionItemK = if ($dimgCountK -eq 2) { Be $originalK ($dimgK.start+6) 2 } else { -1 }
    $basePropsK = if ($baseItemK -ge 0) { @(PrimaryAvifProperties $originalK $baseItemK) } else { @() }
    $satoPropsK = if ($satoIdsK.Count -eq 1) { @(PrimaryAvifProperties $originalK $satoIdsK[0]) } else { @() }
    $extensionPropsK = if ($extensionItemK -ge 0) { @(PrimaryAvifProperties $originalK $extensionItemK) } else { @() }
    $baseColrK = @($basePropsK | Where-Object { $_.type -ceq 'colr' })
    $satoColrK = @($satoPropsK | Where-Object { $_.type -ceq 'colr' })
    $extensionColrK = @($extensionPropsK | Where-Object { $_.type -ceq 'colr' })
    $codecK = @($basePropsK | Where-Object { $_.type -ceq 'av1C' })
    $inputPixiK = @($basePropsK | Where-Object { $_.type -ceq 'pixi' })
    # Sample-transform output precision belongs to the sato item, not the pitm base image.
    $outputPixiK = @($satoPropsK | Where-Object { $_.type -ceq 'pixi' })
    $altrFixtureValid = $false
    if ($altrK -and $dimgK -and $satoIdsK.Count -eq 1 -and $pitmIdK -ge 0 -and
        $altrK.end - $altrK.start -eq 20 -and $dimgCountK -eq 2) {
        $altrFlagsK = Be $originalK $altrK.start 4
        $altrGroupK = Be $originalK ($altrK.start+4) 4
        $altrEntitiesK = Be $originalK ($altrK.start+8) 4
        $altrFirstK = Be $originalK ($altrK.start+12) 4
        $altrSecondK = Be $originalK ($altrK.start+16) 4
        $altrFixtureValid = ($altrFlagsK -eq 0 -and $altrGroupK -eq 4 -and $altrEntitiesK -eq 2 -and
            $altrFirstK -eq $satoIdsK[0] -and $altrSecondK -eq $pitmIdK -and
            $dimgFromK -eq $satoIdsK[0] -and $baseItemK -eq $pitmIdK -and
            $av01IdsK.Count -eq 2 -and $baseItemK -in $av01IdsK -and $extensionItemK -in $av01IdsK -and
            $baseItemK -ne $extensionItemK -and $baseColrK.Count -gt 0 -and
            $satoColrK.Count -eq 0 -and $extensionColrK.Count -eq 0)
    }
    if ($altrFixtureValid) {
        CK $true 'K3 unrelated_altr fixture shape: legacy base-only colr, altr v0/group 4/count 2 = [sato, primary], dimg = [sato, primary, extension]'
    } else {
        Write-Output '  SKIP K3 unrelated_altr: K1 is not the legacy base-only-colr altr layout; strict-color K1 does not require altr'
    }
    $k3Kinds = @('duplicate_dimg','output_precision','input_depth_mismatch')
    if ($altrFixtureValid) { $k3Kinds += 'unrelated_altr' }
    foreach ($kind in $k3Kinds) {
        $mutant = [byte[]]$originalK.Clone(); $patchOk=$false
        if($kind -eq 'duplicate_dimg' -and $dimgCountK -eq 2){
            [Array]::Copy($mutant,$dimgK.start+4,$mutant,$dimgK.start+6,2); $patchOk=$true
        } elseif($kind -eq 'output_precision' -and $outputPixiK.Count -eq 1){
            $pix=$outputPixiK[0]; $channels=$mutant[$pix.start+4]
            if($channels -eq 3 -and $pix.end-$pix.start -eq 8 -and $mutant[$pix.start+5] -eq 16 -and
                $mutant[$pix.start+6] -eq 16 -and $mutant[$pix.start+7] -eq 16){
                for($i=0;$i -lt 3;$i++){$mutant[$pix.start+5+$i]=12};$patchOk=$true
            }
        } elseif($kind -eq 'input_depth_mismatch' -and $codecK.Count -eq 1 -and $inputPixiK.Count -eq 1){
            $codec=$codecK[0];$pix=$inputPixiK[0]
            if($codec.end-$codec.start -ge 4 -and ($mutant[$codec.start+2] -band 96) -eq 96 -and
                $mutant[$pix.start+4] -eq 3 -and $pix.end-$pix.start -eq 8 -and $mutant[$pix.start+5] -eq 12){
                $mutant[$codec.start+2]=$mutant[$codec.start+2] -band 223
                for($i=0;$i -lt 3;$i++){$mutant[$pix.start+5+$i]=10};$patchOk=$true
            }
        } elseif($kind -eq 'unrelated_altr' -and $altrFixtureValid -and $extensionItemK -le 255){
            $newId=[long]$extensionItemK
            for($i=3;$i -ge 0;$i--){$mutant[$altrK.start+16+$i]=[byte]($newId -band 255);$newId=[long][Math]::Floor($newId/256.0)}
            $changed=@();for($i=0;$i -lt $originalK.Length;$i++){if($mutant[$i] -ne $originalK[$i]){$changed+= $i}}
            $patchOk=($mutant.Length -eq $originalK.Length -and $changed.Count -eq 1 -and
                $changed[0] -eq $altrK.start+19 -and $mutant[$altrK.start+19] -eq $extensionItemK)
        }
        CK $patchOk "K3 $kind fixture: bounded metadata-only patch, encoded AV1 items remain unchanged"
        if($patchOk){
            $path=Join-Path $work ("k3_$kind.avif");[IO.File]::WriteAllBytes($path,$mutant)
            $rejected=Run ("k3_$kind") 'png' @('--bit-depth','16','--color-engine','engine') $path
            CK (Rejected91 $rejected) "K3 ${kind}: one task 91, process 1, zero guessed output"
            $reason = switch($kind){
                'duplicate_dimg' {'AVIF_SAMPLE_TRANSFORM_INPUTS_INVALID'}
                'output_precision' {'AVIF_SAMPLE_TRANSFORM_OUTPUT_DEPTH_UNSUPPORTED'}
                'input_depth_mismatch' {'AVIF_SAMPLE_TRANSFORM_INPUT_DEPTH_MISMATCH'}
                'unrelated_altr' {'AVIF_SAMPLE_TRANSFORM_COLOR_UNVERIFIED'}
            }
            CK ($rejected.log -match ('\b'+$reason+'\b')) "K3 $kind rejection has the stable sample-transform contract reason $reason"
            if($kind -eq 'unrelated_altr'){
                CK ($rejected.log -match '\bAVIF_SAMPLE_TRANSFORM_INPUT_UNVERIFIED\b' -and
                    $rejected.log -match '\bAVIF_SAMPLE_TRANSFORM_COLOR_UNVERIFIED\b') `
                    'K3 unrelated_altr has the expected outer INPUT_UNVERIFIED and inner COLOR_UNVERIFIED reason chain'
            }
        }else{CK $false "K3 $kind cannot run without its independently checked fixture structure"}
    }
} else { CK $false 'K3 requires the valid K1 sample-transform artifact' }

Write-Output "===== L AVIF ICC exit (matrix, native samples, range/chroma and terminal reject) ====="
# 置黑/红/白三个 32px 色块；444/RGB 臂提供变换后原生 RGB 取数，601 公式作为错误矩阵负控。
$srcIcc = Join-Path $work 'icc_swatches.png'
$genIcc = @('-y','-v','error','-f','lavfi','-i','color=black:s=32x32:r=1,format=rgb24',
    '-f','lavfi','-i','color=red:s=32x32:r=1,format=rgb24','-f','lavfi','-i','color=white:s=32x32:r=1,format=rgb24',
    '-filter_complex','[0][1][2]hstack=3','-frames:v','1',"`"$srcIcc`"")
$mkIcc = Start-Process -FilePath $ff -ArgumentList $genIcc -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput (Join-Path $work 'l0_gen.o') -RedirectStandardError (Join-Path $work 'l0_gen.e')
CK ($mkIcc.ExitCode -eq 0 -and (Test-Path -LiteralPath $srcIcc)) 'L0 RGB black/red/white swatches generated'
$iccArgs = @('--color-engine','engine','--color-strategy','manual','--color-space','Display P3','--quality','100')
$lYuv = Run 'l1_icc_709' 'avif' ($iccArgs + @('--color-out-matrix','bt709','--color-range','tv','--chroma','4:2:0')) $srcIcc
$lRgb = Run 'l2_icc_rgb' 'avif' ($iccArgs + @('--color-out-matrix','rgb','--color-range','pc','--chroma','4:4:4','--lossless','true')) $srcIcc
$csYuv = if ($lYuv.file) { ProbeCs $lYuv.file } else { '' }
$csIccRgb = if ($lRgb.file) { ProbeCs $lRgb.file } else { '' }
CK ($lYuv.code -eq 0 -and $csYuv -ceq 'yuv420p,tv,bt709') "L1 decoded representation YUV 4:2:0 limited BT.709 (rc=$($lYuv.code); '$csYuv')"
CK ($lRgb.code -eq 0 -and $csIccRgb -ceq 'gbrp,pc,gbr') "L2 decoded representation RGB 4:4:4 full identity (rc=$($lRgb.code); '$csIccRgb')"
# Both profiles describe Display P3/sRGB as full-range RGB; only the pixel storage nclx differs.
# Check each profile against that contract, not against the byte serialization of the other arm.
$yuvAvifBytes = if ($lYuv.file) { ,([IO.File]::ReadAllBytes($lYuv.file)) } else { $null }
$rgbAvifBytes = if ($lRgb.file) { ,([IO.File]::ReadAllBytes($lRgb.file)) } else { $null }
$mYuv = if ($yuvAvifBytes) { ReadAvifColor $yuvAvifBytes } else { $null }
$mRgb = if ($rgbAvifBytes) { ReadAvifColor $rgbAvifBytes } else { $null }
$rgbCicp = '12/13/0/1'; $yuvNclx = '12/13/1/0'
foreach ($arm in @(@{ name = 'L1'; color = $mYuv; nclx = $yuvNclx }, @{ name = 'L2'; color = $mRgb; nclx = $rgbCicp })) {
    $c = $arm.color; $name = $arm.name; $expected = $arm.nclx
    CK ($c.nclx.Count -eq 1 -and $c.nclx[0].value -ceq $expected) `
        "$name primary item nclx P/T/M/full = $expected (got $($c.nclx.value -join ','))"
    $profile = if ($c.icc.Count -eq 1) { $c.icc[0].profile } else { $null }
    CK ($null -ne $profile) "$name primary item carries one structurally valid ICC v4.4 RGB matrix/TRC profile"
    CK ($profile.idOk) "$name ICC Profile ID is nonzero and equals the ICC.1 MD5 checksum (stored $($profile.id))"
    CK ($profile.cicp -ceq $rgbCicp) "$name ICC cicp = $rgbCicp (got $($profile.cicp)); describes full-range RGB independently of nclx"
    CK (ProfileMatchesP3Target $profile) `
        "$name ICC numerical target: D50 whitepoint + Display P3 XYZ matrix + sRGB TRC samples (cICP alone is insufficient)"
}

# In-memory mutants isolate each detector, keep the AVIF media samples unchanged, and add no encoder runs.
$embedded = if ($mYuv.icc.Count -eq 1) { $mYuv.icc[0] } else { $null }
if ($embedded.profile.idOk -and $embedded.profile.cicp -ceq $rgbCicp -and $mYuv.nclx.Count -eq 1 -and $mYuv.nclx[0].value -ceq $yuvNclx) {
    # Flipping legitimate flags and changing the rendering intent must not change a valid ID.
    $headerVariant = [byte[]]$embedded.bytes.Clone()
    $headerVariant[47] = $headerVariant[47] -bxor 1
    $headerVariant[67] = ($headerVariant[67] + 1) % 4
    $variant = ReadRgbIcc44 $headerVariant
    CK ($variant.idOk -and $variant.id -ceq $embedded.profile.id -and $variant.cicp -ceq $rgbCicp) `
        'L Profile ID normalization: changing only flags and rendering intent does not invalidate the same checksum'

    $idMutant = [byte[]]$embedded.bytes.Clone()
    $idMutant[84] = $idMutant[84] -bxor 1
    $badId = ReadRgbIcc44 $idMutant
    [Array]::Clear($idMutant, 84, 16)
    $zeroId = ReadRgbIcc44 $idMutant
    CK ($badId.cicp -ceq $rgbCicp -and -not $badId.idOk -and $zeroId.cicp -ceq $rgbCicp -and -not $zeroId.idOk) `
        'L negative control: both wrong nonzero and zero Profile IDs fail despite valid structure and cicp'

    $cicpOffset = $embedded.profile.cicpTag.Off
    $wrongIcc = [byte[]]$embedded.bytes.Clone()
    $wrongIcc[$cicpOffset + 10] = 1; $wrongIcc[$cicpOffset + 11] = 0
    [Array]::Copy((IccIdDigest $wrongIcc), 0, $wrongIcc, 84, 16)
    $badIcc = ReadRgbIcc44 $wrongIcc
    CK ($badIcc.idOk -and $badIcc.cicp -ceq $yuvNclx -and $badIcc.cicp -cne $rgbCicp) `
        'L negative control: copying storage nclx into ICC cicp violates RGB semantics even with a valid Profile ID'

    # A correct checksum cannot make a tag table entry which points into the header valid.
    [Array]::Clear($wrongIcc, $embedded.profile.cicpTag.Entry + 4, 4)
    [Array]::Copy((IccIdDigest $wrongIcc), 0, $wrongIcc, 84, 16)
    CK ($null -eq (ReadRgbIcc44 $wrongIcc)) 'L negative control: rejects a cicp tag pointing into the ICC header'

    $wrongAvif = [byte[]]$yuvAvifBytes.Clone(); $p = $mYuv.nclx[0].sampleOffset
    $wrongAvif[$p + 1] = 0; $wrongAvif[$p + 2] = 128
    $badAvif = ReadAvifColor $wrongAvif
    CK ($badAvif.nclx.Count -eq 1 -and $badAvif.nclx[0].value -ceq $rgbCicp -and $badAvif.nclx[0].value -cne $yuvNclx -and
        $badAvif.icc.Count -eq 1 -and $badAvif.icc[0].profile.idOk -and $badAvif.icc[0].profile.cicp -ceq $rgbCicp) `
        'L negative control: copying ICC cicp into YUV storage nclx violates sample semantics while ICC remains valid'

    # Valid Profile IDs and unchanged RGB cicp must not hide a wrong matrix or wrong TRC.
    $wrongMatrix = [byte[]]$embedded.bytes.Clone()
    $redX = $embedded.profile.tags['rXYZ'].Off + 8
    WriteIccS15 $wrongMatrix $redX ((IccS15 $wrongMatrix $redX) + 0.05)
    [Array]::Copy((IccIdDigest $wrongMatrix), 0, $wrongMatrix, 84, 16)
    $badMatrixProfile = ReadRgbIcc44 $wrongMatrix
    CK ($badMatrixProfile.idOk -and $badMatrixProfile.cicp -ceq $rgbCicp -and
        -not (ProfileMatchesP3Target $badMatrixProfile)) `
        'L negative control: altered rXYZ with recomputed valid Profile ID and unchanged cicp fails the numerical target'

    $wrongTrc = [byte[]]$embedded.bytes.Clone()
    $trcMutated = MutateIccCurve $wrongTrc $embedded.profile.tags['rTRC']
    [Array]::Copy((IccIdDigest $wrongTrc), 0, $wrongTrc, 84, 16)
    $badTrcProfile = ReadRgbIcc44 $wrongTrc
    CK ($trcMutated -and $badTrcProfile.idOk -and $badTrcProfile.cicp -ceq $rgbCicp -and
        -not (ProfileMatchesP3Target $badTrcProfile)) `
        'L negative control: altered rTRC with recomputed valid Profile ID and unchanged cicp fails the sampled curve target'
} else { CK $false 'L negative controls require the valid L1 ICC and YUV nclx positive control' }
CK (OneMatrixResult $lYuv.log '\[color-out-matrix\] avif: bt709 -> .*') 'L1 exactly one landed BT.709 result'
CK (OneMatrixResult $lRgb.log '\[color-out-matrix\] avif: rgb -> .*') 'L2 exactly one landed identity result'

function ReadRawFrame([string]$path, [string]$tag, [string[]]$videoArgs, [int]$length) {
    $raw = Join-Path $work "$tag.raw"
    $argsRaw = @('-y','-v','error','-i',"`"$path`"",'-map','0:v:0','-frames:v','1') + $videoArgs + @('-f','rawvideo',"`"$raw`"")
    $proc = Start-Process -FilePath $ff -ArgumentList $argsRaw -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $work "$tag.o") -RedirectStandardError (Join-Path $work "$tag.e")
    if ($proc.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $raw)) { return }
    $b = [IO.File]::ReadAllBytes($raw)
    if ($b.Length -eq $length) { return ,$b }
}
$yL = if ($lYuv.file) { ReadRawFrame $lYuv.file 'l_y' @('-vf','extractplanes=y','-pix_fmt','gray') (96 * 32) } else { $null }
$rgbL = if ($lRgb.file) { ReadRawFrame $lRgb.file 'l_rgb' @('-pix_fmt','rgb24') (96 * 32 * 3) } else { $null }
if ($null -ne $yL -and $null -ne $rgbL) {
    $black = 16 * 96 + 16; $red = 16 * 96 + 48; $white = 16 * 96 + 80
    $rr = [int]$rgbL[3 * $red]; $gg = [int]$rgbL[3 * $red + 1]; $bb = [int]$rgbL[3 * $red + 2]
    $y709 = 16 + 219 * (0.2126 * $rr + 0.7152 * $gg + 0.0722 * $bb) / 255
    $y601 = 16 + 219 * (0.299 * $rr + 0.587 * $gg + 0.114 * $bb) / 255
    CK ([Math]::Abs($yL[$black] - 16) -le 3 -and [Math]::Abs($yL[$white] - 235) -le 3) `
        "L1 native Y black/white are limited-range (got $($yL[$black])/$($yL[$white]))"
    CK ([Math]::Abs($y709 - $y601) -ge 8 -and [Math]::Abs($yL[$red] - $y709) -le 3 -and [Math]::Abs($yL[$red] - $y601) -gt 3) `
        "L1 red swatch native Y=$($yL[$red]); matches BT.709=$y709 and rejects BT.601=$y601 (RGB=$rr/$gg/$bb)"
} else { CK $false 'L1/L2 native sample oracle could not decode both arms' }

# L5: same source/target/chroma/depth/quality; only output range and actual encoder route vary.
# Native Y/Cb/Cr are compared to a source-derived RGB->P3->BT.709 formula, never the L2 product.
$sourceRgbL = ReadRawFrame $srcIcc 'l5_original_rgb' @('-pix_fmt','rgb24') (96 * 32 * 3)
function SourceRgbToP3([int]$r,[int]$g,[int]$b) {
    $lin = @()
    foreach ($v in @($r,$g,$b)) { $e=$v/255.0; $lin += $(if($e -le .04045){$e/12.92}else{[Math]::Pow(($e+.055)/1.055,2.4)}) }
    # From IEC sRGB / EG432 P3 xy primaries and D65, independently of product registry/ICC bytes.
    $rows = @(@(.8224619687143624,.1775380312856376,0.0),@(.0331941988509615,.9668058011490385,0.0),@(.0170826307211200,.0723974406639630,.9105199286149170))
    $encoded = @()
    foreach($row in $rows) {
        $l = [Math]::Max(0.0,[Math]::Min(1.0,($row[0]*$lin[0]+$row[1]*$lin[1]+$row[2]*$lin[2])))
        $encoded += $(if($l -le .0031308){12.92*$l}else{1.055*[Math]::Pow($l,1.0/2.4)-.055})
    }
    return ,$encoded
}
$nativeYuvRefs = @{}
foreach ($range in @('pc','tv')) {
    $samples = @()
    foreach ($sampleX in @(16,48,80)) {
        $at = (16*96+$sampleX)*3
        $rgb = if ($sourceRgbL) { SourceRgbToP3 $sourceRgbL[$at] $sourceRgbL[$at+1] $sourceRgbL[$at+2] } else { @() }
        if ($rgb.Count -ne 3) { continue }
        $yy=.2126*$rgb[0]+.7152*$rgb[1]+.0722*$rgb[2]
        $yScale=255.0; $yOffset=0.0; $cScale=255.0
        if($range -eq 'tv'){$yScale=219.0;$yOffset=16.0;$cScale=224.0}
        $samples += ,@(($yOffset+$yScale*$yy),(128+$cScale*($rgb[2]-$yy)/(2*(1-.0722))),(128+$cScale*($rgb[0]-$yy)/(2*(1-.2126))))
    }
    $nativeYuvRefs[$range] = $samples
}
$rangeNative = @{}
# Use the existing AVIF AOM lossless preset to select its concrete EncoderName=libaom-av1;
# override lossless and the preset's 4:4:4/auto-depth axes for this 8-bit 4:2:0 route probe.
$aomPreset = [string]::Concat([char]::ConvertFromUtf32(0x1F680), ' AVIF AOM ', [char]0x2014, ' ', [char]0x65E0, [char]0x635F, ' (CRF 0, 4:4:4, cpu=1)')
foreach ($route in @('icc-avifenc','cicp-ffmpeg')) {
    foreach ($range in @('pc','tv')) {
        $tag = "l5_${route}_$range"
        $strategy = if ($route -eq 'icc-avifenc') { 'manual' } else { 'cicp-only' }
        $cell = Run $tag 'avif' @('--preset',$aomPreset,'--encoder','Ffmpeg','--color-engine','engine','--color-strategy',$strategy,
            '--color-space','Display P3','--color-out-matrix','bt709','--color-range',$range,'--chroma','4:2:0','--bit-depth','8','--quality','100','--lossless','false') $srcIcc
        $color = if($cell.file){ReadAvifColor ([IO.File]::ReadAllBytes($cell.file))}else{$null}
        $fullBit = if($range -eq 'pc'){'1'}else{'0'}
        $nclx = "12/13/1/$fullBit"
        CK ($cell.code -eq 0 -and $color.nclx.Count -eq 1 -and $color.nclx[0].value -ceq $nclx -and
            (ProbeCs $cell.file) -ceq "yuv420p,$range,bt709") "L5 $tag actual primary nclx/storage matches fixed BT.709/420/8 and selected range"
        $routeOk = if($route -eq 'icc-avifenc'){
            $cell.log -match '\[avif\] avifenc .*--cicp 12/13/1' -and $cell.log -match '--icc ' -and $color.icc.Count -eq 1
        }else{
            $cell.log -match 'ffmpeg .* -c:v libaom-av1' -and $cell.log -notmatch '\[avif\] avifenc' -and $color.icc.Count -eq 0
        }
        CK $routeOk "L5 $tag real encoder command and ICC/CICP route are verified, no silent route substitution"
        $yuv = if($cell.file){ReadRawFrame $cell.file $tag @('-pix_fmt','yuv420p') (96*32*3/2)}else{$null}
        $maxError=1000000.0; $gotSamples=@()
        if($yuv -and $nativeYuvRefs[$range].Count -eq 3){
            $maxError=0.0; $sampleIndex=0
            foreach($sampleX in @(16,48,80)){
                $yAt=16*96+$sampleX; $cAt=8*48+[int]($sampleX/2)
                $got=@([int]$yuv[$yAt],[int]$yuv[96*32+$cAt],[int]$yuv[96*32+48*16+$cAt]); $gotSamples += ,$got
                for($channel=0;$channel -lt 3;$channel++){$maxError=[Math]::Max($maxError,[Math]::Abs($got[$channel]-$nativeYuvRefs[$range][$sampleIndex][$channel]))}
                $sampleIndex++
            }
        }
        CK ($sourceRgbL -and $maxError -le 3.0) "L5 $tag source-independent native Y/Cb/Cr samples agree with P3/BT.709 formula (max=$maxError codes <=3)"
        $rangeNative[$tag]=$gotSamples
    }
    $pc=$rangeNative["l5_${route}_pc"]; $tv=$rangeNative["l5_${route}_tv"]
    CK ($pc.Count -eq 3 -and $tv.Count -eq 3 -and [Math]::Abs($pc[0][0]-$tv[0][0]) -ge 12 -and
        [Math]::Abs($pc[2][0]-$tv[2][0]) -ge 15) "L5 $route range negative control: paired full/limited black and white samples are distinguishable"
}

$jpegIccRun = Run 'l4_legacy_jpeg_icc' 'jpg' @('--color-engine','legacy','--color-space','Display P3','--icc-profile-version','v2','--quality','100') $srcIcc
$jpegIccBytes = if ($jpegIccRun.file) { ReadJpegIcc $jpegIccRun.file } else { $null }
$jpegIcc = if ($jpegIccBytes) { ReadRgbIcc44 $jpegIccBytes } else { $null }
CK ($jpegIccRun.code -eq 0 -and $jpegIcc.idOk -and $jpegIcc.cicp -ceq '12/13/0/1' -and
    (ProfileMatchesP3Target $jpegIcc) -and $jpegIccRun.log -match 'Legacy ICC v2 preference ignored') `
    'L4 JPEG APP2: old version preference is accepted; generated ICC v4.4 has valid ID, RGB cicp and measured P3/sRGB colorants/TRC'

$lReject = Run 'l3_icc_lossless_709' 'avif' ($iccArgs + @('--color-out-matrix','bt709','--lossless','true')) $srcIcc -JsonLines
CK (Rejected91 $lReject) 'L3 ICC/lossless BT.709 conflict: JSONL task 91, process 1, zero output'
CK ($lReject.log -match '\bCOLOR_MATRIX_REJECTED\b' -and $lReject.log -notmatch '\[color-out-matrix\] avif: bt709 ->') `
    'L3 stable matrix rejection reason, never a false landed result'

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
if (-not $KeepWork -and $fail -eq 0 -and $ncOk) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
else { Write-Output "[keepwork] product area kept: $work" }

Write-Output ""

$allOk = ($fail -eq 0) -and $ncOk
Write-Output "OUTMATRIX RESULT: pass=$pass fail=$fail"
if (-not $allOk) { exit 1 }
exit 0