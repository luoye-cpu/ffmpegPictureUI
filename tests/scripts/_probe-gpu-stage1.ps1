# GPU FP32 收益实测（小测试）
# 测三个可对比的量：
#   ① CPU 阶段分解（解码 / 色彩变换 / 编码）→ 给出任何 GPU 优化的分母
#   ② GPU 往返开销（hwupload+hwdownload，不做任何计算）→ GPU 化的固定税
#   ③ GPU 内核吞吐（program_opencl，逐平面 FP32 曲线运算）→ GPU 的算力上限证据
# 注：program_opencl 只能**逐平面**访问 image2d_t，无法一次表达跨通道 3x3 矩阵，
#     所以 ③ 用"逐平面曲线"作 GPU 吞吐代理（真实变换的访存量不低于它）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$d  = "$root/tests/output/validate/gpu1_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null
# ⚠ 无 pass/fail 计数 ⇒ 结尾**无条件自清**；⚠ 提前 `exit` 的分支也要各自先自清（否则留 GUID 残留）。
$W = 3840; $H = 2160; $MP = [Math]::Round($W*$H/1e6, 1)

function FF([string]$a2) {
  $p = Start-Process -FilePath $ff -ArgumentList $a2 -NoNewWindow -Wait -PassThru -RedirectStandardError "$d/e.txt"
  return $p.ExitCode
}
function Tim([string]$tag, [string]$a2, [int]$n = 3) {
  $best = [double]::MaxValue; $rc = 0
  for ($k = 0; $k -lt $n; $k++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); $rc = FF $a2; $sw.Stop()
    if ($sw.Elapsed.TotalMilliseconds -lt $best) { $best = $sw.Elapsed.TotalMilliseconds }
  }
  $script:res[$tag] = @{ ms = $best; rc = $rc }
  Write-Output ("  {0,-34} {1,8:F1} ms   exit={2}" -f $tag, $best, $rc)
}

# ── 素材：带噪声的 4K 16-bit PNG（避免平坦图把编解码测得虚快）──
$null = FF "-y -hide_banner -loglevel error -f lavfi -i `"smptehdbars=s=${W}x${H},noise=alls=60:allf=t+u`" -pix_fmt rgb48le `"$d/src.png`""
$null = FF "-y -hide_banner -loglevel error -i `"$d/src.png`" -vf format=rgb48le -f rawvideo `"$d/src.rgb48`""
Write-Output ("素材 4K {0} MPix, PNG={1} MB, rgb48={2} MB" -f $MP, [Math]::Round((Get-Item "$d/src.png").Length/1MB,1), [Math]::Round((Get-Item "$d/src.rgb48").Length/1MB,1))
$script:res = @{}

Write-Host "`n=== ① CPU 阶段分解（分母）===" -ForegroundColor Cyan
Tim "a1 解码→null(不落盘)"        "-y -hide_banner -loglevel error -i `"$d/src.png`" -vf format=rgb48le -f null -"
Tim "a2 解码→raw(落盘47MB)"       "-y -hide_banner -loglevel error -i `"$d/src.png`" -vf format=rgb48le -f rawvideo `"$d/t1.raw`""
Tim "a3 编码 raw→PNG16"           "-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb48le -video_size ${W}x${H} -i `"$d/src.rgb48`" `"$d/t2.png`""
Tim "a4 仅 zscale sRGB→P3(H2)"    "-y -hide_banner -loglevel error -i `"$d/src.png`" -vf `"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=smpte432:t=iec61966-2-1:m=bt709:r=pc,format=rgb48le`" -f null -"
Tim "a5 H2 全链(解+变换+编)"      "-y -hide_banner -loglevel error -i `"$d/src.png`" -vf `"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=smpte432:t=iec61966-2-1:m=bt709:r=pc,format=rgb48le`" `"$(Join-Path $d 't3.png')`""
$dec = $script:res["a1 解码→null(不落盘)"].ms; $z = $script:res["a4 仅 zscale sRGB→P3(H2)"].ms
$enc = $script:res["a3 编码 raw→PNG16"].ms;      $e2e = $script:res["a5 H2 全链(解+变换+编)"].ms
$kernelMs = [Math]::Max(0, $z - $dec)
Write-Output ("  → 色彩变换净耗时 = {0:F1} ms（占 H2 全链 {1:F1}%）；解码 {2:F1} ms + 编码 {3:F1} ms 占 {4:F1}%" -f `
  $kernelMs, (100*$kernelMs/$e2e), $dec, $enc, (100*($dec+$enc)/$e2e))
Write-Output "  → 我方 CPU 表驱动内核实测基准：单核 232 MPix/s、20 核 1100 MPix/s ⇒ 4K ≈ $([Math]::Round($MP*1000/1100,1)) ms"

Write-Host "`n=== ② OpenCL 设备选择尝试 ===" -ForegroundColor Cyan
$dev = ""
# ⚠️ 语法：hwdevice 选项用**冒号**分隔（不是逗号）；枚举形式为 platform_index.device_index
#   本机：0.0 = RTX 5080，0.1 = RTX 4060 Laptop，1.0 = Intel RaptorLake-S iGPU
foreach ($opt in @("opencl=ocl:platform_index=0:device_index=0", "opencl=ocl:device=dGPU", "opencl=ocl:platform_index=1:device_index=0", "opencl=ocl")) {
  $rc = FF "-y -hide_banner -loglevel error -init_hw_device $opt -f lavfi -i color=gray:s=64x64 -vf `"format=gray,hwupload,hwdownload,format=gray`" -frames:v 1 `"$(Join-Path $d 'smoke.png')`""
  Write-Output ("  -init_hw_device {0,-46} exit={1}" -f $opt, $rc)
  if ($rc -eq 0 -and (Test-Path "$d/smoke.png")) { $dev = $opt; break }
  if ($rc -ne 0) { Get-Content "$d/e.txt" -EA SilentlyContinue | Select-Object -Last 2 | ForEach-Object { "      $($_.Trim())" } }
}
if (-not $dev) { Write-Host "  OpenCL 设备不可用 → ②③ 无法测（结论不受影响：①已给出分母）" -ForegroundColor Yellow; Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; exit 0 }
Write-Output "  选定: $dev"

Write-Host "`n=== ③ GPU 往返税 + 内核吞吐（4K, FP32 逐平面）===" -ForegroundColor Cyan
Set-Content -Path "$d/curve.cl" -Encoding ascii -Value @'
kernel void ident(read_only image2d_t src, write_only image2d_t dst) {
    const sampler_t smp = CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP | CLK_FILTER_NEAREST;
    int2 p = (int2)(get_global_id(0), get_global_id(1));
    write_imagef(dst, p, read_imagef(src, smp, p));
}
kernel void curve(read_only image2d_t src, write_only image2d_t dst) {
    const sampler_t smp = CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP | CLK_FILTER_NEAREST;
    int2 p = (int2)(get_global_id(0), get_global_id(1));
    float4 v = read_imagef(src, smp, p);
    // 逐通道 EOTF(sRGB 近似 pow 2.4) → OETF(pow 1/2.4)，模拟色彩内核的算力/访存形态
    float4 l = pow(max(v, 0.0f), 2.4f);
    float4 o = pow(max(l, 0.0f), 1.0f/2.35f);
    write_imagef(dst, p, o);
}
'@
$base = "-hide_banner -loglevel error -init_hw_device $dev -filter_hw_device ocl"
Tim "b1 GPU 往返(upload+download)" "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
Tim "b2 GPU 往返+identity 内核"    "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$d/curve.cl:kernel=ident,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
Tim "b3 GPU 往返+曲线内核(FP32)"   "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$d/curve.cl:kernel=curve,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
Tim "b4 GPU 纯内核(不落盘对比用)"  "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$d/curve.cl:kernel=curve`" -frames:v 1 -f null -"

$b1 = $script:res["b1 GPU 往返(upload+download)"].ms; $b2 = $script:res["b2 GPU 往返+identity 内核"].ms
$b3 = $script:res["b3 GPU 往返+曲线内核(FP32)"].ms;  $b4 = $script:res["b4 GPU 纯内核(不落盘对比用)"].ms
if ($b1 -gt 0) { Write-Output ("  → GPU 往返税 = {0:F1} ms；identity 净内核 = {1:F1} ms；曲线内核净 = {2:F1} ms" -f $b1, [Math]::Max(0,$b2-$b1), [Math]::Max(0,$b3-$b1)) }
Write-Output ("  → 结论对照：色彩变换 CPU 净时 {0:F1} ms vs GPU(含往返) {1:F1} ms = {2:F2}×" -f $kernelMs, $b3, ($b3/[Math]::Max(0.1,$kernelMs)))

# ⚠ 结尾自清（运行级隔离配套）：本脚本**无 pass/fail 计数** ⇒ 必须**无条件**自清 ——
#   `if ($fail -eq 0)` 在无计数脚本里 `$null -eq 0` 为 False，**永不成立**（§6 第 66 条已记这个坑）。
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
