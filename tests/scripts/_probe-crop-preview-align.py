# -*- coding: utf-8 -*-
"""裁剪原点对拍：把"机身内嵌 JPEG"当对照臂，钉住 DefaultCropOrigin 是不是猜出来的。

问题：Canon 的 AspectInfo(0x009a) 只给 CroppedImageWidth/Height/Left/Top=(1936x1288@0,0)，
而 SensorInfo(0x00e0) 另给 DefaultCropAbsolute；LibRaw 把两者**相加**成
`raw_inset_crops[0]=(4,3,1936x1288)`（open.cpp:742-747）。dngtool 现在直接采信这个
合成原点 ⇒ 必须证明 (4,3) 不是 LibRaw 的合成怪癖。

判据：CR2 内嵌预览就是机身 JPEG，尺寸与裁剪框相同（1936x1288）。把源 RAW 去马赛克后
按候选原点滑窗，取 1936x1288 子图，与预览算**梯度幅值相关系数**；正确的原点应显著胜出。
这不是"看着像"：位移 1 px 就会让相关性掉下来（本机实测 (4,3)=0.942、(3,3)=0.776）。

用法：
  py tests/scripts/_probe-crop-preview-align.py <raw> [--expect 4,3] [--search 8] [--out DIR]
退出码：0 = 期望原点就是最优且领先 ≥0.05；1 = 不成立；2 = 素材/依赖缺失（记未判）。
"""
import argparse
import os
import subprocess
import sys

R = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
EX = os.path.join(R, 'publish', 'PLAN', 'exiftool', 'exiftool.exe')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('raw')
    ap.add_argument('--expect', default='4,3')
    ap.add_argument('--search', type=int, default=8)
    ap.add_argument('--out', default=os.path.join(R, 'tests', 'output', 'dng-crop', 'align'))
    a = ap.parse_args()
    if not os.path.isfile(a.raw) or not os.path.isfile(EX):
        print('ALIGN-SKIP 缺素材/工具')
        return 2
    try:
        import numpy as np
        import rawpy
        from PIL import Image
    except Exception as e:
        print('ALIGN-SKIP 缺依赖: %s' % e)
        return 2

    os.makedirs(a.out, exist_ok=True)
    jpg = os.path.join(a.out, os.path.splitext(os.path.basename(a.raw))[0] + '-preview.jpg')
    with open(jpg, 'wb') as fh:
        p = subprocess.run([EX, '-b', '-PreviewImage', a.raw], stdout=fh, stderr=subprocess.DEVNULL)
    if p.returncode != 0 or os.path.getsize(jpg) < 10000:
        print('ALIGN-SKIP 源件没有可用的 PreviewImage')
        return 2

    prev = np.asarray(Image.open(jpg).convert('L'), dtype=np.float32)
    H, W = prev.shape
    raw = rawpy.imread(a.raw)
    img = raw.postprocess(use_camera_wb=True, output_bps=8)
    raw.close()
    g = np.asarray(img, dtype=np.float32)
    lum = 0.299 * g[:, :, 0] + 0.587 * g[:, :, 1] + 0.114 * g[:, :, 2]

    def edges(x):
        gy = np.zeros_like(x)
        gx = np.zeros_like(x)
        gy[1:-1, :] = x[2:, :] - x[:-2, :]
        gx[:, 1:-1] = x[:, 2:] - x[:, :-2]
        return np.hypot(gx, gy)

    pe = edges(prev)
    pe = (pe - pe.mean()) / (pe.std() + 1e-6)
    scored = []
    for y in range(0, a.search + 1):
        for x in range(0, a.search + 1):
            sub = lum[y:y + H, x:x + W]
            if sub.shape != prev.shape:
                continue
            se = edges(sub)
            se = (se - se.mean()) / (se.std() + 1e-6)
            scored.append((float((se * pe).mean()), x, y))
    scored.sort(reverse=True)
    if len(scored) < 3:
        print('ALIGN-SKIP 搜索窗超出图像')
        return 2
    best, bx, by = scored[0]
    second = scored[1][0]
    want = [int(v) for v in a.expect.split(',')]
    hit = (bx, by) == (want[0], want[1])
    print('ALIGN-READ 预览 %dx%d · 最优=(%d,%d) corr=%.4f · 次优=(%d,%d) corr=%.4f · 期望=%s'
          % (W, H, bx, by, best, scored[1][1], scored[1][2], second, a.expect))
    ok = hit and (best - second) >= 0.05
    print('ALIGN-%s %s' % ('OK' if ok else 'FAIL',
                           '最优原点=期望且领先 ≥0.05' if ok else '最优原点≠期望或领先不足'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
