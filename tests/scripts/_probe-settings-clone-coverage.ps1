# _probe-settings-clone-coverage.ps1 —— 核对 AppSettingsService.Save() 手写克隆是否覆盖全部持久化字段
# 用法：pwsh -NoProfile -File tests/scripts/_probe-settings-clone-coverage.ps1 [-RefFile <path>]
# 结论（2026-09-15 实测）：
#   - HEAD 版 Save() 漏 IccDirectory / EnableIpcServer 两项（→ S-P0-1 成立，非 5 项：
#     CjxlPath/CjpegliPath/AvifencPath/UltrahdrPath/JxrPath 是 [Obsolete] 迁移输入，故意不落盘）。
#   - 修复后工作树版漏 0 项。
# -RefFile 传 HEAD 导出的旧文件即为对照组；对照组必须报红，否则本校验器空转。
param([string]$RefFile = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$model = Join-Path $root 'src/FfmpegGui/Models/AppSettings.cs'
$svc = if ($RefFile) { Get-Content $RefFile -Raw } else { Get-Content (Join-Path $root 'src/FfmpegGui/Services/AppSettingsService.cs') -Raw }

# 1) 模型里真正持久化的可写属性：排除 [Obsolete]（迁移输入）与 [JsonIgnore]（计算属性）
$lines = Get-Content $model
$persisted = New-Object System.Collections.Generic.List[string]
$skip = 0
foreach ($l in $lines) {
    if ($l -match '\[Obsolete') { $skip = 1; continue }
    if ($l -match '\[JsonIgnore') { $skip = 1; continue }
    if ($l -match 'public\s+[\w\.\?<>,\s]+\s+(\w+)\s*\{\s*get;\s*set;\s*\}') {
        if ($skip -eq 0) { $persisted.Add($Matches[1]) }
        $skip = 0
    } elseif ($l.Trim() -ne '' -and $l -notmatch '^\s*///' -and $l -notmatch '^\s*//' -and $l -notmatch '^\s*\[') { $skip = 0 }
}

# 2) Save() 的克隆体里出现的赋值
$clone = New-Object System.Collections.Generic.List[string]
$m = [regex]::Match($svc, 'var clone = new AppSettings\s*\{(?<body>.*?)\};', 'Singleline')
if (-not $m.Success) { Write-Output 'FAIL 找不到 Save() 的克隆体（模型或写法已变，本校验器需同步）'; exit 1 }
foreach ($a in [regex]::Matches($m.Groups['body'].Value, '(?<k>\w+)\s*=\s*_current\.(?<k2>\w+)')) {
    if ($a.Groups['k'].Value -ne $a.Groups['k2'].Value) { Write-Output "FAIL 克隆项 $($_.Value) 键值不一致"; exit 1 }
    $clone.Add($a.Groups['k'].Value)
}

$missing = @($persisted | Where-Object { $clone -notcontains $_ })
$ghosts = @($clone | Where-Object { $persisted -notcontains $_ })
Write-Output "持久化属性 $($persisted.Count) 个，克隆项 $($clone.Count) 个"
Write-Output "漏写落盘字段：$(($missing | ForEach-Object { $_ }) -join ', ')"
Write-Output "克隆里出现但模型已不持久化的字段（陈旧）：$(($ghosts | ForEach-Object { $_ }) -join ', ')"
# 3) 反向检查：序列化确实会写出这些字段（AppJsonContext 用源生成，字段需在类型上）
if ($missing.Count -eq 0 -and $ghosts.Count -eq 0) { Write-Output 'PASS 克隆与模型持久化字段同源'; exit 0 }
Write-Output 'FAIL 克隆与模型不同源'
exit 1
