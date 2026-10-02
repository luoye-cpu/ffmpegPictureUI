# verify-gainmap-thirdparty.ps1 —— GainMap 的**第三方**判据（清单第 64 条；
#   2026-10-01 由 `tests/output/t77/gainmap-thirdparty.ps1` 迁入）：libultrahdr 自己去读产物，不看自家日志
# 为什么要新这一段：现行 `verify-gainmap-managed.ps1:66` 是从**产品自己的日志行**里正则出
#   min/max/gamma，再跟"由 1000nit/203nit 推出的期望"比 —— 这只验到"产品想写什么"，
#   验不到"文件里真的写了什么"（序列化写错、写进错的盒、顺序颠倒都可能日志照旧）。
#   本段用 libultrahdr 官方示例程序 `-P`（probe）独立解析 ISO 21496-1，与产品日志逐项对账；
#   MPF 第二子图再由 exiftool 做**第二个**独立读者（两个实现都对上才算数）。
# ⚠ 前置：需要**本机构建的第三方参照** `tools/src/libultrahdr/build/Release/ultrahdr_app.exe`
#   （不在出货包内、不进版本控制）⇒ 缺它时本条判红是**有意的 fail-closed**，不是产品缺陷。
#   （`publish/PLAN/artifacts/` 里只有 JxrEncApp/JxrDecApp/avifenc/dngtool **四件**，从来没有这件 ——
#    `probe-gainmap-tiff.ps1` 就因指错那里而静默缺了整段第三方读数，2026-10-01 修。）
# 退出码：0 = 全绿，1 = 有失败，2 = 被测二进制不在位 / 包内版本串与期望不符（拒绝跑，不产出读数）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（改用 「」）。
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# ── 被测二进制两档（2026-10-01 接进门禁清单时从"只打出货包"改成两档）───────────────────
#   默认 = 本轮**刚构建的 Release 产物**（L2+，每轮都跑）；设 `$env:PKG_VER` 时升 **L3** 打出货包。
#   ⚠ 缺即 exit 2、不产出任何读数，**绝不回退 Debug**；包内版本串与期望不符同样 exit 2
#     （写死目录 ⇒ "beta4 的读数打在 beta3 包上"，那就是一台假绿机器）。
#   ⚠ 历史理由留档：本套件原先**只**打出货包，是因为并行会话的 `dotnet build` 会整时段重写 bin/Release
#     （实测到 FfmpegGui.exe 一度不在，Start-Process 直接报"系统找不到指定的文件"）。接入运行器后
#     该风险改成**响亮红/拒绝跑**（exit 2），不再靠"换一份冻结产物"绕过 ⇒ 读数不会静默打在异构二进制上。
if ($env:PKG_VER) {
  $pkgVer = $env:PKG_VER
  $pkg = "$root\publish\build\FFmpegPictureUI-v$pkgVer-x64-full"
  if (-not (Test-Path "$pkg\FfmpegGui.exe")) { "FAIL 被测包不在位：$pkg\FfmpegGui.exe ⇒ 先跑 pack.ps1 -Mode full，或用 PKG_VER 指到实际发包版本"; exit 2 }
  $pkgPV = (Get-Item "$pkg\FfmpegGui.exe").VersionInfo.ProductVersion
  if ($pkgPV -notlike "$pkgVer*") { "FAIL 包内版本串与期望不符：期望 $pkgVer* 实得 $pkgPV"; exit 2 }
  $exe = "$pkg\FfmpegGui.exe"; $plan = "$pkg\PLAN"
  "SUBJ(被测) = v$pkgVer (L3 出货包)  ProductVersion=$pkgPV"
} else {
  $exe = "$root\src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe"
  if (-not (Test-Path $exe)) { "FAIL Release 被测产物不在位：$exe ⇒ 先构建 Release；本门禁不回退 Debug"; exit 2 }
  $plan = "$root\publish\PLAN"
  'SUBJ(被测) = Release 开发产物 (L2+)  sha=' + (Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0, 16)
}
# T77_EXE 只换被测产品二进制（迭代时用），PLAN 仍取上面解析出的那一份
if ($env:T77_EXE) { $exe = $env:T77_EXE }
$uhdr = "$root\tools\src\libultrahdr\build\Release\ultrahdr_app.exe"
# ⚠ fail-closed：第三方参照不在位 ⇒ **拒绝跑**，不产出"没有增益图"这种读数。
#   不给它兜底（跳过 / 换成读自家日志）的理由：本条的存在意义就是"用第二个实现认证产物"，
#   参照缺席时任何绿灯都等于没判（§六第 5 项「判据两侧都空必须记未判，不给绿」）。
if (-not (Test-Path $uhdr)) {
  'FAIL  G0b 第三方参照 ultrahdr_app 在位 ⇒ 拒绝跑（本条没有替代读者，不给绿也不给"无增益图"）'
  "      缺 $uhdr ⇒ 修法：构建 tools/src/libultrahdr（CMake + MSVC），产物落在 build/Release/ultrahdr_app.exe"
  '---- GainMap 第三方判据: PASS=0 FAIL=1 ----'
  exit 1
}
$et   = "$plan\exiftool\exiftool.exe"
$d    = "$root\tests\output\validate\l3_gainmap3p"
if (Test-Path $d) { Remove-Item -Recurse -Force $d }
New-Item -ItemType Directory -Force -Path $d | Out-Null
$env:FFMPEGGUI_PLAN_DIR = $plan
if (-not (Test-Path $env:FFMPEGGUI_PLAN_DIR)) { $env:FFMPEGGUI_PLAN_DIR = "$root\publish\PLAN" }
if (-not (Test-Path $exe)) { 'FAIL  G0 被测二进制在位'; "缺 $exe"; exit 2 }
"G0 被测二进制: $exe  sha=$((Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0,16))  PLAN=$env:FFMPEGGUI_PLAN_DIR"
$R = @()
function CK($n, $ok, $det) { $script:R += [pscustomobject]@{ n = $n; ok = [bool]$ok }; '{0}  {1}   {2}' -f $(if ($ok) {'PASS'} else {'FAIL'}), $n, $det }
function X($file, $argStr, $tag) {
  $o = "$d\x_$tag.o"; $e = "$d\x_$tag.e"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = ([IO.File]::ReadAllText($o) + [IO.File]::ReadAllText($e)) }
}
function AppRun($tag, $inF, $extra) {
  $o = "$d\o_$tag"; New-Item -ItemType Directory -Force -Path $o | Out-Null
  $p = Start-Process -FilePath $exe -ArgumentList ('--headless --log-level Debug -i "' + $inF + '" --format jpg --output "' + $o + '" ' + $extra) `
       -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$d\a_$tag.o" -RedirectStandardError "$d\a_$tag.e"
  $log = [IO.File]::ReadAllText("$d\a_$tag.o") + [IO.File]::ReadAllText("$d\a_$tag.e")
  $f = @(Get-ChildItem $o -Recurse -File | Where-Object { $_.Extension -eq '.jpg' })
  $file0 = $null; if ($f.Count -ge 1) { $file0 = $f[0] }
  return @{ code = $p.ExitCode; file = $file0; count = $f.Count; log = $log }
}
if (-not (Test-Path $uhdr)) {
  CK 'G0 第三方探针在位' $false "缺 $uhdr ⇒ GainMap 数值无第三方判据，本段全部记未判（不给绿灯）"
  '---- GainMap 第三方判据: 未运行 ----'; exit 2
}
'=== A) HDR 源 + --jpeg-gain-map true ⇒ 产物必须被第三方认成 Ultra HDR，且逐项数值与产品自报一致 ==='
$hdr = "$root\tests\output\sources\src_hdr_pq.png"
$a = AppRun 'gm_hdr' $hdr '--jpeg-gain-map true'
if ($a.count -ne 1) { CK 'A1 产物唯一' $false "产物数=$($a.count) rc=$($a.code)"; exit 1 }
CK 'A1 产物唯一（HDR 源 + 增益图开关）' $true "$($a.file.Name) $((Get-Item $a.file.FullName).Length) B"
$probe = X $uhdr ('-m 1 -P -j "' + $a.file.FullName + '"') 'probe'
CK 'A2 第三方判为 Ultra HDR 图像' ($probe.text -match 'Ultra HDR Image:\s*Yes') (($probe.text -split "`r?`n" | Select-Object -First 1))
# 产品自己日志里的数值（这条**只当被检对象**，不能当判据）
# ⚠ 实测更正（09-30）：出货包这版**没有**「元数据: min=… max=… gamma=…」这行措辞，我上一版按它写正则
#   ⇒ A3 恒红（是我的 harness 假设错，不是产品缺陷）。真正在位的是这条：
#   `[GainMap] 参数: HDR峰值 1000nits, SDR白点 203nits, headroom 4.93 (2.30 log2), 底图=sRGB (SDR)`
#   ⇒ 对账口径改成"声明的 log2 余量 vs 文件里的 maxContentBoost"，这才是宣称与字节的对撞。
$mLog = [regex]::Match($a.log, '参数:\s*HDR峰值\s*([\d.]+)nits,\s*SDR白点\s*([\d.]+)nits,\s*headroom\s*([\d.]+)\s*\(([\d.]+)\s*log2\)')
$mPr  = [regex]::Match($probe.text, '--maxContentBoost\s+([\d.eE+-]+)')
$mPmin= [regex]::Match($probe.text, '--minContentBoost\s+([\d.eE+-]+)')
$mPg  = [regex]::Match($probe.text, '--gamma\s+([\d.eE+-]+)')
$mPo  = [regex]::Match($probe.text, '--offsetSdr\s+([\d.eE+-]+)')
$mCh  = [regex]::Match($probe.text, '--hdrCapacityMax\s+([\d.eE+-]+)')
$mCmin= [regex]::Match($probe.text, '--hdrCapacityMin\s+([\d.eE+-]+)')
$mBase= [regex]::Match($probe.text, '--useBaseColorSpace\s+(\S+)')
if (-not ($mPr.Success -and $mPmin.Success -and $mPg.Success -and $mPo.Success)) {
  # 探针没给全四项 ⇒ 无从对账，记未判（不能让 [double]'' 变成 0 后蒙出"一致"）
  CK 'A3 第三方探针给全了四项元数据' $false ("探针输出: " + (($probe.text -split "`r?`n" | Where-Object { $_.Trim() -ne '' }) -join ' / '))
} elseif (-not $mLog.Success) {
  CK 'A3 产品自报了可对账的余量行' $false "日志里没有「参数: HDR峰值 …nits, SDR白点 …nits, headroom … (… log2)」⇒ 宣称侧缺读数，本段只出文件侧数值"
} else {
  $pm   = [double]$mPr.Groups[1].Value;   $pmin = [double]$mPmin.Groups[1].Value
  $pg   = [double]$mPg.Groups[1].Value;   $po   = [double]$mPo.Groups[1].Value
  $hnit = [double]$mLog.Groups[1].Value;  $snit = [double]$mLog.Groups[2].Value
  $hr   = [double]$mLog.Groups[3].Value;  $lg2  = [double]$mLog.Groups[4].Value
  # ① 宣称的 log2 余量 ⇒ 2^x 必须等于文件里的 maxContentBoost（这一条把"日志说 4.93"变成"文件真是 4.92458"）
  $claim = [Math]::Pow(2.0, $lg2)
  CK 'A3 文件里的 maxContentBoost == 产品声明的 2^log2 余量' ([Math]::Abs($pm - $claim) / $claim -le 0.02) "文件 $pm vs 声明 2^$lg2=$([math]::Round($claim,5))（日志 headroom=$hr）"
  # ② 余量的物理来源：HDR 峰值 / SDR 白点（同一文件里另两个宣称值参与，防"日志与文件各自自洽但互不相干"）
  $ratio = $hnit / $snit
  CK 'A4 峰值/白点之比与文件增益上限一致' ([Math]::Abs($pm - $ratio) / $ratio -le 0.02) "$hnit/$snit=$([math]::Round($ratio,5)) vs 文件 $pm"
  CK 'A5 minContentBoost 合法（HDR-only 增益图恒为 1）' ([Math]::Abs($pmin - 1.0) -le 0.001) "文件 $pmin"
  CK 'A6 gamma 与产品单通道声明一致（默认 --jpeg-gain-map-multi-channel false）' ([Math]::Abs($pg - 1.0) -le 0.001) "文件 $pg"
  CK 'A7 offsetSdr 在规范合法域 [0,1) 且非 0 哨兵' ($po -ge 0 -and $po -lt 1) "文件 $po"
  if ($mCh.Success -and $mCmin.Success) {
    # ③ 文件内部一致性：hdrCapacity* 必须与 boost* 同值（不一致 ⇒ 序列化串位，第三方仍能解出但显示端会错）
    CK 'A8 hdrCapacityMax/Min 与 boost 字段同值（文件内部自洽）' `
      (([Math]::Abs([double]$mCh.Groups[1].Value - $pm) -le 0.001) -and ([Math]::Abs([double]$mCmin.Groups[1].Value - $pmin) -le 0.001)) `
      "boost=[$pmin,$pm] capacity=[$($mCmin.Groups[1].Value),$($mCh.Groups[1].Value)]"
  } else { CK 'A8 探针给到了 hdrCapacity 两项' $false '无从判内部一致性' }
  if ($mBase.Success) {
    # ④ 底图色域宣称：日志说"底图=sRGB(SDR)" ⇒ 增益图必须复用底图色空间（useBaseColorSpace 真）
    CK 'A9 useBaseColorSpace 与"底图=sRGB(SDR)"的宣称一致' ($mBase.Groups[1].Value -match '^(1|true|Yes|yes)') `
      ("文件 useBaseColorSpace=" + $mBase.Groups[1].Value + ' / 日志 ' + $(if ($a.log -match '底图=(\S+)') { $Matches[1] } else { '?' }))
  }
  CK 'A10 第三方读到的增益上限确有大于 1 的余量' ($pm -gt 1.5) "maxContentBoost=$pm"
}
# MPF 第二子图：exiftool 独立于 libultrahdr 的第二个读者
$ex = X $et ('-a -s -G1 -MPF:all -ifd0:orientation "' + $a.file.FullName + '"') 'mpf'
CK 'A11 exiftool 也读到第二子图（MPImage2 / MPF）' (($ex.text -match 'MPImage') -or ($ex.text -match 'MPF')) (($ex.text -split "`r?`n" | Where-Object { $_.Trim() -ne '' -and $_ -notmatch 'warning|locale' } | Select-Object -First 3) -join ' | ')
''
'=== B) 反真空：SDR 源 ⇒ 第三方必须说"没有增益图"（否则 A2 是恒真） ==='
$sdr = "$root\tests\output\sources\src_8bit.png"
$b = AppRun 'gm_sdr' $sdr '--jpeg-gain-map true'
if ($b.count -ne 1) { CK 'B1 SDR 臂产物唯一' $false "产物数=$($b.count) rc=$($b.code)" }
else {
  $pb = X $uhdr ('-m 1 -P -j "' + $b.file.FullName + '"') 'probe_sdr'
  CK 'B1 第三方判该产物**不含**增益图（A2 因此不是恒真）' ($pb.text -match 'does not contain gainmap') (($pb.text -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1))
  $eb = X $et ('-a -s -G1 -MPF:all "' + $b.file.FullName + '"') 'mpf_sdr'
  CK 'B2 exiftool 也读不到第二子图' (($eb.text -notmatch 'MPImage')) (($eb.text -split "`r?`n" | Where-Object { $_ -match 'MPImage' } | Select-Object -First 1))
}
''
$bad = @($R | Where-Object { -not $_.ok }).Count
'---- GainMap 第三方判据: PASS={0} FAIL={1} ----' -f ($R.Count - $bad), $bad
if ($bad -gt 0) { foreach ($x in @($R | Where-Object { -not $_.ok })) { '   ' + $x.n } ; exit 1 } else { exit 0 }
