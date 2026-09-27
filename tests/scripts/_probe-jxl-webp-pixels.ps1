# _probe-jxl-webp-pixels.ps1 —— 诊断：P3 源经「主分支」与「JXL 输入专用通路」两条路产出 webp 时，
# 像素是否落在**同一套编码值**上（决定我们后置嵌入的 P3 ICC 是否与像素一致）。
#
# 为什么单独探这个：P7a-3 把 ICC 后置接给了所有专用通路，但专用通路的命令行**不是 BuildArguments 建的**
# （djxl 解码 → ffmpeg 只负责封装/编码）。若 djxl 已把 P3 像素转成 sRGB，我们再盖一张 P3 ICC
# 就是"在无标注 sRGB 像素上贴广色域标签"——比不贴更错。判据只能是量像素，不能靠推理。
#
# ✅ 2026-09-14 实测结论（上面那个担忧已排除）：
#   • djxl **不改像素**：src_p3.jxl → ppm/png 的角像素 = 116,20,244 = 源 PNG 完全一致
#     ⇒ 喂给 ffmpeg 的就是真 P3 编码值，给它后置嵌 P3 ICC **语义正确**（不是无标注 sRGB）。
#   • 但同一份 P3 源走两条路，webp 角像素不同：主分支 116,16,237、专用通路 94,22,238。
#     已另用 `_probe-icc-affects-decode.ps1` 排除“ICC 在场改变测量值”这种假象 ⇒ 差异是真的。
#     命令行只差三处：管道多 `-color_range pc`、多 `-f ppm_pipe -i -`、**没有 `-pix_fmt`**，
#     特征（R 动 22、G 动 2、B 动 6）像 RGB↔YUV 矩阵/范围往返不一致。归入待查项 **P7d**，
#     本轮不动它（先保证不把未定性的东西当已修）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*\Debug\*') { " (Debug fallback)" } else { " (Release)" }))
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"; $ex = "$root/publish/PLAN/exiftool/exiftool.exe"
$djxl = (Get-ChildItem "$root/publish/PLAN/jxl" -Recurse -Filter djxl.exe -EA SilentlyContinue | Select-Object -First 1).FullName
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$out = "$root/tests/output/validate/jxl-webp-pixels"
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$p3 = "$root/tests/output/results/color/a2_srgb_p3.png"
if (-not (Test-Path $p3)) { "缺素材 $p3（先跑 run-color-regress 或 ensure-fixtures）"; exit 1 }

function Red([string]$f) {
    # 取左上角 1px 的存储值（不经任何 ICC 转换：解码后直接 crop + 定标到 rgb24）
    # ⚠ 必须显式 `-f rawvideo`：裸 .raw 后缀 ffmpeg 认不出 muxer（实测报 Unable to choose an output format）。
    # ⚠ 滤镜顺序必须是 `format=rgb24,crop=…`：先 crop 到 1x1 再从 yuv420p 转 rgb 会输出 **0 字节**（色度子采样无法裁到奇数尺寸）。
    $raw = "$out/px.raw"
    Start-Process -FilePath $ff -ArgumentList @('-y', '-hide_banner', '-loglevel', 'error', '-i', "`"$f`"",
        '-vf', 'format=rgb24,crop=1:1:0:0', '-frames:v', '1', '-f', 'rawvideo', "`"$raw`"") -NoNewWindow -Wait | Out-Null
    if (-not (Test-Path $raw)) { return $null }
    $b = [System.IO.File]::ReadAllBytes($raw); Remove-Item $raw -Force -ErrorAction SilentlyContinue
    if ($b.Length -lt 3) { return $null }
    return "$($b[0]),$($b[1]),$($b[2])"
}
function IccInfo([string]$f) {
    $o = "$out/_icc.txt"
    Start-Process -FilePath $ex -ArgumentList @('-s', '-a', '-ICC_Profile:RedColourantXYZ', '-ICC_Profile:ProfileClass', "`"$f`"") `
        -NoNewWindow -Wait -RedirectStandardOutput $o | Out-Null
    return (([System.IO.File]::ReadAllText($o)) -replace '\r?\n', ' ').Trim()
}
function RunCli([string]$tag, [string]$inFile, [string[]]$extra) {
    $a = @('--headless', '-i', "`"$inFile`"", '--format', 'webp', '--output', "`"$out/$tag`"", '--log-level', 'Debug') + $extra
    Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow `
        -RedirectStandardOutput "$out/$tag.o" -RedirectStandardError "$out/$tag.e" | Out-Null
    $log = [System.IO.File]::ReadAllText("$out/$tag.o") + [System.IO.File]::ReadAllText("$out/$tag.e")
    $f = Get-ChildItem -Recurse "$out/$tag" -Include *.webp -EA SilentlyContinue | Select-Object -First 1
    return @{ log = $log; file = $f }
}

"=== 源 PNG（Display P3，红应为 255,0,0 的存储值）==="
"  a2_srgb_p3.png     red=$(Red $p3)  icc=$(IccInfo $p3)"

"=== ① 主分支：p3.png → webp ==="
$r1 = RunCli 'main' $p3 @()
if ($r1.file) { "  $($r1.file.Name)  red=$(Red $r1.file.FullName)  icc=$(IccInfo $r1.file.FullName)" }
else { "  未产出" }
"  日志关键行：$((($r1.log -split "`n") | Select-String -Pattern '\[色彩\]|iccgen|引擎' | ForEach-Object { $_.Line.Trim() }) -join ' / ')"

"=== ② 专用通路：p3.jxl（libjxl 编码，保留 P3 ICC）→ webp ==="
$jxl = "$out/src_p3.jxl"
Start-Process -FilePath $ff -ArgumentList @('-y', '-hide_banner', '-loglevel', 'error', '-i', "`"$p3`"",
    '-c:v', 'libjxl', '-distance', '0', "`"$jxl`"") -NoNewWindow -Wait -RedirectStandardError "$out/_jxl.e" | Out-Null
if (-not (Test-Path $jxl)) { "  无法生成 .jxl（本构建无 libjxl？）"; exit 1 }
"  src_p3.jxl          icc=$(IccInfo $jxl)"
if ($djxl) {
    $dp = "$out/djxl_out.png"
    Start-Process -FilePath $djxl -ArgumentList @("`"$jxl`"", "`"$dp`"") -NoNewWindow -Wait | Out-Null
    if (Test-Path $dp) { "  djxl→png           red=$(Red $dp)  icc=$(IccInfo $dp)" }
    $dppm = "$out/djxl_out.ppm"
    Start-Process -FilePath $djxl -ArgumentList @("`"$jxl`"", "--output_format=ppm", "`"$dppm`"") -NoNewWindow -Wait | Out-Null
    if (Test-Path $dppm) { "  djxl→ppm 存在（PPM 无 ICC 槽位 ⇒ 交给 ffmpeg 的帧没有任何色彩描述）" }
}
$r2 = RunCli 'special' $jxl @()
if ($r2.file) { "  webp               red=$(Red $r2.file.FullName)  icc=$(IccInfo $r2.file.FullName)" }
else { "  未产出" }
"  日志关键行：$((($r2.log -split "`n") | Select-String -Pattern '\[色彩\]|\[色彩后置\]|djxl|已嵌入' | ForEach-Object { $_.Line.Trim() }) -join ' / ')"

"=== 结论判据 ==="
if ($r1.file -and $r2.file) {
    $a = Red $r1.file.FullName; $b = Red $r2.file.FullName
    if (-not $a -or -not $b) { "  ⚠ 无法比较（主分支=$a 专用通路=$b）——**不得当通过**" }
    elseif ($a -eq $b) { "  两条路像素一致（$a）⇒ 同一张 P3 ICC 对两者都成立" }
    else {
        # 不一致时区分两种成因：ICC 语义错（严重，得回滚后置）还是像素往返漂移（待查 P7d）。
        # 后者用 djxl 自解的像素值当仲裁：它等于源 ⇒ 我们嵌的 ICC 没错，只是编码往返漂了。
        $dj = $null
        if (Test-Path "$out/djxl_out.png") { $dj = Red "$out/djxl_out.png" }
        $srcPx = Red $p3
        if ($dj -and $dj -eq $srcPx) {
            "  ICC 语义正确：djxl 解出像素 $dj == 源 $srcPx ⇒ 后置嵌 P3 ICC 不是贴错标签"
            "  ⚠ 但两条路 webp 像素不同（主分支=$a 专用通路=$b）⇒ 已记为待查项 P7d（矩阵/范围往返）"
        } else {
            "  ⚠ 像素不一致（主分支=$a 专用通路=$b）且 djxl 解出=$(dj) ≠ 源=$srcPx ⇒ 源头已改像素，后置 ICC 不能照搬！"
        }
    }
} else { "  ⚠ 缺产物，无从比较" }
