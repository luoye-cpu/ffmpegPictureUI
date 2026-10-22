#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""_check-dng-original-digest.py —— 嵌入原件的完整性锁（第 77 条 ②e/②f 用）

规范口径（Adobe DNG Spec 1.7.1.0，Tag 50973，p.55）：
    "This tag is an MD5 digest of **the data stored in** the OriginalRawFileData tag."
⇒ 校验对象是**存储的那串字节**（含规范定义的压缩块序列），不是解回后的原件。

为什么值得单独一条：本机实测（2026-10-19）把 50828 载荷里的一个字节翻掉之后，
Adobe `dng_validate -v` 仍打印 "Validation complete"（rc=0）、自家 `dngtool -verify` 也 rc=0
⇒ **没有现成读者替我们查这条**，只能自己钉。

用法:
  py _check-dng-original-digest.py <dng>                     # 校验：相等 ⇒ 退出 0
  py _check-dng-original-digest.py <dng> --mutant-out <p> [--byte N]
        # 造变异件：把载荷第 N 个字节（默认 1000）异或成别的值写到 <p>，供 ②f 验牙
退出码 0 = 通过；1 = 判红（点名原因）；2 = 用法/结构错。
"""
import hashlib
import os
import struct
import sys

TAG_RAW_DATA = 50828
TAG_RAW_DIGEST = 50973


def ifd0(data):
    base = struct.unpack_from('<I', data, 4)[0]
    n = struct.unpack_from('<H', data, base)[0]
    out = {}
    for i in range(n):
        e = base + 2 + 12 * i
        tag, ty, cnt, val = struct.unpack_from('<HHII', data, e)
        out[tag] = (ty, cnt, val, e)
    return out


def main(argv):
    if len(argv) < 2:
        print('用法: _check-dng-original-digest.py <dng> [--mutant-out <p>] [--byte N]')
        return 2
    path = argv[1]
    mutant = None
    byte_at = 1000
    if '--mutant-out' in argv:
        mutant = argv[argv.index('--mutant-out') + 1]
    if '--byte' in argv:
        byte_at = int(argv[argv.index('--byte') + 1])
    if not os.path.exists(path):
        print('ORIG-DIGEST FAIL 文件不存在: %s' % path)
        return 1

    data = open(path, 'rb').read()
    try:
        d0 = ifd0(data)
    except (struct.error, IndexError) as exc:
        print('ORIG-DIGEST FAIL 解 IFD0 失败: %s' % exc)
        return 2
    if TAG_RAW_DATA not in d0:
        print('ORIG-DIGEST FAIL 没有 50828（OriginalRawFileData）⇒ 嵌入件无从校验')
        return 1
    if TAG_RAW_DIGEST not in d0:
        print('ORIG-DIGEST FAIL 没有 50973（OriginalRawFileDigest）⇒ 声明值缺席')
        return 1
    ty, cnt, val, e = d0[TAG_RAW_DATA]
    if ty not in (1, 7) or cnt < 16:
        print('ORIG-DIGEST FAIL 50828 形状不对 type=%d count=%d' % (ty, cnt))
        return 1
    if val + cnt > len(data):
        print('ORIG-DIGEST FAIL 50828 声明 %d B @%d 越出文件长度 %d ⇒ 标签不可信' % (cnt, val, len(data)))
        return 1
    ty2, cnt2, val2, _ = d0[TAG_RAW_DIGEST]
    if ty2 != 1 or cnt2 != 16:
        print('ORIG-DIGEST FAIL 50973 应为 type=1 count=16，实得 type=%d count=%d' % (ty2, cnt2))
        return 1
    # count=16 > 4 ⇒ 值字段是**偏移**（按内联取会读到条目区尾巴；本轮实测错过一次，读出 d4135b02…）
    if val2 + 16 > len(data):
        print('ORIG-DIGEST FAIL 50973 的偏移 %d 越界' % val2)
        return 1
    declared = data[val2:val2 + 16]
    stored = data[val:val + cnt]

    if mutant:
        if byte_at >= cnt:
            print('ORIG-DIGEST FAIL 变异位 %d 超出载荷长度 %d' % (byte_at, cnt))
            return 2
        buf = bytearray(data)
        old = buf[val + byte_at]
        buf[val + byte_at] = (old + 0x5A) & 0xFF          # 总长不变，只动一个字节
        with open(mutant, 'wb') as fp:
            fp.write(bytes(buf))
        print('ORIG-DIGEST MUTANT 写出 %s（载荷第 %d 个字节 %d → %d，文件总长不变 %d）'
              % (os.path.basename(mutant), byte_at, old, buf[val + byte_at], len(buf)))
        return 0

    got = hashlib.md5(stored).digest()
    if got != declared:
        print('ORIG-DIGEST FAIL 50828 存储 %d B 的 MD5 实算=%s 声明=%s ⇒ 嵌入件与声明不符'
              % (cnt, got.hex(), declared.hex()))
        return 1
    print('ORIG-DIGEST OK 50828 存储=%d B MD5=%s（与 50973 声明逐字节相等）' % (cnt, got.hex()))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
