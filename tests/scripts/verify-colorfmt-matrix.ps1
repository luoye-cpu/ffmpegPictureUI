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
# ⚠ fail-closed（2026-10-03 修 M2）：`$refIcc` 是**另一条门禁**（`_probe-jpg-p3-icc.ps1`）的遗留产物，
#   且落在被 .gitignore 排除的 `tests/output/` 下 ⇒ 换机 / `git clean -xdf` 后**必然**不在位。
#   旧行为：缺失 ⇒ `$refMd5` 取默认值 `"无参照"` ⇒ 每格只要产物带 ICC 就落 `⚠`，
#   而 `⚠` **不计入 FAIL** ⇒ 整条 ICC 臂事实上失去判别力却仍 `exit 0`
#   （= 清单第 3 项「依赖上一轮遗留产物」＋ 第 4 项「失败沿用默认值」的叠加）。
#   ⇒ 现在**不降级**：参照缺失即 FAIL 并点名、exit 1。没有参照就没有判别力，
#     不许拿"每格 ⚠"冒充绿（本仓规矩：判据两侧都空 ⇒ 记未判/FAIL，不给绿）。
#   变异验证：把 `$refIcc` 改指一个不存在的路径 ⇒ 旧写法 PASS=0 FAIL=0 WARN=6 + exit 0（假绿），
#     修复后 ⇒ `❌ 参照 P3 ICC 不在位` + PASS=0 FAIL=1 + exit 1 ⇒ 确实转红；指回原路径 ⇒ 转绿。
if (-not (Test-Path $refIcc)) {
    Write-Output "❌ 参照 P3 ICC 不在位：$refIcc"
    Write-Output "   它来自 _probe-jpg-p3-icc.ps1（色度已核），落在 .gitignore 排除的 tests/output/ 下；缺它时本矩阵无从判定"
    Write-Output "PASS=0 FAIL=1 WARN=0（参照缺失 ⇒ fail-closed：拒绝跑，不给绿）"
    Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
    exit 1
}
# ⚠ 2026-10-02 加牙：本脚本**原有结构性缺陷**——表格型输出、**无 pass/fail 计数** ⇒
#   运行器只能看 exit code，而它在任何判定下都 exit 0 ⇒ **结构上永不可能失败**。
#   现补计数与退出码：`❌` ⇒ FAIL（exit 1）；`✅` ⇒ PASS；`⚠` ⇒ WARN。
# ⚠ 2026-10-04 补完（复查 P1-4）：**单条** ⚠ 仍不直接判 FAIL（理由见下），但 ⚠ 的**条数**有显式上限：
#   `WARN > $maxWarn` ⇒ **判 FAIL 并 exit 1，同时逐条点名是哪些格落了 ⚠**。
#   为什么要设上限：本矩阵基线就是 `PASS=2 FAIL=0 WARN=4`（6 格里 4 格是已知的降级 ⚠），
#   若 ⚠ 无限可涨，那么"每格都是 ⚠"也照样 exit 0 ⇒ 这条锁留着一条**结构性绿灯通道**。
#   **上限取值口径**（照本仓对 CJK 门禁的处置原则：**不许把阈值贴着实测写 ⇒ 余量 0 ⇒ 换个机子就假红**）：
#       实测 WARN = 4（2026-10-04 实测：本机 Release 产物 + publish/PLAN 工具链；
#                    表格里 jpg / webp / tiff / jxl 四格落 ⚠，png / avif 因 CICP=smpte432 落 ✅）
#       明示余量 = 2
#       $maxWarn = 4 + 2 = **6**
#     余量 2 吸收的是"某一格因工具链/产品换版从 CICP 命中退化成 ICC-only"这类**单格到成对**的正常漂移；
#     它**不是免罚额度**：≥7 条 ⚠（矩阵半数以上格子失去判别力）必须红，逼人来改判据或修产品。
#     ⚠ 什么时候必须回来改这里：
#       a) 真正修好了这条判据（按"色度是否为 P3"判，而不是与参照 ICC 逐字节相等）⇒ 那 4 条已知假警报
#          会消失、基线回到 WARN=0，`$maxWarn` 要**跟着往下收**，不许继续留 6；
#       b) 矩阵变宽（新增受测格式）⇒ 先量新版基线，再按「实测 + 2」重定；
#       c) 有人想靠抬 `$maxWarn` 来"消红" ⇒ 先看清是哪一格在红、是不是产品侧真退化（上面 b 的口径不适用）。
#   ⚠ **为什么单条 ⚠ 不判 FAIL**（实测澄清）：`⚠ 有 ICC 但非 P3 参照` 的判据是
#     「与 _probe-jpg-p3-icc.ps1 的参照 ICC **逐字节相等**」—— 实测产物 ICC 是 "RGB built-in" 的
#     **Display P3**（D50 适应后 rXYZ=0.51512,0.2412,-0.00105 / gXYZ=0.29198,0.69225,0.04189 /
#     bXYZ=0.1571,0.06657,0.78407，与 P3 标准值一致），只是与参照**不是同一份字节**。
#     ⇒ 该分支是**假警报**；把**单条** ⚠ 判 FAIL 会造假红 ⇒ 由上面的**条数上限闸**兜底，不擅自升格。
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
# 上面已 fail-closed ⇒ 这里不再有"无参照"默认值（旧写法会在缺失时静默降级，见上方 M2 注释）
$refMd5 = (Get-FileHash $refIcc -Algorithm MD5).Hash.Substring(0, 8)
Write-Output "参照 P3 ICC md5 = $refMd5（来自 _probe-jpg-p3-icc.ps1，色度已核）"
$pass = 0; $fail = 0; $warn = 0; $cells = 0
# ⚠ 条数上限闸（2026-10-04 补完 M3，取值口径见头部"上限取值口径"段，勿就地改动数字）：
#   `$maxWarn` = 实测 WARN 基线 4 + 明示余量 2 = 6；`WARN > $maxWarn` ⇒ FAIL + exit 1。
#   `$warnItems` 存每条 ⚠ 的**可点名凭据**（哪一格 / ICC 大小与 md5）—— 上限闸红的时候必须逐条打出来，
#   只报一个 "WARN=8" 的数字等于没有判据（看不见 ⇒ 没法修）。
#   `$cells` = 实际走过判定的格数 ⇒ 末尾用它挡"判据条数下限"（本仓铁律：不许零断言 + exit 0）。
$maxWarn = 6
$warnItems = New-Object System.Collections.ArrayList
Write-Output ("{0,-6} {1,-20} {2,-30} {3}" -f "格式", "ICC(大小/md5)", "CICP/其他", "判定")

foreach ($f in @("png", "jpg", "webp", "tiff", "jxl", "avif")) {
    $cells++
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
    # 本格落 ⚠ 时的**点名凭据**；下面 CICP 覆盖宣判时要按值精确撤掉这一条。
    $warnRow = ''
    if (Test-Path $iccFile) {
        $m = (Get-FileHash $iccFile -Algorithm MD5).Hash.Substring(0, 8)
        $iccInfo = "$((Get-Item $iccFile).Length)B/$m"
        $verdict = if ($m -eq $refMd5) { $pass++; "✅ ICC=P3（与参照同字节）" } else {
            $warn++
            $warnRow = "$f 格：有 ICC($iccInfo) 但非 P3 参照（期望参照 md5=$refMd5，判据逐字节相等，已知假警报）"
            $null = $warnItems.Add($warnRow)
            "⚠ 有 ICC 但非 P3 参照（见头部：判据是字节相等，已知假警报）"
        }
    }
    # CICP：ffprobe（JXL 另用 jxlinfo，因 exiftool/ffprobe 读不到其 ICC）
    $cicp = (Run-Tool $fp "-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer -of csv=p=0 `"$($out.FullName)`"" "probe_$f").Out
    if ($f -eq "jxl" -and (Test-Path $jxlinfo)) {
        $cicp = "jxlinfo: " + (Run-Tool $jxlinfo "`"$($out.FullName)`"" "jxl_$f").Out
    }
    if ($cicp.Length -gt 28) { $cicp = $cicp.Substring(0, 28) + "…" }
    # ⚠ 计数口径：**每格只计一次**。CICP 命中会覆盖 ICC 判定 ⇒ 先撤掉上面记的那一次再按 CICP 记。
    if ($cicp -match "smpte432") {
        if ($verdict -like "✅*") { $pass-- } elseif ($verdict -like "⚠*") { $warn--; $null = $warnItems.Remove($warnRow) }
        $pass++; $verdict = "✅ CICP=P3"
    }
    if ($iccInfo -eq "-" -and $cicp -notmatch "smpte432") {
        $fail++; $verdict = "❌ 产物无任何 P3 描述（会被按 sRGB 误读）"
    }
    Write-Output ("{0,-6} {1,-20} {2,-30} {3}" -f $f, $iccInfo, $cicp, $verdict)
}

# ── 汇总前的两道闸（2026-10-04 补完 M3）────────────────────────────────────────
# ① ⚠ **条数上限**：单一已知的降级 ⚠ 允许存在（它在当前判据下本来就是假警报，见头部），
#    但 ⚠ 的数量必须封顶，否则这条锁的绿灯通道就没堵上；超上限 ⇒ FAIL 并**逐条点名**。
# ② **判据格数下限**：矩阵写死 6 格、每格必有判定 ⇒ 这里显式钉住，把"列表被改空 / 某格被 continue
#    吞掉 ⇒ 断言条数变少却仍 exit 0"这条假绿通道堵掉（本仓铁律：判不出不许算绿）。
if ($warn -gt $maxWarn) {
    $fail++
    Write-Output ("❌ ⚠ 条数超上限：实得 WARN={0} > 上限 {1}（= 实测基线 4 + 明示余量 2，取值口径见头部）⇒ 矩阵半数以上格子失去判别力，判红" -f $warn, $maxWarn)
    foreach ($w in $warnItems) { Write-Output ("      ⚠ 超限条：{0}" -f $w) }
}
if ($cells -lt 6) {
    $fail++
    Write-Output ("❌ 矩阵判据格数不足：实得 {0} 格 < 应有 6 格 ⇒ 有格根本没判（本仓规矩：判不出不许算绿）" -f $cells)
}

# ── 汇总与退出码（2026-10-02 加牙）──────────────────────────────────────────
# ⚠ 运行器的零断言闸只认 PASS=n 形态 ⇒ 汇总行必须带 `PASS=<数字>`。
Write-Output ""
Write-Output "PASS=$pass FAIL=$fail WARN=$warn（表格型；WARN<=$maxWarn 时不计入 FAIL，超限即判红并逐条点名，见头部）"
# ⚠ 退出码必须**成对**给出（防污染 $LASTEXITCODE）。有 FAIL 时保留工作目录以便取证。
if ($fail -gt 0) { Write-Output "⚠ 保留工作目录以便取证: $d"; exit 1 }
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
exit 0
