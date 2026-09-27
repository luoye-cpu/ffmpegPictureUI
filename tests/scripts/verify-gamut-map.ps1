# verify-gamut-map.ps1 —— 色域映射（GMO，`--color-gamut-map`）的**端到端**专项门禁（2026-09-17 新增）
#
# 背景（为什么必须有这一条）：
#   `--color-gamut-map` 接进色彩引擎后，`ColorLoss=GamutCompression` 与 `DegradationCodes.GamutCompressed`
#   在**任何门禁里都没有断言**（`ServiceProbe verdict`/`contract` 本轮才补上纯函数级覆盖）。
#   端到端层面当时只有一条间接证据：日志里出现 `后端=H1(需 GMO 色域压缩)`。产物字节从未被比较过。
#
# 本门禁只做**端到端可观测**的断言（跑真实产品 CLI，比产物字节 / 看退出码与点名日志）：
#   ① 引擎 `off` 与**默认（不传该选项）** 产物**逐字节相同** —— 默认值即契约，默认不得改变行为；
#   ② 引擎 `on` 与 `off` 产物**不同**，且日志出现 `色彩损失=GamutCompression` + `H1(需 GMO 色域压缩)`
#      （**正向有牙**：开关真的改变了像素，且走的是引擎专有算子而非 zscale）；
#   ③ **no-op 负控**：源未越界时 `on` 与 `off` 必须**逐字节相同**（开关不得凭空压缩）；
#      两格素材刻意选**真的发生了映射**的（sRGB → Display P3，`ColorLoss=RoundTripQuantization`），
#      否则"产物相同"也可能只是因为引擎压根没干活 —— 那种空转负控没有意义；
#   ④ **显式 legacy + on ⇒ 硬失败并点名**（传统链只有钳位、没有 GMO 算子，该开关不可能被兑现）；
#      对照组 `legacy + off` 必须正常产出（排除"legacy 本来就坏"的假红）；
#   ⑤ **不得误伤**：默认 `auto` 下引擎不可走（如 gif 非引擎编码出口）时 `on` 只**告警**、不硬失败
#      —— 否则默认切引擎后所有非引擎格式都会全红；
#   ⑥ 非法取值 `--color-gamut-map yes` ⇒ **硬报错且不产出**（不得静默按默认跑）。
#   ⑦ CLI 解析器契约（2026-09-17 补）：
#      · `--color-gamut-map=on`（等号形式）与 `--color-gamut-map on`（空格形式）**逐字节相同**
#        —— 此前 CliParser 只认空格形式，等号形式会**静默失效**（实测产物 == off、零告警）；
#      · 未知选项（`--totally-unknown-option=xyz`）必须打「未知选项」告警行，**但不得改退出码**
#        （无法证明没有调用方依赖静默忽略 ⇒ 只告警是最低风险）；
#      · **负控**：全合法参数下**不得**出现该告警（否则「告警」是常量，上一条就是空转）。
#
# ⚠ 为什么 ①③ 的"逐字节相同"不是空转：同一串选项 `--color-gamut-map on` 在 ② 里让产物变了、
#   在 ③ 里没让产物变 ⇒ 选项确实被解析且确实只在越界时生效，二者互证。
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
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*\Debug\*') { " (Debug fallback)" } else { " (Release)" }))
$fp  = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
foreach ($need in @($exe, $fp)) { if (-not (Test-Path $need)) { Write-Output "缺工具：$need"; exit 1 } }

# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   唯一目录没人替你清 ⇒ 结尾自清（照 `verify-gainmap-memory.ps1` 既有范式）。
$val = "$root/tests/output/validate"; $out = "$val/gmap_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$src = "$root/tests/output/sources/src_8bit.png"       # 未标注 PNG ⇒ 按惯例假定 sRGB（源）
$p3icc = "$val/caps/p3.icc"                            # 用 --icc-file 把**源**判定为 Display P3
foreach ($need in @($src, $p3icc)) { if (-not (Test-Path $need)) { Write-Output "缺素材：$need（先跑 ensure-fixtures.ps1）"; exit 1 } }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }

# ⚠ 参数名不能叫 $args；一律 Start-Process（`& exe` 在部分宿主会静默失败 ⇒ 断言假红）。
#   `--log-level Debug` 不可省：成功项的 item.Log 默认不回显，否则"日志里有决策行"一类断言会假失败。
function RunCli([string]$tag, [string]$fmt, [string[]]$extra) {
    $a = @('--headless', '-i', $src, '--format', $fmt, '--output', "$out/$tag", '--log-level', 'Debug') + $extra
    $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput "$out/$tag.o" -RedirectStandardError "$out/$tag.e"
    $log = [System.IO.File]::ReadAllText("$out/$tag.o") + [System.IO.File]::ReadAllText("$out/$tag.e")
    $f = Get-ChildItem -Recurse "$out/$tag" -Include "*.$fmt" -EA SilentlyContinue | Select-Object -First 1
    return @{ code = $p.ExitCode; log = $log; file = $f }
}
function Md5([string]$path) { return (Get-FileHash $path -Algorithm MD5).Hash }

# 源 Display P3（--icc-file）→ 目标 sRGB：**确实越界**（P3 ⊄ sRGB）⇒ GMO 的适用场景。
$FIX = @('--icc-file', $p3icc, '--color-space', 'sRGB')

Write-Host "`n### 1) 引擎 off 与默认（不传 --color-gamut-map）产物必须逐字节相同 ###" -ForegroundColor Cyan
$rOff = RunCli 'eng_off' 'png' (@('--color-engine', 'engine') + $FIX + @('--color-gamut-map', 'off'))
$rDef = RunCli 'eng_def' 'png' (@('--color-engine', 'engine') + $FIX)
CK ($rOff.code -eq 0) "① off：退出码 0（实得 $($rOff.code)）"
CK ($rDef.code -eq 0) "① 默认：退出码 0（实得 $($rDef.code)）"
CK ($rOff.file -and $rDef.file) "① 两格都产出非空产物"
if ($rOff.file -and $rDef.file) {
    $hOff = Md5 $rOff.file.FullName; $hDef = Md5 $rDef.file.FullName
    CK ($hOff -eq $hDef) "① off 与默认产物逐字节相同（$hOff == $hDef，$($rOff.file.Length) B）"
} else { CK $false "① 缺产物，无法比较字节" }

Write-Host "`n### 2) 引擎 on 必须真的改变像素，且走 GMO（H1 引擎专有算子）###" -ForegroundColor Cyan
$rOn = RunCli 'eng_on' 'png' (@('--color-engine', 'engine') + $FIX + @('--color-gamut-map', 'on'))
CK ($rOn.code -eq 0) "② on：退出码 0（实得 $($rOn.code)）"
CK ($rOn.file -and $rOn.file.Length -gt 0) "② on：产出非空（$(if ($rOn.file) { $rOn.file.Length } else { 0 }) B）"
CK ($rOn.log -match '色彩损失=GamutCompression') "② 决策行声明 ColorLoss=GamutCompression（不是 Clipping）"
CK ($rOn.log -match 'H1\(需 GMO 色域压缩\)') "② 后端声明为 H1(需 GMO 色域压缩) ⇒ 确实用了引擎专有算子"
CK ($rOff.log -match '色彩损失=Clipping') "② 对照：off 的决策行是 Clipping（两者判据真的跟着开关走）"
if ($rOn.file -and $rOff.file) {
    $hOn = Md5 $rOn.file.FullName
    CK ($hOn -ne $hOff) "② on 与 off 产物不同（$hOn ≠ $hOff，$($rOn.file.Length) B vs $($rOff.file.Length) B）"
} else { CK $false "② 缺产物，无法比较字节" }

Write-Host "`n### 3) no-op 负控：源未越界时 on 与 off 必须逐字节相同 ###" -ForegroundColor Cyan
# ⚠ 刻意选**真的发生映射**的两格（不是 Action=None 的直通）：
#   sRGB 源 → Display P3 目标 ⇒ sRGB ⊆ Display P3，不越界，但确实做了矩阵 + 曲线变换
#   （决策行 `Map 色彩损失=RoundTripQuantization 后端=H2`）。
#   若用"源=目标"的直通格，开关本来就不可能影响任何东西 ⇒ 那种负控是空转。
$P3T = @('--color-space', '"Display P3"')
$nOn  = RunCli 'noop_on'  'png' (@('--color-engine', 'engine') + $P3T + @('--color-gamut-map', 'on'))
$nOff = RunCli 'noop_off' 'png' (@('--color-engine', 'engine') + $P3T + @('--color-gamut-map', 'off'))
CK ($nOn.code -eq 0 -and $nOff.code -eq 0) "③ 两格退出码 0（实得 $($nOn.code) / $($nOff.code)）"
CK ($nOn.log -match '色彩损失=RoundTripQuantization') "③ 前置：这格**确实发生了映射**（不是 Action=None 的直通空转）"
CK ($nOn.log -notmatch 'GamutCompression') "③ 前置：未越界 ⇒ 开关开也不会触发 GamutCompression"
# ⚠ 这条是**变异逼出来的**：把「越界」判据删掉（只留 `AllowGamutCompression`）时，上面两条**都不会红**
#   —— 因为 GMO 算子本身对未越界像素是严格 no-op，产物仍逐字节相同；只有**后端**变了
#   （H2→H1「需 GMO 色域压缩」）。所以必须额外钉住后端：未越界时不得启用 GMO 后端。
CK ($nOn.log -notmatch '需 GMO 色域压缩') "③ 前置：未越界 ⇒ 开关开也不得启用 GMO 后端（H1）"
if ($nOn.file -and $nOff.file) {
    $hnOn = Md5 $nOn.file.FullName; $hnOff = Md5 $nOff.file.FullName
    CK ($hnOn -eq $hnOff) "③ 未越界时 on 与 off 逐字节相同（$hnOn == $hnOff）"
} else { CK $false "③ 缺产物，无法比较字节" }

Write-Host "`n### 4) 显式 legacy + on ⇒ 硬失败并点名；legacy + off 对照必须正常产出 ###" -ForegroundColor Cyan
$lOn  = RunCli 'leg_on'  'png' (@('--color-engine', 'legacy') + $FIX + @('--color-gamut-map', 'on'))
$lOff = RunCli 'leg_off' 'png' (@('--color-engine', 'legacy') + $FIX + @('--color-gamut-map', 'off'))
CK ($lOn.code -ne 0) "④ legacy+on：退出码非 0（实得 $($lOn.code)）—— 拒绝必须是响亮的"
CK (-not $lOn.file) "④ legacy+on：不产出文件（不得产出「看着像成功」的产物）"
CK ($lOn.log -match 'color-gamut-map') "④ legacy+on：日志点名 --color-gamut-map（不是含糊失败）"
CK ($lOn.log -match '传统色彩链只有硬裁切') "④ legacy+on：说明该组合为何不可能被兑现"
CK ($lOff.code -eq 0 -and $lOff.file) "④ 对照：legacy+off 正常产出（实得 code=$($lOff.code)）⇒ 失败不是 legacy 本身坏了"

Write-Host "`n### 5) 不得误伤：默认 auto 下引擎不可走时 on 只告警、不硬失败 ###" -ForegroundColor Cyan
# 素材必须选**仍在**引擎编码出口之外的格式：本格守的是「引擎不可走 ⇒ 只告警、不硬失败」这条契约。
# ⚠ 2026-09-17 第二次换素材（avif → gif → **ppm**）：
#   ① 本格原用 **avif**，avif 接入引擎后转红（引擎可走 ⇒ 按设计不再告警）；
#   ② 改用 **gif**，本轮 gif 也接入引擎（`ColorEngineRouter.cs:92` 白名单加 gif）⇒ 会**再次**转红；
#   ③ 改 **ppm**：`ColorTransformPlan` 已把 PPM 登记为「根本没有任何色彩管理通路」的容器之一，
#      且**不在**引擎白名单（HANDOVER 待实现清单里也只有 GIF/JXL/JXR/DNG，无 PPM）⇒ 不会随接入推进而失效。
#      ⚠ 为什么不用 jxr/dng：jxr 可用（实测 code=0/30925B/有告警）但给本门禁引入 **JxrEncApp 外部依赖**
#      （本脚本此前只依赖 ffmpeg/ffprobe）；dng 实测 **code=1 且零产物**（需要传感器 Bayer 数据），
#      会让本格变成"断言一个必然失败的路径"。ppm 纯 ffmpeg，实测 code=0/589839B/有告警。
# 断言本身（引擎不可走 ⇒ 只告警、不硬失败）是**契约**，不得因换素材而删除。
# （avif 的"确实能走引擎"由 `verify-color-strategy.ps1` 的 ③b 段正面钉住；
#   gif 的"确实能走引擎"由同脚本的 **③c** 段正面钉住。
#   ⚠ 2026-09-17 更正：此处原先写的是「⑧ 段」，而该脚本**从未有过** ⑧ 段（只有 ①②③③b③c④）
#     —— 那是一条指向不存在断言的**空承诺**，本轮随 gif 接入一并落地为 ③c。）
# 用户没有显式选 legacy ⇒ 只能告警。若这里改成硬失败，默认切引擎后所有非引擎格式会全红。
$av = RunCli 'auto_ppm_on' 'ppm' (@('--color-gamut-map', 'on'))
CK ($av.code -eq 0) "⑤ auto + ppm + on：退出码 0（实得 $($av.code)）—— 回退场景不得硬失败"
CK ($av.file -and $av.file.Length -gt 0) "⑤ 仍正常产出 ppm（$(if ($av.file) { $av.file.Length } else { 0 }) B）"
CK ($av.log -match '已请求色域映射') "⑤ 日志明确告警「该开关不会被兑现」（不静默、也不拦任务）"

Write-Host "`n### 6) 非法取值 ⇒ 硬报错且不产出 ###" -ForegroundColor Cyan
$bad = RunCli 'bad_value' 'png' @('--color-gamut-map', 'yes')
CK ($bad.code -ne 0) "⑥ --color-gamut-map yes：退出码非 0（实得 $($bad.code)）"
CK (-not $bad.file) "⑥ 非法取值：不产出任何文件"
CK ($bad.log -match '取值无效') "⑥ 日志点名取值无效（不是静默按默认跑）"

Write-Host "`n### 7) CLI 解析：`--key=value` 与空格形式等效 + 未知选项必须出声 ###" -ForegroundColor Cyan
# ⚠ 本节三组断言互证，缺一即空转：
#   ⑦-a 等号形式必须**真的生效**（产物 == 空格形式，且决策行仍是 GamutCompression）；
#   ⑦-b 未知选项必须**出声**（否则用户拼错永远无反馈）；
#   ⑦-c 负控：全合法参数下不得出声（否则「未知选项」是常量，⑦-b 白给）。
$rEq = RunCli 'eq_on' 'png' (@('--color-engine', 'engine') + $FIX + @('--color-gamut-map=on'))
CK ($rEq.code -eq 0) "⑦a 等号形式：退出码 0（实得 $($rEq.code)）"
CK ($rEq.file -and $rEq.file.Length -gt 0) "⑦a 等号形式：产出非空（$(if ($rEq.file) { $rEq.file.Length } else { 0 }) B）"
if ($rEq.file -and $rOn.file) {
    $hEq = Md5 $rEq.file.FullName; $hOnCmp = Md5 $rOn.file.FullName
    CK ($hEq -eq $hOnCmp) "⑦a 等号形式与空格形式产物逐字节相同（$hEq == $hOnCmp）"
} else { CK $false "⑦a 缺产物，无法比较字节" }
CK ($rEq.log -match '色彩损失=GamutCompression') "⑦a 等号形式确实被解析并生效（决策行 = GamutCompression，不是静默按默认跑）"

$rUnk = RunCli 'unknown_opt' 'png' (@('--color-engine', 'engine') + $FIX + @('--totally-unknown-option=xyz'))
CK ($rUnk.code -eq 0) "⑦b 未知选项：退出码仍为 0（实得 $($rUnk.code)）—— 只告警、不拦任务"
CK ($rUnk.file -and $rUnk.file.Length -gt 0) "⑦b 未知选项：仍正常产出（不得因告警而中断）"
CK ($rUnk.log -match '未知选项') "⑦b 未知选项：日志出现「未知选项」告警（不得静默忽略）"
CK ($rUnk.log -match 'totally-unknown-option') "⑦b 未知选项：告警点名具体选项名（不是含糊提示）"

CK ($rOn.log -notmatch '未知选项') "⑦c 负控：全合法参数（空格形式）下不得出现「未知选项」告警"
CK ($rEq.log -notmatch '未知选项') "⑦c 负控：全合法参数（等号形式）下不得出现「未知选项」告警"

Write-Host ""
Write-Host "===== gamut-map: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）；失败则保留供排查
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
exit $(if ($fail -eq 0) { 0 } else { 1 })
