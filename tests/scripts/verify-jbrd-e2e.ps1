# verify-jbrd-e2e.ps1 —— JPEG ↔ JPEG XL 无损互转换：**产物级**端到端判据（清单第 62 条；
#   2026-10-01 由 `tests/output/t76/jbrd-e2e.ps1` 迁入。原首行写"被测 = 出货包内 exe"，迁入后是**两档**，
#   见下方 `SUBJ` 段：默认打刚构建的 Release 产物，设 `$env:PKG_VER` 才升 L3 打出货包）
# 判据读的是产物（ffmpeg 解码 raw 的 SHA256 / jxlinfo / exiftool / ffprobe pix_fmt），不读自家日志措辞。
# 每组都配"必须不成立"的反真空臂。
# 退出码：0 = 全绿，1 = 有失败，2 = 被测二进制不在位 / 包内版本串与期望不符（拒绝跑，不产出读数）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（改用 「」）。
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# ── 被测二进制两档（2026-10-01 接进门禁清单时从"只打出货包"改成两档）───────────────────
#   默认 = 本轮**刚构建的 Release 产物** ⇒ 每轮都能跑（L2+）；设 `$env:PKG_VER` 时升 **L3** 打出货包。
#   ⚠ 两档都**缺即 exit 2、不产出任何读数**，且**绝不回退 Debug**；包内版本串与期望不符同样 exit 2
#     （目录写死 ⇒ "beta4 的读数打在 beta3 包上"，那就是一台假绿机器）。
#   ⚠ `T76_EXE` 只换被测产品二进制，PLAN 工具链仍取上面解析出的那一份（用于迭代时验"修复是否生效"）。
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
if ($env:T76_EXE) { $exe = $env:T76_EXE }
$ff = "$plan\ffmpeg-full\ffmpeg.exe"; $fp = "$plan\ffmpeg-full\ffprobe.exe"
$et = "$plan\exiftool\exiftool.exe"; $cjxl = "$plan\jxl\bin\cjxl.exe"
$djxl = "$plan\jxl\bin\djxl.exe"; $jinfo = "$plan\jxl\bin\jxlinfo.exe"
foreach ($p in @($ff, $fp, $et, $cjxl, $djxl, $jinfo, $exe)) {
  if (-not (Test-Path $p)) { throw "缺工具：$p" }
}
$d = "$root\tests\output\validate\l3_jbrd"
if (Test-Path $d) { Remove-Item -Recurse -Force $d }
New-Item -ItemType Directory -Force -Path $d | Out-Null
$env:FFMPEGGUI_PLAN_DIR = "$plan"
$R = @()
function CK($name, $ok, $detail) {
  $script:R += [pscustomobject]@{ n = $name; ok = [bool]$ok }
  '{0}  {1}   {2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail
}
function X($file, $argStr, $tag) {
  $o = "$d\x_$tag.o"; $e = "$d\x_$tag.e"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = ([IO.File]::ReadAllText($o) + [IO.File]::ReadAllText($e)) }
}
# ── 取"编码器实际收到的命令行"：⚠ 两种后端两种标签 ──
#   引擎路线打 `[色彩] cjxl 命令行: …`，直连路线打 `[cmd] "…cjxl.exe" …`。
#   只认前者 ⇒ 直连那几格取到**空串**，而"不得含 --lossless_jpeg=1"这类**否定式**判据在空串上恒真
#   ⇒ 假绿。所以否定式必须与"取到了命令行"这个前置**合取**（Seen 就是为这件事写的）。
#   直连路线打 `[cjxl] cjxl.exe "输入" "输出" …`（**没有** "[cmd]" 前缀），管道/引擎路线才打 `[色彩] cjxl 命令行: …`
function CmdOf($log, $tool) {
  $ls = @(($log -split "`r?`n") | Where-Object {
    ($_ -match "$tool 命令行") -or
    ($_ -match "\[cmd\].*?$tool") -or
    ($_ -match "$tool(\.exe)?\s+`"") -or
    ($_ -match "$tool(\.exe)?\s+--") })
  if (-not $ls.Count) { return '' }
  $line = $ls[-1]
  $line = $line -replace '^.*?命令行[:：]?\s*', ''
  $line = $line -replace '^.*?\[cmd\]\s*', ''
  $line = $line -replace ('^\[' + $tool + '\]\s*'), ''
  return $line.Trim()
}
function Seen($cmd) { return ($cmd -and $cmd.Trim().Length -gt 0) }
function App($tag, $inF, $fmt, $extra) {
  $o = "$d\o_$tag"; New-Item -ItemType Directory -Force -Path $o | Out-Null
  $a = '--headless --log-level Debug -i "{0}" --format {1} --output "{2}" {3}' -f $inF, $fmt, $o, $extra
  $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$d\a_$tag.o" -RedirectStandardError "$d\a_$tag.e"
  $log = [IO.File]::ReadAllText("$d\a_$tag.o") + [IO.File]::ReadAllText("$d\a_$tag.e")
  $f = @(Get-ChildItem $o -Recurse -File | Where-Object { $_.Extension -eq ".$fmt" })
  # ⚠ 不 throw：一格里 0 产物/多产物必须**记成该格的 FAIL**，而不是让整轮冒烟中断
  #   （本轮第一版就是 throw 掉后面四段，导致 B~F 一个都没跑）。
  $file0 = $null
  if ($f.Count -ge 1) { $file0 = $f[0] }
  return @{ code = $p.ExitCode; file = $file0; count = $f.Count; log = $log }
}
function RawHash($img, $tag) {
  $out = "$d\raw_$tag.raw"
  $null = X $ff ('-y -hide_banner -loglevel error -i "' + $img + '" -vf format=rgb24 -f rawvideo "' + $out + '"') "raw_$tag"
  if (-not (Test-Path $out)) { return '<undecodable>' }
  return (Get-FileHash $out -Algorithm SHA256).Hash.Substring(0, 16)
}
function FileHash($p) { if (-not (Test-Path $p)) { return '<无>' }; return (Get-FileHash $p -Algorithm SHA256).Hash.Substring(0, 16) }
function Pix($img, $tag) {
  $r = X $fp ('-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 "' + $img + '"') "px_$tag"
  return (($r.text -split "`r?`n" | Where-Object { $_ -match '^[a-z0-9]+$' } | Select-Object -First 1))
}
function TagSet($img, $tag) {
  # 空/缺路径先回哨兵：拼进命令行会炸成"路径语法不正确"，那种红会被误读成产品坏了
  if ([string]::IsNullOrWhiteSpace($img) -or (-not (Test-Path -LiteralPath $img))) { return '<无文件>' }
  # 用已经跑通的 X 助手取数（合并 stdout+stderr），再按行滤掉 exiftool 内嵌 perl 的 locale 警告
  # ⚠ 标签名必须用 **CamelCase**：`-icc_profile:color_space_data`（下划线式）实测**静默返回空**、
  #   连警告都没有，而 `-icc_profile:ColorSpaceData` 正常命中 `[ICC-header] Color Space Data : RGB`。
  #   我前一次把它读成"ICC 没写进去/随往返丢了"，其实是**读法不存在**（同一命令对已知存在的文件量到过一次才算数）。
  $r = X $et ('-S -a -G1 -icc_profile:ColorSpaceData -exif:make -exif:model -exif:orientation "' + $img + '"') "tgs_$tag"
  $keep = @($r.text -split "`r?`n" | Where-Object { $_.Trim() -ne '' -and $_ -notmatch 'warning|locale|LANG|LC_ALL|supported and installed|Falling back' })
  return (($keep | Sort-Object) -join ' | ')
}
function CodedDim($img, $tag) {
  # 码流尺寸（不应用取向）⇒ 判"像素有没有被旋正"只能用这个，不能用哈希，也不能用 ffprobe 的显示口径
  if ([string]::IsNullOrWhiteSpace($img) -or (-not (Test-Path -LiteralPath $img))) { return '' }
  $r = X $fp ('-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 "' + $img + '"') "cd_$tag"
  $one = @($r.text -split "`r?`n" | Where-Object { $_ -match '^[0-9]+,[0-9]+$' } | Select-Object -First 1)
  return $(if ($one.Count) { $one[0] } else { '' })
}
''
'=== 夹具构造（ffmpeg 与 Pillow：色度/进度/元数据/增益图 各不相同）==='
$made = [ordered]@{}
$gen = [ordered]@{
  'baseline420' = '-y -hide_banner -loglevel error -f lavfi -i gradients=s=640x480:c0=0x204060:c1=0xc07030:n=5,noise=alls=8:allf=t,format=yuv420p -frames:v 1 -q:v 3 "{OUT}"'
  'chroma444'   = '-y -hide_banner -loglevel error -f lavfi -i gradients=s=640x480:c0=0x204060:c1=0xc07030:n=5,noise=alls=8:allf=t,format=rgb24 -frames:v 1 -pix_fmt yuvj444p -q:v 3 "{OUT}"'
  'chroma422'   = '-y -hide_banner -loglevel error -f lavfi -i gradients=s=640x480:c0=0x204060:c1=0xc07030:n=5,noise=alls=8:allf=t,format=rgb24 -frames:v 1 -pix_fmt yuvj422p -q:v 3 "{OUT}"'
  'grayscale'   = '-y -hide_banner -loglevel error -f lavfi -i gradients=s=640x480:c0=0x202020:c1=0x909090:n=5,format=gray -frames:v 1 -q:v 3 "{OUT}"'
  'large2400'   = '-y -hide_banner -loglevel error -f lavfi -i gradients=s=2400x1600:c0=0x105070:c1=0xd0b040:n=6,noise=alls=8:allf=t,format=yuv420p -frames:v 1 -q:v 4 "{OUT}"'
}
foreach ($k in $gen.Keys) {
  $out = "$d\src_$k.jpg"
  $null = X $ff ($gen[$k].Replace('{OUT}', $out)) "gen_$k"
  if (Test-Path $out) { $made[$k] = $out } else { CK "夹具 $k 生成" $false 'ffmpeg 未产出' }
}
# Pillow：progressive（本构建的 ffmpeg mjpeg 编码器写不出 progressive ⇒ 这个形态此前根本没进过判据）
$py = @"
from PIL import Image, ImageDraw
import sys
im = Image.new('RGB', (640, 480))
dr = ImageDraw.Draw(im)
for y in range(480):
    dr.line((0, y, 640, y), fill=(20 + (y % 200), 60, 90 + (y % 120)))
im.save(sys.argv[1], 'JPEG', quality=92, progressive=True, subsampling=0)
im2 = Image.new('RGB', (320, 240))
d2 = ImageDraw.Draw(im2)
for x in range(320):
    d2.line((x, 0, x, 240), fill=(x % 255, 120, 200 - (x % 120)))
im2.save(sys.argv[2], 'JPEG', quality=90, subsampling=2)
"@
[IO.File]::WriteAllText("$d\mk.py", $py)
$null = X 'py' ('"' + "$d\mk.py" + '" "' + "$d\src_progressive.jpg" + '" "' + "$d\src_420tight.jpg" + '"') 'gen_pil'
if (Test-Path "$d\src_progressive.jpg") { $made['progressive'] = "$d\src_progressive.jpg" } else { CK '夹具 progressive 生成' $false 'Pillow 未产出' }
# 带 ICC + EXIF 的 JPEG（exiftool 写入；jbrd 应当原样保留元数据）
$iccJpg = "$d\src_icc_exif.jpg"
$gIcc = X $ff ('-y -hide_banner -loglevel error -f lavfi -i gradients=s=640x480:c0=0x204060:c1=0xc07030:n=5,noise=alls=8:allf=t,format=yuv420p -frames:v 1 -q:v 3 "' + $iccJpg + '"') 'gen_icc0'
# ⚠ 生成失败必须**响亮**记一条：09-30 实测这一格在**两轮**里都静默没产出（`if (Test-Path)` 直接跳过），
#   于是 C) 段的 icc_exif 臂从来没跑过，而日志里一条红都没有 ⇒ 这是自建 harness 的假绿口。
if (-not (Test-Path $iccJpg)) {
  CK '夹具 icc_exif 生成' $false ("exit=$($gIcc.code) 未产出；输出: " + (($gIcc.text -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -First 2) -join ' / '))
} else {
  # ⚠ `"-ICC_PROFILE<=…"` 这种**整体加引号**的形态实测写不进去（Make/Model 落了、ICC 没落，09-30 复现）；
  #   不带外层引号的形态在独立手测里能写成并读回 `[ICC-header] Color Space Data : RGB`。路径无空格 ⇒ 用后者。
  $null = X $et ('-overwrite_original -ICC_PROFILE<=' + ($root.Replace('\', '/') + '/tests/output/sources/srgb.icc') + ' -Make=TestCam -Model=TestLens -Orientation=8 -n "' + $iccJpg + '"') 'gen_icc1'
  $tagProbe = TagSet $iccJpg 'probe'
  # 两个维度**分别**验到（ICC 与 EXIF）：原来 `'ICC|Make|Orientation'` 只要 Make 命中就算过 ⇒ ICC 没落地也绿
  if (($tagProbe -match 'ColorSpaceData|Color Space') -and ($tagProbe -match 'Make|Model')) { $made['icc_exif'] = $iccJpg; 'icc_exif 标签写入后：' + $tagProbe } else { CK '夹具 icc_exif 元数据写入' $false "写入后读不到（$tagProbe）" }
}
# Ultra HDR JPEG（由被测产物自己造：HDR 源 + --jpeg-gain-map true ⇒ 带 MPF/APP2 的 JPEG）
$hdrSrc = "$root\tests\output\sources\src_hdr_pq.png"
if (Test-Path $hdrSrc) {
  try {
    $u = App 'gen_uhdr' $hdrSrc 'jpg' '--jpeg-gain-map true'
    $made['ultrahdr'] = $u.file.FullName
    'ultrahdr 夹具 = ' + $u.file.FullName + ' (' + $u.file.Length + ' B)'
  } catch { CK '夹具 ultrahdr 生成' $false $_.Exception.Message }
}
$photo = "$root\tests\output\sources\src_photo.jpg"
if (Test-Path $photo) {
  # ⚠ 先复制到 $d 再打元数据：src_photo.jpg 是**共享夹具**，原地 exiftool 会改字节、波及别的门禁
  $photoLocal = "$d\src_photo_local.jpg"
  Copy-Item -LiteralPath $photo -Destination $photoLocal -Force
  $made['photoicc'] = $photoLocal
}
''
'=== 夹具统一打元数据（ICC + Make + Model）⇒ "元数据标签集不变"这一格才有牙 ==='
# 为什么每个夹具都要打：此前只在 icc_exif 一格打了 ⇒ 其余 7 格两侧标签恒空，那一格**没有判别力**
#   （本轮实测按"未判"记红，正是这个原因）。判据要落在全线，而不是落在专门做它的那一格。
# 增益图夹具**跳过**：exiftool 往带 MPF 的 JPEG 里插 APP1 会让 MPF 子图指针失准 ⇒ 该格按 SKIP 记，
#   不混进 PASS/FAIL（拿被改坏的夹具去验增益图路线，比缺一条判据更糟）。
$metaKeys = @()
foreach ($k in @($made.Keys)) {
  if ($k -eq 'ultrahdr') { continue }
  $p = $made[$k]
  $null = X $et ('-overwrite_original -ICC_PROFILE<=' + ($root.Replace('\', '/') + '/tests/output/sources/srgb.icc') + ' -Make=TestCam -Model=TestLens "' + $p + '"') "stamp_$k"
  $t = TagSet $p "stampchk_$k"
  # ⚠ 逐项验，不接受"命中其一"：ICC（Color Space Data）与 EXIF（Make/Model）**两个维度都必须在位**，
  #   否则这一格只验到了 EXIF，ICC 那一路仍是"没判过"。
  if (($t -match 'ColorSpaceData|Color Space') -and ($t -match 'Make|Model')) { $metaKeys += $k; '  {0,-14} 标签集=[{1}]' -f $k, $t }
  else { CK "夹具 $k 元数据写入" $false "写入后读不到（[$t]）" }
}
foreach ($k in $made.Keys) { '  {0,-14} {1,9} B' -f $k, (Get-Item $made[$k]).Length }
# ⚠ 夹具**集合**本身要判：少一种形态 ⇒ 该形态的所有判据静默不跑（icc_exif 就是这么连漏两轮、零红）。
$want = @('baseline420','chroma444','chroma422','grayscale','large2400','progressive','icc_exif','photoicc','ultrahdr')
$lack = @($want | Where-Object { -not $made.Contains($_) })
CK ('夹具集合完整（应 {0} 种形态）' -f $want.Count) ($lack.Count -eq 0) $(if ($lack.Count) { '缺: ' + ($lack -join ',') } else { '齐' })
''
'=== A) 每种输入形态：JPEG -> JXL -> JPEG，判"还原到什么程度" ==='
foreach ($k in $made.Keys) {
  $src = $made[$k]
  $hSrc = RawHash $src "A_${k}_s"; $pSrc = Pix $src "A_${k}_spx"
  $j = App "A1_$k" $src 'jxl' ''
  if ($j.count -ne 1) { CK ("A " + $k + "：正向有且仅有一个产物") $false ("产物数=" + $j.count + " rc=" + $j.code); continue }
  $ji = (X $jinfo ('"' + $j.file.FullName + '"') "A_ji_$k").text
  $cjxlCmd = CmdOf $j.log 'cjxl'
  if ($k -eq 'ultrahdr') {
    # ── 增益图 JPEG 这一格判的是"**不静默**"，不是"有没有重构盒"（10-01 修法后的契约）──
    #   实况：cjxl 收到 --lossless_jpeg=1 时不报错，而是解成 HDR float 重编码 ⇒ 既无重构盒、
    #   "无损"又是假的；且该 float 产物在修之前连反向都取不回来（djxl PPM 报 BitDepth failed、0 产物）。
    #   ⇒ 现在必须同时看到：命令行发的是 =0、日志点名了原因、反向出得来产物、且我们不假装逐比特相同。
    CK ("A " + $k + "：正向产物**不含**重构盒（结构上不可能，与宣称一致）") ($ji -notmatch 'reconstruction') `
      (($ji -split "`r?`n" | Where-Object { $_ -match 'JPEG XL image|reconstruction' }) -join ' / ')
    # ⚠ 实况修正（10-01 第三轮）：增益图 JPEG 在进 cjxl **之前**就被 GainMapDecoder 处理成
    #   `uhdr_linear_*.pfm` 之类的中间文件 ⇒ cjxl 拿到的根本不是 JPEG，`--lossless_jpeg` 这条开关
    #   在这条路上**不会出现**。所以正确的判据不是"必须看到 =0"，而是
    #   "**任何一条** cjxl 命令里都不许出现 =1"（否则就是又嘴上 =1 手里重编码）。
    $anyJbrd = ($j.log -match '--lossless_jpeg=1')
    CK ("A " + $k + "：整条路上没有任何 cjxl 命令带 --lossless_jpeg=1") `
      ((Seen $cjxlCmd) -and (-not $anyJbrd)) `
      $(if (-not (Seen $cjxlCmd)) { 'FAIL 前置：cjxl 命令行没取到' } elseif ($anyJbrd) { ($j.log -split "`r?`n" | Where-Object { $_ -match '--lossless_jpeg=1' } | Select-Object -First 1) } else { '命令行取到且无 =1：' + ($cjxlCmd.Substring(0, [Math]::Min(110, $cjxlCmd.Length))) })
    CK ("A " + $k + "：增益图导致的不重封装被**点名**（不是静默）") ($j.log -match 'gain map|增益图') `
      ((($j.log -split "`r?`n") | Where-Object { $_ -match 'gain map|增益图' } | Select-Object -First 1) -as [string])
    $b = App "A2_$k" $j.file.FullName 'jpg' ''
    CK ("A " + $k + "：反向有且仅有一个产物（float JXL 取不回来 = 修前的红）") ($b.count -eq 1) `
      ("产物数=" + $b.count + " rc=" + $b.code)
    if ($b.count -eq 1) {
      CK ("A " + $k + "：反向改道被点名（djxl PPM 吃不下 float ⇒ 走 ffmpeg 直读）") `
        ($b.log -match 'ffmpeg 直读|PNM 中转失败') `
        ((($b.log -split "`r?`n") | Where-Object { $_ -match 'ffmpeg 直读|PNM 中转失败' } | Select-Object -First 1) -as [string])
      $hRt = RawHash $b.file.FullName "A_${k}_r"; $pRt = Pix $b.file.FullName "A_${k}_rpx"
      CK ("A " + $k + "：反真空——这条路**不该**逐比特相同（相同即为假无损）") ($hRt -ne $hSrc) "src=$hSrc rt=$hRt（$pSrc -> $pRt）"
    }
    continue
  }
  CK ("A " + $k + "：正向产物含 jbrd 重构盒") ($ji -match 'reconstruction') (($ji -split "`r?`n" | Where-Object { $_ -match 'JPEG XL image|reconstruction' }) -join ' / ')
  $b = App "A2_$k" $j.file.FullName 'jpg' ''
  if ($b.count -ne 1) { CK ("A " + $k + "：反向有且仅有一个产物") $false ("产物数=" + $b.count + " rc=" + $b.code + "（djxl 路线失败）"); continue }
  $hRt = RawHash $b.file.FullName "A_${k}_r"; $pRt = Pix $b.file.FullName "A_${k}_rpx"
  CK ("A " + $k + "：往返**像素**逐比特相同") ($hRt -eq $hSrc) "src=$hSrc rt=$hRt（$pSrc -> $pRt）"
  CK ("A " + $k + "：往返 JPEG 与源**逐字节**相同") ((FileHash $b.file.FullName) -eq (FileHash $src)) "src=$(FileHash $src) rt=$(FileHash $b.file.FullName)"
  CK ("A " + $k + "：色度采样/编码模式未被改") ($pRt -eq $pSrc) "$pSrc -> $pRt"
  if ($metaKeys -contains $k) {
    $tS = TagSet $src "A_${k}_t"; $tR = TagSet $b.file.FullName "A_${k}_tr"
    # ⚠ 两边都空 = 这条素材根本没有元数据 ⇒ 判据无从判别，**按未判处理记 FAIL**（不许蒙成绿）
    $tagVacuous = (($tS -eq '') -and ($tR -eq ''))
    CK ("A " + $k + "：元数据标签集不变") $((-not $tagVacuous) -and ($tS -eq $tR)) $(if ($tagVacuous) { '两侧标签集均空 ⇒ 该格无判别力，记未判' } else { "src=[$tS] || rt=[$tR]（A 走 jbrd，逐字节全等已蕴含此条，留作定位）" })
  } else {
    '  SKIP  A ' + $k + '：元数据标签集（该夹具刻意不带元数据，见上方增益图夹具说明）'
  }
}
''
'=== B) 反真空：同素材关掉 jbrd ⇒ A 的两条硬判据必须成立不了 ==='
foreach ($k in $made.Keys) {
  $src = $made[$k]
  $hSrc = RawHash $src "B_${k}_s"
  $j = App "B1_$k" $src 'jxl' '--jxl-lossless-jpeg false'
  if ($j.count -ne 1) { CK ("B " + $k + "：正向有且仅有一个产物") $false ("产物数=" + $j.count); continue }
  $ji = (X $jinfo ('"' + $j.file.FullName + '"') "B_ji_$k").text
  CK ("B " + $k + "：关掉后不含重构盒") ($ji -notmatch 'reconstruction') (($ji -split "`r?`n" | Where-Object { $_ -match 'JPEG XL image' } | Select-Object -First 1))
  $b = App "B2_$k" $j.file.FullName 'jpg' ''
  if ($b.count -ne 1) { CK ("B " + $k + "：反向有且仅有一个产物") $false ("产物数=" + $b.count); continue }
  $hRt = RawHash $b.file.FullName "B_${k}_r"
  CK ("B " + $k + "：关掉后往返像素不再逐比特相同（A 有牙）") ($hRt -ne $hSrc) "src=$hSrc rt=$hRt"
  # ★ B 才是元数据判据**有牙**的那一格：关掉 jbrd ⇒ 走真·重编码，ICC/EXIF 完全可能在半路被丢；
  #   A 里字节全等时那条是恒成立的（由逐字节判据蕴含）。此前只有恒成立的那格，等于没验。
  if ($metaKeys -contains $k) {
    $tSb = TagSet $src "B_${k}_t"; $tRb = TagSet $b.file.FullName "B_${k}_tr"
    # ⚠ 取向**不能**混在"标签集相等"里判：重编码路上产品是"把旋转烘进像素 + 剥掉标签"（契约见
    #   `ExifToolService.cs:471-477`，实测 B 段产物 coded 640,480 → 480,640 且标签已剥），
    #   所以整组相等这条判据在这一路上必然红 —— 那是我的期望写错，不是产品坏。拆成两条独立的判据。
    $noS = ((($tSb -split ' \| ') | Where-Object { $_ -notmatch 'Orientation' }) -join ' | ')
    $noR = ((($tRb -split ' \| ') | Where-Object { $_ -notmatch 'Orientation' }) -join ' | ')
    # ⚠ ICC 不能混进"必须原样保留"这一条：sRGB 目标的**无标注是合法出口**（本仓既有裁定），
    #   判据只能打"**静默**无标注"。⇒ 拆成两条：EXIF 必须原样在；ICC 要么在、要么产品必须自己播报过降级。
    $exifS = ((($noS -split ' \| ') | Where-Object { $_ -notmatch 'ICC' }) -join ' | ')
    $exifR = ((($noR -split ' \| ') | Where-Object { $_ -notmatch 'ICC' }) -join ' | ')
    CK ("B " + $k + "：关 jbrd 重编码后 EXIF 标签集不变") (($exifS -ne '') -and ($exifS -eq $exifR)) "src=[$exifS] || rt=[$exifR]"
    $iccIn  = ($noS -match 'ColorSpaceData|Color Space')
    $iccOut = ($noR -match 'ColorSpaceData|Color Space')
    $announced = ($b.log -match '降级|无色彩标注|Unlabeled|不补 ICC|色彩后置')
    if (-not $iccIn) { '  SKIP  B ' + $k + '：ICC（源无 ICC，无从判保留）' }
    else {
      CK ("B " + $k + "：ICC 保留或**有播报地**降级（不许静默丢）") (($iccOut -or $announced)) `
        ("源有 ICC / 产物" + $(if ($iccOut) { '仍有 ICC' } { '无 ICC' }) + "，日志" + $(if ($announced) { '点名了降级' } else { '没点名（静默）' }))
    }
    # 取向自洽：**只有源带了非 1 的取向标签时**才有意义（源没标签 ⇒ 无从谈"剥掉/烘进"）。
    $dS = CodedDim $src ("B_" + $k + "_ds"); $dR = CodedDim $b.file.FullName ("B_" + $k + "_dr")
    $oKept = ($tRb -match 'Orientation')
    $srcHadOrient = ($tSb -match 'Orientation')
    $swapped = (($dS -ne '') -and ($dR -ne '') -and (($dS -split ',')[0] -ne ($dR -split ',')[0]))
    # ⚠ 只有**源带了取向标签**时这条才有意义：源本来没标签 ⇒ 既没什么可剥、也没什么可烘，判它红是我的错。
    if (-not $srcHadOrient) { '  SKIP  B ' + $k + '：取向自洽（源无取向标签，无从判"剥/烘"）' }
    else {
      CK ("B " + $k + "：取向自洽（留标签⇔码流尺寸未换 / 剥标签⇔码流尺寸已换）") `
        (($oKept -and -not $swapped) -or ((-not $oKept) -and $swapped)) `
        ("src coded=" + $dS + " rt coded=" + $dR + " 标签" + $(if ($oKept) { '留' } else { '剥' }))
    }
  }
}
''
'=== C) 参数冲突：无损重封装 × 强制色彩变换（jbrd 结构上不可能）=== '
foreach ($k in @('baseline420', 'icc_exif')) {
  if (-not $made.Contains($k)) { continue }
  $src = $made[$k]
  $c = App "C_$k" $src 'jxl' '--color-space "Display P3"'
  $cl = CmdOf $c.log 'cjxl'
  $ji = (X $jinfo ('"' + $c.file.FullName + '"') "C_ji_$k").text
  CK ("C " + $k + "：要变换时不发 --lossless_jpeg=1") ((Seen $cl) -and ($cl -notmatch '--lossless_jpeg=1')) `
    $(if (Seen $cl) { $cl -replace '^\s*', '' } else { 'FAIL 前置：命令行没取到 ⇒ 否定式判据无从成立' })
  CK ("C " + $k + "：产物确实无重构盒") ($ji -notmatch 'reconstruction') (($ji -split "`r?`n" | Where-Object { $_ -match 'JPEG XL image' } | Select-Object -First 1))
}
''
'=== D) 参数冲突：--lossless true × --jxl-lossless-jpeg false（显式互斥）==='
if ($made.Contains('baseline420')) {
  $l = App 'D_both' $made['baseline420'] 'jxl' '--lossless true --jxl-lossless-jpeg false'
  $ll = CmdOf $l.log 'cjxl'
  $lji = (X $jinfo ('"' + $l.file.FullName + '"') 'D_ji').text
  CK "D1 显式关掉 jbrd 时**不得**仍发 --lossless_jpeg=1" ((Seen $ll) -and ($ll -notmatch '--lossless_jpeg=1')) `
    $(if (Seen $ll) { $ll -replace '^\s*', '' } else { 'FAIL 前置：命令行没取到 ⇒ 否定式判据无从成立' })
  CK "D2 该组合的取舍被点名（不是静默改写）" (($l.log -match '无损') -and ($l.log -notmatch '^(?s).*静默.*$')) ''
  CK "D3 产物与命令一致（无重构盒 ⇔ 未发 =1）" (($lji -match 'reconstruction') -eq ($ll -match '--lossless_jpeg=1')) "jxlinfo=$(($lji -split "`r?`n" | Where-Object { $_ -match 'JPEG XL image|reconstruction' } | Select-Object -First 1))"
}
''
'=== E) 后端翻转：-e Ffmpeg（libjxl）不得声明 jbrd，且质量档必须活着 ==='
if ($made.Contains('baseline420')) {
  $d30 = App 'E_ff30' $made['baseline420'] 'jxl' '-e Ffmpeg -q 30'
  $d90 = App 'E_ff90' $made['baseline420'] 'jxl' '-e Ffmpeg -q 90'
  $dji = (X $jinfo ('"' + $d30.file.FullName + '"') 'E_ji').text
  CK "E1 -e Ffmpeg 不发 --lossless_jpeg=1" ($d30.log -notmatch '--lossless_jpeg=1') ''
  CK "E2 -e Ffmpeg 产物无重构盒" ($dji -notmatch 'reconstruction') (($dji -split "`r?`n" | Where-Object { $_ -match 'JPEG XL image' } | Select-Object -First 1))
  CK "E3 质量档真的改变了产物（不被常量吞）" ($d30.file.Length -ne $d90.file.Length) "q30=$($d30.file.Length) B vs q90=$($d90.file.Length) B"
  CK "E4 做不到 jbrd 由日志点名" ($d30.log -match '无损重封装|jbrd') ''
}
''
'=== G) 强制模块化 × jbrd：结构互斥必须点名，且 --modular 必须真的发到 cjxl ==='
# 动机（U5）：`--jxl-modular` 过去只在 ffmpeg/libjxl 路线落地，cjxl 路线 0 引用 ⇒
#   出货包实测"勾与不勾产物 SHA 完全相同"（9165 B / 97AFF2ED823C1FC2）—— 一个看起来能用的面板旋钮其实是死的。
#   现在既要它**发得出去**，又要它与 jbrd 的互斥被**说出来**（modular 模式装不下 JPEG 重构数据）。
if ($made.Contains('baseline420')) {
  $g1 = App 'G_modular' $made['baseline420'] 'jxl' '--jxl-modular true'
  $gcmd = CmdOf $g1.log 'cjxl'
  $gji = (X $jinfo ('"' + $g1.file.FullName + '"') 'G_ji').text
  CK "G1 --jxl-modular true 把 --modular=1 真发到了 cjxl" ((Seen $gcmd) -and ($gcmd -match '--modular=1')) `
    $(if (Seen $gcmd) { $gcmd -replace '^\s*', '' } else { 'FAIL 前置：cjxl 命令行没取到（capture 口径问题，不是产品没发）' })
  CK "G2 强制模块化时不发 --lossless_jpeg=1（结构上不能并存）" ((Seen $gcmd) -and ($gcmd -notmatch '--lossless_jpeg=1')) `
    $(if (Seen $gcmd) { $gcmd -replace '^\s*', '' } else { 'FAIL 前置：命令行没取到 ⇒ 否定式判据无从成立' })
  CK "G3 该互斥被点名（不是静默改写）" ($g1.log -match 'structurally exclusive|modular mode wins') `
    ((($g1.log -split "`r?`n") | Where-Object { $_ -match 'structurally exclusive|modular mode wins' } | Select-Object -First 1) -as [string])
  CK "G4 产物与命令一致：modular 路必无重构盒" ($gji -notmatch 'reconstruction') `
    (($gji -split "`r?`n" | Where-Object { $_ -match 'JPEG XL image|reconstruction' } | Select-Object -First 1))
  $g2 = App 'G_plain' $made['baseline420'] 'jxl' ''
  # 反真空：真发出去了就得有差别。若两条 SHA 相同 ⇒ 要么 cjxl 没理这个开关，要么接线是形式（都不能算过）
  CK "G5 反真空：不勾 modular 的产物与勾选的不同" ((FileHash $g1.file.FullName) -ne (FileHash $g2.file.FullName)) `
    "勾=$(FileHash $g1.file.FullName)($($g1.file.Length)B) 不勾=$(FileHash $g2.file.FullName)($($g2.file.Length)B)"
}
''
'=== F) 独立参照：手工 cjxl 造的正向产物（不经被测链路）也要能逐比特回去 ==='
if ($made.Contains('baseline420')) {
  $src = $made['baseline420']; $hSrc = RawHash $src "F_s"
  $null = X $cjxl ('"' + $src + '" "' + "$d\hand.jxl" + '" -d 0 --lossless_jpeg=1') 'F_hand'
  if (Test-Path "$d\hand.jxl") {
    $e = App 'F_rt' "$d\hand.jxl" 'jpg' ''
    CK "F 手工 jbrd 产物经被测反向路线逐比特还原" ((RawHash $e.file.FullName "F_r") -eq $hSrc) "src=$hSrc rt=$(RawHash $e.file.FullName "F_r2")"
    CK "F 手工 jbrd 产物与源逐字节相同" ((FileHash $e.file.FullName) -eq (FileHash $src)) "src=$(FileHash $src) rt=$(FileHash $e.file.FullName)"
  } else { CK 'F 手工 cjxl 参照' $false '未产出 hand.jxl' }
}
''
$bad = @($R | Where-Object { -not $_.ok }).Count
'---- JPEG<->JXL 无损互转换（产物级；被测档见首行 SUBJ）: PASS={0} FAIL={1} ----' -f ($R.Count - $bad), $bad
if ($bad -gt 0) {
  Write-Output '--- 未通过明细 ---'
  foreach ($x in @($R | Where-Object { -not $_.ok })) { Write-Output ('   ' + $x.n) }
  exit 1
} else { exit 0 }
