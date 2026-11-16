# _release-gate-check.ps1 — 出包前红态闸（取证工具，下划线前缀，不进 104 条门禁登记表）
#
# 用途：pack.ps1 在打 release 包前，把本批变更链路必跑门禁的 (gate 名, rc, 日志路径, 产物指纹)
#   写进 tests/output/release-gates.json，然后调本脚本做**最后一道 fail-closed**：任一 rc≠0 ⇒ exit 1。
#
# 为什么需要它（MA-01/GATE-02）：门禁脚本各自退出、各自打日志，但"出包"这个动作过去没有一个
#   集中的最后闸 —— 某条链路门禁红着也能照样 pack。本脚本只做一件事：读清单、逐行看 rc、
#   有一条非 0 就非 0 退出并把红的那行点名。它**不重跑**门禁（那是 runner 的事），只对账清单。
#
# 输入（tests/output/release-gates.json，UTF-8）：
#   {
#     "gates": [
#       { "name": "verify-color-strategy", "rc": 0, "log": "docs/.../verify-color-strategy.log",
#         "artifact": "src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.dll" },
#       ...
#     ]
#   }
#   - rc        : 该门禁进程的退出码（0=绿；非 0=红/未跑）。**字段缺失/类型不对按红处理**（fail-closed）。
#   - log       : 日志路径（相对仓库根），只用于人读点名，本脚本不解析其内容。
#   - artifact  : 被测产物相对路径（dll/exe），打印其 sha256[:16] 让"打在哪个包上"可追溯。
#
# 退出码：0 = 全绿可出包；1 = 任一 rc≠0（或清单本身不合规），禁止出包。
#
# 接线现状（2026-11-16）：pack.ps1 **尚未**调本脚本；由后续打包批次（批次4）接线。本文件只保证
#   helper 与清单格式就位，并自带负控（喂一份含 rc=1 的 json ⇒ 必须 exit 1）。
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$jsonPath = Join-Path $root 'tests/output/release-gates.json'

if (-not (Test-Path -LiteralPath $jsonPath)) {
    Write-Output "[release-gate] 缺少清单 $jsonPath —— 出包前必须先按 docs/RELEASE_GATES.md 跑完门禁并落清单。拒绝出包。"
    exit 1
}

try {
    $raw = [System.IO.File]::ReadAllText($jsonPath, [System.Text.Encoding]::UTF8)
    $doc = $raw | ConvertFrom-Json
} catch {
    Write-Output "[release-gate] 清单不是合法 JSON：$($_.Exception.Message) —— 拒绝出包。"
    exit 1
}

if (-not $doc.gates -or $doc.gates.Count -eq 0) {
    Write-Output "[release-gate] 清单 gates 为空 —— 没跑门禁就想出包？拒绝出包。"
    exit 1
}

$bad = @()
foreach ($g in $doc.gates) {
    $name = if ($g.name) { $g.name } else { '(unnamed)' }
    # fail-closed：rc 缺失/不是整数/非 0，一律按红。
    # ⚠ 不能用 `-is [int]`：ConvertFrom-Json 在 PS7 把 JSON 整数解析成 [long]/[int] 不固定
    #   ⇒ 真·rc=0 也会被判非 int 而假红。统一 TryParse 成整数再比。
    $rcVal = -1
    $hasRc = ($null -ne $g.PSObject.Properties['rc']) -and
             [int]::TryParse([string]$g.rc, [ref]$rcVal)
    $rcOk = $hasRc -and ($rcVal -eq 0)
    $fp = ''
    if ($g.artifact) {
        $ap = Join-Path $root $g.artifact
        if (Test-Path -LiteralPath $ap) {
            try { $fp = (Get-FileHash -Algorithm SHA256 -LiteralPath $ap).Hash.ToLower().Substring(0, 16) } catch { $fp = '(hash-fail)' }
        } else { $fp = '(artifact-missing)' }
    }
    if ($rcOk) {
        Write-Output ("[release-gate] GREEN {0,-32} rc=0  dll={1}  log={2}" -f $name, $fp, $g.log)
    } else {
        $rcv = if ($hasRc) { $rcVal } else { '<missing>' }
        Write-Output ("[release-gate] RED   {0,-32} rc={1}  dll={2}  log={3}" -f $name, $rcv, $fp, $g.log)
        $bad += $name
    }
}

if ($bad.Count -gt 0) {
    Write-Output ("[release-gate] 出包中止：{0} 条门禁非绿（{1}）。按 docs/RELEASE_GATES.md 红态禁止发布。" -f $bad.Count, ($bad -join ', '))
    exit 1
}
Write-Output ("[release-gate] 全绿（{0} 条），允许出包。" -f $doc.gates.Count)
exit 0
