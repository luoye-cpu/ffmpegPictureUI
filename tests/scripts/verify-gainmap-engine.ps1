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
#      色度（≈0.708）与默认 sRGB 底（≈0.640）**不同**；multi-channel 的 XMP hdrgm `Channels=3`
#      与默认 `1` 不同。⚠ 原 ⑤ 对 bt2020 只有**产品日志散文** + `channels=1`（后者与默认相同、
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
$pr  = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $pr)) { $pr = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
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
function ProbeOut([string]$mode, [string]$arg, [string]$arg2) {
    $o = "$out/_probe.txt"
    $al = @($mode, ('"' + $arg + '"'))
    if ($arg2) { $al += ('"' + $arg2 + '"') }
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
function ExifTag([string]$path, [string]$tag) {
    if (-not (Test-Path $path)) { return "" }
    $o = "$out/_et.out"; $e = "$out/_et.err"
    Start-Process -FilePath $et -ArgumentList @('-s', '-s', "-$tag", $path) `
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
if ($g2.file -and $e1.file) {
    $g2ch = ExifTag $g2.file.FullName 'XMP-hdrgm:Channels'
    $e1ch = ExifTag $e1.file.FullName 'XMP-hdrgm:Channels'
    CK ($g2ch -eq '3') "⑤b multi-channel：exiftool 读 XMP hdrgm Channels=3（实得「$g2ch」）"
    CK ($e1ch -eq '1') "⑤b 默认：exiftool 读 XMP hdrgm Channels=1（实得「$e1ch」）⇒ 上一条有判别力"
} else { CK $false "⑤b 缺产物，无法做 exiftool 通道数判别" }
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
    $mm = ExifTag $m1.file.FullName 'XMP-hdrgm:GainMapMax'
    # ⚠ 2026-09-20 裁定（**SDR ⇒ 普通 JPEG**）：SDR 输入（headroom ≤ 1）**不写增益图** ⇒ 产物是**普通 JPEG**
    #   ⇒ `GainMapMax` **为空**。原断言 `-eq '0.00'`（= 假 Ultra HDR）已被该裁定**推翻**，故此处同步改判据。
    #   ⚠ 判据**不弱化**：由「读到一个 0.00 的标签」改为「**必须没有**增益图标签」+「**必须点名**为何未启用」，
    #     后者直接证明走的是 SDR 路（比原探针更贴近本格的用意：ICC 判据压过 cICP/ffprobe）。
    CK ([string]::IsNullOrWhiteSpace($mm)) "⑨ 实测：混合件走 **SDR** 路 ⇒ **不写增益图**（XMP GainMapMax 为空，实得「$mm」）⇒ ICC 判据压过 cICP/ffprobe"
    CK ($m1.log -match '输入为 SDR') "⑨ 实测：日志**点名**「输入为 SDR…已按普通 JPEG 输出」（可用性诚实，非静默降级）"
} else { CK $false "⑨ 混合件无产物" }
if ($n1.file) {
    $nn = ExifTag $n1.file.FullName 'XMP-hdrgm:GainMapMax'
    CK ($nn -eq '2.30') "⑨ 对照：去 ICC 后走 **HDR** 路（XMP GainMapMax=$nn）⇒ 唯一变量 ICC 决定了分类"
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
CK ($gme -match 'Action<string>\? log = null, CancellationToken ct = default,\s+int jpegProgressiveLevel = -1\)') "⑩ 源码锁：jpegProgressiveLevel 仍是 EncodeAsync 的**末位**形参（QueueProcessor 用位置实参传 log/ct）"
CK ($qpc -match 'ColorMapping\.GainMapJpegCodec\.ProgressiveLevelOf\(item\.Options\)') "⑩ 源码锁：专用管线调用点复用单一真值（不另写映射）"
CK ($qpc -match 'jpegProgressiveLevel: gmProgLv\)') "⑩ 源码锁：专用管线确实把该值传进 EncodeAsync（防接线被删）"

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

Write-Host ""
Write-Host "===== gainmap-engine: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）；失败则保留供排查
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
exit $(if ($fail -eq 0) { 0 } else { 1 })
