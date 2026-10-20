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
# ⚠ 2026-10-03 修（复查 S5）：旧写法 `if ($env:T77_EXE) { $exe = $env:T77_EXE }` 在护栏**之后**覆盖，
#   盖完不复验 ProductVersion、也不重打 SUBJ ⇒ 版本错配（`PKG_VER` + `T77_EXE` 指向另一份）可静默绕护栏。
#   ⇒ 覆盖后重跑同一套存在性 + 版本串校验，并补打 `SUBJ(被测,覆盖后)`（G0 行打的是覆盖后的 sha，
#     但**版本串**此前从未对覆盖件重验过）。
#   变异验证：① `T77_EXE=<PLAN\ffmpeg.exe>` + `PKG_VER=1.6.0-beta4` ⇒ 修复后 exit 2 并点名；
#     ② 改回单行覆盖 ⇒ 同组环境变量下无任何红字 ⇒ 反证修复有牙。
if ($env:T77_EXE) {
  $exe = $env:T77_EXE
  if (-not (Test-Path $exe)) { "FAIL T77_EXE 覆盖的被测二进制不在位：$exe ⇒ 拒绝跑（不给异构二进制出读数）"; exit 2 }
  $ovPV = (Get-Item $exe).VersionInfo.ProductVersion
  if ([string]::IsNullOrWhiteSpace($ovPV)) { "FAIL T77_EXE 覆盖件读不到 ProductVersion：$exe ⇒ 无从证明被测是哪一份，拒绝跑"; exit 2 }
  if ($env:PKG_VER) {
    if ($ovPV -notlike "$pkgVer*") { "FAIL T77_EXE 覆盖件的版本串与 PKG_VER 期望不符：期望 $pkgVer* 实得 $ovPV（$exe）"; exit 2 }
  }
  'SUBJ(被测,覆盖后) = ' + $exe + '  ProductVersion=' + $ovPV + '  sha=' + (Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0, 16)
}
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
  # ⚠ 前置合取（2026-10-04 修 M6）：`($eb.text -notmatch 'MPImage')` 是裸否定式 ⇒ 取不到输出时恒真。
  #   ⚠ 但"文本非空"**不能**当前置：实测本仓 exiftool 对**无 MPF** 的文件，合法输出就是**空文本**（exit=0）
  #     ⇒ 用文本非空会误伤基线。而"读取失败"时 exiftool **exit=1** 且文本是 `Error: File not found …`
  #     —— 该错误文本同样不含 MPImage ⇒ 裸否定式照样 PASS（这才是真正的假绿口）。
  #   ⇒ 正确的"取到了"信号是**退出码 0**（exiftool 成功读取，哪怕没命中任何标签）：合取之，取不到点名判红。
  #   变异验证：把这一格的文件参数指到不存在的路径 ⇒ 旧写法 exit=1 仍 PASS（假绿）、新写法 FAIL 并点名。
  CK 'B2 exiftool 也读不到第二子图' (($eb.code -eq 0) -and ($eb.text -notmatch 'MPImage')) `
    $(if ($eb.code -ne 0) { "FAIL 前置：exiftool 未成功读取（exit=$($eb.code)）⇒ 否定式判据无从成立｜" + (($eb.text -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1) -as [string]) } else { (($eb.text -split "`r?`n" | Where-Object { $_ -match 'MPImage' } | Select-Object -First 1) -as [string]) })
}
'=== D) 增益图**输入**再出口：双端点必须由第三方读回（A 路 vs B 路，仓库内合成件；2026-10-15 T12/R4）==='
# 为什么必须有一格：`minContentBoost < 1` 是「暗部下沿没被抹平」的**唯一外部证据**。
#   产品自己日志里的 log2 分位数只能证明"它这么说"，不能证明"文件里真是这样"。
# 判据用**两臂对照**（同素材、同 `--jpeg-gain-map true`，只变 `--gain-map-base-from-source`）：
#   A 路（底图=源）⇒ 第三方必须读到 <1 的下沿；B 路（出口自造 Reinhard）⇒ 必须读回恰好 1
#   ⇒ 这条既钉住"下沿保住了"，也钉住"B 路确实会丢"（不是两臂同绿的假对照）。
# 实测（本仓 `tests/data/synth_heic_gainmap.heic`，声明 min=-0.257 max=1.277 γ=0.95）：
#   A ⇒ minContentBoost 0.817902 / maxContentBoost 2.46229   B ⇒ minContentBoost 1 / max 2.46229
$syn3 = "$root\tests\data\synth_heic_gainmap.heic"
if (-not (Test-Path $syn3)) {
  CK 'D0 仓库内合成增益图夹具在位' $false "缺 $syn3 ⇒ 本段**判红**（本门禁只有 PASS/FAIL 两态，无 SKIP；fail-closed 而不是静默放行）。个人素材不入仓，这里只用合成件"
} else {
  $dA = AppRun 'gm3_A' $syn3 '--jpeg-gain-map true'
  $dB = AppRun 'gm3_B' $syn3 '--jpeg-gain-map true --gain-map-base-from-source false'
  CK 'D1 两臂各产出唯一 jpg' (($dA.count -eq 1) -and ($dB.count -eq 1)) "A=$($dA.count) B=$($dB.count) rc=$($dA.code)/$($dB.code)"
  $pA = X $uhdr ('-m 1 -P -j "' + $dA.file.FullName + '"') 'd3a'
  $pB = X $uhdr ('-m 1 -P -j "' + $dB.file.FullName + '"') 'd3b'
  # ⚠ 变量名**大小写无关**是 PowerShell 的语义（`$mA` 与 `$Ma` 是同一个变量）：
  #   第一版用 `$mA/$Ma`、`$mB/$Mb` 成对存 min/max ⇒ 后一次赋值直接覆盖前一次，
  #   D2 把 max 的 2.46229 当 min 读出来 ⇒ 判据"假红"（而 D3 因 ≥0.999 恰好"假绿"，两格一起骗人）。
  #   ⇒ 这里一律用不重叠的名字。`verify-ps-compat` 抓不到这类（它只管解析与 PS7 语法）。
  $mnA = [regex]::Match($pA.text, '--minContentBoost\s+([0-9.]+)');  $mxA = [regex]::Match($pA.text, '--maxContentBoost\s+([0-9.]+)')
  $mnB = [regex]::Match($pB.text, '--minContentBoost\s+([0-9.]+)');  $mxB = [regex]::Match($pB.text, '--maxContentBoost\s+([0-9.]+)')
  CK 'D2 A 路：第三方读到 sub-1 的暗部下沿' `
     ($mnA.Success -and $mxA.Success -and ([double]$mnA.Groups[1].Value -lt 0.95) -and ([double]$mxA.Groups[1].Value -gt 1.3)) `
     "minContentBoost=$(if($mnA.Success){$mnA.Groups[1].Value}else{'-'}) maxContentBoost=$(if($mxA.Success){$mxA.Groups[1].Value}else{'-'})"
  CK 'D3 B 路（对照臂）：同一件关到 B 路时下沿必须塌回 1' `
     ($mnB.Success -and ([double]$mnB.Groups[1].Value -ge 0.999) -and ([double]$mnB.Groups[1].Value -lt 1.05)) `
     "minContentBoost=$(if($mnB.Success){$mnB.Groups[1].Value}else{'-'})（读到 1 = 底图被出口重造，暗部带丢失 ⇒ 这正是 A 路存在的理由）"
  CK 'D4 两臂只差在下沿、上沿相同（对照臂不是换了另一件事）' `
     ($mxA.Success -and $mxB.Success -and ([Math]::Abs([double]$mxA.Groups[1].Value - [double]$mxB.Groups[1].Value) -lt 0.02)) `
     "maxA=$(if($mxA.Success){$mxA.Groups[1].Value}else{'-'}) maxB=$(if($mxB.Success){$mxB.Groups[1].Value}else{'-'})"
  # D5：**同一份文件的两处声明必须同源**（ISO APP2 与 XMP hdrgm）。
  #   这一格的存在理由 = 审查抓到的阻断缺陷：`BuildXmpMetadata` 曾经**没接** `minLog2Gain`
  #   ⇒ 形参默认 0 ⇒ XMP 写 `GainMapMin=0.0`、ISO 写 −0.29 ⇒ 按 XMP 读的消费者丢下沿，
  #   而自家 `MergeXmpIntoMetadata` 也当场报「ISO 与 XMP 增益范围不一致」。修后两处一致。
  #   ⚠ 单位换算由**两个不同实现各报一次**再对撞：ISO 只给线性 boost（libultrahdr 读回），
  #     XMP 直接给 log2 ⇒ 必须把 ISO 的值取 log2 再比，不然两把尺根本不在同一域。
  # ⚠ 2026-10-20：默认档改成"只写 ISO"⇒ 默认产物里**没有 XMP**，"两处声明同源"这格失去了对撞对象
  #   （读不到 XMP 时 `rxMin.Success` 为假 ⇒ 旧写法会红，而不是被放宽成恒真）。
  #   判据不放宽：为它单开一条 `both` 档臂，并且 **XMP 与 ISO 都从同一条臂读**
  #   （原写法拿 $dA 的 XMP 对 $dA 的 ISO，依赖"A/B 只差下沿"这个前提；单开一条更直接）。
  $dX = AppRun 'gm3_X' $syn3 '--jpeg-gain-map true --jpeg-gain-map-metadata both'
  if ($dX.count -eq 1) {
    $pX = X $uhdr ('-m 1 -P -j "' + $dX.file.FullName + '"') 'd5_iso'
    $mnX = [regex]::Match($pX.text, '--minContentBoost\s+([0-9.]+)')
    $mxX = [regex]::Match($pX.text, '--maxContentBoost\s+([0-9.]+)')
    $x5 = X $et ('-a -s -G1 -XMP-hdrgm:GainMapMin -XMP-hdrgm:GainMapMax "' + $dX.file.FullName + '"') 'xmp_a'
    #   取值正则的正确形状是 `名字\s*:\s*值` —— exiftool 的输出里标签与冒号之间有一串**对齐空白**，
    #   写 `GainMapMin:` 会命中 0 次（实测），写 `GainMapMin\D*(...)` 又会把负号当"非数字"跳过（也实测）。
    $rxMin = [regex]::Match($x5.text, 'GainMapMin\s*:\s*(-?[0-9.]+)')
    $rxMax = [regex]::Match($x5.text, 'GainMapMax\s*:\s*(-?[0-9.]+)')
    $isoLog2Min = [Math]::Log([double]$mnX.Groups[1].Value) / [Math]::Log(2.0)
    $isoLog2Max = [Math]::Log([double]$mxX.Groups[1].Value) / [Math]::Log(2.0)
    $d5ok = ($x5.code -eq 0) -and $mnX.Success -and $mxX.Success `
            -and $rxMin.Success -and $rxMax.Success `
            -and ([Math]::Abs([double]$rxMin.Groups[1].Value - $isoLog2Min) -lt 0.02) `
            -and ([Math]::Abs([double]$rxMax.Groups[1].Value - $isoLog2Max) -lt 0.02)
    CK 'D5 两处声明同源（both 档件内自证）：XMP hdrgm 的 log2 与 ISO 读回取 log2 后一致（±0.02）' $d5ok `
       ("XMP[min,max]=[" + $(if($rxMin.Success){$rxMin.Groups[1].Value}else{'-'}) + "," +
        $(if($rxMax.Success){$rxMax.Groups[1].Value}else{'-'}) + "] ISO 换算=[" +
        $isoLog2Min.ToString('0.000') + "," + $isoLog2Max.ToString('0.000') + "] exiftool exit=" + $x5.code)
    # 反真空：XMP 若被写回 0（旧缺陷形态），上面必须红 ⇒ 用一条"必须不等"的正向自证锁住这把尺没坏
    CK 'D5b 反控：both 档件的 XMP 下沿必须**不是** 0.0（0.0 就是修前形态：声明了却没传参）' `
       ($rxMin.Success -and ([double]$rxMin.Groups[1].Value -lt -0.05)) `
       "GainMapMin=$(if($rxMin.Success){$rxMin.Groups[1].Value}else{'-'})"
    $xDef = X $et ('-a -s -G1 -XMP-hdrgm:GainMapMax "' + $dA.file.FullName + '"') 'xmp_default'
    CK 'D5c 默认档不得带 XMP（否则"默认只写 ISO"这件事没有牙）' `
       ($xDef.text -notmatch 'GainMapMax\s*:') `
       ("默认臂 exiftool exit=" + $xDef.code + "，输出里出现 GainMapMax = " + [bool]($xDef.text -match 'GainMapMax\s*:'))
  } else {
    CK 'D5 前置：both 档臂产出唯一 jpg（默认档不写 XMP ⇒ 这一格只能在 both 档对撞）' $false "count=$($dX.count) rc=$($dX.code)"
  }
  # D5d：字段集合法性（Ultra HDR v1.1 的 hdrgm 表只有九项）。正控 = 同一份 XMP 里读得到 GainMapMax；
  #   负断言 = 我方曾自造的五个字段绝迹（Channels/UseBaseRenderingColorSpace/MapPrimary/MapSecondary/MapImageRatio）。
  $xAll = X $et ('-a -s -G1 -XMP-hdrgm:all "' + $dX.file.FullName + '"') 'xmp_all'
  $madeUp = @('Channels', 'UseBaseRenderingColorSpace', 'MapPrimary', 'MapSecondary', 'MapImageRatio')
  $hit = @($madeUp | Where-Object { $xAll.text -match ('(?m)\b' + $_ + '\s*:') })
  CK 'D5d XMP 只含 Ultra HDR v1.1 的九项（五个自造字段绝迹）' `
     (($xAll.text -match 'GainMapMax\s*:') -and $hit.Count -eq 0) `
     ("GainMapMax 可读=" + [bool]($xAll.text -match 'GainMapMax\s*:') + " 自造命中=" + $hit.Count + $(if ($hit.Count -gt 0) { '［' + ($hit -join ',') + '］' } else { '' }))
  # D5e：xmp 档 + 三通道（无 ISO）也必须被第三方读者认成 Ultra HDR 并读出增益上限。
  $dXR = AppRun 'gm3_XR' $syn3 '--jpeg-gain-map true --jpeg-gain-map-metadata xmp --jpeg-gain-map-multi-channel true'
  if ($dXR.count -eq 1) {
    $pXR = X $uhdr ('-m 1 -P -j "' + $dXR.file.FullName + '"') 'd5e_probe'
    $mxXR = [regex]::Match($pXR.text, '--maxContentBoost\s+([0-9.]+)')
    CK 'D5e xmp+RGB（无 ISO）被 libultrahdr 认成 Ultra HDR 且读出 maxContentBoost' `
       (($pXR.text -match 'Ultra HDR Image:\s*Yes') -and $mxXR.Success) `
       (($pXR.text -split "`r?`n" | Select-Object -First 1) + ' max=' + $(if ($mxXR.Success) { $mxXR.Groups[1].Value } else { '-' }))
  } else {
    CK 'D5e 前置：xmp+RGB 臂产出唯一 jpg' $false "count=$($dXR.count) rc=$($dXR.code)"
  }
}
''
$bad = @($R | Where-Object { -not $_.ok }).Count
'---- GainMap 第三方判据: PASS={0} FAIL={1} ----' -f ($R.Count - $bad), $bad
if ($bad -gt 0) { foreach ($x in @($R | Where-Object { -not $_.ok })) { '   ' + $x.n } ; exit 1 } else { exit 0 }
