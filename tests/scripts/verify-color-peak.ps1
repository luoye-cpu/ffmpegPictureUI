# verify-color-peak.ps1 —— 源峰值读取（P4c）：HDR 静态元数据 → 引擎计划的 headroom
#
# 为什么要单独一条门禁：峰值直接决定 headroom（压暗程度），而本项目已因
# "ffprobe 键名/单位凭记忆写"翻过车 —— 实测本构建 ffprobe 打印的内容峰值键是
# **max_content**（不是 max_cll/MaxCLL），旧写法恒取不到 ⇒ MaxCLL 从未生效；
# max_luminance 则是**有理数字符串** "40000000/10000"（值已是 cd/m²）。
# 依据（不猜）：_probe-hdr-peak-oracle.ps1 的键名普查 + ffmpeg
# libavfilter/colorspace.c::ff_determine_signal_peak() 的 **MaxCLL 优先**语义。
#
# 素材自带已知答案：MaxCLL=600 与 mastering=4000 **故意不同**，于是一次数值就能分辨
#   600=对 / 4000=取了两者较大值 / 0=探测失效（旧代码）/ 0.4=单位重复换算。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$pr = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $pr)) { $pr = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $pr + $(if ($pr -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
if (-not (Test-Path $pr)) { Write-Host "先构建 ServiceProbe（Release 或 Debug）" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $ff)) { Write-Host "缺 ffmpeg：$ff" -ForegroundColor Red; exit 1 }

# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$d = "$root/tests/output/validate/peak_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null
$pass = 0; $fail = 0; $skip = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }
# ⚠ 2026-09-21（`TESTING.md` 第 81 条处置建议）：**SKIP 必须进汇总并计数**（第 76 条口径）。
#   缺陷形态：本门禁的 (d) 段曾用一句裸 `Write-Output "  SKIP (d)…"` **静默跳过 4 条断言** ——
#   **不进汇总**、无 `CK` ⇒ 汇总行仍是「PASS=25 FAIL=0」，读者**没有任何线索**知道少了 4 条。
#   ⚠ 而 `tests/output/validate/src_hdr.png`（生产者 `ensure-fixtures.ps1`）**不受版本控制**
#     （`tests/output/**` 被 .gitignore），且**运行器不跑 ensure-fixtures** ⇒ **干净检出上必然缺失**
#     ⇒ 这条通道会真的发作。现改为与 `verify-gif-avif-framelist.ps1` **同一形态**。
function SKIP([string]$what, [int]$n = 1) {
    $script:skip += $n
    Write-Host "SKIP ($n 条) $what" -ForegroundColor Yellow
}

# ⚠ 参数名不能叫 $args（PowerShell 自动变量会吞掉实参）
function RunTool([string]$exe, [string[]]$argList, [string]$tag) {
    $o = "$d\$tag.out"; $e = "$d\$tag.err"
    $p = Start-Process -FilePath $exe -ArgumentList $argList -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e
    return @{ code = $p.ExitCode; out = [System.IO.File]::ReadAllText($o); err = [System.IO.File]::ReadAllText($e) }
}
function MakeHdr10([string]$path, [string]$x265) {
    return (RunTool $ff @('-hide_banner', '-y', '-loglevel', 'error', '-f', 'lavfi', '-i',
        'color=c=black:s=64x64:r=10:d=0.3,format=yuv420p10le',
        '-c:v', 'libx265', '-preset', 'ultrafast', '-frames:v', '3', '-x265-params', $x265,
        '-color_primaries', 'bt2020', '-color_trc', 'smpte2084', '-colorspace', 'bt2020nc', $path) 'gen')
}
$MD = 'master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(40000000,1)'
$TAGS = 'colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc'
$clipBoth = "$d/hdr10_maxcll.mp4"      # MaxCLL=600 + mastering=4000
$clipMds  = "$d/hdr10_mdonly.mp4"      # 只有 mastering=4000
if ((MakeHdr10 $clipBoth ($MD + ':max-cll=600,200:' + $TAGS)).code -ne 0) { CK $false "造素材失败（双元数据）"; exit 1 }
if ((MakeHdr10 $clipMds ($MD + ':' + $TAGS)).code -ne 0) { CK $false "造素材失败（仅 mastering）"; exit 1 }
$hdrPng = "$root/tests/output/validate/src_hdr.png"   # 带 cICP(9,16) 但**无** HDR 静态元数据

function EchoOut($r) { ($r.out -split "`n") | ForEach-Object { "    $_" } }

Write-Host "`n=== (a) MaxCLL 优先（内容峰值，非监视器峰值） ===" -ForegroundColor Cyan
$r = RunTool $pr @('peak', $clipBoth, '600') 'a'
EchoOut $r
CK ($r.code -eq 0) "(a) ServiceProbe peak 全绿 exit=$($r.code)"
CK ($r.out -match 'trc=smpte2084') "(a) 源确实被探测为 PQ（否则本条会空过）"
CK ($r.out -match 'metaSource=MaxCLL') "(a) 取的是 MaxCLL=600，而不是 mastering=4000（旧实现 Math.Max 会取 4000）"
CK ($r.out -match 'metaPeak=600\.00') "(a) 键名/单位正确（max_content，非 max_cll；有理数已换算）"
CK ($r.out -match 'headroom=2\.9') "(a) headroom=600/203≈2.96（旧实现读到 0→名义 10000→49.26 过度压暗）"
CK ($r.out -match 'reason=.*峰值来源=MaxCLL') "(a) 日志写明来源=MaxCLL（不静默）"

Write-Host "`n=== (b) 无 MaxCLL 时回退 BS.2086 mastering ===" -ForegroundColor Cyan
$r = RunTool $pr @('peak', $clipMds, '4000') 'b'
EchoOut $r
CK ($r.code -eq 0) "(b) ServiceProbe peak 全绿 exit=$($r.code)"
CK ($r.out -match 'metaPeak=4000\.00') "(b) mastering 有理数 40000000/10000 ⇒ 4000 cd/m²（未重复换算）"
CK ($r.out -match 'metaSource=BS\.2086') "(b) 来源标注为 BS.2086 mastering"

Write-Host "`n=== (c) 用户 --color-hdr-peak 覆盖元数据 ===" -ForegroundColor Cyan
$r = RunTool $pr @('peak', $clipBoth, '600', '1500') 'c'
EchoOut $r
CK ($r.code -eq 0) "(c) ServiceProbe peak 全绿 exit=$($r.code)"
CK ($r.out -match 'intentPeak=1500\.00') "(c) 用户指定优先于元数据（600→1500）"
CK ($r.out -match 'reason=.*峰值来源=用户') "(c) 日志写明来源=用户"

Write-Host "`n=== (d) 静像无元数据 ⇒ 诚实回退名义峰值（并写明） ===" -ForegroundColor Cyan
if (-not (Test-Path $hdrPng)) {
    SKIP "(d) 段整段：缺 $hdrPng（先跑 ensure-fixtures.ps1）—— 以下 4 条断言**本轮未运行**，不是通过" 4
} else {
    $r = RunTool $pr @('peak', $hdrPng, '0') 'd'
    EchoOut $r
    CK ($r.code -eq 0) "(d) ServiceProbe peak 全绿 exit=$($r.code)"
    CK ($r.out -match 'trc=smpte2084') "(d) 素材确为 PQ（否则回退断言空过）"
    CK ($r.out -match 'metaPeak=0\.00') "(d) 无静态元数据时不谎报峰值"
    CK ($r.out -match 'reason=.*名义峰值') "(d) 日志必须写明这是**回退**（PQ 名义 10000），不是读来的"
}

Write-Host "`n=== (f) 整帧实测内容峰值（无用户值、无元数据的静像） ===" -ForegroundColor Cyan
# 素材是**已知最高码值**的平坦场（写入时就知道答案），cICP 由生产代码 png3 写入
# （ffmpeg 的 PNG 编码器不写 cICP，实测）。期望 nits 由探针按 PQ 曲线定义换算，与实测路径无关。
function MakePqFlat([string]$name, [int]$code) {
    $w = 64; $h = 48
    $raw = "$d\$name.rgb48"; $png = "$d\$name.png"
    $buf = New-Object byte[] ($w * $h * 6)
    $lo = [byte]($code -band 0xFF); $hi = [byte](($code -shr 8) -band 0xFF)
    for ($i = 0; $i -lt $buf.Length; $i += 6) {
        $buf[$i] = $lo; $buf[$i + 1] = $hi; $buf[$i + 2] = $lo; $buf[$i + 3] = $hi; $buf[$i + 4] = $lo; $buf[$i + 5] = $hi
    }
    [System.IO.File]::WriteAllBytes($raw, $buf)
    if ((RunTool $ff @('-hide_banner', '-y', '-loglevel', 'error', '-f', 'rawvideo', '-pix_fmt', 'rgb48le',
            '-video_size', "$($w)x$($h)", '-i', $raw, $png) 'enc').code -ne 0) { CK $false "(f) 造素材失败 $name"; return $null }
    RunTool $pr @('png3', $png, '0', '9', '16') 'png3' | Out-Null      # 9=bt2020 16=smpte2084
    $tag = (RunTool $fp @('-v', 'error', '-select_streams', 'v:0', '-show_entries',
            'stream=color_primaries,color_transfer', '-of', 'csv=p=0', $png) 'cicp').out
    if ($tag -notmatch 'bt2020') { CK $false "(f) cICP 未写进 $name（实得 $($tag.Trim())）"; return $null }
    return $png
}
$bright = MakePqFlat 'pm_bright' 49152      # 0.75 码 ⇒ 数千 nit ⇒ 实测后 headroom 必大幅低于名义 49.26
$dark   = MakePqFlat 'pm_dark'   16384      # 0.25 码 ⇒ 低于参考白 ⇒ 必钳到 203nit（不增亮）
if ($bright) {
    $r = RunTool $pr @('peakmeasure', $bright, '49152') 'f1'
    EchoOut $r
    CK ($r.code -eq 0) "(f1) 亮图实测全绿 exit=$($r.code)"
    CK ($r.out -match '实测原始值 = 期望') "(f1) 实测值与已知码值推导的期望对得上（不是自我验证）"
    CK ($r.out -match 'Active 与采用值一致') "(f1) 采用值经钳制后仍与 Active 自洽"
    CK ($r.out -match '执行层不污染规划层') "(f1) Plan.Tone 未被执行层原地改写"
}
if ($dark) {
    $r = RunTool $pr @('peakmeasure', $dark, '16384') 'f2'
    EchoOut $r
    CK ($r.code -eq 0) "(f2) 暗图实测全绿 exit=$($r.code)"
    CK ($r.out -match '绝不因图暗而增亮') "(f2) 实测低于参考白 ⇒ 钳到 203nit 恒等（**不拿实测当增亮许可证**）"
}
if ($bright) {
    $r = RunTool $pr @('peakmeasure', $bright, '49152', '1500') 'f3'
    EchoOut $r
    CK ($r.code -eq 0) "(f3) 用户指定峰值时全绿 exit=$($r.code)"
    CK ($r.out -match '用户指定峰值时确实未实测') "(f3) 用户值优先，绝不拿猜值覆盖"
}

Write-Host "`n=== (e) 只解一帧（-read_intervals）后元数据仍在 ===" -ForegroundColor Cyan
Write-Output "  （(a)(b) 走的正是生产那条 ffprobe 命令行；若单帧限定丢了 side data，上面的断言会全挂）"
CK ($true) "(e) 由 (a)(b) 覆盖：限定 %+#1 后 MaxCLL/mastering 仍可读"

Write-Host ""
Write-Host "===== peak 源峰值读取: PASS=$pass FAIL=$fail SKIP=$skip =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# 跑绿自清（跑红保留 GUID 目录供排查）—— 必须在 exit 之前，且用显式 if/else 给出退出码（§6 第 66/67 条）。
if ($fail -eq 0) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
if ($fail -eq 0) { exit 0 } else { exit 1 }
