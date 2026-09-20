# _probe-icc-affects-decode.ps1 —— 对照：webp 产物里“有没有那张 ICC”会不会改变我量到的像素值
# 起因：手敲的主分支等价命令（无 ICC）量到 97,23,247，而 CLI 主分支产物（被后置嵌了 P3 ICC）量到 116,16,237。
#
# ✅ 实测结论（本探针存在的意义）：**嵌 ICC 不改变读到的像素值**（三组前后逐字节相同），
#    所以跨“ICC 在场/不在场”比较像素是可信口径；而 CLI 主分支 116,16,237 vs 专用通路 94,22,238
#    是**真实差异**（不是测量假象），已归入待查项 P7d（见 docs/TESTING.md “已知红与待查”）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"; $ex = "$root/publish/PLAN/exiftool/exiftool.exe"
$out = "$root/tests/output/validate/jxl-webp-pixels"
function Px([string]$f) {
    $raw = "$out/_px.raw"
    Start-Process -FilePath $ff -ArgumentList @('-y', '-hide_banner', '-loglevel', 'error', '-i', "`"$f`"",
        '-vf', 'format=rgb24,crop=1:1:0:0', '-frames:v', '1', '-f', 'rawvideo', "`"$raw`"") -NoNewWindow -Wait | Out-Null
    if (-not (Test-Path $raw)) { return "<解不出>" }
    $b = [System.IO.File]::ReadAllBytes($raw); Remove-Item $raw -Force -ErrorAction SilentlyContinue
    if ($b.Length -lt 3) { return "<空>" }
    return "$($b[0]),$($b[1]),$($b[2])"
}
$icc = "$out/extracted.icc"
# ⚠ exiftool 的 `-o FILE` 必须**分成两个参数**传（写成 `-o$path` 会被当成无效 TAG 名，实测报 Invalid TAG name）
Start-Process -FilePath $ex -ArgumentList @('-s', '-b', '-ICC_Profile', '-o', $icc, "`"$out/main/a2_srgb_p3.webp`"") -NoNewWindow -Wait | Out-Null
"抽出 CLI 主分支产物内的 ICC：exists=$(Test-Path $icc) size=$((Get-Item $icc -EA SilentlyContinue).Length)"
foreach ($t in @('a_裸', 'c_加pixfmt', 'f_png源')) {
    $src = "$out/v_$t.webp"; if (-not (Test-Path $src)) { continue }
    $w = "$out/v_${t}_icc.webp"; Copy-Item $src $w -Force
    Start-Process -FilePath $ex -ArgumentList @('-overwrite_original', "-ICC_Profile<=$icc", "`"$w`"") -NoNewWindow -Wait | Out-Null
    "{0,-12} 无ICC={1,-14} 嵌同一张P3 ICC={2}" -f $t, (Px $src), (Px $w)
}
"CLI 主分支产物   : $(Px "$out/main/a2_srgb_p3.webp")"
"CLI 专用通路产物 : $(Px "$out/special/src_p3.webp")"
"源 PNG / djxl PPM: $(Px "$root/tests/output/results/color/a2_srgb_p3.png") / $(Px "$out/djxl_out.ppm")"
""
"判读：三组「无 ICC / 嵌同一张 ICC」必须逐字相同（实测已相同），否则本目录的像素对照不可信。"
"     若不同 ⇒ 先修测量口径，再谈「两条路是否一致」（否则会把测量伪影当缺陷去修）。"
