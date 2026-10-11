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

Write-Host "`n### 6) 批次 C 数值锁（F-5/F-2/F-3/F-4/F-8，进程内宿主 GainMapTestHost）###" -ForegroundColor Cyan
# 判据载体：tests/GainMapTestHost（FfmpegGui.csproj 的 InternalsVisibleTo 名单里有它 ⇒ 能直接调
#   产品内部的 BuildIso21496Metadata / ComputeGainMap / ApplyGainMap / IsoBmffGainMapWriter.Build）。
# ⚠ 这段锁**只有本 §6 一条执行路径**（宿主默认全量模式由 verify-gainmap-host.ps1 跑，不含本批判据）
#   ⇒ 「标签出现在输出里」才算被执行（本仓踩过 contract 双拷贝「全绿但零执行」，这里用
#   「汇总数 == 点名条数」+ 逐条标签命中自证）。
# ⚠ 不起产品 GUI：宿主是 Exe 控制台进程；产品 exe 只在 §1–§5 以 --headless 跑（既有形态未变）。
$gmHost = "$root/tests/GainMapTestHost/bin/Release/net11.0/win-x64/GainMapTestHost.exe"
if (-not (Test-Path $gmHost)) { $gmHost = "$root/tests/GainMapTestHost/bin/Debug/net11.0/win-x64/GainMapTestHost.exe" }
$prodDll = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.dll"
if (-not (Test-Path $prodDll)) { $prodDll = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.dll" }
if (-not (Test-Path $gmHost)) {
  No "§6 前置：GainMapTestHost 已构建（$gmHost）—— fail-closed，不静默跳过"
} elseif (-not (Test-Path $prodDll)) {
  No "§6 前置：FfmpegGui.dll 存在（构建指纹来源）—— fail-closed"
} else {
  # 宿主输出目录里那份 FfmpegGui.dll 才是**真正被加载**的那份 ⇒ 与产品构建产物比哈希（绑构建指纹）
  $hostDll = Join-Path (Split-Path $gmHost -Parent) "FfmpegGui.dll"
  $hProd = "none"; $hHost = "none"
  if (Test-Path $hostDll) { $hHost = (Get-FileHash $hostDll -Algorithm SHA256).Hash.Substring(0, 16) }
  $hProd = (Get-FileHash $prodDll -Algorithm SHA256).Hash.Substring(0, 16)
  Write-Output ("[gate] host=" + $gmHost)
  Write-Output ("[gate] FfmpegGui.dll[:16] prod=" + $hProd + " host=" + $hHost)
  Ok "§6 被测宿主与产品 DLL 打印（prod=$hProd host=$hHost）"
  if ($hHost -ne $hProd) {
    No "§6 宿主加载的 FfmpegGui.dll 与产品构建**不是同一份**（stale build ⇒ 读数不算数）prod=$hProd host=$hHost"
  } else {
    Ok "§6 构建指纹绑定：宿主里的 FfmpegGui.dll[:16]==产品（$hProd）"
  }

  # 沙箱按进程拦 %TEMP% 写 ⇒ 把 TMP/TEMP/TMPDIR 指进工作区再跑（否则宿主子进程的像素路会假红）
  $envTmpBak = $env:TMP; $envTspBak = $env:TEMP; $envTdirBak = $env:TMPDIR
  $btmp = "$out/batchc-tmp"; New-Item -ItemType Directory -Force -Path $btmp | Out-Null
  $env:TMP = $btmp; $env:TEMP = $btmp; $env:TMPDIR = $btmp
  $gmTxt = ""
  try {
    $gp = Start-Process -FilePath $gmHost -ArgumentList "gainmath" -Wait -NoNewWindow -PassThru `
          -RedirectStandardOutput "$out/gainmath.out" -RedirectStandardError "$out/gainmath.err"
    if (Test-Path "$out/gainmath.out") { $gmTxt = [System.IO.File]::ReadAllText("$out/gainmath.out") }
    if (Test-Path "$out/gainmath.err") { $gmTxt = $gmTxt + "`n" + [System.IO.File]::ReadAllText("$out/gainmath.err") }
  } finally {
    $env:TMP = $envTmpBak; $env:TEMP = $envTspBak; $env:TMPDIR = $envTdirBak
  }
  Write-Host ($gmTxt -replace "(?m)^", "    ")

  # 汇总数 == 点名条数
  $mChecks = [regex]::Match($gmTxt, "GAINMATH CHECKS=(\d+)")
  $mResult = [regex]::Match($gmTxt, "GAINMATH RESULT: pass=(\d+) fail=(\d+)")
  if (-not $mChecks.Success -or -not $mResult.Success) {
    No "§6 宿主未打印 GAINMATH CHECKS/RESULT（判据根本没跑）"
  } else {
    $nChecks = [int]$mChecks.Groups[1].Value
    $nPass = [int]$mResult.Groups[1].Value
    $nFail = [int]$mResult.Groups[2].Value
    # 输出里实际出现的 ✅/❌ 行数（第三口径：防止「checks 计数与打印条目脱节」）
    $nPrinted = @($gmTxt -split "`r?`n" | Where-Object { $_ -match '^\s+[✅❌]\s+C' }).Count
    Ok "§6 读数：CHECKS=$nChecks pass=$nPass fail=$nFail 打印=$nPrinted"
    if ($nChecks -eq ($nPass + $nFail) -and $nChecks -eq $nPrinted -and $nChecks -ge 30) {
      Ok "§6 汇总==点名（CHECKS=pass+fail=打印行数，且 ≥30 条）"
    } else {
      No "§6 汇总与点名脱节 CHECKS=$nChecks pass+fail=$($nPass + $nFail) 打印=$nPrinted（要求三者相等且 ≥30）"
    }
    if ($nFail -eq 0) { Ok "§6 批次 C 数值锁全绿（fail=0）" }
    else {
      No "§6 批次 C 数值锁存在失败项 fail=$nFail"
      foreach ($ln in ($gmTxt -split "`r?`n")) { if ($ln -match '^\s+❌') { Write-Host ("      " + $ln.Trim()) -ForegroundColor DarkRed } }
    }
  }

  # 逐条点名（curated 清单：这批的核心判据，一条缺席就算覆盖丢失）
  $cLabels = @(
    'C1a [F-5] 1000nits', 'C1a [F-5] 4000nits',
    'C1b [F-5] 1000nits', 'C1b [F-5] 4000nits',
    'C1c [F-5·对照]',
    'C1d [F-5·Q6第1格] 1000nits', 'C1d [F-5·Q6第1格] 4000nits',
    'C1e [F-2·量化底] 1000nits', 'C1e [F-2·量化底] 4000nits',
    'C2a [F-2]', 'C2b [F-2]',
    'C3b [F-3]', 'C3e[rgb] [F-3]', 'C3e[gray] [F-3]', 'C3f [F-3·对照臂自证]',
    'C4a [F-4]', 'C4a2 [F-4]',
    'C5a [F-4]', 'C5b [F-4]', 'C6 [F-4/F-8]',
    'C7a [F-8]', 'C7b [F-8]', 'C7c [F-8·对照]'
  )
  $cMiss = @($cLabels | Where-Object { $gmTxt.IndexOf($_) -lt 0 })
  if ($cMiss.Count -eq 0) {
    Ok "§6 点名清单 $($cLabels.Count) 条全部出现在宿主输出里"
  } else {
    No "§6 点名清单缺失 $($cMiss.Count) 条：$($cMiss -join ' | ')"
  }
  # 反向自证：清单里故意放一条不存在的标签，必须判「缺席」⇒ 证明上面的命中检查不是恒真
  $bogus = @('C9z [不存在] 这条应当永远不出现')
  if ($gmTxt.IndexOf($bogus[0]) -lt 0) { Ok "§6 **反控**：不存在的标签确实判为缺席（点名检查有牙，非恒真）" }
  else { No "§6 反控失效：不存在的标签也被判命中 ⇒ 点名检查恒真，无凭据" }

  # 源码形态锁（结构锁：声明==使用 / F-2 锚 / F-3 接线 / F-4 接线）
  #   ⚠ 与宿主数值锁配对：数值锁证明「行为对」，形态锁证明「判据所依赖的那条接线还在」。
  function Get-MethodBody([string]$text, [string]$anchor) {
    $ai = $text.IndexOf($anchor)
    if ($ai -lt 0) { return "" }
    $ob = $text.IndexOf('{', $ai)
    if ($ob -lt 0) { return "" }
    $depth = 0; $end = -1
    for ($i = $ob; $i -lt $text.Length; $i++) {
      $ch = $text[$i]
      if ($ch -eq '{') { $depth++ }
      elseif ($ch -eq '}') { $depth--; if ($depth -eq 0) { $end = $i; break } }
    }
    if ($end -le $ob) { return "" }
    return $text.Substring($ai, $end - $ai + 1)
  }
  function Get-Window([string]$text, [string]$anchor, [int]$len) {
    $ai = $text.IndexOf($anchor)
    if ($ai -lt 0) { return "" }
    $take = [Math]::Min($len, $text.Length - $ai)
    return $text.Substring($ai, $take)
  }
  $encSrc = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/GainMapEncoder.cs")
  $simdSrc = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/SimdPixelOps.cs")
  $codecSrc = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/ColorMapping/GainMapJpegCodec.cs")

  # L-5（F-5 的结构锁）：前向增益式**必须引用声明的 offset 符号**，元数据分数字段也引用同一对符号。
  $cgBody = Get-MethodBody $encSrc 'internal static byte[] ComputeGainMap('
  $isoBody = Get-MethodBody $encSrc 'internal static byte[] BuildIso21496Metadata(int declaredChannels'
  $lg2Body = Get-MethodBody $simdSrc 'public static float Log2Gain('
  if ($cgBody.Length -gt 0 -and $isoBody.Length -gt 0 -and $lg2Body.Length -gt 0) {
    Ok ("§6 形态：取到 ComputeGainMap/BuildIso21496Metadata/Log2Gain 三段方法体（长度 {0}/{1}/{2}）" -f $cgBody.Length, $isoBody.Length, $lg2Body.Length)
  } else {
    No ("§6 形态锁取不到方法体（后续形态断言真空绿）长度 {0}/{1}/{2}" -f $cgBody.Length, $isoBody.Length, $lg2Body.Length)
  }
  $l5a = ($cgBody -match 'Log2Gain\(') -and ($cgBody -match 'KOffsetHdr') -and ($cgBody -match 'KOffsetSdr')
  $l5b = ($lg2Body -match 'offsetHdr') -and ($lg2Body -match 'offsetSdr') -and ($lg2Body -match 'MathF\.Log2')
  $l5c = ($isoBody -match 'KOffsetSdrN') -and ($isoBody -match 'KOffsetHdrN') -and ($isoBody -match 'KOffsetSdrD')
  if ($l5a -and $l5b -and $l5c) {
    Ok "§6 **L-5 结构锁**：前向（ComputeGainMap/Log2Gain）施加的 offset 与 BuildIso21496Metadata 声明的 offset 同源（同一个 KOffset* 符号）"
  } else {
    No "§6 L-5 结构锁红：前向=$l5a 公式=$l5b 声明=$l5c（声明与施加一旦分家，本批 F-5 就白修）"
  }
  # 反控：把前向体内的 KOffset* 变异掉 ⇒ 锚必须不命中
  $l5Mut = $cgBody -replace 'KOffset(Hdr|Sdr)', 'MutatedAway'
  if (-not ($l5Mut -match 'KOffset')) { Ok "§6 **反控**：KOffset 变异后 L-5 锚确实转红（有牙）" }
  else { No "§6 反控失效：变异后仍命中 ⇒ L-5 是恒真断言" }
  # 负断言：元数据体内不得再另写一份 offset 常量（两份必然漂移）
  if ($isoBody -notmatch 'offsetN\s*=\s*1') { Ok "§6 L-5 负断言：BuildIso21496Metadata 体内没有第二份 offset 常量" }
  else { No "§6 出现第二份 offset 常量（声明与前向脱节的路又开了一条）" }

  # F-2 的锚与前置条件必须留在代码里（注释即契约：删掉注释=删掉理由）
  # ⚠ 取数对象修正（2026-10-11 主循环）：这把锚原先只往 SimdPixelOps.cs 里
  #   `LogGainToByteScalar(` 之前的窗口找，但那句一手锚与前置条件实际写在 **GainMapEncoder.cs**
  #   的取整政策文档里（`:393-394`）⇒ 锚在、尺子却量错文件，判出假红。
  #   修法不是放宽，而是把"取整口径的理由"所在的那个窗口一起纳入，并**点名它落在哪个文件**，
  #   这样将来谁把注释删掉照样红（两处都找不到才红）。
  $quantDoc = ""
  $quantWhere = "-"
  $qi = $simdSrc.IndexOf('public static byte LogGainToByteScalar(')
  if ($qi -gt 0) { $quantDoc += $simdSrc.Substring([Math]::Max(0, $qi - 3000), [Math]::Min(3000, $qi - [Math]::Max(0, $qi - 3000))) }
  $ei = $encSrc.IndexOf('取整口径=')
  if ($ei -lt 0) { $ei = $encSrc.IndexOf('LogGainToByte(') }
  # ⚠ 方向修正：一手锚写在政策文档的**下两行**（GainMapEncoder.cs:393-395），
  #   原先只取标记**之前**的窗口 ⇒ 锚被窗口切在外面，判成假红。这里向后取 3000 字符。
  if ($ei -gt 0) { $quantDoc += $encSrc.Substring($ei, [Math]::Min(3000, $encSrc.Length - $ei)); $quantWhere = 'GainMapEncoder' }
  $f2a = ($quantDoc -match 'gainmapmath\.cpp:770') -and ($quantDoc -match 'affineMapGain')
  if ($f2a) { Ok "§6 **F-2 锚锁**：截断口径的一手锚（gainmapmath.cpp:770）+ 改 +0.5 的前置条件（affineMapGain 两趟）仍在注释里" }
  else { No "§6 F-2 锚锁红：取整口径的理由（参照一趟=截断）与前置条件（转两趟才准 round）被删/被改" }
  # 口径本身：落字节那一行必须仍是截断（宿主 C2a 已量过；这里是防「日后被静默改掉」的第二把锁）
  $qBody = Get-MethodBody $simdSrc 'public static byte LogGainToByteScalar('
  $f2b = ($qBody -notmatch '\+\s*0\.5f') -and ($qBody -notmatch 'MathF\.Round') -and ($qBody -notmatch 'Math\.Round')
  if ($f2b) { Ok "§6 F-2 口径锁：LogGainToByteScalar 落字节仍是截断（未偷换 +0.5/round）" }
  else { No "§6 F-2 口径锁红：截断被改成舍入，但两趟前置条件（实测 min/max 进声明）并未落地" }
  $qMut = $qBody -notmatch '\+\s*0\.5f'
  if ($qBody -match 'clamped / maxLog2Gain \* 255f') { Ok "§6 **反控**：截断表达式本身在体内可定位（锚活着）" }
  else { No "§6 截断表达式的锚找不到 ⇒ 上一条 F-2 口径锁可能是真空绿" }

  # F-3 接线锁：增益分母必须来自「底图交付字节」的回灌，而不是钳前 float
  $encBody = Get-MethodBody $encSrc 'public static async Task<bool> EncodeAsync('
  $f3a = ($encBody -match 'StoredBaseLinearFromBgra') -and ($encBody -match 'ComputeGainMap\(normHdr,\s*sdrStored')
  $f3b = ($encBody -notmatch 'ComputeGainMap\(normHdr,\s*sdrLinear')
  if ($f3a -and $f3b) { Ok "§6 **F-3 接线锁**：EncodeAsync 里 ComputeGainMap 的分母是 sdrStored（由交付字节反读），钳前 sdrLinear 不再进增益" }
  else { No "§6 F-3 接线锁红：分母回灌的接线被改回钳前 float（有回退）" }
  $f3Mut = $encBody -replace 'ComputeGainMap\(normHdr,\s*sdrStored', 'ComputeGainMap(normHdr, sdrLinear'
  if ($f3Mut -notmatch 'ComputeGainMap\(normHdr,\s*sdrStored') { Ok "§6 **反控**：把分母变异回 sdrLinear ⇒ 接线锁确实转红（有牙）" }
  else { No "§6 反控失效 ⇒ F-3 接线锁恒真" }
  # 分母回灌与底图字节生成必须是**同一份**数据（不许第二处另算）
  $storedBody = Get-MethodBody $encSrc 'internal static void StoredBaseLinearFromBgra('
  if ($storedBody -match 'SrgbToLinearScalar') { Ok "§6 F-3 同源锁：分母反读走的是与解侧同一个 SrgbToLinearScalar（未另写一份 sRGB 反函数）" }
  else { No "§6 F-3 同源锁红：分母反读没有复用解侧的反函数（两份实现必漂移）" }

  # F-4 接线锁：色度采样由单一真值给定；AVIF 增益图不得再兜回 4:2:0
  #   ⚠ GainMapChromaSubsampling 是**表达式体**成员（无大括号）⇒ 取「方法体」的 brace 法不适用，
  #     改用窗口（锚点后 300 字符）。
  $csBody = Get-Window $codecSrc 'public static string GainMapChromaSubsampling(' 300
  $f4a = ($csBody -match 'multiChannel \? "444" : "420"')
  if ($f4a) { Ok "§6 **F-4 接线锁**：增益图色度采样 = multiChannel⇒444（参照 RGB888 全 1×1），单一真值在 GainMapJpegCodec" }
  else { No "§6 F-4 接线锁红：multiChannel 与色度采样的成套关系被改动（可能出现「声明多通道 / 数据 4:2:0」）" }
  if ($encSrc -notmatch '--chroma_subsampling 420') { Ok "§6 F-4 负断言：GainMapEncoder 内不再硬编码 --chroma_subsampling 420" }
  else { No "§6 GainMapEncoder 里又出现硬编码 --chroma_subsampling 420（绕过单一真值）" }
  $avifBody = Get-MethodBody $encSrc 'private static async Task<bool> EncodeAvifGainMapAsync('
  # ⚠ 取数对象修正（2026-10-11 主循环）：原先用 `-notmatch 'ffmpeg, "yuv420p"'` 判"不退 4:2:0"，
  #   但那一句撞的是**底图**那趟 `EncodeAvifFileAsync(basePng, …, "yuv420p", …)` —— SDR 底图走
  #   4:2:0 是合法交付，不在 F-4 的射程内 ⇒ 假红。本锁的射程只有**增益图**那两趟调用。
  #   判据改成正面钉三件事：成套表达式在、420 只允许出现一次且属于 basePng 那趟、gmPng 两趟都不得带 420。
  $gmFmtOk   = ($avifBody -match 'gmPixFmt\s*=\s*multiChannel \? "yuv444p" : "gray"')
  $n420       = ([regex]::Matches($avifBody, '"yuv420p"')).Count
  $baseHas420 = ($avifBody -match 'EncodeAvifFileAsync\(basePng[^;]*"yuv420p"')
  $gmHas420   = ($avifBody -match 'EncodeAvifFileAsync\(gmPng[^;]*"yuv420p"')
  $f4b = $gmFmtOk -and ($n420 -eq 1) -and $baseHas420 -and (-not $gmHas420)
  if ($f4b) { Ok "§6 F-4 AVIF 锁：增益图交付只允许 gray / yuv444p（420 在方法内出现 1 次且属于底图那趟）" }
  else { No "§6 F-4 AVIF 锁红：成套表达式缺失或 420 越界（gmFmt=$gmFmtOk 次=$n420 底图=$baseHas420 增益图=$gmHas420）" }
  $f4Mut = $csBody -replace 'multiChannel \? "444" : "420"', 'multiChannel ? "420" : "420"'
  if ($f4Mut -notmatch '"444"') { Ok "§6 **反控**：把 444 变异成 420 ⇒ F-4 接线锁确实转红（有牙）" }
  else { No "§6 反控失效 ⇒ F-4 接线锁恒真" }
}

Write-Host "`n===== PASS=$pass FAIL=$fail =====" -ForegroundColor $(if($fail -eq 0){"Green"}else{"Red"})
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）；失败则保留供排查
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
# ⚠ **退出码语义**（2026-09-17 补，§6 第 58 条同类）：此前本脚本**全文无 `exit`** ⇒ 恒 `exit 0`；
#   而运行器/CI **只看退出码** ⇒ 它红了运行器也不知道（"恒绿门禁"）。
#   ⚠ 必须显式 `else { exit 0 }`：宿主删除守卫会污染 `$LASTEXITCODE`（实测变 2）⇒ 只写前半句会假红。
if ($fail -gt 0) { exit 1 } else { exit 0 }
