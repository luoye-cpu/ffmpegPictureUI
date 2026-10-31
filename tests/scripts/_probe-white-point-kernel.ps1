# _probe-white-point-kernel.ps1 —— 白点顶码契约锁（跨位深、双向有牙）
#
# 锁什么：**凡是内核（H1）真正介入的出口，交付白必须等于目标位深的满幅码** —— 8-bit=255、16-bit=65535；
#   并且 16-bit 出口在 65284..65535 这 **252 个顶段码**上不许塌缩成同一个值。前一句钉"往上不该丢 1 LSB"，
#   后一句钉"往下不该拿 65283 当除数/上限" —— 两句合起来才是「按输入位深选除数」这一契约，缺一条只剩半截。
#   第三条（2026-10-31 补，B3 臂）：8-bit 源走 **16-bit 交付**时白必须顶到 65535 —— 8-bit 出口只差 1 LSB，
#   肉眼与阈值都难分；16-bit 出口把"归一除数取错"放大成 **252 码头**，一眼可判。
#
# 为什么需要它（台账 S8 三路命中，本脚本把它落成判据）：
#   · 审计 `FINDINGS_LEDGER.md` §A **S8**（P2·三路命中）+ §F **G11**：改它没人拦、它坏也没人报
#     —— `verify-jpg-webp-matrix.ps1` 在 `ColorKernels.cs` 的 `/65535.0→/65283.0` 变异件下仍 `PASS=30 FAIL=0`
#     ⇒ 对 8-bit 精度**全盲**（同一条门禁对矩阵变异立刻 15 红）。本文件就是补上的那颗牙。
#   · `lanes/white-bias/FINDINGS.md` 交付表：`PW_srgb`=254（P3 标签源→sRGB，日志实打 `决策：Map … 后端=H1`），
#     同夹具 `PW_auto`=255 而日志是 `决策：None` ⇒ **未进内核的臂是无效控制**，不能当"没坏"的证据
#     ⇒ 本脚本每条产品臂都从日志验 `H1`，验不到就 **FAIL（点名无效控制）而不是 SKIP**；直通臂单独作为对照组并要求 `H1` 缺席。
#   · 台账 **G16**（当时"未证"）：`/65283` 那一对齐会把真 16-bit 源的 65283..65535 共 253 码压平，
#     缺的正是"一条能进内核的 16-bit 夹具"⇒ 本脚本的 B 组两臂就是来补这条的（夹具带 cICP 标签 + 从日志实打 H1）。
#   · 代码现场（本轮 2026-10-31 实读 HEAD `5fd3996`）：`src/FfmpegGui/Services/ColorMapping/ColorKernels.cs:634-636`
#     仍是逐字 `s[o + 0] / 65535.0`（16-bit 主循环把 ffmpeg 交来的 rgb48le 样本按 65535 归一）。
#     ⇒ **本脚本在 HEAD 上 A 组两臂应判红**，这是契约锁的正确行为（S8 未修，红了才对），不是仪表坏；
#     修好后 A 组转绿、B 组与 C 组须全程绿。
#   ↳ **2026-10-31 更正（S8 已修）**：本锁全场现在全绿（PASS=27 FAIL=0 SKIP=0 ENV=0）。上文"HEAD 上应红"保留为
#     **未修件**的读数口径 —— 未修件实测 A 组 = 254、本件 B3 臂 = 65283（见
#     `tests/output/_tmp/control_unfixed.log`，那是把 `ResolveSrcCodeMax` 恒返 65535.0 的对照构建）。
#     ⚠ 审查档 §S8 ③ 记的「16-bit 交付白 = 65269」出自**另一条臂另一份夹具**（`lanes/pixel-truth/out/R16/sw12.png`
#       斜坡件，4096 px），与本件 B3 臂（P3 纯白块 → sRGB，白点过矩阵后仍 1.0）**不是同一读数口径**；
#       两者都成立，别拿其中一个去否另一个。分界看的是「是否顶到 65535」，不是顶到哪一个小数。
#     要复现红，用 `publish/FFmpegPictureUI-v1.6.0-beta10-x64-full.7z` 的 exe（sha256[:16]=`f89b247b60dcf1ef`）。
#
# 为什么必须带顶段斜坡、不能只放一张全白图（双向牙齿的道理）：
#   随包 ffmpeg 把 rgb24 解到 rgb48le 的顶码是 **65283**（0.99615 FS，见下面 [mech] 行；非 ×257 复制，
#   实测 256 个码里只有 1 个是 257 的倍数）。应用按 65535 归一 ⇒ 8-bit 白 255→254。若有人拿
#   "改成 /65283"当修复：8-bit 白立刻 255（看着修好了），可真 16-bit 源的 65283..65535 全被算成 ≥1.0
#   再钳位 ⇒ 顶端 253 码塌成一个值 —— **全白夹具对此完全失明**。所以 16-bit 夹具在白块之外铺
#   65024..65535 逐码斜坡，并对"交付后仍互异（=252）"下断言。
#
# 仪表真相（照审计 `FREEZE.md` §7/§8 落，别再重新踩）：
#   · `-o/--output` 是 **OutputDirectory**（文件名由输入名派生，`IpcService.cs BuildOutputPath`）；
#     把文件路径喂给 `-o` ⇒ 应用建出同名目录，"按文件读"就 PermissionError —— 那是自伤不是产品行为。
#   · **Pillow（本轮 12.3.0）读 16-bit RGB PNG 会静默截成 8-bit**（实机：16-bit 夹具/交付被报成
#     `mode=RGB dtype=uint8`，65283 与 65535 在它眼里都是 255 ⇒ 正好把本契约要抓的错全洗掉）。
#     ⇒ 16-bit 判据绝不许用 Pillow；本脚本自带符合 PNG §6 的**逐字节**行滤波读手（审计那份 `png16.py`
#     把 16-bit 样本当整数做滤波加减，低字节进位跨字节 ⇒ 读 libpng 真产物即错，而它用自己的写手自证
#     15/15 所以没暴露 —— 本脚本不复制该缺陷，并加一道"自研读手 vs 随包 ffmpeg 原生解码逐样本互校"）。
#     Pillow 只当 8-bit 的第二只眼，且显式拒绝 16-bit。
#   · `rgb24→rgb48le` 顶码 65283 而非 65535；`/65535*255` 与 `/257` 等价（那条**不是**缺陷，别顺手"修"）。
#   · 子进程一律 `Start-Process -Wait -NoNewWindow -PassThru -RedirectStandard*`（`& exe` 在部分宿主静默
#     空输出 ⇒ 假红）；python 侧统一 `PYTHONIOENCODING=utf-8`，判据 token 全 ASCII，免码页把互校洗成假绿。
#   · 本机某些派发宿主会拒产物区写盘 ⇒ 条目 `失败 (退出码 -13/-102)`、`解码失败 (exit -13)`：那是**环境**
#     不是色彩 ⇒ 单列 `ENV-FAIL`（打印退出码与 exe/in/outdir 路径），**不进色彩判词**，也不给假绿。
#   · TMP/TEMP/TMPDIR 在派发任何子进程**之前**钉进本轮 GUID 工作目录（项目 §3：谁都不许往系统临时根写；
#     夹具与产物一律住 `tests/output/`，不提交二进制夹具）。
#
# 用法（5.1 与 7 都要能跑：无三元、无 `??`、无 `?.`、无 `&&`/`||`）：
#   pwsh       -NoProfile -ExecutionPolicy Bypass -File tests/scripts/_probe-white-point-kernel.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/scripts/_probe-white-point-kernel.ps1
# 退出码：FAIL>0 或 ENV>0 ⇒ 1。**HEAD 上预期就是 1（A 组两臂红 = S8 在场）。**
# ⚠ 已登记为 `_run-step3-gates.ps1` 的**第 87 条**（2026-10-31，清单/常量/散文三处同批）。
#   本脚本不是 ServiceProbe mode ⇒ 不进 `Tests.Shared/TestGroups.cs`（组表只管 mode 派发）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$env:LC_ALL = $null
$env:PYTHONIOENCODING = "utf-8"
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ff = "$env:FFMPEGGUI_FFMPEG_DIR/ffmpeg.exe"
$fp = "$env:FFMPEGGUI_FFMPEG_DIR/ffprobe.exe"

# 一轮一目录（防上一轮残留把空跑洗成假绿）；TMP 先钉再派发。
$work = "$root/tests/output/whitepoint_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:TMP = $work; $env:TEMP = $work; $env:TMPDIR = $work
Write-Output ("[gate] work=" + $work)

$script:pass = 0; $script:fail = 0; $script:skip = 0; $script:envf = 0
$script:readerOk = $true
$script:xcN = 0; $script:xcBad = 0
function CK([bool]$ok, [string]$what) {
  if ($ok) { $script:pass++; Write-Host "PASS $what" -ForegroundColor Green }
  else { $script:fail++; Write-Host "FAIL $what" -ForegroundColor Red }
}
function SKIP([string]$what) { $script:skip++; Write-Host "SKIP $what" -ForegroundColor Yellow }
function ENVFAIL([string]$what, [string]$why) {
  $script:envf++; Write-Host ("ENV-FAIL $what  ==> $why") -ForegroundColor DarkRed
}
function Exec([string]$file, [string]$argStr, [string]$tag, [bool]$outOnly) {
  # 工具缺位不 throw（throw 会炸掉整脚本连汇总行都不打）：返回哨兵，让上层照常记红。
  # ⚠ `py` 是裸命令名（PATH 解析），Test-Path 恒 false ⇒ 必须两者其一即算在位。
  $present = (Test-Path -LiteralPath $file) -or ($null -ne (Get-Command $file -ErrorAction SilentlyContinue))
  if (-not $present) { return @{ code = -1; text = ""; missing = $true } }
  $o = "$work/x_$tag.out"; $e = "$work/x_$tag.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e
  $t = [System.IO.File]::ReadAllText($o)
  if (-not $outOnly) { $t = $t + "`n" + [System.IO.File]::ReadAllText($e) }
  return @{ code = $p.ExitCode; text = $t; missing = $false }
}
# 判据 token 一律从 python 的 `TOKENS ...` 行取（纯 ASCII，键前必须是行首或空格 ⇒ 不会误吃 reader_max）。
function Tok([string]$text, [string]$key) {
  $line = ($text -split "`r?`n" | Where-Object { $_ -match '^TOKENS ' } | Select-Object -First 1)
  if ($null -eq $line) { return "" }
  if ($line -match ('(?:^|\s)' + [regex]::Escape($key) + '=(\S+)')) { return $Matches[1] }
  return ""
}
function Num([string]$s) { if ($s -match '^-?[0-9]+$') { return [int]$s } return -999999 }

foreach ($need in @($exe, $ff, $fp)) {
  if (-not (Test-Path $need)) { Write-Output ("缺工具 " + $need) }
}
if (-not (Test-Path $exe)) { Write-Output "===== PASS=0 FAIL=1 SKIP=0 ENV=0 ====="; exit 1 }
if (-not (Test-Path $ff)) { Write-Output "===== PASS=0 FAIL=1 SKIP=0 ENV=0 ====="; exit 1 }
if ($null -eq (Get-Command py -ErrorAction SilentlyContinue)) {
  CK $false "读手在位：py（自研 PNG 读手 + Pillow 正控都跑在它上面，缺位则本锁无从成立）"
  Write-Output ("===== PASS=" + $script:pass + " FAIL=" + $script:fail + " SKIP=" + $script:skip + " ENV=" + $script:envf + " =====")
  exit 1
}

# ── 内嵌判据器：夹具生成 + 独立读手（纯标准库；不 import 产品任何东西）────────────────
$pySrc = @'
# -*- coding: utf-8 -*-
"""白点契约锁的夹具生成器与**独立** PNG 读手（纯 Python 标准库，不依赖产品、不依赖 numpy）。

为什么读手自己写：
  * Pillow 读 16-bit RGB PNG 会静默降成 8-bit（mode=RGB/dtype=uint8，只留高字节）
    => 65283 与 65535 在它眼里都是 255，正好把本契约要抓的错全洗掉。Pillow 在本锁里只准做
    8-bit 的第二只眼，见到 IHDR depth!=8 一律 refuse。
  * PNG 规范 §6 的行滤波是**逐字节**运算（left = 往前 bpp 个**字节**），不是逐样本；
    把 16-bit 样本当一个整数做 (cur-left) 会在低字节进位/借位时与真实解码器分叉。
    审计那份 png16.py 正栽在这里（写手读手同一套错 => 自证 15/15 通过却读不对 libpng 产物）。
  * 所以外层脚本对每个被测文件再跑一次「随包 ffmpeg 原生解码 vs 本读手」的逐样本互校；
    互校不过 = 仪表问题，本轮不出色彩判词。

子命令：mk | selftest | mech | measure | ident | xcheck | pillook
所有判据都打在 `TOKENS ...` 这一行（纯 ASCII）。
"""
import os
import struct
import sys
import zlib

CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}
# cICP 载体（4 字节：primaries / transfer / matrix / full-range）。12 = Display P3，13 = sRGB TRC，1 = BT.709。
CICP_P3 = bytes([12, 13, 0, 1])
CICP_SRGB = bytes([1, 13, 0, 1])
# 16-bit 夹具版面：行 0..7 = 白块 65535｜行 8..15 = 顶段逐码斜坡 65024..65535｜行 16..23 = 低段 0..1022
W16_W, W16_H = 512, 24
WHITE_ROWS = 8
BAND_LO = 65284          # 65284..65535 = 252 个码（/65283 或钳到 65283 的实现会把它们压成 1 个）


def _chunk(tag, data):
    return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)


def _enc_row(cur, prev, ft, bpp):
    if ft == 0:
        return cur
    out = bytearray(len(cur))
    for i in range(len(cur)):
        a = cur[i - bpp] if i >= bpp else 0
        b = prev[i] if prev is not None else 0
        c = prev[i - bpp] if (prev is not None and i >= bpp) else 0
        if ft == 1:
            x = cur[i] - a
        elif ft == 2:
            x = cur[i] - b
        elif ft == 3:
            x = cur[i] - ((a + b) >> 1)
        else:
            p = a + b - c
            pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
            pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
            x = cur[i] - pr
        out[i] = x & 0xFF
    return bytes(out)


def _dec_row(buf, prev, ft, bpp):
    if ft == 0:
        return bytes(buf)
    cur = bytearray(buf)
    for i in range(len(cur)):
        a = cur[i - bpp] if i >= bpp else 0
        b = prev[i] if prev is not None else 0
        c = prev[i - bpp] if (prev is not None and i >= bpp) else 0
        if ft == 1:
            x = cur[i] + a
        elif ft == 2:
            x = cur[i] + b
        elif ft == 3:
            x = cur[i] + ((a + b) >> 1)
        elif ft == 4:
            p = a + b - c
            pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
            pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
            x = cur[i] + pr
        else:
            raise ValueError('bad filter type %d' % ft)
        cur[i] = x & 0xFF          # 逐字节回绕，**不**把进位带进下一个字节（规范要的就是这个）
    return bytes(cur)


def write_png(path, w, h, depth, ctype, rows, ft=0, labels=()):
    ch = CHANNELS[ctype]
    bpp = max(1, (ch * depth) // 8)
    raw, prev = b'', None
    for y in range(h):
        s = rows[y]
        cur = bytes(bytearray(s)) if depth == 8 else b''.join(struct.pack('>H', v) for v in s)
        raw += bytes([ft]) + _enc_row(cur, prev, ft, bpp)
        prev = cur
    blob = b'\x89PNG\r\n\x1a\n' + _chunk(
        b'IHDR', struct.pack('>IIBBBBB', w, h, depth, ctype, 0, 0, 0))
    for tag, data in labels:
        blob += _chunk(tag.encode('latin1'), data)
    blob += _chunk(b'IDAT', zlib.compress(raw, 6)) + _chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(blob)
    return len(blob)


def read_png(path):
    with open(path, 'rb') as f:
        b = f.read()
    if b[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError('not a PNG')
    i, ihdr, idat, tags = 8, None, b'', []
    while i + 8 <= len(b):
        ln = struct.unpack('>I', b[i:i + 4])[0]
        tag = b[i + 4:i + 8]
        body = b[i + 8:i + 8 + ln]
        if tag == b'IHDR':
            ihdr = body
        elif tag == b'IDAT':
            idat += body
        tags.append(tag.decode('latin1'))
        i += 12 + ln
    w, h, depth, ctype, comp, filt, inter = struct.unpack('>IIBBBBB', ihdr)
    if inter:
        raise ValueError('interlaced PNG not supported by this reader')
    ch = CHANNELS[ctype]
    bpp = max(1, (ch * depth) // 8)
    raw = zlib.decompress(idat)
    stride = w * ch * (2 if depth == 16 else 1)
    rows, prev, pos = [], None, 0
    for _ in range(h):
        ft = raw[pos]; pos += 1
        cur = _dec_row(raw[pos:pos + stride], prev, ft, bpp); pos += stride
        prev = cur
        if depth == 16:
            rows.append([struct.unpack('>H', cur[x * 2:x * 2 + 2])[0] for x in range(w * ch)])
        else:
            rows.append([cur[x] for x in range(w * ch)])
    return {'w': w, 'h': h, 'depth': depth, 'ctype': ctype, 'ch': ch, 'rows': rows, 'tags': tags}


def flat(rows):
    out = []
    for r in rows:
        out.extend(r)
    return out


def ctl_rows(w, h, depth, ch):
    """确定性图案：含 0 与满幅端点，且低字节必然进位/借位 —— 专抓跨字节的滤波错误。"""
    mx = (1 << depth) - 1
    rows = []
    for y in range(h):
        r = []
        for x in range(w):
            for c in range(ch):
                v = (x * 131 + y * 17 + c * 71) % (mx + 1)
                if (x + c) % 7 == 0:
                    v = mx
                if (x * 3 + c) % 11 == 0:
                    v = 0
                r.append(v)
        rows.append(r)
    return rows


def w16_rows():
    rows = []
    for y in range(W16_H):
        if y < WHITE_ROWS:
            rows.append([65535] * (W16_W * 3))
        elif y < 16:
            rows.append([65024 + x for x in range(W16_W)] * 3)
        else:
            rows.append([x * 2 for x in range(W16_W)] * 3)
    return rows


def cmd_mk(d):
    w8 = [[255] * (64 * 3) for _ in range(64)]
    print('  mk w8_p3.png   %d B  (8-bit, cICP=12/13 => Display P3 标签)' % write_png(
        os.path.join(d, 'w8_p3.png'), 64, 64, 8, 2, w8, 0, [('cICP', CICP_P3)]))
    print('  mk w8_srgb.png %d B  (8-bit, cICP=1/13  => sRGB 标签)' % write_png(
        os.path.join(d, 'w8_srgb.png'), 64, 64, 8, 2, w8, 0, [('cICP', CICP_SRGB)]))
    print('  mk w16_p3.png  %d B  (真 16-bit：白块 65535 + 顶段 65024..65535 + 低段 0..1022)' % write_png(
        os.path.join(d, 'w16_p3.png'), W16_W, W16_H, 16, 2, w16_rows(), 0, [('cICP', CICP_P3)]))
    n = 0
    for depth in (8, 16):
        for ctype in (2, 6):
            ch = CHANNELS[ctype]
            for ft in range(5):
                write_png(os.path.join(d, 'ctl_d%d_c%d_f%d.png' % (depth, ch, ft)),
                          13, 9, depth, ctype, ctl_rows(13, 9, depth, ch), ft)
                n += 1
    print('  mk 读手控制件 %d 份（2 位深 x 2 通道数 x 5 种行滤波）' % n)
    print('TOKENS kind=mk files=%d' % n)
    return 0


def cmd_selftest(d):
    total = ok = 0
    for depth in (8, 16):
        for ctype in (2, 6):
            ch = CHANNELS[ctype]
            exp = ctl_rows(13, 9, depth, ch)
            for ft in range(5):
                total += 1
                p = os.path.join(d, 'ctl_d%d_c%d_f%d.png' % (depth, ch, ft))
                try:
                    same = read_png(p)['rows'] == exp
                except Exception as e:
                    print('   SELFTEST-ERR %s: %s: %s' % (os.path.basename(p), type(e).__name__, e))
                    same = False
                if same:
                    ok += 1
                else:
                    print('   SELFTEST-BAD depth=%d ch=%d filter=%d' % (depth, ch, ft))
    print('TOKENS kind=selftest pass=%d total=%d' % (ok, total))
    return 0 if ok == total else 1


def cmd_mech(raw):
    """读产品真实输入通道的原形产物（rgb48le = 小端 16-bit）：证明夹具穿过解码器时量程如何。"""
    with open(raw, 'rb') as f:
        b = f.read()
    v = [struct.unpack('<H', b[i:i + 2])[0] for i in range(0, len(b) - 1, 2)]
    print('TOKENS kind=mech max=%d nuniq=%d n=%d' % (max(v), len(set(v)), len(v)))
    return 0


def cmd_measure(png):
    m = read_png(png)
    vals = flat(m['rows'])
    u = sorted(set(vals))
    ch = m['ch']
    blk = [v for r in m['rows'][:WHITE_ROWS] for v in r]
    ramp = [v for r in m['rows'][8:16] for v in r] if m['h'] >= 16 else []
    low = [v for r in m['rows'][16:24] for v in r] if m['h'] >= 24 else []
    band = sorted(set([v for v in vals if v >= BAND_LO]))
    labels = '+'.join(t for t in m['tags'] if t not in ('IHDR', 'IDAT', 'IEND')) or 'none'
    print('  容器 %s: %dx%d depth=%d ctype=%d 标签=%s' % (
        os.path.basename(png), m['w'], m['h'], m['depth'], m['ctype'], labels))
    print('  白块(前 %d 行) min=%s max=%s 唯一=%s｜全图 min=%s max=%s 唯一=%s 低于满幅样本=%d' % (
        WHITE_ROWS, min(blk), max(blk), len(set(blk)), u[0], u[-1], len(u),
        len([v for v in vals if v != u[-1]])))
    if ramp:
        print('  顶段斜坡(行8-15) 唯一=%d min=%d max=%d｜低段(行16-23) 唯一=%d min=%d max=%d' % (
            len(set(ramp)), min(ramp), max(ramp), len(set(low)), min(low), max(low)))
    print('  全图 >=%d 的唯一码数=%d 顶12码=%s' % (BAND_LO, len(band), band[-12:]))
    print('TOKENS kind=measure file=%s depth=%d ctype=%d n=%d min=%d max=%d nuniq=%d '
          'blk_min=%d blk_max=%d blk_uniq=%d band_uniq=%d ramp_uniq=%d tags=%s' % (
              os.path.basename(png), m['depth'], m['ctype'], len(vals), u[0], u[-1], len(u),
              min(blk), max(blk), len(set(blk)), len(band), len(set(ramp)), labels))
    return 0


def cmd_ident(png, ref):
    a, b = read_png(png), read_png(ref)
    if (a['depth'], a['ctype'], a['w'], a['h']) != (b['depth'], b['ctype'], b['w'], b['h']):
        print('TOKENS kind=ident equal=0 reason=container n=0 ndiff=-1 maxdiff=-1')
        print('   容器不一致：交付 %s vs 夹具 %s' % (
            (a['depth'], a['ctype'], a['w'], a['h']), (b['depth'], b['ctype'], b['w'], b['h'])))
        return 1
    x, y = flat(a['rows']), flat(b['rows'])
    bad = [i for i in range(len(x)) if x[i] != y[i]]
    print('   逐样本对撞：n=%d 差异=%d 最大偏差=%d' % (
        len(x), len(bad), max([abs(x[i] - y[i]) for i in bad] or [0])))
    for i in bad[:5]:
        print('     首个差异 @样本%d 夹具=%d 交付=%d' % (i, y[i], x[i]))
    print('TOKENS kind=ident equal=%d n=%d ndiff=%d maxdiff=%d' % (
        1 if not bad else 0, len(x), len(bad), max([abs(x[i] - y[i]) for i in bad] or [0])))
    return 0 if not bad else 1


def cmd_xcheck(png, raw, pixfmt):
    """自研读手 vs 随包 ffmpeg 的**原生** raw 解码（不带 format 滤镜 ⇒ 不引入升位量程）。"""
    m = read_png(png)
    n = m['w'] * m['h'] * m['ch']
    with open(raw, 'rb') as f:
        b = f.read()
    if pixfmt in ('rgb24', 'rgba', 'gbba', 'gray8'):
        v = list(b[:n]) if len(b) >= n else []
    elif pixfmt in ('rgb48be', 'rgba64be', 'gbpa16be', 'gray16be'):
        v = [struct.unpack('>H', b[i * 2:i * 2 + 2])[0] for i in range(n)] if len(b) >= 2 * n else []
    else:
        print('TOKENS kind=xcheck agree=0 reason=pixfmt_unsupported n=0 ndiff=-1 maxdiff=-1 reader_max=-1 ffmpeg_max=-1')
        print('   不认的 pix_fmt=%s（互校要按原生字节序对齐，猜不得）' % pixfmt)
        return 1
    if len(v) != n:
        print('TOKENS kind=xcheck agree=0 reason=size_mismatch n=%d ndiff=-1 maxdiff=-1 reader_max=%d ffmpeg_bytes=%d' % (
            n, max(flat(m['rows'])), len(b)))
        return 1
    mine = flat(m['rows'])
    bad = [i for i in range(n) if mine[i] != v[i]]
    print('TOKENS kind=xcheck agree=%d n=%d ndiff=%d maxdiff=%d reader_max=%d ffmpeg_max=%d' % (
        1 if not bad else 0, n, len(bad),
        max([abs(mine[i] - v[i]) for i in bad] or [0]), max(mine), max(v)))
    for i in bad[:3]:
        print('     互校差异 @样本%d 自研=%d ffmpeg=%d' % (i, mine[i], v[i]))
    return 0 if not bad else 1


def cmd_pillook(png):
    """8-bit 交付的第二只眼。非 8-bit 一律拒绝（Pillow 会把 RGB16 静默截成 uint8）。"""
    import PIL
    from PIL import Image
    Image.MAX_IMAGE_PIXELS = None
    with open(png, 'rb') as f:
        head = f.read(25)
    bd = head[24]                      # IHDR 的 bit depth 在文件偏移 24（8 签名 + 4 长度 + 4 类型 + 4 宽 + 4 高）
    if bd != 8:
        print('TOKENS kind=pillook version=%s depth=%d refuse=1' % (PIL.__version__, bd))
        print('   Pillow %s 拒绝作 16-bit 判据：它把 RGB16 读成 uint8（只留高字节）' % PIL.__version__)
        return 0
    im = Image.open(png)
    vals = sorted(set([c for px in list(im.convert('RGB').getdata()) for c in px]))
    print('TOKENS kind=pillook version=%s mode=%s depth=%d min=%d max=%d nuniq=%d refuse=0' % (
        PIL.__version__, im.mode, bd, vals[0], vals[-1], len(vals)))
    return 0


if __name__ == '__main__':
    sys.exit({'mk': cmd_mk, 'selftest': cmd_selftest, 'mech': cmd_mech, 'measure': cmd_measure,
              'ident': cmd_ident, 'xcheck': cmd_xcheck, 'pillook': cmd_pillook}[sys.argv[1]](*sys.argv[2:]))
'@
[IO.File]::WriteAllText("$work\wp_probe.py", $pySrc)
Write-Output ("[gate] 判据器写入 " + "$work\wp_probe.py" + "（夹具与产物同处本轮 GUID 目录，不落 %TEMP%，不提交二进制夹具）")

# ── 0. 读手自证：先证明这把尺子活着，再谈像素 ──────────────────────────────────────
$r = Exec 'py' ('"' + "$work\wp_probe.py" + '" mk "' + $work + '"') 'mk' $false
Write-Output ("[fixtures] py rc=" + $r.code)
Write-Output $r.text
if ($r.code -ne 0) { $script:readerOk = $false; ENVFAIL '夹具生成' "wp_probe.py mk 退出码 $($r.code) ⇒ 本轮无夹具，后续判词作废" }
$r = Exec 'py' ('"' + "$work\wp_probe.py" + '" selftest "' + $work + '"') 'selftest' $false
Write-Output $r.text
$sp = Num (Tok $r.text 'pass'); $st = Num (Tok $r.text 'total')
if ($st -le 0 -or $sp -ne $st) { $script:readerOk = $false }
CK ($st -gt 0 -and $sp -eq $st) ("读手自证：自研 conformant PNG 读手对 5 种行滤波 x 2 位深 x 2 通道数 全数读回 " + $sp + "/" + $st)

# 互校：自研读手 vs 随包 ffmpeg 原生解码（不一致 ⇒ 尺子不可信 ⇒ 后面只报仪表，不报色彩）。
# ⚠ 值返回型函数一律**不往管道写**（Write-Output 会把返回值变成数组，把布尔与 hashtable 都污染掉）。
function XCheck([string]$file, [string]$what) {
  $line = ""
  $ok = $false
  $pf = Exec $fp ('-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 "' + $file + '"') ("pf$what") $true
  $pix = ($pf.text -split "`r?`n" | Where-Object { $_ -match '^[a-z0-9]+$' } | Select-Object -First 1)
  $script:xcN++
  if (-not $pix) {
    $line = "  [xcheck] $what ffprobe 未报出 pix_fmt（rc=" + $pf.code + "）⇒ 无从互校，记仪表红"
    $script:xcBad++
  } else {
    $raw = "$work\raw_$what.bin"
    $e = Exec $ff ('-y -hide_banner -loglevel error -i "' + $file + '" -frames:v 1 -f rawvideo "' + $raw + '"') ("raw$what") $false
    if ($e.code -ne 0 -or -not (Test-Path $raw)) {
      $line = "  [xcheck] $what ffmpeg 原生解码失败 rc=" + $e.code + "（" + $file + "）"
      $script:xcBad++
    } else {
      $t = Exec 'py' ('"' + "$work\wp_probe.py" + '" xcheck "' + $file + '" "' + $raw + '" ' + $pix) ("xc$what") $false
      $line = "  [xcheck] $what pix_fmt=" + $pix + " " + (Tok $t.text 'kind') +
              " agree=" + (Tok $t.text 'agree') + " n=" + (Tok $t.text 'n') +
              " ndiff=" + (Tok $t.text 'ndiff') + " reader_max=" + (Tok $t.text 'reader_max') +
              " ffmpeg_max=" + (Tok $t.text 'ffmpeg_max')
      if ((Tok $t.text 'agree') -ne '1') { $script:xcBad++; $line = $line + "`n" + $t.text }
      else { $ok = $true }
    }
  }
  return @{ ok = $ok; line = $line }
}

# ── 1. 夹具自证：臂的有效性先于像素判词（位深 + 标签 + 输入通道量程都要有读数）──────────
$fx8 = Exec 'py' ('"' + "$work\wp_probe.py" + '" measure "' + "$work\w8_p3.png" + '"') 'fx8' $false
Write-Output $fx8.text
$fx16 = Exec 'py' ('"' + "$work\wp_probe.py" + '" measure "' + "$work\w16_p3.png" + '"') 'fx16' $false
Write-Output $fx16.text
CK ((Tok $fx8.text 'depth') -eq '8') ("夹具容器自证：w8_p3.png 的 IHDR bit depth = 8（实读 " + (Tok $fx8.text 'depth') + "）")
CK ((Tok $fx16.text 'depth') -eq '16') ("夹具容器自证：w16_p3.png 的 IHDR bit depth = 16（实读 " + (Tok $fx16.text 'depth') + "；band_uniq=" + (Tok $fx16.text 'band_uniq') + " 证明顶段 252 码在夹具里真在场）")
CK ((Tok $fx16.text 'band_uniq') -eq '252') "夹具顶段自证：夹具自身 >=65284 的唯一码数必须恰为 252"
$x = XCheck "$work\w8_p3.png" 'fx8'; Write-Output $x.line
$x = XCheck "$work\w16_p3.png" 'fx16'; Write-Output $x.line
$x = XCheck "$work\ctl_d16_c3_f4.png" 'fxctl4'; Write-Output $x.line

# 输入通道量程（机制读数，**无产品参与**）：命令形态逐字取自产品自己的 debug 日志
#   ffmpeg -y -hide_banner -loglevel error -i <src> -frames:v 1 -vf "format=rgb48le" -f rawvideo <tmp>.rgb48
function Mech([string]$name) {
  $raw = "$work\mech_$name.bin"
  $e = Exec $ff ('-y -hide_banner -loglevel error -i "' + "$work\$name" + '" -frames:v 1 -vf "format=rgb48le" -f rawvideo "' + $raw + '"') ("mech$name") $false
  if ($e.code -ne 0 -or -not (Test-Path $raw)) {
    return @{ line = "  [mech] $name ffmpeg rc=" + $e.code + " ⇒ 机制读数取不到（仪表问题）"; max = "" }
  }
  $t = Exec 'py' ('"' + "$work\wp_probe.py" + '" mech "' + $raw + '"') ("mechr$name") $false
  return @{ line = "  [mech] " + $name + " 穿过 rgb48le -> kind=" + (Tok $t.text 'kind') +
                 " max=" + (Tok $t.text 'max') + " nuniq=" + (Tok $t.text 'nuniq') + " n=" + (Tok $t.text 'n');
              max = (Tok $t.text 'max') }
}
$s = Mech 'w8_p3.png'; Write-Output $s.line; $m8 = $s.max
$s = Mech 'w16_p3.png'; Write-Output $s.line; $m16 = $s.max
CK ($m16 -eq '65535') ("16-bit 臂有效性：真 16-bit 夹具的 65535 穿过输入通道不被压顶（实测 max=" + $m16 + "；若这里就掉到 65283，16-bit 臂读到的任何数都无从归因给内核）")
CK ($m8 -eq '65283') ("8-bit 臂前提（机制）：随包 ffmpeg 把 8-bit 白解到 rgb48le 的顶码就是 65283 —— S8 的根因（实测 max=" + $m8 + "；哪天变了本锁 A 组前提要重估，故这条也进判据）")

# ── 2. 产品臂 ────────────────────────────────────────────────────────────────────
# want = 'H1'（内核必须介入）| 'none'（直通对照，内核必须**不**介入）
# 返回值：$null（本臂无效，已按 ENV-FAIL 记过）| @{ art; dec; engaged; want; valid; line }
#   valid=否 ⇒ 调用方**只记一条"无效控制"FAIL，像素契约行一律不记 PASS**（防"没进内核却拿 255 当证据"）。
# ⚠ 函数名避开内建别名：`Measure` 是 Measure-Object 的别名，别名优先级**高于**函数 ⇒ 这里叫 Measure-Art。
function Measure-Art([string]$art, [string]$tag) {
  $t = Exec 'py' ('"' + "$work\wp_probe.py" + '" measure "' + $art + '"') ("ms$tag") $false
  return @{ text = $t.text; art = $art }
}
function Run-Arm([string]$tag, [string]$fixture, [string]$extra, [string]$want) {
  $o = "$work\out_$tag"
  if (Test-Path $o) { Remove-Item -LiteralPath $o -Recurse -Force -ErrorAction SilentlyContinue }
  New-Item -ItemType Directory -Force -Path $o | Out-Null     # -o 收的是**目录**（FREEZE §7）
  $argLine = '--headless --log-level debug --log-format text --overwrite -i "{0}" -o "{1}" -f png {2}' -f "$work\$fixture", $o, $extra
  $p = Start-Process -FilePath $exe -ArgumentList $argLine -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$work\run_$tag.out" -RedirectStandardError "$work\run_$tag.err"
  $log = [System.IO.File]::ReadAllText("$work\run_$tag.out") + [System.IO.File]::ReadAllText("$work\run_$tag.err")
  $files = @(Get-ChildItem -LiteralPath $o -Recurse -File -Filter *.png -ErrorAction SilentlyContinue)
  # 介入判据用整段日志里**任何**一行的 H1（决策行 `[色彩引擎] 决策：Map … 后端=H1(进程内融合内核)`），
  # 而不是"第一条像样子的行"—— 日志里 `None` 字样出现的位置不止一处，取首行会误判。
  $eng = ($log -match 'H1')
  $dec = ($log -split "`r?`n" | Where-Object { $_ -match 'H1' } | Select-Object -First 1)
  if (-not $dec) { $dec = ($log -split "`r?`n" | Where-Object { $_ -match 'None' } | Select-Object -First 1) }
  if (-not $dec) { $dec = '<日志里没有决策行>' }
  $line = "  [arm " + $tag + "] rc=" + $p.ExitCode + " 产物=" + $files.Count + " 内核介入=" + $(if ($eng) { '是' } else { '否' }) + " 决策行: " + $dec

  # 环境判别**先于**内容判别：-13/-102 与 Permission denied 属派发/沙箱面，不得变成色彩判词。
  if ($p.ExitCode -eq -13 -or $p.ExitCode -eq -102 -or $log -match 'Permission denied' -or $log -match 'EACCES' -or $log -match 'exit -13') {
    ENVFAIL "臂 $tag（环境，非色彩）" ("产品退出码 " + $p.ExitCode + " | exe=" + $exe + " | in=" + "$work\$fixture" + " | outdir=" + $o + " | 日志: " + (($log -split "`r?`n" | Where-Object { $_ -match '-13|denied|EACCES' } | Select-Object -First 2) -join ' ; '))
    return $null
  }
  if ($files.Count -ne 1) {
    ENVFAIL "臂 $tag（仪表）" ("期望 1 份 PNG 产物，实得 " + $files.Count + " 个于 $o（rc=" + $p.ExitCode + "）⇒ 出口不通，无像素可读")
    return $null
  }
  if (-not $script:readerOk) { ENVFAIL "臂 $tag（仪表）" '读手自证未通过 ⇒ 本轮不出色彩判词'; return $null }
  $art = $files[0].FullName
  $x = XCheck $art $tag
  $line = $line + "`n" + $x.line
  if (-not $x.ok) { ENVFAIL "臂 $tag（仪表）" "自研读手与 ffmpeg 在 $art 上互校不一致 ⇒ 尺子不可信，判词作废"; return $null }
  if ($want -eq 'H1' -and -not $eng) {
    # 无效控制**不许**在契约行上记 PASS：未进内核时 255/65535 都毫无意义（PW_auto 型陷阱）。
    # 判红由调用方那一条 CK 承担（避免同一件事双计）。
    $line = $line + "`n  [guard] 本臂要 H1 而未得到 ⇒ 调用方按『无效控制』记红，像素契约行不记 PASS"
  }
  return @{ art = $art; dec = $dec; engaged = $eng; want = $want; valid = ($want -ne 'H1' -or [bool]$eng); line = $line }
}

Write-Output '=== A. 内核介入 x 8-bit 出口：白必须交付满幅 255（S8 正脸；HEAD 上应红）==='
$a1 = Run-Arm 'K8_p3_to_srgb' 'w8_p3.png' '--color-engine engine --color-space "sRGB"' 'H1'
if ($a1) {
  Write-Output $a1.line
  $t = Measure-Art $a1.art 'a1'; Write-Output $t.text
  if ($a1.engaged) { CK $true "内核确已介入（8-bit P3 标签源 -> sRGB 目标，日志含 H1）" }
  else { CK $false ("内核介入自证：臂 K8_p3_to_srgb 未走 H1（决策行=" + $a1.dec + "）⇒ **无效控制**：没进内核时读到的数什么也不证明，本臂像素契约行不记 PASS") }
  if ($a1.valid) {
    CK ((Tok $t.text 'max') -eq '255') ("白顶码契约 @8-bit（P3->sRGB，H1）：交付每通道必须恰为 255；实测 max=" + (Tok $t.text 'max') +
        " blk_max=" + (Tok $t.text 'blk_max') + " —— 少 1 LSB 即 S8 本体（8-bit 白被 ffmpeg 的 65283 顶码喂进 /65535 归一）")
  }
}
$a2 = Run-Arm 'K8_srgb_to_p3' 'w8_srgb.png' '--color-engine engine --color-space "Display P3"' 'H1'
if ($a2) {
  Write-Output $a2.line
  $t = Measure-Art $a2.art 'a2'; Write-Output $t.text
  if ($a2.engaged) { CK $true "内核确已介入（8-bit sRGB 标签源 -> Display P3 目标）" }
  else { CK $false ("内核介入自证：臂 K8_srgb_to_p3 未走 H1（决策行=" + $a2.dec + "）⇒ 无效控制，契约行不记 PASS") }
  if ($a2.valid) {
    CK ((Tok $t.text 'max') -eq '255') ("白顶码契约 @8-bit（另一方向同一把尺）：交付必须恰为 255；实测 max=" + (Tok $t.text 'max'))
  }
  $pl = Exec 'py' ('"' + "$work\wp_probe.py" + '" pillook "' + $a2.art + '"') 'pil1' $false
  Write-Output ("  [第二只眼/Pillow] version=" + (Tok $pl.text 'version') + " mode=" + (Tok $pl.text 'mode') +
      " depth=" + (Tok $pl.text 'depth') + " max=" + (Tok $pl.text 'max') + " refuse=" + (Tok $pl.text 'refuse') +
      "  （Pillow 仅用于 8-bit 佐证，与自研读手同为 max=" + (Tok $t.text 'max') + " 口径）")
  $pl2 = Exec 'py' ('"' + "$work\wp_probe.py" + '" pillook "' + "$work\w16_p3.png" + '"') 'pil2' $false
  Write-Output ("  [第二只眼/Pillow] version=" + (Tok $pl2.text 'version') + " 对 16-bit 夹具 refuse=" + (Tok $pl2.text 'refuse') +
      " depth=" + (Tok $pl2.text 'depth') + " ⇒ 证明 16-bit 判据不能用 Pillow（它只留高字节，65283 与 65535 都成 255）")
}

Write-Output '=== B. 内核介入 x 真 16-bit 源：满幅 65535 与顶段 252 码不许塌缩（拦 /65283 那族"修复"）==='
$a3 = Run-Arm 'K16_to_png16' 'w16_p3.png' '--color-engine engine --color-space "sRGB" --bit-depth 16' 'H1'
if ($a3) {
  Write-Output $a3.line
  $t = Measure-Art $a3.art 'a3'; Write-Output $t.text
  if ($a3.engaged) { CK $true "内核确已介入（真 16-bit 源 -> PNG16）" }
  else { CK $false ("内核介入自证：臂 K16_to_png16 未走 H1（决策行=" + $a3.dec + "）⇒ 无效控制，契约行不记 PASS") }
  if ($a3.valid) {
    CK ((Tok $t.text 'depth') -eq '16') ("出口容器位深 = 16（--bit-depth 16 真落地）；实测 depth=" + (Tok $t.text 'depth'))
    CK ((Tok $t.text 'max') -eq '65535') ("白顶码契约 @16-bit：真 16-bit 源经 H1 交付的 16-bit 白必须恰为 65535；实测 max=" + (Tok $t.text 'max') + " blk_min=" + (Tok $t.text 'blk_min'))
    CK ((Tok $t.text 'band_uniq') -eq '252') ("顶段不塌缩 @16-bit：夹具的 65284..65535 共 252 码，交付后必须仍互异；实测 band_uniq=" +
        (Tok $t.text 'band_uniq') + " ramp_uniq=" + (Tok $t.text 'ramp_uniq') +
        " —— 凡除/钳到 65283 的实现会把这 252 码压成 1 个，全白夹具看不见，本行看得见")
  }
}
$a4 = Run-Arm 'K16_to_png8' 'w16_p3.png' '--color-engine engine --color-space "sRGB" --bit-depth 8' 'H1'
if ($a4) {
  Write-Output $a4.line
  $t = Measure-Art $a4.art 'a4'; Write-Output $t.text
  if ($a4.engaged) { CK $true "内核确已介入（真 16-bit 源 -> PNG8）" }
  else { CK $false ("内核介入自证：臂 K16_to_png8 未走 H1（决策行=" + $a4.dec + "）⇒ 无效控制，契约行不记 PASS") }
  if ($a4.valid) {
    CK ((Tok $t.text 'depth') -eq '8') ("出口容器位深 = 8（--bit-depth 8 真落地）；实测 depth=" + (Tok $t.text 'depth'))
    CK ((Tok $t.text 'blk_min') -eq '255' -and (Tok $t.text 'blk_max') -eq '255') ("白顶码契约 @16-bit源->8-bit交付：白块每通道必须全是 255；实测 blk_min=" +
        (Tok $t.text 'blk_min') + " blk_max=" + (Tok $t.text 'blk_max') + "（低段的有序抖动不在本契约范围，故只量白块）")
  }
}

# B3. 8-bit 源走 16-bit 交付：把「按源满幅归一」和「未修件」分开的那条读数。
#     8-bit 白 255 被 ffmpeg 扩成 65283；按源满幅 65283 归一 ⇒ 恰好 1.0 ⇒ 交付 65535。
#     未修件按 65535 归一 ⇒ 0.99615 进、0.99615 出，白停在 **65283**（对照臂实测，差 252 码头；
#     见 tests/output/_tmp/control_unfixed.log 的 arm K8_to_png16 那行 TOKENS）。
#     A 组在 8-bit 出口只差 1 LSB，量化后连对比度都不明显 —— 所以必须补这一臂。
#     ⚠ 本臂**不许**套 band_uniq：8-bit 夹具全白，交付后 65283（或任何 < 65284 的值）都低于 BAND_LO
#     ⇒ band 集合为空 ⇒ band_uniq 必 0，套上去等于凭空造一条红。
Write-Output '=== B3. 内核介入 x 8-bit 源走 16-bit 交付：按源满幅 65283 归一 ⇒ 交付必须顶到 65535（未修件停在 65283）==='
$a5 = Run-Arm 'K8_to_png16' 'w8_p3.png' '--color-engine engine --color-space "sRGB" --bit-depth 16' 'H1'
if ($a5) {
  Write-Output $a5.line
  $t = Measure-Art $a5.art 'a5'; Write-Output $t.text
  if ($a5.engaged) { CK $true "内核确已介入（8-bit P3 标签源 -> PNG16 交付）" }
  else { CK $false ("内核介入自证：臂 K8_to_png16 未走 H1（决策行=" + $a5.dec + "）⇒ 无效控制，契约行不记 PASS") }
  if ($a5.valid) {
    CK ((Tok $t.text 'depth') -eq '16') ("出口容器位深 = 16（8-bit 源 + --bit-depth 16 真落地）；实测 depth=" + (Tok $t.text 'depth'))
    CK ((Tok $t.text 'max') -eq '65535') ("源满幅归一 @8-bit源->16-bit交付：8-bit 白 255 扩成的 65283 必须被当成 1.0 交付成 65535；实测 max=" +
        (Tok $t.text 'max') + " blk_min=" + (Tok $t.text 'blk_min') +
        " —— 停在 65283 = 未修件把 65283 按 65535 归一后原样吐回（对照臂实测，非 65535），本条就是修复件与未修件的分界")
    CK ((Tok $t.text 'blk_min') -eq '65535' -and (Tok $t.text 'blk_max') -eq '65535') ("白块整块顶到 65535（不是只有个别样本碰顶）；实测 blk_min=" +
        (Tok $t.text 'blk_min') + " blk_max=" + (Tok $t.text 'blk_max'))
  }
}

Write-Output '=== C. 正控：无目标色彩空间 => 决策：None（未进内核）。这三条必须绿，红了是仪表不是产品 ==='
$c1 = Run-Arm 'CTRL8_passthrough' 'w8_p3.png' '' 'none'
if ($c1) {
  Write-Output $c1.line
  $t = Measure-Art $c1.art 'c1'; Write-Output $t.text
  if ($c1.engaged) { CK $false "直通对照失效（8-bit）：加了个空目标却出现 H1 ⇒ 对照组被污染，本锁的『无效控制』判据失去基准" }
  else {
    CK $true "直通对照成立（8-bit）：日志无 H1 —— 这条就是『未进内核 = 无效控制』的反面教材，故只作仪器正控"
    CK ((Tok $t.text 'max') -eq '255') ("直通出口的 8-bit 白必须原样 255（证明夹具->出口->读手整条链路本身无害）；实测 max=" + (Tok $t.text 'max'))
  }
}
$c2 = Run-Arm 'CTRL16_passthrough' 'w16_p3.png' '' 'none'
if ($c2) {
  Write-Output $c2.line
  $t = Measure-Art $c2.art 'c2'; Write-Output $t.text
  if ($c2.engaged) { CK $false "直通对照失效（16-bit）：日志出现 H1 ⇒ 对照组被污染" }
  else {
    CK $true "直通对照成立（16-bit）：日志无 H1"
    CK ((Tok $t.text 'band_uniq') -eq '252') ("直通出口的顶段 252 码不损；实测 band_uniq=" + (Tok $t.text 'band_uniq') + " max=" + (Tok $t.text 'max'))
    $id = Exec 'py' ('"' + "$work\wp_probe.py" + '" ident "' + $c2.art + '" "' + "$work\w16_p3.png" + '"') 'ident2' $false
    Write-Output $id.text
    CK ((Tok $id.text 'equal') -eq '1') "直通出口的 16-bit 交付与夹具逐样本全等（自研读手两侧同尺）"
  }
}

# ── 3. 汇总 ──────────────────────────────────────────────────────────────────────
if ($script:xcBad -eq 0 -and $script:xcN -gt 0) {
  CK $true ("读手互校：自研 conformant 读手 vs 随包 ffmpeg 原生解码，" + $script:xcN + " 个文件逐样本全等")
} else {
  CK $false ("读手互校：共 " + $script:xcN + " 个文件，不一致 " + $script:xcBad + " 个 ⇒ 尺子不可信，本轮像素判词作废")
}
Write-Output "[note] 未修件（S8 在场，如 beta10 的 f89b247b60dcf1ef）上：A 组两臂红(254)、B3 红(65283)、B 组与 C 组绿 —— 实测见 tests/output/_tmp/control_unfixed.log。"
Write-Output "[note] 若修复退化成 /65283 一刀切或钳位，B 组 band_uniq=252 立刻红（第二颗牙）；退化回 /65535，A 组与 B3 一起红。"
Write-Output ("[note] 产物留在 " + $work + "（tests/output 整树 gitignore，受 _sweep-output.py --runs 管辖）")
Write-Output ("===== PASS=" + $script:pass + " FAIL=" + $script:fail + " SKIP=" + $script:skip + " ENV=" + $script:envf + " =====")
if ($script:fail -gt 0 -or $script:envf -gt 0) { exit 1 }
exit 0
