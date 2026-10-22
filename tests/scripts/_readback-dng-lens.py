#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_readback-dng-lens.py —— 镜头 opcode 的**第二个独立实现**（第 77 条 ⑱ 组用）

它不 import 写侧（dngtool）的任何逻辑，只做四件事：
  1) 自己解 DNG 的 OpcodeList3(51022) **大端**字节流，取回 FixVignetteRadial(3) /
     WarpRectilinear(1) / WarpRectilinear2(14) 的载荷与 flags 字；
  2) 自己解**源件**并按容器分派：ARW 走图像 SubIFD 的 0x7032/0x7035/0x7037（首字 = 样本数，
     载荷是 int16 串）；Fuji RAF 走 FujiIFD 的 0xf00b/0xf00f/0xf010（SRATIONAL 串）；
     Panasonic RW2/RWL 走 IFD0 的 0x0119（UNDEFINED[32] = 16×int16）。三者都按 darktable
     的公开约定换算成增益/半径比例（Fuji: 100/V、1+D/100、1+ca；Panasonic: a/b/c 与
     scale 的三次多项式 + 两点不动点反解；Sony: 2^(v/8192-1) 等）；
  3) 用产物里那组系数**逐结点回算**，与 2) 的表格值对撞（容差写死在本文件，不读被检方常数）；
  4) 检查跨读者的形状：ID=14 必须 optional、其**紧邻**的下一条必须是 ID=1（规范 p.105 的跳过规则
     只在相邻时生效），ID=3 与 ID=1 必须 required 且版本 = 1.3.0.0，每类镜头支最多一支；
     v1 支只按它的四项偶次模型 (r^0,r^2,r^4,r^6) 对源表回算残差 —— 载荷里奇次位本就不存在
     （PutData 只取 fData[0,2,4,6]），所以"奇次位为 0"不能当判据，只有残差能识破
     "把 v2 全阶系数错抄进 v1"。
     缺席分两种：**写侧点名过的跳过**（`--stderr` 里能查到 lens-skipped=<支>）放行，
     **静默缺席**判红。

用法：
  py _readback-dng-lens.py <dng> <源 arw>                    # 正常判据
  py _readback-dng-lens.py <dng> <源 arw> --expect-absent     # 反控：不该有任何镜头 opcode
  py _readback-dng-lens.py <dng> <源 arw> [--stderr <写侧 stderr 落盘文件>]
        # 正常判据 + 允许"写侧点名过的跳过"合法缺席（只有静默缺席才判红）；造坏副本用
        # _mutate-dng-lens.py（十类变异各打一条判据）
  py _readback-dng-lens.py --render <基准.raw> <施加.raw> <WxH> --source <源 arw>
        # 像素臂：rgb48le 裸流（Adobe dng_validate -3 渲染后由 ffmpeg 转），比**环带**增益
退出码 0 = 判据成立；1 = 判红（点名原因）；2 = 用法错。
"""
import os
import struct
import sys

VIG_DIV = 8192.0          # 2^13 —— 暗角是透射指数
DIST_DIV = 16384.0        # 2^14 —— 畸变 = 半径比例
CA_DIV = 2097152.0        # 2^21 —— 横向色差 = 半径比例
KNOT_TOL_VIG = 0.0025     # 增益绝对残差上界（本机实测 0.000319 量级）
KNOT_TOL_WARP = 0.0006    # 半径比例残差上界（× 本机角半径 3605 px ≈ 2.2 px）
KNOT_TOL_V1 = KNOT_TOL_WARP * 4      # v1 只收偶次项，允许比 v2 松
RENDER_LO, RENDER_HI, RENDER_TOL = 0.90, 0.98, 0.05
TYPE_SIZES = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 8: 4, 9: 8, 10: 8, 11: 4, 12: 8}
TAG_OPC3 = 51022
ID_WARP1, ID_VIG, ID_WARP2 = 1, 3, 14
LENS_IDS = (ID_WARP1, ID_VIG, ID_WARP2)
VER_1_3, VER_1_6 = 0x01030000, 0x01060000


def fail(msg):
    print('LENS-READ FAIL %s' % msg)
    return 1


class Tiff:
    """小端 TIFF 走查。entry 一律 (type, count, value字段, entry偏移)；值字段必须按 I 取——
    按 H 只拿到低 16 位 ⇒ 大偏移被静默截成小数字（本仓实测踩过，要靠第二个读者对撞才发现）。"""

    def __init__(self, data, endian='<'):
        self.b = data
        self.e = endian
        want = 0x4949 if endian == '<' else 0x4D4D
        if struct.unpack_from(endian + 'H', data, 0)[0] != want:
            raise ValueError('字节序标记不符')

    def u32(self, off):
        return struct.unpack_from(self.e + 'I', self.b, off)[0]

    def ifd(self, off):
        n = struct.unpack_from(self.e + 'H', self.b, off)[0]
        if n == 0 or n > 1024:
            raise ValueError('IFD 计数异常 %d @%d' % (n, off))
        out = {}
        for i in range(n):
            e = off + 2 + 12 * i
            tag, ty, cnt, val = struct.unpack_from(self.e + 'HHII', self.b, e)
            out[tag] = (ty, cnt, val, e)
        return out

    def payload(self, ent):
        ty, cnt, val, e = ent
        sz = TYPE_SIZES.get(ty, 4) * cnt
        if sz <= 4:
            return struct.pack(self.e + 'I', val)[:sz]
        return self.b[val:val + sz]

    def int16_seq(self, ifd_base, tag):
        d = self.ifd(ifd_base)
        if tag not in d:
            return None
        ty, cnt, val, e = d[tag]
        if ty != 8:
            raise ValueError('tag 0x%X type=%d 不是 8(LONG) ⇒ 这三张参数表的形状不是这样' % (tag, ty))
        raw = self.payload(d[tag])
        return list(struct.unpack('<%dh' % (len(raw) // 2), raw))

    def sub_ifds(self):
        d0 = self.ifd(self.u32(4))
        if 0x14A not in d0:
            return []
        ty, cnt, val, e = d0[0x14A]
        if ty != 4 or cnt < 1:
            raise ValueError('0x14A 形状异常 type=%d count=%d' % (ty, cnt))
        if cnt == 1:
            # TIFF: 值 ≤4 字节时**内联**，那个字段本身就是偏移；再解引用一次就读进缩略图 IFD
            return [val]
        raw = self.payload(d0[0x14A])
        return [struct.unpack_from('<I', raw, 4 * i)[0] for i in range(len(raw) // 4)]


def dng_norm(t):
    """产物自身几何 → 写侧归一化基数的独立复算（不读 dngtool 的日志/常数）。

    写侧口径 = 可见裁切区半对角 / 读者按有效区最远角算出的半径。
    DNG 里对应：ImageWidth/ImageLength = 原始幅面，**ActiveArea(0xC68D) = 有效区**。
    ⚠ 不能用 DefaultCrop(0xC61F/0xC620)：2026-10-23 起产物把厂商内嵌裁剪搬进
    DefaultCrop（Canon/Fuji/Panasonic/Leica/Pentax），它比 ActiveArea 小且带原点；
    而 opcode 的坐标基按 DNG 规范是 ActiveArea，写侧 `ComputeLensNorm` 用的也是
    S.width/height（= ActiveArea），两者必须同基才对得上。"""
    d0 = t.ifd(t.u32(4))

    def nums(tag, n):
        ent = d0.get(tag)
        if ent is None:
            raise ValueError('DNG 缺 0x%04X，无法复算归一化' % tag)
        ty, cnt, val, _ = ent
        if cnt != n or ty not in (3, 4, 5, 10):
            raise ValueError('0x%04X 形状异常 type=%d count=%d' % (tag, ty, cnt))
        raw = t.payload(ent)
        if ty in (3, 4):
            return list(struct.unpack('<%d%s' % (n, 'H' if ty == 3 else 'I'), raw))
        # DefaultCrop* 在本机产物里是 SRATIONAL（type 5）⇒ 逐对取 num/den
        return [num / den if den else 0.0
                for num, den in struct.iter_unpack('<ii', raw)]

    raw_w, raw_h = nums(256, 1)[0], nums(257, 1)[0]
    top, left, bottom, right = nums(0xC68D, 4)
    cw, ch = float(right - left), float(bottom - top)
    cx, cy = left + cw / 2.0, top + ch / 2.0
    maxr = (max(cx, raw_w - cx) ** 2 + max(cy, raw_h - cy) ** 2) ** 0.5
    half = ((cw / 2.0) ** 2 + (ch / 2.0) ** 2) ** 0.5
    return dict(xScale=(half / maxr if maxr > 1e-9 else 1.0),
                centerH=cx / raw_w, centerV=cy / raw_h)


def _raw_ifd(b, off, e):
    n = struct.unpack_from(e + 'H', b, off)[0]
    if n == 0 or n > 1024:
        raise ValueError('IFD 计数异常 %d @%d' % (n, off))
    out = {}
    for i in range(n):
        ep = off + 2 + 12 * i
        tag, ty, cnt, val = struct.unpack_from(e + 'HHII', b, ep)
        out[tag] = (ty, cnt, val, ep)
    return out


def _srational_seq(b, ifd_off, tiff_base, e, tag):
    """Fuji 的 SRATIONAL 串。⚠ 值偏移**相对 Fuji TIFF base**（LibRaw tiff.cpp 同读法）。"""
    d = _raw_ifd(b, ifd_off, e)
    if tag not in d:
        return None
    ty, cnt, val, _ = d[tag]
    if ty != 10 or cnt == 0 or cnt > 256:
        raise ValueError('tag 0x%X 形状异常 type=%d count=%d' % (tag, ty, cnt))
    if cnt * 8 <= 4:
        raise ValueError('tag 0x%X 的载荷内联了，与已证布局不符' % tag)
    at = tiff_base + val
    if at + 8 * cnt > len(b):
        raise ValueError('tag 0x%X 载荷越界' % tag)
    return [num / den if den else 0.0
            for num, den in struct.iter_unpack(e + 'ii', b[at:at + 8 * cnt])]


def source_expect_fuji(b, norm):
    """RAF：三表 count 23/31/23（nc=11）或 19/29/19（nc=9），与写侧同一布局。
    换算（darktable exif.cc:1408-1493 + lens.cc:2099-2133）：暗角 g=100/V（V 是透射百分数）、
    畸变 1+D/100、CA 是加性比例 1+ca、CropMode 2/4 ⇒ 1.25× 裁切。"""
    base = e = None
    for off in (0x64, 0x80):
        if off + 4 > len(b):
            continue
        cand = struct.unpack_from('>I', b, off)[0]
        if cand + 8 > len(b):
            continue
        magic = struct.unpack_from('<H', b, cand)[0]
        if magic not in (0x4949, 0x4D4D):
            continue
        e = '<' if magic == 0x4949 else '>'
        rel = struct.unpack_from(e + 'I', b, cand + 4)[0]
        if rel < 8 or cand + rel + 2 > len(b):
            continue
        base = cand
        break
    if base is None:
        raise ValueError('RAF 头 0x64/0x80 都指不到可解的 FujiIFD')
    ifd0 = base + rel
    d0 = _raw_ifd(b, ifd0, e)
    sub = ifd0
    if 0xF000 in d0:
        ty, cnt, val, _ = d0[0xF000]
        if val >= 8:
            sub = base + val                    # 相对 TIFF base，不是文件绝对偏移
    dist = _srational_seq(b, sub, base, e, 0xF00B)
    ca = _srational_seq(b, sub, base, e, 0xF00F)
    vig = _srational_seq(b, sub, base, e, 0xF010)
    if not dist or not ca or not vig:
        raise ValueError('FujiIFD 缺 0xf00b/0xf00f/0xf010 三表')
    nd, ncc, nv = len(dist), len(ca), len(vig)
    if (nd, ncc, nv) == (23, 31, 23):
        nc, distOff, vigOff, caROff, caBOff, caZero0 = 11, 12, 12, 10, 20, True
    elif (nd, ncc, nv) == (19, 29, 19):
        nc, distOff, vigOff, caROff, caBOff, caZero0 = 9, 10, 10, 10, 19, False
    else:
        raise ValueError('Fuji 三表 count %d/%d/%d 不在已证布局' % (nd, ncc, nv))
    cm = 0
    for src in (d0, _raw_ifd(b, sub, e)):
        if 0x104D in src:
            ty, cnt, val, _ = src[0x104D]
            if ty in (3, 8) and cnt >= 1:
                cm = val & 0xFFFF
            break
    cropf = 1.25 if cm in (2, 4) else 1.0
    f = cropf * norm['xScale']
    knots = [dist[1 + i] * f for i in range(nc)]
    gains = [100.0 / vig[vigOff + i] for i in range(nc)]
    exp = dict(knots=knots,
               gains=gains,
               # 写侧拟合时跳过 V<=1 的结点（100/V-1 无意义）⇒ 读侧必须用同一把尺
               vig_knots=[knots[i] for i in range(nc) if vig[vigOff + i] > 1.0],
               vig_gains=[gains[i] for i in range(nc) if vig[vigOff + i] > 1.0],
               scales=[1.0 + dist[distOff + i] / 100.0 for i in range(nc)],
               ca_r=[1.0 if (caZero0 and i == 0) else 1.0 + ca[caROff + i] for i in range(nc)],
               ca_b=[1.0 if (caZero0 and i == 0) else 1.0 + ca[caBOff + i] for i in range(nc)],
               sw={'cropmode': cm}, nc=nc, nd=nc)
    return exp


def source_expect_panasonic(b, norm):
    """RW2/RWL：IFD0 的 0x0119 UNDEFINED[32] = 16×int16。
    换算（darktable exif.cc:1541-1596 + lens.cc:2445-2493）：开关 word7&0x0f==1；
    scale=1/(1+word5/32768)，a=word8/32768，b=word4/32768，c=word11/32768；
    Ru=Rd+scale*(a·Rd³+b·Rd⁵+c·Rd⁷)，两次不动点反解 Rd/Ru。"""
    off0 = struct.unpack_from('<I', b, 4)[0]
    if off0 < 8 or off0 > 0x00FFFFFF:
        raise ValueError('RW2 IFD0 偏移不可信 %d' % off0)
    d0 = _raw_ifd(b, off0, '<')
    if 0x0119 not in d0:
        raise ValueError('IFD0 无 0x0119 DistortionInfo')
    ty, cnt, val, _ = d0[0x0119]
    if ty != 7 or cnt != 32:
        raise ValueError('0x0119 不是 UNDEFINED[32]（type=%d count=%d）' % (ty, cnt))
    if val + 32 > len(b):
        raise ValueError('0x0119 载荷越界')
    v = list(struct.unpack('<16h', b[val:val + 32]))
    on = (v[7] & 0x0F) == 1
    n = 64
    knots, dr = [], []
    scale = 1.0 / (1.0 + v[5] / 32768.0) if on else 1.0
    a, bb, c = v[8] / 32768.0, v[4] / 32768.0, v[11] / 32768.0
    for i in range(n + 1):
        r = i / float(n)
        rd = r
        if on:
            for _ in range(2):
                rd2 = rd * rd
                f = 1.0 + scale * (a * rd2 + bb * rd2 * rd2 + c * rd2 * rd2 * rd2)
                rd = r / f if f > 1e-6 else r
        knots.append(r * norm['xScale'])
        dr.append(rd / r if r > 0.0 else 1.0)
    return dict(knots=knots, gains=[], scales=dr, ca_r=None, ca_b=None,
                sw={'dist': on}, nc=0, nd=n + 1)


def source_expect(src_path, skip_ca=False, dng_bytes=None):
    """按容器分派。Fuji/Panasonic 的归一化基数从产物几何独立复算（norm）。"""
    b = open(src_path, 'rb').read()
    if b[:15] == b'FUJIFILMCCD-RAW':
        if dng_bytes is None:
            raise ValueError('Fuji 源需要产物字节来复算归一化基数')
        return source_expect_fuji(b, dng_norm(Tiff(dng_bytes, '<')))
    if b[:4] == b'IIU\x00':
        if dng_bytes is None:
            raise ValueError('Panasonic 源需要产物字节来复算归一化基数')
        return source_expect_panasonic(b, dng_norm(Tiff(dng_bytes, '<')))
    return source_expect_sony(src_path, skip_ca)


def source_expect_sony(arw_path, skip_ca=False):
    """源侧：按公开约定把三张表变成"应当被回算出来的值"。

    ⚠ `skip_ca`（写侧按机内开关明确跳过 CA）与 `sw['ca'] is False` 二者任一成立 ⇒ 本函数**不得**
      再期望 R/B 带 CA 因子。写侧的规矩是"只有明确 0 才跳过"（读不出 ≠ 关，darktable 也根本不查它），
      读侧必须用同一把尺，否则换一件开关为 0 的素材就是假红 —— 这与 10-19 修过的"开关按 type=8 去读
      全被判成关"是同一类两侧不同尺的错。"""
    t = Tiff(open(arw_path, 'rb').read(), '<')
    for off in t.sub_ifds():
        try:
            vig = t.int16_seq(off, 0x7032)
            dist = t.int16_seq(off, 0x7037)
            ca = t.int16_seq(off, 0x7035)
        except ValueError:
            continue
        if not vig or not dist:
            continue
        nv, nd = vig[0], dist[0]
        if nv < 5 or nd < 3:
            continue                       # 五项拟合要 ≥5 个结点；样本太少没有自由度
        if len(dist) < 1 + nd:
            raise ValueError('畸变表声明 %d 项但载荷只有 %d 个 int16' % (nd, len(dist)))
        knots = [((i + 0.5) / (nv - 1)) for i in range(nv)]     # dt lens.cc:2225 原式
        gains = []
        for i in range(nv):
            tr = 2.0 ** (vig[1 + i] / VIG_DIV - 1.0)
            gains.append(2.0 ** (tr - 0.5))   # dt 存的是它的倒数；DNG 的 g 是乘增益
        scales = [1.0 + dist[1 + i] / DIST_DIV for i in range(nd)]
        d = t.ifd(off)
        sw = {}
        for tag, nm in ((0x7031, 'vig'), (0x7036, 'dist'), (0x7034, 'ca')):
            e = d.get(tag)
            # 开关的真实形状：type=3(SHORT) count=1，值内联在值字段低 16 位（7M2 = 257/1/1）
            # ⇒ 三态：None 读不出（**不当成"关"**，darktable 也根本不查它）/ False 明确关 / True 开
            if e is None or e[0] not in (3, 8) or e[1] < 1:
                sw[nm] = None
            else:
                sw[nm] = (e[2] & 0xFFFF) != 0
        ca_r = ca_b = None
        if (ca and ca[0] == 2 * nd and len(ca) >= 1 + 2 * nd and
                sw['ca'] is not False and not skip_ca):
            ca_r = [1.0 + ca[1 + i] / CA_DIV for i in range(nd)]
            ca_b = [1.0 + ca[1 + nd + i] / CA_DIV for i in range(nd)]
        return dict(knots=knots, gains=gains, scales=scales, ca_r=ca_r, ca_b=ca_b,
                    sw=sw, nc=nv, nd=nd)
    raise ValueError('源 ARW 的图像 SubIFD 里没有可用的 0x7032/0x7037')


def parse_opcodes(blob):
    o = 0
    n = struct.unpack_from('>I', blob, o)[0]
    o += 4
    ops = []
    for i in range(n):
        if o + 16 > len(blob):
            raise ValueError('opcode #%d 头越界' % i)
        code, minv, flags = struct.unpack_from('>III', blob, o)
        o += 12
        size = struct.unpack_from('>I', blob, o)[0]
        o += 4
        if o + size > len(blob):
            raise ValueError('opcode #%d 载荷越界（需 %d 只剩 %d）' % (i, size, len(blob) - o))
        ops.append(dict(index=i, code=code, minVersion=minv, flags=flags, size=size,
                        body=blob[o:o + size], start=o))
        o += size
    return ops, o


def vig_coeffs(body):
    if len(body) != 56:
        raise ValueError('FixVignetteRadial 载荷 %d 字节，应为 5×double + 光心 2×double = 56' % len(body))
    k = list(struct.unpack_from('>5d', body, 0))
    cx, cy = struct.unpack_from('>2d', body, 40)
    return k, cx, cy


def warp_parse(body, is_v2):
    if len(body) < 4:
        raise ValueError('warp 载荷不足 4 字节')
    planes = struct.unpack_from('>I', body, 0)[0]
    if planes != 3:
        raise ValueError('warp 载荷 planes=%d，本判据只接 3（去马赛克后三平面）' % planes)
    per = 19 if is_v2 else 6        # v2: 15 径向 + 2 切向 + 2 有效半径；v1: 4 径向(偶次) + 2 切向
    tail = 20 if is_v2 else 16      # v2 尾部 = 光心 16 + useReciprocal 4
    need = 4 + per * 8 * planes + tail
    if len(body) < need:
        raise ValueError('warp 载荷 %d 字节 < 需要 %d（%s 支）' % (len(body), need, 'v2' if is_v2 else 'v1'))
    out = []
    for p in range(planes):
        row = list(struct.unpack_from('>%dd' % per, body, 4 + p * per * 8))
        if is_v2:
            out.append(dict(radial=row[:15], tan=row[15:17], valid=row[17:19]))
        else:
            even = {0: row[0], 2: row[1], 4: row[2], 6: row[3]}
            out.append(dict(radial=[even.get(q, 0.0) for q in range(15)], tan=row[4:6], valid=[0.0, 1.0]))
    cx, cy = struct.unpack_from('>2d', body, 4 + per * 8 * planes)
    recip = struct.unpack_from('>I', body, 4 + per * 8 * planes + 16)[0] if is_v2 else 0
    return out, cx, cy, recip


def eval_poly(coef, r):
    return sum(c * (r ** q) for q, c in enumerate(coef))


def render_ratio(argv):
    """--render <基准.raw> <施加.raw> <WxH> --source <源 arw>：证明读者真把增益施加了。

    ⚠ 测量点刻意选在 **r∈[0.90,0.98) 的环带**。本机同一对渲染实测：环带比 = 1.0946/1.0943/1.0954
    （对源表角部增益 1.1117 差 1.5%），而**最外 32 像素的角块**比 = 1.4536/1.4785/1.5259 ——
    角块与环带为何背离**本轮未判定**（界外采样后的边界处理？还是最外 24 像素内径向斜率太陡？），
    所以判据只用环带，角块不参与判定。
    三条判据都要成立：① 三通道环带比都 >1.02（方向）；② 与源表角部增益相差 ≤5%（量级不离谱）；
    ③ 抽样里确实有像素不同（否则"施加"没发生）。
    ⚠ 这一臂**不锁幅度精确性** —— 系数与源表的等价由逐结点回算那条锁（实测残差 3.2e-4），
    本臂只管"读者真的把增益算进了像素"。
    容差为什么从 10% 收到 5%：10% 时接受区间是 [1.02, 1.223]（预测 1.1117 上浮 10%），"只施加了
    2%"也算过 ⇒ 那个区间形同虚设。本机实测 1.0946/1.0943/1.0954 对预测 1.1117 差 1.5%，
    收到 5% 仍有 3.3 倍余量；不给 2% 是因为 warp 会把整幅径向分布挪动，挪动量随场景的径向斜率变
    ——同一对渲染把测量带挪到最外 32 像素的角块就给出 1.4536/1.4785/1.5259，那个背离本轮未判定
    （见上一段），所以本臂只取环带、且只判"量级不离谱"。"""
    import array
    if len(argv) < 5:
        return 2
    base_p, lens_p = argv[2], argv[3]
    try:
        W, H = (int(x) for x in argv[4].split('x'))
    except ValueError:
        return fail('--render 的尺寸参数必须是 WxH，实得 %s' % argv[4])
    want = None
    if '--source' in argv:
        want = source_expect(argv[argv.index('--source') + 1])['gains'][-1]
    for q in (base_p, lens_p):
        if not os.path.exists(q):
            return fail('渲染输入文件不存在: %s ⇒ 这条臂不能空跑' % q)
        sz = os.path.getsize(q)
        if sz != W * H * 6:
            return fail('%s 大小 %d 与 %dx%d×3×2=%d 不符 ⇒ 不是 rgb48le' % (q, sz, W, H, W * H * 6))
    step = 8

    def band(path):
        tot = [0, 0, 0]
        cnt = 0
        rowbytes = W * 6
        with open(path, 'rb') as fp:
            for y in range(0, H, step):
                fp.seek(y * rowbytes)
                buf = fp.read(rowbytes)
                if len(buf) != rowbytes:
                    raise ValueError('%s 第 %d 行读不满' % (path, y))
                a = array.array('H')
                a.frombytes(buf)
                cy = (y + 0.5 - H / 2.0) / (H / 2.0)
                for x in range(0, W, step):
                    cx = (x + 0.5 - W / 2.0) / (W / 2.0)
                    r = (cx * cx + cy * cy) ** 0.5 / (2 ** 0.5)
                    if RENDER_LO <= r < RENDER_HI:
                        for ch in range(3):
                            tot[ch] += a[x * 3 + ch]
                        cnt += 1
        if cnt == 0:
            raise ValueError('%s 在 r∈[%.2f,%.2f) 内一个采样点都没有 ⇒ 尺寸或格式不对'
                             % (path, RENDER_LO, RENDER_HI))
        return [t / cnt for t in tot], cnt

    try:
        rb, nband = band(base_p)
        rl, _ = band(lens_p)
    except (ValueError, OverflowError) as exc:
        return fail('环带测量失败: %s' % exc)
    lift = [b / max(a, 1e-9) for a, b in zip(rb, rl)]

    n_samp = 20000
    diff = 0
    with open(base_p, 'rb') as f1, open(lens_p, 'rb') as f2:
        for k in range(n_samp):
            pos = ((k * 7919 + 104729) % (W * H)) * 6     # 确定性伪随机，铺满整幅
            f1.seek(pos)
            f2.seek(pos)
            if f1.read(6) != f2.read(6):
                diff += 1
    if diff == 0:
        return fail('两支渲染抽样 %d 点逐字节相同 ⇒ 读者没有施加任何东西' % n_samp)
    if not all(v > 1.02 for v in lift):
        return fail('环带 r∈[%.2f,%.2f) 提升=%s（三通道都须 >1.02）基准=%s 施加=%s'
                    % (RENDER_LO, RENDER_HI, ['%.4f' % v for v in lift],
                       ['%.1f' % v for v in rb], ['%.1f' % v for v in rl]))
    if want is not None and not all(abs(v - want) / want <= RENDER_TOL for v in lift):
        return fail('环带提升 %s 与源表角部增益 %.4f 相差超过 %d%%'
                    % (['%.4f' % v for v in lift], want, int(RENDER_TOL * 100)))
    print('LENS-RENDER OK 环带 r∈[%.2f,%.2f) 提升=%s 预测=%.4f 抽样不同=%d/%d 环带样本=%d'
          % (RENDER_LO, RENDER_HI, ['%.4f' % v for v in lift],
             want if want is not None else -1.0, diff, n_samp, nband))
    return 0


def main(argv):
    if len(argv) < 3:
        return 2
    if argv[1] == '--render':
        return render_ratio(argv)
    dng, arw = argv[1], argv[2]
    want_absent = '--expect-absent' in argv
    # 写侧允许**合法跳过**某一支（机内开关明确为 0 / 偶次拟合不到水位 / 恒等 / 样本数不够）。
    # 缺席不是错，"静默缺席"才是 ⇒ 给了 --stderr 就要求：任何缺席支必须能在写侧 stderr 里找到点名。
    # ⚠ 本文件**不造变异件**：造件在 `_mutate-dng-lens.py`（判据与造件分家，否则判据走偏时
    #   造件跟着走偏，缺陷就永远触发不了判据 —— ⑱f 的空跑是同一类事故）。
    named = set()
    if '--stderr' in argv:
        st = open(argv[argv.index('--stderr') + 1], encoding='utf-8', errors='replace').read()
        for leg in ('vignette', 'distortion', 'chromatic-aberration', 'warp-legacy'):
            if ('lens-skipped=' + leg) in st:
                named.add(leg)

    data = open(dng, 'rb').read()
    try:
        t = Tiff(data, '<')
        scans = [t.ifd(t.u32(4))] + [t.ifd(off) for off in t.sub_ifds()]
    except (ValueError, struct.error, IndexError) as exc:
        return fail('解 DNG TIFF 失败: %s' % exc)

    found = []
    for bi, d in enumerate(scans):
        if TAG_OPC3 in d:
            ty, cnt, val, e = d[TAG_OPC3]
            if cnt > 4:
                found.append((bi, val, cnt, e))
    if want_absent:
        if not found:
            print('LENS-READ OK 镜头 opcode 缺席（反控臂）')
            return 0
        for bi, val, cnt, e in found:
            try:
                ops, _ = parse_opcodes(data[val:val + cnt])
            except ValueError as exc:
                return fail('IFD#%d 的 51022 解不开（%d B: %s）⇒ 反控要的是"没有镜头项"，坏列表也算在场'
                            % (bi, cnt, exc))
            lens = [o['code'] for o in ops if o['code'] in LENS_IDS]
            if lens:
                return fail('--expect-absent 但 IFD#%d 的列表里有镜头 opcode %s' % (bi, lens))
        print('LENS-READ OK 镜头 opcode 缺席（反控臂；列表里只有非镜头项）')
        return 0
    if len(found) != 1:
        return fail('期望恰好一处 51022，实得 %d 处 ⇒ 主判据失去落点' % len(found))

    bi, val, cnt, _entry = found[0]
    try:
        ops, end = parse_opcodes(data[val:val + cnt])
    except ValueError as exc:
        return fail('解列表失败: %s' % exc)
    if end != cnt:
        return fail('解到 %d 字节而标签声明 %d ⇒ 列表没落在边界上' % (end, cnt))

    try:
        exp = source_expect(arw, skip_ca=('chromatic-aberration' in named), dng_bytes=data)
    except (ValueError, struct.error) as exc:
        return fail('读源表失败: %s' % exc)

    ids = [o['code'] for o in ops]
    lens = [i for i in ids if i in LENS_IDS]
    dup = sorted({x for x in lens if lens.count(x) > 1})
    if dup:
        return fail('镜头支重复 %s ⇒ 同一校正会被读者施加两次（每类最多一支）' % dup)
    lines = ['IFD#%d:OpcodeList3(%d B) → %d 支 [%s]' % (bi, cnt, len(ops), ','.join(str(i) for i in ids))]
    if not ops:
        return fail('列表是空的 ⇒ 主判据需要真系数在场')

    # ── 暗角 ──
    vg = [o for o in ops if o['code'] == ID_VIG]
    if not vg:
        # Panasonic 没有暗角表（gains 为空）⇒ 缺席是源件的事实，不是丢项
        if exp['gains']:
            if 'vignette' not in named:
                return fail('暗角支缺席且写侧没点名（lens-skipped=vignette:...）⇒ 静默丢项')
            lines.append('暗角: 缺席（写侧已点名跳过）')
        else:
            lines.append('暗角: 缺席（源无暗角表）')
        cx = cy = None
    else:
        v = vg[0]
        if v['minVersion'] != VER_1_3:
            return fail('FixVignetteRadial minVersion=0x%08X 应为 1.3.0.0' % v['minVersion'])
        if v['flags'] & 1:
            return fail('FixVignetteRadial 被标 optional ⇒ 读者可整条跳过 = 白写'
                        '（注：required 是本通道的**政策**而非规范强制 —— 规范只说 flags 由写侧给定；'
                        'Adobe 自带样本也是 required）')
        try:
            k, cx, cy = vig_coeffs(v['body'])
        except (ValueError, struct.error) as exc:
            return fail('暗角载荷形状不对: %s' % exc)
        if not (0.0 <= cx <= 1.0 and 0.0 <= cy <= 1.0):
            return fail('暗角光心越界 (%.4f, %.4f)' % (cx, cy))
        zk = exp.get('vig_knots', exp['knots'])
        zg = exp.get('vig_gains', exp['gains'])
        if not zg:
            return fail('暗角支在场但源表没有 V>1 的结点 ⇒ 判据没有落点')
        worst = max(abs((1.0 + sum(k[q] * (r ** (2 * q + 2)) for q in range(5))) - g)
                    for r, g in zip(zk, zg))
        if worst > KNOT_TOL_VIG:
            return fail('暗角逐结点回算残差 %.6f > %.6f（表增益 ↔ 产物系数不等价）' % (worst, KNOT_TOL_VIG))
        lines.append('暗角: 残差=%.6f 角部回算=%.6f 表=%.6f k=%s'
                     % (worst, 1.0 + sum(k), exp['gains'][-1], ['%.5g' % x for x in k]))

    # ── warp 成对 + 相邻 + flags ──
    w2 = [o for o in ops if o['code'] == ID_WARP2]
    w1 = [o for o in ops if o['code'] == ID_WARP1]
    if w2:
        if not (w2[0]['flags'] & 1):
            return fail('WarpRectilinear2 没标 optional ⇒ 只认 1.3 的读者会因未实现而罢工')
        if w2[0]['minVersion'] != VER_1_6:
            return fail('WarpRectilinear2 minVersion=0x%08X 应为 1.6.0.0' % w2[0]['minVersion'])
        if w1 and w1[0]['index'] != w2[0]['index'] + 1:
            return fail('ID=1 不紧跟 ID=14 ⇒ 规范 p.105 的跳过规则不生效，两遍 warp 会被施加两次')
        try:
            pl, cx2, cy2, recip = warp_parse(w2[0]['body'], True)
        except (ValueError, struct.error) as exc:
            return fail('v2 载荷形状不对: %s' % exc)
        if recip:
            return fail('useReciprocal=1 与本换算方向不符（本处是 r_real = r_ideal * f）')
        if vg and (cx2, cy2) != (cx, cy):
            return fail('warp 与暗角的光心不一致: (%.4f,%.4f) vs (%.4f,%.4f)' % (cx2, cy2, cx, cy))
        m = min(exp['nd'], len(exp['knots']))
        worstp = []
        for p in range(3):
            if not (0.0 <= pl[p]['valid'][0] < pl[p]['valid'][1] <= 1.0):
                return fail('平面 %d 的有效半径区间不合法 %s' % (p, pl[p]['valid']))
            if p == 1 or exp['ca_r'] is None:
                want = exp['scales']
            else:
                want = [exp['scales'][i] * (exp['ca_r'][i] if p == 0 else exp['ca_b'][i])
                        for i in range(m)]
            worstp.append(max(abs(eval_poly(pl[p]['radial'], exp['knots'][i]) - want[i]) for i in range(m)))
        if max(worstp) > KNOT_TOL_WARP:
            return fail('warp 逐结点回算残差 %s > %.6f' % (['%.6f' % x for x in worstp], KNOT_TOL_WARP))
        lines.append('warp2: flags=%d 残差 R/G/B=%.6f/%.6f/%.6f ca=%s'
                     % (w2[0]['flags'], worstp[0], worstp[1], worstp[2],
                        'per-plane' if exp['ca_r'] is not None else 'off'))
    if w1:
        if w1[0]['flags'] & 1:
            return fail('WarpRectilinear（发给老读者的兜底支）标了 optional ⇒ 老读者可以整条跳过 = 白写')
        if w1[0]['minVersion'] != VER_1_3:
            return fail('WarpRectilinear minVersion=0x%08X 应为 1.3.0.0' % w1[0]['minVersion'])
        try:
            pl, _, _, _ = warp_parse(w1[0]['body'], False)
        except (ValueError, struct.error) as exc:
            return fail('v1 载荷形状不对: %s' % exc)
        # 这里原先还遍历"奇次位是否为 0"——但 v1 载荷只有 4 个偶次项，radial[1/3/5…] 是**本读手
        # 自己填的 0** ⇒ 那条断言永不可能触发，属于"看着有牙其实是装饰"。有效的判法是下面的残差：
        # 写侧若把 v2 的全阶系数错抄进 v1（或漏掉偶次拟合），四项复现不了源表畸变，立刻超差。
        m = min(exp['nd'], len(exp['knots']))
        worst = max(abs(eval_poly(pl[p]['radial'], exp['knots'][i]) - exp['scales'][i])
                    for p in range(3) for i in range(m))
        if worst > KNOT_TOL_V1:
            return fail('WarpRectilinear 对畸变表回算残差 %.6f > %.6f' % (worst, KNOT_TOL_V1))
        lines.append('warp1: flags=%d 偶次项残差=%.6f' % (w1[0]['flags'], worst))
    if not w2 and not w1:
        if 'distortion' not in named:
            return fail('畸变两支 warp 全缺席且写侧没点名（lens-skipped=distortion:...）⇒ 静默丢项')
        lines.append('warp: 缺席（写侧已点名跳过）')
    elif w2 and not w1 and 'warp-legacy' not in named:
        return fail('有 1.6 支（WarpRectilinear2）却无 1.3 兜底支，且写侧没点名 lens-skipped=warp-legacy:... '
                    '⇒ 只认 DNG1.3~1.5 的读者拿不到任何畸变校正')
    print('LENS-READ OK ' + ' | '.join(lines))
    print('LENS-READ 源表预测: nc=%d nd=%d 角部增益=%s 最外结点 r=%.4f 开关=%s'
          % (exp['nc'], exp['nd'], ('%.6f' % exp['gains'][-1]) if exp['gains'] else 'n/a',
             exp['knots'][-1], exp['sw']))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
