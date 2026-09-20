# verify-ps-compat.ps1 —— 门禁脚本自身的**宿主兼容性**门禁（2026-09-16 新增）
#
# 背景（为什么必须有这一条）：
#   本项目所有门禁都是 `.ps1`，由 `_run-step3-gates.ps1` 用**当前宿主**的 PowerShell 启动
#   （`$psExe = (Get-Process -Id $PID).Path`）。
#   ⚠ 2026-09-16 更正：旧注释称「本机 `pwsh` 不在 PATH（`Get-Command pwsh` ⇒ NOTFOUND）」，
#   该结论**已过期** —— 实测 `Get-Command pwsh` 命中 `C:\Program Files\PowerShell\7\pwsh.exe`（7.6.6），
#   且 `powershell.exe`（Windows PowerShell **5.1**）同样可用。
#   ⇒ 真实约束是：**子脚本宿主取决于运行器是怎么启动的**（pwsh ⇒ PS 7；powershell.exe ⇒ PS 5.1）。
#   ⇒ 所以脚本必须**双宿主兼容**；只在一个宿主下取得的 `PASS=n` 不可当作基线。
#
#   由此产生一类**静默失效**缺陷：脚本里用了 PowerShell 7+ 专属**语法**（三元 `? :`、
#   空合并 `??`、空条件 `?.`、管道链 `&&`/`||`），在 5.1 下是**解析错误** ⇒
#   整个脚本一行都不执行、不产出任何 PASS/FAIL 行、也不写日志。
#   而运行器只按 `Start-Process` 的退出码与输出里的汇总行判读 ⇒ 表现为"门禁没输出"，
#   极易被当成"这条门禁没内容"而忽略。**门禁跑不起来 = 假绿**，比断言失败更危险。
#
#   另一个同源陷阱：PowerShell 把**全角弯引号** `“ ”`（U+201C/U+201D）也当作字符串定界符。
#   于是普通双引号串里写 `"……“内层引号”……"` 会**提前闭合字符串** ⇒ 同样解析失败。
#   本项目文档大量使用弯引号，复制到脚本里就会踩。
#
# 实测战果（本脚本诞生时即抓到 2 处真缺陷）：
#   ① `verify-color-token-normalize.ps1:215` 用了 `($_ -eq 'gbr' ? 'rgb' : $_)`
#      ⇒ 该门禁在回退宿主下**从未真正跑过**（此前记录的 33/0 是在 PS7 宿主下取得的）。
#   ② `_probe-icc-affects-decode.ps1:34,35` 在双引号串里用了 `“…”`
#      ⇒ 该诊断脚本**完全无法解析**。
#
# 断言：
#   0. 语料自检：扫到的脚本数必须 ≥ 阈值（防"扫了个空目录"式假绿）；
#   1. 解析：每个脚本都能被**当前宿主**的解析器解析（无 ParserError）；
#   2. 语法：剥离字符串/注释后，正文里**不得**出现 PS7+ 专属语法；
#   3. 负控（有牙）：临时构造的"含三元/含 `??`/含弯引号"文件必须被判红，
#      而"三元写在字符串里 / 注释里 / here-string 里"必须**不被**判红；
#   4. 回归锁：本次修复的两个脚本必须解析通过（点名，防回退）。
#
# 范围说明：只查**语法**级。PS7 专属 **cmdlet/参数**（如 `ForEach-Object -Parallel`）
#   在 5.1 下会抛 CommandNotFound/ParameterBindingException，是**响亮**失败而非静默假绿，
#   不属本门禁职责（且靠文本匹配误报率高）。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tests/scripts/verify-ps-compat.ps1
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root

$pass = 0; $fail = 0
function Say($t) { Write-Output $t }
function CK($ok, $msg) { if ($ok) { $script:pass++; Say "  PASS $msg" } else { $script:fail++; Say "  FAIL $msg" } }

# ── 语料：门禁脚本本体 + 运行器 + 打包脚本（这些都会在 5.1 下被直接调用） ──────────
$globs = @('tests/scripts/*.ps1', 'tests/*.ps1', 'tools/*.ps1', '*.ps1')
$corpus = @()
foreach ($g in $globs) { $corpus += @(Get-ChildItem $g -ErrorAction SilentlyContinue) }
$corpus = @($corpus | Sort-Object FullName -Unique)

Say "宿主 PowerShell: $($PSVersionTable.PSVersion)  (Edition=$($PSVersionTable.PSEdition))"
Say "语料：$($corpus.Count) 个 .ps1`n"

# ── 工具：剥离字符串与注释，只留"正文" ──────────────────────────────────────────
# 用宿主自带的 tokenizer 拿 StringLiteral / StringExpandable / Comment / HereString* 的
# 精确区间，把这些区间原地替换成空格（保留 \r\n 以维持行号）。比手写状态机可靠。
$blankKinds = @('StringLiteral', 'StringExpandable', 'Comment', 'HereStringLiteral', 'HereStringExpandable')
function Get-CodeOnly([string]$path, [ref]$errsOut) {
    $tokens = $null; $errs = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errs)
    $errsOut.Value = $errs
    if (-not $tokens) { return '' }
    $chars = $ast.Extent.Text.ToCharArray()
    foreach ($t in $tokens) {
        if ($blankKinds -contains $t.Kind.ToString()) {
            $s = $t.Extent.StartOffset; $e = $t.Extent.EndOffset
            if ($e -gt $chars.Length) { $e = $chars.Length }
            for ($k = $s; $k -lt $e; $k++) {
                if ($chars[$k] -ne "`r" -and $chars[$k] -ne "`n") { $chars[$k] = ' ' }
            }
        }
    }
    return (-join $chars)
}

# ── PS7+ 专属语法探针（在"正文"上匹配） ─────────────────────────────────────────
# 注：`?(?![?.[])` 排除掉 `??` / `?.` / `?[`，避免三元规则与空值运算符互相误报。
$syntaxProbes = @(
    @{ Name = '三元运算符 ? :';    Re = '\?(?![\?\.\[])[^:\r\n]*:' },
    @{ Name = '空合并 ?? / ??=';   Re = '\?\?' },
    @{ Name = '空条件 ?. / ?[';    Re = '\?\s*[\.\[]' },
    @{ Name = '管道链 &&';         Re = '&&' },
    @{ Name = '管道链 ||';         Re = '\|\|' }
)
function Test-Ps7Syntax([string]$code) {
    $hits = @()
    $lines = $code -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($p in $syntaxProbes) {
            $m = [regex]::Match($lines[$i], $p.Re)
            if ($m.Success) {
                $hits += ("L{0} [{1}] {2}" -f ($i + 1), $p.Name, $lines[$i].Trim())
            }
        }
    }
    return $hits
}

# ── 第 0 节：语料自检 ──────────────────────────────────────────────────────────
Say "── 0. 语料自检 ──"
CK ($corpus.Count -ge 30) "扫到 $($corpus.Count) 个 .ps1（≥30，防扫空目录式假绿）"

# ── 第 1/2 节：解析 + 语法 ─────────────────────────────────────────────────────
Say "`n── 1/2. 解析 + PS7 专属语法 ──"
$parseBad = @(); $synBad = @(); $parseFailFiles = @{}
foreach ($f in $corpus) {
    $errs = $null
    $code = Get-CodeOnly $f.FullName ([ref]$errs)
    if ($errs -and $errs.Count -gt 0) {
        $parseBad += ("{0}: L{1} {2}" -f $f.Name, $errs[0].Extent.StartLineNumber, $errs[0].Message)
        $parseFailFiles[$f.Name] = $true
    }
    # 解析失败的文件其 token 流不完整 ⇒ 语法扫描结论不可信，跳过（已由第 1 节判红）
    if (-not $parseFailFiles.ContainsKey($f.Name)) {
        foreach ($h in (Test-Ps7Syntax $code)) { $synBad += ("{0}: {1}" -f $f.Name, $h) }
    }
}
CK ($parseBad.Count -eq 0) "全部脚本可被当前宿主解析（失败 $($parseBad.Count) 个）"
$parseBad | Select-Object -First 10 | ForEach-Object { Say "      $_" }
CK ($synBad.Count -eq 0) "正文无 PS7+ 专属语法（命中 $($synBad.Count) 处）"
$synBad | Select-Object -First 10 | ForEach-Object { Say "      $_" }

# ── 第 3 节：负控（证明探针有牙，且不会误伤字符串/注释） ────────────────────────
Say "`n── 3. 负控 ──"
$nc = Join-Path $env:TEMP ("pscompat_" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $nc -Force | Out-Null
# ⚠ PowerShell **没有**"相邻字符串自动拼接"（那是 C/Python 的规则）⇒ 多行样本文本一律
#   用数组 -join 构造，别写 `'a'$x'b'`（会解析失败，本脚本初版即踩）。
$nl = [char]10

function Probe-Text([string]$text, [string]$name) {
    $p = Join-Path $nc $name
    Set-Content -Path $p -Value $text -Encoding UTF8
    $errs = $null
    $code = Get-CodeOnly $p ([ref]$errs)
    return @{ ParseErrors = @($errs).Count; Hits = @(Test-Ps7Syntax $code).Count }
}

# 3.1 含三元的文件 ⇒ 必须命中
$r = Probe-Text 'function F($a,$b) { return $a ? 1 : 2 }' 'ternary.ps1'
CK ($r.Hits -ge 1) "负控：正文含三元 ⇒ 命中 $($r.Hits) 处（必须 ≥1）"

# 3.2 含 ?? 的文件 ⇒ 必须命中
$r = Probe-Text '$x = $null ?? 5' 'coalesce.ps1'
CK ($r.Hits -ge 1) "负控：正文含 ?? ⇒ 命中 $($r.Hits) 处（必须 ≥1）"

# 3.3 含 && 的文件 ⇒ 必须命中
$r = Probe-Text 'Get-ChildItem; if ($?) { Get-Date }' 'chain-ok.ps1'
CK ($r.Hits -eq 0) "负控：`if (`$?)` 写法**不得**误报（命中 $($r.Hits) 处，须为 0）"

# 3.4 三元写在双引号串里 ⇒ 不得命中（证明字符串被正确剥离）
$r = Probe-Text '$s = "a ? b : c"; Write-Output $s' 'ternary-in-string.ps1'
CK ($r.Hits -eq 0) "负控：三元在双引号串内 ⇒ 不命中（命中 $($r.Hits) 处，须为 0）"

# 3.5 三元写在注释里 ⇒ 不得命中
$r = Probe-Text (@('# 旧写法 $a ? 1 : 2 已废弃', 'Write-Output 1') -join $nl) 'ternary-in-comment.ps1'
CK ($r.Hits -eq 0) "负控：三元在注释内 ⇒ 不命中（命中 $($r.Hits) 处，须为 0）"

# 3.6 三元写在 here-string 里 ⇒ 不得命中（here-string 跨行，最容易漏）
$r = Probe-Text (@('$t = @"', 'a ? b : c', '"@', 'Write-Output $t') -join $nl) 'ternary-in-herestring.ps1'
CK ($r.Hits -eq 0) "负控：三元在 here-string 内 ⇒ 不命中（命中 $($r.Hits) 处，须为 0）"

# 3.7 弯引号写在双引号串里 ⇒ 解析器必须报错（第 1 节的判据在这一点上有牙）
$lq = [char]0x201C; $rq = [char]0x201D
$bad = '"判读：三组' + $lq + '无 ICC' + $rq + '必须逐字相同。"'
$r = Probe-Text $bad 'smartquote.ps1'
CK ($r.ParseErrors -ge 1) "负控：双引号串内嵌弯引号 ⇒ 解析报错 $($r.ParseErrors) 条（必须 ≥1）"

# 3.8 反控：干净的脚本 ⇒ 既不解析报错、也不命中语法
$r = Probe-Text 'Write-Output "ok"' 'clean.ps1'
CK ($r.ParseErrors -eq 0 -and $r.Hits -eq 0) "反控：干净脚本 ⇒ 0 解析错误 / 0 语法命中"

Remove-Item -Recurse -Force $nc -ErrorAction SilentlyContinue

# ── 第 4 节：点名回归锁 ────────────────────────────────────────────────────────
Say "`n── 4. 回归锁（点名） ──"
foreach ($n in @('verify-color-token-normalize.ps1', '_probe-icc-affects-decode.ps1')) {
    $f = Join-Path 'tests/scripts' $n
    if (-not (Test-Path $f)) { CK $false "$n 存在"; continue }
    $errs = $null
    $code = Get-CodeOnly $f ([ref]$errs)
    $h = @(Test-Ps7Syntax $code).Count
    CK (@($errs).Count -eq 0 -and $h -eq 0) "$n 解析通过且无 PS7 语法（解析错 $(@($errs).Count) / 语法 $h）"
}

Say "`n===== ps-compat: PASS=$pass FAIL=$fail ====="
exit $(if ($fail -eq 0) { 0 } else { 1 })
