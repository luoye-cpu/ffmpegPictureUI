# 跨格式"目标=Display P3(SDR)"可用性矩阵：ICC / CICP 到底有没有进产物
# 判据全部来自第三方工具（exiftool / ffprobe / jxlinfo），不用产品自己的解析器自证。
# 注：本环境不允许直接 `& exe`，一律走 Start-Process + 文件捕获。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$runDir  = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64"
# 产品 exe 可能在 win-x64/ 或其 native/ 子目录（AOT 产物位置），逐个探测；
# 工具链按“exe 同级/PLAN”解析，故后面的 PLAN 联接必须建在**真正 exe 所在目录**。
$exe = @("$runDir/FfmpegGui.exe", "$runDir/native/FfmpegGui.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$binDir = if ($exe) { Split-Path $exe } else { $runDir }
$ff      = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp      = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$ex      = "$root/publish/PLAN/exiftool/exiftool.exe"
$jxlinfo = "$root/publish/PLAN/jxl/bin/jxlinfo.exe"
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$d       = "$root/tests/output/validate/fmtmatrix_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$src     = "$d/src.png"
$refIcc  = "$root/tests/output/validate/jpgp3/p3.icc"
New-Item -ItemType Directory -Force -Path $d | Out-Null
# ⚠ 2026-10-02 加牙：本脚本**原有结构性缺陷**——表格型输出、**无 pass/fail 计数** ⇒
#   运行器只能看 exit code，而它在任何判定下都 exit 0 ⇒ **结构上永不可能失败**。
#   现补计数与退出码：`❌` ⇒ FAIL（exit 1）；`✅` ⇒ PASS；`⚠` ⇒ WARN（**不计入 FAIL**）。
#   ⚠ **为什么 ⚠ 不计入 FAIL**（实测澄清）：`⚠ 有 ICC 但非 P3 参照` 的判据是
#     「与 _probe-jpg-p3-icc.ps1 的参照 ICC **逐字节相等**」—— 实测产物 ICC 是 "RGB built-in" 的
#     **Display P3**（D50 适应后 rXYZ=0.51512,0.2412,-0.00105 / gXYZ=0.29198,0.69225,0.04189 /
#     bXYZ=0.1571,0.06657,0.78407，与 P3 标准值一致），只是与参照**不是同一份字节**。
#     ⇒ 该分支是**假警报**；判 FAIL 会造假红。**待办**：把判据改成「色度是否为 P3」而非字节相等，
#       改完后 ⚠ 才可升格为 FAIL。**在那之前不擅自升格。**
#   ⚠ 下面两处「前置条件失败」的提前 `exit 1` 也要各自先自清（否则留 GUID 残留）。
foreach ($need in @($exe, $ff, $fp, $ex)) { if (-not (Test-Path $need)) { Write-Output "缺 $need"; Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; exit 1 } }
if (-not (Test-Path "$binDir/PLAN")) {
    New-Item -ItemType Junction -Path "$binDir/PLAN" -Target "$root/publish/PLAN" -EA SilentlyContinue | Out-Null
}

# 统一执行器：返回 @{Code=..; Out='..'}
# ⚠ 参数名不能叫 $args：它与 PowerShell 自动变量同名，传下去会变成空参
#   （实测 ffmpeg 只收到 Usage 提示、源文件生不出来 ⇒ 脚本自己报“生成源失败”）。
function Run-Tool([string]$tool, [string]$toolArgs, [string]$tag) {
    $so = "$d/$tag.out"; $se = "$d/$tag.err"
    $p = Start-Process -FilePath $tool -ArgumentList $toolArgs -NoNewWindow -Wait -PassThru `
                       -RedirectStandardOutput $so -RedirectStandardError $se
    return @{ Code = $p.ExitCode; Out = ((Get-Content $so -EA SilentlyContinue) -join " ").Trim();
              Err = ((Get-Content $se -EA SilentlyContinue) -join " ").Trim() }
}

# 源：HDR/PQ 标记的 16-bit PNG
$r = Run-Tool $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=64x48:d=0.04`" -pix_fmt rgb48le -frames:v 1 -color_primaries bt2020 -color_trc smpte2084 `"$src`"" "gensrc"
if (-not (Test-Path $src)) { Write-Output "生成源失败: $($r.Err)"; Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; exit 1 }
$refMd5 = if (Test-Path $refIcc) { (Get-FileHash $refIcc -Algorithm MD5).Hash.Substring(0, 8) } else { "无参照" }
Write-Output "参照 P3 ICC md5 = $refMd5（来自 _probe-jpg-p3-icc.ps1，色度已核）"
$pass = 0; $fail = 0; $warn = 0
Write-Output ("{0,-6} {1,-20} {2,-30} {3}" -f "格式", "ICC(大小/md5)", "CICP/其他", "判定")

foreach ($f in @("png", "jpg", "webp", "tiff", "jxl", "avif")) {
    $o = "$d/$f"; New-Item -ItemType Directory -Force -Path $o | Out-Null
    $null = Run-Tool $exe "--headless -i `"$src`" -o `"$o`" -f $f --color-space `"Display P3`"" "run_$f"
    $out = Get-ChildItem "$o/*.*" -EA SilentlyContinue | Select-Object -First 1
    if (-not $out) {
        $fail++
        Write-Output ("{0,-6} {1,-20} {2,-30} {3}" -f $f, "-", "-", "❌ 无产物")
        (Get-Content "$d/run_$f.out" -EA SilentlyContinue | Select-String "失败|错误|❌" | Select-Object -First 2) |
            ForEach-Object { "      $($_.Line.Trim())" }
        continue
    }
    # ICC：exiftool 提取（-b -w，避免重定向损坏二进制）
    $null = Run-Tool $ex "-q -b -ICC_Profile -w `"$d/%f.icc`" `"$($out.FullName)`"" "icc_$f"
    $iccFile = "$d/$($out.BaseName).icc"
    $iccInfo = "-"; $verdict = ""
    if (Test-Path $iccFile) {
        $m = (Get-FileHash $iccFile -Algorithm MD5).Hash.Substring(0, 8)
        $iccInfo = "$((Get-Item $iccFile).Length)B/$m"
        $verdict = if ($m -eq $refMd5) { $pass++; "✅ ICC=P3（与参照同字节）" } else { $warn++; "⚠ 有 ICC 但非 P3 参照（见头部：判据是字节相等，已知假警报）" }
    }
    # CICP：ffprobe（JXL 另用 jxlinfo，因 exiftool/ffprobe 读不到其 ICC）
    $cicp = (Run-Tool $fp "-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer -of csv=p=0 `"$($out.FullName)`"" "probe_$f").Out
    if ($f -eq "jxl" -and (Test-Path $jxlinfo)) {
        $cicp = "jxlinfo: " + (Run-Tool $jxlinfo "`"$($out.FullName)`"" "jxl_$f").Out
    }
    if ($cicp.Length -gt 28) { $cicp = $cicp.Substring(0, 28) + "…" }
    # ⚠ 计数口径：**每格只计一次**。CICP 命中会覆盖 ICC 判定 ⇒ 先撤掉上面记的那一次再按 CICP 记。
    if ($cicp -match "smpte432") {
        if ($verdict -like "✅*") { $pass-- } elseif ($verdict -like "⚠*") { $warn-- }
        $pass++; $verdict = "✅ CICP=P3"
    }
    if ($iccInfo -eq "-" -and $cicp -notmatch "smpte432") {
        $fail++; $verdict = "❌ 产物无任何 P3 描述（会被按 sRGB 误读）"
    }
    Write-Output ("{0,-6} {1,-20} {2,-30} {3}" -f $f, $iccInfo, $cicp, $verdict)
}

# ── 汇总与退出码（2026-10-02 加牙）──────────────────────────────────────────
# ⚠ 运行器的零断言闸只认 PASS=n 形态 ⇒ 汇总行必须带 `PASS=<数字>`。
Write-Output ""
Write-Output "PASS=$pass FAIL=$fail WARN=$warn（表格型；WARN 见头部说明，不计入 FAIL）"
# ⚠ 退出码必须**成对**给出（防污染 $LASTEXITCODE）。有 FAIL 时保留工作目录以便取证。
if ($fail -gt 0) { Write-Output "⚠ 保留工作目录以便取证: $d"; exit 1 }
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
exit 0
