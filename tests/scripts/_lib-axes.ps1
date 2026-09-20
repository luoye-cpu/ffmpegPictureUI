# _lib-axes.ps1 -- 穷举组合测试地基①：参数轴注册表 + L1 用例生成器
#
# 定位（为什么是「库」而不是「门禁」）：
#   本文件**不产 PASS/FAIL 汇总门禁**，只被 dot-source 复用（写法对齐同目录 `_lib-color-assets.ps1`）。
#   它回答一个问题：**这个 CLI 到底有多少个可以独立拨动的输入轴、每个轴有哪些合法取值**。
#   上层（L2 两两组合 / L3 全组合 / 覆盖矩阵）都从这里取轴，避免「几处各写一份轴表」的老毛病。
#
# 四条对外能力：
#   Get-AxisRegistry      -- 全部输入轴的注册表（Name/Group/Values/CliKey/Source/Kind）
#   Get-SingleAxisCases   -- L1：单变量扫描（其余轴取默认，本轴遍历全部取值）
#   ConvertTo-CliArgs     -- 单个用例 -> 命令行字符串（**只用于打印/记账，不用于执行**）
#   Get-CaseSummary       -- @{ TotalAxes; TotalValues; TotalCases }
#   Invoke-AxesSelfTest   -- 结构自检，末行 `AXES RESULT: pass=n fail=0`
#
# 用例的 Args 是**可直接喂给主程序的完整参数数组**（自带 `--headless -i <源> -o <目录>`）。
# 源/目录/ICC 一律走参数注入，**本文件里没有任何本机绝对路径**。
#
# ⚠ 双宿主（PS 5.1 / PS 7）：禁 `? :` / `??` / `?.` / `&&` / `||`；用 `$(if (...) {...} else {...})`。
# ⚠ 代码串纯 ASCII：所有字符串字面量（Args 取值、打印消息）不得含中文；
#   中文只允许出现在注释里（注释会被 tokenizer 剥离，不占 CJK 门禁余量）。
# ⚠ 行尾自洽：全文件 LF。
#
# ── 源码核对结论（2026-09-19 逐条打开源码核对，非转述）─────────────────────────
#   · CLI 解析入口：`src/FfmpegGui/CliParser.cs:64`（Parse），显式 switch `:96-291`，
#     透传轴在 `ApplyExtraOption` `:535-663`。
#   · 四大色彩策略：`src/FfmpegGui/Models/ColorStrategy.cs:14-40`（Recommended/CarryIcc/BakeCicpOnly/Manual）。
#   · 后端枚举：`src/FfmpegGui/Services/EncoderDetectionService.cs:14-21`（Ffmpeg/Cjpegli/Cjxl/Jxr/Dng）。
#   · UI 控件：`src/FfmpegGui/MainWindow.xaml`（下拉项）+ `MainWindow.xaml.cs` 的 FillComboByLoc 填充表。
#   · 色彩空间词表：`src/FfmpegGui/Services/ColorSpaceRegistry.cs:50-141`（ByKey 的 12 个 Key）。
#   · 携带范围：`src/FfmpegGui/Services/ColorMapping/ColorIntent.cs:74-84`（CarryScope）。
#   · 色调映射算子：`src/FfmpegGui/Services/ColorMapping/ColorIntentFactory.cs:195-204`。
#
# ── 三处「与直觉不符」已按源码事实登记（详见文件末尾的 NOTES）────────────────────
#   ① 负数值必须写成 `--key=value` 内联形式（空格形式会被解析器吞掉）。
#   ② `--icc-mode` 的 4 个取值只映射出 3 种策略（None 与 BakeToStandard 同归 Recommended）。
#   ③ `--bit-depth` 没有 `auto` 取值（`int.Parse("auto")` 抛错后被静默吞掉）。

param(
    [switch]$SelfTest,
    [switch]$InjectEmptyAxis
)

# ══════════════════════════════════════════════════════════════════════════════
# 词表（结构自检的判据来源）
# ══════════════════════════════════════════════════════════════════════════════

$script:AxesGroups = @(
    'format', 'encoder', 'quality', 'color', 'metadata', 'geometry', 'anim', 'engine', 'runtime'
)

$script:AxesKinds = @('enum', 'bool', 'continuous', 'path')

# 「不能走默认 `CliKey Value` 拼法」的轴 -> 必须显式给出 token 覆盖的取值清单。
# 为什么需要这张表：`--strip-time` 这类是**无值 flag**，若按默认拼成 `--strip-time true`，
# 那个 `true` 会掉进 CliParser 的 default 分支被当成**位置参数**（=> 多出一个叫 true 的输入文件）。
# 自检 A8 会逐条核对本表已被 Get-AxisTokenOverrides 全覆盖 —— 将来新增 flag 型轴忘了登记，会当场转红。
$script:AxesOverrideRequired = @{
    'auto-threads'          = @('true', 'false')
    'dry-run'               = @('true', 'false')
    'preserve-structure'    = @('true', 'false')
    'strip-gps'             = @('true', 'false')
    'strip-time'            = @('true', 'false')
    'strip-camera'          = @('true', 'false')
    'strip-all-exif'        = @('true', 'false')
    'strip-xmp'             = @('true', 'false')
    'metadata-mode'         = @('PreserveAll', 'StripAll')
    'icc-file'              = @('none', '<icc>')
    'animation-loop'        = @('-1')
    'jpeg-gain-map-quality' = @('-1')
}

# 注入式负控的目标轴（--InjectEmptyAxis 用；挑一个取值数适中的色彩轴）
$script:AxesInjectTarget = 'color-tone-map'

# ══════════════════════════════════════════════════════════════════════════════
# 构造辅助
# ══════════════════════════════════════════════════════════════════════════════

# 造一个轴对象。字段固定 6 个（Name/Group/Values/CliKey/Source/Kind），不多不少。
# ⚠ `-Values @()` 必须能表达「空取值」这个非法态（负控要用）：空数组不是 $null，
#   所以这里显式判 $null 而不是用 `@($Values)` —— 后者会把 $null 变成「1 个 $null 元素」的数组，
#   于是「取值非空」这条断言永远为真，负控就成了哑炮。
function New-Axis {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Group,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Values,
        [Parameter(Mandatory = $true)][string]$CliKey,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Kind
    )
    $v = @()
    if ($null -ne $Values) { $v = @($Values) }
    return [pscustomobject]@{
        Name   = $Name
        Group  = $Group
        Values = $v
        CliKey = $CliKey
        Source = $Source
        Kind   = $Kind
    }
}

# 显式 token 覆盖表：`<轴名>|<取值>` -> 该取值对应的 CLI token 数组（可为空数组）。
# 未登记的键走默认拼法 `@($CliKey, $Value)`（即 `--key value` 形式）。
function Get-AxisTokenOverrides {
    param(
        [string]$IccFile = 'fixtures/p3.icc'
    )
    $m = @{}

    # ── 无值 flag 型（只有「给不给这个 flag」两态）──
    $m['auto-threads|true']        = @('--auto-threads')
    $m['auto-threads|false']       = @()
    $m['dry-run|true']             = @('--dry-run')
    $m['dry-run|false']            = @()
    $m['preserve-structure|true']  = @('--preserve-structure')
    $m['preserve-structure|false'] = @()
    $m['strip-gps|true']           = @('--strip-gps')
    $m['strip-gps|false']          = @('--no-strip-gps')
    $m['strip-time|true']          = @('--strip-time')
    $m['strip-time|false']         = @()
    $m['strip-camera|true']        = @('--strip-camera')
    $m['strip-camera|false']       = @()
    $m['strip-all-exif|true']      = @('--strip-all-exif')
    $m['strip-all-exif|false']     = @()
    $m['strip-xmp|true']           = @('--strip-xmp')
    $m['strip-xmp|false']          = @()

    # ── metadata-mode：CLI 上根本没有 `--metadata-mode`，只有两个互斥 flag ──
    #    ⚠ 副作用登记：`--preserve-metadata` 在 CliParser:211-220 里会**连带清空 5 个 strip 位**，
    #      所以它不是「只设 MetadataMode」的纯单变量开关（L2/L3 组合时要按交互项处理）。
    $m['metadata-mode|PreserveAll'] = @('--preserve-metadata')
    $m['metadata-mode|StripAll']    = @('--strip-metadata')

    # ── path 型：`none` = 显式不给该选项（默认基线）──
    $m['icc-file|none']  = @()
    $m['icc-file|<icc>'] = @('--icc-file', $IccFile)

    # ── 负数值：必须用 `--key=value` 内联形式 ──
    #    为什么：`--animation-loop -1` 的 `-1` 以 '-' 开头，CliParser:274 的
    #    `!args[i+1].StartsWith("-")` 判据不成立 => 不消费它，反而把本键置为 "true"
    #    => `int.Parse("true")` 抛 FormatException => 被 :659 的兜底 catch **静默吞掉**。
    #    即：空格形式下「用户设了 -1」与「用户什么都没设」在行为上无法区分。
    $m['animation-loop|-1']        = @('--animation-loop=-1')
    $m['jpeg-gain-map-quality|-1'] = @('--jpeg-gain-map-quality=-1')

    return $m
}

# 取某轴某取值对应的 CLI token 数组。
#
# ⚠⚠ PowerShell 数组语义陷阱（本文件实测踩过，两处必须成对，改任一处都会静默出错）：
#   · 函数内 `return ,$out`（一元逗号）让**空数组也作为一个对象**送进管道。这是必要的 ——
#     若写成 `return $out`，空数组会「展开成零个对象」，调用方拿到 $null。
#   · 但正因为它是**管道输出项**而非**表达式值**，调用方用 `@(...)` 包住会把那个空数组
#     当成 1 个元素收下（`@(<空数组>)` 走表达式是 0 个，走管道输出却是 1 个 —— 两者不等价）。
#     实测：`@(Get-AxisValueTokens ...)` 得 Count=1 且元素是空数组 => Args 里混进一个空 token。
#   · 故调用方一律用 `[string[]]$tokens = Get-AxisValueTokens ...` 接收（实测空/非空都对）。
function Get-AxisValueTokens {
    param(
        [Parameter(Mandatory = $true)]$Axis,
        [Parameter(Mandatory = $true)][string]$Value,
        [string]$IccFile = 'fixtures/p3.icc',
        $Overrides
    )
    if ($null -eq $Overrides) { $Overrides = Get-AxisTokenOverrides -IccFile $IccFile }
    $key = '{0}|{1}' -f $Axis.Name, $Value
    $out = @()
    if ($Overrides.ContainsKey($key)) {
        $out = @($Overrides[$key])
    } else {
        $out = @($Axis.CliKey, $Value)
    }
    return , $out
}

# ══════════════════════════════════════════════════════════════════════════════
# 1) 注册表
# ══════════════════════════════════════════════════════════════════════════════

function Get-AxisRegistry {
    [CmdletBinding()]
    param()

    # Source 字段格式 = <仓库相对路径>:<行号>，行号指向**该轴的 CLI 定义/映射处**。
    # 连续量（Kind=continuous）用「代表点数组」而非全枚举：取值域是实数区间，穷举无意义。
    return @(
        # ── format ──
        #  `jpegli` 是合法输入但会在 CliParser:150 被规范化成 `jpg`（编码器后端不是容器格式）。
        (New-Axis -Name 'format' -Group 'format' -CliKey '-f' -Source 'src/FfmpegGui/CliParser.cs:139' -Kind 'enum' -Values @('jpg', 'jpeg', 'png', 'webp', 'avif', 'jxl', 'jxr', 'tiff', 'bmp', 'gif', 'apng', 'dng', 'jpegli')),

        # ── encoder ──
        (New-Axis -Name 'encoder' -Group 'encoder' -CliKey '-e' -Source 'src/FfmpegGui/Services/EncoderDetectionService.cs:14' -Kind 'enum' -Values @('Ffmpeg', 'Cjpegli', 'Cjxl', 'Jxr', 'Dng')),

        # ── quality ──
        (New-Axis -Name 'quality' -Group 'quality' -CliKey '-q' -Source 'src/FfmpegGui/CliParser.cs:168' -Kind 'continuous' -Values @('1', '25', '50', '75', '92', '100')),
        #  UI 下拉另有 `auto` 项，但那是「不下发该选项」的意思；CLI 侧的取值域就是 4 个采样串。
        (New-Axis -Name 'chroma' -Group 'quality' -CliKey '--chroma' -Source 'src/FfmpegGui/CliParser.cs:542' -Kind 'enum' -Values @('auto', '4:4:4', '4:2:2', '4:2:0')),
        #  ⚠ 没有 `auto`：CliParser:543 用 int.Parse，`auto` 抛 FormatException 后被兜底 catch 静默吞掉。
        (New-Axis -Name 'bit-depth' -Group 'quality' -CliKey '--bit-depth' -Source 'src/FfmpegGui/CliParser.cs:543' -Kind 'enum' -Values @('8', '10', '12', '16')),
        (New-Axis -Name 'lossless' -Group 'quality' -CliKey '--lossless' -Source 'src/FfmpegGui/CliParser.cs:575' -Kind 'bool' -Values @('true', 'false')),

        # ── runtime ──
        (New-Axis -Name 'threads' -Group 'runtime' -CliKey '--threads' -Source 'src/FfmpegGui/CliParser.cs:574' -Kind 'continuous' -Values @('1', '2', '4', '8')),
        (New-Axis -Name 'jobs' -Group 'runtime' -CliKey '-j' -Source 'src/FfmpegGui/CliParser.cs:176' -Kind 'continuous' -Values @('1', '2', '4')),
        (New-Axis -Name 'auto-threads' -Group 'runtime' -CliKey '--auto-threads' -Source 'src/FfmpegGui/CliParser.cs:185' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'dry-run' -Group 'runtime' -CliKey '--dry-run' -Source 'src/FfmpegGui/CliParser.cs:253' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'log-level' -Group 'runtime' -CliKey '--log-level' -Source 'src/FfmpegGui/CliParser.cs:237' -Kind 'enum' -Values @('Error', 'Warn', 'Info', 'Debug', 'Trace')),
        (New-Axis -Name 'log-format' -Group 'runtime' -CliKey '--log-format' -Source 'src/FfmpegGui/CliParser.cs:244' -Kind 'enum' -Values @('Text', 'JsonLines')),
        (New-Axis -Name 'preserve-structure' -Group 'runtime' -CliKey '--preserve-structure' -Source 'src/FfmpegGui/CliParser.cs:134' -Kind 'bool' -Values @('true', 'false')),

        # ── color：策略 / 引擎 / 旧兼容 ──
        (New-Axis -Name 'color-strategy' -Group 'color' -CliKey '--color-strategy' -Source 'src/FfmpegGui/CliParser.cs:635' -Kind 'enum' -Values @('recommended', 'carry', 'cicp-only', 'manual')),
        (New-Axis -Name 'color-engine' -Group 'engine' -CliKey '--color-engine' -Source 'src/FfmpegGui/CliParser.cs:550' -Kind 'enum' -Values @('auto', 'engine', 'legacy')),
        #  ⚠ 旧兼容入口：4 个取值只映射出 3 种策略（None/BakeToStandard 都 -> Recommended），
        #     见 ColorStrategyMapper.FromIccMode（Models/ColorStrategy.cs:47-53）。
        (New-Axis -Name 'icc-mode' -Group 'color' -CliKey '--icc-mode' -Source 'src/FfmpegGui/CliParser.cs:631' -Kind 'enum' -Values @('None', 'CarryIcc', 'BakeToStandard', 'BakeOnly')),
        (New-Axis -Name 'icc-file' -Group 'color' -CliKey '--icc-file' -Source 'src/FfmpegGui/CliParser.cs:639' -Kind 'path' -Values @('none', '<icc>')),

        # ── color：策略细化开关 ──
        (New-Axis -Name 'color-carry-scope' -Group 'color' -CliKey '--color-carry-scope' -Source 'src/FfmpegGui/CliParser.cs:552' -Kind 'enum' -Values @('none', 'unnameable', 'all')),
        (New-Axis -Name 'color-allow-unlabeled' -Group 'color' -CliKey '--color-allow-unlabeled' -Source 'src/FfmpegGui/CliParser.cs:553' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'color-gamut-map' -Group 'color' -CliKey '--color-gamut-map' -Source 'src/FfmpegGui/CliParser.cs:554' -Kind 'enum' -Values @('off', 'on')),
        (New-Axis -Name 'color-tone-map' -Group 'color' -CliKey '--color-tone-map' -Source 'src/FfmpegGui/CliParser.cs:567' -Kind 'enum' -Values @('none', 'reinhard', 'hable', 'mobius', 'bt2446a')),
        #  旧 legacy 链的 tonemap 曲线（与 --color-tone-map 是两个不同入口）。
        #  ⚠ 引擎路径下非空即被拦（ColorEngineRouter.cs:335 UnconsumedSetting LegacyTonemapCurve）。
        (New-Axis -Name 'tonemap-curve' -Group 'color' -CliKey '--tonemap-curve' -Source 'src/FfmpegGui/CliParser.cs:570' -Kind 'enum' -Values @('hable', 'mobius', 'reinhard')),
        (New-Axis -Name 'color-709-curve' -Group 'color' -CliKey '--color-709-curve' -Source 'src/FfmpegGui/CliParser.cs:551' -Kind 'enum' -Values @('std', 'zimg')),

        # ── color：目标色域与精确三元组 ──
        #  ⚠ UI 只暴露 7 项（auto/sRGB/BT.709/Display P3/P3 PQ/BT.2020 PQ/BT.2020 HLG），
        #    但 ColorSpaceRegistry.Resolve 还认 P3 HLG / BT.2020 / DCI-P3 / Adobe RGB /
        #    ProPhoto RGB / ColorMatch RGB（超广归一分支）=> 全登记。
        (New-Axis -Name 'color-space' -Group 'color' -CliKey '--color-space' -Source 'src/FfmpegGui/CliParser.cs:544' -Kind 'enum' -Values @('auto', 'sRGB', 'BT.709', 'Display P3', 'P3 PQ', 'P3 HLG', 'BT.2020', 'BT.2020 PQ', 'BT.2020 HLG', 'DCI-P3', 'Adobe RGB', 'ProPhoto RGB', 'ColorMatch RGB')),
        #  下面三轴的取值 = MainWindow.xaml 下拉项（已实测「两个角色都可用」的那一版）：
        #  NormalizePrimariesToken / NormalizeTrcToken / NormalizeMatrixToken 另有别名，但别名归一到同一 ffmpeg 名。
        (New-Axis -Name 'color-primaries' -Group 'color' -CliKey '--color-primaries' -Source 'src/FfmpegGui/CliParser.cs:545' -Kind 'enum' -Values @('bt709', 'bt2020', 'smpte432')),
        (New-Axis -Name 'color-trc' -Group 'color' -CliKey '--color-trc' -Source 'src/FfmpegGui/CliParser.cs:546' -Kind 'enum' -Values @('iec61966-2-1', 'bt709', 'smpte2084', 'arib-std-b67', 'linear')),
        (New-Axis -Name 'color-matrix' -Group 'color' -CliKey '--color-matrix' -Source 'src/FfmpegGui/CliParser.cs:547' -Kind 'enum' -Values @('bt709', 'bt2020nc', 'bt470bg', 'smpte170m', 'gbr')),
        (New-Axis -Name 'color-range' -Group 'color' -CliKey '--color-range' -Source 'src/FfmpegGui/CliParser.cs:548' -Kind 'enum' -Values @('auto', 'tv', 'pc')),

        # ── color：亮度端点（nits，连续量取代表点）──
        (New-Axis -Name 'color-hdr-peak' -Group 'color' -CliKey '--color-hdr-peak' -Source 'src/FfmpegGui/CliParser.cs:571' -Kind 'continuous' -Values @('0', '100', '1000', '4000', '10000')),
        (New-Axis -Name 'color-sdr-white' -Group 'color' -CliKey '--color-sdr-white' -Source 'src/FfmpegGui/CliParser.cs:572' -Kind 'continuous' -Values @('100', '203', '300')),
        (New-Axis -Name 'color-sdr-peak' -Group 'color' -CliKey '--color-sdr-peak' -Source 'src/FfmpegGui/CliParser.cs:573' -Kind 'continuous' -Values @('100', '200')),

        # ── metadata：模式 + 5 个 strip 位 ──
        (New-Axis -Name 'metadata-mode' -Group 'metadata' -CliKey '--strip-metadata' -Source 'src/FfmpegGui/CliParser.cs:208' -Kind 'enum' -Values @('PreserveAll', 'StripAll')),
        (New-Axis -Name 'strip-gps' -Group 'metadata' -CliKey '--strip-gps' -Source 'src/FfmpegGui/CliParser.cs:190' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'strip-time' -Group 'metadata' -CliKey '--strip-time' -Source 'src/FfmpegGui/CliParser.cs:196' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'strip-camera' -Group 'metadata' -CliKey '--strip-camera' -Source 'src/FfmpegGui/CliParser.cs:199' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'strip-all-exif' -Group 'metadata' -CliKey '--strip-all-exif' -Source 'src/FfmpegGui/CliParser.cs:202' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'strip-xmp' -Group 'metadata' -CliKey '--strip-xmp' -Source 'src/FfmpegGui/CliParser.cs:205' -Kind 'bool' -Values @('true', 'false')),

        # ── geometry ──
        (New-Axis -Name 'max-dimension-enabled' -Group 'geometry' -CliKey '--max-dimension-enabled' -Source 'src/FfmpegGui/CliParser.cs:627' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'max-dimension' -Group 'geometry' -CliKey '--max-dimension' -Source 'src/FfmpegGui/CliParser.cs:628' -Kind 'continuous' -Values @('256', '1024', '1920', '3840', '7680')),

        # ── anim ──
        (New-Axis -Name 'animation-fps' -Group 'anim' -CliKey '--animation-fps' -Source 'src/FfmpegGui/CliParser.cs:623' -Kind 'continuous' -Values @('10', '15', '24', '30', '60')),
        #  -1 = 不循环（负数取值 -> 必须内联形式，见 Get-AxisTokenOverrides 的说明）
        (New-Axis -Name 'animation-loop' -Group 'anim' -CliKey '--animation-loop' -Source 'src/FfmpegGui/CliParser.cs:624' -Kind 'enum' -Values @('0', '-1', '1', '3')),
        (New-Axis -Name 'animation-scale-w' -Group 'anim' -CliKey '--animation-scale-w' -Source 'src/FfmpegGui/CliParser.cs:625' -Kind 'continuous' -Values @('0', '320', '1280')),
        (New-Axis -Name 'animation-duration' -Group 'anim' -CliKey '--animation-duration' -Source 'src/FfmpegGui/CliParser.cs:626' -Kind 'continuous' -Values @('0', '2.5', '10')),
        (New-Axis -Name 'gif-palette' -Group 'anim' -CliKey '--gif-palette' -Source 'src/FfmpegGui/CliParser.cs:629' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'gif-dither' -Group 'anim' -CliKey '--gif-dither' -Source 'src/FfmpegGui/CliParser.cs:630' -Kind 'bool' -Values @('true', 'false')),

        # ── GainMap（Ultra HDR JPEG）细分 ──
        (New-Axis -Name 'jpeg-gain-map' -Group 'color' -CliKey '--jpeg-gain-map' -Source 'src/FfmpegGui/CliParser.cs:596' -Kind 'bool' -Values @('true', 'false')),
        #  -1 = 跟随主图质量（负数取值 -> 内联形式）
        (New-Axis -Name 'jpeg-gain-map-quality' -Group 'color' -CliKey '--jpeg-gain-map-quality' -Source 'src/FfmpegGui/CliParser.cs:597' -Kind 'continuous' -Values @('-1', '50', '75', '100')),
        (New-Axis -Name 'jpeg-gain-map-target-nits' -Group 'color' -CliKey '--jpeg-gain-map-target-nits' -Source 'src/FfmpegGui/CliParser.cs:598' -Kind 'continuous' -Values @('0', '1000', '4000')),
        #  1/2/4/8/16：UI 有 5 档（含 1/16），RescaleGainMap 对 factor 无上界校验 => 16 可达。
        (New-Axis -Name 'jpeg-gain-map-downsample' -Group 'color' -CliKey '--jpeg-gain-map-downsample' -Source 'src/FfmpegGui/CliParser.cs:599' -Kind 'enum' -Values @('1', '2', '4', '8', '16')),
        (New-Axis -Name 'jpeg-gain-map-multi-channel' -Group 'color' -CliKey '--jpeg-gain-map-multi-channel' -Source 'src/FfmpegGui/CliParser.cs:600' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'jpeg-gain-map-base-gamut' -Group 'color' -CliKey '--jpeg-gain-map-base-gamut' -Source 'src/FfmpegGui/CliParser.cs:601' -Kind 'enum' -Values @('srgb', 'bt2020')),

        # ── DNG（dngtool 后端专属）──
        #  ⚠ 模型里 DNG 有 6 个字段，但 CLI 只透传 6 个中的 5 个之外还多 1 个：
        #    dng-compression / dng-jxl-quality / dng-jxl-effort / dng-linear /
        #    dng-bit-depth / dng-highlight-mode 共 6 条可达；
        #    **DngJxlDecodeSpeed 没有 CLI 入口**（ApplyExtraOption 里无 `dng-jxl-decode-speed`），
        #    只能由 UI/预设设置 => 不登记为轴，改在 NOTES 里点名。
        (New-Axis -Name 'dng-compression' -Group 'encoder' -CliKey '--dng-compression' -Source 'src/FfmpegGui/CliParser.cs:617' -Kind 'enum' -Values @('0', '1')),
        (New-Axis -Name 'dng-jxl-quality' -Group 'encoder' -CliKey '--dng-jxl-quality' -Source 'src/FfmpegGui/CliParser.cs:618' -Kind 'continuous' -Values @('0', '50', '100')),
        (New-Axis -Name 'dng-jxl-effort' -Group 'encoder' -CliKey '--dng-jxl-effort' -Source 'src/FfmpegGui/CliParser.cs:619' -Kind 'continuous' -Values @('1', '7', '9')),
        (New-Axis -Name 'dng-linear' -Group 'encoder' -CliKey '--dng-linear' -Source 'src/FfmpegGui/CliParser.cs:620' -Kind 'bool' -Values @('true', 'false')),
        (New-Axis -Name 'dng-bit-depth' -Group 'encoder' -CliKey '--dng-bit-depth' -Source 'src/FfmpegGui/CliParser.cs:621' -Kind 'enum' -Values @('8', '16')),
        (New-Axis -Name 'dng-highlight-mode' -Group 'encoder' -CliKey '--dng-highlight-mode' -Source 'src/FfmpegGui/CliParser.cs:622' -Kind 'enum' -Values @('0', '1', '2'))
    )
}

# ══════════════════════════════════════════════════════════════════════════════
# 2) L1 用例生成（单变量扫描）
# ══════════════════════════════════════════════════════════════════════════════

# 每个轴遍历其全部取值，其余轴保持默认（= 不出现）。
# 源/输出目录/ICC 全部可注入；默认值是**仓库相对路径**，不含任何本机绝对路径。
function Get-SingleAxisCases {
    [CmdletBinding()]
    param(
        [string]$Source = 'fixtures/in.png',
        [string]$OutDir = 'out',
        [string]$IccFile = 'fixtures/p3.icc',
        $Registry,
        $Overrides
    )
    if ($null -eq $Registry) { $Registry = @(Get-AxisRegistry) }
    if ($null -eq $Overrides) { $Overrides = Get-AxisTokenOverrides -IccFile $IccFile }

    $base = @('--headless', '-i', $Source, '-o', $OutDir)
    $cases = New-Object System.Collections.ArrayList

    foreach ($axis in @($Registry)) {
        $idx = 0
        foreach ($v in @($axis.Values)) {
            $idx++
            $value = [string]$v
            # ⚠ 必须用 [string[]] 接收（见 Get-AxisValueTokens 上方的数组语义说明）
            [string[]]$tokens = Get-AxisValueTokens -Axis $axis -Value $value -IccFile $IccFile -Overrides $Overrides
            $argList = @($base) + @($tokens)
            $case = [pscustomobject]@{
                Id    = ('L1-{0}-{1:d2}' -f $axis.Name, $idx)
                Axis  = $axis.Name
                Value = $value
                Args  = $argList
            }
            [void]$cases.Add($case)
        }
    }

    return $cases.ToArray()
}

# ══════════════════════════════════════════════════════════════════════════════
# 3) 用例 -> 命令行字符串（只用于打印/记账，不执行）
# ══════════════════════════════════════════════════════════════════════════════

# 含空格/全角等的 token 加双引号；其余原样。刻意做成保守规则，不试图完整复刻 cmd 转义。
function Format-AxesToken {
    param([string]$Token)
    if ($Token -match '^[A-Za-z0-9_\.:/=+\-]+$') { return $Token }
    return '"' + $Token + '"'
}

function ConvertTo-CliArgs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, ValueFromPipeline = $true)]$Case
    )
    process {
        $parts = @()
        foreach ($t in @($Case.Args)) { $parts += (Format-AxesToken -Token ([string]$t)) }
        return ($parts -join ' ')
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# 4) 记账
# ══════════════════════════════════════════════════════════════════════════════

function Get-CaseSummary {
    [CmdletBinding()]
    param(
        $Registry,
        $Cases
    )
    if ($null -eq $Registry) { $Registry = @(Get-AxisRegistry) }
    if ($null -eq $Cases) { $Cases = @(Get-SingleAxisCases -Registry $Registry) }

    $totalValues = 0
    foreach ($a in @($Registry)) { $totalValues += @($a.Values).Count }

    return [ordered]@{
        TotalAxes   = @($Registry).Count
        TotalValues = $totalValues
        TotalCases  = @($Cases).Count
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# 5) 结构校验器（自检与负控共用同一个校验器 —— 负控证明的就是「它有牙」）
# ══════════════════════════════════════════════════════════════════════════════

# 返回 @{ Pass; Fail; Lines }。Lines 是逐条断言的可读行。
function Test-AxesStructure {
    [CmdletBinding()]
    param(
        $Registry,
        $Cases,
        $Overrides,
        $OverrideRequired
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
    if ($null -eq $Overrides) { $Overrides = @{} }
    if ($null -eq $OverrideRequired) { $OverrideRequired = @{} }

    # A1 注册表非空
    & $check ($reg.Count -ge 1) 'A1' ('registry non-empty (axes={0})' -f $reg.Count)

    # A2 轴名唯一
    $names = @($reg | ForEach-Object { $_.Name })
    $dupNames = @()
    foreach ($n in @($names | Sort-Object -Unique)) {
        if (@($names | Where-Object { $_ -eq $n }).Count -gt 1) { $dupNames += $n }
    }
    & $check ($dupNames.Count -eq 0) 'A2' ('axis names unique (dups={0})' -f ($dupNames -join ','))

    # A3 必需字段非空
    $badFields = @()
    foreach ($a in $reg) {
        foreach ($f in @('Name', 'Group', 'CliKey', 'Source', 'Kind')) {
            if ([string]::IsNullOrWhiteSpace([string]$a.$f)) { $badFields += ('{0}.{1}' -f $a.Name, $f) }
        }
    }
    & $check ($badFields.Count -eq 0) 'A3' ('required fields non-empty (bad={0})' -f ($badFields -join ','))

    # A4 Group 在词表内
    $badGroups = @()
    foreach ($a in $reg) {
        if ($script:AxesGroups -notcontains [string]$a.Group) { $badGroups += ('{0}={1}' -f $a.Name, $a.Group) }
    }
    & $check ($badGroups.Count -eq 0) 'A4' ('Group known (bad={0})' -f ($badGroups -join ','))

    # A5 Kind 在词表内
    $badKinds = @()
    foreach ($a in $reg) {
        if ($script:AxesKinds -notcontains [string]$a.Kind) { $badKinds += ('{0}={1}' -f $a.Name, $a.Kind) }
    }
    & $check ($badKinds.Count -eq 0) 'A5' ('Kind known (bad={0})' -f ($badKinds -join ','))

    # A6 每轴取值非空（负控 B2 打的就是这条）
    $emptyVals = @()
    foreach ($a in $reg) {
        if (@($a.Values).Count -eq 0) { $emptyVals += $a.Name }
    }
    & $check ($emptyVals.Count -eq 0) 'A6' ('every axis has non-empty Values (empty={0})' -f ($emptyVals -join ','))

    # A7 每轴取值唯一
    $dupVals = @()
    foreach ($a in $reg) {
        $v = @($a.Values)
        if (@($v | Sort-Object -Unique).Count -ne $v.Count) { $dupVals += $a.Name }
    }
    & $check ($dupVals.Count -eq 0) 'A7' ('every axis has unique Values (dups={0})' -f ($dupVals -join ','))

    # A8 flag 型/特殊取值必须显式登记 token（负控 B4 打的就是这条）
    $missOv = @()
    foreach ($k in @($OverrideRequired.Keys)) {
        if ($names -notcontains [string]$k) { $missOv += ('no-such-axis:{0}' -f $k); continue }
        foreach ($v in @($OverrideRequired[$k])) {
            if (-not $Overrides.ContainsKey(('{0}|{1}' -f $k, $v))) { $missOv += ('{0}|{1}' -f $k, $v) }
        }
    }
    & $check ($missOv.Count -eq 0) 'A8' ('special-value token overrides registered (missing={0})' -f ($missOv -join ','))

    # A9 用例数 == 各轴取值数之和
    $sum = 0
    foreach ($a in $reg) { $sum += @($a.Values).Count }
    & $check ($cses.Count -eq $sum) 'A9' ('case count == sum of value counts (cases={0} sum={1})' -f $cses.Count, $sum)

    # A10 用例 Id 唯一
    $ids = @($cses | ForEach-Object { $_.Id })
    & $check (@($ids | Sort-Object -Unique).Count -eq $ids.Count) 'A10' ('case Ids unique (ids={0})' -f $ids.Count)

    # A11/A12 用例的轴与取值都能在注册表里对上
    $byName = @{}
    foreach ($a in $reg) { $byName[[string]$a.Name] = $a }
    $badAxis = @(); $badValue = @()
    foreach ($c in $cses) {
        if (-not $byName.ContainsKey([string]$c.Axis)) { $badAxis += $c.Id; continue }
        if (@($byName[[string]$c.Axis].Values) -notcontains [string]$c.Value) { $badValue += $c.Id }
    }
    & $check ($badAxis.Count -eq 0) 'A11' ('every case Axis exists in registry (bad={0})' -f ($badAxis -join ','))
    & $check ($badValue.Count -eq 0) 'A12' ('every case Value belongs to its axis (bad={0})' -f ($badValue -join ','))

    # A13 Args 非空（负控 B3 打的就是这条）
    $badArgs = @()
    foreach ($c in $cses) {
        if ($null -eq $c.Args) { $badArgs += $c.Id; continue }
        if (@($c.Args).Count -eq 0) { $badArgs += $c.Id }
    }
    & $check ($badArgs.Count -eq 0) 'A13' ('every case Args non-empty (bad={0})' -f ($badArgs -join ','))

    # A14 Args 首元素非空，且不含任何 null/空 token
    #     ⚠ 这条同时是「数组包装是否出错」的哨兵：Args 里混进空数组/null 元素时 [string] 转换得 ""。
    $badTok = @()
    foreach ($c in $cses) {
        $ti = -1
        foreach ($t in @($c.Args)) {
            $ti++
            if ($null -eq $t) { $badTok += ('{0}[{1}]=null' -f $c.Id, $ti); break }
            if ([string]::IsNullOrEmpty([string]$t)) { $badTok += ('{0}[{1}]=empty' -f $c.Id, $ti); break }
        }
    }
    & $check ($badTok.Count -eq 0) 'A14' ('every case Args[0] non-empty and no empty tokens (bad={0})' -f ($badTok -join ','))

    # A15 每个用例都带齐必需基座（--headless / -i / -o）
    $badBase = @()
    foreach ($c in $cses) {
        $t = @($c.Args)
        if (($t -notcontains '--headless') -or ($t -notcontains '-i') -or ($t -notcontains '-o')) { $badBase += $c.Id }
    }
    & $check ($badBase.Count -eq 0) 'A15' ('every case carries --headless -i -o (bad={0})' -f ($badBase -join ','))

    return @{ Pass = [int]$acc['Pass']; Fail = [int]$acc['Fail']; Lines = $acc['Lines'].ToArray() }
}

# ══════════════════════════════════════════════════════════════════════════════
# 6) 自检
# ══════════════════════════════════════════════════════════════════════════════

function Invoke-AxesSelfTest {
    [CmdletBinding()]
    param(
        [switch]$InjectEmptyAxis
    )

    $icc = 'fixtures/p3.icc'

    # ── 取注册表（可选：注入一个空取值轴，用于证明 A6 有牙）──
    $registry = @(Get-AxisRegistry)
    if ($InjectEmptyAxis) {
        $patched = @()
        foreach ($a in $registry) {
            if ($a.Name -eq $script:AxesInjectTarget) {
                # 直接造对象、Values 显式给空数组（不经 New-Axis，排除参数绑定把 @() 变成 $null 的可能）
                $patched += [pscustomobject]@{
                    Name   = $a.Name
                    Group  = $a.Group
                    Values = @()
                    CliKey = $a.CliKey
                    Source = $a.Source
                    Kind   = $a.Kind
                }
            } else {
                $patched += $a
            }
        }
        $registry = $patched
    }

    $overrides = Get-AxisTokenOverrides -IccFile $icc
    $cases = @(Get-SingleAxisCases -Registry $registry -IccFile $icc -Overrides $overrides)
    $summary = Get-CaseSummary -Registry $registry -Cases $cases

    Write-Output ('_lib-axes self-test (axes={0} values={1} cases={2})' -f $summary.TotalAxes, $summary.TotalValues, $summary.TotalCases)

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

    # ── A 段：真实注册表 ──
    $res = Test-AxesStructure -Registry $registry -Cases $cases -Overrides $overrides -OverrideRequired $script:AxesOverrideRequired
    foreach ($l in @($res.Lines)) { Write-Output $l }
    $tally['Pass'] = [int]$tally['Pass'] + [int]$res.Pass
    $tally['Fail'] = [int]$tally['Fail'] + [int]$res.Fail

    # ── B 段：负控 / 正控（合成数据，绝不改动真实注册表）──
    # 合成一套最小注册表：1 个 enum 轴 + 1 个 bool 轴（bool 轴要求显式 token 覆盖）。
    $synReg = @(
        (New-Axis -Name 'syn-enum' -Group 'color' -CliKey '--syn-enum' -Source 'syn:1' -Kind 'enum' -Values @('a', 'b')),
        (New-Axis -Name 'syn-bool' -Group 'quality' -CliKey '--syn-bool' -Source 'syn:2' -Kind 'bool' -Values @('true', 'false'))
    )
    $synReq = @{ 'syn-bool' = @('true', 'false') }
    $synOv = @{
        'syn-bool|true'  = @('--syn-bool', 'true')
        'syn-bool|false' = @('--syn-bool', 'false')
    }
    $synCases = @(Get-SingleAxisCases -Registry $synReg -IccFile $icc -Overrides $synOv)

    # B1 正控：干净合成数据 => 校验器必须 0 fail（证明它不是「恒红」）
    $b1 = Test-AxesStructure -Registry $synReg -Cases $synCases -Overrides $synOv -OverrideRequired $synReq
    & $ck ($b1.Fail -eq 0) 'B1' ('positive control: clean synthetic registry passes (fail={0})' -f $b1.Fail)

    # B2 负控：空取值轴 => 必须转红（这是 -InjectEmptyAxis 与「临时改数据」两种变异共用的判据）
    $emptyReg = @(
        [pscustomobject]@{ Name = 'syn-enum'; Group = 'color'; Values = @(); CliKey = '--syn-enum'; Source = 'syn:1'; Kind = 'enum' },
        $synReg[1]
    )
    $emptyCases = @(Get-SingleAxisCases -Registry $emptyReg -IccFile $icc -Overrides $synOv)
    $b2 = Test-AxesStructure -Registry $emptyReg -Cases $emptyCases -Overrides $synOv -OverrideRequired $synReq
    & $ck ($b2.Fail -ge 1) 'B2' ('negative control: empty-Values axis turns validator red (fail={0})' -f $b2.Fail)

    # B3 负控：Args 空数组 => 必须转红
    $badCases = @([pscustomobject]@{ Id = 'L1-syn-enum-01'; Axis = 'syn-enum'; Value = 'a'; Args = @() }) + $synCases
    $b3 = Test-AxesStructure -Registry $synReg -Cases $badCases -Overrides $synOv -OverrideRequired $synReq
    & $ck ($b3.Fail -ge 1) 'B3' ('negative control: empty-Args case turns validator red (fail={0})' -f $b3.Fail)

    # B4 负控：bool 轴缺显式 token 覆盖 => 必须转红
    $b4 = Test-AxesStructure -Registry $synReg -Cases $synCases -Overrides @{} -OverrideRequired $synReq
    & $ck ($b4.Fail -ge 1) 'B4' ('negative control: missing token override turns validator red (fail={0})' -f $b4.Fail)

    Write-Output ('AXES RESULT: pass={0} fail={1}' -f $tally['Pass'], $tally['Fail'])

    # ⚠ 本函数**只打印、不返回对象**（刻意）：若它 `return` 一个 hashtable，顶层就只能写
    #   `$null = Invoke-AxesSelfTest ...` 来吞掉返回值 —— 而 `$null = f` 会把 f 写进
    #   成功流的**全部**内容（含每一行 Write-Output）一起吞掉，自检就变成「静默零输出 + 退出码 0」
    #   （本文件初版实测踩到：0 行输出、EXIT=0）。计数改由脚本作用域变量带出。
    $script:AxesSelfTestResult = [ordered]@{ Pass = [int]$tally['Pass']; Fail = [int]$tally['Fail'] }
}

# ══════════════════════════════════════════════════════════════════════════════
# 顶层：只在显式 -SelfTest 时跑自检（dot-source 时**零副作用**，与 _lib-color-assets.ps1 同规）
# ══════════════════════════════════════════════════════════════════════════════

if ($SelfTest) {
    Invoke-AxesSelfTest -InjectEmptyAxis:$InjectEmptyAxis
}

# ══════════════════════════════════════════════════════════════════════════════
# NOTES —— 核对源码时发现的「与预期不符之处」（供上层与评审对账）
# ══════════════════════════════════════════════════════════════════════════════
# N1. 负数值的空格形式会被静默吞掉（真缺陷，影响可观测性）
#     CliParser.cs:274 判据 `!args[i + 1].StartsWith("-")` 决定是否消费下一个参数。
#     于是 `--animation-loop -1` / `--jpeg-gain-map-quality -1` 的 `-1` **不被消费**，
#     该键反而被置为字面量 "true"（:280），接着 int.Parse("true") 抛 FormatException，
#     被 :659 的兜底 `catch {}` 静默忽略 => 「设了 -1」与「什么都没设」在外部不可区分。
#     本库的规避：负值一律走 `--key=value` 内联形式（CliParser.cs:78-86 的 inlineValue 分支）。
#
# N2. `--icc-mode` 4 个取值只产出 3 种策略
#     ColorStrategyMapper.FromIccMode（Models/ColorStrategy.cs:47-53）：
#     None -> Recommended、BakeToStandard -> Recommended、CarryIcc -> CarryIcc、BakeOnly -> BakeCicpOnly。
#     => `--icc-mode None` 与 `--icc-mode BakeToStandard` 行为等价，做组合矩阵时应折叠成一个等价类。
#     另注：CliParser.cs:632 用 `Enum.TryParse` 且**无 else**，非法取值被静默忽略（不报错、不改退出码）。
#
# N3. `--bit-depth` 没有 `auto`
#     UI 下拉有 auto（MainWindow.xaml:363），但那是「不下发选项」；CLI 侧 :543 是 int.Parse，
#     `--bit-depth auto` 抛 FormatException 后被吞 => 用户以为设了 auto，其实等于没设。
#     同类静默忽略还见于 --color-strategy（:636 TryParse 无 else）与 --icc-mode。
#     对照：--color-gamut-map（:559-565）与 --jpeg-gain-map-base-gamut（:603-609）非法取值**会显式报错** ——
#     即同一张表里存在两套口径（有的喊、有的吞），组合测试要按轴分别对待。
#
# N4. DNG 是 6 条 CLI 可达轴，不是 5 条
#     ApplyExtraOption 里 DNG 相关共 6 个键（:617-622）。模型另有 DngJxlDecodeSpeed（FfmpegOptions.cs:240，
#     默认 1，UI 有 NumericUpDown MainWindow.xaml:303），但 **ApplyExtraOption 里没有 `dng-jxl-decode-speed`**
#     => 该字段 CLI 完全不可达（只能靠 GUI/预设），故不登记为轴。
#
# N5. `--color-space` 的 CLI 取值域比 UI 宽
#     UI（MainWindow.xaml:375-383）只给 7 项，但 ColorSpaceRegistry.Resolve 还认
#     P3 HLG / BT.2020 / DCI-P3 / Adobe RGB / ProPhoto RGB / ColorMatch RGB（:101-139 的 Key）。
#     => 组合矩阵若以 UI 为准会漏掉 6 个真实可达分支（超广色域归一/保真携带路径全在这些值上）。
#
# N6. `--metadata-mode` 这个 CLI 参数**不存在**
#     模型有 MetadataMode 枚举（PreserveAll/StripAll，FfmpegOptions.cs:9-13），但 CLI 只有两个互斥 flag：
#     `--strip-metadata`(:208) 与 `--preserve-metadata`(:211)。本库把该轴 CliKey 记为 `--strip-metadata`
#     （主作用面），实际 token 由覆盖表给出。
#     ⚠ 交互项：`--preserve-metadata`(:215-219) 会**连带清空 5 个 strip 位**，它不是纯单变量开关。
#
# N7. `--tonemap-curve` 是「引擎路径下必被拦」的遗留轴
#     ColorEngineRouter.cs:335-342 只要该值非空就记 UnconsumedSetting(LegacyTonemapCurve)。
#     而 FfmpegCommandBuilder.Decision.cs:259-260 又会把它原样拼进 ffmpeg 的 `tonemap=<v>` 滤镜
#     => 取值域实际由 **ffmpeg** 决定（不止 hable/mobius/reinhard）。本库只登记文档化的 3 个。
#
# N8. GainMap 下采样 1/16 在 UI 可达、且底层不校验上界
#     MainWindow.xaml.cs:919-921 给 5 档（含 sixteenth -> 16），ParseGainMapDownsample(:5558-5565) 映射到 16；
#     GainMapEncoder.RescaleGainMap(:303-308) 对 factor 只做 ceiling 除法、**没有上界校验**。
#     另：EncodeAsync 的形参默认是 4（:75），而调用方一律传 Math.Max(options.JpegGainMapDownsample, 1)，
#     模型默认 2 => 「形参默认值」在产品路径上从不生效（易误读，故记于此）。
#
# N9. `format` 的 `jpegli` 会被规范化
#     CliParser.cs:150 把 `jpegli` 改写成 `jpg`（避免产出 `.jpegli` 非法扩展名）。
#     => `-f jpegli` 与 `-f jpg` 等价，组合矩阵里属同一等价类。
#
# N10. 未被登记为轴的 CLI 能力（有意排除，附理由）
#     · `--preset <name>` / `--settings <file>` / `--log-file <file>`：取值域来自运行时的预设库/文件系统，
#       无法在注册表里静态枚举（要枚举得先落一组 fixture 预设，属上层的事）。
#     · `AppendPngExtension`（FfmpegOptions.cs:392，UI 有复选框 MainWindow.xaml:760）：**ApplyExtraOption 里无对应键**
#       => CLI 不可达，同 N4。
#     · `--gui` / `--help` / `--version`：不是「输入轴」，是运行模式切换，不参与组合。
