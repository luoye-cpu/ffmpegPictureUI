# 实证：解除 JPEG 的 primaries 钳制后，Display P3 目标是否真的带上 P3 的 ICC（而不是 sRGB / 无 ICC）
# 对照：同一滤镜链只换 p=bt709 vs p=smpte432，比较嵌入 ICC 的字节与色度原色。
#
# ⚠ **输入自备**（2026-09-17 补，理由同 §6 第 64 条「门禁依赖别脚本的遗留产物」）：
#   此前这里读 `tests/output/gainmap/png3_cicp.png` —— 那是**链外的 `GainMapTestHost`**
#   （`tests/GainMapTestHost/Program.cs:83-136`）产出的**遗留文件** ⇒ 干净 `tests/output` 上本脚本
#   会直接 `exit 1`。而本轮要把它加进 `_run-step3-gates.ps1` 的必跑清单（作为 `validate/jpgp3` 的
#   生产者），**必须自己生成** ⇒ 否则等于把「静默假绿」换成「需要手工前置步骤的红」，代价不对等。
#   生成方式与 GainMapTestHost **逐字一致**（testsrc2 128x96 → rgb48be PNG，再用**生产代码**
#   `PngCicpService` 写 cICP(9,16)+sBIT(10)）；这里**复用 `ServiceProbe png3` 模式**调同一份生产代码，
#   不新增第二套口径。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$ex = "$root/publish/PLAN/exiftool/exiftool.exe"
$d  = "$root/tests/output/validate/jpgp3"
if (Test-Path $d) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $d | Out-Null
$in = "$d/src_cicp.png"
$prExe = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $prExe)) { $prExe = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $prExe + $(if ($prExe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
if (Test-Path $prExe) {
    Start-Process -FilePath $ff -ArgumentList "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=size=128x96:duration=0.1`" -frames:v 1 -update 1 -c:v png -pix_fmt rgb48be `"$in`"" -NoNewWindow -Wait | Out-Null
    # png3 <file> [sbitBits（0=不写）] [primaries transfer] ⇒ 10 = sBIT(10)；9/16 = BT.2020 PQ
    Start-Process -FilePath $prExe -ArgumentList "png3 `"$in`" 10 9 16" -NoNewWindow -Wait | Out-Null
}
if (-not (Test-Path $in)) {
    # 兜底：ServiceProbe 不可用时退回链外遗留素材（有则可用）；两者都没有 ⇒ **明确红**，不静默
    $legacy = "$root/tests/output/gainmap/png3_cicp.png"
    if (Test-Path $legacy) { $in = $legacy; Write-Output "  ⚠ ServiceProbe 不可用，退回链外遗留素材 $legacy" }
    else { Write-Output "缺输入且自备失败（ServiceProbe 存在=$(Test-Path $prExe)、ffmpeg 存在=$(Test-Path $ff)）"; exit 1 }
}

foreach ($p in @(@("bt709", "srgb"), @("smpte432", "p3"))) {
    $vf = "format=yuv444p,tonemap=hable:param=0.5,format=rgb48le,zscale=pin=bt2020:tin=linear:min=gbr:p=$($p[0]):t=iec61966-2-1:m=bt709,iccgen"
    $a = "-y -hide_banner -loglevel error -color_primaries bt2020 -color_trc smpte2084 -i `"$in`" -vf `"$vf`" -q:v 9 -pix_fmt yuvj420p -colorspace bt709 `"$d/$($p[1]).jpg`""
    $pr = Start-Process -FilePath $ff -ArgumentList $a -NoNewWindow -Wait -PassThru -RedirectStandardError "$d/$($p[1]).err"
    Write-Output "  $($p[1]).jpg  ffmpeg exit=$($pr.ExitCode)  $((Get-Item "$d/$($p[1]).jpg" -EA SilentlyContinue).Length)B"
    if ($pr.ExitCode -ne 0) { Get-Content "$d/$($p[1]).err" | Select-Object -First 3 | ForEach-Object { "      ! $_" } }
}

foreach ($f in @("srgb", "p3")) {
    $j = "$d/$f.jpg"
    if (-not (Test-Path $j)) { Write-Output "  $f : 未产出"; continue }
    Start-Process -FilePath $ex -ArgumentList "-q -b -ICC_Profile -w `"$d/%f.icc`" `"$j`"" -NoNewWindow -Wait | Out-Null
    $i = "$d/$f.icc"
    if (Test-Path $i) {
        $h = (Get-FileHash $i -Algorithm MD5).Hash.Substring(0, 8)
        Write-Output "  $f.jpg  ICC = $((Get-Item $i).Length) B  md5=$h"
    } else { Write-Output "  $f.jpg  ⚠ 无 ICC（则 ICC 表达路径未通）" }
}

Write-Output "  --- p3.jpg 的 ICC 色度原色（期望 ≈ P3: R 0.680/0.320  G 0.265/0.690  B 0.150/0.060）---"
Start-Process -FilePath $ex -ArgumentList "-q -S -a -G1 -icc_profile:all `"$d/p3.jpg`"" -NoNewWindow -Wait `
  -RedirectStandardOutput "$d/p3tags.txt" | Out-Null
Get-Content "$d/p3tags.txt" | Select-String -Pattern "Description|Color Space|Device|Colourant|XYZ|Primary|Red|Green|Blue" |
  Select-Object -First 12 | ForEach-Object { "    $($_.Line.Trim())" }
