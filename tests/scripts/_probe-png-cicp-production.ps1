# 生产路径端到端：产品 CLI 输出 PNG 时是否真的补上 cICP（QueueProcessor 新分支）
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
# 工具链按“exe 同级目录/PLAN/”解析：apphost 不能改名（会去找同名 dll），
# 故在 bin 目录建一个指向 publish/PLAN 的**联接**，不往 publish/ 里拷构建产物。
$binDir = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64"
$exe = "$binDir/FfmpegGui.exe"
if (-not (Test-Path $exe)) { Write-Output "未找到 $exe"; exit 1 }
if (-not (Test-Path "$binDir/PLAN")) {
    New-Item -ItemType Junction -Path "$binDir/PLAN" -Target "$root/publish/PLAN" -EA SilentlyContinue | Out-Null
    Write-Output "  （已建联接 $binDir/PLAN → publish/PLAN）"
}
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$src = "$root/tests/output/gainmap/png3_cicp.png"
# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   本脚本**无 pass/fail 计数**（诊断型）⇒ 结尾无条件自清。
$out = "$root/tests/output/validate/png3prod_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $out | Out-Null
if (-not (Test-Path $src)) { Write-Output "缺源图 $src（先跑 GainMapTestHost）"; exit 1 }

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# 只读 stdout 版（取「工具的值」专用）：ffprobe 的 `-of csv` 字段值走 **stdout**，合并 stderr 会把
#   报错行当值返回 ⇒ 下面三行 `ffprobe:` 读数（本探针的唯一证据）可能被污染。
#   产品 headless 日志不经本函数（走 `$out/<tag>.err` 文件 + Get-Content），上面的合并版 Exec 因此
#   在本文件已无调用点 —— 保留它是为了「将来要读 ffmpeg stderr 时别再手写一份合并取数」。
function ExecOut([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return [System.IO.File]::ReadAllText($o)
}

function Dump([string]$p, [string]$tag) {
    $b = [System.IO.File]::ReadAllBytes($p); $pos = 8; $l = @()
    while ($pos + 12 -le $b.Length) {
        # ⚠ 必须 [int] 再移：PowerShell 里 `[byte]0x10 -shl 8` 被截断成 0（2026-09-26 实测）
        #   ⇒ 不 cast 的话，IDAT>255 B 的 PNG 一律遍历失步（与 verify-png3-interop 同一处坑）。
        $len = [int](([int]$b[$pos] -shl 24) -bor ([int]$b[$pos+1] -shl 16) -bor ([int]$b[$pos+2] -shl 8) -bor [int]$b[$pos+3])
        if ($len -lt 0 -or $pos + 12 + $len -gt $b.Length) { break }
        $t = [Text.Encoding]::ASCII.GetString($b, $pos + 4, 4)
        $hex = if ($len -ge 1 -and $len -le 8) { (($b[($pos+8)..($pos+7+$len)]) | ForEach-Object { $_.ToString("X2") }) -join ' ' } else { "" }
        $l += if ($hex) { "$t[$hex]" } else { $t }
        $pos += 12 + $len
    }
    Write-Output "  $tag → $($l -join ' ')"
}

foreach ($case in @(@("BT.2020 PQ", "pq"), @("Display P3", "p3"), @("auto", "auto"))) {
    $cs = $case[0]; $tag = $case[1]
    $o = "$out/$tag"; New-Item -ItemType Directory -Force -Path $o | Out-Null
    $a = "--headless -i `"$src`" -o `"$o`" -f png --color-space `"$cs`" --bit-depth 16"
    $p = Start-Process -FilePath $exe -ArgumentList $a -NoNewWindow -Wait -PassThru -RedirectStandardError "$out/$tag.err"
    $made = Get-ChildItem "$o/*.png" -EA SilentlyContinue | Select-Object -First 1
    Write-Output "── $cs (exit=$($p.ExitCode)) → $(if ($made) { $made.Name } else { '无产物' })"
    if (-not $made) { Get-Content "$out/$tag.err" -EA SilentlyContinue | Select-Object -First 3 | ForEach-Object { "    ! $_" }; continue }
    Dump $made.FullName "chunk"
    $probe = (((ExecOut $fp "-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer -of csv=p=0 `"$($made.FullName)`"") -split "`r?`n") -join ",")
    Write-Output "    ffprobe: $probe"
    Get-Content "$out/$tag.err" -EA SilentlyContinue | Select-String -Pattern "png3" | Select-Object -First 3 | ForEach-Object { "      log: $($_.Line.Trim())" }
}

# 预期：cICP 被**携带**而非丢弃（源容器自带 CICP ⇒ 不写就是丢信息）。
$p3out = "$out/p3/png3_cicp.png"
if (Test-Path $p3out) {
    $o2 = "$out/carry"; New-Item -ItemType Directory -Force -Path $o2 | Out-Null
    $a2 = "--headless -i `"$p3out`" -o `"$o2`" -f png --color-strategy carry --bit-depth 16"
    $p2 = Start-Process -FilePath $exe -ArgumentList $a2 -NoNewWindow -Wait -PassThru -RedirectStandardError "$out/carry.err"
    $made2 = Get-ChildItem "$o2/*.png" -EA SilentlyContinue | Select-Object -First 1
    Write-Output "── S2 carry（源含 cICP+iCCP）(exit=$($p2.ExitCode)) → $(if ($made2) { $made2.Name } else { '无产物' })"
    if ($made2) {
        Dump $made2.FullName "chunk"
        Write-Output ("    ffprobe: " + (((ExecOut $fp "-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer -of csv=p=0 `"$($made2.FullName)`"") -split "`r?`n") -join ","))
        $t2 = [Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($made2.FullName))
        Write-Output ("    含 cICP chunk: {0}   含 iCCP chunk: {1}" -f $t2.Contains("cICP"), $t2.Contains("iCCP"))
    }
}

# ── S2 + 源只有标准 ICC（无 CICP）：用带 P3 ICC 的 JPEG 作源 ⇒ 应从 ICC 量出并写 cICP ──
$jpgP3 = "$root/tests/output/validate/jpgp3/p3.jpg"
if (Test-Path $jpgP3) {
    $o3 = "$out/icc2cicp"; New-Item -ItemType Directory -Force -Path $o3 | Out-Null
    $a3 = "--headless -i `"$jpgP3`" -o `"$o3`" -f png --color-strategy carry --bit-depth 16"
    $p3 = Start-Process -FilePath $exe -ArgumentList $a3 -NoNewWindow -Wait -PassThru -RedirectStandardError "$out/icc2cicp.err"
    $made3 = Get-ChildItem "$o3/*.png" -EA SilentlyContinue | Select-Object -First 1
    Write-Output "── S2 carry，源=JPEG(仅 P3 ICC、无 CICP)(exit=$($p3.ExitCode)) → $(if ($made3) { $made3.Name } else { '无产物' })"
    if ($made3) {
        Dump $made3.FullName "chunk"
        Write-Output ("    ffprobe: " + (((ExecOut $fp "-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer -of csv=p=0 `"$($made3.FullName)`"") -split "`r?`n") -join ","))
        Get-Content "$out/icc2cicp.err" -EA SilentlyContinue | Select-String -Pattern "png3" | Select-Object -First 2 | ForEach-Object { "      log: $($_.Line.Trim())" }
    }
} else { Write-Output "── SKIP：缺 $jpgP3（先跑 _probe-jpg-p3-icc.ps1）" }

Write-Output ""
Write-Output "===== PNG cICP 生产路径验证结束 ====="
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

