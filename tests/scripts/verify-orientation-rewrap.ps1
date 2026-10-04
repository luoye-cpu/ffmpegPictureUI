# verify-orientation-rewrap.ps1 —— 取向标签 × 免解码重封装：**产物级**一致性判据（清单第 63 条；
#   2026-10-01 由 `tests/output/t76/orientation-rewrap.ps1` 迁入。原首行写"被测 = 出货包内 exe"，
#   迁入后是**两档**，见下方 `SUBJ` 段：默认打刚构建的 Release 产物，设 `$env:PKG_VER` 才升 L3）
# 契约（ExifToolService/QueueProcessor 自己写在注释里的）：
#   Orientation 只在"像素一个没动"的免解码重封装下才允许保留；其余路径像素已被解码器旋正 ⇒
#   带回该标签 = 查看器二次旋转。
# 硬判据 = 先看**产物像素**是不是已经旋正的（与源的 noautorotate 解码比较），再看标签在不在。
#   像素已旋正 且 标签仍是源的 8/5/6/7 ⇒ FAIL（二次旋转）
#   像素未动（DCT 搬运） 且 标签保留                     ⇒ PASS
# B 段（2026-10-01 加）：`--tiff-dpi` 的**分标签**断言（Xres 与 Yres 各自 == 请求值，且 engine == legacy）
#   —— 单值比较会看漏"一半写对一半写错"，这正是上一轮 U2 那条读数的形状（重测已**否证**，见审计 §5.1）。
# 退出码：0 = 全绿，1 = 有失败，2 = 被测二进制不在位 / 包内版本串与期望不符（拒绝跑，不产出读数）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（改用 「」）。
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# ── 被测二进制两档（2026-10-01 接进门禁清单时从"只打出货包"改成两档）───────────────────
#   默认 = 本轮**刚构建的 Release 产物**（L2+，每轮都跑）；设 `$env:PKG_VER` 时升 **L3** 打出货包。
#   ⚠ 缺即 exit 2、不产出任何读数，**绝不回退 Debug**；包内版本串与期望不符同样 exit 2。
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
# T76_EXE 只换被测产品二进制，PLAN 工具链仍取上面解析出的那一份
# ⚠ 2026-10-03 修（复查 S5 的同形缺陷，本文件也在其列）：旧写法 `if ($env:T76_EXE) { $exe = $env:T76_EXE }`
#   盖在护栏之后、盖完不复验 ProductVersion 也不重打 SUBJ ⇒ 版本错配可静默绕过护栏。
#   ⇒ 覆盖后重跑同一套存在性 + 版本串校验，并补打 `SUBJ(被测,覆盖后)`。
#   变异验证：① `T76_EXE=<PLAN\ffmpeg.exe>` + `PKG_VER=1.6.0-beta4` ⇒ 修复后 exit 2 并点名；
#     ② 改回单行覆盖 ⇒ 同组环境变量下无任何红字 ⇒ 反证修复有牙。
if ($env:T76_EXE) {
  $exe = $env:T76_EXE
  if (-not (Test-Path $exe)) { "FAIL T76_EXE 覆盖的被测二进制不在位：$exe ⇒ 拒绝跑（不给异构二进制出读数）"; exit 2 }
  $ovPV = (Get-Item $exe).VersionInfo.ProductVersion
  if ([string]::IsNullOrWhiteSpace($ovPV)) { "FAIL T76_EXE 覆盖件读不到 ProductVersion：$exe ⇒ 无从证明被测是哪一份，拒绝跑"; exit 2 }
  if ($env:PKG_VER) {
    if ($ovPV -notlike "$pkgVer*") { "FAIL T76_EXE 覆盖件的版本串与 PKG_VER 期望不符：期望 $pkgVer* 实得 $ovPV（$exe）"; exit 2 }
  }
  'SUBJ(被测,覆盖后) = ' + $exe + '  ProductVersion=' + $ovPV + '  sha=' + (Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0, 16)
}
$ff = "$plan\ffmpeg-full\ffmpeg.exe"; $fp = "$plan\ffmpeg-full\ffprobe.exe"
$et = "$plan\exiftool\exiftool.exe"
$d = "$root\tests\output\validate\l3_orient"
if (Test-Path $d) { Remove-Item -Recurse -Force $d }
New-Item -ItemType Directory -Force -Path $d | Out-Null
$env:FFMPEGGUI_PLAN_DIR = "$plan"
$Res = @()
function CK($name, $ok, $detail) {
  $script:Res += [pscustomobject]@{ n = $name; ok = [bool]$ok }
  '{0}  {1}   {2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail
}
# ⚠ 缺工具**不再 throw**（2026-10-04 修 M10）：`$ErrorActionPreference='Stop'` 下 throw 会炸掉整脚本、
#   连汇总行都不打。改为逐条 CK 失败归集并继续（配合下面 X 的工具缺位防护，不会在下游 Start-Process 处再炸）。
foreach ($p in @($ff, $fp, $et, $exe)) {
  if (-not (Test-Path $p)) { CK ('工具在位：' + $p) $false ("缺 $p ⇒ 相关判据无从成立（记红并点名，不 throw 中止）") }
}
function X($file, $argStr, $tag) {
  # ⚠ 工具缺位时不调 Start-Process（2026-10-04 修 M10）：那会在 EAP=Stop 下抛终止错、又炸掉整脚本。
  #   返回空文本哨兵，让上层断言照常记 FAIL（fail-closed），而不是中断。
  # ⚠⚠ 但判据**不能**只看 `Test-Path`：本文件用 `X 'py' …` 生成夹具，而 `py` 是**裸命令名**（PATH 解析），
  #   `Test-Path 'py'` 恒为 false ⇒ 会把**可用**的解释器误判成缺位而**跳过生成**
  #   （实测 2026-10-04：夹具 `orient8.jpg` 不再产出 ⇒ B 段整段假红、`FAIL=10`）。
  #   ⇒ 「路径在位」**或**「PATH 上解析得到」二者其一即算在位。
  $present = (Test-Path -LiteralPath $file) -or ($null -ne (Get-Command $file -ErrorAction SilentlyContinue))
  if (-not $present) { return @{ code = -1; text = '' } }
  $o = "$d\x_$tag.o"; $e = "$d\x_$tag.e"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = ([IO.File]::ReadAllText($o) + [IO.File]::ReadAllText($e)) }
}
function App($tag, $inF, $fmt, $extra) {
  $o = "$d\o_$tag"; New-Item -ItemType Directory -Force -Path $o | Out-Null
  $a = '--headless --log-level Debug -i "{0}" --format {1} --output "{2}" {3}' -f $inF, $fmt, $o, $extra
  $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$d\a_$tag.o" -RedirectStandardError "$d\a_$tag.e"
  $log = [IO.File]::ReadAllText("$d\a_$tag.o") + [IO.File]::ReadAllText("$d\a_$tag.e")
  $f = @(Get-ChildItem $o -Recurse -File | Where-Object { $_.Extension -eq ".$fmt" })
  # ⚠ 不 throw（2026-10-04 修 M10）：一格 0/多产物必须**记成该格的 FAIL 并继续**，而不是在 EAP=Stop 下
  #   炸掉整脚本、连汇总行都不打（同批 jbrd 的 App 已按此返工，本文件照抄）。调用方按 count 判红。
  $file0 = $null
  if ($f.Count -ge 1) { $file0 = $f[0] }
  return @{ code = $p.ExitCode; file = $file0; count = $f.Count; log = $log }
}
function RawOf($path, $vf) {   # 用给定输入滤镜把产物解码成 raw 再取哈希（路径参数全部用位置，避免歧义）
  $t = [Guid]::NewGuid().ToString('N').Substring(0, 8)
  $out = "$d\h_$t.raw"
  $r = X $ff ('-y -hide_banner -loglevel error -i "' + $path + '" -vf "' + $vf + ',format=rgb24" -f rawvideo "' + $out + '"') "h_$t"
  if (-not (Test-Path $out)) { return '<undecodable:' + $r.code + '>' }
  return (Get-FileHash $out -Algorithm SHA256).Hash.Substring(0, 16)
}
function Orient($path) {
  $t = [Guid]::NewGuid().ToString('N').Substring(0, 6)
  $r = X $et ('-n -s3 -EXIF:Orientation "' + $path + '"') "or_$t"
  $s = ($r.text -split "`r?`n" | Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -First 1)
  if ($null -eq $s) { return '<无>' }
  return $s.Trim()
}
''
'=== 夹具：一张**带 Orientation=8** 的非方图（640x480 coded ⇒ 显示 480x640）==='
$py = @'
from PIL import Image, ImageDraw
import subprocess, sys
im = Image.new('RGB', (640, 480))
dr = ImageDraw.Draw(im)
# 上三分之一红、其余蓝：取向被错处理时顶部色块会跑到左边，肉眼与哈希都能分
dr.rectangle((0, 0, 640, 160), fill=(220, 30, 30))
dr.rectangle((0, 160, 640, 480), fill=(30, 60, 200))
for x in range(0, 640, 7):
    dr.line((x, 0, x, 480), fill=(200 - (x % 160), 90, 40 + (x % 90)))
im.save(sys.argv[1], 'JPEG', quality=92, subsampling=0)
'@
[IO.File]::WriteAllText("$d\mk.py", $py)
$src = "$d\orient8.jpg"
$null = X 'py' ('"' + "$d\mk.py" + '" "' + $src + '"') 'pil'
if (-not (Test-Path $src)) { CK '夹具：Pillow 产出 orient8.jpg' $false 'Pillow 未产出夹具 ⇒ 该素材相关判据全部记红（不 throw 中止，继续到汇总行）' }
$null = X $et ('-overwrite_original -n -EXIF:Orientation=8 "' + $src + '"') 'setor'
$oSrc = Orient $src
# 同素材的"未旋正"与"已旋正"两种解码，作为像素侧的两把独立尺子
$hNo   = X $ff ('-y -hide_banner -loglevel error -noautorotate -i "' + $src + '" -vf format=rgb24 -f rawvideo "' + "$d\no.raw" + '"') 'no'
$hNoR  = if (Test-Path "$d\no.raw") { (Get-FileHash "$d\no.raw").Hash.Substring(0,16) } else { '<无>' }
$hYes  = X $ff ('-y -hide_banner -loglevel error -i "' + $src + '" -vf format=rgb24 -f rawvideo "' + "$d\yes.raw" + '"') 'yes'
$hYesR  = if (Test-Path "$d\yes.raw") { (Get-FileHash "$d\yes.raw").Hash.Substring(0,16) } else { '<无>' }
$dimRaw = ((X $fp ('-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 "' + $src + '"') 'dim').text -join '').Trim()
'src: Orientation=' + $oSrc + '  coded=' + $dimRaw + '  noautorotate-raw=' + $hNoR + '  autorotate-raw=' + $hYesR
CK '夹具：源带 Orientation=8' ($oSrc -eq '8') "实得 $oSrc"
CK '夹具：两种解码口径确实不同（判据能分辨旋正与否）' ($hNoR -ne $hYesR) "no=$hNoR yes=$hYesR"
# ⚠ 源尺寸也要有值（2026-10-03 修 S1 的另一半）：`$dimRaw` 空 且 `$dimProd` 空时
#   `$dimProd -eq $dimRaw` = True ⇒ jbrd 臂"像素没动"假绿。空串不得流进比较。
# ⚠ 归一化（同一修）：实测 avif 那格 ffprobe 吐的是 `480,640,`（尾部多一个逗号，
#   见 `x_dp_avif_def.o` 的字节 `4 8 0 , 6 4 0 , \r \n`）⇒ 严格全量比对会把**读到的**格
#   误判成"没读到"（假红）。取文本里第一个 `N,N` 作为码流尺寸，显示仍保留原文。
$mSrcDim = [regex]::Match($dimRaw, '(\d+)\s*,\s*(\d+)')
$dimSrc  = $(if ($mSrcDim.Success) { $mSrcDim.Groups[1].Value + ',' + $mSrcDim.Groups[2].Value } else { '' })
CK '夹具：源码流尺寸读得到（形如 640,480）' ($dimSrc -ne '') "实得 coded=$dimRaw ⇒ 归一化=$dimSrc（源 $src）"
''
'=== A) 各目标格式：像素旋正 ⇒ 标签必须丢；像素未动（jbrd）⇒ 标签必须留 ==='
# 期望表：$expect = 'keep' | 'drop'
$rows = @(
  @{ fmt = 'jpg';  x = '';                        expect = 'drop'; why = 'mjpeg 重编码，像素被解码器旋正' },
  @{ fmt = 'jxl';  x = '';                        expect = 'keep'; why = 'jbrd 免解码重封装，DCT 系数原样搬' },
  @{ fmt = 'jxl';  x = '--jxl-lossless-jpeg false'; expect = 'drop'; why = '关掉重封装 ⇒ 走像素路线 ⇒ 标签该丢' },
  @{ fmt = 'png';  x = '';                        expect = 'drop'; why = 'PNG 无取向概念，像素已旋正' },
  @{ fmt = 'avif'; x = '';                        expect = 'drop'; why = 'AVIF 重编码，像素已旋正' },
  @{ fmt = 'webp'; x = '';                        expect = 'drop'; why = 'WebP 重编码，像素已旋正' }
)
foreach ($r in $rows) {
  $tag = ('{0}_{1}' -f $r.fmt, $(if ($r.x -eq '') { 'def' } else { 'off' }))
  $lab = ('{0}/{1}' -f $r.fmt, $r.why)
  $a = App ("A_$tag") $src $r.fmt $r.x
  # ⚠ 不 throw（2026-10-04 修 M10）：一格 0/多产物记红并 continue，不炸整脚本（App 已改为返回 count）。
  if ($a.count -ne 1) { CK ("A " + $lab + "：产物唯一") $false ("产物数=" + $a.count + " rc=" + $a.code); continue }
  $o = Orient $a.file.FullName
  # ⚠ 判"像素有没有被旋正"只能用**码流尺寸**，不能用整图哈希：有损重编码（jpg/avif/webp）本身
  #   就会让哈希不同 ⇒ 哈希不等既可能来自旋转也可能来自噪声，属混淆判据（本轮实测到旧写法在
  #   png 上恰好判对，只因 png 无损；换成 jpg 就是蒙对）。尺寸判据与噪声无关。
  $jinfo = "$plan/jxl/bin/jxlinfo.exe"
  $jiRaw = ''
  if ($r.fmt -eq 'jxl') {
    if (-not (Test-Path $jinfo)) { CK ("A " + $lab + "：jxlinfo 可用") $false "缺 $jinfo ⇒ JXL 臂无从判定，不给绿灯" }
    else { $jiRaw = (X $jinfo ('"' + $a.file.FullName + '"') "ji_$tag").text }
  }
  if ($r.fmt -eq 'jxl') {
    # jxlinfo 的 "JPEG XL image, 640x480" 是 **coded** 尺寸，另有一行 "Orientation: N"
    $mDim = [regex]::Match($jiRaw, 'JPEG XL image,\s*(\d+)x(\d+)')
    $mOri = [regex]::Match($jiRaw, 'Orientation:\s*(\d+)')
    $dimProd = $(if ($mDim.Success) { $mDim.Groups[1].Value + ',' + $mDim.Groups[2].Value } else { '<未知>' })
    $oriInContainer = $(if ($mOri.Success) { $mOri.Groups[1].Value } else { '<无>' })
  } else {
    $dimProd = ((X $fp ('-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 "' + $a.file.FullName + '"') "dp_$tag").text -join '').Trim()
  }
  # ⚠ 空值前置（2026-10-03 修 S1）：`$dimProd` 读不到时是**空串**，而
  #     · `'' -ne $dimRaw` = True ⇒ "像素已旋正" **PASS**（假绿）；
  #     · 同一格 `Orient` 在同源失败下也回 `'<无>'` ⇒ "旋正后标签已剥" **PASS**（假绿）；
  #     · 两侧同空时 `'' -eq $dimRaw` = True ⇒ jbrd 臂"像素没动"也 **PASS**（假绿）。
  #   即一次读取失败可让**互斥的三条判据同时绿**。正解照 `verify-jbrd-e2e.ps1:289` 的
  #   `($dS -ne '') -and ($dR -ne '') -and ...`：先断言"尺寸读到了且形如 N,N"，
  #   读不到 ⇒ 显式 FAIL 并**点名**这一格，绝不让空串流进比较。
  #   变异验证：把 `$dimProd` 强制置空（模拟 ffprobe/jxlinfo 读取失败）⇒ 旧写法 5 条 drop 臂
  #   "已旋正 + 标签已剥"共 10 条**全绿**、0 FAIL；改回修复写法 ⇒ 12 条 FAIL（6 臂 × 2 条）并点名。
  $mProdDim = [regex]::Match($dimProd, '(\d+)\s*,\s*(\d+)')
  $dimProdN = $(if ($mProdDim.Success) { $mProdDim.Groups[1].Value + ',' + $mProdDim.Groups[2].Value } else { '' })
  $dimOk = ($dimProdN -ne '')
  $pixRotated = ($dimOk -and ($dimProdN -ne $dimSrc))
  if ($r.expect -eq 'drop') {
    CK ("A " + $lab + "：产物码流尺寸读得到（ffprobe/jxlinfo 有值 ⇒ 判据才有判别力）") $dimOk "产物 coded=$dimProd ⇒ 归一化=$dimProdN（源 coded=$dimSrc）"
    CK ("A " + $lab + "：产物像素已被旋正（码流尺寸 640,480 → 480,640）") $pixRotated "产物 coded=$dimProdN（源 coded=$dimSrc）"
    CK ("A " + $lab + "：旋正后必须丢弃/归一 Orientation，否则查看器二次旋转") ($dimOk -and (($o -eq '<无>') -or ($o -eq '1'))) "实得 Orientation=$o（源=8，产物 coded=$dimProdN）"
  } else {
    CK ("A " + $lab + "：产物码流尺寸读得到（jxlinfo 有值 ⇒ 判据才有判别力）") $dimOk "产物 coded=$dimProd ⇒ 归一化=$dimProdN（源 coded=$dimSrc）"
    CK ("A " + $lab + "：jbrd 路线像素没动（JXL coded 尺寸不变）") ($dimOk -and ($dimProdN -eq $dimSrc)) "产物 coded=$dimProdN 源 coded=$dimSrc"
    # 取向可以走两条路且必须恰好一条：EXIF 侧（exiftool 读得到）或 JXL 容器自己的 Orientation 盒
    # 两处都写取向**不是**矛盾：值相同就自洽（读任一处都得到同一答案）。真正会二次旋转的是
    # "像素已旋正 + 仍留旧取向"，那由上面一臂管。本臂只要求"取向被带走"且"两处不打架"。
    CK ("A " + $lab + "：jbrd 路线取向被产物带走且两处不打架") `
       (((($o -ne '<无>') -or ($oriInContainer -ne '<无>'))) -and (($o -eq '<无>') -or ($oriInContainer -eq '<无>') -or ($o -eq $oriInContainer))) `
       "EXIF Orientation=$o，JXL 容器 Orientation=$oriInContainer"
  }
}
''
# ══ B) TIFF 分辨率：Xres 与 Yres 是**两个标签**，只比一个值会把"半写入"看漏 ══
# 动因：审计里记了一条"引擎路线 --tiff-dpi 被降为 72、Yres 仍是 300（自相矛盾）"。
# 本轮（10-01）在 beta3 树上实测**不可复现**（engine/legacy × 8bit/16bit 四格都是 300/300）
#   ⇒ 按本仓规矩不再把它当已知事实引用；但"半写入"这个形状值得一条锁：
#   两个标签都必须等于请求值，且**引擎路线与 legacy 路线一致**。
'=== B) TIFF --tiff-dpi：Xres/Yres 双标签一致 × {engine, legacy} × {8bit, 16bit} ==='
$dpiWant = 300
$dpiCases = @(
  @{ n='8bit';  src="$root\tests\output\sources\src_8bit.png" },
  @{ n='16bit'; src="$root\tests\output\sources\src_16bit.png" }
)
foreach ($c in $dpiCases) {
  # ⚠ fail-closed（2026-10-03 修 S2）：这两个夹具在被 gitignore 的 `tests/output/sources/` 下
  #   ⇒ 干净 checkout / `git clean -xdf` 后必然不在位。旧写法是 `' SKIP ...' + continue` ⇒
  #   B 段**一条断言都不产生**，`$bad` 仍 0 ⇒ 整脚本全绿（本仓明令禁止的"动态枚举无下限断言"）。
  #   ⇒ 夹具缺失**判红**并点名：没跑到 ≠ 通过。
  if (-not (Test-Path $c.src)) {
    CK ("B " + $c.n + "：夹具在位") $false ("夹具缺失 " + $c.src + " ⇒ 判红（该目录被 .gitignore 排除，SKIP 会让本段 0 断言仍全绿）")
    continue
  }
  $perEngine = @{}
  foreach ($eng in @('engine', 'legacy')) {
    $t = "$d\o_bt_$($c.n)_$eng"; New-Item -ItemType Directory -Force -Path $t | Out-Null
    $extra = if ($eng -eq 'engine') { '--color-engine engine' } else { '--color-engine legacy' }
    $p = Start-Process -FilePath $exe -ArgumentList ('--headless --log-level Debug -i "' + $c.src + '" --format tiff --tiff-dpi ' + $dpiWant + ' ' + $extra + ' --output "' + $t + '"') `
         -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$d\b_$($c.n)_$eng.o" -RedirectStandardError "$d\b_$($c.n)_$eng.e"
    $f = @(Get-ChildItem $t -Recurse -File | Where-Object { $_.Extension -in '.tif', '.tiff' })
    if ($f.Count -ne 1) { CK ("B " + $c.n + '/' + $eng + '：产物唯一') $false ("产物数=" + $f.Count + " rc=" + $p.ExitCode); continue }
    # ⚠ 分标签读：单值比较会看漏"X=72 / Y=300"这种半写入（本轮实测过该形状才有这条）
    $rr = X $et ('-n -S -a -XResolution -YResolution -ResolutionUnit "' + $f[0].FullName + '"') "bt_$($c.n)_$eng"
    $txt = ($rr.text -join ' ')
    $xr = [regex]::Match($txt, 'XResolution\D*([\d.]+)').Groups[1].Value
    $yr = [regex]::Match($txt, 'YResolution\D*([\d.]+)').Groups[1].Value
    $perEngine[$eng] = "$xr/$yr"
    CK ("B " + $c.n + '/' + $eng + "：Xres 与 Yres 都等于请求 $dpiWant") `
      ($xr -eq "$dpiWant" -and $yr -eq "$dpiWant") "X=$xr Y=$yr｜$($txt -replace '\s+', ' ')"
  }
  if ($perEngine.ContainsKey('engine') -and $perEngine.ContainsKey('legacy')) {
    CK ("B " + $c.n + "：引擎路线与 legacy 的分辨率一致（不许一条降一档）") `
      ($perEngine['engine'] -eq $perEngine['legacy']) "engine=$($perEngine['engine']) legacy=$($perEngine['legacy'])"
  }
}
# ⚠ B 段**条数下限**（2026-10-03 修 S2）：上面是动态枚举（2 档位 × {Xres/Yres 双标签, 引擎一致性}），
#   少一格（夹具缺 / 产物不唯一 / 循环被 continue 掉）都会让条数掉下去而 `$bad` 不变 ⇒ 静默假绿。
#   N = 6 = 2 个档位 × 3 条（engine X/Y、legacy X/Y、两路线一致）；实测本轮条数 = 6。
#   变异验证：把 `$dpiCases` 砍成一个空数组 ⇒ 旧写法 0 条断言、PASS=18 FAIL=0、exit 0（假绿）；
#     加下限后 ⇒ FAIL=1（"B 段断言条数下限"）并点名，exit 1 ⇒ 确实转红。
$minB = 6
$nB = @($Res | Where-Object { $_.n -like 'B *' }).Count
CK ("B 段断言条数下限（≥ $minB：2 档位 × {Xres/Yres 双标签, 引擎 vs legacy 一致}）") ($nB -ge $minB) "实得 $nB 条"
''
$bad = @($Res | Where-Object { -not $_.ok }).Count
'---- 取向标签 × 免解码重封装（产物级；被测档见首行 SUBJ）: PASS={0} FAIL={1} ----' -f ($Res.Count - $bad), $bad
if ($bad -gt 0) {
  Write-Output '--- 未通过明细 ---'
  foreach ($x in @($Res | Where-Object { -not $_.ok })) { Write-Output ('   ' + $x.n) }
  exit 1
} else { exit 0 }
