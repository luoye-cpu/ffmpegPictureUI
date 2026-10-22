#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_probe-badpix-phase.py —— Phase One SensorDefects 能否搬进标准 opcode 的**前置裁决**

要判的问题（账本 §25 badpix 行的卡点②）：`dng_opcode_FixBadPixelsList` 载荷里的 `BayerPhase`
取 0..3 中哪些值，读者才会用**同色邻居**修复；取错会不会把 R 修进 B。
SDK 只用一个奇偶式（`dng_bad_pixels.h:279-282`：`IsGreen = ((r+c+phase+(phase>>1)) & 1)==0`）
⇒ 四个相位实际只有两档（{0,3} 与 {1,2}）。规范正文那侧本机没有 PDF 可引，所以由夹具裁决。

夹具比现实更苛刻（不依赖"某帧的坏点恰好可见"）：
  · 合成 64×64 的 GRBG / RGGB **非压缩** DNG，三种颜色给三个分离电平（G=1000 R=3000 B=500）
    ⇒ 任何跨色混合都会被量出来；
  · 在两类奇偶位置都植入离群值 15000，写成 OpcodeList1(51008) 的 FixBadPixelsList（ID=5）；
  · 同一份像素对 phase=0..3 各出一份件，用 Adobe `dng_validate -2` 渲染 Stage 2（本机实测：`-1` 的 Stage-1 写出**不反映** OpcodeList1，
    base 与 p0/p1 三份 md5 完全相同；`-2` 起三份分开且仍是单平面 ⇒ 观测点取 `-2`，比 `-3` 少一层去马赛克平滑）；
  · 尺子**自标定**：色间距 = 三种颜色（或 RGB 三通道）电平中位数之间的最小差；
    偏差 = 该植入点相对**同色邻域**（CFA 域取行列都偶偏移的邻点；RGB 域取整窗）中位数的最大差
    ⇒ 不假设 SDK 会不会重新缩放渲染件，也不把绝对阈值拍成常数。

用法：py _probe-badpix-phase.py mk     <dng_validate.exe> <outdir>   # 判相位档（{0,3} vs {1,2}）
      py _probe-badpix-phase.py origin <dng_validate.exe> <outdir>   # 判坐标基（边界 4/2，和为偶数）
退出码 0 = 分得出；1 = 分不出（两档都不像、或都像 ⇒ 记未判定，不据此落地）。
"""
import os
import statistics
import struct
import subprocess
import sys

W = H = 64
WHITE = 16383
OUTLIER = 15000
LEVEL = {'G': 1000, 'R': 3000, 'B': 500}
COLORVAL = {'R': 0, 'G': 1, 'B': 2}        # 规范 CFAPattern 取值
PATTERNS = {'GRBG': (('G', 'R'), ('B', 'G')),
            'RGGB': (('R', 'G'), ('G', 'B'))}
PLANTED = [(10, 10), (10, 11), (11, 10), (11, 11),
           (20, 21), (21, 20), (30, 31), (31, 30), (40, 41), (41, 40)]
BADPIX_ID = 5
TAG_OPC1 = 51008
MINVER_1_3 = 0x01030000
TSZ = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 8, 10: 8, 11: 4, 12: 8}


def pix(pat, r, c):
    return pat[r & 1][c & 1]


def plane(pat):
    a = [[0] * W for _ in range(H)]
    for r in range(H):
        for c in range(W):
            a[r][c] = LEVEL[pix(pat, r, c)] + ((r + c) % 7)
    for (r, c) in PLANTED:
        a[r][c] = OUTLIER
    return a


def blob_opc1(points, phase, flags=0):
    body = b''.join(struct.pack('>ii', r, c) for (r, c) in points)
    params = struct.pack('>IIII', 12 + 8 * len(points), phase, len(points), 0) + body
    return struct.pack('>I', 1) + struct.pack('>III', BADPIX_ID, MINVER_1_3, flags) + params


def ident_rationals():
    v = []
    for i in range(9):
        num, den = (1, 1) if i % 4 == 0 else (0, 1)
        v += [num, den]
    return struct.pack('<18i', *v)


def build_dng(path, patname, pts, phase, top=0, left=0):
    pat = PATTERNS[patname]
    px = b''.join(struct.pack('<%dH' % W, *row) for row in plane(pat))
    opc = blob_opc1(pts, phase) if phase is not None else b''
    inline = [
        (254, 4, 1, 0),                                 # NewSubFileType = sfMainImage
        (256, 4, 1, W), (257, 4, 1, H), (258, 3, 1, 16), (259, 3, 1, 1),
        (262, 3, 1, 32803), (273, 4, 1, 8), (277, 3, 1, 1), (278, 4, 1, H),
        (279, 4, 1, len(px)),
    ]
    # 短于 4 字节的定长值：直接按字节塞进值字段（TIFF 规定左对齐）
    def packed(vals, fm):
        raw = struct.pack(fm, *vals)
        return struct.unpack('<I', raw.ljust(4, b'\0'))[0]
    inline += [
        (274, 3, 1, 1),                         # Orientation
        (0xC616, 1, 3, packed((0, 1, 2), '<3B')),   # CFAPlaneColor = R,G,B
        (0x828D, 3, 2, packed((2, 2), '<HH')),        # CFARepeatPatternDim
        (0x828E, 1, 4, packed((COLORVAL[pat[0][0]], COLORVAL[pat[0][1]],
                               COLORVAL[pat[1][0]], COLORVAL[pat[1][1]]), '<4B')),
        (0xC612, 1, 4, packed((1, 3, 0, 0), '<4B')),  # DNGVersion 1.3.0.0
        (0xC613, 1, 4, packed((1, 1, 0, 0), '<4B')),  # DNGBackwardVersion
        (0xC619, 3, 2, packed((1, 1), '<HH')),        # BlackLevelRepeatDim
        (0xC61D, 3, 1, WHITE),                        # WhiteLevel
    ]
    extern = [
        (0xC61A, 5, 1, struct.pack('<ii', 0, 1)),                  # BlackLevel（SDK 只接 Short/Long/Rational=5）
        (0xC68D, 4, 4, struct.pack('<4I', top, left, H, W)),        # ActiveArea
        (0xC61F, 5, 2, struct.pack('<4i', 0, 0, 0, 0)),             # DefaultCropOrigin（相对 ActiveArea）
        (0xC620, 5, 2, struct.pack('<4i', W - left, 1, H - top, 1)),  # DefaultCropSize
        (0xC614, 2, 17, b'SYNTH BADPIX FIX\0'.ljust(17, b'\0')),   # UniqueCameraModel
        (0xC621, 10, 9, ident_rationals()),                         # ColorMatrix1 = I
    ]
    if phase is not None:
        extern.append((TAG_OPC1, 7, len(opc), opc))
    ents = [(t, ty, c, v) for (t, ty, c, v) in inline]
    ifd_off = 8 + len(px)
    n_all = len(ents) + len(extern)
    val_base = ifd_off + 2 + 12 * n_all + 4
    extra = b''
    for (t, ty, c, data) in extern:
        sz = TSZ[ty] * c
        if len(data) != sz:
            raise AssertionError('tag 0x%X 载荷 %d 与声明 %d 不符' % (t, len(data), sz))
        if sz <= 4:
            ents.append((t, ty, c, struct.unpack('<I', data.ljust(4, b'\0'))[0]))
        else:
            ents.append((t, ty, c, val_base + len(extra)))
            extra += data.ljust((sz + 1) & ~1, b'\0')
    ents.sort(key=lambda e: e[0])
    out = bytearray(struct.pack('<HHI', 0x4949, 42, ifd_off)) + px
    out += struct.pack('<H', len(ents))
    for (t, ty, c, v) in ents:
        out += struct.pack('<HHII', t, ty, c, v)
    out += struct.pack('<I', 0)
    assert len(out) == val_base, 'IFD 结尾 %d 与值区起点 %d 不符' % (len(out), val_base)
    out += extra
    open(path, 'wb').write(bytes(out))


def read_tif_planes(path, want_w, want_h):
    """返回 planes[ch][row][col]：支持 1 平面（CFA）与 3 平面（RGB）交错存储。"""
    b = open(path, 'rb').read()
    if b[:2] != b'II':
        raise AssertionError('渲染件不是小端 TIFF（头=%r）' % b[:4])
    off = struct.unpack_from('<I', b, 4)[0]
    n = struct.unpack_from('<H', b, off)[0]
    d = {}
    for i in range(n):
        tag, ty, cnt, val = struct.unpack_from('<HHII', b, off + 2 + 12 * i)
        d[tag] = (ty, cnt, val)

    def one(tag):
        ty, cnt, v = d[tag]
        sz = TSZ[ty] * cnt
        raw = struct.pack('<I', v)[:sz] if sz <= 4 else b[v:v + sz]
        return struct.unpack('<%d%s' % (cnt, {3: 'H', 4: 'I', 1: 'B', 2: 'B', 5: 'I', 7: 'B',
                                              9: 'i', 10: 'i'}[ty]), raw)
    w, h = one(256)[0], one(257)[0]
    if (w, h) != (want_w, want_h):
        raise AssertionError('渲染尺寸 %dx%d ≠ 期望 %dx%d ⇒ 裁切口径与本判读假设不符'
                             % (w, h, want_w, want_h))
    if one(259)[0] != 1:
        raise AssertionError('渲染 Compression=%d 非"无压缩" ⇒ 本读手不解' % one(259)[0])
    if one(258)[0] != 16:
        raise AssertionError('渲染 BitsPerSample=%d 非 16' % one(258)[0])
    planes = one(277)[0]
    if planes not in (1, 3):
        raise AssertionError('渲染 SamplesPerPixel=%d 既不是 CFA 也不是 RGB' % planes)
    if d.get(284, (0, 1, 0))[1] != 1 or (284 in d and one(284)[0] != 1):
        raise AssertionError('渲染 PlanarConfiguration≠1（交错）⇒ 本读手只接交错')
    rows = one(279)[0] // one(278)[0]
    off0 = one(273)[0]
    out = [[] for _ in range(planes)]
    for r in range(h):
        line = struct.unpack_from('<%dH' % (w * planes), b, off0 + r * rows)
        for c in range(planes):
            out[c].append(list(line[c::planes]) if planes > 1 else list(line))
    return out, planes


def colour_stats(planes, pat, planes_n, w, h, planted):
    """全图（扣除植入点）按 CFA 颜色或按 RGB 通道取中位数；返回 (字典, 尺子=最小间距)。"""
    acc = {}
    med = {}
    if planes_n == 1:
        for r in range(h):
            for c in range(w):
                if (r, c) in planted:
                    continue
                acc.setdefault(pix(pat, r, c), []).append(planes[0][r][c])
        med = {k: statistics.median(v) for k, v in acc.items()}
    else:
        for ch, nm in enumerate('RGB'):
            vals = [planes[ch][r][c] for r in range(h) for c in range(w)
                    if (r, c) not in planted]
            med[nm] = statistics.median(vals)
    sep = min(abs(med[a] - med[b]) for a in med for b in med if a < b)
    return med, sep


OBSERVE = '-2'      # Stage 2：坏点表已施加、仍是单平面（CFA 域）⇒ 不必经去马赛克的平滑
# 实测：-1 的 Stage-1 写出**不反映** OpcodeList1（base 与 p0/p1 三份 md5 完全相同、植入值原样留着），
#       -2 起三份分开，-3 是去马赛克后的 RGB。观测点选 -2。


def render(dv, src, stem):
    r = subprocess.run([dv, OBSERVE, stem, src], capture_output=True, text=True)
    tif = stem + '.tif'
    if r.returncode != 0 or not os.path.exists(tif):
        return None, (r.stdout + r.stderr)[-400:]
    return tif, ''


def phase_group(patname, origin_top, origin_left):
    """返回让 IsGreen 落在真绿点上的相位档：{0,3}=绿点在偶、{1,2}=绿点在奇。"""
    pat = PATTERNS[patname]
    green_even = pix(pat, origin_top, origin_left) == 'G'
    return ({0, 3} if green_even else {1, 2})


def local_dev(planes, planes_n, r, c, planted, mosaic, rad=4):
    """该点相对**同色邻域**的中位数偏差。

    ⚠ Stage 2 仍是 CFA 镶嵌 ⇒ 邻域必须只取同色点（行列都偶偏移）。第一版在此处对混合颜色取中位数，
      于是 R 点(×4≈12017)与"邻域中位数(≈G 4012)"天然差 4 个色距 ⇒ 四档相位全被判红，
      这条红是我的尺子错，不是产物错（实测见账本 §27）。RGB（Stage 3）每通道各自平场，取整个窗口。"""
    worst = 0.0
    step = 2 if mosaic else 1
    for ch in range(planes_n):
        rows = len(planes[ch])
        cols = len(planes[ch][0])
        vals = [planes[ch][rr][cc]
                for rr in range(r - rad * step, r + rad * step + 1, step)
                for cc in range(c - rad * step, c + rad * step + 1, step)
                if 0 <= rr < rows and 0 <= cc < cols
                and (rr, cc) != (r, c) and (rr, cc) not in planted]
        if len(vals) < 8:
            raise AssertionError('同色邻域样本只有 %d 个（坐标 %d,%d）⇒ 尺子没有落点' % (len(vals), r, c))
        worst = max(worst, abs(planes[ch][r][c] - statistics.median(vals)))
    return worst


def arm(dv, outdir, name, patname, pts, phase, top, left, label, defects=None):
    """渲染 Stage 3 并量"植入点是否回到本像素的局部邻域"。

    尺子不用全局通道中位数：带裁切时 CFA 解释会跟着原点一起挪，全局中位数会糊掉；
    改用**同位置局部 7×7 邻域中位数**（排除植入点）⇒ 与读者的缩放/电平分配无关。
    判读：正确件上每个植入点的偏差 << 色间距；错误件上植入点仍是离群值。"""
    src = os.path.join(outdir, 'synth_%s.dng' % name)
    build_dng(src, patname, pts, phase, top, left)
    stem = os.path.join(outdir, 'obs_%s' % name)
    tif, err = render(dv, src, stem)
    if tif is None:
        print('RENDER FAIL %s 尾部=%s' % (name, err))
        return None
    w, h = W - left, H - top
    planes, planes_n = read_tif_planes(tif, w, h)
    # ⚠ 测量位置用**植入时的存储坐标**，不是写进 opcode 的坐标：两者在"裁切基"臂里差一个边界，
    #    早先版本在这里对 crop 臂减了两次边界 ⇒ 量到干净像素 ⇒ 假"零残留"（本轮实测自纠）。
    defects = defects or PLANTED
    planted = {(r - top, c - left) for (r, c) in defects}
    _, sep = colour_stats(planes, PATTERNS[patname], planes_n, w, h, planted)
    if sep < 50:
        print('CALIB FAIL %s：色间距 %.1f 太小 ⇒ 判读无效' % (name, sep))
        return None
    worst = 0.0
    resid = 0
    for (rr, cc) in defects:
        d = local_dev(planes, planes_n, rr - top, cc - left, planted, planes_n == 1)
        worst = max(worst, d)
        if d > 0.7 * sep:
            resid += 1
    print('ARM %-24s %-30s 色间距=%.1f 植入点最大局部偏差=%.1f 比值=%.3f 残留=%d/%d'
          % (name, label, sep, worst, worst / sep, resid, len(pts)))
    return dict(name=name, label=label, ratio=worst / sep, resid=resid, n=len(pts), sep=sep)


def mode_phase(dv, outdir):
    rows = []
    for patname in ('GRBG', 'RGGB'):
        for phase in (None, 0, 1, 2, 3):
            tag = 'base' if phase is None else 'p%d' % phase
            a = arm(dv, outdir, '%s_%s' % (patname, tag), patname, PLANTED, phase, 0, 0,
                    'phase=%s' % tag)
            if a is None:
                return 1
            rows.append(a)
    print('BADPIX-PHASE 判读（比值<0.35 同色修复，>0.7 跨色污染；base 必须 >0.7）')
    ok = True
    for patname in ('GRBG', 'RGGB'):
        mine = [x for x in rows if x['name'].startswith(patname)]
        base = [x for x in mine if x['label'].endswith('base')]
        good = sorted(x['label'] for x in mine if not x['label'].endswith('base') and x['ratio'] < 0.35)
        bad = sorted(x['label'] for x in mine if not x['label'].endswith('base') and x['ratio'] > 0.7)
        if not base or base[0]['ratio'] < 0.7 or not good or not bad:
            ok = False
        print('  %s: base=%.3f 同色修复=%s 跨色污染=%s'
              % (patname, base[0]['ratio'] if base else -1, good, bad))
    print('BADPIX-PHASE %s' % ('OK' if ok else 'INCONCLUSIVE ⇒ 记未判定'))
    return 0 if ok else 1


def mode_origin(dv, outdir):
    """坐标基：坏点列表用的是"存储整幅"还是"ActiveArea 裁切后"的坐标。

    边界刻意取**偶数和**（top=4,left=2）⇒ 裁切不改奇偶，本模式只判坐标；
    相位的基由 SDK 源码定死：FixBadPixelsList 直接拿 badPoint.v/h 既当缓冲坐标又喂 IsGreen
    （`dng_bad_pixels.cpp:1053-1059`），两者同域 ⇒ 坐标基是什么，相位基就是什么。
    """
    top, left = 4, 2
    ok = True
    for patname in ('GRBG', 'RGGB'):
        rows = []
        base = arm(dv, outdir, '%s_m_base' % patname, patname, PLANTED, None, top, left, 'base')
        if base is None:
            return 1
        rows.append(base)
        if base['resid'] != len(PLANTED):
            print('  %s: 带边界的 base 残留 %d/%d ⇒ 反控不成立（应全部离群）'
                  % (patname, base['resid'], len(PLANTED)))
            ok = False
        for cb in ('abs', 'crop'):
            for ph in (0, 1, 2, 3):
                pts = PLANTED if cb == 'abs' else [(r - top, c - left) for (r, c) in PLANTED]
                a = arm(dv, outdir, '%s_m_%s_p%d' % (patname, cb, ph), patname, pts, ph, top, left,
                        'coord=%s phase=%d' % (cb, ph), defects=PLANTED)
                if a is None:
                    return 1
                rows.append(a)
        bycb = {}
        for x in rows:
            if not x['label'].startswith('coord'):
                continue
            cb = x['label'].split()[0].split('=')[1]
            bycb.setdefault(cb, []).append(x)
        clean = sorted(cb for cb, xs in bycb.items() if any(y['resid'] == 0 for y in xs))
        dirty = sorted(cb for cb, xs in bycb.items() if all(y['resid'] > 0 for y in xs))
        phaseok = sorted(x['label'] for x in rows
                         if x['label'].startswith('coord') and x['resid'] == 0)
        print('  %s: 能修掉全部植入点的坐标基=%s 反之=%s；活格子=%s'
              % (patname, clean, dirty, phaseok))
        if clean != ['abs'] or dirty != ['crop']:
            ok = False
    print('BADPIX-ORIGIN %s ⇒ 写侧按判出来的坐标基换算' % ('OK' if ok else 'INCONCLUSIVE ⇒ 记未判定'))
    return 0 if ok else 1


def main(argv):
    if len(argv) < 4 or argv[1] not in ('mk', 'origin'):
        print(__doc__)
        return 2
    dv, outdir = argv[2], argv[3]
    os.makedirs(outdir, exist_ok=True)
    if not os.path.exists(dv):
        print('缺 dng_validate：%s' % dv)
        return 2
    return mode_origin(dv, outdir) if argv[1] == 'origin' else mode_phase(dv, outdir)


if __name__ == '__main__':
    sys.exit(main(sys.argv))
