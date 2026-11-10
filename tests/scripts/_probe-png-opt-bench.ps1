# ============================================================================
# _probe-png-opt-bench.ps1 — PNG 极限压缩重优化器 Benchmark 执行器（T4）
# ============================================================================
# 契约互指（plan.md §2.2，计划修订 P1）：
#   C-BENCH@1    执行协议（调度/超时/隔离/续跑/布局）——本脚本是该契约的定义与实现
#   C-PIXEL@1    像素逐位等价判据——ffprobe pix_fmt + IHDR 不变检查 + `ffmpeg -f md5 -` 对撞
#   C-CHUNK@1    chunk 保真判据——pngcicp info 解析 + 关键集 {iCCP,cICP,eXIf} 计数对撞（大小写敏感）
#   C-REPORT@1   读数报告 schema——bench.json / summary.tsv / summary.md 三件套
#   C-TOOLLIST@1 tools.json 台账（T1 提供，本脚本只读消费）
#   C-PMATRIX@1  manifest.json 素材矩阵（T2 提供，本脚本只读消费）
#   C-REFILTER@1 对照臂 harness CLI（T3 提供，本脚本强制接线三臂 libdeflate-12/zlib9/zlibng）
#
# 与 T1 冒烟探针（_probe-png-opt-smoke.ps1）共享 C-PIXEL@1 的 md5 口径：两脚本头注释互指
# 本契约，不得漂移（漂移表现为冒烟说等价、全量说违例，T6 抽检时暴露）。
#
# status 语义映射（C-REPORT@1 枚举 {ok, skip, timeout, fail, setup_fail}）：
#   ok         — 格执行完成且产物可解析（ffmpeg 解码成功即"可解析"）。像素违例
#                （pixel_equal=false）不改变 status，由 pixel_equal 字段独立表达并按
#                R3.2 计 lossless 违例；exit_code≠0 但产物有效时 notes 注 nonzero-exit。
#   skip       — 臂级不可跑：tools.json 缺条目 / skip=true / 冒烟未过（R1.4，仅全量模式）/
#                对照臂 harness 缺失 / exe 缺失。skip 行测量字段全 null（契约字面）。
#                ⚠ 两类 skip 的字段口径区分（T4 侧实现注记）：
#                  ① 臂级 skip（未执行）：22 字段中仅 case_id/arm_id/skip/status/timeout(false)/notes
#                    有值，其余全 null——「没跑的格不该有测量读数」防伪语义；
#                  ② 执行后 skip（C-REFILTER@1 harness 退出码 3 = dll 缺失）：exit_code/time_ms/
#                    stderr_digest/input_bytes/baseline_* 保留真实执行取证（skip_reason 与 notes
#                    均引用 exit 3，R4.3 可追溯），产物测量（output/ratio/pixel/chunk）null——
#                    置 null 会丢失「为什么 skip」的 stderr 证据，与 R4.3 取证语义冲突。
#   timeout    — 单格超 -TimeoutSec（默认 600，R3.4）：不 SKIP 不 FAIL，耗时维度的诚实
#                读数。产物已落盘且可解析 → 测量照常，notes 注 timeout-after-write；
#                不可解析 → 测量置空，产物文件删除（C-BENCH 布局约定 timeout 臂不落产物）。
#   fail       — 工具硬失败：exit≠0 且无有效产物（原位臂以"工作副本 sha 是否被改写"
#                判定工具是否实际产出）。测量字段中 input/baseline 保留，产物测量置空。
#   setup_fail — 环境级失败：corpus/基线文件缺失、manifest sha256 对撞不符（T2 交付质量
#                门，C-PMATRIX 失败语义）、chunk 子样本缺失。-Resume 时此状态行会被重跑。
#
# [arm] 日志行口径（C-BENCH@1）：[arm] exit=N pass=N fail=N skip=N timeout=N <耗时>
#   pass    = status=ok 且 pixel_equal=true（有效压缩格，R3.2 口径）
#   fail    = status∈{fail,setup_fail} 的格数 + 违例格数（status=ok 且 pixel_equal=false）
#   skip    = status=skip 的格数
#   timeout = timeout=true 的格数（无论产物是否可解析）
#   exit    = 臂级流程退出码：任一 fail/timeout 维度出现 → 1，否则 0
#   守恒式  = pass+fail+skip+timeout == 本臂格数（脚本末尾 fail-fast 断言）
#
# 用法：
#   pwsh -NoProfile -File tests/scripts/_probe-png-opt-bench.ps1 `
#        -RunRoot tests/output/png-opt-bench/<guid> [-Smoke] [-Resume] [-TimeoutSec 600] `
#        [-ToolsRoot tests/output/_tmp/pngopt-b1] [-CorpusRoot <dir>]
#   -RunRoot     轮次目录（T2 已建 corpus/ + baselines/；报告三件套与 outputs/ logs/ 落这里）
#   -CorpusRoot  素材来源根（含 corpus/manifest.json），默认 = RunRoot（同树布局，C-BENCH@1）
#   -ToolsRoot   tools.json 所在根（T1 产物区，只读）。查找顺序：$ToolsRoot/tools.json；
#                若 $ToolsRoot 以 \tools 结尾且其下无 tools.json，回退上级目录查找
#   -Smoke       只跑 manifest 中 smoke=true 的素材（无任何条目带 smoke 字段时回退 B01，
#                C-BENCH@1 契约默认）；报告文件加 smoke- 前缀；不启用 R1.4 冒烟门禁
#                （-Smoke 本身就是全量前的端到端验证面）
#   -Resume      bench.json（或 smoke-bench.json）已存在时跳过其中 status≠setup_fail 的
#                (case_id, arm_id) 行，只补缺行
#   -TimeoutSec  单格执行上限（默认 600，R3.4；自测时可调小验证 kill 路径）
#
# ----------------------------------------------------------------------------
# 修订注记（rev2, 2026-11-09, FullBench-T5d；授权链 = team-leader 对 T5d 任务书第 2 条，明确批准）
#   改动 = 主循环每臂完成处新增 bench.json 增量落盘（仅持久化时机，一处插入）：
#     - 序列化口径与下方最终落盘逐字同款（紧凑 JSON / -Depth 4 / UTF8 无 BOM / 顶层 JSON 数组格式不变）；
#     - tmp + Move-Item -Force 原子替换：进程被杀瞬间 bench.json 要么旧完整版要么新完整版，不会截断；
#     - flush 失败仅打 [flush-warn] 不中断本轮（最终落盘段仍保持原 fail-fast 语义）。
#   动机 = 全量 bench 五次半程死亡读数全丢（事故链五段：T2 覆写→T1 模板缺陷→外部终止悬案→
#     原任 T5 rm/双跑→v2 双实例互踩 12:01:08）；增量落盘后任何中断 bench.json 保留已完成臂的行，
#     -Resume 按 (case_id, arm_id) 自动补缺臂——死一次只丢当前臂。
#   判据字段/决策树/schema/调度逻辑一字未动（改动 = ①本注记 L61-70 ②主循环内 flush 块
#     L788-807，插入于 $armSummaries += 块 L783-786 之后；文件 926 行 → 959 行，其余逐字未动；
#     stdout 标识行 = "[flush] rows=N arm=<arm_id>"）。
# ============================================================================

[CmdletBinding()]
param(
    [string]$RunRoot = '',
    [string]$CorpusRoot = '',
    [string]$ToolsRoot = 'tests/output/_tmp/pngopt-b1',
    [switch]$Smoke,
    [switch]$Resume,
    [int]$TimeoutSec = 600
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 常量与路径解析
# ---------------------------------------------------------------------------
$script:RepoRoot    = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))  # tests/scripts → 仓库根
$script:FfmpegExe   = Join-Path $script:RepoRoot 'publish/PLAN/ffmpeg-full/ffmpeg.exe'
$script:FfprobeExe  = Join-Path $script:RepoRoot 'publish/PLAN/ffmpeg-full/ffprobe.exe'
$script:CicpExe     = Join-Path $script:RepoRoot 'tools/src/pngcicp/bin/Release/net11.0/pngcicp.exe'
$script:RefilterPy  = Join-Path $script:RepoRoot 'tests/scripts/_make-png-refilter.py'   # C-REFILTER@1 harness（T3 交付物）
$script:ChunkKeySet = @('iCCP', 'cICP', 'eXIf')   # C-CHUNK@1 关键集（大小写敏感，byte 级字面）

$script:ScriptStart = Get-Date
$script:BudgetHours = 6.0   # C-BENCH@1：累计超 6 小时打印提示但不自动终止

# C-TOOLLIST@1 的 14 臂表（判据本体，内嵌于此——T4 不内嵌工具版本/sha256，那些随获取批次漂移）
# Refilter 非 null ⇒ 对照臂，T4 强制走 py _make-png-refilter.py --impl <Refilter>（C-BENCH@1 集成约定）
$script:ArmTable = @(
    [pscustomobject]@{ Ord = 1;  Id = 'oxipng-default';  Spec = '工具默认档：零优化 flag';              Refilter = $null }
    [pscustomobject]@{ Ord = 2;  Id = 'oxipng-max';      Spec = '官方最高无损档（禁止 strip）';          Refilter = $null }
    [pscustomobject]@{ Ord = 3;  Id = 'oxipng-zopfli15'; Spec = 'max 档 + zopfli 迭代 15';               Refilter = $null }
    [pscustomobject]@{ Ord = 4;  Id = 'oxipng-zopfli30'; Spec = 'max 档 + zopfli 迭代 30';               Refilter = $null }
    [pscustomobject]@{ Ord = 5;  Id = 'zopfli-png';      Spec = '默认档（15 iterations），写新文件';     Refilter = $null }
    [pscustomobject]@{ Ord = 6;  Id = 'ect-9';           Spec = '最高档 -9（禁止 strip）';               Refilter = $null }
    [pscustomobject]@{ Ord = 7;  Id = 'pngout';          Spec = '默认档 in.png out.png';                 Refilter = $null }
    [pscustomobject]@{ Ord = 8;  Id = 'pingo';           Spec = '无损档（默认档有损属已知坑，硬判据兜底 R3.2）'; Refilter = $null }
    [pscustomobject]@{ Ord = 9;  Id = 'optipng-o7';      Spec = '-o7';                                   Refilter = $null }
    [pscustomobject]@{ Ord = 10; Id = 'pngcrush-brute';  Spec = '-brute，不带 -rem alla/allb（保 chunk）'; Refilter = $null }
    [pscustomobject]@{ Ord = 11; Id = 'advcomp-4';       Spec = '-4 -z（原位改写 → 隔离副本协议 R3.6）'; Refilter = $null }
    [pscustomobject]@{ Ord = 12; Id = 'libdeflate-12';   Spec = 'level 12，统一 paeth filter';           Refilter = 'libdeflate12' }
    [pscustomobject]@{ Ord = 13; Id = 'zlib9';           Spec = 'level 9，统一 paeth filter（对照臂下限锚）'; Refilter = 'zlib9' }
    [pscustomobject]@{ Ord = 14; Id = 'zlibng';          Spec = '默认策略，统一 paeth filter';           Refilter = 'zlibng' }
)

# manifest.pix_fmt → Pillow 期望 mode 接受集（R3.5：只做结构检查宽/高/模式，不做像素对撞——
# PIL 对 rgb48 读数有已知偏差，AGENTS §7 WebP 段先例；rgb48be 经 PIL 读出 mode=RGB 值已截断 8bit）
$script:PilModeMap = @{
    'rgb24'    = @('RGB')
    'rgba'     = @('RGBA')
    'rgb48be'  = @('RGB')
    'gray'     = @('L')
    'gray16le' = @('I', 'I;16')
    'gray16be' = @('I', 'I;16')
}

function Fail-AndExit([string]$Msg) {
    Write-Host "[bench-error] $Msg" -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------------------
# 通用 helpers
# ---------------------------------------------------------------------------
function Get-JProp($Obj, [string]$Name, $Default) {
    # JSON 属性安全读取（tools.json/manifest 由 T1/T2 手写产物，字段可能缺失）
    if ($null -eq $Obj) { return $Default }
    $p = $Obj.PSObject.Properties[$Name]
    if ($null -eq $p) { return $Default }
    if ($null -eq $p.Value) { return $Default }
    return $p.Value
}

function Get-Sha16([string]$Path) {
    # sha256 前 16 位大写（与 C-TOOLLIST/C-PMATRIX 的 sha256_16 同口径）
    $h = [System.Security.Cryptography.SHA256]::Create()
    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $b = $h.ComputeHash($fs)
        return ([BitConverter]::ToString($b) -replace '-', '').Substring(0, 16)
    } finally { $fs.Dispose(); $h.Dispose() }
}

function Read-TextShared([string]$Path) {
    # AGENTS 教训：Start-Process 的 redirect 句柄可能被遗留孙进程继承，宿主 exit 后 ~1s 才释放，
    # ReadAllText 会抛 IOException → FileShare.ReadWrite 容错读 + 重试（不删子进程）。
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $fs = [System.IO.File]::Open($Path, 'Open', 'Read',
                [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
            try {
                $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
                return $sr.ReadToEnd()
            } finally { $fs.Dispose() }
        } catch [System.IO.IOException] {
            Start-Sleep -Milliseconds 300
        }
    }
    return ''
}

function Flatten-LogText([string]$Text, [int]$MaxChars = 256) {
    # stderr_digest 口径：压平换行/制表（TSV 安全），截断保摘要
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    $t = ($Text -replace "`r", ' ' -replace "`n", ' | ' -replace "`t", ' ').Trim()
    if ($t.Length -gt $MaxChars) { $t = $t.Substring(0, $MaxChars) + '...(truncated)' }
    return $t
}

function Quote-Arg([string]$Arg) {
    # Start-Process -ArgumentList 数组对含空格元素不自动加引号（2026-11-09 实测：py -c 代码
    # 被拆词 → SyntaxError）——统一手工引号包裹并以单字符串传命令行（完全可控）。
    # 双引号内部转义为 \"；空串写 ""（占位参数保护）。
    if ([string]::IsNullOrEmpty($Arg)) { return '""' }
    if ($Arg -match '\s') { return '"' + ($Arg -replace '"', '\"') + '"' }
    return $Arg
}

# ---------------------------------------------------------------------------
# 判据原语（C-PIXEL@1 / C-CHUNK@1）
# ---------------------------------------------------------------------------
function Invoke-Native {
    # 判据子进程统一入口：重定向捕获 stdout/stderr，返回 (exit, stdout, stderr)。
    # 判据子进程（ffmpeg/ffprobe/pngcicp/py）无孙进程问题，但读取仍走 Read-TextShared 容错。
    param([string]$Exe, [string[]]$ArgList)
    $of = $script:ProbeOf; $ef = $script:ProbeEf
    Remove-Item $of, $ef -ErrorAction SilentlyContinue
    $exit = $null; $out = ''; $err = ''
    try {
        & $Exe @ArgList 1>$of 2>$ef
        $exit = $LASTEXITCODE
    } catch {
        return [pscustomobject]@{ Exit = -999; Stdout = ''; Stderr = ("launcher-fail: " + $_.Exception.Message) }
    }
    $out = Read-TextShared $of
    $err = Read-TextShared $ef
    return [pscustomobject]@{ Exit = $exit; Stdout = $out; Stderr = $err }
}

function Get-PngDecodedMd5([string]$Path) {
    # C-PIXEL@1：`ffmpeg -v error -i <png> -f md5 -`（解码帧 md5，覆盖解码后全部字节含位深）
    # exit≠0 或 stderr 非空错误 ⇒ $null（decode_ffmpeg=false 语义）
    if (-not (Test-Path $Path)) { return $null }
    $r = Invoke-Native -Exe $script:FfmpegExe -ArgList @('-v', 'error', '-i', $Path, '-f', 'md5', '-')
    if ($r.Exit -ne 0) { return $null }
    if (-not [string]::IsNullOrWhiteSpace($r.Stderr)) { return $null }
    if ($r.Stdout -match 'MD5=([0-9a-fA-F]{32})') { return $Matches[1].ToLowerInvariant() }
    return $null
}

function Get-PngProbe([string]$Path) {
    # C-PIXEL@1：ffprobe 取输出侧 pix_fmt/width/height（IHDR 语义形态）
    if (-not (Test-Path $Path)) { return $null }
    $r = Invoke-Native -Exe $script:FfprobeExe -ArgList @('-v', 'error', '-show_entries', 'stream=pix_fmt,width,height', '-of', 'json', $Path)
    if ($r.Exit -ne 0) { return $null }
    try {
        $j = ConvertFrom-Json $r.Stdout
        $s = $j.streams[0]
        if ($null -eq $s) { return $null }
        return [pscustomobject]@{ PixFmt = $s.pix_fmt; W = [int]$s.width; H = [int]$s.height }
    } catch { return $null }
}

function Get-PngChunks([string]$Path) {
    # C-CHUNK@1：pngcicp info 解析为有序 chunk 名清单。
    # 实测输出形态：`  IHDR: 13 bytes` / `  cICP: primaries=1 ...`（cICP 行为语义展开无 len）/
    # 首行 `File: <path> (...)`。正则抓 4 字母类型名 + PNG chunk 命名规则过滤伪匹配：
    # 规范要求 byte3 必须 uppercase（reserved bit）——"File:" 的 'l' 为小写，被此规则排除。
    # 返回 $null = 解析失败或缺 IEND（corrupt=true 语义）；否则返回有序名数组。
    if (-not (Test-Path $Path)) { return $null }
    $r = Invoke-Native -Exe $script:CicpExe -ArgList @('info', $Path)
    if ($r.Exit -ne 0) { return $null }
    $names = @()
    foreach ($line in ($r.Stdout -split "`n")) {
        if ($line -match '^\s*([A-Za-z]{4})\s*:') {
            $t = $Matches[1]
            if ($t[2] -cmatch '[A-Z]') { $names += $t }
        }
    }
    if ($names -notcontains 'IEND') { return $null }
    return $names
}

function Compare-ChunkLists([string[]]$InList, [string[]]$OutList) {
    # C-CHUNK@1 对撞：关键集按成员计数对撞（大小写敏感；多实例同类型按计数差判增删）。
    # 「按出现序比较」的实现口径：解析保序；R3.3 违例定义 = 丢失或大小写改变，
    # 序变化本身不在违例定义内，序信息保留在解析清单中供 T6 抽检复核。
    # 新增 chunk（gain）对全部类型计数（工具加 tEXt 合法，只记录不判违例）。
    $loss = @(); $gain = @()
    foreach ($k in $script:ChunkKeySet) {
        $nIn = @($InList  | Where-Object { $_ -ceq $k }).Count
        $nOut = @($OutList | Where-Object { $_ -ceq $k }).Count
        if ($nIn -gt 0 -and $nOut -lt $nIn) { $loss += $k }
    }
    $allTypes = @($InList) + @($OutList) | Select-Object -Unique
    foreach ($t in $allTypes) {
        $nIn = @($InList  | Where-Object { $_ -ceq $t }).Count
        $nOut = @($OutList | Where-Object { $_ -ceq $t }).Count
        if ($nOut -gt $nIn) { $gain += $t }
    }
    return [pscustomobject]@{ Loss = $loss; Gain = $gain }
}

function Invoke-PillowCheck([string]$Path, [int]$W, [int]$H, [string[]]$Modes) {
    # R3.5 第二解码器（Pillow 12.3.0）：只做结构检查（宽/高/通道模式），不做像素对撞。
    # 返回：$true 通过 / $false 结构不符 / $null 环境不可用（不计入解码失败数）
    if (-not $script:PyCmd) { return $null }
    if ($null -eq $Modes) { $modeArg = '*' } else { $modeArg = ($Modes -join ',') }
    $code = "import sys;from PIL import Image;p=sys.argv[1];w=int(sys.argv[2]);h=int(sys.argv[3]);m=sys.argv[4].split(',');im=Image.open(p);im.load();ok=(im.size==(w,h)) and ('*' in m or im.mode in m);print('pillow: mode='+im.mode+' size='+str(im.size));sys.exit(0 if ok else 3)"
    $r = Invoke-Native -Exe $script:PyCmd -ArgList @('-c', $code, $Path, "$W", "$H", $modeArg)
    if ($r.Exit -eq 0) { return $true }
    if ($r.Exit -eq 1) { return $null }   # import 失败 = PIL 不可用（环境级）
    return $false
}

# ---------------------------------------------------------------------------
# 预检
# ---------------------------------------------------------------------------
Write-Host "[bench] T4 PNG 极限压缩 Benchmark 执行器（C-BENCH@1 实现）"
foreach ($f in @($script:FfmpegExe, $script:FfprobeExe)) {
    if (-not (Test-Path $f)) { Fail-AndExit "随包工具缺失：$f" }
}
if (-not (Test-Path $script:CicpExe)) { Fail-AndExit "pngcicp 缺失：$script:CicpExe（C-CHUNK@1 判据依赖）" }

# py launcher 预检（R3.5 第二解码器 + C-REFILTER harness 调用依赖）
$script:PyCmd = $null
$pyResolved = Get-Command 'py' -ErrorAction SilentlyContinue
if ($null -ne $pyResolved) { $script:PyCmd = 'py' }

# RunRoot / CorpusRoot 解析
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $guid = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $RunRoot = Join-Path $script:RepoRoot ("tests/output/png-opt-bench/$guid")
    New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null
    Write-Host "[bench] RunRoot 未指定，已新建轮次目录：$RunRoot"
}
$RunRootAbs = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RunRoot)
if (-not (Test-Path $RunRootAbs)) { Fail-AndExit "RunRoot 不存在：$RunRootAbs（先由 T2 建立轮次目录）" }

if ([string]::IsNullOrWhiteSpace($CorpusRoot)) { $CorpusRoot = $RunRootAbs }
$CorpusRootAbs = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CorpusRoot)

$manifestPath = Join-Path $CorpusRootAbs 'corpus/manifest.json'
if (-not (Test-Path $manifestPath)) { Fail-AndExit "manifest.json 缺失：$manifestPath（C-PMATRIX@1 调度输入）" }

# tools.json 解析（查找顺序见头注释）
$ToolsRootAbs = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
    (Join-Path $script:RepoRoot $ToolsRoot))
$toolsJsonPath = Join-Path $ToolsRootAbs 'tools.json'
if (-not (Test-Path $toolsJsonPath)) {
    if ($ToolsRootAbs -match '\\tools$') {
        $cand = Join-Path (Split-Path -Parent $ToolsRootAbs) 'tools.json'
        if (Test-Path $cand) { $toolsJsonPath = $cand }
    }
}
if (-not (Test-Path $toolsJsonPath)) { Fail-AndExit "tools.json 缺失：$toolsJsonPath（C-TOOLLIST@1 台账，T1 交付）" }

# 判据 redirect 临时件：落 RunRoot/logs（产物区），不落 %TEMP%（AGENTS §3）
$logsDir = Join-Path $RunRootAbs 'logs'
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
$script:ProbeOf = Join-Path $logsDir '_probe.out'
$script:ProbeEf = Join-Path $logsDir '_probe.err'

$modeWord = $Smoke ? 'smoke' : 'full'
$prefix   = $Smoke ? 'smoke-' : ''
Write-Host ("[bench] run={0} mode={1} timeout={2}s resume={3} tools={4}" -f `
    $RunRootAbs, $modeWord, $TimeoutSec, ($Resume ? 'on' : 'off'), $toolsJsonPath)

$ffmpegVer = (Invoke-Native -Exe $script:FfmpegExe -ArgList @('-version')).Stdout -split "`n" | Select-Object -First 1

# ---------------------------------------------------------------------------
# tools.json → 臂计划（C-TOOLLIST@1 消费 + R1.4 冒烟门禁）
# ---------------------------------------------------------------------------
$toolsJson = ConvertFrom-Json (Read-TextShared $toolsJsonPath)
$toolsByArm = @{}
foreach ($e in @($toolsJson)) {
    $aid = Get-JProp $e 'arm_id' $null
    if ($aid) { $toolsByArm[$aid] = $e }
}

function Resolve-ExeSpec([string]$Spec) {
    # exe_path 解析：绝对直用；相对先 RepoRoot 再 ToolsRoot；无路径分隔符视为 launcher 命令（py）
    if ([string]::IsNullOrWhiteSpace($Spec)) { return $null }
    if ([System.IO.Path]::IsPathRooted($Spec)) { if (Test-Path $Spec) { return $Spec } else { return $null } }
    $c1 = Join-Path $script:RepoRoot $Spec
    if (Test-Path $c1) { return $c1 }
    $c2 = Join-Path $ToolsRootAbs $Spec
    if (Test-Path $c2) { return $c2 }
    if ($Spec -notmatch '\\|/') { return $Spec }
    return $null
}

$armPlans = @()
foreach ($a in $script:ArmTable) {
    $entry = $toolsByArm[$a.Id]
    $reason = $null; $exe = $null; $impl = $a.Refilter; $dllRoot = $null

    if ($null -eq $entry) {
        $reason = 'tools.json-missing-entry'                      # R1.3：禁止静默缺臂 → 显式 skip
    }
    elseif ((Get-JProp $entry 'skip' $false) -eq $true) {
        $reason = Get-JProp $entry 'skip_reason' 'tools.json-skip=true (no reason given)'
    }
    elseif ($null -ne $impl) {
        # 对照臂：T4 强制接线 harness（C-REFILTER@1），不消费 args_template
        if (-not (Test-Path $script:RefilterPy)) { $reason = 'refilter-harness-missing' }
        elseif (-not $script:PyCmd) { $reason = 'python-launcher-missing' }
        else {
            $shape = Get-JProp $entry 'shape' ''
            if ($shape -eq 'dll+ctypes') {
                $dllSpec = Get-JProp $entry 'exe_path' $null
                $dllRes = Resolve-ExeSpec $dllSpec
                if ($null -ne $dllRes) { $dllRoot = Split-Path -Parent $dllRes }
            }
            $exe = $script:PyCmd
        }
    }
    else {
        $exe = Resolve-ExeSpec (Get-JProp $entry 'exe_path' '')
        if ($null -eq $exe) { $reason = 'exe-missing (' + (Get-JProp $entry 'exe_path' '?') + ')' }
    }

    if ($null -eq $reason -and -not $Smoke) {
        # R1.4 门禁：冒烟未过不进全量。三判据 = exit_code==0 && valid_png && pixel_equal
        # ⚠ 变量名用 smokeRec：$smoke 会与 [switch]$Smoke 撞名（PS 变量名大小写不敏感，
        #   param 参数变量带类型约束，赋 PSCustomObject 当场转换失败）
        $smokeRec = Get-JProp $entry 'smoke' $null
        if ($null -eq $smokeRec) { $reason = 'smoke-missing (R1.4)' }
        else {
            $okExit = ((Get-JProp $smokeRec 'exit_code' (-1)) -eq 0)
            $okPng  = ((Get-JProp $smokeRec 'valid_png' $false) -eq $true)
            $okPix  = ((Get-JProp $smokeRec 'pixel_equal' $false) -eq $true)
            if (-not ($okExit -and $okPng -and $okPix)) { $reason = 'smoke-not-passed (R1.4)' }
        }
    }

    $armPlans += [pscustomobject]@{
        Ord = $a.Ord; Id = $a.Id; Spec = $a.Spec; Refilter = $impl
        Entry = $entry; Exe = $exe; DllRoot = $dllRoot; SkipReason = $reason
    }
}

# ---------------------------------------------------------------------------
# manifest → case 计划（C-PMATRIX@1 消费 + T2 交付质量门）
# ---------------------------------------------------------------------------
$manifest = ConvertFrom-Json (Read-TextShared $manifestPath)
$casePlans = @()
foreach ($m in @($manifest)) {
    $cid      = Get-JProp $m 'case_id' $null
    $file     = Get-JProp $m 'file' ''
    $pf       = Get-JProp $m 'pix_fmt' ''
    $w        = Get-JProp $m 'width' 0
    $h        = Get-JProp $m 'height' 0
    $sha      = Get-JProp $m 'sha256_16' ''
    $bl0      = Get-JProp $m 'baselines' $null
    $bl0F     = $null; $bl9F = $null
    if ($null -ne $bl0) {
        $l0 = $bl0.PSObject.Properties['l0']; $l9 = $bl0.PSObject.Properties['l9']
        if ($l0) { $bl0F = Join-Path $CorpusRootAbs (Get-JProp $l0.Value 'file' '') }
        if ($l9) { $bl9F = Join-Path $CorpusRootAbs (Get-JProp $l9.Value 'file' '') }
    }
    $chunkSample = (Get-JProp $m 'chunk_sample' $false) -eq $true
    $chunkSrc    = Get-JProp $m 'chunk_source_file' $null

    $err = $null
    $inFile = Join-Path $CorpusRootAbs $file
    if ($chunkSample -and $chunkSrc) { $inFile = Join-Path $CorpusRootAbs $chunkSrc }
    $CaseCorpusFile = Join-Path $CorpusRootAbs $file

    if (-not $cid) { $err = 'manifest-entry-no-case_id' }
    elseif (-not (Test-Path $inFile)) { $err = "input-missing: $inFile" }
    elseif ($chunkSample -and (-not (Test-Path (Join-Path $CorpusRootAbs $file)))) { $err = "corpus-file-missing: $file" }
    elseif (-not ($bl0F -and (Test-Path $bl0F))) { $err = "baseline-l0-missing: $bl0F" }
    elseif (-not ($bl9F -and (Test-Path $bl9F))) { $err = "baseline-l9-missing: $bl9F" }
    else {
        # T2 交付质量门（C-PMATRIX 失败语义）：manifest.sha256_16 = file 字段（corpus 原件）的 sha。
        # 注意 chunk_sample=true 时臂输入是 chunk_source_file（meta 子样本），其字节 ≠ 原件
        # （插入 iCCP/cICP/eXIf），不能拿它对撞原件 sha——子样本只查存在性（schema 无其 sha 字段）。
        if ($sha -and ((Get-Sha16 $CaseCorpusFile) -ne $sha)) { $err = "sha256-mismatch: $CaseCorpusFile (manifest=$sha)" }
        if (-not $err) {
            if ((Get-Sha16 $bl0F) -ne (Get-JProp (Get-JProp $bl0 'l0' $null) 'sha256_16' '')) { $err = "baseline-l0-sha-mismatch: $bl0F" }
            elseif ((Get-Sha16 $bl9F) -ne (Get-JProp (Get-JProp $bl0 'l9' $null) 'sha256_16' '')) { $err = "baseline-l9-sha-mismatch: $bl9F" }
        }
    }

    $casePlans += [pscustomobject]@{
        Id = $cid; PixFmt = $pf; W = $w; H = $h
        CorpusFile = (Join-Path $CorpusRootAbs $file); InputFile = $inFile
        BaselineL0 = $bl0F; BaselineL9 = $bl9F
        ChunkSample = $chunkSample; SmokeFlag = (Get-JProp $m 'smoke' $null)
        SetupErr = $err; Cache = @{}
    }
}

# -Smoke 素材选择：manifest 中 smoke=true 的条目；无任何条目带 smoke 字段时回退 B01（C-BENCH@1 默认）
if ($Smoke) {
    $flagged = @($casePlans | Where-Object { $_.SmokeFlag -eq $true })
    if ($flagged.Count -gt 0) { $casePlans = $flagged }
    else {
        $fb = @($casePlans | Where-Object { $_.Id -eq 'B01' })
        if ($fb.Count -gt 0) { $casePlans = $fb; Write-Host "[bench] -Smoke：manifest 无 smoke 标记，回退契约默认 B01" }
        else { Fail-AndExit "-Smoke：manifest 既无 smoke 标记也无 B01" }
    }
}
if ($casePlans.Count -eq 0) { Fail-AndExit "manifest 无可用素材条目" }

# per-case 缓存判据（首格用时算）：输入 ffprobe / L9 基线 md5 锚 / 子样本 chunk 清单
function Get-CaseCache($Case, [string]$Key) {
    if ($Case.Cache.ContainsKey($Key)) { return $Case.Cache[$Key] }
    $v = $null
    switch ($Key) {
        'probeIn' { $v = Get-PngProbe $Case.InputFile }
        'md5L9'   { $v = Get-PngDecodedMd5 $Case.BaselineL9 }   # C-PIXEL@1：对基线取解码 md5 锚
        'md5L0'   { $v = Get-PngDecodedMd5 $Case.BaselineL0 }
        'chunksIn'{ $v = Get-PngChunks $Case.InputFile }
    }
    $Case.Cache[$Key] = $v
    return $v
}

# ---------------------------------------------------------------------------
# -Resume：载入已有行（C-BENCH@1：跳过 (case_id, arm_id) 已有有效行，只补缺行）
# ---------------------------------------------------------------------------
$benchFile   = Join-Path $RunRootAbs ($prefix + 'bench.json')
$tsvFile     = Join-Path $RunRootAbs ($prefix + 'summary.tsv')
$mdFile      = Join-Path $RunRootAbs ($prefix + 'summary.md')
$existingRows = @()
if ($Resume -and (Test-Path $benchFile)) {
    try { $existingRows = @(ConvertFrom-Json (Read-TextShared $benchFile)) } catch { $existingRows = @() }
    $existingRows = @($existingRows | Where-Object { $_ -and (Get-JProp $_ 'case_id' $null) })
}
$doneKeys = @{}
foreach ($r in $existingRows) {
    if ((Get-JProp $r 'status' 'setup_fail') -ne 'setup_fail') {
        $doneKeys["{0}|{1}" -f $r.case_id, $r.arm_id] = $r
    }
}

# ---------------------------------------------------------------------------
# 主循环：臂（表序 1→14）× 格（素材序）——C-BENCH@1
# ---------------------------------------------------------------------------
$outRootAll = Join-Path $RunRootAbs 'outputs'
New-Item -ItemType Directory -Path $outRootAll -Force | Out-Null

$rows = [System.Collections.Generic.List[object]]::new()
$budgetWarned = $false

function New-Row([string]$CaseId, [string]$ArmId) {
    # C-REPORT@1 bench.json 明细行 22 字段（字段序 = schema 顺序）
    return [pscustomobject][ordered]@{
        case_id = $CaseId; arm_id = $ArmId
        input_bytes = $null; baseline_l0_bytes = $null; baseline_l9_bytes = $null
        output_bytes = $null; ratio_vs_l9 = $null; pct_saved_vs_l9 = $null
        pixel_equal = $null; ihdr_unchanged = $null
        decode_ffmpeg = $null; decode_pillow = $null
        chunk_loss = $null; chunk_gain = $null; corrupt = $null
        time_ms = $null; exit_code = $null; stderr_digest = $null
        timeout = $false; skip = $null; status = 'ok'; notes = ''
    }
}

function Add-Note($Row, [string]$Note) {
    if ([string]::IsNullOrEmpty($Note)) { return }
    if ([string]::IsNullOrEmpty($Row.notes)) { $Row.notes = $Note } else { $Row.notes = $Row.notes + '; ' + $Note }
}

function Invoke-Cell($Arm, $Case) {
    $row = New-Row $Case.Id $Arm.Id
    $logPath = Join-Path $logsDir ($prefix + $Arm.Id + '.log')

    if ($Case.SetupErr) {
        $row.status = 'setup_fail'
        $row.notes = $Case.SetupErr
        Add-Content -Path $logPath -Value ("=== case {0} === setup_fail: {1}" -f $Case.Id, $Case.SetupErr) -Encoding utf8
        return $row
    }
    if ($Arm.SkipReason) {
        $row.status = 'skip'
        $row.skip = $Arm.SkipReason
        Add-Content -Path $logPath -Value ("=== case {0} === skip: {1}" -f $Case.Id, $Arm.SkipReason) -Encoding utf8
        return $row
    }

    # 素材属性读数（skip/setup_fail 行保持 null）
    $row.input_bytes       = (Get-Item $Case.InputFile).Length
    $row.baseline_l0_bytes = (Get-Item $Case.BaselineL0).Length
    $row.baseline_l9_bytes = (Get-Item $Case.BaselineL9).Length

    $armDir = Join-Path $outRootAll $Arm.Id
    New-Item -ItemType Directory -Path $armDir -Force | Out-Null
    $outPng = Join-Path $armDir ($Case.Id + '.png')

    # 命令构造
    $entry = $Arm.Entry
    $rewrite = (Get-JProp $entry 'rewrite_inplace' $false) -eq $true
    $workPng = $null; $targetPng = $outPng; $workSha0 = $null
    $argList = @()

    if ($null -ne $Arm.Refilter) {
        # C-REFILTER@1 强制接线：py _make-png-refilter.py --in/--out/--impl [--dll-root]
        $argList = @((Join-Path $script:RepoRoot 'tests/scripts/_make-png-refilter.py'),
                     '--in', $Case.InputFile, '--out', $outPng, '--impl', $Arm.Refilter)
        if ($Arm.DllRoot) { $argList += @('--dll-root', $Arm.DllRoot) }
        $cmdExe = $script:PyCmd
    }
    else {
        if ($rewrite) {
            # R3.6 隔离副本协议：copy → 原位处理 → 收产物；工作副本即产物本体（不二次复制）
            $workPng = Join-Path $armDir ($Case.Id + '.work.png')
            Copy-Item -Path $Case.InputFile -Destination $workPng -Force
            $workSha0 = Get-Sha16 $workPng
            $targetPng = $workPng
        }
        $tmpl = @(Get-JProp $entry 'args_template' @())
        foreach ($t in $tmpl) {
            $v = $t
            if ($v -ceq '<in>')  { $v = $Case.InputFile }
            elseif ($v -ceq '<out>') { $v = $targetPng }
            $argList += $v
        }
        $cmdExe = $Arm.Exe
    }
    $cmdLine = "$cmdExe " + (($argList | ForEach-Object { Quote-Arg $_ }) -join ' ')
    Add-Content -Path $logPath -Value "=== case $($Case.Id) ===" -Encoding utf8
    Add-Content -Path $logPath -Value "cmd: $cmdLine" -Encoding utf8

    # 执行 + 单格超时（R3.4：600s 上限 kill，不阻塞后格）
    $of = Join-Path $logsDir ($Arm.Id + '.last.out')
    $ef = Join-Path $logsDir ($Arm.Id + '.last.err')
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    $exitCode = $null
    $stderrText = ''
    $p = $null
    try {
        $p = Start-Process -FilePath $cmdExe -ArgumentList (($argList | ForEach-Object { Quote-Arg $_ }) -join ' ') `
             -WorkingDirectory $script:RepoRoot -NoNewWindow -PassThru `
             -RedirectStandardOutput $of -RedirectStandardError $ef
        $done = $p.WaitForExit($TimeoutSec * 1000)
        if (-not $done) {
            $timedOut = $true
            try { $p.Kill($true) } catch { }        # .NET entireProcessTree：连孙进程一并终止
            $p.WaitForExit() | Out-Null
        }
        $exitCode = $p.ExitCode
    } catch {
        $exitCode = -998
        $stderrText = "start-process-fail: $($_.Exception.Message)"
    }
    $sw.Stop()
    $row.time_ms = [int]$sw.ElapsedMilliseconds
    if ($timedOut) { $row.timeout = $true }
    if ($null -ne $exitCode) { $row.exit_code = $exitCode }
    if ($stderrText -eq '') { $stderrText = Read-TextShared $ef }
    $row.stderr_digest = Flatten-LogText $stderrText
    Add-Content -Path $logPath -Value ("exit={0} time_ms={1} timeout={2}" -f $exitCode, $row.time_ms, $timedOut) -Encoding utf8
    Add-Content -Path $logPath -Value ("stderr: " + $(if ($row.stderr_digest) { $row.stderr_digest } else { '(empty)' })) -Encoding utf8

    # 产物候选判定：原位臂以"工作副本 sha 是否被改写"识别工具是否实际产出（R3.6）
    $produced = $false
    if ($null -ne $workPng) {
        if (Test-Path $workPng) {
            $workSha1 = Get-Sha16 $workPng
            if ($workSha1 -eq $workSha0 -and $exitCode -ne 0) {
                $produced = $false   # 工具没写文件却报错：副本不算产物（防止把输入副本误收为产物）
            } else {
                $produced = $true
            }
        }
    } else {
        $produced = (Test-Path $outPng) -and ((Get-Item $outPng -ErrorAction SilentlyContinue).Length -gt 0)
    }

    # ---- 判据链（产物存在且可解析才跑测量）----
    if ($produced) {
        $md5Out = Get-PngDecodedMd5 $targetPng
        if ($null -eq $md5Out) {
            # ffmpeg 无法解码 = 产物不可解析 → 无效产物
            $produced = $false
            Add-Note $row 'output-unreadable'
        } else {
            $row.output_bytes = (Get-Item $targetPng).Length
            $base9 = [double]$row.baseline_l9_bytes
            if ($base9 -gt 0 -and $row.output_bytes -gt 0) {
                $row.ratio_vs_l9     = [math]::Round($base9 / [double]$row.output_bytes, 3)
                $row.pct_saved_vs_l9 = [math]::Round((1.0 - [double]$row.output_bytes / $base9) * 100.0, 2)
            }

            # C-PIXEL@1：解码 md5 与基线锚逐字符相等 ⇒ pixel_equal=true
            $md5Anchor = Get-CaseCache $Case 'md5L9'
            $row.decode_ffmpeg = $true
            $row.pixel_equal = ($null -ne $md5Anchor -and $md5Out -eq $md5Anchor)

            # C-PIXEL@1：IHDR 不变检查（输入侧 ffprobe 实测为基准，产物侧对撞——
            # 绕开 manifest 声明与 ffprobe 命名的 le/be 差，如 gray16le/gray16be）
            $probeIn  = Get-CaseCache $Case 'probeIn'
            $probeOut = Get-PngProbe $targetPng
            if ($null -ne $probeIn -and $null -ne $probeOut) {
                $row.ihdr_unchanged = ($probeOut.PixFmt -eq $probeIn.PixFmt -and `
                                       $probeOut.W -eq $probeIn.W -and $probeOut.H -eq $probeIn.H)
                if (-not $row.ihdr_unchanged) {
                    Add-Note $row ("ihdr-changed: {0} {1}x{2} -> {3} {4}x{5}" -f `
                        $probeIn.PixFmt, $probeIn.W, $probeIn.H, $probeOut.PixFmt, $probeOut.W, $probeOut.H)
                }
            }

            # R3.5：第二解码器结构检查（期望宽高 = manifest 规格，期望模式 = pix_fmt 映射）
            $modes = $null
            if ($script:PilModeMap.ContainsKey($Case.PixFmt)) { $modes = $script:PilModeMap[$Case.PixFmt] }
            else { Add-Note $row "unknown-pixfmt-mapping: $($Case.PixFmt)" }
            $row.decode_pillow = Invoke-PillowCheck $targetPng $Case.W $Case.H $modes

            # C-CHUNK@1：chunk 保真（只作用于带 chunk 子样本的素材）
            if ($Case.ChunkSample) {
                $chunksIn = Get-CaseCache $Case 'chunksIn'
                $chunksOut = Get-PngChunks $targetPng
                if ($null -eq $chunksIn) {
                    # 输入子样本自身不可解析（T2 构造质量问题）→ chunk 判定列不判，像素/压缩列照常（C-PMATRIX 失败语义）
                    Add-Note $row 'chunk-input-unreadable'
                } elseif ($null -eq $chunksOut) {
                    $row.corrupt = $true
                    Add-Note $row 'output-chunk-parse-fail-or-no-IEND'
                } else {
                    $cmp = Compare-ChunkLists $chunksIn $chunksOut
                    $row.chunk_loss = $cmp.Loss
                    $row.chunk_gain = $cmp.Gain
                    $row.corrupt = $false
                    if ($cmp.Loss.Count -gt 0) { Add-Note $row ("chunk_loss: " + ($cmp.Loss -join ',')) }
                }
            }
        }
    }

    # ---- status 决策树 + 产物收档 ----
    if ($timedOut) {
        $row.status = 'timeout'
        if ($produced) {
            Add-Note $row 'timeout-after-write'
        } else {
            Add-Note $row 'timeout-no-output'

        }
    }
    elseif (-not $produced) {
        $row.status = 'fail'
        if (-not $row.notes) { Add-Note $row ("no-valid-output (exit={0})" -f $exitCode) }
    }
    else {
        $row.status = 'ok'
        if ($exitCode -ne 0) { Add-Note $row "nonzero-exit" }
    }

    # 产物收档（C-BENCH 布局：outputs/<arm>/<case>.png；timeout 无有效产物 / fail 不落产物）
    if ($null -ne $workPng) {
        if ($produced) {
            Move-Item -Path $workPng -Destination $outPng -Force   # 工作副本即产物本体，改名收档（-Force 覆盖重跑残留）
        } else {
            Remove-Item $workPng -Force -ErrorAction SilentlyContinue
        }
    } elseif ((Test-Path $outPng) -and -not $produced) {
        Remove-Item $outPng -Force -ErrorAction SilentlyContinue
    }

    # harness 退出码 3 → skip（C-REFILTER@1：dll 缺失 fallback），其余非零 → fail
    if ($null -ne $Arm.Refilter -and $exitCode -eq 3 -and -not $produced) {
        $row.status = 'skip'
        $row.skip = 'refilter-dll-missing (exit 3)'
    }

    return $row
}

$totalCells = $armPlans.Count * $casePlans.Count
Write-Host ("[bench] cells={0} (arms={1} x cases={2})" -f $totalCells, $armPlans.Count, $casePlans.Count)

$armSummaries = @()
foreach ($arm in $armPlans) {
    $armSw = [System.Diagnostics.Stopwatch]::StartNew()
    $np = 0; $nf = 0; $ns = 0; $nt = 0; $nResumed = 0
    # 每轮日志清空重写（取证口径 = 本轮动作；resume 跳过格不在本轮日志）
    Set-Content -Path (Join-Path $logsDir ($prefix + $arm.Id + '.log')) `
        -Value ("[arm-start] {0} mode={1} at {2}" -f $arm.Id, $modeWord, (Get-Date).ToString('HH:mm:ss')) -Encoding utf8
    foreach ($case in $casePlans) {
        $key = "{0}|{1}" -f $case.Id, $arm.Id
        if ($Resume -and $doneKeys.ContainsKey($key)) {
            $rows.Add($doneKeys[$key])
            $nResumed++
            continue
        }
        $row = Invoke-Cell $arm $case
        $rows.Add($row)

        # [arm] 行计数口径（见头注释）：pass=有效压缩格；fail=硬失败+setup_fail+违例
        if ($row.status -eq 'timeout') { $nt++ }
        elseif ($row.status -eq 'skip') { $ns++ }
        elseif ($row.status -eq 'fail' -or $row.status -eq 'setup_fail') { $nf++ }
        elseif ($row.status -eq 'ok') {
            if ($row.pixel_equal -eq $false) { $nf++ } else { $np++ }
        } else { $nf++ }
    }
    $armSw.Stop()
    $armExit = (($nf -gt 0 -or $nt -gt 0) ? 1 : 0)
    Write-Host ("[arm] exit={0} pass={1} fail={2} skip={3} timeout={4} {5:F1} s" -f `
        $armExit, $np, $nf, $ns, $nt, $armSw.Elapsed.TotalSeconds)

    # C-BENCH@1 总时长预算监控：累计超 6 小时打印提示但不自动终止
    $elapsedH = ((Get-Date) - $script:ScriptStart).TotalHours
    if ($elapsedH -gt $script:BudgetHours -and -not $budgetWarned) {
        $budgetWarned = $true
        Write-Host ("[budget] elapsed={0:F2}h 已超 {1} 小时预算——按 C-BENCH@1 继续执行，不自动终止" -f $elapsedH, $script:BudgetHours)
    }
    $armSummaries += [pscustomobject]@{
        Arm = $arm; Pass = $np; Fail = $nf; Skip = $ns; Timeout = $nt; Resumed = $nResumed
        Seconds = [math]::Round($armSw.Elapsed.TotalSeconds, 1)
    }

    # [rev2 增量落盘] 每臂完成即全量 flush bench.json（2026-11-09 FullBench-T5d，leader 授权链
    # = T5d 任务书第 2 条，仅改持久化时机）。序列化口径与下方最终落盘段逐字一致（合法 JSON
    # 数组 + 每行 ConvertTo-Json -Compress -Depth 4）；判据字段/决策树/schema/调度逻辑零改动。
    # 任何中断后本文件含已完成臂的行，-Resume 按 (case_id, arm_id) 自动补缺臂，死一次只丢当前臂。
    # tmp + Move-Item -Force 原子替换：被杀瞬间 bench.json 要么旧完整版要么新完整版，不会截断。
    try {
        $flushLines = @()
        foreach ($r in $rows) {
            $j = $r | ConvertTo-Json -Compress -Depth 4
            if ($j -is [array]) { $j = $j[0] }
            $flushLines += $j
        }
        $flushText = "[" + (($flushLines) -join ",`n") + "`n]"
        $flushTmp = $benchFile + '.flush-tmp'
        [System.IO.File]::WriteAllText($flushTmp, $flushText, (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -Path $flushTmp -Destination $benchFile -Force
        Write-Host ("[flush] rows={0} arm={1}" -f $flushLines.Count, $arm.Id)
    } catch {
        Write-Host ("[flush-warn] bench.json 增量 flush 失败（继续执行，最终落盘兜底）: {0}" -f $_.Exception.Message)
    }
}

# resume 守恒检查：合并后总行数必须等于 格总数
if ($rows.Count -ne $totalCells) {
    Fail-AndExit ("行数守恒破坏：rows={0} != cells={1}（existing={2}）" -f $rows.Count, $totalCells, $existingRows.Count)
}

# ---------------------------------------------------------------------------
# bench.json 落盘（C-REPORT@1：明细行数组 = 顶层合法 JSON 数组；每行一条紧凑 JSON 可定位）
# ---------------------------------------------------------------------------
$benchLines = @()
foreach ($r in $rows) {
    $j = $r | ConvertTo-Json -Compress -Depth 4
    if ($j -is [array]) { $j = $j[0] }
    $benchLines += $j
}
$benchText = "[" + (($benchLines) -join ",`n") + "`n]"
[System.IO.File]::WriteAllText($benchFile, $benchText, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[bench] bench.json -> $benchFile (rows=$($benchLines.Count))"

# ---------------------------------------------------------------------------
# 透视聚合（从落盘 bench.json 重读——R4.4 明细/透视一致性由同一数据源保障）
# ---------------------------------------------------------------------------
$diskRows = @(ConvertFrom-Json (Read-TextShared $benchFile))
if ($diskRows.Count -ne $rows.Count) { Fail-AndExit "bench.json 落盘行数与内存不一致" }

function Get-TsvVal($v) {
    # TSV 单元格口径：null → "null"；bool → true/false；数组 → 紧凑 JSON
    if ($null -eq $v) { return 'null' }
    if ($v -is [bool]) { return ($v ? 'true' : 'false') }
    if ($v -is [array]) { return (ConvertTo-Json @($v) -Compress) }
    return "$v"
}

# summary.tsv（C-REPORT@1：17 列，与 bench.json 同名同义，布尔列 true/false）
$tsvCols = @('case_id','arm_id','input_bytes','baseline_l9_bytes','output_bytes','ratio_vs_l9',
             'pct_saved_vs_l9','pixel_equal','decode_ffmpeg','decode_pillow','chunk_loss',
             'chunk_gain','corrupt','time_ms','exit_code','status','notes')
$tsvLines = [System.Collections.Generic.List[string]]::new()
$tsvLines.Add(($tsvCols -join "`t"))
foreach ($r in $diskRows) {
    $cells = foreach ($c in $tsvCols) {
        $v = $r.PSObject.Properties[$c].Value
        if ($c -eq 'notes') { (Get-TsvVal $v) -replace "`t", ' ' } else { Get-TsvVal $v }
    }
    $tsvLines.Add(($cells -join "`t"))
}
[System.IO.File]::WriteAllLines($tsvFile, $tsvLines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[bench] summary.tsv -> $tsvFile"

# summary.md（两张表：①R4.2 每臂透视十二列 ②case × arm 压缩比矩阵）
$md = [System.Collections.Generic.List[string]]::new()
$cmdSelf = "pwsh -NoProfile -File tests/scripts/_probe-png-opt-bench.ps1 -RunRoot $RunRoot" +
           ($(if ($Smoke) { ' -Smoke' }) ) + ($(if ($Resume) { ' -Resume' })) +
           " -ToolsRoot $ToolsRoot -TimeoutSec $TimeoutSec"
$md.Add("# PNG 极限压缩 Benchmark 报告（$modeWord）")
$md.Add("")
$md.Add("- 生成命令：$cmdSelf")
$md.Add("- 轮次目录：$RunRootAbs")
$md.Add("- 模式：$modeWord（timeout=$TimeoutSec s/cell，resume=$($Resume ? 'on' : 'off')）")
$md.Add("- 被测 ffmpeg：$ffmpegVer")
$md.Add("- 工具台账：$toolsJsonPath（C-TOOLLIST@1；各臂版本/许可见透视表）")
$md.Add("- 判据：C-PIXEL@1（ffprobe pix_fmt + IHDR 不变 + ffmpeg md5 对撞）、C-CHUNK@1（关键集 {iCCP,cICP,eXIf} 计数对撞）")
$md.Add("- 生成时间：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
$md.Add("")
$md.Add("## 1. 每臂透视（R4.2 十二列）")
$md.Add("")
$md.Add("| # | 臂 | 版本 | 许可 | 有效格数 | 几何平均比(vs L9) | 总节省% | lossless违例 | chunk丢失 | 解码失败 | timeout | 总耗时 | 结论备注 |")
$md.Add("|---|---|---|---|---|---|---|---|---|---|---|---|---|")

foreach ($a in $armSummaries) {
    $aid = $a.Arm.Id
    $armRows = @($diskRows | Where-Object { $_.arm_id -eq $aid })
    $ver    = Get-JProp $a.Arm.Entry 'version' 'n/a'
    $lic    = Get-JProp $a.Arm.Entry 'license' 'n/a'

    if ($null -ne $a.Arm.SkipReason) {
        # 整臂 SKIP（R4.3：SKIP 原因在行内备注）
        $md.Add("| $($a.Arm.Ord) | $aid | $ver | $lic | 0 | – | – | 0 | 0 | 0 | 0 | – | 整臂 SKIP：$($a.Arm.SkipReason) |")
        continue
    }

    $validRows   = @($armRows | Where-Object { $_.status -eq 'ok' -and $_.pixel_equal -eq $true })
    $violRows    = @($armRows | Where-Object { $_.pixel_equal -eq $false })
    $chunkLossN  = @($armRows | Where-Object { $null -ne $_.chunk_loss -and @($_.chunk_loss).Count -gt 0 }).Count
    $decFailN    = @($armRows | Where-Object { $_.decode_ffmpeg -eq $false -or $_.decode_pillow -eq $false }).Count
    $toN         = @($armRows | Where-Object { $_.timeout -eq $true }).Count
    $totalMs     = [long](@($armRows | Where-Object { $null -ne $_.time_ms } | ForEach-Object { $_.time_ms } | Measure-Object -Sum).Sum)

    $geo = '–'
    if ($validRows.Count -gt 0) {
        $lnSum = 0.0; $n = 0
        foreach ($vr in $validRows) {
            if ($null -ne $vr.ratio_vs_l9 -and [double]$vr.ratio_vs_l9 -gt 0) { $lnSum += [math]::Log([double]$vr.ratio_vs_l9); $n++ }
        }
        if ($n -gt 0) { $geo = [math]::Round([math]::Exp($lnSum / $n), 3) }
    }
    $sumOut = 0; $sumBase = 0
    foreach ($vr in $validRows) {
        if ($null -ne $vr.output_bytes -and $null -ne $vr.baseline_l9_bytes) { $sumOut += [long]$vr.output_bytes; $sumBase += [long]$vr.baseline_l9_bytes }
    }
    $savePct = ($sumBase -gt 0) ? [math]::Round((1.0 - [double]$sumOut / [double]$sumBase) * 100.0, 2) : '–'

    $remarks = @()
    if ($violRows.Count -gt 0)   { $remarks += "lossless 违例 $($violRows.Count) 格（R3.2，不计入有效压缩）" }
    if ($chunkLossN -gt 0)       { $remarks += "chunk 丢失 $chunkLossN 格（R3.3）" }
    if ($decFailN -gt 0)         { $remarks += "解码失败 $decFailN 格" }
    if ($toN -gt 0)              { $remarks += "timeout $toN 格（R3.4，诚实读数）" }
    if ($remarks.Count -eq 0)    { $remarks += "全部有效格像素等价、chunk 保真" }
    $md.Add("| $($a.Arm.Ord) | $aid | $ver | $lic | $($validRows.Count) | $geo | $savePct | $($violRows.Count) | $chunkLossN | $decFailN | $toN | $totalMs ms | $($remarks -join '; ') |")
}
$md.Add("")
$md.Add("## 2. case × arm 压缩比矩阵（格值 = pct_saved_vs_l9；✗loss=像素违例 / ⏱=timeout / ✗fail / ✗setup / –=skip）")
$md.Add("")
$hdr = "| case | " + (($armPlans | ForEach-Object { $_.Id }) -join " | ") + " |"
$sep = "|" + (((1..($armPlans.Count + 1)) | ForEach-Object { '---' }) -join '|') + "|"
$md.Add($hdr)
$md.Add(($sep))
foreach ($case in $casePlans) {
    $cells = foreach ($ap in $armPlans) {
        $r = $diskRows | Where-Object { $_.case_id -eq $case.Id -and $_.arm_id -eq $ap.Id } | Select-Object -First 1
        if ($null -eq $r) { '?' }
        elseif ($r.pixel_equal -eq $false) { '✗loss' }
        elseif ($r.timeout -eq $true) { '⏱' }
        elseif ($r.status -eq 'fail') { '✗fail' }
        elseif ($r.status -eq 'setup_fail') { '✗setup' }
        elseif ($r.status -eq 'skip') { '–' }
        elseif ($null -ne $r.pct_saved_vs_l9) { "$($r.pct_saved_vs_l9)%" }
        else { '?' }
    }
    $md.Add(("| {0} | {1} |" -f $case.Id, ($cells -join " | ")))
}
[System.IO.File]::WriteAllLines($mdFile, $md, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[bench] summary.md -> $mdFile"

# ---------------------------------------------------------------------------
# 尾部自检（fail-fast：守恒破坏 = 执行器 bug，静默不可接受）
# ---------------------------------------------------------------------------
foreach ($a in $armSummaries) {
    $sum = $a.Pass + $a.Fail + $a.Skip + $a.Timeout + $a.Resumed
    if ($sum -ne $casePlans.Count) {
        Fail-AndExit ("[assert] 臂 {0} 计数不守恒：pass+fail+skip+timeout+resumed={1} != 格数={2}" -f $a.Arm.Id, $sum, $casePlans.Count)
    }
}
$gPass = ($armSummaries | Measure-Object -Property Pass -Sum).Sum
$gFail = ($armSummaries | Measure-Object -Property Fail -Sum).Sum
$gSkip = ($armSummaries | Measure-Object -Property Skip -Sum).Sum
$gTo   = ($armSummaries | Measure-Object -Property Timeout -Sum).Sum
$elapsed = ((Get-Date) - $script:ScriptStart).TotalSeconds
Write-Host ("[done] rows={0} pass={1} fail={2} skip={3} timeout={4} {5:F1} s" -f `
    $diskRows.Count, $gPass, $gFail, $gSkip, $gTo, $elapsed)
Write-Host ("[done] 三件套：{0} / {1} / {2}" -f $benchFile, $tsvFile, $mdFile)