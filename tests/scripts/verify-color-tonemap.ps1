# verify-color-tonemap.ps1 —— 色调映射算子与外部参照（ffmpeg vf_tonemap）对拍
# 为什么必须对拍：常数/归一化若凭记忆写，验证会变成"自己的公式等于自己"（本项目吃过多次）。
# ffmpeg 是**独立参照**：同一批线性输入 → 两路输出比 PSNR / 最大偏差。
# 覆盖：Hable、Mobius（两者公式均取自 ffmpeg vf_tonemap.c 源码）。
#   BT.2390 未实现（标准公式原文未取到）→ 本脚本显式 SKIP，而不是给个绿灯。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$pr  = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $pr)) { $pr = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $pr + $(if ($pr -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
if (-not (Test-Path $pr)) { Write-Host "先构建 ServiceProbe（Release 或 Debug）" -ForegroundColor Red; exit 1 }
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$d   = "$root/tests/output/validate/tmv_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }

$peakNits = 1000.0; $sdrWhite = 203.0; $hr = $peakNits / $sdrWhite; $N = 512
Write-Output ("参数: peak={0}nit white={1}nit headroom(=ffmpeg peak)={2:F9} 样本={3}" -f $peakNits, $sdrWhite, $hr, $N)

function CheckMode([string]$name, [string]$filter) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    # ① 我方曲线
    $null = Exec $pr "tonemapcurve `"$d/ours_$name.csv`" $name $peakNits $N $sdrWhite"
    if (-not (Test-Path "$d/ours_$name.csv")) { CK $false "$name 我方曲线导出失败"; return }
    $ours = @(Get-Content "$d/ours_$name.csv" | ForEach-Object { $p = $_ -split ','; [pscustomobject]@{ x = [double]$p[0]; y = [double]$p[1] } })
    CK ($ours.Count -eq $N) "$name 我方曲线样本数 = $N"

    # ② ffmpeg 参照：同一条线性斜坡（1×N gbrpf32le，R=G=B=x）。desat=0 ⇒ 只比曲线本身。
    $e = "(Y/$($N-1))*$hr"
    #    ⚠ 必须写 ${N}："$N:d=..." 会被 PowerShell 当成作用域限定符 $N:d 而吞掉值（实测踩过）
    (Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"nullsrc=s=1x${N}:d=0.04,format=gbrpf32le`" -vf `"geq=red_expr='$e':green_expr='$e':blue_expr='$e',$filter`:desat=0,format=gbrpf32le`" -frames:v 1 -f rawvideo `"$d/ref_$name.bin`"") -split "`r?`n" |
      Select-Object -First 2 | ForEach-Object { "    ! $($_)" }
    if (-not (Test-Path "$d/ref_$name.bin")) { CK $false "$name ffmpeg 参照生成失败"; return }
    $bytes = (Get-Item "$d/ref_$name.bin").Length
    CK ($bytes -ge $N * 12) "$name 参照输出 $bytes B（≥ $($N*12)）"

    $b = [System.IO.File]::ReadAllBytes("$d/ref_$name.bin")
    $offR = 2 * $N * 4                                       # gbrp 平面序 G,B,R
    $ref = New-Object 'double[]' $N
    for ($i = 0; $i -lt $N; $i++) { $ref[$i] = [BitConverter]::ToSingle($b, $offR + $i * 4) }

    # ③ 比较（域 [0,1]）
    $sum = 0.0; $max = 0.0; $maxAt = 0
    for ($i = 0; $i -lt $N; $i++) {
        $dx = [Math]::Abs($ref[$i] - $ours[$i].y); $sum += $dx * $dx
        if ($dx -gt $max) { $max = $dx; $maxAt = $i }
    }
    $rms = [Math]::Sqrt($sum / $N)
    $psnr = if ($rms -gt 0) { 10 * [Math]::Log10(1.0 / ($rms * $rms)) } else { 99 }
    Write-Output ("    结果: RMS={0:E3} 最大|Δ|={1:E3} @idx={2} (x={3:F4})" -f $rms, $max, $maxAt, $ours[$maxAt].x)
    CK ($psnr -ge 40) ("{0} 与 ffmpeg 参照 PSNR ≥40dB（实 {1:F1}dB）" -f $name, $psnr)
    CK ($max -le 1e-3) ("{0} 与 ffmpeg 参照最大偏差 ≤1e-3（实 {1:E2}）" -f $name, $max)

    # ④ 结构性质
    CK ([Math]::Abs($ours[0].y) -lt 1e-6) ("{0} f(0)=0（实 {1:E2}）" -f $name, $ours[0].y)
    CK ([Math]::Abs($ours[$N-1].y - 1.0) -lt 2e-3) ("{0} f(peak)≈1.0（实 {1:F6}）" -f $name, $ours[$N-1].y)
    $mono = $true; $at = -1
    for ($i = 1; $i -lt $N; $i++) { if ($ours[$i].y -lt $ours[$i-1].y - 1e-9) { $mono = $false; $at = $i; break } }
    CK $mono ("{0} 曲线单调不降（首个下降 idx={1}）" -f $name, $at)
    # 亚拐点直通（mobius 的 j=0.3 语义；hable 无此段 ⇒ 只对 Mobius 断言）
    if ($name -eq "Mobius") {
        $lin = 0; $badl = 0
        for ($i = 0; $i -lt $N; $i++) { if ($ours[$i].x -le 0.3) { $lin++; if ([Math]::Abs($ours[$i].y - $ours[$i].x) -gt 1e-9) { $badl++ } } }
        CK ($badl -eq 0 -and $lin -gt 10) ("Mobius 拐点以下 1:1 直通（检查 $lin 点，异常 $badl）")
    }
}

CheckMode "Hable"  "tonemap=hable:peak=$hr"
CheckMode "Mobius" "tonemap=mobius:param=0.3:peak=$hr"

Write-Host "`n=== SKIP（未实现，不给绿灯）===" -ForegroundColor Yellow
Write-Output "  SKIP BT.2390：ITU-R Report BT.2390 公式原文尚未取得（只查到描述：带线性段的 Hermite 样条滚降、拐点偏移规范值 0.5）。"
Write-Output "       引擎里 Bt2390 枚举存在但 Apply() 会原样返回并被策略层当作'未就绪'，绝不静默降级成别的曲线。"

Write-Host ""
Write-Host "===== tonemap 对拍: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 运行级隔离配套：结尾自清（跑绿才清；跑红**保留**供排查 —— 那是证据，不是残留）。⚠ 必须在 `exit` **之前**。
if ($fail -eq 0) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
exit $(if ($fail -eq 0) { 0 } else { 1 })
