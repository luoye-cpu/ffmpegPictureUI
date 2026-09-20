# _probe-stderr-drain-scan.ps1 —— P2-3 结构锁：凡重定向 stderr 的进程，同一方法内必须有人排空
# 为什么需要：Windows 匿名管道缓冲约 4KB。只读 stdout（或只 WaitForExit）而不读 stderr 时，
#   子进程写满 stderr 缓冲就阻塞在写上 ⇒ 我们永远等不到它退出 ⇒ 探测卡死、队列槽位永久泄漏。
#   实测可复现：`ServiceProbe procstreams`（cmd 灌 ~199KB stderr，只读 stdout 的写法 10 秒不返回）。
# 判据：以**方法**为单位——`RedirectStandardError = true` 的声明数不得超过排空动作数
#   （`BeginErrorReadLine()` / 读 `.StandardError` / `ReadStdoutDrainingStderrAsync`）。
#   ⚠ 只认代码不认注释：注释里提一句 “见下方 BeginErrorReadLine” 就能把文本锁喂绿（本轮真踩过，已加固）。
# 用法：pwsh -NoProfile -File tests/scripts/_probe-stderr-drain-scan.ps1（非 0 退出 = 仍有未排空点）
#      已纳入 `_run-step3-gates.ps1` 的脚本清单，随门禁批跑。
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$drainPat = 'BeginErrorReadLine|\.StandardError|ReadStdoutDrainingStderrAsync|DrainProcessStreams'
$declTotal = 0; $bad = @()
Get-ChildItem -Recurse -Path "$root/src/FfmpegGui" -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object {
        $f = $_.FullName; $rel = $f.Substring($root.Length + 1)
        $L = [System.IO.File]::ReadAllLines($f)
        # 方法起点：带访问修饰符的签名行（与 ⑧ 结构锁同一套定位法）
        $starts = @()
        for ($i = 0; $i -lt $L.Count; $i++) {
            if ($L[$i] -match '^\s{2,}(?:private|public|internal|protected)[^=;]*\(' -and $L[$i] -notmatch '=>') { $starts += $i }
        }
        for ($m = 0; $m -lt $starts.Count; $m++) {
            $from = $starts[$m]
            $to = if ($m + 1 -lt $starts.Count) { $starts[$m + 1] - 1 } else { $L.Count - 1 }
            $body = $L[$from..$to]
            $d = @($body | Where-Object { $_ -match 'RedirectStandardError\s*=\s*true' }).Count
            if ($d -eq 0) { continue }
            $declTotal += $d
            # ⚠ 判据只认代码，不认注释：注释里写一句“见下方 BeginErrorReadLine”就能把这类文本锁喂绿（本仓踩过）。
            $code = @($body | ForEach-Object {
                $s = $_
                if ($s.TrimStart().StartsWith('//')) { return '' }
                $q = $s.IndexOf('//'); if ($q -ge 0) { $s = $s.Substring(0, $q) }
                return $s
            })
            $r = @($code | Where-Object { $_ -match $drainPat }).Count
            if ($r -lt $d) {
                $name = ($body[0] -replace '^\s+', '').Substring(0, [Math]::Min(78, ($body[0] -replace '^\s+', '').Length))
                $bad += "${rel}:$($from + 1) 声明=$d 排空=$r  「$name」"
            }
        }
    }
Write-Output "RedirectStandardError=true 声明：$declTotal 处"
if ($bad.Count -eq 0) { Write-Output "PASS 每个用到它的方法都排空了 stderr（不会把子进程堵死）"; exit 0 }
Write-Output "FAIL 疑似未排空的方法：$($bad.Count) 个（需逐个复核：可能是真缺陷，也可能是排空写在别处/由助手完成）"
$bad | ForEach-Object { Write-Output "  $_" }
exit 1
