# verify-jxl-gainmap-readside.ps1 —— JXL 增益图**读侧**（jhgm 识别/播报/不静默丢/透传读数）门禁（2026-11-08 新增）
#
# 锁四件事（R1-R3，2026-11-08 方案）：
#   R1 读侧识别：JxlGainMapProbe.Scan（纯托管盒扫描，非 jxlinfo）识别 jhgm 裸 box / brob 壳（载荷前 4B
#      原始类型，encode.cc:1144 文档化契约）/ 裸码流构造性判无 / 坏载荷 fail-closed（Determinate=false，
#      不装不知道成「没有」）；QueueProcessor 在任务开始处单点求值并播报（[jxl] 诊断桶）。
#   R2 不静默丢：正控件转**任何**目标格式（含 jxl——现状全部是像素级重编码，容器 box 结构性不随）
#      ⇒ 点名「不随本次转换保留」+ Status 后缀 " (gain map NOT preserved)"（P0-1 精神）。
#   R3 透传三件套：①参数面结构锁——CjxlService.BuildCjxlArguments 方法体（注释剥离后）零 container
#      字样（上游 cjxl_main.cc:1146-1157：仅显式 container=0 剥元数据）；②上游透传读数——cjxl 直编
#      jhgm 夹具（无旗标）⇒ jhgm 仍在（brob 形态），--container=0 负控 ⇒ jhgm 消失；③行为面现状读数
#      由 R2 各臂承担（将来若加容器透传路由此门禁会红 = 强制同批改判据的牙）。
#   负控三线：无 jhgm 必须报无（夹具 + 现造裸码流两支）；坏载荷必须红（jxlinfo Invalid）且播报与
#      正控读数可区分；链中断 fail-closed 不判「无」。
#
# 素材：tests/data/synth_jxl_gainmap.jxl（sha16 009F22BEFA1DEF75）/ synth_jxl_gainmap_base.jxl
#   （AF0A836E7C164EDC），生成器 tests/scripts/_make-jxl-gainmap-jhgm.py（幂等，缺则重造）；
#   裸码流对照用 ffmpeg lavfi + cjxl 现造（不依赖遗留产物）。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（用 「」）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$env:FFMPEGGUI_PLAN_DIR = "$root/publish/PLAN"
# ⚠ 只用 Release 产物（本门禁是行为断言；异构/陈旧二进制的读数不可信）：
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$cjxl    = "$root/publish/PLAN/jxl/bin/cjxl.exe"
$djxl    = "$root/publish/PLAN/jxl/bin/djxl.exe"
$jxlinfo = "$root/publish/PLAN/jxl/bin/jxlinfo.exe"
$fixGm   = "$root/tests/data/synth_jxl_gainmap.jxl"
$fixBase = "$root/tests/data/synth_jxl_gainmap_base.jxl"
$genPy   = "$root/tests/scripts/_make-jxl-gainmap-jhgm.py"
$cjxlSrc = "$root/src/FfmpegGui/Services/CjxlService.cs"

$val = "$root/tests/output/validate"
$gid = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = "$val/jxlgmr_${gid}"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:LC_ALL = $null
$env:TMP = $work; $env:TEMP = $work; $env:TMPDIR = $work

$pass = 0; $fail = 0
function CK($ok, $msg) {
  if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
  else     { $script:fail++; Write-Output "  FAIL $msg" }
}
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
function RunCli([string]$tag, [string]$inFile, [string]$fmt) {
  $outDir = "$work/out_$tag"
  if (Test-Path $outDir) { [System.IO.Directory]::Delete($outDir, $true) }
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  $r = Exec $exe @('--headless', '--log-level', 'Debug', '-i', $inFile, '--format', $fmt, '--output', $outDir) "run_$tag"
  $prods = @(Get-ChildItem $outDir -Recurse -File -ErrorAction SilentlyContinue)
  return @{ code = $r.code; text = $r.text; prodCount = $prods.Count }
}
function Sha16([string]$path) {
  if (-not (Test-Path $path)) { return "<缺文件>" }
  return (Get-FileHash $path -Algorithm SHA256).Hash.Substring(0, 16).ToUpper()
}
# 顶层盒遍历（夹具坏件现造用；jxlinfo 的盒语义参照，实现独立）
function FindJhgmPayload([byte[]]$b) {
  $pos = 12
  while (($pos + 8) -le $b.Length) {
    $sz = ([int]$b[$pos] -shl 24) -bor ([int]$b[$pos + 1] -shl 16) -bor ([int]$b[$pos + 2] -shl 8) -bor [int]$b[$pos + 3]
    $ty = [System.Text.Encoding]::ASCII.GetString($b, $pos + 4, 4)
    if ($ty -eq 'jhgm') { return ($pos + 8) }
    if ($sz -le 0) { return -1 }
    $pos += $sz
  }
  return -1
}

# ── §1 前置 fail-closed ─────────────────────────────────────────────────────────
Write-Output "`n### 0) 前置自检（fail-closed） ###"
$missing = @()
foreach ($need in @($exe, $ff, $cjxl, $djxl, $jxlinfo, $genPy, $cjxlSrc)) { if (-not (Test-Path $need)) { $missing += $need } }
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
CK ($true) "§1 前置件齐（exe/ffmpeg/cjxl/djxl/jxlinfo/生成器/CjxlService 源/两夹具）"
$mtBefore = (Get-Item $exe).LastWriteTime
Write-Output "  exe=$exe"
Write-Output "  exe-mtime=$mtBefore"

# ── §2 夹具形态自检 + 坏件现造 ──────────────────────────────────────────────────
Write-Output "`n### 2) 夹具形态自检 + 坏件现造 ###"
CK ((Sha16 $fixGm) -eq '009F22BEFA1DEF75') "§2 正向夹具 sha16 钉死（实 $(Sha16 $fixGm)）"
CK ((Sha16 $fixBase) -eq 'AF0A836E7C164EDC') "§2 无 jhgm 夹具 sha16 钉死（实 $(Sha16 $fixBase)）"
$sig = ""
$gb = [System.IO.File]::ReadAllBytes($fixGm)
if ($gb.Length -ge 12) { $sig = (($gb[0..11] | ForEach-Object { $_.ToString('X2') }) -join ' ') }
CK ($sig -eq '00 00 00 0C 4A 58 4C 20 0D 0A 87 0A') "§2 正向夹具首 12B = JXL 容器签名（实 $sig）"
$bad = "$work/bad.jxl"
$bb = [System.IO.File]::ReadAllBytes($fixGm)
$p0 = FindJhgmPayload $bb
CK ($p0 -gt 0) "§2 盒链遍历定位 jhgm 载荷（payload@=$p0，期 40）"
$bb[$p0 + 1] = 0xFF; $bb[$p0 + 2] = 0xFF
[System.IO.File]::WriteAllBytes($bad, $bb)
CK ((Test-Path $bad) -and ((Get-Item $bad).Length -eq $gb.Length)) "§2 坏件现造完成（BE16 元数据长→0xFFFF，$((Get-Item $bad).Length) B）"
$jB = Exec $jxlinfo @($bad) "jxlinfo_bad"
CK ($jB.text -match 'Invalid gain map box found') "§2 坏件 ⇒ jxlinfo stderr「Invalid gain map box found」（解析器活着；坏件 sha16=$(Sha16 $bad)）"

# ── §3 R1 正控：jhgm 夹具 → png ⇒ 识别行 + 字节读数 ────────────────────────────
Write-Output "`n### 3) R1 正控（识别 + 播报） ###"
$a1 = RunCli 'a_gm_png' $fixGm 'png'
CK ($a1.code -eq 0) "§3 转换 exit=0（实 $($a1.code)）"
CK ($a1.text -match '输入含 JXL 增益图') "§3 R1 识别行出现（不是见容器就报、也不是静默）"
CK ($a1.text -match 'form=jhgm') "§3 R1 form=jhgm"
CK (($a1.text -match '88-byte metadata') -and ($a1.text -match '38-byte gain map')) "§3 R1 逐字段字节读数命中（88B metadata / 38B gain map ⇒ 托管解析活着）"

# ── §4 R1 负控-A：无 jhgm（夹具 + 现造裸码流）⇒ 识别行必须不出现 ────────────────
Write-Output "`n### 4) R1 负控-A（无 jhgm 必须报无） ###"
$a2 = RunCli 'a_base_png' $fixBase 'png'
CK (($a2.code -eq 0) -and ($a2.text -notmatch '输入含 JXL 增益图') -and ($a2.text -notmatch 'NOT preserved')) "§4 无 jhgm 夹具 ⇒ 识别行与 NOT preserved 均不出现（实 rc=$($a2.code)，$($a2.prodCount) 产物）"
$nakedSrc = "$work/naked_src.png"; $naked = "$work/naked.jxl"
$gn = Exec $ff @('-y','-hide_banner','-loglevel','error','-f','lavfi','-i','testsrc2=s=64x48','-frames:v','1',$nakedSrc) "gen_naked"
$cn = Exec $cjxl @($nakedSrc, $naked, '-q', '100', '-e', '3') "cjxl_naked"
$nb = [System.IO.File]::ReadAllBytes($naked)
$nakedSig = (($nb[0..1] | ForEach-Object { $_.ToString('X2') }) -join ' ')
CK (($cn.code -eq 0) -and ($nakedSig -eq 'FF 0A')) "§4 现造裸码流就绪（cjxl 零旗标 ⇒ FF 0A 开头，实 $nakedSig，$($nb.Length) B）"
$a3 = RunCli 'a_naked_png' $naked 'png'
CK (($a3.code -eq 0) -and ($a3.text -notmatch '输入含 JXL 增益图') -and ($a3.text -notmatch 'NOT preserved')) "§4 现造裸码流（构造上不可能有 box）⇒ 判「无」（实 rc=$($a3.code)）"

# ── §5 R1 负控-B：坏载荷 ⇒ 播报出现且与正控读数可区分（不许当「无」） ────────────
Write-Output "`n### 5) R1 负控-B（坏载荷 fail-closed） ###"
$a4 = RunCli 'a_bad_png' $bad 'png'
CK (($a4.code -eq 0) -and ($a4.text -match '输入含 JXL 增益图')) "§5 坏载荷 ⇒ 识别行**仍出现**（box 在=增益图数据在，不装作「没有」；实 rc=$($a4.code)）"
CK (($a4.text -match '无法断定有无 JXL 增益图') -and ($a4.text -match '载荷解析失败')) "§5 坏载荷 fail-closed 独立播报（FailReason 随行 + 细节不可得文案，与正控 88/38 读数可区分）"
CK ($a4.text -match 'NOT preserved') "§5 坏载荷的 R2 点名照发（按 fail-closed 交付基础像素）"

# ── §6 R2 正控：→ png 与 → jpg 各一条 ⇒ 点名 + Status 后缀 ──────────────────────
Write-Output "`n### 6) R2 正控（不静默丢） ###"
$a5 = RunCli 'a_gm_jpg' $fixGm 'jpg'
CK (($a5.code -eq 0) -and ($a5.text -match '输入含 JXL 增益图') -and ($a5.text -match '不随本次转换保留') -and ($a5.text -match 'gain map NOT preserved')) "§6 → jpg：R1+R2+Status 后缀三项齐（实 rc=$($a5.code)）"
$a6 = RunCli 'a_gm_png2' $fixGm 'png'
CK (($a6.code -eq 0) -and ($a6.text -match '不随本次转换保留') -and ($a6.text -match 'gain map NOT preserved')) "§6 → png：R2 点名 + Status 后缀（实 rc=$($a6.code)）"

# ── §7 R2 对照：→ jxl 也点名（现状像素级重编码，容器 box 结构性不随） ────────────
Write-Output "`n### 7) R2 对照（目标 jxl 同样点名） ###"
$a7 = RunCli 'a_gm_jxl' $fixGm 'jxl'
CK (($a7.code -eq 0) -and ($a7.text -match '不随本次转换保留') -and ($a7.text -match 'gain map NOT preserved')) "§7 → jxl：现状结构性丢弃的诚实读数（将来若加透传路由此臂红 ⇒ 强制同批改判据；实 rc=$($a7.code)）"

# ── §8 R3 结构锁：BuildCjxlArguments 方法体（注释剥离后）零 container ───────────
Write-Output "`n### 8) R3 结构锁（参数面） ###"
$cs = [System.IO.File]::ReadAllText($cjxlSrc)
# 定义行定位：可见性 + static + 返回类型 + 方法名（IndexOf 会误落在 :240 IsGainMapContainer 附近
#   的其它引用，必须按「声明行」锚定）；成员缩进 8 空格 ⇒ 下一方法声明用 \s* 容忍。
$md = [regex]::Match($cs, '(?m)^\s*(?:public|internal|private|protected)\s+static\s+[\w<>\[\],\.\?\s]*?\bBuildCjxlArguments\s*\(')
CK ($md.Success) "§8 定位 BuildCjxlArguments 定义行（@$($md.Index)）"
$body = ""
if ($md.Success) {
  # ⚠ 从定义行行尾之后找下一方法声明：子串起点落在行中间时 .NET 的 ^ 也会匹配（字串开头），
  #   直接 Substring(md.Index+5) 会让 nx.Index=0 ⇒ body 只剩 5 B（假锁）。
  $nl = $cs.IndexOf("`n", $md.Index)
  if ($nl -lt 0) { $nl = $md.Index }
  $nx = [regex]::Match($cs.Substring($nl + 1), '(?m)^\s*(?:public|internal|private|protected)\s+static\s')
  if ($nx.Success) { $body = $cs.Substring($md.Index, $nl + 1 + $nx.Index) } else { $body = $cs.Substring($md.Index) }
  # 剥注释（// 到行尾；本方法体无 URL 类字符串字面量）
  $bodyNoC = ([regex]::Replace($body, '//[^\r\n]*', ''))
  $hit = $bodyNoC -match 'container'
  CK (-not $hit) "§8 BuildCjxlArguments 方法体（剥注释 $($body.Length) B）零 container 字样 ⇒ 参数面结构锁"
  $tooth = (($bodyNoC + "`n--container=0") -match 'container')
  CK ($tooth) "§8 牙：同一判据对注入的含 --container=0 假样本必须命中（自证非恒真）"
}

# ── §9 R3 上游透传读数（钉工具链假设） ─────────────────────────────────────────
Write-Output "`n### 9) R3 上游透传 + container=0 负控 ###"
$re = "$work/reenc.jxl"
$r1 = Exec $cjxl @($fixGm, $re, '-q', '100', '-e', '3') "cjxl_keep"
$j1 = Exec $jxlinfo @($re) "jxlinfo_keep"
CK (($r1.code -eq 0) -and ($j1.text -match 'jhgm')) "§9 cjxl 直编夹具（无旗标）⇒ jhgm 仍在（brob 形态：$(($j1.text -split "`r?`n") | Where-Object { $_ -match 'jhgm' } | Select-Object -First 1)）"
$st = "$work/strip.jxl"
$r2 = Exec $cjxl @($fixGm, $st, '--container=0') "cjxl_strip"
$j2 = Exec $jxlinfo @($st) "jxlinfo_strip"
CK (($r2.code -eq 0) -and ($j2.text -notmatch 'jhgm')) "§9 负控 --container=0 ⇒ jhgm 消失（剥除确由该开关引起；vendored cjxl_main.cc:1146-1157）"
$a8 = RunCli 'a_brob_png' $re 'png'
CK (($a8.code -eq 0) -and ($a8.text -match 'form=jhgm-brob')) "§9 brob 产物再进产品 ⇒ form=jhgm-brob 播报（零解压识别原始 box 类型）"

# ── §10 新鲜度 + 清理自证 ──────────────────────────────────────────────────────
Write-Output "`n### 10) 新鲜度 + 清理 ###"
$mtAfter = (Get-Item $exe).LastWriteTime
CK ($mtAfter -eq $mtBefore) "§10 exe mtime 起轮前后一致（$mtBefore）⇒ 读数打在同一个二进制上"
try { if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) } } catch { }
CK (-not (Test-Path $work)) "§10 run-dir 已清零：$work"

Write-Output "`n===== jxl-gainmap-readside: PASS=$pass FAIL=$fail ====="
if ($fail -gt 0) { exit 1 } else { exit 0 }
