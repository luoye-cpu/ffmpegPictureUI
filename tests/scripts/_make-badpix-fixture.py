#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_make-badpix-fixture.py —— 造"只改传感器缺陷表、不动像素"的 IIQ 夹具（第 77 条 ㉒e 用）

为什么需要它：真素材里没有任何一台声明**坏列**（本机三件 IIQ 只有 type=129 坏点 + P40 的 2 条
type=138），所以写侧的 rect 分支与"越界坐标必须丢"分支在真件上**永远走不到** ⇒ 没有判据覆盖的分支
等于没测过。本夹具把 Leaf Credo 40 的 0x0400 载荷里前几条记录原地改写成
  2 条 type=131/137 坏列（列号在幅面内）+ 1 条 type=131 坏列（列号**超出**幅面 ⇒ 必须被丢）
  + 1 条 type=129 坏点（列号 65535 ⇒ 必须被丢）+ 1 条 type=138（不认类型 ⇒ 必须点名丢弃）
其余记录保持原样；像素区一个字节都不动，文件总长不变。

自检（造完立刻自己读回来，读数不符就退非零 ⇒ 不给门禁留"夹具其实没改上"的空档）：
  type 直方图必须是 {129: N-4, 131: 2, 137: 1, 138: 1}（N = 源记录数；五处改写里越界坏点仍是 129），且字节长度与源相同。

用法：py _make-badpix-fixture.py <源 iiq> <输出 iiq>
退出码 0 = 夹具成立；1 = 不成立；2 = 用法/读不到。
"""
import os
import struct
import sys

import importlib.util
_S = importlib.util.spec_from_file_location(
    'rd', os.path.join(os.path.dirname(os.path.abspath(__file__)), '_readback-dng-badpix.py'))
rd = importlib.util.module_from_spec(_S)
_S.loader.exec_module(rd)


def defect_payload(b):
    """返回 (载荷绝对偏移, 字节长)；路径与读手同一条，但这里需要偏移以便改写。"""
    base = b[:32].find(b'IIII')
    if base < 0:
        base = b[:32].find(b'MMMM')
    if base < 0:
        raise ValueError('源头 32 字节里没有 IIII/MMMM')
    d = base + struct.unpack_from('<I', b, base + 8)[0]
    ent = struct.unpack_from('<I', b, d)[0]
    meta = None
    for i in range(ent):
        tag, ty, ln, dt = struct.unpack_from('<IIII', b, d + 8 + 16 * i)
        if tag == 0x0110:
            meta = dt
    if meta is None:
        raise ValueError('MOBJ 没有 tag 0x0110')
    mb = base + meta
    bd = mb + struct.unpack_from('<I', b, mb + 8)[0]
    ent2 = struct.unpack_from('<I', b, bd)[0]
    for i in range(ent2):
        tag, ln, dt = struct.unpack_from('<III', b, bd + 8 + 12 * i)
        if tag == 0x0400:
            return mb + dt, ln
    raise ValueError('元数据块里没有 tag 0x0400')


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    src, dst = argv[1], argv[2]
    if not os.path.exists(src):
        print('MAKETIX FAIL 源不在位: %s' % src)
        return 2
    b = bytearray(open(src, 'rb').read())
    at, ln = defect_payload(b)
    n = ln // 8
    if n < 16:
        print('MAKETIX FAIL 记录只有 %d 条，样本不够' % n)
        return 1
    orig = [struct.unpack_from('<HHHH', b, at + 8 * i) for i in range(n)]
    # 65535 是 u16 最大值：任何真实幅面都容不下 ⇒ 越界判据与素材幅面无关（写侧必须丢掉它）
    mut = list(orig)
    mut[0] = (1040, orig[0][1], 131, 0)                        # 幅面内坏列
    mut[1] = (4096, orig[1][1], 137, 0)                        # 幅面内坏列（另一种类型）
    mut[2] = (65535, orig[2][1], 131, 0)                       # 越界坏列 ⇒ 必须被丢
    mut[3] = (65535, orig[3][1], 129, 0)                       # 越界坏点 ⇒ 必须被丢
    mut[4] = (orig[4][0], orig[4][1], 138, 0)                  # 不认的类型 ⇒ 点名丢弃
    for i, (c, r, t, p) in enumerate(mut):
        struct.pack_into('<HHHH', b, at + 8 * i, c, r, t, p)
    open(dst, 'wb').write(bytes(b))

    if os.path.getsize(dst) != os.path.getsize(src):
        print('MAKETIX FAIL 改写后长度变了（像素区应当一字节未动）')
        return 1
    rec = rd.read_iiq(dst)
    from collections import Counter
    hist = dict(Counter(x[2] for x in rec))
    # 五处改写里第 4 处（越界坏点）仍是 type=129 ⇒ 129 只少 4 条
    want = {129: n - 4, 131: 2, 137: 1, 138: 1}
    ok = (hist == want and len(rec) == n)
    print('MAKETIX %s 源记录=%d 改写后类型直方图=%s 偏移=%d 长度=%d'
          % ('OK' if ok else 'FAIL', n, hist, at, ln))
    if not ok:
        return 1
    print('MAKETIX 原始前 5 条=%s 改写后=%s' % (orig[:5], [tuple(x) for x in rec[:5]]))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
