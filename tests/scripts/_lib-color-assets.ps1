# _lib-color-assets.ps1 —— 色彩门禁**共用素材**生成（**不是门禁**：不产 PASS/FAIL、不登记进 `_run-step3-gates.ps1`）
#
# 为什么存在（2026-09-17，登记见 docs/TESTING.md §6「门禁依赖另一门禁的遗留产物」）：
#   `verify-color-caps.ps1` 与 `verify-color-wiring.ps1` **都要** `validate/caps/p3.icc`。
#   此前 wiring 直接读 caps 的输出目录 ⇒ 形成「一条门禁依赖**另一条门禁的遗留产物**」：
#     · 干净 `tests/output` 上 wiring 的 (8) 段**必红**（素材不存在）；
#     · 而"单跑绿、全量红"极易被误判成**产品回归**（本轮实测就出现 73/1 的假红）。
#   修法：把生成机制抽成**唯一一份**（本文件），两边 dot-source 调用 —— 谁需要谁自备，
#   **不新增第二套生成口径**（本项目已多次因"两处口径"吃亏）。
#
# ⚠ 双宿主兼容（PS 5.1 / PS 7）：禁 `? :` / `??` / `?.` / `&&` / `||`；
#   双引号串内禁全角弯引号（用 `「」`）。门禁：`verify-ps-compat.ps1`。
# ⚠ 本文件**只在 dot-source 时定义函数**，不得有顶层副作用（否则谁 dot-source 谁跑一遍素材生成）。
# ⚠ 取子进程输出一律走 `Start-Process` + 重定向：`& exe` 在部分宿主静默失败（输出为空 ⇒ 假红/空值）。

# 生成 P3 素材：`p3.png`（内嵌 Display P3 ICC 的 16bit PNG）+ 由它提取的纯 `p3.icc`。
# 机制与 `verify-color-caps.ps1` 原先内联的那两行**逐字一致**（ffmpeg `zscale+iccgen` 造 P3 PNG，
# exiftool `-icc_profile` 提取）—— 抽出来只为去掉第二套口径，**没有改任何参数**。
#
# 返回：[pscustomobject] @{ Png; Icc; IccLength; FfmpegExit; ExiftoolExit }
#   `IccLength > 200` 才是"素材就绪"（caps 的既有判据：空的/坏的文件不可能有 200B）。
function New-P3IccAsset {
    param(
        [Parameter(Mandatory = $true)][string]$Dir,       # 素材目录（不存在则创建）
        [Parameter(Mandatory = $true)][string]$Ffmpeg,    # ffmpeg.exe 全路径
        [Parameter(Mandatory = $true)][string]$Exiftool   # exiftool.exe 全路径
    )
    if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Force -Path $Dir | Out-Null }
    $png = "$Dir/p3.png"
    $icc = "$Dir/p3.icc"
    $o = "$Dir/_assetgen.out"; $e = "$Dir/_assetgen.err"
    # ⚠ 必须先删旧 `p3.icc`：exiftool 的 `-w` **不覆盖已存在的文件**（实测：0B 残留会让"自备"变成空操作，
    #   于是素材仍是 0B、(8) 段照旧红）。caps 因为开头整目录删过，所以从没暴露这一点；
    #   本函数被"缺则自备"的调用方复用时**必须自己保证幂等**。
    if (Test-Path $icc) { Remove-Item -Force $icc -ErrorAction SilentlyContinue }

    $p1 = Start-Process -FilePath $Ffmpeg -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e `
        -ArgumentList "-y -hide_banner -loglevel error -f lavfi -i `"smptehdbars=s=64x64`" -vf `"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709,iccgen=color_primaries=smpte432:color_trc=iec61966-2-1:force=1`" -pix_fmt rgb48be -frames:v 1 `"$png`""

    $p2 = Start-Process -FilePath $Exiftool -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e `
        -ArgumentList "-b -icc_profile -w `"$Dir/%f.icc`" `"$png`""

    $len = 0
    if (Test-Path $icc) { $len = (Get-Item $icc).Length }
    return [pscustomobject]@{
        Png          = $png
        Icc          = $icc
        IccLength    = $len
        FfmpegExit   = $p1.ExitCode
        ExiftoolExit = $p2.ExitCode
    }
}
