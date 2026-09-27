# 4 色彩策略 × {SDR/广色域/HDR 源} × {png/avif/jpg/jpg+gainmap} 矩阵实测
# 断言: 输出非0字节; 记录 CICP(ffprobe) + ICC(exiftool) 以验证策略差异
# ⚠ **2026-09-16 契约更新（默认切引擎）**：4 策略的 Reject 契约现在**默认生效**（此前由传统管线的
#   两个布尔近似吞掉）。三格按契约**必须拒绝**，已改为 `$expectReject` 断言（若产出文件则判红）：
#     · carry-sdr-png    —— 源**完全无标签**（无 ICC 无 CICP）⇒ S2「携带」没有可携带的东西
#     · manual-sdr-png / manual-hdr-png —— S4「手动」**未给目标**（本脚本不传 --color-space）
#   想验证它们能产出，请补 `--color-space <目标>`（见下方注释）。
#
# ⚠ **2026-09-16 追加「传统管线对照」**（`--color-engine legacy`）：引擎是默认执行体，但
#   JXR/DNG/动画 等**非引擎编码出口**仍由传统管线承担（2026-09-17 更正：原清单里的
#   AVIF / GainMap / **GIF** 已先后接入引擎，**JXL 的 `backend==Ffmpeg` 子情形**亦已接入
#   （`backend==Cjxl` 仍是出口外的默认后端）—— 见下方 ⚠⚠ / ⚠⚠⚠ 段），而传统管线此前
#   只看用户**声明**的策略（`options.ColorStrategy`），不看计划层的**生效**策略
#   （`ColorTransformPlan.EffectiveStrategy`，S2 有三种显式覆盖）。本段把同一矩阵在传统管线上
#   重跑一遍，断言**两条管线在共同产出格上的 CICP 与 ICC 必须逐格一致**（对照 = 同一条策略
#   不允许有第二套结局），并对传统管线单独断言 S2/S3 的标注语义。
#   ~~已知差异（本段只**记录**不判红）：契约拒绝格（carry-sdr-png / manual-*）传统管线不拦，
#   仍会产出文件 —— 这是"契约由引擎侧执行"的既有分工，不是本段要修的东西。~~
#   ↑ 2026-09-17（S6）**作废**，原文保留备查（见下方 S6 段）。
#   （原先另有一格 `recommended-hdr-png` 的 S1 行为差异，**已于 2026-09-16 修复**：
#   根因是 `png.CanHdr` 未设 ⇒ 自动 tonemap 误触发；PNG 经 cICP 可承载 HDR。见 ③ 段注释。）
#
#   ⚠ **2026-09-17（S6）本段有两处作废，勿再照旧读**：
#     · 原文「已知差异：契约拒绝格（carry-sdr-png / manual-*）传统管线不拦、仍会产出文件」
#       —— **作废**。S6 起 legacy 消费与引擎**同一份**裁决（维护者裁定「拒绝，用自研管线完成
#       色彩映射」）⇒ 那四格现在**两边都不产出**；本段 `expectReject` 已改为**读裁决**决定
#       （`ExpectRejectFromVerdict`，规划 §3 P4.1），不再硬编码 `$false`。
#     · 因此 ③ 段那条「引擎拒绝、传统仍产出」的 INFO 分支**不应再出现**（保留作安全网）。
#
# ⚠⚠ **2026-09-17（AVIF 接入引擎轮）—— 本脚本的 avif 格语义整体反转，务必读完再改**：
#   AVIF **已**成为引擎编码出口（`ColorEngineRouter.cs:92` 白名单加 `avif`；
#   `ImageEncoderArgs.BuildAvifOptions` 承接全部 AVIF 编码参数）⇒ 上面那行不再包含它。连带两处：
#     · `manual-p3-avif`：旧前提是"avif 不可路由 ⇒ 落传统管线 ⇒ 传统管线不做契约拒绝 ⇒ 产出"
#       （所以 `:98` 的 `$expectReject` 传的是 `$false`）。现在 avif 可路由 ⇒ 引擎按 **S4 契约**
#       拒绝（与 `manual-sdr-png`/`manual-hdr-png` **逐字相同的理由串**、退出码 91、零产物）
#       ⇒ `:98` 第 5 参已补 `($strat -eq "manual")`。**这是把断言从「avif 必须回退」改成
#       「avif 必须走引擎并继承 S4 拒绝」，方向反转、仍然有牙**（变异：去掉白名单 ⇒ 该格重新
#       产出 4810B ⇒ 与 `expectReject=$true` 冲突 ⇒ 转红）。
#     · 新增 **③b 段（强制 Map）**：原有 4 个 avif 格（recommended/carry/cicp-only/manual × p3 源）
#       **全是直通/保真**（引擎日志 `计划为直通（不改像素）⇒ 本次由 ffmpeg 完成重编码`，
#       见 `QueueProcessor.cs:775-792`）⇒ 它们**根本不经过** `RawColorPipeline.BuildEncodeArgs`，
#       对「AVIF 出口编码参数」完全无感。③b 是**唯一**正面钉住"avif 走引擎编码出口"的断言。
#
# ⚠⚠⚠ **2026-09-17（GIF 接入引擎轮）**：
#   GIF 也**已**成为引擎编码出口（`ColorEngineRouter.cs:92` 白名单加 `gif`；
#   `ImageEncoderArgs.BuildGifFilterChain`/`BuildGifOptions` 承接调色板链与 `-loop`；
#   `RawColorPipeline.BuildEncodeArgs` 的 gif 出口把**原文件**作第二输入、`alphaextract`+`alphamerge`
#   补回 raw 通路丢失的 alpha）。连带两处：
#     · 文件头上面那行「非引擎编码出口」清单**已删掉 GIF**（它现在是引擎出口）。
#     · 新增 **③c 段（强制 Map）**：与 ③b 同构。理由与 avif 完全相同 —— ① 的 gif 格实测
#       **全是直通**（无标签源 ⇒ `Action=None`，日志 `计划为直通（不改像素）⇒ 本次由 ffmpeg 完成重编码`），
#       走的是传统管线的 `-filter_complex` 调色板链，对引擎出口**无感**。
#       ⇒ ③c 是**唯一**正面钉住「gif 走引擎编码出口」的断言，也是 `verify-gamut-map.ps1` ⑤ 段
#       注释里那句「gif 的『确实能走引擎』由 ③c 段正面钉住」的落点
#       （⚠ 该注释此前指向的是**并不存在**的「⑧ 段」，本轮一并更正）。
#     · ③c 同时钉住**命令行形态**（2026-09-17 第二轮补）：引擎出口的完整命令行由
#       `RawColorPipeline.RunToEndAsync:629` 的 `[色彩] ffmpeg …` 记录（**本来就有**，
#       我上一轮说"不写进日志"是只 grep 了 `[cmd]` 就下的错误结论）⇒ 直接断言链里有
#       `palettegen=reserve_transparent=1`/`paletteuse` 与 `[1:v]…alphaextract…alphamerge`。
#     · 本脚本的 `$expectReject` 格**无一涉及 gif**（逐条查过：只有 sdr-png / p3-avif / hdr-png），
#       ⇒ gif 接入不会让本脚本任何**既有**格转红（接入当时实测既有格仍 **45/0**；此后 ③c 新增 6 条、
#       2026-09-17 第二轮又新增 2 条命令行断言 ⇒ 现为 **53/0**）。
#
# ⚠⚠⚠⚠ **2026-09-17（S7「裁决消费」轮）—— 新增 ③d 段（+5）⇒ 本脚本现为 58/0**：
#   此前 `QueueProcessor.cs` 拿到 `plan.Verdict` 却**从不读**（`Degradations`/`Alternatives` 零消费者）
#   ⇒ 「引擎产出正确但有可量化损失」对用户完全不可见。S7 起逐条打印，③d 正面钉住两个出口：
#     · (a) `ProceedWithDegradation`：复用 ③c 的 `map-p3-gif`，断言日志含稳定码 `降级（UnlabeledAssumedSrgb）`；
#     · (b) **门禁内负控**：同格换 png ⇒ **不得**含该降级码（防止正断言退化成空断言）；
#     · (c) `Reject`：`cicp-only` + P3 → jpg（§5-A）断言日志含 `可替代方案：Format/avif`。
#   ⚠ 断言一律打稳定 Code（`DegradationCodes.*` / `AlternativeKind`），禁止打自由文本（§6.4）。
#
# ⚠⚠⚠⚠⚠ **2026-09-17（S6「legacy 消费裁决」轮）—— 本脚本结构性改动，改动前务必读完**：
#   维护者裁定：「**拒绝**，用自研管线完成色彩映射」⇒ legacy 必须消费与引擎**同一份**裁决
#   （规划 §3 P3「单一消费接口」的最终落地）。本脚本三处连带改动：
#     · ② 段 `expectReject` 由**硬编码 `$false`** 改为**读裁决**（`ExpectRejectFromVerdict`，
#       读 ① 段引擎日志的 `[色彩裁决] 裁决=…`）⇒ 契约拒绝格（carry-sdr-png / manual-*）
#       在传统管线上**也必须不产出**（§3 P4.1 明确要求）；
#     · `S` 的 `expectReject` 判据由「匹配引擎那种 `Reject…｜…` 决策行」换成
#       「出现**裁决行** `[色彩裁决] 裁决=Reject`」——判据从**自由文本**换成**稳定枚举**，
#       仍然要求「零产物 + 明确理由」（**不是放宽**，§6.4）；
#     · 新增 **③d(d) + ③e 段（+5）**：legacy 在 Reject 格上必须同样拒绝并打出结构化替代路径；
#       ⚠ 计数口径：**+5 = ③d(d) 的 1 个格子（`S` 跑一次 legacy jpg）+ 2 条断言，加 ③e 的 2 条断言**
#       —— 别只数 `Assert`（那是 +4，会漏掉 ③d(d) 那个格子；本注释曾误写 (+4)，2026-09-18 更正）。
#       并新增**裁决 parity** 断言 —— 同一输入两条管线各跑一次**真实 CLI**，
#       `Verdict.Kind` 必须逐格相同（比"产物 CICP/ICC 一致"更本质），且**断言覆盖格数 ≥12**
#       （否则"两边都读不到 ⇒ 全 skip"会退化成空断言）。⇒ ①..④ 段原基线 **63/0**（实测）。
#       ⚠ **2026-09-26（任务 #35）新增 ③f 段 = +8 条断言**（F0a..F0e 五条判据自测 + F1/F2/F3 真实格）
#         落地时 **70/1**（F3 刻意红 = #35 那笔"webp/avif 位深升降零播报"的机器可查登记，
#         根因在 `Decision.cs` 与 `ColorIntentFactory.cs` 先把请求档钳掉）。
#       ✅ **同日修复后 71/0**（实测：`tests/output/_gate_script_verify-color-strategy.ps1.txt`
#         16:05:39 那轮，被测 DLL 16:00:17 构建、内含本次新增的 `BitDepthRequested`/`RequestedBitDepth`）
#         —— 修法不是放宽判据，而是把"用户请求档"穿过那两处钳制带进规划层，
#         见 `ColorStrategyPlanning.CollectDegradations` ①b 的非 RgbNative 分支。
#       ⚠ **同日深夜（任务 #39/#41）再加 F4/F5 = +6 条 ⇒ 77/0**（实测 `tests/output/t39/strategy_after2.log`，
#         DLL `1881E2F76539CAEF`）：F4 `avif + Display P3 + 12` 必须**真交付 12bit**、F5 同一格请求 16
#         必须落到编码器上限 12 且恰好播一次。这两条钉的是**删掉"AVIF+P3 高位深 ⇒ 8bit"无据特例之后**的
#         新契约（取证 `tests/output/t36/probe_39b.ps1`），所以它们是**收紧**不是放宽：
#         特例若被加回来，F4 立刻红。⚠ F4 的出口 `-pix_fmt` 取日志里**最后一个**匹配（引擎路线前面那条
#         `rgb48le` 是喂编码器的中间件格式，不是交付档 —— 第一版取第一个匹配，把正确产物判成红）。
#         ⚠ 本段的牙仍在 F0a..F0e（合成行自测）+ F2（真日志配错实测）上：
#         **不许**再为变绿动 F1/F3/F4/F5 的判据。
#   ⚠ 日志标签：传统管线消费裁决时打 `[色彩裁决]`，**不是** `[色彩引擎]` ——
#     `verify-color-wiring.ps1:110` / `verify-gainmap-engine.ps1:116` 断言「显式 legacy 一行
#     `色彩引擎` 日志都不该出现（逃生门零污染）」；换标签后该断言继续成立且仍有牙（它拦的是
#     "legacy 偷偷走引擎执行"），同时裁决消费对用户可见。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*\Debug\*') { " (Debug fallback)" } else { " (Release)" }))
$et = "$root/publish/PLAN/exiftool/exiftool.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$val = "$root/tests/output/validate"; $col = "$val/../results/color"
# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   唯一目录没人替你清 ⇒ 结尾自清（照 `verify-gainmap-memory.ps1` 既有范式）。
$out = "$val/strat_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}
# **只读 stdout** 的取数口径（读"值"用它，读 ffmpeg 的 stderr 进度/错误才用上面的 `Exec`）。
# 理由见 `:175` 的注释：exiftool 的 perl locale warning 会把负断言撑成恒真。
function ExecOut([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return [System.IO.File]::ReadAllText($o)
}

$pass=0;$fail=0
# 每格的实测记录：tag -> @{ Ok=是否按预期; Bytes; Cicp; Icc }。对照段据此逐格比。
$script:res = @{}
$script:tags = @()

function S($tag,$inFile,$fmt,$strat,$gm,$expectReject = $false,$legacy = $false,$extra = @()){
  $runTag = if($legacy){"L-$tag"}else{$tag}
  $a = "--headless -i `"$inFile`" --format $fmt --output `"$out/$runTag`""
  if($strat){$a+=" --color-strategy `"$strat`""}
  if($gm){$a+=" --jpeg-gain-map true"}
  # 附加参数（逐项加引号）：供「强制 Map」等需要额外开关的格使用（见 ③b 段）
  foreach($x in $extra){$a+=" `"$x`""}
  if($legacy){$a+=" --color-engine legacy"}
  Start-Sleep -Seconds 2
  Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput "$out/$runTag.o" -RedirectStandardError "$out/$runTag.e" | Out-Null
  # 日志一律读出来并存进结果：③b 段要按日志判「是否真的走了引擎编码出口」。
  # （成功项的 item.Log 默认不回显 ⇒ ③b 的调用额外传了 --log-level Debug。）
  $log = [System.IO.File]::ReadAllText("$out/$runTag.o") + [System.IO.File]::ReadAllText("$out/$runTag.e")
  $f = Get-ChildItem -Recurse "$out/$runTag" -Include *.$fmt,*.jpg,*.jpeg -EA SilentlyContinue | Select-Object -First 1
  if($expectReject){
    # 契约拒绝：**不得**产出文件，且必须给出明确的拒绝原因（否则就是"静默失败"）
    # ⚠ 2026-09-17（S6）：判据由「能匹配到引擎那种 `Reject…｜…` 决策行」换成
    #   「日志里出现**裁决行** `[色彩裁决] 裁决=Reject`」。理由：现在**两条管线**消费同一份裁决，
    #   传统管线打的是 `[色彩裁决] ❌ 契约拒绝（不出产物）：…`（没有引擎那种 `Reject…｜…` 形状）。
    #   这**不是放宽**：仍然要求「零产物 + 明确拒绝理由」，只是把判据从**自由文本**换成**稳定枚举**
    #   （规划 §6.4：禁止用自由文本当判据）。理由串只用于人读的 PASS 行。
    if($f -and $f.Length -gt 0){
      Write-Host ("  FAIL {0,-24} 期望契约拒绝，却产出了 {1}B 的文件" -f $runTag,$f.Length) -ForegroundColor Red
      $script:fail++; $script:res[$runTag]=@{Ok=$false;Bytes=$f.Length;Cicp="";Icc=$false;Log=$log}; return
    }
    if((GetVerdictKind $log) -eq "Reject"){
      $why = [regex]::Match($log, '契约拒绝（不出产物）：([^\r\n]{0,80})').Groups[1].Value.Trim()
      if(-not $why){ $why = [regex]::Match($log, 'Reject[^\r\n｜]*｜([^\r\n]{0,80})').Groups[1].Value.Trim() }
      Write-Host ("  PASS {0,-24} 契约拒绝：{1}" -f $runTag,$why) -ForegroundColor Green; $script:pass++
      $script:res[$runTag]=@{Ok=$true;Bytes=0;Cicp="";Icc=$false;Log=$log}
    } else {
      Write-Host ("  FAIL {0,-24} 无输出但**没有**裁决行 `[色彩裁决] 裁决=Reject`（静默失败）" -f $runTag) -ForegroundColor Red
      $script:fail++; $script:res[$runTag]=@{Ok=$false;Bytes=0;Cicp="";Icc=$false;Log=$log}
    }
    return
  }
  if(-not $f -or $f.Length -eq 0){
    Write-Host ("  FAIL {0,-24} (无输出)" -f $runTag) -ForegroundColor Red; $script:fail++
    $script:res[$runTag]=@{Ok=$false;Bytes=0;Cicp="";Icc=$false;Log=$log}; return
  }
  # ⚠ 两处取数都改 **ExecOut（只读 stdout）**：`Exec` 把 stderr 并进返回值，而随包 exiftool 是 perl 打的，
  #   环境带本机 perl 不认的 `LC_ALL/LANG` 时每次调用都先喷 locale warning ⇒
  #   ① `$hasIcc` 被喂进 `-not $ge.Icc` 这类**负断言**（gif 不许写假标注），污染会让它偏成假绿/假红两边都可能，
  #   ② `$cicpStr` 里会混进 warning 文本，两格对照（`:289`/`:319`/`:346`）就变成"两条 warning 互比"。
  #   前置的"取数尺有牙"正对照见 `:213`（同一把尺必须先在**真有 ICC** 的素材上读到东西）。
  $cicp = (ExecOut $fp "-v error -select_streams v:0 -show_entries `"stream=color_primaries,color_transfer`" -of csv=p=0 `"$($f.FullName)`"") -split "`r?`n"
  # 归一化：ffprobe 的输出可能带空行 / 尾随分隔符，不归一化会让"值相同"的两格在对照段被判成不同（假红）。
  $cicpStr = (($cicp -join ",") -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ","
  $hasIcc = (ExecOut $et "-a -s -G0 `"$($f.FullName)`"") -match "ICC|ProfileDescription|iccp"
  Write-Host ("  PASS {0,-24} {1,6}B  cicp={2}  ICC={3}" -f $runTag,$f.Length,$cicpStr,$hasIcc) -ForegroundColor Green
  $script:pass++
  $script:res[$runTag]=@{Ok=$true;Bytes=$f.Length;Cicp=$cicpStr;Icc=[bool]$hasIcc;Log=$log}
}

# 从日志里读**裁决类别**（稳定枚举，规划 §6.4：不用自由文本当判据）。
# 两条管线打的是**同一行格式** `[色彩裁决] 裁决=<Kind>`：
#   · 引擎侧 = `QueueProcessor.TryRunColorEngineAsync` 的决策段；
#   · 传统管线侧 = `QueueProcessor` 的 legacy 消费点（S6 新增）。
# 用 `[色彩裁决]` 而不是 `[色彩引擎]`：`verify-color-wiring.ps1:110` / `verify-gainmap-engine.ps1:116`
# 都断言「显式 legacy 一行 `色彩引擎` 日志都不该出现（逃生门零污染）」—— 换标签后该断言继续成立。
# 读不到 ⇒ 返回空串（调用方据此判红，不给"没输出 = 没失败"式假绿）。
function GetVerdictKind($log){
  if(-not $log){ return "" }
  $m = [regex]::Match($log, '\[色彩裁决\] 裁决=([A-Za-z]+)')
  if($m.Success){ return $m.Groups[1].Value }
  return ""
}

# 断言小工具（对照段用；与 S 的 PASS/FAIL 计数同一份）
function Assert($name,$cond,$detail){
  if($cond){ Write-Host ("  PASS {0,-24} {1}" -f $name,$detail) -ForegroundColor Green; $script:pass++ }
  else     { Write-Host ("  FAIL {0,-24} {1}" -f $name,$detail) -ForegroundColor Red;   $script:fail++ }
}

$sdr="$val/sdr_plain.png"; $p3="$col/a2_srgb_p3.png"; $hdr="$val/src_hdr.png"

# ⚠ **取数尺自己的前置正对照**（#51，2026-09-27）：下面的 `$hasIcc` 会被喂进"不许写假标注"这类**负断言**
#   （`map-gif-不写假标注` 的 `-not $ge.Icc`）。负断言最怕的是"尺子一直读不到东西"—— 那让它恒真。
#   ⇒ 先用**同一条命令**在一份**真有 ICC** 的素材上量到"有"，读到不到就直接判红、本轮不作数。
$probeIcc = (ExecOut $et "-a -s -G0 `"$p3`"") -match "ICC|ProfileDescription|iccp"
Assert "前置-取数尺有牙" $probeIcc "同一条 exiftool 读法在 P3 素材上读得到 ICC ⇒ 后面的「不得写出 ICC」负断言不是空转"

# ── ① 引擎（默认执行体）矩阵 ──
foreach($strat in @("recommended","carry","cicp-only","manual")){
  Write-Host "`n### strategy = $strat ###" -ForegroundColor Cyan
  # SDR 源**完全无标签**：只有 S2（携带）没有可携带的东西 ⇒ 按契约拒绝；
  # S4（手动）本脚本不给目标 ⇒ 按契约拒绝（"未提供任何目标色彩描述"）。
  $rejSdr = ($strat -eq "carry" -or $strat -eq "manual")
  # ⚠ 2026-09-17（S6）：① 段也补 `--log-level Debug` —— ③e 的**裁决 parity** 要从日志读
  #   `[色彩裁决] 裁决=…`，而成功项的 item.Log 默认**不回显**（`ConsoleQueueObserver.cs:151`）。
  #   不带它时只有"拒绝"的格有日志 ⇒ parity 只剩 4 格（实测：`裁决parity(4 格)` + 覆盖格数转红）。
  S "$strat-sdr-png"   $sdr "png"  $strat $false $rejSdr $false @("--log-level","Debug")
  # ⚠ 2026-09-17：avif **已可路由**（引擎编码出口）⇒ 不再回退传统管线 ⇒ 必须继承 S4 契约拒绝，
  # 与 `:115`（carry）/`:118`（manual）对齐。变异：去掉 `ColorEngineRouter.cs:92` 白名单的 avif
  # ⇒ 该格重新产出文件 ⇒ 与本断言冲突 ⇒ 转红（见文件头 2026-09-17 段）。
  S "$strat-p3-avif"   $p3  "avif" $strat $false ($strat -eq "manual") $false @("--log-level","Debug")
  # HDR 源：S4 不给目标 ⇒ 拒绝；S2 在 PNG 上可继承 CICP（2026-09-16 修正 png.CanCicp）
  S "$strat-hdr-png"   $hdr "png"  $strat $false ($strat -eq "manual") $false @("--log-level","Debug")
  S "$strat-hdr-jpggm" $hdr "jpg"  $strat $true  $false $false @("--log-level","Debug")
  $script:tags += @("$strat-sdr-png","$strat-p3-avif","$strat-hdr-png","$strat-hdr-jpggm")
}

# 规划 §3 P4.1：legacy 的 `expectReject` **由读裁决决定**，不再硬编码。
# 读的是**同格引擎运行**（① 段刚跑过、日志已记）的 `[色彩裁决] 裁决=…`：同一输入在两条管线上
# 裁决必须相同 ⇒ 引擎说 Reject，传统管线也必须不产出。
# ⚠ 读不到裁决（引擎格未跑 / 无裁决行）⇒ 返回 $false（保持"产出"预期）；该情形会被 ③e 的
#   **覆盖格数**断言抓出来，不会静默退化成"传统管线永远不拒绝"。
function ExpectRejectFromVerdict([string]$tag){
  $r = $script:res[$tag]
  if(-not $r){ return $false }
  return ((GetVerdictKind $r.Log) -eq "Reject")
}

# ── ② 传统管线（--color-engine legacy）对照矩阵 ──
# ⚠ 2026-09-17（S6）：本段此前写着「传统管线不做契约拒绝 ⇒ expectReject 一律 $false」——**已作废**。
#   维护者裁定「拒绝，用自研管线完成色彩映射」⇒ legacy 消费与引擎**同一份**裁决：
#   Reject 格 legacy **也不产出**（非零退出码 + Reason/Advice/Alternatives）。
#   故此处 expectReject 改为**读裁决**（`ExpectRejectFromVerdict`），与 §3 P4.1 一致。
Write-Host "`n### 传统管线对照（--color-engine legacy）###" -ForegroundColor Cyan
foreach($strat in @("recommended","carry","cicp-only","manual")){
  Write-Host "  --- strategy = $strat ---" -ForegroundColor Cyan
  # ⚠ `--log-level Debug` 必需（③e 的裁决 parity 要从日志读 `[色彩裁决] 裁决=…`，见 ① 段同款说明）。
  S "$strat-sdr-png"   $sdr "png"  $strat $false (ExpectRejectFromVerdict "$strat-sdr-png")   $true @("--log-level","Debug")
  S "$strat-p3-avif"   $p3  "avif" $strat $false (ExpectRejectFromVerdict "$strat-p3-avif")   $true @("--log-level","Debug")
  S "$strat-hdr-png"   $hdr "png"  $strat $false (ExpectRejectFromVerdict "$strat-hdr-png")   $true @("--log-level","Debug")
  S "$strat-hdr-jpggm" $hdr "jpg"  $strat $true  (ExpectRejectFromVerdict "$strat-hdr-jpggm") $true @("--log-level","Debug")
}

# ── ③ 两条管线的逐格对照（共同产出格必须 CICP 与 ICC 一致）──
Write-Host "`n### 管线对照（引擎 vs 传统）###" -ForegroundColor Cyan
# ⚠ **2026-09-16 更新：原先登记的唯一差异 `recommended-hdr-png` 已修复，故本表已清空。**
#   原差异：引擎 S1 对「HDR 源 → png」自动 hable 色调映射并附 ICC（53926B/ICC=True），
#   而传统管线保 HDR 不附 ICC（43821B/ICC=False）。根因**不在 S1 的策略**，而在能力表：
#   `png.CanHdr` 未设（false）⇒ `ColorIntentFactory` 的「HDR 源 + 容器不支持 HDR ⇒ 自动 tonemap」
#   被误触发。PNG 经 **cICP** 就能承载 PQ/HLG（两条管线的产物实测都写着 `smpte2084,bt2020`）
#   ⇒ 已置 `CanHdr = true`；修后两格**逐字节相同**（43821B / CICP smpte2084,bt2020 / ICC=False）
#   ⇒ 该格已并入下面的**严格对照**，不再是"已登记差异"。
# 留空表是**有意的**：今后若再出现差异，必须走"要么修、要么显式登记"两条路之一，不得静默放过。
$knownDivergence = @{}
$parityCells = 0; $parityBad = @(); $engineOnlyReject = @()
foreach($tag in $script:tags){
  $e = $script:res[$tag]; $l = $script:res["L-$tag"]
  if(-not $e -or -not $l){ continue }
  # ⚠ 2026-09-17：**已判红的格不进对照**。`expectReject` 违约（期望拒绝却产出）走的是 S 的早退分支，
  #   那条分支**从不测 CICP**（`Cicp` 留空）⇒ 把它算进对照会**多报一条误导性的红**
  #   （"引擎(cicp=) ≠ 传统(cicp=iec61966-2-1,smpte432)"），而真因只是"没按契约拒绝"。
  #   该格本身已经判红 ⇒ 跳过不削弱门禁，只让红行都指向真因。
  #   实测（M1：删白名单 avif）：修前 42/3（含这条假"CICP 不一致"）；修后 43/2，两条都是真因。
  if(-not $e.Ok -or -not $l.Ok){ continue }
  if($e.Bytes -eq 0 -and $l.Bytes -gt 0){
    # 引擎按契约拒绝、传统管线仍产出。
    # ⚠ 2026-09-17（S6）：本分支**此前是常态**（传统管线不做契约拒绝 ⇒ carry-sdr-png/manual-* 四格
    #   长期落在这里"只记录不判红"，见文件头旧"已知差异"段）。S6 让 legacy 消费同一份裁决后，
    #   该形态应当**不再出现**（那四格现在两边都 0 字节）。保留本分支作为**安全网**：
    #   若将来又有格落到这里，就是"legacy 没听裁决"的现行证据 ⇒ 打 INFO 供定性（不判红，
    #   因为"产出正确"本身不构成红；红由 ③e 的裁决 parity 负责）。
    $engineOnlyReject += $tag; continue
  }
  if($e.Bytes -eq 0 -or $l.Bytes -eq 0){ continue }
  if($knownDivergence.ContainsKey($tag)){
    Write-Host ("  INFO {0,-24} 已登记差异（不判红）：{1}" -f $tag, $knownDivergence[$tag]) -ForegroundColor Yellow
    continue
  }
  $parityCells++
  if($e.Cicp -ne $l.Cicp -or $e.Icc -ne $l.Icc){
    $parityBad += ("{0}: 引擎(cicp={1},ICC={2}) ≠ 传统(cicp={3},ICC={4})" -f $tag,$e.Cicp,$e.Icc,$l.Cicp,$l.Icc)
  }
}
Assert "parity($parityCells 格)" ($parityBad.Count -eq 0) `
  ($(if($parityBad.Count -eq 0){"共同产出格 CICP/ICC 全部一致"}else{($parityBad -join " | ")}))
if($engineOnlyReject.Count -gt 0){
  Write-Host ("  INFO {0,-24} 引擎按契约拒绝、传统管线仍产出（已记录，不判红）：{1}" -f "契约分工", ($engineOnlyReject -join "、")) -ForegroundColor Yellow
}

# ── ③b 强制 Map：**唯一**正面钉住「AVIF 走引擎编码出口」的断言 ──
# 背景见文件头 2026-09-17 段。① 的 4 个 avif 格（× 两管线）实测**全是直通/保真**：
# `QueueProcessor.cs:775-792` 在 Action=None/CarryIcc 时直接 `return (false,0,plan)`，
# 重编码交给 ffmpeg ⇒ 它们根本不经过 `RawColorPipeline.BuildEncodeArgs`，对 AVIF 出口的
# 编码参数（CICP 标签写在 -i 之前、-pix_fmt 条件化、位深取法）**完全无感**。
# 本段用 `--color-space sRGB` 强制引擎真的做像素变换（Action=Map），从而必须走编码出口：
#   · 引擎侧日志必须证明「是引擎做的」而非回退传统管线 —— 回退时打的是
#     `QueueProcessor.cs:756` 的「本次不适用引擎」，成功时打的是 `:816` 的「… 变换 …ms（… MPix/s）」；
#   · 成对断言：引擎与 legacy 的 CICP/ICC 必须一致（同一条策略不允许有第二套结局）。
# ⚠ `--log-level Debug` 是**必需**的：成功项的 item.Log 默认只在 Debug 阈值下回显
#   （`ConsoleQueueObserver.cs:151`），不带它日志里根本看不到引擎成功行。
# 变异验证（M1）：去掉 `ColorEngineRouter.cs:92` 白名单里的 avif ⇒ 首条断言转红。
Write-Host "`n### ③b 强制 Map：AVIF 引擎编码出口（正面断言）###" -ForegroundColor Cyan
$mTag = "map-p3-avif"
S $mTag $p3 "avif" "recommended" $false $false $false @("--color-space","sRGB","--log-level","Debug")
S $mTag $p3 "avif" "recommended" $false $false $true  @("--color-space","sRGB")
$me = $script:res[$mTag]; $ml = $script:res["L-$mTag"]
Assert "map-avif-走引擎出口" (($me.Log -match "MPix/s") -and ($me.Log -notmatch "本次不适用引擎")) `
  "强制 Map 必须由引擎编码出口完成（日志须含引擎成功行「MPix/s」且**不得**含回退标记「本次不适用引擎」）"
Assert "map-avif-产出" ($me.Bytes -gt 0) "引擎编码出口须产出非空 avif（bytes=$($me.Bytes)）"
Assert "map-avif-parity" (($me.Cicp -eq $ml.Cicp) -and ($me.Icc -eq $ml.Icc)) `
  "强制 Map 的 CICP/ICC 必须与 legacy 一致（引擎 cicp=$($me.Cicp)/ICC=$($me.Icc)，legacy cicp=$($ml.Cicp)/ICC=$($ml.Icc)）"

# ── ③c 强制 Map：**唯一**正面钉住「GIF 走引擎编码出口」的断言 ──
# 与 ③b 同构，背景见文件头 ⚠⚠⚠ 2026-09-17 段。① 的 gif 格实测全是直通（无标签源 ⇒ Action=None，
# 重编码交 ffmpeg）⇒ 走的是传统管线的调色板链，对引擎出口**无感**。本段用 `--color-space sRGB`
# 强制 Action=Map ⇒ 必须走 `RawColorPipeline.BuildEncodeArgs` 的 gif 出口（第二输入补 alpha + 调色板链）。
# ⚠ **命令行形态可以断言**（2026-09-17 更正）：我上一轮写的「引擎出口的命令行不写进日志」是**错的**
#   —— 那是只 grep 了 `[cmd]`（传统管线用）就下的结论。引擎出口实际走
#   `RawColorPipeline.RunToEndAsync:629` 的 `log?.Invoke($"[色彩] ffmpeg {args}\n")`，
#   **完整命令行本来就在日志里**（GainMap 轮"记录完整命令"的同一处）⇒ 无需新增任何日志。
#   下面两条断言直接钉 gif 出口命令行的**结构**（调色板链 + 第二输入补 alpha）。
# 变异验证（M1）：去掉 `ColorEngineRouter` 白名单里的 gif ⇒ `map-gif-走引擎出口` 转红（回退标记出现）。
# 变异验证（M2）：让 `ImageEncoderArgs.BuildGifFilterChain` 返回 null（等价于"忘了写调色板链"）
#   ⇒ `map-gif-引擎命令行含调色板链` 转红。
Write-Host "`n### ③c 强制 Map：GIF 引擎编码出口（正面断言）###" -ForegroundColor Cyan
$gTag = "map-p3-gif"
S $gTag $p3 "gif" "recommended" $false $false $false @("--color-space","sRGB","--log-level","Debug")
S $gTag $p3 "gif" "recommended" $false $false $true  @("--color-space","sRGB")
$ge = $script:res[$gTag]; $gl = $script:res["L-$gTag"]
Assert "map-gif-走引擎出口" (($ge.Log -match "MPix/s") -and ($ge.Log -notmatch "本次不适用引擎")) `
  "强制 Map 必须由引擎编码出口完成（日志须含引擎成功行「MPix/s」且**不得**含回退标记「本次不适用引擎」）"
Assert "map-gif-产出" ($ge.Bytes -gt 0) "引擎编码出口须产出非空 gif（bytes=$($ge.Bytes)）"
# gif 既无 ICC 通路也无 CICP 通路 ⇒ 产物**必须**零标注。这条钉的是"不得写假标注"（历史偏色根因）。
# ⚠ ffprobe 对"无 CICP"的渲染是 `unknown,unknown`（**不是**空串，实测踩到过）⇒ 按渲染判。
Assert "map-gif-不写假标注" ((-not $ge.Icc) -and ($ge.Cicp -match '^unknown(,unknown)?$')) `
  "gif 无任何色彩标注通路 ⇒ 引擎不得写出 ICC/CICP（ICC=$($ge.Icc) cicp=$($ge.Cicp)，期望 ICC=False / cicp=unknown）"
Assert "map-gif-parity" (($ge.Cicp -eq $gl.Cicp) -and ($ge.Icc -eq $gl.Icc)) `
  "强制 Map 的 CICP/ICC 必须与 legacy 一致（引擎 cicp=$($ge.Cicp)/ICC=$($ge.Icc)，legacy cicp=$($gl.Cicp)/ICC=$($gl.Icc)）"
# ── 引擎 gif 出口的**命令行形态**（② 端到端硬断言）──
# 取引擎日志里**编码步**那一行：`[色彩] ffmpeg … -filter_complex …`（解码步只有 `-vf`，不含它）。
# 不用"最后一行"这种脆弱取法，按 `-filter_complex` 选；链一旦丢失 ⇒ 选不到行 ⇒ 断言转红（这是想要的）。
$gEnc = (($ge.Log -split "`n") | Where-Object { $_ -match '\[色彩\] ffmpeg' -and $_ -match '-filter_complex' } | Select-Object -Last 1)
Assert "map-gif-引擎命令行含调色板链" ($gEnc -and $gEnc -match 'palettegen=reserve_transparent=1' -and $gEnc -match 'paletteuse') `
  "引擎 gif 出口必须写调色板链（`palettegen=reserve_transparent=1` + `paletteuse`），否则是静默降质（32.33 vs 44.33 dB）"
Assert "map-gif-引擎命令行含 alpha 合成" ($gEnc -and $gEnc -match 'alphaextract' -and $gEnc -match 'alphamerge' -and $gEnc -match '\[1:v\]') `
  "引擎 raw 通路无 alpha ⇒ 必须把原文件作第二输入（[1:v]）取 alpha 再 alphamerge，否则透明被静默拍平"

# ── ③d 裁决消费（S7）：`Degradations` / `Alternatives` 必须到达**用户可见出口** ──
# 背景（实测）：`QueueProcessor.cs` 早就拿到 `plan.Verdict`（`ColorVerdict{Kind,Degradations,Alternatives}`），
#   但**从不读它**（`grep Verdict src/FfmpegGui` 在 `Services/ColorMapping/` 之外零命中）
#   ⇒ 「引擎产出正确但有可量化损失」对用户**完全不可见**。S7 起逐条打印。
# 断言纪律（规划 §6.4）：一律打**稳定 Code**（`DegradationCodes.*` / `AlternativeKind`），
#   **禁止**打自由文本 —— 文案会改，Code 不会。
# (a) `ProceedWithDegradation`：复用 ③c 的 `map-p3-gif`（P3 PNG → GIF，`--color-space sRGB` 强制 Map）。
#     gif 既无 ICC 通路也无 CICP 通路 ⇒ 规划层登记 `DegradationCodes.UnlabeledAssumedSrgb`
#     ⇒ 日志必须出现 `降级（UnlabeledAssumedSrgb）`。
Assert "verdict-降级码-gif" ($ge.Log -match '降级（UnlabeledAssumedSrgb）') `
  "「引擎产出正确但有损」必须对用户可见：gif 无色彩通路 ⇒ 须打出稳定降级码 UnlabeledAssumedSrgb"
# (b) **负控**（写进门禁，不是一次性手测）：同一格把 gif 换成 png（PNG 有 cICP/ICC 通路）
#     ⇒ **不得**出现该降级码。没有这条，上面的正断言可能退化成"任何日志都含该串"的空断言（假绿）。
$nTag = "map-p3-png-negctl"
S $nTag $p3 "png" "recommended" $false $false $false @("--color-space","sRGB","--log-level","Debug")
$ne = $script:res[$nTag]
Assert "verdict-降级码负控-png" ($ne.Log -notmatch '降级（UnlabeledAssumedSrgb）') `
  "负控：PNG 有 cICP/ICC 通路 ⇒ 不得登记 UnlabeledAssumedSrgb（否则正断言无判别力）"
# (c) `Reject`：`--color-strategy cicp-only` + P3 源 → jpg。理由见规划 §5-A：
#     S3 的语义是"不要 ICC"，而 jpg **必须有 ICC 才有色彩语义** ⇒ 降级产出得到的是
#     「P3 像素被当作 sRGB 解读」的**错误产物** ⇒ 必须 Reject（警告文案救不了一个错的产物）。
#     拒绝时除自由文本 `Advice` 外，必须打出**结构化**替代路径（`AlternativeKind.Format` + `Value=avif`）。
#     ⚠ 该格同时被 `S` 的 `expectReject=$true` 钉住「不得产出文件」，两条断言互不替代。
$jTag = "cicp-only-p3-jpg"
S $jTag $p3 "jpg" "cicp-only" $false $true
Assert "verdict-替代方案-jpg" ($script:res[$jTag].Log -match '可替代方案：Format/avif') `
  "Reject 必须给出结构化替代路径（AlternativeKind.Format + Value=avif，§5-A），而不是只给自由文本建议"

# (d) **S6 端到端**：同一 Reject 格在**传统管线**上必须同样拒绝，且 Reason/Advice/Alternatives
#     都到达用户出口。② 段的 carry/manual 格只钉「不产出」，本格额外钉「拒绝理由与结构化替代路径可见」。
#     ⚠ `--log-level Debug` 不必需（失败项日志以 Error 级强制回显），但显式给上更稳。
S $jTag $p3 "jpg" "cicp-only" $false $true $true @("--log-level","Debug")
Assert "裁决-legacy拒绝-jpg" ((GetVerdictKind $script:res["L-$jTag"].Log) -eq "Reject") `
  "legacy 必须消费同一份裁决 ⇒ 同格同样 Reject（实测裁决=$(GetVerdictKind $script:res["L-$jTag"].Log)）"
Assert "裁决-legacy替代方案-jpg" ($script:res["L-$jTag"].Log -match '可替代方案：Format/avif') `
  "legacy 拒绝时同样要打出结构化替代路径（与引擎侧同格式，只换日志标签）"

# ── ③e 裁决 parity（S6）：同一输入的 `Verdict.Kind` 两条管线**必须相同** ──
# 为什么比"产物 CICP/ICC 一致"更本质：产物标签一致可能只是"两条管线恰好写了同一个标签"，
#   而裁决一致说明**结局**（产出 / 降级 / 拒绝）同源 —— 这正是维护者裁定「legacy 必须消费
#   与引擎同一份裁决」的可机读判据（规划 §3 P4.1）。
# 怎么测：**真实 CLI 跑两遍**（① 默认引擎、② `--color-engine legacy`），各自从日志读
#   `[色彩裁决] 裁决=<Kind>` 后逐格比字符串 —— 不是只调函数（那样测不到"消费者有没有接线"）。
# ⚠ 只比稳定枚举 `VerdictKind`，禁自由文本（§6.4）。
# ⚠ **覆盖格数必须断言**：否则"两边都读不到 ⇒ 全部 skip"会退化成**空断言**（假绿）。
#   下限取 **12**（= 16 格 − 4 个 GainMap 格）：4 个 `*-hdr-jpggm` 格的 legacy 侧**可能**没有裁决行 ——
#   取决于 `ColorEngineRouter.Requested` 是否把 `--jpeg-gain-map` 恒判为"要引擎"
#   （2026-09-18 起**是**：GainMap 是输出结构能力，不受 `--color-engine legacy` 管辖 ⇒ 那 4 格
#    实际也走引擎、两边都有裁决行 ⇒ 实测 16 格）。取**下限**而不是等值，是为了让
#   `Requested` 的这两种口径**都**不误红；若哪天连 12 格都读不到，就是"消费者没接线"⇒ 必须红。
$vkParityN = 0; $vkParityBad = @(); $vkParitySkipped = @()
foreach($tag in $script:tags){
  $eRec = $script:res[$tag]; $lRec = $script:res["L-$tag"]
  $ev = ""
  if($eRec){ $ev = GetVerdictKind $eRec.Log }
  $lv = ""
  if($lRec){ $lv = GetVerdictKind $lRec.Log }
  if((-not $ev) -or (-not $lv)){ $vkParitySkipped += $tag; continue }
  $vkParityN++
  if($ev -ne $lv){ $vkParityBad += ("{0}: 引擎={1} ≠ 传统={2}" -f $tag,$ev,$lv) }
  # ── 结局 parity：legacy 必须**照着裁决做**（Reject ⇒ 零产物；非 Reject ⇒ 有产物）──
  # ⚠ 只比 Kind **不够**：实测 M1 变异（legacy 忽略裁决、仍照常产出）时 Kind 仍逐格相同
  #   ⇒ 本断言自身转绿。加上"结局"这一条后，同一次变异会直接打在**裁决 parity 断言**上。
  $lOut = ($lRec -and $lRec.Bytes -gt 0)
  if($lOut -ne ($lv -ne "Reject")){
    $vkParityBad += ("{0}: 传统裁决={1} 但产出={2}" -f $tag,$lv,$lOut)
  }
}
Assert "裁决parity($vkParityN 格)" ($vkParityBad.Count -eq 0) `
  ($(if($vkParityBad.Count -eq 0){"两条管线的 Verdict.Kind 逐格相同，且 legacy 的产出/不产出与裁决一致"}else{($vkParityBad -join " | ")}))
Assert "裁决parity-覆盖格数" ($vkParityN -ge 12) `
  "两条管线都给出裁决的格数不得少于 12（否则本断言退化成空断言；实测 $vkParityN）"
if($vkParitySkipped.Count -gt 0){
  Write-Host ("  INFO {0,-24} 无 legacy 侧裁决行（未纳入 parity，见 ③e 注释）：{1}" -f "覆盖边界", ($vkParitySkipped -join "、")) -ForegroundColor Yellow
}

# ── ④ 传统管线自身的策略语义断言（S2 携带 / S3 剥 ICC / HDR 保持）──
Write-Host "`n### 传统管线策略语义 ###" -ForegroundColor Cyan
$L = { param($t) $script:res["L-$t"] }
$r = & $L "carry-p3-avif"
Assert "L-carry-p3-avif"  ($r.Cicp -match "smpte432")        "S2 携带：广色域源不得被静默归一（cicp=$($r.Cicp)）"
$r = & $L "carry-hdr-png"
Assert "L-carry-hdr-png"  ($r.Cicp -match "smpte2084")       "S2 携带：HDR 源保持 HDR 传递（cicp=$($r.Cicp)）"
$r = & $L "cicp-only-p3-avif"
Assert "L-cicp-only-p3-avif" (-not $r.Icc)                   "S3 仅CICP：不得残留 ICC（ICC=$($r.Icc)）"
$r = & $L "cicp-only-hdr-png"
Assert "L-cicp-only-hdr-png" (-not $r.Icc)                   "S3 仅CICP：不得残留 ICC（ICC=$($r.Icc)）"
$r = & $L "recommended-sdr-png"
Assert "L-recommended-sdr-png" ($r.Bytes -gt 0)              "S1 推荐：SDR 无标签源仍须产出（bytes=$($r.Bytes)）"
$r = & $L "manual-hdr-jpggm"
Assert "L-manual-hdr-jpggm" ($r.Bytes -gt 0)                 "S4 手动 + GainMap：须产出可用 UltraHDR（bytes=$($r.Bytes)）"

# 策略必须**真的驱动**传统管线的行为（否则"从计划层推导"就只是摆设）：
# 同一源同一容器，S2(携带) 与 S1(推荐) 的结局必须不同 —— 实测 SDR 无标签 PNG 上
# carry 附 ICC(27458B) 而 recommended 不附(27059B)。
# 变异验证：把 `EffectiveColorStrategy` 的返回值改成 Recommended ⇒ 本断言转红（见交付报告）。
$rc = $script:res["L-carry-sdr-png"]; $rr = $script:res["L-recommended-sdr-png"]
Assert "L-S2≠S1(策略生效)" (($rc.Bytes -ne $rr.Bytes) -or ($rc.Icc -ne $rr.Icc)) `
  "传统管线上 S2(携带) 与 S1(推荐) 必须走出不同结局（carry=$($rc.Bytes)B/ICC=$($rc.Icc)，recommended=$($rr.Bytes)B/ICC=$($rr.Icc)）"

# ── ③f 位深播报锁（任务 #35：出口档 ≠ 请求档 ⇒ 必须点名，且播报必须与实测交付一致）────────────
# 为什么放在本脚本而不是只靠 `verify-color-matrix-full.ps1` 的 CAP 尺子：
#   网格那条判据**读日志**，但它一次跑 360 格、约 35 分钟，且只在 full 层才覆盖位深轴（quick 层
#   只有 png/jxl × {8,16}）⇒ 拿它当"位深播报"的**日常**门禁太贵。本段是三格的最小锁。
#
# 判据形状（照抄网格 §2a 的四态语义，**不**新造一套口径、也**不**只断言"日志里有字"）：
#   · 日志里只有**人话**（"出口位深升为 16bit…"）而没有稳定码 ⇒ `SILENT` ⇒ 红；
#   · 播了升位、实测交付却**更浅** ⇒ `MISANNOUNCED` ⇒ 红（这条就是"播了但没做到"）；
#   · 播了降位、实测交付却**更深** ⇒ `MISANNOUNCED` ⇒ 红；
#   · 同一格同时播升位与降位 ⇒ `MISANNOUNCED-both` ⇒ 红。
#   ⇒ 判据的"牙"由下面的合成自测锁住（零转换成本），真实格只是把它作用到实测日志与实测位深上。
#
# 三格真实断言：
#   F1 `jxl --bit-depth 10`（RGB 原生，规划层看得到请求档 10）⇒ 必须播 `BitDepthRaised`，
#      且 **jxlinfo 读回的码流位深**必须等于播报里那个数（16）。
#      ⚠ 位深真值**不能用 ffprobe**：它对 JXL 报的是**解码输出** pix_fmt（rgb48le），
#        不是码流位深 —— 用它当参照，"码流其实只写了 8bit"这种缺陷能一路骗过本锁。
#   F2 负控：拿 **F1 的同一份真实日志**，把实测交付档位换成 8（= 播"升到 16"却只给到 8）
#      ⇒ 判据**必须**转 MISANNOUNCED。这条钉的是"播报↔实测一致"这半截不是空断言。
#   F3 `webp --bit-depth 10`（非 RGB 原生，容器出口只有 8bit）⇒ 必须播降位、且实测交付=8。
#      ⚠⚠ **本格 2026-09-26 曾按预期转红**（那笔 #35 未平的账的机器可查登记），**判据一个字没放宽**：
#      红时的根因**不在** `CollectDegradations` —— 实测进规划层的 `TargetBitDepth` 已经是 8
#      （`FfmpegCommandBuilder.Decision.cs` 的 capBd 钳制先就地改写调用方 options，
#      `ColorIntentFactory.cs:145` 再 `Math.Min(…, caps.MaxBitDepth)` 钳一次）⇒ 请求档 10 在**看得见它
#      的那一层之外**就没了，①b 拿不到"用户要 10"这个事实，自然播不出"10→8"。
#      当时的实测证据：`--bit-depth 10 -f webp` 的 `[cmd]` 是 `-pix_fmt yuv420p`（交付 8），日志里
#      **零** `BitDepth*` 码；`--bit-depth 16 -f avif` 的 `[cmd]` 是 `-pix_fmt yuv420p10le`（交付 10），
#      同样零播报。⇒ 修法是"把请求档穿过钳制带进规划层"（`FfmpegOptions.BitDepthRequested` →
#      `ColorIntent.RequestedBitDepth` → ①b 的非 RgbNative 分支），本格随即转绿。
Write-Host "`n### ③f 位深播报锁（出口档 ≠ 请求档 ⇒ 必须点名 + 播报须与实测一致）###" -ForegroundColor Cyan
$ji = "$root/publish/PLAN/jxl/bin/jxlinfo.exe"

# 交付位深：ffprobe 的 pix_fmt ⇒ 位深（**只用于非 JXL 容器**；JXL 走 jxlinfo，理由见 F1 注释）
$script:pixDepthForLock = @{
  'rgb24' = 8; 'rgba' = 8; 'bgra' = 8; 'gray' = 8; 'pal8' = 8; 'yuv420p' = 8; 'yuvj420p' = 8
  'yuv444p' = 8; 'yuv422p' = 8; 'yuva420p' = 8
  'gbrp10le' = 10; 'yuv420p10le' = 10; 'yuv444p10le' = 10; 'yuva420p10le' = 10
  'gbrp12le' = 12; 'yuv420p12le' = 12; 'yuv444p12le' = 12; 'yuva420p12le' = 12
  'rgb48le' = 16; 'rgb48be' = 16; 'rgba64le' = 16; 'gbrp16le' = 16; 'yuv420p16le' = 16
}
function Lock-DeliveredDepth([string]$File) {
  # ⚠ 只读 stdout：ffprobe 的值走 stdout，`Exec` 会把 stderr 拼在前面 ⇒ 污染环境下 `[0]` 取到的是警告行，
  #   本函数会"认不出来 ⇒ $null"，而那些 $null 让调用方**只比播报不猜**（负断言侧被撑松）。
  $raw = (ExecOut $fp "-v error -select_streams v:0 -show_entries `"stream=pix_fmt`" -of csv=p=0 `"$File`"")
  $px = (@($raw -split "`r?`n") | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1)
  if (-not $px) { return $null }
  $k = ([string]$px).Trim().ToLowerInvariant()
  if ($script:pixDepthForLock.ContainsKey($k)) { return [int]$script:pixDepthForLock[$k] }
  return $null          # 认不出来 ⇒ $null（调用方只比播报、不猜），**绝不**回落到"看起来正常"的默认值
}
# 与网格 §2a 同语义的四态判据（局部一份，刻意不复用网格脚本：门禁之间不许互相依赖产物）
function Lock-JudgeDepth {
  param([int]$Requested, [string]$Log, $Delivered)
  $raised = ($Log -match 'BitDepthRaised'); $reduced = ($Log -match 'BitDepthReduced')
  if ($raised -and $reduced) { return 'MISANNOUNCED-both' }
  if ((-not $raised) -and (-not $reduced)) { return 'SILENT' }
  if ($null -ne $Delivered) {
    if ($raised -and ([int]$Delivered -lt $Requested)) { return 'MISANNOUNCED-shallower' }
    if ($reduced -and ([int]$Delivered -gt $Requested)) { return 'MISANNOUNCED-deeper' }
  }
  if ($raised) { return 'raised-announced' }
  return 'reduced-announced'
}

# —— 判据自测（合成行，不起进程、不建图）：没有这四条，下面的真实格就是"恒绿门禁" ——
$synWords = '[色彩引擎] ⚠️ 出口位深降为 8bit（本次目标 10bit）⇒ 精度不保留'      # 只有人话、没有稳定码
Assert "F0a 判据自测·无人话外的码" ((Lock-JudgeDepth 10 $synWords 8) -eq 'SILENT') `
  "无稳定码（只有中文描述）的日志必须仍判 SILENT ⇒ 本锁不是在找字（实得 $(Lock-JudgeDepth 10 $synWords 8)）"
$synRaised = '[色彩引擎] ⚠️ 降级（BitDepthRaised）：出口位深升为 16bit（本次目标 10bit）⇒ **升位、非降级**'
Assert "F0b 判据自测·播升给浅" ((Lock-JudgeDepth 10 $synRaised 8) -eq 'MISANNOUNCED-shallower') `
  "播升位而实测 8 < 请求 10 ⇒ 必须 MISANNOUNCED（实得 $(Lock-JudgeDepth 10 $synRaised 8)）"
Assert "F0c 判据自测·播降给深" ((Lock-JudgeDepth 12 ('降级（BitDepthReduced）：出口位深降为 8bit') 16) -eq 'MISANNOUNCED-deeper') `
  "播降位而实测 16 > 请求 12 ⇒ 必须 MISANNOUNCED（方向播反就是假播报）"
Assert "F0d 判据自测·升降同播" ((Lock-JudgeDepth 10 ($synRaised + ' BitDepthReduced') 16) -eq 'MISANNOUNCED-both') `
  "同一格同时播升位与降位 ⇒ 必须 MISANNOUNCED-both（自相矛盾不许互相抵消成绿）"
Assert "F0e 判据自测·一致即绿" ((Lock-JudgeDepth 10 $synRaised 16) -eq 'raised-announced') `
  "播升位且实测交付 16 ⇒ 判合规（否则 F1 的绿没有基准）"

# —— 跑一格真实任务，返回 日志 / 产物 / rc（与 S() 同一套 Start-Process 取法，不走 `& exe`）——
function Lock-RunCell([string]$Tag, [string]$InFile, [string]$Fmt, [string]$Bd) {
  Start-Sleep -Seconds 2
  $a = "--headless --log-level Debug -i `"$InFile`" --format $Fmt --bit-depth $Bd --color-space `"Display P3`" --output `"$out/F-$Tag`""
  $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$out/F-$Tag.o" -RedirectStandardError "$out/F-$Tag.e"
  $log = [System.IO.File]::ReadAllText("$out/F-$Tag.o") + [System.IO.File]::ReadAllText("$out/F-$Tag.e")
  $f = Get-ChildItem -Recurse "$out/F-$Tag" -File -EA SilentlyContinue |
       Where-Object { $_.Extension -eq ".$Fmt" } | Sort-Object Length -Descending | Select-Object -First 1
  return @{ Rc = $p.ExitCode; Log = $log; Prod = $f }
}

# F1 jxl 10 ⇒ 播升位，且 **jxlinfo 读回的码流位深**必须就是播报里那个数
$c1 = Lock-RunCell "jxl10" $sdr "jxl" "10"
if (-not $c1.Prod) {
  Assert "F1 jxl10 播报=实测" $false "jxl --bit-depth 10 无产物（rc=$($c1.Rc)）⇒ 本锁无从比对，判红不放行"
} else {
  # ⚠ 只读 stdout：jxlinfo 的 `N-bit RGB` 是**值**（下面 `[regex]::Match` 后直接喂 F1 的"播报=实测"断言）。
  #   合并版会把 stderr 的 locale 警告一起端进来 ⇒ 匹配到的位数不再是产物实测值（第 56 条扫描点名的 3 处之一）。
  $jiTxt = if (Test-Path $ji) { (ExecOut $ji "`"$($c1.Prod.FullName)`"") | Out-String } else { '' }
  $mJi = [regex]::Match($jiTxt, '(\d+)-bit\s+(?:RGB|Gray|Alphaless)')
  $jiBits = if ($mJi.Success) { [int]$mJi.Groups[1].Value } else { $null }
  $mAnn = [regex]::Match($c1.Log, 'BitDepthRaised）：出口位深升为\s*(\d+)bit（本次目标\s*(\d+)bit')
  $annTo = if ($mAnn.Success) { [int]$mAnn.Groups[1].Value } else { $null }
  $annReq = if ($mAnn.Success) { [int]$mAnn.Groups[2].Value } else { $null }
  $jv = Lock-JudgeDepth 10 $c1.Log $jiBits
  Assert "F1 jxl10 播报=实测" `
    (($jv -eq 'raised-announced') -and ($annReq -eq 10) -and ($null -ne $jiBits) -and ($annTo -eq $jiBits)) `
    ("jxl --bit-depth 10 ⇒ 需 BitDepthRaised 且播报的出口档 == jxlinfo 读回的码流位深" +
     "（判据=$jv 播报 $annReq→$annTo jxlinfo=$jiBits；ffprobe 的 pix_fmt 只作对照、不当位深真值）")
  # F2 负控：同一份**真实**日志 + 被换成 8 的实测交付 ⇒ 必须转红（证明 F1 的"一致"半截有牙）
  $jvNeg = Lock-JudgeDepth 10 $c1.Log 8
  Assert "F2 负控·真日志配错实测" ($jvNeg -eq 'MISANNOUNCED-shallower') `
    "F1 的真实日志配上实测 8 ⇒ 必须 MISANNOUNCED（实得 $jvNeg）—— 否则 F1 只是在找字"
}

# F3 webp 10 ⇒ 出口只有 8bit，必须播降位且实测交付 = 8（2026-09-26 修复后转绿；段首有红时的根因）
$c3 = Lock-RunCell "webp10" $sdr "webp" "10"
if (-not $c3.Prod) {
  Assert "F3 webp10 播报=实测" $false "webp --bit-depth 10 无产物（rc=$($c3.Rc)）⇒ 无法比对，判红"
} else {
  $d3 = Lock-DeliveredDepth $c3.Prod.FullName
  $jv3 = Lock-JudgeDepth 10 $c3.Log $d3
  Assert "F3 webp10 播报=实测" `
    (($jv3 -eq 'reduced-announced') -and ($d3 -eq 8) -and ($c3.Log -match 'BitDepthReduced）：出口位深降为 8bit（本次目标 10bit')) `
    ("[任务#35 回潮] webp --bit-depth 10 ⇒ 需播 BitDepthReduced(10→8) 且实测交付 8" +
     "（判据=$jv3 实测交付 $d3）。红只有两种成因：① 请求档又被钳制吃掉（查 FfmpegOptions.BitDepthRequested" +
     " 是否仍在 capBd 钳制之前被记、ColorIntent.RequestedBitDepth 是否仍装配）；" +
     "② CollectDegradations ①b 的非 RgbNative 分支被改掉。两者都不许用放宽判据解决。")
}

# F4 avif 12 + 显式 Display P3 ⇒ 任务 #41 的现场格（取证 tests/output/t36/attribute_41.ps1）
#   现象（`tests/output/t36/grid-t36.csv` 的 avif × Display P3 行）：bd=10/12 各 6 格交付 `pixfmt=yuv420p`
#     （**8bit**）却记 `verdict=ok`，而 `announced=True`。
#   ⚠ 我第一版把这条写成"产品**静默**削档"，**那是错的**：单格实跑（engine / auto / legacy 三管线 ×
#     8bit 与 16bit 两源，共 6 格）**每一格都播了**「降级（BitDepthReduced）：出口位深降为 8bit
#     （本次目标 12bit）」，产物也确实 8bit ⇒ 播报与交付一致，产品这半边是诚实的。
#   ⇒ 真正的洞有两处，都不在产品播报：
#     ① 网格 `$pixDepth` 缺 8bit YUV 词形 ⇒ `delivered` 恒空（实测：全表 `announced=True 且 delivered 空`
#        = avif 18 格 + webp 54 格）⇒ "播报 vs 实测"对账与**承诺账**在这 72 格上从不执行；
#     ② 网格 CAP 对"请求档在能力表里"的格子**根本不看日志**（`verdict = if ($capOk) { 'ok' }`）⇒
#        #36 把 avif 的 12 档纳进表之后，"表说支持 12、产品按 #39 那条无实测支撑的 P3 折档交付 8"
#        就被判成 ok。✅ **补上①的词形后已实测兑现**：全层网格 `PASS=18 FAIL=1`，红的正是承诺账里
#        这 12 格（`CAP承诺? … →Display P3 avif10/12 能力表说支持、产品却交付了 8bit` × 6 源 × 2 档，
#        日志 `tests/output/t36/grid-t36-run3.log`）⇒ 红指向 #39 那条折档，不指向本段的播报。
#   ⇒ #39 已把那条特例**实测否证并删除** ⇒ 本格从"只要求不静默"升级为钉**新契约**：
#     F4 avif + Display P3 + 请求 12 ⇒ **真交付 12bit**（命令行 `-pix_fmt yuv420p12le`）且零位深码；
#     F5 avif + Display P3 + 请求 16 ⇒ 落到编码器上限 12、且**恰好播一次** BitDepthReduced(16→12)。
#     两条都另外要求"交付位深**测得出来**"：测不出时"只看播报"的判据一律放绿 ⇒ 那是 #41 登记的尺子失明。
$c4 = Lock-RunCell "avif12p3" $sdr "avif" "12"
if (-not $c4.Prod) {
  Assert "F4 avif12+P3 不许静默降位" $false "avif --bit-depth 12 + Display P3 无产物（rc=$($c4.Rc)）⇒ 无从比对，判红不放行"
} else {
  $d4 = Lock-DeliveredDepth $c4.Prod.FullName
  $jv4 = Lock-JudgeDepth 12 $c4.Log $d4
  # 出口侧的 `-pix_fmt` = 该格日志里**最后一个** `-pix_fmt <tok>`。引擎路线会先打
  #   `[色彩] ffmpeg … -f rawvideo -pix_fmt rgb48le -video_size 512x384 -i <临时raw> … -pix_fmt yuv420p12le out.avif`
  # 前一个是**喂给编码器的中间件格式**（16bit RGB 中间件），后一个才是**交付档** ⇒
  # 取第一个会把 rgb48le 当成出口，把正确的产物判成红（本轮实测踩过，故在此写明取法）。
  $mm4 = [regex]::Matches($c4.Log, '-pix_fmt\s+([\w]+)')
  $px4 = if ($mm4.Count -gt 0) { $mm4[$mm4.Count - 1].Groups[1].Value } else { '' }
  $n4 = [regex]::Matches($c4.Log, 'BitDepthReduced）').Count
  Assert "F4 avif12+P3 交付位深测得出" ($null -ne $d4) `
    ("交付位深必须当场测得出来（实得 [" + $d4 + ']，产物 pix_fmt 见该格日志）——测不出来时 #41 这类' +
     '静默削档就会和"只看播报"的判据叠成 ok；红先看 Lock-DeliveredDepth 的 pixDepthForLock 是否缺词形')
  Assert "F4 avif12+P3 必须真交付 12bit" `
    (($d4 -eq 12) -and ($px4 -eq 'yuv420p12le') -and ($c4.Log -notmatch 'BitDepth')) `
    ('请求 12 就必须真给到 12：实测交付 [' + $d4 + '] 命令行 pix_fmt=' + $px4 + ' 判据=' + $jv4 +
     ' 日志含位深码=' + ($c4.Log -match 'BitDepth') + '。产品里"avif + Display P3 + 高位深 ⇒ 8bit"那条特例' +
     '已被实测否证并删除（同一命令行只换 -pix_fmt，8/10/12 三档 rc=0、nclx 标签一致、' +
     '10/12 对 8 的 PSNR=51.45/50.99 dB，取证 tests/output/t36/probe_39b.ps1）' +
     '⇒ 本条红 = 有人把那条特例加回来，或在别处另立了一条无据的削档（不许用放宽判据解决）')
  Assert "F4 avif12+P3 同码不重播" ($n4 -le 1) `
    ('BitDepthReduced 在该格日志里出现 ' + $n4 + ' 次（>1 ⇒ 同一事实有两个写者各播一遍，日志自相矛盾；' +
     '与 ①/①b 的去重约定冲突）')
}

# F5 avif + Display P3 + 请求 16 ⇒ 交付必须是**编码器上限** 12（libaom 无 16bit YUV），且恰好播一次。
#   这条是 F4 的另一半：F4 证明"不再无据削档"，F5 证明"该削的还削、并且仍然点名"。
#   读数含义：16 ⇒ 钳制被整个删掉（libaom 编不出 16bit YUV）；8 ⇒ #39 那条特例又回来了。
$c5 = Lock-RunCell "avif16p3" $sdr "avif" "16"
if (-not $c5.Prod) {
  Assert "F5 avif16+P3 无产物" $false "avif --bit-depth 16 + Display P3 无产物（rc=$($c5.Rc)）⇒ 无从比对，判红"
} else {
  $d5 = Lock-DeliveredDepth $c5.Prod.FullName
  $jv5 = Lock-JudgeDepth 16 $c5.Log $d5
  $n5 = [regex]::Matches($c5.Log, 'BitDepthReduced）').Count
  Assert "F5 avif16+P3 交付必须等于编码器上限 12" ($d5 -eq 12) `
    ('实得 [' + $d5 + ']（判据=' + $jv5 + '）——是 16 说明钳制被整个删掉，是 8 说明那条无据特例又回来了；' +
     '而 12 这个数不是本脚本硬编码的，它与 verify-decision-delivery ⑯ 从 ffmpeg 自报表现场读到的上限同源')
  Assert "F5 avif16+P3 必须点名降档" (($jv5 -eq 'reduced-announced') -and ($c5.Log -match 'BitDepthReduced）：出口位深降为 12bit（本次目标 16bit')) `
    ('降档要点名且方向要对（判据=' + $jv5 + ' 实测 [' + $d5 + ']）——静默 16→12 就是任务 #35 那一族回潮；' +
     '红时先查 FfmpegOptions.BitDepthRequested 是否仍在 capBd 钳制之前被记、ColorIntent.RequestedBitDepth 是否仍装配')
  Assert "F5 avif16+P3 同码不重播" ($n5 -le 1) `
    ('BitDepthReduced 在该格日志里出现 ' + $n5 + ' 次（>1 ⇒ 同一事实有两个写者各播一遍，日志自相矛盾）')
}

Write-Host "`n===== PASS=$pass FAIL=$fail =====" -ForegroundColor $(if($fail -eq 0){"Green"}else{"Red"})
Write-Host "关注: cicp-only 的 avif/png 应 ICC=False; carry/hdr-png 应保 HDR(cicp smpte2084); jpeg+gm 应产出可用 UltraHDR" -ForegroundColor Yellow

# ⚠ 2026-09-17：此前本脚本**有 FAIL 但退出码恒为 0**（与 verify-color-engine-xcheck 同类的
# "恒绿门禁"）—— 调用方/CI 只能靠 stdout 判红，一旦输出被吞掉就是静默放过。修法与
# `verify-color-engine-xcheck.ps1` 一致：有 FAIL ⇒ 非零退出码。
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）；失败则保留供排查
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
# ⚠ 退出码**必须放在自清之后**且写成 if/else：宿主删除守卫会污染 `$LASTEXITCODE`（实测变 2），
#   只写 `if ($fail -gt 0) { exit 1 }` 时全绿路径不显式退出 ⇒ 会以被污染的 2 退出（假红）。
if ($fail -gt 0) { exit 1 } else { exit 0 }
