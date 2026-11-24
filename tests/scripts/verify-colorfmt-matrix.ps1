# Target Display P3: verify actual color descriptions, not ICC serialization identity.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = Join-Path $root 'src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe'
$ff = Join-Path $root 'publish/PLAN/ffmpeg-full/ffmpeg.exe'
$fp = Join-Path $root 'publish/PLAN/ffmpeg-full/ffprobe.exe'
$et = Join-Path $root 'publish/PLAN/exiftool/exiftool.exe'
$ji = Join-Path $root 'publish/PLAN/jxl/bin/jxlinfo.exe'
$d = Join-Path $root ('tests/output/validate/fmtmatrix_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $d | Out-Null
function Run-Tool([string]$tool, [string]$toolArgs, [string]$tag) {
    $so = Join-Path $d "$tag.out"; $se = Join-Path $d "$tag.err"
    $p = Start-Process -FilePath $tool -ArgumentList $toolArgs -NoNewWindow -Wait -PassThru -RedirectStandardOutput $so -RedirectStandardError $se
    return @{ Code = $p.ExitCode; Out = [IO.File]::ReadAllText($so) }
}
function U32([byte[]]$b,[int]$o) { return ([long]$b[$o]*16777216+[long]$b[$o+1]*65536+[long]$b[$o+2]*256+$b[$o+3]) }
function S15([byte[]]$b,[int]$o) {
    $u = U32 $b $o
    if ($u -ge 2147483648L) { $u -= 4294967296L }
    return $u / 65536.0
}
function Tag([byte[]]$b,[int]$o) { [Text.Encoding]::ASCII.GetString($b,$o,4) }
function CurveAt([byte[]]$b, [int]$o, [int]$n, [double]$x) {
    if ((Tag $b $o) -eq 'para' -and $b[$o+8] -eq 0) {
        $g = S15 $b ($o+12)
        switch ($b[$o+9]) {
            3 { if ($n -ge 32) { $a=S15 $b ($o+16); $k=S15 $b ($o+20); $c=S15 $b ($o+24); $cut=S15 $b ($o+28); if ($x -ge $cut) { return [Math]::Pow($a*$x+$k,$g) }; return $c*$x } }
            4 { if ($n -ge 40) { $a=S15 $b ($o+16); $k=S15 $b ($o+20); $c=S15 $b ($o+24); $cut=S15 $b ($o+28); if ($x -ge $cut) { return [Math]::Pow($a*$x+$k,$g)+(S15 $b ($o+32)) }; return $c*$x+(S15 $b ($o+36)) } }
        }
    } elseif ((Tag $b $o) -eq 'curv') {
        $nSamples = U32 $b ($o+8)
        if ($nSamples -ge 2 -and $nSamples*2+12 -le $n) {
            $t=$x*($nSamples-1); $j=[int][Math]::Floor($t); $k=[Math]::Min($j+1,$nSamples-1)
            $a=$o+12+2*$j; $z=$o+12+2*$k
            return (($b[$a]*256+$b[$a+1])*(1-($t-$j))+($b[$z]*256+$b[$z+1])*($t-$j)) / 65535.0
        }
    }
    return [double]::NaN
}
function Test-P3Profile([string]$path) {
    try {
        $b = [IO.File]::ReadAllBytes($path)
        if ($b.Length -lt 132 -or (Tag $b 36) -ne 'acsp' -or (Tag $b 16) -ne 'RGB ' -or (Tag $b 20) -ne 'XYZ ') { return $false }
        $size = U32 $b 0; $count = U32 $b 128; $end = 132+12*$count
        if ($size -gt $b.Length -or $end -gt $size) { return $false }
        $table=@{}
        for($i=132; $i -lt $end; $i+=12) {
            $s=Tag $b $i; $o=U32 $b ($i+4); $n=U32 $b ($i+8)
            if ($o -lt $end -or $n -lt 8 -or $o+$n -gt $size -or $table.ContainsKey($s)) { return $false }
            $table[$s]=@([int]$o,[int]$n)
        }
        # Display P3 D65 chromaticities Bradford-adapted to the ICC PCS D50.
        $target = @{rXYZ=@(.51512,.24120,-.00105);gXYZ=@(.29198,.69225,.04189);bXYZ=@(.15710,.06657,.78407);wtpt=@(.9642,1,.8249)}
        foreach($s in $target.Keys) {
            if (!$table.ContainsKey($s) -or $table[$s][1] -lt 20) { return $false }
            $o=$table[$s][0]; if ((Tag $b $o) -ne 'XYZ ') { return $false }
            for($i=0;$i -lt 3;$i++){ if ([Math]::Abs((S15 $b ($o+8+4*$i))-$target[$s][$i]) -gt .003) { return $false } }
        }
        foreach($s in 'rTRC','gTRC','bTRC') {
            if (!$table.ContainsKey($s) -or $table[$s][1] -lt 12) { return $false }
            foreach($x in .012,.035,.07,.18,.35,.5,.75,.95) {
                $y = CurveAt $b $table[$s][0] $table[$s][1] $x
                $ref = if ($x -le .04045) { $x / 12.92 } else { [Math]::Pow(($x+.055)/1.055,2.4) }
                if ([double]::IsNaN($y) -or [Math]::Abs($y-$ref) -gt .001) { return $false }
            }
        }
        if ($table.ContainsKey('cicp')) {
            $o=$table['cicp'][0]
            if ($table['cicp'][1] -ne 12 -or (Tag $b $o) -ne 'cicp' -or ($b[($o+8)..($o+11)] -join '/') -ne '12/13/0/1') { return $false }
        }
        return $true
    } catch { return $false }
}
$src=Join-Path $d 'src.png'
$r=Run-Tool $ff "-y -hide_banner -loglevel error -f lavfi -i testsrc2=s=64x48:d=0.04 -pix_fmt rgb48le -frames:v 1 -color_primaries bt2020 -color_trc smpte2084 `"$src`"" 'gensrc'
if ($r.Code -ne 0 -or !(Test-Path $src)) { Write-Output "Fixture failed: $d; PASS=0 FAIL=1"; exit 1 }
$pass=0; $fail=0
# Keep the execution list separate from this hard-coded contract: deleting a loop item must not shrink the gate silently.
$expectedMatrixFormats = @('png','jpg','webp','tiff','jxl','avif')
$matrixFormats = @('png','jpg','webp','tiff','jxl','avif')
$formatDelta = @(Compare-Object -ReferenceObject $expectedMatrixFormats -DifferenceObject $matrixFormats)
$uniqueFormats = @($matrixFormats | Sort-Object -Unique)
if ($expectedMatrixFormats.Count -ne 6 -or $matrixFormats.Count -ne 6 -or
    $uniqueFormats.Count -ne 6 -or $formatDelta.Count -ne 0) {
    Write-Output ("FAIL format matrix contract: expected 6 formats [" + ($expectedMatrixFormats -join ',') +
        "]; loop has " + $matrixFormats.Count + " cells [" + ($matrixFormats -join ',') + "]")
    exit 1
}
Write-Output ("PASS format matrix contract: 6/6 formats [" + ($matrixFormats -join ',') + "]")
foreach($f in $matrixFormats) {
    $o = Join-Path $d $f; New-Item -ItemType Directory -Path $o | Out-Null
    $r = Run-Tool $exe "--headless -i `"$src`" -o `"$o`" -f $f --color-space `"Display P3`"" "run_$f"
    $out = Get-ChildItem -LiteralPath $o -File | Select-Object -First 1
    if ($r.Code -ne 0 -or !$out) { $fail++; Write-Output "FAIL ${f}: no product (rc=$($r.Code))"; continue }
    $probe = Run-Tool $fp "-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer -of json `"$($out.FullName)`"" "probe_$f"
    if ($probe.Code -ne 0) { $fail++; Write-Output "FAIL ${f}: ffprobe could not read color tags (rc=$($probe.Code))"; continue }
    try { $p = $probe.Out | ConvertFrom-Json -ErrorAction Stop; $pt = $p.streams[0] }
    catch { $fail++; Write-Output "FAIL ${f}: invalid ffprobe JSON/output"; continue }
    if ($null -eq $pt) { $fail++; Write-Output "FAIL ${f}: ffprobe returned no video stream"; continue }
    $encoded = $pt.color_primaries -eq 'smpte432' -and $pt.color_transfer -eq 'iec61966-2-1'
    $known = $f -in 'png','jxl','avif' -and ($pt.color_primaries -notin @($null,'unknown','unspecified') -or $pt.color_transfer -notin @($null,'unknown','unspecified'))
    if ($f -eq 'jxl' -and (Test-Path $ji)) {
        $jRun = Run-Tool $ji "`"$($out.FullName)`"" 'jxl_info'
        if ($jRun.Code -ne 0) { $fail++; Write-Output "FAIL jxl: jxlinfo read failed (rc=$($jRun.Code))"; continue }
        if ($jRun.Out -match '(?i)(P3|0\.6800).*sRGB') { $encoded=$true }
    }
    $icc = Join-Path $d "$f.icc"
    $iccRead = Run-Tool $et "-q -b -ICC_Profile -W `"$icc`" `"$($out.FullName)`"" "icc_$f"
    if ($iccRead.Code -ne 0) { $fail++; Write-Output "FAIL ${f}: ExifTool ICC read failed (rc=$($iccRead.Code))"; continue }
    $hasIcc = (Test-Path -LiteralPath $icc -PathType Leaf) -and (Get-Item -LiteralPath $icc).Length -gt 0
    $profileOk = $hasIcc -and (Test-P3Profile $icc)
    if (($encoded -or $profileOk) -and (!$hasIcc -or $profileOk) -and (!$known -or $encoded)) {
        $pass++; Write-Output "PASS ${f}: P3/sRGB (ICC=$profileOk; CICP=$encoded)"
    } else { $fail++; Write-Output "FAIL ${f}: ICC present=$hasIcc P3=$profileOk; tags=$($pt.color_primaries)/$($pt.color_transfer)" }
}
Write-Output "PASS=$pass FAIL=$fail; evidence=$d"
if ($fail -gt 0) { exit 1 } else { exit 0 }
