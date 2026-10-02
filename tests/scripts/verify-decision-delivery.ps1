# verify-decision-delivery.ps1 —— 步骤 3（单一裁决点）的**交付端**门禁：跑真实产品 CLI，验“宣称 == 产物里真的有的东西”。
#
# 为什么还要这一条（`ServiceProbe decision` 不够用的地方）：
#   decision 只对账“宣称值 vs 命令行”，而命令行 ≠ 交付物。本仓库实测过两类命令行没问题、产物却错的情况：
#     ① **libwebp 编码器丢弃 iccgen 生成的 ICC**（产物 RIFF 内没有 ICCP 块）⇒ 广色域像素 + 零标注 = 查看器按 sRGB 误读；
#     ② 出口语义写错时，PNG 的 cICP 会与像素相反（HDR 已 tonemap 成 SDR 却仍标 PQ）。
#   所以这里逐项读**产物**（exiftool 量 ICC 色度 / ffprobe 读 cICP），断言它与裁决点的出口语义一致。
#
# 2026-09-14 实测纠正（影响下面两条断言的写法）：本工具链 ffmpeg-full 2026.9.2 的 **PNG 编码器已经会自己写 cICP**
#   （tonemap 后产物 rgb24 内含 cICP=bt709/bt709；16-bit HDR 直通产物内含 cICP=bt2020/smpte2084）
#   ⇒ QueueProcessor 的 PngCicpService 后置补写退化为**兜底**（已有 cICP 时按 SkipAlreadyLabeled 不动它），
#   旧注释“ffmpeg 的 PNG 编码器不写 cICP”只对早期构建成立。因此本门禁**不断言“谁写的”**，
#   只断言“产物里的标签 == 出口语义”——那才是用户会看到的东西。
#
# 判据全部按度量（色度 + 曲线），不按 ICC 描述文字：本仓库生成的标准 ICC 描述恒为 "RGB built-in"。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$ex = "$root/publish/PLAN/exiftool/exiftool.exe"
foreach ($need in @($exe, $ff, $fp, $ex)) { if (-not (Test-Path $need)) { Write-Output "缺工具：$need"; exit 1 } }
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"

$val = "$root/tests/output/validate"; $col = "$val/../results/color"
$out = "$val/decision-delivery"
# ⚠ `-EA SilentlyContinue` 是**环境必需**（2026-09-17）：本宿主的批量删除守卫在累计删除量超过阈值时
#   会往脚本上下文里抛 `[safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED]`（**删除其实已经成功**，
#   见 `docs/HANDOVER.md` 的宿主限制一节）⇒ 不加这句会让门禁**跑到一半崩掉**、报不出 PASS/FAIL 汇总。
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}
# ⚠⚠ **只读 stdout 的取数**（读「工具写在 stdout 的值」时唯一合法的尺；形状与 `Exec` 一致，只换返回口径）
#   本文件的章程是「**宣称 == 产物里真有的东西**」⇒ 读产物的那把尺必须只回"工具说的值"。
#   `Exec` 合并 stderr 只为**值行确实只在 stderr** 的那几处保留（ffmpeg 的 `psnr` 汇总行 `average: xx dB`、
#   signalstats 的 `lavfi.signalstats.YMIN=`/`SATAVG=` 行 —— 实测 stdout 0 字节、值只在 stderr）。
#   ⚠ 但合并版同时吃进两类噪声（2026-09-27 实测）：本机 `LANG`/`LC_ALL` 不被识别时 ffmpeg/ffprobe 会往
#   stderr 吐 locale 警告，且 ffprobe **不带 `-v error` 时**还会吐整段版本横幅（实测 4838 B）⇒
#   凡是拿合并结果做 `-notmatch`（"日志里不许有 X"）的判据都因此变脆（噪声在场时"不含 X"可能只是没读到）。
#   用它读 ffprobe 的 `-of csv` 字段（值在 **stdout**）更是直接把噪声端进"值"里：随包 exiftool 是 perl
#   打包版，环境带着本机 perl 不认的 LC_ALL/LANG 时每次调用先喷 245 B locale warning ⇒ 下面这类
#   `[int](...)` 强转、`-split ','` 逐字段对账既可能**假红**（多一行警告），也可能**假绿**（同污互比）。
#   ⇒ 本轮把这 5 处**取值**站点换成 `ExecOut`；`RunCli`/`AlphaMin`/`$sat` 那几处**读日志全集**的合并保持不动
#     （判据吃的是 stderr 的告警全集），其"负断言无同址正向锁"的口子按主循环口径**登记、不自行改判据**。
#   本文件另有 `ExifTool`/`Cicp` 两个只读 stdout 的老 helper，它们口径正确但**条目固定**
#   （读不了 count_frames / pix_fmt）⇒ 通用版就是这个 `ExecOut`。
function ExecOut([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  $t = [System.IO.File]::ReadAllText($o)
  return $t
}

# ffprobe csv 的**数值**读数 → 逐行转成 [int[]]；空、或夹了非纯数字行 ⇒ 返回 $null（读不出就叫红，不猜）。
#   为什么必须逐行：动图产物常是**多流**（颜色 + gray alpha），于是
#     `-join ''`  把两行拼成 "1010"（帧数读成千位，不抛错）
#     `-join ','` 再 `[int]` 在 zh-CN 文化下把 "1,1" 读成 **11**（逗号是千分位，同样不抛错）
#   两种都是**静默错读数**，实测于本文件 ⑩/⑪（2026-09-27）。⇒ 取值一律走这里，比较前先看是否 $null。
function ProbeInts([string]$raw) {
  $lines = @(($raw -split "`r?`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
  if ($lines.Count -eq 0) { return $null }
  $vals = @()
  foreach ($l in $lines) {
    if ($l -match '^-?\d+$') { $vals += [int]$l } else { return $null }
  }
  return ,$vals
}

$pass = 0; $fail = 0; $skip = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }
# ⚠ 2026-09-21（`TESTING.md` 第 81 条同族）：**SKIP 必须进汇总并计数**（第 76 条口径）。
#   原先这几处是**裸 `Write-Output "  SKIP …"`** —— 真的跳过断言、却**不进汇总**
#   ⇒ 汇总行 `pass=51 fail=0` 看不出少了几条（与 `verify-color-peak` 的 (d) 段同一病灶）。
function SKIP([string]$what, [int]$n = 1) {
    $script:skip += $n
    Write-Output ("  SKIP ($n 条) " + $what)
}

function RunCli([string]$tag, [string]$inFile, [string]$fmt, [string[]]$extra) {
    # --log-level Debug：成功项的 item.Log 默认不回显（只有失败才转储），不写这句日志类断言会假失败。
    # ⚠ 含空格的值（如 "Display P3"）必须自己带引号：Start-Process -ArgumentList 数组是**拼接后**再分词，
    #    不加引号会把 `--color-space "Display P3"` 拆成两个参数（实测踩过）。
    $quoted = @($extra | ForEach-Object { if ($_ -match '\s' -and $_ -notmatch '^"') { "`"$_`"" } else { $_ } })
    $a = @('--headless', '-i', "`"$inFile`"", '--format', $fmt, '--output', "`"$out/$tag`"", '--log-level', 'Debug') + $quoted
    $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput "$out/$tag.o" -RedirectStandardError "$out/$tag.e"
    $log = [System.IO.File]::ReadAllText("$out/$tag.o") + [System.IO.File]::ReadAllText("$out/$tag.e")
    $f = Get-ChildItem -Recurse "$out/$tag" -Include *.webp, *.png, *.jpg, *.jpeg, *.tif, *.tiff, *.jxl -EA SilentlyContinue |
         Select-Object -First 1
    return @{ code = $p.ExitCode; log = $log; file = $f }
}
function ExifTool([string]$path, [string[]]$args2) {
    $o = "$out/_ex2.txt"
    Start-Process -FilePath $ex -ArgumentList ($args2 + @($path)) -NoNewWindow -Wait -RedirectStandardOutput $o | Out-Null
    return [System.IO.File]::ReadAllText($o)
}
function Cicp([string]$path) {
    $o = "$out/_cicp.txt"
    Start-Process -FilePath $fp -ArgumentList @('-v', 'error', '-select_streams', 'v:0', '-show_entries',
        'stream=color_primaries,color_transfer', '-of', 'csv=p=0', $path) -NoNewWindow -Wait -RedirectStandardOutput $o | Out-Null
    return ([System.IO.File]::ReadAllText($o)).Trim()
}
# 产物是否真的带 ICC（exiftool 能读回 ICC 才叫“交付了色彩描述”）
function HasIcc([string]$path) {
    $t = ExifTool $path @('-s', '-ICC_Profile:ProfileClass')
    return ($t -match 'Display Device Profile')
}
function IccRedChroma([string]$path) {
    $t = ExifTool $path @('-a', '-s', '-ICC_Profile:ChromaticityChannel1')
    if ($t -match '([0-9.]+)\s+([0-9.]+)') { return @([double]$matches[1], [double]$matches[2]) }
    return $null
}
# 读产物左上角 1px 的**存储值**（用于两条路径对账：同一内容不同路径必须落在同一套像素上）
# ⚠ 两个实测必需口径：滤镜顺序必须 `format=rgb24` 在 `crop` 前（先裁到 1×1 再从 yuv420p 转 RGB 会输出 0 字节）；
#   输出文件名必须显式 `-f rawvideo`（.raw 后缀 ffmpeg 认不出 muxer）。
function Px([string]$path) {
    $raw = "$out/_px.raw"
    Start-Process -FilePath $ff -ArgumentList @('-y', '-hide_banner', '-loglevel', 'error', '-i', "`"$path`"",
        '-vf', 'format=rgb24,crop=1:1:0:0', '-frames:v', '1', '-f', 'rawvideo', "`"$raw`"") -NoNewWindow -Wait | Out-Null
    if (-not (Test-Path $raw)) { return $null }
    $b = [System.IO.File]::ReadAllBytes($raw); Remove-Item $raw -Force -ErrorAction SilentlyContinue
    if ($b.Length -lt 3) { return $null }
    return "$($b[0]),$($b[1]),$($b[2])"
}
# ── 全帧 PSNR：把**产物**与「参照帧」逐像素比（判据用像素，不用命令行）──────────────────
# 用途见第 ⑤ 节：证明「产物像素真的落在目标色域」，而不是只看它贴了什么标签。
# 两个滤镜图（都用 zscale 作独立参照口径，与本仓库既有互测一致）：
#   $graphSame —— 参照 = 源**按 sRGB 原样解**（像素没动时该 PSNR 会很高）
#   $graphP3   —— 参照 = 源经标准 sRGB→Display P3 变换（像素真转 P3 时该 PSNR 高）
# ⚠ zscale 的 `min`/`m` 只认 `gbr`（`rgb` 会被当成表达式 ⇒ `Invalid argument`，实测踩过）；
#   且 zscale 输出是 **gbrp 平面序**，末尾必须再 `format=rgb24` 才能与产物同序比较（否则三通道错位）。
$graphSame = "format=rgb24[a];[1:v]format=rgb24[b];[a][b]psnr"
$graphP3   = "format=rgb24[a];[1:v]format=rgb24,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:p=smpte432:t=iec61966-2-1:m=gbr,format=rgb24[b];[a][b]psnr"
function PsnrTwo([string]$prod, [string]$ref, [string]$graph) {
    $o = Exec $ff "-hide_banner -i `"$prod`" -i `"$ref`" -lavfi `"$graph`" -f null -"
    if ($o -match 'average:\s*inf') { return 99.0 }          # 逐像素完全相同 ⇒ ffmpeg 报 inf
    $m = [regex]::Match($o, 'average:\s*([0-9.]+)')
    if ($m.Success) { return [double]$m.Groups[1].Value }
    return -1.0                                              # 解析不到 ⇒ 判红（不给假绿）
}

$sdr = "$val/sdr_plain.png"; $p3 = "$col/a2_srgb_p3.png"; $hdr = "$val/src_hdr.png"
foreach ($need in @($sdr, $p3, $hdr)) { if (-not (Test-Path $need)) { Write-Output "缺素材 $need（先跑 ensure-fixtures.ps1）"; exit 1 } }

# ── ① P3 源 → WebP（默认「推荐」策略 + auto 目标）───────────────────────
# 裁决点：像素不动、出口语义 = smpte432/iec61966-2-1；容器无 CICP 且 libwebp 丢 ICC
# ⇒ NeedsPostEmbedIcc=true，队列必须后置嵌入同一张 ICC。
$r = RunCli 'p3_webp' $p3 'webp' @()
if (-not $r.file) { CK $false "p3/webp 未产出（日志尾：$($(($r.log -split "`n") | Select-Object -Last 3) -join ' | ')）" }
else {
    $f = $r.file.FullName
    CK (HasIcc $f) "p3/webp 产物真的带 ICC（libwebp 会丢弃 iccgen 的 ICC ⇒ 必须由裁决点触发后置嵌入）"
    $c = IccRedChroma $f
    if ($c) {
        # Display P3 红原色 x=0.680；sRGB=0.640；BT.2020=0.708 —— 三者必须能区分开
        CK ([math]::Abs($c[0] - 0.680) -lt 0.005) "p3/webp 的 ICC 量出来就是 Display P3（红 x=$($c[0])，非 sRGB 0.640/非 2020 0.708）"
    } else { CK $false "p3/webp 的 ICC 色度读不出（后置嵌入的 ICC 无效？）" }
    # 出口语义必须是 P3：无标注的 webp 会被按 sRGB 解读 ⇒ 上面两条就是全部证据
    CK ($r.log -match '已嵌入出口语义 ICC|已嵌入目标 ICC') "p3/webp 日志写明后置嵌入这一步真的发生了"
}

# ── ② sRGB 源 → WebP：出口语义 = sRGB ⇒ 未标注即正确，不该多嵌一张无用 ICC ──
$r2 = RunCli 'sdr_webp' $sdr 'webp' @()
if (-not $r2.file) { CK $false "sdr/webp 未产出" }
else {
    CK (-not (HasIcc $r2.file.FullName)) "sdr/webp 产物**不**带 ICC（未标注即 sRGB，嵌了是无用的重复描述）"
    CK ($r2.log -notmatch '已嵌入出口语义 ICC') "sdr/webp 日志里没有后置嵌入（判据是出口语义而非“是不是 webp”）"
}

# ── ③ HDR 源（8-bit PQ）→ PNG：像素已 tonemap 成 SDR ⇒ 标签必须跟着变 SDR ──
# 这是步骤 3 的靶子：旧实现宣称 bt2020/smpte2084，而命令行末级 zscale 是 p=bt709:t=bt709。
$r3 = RunCli 'hdr_png8' $hdr 'png' @('--bit-depth', '8')
if (-not $r3.file) { CK $false "hdr/png8 未产出（日志尾：$($(($r3.log -split "`n") | Select-Object -Last 3) -join ' | ')）" }
else {
    $f3 = $r3.file.FullName
    CK ($r3.log -match 'tonemap') "hdr/png8 命令行确实跑了 tonemap（末端像素已是 SDR）"
    $tag3 = Cicp $f3
    # ffprobe 读 PNG 的 cICP（无 cICP 时返回空/unknown）：绝不能仍是 2020/smpte2084
    CK ($tag3 -notmatch 'smpte2084') "hdr/png8 产物未宣称 PQ（实=$tag3）——像素已 tonemap 成 SDR，宣称 HDR 就是标签与像素相反"
    CK ($tag3 -notmatch 'bt2020') "hdr/png8 产物未宣称 BT.2020 原色（实=$tag3）"
    CK ($tag3 -match 'bt709') "hdr/png8 产物 cICP = bt709/bt709，与末级 zscale 一致（实=$tag3）"
    CK ($r3.log -notmatch 'cICP chunk 写入失败|未写 cICP') "hdr/png8 无 cICP 写入失败/降级告警（已有标签时兜底不重写）"
}

# ── ④ HDR 源 → JPEG（SDR-only 容器）：同一判据在另一容器上成立 ──
$r4 = RunCli 'hdr_jpg8' $hdr 'jpg' @('--bit-depth', '8')
if (-not $r4.file) { CK $false "hdr/jpg8 未产出" }
else {
    $t4 = IccRedChroma $r4.file.FullName
    if ($t4) {
        # JPEG 靠 ICC 描述色彩：出口语义是 SDR bt709/sRGB ⇒ 附的必须是 sRGB 的 ICC（红 x=0.640）
        CK ([math]::Abs($t4[0] - 0.640) -lt 0.005) "hdr/jpg8 附的 ICC 是 sRGB（红 x=$($t4[0])），与 tonemap 后的像素一致"
    } else { CK $false "hdr/jpg8 读不到 ICC 色度（iccgen 未落盘？）" }
}

# ── ⑤ sRGB 源 → WebP + 显式 P3 目标：旧能力表把 webp 钳成 bt709 ⇒ 用户要 P3 而像素根未转（伪成功）──
# 现在不钳 primaries（WebP 有 ICCP 块）⇒ 像素真的转 P3，且因 libwebp 丢 iccgen 而必须后置嵌 P3 ICC。
# ⚠ 判据在 2026-09-17 由「命令行匹配」改为「像素/色度度量」（旧断言 `log -match 'zscale=…p=smpte432'`
#   描述的是**传统管线 H2 形态**；而本格在默认走引擎的架构下由 **H1 进程内内核**完成像素变换，
#   `RawColorPipeline.TransformFileAsync` 不生成任何 zscale 滤镜 ⇒ 旧断言在现行架构下不可满足，
#   它会把一个**已兑现**的格判红）。新判据只看**产物**：把源按标准 sRGB→Display P3 变换后与交付
#   像素做全帧 PSNR。实测分离度：像素真转 P3 ⇒ ≈38.2dB；只贴 P3 标签而像素没动 ⇒ ≈14.1dB
#   （后者与「源 vs 源→P3」的 14.07dB 完全一致）。
#   判据取**两者的差**（相对，≥15dB）而非绝对值 —— 理由与实测见下方第 175-178 行注释。
$r5 = RunCli 'p3target_webp' $sdr 'webp' @('--color-space', 'Display P3')
if (-not $r5.file) { CK $false "P3目标/webp 未产出（日志尾：$($(($r5.log -split "`n") | Select-Object -Last 3) -join ' | ')）" }
else {
    $p3Psnr   = PsnrTwo $r5.file.FullName $sdr $graphP3
    $noopPsnr = PsnrTwo $r5.file.FullName $sdr $graphSame
    # ⚠ 判据用**相对差**（2026-09-17 由绝对阈值 30 dB 改来）：绝对阈值与 WebP 的**有损编码质量**
    #   绑定 —— 将来若默认质量下调，$p3Psnr 会跟着掉，真绿会转红。相对判据自校准：
    #   像素真转 P3 ⇒ 「按 P3 解」远好于「按 sRGB 解」；只贴标签不动像素 ⇒ 两者几乎相同。
    #   实测：38.196 − 14.146 = **24.05 dB**（余量 ≈9 dB）。两个绝对值同时回显，便于排查。
    $p3Gain = $p3Psnr - $noopPsnr
    CK ($p3Gain -ge 15.0) "P3目标/webp 的**像素**真的落在 Display P3 域（按 P3 解 = $p3Psnr dB；按 sRGB 解 = $noopPsnr dB；差 $p3Gain dB ≥ 15）"
    $c5 = IccRedChroma $r5.file.FullName
    if ($c5) { CK ([math]::Abs($c5[0] - 0.680) -lt 0.005) "P3目标/webp 附的 ICC 量出来是 Display P3（红 x=$($c5[0])）" }
    else { CK $false "P3目标/webp 读不到 ICC 色度 ⇒ P3 像素会被按 sRGB 误读" }
}

# ── ⑥ HDR 源 → WebP：tonemap 后出口语义 = sRGB ⇒ 无标注即正确，不得多嵌 ICC ──
$r6 = RunCli 'hdr_webp' $hdr 'webp' @()
if (-not $r6.file) { CK $false "hdr/webp 未产出" }
else {
    CK ($r6.log -match 'tonemap') "hdr/webp 跑了 tonemap（WebP 无 HDR 标定通路）"
    CK (-not (HasIcc $r6.file.FullName)) "hdr/webp 不带 ICC（出口已是 sRGB，嵌 ICC 属无用重复）"
}

# ── ⑦ P3 源 → WebP 走**专用通路**（JXL 输入）：色彩后置不得只在主分支生效（P7a-3）──
# 先现造一个带 P3 ICC 的 .jxl（让任务真的落在 ProcessJxlInputAsync 分支，而不是主分支）。
$jxl = "$out/src_p3.jxl"
Start-Process -FilePath $ff -ArgumentList @('-y','-hide_banner','-loglevel','error','-i',$p3,'-c:v','libjxl','-distance','0',$jxl) `
    -NoNewWindow -Wait -RedirectStandardError "$out/_jxl.e" | Out-Null
if (-not (Test-Path $jxl)) { CK $false "无法生成 P3 .jxl 素材（本构建无 libjxl？跳过专用通路用例）" }
else {
    $r7 = RunCli 'p3jxl_webp' $jxl 'webp' @()
    if (-not $r7.file) { CK $false "P3 JXL→webp 未产出（日志尾：$($(($r7.log -split "`n") | Select-Object -Last 3) -join ' | ')）" }
    else {
        $c7 = IccRedChroma $r7.file.FullName
        # 交付端事实：专用通路也必须把 ICC 补上（或至少显式报告它没补）——绝不能静默交个无标注的 P3 webp
        CK ($c7 -and [math]::Abs($c7[0] - 0.680) -lt 0.005) "P3 JXL→webp 产物带 Display P3 ICC（红 x=$($c7[0])）"
        CK ($r7.log -match '已嵌入出口语义 ICC|\[色彩后置\] ⚠') "P3 JXL→webp 日志要么真补了 ICC、要么显式告警（不静默）"
    }
}

# ── ⑫ 专用通路必须吃到**全套**后置（P7a-3 第三轮：入口只有一份）──
# 抽公共入口之前的实测缺口：链尾只补“ICC 被容器编码器丢弃”那一步，`[tiff] 目标色彩空间 ICC`、
# PNG sBIT、cICP、保真 ICC 四步是主分支专属 ⇒ 同一条判据在不同通路上结果不同。
# 这里现造一个**无 ICC 的 sRGB .jxl**（确实落在 djxl→ffmpeg 管道那条登记通路上），目标显式 P3。
if (-not (Test-Path $jxl)) { Write-Output "  SKIP ⑫：无 .jxl 生成条件（本构建无 libjxl？）⇒ 未评估" }
else {
    # ⚠ 对照组必须“源无 ICC”：若源本来就带 P3 ICC，元数据恢复回带的源 ICC 恰好等于目标 ICC，
    #   缺陷会被掩盖（实测：P3 源→P3 目标 的前后两态都能量到 0.680）。
    $srgbSrc = "$out/srgb_src.png"; $srgbJxl = "$out/srgb_src.jxl"
    Start-Process -FilePath $ff -ArgumentList @('-y','-f','lavfi','-i','mandelbrot=size=320x240:rate=10',
        '-frames:v','1','-pix_fmt','rgb24',$srgbSrc) -NoNewWindow -Wait | Out-Null
    Start-Process -FilePath $ff -ArgumentList @('-y','-i',$srgbSrc,'-c:v','libjxl','-distance','0',$srgbJxl) `
        -NoNewWindow -Wait -RedirectStandardError "$out/_jxl2.e" | Out-Null
    if (-not (Test-Path $srgbJxl)) { SKIP "⑫ 外层：无 ICC 的 sRGB .jxl 造不出来 ⇒ 未评估（以下 4 条断言未运行，不是通过）" 4 }
    else {
        CK ($null -eq (IccRedChroma $srgbJxl)) "⑫ 对照组：源 .jxl 自身没有 ICC ⇒ 产物 ICC 不可能来自“回带源 ICC”（还须看下面两步：是否真跑了目标 ICC、落下来的是否为目标色域）"
        $r12 = RunCli 'srgbjxl_tiff' $srgbJxl 'tiff' @('--color-space', 'Display P3')
        if (-not $r12.file) {
            SKIP "⑫ 内层：JXL→TIFF 未产出（$($(($r12.log -split "`n") | Select-Object -Last 2) -join ' | ')）⇒ 未评估（以下 3 条断言未运行）" 3
        } else {
            CK ($r12.log -notmatch '裁决点未参与') "⑫ 走的确实是**已登记**通路（否则链尾不该做任何色彩后置）"
            CK ($r12.log -match '\[tiff\] 已嵌入目标色彩空间 ICC') "⑫ 链尾交付了 TIFF 目标 ICC 这一步（抽公共入口前它只在主分支跑 ⇒ 判据两份）"
            $c12 = IccRedChroma $r12.file.FullName
            CK ($c12 -and [math]::Abs($c12[0] - 0.680) -lt 0.005) "⑫ 按度量复核：TIFF 内 ICC 确实是 Display P3（红 x=$($c12[0])）"
        }
    }
}


# ── ⑨ 两条路径的像素一致性（P7d 回归锁）──
# 同一份 Display P3 内容，走主分支（p3.png→webp）与走 JXL 专用通路（p3.jxl→webp，djxl→ffmpeg 管道）
# 必须交付**同一套像素值**。这一条当初确实是红的（94,22,238 vs 116,16,237）：
# 管道输入是 stdin `-` ⇒ 位深/alpha 探测必然失败 ⇒ `-pix_fmt` 被静默跳过 ⇒ 编码器自选像素格式。
# 修法是 BuildArguments(..., probeInputPath)：stdin 时用真实源文件做探测替代。
# ⚠ 对照要拿 CLI 的真实命令做：不写 `-c:v`（webp muxer 默认选 **libwebp_anim**）。
#   手敲时多加一个 `-c:v libwebp` 就会得到 97,23,247——那是对照伪影，不是产品缺陷（踩过）。
if ($r.file -and $r7.file) {
    $pa = Px $r.file.FullName; $pb = Px $r7.file.FullName
    if (-not $pa -or -not $pb) { CK $false "两条路径的像素读不出（主=$pa 专用=$pb）——**不得当通过**" }
    else { CK ($pa -eq $pb) "同一内容的 webp：两条路径像素一致（主分支=$pa ｜ JXL管道=$pb）——P7d 回归锁" }
} else {
    CK $false "⑨ 无法执行（缺 ①/⑦ 产物）"
}

# ── ⑩ 未登记的通路必须“裁决点未参与”，绝不赌标签（P7a-3 同源登记的背面）──
# 动画 AVIF → WebP 走 ProcessAvifToGifWebpAsync 两步法：它的命令行是自建 filter_complex，
# 不是裁决点产的 ⇒ 拿“按源文件推断的出口语义”去给它嵌 ICC 就是在赌（P7d 证明赌不赢）。
# 允许的唯一行为：显式记录“未参与”；偷偷嵌了或静默都不行。
# ⚠ 素材必须是**真 ≥2 帧动画**（2026-09-27 实测教训）：本段断言的是"选流没退化成单帧副本"，
#   而 `results/` 是**共享产物目录** —— 色彩矩阵跑完会往里放**静帧** `color/d2_p3.avif`(>3 KB)，
#   旧写法"枚举到的第一个算谁是谁"于是随别的门禁跑没跑而翻转 ⇒ 本段 4 条红全与产品无关（假红，
#   且 `裁决点未参与`/`没嵌 ICC` 两条负断言也会被静帧的不同分支翻转）。
#   ⇒ 逐候选量帧数、只接受 ≥2 帧者，并把选中的素材打进日志（读数可追溯）。
$avifCands = @(Get-ChildItem "$root/tests/output/results" -Recurse -Include *.avif -EA SilentlyContinue |
               Where-Object { $_.Length -gt 3000 })
$avif = $null; $avifFrames = -1
foreach ($c in $avifCands) {
    $cn = ProbeInts (ExecOut $fp "-v error -select_streams v -count_frames -show_entries stream=nb_read_frames -of csv=p=0 `"$($c.FullName)`"")
    $cm = if ($null -eq $cn) { -1 } else { (@($cn) | Measure-Object -Maximum).Maximum }
    if ($cm -ge 2) { $avif = $c; $avifFrames = $cm; break }
}
if (-not $avif) { SKIP "⑩：$($avifCands.Count) 个候选 AVIF 里没有一个 ≥2 帧动画 ⇒ 素材不可得，未评估（不拿静帧冒充动画素材）" 6 }
else {
    Write-Output "  （⑩ 素材：$($avif.Name)，源最大流 $avifFrames 帧，候选 $($avifCands.Count) 个）"
    $r10 = RunCli 'avif_webp' $avif.FullName 'webp' @()
    if (-not $r10.file) {
        # 路径本身失败 ⇒ 本断言**无法评估**。绝不能因为“日志里没有嵌 ICC”就给绿灯——
        # 那是任务根本没跑到后置那一步，不是“未参与”分支在起作用（假绿）。
        $why = ((($r10.log -split "`n") | Where-Object { $_ -match '失败|Error' } | Select-Object -First 1) -as [string]).Trim()
        SKIP "⑩：AVIF→webp 两步法自身失败（$why）⇒ 未参与分支未被执行到（已记为待查项 P7e，不计入绿；以下 6 条断言未运行）" 6
    } else {
        CK ($r10.log -match '裁决点未参与') "AVIF→webp（命令行非裁决点产出）显式记录‘裁决点未参与’，不静默"
        CK ($r10.log -notmatch '已嵌入出口语义 ICC') "AVIF→webp 没有赌一把去嵌 ICC（日志实证）"
        # ── P7e 回归锁：选流不能退化成“挑首帧副本”或“挑 alpha 平面”──
        # 产物必须是**多帧动画**且是**彩色**（帧数对齐源、饱和度 > 10）。
        # 实测基准：旧硬编码 `-map 0:2` 在该素材上直接 -22；而 0:0 是 1 帧首图副本，选错就只会得到单帧。
        $o10 = "$out/avif_webp"; $f10 = $r10.file.FullName
        # ⚠ 帧数取自 ffprobe 的 `-of csv`（实测：`-v error` 下值**全在 stdout**、stderr 0 字节 ⇒ 与合并版同值，
        #   换尺不由绿转红）。用 `ExecOut` 是因为这两行都对返回值做数值加工：
        #   合并版把 stderr 的每一行都塞进元素里 ⇒ 转换噪声（本段转红）、
        #   `$srcFrames` 多出非数值元素。消费方 `$dstN -ge 2 -and $dstN -eq 源最大帧数` 是**正向**判据。
        # ⚠ 逐流一行 ⇒ 一律 `ProbeInts` 逐行转数后取**最大**；旧写法 `-join ''` + `[int]` 在源为两流时
        #   把 "10`n10" 读成 1010、把 "1,1" 读成 11，都不抛错（实测 2026-09-27）⇒ 帧数判据会静默失效。
        $srcFrameAll = ProbeInts (ExecOut $fp "-v error -select_streams v -count_frames -show_entries stream=nb_read_frames -of csv=p=0 `"$($avif.FullName)`"")
        $dstFrameAll  = ProbeInts (ExecOut $fp "-v error -select_streams v -count_frames -show_entries stream=nb_read_frames -of csv=p=0 `"$f10`"")
        if ($null -eq $srcFrameAll -or $null -eq $dstFrameAll) {
            CK $false "P7e：帧数读数器读不出逐流帧数（源=$($srcFrameAll -join '/')，产物=$($dstFrameAll -join '/')）⇒ 判据失效，不给绿"
            $srcMaxN = -1; $dstN = -1
        } else {
            # 读数器自检：给两行同值输入，必须解析成**两条**、且最大值 == 单条值。
            # 旧尺（`-join ''` + `[int]`）在这一格会给出 1010、(`-join ','` + `[int]`) 在 zh-CN 给出 11
            # ⇒ 这一条是**判据有牙**的证明，不是恒等式（它专门在"修复前的写法"上必红）。
            $tooth = ProbeInts "10`n10"
            $toothN = @($tooth).Count; $toothMax = (@($tooth) | Measure-Object -Maximum).Maximum
            CK ($toothN -eq 2 -and $toothMax -eq 10) `
               "读数器自检：多行同值输入 → $toothN 条、最大 $toothMax（旧尺读成 1010/11）"
            $srcMaxN = (@($srcFrameAll) | Measure-Object -Maximum).Maximum
            $dstN = (@($dstFrameAll) | Measure-Object -Maximum).Maximum
            Write-Output "  （源逐流帧数：$($srcFrameAll -join ',')，共 $(@($srcFrameAll).Count) 条）"
            # 独立尺对照：同一份读数改走 **json** 序列化（不同解析器）⇒ 两条独立实现必须给同一个最大值。
            # 只比"最大"这一维：素材哪天变成单流也照样成立，不会造出与产品无关的假红。
            $jsRaw = ExecOut $fp "-v error -select_streams v -count_frames -show_entries stream=nb_read_frames -of json `"$($avif.FullName)`""
            $jsN = @()
            try { $jsN = @((ConvertFrom-Json $jsRaw).streams | ForEach-Object { [int]$_.nb_read_frames }) } catch { $jsN = @() }
            $jsMax = if ($jsN.Count) { (@($jsN) | Measure-Object -Maximum).Maximum } else { -1 }
            CK ($jsMax -eq $srcMaxN) "读数器独立尺对照：json 逐流 $(@($jsN).Count) 条最大 $jsMax == csv+ProbeInts 最大 $srcMaxN"
        }
        CK ($dstN -ge 2 -and $dstN -eq $srcMaxN) `
           "P7e：AVIF→webp 产物是 $dstN 帧动画（源最大流 $srcMaxN 帧）——选流未退化成单帧副本"
        $sat = (((Exec $ff "-hide_banner -i `"$f10`" -vf format=rgb24,signalstats,metadata=print:key=lavfi.signalstats.SATAVG -f null -") -split "`r?`n") | Select-String 'SATAVG=' | Select-Object -First 1) -as [string]
        $sv = if ($sat -match 'SATAVG=([0-9.]+)') { [double]$matches[1] } else { -1 }
        CK ($sv -gt 10) "P7e：产物是彩色（平均饱和度 SATAVG=$([math]::Round($sv,1))，非灰度 ⇒ 没把 alpha 平面当颜色输出）"
        CK ($r10.log -match '选流：颜色=0:\d+，alpha=(无|0:\d+)') "P7e：选流结果写进日志（可审计，不是默默猜定）"
    }
}

# ── ⑪ 动画 + 真透明的两步法（P7g/P7h 回归锁；⑩ 的素材只有 2 流无 alpha，覆盖不到 hasAlpha 分支）──
# 素材由**产品自己**生成：透明 GIF → GIF→AVIF(avifenc)。这一步同时是 P7h 的门：
# avifenc 1.4.2 实测 `-j 1` 且 ≥4 帧 ⇒ 段错误(-1073741819)，而旧代码 Math.Max(1,Threads=0) 恒传 -j 1。
$aa = "$out/animalpha"
New-Item -ItemType Directory -Force -Path "$aa/frm" | Out-Null
function FFRun([string[]]$a, [string]$tag) {
    $p = Start-Process -FilePath $ff -ArgumentList $a -NoNewWindow -Wait -PassThru -RedirectStandardError "$out/$tag.ff.err"
    return $p.ExitCode
}
function AlphaMin([string]$path) {
    $o = ((Exec $ff "-hide_banner -i `"$path`" -vf alphaextract,signalstats,metadata=print:key=lavfi.signalstats.YMIN -f null -") -split "`r?`n") |
         Select-String 'YMIN=(\d+)' | Select-Object -First 1
    if ($o -and $o.Line -match 'YMIN=(\d+)') { return [int]$matches[1] }
    return -1
}
# 注意：`drawbox=…:color=black@0.0` **画不出透明**（alpha=0 是 0% 混合，等于什么都没画，实测素材恒为 YMIN=255）；
# 透明必须由 geq 直接写 alpha 通道。逗号在滤镜图里是分隔符，须转义成 \, 。
$mk = FFRun @('-y','-f','lavfi','-i','mandelbrot=size=320x240:rate=10',
              '-vf',"format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='if(lt(X\,80)*gt(X\,19)*lt(Y\,80)*gt(Y\,19),0,255)'",
              '-pix_fmt','rgba','-frames:v','10',"$aa/frm/f_%03d.png") 'mk'
$mkGif = FFRun @('-y','-framerate','10','-i',"$aa/frm/f_%03d.png",
                 '-vf','format=rgba,split[a][b];[a]palettegen=reserve_transparent=1:stats_mode=full[p];[b][p]paletteuse=alpha_threshold=128',
                 '-loop','0',"$aa/trans.gif") 'mk2'
if ($mk -ne 0 -or $mkGif -ne 0 -or -not (Test-Path "$aa/trans.gif")) {
    SKIP "⑪：透明 GIF 素材生成失败（ffmpeg 退出 $mk/$mkGif）⇒ 未评估，不给绿（以下 9 条断言未运行）" 9
} else {
    # 对照组：素材自身必须真有透明。这一条不成立时后面全部不评估——否则“产物没透明”会被误当成产品缺陷。
    $gifAlpha = AlphaMin "$aa/trans.gif"
    CK ($gifAlpha -eq 0) "对照组：透明 GIF 素材自身 alpha YMIN=$gifAlpha（须为 0，否则本组断言无意义）"
    $g2a = Start-Process -FilePath $exe -ArgumentList @('--headless','-i',"$aa/trans.gif",'--format','avif','--output',"$aa/avif",'--log-level','Debug') `
            -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$aa/g2a.o" -RedirectStandardError "$aa/g2a.e"
    $g2aLog = [System.IO.File]::ReadAllText("$aa/g2a.o") + [System.IO.File]::ReadAllText("$aa/g2a.e")
    $madeAvif = Get-ChildItem "$aa/avif" -Filter *.avif -EA SilentlyContinue | Select-Object -First 1
    if ($g2aLog -match '段错误|退出码 -1073741819|access violation') {
        CK $false "P7h：GIF→AVIF 里 avifenc 崩溃（-1073741819）⇒ 又给 avifenc 传了 -j 1"
    } elseif (-not $madeAvif) {
        $why = ((($g2aLog -split "`n") | Where-Object { $_ -match '失败|未检测到|avifenc' } | Select-Object -First 1) -as [string]).Trim()
        SKIP "⑪：产品未能产出动画 AVIF（$why）⇒ 素材不可得，未评估（以下 8 条断言未运行）" 8
    } else {
        # 素材自检：动画（≥2 帧的颜色流）+ 存在 gray 流（alpha 平面），否则下面的断言无从谈起
        # ⚠ 取数只读 stdout（ffprobe csv 的值在 stdout）：下面按逐字段建表，
        #   合并版里多出来的 stderr 行会在这里变成"整表错位 + 一堆转换错误"。
        # ⚠ 行必须**逐条**按 `index,pix_fmt,nb_read_frames` 正则建：旧写法对任意行 `-split ','` 后取
        #   `$c[1]`/`$c[2]`，缺字段时得到 $null ⇒ `[int]$null` 静默变成 **0 帧的一行**（表错位却不报错）。
        $probe = @(((ExecOut $fp "-v error -select_streams v -count_frames -show_entries stream=index,pix_fmt,nb_read_frames -of csv=p=0 `"$($madeAvif.FullName)`"") -split "`r?`n") |
                   ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        $rows = @(); $rowBad = @()
        foreach ($ln in $probe) {
            if ($ln -match '^(\d+),([A-Za-z0-9_]+),(\d+)$') {
                $rows += @{ idx = [int]$matches[1]; pix = $matches[2]; n = [int]$matches[3] }
            } else { $rowBad += $ln }
        }
        CK ($rowBad.Count -eq 0) "读数器：源逐流表 $($rows.Count) 行行合法（被拒 $($rowBad.Count) 行：$(if ($rowBad.Count) { $rowBad -join ' | ' } else { '无' })）"
        # ⚠ 排序键必须写成 **scriptblock**（`{ $_.n }`），不能写属性名 `n`：
        #   本门禁由 `powershell.exe`（Windows PowerShell 5.1）执行，而 5.1 的 `Sort-Object <名字>`
        #   对 **Hashtable 的键**不做属性适配（PS 7 会）⇒ 拿不到键值、`-Descending` 也失效，
        #   排出来恒是原序（实测 5.1：`sorted=0,0,1,10`、`picked=0`；PS 7：`10,1,0,0`、`picked=10`）。
        #   后果是下面两条"素材自检"永远拿到 n=0 的空白行 ⇒ 恒红且与产品行为无关。
        $srcColor = ($rows | Where-Object { $_.pix -ne 'gray' } | Sort-Object { $_.n } -Descending | Select-Object -First 1)
        $srcAlpha = ($rows | Where-Object { $_.pix -eq 'gray' -and $_.n -eq $srcColor.n } | Select-Object -First 1)
        CK ($srcColor.n -ge 4) "素材：GIF→AVIF 产物有 $($srcColor.n) 帧颜色流（≥4，能触发 avifenc 的 -j 1 崩溃区）"
        CK ($null -ne $srcAlpha) "素材：AVIF 带同帧数的 gray alpha 平面 ⇒ 走 hasAlpha 分支"
        # 中间对照：透明是否在 GIF→AVIF 这一跳就丢了（gray 流本身就是 alpha 平面，直接解一帧量 YMIN）
        $gsFrame = "$aa/_gray0.png"
        if (Test-Path $gsFrame) { Remove-Item $gsFrame -ErrorAction SilentlyContinue }
        if ($null -ne $srcAlpha) {
            $null = Start-Process -FilePath $ff -ArgumentList @('-y','-hide_banner','-loglevel','error','-i',$madeAvif.FullName,'-map',"0:$($srcAlpha.idx)",'-frames:v','1',$gsFrame) -NoNewWindow -Wait -PassThru
            $gmin = -1
            if (Test-Path $gsFrame) {
                $o = ((Exec $ff "-hide_banner -i `"$gsFrame`" -vf signalstats,metadata=print:key=lavfi.signalstats.YMIN -f null -") -split "`r?`n") | Select-String 'YMIN=(\d+)' | Select-Object -First 1
                if ($o -and $o.Line -match 'YMIN=(\d+)') { $gmin = [int]$matches[1] }
            }
            CK ($gmin -lt 255) "对照组：GIF→AVIF 后 alpha 流仍有透明值（YMIN=$gmin）⇒ 后面的 webp 断言不是替上游背锅"
        }
        if ($null -eq $srcAlpha) { Write-Output "  SKIP ⑪：无 alpha 平面，后续未评估" }
        else {
            $a2w = Start-Process -FilePath $exe -ArgumentList @('--headless','-i',$madeAvif.FullName,'--format','webp','--output',"$aa/webp",'--log-level','Debug') `
                    -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$aa/a2w.o" -RedirectStandardError "$aa/a2w.e"
            $a2wLog = [System.IO.File]::ReadAllText("$aa/a2w.o") + [System.IO.File]::ReadAllText("$aa/a2w.e")
            $wf = Get-ChildItem "$aa/webp" -Filter *.webp -EA SilentlyContinue | Select-Object -First 1
            if (-not $wf) {
                $why = ((($a2wLog -split "`n") | Where-Object { $_ -match '失败|Error' } | Select-Object -First 1) -as [string]).Trim()
                Write-Output "  SKIP ⑪：AVIF→webp 自身失败（$why）⇒ hasAlpha 分支未被执行到，不给绿"
            } else {
                CK ($a2wLog -match '选流：颜色=0:\d+，alpha=0:\d+') "hasAlpha 分支：日志显示真选中了 alpha 流（不是走了无 alpha 的 else）"
                # ⚠ 帧数/像素格式都取自 ffprobe 的 stdout csv ⇒ `ExecOut`：这两条是 P7g 的**唯一**判据
                #   （日志与退出码都看不出序列号空洞），读数被 stderr 噪声污染就等于判据失效。
                # ⚠ 逐流多行 ⇒ 帧数走 `ProbeInts` 取最大（旧 `-join ''` 会把两行拼成 5 位数且**不报错**）、
                #   pix_fmt 走**逐流全查**（旧 `-join ''` 后 `-match` 会被 "yuva420p/yuv420p" 这种拼接糊过去）。
                $dstFrameAll = ProbeInts (ExecOut $fp "-v error -select_streams v -count_frames -show_entries stream=nb_read_frames -of csv=p=0 `"$($wf.FullName)`"")
                $dstN = if ($null -eq $dstFrameAll) { -1 } else { (@($dstFrameAll) | Measure-Object -Maximum).Maximum }
                CK ($dstN -eq $srcColor.n) `
                   "P7g：AVIF→webp 产物 $dstN 帧 == 源颜色流 $($srcColor.n) 帧（序号空洞会静默截断，退出码仍是 0 ⇒ 只能按帧数判）"
                $pxfAll = @(((ExecOut $fp "-v error -select_streams v -show_entries stream=pix_fmt -of csv=p=0 `"$($wf.FullName)`"") -split "`r?`n") |
                            ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
                $pxfNoAlpha = @($pxfAll | Where-Object { $_ -notmatch 'a[0-9]|a$|argb' })
                CK ($pxfAll.Count -ge 1 -and $pxfNoAlpha.Count -eq 0) ("P7g：产物 $($pxfAll.Count) 条视频流的像素格式（$(if ($pxfAll.Count) { $pxfAll -join '/' } else { '读不出' })）全含 alpha 平面（旧缺陷是 yuv420p=把透明丢了）" + $(if ($pxfNoAlpha.Count) { " ⇒ 无 alpha 的流：$($pxfNoAlpha -join '/')" } else { '' }))
                $ym = AlphaMin $wf.FullName
                CK ($ym -ge 0 -and $ym -lt 255) "P7g：产物真的有透明像素（alpha YMIN=$ym；全 255 = 透明通道被静默丢弃，-1 = 读不出）"
            }
        }
    }
}

# P7h/P7g 的文本结构锁：即使上面素材不可得，也不允许把这两个坑改回去
$qpPath = "$root/src/FfmpegGui/Services/QueueProcessor.cs"
$qpAll = [System.IO.File]::ReadAllText($qpPath)
$badThreads = @([regex]::Matches($qpAll, 'Math\.Max\(1,\s*item\.Options\.Threads\)'))
CK ($badThreads.Count -eq 0) "P7h 锁：不再出现 Math.Max(1, item.Options.Threads)（它恒等于 1 ⇒ avifenc ≥4 帧段错误）"
$fpLines = @(([System.IO.File]::ReadAllLines($qpPath)) | Select-String -Pattern '\-frame_pts') |
             Where-Object { $_.Line.TrimStart().StartsWith('//') -eq $false }
CK ($fpLines.Count -eq 0) "P7g 锁：两步法不再用 -frame_pts 编号（站点：$(($fpLines | ForEach-Object { 'L' + $_.LineNumber }) -join ',')）"

# ── ⑧ 结构断言：所有“会写最终产物”的专用通路都在色彩后置窗口内被调度（P7a-3）──
# 窗口 = `bool colorPostStepsDone = false;` … `if (!colorPostStepsDone && …`：链尾会为窗口内调度的
# 通路补上“ICC 被容器编码器丢弃”那一步。判定用**传递闭包**：
# 从窗口内被调度的方法出发，沿 `await XAsync(` 调用链能到达的方法都算已覆盖；
# 真正要拦的是“新开通路既不在窗口内、也不可达自窗口内方法”——那它永不会经色彩后置。
$qp = [System.IO.File]::ReadAllLines("$root/src/FfmpegGui/Services/QueueProcessor.cs")
$offLine = ($qp | Select-String -Pattern 'bool colorPostStepsDone = false' | Select-Object -First 1).LineNumber
$tailLine = ($qp | Select-String -Pattern 'if \(!colorPostStepsDone &&' | Select-Object -First 1).LineNumber
# 方法起始行→方法名（行区间即方法体范围，用于定位“某调用住在哪个方法里”）
$starts = @(); for ($i = 0; $i -lt $qp.Count; $i++) {
    if ($qp[$i] -match '^\s*(?:private|public|internal|protected)[^=;]*?\b(\w+Async)\s*\(') { $starts += @{ line = $i + 1; name = $matches[1] } }
}
$calls = @(); for ($i = 0; $i -lt $qp.Count; $i++) {
    foreach ($m in [regex]::Matches($qp[$i], 'await\s+(\w+Async)\s*\(')) { $calls += @{ line = $i + 1; name = $m.Groups[1].Value } }
}
function HostOf([int]$ln) { $h = $null; foreach ($s in $starts) { if ($s.line -lt $ln) { $h = $s.name } else { break } }; return $h }
# 窗口内直接调度的方法 = 覆盖集种子
$covered = [System.Collections.Generic.HashSet[string]]::new()
foreach ($c in @($calls | Where-Object { $_.line -ge $offLine -and $_.line -le $tailLine })) { [void]$covered.Add($c.name) }
# BFS 传递闭包：被覆盖方法里的 await 目标也算覆盖（反复一轮直到不再增长）
$grew = $true
while ($grew) {
    $grew = $false
    foreach ($c in $calls) {
        if ($covered.Contains($c.name)) { continue }
        $h = HostOf $c.line
        if ($h -and $covered.Contains($h)) { [void]$covered.Add($c.name); $grew = $true }
    }
}
# 待检对象：专用通路类调用（Process*/Pipe*）；它们的宿主方法必须在覆盖集内或在窗口内
# 例外：宿主方法本身**零调用方**（死代码）不构成交付缺口，但要写进消息里看得见。
$bad = @(); $dead = @()
$targets = @($calls | Where-Object { $_.name -match '^(Process|Pipe)' })
foreach ($c in $targets) {
    if ($c.line -ge $offLine -and $c.line -le $tailLine) { continue }   # 窗口内直接调用
    $h = HostOf $c.line
    if ($h -and $covered.Contains($h)) { continue }                     # 嵌在已覆盖方法里 ⇒ 链尾会负责
    $callers = @($calls | Where-Object { $_.name -eq $h })
    if ($h -and $callers.Count -eq 0) { $dead += $h; continue }         # 死代码（无人调用）⇒ 不是交付缺口
    $host_ = if ($h) { $h } else { '<无宿主方法>' }
    $bad += "L$($c.line) await $($c.name)（宿主 $host_）"
}
CK ($offLine -and $tailLine -and $bad.Count -eq 0) `
   "$($targets.Count) 处专用通路调用均经色彩后置（窗口 L$offLine…L$tailLine；覆盖方法 $($covered.Count) 个；绕开的：$($bad -join ', ')；零调用方的死代码：$(($dead | Sort-Object -Unique) -join '/')）"
CK (@($qp | Select-String -Pattern 'ApplyColorPostStepsAsync\(').Count -ge 3) `
   "色彩后置的唯一入口在主分支、链尾与定义处都出现（P7a-3 第三轮；防被“只删不改”假修）"
CK (@($qp | Select-String -Pattern 'EmbedDroppedFilterIccAsync\(').Count -ge 2) `
   "ICC 后置子步在共享入口内与定义处都还在（判据没被拆成第二份）"
# 同源登记结构锁：直接调 FfmpegCommandBuilder.BuildArguments 的位置必须恰好三处——
#   ① 主分支（captured.Options/captured.InputPath，它自己就是完整后置路径）
#   ② BuildArgsAndNote 内部（登记与建命令在同一个函数里，结构上不可能漏登记）
#   ③ BuildFfmpegPipeArguments 内部（用 itemForRegistration 登记 pipeOptions 克隆）
# 新增第四个直接调用 = 有人开了条新通路却不登记 ⇒ 本断言当场变红。
# ⚠ 2026-09-18：② 的白名单由 `^return FfmpegCommandBuilder.BuildArguments(opts, input, output)` 放宽为
#   「**行内含**该调用」—— 因为顺序统一批次 1 在 ② 的返回前插了一行「扩展 ③ 回显」（`LogInjectedScale`），
#   形状从 `return BuildArguments(...)` 变成 `var args = BuildArguments(...); …; return args;`。
#   ⇒ 锚在 `^return ` 上会**误报一个假红**（它数的是「新站点」，而这里一个站点都没多）。
#   计数仍是 3（`$rawCalls.Count -eq 3`）—— 放宽的只是**形状**，不是**数量**。
$rawCalls = @($qp | Select-String -Pattern 'FfmpegCommandBuilder\.BuildArguments\(')
$noteCount = @($qp | Select-String -Pattern 'BuildArgsAndNote\(').Count - 1   # 减去定义行本身
$badRaw = @($rawCalls | Where-Object {
    $t = $_.Line.Trim()
    -not ($t -match 'captured\.Options, captured\.InputPath' -or $t -match 'FfmpegCommandBuilder\.BuildArguments\(opts, input, output\)' -or $t -match 'BuildArguments\(pipeOptions, "-"') })
CK ($rawCalls.Count -eq 3 -and $badRaw.Count -eq 0) `
   "QueueProcessor 里只有 $($rawCalls.Count) 处直接调 BuildArguments（主分支/helper/管道克隆）；未登记的新站点：$(($badRaw | ForEach-Object { 'L' + $_.LineNumber }) -join ',')"
CK (@($qp | Select-String -Pattern 'BuildArgsAndNote\(').Count -ge 10) `
   "专用通路已批量改走登记入口（调用处 $noteCount 个）"

# ── P7f 同源结构锁：avifenc 探测只能有一个定义点 ──
# 背景：同一件“找 avifenc”的事曾经有三份拷贝且口径不一（队列分派查 PLAN、执行方法漏 PLAN、UI 漏手动路径），
# 导致便携包布局下 GIF→AVIF 两步法永远跑不到却静默回退。现在全仓只允许 PlatformServices.ResolveAvifencPath 里拼一次。
$allSrc = @(Get-ChildItem "$root/src/FfmpegGui" -Recurse -Include *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
# ⚠ 只数代码行：注释里提到某个方法名不是“又多了一个口径”，把它计入只会造出下一个假红。
# ⚠ 但行号必须用**文件真实行号**（先自增再判定），否则报出来的 L 数是过滤后的序号，误导排查。
$codeAll = @($allSrc | ForEach-Object { $f = $_.Name; $n = 0
    foreach ($ln in Get-Content $_.FullName) { $n++
        if ($ln.TrimStart() -notmatch '^(//|///|/\*|\*)') { [pscustomobject]@{ File = $f; Line = $ln; N = $n } }
    } })
$avProbe   = @($codeAll | Where-Object { $_.Line -match 'TryFindInPlanFolder\((PlatformServices\.)?Avifenc\)' })
$avDef     = @($codeAll | Where-Object { $_.Line -match 'static\s+string\?\s+ResolveAvifencPath\s*\(' })
$avWrap    = @($codeAll | Where-Object { $_.Line -match 'HasAvifencAvailable' })
$avGeneric = @($codeAll | Where-Object { $_.Line -match 'FindInArtifactsOrPlan\((PlatformServices\.)?Avifenc\)' })
$avWhere    = ($avProbe | ForEach-Object { $_.File + ':L' + $_.N }) -join ', '
CK ($avProbe.Count -eq 1 -and $avDef.Count -eq 1) `
   "P7f：avifenc 探测同源（PLAN 查找式 $($avProbe.Count) 处、ResolveAvifencPath 定义 $($avDef.Count) 处，均应为 1）；站点：$avWhere"
# 形态约束：定义必须在 PlatformServices.cs；不得再出现“第二口径”形状（无论定义还是调用）
$avDefInPS = @($avDef | Where-Object { $_.File -eq 'PlatformServices.cs' }).Count
CK ($avDefInPS -eq 1 -and $avWrap.Count -eq 0 -and $avGeneric.Count -eq 0) `
   "P7f 形态：定义在 PlatformServices.cs=$avDefInPS（应 1）、无第二口径形状 HasAvifencAvailable=$($avWrap.Count)、无经通用函数探 avifenc=$($avGeneric.Count)（均应为 0）"
if ($avWrap.Count -gt 0) { Write-Output ("     额外口径站点：" + (($avWrap | ForEach-Object { $_.File + ':L' + $_.N }) -join ', ')) }

# ── ⑬ JXL + Cjxl 后端：引擎 cjxl 出口 vs 直连 ProcessCjxlAsync 对拍（2026-09-17 cjxl 出口轮）──
# 为什么放在这条门禁：本门禁的章程是「**宣称 == 产物里真的有的东西**」，而这一节正是它的极端形态——
# 引擎出口宣称「像素零改动 / 目标 = sRGB」，直连通路则根本不参与色彩裁决；两者的**产物**必须能被分别解释。
# 实测（同一 P3 源 `a2_srgb_p3.png`，默认后端即 Cjxl）：
#   · 目标未指定（auto ⇒ 看齐源 ⇒ 计划 CarryIcc）：两路产物**逐像素完全相同**（ffmpeg psnr 报 inf）
#     且标注一致（都读回 P3）——「携带源 ICC」这条语义两条通路确实同源。
#   · 显式目标 sRGB（源色域超出目标 ⇒ 必须映射）：引擎**真的映射**并标注 sRGB；
#     直连 cjxl 分支**不做色彩映射**（它把源 ICC 嵌进输入副本交给 cjxl，`--color-space` 对该通路
#     无效——`QueueProcessor` 的 cjxl 分支自己会打「本通路的命令行不由 FfmpegCommandBuilder 产出」）。
#     ⇒ 两者**有意不同**：这一节把差异**钉成数字**，避免以后有人把"两路不一致"当回归去修掉引擎映射。
#   · 回归锁：`CarryIcc` 计划曾因 `ToSpec()` 的 `?? Linear/?? Srgb` 猜测式默认把整幅提亮
#     （均值 0.600→0.713，逐像素 == lin2srgb）。修复后本节的"逐像素完全相同"就是那道锁的**端到端**版本
#     （纯函数版本在 `ServiceProbe wire`）。
$jxlinfo = "$root/publish/PLAN/jxl/bin/jxlinfo.exe"
function JxlCs([string]$path) {
    $o = "$out/_jxlcs.txt"
    Start-Process -FilePath $jxlinfo -ArgumentList @($path) -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $o | Out-Null
    return ((([System.IO.File]::ReadAllText($o)) -split "`r?`n") |
            Where-Object { $_ -match 'Color space' } | Select-Object -First 1)
}
if (-not (Test-Path $jxlinfo)) {
    CK $false "缺 jxlinfo（$jxlinfo）—— JXL 标注回读的唯一可靠工具（exiftool 对 JXL 连读都读不出 ICC）"
} else {
    $ENG2 = @('--color-engine', 'engine'); $LEG2 = @('--color-engine', 'legacy')
    # ── (a) auto 目标：两路必须**逐像素相同** ──
    $je = RunCli 'jxl_eng_auto' $p3 'jxl' $ENG2
    $jl = RunCli 'jxl_leg_auto' $p3 'jxl' $LEG2
    CK ($je.file -and $jl.file -and $je.file.Length -gt 0 -and $jl.file.Length -gt 0) `
       "⑬ auto 目标：引擎 cjxl 出口与直连 cjxl 都产出非空 jxl（$($je.file.Length)B / $($jl.file.Length)B）"
    if ($je.file -and $jl.file) {
        $ps = PsnrTwo $je.file.FullName $jl.file.FullName $graphSame
        CK ($ps -ge 99.0) "⑬ auto 目标（CarryIcc，像素零改动）：两路产物**逐像素完全相同**（psnr=$ps；曾因 ToSpec 猜测式默认得 15.08dB）"
        $ce = JxlCs $je.file.FullName; $cl = JxlCs $jl.file.FullName
        CK (($ce -match 'P3') -and ($cl -match 'P3')) `
           "⑬ auto 目标：两路标注一致（引擎「$ce」/ 直连「$cl」）"
    }
    # ── (b) 显式 sRGB 目标：两路**有意不同**（引擎映射，直连不映射）──
    $je2 = RunCli 'jxl_eng_srgb' $p3 'jxl' ($ENG2 + @('--color-space', 'sRGB'))
    $jl2 = RunCli 'jxl_leg_srgb' $p3 'jxl' ($LEG2 + @('--color-space', 'sRGB'))
    CK ($je2.file -and $jl2.file -and $je2.file.Length -gt 0 -and $jl2.file.Length -gt 0) `
       "⑬ 显式 sRGB 目标：两路都产出非空 jxl（$($je2.file.Length)B / $($jl2.file.Length)B）"
    if ($je2.file -and $jl2.file) {
        $ce2 = JxlCs $je2.file.FullName; $cl2 = JxlCs $jl2.file.FullName
        CK ($ce2 -match 'sRGB primaries') "⑬ 引擎出口**真的映射到目标**：产物读回 sRGB（$ce2）"
        # ⚠ **契约已于 2026-09-27 翻转（#44）**。这两条原先钉的是"直连 cjxl 不参与色彩映射 ⇒ 两路**有意**不同"
        #   （旧读数：psnr 14.74 dB、两路像素不同）。那是把缺陷当契约钉 —— #44 把 legacy 的 jxl 旁路接上
        #   裁决点后，两路必须**都**兑现目标且**趋同**。翻转的依据是实测而非推理：
        #   `tests/output/t44/matrix44_post.log` / `post44b`（jxl legacy sRGB 从 18235 B/bt2020
        #   变成 7019 B/bt709+iec61966-2-1，`djxl` 解回原生像素 md5 与 auto 格不同 ⇒ 像素真转了），
        #   两路 PSNR 实测 44.43 dB（旧 14.74）。
        CK ($cl2 -match 'sRGB primaries') `
           ("⑬ 直连 cjxl 路线现在也必须兑现目标：产物应读回 sRGB primaries（实得 " + $cl2 +
            "）—— 旧版在这里钉的是'仍是源语义 P3、两路有意不同'，那属 #44 缺陷，已按实测翻转")
        $ps2 = PsnrTwo $je2.file.FullName $jl2.file.FullName $graphSame
        # ⚠ 阈值口径改于 2026-10-02（取证见 `tests/output/capture_carrier.ps1` 与账本第十节）：
        #   两臂**进编码器之前**的像素实测只差 max 15/65535（0.06/255）、聚合 **85.86 dB**（无损编码后逐比特同一量级），
        #   过 `-e 7 -d 2.4` 有损后差到 **36.87 dB**（max 118/255）——差是**有损编码在硬边纯色图上放大**出来的，
        #   不是色彩数学分歧（内核 vs zimg 在同一夹具上逐通道 max 11~15/65535）。
        #   旧缺陷形态（#44 之前"直连不映射"）实测只有 **14.74 dB** ⇒ 把这条的下限放在 25 dB
        #   仍可抓住那一族（相隔 10 dB），同时不再被编码噪声推着摆（历史上它一度只剩 44.43 vs 40 的余量）。
        #   **严格的水不放在这里**：逐点趋同改由下面的无损臂 (⑬c) 守。
        CK ($ps2 -ge 25.0) `
           ("⑬ 两路像素**趋同**（同源同目标 ⇒ 有损档 psnr 必须 ≥ 25 dB，实得 " + $ps2 +
            "）：明显低于此值才说明'只有一路真转了像素'那一族复发；本档不测亚 LSB 级一致性（那是 ⑬c 的活）")
    }
    # ── (c) 无损档：把"两路必须兑现同一份像素"这条真不变量放到**没有编码噪声**的地方测 ──
    $je3 = RunCli 'jxl_eng_srgb_ll' $p3 'jxl' ($ENG2 + @('--color-space', 'sRGB', '--quality', '100'))
    $jl3 = RunCli 'jxl_leg_srgb_ll' $p3 'jxl' ($LEG2 + @('--color-space', 'sRGB', '--quality', '100'))
    CK ($je3.file -and $jl3.file -and $je3.file.Length -gt 0 -and $jl3.file.Length -gt 0) `
       "⑬c 无损臂：两路都产出非空 jxl（$($je3.file.Length)B / $($jl3.file.Length)B）"
    if ($je3.file -and $jl3.file) {
        $ps3 = PsnrTwo $je3.file.FullName $jl3.file.FullName $graphSame
        CK ($ps3 -ge 40.0) `
           ("⑬c 无损档两路像素趋同（同源同目标 ⇒ ≥ 40 dB，实得 " + $ps3 +
            "）——这条才是'谁没转像素'的硬尺；有损档受 4:2:0/熵编码放大，测不了亚 LSB 级一致性")
        $ce3 = JxlCs $je3.file.FullName; $cl3 = JxlCs $jl3.file.FullName
        CK (($ce3 -match 'sRGB primaries') -and ($cl3 -match 'sRGB primaries')) `
           ("⑬c 无损档两路标注一致且都兑现目标（引擎「" + $ce3 + "」/ 直连「" + $cl3 + "」）")
    }
}

# ── ⑭ 携带策略 + 用户指定目标空间 + **编码器丢弃 ICC 的容器** ⇒ 必须回带源 ICC ──
#   2026-09-26 实测缺陷的回归锁。修复前的形态：源挂 Display P3 ICC，跑
#   `--color-strategy carry --color-space BT.2020` 出 webp ⇒ 产物 **96 B、抽不到任何 ICC（0 B）**，
#   日志却写「用户已指定色彩空间，跳过源文件 ICC Profile 恢复」、裁决仍是 `Proceed`
#   ⇒ 零标注产物被查看器按 sRGB 解读（广色域像素洗白）。**legacy / engine 两条通路都中**
#   （engine 侧 P7a 用 `Action != CarryIcc` 排除、legacy 侧元数据恢复用 `!userSpecifiedColor` 排除，
#    两条各自合理、叠加后没有任何一方负责嵌入）。
#   ⚠ 判据取"产物内嵌 ICC 的红原色 == **源** ICC 的红原色"，两边都用同一个 helper 现读：
#     不写死 0.680/0.320 这类"凭记忆的标准值"（本仓规矩），而且若哪天改成挂一张 BT.2020 的 ICC
#     （红原色 0.708）这条照样红 —— 那是在给"没动过的像素"贴假标签，不该被"至少有标注"糊过去。
#   ⚠ 两条**前置**断言挡住真空：夹具自己读不到 ICC ⇒ 后面全无可比；夹具其实是 sRGB 源 ⇒
#     本格会退化成 `UnlabeledMeansSrgb` 的合法情形，"零标注"不再等于缺陷。
$chrSrc = IccRedChroma $p3
$ss = if ($chrSrc) { "$($chrSrc[0]),$($chrSrc[1])" } else { '(读不到)' }
CK ($chrSrc -ne $null) "⑭ 前置：夹具 $p3 自身读得到 ICC 红原色（实得 $ss）⇒ helper 这条路是通的，负断言才有意义"
CK ($chrSrc -and [Math]::Abs($chrSrc[0] - 0.640) -gt 0.01) `
    "⑭ 前置：夹具是**非 sRGB** 广色域源（红原色 $ss，须明显偏离 bt709 的 0.640）⇒ 否则本格退化成合法的不标注"
foreach ($eng in @(@{ n = '引擎'; a = @('--color-engine', 'engine') }, @{ n = '直连'; a = @('--color-engine', 'legacy') })) {
    $tag = 'carry_user_' + $eng.n
    $rc = RunCli $tag $p3 'webp' ($eng.a + @('--color-strategy', 'carry', '--color-space', 'BT.2020'))
    CK ($rc.file -and $rc.file.Length -gt 0) ("⑭ " + $eng.n + " 通路：carry + 指定目标空间仍产出非空 webp（$($rc.file.Length)B）")
    $chr = if ($rc.file) { IccRedChroma $rc.file.FullName } else { $null }
    $cs = if ($chr) { "$($chr[0]),$($chr[1])" } else { '(读不到 ICC)' }
    CK ($chr -and $chrSrc -and [Math]::Abs($chr[0] - $chrSrc[0]) -le 1e-4 -and [Math]::Abs($chr[1] - $chrSrc[1]) -le 1e-4) `
        ("⑭ " + $eng.n + " 通路：内嵌的就是**源** ICC（红原色 产物 $cs vs 源 $ss）")
    CK ($rc.log -notmatch '跳过源文件 ICC Profile 恢复') `
        ("⑭ " + $eng.n + " 通路：不得再写「跳过源 ICC 恢复」这种与产物状态矛盾的话")
}

# ── ⑮ tonemap 链的 SDR 出口：链末**实际用的曲线**必须等于**产物标签兑现的曲线**（任务 #26②）──
# 修复前实测（HDR→8bit PNG 三格一致，取证 `tests/output/_diag_31cur/`）：链末是
#   `zscale=…:p=bt709:t=bt709:m=bt709`，而 **zimg 把 `t=bt709` 实现成纯 γ(1/2.4)**（本仓已登记
#   "zimg 有 sRGB 分段、无 BT.709 分段"）；产物标签却写 `cICP.transfer=1`（=BT.709 **定义式**，分段）
#   ⇒ 标签声称了一条根本没实现的曲线；且 H.273 **没有**"纯 γ2.4"的编号 ⇒ 只能把**像素**提到标签水位。
# 改后：链末 `t=iec61966-2-1`（sRGB 分段，CICP=13，引擎侧 `SdrTwinOf` 本来就优先选它 ⇒ 两条管线收敛）。
# ⚠ 改交付的量化（新 vs 旧，`blend=difference/psnr` 实测）：**PSNR ≈35.5 dB** —— 但整轮 54 条
#   在改前改后**都全绿** ⇒ 说明此前**没有任何门禁钉过这条链的出口曲线**，本段就是补上的那把锁。
#   判据形状刻意的两点：① **两侧都从现场解析**（命令行 / 产物回读），不硬编码比字符串，
#   否则改了标签忘了改像素也能绿；② 另加一条"必须等于 sRGB"的定样断言，
#   否则"两边都还是 bt709"这种**一致的错**会顺利通过 ①。
# ⚠ 取标签**必须按字段名取**：ffprobe 的 `-of csv` **不按 `-show_entries` 的请求顺序输出**
#   （实测请求 `color_primaries,color_transfer` 却回 `iec61966-2-1,bt709`）⇒ 位置索引会把
#   "primaries"当成"transfer"用，把一致的产物判成失配。本仓 `_lib-oracle.ps1` 用 JSON 具名字段
#   正是为了躲这个；新写的判据别再退回 csv 取位。
function CicpField([string]$path, [string]$field) {
    $o = "$out/_cicpf.txt"
    Start-Process -FilePath $fp -ArgumentList @('-v', 'error', '-select_streams', 'v:0', '-show_entries',
        "stream=$field", '-of', 'default=nk=1:nw=1', $path) -NoNewWindow -Wait -RedirectStandardOutput $o | Out-Null
    return ([System.IO.File]::ReadAllText($o)).Trim()
}
$hdr15 = RunCli 'd15_tonemap_exit' $hdr 'png' @('--bit-depth', '8')
$mc15 = [regex]::Match($hdr15.log, 'zscale=[^\s"]*?:p=([a-z0-9]+):t=([a-z0-9-]+):m=([a-z0-9-]+)')
$chainT = if ($mc15.Success) { $mc15.Groups[2].Value } else { $null }
$labT = if ($hdr15.file) { CicpField $hdr15.file.FullName 'color_transfer' } else { '' }
$primT = if ($hdr15.file) { CicpField $hdr15.file.FullName 'color_primaries' } else { '' }
Write-Output "  ⑮ 链末曲线=$chainT  产物标签 transfer=$labT primaries=$primT  exit=$($hdr15.code)"
if (-not $hdr15.file -or -not $chainT) {
    SKIP "⑮ 前置不足（产物=$([bool]$hdr15.file) 链末解析=$([bool]$chainT)）⇒ 本段 4 条断言未运行，不是通过" 4
} else {
    CK ($labT -eq $chainT) `
       "⑮ 一致性：链末用的曲线（$chainT）必须 == 产物标签兑现的曲线（$labT）——不一致就是'标签声称了没实现的曲线'"
    CK ($chainT -eq 'iec61966-2-1') `
       "⑮ 定样：图像容器 SDR 出口 = sRGB（维护者 2026-09-26 口径）⇒ 链末必须是 iec61966-2-1，实得 $chainT（两边都停在 bt709 也是错，故单列一条）"
    # ── 负控：用**生产代码**（`ServiceProbe png3`）把产物标签改成 cICP=1/1 ⇒ ① 那条判据必须转红 ──
    $neg15 = "$out/d15_neg_cicp1.png"
    Copy-Item -LiteralPath $hdr15.file.FullName -Destination $neg15 -Force
    $prExe15 = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
    if (-not (Test-Path $prExe15)) { $prExe15 = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
    if (Test-Path $prExe15) {
        Start-Process -FilePath $prExe15 -ArgumentList "png3 `"$neg15`" 0 1 1" -NoNewWindow -Wait | Out-Null
        $labNeg = CicpField $neg15 'color_transfer'
        CK ($labNeg -ne $chainT) `
           "⑮ 负控：把标签改成 cICP=1 后判据必须失配（链末 $chainT vs 标签 $labNeg）——不失配则 ① 只是在找字"
        CK ($labNeg -eq 'bt709') `
           "⑮ 负控夹具自证：改写后标签确实读回 bt709（实得 $labNeg）⇒ 失配不是'读不到'造的"
    } else { SKIP "⑮ 缺 ServiceProbe（$prExe15）⇒ 2 条负控未运行，不是通过" 2 }
}

# ── ⑯ AVIF 出口位深 == **实际会跑的那个编码器**的能力（任务 #36）────────────────────────
# 修复前实测（取证脚本 tests/output/t36/run_cells.ps1，本机 Release exe）：
#   · CLI 的 --encoder 映射到 **EncoderBackend 枚举**（Ffmpeg/Cjpegli/Cjxl/...），
#     全仓**没有任何入口**能把 ffmpeg 编码器名写进 options.Encoder ⇒ 命令行路径上它恒为 null；
#   · 旧的 AvifMaxBitDepth 用 `options.Encoder ?? ""` 做前缀匹配 ⇒ 空串落到 else 支 ⇒ 上限**恒 10**
#     ⇒ `--bit-depth 12` 在裁决点被就地钳成 10（产物 pix_fmt=yuv420p10le），并播报
#       「本工具对该容器实测可交付的上限 = 10bit」；
#   · 同一条命令里**不写 -c:v** ⇒ ffmpeg 为 .avif 挑的默认编码器实测是 **libaom-av1**
#     （自报表含 yuv420p12le/yuv422p12le/yuv444p12le/gbrp12le），独立参照直接喂同一输入
#     `-c:v libaom-av1 -pix_fmt yuv420p12le` ⇒ rc=0、产物 pix_fmt=yuv420p12le
#     ⇒ 那句"上限 = 10bit"是**假的**：能力在，被"钳制发生在编码器解析之前"抹掉了。
#   本段用**三把独立的尺子**钉住，缺一不可：
#     ① 与 ffmpeg 自己的编码器报表对账（裁判不读产品注释，随包构建换了会自动跟进）；
#     ② 产品的真实交付（[cmd] 的 -pix_fmt + ffprobe 回读产物）；
#     ③ 钳制**仍然活着**（16 ⇒ 必须落在实测上限并播报）——否则"把钳制整个删掉"也能骗过 ②。
# ⚠ 本段刻意**不**用 ServiceProbe 读那个函数的返回值：那只能证明"代码里写了 12"，
#   证明不了"产物是 12"。表①用的是 ffmpeg 自报表（外部权威），与产品实现无关。
function Depth-FromPixFmtToken([string]$name) {
    # yuv420p12le / gbrp12le / p012le / yuv444p12msble ⇒ 12；p010le ⇒ 10；yuv420p / nv12 ⇒ 8
    $m = [regex]::Match($name, 'p(\d{2,3})[a-z]*$')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return 8
}
function Measured-EncoderMaxDepth([string]$enc) {
    $t = Exec $ff "-hide_banner -h encoder=$enc"
    $m = [regex]::Match($t, 'Supported pixel formats:\s*(.+)')
    if (-not $m.Success) { return $null }        # 认不出来 ⇒ $null（调用方判红），绝不猜默认值
    $best = 8
    foreach ($tok in ($m.Groups[1].Value -split '\s+')) {
        if ($tok -notmatch '(le|be)$') { continue }
        $d = Depth-FromPixFmtToken $tok
        if ($d -gt $best) { $best = $d }
    }
    return $best
}
# 不写 -c:v 时 ffmpeg 为 .avif 实际挑的编码器名（从它自己的映射行里读）
$dlog = Exec $ff ('-y -hide_banner -i "' + $sdr + '" -frames:v 1 "' + "$out/d16_default.avif" + '"')
$mDef = [regex]::Match($dlog, '->\s*av1\s*\(([\w\-.]+)\)')
$defEnc = if ($mDef.Success) { $mDef.Groups[1].Value } else { $null }
$defCap = if ($defEnc) { Measured-EncoderMaxDepth $defEnc } else { $null }
$capSvt = Measured-EncoderMaxDepth 'libsvtav1'
Write-Output ("  ⑯ 实测：.avif 默认编码器=" + $defEnc + " 其自报上限=" + $defCap + "  libsvtav1 自报上限=" + $capSvt)
CK ($defEnc -and $defCap -ge 12) `
   ("⑯ 前提（外部权威）：不写 -c:v 时 ffmpeg 为 .avif 选的编码器（实得 " + $defEnc + "，上限 " + $defCap +
    "）必须自己就能到 12bit —— 产品若按别的编码器定上限，本段结论作废，先看这条为什么红")
CK ($capSvt -eq 10 -and $defCap -ne $capSvt) `
   ("⑯ 尺子自证：解析器必须能把 libsvtav1(实得 " + $capSvt + ") 与默认编码器(" + $defCap + ")区分开" +
    "——两边数一样，下面那条'交付==实测上限'就是恒等式，没有牙")

function RunAvif([string]$tag, [string[]]$extra, [string]$In = '') {
    if (-not $In) { $In = $sdr }
    $quoted = @($extra | ForEach-Object { if ($_ -match '\s' -and $_ -notmatch '^"') { "`"$_`"" } else { $_ } })
    $a = @('--headless', '-i', "`"$In`"", '--format', 'avif', '--output', "`"$out/$tag`"", '--log-level', 'Debug') + $quoted
    $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput "$out/$tag.o" -RedirectStandardError "$out/$tag.e"
    $log = [System.IO.File]::ReadAllText("$out/$tag.o") + [System.IO.File]::ReadAllText("$out/$tag.e")
    $f = Get-ChildItem -Recurse "$out/$tag" -Include *.avif -EA SilentlyContinue | Select-Object -First 1
    $cmd = ''
    # ⚠ 必须用 `[cmd]` + 空白 + `ffmpeg` 锚定：运行器自己那句心跳文案里就写着"已追加 -progress pipe:1
    #   （**不改动 [cmd] 行**）"⇒ 光找 `\[cmd\]` 会先命中那行**散文**，`-pix_fmt` 找不到 ⇒ 本段判红。
    $mc = [regex]::Match($log, '\[cmd\]\s+ffmpeg[^\r\n]*')
    if ($mc.Success) { $cmd = $mc.Value }
    $cmdD = $null; $mp = [regex]::Match($cmd, '-pix_fmt\s+([\w]+)')
    if ($mp.Success) { $cmdD = Depth-FromPixFmtToken $mp.Groups[1].Value }
    $prodD = $null
    if ($f) {
        $px = CicpField $f.FullName 'pix_fmt'
        if ($px) { $prodD = Depth-FromPixFmtToken $px }
    }
    return @{ code = $p.ExitCode; log = $log; file = $f; cmd = $cmd; cmdDepth = $cmdD; prodDepth = $prodD;
              hasCv = ($cmd -match '-c:v\s') }
}
$r12 = RunAvif 'd16_avif12' @('--bit-depth', '12')
$r16 = RunAvif 'd16_avif16' @('--bit-depth', '16')
if (-not $r12.file -or -not $r16.file) {
    SKIP ("⑯ 前置不足（avif12 产物=" + [bool]$r12.file + ' avif16 产物=' + [bool]$r16.file + '）⇒ 本段 5 条交付断言未运行，不是通过') 5
} else {
    CK ((-not $r12.hasCv) -and ($r12.cmdDepth -eq 12) -and ($r12.prodDepth -eq 12)) `
       ("⑯ 请求 12 ⇒ 命令行与产物都得是 12bit，且**不许**自己补 -c:v（实得 cmd 位深=" + $r12.cmdDepth +
        ' 产物位深=' + $r12.prodDepth + ' 含 -c:v=' + $r12.hasCv + '）——任务#36 的正身：钳制曾把 12 抹成 10')
    CK ($r12.log -notmatch 'BitDepth(Reduced|Raised)') `
       ('⑯ 请求 12 已按 12 交付 ⇒ 不该再有位深降级码（实得日志含码=' + ($r12.log -match 'BitDepth') + '）——有码说明还在降')
    CK ($r16.prodDepth -eq $defCap) `
       ("⑯ 16bit 请求的交付档必须 == 默认编码器的**实测**上限（实得 " + $r16.prodDepth + ' vs 实测 ' + $defCap +
        '）——裁判与产品各自独立取数，不等就是口径漂移')
    CK (($r16.prodDepth -ne 16) -and ($r16.prodDepth -ne 10)) `
       ("⑯ 钳制必须仍然活着且不再停在旧值 10（实得 " + $r16.prodDepth +
        '）——16 说明钳制被整个删掉（libaom 无 16bit YUV，会编不出），10 说明又退回"编码器未知"的老口径')
    CK ($r16.log -match 'BitDepthReduced）：出口位深降为 12bit（本次目标 16bit') `
       ('⑯ 降档要点名（日志片段=' + ((@($r16.log -split "`r?`n") | Where-Object { $_ -match 'BitDepthReduced' } | Select-Object -First 1)) + '）')
}
# ── ⑯ 同源结构锁：上限只允许从 ClassifyAvifBackend 推出来，且 UI 不得再抄一份三元式 ──
# ⚠ 匹配前**先剥整行注释**（本仓既有教训）：这两处正则都命中过我写在源码里的**说明文字**
#   （"原先是 (isLibaom || isNvenc) ? 12 : 10"）⇒ 不剥注释的话，解释修复的注释自己会把门禁判红。
function Strip-LineComments([string]$text) {
    return ((@($text -split "`r?`n") | ForEach-Object { ($_ -replace '//.*$', '') }) -join "`n")
}
$ieaSrc = Strip-LineComments ([System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs"))
$mwSrc  = Strip-LineComments ([System.IO.File]::ReadAllText("$root/src/FfmpegGui/MainWindow.xaml.cs"))
$fnM = [regex]::Match($ieaSrc, 'public static int AvifMaxBitDepthForEncoder\(string\? encoder\)\s*=>\s*ClassifyAvifBackend')
CK ($fnM.Success) `
   '⑯ 结构锁：AvifMaxBitDepthForEncoder 必须由 ClassifyAvifBackend 推导（空编码器名⇒Libaom 这条口径全仓只有一份）'
$ownTable = [regex]::Matches($ieaSrc, '(?m)^\s*(var|if)\s.*StartsWith\("(libaom|av1_nvenc)"')
$uiTernary = [regex]::Matches($mwSrc, '\?\s*12\s*:\s*10')
CK ($uiTernary.Count -eq 0) `
   ('⑯ 结构锁：MainWindow 不得再自带 "? 12 : 10" 的位深上限副本（实得命中 ' + $uiTernary.Count +
    ' 处）——它漏掉"编码器留空"这一支，正是下拉不给 12 的那半边')
CK ($ownTable.Count -le 2) `
   ('⑯ 结构锁参考读数：ImageEncoderArgs 里按 libaom/av1_nvenc 前缀分类的位置 = ' + $ownTable.Count +
    ' 处（应为 ClassifyAvifBackend 内部那两处；本条只登记规模，超了说明有人又抄了一份前缀表）')

# ── ⑰ HDR 源 → AVIF + 显式广色域目标：#39 撤掉"avif+P3+高位深 ⇒ 8bit"特例之后的交付端锁 ──
# 为什么单列一段（⑯ 覆盖不到）：⑯ 用的是 SDR 源，走的是"目标色域转换"链；而 HDR 源在
#   `Decision.cs` 里另走 tonemap 链，那里有 `tmDstP = capP ?? target.primaries` / `tmDstT = capT ?? …`
#   —— 被撤的那条特例恰好返回过 capP="bt709"/capT="iec61966-2-1"，也就是**它能在这一路上把用户要的
#   P3 静默改成 bt709**（⑯ 的格子看不见这件事）。⇒ 必须拿 HDR 源 + 显式 P3 目标复测一遍。
# 实测（`tests/output/t39/hdr_p3_check.ps1`，两管线）：默认/引擎格 exit=0、产物 5855B、
#   ffprobe `pix_fmt=yuv420p12le color_primaries=smpte432`、零 `BitDepth*` 码。
# ⚠ 当时还量到一条**与本轮无关**的既有缺陷：`--color-engine legacy` 在同一组合下硬失败
#   （链里只挂 iccgen、不走 tonemap ⇒ ffmpeg "Not yet implemented"、零产物），且 bd=8 与 BT.2020 目标
#   同样失败 ⇒ 不是 #39 引入的，登记为任务 #43。当时本段因此**只锁能出货的默认/引擎路**，
#   不去把 legacy 的失败钉成"必须失败"（那等于把缺陷焊死）——#43 修完后由下面的 ⑰b 把两路一起锁上。
$h17 = RunAvif 'd17_hdr_p3_12' @('--bit-depth','12','--color-space','Display P3') $hdr
$s17 = RunAvif 'd17_hdr_srgb_12' @('--bit-depth','12','--color-space','sRGB') $hdr
if (-not $h17.file -or -not $s17.file) {
    SKIP ('⑰ 前置不足（P3 产物=' + [bool]$h17.file + ' sRGB 对照产物=' + [bool]$s17.file + '）⇒ 本段 4 条断言未运行，不是通过') 4
} else {
    $hp = CicpField $h17.file.FullName 'color_primaries'
    $ht = CicpField $h17.file.FullName 'color_transfer'
    $sp = CicpField $s17.file.FullName 'color_primaries'
    Write-Output ('  ⑰ P3 格 primaries=' + $hp + ' trc=' + $ht + ' 位深=' + $h17.prodDepth +
                  '   sRGB 对照 primaries=' + $sp + ' 位深=' + $s17.prodDepth)
    CK (($h17.prodDepth -eq 12) -and ($h17.log -notmatch 'BitDepth')) `
       ('⑰ 请求 12 就必须交付 12（实得位深=' + $h17.prodDepth + ' 含位深码=' + ($h17.log -match 'BitDepth') +
        '）——被撤的那条特例正是把它折成 8bit，本条盯它别回来')
    CK ($hp -eq 'smpte432') `
       ('⑰ 用户显式要 Display P3 ⇒ 产物 primaries 必须是 smpte432（实得 ' + $hp +
        '）。旧特例在这条 tonemap 链上以 `tmDstP = capP ?? target.primaries` 把目标改成过 bt709')
    CK ($sp -eq 'bt709') `
       ('⑰ 对照组：同素材同位深只把目标换成 sRGB ⇒ primaries 必须是 bt709（实得 ' + $sp +
        '）——两格读数必须不同，否则上一条只是在找一个固定字符串')
    CK (($ht -ne 'smpte2084') -and ($ht -ne 'arib-std-b67')) `
       ('⑰ HDR 源已降过 SDR ⇒ 产物 trc 不许还是 ' + $ht + '（PQ/HLG 原样透传就是"改了标签没改像素"那一族）')
}

# ── ⑰b 同一组合走 `--color-engine legacy`：任务 #43 的交付端锁 ──
# 改前实测（`tests/output/t43/matrix2_pre_measure.log`，逐格哈希）：legacy + 显式 SDR 目标三格
#   命令行只挂 `-vf iccgen` ⇒ ffmpeg `Error while filtering: Not yet implemented`、exit=1、**零产物**；
#   而 auto / P3 PQ 两列正常出货（5247B / 7B24C02FE376F435）。
# 根因在 FfmpegCommandBuilder.NeedsHdrToSdrTonemap：avif/jxl/jxr 那一条豁免支原先是**无条件**
#   `return false`（"HDR 原生容器 ⇒ 永不 tonemap"），把"用户显式要 SDR 目标"也一起豁免掉了。
# ⚠ 第四格（auto）是这次修改的"不误伤"锁，也是**唯一能抓住我把谓词写反的读数**：
#   第一版写成 `return noExplicitTarget` ⇒ auto 格被强加 tonemap（产物 5247B → 4281B）、
#   三格显式目标命令行逐字未变（仍然零产物）—— 恰好两列同时搞反，光读代码看不出来。
# 量不到的维度不写断言：`-show_entries stream=color_trc` 对本仓 avif/png 恒空（实测），
#   正确的键名是 `color_transfer`（源 PNG 用同一命令量得出 smpte2084 ⇒ 不是探针坏）。
$l17 = RunAvif 'd17b_legacy_p3'   @('--bit-depth','12','--color-space','Display P3','--color-engine','legacy') $hdr
$m17 = RunAvif 'd17b_legacy_srgb' @('--bit-depth','12','--color-space','sRGB','--color-engine','legacy') $hdr
$a17 = RunAvif 'd17c_legacy_auto' @('--bit-depth','12','--color-engine','legacy') $hdr
$lP = ''; $lT = ''; $mP = ''; $aT = ''
if ($l17.file) { $lP = CicpField $l17.file.FullName 'color_primaries'; $lT = CicpField $l17.file.FullName 'color_transfer' }
if ($m17.file) { $mP = CicpField $m17.file.FullName 'color_primaries' }
if ($a17.file) { $aT = CicpField $a17.file.FullName 'color_transfer' }
Write-Output ('  ⑰b legacy: P3 格 exit=' + $l17.code + ' 产物=' + [bool]$l17.file + ' primaries=' + $lP + ' trc=' + $lT +
              '   sRGB 格 exit=' + $m17.code + ' primaries=' + $mP +
              '   auto 格 exit=' + $a17.code + ' trc=' + $aT + ' 含tonemap=' + ($a17.cmd -match 'tonemap'))
CK (($l17.code -eq 0) -and [bool]$l17.file -and ($l17.log -notmatch 'Not yet implemented')) `
   ('⑰b legacy + 显式 P3 必须出货（exit=' + $l17.code + ' 有产物=' + [bool]$l17.file +
    ' 日志含"Not yet implemented"=' + ($l17.log -match 'Not yet implemented') +
    '）——#43 的原症状就是 HDR 帧被塞给 iccgen 而零产物，改前三格全中')
CK (($lP -eq 'smpte432') -and ($lT -eq 'iec61966-2-1')) `
   ('⑰b legacy 的 P3 格要真兑现目标：primaries 必须 smpte432（实得 ' + $lP + '）且 trc 必须 iec61966-2-1（实得 ' + $lT +
    '）——只把退出码修绿、标签仍停在 bt2020/PQ 等于换了个方式骗人')
CK ($mP -eq 'bt709') `
   ('⑰b 对照组：legacy 同素材只把目标换成 sRGB ⇒ primaries 必须 bt709（实得 ' + $mP +
    '）——与 P3 格读数必须不同，否则上一条只是在匹配固定字符串')
CK ((-not ($a17.cmd -match 'tonemap')) -and ($aT -eq 'smpte2084')) `
   ('⑰b 不误伤锁：legacy 未指定目标（auto）⇒ 命令行不许出现 tonemap（实得含=' + ($a17.cmd -match 'tonemap') +
    '）且产物 trc 必须仍是 smpte2084（实得 ' + $aT + '）——豁免支的谓词一旦写反，这一格第一个红')

Write-Output "  ── decision-delivery: pass=$pass fail=$fail skip=$skip"
if ($fail -gt 0) { exit 1 } else { exit 0 }
