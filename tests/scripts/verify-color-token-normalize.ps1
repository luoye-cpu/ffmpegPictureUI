# verify-color-token-normalize.ps1 —— 色彩 token 规范化门禁（2026-09-16 新增）
#
# 为什么需要它（两个**实测**缺陷，用户可见、且报错文本完全误导）：
#   ① CLI `--help` 宣传 `--color-trc <linear|srgb|pq|hlg|...>`，但这些友好名被**原样**拼进
#      ffmpeg 的 `-color_trc`；ffmpeg 只认自己的常量名 ⇒
#      `Unable to parse "color_trc" option value "hlg"` ⇒ 退出码 -22、**零产物**，
#      而报错只提解码器，用户看不出是自己选的 token 不被支持。
#   ② GUI 的 `ColorMatrixCombo` 历史上混入了 zimg 风格名（bt2020ncl/bt2020cl/ycgcocn/ycgcocs）、
#      裸族名（bt2020）与**基准名**（smpte432/smpte431）；应用白名单 `validColorspaces` 又把这些
#      一律放行 ⇒ 选中即令整条命令 -22 失败。实测 10 个选项里 7 个是坏的。
#
# 断言（全部基于**真实 ffmpeg 探测**，不是照抄文档）：
#   A. `ColorSpaceRegistry.FfmpegColorSpaceNames` 里每个名字都被本机 ffmpeg 的 `-colorspace` 接受；
#   B. `MainWindow.xaml` 的 `ColorMatrixCombo` 每个选项都被 ffmpeg 的 `-colorspace` **与** zscale 的
#      `min=/m=` 同时接受（两侧都要，因为同一 token 会分别进这两处）；
#   C. CLI 别名（srgb/pq/hlg、bt2020ncl/ycgcocn…）经规范化后**命令行里出现的必须是原生名**，
#      且**别名本身不得作为该选项的值出现**；
#   D. 负控（有牙）：别名与原生名必须产出**同一条** `-color_trc/-colorspace` 值。
#      去掉规范化时（别名原样透传）本条立刻转红 ⇒ 证明 C 不是空跑。
#   ── 以下四段为后续批次追加（2026-11-02 / 11-03），头注原先只列到 D：──
#   E. 载体细分与顶码（`--pix-fmt` / `-color_range` 的 token 与探测回拷一致性）。
#   F. R0–R6：`colorRange` 缓存回拷（2026-11-02 补齐；变异 MU1 实测 38/5）。
#   G. R7/R7b/R7b'/R8-PSNR：**WebP 出口恒 tv** 的命令行锁 + 像素侧双向与负控。PSNR 由本门禁
#      用 ffmpeg `psnr` 滤镜**现算并打印**（不再引用无当轮出处的 dB 数）。
#      ⚠ 本段源是 lavfi 合成（`color=c=0xFF14F4:s=64x48`，读回 `251,21,241`），与 HANDOVER §3.11
#      里 `a2_srgb_p3.png`（512×384，读回 `116,20,244`）的 `31.88/28.22 dB` **是两组不同来源，不可互推**。
#   H. R9a–R9f：**jpg/jpeg 显式 limited 放行**（`-strict unofficial`）——命令行含 tv / 不含 pc /
#      含 strict / 带 strict 真出图 / **不带 strict 的同形命令确实零产物**（负控）/ 放行必须出声。
#   I. R10a–R10d：**jpg auto 档不得跟随 limited 输入**（2026-11-03 审阅 M1，第四批）——
#      auto 压回 pc（命令行+载体+像素三层），显式 tv 的放行必须出声（EmitWarn），并配
#      「setparams=range=pc 重标 = libjpeg 满量程直取模拟」的负控证明该像素臂真咬得住。
#
# ⚠ 调外部 exe 一律 Start-Process + 单个参数字符串（见 HANDOVER §C.5 的坑）。
# 用法：pwsh -NoProfile -File tests/scripts/verify-color-token-normalize.ps1
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$val = "$root/tests/output/toknorm_$([guid]::NewGuid().ToString('N').Substring(0, 8))"

$pass = 0; $fail = 0
function Say($t) { Write-Output $t }
function CK($ok, $msg) { if ($ok) { $script:pass++; Say "  PASS $msg" } else { $script:fail++; Say "  FAIL $msg" } }
# 所有子进程输出都重定向到**同一对**文件（每次覆盖）：门禁自己的 stdout 只留 PASS/FAIL，
# 且工作目录里不会堆出几十个文件（堆多了还会撞上本机的批量删除保护，收尾删不掉）。
function Run-Exe([string]$file, [string]$argStr) {
    Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$val/_exec.out" -RedirectStandardError "$val/_exec.err" | Out-Null
    return ([System.IO.File]::ReadAllText("$val/_exec.out") + "`n" + [System.IO.File]::ReadAllText("$val/_exec.err"))
}

if (-not (Test-Path $exe)) { Say "SKIP FfmpegGui.exe 未构建（先 dotnet build -c Release --no-restore）"; exit 0 }
if (-not (Test-Path $ff))  { Say "SKIP 缺 ffmpeg（publish/PLAN/ffmpeg-full）"; exit 0 }
New-Item -ItemType Directory -Path $val -Force | Out-Null

# ── 素材：一张无标签的 RGB PNG（只用它跑 dry-run 与取值探测）──
$src = "$val/src.png"
Run-Exe $ff "-y -v error -f lavfi -i `"testsrc2=s=64x48,format=rgb48be`" -frames:v 1 `"$src`"" | Out-Null

# ── 工具：本机 ffmpeg 是否接受某个 -colorspace / -color_trc / -color_primaries 值 ──
function FfAccepts([string]$opt, [string]$value) {
    $err = Run-Exe $ff "-v error -$opt $value -i `"$src`" -frames:v 1 -f null -"
    return ($err -notmatch 'Unable to parse')
}
# ── 工具：zscale 是否接受某个 matrix 名（zscale 与 ffmpeg 的界面名不同：gbr vs rgb）──
# ⚠ `$value:m` 会被 PowerShell 当成**作用域限定变量**（$value 驱动器下的 m）⇒ 必须写 ${value}，
#   否则参数退化成 `min=`，zscale 报 "Unable to parse \"min\" option value \"=\""，
#   看起来像"zscale 不支持 bt709"这种荒谬结论（本脚本首版就踩了）。
function ZscaleAccepts([string]$value) {
    $err = Run-Exe $ff "-v error -i `"$src`" -vf `"format=gbrpf32le,zscale=min=${value}:m=${value}`" -frames:v 1 -f null -"
    return ($err -notmatch 'Error applying option|Invalid argument|Unable to parse')
}
# ── 工具：dry-run 的 ffmpeg 命令行（命令是单行的）──
function DryRunCmd([string]$extraArgs, [string]$fmt = 'jpg') {
    $txt = Run-Exe $exe "--headless -i `"$src`" -o `"$val/out`" -f $fmt -q 100 $extraArgs --dry-run"
    return ($txt -split "`n" | Where-Object { $_ -match '^ffmpeg ' } | Select-Object -First 1)
}
# ── 工具：dry-run 里某个选项的值 ──
function DryRunValue([string]$extraArgs, [string]$opt, [string]$fmt = 'jpg') {
    $line = DryRunCmd $extraArgs $fmt
    if (-not $line) { return $null }
    if ($line -match "(?:^|\s)-$opt\s+(\S+)") { return $Matches[1] }
    return ''
}
# ── 工具：模型内矩阵 token → ffmpeg `-colorspace` 名（与 ColorSpaceRegistry.ToFfmpegColorSpaceName 同义；
#    脚本侧独立重写，避免"拿被测代码验证被测代码"）──
function ToFfmpegMatrix([string]$t) {
    if ($t -eq 'gbr') { return 'rgb' }
    return $t
}

# ══════════════ A. 白名单自身必须与真实 ffmpeg 一致 ══════════════
Say "[A] ColorSpaceRegistry.FfmpegColorSpaceNames vs 真实 ffmpeg"
$reg = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/ColorSpaceRegistry.cs")
$blk = [regex]::Match($reg, 'FfmpegColorSpaceNames\s*=\s*\{(?<b>[^}]*)\}')
$names = @()
if ($blk.Success) {
    $names = @([regex]::Matches($blk.Groups['b'].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
}
CK ($names.Count -ge 10) "从源码解析到 $($names.Count) 个白名单名字"
$badNames = @($names | Where-Object { -not (FfAccepts 'colorspace' $_) })
CK ($badNames.Count -eq 0) "白名单里每个名字都被 ffmpeg 的 -colorspace 接受（不被接受的：$($badNames -join ', ')）"

# ══════════════ B. GUI 矩阵下拉框的每个选项，ffmpeg 与 zscale 两侧都必须接受 ══════════════
Say "[B] MainWindow.xaml ColorMatrixCombo 选项 vs ffmpeg / zscale"
$xaml = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/MainWindow.xaml")
$cm = [regex]::Match($xaml, '<ComboBox x:Name="ColorMatrixCombo">(?<b>.*?)</ComboBox>', 'Singleline')
$items = @()
if ($cm.Success) { $items = @([regex]::Matches($cm.Groups['b'].Value, '<sys:String>([^<]+)</sys:String>') | ForEach-Object { $_.Groups[1].Value }) }
# ⚠ 2026-10-04：下拉**首项 `auto`** 是**哨兵**（= 不指定），**不是** ffmpeg/zscale 的 token ——
#   `MainWindow.xaml.cs` 的 `AutoToNull()` 在提交前把它映射成 `null`，**绝不会**下发到 ffmpeg
#   ⇒ 必须从"每个选项都要被 ffmpeg/zscale 接受"的枚举里**排除**，否则本判据必然假红
#   （实测：加 `auto` 首项后本条 30/3）。**牙齿不减**：其余**真实 token** 一个都不许不可用。
$items = @($items | Where-Object { $_ -ne 'auto' })
CK ($items.Count -ge 1) "从 XAML 解析到 $($items.Count) 个矩阵选项（$($items -join ', ')）"
# 判据要按**实际界面**分两侧：同一 token 会分别进 ffmpeg 的 `-colorspace`（需经 ToFfmpeg 映射）
# 与 zscale 的 `min=/m=`（用原名）。只拿原名去问 ffmpeg 会误判 gbr（zscale 名）为坏值。
$badFf = @($items | Where-Object { -not (FfAccepts 'colorspace' (ToFfmpegMatrix $_)) })
CK ($badFf.Count -eq 0) "每个选项映射到 ffmpeg 后都被 -colorspace 接受（不被接受的：$($badFf -join ', ')）"
$badZs = @($items | Where-Object { -not (ZscaleAccepts $_) })
CK ($badZs.Count -eq 0) "每个选项都被 zscale 接受（不被接受的：$($badZs -join ', ')）"

# B2. 历史脏值必须**既**已从词表移除、**又**确实不被 ffmpeg 接受。
#     后半条是"有牙"证明：若哪天 ffmpeg 支持了 bt2020ncl，说明判据本身需要重审，而不是可以放回去。
#     `bt2020c`/`ycgco` 于 2026-09-16 加入本清单：它们虽被 ffmpeg 的 `-colorspace` **解析**接受，
#     但实测**两个角色都不可用** —— 作输入声明令 swscale 报 "Impossible to convert between the formats"
#     （-40、零产物），作输出标注被 mjpeg/libwebp/libaom 一律拒绝 ⇒ 属"提供了但不可用"，已从词表移除。
#     ⚠ 因此它们**不能**进下面第 2 条"确实不被接受"的断言（ffmpeg 确实接受其名字）——分两句断言。
$legacyBad = @('bt2020ncl', 'bt2020cl', 'bt2020', 'ycgcocn', 'ycgcocs', 'smpte432', 'smpte431')
$stillListed = @($legacyBad | Where-Object { $_ -in $items })
CK ($stillListed.Count -eq 0) "历史脏值已从矩阵词表移除（仍残留：$($stillListed -join ', ')）"
$nowAccepted = @($legacyBad | Where-Object { FfAccepts 'colorspace' $_ })
CK ($nowAccepted.Count -eq 0) "历史脏值确实不被 ffmpeg -colorspace 接受（若被接受则应重审判据：$($nowAccepted -join ', ')）"
$pipeUnusable = @('bt2020c', 'ycgco', 'chroma-derived-nc', 'chroma-derived-c', 'ictcp')
$stillListed2 = @($pipeUnusable | Where-Object { $_ -in $items })
CK ($stillListed2.Count -eq 0) "「本管线不可用」的矩阵已从词表移除（仍残留：$($stillListed2 -join ', ')）"

# ══════════════ C. CLI 别名 → 命令行里必须是原生名 ══════════════
# ⚠ 矩阵用例必须用**非 RGB 原生**输出格式（jpg）：png/tiff/apng/jxl 会跳过 `-colorspace` 写入
#   （RGB 原生容器不需要 YUV 矩阵标签）⇒ 拿 png 测矩阵永远拿到空值，看着像"没生效"。
Say "[C] CLI 别名规范化（dry-run 实测）"
$trcCases = @(
    @{ Alias = 'srgb'; Native = 'iec61966-2-1' },
    @{ Alias = 'pq';   Native = 'smpte2084' },
    @{ Alias = 'hlg';  Native = 'arib-std-b67' }
)
foreach ($c in $trcCases) {
    $got = DryRunValue "--color-primaries bt2020 --color-trc $($c.Alias)" 'color_trc'
    CK ($got -eq $c.Native) "--color-trc $($c.Alias) ⇒ -color_trc $got（期望 $($c.Native)）"
}

# C2. **核心不变量**：命令行里出现的每一个 -color_primaries/-color_trc/-colorspace 值
#     都必须是本机 ffmpeg **实测接受**的名字。这条与"矩阵该取什么值"无关（矩阵由裁决层决定），
#     它只锁定"绝不把 ffmpeg 不认识的名字拼进命令"——原始缺陷正是违反此条。
$aliasCmdCases = @(
    @('--color-primaries bt2020 --color-trc srgb'),
    @('--color-primaries bt2020 --color-trc pq'),
    @('--color-primaries bt2020 --color-trc hlg'),
    @('--color-primaries bt2020 --color-trc hlg --color-matrix bt2020ncl'),
    @('--color-primaries bt2020 --color-trc hlg --color-matrix ycgcocn'),
    @('--color-primaries bt2020 --color-trc smpte2084 --color-matrix bt2020cl'),
    @('--color-primaries smpte432 --color-trc iec61966-2-1 --color-matrix ycgcocs')
)
$violations = @()
foreach ($case in $aliasCmdCases) {
    $cmd = DryRunCmd $case
    if (-not $cmd) { $violations += "[$case] 未产出 ffmpeg 命令行"; continue }
    foreach ($opt in @('color_primaries', 'color_trc', 'colorspace')) {
        foreach ($m in [regex]::Matches($cmd, "(?:^|\s)-$opt\s+(\S+)")) {
            $v = $m.Groups[1].Value.Trim('"')
            if (-not (FfAccepts $opt $v)) { $violations += "[$case] -$opt $v 不被 ffmpeg 接受" }
        }
    }
}
CK ($violations.Count -eq 0) "命令行里的色彩选项值全部被 ffmpeg 接受（违规 $($violations.Count) 条$($(if ($violations.Count) { '：' + (($violations | Select-Object -First 3) -join ' | ') })))"

# ══════════════ D. 负控（有牙）：别名与原生名必须产出**完全相同**的命令行 ══════════════
# 去掉规范化时别名会原样透传 ⇒ 命令不同 ⇒ 下面每一条转红。这就是 C 不空跑的证明。
Say "[D] 负控：别名与原生名产出同一条命令行"
foreach ($c in $trcCases) {
    $a = DryRunCmd "--color-primaries bt2020 --color-trc $($c.Alias)"
    $n = DryRunCmd "--color-primaries bt2020 --color-trc $($c.Native)"
    CK ($a -and $a -eq $n) "别名 $($c.Alias) 与原生名 $($c.Native) 命令完全一致"
}
$matCases = @(
    @{ Alias = 'bt2020ncl'; Native = 'bt2020nc' },
    @{ Alias = 'bt2020cl';  Native = 'bt2020c' },
    @{ Alias = 'ycgcocn';   Native = 'ycgco' },
    @{ Alias = 'ycgcocs';   Native = 'ycgco' }
)
foreach ($c in $matCases) {
    $a = DryRunCmd "--color-primaries bt2020 --color-trc hlg --color-matrix $($c.Alias)"
    $n = DryRunCmd "--color-primaries bt2020 --color-trc hlg --color-matrix $($c.Native)"
    CK ($a -and $a -eq $n) "矩阵别名 $($c.Alias) 与原生名 $($c.Native) 命令完全一致"
}

# ══════════════ E. 矩阵的「两个角色」必须各就各位（2026-09-16 修复）══════════════
# 原缺陷（角色错位）：用户的矩阵被**只**当作**输出标签**，从不作为**输入声明** ⇒
#   ① 无标签 YUV 源下 `--color-matrix bt2020nc` 与 `bt709` 的产物 **md5 相同、PSNR=inf**
#      （YUV→RGB 走了 swscale 默认矩阵）⇒ 该下拉的唯一语义「声明源矩阵」100% 未落实；
#   ② jpg/webp 上把用户值原样写进输出标签，而 mjpeg/libwebp 会**静默忽略**（宣称≠交付）；
#   ③ `bt2020c`/`ycgco` 更会让 swscale 报 "Impossible to convert between the formats"
#      ⇒ 退出码 -40、**零产物**（GUI 词表里原本就有这两项）。
# 修复后：① 输入声明放 `-i` 前（走 swscale 白名单）；② 输出标注服从容器能力 capM；③ 不可用值跳过并出声。
Say "[E] 矩阵的角色分离（输入声明 / 输出标注）"

# E1/E2：按 ` -i ` 切成前后两段，分别核对两个角色
foreach ($m in @('bt709', 'bt2020nc', 'bt470bg', 'smpte170m')) {
    $cmd = DryRunCmd "--color-primaries bt709 --color-trc iec61966-2-1 --color-matrix $m" 'jpg'
    $head = ($cmd -split ' -i ')[0]
    $tail = ($cmd -split ' -i ')[1]
    CK ($head -match "-colorspace $m") "E1 输入声明出现在 -i 之前（matrix=$m）"
    # 输出转换矩阵必须服从容器能力：jpg 写不进矩阵字段，标准解码器（libjpeg/libwebp）按 BT.601 解，
    # 故 RGB→YUV 也必须用 BT.601。2026-10-25 实测：旧 capM=bt709 会让纯红 255,0,0 解回 232,0,1；
    # 改 bt470bg 后 rc=0 且解回 254,0,0（转换与解码假设一致）。
    CK ($tail -match '-colorspace bt470bg') "E2 输出转换矩阵服从 capM=bt470bg（用户选 $m，jpg 实际用 bt470bg）"
}

# E3：不可用矩阵 —— 必须**出声**且**不得失败**（此前是静默忽略或 -40 零产物）
foreach ($bad in @('ycgco', 'bt2020c')) {
    New-Item -ItemType Directory -Path "$val/out" -Force | Out-Null
    $o = Run-Exe $exe "--headless -i `"$src`" -o `"$val/out`" -f jpg -q 95 --color-primaries bt709 --color-trc iec61966-2-1 --color-matrix $bad"
    CK (Test-Path "$val/out/src.jpg") "E3 不可用矩阵 $bad 不再导致零产物"
    CK ($o -match '本管线不支持') "E3 不可用矩阵 $bad 有明确告知（可用性诚实，不静默忽略）"
}

# E4：GUI 词表里每个选项都必须真的能当**输入声明**用（否则就是"提供了但不可用"）
$regIn = [regex]::Match($reg, 'FfmpegInputMatrixNames\s*=\s*\{(?<b>[^}]*)\}')
$inNames = @()
if ($regIn.Success) { $inNames = @([regex]::Matches($regIn.Groups['b'].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) }
CK ($inNames.Count -ge 5) "从源码解析到 $($inNames.Count) 个输入矩阵白名单"
# ⚠ 不要用三元运算符 `? :`（PowerShell 7+ 专属）——运行器用**当前宿主**作子脚本解释器，
#   宿主随启动方式而变（pwsh ⇒ PS7 / powershell.exe ⇒ PS 5.1）；在 PS 5.1 下整个脚本**解析失败**、
#   一行都不执行、门禁静默失效。此处用 if 语句块。门禁：`verify-ps-compat.ps1`。
$unusable = @($items | Where-Object {
    $n = $_
    if ($n -eq 'gbr') { $n = 'rgb' }
    $n -notin $inNames
})
CK ($unusable.Count -eq 0) "词表每个选项都在输入矩阵白名单内（不在的：$($unusable -join ', ')）"

# ══════════════ F. 输入色彩范围回拷（2026-11-02 第二批：t2）══════════════
# 缺陷：`ProbeWithFfprobeOnce` 把 `colorRange` 只写进**局部** `meta`，回拷段（`FfmpegCommandBuilder.cs`
# 的 `meta.pixFmt = ffMeta.pixFmt;` 一带）**漏拷** ⇒ `ProbeInputColorRange`（:1354）恒得 null ⇒ 三处
# 「按设计该发声」的路径**恒静默**：① 输入侧 `-color_range`（:70-76）；② 输出侧 `ResolveOutputColorRange`
# （:1100-1110）的 auto 档恒 null；③ 组路径 setparams 的 `range=`（:2268-2275）。
# ⚠ 仓规 §7 曾记「8-bit 尺取错」——**实测不成立**：`MapPixFmt` 取的是 `options.ColorRange`（用户值），
#   从不读探测结果 ⇒ 像素与 tag 自洽。真实后果就是上面三条死支。判据因此判**命令行文本**，不判像素。
Say "[F] 输入色彩范围回拷（dry-run 的 -color_range 三个角色）"
# 素材：全范围 16-bit RGB（rgb48le）与 limited YUV —— 两者 pix_fmt 推断必然相反
$pc16 = "$val/pc16.tif"
$limMp4 = "$val/lim.mp4"
Run-Exe $ff "-y -v error -f lavfi -i `"testsrc2=s=64x48,format=rgb48le`" -frames:v 1 `"$pc16`"" | Out-Null
Run-Exe $ff "-y -v error -f lavfi -i `"testsrc2=s=64x48`" -pix_fmt yuv420p -frames:v 30 `"$limMp4`"" | Out-Null
# ── 工具：取 dry-run 命令行里 `-color_range` 的值，按 ` -i ` 切成前后两段（输入声明 / 输出声明）──
function DryRunRange([string]$src, [string]$fmt, [string]$extra) {
    $line = DryRunCmdFor $src $fmt $extra
    $h = ''; $t = ''
    if ($line) { $h = ($line -split ' -i ')[0]; $t = ($line -split ' -i ')[1] }
    $hv = @(); $tv = @()
    if ($h) { $hv = @([regex]::Matches($h, '-color_range\s+(\S+)') | ForEach-Object { $_.Groups[1].Value }) }
    if ($t) { $tv = @([regex]::Matches($t, '-color_range\s+(\S+)') | ForEach-Object { $_.Groups[1].Value }) }
    return [pscustomobject]@{ Head = $h; Tail = $t; HeadVals = $hv; TailVals = $tv }
}
function DryRunCmdFor([string]$src, [string]$fmt, [string]$extra) {
    $txt = Run-Exe $exe "--headless -i `"$src`" -o `"$val/out`" -f $fmt -q 100 $extra --dry-run"
    return ($txt -split "`n" | Where-Object { $_ -match '^ffmpeg ' } | Select-Object -First 1)
}
# R0 形态自检：前后两段都必须非空，否则 R1/R2 恒真（空串上 -match 永远不命中，但 -notmatch 恒真）
$r0 = DryRunRange $pc16 'mkv' ''
CK ($r0.Head -and $r0.Tail) "R0 形态自检：dry-run 命令行能被 ` -i ` 切成非空两段（否则下面几条恒真）"
# R1 输入侧：全范围 RGB 源 ⇒ `-i` **之前**声明 pc（此前恒不发射）
CK ($r0.HeadVals -contains 'pc') "R1 输入侧 -color_range pc 出现在 -i 之前（rgb48le 源；此前恒静默）"
# R2 输出侧：同一源转 YUV 容器 ⇒ `-i` **之后**也声明 pc（auto 档跟随输入；此前 auto 档恒 null）
CK ($r0.TailVals -contains 'pc') "R2 输出侧 -color_range pc 出现在 -i 之后（auto 档跟随输入；此前恒 null）"
# R3 负控（有牙）：limited YUV 源必须判 tv —— 否则 R1/R2 可能只是「恒 pc」
$r3 = DryRunRange $limMp4 'mkv' ''
CK ($r3.HeadVals -contains 'tv') "R3 负控：limited 源（yuv420p）输入侧判 tv（防「恒 pc」）"
CK ($r3.TailVals -contains 'tv') "R3 负控：limited 源输出侧也判 tv（auto 档真跟随输入，不是单向硬编码）"
# R4 RGB 原生出口：png 不写输出侧范围（isRgbNativeFmt 分支），但**输入侧照旧**要发声
$r4 = DryRunRange $pc16 'png' ''
CK ($r4.TailVals.Count -eq 0) "R4 png 原生出口不发射输出侧 -color_range（该分支按设计跳过）"
CK ($r4.HeadVals -contains 'pc') "R4 png 出口时输入侧仍声明 pc（出口格式不该改变输入声明）"
# R5 用户显式值优先：pc 源 + `--color-range tv` ⇒ 输出侧 tv（覆盖 auto 跟随）
$r5 = DryRunRange $pc16 'mkv' '--color-range tv'
CK ($r5.TailVals -contains 'tv') "R5 --color-range tv 用户值优先于 auto 跟随（输出侧 tv）"
CK ($r5.TailVals -notcontains 'pc') "R5 用户值优先时不再同时发 pc（不产生两个 -color_range）"
# R6 调色板格式：gif 无色彩范围概念 ⇒ 输出侧不发射
$r6 = DryRunRange $pc16 'gif' ''
CK ($r6.TailVals.Count -eq 0) "R6 gif 输出不发射输出侧 -color_range（调色板格式无范围概念）"
# ══════════════ G. WebP 出口范围不得发 pc（2026-11-02 第三批）══════════════
# 背景（实测，`verify-decision-delivery` ⑨ 当场红出来的）：`colorRange` 回拷一落地，webp 出口
# 就从「无范围参数」变成 `-color_range pc`（auto 档跟随 rgb48 源的 pc）。而 WebP **写不进范围标记**
# —— ffprobe 读那份带 pc 的产物仍报 `color_range=tv`，解码端只能按 limited 解释 ⇒ 存储按 pc 摆、
# 解读按 limited 拉 = **像素错位**。同一源（`a2_srgb_p3.png` 512×384，左上 1px = 116,20,244）三档实测：
#   无参数 ⇒ 116,16,237（对源 PSNR 31.88 dB）；`-color_range tv` ⇒ 116,16,237（与无参数逐像素等价）；
#   `-color_range pc` ⇒ 104,9,240（PSNR 28.22 dB，丢 3.7 dB）。⇒ 出口口径 = limited 存储 + limited 解读。
# 旧代码反向回退（`webp` 且 outRange=tv 时改 pc）已删 —— 下面 R7/R8 就是它的双向锁。
Say "[G] WebP 出口范围口径（容器无范围标记 ⇒ 必须与解码端的 limited 假设一致）"
$r7 = DryRunRange $pc16 'webp' ''
CK ($r7.TailVals -notcontains 'pc') "R7 webp 输出侧「不发」 pc（auto 档跟随 pc 源时也得压回 tv；实测 pc 会丢 3.7 dB）"
CK ($r7.TailVals -contains 'tv') "R7 webp 输出侧显式声明 tv（不靠「省略即 tv」的巧合，让命令行自证口径）"
$r7b = DryRunRange $pc16 'webp' '--color-range pc'
CK ($r7b.TailVals -notcontains 'pc') "R7b 用户显式 --color-range pc 时 webp 仍不发 pc（容器无法告知解码端，发了就是错位）"
# R7b 正向那一半（与 R7 同构）：只有上面那条负向断言的话，dry-run 拿不到命令行时 TailVals=@()
#   ⇒ `-notcontains 'pc'` 恒真，本条会永远 PASS 而永不可知（2026-11-03 审阅指出）
CK ($r7b.TailVals -contains 'tv') "R7b' 同一档同时确实发 tv（实=$($r7b.TailVals -join ',')）—— 没有这半条，上行可因空集恒真"
# R8 像素侧有牙：用产品那条命令的真实形态把同一源编码两次，比读回值
#   （只判命令行文本不够 —— 上面 R7 只证「没发 pc」，这里证「读回的像素确实与 tv 档一致」）
$pxSrc = "$val/px_src.png"
Run-Exe $ff "-y -v error -f lavfi -i `"color=c=0xFF14F4:s=64x48,format=rgb24`" -frames:v 1 `"$pxSrc`"" | Out-Null
# 取左上 1px 的「存储值」（`format=rgb24` 必须在 `crop` 前，否则从 yuv420p 裁 1×1 会输出 0 字节）
function PxOf([string]$path) {
    $raw = "$val/_px.raw"
    Run-Exe $ff "-y -hide_banner -loglevel error -i `"$path`" -vf `"format=rgb24,crop=1:1:0:0`" -frames:v 1 -f rawvideo `"$raw`"" | Out-Null
    if (-not (Test-Path $raw)) { return $null }
    $b = [System.IO.File]::ReadAllBytes($raw); Remove-Item $raw -Force -ErrorAction SilentlyContinue
    if ($b.Length -lt 3) { return $null }
    return "$($b[0]),$($b[1]),$($b[2])"
}
$wNone = "$val/w_none.webp"; $wPc = "$val/w_pc.webp"; $wTv = "$val/w_tv.webp"
Run-Exe $ff "-y -hide_banner -loglevel error -color_range pc -i `"$pxSrc`" -q:v 75 -pix_fmt yuv420p -colorspace bt470bg `"$wNone`"" | Out-Null
Run-Exe $ff "-y -hide_banner -loglevel error -color_range pc -i `"$pxSrc`" -q:v 75 -pix_fmt yuv420p -color_range pc -colorspace bt470bg `"$wPc`"" | Out-Null
Run-Exe $ff "-y -hide_banner -loglevel error -color_range pc -i `"$pxSrc`" -q:v 75 -pix_fmt yuv420p -color_range tv -colorspace bt470bg `"$wTv`"" | Out-Null
$pNone = PxOf $wNone; $pPc = PxOf $wPc; $pTv = PxOf $wTv
CK ($pNone -and $pPc -and $pTv) "R8 形态自检：三档 webp 都读得出像素（无=$pNone ｜ pc=$pPc ｜ tv=$pTv）——读不出则下面两条恒真"
CK ($pTv -eq $pNone) "R8 像素侧：tv 声明与不声明逐像素等价（实 tv=$pTv ｜ 无=$pNone）⇒ 「压回 tv」不改像素"
CK ($pPc -ne $pNone) "R8 负控（有牙）：pc 声明「确实」改变读回像素（实 pc=$pPc vs 无=$pNone）⇒ 本条不是空跑（若哪天 pc/tv 同效，R7 会变成无害约束，这条先红）"
# R8-PSNR：把「pc 到底差多少」变成**每轮可复算的读数**，不让文档里的 dB 数字失去出处
#   （ffmpeg 自带 psnr 滤镜，省得手写逐像素循环）
function PsnrOf([string]$a, [string]$b) {
    $txt = Run-Exe $ff "-hide_banner -i `"$a`" -i `"$b`" -lavfi psnr -f null -"
    $m = [regex]::Match($txt, 'average:\s*([0-9.]+)')
    if ($m.Success) { return [double]$m.Groups[1].Value }
    return -1
}
$qNone = PsnrOf $pxSrc $wNone
$qPc = PsnrOf $pxSrc $wPc
$qTv = PsnrOf $pxSrc $wTv
Say ("  R8-PSNR（对源 {0}，q75）：无={1:N2} dB ｜ tv={2:N2} dB ｜ pc={3:N2} dB" -f $pNone, $qNone, $qTv, $qPc)
CK ($qNone -gt 0 -and $qPc -gt 0 -and $qTv -gt 0) "R8-PSNR 形态自检：三档都算得出 PSNR（无=$qNone tv=$qTv pc=$qPc）—— 算不出则下两条恒真"
CK ([math]::Abs($qNone - $qTv) -lt 0.01) "R8-PSNR 双向：tv 档与不发档 PSNR 相同（$qNone vs $qTv）⇒ 压回 tv 对画质中性"
CK (($qNone - $qPc) -ge 1.5) "R8-PSNR 有牙：pc 档对源至少差 1.5 dB（实丢 $([math]::Round($qNone - $qPc, 2)) dB）⇒ 「发 pc 会掉画质」不是主观描述"

# ═══ H. JPEG 显式 tv 必须真出图（2026-11-03）═══
# 背景：删掉「`fmt is jpg/jpeg/webp` 且 `outRange=="tv"` ⇒ `outRange="pc"`」那条反向分支后，
#   jpg 一旦**真的**发 `-color_range tv`，mjpeg 就把 limited YUV 判作非标准而**拒编** ⇒ **零产物**
#   （2026-11-03 实测 exit=1，stderr `Non full-range YUV is non-standard, set strict_std_compliance to
#   at most unofficial to use it.` + `Nothing was written into output file`）。HEAD 靠「强制改 pc」
#   挡它，代价是用户的 tv 被**静默丢弃**（ffmpeg 把 pix_fmt 自动升成 `yuvj420p` = full range；
#   源 116,20,244 ⇒ 读回 116,17,239，像素没错但语义错）。现口径 = 照发 tv 并加 `-strict unofficial` 放行：
#   产物真写 limited（ffprobe 读回 `color_range=tv`，`pix_fmt=yuv420p`；读回 120,16,245）。
#   ⚠ 放行**只限显式** `--color-range tv`；auto 档（用户没选）遇 limited 输入必须压回 pc ——
#   见下面 I 段（审阅 M1：auto 放行 = 默认路径发灰，webp/avif/视频帧 → jpg 全中招）。
Say "[H] JPEG 显式 tv：既不零产物，也不静默改 pc"
$r9 = DryRunRange $pc16 'jpg' '--color-range tv'
CK ($r9.TailVals -contains 'tv') "R9a jpg 显式 --color-range tv 时输出侧确实发 tv（实=$($r9.TailVals -join ',')）"
CK ($r9.TailVals -notcontains 'pc') "R9b jpg 不被静默改回 pc（那是 HEAD 的行为，会把 tv 请求吞掉）"
CK ($r9.Tail -match '-strict\s+unofficial') "R9c 同时带 -strict unofficial（没有它 mjpeg 拒编 limited YUV ⇒ 零产物）"
# R9d/R9e 真跑 + 负控：证明 R9c 那面旗子是承重的，不是装饰
$jOk = "$val/j_ok.jpg"; $jBad = "$val/j_bad.jpg"
Run-Exe $ff "-y -hide_banner -loglevel error -i `"$pxSrc`" -q:v 2 -pix_fmt yuv420p -color_range tv -strict unofficial `"$jOk`"" | Out-Null
Run-Exe $ff "-y -hide_banner -loglevel error -i `"$pxSrc`" -q:v 2 -pix_fmt yuv420p -color_range tv `"$jBad`"" | Out-Null
CK ((Test-Path $jOk) -and ((Get-Item $jOk).Length -gt 0)) "R9d 带 -strict 时真出图（$((Get-Item $jOk -ErrorAction SilentlyContinue).Length) B）"
CK (-not (Test-Path $jBad)) "R9e 负控（有牙）：不带 -strict 的同形命令确实零产物 ⇒ R9c 不是空话（哪天 ffmpeg 放宽了，这条先红）"

# R9f 放行必须**出声**（2026-11-03 审阅 M1 配套）：JPEG 无范围载体，显式 tv 的产物在
#   libjpeg/WIC/浏览器里会按满量程直取而发灰 —— 用户必须被告知这一点，不许静默交付。
$r9txt = Run-Exe $exe "--headless -i `"$pc16`" -o `"$val/out`" -f jpg -q 100 --color-range tv --dry-run"
CK ($r9txt -match 'washed out') "R9f 显式 tv 放行有 EmitWarn 播报（文案含 washed out）——静默放行 = 静默发灰"

# ══════════════ I. jpg auto 档不得跟随 limited 输入（2026-11-03 第四批：审阅 M1）══════════════
# 背景：ResolveOutputColorRange 的 auto 档跟随输入推断（yuv* ⇒ tv）；若把 tv 原样发给 jpg，
#   产物 = limited 存储 + 零范围载体（JFIF 没有任何范围标记，ffprobe 的 tv 是从 pix_fmt 反推的）
#   ⇒ libjpeg/WIC/浏览器按满量程直取 ⇒ 发灰（审阅实测 webp(tv)→jpg 默认参数：PIL 对源
#   45.16 dB → 25.67 dB）。⇒ auto 档必须压回 pc（HEAD 语义，yuvj 满量程 = JPEG 事实标准），
#   显式 tv 才放行（H 段）。判据四层：命令行（R10a）+ 载体（R10b）+ 像素（R10c）+ 负控（R10d）。
Say "[I] jpg auto 档：limited 输入必须压回 pc（不得把 limited 写进无载体的 JPEG）"
$r10 = DryRunRange $limMp4 'jpg' ''
CK ($r10.TailVals -contains 'pc' -and $r10.TailVals -notcontains 'tv') "R10a jpg auto 档遇 limited 输入压回 pc（实=$($r10.TailVals -join ',')）⇒ 不把 limited 写进无载体 JPEG"
CK ($r10.Tail -notmatch '-strict') "R10a' auto 档不带 -strict（放行是显式 tv 专属；auto 出现 strict 说明压回失效）"
$ffprobe = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
# ⚠ `-o` 是**输出目录**语义（CLI 按源名命名产物，lim.mp4 ⇒ lim.jpg；与 verify-jpg-webp-matrix
#   `:105` 的 `-o "$dir"` 同款）。传「文件路径」会被 CLI 建成**同名目录**（2026-11-03 实测踩坑：
#   auto.jpg/ 目录里躺着 lim.jpg，ffprobe 对目录报 Permission denied ⇒ R10b/c 假红）。
$autoDir = "$val/auto_out"
Run-Exe $exe "--headless -i `"$limMp4`" -o `"$autoDir`" -f jpg -q 92 --log-level Debug" | Out-Null
$autoJpg = Get-ChildItem -Recurse $autoDir -Include *.jpg -EA SilentlyContinue | Select-Object -First 1
$pf = ''
if ($autoJpg) {
    $pf = (Run-Exe $ffprobe "-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 `"$($autoJpg.FullName)`"").Trim()
}
CK (($autoJpg -ne $null) -and ($pf -match '^yuvj')) "R10b auto 档产物是满量程载体（pix_fmt=$pf，须 yuvj*）⇒ 与 HEAD 语义一致"
# R10c 像素臂（同文件恒等式）：`setparams=range=pc` 重标 = libjpeg/WIC「按满量程直取」的模拟。
#   判据 = 同一产物的「普通解码」与「重标解码」必须恒等 —— yuvj（full）产物对重标免疫（inf ⇒ 99
#   哨兵），limited 产物会被重标拉满（R10d 实测 24.67 dB）。⇒ 与 JPEG 画质（q 值/4:2:0 色度抽样）
#   完全解耦：2026-11-03 首版拿「对源 PSNR ≥38」当判据是错的——testsrc2 的色度细节让正确产物也只有
#   33.52 dB，那 ~4.5 dB 是抽样损失不是范围错误；范围错误由恒等式抓，画质损失由 R10d 的标尺抓。
$refRaw = "$val/_i_ref.raw"; $prdRaw = "$val/_i_prd.raw"; $prdRl = "$val/_i_prd_rl.raw"
Run-Exe $ff "-y -hide_banner -loglevel error -i `"$limMp4`" -frames:v 1 -f rawvideo -pix_fmt rgb24 `"$refRaw`"" | Out-Null
if ($autoJpg) {
    Run-Exe $ff "-y -hide_banner -loglevel error -i `"$($autoJpg.FullName)`" -f rawvideo -pix_fmt rgb24 `"$prdRaw`"" | Out-Null
    Run-Exe $ff "-y -hide_banner -loglevel error -i `"$($autoJpg.FullName)`" -vf `"setparams=range=pc,format=rgb24`" -f rawvideo `"$prdRl`"" | Out-Null
    $qVs = -1.0
    if ((Test-Path $refRaw) -and (Test-Path $prdRl)) {
        $txt = Run-Exe $ff "-hide_banner -f rawvideo -pix_fmt rgb24 -s 64x48 -i `"$refRaw`" -f rawvideo -pix_fmt rgb24 -s 64x48 -i `"$prdRl`" -lavfi psnr -f null -"
        $m = [regex]::Match($txt, 'average:\s*([0-9.]+)')
        if ($m.Success) { $qVs = [double]$m.Groups[1].Value }
        Say ("  R10c 读数：auto 产物按满量程直取对源 {0:N2} dB（4:2:0 抽样损失在内，仅记录不作判据）" -f $qVs)
    }
    $qAuto = -1.0
    if ((Test-Path $prdRaw) -and (Test-Path $prdRl)) {
        $txt = Run-Exe $ff "-hide_banner -f rawvideo -pix_fmt rgb24 -s 64x48 -i `"$prdRaw`" -f rawvideo -pix_fmt rgb24 -s 64x48 -i `"$prdRl`" -lavfi psnr -f null -"
        if ($txt -match 'average:\s*inf') { $qAuto = 99.0 }
        else { $m = [regex]::Match($txt, 'average:\s*([0-9.]+)'); if ($m.Success) { $qAuto = [double]$m.Groups[1].Value } }
    }
    CK ($qAuto -ge 40) "R10c 像素臂：auto 产物对满量程重标免疫（普通解码 vs 重标解码 = $qAuto，99=inf）⇒ 载体真写的是 full range"
}
# R10d 负控（有牙）：同一 limited 产物（H 段 R9d 的显式 tv 出图）重标 pc 后读回**确实**发灰
#   ⇒ 证明 R10c 那把「重标尺」真咬得住 limited 存储，不是对任何产物都放行。
$jRl = "$val/_i_jok_rl.raw"; $jPl = "$val/_i_jok_pl.raw"
Run-Exe $ff "-y -hide_banner -loglevel error -i `"$jOk`" -f rawvideo -pix_fmt rgb24 `"$jPl`"" | Out-Null
Run-Exe $ff "-y -hide_banner -loglevel error -i `"$jOk`" -vf `"setparams=range=pc,format=rgb24`" -f rawvideo `"$jRl`"" | Out-Null
$qWash = -1.0
if ((Test-Path $jRl) -and (Test-Path $jPl)) {
    $txt = Run-Exe $ff "-hide_banner -f rawvideo -pix_fmt rgb24 -s 64x48 -i `"$jPl`" -f rawvideo -pix_fmt rgb24 -s 64x48 -i `"$jRl`" -lavfi psnr -f null -"
    $m = [regex]::Match($txt, 'average:\s*([0-9.]+)')
    if ($m.Success) { $qWash = [double]$m.Groups[1].Value }
}
CK ($qWash -ge 0 -and $qWash -le 38) "R10d 负控（有牙）：limited 产物重标 pc 后读回确实发灰（对正确解码 $qWash dB，须 <=38）⇒ R10c 的重标尺真咬得住"

Say "`n===== color-token-normalize: PASS=$pass FAIL=$fail ====="
if ($fail -eq 0) { Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue }
else { Say "工作目录保留供排查: $val" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
