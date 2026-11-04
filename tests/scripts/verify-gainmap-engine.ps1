# verify-gainmap-engine.ps1 —— GainMap 走**色彩引擎**的专项门禁（2026-09-16 新增）
#
# 背景（为什么需要独立一条）：
#   `--jpeg-gain-map true` + JPEG 输出，原先被 `ColorEngineRouter.UnconsumedOutputSettings`
#   整条拦下（BlockReason 含 "GainMap（属 P6）"）⇒ 走 QueueProcessor 的**独立 GainMap 分派**
#   （纯 C# GainMapEncoder + cjpegli），引擎完全不参与色彩决策。
#   2026-09-16 起改为**由色彩引擎决策并执行**；非 JPEG 格式的拒绝点下移到**规划层**
#   （`ColorStrategyPlanning` 的 `GainMapRequested && !Format.SupportsGainMap ⇒ Reject`）。
#
# 本门禁只做**端到端可观测**的断言（跑真实产品 CLI + ServiceProbe 解析容器结构），不直接调内部函数：
#   ① 默认引擎（不传 --color-engine）：HDR 源 + jpg + --jpeg-gain-map ⇒ 产出非空，且**被识别为 Ultra HDR**
#      （容器可解析、副图可定位、ISO 21496-1 元数据在）；
#   ② 底图与增益图**都真实存在**（不是「一张普通 JPEG 改了名」）：增益图能被纯托管切割出来、
#      本身是可解码 JPEG、且只是容器的一部分；底图尺寸等于源尺寸；
#   ③ 负控：非 JPEG 格式 + GainMap ⇒ **明确拒绝并给建议**，且**不得**产出「看着像成功」的文件；
#      另加「负控的负控」：同一输入不开 GainMap ⇒ 必须正常产出（排除“输入坏了”的假红）；
#   ④ 显式 `--color-engine legacy` + GainMap ⇒ **按设计仍走引擎**（2026-09-18 收窄断言，见该段注释）；
#      负控：显式 legacy + **非** GainMap ⇒ 仍不得出现任何引擎日志（逃生门零污染未被放宽）；
#   ⑤ 引擎出口的底图色域与增益图通道数（`--jpeg-gain-map-base-gamut` / `-multi-channel` 真透传）；
#   ⑤b **第三方判据**（2026-09-19 补，C⑥）：exiftool 直读产物 —— bt2020 底图内嵌 ICC 的红原色
#      色度（≈0.708）与默认 sRGB 底（≈0.640）**不同**；XMP hdrgm 只许出现 Ultra HDR v1.1 的九项
#      （Channels/UseBaseRenderingColorSpace/MapPrimary/MapSecondary/MapImageRatio 是自造字段，必须绝迹），
#      通道数由增益图 JPEG 的 SOF 分量数表达（§1c 的 Nf 判据）。
#      ⚠ 原 ⑤ 对 bt2020 只有**产品日志散文** + `channels=1`（后者与默认相同、
#      无判别力）⇒ 本节补上第三方可证判据（见 §5 末）。另有 ⑤b'：SDR 源 + bt2020 底 ⇒ 用 ffmpeg
#      解出主图 raw 比字节，证明底图**像素真被转换**（不是"只改 ICC 标签"）。
#   ⑤c `--jpeg-gain-map-base-gamut` 的**取值校验**此前无门禁：非法值必须硬失败 + 不产出 + 点名。
#   ⑥ 预缩放中转（`-vf scale=128:-2`）不得静默退化为 SDR：ISO max 与直跑原素材**相同**（防静默降级）；
#   ⑦ tonemap 误伤修复：SDR 源 + 显式 `--color-tone-map` + GainMap ⇒ 产出 + 登记 `ToneMapNotAppliedToGainMap`；
#      负控：**非** GainMap 格 + SDR 源 + tonemap ⇒ 仍须拒绝（那道门一个字没放宽）。
#   ⑧ GainMap + 最长边缩放 ⇒ **走引擎**（2026-09-18 Part C 删掉那段「死拦截」后的新契约）：
#      既断言"真的走引擎的 GainMap 出口"，又断言"几何真的缩了 + 产物仍是真 Ultra HDR"；
#      负控：旧专用管线的专属日志（`本次由 GainMap 专用管线` / `[GainMap] Step 1`）**不得**出现。
#   ⑨ `isHdrInput` 判据换源后的 HDR/SDR 分类锁（2026-09-19 补，C⑦）：构造「cICP=PQ + 内嵌 sRGB ICC」
#      的 PNG ⇒ 实测 **ICC 赢**（引擎走 SDR）；去掉 ICC 的对照 ⇒ 走 HDR（唯一变量是 ICC）。
#      ⚠ 该行为与 **PNG 第三版规范**（Table 1：cICP 优先级 1 > iCCP 优先级 2）**矛盾**，
#      已登记为疑似缺陷；本断言锁**当前真实行为**，将来按规范修成 HDR 时会转红（属预期红）。
#   ⑬ **第三方实拍件语料**（2026-11-02 补，16 条）：三件真实第三方 GainMap 件（DJI `DSC*` 两件走ISO 21496-1 APP2、`seine_sdr_gainmap_srgb.jpg` 走 hdrgm-XMP）落 `tests/output/corpus/gainmap-iso21496/`，整树 gitignore ⇒ **永不随仓分发**；缺件时 fail-closed 并在消息里给 `Invoke-WebRequest` 取回命令。覆盖合成夹具造不出的两个形态：**负 min**（`min=-0.005`）与**剂量分叉**（10.1899 > 1.5×4.1263 且 cap 更大）；`seine` 同时作 hdrgm-XMP **负控**（不得被认成 ISO 21496-1 APP2）。
#
# 退出码：0 = 全绿，1 = 有失败。⚠ 与 _run-step3-gates.ps1 的约定一致（脚本自身不带 gate 前缀）。
#
# ⚠ 双宿主兼容（PS 7 与 PS 5.1，门禁：verify-ps-compat.ps1）：
#   禁止三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁止全角弯引号（用 「」）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$pr  = "$root/tests/projects/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $pr)) { $pr = "$root/tests/projects/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $pr + $(if ($pr -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$fp  = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$et  = "$root/publish/PLAN/exiftool/exiftool.exe"
foreach ($need in @($exe, $pr, $fp, $et)) { if (-not (Test-Path $need)) { Write-Output "缺工具：$need"; exit 1 } }

# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   唯一目录没人替你清 ⇒ 结尾自清（照 `verify-gainmap-memory.ps1` 既有范式）。
$val = "$root/tests/output/validate"; $out = "$val/gmeng_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$hdr = "$val/src_hdr.png"; $sdr = "$val/sdr_plain.png"
foreach ($need in @($hdr, $sdr)) { if (-not (Test-Path $need)) { Write-Output "缺素材 $need（先跑 ensure-fixtures.ps1）"; exit 1 } }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }

# ⚠ 参数名不能叫 $args；取子进程输出一律 Start-Process（`& exe` 在部分宿主会静默失败 ⇒ 断言假红）
function RunCli([string]$tag, [string]$in, [string]$fmt, [string[]]$extra) {
    $a = @('--headless', '-i', $in, '--format', $fmt, '--output', "$out/$tag", '--log-level', 'Debug') + $extra
    $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput "$out/$tag.o" -RedirectStandardError "$out/$tag.e"
    $log = [System.IO.File]::ReadAllText("$out/$tag.o") + [System.IO.File]::ReadAllText("$out/$tag.e")
    $f = Get-ChildItem -Recurse "$out/$tag" -Include "*.$fmt" -EA SilentlyContinue | Select-Object -First 1
    return @{ code = $p.ExitCode; log = $log; file = $f }
}
# 读侧：把一条 Ultra HDR 产物重新交给同一个产品解码（--format png）。
function RunDecode([string]$tag, [string]$inFile) {
    $a = @('--headless', '-i', $inFile, '--format', 'png', '--output', "$out/dec_$tag", '--log-level', 'Debug')
    $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput "$out/dec_$tag.o" -RedirectStandardError "$out/dec_$tag.e"
    $log = [System.IO.File]::ReadAllText("$out/dec_$tag.o") + [System.IO.File]::ReadAllText("$out/dec_$tag.e")
    $f = Get-ChildItem -Recurse "$out/dec_$tag" -Include '*.png' -EA SilentlyContinue | Select-Object -First 1
    return @{ code = $p.ExitCode; log = $log; file = $f }
}
function ProbeOut([string]$mode, [string]$arg, [string]$arg2) {
    $o = "$out/_probe.txt"
    $al = @($mode, ('"' + $arg + '"'))
    if ($arg2) { $al += ('"' + $arg2 + '"') }
    Start-Process -FilePath $pr -ArgumentList $al -NoNewWindow -Wait -PassThru -RedirectStandardOutput $o | Out-Null
    return ([System.IO.File]::ReadAllText($o))
}

# 逐条算 + 逐条报（2026-10-28 账本 §57）：旧写法把四项模式 AND 成一条判据 ⇒ 红了说不出死在哪项
#   （那一轮就是靠手工翻 `apple_new.o` 才定位到「PFM 那行压根没打」）。这里只改**可诊断性**，
#   通过条件一项没减也没加：同样的「出件 ∧ 四项模式全命中」。
#   入参形如 @('检测=检测到 Ultra HDR JPEG', 'headroom=headroom=4\.532783')（名称 | 正则）。
function PatHit([string[]]$named, [string]$log) {
    $parts = @(); $all = $true
    foreach ($it in $named) {
        $kv = $it -split '\|', 2
        $hit = ($log -match $kv[1])
        if (-not $hit) { $all = $false }
        $parts += ($kv[0] + '=' + $hit)
    }
    return @{ ok = $all; detail = ($parts -join ' ') }
}
# 像素级读数：ServiceProbe gainmap-stats 读回**解码器真正写出**的线性像素（gbrpf32le，1.0 = SDR 白点）。
# ⚠ 不能用产品日志里的「峰值 N nits」充当施加证据 —— 那是 `203 × 2^HDRCapacityMax` 由元数据算的。
function ProbeStats([string]$path, [string]$expect) {
    $o = "$out/_stats.txt"
    $al = @('gainmap-stats', ('"' + $path + '"'))
    if ($expect) { $al += $expect }
    Start-Process -FilePath $pr -ArgumentList $al -NoNewWindow -Wait -PassThru -RedirectStandardOutput $o | Out-Null
    return ([System.IO.File]::ReadAllText($o))
}
# 实测宽高：由 ffprobe 读回，不靠产品自己的解析器自证。多幅图容器取第一行（主图）。
function FfprobeSize([string]$path) {
    if (-not (Test-Path $path)) { return "-" }
    $o = "$out/_size.txt"
    Start-Process -FilePath $fp -ArgumentList @('-v', 'error', '-select_streams', 'v:0', '-show_entries',
        'stream=width,height', '-of', 'csv=p=0', $path) `
        -NoNewWindow -Wait -RedirectStandardOutput $o | Out-Null
    $lines = @([System.IO.File]::ReadAllText($o) -split "`r?`n" | Where-Object { $_ -and $_.Trim() })
    if ($lines.Count -eq 0) { return "-" }
    return $lines[0].Trim()
}
# ffprobe 读回 color_transfer（第三方判据：PNG 的 cICP 标签）。
function FfprobeTrc([string]$path) {
    if (-not (Test-Path $path)) { return "" }
    $o = "$out/_trc.txt"
    Start-Process -FilePath $fp -ArgumentList @('-v', 'error', '-select_streams', 'v:0', '-show_entries',
        'stream=color_transfer', '-of', 'csv=p=0', $path) `
        -NoNewWindow -Wait -RedirectStandardOutput $o | Out-Null
    return ([System.IO.File]::ReadAllText($o)).Trim()
}
# 第三方判据：exiftool 直读产物/素材的标签（**不经产品自己的解析器或日志散文**）。
# ⚠ exiftool 是 perl 脚本，stderr 常带 locale 警告 ⇒ 必须单独重定向，只读 stdout。
function ExifTag([string]$path, [string]$tag, [switch]$EE) {
    if (-not (Test-Path $path)) { return "" }
    $o = "$out/_et.out"; $e = "$out/_et.err"
    $al = @('-s', '-s')
    if ($EE) { $al += '-ee' }
    $al += "-$tag"; $al += $path
    Start-Process -FilePath $et -ArgumentList $al `
        -NoNewWindow -Wait -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
    $t = ([System.IO.File]::ReadAllText($o)).Trim()
    $i = $t.IndexOf(': ')
    if ($i -ge 0) { return $t.Substring($i + 2).Trim() }
    return ""
}

Write-Host "`n### 1) 默认引擎：HDR 源 + jpg + --jpeg-gain-map ⇒ 真 Ultra HDR ###" -ForegroundColor Cyan
# 刻意**不传** --color-engine：默认值即契约（默认就是引擎）。
$e1 = RunCli 'eng' $hdr 'jpg' @('--jpeg-gain-map', 'true')
CK ($e1.code -eq 0) "退出码 0（实得 $($e1.code)）"
CK ($e1.file -and $e1.file.Length -gt 0) "产出非空文件（$($e1.file.Length) B）"
CK ($e1.log -notmatch '尚未接入该路径') "日志不再声明「GainMap 路径尚未接入引擎」（旧契约已失效）"
# ⚠ 这条必须看**决策行**而不是泛泛的 `[色彩引擎]`：旧路径也会打一条 `[色彩引擎] ⚠️ …` 警告，
#   只匹配 `[色彩引擎]` 会让本断言在“还没接上”时**空转通过**（实测踩到）。
#   决策行只可能由引擎决策产生，故它对“是否真的走引擎”有牙。
CK ($e1.log -match '\[色彩引擎\] 决策：') "出现引擎**决策行** ⇒ 色彩决策确实由引擎做（不是被路由拦下后由旧管线自决）"
if ($e1.file) {
    $p1 = ProbeOut 'gainmap' $e1.file.FullName "$out/sec1.jpg"
    CK ($p1 -match 'images=2') "① 被识别为 Ultra HDR 容器（顶层 2 幅图：底图 + 增益图）"
    CK ($p1 -match 'iso min=') "① ISO 21496-1 增益元数据存在"
    CK ($p1 -match 'mpf\[1\].*soi=True') "① MPF 副图偏移命中 SOI（增益图可定位）"
    CK ($p1 -match 'PROBE RESULT: pass=\d+ fail=0') "① 探针容器结构断言全通过（含「ISO 增益区间非零」）"
} else { CK $false "① 无产物，容器结构断言无法进行" }

# ── 1b) 底图的 ISO 21496-1「版本裸桩」────────────────────────────────────────
#   Ultra HDR / ISO 21496-1 把这一列标成 Required：底图也要有一根 APP2，体 = 命名空间 +
#   minimum_version(0000) + writer_version(0000)，**只有版本、没有数值**（数值只在次图那根里）。
#   参考实现 libultrahdr v2.0.2：声明见 jpegr.cpp:1074-1078，写入见 :1278-1292。
#   本仓此前只在次图写了全量 ISO（GainMapEncoder 组装段里没有它）⇒ 只读底图找 21496 的消费者
#   认不出这是增益图，而我们的日志却宣称「双元数据」。⇒ 这条判据就是那笔账的锁。
# 唯一的标记扫描器：§1b 的裸桩判据与 §1c 的三档指纹**共用它**（两处各写一份扫描 = #69 ⑤ 要抓的镜像）。
# 返回 @{ Starts=每张图的 SOI 偏移; Segs=按偏移排序的 (marker,len,off) 表 }。
# 扫描数据里 0xFF 后必然跟 0x00（JPEG 的字节填充），所以遇到 FFxx(x≠0) 一定是真标记 ⇒ 线性扫可行。
function Get-JpegSegmentWalk([byte[]]$b) {
    $starts = @()
    $segs = @()
    $i = 0
    while ($i + 1 -lt $b.Length) {
        if ($b[$i] -ne 0xFF) { $i++; continue }
        $m = $b[$i + 1]
        if ($m -eq 0xD8) { $starts += $i; $i += 2; continue }
        if ($m -eq 0xD9 -or $m -eq 0x00 -or $m -eq 0xFF -or $m -eq 0x01 -or ($m -ge 0xD0 -and $m -le 0xD7)) { $i += 2; continue }
        if ($i + 3 -ge $b.Length) { break }
        $len = ($b[$i + 2] * 256) + $b[$i + 3]
        if ($len -lt 2) { break }
        $segs += [pscustomobject]@{ Off = $i; Marker = $m; Len = $len }
        $i += 2 + $len
    }
    return @{ Starts = $starts; Segs = $segs }
}

# 从 $from 开始找第一根 SOF，返回 Nf（分量数）。XMP-only 没有通道数字段（Ultra HDR v1.1 的 hdrgm 只有九项）；
# 通道语义由增益图 image 本身表达——本实现的「灰度」也编成 3 分量 YCbCr（三通道相等），
# 所以 Nf 只是结构事实，灰/彩的判别要看解码后的数据（Get-ChannelSpread）。
function Get-JpegSofComponents([byte[]]$b, [int]$from) {
    if ($from -lt 0 -or $from -ge $b.Length) { return -1 }
    $i = $from
    while ($i + 3 -lt $b.Length) {
        if ($b[$i] -ne 0xFF) { $i++; continue }
        $m = $b[$i + 1]
        if ($m -eq 0xD8 -or $m -eq 0xD9 -or $m -eq 0x00 -or $m -eq 0xFF -or $m -eq 0x01 -or ($m -ge 0xD0 -and $m -le 0xD7)) { $i += 2; continue }
        $len = ($b[$i + 2] * 256) + $b[$i + 3]
        if ($len -lt 2) { break }
        if ($m -ge 0xC0 -and $m -le 0xCF -and $m -ne 0xC4 -and $m -ne 0xC8 -and $m -ne 0xCC -and $len -ge 8 -and ($i + 9) -lt $b.Length) {
            return [int]$b[$i + 9]          # payload 起于 i+4；Nf = payload+5
        }
        $i += 2 + $len
    }
    return -1
}

# 解码后的增益图（rgb24）里有多少像素三通道不相等：灰度增益=0，逐通道增益≈全量。
function Get-ChannelSpread([string]$path) {
    if (-not (Test-Path $path)) { return @{ Pixels = 0; Differ = 0 } }
    $b = [System.IO.File]::ReadAllBytes($path)
    $d = 0
    for ($i = 0; $i + 2 -lt $b.Length; $i += 3) {
        if (-not ($b[$i] -eq $b[$i + 1] -and $b[$i + 1] -eq $b[$i + 2])) { $d++ }
    }
    return @{ Pixels = [int]($b.Length / 3); Differ = $d }
}

function Get-BaseIsoStub([byte[]]$b) {
    $urn = [System.Text.Encoding]::ASCII.GetBytes('urn:iso:std:iso:ts:21496:-1')
    $res = @{ Found = $false; Off = -1; Len = 0; Tail = ''; Why = '主图 SOS 之前没有段长=34 且命名空间命中的 APP2' }
    $walk = Get-JpegSegmentWalk $b
    $secStart = -1
    if (@($walk.Starts).Count -ge 2) { $secStart = @($walk.Starts)[1] }
    foreach ($sg in $walk.Segs) {
        if ($secStart -ge 0 -and $sg.Off -ge $secStart) { break }     # 只看底图
        if ($sg.Marker -ne 0xE2 -or $sg.Len -ne 34) { continue }
        $o = $sg.Off + 4
        $same = $true
        for ($k = 0; $k -lt 27; $k++) { if ($b[$o + $k] -ne $urn[$k]) { $same = $false; break } }
        if (-not $same -or ($b[$o + 27] -ne 0)) { continue }
        $tail = ''
        for ($t = 28; $t -lt 32; $t++) { $tail = $tail + ('{0:x2}' -f $b[$o + $t]) }
        $res.Found = $true; $res.Off = $sg.Off; $res.Len = $sg.Len; $res.Tail = $tail; $res.Why = ''
        return $res
    }
    return $res
}

# 三档的容器指纹：ISO 命名空间与 hdrgm 命名空间各在**底图/次图**出现几次。
# 只数"全文件共几次"是不够的——档内两处都可能落在同一张图上。
function Get-GmMetaFingerprint([string]$path) {
    $b = [System.IO.File]::ReadAllBytes($path)
    $walk = Get-JpegSegmentWalk $b
    $txt = [System.Text.Encoding]::ASCII.GetString($b)
    $secStart = -1
    if (@($walk.Starts).Count -ge 2) { $secStart = @($walk.Starts)[1] }
    $isoBase = 0; $isoSec = 0; $xmpBase = 0; $xmpSec = 0
    foreach ($pat in @('urn:iso:std:iso:ts:21496:-1')) {
        $at = 0
        while ($true) {
            $j = $txt.IndexOf($pat, $at)
            if ($j -lt 0) { break }
            if ($secStart -ge 0 -and $j -ge $secStart) { $isoSec++ } else { $isoBase++ }
            $at = $j + 1
        }
    }
    foreach ($pat in @('http://ns.adobe.com/hdr-gain-map/1.0/')) {
        $at = 0
        while ($true) {
            $j = $txt.IndexOf($pat, $at)
            if ($j -lt 0) { break }
            if ($secStart -ge 0 -and $j -ge $secStart) { $xmpSec++ } else { $xmpBase++ }
            $at = $j + 1
        }
    }
    # 底图 XMP 自声明的次图长度 vs 实测次图长度（P0-4 加了次图 XMP 后，这个账必须仍然闭合）
    $declared = ''
    if ($txt -match 'Item:Length="(\d+)"') { $declared = $Matches[1] }
    $secActual = 0
    if ($secStart -ge 0) { $secActual = $b.Length - $secStart }
    return @{ Images = @($walk.Starts).Count; IsoBase = $isoBase; IsoSec = $isoSec
              XmpBase = $xmpBase; XmpSec = $xmpSec; DeclaredLen = $declared; ActualLen = $secActual
              Stub = (Get-BaseIsoStub $b) }
}

# 合成臂（2026-10-27 合流时改）：本树形状**不带**底图裸桩 ⇒ 原来的"摘掉后必须判无"失去被测物，
#   会变成真空通过。改成正向构造：把一根**合规**裸桩（FF E2 + 段长 34 + URN + 0x00 + 4 字节版本尾）
#   插进底图 SOI 之后，同一把尺必须判**有** ⇒ 证明"判无"那条是尺子在起作用，不是尺子坏了。
#   只改内存字节、不落盘 ⇒ 不影响任何产物指纹。
function Add-BaseIsoStubSynthetic([byte[]]$b) {
    $urn = [System.Text.Encoding]::ASCII.GetBytes('urn:iso:std:iso:ts:21496:-1')
    $payload = New-Object 'byte[]' 32
    [Array]::Copy($urn, 0, $payload, 0, 27)
    $payload[27] = 0            # 命名空间结束符
    $payload[28] = 0; $payload[29] = 1; $payload[30] = 0; $payload[31] = 0   # 版本 major/minor
    $seg = New-Object 'byte[]' (4 + 32)
    $seg[0] = 0xFF; $seg[1] = 0xE2; $seg[2] = 0x00; $seg[3] = 34
    [Array]::Copy($payload, 0, $seg, 4, 32)
    $out = New-Object 'byte[]' ($b.Length + $seg.Length)
    [Array]::Copy($b, 0, $out, 0, 2)                 # SOI
    [Array]::Copy($seg, 0, $out, 2, $seg.Length)     # 合成桩
    [Array]::Copy($b, 2, $out, 2 + $seg.Length, $b.Length - 2)
    return ,$out
}

# 变异臂：在原字节里把那根裸桩摘掉（不落盘、不重建），用来证明上面那把尺有牙。
function Remove-BaseIsoStub([byte[]]$b) {
    $f = Get-BaseIsoStub $b
    if (-not $f.Found) { return ,$b }
    $span = 2 + $f.Len
    $out = New-Object 'byte[]' ($b.Length - $span)
    [Array]::Copy($b, 0, $out, 0, $f.Off)
    [Array]::Copy($b, $f.Off + $span, $out, $f.Off, $b.Length - $f.Off - $span)
    return ,$out
}

Write-Host "`n### 1b) 底图 ISO 21496-1 裸桩：本树形状 = **不带**（形状主张的处置见账本 §54.1/§55）###" -ForegroundColor Cyan
if ($e1.file) {
    $bytes1 = [System.IO.File]::ReadAllBytes($e1.file.FullName)
    $stub = Get-BaseIsoStub $bytes1
    CK (-not $stub.Found) ("①b 默认档(=iso)：底图**不得**出现 APP2 ISO 裸桩（beta9 出货形状 = ISO 只插次图；实 Found=" + $stub.Found + ' off=' + $stub.Off + '）')
    $stubSynth = Get-BaseIsoStub (Add-BaseIsoStubSynthetic $bytes1)
    CK ($stubSynth.Found) ("①b 反控（读尺活性）：往底图合成一根合规裸桩后，同一把尺必须判有（实 Found=" + $stubSynth.Found + ' 段长=' + $stubSynth.Len + ' 版本尾=' + $stubSynth.Tail + '）')
    $urnAll = ([regex]::Matches([System.Text.Encoding]::ASCII.GetString($bytes1), 'urn:iso:std:iso:ts:21496:-1')).Count
    CK ($urnAll -eq 1) ("①b ISO 命名空间恰好 1 处（只次图全量），实 " + $urnAll + ' —— 多了说明底图被插桩/重复插入，少了说明次图丢了 ISO')
    Write-Output '  KNOWN ①b 未采 origin/main 的『底图必须带版本裸桩（规范列为 Required）』：该主张出自 2026-10-20 批次（docs/FIX_PLAN_2026-10-14.md:1119-1145），其"维护者裁决"归因已于 2026-10-27 由维护者本人否认 ⇒ 容器形状属未决项，不是本树契约（IMPLEMENT_PLAN §54.1/§55）'
} else { CK $false "①b 无产物，底图裸桩形状断言无法进行" }

# ── 1c) 元数据方式三档必须**真的分叉**（iso / xmp / both）───────────────────────
#   为什么不能只测"读得回"：三档都读得回时，"开关没生效"与"生效了"在探针输出上同形。
#   ⇒ 打字节指纹：两种命名空间各自落在底图还是次图，外加次图自声明长度的账是否闭合。
Write-Host "`n### 1c) 元数据方式三档的容器指纹 ###" -ForegroundColor Cyan
$armIso = RunCli 'meta_iso'  $hdr 'jpg' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-metadata', 'iso')
$armXmp = RunCli 'meta_xmp'  $hdr 'jpg' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-metadata', 'xmp')
$armBoth = RunCli 'meta_both' $hdr 'jpg' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-metadata', 'both')
$armXmpRgb = RunCli 'meta_xmp_rgb' $hdr 'jpg' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-metadata', 'xmp',
                                             '--jpeg-gain-map-multi-channel', 'true')
foreach ($a in @(@{ n = 'iso'; r = $armIso }, @{ n = 'xmp'; r = $armXmp }, @{ n = 'both'; r = $armBoth })) {
    CK ($a.r.code -eq 0 -and $a.r.file) ("①c " + $a.n + " 档正常产出（实得 code=" + $a.r.code + '）')
}
# 2026-10-20：Ultra HDR v1.1 的 hdrgm 只有九项，GainMapMin/Max 等参数允许「单个 Real 适用于全部通道」，
# 参考实现也只拒绝各通道参数**不相同**的 XMP 编码（jpegr.cpp:1120，!are_all_channels_identical）。
# 本实现的逐通道参数恒相同 ⇒ xmp + RGB 是合法组合，必须产出而不是拒绝。
CK ($armXmpRgb.code -eq 0 -and $armXmpRgb.file) ("①c xmp + RGB 三通道可产出（实得 code=" + $armXmpRgb.code + '）')
if ($armIso.file -and $armXmp.file -and $armBoth.file -and $armXmpRgb.file) {
    $fIso = Get-GmMetaFingerprint $armIso.file.FullName
    $fXmp = Get-GmMetaFingerprint $armXmp.file.FullName
    $fBoth = Get-GmMetaFingerprint $armBoth.file.FullName
    $fXmpRgb = Get-GmMetaFingerprint $armXmpRgb.file.FullName
    # 历史形状（=beta9 出货件、方案一之前）：XMP 只写底图、ISO 只插次图、底图无裸桩 ⇒ 参考读手在 xmp 档读不到。
    # origin/main 同批的另两条"底图也带 ISO 桩 / XMP 只九项"仍未采（P0-3/P0-5，未获授权，§54.1）⇒ 不在此钉，
    # 改钉成"每档的四个位置各有唯一答案"，判别力不减（任一档空转/错位即红）。
    # 2026-10-27 方案一改写出器后的**新形状**（账本 §56）：数值同时写在底图（元素形式，自家读手与
    # exiftool 走这条）与次图（属性形式，libultrahdr 唯一入口，jpegr.cpp:1495-1500 + jpegrutils.cpp:770-800）。
    # 改这条之前，`xmp`/`both` 档对参考读手是「received no valid buffer to parse gainmap metadata」（D5e 实测）。
    CK ($fIso.IsoBase -eq 0 -and $fIso.IsoSec -eq 1 -and $fIso.XmpBase -eq 0 -and $fIso.XmpSec -eq 0) ("①c iso 档＝只次图 1 份 ISO 全量、两处 XMP 都不写（实 底ISO=" + $fIso.IsoBase + ' 次ISO=' + $fIso.IsoSec + ' 底XMP=' + $fIso.XmpBase + ' 次XMP=' + $fIso.XmpSec + '）')
    CK ($fXmp.IsoBase -eq 0 -and $fXmp.IsoSec -eq 0 -and $fXmp.XmpBase -eq 1 -and $fXmp.XmpSec -eq 1) ("①c xmp 档＝底图与次图各 1 份 XMP、不写 ISO（实 底ISO=" + $fXmp.IsoBase + ' 次ISO=' + $fXmp.IsoSec + ' 底XMP=' + $fXmp.XmpBase + ' 次XMP=' + $fXmp.XmpSec + '）')
    CK ($fBoth.IsoBase -eq 0 -and $fBoth.IsoSec -eq 1 -and $fBoth.XmpBase -eq 1 -and $fBoth.XmpSec -eq 1) ("①c both 档＝底图 XMP + 次图 XMP + 次图 ISO 全量（实 底ISO=" + $fBoth.IsoBase + ' 次ISO=' + $fBoth.IsoSec + ' 底XMP=' + $fBoth.XmpBase + ' 次XMP=' + $fBoth.XmpSec + '）')
    # 三档互不相同 = 开关确实改变产物（若某一档空转，这三条里必有一条相等而判红）
    CK (($fIso.XmpBase + $fXmp.XmpBase) -eq 1) ("①c 反控：iso 与 xmp 两档的**底图 XMP**必须一有一无（实 iso底XMP=" + $fIso.XmpBase + ' xmp底XMP=' + $fXmp.XmpBase + '）')
    CK (($fIso.IsoSec + $fXmp.IsoSec) -eq 1) ("①c 反控：iso 与 xmp 两档的**次图 ISO**必须一有一无（实 iso次ISO=" + $fIso.IsoSec + ' xmp次ISO=' + $fXmp.IsoSec + '）')
    # 与容器形状无关的硬判据：三档产物两两不同字节（某档空转 ⇒ 必有一条相等 ⇒ 红）
    $hIso = (Get-FileHash -Algorithm SHA256 $armIso.file.FullName).Hash
    $hXmp = (Get-FileHash -Algorithm SHA256 $armXmp.file.FullName).Hash
    $hBoth = (Get-FileHash -Algorithm SHA256 $armBoth.file.FullName).Hash
    CK ($hIso -ne $hXmp -and $hIso -ne $hBoth -and $hXmp -ne $hBoth) ("①c 三档产物两两不同（SHA256 前 16：iso=" + $hIso.Substring(0,16) + ' xmp=' + $hXmp.Substring(0,16) + ' both=' + $hBoth.Substring(0,16) + '）')
    Write-Output "  KNOWN ①c 『次图也必须带一份 XMP』这一条**已随方案一落地**（2026-10-27，理由不是形状偏好而是实测：libultrahdr 只从次图取元数据）；仍未采的是同批的『底图 ISO 版本裸桩』与『XMP 只许九项』（P0-3/P0-5，未获授权且与 #80 互斥 ⇒ 待裁决，§54.1/§55/§56）"
    # 次图自声明长度 == 实测次图长度（P0-4 之后次图自己多带一段 XMP，这笔账最容易漏）
    CK ($fBoth.DeclaredLen -eq [string]$fBoth.ActualLen) ("①c both 档：底图 XMP 声明的 Item:Length=" + $fBoth.DeclaredLen + ' 必须等于实测次图 ' + $fBoth.ActualLen + ' 字节')
    CK ($fXmp.DeclaredLen -eq [string]$fXmp.ActualLen) ("①c xmp 档：同一笔账闭合（声明 " + $fXmp.DeclaredLen + ' vs 实测 ' + $fXmp.ActualLen + '）')
    CK ($fXmpRgb.IsoBase -eq 0 -and $fXmpRgb.IsoSec -eq 0 -and $fXmpRgb.XmpBase -eq 1 -and $fXmpRgb.XmpSec -eq 1) ("①c xmp+RGB 档＝底图与次图各 1 份 XMP、不写 ISO（实 底ISO=" + $fXmpRgb.IsoBase + ' 次ISO=' + $fXmpRgb.IsoSec + ' 底XMP=' + $fXmpRgb.XmpBase + ' 次XMP=' + $fXmpRgb.XmpSec + '）')
    CK ($fXmpRgb.DeclaredLen -eq [string]$fXmpRgb.ActualLen) ("①c xmp+RGB 档：Item:Length 账闭合（声明 " + $fXmpRgb.DeclaredLen + ' vs 实测 ' + $fXmpRgb.ActualLen + '）')
    CK ($fXmpRgb.Images -eq 2) ("①c xmp+RGB 档仍是两幅图（实 " + $fXmpRgb.Images + '）')
    # SOF 分量数（结构事实）
    $bRgb = [System.IO.File]::ReadAllBytes($armXmpRgb.file.FullName)
    $wRgb = Get-JpegSegmentWalk $bRgb
    $sRgb = -1; if (@($wRgb.Starts).Count -ge 2) { $sRgb = [int]@($wRgb.Starts)[1] }
    $nfRgb = Get-JpegSofComponents $bRgb $sRgb
    $bGray = [System.IO.File]::ReadAllBytes($armXmp.file.FullName)
    $wGray = Get-JpegSegmentWalk $bGray
    $sGray = -1; if (@($wGray.Starts).Count -ge 2) { $sGray = [int]@($wGray.Starts)[1] }
    $nfGray = Get-JpegSofComponents $bGray $sGray
    # 通道语义的载体分两层：JPEG 恒为 3 分量（灰度也编成 YCbCr，三通道相等）；
    #   真正的「逐通道增益」要看解码后的数据，SOF 不区分灰/彩。
    $ffm3 = Join-Path (Split-Path $fp) 'ffmpeg.exe'
    $secG = "$out/sec_xmp_gray.jpg"; $secC = "$out/sec_xmp_rgb.jpg"
    $null = ProbeOut 'gainmap' $armXmp.file.FullName $secG
    $null = ProbeOut 'gainmap' $armXmpRgb.file.FullName $secC
    $rawG = "$out/sec_xmp_gray.rgb"; $rawC = "$out/sec_xmp_rgb.rgb"
    Start-Process -FilePath $ffm3 -ArgumentList @('-v', 'error', '-y', '-i', $secG,
        '-f', 'rawvideo', '-pix_fmt', 'rgb24', $rawG) -NoNewWindow -Wait | Out-Null
    Start-Process -FilePath $ffm3 -ArgumentList @('-v', 'error', '-y', '-i', $secC,
        '-f', 'rawvideo', '-pix_fmt', 'rgb24', $rawC) -NoNewWindow -Wait | Out-Null
    $sprG = Get-ChannelSpread $rawG
    $sprC = Get-ChannelSpread $rawC
    CK ($nfGray -eq 3 -and $nfRgb -eq 3) ("①c 增益图 JPEG 恒为 3 分量（实 " + $nfGray + '/' + $nfRgb + '）—— SOF 不区分灰/彩，通道语义在数据里')
    CK ($sprG.Pixels -gt 1000 -and $sprG.Differ -eq 0) ("①c xmp 单通道臂：增益图逐像素三通道相等（有差像素=" + $sprG.Differ + '/' + $sprG.Pixels + '）')
    CK ($sprC.Pixels -gt 1000 -and $sprC.Differ -gt [int]($sprC.Pixels / 2)) ("①c xmp+RGB 臂：增益图逐通道数据真的不同（有差像素=" + $sprC.Differ + '/' + $sprC.Pixels + '）⇒ 上一条有判别力')
    CK ($fBoth.Images -eq 2 -and $fXmp.Images -eq 2 -and $fIso.Images -eq 2) ("①c 三档都是两幅图（实 " + $fIso.Images + '/' + $fXmp.Images + '/' + $fBoth.Images + '）—— 拒绝对外不是"普通 JPEG 改名"')
} else { CK $false '①c 三档缺产物，指纹判据无法进行' }

# ── 1d) 读侧：iso / xmp / both 三档产物都必须被自家解码器识别 ─────────────────
#   为什么读侧要单列：写侧"有元数据"不等于读侧认得出。规范把增益元数据放在**次图** XMP，
#   主图只留 hdrgm:Version；不带 -ee 的读取器只读主图 ⇒ 会把合规的 XMP-only 文件当普通 JPEG
#   （2026-10-20 用参考实现生成的 ref_xmp 实测：修前输出 SDR 53120 B，修后 117451 B + HDR 日志）。
Write-Host "`n### 1d) 读侧：三档产物都能被自家解码器识别 ###" -ForegroundColor Cyan
foreach ($a in @(
        @{ n = 'iso';  r = $armIso;  want = 'ISO 21496-1 APP2' },
        @{ n = 'xmp';  r = $armXmp;  want = 'hdrgm XMP' },
        @{ n = 'both'; r = $armBoth; want = 'ISO 21496-1 APP2' })) {
    if (-not $a.r.file) { CK $false ("①d " + $a.n + " 缺产物，读侧判据无法进行"); continue }
    $d = RunDecode $a.n $a.r.file.FullName
    CK ($d.code -eq 0 -and $d.file) ("①d " + $a.n + " 档可回解并产出 png（code=" + $d.code + '）')
    CK ($d.log -match ('元数据来源: ' + [regex]::Escape($a.want))) ("①d " + $a.n + " 档被识别为 " + $a.want)
    CK ($d.log -match '已解码为线性 HDR PFM') ("①d " + $a.n + " 档确实按增益图恢复 HDR（不是只解底图）")
}
# 反控：同输入**不写**增益图 ⇒ 解码器不得报任何元数据来源（否则上面的"识别"可能对任何 jpg 都成立）
$plainJpg = RunCli 'plain_jpg' $hdr 'jpg' @()
if ($plainJpg.file) {
    $decPlain = RunDecode 'plain' $plainJpg.file.FullName
    CK (-not ($decPlain.log -match '元数据来源:')) ("①d 反控：普通 JPEG（无增益图）不得出现元数据来源行")
} else { CK $false '①d 反控缺产物' }

Write-Host "`n### 2) 底图与增益图都真实存在（不是「普通 JPEG 改名」）###" -ForegroundColor Cyan
if ($e1.file) {
    $prodLen = (Get-Item $e1.file.FullName).Length
    $secPath = "$out/sec1.jpg"
    $secLen = 0
    if (Test-Path $secPath) { $secLen = (Get-Item $secPath).Length }
    CK ($secLen -gt 100) "② 增益图被纯托管切割出来且非空（$secLen B）"
    CK ($secLen -gt 0 -and $secLen -lt $prodLen) "② 增益图只是容器的一部分，不是整个文件（$secLen < $prodLen）"
    $secSz = FfprobeSize $secPath
    CK ($secSz -match '^\d+,\d+$') "② 增益图本身是可解码的 JPEG（ffprobe 读回 $secSz）"
    $prodSz = FfprobeSize $e1.file.FullName
    CK ($prodSz -eq '512,384') "② 底图尺寸 == 源尺寸（源 512x384，ffprobe 读回 $prodSz）"
} else { CK $false "② 无产物，底图/增益图存在性无法验证" }

Write-Host "`n### 3) 负控：非 JPEG + GainMap ⇒ 明确拒绝 + 建议，且不得产出「看着像成功」的文件 ###" -ForegroundColor Cyan
$n1 = RunCli 'neg_png' $hdr 'png' @('--jpeg-gain-map', 'true', '--color-engine', 'engine')
CK ($n1.code -ne 0) "退出码非 0（实得 $($n1.code)）—— 拒绝必须是响亮的"
CK (-not $n1.file) "未产出 .png（不得产出「看着像成功」的文件）"
CK ($n1.log -match 'GainMap') "日志点名 GainMap（不是含糊失败）"
CK ($n1.log -match '建议') "明确拒绝时给出建议（日志含「建议：」）"
CK ($n1.log -match 'AVIF') "建议内容点名改 AVIF/JXL（日志含 AVIF）"
# 负控的负控：同一输入**不开** GainMap ⇒ 必须正常产出（排除“输入坏了 / 引擎坏了”造成的假红）
$n0 = RunCli 'neg_png_ok' $hdr 'png' @('--color-engine', 'engine')
CK ($n0.code -eq 0 -and $n0.file) "对照：同输入不开 GainMap ⇒ 正常产出 png（实得 code=$($n0.code)）"

# ⚠⚠ 2026-09-20 补（本轮新锁）：**引擎不可走**的非 JPEG（`bmp`）此前**绕过**规划层的能力闸
#   （`ColorIntentFactory` 对 BMP/DNG/PPM 返回 null ⇒ `SupportsGainMap` 闸**根本不运行**）
#   ⇒ 请求被**静默丢弃**：L3 实测 20 例 `gainmap-absent`（产物是普通 BMP、日志**无点名**、末行仍报「完成 1 项, 失败 0 项」）。
#   修法 = 分派处补「非 JPEG + GainMap + **引擎不可走** ⇒ 契约拒绝（项级 91，与引擎侧同码）」。
#   ⚠ 本格锁的正是「**引擎不可走**」那一半（上面 `neg_png` 走的是引擎 ⇒ 规划层拦），二者互补，不得只留其一。
$n2 = RunCli 'neg_bmp' $hdr 'bmp' @('--jpeg-gain-map', 'true')
CK ($n2.code -ne 0) "⑨b 引擎不可走的非 JPEG（bmp）+ GainMap ⇒ 退出码非 0（实得 $($n2.code)）—— 不得静默忽略"
CK (-not $n2.file) "⑨b 未产出 .bmp（不得产出「看着像成功」的文件）"
CK ($n2.log -match '契约拒绝') "⑨b 日志**点名契约拒绝**（不是静默忽略）"
CK ($n2.log -match 'GainMap') "⑨b 点名 GainMap（不是含糊失败）"
# 负控的负控：同一 bmp + **不开** GainMap ⇒ 必须正常产出（排除「bmp 本身坏了 / 引擎坏了」造成的假红）
$n3 = RunCli 'neg_bmp_ok' $hdr 'bmp' @()
CK ($n3.code -eq 0 -and $n3.file) "⑨b 对照：同 bmp 不开 GainMap ⇒ 正常产出（实得 code=$($n3.code)）"

Write-Host "`n### 4) 显式 legacy + GainMap ⇒ **按设计仍走引擎**（GainMap 是引擎自有能力）###" -ForegroundColor Cyan
# ⚠ **2026-09-18 本条断言收窄（不是放宽）**：原断言是「显式 legacy 一行 `[色彩引擎]` 日志都不该出现」。
#   依据维护者裁定「GainMap 软件应该有一套自实现的管线，这一套自实现管线融合进引擎作为 GainMap 后续的
#   完整实现」⇒ 引擎是 GainMap 的**唯一实现**；`--color-engine legacy` 是「色彩像素怎么算」的执行体选择，
#   而 GainMap（ISO 21496-1 / MPF）是**输出结构**能力 —— 两个正交维度，前者不该把后者踢出引擎。
#   ⇒ 显式 legacy + GainMap **必须**出现引擎日志，且必须真的走**引擎的** GainMap 出口。
#   逃生门的「零污染」语义因此**收窄为**：显式 legacy + **非 GainMap** ⇒ 仍不得出现任何引擎日志
#   （见下方负控 —— 这一条把「断言被整条删掉」与「断言被合理收窄」区分开）。
$l1 = RunCli 'legacy' $hdr 'jpg' @('--jpeg-gain-map', 'true', '--color-engine', 'legacy')
CK ($l1.code -eq 0) "退出码 0（实得 $($l1.code)）"
CK ($l1.file -and $l1.file.Length -gt 0) "产出非空（$($l1.file.Length) B）"
CK ($l1.log -match '\[色彩引擎\] 决策：') "显式 legacy + GainMap ⇒ 出现引擎**决策行**（GainMap 恒走引擎，legacy 开关踢不走它）"
# ⚠ 用「色调映射由本出口自持」而不是 `[GainMap] Step 2`：后者**两条路径都会打**
#   （旧专用管线 `ProcessGainMapJpegAsync` 也打 `[GainMap] Step 2: ffmpeg 解码…`，实测）⇒ 它没有判别力。
#   前者只由引擎出口 `RawColorPipeline.TransformGainMapAsync` 打出（全仓唯一出处）。
CK ($l1.log -match '\[GainMap\] 色调映射由本出口自持') "显式 legacy + GainMap ⇒ 走的是**引擎的** GainMap 出口（引擎出口专属日志行）"
# 负控（逃生门零污染**未被放宽**）：显式 legacy + 非 GainMap ⇒ 一行引擎日志都不该出现。
$l0 = RunCli 'legacy_plain' $hdr 'png' @('--color-engine', 'legacy')
CK ($l0.code -eq 0 -and $l0.file) "对照：显式 legacy + 非 GainMap ⇒ 正常产出（实得 code=$($l0.code)）"
CK ($l0.log -notmatch '\[色彩引擎\]') "对照：显式 legacy + 非 GainMap ⇒ 仍不得出现任何引擎日志（逃生门零污染未被放宽）"
if ($l1.file) {
    $p2 = ProbeOut 'gainmap' $l1.file.FullName "$out/sec2.jpg"
    CK ($p2 -match 'images=2') "④ 显式 legacy + GainMap 产物同样是 Ultra HDR 容器（顶层 2 幅图）"
    CK ($p2 -match 'iso min=') "④ 该产物 ISO 21496-1 元数据存在"
    CK ($p2 -match 'mpf\[1\].*soi=True') "④ 该产物副图可定位"
    CK ($p2 -match 'PROBE RESULT: pass=\d+ fail=0') "④ 该产物容器断言全通过"
} else { CK $false "④ 无产物，容器结构断言无法进行" }

Write-Host "`n### 5) 引擎出口的底图色域与增益图通道数（接入前零覆盖）###" -ForegroundColor Cyan
# ⚠ 这两项在 GainMap 接入引擎**之前没有任何门禁覆盖**（旧专用管线时也没有）。
#   断言取值来自 2026-09-16 的**探索性实测**，不是猜的：
#     --jpeg-gain-map-base-gamut bt2020 ⇒ 日志「底=Rec.2020(SDR)」+ ISO `channels=1`（灰度增益图）
#     --jpeg-gain-map-multi-channel     ⇒ 日志「底=sRGB(SDR)」    + ISO `channels=3`（RGB 增益图）
#   `channels` 是**真判别式**：它由 GainMapEncoder 按 multiChannel 写进 ISO 21496-1，与底图色域无关
#   ⇒ 两格必须分别落在 1 与 3；若参数没透传到编码器，这里会红（而不是被"产物非空"蒙过去）。
$g1 = RunCli 'gm_bt2020' $hdr 'jpg' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-base-gamut', 'bt2020')
CK ($g1.code -eq 0) "⑤ bt2020 底：退出码 0（实得 $($g1.code)）"
CK ($g1.file -and $g1.file.Length -gt 0) "⑤ bt2020 底：产出非空（$($g1.file.Length) B）"
CK ($g1.log -match '底=Rec\.2020\(SDR\)') "⑤ bt2020 底：日志确认底图色域为 Rec.2020(SDR)（参数真透传）"
if ($g1.file) {
    $pg1 = ProbeOut 'gainmap' $g1.file.FullName "$out/sec3.jpg"
    CK ($pg1 -match 'images=2') "⑤ bt2020 底：仍是 Ultra HDR 容器（顶层 2 幅图）"
    CK ($pg1 -match 'iso min=') "⑤ bt2020 底：ISO 21496-1 元数据存在"
    CK ($pg1 -match 'channels=1') "⑤ bt2020 底：增益图为灰度（ISO channels=1）"
} else { CK $false "⑤ bt2020 底：无产物，容器断言无法进行" }

$g2 = RunCli 'gm_multi' $hdr 'jpg' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-multi-channel', 'true')
CK ($g2.code -eq 0) "⑤ multi-channel：退出码 0（实得 $($g2.code)）"
CK ($g2.file -and $g2.file.Length -gt 0) "⑤ multi-channel：产出非空（$($g2.file.Length) B）"
if ($g2.file) {
    $pg2 = ProbeOut 'gainmap' $g2.file.FullName "$out/sec4.jpg"
    CK ($pg2 -match 'images=2') "⑤ multi-channel：仍是 Ultra HDR 容器（顶层 2 幅图）"
    CK ($pg2 -match 'channels=3') "⑤ multi-channel：增益图为 RGB（ISO channels=3）"
} else { CK $false "⑤ multi-channel：无产物，容器断言无法进行" }

# 负控：两格产物必须**真的不同**。否则说明其中一个选项被忽略、上面两条 channels 断言在测同一件事。
if ($g1.file -and $g2.file) {
    $h1 = (Get-FileHash $g1.file.FullName -Algorithm MD5).Hash
    $h2 = (Get-FileHash $g2.file.FullName -Algorithm MD5).Hash
    CK ($h1 -ne $h2) "⑤ 负控：bt2020 底 与 multi-channel 产物**不同**（$($g1.file.Length)B vs $($g2.file.Length)B）⇒ 两个选项都真生效"
} else { CK $false "⑤ 负控：缺产物，无法比较" }

Write-Host "`n### 5b) 第三方判据：exiftool 直读产物（C⑥ 补，原 ⑤ 只有产品日志散文）###" -ForegroundColor Cyan
# ⚠ 为什么必须补：原 ⑤ 对 bt2020 只有**产品自己的日志**（`底=Rec.2020(SDR)`）+ ISO `channels=1`。
#   但 `channels=1` 与**默认（sRGB 底）完全相同** ⇒ 它不是判别式，bt2020 若被静默忽略，原 ⑤ 照样绿。
#   底图色域由**底图内嵌 ICC** 表达（JPEG 无 CICP，见 GainMapEncoder.cs:139-144）⇒ 用 exiftool 读
#   ICC 的**色度**才是判别式：BT.2020 红原色 x≈0.708，sRGB/BT.709 x≈0.640。
if ($g1.file -and $e1.file) {
    $g1Red = ExifTag $g1.file.FullName 'ICC_Profile:ChromaticityChannel1'
    $e1Red = ExifTag $e1.file.FullName 'ICC_Profile:ChromaticityChannel1'
    CK ($g1Red -match '^0\.70[0-9]') "⑤b bt2020 底：exiftool 读底图 ICC 红原色 ≈ BT.2020（实得「$g1Red」）"
    CK ($e1Red -match '^0\.64') "⑤b 默认底：exiftool 读底图 ICC 红原色 == sRGB 0.64（实得「$e1Red」）⇒ 上一条有判别力"
    CK ($g1Red -ne $e1Red -and $g1Red -ne "") "⑤b bt2020 与默认的底图 ICC 色度**不同**（$g1Red vs $e1Red）⇒ bt2020 真改了底图色域"
} else { CK $false "⑤b 缺产物，无法做 exiftool 色度判别" }
# ⚠ 2026-10-20：默认档改成 iso（不再写 XMP）⇒ 原先直接读 `$e1`/`$g2` 的 XMP 标签会恒空。
#   2026-10-27 合流：本树把 hdrgm **数值写在底图**（14 项，含五个扩展字段），origin/main 要求
#   「底图只留 Version、数值在次图、且只九项」——那是该批次 agent 的容器形状主张，未获维护者授权
#   （IMPLEMENT_PLAN §54.1/§55）⇒ 这里不再钉"自造字段绝迹"，改钉更强的**一致性**：
#   ① 规范九项必须**逐项读得到且非空**（缺一项就是元数据说谎）；
#   ② 五个扩展字段必须与**同一份文件里第三方读到的事实**成套（尺寸标签≠实测几何、或通道数≠实际交付 ⇒ 红）；
#   ③ 次图自声明长度仍须等于实测（§1c 的账）。
#   通道数另由 §1c 的 SOF Nf 与逐通道像素判据表达。仍为它另起两条 both 档臂，不去改 $e1/$g2。
$e1b = RunCli 'e1_both' $hdr 'jpg' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-metadata', 'both')
$g2b = RunCli 'g2_both' $hdr 'jpg' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-metadata', 'both',
                                     '--jpeg-gain-map-multi-channel', 'true')
CK ($e1b.code -eq 0 -and $e1b.file) "⑤b both 档（单通道）正常产出（实得 code=$($e1b.code)）"
CK ($g2b.code -eq 0 -and $g2b.file) "⑤b both 档（三通道）正常产出（实得 code=$($g2b.code)）"
if ($g2b.file -and $e1b.file) {
    # ① 规范九项逐项非空（读尺 = exiftool 直读底图 XMP，不经产品自己的解析器）
    $nine = @('Version', 'GainMapMin', 'GainMapMax', 'Gamma', 'OffsetSDR', 'OffsetHDR',
              'HDRCapacityMin', 'HDRCapacityMax', 'BaseRenditionIsHDR')
    $missing = @()
    foreach ($nm in $nine) {
        $v = ExifTag $e1b.file.FullName ('XMP-hdrgm:' + $nm)
        if ($v -eq '') { $missing += $nm }
    }
    CK ($missing.Count -eq 0) ("⑤b 规范九项在底图 XMP 内逐项可读（缺失 " + $missing.Count + ' 项' + $(if ($missing.Count -gt 0) { '［' + ($missing -join ',') + '］' } else { '' }) + '）')
    # ② 扩展字段与第三方实测成套：MapPrimary 必须等于**底图**真实几何（标签说不存在的尺寸=说谎）
    $mpTag = ExifTag $e1b.file.FullName 'XMP-hdrgm:MapPrimary'
    $baseSize = (FfprobeSize $e1b.file.FullName) -replace '\s*,\s*', 'x'
    CK ($mpTag -ne '' -and $baseSize -ne '-' -and $mpTag -eq $baseSize) ("⑤b MapPrimary=「" + $mpTag + '」必须等于 ffprobe 实测底图 ' + $(if ($baseSize -eq '-') { '（读不到，判红）' } else { "「$baseSize」" }))
    #    MapSecondary 必须等于**次图**真实几何（次图由自家解码器取出，再交 ffprobe 量）
    $msTag = ExifTag $e1b.file.FullName 'XMP-hdrgm:MapSecondary'
    $secForSize = "$out/sec_e1b.jpg"
    $null = ProbeOut 'gainmap' $e1b.file.FullName $secForSize
    $secSize = (FfprobeSize $secForSize) -replace '\s*,\s*', 'x'
    CK ($msTag -ne '' -and $secSize -ne '-' -and $msTag -eq $secSize) ("⑤b MapSecondary=「" + $msTag + '」必须等于实测次图 ' + $(if ($secSize -eq '-') { '（取不到次图，判红）' } else { "「$secSize」" }))
    #    Channels 必须与**实际交付的通道数**成套：灰度臂=1、三通道臂=3（写错=声明与数据分家）
    $chGray = ExifTag $e1b.file.FullName 'XMP-hdrgm:Channels'
    $chRgb = ExifTag $g2b.file.FullName 'XMP-hdrgm:Channels'
    CK ($chGray -eq '1' -and $chRgb -eq '3') ("⑤b Channels 标签随实际通道成套（灰度臂实得「" + $chGray + '」期望 1；三通道臂实得「' + $chRgb + '」期望 3）')
    Write-Output ("  KNOWN ⑤b 未采 origin/main 的『XMP 只许规范九项 / 底图只留 Version / 数值写次图』（docs/GAINMAP_ROUTES_AND_PATCH_PLAN.md P0-5）：其依据 libultrahdr jpegrutils.cpp:915-940 是真读数，但那是参考实现的形状，五个扩展字段是否被真实读者拒绝本机无读者可证 ⇒ 属未决主张，非本树契约（§54.1/§55）")
} else { CK $false "⑤b 缺 both 档产物，无法做 XMP 字段判别" }
# ⑤b' 像素级判别（比 ICC 更硬）：**SDR 源(bt709)** + base=bt2020 ⇒ 底图**像素真的被转换**，
#   不是"只改 ICC 标签"。原理：srgb 底走 bt709→bt709（**恒等 ⇒ 无映射**）；bt2020 底走
#   bt709→bt2020（**真转换**）⇒ 两者的底图像素必然不同。判据 = 用**第三方解码器**（ffmpeg）
#   把各自容器的**主图**解成 rawvideo 后比字节（若选项被忽略，两次输出逐字节相同 ⇒ 本断言转红）。
$ffm5 = Join-Path (Split-Path $fp) 'ffmpeg.exe'
$s1 = RunCli 'sdr_srgb' $sdr 'jpg' @('--jpeg-gain-map', 'true')
$s2 = RunCli 'sdr_2020' $sdr 'jpg' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-base-gamut', 'bt2020')
CK ($s1.code -eq 0 -and $s1.file) "⑤b' SDR 源 + srgb 底正常产出（实得 code=$($s1.code)）"
CK ($s2.code -eq 0 -and $s2.file) "⑤b' SDR 源 + bt2020 底正常产出（实得 code=$($s2.code)）"
if ($s1.file -and $s2.file) {
    $r1 = "$out/s1.raw"; $r2 = "$out/s2.raw"
    Start-Process -FilePath $ffm5 -ArgumentList @('-v', 'error', '-y', '-i', $s1.file.FullName,
        '-f', 'rawvideo', '-pix_fmt', 'rgb24', $r1) -NoNewWindow -Wait | Out-Null
    Start-Process -FilePath $ffm5 -ArgumentList @('-v', 'error', '-y', '-i', $s2.file.FullName,
        '-f', 'rawvideo', '-pix_fmt', 'rgb24', $r2) -NoNewWindow -Wait | Out-Null
    $len1 = 0; if (Test-Path $r1) { $len1 = (Get-Item $r1).Length }
    $len2 = 0; if (Test-Path $r2) { $len2 = (Get-Item $r2).Length }
    CK ($len1 -gt 0 -and $len1 -eq $len2) "⑤b' 两路底图均解出等长 raw（$len1 vs $len2 B）"
    $m1 = ""; if ($len1 -gt 0) { $m1 = (Get-FileHash $r1 -Algorithm MD5).Hash }
    $m2 = ""; if ($len2 -gt 0) { $m2 = (Get-FileHash $r2 -Algorithm MD5).Hash }
    $p1 = "-"; if ($m1.Length -ge 8) { $p1 = $m1.Substring(0, 8) }
    $p2 = "-"; if ($m2.Length -ge 8) { $p2 = $m2.Substring(0, 8) }
    CK ($m1 -ne "" -and $m2 -ne "" -and $m1 -ne $m2) "⑤b' 底图像素**真的不同**（raw MD5 $p1 vs $p2）⇒ bt2020 底是真转换，不是只改标签"
} else { CK $false "⑤b' 缺产物，像素级判别无法进行" }
# ⑤c 取值校验（`--jpeg-gain-map-base-gamut` 的值域此前**无门禁**；范式同 `verify-gamut-map.ps1` §6）：
#   非法取值必须**硬失败 + 不产出 + 点名**，不得静默按默认（srgb）跑 —— 那正是本项目最忌讳的
#   「看起来配置过」假象。判据 = 退出码 + 有无产物 + 日志点名（第三方可证：产物不存在）。
$bad = RunCli 'gm_bad_gamut' $hdr 'jpg' @('--jpeg-gain-map', 'true', '--jpeg-gain-map-base-gamut', 'bogus')
CK ($bad.code -ne 0) "⑤c 非法 base-gamut：退出码非 0（实得 $($bad.code)）"
CK (-not $bad.file) "⑤c 非法 base-gamut：不产出任何文件（不得静默按 srgb 跑）"
CK ($bad.log -match '取值无效') "⑤c 非法 base-gamut：日志点名「取值无效」（不是静默忽略）"

Write-Host "`n### 6) 预缩放中转不得静默退化为 SDR（ISO max 锁）###" -ForegroundColor Cyan
# 为什么要锁：GainMap 的 headroom 完全由**源的 HDR 语义**决定（1.0 = 峰值 nits）。若中转件在
#   缩放/重编码途中丢掉 `rgb48be` 或 `bt2020/smpte2084` 标签，源会被当成 SDR 读入 ⇒ 静默产出
#   headroom=1 的假 Ultra HDR（增益区间 max=0.000，底图被压暗却没有可用的增益信息）。
# 判据（零新增工具）：复用 `ServiceProbe gainmap` 的 `iso max` 读数，与**直跑原素材**的读数比对。
#   实测（2026-09-18）：`-vf scale=128:-2` 中转后 ffmpeg 仍输出 `rgb48be(gbr/bt2020/smpte2084)`
#   ⇒ 两路 ISO max 都是 2.3（= log2(1000/203)）。静默退化时该值会变成 0.000。
$ffm = Join-Path (Split-Path $fp) 'ffmpeg.exe'
if (-not (Test-Path $ffm)) { Write-Output "缺工具：$ffm"; exit 1 }
$relay = "$out/relay128.png"
# ⚠ 刻意**不**指定 `-pix_fmt`、也**不**重打 cICP：中转件要能自己扛住 16bit + HDR 标签。
Start-Process -FilePath $ffm -ArgumentList @('-y', '-hide_banner', '-loglevel', 'error',
    '-i', $hdr, '-vf', 'scale=128:-2', $relay) -NoNewWindow -Wait | Out-Null
CK (Test-Path $relay) "⑥ 预缩放中转件已生成（-vf scale=128:-2，未指定 pix_fmt / 未重打 cICP）"
$r1 = RunCli 'prescale' $relay 'jpg' @('--jpeg-gain-map', 'true')
CK ($r1.code -eq 0 -and $r1.file) "⑥ 预缩放中转件跑 GainMap 正常产出（实得 code=$($r1.code)）"
function IsoMax([string]$text) {
    $m = [regex]::Match($text, 'iso min=\S+ max=(\S+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return ""
}
$directMax = ""
if ($e1.file) { $directMax = IsoMax (ProbeOut 'gainmap' $e1.file.FullName "$out/sec5.jpg") }
$relayMax = ""
if ($r1.file) { $relayMax = IsoMax (ProbeOut 'gainmap' $r1.file.FullName "$out/sec6.jpg") }
CK ($directMax -ne "") "⑥ 直跑基准的 ISO max 可读（实得 「$directMax」）"
CK ($relayMax -ne "") "⑥ 预缩放中转的 ISO max 可读（实得 「$relayMax」）"
# ⚠ 用**数值**判"非退化"而不是字符串比 `0.000`：探针的格式是 `0.####`，SDR（headroom=1）会打成 `0`
#   而不是 `0.000`（实测）—— 写成字符串比会漏判，正好把本锁的牙拔掉。
$relayNum = 0.0
$relayNumOk = [double]::TryParse($relayMax, [ref]$relayNum)
CK ($relayMax -eq $directMax -and $relayNumOk -and $relayNum -gt 0) "⑥ 预缩放中转后 ISO max **不变且非零**（$relayMax == $directMax，>0；静默退化成 SDR 时 headroom=1 ⇒ max=0）"

Write-Host "`n### 7) tonemap 误伤修复：SDR 源 + 显式 tonemap + GainMap ⇒ 产出 + 诚实降级 ###" -ForegroundColor Cyan
# 改动前（实测）：`ColorIntentFactory` 的 tonemap 装配门（「tone-map 需要 HDR 源」）在**连 GainMap 出口
#   都没进**的时候就把任务 Reject 掉（进程退出码 ≠0、不产出），而 legacy 产出。
# 为什么是误伤：`RawColorPipeline`（GainMap 出口）明写「计划层的 tonemap 参数**不适用于** GainMap」——
#   底图压暗由 `GainMapEncoder` 内部的**分段 Reinhard** 自持 ⇒ 该选项在 GainMap 出口**根本不会被消费**，
#   产出不会因此出错。现改为「**产出 + 诚实告知**」：放行 + 登记结构化降级 `ToneMapNotAppliedToGainMap`。
$m1 = RunCli 'sdrtm_gm' $sdr 'jpg' @('--jpeg-gain-map', 'true', '--color-tone-map', 'hable')
CK ($m1.code -eq 0) "⑦ SDR 源 + tonemap + GainMap：退出码 0（实得 $($m1.code)；改动前为 ≠0 且零产出）"
CK ($m1.file -and $m1.file.Length -gt 0) "⑦ 产出非空（$($m1.file.Length) B）"
CK ($m1.log -match '降级（ToneMapNotAppliedToGainMap）') "⑦ 诚实告知：日志打出稳定降级码 ToneMapNotAppliedToGainMap（用户要求的曲线确实没生效）"
if ($m1.file) {
    $pm = ProbeOut 'gainmap' $m1.file.FullName "$out/sec7.jpg"
    # ⚠ 2026-09-20 裁定（**SDR ⇒ 普通 JPEG**）：SDR 源**不再**产出「零增益的假 Ultra HDR」
    #   ⇒ 顶层**只有 1 幅图**（普通 JPEG）。本格原断言 `images=2` 已被该裁定**推翻**。
    #   ⚠ 判据**不弱化**：改为「**必须不是** Ultra HDR 容器」+「必须点名为何未启用」——
    #     后者正是本段原意（产出 + 诚实告知）的直接探针。
    CK ($pm -match 'images=1') "⑦ 产物是**普通 JPEG**（顶层 1 幅图；SDR 源不再产零增益的假 Ultra HDR）"
    CK ($m1.log -match '输入为 SDR') "⑦ 诚实告知：日志点名「输入为 SDR…已按普通 JPEG 输出」（非静默降级）"
} else { CK $false "⑦ 无产物，容器断言无法进行" }
# 负控：**非** GainMap 格**不得**放宽这道门（否则「HDR 才需要 tonemap」的检查失效 ⇒ 真正的静默压暗会溜过去）。
$m0 = RunCli 'sdrtm_png' $sdr 'png' @('--color-tone-map', 'hable')
CK ($m0.code -ne 0) "⑦ 负控：非 GainMap + SDR 源 + tonemap ⇒ 仍须拒绝（实得 $($m0.code)）"
CK ($m0.log -match '需要 HDR 源') "⑦ 负控：拒绝理由仍是「tone-map 需要 HDR 源」（这道门一个字没放宽）"

Write-Host "`n### 8) GainMap + 最长边缩放 ⇒ 走引擎（Part C 删掉死拦截后的新契约）###" -ForegroundColor Cyan
# 背景：原先 `ColorEngineRouter` 对「GainMap + 最长边缩放」显式拦（Code=`GeometryWithGainMap`）。
# 2026-09-18 实测证明那是**死拦截**：`QueueProcessor.cs:421-470` 的预缩放中继（**所有后端共用**、
# 在格式分发之前）已把超限输入换成缩过的临时件 ⇒ 引擎侧 `BuildMaxDimensionScaleFilter` 恒返回 null
# ⇒ 判据恒假。删除后该组合 `ShouldRoute=true` ⇒ 由引擎的 GainMap 出口产出真 Ultra HDR。
# 本节把新契约钉住：**既**要"真的走引擎"，**又**要"几何真的缩了 + 产物仍是真 Ultra HDR"。
# ⚠ 只断言"退出码 0 + 有产物"是不够的：那种断言在旧契约下（走专用管线）**同样绿** ⇒ 没有牙。
$q1 = RunCli 'gmgeo' $hdr 'jpg' @('--jpeg-gain-map', 'true', '--max-dimension-enabled', 'true', '--max-dimension', '128')
CK ($q1.code -eq 0) "⑧ GainMap + 最长边缩放(128)：退出码 0（实得 $($q1.code)）"
CK ($q1.file -and $q1.file.Length -gt 0) "⑧ 产出非空（$($q1.file.Length) B）"
CK ($q1.log -match '\[色彩引擎\] 决策：') "⑧ 出现引擎**决策行** ⇒ 色彩决策确实由引擎做（不是被路由拦下后由旧管线自决）"
CK ($q1.log -match '\[GainMap\] 色调映射由本出口自持') "⑧ 走的是**引擎的** GainMap 出口（该行全仓唯一出处 = RawColorPipeline.TransformGainMapAsync）"
# 负控（本节真正的牙）：旧专用管线的**专属**日志不得出现。
#   ① `本次由 GainMap 专用管线` 只在 `gainMapViaDedicated` 为真时打（QueueProcessor 诚实提示）；
#   ② `[GainMap] Step 1` 只在 `ProcessGainMapJpegAsync`（专用管线）打 ——
#      ⚠ 刻意**不**用 `[GainMap] Step 2`：两路都会打（实测），它没有判别力。
CK ($q1.log -notmatch '本次由 GainMap 专用管线') "⑧ 负控：不得出现「GainMap 专用管线」提示（该组合已由引擎接管）"
CK ($q1.log -notmatch '\[GainMap\] Step 1') "⑧ 负控：不得出现专用管线专属的「[GainMap] Step 1」"
if ($q1.file) {
    # 几何确实生效：源 512x384、最长边限 128 ⇒ 底图应为 128x96（由 ffprobe 读回，不靠产品自证）。
    $q1sz = FfprobeSize $q1.file.FullName
    CK ($q1sz -eq '128,96') "⑧ 几何确实生效：底图 == 128x96（源 512x384 限最长边 128，ffprobe 读回 $q1sz）"
    $pq1 = ProbeOut 'gainmap' $q1.file.FullName "$out/sec8.jpg"
    CK ($pq1 -match 'images=2') "⑧ 产物仍是真 Ultra HDR 容器（顶层 2 幅图：底图 + 增益图）"
    CK ($pq1 -match 'iso min=') "⑧ ISO 21496-1 增益元数据存在"
    CK ($pq1 -match 'mpf\[1\].*soi=True') "⑧ MPF 副图偏移命中 SOI（增益图可定位）"
    CK ($pq1 -match 'PROBE RESULT: pass=\d+ fail=0') "⑧ 探针容器结构断言全通过（含「ISO 增益区间非零」）"
} else { CK $false "⑧ 无产物，几何与容器结构断言无法进行" }

Write-Host "`n### 9) isHdrInput 判据换源：ICC 感知 vs 裸 ffprobe（C⑦，2026-09-19 补）###" -ForegroundColor Cyan
# 背景：`isHdrInput` 的判据由「裸 ffprobe `color_transfer`」换成「ICC 感知的 `ProbeInputColorMetadata`」
#   （消费点 GainMapJpegCodec.ResolveEncodeParams:111 → `meta.colorTrc`；ICC 命中时
#    `hasIccSemantics=true` 会**屏蔽** ffprobe 回退，见 FfmpegCommandBuilder.cs:1256）。
# 素材构造：`src_hdr.png`（cICP=PQ/bt2020、**无 ICC**）内嵌 **sRGB ICC** ⇒
#   · ffprobe 仍报 `color_transfer=smpte2084`（cICP chunk 没被删）
#   · exiftool 读到 ICC（sRGB）⇒ ICC 推导 trc=`iec61966-2-1` ⇒ `IsHdrInput=false`
# 实测（2026-09-19）：**ICC 赢** ⇒ 引擎走 **SDR** 路（headroom=1 ⇒ XMP GainMapMax=0.00）。
# ⚠ 诚实登记：这与 **PNG 第三版规范**矛盾 —— 规范 4.3 / Table 1 规定 cICP 优先级 1 > iCCP 优先级 2，
#   「同时存在时**优先级数字最低者优先**」⇒ 该素材按规范应判 **HDR(PQ)**。本行为属**疑似缺陷**，
#   已上报（不是"正确契约"）。本断言锁的是**当前真实行为**；将来若按规范修成 HDR，这里会转红
#   ⇒ 那是**预期红**，届时同步更新本断言与 HANDOVER/TESTING 记账。
$iccSrc = "$root/tests/output/sources/srgb.icc"
if (-not (Test-Path $iccSrc)) { $iccSrc = "C:/Windows/System32/spool/drivers/color/sRGB Color Space Profile.icm" }
CK (Test-Path $iccSrc) "⑨ 找到 sRGB ICC 源（$iccSrc）"
$mixed = "$out/icc_srgb_pq.png"; $noicc = "$out/noicc_pq.png"
Copy-Item $hdr $mixed -Force
Start-Process -FilePath $et -ArgumentList @('-overwrite_original', "-icc_profile<=$iccSrc", $mixed) `
    -NoNewWindow -Wait -RedirectStandardOutput "$out/_etm.out" -RedirectStandardError "$out/_etm.err" | Out-Null
Copy-Item $mixed $noicc -Force
Start-Process -FilePath $et -ArgumentList @('-overwrite_original', '-icc_profile=', $noicc) `
    -NoNewWindow -Wait -RedirectStandardOutput "$out/_etn.out" -RedirectStandardError "$out/_etn.err" | Out-Null
# 前提断言（第三方）：混合件必须"cICP 仍 PQ + ICC 仍在"；两个对照必须"cICP 仍 PQ + 无 ICC"
$mTrc = FfprobeTrc $mixed
CK ($mTrc -eq 'smpte2084') "⑨ 前提：混合件 ffprobe 仍报 color_transfer=smpte2084（cICP 未被删，实得「$mTrc」）"
CK ((ExifTag $mixed 'ICC_Profile:ProfileDescription') -ne "") "⑨ 前提：混合件确有内嵌 ICC（exiftool 读到描述）"
CK ((ExifTag $hdr 'ICC_Profile:ProfileDescription') -eq "") "⑨ 前提：源 src_hdr.png 本无 ICC（判别式：只差 ICC 一项）"
CK ((ExifTag $noicc 'ICC_Profile:ProfileDescription') -eq "") "⑨ 前提：去 ICC 对照件确无 ICC"
# 真实行为：读**产物**的 XMP/ISO 增益区间（headroom=1 ⇒ max=0.00；HDR ⇒ 2.30）
$m1 = RunCli 'icc_mixed' $mixed 'jpg' @('--jpeg-gain-map', 'true')
$n1 = RunCli 'icc_noicc' $noicc 'jpg' @('--jpeg-gain-map', 'true')
CK ($m1.code -eq 0 -and $m1.file) "⑨ 混合件（cICP=PQ + ICC=sRGB）正常产出（实得 code=$($m1.code)）"
CK ($n1.code -eq 0 -and $n1.file) "⑨ 去 ICC 对照件正常产出（实得 code=$($n1.code)）"
if ($m1.file) {
    # ⚠ 2026-10-20：默认档改成 iso ⇒ 这里**不再读 XMP 标签**（原来读 `XMP-hdrgm:GainMapMax`，
    #   在默认档下两侧都会变成"空"，那条"SDR ⇒ 标签为空"的判据会退化成恒真）。
    #   改用**自家探针的 ISO 读数**当同一件事的读数，并用下一条的 2.3 当它的对照臂 ⇒ 判别力不变。
    $p9m = ProbeOut 'gainmap' $m1.file.FullName "$out/sec9m.jpg"
    $mmIso = ''
    if ($p9m -match 'iso min=([^\r\n]*)') { $mmIso = $Matches[1].Trim() }
    $mmMax = ''
    if ($mmIso -match 'max=([-0-9.]+)') { $mmMax = $Matches[1] }
    CK ($mmMax -eq '') "⑨ 实测：混合件走 **SDR** 路 ⇒ 产物里没有 ISO 增益区间（实得「$mmIso」）⇒ ICC 判据压过 cICP/ffprobe"
    CK ($m1.log -match '输入为 SDR') "⑨ 实测：日志**点名**「输入为 SDR…已按普通 JPEG 输出」（可用性诚实，非静默降级）"
} else { CK $false "⑨ 混合件无产物" }
if ($n1.file) {
    $p9n = ProbeOut 'gainmap' $n1.file.FullName "$out/sec9n.jpg"
    $nnMax = ''
    if ($p9n -match 'iso min=[^\r\n]*max=([-0-9.]+)') { $nnMax = $Matches[1] }
    CK ($nnMax -eq '2.3') "⑨ 对照：去 ICC 后走 **HDR** 路（ISO 声明 max=$nnMax，上一条因此不是恒真）⇒ 唯一变量 ICC 决定了分类"
} else { CK $false "⑨ 对照件无产物" }
if ($m1.file -and $n1.file) {
    $mIso = ProbeOut 'gainmap' $m1.file.FullName "$out/sec9.jpg"
    # ⚠ 2026-09-20 裁定（**SDR ⇒ 普通 JPEG**）：SDR 路**不写增益图** ⇒ 既无 ISO 增益区间可读、顶层也只有 1 幅图。
    #   原两条断言（`ISO max ≈ 0` + `images=2`）已被该裁定**推翻**。
    #   ⚠ 判据**不弱化**：仍断言「SDR 路 ⇒ **不是** Ultra HDR 容器」+「必须点名」，条数不变。
    CK ($mIso -match 'images=1') "⑨ 混合件是**普通 JPEG**（顶层 1 幅图；SDR 路不写增益图 ⇒ 无 ISO 增益区间）"
    CK ($m1.log -match '输入为 SDR') "⑨ 诚实告知：日志点名「输入为 SDR…」（非静默降级）"
} else { CK $false "⑨ 缺产物，容器断言无法进行" }

Write-Host "`n### 10) --jpeg-progressive 三态 × GainMap 双路径（2026-09-19 补，R3/R7 结构锁） ###" -ForegroundColor Cyan
# 为什么需要本段：`--jpeg-progressive` × GainMap 此前**没有任何锁**，而它同时踩过两个坑：
#   · R3 假拦截：判据写成 `!= 0` 会把「自动(-1)」当成「要渐进」⇒ 误拦引擎 + 打错误文案；
#   · R7 静默丢弃：GainMap 的底图/增益图**恒由 cjpegli 编码**（cjpegli **支持** `-p 0..2`），
#     但两条 GainMap 路径曾经都**不读**该选项 ⇒ 用户设的「基线」被静默忽略（恒渐进）。
# 本段锁三件事：① 三态语义（0=顺序 / 1=渐进 / 不传=默认基线）；② 两条路径都消费**单一真值**
#   `GainMapJpegCodec.ProgressiveLevelOf`；③ 对 GainMap 的**放行没有被扩大成恒放**。
# ⚠ 靶子必须**两条**，因为它们是两条不同的管线：
#   · 静态 png ⇒ 引擎 GainMap 出口（`RawColorPipeline`）；
#   · **动画输入** ⇒ GainMap 专用管线（`QueueProcessor.ProcessGainMapJpegAsync`）。
#   ⚠ `--color-engine legacy` **打不到**专用管线 —— `ColorEngineRouter.Requested()` 对
#     `JpegGainMap` 恒真（GainMap 不受 legacy 开关管辖）⇒ 静态 png + legacy 只会走引擎，
#     此时断言「专用管线」会**恒假**（实测踩过）。
#   动画素材**现场自造**（3 帧 lavfi testsrc2）⇒ 不依赖仓外素材、不因素材缺失假红。
# 第三方判据：渐进与否读 **JPEG SOF 标记**（SOF2=渐进 / SOF0、SOF1=顺序），不看产品自述。
function SofMarker([string]$path) {
    if (-not (Test-Path $path)) { return "-" }
    $b = [System.IO.File]::ReadAllBytes($path)
    $i = 2
    while ($i -lt ($b.Length - 3)) {
        if ($b[$i] -ne 0xFF) { $i++; continue }
        $t = $b[$i + 1]
        if ($t -eq 0xD8 -or $t -eq 0x01 -or ($t -ge 0xD0 -and $t -le 0xD7)) { $i += 2; continue }
        if ($t -eq 0xDA) { return "noSOF" }
        $len = ($b[$i + 2] * 256) + $b[$i + 3]
        if ($t -ge 0xC0 -and $t -le 0xCF -and $t -ne 0xC4 -and $t -ne 0xC8 -and $t -ne 0xCC) { return ("SOF" + ($t - 0xC0)) }
        $i += 2 + $len
    }
    return "?"
}
function Md5Of([string]$path) {
    if (-not (Test-Path $path)) { return "-" }
    return (Get-FileHash -LiteralPath $path -Algorithm MD5).Hash
}
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
CK (Test-Path $ff) "⑩ 前提：ffmpeg 可用（用于现场自造动画素材）"
$anim = "$out/anim_src.gif"
Start-Process -FilePath $ff -ArgumentList @('-y','-hide_banner','-loglevel','error','-f','lavfi',
    '-i','testsrc2=size=64x48:rate=5:duration=0.6','-frames:v','3',$anim) `
    -NoNewWindow -Wait -RedirectStandardOutput "$out/_gif.out" -RedirectStandardError "$out/_gif.err" | Out-Null
$animOk = (Test-Path $anim) -and ((Get-Item $anim).Length -gt 0)
CK $animOk "⑩ 前提：现场自造 3 帧动画 GIF 成功（缺它本段会退化成只覆盖一条管线）"

# ── 路径 A：引擎 GainMap 出口（静态 png；不传 --color-engine ⇒ 默认即引擎） ──
$a0 = RunCli 'prog_eng_0' $sdr 'jpg' @('--jpeg-gain-map','true','--jpeg-progressive','0')
$a1 = RunCli 'prog_eng_1' $sdr 'jpg' @('--jpeg-gain-map','true','--jpeg-progressive','1')
$aN = RunCli 'prog_eng_n' $sdr 'jpg' @('--jpeg-gain-map','true')
CK ($a0.log -match '\[色彩引擎\] 决策：') "⑩A 前提：本格确实由**引擎出口**完成（有决策行；不是回退专用管线）"
CK ($a0.code -eq 0 -and $a0.file) "⑩A --jpeg-progressive 0 ⇒ 退出码 0 + 产物（实得 code=$($a0.code)）"
CK ($a1.code -eq 0 -and $a1.file) "⑩A --jpeg-progressive 1 ⇒ 退出码 0 + 产物（放行：GainMap 走 cjpegli，支持渐进；实得 code=$($a1.code)）"
CK ($a1.log -notmatch 'ProgressiveJpeg') "⑩A 1 时**不再**被 ProgressiveJpeg 拦截（放行生效）"
# ⚠ 实测缺口（2026-09-19）：只断言「1 有产物 + 产物渐进 + 0≠1」时，把判据改回
#   `if (o.JpegProgressiveId > 0)`（对 GainMap 也拦）**行为断言全绿** —— 因为拦截后
#   会回退到 GainMap 专用管线，而专用管线也已消费该选项（Phase 3 接线）⇒ 靠兜底蒙混。
#   故必须把「路径 A 的 1 真的落在**引擎侧**」也钉死：有决策行、且无专用管线回退。
CK ($a1.log -match '\[色彩引擎\] 决策：') "⑩A 1 仍由**引擎出口**完成（防：1 悄悄回退专用管线后本段靠兜底蒙混）"
CK ($a1.log -notmatch '本次由 GainMap 专用管线') "⑩A 1 **不得**回退到专用管线（放行必须落在引擎侧，不是靠专用管线兜底）"
CK ($a0.log -match 'JPEG progressive level -p 0') "⑩A 0 的日志点名 `-p 0`（单一真值被消费，不是静默丢弃）"
CK ($a1.log -match 'JPEG progressive level -p 2') "⑩A 1 的日志点名 `-p 2`（`1 => 2` 映射生效）"
if ($a1.file) {
    $a1sof = SofMarker $a1.file.FullName
    CK ($a1sof -eq 'SOF2') "⑩A 1 的产物**确实是渐进**（SOF 标记 SOF2，实得 $a1sof）—— 第三方判据"
} else { CK $false "⑩A 1 无产物，SOF 断言无法进行" }
if ($a0.file) {
    $a0sof = SofMarker $a0.file.FullName
    CK ($a0sof -ne 'SOF2') "⑩A 0 的产物**不是渐进**（实得 $a0sof；顺序 = SOF0/SOF1）"
} else { CK $false "⑩A 0 无产物，SOF 断言无法进行" }
if ($a0.file -and $a1.file) {
    CK ((Md5Of $a0.file.FullName) -ne (Md5Of $a1.file.FullName)) "⑩A 0 与 1 的产物**字节不同**（接线生效；相同 ⇒ 该选项被静默忽略）"
} else { CK $false "⑩A 缺产物，MD5 断言无法进行" }
if ($a0.file -and $aN.file) {
    CK ((Md5Of $a0.file.FullName) -eq (Md5Of $aN.file.FullName)) "⑩A 三态第三态：**不传 ≡ 0**（默认 0=基线，MD5 相同）"
} else { CK $false "⑩A 缺产物，默认态 MD5 断言无法进行" }

# ── 路径 B：GainMap 专用管线（动画输入 ⇒ 引擎不可走） ──
if ($animOk) {
    $b0 = RunCli 'prog_ded_0' $anim 'jpg' @('--jpeg-gain-map','true','--jpeg-progressive','0')
    $b1 = RunCli 'prog_ded_1' $anim 'jpg' @('--jpeg-gain-map','true','--jpeg-progressive','1')
    CK ($b0.log -match '专用管线') "⑩B 前提：动画输入确实走 **GainMap 专用管线**（静态 png 打不到这条）"
    CK ($b0.code -eq 0 -and $b0.file) "⑩B 0 ⇒ 退出码 0 + 产物（实得 code=$($b0.code)）"
    CK ($b1.code -eq 0 -and $b1.file) "⑩B 1 ⇒ 退出码 0 + 产物（实得 code=$($b1.code)）"
    CK ($b0.log -match 'JPEG progressive level -p 0') "⑩B 0 的日志点名 `-p 0`（专用管线调用点已接上单一真值）"
    CK ($b1.log -match 'JPEG progressive level -p 2') "⑩B 1 的日志点名 -p 2（专用管线把 1 映射成 2）"
    if ($b1.file) {
        $b1sof = SofMarker $b1.file.FullName
        CK ($b1sof -eq 'SOF2') "⑩B 1 的产物确实是渐进（实得 $b1sof）—— 第三方判据"
    } else { CK $false "⑩B 1 无产物，SOF 断言无法进行" }
    if ($b0.file) {
        $b0sof = SofMarker $b0.file.FullName
        CK ($b0sof -ne 'SOF2') "⑩B 0 的产物不是渐进（实得 $b0sof）"
    } else { CK $false "⑩B 0 无产物，SOF 断言无法进行" }
    if ($b0.file -and $b1.file) {
        CK ((Md5Of $b0.file.FullName) -ne (Md5Of $b1.file.FullName)) "⑩B 0 与 1 的产物字节不同 ⇒ **专用管线也接了线**（此前它吃默认值、恒渐进）"
    } else { CK $false "⑩B 缺产物，MD5 断言无法进行" }
} else {
    CK $false "⑩B 动画素材自造失败 ⇒ 专用管线路径**未被覆盖**（显式判红，不静默跳过）"
}

# ── 反例：放行**没有**被扩大成恒放（非 GainMap 的 jpg 仍须拦） ──
$np1 = RunCli 'prog_plain_1' $sdr 'jpg' @('--jpeg-progressive','1','--color-engine','engine')
CK ($np1.code -ne 0) "⑩ 负控：非 GainMap + 1 + 显式 engine ⇒ **仍硬失败**（实得 code=$($np1.code)）"
CK (-not $np1.file) "⑩ 负控：非 GainMap + 1 ⇒ **零产物**（放行只对 GainMap 生效）"
CK ($np1.log -match 'ProgressiveJpeg') "⑩ 负控：失败原因点名 `ProgressiveJpeg`（ffmpeg mjpeg 确实无渐进能力）"
$npN = RunCli 'prog_plain_n' $sdr 'jpg' @('--color-engine','engine')
CK ($npN.code -eq 0 -and $npN.file) "⑩ 负控的负控：同输入**不传**该选项 ⇒ 正常产出（排除「输入坏了」造成的假红）"

# ── 源码形态锁（结构锁：防判据被改回、防接线被删、防形参位置被挪） ──
$cer = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/ColorMapping/ColorEngineRouter.cs")
$gme = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/GainMapEncoder.cs")
$gjc = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/ColorMapping/GainMapJpegCodec.cs")
$qpc = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/QueueProcessor.cs")
CK ($cer -match 'bool gainMapJpeg = o\.JpegGainMap && \(fmt == "jpg" \|\| fmt == "jpeg"\)') "⑩ 源码锁：GainMap 白名单定义在（JpegGainMap + fmt 只认 jpg/jpeg）"
CK ($cer -match 'if \(o\.JpegProgressiveId > 0 && !gainMapJpeg\)') "⑩ 源码锁：拦截判据含 !gainMapJpeg（防被改回对 GainMap 恒拦）"
CK ($cer -notmatch 'if \(o\.JpegProgressiveId > 0\)\s*\r?\n\s*items\.Add') "⑩ 源码锁：**不得**存在不带 !gainMapJpeg 的裸判据（改回旧形态即红）"
CK ($gjc -match '=> o\.JpegProgressiveId switch \{ 1 => 2, 0 => 0, _ => -1 \};') "⑩ 源码锁：ProgressiveLevelOf 仍是唯一映射 1=>2 / 0=>0 / 其它=>-1"
# ⚠ 锚演进（2026-10-02 重钉）：`GainMapEncoder.EncodeAsync` 末尾新增了 `container = "jpeg"`
#   （AVIF 增益图分流，`docs/AVIF_GAINMAP_ENCODER_2026-10-02.md §1`）⇒ `jpegProgressiveLevel` 不再是末位形参。
#   这条锁**真正防的**是"有人往 log/ct 前面插一个参数 ⇒ 主路那句位置实参 `s => …, ct` 静默错绑"。
#   ⇒ 判据改为钉 log/ct 的**相对位置**（紧跟 sourcePrimaries 之后、任何后续可选参数之前），
#     并把"接线不许删"从"必须以 ) 收尾"改成**具名传参**本身（两条调用路各钉一条）。
CK ($gme -match 'string\? sourcePrimaries = null,\s+Action<string>\? log = null, CancellationToken ct = default,') "⑩ 源码锁：log/ct 仍紧跟 sourcePrimaries 之后（位置实参不错绑的前提；在其后追加 container 是合法的）"
CK ($qpc -match 'ColorMapping\.GainMapJpegCodec\.ProgressiveLevelOf\(item\.Options\)') "⑩ 源码锁：专用管线调用点复用单一真值（不另写映射）"
CK ($qpc -match 'jpegProgressiveLevel: gmProgLv,') "⑩ 源码锁：主路调用点具名传入 progressive 档（防接线被删）"
# ⚠ 锚演进（2026-10-15 T12/R3 重钉）：主路调用点在 `container:` 之后又追加了两个**具名**可选实参
#   （`sourceBaseRaw:` / `inputIsSdrWhiteUnit:`，配对重封装与单位约定），所以这里**不再要求 `)` 紧跟其后**
#   —— 本锁真正防的是「就地写死 "jpeg"」，不是「container 必须是最后一个实参」（与 :543 的判据同源）。
CK ($qpc -match 'container: GainMapEncoder\.ContainerOf\(outputPath\)') "⑩ 源码锁：主路容器由单一真值决定（不许就地写死 \"jpeg\"）"

CK (([System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs")) -match 'jpegProgressiveLevel: gmProgLv,') "⑩ 源码锁：引擎路调用点同样具名传入 progressive 档"

# ══ ⑪ §38 锁：GainMap + `--encoder cjpegli` ⇒ 底图 ICC 不得被**源** ICC 覆盖 ════════════
Write-Host "`n### 11) §38 GainMap + cjpegli：底图 ICC 必须与引擎一致、且不得等于源 ICC ###" -ForegroundColor Cyan
# 缺陷（`docs/HANDOVER.md §38`，轴 C 报、09-22 首次实机复现）：`encoderProtectsColor` 原先不含
#   Cjpegli  ⇒ GainMap 变体下 `protectColorMetadata=false` ⇒ `RestoreMetadataAsync` 走
#   「完整元数据复制（含 ICC）」，把**源**色域的 ICC 盖到底图上（实测修复前底图 ICC 与源 ICC
#   **逐字节相同** = `D6881218…`）。而 GainMap 的底图 ICC 由 `GainMapEncoder` 自己写
#   （对齐 libultrahdr：底图是什么色域就附什么 ICC）⇒ 复制回来的源 ICC 是**错标**。
# 判据（以**引擎路径为参照**，因为 §38.4 实测二者修复后一致；三条互相制衡，任何一条空跑都会被另两条抓住）：
#   a) 引擎底图 ICC ≠ 源 ICC       —— 反空跑对照（a 不成立则 b/c 恒真 = 真空绿）
#   b) cjpegli 底图 ICC == 引擎底图 ICC —— 换后端**不得**换色彩宣称
#   c) cjpegli 底图 ICC ≠ 源 ICC       —— 正是修复前逐字节相同的那一条
#   d) 第三方色度读数交叉核对（exiftool 读红原色），不依赖任何自研解析器
# 素材**自备**：P3 ICC 由 ffmpeg `iccgen` 现造（口径同 `_probe-jpg-p3-icc.ps1` 的 zscale 链），
#   再内嵌进本门禁已有的 `$hdr`（PQ/bt2020、**本无 ICC**）⇒ 与引擎臂的唯一变量就是"源带 P3 ICC"。
#   ⚠ 三个 ICC 一律**从文件里抽出来再比**（PNG 的 iCCP / JPEG 的 APP2 存储形态不同），
#     拿"嵌入前的原始 .icc 字节"去比"产物抽出来的字节"会因容器封装差异而假红。
$ffG = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
# ⚠ `-w` 的参数是**文件名模板**（`%f` = 源文件不带扩展名的名字）：实测给它一个**字面文件名**
#   （如 `$out/x.icc`）时 exiftool **不落盘**、也不报错 ⇒ 静默抽不到 ICC。
function ExtractIcc([string]$src, [string]$tag) {
    Get-ChildItem "$out/s38icc_${tag}_*.icc" -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
    Start-Process -FilePath $et -ArgumentList @('-b', '-ICC_Profile', '-w', "$out/s38icc_${tag}_%f.icc", "`"$src`"") `
        -NoNewWindow -Wait -RedirectStandardOutput "$out/_iccd.out" -RedirectStandardError "$out/_iccd.err" | Out-Null
    $f = Get-ChildItem "$out/s38icc_${tag}_*.icc" -EA SilentlyContinue | Select-Object -First 1
    # ⚠ 必须**非空文件**：`Get-FileHash` 对 0 字节文件也返回一个固定哈希 ⇒ "抽到了"与"抽到空壳"
    #   在哈希层面无法区分 ⇒ ⑪b 的 `==` 会被两个空壳撑成恒等（假绿）。
    if ($null -ne $f -and $f.Length -gt 0) { return $f.FullName }
    return ""
}
function IccDump([string]$src, [string]$tag) {
    $p = ExtractIcc $src $tag
    if ($p -eq "") { return "" }
    return (Get-FileHash $p -Algorithm SHA256).Hash
}
$p3Jpg = "$out/s38_p3src.jpg"
if (Test-Path $p3Jpg) { Remove-Item $p3Jpg -Force }
# ⚠ ArgumentList 必须是**单个已拼好的字符串**：`-ArgumentList 'a' + $b` 会被 PowerShell 当成
#   位置参数（实测报 `A positional parameter cannot be found that accepts argument '+'`）。
$p3Args = '-y -hide_banner -loglevel error -f lavfi -i "testsrc2=size=64x48:duration=0.1" ' +
          '-frames:v 1 -vf "format=rgb48le,zscale=pin=bt709:tin=linear:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709,iccgen" "' + $p3Jpg + '"'
$gp3 = Start-Process -FilePath $ffG -ArgumentList $p3Args -NoNewWindow -Wait -PassThru
CK (($gp3.ExitCode -eq 0) -and (Test-Path $p3Jpg)) "⑪ 素材：ffmpeg iccgen 造出带 ICC 的 P3 载体（exit=$($gp3.ExitCode)）"
$p3IccFile = ExtractIcc $p3Jpg 'emb'
CK ($p3IccFile -ne "") "⑪ 素材：P3 ICC 已从 iccgen 载体里抽出（$p3IccFile）"
$srcP3 = "$out/s38_src_p3.png"
Copy-Item $hdr $srcP3 -Force
Start-Process -FilePath $et -ArgumentList @('-overwrite_original', "-icc_profile<=$p3IccFile", "`"$srcP3`"") `
    -NoNewWindow -Wait -RedirectStandardOutput "$out/_icce.out" -RedirectStandardError "$out/_icce.err" | Out-Null
$srcRed = ExifTag $srcP3 'ICC_Profile:ChromaticityChannel1'
# ⚠ 判据取 `^0\.6[78]` 而非 `^0\.68`：iccgen 写出的 P3 红原色实测是 **0.67999**（规范值 0.680 的
#   定点舍入），而 sRGB=0.64、BT.2020=0.708 都落在该区间外 ⇒ 既有牙又不会被舍入翻红。
CK ($srcRed -match '^0\.6[78]') "⑪ 前提：源已带 **P3** ICC（exiftool 红原色 x∈[0.67,0.69)，实得「$srcRed」）"
$srcIcc = IccDump $srcP3 'src'
CK ($srcIcc -ne "") "⑪ 前提：源 ICC 可被抽出（防空对照：抽不到则下面三条全无意义）"

$e38 = RunCli 's38_engine' $srcP3 'jpg' @('--jpeg-gain-map', 'true')
$c38 = RunCli 's38_cjli'  $srcP3 'jpg' @('--jpeg-gain-map', 'true', '--encoder', 'cjpegli')
CK ($e38.code -eq 0 -and $e38.file) "⑪ 引擎臂产出底图（exit=$($e38.code)）"
CK ($c38.code -eq 0 -and $c38.file) "⑪ cjpegli 臂产出底图（exit=$($c38.code)）"
$eIcc = ""; $cIcc = ""
if ($e38.file) { $eIcc = IccDump $e38.file.FullName 'engine' }
if ($c38.file) { $cIcc = IccDump $c38.file.FullName 'cjli' }
$eRed = ""; $cRed = ""
if ($e38.file) { $eRed = ExifTag $e38.file.FullName 'ICC_Profile:ChromaticityChannel1' }
if ($c38.file) { $cRed = ExifTag $c38.file.FullName 'ICC_Profile:ChromaticityChannel1' }
#   ⚠ **源 ICC 的哈希每轮都会变**：实测同一滤镜链两次生成的 P3 ICC **长度相同(584B)、只有第 35 字节不同**
#     ⇒ 那是 ICC 头 date/time 字段里的秒 ⇒ ffmpeg `iccgen` 把生成时刻写进了配置文件头。
#     所以源 ICC 只能当**不等式判据的一侧**，**不许**被当成可 pin 的指纹。
#     引擎/cjpegli 两个**底图** ICC 实测跨轮稳定（`75FF19E1029473FE`，与 `docs/HANDOVER.md §38.1` 逐字同值）
#     ⇒ ⑪b 的等号判据成立（两条臂在同一轮内比较）。
Write-Host ("  [§38 读数] 源 ICC=" + $srcIcc.Substring(0, [Math]::Min(16, $srcIcc.Length)) +
            "  引擎底图=" + $eIcc.Substring(0, [Math]::Min(16, $eIcc.Length)) +
            "  cjpegli 底图=" + $cIcc.Substring(0, [Math]::Min(16, $cIcc.Length)))
CK (($eIcc -ne "") -and ($cIcc -ne "")) "⑪ 两条臂的底图 ICC 都抽到了（非空，否则等号是真空成立）"
CK ($eIcc -ne $srcIcc) "⑪a 对照：引擎底图 ICC ≠ 源 ICC（实得引擎 $eIcc / 源 $srcIcc）⇒ 下面两条有判别力"
CK (($cIcc -ne "") -and ($cIcc -eq $eIcc)) "⑪b 锁：cjpegli 底图 ICC == 引擎底图 ICC ⇒ 换后端不得换色彩宣称"
CK (($cIcc -ne "") -and ($cIcc -ne $srcIcc)) "⑪c 锁：cjpegli 底图 ICC ≠ 源 ICC（修复前正是逐字节等于源 ICC = 错标）"
CK (($cRed -ne "") -and ($srcRed -ne "") -and ($cRed -ne $srcRed)) "⑪d 第三方色度交叉核对：cjpegli 底图红原色（$cRed）≠ 源（$srcRed）"
$qpcText = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/QueueProcessor.cs")
# ⚠⚠ 源码锁必须**锚定到初始化式内部**：全文无锚的 `–match` 只要那串出现在**任何**位置就绿 ⇒
#   ①整行注释掉仍绿（注释里也含该串）②挪进死变量/别的分支也绿。⇒ 三条分开咬。
$ecInit = [regex]::Match($qpcText, '(?s)var encoderProtectsColor\s*=[\s\S]{0,400}?;')
CK $ecInit.Success "⑪ 源码锁A：encoderProtectsColor 的初始化式仍在本文件（形态大变 ⇒ 先红再核，不静默）"
CK ($ecInit.Value -match 'Cjpegli && item\.Options\.JpegGainMap') `
   "⑪ 源码锁B：GainMap 变体例外确实在 encoderProtectsColor 初始化式「内」（挪到别处/删掉即红）"
$commentedOut = @($qpcText -split "`r?`n" | Where-Object { $_ -match 'Cjpegli\s*&&\s*item\.Options\.JpegGainMap' -and $_.Trim().StartsWith('//') })
CK ($commentedOut.Count -eq 0) "⑪ 源码锁C：该例外不得以整行注释形态存在（注释掉即红，实得 $($commentedOut.Count) 行）"

Write-Host "`n### 12) 输入侧：Apple 两代 Ultra HDR JPEG（第三方样本；像素级施加证据）###" -ForegroundColor Cyan
# 为什么这一段必须存在：
#   · Apple 用私有 XMP 命名空间（`HDRGainMap:` / `apdi:`），exiftool **不解析**它 ⇒ 纯托管兜底
#     `TryReadAppleXmpMetadata` 是唯一读通路；若它坏了，样本会被静默当 SDR。
#   · 产品日志里的「峰值 N nits」是 `203 × 2^HDRCapacityMax`，由**元数据**算出，不是像素读数。
#     §3c/本段的「施加后峰值 > 底图峰值」必须由**像素**证明 ⇒ `gainmap-stats` 读回解码器写出的线性像素。
#   · 两代样本的 headroom 来源不同（new：XMP 显式 4.532783；old：v1.0 无字段 ⇒ 隐式 8×）
#     ⇒ 两臂的最大线性值必须分叉；两臂同值就说明参数根本没进增益公式。
#   · 负控用 `exiftool -all=` 剥掉**全部**元数据（实测 `-MPF:all= -XMP:all=` 只去掉一根 APP2，
#     Apple 的 XMP 仍在 ⇒ 负控会假绿，2026-10-20 实测）。
$appleNew = "$root/tools/src/libultrahdr/tests/data/apple_gainmap_new.jpg"
$appleOld = "$root/tools/src/libultrahdr/tests/data/apple_gainmap_old.jpg"
CK ((Test-Path $appleNew) -and (Test-Path $appleOld)) `
   "⑫ 前提：Apple 两代样本在位（libultrahdr tests/data）—— 缺则本段无凭据（fail-closed，不静默跳过）"
$newRun = RunCli "apple_new" $appleNew "png" @()
$oldRun = RunCli "apple_old" $appleOld "png" @()
$newOut = (($null -ne $newRun.file) -and (Test-Path $newRun.file))
$newHit = PatHit @('检测=检测到 Ultra HDR JPEG', 'XMP源=元数据来源: Apple HDRGainMap XMP',
                   'headroom=headroom=4\.532783', 'PFM=已解码为线性 HDR PFM（峰值 4699 nits）') $newRun.log
CK ($newOut -and $newHit.ok) ("⑫ new：识别 + 元数据非空（显式 headroom=4.532783）+ 走完解码并产出" +
      "  逐条命中: 出件=" + $newOut + ' ' + $newHit.detail)
$oldOut = (($null -ne $oldRun.file) -and (Test-Path $oldRun.file))
$oldHit = PatHit @('检测=检测到 Ultra HDR JPEG', 'XMP源=元数据来源: Apple HDRGainMap XMP',
                   '隐式8x=隐式 8× 上限', 'PFM=已解码为线性 HDR PFM（峰值 1624 nits）') $oldRun.log
CK ($oldOut -and $oldHit.ok) ("⑫ old：v1.0 无 headroom 字段 ⇒ 按隐式 8× 处理，仍走完解码并产出" +
      "  逐条命中: 出件=" + $oldOut + ' ' + $oldHit.detail)
$stNew = ProbeStats $appleNew ""
$stOld = ProbeStats $appleOld ""
Write-Output ($stNew -split "`r?`n" | Where-Object { $_ -match '^stats ' })
Write-Output ($stOld -split "`r?`n" | Where-Object { $_ -match '^stats ' })
$mxn = 0.0; $mxo = 0.0
$mm = [regex]::Match($stNew, 'maxLinear=([0-9.]+)')
if ($mm.Success) { $mxn = [double]$mm.Groups[1].Value }
$mm = [regex]::Match($stOld, 'maxLinear=([0-9.]+)')
if ($mm.Success) { $mxo = [double]$mm.Groups[1].Value }
CK (($stNew -match 'PROBE RESULT: pass=4 fail=0') -and ($mxn -gt 1.0) -and ($stNew -match 'over1=[1-9][0-9]*')) `
   "⑫ new 像素级：maxLinear=$mxn > 1.0（SDR 白点）且确有像素越白点 ⇒ 增益真被施加（不是只读了元数据）"
CK (($stOld -match 'PROBE RESULT: pass=4 fail=0') -and ($mxo -gt 1.0) -and ($stOld -match 'over1=[1-9][0-9]*')) `
   "⑫ old 像素级：maxLinear=$mxo > 1.0 ⇒ 隐式 8× 参数真的进了公式"
CK (($mxn -gt 0.0) -and ($mxo -gt 0.0) -and ($mxn -gt ($mxo * 1.5))) `
   "⑫ 剂量分叉：new（显式 headroom，实测 $mxn）必须明显亮于 old（隐式 8×，实测 $mxo）—— 两臂同值说明参数被忽略"
# 负控：剥掉全部元数据（含 Apple XMP 与 MPF）⇒ 同一底图不得再走增益图路径。
# ⚠ `-all=` 会连 ICC 一起剥掉；本段只判「不走增益图路径」，不做色彩断言。
$strippedNew = "$out/apple_new_stripped.jpg"
$strippedOld = "$out/apple_old_stripped.jpg"
Start-Process -FilePath $et -ArgumentList @('-all=','-o',$strippedNew,$appleNew) -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput "$out/_ets1.out" -RedirectStandardError "$out/_ets1.err" | Out-Null
Start-Process -FilePath $et -ArgumentList @('-all=','-o',$strippedOld,$appleOld) -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput "$out/_ets2.out" -RedirectStandardError "$out/_ets2.err" | Out-Null
$negProd = RunCli "apple_new_stripped" $strippedNew "png" @()
$negNew = ProbeStats $strippedNew "none"
$negOld = ProbeStats $strippedOld "none"
CK (($null -ne $negProd.file) -and (Test-Path $negProd.file) -and ($negProd.log -notmatch '检测到 Ultra HDR JPEG') -and ($negProd.log -notmatch 'Apple HDRGainMap')) `
   "⑫ **负控**：同一底图剥掉全部元数据（exiftool -all=）⇒ 产品不走增益图路径，但产物照出（证明正控的日志来自增益层）"
CK ($negNew -match 'PROBE RESULT: pass=1 fail=0') "⑫ **负控**：剥元数据后的 new 底图 ⇒ 解码器返回 null（pass=1 fail=0）"
CK ($negOld -match 'PROBE RESULT: pass=1 fail=0') "⑫ **负控**：剥元数据后的 old 底图 ⇒ 解码器返回 null（pass=1 fail=0）"

Write-Host "`n### 13) 输入侧：第三方 ISO 21496-1 APP2 JPEG（第三方产量实件；纯托管解析 + 像素级施加 + 负 min + 负控）###" -ForegroundColor Cyan
# 为什么这一段必须存在：
#   · §12 的 Apple 两代样本走**私有 XMP 命名空间**（HDRGainMap: / apdi:）⇒ 兜底解析器。ISO 21496-1 APP2
#     那条通路（APP2 识别 → ParseIso21496 → MPF 副图定位）此前只有**自造夹具**能触发，而夹具与产品
#     共用同一套假设 ⇒ 双向自证。本段换成第三方编码器/相机产出的真件，口径全部来自**外部字节**。
#   · 两件真件的形态都在合成件里见不到：底图 CICP = smpte432(P3)、增益图分辨率 = 底图 1/2（1368x912 vs
#     2736x1824 / 1500x1000 vs 3000x2000）、APP2 载荷带 flags=0x40、capMax 与底图 headroom 不同。
#   · DSC01058 的 min = -0.0049 是**首次**出现的负 min（合成夹具全是 0）⇒ 覆盖「负 min 不得被截成 0」。
#   · 负控是 xmp-only 变体（同一站点另一仓库，非 ISO 容器）：产品必须走 hdrgm XMP 分支，**不得**声称
#     `元数据来源: ISO 21496-1 APP2` —— 否则上面那些 iso 读数就是「恒真日志」，整段无判别力。
#
# ⚠ 语料落点：`tests/output/corpus/gainmap-iso21496/`（**工作区内**，`output/` 整树 gitignore）。
#   只读取；可用 `$env:GAINMAP_CORPUS_DIR` 指向别处的**只读**副本。**不得**写到工作区外（全局规范 §W）。
#   来源 / 许可 / 不入仓理由：
#     DSC00037.jpg, DSC01058.jpg  https://raw.githubusercontent.com/1096bimu/Dark-Melon/main/docs/samples/   GPL-3.0-only
#     seine_sdr_gainmap_srgb.jpg  https://raw.githubusercontent.com/AOMediaCodec/libavif/main/tests/data/      BSD-2-Clause
#   再分发许可未逐一确认 ⇒ 不入仓；靠 sha256 前 16 认同一份字节（同名不同字节 = 读数漂移，单列一条红）。
$corpus = "$root/tests/output/corpus/gainmap-iso21496"
if ($env:GAINMAP_CORPUS_DIR) { $corpus = $env:GAINMAP_CORPUS_DIR }
$gm37  = "$corpus/DSC00037.jpg"
$gm58  = "$corpus/DSC01058.jpg"
$gmNeg = "$corpus/seine_sdr_gainmap_srgb.jpg"
$cWant = @(
    @{ p = $gm37;  s = '34504aa6d6db6e6f' },
    @{ p = $gm58;  s = '95419a66144f1a65' },
    @{ p = $gmNeg; s = '1aaf0a1afa91a485' })
$cMissing = @(); $cBad = @(); $cRecon = @()
foreach ($need in $cWant) {
    $nm = Split-Path $need.p -Leaf
    if (-not (Test-Path $need.p)) { $cMissing += $nm; $cRecon += ($nm + '=MISSING'); continue }
    $h = (Get-FileHash -Algorithm SHA256 -Path $need.p).Hash.ToLower()
    $okSha = ($h.Substring(0, 16) -eq $need.s)
    if (-not $okSha) { $cBad += $nm }
    $cRecon += ($nm + '=' + $h.Substring(0, 16) + $(if ($okSha) { '/OK' } else { '/MISMATCH(期望 ' + $need.s + ')' }))
}
Write-Output ("  语料根=" + $corpus)
Write-Output ("  sha16 对账=" + ($cRecon -join '  '))
CK ($cMissing.Count -eq 0) ("⑬ 前提：三件第三方语料在位（fail-closed，不许静默 SKIP；缺=" + ($cMissing -join ',') + "）")
CK ($cBad.Count -eq 0) ("⑬ 前提：在位件字节与登记一致（同名不同字节 ⇒ 下面所有读数失效；不符=" + ($cBad -join ',') + "）")
if ($cMissing.Count -gt 0) {
    Write-Output "  ⚠ 取法（**落在工作区内**，禁止写到工作区外）："
    Write-Output "    Invoke-WebRequest -Uri https://raw.githubusercontent.com/1096bimu/Dark-Melon/main/docs/samples/DSC00037.jpg -OutFile `"$gm37`"    # sha256[:16]=34504aa6d6db6e6f"
    Write-Output "    Invoke-WebRequest -Uri https://raw.githubusercontent.com/1096bimu/Dark-Melon/main/docs/samples/DSC01058.jpg -OutFile `"$gm58`"    # sha256[:16]=95419a66144f1a65"
    Write-Output "    Invoke-WebRequest -Uri https://raw.githubusercontent.com/AOMediaCodec/libavif/main/tests/data/seine_sdr_gainmap_srgb.jpg -OutFile `"$gmNeg`"    # sha256[:16]=1aaf0a1afa91a485"
    Write-Output "    ⚠ 本机 curl.exe -C - 对 raw.githubusercontent.com 报 exit 56/23 且落 0 字节 ⇒ 用 Invoke-WebRequest（2026-11-03 实测）"
}
# 前提不成立就不跑臂：缺件时跑出来的是一堆「读不到文件」的红，会把真正的红盖住；前提红已经让本门禁转红。
if (($cMissing.Count -eq 0) -and ($cBad.Count -eq 0)) {
    # ── 正控 A：DSC00037（min=0 的常规件）────────────────────────────────────────
    $r37 = RunCli 'iso37' $gm37 'png' @()
    $o37 = (($null -ne $r37.file) -and (Test-Path $r37.file))
    $h37 = PatHit @('检测=检测到 Ultra HDR JPEG', 'ISO源=元数据来源: ISO 21496-1 APP2 \(纯托管\)',
                    'min=min=0\.000 max=4\.140 ', 'ch=Channels=1 ', '切割=纯托管容器切割 \(216059 B\)',
                    '峰值=峰值 880nits', '底图=底图原色=smpte432') $r37.log
    CK ($o37 -and $h37.ok) ("⑬a DSC00037：识别为 Ultra HDR + 走纯托管 ISO APP2 通路 + 元数据/切割字节/峰值/P3 底图逐项命中" +
        "  逐条命中: 出件=" + $o37 + ' ' + $h37.detail)
    $p37 = ProbeOut 'gainmap' $gm37 ''
    $p37ok = (($p37 -match 'images=2') -and ($p37 -match 'iso min=0 max=4\.1402') -and
              ($p37 -match 'image\[1\] start=705176 len=216059 1368x912') -and
              ($p37 -match 'PROBE RESULT: pass=6 fail=0'))
    CK $p37ok ("⑬b DSC00037 容器结构（探针直读字节）：2 幅图 + ISO 增益区间 0..4.1402 + 副图 1368x912（= 底图 1/2）+ " +
        "副图定位命中 SOI；pass=6 fail=0 ⇐ 实得 [" + ([regex]::Match($p37, 'PROBE RESULT: [^\r\n]*').Value.Trim()) + "]")
    $s37 = ProbeStats $gm37 ''
    $m37 = 0.0; $cap37 = 0.0
    $mm = [regex]::Match($s37, 'maxLinear=([0-9.]+)')
    if ($mm.Success) { $m37 = [double]$mm.Groups[1].Value }
    $mm = [regex]::Match($s37, 'capLinear=([0-9.]+)')
    if ($mm.Success) { $cap37 = [double]$mm.Groups[1].Value }
    CK (($s37 -match 'PROBE RESULT: pass=4 fail=0') -and ($m37 -gt 1.0) -and ($s37 -match 'over1=[1-9][0-9]*')) `
       "⑬c DSC00037 像素级：maxLinear=$m37 > 1.0（SDR 白点）且确有像素越白点 ⇒ 增益真被施加（不是只读了元数据）"
    CK (($cap37 -gt 0.0) -and ($m37 -le $cap37)) `
       "⑬c' DSC00037 自洽：像素峰 $m37 不得超过元数据上限 $cap37（越上限 = 公式或通道乘法错）"
    CK ($s37 -match 'peakNits=880') "⑬c'' DSC00037 交叉：像素统计的 peakNits=880 与产品日志「峰值 880nits」同源"

    # ── 正控 B：DSC01058（负 min；首个非零 offset 的现实形态）──────────────────────
    $r58 = RunCli 'iso58' $gm58 'png' @()
    $o58 = (($null -ne $r58.file) -and (Test-Path $r58.file))
    $h58 = PatHit @('检测=检测到 Ultra HDR JPEG', 'ISO源=元数据来源: ISO 21496-1 APP2 \(纯托管\)',
                    '负min=min=-0\.005 max=3\.362 ', '切割=纯托管容器切割 \(333451 B\)',
                    '峰值=峰值 2087nits', '底图=底图原色=smpte432') $r58.log
    CK ($o58 -and $h58.ok) ("⑬d DSC01058：min 为负（-0.005）的件仍走通 ISO APP2 通路并产出" +
        "  逐条命中: 出件=" + $o58 + ' ' + $h58.detail)
    $p58 = ProbeOut 'gainmap' $gm58 ''
    $p58ok = (($p58 -match 'iso min=-0\.0049') -and ($p58 -match 'image\[1\] start=508887 len=333451 1500x1000') -and
              ($p58 -match 'PROBE RESULT: pass=6 fail=0'))
    CK $p58ok ("⑬e DSC01058 容器结构：探针读到负 min=-0.0049（**首次**在第三方真件上覆盖负 min）+ 副图 1500x1000；" +
        "pass=6 fail=0 ⇐ 实得 [" + ([regex]::Match($p58, 'PROBE RESULT: [^\r\n]*').Value.Trim()) + "]")
    $s58 = ProbeStats $gm58 ''
    $m58 = 0.0; $cap58 = 0.0
    $mm = [regex]::Match($s58, 'maxLinear=([0-9.]+)')
    if ($mm.Success) { $m58 = [double]$mm.Groups[1].Value }
    $mm = [regex]::Match($s58, 'capLinear=([0-9.]+)')
    if ($mm.Success) { $cap58 = [double]$mm.Groups[1].Value }
    CK (($s58 -match 'PROBE RESULT: pass=4 fail=0') -and ($m58 -gt 1.0) -and ($s58 -match 'over1=[1-9][0-9]*') -and
        ($cap58 -gt 0.0) -and ($m58 -le $cap58)) `
       "⑬f DSC01058 像素级：maxLinear=$m58 > 1.0 且 ≤ capLinear=$cap58 ⇒ 负 min 未把增益打折成 0"
    CK (($m58 -gt $m37) -and ($m58 -gt ($m37 * 1.5)) -and ($cap58 -gt $cap37)) `
       "⑬g 剂量分叉：58 的 headroom 明显大于 37（maxLinear $m58 vs $m37；capLinear $cap58 vs $cap37）⇒ 两件的参数各自进了公式"
    Write-Output ($s37 -split "`r?`n" | Where-Object { $_ -match '^stats ' })
    Write-Output ($s58 -split "`r?`n" | Where-Object { $_ -match '^stats ' })

    # ── 负控：xmp-only 变体（不是 ISO 容器）──────────────────────────────────────
    # ⚠ 这条负控咬的是**读数来源**：如果产品对任何带副图的 JPEG 都打 `ISO 21496-1 APP2`，本段全绿但毫无判别力。
    $rNeg = RunCli 'iso_neg' $gmNeg 'png' @()
    $oNeg = (($null -ne $rNeg.file) -and (Test-Path $rNeg.file))
    CK ($oNeg -and ($rNeg.log -match '元数据来源: hdrgm XMP') -and ($rNeg.log -notmatch '元数据来源: ISO 21496-1 APP2')) `
       "⑬h **负控**：xmp-only 样本走 hdrgm XMP 分支，产品**不**声称 `元数据来源: ISO 21496-1 APP2` ⇒ ⑬a/⑬d 的来源行非恒真"
    $pNeg = ProbeOut 'gainmap' $gmNeg ''
    CK (($pNeg -match 'iso=null') -and ($pNeg -match 'FAIL IsGainMapContainer\(\)=true') -and
        ($pNeg -match 'FAIL ISO 21496-1 元数据存在') -and ($pNeg -match 'PROBE RESULT: pass=3 fail=3')) `
       "⑬i **负控**：探针判它不是 ISO 容器（iso=null + 3 条 FAIL）⇐ 实得 [" + ([regex]::Match($pNeg, 'PROBE RESULT: [^\r\n]*').Value.Trim()) + "]"
    $sNeg = ProbeStats $gmNeg ''
    CK (($sNeg -match 'maxLinear=1\.0000') -and ($sNeg -match 'over1=0 total=')) `
       "⑬j **负控像素**：maxLinear 恰为 1.0000 且 over1=0（解码没坏、只是没有增益可施加）—— 与 ⑬c/⑬f 的大数 over1 形成对照"
}


Write-Host "`n### 14) 出口诚实性（批次 E 2026-11-03：审阅面 2/面 4 的四条必修/应修）###" -ForegroundColor Cyan
# ── 14a P0-3：avif+gainmap 的 --dry-run 必须展示真实管线（纯托管 tmap），不再打印普通 avif ffmpeg 命令。
#    旧代码预览分支只认 jpg/jpeg ⇒ avif 打印 `ffmpeg -y …` 与实际执行的 tmap 出口失真（JXR 预览漂移同类）。
$drAv = RunCli 'eg_dr_av' $hdr 'avif' @('--jpeg-gain-map', 'true', '--dry-run')
CK ($drAv.log -match 'GainMapEncoder -> AVIF tmap') "14a avif+gainmap dry-run：点名纯托管 tmap 管线（实得含 tmap 文案）"
CK ($drAv.log -notmatch 'ffmpeg -y') "14a avif+gainmap dry-run：**不再**打印普通 avif ffmpeg 命令（旧失真形态绝迹）"
#    负控：jpg 侧的 dry-run 仍走同一份纯托管文案（该分支没被 avif 改动波及）
$drJpg = RunCli 'eg_dr_jpg' $hdr 'jpg' @('--jpeg-gain-map', 'true', '--dry-run')
CK ($drJpg.log -match '\[纯托管\] GainMapEncoder 色调映射') "14a' jpg+gainmap dry-run 仍是纯托管文案（回归对照）"

# ── 14b P0-2：引擎**可走**的 gainmap + `-e Cjpegli` 不得被 Cjpegli 分派分支截成普通 JPEG。
#    旧代码该分支没有引擎豁免（Cjxl/Jxr 2026-09-17 就加了）⇒ 静默出普通 cjpegli JPEG（images=1 无 ISO）。
$rCj = RunCli 'eg_cjpegli' $hdr 'jpg' @('--jpeg-gain-map', 'true', '-e', 'Cjpegli')
CK ($rCj.code -eq 0 -and $rCj.file) "14b routable + `-e Cjpegli` + gainmap：正常产出（实得 code=$($rCj.code)）"
if ($rCj.file) {
    $pmCj = ProbeOut 'gainmap' $rCj.file.FullName "$out/sec14b.jpg"
    CK ($pmCj -match 'images=2') "14b 产物是真增益图容器（images=2；被静默截成普通 JPEG 时为 images=1）"
    CK ((IsoMax $pmCj) -ne "" -and ([double](IsoMax $pmCj) -gt 0)) "14b ISO max 在场且非零（实得 $(IsoMax $pmCj)）"
}

# ── 14c P0-2 回归对照：引擎**不可走**（动画输入）+ `-e Cjpegli` ⇒ 仍走专用管线（该路由本批未动，锁住不回归）。
$anim = "$out/anim.gif"
Start-Process -FilePath $ffm -ArgumentList @('-y', '-hide_banner', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'testsrc2=s=64x48', '-frames:v', '4', $anim) -NoNewWindow -Wait | Out-Null
CK (Test-Path $anim) "14c 动画输入已生成（4 帧 gif）"
$rAn = RunCli 'eg_anim' $anim 'jpg' @('--jpeg-gain-map', 'true', '-e', 'Cjpegli')
CK ($rAn.code -eq 0 -and $rAn.file) "14c 动画 + `-e Cjpegli` + gainmap：专用管线照常产出（实得 code=$($rAn.code)）"
if ($rAn.file) {
    $pmAn = ProbeOut 'gainmap' $rAn.file.FullName "$out/sec14c.jpg"
    CK ($rAn.log -match '\[GainMap\] Step') "14c 真牙：日志点名专用管线（[GainMap] Step 在场；被 Cjpegli 分派截走时无此标记）"
    CK ($pmAn -match 'images=1') "14c 动画 testsrc2 是 SDR 源 => 按 2026-09-20 裁定产普通 JPEG（images=1），不是零增益假 Ultra HDR"
    CK ($rAn.log -match '输入为 SDR') "14c 诚实降级：日志点名「输入为 SDR…已按普通 JPEG 输出」"
}

# ── 14d P1-4：ISO 21496-1 min_version≠0 不得静默降级 SDR —— 字节手术把 DSC00037 的
#    min_version u16 翻成 1，解码必须**点名**（「超出本解码器（仅 v0）…SDR 交付」）而不是无声当无增益图。
$dscV1 = "$out/dsc_minver1.jpg"
$srcB = [IO.File]::ReadAllBytes($gm37)
$txt = [Text.Encoding]::ASCII.GetString($srcB)
$urnIdx = $txt.IndexOf('urn:iso:std:iso:ts:21496:-1')
CK ($urnIdx -ge 0) "14d 前置：DSC00037 内找到 ISO URN（偏移 $urnIdx）"
if ($urnIdx -ge 0) {
    # ⚠ URN 字串可能在 XMP 文本里也出现一次（实测 DSC00037 两处，首个在 APP1）=>
    #   翻**所有**「NUL 后两字节为 00 00」的出现点；真 APP2 必在其中，XMP 文本被翻
    #   两个字节对本判据无害（一次性变异件）。
    $urnLen = 'urn:iso:std:iso:ts:21496:-1'.Length
    $flips = 0
    $scan = $urnIdx
    while ($scan -ge 0) {
        $cand = $scan + $urnLen + 1
        if ($srcB[$cand] -eq 0 -and $srcB[$cand + 1] -eq 0) { $srcB[$cand] = 0; $srcB[$cand + 1] = 1; $flips++ }
        $scan = $txt.IndexOf('urn:iso:std:iso:ts:21496:-1', $scan + 1)
    }
    CK ($flips -ge 1) "14d 前置：翻转了 $flips 处 min_version（真 APP2 必在其中）"
    [IO.File]::WriteAllBytes($dscV1, $srcB)
    # ⚠ 断言走**产品日志**：ServiceProbe gainmap 模式不转发 [GainMap解码] 日志（容器层自有输出），
    #   而点名行是解码编排层打的 ⇒ 用产品 CLI 把变异件转 png，从 item.Log 里断言。
    $rV1 = RunCli 'eg_minver' $dscV1 'png' @()
    CK ($rV1.code -eq 0) "14d 前置：min_version=1 变异件正常转出（实得 code=$($rV1.code)；静默降级=SDR png）"
    CK ($rV1.log -match 'min_version=1') "14d 点名在案：min_version=1 超出支持被日志点名（不再静默）"
    CK ($rV1.log -match 'SDR 交付') "14d 降级语义诚实：日志写明按无增益图处理（SDR 交付）"
    CK ($rV1.log -notmatch '元数据来源: ISO 21496-1 APP2') "14d 该 APP2 确实被忽略（不再解析出 ISO 元数据）"
}

# ── 14e P0-1：gainmap 产物不得带回**源的** hdrgm XMP（复制链排除）。
#    诚实界定：随包 exiftool 13.60 复制未知命名空间时本来就整个丢弃 ⇒ 污染**当前不重现**；
#    本臂是**版本脆弱性契约锁**——新版 exiftool 一旦开始搬运 hdrgm，没有复制链排除就会
#    静默污染（解码端 ISO/XMP 差 >0.02 时改采 XMP ⇒ 宣称旧量程交付新量程），届时本臂当场红。
#    构造：给 PQ 源 PNG 注入 iTXt XMP（hdrgm:GainMapMax=9.9，与请求量程差 >0.02）再走完整产品链。
$hdrXmp = "$out/src_hdr_xmp.png"
Copy-Item $hdr $hdrXmp -Force
$inj = @'
import struct, zlib, sys
p = sys.argv[1]
xmp = ("<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?>"
       "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
       "<rdf:Description xmlns:hdrgm='http://ns.adobe.com/hdrgm/1.0/'"
       " hdrgm:GainMapMin='0.0' hdrgm:GainMapMax='9.9' hdrgm:Gamma='1.0'"
       " hdrgm:OffsetSDR='0' hdrgm:OffsetHDR='0' hdrgm:HDRCapacityMin='0' hdrgm:HDRCapacityMax='9.9'/>"
       "</rdf:RDF></x:xmpmeta><?xpacket end='w'?>").encode()
data = b'XML:com.adobe.xmp' + b'\x00\x00\x00\x00\x00' + xmp
chunk = b'iTXt' + data
png = bytearray(open(p, 'rb').read())
i = png.find(b'IHDR')
ins = i + 4 + 13 + 4
png[ins:ins] = struct.pack('>I', len(data)) + chunk + struct.pack('>I', zlib.crc32(chunk) & 0xffffffff)
open(p, 'wb').write(bytes(png))
print('injected', len(data))
'@
$injOut = $inj | python - $hdrXmp
CK ($LASTEXITCODE -eq 0) "14e 前置：XMP iTXt 注入成功（$($injOut | Select-Object -First 1)）"
$rPol = RunCli 'eg_poll' $hdrXmp 'jpg' @('--jpeg-gain-map', 'true')
CK ($rPol.code -eq 0 -and $rPol.file) "14e 污染源 + gainmap：正常产出（实得 code=$($rPol.code)）"
if ($rPol.file) {
    $prodTxt = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($rPol.file.FullName))
    CK (-not $prodTxt.Contains('hdrgm')) "14e 产物**字节级无** hdrgm XMP（源的旧增益量程没被搬进来；污染回归时此条当场红）"
    # ⚠ 不 assert 产品日志的「元数据来源」行：它只在**解码增益图源**时出现，本臂源是 PQ PNG（无源增益图可解）。
    #   「源旧量程不得被采用」已由上面字节级断言 + 下面 ISO max 值级断言共同钉住（无 XMP 载体 ⇒ ISO 必胜）。
    $pmPol = ProbeOut 'gainmap' $rPol.file.FullName "$out/sec14e.jpg"
    CK ((IsoMax $pmPol) -ne "" -and ([double](IsoMax $pmPol) -gt 0)) "14e ISO max 在场且非零（实得 $(IsoMax $pmPol)）"
}


Write-Host ""
Write-Host "===== gainmap-engine: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）；失败则保留供排查
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
exit $(if ($fail -eq 0) { 0 } else { 1 })
