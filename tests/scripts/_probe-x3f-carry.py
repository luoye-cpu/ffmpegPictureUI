#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""_probe-x3f-carry.py —— Foveon X3F → DNG 的"能开、电平对、渲染一致"三件（第 77 条 ㉗ 组）

背景（账本 §35/§40）：X3F 4/4 打不开的根因是 `USE_X3FTOOLS` 从未定义（解码器整个被宏包住 ⇒ 空 TU）；
打开宏之后又量出一条**假声明**：非 Bayer/Linraw 源一律写 WhiteLevel=65535，而 Foveon 的存储值
只有 12-bit（源 white_level=2100）⇒ 读者按 65535 归一化，整幅渲染只剩 3%。修法：线性源按源声明写。

判据（每条都由**第二个实现**给读数，不采信 dngtool 自己的日志）：
  1) 四件 X3F 全部 rc=0 且出件（修前 4/4 都是 enc=2 打不开）。
  2) DP1 产物：rawpy(独立 LibRaw 构建) 读到的 white_level == 源 white_level；
     三个存储平面与源**逐位相同**；rawpy postprocess 的 8-bit 渲染与源 max|Δ|=0。
  3) 负控：修前 exe 同一输入必须复现缺陷（WhiteLevel=65535 且渲染均值比 < 0.1）⇒ 判据有牙。

退出码：0 = 1)2)3) 全成立；1 = 有齿的项不成立；2 = 跑不动（缺 rawpy/缺件/缺修前 exe ⇒ 记未判）。

用法：py _probe-x3f-carry.py <dngtool.exe> <outdir> <修前 dngtool.exe|->
"""
import os
import subprocess
import sys

SAMPLES = [
    ('tests/output/raw_samples/Sigma_DP1.X3F', True),
    ('tests/output/raw_samples2/Sigma_DP1_7.x3f', False),
    ('tests/output/raw_samples2/Sigma_SIGMA_SD14_8.x3f', False),
    ('tests/output/raw_samples2/Sigma_SIGMA_SD15_9.x3f', False),
]


def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    return p.returncode, (p.stdout or '') + (p.stderr or '')


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    dngtool, outdir = argv[1], argv[2]
    old = argv[3] if len(argv) > 3 and argv[3] != '-' else None
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    os.chdir(root)
    if not os.path.isdir(outdir):
        os.makedirs(outdir)

    try:
        import rawpy
        import numpy as np
    except ImportError as e:                                   # noqa: BLE001
        print('X3F-CARRY 未判 缺 rawpy/numpy（%r）⇒ 独立读者缺席，不记绿' % (e,))
        return 2

    fails, pending = [], []
    # 1) 四件都能开、都出件
    outs = {}
    for rel, is_dp1 in SAMPLES:
        if not os.path.exists(rel):
            fails.append('样本缺席 %s' % rel)
            continue
        dst = os.path.join(outdir, os.path.basename(rel) + '.dng')
        if os.path.exists(dst):
            os.remove(dst)
        rc, talk = run([dngtool, '-e', '-lossless', '-i', rel, '-O', dst])
        ok = rc == 0 and os.path.exists(dst)
        print('X3F-CARRY 读数 %-40s rc=%d 产物=%s'
              % (os.path.basename(rel), rc, ('%d B' % os.path.getsize(dst)) if os.path.exists(dst) else '无'))
        if not ok:
            fails.append('%s 打不开/没出件（rc=%d）:: %s' % (os.path.basename(rel), rc, talk.strip()[-160:]))
        else:
            outs[rel] = dst

    dp1_src = SAMPLES[0][0]
    dp1_out = outs.get(dp1_src)
    if not dp1_out:
        print('X3F-CARRY 未判 DP1 没出件 ⇒ 电平/渲染这两条判据没量到')
        return 2

    # 2) 电平 + 存储平面 + 渲染（全部由 rawpy 给数）
    try:
        with rawpy.imread(dp1_src) as r:
            src_white = int(r.white_level)
            src_raw = r.raw_image.copy()
            src_rgb = r.postprocess(use_camera_wb=True, no_auto_bright=True, output_bps=8)
        with rawpy.imread(dp1_out) as r:
            out_white = int(r.white_level)
            out_raw = r.raw_image.copy()
            out_rgb = r.postprocess(use_camera_wb=True, no_auto_bright=True, output_bps=8)
    except Exception as e:                                     # noqa: BLE001
        print('X3F-CARRY 未判 rawpy 读不动（%r）⇒ 不记绿' % (e,))
        return 2

    print('X3F-CARRY 读数 white_level 源=%d 产物=%d（读数来自 rawpy，不是 dngtool 日志）'
          % (src_white, out_white))
    if out_white != src_white:
        fails.append('产物 WhiteLevel=%d ≠ 源 white_level=%d（读者会按前者归一化）'
                     % (out_white, src_white))
    if src_raw.ndim == 3:
        planes = min(src_raw.shape[2], out_raw.shape[2])
        worst = 0
        for i in range(planes):
            d = np.abs(src_raw[:, :, i].astype('int64') - out_raw[:, :, i].astype('int64'))
            worst = max(worst, int(d.max()))
        print('X3F-CARRY 读数 存储平面逐位对撞：%d 个平面 max|Δ|=%d（产物第 4 平面为零填充，属读手形状，不计）'
              % (planes, worst))
        if worst != 0:
            fails.append('存储平面 max|Δ|=%d ≠ 0' % worst)
    if src_rgb.shape != out_rgb.shape:
        fails.append('渲染形状 %s ≠ %s' % (src_rgb.shape, out_rgb.shape))
    else:
        d = np.abs(src_rgb.astype('int64') - out_rgb.astype('int64'))
        print('X3F-CARRY 读数 渲染对撞：max|Δ|=%d mean|Δ|=%.3f（源均值 %.2f，产物 %.2f）'
              % (int(d.max()), float(d.mean()), float(src_rgb.mean()), float(out_rgb.mean())))
        if int(d.max()) != 0:
            fails.append('渲染 max|Δ|=%d ≠ 0' % int(d.max()))

    # 3) 负控：修前 exe 必须复现"按 65535 归一化 ⇒ 只剩 3%"这条缺陷
    if old is None:
        pending.append('未判 负控：没给修前 exe')
    elif not os.path.exists(old):
        pending.append('未判 负控：修前 exe 不在位 (%s)' % old)
    else:
        od = os.path.join(outdir, 'old_' + os.path.basename(dp1_src) + '.dng')
        if os.path.exists(od):
            os.remove(od)
        rc, _t = run([old, '-e', '-lossless', '-i', dp1_src, '-O', od])
        if rc != 0 or not os.path.exists(od):
            pending.append('未判 负控：修前 exe 没出件（rc=%d）' % rc)
        else:
            try:
                with rawpy.imread(od) as r:
                    ow = int(r.white_level)
                    orgb = r.postprocess(use_camera_wb=True, no_auto_bright=True, output_bps=8)
                ratio = float(orgb.mean()) / max(1e-9, float(src_rgb.mean()))
                print('X3F-CARRY 读数 负控(修前 exe)：white_level=%d 渲染均值 %.3f / 源 %.3f = %.3f'
                      % (ow, float(orgb.mean()), float(src_rgb.mean()), ratio))
                if not (ow > src_white * 10 and ratio < 0.1):
                    fails.append('负控失效：修前 exe 也应复现假声明（实得 white=%d ratio=%.3f）' % (ow, ratio))
            except Exception as e:                             # noqa: BLE001
                pending.append('未判 负控：rawpy 读不动修前产物（%r）' % (e,))

    for line in pending:
        print('X3F-CARRY %s' % line)
    if fails:
        for line in fails:
            print('X3F-CARRY FAIL %s' % line)
        return 1
    if any(p.startswith('未判 负控') for p in pending):
        # 没有负控就不能说"这条绿是修出来的" ⇒ 只记未判，不记绿
        return 2
    print('X3F-CARRY OK 四件都能开、DP1 的 white_level/存储平面/渲染都与源一致，负控复现缺陷')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
