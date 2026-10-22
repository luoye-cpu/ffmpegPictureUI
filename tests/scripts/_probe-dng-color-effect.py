# -*- coding: utf-8 -*-
"""色彩标准位"真的被 Adobe 读者用了"的开/关对撞（第 77 条 ⑱f 的彩色版，账本 §44）。

判据不是"标签在不在"，而是**改一个值，渲染就得跟着变**：
  · AsShotNeutral(0xC628)：R 通道中性值 ×2 ⇒ 红增益减半 ⇒ 8-bit sRGB 渲染逐像素大改；
  · ForwardMatrix1/2(0xC714/0xC715)：把两个 tag 号改成未用值 ⇒ 读者回退到 ColorMatrix。
    这是"DNG SDK 实际用哪张矩阵"的开关对照：删 FM 的像素差必须显著大于只改 CM1。
⚠ 为什么钉 ForwardMatrix 而不是 ColorMatrix：本机产物同时带 CM1/2 与 FM1/2，dng_validate
（DNG SDK 1.7.1）在 FM 在场时走 ForwardMatrix ⇒ 只改 CM1 时渲染几乎不动（实测 MAD=0.117 vs
删 FM 的 1.605、改 ASN 的 38.5）。CM1 变异保留为**读数**（证明"哪张矩阵在生效"），不作通过条件。
⚠ 不能直接缩放 FM：改动会破坏 D50 行归一化，dng_validate 判 106（实测）⇒ 用删除对照。
两条变异都是**同长就地改**（SRATIONAL 分子 / tag 号），不改偏移与总长。
原件的两个值也从产物里读出来打印 —— 证明变异打在真实存在的位上，不是造了个空标签。

读者口径：dng_validate 1.7.1 默认 8-bit sRGB（本探针不传 -16/-cs*），所以读数只在这一档内可比。
⚠ 这不是"ACR 也会这样"的证明：ACR 的 profile 选取与 dng_validate 的默认路径不同（账本 §44 读者矩阵）。

用法：py tests/scripts/_probe-dng-color-effect.py <product.dng> [--out DIR]
退出码：0 = 两条变异各自生效；1 = 有变异不生效；2 = 缺 dng_validate/标签/依赖。
"""
import argparse
import importlib.util
import os
import shutil
import struct
import subprocess
import sys

R = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
DV = os.path.join(R, 'tools', 'src', 'dngtool', 'build', 'Release', 'dng_validate.exe')
HERE = os.path.dirname(os.path.abspath(__file__))
SPEC = importlib.util.spec_from_file_location('lensread', os.path.join(HERE, '_readback-dng-lens.py'))
LR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LR)

ASN, CM1, FM1, FM2 = 0xC628, 0xC621, 0xC714, 0xC715


def find(ifd, tag):
    ent = ifd.get(tag)
    if not ent:
        return None
    ty, cnt, val, e = ent
    sz = LR.TYPE_SIZES.get(ty, 4) * cnt
    return dict(ty=ty, cnt=cnt, val=val, entry=e, off=val if sz > 4 else None, size=sz)


def render(dv, src, base):
    p = subprocess.run([dv, '-tif', base, src], capture_output=True, text=True, errors='replace')
    out = base + '.tif'
    return p.returncode, out if os.path.exists(out) else None, (p.stdout + p.stderr)[-200:]


def means(tif):
    from PIL import Image
    import numpy as np
    a = np.asarray(Image.open(tif).convert('RGB'), dtype=np.float64)
    return a.reshape(-1, 3).mean(axis=0), a.shape[:2]


def mad(tif_a, tif_b):
    """两幅渲染的逐通道平均绝对差（0..255 级）。"""
    from PIL import Image
    import numpy as np
    a = np.asarray(Image.open(tif_a).convert('RGB'), dtype=np.float64)
    b = np.asarray(Image.open(tif_b).convert('RGB'), dtype=np.float64)
    if a.shape != b.shape:
        return float('nan')
    return float(np.abs(a - b).mean())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('dng')
    ap.add_argument('--out', default=os.path.join(R, 'tests', 'output', 'dng-crop', 'color-effect'))
    a = ap.parse_args()
    if not os.path.isfile(a.dng) or not os.path.isfile(DV):
        print('COLOR-SKIP 缺产物或 dng_validate')
        return 2
    try:
        import numpy  # noqa: F401
        import PIL  # noqa: F401
    except Exception as e:
        print('COLOR-SKIP 缺依赖: %s' % e)
        return 2
    os.makedirs(a.out, exist_ok=True)
    data = open(a.dng, 'rb').read()
    t = LR.Tiff(data, '<')
    ifd = t.ifd(t.u32(4))
    asn, cm = find(ifd, ASN), find(ifd, CM1)
    fm1, fm2 = find(ifd, FM1), find(ifd, FM2)
    if not asn or not cm or asn['cnt'] != 3 or cm['cnt'] != 9 or asn['off'] is None or cm['off'] is None:
        print('COLOR-SKIP 产物缺 AsShotNeutral(3) 或 ColorMatrix1(9) 的外置值')
        return 2
    if not fm1 or not fm2 or fm1['cnt'] != 9 or fm2['cnt'] != 9:
        print('COLOR-SKIP 产物没有 ForwardMatrix1/2(9)：本判据钉的是 DNG SDK 实际使用的那对矩阵')
        return 2
    asn_v = struct.unpack_from('<3i', data, asn['off'])
    cm_v = struct.unpack_from('<9i', data, cm['off'])

    # ── 变异 1：AsShotNeutral R ×2（分子；分母不动）──────────────────────────
    b1 = bytearray(data)
    struct.pack_into('<i', b1, asn['off'], asn_v[0] * 2)
    p1 = os.path.join(a.out, 'asn-mut.dng')
    open(p1, 'wb').write(bytes(b1))
    # ── 变异 2：ColorMatrix1 第一行 ×0.8（读数：FM 在场时它是否还生效）────────
    b2 = bytearray(data)
    for i in range(3):
        struct.pack_into('<i', b2, cm['off'] + 8 * i, int(cm_v[2 * i] * 0.8))
    p2 = os.path.join(a.out, 'cm1-mut.dng')
    open(p2, 'wb').write(bytes(b2))
    # ── 变异 3：删掉 ForwardMatrix1/2（tag 号改成未用值 0xFFFD）⇒ 读者回退到 CM ──
    b3 = bytearray(data)
    for ent in (fm1, fm2):
        struct.pack_into('<H', b3, ent['entry'], 0xFFFD)
    p3 = os.path.join(a.out, 'fm-del.dng')
    open(p3, 'wb').write(bytes(b3))

    rc0, t0, e0 = render(DV, a.dng, os.path.join(a.out, 'orig'))
    rc1, t1, e1 = render(DV, p1, os.path.join(a.out, 'asn'))
    rc2, t2, e2 = render(DV, p2, os.path.join(a.out, 'cm1'))
    rc3, t3, e3 = render(DV, p3, os.path.join(a.out, 'fmdel'))
    if rc0 or rc1 or rc2 or rc3 or not (t0 and t1 and t2 and t3):
        print('COLOR-FAIL dng_validate rc=%d/%d/%d/%d %s' % (rc0, rc1, rc2, rc3, e0))
        return 2
    m0, shape = means(t0)
    m1, _ = means(t1)
    d_asn = (m1 - m0) / (m0 + 1e-9) * 100.0
    mad_asn = mad(t0, t1)
    mad_cm = mad(t0, t2)
    mad_fm = mad(t0, t3)
    print('COLOR-READ 原件 %dx%d 8-bit sRGB · 均值 R/G/B=%.2f/%.2f/%.2f' % (shape[1], shape[0], *m0))
    print('COLOR-READ ASN R×2      ⇒ 均值Δ%%=%+.2f/%+.2f/%+.2f · 逐像素 MAD=%.3f'
          % (d_asn[0], d_asn[1], d_asn[2], mad_asn))
    print('COLOR-READ CM1 行0×0.8  ⇒ 逐像素 MAD=%.3f（FM 在场 ⇒ 预期很小）' % mad_cm)
    print('COLOR-READ 删 FM1/2     ⇒ 逐像素 MAD=%.3f（回退到 ColorMatrix）' % mad_fm)
    ok_asn = mad_asn > 5.0
    ok_fm = mad_fm > 0.5 and mad_fm > 10.0 * max(mad_cm, 1e-6)
    print('COLOR-%s ASN 生效=%d（MAD>5）· FM 是生效矩阵=%d（MAD>0.5 且 >10×改 CM1）· 改 CM1 后 MAD=%.3f'
          % ('OK' if (ok_asn and ok_fm) else 'FAIL', int(ok_asn), int(ok_fm), mad_cm))
    return 0 if (ok_asn and ok_fm) else 1


if __name__ == '__main__':
    sys.exit(main())
