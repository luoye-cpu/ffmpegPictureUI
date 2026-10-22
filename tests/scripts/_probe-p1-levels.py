#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_probe-p1-levels.py —— Phase One/Leaf IIQ 的黑/白电平声明是否落在**标准位**且与平面口径一致
（第 77 条 ㉓ 组用）

背景（一句话）：LibRaw 只把源声明的 0x021D 存着不扣（扣减在 raw2image 的
phase_one_subtract_black），而 phase_one_load_raw_c 顺手把 maximum 写成
`0xfffc - t_black`（扣减后的上限）。我们走 unpack() ⇒ 平面仍带偏置，
旧产物却按"扣减后"的跨度声明 WhiteLevel 而 BlackLevel 留 0。

判据分两层：
  默认层（不渲染，秒级）
    A1 产物 BlackLevel == 源 MOBJ 顶层 0x021D（由本脚本自己走 IIQ 容器读，不借 dngtool 的读数）；
    A2 BlackLevelRepeatDim == 1×1（SDK 的 SetBlackLevel 就是单值口径，写成 2×2 会把另三个通道判成 0）；
    A3 产物平面的实测最大样本 ≤ WhiteLevel（新声明不削高光）；
    A4 登记（不硬判）：实测最大样本落在旧跨度的哪一侧 —— 素材没到过那段高光时"削高光"就是未判定，
       不能拿它当判据的红；
    A5 WhiteLevel − BlackLevel == rawpy(源 IIQ).white_level（跨度与修补前逐数相同 ⇒ 归一化分母没动）。
       注：rawpy 是另一份 LibRaw 构建，读源得到的就是旧产物用的那个 maximum ⇒ A5 是第二实现背书，
       不是把 64508 抄进判据。
  重臂（--render-ab，两枚 Adobe 渲染）
    把产物等长改回旧声明（BlackLevel→0、WhiteLevel→旧跨度），两臂都 `dng_validate -2`，
    用 Adobe 自己的渲染量"错声明会让读者做什么"：
    A6 低分位读数 old − new ≈ black/span ×65535（整幅被抬上去的那一截，实测 ≈1041/65535 = 1.59% 满量程）；
    A7 旧臂的 65535 饱和像素数不得少于新臂（跨度更小 ⇒ 只会多削，反向就是读法错了）；
    A8 中位差同样落在该值附近 ⇒ 这不是"只有黑场受影响"，是整幅的偏置。
SDK 的兑现路径（写判据前读死的）：`dng_linearization_info.cpp:143-152` scale=1/(white−maxBlack)，
`:359` y = v − BlackLevel ⇒ 渲染值 = (v − BlackLevel)/(WhiteLevel − BlackLevel) ×65535。

用法：
  py _probe-p1-levels.py <产物 dng> <源 iiq>
  py _probe-p1-levels.py <产物 dng> <源 iiq> --render-ab <dng_validate.exe> <outdir>
退出码 0 = 判据成立；1 = 判红（点名哪条）；2 = 读不到/缺件。
"""
import importlib.util
import os
import statistics
import struct
import subprocess
import sys

_S = importlib.util.spec_from_file_location(
    'rd', os.path.join(os.path.dirname(os.path.abspath(__file__)), '_readback-dng-badpix.py'))
rd = importlib.util.module_from_spec(_S)
_S.loader.exec_module(rd)

TAG_BLDIM = 0xC619        # BlackLevelRepeatDim (50713)
TAG_BLACK = 0xC61A        # BlackLevel          (50714)
TAG_WHITE = 0xC61D        # WhiteLevel          (50717)
TAG_ACTIVE = 0xC68D       # ActiveArea          (50829)


def fail(msg):
    print('LEVELS-FAIL %s' % msg)
    return 1


def ifd0(data):
    e = '<' if data[:2] == b'II' else ('>' if data[:2] == b'MM' else None)
    if e is None:
        raise ValueError('产物字节序头不认识 %r' % data[:4])
    t = rd.Tiff(data, e)
    return t, t.ifd(t.u32(4))[0]


def val_at(t, d, tag):
    """按标签自己声明的类型取值（SRATIONAL/URATIONAL 要除分母，SHORT 不能当 LONG 读）。"""
    if tag not in d:
        raise ValueError('产物没有 tag 0x%04X' % tag)
    ty, cnt, val = d[tag]
    raw = t.raw((ty, cnt, val))
    if ty in (5, 10):
        v = struct.unpack('%s%dI' % (t.e, 2 * cnt), raw)
        return [v[2 * i] // max(1, v[2 * i + 1]) for i in range(cnt)]
    if ty in (9, 11):
        v = struct.unpack('%s%di' % (t.e, cnt), raw)
        return list(v)
    return t.ints((ty, cnt, val))


def plane_stats(path, active):
    """用 rawpy（另一份 LibRaw 实现）读产物自己的存储平面，只取 ActiveArea 内的样本。"""
    import numpy as np
    import rawpy
    with rawpy.imread(path) as im:
        a = im.raw_image
        if active:
            top, left, bottom, right = active
            if not (0 <= top < bottom <= a.shape[0] and 0 <= left < right <= a.shape[1]):
                raise ValueError('ActiveArea %s 越出平面 %dx%d' % (str(active), a.shape[0], a.shape[1]))
            a = a[top:bottom, left:right]
        flat = np.asarray(a).reshape(-1)
        return dict(n=int(flat.size), mn=int(flat.min()), mx=int(flat.max()),
                    p01=int(np.percentile(flat, 0.1)), med=int(np.median(flat)),
                    p999=int(np.percentile(flat, 99.9)))


def render(dv, dng, outdir, stem):
    out = os.path.join(outdir, stem)
    r = subprocess.run([dv, '-2', out, dng], capture_output=True, text=True)
    tif = out + '.tif'
    if r.returncode != 0 or not os.path.exists(tif):
        raise ValueError('%s 渲染失败 rc=%d 尾部=%s' % (stem, r.returncode,
                                                       (r.stdout + r.stderr)[-300:]))
    b = open(tif, 'rb').read()
    t, d = ifd0(b)
    w, h = val_at(t, d, 256)[0], val_at(t, d, 257)[0]
    if val_at(t, d, 277)[0] != 1:
        raise ValueError('渲染件 SamplesPerPixel≠1')
    rows = val_at(t, d, 278)
    rowbytes = val_at(t, d, 279)[0] // rows[0]
    off = val_at(t, d, 273)[0]
    return [list(struct.unpack_from('%s%dH' % (t.e, w), b, off + y * rowbytes)) for y in range(h)]


def patch_tag_value(data, tag, num):
    """把一个计数为 1 的标签值等长改写。
    ⚠ 内联（byteCount≤4，如 WhiteLevel=SHORT）时值场本身就是数据，地址是**表项+8**；
    外链（如 BlackLevel=URATIONAL 8 字节）时 `val` 才是偏移，且只改分子、分母留着不动。
    两种情形搞混会得到"改写没生效"或"改坏邻居"两种假读数（本臂第一版就是按偏移改 SHORT 改错的）。"""
    e = '<' if data[:2] == b'II' else '>'
    off = struct.unpack_from(e + 'I', data, 4)[0]
    n = struct.unpack_from(e + 'H', data, off)[0]
    out = bytearray(data)
    for i in range(n):
        eo = off + 2 + 12 * i
        t, ty, cnt, val = struct.unpack_from(e + 'HHII', data, eo)
        if t != tag:
            continue
        sz = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 8, 10: 8, 11: 4, 12: 8}[ty] * cnt
        if cnt != 1:
            raise ValueError('tag 0x%04X 计数=%d，本臂只改单值标签' % (tag, cnt))
        if ty in (3, 4, 9, 11) and sz <= 4:
            struct.pack_into(e + {3: 'H', 4: 'I', 9: 'i', 11: 'i'}[ty], out, eo + 8, num)
        elif ty in (5, 10):
            struct.pack_into(e + 'I', out, val, num)      # 分子；分母保持原值
        else:
            raise ValueError('tag 0x%04X type=%d cnt=%d 不是本臂认得的单值形式' % (tag, ty, cnt))
        return bytes(out)
    raise ValueError('产物 IFD0 没有 tag 0x%04X' % tag)


def make_old_decl(data, span):
    """等长把两个标签改回旧口径（BlackLevel→0，WhiteLevel→旧跨度），其余一字节不动。"""
    out = patch_tag_value(data, TAG_BLACK, 0)
    out = patch_tag_value(out, TAG_WHITE, span)
    if len(out) != len(data):
        raise ValueError('改长度了（不该）')
    return out


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    dngP, srcP = argv[1], argv[2]
    heavy = '--render-ab' in argv
    dv = outdir = None
    if heavy:
        i = argv.index('--render-ab')
        dv, outdir = argv[i + 1], argv[i + 2]
    for p in (dngP, srcP):
        if not os.path.exists(p):
            print('LEVELS-SKIP 缺件 %s ⇒ 记未判，不记绿' % p)
            return 2
    if heavy and not os.path.exists(dv):
        print('LEVELS-SKIP 缺 dng_validate ⇒ 记未判，不记绿')
        return 2

    bad = []
    try:
        src_black = rd.iiq_black_level(srcP)
    except (ValueError, struct.error) as exc:
        return fail('读源 0x021D 失败: %s' % exc)
    data = open(dngP, 'rb').read()
    try:
        t, d = ifd0(data)
        black = val_at(t, d, TAG_BLACK)[0]
        white = val_at(t, d, TAG_WHITE)[0]
        dim = val_at(t, d, TAG_BLDIM)[:2] if TAG_BLDIM in d else None
        act = val_at(t, d, TAG_ACTIVE) if TAG_ACTIVE in d else None
    except (ValueError, struct.error, KeyError) as exc:
        return fail('解产物标签失败: %s' % exc)
    try:
        st = plane_stats(dngP, act)
    except Exception as exc:
        return fail('rawpy 读产物平面失败: %s' % exc)
    try:
        import rawpy
        with rawpy.imread(srcP) as sim:
            src_white = int(sim.white_level)
            src_black_rp = list(sim.black_level_per_channel)
    except Exception as exc:
        return fail('rawpy 读源平面失败: %s' % exc)
    span = white - black

    # A1 源声明的黑电平 == 产物 BlackLevel
    if black != src_black:
        bad.append('A1 产物 BlackLevel=%d ≠ 源 MOBJ 0x021D=%d ⇒ 声明与源不符（或读手走错了层）' % (black, src_black))
    # A2 重复维必须是 1×1
    if dim not in ([1, 1], (1, 1), None):
        bad.append('A2 BlackLevelRepeatDim=%s 不是 1×1 ⇒ 另三个通道会被读者按该矩阵取值解释' % (dim,))
    # A3 新声明必须容得下实测最大样本
    if st['mx'] > white:
        bad.append('A3 实测最大样本=%d > WhiteLevel=%d ⇒ 新声明仍在削高光' % (st['mx'], white))
    # A5 跨度必须等于"修补前那个 WhiteLevel"（由第二份 LibRaw 读源给出）
    if span != src_white:
        bad.append('A5 跨度 WhiteLevel−BlackLevel=%d ≠ rawpy 读源的 white_level=%d ⇒ 归一化分母被动过'
                   '（本修补应当只补黑电平）' % (span, src_white))
    # "旧声明会削掉东西"这一条由素材决定 ⇒ 只登记读数，不硬判（素材没到过高光就是未判定）
    clip_note = ('旧跨度=%d 实测最大=%d ⇒ 本件样本没越过旧上限，"削高光"一项未判定' % (span, st['mx'])
                 if st['mx'] <= span else
                 '旧跨度=%d 实测最大=%d ⇒ 旧声明确实会削掉 %d 这一段' % (span, st['mx'], st['mx'] - span))

    print('LEVELS-READ 源 0x021D=%d（rawpy 报 black=%s white=%d）· 产物 BlackLevel=%d WhiteLevel=%d '
          'RepeatDim=%s ActiveArea=%s'
          % (src_black, src_black_rp, src_white, black, white, dim, act))
    print('LEVELS-READ 产物平面(ActiveArea 内) n=%d min=%d p0.1=%d 中位=%d p99.9=%d max=%d'
          ' ⇒ 旧跨度=%d 新跨度=%d' % (st['n'], st['mn'], st['p01'], st['med'], st['p999'], st['mx'], span, span))
    print('LEVELS-READ %s' % clip_note)
    print('LEVELS-READ 若仍按旧声明(0/%d)渲染：真黑被抬到 %d/65535，且样本>%d 的全部饱和'
          % (src_white, int(round(src_black * 65535.0 / src_white)), src_white))
    if bad:
        for m in bad:
            print('LEVELS-FAIL ' + m)
        return 1
    print('LEVELS-OK A1/A2/A3/A5：黑电平取到源 0x021D、RepeatDim 1×1、新口径不削实测高光、跨度与修补前逐数相同')

    if not heavy:
        print('LEVELS-SKIP 未跑渲染 A/B（要 --render-ab ⇒ 记未判，不记绿）')
        return 0

    os.makedirs(outdir, exist_ok=True)
    try:
        old_bytes = make_old_decl(data, span)
        tm, dm = ifd0(old_bytes)
        mb, mw = val_at(tm, dm, TAG_BLACK)[0], val_at(tm, dm, TAG_WHITE)[0]
    except ValueError as exc:
        return fail('改写旧声明失败: %s' % exc)
    ndiff = sum(1 for x, y in zip(data, old_bytes) if x != y)
    if (mb, mw) != (0, span):
        return fail('变异件读回 BlackLevel=%d WhiteLevel=%d 不是要的 (0,%d) ⇒ 改写没生效，两臂会是同一张图'
                    % (mb, mw, span))
    if ndiff > 8:
        return fail('变异件与原件有 %d 个字节不同（两处单值改写最多 6 个）⇒ 不知改了什么，两臂不可比' % ndiff)
    print('LEVELS-RENDER 变异件回读 BlackLevel=%d WhiteLevel=%d（与原件不同的字节数=%d，只许动这两处）'
          % (mb, mw, ndiff))
    oldP = os.path.join(outdir, 'olddecl.dng')
    open(oldP, 'wb').write(old_bytes)
    try:
        A = render(dv, dngP, outdir, 'lv_new')      # 新声明
        B = render(dv, oldP, outdir, 'lv_old')      # 等长改回旧声明
    except ValueError as exc:
        return fail('渲染臂失败: %s' % exc)
    if (len(A), len(A[0])) != (len(B), len(B[0])):
        return fail('两臂渲染尺寸不同 %dx%d vs %dx%d ⇒ 无从逐点比'
                    % (len(A[0]), len(A), len(B[0]), len(B[1])))

    def flat(img):
        return [v for row in img for v in row]

    fa, fb = flat(A), flat(B)
    sa, sb = sorted(fa), sorted(fb)
    sat_a, sat_b = sum(1 for v in fa if v == 65535), sum(1 for v in fb if v == 65535)
    lo_a, lo_b = sa[int(0.001 * len(sa))], sb[int(0.001 * len(sb))]
    med_a, med_b = statistics.median(sa), statistics.median(sb)
    # 读者自己的公式：渲染值 = (v − BlackLevel)/(WhiteLevel − BlackLevel) ×65535
    # ⇒ 两臂之差在**任何**未饱和样本上都应等于 black/span ×65535（黑电平没被扣的那一截）
    exp_delta = int(round(black * 65535.0 / span))
    print('LEVELS-RENDER 尺寸=%dx%d p0.1: 新=%d 旧=%d 差=%d · 中位: 新=%d 旧=%d 差=%d（预测抬升≈%d）'
          % (len(A[0]), len(A), lo_a, lo_b, lo_b - lo_a, med_a, med_b, med_b - med_a, exp_delta))
    print('LEVELS-RENDER 65535 饱和像素: 新=%d 旧=%d' % (sat_a, sat_b))
    bad = []
    if not (0.85 * exp_delta <= lo_b - lo_a <= 1.15 * exp_delta):
        bad.append('A6 低分位差 %d 不在预测 %d 的 ±15%% 内 ⇒ 读者没按声明的黑电平兑现' % (lo_b - lo_a, exp_delta))
    if not (0.85 * exp_delta <= med_b - med_a <= 1.15 * exp_delta):
        bad.append('A8 中位差 %d 不在预测 %d 的 ±15%% 内 ⇒ 影响不止黑场这一条不成立' % (med_b - med_a, exp_delta))
    if sat_b < sat_a:
        bad.append('A7 旧臂饱和 %d < 新臂 %d ⇒ 方向反了（旧跨度更小，不可能更少饱和）' % (sat_b, sat_a))
    if bad:
        for m in bad:
            print('LEVELS-FAIL ' + m)
        return 1
    print('LEVELS-RENDER OK Adobe 自己给出的读数：旧声明把整幅抬 %d/65535（饱和 新=%d 旧=%d，差 %d）'
          % (lo_b - lo_a, sat_a, sat_b, sat_b - sat_a))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
