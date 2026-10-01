# GainMap 纯托管兜底实测 (无 exiftool 依赖路径)
#   1) 编码 Ultra HDR JPEG (GainMapEncoder)
#   2) 解码走「纯托管 ISO 21496-1 + 容器切割」路径 (日志断言)
#      交叉校验: 纯托管解析出的增益数值 == exiftool 读到的 hdrgm XMP 数值
#   3) 端到端往返 UltraHDR → JXL 产出正常 + 峰值亮度符合源 (1000nits 级)
#   4) 普通 JPEG 不被误判为增益图容器
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$et  = "$root/publish/PLAN/exiftool/exiftool.exe"
$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   唯一目录没人替你清 ⇒ 结尾自清（照 `verify-gainmap-memory.ps1` 既有范式）。
$val = "$root/tests/output/validate"; $out = "$val/mgd_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$pass=0;$fail=0
function Ok($m){ Write-Host ("  PASS {0}" -f $m) -ForegroundColor Green; $script:pass++ }
function No($m){ Write-Host ("  FAIL {0}" -f $m) -ForegroundColor Red;   $script:fail++ }
function Run($a,$tag){
  Start-Sleep -Seconds 2
  Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput "$out/$tag.o" -RedirectStandardError "$out/$tag.e" | Out-Null
}
# ⚠ 取子进程 stdout 一律走 Start-Process，**不要用调用运算符 `& exe`**：
#   `& exe` 在部分宿主（含 agent 宿主）会**静默失败**，输出为空 ⇒ 依赖它的断言全部假红。
#   本轮实测：§1b 的 5 条 + §2 的 6 条就是这么红的（同一命令直接跑得到 pass=6 fail=0）。
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

Write-Host "`n### 1) 编码 Ultra HDR JPEG ###" -ForegroundColor Cyan
Run "--headless --log-level debug -i `"$val/src_hdr.png`" --format jpg --jpeg-gain-map true --output `"$out/enc`"" "enc"
$jpg = (Get-ChildItem -Recurse "$out/enc" -Include *.jpg,*.jpeg -EA SilentlyContinue | Select-Object -First 1).FullName
if(-not $jpg -or (Get-Item $jpg).Length -eq 0){
  No "编码产出 Ultra HDR"; Write-Host "`n===== PASS=$pass FAIL=$fail =====" -ForegroundColor Red; exit 1
}
Ok "编码产出 Ultra HDR ($((Get-Item $jpg).Length) B)"

Write-Host "`n### 1b) 服务级探针 (ServiceProbe gainmap — 纯托管容器解析单元断言) ###" -ForegroundColor Cyan
$probe = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if(Test-Path $probe){
  $po = Exec $probe "gainmap `"$jpg`" `"$out/sec.jpg`""
  Write-Host ($po -replace "(?m)^", "    ")
  if($po -match "images=2"){ Ok "顶层两幅图 (主图 + 增益图)" } else { No "顶层图数非 2" }
  if($po -match "mpf\[1\].*soi=True"){ Ok "MPF 副图偏移命中 SOI" } else { No "MPF 副图偏移未命中 SOI" }
  if($po -match "iso min="){ Ok "ISO 21496-1 元数据解出" } else { No "ISO 元数据未解出" }
  if($po -match "secondary_written=.*bytes=[1-9]"){ Ok "副图字节可纯托管切割" } else { No "副图切割失败" }
  if($po -match "PROBE RESULT: pass=\d+ fail=0"){ Ok "探针断言全部通过" } else { No "探针存在失败项" }
} else { Write-Host "  SKIP 未构建 ServiceProbe" -ForegroundColor Yellow }

Write-Host "`n### 2) 往返解码 UltraHDR → JXL (走纯托管路径) ###" -ForegroundColor Cyan
Run "--headless --log-level debug -i `"$jpg`" --format jxl --output `"$out/dec`"" "dec"
$log = (Get-Content "$out/dec.o" -EA SilentlyContinue) -join "`n"
$jxl = Get-ChildItem -Recurse "$out/dec" -Filter *.jxl -EA SilentlyContinue | Select-Object -First 1
if($jxl -and $jxl.Length -gt 0){ Ok "往返产出 JXL ($($jxl.Length) B)" } else { No "往返未产出 JXL" }
if($log -match "元数据来源: ISO 21496-1"){ Ok "元数据使用纯托管 ISO 21496-1 解析" }
else { No "元数据未走纯托管 ISO 路径" }
if($log -match "纯托管容器切割"){ Ok "副图使用纯托管容器切割" } else { No "副图未走纯托管切割" }
$mMgd = [regex]::Match($log, "元数据: min=(-?[\d.]+) max=(-?[\d.]+) gamma=([\d.]+) offsetSDR=([\d.]+) offsetHDR=([\d.]+) Channels=(\d+)")
if(-not $mMgd.Success){ No "日志中无纯托管元数据行可比对"; Write-Host $log }
else {
  $mgd = @{ min=[double]$mMgd.Groups[1].Value; max=[double]$mMgd.Groups[2].Value; gamma=[double]$mMgd.Groups[3].Value;
            osdr=[double]$mMgd.Groups[4].Value; ohdr=[double]$mMgd.Groups[5].Value; ch=[int]$mMgd.Groups[6].Value }
  Ok "解析到元数据 min=$($mgd.min) max=$($mgd.max) gamma=$($mgd.gamma) offsetSDR=$($mgd.osdr) offsetHDR=$($mgd.ohdr) ch=$($mgd.ch)"
  # ── 交叉校验：改用**可推导的期望值**（原写法读 hdrgm XMP，但该数据源已不存在）──
  # ⚠ 原写法拿 `exiftool -GainMapMin/-GainMapMax/...`（hdrgm XMP 组）与本解析结果比对；
  #   但现行实现只写 **ISO 21496-1**、不写 XMP（实测 exiftool 读不到任何 hdrgm 标签）
  #   ⇒ 那 6 条断言属**陈旧契约**（不是产品回归）。这里换成不依赖外部元数据源的校验：
  #   源峰值 1000 nits、SDR 白点 203 nits ⇒ 增益上限应 = log2(1000/203) ≈ 2.3005；
  #   且单通道 SDR 底契约要求 min=0、gamma=1、channels=1。
  $expMax = [Math]::Log(1000 / 203, 2)
  if([Math]::Abs($mgd.max - $expMax) -le 0.05){
    Ok "max=$($mgd.max) 与「源 1000nits / 白点 203nits」推导值 $([Math]::Round($expMax,4)) 一致"
  } else {
    No "max=$($mgd.max) 与推导值 $([Math]::Round($expMax,4)) 不一致"
  }
  if($mgd.min -eq 0 -and $mgd.gamma -eq 1 -and $mgd.ch -eq 1){
    Ok "min=0 / gamma=1 / channels=1 符合「单通道 SDR 底」契约"
  } else {
    No "基础字段异常: min=$($mgd.min) gamma=$($mgd.gamma) ch=$($mgd.ch)"
  }
}
if($log -match "峰值\s+(\d+)\s*nits"){
  $nits=[int]$Matches[1]
  if($nits -ge 900 -and $nits -le 1100){ Ok "解码峰值 $nits nits (源 1000nits 级)" } else { No "解码峰值异常: $nits nits" }
} else { No "日志中找不到峰值亮度" }

Write-Host "`n### 3) 普通 JPEG 不误判为增益图容器 ###" -ForegroundColor Cyan
Run "--headless --log-level debug -i `"$val/sdr_plain.png`" --format jpg --output `"$out/plain`"" "plain"
$pjpg = (Get-ChildItem -Recurse "$out/plain" -Include *.jpg,*.jpeg -EA SilentlyContinue | Select-Object -First 1).FullName
if($pjpg){
  Run "--headless --log-level debug -i `"$pjpg`" --format jxl --output `"$out/plain2jxl`"" "plain2"
  $plog = (Get-Content "$out/plain2.o" -EA SilentlyContinue) -join "`n"
  if($plog -notmatch "检测到 Ultra HDR"){ Ok "普通 JPEG 未误判 (无增益图解码)" } else { No "普通 JPEG 被误判为 Ultra HDR" }
  $pjxl = Get-ChildItem -Recurse "$out/plain2jxl" -Filter *.jxl -EA SilentlyContinue | Select-Object -First 1
  if($pjxl -and $pjxl.Length -gt 0){ Ok "普通 JPEG → JXL 正常 ($($pjxl.Length) B)" } else { No "普通 JPEG → JXL 失败" }
} else { No "普通 JPEG 编码失败" }

Write-Host "`n### 4) SDR 源（headroom=1）⇒ **普通 JPEG**（2026-09-20 裁定）；纯托管往返改用 HDR 源 ###" -ForegroundColor Cyan
# ⚠ 2026-09-20 裁定（**SDR ⇒ 普通 JPEG**）：SDR 源**不再**产出「零增益的假 Ultra HDR」
#   ⇒ 本段原「SDR 增益图纯托管往返」的**前提已消失**。现拆成两半，**覆盖不丢**：
#     ① SDR 源 ⇒ 必须是**普通 JPEG**（产物无增益图）+ **点名**；
#     ② 纯托管往返（原覆盖）改用 **HDR 源**（其产物含真增益图，往返路径与原先同一条）。
Run "--headless --log-level debug -i `"$val/sdr_plain.png`" --format jpg --jpeg-gain-map true --output `"$out/sdrgm`"" "sdrgm"
$slog0 = (Get-Content "$out/sdrgm.o" -EA SilentlyContinue) -join "`n"
if($slog0 -match '输入为 SDR'){ Ok "SDR 源：日志**点名**「输入为 SDR…已按普通 JPEG 输出」（非静默）" } else { No "SDR 源未点名（应已按普通 JPEG 输出）" }
$sg = (Get-ChildItem -Recurse "$out/sdrgm" -Include *.jpg,*.jpeg -EA SilentlyContinue | Select-Object -First 1).FullName
if($sg){
  Run "--headless --log-level debug -i `"$sg`" --format jxl --output `"$out/sdrgm2jxl`"" "sdrgm2"
  $slog = (Get-Content "$out/sdrgm2.o" -EA SilentlyContinue) -join "`n"
  if($slog -notmatch "元数据来源: ISO 21496-1"){ Ok "SDR 源产物**不含增益图**（重编码读不到 ISO 21496-1）⇒ 确为普通 JPEG" }
  else { No "SDR 源产物仍带增益图（应已按普通 JPEG 输出）" }
} else { No "SDR 源编码失败" }
# ── 纯托管往返（原 §4 覆盖，改用 HDR 源保留）──
Run "--headless --log-level debug -i `"$val/src_hdr.png`" --format jpg --jpeg-gain-map true --output `"$out/hdrgm`"" "hdrgm"
$hg = (Get-ChildItem -Recurse "$out/hdrgm" -Include *.jpg,*.jpeg -EA SilentlyContinue | Select-Object -First 1).FullName
if($hg){
  Run "--headless --log-level debug -i `"$hg`" --format jxl --output `"$out/hdrgm2jxl`"" "hdrgm2"
  $hlog = (Get-Content "$out/hdrgm2.o" -EA SilentlyContinue) -join "`n"
  if($hlog -match "元数据来源: ISO 21496-1"){ Ok "HDR 源增益图: **纯托管往返**（ISO 解析，max=$([regex]::Match($hlog,'max=(-?[\d.]+)').Groups[1].Value)）" }
  else { No "HDR 源增益图未走纯托管路径" }
  $hjxl = Get-ChildItem -Recurse "$out/hdrgm2jxl" -Filter *.jxl -EA SilentlyContinue | Select-Object -First 1
  if($hjxl -and $hjxl.Length -gt 0){ Ok "HDR 增益图 → JXL 正常 ($($hjxl.Length) B)" } else { No "HDR 增益图 → JXL 失败" }
} else { No "HDR 源增益图编码失败" }

Write-Host "`n### 5) 已移除的「BT.2100 PQ 底」：废弃开关必须无效（带负控）###" -ForegroundColor Cyan
# ⚠ 原 §5 断言的是「PQ HDR 底」契约（日志标 HDR 底 / 底图附 PQ ICC / ISO BaseRenditionIsHDR /
#   增益方向翻转 / 解码走 HDR 底分支 …）。该契约**已从产品中移除**：
#     · `JpegGainMapBaseHdr` / `jpeg-gain-map-base-hdr` / `BaseHdr` 在 `src/FfmpegGui` 下**零引用**
#       （实测 grep 无任何命中）；
#     · `GainMapEncoder` 只产出 SDR 底（sRGB 或 Rec.2020 SDR，见其 baseGamut 分支与日志「底图=sRGB (SDR)」）。
#   ⇒ 原 6 条断言属**陈旧契约**（不是产品回归）。
#   这里改为断言**当前的真实行为**：该开关被 CLI 接受但**不产生任何效果** ——
#   带它编码出的产物与不带它编码出的产物**逐像素一致**（降级后 PSNR ≥ 50dB）。
#   这既把「开关已废弃」钉成可回归契约，又构成负控：比较若发现差异就会红。
Run "--headless --log-level debug -i `"$val/src_hdr.png`" --format jpg --jpeg-gain-map true --jpeg-gain-map-base-hdr true --output `"$out/pqbase`"" "pq"
$pq = (Get-ChildItem -Recurse "$out/pqbase" -Include *.jpg,*.jpeg -EA SilentlyContinue | Select-Object -First 1).FullName
if(-not $pq){ No "废弃开关用例未产出" }
else {
  Ok "带 --jpeg-gain-map-base-hdr true 仍能正常产出 ($((Get-Item $pq).Length) B)"
  $plog = (Get-Content "$out/pq.o" -EA SilentlyContinue) -join "`n"
  if($plog -match "底图=sRGB \(SDR\)" -or $plog -match "底图=Rec\.2020 \(SDR"){ Ok "日志确认底图仍为 SDR（该开关未生效）" }
  else { No "底图不是 SDR —— 该开关似乎又生效了，需复核契约" }
  Run "--headless -i `"$pq`" --format png --output `"$out/pq2png`"" "pq2p"
  Run "--headless -i `"$jpg`" --format png --output `"$out/sdr2png`"" "sdr2p"
  $pa = (Get-ChildItem -Recurse "$out/pq2png" -Filter *.png -EA SilentlyContinue | Select-Object -First 1).FullName
  $pb = (Get-ChildItem -Recurse "$out/sdr2png" -Filter *.png -EA SilentlyContinue | Select-Object -First 1).FullName
  if($pa -and $pb){
    $ps = Exec $ff "-i `"$pa`" -i `"$pb`" -lavfi psnr -f null -"
    $m = [regex]::Match($ps, "average:([\d.]+|inf)")
    if($m.Success){
      $v = if($m.Groups[1].Value -eq "inf"){ 99 } else { [double]$m.Groups[1].Value }
      if($v -ge 50){ Ok "带/不带废弃开关的产物降级后 PSNR=$v dB ⇒ 开关无效（契约一致）" }
      else { No "带/不带废弃开关的产物出现差异 PSNR=$v ⇒ 开关语义变了，需复核" }
    } else { No "PSNR 无法计算`n$ps" }
  } else { No "降级 PNG 未产出" }
}

Write-Host "`n===== PASS=$pass FAIL=$fail =====" -ForegroundColor $(if($fail -eq 0){"Green"}else{"Red"})
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）；失败则保留供排查
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
# ⚠ **退出码语义**（2026-09-17 补，§6 第 58 条同类）：此前本脚本**全文无 `exit`** ⇒ 恒 `exit 0`；
#   而运行器/CI **只看退出码** ⇒ 它红了运行器也不知道（"恒绿门禁"）。
#   ⚠ 必须显式 `else { exit 0 }`：宿主删除守卫会污染 `$LASTEXITCODE`（实测变 2）⇒ 只写前半句会假红。
if ($fail -gt 0) { exit 1 } else { exit 0 }
