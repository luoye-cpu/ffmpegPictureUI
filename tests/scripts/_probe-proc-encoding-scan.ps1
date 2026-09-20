# _probe-proc-encoding-scan.ps1 —— P2-5 结构锁：凡重定向子进程 stdout/stderr 的 ProcessStartInfo，必须显式声明解码编码
# 为什么需要：StandardOutputEncoding / StandardErrorEncoding 为 null 时，.NET 用「控制台当前代码页」解码子进程输出。
#   GUI 宿主（WinExe，无控制台）与中文 Windows 下该默认值不是 UTF-8 ⇒ 含非 ASCII 的路径/日志被解码成乱码。
#   实测现场：cjpegli/djxl 的 OutputDataReceived 日志里中文路径乱码（cjxl 早已设 UTF-8，同族工具却漏网）。
# 判据：以 **ProcessStartInfo 对象初始化块** 为单位（比方法级更精确，不会漏掉「块外消费」的写法）——
#   `RedirectStandardOutput = true` ⇒ 同块内必须有 `StandardOutputEncoding`；stderr 同理。
#   ⚠ 只认代码不认注释（与 _probe-stderr-drain-scan.ps1 同一套加固）：注释里提一句编码名喂不绿本锁。
#   ⚠ 不区分「文本读取」与「BaseStream 二进制搬运」：二进制搬运下该设置无副作用，
#     而分类判据脆弱（消费点常在块外）——一律要求声明，换取锁的不可绕过性。
# 用法：pwsh -NoProfile -File tests/scripts/_probe-proc-encoding-scan.ps1（非 0 退出 = 仍有未声明编码的重定向点）
#      报告落 tests/output/_probe-proc-encoding.txt（可 -Report 覆盖）；已纳入 `_run-step3-gates.ps1` 清单。
param([string]$Report = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Report) { $Report = Join-Path $root 'tests/output/_probe-proc-encoding.txt' }

$lines = New-Object System.Collections.Generic.List[string]
function Say([string]$t) { Write-Output $t; $script:lines.Add($t) }

try {
    $blocks = 0; $redirOut = 0; $redirErr = 0; $bad = @()

    Get-ChildItem -Recurse -Path "$root/src/FfmpegGui" -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $f = $_.FullName; $rel = $f.Substring($root.Length + 1)
            $L = [System.IO.File]::ReadAllLines($f)

            for ($i = 0; $i -lt $L.Count; $i++) {
                if ($L[$i] -notmatch 'ProcessStartInfo') { continue }
                if ($L[$i] -match '=>') { continue }                       # 排除 lambda / 表达式体

                # 定位块起始 '{'：同一行或紧随其后 3 行内
                $openLine = -1; $openCol = -1
                for ($k = $i; $k -le [Math]::Min($i + 3, $L.Count - 1); $k++) {
                    $c = $L[$k].IndexOf('{')
                    if ($c -ge 0) { $openLine = $k; $openCol = $c; break }
                    if ($L[$k].TrimEnd().EndsWith(';')) { break }           # 声明式用法（非对象初始化），跳过
                }
                if ($openLine -lt 0) { continue }

                # 花括号配平求块尾
                $depth = 0; $closeLine = -1
                for ($k = $openLine; $k -lt $L.Count; $k++) {
                    $s = $L[$k]
                    if ($k -eq $openLine) { $s = $s.Substring($openCol) }
                    foreach ($ch in $s.ToCharArray()) {
                        if ($ch -eq '{') { $depth++ }
                        elseif ($ch -eq '}') { $depth--; if ($depth -eq 0) { $closeLine = $k; break } }
                    }
                    if ($closeLine -ge 0) { break }
                }
                if ($closeLine -lt 0) { continue }

                $body = $L[$openLine..$closeLine]
                if (-not (@($body | Where-Object { $_ -match 'FileName\s*=' }).Count)) { continue }   # 非进程启动块
                $blocks++

                # 只认代码：剥掉整行注释与行尾注释
                $code = @($body | ForEach-Object {
                    $s = $_
                    if ($s.TrimStart().StartsWith('//')) { return '' }
                    $q = $s.IndexOf('//'); if ($q -ge 0) { $s = $s.Substring(0, $q) }
                    return $s
                })
                $codeText = ($code -join "`n")

                $hasOut = $codeText -match 'RedirectStandardOutput\s*=\s*true'
                $hasErr = $codeText -match 'RedirectStandardError\s*=\s*true'
                $encOut = $codeText -match 'StandardOutputEncoding'
                $encErr = $codeText -match 'StandardErrorEncoding'

                $miss = @()
                if ($hasOut) { $redirOut++; if (-not $encOut) { $miss += 'stdout' } }
                if ($hasErr) { $redirErr++; if (-not $encErr) { $miss += 'stderr' } }
                if ($miss.Count -gt 0) {
                    $bad += "${rel}:$($openLine + 1)  缺 $($miss -join '+') 编码"
                }

                $i = $closeLine   # 跳到块尾，避免嵌套重复计数
            }
        }

    Say "ProcessStartInfo 启动块：$blocks 个（重定向 stdout $redirOut 处 / stderr $redirErr 处）"
    $code0 = 1
    if ($bad.Count -eq 0) {
        Say "PASS 每个重定向块都显式声明了 UTF-8 解码编码（子进程输出不会按控制台代码页乱码）"
        $code0 = 0
    } else {
        Say "FAIL 未声明编码的重定向点：$($bad.Count) 个"
        $bad | ForEach-Object { Say "  $_" }
    }
}
catch {
    Say "EXCEPTION: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    Say ($_.ScriptStackTrace -replace "`r?`n", ' | ')
    $code0 = 9
}

$dir = Split-Path -Parent $Report
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllLines($Report, $lines, (New-Object System.Text.UTF8Encoding($false)))
exit $code0
