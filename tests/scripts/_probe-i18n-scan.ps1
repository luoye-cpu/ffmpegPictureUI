# _probe-i18n-scan.ps1 —— 双语（i18n）完整性结构锁
#
# 为什么需要它：`LocalizationService` 的索引器在 key 缺失时**回退返回 key 名本身**
# （这是好设计，便于发现遗漏），但只有"有人去看"时才暴露。本锁把它变成门禁：
#   ① XAML 引用的每个 {ext:Loc key} 必须在 zh-CN 与 en-US 里**都有定义**；
#   ② 两个语言文件的 key 集合必须**一致**（否则某一语言下会显示 key 名）；
#   ③ 顺带统计 XAML/C# 里的**硬编码中文**（只报告、不判红 —— 存量较多，
#      见 HANDOVER §D；C# 里多为日志可不译，XAML 里是用户可见文本应逐步迁到 Loc）。
#
# 不依赖任何外部 exe（纯 PowerShell），因此不受 `& exe` 宿主问题影响。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$locDir = "$root/src/FfmpegGui/Resources/Locales"
$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }

$zhPath = "$locDir/zh-CN.json"; $enPath = "$locDir/en-US.json"
foreach ($p in @($zhPath, $enPath)) {
    if (-not (Test-Path $p)) { Write-Output "  FAIL 缺少语言文件 $p"; Write-Output "===== i18n 扫描: PASS=0 FAIL=1 ====="; exit 1 }
}

# 提取扁平 JSON 的 key（不依赖 ConvertFrom-Json，避免深度/注释差异）
function Get-LocKeys([string]$path) {
    (Select-String -Path $path -Pattern '^\s*"([A-Za-z0-9._]+)"\s*:' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
}
$zh = @(Get-LocKeys $zhPath); $en = @(Get-LocKeys $enPath)

CK ($zh.Count -gt 0) "zh-CN 解析到 $($zh.Count) 个 key"
CK ($en.Count -gt 0) "en-US 解析到 $($en.Count) 个 key"

# ②′ 每个语言文件内**不得有重复键**（2026-09-17 补 —— 补的是一个真实溜过去的缺陷）
#    ⚠ 根因：上面 `Get-LocKeys` 用了 `Sort-Object -Unique` ⇒ **重复键被静默折叠**。
#      实测两份 locale 曾各出现一个重复的 `nvenc.aq.strength`（两个写者并发插在同一位置），
#      而本门禁当时**全绿** —— 因为键集合一致、缺失为 0，两条断言都看不见重复。
#    ⚠ 后果不是"多一行"，而是：`System.Text.Json` 反序列化进 Dictionary 时**后者覆盖前者**
#      ⇒ **你以为改的那一条可能根本没生效**，且改错时没有任何信号。
function Get-LocKeyRaw([string]$path) {
    Select-String -Path $path -Pattern '^\s*"([A-Za-z0-9._]+)"\s*:' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }
}
$zhDup = @(@(Get-LocKeyRaw $zhPath) | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { "$($_.Name)x$($_.Count)" })
$enDup = @(@(Get-LocKeyRaw $enPath) | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { "$($_.Name)x$($_.Count)" })
CK ($zhDup.Count -eq 0) "zh-CN 无重复键（重复 $($zhDup.Count) 个$($(if ($zhDup.Count) { '：' + (($zhDup | Select-Object -First 5) -join ', ') })))"
CK ($enDup.Count -eq 0) "en-US 无重复键（重复 $($enDup.Count) 个$($(if ($enDup.Count) { '：' + (($enDup | Select-Object -First 5) -join ', ') })))"

# ② 两个语言的 key 集合必须一致
$onlyZh = @($zh | Where-Object { $_ -notin $en })
$onlyEn = @($en | Where-Object { $_ -notin $zh })
CK ($onlyZh.Count -eq 0) "zh-CN 没有 en-US 缺失的 key（多出 $($onlyZh.Count) 个$($(if ($onlyZh.Count) { '：' + (($onlyZh | Select-Object -First 5) -join ', ') })))"
CK ($onlyEn.Count -eq 0) "en-US 没有 zh-CN 缺失的 key（多出 $($onlyEn.Count) 个$($(if ($onlyEn.Count) { '：' + (($onlyEn | Select-Object -First 5) -join ', ') })))"

# ① 引用的 key 必须都有定义（缺失时界面会显示 key 名本身）
#
# ⚠ 覆盖缺口教训 1（glob 太窄）：原先只 glob `*.xaml` + `Controls/*.axaml`，**漏掉了
#   根目录下的 .axaml 窗口**（FormatFilterWindow / ImageDetailWindow / PresetManagerWindow），
#   真实缺失数被低报 143（实为 194）。改为递归收集两种扩展名（-Include 同时匹配，
#   不会重复计数），并排除 bin/obj 下的构建产物副本。
# ⚠ 覆盖缺口教训 2（只扫 XAML 是不够的）：C# 侧同样引用 Loc key，共两类来源：
#   (a) 索引器字面量 `LocalizationService.Instance["k"]` / `loc["k"]`；
#   (b) `FillComboByLoc(combo, idx, "k1", "k2", ...)` 的 params 实参（下拉选项文本）。
#   这两类此前**完全没被扫描**，实测共 178 个 key 全部缺失 ⇒ 优先级下拉、元数据分类、
#   预设详情、线程提示等界面长期显示原始 key 名（如 `priority.normal`）。
#   同一处缺口在 XAML 侧也发生过（`<sys:String>` 元素内容，见 ④），故一并锁住。
$xamlFiles = @(Get-ChildItem -Path "$root/src/FfmpegGui" -Recurse -File -Include *.xaml, *.axaml |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.FullName } | Sort-Object -Unique)
CK ($xamlFiles.Count -gt 0) "扫描到 $($xamlFiles.Count) 个 XAML/AXAML 文件"
$usedXaml = @(Select-String -Path $xamlFiles `
        -Pattern '\{ext:Loc ([A-Za-z0-9._]+)\}' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
CK ($usedXaml.Count -gt 0) "XAML 引用到 $($usedXaml.Count) 个 Loc key"

$csFiles = @(Get-ChildItem -Path "$root/src/FfmpegGui" -Recurse -File -Include *.cs |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.FullName } | Sort-Object -Unique)
# (a) 索引器字面量。⚠ 必须 -CaseSensitive：Select-String 默认大小写不敏感，
#     否则 `["BT.2020"]` 这类色彩注册表的色域名会被当成 Loc key（实测误报 2 个）。
#     限定「含点且各段小写起头」以排除 `["sRGB"]` / `["bt709"]` 等注册表键。
$usedCsIndexer = @(Select-String -Path $csFiles -CaseSensitive `
        -Pattern '\[\s*"([a-z][A-Za-z0-9._]*\.[A-Za-z0-9._]+)"\s*\]' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
CK ($usedCsIndexer.Count -gt 0) "C# 索引器引用到 $($usedCsIndexer.Count) 个 Loc key"
# (b) FillComboByLoc 实参。调用可跨行，故按「从 FillComboByLoc( 起、到以 ); 结尾的行」的块提取。
$usedCsCombo = @()
foreach ($f in $csFiles) {
    $inCall = $false
    foreach ($line in (Get-Content $f -Encoding UTF8)) {
        if ($line -match 'FillComboByLoc\s*\(') { $inCall = $true }
        if ($inCall) {
            foreach ($m in [regex]::Matches($line, '"([a-z][A-Za-z0-9._]*\.[A-Za-z0-9._]+)"')) { $usedCsCombo += $m.Groups[1].Value }
            if ($line -match '\)\s*;\s*$') { $inCall = $false }
        }
    }
}
$usedCsCombo = @($usedCsCombo | Sort-Object -Unique)
CK ($usedCsCombo.Count -gt 0) "FillComboByLoc 引用到 $($usedCsCombo.Count) 个 Loc key"

$used = @($usedXaml + $usedCsIndexer + $usedCsCombo | Sort-Object -Unique)
$missing = @($used | Where-Object { ($_ -notin $zh) -or ($_ -notin $en) })
CK ($missing.Count -eq 0) "XAML/C# 引用的 key 在两种语言里都有定义（缺失 $($missing.Count) 个$($(if ($missing.Count) { '：' + (($missing | Select-Object -First 5) -join ', ') })))"

# ③ 硬编码中文：只报告（存量问题，不强制清零，避免"为了绿而放宽"）
#   ⚠ 口径说明（2026-09-17 更正）：下面是**口径①**（**窄**口径，仅供快速一瞥）——
#     只扫 `MainWindow.xaml` 的属性值 / `MainWindow.xaml.cs`，且**不剥注释**。
#     **权威口径（口径②）**是 `_probe-cjk-hardcode-scan.ps1`：扫**全部** `*.xaml`/`*.axaml`/`*.cs`
#     并剥注释，实测 **XAML 13 / C# 1135**（其中 342 条 `[tag]` 编码日志），
#     且**有牙**（断言"不得增加"，含负控）。**两个数不要混用**：
#     本行的 `9/216` 只是"本脚本当前打印值"，**不是存量规模**；要引用存量规模请用口径②。
#     详见 `docs/TESTING.md §6` 第 61 条。
$hardXaml = @(Select-String -Path "$root/src/FfmpegGui/MainWindow.xaml" `
             -Pattern '(Text|Content|Header|ToolTip\.Tip|PlaceholderText)="[^"{]*[\u4e00-\u9fa5]' -AllMatches).Count
$hardCs = @(Select-String -Path "$root/src/FfmpegGui/MainWindow.xaml.cs" `
           -Pattern '"[^"]*[\u4e00-\u9fa5][^"]*"' -AllMatches).Count
Write-Output "  ℹ 硬编码中文（口径①，窄口径，只报告不判红）：XAML $hardXaml 处 / C# $hardCs 处"
Write-Output "  ℹ 权威口径②（全仓 + 剥注释、且**有牙**）见 _probe-cjk-hardcode-scan.ps1（当前 13 / 1135，判据「不得增加」）"
Write-Output "  ℹ 说明：C# 里多为**日志**（可不译）；XAML 里是**用户可见文本**（应逐步迁到 Loc）"

# ④ `<sys:String>`（ComboBox 选项）里的硬编码中文 —— 必须单独统计
#    ⚠ 这类**不能用 {ext:Loc}**（标记扩展在元素内容里不生效），必须改为 C# 的
#      FillComboByLoc 填充。上面 ③ 的正则只匹配属性值，**完全看不见元素内容**，
#      不单独统计就会成为隐形缺口（本项目实际发生过：67 处一直没被发现）。
# ⚠ 必须显式 -Encoding UTF8：Get-Content 默认按 ANSI 读，中文会变乱码，
#   导致 `[\u4e00-\u9fa5]` 误匹配（实测把注释行也计入，误报 3 处，而 grep 实测为 0）。
$hardSysStr = @(Get-Content "$root/src/FfmpegGui/MainWindow.xaml" -Encoding UTF8 |
                Where-Object { $_ -match '<sys:String>[^<]*[\u4e00-\u9fa5]' -and $_ -notmatch '<!--' }).Count
Write-Output "  ℹ <sys:String> 里的中文：$hardSysStr 处（⚠ 仅供参考：该计数走 PowerShell 正则，与 grep 结果有偏差；权威值请用 grep -c '<sys:String>[^<]*[一-龥]'）"

Write-Output "===== i18n 扫描: PASS=$pass FAIL=$fail ====="
if ($fail -gt 0) { exit 1 }
exit 0
