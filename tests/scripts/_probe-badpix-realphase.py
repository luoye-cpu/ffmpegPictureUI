#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_probe-badpix-realphase.py —— 真素材上证明"相位档位选对了"（第 77 条 ㉒d，重臂）

为什么不能只靠合成夹具：合成件证明的是"读者会按 phase 选同色/跨色邻居"这条机制；
真实件要证的是**这一张件的相位档选得对不对**——写错档会把 R 点填成 B 电平（§27 已量到 5 个色距），
而那批点在低感帧上本来就干净，所以"产物看起来没问题"完全不能当证据。

做法（同一条命令跑两臂，只有 4 个字节不同）：
  1) 从产物的 OpcodeList1(51008) 里定位 BayerPhase 那个 u32，按奇偶翻到**另一档**
     （{0,3} ↔ {1,2}；SDK 的 IsGreen 只用奇偶，账本 §27）；
  2) 两臂都 `dng_validate -2` 渲染（Stage 2：已施加坏点表、仍是单平面）；
  3) 三条判据：
     ① 两枚渲染必须不同，且不同的像素数 ≥ 列点落入有效区数的 50%（相位真的改了什么）；
     4) 列点处"与同色邻域中位数的偏差"：正确档 ≤ 错档的一半（同色修复 vs 跨色污染）；
     ③ **未列对照点**在两臂必须完全相同（证明影响范围只有这张表点到的那些像素，没有连带伤害）。
坐标映射：表里的坐标是**存储整幅**的绝对坐标（§27 实测），而 Stage 2 渲染件是有效区 ⇒
量之前要减 ActiveArea 的 (top,left)。这一步搞错会得到"两臂读数完全一样"的假象（本轮实测踩过）。

用法：py _probe-badpix-realphase.py <dng> <iiq> <dng_validate.exe> <outdir>
退出码 0 = 三条都成立；1 = 判红（点名哪条）；2 = 读不到。
"""
import hashlib
import os
import statistics
import struct
import subprocess
import sys

import importlib.util
_S = importlib.util.spec_from_file_location(
    'rd', os.path.join(os.path.dirname(os.path.abspath(__file__)), '_readback-dng-badpix.py'))
rd = importlib.util.module_from_spec(_S)
_S.loader.exec_module(rd)

TSZ = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 8, 10: 8, 11: 4, 12: 8}


def fail(msg):
    print('BADPIX-REAL FAIL %s' % msg)
    return 1


def ifd0(data, e):
    t = rd.Tiff(data, e)
    return t, t.ifd(t.u32(4))[0]


def one(t, d, tag):
    ty, cnt, val = d[tag]
    raw = t.raw((ty, cnt, val))
    fm = {3: 'H', 4: 'I', 1: 'B', 2: 'B', 7: 'B', 5: 'I', 9: 'i', 10: 'i'}[ty]
    return list(struct.unpack('%s%d%s' % (t.e, cnt, fm), raw))


def main(argv):
    if len(argv) < 5:
        print(__doc__)
        return 2
    dngP, iiqP, dv, outdir = argv[1], argv[2], argv[3], argv[4]
    if not (os.path.exists(dngP) and os.path.exists(iiqP) and os.path.exists(dv)):
        print('BADPIX-REAL SKIP 缺件（dng/iiq/dng_validate 之一不在位）⇒ 记未判，不记绿')
        return 2
    os.makedirs(outdir, exist_ok=True)
    data = bytearray(open(dngP, 'rb').read())
    e0 = '<' if data[:2] == b'II' else '>'
    t, d0 = ifd0(bytes(data), e0)
    ent = d0.get(rd.TAG_OPC1)
    if not ent or ent[1] <= 4:
        return fail('产物没有 OpcodeList1 ⇒ 本臂没有落点')
    val = ent[2]
    # 列表 = u32 条数 | 记录(ID,minVersion,flags,size,phase,pCount,rCount,...)
    head = struct.unpack_from('>7I', data, val)
    if head[0] != 1 or head[1] != rd.ID_FIXBAD:
        return fail('OpcodeList1 第一支不是 FixBadPixelsList（条数=%d ID=%d）' % (head[0], head[1]))
    phase_at = val + 4 + 12 + 4
    phase = struct.unpack_from('>I', data, phase_at)[0]
    flipped = phase ^ 1                       # {0,3} ↔ {1,2}
    w, h = one(t, d0, 256)[0], one(t, d0, 257)[0]
    def rational_or_int(tag, n):
        ty, cnt, val = d0[tag]
        raw = t.raw((ty, cnt, val))
        if ty in (5, 10):
            v = struct.unpack('%s%dI' % (t.e, 2 * cnt), raw)
            return [v[2 * i] // max(1, v[2 * i + 1]) for i in range(cnt)]
        return one(t, d0, tag)
    at, al, ab, ar = rational_or_int(rd.TAG_ACTIVE, 4)
    cl, ct = rational_or_int(0xC61F, 2)          # DefaultCropOrigin（相对 ActiveArea）
    cw, chh = rational_or_int(0xC620, 2)         # DefaultCropSize
    top, left = at + ct, al + cl                 # 渲染件 (0,0) 对应的存储坐标
    print('BADPIX-REAL 存储=%dx%d ActiveArea=(%d,%d)-(%d,%d) DefaultCropOrigin=(%d,%d) Size=(%d,%d)'
          ' ⇒ 渲染原点=存储(%d,%d)；phase=%d→翻转档=%d'
          % (w, h, at, al, ab, ar, cl, ct, cw, chh, top, left, phase, flipped))

    badP = os.path.join(outdir, 'realphase_flip.dng')
    mutated = bytearray(data)
    struct.pack_into('>I', mutated, phase_at, flipped)
    if len(mutated) != len(data):
        return fail('改长度了（不该）⇒ 本臂只做等长替换')
    open(badP, 'wb').write(bytes(mutated))

    outs = {}
    for tag, f in (('right', dngP), ('wrong', badP)):
        stem = os.path.join(outdir, 'rs2_%s' % tag)
        r = subprocess.run([dv, '-2', stem, f], capture_output=True, text=True)
        tif = stem + '.tif'
        if r.returncode != 0 or not os.path.exists(tif):
            return fail('%s 臂渲染失败 rc=%d 尾部=%s' % (tag, r.returncode, (r.stdout + r.stderr)[-300:]))
        outs[tag] = tif

    def plane(path):
        b = open(path, 'rb').read()
        tt, dd = ifd0(b, '<' if b[:2] == b'II' else '>')
        W, H = one(tt, dd, 256)[0], one(tt, dd, 257)[0]
        sp = one(tt, dd, 277)[0]
        if sp != 1:
            raise ValueError('Stage 2 渲染件 SamplesPerPixel=%d 非 1' % sp)
        row = one(tt, dd, 279)[0] // one(tt, dd, 278)[0]
        off0 = one(tt, dd, 273)[0]
        img = [list(struct.unpack_from('<%dH' % W, b, off0 + y * row)) for y in range(H)]
        return img, hashlib.md5(b).hexdigest()[:12]

    A, hA = plane(outs['right'])
    B, hB = plane(outs['wrong'])
    if (len(A), len(A[0])) != (chh, cw):
        return fail('Stage 2 尺寸 %dx%d ≠ 产物 DefaultCropSize %dx%d ⇒ 坐标映射前提失效'
                    % (len(A[0]), len(A), cw, chh))

    rec = rd.read_iiq(iiqP)
    listed = [(r - top, c - left) for (c, r, ty, _p) in rec
              if ty == 129 and top <= r < len(A) + top and left <= c < len(A[0]) + left]
    if len(listed) < 100:
        return fail('落入有效区的列点只有 %d 个 ⇒ 样本不够' % len(listed))

    def dev(img, r, c):
        vals = [img[rr][cc] for rr in (r - 4, r - 2, r + 2, r + 4)
                for cc in (c - 4, c - 2, c + 2, c + 4)
                if 0 <= rr < len(img) and 0 <= cc < len(img[0]) and (rr, cc) != (r, c)]
        return abs(img[r][c] - statistics.median(vals)) if vals else None

    # ① 两臂确有不同，且差异只发生在列点附近
    diff = [(y, x) for y in range(len(A)) for x in range(len(A[0])) if A[y][x] != B[y][x]]
    setl = set(listed)
    near = sum(1 for (y, x) in diff if any((y + dy, x + dx) in setl
                                           for dy in (-2, -1, 0, 1, 2) for dx in (-2, -1, 0, 1, 2)))
    # ③ 未列对照点：两臂必须逐点相同
    # 对照点：远离任何列点（>2 像素）的确定性命样
    nearset = set()
    for (r, c) in listed:
        for dy in range(-2, 3):
            for dx in range(-2, 3):
                nearset.add((r + dy, c + dx))
    ctrl = []
    for k in range(1, 6001):
        y = (k * 7919 + 13) % len(A)
        x = (k * 104729 + 7) % len(A[0])
        if (y, x) not in nearset:
            ctrl.append((y, x))
    ctrl_same = sum(1 for (y, x) in ctrl if A[y][x] != B[y][x])
    ds_r = sorted(d for d in (dev(A, r, c) for (r, c) in listed) if d is not None)
    ds_w = sorted(d for d in (dev(B, r, c) for (r, c) in listed) if d is not None)
    md_r, md_w = statistics.median(ds_r), statistics.median(ds_w)
    p95_r, p95_w = ds_r[int(.95 * len(ds_r))], ds_w[int(.95 * len(ds_w))]
    print('BADPIX-REAL 列点=%d 两臂不同像素=%d 其中列点附近=%d(占不同数 %.1f%%) 对照点=%d 其中两臂不同=%d'
          % (len(listed), len(diff), near, 100.0 * near / max(1, len(diff)), len(ctrl), ctrl_same))
    print('BADPIX-REAL 列点与同色邻域的偏差 中位: 正确档=%.1f 错档=%.1f（倍率 %.2f）p95: %.1f vs %.1f'
          % (md_r, md_w, (md_w / md_r if md_r > 0 else float('inf')), p95_r, p95_w))

    bad = []
    if len(diff) * 2 < len(listed):
        bad.append('两臂不同的像素只有 %d，不足列点 %d 的一半 ⇒ 相位没真的起作用' % (len(diff), len(listed)))
    if near * 2 < len(diff):
        bad.append('两臂差异有 %d/%d 落在列点 2 像素之外 ⇒ 影响范围超出这张表' % (len(diff) - near, len(diff)))
    if ctrl_same:
        bad.append('未列对照点里 %d/%d 两臂不同 ⇒ 连带伤害（不是只修表点）' % (ctrl_same, len(ctrl)))
    if not (md_w >= 2.0 * max(md_r, 1.0)):
        bad.append('错档中位偏差 %.1f 不足正确档 %.1f 的 2 倍 ⇒ 分不出档位' % (md_w, md_r))
    if bad:
        for m in bad:
            print('BADPIX-REAL FAIL ' + m)
        return 1
    print('BADPIX-REAL OK 相位档分叉成立、影响范围只在这张表点到的像素上')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
