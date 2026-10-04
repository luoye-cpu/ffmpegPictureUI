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
    # 输出标注必须服从容器能力：jpg 的能力表写明矩阵只能 bt709（写别的值 mjpeg 会静默忽略）
    CK ($tail -match '-colorspace bt709') "E2 输出标注服从 capM=bt709（用户选 $m，jpg 实际标注 bt709）"
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

Say "`n===== color-token-normalize: PASS=$pass FAIL=$fail ====="
if ($fail -eq 0) { Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue }
else { Say "工作目录保留供排查: $val" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
