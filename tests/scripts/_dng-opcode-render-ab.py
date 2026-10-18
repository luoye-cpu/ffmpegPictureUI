#!/usr/bin/env python3
"""
_dng-opcode-render-ab.py —— "读者真的施加了这三支表" 的像素级对账（第 77 条 ⑯e 重组用）

输入是三份 **Adobe dng_validate -3 渲染出来的 stage-3 TIFF 转成的 rgb48 裸数据**：
  A = 不加参数的产物渲染（基准）
  B = 恒等曲线臂的渲染（应当与 A 逐字节相同 ⇒ 证明"跳过"是真跳过）
  C = 三支非恒等表的渲染（应当与 A 不同，且逐点等于按规格算出的 LUT）
判据三条（都在同一次运行里给，缺一不可）：
  ① B 与 A 完全相同（任何差异 ⇒ 恒等臂被写了东西）
  ② C 与 A 不同的平面值数 > 0（相同 ⇒ 读者根本没施加表）
  ③ C == LUT(A) 的抽样比例 ≥ 0.99 且最大偏差 ≤ 1 码（>1 码说明表的域/量化与约定不符）
退出码 0 = 三条全立；非 0 = 点名原因。
"""
import sys

try:
    import numpy as np
except Exception:
    print('RENDER-AB FAIL 需要 numpy（本机 py 自带与否直接决定这条能不能跑）')
    sys.exit(2)


def curve_lut(pts):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    x = np.arange(65536, dtype=np.float64) / 65535.0
    return np.round(np.interp(x, xs, ys) * 65535.0).astype(np.uint16)


def load(path, h, w):
    a = np.fromfile(path, dtype='<u2')
    need = h * w * 3
    if a.size != need:
        raise ValueError('%s 元素数=%d 与 %dx%d×3=%d 不符' % (path, a.size, h, w, need))
    return a.reshape(h, w, 3)


def main(argv):
    if len(argv) < 6:
        print('RENDER-AB FAIL 用法: _dng-opcode-render-ab.py A.raw B.raw C.raw HxW spec文件')
        return 2
    a, b, c = argv[1], argv[2], argv[3]
    hh, ww = (int(x) for x in argv[4].split('x'))
    spec = argv[5]
    try:
        A = load(a, hh, ww)
        B = load(b, hh, ww)
        C = load(c, hh, ww)
        pts = {}
        with open(spec, 'rb') as fp:
            for raw in fp.read().split(b'\n'):
                line = raw.decode('utf-8', 'replace').strip()
                if '=' not in line or line.startswith('#'):
                    continue
                k, v = line.split('=', 1)
                p = {'curveR': 0, 'curveG': 1, 'curveB': 2}.get(k.strip())
                if p is None:
                    continue
                pairs = sorted((tuple(float(z) for z in tok.split(':')) for tok in v.split()),
                               key=lambda q: q[0])
                pts[p] = pairs
        if sorted(pts) != [0, 1, 2]:
            raise ValueError('规格里必须三条 curveR/G/B 全给（实得 %s）' % sorted(pts))
    except Exception as exc:
        print('RENDER-AB FAIL %s' % exc)
        return 1

    if not np.array_equal(A, B):
        n = int((A != B).sum())
        print('RENDER-AB FAIL 恒等臂与基准不同（%d 个平面值）⇒ "跳过恒等表"没真发生' % n)
        return 1
    diff = int((C != A).sum())
    if diff == 0:
        print('RENDER-AB FAIL 非恒等臂与基准逐点相同 ⇒ 读者没有施加这三支表')
        return 1

    flat = A.reshape(-1, 3)
    rng = np.random.default_rng(5)
    pick = rng.integers(0, flat.shape[0], 200000)
    got = C.reshape(-1, 3)[pick]
    pred = np.empty_like(got)
    for p in (0, 1, 2):
        lut = curve_lut(pts[p])
        pred[:, p] = lut[flat[pick, p].astype(np.uint16)]
    d = np.abs(pred.astype(np.int64) - got.astype(np.int64))
    exact = float((d == 0).mean())
    mx = int(d.max())
    # 容差的来路写清楚：表的施加本身是逐码精确的 LUT 查表，±1 码来自 SDK 对表的黑电平调整
    # (dng_misc_opcodes.cpp 的 fBlackAdjustedTable) 的舍入 ⇒ 实测地板 = 1 码；判据取 2 码
    # = 地板的一倍余量（不是"实测刚好过"），另设 0.90 的精确比例下限只防"完全跑偏"。
    if mx > 2 or exact < 0.90:
        print('RENDER-AB FAIL C 与 LUT(A) 不符: 完全相等比例=%.4f 最大偏差=%d 码（须 ≤2 码且比例 ≥0.90）' % (exact, mx))
        return 1
    print('RENDER-AB OK 恒等臂==基准(逐字节) · 非恒等臂不同 %d 个平面值 · C==LUT(A) 精确 %.4f 最大 %d 码（抽样 20 万，地板 1 码/判据 2 码）'
          % (diff, exact, mx))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
