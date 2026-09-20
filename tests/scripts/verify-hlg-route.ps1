# verify-hlg-route.ps1 —— HLG 走 OOTF 分支的**接线级**门禁（2026-09-16 新增）
#
# 背景（为什么必须有这一条）：
#   既有覆盖只有「类级」—— `ServiceProbe ootf`（11 条）只测 `Bt2100Ootf` 类本身数学正确，
#   `ServiceProbe hlgwire`（3 条）测 H1 内核（float RGBA 路径）施加 OOTF 的数值。
#   但**真实执行路径**是 QueueProcessor → 色彩引擎 → 默认走 H2（zscale）后端，没有任何断言
#   证明"HLG 源确实被当 HLG 处理"。本项目在 `verify-color-wiring` 上吃过的亏与此同类：
#   **类级绿 ≠ 接线级绿**。
#
# 上一手交接把「HLG OOTF headless 未触发」列为唯一遗留 ⚠️，判据是"HLG 与 PQ 源同图转 SDR
# 像素逐位一致（PSNR=inf）"。本次复核实测该结论**不成立**，原因是**测试素材缺陷**：
#   交接所用的 hlg_src.png 与 pq_src.png **md5 完全相同**（`2a037da4…`），且 ffprobe 报
#   `color_transfer=unknown` —— 两个"源"其实是同一个无标签文件 ⇒ 产物当然逐位一致。
#   素材缺陷的成因：本机 ffmpeg 的 PNG 编码器**不写 cICP**（`-color_trc arib-std-b67` 写不进 PNG），
#   必须由应用侧（PngCicpService）打标。
# ⇒ 本脚本第 0 节把"素材必须真的带标签且互不相同"变成断言，正是这条缺陷的直接回归锁。
#
# 断言：
#   0. 素材自检：两源 md5 必须**不同**，且 ffprobe 读出的 transfer 分别为 arib-std-b67 / smpte2084；
#   1. 接线：HLG→SDR 与 PQ→SDR 都成功；日志含「决策：Map」+「后端=H2」；两者实测峰值不同；
#      PSNR(HLG→SDR, PQ→SDR) 必须**有限且显著低于阈值**（原缺陷为 inf）；
#   2. 控制组（有牙）：同一 HLG 源跑两次 ⇒ PSNR=inf（证明第 1 节的 PSNR 判据能识别"逐位一致"）；
#      另锁一条可用性诚实：无标签(SDR)源 + `--color-tone-map` 必须**明确报错**，不得静默直通。
#
# ⚠ 调外部 exe 一律 Start-Process + 单个参数字符串（见 HANDOVER §C.5 的坑）。
# 用法：pwsh -NoProfile -File tests/scripts/verify-hlg-route.ps1
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$val = "$root/tests/output/hlgroute_$([guid]::NewGuid().ToString('N').Substring(0, 8))"

$pass = 0; $fail = 0
function Say($t) { Write-Output $t }
function CK($ok, $msg) { if ($ok) { $script:pass++; Say "  PASS $msg" } else { $script:fail++; Say "  FAIL $msg" } }
function Run-Exe([string]$file, [string]$argStr) {
    Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$val/_exec.out" -RedirectStandardError "$val/_exec.err" | Out-Null
    return ([System.IO.File]::ReadAllText("$val/_exec.out") + "`n" + [System.IO.File]::ReadAllText("$val/_exec.err"))
}
# 取 PSNR 平均值（无差异时 ffmpeg 打 inf）
function Get-Psnr([string]$a, [string]$b) {
    $o = Run-Exe $ff "-hide_banner -i `"$a`" -i `"$b`" -lavfi psnr -f null -"
    $m = [regex]::Match($o, 'average:(\S+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}
function Get-Transfer([string]$f) {
    $o = Run-Exe $fp "-v error -select_streams v:0 -show_entries stream=color_transfer -of csv=p=0 `"$f`""
    return ($o -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
}
function Get-Md5([string]$f) {
    if (-not (Test-Path $f)) { return '' }
    return (Get-FileHash -Algorithm MD5 -Path $f).Hash
}
function Get-MeasuredPeak([string]$logPath) {
    if (-not (Test-Path $logPath)) { return '' }
    $t = [System.IO.File]::ReadAllText($logPath)
    $m = [regex]::Match($t, '采用\s*(\d+(?:\.\d+)?)\s*nit')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

if (-not (Test-Path $exe)) { Say "SKIP FfmpegGui.exe 未构建（先 dotnet build -c Release --no-restore）"; exit 0 }
if (-not (Test-Path $ff))  { Say "SKIP 缺 ffmpeg（publish/PLAN/ffmpeg-full）"; exit 0 }
New-Item -ItemType Directory -Path $val -Force | Out-Null

# ── 素材：同一批像素，分别打 HLG / PQ 标签 ──
# 为什么用应用自己打标：本机 ffmpeg 的 PNG 编码器不写 cICP（实测），只有应用侧
# PngCicpService 会写 cICP 块。同时这也顺带锁住 CLI 的 token 规范化（见 verify-color-token-normalize.ps1）。
$plain = "$val/plain.png"
Run-Exe $ff "-y -v error -f lavfi -i `"testsrc2=s=192x128,format=rgb48be`" -frames:v 1 `"$plain`"" | Out-Null
$hdir = "$val/mk_hlg"; $pdir = "$val/mk_pq"
New-Item -ItemType Directory -Path $hdir, $pdir -Force | Out-Null
Run-Exe $exe "--headless -i `"$plain`" -o `"$hdir`" -f png -q 100 --color-primaries bt2020 --color-trc arib-std-b67 --log-level Info" | Out-Null
Run-Exe $exe "--headless -i `"$plain`" -o `"$pdir`" -f png -q 100 --color-primaries bt2020 --color-trc smpte2084 --log-level Info" | Out-Null
$hsrc = "$hdir/plain.png"; $psrc = "$pdir/plain.png"

Say "[0] 素材自检（原「HLG 未触发」结论的直接回归锁）"
if (-not (Test-Path $hsrc) -or -not (Test-Path $psrc)) {
    $fail++; Say "  FAIL 素材生成失败（$hsrc / $psrc 缺失）"
}
else {
    $mh = Get-Md5 $hsrc; $mp = Get-Md5 $psrc
    CK ($mh -ne $mp) "HLG 源与 PQ 源**不是同一个文件**（md5 $($mh.Substring(0,8))… vs $($mp.Substring(0,8))…）"
    $th = Get-Transfer $hsrc; $tp = Get-Transfer $psrc
    CK ($th -eq 'arib-std-b67') "HLG 源的 color_transfer=$th（期望 arib-std-b67；unknown 即素材没打上标）"
    CK ($tp -eq 'smpte2084')    "PQ  源的 color_transfer=$tp（期望 smpte2084）"
}

Say "[1] 接线：HLG/PQ → SDR 走色彩引擎"
$od1 = "$val/hlg2sdr"; $od2 = "$val/pq2sdr"; $od3 = "$val/hlg2sdr_b"
New-Item -ItemType Directory -Path $od1, $od2, $od3 -Force | Out-Null
$log1 = "$val/hlg2sdr.log"; $log2 = "$val/pq2sdr.log"
Run-Exe $exe "--headless -i `"$hsrc`" -o `"$od1`" -f png -q 100 --color-engine engine --color-tone-map hable --log-level Debug --log-file `"$log1`"" | Out-Null
Run-Exe $exe "--headless -i `"$psrc`" -o `"$od2`" -f png -q 100 --color-engine engine --color-tone-map hable --log-level Debug --log-file `"$log2`"" | Out-Null
$o1 = "$od1/plain.png"; $o2 = "$od2/plain.png"
CK (Test-Path $o1) "HLG→SDR 产出成功"
CK (Test-Path $o2) "PQ →SDR 产出成功"
if ((Test-Path $o1) -and (Test-Path $o2)) {
    $t1 = if (Test-Path $log1) { [System.IO.File]::ReadAllText($log1) } else { '' }
    CK ($t1 -match '决策：Map') "HLG 决策走 Map 分支（非静默直通）"
    CK ($t1 -match '后端=H2')   "HLG 由 H2(zscale) 后端执行"
    $pk1 = Get-MeasuredPeak $log1; $pk2 = Get-MeasuredPeak $log2
    # HLG 名义峰值 1000nit、PQ 10000nit ⇒ 实测峰值不同即证明两条曲线的 EOTF 各自生效
    # （若 HLG 被当 PQ 解码，同像素会得到同一个峰值）。
    CK ($pk1 -and $pk2 -and $pk1 -ne $pk2) "HLG/PQ 实测峰值不同（$pk1 nit vs $pk2 nit）⇒ 各自曲线生效"
    $ps = Get-Psnr $o1 $o2
    CK ($ps -ne '' -and $ps -ne 'inf') "HLG→SDR 与 PQ→SDR 产物**不同**（PSNR=$ps，原缺陷为 inf）"
    $num = 0.0
    if ([double]::TryParse($ps, [ref]$num)) { CK ($num -lt 40.0) "PSNR 显著低于 40dB（$ps）⇒ 差异是实质性的，非编码噪声" }
    else { $fail++; Say "  FAIL PSNR 无法解析（$ps）" }
}

Say "[2] 控制组（有牙）+ 可用性诚实"
if ((Test-Path $o1)) {
    # 同一源跑两次 ⇒ 必须逐位一致。证明第 1 节的 PSNR 判据能识别"逐位一致"，
    # 否则第 1 节的"PSNR 有限"可能是判据本身永远报有限，属空跑。
    Run-Exe $exe "--headless -i `"$hsrc`" -o `"$od3`" -f png -q 100 --color-engine engine --color-tone-map hable --log-level Info" | Out-Null
    $o3 = "$od3/plain.png"
    if (Test-Path $o3) {
        $psSame = Get-Psnr $o1 $o3
        CK ($psSame -eq 'inf') "控制组：同一 HLG 源跑两次 PSNR=$psSame（逐位一致）⇒ 第 1 节判据非空跑"
    }
    else { $fail++; Say "  FAIL 控制组未产出" }
}
# 无标签(SDR)源 + tone-map ⇒ 必须明确报错（可用性诚实：不静默直通、不静默改像素）
$od4 = "$val/notag"; New-Item -ItemType Directory -Path $od4 -Force | Out-Null
$r4 = Run-Exe $exe "--headless -i `"$plain`" -o `"$od4`" -f png -q 100 --color-engine engine --color-tone-map hable --log-level Debug"
$o4 = "$od4/plain.png"
CK (-not (Test-Path $o4)) "无标签(SDR)源 + tone-map **不产出**产物（不静默直通）"
CK ($r4 -match '需要 HDR 源') "报错文本明确指出「需要 HDR 源」（可用性诚实）"

Say "`n===== hlg-route: PASS=$pass FAIL=$fail ====="
if ($fail -eq 0) { Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue }
else { Say "工作目录保留供排查: $val" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
