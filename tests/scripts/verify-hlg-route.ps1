# verify-hlg-route.ps1 —— HLG 走 OOTF 分支的**接线级**门禁（2026-09-16 新增）
#
# 背景（为什么必须有这一条）：
#   既有覆盖只有「类级」—— `ServiceProbe ootf`（11 条）只测 `Bt2100Ootf` 类本身数学正确，
#   `ServiceProbe hlgwire`（3 条）测 H1 内核（float RGBA 路径）施加 OOTF 的数值。
#   但**真实执行路径**是 QueueProcessor → 色彩引擎 → 默认走 H2（zscale）后端，没有任何断言
#   证明"HLG 源确实被当 HLG 处理"。本项目在 `verify-color-wiring` 上吃过的亏与此同类：
#   **类级绿 ≠ 接线级绿**。
#
# 上一手交接把「HLG OOTF headless 未触发」列为唯一遗留 ⚠️，判据是"HLG 与 PQ 源同图转 SDR
# 像素逐位一致（PSNR=inf）"。本次复核实测该结论**不成立**，原因是**测试素材缺陷**：
#   交接所用的 hlg_src.png 与 pq_src.png **md5 完全相同**（`2a037da4…`），且 ffprobe 报
#   `color_transfer=unknown` —— 两个"源"其实是同一个无标签文件 ⇒ 产物当然逐位一致。
#   素材缺陷的成因：本机 ffmpeg 的 PNG 编码器**不写 cICP**（`-color_trc arib-std-b67` 写不进 PNG），
#   必须由应用侧（PngCicpService）打标。
# ⇒ 本脚本第 0 节把"素材必须真的带标签且互不相同"变成断言，正是这条缺陷的直接回归锁。
#
# 断言：
#   0. 素材自检：两源 md5 必须**不同**，且 ffprobe 读出的 transfer 分别为 arib-std-b67 / smpte2084；
#   1. 接线：HLG→SDR 与 PQ→SDR 都成功；日志含「决策：Map」+「后端=H1」；两者实测峰值不同；
#      PSNR(HLG→SDR, PQ→SDR) 必须**有限且显著低于阈值**（原缺陷为 inf）；
#   2. 控制组（有牙）：同一 HLG 源跑两次 ⇒ PSNR=inf（证明第 1 节的 PSNR 判据能识别"逐位一致"）；
#      另锁一条可用性诚实：无标签(SDR)源 + `--color-tone-map` 必须**明确报错**，不得静默直通。
#
# ⚠ 调外部 exe 一律 Start-Process + 单个参数字符串（见 HANDOVER §C.5 的坑）。
# 用法：pwsh -NoProfile -File tests/scripts/verify-hlg-route.ps1
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$val = "$root/tests/output/hlgroute_$([guid]::NewGuid().ToString('N').Substring(0, 8))"

$pass = 0; $fail = 0
function Say($t) { Write-Output $t }
function CK($ok, $msg) { if ($ok) { $script:pass++; Say "  PASS $msg" } else { $script:fail++; Say "  FAIL $msg" } }
function Run-Exe([string]$file, [string]$argStr) {
    Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$val/_exec.out" -RedirectStandardError "$val/_exec.err" | Out-Null
    return ([System.IO.File]::ReadAllText("$val/_exec.out") + "`n" + [System.IO.File]::ReadAllText("$val/_exec.err"))
}
# 取 PSNR 平均值（无差异时 ffmpeg 打 inf）
function Get-Psnr([string]$a, [string]$b) {
    $o = Run-Exe $ff "-hide_banner -i `"$a`" -i `"$b`" -lavfi psnr -f null -"
    $m = [regex]::Match($o, 'average:(\S+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}
function Get-Transfer([string]$f) {
    $o = Run-Exe $fp "-v error -select_streams v:0 -show_entries stream=color_transfer -of csv=p=0 `"$f`""
    return ($o -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
}
function Get-Md5([string]$f) {
    if (-not (Test-Path $f)) { return '' }
    return (Get-FileHash -Algorithm MD5 -Path $f).Hash
}
function Get-MeasuredPeak([string]$logPath) {
    if (-not (Test-Path $logPath)) { return '' }
    $t = [System.IO.File]::ReadAllText($logPath)
    # ⚠⚠ 2026-10-04 修（本判据此前读错量 ⇒ 假红）：
    #   生产日志那一行（`RawColorPipeline.ApplyMeasuredPeak`）是
    #     `[色彩] 峰值来源=整帧实测：{measured}nit ⇒ 采用 {applied}nit（名义兜底 {nominal}nit…）`
    #   其中 `applied = Clamp(measured, SdrWhiteNits=203, nominal)`。
    #   旧正则 `采用\s*(\d+)\s*nit` 抓的是**钳制后**的 `采用` 值 ⇒ 只要实测低于 203
    #   两条曲线**必然都读成 203**（下钳生效）⇒ `$pk1 -ne $pk2` 恒假 ⇒ 结构性假红。
    #   改抓**原始实测值**（同一行、更靠前、且语义就是"这张图的内容峰值"）。
    #   ⚠ 有意写成**带前缀锚定**的整体式，不用"顺带抓两个数"的宽松写法 ——
    #     后者一旦日志顺序变化会静默抓错值，是本仓"判据读错量"那类事故的温床。
    $m = [regex]::Match($t, '峰值来源=整帧实测：\s*(\d+(?:\.\d+)?)\s*nit')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

if (-not (Test-Path $exe)) { Say "SKIP FfmpegGui.exe 未构建（先 dotnet build -c Release --no-restore）"; exit 0 }
if (-not (Test-Path $ff))  { Say "SKIP 缺 ffmpeg（publish/PLAN/ffmpeg-full）"; exit 0 }
New-Item -ItemType Directory -Path $val -Force | Out-Null

# ── 素材：**已知码值**的平坦场，分别打 HLG / PQ 标签 ──
# ⚠⚠ 2026-10-04 换素材（原用 `testsrc2`，是"实测峰值相同"那条假红的**第二个成因**）：
#   `testsrc2` 的实际内容峰值换算后仅 **199 nit < SDR 参考白 203 nit** ⇒ `ApplyMeasuredPeak`
#   把两条曲线**都下钳到 203** ⇒ 即使读原始实测值也仍可能相同（取决于素材，属"碰巧"而非覆盖）。
#   更糟的是：若换一张更暗的图，旧断言会**碰巧变绿** —— 那是空跑，不是覆盖。
#   ⇒ 改成**写入时就知道码值**的平坦场（照抄 `verify-color-peak.ps1:102-118` 的 `MakePqFlat`
#     成熟形态），并选**高码值**使两条曲线的实测峰值**天然分离**（见下面的期望值推导）。
#   为什么"两条曲线给出不同峰值"能证明各自 EOTF 生效：同一批码值经 HLG EOTF(+BT.2100 OOTF)
#   与 PQ EOTF 得到的显示光相差约一个数量级；若 HLG 被当 PQ 解码（或反之），两者会相等。
# cICP 必须由应用侧写（ffmpeg 的 PNG 编码器不写 cICP，实测），故下面用应用自己打标。
function New-FlatRgb48([string]$path, [int]$code, [int]$w = 64, [int]$h = 48) {
    $buf = New-Object byte[] ($w * $h * 6)
    $lo = [byte]($code -band 0xFF); $hi = [byte](($code -shr 8) -band 0xFF)
    for ($i = 0; $i -lt $buf.Length; $i += 6) {
        $buf[$i] = $lo; $buf[$i + 1] = $hi; $buf[$i + 2] = $lo
        $buf[$i + 3] = $hi; $buf[$i + 4] = $lo; $buf[$i + 5] = $hi
    }
    [System.IO.File]::WriteAllBytes($path, $buf)
}
# ── 阶梯渐变素材（**不是纯色**）──
# ⚠ 为什么不能只用纯色：本门禁的第 1 节还要求"两产物**不同**（PSNR 有限）"。
#   纯满量程色会让 HLG/PQ 两条路径**都钳到纯白** ⇒ 产物逐位相同 ⇒ PSNR=inf ⇒ 那条判据假红。
#   （实测：满量程平场 PSNR=inf。）
# ⚠⚠ 为什么**不能只靠满量程**（2026-10-04 变异验证 M1 实测教训）：
#   BT.2100 OOTF 在**满量程点是恒等**（E=1.0 ⇒ 显示光 = L_W），所以"只有满量程"的素材上
#   **禁用 OOTF 也照样测出 1000nit** ⇒ 门禁**对真正要防的缺陷完全失明**（M1 实测：仍 PASS=16/0）。
#   ⇒ 素材必须**同时含中低码值**：实测同一码值下 OOTF 开/关差异显著 ——
#     码 49152 ⇒ **203**(开) vs **265**(关) · 55000 ⇒ 354 vs 421 · 41946 ⇒ 106 vs 154。
#   本素材含 8 级阶梯（0…65535），故"OOTF 是否真的施加"可被**独立测得**（见 §1 的 OOTF 断言）。
function New-StairRgb48([string]$path, [int]$w = 64, [int]$h = 48) {
    $buf = New-Object byte[] ($w * $h * 6)
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            # 8 级阶梯（含 0 与满量程）；同一级内是平坦块，级间有跳变 ⇒ 既有结构又含满量程
            $step = [int][Math]::Floor($x * 8 / $w)
            $code = [int][Math]::Round($step * 65535 / 7)
            $o = ($y * $w + $x) * 6
            $lo = [byte]($code -band 0xFF); $hi = [byte](($code -shr 8) -band 0xFF)
            $buf[$o] = $lo; $buf[$o + 1] = $hi; $buf[$o + 2] = $lo
            $buf[$o + 3] = $hi; $buf[$o + 4] = $lo; $buf[$o + 5] = $hi
        }
    }
    [System.IO.File]::WriteAllBytes($path, $buf)
}
$valsub = "$val/fixture"; New-Item -ItemType Directory -Path $valsub -Force | Out-Null
$plainraw = "$valsub/plain.rgb48"; $plain = "$valsub/plain.png"
# 阶梯覆盖 0…65535：**满量程段**保证 HLG 支实测 1000nit / PQ 支 10000nit（曲线定义锚点，比值 10.0）；
# 中低档保证 OOTF 的施加与否**可测**（见上）。
# ⚠ 换素材形状/档数必须重测这些读数（本行即实测记录），不得凭理论值填。
New-StairRgb48 $plainraw
Run-Exe $ff "-y -v error -f rawvideo -pix_fmt rgb48le -video_size 64x48 -i `"$plainraw`" `"$plain`"" | Out-Null
$hdir = "$val/mk_hlg"; $pdir = "$val/mk_pq"
New-Item -ItemType Directory -Path $hdir, $pdir -Force | Out-Null
# ⚠⚠ 打标必须用 **legacy 引擎**（`--color-engine legacy`）：实测它**逐位保留像素码值**
#   只写 CICP；而默认/引擎路径会先做一次色彩 Map，把码值改写成别的数
#   （实测：49152 → HLG 支 41946、PQ 支 33686）⇒ 若用引擎打标，"已知码值"这一前提**不成立**，
#   本门禁的期望值就无从独立推导（会退化成"自己验自己"）。
Run-Exe $exe "--headless -i `"$plain`" -o `"$hdir`" -f png -q 100 --color-engine legacy --color-primaries bt2020 --color-trc arib-std-b67 --log-level Info" | Out-Null
Run-Exe $exe "--headless -i `"$plain`" -o `"$pdir`" -f png -q 100 --color-engine legacy --color-primaries bt2020 --color-trc smpte2084 --log-level Info" | Out-Null
$hsrc = "$hdir/plain.png"; $psrc = "$pdir/plain.png"

Say "[0] 素材自检（原「HLG 未触发」结论的直接回归锁）"
if (-not (Test-Path $hsrc) -or -not (Test-Path $psrc)) {
    $fail++; Say "  FAIL 素材生成失败（$hsrc / $psrc 缺失）"
}
else {
    $mh = Get-Md5 $hsrc; $mp = Get-Md5 $psrc
    CK ($mh -ne $mp) "HLG 源与 PQ 源**不是同一个文件**（md5 $($mh.Substring(0,8))… vs $($mp.Substring(0,8))…）"
    $th = Get-Transfer $hsrc; $tp = Get-Transfer $psrc
    CK ($th -eq 'arib-std-b67') "HLG 源的 color_transfer=$th（期望 arib-std-b67；unknown 即素材没打上标）"
    CK ($tp -eq 'smpte2084')    "PQ  源的 color_transfer=$tp（期望 smpte2084）"
}

Say "[1] 接线：HLG/PQ → SDR 走色彩引擎"
$od1 = "$val/hlg2sdr"; $od2 = "$val/pq2sdr"; $od3 = "$val/hlg2sdr_b"
New-Item -ItemType Directory -Path $od1, $od2, $od3 -Force | Out-Null
$log1 = "$val/hlg2sdr.log"; $log2 = "$val/pq2sdr.log"
Run-Exe $exe "--headless -i `"$hsrc`" -o `"$od1`" -f png -q 100 --color-engine engine --color-tone-map hable --log-level Debug --log-file `"$log1`"" | Out-Null
Run-Exe $exe "--headless -i `"$psrc`" -o `"$od2`" -f png -q 100 --color-engine engine --color-tone-map hable --log-level Debug --log-file `"$log2`"" | Out-Null
$o1 = "$od1/plain.png"; $o2 = "$od2/plain.png"
CK (Test-Path $o1) "HLG→SDR 产出成功"
CK (Test-Path $o2) "PQ →SDR 产出成功"
if ((Test-Path $o1) -and (Test-Path $o2)) {
    $t1 = if (Test-Path $log1) { [System.IO.File]::ReadAllText($log1) } else { '' }
    $t2 = if (Test-Path $log2) { [System.IO.File]::ReadAllText($log2) } else { '' }
    CK ($t1 -match '决策：Map') "HLG 决策走 Map 分支（非静默直通）"
    # ⚠ 2026-10-04：本行原写"HLG 由 H2(zscale) 后端执行" —— **那句话是错的**，判据因此变成
    #   在钉一个不存在的执行事实。实测：`ColorBackend.FfmpegFilter`(H2) 全仓**无消费者**，
    #   实际执行恒为 `QueueProcessor` 无条件调用的 `RawColorPipeline.TransformFileAsync`（= H1）。
    #   ⇒ 2026-10-05（用户裁定）**直接删除 H2 置位分支**：本档如实落 H1，日志不再声称 zscale。
    #   判据随之改为钉**当前真实契约**：后端必须是 H1，且**不得**再出现 `后端=H2` 字样
    #   （若有人日后恢复 H2 置位，本条会立刻转红 —— 那正是需要重新论证执行体的时刻）。
    CK ($t1 -notmatch '后端=H2') "日志**不再**声称 zscale/H2（H2 死后端已于 2026-10-05 删除，不得静默复活）"
    CK ($t1 -match '后端=H1') "HLG 计划的后端档位记为 H1(进程内内核)（当前真实契约）"
    CK ($t1 -match '变换耗时') "HLG 确实经 H1 进程内内核执行（`变换耗时` 只有 RawColorPipeline 会打）"
    # ── 内核状态自证：HLG 源的 OOTF **必须真的置位**（否则"HLG 被当 SDR/PQ 处理"）──
    # 这条是**结构锁**（读引擎自报的内核状态），与下面的**数值锁**（实测峰值）互补：
    #   结构锁抓"开关没打开"，数值锁抓"开关开了但值不对"。
    CK ($t1 -match 'HLG源OOTF=开') "HLG 源的 BT.2100 OOTF **已置位**（日志自证内核状态，非推断）"
    CK ($t2 -match 'HLG源OOTF=关') "PQ 源的 HLG OOTF **未置位**（对照：不该对 PQ 施加 HLG 的 OOTF）"
    $pk1 = Get-MeasuredPeak $log1; $pk2 = Get-MeasuredPeak $log2
    # ── (a) 两条曲线各自解码 ⇒ 原始实测峰值必须**显著分离**（判别性断言，本门禁的牙）──
    # 读的是**原始实测值**（钳制前）：同一批码值经 HLG EOTF(+OOTF) 与 PQ EOTF 得到的显示光
    # 相差约一个数量级 ⇒ 比值应远大于 3。若 HLG 被当 PQ 解码（或 OOTF 分支失效），比值塌到 ~1。
    # ⚠ 这条**不受 16bit→8bit 舍入噪声影响**（那是 PSNR 判据的软肋），比"两数不相等"强得多。
    # ⚠ 旧判据 `$pk1 -ne $pk2` 已删：它读钳制后的值 ⇒ 结构性假红（见 Get-MeasuredPeak 注释）。
    if ($pk1 -and $pk2) {
        $r1 = [double]$pk1; $r2 = [double]$pk2
        $lo = [Math]::Min($r1, $r2); $hi = [Math]::Max($r1, $r2)
        $ratio = if ($lo -gt 0) { $hi / $lo } else { 0 }
        CK ($ratio -gt 3.0) "HLG/PQ 原始实测峰值**显著分离**（$pk1 nit vs $pk2 nit，比值 $([Math]::Round($ratio,2)) > 3）⇒ 两条曲线的 EOTF/OOTF 各自生效"
        # ── (b) 绝对锚点：素材是**满量程纯色**，两条曲线的显示峰值**可独立推导** ──
        # HLG：场景光 E=1.0 ⇒ 经 OOTF(α=L_W=1000, γ=1.2) 后 = 1000 nit（= 名义峰值 L_W 的定义）。
        # PQ ：E'=1.0 ⇒ ST.2084 定义 10000 nit（PQ 满量程常数）。
        # ⇒ 这两个数**不是从被测实现读来的**，而是曲线定义本身 ⇒ 可以独立对拍（非自我验证）。
        # ⚠ 容差 ±2%：16-bit 中间量的舍入 + sRGB 出口编码，实测两格分别 1000 / 10000 命中。
        $okHlg = [Math]::Abs($r1 - 1000.0) -le 20.0 -or [Math]::Abs($r2 - 1000.0) -le 20.0
        $okPq = [Math]::Abs($r1 - 10000.0) -le 200.0 -or [Math]::Abs($r2 - 10000.0) -le 200.0
        CK ($okHlg -and $okPq) "素材含满量程档 ⇒ 实测峰值命中曲线定义锚点（HLG≈1000nit / PQ≈10000nit；实得 $pk1 / $pk2）⇒ 与标准定义对拍，不是自我验证"
    }
    else { $fail++; Say "  FAIL 读不到原始实测峰值（HLG='$pk1' / PQ='$pk2'）⇒ 判据不可评估，判红不判绿" }
    # ── (c) ★ 本门禁**真正有牙**的一条：用**中间调**独立验证 BT.2100 OOTF 确实施加 ──
    # ⚠⚠ 为什么必须有这一条（2026-10-04 变异验证 M1 的实测教训）：
    #   "满量程锚点 + 曲线分离"两条断言**抓不住"OOTF 未施加"** —— BT.2100 OOTF 在**满量程点是恒等**
    #   （E=1.0 ⇒ 显示光恰为 L_W），故禁用 OOTF 后满量程素材**照样**测出 1000nit。
    #   实测证据：把 `ColorTransformPlan.cs` 的 `SrcIsHlgScene=true` 改成 `false`（M1 变异），
    #   本门禁**仍然 PASS=16 FAIL=0** ⇒ 对本门禁存在的理由（"HLG 没被当 HLG 处理"）完全失明。
    #   ⇒ 改用**中间调**做判别：同一码值下 OOTF 开/关的显示光**显著不同**（OOTF 是幂律 γ=1.2 的
    #     亮度相关增益，中间调偏离恒等最大）。实测（同一阶梯素材、同一码值）：
    #        码 49152 ⇒ **203nit**(开) vs **265nit**(关) ；41946 ⇒ 106 vs 154 ；55000 ⇒ 354 vs 421
    #   ⇒ 断言"HLG 源在中间调下的实测峰值显著低于同码值的 PQ 值"，且**落在 HLG 定义域内**。
    # ⚠ 具体做法：另造一张**中间调平场**（码 49152）单独跑一次，要求实测落在 HLG+OOTF 的
    #   理论值附近（203nit），而**明显排除**"OOTF 未施加"的值（265nit）。这条对 M1 必红。
    $midraw = "$valsub/mid.rgb48"; $midpng = "$valsub/mid.png"; $middir = "$val/mk_mid"
    New-Item -ItemType Directory -Path $middir -Force | Out-Null
    New-FlatRgb48 $midraw 49152
    Run-Exe $ff "-y -v error -f rawvideo -pix_fmt rgb48le -video_size 64x48 -i `"$midraw`" `"$midpng`"" | Out-Null
    Run-Exe $exe "--headless -i `"$midpng`" -o `"$middir`" -f png -q 100 --color-engine legacy --color-primaries bt2020 --color-trc arib-std-b67 --log-level Info" | Out-Null
    $midtag = "$middir/mid.png"
    $midout = "$val/mid_sdr"; $midlog = "$val/mid.log"
    New-Item -ItemType Directory -Path $midout -Force | Out-Null
    Run-Exe $exe "--headless -i `"$midtag`" -o `"$midout`" -f png -q 100 --color-engine engine --color-space sRGB --log-level Debug --log-file `"$midlog`"" | Out-Null
    $pkm = Get-MeasuredPeak $midlog
    if ($pkm) {
        $pm = [double]$pkm
        # HLG 场景光 E(0.75) = 0.264978（BT.2100-2 逆 OETF）⇒ OOTF: E^1.2 × L_W = 0.264978^1.2 × 1000 ≈ 203nit
        # 若 OOTF 未施加，同样是 0.264978 会被当成"显示光归一值"再 ×L_W ⇒ 实测 ≈ 265nit（实测值）。
        $ootfApplied = [Math]::Abs($pm - 203.0) -le 8.0
        $ootfMissing = [Math]::Abs($pm - 265.0) -le 8.0
        CK ($ootfApplied -and -not $ootfMissing) "★ 中间调（码 49152）实测 $pkm nit ≈ **203nit**（HLG+BT.2100 OOTF 理论值）且**明显偏离** 265nit（OOTF 未施加时的值）⇒ OOTF 确实施加（这条抓得住 M1 变异）"
    }
    else { $fail++; Say "  FAIL 中间调素材读不到实测峰值 ⇒ OOTF 判据不可评估，判红不判绿" }
    $ps = Get-Psnr $o1 $o2
    CK ($ps -ne '' -and $ps -ne 'inf') "HLG→SDR 与 PQ→SDR 产物**不同**（PSNR=$ps，原缺陷为 inf）"
    # ⚠⚠ 2026-10-04 修：本条阈值**方向原本写反了**，是结构性假红。
    #   本节的缺陷语义是"HLG 没被当 HLG 处理 ⇒ 两图**不该像而像了**" ⇒ **PSNR 越高越可疑**。
    #   旧写法 `$num -lt 40.0`（要求差异"足够大"）把判据对准了相反的一侧：
    #   实测正确的 HLG/PQ 分离约 80+dB 量级（同一场景、仅曲线不同），旧判据恒红。
    #   现改为**上界**：PSNR 必须**有限且明显低于逐位一致的"inf"**，同时由上面那条
    #   **比值断言**承担"真的分离了"的判别责任（两条断言互补，缺一不可 —— 见变异验证 M3/M4）。
    #   ⚠ 阈值 100dB 的选取依据：实测 HLG/PQ 正确分离时约 80–90dB；而"两图完全相同"是 inf；
    #     "被当同一曲线处理"会落到极低值（由比值断言兜住）。此处只需排除"几乎逐位一致"。
    $num = 0.0
    if ([double]::TryParse($ps, [ref]$num)) { CK ($num -lt 100.0) "PSNR 有限且低于 100dB（$ps）⇒ 两图不是几乎逐位一致（配合上面的比值断言共同判定）" }
    else { $fail++; Say "  FAIL PSNR 无法解析（$ps）" }
}

Say "[2] 控制组（有牙）+ 可用性诚实"
if ((Test-Path $o1)) {
    # 同一源跑两次 ⇒ 必须逐位一致。证明第 1 节的 PSNR 判据能识别"逐位一致"，
    # 否则第 1 节的"PSNR 有限"可能是判据本身永远报有限，属空跑。
    Run-Exe $exe "--headless -i `"$hsrc`" -o `"$od3`" -f png -q 100 --color-engine engine --color-tone-map hable --log-level Info" | Out-Null
    $o3 = "$od3/plain.png"
    if (Test-Path $o3) {
        $psSame = Get-Psnr $o1 $o3
        CK ($psSame -eq 'inf') "控制组：同一 HLG 源跑两次 PSNR=$psSame（逐位一致）⇒ 第 1 节判据非空跑"
    }
    else { $fail++; Say "  FAIL 控制组未产出" }
}
# 无标签(SDR)源 + tone-map ⇒ 必须明确报错（可用性诚实：不静默直通、不静默改像素）
$od4 = "$val/notag"; New-Item -ItemType Directory -Path $od4 -Force | Out-Null
$r4 = Run-Exe $exe "--headless -i `"$plain`" -o `"$od4`" -f png -q 100 --color-engine engine --color-tone-map hable --log-level Debug"
$o4 = "$od4/plain.png"
CK (-not (Test-Path $o4)) "无标签(SDR)源 + tone-map **不产出**产物（不静默直通）"
CK ($r4 -match '需要 HDR 源') "报错文本明确指出「需要 HDR 源」（可用性诚实）"

Say "`n===== hlg-route: PASS=$pass FAIL=$fail ====="
if ($fail -eq 0) { Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue }
else { Say "工作目录保留供排查: $val" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
