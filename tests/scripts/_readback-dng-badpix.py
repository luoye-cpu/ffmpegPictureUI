#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_readback-dng-badpix.py —— 源声明的坏点表 → 产物 OpcodeList1 的**第二个独立实现**

不 import 写侧（dngtool）任何逻辑，做四件事：
  1) 自己走 IIQ 容器：头 32 字节里的 IIII/MMMM = base → base+4 处 (u32>>8) 必须是 'Raw'(0x526177)
     → base+8 = 目录偏移；记录 (tag,type,len,data) 四个 u32；tag **0x0110** 的 data 是**内联**的块偏移
     → 块头 u16 字节序 + 跳 6 + u32 = 块内目录偏移；记录 (tag,len,data) 三个 u32，data 相对块首
     → tag **0x0400** = SensorDefects，每条 8 字节 = col,row,type,pad 四个 u16
     （type 129=坏点、131/137=坏列，其余不认 ⇒ 写侧必须点名丢弃而不是猜）；
  2) 自己解产物的 OpcodeList1(51008)（大端），取 FixBadPixelsList(ID=5) 的 phase/pCount/rCount/点集/矩形集；
  3) 对撞：点集**逐条相同**（含越界过滤后的集合）、矩形集相同、不认类型一条不进、size 自洽、
     列表正好落在标签 byteCount 上；
  4) 相位档位：从**产物自己的** CFAPattern(0x828E) + ActiveArea(0xC68D) 推"存储 (0,0) 是不是绿点"，
     要求写进去的 phase 落在实测那一档（绿⇒{0,3}，非绿⇒{1,2}；账本 §27 的夹具读数）。

用法：
  py _readback-dng-badpix.py <dng> <源 iiq>
  py _readback-dng-badpix.py <dng> <源> --expect-absent      # 反控：非 IIQ 源不得有 51008
退出码 0 = 判据成立；1 = 判红（点名原因）；2 = 用法错/读不到。
"""
import struct
import sys

TSZ = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 8, 10: 8, 11: 4, 12: 8}
TAG_OPC1 = 51008
ID_FIXBAD = 5
TAG_CFA_DIM = 0x828D        # CFARepeatPatternDim
TAG_CFA_PAT = 0x828E        # CFAPattern（0x82AE 是 Exif 的另一个键，SDK 不认）
TAG_ACTIVE = 0xC68D         # ActiveArea
COLOR_G = 1


def fail(msg):
    print('BADPIX-READ FAIL %s' % msg)
    return 1


class Tiff:
    """按字节序走 TIFF；值场一律按 I 取（按 H 只拿低 16 位会把大偏移截成小数字）。"""

    def __init__(self, data, endian):
        self.b = data
        self.e = endian

    def u32(self, off):
        return struct.unpack_from(self.e + 'I', self.b, off)[0]

    def ifd(self, off):
        n = struct.unpack_from(self.e + 'H', self.b, off)[0]
        if n == 0 or n > 4096:
            raise ValueError('IFD 计数异常 %d @%d' % (n, off))
        out = {}
        for i in range(n):
            t, ty, cnt, val = struct.unpack_from(self.e + 'HHII', self.b, off + 2 + 12 * i)
            out[t] = (ty, cnt, val)
        return out, off + 2 + 12 * n + 4

    def raw(self, ent):
        ty, cnt, val = ent
        sz = TSZ[ty] * cnt
        if sz <= 4:
            return struct.pack(self.e + 'I', val)[:sz]
        return self.b[val:val + sz]

    def ints(self, ent):
        ty, cnt, val = ent
        raw = self.raw(ent)
        if ty in (3,):
            return list(struct.unpack(self.e + '%dH' % cnt, raw))
        if ty in (4,):
            return list(struct.unpack(self.e + '%dI' % cnt, raw))
        if ty in (1, 2, 7):
            return list(raw)
        raise ValueError('不支持的读回类型 %d' % ty)


def sub_ifds(t):
    d0, _ = t.ifd(t.u32(4))
    e = d0.get(0x14A)
    if not e:
        return d0, []
    return d0, [o for o in t.ints(e) if o]


def read_dng(path):
    data = open(path, 'rb').read()
    bo = data[:2]
    if bo == b'II':
        t = Tiff(data, '<')
    elif bo == b'MM':
        t = Tiff(data, '>')
    else:
        raise ValueError('产物字节序头不认识: %r' % bo)
    ifd0, _ = t.ifd(t.u32(4))
    scans = [ifd0] + [t.ifd(o)[0] for o in sub_ifds(t)[1]]
    return data, t, scans


def find_opc1(data, scans):
    hits = []
    for i, d in enumerate(scans):
        e = d.get(TAG_OPC1)
        if e and e[1] > 4:
            hits.append((i, e))
    return hits


def parse_opc1(blob):
    o = 0
    n = struct.unpack_from('>I', blob, o)[0]
    o += 4
    out = []
    for i in range(n):
        code, minv, flags = struct.unpack_from('>III', blob, o)
        o += 12
        size = struct.unpack_from('>I', blob, o)[0]
        o += 4
        if o + size > len(blob):
            raise ValueError('opcode #%d 载荷越界（需 %d 只剩 %d）' % (i, size, len(blob) - o))
        out.append(dict(code=code, minv=minv, flags=flags, size=size, body=blob[o:o + size], at=o))
        o += size
    return out, o


def parse_fixbad(rec_size, body):
    """线格式（`dng_opcode_list.cpp:213-219` 只写 ID/minVersion/flags，**没有**独立的记录长度场；
    长度场由 `dng_opcode_FixBadPixelsList::PutData` 自己写在参数最前，`dng_bad_pixels.cpp:699-731`）：
      记录 = ID | minVersion | flags | size | BayerPhase | pCount | rCount | (row,col)* | (t,l,b,r)*
    ⇒ `size` 已在 parse_opc1 里被消费，这里收到的 body 从 BayerPhase 起。"""
    if len(body) != rec_size:
        raise ValueError('载荷 %d 字节与记录声明 size=%d 不符 ⇒ 上一条记录的边界算错了' % (len(body), rec_size))
    if len(body) < 12:
        raise ValueError('FixBadPixelsList 参数只有 %d 字节' % len(body))
    phase, pc, rc = struct.unpack_from('>III', body, 0)
    if rec_size != 12 + 8 * pc + 16 * rc:
        raise ValueError('size=%d 与 pCount=%d/rCount=%d 不成（应为 %d）'
                         % (rec_size, pc, rc, 12 + 8 * pc + 16 * rc))
    pts = [struct.unpack_from('>ii', body, 12 + 8 * i) for i in range(pc)]
    rcs = [struct.unpack_from('>iiii', body, 12 + 8 * pc + 16 * i) for i in range(rc)]
    return phase, pts, rcs


def read_iiq_top(path):
    """MOBJ **顶层**目录的 tag -> (type, len, data)。
    Phase One 的黑电平 0x021D 就挂在顶层（不在 0x0110 块内）——本轮先只在块内找过，
    结果把已在盘上的 0x021D 读成了"缺席"，所以这里把两层分开、由调用方点名要哪一层。"""
    b, e, _base, d = _iiq_dir(path)
    ent = struct.unpack_from(e + 'I', b, d)[0]
    if ent == 0 or ent > 8192:
        raise ValueError('MOBJ 目录条数异常 %d' % ent)
    out = {}
    for i in range(ent):
        tag, ty, ln, dt = struct.unpack_from(e + 'IIII', b, d + 8 + 16 * i)
        out[tag] = (ty, ln, dt)
    return out


def iiq_black_level(path):
    """源自己声明的黑电平 = MOBJ 顶层 tag 0x021D（LibRaw 存进 color.phase_one_data.t_black）。
    ⚠ MOBJ 的标量记录把值**内联**在第 4 个 u32 里（`mediumformat.cpp:143` 就是
    `ph1.t_black = data`），不像 TIFF 那样 ≤4 字节放偏移、>4 才外链——
    按偏移去取会读到文件第 1024 字节处的垃圾（本函数第一版就这么错过一次）。"""
    _b, _e, _base, _d = _iiq_dir(path)
    hit = read_iiq_top(path).get(0x021D)
    if not hit:
        raise ValueError('MOBJ 顶层没有 tag 0x021D ⇒ 本件没有声明黑电平')
    ty, ln, dt = hit
    if ty != 4 or ln != 4:
        raise ValueError('0x021D 的类型/长度不认识 type=%d len=%d' % (ty, ln))
    return dt


def _iiq_dir(path):
    """认 IIQ 头（base + 'Raw' 签名），返回 (整文件字节, 顶层字节序, base, 顶层目录绝对偏移)。"""
    b = open(path, 'rb').read()
    head = b[:32]
    base = -1
    e = '<'
    for pat, en in ((b'IIII', '<'), (b'MMMM', '>')):
        i = head.find(pat)
        if i >= 0:
            base, e = i, en
            break
    if base < 0:
        raise ValueError('源头 32 字节里没有 IIII/MMMM ⇒ 这不是 IIQ')
    if (struct.unpack_from(e + 'I', b, base + 4)[0] >> 8) != 0x526177:
        raise ValueError("base+4 处不是 'Raw' 签名 ⇒ 不按 IIQ 读")
    return b, e, base, base + struct.unpack_from(e + 'I', b, base + 8)[0]


def read_iiq(path):
    b, e, base, _d = _iiq_dir(path)
    hit = read_iiq_top(path).get(0x0110)
    if not hit or hit[1] < 8:
        raise ValueError('MOBJ 没有 tag 0x0110 元数据块')
    mb = base + hit[2]                      # data 场是内联的块偏移（相对 base）
    m16 = struct.unpack_from('<H', b, mb)[0]
    e2 = '<' if m16 == 0x4949 else '>'
    if m16 not in (0x4949, 0x4D4D):
        raise ValueError('元数据块头字节序标记不认识 %04X' % m16)
    bd = mb + struct.unpack_from(e2 + 'I', b, mb + 8)[0]
    ent2 = struct.unpack_from(e2 + 'I', b, bd)[0]
    if ent2 == 0 or ent2 > 8192:
        raise ValueError('块内目录条数异常 %d' % ent2)
    ln = dt = None
    for i in range(ent2):
        tag, l2, d2 = struct.unpack_from(e2 + 'III', b, bd + 8 + 12 * i)
        if tag == 0x0400:
            ln, dt = l2, d2
            break
    if ln is None:
        raise ValueError('元数据块里没有 tag 0x0400 SensorDefects')
    if ln < 8 or ln % 8:
        raise ValueError('SensorDefects 长度 %d 不成条' % ln)
    at = mb + dt
    rec = [struct.unpack_from('<HHHH', b, at + 8 * i) for i in range(ln // 8)]
    return rec


def expect_phase(t, ifd0):
    """从产物自己的 CFAPattern + ActiveArea 推"存储 (0,0) 是不是绿点"。"""
    dim = ifd0.get(TAG_CFA_DIM)
    pat = ifd0.get(TAG_CFA_PAT)
    act = ifd0.get(TAG_ACTIVE)
    if not dim or not pat or not act:
        raise ValueError('产物缺 CFARepeatPatternDim/CFAPattern/ActiveArea ⇒ 推不出相位档')
    rows, cols = t.ints(dim)[:2]
    vals = t.ints(pat)
    if rows != 2 or cols != 2 or len(vals) < 4:
        raise ValueError('本判据只接 2x2 Bayer，实得 %dx%d 与 %d 个图案值' % (rows, cols, len(vals)))
    top, left, _b, _r = t.ints(act)
    green_at_active = vals[0] == COLOR_G
    shift = ((top + left) & 1) != 0
    green_at_stored = (not green_at_active) if shift else green_at_active
    return green_at_stored, (top, left), vals


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    dng, src = argv[1], argv[2]
    want_absent = '--expect-absent' in argv
    try:
        data, t, scans = read_dng(dng)
    except (ValueError, struct.error, IndexError) as exc:
        return fail('解产物失败: %s' % exc)
    hits = find_opc1(data, scans)

    if want_absent:
        if hits:
            for i, e in hits:
                try:
                    ops, _ = parse_opc1(data[e[2]:e[2] + e[1]])
                except ValueError as exc:
                    return fail('IFD#%d 的 51008 解不开（%s）⇒ 反控要的是"没有这一支"' % (i, exc))
                if any(o['code'] == ID_FIXBAD for o in ops):
                    return fail('--expect-absent 但 IFD#%d 的 OpcodeList1 里有 FixBadPixelsList' % i)
        print('BADPIX-READ OK 产物没有 FixBadPixelsList（反控臂，51008 命中 %d 处）' % len(hits))
        return 0

    if len(hits) != 1:
        return fail('期望恰好一处 51008，实得 %d 处 ⇒ 主判据失去落点' % len(hits))
    bi, ent = hits[0]
    ty = ent[0]
    if ty != 7:
        return fail('51008 的 type=%d 不是 UNDEFINED(7)' % ty)
    blob = data[ent[2]:ent[2] + ent[1]]
    try:
        ops, end = parse_opc1(blob)
    except ValueError as exc:
        return fail('解 OpcodeList1 失败: %s' % exc)
    if end != ent[1]:
        return fail('解到 %d 字节而标签声明 %d ⇒ 列表没落在边界上' % (end, ent[1]))
    fb = [o for o in ops if o['code'] == ID_FIXBAD]
    if len(fb) != 1:
        return fail('FixBadPixelsList 支数=%d（应为 1）；列表里是 %s' % (len(fb), [o['code'] for o in ops]))
    if len([o for o in ops if o['code'] in (4, 5)]) != 1:
        return fail('同一列表里坏点支重复')
    try:
        phase, pts, rcs = parse_fixbad(fb[0]['size'], fb[0]['body'])
    except ValueError as exc:
        return fail('FixBadPixelsList 载荷不对: %s' % exc)

    try:
        rec = read_iiq(src)
    except (ValueError, struct.error) as exc:
        return fail('读源 IIQ 失败: %s' % exc)

    try:
        gStored, margin, patvals = expect_phase(t, scans[bi] if bi < len(scans) else scans[0])
    except ValueError as exc:
        return fail('推相位档失败: %s' % exc)
    raw_w = None
    for d in scans:
        if 256 in d and 257 in d:
            raw_w, raw_h = t.ints(d[256])[0], t.ints(d[257])[0]
            break
    if not raw_w:
        return fail('产物没有 ImageWidth/ImageLength')

    src_pts = sorted((r, c) for (c, r, ty2, _p) in rec if ty2 == 129 and r < raw_h and c < raw_w)
    # 越界按写侧同一定义统计：坏点看 (row,col) 都进幅面，坏列看 col 进幅面 ⇒ 两边的数才对得上
    src_oob = sum(1 for (c, r, ty2, _p) in rec
                  if ty2 == 129 and not (r < raw_h and c < raw_w)) +               sum(1 for (c, _r, ty2, _p) in rec if ty2 in (131, 137) and c >= raw_w)
    src_cols = sorted(c for (c, _r, ty2, _p) in rec if ty2 in (131, 137) and c < raw_w)
    src_unknown = sum(1 for (_c, _r, ty2, _p) in rec if ty2 not in (129, 131, 137))

    got_pts = sorted(pts)
    got_cols = sorted(l for (_tp, l, _bt, _rt) in rcs)
    bad = []
    if got_pts != src_pts:
        miss = [x for x in src_pts if x not in set(got_pts)]
        extra = [x for x in got_pts if x not in set(src_pts)]
        bad.append('点集不符：产物 %d 条，源应 %d 条（缺 %d 例=%s 多 %d 例=%s）'
                   % (len(got_pts), len(src_pts), len(miss), miss[:4], len(extra), extra[:4]))
    if len(rcs) != len(src_cols):
        bad.append('矩形条数=%d 而源坏列=%d 条' % (len(rcs), len(src_cols)))
    elif got_cols != src_cols:
        bad.append('坏列集合不符（产物 %s 源 %s）' % (got_cols[:4], src_cols[:4]))
    if src_oob or src_unknown:
        # 这些必须留在表外：条数对不上就是"偷偷带进去了"或"该带的没带"
        if len(pts) != len(src_pts):
            bad.append('越界=%d/不认类型=%d 条时应恰好丢掉，实得 pCount=%d' % (src_oob, src_unknown, len(pts)))
    ok_phase = (phase in (0, 3)) == gStored
    if not ok_phase:
        bad.append('相位=%d 落在%s档，而产物 CFAPattern=%s + ActiveArea=%s 推得存储(0,0)%s绿点'
                   % (phase, '奇' if ok_phase else '偶', patvals, margin, '是' if gStored else '非'))
    if fb[0]['minv'] != 0x01030000:
        bad.append('minVersion=0x%08X 不是 1.3.0.0' % fb[0]['minv'])

    print('BADPIX-READ 源=%s 记录=%d (点%d 列%d 越界%d 不认%d) 产物: IFD#%d 支=%d pCount=%d rCount=%d phase=%d 幅面=%dx%d'
          % (src, len(rec), len(src_pts), len(src_cols), src_oob, src_unknown, bi, len(ops),
             len(pts), len(rcs), phase, raw_w, raw_h))
    if bad:
        for m in bad:
            print('BADPIX-READ FAIL ' + m)
        return 1
    print('BADPIX-READ OK 点集逐条相同、坏列相同、不认类型未进、相位档与产物 CFA 一致')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
