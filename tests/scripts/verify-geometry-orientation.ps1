# verify-geometry-orientation.ps1 —— 取向标签（TIFF Orientation）下「显示尺寸」口径的专项门禁（2026-09-30 新增）
#
# 钉住的缺陷（实机确证，用户素材 DSC00118.TIF：6000x4000、16-bit 无压缩 RGB、Orientation=8）：
#   ffmpeg 对取向标签会 **autorotate**（随包 git-2026-09-26 实测：解码帧挂 `3x3 displaymatrix
#   rotation=90`，帧是 4000x6000），而色彩引擎的 `ProbeSizeAsync` 当时读的是
#   `ffprobe stream=width,height` = **coded** 6000x4000 ⇒ 编码段用 `-video_size 6000x4000`
#   声明一段真实排布为 4000x6000 的 raw ⇒ **每行错位重排**。
#   实测：该产物 vs 真值 **PSNR 7.97 dB**；同一段 raw 按 4000x6000 重排再 `transpose=1` vs 真值
#   **inf**（逐字节等）⇒ 错的是几何标注，不是像素处理，也与 JXL 无关（PNG 出口同样 7.97 dB）。
#   旧的长度守卫 `rawLen < w*h*6` 对此**完全盲**（转置下字节数恒等）。
#
# ⚠ 本脚本不只看 `PROBE RESULT`：结构化行逐条独立复读（探针自身逻辑写错时不会一起假绿）。
# ⚠ 变异验牙（2026-09-30 实做）：把 `ImageGeometry.ApplyRotation` 改成恒等（= 退回 coded 口径）
#   重建后本门禁报 **7 条 FAIL**，读数分别是
#   `probe(显示)=64x32`、`engine=64x32`、`psnr=图不可比:Width and height of input videos must be same`、
#   `filter="scale=16:-2"`（锁错边 ⇒ 产物 16x32，最长边**突破**用户上限 16）。
#
# 退出码：0 = 全绿，1 = 有失败。
#
# ⚠ 双宿主兼容（PS 7 与 PS 5.1，门禁：verify-ps-compat.ps1）：
#   禁止三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁止全角弯引号（用 「」）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$pr  = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $pr)) { $pr = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
# ⚠ 回退是静默的（TESTING 第 84 条）⇒ 一律打印被测 exe 与构建类型，让读数可追溯。
Write-Output ("[gate] exe=" + $pr + $(if ($pr -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
foreach ($need in @($pr, $ff)) { if (-not (Test-Path $need)) { Write-Output "缺工具：$need"; exit 1 } }

# 运行级隔离（GUID）：固定目录会被并发同名实例互删 ⇒ 假红（TESTING §6 第 66 条）
$val  = "$root/tests/output/validate"
$gid  = [guid]::NewGuid().ToString('N').Substring(0, 8)
$logf = "$val/orient_${gid}_probe.txt"
New-Item -ItemType Directory -Force -Path $val | Out-Null
$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }
function Has($log, $lit) { return $log.Contains($lit) }
# 剥掉整行注释后再做结构判据（注释里提到旧写法就把文本锁喂绿 —— 本仓实测踩过）
function CodeLines($path) {
    $out = @()
    foreach ($l in [System.IO.File]::ReadAllLines($path)) {
        $t = $l.TrimStart()
        if ($t.StartsWith('//') -or $t.StartsWith('///') -or $t.StartsWith('/*') -or $t.StartsWith('*')) { continue }
        $out += $l
    }
    return $out
}

Write-Host "`n### 1) 直调引擎（orientation 模式）###" -ForegroundColor Cyan
$p = Start-Process -FilePath $pr -ArgumentList @('orientation', ('"' + $val + '"')) -NoNewWindow -Wait -PassThru `
     -RedirectStandardOutput $logf
if (-not (Test-Path $logf)) { Write-Output "探针没有产出日志文件：$logf（判红，不按缺省绿）"; exit 1 }
$log = [System.IO.File]::ReadAllText($logf)
Write-Output $log
CK ($p.ExitCode -eq 0) "探针退出码 0（实得 $($p.ExitCode)）"
$m = [regex]::Match($log, 'PROBE RESULT: pass=(\d+) fail=(\d+)')
CK ($m.Success -and $m.Groups[2].Value -eq '0') "探针全绿（PROBE RESULT 的 fail 必须为 0，实得 $($m.Value)）"
if ($m.Success) { CK ([int]$m.Groups[1].Value -ge 15) "断言条数 ≥15（实 $($m.Groups[1].Value)）—— 掉了说明有用例被跳过" }
CK (-not (Has $log 'FAIL ')) "探针内部无任何 FAIL 行"

Write-Host "`n### 2) 夹具前提（不成立则整条门禁退化成恒真）###" -ForegroundColor Cyan
CK (Has $log 'orient-fixture coded=64x32 rotation=90 displayExpect=32x64') `
   "② 夹具：coded 64x32 而 rotation=90 ⇒ 显示口径是 32x64（两口径**轴向不同**才有效）"
CK (Has $log 'PASS 负控夹具无取向标签（rotation=0，coded=64x32）') `
   "② 负控夹具确实不带取向标签（否则 O4 测不到「凭空旋转」）"

Write-Host "`n### 3) 探测口径：显示尺寸必须与 coded 不同（这就是被修的那一点）###" -ForegroundColor Cyan
CK (Has $log 'orient-probe coded=64x32 probe(显示)=32x64') `
   "③ ProbeSizeAsync 报 32x64 而非 coded 64x32（旧实现在这一格就是 64x32）"

Write-Host "`n### 4) 逐用例：产出尺寸 + Stats + 像素对照 ###" -ForegroundColor Cyan
$cases = @(
    @{ id = 'O1-16bit';  expect = 'expect=32x64 engine=32x64 stats=32x64' },
    @{ id = 'O2-8bit';   expect = 'expect=32x64 engine=32x64' },
    @{ id = 'O3-maxdim'; expect = 'expect=8x16 engine=8x16 stats=8x16' },
    @{ id = 'O4-negative'; expect = 'expect=64x32 engine=64x32' }
)
foreach ($c in $cases) {
    $pat = 'orient-case id=' + $c.id + ' .*' + $c.expect
    CK ([regex]::IsMatch($log, $pat)) "④ $($c.id)：产出与 Stats 均为期望几何"
}
CK ([regex]::IsMatch($log, 'orient-case id=O1-16bit .*psnr=inf\b')) `
   "④ O1 与独立参照（ffmpeg 直解）**逐字节等**（psnr=inf）"
# O2 的读数**两种合法形态**：`inf`（降位与 ffmpeg 逐字节等）或一个 ≥45 的 dB 数（允许 ±1 LSB 舍入差）。
# ⚠ 不要把判据写成"必须是数字"—— 那会把 `inf`（零误差，严格更好）误判成"缺读数"（2026-09-30 实测踩过：
#   同一夹具两次读数 58.38 → inf，第二次被自己的解析判红）。真正该判红的是「图不可比」/「无读数」。
$mp2 = [regex]::Match($log, 'orient-case id=O2-8bit .*psnr=(inf|[0-9][0-9.]*)')
if (-not $mp2.Success) {
    CK $false "④ O2 的 psnr 读数不可用（实为两臂不可比或没跑成 ⇒ 判红）"
} elseif ($mp2.Groups[1].Value -eq 'inf') {
    CK $true "④ O2 8-bit 出口与独立参照逐字节等（psnr=inf）"
} else {
    CK ([double]$mp2.Groups[1].Value -ge 45.0) `
       "④ O2 8-bit 出口 psnr = $($mp2.Groups[1].Value) dB（≥45；剪切会掉到个位数）"
}

CK ([regex]::IsMatch($log, 'orient-case id=O3-maxdim filter="scale=-2:16')) `
   "④ O3 最长边锁的是**纵向**（scale=-2:16）⇒ 判据用的是显示口径；按 coded 会选成 scale=16:-2"
CK (Has $log 'PASS 参照臂前提：ffmpeg 直解产物 coded=32x64') `
   "④ 参照臂自身不带 rotation ⇒ 对拍的是几何口径，不是「两次旋转是否抵消」"

Write-Host "`n### 5) 残留自证：探针的运行级目录必须自清 ###" -ForegroundColor Cyan
$mo = [regex]::Match($log, 'orient-out=(\S+)')
CK ($mo.Success) "⑤ 探针报告了运行级目录（orient-out=…）"
if ($mo.Success) {
    $pout = $mo.Groups[1].Value -replace '\\', '/'
    CK (-not (Test-Path $pout)) "⑤ 运行级目录已消失（无残留）：$pout"
}
$mc = [regex]::Match($log, 'orient-cleanup: 删除文件 (\d+)/(\d+)')
if ($mc.Success) {
    CK ($mc.Groups[1].Value -eq $mc.Groups[2].Value) `
       "⑤ 按计数自清：删除 $($mc.Groups[1].Value)/$($mc.Groups[2].Value) 个文件（必须相等）"
} else { CK $false "⑤ 缺 orient-cleanup 行（自清未执行）" }

Write-Host "`n### 6) 元数据恢复不得把 Orientation 抄回已旋正的产物（第二个取向缺陷）###" -ForegroundColor Cyan
# 根因：两条恢复通路（-all:all 与 -EXIF:all 安全模式）都不排除 Orientation ⇒ 像素修对了、标签仍会让
#   查看器二次旋转。实测（修前）：对 4000x6000 的产物跑恢复命令 ⇒ exiftool 读回 Orientation=8。
$logf2 = "$val/orient_${gid}_meta.txt"
$pm = Start-Process -FilePath $pr -ArgumentList @('orientmeta', ('"' + $val + '"')) -NoNewWindow -Wait -PassThru `
     -RedirectStandardOutput $logf2
if (-not (Test-Path $logf2)) {
    CK $false "⑥ orientmeta 探针没有产出日志（$logf2）⇒ 判红，不按缺省绿"
} else {
    $mlog = [System.IO.File]::ReadAllText($logf2)
    Write-Output $mlog
    CK ($pm.ExitCode -eq 0) "⑥ orientmeta 退出码 0（实得 $($pm.ExitCode)）"
    $mm = [regex]::Match($mlog, 'PROBE RESULT: pass=(\d+) fail=(\d+)')
    CK ($mm.Success -and $mm.Groups[2].Value -eq '0') "⑥ orientmeta 全绿（实得 $($mm.Value)）"
    if ($mm.Success) { CK ([int]$mm.Groups[1].Value -ge 8) "⑥ orientmeta 断言数 ≥8（实 $($mm.Groups[1].Value)）" }
    # 逐条点名：M1/M3 是修复本体，M2 是"排除不是掐掉整块复制"的控制臂，M4 是重封装反证臂
    foreach ($k in @('M1 全量复制', 'M2 同一次复制必须仍把 Make/Model 带过去', 'M3 安全模式', 'M4 传 dropOrientation:false', 'M5 重封装判据三格')) {
        CK ([regex]::IsMatch($mlog, 'PASS ' + [regex]::Escape($k))) "⑥ 在位：$k"
    }
    CK ([regex]::IsMatch($mlog, 'orientmeta-fixture|夹具：Orientation=8 且\*\*显示\*\*尺寸 = 32x64')) `
       "⑥ 夹具前提：源 coded 64x32 而显示 32x64（两口径同轴则本段恒真）"
    Remove-Item -Force $logf2 -ErrorAction SilentlyContinue
}
# 结构锁：排除式的**形态**与**覆盖面**（行为判据只测当前夹具，形态回退要靠这里）
$srcExif = Join-Path $root 'src/FfmpegGui/Services/ExifToolService.cs'
$exifCode = ((CodeLines $srcExif) -join "`n")
CK ($exifCode -notmatch [regex]::Escape('--Orientation:all')) `
   "⑥ 结构锁：排除式不得写成 --Orientation:all（实测该形态**不生效**，见 ExifToolService 注释）"
$uses = @([regex]::Matches($exifCode, 'OrientationExclusion')).Count
CK ($uses -ge 3) "⑥ 结构锁：两条恢复通路都吃到排除式（定义 1 + 引用 $uses 处，须 ≥3）"
$cj = ((CodeLines (Join-Path $root 'src/FfmpegGui/Services/CjxlService.cs')) -join "`n")
$qp = ((CodeLines (Join-Path $root 'src/FfmpegGui/Services/QueueProcessor.cs')) -join "`n")
CK (($cj -match 'UsesLosslessJpegRewrap') -and (($qp -match 'UsesLosslessJpegRewrap'))) `
   "⑥ 结构锁：重封装判据单一实现，且 cjxl 命令行与恢复步骤**都**在用它"
CK (@([regex]::Matches($cj, '--lossless_jpeg=1')).Count -eq 1) `
   "⑥ 结构锁：--lossless_jpeg=1 只有一处发射点（多处 ⇒ 判据会各写一份）"

Write-Host ""
Write-Host "===== geometry-orientation: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
Remove-Item -Force $logf -ErrorAction SilentlyContinue
exit $(if ($fail -eq 0) { 0 } else { 1 })
