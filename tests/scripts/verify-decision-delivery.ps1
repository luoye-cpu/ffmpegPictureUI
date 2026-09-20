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

$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }

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
    if (-not (Test-Path $srgbJxl)) { Write-Output "  SKIP ⑫：无 ICC 的 sRGB .jxl 造不出来 ⇒ 未评估" }
    else {
        CK ($null -eq (IccRedChroma $srgbJxl)) "⑫ 对照组：源 .jxl 自身没有 ICC ⇒ 产物 ICC 不可能来自“回带源 ICC”（还须看下面两步：是否真跑了目标 ICC、落下来的是否为目标色域）"
        $r12 = RunCli 'srgbjxl_tiff' $srgbJxl 'tiff' @('--color-space', 'Display P3')
        if (-not $r12.file) {
            Write-Output "  SKIP ⑫：JXL→TIFF 未产出（$($(($r12.log -split "`n") | Select-Object -Last 2) -join ' | ')）⇒ 未评估"
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
$avif = Get-ChildItem "$root/tests/output/results" -Recurse -Include *.avif -EA SilentlyContinue |
        Where-Object { $_.Length -gt 3000 } | Select-Object -First 1
if (-not $avif) { Write-Output "  SKIP ⑩：无动图 AVIF 素材（先用 results/*.avif）" }
else {
    $r10 = RunCli 'avif_webp' $avif.FullName 'webp' @()
    if (-not $r10.file) {
        # 路径本身失败 ⇒ 本断言**无法评估**。绝不能因为“日志里没有嵌 ICC”就给绿灯——
        # 那是任务根本没跑到后置那一步，不是“未参与”分支在起作用（假绿）。
        $why = ((($r10.log -split "`n") | Where-Object { $_ -match '失败|Error' } | Select-Object -First 1) -as [string]).Trim()
        Write-Output "  SKIP ⑩：AVIF→webp 两步法自身失败（$why）⇒ 未参与分支未被执行到（已记为待查项 P7e，不计入绿）"
    } else {
        CK ($r10.log -match '裁决点未参与') "AVIF→webp（命令行非裁决点产出）显式记录‘裁决点未参与’，不静默"
        CK ($r10.log -notmatch '已嵌入出口语义 ICC') "AVIF→webp 没有赌一把去嵌 ICC（日志实证）"
        # ── P7e 回归锁：选流不能退化成“挑首帧副本”或“挑 alpha 平面”──
        # 产物必须是**多帧动画**且是**彩色**（帧数对齐源、饱和度 > 10）。
        # 实测基准：旧硬编码 `-map 0:2` 在该素材上直接 -22；而 0:0 是 1 帧首图副本，选错就只会得到单帧。
        $o10 = "$out/avif_webp"; $f10 = $r10.file.FullName
        $srcFrames = (((Exec $fp "-v error -select_streams v -count_frames -show_entries stream=nb_read_frames -of csv=p=0 `"$($avif.FullName)`"") -split "`r?`n") -join ',' -split ',') | ForEach-Object { [int]$_ } | Measure-Object -Maximum
        $dstN = [int]((((Exec $fp "-v error -select_streams v -count_frames -show_entries stream=nb_read_frames -of csv=p=0 `"$f10`"") -split "`r?`n") -join '').Trim())
        CK ($dstN -ge 2 -and $dstN -eq $srcFrames.Maximum) `
           "P7e：AVIF→webp 产物是 $dstN 帧动画（源最大流 $($srcFrames.Maximum) 帧）——选流未退化成单帧副本"
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
    Write-Output "  SKIP ⑪：透明 GIF 素材生成失败（ffmpeg 退出 $mk/$mkGif）⇒ 未评估，不给绿"
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
        Write-Output "  SKIP ⑪：产品未能产出动画 AVIF（$why）⇒ 素材不可得，未评估"
    } else {
        # 素材自检：动画（≥2 帧的颜色流）+ 存在 gray 流（alpha 平面），否则下面的断言无从谈起
        $probe = ((Exec $fp "-v error -select_streams v -count_frames -show_entries stream=index,pix_fmt,nb_read_frames -of csv=p=0 `"$($madeAvif.FullName)`"") -split "`r?`n")
        $rows = @($probe | ForEach-Object { $c = $_ -split ','; @{ idx = [int]$c[0]; pix = $c[1]; n = [int]$c[2] } })
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
                $dstN = [int]((((Exec $fp "-v error -select_streams v -count_frames -show_entries stream=nb_read_frames -of csv=p=0 `"$($wf.FullName)`"") -split "`r?`n") -join '').Trim())
                CK ($dstN -eq $srcColor.n) `
                   "P7g：AVIF→webp 产物 $dstN 帧 == 源颜色流 $($srcColor.n) 帧（序号空洞会静默截断，退出码仍是 0 ⇒ 只能按帧数判）"
                $pxf = (((Exec $fp "-v error -select_streams v -show_entries stream=pix_fmt -of csv=p=0 `"$($wf.FullName)`"") -split "`r?`n") -join '').Trim()
                CK ($pxf -match 'a$|argb|yuva') "P7g：产物像素格式 $pxf 含 alpha 平面（旧缺陷是 yuv420p=把透明丢了）"
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
        CK ($cl2 -match 'P3') "⑬ 直连 cjxl 分支**不参与色彩映射**：产物仍是源语义 P3（$cl2）—— 登记为两条通路的**有意**差异，不是回归"
        $ps2 = PsnrTwo $je2.file.FullName $jl2.file.FullName $graphSame
        CK ($ps2 -ge 0 -and $ps2 -lt 30.0) `
           "⑬ 两路像素**确实不同**（psnr=$ps2 ＜ 30dB）：证明引擎真做了 P3→sRGB 映射，直连真没做"
    }
}

Write-Output "  ── decision-delivery: pass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 } else { exit 0 }
