$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$pr = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$d  = "$root/tests/output/validate/cmsx"

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# xcheck 内部：A 用例的 zimg 输出 ($d/A/zimg.png) 与输入 (bars16.png) 是否逐码值相同？
# 相同 ⇒ zscale 的 tin/t 在这条命令里没起作用（参照失效），需要换参照写法。
foreach ($case in @("A","B")) {
  $z = "$d/$case/zimg.png"
  if (-not (Test-Path $z)) { Write-Output "  ${case}: 无 zimg.png"; continue }
  $rawA = "$d/${case}_in.raw"; $rawB = "$d/${case}_zimg.raw"
  Exec $ff "-y -v error -i `"$d/bars16.png`" -vf format=rgb48le -f rawvideo `"$rawA`"" | Out-Null
  Exec $ff "-y -v error -i $z -vf format=rgb48le -f rawvideo `"$rawB`"" | Out-Null
  $a = [System.IO.File]::ReadAllBytes($rawA); $b = [System.IO.File]::ReadAllBytes($rawB)
  $n = [Math]::Min($a.Length, $b.Length); $maxd = 0; $ndiff = 0
  for ($i = 0; $i + 2 -le $n; $i += 2) {
    # ⚠ [int] 必须先转：PowerShell 的 `[byte] x -shl 8` 截断回 byte ⇒ 高位字节恒丢（>255 的 16-bit 样本读成低字节）
    $v1 = [int]$a[$i] + ([int]$a[$i+1] -shl 8); $v2 = [int]$b[$i] + ([int]$b[$i+1] -shl 8)
    $v1 = $a[$i] + ($a[$i+1] -shl 8); $v2 = $b[$i] + ($b[$i+1] -shl 8)
    $dv = [Math]::Abs($v1 - $v2); if ($dv -gt $maxd) { $maxd = $dv }; if ($dv -gt 1) { $ndiff++ }
  }
  # 同时看 zimg 输出是否被打了不同的标签
  $tagZ = ((Exec $ff "-hide_banner -i `"$z`"") -split "`r?`n" | Select-String "Stream.*Video" | ForEach-Object { $_.Line.Trim() }) -join " "
  $tagI = ((Exec $ff "-hide_banner -i `"$d/bars16.png`"") -split "`r?`n" | Select-String "Stream.*Video" | ForEach-Object { $_.Line.Trim() }) -join " "
  Write-Output ("  {0}: zimg输出 vs 输入 maxDiff={1}/65535 差异样本数={2}" -f $case, $maxd, $ndiff)
  Write-Output ("       输入流: {0}" -f $tagI)
  Write-Output ("       zimg流: {0}" -f $tagZ)
}
