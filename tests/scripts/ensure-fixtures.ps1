# 补齐回归脚本依赖的"派生素材"（tests/output/validate 与 tests/output/results/color 是 scratch 目录，
# generate-sources.ps1 只填 tests/output/sources，故这些需单独重建）。全部由 ffmpeg/exiftool/生产代码生成，不下载。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root

# ── ⚠⚠ #147 C：与运行器**同一把**仓库级锁（2026-09-19 实施；方案见 docs/TESTING.md §6 第 86 条）──
# 为什么本脚本也要锁：它是**唯一残留的共享 fixture 生产者**（写 tests/output/validate 与
#   tests/output/results/color）⇒ 任何两个会**写**它的进程都必须互斥。
#   ⚠ generate-sources.ps1 **不加**：它写 tests/output/sources，而门禁**只读它、从不覆盖**。
#   ⚠ 刻意**不**给 12+ 个 verify-*.ps1 各加锁（会把「共享文件面」从 1 个扩到 12+ 个 ⇒ 反而制造并发编辑成因）。
# 行为与运行器**逐条一致**：被占用 ⇒ 明确报错 + exit 9（不等待/不静默跳过）；stale 锁**不自动删**（提示手工删）。
# ⚠ 本脚本**不声称覆盖构建**（dotnet/MSBuild 属另一条线），也不覆盖直接跑单个 verify-*.ps1。
# ⚠ 生命周期：正常结束在**末尾**释放；**异常中断会残留** ⇒ 下次运行会以「疑似残留锁」提示，需**手工**删。
$script:gatesLockPath = "$root/tests/output/.gates.lock"
try {
  $script:gatesLockStream = [System.IO.File]::Open($script:gatesLockPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
} catch {
  $holder = "(锁内容读不到)"
  try { $holder = [System.IO.File]::ReadAllText($script:gatesLockPath) } catch { }
  $stale = $false
  if ($holder -match '"pid"\s*:\s*(\d+)') {
    $hpid = [int]$Matches[1]
    $hproc = $null
    try { $hproc = Get-Process -Id $hpid -ErrorAction SilentlyContinue } catch { $hproc = $null }
    if (-not $hproc) { $stale = $true }
  }
  Write-Output ""
  Write-Output "===== ensure-fixtures 拒绝启动：仓库级锁被占用（#147 C）====="
  Write-Output "  锁文件：$script:gatesLockPath"
  Write-Output "  持有者：$holder"
  if ($stale) {
    Write-Output "  ⚠ 持有者进程已不存在 ⇒ **疑似残留锁**。请**手工**确认后删除该文件再重试（本脚本不会自动删）。"
  } else {
    Write-Output "  ⚠ 另一个门禁运行器（或 ensure-fixtures）正在跑 ⇒ 本轮**不启动**（并发写同一批 fixture ⇒ 读数不可信）。"
  }
  exit 9
}
# ⚠ cmdline 截断（同运行器，理由见 _run-step3-gates.ps1 内注释）：宿主注入环境下 CommandLine 极长。
$fixturesCmdRaw  = ([Environment]::CommandLine) -replace '\s+', ' '
$fixturesCmdTrim = $fixturesCmdRaw.Substring(0, [Math]::Min(300, $fixturesCmdRaw.Length))
$fixturesLockObj = [ordered]@{
  pid       = $PID
  host      = ("{0} {1}" -f $PSVersionTable.PSVersion, $PSVersionTable.PSEdition)
  startedAt = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
  script    = "ensure-fixtures.ps1"
  cmdline   = $fixturesCmdTrim
}
$fixturesLockJson  = ($fixturesLockObj | ConvertTo-Json -Compress)
$fixturesLockBytes = [System.Text.Encoding]::UTF8.GetBytes($fixturesLockJson)
$script:gatesLockStream.Write($fixturesLockBytes, 0, $fixturesLockBytes.Length)
$script:gatesLockStream.Flush()
$script:gatesLockStream.Dispose()
$script:gatesLockStream = $null
Write-Output "gates-lock=$script:gatesLockPath (pid=$PID, script=ensure-fixtures.ps1)"

$ff   = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$ex   = "$root/publish/PLAN/exiftool/exiftool.exe"
$prb  = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.dll"
$val  = "$root/tests/output/validate"
$col  = "$root/tests/output/results/color"
$srcs = "$root/tests/output/sources"
New-Item -ItemType Directory -Force -Path $val, $col | Out-Null

function FF($a) { Start-Process -FilePath $ff -ArgumentList $a -NoNewWindow -Wait -RedirectStandardError "$val/_ff.err" | Out-Null; return (Get-Content "$val/_ff.err" -EA SilentlyContinue) }

# 0) 修正素材层真 bug：generate-sources.ps1 只给 -color_trc 标签，而 ffmpeg 的 PNG 编码器不写 cICP
#    ⇒ “HDR PNG 素材”实际无色彩描述，探测回来是 sRGB。用生产代码补写真正的 cICP。
foreach ($st in @(@("src_hdr_pq.png", 9, 16), @("src_hdr_hlg.png", 9, 18))) {
    $fp2 = "$srcs/$($st[0])"
    if (Test-Path $fp2) {
        Start-Process -FilePath "dotnet" -ArgumentList "`"$prb`" png3 `"$fp2`" 0 $($st[1]) $($st[2])" -NoNewWindow -Wait | Out-Null
        $lab = (Start-Process -FilePath "$root/publish/PLAN/ffmpeg-full/ffprobe.exe" `
                 -ArgumentList "-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer -of csv=p=0 `"$fp2`"" `
                 -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$val/_lab.txt"); $val
        "    素材补标签: $($st[0]) → $((Get-Content "$val/_lab.txt" -Raw).Trim())"
    }
}

# 1) sdr_plain.png —— 普通 SDR 测试图
FF "-y -hide_banner -loglevel error -i `"$srcs/src_8bit.png`" -pix_fmt rgb24 `"$val/sdr_plain.png`""
# 2) src_hdr.png —— 16-bit + bt2020/PQ 标签（cICP 由生产代码写，因 ffmpeg PNG 编码器不写 cICP）
FF "-y -hide_banner -loglevel error -i `"$srcs/src_16bit.png`" -pix_fmt rgb48le `"$val/src_hdr.png`""
Start-Process -FilePath "dotnet" -ArgumentList "`"$prb`" png3 `"$val/src_hdr.png`" 0 9 16" -NoNewWindow -Wait | Out-Null
# 3) src_hdr8.png —— 8-bit + bt2020/PQ 标签（Bug1 场景：8bit HDR 源）
FF "-y -hide_banner -loglevel error -i `"$srcs/src_8bit.png`" -pix_fmt rgb24 `"$val/src_hdr8.png`""
Start-Process -FilePath "dotnet" -ArgumentList "`"$prb`" png3 `"$val/src_hdr8.png`" 0 9 16" -NoNewWindow -Wait | Out-Null
# 4) a2_srgb_p3.png —— 像素为 Display P3、附 P3 ICC（广色域 SDR 源）
#    P3 ICC 参照：先跑 _probe-jpg-p3-icc.ps1 重建（它用 iccgen 生成并提取）；没有则现场用 iccgen 造一份。
FF "-y -hide_banner -loglevel error -f lavfi -i `"color=c=black:s=1x1:d=0.04`" -vf `"format=gbrp,iccgen=color_primaries=smpte432:color_trc=iec61966-2-1:force=1`" -frames:v 1 -f rawvideo `"$val/_iccgen.raw`"" | Out-Null
$p3icc = "$val/jpgp3/p3.icc"
if (-not (Test-Path $p3icc)) {
    New-Item -ItemType Directory -Force -Path "$val/jpgp3" | Out-Null
    # 首选**生产代码**的命名 ICC（IccProfileService.ResolveNamedIcc）：与产品实际会附的 ICC 同源，
    # 不依赖 ffmpeg iccgen（它的输出会因构建/配置而异）。
    # ⚠ 带空格的值必须自己加引号：Start-Process 把 'Display P3' 拼成两个参数，
    # 探针就会收到 args[1]="Display" ⇒ “未识别”⇒ 下游默默用 bt709 默认值做出一张 sRGB ICC（实测踩过）。
    $null = Start-Process -FilePath "dotnet" -ArgumentList @("`"$prb`"", 'icc', '"Display P3"') `
            -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$val/_icc.txt"
    $iccTxt = Get-Content "$val/_icc.txt" -Raw -EA SilentlyContinue
    $iccFrom = [regex]::Match($iccTxt, '(?m)^icc=(.+)$').Groups[1].Value.Trim()
    $specOk = $iccTxt -match '(?m)^spec=Display P3'
    # 自校验：拿到的必须是**按 P3 生成**的 ICC（判据=探针报告的 spec 与路径里的色域 token）。
    # ⚠ 不可拿 ICC 的 desc 当色域判据：本仓库生成的标准 ICC 描述就是“RGB built-in”，
    #   真正的判据是 rXYZ 色度（=IccProfileService.DetectIccPrimaries）或生成时的 token。
    $descLine = [regex]::Match($iccTxt, '(?m)^.*desc=([^\r\n]*)').Groups[1].Value.Trim()
    if ($iccFrom -and (Test-Path $iccFrom) -and $specOk -and $iccFrom -match 'smpte432') {
        Copy-Item $iccFrom $p3icc -Force
        "    P3 ICC 参照 ← 生产代码命名 ICC（smpte432；desc='$descLine'——内置名不代表色域）"
    } else {
        "    ❌ P3 ICC 不可信（specOk=$specOk path=$(if ($iccFrom) { $iccFrom } else { '(null)' })）"
        "       ⇒ 宁可不附 ICC：错色的 ICC 比没有 ICC 更坏（会被下游当成真 P3 描述）"
    }
}
# zscale 必须给全输入标签，否则 libzimg 报 3074 “no path between colorspaces”
FF "-y -hide_banner -loglevel error -i `"$srcs/src_8bit.png`" -vf `"zscale=pin=bt709:tin=iec61966-2-1:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709`" -pix_fmt rgb48le `"$col/a2_srgb_p3.png`"" | Out-Null
# ⚠ 不再静默跳过 ICC 附着：没有 P3 ICC 参照时以前会“静默给出一个只有 cICP 的素材”，
#   下游测试会以为在测“像素 P3 + ICC P3”，实际在测 CICP-only —— 素材必须自己说清。
if (-not (Test-Path $p3icc)) {
    "    ❌ P3 ICC 参照缺失（$p3icc）⇒ a2_srgb_p3.png 仅带 cICP，不携带 ICC"
} elseif (Test-Path $col/a2_srgb_p3.png) {
    $f = "$col/a2_srgb_p3.png"
    Start-Process -FilePath $ex -ArgumentList "-overwrite_original -icc_profile<=`"$p3icc`" `"$f`"" -NoNewWindow -Wait | Out-Null
    # 自校验：用**生产代码本身**反读（ServiceProbe color 会跑 IccProfileService.ExtractIccToTempFile + GuessColorSpace），
    # 这才是下游测试真正依赖的事实：“引擎/legacy 能否从这张图提取出 ICC 并认出 Display P3”。
    # ⚠ 不拿 exiftool 的 ProfileDescription 当判据：标准生成的 ICC 描述就是“RGB built-in”。
    $null = Start-Process -FilePath "dotnet" -ArgumentList @("`"$prb`"", 'color', $f, 'png', $ex) `
            -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$val/_iccc.txt"
    $back = (Get-Content "$val/_iccc.txt" -Raw -EA SilentlyContinue)
    $hasIcc = $back -notmatch 'extractedIcc=\(null\)'
    # 断言用 `spaceName=`（= 引擎实际用的那条链：IccProfileService 提取 + ColorSpaceIdentify 按色度/曲线识别），
    # 不用 `guessed=`（它只看 ICC 描述文字，而我们生成的标准 ICC 描述就是“RGB built-in”）。
    if ($hasIcc -and $back -match 'spaceName=Display P3') {
        "    OK  a2_srgb_p3.png：引擎可按度量认出 Display P3"
    } elseif ($hasIcc) {
        "    ❌ a2_srgb_p3.png：ICC 可提取但引擎识别为 '$([regex]::Match($back,'spaceName=([^\r\n]*)').Groups[1].Value.Trim())'（应为 Display P3）"
    } else {
        "    ❌ a2_srgb_p3.png：生产代码提不出 ICC ⇒ 本素材实际仅为 CICP-only（下游不得当作携带 ICC 的用例）"
    }
}
# 5) a3_p3pq.png —— P3 原色 + PQ 传递（HDR 广色域源），cICP 用生产代码写 (12,16)
FF "-y -hide_banner -loglevel error -i `"$srcs/src_16bit.png`" -pix_fmt rgb48le `"$col/a3_p3pq.png`""
Start-Process -FilePath "dotnet" -ArgumentList "`"$prb`" png3 `"$col/a3_p3pq.png`" 0 12 16" -NoNewWindow -Wait | Out-Null

"  ### 生成结果（存在性 + 尺寸 + 标签） ###"
foreach ($p in @("$val/sdr_plain.png", "$val/src_hdr.png", "$val/src_hdr8.png", "$col/a2_srgb_p3.png", "$col/a3_p3pq.png")) {
    if (Test-Path $p) {
        $o = Start-Process -FilePath "$root/publish/PLAN/ffmpeg-full/ffprobe.exe" `
             -ArgumentList "-v error -select_streams v:0 -show_entries stream=pix_fmt,color_primaries,color_transfer -of csv=p=0 `"$p`"" `
             -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$val/_probe.txt"
        "    OK  {0,-34} {1,8:N0}B  {2}" -f (Split-Path $p -Leaf), (Get-Item $p).Length, (Get-Content "$val/_probe.txt" -Raw).Trim()
    } else { "    ❌  $p 未生成" }
}
"    P3 ICC 参照: " + (Test-Path $p3icc)

# ── #147 C：释放仓库级单锁（只在**自己写的**锁上删 —— pid 必须等于本进程，防删别人的锁）──────
# ⚠ 用 .NET API 删除（宿主批量删除守卫会污染 $LASTEXITCODE）；异常中断会残留锁，属**刻意**取舍。
try {
  if (Test-Path $script:gatesLockPath) {
    $lt = [System.IO.File]::ReadAllText($script:gatesLockPath)
    if ($lt -match '"pid"\s*:\s*(\d+)') {
      if ([int]$Matches[1] -eq $PID) { [System.IO.File]::Delete($script:gatesLockPath) }
    }
  }
} catch { }
Write-Output "gates-lock-released=$(-not (Test-Path $script:gatesLockPath))"

# ⚠ 显式 exit 0（2026-09-19 随 #147 C 一并补）：本脚本原先**无退出码契约** ⇒ `$LASTEXITCODE` 会
#   **残留上一次**的值（实测：上一次是锁冲突的 9 ⇒ 本脚本明明跑完了却报 9，误导读日志的人）。
#   ⚠ 语义仅「跑完了」：素材生成失败只打 ❌、**不**改退出码（本脚本**不是**门禁，别拿它当判据）。
exit 0
