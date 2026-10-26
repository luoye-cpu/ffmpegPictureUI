# -*- coding: utf-8 -*-
"""ACR 路线（dngtool -rdp）渲染读手：只出读数，不判词。

用法：
  py _readback-acr-render.py ratio  <with-op.tif> <without-op.tif>
      → ACR-RATIO with=<角/中> without=<角/中> rel=<with/without>
  py _readback-acr-render.py space  <rec2020.tif> <srgb.tif>
      → ACR-SPACE mad=… psnr=… oog=… （srgb 腿 == clip(rec2020 腿 × M) 的残差）
  py _readback-acr-render.py dims   <a.tif> [<b.tif>]
      → ACR-DIMS w=… h=… [w2=… h2=…]
  py _readback-acr-render.py median <a.tif> <b.tif>
      → ACR-MEDIAN a=… b=… ratio=…
        两条解码臂的中位亮度比（线性 ≈ 同量级；gamma 编码会把小值抬到 ~2.7×）。

为什么用 memmap：产物是 16-bit 未压缩 RGB（一张 6024×4024 ≈ 145 MB），
`tifffile.imread` 会在每次调用里整幅落内存；memmap + 步长视图只碰抽样格。
⚠ 判"两腿是否真的不同"**不能**用抽样：必须比对整个文件的哈希（由调用方做）。
"""
import hashlib
import sys

import numpy as np
import tifffile

M_R2X = np.array([[0.636958, 0.144617, 0.168881],
                  [0.262700, 0.677998, 0.059302],
                  [0.000000, 0.028073, 1.060985]], dtype=np.float64)
M_X2S = np.array([[3.2404542, -1.5371385, -0.4985314],
                  [-0.9692660, 1.8760108, 0.0415560],
                  [0.0556434, -0.2040259, 1.0572252]], dtype=np.float64)
M = (M_X2S @ M_R2X).astype(np.float32)


def load(path, step=2):
    a = tifffile.memmap(path)
    a = np.asarray(a[::step, ::step, :3], dtype=np.float32) / 65535.0
    return a


def ratio(a, b):
    def cc(img):
        lum = img.mean(axis=2)
        h, w = lum.shape
        k = min(h, w) // 8
        corner = np.mean([lum[:k, :k].mean(), lum[:k, -k:].mean(),
                          lum[-k:, :k].mean(), lum[-k:, -k:].mean()])
        mid = lum[h // 2 - k:h // 2 + k, w // 2 - k:w // 2 + k].mean()
        return float(corner), float(mid), float(corner / mid)

    ca, ma, ra = cc(a)
    cb, mb, rb = cc(b)
    print("ACR-RATIO with=%.4f without=%.4f rel=%.4f "
          "(corner/center with=%.4f/%.4f without=%.4f/%.4f)"
          % (ra, rb, ra / rb, ca, ma, cb, mb))


def space(rec2020, srgb):
    conv = rec2020.reshape(-1, 3) @ M.T
    oog = float(np.any((conv < -1e-6) | (conv > 1.0 + 1e-6), axis=1).mean())
    conv = np.clip(conv, 0.0, 1.0)
    diff = np.abs(conv - srgb.reshape(-1, 3))
    mse = float(np.mean(diff ** 2))
    psnr = float("inf") if mse == 0 else 10 * np.log10(1.0 / mse)
    print("ACR-SPACE mad=%.6e psnr=%.2f oog=%.6f" % (float(diff.mean()), psnr, oog))


def median(a, b):
    la = float(np.median(a.mean(axis=2)))
    lb = float(np.median(b.mean(axis=2)))
    print("ACR-MEDIAN a=%.6f b=%.6f ratio=%.4f"
          % (la, lb, (la / lb) if lb > 0 else 0.0))


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()[:16]


def main():
    if len(sys.argv) < 3:
        sys.exit(2)
    mode = sys.argv[1]
    if mode == "ratio":
        ratio(load(sys.argv[2]), load(sys.argv[3]))
    elif mode == "space":
        a = load(sys.argv[2], step=2)
        b = load(sys.argv[3], step=2)
        space(a, b)
        print("ACR-SPACE sha=%s/%s identical=%d"
              % (sha(sys.argv[2]), sha(sys.argv[3]),
                 1 if sha(sys.argv[2]) == sha(sys.argv[3]) else 0))
    elif mode == "dims":
        a = tifffile.memmap(sys.argv[2])
        line = "ACR-DIMS h=%d w=%d" % (a.shape[0], a.shape[1])
        if len(sys.argv) > 3:
            b = tifffile.memmap(sys.argv[3])
            line += " h2=%d w2=%d" % (b.shape[0], b.shape[1])
        print(line)
    elif mode == "median":
        median(load(sys.argv[2]), load(sys.argv[3]))
    else:
        sys.exit(2)


if __name__ == "__main__":
    main()
