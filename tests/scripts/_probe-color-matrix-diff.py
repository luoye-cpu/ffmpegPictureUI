# -*- coding: utf-8 -*-
"""色彩矩阵逐值对撞：厂商 ColorMatrix* 与产物 ColorMatrix1/2 到底是不是同一组数。

为什么要这支：旧普查把 "色彩配方/矩阵" 记成 86 条（名字正则命中就算），但里面混进了
ChromaticAberrationParams（镜头 CA 表）、Sony_SR2DataIFD_0x78cc（不透明块）、
FilterEffect*（机内风格）等。本探针只回答两件事：
  1) 厂商组的矩阵（Olympus/Samsung/Sony 的定点串、PhaseOne/Leaf/Pentax 的有理数）
     与产物 ColorMatrix1/2 是否逐元素相同（直接 or 逆矩阵）；
  2) 若不同，差多少 —— 用来给"未搬运"定性，而不是靠名字猜。
定点串的尺度由**行和**定：Samsung 行和=256、Sony=1024、Pentax A2/B2=8192/16384
（都是 DNG 矩阵行和≈1 的定点形式），不与产物比对时不做其它解释。

用法：
  py tests/scripts/_probe-color-matrix-diff.py --ledger <ledger.csv> --products <dir> [--out FILE]
退出码：0 = 读数完成（不论是否相同）；2 = 缺素材/解析失败。
"""
import argparse
import csv
import json
import os
import re
import subprocess
import sys

R = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
EX = os.path.join(R, 'publish', 'PLAN', 'exiftool', 'exiftool.exe')
SCALES = (256, 1024, 4096, 8192, 16384)


def nums(v):
    return [float(x) for x in re.findall(r'-?\d+(?:\.\d+)?', str(v))]


def mat(v):
    a = nums(v)
    return a[:9] if len(a) >= 9 else None


def inv3(m):
    a, b, c, d, e, f, g, h, i = m
    det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)
    if abs(det) < 1e-12:
        return None
    return [
        (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
        (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
        (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det,
    ]


def maxdiff(x, y):
    return max(abs(p - q) for p, q in zip(x, y))


def exif(path, *tags):
    p = subprocess.run([EX, '-json', '-G1', '-a', '-u', '-s'] + ['-' + t for t in tags] + [path],
                       capture_output=True, text=True, encoding='utf-8', errors='replace')
    try:
        arr = json.loads(p.stdout)
    except Exception:
        return {}
    if not arr:
        return {}
    out = {}
    for k, v in arr[0].items():
        out.setdefault(k.split(':', 1)[-1], v)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ledger', default=os.path.join(R, 'tests', 'output', 'dng-audit', 'run4', 'ledger.csv'))
    ap.add_argument('--products', default=os.path.join(R, 'tests', 'output', 'dng-crop-map', 'run-b'))
    ap.add_argument('--out', default='')
    a = ap.parse_args()
    if not os.path.isfile(a.ledger) or not os.path.isdir(a.products):
        print('CMAT-SKIP 缺 ledger 或产物目录')
        return 2

    rows = []
    with open(a.ledger, encoding='utf-8-sig') as fh:
        for r in csv.DictReader(fh):
            if r['tag'] in ('ColorMatrix', 'ColorMatrix1', 'ColorMatrix2',
                            'ColorMatrixA2', 'ColorMatrixB2'):
                rows.append(r)
    if not rows:
        print('CMAT-SKIP ledger 里没有 ColorMatrix* 行')
        return 2

    lines = []
    for r in rows:
        stem = os.path.splitext(os.path.basename(r['file']))[0]
        prod = os.path.join(a.products, stem + '.dng')
        v = mat(r['value'])
        if not prod or not os.path.isfile(prod) or not v:
            lines.append('CMAT %-28s %-14s SKIP(无产物或值不足9项)' % (stem, r['tag']))
            continue
        pj = exif(prod, 'ColorMatrix1', 'ColorMatrix2')
        cm1 = mat(pj.get('ColorMatrix1')) if 'ColorMatrix1' in pj else None
        cm2 = mat(pj.get('ColorMatrix2')) if 'ColorMatrix2' in pj else None
        # 定点尺度：选使行和≈1 的那个（DNG ColorMatrix 的常见归一化）
        scale = 1.0
        if max(abs(x) for x in v) > 4:
            best = None
            for s in SCALES:
                err = max(abs(sum(v[i * 3:i * 3 + 3]) / s - 1.0) for i in range(3))
                if best is None or err < best[0]:
                    best = (err, s)
            if best and best[0] < 0.05:
                scale = float(best[1])
        vn = [x / scale for x in v]
        vi = inv3(vn)
        cands = []
        for name, cm in (('CM1', cm1), ('CM2', cm2)):
            if not cm:
                continue
            cands.append((name + '/direct', maxdiff(vn, cm)))
            if vi:
                cands.append((name + '/inv', maxdiff(vi, cm)))
        if not cands:
            lines.append('CMAT %-28s %-14s SKIP(产物无 ColorMatrix)' % (stem, r['tag']))
            continue
        best = min(cands, key=lambda t: t[1])
        lines.append('CMAT %-28s %-14s scale=%-6g rowsum=%s best=%s max|d|=%.4f  all=[%s]'
                     % (stem, r['tag'], scale,
                        '/'.join('%.4f' % (sum(vn[i * 3:i * 3 + 3])) for i in range(3)),
                        best[0], best[1],
                        ', '.join('%s=%.3f' % (n, d) for n, d in cands)))
    txt = '\n'.join(lines)
    print(txt)
    if a.out:
        with open(a.out, 'w', encoding='utf-8') as fh:
            fh.write(txt + '\n')
    return 0


if __name__ == '__main__':
    sys.exit(main())
