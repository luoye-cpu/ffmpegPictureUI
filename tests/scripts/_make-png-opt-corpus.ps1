#requires -Version 7
<#
.SYNOPSIS
  PNG 极限压缩 Benchmark 素材矩阵生成器（plan.md §3.1 T2 / 契约 C-PMATRIX@1）。

.DESCRIPTION
  按 plan.md C-PMATRIX@1 矩阵表产出 21 素材（corpus/）+ 21×2 ffmpeg 基线（baselines/）
  + manifest.json + 4 个 chunk 保真子样本（B01/B02/C02/G01 → *-meta.png，携带 iCCP/cICP/eXIf）。
  消费契约：C-PMATRIX@1（矩阵表/manifest schema/基线命令/子样本五步）、C-CHUNK@1（关键集
  {iCCP, cICP, eXIf}，大小写敏感）、R2.1–R2.5、R5.1/R5.3（产物全落
  tests/output/png-opt-bench/<guid>/，不落 %TEMP%）。

  与矩阵表字面的实测偏差（随包 ffmpeg git-2026-10-11-edadf711a9 实证，全部为保 R2.2
  确定性的必要替换）：
  1. testsrc2 无 `n` 选项 → 去 `n=1`，确定性由 `-frames:v 1` 固定 t=0 帧保证（实测两跑一致）；
  2. noise 的 seed 真选项是 `all_seed`（矩阵表字面 `seed` 不存在）→ `noise=alls=100:all_seed=42`；
  3. gradients 的 seed=42 不覆盖全部随机源（x0/y0/x1/y1/颜色默认随机）→ 显式钉死
     c0/c1/n/x0/y0/x1/y1（实测两跑一致）；
  4. gray16le 素材的 PNG 载体 ffprobe 读回 gray16be（PNG 16-bit 网络序 BE 规范）→
     回读校验做 gray16be≡gray16le 等价映射，manifest 记矩阵表字面 gray16le；
  5. drawtext 字体：系统字体路径含 `:`，`\:` 转义在本版 ffmpeg filtergraph 解析下不生效
     → 每轮把 C:\Windows\Fonts\arial.ttf 只读拷进产物区，fontfile 用相对路径（零转义）；
  6. A02（照片 16-bit）的 corpus/基线命令显式 `-pix_fmt rgb48be`：jpg 解码自动编 png 只落
     rgb24，不加则基线与 corpus 位深不同源（违 R2.3「corpus 与基线像素同源」）；
  7. D3（sRGB ICC 来源）：仓内 stdicc 族现成件 glob `*.icc` = 0（历史产物已被清扫，产品侧
     stdicc 生成写产品缓存目录不可复用）→ 按契约 fallback：exiftool 从语料
     DSC00037.jpg `-b -icc_profile` 抽取（实测 536 B，profile size 字段自洽）。

.EXAMPLE
  pwsh -NoProfile -File tests/scripts/_make-png-opt-corpus.ps1
#>
param(
    # 产物根目录；默认 <repo>/tests/output/png-opt-bench。每轮自动建 <guid> 子目录。
    [string]$OutRoot = ''
)

$ErrorActionPreference = 'Stop'

# ── 仓库根与工具常量（本脚本位于 tests/scripts/，上两级 = 仓库根）─────────────────
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $repoRoot

$Ffmpeg   = Join-Path $repoRoot 'publish/PLAN/ffmpeg-full/ffmpeg.exe'
$Ffprobe  = Join-Path $repoRoot 'publish/PLAN/ffmpeg-full/ffprobe.exe'
$Exiftool = Join-Path $repoRoot 'publish/PLAN/exiftool/exiftool.exe'
$Pngcicp  = Join-Path $repoRoot 'tools/src/pngcicp/bin/Release/net11.0/pngcicp.exe'
$FontSrc  = 'C:\Windows\Fonts\arial.ttf'
$CorpusSrc = 'tests/output/corpus/gainmap-iso21496'
$JpgA12   = Join-Path $repoRoot "$CorpusSrc/DSC00037.jpg"   # A01/A02 照片源
$JpgA3    = Join-Path $repoRoot "$CorpusSrc/DSC01058.jpg"   # A03 照片源

$runLog = ''
function Log([string]$s) {
    Write-Host $s
    if ($script:runLog) { Add-Content -LiteralPath $script:runLog -Value $s -Encoding utf8 }
}
function Fail([int]$code, [string]$msg) {
    Log "[FAIL] $msg"
    Log "[exit] code=$code"
    exit $code
}

# ── 0. 前置存在性检查（fail-fast，不静默）────────────────────────────────────────
$missing = @()
foreach ($t in @($Ffmpeg, $Ffprobe, $Exiftool, $Pngcicp, $FontSrc, $JpgA12, $JpgA3)) {
    if (-not (Test-Path -LiteralPath $t)) { $missing += $t }
}
if ($missing.Count -gt 0) {
    Write-Host "[FAIL] prerequisite missing:`n  $($missing -join "`n  ")"
    exit 1
}

# ── 1. 一轮一 GUID 目录（AGENTS §3 / R5.3）──────────────────────────────────────
if (-not $OutRoot) { $OutRoot = Join-Path $repoRoot 'tests/output/png-opt-bench' }
$guid      = [guid]::NewGuid().ToString('N')
$dir       = Join-Path $OutRoot $guid
$corpusDir = Join-Path $dir 'corpus'
$baseDir   = Join-Path $dir 'baselines'
$tmpRoot   = Join-Path $dir '_tmp'
foreach ($d in @($corpusDir, $baseDir, $tmpRoot)) {
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}
$runLog = Join-Path $corpusDir '_make-run.log'
Set-Content -LiteralPath $runLog -Value "make-png-opt-corpus run $guid" -Encoding utf8

Log "[gen] guid=$guid"
Log "[gen] outdir=$dir"

# ── 2. 钉 TMP 根到产物区（AGENTS §3：子进程继承，绝不写系统 %TEMP%）──────────────
$env:TMP    = $tmpRoot
$env:TEMP   = $tmpRoot
$env:TMPDIR = $tmpRoot
Set-Content -LiteralPath (Join-Path $tmpRoot '.writable') -Value 'ok' -Encoding utf8
Log "[env-temp-pinned=$tmpRoot]"
Log "[env-temp-writable=True]"

# ── 3. drawtext 字体副本（相对路径零转义，见头注释偏差 5）────────────────────────
Copy-Item -LiteralPath $FontSrc -Destination (Join-Path $corpusDir '_arial.ttf') -Force
$FontRel = "tests/output/png-opt-bench/$guid/corpus/_arial.ttf"
Log "[font] copied arial.ttf -> corpus/_arial.ttf ($((Get-Item -LiteralPath (Join-Path $corpusDir '_arial.ttf')).Length) bytes)"

# ── 4. D3：sRGB ICC 抽取（契约 fallback；二进制必须进程级重定向，pwsh 管道会破坏）──
$iccAbs = Join-Path $corpusDir 'srgb.icc'
$iccErr = Join-Path $tmpRoot 'icc-err.txt'
$p = Start-Process -FilePath $Exiftool `
    -ArgumentList @('-b', '-icc_profile', $JpgA12) `
    -RedirectStandardOutput $iccAbs -RedirectStandardError $iccErr `
    -NoNewWindow -Wait -PassThru
if ($p.ExitCode -ne 0) {
    Fail 1 "ICC extraction failed exit=$($p.ExitCode) stderr=$(Get-Content -LiteralPath $iccErr -Raw)"
}
# 句柄释放竞态容错（AGENTS 文件锁教训：重试读）
$iccBytes = $null
for ($i = 0; $i -lt 5; $i++) {
    try { $iccBytes = [System.IO.File]::ReadAllBytes($iccAbs); break }
    catch [System.IO.IOException] { Start-Sleep -Milliseconds 200 }
}
if ($null -eq $iccBytes) { Fail 1 "ICC file unreadable after retries: $iccAbs" }
$iccSizeField = [int]$iccBytes[0]*16777216 + [int]$iccBytes[1]*65536 + [int]$iccBytes[2]*256 + [int]$iccBytes[3]
if ($iccBytes.Length -lt 128 -or $iccSizeField -ne $iccBytes.Length) {
    Fail 1 "ICC invalid: size-field=$iccSizeField actual=$($iccBytes.Length)"
}
Log "[icc] source=DSC00037.jpg bytes=$($iccBytes.Length) sha256=$((Get-FileHash -LiteralPath $iccAbs -Algorithm SHA256).Hash.Substring(0,16)) (D3 fallback, per C-PMATRIX@1)"

# 语料照片 sha 证据（AGENTS §7 钉死批次复核）
foreach ($j in @($JpgA12, $JpgA3)) {
    Log "[corpus-src] $(Split-Path -Leaf $j) bytes=$((Get-Item -LiteralPath $j).Length) sha256=$((Get-FileHash -LiteralPath $j -Algorithm SHA256).Hash.Substring(0,16))"
}

# ── 5. 矩阵表（C-PMATRIX@1，21 条，行序 = plan.md 表序）─────────────────────────
# kind: jpg | b(screenshot testsrc2+drawtext) | c(gradients) | n(noise) | l(lineart) | g(gray testsrc2)
# 尺寸: 照片件=原生实测（2736x1824 / 3000x2000）；合成件按矩阵表尺寸列。
$Matrix = @(
    @{ id='A01'; cat='photo';      kind='jpg'; jpg='A12'; pf='rgb24';   w=2736; h=1824; smoke=$false; chunk=$false },
    @{ id='A02'; cat='photo';      kind='jpg'; jpg='A12'; pf='rgb48be'; w=2736; h=1824; smoke=$false; chunk=$false },
    @{ id='A03'; cat='photo';      kind='jpg'; jpg='A3';  pf='rgb24';   w=3000; h=2000; smoke=$false; chunk=$false },
    @{ id='B01'; cat='screenshot'; kind='b';   pf='rgb24';   w=512;  h=384;  smoke=$true;  chunk=$true  },
    @{ id='B02'; cat='screenshot'; kind='b';   pf='rgba';    w=512;  h=384;  smoke=$false; chunk=$true  },
    @{ id='B03'; cat='screenshot'; kind='b';   pf='rgb24';   w=64;   h=64;   smoke=$false; chunk=$false },
    @{ id='B04'; cat='screenshot'; kind='b';   pf='rgb24';   w=6000; h=4000; smoke=$false; chunk=$false },
    @{ id='C01'; cat='gradient';   kind='c';   pf='rgb24';   w=512;  h=384;  smoke=$false; chunk=$false },
    @{ id='C02'; cat='gradient';   kind='c';   pf='rgb48be'; w=512;  h=384;  smoke=$false; chunk=$true  },
    @{ id='C03'; cat='gradient';   kind='c';   pf='rgb24';   w=64;   h=64;   smoke=$false; chunk=$false },
    @{ id='N01'; cat='noise';      kind='n';   pf='rgb24';   w=512;  h=384;  smoke=$false; chunk=$false },
    @{ id='N02'; cat='noise';      kind='n';   pf='rgb48be'; w=512;  h=384;  smoke=$false; chunk=$false },
    @{ id='N03'; cat='noise';      kind='n';   pf='rgb24';   w=6000; h=4000; smoke=$false; chunk=$false },
    @{ id='N04'; cat='noise';      kind='n';   pf='rgba';    w=512;  h=384;  smoke=$false; chunk=$false },
    @{ id='L01'; cat='lineart';    kind='l';   pf='rgb24';   w=512;  h=384;  smoke=$false; chunk=$false },
    @{ id='L02'; cat='lineart';    kind='l';   pf='rgba';    w=512;  h=384;  smoke=$false; chunk=$false },
    @{ id='L03'; cat='lineart';    kind='l';   pf='rgb24';   w=64;   h=64;   smoke=$false; chunk=$false },
    @{ id='G01'; cat='gray';       kind='g';   pf='gray';    w=512;  h=384;  smoke=$false; chunk=$true  },
    @{ id='G02'; cat='gray';       kind='g';   pf='gray16le'; w=512; h=384;  smoke=$false; chunk=$false },
    @{ id='G03'; cat='gray';       kind='g';   pf='gray';    w=6000; h=4000; smoke=$false; chunk=$false },
    @{ id='B05'; cat='screenshot'; kind='b';   pf='rgba';    w=6000; h=4000; smoke=$false; chunk=$false }
)

# lavfi 源串构造（确定性参数全部显式，见头注释偏差 1/2/3）
function Get-LavfiSource([hashtable]$m) {
    $w = $m.w; $h = $m.h; $pf = $m.pf
    switch ($m.kind) {
        'b' {
            $fs = if ($w -le 64) { 16 } elseif ($w -le 512) { 48 } else { 400 }
            $font = $script:FontRel   # 注意：${font} 定界——"$var:text" 的冒号会被 PS 当变量名展开（实测坑）
            return "testsrc2=size=${w}x${h}," +
                   "drawtext=fontfile=${font}:text=PNG:fontcolor=white:fontsize=${fs}:x=(w-text_w)/2:y=(h-text_h)/2," +
                   "format=$pf"
        }
        'c' {
            # 显式钉死颜色与几何（R2.2 固定 seed 的完全形态，实测两跑一致）
            return "gradients=s=${w}x${h}:speed=0:seed=42:c0=0x101010:c1=0xF0F0F0:n=2:x0=0:y0=0:x1=${w}:y1=${h}," +
                   "format=$pf"
        }
        'n' {
            return "nullsrc=s=${w}x${h},noise=alls=100:all_seed=42,format=$pf"
        }
        'l' {
            $g = if ($w -le 64) { 16 } elseif ($w -le 512) { 64 } else { 600 }
            $fs = if ($w -le 64) { 16 } elseif ($w -le 512) { 120 } else { 900 }
            $font = $script:FontRel   # 同上：${font} 定界
            return "color=c=black:s=${w}x${h}," +
                   "drawgrid=w=${g}:h=${g}:t=2:c=white," +
                   "drawtext=fontfile=${font}:text=LINE:fontcolor=white:fontsize=${fs}:x=(w-text_w)/2:y=(h-text_h)/2," +
                   "format=$pf"
        }
        'g' {
            return "testsrc2=size=${w}x${h},format=$pf"
        }
        default { Fail 1 "unknown lavfi kind '$($m.kind)'" }
    }
}

# 素材源文件绝对路径
function Get-SourceJpg([string]$key) {
    switch ($key) { 'A12' { return $script:JpgA12 } 'A3' { return $script:JpgA3 } }
    Fail 1 "unknown jpg key '$key'"
}

# ── 6. ffmpeg / ffprobe / sha helpers ────────────────────────────────────────────
function Invoke-Ffmpeg([string[]]$argv) {
    & $script:Ffmpeg -y -hide_banner -loglevel error @argv
    if ($LASTEXITCODE -ne 0) {
        Fail 1 "ffmpeg exit=$LASTEXITCODE argv=$($argv -join ' ')"
    }
}
# ffprobe 回读：实测输出顺序 = width,height,pix_fmt
function Get-PngProbe([string]$absPath) {
    $line = (& $script:Ffprobe -v error -show_entries stream=pix_fmt,width,height -of csv=p=0 $absPath | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($line)) {
        Fail 1 "ffprobe failed for $absPath"
    }
    $parts = $line -split ','
    return @{ w = [int]$parts[0]; h = [int]$parts[1]; pf = $parts[2] }
}
function Get-Sha16([string]$absPath) {
    return (Get-FileHash -LiteralPath $absPath -Algorithm SHA256).Hash.Substring(0, 16)
}
# 回读 pix_fmt 与矩阵表字面对撞（gray16be ≡ gray16le 等价映射，见头注释偏差 4）
function Test-PixFmtEqual([string]$probePf, [string]$specPf) {
    if ($probePf -eq $specPf) { return $true }
    if (($probePf -eq 'gray16be' -and $specPf -eq 'gray16le') -or
        ($probePf -eq 'gray16le' -and $specPf -eq 'gray16be')) { return $true }
    return $false
}

# ── 7. corpus 生成（21 素材）────────────────────────────────────────────────────
Log "[phase] corpus generation (21 items)"
foreach ($m in $Matrix) {
    $outAbs = Join-Path $corpusDir "$($m.id).png"
    $outRel = "corpus/$($m.id).png"
    if ($m.kind -eq 'jpg') {
        $jpgAbs = Get-SourceJpg $m.jpg
        $jpgRel = "$CorpusSrc/$(Split-Path -Leaf $jpgAbs)"
        # 照片件显式 -pix_fmt（见头注释偏差 6）；其余序列与契约基线模板同形
        Invoke-Ffmpeg @('-i', $jpgAbs, '-frames:v', '1', '-pix_fmt', $m.pf, '-c:v', 'png', '-compression_level', '9', $outAbs)
        $m.generate_cmd = @('ffmpeg', '-i', $jpgRel, '-frames:v', '1', '-pix_fmt', $m.pf, '-c:v', 'png', '-compression_level', '9', $outRel)
        $m.baseline_in_abs = $jpgAbs
        $m.baseline_in_rel = $jpgRel
        $m.baseline_needs_pixfmt = $true
    }
    else {
        $src = Get-LavfiSource $m
        Invoke-Ffmpeg @('-f', 'lavfi', '-i', $src, '-frames:v', '1', '-c:v', 'png', '-compression_level', '9', $outAbs)
        $m.generate_cmd = @('ffmpeg', '-f', 'lavfi', '-i', $src, '-frames:v', '1', '-c:v', 'png', '-compression_level', '9', $outRel)
        $m.baseline_in_abs = $outAbs           # 合成件基线输入 = corpus 文件本身（C-PMATRIX@1）
        $m.baseline_in_rel = $outRel
        $m.baseline_needs_pixfmt = $false      # png 解码自动保持位深
    }
    $probe = Get-PngProbe $outAbs
    if (-not (Test-PixFmtEqual $probe.pf $m.pf)) {
        Fail 1 "$($m.id) pix_fmt mismatch: spec=$($m.pf) probe=$($probe.pf)"
    }
    if ($probe.w -ne $m.w -or $probe.h -ne $m.h) {
        Fail 1 "$($m.id) size mismatch: spec=$($m.w)x$($m.h) probe=$($probe.w)x$($probe.h)"
    }
    $m.bytes = (Get-Item -LiteralPath $outAbs).Length
    $m.sha = Get-Sha16 $outAbs
    $probeNote = if ($m.pf -eq 'gray16le') { ' probe=' + $probe.pf + '(PNG16BE-carrier)' } else { '' }
    Log "[case=$($m.id)] $($probe.w)x$($probe.h) spec_pf=$($m.pf)$probeNote bytes=$($m.bytes) sha256=$($m.sha)"
}

# ── 8. 基线两档（R2.3：命令逐字 ffmpeg -i <in> -frames:v 1 -c:v png -compression_level {0|9} <out>，不传 -pred）──
Log "[phase] baselines (21 x {l0,l9})"
foreach ($m in $Matrix) {
    foreach ($lvl in @('0', '9')) {
        $tag = if ($lvl -eq '0') { 'l0' } else { 'l9' }
        $outAbs = Join-Path $baseDir "$($m.id)-$tag.png"
        $argv = @('-i', $m.baseline_in_abs, '-frames:v', '1')
        if ($m.baseline_needs_pixfmt) { $argv += @('-pix_fmt', $m.pf) }
        $argv += @('-c:v', 'png', '-compression_level', $lvl, $outAbs)
        Invoke-Ffmpeg $argv
        $m["base_$tag"] = (Get-Item -LiteralPath $outAbs).Length
        $m["base_$tag" + '_sha'] = Get-Sha16 $outAbs
    }
    $lt = $m.base_l9 -lt $m.base_l0
    Log "[base=$($m.id)] l0=$($m.base_l0) l9=$($m.base_l9) l9_lt_l0=$lt"
    if (-not $lt) { $m.sanity_fail = $true }
}

# ── 9. sanity：l9 < l0（任务书硬判据：例外 = 基线生成有错，定位修复）─────────────
$violations = @($Matrix | Where-Object { $_.sanity_fail })
foreach ($v in $violations) {
    Log "[sanity-fail] $($v.id) l9=$($v.base_l9) >= l0=$($v.base_l0)"
}
if ($violations.Count -gt 0) {
    Fail 2 "l9<l0 sanity violated on $($violations.Count) item(s): $($violations.id -join ',')"
}

# ── 10. chunk 子样本（C-PMATRIX@1 五步逐字；关键集 {iCCP, cICP, eXIf} per C-CHUNK@1）──
Log "[phase] chunk subsamples (B01/B02/C02/G01, 5-step per C-PMATRIX@1)"
foreach ($m in ($Matrix | Where-Object { $_.chunk })) {
    $metaAbs = Join-Path $corpusDir "$($m.id)-meta.png"
    $metaRel = "corpus/$($m.id)-meta.png"
    # 步骤 1：复制
    Copy-Item -LiteralPath (Join-Path $corpusDir "$($m.id).png") -Destination $metaAbs -Force
    # 步骤 2：pngcicp cicp 原位插入 cICP（primaries=1/sRGB transfer=13/sRGB matrix=0 full=1）
    & $Pngcicp cicp $metaAbs $metaAbs 1 13 0 1
    if ($LASTEXITCODE -ne 0) { Fail 3 "$($m.id) pngcicp cicp exit=$LASTEXITCODE" }
    # 步骤 3：exiftool 写 iCCP
    & $Exiftool -q "-icc_profile<=$iccAbs" -overwrite_original $metaAbs
    if ($LASTEXITCODE -ne 0) { Fail 3 "$($m.id) exiftool iCCP exit=$LASTEXITCODE" }
    # 步骤 4：exiftool 写 eXIf
    & $Exiftool -q '-exififd:datetimeoriginal=2026:01:01 00:00:00' -overwrite_original $metaAbs
    if ($LASTEXITCODE -ne 0) { Fail 3 "$($m.id) exiftool eXIf exit=$LASTEXITCODE" }
    # 步骤 5：pngcicp info 校验三 chunk 齐（大小写敏感 per C-CHUNK@1）
    $info = & $Pngcicp info $metaAbs
    if ($LASTEXITCODE -ne 0) { Fail 3 "$($m.id) pngcicp info exit=$LASTEXITCODE" }
    $infoText = ($info -join "`n")
    Log "--- pngcicp info $($m.id)-meta.png ---"
    foreach ($l in $info) { Log $l }
    $has = @{}
    foreach ($k in @('iCCP', 'cICP', 'eXIf')) {
        $has[$k] = $infoText -cmatch "(?m)^\s{2}$([regex]::Escape($k)):"
    }
    $allOk = $has['iCCP'] -and $has['cICP'] -and $has['eXIf']
    Log "[meta=$($m.id)] iCCP=$($has['iCCP']) cICP=$($has['cICP']) eXIf=$($has['eXIf']) sha256=$(Get-Sha16 $metaAbs)"
    if (-not $allOk) { Fail 3 "$($m.id) chunk check failed: iCCP=$($has['iCCP']) cICP=$($has['cICP']) eXIf=$($has['eXIf'])" }
    $m.meta_rel = $metaRel
}

# ── 11. manifest.json（C-PMATRIX@1 schema 逐字段）───────────────────────────────
Log "[phase] manifest.json"
$manifest = foreach ($m in $Matrix) {
    [pscustomobject]@{
        case_id        = $m.id
        category       = $m.cat
        file           = "corpus/$($m.id).png"
        pix_fmt        = $m.pf
        width          = $m.w
        height         = $m.h
        sha256_16      = $m.sha
        generate_cmd   = $m.generate_cmd
        baselines      = [pscustomobject]@{
            l0 = [pscustomobject]@{ file = "baselines/$($m.id)-l0.png"; sha256_16 = $m.base_l0_sha; bytes = $m.base_l0 }
            l9 = [pscustomobject]@{ file = "baselines/$($m.id)-l9.png"; sha256_16 = $m.base_l9_sha; bytes = $m.base_l9 }
        }
        chunk_sample   = $m.chunk
        chunk_source_file = if ($m.chunk) { $m.meta_rel } else { $null }
    }
}
$manifestAbs = Join-Path $corpusDir 'manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestAbs -Encoding utf8
Log "[manifest] written: $manifestAbs ($((Get-Item -LiteralPath $manifestAbs).Length) bytes)"

# ── 12. 覆盖度统计（R2.1：6 内容类 / 5 位深形态 / ≥3 尺寸 / 总数 21）──────────────
$cats   = @($Matrix.cat | Sort-Object -Unique)
$pfs    = @($Matrix.pf  | Sort-Object -Unique)
$sizes  = @($Matrix | ForEach-Object { "$($_.w)x$($_.h)" } | Sort-Object -Unique)
Log "[coverage] categories=$($cats.Count) [$($cats -join '/')]"
Log "[coverage] pix_fmts=$($pfs.Count) [$($pfs -join '/')]"
Log "[coverage] sizes=$($sizes.Count) [$($sizes -join '/')]"
Log "[coverage] total=$($Matrix.Count) baselines=$($Matrix.Count * 2) subsamples=$(@($Matrix | Where-Object { $_.chunk }).Count)"
Log "[smoke] $((@($Matrix | Where-Object { $_.smoke }).id) -join ',')"

if ($cats.Count -lt 6 -or $pfs.Count -lt 5 -or $sizes.Count -lt 3 -or $Matrix.Count -ne 21) {
    Fail 2 "coverage below R2.1 floor: cats=$($cats.Count) pfs=$($pfs.Count) sizes=$($sizes.Count) total=$($Matrix.Count)"
}

Log "[summary] corpus=21 baselines=$($Matrix.Count * 2) subsamples=4 l9_lt_l0=21/21 manifest=corpus/manifest.json"
Log "[exit] code=0"
exit 0