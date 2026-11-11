# verify-gainmap-jxl.ps1 —— JXL 增益图**写侧**（jhgm box）门禁（2026-11-08 新增）
#
# 背景：用户 2026-11-08 按「解析级标准」裁决开写侧（P2-1 原关闸解除，裁决记录见
#   docs/GAINMAP_ROUTES_AND_PATCH_PLAN.md P2-1 的 2026-11-08 条目）。写侧形状 =
#   SDR sRGB 底 + jhgm（version 0 + ISO 21496-1 blob + 无 alt color encoding/ICC + 增益图裸码流，
#   jhgm 在 jxlc 之前）—— 与 rawmakase PR #912 同形、与主 agent 的五组正控实验同构。
#
# 本门禁锁三件事：
#   §1 正控：产品 CLI（HDR 源 → -f jxl --jpeg-gain-map）产出带 jhgm 的容器；随包 jxlinfo 0.12.0
#      解析出 version=0 / 61-byte metadata（单通道 ISO 21496-1 blob）/ N-byte gain map；
#      ServiceProbe jxlgm（托管互验，21 条：盒序 jhgm<jxlc、ISO blob 与 BuildIso21496Metadata
#      逐字节相等——jxlinfo 看不见 blob 内容，这是解析级判据的核心一针——载荷逐项、负控三臂）；
#      djxl 解 jxlc 帧尺寸==源；cjxl 重编码透传。
#   §2 负控：坏载荷（BE16→0xFFFF）⇒ jxlinfo stderr「Invalid gain map box found」（解析器活着）；
#      无 jhgm 夹具 ⇒ jxlinfo 无 jhgm 行（不是见容器就报）；compose jxl **正控**（2026-11-11 修复批 A2
#      接线放开 jxl：产物带可解析 jhgm，旧负控「格式守卫先拦」随守卫放开而作废）；compose jxl+xmp ⇒
#      建队拒绝零产物（jhgm 无 XMP 形态）；compose jxl+--gainmap-encoder ⇒ 建队拒绝零产物（子图恒由
#      cjxl 编、无编码器选择面）；单输入 xmp+jxl ⇒ 运行时拒绝零产物；
#      both ⇒ 产物正常 + NOTE 降级 iso；bt2020 底 ⇒ D6 拒绝（v1 只支持 sRGB 底）。
#   §3 tripwire：jxl-oxide / zenjxl / jxl-rs CLI 任一出现在工具搜索面（publish/tools/PATH）⇒ 当场红，
#      要求按 2026-11-08 裁决升级双实现（渲染级）判据并重测能力表（形态对齐 verify-color-caps (h)）。
#
# 素材：tests/output/validate/src_hdr.png（ensure-fixtures.ps1 生成，缺则 fail-closed）；
#   SDR 对照用 ffmpeg lavfi 现造；tests/data/synth_jxl_gainmap*.jxl（sha16 钉死，
#   缺则 py tests/scripts/_make-jxl-gainmap-jhgm.py --outdir tests/data 重造）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（用 「」）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$env:FFMPEGGUI_PLAN_DIR = "$root/publish/PLAN"
# ⚠ 只用 Release 产物（本门禁是行为断言；异构/陈旧二进制的读数不可信）：
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
$pr  = "$root/tests/projects/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp  = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$cjxl    = "$root/publish/PLAN/jxl/bin/cjxl.exe"
$djxl    = "$root/publish/PLAN/jxl/bin/djxl.exe"
$jxlinfo = "$root/publish/PLAN/jxl/bin/jxlinfo.exe"
$hdrSrc  = "$root/tests/output/validate/src_hdr.png"
$fixGm   = "$root/tests/data/synth_jxl_gainmap.jxl"
$fixBase = "$root/tests/data/synth_jxl_gainmap_base.jxl"
$genPy   = "$root/tests/scripts/_make-jxl-gainmap-jhgm.py"

$val = "$root/tests/output/validate"
$gid = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = "$val/gmjxl_${gid}"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:LC_ALL = $null
$env:TMP = $work; $env:TEMP = $work; $env:TMPDIR = $work

$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
  else     { $script:fail++; Write-Output "  FAIL $msg" }
}
# 遗留孙进程会继承重定向句柄（AGENTS：ReadAllText 抛 IOException ⇒ summary=False 假红）
# ⇒ 容错读：普通读失败就等 200ms 重试，末次用 FileShare.ReadWrite 强读。
function ReadTextSafe([string]$path) {
  for ($i = 0; $i -lt 10; $i++) {
    try { return [System.IO.File]::ReadAllText($path) } catch { Start-Sleep -Milliseconds 200 }
  }
  $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
  try { $sr = New-Object System.IO.StreamReader($fs); return $sr.ReadToEnd() } finally { $fs.Dispose() }
}
function Exec([string]$file, [string[]]$argList, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argList -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = (ReadTextSafe $o) + "`n" + (ReadTextSafe $e) }
}
function ExecOut([string]$file, [string[]]$argList, [string]$tag) {
  $o = "$work/$tag.out"; $e = "$work/$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argList -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput $o -RedirectStandardError $e
  return @{ code = $p.ExitCode; text = ReadTextSafe $o }
}
function RunCli([string]$tag, [string]$inSpec, [string]$fmt, [string[]]$extra) {
  $outDir = "$work/out_$tag"
  if (Test-Path $outDir) { [System.IO.Directory]::Delete($outDir, $true) }
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  $a = @('--headless', '--log-level', 'Debug', '-i', $inSpec, '--format', $fmt, '--output', $outDir) + $extra
  $r = Exec $exe $a "run_$tag"
  $prods = @(Get-ChildItem $outDir -Recurse -File -ErrorAction SilentlyContinue)
  $prod = $null; $pp = ""
  if ($prods.Count -ge 1) { $prod = $prods[0]; $pp = $prod.FullName }
  return @{ code = $r.code; text = $r.text; dir = $outDir; prod = $prod; prodPath = $pp; prodCount = $prods.Count }
}
function Sha16([string]$path) {
  if (-not (Test-Path $path)) { return "<缺文件>" }
  return (Get-FileHash $path -Algorithm SHA256).Hash.Substring(0, 16).ToUpper()
}
function SrcSize([string]$path) {
  $r = ExecOut $fp @('-v','error','-select_streams','v:0','-show_entries','stream=width,height','-of','csv=p=0', $path) "size_$(Split-Path $path -Leaf)"
  foreach ($ln in ($r.text -split "`r?`n")) {
    $t = $ln.Trim()
    if ($t -match '^\s*(\d+)\s*,\s*(\d+)\s*$') { return @{ w = [int]$Matches[1]; h = [int]$Matches[2] } }
  }
  return @{ w = -1; h = -1 }
}

# ── §0 前置 fail-closed（缺件即红并点名，不静默给绿） ─────────────────────────────
Write-Output "`n### 0) 前置自检（fail-closed） ###"
$missing = @()
foreach ($need in @($exe, $pr, $ff, $fp, $cjxl, $djxl, $jxlinfo, $hdrSrc, $genPy)) { if (-not (Test-Path $need)) { $missing += $need } }
$fixMissing = @()
foreach ($need in @($fixGm, $fixBase)) { if (-not (Test-Path $need)) { $fixMissing += $need } }
if ($fixMissing.Count -gt 0) {
  Write-Output "  缺夹具：$($fixMissing -join ', ')"
  Write-Output "  重造命令：py tests/scripts/_make-jxl-gainmap-jhgm.py --outdir tests/data"
}
if ($missing.Count -gt 0 -or $fixMissing.Count -gt 0) {
  Write-Host "  ❌ 缺工具/产物/夹具（本门禁不做 Debug 回退，也不静默给绿）：" -ForegroundColor Red
  ($missing + $fixMissing) | ForEach-Object { Write-Output "    $_" }
  try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
  exit 1
}
CK ($true) "§0 前置件齐（exe/ServiceProbe/ffmpeg/ffprobe/cjxl/djxl/jxlinfo/src_hdr.png/生成器/两夹具）"
CK ((Sha16 $fixGm) -eq '009F22BEFA1DEF75') "§0 正向夹具 sha16 钉死（实 $(Sha16 $fixGm)，期 009F22BEFA1DEF75）"
CK ((Sha16 $fixBase) -eq 'AF0A836E7C164EDC') "§0 无 jhgm 夹具 sha16 钉死（实 $(Sha16 $fixBase)，期 AF0A836E7C164EDC）"
$mtBefore = (Get-Item $exe).LastWriteTime
Write-Output "  exe=$exe"
Write-Output "  exe-mtime=$mtBefore"

# ── §1 正控：HDR → jxl 增益图 → jxlinfo / ServiceProbe / djxl / cjxl 透传 ────────
Write-Output "`n### 1) 写侧正控 ###"
$w1 = RunCli 'w1' $hdrSrc 'jxl' @('--jpeg-gain-map', 'true')
CK ($w1.code -eq 0) "§1① 产品 CLI exit=0（实 $($w1.code)）"
CK (($null -ne $w1.prod) -and ($w1.prodCount -ge 1) -and ($w1.prod.Length -gt 0)) "§1① 产物 .jxl 非空（$($w1.prodCount) 件，$($w1.prod.Length) B）"
$j1 = $null
if ($null -ne $w1.prod) { $j1 = Exec $jxlinfo @($w1.prodPath) "jxlinfo_w1" }
$verLine = ""
if ($null -ne $j1) { foreach ($ln in ($j1.text -split "`r?`n")) { if ($ln -match 'Gain map \(jhgm\) box: version = (\d+)\s+(\d+)-byte gain map, (\d+)-byte metadata') { $verLine = $ln } } }
CK ($verLine -ne "") "§1② jxlinfo 解析出 jhgm（$verLine）"
$metaLen = 0; $gmLen = 0
if ($verLine -match '(\d+)-byte gain map, (\d+)-byte metadata') { $gmLen = [int]$Matches[1]; $metaLen = [int]$Matches[2] }
CK ($metaLen -eq 61) "§1③ ISO metadata = 61 B（单通道 blob；实 $metaLen）"
CK ($gmLen -gt 0) "§1③ 增益图码流 N-byte > 0（实 $gmLen）"
$sp = Exec $pr @('jxlgm') "sp_jxlgm"
CK (($sp.code -eq 0) -and ($sp.text -match 'PROBE RESULT: pass=21 fail=0')) "§1④ ServiceProbe jxlgm 全绿（exit=$($sp.code)，21 条托管互验：盒序/ISO blob 逐字节/载荷逐项）"
CK (($sp.text -match '负控① 传入容器件') -and ($sp.text -match '负控③ 空增益图')) "§1④ 托管侧负控三臂在位（写出器拒绝容器件/随机字节/空增益图）"
$szSrc = SrcSize $hdrSrc
$decOk = $false; $decSize = ""
if ($null -ne $w1.prod) {
  $dec = "$work/dec.png"
  $d1 = Exec $djxl @($w1.prodPath, $dec) "djxl_w1"
  $decOk = ($d1.code -eq 0) -and (Test-Path $dec)
  foreach ($ln in ($d1.text -split "`r?`n")) { if ($ln -match '(\d+)\s*x\s*(\d+)') { $decSize = "$($Matches[1])x$($Matches[2])" } }
}
CK ($decOk -and ($decSize -eq "$($szSrc.w)x$($szSrc.h)")) "§1⑤ djxl 解 jxlc 帧成功且尺寸==源（产物 $decSize vs 源 $($szSrc.w)x$($szSrc.h)）"
$re = "$work/reenc.jxl"
$r1x = Exec $cjxl @($w1.prodPath, $re, '-q', '100', '-e', '3') "cjxl_re"
$j2 = Exec $jxlinfo @($re) "jxlinfo_re"
CK (($r1x.code -eq 0) -and ($j2.text -match 'jhgm')) "§1⑥ cjxl 重编码透传（jxlinfo 仍见 jhgm，实 rc=$($r1x.code)）"

# §1⑦（2026-11-08 追加，djxl 作**第二读者**）：把 jhgm 里的增益图裸码流整段抽出来，
#   单独喂 djxl —— 判据不是"jxlinfo 报了多少字节"，而是 **libjxl 自己把这段码流解成像素**。
#   为什么这条比 §1②④ 更强：jxlinfo 只读盒头、只信长度闭合；ServiceProbe 是我方托管实现
#   （自产自读不算第二读者）。djxl 解成功 ⇒ 那 1384 字节是**结构合法的完整 JXL 码流**，
#   能抓出"截断但长度凑巧闭合""码流中间坏掉"这类头尾都对的坏件（§2④ 就是它的负控）。
#   ⚠ 顺带钉死 djxl 的能力边界（2026-11-08 实测）：它**不**通过任何开关暴露容器 box——
#   `-v -v` 只说 "Read N compressed bytes"、`--metadata_out` 根本不写 JSON、
#   `--output_extra_channels` 也不把增益图当 extra channel（增益图在 box 里不在码流里）。
#   ⇒ 增益图只能靠"抽出来解"来验证；整件解码成功 ≠ 增益图被读懂，两条必须分开断言。
if ($null -ne $w1.prod) {
  # 按盒链遍历抽 jhgm 载荷与增益图段（不引第三方库，纯字节算术）
  $ab = [System.IO.File]::ReadAllBytes($w1.prodPath)
  $aoff = 12; $agh = $null
  while (($aoff + 8) -le $ab.Length) {
    $asz = ([int]$ab[$aoff] -shl 24) -bor ([int]$ab[$aoff+1] -shl 16) -bor ([int]$ab[$aoff+2] -shl 8) -bor [int]$ab[$aoff+3]
    $aty = [System.Text.Encoding]::ASCII.GetString($ab, $aoff + 4, 4)
    if ($aty -eq 'jhgm') { $agh = @{ off = $aoff; size = $asz }; break }
    if ($asz -le 0) { break }
    $aoff += $asz
  }
  CK ($null -ne $agh) "§1⑦ 盒链遍历定位 jhgm（off=$($agh.off) size=$($agh.size)）"
  $script:gmCod = "$work/gm_codestream.jxl"
  $script:gmBytes = 0
  if ($null -ne $agh) {
    $pl = $agh.off + 8
    $ameta = ([int]$ab[$pl+1] -shl 8) -bor [int]$ab[$pl+2]
    $acenc = [int]$ab[$pl + 3 + $ameta]
    $apos  = $pl + 3 + $ameta + 1
    $aicc  = ([int]$ab[$apos] -shl 24) -bor ([int]$ab[$apos+1] -shl 16) -bor ([int]$ab[$apos+2] -shl 8) -bor [int]$ab[$apos+3]
    $apos += 4 + $aicc
    $gmEnd = $agh.off + $agh.size
    $script:gmBytes = $gmEnd - $apos
    [System.IO.File]::WriteAllBytes($script:gmCod, $ab[$apos..($gmEnd - 1)])
    CK ($ameta -eq $metaLen -and $acenc -eq 0 -and $aicc -eq 0) "§1⑦ jhgm 载荷字段与 §1② 读数自洽（meta=$ameta colorEnc=$acenc altIcc=$aicc）"
    CK ($script:gmBytes -eq $gmLen) "§1⑦ 增益图段长 == jxlinfo 报的 $gmLen B（实 $($script:gmBytes)）"
  }
  $dg = Exec $djxl @($script:gmCod, "$work/gm_dec.png") "djxl_gm"
  $gmW = -1; $gmH = -1
  foreach ($ln in ($dg.text -split "`r?`n")) { if ($ln -match '^\s*(\d+)\s*x\s*(\d+)\s*,') { $gmW = [int]$Matches[1]; $gmH = [int]$Matches[2] } }
  CK (($dg.code -eq 0) -and (Test-Path "$work/gm_dec.png")) "§1⑦ **djxl 独立解码增益图码流成功**（rc=$($dg.code)，$($script:gmBytes) B）——libjxl 认可它是完整合法码流，不只是长度闭合"
  CK (($gmW -gt 0) -and (($szSrc.w % $gmW) -eq 0) -and (($szSrc.h % $gmH) -eq 0)) "§1⑦ 增益图尺寸 $($gmW)x$($gmH) 整除底图 $($szSrc.w)x$($szSrc.h)（下采样比 1/$([int]($szSrc.w / $gmW))，增益图按此比例下采样）"
  $script:aghOff = if ($null -ne $agh) { $agh.off } else { -1 }
  $script:aghSize = if ($null -ne $agh) { $agh.size } else { -1 }
}

# ── §2 负控 ──────────────────────────────────────────────────────────────────────
Write-Output "`n### 2) 负控 ###"
# ① 坏载荷：正向夹具副本按盒链现造（jhgm 盒内 BE16 元数据长 → 0xFFFF）⇒ jxlinfo stderr Invalid
$bad = "$work/bad.jxl"
$bb = [System.IO.File]::ReadAllBytes($fixGm)
$pos = 12; $p0 = -1
while (($pos + 8) -le $bb.Length) {
  $sz = ([int]$bb[$pos] -shl 24) -bor ([int]$bb[$pos + 1] -shl 16) -bor ([int]$bb[$pos + 2] -shl 8) -bor [int]$bb[$pos + 3]
  $ty = [System.Text.Encoding]::ASCII.GetString($bb, $pos + 4, 4)
  if ($ty -eq 'jhgm') { $p0 = $pos + 8; break }
  if ($sz -le 0) { break }
  $pos += $sz
}
CK ($p0 -gt 0) "§2① 盒链遍历定位 jhgm 载荷（payload@=$p0，期 40）"
$bb[$p0 + 1] = 0xFF; $bb[$p0 + 2] = 0xFF
[System.IO.File]::WriteAllBytes($bad, $bb)
$j3 = Exec $jxlinfo @($bad) "jxlinfo_bad"
CK ($j3.text -match 'Invalid gain map box found') "§2① 坏载荷 ⇒ jxlinfo stderr「Invalid gain map box found」（解析器活着）"
# ② 无 jhgm 夹具 ⇒ jxlinfo 无 jhgm 行
$j4 = Exec $jxlinfo @($fixBase) "jxlinfo_base"
CK (($j4.code -eq 0) -and ($j4.text -notmatch 'jhgm')) "§2② 无 jhgm 夹具 ⇒ jxlinfo 不报 jhgm（不是见容器就报）"
# ④ 合成模式（2026-11-11 修复批 A2 接线放开 jxl；口径变更写在此处备查）：
#   修复前：jxl 合成被**格式守卫**在建队期拦掉（只收 jpg/jpeg/avif），本组负控打的是「守卫先拦」。
#   修复后：jxl 合成**合法**——承载 = 容器 **jhgm box**（ISO 21496-1；服务层 GainMapComposeService
#     与 GUI 早已支持，此前只是 CLI/队列两处守卫漏放行）⇒ 判据改为：
#     正控 = `--compose-gainmap -f jxl` 产出**可解析 jhgm 的合法 JXL**（随包 jxlinfo 0.12.0 复核，
#            回读自检由 JxlGainMapProbe + ffprobe 承担——见 GainMapComposeService §6）；
#     负控 = ① jxl + xmp（jhgm 无 XMP 形态，建队期拒绝零产物）；
#            ② jxl + `--gainmap-encoder`（子图恒由 cjxl 编、无编码器选择面，建队期拒绝零产物）。
$szHdr = SrcSize $hdrSrc
$sdrPairJxl = "$work/sdr_pair_jxl.png"
$genSdr = Exec $ff @('-y','-hide_banner','-loglevel','error','-f','lavfi',"-i","testsrc2=s=$($szHdr.w)x$($szHdr.h):d=1",'-frames:v','1','-vf','scale=out_color_matrix=bt709:out_range=full,format=rgb24','-color_primaries','bt709','-color_trc','iec61966-2-1','-colorspace','bt709',$sdrPairJxl) "gen_sdr_pair"
CK (($genSdr.code -eq 0) -and (Test-Path $sdrPairJxl)) "§2④ SDR 底图夹具生成（$($szHdr.w)x$($szHdr.h) 与 HDR 参考同尺寸；实 rc=$($genSdr.code)）"
$c0 = RunCli 'c_jxl_ok' "$sdrPairJxl,$hdrSrc" 'jxl' @('--compose-gainmap', '--gainmap-peak', '1000')
CK (($c0.code -eq 0) -and ($c0.prodCount -ge 1) -and ($c0.prod.Length -gt 0)) "§2④ compose jxl 正控：exit=0 且产物非空（实 rc=$($c0.code)，$($c0.prodCount) 产物，$($c0.prod.Length) B）"
if ($c0.prodCount -ge 1) {
  $j0 = Exec $jxlinfo @($c0.prodPath) "jxlinfo_compose"
  CK (($j0.code -eq 0) -and ($j0.text -match 'Gain map \(jhgm\) box: version = 0')) "§2④ compose jxl 产物带可解析 jhgm（jxlinfo 0.12.0：version=0 + N-byte gain map）"
} else { CK $false "§2④ compose jxl jxlinfo 复核（产物缺失）" }
$c1 = RunCli 'c_xmp' "$sdrPairJxl,$hdrSrc" 'jxl' @('--compose-gainmap', '--gainmap-metadata', 'xmp')
CK (($c1.code -ne 0) -and ($c1.prodCount -eq 0) -and ($c1.text -match '--gainmap-metadata xmp 与 -f jxl 不匹配')) "§2④ compose jxl+xmp ⇒ 建队拒绝零产物（jhgm 无 XMP 形态；实 rc=$($c1.code)，$($c1.prodCount) 产物）"
$c1b = RunCli 'c_jxl_enc' "$sdrPairJxl,$hdrSrc" 'jxl' @('--compose-gainmap', '--gainmap-encoder', 'cjpegli')
CK (($c1b.code -ne 0) -and ($c1b.prodCount -eq 0) -and ($c1b.text -match '不匹配') -and ($c1b.text -match 'JXL')) "§2④ compose jxl+--gainmap-encoder ⇒ 建队拒绝零产物（子图恒由 cjxl 编，无编码器选择面；实 rc=$($c1b.code)）"
$c2 = RunCli 'c_xmp_s' $hdrSrc 'jxl' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-metadata', 'xmp')
CK (($c2.code -ne 0) -and ($c2.prodCount -eq 0) -and ($c2.text -match 'no XMP-only gain-map form')) "§2④ 单输入 xmp+jxl ⇒ 运行时拒绝零产物并点名（实 rc=$($c2.code)，$($c2.prodCount) 产物）"
$c3 = RunCli 'c_both' $hdrSrc 'jxl' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-metadata', 'both')
CK (($c3.code -eq 0) -and ($c3.prodCount -ge 1) -and ($c3.text -match 'is carried by the jhgm box')) "§2④ both ⇒ 产物正常 + NOTE 降级 iso（实 rc=$($c3.code)，$($c3.prodCount) 产物）"
# ⑤ D6：bt2020 底 ⇒ 拒绝（v1 只支持 sRGB 底）
$c4 = RunCli 'c_bt2020' $hdrSrc 'jxl' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-base-gamut', 'bt2020')
CK (($c4.code -ne 0) -and ($c4.prodCount -eq 0) -and ($c4.text -match 'supports an sRGB base only')) "§2⑤ bt2020 底 ⇒ D6 显式拒绝零产物（实 rc=$($c4.code)，$($c4.prodCount) 产物）"
# ⑥ §1⑦ 的**负控（这条判据的牙）**：把增益图码流中段字节改坏，但 jhgm 盒长/字段长度**一字不动**
#   ⇒ jxlinfo 照旧报 "version = 0 N-byte gain map"（它只读盒头），而 djxl **必须解不出来**。
#   若这条转为绿（djxl 还能解），说明 §1⑦ 实际上只在重复 jxlinfo 的读数、没有独立价值 ⇒ 当场红。
if ((Test-Path $script:gmCod) -and ($script:aghOff -ge 0)) {
  $gb = [System.IO.File]::ReadAllBytes($script:gmCod)
  $mid = [int]($gb.Length / 2)
  # 只改中段一个字（避开 FF0A 头与盒尾），长度与全部长度字段保持不变
  $gb[$mid] = [byte]([int]$gb[$mid] -bxor 0xFF)
  $gmBroken = "$work/gm_broken.jxl"
  [System.IO.File]::WriteAllBytes($gmBroken, $gb)
  $dbg = Exec $djxl @($gmBroken, "$work/gm_broken.png") "djxl_gm_broken"
  CK ($dbg.code -ne 0) "§2⑥ 负控：增益图码流中段改坏（长度/字段不变）⇒ djxl **解不出**（rc=$($dbg.code)）——证明 §1⑦ 不是 jxlinfo 读数的复读"
  # 同一坏码流塞回 jhgm 载荷（长度一字不动）⇒ jxlinfo 仍报正常 ⇒ 对照证明"盒头对≠码流对"
  $hb = [System.IO.File]::ReadAllBytes($w1.prodPath)
  $hpl = $script:aghOff + 8
  $hmeta = ([int]$hb[$hpl+1] -shl 8) -bor [int]$hb[$hpl+2]
  $hpos = $hpl + 3 + $hmeta + 1
  $hicc = ([int]$hb[$hpos] -shl 24) -bor ([int]$hb[$hpos+1] -shl 16) -bor ([int]$hb[$hpos+2] -shl 8) -bor [int]$hb[$hpos+3]
  $hpos += 4 + $hicc
  for ($k = 0; $k -lt $gb.Length; $k++) { $hb[$hpos + $k] = $gb[$k] }
  $wholeBroken = "$work/whole_gm_broken.jxl"
  [System.IO.File]::WriteAllBytes($wholeBroken, $hb)
  $j5 = Exec $jxlinfo @($wholeBroken) "jxlinfo_whole_broken"
  CK ($j5.text -match 'Gain map \(jhgm\) box: version = 0') "§2⑥ 对照：同一坏码流塞回容器 ⇒ **jxlinfo 照旧报对**（盒头自洽）——所以只有 djxl 那条臂能抓出这类坏件"
}

# ── §3 tripwire：第二实现读者出现 ⇒ 升级双实现（渲染级）判据 ─────────────────────
Write-Output "`n### 3) tripwire（第二实现读者） ###"
$found = @(Get-ChildItem "$root/publish", "$root/tools" -Recurse -Include 'jxl-oxide*.exe', 'zenjxl*.exe', 'jxl-rs*.exe' -ErrorAction SilentlyContinue)
foreach ($n in @('jxl-oxide', 'zenjxl', 'jxl-rs')) {
  $cmd = Get-Command $n -ErrorAction SilentlyContinue
  if ($null -ne $cmd) { $found += $cmd.Source }
}
Write-Output ("  搜索面（publish/tools/PATH）第二实现读者命中数={0}" -f $found.Count)
CK ($found.Count -eq 0) "§3 tripwire 静默（无 jxl-oxide/zenjxl/jxl-rs CLI ⇒ 解析级判据维持 2026-11-08 裁决口径）"
if ($found.Count -gt 0) {
  $names = ($found | ForEach-Object { $_.FullName }) -join ', '
  Write-Host "    ❌ 第二实现读者已在（$names）：按 2026-11-08 裁决升级双实现（渲染级）判据并重测能力表！" -ForegroundColor Red
}

# ── §4 新鲜度 + 清理自证 ────────────────────────────────────────────────────────
Write-Output "`n### 4) 新鲜度 + 清理 ###"
$mtAfter = (Get-Item $exe).LastWriteTime
CK ($mtAfter -eq $mtBefore) "§4 exe mtime 起轮前后一致（$mtBefore）⇒ 读数打在同一个二进制上"
try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
CK (-not (Test-Path $work)) "§4 run-dir 已清零：$work"

Write-Output "`n===== gainmap-jxl: PASS=$pass FAIL=$fail ====="
if ($fail -gt 0) { exit 1 } else { exit 0 }
