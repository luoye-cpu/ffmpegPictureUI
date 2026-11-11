# ═══════════════════════════════════════════════════════════════════════════
# _probe-host-temp-sites.ps1 — 宿主 TEMP 站点文本级探针（第 94 条，2026-11-01 接线）
#
# 判什么：AGENTS §7「宿主 TEMP 站点无源级锁」——宿主/测试工程向 Path.GetTempPath()
#   的取数点此前的锁只有"运行时钉扎"（Harness.PinScratchRoot 把 TMP/TEMP/TMPDIR 钉进
#   tests/output/_tmp/scratchroot），**没有文本级锁**：新代码随手再写一个 GetTempPath
#   站点、或者再发明一个固定名 scratch 根，没有任何门禁会红。
#
# 三把尺（全部 fail-closed）：
#   ① 每文件站点数冻结：tests/projects/**/*.cs 里 GetTempPath/GetTempFileName 的
#      命中行数按文件钉在 $siteTable 里——新增站点必须同批改表并写明生命周期，
#      否则红（这就是 stderr-merge-scan 的 debtBaseline 机制换个面）。
#   ② 坏根名黑名单：历史反例的临时根名（gate_tmp/gtmp/roundtmp/_ramdisk/faketemp，
#      AGENTS §3 记的 12 个里能从在册文本 recover 的 5 个）在 tests/projects 与 src
#      的 .cs 里必须 0 命中。
#   ③ 扫描面自证：命中总数必须等于表内合计（≠0 且两边相等）——扫描 mechanism 坏了
#      （比如 glob 改坏）时当场红，不是静默绿。
#
# 站点生命周期注记（2026-11-01 逐站点核过）：
#   ServiceProbe:11   —— tooldetect_<guid> 有 finally 递归删（Program.cs:7823-7831）；
#                        cmsbench= 有意缓存（BenchEndToEnd 复用 in.png；无在册调用者，手工档）；
#                        cmsx   = 手工档默认出参（在册调用者 verify-color-engine-xcheck.ps1:84 传 outDir）；
#                        其余 GUID 名均 try/finally 删。
#   GainMapTestHost:13 / gainmap-decode:7+1 / gainmap-encode:5（镜像切片）
#                     —— GUID 名 + cap.dng/gm_manual_*（2026-10-31 已补 finally 删的 4 处）。
#   UiTestHost:1 / UiHostShared:1 —— 引导点本体（Bootstrap/CreateTempDir，钉扎后的 GUID 目录）。
#
# 不扫 src/ 的理由（本条只管测试宿主的卫生，不管产品运行时的临时目录）：
#   产品的 GetTempDir 有三档（PlatformServices.cs:501-552）：① AppSettingsService.CacheDirectory
#   用户设置（:506-512）→ ② FFMPEGGUI_RAMDISK / /dev/shm / /var/tmp（:519/:535/:546）
#   → ③ **%TEMP% 兜底**（:551 return Path.GetTempPath()）。
#   即便配置用户 CacheDirectory 的宿主仍会走到 :551。
#   所以 ① 尺不纳入 src 的原由是「判据未定」（产品允许用 %TEMP%），
#   不是「产品无 %TEMP% 站点」——读者不要把本条读成产品卫生已全覆。要纳入须先
#   决定 src 站点是否也按文件钉数（独立一批）。
# ═══════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))

$pass = 0; $fail = 0
function CK($ok, $msg) {
    if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
    else     { $script:fail++; Write-Output "  FAIL $msg" }
}

# ── ① 扫描 ───────────────────────────────────────────────────────────────────
$csFiles = @(Get-ChildItem -Path 'tests/projects' -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
$hits = @{}
$total = 0
foreach ($f in $csFiles) {
    $n = 0
    foreach ($line in (Get-Content -LiteralPath $f.FullName -Encoding UTF8)) {
        if ($line -match 'GetTempPath|GetTempFileName') { $n++ }
    }
    if ($n -gt 0) { $hits[$f.FullName.Replace((Get-Location).Path + '\', '').Replace('\', '/')] = $n; $total += $n }
}

# ── ② 每文件站点数冻结表（改任何一边都要同批改另一边，并写明生命周期）──────────
$siteTable = @{
    # 2026-11-12（批 3 拆分同批迁表）：Program.cs 的 11 站点逐字节搬入 Parts，实测 11 个 Part 各 1 站点（总量守恒）：
    'tests/projects/ServiceProbe/Parts/07_ProbeUltraHdr.cs'        = 1
    'tests/projects/ServiceProbe/Parts/11_ProbeGainMapStats.cs'    = 1
    'tests/projects/ServiceProbe/Parts/16_ProbePlan.cs'            = 1
    'tests/projects/ServiceProbe/Parts/28_ProbePeakMeasure.cs'     = 1
    'tests/projects/ServiceProbe/Parts/43_ProbeIpc.cs'             = 1
    'tests/projects/ServiceProbe/Parts/44_ProbeSettingsSave.cs'    = 1
    'tests/projects/ServiceProbe/Parts/45_ProbeRunnerGuard.cs'     = 1
    'tests/projects/ServiceProbe/Parts/49_ProbeToolDetect.cs'      = 1
    'tests/projects/ServiceProbe/Parts/52_ProbeFrameLengthGuard.cs' = 1
    'tests/projects/ServiceProbe/Parts/53_ProbeBench.cs'           = 1
    'tests/projects/ServiceProbe/Parts/54_ProbeXcheck.cs'          = 1
    'tests/projects/GainMapTestHost/Program.cs'         = 13
    'tests/projects/gainmap-decode/GainMapDecodeGroupD.cs' = 7
    'tests/projects/gainmap-encode/GainMapEncodeGroup.cs'  = 5
    'tests/projects/gainmap-decode/GainMapDecodeGroup.cs'  = 1
    'tests/projects/UiTestHost/Program.cs'              = 1
    'tests/projects/UiHostShared/UiHost.cs'             = 1
}
$expectedTotal = 0; foreach ($v in $siteTable.Values) { $expectedTotal += $v }

CK ($total -gt 0) "扫描面自证：tests/projects 下扫到 $total 个 GetTempPath 站点（=0 ⇒ 扫描坏了，不当绿）"
CK ($total -eq $expectedTotal) "站点总数冻结：实扫 $total == 表内合计 $expectedTotal（不等 ⇒ 有人加了/删了站点，必须同批改 `$siteTable 并写明生命周期）"

$diffs = @()
foreach ($k in @($hits.Keys + $siteTable.Keys | Sort-Object -Unique)) {
    $a = 0; if ($hits.ContainsKey($k)) { $a = [int]$hits[$k] }
    $e = 0; if ($siteTable.ContainsKey($k)) { $e = [int]$siteTable[$k] }
    if ($a -ne $e) { $diffs += ("{0}: 实扫 {1} vs 表 {2}" -f $k, $a, $e) }
}
$diffLine = $(if ($diffs) { $diffs -join '；' } else { '无' })
CK ($diffs.Count -eq 0) "每文件站点数对账：差异 $($diffs.Count) 处（$diffLine）——新站点要进表并写明生命周期（删过头的要把表一起减）"

# ── ③ 坏根名黑名单（tests/projects + src 的 .cs）────────────────────────────
# 大小写敏感 + 前置分隔符（引号/斜杠）＝只抓「被当成路径段/字面量」的根名。
#   撞名不算：变量名 pngTmp、环境变量 FFMPEGGUI_RAMDISK（用户 RAM 盘设置）实测两族假阳性
#   （2026-11-01 首跑被它们打出 8 处假命中后收紧）。
$sep = '[' + '"' + '\\' + '/' + ']'
$badRoots = [ordered]@{}
foreach ($r in @('gate_tmp', 'gtmp', 'roundtmp', '_ramdisk', 'faketemp')) { $badRoots[$r] = $sep + [regex]::Escape($r) }
$badHits = @()
foreach ($scope in @('tests/projects', 'src')) {
    $files = @(Get-ChildItem -Path $scope -Recurse -File -Filter *.cs |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    foreach ($f in $files) {
        $ln = 0
        foreach ($line in (Get-Content -LiteralPath $f.FullName -Encoding UTF8)) {
            $ln++
            # 剥注释行（与 _probe-cjk-hardcode-scan 同口径）：坏根名的**在册记录**就在
            #   Harness.cs PinScratchRoot 的文档注释里（AGENTS §3 那 12 个的出处），
            #   注释里出现不是缺陷；活代码里出现才是要抓的"再发明临时根"。
            if ($line.TrimStart() -match '^(//|\*|/\*)') { continue }
            foreach ($rk in $badRoots.Keys) {
                if ($line -cmatch $badRoots[$rk]) { $badHits += ("{0}:{1} 含坏根名 {2}" -f $f.Name, $ln, $rk) }
            }
        }
    }
}
CK ($badHits.Count -eq 0) "坏根名黑名单（$($badRoots.Keys -join '/')）：命中 $($badHits.Count) 处$(if ($badHits) { '：' + (($badHits | Select-Object -First 5) -join '；') } else { '' })"

# ── ④ 自检有牙：对账函数对合成输入必须能分出红绿（防对账恒真）────────────────
$synBad = @()
$synA = @{ 'x/a.cs' = 2 }; $synE = @{ 'x/a.cs' = 3 }
foreach ($k in @($synA.Keys + $synE.Keys | Sort-Object -Unique)) {
    $av = 0; if ($synA.ContainsKey($k)) { $av = [int]$synA[$k] }
    $ev = 0; if ($synE.ContainsKey($k)) { $ev = [int]$synE[$k] }
    if ($av -ne $ev) { $synBad += $k }
}
$synOk = @()
$synA2 = @{ 'x/a.cs' = 2 }; $synE2 = @{ 'x/a.cs' = 2 }
foreach ($k in @($synA2.Keys)) {
    if ($synA2[$k] -ne $synE2[$k]) { $synOk += $k }
}
CK (($synBad.Count -eq 1) -and ($synOk.Count -eq 0)) "对账自检有牙：合成不匹配必须报 1 处、匹配必须报 0 处（实得 $($synBad.Count)/$($synOk.Count)）"

Write-Output ("TEMP SITES RESULT: pass=$pass fail=$fail")
if ($fail -gt 0) { exit 1 } else { exit 0 }
