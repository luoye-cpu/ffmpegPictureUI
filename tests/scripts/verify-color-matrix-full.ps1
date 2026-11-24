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
#   [NUM] 与 ITU/IEC 标准色度与分段 OETF 的独立 RGB 参照逐码对拍；按源/目标位深分账，不准有损冒充无损。
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

# ── 接线前奏（2026-11-01，第 88 条批）：TMP 钉扎 ────────────────────────────
# 引擎臂的解码中间件按 Path.GetTempPath() 落点；本机沙箱拦真 %TEMP% 写 ⇒ 引擎臂批量
# rc=1 无产物（2026-11-01 实测：LEGACY 48 格里 44 违规全是这个根因）。AGENTS §4 标准处置。
# 统一使用仓库规定的共享临时根；本轮仍以每轮 $d 作为实际产物/证据目录。
$scratch = Join-Path $root 'tests/output/_tmp/round-tmp'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$env:TMP = $scratch; $env:TEMP = $scratch; $env:TMPDIR = $scratch
Set-OracleConfig -RepoRoot $root -TimeoutSec 180 | Out-Null

$avail = Test-OracleAvailable
# 主网格锁定软件 cjxl；这不是硬件编码器覆盖或自动选择能力测试。
$need = @('ffmpeg', 'ffprobe', 'exiftool', 'cjxl', 'djxl', 'jxlinfo')
$missing = @($need | Where-Object { $avail.Available -notcontains $_ })
function Say($t) { Write-Output $t }
$script:pass = 0; $script:fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Say "  PASS $msg" } else { $script:fail++; Say "  FAIL $msg" } }

$ff = Get-ToolPath 'ffmpeg'
$ex = Get-ToolPath 'exiftool'
if ($ex) {
  # 与产品相同：独立 oracle 的 exiftool 子进程也必须找得到随包 Perl DLL。
  $exDir = Split-Path -Parent $ex
  $env:PATH = (Join-Path $exDir 'exiftool_files') + ";$exDir;$env:PATH"
}
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
    # exiftool 的暂存件不是交付产物；即使产品清理回归，也绝不能让裁判改读第二份文件。
    $f = Get-ChildItem -LiteralPath $p -File -EA SilentlyContinue |
      Where-Object { $_.Name -notlike '*_exiftool_tmp' } |
      Sort-Object Length -Descending | Select-Object -First 1
    if ($f) { return $f.FullName }
  }
  return $null
}

# 主格的进程、JSONL 唯一终态和产物分别核对；一条成功日志不能替代其它两列。
function Read-GridTask([string]$Log, [int]$Rc, [bool]$Made) {
  $done = @(); $messages = @(); $parseOk = $true
  foreach ($line in ($Log -split '\r?\n')) {
    if ($line -cmatch '^\s*\{') {
      try { $evt = ConvertFrom-Json -InputObject $line -ErrorAction Stop } catch { $parseOk = $false; continue }
      if ($evt.c -ceq 'item_completed') { $done += $evt }
      if ($evt.c -ceq 'item_log' -and $evt.m -is [string]) { $messages += $evt.m }
    }
  }
  $isCode = $done.Count -eq 1 -and ($done[0].code -is [int] -or $done[0].code -is [long])
  $hasShape = $done.Count -eq 1 -and $done[0].p -eq 1 -and $done[0].tot -eq 1
  $success = $parseOk -and $Rc -eq 0 -and $Made -and $done.Count -eq 1 -and $isCode -and $hasShape -and
    $done[0].code -eq 0 -and $done[0].l -ceq 'info'
  return @{ Ok = [bool]$success; Parsed = $parseOk; Count = $done.Count; Code = $(if ($done.Count -eq 1) { $done[0].code } else { $null });
    Messages = ($messages -join "`n"); Detail = "process=$Rc made=$Made parsed=$parseOk completed=$($done.Count) code=$($done.code -join ',')" }
}

# 本脚本的 SDR 无 alpha 格使用引擎 raw → PNG → cjxl 通路。只认进程启动记录；
# `[色彩] cjxl 命令行:` 是计划。核对实际参数及 PNG 交接路径，排除 legacy cjxl 直编。
function Test-GridCjxlExecution([string]$Log, [string]$Source, [string]$Product, [string]$Target, [int]$Depth) {
  $lines = $Log -split '\r?\n'
  $commands = @($lines | Where-Object { $_ -match '^\[cjxl\]\s+cjxl\.exe(?:\s|$)' })
  if ($commands.Count -ne 1) { return @{ Ok = $false; Detail = "cjxl launches=$($commands.Count)" } }
  $cmd = [regex]::Match($commands[0], '(?i)^\[cjxl\]\s+cjxl\.exe\s+"(?<input>[^"\r\n]+\.png)"\s+"(?<output>[^"\r\n]+\.jxl)"\s+-e\s+7\s+-d\s+0\s+-x\s+(?<hint>.+)$')
  if (-not $cmd.Success -or $cmd.Groups['output'].Value -ine $Product) {
    return @{ Ok = $false; Detail = 'actual cjxl must target this artifact with -e 7 -d 0 -x' }
  }
  $hint = $cmd.Groups['hint'].Value
  $hintOk = switch ($Target) {
    'sRGB'       { $hint -ceq 'color_space=sRGB' }
    'Display P3' { $hint -ceq 'color_space=DisplayP3' }
    'BT.2020'    { $hint -match '^"icc_pathname=[^"\r\n]+\.icc"$' }
    default      { $false }
  }
  $pix = switch ($Depth) { 8 { 'rgb24' } 16 { 'rgb48le' } default { '' } }
  $pngArg = [regex]::Escape('"' + $cmd.Groups['input'].Value + '"')
  $sourceArg = [regex]::Escape('"' + $Source + '"')
  $route = @($lines | Where-Object { $_ -match ('^\[色彩\] ffmpeg .* -f rawvideo .* -i ' + $sourceArg + ' -map 0:v -map_metadata 1 -vf "format=' + $pix + '" -f image2 ' + $pngArg + '$') })
  $routeOk = $pix -ne '' -and $route.Count -eq 1 -and [array]::IndexOf($lines, $route[0]) -lt [array]::IndexOf($lines, $commands[0])
  return @{ Ok = ($hintOk -and $routeOk); Detail = "actual cjxl -e 7 -d 0 hint=$hint hintOk=$hintOk enginePngHandoff=$routeOk format=$pix" }
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
function Test-AvifSampleTransform([string]$Path) {
  try {
    $b = [IO.File]::ReadAllBytes($Path); if ($b.Length -lt 16) { return $false }
    function Local-Boxes([byte[]]$x,[int]$s,[int]$e) { $r=@(); for($p=$s;$p -lt $e;) { if($e-$p -lt 8){return}; $n=0; for($j=0;$j -lt 4;$j++){$n=($n*256)+$x[$p+$j]}; if($n -lt 8 -or $n -gt ($e-$p)){return}; $r+=,@{type=[Text.Encoding]::ASCII.GetString($x,$p+4,4);start=$p+8;end=$p+$n};$p+=$n }; return $r }
    function Local-One([byte[]]$x,[int]$s,[int]$e,[string]$t) { @((Local-Boxes $x $s $e)|Where-Object type -ceq $t)|Select-Object -First 1 }
    $meta=Local-One $b 0 $b.Length 'meta'; if(-not $meta){return $false}
    $iinf=Local-One $b ($meta.start+4) $meta.end 'iinf';$iref=Local-One $b ($meta.start+4) $meta.end 'iref';
    $iprp=Local-One $b ($meta.start+4) $meta.end 'iprp'; if(-not $iinf -or -not $iref -or -not $iprp){return $false}
    $ipco=Local-One $b ($iprp.start+4) $iprp.end 'ipco';$ipma=Local-One $b ($iprp.start+4) $iprp.end 'ipma';if(-not $ipco -or -not $ipma){return $false}
    $sato=@();$av01=@()
    foreach($e in @(Local-Boxes $b ($iinf.start+6) $iinf.end|Where-Object type -ceq 'infe')) { if($e.end-$e.start -lt 12 -or $b[$e.start] -ne 2){continue};$id=($b[$e.start+4]*256)+$b[$e.start+5];$kind=[Text.Encoding]::ASCII.GetString($b,$e.start+8,4);if($kind -ceq 'sato'){$sato+=$id};if($kind -ceq 'av01'){$av01+=$id} }
    if($sato.Count -ne 1 -or $av01.Count -ne 2 -or $av01[0] -eq $av01[1]){return $false}
    $ref=Local-One $b ($iref.start+4) $iref.end 'dimg';if(-not $ref -or $ref.end-$ref.start -ne 8){return $false}
    $from=($b[$ref.start+4]*256)+$b[$ref.start+5];$to=($b[$ref.start+6]*256)+$b[$ref.start+7]
    $dimgOk=(($b[$ref.start]*256)+$b[$ref.start+1] -eq $sato[0] -and $from -ne $to -and $from -in $av01 -and $to -in $av01);if(-not $dimgOk){return $false}
    # pixi is associated with the sato item through ipma; require 3 channels at 16 bits.
    $version=$b[$ipma.start];$flags=0;for($j=0;$j -lt 3;$j++){$flags=($flags*256)+$b[$ipma.start+1+$j]};if($version -gt 1 -or $flags -gt 1){return $false}
    $idWidth=2+2*$version;$assocWidth=1+$flags;$mask=(1 -shl (8*$assocWidth-1))-1;$entries=0;for($j=0;$j -lt 4;$j++){$entries=($entries*256)+$b[$ipma.start+4+$j]};$q=$ipma.start+8;$pixi16=$false
    $props=@(Local-Boxes $b ($ipco.start+4) $ipco.end)
    for($i=0;$i -lt $entries;$i++) {
      if($ipma.end-$q -lt $idWidth+1){return $false};$id=0;for($j=0;$j -lt $idWidth;$j++){$id=($id*256)+$b[$q+$j]};$q+=$idWidth;$n=$b[$q];$q++
      if($ipma.end-$q -lt $n*$assocWidth){return $false}
      if($id -eq $sato[0]) { for($j=0;$j -lt $n;$j++) { $index=0;for($k=0;$k -lt $assocWidth;$k++){$index=($index*256)+$b[$q+$j*$assocWidth+$k]};$index=$index -band $mask;if($index -lt 1 -or $index -gt $props.Count){return $false};$prop=$props[$index-1];if($prop.type -ceq 'pixi' -and $prop.end-$prop.start -ge 8){$channels=$b[$prop.start+4];if($channels -eq 3 -and $prop.end-$prop.start -ge 5+$channels){# libavif's pixi writer/read path stores bits_per_channel as the actual depth; 16-bit is exactly 16.
$pixi16=($b[$prop.start+5] -eq 16 -and $b[$prop.start+6] -eq 16 -and $b[$prop.start+7] -eq 16)}}} }
      $q+=$n*$assocWidth
    }
    return $pixi16
  } catch { return $false }
}
function Get-GridDeliveredDepth([string]$Path, [string]$Pix) {
  $d = Get-DeliveredDepth $Pix
  if ([IO.Path]::GetExtension($Path) -ieq '.avif' -and (Test-AvifSampleTransform $Path)) { return 16 }
  return $d
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
    $convert = "format=gbrp16le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=full:p=${pp}:t=${tt}:m=gbr:r=full,format=gbrp16le"
    if ($tag -eq 'bt2020') {
      # zimg 的 bt709 变换实际是纯 gamma 2.4，但 iccgen 的 bt709 写的是分段曲线。
      # 先做线性矩阵，再显式应用 BT.709-6 分段 OETF，最后按同一曲线生成 ICC。
      $convert = $convert.Replace(':t=bt709:', ':t=linear:')
      # lutrgb 的 maxval 可能按下游格式协商成 65280；这里在定死的 gbrp16le 上必须除以 65535。
      $oetf = 'floor(65535*if(lt(val/65535\,0.0180539685108078)\,4.5*val/65535\,1.0992968268094429*pow(val/65535\,0.45)-0.0992968268094429)+0.5)'
      $convert += ",lutrgb=r='$oetf':g='$oetf':b='$oetf'"
    }
    $convert += ",iccgen=color_primaries=${pp}:color_trc=${tt}:force=1,setparams=color_trc=${tt},format=${outPix}"
    $fr = ExecCli $ff ("-hide_banner -loglevel error -y -i `"$in`" -vf `"$convert`" -frames:v 1 `"$o`"")
    if ($fr.Rc -eq 0 -and (Test-Path -LiteralPath $o -PathType Leaf)) { $srcs["${tag}$bpp"] = $o }
    else { Say ("  夹具失败 {0}/{1} rc={2} ⇒ {3}" -f $tag, $bpp, $fr.Rc, (($fr.Out -replace "`r?`n", ' | ').Substring(0, [Math]::Min(300, $fr.Out.Length)))) }
  }
}
CK ($srcs.Count -eq 6) "命名夹具计数 = $($srcs.Count)/6（$($srcs.Keys -join ' ')）"
$adobeIcc = @(
  'tools/src/libjxl/third_party/skcms/profiles/misc/AdobeRGB.icc',
  'tools/src/jpegli/third_party/skcms/profiles/misc/AdobeRGB.icc'
) | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
  Select-Object -First 1 | ForEach-Object { Get-Item -LiteralPath $_ }
CK ($null -ne $adobeIcc) '仓库内 vendored Adobe RGB ICC 就位（缺失是夹具错误，不再记 SKIP）'
if ($adobeIcc) { $srcs['adobe8'] = $base8; Say "  Adobe RGB 轴用仓库内权威 ICC：$($adobeIcc.FullName)（源=base8 + --icc-file ⇒ 同数值重解释，非真 Adobe 照片）" }
else { Say '  vendored Adobe ICC 缺失 ⇒ Adobe 轴无法执行，本门禁按红处理' }

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
#   ✅ AVIF `@(8,10,12,16)`：12-bit libaom 与 16-bit sato/dimg 均必须由实际产物回读证明；
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
# AVIF 16-bit is delivered as a verified sato/dimg sample-transform pair, not by base pix_fmt alone.
$capDepths = @{ png = @(8, 16); webp = @(8); tiff = @(8, 16); jxl = @(8, 16); avif = @(8, 10, 12, 16) }
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
# transfer 词形（2026-11-01 新增）：jxlinfo 新输出打「Transfer function: sRGB / 709」。
#   旧字面量表（$legacyExpect.trc = 'sRGB transfer' 等）与它对不上 ⇒ jxl 标签对照分家漂移的另一半。
function Get-TargetTransferRx([string]$Tgt) {
  if ($Tgt -eq 'sRGB')       { return '(?i)iec61966-2-1|sRGB' }
  if ($Tgt -eq 'Display P3') { return '(?i)iec61966-2-1|sRGB' }
  if ($Tgt -eq 'BT.2020')    { return '(?i)bt709|709' }
  return ''
}
# 独立 ICC 度量尺：读实际嵌入字节，不读取描述字符串、产品解析器或产品矩阵常量。
# 标准 xy/D65 → Bradford D50；三原色、白点与三通道 TRC 均须匹配目标。
if (-not ('MatrixGridIcc' -as [type])) {
  Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;
public static class MatrixGridIcc {
    static int U16(byte[] b,int o) => (b[o]<<8)|b[o+1];
    static int U32(byte[] b,int o) { long n=((long)b[o]<<24)|((long)b[o+1]<<16)|((long)b[o+2]<<8)|b[o+3]; if(n>int.MaxValue) throw new ArgumentException("ICC integer"); return (int)n; }
    static double Fixed(byte[] b,int o) => unchecked((int)(((uint)b[o]<<24)|((uint)b[o+1]<<16)|((uint)b[o+2]<<8)|b[o+3]))/65536.0;
    static string Sig(byte[] b,int o) => Encoding.ASCII.GetString(b,o,4);
    static double[,] Inv(double[,] m) {
        var a=new double[3,6]; for(int i=0;i<3;i++) for(int j=0;j<3;j++){a[i,j]=m[i,j];a[i,j+3]=i==j?1:0;}
        for(int k=0;k<3;k++){ int p=k;for(int i=k+1;i<3;i++)if(Math.Abs(a[i,k])>Math.Abs(a[p,k]))p=i;
            for(int j=0;j<6;j++){double v=a[p,j];a[p,j]=a[k,j];a[k,j]=v;} double d=a[k,k];if(Math.Abs(d)<1e-12)throw new ArgumentException("singular");
            for(int j=0;j<6;j++)a[k,j]/=d;for(int i=0;i<3;i++)if(i!=k){double s=a[i,k];for(int j=0;j<6;j++)a[i,j]-=s*a[k,j];}}
        var r=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)r[i,j]=a[i,j+3];return r;
    }
    static double[] Mul(double[,] m,double[] v){var r=new double[3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)r[i]+=m[i,j]*v[j];return r;}
    static double[,] Colorants(string target) {
        double[] xy=target=="sRGB"?new[]{.64,.33,.30,.60,.15,.06}:target=="Display P3"?new[]{.68,.32,.265,.69,.15,.06}:target=="BT.2020"?new[]{.708,.292,.170,.797,.131,.046}:throw new ArgumentException("target");
        var w=new[]{.3127/.329,1.0,(1-.3127-.329)/.329};var d50=new[]{.9642,1.0,.8249};var m=new double[3,3];
        for(int c=0;c<3;c++){m[0,c]=xy[2*c]/xy[2*c+1];m[1,c]=1;m[2,c]=(1-xy[2*c]-xy[2*c+1])/xy[2*c+1];}
        var s=Mul(Inv(m),w);for(int c=0;c<3;c++)for(int r=0;r<3;r++)m[r,c]*=s[c];
        var br=new double[,]{{.8951,.2664,-.1614},{-.7502,1.7135,.0367},{.0389,-.0685,1.0296}};var bi=Inv(br);var sw=Mul(br,w);var dw=Mul(br,d50);
        for(int c=0;c<3;c++){var v=Mul(br,new[]{m[0,c],m[1,c],m[2,c]});for(int r=0;r<3;r++)v[r]*=dw[r]/sw[r];v=Mul(bi,v);for(int r=0;r<3;r++)m[r,c]=v[r];}return m;
    }
    static double Curve(byte[] b,int o,int size,double x){
        string type=Sig(b,o); if(size<12)throw new ArgumentException("TRC size");
        if(type=="curv"){int n=U32(b,o+8);if(n>(size-12)/2)throw new ArgumentException("TRC bounds");if(n==0)return x;if(n==1)return Math.Pow(x,U16(b,o+12)/256.0);
            double p=x*(n-1);int lo=(int)Math.Floor(p),hi=Math.Min(lo+1,n-1);return (U16(b,o+12+2*lo)*(hi-p)+U16(b,o+12+2*hi)*(p-lo))/65535.0+(lo==hi?U16(b,o+12+2*lo)/65535.0:0);}
        if(type!="para")throw new ArgumentException("TRC type");int f=U16(b,o+8);int[] need={16,24,28,32,40};if(f>4||size<need[f])throw new ArgumentException("TRC function");
        double g=Fixed(b,o+12);if(f==0)return Math.Pow(x,g);double a=Fixed(b,o+16),bb=Fixed(b,o+20);if(a==0)throw new ArgumentException("TRC a");
        if(f==1)return x< -bb/a?0:Math.Pow(a*x+bb,g);double c0=Fixed(b,o+24);if(f==2)return x< -bb/a?c0:Math.Pow(a*x+bb,g)+c0;
        double d=Fixed(b,o+28);if(f==3)return x<d?c0*x:Math.Pow(a*x+bb,g);return x<d?c0*x+Fixed(b,o+36):Math.Pow(a*x+bb,g)+Fixed(b,o+32);
    }
    public static bool MutationControls(byte[] profile,string target) {
        if(Check(profile,target)!="")return false;
        var matrix=(byte[])profile.Clone();var curve=(byte[])profile.Clone();bool sawMatrix=false,sawCurve=false;
        for(int i=0;i<U32(profile,128);i++) {
            int p=132+12*i,o=U32(profile,p+4);string name=Sig(profile,p);
            if(name=="rXYZ"){matrix[o+8]^=0x10;sawMatrix=true;}
            if(name=="rTRC") {
                if(Sig(profile,o)=="para"){curve[o+12]^=0x20;sawCurve=true;}
                else if(Sig(profile,o)=="curv") {int n=U32(profile,o+8);if(n>0){curve[o+12+2*(n/2)]^=128;sawCurve=true;}}
            }
        }
        return sawMatrix&&sawCurve&&Check(matrix,target)!=""&&Check(curve,target)!="";
    }
    public static string Check(byte[] b,string target){
        try{
            if(b==null||b.Length<132||b.Length>33554432||U32(b,0)!=b.Length||Sig(b,36)!="acsp"||Sig(b,16)!="RGB "||Sig(b,20)!="XYZ ")return "ICC header";
            int n=U32(b,128);if(n<1||n>(b.Length-132)/12)return "ICC tag count";int end=132+12*n;var tags=new Dictionary<string,int[]>();
            for(int i=0;i<n;i++){int p=132+12*i;string name=Sig(b,p);int off=U32(b,p+4),len=U32(b,p+8);if(tags.ContainsKey(name)||off<end||len<8||off>b.Length-len)return "ICC tag bounds";tags[name]=new[]{off,len};}
            var m=Colorants(target);string[] names={"rXYZ","gXYZ","bXYZ","wtpt"};
            for(int c=0;c<4;c++){if(!tags.ContainsKey(names[c]))return "missing "+names[c];var t=tags[names[c]];if(t[1]<20||Sig(b,t[0])!="XYZ ")return "XYZ type";
                for(int r=0;r<3;r++){double actual=Fixed(b,t[0]+8+4*r),want=c==3?new[]{.9642,1.0,.8249}[r]:m[r,c];if(Math.Abs(actual-want)>.003)return names[c]+" mismatch";}}
            foreach(string name in new[]{"rTRC","gTRC","bTRC"}){if(!tags.ContainsKey(name))return "missing "+name;var t=tags[name];
                for(int i=0;i<=64;i++){double x=i/64.0;double want=target=="BT.2020"?(x<4.5*.0180539685108078?x/4.5:Math.Pow((x+.0992968268094429)/1.0992968268094429,1/.45)):(x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4));
                    double actual=Curve(b,t[0],t[1],x);if(double.IsNaN(actual)||double.IsInfinity(actual)||Math.Abs(actual-want)>.001)return name+" mismatch";}}
            return "";
        }catch(Exception e){return "ICC parse: "+e.GetType().Name;}
    }
}
'@
}
function Test-GridIcc([string]$Path, [string]$Target, [string]$Key) {
  $profile = Join-Path $d ("label_" + $Key + '.icc'); $stderr = "$profile.err"
  if ([IO.Path]::GetExtension($Path) -ieq '.jxl') {
    $dj = Get-ToolPath 'djxl'; if (-not $dj) { return @{ Ok = $false; Detail = 'djxl missing' } }
    $decoded = Join-Path $d ("label_" + $Key + '.png')
    $r = ExecCli $dj "`"$Path`" `"$decoded`" --orig_icc_out=`"$profile`" --quiet"
    if ($r.Rc -ne 0) { return @{ Ok = $false; Detail = 'JXL ICC extraction failed' } }
  } else {
    $p = Start-Process -FilePath $ex -ArgumentList "-b -ICC_Profile `"$Path`"" -Wait -NoNewWindow -PassThru -RedirectStandardOutput $profile -RedirectStandardError $stderr
    if ($p.ExitCode -ne 0) { return @{ Ok = $false; Detail = "ICC extraction exit=$($p.ExitCode)" } }
  }
  if (-not (Test-Path -LiteralPath $profile -PathType Leaf)) { return @{ Ok = $false; Detail = 'ICC extraction absent' } }
  $bytes = [IO.File]::ReadAllBytes($profile)
  $issue = [MatrixGridIcc]::Check($bytes, $Target)
  $mutations = if ($issue -ceq '') { [MatrixGridIcc]::MutationControls($bytes, $Target) } else { $false }
  $detail = if ($issue -ne '') { $issue } elseif (-not $mutations) { 'ICC mutation negative controls did not reject changed matrix/TRC' } else { '' }
  return @{ Ok = ($detail -ceq ''); Detail = $detail; Bytes = $bytes.Length; ProfilePath = $profile }
}
function Test-GridLabel($Cell) {
  $rp = Get-TargetPrimariesRx $Cell.tgt; $rt = Get-TargetTransferRx $Cell.tgt
  $tagged = $rp -and $rt -and $Cell.prim -match $rp -and $Cell.trc -match $rt
  if ($Cell.hasIcc) {
    $icc = Test-GridIcc $Cell.out $Cell.tgt ([IO.Path]::GetFileNameWithoutExtension($Cell.cellLog))
    # ICC+CICP 同时存在时两者都不能反向声明；缺省 CICP 可由已核实 ICC 单独描述。
    $contradiction = ($Cell.prim -and $Cell.prim -notmatch '^(?i:unknown|unspecified)$' -and $Cell.prim -notmatch $rp) -or
      ($Cell.trc -and $Cell.trc -notmatch '^(?i:unknown|unspecified)$' -and $Cell.trc -notmatch $rt)
    return @{ Ok = ($icc.Ok -and -not $contradiction); Detail = "ICC=$($icc.Detail) conflictingTags=$contradiction" }
  }
  if ($tagged) { return @{ Ok = $true; Detail = 'target P/T read back' } }
  # 仅 WebP/sRGB 的确定无 ICC + 隐含 sRGB 可作无标注例外；泛「降级」绝不放行广色域。
  $implicit = $Cell.fmt -eq 'webp' -and $Cell.tgt -eq 'sRGB' -and $Cell.metadataOk -and
    $Cell.messages -match '\bUnlabeledAssumedSrgb\b' -and $Cell.taskOk
  return @{ Ok = [bool]$implicit; Detail = "target P/T missing or wrong (P=$($Cell.prim) T=$($Cell.trc)); implicitSrgb=$implicit" }
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
        # 这张网格要分辨矩阵偏差，不把默认 -d 2.4 的 JXL 压缩噪声预算混进 NUM。
        $codecArgs = if ($f -eq 'jxl') { '--encoder Cjxl --jxl-effort 7 --lossless true' } else { '' }
        $r = ExecCli $exe "--headless --log-level Debug --log-format JsonLines -i `"$srcP`" -o `"$outDir`" -f $f --bit-depth $bd --color-space `"$tn`" --color-strategy recommended --color-gamut-map off --color-engine engine $codecArgs $extra" -Tee $cellLog
        # 取产物只有一条路：Resolve-Prod（Leaf 原样 / Container 取最大文件 / 没有 ⇒ $null）
        $prod = Resolve-Prod $outDir
        $tempArtifacts = @(Get-ChildItem -LiteralPath $outDir -File -Filter '*_exiftool_tmp' -EA SilentlyContinue).Count
        $made = ($null -ne $prod)
        $st = if ($made) { Get-StreamInfo $prod } else { $null }
        $cm = if ($made) { Get-ColorMetadata $prod } else { $null }
        $jxi = if ($made -and $f -eq 'jxl') { Get-JxlInfo $prod } else { $null }
        $delivered = if ($jxi -and $jxi.Ok) { $jxi.BitDepth }
                     elseif ($f -ne 'jxl' -and $st -and $st.Ok) { Get-GridDeliveredDepth $prod ([string]$st.PixFmt) } else { $null }
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
        $task = Read-GridTask $r.Out $r.Rc $made
        $capNote = $task.Detail
        $encoderLanded = $f -ne 'jxl'
        if ($f -eq 'jxl' -and $made) {
          $expectedJxlDepth = if ($bd -eq 8) { 8 } else { 16 } # 10/12 requests are promoted to RGB16.
          $exec = Test-GridCjxlExecution $task.Messages $srcP $prod $tn $expectedJxlDepth
          $encoderLanded = $exec.Ok -and $delivered -eq $expectedJxlDepth
          $capNote += '; ' + $exec.Detail
        }
        # 支持档必须实际交付同档；缺读数或未播报的折档也不能算 ok。
        $verdict = if ($capOk) {
          if ($task.Ok -and $null -ne $delivered -and [int]$delivered -eq $bd -and $encoderLanded) { 'ok' } else { 'CAP-FAIL' }
        } else {
          $o = Get-BitDepthOutcome -Requested $bd -Made $made -Rc $r.Rc -Log $task.Messages -Delivered $delivered
          $capNote += '; ' + $o.Evidence
          if ($made -and (-not $task.Ok -or -not $encoderLanded -or $null -eq $delivered)) { 'CAP-FAIL' } else { $o.Verdict }
        }
        $capNote += "; requested=$bd delivered=$delivered selectedEncoder=$(if ($f -eq 'jxl') { 'cjxl' } else { 'format-default' }) encoderLanded=$encoderLanded"
        $rows.Add([pscustomobject]@{
            src = $sk; tgt = $tn; fmt = $f; bd = $bd; rc = $r.Rc; made = $made; verdict = $verdict
            announced = ($task.Messages -match 'BitDepthRaised|BitDepthReduced')
            out = $prod; tempArtifacts = $tempArtifacts; pixfmt = $(if ($st) { [string]$st.PixFmt } else { '' })
            prim = $(if ($cm -and $cm.Primaries) { $cm.Primaries } elseif ($st -and $st.Ok) { $st.ColorPrimaries } else { '' })
            width = $st.Width; height = $st.Height
            trc = $(if ($cm -and $cm.Transfer) { $cm.Transfer } elseif ($st -and $st.Ok) { $st.ColorTransfer } else { '' })
            delivered = $delivered; capNote = $capNote; cellLog = $cellLog
            metadataOk = ($cm -and $cm.Ok); taskOk = $task.Ok; taskCode = $task.Code; terminalCount = $task.Count
            messages = $task.Messages; encoder = $(if ($f -eq 'jxl') { 'cjxl' } else { 'format-default' }); encoderLanded = $encoderLanded
            # jxlinfo 的 “(possibly) lossless” 只说明采用可能无损的编码工具；逐样本再对独立参照。
            jxlLosslessMode = $(if ($jxi -and $jxi.Ok) { $jxi.Raw -match '(?im)^JPEG XL image, .*?, (?:\(possibly\) )?lossless,' } else { $false })
            # ⚠ 字段名必须照 `Get-ColorMetadata` 的真实形状取：它给的是 `HasIcc` / `IccProfileName` /
            #   `IccProfileDescription`，**没有** `.IccDescription`（我上一版读错 ⇒ 恒空 ⇒ 把带 ICC 的 TIFF
            #   判成"无标注"，62 格里的一半就是这么来的）。
            hasIcc = ($cm.HasIcc -or ($jxi -and $jxi.Ok -and $jxi.Raw -match '(?im)^\d+-byte ICC profile,.*Color space:'))
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
$promOk = @($rows | Where-Object { $capDepths[$_.fmt] -contains $_.bd })
$promBad = @($promOk | Where-Object { -not $_.taskOk -or $null -eq $_.delivered -or [int]$_.delivered -ne [int]$_.bd -or -not $_.encoderLanded })
foreach ($b in ($promBad | Select-Object -First 6)) { Say ("  CAP承诺? {0,-9} →{1,-11} {2}{3} requested={3} delivered={4} task={5}；{6}" -f $b.src, $b.tgt, $b.fmt, $b.bd, $b.delivered, $b.taskCode, $b.capNote) }
CK ($promBad.Count -eq 0) "CAP 承诺账：$($promOk.Count) 个支持格逐格要求进程/唯一成功终态/产物/实际同位深/选定编码器，违规=$($promBad.Count)；与升降档日志无关"
$tempTotal = [int](($rows | Measure-Object -Property tempArtifacts -Sum).Sum)
CK ($tempTotal -eq 0) "交付目录不得残留 ExifTool 暂存件（*_exiftool_tmp），实得=$tempTotal；裁判只读精确最终产物"

# ── 3) LBL 标签账 ───────────────────────────────────────────────────────────────────
Say '== 3) LBL 标签回读 =='
$lblBad = New-Object System.Collections.Generic.List[string]
$lblN = 0
foreach ($g in ($rows | Where-Object { $_.verdict -eq 'ok' })) {
  $lblN++
  $label = Test-GridLabel $g
  if (-not $label.Ok) { $lblBad.Add("$($g.src)→$($g.tgt) $($g.fmt)$($g.bd) $($label.Detail)") }
}
foreach ($x in ($lblBad | Select-Object -First 10)) { Say "  LBL? $x" }
CK ($lblBad.Count -eq 0) "LBL 标签账：$lblN 个成功格逐一核对目标 P/T 或实际 ICC 矩阵/白点/三通道曲线，违规=$($lblBad.Count)；任意 ICC/泛降级词不作正证"

# ── 4) NUM：只从源像素和标准构造参照，不读产品目标标签/日志决定数值或放行 ──────────
Say '== 4) NUM 独立 SDR 矩阵 / TRC / 原生 RGB 码值对拍 =='
# IEC 61966-2-1 / SMPTE EG 432-1 / ITU-R BT.2020 色度 + D65；ITU-R BT.709-6 分段 OETF。
# RGB → XYZ 的列由 xy 换算并使 RGB 白点等于 D65；目标矩阵求逆。GMO=off 明确约定在线性目标域裁切。
# FFmpeg 只解码 RGB，不进 PSNR 自动格式协商（旧 16bit 对拍可悄悄退到 yuv/RGB8）。
if (-not ('MatrixGridNum' -as [type])) {
  Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
public static class MatrixGridNum {
    // Derive a 3x3 inverse with partial pivoting; no engine constants, ICC reader or transform code.
    static double[,] Inverse(double[,] m) {
        var t = new double[3,6];
        for (int y=0;y<3;y++) for (int x=0;x<3;x++) { t[y,x]=m[y,x]; t[y,x+3]=y==x?1:0; }
        for (int i=0;i<3;i++) {
            int p=i;
            for (int y=i+1;y<3;y++) if (Math.Abs(t[y,i])>Math.Abs(t[p,i])) p=y;
            for (int x=0;x<6;x++) { double a=t[i,x]; t[i,x]=t[p,x]; t[p,x]=a; }
            double d=t[i,i];
            for (int x=0;x<6;x++) t[i,x]/=d;
            for (int y=0;y<3;y++) if (y!=i) {
                double k=t[y,i];
                for (int x=0;x<6;x++) t[y,x]-=k*t[i,x];
            }
        }
        for (int y=0;y<3;y++) for (int x=0;x<3;x++) m[y,x]=t[y,x+3];
        return m;
    }
    static double[,] Xyz(string name) {
        double[] xy;
        switch (name) {
        case "sRGB": xy=new[]{.64,.33,.30,.60,.15,.06}; break;
        case "Display P3": xy=new[]{.68,.32,.265,.69,.15,.06}; break;
        case "BT.2020": xy=new[]{.708,.292,.170,.797,.131,.046}; break;
        default: throw new ArgumentException(name);
        }
        var white=new[]{.3127/.329, 1.0, (1-.3127-.329)/.329};
        var m=new double[3,3];
        for (int i=0;i<3;i++) { m[0,i]=xy[2*i]/xy[2*i+1]; m[1,i]=1; m[2,i]=(1-xy[2*i]-xy[2*i+1])/xy[2*i+1]; }
        var inv=Inverse((double[,])m.Clone());
        for (int x=0;x<3;x++) {
            double scale=0;
            for (int k=0;k<3;k++) scale+=inv[x,k]*white[k];
            for (int y=0;y<3;y++) m[y,x]*=scale;
        }
        return m;
    }
    const double A=1.0992968268094429, B=.0180539685108078;
    static double Decode(double e, bool rec709) => rec709
        ? (e < 4.5*B ? e/4.5 : Math.Pow((e+(A-1))/A, 1/.45))
        : (e <= .04045 ? e/12.92 : Math.Pow((e+.055)/1.055, 2.4));
    static double Encode(double l, bool rec709) {
        l=Math.Max(0,Math.Min(1,l));
        return rec709 ? (l < B ? 4.5*l : A*Math.Pow(l,.45)-(A-1))
                      : (l <= .0031308 ? 12.92*l : 1.055*Math.Pow(l,1/2.4)-.055);
    }
    static int Code(byte[] data,int i,int bits) => bits==8 ? data[i] : data[2*i]+256*data[2*i+1];
    public static double[] Reference(byte[] src,int bits,string input,string target) {
        int n=src.Length/(bits/8), peak=(1<<bits)-1;
        var toXyz=Xyz(input); var fromXyz=Inverse(Xyz(target));
        var matrix=new double[3,3];
        for (int y=0;y<3;y++) for (int x=0;x<3;x++) for (int k=0;k<3;k++) matrix[y,x]+=fromXyz[y,k]*toXyz[k,x];
        var table=new double[peak+1];
        for (int i=0;i<=peak;i++) table[i]=Decode((double)i/peak,input=="BT.2020");
        var result=new double[n];
        for (int i=0;i<n;i+=3) {
            if (input==target) { for (int c=0;c<3;c++) result[i+c]=(double)Code(src,i+c,bits)/peak; continue; }
            double r=table[Code(src,i,bits)], g=table[Code(src,i+1,bits)], b=table[Code(src,i+2,bits)];
            for (int c=0;c<3;c++) result[i+c]=Encode(matrix[c,0]*r+matrix[c,1]*g+matrix[c,2]*b,target=="BT.2020");
        }
        return result;
    }
    // 原生 16-bit 阶梯：每行连续 1 LSB，各通道跨不同码段；没有 8-bit 前身。
    public static byte[] NativeSteps() {
        var data=new byte[256*192*6];
        for(int p=0;p<256*192;p++) for(int c=0;c<3;c++) {
            int code=((p/256)*769+(p%256)+11731*c+1)&65535, at=6*p+2*c;
            data[at]=(byte)code;data[at+1]=(byte)(code>>8);
        }
        return data;
    }
    public static byte[] CollapseTo8(byte[] data) {
        var collapsed=new byte[data.Length];for(int i=0;i<data.Length/2;i++) {
            int code=(int)Math.Round(Code(data,i,16)/257.0)*257;collapsed[2*i]=(byte)code;collapsed[2*i+1]=(byte)(code>>8);
        }return collapsed;
    }
    public static int LowDetailSamples(byte[] data) {
        int n=0;for(int i=0;i<data.Length/2;i++)if(Code(data,i,16)%257!=0)n++;return n;
    }
    public static double[] Error(double[] reference,byte[] actual,int bits) {
        if (reference.Length*(bits/8)!=actual.Length) throw new ArgumentException("RGB sample counts differ");
        double peak=(1<<bits)-1, sum=0, max=0;
        for (int i=0;i<reference.Length;i++) {
            double d=Math.Abs(reference[i]*peak-Code(actual,i,bits));
            sum+=d*d; max=Math.Max(max,d);
        }
        return new[]{Math.Sqrt(sum/reference.Length),max};
    }
}
'@
}
function Read-NumRgb([string]$Path, [int]$Bits, [string]$Key) {
  $px = if ($Bits -eq 8) { 'rgb24' } elseif ($Bits -eq 16) { 'rgb48le' } else { throw "未知 RGB 位深 $Bits" }
  $raw = Join-Path $d "num_${Key}.rgb"
  $dec = ExecCli $ff "-nostdin -hide_banner -loglevel error -y -i `"$Path`" -frames:v 1 -threads 1 -c:v rawvideo -pix_fmt $px -f rawvideo `"$raw`""
  if ($dec.Rc -ne 0 -or -not (Test-Path -LiteralPath $raw -PathType Leaf)) { throw "RGB 解码 rc=$($dec.Rc)：$($dec.Out)" }
  $buf = [IO.File]::ReadAllBytes($raw)
  if ($buf.Length -ne (256 * 192 * 3 * ($Bits / 8))) { throw "RGB 样本数不符（$($buf.Length) 字节）" }
  return ,$buf
}
function Test-NumFixtureCurve([string]$Path, [string]$Space, [string]$Key) {
  # ICC.1 parametricCurveType 3: g,a,b,c,d in s15Fixed16; y=(ax+b)^g if x>=d, else cx.
  # 这里只核生成夹具 ICC（而非产品输出或描述字符串），三通道参数均要等于独立定义。
  $out = Join-Path $d "num_${Key}.trc"
  $err = "$out.err"
  $p = $null
  for ($attempt = 0; $attempt -lt 3; $attempt++) {
    $p = Start-Process -FilePath $ex -ArgumentList "-b -RedTRC -GreenTRC -BlueTRC `"$Path`"" `
      -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    if ($p.ExitCode -eq 0) { break }
    Start-Sleep -Milliseconds 250
  }
  if ($p.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $out -PathType Leaf)) { return $false }
  $bytes = [IO.File]::ReadAllBytes($out)
  if ($bytes.Length -ne 3*32) { return $false }
  $a = 1.0992968268094429
  $want = if ($Space -eq 'BT.2020') { (1/.45),(1/$a),(($a-1)/$a),(1/4.5),(4.5*.0180539685108078) }
          else { 2.4,(1/1.055),(.055/1.055),(1/12.92),.04045 }
  for ($at=0; $at -lt $bytes.Length; $at+=32) {
    if ([Text.Encoding]::ASCII.GetString($bytes,$at,4) -ne 'para' -or $bytes[$at+8] -ne 0 -or $bytes[$at+9] -ne 3) { return $false }
    for ($i=0; $i -lt 5; $i++) {
      $k = $at+12+4*$i
      $fixed = $bytes[$k]*16777216 + $bytes[$k+1]*65536 + $bytes[$k+2]*256 + $bytes[$k+3]
      if ([Math]::Abs($fixed - 65536*$want[$i]) -gt 1.0) { return $false }
    }
  }
  return $true
}
function Get-NumBudget([int]$SourceBits, [int]$DestBits, [bool]$Identity, [bool]$Narrowing = $false) {
  # 同档 sRGB/P3 直通必须逐码相同；8bit 变换保留 0.5 LSB 舍入 + 0.47 LSB Bayer 及小量 RGB 重采样。
  # 8→16 的 swscale RGB 展码并不等于 n×257（且窄色域近黑的 OETF 可放大这点），预算仍以 8bit LSB 计。
  # 真正的 16→16 不借 8bit 预算；最多 32 码（<0.05% 满量程），给 ICC 固定点和曲线插值留余量。
  if ($Identity -and $SourceBits -eq $DestBits) { return 0.0, 0.0 }
  # 原生 8 位源展到 RGB48 时的 ≤1 输入码偏差，在窄向矩阵的近黑抵消处可放大；
  # 例如 BT.2020 [144,243,69]→sRGB，理想 R=6.407，8→16 展码 + 裁切/抖动后实测 R=3。
  # 只给这种方向增加局部峰值预算；全图 RMS 仍 ≤0.8 个 8 位码，同源真 16 位毫无此项豁免。
  $peak8 = if ($SourceBits -eq 8 -and $Narrowing) { 4.0 } else { 2.0 }
  if ($DestBits -eq 8) { return 0.8, $peak8 }
  if ($SourceBits -eq 8) { return (0.8 * 257), ($peak8 * 257) }
  return 10.0, 32.0
}
$numSourceSpace = [ordered]@{ srgb8='sRGB'; srgb16='sRGB'; p38='Display P3'; p316='Display P3'; bt20208='BT.2020'; bt202016='BT.2020' }
$numSrc = @{}; $numRef = @{}; $numDepth = @{}
$numGamutRank = @{ 'sRGB'=0; 'Display P3'=1; 'BT.2020'=2 }
$numBad = New-Object System.Collections.Generic.List[string]
$numN = 0; $numErr = 0
foreach ($sk in $numSourceSpace.Keys) {
  $numDepth[$sk] = if ($sk.EndsWith('16')) { 16 } else { 8 }
  try {
    if ($numSourceSpace[$sk] -ne 'sRGB') {
      if (-not (Test-NumFixtureCurve $srcs[$sk] $numSourceSpace[$sk] $sk)) { throw '宽色域夹具 ICC 的三通道曲线缺失或不符' }
    }
    $numSrc[$sk] = Read-NumRgb $srcs[$sk] $numDepth[$sk] "source_$sk"
    foreach ($target in $targets.Keys) {
      $numRef["$sk/$target"] = [MatrixGridNum]::Reference($numSrc[$sk], $numDepth[$sk], $numSourceSpace[$sk], $target)
    }
  } catch { $numErr++; Say "  NUM 夹具取数失败 ${sk}：$($_.Exception.Message)" }
}
# 独立校验夹具确实按声称的色域/TRC 编码；错误 gamma 即使和 ICC 口径相反也不能靠产品自洽通过。
foreach ($sk in @('p38','p316','bt20208','bt202016')) {
  $bits = $numDepth[$sk]; $srcKey = "srgb$bits/$($numSourceSpace[$sk])"
  if (-not ($numSrc.ContainsKey($sk) -and $numRef.ContainsKey($srcKey))) { continue }
  $metric = [MatrixGridNum]::Error($numRef[$srcKey], $numSrc[$sk], $bits)
  $budget = if ($bits -eq 8) { 0.65, 1.1 } else { 12.0, 48.0 } # 夹具 zimg 先量化线性 16 位，再编码目标 TRC
  if ($metric[0] -gt $budget[0] -or $metric[1] -gt $budget[1]) {
    $numBad.Add(('夹具 {0} × {1}-bit 色度/TRC 不自洽：RMS={2:F3}/{3} max={4:F3}/{5} LSB' -f $sk,$bits,$metric[0],$budget[0],$metric[1],$budget[1]))
  }
}
# 不用产品当负控：把源的原始 sRGB 码值假装是 P3，必须在独立数值预算外；正确同空间则逐码相同。
if ($numSrc.ContainsKey('srgb16') -and $numRef.ContainsKey('srgb16/Display P3')) {
  $wrong = [MatrixGridNum]::Error($numRef['srgb16/Display P3'], $numSrc['srgb16'], 16)
  $right = [MatrixGridNum]::Error($numRef['srgb16/sRGB'], $numSrc['srgb16'], 16)
  $strict = Get-NumBudget 16 16 $false
  CK ($wrong[0] -gt $strict[0] -and $wrong[1] -gt $strict[1] -and $right[1] -eq 0) 'NUM 自测：sRGB 原码冒充 P3 必须被拒，真直通逐码相同'
} else { CK $false 'NUM 自测缺少独立样本' }
$numCells = @($rows | Where-Object { $_.verdict -eq 'ok' -and $_.fmt -in @('png', 'tiff', 'jxl') -and $_.src -notlike 'adobe*' })
if ($srcs.Contains('adobe8')) { Say '  NUM Adobe：外部 ICC 没有命名数学参照，仅进 CAP/LBL，不拿 sRGB 冒充' }
foreach ($g in $numCells) {
  $name = "$($g.src)→$($g.tgt) $($g.fmt)$($g.bd)"
  $refKey = "$($g.src)/$($g.tgt)"
  if (-not $numRef.ContainsKey($refKey)) { $numErr++; Say "  NUM 取数失败 ${name}：无源参照"; continue }
  if ($g.delivered -ne $g.bd -or $g.width -ne 256 -or $g.height -ne 192 -or ($g.fmt -eq 'jxl' -and -not $g.jxlLosslessMode)) {
    $numBad.Add("${name}：交付位深=$($g.delivered) 尺寸=$($g.width)x$($g.height) JXL无损模式=$($g.jxlLosslessMode)")
    continue
  }
  try { $actual = Read-NumRgb $g.out $g.bd ((Split-Path -Leaf (Split-Path -Parent $g.out))) }
  catch { $numErr++; Say "  NUM 取数失败 ${name}：$($_.Exception.Message)"; continue }
  $metric = [MatrixGridNum]::Error($numRef[$refKey], $actual, $g.bd)
  $ident = ($numSourceSpace[$g.src] -eq $g.tgt -and $g.tgt -ne 'BT.2020')
  $narrowing = ($numGamutRank[$numSourceSpace[$g.src]] -gt $numGamutRank[$g.tgt])
  $budget = Get-NumBudget $numDepth[$g.src] $g.bd $ident $narrowing
  $numN++
  if ($metric[0] -gt $budget[0] -or $metric[1] -gt $budget[1]) {
    $numBad.Add(('{0} RMS={1:F3}/{2} max={3:F3}/{4} LSB' -f $name,$metric[0],$budget[0],$metric[1],$budget[1]))
  }
}
foreach ($x in $numBad) { Say "  NUM? $x" }
$numExpected = $numSourceSpace.Count * $targets.Count * @($fmts | Where-Object { $_ -in @('png','tiff','jxl') }).Count * 2
CK (($numN -eq $numExpected) -and ($numBad.Count -eq 0) -and ($numErr -eq 0)) "NUM 对拍：$numN/$numExpected 格比过（候选 $($numCells.Count)），红 $($numBad.Count)，参照/解码取数失败 $numErr"

# ── 4b) 原生 16-bit 低位阶梯（与 8-bit 展码网格分账）──────────────────────────
Say '== 4b) 原生 16-bit：1 LSB 阶梯恒等与独立变换 =='
$nativeRows = New-Object System.Collections.Generic.List[object]
$nativeBad = New-Object System.Collections.Generic.List[string]
$nativeBytes = [MatrixGridNum]::NativeSteps()
$nativeRaw = Join-Path $d 'native16.rgb48le'; $nativePng = Join-Path $d 'native16.png'
[IO.File]::WriteAllBytes($nativeRaw, $nativeBytes)
$nativeMake = ExecCli $ff "-nostdin -hide_banner -loglevel error -y -f rawvideo -pixel_format rgb48le -video_size 256x192 -i `"$nativeRaw`" -vf iccgen=color_primaries=bt709:color_trc=iec61966-2-1:force=1 -frames:v 1 -pix_fmt rgb48be `"$nativePng`""
$nativeReady = $false
try {
  if ($nativeMake.Rc -ne 0 -or -not (Test-Path -LiteralPath $nativePng -PathType Leaf)) { throw "native fixture encoder exit=$($nativeMake.Rc)" }
  $nativeRead = Read-NumRgb $nativePng 16 'native_source'
  $nativeIdentity = [MatrixGridNum]::Reference($nativeBytes, 16, 'sRGB', 'sRGB')
  $fixtureError = [MatrixGridNum]::Error($nativeIdentity, $nativeRead, 16)
  $low = [MatrixGridNum]::LowDetailSamples($nativeRead)
  if ($fixtureError[1] -ne 0 -or $low -lt 100000) { throw "fixture not exact native RGB16 (max=$($fixtureError[1]) lowDetail=$low)" }
  # 坍缩到 RGB8 再展码必须被原生恒等和变换两把尺子判负。
  $collapsed = [MatrixGridNum]::CollapseTo8($nativeRead)
  $collapseError = [MatrixGridNum]::Error($nativeIdentity, $collapsed, 16)
  $p3Reference = [MatrixGridNum]::Reference($nativeRead, 16, 'sRGB', 'Display P3')
  $p3Collapsed = [MatrixGridNum]::Reference($collapsed, 16, 'sRGB', 'Display P3')
  $collapsedP3Bytes = New-Object byte[] $nativeRead.Length
  for ($i=0; $i -lt $p3Collapsed.Length; $i++) {
    $v = [int][Math]::Round($p3Collapsed[$i]*65535); $collapsedP3Bytes[2*$i] = [byte]($v -band 255); $collapsedP3Bytes[2*$i+1] = [byte]($v -shr 8)
  }
  $transformCollapseError = [MatrixGridNum]::Error($p3Reference, $collapsedP3Bytes, 16)
  if ($collapseError[1] -eq 0 -or $transformCollapseError[0] -le 10 -or $transformCollapseError[1] -le 32) { throw 'native precision negative control cannot distinguish an RGB8 collapse' }
  $nativeReady = $true
  CK $true "N16 夹具：原始 RGB16 写入/读回逐码相同，非8bit展码样本=$low；8bit坍缩负控恒等max=$($collapseError[1])、P3 RMS/max=$($transformCollapseError -join '/')"
} catch { CK $false "N16 夹具/负控失败：$($_.Exception.Message)" }
foreach ($case in @(@{ Fmt='png'; Target='sRGB'; Identity=$true }, @{ Fmt='jxl'; Target='sRGB'; Identity=$true }, @{ Fmt='png'; Target='Display P3'; Identity=$false })) {
  $key = 'native_' + $case.Fmt + '_' + ($case.Target -replace '[^A-Za-z0-9]', '')
  $dir = Join-Path $d $key
  $row = [pscustomobject]@{ fmt=$case.Fmt; target=$case.Target; process=$null; taskCode=$null; terminalCount=0; made=$false; depth=$null; encoder=''; encoderEvidence=''; rms=$null; max=$null; measured=$false; error='' }
  $nativeRows.Add($row)
  try {
    if (-not $nativeReady) { throw 'native fixture/oracle precheck failed' }
    $codec = if ($case.Fmt -eq 'jxl') { '--encoder Cjxl --jxl-effort 7 --lossless true' } else { '--png-optimizer off' }
    $run = ExecCli $exe "--headless --log-level Debug --log-format JsonLines -i `"$nativePng`" -o `"$dir`" -f $($case.Fmt) --bit-depth 16 --color-space `"$($case.Target)`" --color-strategy recommended --color-gamut-map off --color-engine engine $codec" -Tee "$dir.log"
    $prod = Resolve-Prod $dir; $task = Read-GridTask $run.Out $run.Rc ([bool]$prod)
    $row.process=$run.Rc; $row.taskCode=$task.Code; $row.terminalCount=$task.Count; $row.made=[bool]$prod
    if (-not $task.Ok) { throw $task.Detail }
    if ($case.Fmt -eq 'jxl') {
      $j = Get-JxlInfo $prod; $row.depth=$j.BitDepth; $row.encoder='cjxl'
      $exec = Test-GridCjxlExecution $task.Messages $nativePng $prod $case.Target 16
      $row.encoderEvidence = $exec.Detail
      if (-not $exec.Ok -or -not $j.Ok -or $j.BitDepth -ne 16 -or $j.Raw -notmatch '(?im)^JPEG XL image, .*?, (?:\(possibly\) )?lossless,') {
        throw "JXL must read back 16-bit lossless and actual engine cjxl execution (depth=$($j.BitDepth); $($exec.Detail))"
      }
    } else {
      $st = Get-StreamInfo $prod; $row.depth=Get-DeliveredDepth $st.PixFmt; $row.encoder='ffmpeg/png'
      if (-not $st.Ok -or $row.depth -ne 16) { throw 'PNG native 16-bit delivery missing' }
    }
    $actual = Read-NumRgb $prod 16 $key
    $reference = [MatrixGridNum]::Reference($nativeRead,16,'sRGB',$case.Target)
    $metric = [MatrixGridNum]::Error($reference,$actual,16); $row.rms=$metric[0]; $row.max=$metric[1]; $row.measured=$true
    $budget = Get-NumBudget 16 16 $case.Identity
    if ($metric[0] -gt $budget[0] -or $metric[1] -gt $budget[1]) { throw "native RMS/max=$($metric -join '/') > $($budget -join '/') LSB" }
  } catch { $row.error=$_.Exception.Message; $nativeBad.Add("$key：$($row.error)") }
}
$nativeRows | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $d 'native16-outcomes.csv')
foreach ($x in $nativeBad) { Say "  N16? $x" }
CK ($nativeRows.Count -eq 3 -and @($nativeRows | Where-Object measured).Count -eq 3 -and $nativeBad.Count -eq 0) "N16：3/3 原生16bit格须完整到场，PNG/JXL恒等逐码相同、PNG→P3沿用10/32码预算（红=$($nativeBad.Count)）"

# ── 5) RT 往返自洽（转置/顺序错的探测器）────────────────────────────────────────────
Say '== 5) RT 往返漂移 =='
$rtBad = New-Object System.Collections.Generic.List[string]
$rtRows = New-Object System.Collections.Generic.List[object]
$rtN = 0; $rtExpected = 9
foreach ($sk in @('srgb8', 'p38', 'bt20208')) {
  foreach ($tn in @('Display P3', 'BT.2020', 'sRGB')) {
    $mid = Join-Path $d "rt1_${sk}_$($tn -replace '[^A-Za-z0-9]', '').png"
    $back = Join-Path $d "rt2_${sk}_$($tn -replace '[^A-Za-z0-9]', '').png"
    $srcName = switch -Regex ($sk) { '^srgb' { 'sRGB' } '^p3' { 'Display P3' } '^bt2020' { 'BT.2020' } }
    $row = [pscustomobject]@{ src = $sk; via = $tn; firstProcess = $null; firstCode = $null; firstMade = $false; secondProcess = $null; secondCode = $null; secondMade = $false; measured = $false; psnr = $null; error = '' }
    $rtRows.Add($row)
    try {
      if (-not $srcs[$sk]) { throw 'source fixture absent' }
      $a = ExecCli $exe "--headless --log-level Debug --log-format JsonLines -i `"$($srcs[$sk])`" -o `"$mid`" -f png --bit-depth 16 --color-space `"$tn`" --color-engine engine" -Tee "$mid.log"
      $midProd = Resolve-Prod $mid; $at = Read-GridTask $a.Out $a.Rc ([bool]$midProd)
      $row.firstProcess = $a.Rc; $row.firstCode = $at.Code; $row.firstMade = [bool]$midProd
      if (-not $at.Ok) { throw "first leg: $($at.Detail)" }
      $b = ExecCli $exe "--headless --log-level Debug --log-format JsonLines -i `"$midProd`" -o `"$back`" -f png --bit-depth 16 --color-space `"$srcName`" --color-engine engine" -Tee "$back.log"
      $backProd = Resolve-Prod $back; $bt = Read-GridTask $b.Out $b.Rc ([bool]$backProd)
      $row.secondProcess = $b.Rc; $row.secondCode = $bt.Code; $row.secondMade = [bool]$backProd
      if (-not $bt.Ok) { throw "second leg: $($bt.Detail)" }
      $r = Invoke-Psnr -Ref $srcs[$sk] -Dist $backProd
      if (-not $r.Ok) { throw "PSNR read failed: $($r.Error)" }
      $row.measured = $true; $row.psnr = $r.Psnr; $rtN++
      if ([double]$r.Psnr -lt 45) { throw "PSNR=$($r.Psnr) < 45 dB" }
    } catch {
      $row.error = $_.Exception.Message
      $rtBad.Add("$sk →$tn →$srcName：$($row.error)")
    }
  }
}
$rtRows | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $d 'rt-outcomes.csv')
foreach ($x in $rtBad) { Say "  RT? $x" }
CK (($rtRows.Count -eq $rtExpected) -and ($rtN -eq $rtExpected) -and ($rtBad.Count -eq 0)) "RT 往返：预期=$rtExpected 到场=$($rtRows.Count) 完整测量=$rtN 红=$($rtBad.Count)；任一路进程/终态/产物/取数失败均计红"

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
# NC3 只测 LBL 的 ICC 分支，不要求产品网格选 ICC：有 CICP 的正常交付本来就可以不写 ICC。
#   本轮独立生成仅有 ICC 标注的 sRGB PNG；同一份像素只剥 ICC 后，§3 所用的回读必须由有变无。
RunGate 'NC3' {
  $keep = Join-Path $d 'nc3_icc.png'; $strip = Join-Path $d 'nc3_stripped.png'
  $mk = ExecCli $ff ('-hide_banner -loglevel error -y -f lavfi -i testsrc=s=64x48:r=1 ' +
    '-vf "format=rgb24,iccgen=color_primaries=bt709:color_trc=iec61966-2-1:force=1,setparams=color_primaries=unknown:color_trc=unknown:colorspace=unknown" ' +
    '-frames:v 1 "' + $keep + '"')
  if ($mk.Rc -ne 0 -or -not (Test-Path -LiteralPath $keep -PathType Leaf)) {
    CK $false "NC3 带 ICC 的独立正控生成失败（rc=$($mk.Rc)）：$($mk.Out)"
    return
  }
  Copy-Item -LiteralPath $keep -Destination $strip -ErrorAction Stop
  $rm = ExecCli $ex ('-overwrite_original -ICC_Profile= "' + $strip + '"')
  if ($rm.Rc -ne 0) {
    CK $false "NC3 ICC 剥除失败（rc=$($rm.Rc)）：$($rm.Out)"
    return
  }
  $cmKeep = Get-ColorMetadata $keep
  $cmStrip = Get-ColorMetadata $strip
  $px = Invoke-Psnr -Ref $keep -Dist $strip
  # 取数失败不能充当「无标注」，另有 CICP 时也不能用这对文件证明 LBL 会判缺。
  $st3 = $cmKeep.Ok -and $cmStrip.Ok -and -not $cmKeep.Primaries -and -not $cmStrip.Primaries -and
    $cmKeep.HasIcc -and -not $cmStrip.HasIcc -and $px.Ok -and $px.IsIdentical
  CK $st3 "NC3 标签尺子有牙：原件 HasIcc=$($cmKeep.HasIcc)/读=$($cmKeep.Ok)/CICP='$($cmKeep.Primaries)'；剥除后 HasIcc=$($cmStrip.HasIcc)/读=$($cmStrip.Ok)/CICP='$($cmStrip.Primaries)'；像素相同=$($px.IsIdentical)/读=$($px.Ok)（$($cmKeep.Error) $($cmStrip.Error) $($px.Error)）"
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
# 先读 JsonLines 的 item_completed.code，再核对 item_log 中的拒绝形态（优先稳定字段）。
# headless 进程只返回聚合 rc=1；项级 91 是契约拒绝，90 是显式路由拦截，92 是执行失败。
# F1/F4 使用 VerdictKind.Reject；F2 使用 Engine 显式输出矩阵拒绝的 [color-out-matrix] 模板 3/4；
# F3 是尚无独立拒绝码的传统/分派级固定前缀，不认其人读原因；F5 使用 EngineBlockCodes 的实际词表。
# 引擎现在可能先打裁决=Reject、再警告并回传统链路。规划行不是终态，只有项级 code 与形态吻合才算拒绝。
# 缺终态、错码、空日志、异常/崩溃均不判绿；源码载体失效也一律按红处理。
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
$srcMatrixPlan  = 'src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs'
$srcQueueLog    = 'src/FfmpegGui/Services/ConsoleQueueObserver.cs'
$txByFile = @{}
foreach ($src in @($srcVerdictKind, $srcVerdictLog, $srcBlockCodes, $srcMatrixPlan, $srcQueueLog)) {
  $txByFile[$src] = Read-RepoText $src
}
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
if ($kindMembers -ccontains 'Reject') { $rxKindReject = [regex]::Escape([string]($kindMembers -ceq 'Reject' | Select-Object -First 1)) }
$blockValues = @([regex]::Matches($txByFile[$srcBlockCodes].Text, '(?m)^\s*public const string \w+ = "([A-Za-z][A-Za-z0-9_]*)";') |
  ForEach-Object { [regex]::Escape($_.Groups[1].Value) })
# 直接编译发布给调用者的模板而非抄一份正则；只接受源码中确认为 exit 91、零交付的模板。
$matrixTemplateRx = @(foreach ($symbol in @('OutMatrixReportRejected', 'OutMatrixReportConflict')) {
  $m = [regex]::Match($txByFile[$srcMatrixPlan].Text, ('public const string ' + $symbol + ' = "(\[color-out-matrix\] [^"\r\n]+ => contract rejection, exit 91, no output)";'))
  if ($m.Success) { '^' + ([regex]::Escape($m.Groups[1].Value) -replace '\\\{[0-9]+\\?\}', '[^\r\n]+?') + '$' }
})
$script:RejectForms = @(
  [pscustomobject]@{ Id = 'F1-裁决=Reject'; Code = 91; Enabled = ($rxKindReject -ne '')
    Rx = '^\[色彩裁决\] 裁决=' + $rxKindReject + '$'
    Audits = @(@{ File = $srcVerdictKind; Lit = 'public enum VerdictKind' }, @{ File = $srcVerdictLog; Lit = '[色彩裁决] 裁决={plan.Verdict?.Kind}' }, @{ File = $srcVerdictLog; Lit = '[色彩裁决] 裁决={legacyVerdict.Kind}' }) },
  [pscustomobject]@{ Id = 'F2-显式输出矩阵拒绝'; Code = 91; Enabled = ($matrixTemplateRx.Count -eq 2)
    Rx = $matrixTemplateRx -join '|'
    Audits = @(@{ File = $srcVerdictLog; Lit = 'OutMatrixCapability.Rejected)' }) },
  [pscustomobject]@{ Id = 'F3-传统侧契约拒绝'; Code = 91; Enabled = $true
    Rx = '^\[色彩裁决\] ❌ 契约拒绝（不出产物）：[^\r\n]+$'
    Audits = @(@{ File = $srcVerdictLog; Lit = '[色彩裁决] ❌ 契约拒绝（不出产物）：' }) },
  [pscustomobject]@{ Id = 'F4-cjxl-legacy旁路拒绝'; Code = 91; Enabled = ($rxKindReject -ne '')
    Rx = '^\[色彩裁决\] 通路=cjxl-legacy [^\r\n]+ reject=' + $rxKindReject + '$'
    Audits = @(@{ File = $srcVerdictLog; Lit = '通路=cjxl-legacy {jxlDecision.Short()}' }, @{ File = $srcVerdictLog; Lit = ' reject={(jxlObsVerdict' }) },
  [pscustomobject]@{ Id = 'F5-显式引擎路由拦截'; Code = 90; Enabled = ($blockValues.Count -gt 0)
    Rx = '^\[色彩引擎\] ❌ 按要求应走引擎，但本次不可走（(?:' + ($blockValues -join '|') + ')）：[^\r\n]+$'
    Audits = @(@{ File = $srcVerdictLog; Lit = '按要求应走引擎，但本次不可走（{block.Code}）' }, @{ File = $srcBlockCodes; Lit = 'public static class EngineBlockCodes' }) }
)
# 核对输出载体；源码漂移不可被「刚好没有无产物格」掩盖。
$carrierBad = @()
if ($kindParseErr -ne '') { $carrierBad += ('VerdictKind 解析：' + $kindParseErr) }
$queueAudits = @('"item_completed"', '"item_log"', 'JsonPropertyName("c")', 'JsonPropertyName("m")', 'JsonPropertyName("code")', 'exitCode: item.ExitCode') |
  ForEach-Object { @{ File = $srcQueueLog; Lit = $_ } }
foreach ($f in @($script:RejectForms) + @(@{ Id = 'JsonLines 终态'; Enabled = $true; Audits = $queueAudits })) {
  if (-not $f.Enabled) { $carrierBad += ($f.Id + '：未能从产品源码解析稳定符号或模板 ⇒ 该形态停用') }
  foreach ($a in $f.Audits) {
    $tx = $txByFile[$a.File]
    if (-not $tx.Ok) { $carrierBad += ($f.Id + '：' + $tx.Error) }
    elseif ($tx.Text.IndexOf($a.Lit, [System.StringComparison]::Ordinal) -lt 0) {
      $carrierBad += ($f.Id + '：产品源码缺载体『' + $a.Lit + '』')
    }
  }
}
foreach ($x in ($carrierBad | Select-Object -First 8)) { Say "  载体? $x" }
$script:rejectTrusted = ($carrierBad.Count -eq 0)
CK ($script:rejectTrusted) "拒绝判据载体账：F1..F5 与 JsonLines 终态字段（漂移/读不到 = $($carrierBad.Count)；缺载体不判绿）"

function Get-StableRejection {
  param([string]$Log, [int]$Rc)
  if (-not $script:rejectTrusted) {
    return @{ Ok = $false; Form = ''; Detail = '拒绝判据的载体核对未过（见 6b 载体账）⇒ 本条负路径不作数，按红处理' }
  }
  if ($Rc -ne 1 -or [string]::IsNullOrWhiteSpace($Log)) {
    return @{ Ok = $false; Form = ''; Detail = "缺聚合失败 rc=1 或项级日志（实得 rc=$Rc）" }
  }
  $messages = @(); $completed = @()
  foreach ($line in ($Log -split '\r?\n')) {
    # CLI 启动/外部工具可能输出非 JSON，但不能提供项级证据；若 JSON 事件解析失败则不能放行。
    if ($line -cmatch '^\s*\{') {
      try { $evt = ConvertFrom-Json -InputObject $line -ErrorAction Stop }
      catch { return @{ Ok = $false; Form = ''; Detail = 'JsonLines 事件无法解析' } }
      if ($evt.c -ceq 'item_completed') { $completed += $evt }
      elseif ($evt.c -ceq 'item_log' -and $evt.m -is [string]) { $messages += $evt.m }
    }
    elseif ($line -cmatch '^\s*(?:Unhandled exception\b|错误:)') {
      return @{ Ok = $false; Form = ''; Detail = 'CLI 报出未处理异常或顶层执行错误，不能算拒绝' }
    }
  }
  if ($completed.Count -ne 1 -or $completed[0].code -cnotin @(90, 91) -or $completed[0].code -is [string]) {
    return @{ Ok = $false; Form = ''; Detail = "需要唯一 item_completed 且 code 为 90/91（读取 $($completed.Count) 项；code=$($completed.code -join ',')）" }
  }
  $code = $completed[0].code
  foreach ($f in $script:RejectForms) {
    if ($f.Enabled -and $f.Code -eq $code -and ($messages -cmatch $f.Rx)) {
      return @{ Ok = $true; Form = $f.Id; Detail = "干净拒绝：$($f.Id)（rc=$Rc，项级 code=$code）" }
    }
  }
  return @{ Ok = $false; Form = ''; Detail = "项级 code=$code 无对应稳定拒绝载体 ⇒ 不能算干净拒绝" }
}
# 不启动产品：合成事件必须过和实际格同一套 JsonLines 解码、终态与形态匹配。
$synRejectCases = @(
  @{ N='崩溃'; Rc=1; Raw='Unhandled exception. System.NullReferenceException'; Want=$false },
  @{ N='规划拒绝后执行失败'; Code=92; L='[色彩裁决] 裁决=Reject'; Want=$false },
  @{ N='只有人话的拒绝'; Code=91; L='这个目标被拒绝了'; Want=$false },
  @{ N='找不到拒绝裁决'; Code=91; L='[色彩裁决] 裁决=ProceedWithDegradation'; Want=$false },
  @{ N='有拒绝日志但聚合 rc=0'; Rc=0; Code=91; L='[色彩裁决] 裁决=Reject'; Want=$false },
  @{ N='空日志'; Want=$false },
  @{ N='cjxl 旁路 reject=null'; Code=91; L='[色彩裁决] 通路=cjxl-legacy 出口=bt2020/bt709 链数=0 reject=null'; Want=$false },
  @{ N='有拒绝日志但没有终态'; L='[色彩裁决] 裁决=Reject'; Want=$false },
  @{ N='有拒绝日志但崩溃'; Code=91; L='[色彩裁决] 裁决=Reject'; Raw='Unhandled exception. System.NullReferenceException'; Want=$false },
  @{ N='未知路由码'; Code=90; L='[色彩引擎] ❌ 按要求应走引擎，但本次不可走（UnknownCode）：原因'; Want=$false },
  @{ N='路由码与项级契约拒绝码冲突'; Code=91; L='[色彩引擎] ❌ 按要求应走引擎，但本次不可走（FormatNotSupported）：原因'; Want=$false },
  @{ N='F1 两路共有机读裁决'; Code=91; L='[色彩裁决] 裁决=Reject'; Want=$true; Form='F1-裁决=Reject' },
  @{ N='F2 输出矩阵拒绝模板 3'; Code=91; L='[color-out-matrix] bt709 is not carried by png (RGB-native/palette container has no YUV matrix concept) => contract rejection, exit 91, no output'; Want=$true; Form='F2-显式输出矩阵拒绝' },
  @{ N='F2 输出矩阵拒绝模板 4'; Code=91; L='[color-out-matrix] bt709 conflicts with derived matrix bt2020nc for avif output => contract rejection, exit 91, no output'; Want=$true; Form='F2-显式输出矩阵拒绝' },
  @{ N='F3 传统/分派级拒绝（无裁决行）'; Code=91; L='[色彩裁决] ❌ 契约拒绝（不出产物）：增益图（GainMap）不适用于 png'; Want=$true; Form='F3-传统侧契约拒绝' },
  @{ N='F4 cjxl 直编旁路'; Code=91; L='[色彩裁决] 通路=cjxl-legacy 出口=bt2020/bt709 链数=0 reject=Reject'; Want=$true; Form='F4-cjxl-legacy旁路拒绝' },
  @{ N='F5 显式引擎路由拦截'; Code=90; L='[色彩引擎] ❌ 按要求应走引擎，但本次不可走（FormatNotSupported）：出口容器不在引擎编码白名单内'; Want=$true; Form='F5-显式引擎路由拦截' }
)
$synRejectBad = @()
foreach ($c in $synRejectCases) {
  $log = @(
    if ($c.L) { @{ c = 'item_log'; m = $c.L } | ConvertTo-Json -Compress }
    if ($c.ContainsKey('Code')) { @{ c = 'item_completed'; code = $c.Code } | ConvertTo-Json -Compress }
    if ($c.Raw) { $c.Raw }
  ) -join "`n"
  $rc = if ($c.ContainsKey('Rc')) { $c.Rc } else { 1 }
  $r = Get-StableRejection -Log $log -Rc $rc
  $shapeOk = ($r.Ok -eq $c.Want)
  if ($shapeOk -and $c.Form) { $shapeOk = ($r.Form -ceq $c.Form) }
  if (-not $shapeOk) {
    $synRejectBad += ('{0} ⇒ 判据实得 Ok={1}/Form={2}，期望 Ok={3}/Form={4}（依据：{5}）' -f $c.N, $r.Ok,
      $(if ($r.Form) { $r.Form } else { '—' }), $c.Want, $(if ($c.Form) { $c.Form } else { '—' }), $r.Detail)
  }
}
foreach ($x in $synRejectBad) { Say "  拒绝自测? $x" }
CK ($synRejectBad.Count -eq 0) "拒绝判据自测：$($synRejectCases.Count) 组 JsonLines（含 F1..F5 正控与缺终态、错码、崩溃负控）分判对 $($synRejectCases.Count - $synRejectBad.Count) 组"

# ── 6c) LEGACY 通路层（任务 #49，2026-09-27）───────────────────────────────────────
# 为什么必须补这一段：§2 那 360 格**每格都写死 `--color-engine engine`**，而 `$fmts` 里连 jxr 都没有
#   ⇒ #43/#44 那一族「legacy 旁路吃掉用户目标」的缺陷，全层网格**一格都看不见**（360 格全绿、缺陷在场）。
#   补的首先是**通路**覆盖（两条执行路必须被同一把尺子量），格式轴只是给那把尺子多两处能落地的地方：
#   容器面已由 {jxl,jxr}=24 格扩到 {jxl,jxr,png,tiff}=48 格（任务 #50 缺口②，扩的理由见下面 6c 中段）。
# ⚠ 像素判据**只用 SDR 源**、HDR 源不跨路比像素：HDR→SDR 两路的**色调映射契约本就不同**
#   （legacy=ffmpeg `tonemap=mobius`、不发 intensity_target；engine=进程内内核按 203nit 白
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
          $r = ExecCli $exe ("--headless --log-level Debug --log-format JsonLines -i `"$($srcs[$sk])`" -o `"$co`" -f $f --bit-depth 8 --color-space `"$tn`" --color-engine $eng") -Tee "$co.log"
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
            # ── (A) 负路径：无产物须同时满足 §6b 的项级终态、对应拒绝载体与进程失败 ──
            #   例如 JXR 的 engine 规划可先给 Reject 再回传统链路；只有最后的项级 91 才确认未交付是契约拒绝。
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
            # 2026-11-01：改用与 §3 LBL 共享的词形表（本文件 :223 的口径——「词形表一分家就各自漂移」）。
            #   旧写法拿 $legacyExpect 的旧字面量（'sRGB primaries' 等）做 Escape 精确匹配，jxlinfo 新
            #   输出的词形（'sRGB'/'P3'/'Rec.2100'/'709'）一个都撞不上 ⇒ 6 格假红（re2 轮实测）。
            $okL = ("$($cm.Primaries)" -match (Get-TargetPrimariesRx $tn)) -and ("$($cm.Transfer)" -match (Get-TargetTransferRx $tn))
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
          # 两端都绕开 zimg 的 bt709 纯 γ2.4：入口先用 DNG 常数反解到线性，出口再用定义式 OETF。
          # 旧参照只拆出口、入口仍 tin=bt709，会与 legacy 产品同错互证，并把已按 ICC 定义式转换的 engine 误判为红。
          # 常数与 ServiceProbe/54_ProbeXcheck.cs 的独立参照一致，不读产品侧曲线代码。
          $defOetf = "floor(maxval*if(lt(val/maxval\,0.0180539685108078)\,4.5*val/maxval\,1.0992968268094429*pow(val/maxval\,0.45)-0.0992968268094429)+0.5)"
          $defInv = "maxval*if(lt(val/maxval\,0.0812428582986351)\,(val/maxval)/4.5\,pow(((val/maxval)+0.0992968268094429)/1.0992968268094429\,1/0.45))"
          $srcNeedsDef = $srcTrc -eq 'bt709'
          $dstNeedsDef = $dtTrc -eq 'bt709'
          $refPre = if ($srcNeedsDef) { "format=rgb48le,lutrgb=r='$defInv':g='$defInv':b='$defInv',format=gbrpf32le," } else { '' }
          $refTin = if ($srcNeedsDef) { 'linear' } else { $srcTrc }
          $refTout = if ($dstNeedsDef) { 'linear' } else { $dtTrc }
          $refTail = ',format=rgb48le' + $(if ($dstNeedsDef) { ",lutrgb=r='$defOetf':g='$defOetf':b='$defOetf'" } else { '' })
          ExecCli $ff ("-hide_banner -loglevel error -y -i `"$($srcs[$sk])`" -vf `"${refPre}zscale=pin=${srcTok}:tin=${refTin}:min=gbr:p=$($targets[$tn]):t=${refTout}:m=gbr${refTail}`" -frames:v 1 `"$refT`"") | Out-Null
        }
        # ⚠ 2026-11-24：目标/源 TRC 为 bt709 时，上方参照已改用 t=linear + BT.709-6 定义式 OETF；
        #   因而 legacy 与 engine 都必须接受同一把方向性像素尺，不再让 engine 走 lgCurveSkip。
        #   这不是抬阈值：仍要求离目标参照至少比离源原样远 4 dB，目标侧绝对下限仍为 26 dB。
        $sameGamut = ($sk -eq 'p38' -and $tn -eq 'Display P3') -or ($sk -eq 'bt20208' -and $tn -eq 'BT.2020')
        foreach ($eng in @('legacy', 'engine')) {
          $m = $made[$eng]
          if (-not $m.Prod) { continue }
          $dp = LgProdPng $m.Prod ("{0}_{1}_{2}_{3}" -f $sk, $f, ($tn -replace '[^A-Za-z0-9]', ''), $eng)
          if (-not $dp) { $lgBad.Add("$($sk)_$($tn)_$f（$eng）：产物解码件取不到 ⇒ 像素账未跑（不许当过）"); continue }
          if ($sameGamut) {
            # 2026-11-01：恒等格（源域==目标域）的像素真值 = 源本身。对这类格拿「转到目标」的参照
            #   （无论纯 γ 还是定义式）量的都是**夹具编码口径 vs 参照口径**的差（纯 γ 夹具 vs 定义式
            #   参照 = 23.3 dB，双向对撞同值）—— 不是产品的转换质量 ⇒ (B) 对它们没有判别力。
            #   改成按容器钉「恒等请求不得乱动像素」，阈值 = 各容器恒等观测最低值 - ≥5 dB 余量：
            #     png  legacy=inf（逐字节）/ engine ≥40 ⇒ 40；tiff legacy=31.2（出口 8bit 有序抖动，
            #     播报在案）/ jxl legacy=35.0~37.6（cjxl -d 3.8 有损，:858-860 自记恒等 39.87）/
            #     jxr ≥40 ⇒ 其余容器 26（都远高于「恒等被真转换」的 ~20 dB 特征）。
            $identBar = if ($f -eq 'png') { 40.0 } else { 26.0 }
            $pS = LgPsnr $srcs[$sk] $dp
            if (-not $pS.Ok) { $lgBad.Add("$($sk)_$($tn)_$f（$eng）：恒等格 PSNR 取数失败 ⇒ 不判绿"); continue }
            if ([double]$pS.Psnr -lt $identBar) { $lgBad.Add("$($sk)_$($tn)_$f（$eng）：恒等格离源只有 $([math]::Round($pS.Psnr,2)) dB（<$identBar ⇒ 恒等请求被乱动了像素）") }
            continue
          }
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
