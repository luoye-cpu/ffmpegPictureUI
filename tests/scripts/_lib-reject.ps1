# _lib-reject.ps1 -- 穷举组合测试地基③：拒绝路径 / 降级路径 全枚举库（P2-E，L4 层）
#
# 定位（为什么是「库」而不是「门禁」）：
#   本文件**不产 PASS/FAIL 汇总门禁**，只被 dot-source 复用（写法对齐同目录
#   `_lib-axes.ps1` / `_lib-oracle.ps1`）。它回答一个问题：
#   **这个软件宣称会打出的每一个「拒绝码 / 降级码」，到底有没有一条能真实触发它的用例。**
#
#   L4（规划 §3）的要求：每个 `EngineBlockCodes.*` / `DegradationCodes.*` 各给
#   **一条正控（该触发）** + **一条负控（不该触发）**，且负控必须与正控参数不同
#   （否则负控是空跑 => 恒真 => 没有判别力）。
#
# 五条对外能力：
#   Get-RejectCodeRegistry  -- 全部拒绝码/降级码的结构化登记
#                              (Name/Family/Constant/Source/DocSource/Doc/DocError/
#                               Trigger/TriggerScope/Reachable/UnreachableReason/
#                               Precondition/AssertTokens)
#   Get-RejectCases         -- 每个码 x {正控, 负控} 的用例表（Args 可直接喂 ConvertTo-CliArgs 形态）
#   Test-RejectStructure    -- 结构自检（自检与负控共用同一个校验器 => 负控证明的就是「它有牙」）
#   Invoke-RejectSelfTest   -- 自检，末行 `REJECT RESULT: pass=n fail=0`
#   ConvertFrom-RejectUnicodeEscape / Get-RejectSourceLine / Get-RejectRepoRoot  -- 底层工具
#
# ── 三条设计铁律 ────────────────────────────────────────────────────────────────
#  ① **不用「我自己写的常量表」去验证「我自己写的枚举解析」**：
#     `Source` / `Trigger` 都是 `file:line`，自检会**真的去读那一行**，并断言：
#       · Source 行必须同时含「`const string`」与「= "<Code>"」（即真的是**常量声明行**，
#         而不是恰好提到该名字的注释行 —— 这正是「载体选错 => 断言恒真」的防呆）；
#       · Trigger 行必须含该码名，且**它所在的方法**（按 4 空格缩进向上找最近的成员声明）
#         必须等于登记里的 `TriggerScope`。只做全文件 `Contains` 是不够的：
#         码名同样出现在注释/删除记录里（如 `GeometryWithGainMap`），行号定位才有效。
#  ② **读文件失败必须显式失败**：`Get-RejectSourceLine` 返回 `Ok=$false` + 非空 `Error`，
#     绝不在失败时返回空串让上层把「读不出来」当成「这一行没有该码名」（那会把红变成绿）。
#     本库**不做跨调用缓存**（对齐 `_lib-oracle.ps1` 的铁律）；只有校验器内部那次调用
#     会用一张**局部** hashtable 缓存文件内容，函数返回即失效。
#  ③ **断言载体必须与家族绑定**：`EngineBlockCodes.EncoderUnsupportedSetting` 与
#     `DegradationCodes.EncoderUnsupportedSetting` 是**同一个字符串**，`GeometryWithGainMap`
#     亦然。所以断言串一律带家族限定前缀（`降级（X）` / `本次不适用引擎（X：` /
#     `按要求应走引擎，但本次不可走（X）`），并有一条自检断言「全库断言串唯一」。
#     若有人偷懒写成裸 `（X）`，两个家族会撞车 => 当场转红。
#
# ⚠ 双宿主（PS 5.1 / PS 7）：禁 `? :` / `??` / `??=` / `?.` / `?[` / `&&` / `||`；
#   一律用 `$(if (...) {...} else {...})`。门禁：`verify-ps-compat.ps1`。
# ⚠ 串里全部纯 ASCII（本仓 `_probe-cjk-hardcode-scan.ps1` 的 CJK 余量为 0）：
#   需要中文的**日志断言串**一律以 `\uXXXX` 转义形式存储，由
#   `ConvertFrom-RejectUnicodeEscape` 还原；自检 A15 会拿还原后的字面片段去
#   `QueueProcessor.cs` 里核对 —— 所以**转义码写错会当场转红**，不是自我印证。
# ⚠ 本文件**只在 dot-source 时定义函数**，无顶层副作用（不打印、不建目录、不起进程）。
#
# ── 源码核对结论（2026-09-19 逐条打开源码核对，非转述）─────────────────────────
#   · 拒绝码词表：`EngineBlockCodes`（9 个）`src/FfmpegGui/Services/ColorMapping/EngineBlock.cs:48-68`
#   · 降级码词表：`DegradationCodes`（14 个）`src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:65-102`
#   · 路由拦截生产点：`ColorEngineRouter.BlockReason`（:166）/`UnconsumedOutputSettingItems`（:323）
#   · 降级生产点：`ColorStrategyPlanning.CollectDegradations`（:115）
#   · 日志消费点：`QueueProcessor.cs:919/:923`（引擎拦截）、`:950`（引擎降级）、
#     `:649`（传统管线降级）、`:863`（可替代方案）
#   · ⚠ 另有 **无稳定码的拒绝**：`ColorAction.Reject` 一族（`ColorStrategyPlanning.Reject`
#     助手，:698）只写自由文本 `Reason`/`Advice`，**不进任何 Code 词表**。
#     本库不登记它们（它们没有码），但这条事实记在文件末尾 NOTES 的 N5。
#
# ── 用法 ────────────────────────────────────────────────────────────────────────
#   . "$PSScriptRoot/_lib-reject.ps1"
#   $reg   = @(Get-RejectCodeRegistry)
#   $cases = @(Get-RejectCases)
#   $r = Test-RejectStructure -Registry $reg -Cases $cases
#   if ($r.Fail -gt 0) { $r.Lines | Write-Output }
#   自检： pwsh -File tests/scripts/_lib-reject.ps1 -SelfTest
#         末行 `REJECT RESULT: pass=40 fail=0`
#   变异： pwsh -File tests/scripts/_lib-reject.ps1 -SelfTest -InjectPhantomCode
#         => 把一条**源码里不存在**的码拼进真实登记表，末行转红 `pass=35 fail=6`
#            （A7/A8/A10/A11/B1/B7 六格；C1 断言「A7/A8 确实红了」）。
#            开关关闭即恢复 fail=0 —— 这就是「校验器有牙」的可复现证据。

param(
    [switch]$SelfTest,
    [switch]$InjectPhantomCode
)

# ══════════════════════════════════════════════════════════════════════════════
# 词表（结构自检的判据来源）
# ══════════════════════════════════════════════════════════════════════════════

$script:RejectFamilies = @('EngineBlock', 'Degradation')

$script:RejectKinds = @('positive', 'negative')

# 家族 -> 常量类名（用于断言 `Constant` 前缀正确）
$script:RejectFamilyClass = @{
    'EngineBlock' = 'EngineBlockCodes'
    'Degradation' = 'DegradationCodes'
}

# 家族 -> 日志断言串模板（**ASCII 转义形式**，`{0}` = 码名）。
#   · Degradation  : `降级（{0}）`                    -- QueueProcessor.cs:649 / :950
#   · EngineBlock-A: `本次不适用引擎（{0}：`           -- QueueProcessor.cs:923（auto 回退）
#   · EngineBlock-E: `按要求应走引擎，但本次不可走（{0}）` -- QueueProcessor.cs:919（显式 engine，exit 90）
# ⚠ 两条 EngineBlock 变体都要留：显式 `--color-engine engine` 走 A 之外的那条，文案不同。
$script:RejectAssertTemplates = @{
    'Degradation'  = @('\u964D\u7EA7\uFF08{0}\uFF09')
    'EngineBlock'  = @(
        '\u672C\u6B21\u4E0D\u9002\u7528\u5F15\u64CE\uFF08{0}\uFF1A',
        '\u6309\u8981\u6C42\u5E94\u8D70\u5F15\u64CE\uFF0C\u4F46\u672C\u6B21\u4E0D\u53EF\u8D70\uFF08{0}\uFF09'
    )
}

# 日志消费点所在文件（自检 A15 拿它核对断言串字面片段）
$script:RejectLogSource = 'src/FfmpegGui/Services/QueueProcessor.cs'

# 注入式变异用的幽灵码名（`-InjectPhantomCode`）。刻意挑一个**源码里不存在**的名字。
$script:RejectPhantomName = 'PhantomRejectCode'

# ══════════════════════════════════════════════════════════════════════════════
# 底层工具
# ══════════════════════════════════════════════════════════════════════════════

# 把 `\uXXXX` 转义还原成字符。
# 为什么需要：本仓 `tests/scripts/**.ps1` 的字符串字面量必须是纯 ASCII，
# 而日志断言串里必须出现中文包装词（降级 / 本次不适用引擎 / …）。
function ConvertFrom-RejectUnicodeEscape {
    param([Parameter(Mandatory = $true)][string]$Text)
    $sb = New-Object System.Text.StringBuilder
    $i = 0
    $n = $Text.Length
    while ($i -lt $n) {
        $c = $Text[$i]
        if (($c -eq '\') -and (($i + 5) -lt $n) -and ($Text[$i + 1] -eq 'u')) {
            $hex = $Text.Substring($i + 2, 4)
            $code = 0
            $okParse = [int]::TryParse($hex, [System.Globalization.NumberStyles]::HexNumber,
                [System.Globalization.CultureInfo]::InvariantCulture, [ref]$code)
            if ($okParse) {
                [void]$sb.Append([char]$code)
                $i = $i + 6
                continue
            }
        }
        [void]$sb.Append($c)
        $i = $i + 1
    }
    return $sb.ToString()
}

# 仓库根：优先 env `FFMPEGGUI_REJECT_REPO_ROOT`，其次从本文件所在目录向上找 `src/FfmpegGui`。
# 找不到 => 返回 ''（**显式失败**，不静默回退到 cwd —— 那会让所有 file:line 静默错位）。
function Get-RejectRepoRoot {
    param([string]$Override = '')
    if (-not [string]::IsNullOrWhiteSpace($Override)) {
        if (Test-Path -LiteralPath $Override) { return [System.IO.Path]::GetFullPath($Override) }
        return ''
    }
    $envRoot = [System.Environment]::GetEnvironmentVariable('FFMPEGGUI_REJECT_REPO_ROOT')
    if (-not [string]::IsNullOrWhiteSpace($envRoot)) {
        if (Test-Path -LiteralPath $envRoot) { return [System.IO.Path]::GetFullPath($envRoot) }
        return ''
    }
    $start = ''
    if (-not [string]::IsNullOrEmpty($PSScriptRoot)) { $start = $PSScriptRoot }
    else { $start = (Get-Location).Path }
    $p = $start
    for ($k = 0; $k -lt 6; $k++) {
        if (Test-Path -LiteralPath (Join-Path $p 'src/FfmpegGui')) {
            return [System.IO.Path]::GetFullPath($p)
        }
        $up = Split-Path -Parent $p
        if ([string]::IsNullOrEmpty($up)) { break }
        $p = $up
    }
    return ''
}

# 仓库相对路径 -> 绝对路径。已绝对则原样返回。
function Resolve-RejectPath {
    param([string]$RelPath, [string]$RepoRoot = '')
    if ([string]::IsNullOrWhiteSpace($RelPath)) { return '' }
    if ([System.IO.Path]::IsPathRooted($RelPath)) { return $RelPath }
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) { return $RelPath }
    return (Join-Path $RepoRoot $RelPath)
}

# 读整个文件的行数组。返回 @{ Ok; Lines; Error }。
# ⚠ 失败一律 Ok=$false + Error 非空，**绝不**返回空数组冒充「文件是空的」。
function Get-RejectFileLines {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        $Cache
    )
    if ($null -eq $Cache) { $Cache = @{} }
    if ($Cache.ContainsKey($Path)) { return $Cache[$Path] }
    $rec = $null
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $rec = @{ Ok = $false; Lines = @(); Error = ('file not found: ' + $Path) }
    } else {
        try {
            $lines = @([System.IO.File]::ReadAllLines($Path))
            $rec = @{ Ok = $true; Lines = $lines; Error = '' }
        }
        catch {
            $rec = @{ Ok = $false; Lines = @(); Error = ('read failed: ' + $_.Exception.Message) }
        }
    }
    $Cache[$Path] = $rec
    return $rec
}

# 取某文件的第 LineNumber 行（1-based）。返回 @{ Ok; Text; Error }。
function Get-RejectSourceLine {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$LineNumber,
        $Cache
    )
    $f = Get-RejectFileLines -Path $Path -Cache $Cache
    if (-not $f.Ok) { return @{ Ok = $false; Text = ''; Error = $f.Error } }
    if (($LineNumber -lt 1) -or ($LineNumber -gt @($f.Lines).Count)) {
        return @{ Ok = $false; Text = ''; Error = ('line ' + $LineNumber + ' out of range (file has ' +
            @($f.Lines).Count + ' lines): ' + $Path) }
    }
    return @{ Ok = $true; Text = [string]@($f.Lines)[$LineNumber - 1]; Error = '' }
}

# 把 `path:line` 拆成 @{ Ok; Path; Line; Error }。用**最后一个**冒号切（防 Windows 盘符）。
function ConvertFrom-RejectLocator {
    param([string]$Locator)
    if ([string]::IsNullOrWhiteSpace($Locator)) {
        return @{ Ok = $false; Path = ''; Line = 0; Error = 'empty locator' }
    }
    $i = $Locator.LastIndexOf(':')
    if ($i -lt 1) { return @{ Ok = $false; Path = ''; Line = 0; Error = ('no colon in locator: ' + $Locator) } }
    $p = $Locator.Substring(0, $i).Trim()
    $ls = $Locator.Substring($i + 1).Trim()
    $ln = 0
    if (-not [int]::TryParse($ls, [ref]$ln)) {
        return @{ Ok = $false; Path = ''; Line = 0; Error = ('bad line number in locator: ' + $Locator) }
    }
    return @{ Ok = $true; Path = $p; Line = $ln; Error = '' }
}

# 向上找**最近的 4 空格缩进成员声明**（`private/public/internal/protected/static ...`），
# 取它 `(` 前的标识符当方法名。返回 @{ Ok; Name; Error }。
# 为什么用 4 空格而不是花括号配对：本仓 4 个目标文件里成员声明一律 4 空格缩进，
# 而方法体内部一律 >=8 空格；花括号配对还要处理字符串/注释里的花括号，得不偿失。
function Get-RejectEnclosingScope {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$LineNumber,
        $Cache
    )
    $f = Get-RejectFileLines -Path $Path -Cache $Cache
    if (-not $f.Ok) { return @{ Ok = $false; Name = ''; Error = $f.Error } }
    $lines = @($f.Lines)
    $from = $LineNumber - 1
    if ($from -ge $lines.Count) { $from = $lines.Count - 1 }
    for ($i = $from; $i -ge 0; $i--) {
        $line = [string]$lines[$i]
        if ($line.Length -lt 6) { continue }
        if (-not $line.StartsWith('    ')) { continue }
        if ($line.StartsWith('     ')) { continue }
        $t = $line.Trim()
        if ($t.StartsWith('//')) { continue }
        if ($t.StartsWith('///')) { continue }
        if (-not ($line -match '^    (private|public|internal|protected|static)')) { continue }
        $p = $line.IndexOf('(')
        if ($p -lt 0) { continue }
        $head = $line.Substring(0, $p).TrimEnd()
        $sp = $head.LastIndexOf(' ')
        if ($sp -lt 0) { continue }
        $name = $head.Substring($sp + 1)
        if ($name -eq '') { continue }
        return @{ Ok = $true; Name = $name; Error = '' }
    }
    return @{ Ok = $false; Name = ''; Error = ('no enclosing member declaration found above line ' + $LineNumber) }
}

# ══════════════════════════════════════════════════════════════════════════════
# 1) 登记表构造
# ══════════════════════════════════════════════════════════════════════════════

# 造一条登记记录。字段固定，不多不少。
#   Source / DocSource / Trigger 都是 `<仓库相对路径>:<行号>`；Trigger 为空 = 无生产点。
#   Reachable=$false 时 UnreachableReason 必须非空（自检 A12 强制）。
function New-RejectCode {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Family,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$DocSource,
        [Parameter(Mandatory = $true)][string]$Precondition,
        [string]$Trigger = '',
        [string]$TriggerScope = '',
        [bool]$Reachable = $true,
        [string]$UnreachableReason = ''
    )
    return [pscustomobject]@{
        Name              = $Name
        Family            = $Family
        Constant          = ''
        Source            = $Source
        DocSource         = $DocSource
        Doc               = ''
        DocError          = ''
        Trigger           = $Trigger
        TriggerScope      = $TriggerScope
        Reachable         = $Reachable
        UnreachableReason = $UnreachableReason
        Precondition      = $Precondition
        AssertTokens      = @()
    }
}

# ⚠ `Reachable` 的判据（本库的定义，写清楚免得误读）：
#   = 「存在一条 CLI 用例，能让**该码的日志断言串**真的出现在 item 日志里」。
#   注意这与「源码里有没有构造点」**不是一回事**：`ExternalBackend` 有构造点（:220）
#   但没有任何 CLI 组合能让它到达日志（见该条 UnreachableReason）。
#   `OptionsNull` / 四个从未被构造的 DegradationCodes 则是**连构造点都没有**。
function Get-RejectCodeRegistry {
    [CmdletBinding()]
    param(
        [string]$RepoRoot = ''
    )

    $deg = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs'
    $eng = 'src/FfmpegGui/Services/ColorMapping/EngineBlock.cs'
    $plan = 'src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs'
    $router = 'src/FfmpegGui/Services/ColorMapping/ColorEngineRouter.cs'

    $list = @(
        # ── 引擎路由拦截词表（EngineBlockCodes，9 个）────────────────────────────
        (New-RejectCode -Name 'OptionsNull' -Family 'EngineBlock' `
            -Source ($eng + ':51') -DocSource ($eng + ':50') `
            -Trigger '' -TriggerScope '' -Reachable $false `
            -UnreachableReason 'BlockReason(o==null) branch is defensive: no CLI path can pass a null FfmpegOptions (Requested(null)=false and CliParser always constructs options). Source comment says so verbatim.' `
            -Precondition 'none reachable; the closest candidate is any case with --color-engine engine'),

        (New-RejectCode -Name 'FormatNotSupported' -Family 'EngineBlock' `
            -Source ($eng + ':53') -DocSource ($eng + ':52') `
            -Trigger ($router + ':184') -TriggerScope 'BlockReason' `
            -Precondition '-f bmp : output extension .bmp is NOT in ImageEncoderArgs.IsEngineEncodable (png/apng/tiff/tif/webp/jpg/jpeg/avif/gif/jxl/jxr) => BlockReason returns FormatNotSupported before any other check'),

        (New-RejectCode -Name 'AnimatedInput' -Family 'EngineBlock' `
            -Source ($eng + ':55') -DocSource ($eng + ':54') `
            -Trigger ($router + ':192') -TriggerScope 'BlockReason' `
            -Precondition '-i <*.gif> (input side clause of QueueProcessor.InputHasMultipleFramesAsync returns true unconditionally for .gif/.apng) with any engine-encodable -f'),

        (New-RejectCode -Name 'ExternalBackend' -Family 'EngineBlock' `
            -Source ($eng + ':57') -DocSource ($eng + ':56') `
            -Trigger ($router + ':220') -TriggerScope 'BlockReason' `
            -Reachable $false `
            -UnreachableReason 'Constructed at ColorEngineRouter.cs:220 but never reaches the log: QueueProcessor dispatch intercepts every compatible backend/format pair BEFORE the engine insertion point (:600) -- Cjpegli/jpg hits the unconditional branch at :582, Jxr/jxr at :586, Dng/dng at :596, Cxl/jxl at :571 -- while an INCOMPATIBLE pair (e.g. -f png -e Cjxl) throws at :328 before BlockReason is consulted. BlockReason is still evaluated inside ShouldRoute (:287) but its EngineBlock object is discarded without logging.' `
            -Precondition '-f jpg -e Cjpegli is the closest candidate (guard passes, BlockReason would return ExternalBackend) but the item is dispatched to ProcessCjpegliAsync at QueueProcessor.cs:582 and the code is never logged'),

        (New-RejectCode -Name 'AnimationScaleUnsupported' -Family 'EngineBlock' `
            -Source ($eng + ':59') -DocSource ($eng + ':58') `
            -Trigger ($router + ':329') -TriggerScope 'UnconsumedOutputSettingItems' `
            -Precondition '--animation-scale-w <non-zero> (UnconsumedOutputSettingItems item[0]; FfmpegOptions.AnimationScaleW != 0 is checked regardless of the output container)'),

        (New-RejectCode -Name 'LegacyTonemapCurve' -Family 'EngineBlock' `
            -Source ($eng + ':61') -DocSource ($eng + ':60') `
            -Trigger ($router + ':336') -TriggerScope 'UnconsumedOutputSettingItems' `
            -Precondition '--tonemap-curve <hable|mobius|reinhard> : non-empty FfmpegOptions.TonemapCurve is structurally unconsumed by the engine (use --color-tone-map instead)'),

        (New-RejectCode -Name 'GeometryWithGainMap' -Family 'EngineBlock' `
            -Source ($eng + ':63') -DocSource ($eng + ':62') `
            -Trigger '' -TriggerScope '' -Reachable $false `
            -UnreachableReason 'The guard was DELETED 2026-09-18 as a dead guard (predicate was permanently false because QueueProcessor.cs:421-470 pre-scales all inputs before format dispatch). The constant is deliberately retained for historical logs/external references (see the deletion record at ColorEngineRouter.cs:344-370); it is no longer produced anywhere.' `
            -Precondition 'historical trigger was --jpeg-gain-map true --max-dimension-enabled true --max-dimension <n>; now expected to be a no-op'),

        (New-RejectCode -Name 'ProgressiveJpeg' -Family 'EngineBlock' `
            -Source ($eng + ':65') -DocSource ($eng + ':64') `
            -Trigger ($router + ':394') -TriggerScope 'UnconsumedOutputSettingItems' `
            -Precondition '--jpeg-progressive 1 with a NON-GainMap jpeg target (JpegProgressiveId > 0 and NOT (JpegGainMap and fmt in jpg/jpeg)); the >0 test is deliberate so -1 (auto) does not trip it'),

        (New-RejectCode -Name 'EncoderUnsupportedSetting' -Family 'EngineBlock' `
            -Source ($eng + ':67') -DocSource ($eng + ':66') `
            -Trigger ($router + ':407') -TriggerScope 'UnconsumedOutputSettingItems' `
            -Precondition '-f webp --chroma 4:4:4 (libwebp has no 4:4:4). Other sub-cases: -f avif --chroma 4:4:4 with a libsvtav1 encoder, -f jxl --jxl-lossless-jpeg true'),

        # ── 降级词表（DegradationCodes，14 个）───────────────────────────────────
        (New-RejectCode -Name 'NoCicpCarrier' -Family 'Degradation' `
            -Source ($deg + ':68') -DocSource ($deg + ':67') `
            -Trigger '' -TriggerScope '' -Reachable $false `
            -UnreachableReason 'The constant exists but has NO construction site anywhere in src (grep count = 1, the declaration itself). CollectDegradations never emits it; the CICP-unavailable fact is carried by ColorTransformPlan.CicpUnavailable (an enum) instead, not by a degradation code.' `
            -Precondition 'none; closest candidate is any case that lands on a container without a CICP path (e.g. -f jpg --color-strategy carry)'),

        (New-RejectCode -Name 'NoIccCarrier' -Family 'Degradation' `
            -Source ($deg + ':70') -DocSource ($deg + ':69') `
            -Trigger ($plan + ':243') -TriggerScope 'CollectDegradations' `
            -Precondition 'source carries an ICC AND the plan ends with AttachIcc=false. Easiest: -i <jpeg-with-ICC> -f gif (gif cannot carry arbitrary ICC, so S1 maps to the sRGB working space and drops the source ICC)'),

        (New-RejectCode -Name 'HdrNoIccCarryPath' -Family 'Degradation' `
            -Source ($deg + ':72') -DocSource ($deg + ':71') `
            -Trigger '' -TriggerScope '' -Reachable $false `
            -UnreachableReason 'Name collision with a DIFFERENT type: OverrideReason.HdrNoIccCarryPath (ColorIntent.cs:64) IS written to plan.Override at ColorStrategyPlanning.cs:297/:350, but DegradationCodes.HdrNoIccCarryPath (a string const) is never constructed. Grep shows 4 hits: 1 enum member, 2 OverrideReason uses, 1 const declaration -- zero degradations.' `
            -Precondition 'the OverrideReason path needs -i <HDR source> --color-strategy carry; that path produces an Override, NOT this degradation'),

        (New-RejectCode -Name 'ContainerCannotCarryIcc' -Family 'Degradation' `
            -Source ($deg + ':74') -DocSource ($deg + ':73') `
            -Trigger ($plan + ':246') -TriggerScope 'CollectDegradations' `
            -Precondition 'source carries an ICC AND FormatColorCaps.CanCarryArbitraryIcc is false for the output container. Same case as NoIccCarrier with gif/jxr: -i <jpeg-with-ICC> -f gif'),

        (New-RejectCode -Name 'UnlabeledAssumedSrgb' -Family 'Degradation' `
            -Source ($deg + ':76') -DocSource ($deg + ':75') `
            -Trigger ($plan + ':234') -TriggerScope 'CollectDegradations' `
            -Precondition 'plan.UnlabeledAssumedSrgb is set. Two proven routes: (a) container has no colour path at all (gif/jxr) with an sRGB-equivalent target -> -f gif --color-space sRGB; (b) the confidence gate: an unlabelled source (Confidence=CodecDefault < CicpTag) with no explicit target and a container where UnlabeledMeansSrgb is true -> -f jpg'),

        (New-RejectCode -Name 'GamutClipped' -Family 'Degradation' `
            -Source ($deg + ':78') -DocSource ($deg + ':77') `
            -Trigger ($plan + ':228') -TriggerScope 'CollectDegradations' `
            -Precondition 'plan.GamutOutsideDestination = true AND plan.GamutMap = false. Proven by verify-gamut-map.ps1: --icc-file <p3.icc> --color-space sRGB on an unlabelled PNG (source metric = Display P3, P3 is not contained in sRGB) with --color-gamut-map off/absent'),

        (New-RejectCode -Name 'GamutCompressed' -Family 'Degradation' `
            -Source ($deg + ':87') -DocSource ($deg + ':79') `
            -Trigger ($plan + ':225') -TriggerScope 'CollectDegradations' `
            -Precondition 'plan.GamutOutsideDestination = true AND plan.GamutMap = true (needs --color-gamut-map on AND ColorTransformPlan.LuminancesOf(dst) non-null). Proven by verify-gamut-map.ps1 sec.2 (on vs off differ, log shows H1/GMO). Mutually exclusive with GamutClipped'),

        # ⚠ 本码现在**有三个生产点**（登记表一行只放得下一个，其余在此点名，行号按 2026-09-26 树）：
        #   ① :124 出口 8bit 容器上限（Map 且 OutBitDepth=8）；
        #   ①b :194 RGB 原生出口取到别的档（png/tiff/jxl 的 10/12 ⇒ 升位走 BitDepthRaised，同一条语句二选一）；
        #   ①b :213 **非 RGB 原生出口**（webp/jpg/avif…）：任务 #35 新增，判据 = 用户请求档
        #      （`ColorIntent.RequestedBitDepth`）> 交付档（`PlanPolicy.TargetBitDepth`）。
        #      CLI：--bit-depth 10 -f webp ⇒ 日志 降级（BitDepthReduced）：出口位深降为 8bit（本次目标 10bit）。
        #   ⚠ 注释不能放在 `` ` `` 续行链**中间**（PS 会在下一个非续行 token 处断句 ⇒ 整段解析失败）。
        (New-RejectCode -Name 'BitDepthReduced' -Family 'Degradation' `
            -Source ($deg + ':89') -DocSource ($deg + ':88') `
            -Trigger ($plan + ':124') -TriggerScope 'CollectDegradations' `
            -Precondition 'plan.Action = Map AND plan.OutBitDepth = 8, i.e. TargetBitDepth <= 8 AND !Requires16BitIntermediate AND FormatColorCaps.RgbNative (png/apng/tiff/jxl/jxr/gif). Needs a real mapping: --bit-depth 8 --color-space "Display P3" on an sRGB PNG. Second/third sites are named in the comment above; the non-RgbNative one needs an explicit --bit-depth above the container ceiling (--bit-depth 10 -f webp)'),

        (New-RejectCode -Name 'GainMapNonJpeg' -Family 'Degradation' `
            -Source ($deg + ':91') -DocSource ($deg + ':90') `
            -Trigger '' -TriggerScope '' -Reachable $false `
            -UnreachableReason 'The constant exists but has NO construction site (grep count = 1). The non-JPEG GainMap request is handled EARLIER by a contract-layer Reject (ColorStrategyPlanning.cs:181-193, "gain map is not applicable to <fmt>") which writes only free-text Reason/Advice and deliberately registers NO degradation (Reject rows never carry Degradations -- see ProjectVerdict).' `
            -Precondition 'candidate is -f png --jpeg-gain-map true, which produces a Reject with exit code 91, not this degradation'),

        (New-RejectCode -Name 'GeometryWithGainMap' -Family 'Degradation' `
            -Source ($deg + ':93') -DocSource ($deg + ':92') `
            -Trigger '' -TriggerScope '' -Reachable $false `
            -UnreachableReason 'Same-name sibling of EngineBlockCodes.GeometryWithGainMap; also never constructed (grep count = 1). Its only live descendant is a defensive throw in RawColorPipeline.cs:228-234, which is unreachable in production because the pre-scale relay at QueueProcessor.cs:421-470 always shrinks first.' `
            -Precondition 'candidate is -f jpg --jpeg-gain-map true --max-dimension-enabled true --max-dimension 128 (historically blocked by the engine router, now a normal success)'),

        (New-RejectCode -Name 'NoColorPath' -Family 'Degradation' `
            -Source ($deg + ':95') -DocSource ($deg + ':94') `
            -Trigger '' -TriggerScope '' -Reachable $false `
            -UnreachableReason 'The constant exists but has NO construction site (grep count = 1). The doc comment names GIF/JXR/PPM, but the "container has no colour management path" fact is consumed structurally (ColorIntentFactory.cs:44 gate, PlanRecommended !caps.HasColorPath branch) and surfaces as UnlabeledAssumedSrgb instead.' `
            -Precondition 'candidate is -i <jpeg-with-ICC> -f gif, which registers NoIccCarrier + ContainerCannotCarryIcc + UnlabeledAssumedSrgb, NOT NoColorPath'),

        (New-RejectCode -Name 'AnimatedNotSupported' -Family 'Degradation' `
            -Source ($deg + ':97') -DocSource ($deg + ':96') `
            -Trigger '' -TriggerScope '' -Reachable $false `
            -UnreachableReason 'The constant exists but has NO construction site (grep count = 1). Animated inputs are handled one level up: ColorEngineRouter.BlockReason returns EngineBlockCodes.AnimatedInput (a ROUTING block, not a task-level degradation), and the engine is never asked to plan an animated frame.' `
            -Precondition 'candidate is -i <*.gif> -f gif, which surfaces as EngineBlockCodes.AnimatedInput'),

        (New-RejectCode -Name 'EncoderUnsupportedSetting' -Family 'Degradation' `
            -Source ($deg + ':99') -DocSource ($deg + ':98') `
            -Trigger '' -TriggerScope '' -Reachable $false `
            -UnreachableReason 'TRAP: the string "EncoderUnsupportedSetting" IS produced, but only as EngineBlock.Code (EngineBlockCodes.EncoderUnsupportedSetting at ColorEngineRouter.cs:407). DegradationCodes.EncoderUnsupportedSetting is never constructed, so it never appears in a "degradation (<code>)" line. A naive Contains assertion on the bare name would be vacuously true here.' `
            -Precondition 'candidate is -f webp --chroma 4:4:4, which surfaces as the ENGINE-BLOCK variant of the same name'),

        (New-RejectCode -Name 'ToneMapNotAppliedToGainMap' -Family 'Degradation' `
            -Source ($deg + ':101') -DocSource ($deg + ':100') `
            -Trigger ($plan + ':261') -TriggerScope 'CollectDegradations' `
            -Precondition 'plan.GainMap = true AND it.ToneMapRequested AND NOT it.ToneMapAuto. Proven by verify-gainmap-engine.ps1 sec.7: -f jpg --jpeg-gain-map true --color-tone-map hable on an SDR source (ColorIntentFactory skips the "tonemap needs an HDR source" gate when JpegGainMap is set, on purpose)')
    )

    # ── 补 Constant / AssertTokens / Doc 三个派生字段 ──────────────────────────────
    $root = Get-RejectRepoRoot -Override $RepoRoot
    $cache = @{}
    $out = @()
    foreach ($r in $list) {
        $cls = ''
        if ($script:RejectFamilyClass.ContainsKey([string]$r.Family)) {
            $cls = [string]$script:RejectFamilyClass[[string]$r.Family]
        }
        $r.Constant = ($cls + '.' + $r.Name)

        $toks = @()
        if ($script:RejectAssertTemplates.ContainsKey([string]$r.Family)) {
            foreach ($t in @($script:RejectAssertTemplates[[string]$r.Family])) {
                $toks += ((ConvertFrom-RejectUnicodeEscape -Text $t) -f $r.Name)
            }
        }
        $r.AssertTokens = @($toks)

        # Doc：**去源码里读**（不把中文抄进本文件的字符串字面量），读失败显式记账。
        $d = ConvertFrom-RejectLocator -Locator ([string]$r.DocSource)
        if (-not $d.Ok) {
            $r.DocError = 'bad DocSource locator: ' + $d.Error
        } else {
            $abs = Resolve-RejectPath -RelPath $d.Path -RepoRoot $root
            $ln = Get-RejectSourceLine -Path $abs -LineNumber $d.Line -Cache $cache
            if ($ln.Ok) { $r.Doc = $ln.Text.Trim() }
            else { $r.DocError = $ln.Error }
        }
        $out += $r
    }
    return $out
}

# ══════════════════════════════════════════════════════════════════════════════
# 2) 用例表（每个码 x {正控, 负控}）
# ══════════════════════════════════════════════════════════════════════════════

# 造一条用例。Args 是**可直接喂 ConvertTo-CliArgs 形态**的完整参数数组
# （自带 `--headless -i <源> -o <目录> -f <格式> --log-level Debug`）。
# ⚠ `--log-level Debug` 不可省：成功项的 item.Log 默认不回显，
#   少了它「日志里有降级码」一类断言会**假失败**（既有门禁 verify-gamut-map.ps1 的同款注释）。
# ⚠ Expect 的语义 = 「**该码**的断言串应当出现」，**不是**「日志里只有这一个码」：
#   同一次运行可能合法地登记多个降级（例如 gif 格同时给 NoIccCarrier +
#   ContainerCannotCarryIcc + UnlabeledAssumedSrgb）⇒ 负控只判「本码不出现」。
function New-RejectCase {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Family,
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][bool]$Expect,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Args,
        [bool]$Unreachable = $false,
        [string]$Evidence = '',
        [string]$Note = ''
    )
    $a = @()
    if ($null -ne $Args) { $a = @($Args) }
    return [pscustomobject]@{
        Id          = $Id
        Code        = $Code
        Family      = $Family
        Kind        = $Kind
        Expect      = $Expect
        Args        = $a
        Unreachable = $Unreachable
        Evidence    = $Evidence
        Note        = $Note
    }
}

function Get-RejectCases {
    [CmdletBinding()]
    param(
        # 素材角色：默认值是**仓库相对路径**，本文件里没有任何本机绝对路径。
        #   SourceSrgb : 未标注 PNG（惯例假定 sRGB，Confidence=CodecDefault）
        #   SourceIcc  : 带 ICC(+EXIF+GPS) 的 JPEG
        #   SourceHdr  : PQ/BT.2020 PNG
        #   SourceAnim : 动图 GIF（.gif 输入的「可能多帧」判据恒 true）
        #   P3Icc      : Display P3 的 ICC（用 --icc-file 把**源**判定为 P3）
        [string]$SourceSrgb = 'tests/output/sources/src_8bit.png',
        [string]$SourceIcc = 'tests/output/sources/src_photo.jpg',
        [string]$SourceHdr = 'tests/output/sources/src_hdr_pq.png',
        [string]$SourceAnim = 'tests/output/sources/anim_gif.gif',
        [string]$P3Icc = 'tests/output/validate/caps/p3.icc',
        [string]$OutDir = 'out',
        $Registry
    )
    if ($null -eq $Registry) { $Registry = @(Get-RejectCodeRegistry) }
    # ⚠ 两个家族共用同名字符串（`EncoderUnsupportedSetting` / `GeometryWithGainMap`）⇒
    #   码名单独**不唯一**，可达性映射键一律用 `Family|Name`（与 A2 的口径一致）。
    $reachOf = @{}
    foreach ($r in @($Registry)) {
        $reachOf[([string]$r.Family + '|' + [string]$r.Name)] = [bool]$r.Reachable
    }

    $cases = New-Object System.Collections.ArrayList

    # 局部构造器：闭包捕获 $OutDir / $cases / $famOf / $reachOf。
    # 刻意**不让数组跨函数边界返回**（`_lib-axes.ps1` 记录过「空数组经管道展开成 $null /
    # 经 @() 包装成 1 元素」的陷阱）—— 这里数组只作为 pscustomobject 的属性存在。
    $add = {
        param(
            [string]$Code, [string]$Kind, [bool]$Expect, [string]$Fmt, [string]$Src,
            [string[]]$Extra, [string]$Evidence, [string]$Note, [string]$Fam
        )
        $a = @('--headless', '-i', $Src, '-o', $OutDir, '-f', $Fmt, '--log-level', 'Debug')
        if ($null -ne $Extra) { $a = $a + @($Extra) }
        # Family 解析：只在码名**唯一**时自动推断；同名跨家族（2 个码）必须由调用点显式给 `-Fam`，
        # 推断不出来就留空 ⇒ 下面 B3/B5 会转红（fail-closed，不允许含糊地猜一个家族）。
        if ([string]::IsNullOrWhiteSpace($Fam)) {
            $hits = @($Registry | Where-Object { [string]$_.Name -eq $Code })
            if ($hits.Count -eq 1) { $Fam = [string]$hits[0].Family }
        }
        if ([string]::IsNullOrWhiteSpace($Fam)) { $Fam = '' }
        $un = $true
        $rk = ($Fam + '|' + $Code)
        if ($reachOf.ContainsKey($rk)) { $un = -not [bool]$reachOf[$rk] }
        $id = ('R-{0}-{1}-{2}' -f $Fam, $Code, $Kind)
        [void]$cases.Add((New-RejectCase -Id $id -Code $Code -Family $Fam -Kind $Kind `
            -Expect $Expect -Args $a -Unreachable $un -Evidence $Evidence -Note $Note))
    }

    # ── 家族 EngineBlock ────────────────────────────────────────────────────────
    & $add 'OptionsNull' 'positive' $false 'png' $SourceSrgb @('--color-engine', 'engine') `
        'derived-from-source' 'defensive branch; kept so the registry is complete'
    & $add 'OptionsNull' 'negative' $false 'png' $SourceSrgb @() `
        'derived-from-source' 'plain case; no block code at all'

    & $add 'FormatNotSupported' 'positive' $true 'bmp' $SourceSrgb @() `
        'derived-from-source' 'bmp output ext is outside IsEngineEncodable'
    & $add 'FormatNotSupported' 'negative' $false 'png' $SourceSrgb @() `
        'derived-from-source' 'png is in the engine encode whitelist'

    & $add 'AnimatedInput' 'positive' $true 'png' $SourceAnim @() `
        'derived-from-source' '.gif input => InputHasMultipleFramesAsync true unconditionally'
    & $add 'AnimatedInput' 'negative' $false 'png' $SourceSrgb @() `
        'derived-from-source' 'still image input => single frame'

    & $add 'ExternalBackend' 'positive' $false 'jpg' $SourceSrgb @('-e', 'Cjpegli') `
        'derived-from-source' 'guard passes but QueueProcessor.cs:582 intercepts before the engine insertion point => code never logged'
    & $add 'ExternalBackend' 'negative' $false 'jpg' $SourceSrgb @('-e', 'Ffmpeg') `
        'derived-from-source' 'Ffmpeg backend is the exception-free default'

    & $add 'AnimationScaleUnsupported' 'positive' $true 'png' $SourceSrgb @('--animation-scale-w', '320') `
        'derived-from-source' 'UnconsumedOutputSettingItems item[0] when AnimationScaleW != 0'
    & $add 'AnimationScaleUnsupported' 'negative' $false 'png' $SourceSrgb @('--animation-scale-w', '0') `
        'derived-from-source' 'explicit zero == no animation scaling'

    & $add 'LegacyTonemapCurve' 'positive' $true 'png' $SourceSrgb @('--tonemap-curve', 'hable') `
        'derived-from-source' 'legacy --tonemap-curve is structurally unconsumed by the engine'
    & $add 'LegacyTonemapCurve' 'negative' $false 'png' $SourceSrgb @() `
        'derived-from-source' 'no legacy curve requested'

    # ⚠ 同名跨家族：`GeometryWithGainMap` / `EncoderUnsupportedSetting` 在两套词表里都有，
    #   码名**不唯一** => 这里必须显式给 `-Fam`，否则 $add 推断不出家族、留空后由 B3/B5 转红。
    & $add 'GeometryWithGainMap' 'positive' $false 'jpg' $SourceSrgb `
        @('--jpeg-gain-map', 'true', '--max-dimension-enabled', 'true', '--max-dimension', '128') `
        'derived-from-source' 'historical trigger; guard deleted 2026-09-18 as a dead guard' `
        -Fam 'EngineBlock'
    & $add 'GeometryWithGainMap' 'negative' $false 'jpg' $SourceSrgb @('--jpeg-gain-map', 'true') `
        'derived-from-source' 'GainMap alone no longer trips anything in the router' `
        -Fam 'EngineBlock'

    & $add 'ProgressiveJpeg' 'positive' $true 'jpg' $SourceSrgb @('--jpeg-progressive', '1') `
        'derived-from-source' 'JpegProgressiveId > 0 and not a GainMap jpeg'
    & $add 'ProgressiveJpeg' 'negative' $false 'jpg' $SourceSrgb @('--jpeg-progressive', '0') `
        'derived-from-source' 'baseline JPEG; note -1 (auto) must NOT trip it either'

    & $add 'EncoderUnsupportedSetting' 'positive' $true 'webp' $SourceSrgb @('--chroma', '4:4:4') `
        'derived-from-source' 'libwebp accepts only 4:2:0 / bgra' -Fam 'EngineBlock'
    & $add 'EncoderUnsupportedSetting' 'negative' $false 'webp' $SourceSrgb @('--chroma', '4:2:0') `
        'derived-from-source' '4:2:0 is exactly what libwebp accepts' -Fam 'EngineBlock'

    # ── 家族 Degradation ───────────────────────────────────────────────────────
    & $add 'NoCicpCarrier' 'positive' $false 'jpg' $SourceSrgb @('--color-strategy', 'carry') `
        'derived-from-source' 'constant has no construction site anywhere in src'
    & $add 'NoCicpCarrier' 'negative' $false 'png' $SourceSrgb @('--color-strategy', 'carry') `
        'derived-from-source' 'png can write cICP; the code does not exist in either case'

    & $add 'NoIccCarrier' 'positive' $true 'gif' $SourceIcc @() `
        'derived-from-source' 'gif cannot carry arbitrary ICC => S1 maps to sRGB and drops the source ICC'
    & $add 'NoIccCarrier' 'negative' $false 'png' $SourceIcc @() `
        'derived-from-source' 'png carries the source ICC (Action=CarryIcc, AttachIcc=true)'

    & $add 'HdrNoIccCarryPath' 'positive' $false 'png' $SourceHdr @('--color-strategy', 'carry') `
        'derived-from-source' 'this path writes OverrideReason, not the same-named degradation'
    & $add 'HdrNoIccCarryPath' 'negative' $false 'png' $SourceHdr @() `
        'derived-from-source' 'default strategy; no carry override at all'

    & $add 'ContainerCannotCarryIcc' 'positive' $true 'gif' $SourceIcc @() `
        'derived-from-source' 'FormatColorCaps.CanCarryArbitraryIcc is false for gif'
    & $add 'ContainerCannotCarryIcc' 'negative' $false 'png' $SourceIcc @() `
        'derived-from-source' 'CanCarryArbitraryIcc is true for png'

    & $add 'UnlabeledAssumedSrgb' 'positive' $true 'gif' $SourceSrgb @('--color-space', 'sRGB') `
        'gate:verify-color-strategy' 'gif has no colour path at all; sRGB-equivalent target => UnlabeledAssumedSrgb'
    & $add 'UnlabeledAssumedSrgb' 'negative' $false 'png' $SourceSrgb @('--color-space', 'sRGB') `
        'gate:verify-color-strategy' 'png has cICP/ICC paths => the same options must NOT register it'

    & $add 'GamutClipped' 'positive' $true 'png' $SourceSrgb @('--icc-file', $P3Icc, '--color-space', 'sRGB') `
        'gate:verify-gamut-map' 'source metric = Display P3, target sRGB, GMO off => hard clip'
    & $add 'GamutClipped' 'negative' $false 'png' $SourceSrgb @('--color-space', 'Display P3') `
        'derived-from-source' 'sRGB source is contained in Display P3 => not outside destination'

    & $add 'GamutCompressed' 'positive' $true 'png' $SourceSrgb `
        @('--icc-file', $P3Icc, '--color-space', 'sRGB', '--color-gamut-map', 'on') `
        'gate:verify-gamut-map' 'same out-of-gamut case with GMO on => GamutMap=true, mutually exclusive with GamutClipped'
    & $add 'GamutCompressed' 'negative' $false 'png' $SourceSrgb @('--icc-file', $P3Icc, '--color-space', 'sRGB') `
        'derived-from-source' 'GMO off => registers GamutClipped instead, never GamutCompressed'

    & $add 'BitDepthReduced' 'positive' $true 'png' $SourceSrgb @('--bit-depth', '8', '--color-space', 'Display P3') `
        'derived-from-source' 'Map + OutBitDepth 8 (png is RgbNative, TargetBitDepth<=8, no 16-bit intermediate needed)'
    & $add 'BitDepthReduced' 'negative' $false 'png' $SourceSrgb @('--bit-depth', '16', '--color-space', 'Display P3') `
        'derived-from-source' 'TargetBitDepth 16 => Needs16Bit true => OutBitDepth 16'

    & $add 'GainMapNonJpeg' 'positive' $false 'png' $SourceSrgb @('--jpeg-gain-map', 'true') `
        'derived-from-source' 'non-JPEG GainMap is rejected earlier at the contract layer (exit 91), with no code'
    & $add 'GainMapNonJpeg' 'negative' $false 'jpg' $SourceSrgb @('--jpeg-gain-map', 'true') `
        'derived-from-source' 'jpeg is the GainMap-capable family, so no non-jpeg degradation'

    & $add 'GeometryWithGainMap' 'positive' $false 'jpg' $SourceSrgb `
        @('--jpeg-gain-map', 'true', '--max-dimension-enabled', 'true', '--max-dimension', '128') `
        'derived-from-source' 'same-name sibling of the engine-block constant; also never constructed' `
        -Fam 'Degradation'
    & $add 'GeometryWithGainMap' 'negative' $false 'jpg' $SourceSrgb @('--jpeg-gain-map', 'true') `
        'derived-from-source' 'pre-scale relay makes the historical guard permanently false' `
        -Fam 'Degradation'

    & $add 'NoColorPath' 'positive' $false 'gif' $SourceIcc @() `
        'derived-from-source' 'constant has no construction site; gif surfaces UnlabeledAssumedSrgb instead'
    & $add 'NoColorPath' 'negative' $false 'png' $SourceIcc @() `
        'derived-from-source' 'png has a colour path'

    & $add 'AnimatedNotSupported' 'positive' $false 'gif' $SourceAnim @() `
        'derived-from-source' 'animated input surfaces as the engine-block AnimatedInput instead'
    & $add 'AnimatedNotSupported' 'negative' $false 'gif' $SourceSrgb @() `
        'derived-from-source' 'still input; same output container'

    & $add 'EncoderUnsupportedSetting' 'positive' $false 'webp' $SourceSrgb @('--chroma', '4:4:4') `
        'derived-from-source' 'TRAP: the bare name appears, but only as EngineBlock.Code, never as a degradation' `
        -Fam 'Degradation'
    & $add 'EncoderUnsupportedSetting' 'negative' $false 'webp' $SourceSrgb @('--chroma', '4:2:0') `
        'derived-from-source' 'libwebp-compatible chroma' -Fam 'Degradation'

    & $add 'ToneMapNotAppliedToGainMap' 'positive' $true 'jpg' $SourceSrgb `
        @('--jpeg-gain-map', 'true', '--color-tone-map', 'hable') `
        'gate:verify-gainmap-engine' 'SDR source + explicit tonemap + GainMap: produced, but the curve is not consumed'
    & $add 'ToneMapNotAppliedToGainMap' 'negative' $false 'jpg' $SourceSrgb @('--jpeg-gain-map', 'true') `
        'gate:verify-gainmap-engine' 'no explicit tonemap => ToneMapRequested false'

    return $cases.ToArray()
}

# ══════════════════════════════════════════════════════════════════════════════
# 3) 结构校验器（自检与负控共用同一个校验器 —— 负控证明的就是「它有牙」）
# ══════════════════════════════════════════════════════════════════════════════

# 返回 @{ Pass; Fail; Lines }。Lines 是逐条断言的可读行。
function Test-RejectStructure {
    [CmdletBinding()]
    param(
        $Registry,
        $Cases,
        [string]$RepoRoot = ''
    )

    # 用 hashtable 当累加器：脚本块在子作用域执行，只有引用类型能带出改动。
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
    $cses = @($Cases)
    $root = Get-RejectRepoRoot -Override $RepoRoot
    $cache = @{}

    # ── A 段：登记表 ────────────────────────────────────────────────────────────
    & $check ($reg.Count -ge 1) 'A1' ('registry non-empty (codes={0})' -f $reg.Count)

    # ⚠ 唯一性键是 **(Family, Name)** 而不是 Name：`EncoderUnsupportedSetting` 与
    #   `GeometryWithGainMap` **同时**存在于两套词表里（EngineBlock.cs:63/:67 与
    #   ColorVerdict.cs:93/:99）。把 Name 当全局唯一键会把两个真实存在的码判成重复。
    $keys = @($reg | ForEach-Object { ([string]$_.Family + '|' + [string]$_.Name) })
    $dupNames = @()
    foreach ($n in @($keys | Sort-Object -Unique)) {
        if (@($keys | Where-Object { $_ -eq $n }).Count -gt 1) { $dupNames += $n }
    }
    & $check ($dupNames.Count -eq 0) 'A2' ('(Family,Name) pairs unique (dups={0})' -f ($dupNames -join ','))

    $badFields = @()
    foreach ($r in $reg) {
        foreach ($f in @('Name', 'Family', 'Constant', 'Source', 'DocSource', 'Precondition')) {
            if ([string]::IsNullOrWhiteSpace([string]$r.$f)) { $badFields += ('{0}.{1}' -f $r.Name, $f) }
        }
    }
    & $check ($badFields.Count -eq 0) 'A3' ('required fields non-empty (bad={0})' -f ($badFields -join ','))

    $badFam = @()
    foreach ($r in $reg) {
        if ($script:RejectFamilies -notcontains [string]$r.Family) { $badFam += ('{0}={1}' -f $r.Name, $r.Family) }
    }
    & $check ($badFam.Count -eq 0) 'A4' ('Family known (bad={0})' -f ($badFam -join ','))

    $badConst = @()
    foreach ($r in $reg) {
        $want = ([string]$script:RejectFamilyClass[[string]$r.Family] + '.' + [string]$r.Name)
        if ([string]$r.Constant -ne $want) { $badConst += ('{0}->{1} want {2}' -f $r.Name, $r.Constant, $want) }
    }
    & $check ($badConst.Count -eq 0) 'A5' ('Constant == <FamilyClass>.<Name> (bad={0})' -f ($badConst -join ','))

    # A6/A7/A8 Source：真的是「那一行」的常量声明（不是恰好提到该名字的注释行）
    $badLoc = @(); $badLine = @(); $badDecl = @()
    foreach ($r in $reg) {
        $loc = ConvertFrom-RejectLocator -Locator ([string]$r.Source)
        if (-not $loc.Ok) { $badLoc += ('{0}:{1}' -f $r.Name, $loc.Error); continue }
        $abs = Resolve-RejectPath -RelPath $loc.Path -RepoRoot $root
        $ln = Get-RejectSourceLine -Path $abs -LineNumber $loc.Line -Cache $cache
        if (-not $ln.Ok) { $badLine += ('{0}:{1}' -f $r.Name, $ln.Error); continue }
        $txt = [string]$ln.Text
        if ($txt.IndexOf([string]$r.Name) -lt 0) { $badLine += ('{0}: line does not contain the code name' -f $r.Name) }
        $isDecl = ($txt -match 'const\s+string') -and ($txt.IndexOf('= "' + [string]$r.Name + '"') -ge 0)
        if (-not $isDecl) { $badDecl += ('{0}: not a const-string declaration line' -f $r.Name) }
    }
    & $check ($badLoc.Count -eq 0) 'A6' ('Source locators parse as path:line (bad={0})' -f ($badLoc -join ','))
    & $check ($badLine.Count -eq 0) 'A7' ('Source line really contains the code name (bad={0})' -f ($badLine -join ','))
    & $check ($badDecl.Count -eq 0) 'A8' ('Source line is the const-string declaration of that code (bad={0})' -f ($badDecl -join ','))

    # A9/A10 Doc：去源码读注释行，读失败必须显式红
    $badDocLoc = @(); $badDoc = @()
    foreach ($r in $reg) {
        $loc = ConvertFrom-RejectLocator -Locator ([string]$r.DocSource)
        if (-not $loc.Ok) { $badDocLoc += ('{0}:{1}' -f $r.Name, $loc.Error); continue }
        $abs = Resolve-RejectPath -RelPath $loc.Path -RepoRoot $root
        $ln = Get-RejectSourceLine -Path $abs -LineNumber $loc.Line -Cache $cache
        if (-not $ln.Ok) { $badDoc += ('{0}:{1}' -f $r.Name, $ln.Error); continue }
        if (-not ([string]$ln.Text).Trim().StartsWith('///')) {
            $badDoc += ('{0}: DocSource line is not a /// comment' -f $r.Name)
        }
        if (-not [string]::IsNullOrEmpty([string]$r.DocError)) { $badDoc += ('{0}: DocError={1}' -f $r.Name, $r.DocError) }
        elseif ([string]::IsNullOrWhiteSpace([string]$r.Doc)) { $badDoc += ('{0}: Doc text empty' -f $r.Name) }
    }
    & $check ($badDocLoc.Count -eq 0) 'A9' ('DocSource locators parse as path:line (bad={0})' -f ($badDocLoc -join ','))
    & $check ($badDoc.Count -eq 0) 'A10' ('doc text read from source (bad={0})' -f ($badDoc -join ','))

    # A11/A12 Trigger：**只要登记了 Trigger，就必须能被行号定位、且落在登记的那个方法里**；
    #   可达的码必须**有** Trigger。不可达的码**允许**留 Trigger —— `ExternalBackend` 属于
    #   「有构造点但到不了日志」那一类，抹掉它的 Trigger 反而丢掉这条信息。
    #   A13 只强制一件事：不可达 ⇒ UnreachableReason 非空（不允许无声地宣称不可达）。
    $badTrig = @(); $badScope = @(); $badUnreach = @(); $badReach = @()
    foreach ($r in $reg) {
        $trig = [string]$r.Trigger
        $key = ([string]$r.Family + '|' + [string]$r.Name)
        if ([bool]$r.Reachable) {
            if ([string]::IsNullOrWhiteSpace($trig)) { $badReach += ('{0}: reachable but Trigger empty' -f $key); continue }
            if ([string]::IsNullOrWhiteSpace([string]$r.TriggerScope)) { $badReach += ('{0}: reachable but TriggerScope empty' -f $key); continue }
        } else {
            if ([string]::IsNullOrWhiteSpace([string]$r.UnreachableReason)) {
                $badUnreach += ('{0}: unreachable without a reason' -f $key)
            }
        }
        if ([string]::IsNullOrWhiteSpace($trig)) { continue }
        $loc = ConvertFrom-RejectLocator -Locator $trig
        if (-not $loc.Ok) { $badTrig += ('{0}:{1}' -f $key, $loc.Error); continue }
        $abs = Resolve-RejectPath -RelPath $loc.Path -RepoRoot $root
        $ln = Get-RejectSourceLine -Path $abs -LineNumber $loc.Line -Cache $cache
        if (-not $ln.Ok) { $badTrig += ('{0}:{1}' -f $key, $ln.Error); continue }
        if (([string]$ln.Text).IndexOf([string]$r.Name) -lt 0) {
            $badTrig += ('{0}: trigger line does not contain the code name' -f $key)
        }
        $sc = Get-RejectEnclosingScope -Path $abs -LineNumber $loc.Line -Cache $cache
        if (-not $sc.Ok) { $badScope += ('{0}:{1}' -f $key, $sc.Error) }
        elseif ([string]$sc.Name -ne [string]$r.TriggerScope) {
            $badScope += ('{0}: enclosing scope is {1}, registry says {2}' -f $key, $sc.Name, $r.TriggerScope)
        }
    }
    & $check ($badTrig.Count -eq 0) 'A11' ('trigger line exists and contains the code name (bad={0})' -f ($badTrig -join ','))
    & $check ($badScope.Count -eq 0) 'A12' ('trigger line lives in the declared enclosing method (bad={0})' -f ($badScope -join ','))
    & $check ($badUnreach.Count -eq 0) 'A13' ('unreachable entries carry an explicit reason (bad={0})' -f ($badUnreach -join ','))
    & $check ($badReach.Count -eq 0) 'A14' ('reachable entries carry a trigger + scope (bad={0})' -f ($badReach -join ','))

    # A15/A16/A17 断言串：非空、含码名、无残留转义
    $badTokEmpty = @(); $badTokName = @(); $badTokEsc = @()
    $allToks = @()
    foreach ($r in $reg) {
        $key = ([string]$r.Family + '|' + [string]$r.Name)
        $toks = @($r.AssertTokens)
        if ($toks.Count -eq 0) { $badTokEmpty += $key; continue }
        foreach ($t in $toks) {
            $s = [string]$t
            $allToks += $s
            if ($s.IndexOf([string]$r.Name) -lt 0) { $badTokName += ('{0}:{1}' -f $key, $s) }
            if ($s.IndexOf('\u') -ge 0) { $badTokEsc += ('{0}:{1}' -f $key, $s) }
        }
    }
    & $check ($badTokEmpty.Count -eq 0) 'A15' ('every code has at least one assert token (bad={0})' -f ($badTokEmpty -join ','))
    & $check ($badTokName.Count -eq 0) 'A16' ('every assert token contains its code name (bad={0})' -f ($badTokName -join ','))
    & $check ($badTokEsc.Count -eq 0) 'A17' ('every assert token decoded (no leftover \\u escape) (bad={0})' -f ($badTokEsc -join ','))

    # A18 断言串**全库唯一**：这是「载体选错 => 断言恒真」的正面防线。
    #     两个家族共用同名字符串（EncoderUnsupportedSetting / GeometryWithGainMap），
    #     若断言退化成裸 `（X）`，引擎拦截那条日志会让降级断言恒真 => 这里必须转红。
    $dupToks = @()
    foreach ($t in @($allToks | Sort-Object -Unique)) {
        if (@($allToks | Where-Object { $_ -eq $t }).Count -gt 1) { $dupToks += $t }
    }
    & $check ($dupToks.Count -eq 0) 'A18' ('assert tokens are globally unique across families (dups={0})' -f ($dupToks -join ' | '))

    # A19 断言串的**包装词**必须真的出现在日志消费点源码里。
    #     这一条同时校验两件事：① 包装词不是我们凭空编的；② `\uXXXX` 转义码没写错。
    #     ⚠ 必须拿**模板**（带 `{0}` 占位）去拆片段，不能拿已经 `-f` 格式化过的 token ——
    #       后者里的 `{0}` 已变成码名，整串当然不在源码里（本文件初版就踩了这个坑，
    #       表现为 A19 全红、而实际转义码是对的）。
    #     ⚠ 本仓日志生产点里的格式是 `降级（{d.Code}）` / `本次不适用引擎（{block.Code}：`
    #       ⇒ 占位符两侧的字面片段（`降级（`、`：`、`）`…）都应当能逐字命中。
    $logAbs = Resolve-RejectPath -RelPath $script:RejectLogSource -RepoRoot $root
    $logFile = Get-RejectFileLines -Path $logAbs -Cache $cache
    if (-not $logFile.Ok) {
        & $check $false 'A19' ('log source unreadable: {0}' -f $logFile.Error)
    } else {
        $logText = (@($logFile.Lines) -join "`n")
        $badFrag = @()
        foreach ($famName in @($script:RejectAssertTemplates.Keys)) {
            foreach ($tpl in @($script:RejectAssertTemplates[$famName])) {
                $decoded = ConvertFrom-RejectUnicodeEscape -Text ([string]$tpl)
                foreach ($frag in @($decoded -split '\{0\}')) {
                    if ($frag -eq '') { continue }
                    if ($logText.IndexOf($frag) -lt 0) {
                        $badFrag += ('{0} template fragment not found in log source: {1}' -f $famName, $frag)
                    }
                }
            }
        }
        & $check ($badFrag.Count -eq 0) 'A19' ('assert-token wrappers really exist in the log producer (bad={0})' -f ($badFrag -join ' | '))
    }

    # A20 两个桶都非空：防止将来有人「把不可达的码全删了」或「把不可达的码谎报成可达」
    $nReach = 0
    foreach ($r in $reg) { if ([bool]$r.Reachable) { $nReach++ } }
    & $check (($nReach -ge 1) -and ($nReach -lt $reg.Count)) 'A20' `
        ('both reachable and unreachable buckets are non-empty (reachable={0} of {1})' -f $nReach, $reg.Count)

    # ── B 段：用例表 ────────────────────────────────────────────────────────────
    & $check ($cses.Count -eq (2 * $reg.Count)) 'B1' `
        ('case count == 2 * code count (cases={0} codes={1})' -f $cses.Count, $reg.Count)

    $ids = @($cses | ForEach-Object { [string]$_.Id })
    & $check (@($ids | Sort-Object -Unique).Count -eq $ids.Count) 'B2' ('case Ids unique (ids={0})' -f $ids.Count)

    # ⚠ 用例 -> 登记表的查找键同样是 **(Family, Code)**：`Code` 单独不唯一
    #   （EncoderUnsupportedSetting / GeometryWithGainMap 两套词表同名）。
    $byKey = @{}
    foreach ($r in $reg) { $byKey[([string]$r.Family + '|' + [string]$r.Name)] = $r }

    $badCode = @(); $badKind = @(); $badFamCase = @(); $badUn = @()
    foreach ($c in $cses) {
        $ck = ([string]$c.Family + '|' + [string]$c.Code)
        if (-not $byKey.ContainsKey($ck)) { $badCode += [string]$c.Id; continue }
        if ($script:RejectKinds -notcontains [string]$c.Kind) { $badKind += [string]$c.Id }
        if ([string]$c.Family -ne [string]$byKey[$ck].Family) { $badFamCase += [string]$c.Id }
        $wantUn = -not [bool]$byKey[$ck].Reachable
        if ([bool]$c.Unreachable -ne $wantUn) { $badUn += [string]$c.Id }
    }
    & $check ($badCode.Count -eq 0) 'B3' ('every (Family,Code) exists in the registry (bad={0})' -f ($badCode -join ','))
    & $check ($badKind.Count -eq 0) 'B4' ('every case Kind in vocabulary (bad={0})' -f ($badKind -join ','))
    & $check ($badFamCase.Count -eq 0) 'B5' ('every case Family matches the registry (bad={0})' -f ($badFamCase -join ','))
    & $check ($badUn.Count -eq 0) 'B6' ('every case Unreachable flag matches the registry (bad={0})' -f ($badUn -join ','))

    # B7 每码恰好 1 正 1 负（缺一即红 —— 这是 L4 的核心要求）
    $badPair = @()
    foreach ($r in $reg) {
        $rk = ([string]$r.Family + '|' + [string]$r.Name)
        $p = @($cses | Where-Object { (([string]$_.Family + '|' + [string]$_.Code) -eq $rk) -and ([string]$_.Kind -eq 'positive') })
        $n = @($cses | Where-Object { (([string]$_.Family + '|' + [string]$_.Code) -eq $rk) -and ([string]$_.Kind -eq 'negative') })
        if (($p.Count -ne 1) -or ($n.Count -ne 1)) {
            $badPair += ('{0}(pos={1} neg={2})' -f $rk, $p.Count, $n.Count)
        }
    }
    & $check ($badPair.Count -eq 0) 'B7' ('every code has exactly one positive and one negative case (bad={0})' -f ($badPair -join ','))

    # B8 Expect 契约：正控 Expect == Reachable；负控 Expect 恒 false
    $badExpect = @()
    foreach ($c in $cses) {
        $ck = ([string]$c.Family + '|' + [string]$c.Code)
        if (-not $byKey.ContainsKey($ck)) { continue }
        $want = $false
        if ([string]$c.Kind -eq 'positive') { $want = [bool]$byKey[$ck].Reachable }
        if ([bool]$c.Expect -ne $want) {
            $badExpect += ('{0}(expect={1} want={2})' -f $c.Id, $c.Expect, $want)
        }
    }
    & $check ($badExpect.Count -eq 0) 'B8' ('Expect == (positive and reachable); negatives always expect false (bad={0})' -f ($badExpect -join ','))

    # B9 正控与负控的 Args **必须不同**（否则负控是空跑 => 恒真）
    $sameArgs = @()
    foreach ($r in $reg) {
        $rk = ([string]$r.Family + '|' + [string]$r.Name)
        $p = @($cses | Where-Object { (([string]$_.Family + '|' + [string]$_.Code) -eq $rk) -and ([string]$_.Kind -eq 'positive') })
        $n = @($cses | Where-Object { (([string]$_.Family + '|' + [string]$_.Code) -eq $rk) -and ([string]$_.Kind -eq 'negative') })
        if (($p.Count -ne 1) -or ($n.Count -ne 1)) { continue }
        $ps = (@($p[0].Args) -join ' ')
        $ns = (@($n[0].Args) -join ' ')
        if ($ps -eq $ns) { $sameArgs += $rk }
    }
    & $check ($sameArgs.Count -eq 0) 'B9' ('positive and negative Args differ (otherwise the negative control is a no-op) (bad={0})' -f ($sameArgs -join ','))

    # B10 Args 非空、无空 token
    $badArgs = @()
    foreach ($c in $cses) {
        $t = @($c.Args)
        if ($t.Count -eq 0) { $badArgs += [string]$c.Id; continue }
        $ti = -1
        foreach ($x in $t) {
            $ti++
            if ($null -eq $x) { $badArgs += ('{0}[{1}]=null' -f $c.Id, $ti); break }
            if ([string]::IsNullOrEmpty([string]$x)) { $badArgs += ('{0}[{1}]=empty' -f $c.Id, $ti); break }
        }
    }
    & $check ($badArgs.Count -eq 0) 'B10' ('every case Args non-empty with no empty tokens (bad={0})' -f ($badArgs -join ','))

    # B11 每条用例都带齐基座（--headless / -i / -o / -f / --log-level）
    $badBase = @()
    foreach ($c in $cses) {
        $t = @($c.Args)
        if (($t -notcontains '--headless') -or ($t -notcontains '-i') -or ($t -notcontains '-o') `
            -or ($t -notcontains '-f') -or ($t -notcontains '--log-level')) { $badBase += [string]$c.Id }
    }
    & $check ($badBase.Count -eq 0) 'B11' ('every case carries --headless -i -o -f --log-level (bad={0})' -f ($badBase -join ','))

    # B12 Evidence 字段必须非空（登记「这条用例是既有门禁实证过的，还是从源码推出来的」）
    $badEv = @()
    foreach ($c in $cses) {
        if ([string]::IsNullOrWhiteSpace([string]$c.Evidence)) { $badEv += [string]$c.Id }
    }
    & $check ($badEv.Count -eq 0) 'B12' ('every case records its evidence source (bad={0})' -f ($badEv -join ','))

    return @{ Pass = [int]$acc['Pass']; Fail = [int]$acc['Fail']; Lines = $acc['Lines'].ToArray() }
}

# ══════════════════════════════════════════════════════════════════════════════
# 4) 自检
# ══════════════════════════════════════════════════════════════════════════════

function Invoke-RejectSelfTest {
    [CmdletBinding()]
    param(
        [switch]$InjectPhantomCode
    )

    # 幽灵码记录：Source 指向一条**真实存在、但内容不含该码名**的行
    # （ColorVerdict.cs:66 只有 `{`）。同一份数据供两处使用：
    #   · B2 负控 —— 与合成表拼起来，证明 A7/A8 真的会转红；
    #   · -InjectPhantomCode 顶层变异 —— 直接拼进**真实**登记表。
    # ⚠ 变异必须打在主跑那一份数据上（与 `_lib-axes.ps1 -InjectEmptyAxis` 同一种做法）：
    #   若只跑一趟「内部另算」的变异，外部读数仍是 fail=0，「注入后转红」就没有证据。
    $phantom = [pscustomobject]@{
        Name = $script:RejectPhantomName; Family = 'Degradation'
        Constant = ('DegradationCodes.' + $script:RejectPhantomName)
        Source = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:66'
        DocSource = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:65'
        Doc = 'syn'; DocError = ''
        Trigger = 'src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs:123'
        TriggerScope = 'CollectDegradations'
        Reachable = $true; UnreachableReason = ''
        Precondition = 'syn'
        AssertTokens = @((ConvertFrom-RejectUnicodeEscape -Text $script:RejectAssertTemplates['Degradation'][0]) -f $script:RejectPhantomName)
    }

    $registry = @(Get-RejectCodeRegistry)
    if ($InjectPhantomCode) { $registry = @($registry) + @($phantom) }
    $cases = @(Get-RejectCases -Registry $registry)

    $nReach = 0
    foreach ($r in $registry) { if ([bool]$r.Reachable) { $nReach++ } }
    Write-Output ('_lib-reject self-test (codes={0} reachable={1} unreachable={2} cases={3})' -f `
        $registry.Count, $nReach, ($registry.Count - $nReach), $cases.Count)

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

    # 断言「**某个具体编号**真的转了红」——比 `Fail -ge 1` 有判别力得多：
    # 后者在「数据本身不完整」（例如用例数与码数不匹配）时也会为真，
    # 于是「校验器有牙」与「我写漏了」两件事会混成同一个绿。
    $hasFail = {
        param($Res, [string]$Id)
        foreach ($l in @($Res.Lines)) {
            if ($l.StartsWith('  FAIL ' + $Id + ' ')) { return $true }
        }
        return $false
    }

    # ── A 段：真实数据（-InjectPhantomCode 打开时，跑的就是被注入过的那一份）──
    $res = Test-RejectStructure -Registry $registry -Cases $cases
    foreach ($l in @($res.Lines)) { Write-Output $l }
    $tally['Pass'] = [int]$tally['Pass'] + [int]$res.Pass
    $tally['Fail'] = [int]$tally['Fail'] + [int]$res.Fail

    # C1：变异开关的**读数**断言。A 段跑的就是被注入的那一份，故不必再跑第二趟；
    #     它核对「A7/A8 确实因注入而转红」，而红计数已经落在上面的 tally 里
    #     => 打开开关时末行 fail>=2（转红），关闭时 fail=0（转绿）。
    if ($InjectPhantomCode) {
        & $ck (((& $hasFail $res 'A7') -or (& $hasFail $res 'A8'))) 'C1' `
            ('mutation: -InjectPhantomCode on the real registry turns A7/A8 red (A7={0} A8={1} totalFail={2})' -f `
                (& $hasFail $res 'A7'), (& $hasFail $res 'A8'), $res.Fail)
    }

    # ── B 段：负控 / 正控（合成数据，绝不改动真实登记表）──────────────────────────
    # 合成一套最小登记表：1 条可达的 EngineBlock + 1 条可达的 Degradation。
    # Source / DocSource / Trigger 都指向**真实的**源码行，所以干净合成数据必须 0 fail。
    $synEng = [pscustomobject]@{
        Name = 'FormatNotSupported'; Family = 'EngineBlock'; Constant = 'EngineBlockCodes.FormatNotSupported'
        Source = 'src/FfmpegGui/Services/ColorMapping/EngineBlock.cs:53'
        DocSource = 'src/FfmpegGui/Services/ColorMapping/EngineBlock.cs:52'
        Doc = 'syn'; DocError = ''
        Trigger = 'src/FfmpegGui/Services/ColorMapping/ColorEngineRouter.cs:184'
        TriggerScope = 'BlockReason'
        Reachable = $true; UnreachableReason = ''
        Precondition = 'syn'
        AssertTokens = @((ConvertFrom-RejectUnicodeEscape -Text $script:RejectAssertTemplates['EngineBlock'][0]) -f 'FormatNotSupported')
    }
    $synDeg = [pscustomobject]@{
        Name = 'BitDepthReduced'; Family = 'Degradation'; Constant = 'DegradationCodes.BitDepthReduced'
        Source = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:89'
        DocSource = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:88'
        Doc = 'syn'; DocError = ''
        Trigger = 'src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs:123'
        TriggerScope = 'CollectDegradations'
        Reachable = $true; UnreachableReason = ''
        Precondition = 'syn'
        AssertTokens = @((ConvertFrom-RejectUnicodeEscape -Text $script:RejectAssertTemplates['Degradation'][0]) -f 'BitDepthReduced')
    }
    # 再来一条**不可达**的（真实存在的死码），否则 A20「两个桶都非空」会红
    # —— 那会让 B1 正控变成假红，把「校验器有牙」和「合成数据本身不完整」两件事混在一起。
    $synDead = [pscustomobject]@{
        Name = 'NoCicpCarrier'; Family = 'Degradation'; Constant = 'DegradationCodes.NoCicpCarrier'
        Source = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:68'
        DocSource = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:67'
        Doc = 'syn'; DocError = ''
        Trigger = ''; TriggerScope = ''
        Reachable = $false; UnreachableReason = 'syn: no construction site anywhere in src'
        Precondition = 'syn'
        AssertTokens = @((ConvertFrom-RejectUnicodeEscape -Text $script:RejectAssertTemplates['Degradation'][0]) -f 'NoCicpCarrier')
    }
    $synReg = @($synEng, $synDeg, $synDead)

    $synCases = @(
        (New-RejectCase -Id 'R-EngineBlock-FormatNotSupported-positive' -Code 'FormatNotSupported' -Family 'EngineBlock' `
            -Kind 'positive' -Expect $true -Args @('--headless', '-i', 'a.png', '-o', 'out', '-f', 'bmp', '--log-level', 'Debug') `
            -Evidence 'syn'),
        (New-RejectCase -Id 'R-EngineBlock-FormatNotSupported-negative' -Code 'FormatNotSupported' -Family 'EngineBlock' `
            -Kind 'negative' -Expect $false -Args @('--headless', '-i', 'a.png', '-o', 'out', '-f', 'png', '--log-level', 'Debug') `
            -Evidence 'syn'),
        (New-RejectCase -Id 'R-Degradation-BitDepthReduced-positive' -Code 'BitDepthReduced' -Family 'Degradation' `
            -Kind 'positive' -Expect $true -Args @('--headless', '-i', 'a.png', '-o', 'out', '-f', 'png', '--log-level', 'Debug', '--bit-depth', '8') `
            -Evidence 'syn'),
        (New-RejectCase -Id 'R-Degradation-BitDepthReduced-negative' -Code 'BitDepthReduced' -Family 'Degradation' `
            -Kind 'negative' -Expect $false -Args @('--headless', '-i', 'a.png', '-o', 'out', '-f', 'png', '--log-level', 'Debug', '--bit-depth', '16') `
            -Evidence 'syn'),
        (New-RejectCase -Id 'R-Degradation-NoCicpCarrier-positive' -Code 'NoCicpCarrier' -Family 'Degradation' `
            -Kind 'positive' -Expect $false -Args @('--headless', '-i', 'a.png', '-o', 'out', '-f', 'jpg', '--log-level', 'Debug', '--color-strategy', 'carry') `
            -Unreachable $true -Evidence 'syn'),
        (New-RejectCase -Id 'R-Degradation-NoCicpCarrier-negative' -Code 'NoCicpCarrier' -Family 'Degradation' `
            -Kind 'negative' -Expect $false -Args @('--headless', '-i', 'a.png', '-o', 'out', '-f', 'png', '--log-level', 'Debug', '--color-strategy', 'carry') `
            -Unreachable $true -Evidence 'syn')
    )

    # B1 正控：干净合成数据 => 校验器必须 0 fail（证明它不是「恒红」）
    $b1 = Test-RejectStructure -Registry $synReg -Cases $synCases
    & $ck ($b1.Fail -eq 0) 'B1' ('positive control: clean synthetic registry+table passes (fail={0})' -f $b1.Fail)
    if ($b1.Fail -ne 0) { foreach ($l in @($b1.Lines)) { if ($l.StartsWith('  FAIL')) { Write-Output ('        ' + $l) } } }

    # B2 负控：幽灵码（$phantom 在上面定义，与 -InjectPhantomCode 共用同一份数据）
    #    —— Source 指向一条**真实存在但内容不含该码名**的行 => A7/A8 必须转红。
    #    这是「file:line 与码名来自真实源码文本」这条纪律的**牙**：
    #    ⚠ 它同时证明校验器是**去读文件那一行**的，而不是信任我写的表。
    $b2 = Test-RejectStructure -Registry (@($synReg) + @($phantom)) -Cases $synCases
    & $ck (((& $hasFail $b2 'A7') -or (& $hasFail $b2 'A8'))) 'B2' `
        ('negative control: phantom code with a fake source line turns A7/A8 red (A7={0} A8={1} totalFail={2})' -f `
            (& $hasFail $b2 'A7'), (& $hasFail $b2 'A8'), $b2.Fail)

    # B3 负控：正控与负控 Args 相同 => B9 必须转红
    $dupArgs = @(
        $synCases[0],
        (New-RejectCase -Id 'R-EngineBlock-FormatNotSupported-negative' -Code 'FormatNotSupported' -Family 'EngineBlock' `
            -Kind 'negative' -Expect $false -Args @($synCases[0].Args) -Evidence 'syn'),
        $synCases[2], $synCases[3], $synCases[4], $synCases[5]
    )
    $b3 = Test-RejectStructure -Registry $synReg -Cases $dupArgs
    & $ck (& $hasFail $b3 'B9') 'B3' ('negative control: identical positive/negative Args turns B9 red (totalFail={0})' -f $b3.Fail)

    # B4 负控：缺一条负控 => B7 必须转红（L4 的核心要求：缺一即红）
    $b4 = Test-RejectStructure -Registry $synReg -Cases @($synCases[0], $synCases[2], $synCases[3], $synCases[4], $synCases[5])
    & $ck (& $hasFail $b4 'B7') 'B4' ('negative control: a missing negative case turns B7 red (totalFail={0})' -f $b4.Fail)

    # B5 负控：(Family,Name) 重复 => A2 必须转红
    $b5 = Test-RejectStructure -Registry (@($synReg) + @($synEng)) -Cases $synCases
    & $ck (& $hasFail $b5 'A2') 'B5' ('negative control: duplicate (Family,Name) turns A2 red (totalFail={0})' -f $b5.Fail)

    # B6 负控：TriggerScope 写错（生产点行不在该方法里）=> A12 必须转红
    $wrongScope = [pscustomobject]@{
        Name = 'BitDepthReduced'; Family = 'Degradation'; Constant = 'DegradationCodes.BitDepthReduced'
        Source = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:89'
        DocSource = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:88'
        Doc = 'syn'; DocError = ''
        Trigger = 'src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs:123'
        # 故意写错：那一行的真实宿主是 CollectDegradations
        TriggerScope = 'PlanRecommended'
        Reachable = $true; UnreachableReason = ''
        Precondition = 'syn'
        AssertTokens = @((ConvertFrom-RejectUnicodeEscape -Text $script:RejectAssertTemplates['Degradation'][0]) -f 'BitDepthReduced')
    }
    $b6 = Test-RejectStructure -Registry @($synEng, $wrongScope) -Cases $synCases
    & $ck (& $hasFail $b6 'A12') 'B6' ('negative control: wrong TriggerScope turns A12 red (totalFail={0})' -f $b6.Fail)

    # B7 负控：断言串**不带家族限定**（裸 `（X）`）=> A18 必须转红。
    #    打的正是 N3 那个坑：两个家族共用同名字符串，裸名断言会让降级断言**恒真**。
    $bareTok = [pscustomobject]@{
        Name = 'EncoderUnsupportedSetting'; Family = 'Degradation'
        Constant = 'DegradationCodes.EncoderUnsupportedSetting'
        Source = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:99'
        DocSource = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:98'
        Doc = 'syn'; DocError = ''
        Trigger = ''; TriggerScope = ''; Reachable = $false
        UnreachableReason = 'syn'
        Precondition = 'syn'
        AssertTokens = @((ConvertFrom-RejectUnicodeEscape -Text '\uFF08{0}\uFF09') -f 'EncoderUnsupportedSetting')
    }
    $synEngEnc = [pscustomobject]@{
        Name = 'EncoderUnsupportedSetting'; Family = 'EngineBlock'
        Constant = 'EngineBlockCodes.EncoderUnsupportedSetting'
        Source = 'src/FfmpegGui/Services/ColorMapping/EngineBlock.cs:67'
        DocSource = 'src/FfmpegGui/Services/ColorMapping/EngineBlock.cs:66'
        Doc = 'syn'; DocError = ''
        Trigger = 'src/FfmpegGui/Services/ColorMapping/ColorEngineRouter.cs:407'
        TriggerScope = 'UnconsumedOutputSettingItems'
        Reachable = $true; UnreachableReason = ''
        Precondition = 'syn'
        AssertTokens = @((ConvertFrom-RejectUnicodeEscape -Text '\uFF08{0}\uFF09') -f 'EncoderUnsupportedSetting')
    }
    $b7 = Test-RejectStructure -Registry @($synEngEnc, $bareTok) -Cases @()
    & $ck (& $hasFail $b7 'A18') 'B7' ('negative control: family-unqualified assert tokens turn A18 red (totalFail={0})' -f $b7.Fail)

    # B8 负控：unreachable 却不给理由 => A13 必须转红
    $noReason = [pscustomobject]@{
        Name = 'NoCicpCarrier'; Family = 'Degradation'; Constant = 'DegradationCodes.NoCicpCarrier'
        Source = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:68'
        DocSource = 'src/FfmpegGui/Services/ColorMapping/ColorVerdict.cs:67'
        Doc = 'syn'; DocError = ''
        Trigger = ''; TriggerScope = ''; Reachable = $false
        UnreachableReason = ''
        Precondition = 'syn'
        AssertTokens = @((ConvertFrom-RejectUnicodeEscape -Text $script:RejectAssertTemplates['Degradation'][0]) -f 'NoCicpCarrier')
    }
    $b8 = Test-RejectStructure -Registry @($synEng, $noReason) -Cases @()
    & $ck (& $hasFail $b8 'A13') 'B8' ('negative control: unreachable code without a reason turns A13 red (totalFail={0})' -f $b8.Fail)

    # ⚠ C1（变异开关的读数断言）放在 A 段之后 —— 它核对的正是上面那一趟。
    #   这是刻意的：若把 C1 放这里、再另跑一趟内部变异，末行读数仍会是 fail=0，
    #   「注入后转红」就只剩一句自述、没有外部证据。见 `_lib-axes.ps1 -InjectEmptyAxis`。

    Write-Output ('REJECT RESULT: pass={0} fail={1}' -f $tally['Pass'], $tally['Fail'])

    # ⚠ 本函数**只打印、不返回对象**（刻意）：若它 `return` 一个 hashtable，顶层就得写
    #   `$null = Invoke-RejectSelfTest ...` 来吞掉返回值 —— 而 `$null = f` 会把 f 写进
    #   成功流的**全部**内容（含每一行 Write-Output）一起吞掉，自检就变成
    #   「静默零输出 + 退出码 0」（`_lib-axes.ps1` 初版实测踩到）。计数改由脚本作用域变量带出。
    $script:RejectSelfTestResult = [ordered]@{ Pass = [int]$tally['Pass']; Fail = [int]$tally['Fail'] }
}

# ══════════════════════════════════════════════════════════════════════════════
# 顶层：只在显式 -SelfTest 时跑自检（dot-source 时**零副作用**）
# ══════════════════════════════════════════════════════════════════════════════

if ($SelfTest) {
    Invoke-RejectSelfTest -InjectPhantomCode:$InjectPhantomCode
}

# ══════════════════════════════════════════════════════════════════════════════
# NOTES —— 核对源码时发现的「与预期不符之处」（供上层与评审对账）
# ══════════════════════════════════════════════════════════════════════════════
# N1. 23 个码里有 **10 个到不了日志**（本库最大的发现）。分四类，原因各不相同：
#     ① 「从未被构造」—— 全仓 grep 只命中声明那一行本身（6 个）：
#        DegradationCodes.NoCicpCarrier              ColorVerdict.cs:68
#        DegradationCodes.GainMapNonJpeg             ColorVerdict.cs:91
#        DegradationCodes.NoColorPath                ColorVerdict.cs:95
#        DegradationCodes.AnimatedNotSupported       ColorVerdict.cs:97
#        DegradationCodes.EncoderUnsupportedSetting  ColorVerdict.cs:99
#          （**同名字符串**只以 EngineBlockCodes 的身份被生产，落在 EngineBlock.Code）
#        DegradationCodes.GeometryWithGainMap        ColorVerdict.cs:93
#          （其同名兄弟 EngineBlockCodes.GeometryWithGainMap 是 ② 类，别混）
#     ② 「判据已被删除 / 防御分支」—— 曾经会生产，现在恒假（2 个）：
#        EngineBlockCodes.GeometryWithGainMap        EngineBlock.cs:63
#          2026-09-18 作为死判据删除（前置缩放让谓词恒假），删除记录在
#          ColorEngineRouter.cs:344-370；常量被有意保留给历史日志/外部引用。
#        EngineBlockCodes.OptionsNull                EngineBlock.cs:51
#          防御分支：没有任何 CLI 路径能传进 null FfmpegOptions
#          （Requested(null)=false，且 CliParser 一定构造 options）。
#     ③ 「有构造点，但被分发顺序截走，从不落日志」（1 个）：
#        EngineBlockCodes.ExternalBackend            EngineBlock.cs:57（详见 N2）
#     ④ 「与另一个类型的成员**同名**，被 grep 误判为已生产」（1 个）：
#        DegradationCodes.HdrNoIccCarryPath          ColorVerdict.cs:72
#          OverrideReason.HdrNoIccCarryPath（ColorIntent.cs:64）确实被写进
#          plan.Override（ColorStrategyPlanning.cs:297/:350），但那是**枚举成员**，
#          与 DegradationCodes 里那个**字符串常量**是两个东西。全仓 4 处命中里
#          0 处是降级生产点。
#     ⇒ 「码词表」比「实际会发生的损失集合」大：23 个码里只有 13 个能真的打进日志。
#       词表里存在 ≠ 会打出。任何「遍历码表逐个断言」的测试若假定每个码都能触发，
#       会得到 10 个必然红格（而不是 0 个）。
#     ⇒ 自检 A20 因此强制「可达桶与不可达桶都非空」，把这条事实钉成常量。
#
# N2. `ExternalBackend` 有生产点但**到不了日志**
#     ColorEngineRouter.cs:220 确实会构造它，但 QueueProcessor 的格式分发在引擎插入点
#     （:600）**之前**把所有「后端与格式兼容」的组合都截走了：
#       · Cjpegli + jpg  -> :582（无条件）
#       · Jxr + jxr      -> :586
#       · Dng + dng      -> :596（且 dng 在白名单外，先被 FormatNotSupported 挡）
#       · Cjxl + jxl     -> :571（引擎可走时落到 else，但 BlockReason 对 jxl+Cjxl **豁免**）
#     而「后端与格式不兼容」的组合（如 `-f png -e Cjxl`）在 :328 **直接抛异常**，
#     连 BlockReason 都不会被问。
#     ⇒ BlockReason 仍会在 `ShouldRoute`（:287）里被求值，但返回的 EngineBlock 被丢弃、
#       从不落日志。**这是与 `GeometryWithGainMap` 同类但更隐蔽的一类死判据**：
#       它不在路由层死，而是在**分发顺序**上死。
#
# N3. 两个家族**共用同一个字符串**，裸名断言会恒真
#     `EncoderUnsupportedSetting` 与 `GeometryWithGainMap` 同时出现在
#     `EngineBlockCodes` 与 `DegradationCodes` 里（EngineBlock.cs:63/:67 与
#     ColorVerdict.cs:93/:99）。若断言写成「日志含 EncoderUnsupportedSetting」，
#     那么引擎拦截那条日志会让「降级码」的断言**恒真**。
#     ⇒ 本库的断言串一律带家族限定前缀，并有 A18「全库断言串唯一」这条锁。
#
# N4. 降级码的载体是**两条**日志行，格式不同、标签不同
#     引擎侧：`[色彩引擎] ⚠️ 降级（{Code}）：{Text}`   QueueProcessor.cs:950
#     传统侧：`[色彩裁决] ⚠️ 降级（{Code}）：{Text}`   QueueProcessor.cs:649
#     两者 `降级（{Code}）` 片段逐字相同 ⇒ 断言串对两条管线都成立（这是有意的）。
#     ⚠ 但传统侧那条**只在 `engPlan == null` 时打印**（:643），即「裁决由本消费点算出来」
#       才算 —— 否则同一条损失会在日志里出现两次。用 `-e legacy` 跑降级用例时要注意
#       走的是哪条分支。
#
# N5. **拒绝路径没有稳定码**（本库覆盖不到的一半）
#     `ColorAction.Reject` 一族（`ColorStrategyPlanning.Reject` 助手，:698）只写自由文本
#     `Reason`/`Advice`（+ 可选结构化 `Alternatives`），**没有 Code**。
#     全仓有 **16** 个 `return Reject(...)` 调用点（:184/:228/:342/:357/:372/:412/:440/:448/
#     :453/:491/:496/:501/:508/:557/:615/:626），每一个都是一条独立的拒绝语义，
#     但**没有任何稳定标识符**可供门禁断言 —— 只能匹配中文文案，而文案会改。
#     ⇒ L4 的「每个拒绝码一条正控+一条负控」目前只能覆盖**有码**的 23 个；
#       「无码拒绝」这一族要等 `ColorAction.Reject` 也带上 Code 才能纳入（属产品改动）。
#       这是本波次**未能覆盖**的部分，如实登记。
#
# N6. 触发前置条件全部来自**源码推导**，没有一条由本波次端到端实测
#     原因：本波次有并行的构建子代理，硬约束禁止本代理跑 `dotnet build`、
#     也禁止运行 `src/FfmpegGui/bin/**` 与 `tests/ServiceProbe/bin/**` 下的 exe。
#     时间戳核对（本文件写盘时）：
#       · 被测源码最新 = ColorEngineRouter.cs  2026-09-19 10:39:57
#                        QueueProcessor.cs    2026-09-19 10:02:22
#       · src/FfmpegGui/bin/Debug/.../FfmpegGui.exe    2026-09-18 12:23:54  ← 比源码旧
#       · publish/FfmpegGui.exe                        2026-08-30 07:01:23  ← 更旧
#       · src/FfmpegGui/bin/Release/.../FfmpegGui.exe  2026-09-19 16:24:20
#         ⚠ 这一份是**并行构建子代理**在我写盘前后刚产出的（不是本代理跑的），
#           它的新鲜度取决于那一侧什么时候再构建 ⇒ **不可作为本库的稳定基线**。
#     ⇒ 本波次一律不把「跑 exe 看日志」当作证据来源。
#     故用例分两类，`Evidence` 字段如实标注：
#       · `gate:<脚本名>`  —— 该组合已被一条**现存绿色门禁**断言过（4 条用例属此类）
#       · `derived-from-source` —— 从源码分支逐条推导，**尚未端到端实测**
#     消费方（P2-E 的执行阶段）应在构建稳定后先跑一遍正控，把 derived 的转成实测；
#     届时**唯一需要改的就是 `Evidence` 字段**，断言串与 Args 不用动。
#     消费方（P2-E 的执行阶段）应在构建恢复后先跑一遍正控，把 derived 的转成实测。
