# 引擎 vs 外部参照（libzimg/zscale）逐像素互测 —— 可行性硬门禁
# 生成含饱和色与灰阶斜坡的测试图，跑多组转换，汇总 PASS/FAIL。
#
# ── 2026-09-17 修复：本门禁此前**恒 exit 0**（一个永远不会失败的门禁 = 没有门禁）──────────
#   根因：全文没有任何 `exit`，`$allOk` 只决定打印文案。
#   同时补上 6 格**已知非缺陷**的显式白名单（依据见下方 `$knownNonDefects` 的原文引用），
#   否则补上退出码后会**立刻长期红**（正是 `docs/TESTING.md §6` 第 55 条警告的模式：
#   把「已定性的口径分歧」当缺陷修 ⇒ 要么白改、要么把参照口径改坏）。
#   ⚠ 白名单是**有意的、精确的**：只豁免那 6 个「图/用例」组合；**新增**失败（含新图、新用例）
#     必须照常红。变异验证：从 `$knownNonDefects` 移除任一条 ⇒ 该格转红（见交付报告）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff  = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$pr  = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   本脚本在必跑清单内 ⇒ 自并发是**真实**风险（不是理论风险），故隔离。
#   代价评估（已实测/已核）：清单内**无**消费者读 `cmsx`；唯一的清单外消费者
#   `_probe-zimg-noop-check.ps1` 读的是 `$d/A|B/zimg.png`，而**全仓没有任何脚本写这个路径**
#   （`grep zimg.png` 只命中该消费者自身）⇒ 它本就是**恒 `continue` 的死诊断**，隔离不损失任何活链。
$d   = "$root/tests/output/validate/cmsx_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
# ⚠ `-EA SilentlyContinue` 是**环境必需**（2026-09-17）：本宿主的批量删除守卫在累计删除量超阈值时
#   会往脚本上下文抛 `[safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED]`（删除其实已成功，见
#   `docs/HANDOVER.md` 宿主限制一节）⇒ 结尾自清不加这句会**吞掉汇总行的输出顺序**并污染 `$LASTEXITCODE`。
New-Item -ItemType Directory -Force -Path $d | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# 1) 测试图：smptehdbars（含饱和原色/彩条/灰阶）+ 一张渐变斜坡，均 16bit PNG
$null = Exec $ff "-y -v error -f lavfi -i `"smptehdbars=s=1024x768`" -pix_fmt rgb48le `"$d/bars16.png`""
$null = Exec $ff "-y -v error -f lavfi -i `"testsrc2=s=640x480:duration=1:rate=1`" -frames:v 1 -pix_fmt rgb48le `"$d/t2.png`""
foreach ($f in @("bars16.png","t2.png")) {
  $p = "$d/$f"
  Write-Output ("  生成 {0}: {1} B" -f $f, (Get-Item $p -EA SilentlyContinue).Length)
}

# 2) 互测用例：源描述 → 目标描述
$cases = @(
  @{ n = "sRGB→DisplayP3";   sp="bt709";   st="iec61966-2-1"; dp="smpte432"; dt="iec61966-2-1" },
  @{ n = "DisplayP3→sRGB";   sp="smpte432"; st="iec61966-2-1"; dp="bt709";   dt="iec61966-2-1" },
  @{ n = "BT.2020→sRGB";     sp="bt2020";  st="bt709";        dp="bt709";    dt="iec61966-2-1" },
  @{ n = "sRGB→BT.2020";     sp="bt709";   st="iec61966-2-1"; dp="bt2020";   dt="bt709" },
  @{ n = "PQ(BT.2020)→sRGB"; sp="bt2020";  st="smpte2084";    dp="bt709";    dt="iec61966-2-1" }
)
# ── 已知非缺陷白名单（**有意的豁免**；新增失败必须照常红）─────────────────────────────
# 依据（原文引用，均为本项目既有结论）：
#   • `docs/MANUAL_VERIFICATION.md:223-224` ——
#     「**已知非缺陷**：`verify-color-engine-xcheck` 的 6 格 FAIL，经控制组实验判定为
#       **zimg 参照口径分歧**（zimg 的 bt709 是纯 γ2.4 / PQ 归一约定不同），**不是引擎缺陷**（详见 `HANDOVER §D`）」
#   • `docs/archive/BUILD_HANDOVER.md:123-124` ——
#     「⚠ 唯一 FAIL：`verify-color-engine-xcheck` —— **已知非缺陷**（zimg 参照口径分歧，经控制组实验判定，见 `HANDOVER §D`）」
#   • `docs/archive/BUILD_HANDOVER.md:151` ——
#     「本环境的 `verify-color-engine-xcheck` FAIL **不是新问题**，不要试图「修绿」它 ——
#       它是刻意保留的对照标记（口径分歧已定性）」
# ⚠ 精确性要求：键 = 「图/用例」二元组，共 6 条，与实测失败格**逐一对应**（不含任何通配）。
#   任何**新增**失败（新图、新用例、或这 6 格之外的任何格）都不在白名单内 ⇒ 照常判红。
$knownNonDefects = @{
  "bars16.png/BT.2020→sRGB"     = "zimg 参照口径分歧：zimg 的 bt709 是纯 γ2.4（与定义式分歧）"
  "bars16.png/sRGB→BT.2020"     = "zimg 参照口径分歧：zimg 的 bt709 是纯 γ2.4（与定义式分歧）"
  "bars16.png/PQ(BT.2020)→sRGB" = "zimg 参照口径分歧：PQ 归一约定不同"
  "t2.png/BT.2020→sRGB"         = "zimg 参照口径分歧：zimg 的 bt709 是纯 γ2.4（与定义式分歧）"
  "t2.png/sRGB→BT.2020"         = "zimg 参照口径分歧：zimg 的 bt709 是纯 γ2.4（与定义式分歧）"
  "t2.png/PQ(BT.2020)→sRGB"     = "zimg 参照口径分歧：PQ 归一约定不同"
}

$allOk = $true
$waived = 0
foreach ($img in @("bars16.png","t2.png")) {
  Write-Host "`n########## 图: $img ##########" -ForegroundColor Cyan
  foreach ($c in $cases) {
    $out = "$d/x_$($c.n -replace '[^A-Za-z0-9]','_')"
    $o = (Exec $pr "xcheck `"$d/$img`" $($c.sp) $($c.st) $($c.dp) $($c.dt) `"$out`"") | Out-String
    $line = ($o -split "`n" | Where-Object { $_ -match "PSNR" -or $_ -match "^FAIL" }) -join " || "
    Write-Output ("  {0,-20} {1}" -f $c.n, $line.Trim())
    $m = [regex]::Match($o, 'PROBE RESULT: pass=(\d+) fail=(\d+)')
    if (-not $m.Success) {
        # 解析不到汇总行（探针崩溃/参数错）也必须判红 —— 不给"没输出 = 没失败"式假绿
        Write-Host "        ↳ 未解析到 PROBE RESULT 行 ⇒ 判红" -ForegroundColor Red
        $allOk = $false
    } elseif ($m.Groups[2].Value -ne "0") {
        $key = "$img/$($c.n)"
        if ($knownNonDefects.ContainsKey($key)) {
            $waived++
            Write-Host ("        ↳ 白名单豁免（已知非缺陷）：{0}" -f $knownNonDefects[$key]) -ForegroundColor Yellow
        } else {
            Write-Host "        ↳ 不在白名单内 ⇒ 判红" -ForegroundColor Red
            $allOk = $false
        }
    }
  }
}
Write-Host "`n===== xcheck 总结: $(if ($allOk) {'全部通过'} else {'存在失败'})（白名单豁免 $waived 格） =====" -ForegroundColor $(if ($allOk) {"Green"} else {"Red"})
# ── 退出码语义（2026-09-17 新增）────────────────────────────────────────────────
#   **除白名单外的任何一格失败 ⇒ exit 1**；白名单内那 6 格不判红（依据见上方 `$knownNonDefects`）。
#   ⇒ 现状（6 格全豁免）**exit 0**；出现**新增**失败时 exit 1。
#   修复前：全文无 `exit` ⇒ **恒 exit 0**（失败也不报，等于没有门禁）。
# 跑绿自清（跑红保留 GUID 目录供排查）—— 必须在 exit 之前，且用显式 if/else 给出退出码（§6 第 66/67 条）。
if ($allOk) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
if (-not $allOk) { exit 1 } else { exit 0 }
