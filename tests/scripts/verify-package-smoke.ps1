# verify-package-smoke.ps1 —— **包级冒烟**（清单第 61 条；2026-10-01 由 `tests/output/t69/pkg-smoke3.ps1` 迁入）
#
# 判的是什么（都是"只有真工具链才判得出来"的形状，源码级门禁一律看不到）：
#   · JXR 交付位深：8-bit 源 ⇒ **24-bit BGR** 载体（旧行为是拿 48-bit 载体冒充），且
#     `--bit-depth 16` 那条升档臂**不是死分支**（8-bit 交付必须真小于 16-bit 交付）；
#   · 独立解码器 `JxrDecApp -c 0` 能不能把产物解出来（自家编码器"退出码 0"不等于产物可读）；
#   · `-e Ffmpeg` 后端的质量轴：同一 JPEG 的 `-q 30` 与 `-q 90` 必须**不同尺寸**
#     （旧行为是四档逐字节同尺寸 = 滑块是死的），且做不到 jbrd 时必须**点名**、命令行里
#     不许出现 `-distance 0` 冒充无损。
# ⚠ 被测两档见下方 `SUBJ` 段：默认 = 刚构建的 Release 产物（每轮），`$env:PKG_VER` 时升 L3 打出货包；
#   两档都缺 ⇒ `exit 2` 不产出读数，**绝不回退 Debug**。
# 退出码：0 = 全绿，1 = 有失败，2 = 被测二进制不在位/版本串不符。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（改用 「」）。
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$d    = "$root\tests\output\validate\l3_pkg_smoke"   # ⚠ 输出目录不能是被执行脚本自身所在的目录：
                           #   开头那句 Remove-Item 会把正在运行的 .ps1 自己删掉（t69 原版实测踩过）。
# ── 被测二进制两档（2026-10-01 接进门禁清单时从"只打出货包"改成两档）───────────────────
#   默认 = 本轮**刚构建的 Release 产物** ⇒ 每轮都能跑（判据等级 L2+：真产物、逐字节差分，但不是出货包）。
#   设 `$env:PKG_VER` 时升 **L3**：被测改成 `publish\build\FFmpegPictureUI-v<ver>-x64-full` 那份包内件。
#   ⚠ 两档都**缺即 exit 2、不产出任何读数**，且**绝不回退 Debug**（异构/陈旧二进制跑出来的读数不可信）。
#   ⚠ 包内版本串与期望不符同样 exit 2：目录写死 ⇒ "beta4 的读数打在 beta3 包上"，那就是一台假绿机器。
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
$ff   = "$plan\ffmpeg-full\ffmpeg.exe"
$cjxl = "$plan\jxl\bin\cjxl.exe"
$djxl = "$plan\jxl\bin\djxl.exe"
$jinfo= "$plan\jxl\bin\jxlinfo.exe"
if (Test-Path $d) { Remove-Item -Recurse -Force $d }
New-Item -ItemType Directory -Force -Path $d | Out-Null
$env:FFMPEGGUI_PLAN_DIR = $plan  # ⚠ 该变量要的是 PLAN 目录本身，不是包根（指到包根会让 artifacts/exiftool 探测落空）
$NL = [string][char]10
$W = 1200; $H = 1600
function KB($p){ if (Test-Path $p){ [math]::Round((Get-Item $p).Length/1024,1) } else { -1 } }
function Raw($p,$fmt,$o){ & $ff -y -hide_banner -loglevel error -i $p -vf "format=$fmt" -f rawvideo $o | Out-Null }
function Same($a,$b){ (Get-FileHash $a -Algorithm SHA256).Hash -eq (Get-FileHash $b -Algorithm SHA256).Hash }
$R = @()
function CK($name,$ok,$detail){ $script:R += [pscustomobject]@{n=$name;ok=[bool]$ok}; '{0}  {1}   {2}' -f $(if($ok){'PASS'}else{'FAIL'}),$name,$detail }
function Run($tag,$inF,$fmt,$extra){
  $o = "$d\o_$tag"; New-Item -ItemType Directory -Force -Path $o | Out-Null
  $a = @('--headless','-i',$inF,'--format',$fmt,'--output',$o,'--log-level','Debug') + $extra
  $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput "$o.o" -RedirectStandardError "$o.e"
  $log = [IO.File]::ReadAllText("$o.o") + [IO.File]::ReadAllText("$o.e")
  $f = @(Get-ChildItem $o -Recurse -File | Where-Object { $_.Extension -eq ".$fmt" })
  if ($f.Count -ne 1) { throw "$tag 产物数=$($f.Count)" }
  return @{ code=$p.ExitCode; file=$f[0]; log=$log }
}
# ── 否定式判据的「确实读到了」前置（2026-10-03 修 S3；口径照 `verify-jbrd-e2e.ps1:51-68` 的 CmdOf + Seen）──
#   `$log` 为空（日志捕获口径变了 / 产品不再回显命令行 / 重定向文件被截短 / PS5.1 编码差异）时
#   `-notmatch` **恒真** ⇒ "日志里不该出现 X" 这类断言无条件 PASS。本文件 B4/C1/C2/E3 原先是
#   **裸否定式**，正是这个形状 ⇒ 一次捕获失败 = 四条同时假绿。⇒ 否定式必须与"取到了命令行/日志"
#   这个前置**合取**，取不到就记红并点名（取不到 ≠ 通过）。
function CmdOf($log, $tool) {
  # ⚠ 两趟取：先只认**产品自己报出来的命令行**（`[cmd] …` / `… 命令行: …`）；取不到才退回
  #   "日志里形似命令行的行"。为什么不能一把梭再取末条：实测 `-e Ffmpeg` 那格日志里
  #   ffmpeg 的**横幅行** `  configuration: --prefix=/home/dominic/ffmpeg/bin --bindir=…`
  #   也含 `ffmpeg` + `--` ⇒ 会被当成命令行排在末条 ⇒ 前置看着"取到了"，其实取到的是横幅。
  $ls = @(($log -split "`r?`n") | Where-Object { ($_ -match "$tool 命令行") -or ($_ -match "\[cmd\].*?$tool") })
  if (-not $ls.Count) {
    $ls = @(($log -split "`r?`n") | Where-Object { ($_ -match "$tool(\.exe)?\s+`"") -or ($_ -match "$tool(\.exe)?\s+--") })
  }
  if (-not $ls.Count) { return '' }
  $line = $ls[-1]
  $line = $line -replace '^.*?命令行[:：]?\s*', ''
  $line = $line -replace '^.*?\[cmd\]\s*', ''
  $line = $line -replace ('^\[' + $tool + '\]\s*'), ''
  return $line.Trim()
}
function Seen($s) { return ($s -and $s.Trim().Length -gt 0) }
$i = Get-Item $exe
"包内 exe: {0}  ProductVersion={1}  sha={2}" -f $i.Directory.Name, $i.VersionInfo.ProductVersion,
    ((Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0,16))
''
& $ff -y -hide_banner -loglevel error -f lavfi -i "gradients=s=${W}x${H}:c0=0x305090:c1=0xd0a040:n=7,noise=alls=10:allf=t,format=rgb24" -frames:v 1 "$d\s8.png" | Out-Null
& $ff -y -hide_banner -loglevel error -f lavfi -i "gradients=s=${W}x${H}:c0=0x305090:c1=0xd0a040:n=7,noise=alls=6:allf=t,format=rgb24"   -frames:v 1 -q:v 4 "$d\s.jpg" | Out-Null
$pngKB = KB "$d\s8.png"; $jpgKB = KB "$d\s.jpg"
"夹具: 8bit PNG = $pngKB kB   JPEG = $jpgKB kB"
''
'--- A) 8-bit 源 -> JXL 无损：交付档 / 逐比特 / 载体白涨 ---'
$a = Run 'a' "$d\s8.png" 'jxl' @('--lossless','true')
& $djxl $a.file.FullName "$d\a_back.png" 2>&1 | Out-Null
Raw "$d\s8.png" 'rgb24' "$d\a_src.raw"; Raw "$d\a_back.png" 'rgb24' "$d\a_out.raw"
$infoA = ((& $jinfo $a.file.FullName 2>&1) | Select-String '\d+-bit' | Select-Object -First 1).ToString().Trim()
CK 'A1 交付 8-bit（不再是 16-bit 载体）' ($infoA -match '8-bit') $infoA
CK 'A2 往返逐比特相同（无损仍是无损）'   (Same "$d\a_src.raw" "$d\a_out.raw") "exit=$($a.code) 产物=$(KB $a.file.FullName) kB"
# A3 用**内容无关**的对照：同一份像素手工按 16-bit 载体编码的体积。
# （"产物 < 源 PNG"是越界判据：无损 JXL 是否胜过 PNG 取决于内容，均匀噪声图正是 PNG 的强项，
#   那既不是缺陷也不是本修复的承诺。承诺只有一条：16-bit 载体那 +76% 的白涨要没了。）
& $ff -y -hide_banner -loglevel error -i "$d\s8.png" -vf format=rgb48le -compression_level 0 -f image2pipe -c:v ppm "$d\a16.ppm" | Out-Null
& $cjxl "$d\a16.ppm" "$d\a16.jxl" -d 0 -e 7 2>&1 | Out-Null
$kb16 = KB "$d\a16.jxl"; $kbNow = KB $a.file.FullName
CK 'A3 16-bit 载体那笔白涨已消除（<= 手工 16bit 对照）' ($kbNow -le $kb16) `
   "包内 $kbNow kB vs 16-bit 载体对照 $kb16 kB（源 PNG $pngKB kB，内容相关不作判据）"
''
'--- B) JPEG -> JXL：默认就走 jbrd 重封装 ---'
$b = Run 'b' "$d\s.jpg" 'jxl' @()
$infoB = ((& $jinfo $b.file.FullName 2>&1) | Select-String 'reconstruction|\d+-bit') -join ' / '
$bl = (($b.log -split $NL) | Where-Object { $_ -match '--lossless_jpeg=1' } | Select-Object -First 1)
CK 'B1 命令行发出 --lossless_jpeg=1'     ($null -ne $bl) `
   ("{0} kB | {1}" -f (KB $b.file.FullName), $(if($bl){$bl.Trim().Substring(0,[Math]::Min(110,$bl.Trim().Length))}else{'<无>'}))
CK 'B2 产物真是 jbrd 比特流重建'          ($infoB -match 'reconstruction') $infoB
CK 'B3 重封装产物小于源 JPEG'             ($b.file.Length -lt (Get-Item "$d\s.jpg").Length) "$((KB $b.file.FullName)) kB < $jpgKB kB"
$c = Run 'c' "$d\s.jpg" 'jxl' @('--jxl-lossless-jpeg','false')
$ccmd = CmdOf $c.log 'cjxl'
CK 'B4 负控：显式关掉 => 不再发 =1'       ((Seen $ccmd) -and ($c.log -notmatch '--lossless_jpeg=1')) `
   ("{0} kB（像素重编码路线）｜前置: {1}" -f (KB $c.file.FullName), $(if (Seen $ccmd) { '取到 cjxl 命令行 ' + $ccmd.Substring(0, [Math]::Min(90, $ccmd.Length)) } else { 'FAIL 前置：cjxl 命令行没取到 ⇒ 否定式判据无从成立' }))
''
'--- C) 自动光子噪声那三项回归 ---'
$e = Run 'e' "$d\s8.png" 'jxl' @('--cjxl-auto-photon-noise','true','--lossless','true')
$ecmd = CmdOf $e.log 'cjxl'
CK 'C1 命令行不含 =auto'                  ((Seen $ecmd) -and ($e.log -notmatch 'photon_noise_iso=auto')) `
   ("前置: {0}" -f $(if (Seen $ecmd) { '取到 cjxl 命令行 ' + $ecmd.Substring(0, [Math]::Min(90, $ecmd.Length)) } else { 'FAIL 前置：cjxl 命令行没取到 ⇒ 否定式判据无从成立' }))
CK 'C2 无「传输错误」/无「超时」'          ((Seen $e.log) -and ($e.log -notmatch '传输错误') -and ($e.log -notmatch '超时或已取消')) `
   ("exit=$($e.code)｜前置: {0}" -f $(if (Seen $e.log) { '日志非空' } else { 'FAIL 前置：日志为空 ⇒ 否定式判据无从成立' }))
CK 'C3 产物非空'                          ($e.file.Length -gt 0) "$((KB $e.file.FullName)) kB"
''
'--- D) JXR 出口：交付档取自目标位深（不再是 16-bit 中间件）---'
$pk = "$plan\exiftool\exiftool.exe"
$jd = "$plan\artifacts\JxrDecApp.exe"
function JxrPF($f) {
  $null = Start-Process -FilePath $pk -ArgumentList @('-a','-s','-G1','-PixelFormat', $f) -NoNewWindow -Wait -RedirectStandardOutput "$d/_pf.txt"
  return ([IO.File]::ReadAllText("$d/_pf.txt")).Trim()
}
function JxrOK($f, $c, $o) {
  if (Test-Path $o) { Remove-Item $o -Force }
  $null = Start-Process -FilePath $jd -ArgumentList @('-i', $f, '-o', $o, '-c', $c) -NoNewWindow -Wait -RedirectStandardOutput "$d/_jd.txt"
  return (Test-Path $o)
}
$g1 = Run 'g1' "$d\s8.png" 'jxr' @()
$pf1 = JxrPF $g1.file.FullName
CK 'D1 8-bit 源 ⇒ 交付 24-bit BGR（旧行为是 48-bit 载体）' ($pf1 -match '24-bit BGR') $pf1
CK 'D2 独立解码器 -c 0 能解出' (JxrOK $g1.file.FullName '0' "$d\g1.bmp") 'JxrDecApp -c 0'
$g2 = Run 'g2' "$d\s8.png" 'jxr' @('--bit-depth','16')
$pf2 = JxrPF $g2.file.FullName
CK 'D3 --bit-depth 16 ⇒ 交付 48-bit RGB（升档臂不是死分支）' ($pf2 -match '48-bit RGB') $pf2
CK 'D4 同素材：8-bit 交付 < 16-bit 交付' ($g1.file.Length -lt $g2.file.Length) "$((KB $g1.file.FullName)) kB < $((KB $g2.file.FullName)) kB"
''
'--- E) -e Ffmpeg 后端：质量档没被 -distance 0 吞掉 ---'
$h1 = Run 'h1' "$d\s.jpg" 'jxl' @('-e','Ffmpeg','-q','30')
$h2 = Run 'h2' "$d\s.jpg" 'jxl' @('-e','Ffmpeg','-q','90')
CK 'E1 同一 JPEG 的两档产物**不同尺寸**（旧行为四档逐字节同尺寸）' ($h1.file.Length -ne $h2.file.Length) "q30=$((KB $h1.file.FullName)) kB vs q90=$((KB $h2.file.FullName)) kB"
CK 'E2 做不到 jbrd 由日志点名，不静默' ($h1.log -match 'jbrd') ''
$hcmd = CmdOf $h1.log 'ffmpeg'
CK 'E3 命令行不含 -distance 0 的冒充' ((Seen $hcmd) -and ($h1.log -notmatch '-d 0') -and ($h1.log -notmatch '-distance 0')) `
   ("前置: {0}" -f $(if (Seen $hcmd) { '取到 ffmpeg 命令行 ' + $hcmd.Substring(0, [Math]::Min(90, $hcmd.Length)) } else { 'FAIL 前置：ffmpeg 命令行没取到 ⇒ 否定式判据无从成立' }))
$bad = @($R | Where-Object { -not $_.ok }).Count
'---- 包级冒烟: PASS={0} FAIL={1} ----' -f ($R.Count - $bad), $bad
# ⚠ 显式 exit 0（2026-10-04 修 L1）：旧写法只有 `if ($bad -gt 0) { exit 1 }`，成功路径靠"尾随隐式 0"，
#   一旦将来在其后插入任何语句（或尾随表达式返回非零），绿就会被静默改判 ⇒ 补显式出口。
if ($bad -gt 0) { exit 1 } else { exit 0 }
