#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_make-secondary-ifd-fixture.py —— 造一个**带次级 IFD** 的合成 DNG `base.dng`（第 77 条 F 类夹具）

为什么必须自己造（账本 §36/§39）：DNG 规范里的透明遮罩(NSFT 4)、深度图(NSFT 8 + 51177-51181)、
语义遮罩(NSFT 65540 + PI 52527 + 52526/52528/52536)、`MaskedAreas(50830)`、XMP 包(tag 700)、
预览(NSFT 1) 都住在**次级 IFD / 主 IFD 的规范标签**里；本机 44 件厂商语料一件都不带这些位，
而 dngtool 里四类遮罩/深度的写侧 API（SetTransparencyMask / SetDepthMap / AppendSemanticMask /
SetMaskedArea）调用计数为 0、预览搬运又只在 `!isDngInput` 分支 ⇒ "DNG→DNG 再编码会不会静默丢位"
在 44 件语料上**永远走不到**，也就没有任何在册判据看着它。没有夹具只能记"未判定"。

本脚本沿用本仓 `_probe-badpix-phase.py:build_dng` 那套自写 TIFF 布局机制（脚本侧无法调用 SDK
写侧 API ⇒ 只能手写字节）。类型/计数取自 SDK 自己的 ParseTag 要求（逐条一手核对）：
  51177 SHORT/1、51178 RATIONAL/1、51179 RATIONAL/1、51180 SHORT/1、51181 SHORT/1（dng_shared.cpp:3211+）
  52526 ASCII 串、52528 ASCII 串、52536 LONG/4（dng_ifd.cpp:2826/2877/2983）
  50830 SHORT 或 LONG、count 必须是 4 的倍数、且只准住在主 IFD（dng_ifd.cpp:2065）
  700   UNDEFINED，载荷以 `http://ns.adobe.com/xap/1.0/\0` 开头（dng_tag_codes.h:147）
  NSFT：sfTransparencyMask=4、sfDepthMap=8、sfSemanticMask=0x10004=65540、sfPreviewImage=1
  PI：piTransparencyMask=4、piDepth=51177、piPhotometricMask=52527（dng_tag_values.h:77/87/89）
次级 IFD 一律由主 IFD 的 330 SubIFDs 指过去（DNG 规范；next-IFD 链留 0）。

结构：
  IFD0 主图 64×64 GRBG 16-bit 无压缩 Bayer + 必需 DNG 位 + MaskedAreas(50830) + XMP(700) + SubIFDs(330)
  IFD#1 16×16 8-bit  透明遮罩 NSFT=4     PI=4
  IFD#2 16×16 16-bit 深度图   NSFT=8     PI=51177 + 51177/51178/51179/51180/51181
  IFD#3 16×16 16-bit 语义遮罩 NSFT=65540 PI=52527 + 52526/52528/52536
  IFD#4 16×16 JPEG   预览     NSFT=1     Compression=6 + 513/514

自检（三道，任一不过就退非零 ⇒ 不给门禁留"夹具其实没成立"的空档）：
  1) 自己的 IFD 枚举（写手与读手分开实现，避免"自己造自己认"）：NSFT 集合、各 IFD 标签号、byteCount；
  2) exiftool（第二个实现）读回 NewSubFileType / MaskedAreas / XMP；
  3) Adobe 官方 `dng_validate -v`：**rc=0** 且 verbose 点名 DepthFormat/SemanticName/MaskedAreas/XMP。

用法：py _make-secondary-ifd-fixture.py <输出 dng> [<dng_validate.exe>]
退出 0 = 夹具成立；1 = 不成立；2 = 用法/写不出。
"""
import os
import struct
import subprocess
import sys

TSZ = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 4, 10: 8}
MAIN_W = MAIN_H = 64
MASK_W = MASK_H = 16
WHITE = 16383
COLORVAL = {'R': 0, 'G': 1, 'B': 2}
GRBG = (('G', 'R'), ('B', 'G'))
XMP_MARKER = b'http://ns.adobe.com/xap/1.0/\x00'

EXPECT_NSFT = {0, 1, 4, 8, 65540}
DEPTH_TAGS = {51177, 51178, 51179, 51180, 51181}
SEM_TAGS = {52526, 52528, 52536}


class Ifd(object):
    def __init__(self, name):
        self.name = name
        self.items = []                     # (tag, type, count, payload bytes)

    def add(self, tag, ty, count, payload=None, value=None):
        if payload is None:
            payload = encode(ty, count, value)
        if len(payload) != TSZ[ty] * count:
            raise AssertionError('tag 0x%X 载荷 %d 与声明 %d*%d 不符'
                                 % (tag, len(payload), TSZ[ty], count))
        self.items.append((tag, ty, count, payload))

    def addstr(self, tag, s):
        """ASCII 串：count 必须含结尾 NUL（TIFF 规定），别让调用方各数一遍。"""
        pay = s if isinstance(s, bytes) else s.encode('ascii')
        if not pay.endswith(b'\0'):
            pay += b'\0'
        self.add(tag, 2, len(pay), payload=pay)

    def size(self):
        return 2 + 12 * len(self.items) + 4


def encode(ty, count, value):
    if ty == 2:
        v = value if isinstance(value, bytes) else value.encode('ascii')
        return v if v.endswith(b'\0') else v + b'\0'
    if ty in (1, 7):
        return value if isinstance(value, bytes) else bytes(value)
    if ty == 3:
        return struct.pack('<%dH' % count, *value)
    if ty == 4:
        return struct.pack('<%dI' % count, *value)
    if ty == 5:
        return struct.pack('<%dI' % (2 * count), *[x for pr in value for x in pr])
    if ty == 9:
        return struct.pack('<%di' % count, *value)
    if ty == 10:
        return struct.pack('<%di' % (2 * count), *[x for pr in value for x in pr])
    raise AssertionError('类型 %d 未实现' % ty)


def align2(n):
    return (n + 1) & ~1


def main_bytes():
    rows = []
    for r in range(MAIN_H):
        row = [{'G': 1000, 'R': 3000, 'B': 500}[GRBG[r & 1][c & 1]] + ((r + c) % 7)
               for c in range(MAIN_W)]
        rows.append(struct.pack('<%dH' % MAIN_W, *row))
    return b''.join(rows)


def gray16_bytes(step):
    rows = []
    for r in range(MASK_H):
        rows.append(struct.pack('<%dH' % MASK_W,
                                *[min(65535, (r * MASK_W + c) * step) for c in range(MASK_W)]))
    return b''.join(rows)


def alpha_bytes():
    return bytes(bytearray([(r * MASK_W + c) % 256 for r in range(MASK_H) for c in range(MASK_W)]))


def gray8_bytes(step):
    """语义遮罩按 SDK 的硬规矩必须是 **8 bit / 1 sample**（dng_ifd.cpp:3616-3628）。"""
    return bytes(bytearray([((r * MASK_W + c) * step) % 256
                            for r in range(MASK_H) for c in range(MASK_W)]))


def xmp_packet():
    # 除了自定义命名空间（验证"未知 ns 抄不走"这条边界），还要带三个 **PS/ACR 真会读** 的标准键：
    # xmp:CreatorTool / xmp:Rating / dc:subject。产品侧的后置搬运用的是 `-XMP:all`（按标签抄），
    # 只有标准命名空间才到得了产物 ⇒ 判据要靠这三个键（账本 §40.9）。
    body = (b'<?xpacket begin="\xef\xbb\xbf" id="W5M0MpCehiHzreSzNTczkc9d"?>'
            b'<x:xmpmeta xmlns:x="adobe:ns:meta/">'
            b'<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
            b'<rdf:Description rdf:about=""'
            b' xmlns:xmp="http://ns.adobe.com/xap/1.0/"'
            b' xmlns:dc="http://purl.org/dc/elements/1.1/"'
            b' xmlns:syn="http://example.invalid/syn/1.0/"'
            b' xmp:CreatorTool="fixturetool" xmp:Rating="5"'
            b' syn:FixtureFor="secondary IFD carry">'
            b'<dc:subject><rdf:Bag><rdf:li>kw1</rdf:li><rdf:li>kw2</rdf:li></rdf:Bag></dc:subject>'
            b'</rdf:Description>'
            b'</rdf:RDF></x:xmpmeta><?xpacket end="w"?>')
    return XMP_MARKER + body


def preview_jpeg(scratch):
    from PIL import Image
    im = Image.new('RGB', (MASK_W, MASK_H))
    im.putpixel((3, 3), (255, 0, 0))
    im.putpixel((9, 9), (0, 255, 255))
    im.save(scratch, 'JPEG', quality=90)
    with open(scratch, 'rb') as f:
        return f.read()


def mask_ifd(name, nsft, bps, comp, pi, data_off, data_len):
    d = Ifd(name)
    d.add(254, 4, 1, value=(nsft,))
    d.add(256, 4, 1, value=(MASK_W,))
    d.add(257, 4, 1, value=(MASK_H,))
    d.add(258, 3, 1, value=(bps,))
    d.add(259, 3, 1, value=(comp,))
    d.add(262, 3, 1, value=(pi,))
    d.add(273, 4, 1, value=(data_off,))
    d.add(277, 3, 1, value=(1,))
    d.add(278, 4, 1, value=(MASK_H,))
    d.add(279, 4, 1, value=(data_len,))
    d.add(284, 3, 1, value=(1,))
    return d


def build(path, drop=()):
    """drop 取值：xmp / maskedareas / transparency / depth / semantic / preview。

    用途只有一条：判据要覆盖"主 IFD 内容位**单独存在**"的形状（例如只有 MaskedAreas(50830)、
    一个次级 IFD 都没有）——带全副夹具时这条空档量不到（账本 §40.8）。
    """
    drop = set(drop)
    here = os.path.dirname(os.path.abspath(path)) or '.'
    jpg = preview_jpeg(os.path.join(here, '_secfixture-preview.jpg')) if 'preview' not in drop else b''
    blobs = [main_bytes(), alpha_bytes(), gray16_bytes(1), gray8_bytes(3), jpg]

    # 1) 图像数据区
    offs, cur = [], 8
    for b in blobs:
        offs.append(cur)
        cur += align2(len(b))

    # 2) 主 IFD
    m = Ifd('main')
    m.add(254, 4, 1, value=(0,))
    m.add(256, 4, 1, value=(MAIN_W,))
    m.add(257, 4, 1, value=(MAIN_H,))
    m.add(258, 3, 1, value=(16,))
    m.add(259, 3, 1, value=(1,))
    m.add(262, 3, 1, value=(32803,))
    m.add(273, 4, 1, value=(offs[0],))
    m.add(274, 3, 1, value=(1,))
    m.add(277, 3, 1, value=(1,))
    m.add(278, 4, 1, value=(MAIN_H,))
    m.add(279, 4, 1, value=(len(blobs[0]),))
    m.add(284, 3, 1, value=(1,))
    m.add(0x828D, 3, 2, value=(2, 2))
    m.add(0x828E, 1, 4, payload=struct.pack('<4B', COLORVAL[GRBG[0][0]], COLORVAL[GRBG[0][1]],
                                             COLORVAL[GRBG[1][0]], COLORVAL[GRBG[1][1]]))
    m.add(0xC612, 1, 4, payload=struct.pack('<4B', 1, 7, 0, 0))
    m.add(0xC613, 1, 4, payload=struct.pack('<4B', 1, 4, 0, 0))
    m.addstr(0xC614, 'SYNTH SECONDARY')
    m.add(0xC616, 1, 3, payload=struct.pack('<3B', 0, 1, 2))
    m.add(0xC619, 3, 2, value=(1, 1))
    m.add(0xC61A, 5, 1, value=((0, 1),))
    m.add(0xC61D, 3, 1, value=(WHITE,))
    m.add(0xC61F, 4, 2, value=(0, 0))
    m.add(0xC620, 4, 2, value=(MAIN_W, MAIN_H))
    m.add(0xC621, 10, 9, payload=struct.pack(
        '<18i', *[x for i in range(9) for x in ((1, 1) if i in (0, 4, 8) else (0, 1))]))
    m.add(0xC68D, 4, 4, value=(0, 0, MAIN_H, MAIN_W))
    if 'maskedareas' not in drop:
        m.add(50830, 4, 4, value=(4, 8, 20, 32))                   # MaskedAreas 一个矩形
    if 'xmp' not in drop:
        m.add(700, 7, len(xmp_packet()), payload=xmp_packet())      # XMP 包

    ifds = [m]
    if 'transparency' not in drop:
        ifds.append(mask_ifd('transparency', 4, 8, 1, 4, offs[1], len(blobs[1])))
    if 'depth' not in drop:
        d = mask_ifd('depth', 8, 16, 1, 51177, offs[2], len(blobs[2]))
        d.add(51177, 3, 1, value=(1,))                              # DepthFormat = Linear
        d.add(51178, 5, 1, value=((1, 1000),))                     # DepthNear
        d.add(51179, 5, 1, value=((25, 1),))                       # DepthFar
        d.add(51180, 3, 1, value=(1,))                              # DepthUnits = Meters
        d.add(51181, 3, 1, value=(1,))                              # DepthMeasureType
        ifds.append(d)
    if 'semantic' not in drop:
        s = mask_ifd('semantic', 65540, 8, 1, 52527, offs[3], len(blobs[3]))
        s.addstr(52526, 'fixture-mask')                # SemanticName
        s.addstr(52528, 'instance-1')                  # SemanticInstanceID
        s.add(52536, 4, 4, value=(0, 0, MASK_H, MASK_W))           # MaskSubArea
        ifds.append(s)
    if 'preview' not in drop:
        p = Ifd('preview')
        p.add(254, 4, 1, value=(1,))
        p.add(256, 4, 1, value=(MASK_W,))
        p.add(257, 4, 1, value=(MASK_H,))
        p.add(259, 3, 1, value=(6,))                                # Compression = JPEG
        p.add(262, 3, 1, value=(6,))                                # YCbCr（PIL 默认）
        p.add(277, 3, 1, value=(3,))                                # SamplesPerPixel（Adobe 硬要求）
        p.add(282, 5, 1, value=((72, 1),))
        p.add(283, 5, 1, value=((72, 1),))
        p.add(296, 3, 1, value=(2,))
        p.add(513, 4, 1, value=(offs[4],))                          # JPEGInterchangeFormat
        p.add(514, 4, 1, value=(len(blobs[4]),))                    # JPEGInterchangeFormatLength
        ifds.append(p)

    # 3) 分配 IFD 偏移；330 SubIFDs 指向 1..N（没有次级 IFD 时不写 330）
    if len(ifds) > 1:
        ifd_off, cur2 = [], cur
        for x in ifds:
            ifd_off.append(cur2)
            cur2 += x.size()
        m.add(330, 4, len(ifds) - 1, value=tuple(ifd_off[1:]))
        # 330 加进来后尺寸变了 ⇒ 重新分配一遍（保证偏移与序列化一致）
        ifd_off, cur2 = [], cur
        for x in ifds:
            ifd_off.append(cur2)
            cur2 += x.size()
        m.items[[e[0] for e in m.items].index(330)] = (
            330, 4, len(ifds) - 1, encode(4, len(ifds) - 1, tuple(ifd_off[1:])))
    else:
        ifd_off, cur2 = [], cur
        for x in ifds:
            ifd_off.append(cur2)
            cur2 += x.size()

    # 4) 外联值区偏移
    val_at = cur2
    table, order = {}, []
    for x in ifds:
        for (tag, ty, cnt, pay) in sorted(x.items, key=lambda e: e[0]):
            if len(pay) > 4:
                order.append((x, tag, val_at))
                table[(id(x), tag)] = val_at
                val_at += align2(len(pay))

    # 5) 序列化
    out = bytearray(struct.pack('<HHI', 0x4949, 42, ifd_off[0]))
    for b in blobs:
        out += b
        if len(b) % 2:
            out += b'\0'
    if len(out) != cur:
        raise AssertionError('数据区 %d 与 IFD 起点 %d 不符' % (len(out), cur))
    for i, x in enumerate(ifds):
        if len(out) != ifd_off[i]:
            raise AssertionError('IFD %s 偏移错（%d vs %d）' % (x.name, len(out), ifd_off[i]))
        items = sorted(x.items, key=lambda e: e[0])
        out += struct.pack('<H', len(items))
        for (tag, ty, cnt, pay) in items:
            v = (struct.unpack('<I', pay.ljust(4, b'\0'))[0] if len(pay) <= 4
                 else table[(id(x), tag)])
            out += struct.pack('<HHII', tag, ty, cnt, v)
        out += struct.pack('<I', 0)          # DNG：次级 IFD 只由 330 指，不接 next 链
    for (x, tag, off) in order:
        pay = [e for e in x.items if e[0] == tag][0][3]
        if len(out) != off:
            raise AssertionError('值区偏移错（tag 0x%X：%d vs %d）' % (tag, len(out), off))
        out += pay
        if len(pay) % 2:
            out += b'\0'
    with open(path, 'wb') as f:
        f.write(bytes(out))
    return {'ifdOffs': ifd_off, 'bytes': len(out)}


# ── 独立枚举（读手与写手分开实现） ────────────────────────────────────────────
def enumerate_ifds(path):
    """返回 [(偏移, NSFT, {tag: (type, count, byteCount, 值字段)})]，按发现顺序。"""
    with open(path, 'rb') as f:
        b = f.read()
    if b[:2] != b'II':
        raise ValueError('不是小端 TIFF：%r' % b[:4])

    def read_ifd(off):
        n = struct.unpack_from('<H', b, off)[0]
        d, i = {}, 0
        while i < n:
            tag, ty, cnt = struct.unpack_from('<HHI', b, off + 2 + 12 * i)
            if ty not in TSZ:
                raise ValueError('tag 0x%X 未知类型 %d' % (tag, ty))
            bc = TSZ[ty] * cnt
            d[tag] = (ty, cnt, bc, struct.unpack_from('<I', b, off + 2 + 12 * i + 8)[0])
            i += 1
        return d, struct.unpack_from('<I', b, off + 2 + 12 * n)[0]

    def deref(tag_entry):
        ty, cnt, bc, v = tag_entry
        return (struct.pack('<I', v)[:bc], b[v:v + bc])[bc > 4]

    out, seen, todo = [], set(), [struct.unpack_from('<I', b, 4)[0]]
    while todo:
        o = todo.pop(0)
        if not o or o in seen or o + 2 > len(b):
            continue
        seen.add(o)
        d, nxt = read_ifd(o)
        ns = struct.unpack('<I', deref(d[254]))[0] if 254 in d else 0
        out.append((o, ns, d))
        if 330 in d:
            raw = deref(d[330])
            for i in range(len(raw) // 4):
                todo.append(struct.unpack_from('<I', raw, 4 * i)[0])
        if nxt:
            todo.append(nxt)
    return out, b


def selfcheck(path, dvp):
    fails, reads = [], []
    ifs, b = enumerate_ifds(path)
    nsfts = sorted(set(x[1] for x in ifs))
    reads.append('IFD数=%d NSFT集合=%s' % (len(ifs), nsfts))
    if set(nsfts) != EXPECT_NSFT:
        fails.append('NSFT 集合 %s ≠ 期望 %s' % (nsfts, sorted(EXPECT_NSFT)))
    by = dict((x[1], x[2]) for x in ifs)

    m = by.get(0, {})
    for tag, why in ((50830, '主 IFD 无 MaskedAreas(50830)'), (700, '主 IFD 无 XMP(700)')):
        if tag not in m:
            fails.append(why)
    if 700 in m:
        ty, cnt, bc, v = m[700]
        pkt = b[v:v + bc]
        reads.append('XMP byteCount=%d 标记=%r' % (bc, pkt[:16]))
        if not pkt.startswith(XMP_MARKER):
            fails.append('XMP 载荷不以规范标记开头')
    if 50830 in m:
        ty, cnt, bc, v = m[50830]
        vals = struct.unpack('<%dI' % cnt, b[v:v + bc] if bc > 4 else struct.pack('<I', v))
        reads.append('MaskedAreas count=%d 值=%s' % (cnt, vals))
        if cnt % 4 or cnt != 4:
            fails.append('MaskedAreas count=%d 不是 4 的倍数' % cnt)

    dd = by.get(8, {})
    miss = sorted(DEPTH_TAGS - set(dd))
    dpi = dd[262][3] if 262 in dd and dd[262][2] <= 4 else None
    reads.append('深度 NSFT=8 标签=%s PI=%s' % (sorted(dd), dpi))
    if miss:
        fails.append('深度 IFD 缺 %s' % miss)

    sm = by.get(65540, {})
    miss = sorted(SEM_TAGS - set(sm))
    pi = sm[262][3] if 262 in sm and sm[262][2] <= 4 else None
    reads.append('语义 NSFT=65540 标签=%s PI=%s' % (sorted(sm), pi))
    if miss:
        fails.append('语义遮罩 IFD 缺 %s' % miss)
    if pi is not None and pi != 52527:
        fails.append('语义遮罩 PI=%s 非 52527' % pi)
    if 4 not in by:
        fails.append('无透明遮罩 IFD(NSFT=4)')
    if 1 not in by:
        fails.append('无预览 IFD(NSFT=1)')

    ex = os.environ.get('FFMPEGGUI_EXIFTOOL') or os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
        'publish', 'PLAN', 'exiftool', 'exiftool.exe')
    if os.path.exists(ex):
        # ⚠ exiftool 把 tag 254 叫 **SubfileType**（不叫 NewSubfileType/NewSubFileType）⇒
        #   拿后者查会读成空，看起来像"第二个实现不认"，其实是我用错了名字。
        p = subprocess.run([ex, '-a', '-u', '-G1', '-n', '-s', '-SubfileType',
                            '-MaskedAreas', '-XMP', '-DepthFormat', '-SemanticName', path],
                           capture_output=True, text=True, encoding='utf-8', errors='replace')
        txt = (p.stdout or '').strip()
        subs = [L.split(':', 1)[1].strip() for L in txt.splitlines() if 'SubfileType' in L]
        reads.append('exiftool SubfileType(-n 数值)集合=%s' % subs)
        reads.append('exiftool 其余: %s' % ' | '.join(
            L.strip() for L in txt.splitlines() if 'SubfileType' not in L)[:240])
        if set(subs) != set(str(x) for x in EXPECT_NSFT):
            fails.append('exiftool（第二个实现）读出的 SubfileType 数值集合 %s ≠ 期望 %s'
                         % (sorted(subs), sorted(str(x) for x in EXPECT_NSFT)))
        if not all(x in txt for x in ('DepthFormat', 'SemanticName', 'MaskedAreas', 'XMP')):
            fails.append('exiftool 没读出 DepthFormat/SemanticName/MaskedAreas/XMP 之一')
    else:
        reads.append('exiftool 不在位 ⇒ 第二口径未跑（不因此判过）')

    if dvp and os.path.exists(dvp):
        p = subprocess.run([dvp, '-v', path], capture_output=True,
                           text=True, encoding='utf-8', errors='replace')
        txt = (p.stdout or '') + (p.stderr or '')
        keys = [k for k in ('DepthFormat', 'DepthNear', 'DepthFar', 'DepthUnits',
                            'SemanticName', 'MaskSubArea', 'MaskedAreas', 'XMP',
                            'TransparencyMask', 'PreviewImage') if k in txt]
        reads.append('dng_validate rc=%d verbose 点名=%s' % (p.returncode, ','.join(keys) or '无'))
        if p.returncode != 0:
            fails.append('dng_validate rc=%d ⇒ Adobe 不认这份夹具 :: %s'
                         % (p.returncode, txt.strip()[-300:]))
        for must in ('DepthFormat', 'SemanticName', 'MaskedAreas', 'XMP'):
            if must not in txt:
                fails.append('dng_validate verbose 未点名 %s' % must)
    else:
        fails.append('没有 dng_validate ⇒ 无法证明"Adobe 认这些位" ⇒ 夹具不成立')

    for r in reads:
        print('SEC-FIXTURE 读数 %s' % r)
    if fails:
        for f in fails:
            print('SEC-FIXTURE FAIL %s' % f)
        print('SEC-FIXTURE 不成立')
        return 1
    print('SEC-FIXTURE OK 夹具成立（主图+透明遮罩+深度+语义遮罩+MaskedAreas+XMP+预览 ⇒ '
          '本方枚举、exiftool、Adobe dng_validate 三家都认）')
    return 0


def main(argv):
    if len(argv) < 2 or argv[1].startswith('-'):
        # 第一版没这道闸：`--help` 被当成输出路径 ⇒ 在仓库根写出名为 `--help` 的 DNG 和一张预览 JPEG。
        print(__doc__)
        return 2
    path = argv[1]
    dvp = argv[2] if len(argv) > 2 else ''
    try:
        info = build(path)
    except Exception as exc:
        print('SEC-FIXTURE FAIL 写不出: %s' % exc)
        return 2
    print('SEC-FIXTURE 已写 %s（%d 字节，IFD0=%d）' % (path, info['bytes'], info['ifdOffs'][0]))
    return selfcheck(path, dvp)


if __name__ == '__main__':
    sys.exit(main(sys.argv))
