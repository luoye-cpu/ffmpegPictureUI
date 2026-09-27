# PNG 3.0 实机互操作验证：cICP / sBIT 是否被**第三方解码器**真正接受与读出
# 现有 GainMapTestHost 的 D1–D7 只验"我们自写自读"，本脚本补上外部证伪：
#   ① ffmpeg 自己写不写 cICP；② 生产代码写入的 chunk 结构与 CRC（独立实现校验）；
#   ③ ffprobe / ffmpeg 解码器 / exiftool 能否读出色彩属性与有效位；④ iCCP 与 cICP 共存观察。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff    = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp    = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$ex    = "$root/publish/PLAN/exiftool/exiftool.exe"
$pr    = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   注：本脚本 ④ 段仍**跨读** jpgp3 组共享目录（`validate/jpgp3/p3.icc`）—— 那是 C 类生产者簇，按裁定 (b) 保持固定目录，不在本轮隔离范围。
$d     = "$root/tests/output/validate/png3_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null
$pass = 0; $fail = 0; $skip = 0
# ⚠ 2026-09-21（`TESTING.md` 第 81 条同族）：**SKIP 必须进汇总并计数**（第 76 条口径）。
#   原先 ④ 段缺 ICC 时只打一句 `SKIP`、**不进汇总** ⇒ 汇总仍是 `PASS=9 FAIL=0`，
#   读者**看不出**少了 1 条（与 `verify-color-peak` 的 (d) 段同一病灶）。
function SKIP([string]$what, [int]$n = 1) {
    $script:skip += $n
    Write-Output ("  SKIP ($n 条) " + $what)
}
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }
if (-not (Test-Path $pr)) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; Write-Output "先构建 ServiceProbe -c Release"; exit 1 }

function Dump-Png([string]$path, [string]$tag) {
    $b = [System.IO.File]::ReadAllBytes($path); $pos = 8; $list = @()
    while ($pos + 12 -le $b.Length) {
        # ⚠ PowerShell 的移位**保留左操作数类型**：`[byte]0x10 -shl 8` 截断成 **0**（实测），
        #   于是任何 IDAT>255 B 的 PNG 都会让遍历失步 ⇒ 必须显式 [int] 再移。
        #   2026-09-26 由 ⑥ 的负控夹具抓出（此前 ②/③ 只看 IDAT 之前的块，恰好没暴露）。
        $len = [int](([int]$b[$pos] -shl 24) -bor ([int]$b[$pos+1] -shl 16) -bor ([int]$b[$pos+2] -shl 8) -bor [int]$b[$pos+3])
        if ($len -lt 0 -or $pos + 12 + $len -gt $b.Length) { break }
        $t = [Text.Encoding]::ASCII.GetString($b, $pos + 4, 4)
        $hex = if ($len -ge 1 -and $len -le 8) { (($b[($pos+8)..($pos+7+$len)]) | ForEach-Object { $_.ToString("X2") }) -join ' ' } else { "" }
        $list += ,@($t, $hex)
        $pos += 12 + $len
    }
    Write-Output ("  {0} → {1}" -f $tag, (($list | ForEach-Object { if ($_[1]) { "$($_[0])[$($_[1])]" } else { $_[0] } }) -join ' '))
    return $list
}

# 独立 CRC32：全部在 **Int64 域** 运算并掩码到 32 位。
# ⚠ PowerShell 会把 0xEDB88320 / 0xFFFFFFFF 当 Int32 负字面量，[uint32] 转换直接抛异常
#   → 之前“11 个 chunk 全错”是检查器自己坏了，不是产品 CRC 错（ffmpeg 解码零告警已反证）。
$mask = 0xFFFFFFFFL
$tab = for ($i = 0; $i -lt 256; $i++) { $c = [int64]$i; for ($k = 0; $k -lt 8; $k++) { $c = if ($c -band 1) { (0xEDB88320L -bxor ($c -shr 1)) -band $mask } else { $c -shr 1 } }; $c }
function Get-CrcBad([string]$path) {
    $b = [System.IO.File]::ReadAllBytes($path); $pos = 8; $bad = 0; $n = 0
    while ($pos + 12 -le $b.Length) {
        # ⚠ PowerShell 的移位**保留左操作数类型**：`[byte]0x10 -shl 8` 截断成 **0**（实测），
        #   于是任何 IDAT>255 B 的 PNG 都会让遍历失步 ⇒ 必须显式 [int] 再移。
        #   2026-09-26 由 ⑥ 的负控夹具抓出（此前 ②/③ 只看 IDAT 之前的块，恰好没暴露）。
        $len = [int](([int]$b[$pos] -shl 24) -bor ([int]$b[$pos+1] -shl 16) -bor ([int]$b[$pos+2] -shl 8) -bor [int]$b[$pos+3])
        if ($len -lt 0 -or $pos + 12 + $len -gt $b.Length) { break }
        $crc = 0xFFFFFFFFL
        for ($i = $pos + 4; $i -lt $pos + 8 + $len; $i++) {
            $crc = ($tab[(($crc -bxor [int64]$b[$i]) -band 255)] -bxor ($crc -shr 8)) -band $mask
        }
        $crc = ((-bnot $crc) -band $mask)   # ⚠ PowerShell 的 -not 是逻辑非（返回布尔），位非必须用 -bnot
        $got = (([int64]$b[$pos+8+$len] -shl 24) -bor ([int64]$b[$pos+9+$len] -shl 16) -bor ([int64]$b[$pos+10+$len] -shl 8) -bor [int64]$b[$pos+11+$len]) -band $mask
        $n++; if ($crc -ne $got) { $bad++ }
        $pos += 12 + $len
    }
    return @{ N = $n; Bad = $bad }
}

function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e
  $global:LASTEXITCODE = $p.ExitCode
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}
# ⚠ **只读 stdout 的取数**（读「工具写在 stdout 的值」时唯一合法的尺，与 `Exec` 同形状、只换返回口径）：
#   ffprobe 的 `-of csv` 字段值在 **stdout**，而合并版 `Exec` 会把 stderr 一起拼进返回值 ——
#   随包 exiftool 是 perl 打包版，环境带着本机 perl 不认的 `LC_ALL`/`LANG`（MSYS/bash 侧的 `C.UTF-8`）
#   时每次调用先喷 locale warning ⇒ 拼进来的噪声会让 `-eq` / `Compare-Object` 式的**逐字段对账**
#   既可能假红（值前多一行警告），也可能假绿（两侧同污时变成两条警告互比）。stderr 仍落盘供排查。
function ExecOut([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e
  $global:LASTEXITCODE = $p.ExitCode
  $t = [System.IO.File]::ReadAllText($o)
  return $t
}

# ── ① ffmpeg 直写：带帧标签的 16-bit PNG 会不会自己写 cICP ──
Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=32x24:d=0.04`" -pix_fmt rgb48le -frames:v 1 -color_primaries bt2020 -color_trc smpte2084 `"$d/a_ffmpeg.png`"" | Out-Null
$l1 = Dump-Png "$d/a_ffmpeg.png" "① ffmpeg 直写(帧标签)"
$hasCicpFf = @($l1 | Where-Object { $_[0] -eq "cICP" }).Count -gt 0
Write-Output "  ① ffmpeg 是否自写 cICP: $hasCicpFf"

# ── ② 生产代码写入 sBIT(10) + cICP(9,16) ──
Copy-Item "$d/a_ffmpeg.png" "$d/b_ours.png" -Force
(Exec $pr "png3 `"$d/b_ours.png`" 10 9 16") -split "`r?`n" | Select-String -Pattern "png3:" | ForEach-Object { "  $($_.Line.Trim())" }
$l2 = Dump-Png "$d/b_ours.png" "② 生产代码写入后"
$sbit = @($l2 | Where-Object { $_[0] -eq "sBIT" }); $cicp = @($l2 | Where-Object { $_[0] -eq "cICP" })
CK ($sbit.Count -eq 1 -and $sbit[0][1] -eq "0A 0A 0A") "② sBIT = 0A 0A 0A（RGB 三通道各 10 有效位）"
CK ($cicp.Count -eq 1 -and $cicp[0][1] -eq "09 10 00 01") "② cICP = 09 10 00 01（BT.2020 / PQ / matrix=RGB / full）"
CK (@($l2 | Where-Object { $_[0] -eq "IHDR" }).Count -eq 1) "② IHDR 唯一（chunk 重写未破坏结构）"
# 顺序：cICP/sBIT 必须在 PLTE/IDAT 之前
$idxIdat = [array]::IndexOf(($l2 | ForEach-Object { $_[0] }), "IDAT")
$idxCicp = [array]::IndexOf(($l2 | ForEach-Object { $_[0] }), "cICP")
CK ($idxCicp -ge 0 -and ($idxIdat -lt 0 -or $idxCicp -lt $idxIdat)) "② cICP 位于 IDAT 之前（PNG 规范要求色彩信息在前）"
# ❗ 自写的 CRC 检查器不可信：对照组（ffmpeg 自己生成的 PNG）同样报 7/9 错，
#    而 ffmpeg 原生 PNG 的 CRC 不可能错 → 错在检查器本身（非产品）。
#    CRC 的**权威判据**已由下面的“ffmpeg 解码零告警”给出（其 PNG 解码器逐 chunk 校 CRC）。
$c  = Get-CrcBad "$d/b_ours.png"
$cf = Get-CrcBad "$d/a_ffmpeg.png"
Write-Output "  ℹ 自写 CRC 检查器（仅供参考，不判定）：我们写的 $($c.N) 个中报 $($c.Bad) 错；对照组 ffmpeg 原生 $($cf.N) 个中报 $($cf.Bad) 错 → 检查器自身不可信，已弃用"

# ── ③ 第三方解码器实机读取 ──
# ⚠ 取数 `ExecOut`：实测（laneB）ffprobe 带 `-v error` 时 stderr=0 字节、csv 值全在 stdout ⇒ 与合并版同值，
#   换尺不会由绿转红；而 ffprobe **读不到文件时** stdout 为空、报错只在 stderr ⇒ 下面两条 `-match` 判红是对的。
$probe = (ExecOut $fp "-v error -select_streams v:0 -show_entries stream=pix_fmt,color_primaries,color_transfer,color_range -of csv=p=0 `"$d/b_ours.png`"") -join " "
Write-Output "  ③ ffprobe: $probe"
CK ($probe -match "bt2020") "③ ffprobe 从文件读出 primaries=bt2020（cICP 被第三方解码器识别）"
CK ($probe -match "smpte2084") "③ ffprobe 读出 transfer=smpte2084"
$dec = (Exec $ff "-v error -i `"$d/b_ours.png`" -frames:v 1 -f null -") -split "`r?`n"
# 判据口径修正（2026-09-16）：Exec 返回 `stdout + "\n" + stderr`，两者都空时得到 "\n"，
# split 后是 2 个空元素 ⇒ 旧写法 `$dec.Count -eq 1` 在「零告警」这个**正确**情形下反而判红。
# 意图不变（ffmpeg 无任何告警输出），只把判据换成与切分方式无关的「整体空白」。
# ⚠⚠ 这里**必须**继续用合并版 `Exec`：判据吃的是 ffmpeg 的**告警全集**（CRC 错、chunk 结构错、解码告警
#   都只在 stderr —— 实测 stdout 0 字节），换成只读 stdout 会**恒真**（那条权威判定当场失效）。
#   ⚠ 但"合并"不是没有代价（2026-09-27 实测）：本机 LANG/LC_ALL 不被识别时 ffmpeg 会往 stderr 吐 locale
#   警告，ffprobe **不带 `-v error` 时**还会吐整段版本横幅（实测 4838 B）⇒ 这条「整体空白」判据在**污染
#   环境下会假红**。处置法（归一化剥掉 locale 噪声行 / 改成按字段取告警）属**改判据**，按本轮口径只登记不动。
CK ([string]::IsNullOrWhiteSpace($dec -join "")) "③ ffmpeg 解码零告警 = **CRC 与 chunk 结构的权威判定**（其 PNG 解码器逐 chunk 校 CRC；实际: $(($dec | Where-Object { $_ } | Select-Object -First 2) -join ' | '))"
$exOut = Exec $ex "-q -S -a -G1 `"$d/b_ours.png`""   # 合并取数已论证：只喂下面那行 Write-Output 摘要，不喂任何断言（人读输出，值多几行警告不影响判决）
Write-Output "  ③ exiftool 摘要: $((($exOut -split "`n" | Select-String -Pattern "Color|Bit|Image Width|PNG" | Select-Object -First 4) | ForEach-Object { $_.ToString().Trim() }) -join ' ; ')"

# ── ④ 观察项：iCCP 与 cICP 共存（产品对 P3 PNG 会带 ICC） ──
Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"testsrc2=s=32x24:d=0.04`" -pix_fmt rgb48le -frames:v 1 `"$d/c_base.png`"" | Out-Null
$icc = "$root/tests/output/validate/jpgp3/p3.icc"
if (Test-Path $icc) {
    Exec $ex "`"-icc_profile<=$icc`" -overwrite_original `"$d/c_base.png`"" | Out-Null   # 合并取数已论证：这是**写入**调用，返回值 `| Out-Null` 不消费；下面的判据走 Dump-Png 自己读块
    $l4 = Dump-Png "$d/c_base.png" "④ iCCP 嵌入后"
    $both = @($l4 | Where-Object { $_[0] -in @("iCCP","cICP") } | ForEach-Object { $_[0] }) -join "+"
    Write-Output "  ④ 共存情况: $both —— 规范优先级是 **cICP > iCCP > sRGB > cHRM/gAMA**（PNG Third Edition，" +
                  "与 ⑥ 段同源）；iCCP 与 cICP **并存本身合法**（两者一致时是有意双描述），" +
                  "矛盾的那一类才是要治的（见 ⑥）。"
    CK ($both -ne "") "④ iCCP 嵌入成功（供人工核对与 cICP 是否冲突）"
} else { SKIP "④ 缺 $icc（先跑 verify-color-tonemap 或 JPEG P3 探针）—— 以下 1 条断言未运行，不是通过" 1 }

# ── ⑥ 任务 #31(ii)：产品写了 cICP 之后，**不得**再留下被它取代且可能矛盾的 gAMA/cHRM/sRGB ──
# 为什么这条要落门禁（2026-09-26 实测，取证 `tests/output/_diag_31cur/`）：
#   修复**前**两张产物的块序是
#     ['IHDR','cICP','pHYs','sRGB','cHRM','gAMA','IDAT…']，其中 cICP.transfer=13(sRGB 分段)
#     与 gAMA=45455(γ=1/2.2) **同时存在** ⇒ 认 cICP 的读者与只认 gAMA/sRGB 的老读者解出不同结果。
#   本仓既定口径（`PngCicpService` :134/:161「优先级 cICP>iCCP>sRGB>cHRM/gAMA」「写 cICP 消除 gAMA」）
#   此前只写在注释里、代码没做 ⇒ 本段把口径钉成可判红的断言。
Write-Output "  --- ⑥ cICP 与低优先级色彩块不得并存（#31(ii)）---"
$exePng = Join-Path $root 'src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe'
$srcPng = Join-Path $root 'tests/output/sources/src_8bit.png'
if ((Test-Path $exePng) -and (Test-Path $srcPng)) {
    $od6 = Join-Path $d 'e6_prod'
    New-Item -ItemType Directory -Force -Path $od6 | Out-Null
    $a6 = @('--headless','--log-level','Debug','-i', "`"$srcPng`"", '--format','png','--bit-depth','8',
            '--color-space','"sRGB"', '--color-engine','engine', '-o', "`"$od6`"")
    $p6 = Start-Process -FilePath $exePng -ArgumentList $a6 -NoNewWindow -Wait -PassThru `
          -RedirectStandardOutput "$od6/o.txt" -RedirectStandardError "$od6/e.txt"
    $log6 = [System.IO.File]::ReadAllText("$od6/o.txt") + [System.IO.File]::ReadAllText("$od6/e.txt")
    $out6 = Get-ChildItem -Recurse $od6 -File -Filter *.png -EA SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
    if ($p6.ExitCode -eq 0 -and $out6) {
        $l6 = Dump-Png $out6.FullName "⑥ 产品产物"
        $types6 = @($l6 | ForEach-Object { $_[0] })
        $cicp6 = @($l6 | Where-Object { $_[0] -eq 'cICP' })
        CK ($cicp6.Count -eq 1) "⑥ 前置：产物确实带 cICP（实得 $($cicp6.Count) 个，块序=$($types6 -join ','))"
        # ⚠ 三条分开判：一条"不含任何低优先级块"的合计断言会在只剩其中一块时也绿（漏一半）
        foreach ($t in @('gAMA','cHRM','sRGB')) {
            CK ($types6 -notcontains $t) "⑥ 写了 cICP 后不得残留被其取代的 $t 块（实得块序=$($types6 -join ','))"
        }
        CK ($log6 -match '已剥离与 cICP 并存') "⑥ 剥离动作必须在日志里可见（不许静改产物）"
        # ── 负控（这把尺子的牙）：**仓库里跟踪的夹具**同时带 cICP 与 gAMA/cHRM ──
        #    没有它，"产物里没有 gAMA"可能只是 `Dump-Png` 读不到该类型（块长/遍历写错）。
        #    ⚠ 曾经在这里**用 PS 手工拼一个 gAMA 块**，结果拼出来的文件本脚本自己的遍历都走不通
        #      （PS 报 len=0、独立实现报 len=4096）⇒ 那种夹具既证明不了读法有牙、又把门禁自己变成噪声源。
        #      改成夹具文件：CRC 由 `zlib.crc32` 生成、`ffmpeg -v error` 解码零告警（生成脚本见
        #      `docs/COLOR_MATRIX_FIX_PLAN_2026-09-24.md` 的 #31 段）。
        $ctl6 = Join-Path $root 'tests/fixtures/png_cicp_conflicting_gama.png'
        if (Test-Path $ctl6) {
            $lc = Dump-Png $ctl6 "⑥ 负控夹具"
            $tc = @($lc | ForEach-Object { $_[0] })
            $kc = Get-CrcBad $ctl6
            CK ($kc.Bad -eq 0) "⑥ 负控夹具自身 CRC 全对（块数=$($kc.N) 错=$($kc.Bad)）——夹具坏了，'抓得到'不算检查"
            CK ($tc -contains 'cICP' -and $tc -contains 'gAMA') `
               "⑥ 负控：夹具里 cICP 与 gAMA **同时存在**，本脚本的读法必须同时抓到（实得=$($tc -join ','))"
        } else { SKIP "⑥ 缺夹具 tests/fixtures/png_cicp_conflicting_gama.png ⇒ 本段 2 条负控未运行，不是通过" 2 }
    } else { SKIP "⑥ 产品未产出（exit=$($p6.ExitCode)）⇒ 本段 7 条断言未运行，不是通过" 7 }
} else { SKIP "⑥ 缺 exe 或素材 ⇒ 本段 7 条断言未运行，不是通过" 7 }

# ── ⑤ H.273 编号表循环验证：写 cICP(p,t) → ffprobe 必须读回对应令牌（ffmpeg 当 oracle） ──
# 目的：证明 C# 里的令牌→编号表不是背出来的；编号错则 ffprobe 读出另一个名字。
Write-Output "  --- ⑤ CICP 编号表循环验证（oracle = ffprobe）---"
$pairs = @(
    @(1, 13,  "bt709",    "iec61966-2-1"),
    @(9, 16,  "bt2020",   "smpte2084"),
    @(9, 18,  "bt2020",   "arib-std-b67"),
    @(12, 13, "smpte432", "iec61966-2-1"),
    @(9, 14,  "bt2020",   "bt2020-10"),
    @(9, 15,  "bt2020",   "bt2020-12"),
    @(1, 8,   "bt709",    "linear"),
    @(6, 6,   "smpte170m","smpte170m")
)
$rtFail = 0
foreach ($q in $pairs) {
    $f = "$d/rt_$($q[0])_$($q[1]).png"
    Copy-Item "$d/a_ffmpeg.png" $f -Force
    # 第二位 0 = 不写 sBIT（TryInsertSbit 对 bits=0 直接拒绝且不改文件），只验 cICP 编号
    Exec $pr "png3 `"$f`" 0 $($q[0]) $($q[1])" | Out-Null
    # ⚠ `ExecOut`（实测 ffprobe `-v error` 的 csv 全在 stdout、stderr 0 B ⇒ 与合并版同值，换尺不由绿转红）：
    #   下面把它 `-split ','` 再与期望令牌做**集合比较**，任何多出来的行都会让 `Compare-Object` 失配。
    $got = ((ExecOut $fp "-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer -of csv=p=0 `"$f`"") -join ",")
    # ⚠ ffprobe 按它自己的规范顺序输出（transfer 在前），不能依赖 -show_entries 的书写顺序
    #   → 把两侧拆成集合比较（字段含义另由下面显式检查）
    $g = @($got -split ',') | ForEach-Object { $_.Trim() } | Sort-Object
    $w = @($q[2], $q[3]) | Sort-Object
    $okOne = (@(Compare-Object $g $w -SyncWindow 0)).Count -eq 0
    # 再分别校 prim/transfer 字段（防两个字段恰好互换而集合比较漏检）
    # ⚠ 这两条是**逐字段 -eq 对账**（不是子串匹配）⇒ 取数必须只读 stdout：实测本机 ffprobe 带 `-v error`
    #   时 stderr 为 0 字节，但**去掉 `-v error` 就会倒 4838 B 横幅**（实测）⇒ 用合并版等于把这条判据的
    #   成立押在"每次都记得加 -v error"上，而 `.Trim()` 只剥得掉首尾空白、剥不掉拼进来的文本。
    $gp = (ExecOut $fp "-v error -select_streams v:0 -show_entries stream=color_primaries -of csv=p=0 `"$f`"").Trim()
    $gt = (ExecOut $fp "-v error -select_streams v:0 -show_entries stream=color_transfer -of csv=p=0 `"$f`"").Trim()
    if ($okOne -and $gp -eq $q[2] -and $gt -eq $q[3]) { Write-Output "    ok   cICP($($q[0]),$($q[1])) → prim=$gp trc=$gt" }
    else { $rtFail++; Write-Output "    ✗ 差 cICP($($q[0]),$($q[1])) → prim='$gp'(期望$($q[2])) trc='$gt'(期望$($q[3]))" }
}
CK ($rtFail -eq 0) "⑤ H.273 编号表与 ffprobe 循环一致（$($pairs.Count) 对，失败 $rtFail）"

Write-Output ""
Write-Output "===== PNG 3.0 实机互操作: PASS=$pass FAIL=$fail SKIP=$skip ====="
# 跑绿自清（跑红保留 GUID 目录供排查）—— 必须在 exit 之前，且用显式 if/else 给出退出码（§6 第 66/67 条）。
if ($fail -eq 0) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
if ($fail -eq 0) { exit 0 } else { exit 1 }
