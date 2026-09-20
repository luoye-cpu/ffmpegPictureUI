# _probe-p7d-bisect.ps1 —— 定位 P7d：同一份 Display P3 内容，主分支 webp 角像素 116,16,237，
# JXL 专用通路 webp 94,22,238。已排除「ICC 在场改变测量值」与「-pix_fmt 是唯一原因」（加了也只到 97）。
#
# 方法：以主分支 CLI 的真实命令为模板，**每次只改一个开关**，量同一像素。
# 只报读数，不下结论——最后一行给出"哪些开关被证明无关"，剩下的才是嫌疑集。
#
# ✅ 已完成定性（2026-09-14）：唯一变量是管道命令缺 `-pix_fmt`（stdin 探不到位深 ⇒ 整条被静默跳过）。
#    已修（`BuildArguments(..., probeInputPath)`），回归锁改成 `verify-decision-delivery.ps1` 的 ⑨；
#    结论与全部数字见 `docs/TESTING.md` 六、注意事项第 8/9 条。
#    本脚本保留作“如何隔离这类问题”的可执行样本；其中 **B7（`-colorspace gbr`）注定失败**
#    ——本构建 ppm 解码器不接受该常数（报 Undefined constant），属预期，不是回归。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"; $fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$ex = "$root/publish/PLAN/exiftool/exiftool.exe"
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$out = "$root/tests/output/validate/p7d"
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

$pg = "$root/tests/output/results/color/a2_srgb_p3.png"
if (-not (Test-Path $pg)) { "缺素材 $pg"; exit 1 }

function Px([string]$f) {
    # ⚠ 顺序必须 format 在 crop 前；裸 .raw 必须显式 -f rawvideo（两条都是实测踩坑，见 docs/TESTING 六.7）
    $raw = "$out/_px.raw"
    Start-Process -FilePath $ff -ArgumentList @('-y', '-hide_banner', '-loglevel', 'error', '-i', "`"$f`"",
        '-vf', 'format=rgb24,crop=1:1:0:0', '-frames:v', '1', '-f', 'rawvideo', "`"$raw`"") -NoNewWindow -Wait | Out-Null
    if (-not (Test-Path $raw)) { return "<解不出>" }
    $b = [System.IO.File]::ReadAllBytes($raw); Remove-Item $raw -Force -ErrorAction SilentlyContinue
    if ($b.Length -lt 3) { return "<空>" }
    return "$($b[0]),$($b[1]),$($b[2])"
}
function Enc([string]$tag, [string[]]$a) {
    $o = "$out/$tag.webp"
    Start-Process -FilePath $ff -ArgumentList ($a + @("`"$o`"")) -NoNewWindow -Wait `
        -RedirectStandardError "$out/_e_$tag.txt" | Out-Null
    if (-not (Test-Path $o)) {
        $err = ((Get-Content "$out/_e_$tag.txt" -Raw) -replace '\s+', ' ')
        "{0,-26} 失败：{1}" -f $tag, $err.Substring(0, [Math]::Min(90, $err.Length)); return
    }
    "{0,-26} px={1,-14} tags={2}" -f $tag, (Px $o), ((((Exec $fp "-v error -select_streams v:0 -show_entries stream=pix_fmt,color_range,color_space -of csv=p=0 `"$o`"") -split "`r?`n")) -join ',')
}
# 主分支 CLI 的真实命令行（从 [cmd] 日志抄录）
$SRC = @('-y', '-hide_banner', '-loglevel', 'error', '-color_primaries', 'smpte432', '-color_trc', 'iec61966-2-1')
$IN = @('-i', $pg)
$POSTFULL = @('-threads', '0', '-q:v', '75', '-pix_fmt', 'yuv420p', '-map_metadata', '-1', '-map_chapters', '-1', '-vf', 'iccgen')

"源 PNG 像素：$(Px $pg)   tags=$((((Exec $fp "-v error -select_streams v:0 -show_entries stream=pix_fmt,color_range,color_space,color_primaries -of csv=p=0 `"$pg`"") -split "`r?`n")) -join ',')"
""
"=== A. 照抄主分支 CLI 的各开关组合（输入 PNG）==="
Enc 'A0_照抄' ($SRC + $IN + $POSTFULL)
Enc 'A1_去map_metadata' ($SRC + $IN + @('-threads','0','-q:v','75','-pix_fmt','yuv420p','-map_chapters','-1','-vf','iccgen'))
Enc 'A2_去iccgen' ($SRC + $IN + @('-threads','0','-q:v','75','-pix_fmt','yuv420p','-map_metadata','-1','-map_chapters','-1'))
Enc 'A3_去threads' ($SRC + $IN + @('-q:v','75','-pix_fmt','yuv420p','-map_metadata','-1','-map_chapters','-1','-vf','iccgen'))
Enc 'A4_全去只留q-pixfmt-iccgen' (@('-y','-hide_banner','-loglevel','error') + @('-color_primaries','smpte432','-color_trc','iec61966-2-1') + $IN + @('-q:v','75','-pix_fmt','yuv420p','-vf','iccgen'))
Enc 'A5_无输入色彩覆盖' (@('-y','-hide_banner','-loglevel','error') + $IN + $POSTFULL)
# ★ 上一轮“手敲等价命令得 97”与 CLI 的 116 对不上，而 A0-A5 全等于 116 ⇒ 差异必在上一轮多写的
#    `-c:v libwebp` 上（CLI 不写 -c:v，webp muxer 自己选默认编码器）。本行就是验这个假设。
Enc 'A6b_照抄但显式libwebp' ($SRC + @('-i',$pg,'-c:v','libwebp','-threads','0','-q:v','75','-pix_fmt','yuv420p','-map_metadata','-1','-map_chapters','-1','-vf','iccgen'))
Enc 'A6c_照抄但显式libwebp_anim' ($SRC + @('-i',$pg,'-c:v','libwebp_anim','-threads','0','-q:v','75','-pix_fmt','yuv420p','-map_metadata','-1','-map_chapters','-1','-vf','iccgen'))

""
"=== B. 换成 PPM 输入（= 专用通路喂给 ffmpeg 的东西），逐个加回开关 ==="
$ppm = "$out/src.ppm"
$djxl = (Get-ChildItem "$root/publish/PLAN/jxl" -Recurse -Filter djxl.exe -EA SilentlyContinue | Select-Object -First 1).FullName
$jxl = "$root/tests/output/validate/jxl-webp-pixels/src_p3.jxl"
if (-not (Test-Path $jxl)) {
    Start-Process -FilePath $ff -ArgumentList @('-y','-hide_banner','-loglevel','error','-i',$pg,'-c:v','libjxl','-distance','0',"`"$jxl`"") -NoNewWindow -Wait | Out-Null
}
if (Test-Path $djxl) { Start-Process -FilePath $djxl -ArgumentList @("`"$jxl`"", "--output_format=ppm", "`"$ppm`"") -NoNewWindow -Wait | Out-Null }
if (-not (Test-Path $ppm)) { "  无法生成 PPM（缺 djxl 或 libjxl），B 组跳过"; }
else {
    "PPM 像素：$(Px $ppm)（应 == 源）"
    "PPM 帧属性：" + ((((Exec $fp "-v error -select_streams v:0 -show_entries stream=pix_fmt,color_range,color_space,color_primaries -of csv=p=0 `"$ppm`"") -split "`r?`n")) -join ',')
    $P = @('-color_primaries','smpte432','-color_trc','iec61966-2-1')
    # 本构建无 `ppm_image` demuxer（实测 Unknown input format），喂文件靠扩展名即可
    Enc 'B0_PPM裸' (@('-y','-hide_banner','-loglevel','error','-i',$ppm) + @('-q:v','75'))
    Enc 'B1_+pixfmt' (@('-y','-hide_banner','-loglevel','error','-i',$ppm) + @('-q:v','75','-pix_fmt','yuv420p'))
    Enc 'B2_+range_pc' (@('-y','-hide_banner','-loglevel','error','-color_range','pc','-i',$ppm) + @('-q:v','75','-pix_fmt','yuv420p'))
    Enc 'B3_+pctag' (@('-y','-hide_banner','-loglevel','error') + $P + @('-color_range','pc','-i',$ppm) + @('-q:v','75','-pix_fmt','yuv420p'))
    Enc 'B4_+iccgen' (@('-y','-hide_banner','-loglevel','error') + $P + @('-color_range','pc','-i',$ppm) + @('-q:v','75','-pix_fmt','yuv420p','-vf','iccgen'))
    Enc 'B5_照抄主分支(PPM输入)' (@('-y','-hide_banner','-loglevel','error') + $P + @('-color_range','pc','-i',$ppm) + @('-threads','0','-q:v','75','-pix_fmt','yuv420p','-map_metadata','-1','-map_chapters','-1','-vf','iccgen'))
    # 候选修法：把“输入是 full-range RGB”从声明改成**滤镜里强制**（不赌 demuxer 传没传到位）
    Enc 'B6_滤镜强制full范围' (@('-y','-hide_banner','-loglevel','error') + $P + @('-i',$ppm) + @('-threads','0','-q:v','75','-pix_fmt','yuv420p','-vf','scale=in_range=full:out_range=full:out_color_matrix=bt709'))
    Enc 'B7_声明gbr矩阵' (@('-y','-hide_banner','-loglevel','error') + $P + @('-colorspace','gbr','-color_range','pc','-i',$ppm) + @('-threads','0','-q:v','75','-pix_fmt','yuv420p'))
}

""
"=== C. 两条 CLI 真实产物（基准值，用于核对上面哪个组合与之一致）==="
foreach ($t in @('main', 'special')) {
    $inFile = if ($t -eq 'main') { $pg } else { $jxl }
    Start-Process -FilePath $exe -ArgumentList @('--headless', '-i', "`"$inFile`"", '--format', 'webp',
        '--output', "`"$out/cli_$t`"", '--log-level', 'Debug') -NoNewWindow -Wait `
        -RedirectStandardOutput "$out/cli_$t.o" -RedirectStandardError "$out/cli_$t.e" | Out-Null
    $f = Get-ChildItem -Recurse "$out/cli_$t" -Include *.webp -EA SilentlyContinue | Select-Object -First 1
    if (-not $f) { "cli_$t 未产出"; continue }
    $log = [System.IO.File]::ReadAllText("$out/cli_$t.o")
    $cmd = (($log -split "`n") | Where-Object { $_ -match '\[cmd\]' } | Select-Object -First 1)
    "cli_$t".PadRight(26) + " px=$(Px $f.FullName)"
    "    命令行：$(if ($cmd) { $cmd.Trim() } else { '<日志里没有 [cmd]>' })"
}
""
"读法：与 cli_main 一致的组合 = 主分支真实行为；与 cli_special 一致的 = 专用通路真实行为。"
"两者之间**第一个让读数分叉的开关**就是 P7d 的成因（其余开关已被证明无关）。"
