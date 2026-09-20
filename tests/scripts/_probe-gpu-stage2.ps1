# GPU FP32 收益实测（二）：OpenCL 内核吞吐 + 往返税 + FP16 精度差
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$d  = "$root/tests/output/validate/gpu2"
if (Test-Path $d) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $d | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e
  $global:LASTEXITCODE = $p.ExitCode
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}
$W = 3840; $H = 2160; $MP = [Math]::Round($W*$H/1e6, 1)
# 正确语法（实测得出）：`-init_hw_device opencl=<name>:<platform>.<device>`，如 0.0 = 枚举表里的 RTX 5080
#   错例（均报 Invalid platform/device index specification）：dGPU=1 / platform_index=.. / device=0
$dev = "opencl=ocl:0.0"
if ($env:GPUDEV) { $dev = "opencl=ocl:$env:GPUDEV" }

function FF([string]$a2) {
  # Start-Process 不继录 PS 的 Set-Location，必须显式 WorkingDirectory，否则相对路径的 kernel 文件找不到
  $p = Start-Process -FilePath $ff -ArgumentList $a2 -NoNewWindow -Wait -PassThru -WorkingDirectory $root -RedirectStandardError "$d/e.txt"
  return $p.ExitCode
}
$script:res = @{}
function Tim([string]$tag, [string]$a2, [int]$n = 3) {
  $best = [double]::MaxValue; $rc = 0
  for ($k = 0; $k -lt $n; $k++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); $rc = FF $a2; $sw.Stop()
    if ($sw.Elapsed.TotalMilliseconds -lt $best) { $best = $sw.Elapsed.TotalMilliseconds }
  }
  $script:res[$tag] = @{ ms = $best; rc = $rc }
  Write-Output ("  {0,-36} {1,9:F1} ms  exit={2}" -f $tag, $best, $rc)
}

# 素材（与 stage1 同样的 4K 噪声 PNG16）
$null = FF "-y -hide_banner -loglevel error -f lavfi -i `"smptehdbars=s=${W}x${H},noise=alls=60:allf=t+u`" -pix_fmt rgb48le `"$d/src.png`""

Set-Content -Path "$d/k.cl" -Encoding ascii -Value @'
#ifdef cl_khr_fp16
#pragma OPENCL EXTENSION cl_khr_fp16 : enable
#endif
kernel void ident(read_only image2d_t src, write_only image2d_t dst) {
    const sampler_t smp = CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP | CLK_FILTER_NEAREST;
    write_imagef(dst, (int2)(get_global_id(0), get_global_id(1)),
                 read_imagef(src, smp, (int2)(get_global_id(0), get_global_id(1))));
}
/* 逐平面 FP32 曲线：pow(2.4) → pow(1/2.35)，代表色彩内核的访存/算力形态 */
kernel void curve32(read_only image2d_t src, write_only image2d_t dst) {
    const sampler_t smp = CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP | CLK_FILTER_NEAREST;
    int2 p = (int2)(get_global_id(0), get_global_id(1));
    float4 v = read_imagef(src, smp, p);
    float4 l = pow(max(v, (float4)(0.0f)), (float4)(2.4f));
    write_imagef(dst, p, pow(max(l, (float4)(0.0f)), (float4)(1.0f/2.35f)));
}
/* 同上，但中间量走 half（模拟 FP16 通路） */
kernel void curve16(read_only image2d_t src, write_only image2d_t dst) {
    const sampler_t smp = CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP | CLK_FILTER_NEAREST;
    int2 p = (int2)(get_global_id(0), get_global_id(1));
    float4 v = read_imagef(src, smp, p);
#ifdef cl_khr_fp16
    half4 hv = convert_half4(v);
    float4 l = pow(max(convert_float4(hv), (float4)(0.0f)), (float4)(2.4f));
    write_imagef(dst, p, convert_float4(convert_half4(pow(max(l, (float4)(0.0f)), (float4)(1.0f/2.35f)))));
#else
    write_imagef(dst, p, v);
#endif
}
'@

Write-Host "=== ① 参照：CPU 上同一张图的净色彩变换耗时 ===" -ForegroundColor Cyan
Tim "c1 解码 only"           "-y -hide_banner -loglevel error -i `"$d/src.png`" -vf format=rgb48le -f null -"
Tim "c2 解码+zscale(H2 变换)" "-y -hide_banner -loglevel error -i `"$d/src.png`" -vf `"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=smpte432:t=iec61966-2-1:m=bt709:r=pc,format=rgb48le`" -f null -"
$cpuT = $script:res["c2 解码+zscale(H2 变换)"].ms - $script:res["c1 解码 only"].ms
Write-Output ("  → CPU(zscale) 净变换 = {0:F1} ms；我方引擎(表驱动,20核) ≈ {1:F1} ms" -f $cpuT, ($MP*1000/1100))

# ⚠️ 滤镜选项里的文件路径不能含盘符冒号（`source=c:/...` 会破坏滤镜语法），必须用相对路径
$kl = "tests/output/validate/gpu2/k.cl"

Write-Host "`n=== ② GPU：往返税 + FP32 内核吞吐（4K, 逐平面）===" -ForegroundColor Cyan
$base = "-hide_banner -loglevel error -init_hw_device $dev -filter_hw_device ocl"
Tim "g1 仅往返 upload+download"  "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
Tim "g2 往返+identity 内核"      "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$kl:kernel=ident,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
Tim "g3 往返+FP32 曲线内核"       "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$kl:kernel=curve32,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
Tim "g4 往返+FP16 曲线内核"       "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$kl:kernel=curve16,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
$g1 = $script:res["g1 仅往返 upload+download"].ms
$g3 = $script:res["g3 往返+FP32 曲线内核"].ms
$cpuK = $script:res["c2 解码+zscale(H2 变换)"].ms - $script:res["c1 解码 only"].ms
$decOnly = $script:res["c1 解码 only"].ms
$eng = $MP*1000/1100
Write-Output ("  → GPU 往返税 = {0:F1} ms（= g1 − 解码 {1:F1}）；GPU FP32 内核净 = {2:F1} ms" -f [Math]::Max(0,$g1-$decOnly), $decOnly, [Math]::Max(0,$g3-$g1))
Write-Output ("  → 同任务净变换对比：CPU zscale = {0:F1} ms | CPU 引擎(表驱动) ≈ {1:F1} ms | GPU 往返+内核 = {2:F1} ms" -f $cpuK, $eng, [Math]::Max(0,$g1-$decOnly)+[Math]::Max(0,$g3-$g1))

Write-Host "`n=== ③ FP16 vs FP32 精度差（同负载，落 PNG16 后 ffmpeg psnr）===" -ForegroundColor Cyan
foreach ($k in @("curve32","curve16")) {
  $null = FF "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$kl:kernel=$k,hwdownload,format=gbrpf32le`" -frames:v 1 `"$(Join-Path $d "out_$k.png")`""
}
if ((Test-Path "$d/out_curve32.png") -and (Test-Path "$d/out_curve16.png")) {
  $o = Exec $ff "-hide_banner -i `"$(Join-Path $d 'out_curve32.png')`" -i `"$(Join-Path $d 'out_curve16.png')`" -lavfi psnr -f null -"
  ($o -split "`r?`n" | Select-String "average" | Select-Object -First 1 | ForEach-Object { "  $($_.Line.Trim())" })
  $mm = [regex]::Match($o, 'maxdiff[= ]+(\d+)'); if ($mm.Success) { Write-Output "  maxdiff(16bit码值) = $($mm.Groups[1].Value)" }
} else { Write-Output "  跳过：GPU 输出未生成（检查 e.txt）"; Get-Content "$d/e.txt" -EA SilentlyContinue | Select-Object -Last 4 | ForEach-Object { "    $($_.ToString().Trim())" } }
