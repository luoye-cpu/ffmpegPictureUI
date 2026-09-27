# _probe-settings-clone-coverage.ps1 —— 核对 AppSettingsService.Save() 的手写克隆是否覆盖**全部**持久化字段
#
# 为什么这条必须存在（2026-09-24 实测抓到一个长期缺陷，非本会话引入）：
#   `Save()` 不序列化 `_current`，而是 `var clone = new AppSettings { 逐字段 = _current.X }`
#   （理由写在源码里：不序列化已迁移的 [Obsolete] 旧字段）。⇒ **任何一个新加的持久化属性
#   如果忘了进这张表，就会被静默重置成"模型默认值"** —— 不是"不落盘"，是**落盘成默认值**，
#   比不落盘更坏：用户改完立刻 Save，值当场被打回默认。
#   实测命中：`AppSettings.RenderingMode`（模型 `AppSettings.cs:70`，默认 "auto"；
#   菜单 `MainWindow.xaml.cs:5239` 改它、`:5243` 立刻 `Save()`；启动 `Program.cs:127` 读它选渲染后端）
#   **从未出现在 `AppSettingsService.cs` 里**（`grep -c RenderingMode` = 0，`git log -S` 亦为空）
#   ⇒ 用户选 vulkan 会被下一次保存打回 auto ⇒ 回退链变成 AngleEgl→Vulkan→Software（"我选了 Vulkan，它先用 ANGLE"）。
#   ⚠ 本次之前这条判据**存在但不在受管清单里** ⇒ 谁也不跑 ⇒ 缺陷一直躺着。
#
# 判据形状（为什么这样写）：
#   · **下限断言**：模型属性数 / 克隆项数各设下限 ⇒ 否则"模型正则失效 ⇒ 数出 0 个字段 ⇒ 漏项=0 ⇒ 恒绿"
#     这条空断言通道会被当场抓住（本仓老规矩：动态枚举型门禁必须带下限）。
#   · **有牙自证**：从真实文本里删掉一行赋值再跑同一套比对 ⇒ 必须恰好报出被删的那个字段漏项。
#     不做这一步，"漏项 = 0"永远可能是"比对根本没在起作用"。⚠ 变异体只存在于内存，绝不回写源文件。
#   · `-RefFile` 留给人工对照（喂 `git show HEAD:...` 的旧文件），不参与本门禁的计数。
#
# 用法：pwsh -NoProfile -File tests/scripts/_probe-settings-clone-coverage.ps1 [-RefFile <path>]
# 退出码：0 = 全绿；1 = 有失败。**自带 PASS=/FAIL= 汇总行**（运行器按它分类，零断言会被判红）。
param([string]$RefFile = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pass = 0; $fail = 0
$script:mismatch = $false
function Say($t) { Write-Output $t }
function CK($ok, $msg) { if ($ok) { $script:pass++; Say "  PASS $msg" } else { $script:fail++; Say "  FAIL $msg" } }
function ListText($arr) { $a = @($arr | Where-Object { $_ }); if (@($a).Count -eq 0) { return '无' } ; return (@($a) -join ', ') }

# ── 1) 模型里真正持久化的可写属性：排除 [Obsolete]（迁移输入）与 [JsonIgnore]（计算属性） ──
function Get-PersistedFields([string]$modelPath) {
    $persisted = New-Object System.Collections.Generic.List[string]
    $skip = 0
    foreach ($l in (Get-Content -LiteralPath $modelPath)) {
        if ($l -match '\[Obsolete' -or $l -match '\[JsonIgnore') { $skip = 1; continue }
        if ($l -match 'public\s+[\w\.\?<>,\s]+\s+(\w+)\s*\{\s*get;\s*set;\s*\}') {
            if ($skip -eq 0) { $persisted.Add($Matches[1]) }
            $skip = 0
        } elseif ($l.Trim() -ne '' -and $l -notmatch '^\s*///' -and $l -notmatch '^\s*//' -and $l -notmatch '^\s*\[') { $skip = 0 }
    }
    return $persisted.ToArray()
}

# ── 2) 从一份 AppSettingsService 文本里取 Save() 的手写克隆体 ──
function Test-CloneBody([string]$svcText) {
    return [regex]::IsMatch($svcText, 'var clone = new AppSettings\s*\{(?<body>.*?)\};', 'Singleline')
}
function Get-CloneFields([string]$svcText) {
    $clone = New-Object System.Collections.Generic.List[string]
    $m = [regex]::Match($svcText, 'var clone = new AppSettings\s*\{(?<body>.*?)\};', 'Singleline')
    if (-not $m.Success) { return $clone.ToArray() }
    foreach ($a in [regex]::Matches($m.Groups['body'].Value, '(?<k>\w+)\s*=\s*_current\.(?<k2>\w+)')) {
        if ($a.Groups['k'].Value -ne $a.Groups['k2'].Value) { $script:mismatch = $true }
        $clone.Add($a.Groups['k'].Value)
    }
    return $clone.ToArray()
}

$modelPath = Join-Path $root 'src/FfmpegGui/Models/AppSettings.cs'
$svcPath = Join-Path $root 'src/FfmpegGui/Services/AppSettingsService.cs'
$both = ((Test-Path -LiteralPath $modelPath) -and (Test-Path -LiteralPath $svcPath))
CK $both '前置：模型与服务两个文件都在'

$persisted = @()
$clone = @()
$parseOk = $false
$svcText = ''
if ($both) {
    $persisted = @(Get-PersistedFields $modelPath)
    $svcText = Get-Content -LiteralPath $svcPath -Raw
    $clone = @(Get-CloneFields $svcText)
    $parseOk = Test-CloneBody $svcText
}

# ── 3) 下限断言（防「正则失效 ⇒ 空集合 ⇒ 恒绿」这条空断言通道） ──
$msgLo1 = '下限：模型里解析出的持久化属性 >= 15（实得 ' + $persisted.Count + '）⇒ 否则本门禁的比对没有对象'
$msgLo2 = '下限：Save() 克隆体里解析出的赋值项 >= 15（实得 ' + $clone.Count + '）⇒ 否则是解析失效不是覆盖'
CK ($persisted.Count -ge 15) $msgLo1
CK ($clone.Count -ge 15) $msgLo2

# ── 4) 主判据：两边必须同源 ──
$missing = @($persisted | Where-Object { $clone -notcontains $_ })
$ghosts = @($clone | Where-Object { $persisted -notcontains $_ })
Say ('  读数：持久化属性 ' + $persisted.Count + ' 个 / 克隆项 ' + $clone.Count + ' 个')
Say ('  读数：漏写落盘字段 = ' + (ListText $missing))
Say ('  读数：克隆里陈旧字段（模型已不持久化）= ' + (ListText $ghosts))
CK $parseOk '克隆体解析成功（写法变了 ⇒ 本校验器需同步，不许静默通过）'
CK (-not $script:mismatch) '克隆项的键名与右值属性名一致（不出现 X = _current.Y 这种张冠李戴）'
$msgMain = '每个持久化属性都被 Save() 写回落盘（漏项 = ' + (ListText $missing) + '）'
CK ($missing.Count -eq 0) $msgMain
$msgGhost = '克隆里没有模型已不再持久化的字段（陈旧项 = ' + (ListText $ghosts) + '）'
CK ($ghosts.Count -eq 0) $msgGhost

# ── 5) 有牙自证：内存里删掉一行赋值 ⇒ 比对必须恰好报出它（**绝不回写源文件**） ──
if ($parseOk -and $clone.Count -ge 1) {
    $victim = [string]$clone[0]
    $pat = '(?m)^\s*' + [regex]::Escape($victim) + '\s*=\s*_current\.' + [regex]::Escape($victim) + ',\r?\n'
    $badText = [regex]::Replace($svcText, $pat, '')
    $badClone = @(Get-CloneFields $badText)
    $badParse = Test-CloneBody $badText
    $badMissing = @($persisted | Where-Object { $badClone -notcontains $_ })
    #   ⚠ 判据是"相对基线恰好多出被删的那一个"，**不是**"漏项总数 == 1" ——
    #     主产品缺陷未修时基线漏项本来就非空（实得就是这种形态：基线 {RenderingMode} + 被删的 {FfmpegDirectory}
    #     ⇒ 2 个），写成 ==1 会让"有牙自证"与主判据**互相绑死**：主判据一红，自证也红，就分不清
    #     是"比对失效"还是"确实还漏着一个"。⇒ 用集合差判定，两条各自独立报账。
    $toothExtra = @($badMissing | Where-Object { $missing -notcontains $_ })
    $toothOk = ($badParse -and $badClone.Count -eq ($clone.Count - 1) -and $toothExtra.Count -eq 1 -and (@($toothExtra)[0] -eq $victim))
    $msgTooth = '有牙自证：内存里删掉「' + $victim + '」这一行赋值 ⇒ 相对基线必须恰好多报出它（实得 clone=' + $badClone.Count + ' / 基线漏项=' + (ListText $missing) + ' / 删后漏项=' + (ListText $badMissing) + '）'
    CK $toothOk $msgTooth
} else {
    CK $false '有牙自证无法执行（克隆体没解析成功 ⇒ 上面已判红，这里再记一次以免它被当成「没跑」）'
}

# ── 6) 可选人工对照（-RefFile 不参与计数；用来喂 `git show HEAD:...` 的旧文件） ──
if ($RefFile) {
    if (Test-Path -LiteralPath $RefFile) {
        $refClone = @(Get-CloneFields (Get-Content -LiteralPath $RefFile -Raw))
        $refMissing = @($persisted | Where-Object { $refClone -notcontains $_ })
        Say ('  读数（-RefFile 对照，不计分）：克隆项 ' + $refClone.Count + ' 个 / 漏项 = ' + (ListText $refMissing))
    } else {
        Say ('  读数（-RefFile 对照）：文件不存在 ' + $RefFile + ' ⇒ 对照未跑（不参与计分）')
    }
}

Say ''
Say ('===== settings-clone-coverage: PASS=' + $pass + ' FAIL=' + $fail + ' =====')
if ($fail -gt 0) { exit 1 }
exit 0
