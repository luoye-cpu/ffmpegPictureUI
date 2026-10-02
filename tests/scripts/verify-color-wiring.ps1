# verify-color-wiring.ps1 —— P7a 引擎接线的端到端门禁（跑**真实产品 CLI**，不直接调内部函数）
#
# 本门禁的历史价值（它拓出来的东西比“通过”本身重要）：
#   • 发现并钉住了一个真缺陷：`ColorKernels.EncodeTo8` 的**解析式分支漏乘 65535**（把 0..1 当 16-bit 码值），
#     使 8-bit 出口的引擎产物整幅近黑。它只在**无表路径**（小帧 <256k 像素）暴露，
#     所以旧的 16-bit 出口“表≡解析”断言永远看不到；现在 selftest 要求 **rgb8 出口也表≡解析且均值合理**。
#   • 当时的“引擎 vs legacy 全部 ≥40dB”是假绿：旧比较把 0..255 的字节当 16-bit 码除以 65535（PSNR 虚高 ~48dB）。
#   • 成功项的 item.Log 默认**不回显**（只有失败才转储）⇒ 本脚本统一带 `--log-level Debug`。
#
# 要钉住的五件事：
#   ① 不选引擎时行为不变（同参数产物逐字节相同）；
#   ② 选了引擎真的走了引擎（日志有决策/耗时行，产物标签正确，与 legacy 像素同域）；
#   ③ 引擎不能走时**显式失败并列名**（绝不静默回退 legacy —— 可用性诚实原则）；
#   ④ 引擎不跑 iccgen ⇒ 目标 ICC 必须后置补齐（否则只有标签、或被恢复带回源 ICC 与像素失配）。
#   ⑤（2026-09-17 新增）JXL 的两个后端**都**走引擎出口：ffmpeg-libjxl 子情形见 (9) 段，
#      **Cjxl 子情形见 (10) 段**（旧表述"Cjxl 后端仍被挡"是 (a) 轮的状态，已被 (b) 轮取代）。
#   ⑥（2026-09-17 新增）cjxl 执行出口的**显式失败**语义：任何一环不满足即抛异常，绝不静默回退 ffmpeg。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"   # MeanY 要跑 signalstats；不定义就会拿到空路径而永远返 -1
# (8) 段自备 P3 ICC 素材要用 exiftool（提取 ICC）；生成逻辑与 caps 共用 `_lib-color-assets.ps1`。
# ⚠ 不把它塞进下面的 `$need` 硬检查：缺它只该让 (8) 段转红，不该让整个脚本 exit 1（那会掩盖其余段的结论）。
$et = "$root/publish/PLAN/exiftool/exiftool.exe"
. "$root/tests/scripts/_lib-color-assets.ps1"
$pr = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $pr)) { $pr = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $pr + $(if ($pr -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
foreach ($need in @($exe, $fp, $pr)) { if (-not (Test-Path $need)) { Write-Output "缺工具：$need"; exit 1 } }
$val = "$root/tests/output/validate"; $col = "$val/../results/color"
# ⚠ **运行级隔离（2026-09-17 修）**：原先 `$out` 是**固定**路径 + 下一行整目录删
#   ⇒ **两个同名实例并发时互删**：`RunCli` 把日志写到固定 `$out/<tag>.o`，A 实例删掉目录后
#   B 实例读到的 `<tag>.o` 是空的 ⇒ **日志类断言齐红、而文件类断言仍绿**（实测 94/4 与 98/0 交替，
#   且**两次运行的 PASS 总数都会变** —— 这正是识别它的最强信号）。
#   修法照既有范式（`verify-ps-compat` / `verify-gainmap-memory` 的 GUID 隔离）：加唯一后缀 + 结尾自清。
$out = "$val/wire_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
Remove-Item -Recurse -Force $out -EA SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null
$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }

# ⚠ 参数名不能叫 $args；Start-Process 而非直接 & exe
function RunCli([string]$tag, [string]$in, [string]$fmt, [string[]]$extra) {
    # --log-level Debug：成功项的 item.Log 默认**不回显**（只有失败才转储），
    # 不写这句则“日志里有引擎决策行”一类断言会假失败（实测）。
    $a = @('--headless', '-i', $in, '--format', $fmt, '--output', "$out/$tag", '--log-level', 'Debug') + $extra
    $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput "$out/$tag.o" -RedirectStandardError "$out/$tag.e"
    $log = [System.IO.File]::ReadAllText("$out/$tag.o") + [System.IO.File]::ReadAllText("$out/$tag.e")
    $f = Get-ChildItem -Recurse "$out/$tag" -Include *.$fmt, *.jpg, *.jpeg, *.tif, *.tiff -EA SilentlyContinue |
         Select-Object -First 1
    return @{ code = $p.ExitCode; log = $log; file = $f }
}
function Cicp([string]$path) {
    $o = "$out/_cicp.txt"
    Start-Process -FilePath $fp -ArgumentList @('-v', 'error', '-select_streams', 'v:0', '-show_entries',
        'stream=color_primaries,color_transfer', '-of', 'csv=p=0', $path) `
        -NoNewWindow -Wait -RedirectStandardOutput $o | Out-Null
    return ([System.IO.File]::ReadAllText($o)).Trim()
}
function IccOf([string]$path) { return (Cicp $path) }   # 仅用于日志展示
function PixelPsnr([string]$a, [string]$b, [string]$thr) {
    $o = "$out/_psnr.txt"
    $p = Start-Process -FilePath $pr -ArgumentList @('psnr', $a, $b, $thr) -NoNewWindow -Wait -PassThru `
         -RedirectStandardOutput $o | Out-Null
    return ([System.IO.File]::ReadAllText($o))
}
# ServiceProbe 单次调用的文本输出（GainMap 容器结构交叉验证用）。
# ⚠ 一律 Start-Process，不用调用运算符 `& exe`（部分宿主下会静默失败、输出为空 ⇒ 断言假红）。
function ProbeOut([string]$mode, [string]$arg) {
    $o = "$out/_probe.txt"
    Start-Process -FilePath $pr -ArgumentList @($mode, ('"' + $arg + '"')) -NoNewWindow -Wait -PassThru `
         -RedirectStandardOutput $o | Out-Null
    return ([System.IO.File]::ReadAllText($o))
}
# 实测宽高：一律由 ffprobe 读回产物，不靠产品自己的解析器自证
function SizeOf([string]$path) {
    $o = "$out/_size.txt"
    Start-Process -FilePath $fp -ArgumentList @('-v', 'error', '-select_streams', 'v:0', '-show_entries',
        'stream=width,height', '-of', 'csv=p=0', $path) `
        -NoNewWindow -Wait -RedirectStandardOutput $o | Out-Null
    return ([System.IO.File]::ReadAllText($o)).Trim()
}

$sdr = "$val/sdr_plain.png"; $p3 = "$col/a2_srgb_p3.png"; $hdr = "$val/src_hdr.png"
foreach ($need in @($sdr, $hdr)) { if (-not (Test-Path $need)) { Write-Output "缺素材 $need（先跑 ensure-fixtures.ps1）"; exit 1 } }
$ENG = @('--color-engine', 'engine'); $LEG = @('--color-engine', 'legacy'); $Q100 = @('--quality', '100')

Write-Host "`n=== (1) 默认走引擎；legacy 是显式逃生门（2026-09-16 契约更新）===" -ForegroundColor Cyan
# ⚠ 旧版此处是「不传参 ⇒ legacy」，与 legacy 逐字节比。**默认值已改为 engine**，
#   若照旧写就成了「引擎 vs 引擎」的空断言 ⇒ 必须显式传 legacy 才是真对照，
#   并另加一条断言把「默认即引擎」本身锁住（否则哪天被改回 legacy 无人发现）。
$a = RunCli 'leg_auto' $sdr 'png' ($Q100 + $LEG)
$b = RunCli 'eng_auto' $sdr 'png' ($Q100 + $ENG)
$d = RunCli 'def_auto' $sdr 'png' $Q100
CK ($a.file -and $b.file -and $d.file) "(1) 三路都有产物（显式 legacy / 显式 engine / 默认）"
if ($a.file -and $b.file) {
    $ha = (Get-FileHash $a.file.FullName -Algorithm SHA256).Hash
    $hb = (Get-FileHash $b.file.FullName -Algorithm SHA256).Hash
    CK ($ha -eq $hb) "(1) SDR 源 + auto 目标：引擎判直通 ⇒ 与显式 legacy **逐字节相同**（$($ha.Substring(0,12))…）"
}
CK ($b.log -match '直通') "(1) 日志写明「计划为直通 ⇒ 由 ffmpeg 重编码」（不是偷偷不走）"
if ($b.file -and $d.file) {
    $hb2 = (Get-FileHash $b.file.FullName -Algorithm SHA256).Hash
    $hd = (Get-FileHash $d.file.FullName -Algorithm SHA256).Hash
    CK ($hb2 -eq $hd) "(1) **默认（不传 --color-engine）⇒ 走引擎**：与显式 engine 逐字节相同"
}
CK ($d.log -match '\[色彩引擎\]') "(1) 默认路径出现引擎日志 ⇒ 默认值确实是 engine（默认值即契约）"
CK ($a.log -notmatch '色彩引擎') "(1) 显式 legacy 时一行引擎日志都不该出现（逃生门零污染）"

Write-Host "`n=== (2) 选了引擎 ⇒ 真的走引擎 ===" -ForegroundColor Cyan
if (Test-Path $p3) {
    $l1 = RunCli 'leg_p3srgb' $p3 'png' ($Q100 + @('--color-space', 'sRGB') + $LEG)
    $e1 = RunCli 'eng_p3srgb' $p3 'png' ($Q100 + @('--color-space', 'sRGB') + $ENG)
    CK ($e1.file -and $e1.file.Length -gt 0) "(2) 引擎产出文件 $($e1.file.Length)B"
    CK ($e1.log -match '\[色彩引擎\] 决策：Map') "(2) 日志有引擎决策行（Map）"
    CK ($e1.log -match 'MPix/s') "(2) 日志有引擎执行耗时行（证明像素真由本进程变换）"
    CK ($e1.log -notmatch '\[cmd\] ffmpeg') "(2) 本次**没有**再跑 legacy 的 ffmpeg 色彩命令"
    if ($l1.file -and $e1.file) {
        $ps = PixelPsnr $l1.file.FullName $e1.file.FullName '40'
        CK ($ps -match 'PROBE RESULT: pass=\d+ fail=0') "(2) 引擎 vs legacy 像素同域（$(($ps -split "`n" | Where-Object { $_ -match 'psnr=' }) -join '')）"
        CK ((Cicp $e1.file.FullName) -match 'bt709|iec61966') "(2) 引擎产物 CICP 标签为 sRGB 目标（$(Cicp $e1.file.FullName)）"
        CK ($e1.log -match '已嵌入目标 ICC|计划为直通') "(2) 目标 ICC 已后置补齐（④：不嵌就是与像素失配的色彩描述）"
    }
} else { Write-Output "  SKIP (2)：缺 P3 广色域素材 $p3" }

Write-Host "`n=== (3) HDR→SDR：引擎 tonemap + 峰值来源可见 ===" -ForegroundColor Cyan
$e2 = RunCli 'eng_tone' $hdr 'png' ($Q100 + @('--color-space', 'sRGB', '--color-tone-map', 'hable') + $ENG)
CK ($e2.file -and $e2.file.Length -gt 0) "(3) 引擎 tonemap 产出文件"
CK ($e2.log -match 'tonemap') "(3) 引擎决策含 tonemap 挂载"
CK ($e2.log -match '峰值来源=') "(3) 日志写明峰值来源（用户/元数据/整帧实测/名义兜底）"
if ($e2.file) { CK ((Cicp $e2.file.FullName) -notmatch 'smpte2084') "(3) 输出不得仍挂 PQ 标签（$(Cicp $e2.file.FullName)）" }

Write-Host "`n=== (4) 不能走 ⇒ 显式失败并点名（绝不静默回退） ===" -ForegroundColor Cyan
# ⚠ 2026-09-16 契约更新：质量参数**已由引擎编码出口承接**（见 ImageEncoderArgs）⇒ 不再拒绝。
#   此处改为「承接生效」的正向断言，并把"真的生效"钉住（q60 与 q100 的 PNG 压缩级别不同 ⇒ 体积必须不同）；
#   真正不可承接的项由下面这条（旧 tonemap-curve）继续守着 —— 负控不放松。
$r1 = RunCli 'eng_q' $sdr 'png' (@('--quality', '60') + $ENG)
$r0 = RunCli 'eng_q100' $sdr 'png' ($Q100 + $ENG)
CK ($r1.file -and $r1.log -notmatch '尚未承接') "(4) 质量参数已承接 ⇒ 不再拒绝（$($r1.file.Length)B）"
if ($r1.file -and $r0.file) {
    CK ($r1.file.Length -ne $r0.file.Length) `
        "(4) 质量**确实生效**（q60 $($r1.file.Length)B ≠ q100 $($r0.file.Length)B，PNG 压缩级别随之变化）"
}
$r3 = RunCli 'eng_old' $sdr 'png' (@('--tonemap-curve', 'reinhard') + $ENG)
CK ($r3.log -match 'tonemap-curve') "(4) 旧 --tonemap-curve 未接入 ⇒ 拒绝并提示改用 --color-tone-map"

Write-Host "`n=== (7) 最长边几何缩放：不再拒绝，且真的缩到限值 ===" -ForegroundColor Cyan
# ⚠ 2026-09-16 契约更新：`--max-dimension-enabled` 原先被 ColorEngineRouter 当作「引擎不做几何变换」
#   整条拒绝（显式 engine ⇒ 退出码 90）。但队列的**统一预处理阶段**（所有后端共用）本就会先缩放到临时
#   文件再分发 ⇒ 那次拒绝是纯粹的误伤：用户开了限值就再也用不了引擎。
#   现在：路由放行；缩放由预处理层完成，引擎侧另有同源兜底（见 RawColorPipeline）。
$r2 = RunCli 'eng_geo' $sdr 'png' (@('--max-dimension-enabled', 'true', '--max-dimension', '32') + $ENG)
CK ($r2.file -and $r2.file.Length -gt 0) "(7) 需要几何缩放 ⇒ 不再拒绝（显式 engine 也出产物）"
if ($r2.file) {
    $sz = SizeOf $r2.file.FullName
    CK ($sz -eq '32,24') "(7) 几何缩放**实测宽高** = 32,24（源 512x384，最长边锁 32；ffprobe 读回 $sz）"
}
# ⚠ 第 72 条（2026-09-17 登记）**复核（2026-09-19）——该登记的担心不成立**：
#   登记说「本条用 `MaxDimension|几何缩放` 做**或**匹配 ⇒ 分不清是哪一层 ⇒ 删了预缩放、但引擎那段没跑也会假绿」。
#   但**紧邻的 `:158` 已断言实测宽高 == 32,24**（源 512x384、限 32）⇒ 若引擎几何段没跑且预缩放被删，
#   尺寸会是 512x384 ≠ 32,24 ⇒ **`:158` 必红**。⇒ **假绿的路径不存在**，本条只需保证「可追溯」。
#   ⚠ 今天（预缩放还在）**不能**收紧为「必须命中 `[色彩] 几何缩放`」：默认路径下缩放由**预缩放层**完成、
#   引擎几何段**不跑**（日志只有 `[MaxDimension]`）⇒ 收紧会**假红**。⇒ 真正的收紧时机 = **删预缩放时**。
#   ⇒ 现在改为**打印实际命中的层**（诊断价值；判据仍只要求「命中其一」，不放松也不收紧）。
$geoLayer = if ($r2.log -match '\[MaxDimension\]') { 'prescale' } elseif ($r2.log -match '\[色彩\] 几何缩放') { 'engine' } else { 'none' }
CK ($geoLayer -ne 'none') "(7) 日志可追溯缩放发生在哪一层（实命中层: $geoLayer；⚠ 今天默认路径应为 prescale，删预缩放后应变为 engine）"
# 负控：不启用限值时尺寸必须原样（否则"缩了"可能只是编码器/别处的行为，断言就白写了）
$r2b = RunCli 'eng_nogeo' $sdr 'png' ($Q100 + $ENG)
if ($r2b.file) {
    $szb = SizeOf $r2b.file.FullName
    CK ($szb -eq '512,384') "(7) 未启用限值 ⇒ 尺寸不变（512,384；ffprobe 读回 $szb）"
}
# 负控：限值大于最长边 ⇒ 不该缩放（判据是"超限才缩"，不是"启用就缩"）
$r2c = RunCli 'eng_geobig' $sdr 'png' (@('--max-dimension-enabled', 'true', '--max-dimension', '4096') + $ENG)
if ($r2c.file) {
    $szc = SizeOf $r2c.file.FullName
    CK ($szc -eq '512,384') "(7) 限值 4096 > 最长边 ⇒ 不缩放（512,384；ffprobe 读回 $szc）"
}

function MeanY([string]$path) {
    # 用 ffmpeg signalstats 读实际像素均值（不靠产品自己的解析器自证）
    $o = "$out/_mean.txt"
    Start-Process -FilePath $ff -ArgumentList @('-hide_banner', '-loglevel', 'info', '-i', $path,
        '-vf', 'format=yuv444p,signalstats,metadata=print', '-frames:v', '1', '-f', 'null', '-') `
        -NoNewWindow -Wait -RedirectStandardError $o | Out-Null
    # ⚠ signalstats 是 YUV 滤镜：对 RGB 输入直接接它会不产生 YAVG（实测读回 -1）。
    $m = [regex]::Match([System.IO.File]::ReadAllText($o), 'YAVG=([0-9.]+)')
    return $(if ($m.Success) { [double]$m.Groups[1].Value } else { -1 })
}

Write-Host "`n=== (6) 超广目标：声明域必须等于像素实际所处的域 ===" -ForegroundColor Cyan
# “归一 BT.2020”不是只改标签：像素被矩阵映射进了 BT.2020（registry 的 Out*=bt2020 配 Rp/Gp/Bp 做矩阵）。
# 本用例钉两件必须一致的事（实测发现：旧代码给 BT.709 编码的像素附了 sRGB-TRC 的 ICC）：
#   A) 引擎自己的两条声明必须同一条 trc：cICP 的 transfer ≡ 嵌入 ICC 的 trc
#   B) 与 legacy 的亮度一致（均值）——**不要求逐点同值**：目标 trc=bt709 时两侧编码曲线本就不同
#      （引擎=ITU-R BT.709-6 定义式，legacy=zimg 纯 γ2.4，已登记的口径分歧），拿 dB 阀会把这个分歧洗白。
$eng6 = RunCli 'eng_adobe' $sdr 'png' ($Q100 + @('--color-space', '"Adobe RGB"') + $ENG)
$leg6 = RunCli 'leg_adobe' $sdr 'png' ($Q100 + @('--color-space', '"Adobe RGB"') + $LEG)
if ($eng6.file -and $leg6.file) {
    CK ($eng6.log -match '\[色彩引擎\] 决策：Map') "(6) 超广目标确实走了引擎（Map）"
    $ci6 = Cicp $eng6.file.FullName
    CK ($ci6 -match 'bt2020') "(6) 产物 cICP 声明归一后的 BT.2020 原色（实得 $ci6）"
    # A) 提取日志里的两条 trc：cICP 行与 ICC 行
    # ⚠ 日志行形如“cICP（primaries=9, transfer=1 ← bt2020/bt709；策略=Recommended）”
    #   —— token 对后面带“；…”，正则必须只取到分号前，否则比对永远不等（实测踩过）。
    $tCicp = [regex]::Match($eng6.log, 'cICP（primaries=\d+, transfer=(\d+) ← ([^)；]*)(?:；[^）]*)?）')
    $tIcc  = [regex]::Match($eng6.log, '已嵌入目标 ICC（([^)/]*)/([^）]*)）')
    CK ($tCicp.Success -and $tIcc.Success) "(6) 日志同时给出了 cICP 与嵌入 ICC 的 tokens"
    if ($tCicp.Success -and $tIcc.Success) {
        $trcPair = $tCicp.Groups[2].Value.Split('/')
        CK ($trcPair[1].Trim() -eq $tIcc.Groups[2].Value.Trim()) `
           "(6) cICP 的 trc ≡ 嵌入 ICC 的 trc（cICP='$($trcPair[1].Trim())' ICC='$($tIcc.Groups[2].Value.Trim())'）"
        # 编号交叉校：bt709⇒transfer=1，iec61966-2-1⇒13
        $want = switch ($trcPair[1].Trim()) { 'bt709' { '1' } 'iec61966-2-1' { '13' } default { '?' } }
        CK ($want -eq $tCicp.Groups[1].Value) "(6) cICP transfer 编号与 trc 名一致（$($tCicp.Groups[1].Value) ⇔ $($trcPair[1].Trim())）"
    }
    # B) 亮度“没跑飞”的下界（相对差 <10%）。⚠ 这里**故意不要求逐点同值**：
    #   目标 trc=bt709 时两侧编码曲线本就不同（引擎=ITU-R BT.709-6 定义式，legacy=zimg 纯 γ2.4，
    #   已登记口径分歧），实测均值 144.7 vs 156.3（7.9%）。逐点同域的严格 A/B 属于
    #   两侧同曲线的用例（本脚本 (2) sRGB→P3 实测 51.7dB，以及 ServiceProbe engine 32/32）。
    $my1 = MeanY $eng6.file.FullName; $my2 = MeanY $leg6.file.FullName
    $rel = if ($my2 -gt 0) { [Math]::Abs($my1 - $my2) / $my2 } else { 9 }
    CK ($my1 -gt 0 -and $my2 -gt 0 -and $rel -lt 0.10) `
       "(6) 引擎与 legacy 亮度同量级（YAVG $my1 vs $my2，相对差 $([Math]::Round($rel*100,1))%＜10%）"
       # 备注：两者不同是已登记的 zimg γ2.4 vs BT.709 定义式口径分歧，不打算洗成“相等”
} else { CK $false "(6) 超广目标两路产物缺失（engine=$($null -ne $eng6.file) legacy=$($null -ne $leg6.file)）" }

Write-Host "`n=== (8) 外部 ICC（--icc-file）：判定源色彩空间并真驱动映射 ===" -ForegroundColor Cyan
# ⚠ 2026-09-16 契约更新：`--icc-file` 原先被 ColorEngineRouter 当作「引擎未承接」整条拒绝，
#   而传统管线侧该选项其实也**没有消费者**（`ResolveIccSourceParams`/`GetIccSourceDisplayName` 均无调用点）
#   ⇒ 选项实际是死的。现由 ColorIntentFactory 承接：语义 = 用户显式指定**源**色彩空间。
# 本段的判据全是**可观测差异**，不靠"代码里写了"自证：
#   ① 源无 ICC 无标签（sdr_plain.png）⇒ 引擎按惯例假定 sRGB ⇒ 与 sRGB 目标等价 ⇒ 直通（不改像素）；
#   ② 同一输入 + `--icc-file p3.icc` ⇒ 源被改判为 Display P3（IccMetric）⇒ P3 ⊃ sRGB 越界 ⇒ Map（真转像素）；
#   ③ 无效/不存在的 ICC ⇒ 不得被采信（回落惯例假定），也不得报"用了外部 ICC"。
# ⚠ 素材**自备**（2026-09-17 修「门禁依赖另一门禁的遗留产物」）：此前这里直接读 `verify-color-caps.ps1`
#   的输出目录 ⇒ 干净 `tests/output` 上本段**必红**，而"单跑绿、全量红"极易被误判成**产品回归**
#   （本轮实测踩到 73/1 的假红）。现在缺素材（或素材退化成 ≤200B）就**按同一机制自己生成**——
#   生成逻辑抽到 `_lib-color-assets.ps1`，与 caps 共用**唯一一份**，不是第二套口径。
$p3icc = "$val/caps/p3.icc"
$p3len = 0
if (Test-Path $p3icc) { $p3len = (Get-Item $p3icc).Length }
if ($p3len -le 200 -and (Test-Path $et)) {
    Write-Output "  ℹ (8) 自备 P3 ICC 素材（caps 的遗留产物缺失/退化：$p3len B）"
    $asset = New-P3IccAsset -Dir "$val/caps" -Ffmpeg $ff -Exiftool $et
    $p3icc = $asset.Icc
    if (Test-Path $p3icc) { $p3len = (Get-Item $p3icc).Length }
}
if ($p3len -gt 200) {
    $i0 = RunCli 'icc_off' $sdr 'png' ($Q100 + @('--color-space', 'sRGB') + $ENG)
    $i1 = RunCli 'icc_on'  $sdr 'png' ($Q100 + @('--color-space', 'sRGB', '--icc-file', $p3icc) + $ENG)
    $i2 = RunCli 'icc_bad' $sdr 'png' ($Q100 + @('--color-space', 'sRGB', '--icc-file', "$out/_not_an_icc.icc") + $ENG)
    CK ($i0.file -and $i1.file -and $i2.file) "(8) 三路都有产物（无 ICC / 有效 ICC / 无效 ICC）"
    CK ($i0.log -match '源=目标（矩阵与曲线等价）') "(8) 无 --icc-file：源按惯例假定 sRGB ⇒ 与 sRGB 目标等价 ⇒ 直通"
    CK ($i1.log -match '决策：Map') "(8) --icc-file=p3.icc ⇒ 源改判为 P3 ⇒ 走 Map（像素真被转换）"
    CK ($i1.log -match '源色域超出目标') "(8) 决策理由含「源色域超出目标」（P3 ⊃ sRGB）—— 外部 ICC 被真正消费"
    CK ($i1.log -match '源=.*\[--icc-file\]') `
       "(8) 引擎日志写明源描述符来自外部 ICC（$([regex]::Match($i1.log, '源=[^\r\n]*').Value)）"
    CK ($i2.log -match '源=目标（矩阵与曲线等价）' -and $i2.log -notmatch '\[--icc-file\]') `
       "(8) 无效/不存在的 ICC 不被采信（回落惯例假定，且不冒认用了外部 ICC）"
    if ($i0.file -and $i1.file) {
        $ps = PixelPsnr $i0.file.FullName $i1.file.FullName '55'
        $m = [regex]::Match($ps, 'psnr=([0-9.]+)dB')
        $pd = if ($m.Success) { [double]$m.Groups[1].Value } else { -1 }
        CK ($pd -gt 0 -and $pd -lt 50) `
           "(8) 像素**确实变了**（P3→sRGB 实测 PSNR $pd dB ＜ 50dB ⇒ 不是「只改了标签」）"
    }
} else { CK $false "(8) 缺 P3 ICC 素材 $p3icc（自备也失败：exiftool 在位=$(Test-Path $et)、长度=$p3len B）" }

Write-Host "`n=== (5) GainMap：由色彩引擎决策并执行（2026-09-16 契约更新）===" -ForegroundColor Cyan
# ⚠ 旧契约：断言日志出现「尚未接入该路径」—— 那是「GainMap 被引擎**拒绝**」的写法。
#   改造后 `--jpeg-gain-map true` + JPEG 由**引擎**决策并执行 ⇒ 该断言已反向，必须整段替换。
#   新契约三条，缺一不可：
#     ① 日志**不得**再声明「GainMap 路径尚未接入引擎」（否则就是没接上）；
#     ② 日志出现**引擎色彩决策行**（证明决策真的由引擎做，不是被路由拦下后由别处处理）；
#     ③ 产物必须是**真 Ultra HDR**（不是普通 JPEG 改名）—— 用 ServiceProbe gainmap 解析容器结构交叉验证。
$r4 = RunCli 'eng_gm' $hdr 'jpg' (@('--jpeg-gain-map', 'true') + $ENG)
CK ($r4.file -and $r4.file.Length -gt 0) "(5) GainMap + 显式引擎 ⇒ 产出非空文件（$($r4.file.Length)B）"
CK ($r4.log -notmatch '尚未接入该路径') "(5) 日志不得再声明「GainMap 路径尚未接入引擎」（已接入）"
CK ($r4.log -match '\[色彩引擎\] 决策：') "(5) 出现引擎色彩决策行（决策由引擎做，而非被路由拦下）"
if ($r4.file) {
    $gpo = ProbeOut 'gainmap' $r4.file.FullName
    CK ($gpo -match 'images=2') "(5) 产物是 Ultra HDR 容器（顶层 2 幅图：底图 + 增益图）"
    CK ($gpo -match 'iso min=') "(5) ISO 21496-1 增益元数据存在（不是一张普通 JPEG 改名）"
    CK ($gpo -match 'mpf\[1\].*soi=True') "(5) MPF 副图偏移命中 SOI（增益图真实可定位）"
}

Write-Host "`n=== (9) JXL：ffmpeg-libjxl 子情形接入引擎（Cjxl 子情形见 (10) 段）===" -ForegroundColor Cyan
# 背景：JXL 有**两个后端**（`EncoderDetectionService.cs:55`：本机装了 cjxl ⇒ 默认就是 Cjxl）：
#   · backend=Ffmpeg（ffmpeg 内置 libjxl，吃 rawvideo）⇒ 2026-09-17 第一轮接入引擎出口；
#   · backend=Cjxl（**不吃 rawvideo**、色彩语义靠 `-x color_space=`/`-x icc_pathname=`/`--intensity_target=`）
#     ⇒ 2026-09-17 **第二轮**由引擎的 cjxl 执行出口接管（(10) 段）；**同日之前**它由
#     `QueueProcessor.ProcessCjxlAsync` 处理、被 `ColorEngineRouter` 的「外部编码器」门挡住。
# 本段前半证明 ffmpeg 子情形真的在跑；后半证明默认后端**也已改道**（契约变更，旧断言已作废）。
$jxlinfo = "$root/publish/PLAN/jxl/bin/jxlinfo.exe"
# ⚠ JXL 的标注回读**必须用 jxlinfo**：exiftool 对 JXL **连读都读不出** ICC（verify-color-caps.ps1 已锁）。
function JxlColorSpace([string]$path) {
    $o = "$out/_jxl.txt"
    Start-Process -FilePath $jxlinfo -ArgumentList @($path) -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $o | Out-Null
    return ((([System.IO.File]::ReadAllText($o)) -split "`r?`n") |
            Where-Object { $_ -match 'Color space' } | Select-Object -First 1)
}
# jxlinfo 的**第一行**（形如 `JPEG XL image, 32x32, lossy, 8-bit RGB+Alpha`）。
# ⚠ 通道信息（Alpha）只在这一行，**不在** `Color space` 行 ⇒ 用 `JxlColorSpace` 断言 alpha 必然假红
#   （2026-09-17 实测踩过：同一段 `AlphaYMin` 断言 PASS 已反证 alpha 确实存在，是断言 bug 不是缺陷）。
function JxlHeader([string]$path) {
    $o = "$out/_jxlh.txt"
    Start-Process -FilePath $jxlinfo -ArgumentList @($path) -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $o | Out-Null
    return ((([System.IO.File]::ReadAllText($o)) -split "`r?`n") |
            Where-Object { $_.Trim() } | Select-Object -First 1)
}
# ⚠ 必须用 **P3 源 + sRGB 目标**（不能用无标签的 sdr_plain：源按惯例假定 sRGB ⇒ 与 sRGB 目标
#   等价 ⇒ Action=None ⇒ 直通，根本不经过引擎编码出口；实测该格日志是「计划为直通」）。
# ⚠ 含空格的空间名必须自己带引号：`Start-Process -ArgumentList` 是**数组拼空格**、不会自动加引号
#   （实测 `'Display P3'` 被拆成两个参数 ⇒ 「目标语义 / 无法命名」）。本脚本 (6) 段同款写法。
$j1 = RunCli 'eng_jxl_srgb' $p3 'jxl' (@('--color-space', 'sRGB', '-e', 'Ffmpeg') + $ENG)
CK ($j1.file -and $j1.file.Length -gt 0) "(9) jxl(backend=Ffmpeg) 引擎出口产出非空文件（$($j1.file.Length)B）"
CK (($j1.log -match 'MPix/s') -and ($j1.log -notmatch '本次不适用引擎')) `
   "(9) 确实由引擎出口完成（日志有「MPix/s」且无「本次不适用引擎」）"
# 命令行形态：JXL 的 P/T 标签必须写在 `-i` **之前**（输出侧标签对 rawvideo 输入**被忽略**，
# 实测：输出侧 smpte432 ⇒ jxlinfo 读回 sRGB；前置 ⇒ 读回 P3）。
# 取引擎日志里编码步那一行：含 JXL 专有质量参数 `-distance`（解码步只有 `-vf`，不含它）。
$jEnc = (($j1.log -split "`n") | Where-Object { $_ -match '\[色彩\] ffmpeg' -and $_ -match '-distance' } | Select-Object -Last 1)
CK ($jEnc -and $jEnc -match '-color_primaries') "(9) 引擎 jxl 出口命令行含 CICP 标签"
if ($jEnc) {
    $iPrim = $jEnc.IndexOf('-color_primaries'); $iRaw = $jEnc.IndexOf('-f rawvideo')
    CK ($iPrim -ge 0 -and $iRaw -gt $iPrim) `
       "(9) 标签写在 `-i` 之前（-color_primaries@$iPrim < -f rawvideo@$iRaw；写输出侧会被静默忽略）"
}
if ($j1.file -and (Test-Path $jxlinfo)) {
    $cs1 = JxlColorSpace $j1.file.FullName
    CK ($cs1 -match 'sRGB') "(9) jxlinfo 读回 sRGB（$cs1）"
}
# Display P3：枚举里的另一档，钉住「目标空间真的驱动了标签」而不是恒写 sRGB。
$j2 = RunCli 'eng_jxl_p3' $sdr 'jxl' (@('--color-space', '"Display P3"', '-e', 'Ffmpeg') + $ENG)
CK ($j2.file -and $j2.file.Length -gt 0) "(9) 目标 Display P3 的引擎出口产出非空文件（$($j2.file.Length)B）"
if ($j2.file -and (Test-Path $jxlinfo)) {
    $cs2 = JxlColorSpace $j2.file.FullName
    CK ($cs2 -match 'P3') "(9) 目标 Display P3 ⇒ jxlinfo 读回 P3（$cs2）"
}
# ── 后半（**契约变更** 2026-09-17 第二轮）──────────────────────────────────────────
# 原断言「Cjxl 后端仍被挡」**已作废**：引擎新增了 cjxl 执行出口 ⇒ 默认后端（本机装了 cjxl
# ⇒ `--format jxl` 的默认就是 Cjxl）**现在也走引擎**。旧断言若保留会永久假红 ——
# 这是**有意**的契约变更，**不要**为了让旧断言变绿而回退路由门（详见下面 (10) 段）。
$j3 = RunCli 'def_jxl' $p3 'jxl' @()
CK ($j3.file -and $j3.file.Length -gt 0) "(9) 默认后端（Cjxl）产出非空 jxl（$($j3.file.Length)B）"
CK ($j3.log -notmatch '尚未接入该路径') "(9) 默认后端**不再**被报「引擎尚未接入该路径」（契约变更）"
CK ($j3.log -match '\[色彩\] cjxl 命令行') "(9) 默认后端确实由引擎的 **cjxl 出口**执行（日志有 cjxl 命令行）"

Write-Host "`n=== (10) JXL 的 Cjxl 后端：引擎 cjxl 执行出口（2026-09-17 第二轮接入；2026-10-02 SDR 改走 PNG 中转）===" -ForegroundColor Cyan
# 出口形态（2026-10-02 起两条腿）：
#   · SDR 且源在盘：`ffmpeg -f rawvideo … -f image2 <tmp>.png`（第二输入 -map_metadata 1 带源 EXIF/XMP）
#     → `cjxl <tmp>.png out.jxl …` —— 唯一能同时保住 cjxl 参数与源元数据的形态
#     （PPM/PAM 结构上不携带元数据；exiftool 补写 JXL 实测必然失败）。
#   · alpha（PAM+alphamerge）/ 真 HDR 目标（PPM）：仍走 `… -f image2pipe -c:v pam|ppm -` 管道。
# ⚠ 旧结论「PNG 等容器输入 -x 被忽略」已按 2026-10-02 实测**改正**：决定因素是**输入是否自带色彩
#   元数据**（#44 规则，与容器/裸流无关）。ffmpeg 从 rawvideo 写出的 PNG 无色彩 chunk
#   （chunk 表仅 IHDR/pHYs/eXIf/IDAT/IEND）⇒ `-x` 照常生效；三组对照
#   （bt2020+linear / sRGB / P3 ICC）jxlinfo 读回与 PPM 逐字一致。
# 为什么必须用 **P3 源**：无标签源会落到「无标注直通」，测不出色彩语义是否真由计划驱动。
function AlphaYMin([string]$path) {
    $o = "$out/_aymin.txt"
    Start-Process -FilePath $ff -ArgumentList @('-hide_banner', '-i', $path, '-vf',
        'alphaextract,signalstats,metadata=print:key=lavfi.signalstats.YMIN', '-f', 'null', '-') `
        -NoNewWindow -Wait -RedirectStandardOutput $o -RedirectStandardError "$o.e" | Out-Null
    $t = [System.IO.File]::ReadAllText($o) + [System.IO.File]::ReadAllText("$o.e")
    $m = ($t -split "`r?`n") | Where-Object { $_ -match 'YMIN=([0-9.]+)' } | Select-Object -First 1
    if ($m) { return [double]($m -replace '.*YMIN=([0-9.]+).*', '$1') }
    return -1
}
# 16-bit 口径的 alpha 最小值：**先统一到 rgba64le** 再取 alpha 的 YMIN。
# ⚠ 必须与 8-bit 口径分开：8-bit 源经 `format=rgba64le` 的 8→16 扩张**不是** v*257
#   （实测 128 ⇒ 32238，而 128*257=32896）⇒ 直接拿 8-bit 阈值去判 16-bit 产物会假红。
#   本 helper 用于**同口径对比**（源与产物都过这一遍），相等即证明未被拍平。
function AlphaYMin16([string]$path) {
    $o = "$out/_aymin16.txt"
    Start-Process -FilePath $ff -ArgumentList @('-hide_banner', '-i', $path, '-vf',
        'format=rgba64le,alphaextract,signalstats,metadata=print:key=lavfi.signalstats.YMIN', '-f', 'null', '-') `
        -NoNewWindow -Wait -RedirectStandardOutput $o -RedirectStandardError "$o.e" | Out-Null
    $t = [System.IO.File]::ReadAllText($o) + [System.IO.File]::ReadAllText("$o.e")
    $m = ($t -split "`r?`n") | Where-Object { $_ -match 'YMIN=([0-9.]+)' } | Select-Object -First 1
    if ($m) { return [double]($m -replace '.*YMIN=([0-9.]+).*', '$1') }
    return -1
}
# ── (10a) 携带源 ICC 的计划：cjxl 出口**能**写 ICC（ffmpeg-libjxl 出口做不到，见 (9) 段）──
# ⚠ 取命令行必须认准 `[色彩] cjxl 命令行` 这个标签：同名前缀下还有 `[色彩] cjxl 出口：…` 的散文行
#   （"变换后的 raw → PPM → cjxl"），用宽前缀 `-Last 1` 会取到散文 ⇒ 断言假红（实测踩过）。
$c1 = RunCli 'eng_cjxl_icc' $p3 'jxl' @()
CK ($c1.file -and $c1.file.Length -gt 0) "(10) cjxl 出口产出非空文件（$($c1.file.Length)B）"
$c1Cmd = (($c1.log -split "`n") | Where-Object { $_ -match '\[色彩\] cjxl 命令行' } | Select-Object -Last 1)
CK ($c1Cmd -and $c1Cmd -match 'icc_pathname=') `
   "(10) 携带源 ICC 的计划走 `-x icc_pathname=`（**ffmpeg-libjxl 出口做不到**这件事）"
CK (($c1.log -split "`n") | Where-Object { $_ -match '\[色彩\] ffmpeg' -and $_ -match '-f image2 ' -and $_ -match '\.png' }) `
   "(10) SDR 场景 ffmpeg 侧走 **PNG 落盘中转**（-map_metadata 1 保住源 EXIF/XMP；图像体积与 PPM 一致，2026-10-02 实测）"
CK ($c1.log -notmatch 'PNG 中转落盘失败') `
   "(10) 负控：PNG 中转未失败（失败会点名且显式不产出，不回退 PPM 以免静默丢元数据）"
CK ($c1.log -match '跳过 exiftool 后置') `
   "(10) 已跳过 exiftool 后置（对 JXL 必然失败并打出假告警：实测退出码 1）"
if ($c1.file -and (Test-Path $jxlinfo)) {
    $csIcc = JxlColorSpace $c1.file.FullName
    CK ($csIcc -match 'P3') "(10) 携带的源 ICC 真的进了产物（jxlinfo 读回 P3：$csIcc）"
}
# ── (10b) 显式目标：语义由**计划**翻译成 cjxl 的 color_space ──
$c2 = RunCli 'eng_cjxl_srgb' $p3 'jxl' @('--color-space', 'sRGB')
CK ($c2.file -and $c2.file.Length -gt 0) "(10) 显式 sRGB 目标产出非空文件（$($c2.file.Length)B）"
$c2Cmd = (($c2.log -split "`n") | Where-Object { $_ -match '\[色彩\] cjxl 命令行' } | Select-Object -Last 1)
CK ($c2Cmd -and $c2Cmd -match 'color_space=sRGB') "(10) 目标语义由计划翻译成 cjxl 的 color_space（$c2Cmd）"
if ($c2.file -and (Test-Path $jxlinfo)) {
    $cs2b = JxlColorSpace $c2.file.FullName
    CK ($cs2b -match 'sRGB') "(10) jxlinfo 读回 sRGB（$cs2b）"
}
# ── (10c) HDR 目标：**必须**声明 --intensity_target（缺了 libjxl 回落默认峰值 ⇒ HDR 亮度错）──
$c3 = RunCli 'eng_cjxl_pq' $p3 'jxl' @('--color-space', '"BT.2020 PQ"')
CK ($c3.file -and $c3.file.Length -gt 0) "(10) BT.2020 PQ 目标产出非空文件（$($c3.file.Length)B）"
$c3Cmd = (($c3.log -split "`n") | Where-Object { $_ -match '\[色彩\] cjxl 命令行' } | Select-Object -Last 1)
CK ($c3Cmd -and $c3Cmd -match 'color_space=Rec2100PQ') "(10) HDR 目标落到 Rec2100PQ 枚举（$c3Cmd）"
CK ($c3Cmd -and $c3Cmd -match '--intensity_target=') `
   "(10) **HDR 目标必须声明 --intensity_target**（缺了 libjxl 回落默认峰值 ⇒ 亮度语义错）"
CK (($c3.log -split "`n") | Where-Object { $_ -match '\[色彩\] ffmpeg' -and $_ -match '-c:v ppm' }) `
   "(10) **HDR 目标仍走 PPM 管道**（PNG 中转只给 SDR：PNG 表达不了 PQ/HLG 语义，历史坑 JXL_ISSUES_HANDOVER P0-2）"
if ($c3.file -and (Test-Path $jxlinfo)) {
    $cs3b = JxlColorSpace $c3.file.FullName
    CK ($cs3b -match 'PQ') "(10) jxlinfo 读回 PQ 传递函数（$cs3b）"
}
# ── (10d) 带 alpha 的源：引擎 raw 通路无 alpha 平面 ⇒ 必须补第二输入（PAM），不得静默拍平 ──
# 素材现场生成（32x32 半透明），不依赖 ensure-fixtures（避免"缺素材 ⇒ SKIP"把这条悄悄放过）。
$alphaSrc = "$out/_alpha_src.png"
Start-Process -FilePath $ff -ArgumentList @('-y', '-hide_banner', '-loglevel', 'error', '-f', 'lavfi',
    '-i', 'color=c=red:s=32x32:d=1,format=rgba', '-vf', 'colorchannelmixer=aa=0.5', '-frames:v', '1', $alphaSrc) `
    -NoNewWindow -Wait -RedirectStandardOutput "$out/_alpha_gen.txt" -RedirectStandardError "$out/_alpha_gen.e" | Out-Null
if (Test-Path $alphaSrc) {
    $c4 = RunCli 'eng_cjxl_alpha' $alphaSrc 'jxl' @()
    CK ($c4.file -and $c4.file.Length -gt 0) "(10) 带 alpha 源产出非空文件（$($c4.file.Length)B）"
    $c4Cmd = (($c4.log -split "`n") | Where-Object { $_ -match '\[色彩\] ffmpeg' -and $_ -match 'image2pipe' } | Select-Object -Last 1)
    CK ($c4Cmd -and $c4Cmd -match '-c:v pam' -and $c4Cmd -match 'alphaextract' -and $c4Cmd -match 'alphamerge' -and $c4Cmd -match '\[1:v\]') `
       "(10) 透明源补了 alpha 平面（PAM + 第二输入 alphaextract/alphamerge）"
    if ($c4.file) {
        $aySrc = AlphaYMin $alphaSrc
        $ayOut = AlphaYMin $c4.file.FullName
        CK ($aySrc -gt 0 -and $ayOut -gt 0 -and $ayOut -lt 200) `
           "(10) 透明**未被静默拍平**（源 alpha YMIN=$aySrc，产物 YMIN=$ayOut；拍平会得 255）"
    }
} else {
    CK $false "(10) 生成 alpha 素材失败（$alphaSrc）—— 本条**不给绿灯**"
}
# ── (10e) 目标**落不进 cjxl 枚举** ⇒ 现在由**生成目标 ICC** 交付（2026-09-26 任务 #37 契约变更）──
# 旧现状（2026-09-17 登记）：`--color-space DCI-P3`（或 ProPhoto / 裸 BT.2020）在 jxl 上被规划层归一成
#   `bt2020/bt709`，而 cjxl 的 `-x color_space=` 枚举表达不了 ⇒ 出口**显式失败且不产出**。
#   当时那一条注释就预告过："若将来有人补上『为不可枚举目标生成目标 ICC』的能力，本段 ① 会转红
#   并提示该契约已变更"。**现在就是那一天**：真凶不是能力位 `CanGenerateIccFromCicp`（它与
#   `CanCarryArbitraryIcc` 是或关系，本就恒真），而是 `EnforceContainerAnnotationRules` 的**择一**——
#   它留着"容器写不出的 CICP"、剥掉了"唯一能兑现的生成 ICC"。择一加了一条前置：
#   **CICP 必须真的落得进本容器的枚举**（判据仍是同一个 `CapsAcceptsCicp`，不另立标准）。
# 本段因此改钉**新契约**，四条都要有牙：交付、语义可读回、机制点名、**旧拒绝文案不得复现**。
$c5 = RunCli 'eng_cjxl_dcip3' $p3 'jxl' @('--color-space', '"DCI-P3"')
CK ($c5.code -eq 0 -and $c5.file -and $c5.file.Length -gt 0) `
   "(10) 目标落不进 cjxl 枚举（DCI-P3 ⇒ bt2020/bt709）⇒ **现在必须交付**（exit=$($c5.code)，产物=$([bool]$c5.file)）—— 契约见本段头注"
if ($c5.file -and (Test-Path $jxlinfo)) {
    $cs5 = JxlColorSpace $c5.file.FullName
    CK ($cs5 -match 'Rec\.2100 primaries' -and $cs5 -match '709 transfer') `
       "(10) 且 cjxl 侧真的交付了该语义（jxlinfo 读回「$cs5」）⇒ 不是'出了个文件但标注丢了'"
} else { CK $false "(10) 无产物或无 jxlinfo ⇒ 语义无从读回，判红" }
CK ($c5.log -match 'icc_pathname') `
   "(10) 机制点名：cjxl 命令行带 -x icc_pathname=（生成目标 ICC 落盘）—— 否则这条交付无从复现"
CK ($c5.log -notmatch '落不进 cjxl 的 color_space 枚举') `
   "(10) 旧的显式拒绝**不得复现**（日志里再出现「落不进 cjxl 的 color_space 枚举」= 择一被改回原样）"
$c6 = RunCli 'eng_ffmpeg_dcip3' $p3 'jxl' @('--color-space', '"DCI-P3"', '-e', 'Ffmpeg')
CK ($c6.file -and $c6.file.Length -gt 0) `
   "(10) 交叉证明：**同一目标**走 ffmpeg-libjxl 后端也产出非空 jxl（$($c6.file.Length)B）⇒ 两条后端现在都走得通"
if ($c6.file -and (Test-Path $jxlinfo)) {
    $cs6 = JxlColorSpace $c6.file.FullName
    CK ($cs6 -match 'Rec\.2100 primaries' -and $cs6 -match '709 transfer') `
       "(10) 且 ffmpeg 侧真的交付了该语义（jxlinfo 读回「$cs6」）—— 证明目标本身可交付"
}
# ── (10f) PAM（含 alpha）+ `-x color_space=` 语义生效（2026-09-17 补覆盖）──
# 背景：raw 流下 cjxl 的 `-x color_space=` 只对 **PPM/PAM** 生效（容器输入会被忽略）。
# 已实测 **PPM**（10b/10c）生效；**PAM** 此前只有"间接"证据（alpha 用例的计划不要求标注 ⇒ 没给 `-x`）。
# 本段用「alpha 源 + 显式 Display P3」把 PAM 与 `-x color_space=` 同时压上：
#   源是无标签 PNG ⇒ 规划层按惯例假定 sRGB，目标 P3 ⇒ 计划是 **Map** 且要求写 CICP。
if (Test-Path $alphaSrc) {
    $c7 = RunCli 'eng_cjxl_pam_p3' $alphaSrc 'jxl' @('--color-space', '"Display P3"')
    CK ($c7.file -and $c7.file.Length -gt 0) "(10) PAM 用例产出非空 jxl（$($c7.file.Length)B）"
    $c7Cmd = (($c7.log -split "`n") | Where-Object { $_ -match '\[色彩\] cjxl 命令行' } | Select-Object -Last 1)
    CK ($c7Cmd -and $c7Cmd -match 'color_space=DisplayP3') "(10) PAM 用例的命令行确实带了「-x color_space=DisplayP3」"
    $c7Ff = (($c7.log -split "`n") | Where-Object { $_ -match '\[色彩\] ffmpeg' -and $_ -match 'image2pipe' } | Select-Object -Last 1)
    CK ($c7Ff -and $c7Ff -match '-c:v pam' -and $c7Ff -notmatch '-c:v ppm') `
       "(10) PAM 用例 ffmpeg 侧用的是「-c:v pam」（**不是** ppm）"
    if ($c7.file -and (Test-Path $jxlinfo)) {
        $cs7 = JxlColorSpace $c7.file.FullName
        CK ($cs7 -match 'P3 primaries') `
           "(10) **PAM 下「-x color_space=」确实生效**（第三方工具 jxlinfo 读回 P3，而非只看命令行）：$cs7"
        $h7 = JxlHeader $c7.file.FullName
        CK ($h7 -match 'Alpha') "(10) PAM 用例产物**仍含 alpha**（未因换 PAM 目标语义而丢通道）：$h7"
    }
    if ($c7.file) {
        $aySrc7 = AlphaYMin $alphaSrc
        $ayOut7 = AlphaYMin $c7.file.FullName
        CK ($aySrc7 -gt 0 -and $ayOut7 -gt 0 -and $ayOut7 -lt 200) `
           "(10) PAM 用例透明**未被拍平**（源 alpha YMIN=$aySrc7，产物 YMIN=$ayOut7）"
    }
}

# ── (10g) 自动光子噪声 × 引擎 cjxl 出口（2026-09-30：未解析的 "auto" 曾被当成真参数发出去）──
# 缺陷形状：cjxl 的 `--photon_noise_iso` 解析器是 `ParseFloat && >= 0`（libjxl
#   `tools/cjxl_main.cc:ParsePhotonNoiseParameter`），**没有 auto 这个取值**；而"从 EXIF 取 ISO"
#   这句当时内联在 `ProcessCjxlAsync` 里，`:714` 的分派写成 `backend == Cjxl && !engineRoutable`
#   ⇒ 引擎可走（= 默认路线）**根本进不到**它 ⇒ 占位串被当真参数发出，cjxl 在读 stdin 之前就退出 1、
#   零产物，写方只看到"管道正在被关闭"（实测取证 `tests/output/t62/`：A/D 臂 exit 1 + 0 B，
#   B/C/E 臂同参数只换掉该 flag ⇒ 正常出货，排除了 `-x color_space=` 这条备选解释）。
# 本段钉四件事：① 该组合出得来货；② 命令行里**没有 auto**；③ ISO 解析确实发生在**引擎路线**上；
#   ④ **正控**：数值 ISO + 有损时 flag 照发 —— 没有 ④，② 可能只是"整个功能没接上"造成的假绿。
$ph1 = RunCli 'eng_cjxl_photon_auto' $p3 'jxl' @('--cjxl-auto-photon-noise', 'true')
CK ($ph1.code -eq 0 -and $ph1.file -and $ph1.file.Length -gt 0) `
   "(10) auto 光子噪声 + 引擎 cjxl 出口产出非空 jxl（exit=$($ph1.code)，$($ph1.file.Length)B）"
$ph1Cmd = (($ph1.log -split "`n") | Where-Object { $_ -match '\[色彩\] cjxl 命令行' } | Select-Object -Last 1)
CK ($ph1Cmd -and $ph1Cmd -notmatch 'photon_noise_iso=auto') `
   "(10) 未解析的占位串**没有**被发给编码器（命令行：$ph1Cmd）"
CK ($ph1.log -match '自动光子噪声') `
   "(10) ISO 解析确实跑在**引擎路线**上（该行由分叉前的共用步骤打出，不再内联在 ProcessCjxlAsync）"
$ph2 = RunCli 'eng_cjxl_photon_iso' $p3 'jxl' @('--cjxl-photon-noise-iso', '400')
$ph2Cmd = (($ph2.log -split "`n") | Where-Object { $_ -match '\[色彩\] cjxl 命令行' } | Select-Object -Last 1)
CK ($ph2.file -and $ph2.file.Length -gt 0 -and $ph2Cmd -match '--photon_noise_iso=400') `
   "(10) **正控**：有损 + 数值 ISO ⇒ flag 照发且产物非空（命令行：$ph2Cmd）"
# 无损与光子噪声互斥。实测（891x981 16-bit，同 effort）：`-d 0` 往返逐字节相同；
# `-d 0 --photon_noise_iso=400` ⇒ **99.46%** 的 16-bit 样本偏离源（平均偏差 30875/65535），
# 而 jxlinfo 对两者都报 "(possibly) lossless" —— 噪声由解码端按帧头参数合成，体积只多 10 B，
# 用文件大小根本查不出来 ⇒ 这个组合不发给编码器，且**点名**丢弃。
$ph3 = RunCli 'eng_cjxl_photon_lossless' $p3 'jxl' @('--lossless', 'true', '--cjxl-photon-noise-iso', '400')
$ph3Cmd = (($ph3.log -split "`n") | Where-Object { $_ -match '\[色彩\] cjxl 命令行' } | Select-Object -Last 1)
CK ($ph3.file -and $ph3.file.Length -gt 0 -and $ph3Cmd -match '-d 0' -and $ph3Cmd -notmatch 'photon_noise_iso') `
   "(10) 无损 ⇒ 命令行只有 -d 0、**不带** --photon_noise_iso（叠加会静默破坏逐比特无损）"
CK ($ph3.log -match 'photon noise skipped') `
   "(10) 被丢弃的用户参数**点名**（不静默吞设置）"
# ── (10h) 双进程管道的统一卫生要求（结构锁，进程→进程的每一条都要过）──
# 动机：cjxl 提前退出后，写侧 ffmpeg 会**永久堵在无人读的 stdout 上**（实测卡 25 分钟直到父进程句柄
#   关闭），而等待用的 token 只有 30 分钟超时会触发 ⇒ 用户停在「处理中」，真因被埋在后面的 stderr 行里。
#   这一格**没有用户可达的行为判据**（要复现就得让真编码器在读完 stdin 前死掉，而那正是被修掉的东西），
#   所以钉**结构**：谁把 Kill 那几行删了、或新加一条管道没带这套要求，本段立刻红。
# ⚠ 先剥整行注释再匹配（注释里出现过 `pa.Kill` 字样，会造假命中）。
function Strip-LineComments10([string]$text) {
    return ((@($text -split "`r?`n") | ForEach-Object { ($_ -replace '//.*$', '') }) -join "`n")
}
# 表 = 全仓**进程→进程**的管道（写侧变量名即该条的判据锚）
$pipeSites = @(
    @{ f='src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs'; m='PipeToEncoderAsync'; w='pa'; x='(?s)管道传输失败.{0,420}encErr\.Trim\(\)' },
    @{ f='src/FfmpegGui/Services/JxlPipelineService.cs';            m='TryPipeDjxlToCjpegliAsync';         w='procDj' },
    @{ f='src/FfmpegGui/Services/QueueProcessor.cs';                m='PipeFfmpegToExternalEncoderAsync';  w='procFf' },
    @{ f='src/FfmpegGui/Services/QueueProcessor.cs';                m='PipeDjxlToCjxlAsync';               w='procDj' },
    @{ f='src/FfmpegGui/Services/QueueProcessor.cs';                m='PipeDjxlToFfmpegAsync';             w='procDj' }
)
# **发现闸**：条数由源码现量，不靠人记得改表 ⇒ 新增一条进程→进程管道而没入表，这里先红。
# 只数"目标是别人的 stdin"的那些；写进 MemoryStream/FileStream 的不算（不存在无人读管道这一死法）。
$pipeAll = ''
foreach ($cs in (Get-ChildItem -Path "$root/src" -Recurse -Filter *.cs |
                 Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
    $pipeAll += (Strip-LineComments10 ([IO.File]::ReadAllText($cs.FullName))) + "`n"
}
$pipeFound = @([regex]::Matches($pipeAll, '(?s)StandardOutput\.BaseStream\.CopyToAsync\(\s*\w+\.StandardInput'))
CK ($pipeFound.Count -eq $pipeSites.Count) `
   "(10) 进程→进程管道条数 == 结构锁表长（实得 $($pipeFound.Count)，表 $($pipeSites.Count)）⇒ 新增管道必须先入表并带齐三件"
foreach ($s in $pipeSites) {
    $txt = Strip-LineComments10 ([IO.File]::ReadAllText("$root/$($s.f)"))
    # 锚**定义**而不是首次出现：这三条管道在 QueueProcessor 里都先被调用再定义，
    # 拿 IndexOf("Name(") 会锚到调用点 ⇒ 切出来的是别的方法的代码 ⇒ 三项判据一起 False（假红）。
    $mm = [regex]::Match($txt, "(?m)^[^\S\r\n]*(private|public|internal)[^\r\n]*\b$($s.m)\b[^\r\n]*\(")
    if (-not $mm.Success) { CK $false "(10h) 定义锚点丢失：$($s.m)（被改名/删掉，判据不再覆盖它）"; continue }
    $open = $txt.IndexOf('{', $mm.Index)
    if ($open -lt 0) { CK $false "(10h) $($s.m)：找不到方法体起点"; continue }
    $depth = 0; $end = $txt.Length
    for ($k = $open; $k -lt $txt.Length; $k++) {
        $ch = $txt[$k].ToString()
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') { $depth--; if ($depth -eq 0) { $end = $k; break } }
    }
    $body = $txt.Substring($mm.Index, $end - $mm.Index)
    $cap = $body -match 'transferError = ex'
    $kill = $body -match "(?s)if \(transferError != null\).{0,240}$($s.w)\.Kill\("
    # 成功判据：要么条件里带 `transferError == null`，要么显式 throw —— 二者皆无 = 半截流能被判成功
    $gate = ($body -match 'transferError == null') -or ($body -match '(?s)if \(transferError != null\)[\s\S]{0,520}?throw')
    # 表项可带 x = 该条独有的附加要求（引擎出口那条：异常文本必须含编码器 stderr 原话）
    $extra = $true
    if ($s.x) { $extra = ($body -match $s.x) }
    CK ($cap -and $kill -and $gate -and $extra) `
       "(10h) $($s.m)：捕获=$cap 杀写侧($($s.w))=$kill 成功判据含传输失败=$gate 附加=$extra"
}

# ── (10j) 交付样本深度：外部编码器出口不得把「中间件精度」当「交付深度」（2026-10-01）──
# 实测：8-bit 源经引擎出口时，cjxl 出口曾喂 16-bit PPM（同一份像素 +76%，比源 PNG 还大）、
#   JXR 出口曾落 16-bit TIFF（877.2 kB / rgb48le）。png/tiff 出口一直是对的（它们不由本字段决定交付）。
# 判据用**第三方读回的产物 pix_fmt**，不看自家日志；并配 16-bit 源的正控，
# 否则"恒 8-bit"也能骗过前两条。
$p8src = "$out/_d8.png"; $p16src = "$out/_d16.png"
Start-Process -FilePath $ff -ArgumentList @('-y','-hide_banner','-loglevel','error','-f','lavfi',
    '-i','gradients=s=240x180:c0=0x305090:c1=0xd0a040:n=5,format=rgb24','-frames:v','1',$p8src) `
    -NoNewWindow -Wait -RedirectStandardOutput "$out/_dgen.txt" -RedirectStandardError "$out/_dgen.e" | Out-Null
Start-Process -FilePath $ff -ArgumentList @('-y','-hide_banner','-loglevel','error','-f','lavfi',
    '-i','gradients=s=240x180:c0=0x305090:c1=0xd0a040:n=5,format=rgb48le','-frames:v','1',$p16src) `
    -NoNewWindow -Wait -RedirectStandardOutput "$out/_dgen2.txt" -RedirectStandardError "$out/_dgen2.e" | Out-Null
function PixOf([string]$path) {
    $o = "$out/_pix.txt"
    Start-Process -FilePath $fp -ArgumentList @('-v','error','-select_streams','v:0','-show_entries',
        'stream=pix_fmt','-of','csv=p=0', $path) -NoNewWindow -Wait -RedirectStandardOutput $o | Out-Null
    return ([System.IO.File]::ReadAllText($o)).Trim()
}
if ((Test-Path $p8src) -and (Test-Path $p16src)) {
    $d1 = RunCli 'depth_jxl8' $p8src 'jxl' @('--lossless','true')
    $pixJ8 = if ($d1.file) { PixOf $d1.file.FullName } else { '<无产物>' }
    CK ($pixJ8 -match '^rgb24$') "(10j) 8-bit 源 → JXL 无损交付 rgb24（不是 rgb48le 载体）：$pixJ8"
    $d2 = RunCli 'depth_jxr8' $p8src 'jxr' @('--lossless','true')
    $pixX8 = if ($d2.file) { PixOf $d2.file.FullName } else { '<无产物>' }
    CK ($pixX8 -match 'bgr24|rgb24') "(10j) 8-bit 源 → JXR 交付 8-bit（曾为 rgb48le / +369%）：$pixX8"
    $d3 = RunCli 'depth_jxl16' $p16src 'jxl' @('--lossless','true')
    $pixJ16 = if ($d3.file) { PixOf $d3.file.FullName } else { '<无产物>' }
    CK ($pixJ16 -match '48') "(10j) **正控**：16-bit 源仍按 16-bit 交付（证明上两条不是恒 8-bit）：$pixJ16"
} else { CK $false "(10j) 位深夹具生成失败 ⇒ 本段三条不给绿灯" }

# ── (11) JXR 执行出口（2026-09-17 接入）──
# 背景：`JxrEncApp` 是**纯文件式** `-i input -o output`（不吃 rawvideo/stdin）。⚠ 本段初稿把「不走 ffmpeg
# 编码」的理由写成「本工具链的 ffmpeg 没有 jxr 编码器」—— **已按实测改正**（09-26 构建 `-codecs` 报
# `(encoders: libjxr)`，取证 `tests/output/t50/`）：libjxr **在**，但它**无质量 AVOption** 且字节序与
# jxrlib 不一致 ⇒ 不作替代执行体（唯一口径见 `RawColorPipeline` 的 JXR 出口文档）。⇒ 引擎只能把变换后的
# raw 落成中转容器（BMP/TIFF）再交给 JxrEncApp。
# 本段钉住四件事：① 出口真的产出**真 JXR**（由**独立解码器** JxrDecApp 回读，不看自家日志）；
# ② 色彩决策来自**计划**而不是那条硬编码 zscale；③ alpha **没被静默拍平**（同 GIF/cjxl 的坑）；
# ④ 中转容器由**计划位深**决定。
#
# ⚠ **中转容器的判据是 `plan.OutBitDepth`，不是源位深**（2026-09-17 更正：本段初稿按「8-bit 源 ⇒ BMP」
#   写，在真实链路上必红）。依据两条：
#     · `JxrEncApp` 自述 `bmp: <=8bpc, BGR` / `tif: >=8bpc, RGB` ⇒ 48-bit 中转**只能**走 TIFF，BMP 装不下；
#     · 计划层规则「直通/携带绝不降位」（`ColorStrategyPlanning.cs:245-252`）⇒ 8-bit **源**的直通用例
#       出口位深仍是 16 ⇒ TIFF。这与 legacy 同一请求一致（legacy 也是 `rgb48le` TIFF），**不是回归**。
#   8-bit 出口只有 `--bit-depth=8` 才出现（Map + Clipping + TargetBitDepth<=8 + RgbNative），见 (11b)。
Write-Host "`n=== (11) JXR 执行出口：BMP/TIFF 中转 + JxrEncApp ===" -ForegroundColor Cyan
$jxrEnc = "$root/publish/PLAN/artifacts/JxrEncApp.exe"
$jxrDec = "$root/publish/PLAN/artifacts/JxrDecApp.exe"
# jxr 回读**一律经 JxrDecApp**，不用 ffmpeg 自解。⚠ 理由不是「本构建没有 jxr 解码器」（实测**有**：
# `-codecs` 报 `(decoders: libjxr)`，且 8/16-bit 往返位精确）而是两条：① 门禁要的是**第三方**证明
# 「这是真 JXR」，自家链路自解会把「写法自洽」冒充成「格式正确」；② ffmpeg 解 .jxr **不导出 ICC**
# ⇒ ICC 只能走 exiftool（`tests/output/t50/` 实测）。
function JxrDec([string]$jxrPath, [string]$outPath, [string]$fmt) {
    Start-Process -FilePath $jxrDec -ArgumentList @('-i', $jxrPath, '-o', $outPath, '-c', $fmt) `
        -NoNewWindow -Wait -RedirectStandardOutput "$out/_jxrdec.txt" | Out-Null
    return (Test-Path $outPath)
}
function JxrPixelFormat([string]$jxrPath) {
    Start-Process -FilePath $et -ArgumentList @('-a', '-s', '-G1', '-PixelFormat', $jxrPath) `
        -NoNewWindow -Wait -RedirectStandardOutput "$out/_jxrpf.txt" | Out-Null
    return ([System.IO.File]::ReadAllText("$out/_jxrpf.txt")).Trim()
}
if ((Test-Path $jxrEnc) -and (Test-Path $jxrDec)) {
    $cmjxrBefore = @(Get-ChildItem $env:TEMP -Directory -Filter 'cmjxr_*' -EA SilentlyContinue).Count
    # ── (11a) 直通源（8-bit，无 ICC）⇒ **交付**按源档 8bit ⇒ BMP 中转 ──
    # ⚠ 2026-09-30 契约变更（原判据在此处已过期，逐字记下免得再被改回去）：原先钉的是
    #   「中转恒为 TIFF + `-pix_fmt rgb48le`，因为计划『直通/携带绝不降位』⇒ 出口位深 16、不由源位深决定」。
    #   那句话把**中间件精度**当成了**交付深度**：同一份 8-bit 像素按 16-bit 交付实测 151,899 B，
    #   按 8-bit 交付 30,925 B（**4.91×**，本轮取证 `tests/output/t74/jxrcodes.ps1`），而两侧
    #   JxrDecApp 独立回读**都能解出** ⇒ 像素没变、只是载体白涨。
    #   现在交付档取自 `plan.TargetBitDepth`；『绝不降位』那条约束仍然管**中间件**
    #   （同一行的 `-f rawvideo -pix_fmt rgb48le` 必须继续成立）⇒ 两条一起钉，**判据只增不减**。
    $x1 = RunCli 'eng_jxr_sdr' $sdr 'jxr' @()
    CK ($x1.file -and $x1.file.Length -gt 0) "(11) JXR 出口产出非空 .jxr（$($x1.file.Length)B）"
    $x1ff = (($x1.log -split "`n") | Where-Object { $_ -match '\[色彩\] ffmpeg' -and $_ -match 'rawvideo' } | Select-Object -Last 1)
    CK ($x1ff -and $x1ff -match 'in\.bmp' -and $x1ff -match '-pix_fmt rgb24 ') `
       "(11) 8-bit 直通源 ⇒ **BMP** 中转 + 交付 `-pix_fmt rgb24`（交付档取自计划的目标位深，不再取自中间件档）"
    CK ($x1ff -and $x1ff -match '-f rawvideo -pix_fmt rgb48le') `
       "(11) 同一行的**中间件**仍是 rgb48le ⇒ 『直通绝不降位』没有被削弱，改的只是出口交付档"
    CK ($x1ff -and $x1ff -notmatch 'in\.tiff') `
       "(11) 该用例**不是** TIFF（TIFF 只属于 ≥8bpc 交付；两档同时出现=交付档被两处独立推导）"
    # ⚠ 日志文案必须说**实际写的那个容器**：本仓出过一次「日志 → TIFF、命令行 in.bmp」的假文案
    #   （2026-10-01 打包冒烟抓到，根因是文案读 `out8`（中间件档）而容器读 `deliver8`（交付档））。
    $x1s = (($x1.log -split "`n") | Where-Object { $_ -match 'JXR 出口：变换后的 raw' } | Select-Object -Last 1)
    CK ($x1s -and $x1s -match '→ BMP') `
       "(11) 播报的中转容器与命令行一致（实得「$($x1s -replace '^\s*\[色彩\]\s*','')」；旧文案在此处写 TIFF）"
    CK ($x1ff -and $x1ff -notmatch 'zscale') `
       "(11) 出口的 ffmpeg 命令行**不含 zscale** ⇒ 色彩不再由硬编码滤镜决定（原 ProcessJxrAsync 的 :1783 那条）"
    CK ($x1.log -match '\[色彩\] JxrEncApp 命令行:') "(11) 命令行有可追溯的「JxrEncApp 命令行:」标签（A/B 对拍用）"
    # ── (11a2) 显式 `--bit-depth 16` ⇒ 交付升档 ⇒ TIFF 中转 + 48-bit 回读（两档各自可达，不是死分支）──
    # ⚠ BMP 侧写 `-pix_fmt rgb24`、TIFF 侧写 `rgb48le`：**判据必须按交付档分叉**，
    #   否则 8-bit 交付的产物会被 B 侧字节序/档位判成另一种东西（旧判据 `-match '-pix_fmt rgb48le'`
    #   在两条臂上都成立 —— 那行的**输入侧**本来就是 rgb48le，这是一条恒真断言，已作废）。
    $x1t = RunCli 'eng_jxr_tiff16' $sdr 'jxr' @('--bit-depth', '16')
    $x1tff = (($x1t.log -split "`n") | Where-Object { $_ -match '\[色彩\] ffmpeg' -and $_ -match 'rawvideo' } | Select-Object -Last 1)
    CK ($x1tff -and $x1tff -match 'in\.tiff' -and $x1tff -notmatch 'in\.bmp') `
       "(11) --bit-depth 16 ⇒ **TIFF** 中转（JxrEncApp 自述 bmp: <=8bpc ⇒ 48-bit 中转只能走 TIFF）"
    if ($x1.file -and $x1t.file) {
        CK ($x1.file.Length -lt $x1t.file.Length) `
           "(11) 同一素材：8-bit 交付 $($x1.file.Length) B < 16-bit 交付 $($x1t.file.Length) B ⇒ 载体档位不再白涨（旧行为下两格同为 16-bit，本条会红）"
    } else {
        CK $false "(11) 两档臂缺一（8bit=$([bool]$x1.file) 16bit=$([bool]$x1t.file)）⇒ 上面那条体积判据无从对比，判红"
    }
    if ($x1.file) {
        # **第三方回读**：用独立解码器证明产物是真 JXR，且位深/通道与中转一致
        $ok1 = JxrDec $x1.file.FullName "$out/_x1_back.bmp" '0'
        CK $ok1 "(11) **独立解码器 JxrDecApp 能解出 8-bit 产物**（`-c 0` = 24bppBGR，码表取自工具自己的 usage）"
        $pf1 = JxrPixelFormat $x1.file.FullName
        CK ($pf1 -match '24-bit BGR') "(11) 8-bit 交付的容器像素格式回读为 24-bit BGR（实得「$pf1」）"
    }
    if ($x1t.file) {
        $ok1t = JxrDec $x1t.file.FullName "$out/_x1t_back.tif" '10'
        CK $ok1t "(11) 16-bit 交付产物可被 JxrDecApp `-c 10`（48bppRGB）独立解出"
        # 负控：同一产物**不得**被 alpha 码解出 ⇒ 上面那条"能解"不是"随便给个码都过"
        $neg1t = JxrDec $x1t.file.FullName "$out/_x1t_neg22.tif" '22'
        CK (-not $neg1t) "(11) 负控：48-bit 产物**不得**被 `-c 22`（32bppBGRA）解出 ⇒ 上一条的成功有判别力"
        $pf1t = JxrPixelFormat $x1t.file.FullName
        CK ($pf1t -match '48-bit RGB') "(11) 16-bit 交付的容器像素格式回读为 48-bit RGB（实得「$pf1t」）"
    }
    # ── (11b) 显式 `--bit-depth=8` 的 P3 源（Map + Clipping ⇒ TargetBitDepth=8）──
    # ⚠ 2026-09-30：本臂原来的职责是「BMP 分支不是死代码」的唯一证据；交付档改按目标位深后，
    #   **默认**臂 (11a) 也落进 BMP ⇒ 本臂的职责换成「**显式请求** 8bit 与默认推断 8bit 走同一条出口判据」
    #   （两者产物档位必须一致，否则说明有第二条独立推导路径）。16-bit 那一侧由 (11a2) 钉。
    # ⚠ 断言的是 `-pix_fmt rgb24` 而**不是** `bgr24`：出口对两条分支都用「变换后 raw 的像素格式」
    #   （`pixOut = pixIn`），BMP 的 BGR 字节序由 ffmpeg 的 BMP 编码器负责；这一点由**独立回读**
    #   （exiftool `PixelFormat : 24-bit BGR` + JxrDecApp `-c 0` 解码）钉住，比断言 ffmpeg 的入参更硬。
    #   legacy 侧写的是 `bgr24`，两者产物字节序一致（BMP 只有一种字节序），不构成差异。
    $x1b = RunCli 'eng_jxr_bmp8' $p3 'jxr' @('--bit-depth=8')
    $x1bff = (($x1b.log -split "`n") | Where-Object { $_ -match '\[色彩\] ffmpeg' -and $_ -match 'rawvideo' } | Select-Object -Last 1)
    CK ($x1bff -and $x1bff -match 'in\.bmp' -and $x1bff -match '-pix_fmt rgb24') `
       "(11) 用 --bit-depth=8 ⇒ 计划出口位深 8 ⇒ **BMP** 中转（BMP 分支确实可达，不是死代码）"
    if ($x1b.file) {
        $ok1b = JxrDec $x1b.file.FullName "$out/_x1b_back.bmp" '0'
        CK $ok1b "(11) 8-bit 产物可被 JxrDecApp `-c 0` 独立解出"
        $pf1b = JxrPixelFormat $x1b.file.FullName
        CK ($pf1b -match '24-bit BGR') "(11) 8-bit 产物容器像素格式回读为 24-bit BGR（实得「$pf1b」）"
    }
    # ── (11c) 色彩决策来自计划（P3 源 ⇒ Map 到 sRGB），而不是硬编码 bt2020 ──
    $x2 = RunCli 'eng_jxr_p3' $p3 'jxr' @()
    CK ($x2.log -match '\[色彩引擎\] 决策：Map' -and $x2.log -match '目标=sRGB') `
       "(11) P3 源 + jxr：决策由**计划**给出（Map → sRGB），而不是硬编码把像素当 Rec.2020"
    # 对照：legacy 同一请求仍走**专用 JXR 管线**。
    # ⚠ 判据**不能**是 `[cmd] ffmpeg …`：legacy 的 `ProcessJxrAsync` 从不记这行（只写 `item.Command`）
    #   ⇒ 按那个判据取到的永远是空串。本段初稿就写错了这一点，红出来的现象会被误读成「legacy 被改动了」。
    #   也**不能**用 `[jxr] JxrEncApp.exe`：`JxrService.RunAsync` 两侧都记（引擎出口内部也调它）。
    #   真正两侧互斥的只有 `[jxr] Step 1:`（legacy 专用）与 `[色彩] JxrEncApp 命令行:`（引擎出口）。
    $x2leg = RunCli 'leg_jxr_p3' $p3 'jxr' @('--color-engine', 'legacy')
    CK ($x2leg.log -match '\[jxr\] Step 1:' -and $x2leg.log -notmatch '\[色彩\] JxrEncApp 命令行:') `
       "(11) 对照：legacy 同一请求仍走**专用 JXR 管线**（有「[jxr] Step 1」、无引擎出口标签）"
    CK ($x2.log -notmatch '\[jxr\] Step 1:') `
       "(11) 对照：引擎用例**没有** legacy 的「[jxr] Step 1」⇒ 两条路确实不同、本段的断言不是空转"
    # ⚠ **2026-09-18 行为变更（P9 口径回填，见 §6）**：此处原断言是
    #   「源标 smpte432 ⇒ 中转 TIFF 被改标 bt709」—— 它钉住的是**已被推翻**的旧策略
    #   （对**非 RAW** 的 >8bit 输入一律硬按 Rec.2020 转换 + 改标）。
    #   现在 legacy 不再做该硬编码转换 ⇒ 中转 TIFF **保留源标注**（smpte432）。
    #   ⚠ 这**不是**"放宽断言"：旧断言钉的是**错的**行为；新断言钉**改后**的行为，
    #     且同一概念另加"命令行**不含**该滤镜"的直接判据（见下）⇒ 判据只增不减。
    CK ($x2leg.log -match 'Video: png, rgb48be\(pc, gbr/smpte432' `
        -and $x2leg.log -match 'Video: tiff, rgb48le\(pc, gbr/smpte432') `
       "(11) legacy 不再硬编码改标（源 smpte432 ⇒ 中转 TIFF **保持** smpte432，不再被改标 bt709）"
    # **直接判据**：legacy 的 `ProcessJxrAsync` **真的记**了这条命令行（2026-09-17 补）——
    # 此前它只写 `item.Command`（UI 字段，**不进日志**）⇒ 门禁按 `[cmd] ffmpeg` 去取只能拿到空串。
    # ⚠ 方向已于 2026-09-18 **反转**：该滤镜**不该再出现**。与上面那条（间接：看中转 TIFF 的标注）
    #   仍是交叉验证：只留间接 ⇒ 换一种实现也可能碰巧标对；只留直接 ⇒ 只证明命令行里没有它，
    #   不证明标注没被动过。**两条都保留**。
    $x2ff = @(($x2leg.log -split "`n") | Where-Object { $_ -match '\[cmd\] ffmpeg' -and $_ -match 'zscale=primariesin=bt2020' })
    CK ($x2ff.Count -eq 0) `
       "(11) legacy 的 ffmpeg 命令行**不再**含 zscale=primariesin=bt2020（实测 $($x2ff.Count) 条；2026-09-18 之前为 ≥1）"
    # ── (11d) alpha 未拍平（与 GIF/cjxl 同款坑）──
    if (Test-Path $alphaSrc) {
        $x3 = RunCli 'eng_jxr_alpha' $alphaSrc 'jxr' @()
        $x3ff = (($x3.log -split "`n") | Where-Object { $_ -match '\[色彩\] ffmpeg' -and $_ -match 'rawvideo' } | Select-Object -Last 1)
        CK ($x3ff -and $x3ff -match 'alphaextract' -and $x3ff -match 'alphamerge') `
           "(11) alpha 源：中转用第二输入补 alpha 平面（alphaextract/alphamerge），不是直接喂 rgb"
        if ($x3.file) {
            $pf3 = JxrPixelFormat $x3.file.FullName
            CK ($pf3 -match 'RGBA|BGRA') `
               "(11) **产物容器真的含 alpha**（exiftool 回读 PixelFormat=「$pf3」）—— 不是只看命令行"
            # ⚠ 解码码**必须由产物的 PixelFormat 决定**，不能写死 `-c 23`：
            #   23=64bppRGBA、22=32bppBGRA（JxrDecApp 自己的 usage 表）。交付档改成跟随目标位深后，
            #   8-bit alpha 源出的是 32bppBGRA ⇒ 写死 23 会 exit=-106，读起来像"产物坏了"。
            #   （同族坑已由 `verify-jxr-anim-input.ps1:244` 记过一笔：判据吃的是档位不是意图。）
            $code3 = ''; $ext3 = ''
            if ($pf3 -match '64-bit RGBA') { $code3 = '23'; $ext3 = 'tif' }
            elseif ($pf3 -match '32-bit BGRA') { $code3 = '22'; $ext3 = 'bmp' }
            if ($code3 -eq '') {
                CK $false "(11) alpha 产物的 PixelFormat「$pf3」不在已知码表内 ⇒ 无法选解码码，判红（不放行）"
            } else {
                $back3 = "$out/_x3_back.$ext3"
                $ok3 = JxrDec $x3.file.FullName $back3 $code3
                if ($ok3) {
                    # 源与产物在**同一转换口径**（rgba64le）下比 alpha 的 YMIN：相等 ⇒ 未被拍平
                    $ayS = AlphaYMin16 $alphaSrc
                    $ayP = AlphaYMin16 $back3
                    CK ($ayS -gt 0 -and $ayP -gt 0 -and $ayP -eq $ayS) `
                       "(11) 透明**未被拍平**（同一 16-bit 口径下源 alpha YMIN=$ayS，产物=$ayP；解码码 $code3 由 $pf3 选出）"
                } else { CK $false "(11) alpha 产物 JxrDecApp `-c $code3` 回读失败（无法判定是否拍平）" }
            }
            # ── (11d2) 同一 alpha 源显式 `--bit-depth 16` ⇒ 必须升到 64bppRGBA（两档都可达）──
            $x3t = RunCli 'eng_jxr_alpha16' $alphaSrc 'jxr' @('--bit-depth', '16')
            if ($x3t.file) {
                $pf3t = JxrPixelFormat $x3t.file.FullName
                CK ($pf3t -match '64-bit RGBA') `
                   "(11) --bit-depth 16 的 alpha 交付回读为 64-bit RGBA（实得「$pf3t」）⇒ 升档臂不是死分支"
                CK ($x3t.file.Length -gt $x3.file.Length) `
                   "(11) 同一 alpha 素材：8-bit 交付 $($x3.file.Length) B < 16-bit 交付 $($x3t.file.Length) B（体积差由交付档解释，不是编码器换档）"
                $ok3t = JxrDec $x3t.file.FullName "$out/_x3t_back.tif" '23'
                CK $ok3t "(11) 64bppRGBA 产物可被 `-c 23` 独立解出"
            } else { CK $false "(11) 升档 alpha 臂无产物 ⇒ 上面两条无从判定，判红" }
        }
    }
    # ── (11e) P9 口径回填：**无标签** 16-bit 不得被硬按 Rec.2020 解读（2026-09-18）──
    # 缺陷：`ProcessJxrAsync` 的解码段曾对**所有**「isHdr 且无显式色彩参数」的输入无条件套
    #   `zscale=primariesin=bt2020:…` —— 把「**没有标签**」硬猜成 Rec.2020 并**真的转换像素**，
    #   而用户完全看不出（exit 0、状态「已完成」）。同文件下游的 cjxl/cjpegli 管道段早有正确口径
    #   （**仅 RAW 来源**才做该猜测）⇒ 本轮回填，同一个判据在同一个文件里不再有两个方向。
    # ⚠ 前提自检（先断言素材形态，否则断言毫无意义）：素材必须**确实无色彩标签**。
    $p9src = "$root/tests/output/sources/src_16bit.png"
    if (-not (Test-Path $p9src)) {
        CK $false "(11e) 缺素材 tests/output/sources/src_16bit.png（由 generate-sources.ps1 生成）—— 本条**不给绿灯**"
    } else {
        $p9tags = Cicp $p9src
        CK ($p9tags -match 'unknown' -and $p9tags -notmatch 'bt709|bt2020|smpte|iec61966') `
           "(11e) 前提自检：素材确实**无色彩标签**（ffprobe primaries/transfer = 「$p9tags」）"
        $x5 = RunCli 'leg_jxr_p9' $p9src 'jxr' @('--color-engine', 'legacy')
        # 先钉「真的走了 legacy 专用 JXR 解码段」：否则「命令行不含某串」可能只是因为**根本没跑这条路**
        # ⇒ 断言恒真（与 §6 第 74 条「载体选错 ⇒ 断言恒真」同族）。
        CK ($x5.log -match '\[jxr\] Step 1:') `
           "(11e) 该用例**确实**走 legacy 专用 JXR 解码段（有「[jxr] Step 1」）"
        $x5ff = @(($x5.log -split "`n") | Where-Object { $_ -match '\[cmd\] ffmpeg' -and $_ -match 'primariesin=bt2020' })
        CK ($x5ff.Count -eq 0) `
           "(11e) 无标签 16-bit → legacy JXR：ffmpeg 命令行**不得**含 zscale=primariesin=bt2020（实测 $($x5ff.Count) 条）"
        CK ($x5.log -match 'Video: tiff, rgb48le\(pc, gbr/unknown') `
           "(11e) 中转 TIFF 保持**无标签**（不被改标 bt709；原实现会标 bt709）"
        CK ($x5.file -and $x5.file.Length -gt 0) `
           "(11e) 仍须产出非空 .jxr（不转换 ≠ 不产出；实测 $(if($x5.file){$x5.file.Length}else{0})B）"
        # ⚠ **负控（RAW ⇒ 该串必须出现）在本仓结构性不可达，故未实测** —— 两条原因均已实测/读码确认：
        #   ① RAW 输入在更上游已被 dngtool 预解码成 `*_raw.tiff` 并改写 `captured.InputPath`
        #      ⇒ 到这里 `RawService.IsRawFile(item.InputPath)` 恒为 false；
        #   ② 那次预处理把 ColorPrimaries/ColorTrc/UseAdvancedColorParameters 置为 bt709/linear/true
        #      ⇒ `colorArgs` **非空** ⇒ 外层 `if (isHdr && …WhiteSpace(colorArgs))` 根本进不来。
        #   实测 `raw_lossy.dng --format jxr --color-engine legacy` 的命令行是
        #   `-color_primaries bt709 -color_trc linear -i "…_raw.tiff" …`，**无 -vf**。
        #   ⇒ 不写一条"RAW 也必须含该串"的断言（那是一条**必红**的断言）；如实登记为不可达。
    }
    $cmjxrAfter = @(Get-ChildItem $env:TEMP -Directory -Filter 'cmjxr_*' -EA SilentlyContinue).Count
    CK ($cmjxrAfter -le $cmjxrBefore) `
       "(11) 中转目录已清理（跑前 $cmjxrBefore 个 / 跑后 $cmjxrAfter 个；用 File.Delete，Remove-Item 对仓外路径静默无效）"
} else {
    CK $false "(11) 缺 JxrEncApp/JxrDecApp（publish/PLAN/artifacts）—— 本条**不给绿灯**"
}

# ── (12) 取向标签自洽：像素被旋正 ⇒ 源的 Orientation 必须丢（2026-10-01 补，实测回归）──────
# 钉住的缺陷是**本仓自己引入的**：09-30 把 jbrd 开关默认翻成 true 之后，
#   `CjxlService.UsesLosslessJpegRewrap`（当时只与到"输入是 JPEG"，没有目标格式条件）同时被
#   元数据恢复当作"这次像素没动"的唯一判据 ⇒ JPEG→JPEG/PNG/AVIF/WebP 也把 Orientation=8
#   留在了**已被解码器旋正**的像素上 ⇒ 查看器二次旋转。
# 修前（同一出货包、同一素材，`tests/output/t76/orientation-rewrap.ps1`）：四格 coded=480,640
#   且 Orientation=8 ⇒ 全中；修后四格 Orientation 均被丢弃，而 jxl(jbrd) 仍带走取向。
Write-Host "`n=== (12) 取向标签 × 免解码重封装：产物自洽性 ===" -ForegroundColor Cyan
$orIn = "$out/orient_in.jpg"
$null = & $ff -y -hide_banner -loglevel error -f lavfi -i "gradients=s=640x480:c0=0x204060:c1=0xc07030:n=5,format=rgb24" -frames:v 1 -q:v 3 "$orIn" 2>&1
if ((Test-Path $orIn) -and (Test-Path $et)) {
    $null = & $et -overwrite_original -n "-EXIF:Orientation=8" "$orIn" 2>&1
    function OrientOf([string]$p) {
        $o = "$out/_or_$([guid]::NewGuid().ToString('N').Substring(0,6)).txt"
        $null = Start-Process -FilePath $et -ArgumentList @('-n','-s3','-EXIF:Orientation',$p) -NoNewWindow -Wait -RedirectStandardOutput $o
        $v = ([System.IO.File]::ReadAllText($o)).Trim()
        if ($v -eq '') { return '<无>' }
        return $v
    }
    function CodedOf([string]$p) {
        $o = "$out/_px_$([guid]::NewGuid().ToString('N').Substring(0,6)).txt"
        $null = Start-Process -FilePath $fp -ArgumentList @('-v','error','-select_streams','v:0','-show_entries','stream=width,height','-of','csv=p=0',$p) -NoNewWindow -Wait -RedirectStandardOutput $o
        return (([System.IO.File]::ReadAllText($o)) -split "`r?`n" | Where-Object { $_ -match '^\s*\d+\s*,\s*\d+\s*$' } | Select-Object -First 1)
    }
    $oTag0 = OrientOf $orIn; $oDim0 = CodedOf $orIn
    CK (($oTag0 -eq '8') -and ($oDim0 -replace '\s','' -eq '640,480')) `
       "(12) 夹具自检：源 coded=640x480 且 Orientation=8（不成立则本段无从判定）：实得 coded=$oDim0 tag=$oTag0"
    # 像素被旋正的三格：标签必须丢
    foreach ($fmt in @('jpg','png','webp')) {
        $x = RunCli ("or_$fmt") $orIn $fmt @()
        $od = CodedOf $x.file.FullName; $ot = OrientOf $x.file.FullName
        CK (($od -replace '\s','' -eq '480,640')) "(12) $fmt：像素已被旋正（coded 应成 480x640，实得 $od）"
        CK (($ot -eq '<无>') -or ($ot -eq '1')) "(12) $fmt：旋正后不得仍带源 Orientation（否则二次旋转），实得 $ot"
    }
    # jbrd 那一格：像素没动 ⇒ 取向必须**被带走**（EXIF 或 JXL 容器任一处，值不得互相矛盾）
    $xj = RunCli 'or_jxl' $orIn 'jxl' @()
    # ⚠ JXL 的**尺寸口径与 JPEG 不同**：ffprobe 对 .jxl 返回的是**显示**尺寸（它自己应用了容器里的
    #   Orientation），而 jxlinfo 第一行才是 **coded** 尺寸（实测：同一产物 ffprobe=480,640、
    #   jxlinfo="JPEG XL image, 640x480"+容器 Orientation: 8）。⇒ 判"像素没动"必须用 jxlinfo，
    #   拿 ffprobe 的 JXL 尺寸与 JPEG 的 coded 尺寸横比会造出假红（本条第一版就踩了）。
    $jiJxl = ''
    if (Test-Path $jxlinfo) {
        $jo = "$out/_jior_$([guid]::NewGuid().ToString('N').Substring(0,6)).txt"
        $null = Start-Process -FilePath $jxlinfo -ArgumentList @($xj.file.FullName) -NoNewWindow -Wait -RedirectStandardOutput $jo
        $jiJxl = ([System.IO.File]::ReadAllText($jo))
    }
    $mJxl = [regex]::Match($jiJxl, 'JPEG XL image,\s*(\d+)x(\d+)')
    $odj = $(if ($mJxl.Success) { $mJxl.Groups[1].Value + ',' + $mJxl.Groups[2].Value } else { '<未知>' })
    $otj = OrientOf $xj.file.FullName
    CK (($odj -replace '\s','' -eq '640,480')) "(12) jxl(jbrd)：像素应当没动（jxlinfo coded 仍是 640x480，实得 $odj）"
    CK ($otj -eq '8') "(12) jxl(jbrd)：取向随产物带走（EXIF 读回 $otj，期望 8）"
    # 反真空：同一目标关掉重封装 ⇒ 走像素路线 ⇒ 标签反过来必须丢
    $xo = RunCli 'or_jxl_off' $orIn 'jxl' @('--jxl-lossless-jpeg','false')
    $odo = CodedOf $xo.file.FullName; $oto = OrientOf $xo.file.FullName
    CK (($oto -eq '<无>') -or ($oto -eq '1')) "(12) 反真空：jxl 关掉 jbrd ⇒ 像素被旋正，标签必须丢（实得 $oto，coded $odo）"
} else {
    CK $false "(12) 取向夹具或 exiftool 不可用 ⇒ 本段不给绿灯（缺 $orIn / $et）"
}

# 运行级隔离的配套：唯一目录不会被下次运行清掉 ⇒ 成功时自己清（失败则保留供排查）。
# ⚠ 宿主批量删除守卫会拦整目录删 ⇒ 必须 `-EA SilentlyContinue`（§6 第 57 条 ③ 的既定口径）。
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -EA SilentlyContinue }
Write-Host ""
Write-Host "===== 接线路径: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
exit $(if ($fail -eq 0) { 0 } else { 1 })
