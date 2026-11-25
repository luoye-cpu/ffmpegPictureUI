[CmdletBinding()]
param([switch]$SelfCheck)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$probeSource = Join-Path $root 'tests\projects\UiHostShared\GuiCombinationCoverage.cs'
$requiredAnchors = @(
    '--gui-color-combinations', 'GUI-COMBINATION', 'yuv_tilegrid_heic',
    'CoreEnumerated', 'PolicyEnumerated', 'GeometryEnumerated', 'UniversalEnumerated',
    'independent-ffmpeg-matrix-oracle', 'pixel-route-count=36'
)
if ($SelfCheck) {
    $text = Get-Content -LiteralPath $PSCommandPath -Raw
    $bad = @($requiredAnchors | Where-Object { -not $text.Contains($_, [StringComparison]::Ordinal) })
    $probeText = Get-Content -LiteralPath $probeSource -Raw
    foreach ($anchor in @('CollectOptionsFromUiAsync', 'ResolveOutMatrixCapability', 'BuildPipeColorArgs',
                          'valid YUV matrix did not resolve to Convert', 'full discrete Cartesian accounting',
                          'UiWarningStates', 'ValidateEncodersAsync')) {
        if (-not $probeText.Contains($anchor, [StringComparison]::Ordinal)) { $bad += "probe:$anchor" }
    }
    if ($bad.Count -gt 0) { Write-Output ('SELF-CHECK FAIL: ' + ($bad -join ', ')); exit 1 }
    Write-Output 'SELF-CHECK OK: live GUI collection, universal Cartesian accounting, command families, pipe builder and pixel oracle anchors present'
    Write-Output 'PASS=1 (structural self-check only)'
    exit 0
}

function Fail([string]$Message) { throw $Message }
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { Fail $Message } }
$run = Join-Path $root ('tests\output\_tmp\gui-color-combinations\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $run | Out-Null
$tmp = Join-Path $root 'tests\output\_tmp\round-tmp'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
foreach ($key in @('TMP', 'TEMP', 'TMPDIR')) { [Environment]::SetEnvironmentVariable($key, $tmp, 'Process') }
$env:FFMPEGGUI_PLAN_DIR = Join-Path $root 'publish\PLAN'
$env:FFMPEGGUI_FFMPEG_DIR = Join-Path $root 'publish\PLAN\ffmpeg-full'
$ui = Join-Path $root 'tests\projects\UiTestHost\bin\Release\net11.0\win-x64\UiTestHost.exe'
$exe = Join-Path $root 'src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe'
$ff = Join-Path $root 'publish\PLAN\ffmpeg-full\ffmpeg.exe'
$srcDir = Join-Path $root 'tests\output\sources'
$png = Join-Path $srcDir 'src_8bit.png'
$heic = Join-Path $root 'tests\data\synth_heic_tilegrid_plain.heic'
foreach ($path in @($ui, $exe, $ff, $png, $heic)) { Require (Test-Path -LiteralPath $path) "missing: $path" }

try {
    $reportPath = Join-Path $run 'gui-combinations.json'
    $uiLog = Join-Path $run 'gui-combinations.log'
    & $ui $srcDir --gui-color-combinations --report $reportPath *> $uiLog
    $uiExit = $LASTEXITCODE
    Require ($uiExit -eq 0) "GUI combination probe exit=$uiExit; see $uiLog"
    Require (Test-Path -LiteralPath $reportPath) 'combination report missing'
    $coverage = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Require ($coverage.FailureCount -eq 0) "combination failures=$($coverage.FailureCount)"
    Require ($coverage.GpuDomainFinalized -eq $true) 'GPU runtime validation did not reach its terminal domain before collection'
    Require ($coverage.FormatEncoderStates -ge 7) "format/encoder states=$($coverage.FormatEncoderStates)"
    Require ($coverage.CoreEnumerated -gt 0 -and $coverage.PolicyEnumerated -gt 0 -and $coverage.GeometryEnumerated -gt 0) 'a Cartesian family was empty'
    Require ($coverage.CommandsBuilt -gt 0 -and $coverage.PipeCommandsBuilt -gt 0) 'command or external-pipe layer did not run'
    [long]$familyTotal = [long]$coverage.CoreEnumerated + [long]$coverage.PolicyEnumerated + [long]$coverage.GeometryEnumerated
    [long]$familyAccounted = [long]$coverage.CommandsBuilt + [long]$coverage.ExpectedRejects + [long]$coverage.UiLockedStates
    Require ($familyAccounted -eq $familyTotal) "command-family classification gap: $familyAccounted != $familyTotal"
    [long]$universalClassified = [long]$coverage.UniversalPlanned + [long]$coverage.UniversalUiLocked `
        + [long]$coverage.UniversalInputRejected + [long]$coverage.UniversalOutputRejected
    Require ($coverage.UniversalEnumerated -gt $coverage.CoreEnumerated) "universal discrete coverage was not expanded: $($coverage.UniversalEnumerated)"
    Require ($coverage.UniversalPlanned -gt 0) 'universal discrete coverage has no executable state'
    Require ($universalClassified -eq $coverage.UniversalEnumerated) "universal classification gap: $universalClassified != $($coverage.UniversalEnumerated)"
    Require (@($coverage.States | Where-Object { @($_.IccVersions).Count -eq 0 }).Count -eq 0) 'a live GUI state has no ICC-version domain'
    $liveInputMatrices = @($coverage.States | ForEach-Object { $_.InputMatrices })
    $liveToneMaps = @($coverage.States | ForEach-Object { $_.ToneMaps })
    Require ($liveInputMatrices -contains 'bt470bg' -and $liveInputMatrices -contains 'gbr') 'live input-matrix domain is incomplete'
    Require ($liveToneMaps -contains 'bt2446a') 'live tone-map domain is incomplete'
    Require (@($coverage.States | Where-Object { @($_.LosslessValues).Count -eq 0 }).Count -eq 0) 'a live GUI state has no lossless domain'

    $rows = [System.Collections.Generic.List[object]]::new()
    $matrices = @('bt709', 'bt2020nc', 'smpte170m')
    foreach ($shape in @('png', 'heic')) {
        $input = if ($shape -eq 'heic') { $heic } else { $png }
        foreach ($engine in @('auto', 'engine', 'legacy')) {
            foreach ($encoder in @('cjxl', 'ffmpeg')) {
                foreach ($matrix in $matrices) {
                    $tag = "$shape-$engine-$encoder-$matrix"
                    $out = Join-Path $run $tag
                    New-Item -ItemType Directory -Force -Path $out | Out-Null
                    $json = Join-Path $out 'run.jsonl'
                    $arguments = @(
                        '--headless', '-i', $input, '--format', 'jxl', '--encoder', $encoder,
                        '--color-engine', $engine, '--color-matrix', $matrix,
                        '--lossless', 'true', '--output', $out,
                        '--log-level', 'Debug', '--log-format', 'JsonLines', '--log-file', $json
                    )
                    $process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -NoNewWindow -PassThru `
                        -RedirectStandardOutput (Join-Path $out 'stdout.txt') -RedirectStandardError (Join-Path $out 'stderr.txt')
                    $jxl = Get-ChildItem -LiteralPath $out -Filter *.jxl -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                    $terminal = Get-Content -LiteralPath $json -ErrorAction SilentlyContinue | ConvertFrom-Json |
                        Where-Object c -eq 'item_completed' | Select-Object -Last 1
                    Require ($process.ExitCode -eq 0 -and $null -ne $terminal -and $terminal.code -eq 0) "$tag failed: process=$($process.ExitCode) item=$($terminal.code)"
                    Require ($null -ne $jxl -and $jxl.Length -gt 0) "$tag produced no non-empty JXL"
                    $actual = ''
                    $expected = ''
                    $precision = 'not-applicable'
                    if ($shape -eq 'heic') {
                        # independent-ffmpeg-matrix-oracle: decode the original derived group directly,
                        # without product code or product-generated intermediate files.
                        $actual = (& $ff -v error -i $jxl.FullName -map 0:v:0 -frames:v 1 -pix_fmt rgb24 -f md5 - 2>$null)
                        $stdoutText = Get-Content -LiteralPath (Join-Path $out 'stdout.txt') -Raw
                        $rgb48Intermediate = $stdoutText.Contains('format=rgb48le', [StringComparison]::Ordinal) `
                            -or $stdoutText.Contains('-pix_fmt rgb48le', [StringComparison]::Ordinal)
                        $precision = if ($rgb48Intermediate) { 'rgb48le→rgb24' } else { 'rgb24-direct' }
                        $filter = if ($rgb48Intermediate) {
                            # 仅当日志证明实际经过 rgb48le 中间件时，独立复现该精度路径。
                            "scale=in_color_matrix=$matrix`:in_range=tv,format=rgb48le,format=rgb24"
                        }
                        else {
                            "scale=in_color_matrix=$matrix`:in_range=tv,format=rgb24"
                        }
                        $expected = (& $ff -v error -colorspace $matrix -i $input `
                            -filter_complex $filter -frames:v 1 -pix_fmt rgb24 -f md5 - 2>$null)
                        Require ($LASTEXITCODE -eq 0 -and $actual -eq $expected -and $actual.StartsWith('MD5=')) "$tag pixel mismatch: expected=$expected actual=$actual"
                    }
                    $rows.Add([pscustomobject]@{
                        Shape = $shape; Engine = $engine; Encoder = $encoder; Matrix = $matrix; Precision = $precision
                        Exit = $process.ExitCode; Bytes = $jxl.Length; ActualMD5 = $actual; ExpectedMD5 = $expected
                    })
                }
            }
        }
    }
    Require ($rows.Count -eq 36) "pixel-route-count=36 expected, actual=$($rows.Count)"
    $groupRows = @($rows | Where-Object Shape -eq 'heic')
    Require ($groupRows.Count -eq 18) "HEIC pixel routes=$($groupRows.Count)"
    foreach ($matrix in $matrices) {
        foreach ($precisionName in @('rgb24-direct', 'rgb48le→rgb24')) {
            $hashes = @($groupRows | Where-Object { $_.Matrix -eq $matrix -and $_.Precision -eq $precisionName } |
                Select-Object -ExpandProperty ActualMD5 -Unique)
            Require ($hashes.Count -le 1) "$matrix / $precisionName routes disagree: $($hashes -join ' / ')"
        }
    }
    foreach ($precisionName in @('rgb24-direct', 'rgb48le→rgb24')) {
        $classRows = @($groupRows | Where-Object Precision -eq $precisionName)
        Require (@($classRows.ActualMD5 | Select-Object -Unique).Count -eq 3) "$precisionName did not produce three distinct matrix results"
    }
    $rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'pixel-routes.json') -Encoding utf8
    $summary = [pscustomobject]@{
        Result = 'PASS'; FormatEncoderStates = $coverage.FormatEncoderStates; TargetTemplates = $coverage.TargetTemplates
        Core = $coverage.CoreEnumerated; Policy = $coverage.PolicyEnumerated; Geometry = $coverage.GeometryEnumerated
        Universal = $coverage.UniversalEnumerated; UniversalPlanned = $coverage.UniversalPlanned
        UniversalUiWarnings = $coverage.UniversalUiWarnings; UniversalUiLocked = $coverage.UniversalUiLocked
        UniversalInputRejected = $coverage.UniversalInputRejected; UniversalOutputRejected = $coverage.UniversalOutputRejected
        CommandsBuilt = $coverage.CommandsBuilt; PipeCommandsBuilt = $coverage.PipeCommandsBuilt
        ExpectedRejects = $coverage.ExpectedRejects; UiWarningStates = $coverage.UiWarningStates
        UiLockedStates = $coverage.UiLockedStates
        RealConversions = $rows.Count; PixelOracleComparisons = $groupRows.Count
    }
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8
    $summary | Format-List | Out-String | Write-Output
    Write-Output 'PASS=54 (36 real conversions + 18 independent pixel-oracle comparisons; combination assertions separately closed)'
    Write-Output "PASS: GUI colour combination coverage + pixel routes; evidence=$run"
    exit 0
}
catch {
    Write-Output ("FAIL: " + $_.Exception.Message)
    Write-Output "evidence=$run"
    exit 1
}
