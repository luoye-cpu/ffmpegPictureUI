# verify-color-matrix-full.ps1 —— 色彩矩阵全网格实测（源空间 × 目标空间 × 位深 × 容器），四把独立裁判 + 四条负控。
#
# 本仓已有什么、这块补什么（不重复造裁判）：
#   · `verify-color-engine-xcheck.ps1` = 「引擎 vs libzimg 逐像素」，但只有 5 组方向 × 2 张图，**无位深轴、无容器轴**；
#   · `verify-oracle-matrix.ps1`       = 「参数轴组合下产物是否被正确交付」，判据来自 `_lib-oracle.ps1`，
#                                        **不比矩阵数值**（它判容器/标签/结构/PSNR 下限）；
#   ⇒ 本脚本填的是：源空间 × 目标空间 × {8,10,12,16} × {png,webp,tiff,jxl,avif} 的**逐格三账**。
#
# 四把尺子互相独立（任何一把单独都能被自洽骗过）：
#   [CAP] 请求位深不在该容器的能力表里时，只允许三种结局：**干净拒绝** / **升降档但已响亮播报** /
#         **既没拒绝也没播报**。前两种合规，第三种（真静默）才是红 —— 判据在 `Get-BitDepthOutcome`。
#         ⚠ 2026-09-26 之前的判据是纯数值的（请求≠交付即红），把产品**既定设计**的
#           `--bit-depth 10/12 → 交付 8/16 并播报 BitDepthReduced/BitDepthRaised`（RGB 出口只有两档）
#           一并算成违规 ⇒ full 层 360 格里 144 条假红。改成读该格日志定性，但**不放宽**：
#           判据的牙由 §2a 的合成行自测锁住（无码必红 / 有码必绿 / 播报与实测矛盾必红）。
#   [LBL] 产物**实际携带**的标注必须等于承诺（`Get-ColorMetadata` / `Get-StreamInfo` 位深）⇒ 专治「只改标签」。
#   [NUM] 与**纯 zimg 参照链**同变换对拍（`Invoke-Psnr`，≥60 dB 才绿）⇒ 独立实现背书。
#   [RT ] A→B→A 往返漂移 ⇒ 不需要外部参照，专治转置/复合顺序错（自洽骗不过往返）。
#   [NC1/NC1b/NC2/NC3] 四条负控 —— 不同目标空间的产物互比、篡改一字节、非法位深必须被拒、
#         抹掉 ICC 后标签尺子必须判"缺"。**没有负控的裁判等于没有裁判。**
#         四项在 §7 还有一笔"到场账"：任何一项没被尝试过（= 中途被打断）就是汇总红。
#
# 夹具的诚实性：源空间的 P3/BT.2020 图由 `zscale + iccgen` **真转换**生成（像素与 ICC 同源），
#   不是「sRGB 数值挂个 P3 标签」。Adobe RGB 一列只能走 `--icc-file`（CICP 无此空间），
#   ⇒ 那一列是「同一批编码数值按 Adobe 原色重解释」，两把尺子都做同样重解释，比较仍成立，但**不是真 Adobe 照片**（见汇总 SKIP 注记）。
#
# 用法：pwsh -NoProfile -File tests/scripts/verify-color-matrix-full.ps1 [-Layer quick|full] [-Csv <path>]
param([string]$Layer = 'quick', [string]$Csv = '')
$ErrorActionPreference = 'Continue'
if ($Layer -notin @('quick', 'full')) { Write-Output "LAYER-ARG-ERROR 只接受 quick|full（实得 $Layer）"; exit 2 }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
. (Join-Path $PSScriptRoot '_lib-oracle.ps1')
Set-OracleConfig -RepoRoot $root -TimeoutSec 180 | Out-Null

$avail = Test-OracleAvailable
$need = @('ffmpeg', 'ffprobe', 'exiftool')
$missing = @($need | Where-Object { $avail.Available -notcontains $_ })
function Say($t) { Write-Output $t }
$script:pass = 0; $script:fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Say "  PASS $msg" } else { $script:fail++; Say "  FAIL $msg" } }

$ff = Get-ToolPath 'ffmpeg'
$ex = Get-ToolPath 'exiftool'
$exe = Join-Path $root 'src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe'
CK ($missing.Count -eq 0) "前置：oracle 工具齐备（缺 = $(if ($missing) { $missing -join ',' } else { '无' })）"
CK (Test-Path $exe) "前置：被测产品 exe 在位 $exe"
if ($script:fail -gt 0) { Say 'PRECHECK-FAIL ⇒ 本轮不作数'; exit 2 }

$d = Join-Path $root ('tests/output/validate/colormx_' + [guid]::NewGuid().ToString('N').Substring(0,8)); New-Item -ItemType Directory -Force -Path $d | Out-Null
function ExecCli([string]$file, [string]$argStr, [string]$Tee = '') {
  $o = Join-Path $d ('o_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
  $e = "$o.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e
  $txt = ((Get-Content -LiteralPath $o -Raw -EA SilentlyContinue) + (Get-Content -LiteralPath $e -Raw -EA SilentlyContinue))
  # -Tee：把该格的 stdout+stderr 全文**留在现场**（CAP 判"有没有播报"要读它，红的时候也要能复查）。
  if ($Tee -ne '') { Set-Content -LiteralPath $Tee -Value $txt -Encoding UTF8 -EA SilentlyContinue }
  [pscustomobject]@{ Rc = $p.ExitCode; Out = $txt }
}
# 产品把 -o 当**输出目录**用（实测 art.png/ 里躺 art.png/in.png）⇒ 任何"取产物"都必须走这里，
# 不能拿 -o 的路径直接喂给 ffprobe/ReadAllBytes（Test-Path 对目录也返回 True ⇒ 静默假绿）。
function Resolve-Prod([string]$p) {
  if (Test-Path -LiteralPath $p -PathType Leaf) { return $p }
  if (Test-Path -LiteralPath $p -PathType Container) {
    $f = Get-ChildItem -LiteralPath $p -File -EA SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
    if ($f) { return $f.FullName }
  }
  return $null
}

# ── CAP 判据：请求位深 ≠ 容器能力档时，该格有三种结局 ────────────────────────────────
# 事实基线（生产代码 `ColorStrategyPlanning.cs` 的 `CollectDegradations`，只读参考）：
#   RGB 原生出口（png/apng/tiff/jxl）的像素格式只有 8/16 两档（rgb24 / rgb48le）⇒ 请求 10/12
#   必然被取到 16（升位）、请求高于出口的必然被取到 8 或 16（降位）。产品的既定设计是**照做但播报**：
#   升位走 `BitDepthRaised`、降位走 `BitDepthReduced`，日志原文形如
#   `[色彩引擎] ⚠️ 降级（BitDepthRaised）：出口位深升为 16bit（本次目标 12bit）⇒ …`。
# ⇒ 判据必须**读该格日志**，把「已播报的升档 / 已播报的降档 / 真静默」分开，只有真静默（和不干净
#   的拒绝）算违规。名字一律按"它判的是什么"起：`raised-announced` / `reduced-announced` /
#   `SILENT-MISMATCH` —— 不再有一个写着 SILENT 却其实是已播报的档。
# 交付位深只用于**复核播报方向**（能测出来时才用）：`BitDepth*` 说升、实测却更浅 ⇒ `MISANNOUNCED`，
#   这一条是防止"只要日志里有码就放行"退化成新的假绿。测不出就退回只看播报、不猜 ——
#   ⚠⚠ **2026-09-26 任务 #41：本表原先缺 8bit YUV 那一族名字**（只有 `yuv420p10le/12le/16le`，没有
#     `yuv420p / yuv422p / yuv444p / yuva420p / yuvj420p`）⇒ 凡交付 8bit YUV 的格子 `delivered` 恒 $null ⇒
#     "播报 vs 实测"两条 MISANNOUNCED 判据在 **avif 18 格 + webp 54 格上从不执行**（实测：全表
#     `announced=True 且 delivered 空` 恰为 avif=18 / webp=54，取证 `tests/output/t36/check_announced.ps1`）。
#     本表上方旧注释把这条写成"webp 与 avif 给不出位深"，**那是把尺子缺词误记成容器的性质** ——
#     ffprobe 对这些容器一直给得出 `pix_fmt`（`yuv420p`），只是本表认不出来。补齐后 #36 纳入的
#     avif 12 档才真正被承诺账管住（`avif × Display P3` 的 bd=10/12 那 12 格"表说支持、交付 8"会转红，
#     红的主角是 #39 那条无实测支撑的折档，不许靠改本表或放宽承诺账消红）。
$pixDepth = @{
  'rgb24' = 8; 'rgba' = 8; 'bgra' = 8; 'argb' = 8; 'abgr' = 8; 'gray' = 8; 'pal8' = 8; 'gbrp' = 8; 'gbrap' = 8
  'yuv420p' = 8; 'yuv422p' = 8; 'yuv444p' = 8; 'yuva420p' = 8; 'yuva444p' = 8; 'yuvj420p' = 8; 'yuvj444p' = 8
  'gbrp10le' = 10; 'gbrap10le' = 10; 'yuv420p10le' = 10; 'yuv444p10le' = 10; 'yuv422p10le' = 10; 'yuva420p10le' = 10
  'gbrp12le' = 12; 'gbrap12le' = 12; 'yuv420p12le' = 12; 'yuv444p12le' = 12; 'yuv422p12le' = 12; 'yuva420p12le' = 12
  'rgb48le' = 16; 'rgb48be' = 16; 'rgba64le' = 16; 'gbrp16le' = 16; 'gbrap16le' = 16
  'gray16le' = 16; 'gray16be' = 16; 'yuv420p16le' = 16; 'yuv444p16le' = 16
}
function Get-DeliveredDepth([string]$Pix) {
  if ([string]::IsNullOrEmpty($Pix)) { return $null }
  $k = $Pix.ToLowerInvariant()
  if ($pixDepth.ContainsKey($k)) { return $pixDepth[$k] }
  return $null
}
function Get-BitDepthOutcome {
  param(
    [int]$Requested,        # 本格请求的 --bit-depth
    [bool]$Made,            # 有没有拿到**文件**产物
    [int]$Rc,               # 进程退出码
    [string]$Log,           # 本格 stdout+stderr 全文（播报与否只看它）
    $Delivered              # 实测交付位深；$null = 该容器测不出来
  )
  if (-not $Made) {
    if ($Rc -ne 0) { return @{ Verdict = 'refused'; Evidence = "干净拒绝 rc=$Rc" } }
    return @{ Verdict = 'REFUSE-UNCLEAN'; Evidence = '无产物却 rc=0 ⇒ 拒绝不干净' }
  }
  $raised = ($Log -match 'BitDepthRaised')
  $reduced = ($Log -match 'BitDepthReduced')
  if ((-not $raised) -and (-not $reduced)) {
    return @{ Verdict = 'SILENT-MISMATCH'; Evidence = "有产物、请求 $Requested 不在容器能力档内、日志零播报 ⇒ 真静默" }
  }
  if ($raised -and $reduced) {
    return @{ Verdict = 'MISANNOUNCED'; Evidence = '同一格同时播升位与降位 ⇒ 播报自相矛盾' }
  }
  $canCheck = ($null -ne $Delivered)
  if ($raised -and $canCheck -and ([int]$Delivered -lt $Requested)) {
    return @{ Verdict = 'MISANNOUNCED'; Evidence = "播升位，实测交付 $Delivered < 请求 $Requested" }
  }
  if ($reduced -and $canCheck -and ([int]$Delivered -gt $Requested)) {
    return @{ Verdict = 'MISANNOUNCED'; Evidence = "播降位，实测交付 $Delivered > 请求 $Requested" }
  }
  if ($raised) { return @{ Verdict = 'raised-announced'; Evidence = "BitDepthRaised（升位不丢精度；实测 $Delivered）" } }
  return @{ Verdict = 'reduced-announced'; Evidence = "BitDepthReduced（降位已点名；实测 $Delivered）" }
}

# ── 1) 夹具：真转换出来的 P3 / BT.2020 源 ────────────────────────────────────────────
Say '== 1) 夹具 =='
$base8 = Join-Path $d 'base8.png'; $base16 = Join-Path $d 'base16.png'
ExecCli $ff "-hide_banner -loglevel error -y -f lavfi -i testsrc2=s=256x192:r=1 -frames:v 1 $base8" | Out-Null
ExecCli $ff "-hide_banner -loglevel error -y -i $base8 -frames:v 1 -pix_fmt rgb48le $base16" | Out-Null
CK ((Test-Path $base8) -and (Test-Path $base16)) '基准图 8/16-bit 就位'

$srcs = [ordered]@{}
$srcs['srgb8'] = $base8
$srcs['srgb16'] = $base16
foreach ($pair in @(@('p3', 'smpte432', 'iec61966-2-1'), @('bt2020', 'bt2020', 'bt709'))) {
  $tag = $pair[0]; $pp = $pair[1]; $tt = $pair[2]
  foreach ($bpp in @(8, 16)) {
    $o = Join-Path $d "src_${tag}_$bpp.png"
    $in = if ($bpp -eq 8) { $base8 } else { $base16 }
    ${outPix} = 'rgb24'; if ($bpp -eq 16) { ${outPix} = 'rgb48le' }
    # iccgen **没有 iccfile 选项**（本机 ffmpeg 实测：只有 color_primaries/color_trc 等）⇒ 它把 profile 附在帧上，
    #   写 PNG 时自动进 iCCP。写成 `iccgen=iccfile=...` 会让整条命令失败 ⇒ 上一轮夹具四格全空。
    $fr = ExecCli $ff ("-hide_banner -loglevel error -y -i `"$in`" -vf `"format=rgb48le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:p=${pp}:t=${tt}:m=gbr,iccgen,format=${outPix}`" -frames:v 1 `"$o`"")
    if (Test-Path -LiteralPath $o -PathType Leaf) { $srcs["${tag}$bpp"] = $o }
    else { Say ("  夹具失败 {0}/{1} rc={2} ⇒ {3}" -f $tag, $bpp, $fr.Rc, (($fr.Out -replace "`r?`n", ' | ').Substring(0, [Math]::Min(300, $fr.Out.Length)))) }
  }
}
CK ($srcs.Count -ge 4) "夹具计数 = $($srcs.Count)（$($srcs.Keys -join ' ')）"
$adobeIcc = @(Get-ChildItem "$env:SystemRoot\System32\spool\drivers\color" -Recurse -Include '*.icc', '*.icm' -EA SilentlyContinue | Where-Object { $_.Name -match 'Adobe' }) | Select-Object -First 1
if ($adobeIcc) { $srcs['adobe8'] = $base8; Say "  Adobe RGB 轴用权威 ICC：$($adobeIcc.FullName)（源=base8 + --icc-file ⇒ 同数值重解释，非真 Adobe 照片）" }
else { Say '  本机无 Adobe ICC ⇒ Adobe 轴记 SKIP' }

# ── 2) 网格与容器能力表 ─────────────────────────────────────────────────────────────
# 值 = 该容器**能**承载的出口位深；不在表内 ⇒ 产品必须显式拒绝（CAP 尺子）
# ⚠ 这张表的口径是「**本产品实际交付得了哪几档**」，不是「码流格式理论上能表达哪几档」——
#   png/tiff 两行已经体现了这一点：PNG 规范允许 1/2/4/8/16、TIFF 允许 10/12，但本引擎出口
#   只有 rgb24 / rgb48le 两档 ⇒ 表里就是 @(8,16)。
# ⚠⚠ **jxl 一行于 2026-09-26 由 @(8,10,12,16) 收窄为 @(8,16)**（任务 #35，实测依据如下）：
#   1) JXL **码流**确实支持 10/12bit（ISO 18181-1 的 bitDepth 允许 8/10/12 与 16/浮点）——
#      这一句是"表写宽了 vs 产品少做了"里唯一站在"表"那边的论据，所以下面必须用实测把它压回去；
#   2) 本产品的 jxl 出口拿不到 10/12 档：规划层 `p.OutBitDepth` 值域 {8,16}（见
#      `ColorStrategyPlanning` ①b），`ImageEncoderArgs.MapPixFmt('jxl', …)` 明写
#      「bd<=8 ? rgb24 : rgb48le」⇒ 喂给编码器的永远是 8bit 或 48bit(16) 像素；
#   3) **实测读回**（`--bit-depth 8|10|12|16 -f jxl --color-space sRGB --log-level Debug`，
#      产物用 `publish/PLAN/jxl/bin/jxlinfo.exe` 读，⚠ 不是 ffprobe —— ffprobe 报的 pix_fmt 是
#      **解码输出**格式，不是码流位深，拿它当位深真值就是本轮要避免的第二个坑）：
#        jxl8  → `JPEG XL image, 64x48, lossy, 16-bit RGB`
#        jxl10 → `JPEG XL image, 64x48, lossy, 16-bit RGB`
#        jxl12 → `JPEG XL image, 64x48, lossy, 16-bit RGB`
#        jxl16 → `JPEG XL image, 64x48, lossy, 16-bit RGB`
#      16bit 源 ⇒ 四格**全部**读出 16-bit 码流，没有任何一格产出 10/12；
#   4) 产品自己在这些格上**已经响亮播报**了取档：`降级（BitDepthRaised）：出口位深升为 16bit
#      （本次目标 10bit）⇒ …原因=jxl 出口的 RGB 原生像素格式只有 8/16 两档（rgb24/rgb48le）`
#      （实测原文见 tests/output/t35/p_jxl10.log）⇒ 判据把它们归到 `raised-announced` 是
#      与产品口径**一致**的，把它们留在 `ok` 才是要造一个"承诺了 10/12 却从不兑现"的假事实。
#   ⇒ 于是"能力表与产品口径不一致"的 24 格（jxl10/jxl12 × 6 源 × 2 目标）**由改表收口**，
#     而不是靠放宽 `Get-BitDepthOutcome`。⚠ 本条写成时 BT.2020 那一列是 `refused-semantics`、
#     不入 ok 集；**2026-09-26 任务 #37 之后那一列会出产物**（择一不再剥掉生成 ICC），
#     jxl×BT.2020 的 8/16 两档因此**新进入 ok 集**，LBL/NUM 第一次真正对拍这 12 格的像素。
#   ⚠ 代价（明账，不许糊）：jxl10/jxl12 共 36 格从 `verdict -eq 'ok'` 集合里移出 ⇒
#     LBL/NUM/RT 三把尺子对这两档**不再对拍**（脚本 §3/§4/§5 的过滤器就是按 ok 取的）。
#     png10/png12/tiff10/tiff12 早就在同一待遇下，所以这不是新引入的覆盖缺口，而是
#     把 jxl 对齐到与 png/tiff 相同的既有口径。
#   ⚠ 仍待主循环裁定（不在本轮范围，也**不**由本表判红）：`FormatCapabilitiesService.SeedLocalRules`
#     的 `jxl.SupportedBitDepths = {8,10,12,16,32}` 与 UI 位深下拉同样**过宽**（引擎出口只出 8/16）
#     ⇒ 用户能在下拉里选到 10/12。那是"下拉给了选不出来的档位"，属能力表侧的口径修复，
#     归属另一个 lane（本脚本改不到它，也不该由网格替它背书）。
#   ✅ `avif = @(8, 10, 12)`（**2026-09-26 第二笔：任务 #36 修好后重新放开 12**）：
#     本表曾收窄到 @(8,10)，理由写在下面这条历史里 —— 因为产品当时**确实**把 12 钳成 10。
#     那个钳制的依据是 `AvifMaxBitDepth` 按 `options.Encoder` 前缀判档，而**空编码器名**（CLI/headless
#     唯一可能的形态：`--encoder` 映射到 EncoderBackend 枚举，写不到编码器名）被算成"上限 10"。
#     实测反驳（取证 `tests/output/t36/`）：命令里不写 `-c:v` ⇒ ffmpeg 为 .avif 选的默认编码器是
#     **libaom-av1**（自报表含 yuv420p12le/422p12le/444p12le/gbrp12le），直接喂同一输入
#     `-c:v libaom-av1 -pix_fmt yuv420p12le` ⇒ rc=0、产物 pix_fmt=yuv420p12le
#     ⇒ "12 从来不是本路径的承诺"这句**当时成立、修后不成立**：产品现在就在跑 libaom-av1。
#     ⇒ 已把上限改为**由实际会跑的编码器推**（`AvifMaxBitDepthForEncoder` 复用 `ClassifyAvifBackend`，
#       空名 ⇒ Libaom ⇒ 12），并由 `verify-decision-delivery` ⑯ 与 ffmpeg 自报表逐编码器对账。
#   ⚠ 历史（收窄的原始理由，保留备查）：产品对 avif 的**声明上限**由
#     `ImageEncoderArgs.AvifMaxBitDepth` 给 —— 未显式选编码器时恒 **10**（保守取候选编码器的交集：
#     libsvtav1 只到 10），实测（`tests/output/t35/grid-t35fix.csv`，full 层 12 格 avif12）交付
#     `-pix_fmt yuv420p10le` 并播报"降为 10bit（本次目标 12bit）"⇒ 当时产品兑现了自己的声明、没有静默。
$capDepths = @{ png = @(8, 16); webp = @(8); tiff = @(8, 16); jxl = @(8, 16); avif = @(8, 10, 12) }
$targets = @{ 'sRGB' = 'bt709'; 'Display P3' = 'smpte432'; 'BT.2020' = 'bt2020' }
# 目标空间 → **裁判工具实际输出的词形**（同一张表现在由 §3 LBL 与 §6c 的 png/tiff 标签判据共用，
# 不再各写一份：判据词形表一分家就各自漂移，而"缺一个词形"的代价是把**正确的标注判成缺失**）。
# ⚠ 返回空串 = 本表缺这一档 ⇒ 调用侧必须按「取数失败」处理：**绝不**能拿空 regex 去 -match
#   （`('任意串' -match '')` 恒真 ⇒ 判据当场失去判别力，这是最隐蔽的一种假绿）。
function Get-TargetPrimariesRx([string]$Tgt) {
  if ($Tgt -eq 'sRGB')       { return '(?i)bt709|BT\.?709|sRGB' }
  if ($Tgt -eq 'Display P3') { return '(?i)smpte432|EG 432|P3' }
  if ($Tgt -eq 'BT.2020')    { return '(?i)bt2020|BT\.?2020|BT\.?2100|Rec\.?\s?2100' }
  return ''
}
$fmts = if ($Layer -eq 'quick') { @('png', 'jxl') } else { @('png', 'webp', 'tiff', 'jxl', 'avif') }
$depths = if ($Layer -eq 'quick') { @(8, 16) } else { @(8, 10, 12, 16) }
Say "== 2) 网格 $Layer ：源 $($srcs.Count) × 目标 $($targets.Count) × 容器 $($fmts.Count) × 位深 $($depths.Count) =="

# ── 2a) CAP 判据自测：合成行，零转换成本 ─────────────────────────────────────────────
# 新判据比旧的宽松（已播报不再算红），所以**必须先自证它还有牙**，否则就是"为了变绿而放宽"。
# 这五条不打产品、不建图，喂的是下面这些字符串本身 ⇒ 跑一次几毫秒。
Say '== 2a) CAP 判据自测（合成行，不产生任何真实转换）=='
# 牙 1：日志里**没有任何降级码** ⇒ 必须仍判违规。这行故意把升降用**人话**说清楚了
#   （"升为 16bit（本次目标 12bit）"）⇒ 判据要是靠读人话，这条就会被它骗成合规。
$synNoCode = '[色彩引擎] ⚠️ 出口位深升为 16bit（本次目标 12bit）：像素精度不损失，代价是产物更大'
$cs1 = Get-BitDepthOutcome -Requested 12 -Made $true -Rc 0 -Log $synNoCode -Delivered 16
CK ($cs1.Verdict -eq 'SILENT-MISMATCH') "CAP 自测1 无降级码（只有人话）的合成行 => 判违规（实得 $($cs1.Verdict)）"
# 牙 2：产品**实际打出的那一行**（2026-09-26 实测：--bit-depth 12 打到 png，见 §验收）⇒ 必须判合规
$synRaised = '[色彩引擎] ⚠️ 降级（BitDepthRaised）：出口位深升为 16bit（本次目标 12bit）⇒ **升位、非降级**：像素精度不损失，代价是产物更大。原因=png 出口的 RGB 原生像素格式只有 8/16 两档（rgb24 / rgb48le）⇒ 目标 12bit 无对应出口档，被取到 16bit'
$cs2 = Get-BitDepthOutcome -Requested 12 -Made $true -Rc 0 -Log $synRaised -Delivered 16
CK ($cs2.Verdict -eq 'raised-announced') "CAP 自测2 有 BitDepthRaised 播报的合成行 => 判合规（实得 $($cs2.Verdict)）"
$cs3 = Get-BitDepthOutcome -Requested 32 -Made $true -Rc 0 -Log '[色彩引擎] ⚠️ 降级（BitDepthReduced）：出口位深降为 16bit（本次目标 32bit）⇒ 高于出口档的精度不保留。原因=tiff 出口只有 8/16 两档' -Delivered 16
CK ($cs3.Verdict -eq 'reduced-announced') "CAP 自测3 有 BitDepthReduced 播报 => 判合规（实得 $($cs3.Verdict)）"
# 牙 4：播了升位、实测交付却**更浅** ⇒ 播报不等于做到 ⇒ 仍红（这条是新增判据自己的负控）
$cs4 = Get-BitDepthOutcome -Requested 12 -Made $true -Rc 0 -Log $synRaised -Delivered 8
CK ($cs4.Verdict -eq 'MISANNOUNCED') "CAP 自测4 播升位但实测 8 < 请求 12 => 判违规（实得 $($cs4.Verdict)）"
$cs5 = Get-BitDepthOutcome -Requested 12 -Made $false -Rc 0 -Log '' -Delivered $null
$cs6 = Get-BitDepthOutcome -Requested 12 -Made $false -Rc 3 -Log '拒绝：容器不支持 12bit' -Delivered $null
CK (($cs5.Verdict -eq 'REFUSE-UNCLEAN') -and ($cs6.Verdict -eq 'refused')) "CAP 自测5 无产物的两种收口 => 红/绿分明（实得 $($cs5.Verdict) / $($cs6.Verdict)）"

$rows = New-Object System.Collections.Generic.List[object]
foreach ($sk in $srcs.Keys) {
  $srcP = $srcs[$sk]
  $extra = ''
  if ($sk -like 'adobe*') { $extra = "--icc-file `"$($adobeIcc.FullName)`"" }
  foreach ($tn in $targets.Keys) {
    foreach ($f in $fmts) {
      foreach ($bd in $depths) {
        # ⚠ 产品会把**带扩展名的 -o 当成输出目录**（实测 `art.png/` + 里面 `art.png/in.png`）⇒
        #   "有产物"必须是"存在**一个文件**"，不能用 Test-Path（它对目录也返回 True ⇒ 静默假绿）。
        $outDir = Join-Path $d ("m_{0}_{1}_{2}{3}" -f $sk, ($tn -replace '[^A-Za-z0-9]', ''), $f, $bd)
        # 本格日志**与产物目录同级、不同名**（`<目录>.log`）：不写进目录里，是因为取产物走的是
        #   "目录里最大的文件"那套语义（Resolve-Prod）⇒ 把裁判要读的证据放进产物目录，等于自己喂自己。
        $cellLog = "$outDir.log"
        # --log-level Debug：LBL 的"缺标注必须有名字"要看的是降级播报，而它**不在 Info 级别**（Lane E 是在 Debug 下看到的）
        $r = ExecCli $exe "--headless --log-level Debug -i `"$srcP`" -o `"$outDir`" -f $f --bit-depth $bd --color-space `"$tn`" --color-engine engine $extra" -Tee $cellLog
        # 取产物只有一条路：Resolve-Prod（Leaf 原样 / Container 取最大文件 / 没有 ⇒ $null）
        $prod = Resolve-Prod $outDir
        $made = ($null -ne $prod)
        $st = if ($made) { Get-StreamInfo $prod } else { $null }
        $cm = if ($made) { Get-ColorMetadata $prod } else { $null }
        $delivered = if ($st -and $st.Ok) { Get-DeliveredDepth ([string]$st.PixFmt) } else { $null }
        $capOk = ($capDepths[$f] -contains $bd)
        # ⚠⚠ 这段"合法拒绝"前提**已作废并删除**（2026-09-26 任务 #37 实测）：
        #   旧事实（2026-09-25 取证，审计 §6-C）：cjxl 的 `color_space` 枚举只有 sRGB / Display P3 /
        #   Rec.2100 PQ / HLG ⇒ SDR BT.2020 无处可标 ⇒ 产品拒绝产出（当时 rc=1、零产物，共 24 格）。
        #   新事实：**枚举装不下不等于这格式做不到** —— `EnforceContainerAnnotationRules` 的择一原先
        #   留着写不出的 CICP、剥掉唯一能兑现的生成 ICC；修后该 24 格出产物，cjxl 命令行带
        #   `-x icc_pathname=…stdicc_bt2020_bt709_bt2020nc.icc`，jxlinfo 读回
        #   「RGB, D65, **Rec.2100 primaries, 709 transfer function**」（单格实测
        #   `tests/output/t35/probe-jxl-cell.ps1`；ICC 通路本身的 PPM 二分实测见
        #   `tests/output/t35/probe-jxl-bt2020-icc.ps1`）。
        #   ⇒ 这些格现在按**普通格**判（LBL 查标注是否真落地、NUM 与 zimg 参照对拍像素），
        #     不再享受"必须响亮拒绝"的特殊待遇；若某环境 cjxl 缺失而 rc≠0 零产物，仍会被
        #     `Get-BitDepthOutcome` 判成 `refused`（干净拒绝）而不是放过。
        $capNote = ''
        $verdict = if ($capOk) { if ($made -and $r.Rc -eq 0) { 'ok' } else { 'CAP-FAIL' } }
                   # 能力表说不支持的档位：读该格日志判三种结局（见 §2a 的自测）。
                   else { $o = Get-BitDepthOutcome -Requested $bd -Made $made -Rc $r.Rc -Log $r.Out -Delivered $delivered
                          $capNote = $o.Evidence; $o.Verdict }
        $rows.Add([pscustomobject]@{
            src = $sk; tgt = $tn; fmt = $f; bd = $bd; rc = $r.Rc; made = $made; verdict = $verdict
            announced = ($r.Out -match 'BitDepthRaised|BitDepthReduced')
            out = $prod; pixfmt = $(if ($st) { [string]$st.PixFmt } else { '' }); prim = $(if ($cm) { $cm.Primaries } else { '' })
            trc = $(if ($cm) { $cm.Transfer } else { '' })
            delivered = $delivered; capNote = $capNote; cellLog = $cellLog
            # ⚠ 字段名必须照 `Get-ColorMetadata` 的真实形状取：它给的是 `HasIcc` / `IccProfileName` /
            #   `IccProfileDescription`，**没有** `.IccDescription`（我上一版读错 ⇒ 恒空 ⇒ 把带 ICC 的 TIFF
            #   判成"无标注"，62 格里的一半就是这么来的）。
            hasIcc = $(if ($cm) { [bool]$cm.HasIcc } else { $false })
            # 日志里有没有"我选择无标注"的点名（降级码）⇒ LBL 只打**静默**无标注，见 §3 的判据
            loud = ($r.Out -match 'UnlabeledAssumedSrgb|降级')
            icc = $(if ($cm) { "$($cm.IccProfileName)|$($cm.IccProfileDescription)" } else { '' })
        })
        # 逐格打进度：360 格的黑盒跑法一旦断线就看不出跑到哪（第一次 full 层中断在 105/360 就是这样）
        $tot = $srcs.Count * $targets.Count * $fmts.Count * $depths.Count
        Say ("  [{0,3}/{1}] {2,-9} →{3,-11} {4,-5}{5,-3} {6,-18} rc={7} prod={8} got={9}" -f `
            $rows.Count, $tot, $sk, $tn, $f, $bd, $verdict, $r.Rc, $(if ($prod) { Split-Path -Leaf $prod } else { '-' }), `
            $(if ($null -ne $delivered) { $delivered } else { '?' }))
      }
    }
  }
}
# 违规只有四类（其余 verdict 都是"产品兑现了它说过的话"）：
#   CAP-FAIL        能力表说支持却没出产物/非零退出，或 jxl 语义拒绝不响亮
#   SILENT-MISMATCH 请求档不被容器支持、出了产物、日志里**零播报** ⇒ 这才是真静默
#   MISANNOUNCED    播报的方向与实测交付位深相反（或自相矛盾）⇒ 播了但没做到
#   REFUSE-UNCLEAN  没有产物也没有非零退出 ⇒ 既不交付也不报错
$bad = @($rows | Where-Object { $_.verdict -in @('CAP-FAIL', 'SILENT-MISMATCH', 'MISANNOUNCED', 'REFUSE-UNCLEAN') })
foreach ($b in ($bad | Select-Object -First 12)) { Say ("  CAP? {0,-9} →{1,-11} {2}{3} verdict={4} rc={5} made={6} got={7} 依据={8}" -f $b.src, $b.tgt, $b.fmt, $b.bd, $b.verdict, $b.rc, $b.made, $(if ($null -ne $b.delivered) { $b.delivered } else { '?' }), $b.capNote) }
function CountVerdict([string]$v) { return @($rows | Where-Object { $_.verdict -eq $v }).Count }
$capLine = ("CAP 位深/容器账：{0} 格里违规 {1}（真静默 {2} · 播报与实测矛盾 {3} · 拒绝不干净 {4} · CAP-FAIL {5} || 已播报升档 {6} · 已播报降档 {7} · 干净拒绝 {8}）" -f `
    $rows.Count, $bad.Count, (CountVerdict 'SILENT-MISMATCH'), (CountVerdict 'MISANNOUNCED'), (CountVerdict 'REFUSE-UNCLEAN'), (CountVerdict 'CAP-FAIL'), (CountVerdict 'raised-announced'), (CountVerdict 'reduced-announced'), (CountVerdict 'refused'))
CK ($bad.Count -eq 0) $capLine
# CAP 的**承诺账**：能力表说"这一档该容器承载得了"，产品却在同一格播报了**与请求不同的交付档** ⇒ 口径不一致。
#   2026-09-26 full 层实测：jxl 10/12 共 24 格，产品原文"jxl 出口的 RGB 原生像素格式只有 8/16 两档"，
#   ffprobe 实测 pix_fmt=rgb48le ⇒ 是 `$capDepths.jxl` 里的 10/12 过期（裁判的前提，不是产品的错）。
#   ✅ **同日已按那条证据把表收窄**（jxl ⇒ @(8,16)，逐格实测与 jxlinfo 读回见 §2 上方注释）
#      ⇒ 这 24 格现在归 `raised-announced`（产品播报了升位）而不再计入承诺账。
#   ✅ **同日第二笔（任务 #35 播报修复当场量出来的）**：`avif 12` 共 12 格，产品交付 `yuv420p10le`
#      并播报"降为 10bit（本次目标 12bit）"⇒ 承诺账把它算成"表说支持、产品却交付 10"。
#      按 `$capDepths.avif ⇒ @(8,10)` 收窄（理由与"编码器在钳制之后才已知 ⇒ 本可 12"这笔未清的账，
#      见 §2 上方 `$capDepths` 的注释）。**不是**为了让绿灯而改尺子：改前改后产品行为一字未动，
#      变的是"本路径从未承诺过 12"这一事实。
#      ⚠ **同日第三笔（任务 #36）把这条收窄撤销了**：那笔账的根因是"钳制发生在编码器解析之前"，
#      已改成按**实际会跑的编码器**定上限 ⇒ avif 12 现在真交付 12bit（本表重新纳入 12）。
#      ⇒ 本账对 avif 12 的 12 格**第一次**要求"表支持 ⇒ 产品必须交付同一档"，
#        红就是回归（又钳回 10 的话，这些格会以 `reduced-announced` 出现在 CAP 里、并计入承诺账）。
#   ⚠ 位深真值**不取 ffprobe 的 pix_fmt**：那是**解码输出**格式（对 JXL 恒给 rgb48le 一类），
#      不等于码流位深；JXL 用 `jxlinfo` 读回。本账只在"裁判测得出来"的格上比对（`delivered` 为
#      $null ⇒ 只比播报，不猜）。
#   ⚠ 必须比对**实测交付 ≠ 请求**才算：`BitDepthReduced` 还有一个来源是"出口按容器上限 8bit 交付并做
#     有序抖动"（①），那种格子请求本来就是 8、交付也是 8 ⇒ 不是"承诺≠交付"（png8/tiff8/jxl8 共 36 格
#     就是这么播报的，拿"日志里有码"当判据会把它们误计成红）。
#   这里不改 verdict（那些格仍归 LBL/NUM 判；改判会把它们送出对拍范围 ⇒ coverage 反而掉），
#   单独判红是为了"承诺≠交付"不许静默：要么改表，要么产品补上 10/12 出口档。
$promOk = @($rows | Where-Object { $_.verdict -eq 'ok' })
$promBad = @($promOk | Where-Object { $_.announced -and ($null -ne $_.delivered) -and ([int]$_.delivered -ne [int]$_.bd) })
foreach ($b in ($promBad | Select-Object -First 6)) { Say ("  CAP承诺? {0,-9} →{1,-11} {2}{3} 能力表说支持、产品却交付了 {4}bit" -f $b.src, $b.tgt, $b.fmt, $b.bd, $b.delivered) }
CK ($promBad.Count -eq 0) "CAP 承诺账：判 ok 的 $($promOk.Count) 格里『播报了升降档且实测交付 ≠ 请求』的 = $($promBad.Count)（>0 ⇒ 能力表与产品口径不一致：改表或改产品，不许静默）"

# ── 3) LBL 标签账 ───────────────────────────────────────────────────────────────────
Say '== 3) LBL 标签回读 =='
$lblBad = New-Object System.Collections.Generic.List[string]
$lblN = 0
foreach ($g in ($rows | Where-Object { $_.verdict -eq 'ok' })) {
  $lblN++
  $wantTok = $targets[$g.tgt]
  # ⚠ 判据必须按**裁判工具实际输出的形状**匹配：exiftool/jxlinfo 给人话名（'SMPTE EG 432-1'/'P3 primaries'/
  #   'BT.2020, BT.2100'/'sRGB primaries'），ffprobe 给 CICP token ⇒ 只比 token 会把**正确的标注判成缺失**
  #   （本轮第一次跑就是这么假红了 20 格）。
  # ⚠ 2026-09-26 同一族的**第二个**词形：jxlinfo 报 BT.2020 原色时写的是 **`Rec.2100 primaries`**
  #   （Rec.2100 用的就是 BT.2020 原色集），旧式 `BT\.?2100` 咬不住 ⇒ 任务 #37 交付成功的
  #   4 格 `jxl × BT.2020 × 16bit` 被本尺子判成"静默无标注"（实测 4 条 LBL? 行，prim 字段就是
  #   'Rec.2100 primaries'）。⇒ 加 `Rec\.?\s?2100`，**只扩词形、不扩语义**：这条 regex 仍然只认
  #   "原色是 2020 家族"，不会因此放过别的原色。
  # ⚠ 词形表已上移成 `Get-TargetPrimariesRx`（§6c 的 png/tiff 标签判据共用同一张表 ⇒ 只有一份尺子）。
  $rx = Get-TargetPrimariesRx $g.tgt
  $tokHit = ($g.prim -match $rx)
  # 判"有没有带标注"用 `HasIcc` 这个布尔；**不要**拿 ICC 描述文字当色域证据
  # （本仓既有结论：iccgen 生成的 ICC 描述恒为 "RGB built-in"，识别色域必须按色度+曲线量）。
  $iccHit = [bool]$g.hasIcc
  # 「无标注」只有在**静默**时才算缺陷：webp 的 sRGB 目标本来就无 CICP 通路、也不嵌 ICC，
  #   但产品会打 `降级（UnlabeledAssumedSrgb）` 且 `裁决=ProceedWithDegradation`（Lane E 实测到的声明处）。
  #   ⇒ 判据写成"缺标注 ⇒ 必须有名字"，这样既不放走静默无标注，也不把合法出口判成红。
  if (-not ($tokHit -or $iccHit) -and -not $g.loud) { $lblBad.Add("$($g.src)→$($g.tgt) $($g.fmt)$($g.bd) [prim='$($g.prim)' 静默无标注]") }
}
foreach ($x in ($lblBad | Select-Object -First 10)) { Say "  LBL? $x" }
CK ($lblBad.Count -eq 0) "LBL 标签账：$lblN 个成功格里，既无 CICP 命中又无 ICC 的 = $($lblBad.Count)"

# ── 4) NUM 与 zimg 参照对拍（只判无损容器，lossy 的 PSNR 归 oracle-matrix 管）──────────
Say '== 4) NUM 与 zimg 参照链对拍 =='
$numBad = New-Object System.Collections.Generic.List[string]
$numN = 0; $numErr = 0
foreach ($g in ($rows | Where-Object { $_.verdict -eq 'ok' -and $_.fmt -in @('png', 'tiff', 'jxl') })) {
  $srcTok = switch -Regex ($g.src) { '^srgb' { 'bt709' } '^p3' { 'smpte432' } '^bt2020' { 'bt2020' } default { 'bt709' } }
  $srcTrc = switch -Regex ($g.src) { '^bt2020' { 'bt709' } default { 'iec61966-2-1' } }
  $dtTrc = if ($g.tgt -eq 'BT.2020') { 'bt709' } else { 'iec61966-2-1' }
  $ref = Join-Path $d ('ref_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.png')
  # 参照链：把产物按「它自己声明的源」再转到目标空间；两侧同输入 ⇒ 差异只可能来自矩阵/曲线/量化
  ExecCli $ff "-hide_banner -loglevel error -y -i `"$($g.out)`" -vf `"zscale=pin=$($targets[$g.tgt]):tin=${dtTrc}:min=gbr:p=$($targets[$g.tgt]):t=${dtTrc}:m=gbr,format=rgb48le`" -frames:v 1 `"$ref`"" | Out-Null
  if (-not (Test-Path $ref)) { $numErr++; continue }
  $r = Invoke-Psnr -Ref $ref -Dist $g.out
  if (-not $r.Ok) { $numErr++; Say "  NUM 取数失败 $($g.src)→$($g.tgt) $($g.fmt)$($g.bd) : $($r.Error)"; continue }
  $numN++
  if ([double]$r.Psnr -lt 60) { $numBad.Add("$($g.src)→$($g.tgt) $($g.fmt)$($g.bd) PSNR=$([math]::Round($r.Psnr,2))") }
}
foreach ($x in ($numBad | Select-Object -First 12)) { Say "  NUM? $x" }
CK (($numN -gt 0) -and ($numBad.Count -eq 0)) "NUM 对拍：$numN 格比过，红 $($numBad.Count)，参照取数失败 $numErr"

# ── 5) RT 往返自洽（转置/顺序错的探测器）────────────────────────────────────────────
Say '== 5) RT 往返漂移 =='
$rtBad = New-Object System.Collections.Generic.List[string]
$rtN = 0
foreach ($sk in @('srgb8', 'p38', 'bt20208')) {
  if (-not $srcs[$sk]) { continue }
  foreach ($tn in @('Display P3', 'BT.2020', 'sRGB')) {
    $mid = Join-Path $d "rt1_${sk}_$($tn -replace '[^A-Za-z0-9]', '').png"
    $back = Join-Path $d "rt2_${sk}_$($tn -replace '[^A-Za-z0-9]', '').png"
    $tgt2 = if ($tn -eq 'Display P3') { 'sRGB' } elseif ($tn -eq 'BT.2020') { 'sRGB' } else { 'Display P3' }
    # 往返必须**回到源自己的空间**（源空间由夹具的 ICC 声明），否则"A→B→C"根本不闭合
    $srcName = switch -Regex ($sk) { '^srgb' { 'sRGB' } '^p3' { 'Display P3' } '^bt2020' { 'BT.2020' } default { 'sRGB' } }
    $a = ExecCli $exe "--headless -i `"$($srcs[$sk])`" -o `"$mid`" -f png --bit-depth 16 --color-space `"$tn`" --color-engine engine"
    $midProd = Resolve-Prod $mid
    if (-not $midProd) { continue }
    $b = ExecCli $exe "--headless -i `"$midProd`" -o `"$back`" -f png --bit-depth 16 --color-space `"$srcName`" --color-engine engine"
    $backProd = Resolve-Prod $back
    if (-not $backProd) { continue }
    # 与源比：源是 8-bit、往返产物按 16-bit 承载 ⇒ 允许的差异上限取"8-bit 量化 + 两次矩阵"的量级（45 dB）
    $r = Invoke-Psnr -Ref $srcs[$sk] -Dist $backProd
    if (-not $r.Ok) { continue }
    $rtN++
    if ([double]$r.Psnr -lt 45) { $rtBad.Add("$sk →$tn →$srcName 往返 PSNR=$([math]::Round($r.Psnr,2))（<45 ⇒ 矩阵或顺序有问题，不是量化）") }
  }
}
foreach ($x in $rtBad) { Say "  RT? $x" }
CK (($rtN -gt 0) -and ($rtBad.Count -eq 0)) "RT 往返：$rtN 组，红 $($rtBad.Count)（往返不需要外部参照 ⇒ 转置/复合顺序错只能由它抓）"

# ── 6) NC 负控 ──────────────────────────────────────────────────────────────────────
Say '== 6) NC 负控 =='
# ⚠ 2026-09-26 full 层的实测教训（360 格那次的日志尾巴停在 `PASS NC1b` 之后什么都没有、rc=1）：
#   这一段原先**没有任何到场保证** —— 取样行读到的字段只要是 null，`Copy-Item -LiteralPath` /
#   `Get-ColorMetadata -Path` 这类 Mandatory 参数的绑定错误就是**终止性**的（`$ErrorActionPreference`
#   ='Continue' 也照杀），一抛就把整个驱动打断 ⇒ NC2/NC3/§7 汇总从未执行，而日志看起来"像跑过了"。
#   ⇒ 两处一起改，缺一不可：
#     ① 根因：所有取样一律要求 `out` 非空（产物路径都没登记成功的行，本来就不配当负控样本）；
#     ② 到场：每项过 `RunGate` —— 抛异常时把**异常原文连位置一起打出来**并把该项判红，后面的项照跑。
#        这不是"用 try/catch 吞掉交差"：异常可见、该项必红，且 §7 还有一笔 `NC 到场账`，
#        四项里少一项到场就是汇总红 —— 静默中断这条路被从两头堵死。
$script:ncRan = New-Object System.Collections.Generic.List[string]
function RunGate([string]$Name, [scriptblock]$Body) {
  $script:ncRan.Add($Name)
  try { & $Body }
  catch {
    Say ("  [EXCEPTION] {0} ⇒ {1}" -f $Name, $_.Exception.Message)
    $ii = $_.InvocationInfo
    if ($ii) { Say ("                位置 {0}:{1} 行文: {2}" -f $ii.ScriptName, $ii.ScriptLineNumber, (($ii.Line -replace '\s+', ' '))) }
    CK $false "$Name 抛异常 ⇒ 该尺子本轮未生效（原文见上一行，不是判红后就算跑过）"
  }
}
# NC1 换了做法：翻 IDAT 里的字节会让 PNG **解不开** ⇒ 裁判报 error 而不是低 PSNR，那样测的是「文件坏了」
#   而不是「这把尺子分得出色彩矩阵不同」。所以主判据改成**同源、两个不同目标空间**的产物互比。
RunGate 'NC1' {
  $pa = @($rows | Where-Object { $_.verdict -eq 'ok' -and $_.out -and $_.fmt -eq 'png' -and $_.tgt -eq 'Display P3' }) | Select-Object -First 1
  $pb = @($rows | Where-Object { $_.verdict -eq 'ok' -and $_.out -and $_.fmt -eq 'png' -and $_.tgt -eq 'BT.2020' -and $_.src -eq $pa.src }) | Select-Object -First 1
  if ($pa -and $pb) {
    $r1 = Invoke-Psnr -Ref $pa.out -Dist $pb.out
    $st1 = $r1.Ok -and (-not $r1.IsIdentical) -and ([double]$r1.Psnr -lt 60)
    CK $st1 "NC1 同源的两个不同目标产物互比 ⇒ NUM 必须判为不同（Ok=$($r1.Ok) PSNR=$($r1.Psnr) identical=$($r1.IsIdentical)）"
  } else { CK $false 'NC1 凑不出「同源两目标」产物对 ⇒ 本轮 NUM 那一账不算可信' }
}
# NC1b 保留篡改，但判据写成它真正能证明的东西：裁判**不能**说两者相同（解不开也算它抓住了）
RunGate 'NC1b' {
  $v = @($rows | Where-Object { $_.verdict -eq 'ok' -and $_.out -and $_.fmt -eq 'png' }) | Select-Object -First 1
  if ($v) {
    $bytes = [IO.File]::ReadAllBytes($v.out); $mid = [int]($bytes.Length * 0.6); $bytes[$mid] = $bytes[$mid] -bxor 0xFF
    $tam = Join-Path $d 'nc_tampered.png'; [IO.File]::WriteAllBytes($tam, $bytes)
    $r2 = Invoke-Psnr -Ref $v.out -Dist $tam
    $st2 = (-not $r2.Ok) -or (-not $r2.IsIdentical)
    CK $st2 "NC1b 篡改一个字节后裁判不得判「相同」（Ok=$($r2.Ok) PSNR=$($r2.Psnr) err=$($r2.Error)）"
  } else { CK $false 'NC1b 没有可比对的无损产物 ⇒ 上面 NUM 那一账不算可信' }
}
# NC3 = **LBL 这把尺子自己的牙**：LBL 是负断言（"不许缺标注"），此前没有任何负控能证明它会判出"缺"。
#   做法：拿一个带 ICC 的产物，复制一份用 exiftool 抹掉 ICC ⇒ 判据必须"原件=有 / 抹掉后=缺"。
#   两端都要成立：只"抹掉后判缺"可能只是取数一直失败；只"原件判有"则没有牙。
#   ⚠ 取样不能限死容器：quick 层不跑 tiff，上一版把前置写死成 tiff ⇒ 对照永远取不到，把**我自己的**
#     范围错报成一条红。现在退到"任意带 ICC 的产物"，优先 tiff/png（ICC 通路最稳）。
#   ⚠ 排序键只能有一份：这里要的是"tiff(0) → png(1) → 其他(2)"**升序**取第一个当对照，
#     写成 `@{E={…}; E='Descending'}` 既是重复键（ParserError：哈希文本中不允许重复的键"E"，
#     实测整驱动在解析期就死 ⇒ full 层此前从未真的跑起来），也会被误当成降序而取到"其他"。
RunGate 'NC3' {
  # `verdict=ok 且 hasIcc` 逻辑上就蕴含 `out` 非空；这里仍显式要 `out`，是因为 ① 的根因就出在
  #   "拿着 null 路径去喂 Mandatory 参数"。同时数一下**被排除的矛盾行**，有就说出来，不静默放行。
  $ghost = @($rows | Where-Object { $_.verdict -eq 'ok' -and $_.hasIcc -and -not $_.out })
  if ($ghost.Count -gt 0) { Say "  NC3 注意：$($ghost.Count) 格登记了 ok+带 ICC 却没有产物路径 ⇒ 逐格账自相矛盾，见 CSV" }
  $tif = @($rows | Where-Object { $_.verdict -eq 'ok' -and $_.hasIcc -and $_.out } | Sort-Object @{E = { if ($_.fmt -eq 'tiff') { 0 } elseif ($_.fmt -eq 'png') { 1 } else { 2 } } }) | Select-Object -First 1
  if ($tif) {
    $strip = Join-Path $d ('nc3_stripped.' + $tif.fmt); Copy-Item -LiteralPath $tif.out -Destination $strip -Force
    & (Get-ToolPath 'exiftool') -overwrite_original -ICC_Profile= $strip | Out-Null
    $cmKeep = Get-ColorMetadata $tif.out
    $cmStrip = Get-ColorMetadata $strip
    # 取数失败（Ok=false）不能算"没有 ICC"⇒ 两端都必须 Ok 才谈得上"有牙"，否则这条就是假绿。
    $st3 = $cmKeep.Ok -and $cmStrip.Ok -and ([bool]$cmKeep.HasIcc) -and (-not [bool]$cmStrip.HasIcc)
    CK $st3 "NC3 标签尺子有牙（样本 $($tif.fmt)）：原件 HasIcc=$([bool]$cmKeep.HasIcc)/读=$($cmKeep.Ok) 抹掉后 HasIcc=$([bool]$cmStrip.HasIcc)/读=$($cmStrip.Ok)，要 真/假"
  } else {
    # 取不到对照样本**不能只是打一行 SKIP 就算过关**：那等于"这条负控这轮不存在"却仍是绿的。
    #   取样已经不限容器（png 带 ICC 就够，quick 层也有）⇒ 走到这里说明整张网格没有一个带 ICC 的产物，
    #   LBL 的牙无从证明 ⇒ 判红。
    CK $false 'NC3 没有任何带 ICC 的产物可当对照 ⇒ LBL 的负控这轮未做成（不许当"跑过了"）'
  }
}
RunGate 'NC2' {
  $f = ExecCli $exe "--headless -i `"$base8`" -o `"$(Join-Path $d 'nc_bad.png')`" -f png --bit-depth 47 --color-space sRGB"
  CK ($f.Rc -ne 0) "NC2 非法位深 47 必须被拒（exit=$($f.Rc)）"
}

# ── 7) 汇总 ────────────────────────────────────────────────────────────────────────
Say ''
# 到场账：四项负控每一项都必须**被尝试过**（`RunGate` 一进门就先登记名字）。
#   缺席只能意味着代码根本没走到那一行 —— 正是 2026-09-26 full 层那次的形状（NC1b 之后被异常打断，
#   NC2/NC3/本节从未执行，而日志尾巴看着像"跑到负控就完了"）。到场面 ≠ 通过：通过由各自的 CK 判。
# ── 6b) 「干净拒绝」的稳定码表 + 它自己的牙（合成行，零转换成本）───────────────────
# 为什么这一段必须存在：§6c 的 (A) 负路径原先写成「无产物 ⇒ rc≠0 **或** 日志含 Reject/拒绝」，
#   而**任何崩溃都 rc≠0** ⇒ 「按契约拒绝」与「产品炸了」在这一族格里根本分不开（任务 #50 缺口①）。
#   现在只认**产品源码里逐字存在的稳定载体**，并且每条都做一次「载体还在不在源码里」的核对
#   —— 按符号/日志文案常量核对，不引用会漂移的行号；凭猜测硬编码的字符串一律不收。
# 五种合法形态（⚠ 每条都锚在源码的**符号名**或**日志文案常量**上）：
#   F1 = 枚举成员名 VerdictKind.Reject（定义在 ColorVerdict.cs 的「public enum VerdictKind」），
#        投影成日志行「[色彩裁决] 裁决=<Kind>」：引擎侧与传统管线消费点**逐字同格式**
#        （QueueProcessor 里那条 S6 注释就写着"两条管线的裁决因此可以按同一口径直接对拍"）。
#        ⚠ 这一条里的 "Reject" 是**从源码 enum 成员名解析出来**的、不是我抄的字符串：解析不到
#          ⇒ F1/F4 整条停用并由下面的载体账判红（判据自身失效时**不许**退化成"什么都算拒绝"）。
#   F2 = 引擎消费点「plan.IsOk=false」⇒「[色彩引擎] ❌ 契约拒绝（不出产物）」，项级 exit 91。
#   F3 = 传统管线消费点「legacyVerdict.Kind=Reject」⇒「[色彩裁决] ❌ 契约拒绝（不出产物）：{Reason}」，
#        同为 exit 91；分派级那条「非 JPEG 目标 + GainMap」的拒绝打的也是这一行、且**没有** 裁决= 行
#        ⇒ 只靠 F1 会把它漏成"静默失败"。
#   F4 = jxl 直编旁路的观测行「[色彩裁决] 通路=cjxl-legacy … reject=<Kind>」：该路线**到不了**主消费点
#        （源码注释点名"本旁路原先零读数"），裁决只能从这一行读。⚠「reject=null」= 算不出裁决，
#        **不是**拒绝（自测里专门有一条盯这个形状）。
#   F5 = 显式「--color-engine」时 EngineBlockCodes.* 的路由拦截行（exit 90）。
#        ⚠ 语义是"本执行体不接 ⇒ 换执行体"，与 Reject「任务做不了」**不同型**（EngineBlock.cs 的 ⚠），
#          所以单列一条、读数里分开统计，不让"回退"被算进"契约拒绝"。
# ⚠ **不在表里的一律不算干净拒绝**：「[色彩引擎] ❌ 未产出文件」与「❌ 执行失败：…」（= exit 92 的两条形状）、
#   「❌ 选项→色彩意图装配失败」（内部失败）、任何异常栈、rc=0 的静默 ⇒ 全部判红。
#   并且保留 fail-closed：**日志为空/取数失败**也不判绿（headless 只有 --log-level Debug 才回显项级日志）。
Say '== 6b) 拒绝判据的稳定码表 + 自测（合成行，不产生任何真实转换）=='
function Read-RepoText([string]$Rel) {
  if ([string]::IsNullOrEmpty($Rel)) { return @{ Ok = $false; Text = ''; Error = '载体核对未给源码路径（判据自身缺参数）' } }
  $abs = Join-Path $root $Rel
  if (-not (Test-Path -LiteralPath $abs -PathType Leaf)) { return @{ Ok = $false; Text = ''; Error = ('源码不在位 ' + $Rel) } }
  try { return @{ Ok = $true; Text = ([IO.File]::ReadAllText($abs)); Error = '' } }
  catch { return @{ Ok = $false; Text = ''; Error = ('读源码失败 ' + $Rel + '：' + $_.Exception.Message) } }
}
$srcVerdictKind = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs'
$srcVerdictLog  = 'src/FfmpegGui/Services/QueueProcessor.cs'
$srcBlockCodes  = 'src/FfmpegGui/Services/ColorMapping/EngineBlock.cs'
$txByFile = @{}
$txByFile[$srcVerdictKind] = Read-RepoText $srcVerdictKind
$txByFile[$srcVerdictLog]  = Read-RepoText $srcVerdictLog
$txByFile[$srcBlockCodes]  = Read-RepoText $srcBlockCodes
# 从源码解析 VerdictKind 的**成员名**（判据的词表来自符号，不来自记忆）
$kindMembers = @()
$kindParseErr = ''
$txK = $txByFile[$srcVerdictKind]
if (-not $txK.Ok) { $kindParseErr = $txK.Error }
else {
  $mk = [regex]::Match($txK.Text, 'enum\s+VerdictKind\s*\{([^}]*)\}')
  if (-not $mk.Success) { $kindParseErr = '源码里找不到「enum VerdictKind { … }」的声明（枚举改名/换行写法 ⇒ 判据停用）' }
  else {
    foreach ($p in ($mk.Groups[1].Value -split ',')) {
      $t = $p.Trim()
      if ($t -match '^[A-Za-z_][A-Za-z0-9_]*$') { $kindMembers += $t }
    }
  }
}
$rxKindReject = ''
if ($kindMembers -contains 'Reject') { $rxKindReject = 'Reject' }
$script:RejectForms = @(
  [pscustomobject]@{ Id = 'F1-裁决=Reject'; Enabled = ($rxKindReject -ne '')
    Rx = $(if ($rxKindReject -ne '') { '\[色彩裁决\]\s*裁决=' + $rxKindReject + '\b' } else { '' })
    Sym = 'VerdictKind.Reject（ColorVerdict.cs 的 enum 成员名）经两条管线的「[色彩裁决] 裁决=<Kind>」行投影'
    Audits = @(@{ File = $srcVerdictKind; Lit = 'public enum VerdictKind' }, @{ File = $srcVerdictLog; Lit = '[色彩裁决] 裁决=' }) },
  [pscustomobject]@{ Id = 'F2-引擎侧契约拒绝'; Enabled = $true
    Rx = '\[色彩引擎\][^\r\n]*契约拒绝（不出产物）'
    Sym = 'QueueProcessor 引擎消费点 plan.IsOk=false ⇒ exit 91'
    Audits = @(@{ File = $srcVerdictLog; Lit = '[色彩引擎] ❌ 契约拒绝（不出产物）' }) },
  [pscustomobject]@{ Id = 'F3-传统侧契约拒绝'; Enabled = $true
    Rx = '\[色彩裁决\][^\r\n]*契约拒绝（不出产物）'
    Sym = 'QueueProcessor 传统管线消费点 legacyVerdict.Kind=VerdictKind.Reject ⇒ exit 91（含分派级的非 JPEG GainMap 拒绝，同文案）'
    Audits = @(@{ File = $srcVerdictLog; Lit = '契约拒绝（不出产物）：' }) },
  [pscustomobject]@{ Id = 'F4-cjxl-legacy旁路拒绝'; Enabled = ($rxKindReject -ne '')
    Rx = '\[色彩裁决\]\s*通路=cjxl-legacy[^\r\n]*reject=' + $rxKindReject + '\b'
    Sym = 'QueueProcessor 的 jxl 直编旁路观测行（ResolveLegacyVerdictPlan(...) 的 Verdict.Kind ⇒ 该路线唯一读得到裁决的地方）'
    Audits = @(@{ File = $srcVerdictLog; Lit = '通路=cjxl-legacy' }, @{ File = $srcVerdictLog; Lit = ' reject=' }) },
  [pscustomobject]@{ Id = 'F5-显式引擎路由拦截'; Enabled = $true
    Rx = '\[色彩引擎\][^\r\n]*按要求应走引擎，但本次不可走（[A-Za-z]+）'
    Sym = 'EngineBlock.Code（EngineBlockCodes.* 词表）⇒ exit 90；语义是「换执行体」，与 Reject 不同型，单列不并入'
    Audits = @(@{ File = $srcVerdictLog; Lit = '按要求应走引擎，但本次不可走（' }, @{ File = $srcBlockCodes; Lit = 'public static class EngineBlockCodes' }) }
)
# 载体账：每条形态的**日志文案**都回源码逐字核对。文案在产品侧被改掉 ⇒ 本表的 regex 就永远咬不到 ⇒
#   负路径会静默退化成"全部判红"或（更糟）"从没被执行过"。这一条把那种漂移当场钉成红。
$carrierBad = @()
if ($kindParseErr -ne '') { $carrierBad += ('VerdictKind 解析：' + $kindParseErr) }
foreach ($f in @($script:RejectForms)) {
  if (-not $f.Enabled) { $carrierBad += ($f.Id + '：源码 enum VerdictKind 的成员里解析不到 Reject ⇒ 该形态整条停用') }
  foreach ($a in $f.Audits) {
    $tx = $null
    if ($txByFile.ContainsKey($a.File)) { $tx = $txByFile[$a.File] }
    if ($null -eq $tx) { $carrierBad += ($f.Id + '：核对未预载源码 ' + $a.File) ; continue }
    if (-not $tx.Ok) { $carrierBad += ($f.Id + '：' + $tx.Error) }
    elseif ($tx.Text.IndexOf($a.Lit, [System.StringComparison]::Ordinal) -lt 0) {
      $carrierBad += ($f.Id + '：源码里找不到文案『' + $a.Lit + '』（载体漂移 ⇒ 这一行再也不会出现在日志里）')
    }
  }
}
foreach ($x in ($carrierBad | Select-Object -First 8)) { Say "  载体? $x" }
$script:rejectTrusted = ($carrierBad.Count -eq 0)
CK ($script:rejectTrusted) ("拒绝判据载体账：F1..F5 的文案在**产品源码**里逐字在位（漂移/读不到 = $($carrierBad.Count)；" +
  '判据自身失效时不许把它掩盖成"没有无产物格"，也不许放过任何一次崩溃')

function Get-StableRejection {
  param([string]$Log, [int]$Rc)
  # 「无产物」要算**干净拒绝**，两条同时成立缺一不可（比旧判据「rc≠0 或 日志含 Reject/拒绝」严格更紧）：
  #   ① 日志里读到 F1..F5 之一（稳定载体）；② rc≠0（产品侧契约拒绝 = 91、显式引擎路拦截 = 90；
  #      rc=0 却零产物按 §2a 的 REFUSE-UNCLEAN 同口径判违规）。
  # 返回 @{ Ok; Form; Detail }；**取数失败（日志空）一律 Ok=false**，不猜。
  if (-not $script:rejectTrusted) {
    return @{ Ok = $false; Form = ''; Detail = '拒绝判据的载体核对未过（见 6b 载体账）⇒ 本条负路径不作数，按红处理' }
  }
  if ([string]::IsNullOrWhiteSpace($Log)) {
    return @{ Ok = $false; Form = ''; Detail = '日志为空/未回显 ⇒ 无从证实拒绝（取数失败不判绿；headless 需 --log-level Debug 才回显项级日志）' }
  }
  $hit = ''
  foreach ($f in @($script:RejectForms)) {
    if (-not $f.Enabled) { continue }
    if ($f.Rx -eq '') { continue }
    if ($Log -match $f.Rx) { $hit = $f.Id; break }
  }
  if ($hit -eq '') {
    return @{ Ok = $false; Form = ''; Detail = ("rc=$Rc 且日志里没有任何稳定拒绝载体 ⇒ 崩溃/内部失败不得算干净拒绝") }
  }
  if ($Rc -eq 0) {
    return @{ Ok = $false; Form = $hit; Detail = "读到 $hit 却 rc=0 ⇒ 既不交付也不报错（REFUSE-UNCLEAN 同口径）" }
  }
  return @{ Ok = $true; Form = $hit; Detail = ("干净拒绝：$hit（rc=$Rc）") }
}
# 自测（§2a 的同款做法）：不打产品、不建图，喂的就是判据要读的那些行本身 ⇒ 跑一次几毫秒。
# 前 7 条是**牙**（必须红），后 5 条是**反真空正控**（每条形态单独成立 ⇒ 证明判据不是一条巨型 regex）。
$synRejectCases = @(
  @{ N='牙1 崩溃：rc=1 + 异常栈、零稳定码'; Rc=1; Want=$false; Form=''
     L='Unhandled exception. System.NullReferenceException: Object reference not set to an instance of an object.' },
  @{ N='牙2 引擎侧「未产出文件」（exit 92 的形状：有 ❌ 但不是拒绝）'; Rc=92; Want=$false; Form=''
     L='[色彩引擎] ❌ 未产出文件' },
  @{ N='牙3 只说人话的「被拒绝了」（旧判据 Reject|拒绝|不出产物 会被这条骗成干净拒绝）'; Rc=1; Want=$false; Form=''
     L='[信息] 这个目标在当前容器上被拒绝了，因此没有产出文件' },
  @{ N='牙4 裁决=ProceedWithDegradation 却没有产物（本该交付 ⇒ 不是拒绝）'; Rc=91; Want=$false; Form=''
     L='[色彩裁决] 裁决=ProceedWithDegradation' },
  @{ N='牙5 有 F1 拒绝行却 rc=0（既不交付也不报错）'; Rc=0; Want=$false; Form='F1-裁决=Reject'
     L='[色彩裁决] 裁决=Reject' },
  @{ N='牙6 日志空白（取数失败 ⇒ 不许当过）'; Rc=1; Want=$false; Form=''
     L='' },
  @{ N='牙7 jxl 旁路 reject=null（算不出裁决 ≠ 拒绝）'; Rc=1; Want=$false; Form=''
     L='[色彩裁决] 通路=cjxl-legacy 出口=bt2020/bt709 链数=0 reject=null' },
  @{ N='F1 正控（两条管线同一行格式）'; Rc=91; Want=$true; Form='F1-裁决=Reject'
     L='[色彩裁决] 裁决=Reject' },
  @{ N='F2 正控（引擎消费点）'; Rc=91; Want=$true; Form='F2-引擎侧契约拒绝'
     L='[色彩引擎] ❌ 契约拒绝（不出产物）' },
  @{ N='F3 正控（传统/分派级，无 裁决= 行）'; Rc=91; Want=$true; Form='F3-传统侧契约拒绝'
     L='[色彩裁决] ❌ 契约拒绝（不出产物）：增益图（GainMap）不适用于 png：它依赖 JPEG 的 APP2/XMP+MPF 结构' },
  @{ N='F4 正控（jxl 直编旁路的唯一读数）'; Rc=91; Want=$true; Form='F4-cjxl-legacy旁路拒绝'
     L='[色彩裁决] 通路=cjxl-legacy 出口=bt2020/bt709 链数=0 reject=Reject' },
  @{ N='F5 正控（显式引擎路被路由拦截，exit 90）'; Rc=90; Want=$true; Form='F5-显式引擎路由拦截'
     L='[色彩引擎] ❌ 按要求应走引擎，但本次不可走（FormatNotSupported）：出口容器不在引擎编码白名单内' }
)
$synRejectBad = @()
foreach ($c in $synRejectCases) {
  $r = Get-StableRejection -Log $c.L -Rc $c.Rc
  $shapeOk = ($r.Ok -eq $c.Want)
  if ($shapeOk -and ($c.Form -ne '')) { $shapeOk = ($r.Form -eq $c.Form) }
  if (-not $shapeOk) {
    $synRejectBad += ('{0} ⇒ 判据实得 Ok={1}/Form={2}，期望 Ok={3}/Form={4}（依据：{5}）' -f $c.N, $r.Ok,
      $(if ($r.Form) { $r.Form } else { '—' }), $c.Want, $(if ($c.Form) { $c.Form } else { '—' }), $r.Detail)
  }
}
foreach ($x in $synRejectBad) { Say "  拒绝自测? $x" }
CK ($synRejectBad.Count -eq 0) ("拒绝判据自测：$($synRejectCases.Count) 条合成行（7 条必须红 = 崩溃 / exit92 未产出 / 只说人话的拒绝 / " +
  "非 Reject 裁决 / rc=0 / 空日志 / reject=null，5 条必须绿 = F1..F5 逐条）形态分判全对 = $($synRejectCases.Count - $synRejectBad.Count)")

# ── 6c) LEGACY 通路层（任务 #49，2026-09-27）───────────────────────────────────────
# 为什么必须补这一段：§2 那 360 格**每格都写死 `--color-engine engine`**，而 `$fmts` 里连 jxr 都没有
#   ⇒ #43/#44 那一族「legacy 旁路吃掉用户目标」的缺陷，全层网格**一格都看不见**（360 格全绿、缺陷在场）。
#   补的首先是**通路**覆盖（两条执行路必须被同一把尺子量），格式轴只是给那把尺子多两处能落地的地方：
#   容器面已由 {jxl,jxr}=24 格扩到 {jxl,jxr,png,tiff}=48 格（任务 #50 缺口②，扩的理由见下面 6c 中段）。
# ⚠ 像素判据**只用 SDR 源**、HDR 源不跨路比像素：HDR→SDR 两路的**色调映射契约本就不同**
#   （legacy=ffmpeg `tonemap=hable` 收在满量程、不发 intensity_target；engine=进程内内核按 203nit 白
#    + `--intensity_target=1000`）⇒ 同目标稳定差 ~10 dB，而**同路跨目标只差 ~30 dB**
#   （实测与推理过程见 `docs/COLOR_MATRIX_FIX_PLAN_2026-09-24.md` 的 #44 段末）。拿跨路 PSNR 判 HDR 格
#   等于把"两路算子分歧"当色彩缺陷钉 —— 那是另一条待决的账（任务 #55：tone-map 白点/峰值契约），不是这里的。
# ⚠ 像素比对一律走**能被读方支持的容器**：jxl 先 `djxl → PNG`，**不能用 djxl 的 .ppm**
#   （那是 `P6 / maxval 65535`，ffmpeg 的 pnm 解码器读不了 ⇒ 字节错位会把任意两格都量成 ~10 dB，
#    本轮实测踩过、差点把好的报成坏的）。jxr 直接交给 ffmpeg（本构建有 `libjxr` 解码器，实测可用）。
# ── 任务 #50 缺口②：容器覆盖面由 {jxl,jxr} 扩到 {jxl,jxr,png,tiff} ──────────────────
#   为什么是 png/tiff：这两条**引擎与 legacy 两路都出产物**、且都是**无损**容器 ⇒
#     (A)「标签等于目标」与 (C)「与同路 auto 格逐字节不同」都成立而不必借跨路 PSNR 绝对阈值
#     （jxl/jxr 的 (B) 之所以只能做**方向判别**，是因为两路各自还要过一次有损编码；无损容器没有这一层扩散）。
#   ⚠ 反面教训仍然生效：**不许**给 lossy 容器（webp/avif/jpg）加跨路绝对阈值 —— 那正是 (B) 被实测否证的原因
#     （同源同目标的恒等格两路也只有 39.87 dB ⇒ 量的是有损扩散，不是色彩）。所以这里只加 png/tiff。
#   png/tiff 的标签判据**不新建词表**：原色词形走 §3 同一张 `Get-TargetPrimariesRx`
#     （ffprobe 的 CICP token 与 exiftool 的人话名两侧都咬），ICC 那一侧复用下面 `$legacyExpect` 里
#     **实测来的红原色三元组**（与 jxr 同一张表、同一个容差）—— ⚠ 描述文字仍不可当色域判据。
Say '== 6c) LEGACY 通路层：{jxl,jxr,png,tiff} × SDR 源 × 3 目标 × 两条引擎路 =='
# 三行期望值**全部来自实测**，不是凭记忆：
#   · jxl 的 `jxlinfo` 标签串 = 2026-09-27 打在 `tests/output/post44c/jxl_{legacy,engine}_{srgb,p3,bt2020}`
#     上读回（两路三格**逐字相同**：`sRGB primaries` / `P3 primaries` / `Rec.2100 primaries` + 对应曲线）；
#   · jxr 的 ICC 红原色 = 同批 `exiftool -ICC_Profile:ChromaticityChannel1` 读回
#     0.64/0.33、0.67999/0.32001、0.70799/0.29201（描述文字不可当判据：生成的标准 ICC 恒为 `RGB built-in`）；
#   · png/tiff 用同一批 `red` 值 + CICP 词形表（JXR 的容器本来就是 TIFF 族 ⇒ 同一个 exiftool ICC 读路）。
$lgFormats = @('jxl', 'jxr', 'png', 'tiff')
$jxrRedUnlabeledTol = 0.006
$legacyExpect = [ordered]@{
  'sRGB'       = @{ prim = 'sRGB primaries';      trc = 'sRGB transfer'; red = @(0.64,  0.33) }
  'Display P3' = @{ prim = 'P3 primaries';        trc = 'sRGB transfer'; red = @(0.680, 0.320) }
  'BT.2020'    = @{ prim = 'Rec.2100 primaries';  trc = '709 transfer';  red = @(0.708, 0.292) }
}
function LgProdPng([string]$prod, [string]$tag) {
  if ([IO.Path]::GetExtension($prod) -ieq '.jxl') {
    $dj = Get-ToolPath 'djxl'
    if ($null -eq $dj) { return $null }
    $png = Join-Path $d ("lgd_" + $tag + '.png')
    # Start-Process + 显式重定向：`& $dj` 会把解码器横幅从 stderr 喷进本门禁的日志（干扰逐格读数）
    Start-Process -FilePath $dj -ArgumentList @("`"$prod`"", "`"$png`"") -NoNewWindow -Wait `
        -RedirectStandardOutput ($png + '.o') -RedirectStandardError ($png + '.e') | Out-Null
    if (Test-Path -LiteralPath $png -PathType Leaf) { return $png }
    return $null
  }
  return $prod
}
# 两路像素比对**必须先把双方归到同一像素域**：legacy 与 engine 的 JXL/JXR 出口位深可以不同
# （8-bit 源 ⇒ 一路 rgb24、一路 rgb48be），不归一就会重演本轮那个 ~10 dB 假信号。
$lgGraphNorm8 = 'format=rgb24[a];[1:v]format=rgb24[b];[a][b]psnr'
function LgPsnr([string]$a, [string]$b) {
  return Invoke-PsnrCore -Ref $a -Dist $b -FilterGraph $lgGraphNorm8
}
function LgIccRed([string]$path) {
  # ⚠ 只取 stdout：随包 exiftool 是 perl 打的，环境带 LC_ALL/LANG 时 stderr 喷 warning ⇒
  #   合并式取数会把 warning 当成值喂给负断言（#51 数出的假绿面之一）。这里用 `_lib-oracle` 的分流读取。
  $r = Invoke-ExeCapture -FilePath $ex -Arguments ('-q -a -s -ICC_Profile:ChromaticityChannel1 "' + $path + '"')
  if (-not $r.Ok) { return $null }
  if ($r.StdOut -match '([0-9.]+)\s+([0-9.]+)') { return @([double]$Matches[1], [double]$Matches[2]) }
  return $null
}
$lgCells = 0; $lgCurveSkip = 0; $lgBad = New-Object System.Collections.Generic.List[string]
$lgAsym = New-Object System.Collections.Generic.List[string]
# 缺口①的读数账：无产物格有多少、分别按哪一条**稳定拒绝形态**收口（形态名来自 §6b 的表）。
$lgNoProd = 0
$script:lgRejectForms = @{}
$script:lgCellsFmt = @{}   # fmt -> 实际跑过的格数（到场账按容器逐一核，不让"png/tiff 一格没跑"混在总数里）
RunGate 'LEGACY' {
  foreach ($sk in @('p38', 'bt20208')) {
    if (-not $srcs[$sk]) { continue }
    foreach ($f in $lgFormats) {
      # 同路 auto 基准格（不给任何目标）：给"目标必须真的改变产物"那条对照用
      $autoRun = @{}
      foreach ($eng in @('legacy', 'engine')) {
        $ao = Join-Path $d ("lgauto_{0}_{1}_{2}" -f $sk, $f, $eng)
        # ⚠ 必须 `--log-level Debug`：降级播报（`UnlabeledAssumedSrgb` 等）**不在 Info 级别**，
        #   而 (A) 的"无标注必须点名"判据要读的就是它（同 §2 主网格那句注释）。
        $null = ExecCli $exe ("--headless --log-level Debug -i `"$($srcs[$sk])`" -o `"$ao`" -f $f --bit-depth 8 --color-engine $eng") -Tee "$ao.log"
        $ap = Resolve-Prod $ao
        $autoRun[$eng] = $ap
      }
      foreach ($tn in $legacyExpect.Keys) {
        $exp = $legacyExpect[$tn]
        $made = @{}   # eng -> @{Prod;Rc;Log}
        foreach ($eng in @('legacy', 'engine')) {
          $co = Join-Path $d ("lgcell_{0}_{1}_{2}_{3}" -f $sk, ($tn -replace '[^A-Za-z0-9]', ''), $f, $eng)
          $r = ExecCli $exe ("--headless --log-level Debug -i `"$($srcs[$sk])`" -o `"$co`" -f $f --bit-depth 8 --color-space `"$tn`" --color-engine $eng") -Tee "$co.log"
          $made[$eng] = @{ Prod = (Resolve-Prod $co); Rc = $r.Rc; Log = $r.Out }
          $script:lgCells++
          $script:lgCellsFmt[$f] = 1 + [int]$script:lgCellsFmt[$f]
        }
        # ── (A) 标签/结局：出产物就必须把目标**标注**出来；标注不出来就必须干净拒绝 ──
        #    这一条对两路**同形** ⇒ 谁退化都抓得到：engine 若哪天"出了产物却既无 ICC、目标又不是 sRGB"，
        #    它就是给用户一张会被按 sRGB 解读的广色域图（零标注洗白），必红。
        foreach ($eng in @('legacy', 'engine')) {
          $m = $made[$eng]; $tag = "${sk}_${f}_${tn}_$eng"
          if (-not $m.Prod) {
            # ── (A) 负路径（任务 #50 缺口①）：无产物**必须**在日志里读到 §6b 的稳定拒绝形态之一 ──
            #   旧写法「rc≠0 或 日志含 Reject/拒绝」没有牙：任何崩溃都 rc≠0，于是"炸了"也能交差。
            #   新判据 = **稳定拒绝码 ∧ rc≠0**（两条缺一不可 ⇒ 相对旧写法严格更紧，不是放宽）：
            #     · 崩溃/异常栈/exit 92 的「未产出文件」/「装配失败」/只说人话的"被拒绝" ⇒ 红；
            #     · 日志空白（取数失败）⇒ 红；有拒绝码却 rc=0 ⇒ 红（§2a 的 REFUSE-UNCLEAN 同口径）。
            $script:lgNoProd++
            $rj = Get-StableRejection -Log $m.Log -Rc $m.Rc
            if ($rj.Ok) {
              $script:lgRejectForms[$rj.Form] = 1 + [int]$script:lgRejectForms[$rj.Form]
            } else {
              $lgBad.Add("$tag（$eng）：无产物 ⇒ $($rj.Detail)")
            }
            continue
          }
          if ($f -eq 'jxl') {
            $cm = Get-ColorMetadata -Path $m.Prod
            if (-not $cm.Ok) { $lgBad.Add("$tag（$eng）：jxl 标签回读失败 ⇒ 不判绿（$($cm.Error)）"); continue }
            $okL = ("$($cm.Primaries)" -match [regex]::Escape($exp.prim)) -and ("$($cm.Transfer)" -match [regex]::Escape($exp.trc))
            if (-not $okL) { $lgBad.Add("$tag（$eng）：标签 prim='$($cm.Primaries)' trc='$($cm.Transfer)' ≠ 目标 $tn（'$($exp.prim)'/'$($exp.trc)'）") }
          }
          elseif ($f -eq 'jxr') {
            $red = LgIccRed $m.Prod
            $assumedSrgb = ($m.Log -match 'UnlabeledAssumedSrgb') -and ($tn -eq 'sRGB')
            if ($null -eq $red) {
              if (-not $assumedSrgb) { $lgBad.Add("$tag（$eng）：jxr 无 ICC 标注且日志未点名『隐含 sRGB』⇒ 目标 $tn 未被交付也没报错") }
            }
            else {
              $dMax = [Math]::Max([Math]::Abs($red[0] - $exp.red[0]), [Math]::Abs($red[1] - $exp.red[1]))
              if ($dMax -gt $jxrRedUnlabeledTol) {
                $lgBad.Add("$tag（$eng）：jxr 内嵌 ICC 红原色 $($red[0]),$($red[1]) ≠ 目标 $tn 的 $($exp.red -join ',')（差 $([math]::Round($dMax,4))）")
              }
            }
          }
          else {
            # ── png / tiff（缺口②新增，两路都跑）：读到的**标注必须等于目标** ──
            #   正证据三选一（都锚在既有裁判上，不新造词表）：
            #     ① exiftool 的 ColorPrimaries 人话名命中 `Get-TargetPrimariesRx`；
            #     ② ffprobe 的 color_primaries（CICP token，PNG 的 cICP 走这一路）；
            #     ③ 内嵌 ICC 的**红原色测量值**落在目标的实测三元组内（同 jxr 的容差）。
            #   ⚠ 判据只看**原色**、不把 TRC 当必要条件：BT.709 曲线的口径黑名单（zscale 的 t=bt709 是纯 γ(1/2.4)、
            #     引擎按 BT.709-6 定义式）是**像素**侧的问题，标签侧再拿它当必要条件只会多造假红、不少抓一个缺陷。
            #   ⚠ 三条证据全无 ⇒ 只允许「日志点名隐含 sRGB 且目标就是 sRGB」这一种收口（与 jxr 那条同形），
            #     其余一律红；取数失败（`Get-ColorMetadata.Ok=false`、词形表缺档）同样**不判绿**。
            $rxTgt = Get-TargetPrimariesRx $tn
            if ($rxTgt -eq '') { $lgBad.Add("$tag（$eng）：目标 $tn 在词形表里没有条目 ⇒ 本格标签无法判（判据缺词，不判绿）"); continue }
            $cm = Get-ColorMetadata -Path $m.Prod
            if (-not $cm.Ok) { $lgBad.Add("$tag（$eng）：$f 标签回读失败 ⇒ 不判绿（$($cm.Error)）"); continue }
            $st = Get-StreamInfo -Path $m.Prod
            $hit = ''
            if ("$($cm.Primaries)" -match $rxTgt) { $hit = "exiftool ColorPrimaries='$($cm.Primaries)'" }
            if (($hit -eq '') -and $st.Ok -and ("$($st.ColorPrimaries)" -match $rxTgt)) { $hit = "ffprobe color_primaries='$($st.ColorPrimaries)'" }
            $iccBad = ''
            if ($hit -eq '') {
              $red = LgIccRed $m.Prod
              if ($null -ne $red) {
                $dMax = [Math]::Max([Math]::Abs($red[0] - $exp.red[0]), [Math]::Abs($red[1] - $exp.red[1]))
                if ($dMax -le $jxrRedUnlabeledTol) { $hit = "内嵌 ICC 红原色 $($red[0]),$($red[1])" }
                else { $iccBad = "内嵌 ICC 红原色 $($red[0]),$($red[1]) ≠ 目标 $tn 的 $($exp.red -join ',')（差 $([math]::Round($dMax,4))）" }
              }
            }
            if ($hit -eq '') {
              if ($iccBad -ne '') { $lgBad.Add("$tag（$eng）：$f $iccBad ⇒ 目标 $tn 没被标注出来") }
              else {
                $assumedSrgb = ($m.Log -match 'UnlabeledAssumedSrgb') -and ($tn -eq 'sRGB')
                if (-not $assumedSrgb) {
                  $lgBad.Add("$tag（$eng）：$f 既无等于目标 $tn 的 CICP（prim='$($cm.Primaries)' ffprobe='$($st.ColorPrimaries)'）、又无可核对的 ICC 红原色，日志也没点名『隐含 sRGB』⇒ 目标没交付也没报错")
                }
              }
            }
          }
        }
        # ── (B) 像素**落在目标域**，而不是"两路彼此一样"就行 ──
        # 为什么不拿"两路互比 PSNR ≥40"当判据（本段上一版的写法）：两路是**两次独立的有损编码**
        #   （jxl 走 cjxl `-d 3.8`、jxr 走 JxrEncApp `-q`），实测连**同源同目标**（bt20208 → BT.2020，
        #   变换本应是恒等）都只有 39.87 dB ⇒ 40 这个门槛量的是有损扩散，不是色彩。拿它判红会把
        #   编码噪声当缺陷（同 §4 NUM 只判无损容器的理由）。
        # 换成一对外部参照做**方向判别**：产物必须离"按 zimg 转到目标空间"的参照更近，
        #   而不是离"源按原样存值"更近 ⇒ "目标被吃掉"那种形态会当场翻向（对照件自带，不靠阈值猜）。
        # ⚠ png/tiff（缺口②新增）走**同一对参照、同一组阈值**：它们是无损容器 ⇒ 两路都不再有编码扩散，
        #   这段对它们只可能更稳（不是新增一把尺子，是同一把尺子多量两格）；跨路绝对阈值仍**不加**、
        #   lossy 容器（webp/avif/jpg）也**不进**这一层 —— 理由见上面那段实测否证。
        $srcTok = switch -Regex ($sk) { '^srgb' { 'bt709' } '^p3' { 'smpte432' } '^bt2020' { 'bt2020' } default { 'bt709' } }
        $srcTrc = switch -Regex ($sk) { '^bt2020' { 'bt709' } default { 'iec61966-2-1' } }
        $dtTrc  = if ($tn -eq 'BT.2020') { 'bt709' } else { 'iec61966-2-1' }
        $refT = Join-Path $d ("lgref_{0}_{1}.png" -f $sk, ($tn -replace '[^A-Za-z0-9]', ''))
        if (-not (Test-Path -LiteralPath $refT -PathType Leaf)) {
          ExecCli $ff ("-hide_banner -loglevel error -y -i `"$($srcs[$sk])`" -vf `"zscale=pin=${srcTok}:tin=${srcTrc}:min=gbr:p=$($targets[$tn]):t=${dtTrc}:m=gbr,format=rgb48le`" -frames:v 1 `"$refT`"") | Out-Null
        }
        # ⚠ 这条参照**只在"两侧曲线都不是 bt709"时对两路同时适用**：`zscale t=bt709` 实为纯 γ(1/2.4)，
        #   而引擎按 BT.709-6 定义式（分段、α=1.099/β=0.055）编码 —— 这是本仓自己的**口径黑名单**
        #   （`verify-color-caps.ps1` (f) 段实测：两者 8-bit 最大差 27.2 LSB ⇒ 相对量 ~19.5 dB，与位深无关）。
        #   实测签名（bt20208 → sRGB jxl，18:45 那轮）：engine 离本参照 14.68 dB / 离源原样 18.72 dB，
        #   而 legacy（走的正是 zimg 那条链）离本参照 28.32 dB / 离源原样 11.69 dB
        #   ⇒ 判 engine "没转到目标"是**拿错的尺子量对的像素**。所以：曲线含 bt709 时本段只判 legacy，
        #   engine 那一侧仍由 (A) 标签 + (C) 与 auto 不同字节 两把尺子看住（不是放宽，是换用不适用的尺子=不用）。
        $curveBlocked = ($srcTrc -eq 'bt709') -or ($dtTrc -eq 'bt709')
        $sameGamut = ($sk -eq 'p38' -and $tn -eq 'Display P3') -or ($sk -eq 'bt20208' -and $tn -eq 'BT.2020')
        foreach ($eng in @('legacy', 'engine')) {
          $m = $made[$eng]
          if (-not $m.Prod) { continue }
          if ($curveBlocked -and $eng -eq 'engine') { $script:lgCurveSkip++; continue }
          $dp = LgProdPng $m.Prod ("{0}_{1}_{2}_{3}" -f $sk, $f, ($tn -replace '[^A-Za-z0-9]', ''), $eng)
          if (-not $dp) { $lgBad.Add("$($sk)_$($tn)_$f（$eng）：产物解码件取不到 ⇒ 像素账未跑（不许当过）"); continue }
          if (-not (Test-Path -LiteralPath $refT -PathType Leaf)) { $lgBad.Add("$($sk)_$($tn)_$f（$eng）：目标参照未生成 ⇒ 该格无法判"); continue }
          $pT = LgPsnr $refT $dp           # 与"转到目标空间"的参照比
          $pA = LgPsnr $srcs[$sk] $dp      # 与"源原样存值"比（对照件：目标被吃掉时这一头会更高）
          if (-not $pT.Ok -or -not $pA.Ok) { $lgBad.Add("$($sk)_$($tn)_$f（$eng）：PSNR 取数失败 ⇒ 不判绿"); continue }
          $dT = [double]$pT.Psnr; $dA = [double]$pA.Psnr
          if ($dT -lt 26.0) { $lgBad.Add("$($sk)_$($tn)_$f（$eng）：离目标参照只有 $([math]::Round($dT,2)) dB（<26 ⇒ 像素没按目标交付）") }
          if (-not $sameGamut -and $dA -gt ($dT - 4.0)) {
            $lgBad.Add("$($sk)_$($tn)_$f（$eng）：离源原样 $([math]::Round($dA,2)) dB 不比离目标 $([math]::Round($dT,2)) dB 明显更远 ⇒ 像素没真的转到目标（#44 那一族的形状）")
          }
        }
        # ── (B2) 两路结局不同：**记成读数**（不判绿也不判红）──
        #   它是任务 #53 那笔待决的账（jxr 能力表 CanCarryArbitraryIcc=false vs legacy 路实测能嵌 ICC）。
        #   只打印不判决的理由：判绿会掩盖差异、判红等于替维护者拍板"档①要不要做"。
        $pl = $made['legacy'].Prod; $pe = $made['engine'].Prod
        if ($pl -xor $pe) {
          $who = $(if ($pl) { 'legacy' } else { 'engine' }); $other = $(if ($pl) { 'engine' } else { 'legacy' })
          $lgAsym.Add("$sk →$tn $f ：只有 $who 出了产物（$other 无产物）")
        }
        # ── (C) 牙：目标与源**色域不同**时，产物必须与**同路**的 auto 格不同字节 ──
        #   ⚠ 既有范围保持不变（jxl/jxr 只打 legacy 路）：实测 engine+jxr 的 auto 与 sRGB 两格字节相同是
        #     **合法的**（两格计划同为「Map→sRGB + UnlabeledAssumedSrgb」且日志点名），
        #     拿 engine 当这两条容器的对照会假红。取证见任务 #53。
        #   ⚠ png/tiff（缺口②新增）**两路都打**：无损容器 ⇒ auto 与显式目标之间不存在"编码噪声刚好抹平"
        #     这种巧合，同字节就等于目标真的没改变产物。并且**对照件缺席也算红**：
        #     auto 格没出产物时这条对照根本没跑成，不许把它当成"没有违规"（反真空）。
        $cEngs = @('legacy')
        if (($f -eq 'png') -or ($f -eq 'tiff')) { $cEngs = @('legacy', 'engine') }
        if (-not $sameGamut) {
          foreach ($eng in $cEngs) {
            $cp = $made[$eng].Prod
            $ap = $autoRun[$eng]
            if ($cp -and $ap) {
              $h1 = (Get-FileHash -LiteralPath $cp -Algorithm SHA256).Hash
              $h2 = (Get-FileHash -LiteralPath $ap -Algorithm SHA256).Hash
              if ($h1 -eq $h2) { $lgBad.Add("$($sk)_$($tn)_$f（$eng）：显式目标的产物与 auto 格**逐字节相同** ⇒ 目标被吃掉（#44 那一族）") }
            }
            elseif ($cp -and (-not $ap) -and (($f -eq 'png') -or ($f -eq 'tiff'))) {
              $lgBad.Add("$($sk)_$($tn)_$f（$eng）：同路 auto 对照格没出产物 ⇒ (C) 这条对照没跑成 ⇒ 不判绿")
            }
          }
        }
      }
    }
  }
}
foreach ($x in ($lgAsym | Select-Object -First 8)) { Say "  LEGACY 两路结局不同（读数，不判决）：$x" }
foreach ($x in ($lgBad | Select-Object -First 12)) { Say "  LEGACY? $x" }
# 到场账两笔：总数 + **每容器**（缺口②把容器从 2 个扩到 4 个 ⇒ 只核总数会让"png/tiff 一格没跑"藏在总数里）。
$lgFmtMiss = @()
foreach ($f in $lgFormats) {
  $n = 0
  if ($script:lgCellsFmt.ContainsKey($f)) { $n = [int]$script:lgCellsFmt[$f] }
  if ($n -lt 10) { $lgFmtMiss += "$f=$n" }
}
CK ($lgCells -ge 44) "LEGACY 层到场账：本轮实际跑了 $lgCells 格（$($lgFormats.Count) 容器 × 2 SDR 源 × 3 目标 × 2 引擎路 = 48；<44 ⇒ 这一段没跑全，别当绿。上一版是 24 格/底线 20，本轮按同一比例收紧）"
CK ($lgFmtMiss.Count -eq 0) "LEGACY 每容器到场账：每个容器至少 10 格（2 源 × 3 目标 × 2 路 = 12，容错 2）⇒ 缺席 = $(if ($lgFmtMiss) { $lgFmtMiss -join ' ' } else { '无' })"
Say ("  LEGACY 负路径读数：无产物格 $lgNoProd 个 ⇒ 判为干净拒绝的稳定码形态 = " +
     $(if ($script:lgRejectForms.Count -gt 0) { (@($script:lgRejectForms.Keys | ForEach-Object { "${_}×$($script:lgRejectForms[$_])" }) -join ' · ') } else { '无' }))
if ($lgNoProd -eq 0) {
  Say '  LEGACY 负路径到场：本轮真实网格里没有一个"无产物"格 ⇒ (A) 负路径的牙只由 §6b 的合成行自测代证（如实登记，不装作跑过）'
}
Say ("  LEGACY 像素参照因『参照 TRC=bt709 而引擎走 BT.709-6 定义式』不适用而跳过的格数 = $lgCurveSkip" +
     "（口径黑名单见「verify-color-caps.ps1 (f)」；这些格仍受 (A) 标签与 (C) 字节对照约束 ⇒ png/tiff 两路都受 (C)）")
CK ($lgBad.Count -eq 0) "LEGACY 通路账：$lgCells 格里违规 $($lgBad.Count)（无稳定拒绝码的无产物 / 标签不等于目标 / 像素未落到目标 / 目标被吃掉 / 对照件缺席）"

Say '===== 到场账（每把尺子是否真的跑了）====='
$ncNeed = @('NC1', 'NC1b', 'NC2', 'NC3', 'LEGACY')
$ncMiss = @($ncNeed | Where-Object { $script:ncRan -notcontains $_ })
CK ($ncMiss.Count -eq 0) "NC 到场账：$($ncNeed -join '/') 每项都要被尝试过（缺席 = $(if ($ncMiss) { $ncMiss -join ',' } else { '无' })）"
Say '===== 逐格摘要（按 verdict 分组计数）====='
$rows | Group-Object verdict | ForEach-Object { Say ("  {0,-18} {1}" -f $_.Name, $_.Count) }
Say '  ── 抽样 12 格 ──'
$rows | Where-Object { $_.verdict -eq 'ok' } | Select-Object -First 12 | ForEach-Object {
  Say ("    {0,-9} →{1,-11} {2,-5}{3,-3} prim='{4}' trc='{5}' icc='{6}'" -f $_.src, $_.tgt, $_.fmt, $_.bd, $_.prim, $_.trc, $_.icc)
}
# 有红时即使调用方没要 -Csv 也留一份机器可读的逐格账（否则红只存在于会滚走的终端输出里）
if (-not $Csv -and $script:fail -gt 0) { $Csv = Join-Path $root "tests/output/colormx-$Layer-auto.csv" }
if ($Csv) { RunGate 'CSV' { $rows | Export-Csv -NoTypeInformation -LiteralPath $Csv; Say "CSV = $Csv" } }
Say ("===== colormatrix: PASS={0} FAIL={1} cells={2} layer={3} =====" -f $script:pass, $script:fail, $rows.Count, $Layer)
# 清理必须是**有条件的**：红的时候工作目录就是证据本身（上一版无条件删 ⇒ 中断/判红后什么也留不下）
if ($script:fail -eq 0) {
  Remove-Item -LiteralPath $d -Recurse -Force -EA SilentlyContinue
  Say "  全绿 ⇒ 工作目录已清理 $d"
} else {
  Say "  有红 ⇒ 工作目录保留供取证：$d"
}
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
