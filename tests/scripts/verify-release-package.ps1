# verify-release-package.ps1 —— 出包后的**独立验收**：不信 `pack.ps1` 自己打的"完成"，从归档反推目录并逐项对账。
#
# 为什么要这么验（2026-10-01 beta3 实测）：`pack.ps1` 曾在**清理旧产物目录失败**（被占用）之后
# 一路打印"✅ 压缩完成 / 🎉 全部打包完成"，而磁盘上的产物目录已被打成 58 个文件的残缺态；
# 同一时刻归档本身却是完整的 ⇒ **目录与归档要分别验，且互相对账**（`pack.ps1` 现已在压缩前加完整性闸，
# 这条是**第二把**闸：它看的是最终交付物，不是打包脚本自己的中间状态）。
#
# ⚠ **有意不进受管清单**（与 `verify-oracle-matrix.ps1` / `probe-gainmap-tiff.ps1` 同类）：
#   它的前置条件是**磁盘上已有本次要发的包**，而整轮门禁在任何开发机上都不该假设这一点。
#   ⇒ 用法 = 出包流程里的**人工一步**：`$env:PKG_VER` 必须设（不设就拒绝跑，见下），
#   跑法 `pwsh -NoProfile -File tests/scripts/verify-release-package.ps1`，紧跟 `pack.ps1` 之后、
#   五条 L3 套件（清单第 61~65 条，同一个 `PKG_VER`）之前。
#
# 判据分组：① 归档在位 + 条目数合理 ② 必需成员逐项命中（**反斜杠**匹配式：`7z l` 用反斜杠打印路径，
#   拿正斜杠去 grep 会让六件工具恒 0 命中 = 误报"包里缺 exiftool/artifacts"，09-30 实测踩过）
#   ③ **磁盘目录 ↔ 归档同数**对账（目录在但残缺也要红；目录不在则从归档解出再数）
#   ④ full 包内六件工具与仓内 `publish/PLAN` **逐字节同指纹**（防"旧 PLAN 快照混进包"）
#   ⑤ 包内 `FfmpegGui.exe` 的 `ProductVersion` 必须就是 `PKG_VER`。
# 退出码：0 = 全绿，1 = 有失败，2 = `PKG_VER` 没给（拒绝跑，不产出读数）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（用 「」）。
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# ⚠ 版本**必须显式给**，不给默认值：任何写死的默认版本都会在下一个发布日变成"拿 beta(n) 的读数
#   宣布 beta(n+1) 已验"。缺 ⇒ exit 2。
if (-not $env:PKG_VER) {
  'FAIL  未设 $env:PKG_VER ⇒ 拒绝跑。本脚本**不给默认版本**（给了就是一部假绿机器）'
  '      用法示例：$env:PKG_VER="1.6.0-beta4"; pwsh -NoProfile -File tests/scripts/verify-release-package.ps1'
  exit 2
}
$ver = $env:PKG_VER
# 与 `pack.ps1` 同一把尺：先问 PATH，再退到本机默认安装位置。
$sz = 'C:\Program Files\7-Zip\7z.exe'
$cmd7 = Get-Command '7z' -ErrorAction SilentlyContinue
if ($cmd7) { $sz = $cmd7.Source }
if (-not (Test-Path $sz)) { "FAIL  找不到 7z（PATH 与 $sz 都没有）⇒ 无法读归档"; exit 2 }
$R = New-Object System.Collections.ArrayList
function CK($n, $ok, $det) {
  $null = $R.Add([pscustomobject]@{ n = $n; ok = [bool]$ok })
  '{0}  {1}   {2}' -f $(if ($ok) {'PASS'} else {'FAIL'}), $n, $det
}
function Sha($p) { if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.Substring(0,16) } else { '<缺失>' } }

"VER(被验包) = $ver   7z = $sz"
foreach ($m in @(@{ tag='app'; dir="FFmpegPictureUI-v$ver-x64"; need=$false },
                @{ tag='full'; dir="FFmpegPictureUI-v$ver-x64-full"; need=$true })) {
  $arc  = "$root\publish\$($m.dir).7z"
  $bdir = "$root\publish\build\$($m.dir)"
  ''
  "===== $($m.tag)：$($m.dir) ====="
  CK "$($m.tag) 归档在位" (Test-Path $arc) $arc
  if (-not (Test-Path $arc)) { continue }
  $list = & $sz l $arc 2>$null
  $summ = @($list | Where-Object { $_ -match '^\d{4}-\d{2}-\d{2}' } | Select-Object -Last 1)
  $arcFiles = 0
  if ($summ.Count) { if ($summ[0] -match '(\d+) files') { $arcFiles = [int]$Matches[1] } }
  CK "$($m.tag) 归档条目数合理" ($arcFiles -ge $(if ($m.need) { 560 } else { 8 })) "归档内 $arcFiles 个文件（要求 ≥ $(if ($m.need) {560} else {8})）"
  # 必需成员（归档视角，注意 7z 用反斜杠打印路径 ⇒ 匹配式也必须用反斜杠，正斜杠会恒 0 命中）
  $need = @('FfmpegGui\.exe', 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES\.md', 'licenses')
  if ($m.need) {
    $need += @('PLAN\\ffmpeg-full\\ffmpeg\.exe', 'PLAN\\jxl\\bin\\cjxl\.exe', 'PLAN\\jxl\\bin\\djxl\.exe',
               'PLAN\\jxl\\bin\\jxlinfo\.exe', 'PLAN\\exiftool\\exiftool\.exe',
               'PLAN\\artifacts\\JxrEncApp\.exe', 'PLAN\\artifacts\\JxrDecApp\.exe',
               'PLAN\\artifacts\\avifenc\.exe', 'PLAN\\artifacts\\dngtool\.exe')
  }
  $abs = @()
  foreach ($pat in $need) { if (-not (@($list | Where-Object { $_ -match $pat }).Count)) { $abs += $pat } }
  CK "$($m.tag) 归档必需成员齐备" ($abs.Count -eq 0) $(if ($abs.Count) { '缺: ' + ($abs -join ' ') } else { "$($need.Count) 项全在" })
  # 目录 vs 归档：数量对账（归档完整但目录残缺 = 上次事故的实况）
  if (Test-Path $bdir) {
    $dirFiles = @(Get-ChildItem $bdir -Recurse -File).Count
    CK "$($m.tag) 磁盘目录与归档同数" ($dirFiles -eq $arcFiles) "目录 $dirFiles vs 归档 $arcFiles"
  } else {
    CK "$($m.tag) 磁盘目录在位" $false "$bdir 不存在 ⇒ 从归档解出"
    New-Item -ItemType Directory -Force -Path $bdir | Out-Null
    $null = & $sz x $arc "-o$bdir" -y 2>&1 | Select-Object -Last 1
    $dirFiles = @(Get-ChildItem $bdir -Recurse -File).Count
    CK "$($m.tag) 解出的目录条目数" ($dirFiles -eq $arcFiles) "解出 $dirFiles vs 归档 $arcFiles"
  }
  # 工具链与仓内主 PLAN 逐字节对账（防"旧 PLAN 快照混进包"）
  if ($m.need) {
    foreach ($t in @('PLAN\ffmpeg-full\ffmpeg.exe','PLAN\jxl\bin\cjxl.exe','PLAN\exiftool\exiftool.exe',
                     'PLAN\artifacts\JxrEncApp.exe','PLAN\artifacts\avifenc.exe','PLAN\artifacts\dngtool.exe')) {
      $a = "$bdir\$t"; $b = "$root\publish\$t"
      CK ("full 工具同指纹 " + (Split-Path $t -Leaf)) ((Sha $a) -eq (Sha $b) -and (Sha $a) -ne '<缺失>') "$bdir\$t=$(Sha $a)  publish\$t=$(Sha $b)"
    }
  }
  $exe = "$bdir\FfmpegGui.exe"
  CK "$($m.tag) 包内 exe 版本串" (((Get-Item $exe).VersionInfo.ProductVersion) -like "$ver*") (Get-Item $exe).VersionInfo.ProductVersion
  "  包内 exe sha=$((Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0,16))  $((Get-Item $exe).Length) B"
}
''
$bad = @($R | Where-Object { -not $_.ok }).Count
"---- 出包独立验收 (v$ver): PASS={0} FAIL={1} ----" -f ($R.Count - $bad), $bad
if ($bad) { exit 1 } else { exit 0 }
