#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""_cmp-raw-plane-identity.py —— 证明"源 RAW 的 sensor 平面 == DNG 自己的平面"（第 77 条 ⑲d 用）

为什么必须有：2026-10-19 的丢弃政策（默认不嵌整个原文件）只有在一句话成立时才不动图像——
"DNG 自己那份 raw 就是原件的逐位拷贝"。这句话不能靠声称，要逐像素量。
用 LibRaw（独立实现，rawpy 0.27.1 / LibRaw 0.22.1）读两边的 `raw_image`（不做黑电平/白电平缩放、
不做 demosaic），逐点比。形状不同直接判红（尺寸/边距任何一处不同都不许当成"相同"）。

用法: py _cmp-raw-plane-identity.py <源 raw/dng> <产物 dng>
退出 0 = 逐位相同；1 = 有差异（点名不同点数与 max|Δ|）；2 = 用法/读取失败。
"""
import sys

import rawpy


def plane(path):
    with rawpy.imread(path) as im:
        return im.raw_image.copy()


def main(argv):
    if len(argv) < 3:
        print('用法: _cmp-raw-plane-identity.py <源> <产物>')
        return 2
    try:
        a = plane(argv[1])
        b = plane(argv[2])
    except Exception as exc:
        print('RAW-PLANE FAIL 读取失败: %s' % exc)
        return 2
    # 3 维视图（多平面）允许**尾部补零平面**：LibRaw/rawpy 读 LinearRaw/3 平面件时会把
    # 通道数补到 4（实测 Sigma/Foveon X3F：源 (1792,2688,3)、产物 (1792,2688,4) 且第 4 平面全零）。
    # 只在"多出来的平面逐位为零"时放行，并把这件事打在读数里；有任何非零 ⇒ 照旧判红。
    padNote = ''
    if a.shape != b.shape and a.ndim == 3 and b.ndim == 3 and a.shape[:2] == b.shape[:2]:
        n = min(a.shape[2], b.shape[2])
        extra = b[:, :, n:] if b.shape[2] > a.shape[2] else a[:, :, b.shape[2]:]
        if int((extra != 0).sum()) == 0:
            padNote = '（尾随补零平面 %d 个，逐位为零）' % (max(a.shape[2], b.shape[2]) - n)
            a, b = a[:, :, :n], b[:, :, :n]
    if a.shape != b.shape:
        print('RAW-PLANE FAIL 形状不同 %s vs %s ⇒ 无从逐位比' % (a.shape, b.shape))
        return 1
    d = a.astype('int64') - b.astype('int64')
    diff = int((d != 0).sum())
    worst = int(abs(d).max()) if d.size else 0
    if diff:
        print('RAW-PLANE FAIL 不同点=%d/%d max|Δ|=%d ⇒ DNG 不是原件的逐位拷贝，丢弃嵌入件会动图像'
              % (diff, d.size, worst))
        return 1
    print('RAW-PLANE OK %s ↔ %s 逐位相同（%d 点全等，min=%d max=%d%s）'
          % (argv[1].split('/')[-1], argv[2].split('/')[-1], d.size,
             int(a.min()), int(a.max()), padNote))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
