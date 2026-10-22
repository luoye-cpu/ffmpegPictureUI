param([string]$Path)
$e = $null; $t = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$t, [ref]$e)
if ($e) { $e | ForEach-Object { "L$($_.Extent.StartLineNumber): $($_.Message)" }; exit 1 }
"PS_OK"
