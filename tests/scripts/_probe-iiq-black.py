#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_probe-iiq-black.py —— 一次性取证：IIQ 的逐行/逐列黑电平（MOBJ 0x0223/0x0225）能不能
**精确**落进标准位 BlackLevelDeltaV(50716)/BlackLevelDeltaH(50715)

为什么先量这个：DNG 的 DeltaV 只有"**每行一个值**"、DeltaH 只有"每列一个值"，而 Phase One 的
0x0223/0x0225 每条存 2 个 int16，按 split_col/split_row 取左半或右半
（`phaseone_processing.cpp:73-79`：`val = src - bl + ph1_cblack[row][col>=split_col] + ph1_rblack[col][row>=split_row]`）。
⇒ 只有当**同一行的两半相等**（且同一列的两半相等）时才是"每行/每列一个值"，才谈得上无损搬运；
否则落 DeltaH/V 就是拟合，按政策要么显式放弃、要么另立开关并记残差。

用法：py _probe-iiq-black.py <iiq> [<iiq> ...]
退出码 0 = 读完并给出可判词；2 = 读不到。
"""
import importlib.util
import os
import struct
import sys

_S = importlib.util.spec_from_file_location(
    'rd', os.path.join(os.path.dirname(os.path.abspath(__file__)), '_readback-dng-badpix.py'))
rd = importlib.util.module_from_spec(_S)
_S.loader.exec_module(rd)


def tops(path):
    _b, e, base, _d = rd._iiq_dir(path)
    return _b, e, base, rd.read_iiq_top(path)


def arr(b, e, base, ent):
    """type=2、len=字节数、data 是**偏移**（LibRaw: `int(data + base)`）；每条 2 个 int16。"""
    ty, ln, dt = ent
    raw = b[base + dt: base + dt + ln]
    n = ln // 4
    return [list(struct.unpack_from(e + 'hh', raw, 4 * i)) for i in range(n)]


def report(path):
    b, e, base, top = tops(path)
    get = lambda t: top.get(t)
    tb = rd.iiq_black_level(path)
    sc, sr = get(0x0222), get(0x0224)
    split_col = sc[2] if sc else 0
    split_row = sr[2] if sr else 0
    print('== %s  小端=%s  0x021D t_black=%d  split_col=%d split_row=%d'
          % (os.path.basename(path), 'II' if e == '<' else 'MM', tb, split_col, split_row))
    bad = []
    for tag, name in ((0x0223, '0x0223 black_col（按**行**，每行 2 值，split_col 分左右）→ 标准位 BlackLevelDeltaV'),
                      (0x0225, '0x0225 black_row（按**列**，每列 2 值，split_row 分上下）→ 标准位 BlackLevelDeltaH')):
        ent = get(tag)
        if not ent:
            print('   %s 缺席 ⇒ 本件没有这一维的声明' % hex(tag))
            continue
        ty, ln, dt = ent
        a = arr(b, e, base, ent)
        if not a:
            print('   %s 空数组' % hex(tag))
            continue
        mins = min(min(x) for x in a)
        maxs = max(max(x) for x in a)
        neq = sum(1 for x in a if x[0] != x[1])
        dif = max(abs(x[0] - x[1]) for x in a)
        # 两半之差的绝对和（若为 0 ⇒ 每行/每列一个值，DeltaV/DeltaH 是**精确**载体）
        print('   type=%d len=%d 条数=%d 值域=[%d,%d] 两半不等的条数=%d 最大半差=%d'
              % (ty, ln, len(a), mins, maxs, neq, dif))
        print('      ⇒ %s' % (name if neq == 0 else
                              '%s ⇒ **不可精确表达**：同一行的两半不同值，DeltaV 只有每行一个位置' % name))
        if neq:
            bad.append((hex(tag), neq, dif))
    import rawpy
    with rawpy.imread(path) as im:
        sz = im.sizes
        print('   幅面 raw=%dx%d（split_col<raw_width 且 split_row<raw_height 才成立）' % (sz.raw_width, sz.raw_height))
        if not (0 < split_col < sz.raw_width and 0 < split_row < sz.raw_height):
            print('   ⚠ split 不在幅面内 ⇒ 写侧的取半判据在这件上不成立（记未判）')
    if bad:
        print('   VERDICT 不可精确搬运：%s' % bad)
    else:
        print('   VERDICT 两半全等 ⇒ 每行/每列一个值，DeltaH/DeltaV 可无损承载（还需素材侧再验读者兑现）')
    return 0


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    rc = 0
    for p in argv[1:]:
        try:
            report(p)
        except Exception as exc:
            print('IIQ-BLACK FAIL %s: %s' % (p, exc))
            rc = 2
    return rc


if __name__ == '__main__':
    sys.exit(main(sys.argv))
