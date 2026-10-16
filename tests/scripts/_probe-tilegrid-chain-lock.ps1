# 结构锁：分块流组的「简单 vs 复合滤镜」形态判据不许被绕过
#
# 为什么需要它（真图实测，docs/FIX_PLAN_2026-10-14.md 的 T1–T4）：
#   ffmpeg ≥2025-10-30 对 Tile Grid 流组自动建 xstack 复合图（istg_parse_tile_grid），
#   输出流由复合图喂 ⇒ 同一条流再挂 `-vf` 直接 EINVAL、零产物。
#   缺陷的复发方式是「有人在新地方又写了一条裸 -vf」，而不是判据算错 ⇒ 用**词法**锁住发射点集合。
#
# 用法：pwsh -NoProfile -File tests/scripts/_probe-tilegrid-chain-lock.ps1 [-Files <待扫文件>]
# 有牙自检（本仓规矩：判据必须在未加固形态上判过一次红）：
#   把某份带裸 -vf 的副本指给 -Files，脚本必须判红。

param([string[]]$Files = @())

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $Files -or $Files.Count -eq 0) {
    $Files = @(
        (Join-Path $root 'src/FfmpegGui/Services/FfmpegCommandBuilder.cs'),
        (Join-Path $root 'src/FfmpegGui/Services/QueueProcessor.cs'),
        (Join-Path $root 'src/FfmpegGui/Services/ThumbnailService.cs'),
        (Join-Path $root 'src/FfmpegGui/Services/IccProfileService.cs'),
        (Join-Path $root 'src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs'),
        (Join-Path $root 'src/FfmpegGui/Services/ColorMapping/GainMapJpegCodec.cs')
    )
}

# 允许出现裸 -vf 的位置：
#  ① 收口入口自身（FfmpegCommandBuilder.cs 内 CreateVideoChain 的新建点，全仓只许 1 处）；
#  ② 打了行内豁免标记 `tile-lock: flattened-input` 的通路——其输入是**已拍平的中间件**
#     （raw/ppm/PNG 序列），结构上不可能是流组。按标记而非按 文件:行号 豁免，行号漂移不会让锁失效。
$BuilderName = 'FfmpegCommandBuilder.cs'

$fails = 0
$hits  = 0
$exempt = 0
foreach ($f in $Files) {
    if (-not (Test-Path $f)) { Write-Host "FAIL=文件不在位: $f"; $fails++; continue }
    $name = Split-Path -Leaf $f
    $n = 0
    Get-Content -LiteralPath $f -Encoding UTF8 | ForEach-Object {
        $n++
        $line = $_
        # 只认「发射」形态：args.Add("-vf") 与 -vf \"...\" ；注释、FindIndex(a => a == "-vf") 不计
        $isEmit = ($line -match 'Add\("-vf"\)') -or ($line -match '-vf \\""') -or ($line -match '\-vf \\"')
        if (-not $isEmit) { return }
        if ($line -match '^\s*(//|/\*|\*)') { return }
        if ($line -match 'FindIndex') { return }
        $tag = "$name`:$n"
        if ($line -match 'tile-lock: flattened-input') { $exempt++; return }
        if ($name -eq $BuilderName -and $line -match 'Add\("-vf"\)') {
            # 收口入口内的新建点全仓只许 1 处（CreateVideoChain）；ChainArgs* 的返回串不算裸发射
            $hits++
            if ($hits -gt 1) { Write-Host "FAIL=第 $hits 处收口内新建 -vf（应恒为 1）: $tag"; $fails++ }
            return
        }
        Write-Host "FAIL=未收口的裸 -vf 发射: $tag  =>  $($line.Trim())"
        $fails++
    }
}

$checks = $hits + $exempt + 1     # 收口新建点数 + 豁免条目数 + "全仓唯一 sink"这一总判
$passed = $checks - $fails
Write-Host ("===== tilegrid-chain-lock: PASS=$passed FAIL=$fails   (收口内新建点=$hits, 行内豁免=$exempt) =====")
if ($fails -eq 0) { exit 0 }
exit 1
