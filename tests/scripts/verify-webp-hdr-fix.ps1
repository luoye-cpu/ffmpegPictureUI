# WebP + HDR 目标修复验证（Bug1: 8-bit cICP 源误判 SDR; Bug2: -colorspace gbr 被 libwebp 拒绝）
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ffprobe = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$ffmpeg = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$val = "$root/tests/output/validate"
# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   本脚本**无 pass/fail 计数**（诊断型）⇒ 结尾无条件自清。
$out = "$val/webpfix_$([guid]::NewGuid().ToString('N').Substring(0, 8))"

New-Item -ItemType Directory -Force -Path $out | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# 只读 stdout 版（取「工具的值」专用）：ffprobe 的 `-of csv` 字段值走 **stdout**，合并 stderr 会把报错行
#   当值填进 `[META]` 读数。下面读 ffmpeg 的 `YAVG`（signalstats 的 `metadata=print` **只写 stderr**）
#   仍必须用 Exec —— 那是合法的合并取数。
function ExecOut([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return [System.IO.File]::ReadAllText($o)
}

function Run-Case($name, $inFile, $extra) {
    Write-Host "`n===== $name =====" -ForegroundColor Cyan
    Start-Sleep -Seconds 2   # 规避 Defender 对新构建 exe 的瞬时锁
    $dst = "$out/$name"
    # WinExe(GUI 子系统): 必须用 Start-Process -Wait 才会等待异步队列真正结束；
    # -ArgumentList 用单一字符串(非数组)并逐项加引号，避免含空格值(如 "BT.2020 PQ")被拆分。
    $argStr = "--headless -i `"$inFile`" --format webp --output `"$dst`""
    foreach ($e in $extra) { $argStr += " `"$e`"" }
    $so = "$out/$name.stdout.txt"; $se = "$out/$name.stderr.txt"
    $p = Start-Process -FilePath $exe -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput $so -RedirectStandardError $se
    Write-Host "  [EXIT] $($p.ExitCode)"
    Get-Content $so -ErrorAction SilentlyContinue | Select-String -Pattern "队列处理|已完成|失败" | ForEach-Object { Write-Host "  $($_.Line)" }
    Get-Content $se -ErrorAction SilentlyContinue | Where-Object { $_ -ne "" } | ForEach-Object { Write-Host "  [ERR] $_" -ForegroundColor Yellow }
    $webp = Get-ChildItem -Recurse $dst -Filter *.webp -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $webp) { Write-Host "  [RESULT] NO OUTPUT FILE" -ForegroundColor Red; return }
    if ($webp.Length -eq 0) { Write-Host "  [RESULT] 0-BYTE (FAIL)" -ForegroundColor Red; return }
    # 探测输出色彩元数据 + 像素亮度均值
    $meta = ExecOut $ffprobe "-v error -select_streams v:0 -show_entries `"stream=pix_fmt,color_primaries,color_transfer,color_space`" -of csv=p=0 `"$($webp.FullName)`""
    $yavg = (Exec $ffmpeg "-i `"$($webp.FullName)`" -vf `"signalstats,metadata=print:key=lavfi.signalstats.YAVG`" -f null -") -split "`r?`n" | Select-String "YAVG" | Select-Object -Last 1
    Write-Host "  [SIZE] $($webp.Length) bytes" -ForegroundColor Green
    Write-Host "  [META] $meta"
    Write-Host "  [YAVG] $yavg"
}

# 8-bit HDR PNG (cICP bt2020/smpte2084) -> WebP + 显式 HDR 目标（Bug1 场景）
Run-Case "t2b_8bit_hdr_target" "$val/src_hdr8.png" @("--color-space", "BT.2020 PQ")
# 16-bit HDR PNG -> WebP + 显式 HDR 目标（Bug2 场景）
Run-Case "t2_16bit_hdr_target" "$val/src_hdr.png" @("--color-space", "BT.2020 PQ")
# 16-bit HDR PNG -> WebP auto（Bug2 auto 分支）
Run-Case "t3_16bit_auto" "$val/src_hdr.png" @()
# 回归: 普通 SDR PNG -> WebP auto（不应受影响）
Run-Case "t4_sdr_auto" "$val/sdr_plain.png" @()
# 回归: 普通 SDR PNG -> WebP + sRGB 目标
Run-Case "t5_sdr_srgb" "$val/sdr_plain.png" @("--color-space", "sRGB")

Write-Host "`n===== DONE =====" -ForegroundColor Cyan
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

