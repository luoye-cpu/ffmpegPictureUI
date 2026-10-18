#!/usr/bin/env python3
"""
_readback-dng-opcodes.py —— OpcodeList3(51022) 的独立读手（第 77 条 ⑰ 组用）

为什么要有第二个实现：dngtool 自己说"写了三支表"不算数。本脚本按 DNG 1.7.1 的
opcode 序列化规则**自己从 TIFF 里解**：count(u32) 后逐条 [code,minVersion,flags] +
载荷（MapTable 的载荷 = dataSize(u32) + area(32) + tableCount(u32) + table*2）。
两条形状判据是本轮踩出来的坑，专门钉住：
  ① 整个列表解完必须**正好落在标签的 byteCount 上**（SDK 读者以此为准，差一个字节就
     报 dng_error_bad_format）；
  ② area 必须是**非空矩形且 pitch==1**：空矩形在读者侧会先被按 fArea.H()/W()(=0) 钳制
     pitch ⇒ 1 变 0 ⇒ 再被 IsEmpty 分支判坏（dng_misc_opcodes.cpp:141-155）。
用法:
  py _readback-dng-opcodes.py x.dng --expect-none
  py _readback-dng-opcodes.py x.dng --expect-spec look.txt      # 表内容逐点回算
  py _readback-dng-opcodes.py x.dng --expect-planes 0,1,2
退出码 0 = 全部判据成立；非 0 = 点名原因（不抛栈、不静默）。
"""
import struct
import sys
import random

SZT = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8, 13: 4}
TAG_OPC1, TAG_OPC2, TAG_OPC3, TAG_SUBIFDS = 51008, 51009, 51022, 0x14A
ID_MAPTABLE = 7
KEYMAP = {'curveR': 0, 'curveG': 1, 'curveB': 2}


def fail(msg):
    print('OPCODE-READ FAIL ' + msg)
    return 1


class Tiff:
    def __init__(self, data):
        if len(data) < 8:
            raise ValueError('文件不足 8 字节')
        self.b = data
        self.e = '<' if data[:2] == b'II' else '>'
        if data[2:4] != struct.pack(self.e + 'H', 42):
            raise ValueError('只支持经典 TIFF (magic=42)')

    def u32(self, off):
        return struct.unpack_from(self.e + 'I', self.b, off)[0]

    def ifd(self, off):
        n = struct.unpack_from(self.e + 'H', self.b, off)[0]
        out = {}
        for i in range(n):
            # TIFF entry = tag(H) type(H) count(I) value/offset(I) —— 值场必须是 I:
            # 写成 'HHIH' 只取到 16 位低半, 大偏移会被截成小数字（本轮实测踩过）
            t, ty, c, v = struct.unpack_from(self.e + 'HHII', self.b, off + 2 + 12 * i)
            out[t] = (ty, c, v)
        return out, self.u32(off + 2 + 12 * n)

    def data(self, ty, c, v):
        """返回 (绝对偏移, 字节数)；内联值不当数据处理"""
        sz = SZT.get(ty, 1) * c
        return (v, sz) if sz > 4 else None

    def ifd0(self):
        return self.ifd(self.u32(4))[0]

    def sub_ifds(self):
        e = self.ifd0().get(TAG_SUBIFDS)
        if not e:
            return []
        r = self.data(*e)
        if not r:
            return [e[2]] if e[1] == 1 else []
        base, sz = r
        return [struct.unpack_from(self.e + 'I', self.b, base + 4 * k)[0] for k in range(sz // 4)]


def parse_list(bytes_data):
    """按规范解一支 opcode list；返回 (opcode 列表, 结束位置) —— 结构不合就抛 ValueError"""
    o = 0
    n = struct.unpack_from('>I', bytes_data, o)[0]
    o += 4
    ops = []
    for i in range(n):
        if o + 12 > len(bytes_data):
            raise ValueError('opcode #%d 头越界' % i)
        code, minv, flags = struct.unpack_from('>III', bytes_data, o)
        o += 12
        KNOWN = (1, 3, ID_MAPTABLE, 14)      # WarpRectilinear / FixVignetteRadial / MapTable / WarpRectilinear2
        dsize = struct.unpack_from('>I', bytes_data, o)[0]
        o += 4
        if code not in KNOWN:
            raise ValueError('opcode #%d code=%d 不在已知集合 %s 内' % (i, code, KNOWN))
        if o + dsize > len(bytes_data):
            raise ValueError('opcode #%d 载荷越界 (need %d)' % (i, dsize))
        if code != ID_MAPTABLE:
            # 镜头那三支由 _readback-dng-lens.py 逐结点回算，这里只做长度走查 ⇒ 两条判据合起来
            # 才覆盖"look 曲线 + 镜头校正同时开"的产物
            ops.append(dict(index=i, code=code, minVersion=minv, flags=flags, area=None,
                            plane=None, planes=None, rowPitch=None, colPitch=None,
                            count=None, table=None, nonTable=True))
            o += dsize
            continue
        t, l, b, r, plane, planes, rp, cp = struct.unpack_from('>iiiiIIII', bytes_data, o)
        cnt = struct.unpack_from('>I', bytes_data, o + 32)[0]
        if dsize != 32 + 4 + cnt * 2:
            raise ValueError('opcode #%d dataSize=%d 与 32+4+count*2=%d 不符'
                             % (i, dsize, 32 + 4 + cnt * 2))
        if o + dsize > len(bytes_data):
            raise ValueError('opcode #%d 载荷越界 (need %d)' % (i, dsize))
        tbl = struct.unpack_from('>%dH' % cnt, bytes_data, o + 36)
        o += dsize
        ops.append(dict(index=i, code=code, minVersion=minv, flags=flags,
                        area=(t, l, b, r), plane=plane, planes=planes,
                        rowPitch=rp, colPitch=cp, count=cnt, table=tbl))
    return ops, o


def curve_from_spec(path):
    """读 look 文件 → {plane: [(x,y),...]}"""
    out = {}
    with open(path, 'rb') as fp:
        for raw in fp.read().split(b'\n'):
            line = raw.decode('utf-8', 'replace').strip()
            if not line or line.startswith('#') or '=' not in line:
                continue
            k, v = line.split('=', 1)
            p = KEYMAP.get(k.strip())
            if p is None:
                continue
            pts = []
            for tok in v.split():
                a, b = tok.split(':')
                pts.append((float(a), float(b)))
            out[p] = pts
    return {p: sorted(out[p], key=lambda q: q[0]) for p in out}


def interp(pts, x):
    if x <= pts[0][0]:
        return pts[0][1]
    if x >= pts[-1][0]:
        return pts[-1][1]
    for i in range(len(pts) - 1):
        x0, y0 = pts[i]
        x1, y1 = pts[i + 1]
        if x0 <= x <= x1:
            t = 0.0 if x1 == x0 else (x - x0) / (x1 - x0)
            return y0 + (y1 - y0) * t
    return pts[-1][1]


def main(argv):
    if len(argv) < 2:
        return fail('用法: _readback-dng-opcodes.py <dng> [--expect-none|--expect-spec F] [--expect-planes 0,1,2]')
    path = argv[1]
    mode_none = '--expect-none' in argv
    spec = None
    planes = None
    if '--expect-spec' in argv:
        spec = argv[argv.index('--expect-spec') + 1]
    if '--expect-planes' in argv:
        planes = [int(x) for x in argv[argv.index('--expect-planes') + 1].split(',')]
    try:
        with open(path, 'rb') as fp:
            tf = Tiff(fp.read())
    except Exception as exc:
        return fail('读取/解析 TIFF 失败: %s' % exc)

    # 51022 落在 raw IFD（SubIFD），也可能在 IFD0 ⇒ 两处都找
    found = []
    scans = [('IFD0', tf.ifd0())]
    for i, off in enumerate(tf.sub_ifds()):
        scans.append(('SubIFD%d' % i, tf.ifd(off)[0]))
    for label, base in scans:
        e = base.get(TAG_OPC3)
        if e:
            r = tf.data(*e)
            if r is None:
                return fail('%s 里 51022 的字节数 <=4 ⇒ 不可能是合法列表' % label)
            found.append((label, r[0], r[1]))

    if mode_none:
        if found:
            return fail('--expect-none 但找到 51022: %s' % found)
        print('OPCODE-READ OK 51022 缺席（反控臂）')
        return 0
    if len(found) != 1:
        return fail('期望恰好一处 51022，实得 %d 处: %s' % (len(found), found))

    label, off, cnt = found[0]
    ops, end = parse_list(tf.b[off:off + cnt])
    if end != cnt:
        return fail('解到 %d 字节，标签声明 %d 字节 ⇒ 列表没落在边界上' % (end, cnt))

    tables = [op for op in ops if not op.get('nonTable')]
    others = [op for op in ops if op.get('nonTable')]
    lines = ['%s:%s(%d B) → %d 支（MapTable=%d 镜头=%s）'
             % (label, 'OpcodeList3', cnt, len(ops), len(tables),
                ','.join(str(op['code']) for op in others) or '无')]
    for op in tables:
        t, l, b, r = op['area']
        if (t, l, b, r) == (0, 0, 0, 0):
            return fail('opcode #%d 的 area 是空矩形 ⇒ 读者侧会被判坏（本轮踩过的坑）' % op['index'])
        if op['rowPitch'] != 1 or op['colPitch'] != 1:
            return fail('opcode #%d pitch=%d/%d（须 1/1）' % (op['index'], op['rowPitch'], op['colPitch']))
        if op['planes'] != 1:
            return fail('opcode #%d planes=%d（分通道形状须 1）' % (op['index'], op['planes']))
        if op['count'] != 65536:
            return fail('opcode #%d 表长=%d（须 65536）' % (op['index'], op['count']))
        if op['minVersion'] < 0x01030000:
            return fail('opcode #%d minVersion=0x%08x < 1.3.0.0' % (op['index'], op['minVersion']))
        lines.append('  #%d plane=%d 表=%d 项 首末=%d/%d' %
                     (op['index'], op['plane'], op['count'], op['table'][0], op['table'][-1]))
    if planes is not None:
        got = sorted(op['plane'] for op in tables)
        if got != sorted(planes):
            return fail('plane 集合实得 %s 期望 %s' % (got, sorted(planes)))
    if spec is not None:
        want = curve_from_spec(spec)
        if not want:
            return fail('规格文件里没有 curveR/G/B 行')
        if len(tables) != len(want):
            return fail('表支数=%d 与规格里的通道数=%d 不符' % (len(tables), len(want)))
        rnd = random.Random(11)
        idxs = sorted(set([0, 1, 16384, 32768, 49152, 65535] + [rnd.randrange(65536) for _ in range(200)]))
        for op in tables:
            pts = want.get(op['plane'])
            if pts is None:
                return fail('产物里有 plane=%d 的表，规格里没有' % op['plane'])
            worst = 0
            for i in idxs:
                pred = round(min(1.0, max(0.0, interp(pts, i / 65535.0))) * 65535.0)
                worst = max(worst, abs(pred - op['table'][i]))
            if worst > 1:
                return fail('plane=%d 表内容与规格差 %d 码（>1 ⇒ 不是量化地板）' % (op['plane'], worst))
            lines.append('  #%d plane=%d 逐点回算最大偏差=%d 码（抽样 %d 点）' %
                         (op['index'], op['plane'], worst, len(idxs)))
    print('OPCODE-READ OK ' + '; '.join(lines))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
