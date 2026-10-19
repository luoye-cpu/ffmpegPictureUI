# verify-gainmap-isobmff.ps1 —— AVIF/HEIC（ISO-BMFF）增益图的「识别 + 解码」专项门禁（2026-09-20 新增）
#
# 缺陷（本门禁要锁住的东西，全部有实测证据）：
#   ① `GainMapDecoder` 原先只认 **JPEG 容器**（SOI / APP2 ISO 21496-1 / MPF）⇒ AVIF/HEIC 的增益图
#      **完全看不见** ⇒ 增益图被**静默丢弃、无任何点名**（与 `--jpeg-gain-map` 配非 JPEG 目标同族）。
#   ② `IsAnimatedAvifAsync` 的旧判据是 **`videoCount >= 2`**，而**静态增益图 AVIF**（`Color`+`GMap`）与
#      **静态 AVIF + alpha**（`Color`+`Alpha`）**都是 2 条视频流、`nb_frames` 全为 1**（实测）
#      ⇒ 旧判据把两者**一律误判成动画**：色彩引擎被整体绕过（`AnimatedInput`），
#      且 AVIF→GIF/WebP 会走两步法并把第二流当成 alpha。
#
# 修复（判据与载体）：
#   · 新增 `IsoBmffGainMapProbe`：解析 `meta` → `iinf`(item 类型) / `iref`(`dimg`) / `iloc`(extent) /
#     `iprp`(`auxC`)，识别 **`tmap` 派生项**并切出「tmap 载荷」与「增益图 item 字节」。
#   · `tmap` 载荷 = `u8 version` + ISO 21496-1 `GainMapMetadata`（**独立分母**形式，与 JPEG APP2 同构）
#     ⇒ 复用 `JpegContainer.ParseIso21496`，仅需跳过首字节。长度闭合 = 1 + 21 + 通道数×40（3 通道 = 142 B）。
#   · 增益图像素走「抽 item 字节 → `ffmpeg -f obu`（AV1）/ `-f hevc`（HEVC）」，底图显式 `-map 0:0 -frames:v 1`。
#   · 方向：ISO 21496-1 的 flags **没有**「底图是否 HDR」位 ⇒ 由 **headroom 比较**推出
#     （`base > alternate` ⇒ 底图已是 HDR）。反向文件**点名**并交回普通 HDR 路径（不使用增益图）。
#   · 动画判据改为「**是否真有流带多帧**」+「是否增益图容器」两级，见 `QueueProcessor.IsAnimatedAvifAsync`。
#
# ⚠⚠ **变异验证（本轮已做，登记在此；`§6` 第 4 条要求「每条新断言/新锁必须变异验证」）**：
#   · 变异 A（容器层）：把 `IsoBmffGainMapProbe` 的 `tmap` 判定改成恒不命中 ⇒
#     §1 的正向断言（`hasGainMap=True` / `pass=11`）与 §3 的「检测到 AVIF/HEIC 增益图」全部转红。
#   · 变异 B（动画判据）：把 `HasTmapItemFast(...)` 那一支改成恒 `false`（即退回「单帧多流 ⇒ 动画」）⇒
#     §3 的「增益图 AVIF → webp **不得**出现两步法」转红（增益图 AVIF 会重新被判动画）。
#   · 变异 C（载荷偏移）：把 `ParseIso21496(payload, 1, len-1)` 的起点改回 `0` ⇒
#     §1 的「载荷长度闭合 1+21+ch×40」与字段值断言转红。
#   · 变异 D（缺陷 C 的 CICP 优先级）：把 `FfmpegCommandBuilder` 里的 `ffCicpUsable` 判据去掉
#     （退回「EXIF ColorSpace 无条件生效」）⇒ §3b 的正向断言与「HDR 底 ⇒ 产出 ≥1」转红。
#   · 变异 E（HEVC 不转换、直给 `-f hevc`）：把 `GainMapFfmpegFormat` 映射改回
#     `"hvc1" or "hev1" => "hevc"` 且**不做** Annex-B 转换 ⇒ §1b 的「产物首 4 字节 = 起始码」与
#     「端到端解出 64x48」转红（ffmpeg 报 `No start code is found`）。
#   · 变异 F（HEVC 分支整体退回 fail-closed，**实测 `PASS=42 FAIL=5`**）：把
#     `else if (gty is "hvc1" or "hev1")` 改成永不命中的条件 ⇒ §1b 的「转换成功」「产物是 Annex-B」
#     「端到端」以及两条**点名内容**断言（负控①/② 分别要求点名含 `hvcC` / `length-prefix chain broken`）
#     全部转红 ⇒ 证明这 5 条**不是恒真**，也证明本段新代码**是必要的**。
#   · 变异 G（转换函数原样透传，**实测 `PASS=43 FAIL=4`**）：把 `var annexB = ToAnnexB(...)` 改成
#     `annexB = raw`（不转换，直接返回长度前缀字节）⇒ 「产物是 Annex-B」「端到端」与**负控②的两条**
#     转红（负控② 反而变成 `determinate=True`）⇒ 证明「严格闭合」与「起始码」判据有牙。
#     ⚠ 变异 G 下「转换成功」那条**仍然 PASS**（`determinate=True` / `gmData=2898 > 0`）——
#       这正是文件头说它是**弱断言**的实测依据。
#   · 变异 H（HEVC 分支退回 fail-closed，**实测 `PASS=45 FAIL=7`**）：改法与变异 F 同，但在 **§3c 已存在**
#     的前提下重跑 ⇒ 除 §1b 那 5 条外，**§3c 的「真被解码并应用」与「点名 `hvc1`」也转红**
#     ⇒ 证明 §3c 这两条**有牙**。⚠ 同时 §3c 的「识别并产出」**仍然 PASS** ⇒ 它是**弱断言**（已登记）。
#   §1 / §1b / §2 / §2b 各自还带**可执行的**反控（见下），证明断言**有牙**而不是恒真。
#
# ⚠⚠ **本脚本自身踩过的一个「静默吞代码」陷阱（2026-09-20 实测，值得全仓警惕）**：
#   §3b 的消息串原先写作「反引号包裹的 `Not yet implemented`」—— **尾反引号紧贴字符串结束引号**
#   ⇒ PowerShell 把「反引号 + 双引号」解析成**字面双引号** ⇒ 字符串**不闭合**，把紧随其后的
#   `# ── §4 收尾 ──` 注释、`Write-Host` 标题、**以及整段 §3c** 全部**吞成字符串内容** ⇒ 那些代码**静默不执行**。
#   **症状**：① `PASS` 总数比文件头声明少；② **「注释行」竟然出现在脚本输出里**；③ `§4` 的标题从未打印。
#   ⚠ **它不是语法错误** ⇒ `Parser.ParseFile` 与 `verify-ps-compat` **都抓不到**（解析器认为语法合法）。
#   **检测法（本次用的）**：用 AST 取所有**字符串 token 的行范围**，检查是否有「以 `#` 开头的源码行」落在其中
#   （已全仓扫过：48 个脚本里**只有本文件**是真实例，另两处命中是 here-string 里的 OpenCL / ExtendScript 代码，属误报）。
#   **纪律**：双引号串里**不要**用反引号包裹代码片段（裸写或用「」）；**尾反引号尤其不得紧贴引号**。
#
# ⚠⚠ **已登记的覆盖缺口（措辞不得大于扫描面）**：
#   · **真实 HEIC 增益图素材仍然缺失** ⇒ HEVC（`hvc1`）解码路径是用**合成件**覆盖的：
#     `tests/data/synth_heic_gainmap.heic`（生成器 `tests/scripts/_make-synth-heic-gainmap.py` ——
#     ffmpeg/libx265 编一段真实 HEVC 码流 + 自构 `hvcC` + 借库内 AVIF 样本的 tmap 载荷）。
#     ⇒ 覆盖的是「**解码路径**」（识别 → `hvcC` → 长度前缀转 Annex-B → ffmpeg 解出像素），
#     **不代表**拿到了真实 HEIC 增益图素材，也**不代表**真实设备产物的元数据语义被验证过。
#   · **HEIC/AVIF 共用同一条 ISO-BMFF 识别路径**（按 item 类型判定，不看扩展名）；
#     两条解码路径现在**都实测过**：`av01` → `-f obu`（§1）· `hvc1`/`hev1` → `hvcC` + Annex-B 转换（§1b）。
#   · ⚠ **素材搜寻已试过并失败（2026-09-20，记下来省得重复找）**：
#       - `imaging.org` 的 `ISO_21496-1_Adaptive_HDR` 测试集（最权威）⇒ 直链 **403**，需注册 IS&T 账号；
#       - `github.com/chemharuka/toGainMapHDR` 的 `sample/DJI_*.heic`（3 个）⇒ 实测
#         `hasGainMap=False`（是**普通 HLG HDR HEIC，无 `tmap`**），**不是**增益图 HEIC；
#       - `Awesome-Gain-Maps` 仓库只收录 **JPEG**（Ultra HDR）样本，无 HEIC/AVIF 增益图。
#     ⇒ 下一步若要真素材，优先走 **注册 imaging.org 免费下载**（CC BY-NC 4.0）或**用手机拍一张
#       Adaptive HDR 照片**（iPhone 15+ / 部分 Android）。
#
# 断言（共 70 条）：§0 前置 6 · §1 容器层 8 · §1b HEVC 解码路径 11（含 2 条负控 + 1 条反控）·
#                   §2 源码形态 6（含反控）· §2b 缺陷 C 源码形态 3（含反控）· §3 行为 8 ·
#                   §3b 缺陷 C 行为 4（含负控与素材形态自检）· **§3c HEIC 端到端行为 5（含负控）** ·
#                   **§3d 厂商尾注执行面 4（含拒因与互斥点名）** · **§3e 底图取法 A/B 4（含对照臂分叉）** · **§3f HEIC→BT.2020/PQ 出口 10（三读者 + 码域分叉 + 2 条对照）** · §4 收尾 1
#   ⚠ **两条弱断言**（都实测过「变异后仍然通过」，别只信它们）：
#     · §1b「转换成功」：只看 `determinate` / `ffmpegFmt=hevc` / `gmData>0` —— **原样透传不转换**也能过；
#     · §3c「识别并产出」：只看「日志出现『检测到 ISO-BMFF 增益图』」+ 有产物 —— **退回 fail-closed** 也能过。
#     ⇒ 真正的判据分别是它们后面那两条：§1b = **产物首 4 字节 = Annex-B 起始码** + **ffprobe 解出 64x48**；
#       §3c = **「已解码为线性 HDR PFM」** + **点名到 `hvc1` 且字节数为 Annex-B**。三条一起看。
#
# ⚠⚠ **§3c 的定位**：§1b 只验到「**探针层**」（识别 + 转换 + 产物可解），**不证明**这条路径接进了
#   `GainMapDecoder`。§3c 补上端到端（元数据 → HEVC item 解码 → 增益公式 → 产出）。§3 对 AVIF 有同类断言，
#   HEIC 侧此前**空白**。⚠ 本件的底图与增益图用的是**同一份** HEVC 码流（合成件）⇒ §3c 断言的是
#   **路径被走通**（含「增益真被应用」这一事件），**不是**图像语义正确性。
#
# 前置依赖（缺失 ⇒ **fail-closed 判红**，不静默跳过）：
#   · 库内样本 `tools/src/libavif/tests/data/seine_{sdr,hdr}_gainmap_srgb.avif`（第三方源码树自带，稳定）
#   · `tools/src/libavif/build/Release/avifenc.exe`（造「静态 AVIF+alpha」负控；形状与增益图**完全相同**，
#     是本门禁最关键的一条负控 —— 缺它则「没误伤 alpha」这句话无凭据）
#
# ⚠ 退出码：0 = 全绿，1 = 有失败。结尾必须是**完整 if/else**（§6 第 67 条：清理步会污染 `$LASTEXITCODE`）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（用 「」）。
# ⚠⚠ 杀树禁用 `$p.Kill($true)`（PS5.1 的 .NET Framework 无该重载）⇒ 用下面的 `KillTree`（先子后父）。
# ⚠⚠ 退出码禁用轮询式 `.ExitCode`（PS5.1 下 `Start-Process -PassThru` 不带 `-Wait` 时恒为 `$null`，
#   而 `[int]$null == 0` ⇒ 强转会把「读不到」伪装成 exit 0 ⇒ 假绿）⇒ 判据一律用「产物数 + 日志形状」。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$sp  = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp  = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$avifenc = "$root/tools/src/libavif/build/Release/avifenc.exe"
$libData = "$root/tools/src/libavif/tests/data"
$gmSdr = "$libData/seine_sdr_gainmap_srgb.avif"
$gmHdr = "$libData/seine_hdr_gainmap_srgb.avif"

# ⚠ 运行级隔离（GUID）：固定 `$work` 会被并发的同名实例互删 ⇒ 假红（§6 第 66 条）。
$val  = "$root/tests/output/validate"
$gid  = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = "$val/gmisobmff_${gid}"
New-Item -ItemType Directory -Force -Path $work | Out-Null

$appLimitSec = 90
$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Host "PASS $msg" -ForegroundColor Green }
  else     { $script:fail++; Write-Host "FAIL $msg" -ForegroundColor Red }
}

# 取子进程输出一律走 Start-Process + 重定向（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）。
function Exec([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  $t = "";  if (Test-Path $o) { $t  = [System.IO.File]::ReadAllText($o) }
  $t2 = ""; if (Test-Path $e) { $t2 = [System.IO.File]::ReadAllText($e) }
  return @{ code = $p.ExitCode; text = ($t + "`n" + $t2) }
}

# ⚠⚠ **只读 stdout 的取数（读「工具写在 stdout 的值」时唯一合法的尺）**
#   上面 `Exec` 把 stderr 拼进 `text` —— 只对**实测只在 stderr** 的东西是对的：ffmpeg 的 PSNR 行、`frame=`、
#   signalstats 的 `YMIN=`（实测 `-hide_banner -i x -vf signalstats,metadata=print… -f null -` 的
#   stdout = **0 字节**、`YMIN=30` 只在 stderr ⇒ 这类判据吃的是 stderr 的**告警全集**，换成 `ExecOut`
#   会读到空 ⇒ 恒真（**不许换**）。⚠ 合并的代价也要写清（2026-09-27 实测）：本机 `LANG`/`LC_ALL` 不被识别时
#   ffmpeg/ffprobe 会往 stderr 吐 locale 警告（实测 26 B），ffprobe **不带 `-v error` 时**还会吐整段版本
#   横幅（实测 4838 B）⇒ 拿合并结果做 `-notmatch`（"不许出现 X"）的判据在污染环境下会变脆（有噪声时
#   "不含 X"可能只是没读到）；这一类属**改判据**范畴（归一化剥噪声 / 按字段取值），本轮只登记、不动。
#   ⚠⚠ **本文件原先「产品/探针日志只在 stderr」的说法已被实测否证，别再引用**：
#   · ServiceProbe 的读数**全在 stdout**（主循环 2026-09-27 实测 `decision` 模式：stdout 6184 B、stderr **0 B**，
#     `PROBE RESULT:` / `pass=` / `ColorMatrix1=` 全在 stdout）⇒ 本文件 §1/§1b/§2b 那些
#     `Exec $sp "gainmap-isobmff …"` / `Exec $sp "color …"` 取的是 stdout 的值，**同样该走 `ExecOut`**
#     —— 只是它们没被结构锁点名（锁的变量名单里没有 `$sp`），**不在本轮分诊清单**，留作下一步（换之前按实测确认）。
#   · 本文件的产品日志走 `RunApp`，它本来就是「先读 stdout、空了才读 stderr」⇒ 也不保证在 stderr。
#   ⚠ "值在哪个流"要**逐条实测**，别照抄直觉：exiftool 的 ICC 诊断在**全量 dump**（`-a -G1 -s` 不带标签名）时
#   两边都打（实测 stdout 1678 B 里就有 `[ExifTool] Warning : Bad ICC_Profile table…`），而**指定标签名**
#   （`-G -s -ColorSpace` / `-ProfileDescription`）时 stdout **只有值**、诊断只在 stderr。
#   本轮改的站点读的都是**值**（ffprobe `-of csv` 字段、exiftool 指定标签）⇒ 一律走 `ExecOut`，因为随包
#   `publish/PLAN/exiftool/exiftool.exe` 是 **perl 打包版**：进程环境带着本机 perl 不认的
#   `LC_ALL`/`LANG`（从 MSYS/bash 侧起门禁就是 `C.UTF-8`）时，它**每次调用往 stderr 喷 245 B locale warning**
#   ⇒ 合并版"取到的值"会混进警告文本（实测同一条 `-G -s -ColorSpace`：OLD 301 B / NEW 56 B），两种坏法都真实发生过：
#     · **假绿**：负断言被撑成恒真（`'' -notmatch 'x'` 通过）、两侧同污时 `-ne` 变成两条警告互比；
#     · **假红**：值前面多一行警告，归一化剥不干净 ⇒ 两格判成不同。
#   ⇒ 本函数**只**回 stdout（形状与 `Exec` 一致，调用点只需换函数名）；stderr 照样落盘供排查，只是不进返回值。
function ExecOut([string]$file, [string]$argStr, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  if (-not (Test-Path $o)) { return @{ code = $p.ExitCode; text = "" } }
  $t = [System.IO.File]::ReadAllText($o)
  return @{ code = $p.ExitCode; text = $t }
}

# ⚠ 共享读：重定向目标在子进程存活期间被独占 ⇒ 直接 ReadAllText 会抛「being used by another process」。
function ReadShared([string]$path) {
  if (-not (Test-Path $path)) { return "" }
  try {
    $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Open, `
          [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
    $t = $sr.ReadToEnd(); $sr.Close(); $fs.Close(); return $t
  } catch { return "" }
}

# ⚠⚠ 杀**进程树**：**不要**用 `$p.Kill($true)`（该重载在 Windows PowerShell 5.1 上不存在）。
#   ⚠ 形参名**不能**叫 `$pid`（PowerShell 只读自动变量）。
function KillTree([int]$rootPid) {
  $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
  foreach ($q in $all) { if ($q.ParentProcessId -eq $rootPid) { KillTree ([int]$q.ProcessId) } }
  $pr = $null
  try { $pr = Get-Process -Id $rootPid -ErrorAction SilentlyContinue } catch { $pr = $null }
  if ($pr) { try { $pr.Kill() } catch { } }
}
$script:spawned = @()

# 单条用例：起应用（headless + debug 日志）。判据一律用「产物数 + 日志形状」，不用退出码。
function RunApp([string]$inPath, [string]$fmt, [string]$tag, [string[]]$appExtra = @()) {
  $outdir = "$work/out_$tag"
  $so = "$work/$tag.app.out"; $se = "$work/$tag.app.err"
  $p = Start-Process -FilePath $exe `
       -ArgumentList ((@("--headless", "--log-level", "debug", "-i", $inPath, "-o", $outdir, "--format", $fmt) + $appExtra)) `
       -PassThru -NoNewWindow -RedirectStandardOutput $so -RedirectStandardError $se
  $script:spawned += [int]$p.Id
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $appLimitSec) { Start-Sleep -Milliseconds 300 }
  $sw.Stop()
  $terminated = $p.HasExited
  if (-not $terminated) { KillTree ([int]$p.Id); Start-Sleep -Milliseconds 600 }
  $text = ReadShared $so
  if ($text.Length -eq 0) { $text = ReadShared $se }
  $prods = 0
  if (Test-Path $outdir) { $prods = @(Get-ChildItem -LiteralPath $outdir -File -Recurse -ErrorAction SilentlyContinue).Count }
  return @{ terminated = $terminated; elapsed = $sw.Elapsed.TotalSeconds; text = $text; prods = $prods; dir = $outdir }
}

# ── §0 前置自检 ─────────────────────────────────────────────────────────────────
Write-Host "`n### §0 前置自检（缺任一 ⇒ 后续断言无凭据，一律 fail-closed）###" -ForegroundColor Cyan
foreach ($need in @($exe, $sp, $ff, $fp)) {
  if (-not (Test-Path $need)) { Write-Host "缺工具：$need" -ForegroundColor Red; exit 1 }
}
$exeMtimeBefore = (Get-Item $exe).LastWriteTimeUtc
Write-Host "被测 exe: $exe"
Write-Host "exe mtime(UTC) = $exeMtimeBefore"
Write-Host "运行级目录: $work"

CK ((Test-Path $gmSdr) -and (Test-Path $gmHdr)) `
   "§0 库内增益图样本齐备（seine_sdr_gainmap_srgb.avif / seine_hdr_gainmap_srgb.avif）—— 缺则本门禁整段无凭据"
CK (Test-Path $avifenc) `
   "§0 avifenc 可用（$avifenc）—— 用来造「静态 AVIF+alpha」负控；缺它则「没误伤 alpha」这句话无凭据（fail-closed，不静默跳过）"

# 负控素材：静态 AVIF / 动画 AVIF（ffmpeg 造）
$plain = "$work/plain.avif"; $anim = "$work/anim.avif"; $alphaPng = "$work/a.png"; $alphaAvif = "$work/alpha.avif"
$r = Exec $ff "-y -v error -f lavfi -i `"testsrc2=s=64x48`" -frames:v 1 -c:v libaom-av1 -crf 40 -strict experimental -pix_fmt yuv420p `"$plain`"" "gen_plain"
$r = Exec $ff "-y -v error -f lavfi -i `"testsrc2=s=64x48`" -frames:v 8 -c:v libaom-av1 -crf 40 -strict experimental -pix_fmt yuv420p `"$anim`"" "gen_anim"
$r = Exec $ff "-y -v error -f lavfi -i `"color=c=black:s=64x48`" -f lavfi -i `"gradients=s=64x48:c0=black:c1=white`" -filter_complex `"[0:v][1:v]alphamerge`" -frames:v 1 `"$alphaPng`"" "gen_apng"
if (Test-Path $avifenc) { $r = Exec $avifenc "-q 60 `"$alphaPng`" `"$alphaAvif`"" "gen_aavif" }

# 形态自检：**这是本门禁的判据载体**（必须先钉住素材真实形态，否则后面的行为断言可能真空绿）
# ⚠ 这四条读 ffprobe `-of csv` 的**值** ⇒ 用只读 stdout 的 `ExecOut`。实测（2026-09-27，laneB 微实验，
#   `tests/output/laneB/equiv-laneB4.ps1`）：本机 ffprobe **带 `-v error` 时 stderr = 0 字节**（clean 与
#   LC_ALL/LANG=C.UTF-8 两种条件相同），csv 值全在 stdout（seine_sdr_gainmap_srgb.avif 读回
#   `0,video,1,` + `1,video,1,`）⇒ 合并版与只读版**同值**（只差合并版固定多塞的那个换行符），
#   因此这次换尺**不会**让任何格由绿转红；而不带 `-v error` 时 ffprobe 会往 stderr 吐 4838 B 横幅 ⇒
#   合并读法一旦有人日后去掉 `-v error` 就会把横幅端进"值"里，这正是要用 `ExecOut` 的理由。
#   ⚠ 另外三条 CK 都带**正向** `-match`（读不到就红），唯一那条 `-notmatch 1,video` 与正向锁同址。
$sp1 = ExecOut $fp "-v error -show_entries stream=index,codec_type,nb_frames -of csv=p=0 `"$plain`"" "sh_plain"
$sa1 = ExecOut $fp "-v error -show_entries stream=index,codec_type,nb_frames -of csv=p=0 `"$anim`"" "sh_anim"
$sg1 = ExecOut $fp "-v error -show_entries stream=index,codec_type,nb_frames -of csv=p=0 `"$gmSdr`"" "sh_gm"
$sx1 = ""
if (Test-Path $alphaAvif) { $sx1 = (ExecOut $fp "-v error -show_entries stream=index,codec_type,nb_frames -of csv=p=0 `"$alphaAvif`"" "sh_alpha").text }
Write-Host "  [形态] plain: $($sp1.text.Trim())"
Write-Host "  [形态] anim : $($sa1.text.Trim())"
Write-Host "  [形态] gmSdr: $($sg1.text.Trim())"
Write-Host "  [形态] alpha: $($sx1.Trim())"
CK (($sp1.text -match '(?m)^\s*0\s*,\s*video\s*,\s*1\s*,?\s*$') -and ($sp1.text -notmatch '(?m)^\s*1\s*,\s*video')) `
   "§0 静态 AVIF 恰好 1 条视频流（判据载体：单流 ⇒ 必然判静态）"
# ⚠ ffprobe 的 `-of csv=p=0` 在末字段后可能补一个**尾逗号**（实测 `0,video,1,`）⇒ 正则必须容忍 `,?`，
#   否则会因格式细节误判成「形态不符」而假红（本轮首跑正是这样红的）。
CK (($sa1.text -match '(?m)^\s*1\s*,\s*video\s*,\s*8\s*,?\s*$')) `
   "§0 动画 AVIF 的流 1 有 8 帧（判据载体：存在 nb_frames>1 的流 ⇒ 必然判动画）"
CK (($sg1.text -match '(?m)^\s*0\s*,\s*video\s*,\s*1\s*,?\s*$') -and ($sg1.text -match '(?m)^\s*1\s*,\s*video\s*,\s*1\s*,?\s*$')) `
   "§0 增益图 AVIF 是「2 条流且都 1 帧」——**与下面 alpha 负控形状完全相同**（这正是旧判据失效的根因）"
CK (($sx1 -match '(?m)^\s*1\s*,\s*video\s*,\s*1\s*,?\s*$')) `
   "§0 静态 AVIF+alpha 也是「2 条流且都 1 帧」⇒ 单靠 nb_frames **无法**区分增益图与 alpha（必须看容器 tmap）"

# ── §1 容器层：ServiceProbe gainmap-isobmff ─────────────────────────────────────
Write-Host "`n### §1 容器层：ISO-BMFF 增益图探测（tmap 项 + 载荷闭合 + 方向）###" -ForegroundColor Cyan
$cSdr = Exec $sp "gainmap-isobmff `"$gmSdr`"" "p_sdr"
$cHdr = Exec $sp "gainmap-isobmff `"$gmHdr`"" "p_hdr"
$cPlain = Exec $sp "gainmap-isobmff `"$plain`" none" "p_plain"
$cAnim = Exec $sp "gainmap-isobmff `"$anim`" none" "p_anim"
$cAlpha = Exec $sp "gainmap-isobmff `"$alphaAvif`" none" "p_alpha"
$cCtl = Exec $sp "gainmap-isobmff `"$plain`" gainmap" "p_ctl"

CK (($cSdr.text -match 'PROBE RESULT: pass=11 fail=0') -and ($cSdr.text -match 'hasGainMap=True') -and ($cSdr.text -match 'form=tmap')) `
   "§1 SDR 增益图 AVIF：识别为 tmap 形式（pass=11 fail=0）"
CK (($cSdr.text -match 'primary=1') -and ($cSdr.text -match 'tmap=2') -and ($cSdr.text -match 'gainmapItem=3') -and ($cSdr.text -match 'ffmpegFmt=obu') -and ($cSdr.text -match 'tmapPayload=142')) `
   "§1 SDR：item 定位正确（底图=1 / tmap=2 / 增益图=3，av01→`-f obu`，tmap 载荷 142 B）"
CK ($cSdr.text -match 'direction=forward') "§1 SDR：方向判为 forward（底图 SDR ⇒ 增益给 HDR）"
CK (($cHdr.text -match 'PROBE RESULT: pass=11 fail=0') -and ($cHdr.text -match 'direction=backward')) `
   "§1 HDR 增益图 AVIF：识别 + 方向判为 backward（底图已是 HDR）—— 方向由 headroom 比较推出"
CK (($cPlain.text -match 'PROBE RESULT: pass=3 fail=0') -and ($cPlain.text -match 'hasGainMap=False')) `
   "§1 负控：普通静态 AVIF ⇒ 无增益图"
CK (($cAnim.text -match 'PROBE RESULT: pass=3 fail=0') -and ($cAnim.text -match 'hasGainMap=False')) `
   "§1 负控：动画 AVIF ⇒ 无增益图（不把动画轨误当增益图）"
CK (($cAlpha.text -match 'PROBE RESULT: pass=3 fail=0') -and ($cAlpha.text -match 'hasGainMap=False')) `
   "§1 负控：静态 AVIF+alpha（**与增益图形状相同**）⇒ 仍判「无增益图」（判据看的是容器 tmap，不是流数）"
CK ($cCtl.text -match 'fail=[1-9]') `
   "§1 **反控（断言有牙）**：把普通 AVIF 按「期望有增益图」跑 ⇒ 必须判红（实 $($cCtl.text -replace '(?s).*PROBE RESULT:\s*','')）—— 证明上面 3 条负控不是恒真"

# ── §1b HEVC（`hvc1`）增益图 item：**hvcC 驱动的「长度前缀 → Annex-B」转换** ────────────────────
#   事实（2026-09-20 实测）：ISO-BMFF 里的 HEVC item 是**长度前缀 NAL**（参数集另存于 `hvcC`），
#   而 ffmpeg 的 `-f hevc` 只吃 **Annex-B**（起始码）⇒ 从真实 HEIC 抽出 `hvc1` item 直接喂 `-f hevc`
#   会 `No start code is found` / `could not find codec parameters`（实测 exit=2）。
#   AV1 不同：`av01` item 的 OBU 流自带结构，`-f obu` **可**直接解（§1 已实测）。
#   ⇒ 本段覆盖三件事：
#     ① **正向**：合成 HEIC 件的「识别 + 转换 + 产物真能被 ffmpeg 解」（含 `-f obu` 反控）；
#     ② **负控①**：item 类型改成 `hvc1` 但容器**没有 `hvcC`** ⇒ fail-closed；
#     ③ **负控②**：`hvcC` 在、但 item 字节是 **AV1** ⇒ 长度前缀**必断链** ⇒ fail-closed
#        —— 这条证明转换**不是无脑照搬**，而是逐 NAL 严格闭合。
#   ⚠ 素材是**合成件**（`tests/data/`，生成器 `tests/scripts/_make-synth-heic-gainmap.py`，可复现）：
#     真实 HEIC 增益图素材搜寻三条线全断（见文件头覆盖缺口登记）⇒ 本段覆盖的是**解码路径**，
#     **不代表**拿到了真实 HEIC 增益图素材。
function Replace-Bytes([byte[]]$src, [byte[]]$from, [byte[]]$to) {
  $out = New-Object System.Collections.Generic.List[byte]
  $i = 0
  while ($i -lt $src.Length) {
    $hit = $false
    if ($i + $from.Length -le $src.Length) {
      $hit = $true
      for ($k = 0; $k -lt $from.Length; $k++) { if ($src[$i + $k] -ne $from[$k]) { $hit = $false; break } }
    }
    if ($hit) { $out.AddRange($to); $i += $from.Length } else { $out.Add($src[$i]); $i++ }
  }
  return ,$out.ToArray()
}
$heicPos = "$root/tests/data/synth_heic_gainmap.heic"
$heicNeg = "$root/tests/data/synth_heic_gainmap_fakehevc.heic"
CK ((Test-Path $heicPos) -and (Test-Path $heicNeg)) `
   "§1b 前置：HEIC 合成件齐备（synth_heic_gainmap.heic / synth_heic_gainmap_fakehevc.heic）—— 缺则本段整段无凭据（fail-closed，不静默跳过）"

# 形态自检：正向件必须被 ffprobe 认成「2 条 hevc 流」（证明容器合法，且底图/增益图 item 都是**真实 HEVC**）
# ⚠ `ExecOut`：实测 ffprobe `-v error` 下 csv 两行全在 stdout（`0,hevc,64,48` + `1,hevc,64,48`）、stderr 0 B
#   ⇒ 与合并版同值，换尺不产生由绿转红；读不到容器时 stdout 空、报错在 stderr ⇒ 那条红是对的（正向断言）。
$hp = ExecOut $fp "-v error -show_entries stream=index,codec_name,width,height -of csv=p=0 `"$heicPos`"" "sh_heicpos"
Write-Host "  [形态] heicPos: $($hp.text.Trim())"
CK (($hp.text -match '(?m)^\s*0\s*,\s*hevc') -and ($hp.text -match '(?m)^\s*1\s*,\s*hevc')) `
   "§1b 素材形态：合成 HEIC 含 2 条 hevc 流（判据载体：增益图 item 确是真实 HEVC 码流，不是改标签的空壳）"

# ── §1b-① 正向：识别 + 转换 + 产物真能被 ffmpeg 解 ──
$cHeic = Exec $sp "gainmap-isobmff `"$heicPos`" `"$work/gm_pos.hevc`"" "p_heic"
CK (($cHeic.text -match 'hasGainMap=True') -and ($cHeic.text -match 'form=tmap') -and ($cHeic.text -match 'gainmapItem=3') -and ($cHeic.text -match 'codec=hvc1')) `
   "§1b 正向：合成 HEIC 识别为 tmap 增益图（识别只看 item 关系，与编解码类型无关）"
CK (($cHeic.text -match 'determinate=True') -and ($cHeic.text -match 'ffmpegFmt=hevc') -and ($cHeic.text -match 'gmData=[1-9][0-9]*')) `
   "§1b 正向：hvc1 增益图 item **转换成功** ⇒ 给出 `-f hevc` 且切出字节（实 $(($cHeic.text -split "`r?`n" | Where-Object { $_ -match '^codec=' } | Select-Object -First 1)))"
# 判据载体：产物必须以 **4 字节 Annex-B 起始码**开头。
# ⚠ 长度前缀形态**不可能**以 `00 00 00 01` 开头（那等于声称第一个 NAL 只有 1 字节）⇒ 该断言能真正区分两种形态。
$scOk = $false; $gmLen = -1
if (Test-Path "$work/gm_pos.hevc") {
  $gb = [System.IO.File]::ReadAllBytes("$work/gm_pos.hevc")
  $gmLen = $gb.Length
  $scOk = ($gb.Length -ge 4 -and $gb[0] -eq 0 -and $gb[1] -eq 0 -and $gb[2] -eq 0 -and $gb[3] -eq 1)
}
CK $scOk "§1b 正向：产物是 **Annex-B**（首 4 字节 = 00 00 00 01，实 $gmLen B）"
$dg = ExecOut $fp "-v error -f hevc -show_entries stream=codec_name,width,height -of csv=p=0 `"$work/gm_pos.hevc`"" "d_heic"
CK ($dg.text -match '(?m)^\s*hevc,64,48') `
   "§1b 正向 **端到端**：探针产出的字节被 ffprobe 解为 hevc 64x48（实 $($dg.text.Trim())）—— 参数集前置与逐 NAL 转换都对"
# 反控：把**同一份**产物按 AV1 解复用器（`-f obu`）解 ⇒ 不得成功（证明它真是 HEVC/Annex-B，而非被原样透传的 OBU）
$dg2 = ExecOut $fp "-v error -f obu -show_entries stream=codec_name,width,height -of csv=p=0 `"$work/gm_pos.hevc`"" "d_heic_obu"
# ⚠ 不能断言「输出不含 `av1`」—— `-f obu` 是**强制**解复用器，它照样会报 `codec_name=av1`，
#   只是**宽高为 0**（实测 `av1,0,0` + stderr `Failed to read obu`）。判据取「解不出 64x48」。
#   ⚠ 取数走 `ExecOut`（只看 stdout 的那行 csv）：`-f obu` 的报错在 stderr，拼进返回值只会给这条
#     **负断言**白送噪声；判据的**活性**由上一条正向断言（同一份产物、`-f hevc` 解出 64x48）承担。
CK (-not ($dg2.text -match ',\s*64\s*,\s*48')) `
   "§1b **反控（断言有牙）**：产物按 `-f obu`（AV1）解**解不出 64x48**（实 $(($dg2.text -split "`r?`n" | Where-Object { $_ -match 'av1' } | Select-Object -First 1))）—— 否则说明产物其实是 AV1 字节，上面两条正向断言就是假绿"

# ── §1b-② 负控①：类型改成 hvc1 但容器**没有 hvcC** ⇒ 必须 fail-closed ──
$synth = "$work/synth_hevc_gainmap.avif"
$srcBytes = [System.IO.File]::ReadAllBytes($gmSdr)
$patched = Replace-Bytes $srcBytes ([System.Text.Encoding]::ASCII.GetBytes('av01')) ([System.Text.Encoding]::ASCII.GetBytes('hvc1'))
[System.IO.File]::WriteAllBytes($synth, $patched)
$cSynth = Exec $sp "gainmap-isobmff `"$synth`"" "p_synth"
Write-Host "  负控①（改标签无 hvcC）: $(($cSynth.text -split "`r?`n" | Where-Object { $_ -match '^determinate=' } | Select-Object -First 1))"
CK (($cSynth.text -match 'hasGainMap=True') -and ($cSynth.text -match 'form=tmap') -and ($cSynth.text -match 'gainmapItem=3')) `
   "§1b 负控①：类型改 hvc1 后仍**识别**为 tmap 增益图（识别只看 item 关系）"
CK (($cSynth.text -match 'determinate=False') -and ($cSynth.text -match 'ffmpegFmt=-') -and ($cSynth.text -match 'gmData=-1') -and ($cSynth.text -match 'hvcC')) `
   "§1b 负控①：无 hvcC ⇒ **fail-closed + 点名**（不得给出 `-f hevc` 这个错命令，也不切字节）"

# ── §1b-③ 负控②：`hvcC` 在，但 item 字节是 **AV1** ⇒ 长度前缀必断链 ⇒ fail-closed ──
$cNeg = Exec $sp "gainmap-isobmff `"$heicNeg`"" "p_heicneg"
CK (($cNeg.text -match 'determinate=False') -and ($cNeg.text -match 'ffmpegFmt=-') -and ($cNeg.text -match 'gmData=-1')) `
   "§1b 负控②：hvcC 存在但 item 是 AV1 字节 ⇒ **仍** fail-closed（不切字节、不给解复用器）"
CK ($cNeg.text -match 'length-prefix chain broken') `
   "§1b 负控②：失败原因精确到「长度前缀断链」（实 $(($cNeg.text -split "`r?`n" | Where-Object { $_ -match '^determinate=' } | Select-Object -First 1))）—— **证明转换不是无脑照搬**"

# ── §2 源码形态：动画判据锁（含反控） ────────────────────────────────────────────
Write-Host "`n### §2 源码形态：动画判据（多帧 + tmap 两级）###" -ForegroundColor Cyan
$qpSrc = "$root/src/FfmpegGui/Services/QueueProcessor.cs"
$srcTxt = ""
if (Test-Path $qpSrc) { $srcTxt = [System.IO.File]::ReadAllText($qpSrc) }
# 去掉**整行**注释后再断言（该方法的注释里逐字引用了旧判据与新判据）。
$srcCodeLines = @()
foreach ($ln in ($srcTxt -split "`r?`n")) { if ($ln -notmatch '^\s*//') { $srcCodeLines += $ln } }
$srcCode = $srcCodeLines -join "`n"
# 只取 IsAnimatedAvifAsync 的方法体（大括号配对），避免命中别处的同名字符串。
$ai = $srcCode.IndexOf('private static async Task<(bool animated, bool determinate)> IsAnimatedAvifAsync(')
$avifBody = ""
if ($ai -ge 0) {
  $ob = $srcCode.IndexOf('{', $ai)
  if ($ob -ge 0) {
    $depth = 0; $end = -1
    for ($i = $ob; $i -lt $srcCode.Length; $i++) {
      $ch = $srcCode[$i]
      if ($ch -eq '{') { $depth++ }
      elseif ($ch -eq '}') { $depth--; if ($depth -eq 0) { $end = $i; break } }
    }
    if ($end -gt $ob) { $avifBody = $srcCode.Substring($ai, $end - $ai + 1) }
  }
}
CK ($avifBody.Length -gt 0) "§2 取到 IsAnimatedAvifAsync 方法体（否则后续源码形态断言真空绿）"
CK ($avifBody -match 'nb_frames') `
   "§2 探测命令带 `nb_frames`（判据已从「数流条数」改为「是否真有流带多帧」的前提）"
CK (($avifBody -match 'maxFrames\s*>\s*1') -and ($avifBody -match 'framesKnown')) `
   "§2 判据含「多帧」支（maxFrames > 1 / framesKnown）"
CK ($avifBody -match 'IsoBmffGainMapProbe\.HasTmapItemFast\(') `
   "§2 判据含「增益图容器」支（HasTmapItemFast）—— 用来把「静态 AVIF+alpha」与「静态增益图 AVIF」分开"
CK ($avifBody -notmatch 'videoCount\s*>=\s*2') `
   "§2 旧判据「videoCount >= 2」已移除（它把「输入有第二条流」误当「输入多帧」）"
# 反控：把「增益图容器」那一支从源码文本删掉 ⇒ 锚点断言必须转红。
$mutantBody = $avifBody -replace 'IsoBmffGainMapProbe\.HasTmapItemFast\(', 'MutatedAway('
CK (-not ($mutantBody -match 'IsoBmffGainMapProbe\.HasTmapItemFast\(')) `
   "§2 **反控（断言有牙）**：把 HasTmapItemFast( 变异掉 ⇒ 上面的锚点断言会判红 —— 证明该锁不是恒真"

# §2b 缺陷 C 的源码形态锁：色彩探测里必须有「CICP 可用 ⇒ EXIF 让位」这一支。
#   ⚠ 与 §3b 的**行为**断言配对：行为断言可能因素材/环境变化而失效，源码形态锁保证**判据本身**还在。
$fcbSrc = "$root/src/FfmpegGui/Services/FfmpegCommandBuilder.cs"
$fcbTxt = ""
if (Test-Path $fcbSrc) { $fcbTxt = [System.IO.File]::ReadAllText($fcbSrc) }
$fcbLines = @()
foreach ($ln in ($fcbTxt -split "`r?`n")) { if ($ln -notmatch '^\s*//') { $fcbLines += $ln } }
$fcbCode = $fcbLines -join "`n"
CK ($fcbCode -match 'bool\s+ffCicpUsable\s*=') `
   "§2b 源码形态：色彩探测里有「CICP 可用」判据（ffCicpUsable）"
CK (($fcbCode -match 'if\s*\(exifTrc\s*!=\s*null\)') -and ($fcbCode -match 'if\s*\(ffCicpUsable\)')) `
   "§2b 源码形态：EXIF 的采用与否受 CICP 可用性门控（EXIF **只在 CICP 不可用时**才生效）"
$fcbMut = $fcbCode -replace 'bool\s+ffCicpUsable\s*=', 'bool MutatedAway ='
CK (-not ($fcbMut -match 'bool\s+ffCicpUsable\s*=')) `
   "§2b **反控（断言有牙）**：把 ffCicpUsable 变异掉 ⇒ 上面的锚点断言会判红 —— 证明该锁不是恒真"

# ── §3 行为：识别 + 解码 + 不误伤 ──────────────────────────────────────────────
Write-Host "`n### §3 行为：增益图 AVIF 走增益图路径；动画/alpha 不被误伤 ###" -ForegroundColor Cyan
$bSdr = RunApp $gmSdr "webp" "gm_sdr"
$bHdr = RunApp $gmHdr "webp" "gm_hdr"
$bAnim = RunApp $anim "gif" "anim"
$bAlpha = RunApp $alphaAvif "webp" "alpha"
$bPlain = RunApp $plain "webp" "plain"
foreach ($x in @(@{n='gm_sdr';r=$bSdr}, @{n='gm_hdr';r=$bHdr}, @{n='anim';r=$bAnim}, @{n='alpha';r=$bAlpha}, @{n='plain';r=$bPlain})) {
  Write-Host ("  [{0}] 结束={1} 耗时={2:N1}s 产物={3}" -f $x.n, $x.r.terminated, $x.r.elapsed, $x.r.prods)
}
# ⚠⚠ 判据载体必须是**日志里真的会出现**的串：`两步法` 只出现在 `item.Command` 与 `--dry-run` 输出里，
#   **不进项目日志** ⇒ 拿它当判据会得到「恒 0 条」⇒ `-eq 0` 恒真（**真空绿**）、`-ge 1` 恒假（假红）。
#   本轮首跑实测踩到。真正的日志标记是 `[avif→xxx] Step1: 分轨提取颜色+alpha...`。
$rxStep1 = '\[avif→[a-z]+\]\s*Step1:'
$step2Sdr   = @($bSdr.text   -split "`r?`n" | Where-Object { $_ -match $rxStep1 }).Count
$step2Hdr   = @($bHdr.text   -split "`r?`n" | Where-Object { $_ -match $rxStep1 }).Count
$step2Anim  = @($bAnim.text  -split "`r?`n" | Where-Object { $_ -match $rxStep1 }).Count
$step2Alpha = @($bAlpha.text -split "`r?`n" | Where-Object { $_ -match $rxStep1 }).Count

CK (($bSdr.terminated) -and ($bSdr.prods -ge 1) -and ($bSdr.text -match '检测到 AVIF/HEIC 增益图')) `
   "§3 SDR 增益图 AVIF → webp：识别并产出（实 产物=$($bSdr.prods)）"
CK ($bSdr.text -match '已解码为线性 HDR PFM') `
   "§3 SDR：增益图**真被应用**（日志出现「已解码为线性 HDR PFM」= 走完元数据+增益图 item 解码）"
CK ($step2Sdr -eq 0) `
   "§3 SDR：**不得**再走两步法（实 $step2Sdr 条 `[avif→xxx] Step1:`）—— 旧判据下它被判动画，这里正是缺陷②的回归钉。⚠ 该判据载体的**活性**由下面 anim/alpha 两条 `-ge 1` 证明（否则 `-eq 0` 可能是真空绿）"
CK ($bHdr.text -match 'backward') `
   "§3 HDR 增益图 AVIF：方向**点名**（backward）⇒ 按普通 HDR 处理，不静默套用增益"
CK ($step2Hdr -eq 0) `
   "§3 HDR：**不得**再走两步法（实 $step2Hdr 条）"
CK ($step2Anim -ge 1) `
   "§3 **不误伤**：真动画 AVIF → gif 仍走两步法（实 $step2Anim 条）—— 新判据不得把真动画判成静态"
CK ($step2Alpha -ge 1) `
   "§3 **不误伤**：静态 AVIF+alpha → webp 仍走两步法（实 $step2Alpha 条）—— 改判静态会丢 alpha"
CK ($bPlain.text -match '输入为静态 AVIF') `
   "§3 **不误伤**：普通静态 AVIF → webp 仍走单命令（日志「输入为静态 AVIF」）"

# ── §3b 缺陷 C 回归钉：容器 CICP 优先于 EXIF/XMP `ColorSpace` ─────────────────────
#   缺陷：`seine_hdr_gainmap_srgb.avif` 的 **CICP 说 `smpte2084`(PQ)**，而 **`XMP-exif:ColorSpace` 说 `sRGB`**
#   ⇒ 旧逻辑让 EXIF 覆盖 CICP ⇒ 判「SDR 输入」⇒ **不插 HDR 色调映射链** ⇒ 下游 `iccgen` 遇 **PQ 帧**
#   **硬失败**（`Not yet implemented in FFmpeg`）⇒ **零产物**。
#   修法：CICP 可用（非 `unknown`/`unspecified`）时 EXIF **让位**；CICP 不可用时行为**完全不变**。
Write-Host "`n### §3b 缺陷 C：容器 CICP 优先于 EXIF ColorSpace（含负控）###" -ForegroundColor Cyan
$et = "$root/publish/PLAN/exiftool/exiftool.exe"
$negPng = "$work/neg_src.png"; $negJpg = "$work/neg_exif_srgb.jpg"
$r = Exec $ff "-y -v error -f lavfi -i `"testsrc2=s=64x48`" -frames:v 1 `"$negPng`"" "gen_negpng"
$r = Exec $ff "-y -v error -i `"$negPng`" -frames:v 1 `"$negJpg`"" "gen_negjpg"
if ((Test-Path $et) -and (Test-Path $negJpg)) { $r = Exec $et "-overwrite_original -EXIF:ColorSpace=sRGB `"$negJpg`"" "tag_negjpg" }  # 合并取数已论证：这是**写标签**的动作（打标夹具），返回值只赋给 $r 且从不消费 ⇒ 不喂任何断言
# 负控素材形态自检（**没有它，下面那条负控可能真空绿**）：CICP 必须真是 unknown，且 EXIF 标签必须真写进去了。
# ⚠ 两条都读**工具写在 stdout 的值** ⇒ `ExecOut`。实测（laneB equiv-laneB3/4）：
#   · `$nchk` 用 `-v error` 的 ffprobe ⇒ stderr 0 字节、`unknown` 在 stdout ⇒ 与合并版同值，换尺不由绿转红；
#   · `$echk` 打标后回读：stdout = 「[EXIF] ColorSpace : sRGB」56 B，合并版在同一次调用里多吐 245 B 的
#     perl locale 警告（OLD 301 B / NEW 56 B）⇒ 值被警告糊住，`-match 'ColorSpace'` 读的就不再是"文件里
#     真有什么标签"。⚠ 反向也实测过：文件缺失时 exiftool 的 Error 行只在 **stderr**、stdout 为空 ⇒
#     只读 stdout 会让这条自检**转红**，而"标签没写进去"本来就该红（收紧，不是放宽）。
$nchk = ExecOut $fp "-v error -select_streams v:0 -show_entries stream=color_transfer -of csv=p=0 `"$negJpg`"" "chk_neg_cicp"
$echk = ExecOut $et "-G -s -ColorSpace `"$negJpg`"" "chk_neg_et"
CK (($nchk.text -match 'unknown') -and ($echk.text -match 'ColorSpace')) `
   "§3b 负控素材形态：JPEG 的 CICP 真是 unknown 且 EXIF ColorSpace 标签真写进去了（否则下面那条负控真空绿）"
$pPos = Exec $sp "color `"$gmHdr`"" "p_cicp_pos"
$pNeg = Exec $sp "color `"$negJpg`"" "p_cicp_neg"
Write-Host "  正向(HDR AVIF)  : $(($pPos.text -split "`r?`n" | Where-Object { $_ -match 'primaries=' } | Select-Object -First 1))"
Write-Host "  负控(JPEG+EXIF) : $(($pNeg.text -split "`r?`n" | Where-Object { $_ -match 'primaries=' } | Select-Object -First 1))"
CK (($pPos.text -match 'trc=smpte2084') -and ($pPos.text -match 'hasIccSemantics=False')) `
   "§3b 正向：容器 CICP（trc=smpte2084 / PQ）**胜过** EXIF 的 sRGB 残留（hasIccSemantics=False）"
CK (($pNeg.text -match 'trc=iec61966-2-1') -and ($pNeg.text -match 'hasIccSemantics=True')) `
   "§3b **负控**：JPEG 无 CICP（ffprobe 恒 unknown）时 EXIF `ColorSpace` **仍生效**（trc=iec61966-2-1）—— 不得一刀切删掉该分支"
$bHdrJpg = RunApp $gmHdr "jpg" "gm_hdr_jpg"
CK (($bHdrJpg.terminated) -and ($bHdrJpg.prods -ge 1)) `
   "§3b 行为：HDR 底增益图 AVIF → jpg **产出 ≥1**（实 $($bHdrJpg.prods)）—— 修复前是**零产物** + Not yet implemented"
# ⚠⚠ **上面那行末尾原本写作 `` `Not yet implemented` ``（反引号包裹）**：尾反引号**紧贴字符串结束引号**
#   ⇒ PowerShell 把 `` `" `` 解析成**字面双引号** ⇒ 字符串**不闭合**，把紧随其后的
#   `# ── §4 收尾 ──` 注释、`Write-Host` 标题、以及**整段 §3c** 全部**吞成字符串内容**
#   ⇒ 那些代码**静默不执行**（症状：`PASS` 总数偏少、且「注释行」竟然出现在输出里）。
#   ⚠ 这不是语法错误（解析器不报错）⇒ `verify-ps-compat` 与 `Parser.ParseFile` **都抓不到**。
#   ⇒ **纪律**：双引号串里不要用反引号包裹代码片段（改裸写或用「」）；尤其**尾反引号不得紧贴引号**。

# ── §3d 厂商 HDR 尾注：V1–V18 必须在**执行面**跑，且条数钉死 ──────────────────────────
#   为什么落 ServiceProbe 而不是 tests/gainmap：那四份镜像工程（tests/{encode,io,metadata,gainmap}）的 exe
#     从来不被任何门禁执行，只是源文本拷贝 ⇒ 断言写在那里＝「只在死代码里有的断言」。
#     2026-10-15 先把 V1–V18 那段从镜像删掉；2026-10-16 价值普查 A-1 把四份镜像工程整体删除
#     （`docs/review-2026-10-16/test-value-audit.md`），#76 的 ⑤-c 同批改成「只此一份锁」⑤。
#   为什么钉 pass=18 而不只钉 fail=0：本段的牙是**条数** —— 早退/改名会让断言整段不跑而 fail 照旧 0。
#     #76 ③ 量的是源文本里的调用位，这里量实际执行并通过的数 ⇒ 两把尺互补，不重复。
#   两臂天然分叉（不是自身对照）：识别若退化成「见到字段名就放行」⇒ V6/V7/V8 红；
#     识别若整体失效 ⇒ V1/V2/V3 红 ⇒ 任一侧坏都有活格子。
Write-Host "`n### §3d 厂商 HDR 尾注（ServiceProbe vendor-hdr，V1–V18）###" -ForegroundColor Cyan
$vh = ExecOut $sp "vendor-hdr" "vendor_hdr"
$vhSum = ($vh.text -split "\r?\n" | Where-Object { $_ -match "PROBE RESULT" } | Select-Object -First 1)
CK ($vh.text -match "PROBE RESULT: pass=18 fail=0") "§3d vendor-hdr 18 条全绿（实 $vhSum）"
$vPass = @([regex]::Matches($vh.text, "(?m)^PASS V")).Count
CK ($vPass -eq 18) "§3d 逐条点名：以 PASS V 开头的行 = 18（实 $vPass）⇒ 汇总数不是靠早退蒙对"
#   两条「必须拒」的臂只钉 Found=False 是不够的（任何解析失步都会 False）⇒ 必须钉**拒因**。
CK (($vh.text -match "no JPEG matches the declared mask length") -and ($vh.text -match "expected 144")) `
   "§3d 拒因点名：V6 声明长度不符 / V7 参数块非 144（两条各死于自己那条判据，不是顺带没认出）"
CK ($vh.text -match "V8/V9 vendorFound=False isoHasGainMap=True isoForm=tmap") `
   "§3d 与标准承载互斥：真 tmap 件厂商支不认、ISO 支仍认（同一行读数钉住两侧）"

# ── §3e 底图取法 A/B（--gain-map-base-from-source）：开关必须**可测**，A 必须**更忠实** ────────
#   为什么判据用**相对**口径（|ΔA| < |ΔB|）而不是绝对阈值：合成件只有 64x48，JPEG 重编码本身就带来
#   ~2 code 的均值漂移，钉绝对数会随夹具尺寸假红。真机件（26 MP 一加原片）上的绝对数是
#   源 124.77 / A 124.29(Δ0.48) / B 130.53(Δ5.76)，那一份**不进门禁**（个人素材不入仓）。
#   ⚠ 对照臂必须先证明**真的分叉**（本仓老规矩）：CK-1 钉两臂各自点名、CK-2 钉两臂产物字节不同。
Write-Host "`n### §3e 底图取法 A/B（两臂点名 + 相对忠实度 + 单一真值）###" -ForegroundColor Cyan
$abA = RunApp $heicPos "jpg" "ab_A" @("--jpeg-gain-map", "true", "--gain-map-base-from-source", "true")
$abB = RunApp $heicPos "jpg" "ab_B" @("--jpeg-gain-map", "true", "--gain-map-base-from-source", "false")
$fa = Get-ChildItem "$work/out_ab_A" -Filter *.jpg -ErrorAction SilentlyContinue | Select-Object -First 1
$fb = Get-ChildItem "$work/out_ab_B" -Filter *.jpg -ErrorAction SilentlyContinue | Select-Object -First 1
$srcRgb = "$work/ab_src.rgb"; $aRgb = "$work/ab_A.rgb"; $bRgb = "$work/ab_B.rgb"
$null = Exec $ff "-y -v error -i `"$heicPos`" -map 0:0 -frames:v 1 -f rawvideo -pix_fmt rgb24 `"$srcRgb`"" "abff_src"
if ($fa) { $null = Exec $ff "-y -v error -i `"$($fa.FullName)`" -frames:v 1 -f rawvideo -pix_fmt rgb24 `"$aRgb`"" "abff_a" }
if ($fb) { $null = Exec $ff "-y -v error -i `"$($fb.FullName)`" -frames:v 1 -f rawvideo -pix_fmt rgb24 `"$bRgb`"" "abff_b" }
function MeanRgb([string]$p) {
  if (-not (Test-Path $p)) { return -1.0 }
  $bb = [System.IO.File]::ReadAllBytes($p)
  if ($bb.Length -eq 0) { return -1.0 }
  $s = [long]0; foreach ($x in $bb) { $s += [long]$x }
  return ($s / $bb.Length)
}
$mS = MeanRgb $srcRgb; $mA = MeanRgb $aRgb; $mB = MeanRgb $bRgb
Write-Output ("  [ab] 源底图均值={0:N3} A臂={1:N3} B臂={2:N3}  |dA|={3:N3} |dB|={4:N3}" -f `
  $mS, $mA, $mB, [Math]::Abs($mA - $mS), [Math]::Abs($mB - $mS))
#   ⚠ **前置自证**：两臂必须各自打出「✅ Ultra HDR JPEG」—— 第一版没带 `--jpeg-gain-map=true`，
#   两臂都走成普通管线（均值 48.088/48.088、日志无「底图=」点名），§3e-3 的相对比较变成真空判定。
CK (($abA.text -match 'Ultra HDR JPEG') -and ($abB.text -match 'Ultra HDR JPEG') `
    -and ($abA.text -match '底图=源 SDR 底图') -and ($abB.text -notmatch '底图=源 SDR 底图') `
    -and ($abB.text -match '分段 Reinhard')) `
   "§3e-1 两臂都真产出增益图，且各自点名（A=底图=源 / B=自造 Reinhard，互不出现对方点名）"
$shaA = ""; $shaB = ""
if ($fa) { $shaA = (Get-FileHash -Algorithm SHA256 $fa.FullName).Hash }
if ($fb) { $shaB = (Get-FileHash -Algorithm SHA256 $fb.FullName).Hash }
$shaAd = if ($shaA.Length -ge 12) { $shaA.Substring(0, 12) } else { "(无产物)" }
$shaBd = if ($shaB.Length -ge 12) { $shaB.Substring(0, 12) } else { "(无产物)" }
CK (($shaA.Length -eq 64) -and ($shaB.Length -eq 64) -and ($shaA -ne $shaB)) `
   "§3e-2 对照臂真的分叉：两臂产物 sha256 不同（实 $shaAd… vs $shaBd…）⇒ 不是死旋钮"
CK (($mS -ge 0) -and ($mA -ge 0) -and ($mB -ge 0) -and ([Math]::Abs($mA - $mS) -lt [Math]::Abs($mB - $mS))) `
   "§3e-3 A 更接近源底图：|dA| < |dB|（读数见上一行）⇒ 「底图=源」是更忠实的一档"
# 单一真值：两个出口必须都走 accessor，不许再各自按 Options 判一次
$optFiles = @("$root/src/FfmpegGui/Services/QueueProcessor.cs",
              "$root/src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs")
$bare = 0; $via = 0
foreach ($f in $optFiles) {
  if (-not (Test-Path $f)) { CK $false "§3e-4 出口文件在位（缺 $f）"; continue }
  $code = @()
  foreach ($ln in (([System.IO.File]::ReadAllText($f)) -split "`r?`n")) { if ($ln -notmatch '^\s*//') { $code += $ln } }
  $t = $code -join "`n"
  $via += ([regex]::Matches($t, 'GainMapSourceBaseRawForEncode')).Count
  # 裸读 = 出现 `.GainMapSourceBaseRaw` 且**后面不是** `=`（赋值给它是合法的，读它才是要禁的）
  $bare += ([regex]::Matches($t, '\.GainMapSourceBaseRaw(?!ForEncode)(?!\s*=)')).Count
}
CK (($via -eq 2) -and ($bare -eq 0)) `
   "§3e-4 两个出口都只读唯一取底图入口（ForEncode 命中 $via 须 2、裸读 GainMapSourceBaseRaw 命中 $bare 须 0）"

# ── §3f HEIC 解码 → 宽色域/PQ 出口（产品级 · 三个独立读者 · 码域分叉判据）────────
#   立 §3f 的原因：§3c/§3e 只把 HEIC 转到 **jpg**，而 jpg 结构上承载不了 PQ
#   （`FfmpegCommandBuilder.cs:1011-1015`：无增益图时 trc 被钳成 iec61966-2-1）⇒
#   「HEIC 解码出来的 HDR 能不能带着 BT.2020 + PQ 走出去」这件事**全仓一条判据都没有**。
#   我因此在两条错误读数上各浪费一轮（其中一条是我自己的传参被拆成两个 token）。
#   本段把这条链钉成三件事：① 标注被**三个互不依赖的读者**同向读出；② 增益图**真被解码**
#   （日志点名 item，反"exit=0 但其实没解增益图"的真空）；③ 像素确实落在 PQ 色调域
#   （用同素材的 sRGB 对照臂做**相对**判据，不用魔法常数）。
#   另两条口径：① 第三个读者 avifdec **不在位时本段转「未判」WARN**，不许退化成"只读两个读者还说三读者同向"；
#             ② §3f-10 是**已知缺陷的可见性行**（当前只 WARN 不判红）——修点在别人正改的 CliParser.cs 上，
#                等 --color-space 的取值校验上线后，本行会自己转 PASS，那时才该把它翻成 CK。
$avd = "$root/tools/src/libavif/build/Release/avifdec.exe"

function NclxNum([string]$path, [string]$tag) {
    $t = (ExecOut $et "-n -s -ColorPrimaries -TransferCharacteristics -MatrixCoefficients `"$path`"" "nx_$tag").text
    $p = ''; $q = ''; $m = ''
    if ($t -match 'ColorPrimaries\s*:\s*(\d+)')         { $p = $Matches[1] }
    if ($t -match 'TransferCharacteristics\s*:\s*(\d+)') { $q = $Matches[1] }
    if ($t -match 'MatrixCoefficients\s*:\s*(\d+)')      { $m = $Matches[1] }
    return @($p, $q, $m)
}
function FpPair([string]$path, [string]$tag) {
    $t = (ExecOut $fp "-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer -of default=nw=1 `"$path`"" "fx_$tag").text
    $p = ''; $q = ''
    if ($t -match 'color_primaries=(\S+)') { $p = $Matches[1].Trim() }
    if ($t -match 'color_transfer=(\S+)')  { $q = $Matches[1].Trim() }
    return @($p, $q)
}
function AvdPair([string]$path, [string]$tag) {
    if (-not (Test-Path $avd)) { return @('', '', '') }
    $t = (ExecOut $avd "--info `"$path`"" "ad_$tag").text
    $p = ''; $q = ''; $alt = ''
    if ($t -match 'Color Primaries\s*:\s*(\d+)')          { $p = $Matches[1] }
    if ($t -match 'Transfer Char\.\s*:\s*(\d+)')          { $q = $Matches[1] }
    if ($t -match 'Alternate Headroom\s+([-\d.]+)')          { $alt = $Matches[1] }   # avifdec 的真实形状是「Alternate Headroom 1.30 (HDR)」——**没有冒号**
    $allT = (([regex]::Matches($t, 'Transfer Char\.\s*:\s*(\d+)') | ForEach-Object { $_.Groups[1].Value }) -join '/')
    return @($p, $q, $alt, $allT)
}
function FrameTop([string]$path, [string]$tag) {
    # 码域 Ymax（10bit）：只扫 Y 平面（前 W*H 个样本），避免把色度分量当成亮度
    $dh = (ExecOut $fp "-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 `"$path`"" "dm_$tag").text
    $mm = [regex]::Match($dh, '(\d+)\D+(\d+)')
    if (-not $mm.Success) { return -1 }
    $n = ([int]$mm.Groups[1].Value) * ([int]$mm.Groups[2].Value) * 2
    $raw = "$work/top_$tag.raw"
    $null = Exec $ff "-y -v error -c:v libdav1d -i `"$path`" -frames:v 1 -pix_fmt yuv420p10le -f rawvideo `"$raw`"" "dy_$tag"
    if (-not (Test-Path $raw)) { return -1 }
    $b = [System.IO.File]::ReadAllBytes($raw)
    if ($b.Length -lt $n) { $n = $b.Length - ($b.Length % 2) }
    $best = 0
    for ($i = 0; $i + 1 -lt $n; $i += 2) {
        $v = [int]$b[$i] + ([int]$b[$i + 1] * 256)
        if ($v -gt $best) { $best = $v }
    }
    return $best
}
function PngCicp([string]$path) {
    $b = [System.IO.File]::ReadAllBytes($path)
    $i = 8
    while ($i + 12 -le $b.Length) {
        $ln = ([int]$b[$i] * 16777216) + ([int]$b[$i + 1] * 65536) + ([int]$b[$i + 2] * 256) + [int]$b[$i + 3]
        $t = [System.Text.Encoding]::ASCII.GetString($b, $i + 4, 4)
        if (($t -eq 'cICP') -and ($ln -ge 4)) {
            return @([int]$b[$i + 8], [int]$b[$i + 9], [int]$b[$i + 10], ([int]$b[$i + 11] -band 1))
        }
        if (($t -eq 'IDAT') -or ($ln -le 0)) { break }
        $i += 12 + $ln
    }
    return @(-1, -1, -1, -1)
}
function FirstFile([string]$dir, [string]$ext) {
    $f = @(Get-ChildItem -LiteralPath $dir -Filter ("*." + $ext) -File -EA SilentlyContinue)
    if ($f.Count) { return $f[0].FullName }
    return ''
}

Write-Host "`n### §3f HEIC → BT.2020/PQ 出口（三读者 + 码域分叉）###" -ForegroundColor Cyan
$g3fPq = RunApp $heicPos "avif" "f3_pq" @('--bit-depth', '10', '--color-space', '"BT.2020 PQ"', '--hdr-mode', 'traditional')
$g3fSd = RunApp $heicPos "avif" "f3_sdr" @('--bit-depth', '10', '--color-space', 'sRGB')
$f3Pq = FirstFile $g3fPq.dir 'avif'
$f3Sd = FirstFile $g3fSd.dir 'avif'
CK (($f3Pq -ne '') -and ($f3Sd -ne '')) `
   "§3f-0 前置：两臂都必须产出 AVIF（PQ 臂=$($f3Pq -ne '') 对照臂=$($f3Sd -ne '')）—— 缺件则下面全部无凭据"

if (($f3Pq -ne '') -and ($f3Sd -ne '')) {
    $nPq = NclxNum $f3Pq 'pq'; $nSd = NclxNum $f3Sd 'sd'
    $xPq = FpPair  $f3Pq 'pq'
    $aPq = AvdPair $f3Pq 'pq'
    CK (($nPq[0] -eq '9') -and ($nPq[1] -eq '16') -and ($nPq[2] -eq '9')) `
       "§3f-1 读者①exiftool(-n)：请求 BT.2020 PQ ⇒ 容器 nclx 必须 9/16/9（实 $($nPq -join '/'))"
    CK (($xPq[0] -eq 'bt2020') -and ($xPq[1] -eq 'smpte2084')) `
       "§3f-2 读者②ffprobe：必须 bt2020/smpte2084（实 $($xPq -join '/'))——与①不同实现，互不遮蔽"
    if (($aPq[0] -eq '') -and (-not (Test-Path $avd))) {
        Write-Host "WARN §3f-3 读者③avifdec 不在位（$avd）⇒ 本条**未判**；§3f-1/2 只算两读者，不许对外说三读者同向" -ForegroundColor Yellow
    } else {
        CK (($aPq[0] -eq '9') -and ($aPq[1] -eq '16')) `
           "§3f-3 读者③avifdec --info：必须 9/16（实 $($aPq[0])/$($aPq[1])）"
    }
    CK ($g3fPq.text -match '增益图来源: ISO-BMFF item') `
       "§3f-4 反真空：PQ 臂日志必须点名「增益图来源: ISO-BMFF item…」⇒ 这条真走了 HEIC 增益图解码，不是普通转码"
    $yPq = FrameTop $f3Pq 'pq'; $ySd = FrameTop $f3Sd 'sd'
    CK (($yPq -gt 0) -and ($ySd -gt 0) -and (($ySd - $yPq) -ge 200) -and ($yPq -lt (0.8 * $ySd))) `
       "§3f-5 标签↔像素一致（码域分叉）：同素材同尺寸，PQ 臂 Ymax 必须显著低于 sRGB 臂，换的才只是矩阵不会造成这个量级（实 PQ=$yPq / SDR=$ySd；判据 差≥200 且 PQ<0.8×SDR）"
    CK (-not (($nSd[0] -eq '9') -and ($nSd[1] -eq '16'))) `
       "§3f-6 对照臂不得同向：sRGB 臂必须**不是** 9/16（实 $($nSd -join '/'))——否则 §3f-1 只是在匹配恒真"
}

$g3fPng = RunApp $heicPos "png" "f3_png" @('--bit-depth', '16', '--color-space', '"BT.2020 PQ"', '--hdr-mode', 'traditional')
$f3Png = FirstFile $g3fPng.dir 'png'
if ($f3Png -ne '') {
    $c3f = PngCicp $f3Png
    CK (($c3f[0] -eq 9) -and ($c3f[1] -eq 16) -and ($c3f[3] -eq 1)) `
       "§3f-7 PNG 出口：本门禁自己解析 cICP 块必须 = primaries 9 / trc 16 / fullrange 1（实 $($c3f -join '/'))——不借任何产品侧读者"
} else {
    CK $false "§3f-7 PNG 出口未产出（$($g3fPng.dir)）⇒ 本条无凭据"
}

$g3fGm = RunApp $heicPos "avif" "f3_gm" @('--hdr-mode', 'gainmap', '--jpeg-gain-map', 'true', '--jpeg-gain-map-base-gamut', 'bt2020')
$f3Gm = FirstFile $g3fGm.dir 'avif'
if ($f3Gm -ne '') {
    $bg = [System.IO.File]::ReadAllBytes($f3Gm)
    $tx = [System.Text.Encoding]::GetEncoding(28591).GetString($bg)
    $aGm = AvdPair $f3Gm 'gm'
    CK ($tx.Contains('tmap') -and $tx.Contains('dimg')) `
       "§3f-8 增益图出口：容器里必须同时有 tmap 与 dimg（实 tmap=$($tx.Contains('tmap')) dimg=$($tx.Contains('dimg'))）"
    if (-not (Test-Path $avd)) {
        Write-Host "WARN §3f-9 avifdec 不在位 ⇒ 「第三方读者认这份增益图」未判" -ForegroundColor Yellow
    } else {
        CK (($aGm[2] -ne '') -and ($aGm[3] -match '16')) `
           "§3f-9 第三方读者解析成功：avifdec 读出 Alternate Headroom=$($aGm[2])，且文件里存在 trc=16 的 HDR 表示（读者报出的 Transfer Char 序列=$($aGm[3])）⇒ 这份增益图能被 libavif 理解；读不出就是写错了"
    }
} else {
    CK $false "§3f-8 增益图出口未产出（$($g3fGm.dir)）⇒ 本条无凭据"
}

# ── 已知缺陷的可见性行（**不判红**，但必须把现象打在输出里）────────────────────
#   现象（HEAD c694f27 实测）：`--color-space` 无取值校验（CliParser.cs:760）+ Resolve 用子串关键词
#   匹配（ColorSpaceRegistry.cs:150-185）⇒ 拼错的 "BT.2020 PPP" 与合法的 "BT.2020" **逐字节相同**、
#   exit=0、零警告。修点在别人正在写的 CliParser.cs 上 ⇒ 本轮不动它；这里先把现象钉成可见读数，
#   等校验上线后本行自动转 PASS（那时应把它翻成 CK：非法值 ⇒ 非零退出 + 不产出 + 点名）。
$g3fBad = RunApp $heicPos "avif" "f3_bad" @('--bit-depth', '10', '--color-space', '"BT.2020 PPP"')
$g3fNear = RunApp $heicPos "avif" "f3_near" @('--bit-depth', '10', '--color-space', '"BT.2020"')
$fbBad = FirstFile $g3fBad.dir 'avif'; $fbNear = FirstFile $g3fNear.dir 'avif'
if (($fbBad -ne '') -and ($fbNear -ne '')) {
    $hBad = (Get-FileHash -Algorithm SHA256 -LiteralPath $fbBad).Hash
    $hNear = (Get-FileHash -Algorithm SHA256 -LiteralPath $fbNear).Hash
    if ($hBad -eq $hNear) {
        Write-Host ("WARN §3f-10 已知缺陷仍在：拼错的 BT.2020 PPP 与合法的 BT.2020 产物逐字节相同（sha12=" + $hBad.Substring(0, 12) + "）且 exit=0 ⇒ 外部不可区分。处方 = --color-space 走 ParseTokenStrict/BadValue（同文件 :772/:924 的先例）+ 一条「拼错词必须与合法近邻字节不同」的判据") -ForegroundColor Yellow
    } else {
        Write-Host "§3f-10 拼错词与合法近邻已可区分（校验已上线）⇒ 本行应翻成 CK" -ForegroundColor Green
    }
}

# ── §4 收尾 ────────────────────────────────────────────────────────────────────
# ── §3c HEIC 端到端：合成件的增益图**真被解码并应用**（含负控）──────────────────────────
#   为什么需要它：§1b 只验到「**探针层**」（识别 + 转换 + 产物可解），**没有**证明这条路径真的接进了
#   `GainMapDecoder`（元数据 → item 解码 → 增益公式 → 产出）。§3 对 AVIF 有同类断言，HEIC 侧此前**空白**。
#   ⚠ 诚实界定：本件的底图与增益图用的是**同一份** HEVC 码流（合成件），所以这里断言的是**路径被走通**
#     （含「增益真被应用」这一事件），**不是**图像语义正确性。
Write-Host "`n### §3c 行为：HEIC 合成件走完增益图路径（含负控）###" -ForegroundColor Cyan
$heicPlain = "$root/tools/src/libultrahdr/third_party/libheif/examples/example.heic"
CK (Test-Path $heicPlain) "§3c 负控素材存在（libheif 的 example.heic = 普通 HEIC，无 tmap）"
$bHeic = RunApp $heicPos "jpg" "heic_gm"
CK (($bHeic.prods -ge 1) -and ($bHeic.text -match '检测到 ISO-BMFF 增益图')) `
   "§3c 合成 HEIC → jpg：识别并产出（实 产物=$($bHeic.prods)）"
CK ($bHeic.text -match '已解码为线性 HDR PFM') `
   "§3c HEIC：增益图**真被解码并应用**（日志出现「已解码为线性 HDR PFM」= 走完元数据 + HEVC item 解码 + 增益公式）"
CK ($bHeic.text -match 'hvc1，\d+ B') `
   "§3c HEIC：增益图来源点名到 hvc1 item 且字节数为转换后的 Annex-B（实 $(($bHeic.text -split "`r?`n" | Where-Object { $_ -match '增益图来源' } | Select-Object -First 1))）"
# 负控：普通 HEIC（无 `tmap`）**不得**走增益图路径 —— 否则上面「识别」那条断言可能恒真
if (Test-Path $heicPlain) {
  $bHeicCtl = RunApp $heicPlain "jpg" "heic_plain"
  CK (($bHeicCtl.prods -ge 1) -and ($bHeicCtl.text -notmatch '检测到 ISO-BMFF 增益图')) `
     "§3c **负控**：普通 HEIC → jpg 产出正常且**不出现**增益图路径（实 产物=$($bHeicCtl.prods)）"
}

Write-Host "`n### §4 收尾 ###" -ForegroundColor Cyan
$exeMtimeAfter = (Get-Item $exe).LastWriteTimeUtc
CK ($exeMtimeBefore -eq $exeMtimeAfter) "§4 被测 exe 未被中途重建（$exe）"
Write-Host "  本门禁共起过 $($script:spawned.Count) 个应用进程"
foreach ($spid in $script:spawned) { KillTree ([int]$spid) }

Write-Host ""
Write-Host "===== gainmap-isobmff: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 退出码必须在清理之后，且必须写完整 if/else（§6 第 67 条）。
if ($fail -gt 0) { exit 1 } else { exit 0 }
