# _probe-hdr-peak-oracle.ps1 —— 用 ffprobe 作为 oracle，核对我们读取 HDR 静态元数据的
# 三个前提（凭记忆写死过一次，必须实测）：
#   ①  -show_entries frame_side_data_list 在本构建的 ffprobe 上是否仍然有效（失效即静默返 0）
#   ②  JSON 里 MaxCLL / max_luminance 的**键名**到底是什么
#   ③  max_luminance 的**单位**是 0.0001 cd/m²（BS.2086 原始整数）还是已经换算成 cd/m²
# 用法： pwsh -File tests/scripts/_probe-hdr-peak-oracle.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$ff = "$root\publish\PLAN\ffmpeg-full\ffmpeg.exe"
$fp = "$root\publish\PLAN\ffmpeg-full\ffprobe.exe"
foreach ($f in @($ff, $fp)) { if (-not (Test-Path $f)) { Write-Output "缺失工具: $f"; exit 1 } }

$d = "$root\tests\output\tmp\peak-oracle"
New-Item -ItemType Directory -Force -Path $d | Out-Null
$clip = "$d\hdr10.mp4"

# ⚠ 参数名不能叫 $args —— 那是 PowerShell 的自动变量，会把实参吞掉（实测：ffmpeg 收到空命令行，只打印 usage）
function RunTool([string]$exe, [string[]]$argList, [string]$tag) {
    $o = "$d\$tag.out"; $e = "$d\$tag.err"
    $p = Start-Process -FilePath $exe -ArgumentList $argList -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e
    # 用 ReadAllText：空文件得 ""（Get-Content -Raw 对空文件返回 $null，会让 hashtable 键缺失）
    return @{ code = $p.ExitCode
              out  = [System.IO.File]::ReadAllText($o)
              err  = [System.IO.File]::ReadAllText($e) }
}

# 造一个已知元数据的 HDR10 片段：mastering max = 4000 nits（原始 40000000），MaxCLL = 600 nits
# 两个值故意不同，才能分辨"取哪一个 / 单位对不对"。
Write-Output "=== 生成 $clip ==="
$gen = RunTool $ff @('-hide_banner', '-y', '-f', 'lavfi', '-i',
    'color=c=black:s=64x64:r=10:d=0.3,format=yuv420p10le',
    '-c:v', 'libx265', '-preset', 'ultrafast', '-frames:v', '3',
    '-x265-params', 'master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(40000000,1):max-cll=600,200:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc',
    '-color_primaries', 'bt2020', '-color_trc', 'smpte2084', '-colorspace', 'bt2020nc',
    $clip) 'gen'
Write-Output "  exit=$($gen.code)"
if ($gen.code -ne 0) { Write-Output $gen.err; exit 1 }

# ① 人读输出（第二 oracle：ffmpeg 自己打印的 nits 值）
Write-Output "`n=== ① ffmpeg -i 的人读元数据行 ==="
$info = RunTool $ff @('-hide_banner', '-i', $clip) 'info'
(($info.err -split "`n") | Where-Object { $_ -match 'Mastering|light level|Stream #' } | ForEach-Object { "  $($_.Trim())" })
if (-not $info.out) { }

# ② 我们代码里用的那一条 ffprobe 命令行，原样跑
Write-Output "`n=== ② 生产同款 ffprobe 命令（-show_entries frame_side_data_list） ==="
$probe = RunTool $fp @('-v', 'error', '-select_streams', 'v:0', '-show_frames',
    '-show_entries', 'frame_side_data_list', '-of', 'json', $clip) 'probe'
Write-Output "  exit=$($probe.code)"
if ($probe.err.Length -gt 0) { Write-Output "  stderr: $($probe.err.Trim())" }
$lines = ($probe.out -split "`n") | Where-Object { $_.Trim() }
Write-Output "  输出行数 = $($lines.Count)（前 40 行）"
$lines | Select-Object -First 40 | ForEach-Object { "    $($_.TrimEnd())" }

# ③ 键名与单位的直接结论
Write-Output "`n=== ③ 键名普查（区分大小写 / 下划线） ==="
foreach ($pat in @('max_cll', 'maxcll', 'MaxCLL', 'max_luminance', 'mastering', 'Side data', 'side_data')) {
    $hit = ([regex]::Matches($probe.out, [regex]::Escape($pat))).Count
    Write-Output ("  {0,-18} 命中 {1}" -f $pat, $hit)
}
if ($probe.out -match '"(\w*)"\s*:\s*"?([0-9.]+)"?') { }
$m = [regex]::Matches($probe.out, '"([A-Za-z_0-9]+)"\s*:\s*"?(-?[0-9.]+)"?')
Write-Output "  出现的数值键："
$m | ForEach-Object { "    {0} = {1}" -f $_.Groups[1].Value, $_.Groups[2].Value } | Select-Object -Unique

# ④ 我们代码算出来的结果应当是：MaxCLL=600、mastering max=4000 ⇒ 按 ffmpeg 语义取 600
Write-Output "`n=== ④ 结论（已实测，写入生产代码注释） ==="
Write-Output "  • 内容峰值键名是 max_content（不是 max_cll/MaxCLL）⇒ 旧代码 ExtractJsonNumber('max_cll') 恒为 0。"
Write-Output "  • max_luminance 打印为**有理数字符串** \"40000000/10000\"，其值已是 cd/m²（=4000）⇒ 只取分子再 /10000 属巧合正确。"
Write-Output "  • 语义按 ffmpeg ff_determine_signal_peak：MaxCLL 优先，mastering 仅作 fallback（不是 Math.Max）。"

# ⑤ -read_intervals \"%+#1\"（只解一帧，避免为读元数据而整片解码）在各容器上是否仍能给到 side data
Write-Output "`n=== ⑤ 限定单帧读取的可行性（各素材） ==="
$fx = @($clip, "$root\tests\output\sources\src_8bit.png", "$root\tests\output\sources\src_hdr_hlg.png",
    "$root\tests\output\sources\anim_gif.gif", "$root\tests\output\sources\anim_webp.webp",
    "$root\tests\output\sources\anim_apng.png", "$root\tests\output\sources\src_avif.avif")
foreach ($f in $fx) {
    if (-not (Test-Path $f)) { Write-Output "  跳过（素材缺失）$f"; continue }
    $a = RunTool $fp @('-v', 'error', '-select_streams', 'v:0', '-show_frames',
        '-show_entries', 'frame_side_data_list', '-of', 'json', $f) 'full'
    $b = RunTool $fp @('-v', 'error', '-select_streams', 'v:0', '-read_intervals', '%+#1', '-show_frames',
        '-show_entries', 'frame_side_data_list', '-of', 'json', $f) 'one'
    $fa = ([regex]::Matches($a.out, '\"side_data_type\"')).Count
    $fb = ([regex]::Matches($b.out, '\"side_data_type\"')).Count
    Write-Output ("  {0,-16} 全量: exit={1} side_data={2} 字节={3} | 单帧: exit={4} side_data={5} 字节={6} stderr={7}" -f `
        (Split-Path $f -Leaf), $a.code, $fa, $a.out.Length, $b.code, $fb, $b.out.Length, $b.err.Trim())
}
