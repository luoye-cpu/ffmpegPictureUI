# verify-png-structure.ps1 -- P3-D 门禁: PNG 容器结构合规(Test-PngStructure 的唯一消费者)
#
# 为什么需要这一条:
#   `_lib-oracle.ps1` 的 `Test-PngStructure` 已于 2026-09-19 加固为真正的 PNG 容器合规校验
#   (逐 chunk CRC32 含 IHDR/IDAT/IEND; IHDR 必须首块且长度恰 13; IEND 后禁尾随字节;
#    IHDR 字段语义合法性: 宽高>0 / BitDepth x ColorType 合法组合 / Compression=0 / Filter=0 /
#    Interlace in {0,1})。
#   ⚠ 但加固当时**全仓零调用者**(grep 只命中库自身) => 等于零覆盖。本门禁是它的**唯一消费者**,
#     把它接上线; 没有本文件, 那次加固就只是一段没人跑的死代码。
#
# 判据(必须**同时**满足, 缺一不可):
#   退出码 == 0  ^  汇总行存在  ^  FAIL == 0  ^  锚点在册(required id 集合 含于 实际 PASS id 集合)
#   ⚠ 本仓教训: `exit 0` 可能只是"跑完了"; 汇总行存在但退出码非 0 也判红。
#
# 工程形态(照 verify-ui-host.ps1 / verify-ui-strategy-map.ps1):
#   1. 仓库根由脚本自身位置推导(tests/scripts/x.ps1 => 上两级), 不写死绝对路径;
#   2. 主跑 = **子进程**(本脚本用 `-Child` 自调) + **硬超时**(默认 120 s);
#      本脚本**不依赖任何外部 oracle 工具**(纯字节读), 因此只有进程级风险;
#   3. 汇总行解析: 只匹配 ASCII 片段 `PNGSTRUCT RESULT: pass=N fail=M`;
#   4. 锚点在册: 按**实际产出的断言名集合**比对 —— 从 `  PASS <id> ...` 行取首 token,
#      再与脚本内的 required 清单做集合差。⚠ 不用宽松的 `Contains` 全文件匹配
#      (本仓教训: 子串匹配会把 "id 出现在别人 detail 里" 也算命中 => 断言恒真);
#   5. `-SelfCheck`: 把某个**必产锚点的期望值换成不存在的名字**(环境变量 PNGSTRUCT_NEGCTL=1)
#      => 子进程必须转红 => 证明「退出码 + 汇总行 + 锚点在册」这条判读链不是空跑。
#
# ⚠⚠ 退出码通道: 双宿主实测(2026-09-19, 本脚本第一轮踩过, 三个读数见报告):
#   ① **PS 5.1 父进程**: `Start-Process -PassThru`(不带 `-Wait`) 拿到的 `.ExitCode` 恒为 `$null`
#      —— `WaitForExit()` / `Refresh()` / 再等 500 ms 都**无效**; 只有 `-Wait` 才有值(实测 14)。
#      但 `-Wait` **没有 timeout 参数** => 用了就丢掉硬超时(本仓硬约束 §6) => 不可用。
#   ② **PS 7 父进程**: 同一写法 `.ExitCode` 正常(实测 11 / 12 / 13)。
#      ⚠ 只在 PS 7 父进程下试, 会得出"没问题"的**错误结论**(本脚本第一版就是这么错的)。
#   ③ 模板那套「`-Command` 包装 + `& <目标>; WriteAllText(rc, $LASTEXITCODE)`」对 **.ps1** 目标
#      也救不了: 5.1 下包装行会执行但 `$LASTEXITCODE` 是**空串**(且进程退出码恒 0), 7 下能拿到
#      数字但进程退出码**仍恒 0**。(模板里能用, 是因为它包的是 UiTestHost.exe 这类原生 exe。)
#   => 本脚本的通道 = **子进程自己写 rc 文件**(它自己知道 $fail) + 父进程优先取 `.ExitCode`,
#      取不到再读 rc 文件, 并**要求两条通道互相印证**(P1.5)。这样 5.1/7 都拿到真实值;
#      子进程被超时击杀 / 解析失败时**两条通道同时为空** => rc=-1 => 判红(绝不静默沿用上一轮)。
#
# 用法:
#   pwsh       -NoProfile -File tests/scripts/verify-png-structure.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/scripts/verify-png-structure.ps1
#   ... -TimeoutSec 300    # 覆盖超时(秒)
#   ... -SelfCheck         # 注入**必红**故障(PNGSTRUCT_NEGCTL=1: 把一个必产锚点的期望值换成
#                          # 不存在的名字) => 本门禁**必须转红(exit 1)**, 这就是**预期结果**;
#                          # P4.1/P4.2 证明那次红是**锚点在册检查**报出来的(不是别的什么顺手报的)。
#                          # 若连红都红不了 => 判读链是空跑 => 同样 exit 1(但理由串不同)。
#   ... -MinPng 5          # 素材下限断言(防素材目录被清空 => 静默全绿)
#
# 退出码: 0 = 全绿; 1 = 有失败(含超时 / 缺汇总行 / 非零退出码 / FAIL>0 / 锚点缺失)
#         ⚠ 带 `-SelfCheck` 时 **exit 1 是预期结果**(语义 = 期望本门禁转红)。
#
# ⚠ 双宿主兼容(PS 7 / PS 5.1): 禁 PS7+ 语法(三元 `? :` / `??` / `?.` / `&&` / `||`);
#   需要条件表达式一律用 `$(if (...) {...} else {...})`。
# ⚠ 代码串纯 ASCII: 所有**字符串字面量**不含中文, 解释性中文一律放注释。
param(
    [int]$TimeoutSec = 120,
    [switch]$SelfCheck,
    [switch]$Child,
    [int]$MinPng = 5,
    [string]$RcFile = ''
)

$ErrorActionPreference = 'Continue'

# 仓库根: 由脚本自身位置推导(tests/scripts/x.ps1 => 上两级)
$scriptDir = $PSScriptRoot
$testsDir  = Split-Path -Parent $scriptDir
$repoRoot  = Split-Path -Parent $testsDir

# ---------------------------------------------------------------------------
# 记账 helper: 断言名 = 消息的首 token(如 `A1.2`)。实际产出集合 = $script:ids。
# ---------------------------------------------------------------------------
$pass = 0; $fail = 0
$script:ids = New-Object System.Collections.ArrayList

function CK($ok, $msg) {
    $id = (($msg -split ' ')[0])
    [void]$script:ids.Add($id)
    if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
    else     { $script:fail++; Write-Output "  FAIL $msg" }
}

# 本脚本**应当产出**的断言名清单(锚点册)。
# ⚠ 只登记 E 块之前(实质断言)的 id: E 块是"锚点自检"本身, 放进册里会自我循环。
# ⚠ `PNGSTRUCT_NEGCTL=1` 时**追加一个不存在的名字** => E1.1 必红 => 供 -SelfCheck 使用。
function Get-RequiredAnchorIds {
    $ids = @(
        'A1.1', 'A1.2', 'A1.3', 'A1.4', 'A1.5', 'A1.6',
        'B0.1',
        'B1.1', 'B1.2', 'B1.3', 'B1.4', 'B1.5', 'B1.6', 'B1.7', 'B1.8',
        'B2.1',
        'B3.1', 'B3.2', 'B3.3',
        'C1.1',
        'D1.1', 'D1.2', 'D1.3'
    )
    if ($env:PNGSTRUCT_NEGCTL -eq '1') { $ids += 'ZZNEGCTL_MISSING_ANCHOR' }
    # ⚠ 不能写 `return ,$ids`: 那会把整个数组当成**单个对象**输出, 调用方的 `@(fn)` 只会
    #   收到 1 个元素(元素本身是数组) => `-notcontains` 逐条比对全部落空(本轮实测踩过:
    #   D1.1/E1.1 报"全部缺失", 而 P3.2 打印出 `System.Object[]`)。
    #   直接 `return $ids` 让管道正常展开, 调用方 `@(fn)` 才能拿到元素集合。
    return $ids
}

# ---------------------------------------------------------------------------
# 子进程运行器(带硬超时)。
# ⚠ 走 `-File` 直调 + `WaitForExit()` 后读 `.ExitCode` —— 双宿主实测均拿到真实退出码
#   (见文件头「实测更正」; 不用 rc 文件回传, 因为它对 .ps1 目标在 5.1 下退化为空串)。
# 返回 @{ ExitCode; TimedOut; OutFile; ErrFile; StartError }; ExitCode 永不返回 $null(缺 => -1)。
# ---------------------------------------------------------------------------
function Invoke-PsProc {
    param(
        [string]$HostExe,
        [string[]]$PsArgs,
        [int]$TimeoutMs,
        [hashtable]$EnvOverride,
        [string]$WorkDir,
        [string]$Tag,
        [string]$RcFile = ''
    )
    $outF = Join-Path $WorkDir ('out_' + $Tag + '.txt')
    $errF = Join-Path $WorkDir ('err_' + $Tag + '.txt')
    $res = @{ ExitCode = -1; TimedOut = $false; OutFile = $outF; ErrFile = $errF; StartError = ''
              OsExitCode = $null; RcExitCode = $null }

    # 含空格的参数补引号(仓库路径可能带空格)
    $quoted = @()
    foreach ($a in $PsArgs) {
        if ($a -match '\s') { $quoted += ('"' + $a + '"') } else { $quoted += $a }
    }

    $saved = @{}
    foreach ($k in $EnvOverride.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process')
        [Environment]::SetEnvironmentVariable($k, [string]$EnvOverride[$k], 'Process')
    }

    $p = $null
    try {
        $p = Start-Process -FilePath $HostExe -ArgumentList $quoted -NoNewWindow -PassThru `
                -RedirectStandardOutput $outF -RedirectStandardError $errF
    } catch {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
        $res.StartError = $_.Exception.Message
        return $res
    }

    $finished = $false
    try { $finished = $p.WaitForExit($TimeoutMs) } catch { $finished = $false }

    if (-not $finished) {
        $res.TimedOut = $true
        # PS 5.1 没有 $p.Kill($true) 重载 => 用 taskkill /T 杀整棵树, 再补无参 Kill 兜底
        try { & taskkill.exe /PID $p.Id /T /F 2>$null | Out-Null } catch { }
        try { $null = $p.WaitForExit(5000) } catch { }
        if (-not $p.HasExited) { try { $p.Kill() } catch { } }
    } else {
        try { $p.WaitForExit() } catch { }   # 排空异步输出流(否则可能读到半截产物)
    }

    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }

    # 退出码双通道: OS 级 `.ExitCode`(PS 7 有值 / PS 5.1 恒 $null) + 子进程自写的 rc 文件
    if (-not $res.TimedOut) {
        try { if ($null -ne $p.ExitCode) { $res.OsExitCode = [int]$p.ExitCode } } catch { $res.OsExitCode = $null }
    }
    if (($RcFile -ne '') -and (Test-Path -LiteralPath $RcFile)) {
        try {
            $t = [System.IO.File]::ReadAllText($RcFile).Trim()
            if ($t -ne '') { $res.RcExitCode = [int]$t }
        } catch { $res.RcExitCode = $null }
    }
    if ($null -ne $res.OsExitCode) { $res.ExitCode = $res.OsExitCode }
    elseif ($null -ne $res.RcExitCode) { $res.ExitCode = $res.RcExitCode }
    else { $res.ExitCode = -1 }
    return $res
}

# 解析汇总行。只匹配 ASCII 片段, 不匹配中文(免编码差异导致静默零命中)。
function Read-PngSummary([string]$Text) {
    $m = [regex]::Match($Text, 'PNGSTRUCT RESULT: pass=(\d+) fail=(\d+)')
    $has = $m.Success
    $pN = -1; $fN = -1
    if ($has) { $pN = [int]$m.Groups[1].Value; $fN = [int]$m.Groups[2].Value }
    return @{ HasSummary = $has; Pass = $pN; Fail = $fN }
}

# 从 `  PASS <id> ...` / `  FAIL <id> ...` 行提取**断言名集合**(前缀 + 首 token, 非子串匹配)。
function Get-AssertIdsFromText([string]$Text, [string]$Status) {
    $ids = @()
    $needle = '  ' + $Status + ' '
    foreach ($ln in ($Text -split "`r?`n")) {
        if ($ln.StartsWith($needle)) {
            $rest = $ln.Substring($needle.Length).Trim()
            if ($rest -ne '') { $ids += (($rest -split ' ')[0]) }
        }
    }
    # 同上: 不用 `,$ids`, 否则调用方拿到的是"装着数组的单元素数组"
    return $ids
}

function Read-TextFile([string]$Path) {
    $t = ''
    if (Test-Path -LiteralPath $Path) {
        try { $t = [System.IO.File]::ReadAllText($Path) } catch { $t = '' }
    }
    return $t
}

# ===========================================================================
# PNG 造样助手(负控用)。**独立实现** CRC-32/IEEE, 不调库里的内部助手:
#   => 造样用的 CRC 与库校验用的 CRC 是**两套独立实现**, 二者不一致会被
#      `CrcFailChunk -ne $null` 抓住(B1.5..B1.8 的断言正是"CRC 必须过, 语义必须拦")。
# ⚠ 十六进制字面量在 PS 里按 **Int32 有符号**解析(`0xEDB88320` = Int32 负数) =>
#   必须用十进制常量强制 Int64(库文件里有同一坑的记录)。
# ===========================================================================
$script:GateMask = [long]4294967295
$script:GatePoly = [long]3988292384
$script:GateTable = $null

function Get-GateCrcTable {
    if ($null -ne $script:GateTable) { return $script:GateTable }
    $mask = $script:GateMask
    $poly = $script:GatePoly
    $t = New-Object 'long[]' 256
    for ($n = 0; $n -lt 256; $n++) {
        $c = [long]$n
        for ($k = 0; $k -lt 8; $k++) {
            if (($c -band 1) -ne 0) { $c = ($poly -bxor ($c -shr 1)) -band $mask }
            else { $c = $c -shr 1 }
        }
        $t[$n] = $c
    }
    $script:GateTable = $t
    return $t
}

function Get-GateCrc32 {
    param([byte[]]$Bytes, [int]$Start, [int]$Count)
    $mask = $script:GateMask
    $t = Get-GateCrcTable
    $c = $mask
    for ($i = 0; $i -lt $Count; $i++) {
        $idx = [int](($c -bxor [long]$Bytes[$Start + $i]) -band 0xFF)
        $c = ($t[$idx] -bxor ($c -shr 8)) -band $mask
    }
    return (($c -bxor $mask) -band $mask)
}

function Get-BE32([long]$v) {
    $b = New-Object 'byte[]' 4
    $b[0] = [byte](($v -shr 24) -band 0xFF)
    $b[1] = [byte](($v -shr 16) -band 0xFF)
    $b[2] = [byte](($v -shr 8) -band 0xFF)
    $b[3] = [byte]($v -band 0xFF)
    return ,$b
}

# 把 PNG 读成有序 chunk 列表: @{ Type; Data(byte[]); ForceCrc(可覆盖写出的 CRC 字段) }
function Read-PngChunkList([byte[]]$Bytes) {
    $list = New-Object System.Collections.ArrayList
    if ($Bytes.Length -lt 8) { return ,$list }
    $pos = 8
    while (($pos + 8) -le $Bytes.Length) {
        $cl = [long]$Bytes[$pos] * 16777216 + [long]$Bytes[$pos + 1] * 65536 + [long]$Bytes[$pos + 2] * 256 + [long]$Bytes[$pos + 3]
        $t = [System.Text.Encoding]::ASCII.GetString($Bytes, [int]($pos + 4), 4)
        $data = New-Object 'byte[]' ([int]$cl)
        [Array]::Copy($Bytes, [int]($pos + 8), $data, 0, [int]$cl)
        [void]$list.Add([pscustomobject]@{ Type = $t; Data = $data; ForceCrc = $null })
        if ($t -eq 'IEND') { break }
        $pos = $pos + 8 + $cl + 4
    }
    return ,$list
}

function Copy-PngChunkList($Chunks) {
    $out = New-Object System.Collections.ArrayList
    foreach ($c in $Chunks) {
        [void]$out.Add([pscustomobject]@{ Type = $c.Type; Data = $c.Data.Clone(); ForceCrc = $null })
    }
    return ,$out
}

# 序列化: 签名 + 逐 chunk(length + type + data + crc)。ForceCrc 非 $null 时用它覆盖真 CRC。
function Write-PngChunkList {
    param([string]$Path, $Chunks, [byte[]]$Trailing)
    $ms = New-Object System.IO.MemoryStream
    $sig = New-Object 'byte[]' 8
    $sig[0] = 0x89; $sig[1] = 0x50; $sig[2] = 0x4E; $sig[3] = 0x47
    $sig[4] = 0x0D; $sig[5] = 0x0A; $sig[6] = 0x1A; $sig[7] = 0x0A
    $ms.Write($sig, 0, 8)
    foreach ($c in $Chunks) {
        $tb = [System.Text.Encoding]::ASCII.GetBytes($c.Type)
        $buf = New-Object 'byte[]' (4 + $c.Data.Length)
        [Array]::Copy($tb, 0, $buf, 0, 4)
        [Array]::Copy($c.Data, 0, $buf, 4, $c.Data.Length)
        $crc = Get-GateCrc32 -Bytes $buf -Start 0 -Count $buf.Length
        if ($null -ne $c.ForceCrc) { $crc = [long]$c.ForceCrc }
        $lb = Get-BE32 ([long]$c.Data.Length)
        $ms.Write($lb, 0, 4)
        $ms.Write($tb, 0, 4)
        $ms.Write($c.Data, 0, $c.Data.Length)
        $cb = Get-BE32 $crc
        $ms.Write($cb, 0, 4)
    }
    if ($null -ne $Trailing) { $ms.Write($Trailing, 0, $Trailing.Length) }
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $ms.Dispose()
}

function Get-ChunkCrc($Chunk) {
    $tb = [System.Text.Encoding]::ASCII.GetBytes($Chunk.Type)
    $buf = New-Object 'byte[]' (4 + $Chunk.Data.Length)
    [Array]::Copy($tb, 0, $buf, 0, 4)
    [Array]::Copy($Chunk.Data, 0, $buf, 4, $Chunk.Data.Length)
    return (Get-GateCrc32 -Bytes $buf -Start 0 -Count $buf.Length)
}

function Get-ChunkByName($Chunks, [string]$Type) {
    foreach ($c in $Chunks) { if ($c.Type -eq $Type) { return $c } }
    return $null
}

# ===========================================================================
# 子进程体: 真的跑断言, 打 `  PASS/FAIL <id> ...` 行 + 末行汇总。
# ===========================================================================
if ($Child) {
    # 运行级 GUID 临时目录(固定目录会被并发同名实例互删 => 假红)
    $workC = Join-Path ([System.IO.Path]::GetTempPath()) ('pngstruct_c_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workC -Force | Out-Null
    $sampleDir = Join-Path $workC 'samples'
    New-Item -ItemType Directory -Path $sampleDir -Force | Out-Null
    Write-Output "child work dir = $workC"

    # 唯一的库依赖: dot-source oracle(纯字节读, 不起外部进程)
    . (Join-Path $scriptDir '_lib-oracle.ps1')

    # ---------------- A. 正控: 真实素材 ----------------
    $srcDir = Join-Path $repoRoot 'tests/output/sources'
    $pngFiles = @()
    if (Test-Path -LiteralPath $srcDir -PathType Container) {
        $pngFiles = @(Get-ChildItem -LiteralPath $srcDir -Filter '*.png' -File -ErrorAction SilentlyContinue | Sort-Object Name)
    }
    Write-Output "  [info] png files = $($pngFiles.Count) (min required $MinPng)"

    CK (Test-Path -LiteralPath $srcDir -PathType Container) "A1.1 sources dir exists: $srcDir"
    CK ($pngFiles.Count -ge $MinPng) "A1.2 png corpus count >= $MinPng (actual $($pngFiles.Count))"

    $nOk = 0; $nCrc = 0; $nTrail = 0; $nSig = 0
    $idx = 0
    foreach ($f in $pngFiles) {
        $idx++
        $r = Test-PngStructure -Path $f.FullName
        if ($r.Ok -eq $true) { $nOk++ }
        if ($r.CrcChecked -eq $r.ChunkCount) { $nCrc++ }
        if ($r.TrailingBytes -eq 0) { $nTrail++ }
        if ($r.HasSignature -and $r.HasIhdr -and $r.HasIend) { $nSig++ }
        $good = ($r.Ok -eq $true) -and ($r.CrcChecked -eq $r.ChunkCount) -and ($r.TrailingBytes -eq 0)
        CK $good ("A2.{0} {1} Ok={2} crc={3}/{4} trailing={5} err='{6}'" -f `
            $idx, $f.Name, $r.Ok, $r.CrcChecked, $r.ChunkCount, $r.TrailingBytes, $r.Error)
    }
    $script:perFileIds = $idx

    # 聚合断言: 逐文件那几条若被删掉/改名, 这里仍能给出可读的读数
    # ⚠ 每条都带 `-and ($pngFiles.Count -ge $MinPng)`, 否则语料为空时 `0 -eq 0` 会**真空成立**
    $nonEmpty = ($pngFiles.Count -ge $MinPng)
    CK ($nonEmpty -and ($nOk -eq $pngFiles.Count)) "A1.3 all corpus png Ok=True ($nOk/$($pngFiles.Count))"
    CK ($nonEmpty -and ($nCrc -eq $pngFiles.Count)) "A1.4 all corpus png CrcChecked == ChunkCount ($nCrc/$($pngFiles.Count))"
    CK ($nonEmpty -and ($nTrail -eq $pngFiles.Count)) "A1.5 all corpus png TrailingBytes == 0 ($nTrail/$($pngFiles.Count))"
    CK ($nonEmpty -and ($nSig -eq $pngFiles.Count)) "A1.6 all corpus png signature+IHDR+IEND present ($nSig/$($pngFiles.Count))"

    # ---------------- 负控的基座素材 ----------------
    # 取**最小的**带 IDAT 的合规 PNG 做基座(逐样本只改一处, 差异可控)
    $basePath = ''
    foreach ($f in (@($pngFiles) | Sort-Object Length)) {
        $rb = Test-PngStructure -Path $f.FullName
        if (($rb.Ok -eq $true) -and (@($rb.ChunkTypes) -contains 'IDAT')) { $basePath = $f.FullName; break }
    }
    CK ($basePath -ne '') "B0.1 base png with IDAT selected: $basePath"

    $base = $null
    if ($basePath -ne '') {
        try { $base = Read-PngChunkList ([System.IO.File]::ReadAllBytes($basePath)) } catch { $base = $null }
    }

    # 负控判定助手: 必须 Ok=$false 且 Error 含期望片段(片段检查防"文件没造出来 => Ok=False"真空通过)
    # ⚠ 本函数**不返回**任何值: 它只把 CK 的 PASS/FAIL 行写到输出流。
    #   若它 `return $r`, 调用方一旦写 `| Out-Null` 或 `$x = fn`, CK 的 Write-Output 会被
    #   一起吞掉 => 断言**执行了但没进 stdout** => 父进程的锚点在册检查报"缺失"(本轮实测踩过:
    #   B1.1/B1.2/B1.3/B1.5..B1.8 的 PASS 行全部消失)。所以调用点一律**裸语句**。
    function Check-Neg {
        param([string]$Id, [string]$Path, [string]$Sub, [string]$ExpectCrcChunk, [bool]$RequireNoCrcFail)
        $r = Test-PngStructure -Path $Path
        $ok = ($r.Ok -eq $false) -and ($r.Error.Contains($Sub))
        if ($ExpectCrcChunk -ne '') { $ok = $ok -and ($r.CrcFailChunk -eq $ExpectCrcChunk) }
        if ($RequireNoCrcFail) { $ok = $ok -and ($null -eq $r.CrcFailChunk) }
        CK $ok ("{0} {1} Ok={2} crcFail='{3}' err='{4}'" -f $Id, (Split-Path -Leaf $Path), $r.Ok, $r.CrcFailChunk, $r.Error)
    }

    if ($null -eq $base) {
        foreach ($id in @('B2.1', 'B1.1', 'B1.2', 'B1.3', 'B1.4', 'B1.5', 'B1.6', 'B1.7', 'B1.8')) {
            CK $false "$id SKIPPED: no usable base png (basePath='$basePath')"
        }
    } else {
        # ---- B2.1 反真空: 未改动的重建必须**仍然合法**(证明造样器本身没坏) ----
        # ⚠ 没有这一条, B1.5..B1.8 的"被拦"可能只是因为造样器把文件写坏了(错误的理由通过)
        $rt = Join-Path $sampleDir 'rt_same.png'
        Write-PngChunkList -Path $rt -Chunks (Copy-PngChunkList $base)
        $rrt = Test-PngStructure -Path $rt
        CK (($rrt.Ok -eq $true) -and ($rrt.CrcChecked -eq $rrt.ChunkCount) -and ($rrt.ChunkCount -eq $base.Count) -and ($rrt.TrailingBytes -eq 0)) `
           ("B2.1 rebuild round-trip stays legal: Ok={0} chunks={1}/{2} crc={3} trailing={4}" -f `
            $rrt.Ok, $rrt.ChunkCount, $base.Count, $rrt.CrcChecked, $rrt.TrailingBytes)

        # ---- B1.1 翻转 IDAT 数据字节 => CRC 必须抓住, 且点名 IDAT ----
        # ⚠⚠ 实测坑(本轮第一版就栽在这里): `Write-PngChunkList` **总是重算 CRC** =>
        #   只翻数据字节会写出一个「CRC 正确、载荷已坏」的文件, 库判 Ok=True
        #   (容器层面确实合规 —— 它不解压 zlib)。那不是 CRC 负控。
        #   => 必须**保留原 CRC 字段**(ForceCrc = 改动前的 CRC), 才是"数据被改而 CRC 未更新"
        #   这一真实损坏形态。
        $c = Copy-PngChunkList $base
        $idat = Get-ChunkByName $c 'IDAT'
        $staleCrc = Get-ChunkCrc $idat
        $idat.Data[0] = [byte]($idat.Data[0] -bxor 0xFF)
        $idat.ForceCrc = $staleCrc
        $p1 = Join-Path $sampleDir 'neg_idat_flip.png'
        Write-PngChunkList -Path $p1 -Chunks $c
        Check-Neg 'B1.1' $p1 'CRC mismatch' 'IDAT' $false

        # ---- B1.2 翻转 IDAT 的 CRC 字段 => 必须抓住 ----
        $c = Copy-PngChunkList $base
        $idat = Get-ChunkByName $c 'IDAT'
        $idat.ForceCrc = ((Get-ChunkCrc $idat) -bxor 1)
        $p2 = Join-Path $sampleDir 'neg_crc_flip.png'
        Write-PngChunkList -Path $p2 -Chunks $c
        Check-Neg 'B1.2' $p2 'CRC mismatch' 'IDAT' $false

        # ---- B1.3 整块删掉 IEND => 必须抓住 ----
        $c = New-Object System.Collections.ArrayList
        foreach ($x in (Copy-PngChunkList $base)) { if ($x.Type -ne 'IEND') { [void]$c.Add($x) } }
        $p3 = Join-Path $sampleDir 'neg_iend_missing.png'
        Write-PngChunkList -Path $p3 -Chunks $c
        Check-Neg 'B1.3' $p3 'IEND not found' '' $false

        # ---- B1.4 IEND 后追加垃圾字节 => 必须抓住, 且 TrailingBytes 读数正确 ----
        $p4 = Join-Path $sampleDir 'neg_trailing.png'
        Write-PngChunkList -Path $p4 -Chunks (Copy-PngChunkList $base) -Trailing ([byte[]]@(0xDE, 0xAD, 0xBE, 0xEF))
        $r4 = Test-PngStructure -Path $p4
        CK (($r4.Ok -eq $false) -and ($r4.TrailingBytes -eq 4) -and ($r4.Error.Contains('trailing byte'))) `
           ("B1.4 neg_trailing.png Ok={0} trailing={1} err='{2}'" -f $r4.Ok, $r4.TrailingBytes, $r4.Error)

        # ---- B1.5 非法 ColorType(**重算 CRC 使其合法**) => CRC 过、语义拦 ----
        # ⚠ 这正是"CRC 之外还有语义校验"的交叉验证: RequireNoCrcFail 要求 CrcFailChunk 为 $null
        $c = Copy-PngChunkList $base
        (Get-ChunkByName $c 'IHDR').Data[9] = [byte]7
        $p5 = Join-Path $sampleDir 'neg_colortype.png'
        Write-PngChunkList -Path $p5 -Chunks $c
        Check-Neg 'B1.5' $p5 'color type' '' $true

        # ---- B1.6 非法 Interlace(重算 CRC) ----
        $c = Copy-PngChunkList $base
        (Get-ChunkByName $c 'IHDR').Data[12] = [byte]2
        $p6 = Join-Path $sampleDir 'neg_interlace.png'
        Write-PngChunkList -Path $p6 -Chunks $c
        Check-Neg 'B1.6' $p6 'interlace' '' $true

        # ---- B1.7 非法 BitDepth x ColorType 组合(重算 CRC) ----
        $c = Copy-PngChunkList $base
        (Get-ChunkByName $c 'IHDR').Data[8] = [byte]3
        $p7 = Join-Path $sampleDir 'neg_bitdepth.png'
        Write-PngChunkList -Path $p7 -Chunks $c
        Check-Neg 'B1.7' $p7 'bit depth' '' $true

        # ---- B1.8 IHDR 长度 != 13(多塞一字节, 重算 CRC) ----
        $c = Copy-PngChunkList $base
        $ih = Get-ChunkByName $c 'IHDR'
        $d14 = New-Object 'byte[]' 14
        [Array]::Copy($ih.Data, 0, $d14, 0, 13)
        $d14[13] = 0x00
        $ih.Data = $d14
        $p8 = Join-Path $sampleDir 'neg_ihdr_len.png'
        Write-PngChunkList -Path $p8 -Chunks $c
        Check-Neg 'B1.8' $p8 'IHDR length' '' $true
    }

    # ---------------- B3. CRC 绕过的交叉验证(在**临时副本**上变异, 不动库本体) ----------------
    # 目的: 证明「B1.1/B1.2 是被 CRC 校验抓住的」而不是被别的什么顺手抓住;
    #       同时证明「B1.3/B1.5 与 CRC 无关, 语义校验独立成立」。
    # 手段: 把副本里的 CRC 比较行改成 `if ($false)`, 在**独立子进程**里 dot-source 该副本
    #       (不污染本进程已加载的函数), 再对同一批样本重跑。
    $libPath = Join-Path $scriptDir '_lib-oracle.ps1'
    $mutDir = Join-Path $workC 'mut'
    New-Item -ItemType Directory -Path $mutDir -Force | Out-Null
    $mutLib = Join-Path $mutDir '_lib-oracle-mut.ps1'
    $anchor = 'if ($calc -ne $stored) {'
    $repl = 'if ($false) {'
    $hit = 0
    try { $hit = ([regex]::Matches([System.IO.File]::ReadAllText($libPath), [regex]::Escape($anchor))).Count } catch { $hit = 0 }
    CK ($hit -eq 1) "B3.1 mutation anchor occurs exactly once in oracle lib (actual $hit)"

    if ($hit -eq 1) {
        $mutTxt = [System.IO.File]::ReadAllText($libPath).Replace($anchor, $repl)
        [System.IO.File]::WriteAllText($mutLib, $mutTxt, (New-Object System.Text.UTF8Encoding($true)))

        $probeTpl = @'
$ErrorActionPreference = 'Continue'
. '__MUTLIB__'
$d = '__SAMPLEDIR__'
$r1 = Test-PngStructure -Path (Join-Path $d 'neg_idat_flip.png')
$r2 = Test-PngStructure -Path (Join-Path $d 'neg_crc_flip.png')
$r3 = Test-PngStructure -Path (Join-Path $d 'neg_iend_missing.png')
$r4 = Test-PngStructure -Path (Join-Path $d 'neg_colortype.png')
Write-Output ("MUTPROBE idat_ok=" + $r1.Ok + " crc_ok=" + $r2.Ok + " iend_ok=" + $r3.Ok + " ct_ok=" + $r4.Ok)
'@
        $probePath = Join-Path $mutDir 'probe.ps1'
        $probeTxt = $probeTpl.Replace('__MUTLIB__', $mutLib).Replace('__SAMPLEDIR__', $sampleDir)
        [System.IO.File]::WriteAllText($probePath, $probeTxt, (New-Object System.Text.UTF8Encoding($true)))

        $hostExeC = (Get-Process -Id $PID).Path
        $pr = Invoke-PsProc -HostExe $hostExeC -PsArgs @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $probePath) `
                -TimeoutMs ($TimeoutSec * 1000) -EnvOverride @{} -WorkDir $workC -Tag 'mut'
        $mutOut = Read-TextFile $pr.OutFile
        $mm = [regex]::Match($mutOut, 'MUTPROBE idat_ok=(\w+) crc_ok=(\w+) iend_ok=(\w+) ct_ok=(\w+)')
        if ($mm.Success) {
            $idatOk = ($mm.Groups[1].Value -eq 'True')
            $crcOk  = ($mm.Groups[2].Value -eq 'True')
            $iendOk = ($mm.Groups[3].Value -eq 'True')
            $ctOk   = ($mm.Groups[4].Value -eq 'True')
            CK ($idatOk -and $crcOk) ("B3.2 CRC bypass => idat-flip and crc-flip are MISSED (Ok=True) so the CRC check is load-bearing (idat={0} crc={1})" -f $idatOk, $crcOk)
            CK ((-not $iendOk) -and (-not $ctOk)) ("B3.3 CRC bypass => IEND-missing and bad-colortype are STILL caught so semantic checks are CRC-independent (iend={0} ct={1})" -f $iendOk, $ctOk)
        } else {
            CK $false ("B3.2 CRC-bypass probe produced no MUTPROBE line (exit={0} timedOut={1})" -f $pr.ExitCode, $pr.TimedOut)
            CK $false ("B3.3 CRC-bypass probe produced no MUTPROBE line (exit={0} timedOut={1})" -f $pr.ExitCode, $pr.TimedOut)
        }
    } else {
        CK $false "B3.2 mutation not applied (anchor count $hit) => cannot cross-verify"
        CK $false "B3.3 mutation not applied (anchor count $hit) => cannot cross-verify"
    }

    # ---------------- C. 自清临时目录 ----------------
    Remove-Item -Recurse -Force $workC -ErrorAction SilentlyContinue
    CK (-not (Test-Path -LiteralPath $workC)) "C1.1 child temp dir removed: $workC"

    # ---------------- D. 集合自检(按**实际产出的断言名集合**比对) ----------------
    # ⚠ 这里**不做**寄存器比对: D 块自己的 id 要等 D 块跑完才入册, 放在这里会自我循环
    #   (本轮实测: D1.1 报 "missing: D1.1,D1.2,D1.3")。寄存器比对统一放到最后一条 E1.1。
    $actual = @($script:ids)
    CK (($script:perFileIds -eq $pngFiles.Count) -and ($pngFiles.Count -ge $MinPng)) `
       "D1.1 per-file assertion count == png count ($($script:perFileIds)/$($pngFiles.Count))"
    $bIds = @($actual | Where-Object { $_ -like 'B*' })
    CK ($bIds.Count -ge 13) "D1.2 negative-control anchor ids >= 13 (actual $($bIds.Count))"
    $uniq = @($actual | Sort-Object -Unique)
    CK ($uniq.Count -eq $actual.Count) "D1.3 assertion ids are unique ($($actual.Count) produced, $($uniq.Count) distinct)"

    # ---------------- E. 锚点在册(放最后: 此时 A/B/C/D 的 id 全部已入册) ----------------
    $req = @(Get-RequiredAnchorIds)
    $actualE = @($script:ids)
    $missE = @()
    foreach ($a in $req) { if ($actualE -notcontains $a) { $missE += $a } }
    CK ($missE.Count -eq 0) "E1.1 anchor register fully covered by produced assertion ids (missing: $($missE -join ','))"
    CK ($actualE.Count -ge 28) "E1.2 total assertion count >= 28 (actual $($actualE.Count))"

    Write-Output ""
    Write-Output ("  [info] child assertions = {0} pass={1} fail={2}" -f $script:ids.Count, $pass, $fail)
    Write-Output ("PNGSTRUCT RESULT: pass={0} fail={1}" -f $pass, $fail)
    # 退出码经**文件**回传: 父进程可能是 PS 5.1, 那里 `.ExitCode` 恒 $null(见文件头实测)
    $code = $(if ($fail -eq 0) { 0 } else { 1 })
    if ($RcFile -ne '') {
        try { [System.IO.File]::WriteAllText($RcFile, [string]$code) } catch { }
    }
    exit $code
}

# ===========================================================================
# 父进程体: 起子进程(硬超时) -> 读它的 stdout 制品 -> 判 退出码 + 汇总行 + FAIL + 锚点在册。
# ===========================================================================
$hostExe = ''
try { $hostExe = (Get-Process -Id $PID).Path } catch { $hostExe = '' }

Write-Output "repo root = $repoRoot"
Write-Output "host exe  = $hostExe"
Write-Output "script    = $PSCommandPath"

$srcDir = Join-Path $repoRoot 'tests/output/sources'
if (-not (Test-Path -LiteralPath $srcDir -PathType Container)) {
    Write-Output "  FAIL sources dir exists: $srcDir"
    Write-Output "PNGSTRUCT RESULT: pass=0 fail=1"
    exit 1
}
CK $true "P1.1 sources dir exists: $srcDir"
CK ($hostExe -ne '') "P1.2 host exe resolvable: $hostExe"

# 运行级 GUID 临时目录(父进程自己的制品目录)
$workP = Join-Path ([System.IO.Path]::GetTempPath()) ('pngstruct_p_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workP -Force | Out-Null

$rcMain = Join-Path $workP 'rc_main.txt'
$childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Child',
               '-MinPng', [string]$MinPng, '-TimeoutSec', [string]$TimeoutSec, '-RcFile', $rcMain)

Write-Output ""
Write-Output "===== run: child assertions (timeout=${TimeoutSec}s) ====="
$sw = [System.Diagnostics.Stopwatch]::StartNew()
# 显式把 NEGCTL 置空: 免得外层环境里恰好设了它 => 正常跑也转红
$r = Invoke-PsProc -HostExe $hostExe -PsArgs $childArgs -TimeoutMs ($TimeoutSec * 1000) `
        -EnvOverride @{ PNGSTRUCT_NEGCTL = '' } -WorkDir $workP -Tag 'main' -RcFile $rcMain
$sw.Stop()
$elapsed = [int]$sw.Elapsed.TotalSeconds

if ($r.StartError -ne '') {
    CK $false "P1.3 child started (start error: $($r.StartError))"
} else {
    CK $true "P1.3 child started"
}
CK (-not $r.TimedOut) "P1.4 no timeout (elapsed ${elapsed}s, limit ${TimeoutSec}s)"

# 退出码双通道互相印证: PS 7 两条都有且必须相等; PS 5.1 OS 通道恒 $null => 以 rc 文件为准
if ($r.TimedOut) {
    CK $false "P1.5 exit-code channels (timed out => no exit code)"
} else {
    $osV = $r.OsExitCode; $rcV = $r.RcExitCode
    $chOk = $false
    if (($null -ne $osV) -and ($null -ne $rcV)) { if ($osV -eq $rcV) { $chOk = $true } }
    elseif (($null -eq $osV) -and ($null -ne $rcV)) { $chOk = $true }
    CK $chOk "P1.5 exit-code channels agree (os=$osV rc=$rcV)"
}

$rawChild = Read-TextFile $r.OutFile
$childText = $rawChild
# ⚠ 超时击杀后**抑制汇总解析**: 被杀的进程留下半截输出, 若其中恰有汇总行
#   => 「挂死」会被当成「跑完了且干净」。
if ($r.TimedOut) {
    $tail = ''
    if (Test-Path -LiteralPath $r.OutFile) { $tail = ((Get-Content -LiteralPath $r.OutFile -Tail 5 -ErrorAction SilentlyContinue) -join "`n") }
    $childText = "[TIMEOUT] summary parsing suppressed; tail:`n$tail"
}

# 子进程临时目录兜底清理: 正常结束时它自己删(断言 C1.1); **被超时击杀时来不及删** =>
# 父进程按它开头打印的 `child work dir = <path>` 兜底删。
# ⚠ 只认 temp 根下的 `pngstruct_c_*` 前缀, 绝不对其它路径动手(实测: -TimeoutSec 0 那次就漏了一个)。
$childWorkDir = ''
$mmW = [regex]::Match($rawChild, 'child work dir = (.+)')
if ($mmW.Success) { $childWorkDir = $mmW.Groups[1].Value.Trim() }
if ($childWorkDir -ne '') {
    $tmpRoot = [System.IO.Path]::GetTempPath()
    if ($childWorkDir.StartsWith($tmpRoot) -and ((Split-Path -Leaf $childWorkDir).StartsWith('pngstruct_c_'))) {
        Remove-Item -Recurse -Force $childWorkDir -ErrorAction SilentlyContinue
    }
}
CK (($childWorkDir -eq '') -or (-not (Test-Path -LiteralPath $childWorkDir))) `
   "P1.6 child work dir cleaned up (parent safety net; path='$childWorkDir')"

Write-Output "----- child stdout -----"
if ($childText -ne '') { Write-Output $childText } else { Write-Output "(empty)" }
$errText = Read-TextFile $r.ErrFile
if ($errText.Trim() -ne '') { Write-Output "----- child stderr -----"; Write-Output $errText }
Write-Output "----- end child -----"

$s = Read-PngSummary $childText
CK $s.HasSummary "P2.1 summary line present (exit 0 alone is not enough)"
CK ($s.HasSummary -and ($s.Fail -eq 0)) "P2.2 summary FAIL=0"
CK ($r.ExitCode -eq 0) "P2.3 child exit code == 0 (actual $($r.ExitCode))"

# 锚点在册: required 集合 含于 子进程**实际 PASS** 的断言名集合
$req = @(Get-RequiredAnchorIds)
$passIds = @(Get-AssertIdsFromText $childText 'PASS')
$failIds = @(Get-AssertIdsFromText $childText 'FAIL')
$miss = @()
foreach ($a in $req) { if ($passIds -notcontains $a) { $miss += $a } }
CK ($miss.Count -eq 0) "P3.1 all required anchor ids PASS in child stdout (missing: $($miss -join ','))"
CK ($failIds.Count -eq 0) "P3.2 no FAIL assertion ids in child stdout (actual $($failIds.Count): $($failIds -join ','))"
CK ($passIds.Count -ge 28) "P3.3 child PASS assertion count >= 28 (actual $($passIds.Count))"
$perFileIds = @($passIds | Where-Object { $_ -like 'A2.*' })
CK ($perFileIds.Count -ge $MinPng) "P3.4 per-file anchors >= $MinPng (actual $($perFileIds.Count))"

# ---------------- 自检负控(-SelfCheck): 证明判读链有牙 ----------------
# ⚠ 语义: `-SelfCheck` = **期望本门禁转红**。$ncOk 只表示"注入的故障确实把子进程弄红了"
#   (= 判读链有牙); 最终 `$ok` 在 SelfCheck 下被强制为 $false(见判据段)。
$ncOk = $true
if ($SelfCheck) {
    Write-Output ""
    Write-Output "===== selfcheck: PNGSTRUCT_NEGCTL=1 must turn RED ====="
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $rcNc = Join-Path $workP 'rc_negctl.txt'
    $ncArgs = $childArgs
    # 把 -RcFile 指向本轮的独立 rc 文件(不能复用主跑的, 否则 rc 会沿用上一轮 = 假绿)
    $ncArgs = @()
    foreach ($a in $childArgs) { $ncArgs += $a }
    for ($i = 0; $i -lt $ncArgs.Count; $i++) { if ($ncArgs[$i] -eq '-RcFile') { $ncArgs[$i + 1] = $rcNc } }
    $r2 = Invoke-PsProc -HostExe $hostExe -PsArgs $ncArgs -TimeoutMs ($TimeoutSec * 1000) `
            -EnvOverride @{ PNGSTRUCT_NEGCTL = '1' } -WorkDir $workP -Tag 'negctl' -RcFile $rcNc
    $sw2.Stop()
    $t2 = Read-TextFile $r2.OutFile
    $s2 = Read-PngSummary $t2
    $miss2 = @(Get-AssertIdsFromText $t2 'FAIL')
    Write-Output ("  [selfcheck] exit={0} timedOut={1} summary={2} pass={3} fail={4} failIds=[{5}] elapsed={6}s" -f `
        $r2.ExitCode, $r2.TimedOut, $s2.HasSummary, $s2.Pass, $s2.Fail, ($miss2 -join ','), [int]$sw2.Elapsed.TotalSeconds)
    CK ((-not $r2.TimedOut) -and $s2.HasSummary -and ($s2.Fail -ge 1) -and ($r2.ExitCode -ne 0)) `
       "P4.1 negative-control switch => summary shows FAIL and exit code != 0"
    CK (@($miss2) -contains 'E1.1') "P4.2 the red one is the anchor-register check E1.1 (failIds=[$($miss2 -join ',')])"
    $ncOk = ((-not $r2.TimedOut) -and $s2.HasSummary -and ($s2.Fail -ge 1) -and ($r2.ExitCode -ne 0))
}

# ---------------- 清理 + 判据 ----------------
Remove-Item -Recurse -Force $workP -ErrorAction SilentlyContinue
CK (-not (Test-Path -LiteralPath $workP)) "P5.1 parent temp dir removed: $workP"

# 父进程自己的锚点册。⚠ 不含 P5.2(本条检查自身, 放进来会自我循环, 与 D 块同因) 与
#   P4.1/P4.2(只在 `-SelfCheck` 下产出)。
$parentReq = @('P1.1', 'P1.2', 'P1.3', 'P1.4', 'P1.5', 'P1.6', 'P2.1', 'P2.2', 'P2.3', 'P3.1', 'P3.2', 'P3.3', 'P3.4', 'P5.1')
$parentIds = @($script:ids)
$pMiss = @()
foreach ($a in $parentReq) { if ($parentIds -notcontains $a) { $pMiss += $a } }
CK ($pMiss.Count -eq 0) "P5.2 parent anchor register covered (missing: $($pMiss -join ','))"

$ok = ($fail -eq 0) -and (-not $r.TimedOut) -and $s.HasSummary -and ($s.Fail -eq 0) -and ($r.ExitCode -eq 0) -and $ncOk
# -SelfCheck 的语义 = 期望转红 => 强制非绿(转红本身才是通过)
if ($SelfCheck) { $ok = $false }

Write-Output ""
Write-Output ("  [info] child exit={0} timedOut={1} summary={2} pass={3} fail={4} anchors={5} elapsed={6}s" -f `
    $r.ExitCode, $r.TimedOut, $s.HasSummary, $s.Pass, $s.Fail, $passIds.Count, $elapsed)

if ($ok) {
    Write-Output "PNGSTRUCT RESULT: pass=$($s.Pass) fail=0"
    exit 0
} else {
    $reason = 'unknown'
    if ($SelfCheck -and $ncOk) { $reason = 'selfcheck expected red: injected anchor fault E1.1 was caught (exit 1 is the expected result)' }
    elseif ($SelfCheck) { $reason = 'selfcheck negative control did NOT turn the child red => judgement chain is broken' }
    elseif ($r.TimedOut) { $reason = "timeout ${TimeoutSec}s" }
    elseif (-not $s.HasSummary) { $reason = 'summary line missing' }
    elseif ($r.ExitCode -ne 0) { $reason = "child exit code $($r.ExitCode)" }
    elseif ($s.Fail -ne 0) { $reason = "child FAIL=$($s.Fail)" }
    elseif (-not $ncOk) { $reason = 'selfcheck negative control did not turn red' }
    elseif ($fail -ne 0) { $reason = "local checks failed ($fail)" }
    Write-Output "PNGSTRUCT FAIL: $reason"
    Write-Output "PNGSTRUCT RESULT: pass=$($s.Pass) fail=$($s.Fail)"
    exit 1
}
