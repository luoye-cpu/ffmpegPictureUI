# _lib-matrix.ps1 -- 穷举组合测试地基④：L2 两两正交（pairwise）+ L3 语义真矩阵（P2-A / P2-F）
#
# 定位（为什么是「库」而不是「门禁」）：
#   本文件**不产 PASS/FAIL 汇总门禁**，只被 dot-source 复用（写法对齐同目录
#   `_lib-axes.ps1` / `_lib-oracle.ps1` / `_lib-reject.ps1`）。它回答两个问题：
#     · L2（P2-F）：57 条轴里「任意两条轴的任意取值组合」如何用**远小于全笛卡尔积**的
#       用例集覆盖到（正交/两两覆盖），且这个性质是**被逐组合核验过的**，不是自称。
#     · L3（P2-A）：哪些轴**语义不独立**、必须做真笛卡尔积（全交叉），以及折叠掉语义等价
#       取值之后每张矩阵还剩多少格。
#
# 五条对外能力：
#   Get-EquivalenceClasses -- 等价类折叠表（哪些轴的哪些取值语义等价），**每条附 file:line 取证**
#   Get-PairwiseCases      -- L2 两两正交用例集（确定性贪心，无随机；Args 可直接喂 ConvertTo-CliArgs）
#   Get-SemanticMatrices   -- L3 真矩阵（Name/Axes/Cases/Rationale/Evidence/RawCount/FoldedCount）
#   Test-MatrixStructure   -- 结构自检（自检与负控共用同一个校验器 => 负控证明的就是「它有牙」）
#   Invoke-MatrixSelfTest  -- 自检，末行 `MATRIX RESULT: pass=n fail=0`
#   另：Get-MatrixPairwiseAxes / Get-MatrixCanonicalAssignment / Get-MatrixRepoRoot / Read-MatrixSourceLines
#
# ── 四条设计铁律（照抄既有三库）──────────────────────────────────────────────────
#  ① **不用「我自己写的常量表」验证「我自己写的解析」**：
#     折叠表每条都带 `Evidence`（`file:line|字面片段`）。自检会**真的去读那个文件的第 N 行**，
#     断言该字面片段**出现在那一行上**。定位**按行号**（不是全文件 Contains）——本仓教训：
#     「载体选错 => 断言恒真」。读文件失败 => 显式失败（`Ok=$false` + 非空 Error），
#     绝不返回空串（否则「读不出来」会被当成「那一行没有」而把红变绿）。
#  ② **两两覆盖性质必须逐组合核验**：`Test-MatrixStructure` 从用例的 `Assignments`
#     **独立重算**覆盖集（不读生成器内部记账），再对「参与轴的每一对 × 每一对取值」
#     逐条查表，任一缺失即红。生成器的记账只是性能手段，不是判据。
#  ③ **L2 用例数必须显著小于全笛卡尔积**（全积按 log10 比较，避免 double 溢出），否则 L2 没降维。
#  ④ **注入式变异必须打在主跑那一份数据上**：`-InjectBadPair` 在**主用例集**上删掉
#     「覆盖某个指定取值组合的全部用例」=> 主跑那一份校验器的 fail 计数当场 > 0。
#     不做「内部另跑一趟」那种自述式变异（本仓教训：外层读数仍是 fail=0，那不是证据）。
#
# ── 折叠表的取证结论（2026-09-19 逐条打开源码核对，非转述）─────────────────────
#   F1 `--icc-mode` 4 值 -> 3 策略（无条件折叠）
#      `Models/ColorStrategy.cs:73-79` ColorStrategyMapper.FromIccMode：
#      None -> Recommended（走 `_ =>` 默认分支，:52）、BakeToStandard -> Recommended（:51）、
#      CarryIcc -> CarryIcc（:49）、BakeOnly -> BakeCicpOnly（:50）。
#   F2 `-f jpegli` 与 `-f jpg` 等价（无条件折叠）
#      `CliParser.cs:150` `if (fmt == "jpegli") fmt = "jpg";`（避免产出 `.jpegli` 非法扩展名）。
#   F3 `--bit-depth` 在 format ∈ {jpg,jpeg} 下 4 值 -> 8（**条件折叠**）
#      `FfmpegCommandBuilder.Decision.cs` 的 capBd 分支按容器上限**就地改写 options.BitDepth**；
#      上限来自 `GetFormatColorCapabilities` 的 jpg/jpeg 行（-> 8）。改写是单点的 => 下游只见 8。
#   F4 `--bit-depth` 在 format = webp 下 4 值 -> 8（**条件折叠**）
#      同上那段就地改写 + `GetFormatColorCapabilities` 的 webp 行（-> 8）。
#      ⚠ 精确行号**只写在各自的 Evidence 字段里**（A21/A30 按行锚定校验，行号漂了会当场响）；
#        散文里不要再复写一遍数字 —— 本轮实测：散文里的 :867/:877/:486/:1871 全都漂了，
#        而机器校的那份也漂了 6 条（见文件末尾 N7 的补充登记）。
#   F5 ~~`--bit-depth` 在 format = avif 且 --color-space = "Display P3" 下 4 值 -> 8~~
#      **2026-09-26 任务 #39 删除该折叠**：它的取证行是产品里
#      `return ("bt709", "iec61966-2-1", "bt709", 8, false)` 那条"P3 高位深 ⇒ 回退 8bit"特例，
#      而该特例的理由（"libaom 上 P3+10/12/16 有回归"）**从未被量过**。补量后不成立
#      （同一命令行只换 -pix_fmt：8/10/12 三档 rc=0、nclx 标签一致、10/12 对 8 的 PSNR 51.4/51.0 dB，
#      取证 `tests/output/t36/probe_39b.ps1`）⇒ 产品已删特例，四个取值**不再等价**。
#      ⇒ 覆盖账是**变多**不是变少：avif+Display P3 的 bit-depth 从"1 格代表 4 档"变成 4 档各跑。
#
# ── 已考察但**故意不折叠**的候选（防过度折叠；详见文件末尾 NOTES）─────────────────
#   N1 `-f jpeg` 与 `-f jpg`：编码器选择相同（`EncoderDetectionService.cs:38`），
#      但 `CliParser.BuildOutputPath` 用 Format 原样拼扩展名 => `.jpeg` vs `.jpg` 产物名不同，
#      **不等价**（只折编码器语义、不折产物名，会掩盖扩展名缺陷）。
#   N2 `--color-engine auto` 与 `engine`：`Requested`（`ColorEngineRouter.cs` 的
#      `public static bool Requested`，2026-09-27 在 :100-102）对二者
#      同为真，但 `ExplicitlyRequested`（同文件 `public static bool ExplicitlyRequested`）只认字面 engine => 不可走时
#      「硬失败并点名」vs「回退传统管线」两种结局，**不等价**。
#   N3 `--color-space "P3 PQ"` 与 `"BT.2020 PQ"`：输出 CICP tokens 相同，但前者 Kind=UltraWide
#      => 额外做源->BT.2020 像素 gamut 转换（`ColorSpaceRegistry.cs:94-98` vs :106-109），**不等价**。
#   N4 `--chroma auto` / `--color-range auto`：语义是「不下发/跟随输入」（`FfmpegCommandBuilder.cs:464`、
#      :936-946），等价物是「不设该轴」而非同轴另一个取值 => 不进折叠表（折叠表的定义域是同轴取值）。
#
# ⚠ 双宿主（PS 5.1 / PS 7）：禁 `? :` / `??` / `?.` / `&&` / `||`；用 `$(if (...) {...} else {...})`。
#   门禁：`tests/scripts/verify-ps-compat.ps1`。
# ⚠ 代码串纯 ASCII：所有字符串字面量（含 Rationale / 打印消息 / Evidence 片段）不得含中文；
#   中文只允许出现在注释里（注释被 tokenizer 剥离，不占 CJK 门禁余量）。
# ⚠ 行尾自洽：全文件 LF。
# ⚠ dot-source 时**零副作用**（不打印、不建临时目录、不起进程）。
#
# 用法：
#   . "$PSScriptRoot/_lib-matrix.ps1"
#   $cases = @(Get-PairwiseCases)                 # 默认 = 全部非 path 轴
#   $m     = @(Get-SemanticMatrices)
#   自检： pwsh -File tests/scripts/_lib-matrix.ps1 -SelfTest
#   变异： pwsh -File tests/scripts/_lib-matrix.ps1 -SelfTest -InjectBadPair
#          => 主用例集被删掉覆盖某个取值组合的全部用例 => A7（两两覆盖）转红。

param(
    [switch]$SelfTest,
    [switch]$InjectBadPair
)

# 脚本目录在**文件作用域**取一次：函数里再用 $PSScriptRoot 在 dot-source 场景下语义不稳。
$script:MatrixScriptDir = $PSScriptRoot

# ⚠⚠ 陷阱（本库实测踩到，2026-09-19；这段必须保留）：
#   `_lib-axes.ps1` 顶部有它自己的 `param([switch]$SelfTest, [switch]$InjectEmptyAxis)`。
#   而 dot-source 是在**调用方作用域**里逐语句执行的 => 它的 param 块会把自己那两个开关的
#   **默认值**（$false）写进本作用域，从而**覆盖本文件的 $SelfTest**。
#   症状：`pwsh -File tests/scripts/_lib-matrix.ps1 -SelfTest` **一行都不打印、退出码 0**
#   （自检被静默跳过）——正是本仓最忌讳的「假绿」形态。
#   处置：① 先把本文件的开关快照进 $script: 变量（下面的 if 用快照，不读被覆盖后的 $SelfTest）；
#         ② dot-source 前后恢复/清理可能被覆盖的调用方变量，保持「dot-source 零副作用」。
$script:MatrixRunSelfTest = [bool]$SelfTest
$script:MatrixRunInjectBadPair = [bool]$InjectBadPair

$script:MatrixHadSelfTest = Test-Path -Path 'variable:SelfTest'
$script:MatrixPrevSelfTest = $null
if ($script:MatrixHadSelfTest) { $script:MatrixPrevSelfTest = $SelfTest }
$script:MatrixHadInjectEmpty = Test-Path -Path 'variable:InjectEmptyAxis'
$script:MatrixPrevInjectEmpty = $null
if ($script:MatrixHadInjectEmpty) { $script:MatrixPrevInjectEmpty = $InjectEmptyAxis }

# 复用 L1 库：轴注册表 + token 覆盖表 + ConvertTo-CliArgs + Format-AxesToken。
# 若调用方已 dot-source 过 `_lib-axes.ps1` 则跳过（避免重复定义，也避免上面那个覆盖）。
if (-not (Get-Command -Name 'Get-AxisRegistry' -ErrorAction SilentlyContinue)) {
    . (Join-Path $script:MatrixScriptDir '_lib-axes.ps1')
    if ($script:MatrixHadSelfTest) { $SelfTest = $script:MatrixPrevSelfTest }
    else { Remove-Variable -Name 'SelfTest' -ErrorAction SilentlyContinue }
    if ($script:MatrixHadInjectEmpty) { $InjectEmptyAxis = $script:MatrixPrevInjectEmpty }
    else { Remove-Variable -Name 'InjectEmptyAxis' -ErrorAction SilentlyContinue }
}

# ══════════════════════════════════════════════════════════════════════════════
# 0) 底层工具
# ══════════════════════════════════════════════════════════════════════════════

# 仓库根：本文件位于 <repo>/tests/scripts/_lib-matrix.ps1 => 上两级。
function Get-MatrixRepoRoot {
    [CmdletBinding()]
    param()
    $p1 = Split-Path -Parent $script:MatrixScriptDir
    $p2 = Split-Path -Parent $p1
    return $p2
}

# 读文本并按行返回。**读失败显式失败**（Ok=$false + 非空 Error），绝不返回空串。
# 显式指定 UTF-8：PS 5.1 的默认编码受系统 codepage 影响，中文注释行可能被错误解码，
# 而本库的 Evidence 字面片段是纯 ASCII —— 显式 UTF-8 让双宿主读到的文本一致。
# 返回 @{ Ok; Lines; Error }。
function Read-MatrixSourceLines {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $res = @{ Ok = $false; Lines = @(); Error = '' }
    if ([string]::IsNullOrWhiteSpace($Path)) { $res.Error = 'empty path'; return $res }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $res.Error = ('no such file: {0}' -f $Path)
        return $res
    }
    $text = $null
    try {
        $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    } catch {
        $res.Error = ('read failed: {0}' -f $_.Exception.Message)
        return $res
    }
    if ($null -eq $text) { $res.Error = ('read returned null: {0}' -f $Path); return $res }
    $norm = $text -replace "`r`n", "`n"
    $lines = @($norm -split "`n")
    $res.Ok = $true
    $res.Lines = $lines
    return $res
}

# 校验一条 Evidence（`<repo 相对路径>:<行号>|<字面片段>`）。
# 判据：文件可读、行号在范围内、且该字面片段**出现在那一行上**（按行号定位，不做全文件匹配）。
# 返回 @{ Ok; Detail }。
function Test-MatrixEvidenceEntry {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Entry)

    $out = @{ Ok = $false; Detail = '' }
    if ([string]::IsNullOrWhiteSpace($Entry)) { $out.Detail = 'empty evidence entry'; return $out }
    $bar = $Entry.IndexOf('|')
    if ($bar -lt 1) { $out.Detail = ('evidence entry lacks "|": {0}' -f $Entry); return $out }
    $loc = $Entry.Substring(0, $bar)
    $lit = $Entry.Substring($bar + 1)
    if ([string]::IsNullOrEmpty($lit)) { $out.Detail = ('evidence literal empty: {0}' -f $loc); return $out }
    $colon = $loc.LastIndexOf(':')
    if ($colon -lt 1) { $out.Detail = ('evidence loc lacks ":<line>": {0}' -f $loc); return $out }
    $rel = $loc.Substring(0, $colon)
    $ln = 0
    if (-not [int]::TryParse($loc.Substring($colon + 1), [ref]$ln)) {
        $out.Detail = ('evidence line not an int: {0}' -f $loc)
        return $out
    }
    if ($ln -lt 1) { $out.Detail = ('evidence line < 1: {0}' -f $loc); return $out }

    $full = Join-Path (Get-MatrixRepoRoot) $rel
    $rd = Read-MatrixSourceLines -Path $full
    if (-not $rd.Ok) { $out.Detail = ('{0} ({1})' -f $rd.Error, $rel); return $out }
    $all = @($rd.Lines)
    if ($ln -gt $all.Count) {
        $out.Detail = ('line {0} beyond EOF ({1} lines) in {2}' -f $ln, $all.Count, $rel)
        return $out
    }
    $line = [string]$all[$ln - 1]
    if ($line.IndexOf($lit) -lt 0) {
        # 诊断增强（不削弱断言）：字面片段不在该行时，报出它在文件里的**实际行号**，
        # 让「源码位移」与「片段写错」一眼可分。定位仍然是行号锚定的 —— 不因为别处存在就放行。
        $elsewhere = 0
        for ($k = 0; $k -lt $all.Count; $k++) {
            if ([string]$all[$k].IndexOf($lit) -ge 0) { $elsewhere = $k + 1; break }
        }
        $hint = 'not found anywhere in file'
        if ($elsewhere -gt 0) { $hint = ('found at line {0}' -f $elsewhere) }
        $out.Detail = ('literal not on {0}:{1} ({2}) -> "{3}"' -f $rel, $ln, $hint, $lit)
        return $out
    }
    $out.Ok = $true
    $out.Detail = ('{0}:{1}' -f $rel, $ln)
    return $out
}

# 造一条折叠记录。字段固定 7 个（Axis/ClassId/Representative/Members/When/Evidence/Rationale）。
# `-Members @()` 与 `-When @{}` 必须能表达（负控要用），故显式判 $null 而不是 `@($x)`。
function New-FoldClass {
    param(
        [Parameter(Mandatory = $true)][string]$Axis,
        [Parameter(Mandatory = $true)][string]$ClassId,
        [Parameter(Mandatory = $true)][string]$Representative,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Members,
        $When,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Evidence,
        [Parameter(Mandatory = $true)][string]$Rationale
    )
    $m = @(); if ($null -ne $Members) { $m = @($Members) }
    $w = @{}; if ($null -ne $When) { $w = $When }
    $e = @(); if ($null -ne $Evidence) { $e = @($Evidence) }
    return [pscustomobject]@{
        Axis           = $Axis
        ClassId        = $ClassId
        Representative = $Representative
        Members        = $m
        When           = $w
        Evidence       = $e
        Rationale      = $Rationale
    }
}

# `When` 是否被当前赋值满足：When 的每个键都必须出现在 Assignment 里，且取值在允许集内。
# When 为空 => 无条件成立。
function Test-FoldWhen {
    param($When, $Assignment)
    if ($null -eq $When) { return $true }
    if ($null -eq $Assignment) { return $false }
    $keys = @($When.Keys)
    if ($keys.Count -eq 0) { return $true }
    foreach ($k in $keys) {
        if (-not $Assignment.Contains([string]$k)) { return $false }
        if (@($When[$k]) -notcontains [string]$Assignment[[string]$k]) { return $false }
    }
    return $true
}

# 把一份赋值按折叠表**迭代到不动点**（条件折叠的 When 可能依赖被另一条折叠改写的轴，
# 例如 format 先由 jpegli 折成 jpg，bit-depth 的 jpg 条件才会成立）。
# 返回 [ordered] 的规范化赋值；最多 8 轮（表内无环，轮数上限只是防御）。
function Get-MatrixCanonicalAssignment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Assignment,
        $Folds
    )
    if ($null -eq $Folds) { $Folds = @(Get-EquivalenceClasses) }
    $cur = [ordered]@{}
    foreach ($k in @($Assignment.Keys)) { $cur[[string]$k] = [string]$Assignment[$k] }
    $foldList = @($Folds)
    for ($it = 0; $it -lt 8; $it++) {
        $changed = $false
        foreach ($f in $foldList) {
            if (-not $cur.Contains([string]$f.Axis)) { continue }
            if (-not (Test-FoldWhen -When $f.When -Assignment $cur)) { continue }
            $v = [string]$cur[[string]$f.Axis]
            if (@($f.Members) -notcontains $v) { continue }
            if ($v -ne [string]$f.Representative) {
                $cur[[string]$f.Axis] = [string]$f.Representative
                $changed = $true
            }
        }
        if (-not $changed) { break }
    }
    return $cur
}

# 笛卡尔积的索引元组（迭代进位，无递归；确定性顺序）。
# 返回 int[] 的数组；任一维计数 <= 0 时返回空数组。
function Get-MatrixIndexTuples {
    [CmdletBinding()]
    param([int[]]$Counts)
    $res = New-Object System.Collections.ArrayList
    $c = @($Counts)
    $m = $c.Count
    if ($m -eq 0) { return @() }
    foreach ($x in $c) { if ($x -le 0) { return @() } }
    $idx = New-Object 'int[]' $m
    while ($true) {
        [void]$res.Add(@($idx))
        $t = $m - 1
        while ($t -ge 0) {
            $idx[$t] = $idx[$t] + 1
            if ($idx[$t] -lt [int]$c[$t]) { break }
            $idx[$t] = 0
            $t--
        }
        if ($t -lt 0) { break }
    }
    return $res.ToArray()
}

# 一条用例是否同时取到 (AxisA=ValueA, AxisB=ValueB)。
function Test-MatrixCaseCovers {
    param($Case, [string]$AxisA, [string]$ValueA, [string]$AxisB, [string]$ValueB)
    $as = $Case.Assignments
    if (-not $as.Contains($AxisA)) { return $false }
    if (-not $as.Contains($AxisB)) { return $false }
    if ([string]$as[$AxisA] -ne $ValueA) { return $false }
    if ([string]$as[$AxisB] -ne $ValueB) { return $false }
    return $true
}

# 取一个 CLI token 的「键」部分（用于查重键）；非键 token 返回 $null。
function Get-MatrixCliKey {
    param([string]$Token)
    if ([string]::IsNullOrEmpty($Token)) { return $null }
    if ($Token -notmatch '^-{1,2}[A-Za-z]') { return $null }
    $eq = $Token.IndexOf('=')
    if ($eq -gt 0) { return $Token.Substring(0, $eq) }
    return $Token
}

# ══════════════════════════════════════════════════════════════════════════════
# 1) 等价类折叠表
# ══════════════════════════════════════════════════════════════════════════════

function Get-EquivalenceClasses {
    [CmdletBinding()]
    param()

    # ── F1 --icc-mode：4 个取值 -> 3 种策略（无条件）──
    $f1a = @{
        Axis = 'icc-mode'
        ClassId = 'icc-mode-recommended'
        Representative = 'None'
        Members = @('None', 'BakeToStandard')
        Evidence = @(
            'src/FfmpegGui/Models/ColorStrategy.cs:77|IccMode.BakeToStandard => ColorStrategy.Recommended,',
            'src/FfmpegGui/Models/ColorStrategy.cs:78|_ => ColorStrategy.Recommended,'
        )
        Rationale = 'FromIccMode maps BakeToStandard and the default branch (None/unknown) to ColorStrategy.Recommended'
    }
    $f1b = @{
        Axis = 'icc-mode'
        ClassId = 'icc-mode-carry'
        Representative = 'CarryIcc'
        Members = @('CarryIcc')
        Evidence = @('src/FfmpegGui/Models/ColorStrategy.cs:75|IccMode.CarryIcc => ColorStrategy.CarryIcc,')
        Rationale = 'FromIccMode maps CarryIcc to ColorStrategy.CarryIcc'
    }
    $f1c = @{
        Axis = 'icc-mode'
        ClassId = 'icc-mode-cicp-only'
        Representative = 'BakeOnly'
        Members = @('BakeOnly')
        Evidence = @('src/FfmpegGui/Models/ColorStrategy.cs:76|IccMode.BakeOnly => ColorStrategy.BakeCicpOnly,')
        Rationale = 'FromIccMode maps BakeOnly to ColorStrategy.BakeCicpOnly'
    }

    # ── F2 -f jpegli 与 -f jpg（无条件）──
    $f2a = @{
        Axis = 'format'
        ClassId = 'format-jpg'
        Representative = 'jpg'
        Members = @('jpg', 'jpegli')
        Evidence = @('src/FfmpegGui/CliParser.cs:150|if (fmt == "jpegli") fmt = "jpg";')
        Rationale = 'CliParser normalizes format jpegli to jpg, so -f jpegli and -f jpg select the same container'
    }

    # ── F3 bit-depth 在 jpg/jpeg 下 4 值 -> 8（条件）──
    $f3 = @{
        Axis = 'bit-depth'
        ClassId = 'bit-depth-jpeg8'
        Representative = '8'
        Members = @('8', '10', '12', '16')
        When = @{ 'format' = @('jpg', 'jpeg') }
        Evidence = @(
            'src/FfmpegGui/Services/FfmpegCommandBuilder.Decision.cs:147|if (options.BitDepth.HasValue && options.BitDepth.Value > capBd)',
            'src/FfmpegGui/Services/FfmpegCommandBuilder.Decision.cs:155|options.BitDepth = capBd;',
            # ⚠ pin 演进（2026-10-04）：966 → **977**、976 → **987**（+11）
            #   成因 = `FfmpegCommandBuilder.BuildColorArgsSplit` 顶部新增「RAW 中间件输入声明」分支
            #   （`ColorSourceDecl*`，2026-10-03 RAW 默认三档改动），插在本文件之前。
            #   已按 `grep -n` 复核实况、并确认两处字面量全文件唯一。
            'src/FfmpegGui/Services/FfmpegCommandBuilder.cs:981|options.JpegGainMap ? null : "iec61966-2-1", "bt709", 8, true'   # 2026-10-04 重钉 977→981（早前会话 +4 行，grep -n 复核实况 + 字面量唯一）
        )
        Rationale = 'JPEG container caps bit depth at 8 and DecideOutputColor rewrites options.BitDepth in place, so 8/10/12/16 are observationally equal'
    }

    # ── F4 bit-depth 在 webp 下 4 值 -> 8（条件）──
    $f4 = @{
        Axis = 'bit-depth'
        ClassId = 'bit-depth-webp8'
        Representative = '8'
        Members = @('8', '10', '12', '16')
        When = @{ 'format' = @('webp') }
        Evidence = @(
            'src/FfmpegGui/Services/FfmpegCommandBuilder.Decision.cs:147|if (options.BitDepth.HasValue && options.BitDepth.Value > capBd)',
            'src/FfmpegGui/Services/FfmpegCommandBuilder.Decision.cs:155|options.BitDepth = capBd;',
            'src/FfmpegGui/Services/FfmpegCommandBuilder.cs:991|return (null, "iec61966-2-1", "bt709", 8, true);'   # 2026-10-04 重钉 987→991（同批 +4）
        )
        Rationale = 'WebP container caps bit depth at 8 and DecideOutputColor rewrites options.BitDepth in place, so 8/10/12/16 are observationally equal'
    }

    # ── F5 **已删除**（2026-09-26 任务 #39）──
    # 原先这里登记过一条条件折叠：`bit-depth-avifp3-8`（format=avif 且 color-space="Display P3"
    # 时 8/10/12/16 四个取值都等价于 8），Evidence 指向
    # `FfmpegCommandBuilder.cs|return ("bt709", "iec61966-2-1", "bt709", 8, false);`。
    # 该特例的理由只是注释里那句"Display P3 + 10/12/16-bit 在 libaom 上有回归风险"，全仓无实测记录；
    # 补做的实测（`tests/output/t36/probe_39b.ps1`，拿产品自己那条命令行逐字复用、只换 -pix_fmt）：
    # 8/10/12 三档 rc 都为 0、nclx 标签都落得进去（smpte432/iec61966-2-1/bt709）、
    # 10le 与 12le 对 8bit 产物的 PSNR = 51.45 / 50.99 dB ⇒ 三档**可观测地不同**（交付位深不同）
    # ⇒ 特例已从产品删除，这条折叠随之删除：`--bit-depth` 在 avif+P3 下**不再等价**。
    # ⚠ 覆盖账：删掉这条折叠 = avif+Display P3 的 bit-depth 由"1 格代表 4 档"变成"4 档各跑"
    #   ⇒ L1/L3 的用例数上升（不是下降），`Test-MatrixStructure` 的 A21 取证行也不再指向已删的行。

    return @(
        (New-FoldClass @f1a),
        (New-FoldClass @f1b),
        (New-FoldClass @f1c),
        (New-FoldClass @f2a),
        (New-FoldClass @f3),
        (New-FoldClass @f4)
    )
}

# ══════════════════════════════════════════════════════════════════════════════
# 2) L2 两两正交用例生成
# ══════════════════════════════════════════════════════════════════════════════

# 默认参与轴 = 注册表里所有 Kind != 'path' 的轴（path 轴的取值是文件路径，不参与组合）。
function Get-MatrixPairwiseAxes {
    [CmdletBinding()]
    param($Registry)
    if ($null -eq $Registry) { $Registry = @(Get-AxisRegistry) }
    $names = New-Object System.Collections.ArrayList
    foreach ($a in @($Registry)) {
        if ([string]$a.Kind -eq 'path') { continue }
        [void]$names.Add([string]$a.Name)
    }
    return $names.ToArray()
}

# L2 生成：**确定性贪心**（无随机、无种子；同输入同输出）。
#   1. 枚举全部待覆盖组合：每对轴 (i<j) × 每对取值 (va,vb) —— 有序列 + 成员集双份。
#   2. 每轮取「第一个仍未覆盖的组合」当种子，然后按注册表顺序给其余轴逐个选
#      「与已定轴形成最多未覆盖组合」的取值（平局取先出现的取值 => 确定性）。
#   3. 该用例覆盖到的组合全部标记为已覆盖；循环直到无未覆盖组合（=> 覆盖由构造成立）。
# 生成器**不保证**自己记的账一定对 —— 承重断言在 Test-MatrixStructure 里**独立重算**。
function Get-PairwiseCases {
    [CmdletBinding()]
    param(
        [string[]]$AxisNames,
        [string]$Source = 'fixtures/in.png',
        [string]$OutDir = 'out',
        [string]$IccFile = 'fixtures/p3.icc',
        $Registry,
        $Overrides
    )
    if ($null -eq $Registry) { $Registry = @(Get-AxisRegistry) }
    if ($null -eq $Overrides) { $Overrides = Get-AxisTokenOverrides -IccFile $IccFile }
    $reg = @($Registry)

    if ($null -eq $AxisNames -or @($AxisNames).Count -eq 0) {
        $AxisNames = @(Get-MatrixPairwiseAxes -Registry $reg)
    }

    $sel = New-Object System.Collections.ArrayList
    foreach ($nm in @($AxisNames)) {
        $hit = $null
        foreach ($a in $reg) { if ([string]$a.Name -eq [string]$nm) { $hit = $a; break } }
        if ($null -eq $hit) { throw ('Get-PairwiseCases: unknown axis name: {0}' -f $nm) }
        [void]$sel.Add($hit)
    }
    $n = $sel.Count
    if ($n -lt 2) { throw ('Get-PairwiseCases: need at least 2 axes, got {0}' -f $n) }

    $vals = @()
    foreach ($a in $sel) { $vals += ,@(@($a.Values)) }

    $comboList = New-Object System.Collections.ArrayList
    $uncov = @{}
    for ($i = 0; $i -lt $n; $i++) {
        for ($j = $i + 1; $j -lt $n; $j++) {
            foreach ($va in @($vals[$i])) {
                foreach ($vb in @($vals[$j])) {
                    $k = '{0}|{1}|{2}|{3}' -f $i, $j, [string]$va, [string]$vb
                    [void]$comboList.Add($k)
                    $uncov[$k] = $true
                }
            }
        }
    }

    $base = @('--headless', '-i', $Source, '-o', $OutDir)
    $cases = New-Object System.Collections.ArrayList
    $ptr = 0
    while ($true) {
        while ($ptr -lt $comboList.Count -and -not $uncov.ContainsKey([string]$comboList[$ptr])) { $ptr++ }
        if ($ptr -ge $comboList.Count) { break }
        $seed = [string]$comboList[$ptr]
        $p = $seed -split '\|'
        $assign = @{}
        $assign[[int]$p[0]] = [string]$p[2]
        $assign[[int]$p[1]] = [string]$p[3]

        for ($k = 0; $k -lt $n; $k++) {
            if ($assign.ContainsKey($k)) { continue }
            $best = $null
            $bestScore = -1
            foreach ($v in @($vals[$k])) {
                $score = 0
                foreach ($m in @($assign.Keys)) {
                    # ⚠ 组合键的规范形式是 `<小下标>|<大下标>|<小下标取值>|<大下标取值>`。
                    #   下标交换时**取值必须一起交换**（本库初版只换了下标 => k>m 的查表全部 miss
                    #   => 打分近似失明 => 用例数从 ~10^2 量级虚涨到 1432 条；覆盖仍然完整，
                    #   所以只有「用例数 vs 理论下界」这个读数能发现它）。
                    $a1 = $k; $b1 = [int]$m
                    $v1 = [string]$v; $v2 = [string]$assign[$m]
                    if ($a1 -gt $b1) {
                        $t2 = $a1; $a1 = $b1; $b1 = $t2
                        $t3 = $v1; $v1 = $v2; $v2 = $t3
                    }
                    $kk = '{0}|{1}|{2}|{3}' -f $a1, $b1, $v1, $v2
                    if ($uncov.ContainsKey($kk)) { $score++ }
                }
                if ($score -gt $bestScore) { $bestScore = $score; $best = [string]$v }
            }
            if ($null -eq $best) {
                $tmp = @($vals[$k])
                $best = [string]$tmp[0]
            }
            $assign[$k] = $best
        }

        $idxs = @($assign.Keys | Sort-Object)
        for ($x = 0; $x -lt $idxs.Count; $x++) {
            for ($y = $x + 1; $y -lt $idxs.Count; $y++) {
                $a1 = [int]$idxs[$x]; $b1 = [int]$idxs[$y]
                $kk = '{0}|{1}|{2}|{3}' -f $a1, $b1, [string]$assign[$a1], [string]$assign[$b1]
                if ($uncov.ContainsKey($kk)) { [void]$uncov.Remove($kk) }
            }
        }

        $ordered = [ordered]@{}
        $argList = @($base)
        for ($k = 0; $k -lt $n; $k++) {
            $ordered[[string]$sel[$k].Name] = [string]$assign[$k]
            [string[]]$tokens = Get-AxisValueTokens -Axis $sel[$k] -Value ([string]$assign[$k]) -IccFile $IccFile -Overrides $Overrides
            $argList += @($tokens)
        }
        $case = [pscustomobject]@{
            Id          = ('L2-{0:d4}' -f ($cases.Count + 1))
            Assignments = $ordered
            Args        = $argList
        }
        [void]$cases.Add($case)
    }

    return $cases.ToArray()
}

# ══════════════════════════════════════════════════════════════════════════════
# 3) L3 语义真矩阵
# ══════════════════════════════════════════════════════════════════════════════

# 每张矩阵：全部轴的**真笛卡尔积**，再按折叠表把语义等价的格子去重
# （去重后 Cases 的 Assignments 一律是**规范化**取值 => 不重复劳动）。
# Rationale 说明「为什么这几条轴不独立、必须真积」；Evidence 指向该耦合的源码位置。
function Get-SemanticMatrices {
    [CmdletBinding()]
    param(
        [string]$Source = 'fixtures/in.png',
        [string]$OutDir = 'out',
        [string]$IccFile = 'fixtures/p3.icc',
        $Registry,
        $Overrides,
        $Folds
    )
    if ($null -eq $Registry) { $Registry = @(Get-AxisRegistry) }
    if ($null -eq $Folds) { $Folds = @(Get-EquivalenceClasses) }
    if ($null -eq $Overrides) { $Overrides = Get-AxisTokenOverrides -IccFile $IccFile }

    $byName = @{}
    foreach ($a in @($Registry)) { $byName[[string]$a.Name] = $a }

    $base = @('--headless', '-i', $Source, '-o', $OutDir)

    $defs = @(
        @{
            Name = 'color-core'
            Axes = @('format', 'color-strategy', 'color-space', 'color-engine')
            Rationale = 'Strategy decides whether/how color is converted and labelled, target gamut decides where to, container decides which labelling is even expressible (CICP vs ICC), engine decides which combinations are unroutable. The four are not independent.'
            Evidence = @(
                'src/FfmpegGui/Services/FfmpegCommandBuilder.Decision.cs:144|var (capP, capT, capM, capBd, capIcc) = GetFormatColorCapabilities(fmt, options);',
                'src/FfmpegGui/Services/ColorMapping/ColorEngineRouter.cs:102|!string.Equals(o.ColorEngine, "legacy", StringComparison.OrdinalIgnoreCase));'
            )
        },
        @{
            Name = 'encoder-core'
            Axes = @('format', 'encoder', 'chroma', 'bit-depth')
            Rationale = 'Backend compatibility is a function of format, and -pix_fmt is a single-truth mapping of (format, chroma, bitDepth); the container also clamps bit depth. None of the four is independent.'
            Evidence = @(
                'src/FfmpegGui/Services/EncoderDetectionService.cs:38|EncoderBackend.Cjpegli => fmt is "jpg" or "jpeg" or "jpegli",',
                # ⚠ pin 演进：763 → **796** → **821**（+33 三段 U4b，再 +25 = 2026-10-02 AVIF 增益图
                #   写出器那批在本文件之前的编辑；两处锚 796/748 → 821/773 已按 `grep -n` 复核实况、
                #   2026-10-01 U4b 第一半给 libaom tune switch 补 `default:` 播报；
                #   2026-10-02 U4b 第二半改 `NormalizeTuneToken` 的"空串两义性"并加认得集合；
                #   2026-10-02 U4b 第三半把 default 收窄 + 加"这条后端根本没有 tune 通路"的中心播报）。
                #   抬号前已按本仓规矩 `grep -n` 复核实况行，并确认该字面量在全文件**只出现一次**
                #   （行号锚不容歧义）。见 docs/TESTING.md §6 第 112 条。
                'src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs:821|if (fmt is "png" or "tiff" or "apng" or "jxl")'
            )
        },
        @{
            Name = 'metadata-core'
            Axes = @('format', 'metadata-mode', 'strip-gps', 'strip-time', 'strip-camera', 'strip-all-exif', 'strip-xmp')
            Rationale = 'The preserve flag clears all five strip bits in the parser, so metadata-mode is NOT a single-variable switch against the strip bits; the container then decides whether EXIF/XMP can be carried at all.'
            Evidence = @(
                'src/FfmpegGui/CliParser.cs:216|case "--preserve-metadata":',
                # ⚠ pin 演进：2011 →（#39/#43 插注释）2041 →（2026-09-30 显示尺寸轮往 :1641 之后插了 14 行）**2055**。
                #   →（2026-10-01 并发会话从同一文件删 5 行 ⇒ 该文件**四条 pin 同步 −5**：971→966、981→976、1013→1008、2055→2050）**2050**。
                #   漂移由 A30 当场报出（`literal not on ...:2041 (found at line 2055)`）⇒ 抬号前先 `grep -n` 复核实况行，
                #   并确认全文件该字面量**只出现一次**（行号锚不容歧义）。见 docs/TESTING.md §6 第 112 条。
                # ⚠ pin 演进（2026-10-04）：2050 → **2061**（+11），同 A21 那条成因
                'src/FfmpegGui/Services/FfmpegCommandBuilder.cs:2065|bool exiftoolFormats = fmt is "jpg" or "jpeg" or "png" or "tiff" or "webp";'   # 2026-10-04 重钉 2061→2065（同批 +4）
            )
        },
        @{
            Name = 'gainmap-core'
            Axes = @('format', 'color-engine', 'jpeg-gain-map', 'jpeg-gain-map-base-gamut', 'jpeg-gain-map-downsample')
            Rationale = 'Requesting a gain map forces the engine regardless of --color-engine legacy, so the engine axis is not independent of the gain-map switch; base gamut and downsample are encoder args of the same Ultra HDR output.'
            Evidence = @(
                'src/FfmpegGui/Services/ColorMapping/ColorEngineRouter.cs:102|!string.Equals(o.ColorEngine, "legacy", StringComparison.OrdinalIgnoreCase));'
            )
        },
        @{
            Name = 'bitdepth-container'
            Axes = @('format', 'color-space', 'bit-depth')
            Rationale = 'Bit depth is clamped by the outlet: AVIF takes its ceiling from the encoder that actually runs (libaom/av1_nvenc 12, others 10; an empty encoder name means no -c:v, i.e. ffmpeg default = libaom-av1), WebP/JPEG are capped at 8 by the container, and RGB-native outlets only ship 8/16 - so the three axes are coupled. (2026-09-26: the former "AVIF + Display P3 + high bit depth collapses to sRGB 8-bit" clause was measured and removed; see task 39 and tests/output/t36/probe_39b.ps1.)'
            Evidence = @(
                # ⚠ pin 演进：715 → **748**（+33），与上面 encoder-core 是同三段 U4b 编辑造成的同向漂移
                #   （2026-10-01 第一半补 libaom tune `default:` 播报、2026-10-02 第二半改
                #   `NormalizeTuneToken` 的两义性、第三半加"这条后端没有 tune 通路"的中心播报）；
                #   唯一性同样已 `grep -n` 核过。
                'src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs:773|AvifMaxBitDepthForEncoder(string? encoder)',
                'src/FfmpegGui/Services/FfmpegCommandBuilder.cs:1023|return (null, null, null, ColorMapping.ImageEncoderArgs.AvifMaxBitDepth(options), false);',   # 2026-10-04 重钉 1019→1023（同批 +4）
                'src/FfmpegGui/Services/FfmpegCommandBuilder.Decision.cs:155|options.BitDepth = capBd;'
            )
        }
    )

    $out = New-Object System.Collections.ArrayList
    foreach ($d in $defs) {
        $ax = @($d.Axes)
        $lists = @()
        foreach ($nm in $ax) {
            if ($byName.ContainsKey([string]$nm)) { $lists += ,@($byName[[string]$nm].Values) }
            else { $lists += ,@() }
        }
        $counts = @()
        $raw = 1
        foreach ($l in $lists) { $counts += @($l).Count; $raw = $raw * @($l).Count }

        $tuples = @(Get-MatrixIndexTuples -Counts $counts)
        $seen = @{}
        $cases = New-Object System.Collections.ArrayList
        foreach ($t in $tuples) {
            $assign = [ordered]@{}
            for ($k = 0; $k -lt $ax.Count; $k++) { $assign[[string]$ax[$k]] = [string]$lists[$k][$t[$k]] }
            $canon = Get-MatrixCanonicalAssignment -Assignment $assign -Folds $Folds
            $ckey = ''
            foreach ($k2 in $ax) { $ckey = $ckey + '|' + [string]$canon[[string]$k2] }
            if ($seen.ContainsKey($ckey)) { continue }
            $seen[$ckey] = $true

            $argList = @($base)
            foreach ($k3 in $ax) {
                [string[]]$tokens = Get-AxisValueTokens -Axis $byName[[string]$k3] -Value ([string]$canon[[string]$k3]) -IccFile $IccFile -Overrides $Overrides
                $argList += @($tokens)
            }
            $case = [pscustomobject]@{
                Id          = ('L3-{0}-{1:d4}' -f $d.Name, ($cases.Count + 1))
                Matrix      = $d.Name
                Assignments = $canon
                Args        = $argList
            }
            [void]$cases.Add($case)
        }

        $obj = [pscustomobject]@{
            Name        = [string]$d.Name
            Axes        = $ax
            Cases       = $cases.ToArray()
            Rationale   = [string]$d.Rationale
            Evidence    = @($d.Evidence)
            RawCount    = [int]$raw
            FoldedCount = [int]$cases.Count
        }
        [void]$out.Add($obj)
    }
    return $out.ToArray()
}

# ══════════════════════════════════════════════════════════════════════════════
# 4) 结构校验器（自检与负控共用同一个校验器 —— 负控证明的就是「它有牙」）
# ══════════════════════════════════════════════════════════════════════════════

# 返回 @{ Pass; Fail; Lines }。Lines 是逐条断言的可读行。
function Test-MatrixStructure {
    [CmdletBinding()]
    param(
        $Registry,
        $Folds,
        [string[]]$PairwiseAxes,
        $PairwiseCases,
        $Matrices,
        $Overrides,
        [string]$IccFile = 'fixtures/p3.icc'
    )

    $acc = @{ Pass = 0; Fail = 0; Lines = (New-Object System.Collections.ArrayList) }
    $check = {
        param([bool]$Ok, [string]$Id, [string]$Msg)
        if ($Ok) {
            $acc['Pass'] = [int]$acc['Pass'] + 1
            [void]$acc['Lines'].Add(('  PASS {0} {1}' -f $Id, $Msg))
        } else {
            $acc['Fail'] = [int]$acc['Fail'] + 1
            [void]$acc['Lines'].Add(('  FAIL {0} {1}' -f $Id, $Msg))
        }
    }

    $reg = @($Registry)
    $foldList = @($Folds)
    $pwAxes = @($PairwiseAxes)
    $cses = @($PairwiseCases)
    $mtxs = @($Matrices)
    if ($null -eq $Overrides) { $Overrides = @{} }

    $byName = @{}
    foreach ($a in $reg) { $byName[[string]$a.Name] = $a }

    # ── A1 注册表非空 ──
    & $check ($reg.Count -ge 1) 'A1' ('registry non-empty (axes={0})' -f $reg.Count)

    # ── 收集所有「被引用的轴名」与「被引用的 (轴,取值)」 ──
    $refAxes = New-Object System.Collections.ArrayList
    foreach ($f in $foldList) {
        [void]$refAxes.Add([string]$f.Axis)
        foreach ($k in @($f.When.Keys)) { [void]$refAxes.Add([string]$k) }
    }
    foreach ($nm in $pwAxes) { [void]$refAxes.Add([string]$nm) }
    foreach ($m in $mtxs) { foreach ($nm in @($m.Axes)) { [void]$refAxes.Add([string]$nm) } }
    foreach ($c in $cses) { foreach ($k in @($c.Assignments.Keys)) { [void]$refAxes.Add([string]$k) } }
    foreach ($m in $mtxs) { foreach ($c in @($m.Cases)) { foreach ($k in @($c.Assignments.Keys)) { [void]$refAxes.Add([string]$k) } } }

    $badAxis = @()
    foreach ($nm in @($refAxes | Sort-Object -Unique)) {
        if (-not $byName.ContainsKey([string]$nm)) { $badAxis += [string]$nm }
    }
    & $check ($badAxis.Count -eq 0) 'A2' ('every referenced axis exists in registry (missing={0})' -f ($badAxis -join ','))

    # ── A3 被引用的取值真的属于该轴 ──
    $badVal = New-Object System.Collections.ArrayList
    foreach ($f in $foldList) {
        $nm = [string]$f.Axis
        if (-not $byName.ContainsKey($nm)) { continue }
        $vs = @($byName[$nm].Values)
        if ($vs -notcontains [string]$f.Representative) { [void]$badVal.Add(('{0}.rep={1}' -f $nm, $f.Representative)) }
        foreach ($mv in @($f.Members)) {
            if ($vs -notcontains [string]$mv) { [void]$badVal.Add(('{0}.member={1}' -f $nm, $mv)) }
        }
        foreach ($wk in @($f.When.Keys)) {
            if (-not $byName.ContainsKey([string]$wk)) { continue }
            $wvs = @($byName[[string]$wk].Values)
            foreach ($wv in @($f.When[$wk])) {
                if ($wvs -notcontains [string]$wv) { [void]$badVal.Add(('{0}.when={1}' -f $wk, $wv)) }
            }
        }
    }
    $allCases = @($cses) + @($mtxs | ForEach-Object { @($_.Cases) })
    foreach ($c in $allCases) {
        foreach ($k in @($c.Assignments.Keys)) {
            if (-not $byName.ContainsKey([string]$k)) { continue }
            $vs = @($byName[[string]$k].Values)
            if ($vs -notcontains [string]$c.Assignments[$k]) { [void]$badVal.Add(('{0}={1}' -f $k, $c.Assignments[$k])) }
        }
    }
    & $check ($badVal.Count -eq 0) 'A3' ('every referenced value belongs to its axis (bad={0})' -f (@($badVal | Sort-Object -Unique) -join ','))

    # ── A4/A5/A6 L2 规模 ──
    & $check ($pwAxes.Count -ge 2) 'A4' ('pairwise axis set has >= 2 axes (axes={0})' -f $pwAxes.Count)
    & $check ($cses.Count -gt 0) 'A5' ('pairwise case count > 0 (cases={0})' -f $cses.Count)

    $logProd = 0.0
    foreach ($nm in $pwAxes) {
        if (-not $byName.ContainsKey([string]$nm)) { $logProd = [double]::NegativeInfinity; break }
        $cnt = @($byName[[string]$nm].Values).Count
        if ($cnt -le 0) { $logProd = [double]::NegativeInfinity; break }
        $logProd = $logProd + [Math]::Log10([double]$cnt)
    }
    $logCases = [double]::NegativeInfinity
    if ($cses.Count -gt 0) { $logCases = [Math]::Log10([double]$cses.Count) }
    $okA6 = ($cses.Count -gt 0) -and ($logCases -lt $logProd)
    & $check $okA6 'A6' ('pairwise cases={0} (10^{1:F2}) < full cartesian 10^{2:F2}' -f $cses.Count, $logCases, $logProd)

    # ── A7/A8 两两覆盖：**独立重算**（只读用例的 Assignments，不读生成器内部记账）──
    $axisIdx = @{}
    for ($i = 0; $i -lt $pwAxes.Count; $i++) { $axisIdx[[string]$pwAxes[$i]] = $i }
    $cov = @{}
    foreach ($c in $cses) {
        $items = @()
        foreach ($k in @($c.Assignments.Keys)) {
            $nm = [string]$k
            if (-not $axisIdx.ContainsKey($nm)) { continue }
            $items += ,@([int]$axisIdx[$nm], [string]$c.Assignments[$k])
        }
        for ($x = 0; $x -lt $items.Count; $x++) {
            for ($y = $x + 1; $y -lt $items.Count; $y++) {
                $i1 = [int]$items[$x][0]; $v1 = [string]$items[$x][1]
                $i2 = [int]$items[$y][0]; $v2 = [string]$items[$y][1]
                if ($i1 -lt $i2) { $key = '{0}|{1}|{2}|{3}' -f $i1, $i2, $v1, $v2 }
                else { $key = '{0}|{1}|{2}|{3}' -f $i2, $i1, $v2, $v1 }
                $cov[$key] = $true
            }
        }
    }
    $totalCombo = 0
    $missing = 0
    $firstMiss = ''
    for ($i = 0; $i -lt $pwAxes.Count; $i++) {
        for ($j = $i + 1; $j -lt $pwAxes.Count; $j++) {
            if (-not $byName.ContainsKey([string]$pwAxes[$i])) { continue }
            if (-not $byName.ContainsKey([string]$pwAxes[$j])) { continue }
            foreach ($va in @($byName[[string]$pwAxes[$i]].Values)) {
                foreach ($vb in @($byName[[string]$pwAxes[$j]].Values)) {
                    $totalCombo++
                    $key = '{0}|{1}|{2}|{3}' -f $i, $j, [string]$va, [string]$vb
                    if (-not $cov.ContainsKey($key)) {
                        $missing++
                        if ([string]::IsNullOrEmpty($firstMiss)) { $firstMiss = $key }
                    }
                }
            }
        }
    }
    & $check ($totalCombo -gt 0) 'A8' ('pairwise combo universe non-empty (combos={0})' -f $totalCombo)
    & $check ($missing -eq 0) 'A7' ('pairwise coverage complete: checked {0} combos, missing={1} (first={2})' -f $totalCombo, $missing, $firstMiss)

    # ── A9/A10/A11 用例 Args 卫生（L2 + L3）──
    $badEmpty = New-Object System.Collections.ArrayList
    foreach ($c in $allCases) {
        $ti = -1
        foreach ($t in @($c.Args)) {
            $ti++
            if ($null -eq $t) { [void]$badEmpty.Add(('{0}[{1}]=null' -f $c.Id, $ti)); break }
            if ([string]::IsNullOrEmpty([string]$t)) { [void]$badEmpty.Add(('{0}[{1}]=empty' -f $c.Id, $ti)); break }
        }
    }
    & $check ($badEmpty.Count -eq 0) 'A9' ('no null/empty token in any case Args (bad={0})' -f (@($badEmpty) -join ','))

    $badDup = New-Object System.Collections.ArrayList
    foreach ($c in $allCases) {
        $seenKey = @{}
        foreach ($t in @($c.Args)) {
            $k = Get-MatrixCliKey -Token ([string]$t)
            if ($null -eq $k) { continue }
            if ($seenKey.ContainsKey($k)) { [void]$badDup.Add(('{0}:{1}' -f $c.Id, $k)); break }
            $seenKey[$k] = $true
        }
    }
    & $check ($badDup.Count -eq 0) 'A10' ('no duplicate CLI key in any case Args (bad={0})' -f (@($badDup) -join ','))

    $badBase = New-Object System.Collections.ArrayList
    foreach ($c in $allCases) {
        $t = @($c.Args)
        if (($t -notcontains '--headless') -or ($t -notcontains '-i') -or ($t -notcontains '-o')) { [void]$badBase.Add($c.Id) }
    }
    & $check ($badBase.Count -eq 0) 'A11' ('every case carries --headless -i -o (bad={0})' -f (@($badBase) -join ','))

    # ── A12 用例 Id 唯一（L2）──
    $ids = @($cses | ForEach-Object { $_.Id })
    & $check (@($ids | Sort-Object -Unique).Count -eq $ids.Count) 'A12' ('pairwise case Ids unique (ids={0})' -f $ids.Count)

    # ── A13 每条 L2 用例：轴不重复、且赋值的轴都在参与轴集合内 ──
    $badAsg = New-Object System.Collections.ArrayList
    foreach ($c in $cses) {
        $seenAxis = @{}
        foreach ($k in @($c.Assignments.Keys)) {
            $nm = [string]$k
            if ($seenAxis.ContainsKey($nm)) { [void]$badAsg.Add(('{0}:dup-axis:{1}' -f $c.Id, $nm)); break }
            $seenAxis[$nm] = $true
            if ($axisIdx.ContainsKey($nm)) { continue }
            [void]$badAsg.Add(('{0}:not-a-pairwise-axis:{1}' -f $c.Id, $nm))
        }
    }
    & $check ($badAsg.Count -eq 0) 'A13' ('every pairwise case assigns pairwise axes only, no dup axis (bad={0})' -f (@($badAsg) -join ','))

    # ── A14 折叠：代表元必须在成员集内 ──
    $badRep = New-Object System.Collections.ArrayList
    foreach ($f in $foldList) {
        if (@($f.Members) -notcontains [string]$f.Representative) { [void]$badRep.Add([string]$f.ClassId) }
    }
    & $check ($badRep.Count -eq 0) 'A14' ('every fold representative is one of its members (bad={0})' -f (@($badRep) -join ','))

    # ── A15/A16 折叠组内：折叠后的 CLI 参数完全相同；且原始拼写不能全同（否则折叠是空动作）──
    $badIn = New-Object System.Collections.ArrayList
    $badRaw = New-Object System.Collections.ArrayList
    $multiMember = 0
    foreach ($f in $foldList) {
        $ax = $null
        if ($byName.ContainsKey([string]$f.Axis)) { $ax = $byName[[string]$f.Axis] }
        if ($null -eq $ax) { continue }
        if (@($f.Members).Count -ge 2) { $multiMember++ }
        $folded = @()
        $rawSet = @{}
        foreach ($mv in @($f.Members)) {
            [string[]]$tkRep = Get-AxisValueTokens -Axis $ax -Value ([string]$f.Representative) -IccFile $IccFile -Overrides $Overrides
            $folded += (@($tkRep) -join ' ')
            [string[]]$tkRaw = Get-AxisValueTokens -Axis $ax -Value ([string]$mv) -IccFile $IccFile -Overrides $Overrides
            $rawSet[(@($tkRaw) -join ' ')] = $true
        }
        if (@($folded | Sort-Object -Unique).Count -ne 1) { [void]$badIn.Add([string]$f.ClassId) }
        if (@($f.Members).Count -ge 2 -and @($rawSet.Keys).Count -lt 2) { [void]$badRaw.Add([string]$f.ClassId) }
    }
    & $check ($badIn.Count -eq 0) 'A15' ('folded CLI args identical inside every fold class (bad={0})' -f (@($badIn) -join ','))
    & $check ($badRaw.Count -eq 0) 'A16' ('multi-member fold classes merge >= 2 distinct raw CLI spellings (bad={0})' -f (@($badRaw) -join ','))

    # ── A17/A18 折叠跨组：同轴同 When 的类，代表元参数必须两两不同；且这个比较不能是空集 ──
    $groups = @{}
    foreach ($f in $foldList) {
        $sig = [string]$f.Axis
        $wk = @($f.When.Keys | Sort-Object)
        foreach ($k in $wk) { $sig = $sig + ';' + [string]$k + '=' + (@($f.When[$k] | Sort-Object) -join ',') }
        if (-not $groups.ContainsKey($sig)) { $groups[$sig] = New-Object System.Collections.ArrayList }
        [void]$groups[$sig].Add($f)
    }
    $badCross = New-Object System.Collections.ArrayList
    $comparedGroups = 0
    foreach ($sig in @($groups.Keys)) {
        $g = @($groups[$sig])
        if ($g.Count -lt 2) { continue }
        $comparedGroups++
        $spell = @{}
        foreach ($f in $g) {
            $ax = $null
            if ($byName.ContainsKey([string]$f.Axis)) { $ax = $byName[[string]$f.Axis] }
            if ($null -eq $ax) { continue }
            [string[]]$tk = Get-AxisValueTokens -Axis $ax -Value ([string]$f.Representative) -IccFile $IccFile -Overrides $Overrides
            $sp = (@($tk) -join ' ')
            if ($spell.ContainsKey($sp)) { [void]$badCross.Add(('{0} vs {1}' -f $spell[$sp], $f.ClassId)) }
            else { $spell[$sp] = [string]$f.ClassId }
        }
    }
    & $check ($badCross.Count -eq 0) 'A17' ('cross-group representatives produce distinct CLI args (bad={0})' -f (@($badCross) -join ','))
    & $check ($comparedGroups -ge 1) 'A18' ('at least one axis has >= 2 same-When fold classes, so A17 is not vacuous (groups={0})' -f $comparedGroups)

    # ── A19 折叠表非空动作：至少有一个多成员类 ──
    & $check ($multiMember -ge 1) 'A19' ('at least one fold class merges >= 2 values (multi={0})' -f $multiMember)

    # ── A20 同轴同 When 的折叠类两两不相交 ──
    $badDisj = New-Object System.Collections.ArrayList
    foreach ($sig in @($groups.Keys)) {
        $g = @($groups[$sig])
        if ($g.Count -lt 2) { continue }
        for ($x = 0; $x -lt $g.Count; $x++) {
            for ($y = $x + 1; $y -lt $g.Count; $y++) {
                foreach ($mv in @($g[$x].Members)) {
                    if (@($g[$y].Members) -contains [string]$mv) {
                        [void]$badDisj.Add(('{0}/{1}:{2}' -f $g[$x].ClassId, $g[$y].ClassId, $mv))
                    }
                }
            }
        }
    }
    & $check ($badDisj.Count -eq 0) 'A20' ('same-axis same-When fold classes are disjoint (bad={0})' -f (@($badDisj) -join ','))

    # ── A21 折叠取证：每条 Evidence 必须能在**指定行号那一行**上找到字面片段 ──
    $evTotal = 0
    $evBad = New-Object System.Collections.ArrayList
    $noEv = New-Object System.Collections.ArrayList
    foreach ($f in $foldList) {
        $evs = @($f.Evidence)
        if ($evs.Count -eq 0) { [void]$noEv.Add([string]$f.ClassId); continue }
        foreach ($e in $evs) {
            $evTotal++
            $r = Test-MatrixEvidenceEntry -Entry ([string]$e)
            if (-not $r.Ok) { [void]$evBad.Add(('{0}: {1}' -f $f.ClassId, $r.Detail)) }
        }
    }
    & $check ($noEv.Count -eq 0) 'A22' ('every fold class carries >= 1 evidence entry (none={0})' -f (@($noEv) -join ','))
    & $check ($evBad.Count -eq 0) 'A21' ('every fold evidence literal is present on its exact source line (checked={0}, bad={1} :: {2})' -f $evTotal, $evBad.Count, (@($evBad) -join ' | '))

    # ── A23 折叠理由非空 ──
    $badRat = @()
    foreach ($f in $foldList) {
        if ([string]::IsNullOrWhiteSpace([string]$f.Rationale)) { $badRat += [string]$f.ClassId }
    }
    & $check ($badRat.Count -eq 0) 'A23' ('every fold class has non-empty Rationale (bad={0})' -f ($badRat -join ','))

    # ── A24 L3 矩阵：名称/轴/理由非空、轴数 >= 2 ──
    $badMtx = New-Object System.Collections.ArrayList
    foreach ($m in $mtxs) {
        if ([string]::IsNullOrWhiteSpace([string]$m.Name)) { [void]$badMtx.Add('empty-name') }
        if ([string]::IsNullOrWhiteSpace([string]$m.Rationale)) { [void]$badMtx.Add(('{0}:empty-rationale' -f $m.Name)) }
        if (@($m.Axes).Count -lt 2) { [void]$badMtx.Add(('{0}:axes<2' -f $m.Name)) }
        foreach ($nm in @($m.Axes)) {
            if ([string]::IsNullOrWhiteSpace([string]$nm)) { [void]$badMtx.Add(('{0}:empty-axis' -f $m.Name)) }
        }
        if (@($m.Cases).Count -le 0) { [void]$badMtx.Add(('{0}:no-cases' -f $m.Name)) }
    }
    & $check ($badMtx.Count -eq 0) 'A24' ('every L3 matrix has name/axes>=2/rationale/cases (bad={0})' -f (@($badMtx) -join ','))

    # ── A25 矩阵名唯一 + 用例 Id 唯一（跨全部矩阵）──
    $mNames = @($mtxs | ForEach-Object { $_.Name })
    $allIds = @($allCases | ForEach-Object { $_.Id })
    & $check (@($mNames | Sort-Object -Unique).Count -eq $mNames.Count) 'A25' ('matrix names unique (names={0})' -f $mNames.Count)
    & $check (@($allIds | Sort-Object -Unique).Count -eq $allIds.Count) 'A26' ('all case Ids unique across L2 + L3 (ids={0})' -f $allIds.Count)

    # ── A27 L3 真矩阵：Cases 必须等于「规范化后的全笛卡尔积」集合（集合相等）──
    $badProd = New-Object System.Collections.ArrayList
    foreach ($m in $mtxs) {
        $ax = @($m.Axes)
        $lists = @()
        $counts = @()
        foreach ($nm in $ax) {
            if ($byName.ContainsKey([string]$nm)) { $lists += ,@($byName[[string]$nm].Values) }
            else { $lists += ,@() }
            $counts += @($lists[$lists.Count - 1]).Count
        }
        $tuples = @(Get-MatrixIndexTuples -Counts $counts)
        $expect = @{}
        foreach ($t in $tuples) {
            $assign = [ordered]@{}
            for ($k = 0; $k -lt $ax.Count; $k++) { $assign[[string]$ax[$k]] = [string]$lists[$k][$t[$k]] }
            $canon = Get-MatrixCanonicalAssignment -Assignment $assign -Folds $foldList
            $ck = ''
            foreach ($k2 in $ax) { $ck = $ck + '|' + [string]$canon[[string]$k2] }
            $expect[$ck] = $true
        }
        $got = @{}
        foreach ($c in @($m.Cases)) {
            $ck = ''
            foreach ($k2 in $ax) { $ck = $ck + '|' + [string]$c.Assignments[[string]$k2] }
            $got[$ck] = $true
        }
        $miss = 0
        foreach ($k in @($expect.Keys)) { if (-not $got.ContainsKey($k)) { $miss++ } }
        $extra = 0
        foreach ($k in @($got.Keys)) { if (-not $expect.ContainsKey($k)) { $extra++ } }
        if ($miss -ne 0 -or $extra -ne 0) {
            [void]$badProd.Add(('{0}:missing={1},extra={2}' -f $m.Name, $miss, $extra))
        }
    }
    & $check ($badProd.Count -eq 0) 'A27' ('every L3 matrix equals its fold-canonical full product (bad={0})' -f (@($badProd) -join ','))

    # ── A28 矩阵规模读数：0 < FoldedCount <= RawCount，且至少一张矩阵真的被折叠 ──
    $badSize = New-Object System.Collections.ArrayList
    $reduced = 0
    foreach ($m in $mtxs) {
        if ([int]$m.RawCount -le 0) { [void]$badSize.Add(('{0}:raw<=0' -f $m.Name)) }
        if ([int]$m.FoldedCount -le 0) { [void]$badSize.Add(('{0}:folded<=0' -f $m.Name)) }
        if ([int]$m.FoldedCount -gt [int]$m.RawCount) { [void]$badSize.Add(('{0}:folded>raw' -f $m.Name)) }
        if ([int]$m.FoldedCount -lt [int]$m.RawCount) { $reduced++ }
    }
    & $check ($badSize.Count -eq 0) 'A28' ('every L3 matrix has 0 < folded <= raw (bad={0})' -f (@($badSize) -join ','))
    & $check ($reduced -ge 1) 'A29' ('at least one L3 matrix is strictly reduced by folding (reduced={0})' -f $reduced)

    # ── A30 矩阵取证：与折叠表同规（行号定位 + 字面片段）──
    $mevTotal = 0
    $mevBad = New-Object System.Collections.ArrayList
    $mevNone = New-Object System.Collections.ArrayList
    foreach ($m in $mtxs) {
        $evs = @($m.Evidence)
        if ($evs.Count -eq 0) { [void]$mevNone.Add([string]$m.Name); continue }
        foreach ($e in $evs) {
            $mevTotal++
            $r = Test-MatrixEvidenceEntry -Entry ([string]$e)
            if (-not $r.Ok) { [void]$mevBad.Add(('{0}: {1}' -f $m.Name, $r.Detail)) }
        }
    }
    & $check ($mevNone.Count -eq 0) 'A31' ('every L3 matrix carries >= 1 evidence entry (none={0})' -f (@($mevNone) -join ','))
    & $check ($mevBad.Count -eq 0) 'A30' ('every matrix evidence literal is present on its exact source line (checked={0}, bad={1} :: {2})' -f $mevTotal, $mevBad.Count, (@($mevBad) -join ' | '))

    return @{ Pass = [int]$acc['Pass']; Fail = [int]$acc['Fail']; Lines = $acc['Lines'].ToArray() }
}

# ══════════════════════════════════════════════════════════════════════════════
# 5) 自检（A 段：真实数据；B 段：合成正控 + 负控，绝不改动真实数据）
# ══════════════════════════════════════════════════════════════════════════════

function Invoke-MatrixSelfTest {
    [CmdletBinding()]
    param([switch]$InjectBadPair)

    $icc = 'fixtures/p3.icc'
    $src = 'fixtures/in.png'
    $out = 'out'

    $registry = @(Get-AxisRegistry)
    $overrides = Get-AxisTokenOverrides -IccFile $icc
    $folds = @(Get-EquivalenceClasses)
    $pwAxes = @(Get-MatrixPairwiseAxes -Registry $registry)

    $cases = @(Get-PairwiseCases -AxisNames $pwAxes -Registry $registry -Overrides $overrides -IccFile $icc -Source $src -OutDir $out)
    $matrices = @(Get-SemanticMatrices -Registry $registry -Folds $folds -Overrides $overrides -IccFile $icc -Source $src -OutDir $out)

    $mutNote = 'off'
    if ($InjectBadPair) {
        # 变异打在主跑这一份数据上：删掉「同时取到 (AxisA=ValueA) 与 (AxisB=ValueB)」的**全部**用例
        # => 该取值组合必然无人覆盖 => A7 当场转红。删了多少条一并打印（可复现）。
        $nmA = [string]$pwAxes[0]
        $nmB = [string]$pwAxes[1]
        $axA = $null; $axB = $null
        foreach ($a in $registry) {
            if ([string]$a.Name -eq $nmA) { $axA = $a }
            if ([string]$a.Name -eq $nmB) { $axB = $a }
        }
        $valA = [string]@($axA.Values)[0]
        $valB = [string]@($axB.Values)[0]
        $kept = New-Object System.Collections.ArrayList
        $dropped = 0
        foreach ($c in $cases) {
            if (Test-MatrixCaseCovers -Case $c -AxisA $nmA -ValueA $valA -AxisB $nmB -ValueB $valB) { $dropped++ }
            else { [void]$kept.Add($c) }
        }
        $cases = $kept.ToArray()
        $mutNote = ('ON: dropped {0} case(s) covering {1}={2} x {3}={4}' -f $dropped, $nmA, $valA, $nmB, $valB)
    }

    $pwCombo = 0
    $sumVals = 0
    foreach ($a in $registry) { if ([string]$a.Kind -ne 'path') { $sumVals += @($a.Values).Count } }
    $logProd = 0.0
    foreach ($a in $registry) {
        if ([string]$a.Kind -eq 'path') { continue }
        $logProd = $logProd + [Math]::Log10([double]@($a.Values).Count)
    }

    Write-Output ('_lib-matrix self-test (axes={0} pairwise-axes={1} pairwise-values={2} pairwise-cases={3} matrices={4} fold-classes={5})' -f $registry.Count, $pwAxes.Count, $sumVals, $cases.Count, $matrices.Count, $folds.Count)
    foreach ($m in $matrices) {
        Write-Output ('  matrix {0}: axes={1} raw={2} folded={3}' -f $m.Name, @($m.Axes).Count, $m.RawCount, $m.FoldedCount)
    }
    Write-Output ('  injection: {0}' -f $mutNote)

    $tally = @{ Pass = 0; Fail = 0 }
    $ck = {
        param([bool]$Ok, [string]$Id, [string]$Msg)
        if ($Ok) {
            $tally['Pass'] = [int]$tally['Pass'] + 1
            Write-Output ('  PASS {0} {1}' -f $Id, $Msg)
        } else {
            $tally['Fail'] = [int]$tally['Fail'] + 1
            Write-Output ('  FAIL {0} {1}' -f $Id, $Msg)
        }
    }

    # ── A 段：真实数据（变异开关就打在它身上）──
    $res = Test-MatrixStructure -Registry $registry -Folds $folds -PairwiseAxes $pwAxes -PairwiseCases $cases -Matrices $matrices -Overrides $overrides -IccFile $icc
    foreach ($l in @($res.Lines)) { Write-Output $l }
    $tally['Pass'] = [int]$tally['Pass'] + [int]$res.Pass
    $tally['Fail'] = [int]$tally['Fail'] + [int]$res.Fail

    # ── B 段：合成数据（正控 + 负控）──
    # 合成注册表里带上真实的 `icc-mode` 轴，好让折叠记录复用**真实源码取证**（不是编造的行号）。
    $synReg = @(
        (New-Axis -Name 'icc-mode' -Group 'color' -CliKey '--icc-mode' -Source 'src/FfmpegGui/CliParser.cs:631' -Kind 'enum' -Values @('None', 'CarryIcc', 'BakeToStandard', 'BakeOnly')),
        (New-Axis -Name 'syn-a' -Group 'color' -CliKey '--syn-a' -Source 'syn:1' -Kind 'enum' -Values @('x', 'y')),
        (New-Axis -Name 'syn-b' -Group 'quality' -CliKey '--syn-b' -Source 'syn:2' -Kind 'enum' -Values @('p', 'q'))
    )
    # 合成折叠表只取**轴存在于合成注册表**的那些类（真实的 icc-mode 三类，取证也真实），
    # 另加一条**合成**折叠（syn-a 的 x/y 折叠），好让正控也走到「矩阵按折叠去重」这条路径
    # （否则 A29「至少一张矩阵被折叠」在合成数据上恒红 => 正控失去「校验器不是恒红」的证明力）。
    # ⚠ 不能把整张真实折叠表塞进来：format/bit-depth 不在合成注册表里 => A2 会红。
    $synFoldX = @{
        Axis = 'syn-a'
        ClassId = 'syn-a-fold'
        Representative = 'x'
        Members = @('x', 'y')
        Evidence = @('src/FfmpegGui/Models/ColorStrategy.cs:75|IccMode.CarryIcc => ColorStrategy.CarryIcc,')
        Rationale = 'synthetic fold used only by the positive control'
    }
    $synFolds = @(Get-EquivalenceClasses | Where-Object { [string]$_.Axis -eq 'icc-mode' }) + @(New-FoldClass @synFoldX)
    $synAxes = @(Get-MatrixPairwiseAxes -Registry $synReg)
    $synCases = @(Get-PairwiseCases -AxisNames $synAxes -Registry $synReg -Overrides $overrides -IccFile $icc -Source $src -OutDir $out)

    # 合成矩阵：syn-a x syn-b 的**折叠后全积**（syn-a 折成 x => 2 格），显式构造
    # （不借道 Get-SemanticMatrices，否则会把真实 5 张矩阵的格子混进来 => A2/A27 红）。
    $synCells = New-Object System.Collections.ArrayList
    foreach ($va in @('x')) {
        foreach ($vb in @('p', 'q')) {
            $asg = [ordered]@{}
            $asg['syn-a'] = $va
            $asg['syn-b'] = $vb
            [void]$synCells.Add([pscustomobject]@{
                Id          = ('L3-syn-matrix-{0}{1}' -f $va, $vb)
                Matrix      = 'syn-matrix'
                Assignments = $asg
                Args        = @('--headless', '-i', $src, '-o', $out, '--syn-a', $va, '--syn-b', $vb)
            })
        }
    }
    $synMtx = @(
        [pscustomobject]@{
            Name        = 'syn-matrix'
            Axes        = @('syn-a', 'syn-b')
            Cases       = $synCells.ToArray()
            Rationale   = 'synthetic matrix for the positive control'
            Evidence    = @('src/FfmpegGui/Models/ColorStrategy.cs:75|IccMode.CarryIcc => ColorStrategy.CarryIcc,')
            RawCount    = 4
            FoldedCount = 2
        }
    )

    # B1 正控：干净合成数据 => 0 fail（证明校验器不是「恒红」）
    $b1 = Test-MatrixStructure -Registry $synReg -Folds $synFolds -PairwiseAxes $synAxes -PairwiseCases $synCases -Matrices $synMtx -Overrides $overrides -IccFile $icc
    & $ck ($b1.Fail -eq 0) 'B1' ('positive control: clean synthetic data passes (fail={0})' -f $b1.Fail)
    if ($b1.Fail -gt 0) {
        foreach ($l in @($b1.Lines)) { if ([string]$l -like '*FAIL*') { Write-Output ('      b1' + $l) } }
    }

    # B2 负控：两两覆盖被破坏（删掉覆盖某个取值组合的全部用例）=> 必须转红
    $cA = [string]$synAxes[0]
    $cB = [string]$synAxes[1]
    $vA = ''; $vB = ''
    foreach ($a in $synReg) {
        if ([string]$a.Name -eq $cA) { $vA = [string]@($a.Values)[0] }
        if ([string]$a.Name -eq $cB) { $vB = [string]@($a.Values)[0] }
    }
    $b2cases = @($synCases | Where-Object { -not (Test-MatrixCaseCovers -Case $_ -AxisA $cA -ValueA $vA -AxisB $cB -ValueB $vB) })
    $b2 = Test-MatrixStructure -Registry $synReg -Folds $synFolds -PairwiseAxes $synAxes -PairwiseCases $b2cases -Matrices $synMtx -Overrides $overrides -IccFile $icc
    & $ck ($b2.Fail -ge 1) 'B2' ('negative control: broken pairwise coverage turns validator red (fail={0})' -f $b2.Fail)

    # B3 负控：空 Args 用例 => 必须转红
    $b3cases = @([pscustomobject]@{ Id = 'L2-9999'; Assignments = [ordered]@{ 'syn-a' = 'x' }; Args = @() }) + $synCases
    $b3 = Test-MatrixStructure -Registry $synReg -Folds $synFolds -PairwiseAxes $synAxes -PairwiseCases $b3cases -Matrices $synMtx -Overrides $overrides -IccFile $icc
    & $ck ($b3.Fail -ge 1) 'B3' ('negative control: empty-Args case turns validator red (fail={0})' -f $b3.Fail)

    # B4 负控：折叠取证的行号上**没有**该字面片段 => 必须转红（证明 A21 按行号比对，不是全文件 Contains）
    $bogus = @{
        Axis = 'syn-a'
        ClassId = 'syn-bogus'
        Representative = 'x'
        Members = @('x', 'y')
        Evidence = @('src/FfmpegGui/Models/ColorStrategy.cs:75|IccMode.BakeOnly => ColorStrategy.BakeCicpOnly,')
        Rationale = 'bogus evidence for the negative control'
    }
    $b4 = Test-MatrixStructure -Registry $synReg -Folds @($synFolds + @(New-FoldClass @bogus)) -PairwiseAxes $synAxes -PairwiseCases $synCases -Matrices $synMtx -Overrides $overrides -IccFile $icc
    & $ck ($b4.Fail -ge 1) 'B4' ('negative control: evidence literal on a wrong line turns validator red (fail={0})' -f $b4.Fail)

    # B5 负控：矩阵引用了不存在的轴 => 必须转红
    $badMtx = @([pscustomobject]@{
        Name = 'syn-bad-axis'; Axes = @('syn-a', 'no-such-axis'); Cases = @()
        Rationale = 'negative control'; Evidence = @('src/FfmpegGui/Models/ColorStrategy.cs:75|IccMode.CarryIcc => ColorStrategy.CarryIcc,')
        RawCount = 0; FoldedCount = 0
    })
    $b5 = Test-MatrixStructure -Registry $synReg -Folds $synFolds -PairwiseAxes $synAxes -PairwiseCases $synCases -Matrices $badMtx -Overrides $overrides -IccFile $icc
    & $ck ($b5.Fail -ge 1) 'B5' ('negative control: matrix referencing a missing axis turns validator red (fail={0})' -f $b5.Fail)

    # B6 负控：用例赋了一个不属于该轴的取值 => 必须转红
    $b6cases = @([pscustomobject]@{ Id = 'L2-8888'; Assignments = [ordered]@{ 'syn-a' = 'zzz'; 'syn-b' = 'p' }; Args = @('--headless', '-i', $src, '-o', $out, '--syn-a', 'zzz', '--syn-b', 'p') }) + $synCases
    $b6 = Test-MatrixStructure -Registry $synReg -Folds $synFolds -PairwiseAxes $synAxes -PairwiseCases $b6cases -Matrices $synMtx -Overrides $overrides -IccFile $icc
    & $ck ($b6.Fail -ge 1) 'B6' ('negative control: value outside the axis domain turns validator red (fail={0})' -f $b6.Fail)

    # B7 负控：折叠表里混入「代表元不在成员集内」的类 => 必须转红
    $badFold = @{
        Axis = 'syn-a'
        ClassId = 'syn-bad-rep'
        Representative = 'x'
        Members = @('y')
        Evidence = @('src/FfmpegGui/Models/ColorStrategy.cs:75|IccMode.CarryIcc => ColorStrategy.CarryIcc,')
        Rationale = 'negative control'
    }
    $b7 = Test-MatrixStructure -Registry $synReg -Folds @($synFolds + @(New-FoldClass @badFold)) -PairwiseAxes $synAxes -PairwiseCases $synCases -Matrices $synMtx -Overrides $overrides -IccFile $icc
    & $ck ($b7.Fail -ge 1) 'B7' ('negative control: representative outside members turns validator red (fail={0})' -f $b7.Fail)

    Write-Output ('MATRIX RESULT: pass={0} fail={1}' -f $tally['Pass'], $tally['Fail'])

    # 与 _lib-axes.ps1 同规：本函数**只打印、不返回对象**（返回对象会让调用方用
    # `$null = Invoke-MatrixSelfTest` 把全部输出一起吞掉，变成「静默零输出 + 退出码 0」）。
    $script:MatrixSelfTestResult = [ordered]@{ Pass = [int]$tally['Pass']; Fail = [int]$tally['Fail'] }
}

# ══════════════════════════════════════════════════════════════════════════════
# 顶层：只在显式 -SelfTest 时跑自检（dot-source 时**零副作用**）
# ══════════════════════════════════════════════════════════════════════════════

if ($script:MatrixRunSelfTest) {
    Invoke-MatrixSelfTest -InjectBadPair:$script:MatrixRunInjectBadPair
}

# ══════════════════════════════════════════════════════════════════════════════
# NOTES —— 核对源码时发现的「与预期不符之处」与**未查全项**（供上层与评审对账）
# ══════════════════════════════════════════════════════════════════════════════
# N1. `-f jpeg` 与 `-f jpg` 故意**不折叠**
#     编码器选择确实相同（EncoderDetectionService.cs:38 把 "jpg"/"jpeg"/"jpegli" 一视同仁），
#     但 `CliParser.BuildOutputPath` 用 `opts.Format` 原样拼扩展名 => 产物名 `.jpeg` vs `.jpg`
#     不同。只折编码器语义、不折产物名，会把「扩展名与真实内容是否一致」这类缺陷藏起来。
#     ⚠ 待办（未做）：CLI 侧是否存在 `jpeg -> jpg` 的规范化，本波次**没有**查到；见 N7。
#
# N2. `--color-engine auto` 与 `engine` 故意**不折叠**
#     ColorEngineRouter.cs:100-101（Requested）对二者同为真；但 :115-116（ExplicitlyRequested）
#     只认字面 `engine` => 不可走时「硬失败并点名」vs「记一条说明后回退传统管线」两种结局。
#
# N3. `--color-space "P3 PQ"` 与 `"BT.2020 PQ"` 故意**不折叠**
#     二者输出 CICP tokens 相同（bt2020/smpte2084/bt2020nc）、CjxlColorSpace 同为 Rec2100PQ，
#     但 P3 PQ 的 Kind=UltraWide（ColorSpaceRegistry.cs:94-98）=> 额外做源->BT.2020 的
#     像素 gamut 矩阵转换；BT.2020 PQ 的 Kind=Wide（:106-109）=> 仅重贴标签。**像素不同**。
#
# N4. `--chroma auto` / `--color-range auto` 等价物是「不设该轴」，不是同轴另一取值
#     FfmpegCommandBuilder.cs:464 判 `!Chroma.Equals("auto")` 才下发；:936-946 同理。
#     折叠表的定义域是同轴取值 => 这类「默认值 == 缺省」不进折叠表（否则会把
#     「显式设 auto」与「未设」混为一谈，而 A 面验收要求恰恰要区分这两者）。
#
# N5. **未查全**：`bit-depth` 在 png/tiff/apng/jxl 下 10/12/16 的 pix_fmt 同为 rgb48le
#     （ImageEncoderArgs.cs:486-489）。看起来也是一条条件折叠，但本波次**只确认了 pix_fmt 一处**，
#     没有查清是否还有别的 `BitDepth` 消费者（例如位深元数据 / `-bits_per_raw_sample`）
#     会让 10/12/16 在产物上可区分 => **故意不登记**（宁缺勿滥：错的折叠会静默丢覆盖）。
#     同理未登记 avif 的 12/16 -> `AvifMaxBitDepthForEncoder`（取证在 `bitdepth-container` 矩阵的
#     Evidence 里；#36 之后上限由 `ClassifyAvifBackend` 推，空编码器名 ⇒ 实跑 libaom-av1 ⇒ 12），
#     且 16 与 12 在产物上是否可区分还没逐格量过 => **故意不登记**（宁缺勿滥：错的折叠会静默丢覆盖）。
#
# N6. **未查全**：`--color-strategy` / `--jpeg-gain-map-base-gamut` 的**别名**不在轴取值域内
#     ColorStrategyMapper.TryParse（ColorStrategy.cs:62-69）认 recommend/auto/携带/0 等别名；
#     CliParser.cs:610-611 认 s-r-g-b/bt709/rec2020/bt2100/2020 等别名。
#     这些别名**不是注册表的取值**，故不能进折叠表（自检 A3 会判红：取值必须属于该轴）。
#     本波次**未**单独建「别名表」=> 别名覆盖率是空白，登记在此。
#
# N7. 本库的取证断言**按行号锚定**：若并发修改让 `ColorStrategy.cs:49-52` / `CliParser.cs:150` /
#     `FfmpegCommandBuilder.cs`（jpg / webp / avif 三个能力分支）/ `FfmpegCommandBuilder.Decision.cs`
#     （capBd 就地改写那几行）/ `ImageEncoderArgs.cs`（MapPixFmt 的 RGB 原生分支）/
#     `EncoderDetectionService.cs:38` / `CliParser.cs:216` 中任一行移动，A21/A30 会当场转红 ——
#     这是**故意**的（取证失效必须响，不允许静默沿用）。
#     实测：本库开发期间 `CliParser.cs` 被并发改动、整体位移 +5 行（`case "--preserve-metadata":`
#     从 :211 移到 :216），A30 立刻转红并报出「found at line 216」—— 行号锚定确实有牙。
#
# N9. ⚠⚠ **实测与预期不符（本波次最有价值的一条，属 P1-D 的地盘，但会直接影响折叠表的适用范围）**
#     `--icc-mode` 的 4→3 折叠**只在 CLI 路径成立**；**UI 的同一组 4 个单选按钮是 4→4，不折叠**，
#     而且两边的映射**方向都不一致**：
#       · CLI：`CliParser.cs:684-686` 走 `ColorStrategyMapper.FromIccMode`
#         => None->Recommended、BakeToStandard->Recommended（**合并**）、CarryIcc->CarryIcc、
#            BakeOnly->BakeCicpOnly。
#       · UI：`MainWindow.xaml.cs:2523-2529 GetColorStrategy()`
#         => 默认(None)->Recommended、Carry->CarryIcc、**Bake->BakeCicpOnly**、
#            **BakeOnly->Manual**（4 个取值给出 4 个互不相同的策略）。
#       两者对同名取值给出**不同**结果：Bake 在 CLI 是 Recommended、在 UI 是 BakeCicpOnly；
#       BakeOnly 在 CLI 是 BakeCicpOnly、在 UI 是 Manual。
#       另有一个 `GetIccMode()`（`MainWindow.xaml.cs:2514-2520`）返回**旧枚举**，只被
#       `:5792` 用于**存预设**（`IccMode = GetIccMode().ToString()`），不参与 ColorStrategy 赋值；
#       真正赋 ColorStrategy 的是 `GetColorStrategy()`（调用点 `:1598` `:2817` `:4014`）。
#     => 结论：**「UI 4 单选」与「CLI --icc-mode」不是同一条轴的两个入口，而是两张不同的映射表**。
#        本库的折叠表按**轴名 icc-mode（CLI 轴）**定义，故折叠正确且只对 CLI 成立；
#        UI 侧必须由 P1-D 单独逐值验证（不能复用本折叠）。**本波次不做 UI 侧断言**（越界）。
#
# N8. **设计取舍（有意为之）**：L2 层**不折叠**，L3 层折叠。
#     · L2 的承重断言是「每一对轴的每一对**原始取值**组合都至少出现一次」——这是最强形式；
#       若在 L2 折叠，这条断言会退化成「折叠后的组合」而失去「某取值是否可达」的判别力
#       （那正是 L1 单变量扫要抓的东西）。
#     · 折叠的收益在 L3 才真实：L3 是全笛卡尔积，折叠直接减少格子数（本波次 5 张矩阵
#       合计 5356 -> 4644 格，与自检 A26 的用例总数 4816 = 172 + 4644 对得上）。想对 L2 做折叠的调用方可以自己用
#       `Get-MatrixCanonicalAssignment -Assignment $case.Assignments -Folds (Get-EquivalenceClasses)`。
#     · 代价（如实登记，**实测**不是估计）：L2 用例里会出现「被容器钳掉的取值」，例如
#       `-f jpg --bit-depth 10`（F3 折叠下与 8 等价）。实测 172 条里有 **42 条（24.4%）**
#       在折叠意义下是重复的。这笔代价换来的是 A 面（逐 token 传递）仍然覆盖原始取值域 ——
#       「被钳掉的取值也必须被正确**传递**到命令行」本身就是要验的东西。
#       想按折叠意义去重的调用方请自行用 `Get-MatrixCanonicalAssignment`（本库不强制）。
#
# N10. L2 生成是**确定性贪心**（无随机、无种子）：种子组合按「轴下标升序、取值下标升序」取第一个
#      未覆盖者；每轴取值按「与已定轴形成最多未覆盖组合」打分，平局取先出现的取值。
#      同输入同输出、可复现。实测 172 条覆盖 17970 个组合（理论下界 = max(|A| x |B|) = 13 x 13 = 169）。
