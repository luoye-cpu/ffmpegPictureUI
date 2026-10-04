# verify-gainmap-host.ps1 —— 把 `tests/GainMapTestHost` 全量套件接进清单（2026-09-30 新增，清单第 60 条）
#
# 为什么需要（这条门禁存在的理由本身就是遗留问题的答案）：
#   该宿主此前**不被任何门禁执行** —— 运行器自己在 `_run-step3-gates.ps1` 里登记着
#   「tests/GainMapTestHost 不在本闸内（其 exe 不被任何门禁执行）」。于是里面 **4 条红稳定存在了 10 天**
#   （09-20 的「SDR ⇒ 普通 JPEG」裁定把 `sdr` 格的 UltraHDR 期望作废，测试端没跟着改），
#   每次都是**手工**跑宿主才偶然看见。⇒ 与 2026-09-19 P4-A 那批「有锁但不上锁」同性质。
#
# 判据（不只看汇总行，防"红了但没计数"与"用例被静默删掉"）：
#   ① 必须出现 `═══ 汇总: 通过 N, 失败 0, 已知问题 M ═══`，且 N ≥ 103（基线演进：86 →（2026-10-01 补
#      取向标签两条用例 E1 九条 + E2 八条）**103**；再往前是"原 80 + 把 `sdr` 格 3 条过期期望换成 6 条契约断言"）；
#   ② 逐条 `❌` 的行数必须**等于**汇总里的失败数（不等 ⇒ 计数与明细脱钩）；
#   ③ 零余量（SDR）契约的 6 条断言必须**逐条点名在位** —— 这 6 条是 2026-09-20 裁定
#      （`GainMapEncoder.cs:188-197`）唯一的机器可验背书，缺任何一条都算覆盖塌陷；
#   ④ 取向标签（`Orientation=8`）的 4 条承重断言必须点名在位（E1f/E1i/E2c/E2g2）——
#      其中 **E2g2 是"判据没被放宽"的证据**：轴向真不一致的产物仍必须被拒；
#   ⑤ 宿主 exe 缺失 ⇒ **判红**（不静默跳过）；并打印被测产物路径与构建类型（TESTING 第 84 条）。
#
# ⚠ 本门禁跑的是 `tests/GainMapTestHost/bin/Release/.../GainMapTestHost.exe`，
#   其新鲜度由运行器的 STALE 第 ⑤ 对负责（该 exe vs `tests/GainMapTestHost` 源码）。
#
# 退出码：0 = 全绿，1 = 有失败。
#
# ⚠ 双宿主兼容（PS 7 与 PS 5.1，门禁：verify-ps-compat.ps1）：
#   禁止三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁止全角弯引号（用 「」）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$exe = "$root/tests/GainMapTestHost/bin/Release/net11.0/win-x64/GainMapTestHost.exe"
if (-not (Test-Path $exe)) { $exe = "$root/tests/GainMapTestHost/bin/Debug/net11.0/win-x64/GainMapTestHost.exe" }
$tag = " (Release)"
if ($exe -like '*[/\]Debug[/\]*') { $tag = " (Debug fallback)" }
Write-Output ("[gate] exe=" + $exe + $tag)
if (-not (Test-Path $exe)) { Write-Output "  FAIL 宿主未构建：$exe（先 dotnet build tests/GainMapTestHost -c Release）"; exit 1 }

$val  = "$root/tests/output/validate"
$gid  = [guid]::NewGuid().ToString('N').Substring(0, 8)
$logf = "$val/gmhost_${gid}.txt"
New-Item -ItemType Directory -Force -Path $val | Out-Null
$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }

Write-Host "`n### 1) 跑全量宿主（A 编码 / B 解码闭环 / C 结构 / D PNG3.0 / 像素内核）###" -ForegroundColor Cyan
# ⚠⚠ 2026-10-04 修（PS5.1 双宿主合规，实测）：
#   原写法 `-ArgumentList @()` 在 **Windows PowerShell 5.1** 下**直接抛参数绑定异常**，整条门禁
#   在 5.1 宿主里 exit=1 且**不产出日志**（实测报错原文）：
#     `Start-Process : Cannot validate argument on parameter 'ArgumentList'. The argument is null,
#      empty, or an element of the argument collection contains a null value.`
#   ⇒ 本仓铁律「静态 PARSEOK ≠ 双宿主可用」在本条上**实测成立**：`verify-ps-compat` 的静态扫描
#     查不出它（语法合法），只有真 5.1 宿主跑一次才暴露。
#   修法：该宿主**不需要任何参数** ⇒ 直接**省略 `-ArgumentList`**（PS5.1/PS7 都接受），
#     而不是传 `@()` 或 `''`（前者 5.1 抛错，后者会塞进一个空字符串实参）。
#   ⚠ 验收必须在**真 PS5.1** 下重跑（`$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe`），
#     不能只看静态兼容门禁 —— 本缺陷正是"静态绿、真机红"。
$p = Start-Process -FilePath $exe -NoNewWindow -Wait -PassThru -RedirectStandardOutput $logf
if (-not (Test-Path $logf)) { Write-Output "  FAIL 宿主没有产出日志：$logf（判红，不按缺省绿）"; exit 1 }
$log = [System.IO.File]::ReadAllText($logf)
# 明细先原样落一份（只留汇总行会让"为什么红"无法复盘）
Write-Output ($log -split "`r?`n" | Where-Object { $_ -match '❌|汇总|── ' } | Select-Object -First 40)

Write-Host "`n### 2) 汇总行与计数对账 ###" -ForegroundColor Cyan
$m = [regex]::Match($log, '汇总: 通过 (\d+), 失败 (\d+), 已知问题 (\d+)')
CK ($m.Success) "② 汇总行在位（实得「$($m.Value)」）"
if (-not $m.Success) { Write-Host "===== gainmap-host: PASS=$pass FAIL=$(( $fail + 1 )) ====="; exit 1 }
$np = [int]$m.Groups[1].Value; $nf = [int]$m.Groups[2].Value; $nk = [int]$m.Groups[3].Value
CK ($nf -eq 0) "② 失败数 = 0（实得 $nf）"
CK ($nk -eq 0) "② 已知问题数 = 0（实得 $nk，非 0 说明有断言被标成已知失败）"
CK ($np -ge 103) "② 通过数 ≥103（实得 $np）—— 掉了说明有用例被跳过或删除"
# ⚠ 只数**汇总行之前**的 ❌：宿主在汇总之后又把失败清单**重打一遍**
#   （`Program.cs:71-73` 先打汇总、再 `foreach (_failures) Console.WriteLine("  ❌ …")`）
#   ⇒ 全文计数恒为 2×失败数，那条对账就永远是假红。
$head = $log
$idx = $log.IndexOf('汇总: 通过')
if ($idx -gt 0) { $head = $log.Substring(0, $idx) }
$bad = @([regex]::Matches($head, '(?m)^\s*❌')).Count
CK ($bad -eq $nf) "② 汇总前逐条 ❌ 行数($bad) == 汇总失败数($nf)（不等 ⇒ 计数与明细脱钩）"
CK ($p.ExitCode -eq 0) "② 宿主退出码 0（实得 $($p.ExitCode)）"

Write-Host "`n### 3) 零余量（SDR）契约的 6 条必须逐条在位 ###" -ForegroundColor Cyan
# 这 6 条背书的是 `GainMapEncoder.cs:188-197` 的 2026-09-20 裁定：
#   headroom ≤ 1 ⇒ **不写增益图**、按普通 JPEG 交付并点名；解码侧不许凭空造增益。
$must = @(
    '零余量 ⇒ 日志点名「无可承载的 HDR 增益」降级（不静默）',
    '产物是完整 JPEG（SOI FFD8 \+ EOI FFD9',
    '负断言——\*\*不得\*\*残留 MPImage2',
    '负断言——\*\*不得\*\*残留 hdrgm GainMapMax',
    '降级只去掉增益层，底图像素尺寸仍是 256x192',
    '零余量产物被解码器判为\*\*非 UltraHDR\*\*'
)
foreach ($k in $must) {
    CK ([regex]::IsMatch($log, 'sdr: ' + $k)) "③ 在位：$k"
}
# 反向锁：`sdr` 格**不许**再出现"MPImage2 (UltraHDR 结构) 存在"这类过期期望
#（裁定之后该断言恒假；留着它就是把契约重新写错）
CK (-not [regex]::IsMatch($log, 'sdr: MPImage2 \(UltraHDR 结构\) 存在')) `
   "③ 反向锁：`$sdr` 格不再断言 UltraHDR 结构存在（09-20 裁定后该期望恒假）"

Write-Host "`n### 4) 取向标签（Orientation=8）的 4 条承重断言必须点名在位 ###" -ForegroundColor Cyan
# 这四条钉的是 2026-09-30 那条"coded vs 显示"缺陷链在 GainMap 解码与质量分析两条通路上的修复：
#   E1f  ⇒ `GainMapDecoder` 必须报**显示**尺寸（coded 256x192 ⇒ 显示 192x256）
#   E1i  ⇒ 线性产物 vs ffmpeg 独立参照（显示几何）PSNR ≥40dB
#   E2c  ⇒ 取向源 × 旋正产物**不得**被"分辨率不一致"拒绝（修前的假红方向）
#   E2g2 ⇒ 反证臂：轴向**真**不一致的产物仍必须被拒 ⇒ 证明 E2c 不是把判据放宽换来的绿
$orient = @(
    'E1f GainMapDecoder 报\*\*显示\*\*尺寸',
    'E1i 线性产物 vs ffmpeg 独立参照（显示几何）',
    'E2c 取向源 × 旋正产物 ⇒ \*\*不得\*\*报「分辨率不一致」',
    'E2g2 反证: 轴向真不一致的产物仍必须被「分辨率不一致」'
)
foreach ($k in $orient) {
    CK ([regex]::IsMatch($log, $k)) "④ 在位：$k"
}

Write-Host "`n### 5) 残留自证 ###" -ForegroundColor Cyan
CK ($p.ExitCode -eq 0 -and $log -match '汇总') "⑤ 宿主跑完（未中途异常退出）"

Write-Host ""
Write-Host "===== gainmap-host: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
Remove-Item -Force $logf -ErrorAction SilentlyContinue
exit $(if ($fail -eq 0) { 0 } else { 1 })
