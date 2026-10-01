# _probe-geometry-single-source-scan.ps1 —— 结构锁：图像宽高探测必须只有 `ImageGeometry` 一份实现
#
# 为什么需要：本缺陷（取向标签 ⇒ coded 尺寸标注旋正像素 ⇒ 产物行错位）之所以能长期存在，
#   不是因为没人写对，而是因为「探宽高」在 `src/` 里有**四份**各自独立的实现
#   （`RawColorPipeline.ProbeSizeAsync`、`GainMapDecoder.ProbeSizeAsync`、
#    `QualityAnalysisService.GetResolutionAsync`、`QueueProcessor.ProbeImageSizeAsync`），
#   全部只读 `ffprobe stream=width,height`（= coded）。⇒ 修一处漏三处，且下一次也会漏。
#   2026-09-30 四处已全部转发 `Services/ImageGeometry.cs`，本锁负责让它**不能再长出第五份**。
#
# 判据（全部只看代码，剥掉整行注释后再匹配 —— 注释里提一句旧写法就能把文本锁喂绿，本轮真踩过）：
#   ① `stream=width,height -of`（只探宽高的旧形态）在 `src/` 代码中出现次数 **必须为 0**；
#   ② `ImageGeometry.cs` 必须仍然同时含 `frame_side_data_list` / `displaymatrix` / `ApplyRotation`
#      —— 否则 ① 会在"唯一实现也被删掉"时假绿；
#   ③ 那四个消费者必须各自仍然引用 `ImageGeometry.DisplaySizeAsync`（防止改成自己写一份）。
#
# ⚠ 汇总行**必须**是 `PASS=n FAIL=m` 形态：运行器有一道「零断言闸」，凡不自我汇总的脚本会被判
#   "一条断言都没跑"而**拒跑**（本锁 2026-09-30 首次进清单就栽在这里 —— 只打 `[lock]` 散文行，
#   整轮报 `script:... (零断言：无 PASS=n 汇总)`）。要么自报汇总，要么进 `$noSelfSummary` 清单
#   （那会动第 14 针的条数锁）⇒ 选前者。
#
# 用法：pwsh -NoProfile -File tests/scripts/_probe-geometry-single-source-scan.ps1
#      退出码 0 = 锁成立；非 0 = 有越界点（逐条点名）。
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pass = 0; $fail = 0
function CK($ok, $msg) {
    if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
    else { $script:fail++; Write-Output "  FAIL $msg" }
}

function CodeLines($path) {
    # 剥掉**整行**注释（`//`、`///`、`/*`、`*`）与空行；不处理行尾注释（那会误伤字符串里的 `//`）
    $out = @()
    foreach ($l in [System.IO.File]::ReadAllLines($path)) {
        $t = $l.TrimStart()
        if ($t.StartsWith('//') -or $t.StartsWith('///') -or $t.StartsWith('/*')) { continue }
        if ($t.StartsWith('*')) { continue }
        $out += $l
    }
    return $out
}

$srcRoot = Join-Path $root 'src/FfmpegGui'
$files = Get-ChildItem -Recurse -Path $srcRoot -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

$bad = @()
$oldTotal = 0
foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length + 1) -replace '\\', '/'
    $code = CodeLines $f.FullName
    $hits = @($code | Select-String -Pattern 'stream=width,height\s+-of' -AllMatches)
    foreach ($h in $hits) {
        $oldTotal++
        $bad += "$rel : $($h.Line.Trim())"
    }
}
foreach ($b in $bad) { Write-Output "  越界: $b" }
CK ($oldTotal -eq 0) "① 旧形态（只探 coded 宽高）在 src/ 代码中命中 $oldTotal 处（必须 0）"

# ② 唯一实现必须仍然"活着"（否则 ① 会在"唯一实现也被删"时假绿）
$geomPath = Join-Path $srcRoot 'Services/ImageGeometry.cs'
$need = @('frame_side_data_list', 'displaymatrix', 'ApplyRotation')
$miss = @()
if (Test-Path $geomPath) {
    $gcode = (CodeLines $geomPath) -join "`n"
    foreach ($n in $need) { if ($gcode -notmatch [regex]::Escape($n)) { $miss += $n } }
} else { $miss += '(文件不存在)' }
CK ($miss.Count -eq 0) "② ImageGeometry.cs 同时含 $($need -join ' / ')（缺：$(if ($miss.Count -eq 0) { '无' } else { $miss -join ',' }))"

# ③ 四个消费者必须仍走唯一实现（逐个点名，防"改成自己写一份"）
$consumers = @(
    'src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs',
    'src/FfmpegGui/Services/GainMapDecoder.cs',
    'src/FfmpegGui/Services/QualityAnalysisService.cs',
    'src/FfmpegGui/Services/QueueProcessor.cs'
)
foreach ($c in $consumers) {
    $full = Join-Path $root $c
    $ok = $false
    if (Test-Path $full) {
        $cc = (CodeLines $full) -join "`n"
        $ok = [bool]($cc -match [regex]::Escape('ImageGeometry.DisplaySizeAsync'))
    }
    CK $ok "③ $c ⇒ 引用唯一实现 ImageGeometry.DisplaySizeAsync"
}

Write-Output ""
Write-Output "===== geometry-single-source: PASS=$pass FAIL=$fail ====="
if ($fail -eq 0) { exit 0 } else { exit 1 }
