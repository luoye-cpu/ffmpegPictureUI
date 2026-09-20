# _probe-cancel-propagation-scan.ps1 —— 结构锁：每个 FfmpegRunner.RunAsync 调用点都必须能收到取消令牌
# 为什么需要：FfmpegRunner.RunAsync 的 ct 是**可选参数**（默认 default）⇒ 漏传不会编译报错、不会告警，
#   只表现为「用户点了停止，ffmpeg 子进程仍跑到自然结束」——静默失效，只有 30 分钟挂死守卫兜底。
#   实测（2026-09-15 审计）：24 个调用点里 10 个没传（其中 1 处是有意豁免，见下）。
#   ⚠ 上一轮按行 grep 统计 "ct" 得出了「全部未传」的错误结论（多行调用里 ct 在下一行）
#     ⇒ 本锁用**括号配平**取参数列表，不按行匹配。
# 判据：调用点参数列表内必须出现独立的 `ct` 标识符（位置参数或 `ct:` 具名参数）。
#   豁免名单**硬编码在脚本里**（不是靠注释标记）：要新增豁免必须改本脚本，评审时看得见。
#     ⚠ 唯一的豁免 RestoreMetadataViaFfmpegAsync 不是遗漏：它跑在编码**成功之后**，是秒级 `-c copy` 重封装；
#       传 ct 会让「停止」命中 catch(OperationCanceledException)→Status="已停止"，
#       而 finally 对「已停止」会 TryDiscardPartialOutput **删掉已完成的合法产物** ⇒ 那是回归不是修复。
# 用法：pwsh -NoProfile -File tests/scripts/_probe-cancel-propagation-scan.ps1（非 0 退出 = 有漏传点）
#      报告落 tests/output/_probe-cancel-propagation.txt（可 -Report 覆盖）
param([string]$Report = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Report) { $Report = Join-Path $root 'tests/output/_probe-cancel-propagation.txt' }

# 豁免名单：方法名 → 理由（改这里 = 有意为之，评审可见）
$Exempt = @{
    'RestoreMetadataViaFfmpegAsync' = '编码成功之后的秒级 -c copy 收尾；传 ct 会在「停止」时删掉已完成产物'
}

$lines = New-Object System.Collections.Generic.List[string]
function Say([string]$t) { Write-Output $t; $script:lines.Add($t) }

try {
    $calls = 0; $ok = 0; $exemptHit = 0; $bad = @()

    Get-ChildItem -Recurse -Path "$root/src/FfmpegGui" -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $rel = $_.FullName.Substring($root.Length + 1)
            $text = [System.IO.File]::ReadAllText($_.FullName)

            # 收集「方法签名」的行号 → 用于判定调用点归属哪个方法
            $sigs = @()
            $m = [regex]::Matches($text, '(?m)^\s{2,}(?:private|public|internal|protected)\b[^=;]*\(')
            foreach ($x in $m) { $sigs += [pscustomobject]@{ Pos = $x.Index; Text = $x.Value.Trim() } }

            foreach ($c in [regex]::Matches($text, 'FfmpegRunner\.RunAsync\s*\(')) {
                $open = $c.Index + $c.Length - 1
                # 括号配平取参数列表
                $depth = 0; $close = -1
                for ($i = $open; $i -lt $text.Length; $i++) {
                    $ch = $text[$i]
                    if ($ch -eq '(') { $depth++ }
                    elseif ($ch -eq ')') { $depth--; if ($depth -eq 0) { $close = $i; break } }
                }
                if ($close -lt 0) { $bad += "${rel}: 括号配平失败"; continue }
                $args = $text.Substring($open + 1, $close - $open - 1)
                $lineNo = ($text.Substring(0, $c.Index) -split "`n").Count
                $calls++

                # 归属方法（调用点之前最近的方法签名）
                $owner = '<顶层>'
                foreach ($s in $sigs) { if ($s.Pos -lt $c.Index) { $owner = $s.Text } else { break } }
                # 取方法名：先剥泛型尖括号内容（Task<(...)> 里的括号会被误当参数列表），再取参数列表前的最后一个标识符
                $ownerName = ([regex]::Replace($owner, '<[^<>]*>', '') -replace '\(.*$', '').Trim()
                $ownerName = ($ownerName -split '\s+')[-1]

                if ($args -match '(?<![A-Za-z0-9_])ct(?![A-Za-z0-9_])') { $ok++; continue }
                if ($Exempt.ContainsKey($ownerName)) {
                    $exemptHit++
                    Say "  (豁免) ${rel}:$lineNo  $ownerName —— $($Exempt[$ownerName])"
                    continue
                }
                $bad += "${rel}:$lineNo  $ownerName"
            }
        }

    Say "FfmpegRunner.RunAsync 调用点：$calls 个（已传 ct $ok / 豁免 $exemptHit / 漏传 $($bad.Count)）"
    if ($bad.Count -eq 0) {
        # ⚠⚠ 文案必须**带上限定词**（2026-09-18 更正）：本脚本的扫描面**只有** `FfmpegRunner.RunAsync`
        #   （见 `:41` 的正则 `FfmpegRunner\.RunAsync\s*\(`）—— 它**不覆盖**直连 `Process.Start` 的站点。
        #   此前这句写「**每个**调用点都能收到取消令牌」，把限定条件丢了 ⇒ 读者会以为「所有子进程调用点都受控」。
        #   实测规模：全仓直连 `Process.Start(` 有 **65 处**（`QueueProcessor.cs` 11 · `ExifToolService.cs` 10 · …）
        #   全部**不在本脚本的扫描面内**。⚠ 这**不等于**那 65 处都有问题（那需独立审计）；
        #   本条只负责「**门禁的措辞不得大于它的判据**」（与 `docs/TESTING.md §6` 第 50/58 条同族）。
        #   已知的一个真实受害者：`QueueProcessor.IsAnimatedJxl` 曾直连 `Process.Start`、无 ct，
        #   且其 30 秒超时不可达 ⇒ 队列永久挂死（2026-09-18 修；门禁 `verify-jxl-probe-timeout.ps1`）。
        Say "PASS **FfmpegRunner.RunAsync** 的每个调用点都能收到取消令牌（「停止」能真正杀掉 ffmpeg 子进程）"
        Say "      ⚠ 本结论**只覆盖 `FfmpegRunner.RunAsync` 调用点**；全仓直连 `Process.Start(` 的 65 处**不在扫描面内**（另有独立风险，需单独审计）"
        $code0 = 0
    } else {
        Say "FAIL 漏传 ct 的 **FfmpegRunner.RunAsync** 调用点：$($bad.Count) 个（点了停止也停不下来）"
        $bad | ForEach-Object { Say "  $_" }
        $code0 = 1
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
