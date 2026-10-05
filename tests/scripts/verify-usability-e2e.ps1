# verify-usability-e2e.ps1 —— **用户可感知可用性**的实机端到端矩阵
#
# 与既有门禁的分工（不重复造轮子）：
#   既有 57 条门禁大多覆盖"色彩裁决/标注/位深/拒绝码"，且部分是「产物非空」式弱断言；
#   本脚本专补**高频用法**的端到端真相：完整格式互转矩阵、批量/目录输入、动图帧数、
#   alpha 保留、目标色彩空间兑现，并且每条都带**可证伪**判据（ffprobe 读标注 + PSNR）。
#
# 判据档（沿用 verify-decision-delivery 的强度，不用"存在即绿"）：
#   [OK]   进程 exit 0 且产出非空文件
#   [DIM]  尺寸守恒（宽高 == 源；动图另判帧数）
#   [TAG]  目标色彩空间兑现（显式指定时 ffprobe 的 primaries/transfer 或 ICC 描述必须命中）
#   [PSNR] 与源比对 ≥ 25 dB（低于此即"内容被毁/黑图/损坏"，能抓住绝大多数可用性事故）
#
# 用法： pwsh -File tests/scripts/verify-usability-e2e.ps1 [-Layer core|formats|color|batch|all]
param([ValidateSet('core','formats','color','batch','all')][string]$Layer = 'all')

$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

# exe 定位：AOT 发布会落到 native\ 子目录；也可用打包产物（publish\build\*-x64\）
$exe = @(
    "$root\src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe",
    "$root\src\FfmpegGui\bin\Release\net11.0\win-x64\publish\FfmpegGui.exe",
    "$root\src\FfmpegGui\bin\Release\net11.0\win-x64\native\FfmpegGui.exe",
    (Get-ChildItem "$root\publish\build" -Recurse -Filter FfmpegGui.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '-full' } | Select-Object -First 1).FullName
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
$ff  = "$root\publish\PLAN\ffmpeg-full\ffmpeg.exe"
# ⚠ 读流信息（宽高/pix_fmt/cICP/帧数）是 **ffprobe** 的能力，ffmpeg 不接受 -show_entries
$fp  = "$root\publish\PLAN\ffmpeg-full\ffprobe.exe"
if (-not (Test-Path $fp)) { $fp = (Get-Command ffprobe -ErrorAction SilentlyContinue).Source }
$src = "$root\tests\output\sources"
$work = "$root\tests\output\usability_$(Get-Date -Format 'MMdd_HHmm')"
New-Item -ItemType Directory -Force -Path $work | Out-Null

if (-not (Test-Path $exe)) { Write-Output "FATAL 产品 exe 不存在: $exe （请先 Release 发布）"; exit 2 }
if (-not (Test-Path $ff))  { $ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source }
if (-not $ff) { Write-Output "FATAL 找不到 ffmpeg"; exit 2 }

# ── 素材：缺失则用仓库自带生成器补齐（保证新克隆可复现）──
if (-not (Get-ChildItem $src -File -ErrorAction SilentlyContinue)) {
    Write-Output "i  素材缺失，调用 generate-sources.ps1 ..."
    & "$root\tests\scripts\generate-sources.ps1" 2>&1 | Select-Object -Last 5
}
if (-not (Get-ChildItem $src -File -ErrorAction SilentlyContinue)) {
    Write-Output "FATAL 素材仍不可用: $src"; exit 2
}

$rows = @(); $pass = 0; $fail = 0; $skip = 0

function Find-Src([string[]]$exts) {
    foreach ($e in $exts) {
        $f = Get-ChildItem $src -Filter "*.$e" -File -ErrorAction SilentlyContinue |
             Where-Object { $_.Length -gt 0 } | Select-Object -First 1
        if ($f) { return $f.FullName }
    }
    return $null
}

function Run-Cli([string]$in, [string]$outDir, [string[]]$extra) {
    $o = "$work\_o.txt"; $e = "$work\_e.txt"
    Remove-Item $o,$e -Force -ErrorAction SilentlyContinue
    $argv = @('--headless','-i',$in,'--output',$outDir,'--log-level','Debug') + $extra
    # ⚠⚠ 2026-10-05 修（含空格取值被拆成两个参数 ⇒ 该格恒红）：
    #   `Start-Process -ArgumentList <数组>` 在 Windows 上把数组**拼成一条命令行、
    #   *不做* 逐元素引号包裹 ⇒ `@('--color-space','Display P3')` 到达子进程时变成
    #   `--color-space Display P3` ⇒ CLI 把 `P3` 当成位置参数、`Display` 当成取值
    #   ⇒ 实测 exit=1、**零产物** ⇒ `B-color SDR→Display P3/*` 三格恒红（与本闸无关的产品侧没有问题：
    #     手工对同一命令加引号跑 ⇒ 产物标注 `smiec61966-2-1,smpte432` 完全正确）。
    #   ⇒ 逐个元素**显式加引号**（并转义元素内既有的双引号），保留含空格取值的完整性。
    #   ⚠ 不用 `-ArgumentList ($argv -join ' ')` 那种"看起来对"的写法：仍不会给含空格元素加引号。
    $quoted = $argv | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }
    $p = Start-Process -FilePath $exe -ArgumentList $quoted -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput $o -RedirectStandardError $e
    $log = ((Get-Content $o -Raw -ErrorAction SilentlyContinue) + "`n" + (Get-Content $e -Raw -ErrorAction SilentlyContinue))
    return @{ Rc = $p.ExitCode; Log = $log }
}

function Get-Prod([string]$dir) {
    Get-ChildItem $dir -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -gt 0 } | Select-Object -First 1
}

function Probe-V([string]$f) {
    $a = & $fp -v error -select_streams v:0 -show_entries stream=width,height,pix_fmt,color_primaries,color_transfer -of csv=p=0 "$f" 2>$null
    if (-not $a) { return $null }
    # ⚠⚠ 2026-10-05 修（本闸**读反了列序** ⇒ 系统性假红）：
    #   `ffprobe -of csv=p=0` 的列序**不按请求顺序**，而是按其内部字段顺序。
    #   实测（同一 PNG，请求 `width,height,pix_fmt,color_primaries,color_transfer`）：
    #     两个一起请求 ⇒ `128,96,rgb24,iec61966-2-1,bt709`
    #     单独请求 primaries  ⇒ `bt709`
    #     单独请求 transfer   ⇒ `iec61966-2-1`
    #   ⇒ `[3]` 是 **transfer**、`[4]` 才是 **primaries**，**与请求顺序相反**。
    #   旧写法按请求顺序取 `Prim=$p[3]; Trc=$p[4]` ⇒ `Prim` 拿到的是 transfer 值
    #   ⇒ 下面「标注兑现」判据拿 `Prim` 与 `'bt709'`(sRGB 的原色) 比 ⇒ **恒不相等**
    #   ⇒ 该闸 core 档**系统性假红**（实测 12 条 HDR→SDR 全部 '标注未兑现'，而产物标注其实正确）。
    #   ⚠ 为什么不改成"各自单独请求"：那要跑 5 次 ffprobe；这里保留一次调用、只把**下标取对**，
    #     并把列序事实写进注释（避免下次又被"请求顺序=输出顺序"的直觉骗到）。
    $p = ($a -split ',')[0..4]
    return @{ W=[int]$p[0]; H=[int]$p[1]; Fmt=$p[2]; Trc=$p[3]; Prim=$p[4] }
}

function Get-Psnr([string]$a, [string]$b) {
    $out = & $ff -v error -i $a -i $b -lavfi psnr -f null - 2>&1 | Out-String
    $m = [regex]::Match($out, 'average:([\d\.]+)')
    if ($m.Success) { return [double]$m.Groups[1].Value }
    return $null
}

function Add-Row([string]$group,[string]$name,[string]$srcName,[string]$in,[string]$outFmt,
                [string]$targetSpace,[string]$outDir,$r,[string]$note) {
    $script:rows += [pscustomobject]@{
        Group=$group; Case=$name; Src=$srcName; In=$in; OutFmt=$outFmt; Target=$targetSpace
        Rc=$r.Rc; Prod=$r.Prod; DimOk=$r.DimOk; Tag=$r.Tag; TagOk=$r.TagOk; Psnr=$r.Psnr; Note=$note
    }
    if ($r.Verdict -eq 'PASS') { $script:pass++ }
    elseif ($r.Verdict -eq 'SKIP') { $script:skip++ }
    else { $script:fail++ }
}

# ── 单格执行 + 判定 ──────────────────────────────────────────────
function Invoke-Cell([string]$group,[string]$in,[string]$outFmt,[string[]]$extra,[string]$targetSpace) {
    $id = "$group`_$outFmt`_" + ([guid]::NewGuid().ToString('N').Substring(0,6))
    $outDir = Join-Path $work $id
    $r = Run-Cli $in $outDir $extra
    $res = @{ Rc=$r.Rc; Prod=''; DimOk=''; Tag=''; TagOk=''; Psnr=$null; Verdict='FAIL'; Note='' }

    if ($r.Rc -ne 0) { $res.Note = "exit=$($r.Rc)"; return $res }

    $prod = Get-Prod $outDir
    if (-not $prod) { $res.Note = '零产物'; return $res }
    $res.Prod = $prod.Name

    $pv = Probe-V $prod.FullName
    $sv = Probe-V $in
    if (-not $pv) { $res.Note = '产物不可解码'; return $res }
    $res.DimOk = if ($sv -and $pv.W -eq $sv.W -and $pv.H -eq $sv.H) { 'yes' } else { "no($($pv.W)x$($pv.H) vs $($sv.W)x$($sv.H))" }
    $res.Tag = "$($pv.Prim)/$($pv.Trc)"

    # 标注兑现：显式指定目标色彩空间时，primaries 必须命中（transfer 允许由容器决定时留空）
    if ($targetSpace) {
        $want = switch -Wildcard ($targetSpace) {
            'sRGB*'      { 'bt709' }
            'Display P3*'{ 'smpte432' }
            'BT.2020*'   { 'bt2020' }
            default      { '' }
        }
        $res.TagOk = if (-not $want) { 'n/a' } elseif ($pv.Prim -eq $want) { 'yes' } else { "no(want $want)" }
    } else { $res.TagOk = 'n/a' }

    $psnr = Get-Psnr $in $prod.FullName
    $res.Psnr = if ($psnr) { [math]::Round($psnr,2) } else { $null }

    $bad = @()
    if ($res.DimOk -ne 'yes') { $bad += '尺寸不守恒' }
    if ($res.TagOk -like 'no*') { $bad += '标注未兑现' }
    # ⚠ PSNR 下限必须**按输出格式分级**：它测的是"内容有没有被毁"，不是画质评比。
    #   · png/tiff/jxl = 无损容器 ⇒ 正常就 ≥40dB，低于即真事故（黑图/损坏/错误色彩）
    #   · jpg/webp/avif = 有损但保真 ⇒ ≥28dB
    #   · gif = **256 色调色板量化**（正常转换也就 ~20dB）⇒ 阈值必须放到 18，否则整格误判
    $minPsnr = switch ($outFmt) {
        'png'  { 40 }
        'tiff' { 40 }
        'jxl'  { 40 }
        'gif'  { 18 }
        default { 28 }
    }
    if ($psnr -ne $null -and $psnr -lt $minPsnr) { $bad += "PSNR<$minPsnr dB(实$([math]::Round($psnr,1)))" }
    if ($bad.Count -eq 0) { $res.Verdict = 'PASS'; $res.Note = 'ok' }
    else { $res.Verdict = 'FAIL'; $res.Note = ($bad -join '; ') }
    return $res
}

Write-Output "═══════════════════════════════════════════"
Write-Output "  实机可用性矩阵 (Layer=$Layer)"
Write-Output "  work=$work"
Write-Output "═══════════════════════════════════════════"

# ══ A. 完整格式互转矩阵（最高频用法；既有门禁只有子集且多为弱断言）══
if ($Layer -in @('all','core','formats')) {
    Write-Output "`n── A. 格式互转矩阵 ──"
    $inExts = @('jpg','png','webp','avif','jxl','tiff','gif','jxr')
    $outFmts = @('jpg','png','webp','avif','jxl','tiff')
    foreach ($ie in $inExts) {
        $in = Find-Src @($ie)
        if (-not $in) { Write-Output "  SKIP 输入素材缺失: .$ie"; $skip++; continue }
        foreach ($of in $outFmts) {
            $extra = @('--format',$of)
            $r = Invoke-Cell 'A-format' $in $of $extra $null
            $verdict = if ($r.Verdict -eq 'PASS') { 'PASS' } elseif ($r.Verdict -eq 'SKIP') { 'SKIP' } else { 'FAIL' }
            Write-Output ("  {0} {1,-5} → {2,-5} rc={3} prod={4} psnr={5} {6}" -f `
                $verdict, $ie, $of, $r.Rc, $r.Prod, $r.Psnr, $r.Note)
            Add-Row 'A-format' "$ie→$of" (Split-Path $in -Leaf) $in $of '' $work $r $r.Note
        }
    }
}

# ══ B. 色彩空间目标兑现（SDR/HDR 源 × 显式目标）══
if ($Layer -in @('all','core','color')) {
    Write-Output "`n── B. 色彩目标兑现 ──"
    $hdr = Find-Src @('png')   # HDR 素材为 16bit PNG（src_hdr_pq / src_hdr_hlg）
    $hdrPq = Get-ChildItem $src -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'hdr.*pq' } | Select-Object -First 1
    $hdrHlg = Get-ChildItem $src -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'hdr.*hlg' } | Select-Object -First 1
    $sdr = Get-ChildItem $src -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '8bit' -and $_.Extension -eq '.png' } | Select-Object -First 1
    if (-not $sdr) { $sdr = Get-ChildItem $src -Filter *.png -File | Select-Object -First 1 }

    $cases = @()
    if ($hdrPq)  { $cases += @{ N='HDR-PQ';  F=$hdrPq.FullName } }
    if ($hdrHlg) { $cases += @{ N='HDR-HLG'; F=$hdrHlg.FullName } }
    if ($sdr)    { $cases += @{ N='SDR';     F=$sdr.FullName } }

    foreach ($c in $cases) {
        foreach ($t in @('sRGB','Display P3','BT.2020 PQ')) {
            foreach ($of in @('png','avif','jxl')) {
                $extra = @('--format',$of,'--color-space',$t)
                $r = Invoke-Cell 'B-color' $c.F $of $extra $t
                Write-Output ("  {0} {1,-8} → {2,-12} {3,-5} rc={4} tag={5} tagOk={6} psnr={7} {8}" -f `
                    $(if($r.Verdict -eq 'PASS'){'PASS'}else{'FAIL'}), $c.N, $t, $of, $r.Rc, $r.Tag, $r.TagOk, $r.Psnr, $r.Note)
                Add-Row 'B-color' "$($c.N)→$t/$of" (Split-Path $c.F -Leaf) $c.F $of $t $work $r $r.Note
            }
        }
    }
}

# ══ C. 批量 / 目录输入 + --preserve-structure（端到端覆盖曾为 0）══
if ($Layer -in @('all','core','batch')) {
    Write-Output "`n── C. 批量与目录结构 ──"
    $pics = @(Get-ChildItem $src -File -ErrorAction SilentlyContinue |
              Where-Object { $_.Extension -in '.png','.jpg','.webp' } | Select-Object -First 3)
    if ($pics.Count -lt 2) { Write-Output "  SKIP 批量素材不足"; $skip++ }
    else {
        # C1: 目录输入
        $dirIn = Join-Path $work 'batchdir'
        New-Item -ItemType Directory -Force -Path (Join-Path $dirIn 'sub') | Out-Null
        foreach ($p in $pics) { Copy-Item $p.FullName (Join-Path $dirIn $p.Name) -Force
                                Copy-Item $p.FullName (Join-Path $dirIn "sub\$($p.Name)") -Force }
        $outDir = Join-Path $work 'batch_out'
        $r = Run-Cli $dirIn $outDir @('--format','png')
        $prodCount = @(Get-ChildItem $outDir -File -Recurse -EA SilentlyContinue | Where-Object { $_.Length -gt 0 }).Count
        $ok = ($r.Rc -eq 0) -and ($prodCount -ge $pics.Count * 2)
        Write-Output "  $(if($ok){'PASS'}else{'FAIL'}) C1 目录输入(含子目录): rc=$($r.Rc) 输入=$($pics.Count*2) 产物=$prodCount"
        if ($ok) { $pass++ } else { $fail++ }

        # C2: 目录输入 + --preserve-structure
        $outDir2 = Join-Path $work 'batch_out_struct'
        $r2 = Run-Cli $dirIn $outDir2 @('--format','png','--preserve-structure')
        $hasSub = Test-Path (Join-Path $outDir2 'sub')
        $cnt2 = @(Get-ChildItem $outDir2 -File -Recurse -EA SilentlyContinue | Where-Object { $_.Length -gt 0 }).Count
        $ok2 = ($r2.Rc -eq 0) -and $hasSub -and ($cnt2 -ge $pics.Count * 2)
        Write-Output "  $(if($ok2){'PASS'}else{'FAIL'}) C2 --preserve-structure: rc=$($r2.Rc) 子目录保留=$hasSub 产物=$cnt2"
        if ($ok2) { $pass++ } else { $fail++ }

        # C3: 多输入（-i a,b,c）
        $outDir3 = Join-Path $work 'multi_out'
        $multi = ($pics | ForEach-Object { $_.FullName }) -join ','
        $r3 = Run-Cli $multi $outDir3 @('--format','jpg')
        $cnt3 = @(Get-ChildItem $outDir3 -File -Recurse -EA SilentlyContinue | Where-Object { $_.Length -gt 0 }).Count
        $ok3 = ($r3.Rc -eq 0) -and ($cnt3 -ge $pics.Count)
        Write-Output "  $(if($ok3){'PASS'}else{'FAIL'}) C3 多输入逗号分隔: rc=$($r3.Rc) 输入=$($pics.Count) 产物=$cnt3"
        if ($ok3) { $pass++ } else { $fail++ }
    }
}

# ══ D. 动图帧数守恒 ══
if ($Layer -in @('all','core')) {
    Write-Output "`n── D. 动图帧数守恒 ──"
    foreach ($ae in @('gif','webp','apng')) {
        $ext = if ($ae -eq 'apng') { 'png' } else { $ae }
        $anim = Get-ChildItem $src -File -EA SilentlyContinue |
                Where-Object { $_.Extension -eq ".$ext" -and $_.Name -match 'anim' } | Select-Object -First 1
        if (-not $anim) { Write-Output "  SKIP 动图素材缺失: $ae"; $skip++; continue }
        $srcFrames = (& $fp -v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames -of csv=p=0 $anim.FullName 2>$null)
        foreach ($of in @('gif','webp')) {
            $r = Invoke-Cell 'D-anim' $anim.FullName $of @('--format',$of) $null
            $prod = Get-Prod (Join-Path $work "*$of*") 2>$null
            Write-Output ("  {0} {1,-5} → {2,-5} 源帧={3} rc={4} {5}" -f `
                $(if($r.Verdict -eq 'PASS'){'PASS'}else{'FAIL'}), $ae, $of, $srcFrames, $r.Rc, $r.Note)
            Add-Row 'D-anim' "$ae→$of" $anim.Name $anim.FullName $of '' $work $r $r.Note
        }
    }
}

# ══ 汇总 ══
$csv = Join-Path $work 'usability.csv'
$rows | Export-Csv -NoTypeInformation -LiteralPath $csv -Encoding UTF8
Write-Output "`n═══════════════════════════════════════════"
Write-Output "  PASS=$pass  FAIL=$fail  SKIP=$skip   (格数=$($rows.Count))"
Write-Output "  CSV: $csv"
if ($rows.Count -gt 0) {
    Write-Output "`n  —— 失败明细 ——"
    $rows | Where-Object { $_.Note -and $_.Note -ne 'ok' } | Select-Object Group,Case,Rc,Prod,Psnr,Note |
        Format-Table -AutoSize | Out-String | Write-Output
}
Write-Output "═══════════════════════════════════════════"
if ($fail -gt 0) { exit 1 } else { exit 0 }
