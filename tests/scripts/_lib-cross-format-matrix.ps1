[CmdletBinding()]
param([switch]$SelfCheck)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$probeSource = Join-Path $root 'tests\projects\UiHostShared\GuiCombinationCoverage.cs'
$requiredAnchors = @(
    '--gui-real-formats', 'GUI-REAL-FORMATS', 'RunRealFormats',
    'independent-ffmpeg-matrix-oracle', 'WrongBestMatrix', 'NonDistinctMatrixGroups',
    'CrossOracleEvaluations', 'QueueProcessor', 'libjxr', 'EncoderRoutingFailures'
)
if ($SelfCheck) {
    $text = Get-Content -LiteralPath $PSCommandPath -Raw
    $bad = @($requiredAnchors | Where-Object { -not $text.Contains($_, [StringComparison]::Ordinal) })
    $probeText = Get-Content -LiteralPath $probeSource -Raw
    foreach ($anchor in @('RunRealFormats', 'CollectStates(window, heic)', 'ValidateEncodersAsync',
                          'queue.Start(1)', 'queue.StopAfterCurrentQueue()', 'synth_heic_tilegrid_plain_256.heic')) {
        if (-not $probeText.Contains($anchor, [StringComparison]::Ordinal)) { $bad += "probe:$anchor" }
    }
    if ($bad.Count -gt 0) { Write-Output ('SELF-CHECK FAIL: ' + ($bad -join ', ')); exit 1 }
    Write-Output 'SELF-CHECK OK: live GUI states, real QueueProcessor routes and independent cross-matrix oracle anchors present'
    Write-Output 'PASS=1 (structural self-check only)'
    exit 0
}

function Fail([string]$Message) { throw $Message }
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { Fail $Message } }
function Get-Psnr([string]$Actual, [string]$Expected, [string]$LogPath) {
    $text = (& $script:ff -hide_banner -nostats `
        -f rawvideo -pixel_format rgb24 -video_size 256x256 -i $Actual `
        -f rawvideo -pixel_format rgb24 -video_size 256x256 -i $Expected `
        -lavfi psnr -f null - 2>&1 | Out-String)
    $text | Set-Content -LiteralPath $LogPath -Encoding utf8
    if ($text -match 'average:(inf|[0-9]+(?:\.[0-9]+)?)') {
        if ($Matches[1] -eq 'inf') { return [double]::PositiveInfinity }
        return [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
    }
    Fail "PSNR parse failed; see $LogPath"
}

$run = Join-Path $root ('tests\output\_tmp\cross-format-matrix-gate\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $run | Out-Null
$tmp = Join-Path $root 'tests\output\_tmp\round-tmp'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
foreach ($key in @('TMP', 'TEMP', 'TMPDIR')) { [Environment]::SetEnvironmentVariable($key, $tmp, 'Process') }
$env:FFMPEGGUI_PLAN_DIR = Join-Path $root 'publish\PLAN'
$env:FFMPEGGUI_FFMPEG_DIR = Join-Path $root 'publish\PLAN\ffmpeg-full'
$ui = Join-Path $root 'tests\projects\UiTestHost\bin\Release\net11.0\win-x64\UiTestHost.exe'
$script:ff = Join-Path $root 'publish\PLAN\ffmpeg-full\ffmpeg.exe'
$srcDir = Join-Path $root 'tests\output\sources'
$heic = Join-Path $root 'tests\data\synth_heic_tilegrid_plain_256.heic'
foreach ($path in @($ui, $script:ff, $heic)) { Require (Test-Path -LiteralPath $path) "missing: $path" }

try {
    $reportPath = Join-Path $run 'gui-real-formats.json'
    $uiLog = Join-Path $run 'gui-real-formats.log'
    & $ui $srcDir --gui-real-formats --report $reportPath *> $uiLog
    $uiExit = $LASTEXITCODE
    Require (Test-Path -LiteralPath $reportPath) "real-format report missing; host exit=$uiExit; see $uiLog"
    $coverage = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $routes = @($coverage.Routes)
    Require ($coverage.FormatEncoderStates -gt 0) 'live GUI returned no format/encoder states'
    Require ($coverage.ExpectedRoutes -eq $coverage.FormatEncoderStates * 9) "expected-route accounting=$($coverage.ExpectedRoutes), states=$($coverage.FormatEncoderStates)"
    Require ($routes.Count -eq $coverage.ExpectedRoutes) "route count=$($routes.Count), expected=$($coverage.ExpectedRoutes)"
    Require (@($routes.StateIndex | Select-Object -Unique).Count -eq $coverage.FormatEncoderStates) 'route/state accounting mismatch'

    # independent-ffmpeg-matrix-oracle: decode the original HEIC derived group directly,
    # never through product code or product-generated intermediate files.
    $oracleDir = Join-Path $run 'oracles'
    New-Item -ItemType Directory -Force -Path $oracleDir | Out-Null
    $matrices = @('bt709', 'bt2020nc', 'smpte170m')
    $oracles = @{}
    $oracleHashes = @{}
    foreach ($matrix in $matrices) {
        $oracle = Join-Path $oracleDir "$matrix.rgb"
        & $script:ff -v error -y -colorspace $matrix -i $heic `
            -filter_complex "scale=in_color_matrix=$matrix`:in_range=tv,format=rgb24" `
            -frames:v 1 -pix_fmt rgb24 -f rawvideo $oracle 2> (Join-Path $oracleDir "$matrix.log")
        Require ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $oracle) -and (Get-Item -LiteralPath $oracle).Length -gt 0) "oracle decode failed: $matrix"
        $oracles[$matrix] = $oracle
        $oracleHashes[$matrix] = (Get-FileHash -Algorithm MD5 -LiteralPath $oracle).Hash
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    [int]$conversionFailures = 0
    [int]$pixelReadFailures = 0
    [int]$wrongBest = 0
    [int]$nonDistinct = 0
    [int]$losslessMismatches = 0
    $routingErrors = [System.Collections.Generic.List[string]]::new()
    [double]$minCorrectPsnr = [double]::PositiveInfinity
    foreach ($route in $routes) {
        $key = "$($route.Format)-$($route.StateIndex)-$($route.Engine)-$($route.Matrix)"
        $routeDir = Join-Path $run $key
        New-Item -ItemType Directory -Force -Path $routeDir | Out-Null
        [int]$exit = if ($null -eq $route.ExitCode) { -1 } else { [int]$route.ExitCode }
        [long]$bytes = if (Test-Path -LiteralPath $route.Artifact) { (Get-Item -LiteralPath $route.Artifact).Length } else { 0 }
        if ($exit -ne 0 -or $bytes -le 0) { $conversionFailures++ }
        $actual = Join-Path $routeDir 'actual.rgb'
        $actualHash = ''
        $bestMatrix = ''
        $correctPsnr = $null
        $psnr = @{}
        $pixelError = ''
        $routingError = ''
        if ($route.Engine -eq 'legacy' -and $route.EncoderLabel.StartsWith('libjxr', [StringComparison]::Ordinal)) {
            $itemLog = Get-Content -LiteralPath (Join-Path $route.Directory 'item.log') -Raw -ErrorAction SilentlyContinue
            if ($route.Encoder -ne 'libjxr' -or [int]$route.Backend -ne 0 `
                -or $route.Command -notmatch '-c:v\s+libjxr(?:\s|$)' `
                -or $itemLog -notmatch 'encoder\s+:\s+Lavc[^\r\n]*libjxr') {
                $routingError = 'GUI libjxr label did not execute the FFmpeg libjxr encoder'
                $routingErrors.Add("$key`: $routingError")
            }
        }
        if ($exit -eq 0 -and $bytes -gt 0) {
            & $script:ff -v error -y -i $route.Artifact -map 0:v:0 -frames:v 1 `
                -pix_fmt rgb24 -f rawvideo $actual 2> (Join-Path $routeDir 'decode.log')
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $actual) `
                -or (Get-Item -LiteralPath $actual).Length -ne (Get-Item -LiteralPath $oracles['bt709']).Length) {
                $pixelReadFailures++
                $pixelError = 'independent rgb24 decode failed or frame size changed'
            }
            else {
                $actualHash = (Get-FileHash -Algorithm MD5 -LiteralPath $actual).Hash
                [double]$best = [double]::NegativeInfinity
                foreach ($matrix in $matrices) {
                    [double]$value = Get-Psnr $actual $oracles[$matrix] (Join-Path $routeDir "psnr-$matrix.log")
                    $psnr[$matrix] = $value
                    if ($value -gt $best) { $best = $value; $bestMatrix = $matrix }
                }
                $correctPsnr = $psnr[$route.Matrix]
                if ($bestMatrix -ne $route.Matrix) { $wrongBest++ }
                if (-not [double]::IsPositiveInfinity($correctPsnr)) {
                    if ($correctPsnr -lt $minCorrectPsnr) { $minCorrectPsnr = $correctPsnr }
                    if ($correctPsnr -lt 20.0) { $pixelError += " correct-matrix PSNR below 20 dB:$correctPsnr" }
                }
                if (($route.Format -eq 'png' -or $route.Format -eq 'tif') -and $actualHash -ne $oracleHashes[$route.Matrix]) {
                    $losslessMismatches++
                }
            }
        }
        $rows.Add([pscustomobject]@{
            StateIndex = $route.StateIndex; Format = $route.Format; Encoder = $route.Encoder; EncoderLabel = $route.EncoderLabel; Backend = $route.Backend
            Engine = $route.Engine; Matrix = $route.Matrix; Exit = $exit; Bytes = $bytes
            Artifact = $route.Artifact; Directory = $route.Directory
            PixelMD5 = $actualHash; BestMatrix = $bestMatrix; CorrectPsnr = $correctPsnr
            PsnrBt709 = $psnr['bt709']; PsnrBt2020nc = $psnr['bt2020nc']; PsnrSmpte170m = $psnr['smpte170m']
            PixelError = $pixelError; RoutingError = $routingError; ConversionError = $route.Error
        })
    }

    $groups = @($rows | Where-Object { $_.PixelMD5 } | Group-Object StateIndex, Engine)
    foreach ($group in $groups) {
        if (@($group.Group.PixelMD5 | Select-Object -Unique).Count -ne 3) { $nonDistinct++ }
    }
    $finiteRows = @($rows | Where-Object { $null -ne $_.CorrectPsnr -and -not [double]::IsPositiveInfinity([double]$_.CorrectPsnr) })
    if ($finiteRows.Count -eq 0) { $minCorrectPsnr = 0 }
    [int]$qualityFailures = @($rows | Where-Object { $_.PixelError }).Count
    $summary = [pscustomobject]@{
        Result = if ($uiExit -eq 0 -and @($coverage.Failures).Count -eq 0 -and $conversionFailures -eq 0 `
            -and $pixelReadFailures -eq 0 -and $wrongBest -eq 0 -and $nonDistinct -eq 0 `
            -and $losslessMismatches -eq 0 -and $qualityFailures -eq 0 -and $routingErrors.Count -eq 0) { 'PASS' } else { 'FAIL' }
        FormatEncoderStates = $coverage.FormatEncoderStates; Routes = $routes.Count
        ExecutedRoutes = @($routes | Where-Object { $null -ne $_.ExitCode }).Count
        ConversionFailures = $conversionFailures
        HarnessFailures = @($coverage.Failures).Count
        PixelReadFailures = $pixelReadFailures; WrongBestMatrix = $wrongBest
        NonDistinctMatrixGroups = $nonDistinct; LosslessMismatches = $losslessMismatches
        EncoderRoutingFailures = $routingErrors.Count; EncoderRoutingErrors = $routingErrors
        PixelOracleComparisons = @($rows | Where-Object { $_.BestMatrix }).Count
        CrossOracleEvaluations = @($rows | Where-Object { $_.BestMatrix }).Count * 3
        MinCorrectPsnr = $minCorrectPsnr; HostExit = $uiExit
    }
    $rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'pixel-routes.json') -Encoding utf8
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8
    $summary | Format-List | Out-String | Write-Output
    Require ($summary.Result -eq 'PASS') "cross-format matrix gate failed; see $run\summary.json and $uiLog"
    Write-Output "PASS=$($routes.Count + $summary.PixelOracleComparisons) ($($routes.Count) real conversions + $($summary.PixelOracleComparisons) independent best-matrix verdicts)"
    Write-Output "PASS: all live GUI format/encoder states apply the selected matrix; evidence=$run"
    exit 0
}
catch {
    Write-Output ("FAIL: " + $_.Exception.Message)
    Write-Output "evidence=$run"
    exit 1
}
