# -*- coding: utf-8 -*-
"""生成 HEIC 增益图 **grid 分块 + idat 承载** 合成测试件（供 verify-gainmap-isobmff.ps1 的 §1d 使用）。

用法：
    python _make-synth-heic-gridgainmap.py --ffmpeg <ffmpeg.exe> --seine <seine_sdr_gainmap_srgb.avif> --outdir <dir>

产出：
  · synth_heic_grid_gainmap.heic
      底图   = 单个 hvc1 item（64x48，mdat，construction_method=0）
      增益图 = **2x2 grid**（4 个 hvc1 瓦片 64x48，mdat），grid 载荷声明整幅 128x96
      tmap 载荷与 grid 载荷都放在 **meta/idat**（construction_method=1）

为什么是合成件（不是真实 iPhone 件）：真实件有 30 个底图瓦片 + 4 个增益图瓦片 + 43 项元数据，
体积 7.4 MB，不适合入仓；本件以最小结构复现**两条此前未覆盖的容器事实**：
  ① `iloc` construction_method=1（数据在 meta/idat）—— 旧实现只支持 0 ⇒ 整条增益图 fail-closed；
  ② 增益图以 `grid` 派生项分块承载 —— 旧实现要求 dimg 目标恰是一个 av01/hvc1/hev1 item ⇒ 候选 0。
结构方向与真实件一致（iref 方向）；瓦片复用同一段 ffmpeg/libx265 编出的真实 HEVC 码流。

构件来源：
  · HEVC 码流 + 参数集  <- ffmpeg（libx265 编一个 64x48 单帧，Annex-B）
  · tmap 载荷（ISO 21496-1） <- 库内 libavif 样本 seine_sdr_gainmap_srgb.avif（真实产物）
"""
import argparse, os, struct, subprocess, sys


# ───────────────────────── box 工具 ─────────────────────────

def boxes(data, start, end):
    p = start
    while p + 8 <= end:
        size = struct.unpack('>I', data[p:p + 4])[0]
        typ = data[p + 4:p + 8].decode('latin1')
        hdr = 8
        if size == 1:
            size = struct.unpack('>Q', data[p + 8:p + 16])[0]
            hdr = 16
        elif size == 0:
            size = end - p
        if size < hdr or p + size > end:
            break
        yield typ, p, size, hdr
        p += size


def box(data, path, start, end):
    for want in path:
        hit = None
        for typ, p, size, hdr in boxes(data, start, end):
            if typ == want:
                hit = (p, size, hdr); break
        if hit is None:
            return None
        p, size, hdr = hit
        start, end = p + hdr + (4 if want == 'meta' else 0), p + size
    return hit


def parse_iloc_v0(data, start):
    ver = data[start]
    p = start + 4
    b0, b1 = data[p], data[p + 1]; p += 2
    off_size, len_size, base_size = b0 >> 4, b0 & 0xF, b1 >> 4
    if ver < 2:
        count = struct.unpack('>H', data[p:p + 2])[0]; p += 2; id_size = 2
    else:
        count = struct.unpack('>I', data[p:p + 4])[0]; p += 4; id_size = 4
    out = {}
    for _ in range(count):
        iid = int.from_bytes(data[p:p + id_size], 'big'); p += id_size
        if ver in (1, 2):
            p += 2
        p += 2
        base = int.from_bytes(data[p:p + base_size], 'big') if base_size else 0
        p += base_size
        ec = struct.unpack('>H', data[p:p + 2])[0]; p += 2
        exts = []
        for _ in range(ec):
            o = int.from_bytes(data[p:p + off_size], 'big') if off_size else 0; p += off_size
            l = int.from_bytes(data[p:p + len_size], 'big') if len_size else 0; p += len_size
            exts.append((o, l))
        out[iid] = (base, exts)
    return out


def slice_item(data, loc):
    base, exts = loc
    buf = bytearray()
    for o, l in exts:
        buf += data[base + o: base + o + l]
    return bytes(buf)


def full_box(typ, payload, ver=0, flags=0):
    return struct.pack('>I', 8 + 4 + len(payload)) + typ.encode() + struct.pack('>I', (ver << 24) | flags) + payload


def plain_box(typ, payload):
    return struct.pack('>I', 8 + len(payload)) + typ.encode() + payload


# ───────────────────────── HEVC 工具（与 _make-synth-heic-gainmap.py 同源）─────────────────────────

def split_annexb(d):
    idx, nals = 0, []
    while True:
        cands = [x for x in (d.find(b'\x00\x00\x00\x01', idx), d.find(b'\x00\x00\x01', idx)) if x >= 0]
        if not cands:
            break
        s = min(cands)
        sc = 4 if d[s:s + 4] == b'\x00\x00\x00\x01' else 3
        nxt = min([x for x in (d.find(b'\x00\x00\x00\x01', s + sc), d.find(b'\x00\x00\x01', s + sc)) if x >= 0]
                  + [len(d)])
        nals.append((s + sc, nxt))
        idx = s + sc
    return [((d[a] >> 1) & 0x3F, d[a:b]) for a, b in nals]


def build_hvcc(paramsets, length_size=4):
    hdr = bytearray(23)
    hdr[0] = 1
    hdr[1] = 0x01
    hdr[2:6] = b'\x60\x00\x00\x00'
    hdr[6:12] = b'\x90\x00\x00\x00\x00\x00'
    hdr[12] = 120
    hdr[13], hdr[14] = 0xF0, 0x00
    hdr[15] = 0xFC
    hdr[16] = 0xFD
    hdr[17] = 0xF8
    hdr[18] = 0xF8
    hdr[19], hdr[20] = 0x00, 0x00
    hdr[21] = 0xFC | (length_size - 1)
    hdr[22] = len(paramsets)
    body = bytearray(hdr)
    for nal_type, payload in paramsets:
        body.append(0x80 | nal_type)
        body += struct.pack('>H', 1)
        body += struct.pack('>H', len(payload)) + payload
    return plain_box('hvcC', bytes(body))


def to_length_prefixed(annexb, length_size=4):
    out = bytearray()
    for _, payload in split_annexb(annexb):
        out += len(payload).to_bytes(length_size, 'big') + payload
    return bytes(out)


# ───────────────────────── 主流程 ─────────────────────────

def build(out_path, hvcc, tile_item, payload):
    """组装 grid 增益图 HEIC。

    项：1=hvc1(底图) 2=tmap 3=grid 4..7=hvc1(瓦片)
    iref：dimg 2 -> [1,3]；dimg 3 -> [4,5,6,7]
    iloc：1,4..7 = method 0（mdat 绝对偏移）；2,3 = method 1（idat 相对偏移）
    """
    def infe(iid, itype):
        return full_box('infe', struct.pack('>HH', iid, 0) + itype.encode() + b'\x00', ver=2)

    iinf = full_box('iinf', struct.pack('>H', 7)
                    + infe(1, 'hvc1') + infe(2, 'tmap') + infe(3, 'grid')
                    + infe(4, 'hvc1') + infe(5, 'hvc1') + infe(6, 'hvc1') + infe(7, 'hvc1'))
    pitm = full_box('pitm', struct.pack('>H', 1))
    hdlr = full_box('hdlr', struct.pack('>I', 0) + b'pict' + b'\x00' * 12 + b'\x00')
    dimg_tmap = plain_box('dimg', struct.pack('>HH', 2, 2) + struct.pack('>HH', 1, 3))
    dimg_grid = plain_box('dimg', struct.pack('>HH', 3, 4) + struct.pack('>HHHH', 4, 5, 6, 7))
    iref = full_box('iref', dimg_tmap + dimg_grid)

    ispe_tile = full_box('ispe', struct.pack('>II', 64, 48))
    ispe_grid = full_box('ispe', struct.pack('>II', 128, 96))
    ipco = plain_box('ipco', hvcc + ispe_tile + ispe_grid)
    entries = []
    for iid in (1, 4, 5, 6, 7):
        entries.append(struct.pack('>HB', iid, 2) + bytes([0x81, 0x82]))   # hvcC + ispe(64x48)
    entries.append(struct.pack('>HB', 2, 1) + bytes([0x83]))                # tmap -> ispe(128x96)
    entries.append(struct.pack('>HB', 3, 1) + bytes([0x83]))                # grid -> ispe(128x96)
    ipma = full_box('ipma', struct.pack('>I', 7) + b''.join(entries))
    iprp = plain_box('iprp', ipco + ipma)

    # grid 载荷：version 0 / flags 1（带宽高）/ rows-1=1（2 行）/ cols-1=1（2 列）/ 128x96
    grid_payload = struct.pack('>BB', 0, 1) + struct.pack('>BB', 1, 1) + struct.pack('>II', 128, 96)

    def iloc_bytes(o_base, o_tiles):
        body = struct.pack('>H', 7)
        # method 0 项：1（底图）、4..7（瓦片）
        for iid, ln in ((1, len(tile_item)),) + tuple((i, len(tile_item)) for i in (4, 5, 6, 7)):
            body += struct.pack('>HHH', iid, 0, 0) + struct.pack('>I', 0)
            body += struct.pack('>H', 1) + struct.pack('>II', o_base if iid == 1 else o_tiles[iid - 4], ln)
        # method 1 项：2（tmap，idat 偏移 0）、3（grid，idat 偏移 len(payload)）
        body += struct.pack('>HHH', 2, 1, 0) + struct.pack('>I', 0)
        body += struct.pack('>H', 1) + struct.pack('>II', 0, len(payload))
        body += struct.pack('>HHH', 3, 1, 0) + struct.pack('>I', 0)
        body += struct.pack('>H', 1) + struct.pack('>II', len(payload), len(grid_payload))
        return full_box('iloc', struct.pack('>BB', 0x44, 0x40) + body, ver=1)

    idat = plain_box('idat', payload + grid_payload)
    ftyp = plain_box('ftyp', b'heic' + struct.pack('>I', 0) + b'mif1' + b'heic' + b'tmap')

    # 先算 meta 长度（method 0 偏移值不影响 iloc 盒长 ⇒ 可先占位）
    meta_probe = full_box('meta', hdlr + pitm + iinf + iref + iprp + iloc_bytes(0, {k: 0 for k in range(4)}) + idat)
    o_base = len(ftyp) + len(meta_probe) + 8            # mdat 载荷起点
    o_tiles = {k: o_base + len(tile_item) + k * len(tile_item) for k in range(4)}
    meta = full_box('meta', hdlr + pitm + iinf + iref + iprp + iloc_bytes(o_base, o_tiles) + idat)
    # 验证两次长度一致（否则偏移全错）
    if len(meta) != len(meta_probe):
        raise SystemExit("✗ meta 长度在占位/实值两次计算下不一致")
    blob = ftyp + meta + plain_box('mdat', tile_item + tile_item * 4)
    open(out_path, 'wb').write(blob)
    return len(blob)


def build_aux_grid(out_path, hvcc, tile_item, xmp):
    """组装 **auxC(hdrgainmap) + grid** 变体（手机私有件形态）。

    与 `build()` 的差别：
      · 没有 tmap item；`grid` 直接挂在 `auxC` 属性上，`iref auxl: 3 -> 1`；
      · grid 载荷 `flags=0`，但尾部写 2×u16 宽高（128/96）—— 锁 C7；
      · 带一个 `mime`(XMP) item（XMP 头room=2.0），让产品链在**无 Exif**时走 XMP；
      · `dimg: 3 -> [4..7]`，`ipma 3 = [ispe_grid, auxC]`。
    """
    def infe(iid, itype):
        return full_box('infe', struct.pack('>HH', iid, 0) + itype.encode() + b'\x00', ver=2)

    iinf = full_box('iinf', struct.pack('>H', 7)
                    + infe(1, 'hvc1') + infe(2, 'mime') + infe(3, 'grid')
                    + infe(4, 'hvc1') + infe(5, 'hvc1') + infe(6, 'hvc1') + infe(7, 'hvc1'))
    pitm = full_box('pitm', struct.pack('>H', 1))
    hdlr = full_box('hdlr', struct.pack('>I', 0) + b'pict' + b'\x00' * 12 + b'\x00')
    dimg_grid = plain_box('dimg', struct.pack('>HH', 3, 4) + struct.pack('>HHHH', 4, 5, 6, 7))
    auxl = plain_box('auxl', struct.pack('>HHH', 3, 1, 1))
    iref = full_box('iref', dimg_grid + auxl)

    ispe_tile = full_box('ispe', struct.pack('>II', 64, 48))
    ispe_grid = full_box('ispe', struct.pack('>II', 128, 96))
    auxc = full_box('auxC', b'urn:com:apple:photo:2020:aux:hdrgainmap\x00')
    ipco = plain_box('ipco', hvcc + ispe_tile + ispe_grid + auxc)
    entries = []
    for iid in (1, 4, 5, 6, 7):
        entries.append(struct.pack('>HB', iid, 2) + bytes([0x81, 0x82]))   # hvcC + ispe(64x48)
    entries.append(struct.pack('>HB', 2, 0))                                # mime：无属性
    entries.append(struct.pack('>HB', 3, 2) + bytes([0x83, 0x84]))          # grid：ispe(128x96) + auxC
    ipma = full_box('ipma', struct.pack('>I', 7) + b''.join(entries))
    iprp = plain_box('iprp', ipco + ipma)

    # C7：flags bit0=0，但尾部 2×u16 = 128/96（非标私货；标准 flags=1 才写 u32）
    grid_payload = (struct.pack('>BB', 0, 0) + struct.pack('>BB', 1, 1)
                    + struct.pack('>HH', 128, 96))

    def iloc_bytes(o_base, o_tiles):
        body = struct.pack('>H', 7)
        for iid, ln in ((1, len(tile_item)),) + tuple((i, len(tile_item)) for i in (4, 5, 6, 7)):
            body += struct.pack('>HHH', iid, 0, 0) + struct.pack('>I', 0)
            body += struct.pack('>H', 1) + struct.pack('>II', o_base if iid == 1 else o_tiles[iid - 4], ln)
        # method 1：2（mime/XMP，idat 偏移 0）、3（grid，idat 偏移 len(xmp)）
        body += struct.pack('>HHH', 2, 1, 0) + struct.pack('>I', 0)
        body += struct.pack('>H', 1) + struct.pack('>II', 0, len(xmp))
        body += struct.pack('>HHH', 3, 1, 0) + struct.pack('>I', 0)
        body += struct.pack('>H', 1) + struct.pack('>II', len(xmp), len(grid_payload))
        return full_box('iloc', struct.pack('>BB', 0x44, 0x40) + body, ver=1)

    idat = plain_box('idat', xmp + grid_payload)
    ftyp = plain_box('ftyp', b'heic' + struct.pack('>I', 0) + b'mif1' + b'heic')
    meta_probe = full_box('meta', hdlr + pitm + iinf + iref + iprp
                          + iloc_bytes(0, {k: 0 for k in range(4)}) + idat)
    o_base = len(ftyp) + len(meta_probe) + 8
    o_tiles = {k: o_base + len(tile_item) + k * len(tile_item) for k in range(4)}
    meta = full_box('meta', hdlr + pitm + iinf + iref + iprp
                    + iloc_bytes(o_base, o_tiles) + idat)
    if len(meta) != len(meta_probe):
        raise SystemExit("✗ aux grid：meta 长度在占位/实值两次计算下不一致")
    blob = ftyp + meta + plain_box('mdat', tile_item + tile_item * 4)
    open(out_path, 'wb').write(blob)
    return len(blob)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ffmpeg', required=True)
    ap.add_argument('--seine', required=True, help='seine_sdr_gainmap_srgb.avif（取 tmap 载荷）')
    ap.add_argument('--outdir', required=True)
    a = ap.parse_args()

    os.makedirs(a.outdir, exist_ok=True)
    tmp = os.path.join(a.outdir, '_hevc_grid_src.h265')
    r = subprocess.run([a.ffmpeg, '-y', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=s=64x48',
                        '-frames:v', '1', '-c:v', 'libx265', '-f', 'hevc', tmp], capture_output=True)
    if r.returncode != 0:
        print(f"✗ ffmpeg 生成 HEVC 失败: {r.stderr.decode('utf-8', 'replace')[:300]}")
        return 1
    annexb = open(tmp, 'rb').read()
    os.remove(tmp)

    nals = split_annexb(annexb)
    paramsets = [(t, p) for t, p in nals if t in (32, 33, 34)]
    print(f"HEVC 码流 {len(annexb)} B, NAL {len(nals)} 个, 参数集 {[t for t, _ in paramsets]}")

    hvcc = build_hvcc(paramsets)
    item = to_length_prefixed(annexb)

    # tmap 载荷（从库内真实 AVIF 样本取）
    seine = open(a.seine, 'rb').read()
    sm = box(seine, ['meta'], 0, len(seine))
    sb_s, sb_e = sm[0] + sm[2] + 4, sm[0] + sm[1]
    sil = box(seine, ['iloc'], sb_s, sb_e)
    slocs = parse_iloc_v0(seine, sil[0] + sil[2])
    siinf = box(seine, ['iinf'], sb_s, sb_e)
    p = siinf[0] + siinf[2] + 4
    p += 2 if seine[p] == 0 else 4
    tmap_id = None
    for typ, bp, size, hdr in boxes(seine, p, siinf[0] + siinf[1]):
        if typ == 'infe':
            q = bp + hdr + 4
            iid = struct.unpack('>H', seine[q:q + 2])[0]
            if seine[q + 4:q + 8].decode('latin1') == 'tmap':
                tmap_id = iid
    if tmap_id is None:
        print("✗ 未在样本里找到 tmap item")
        return 1
    payload = slice_item(seine, slocs[tmap_id])
    print(f"tmap 载荷 {len(payload)} B（item {tmap_id}）")

    p1 = os.path.join(a.outdir, 'synth_heic_grid_gainmap.heic')
    n1 = build(p1, hvcc, item, payload)
    print(f"✓ {p1}  {n1} B（grid 2x2 瓦片 + idat 承载 / construction_method=1）")

    # 2026-11-15：auxC(hdrgainmap) + grid + flags=0 尾宽高 + 无 Exif（手机私有件形态）
    xmp = ('<?xpacket begin="\ufeff" id="W5M0MpCehiHzreSzNTczkc9d"?>'
           '<x:xmpmeta xmlns:x="adobe:ns:meta/">'
           '<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
           '<rdf:Description rdf:about="" xmlns:HDRGainMap="http://ns.apple.com/HDRGainMap/1.0/" '
           'xmlns:xmp="http://ns.adobe.com/xap/1.0/">'
           '<HDRGainMap:HDRGainMapHeadroom>2.0</HDRGainMap:HDRGainMapHeadroom>'
           '</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end="w"?>').encode('utf-8')
    p2 = os.path.join(a.outdir, 'synth_heic_grid_aux_gainmap.heic')
    n2 = build_aux_grid(p2, hvcc, item, xmp)
    print(f"✓ {p2}  {n2} B（auxC hdrgainmap + grid 2x2 + flags=0 尾宽高 128x96 + XMP headroom=2.0）")

    # 2026-11-15：灰瓦片变体 —— 增益中位 = 128 ⇒ 锁「对称/128 基」B 口径（testsrc2 件 p0.5≈5 只走 A）。
    tmp_gray = os.path.join(a.outdir, '_hevc_grid_gray.h265')
    rg = subprocess.run([a.ffmpeg, '-y', '-v', 'error', '-f', 'lavfi', '-i', 'color=c=0x808080:s=64x48',
                         '-frames:v', '1', '-c:v', 'libx265', '-f', 'hevc', tmp_gray], capture_output=True)
    if rg.returncode != 0:
        print(f"✗ ffmpeg 生成灰瓦片失败: {rg.stderr.decode('utf-8', 'replace')[:300]}")
        return 1
    gray_annexb = open(tmp_gray, 'rb').read()
    os.remove(tmp_gray)
    gray_nals = split_annexb(gray_annexb)
    gray_paramsets = [(t, p) for t, p in gray_nals if t in (32, 33, 34)]
    gray_hvcc = build_hvcc(gray_paramsets)
    gray_item = to_length_prefixed(gray_annexb)
    p3 = os.path.join(a.outdir, 'synth_heic_grid_aux_gainmap_b.heic')
    n3 = build_aux_grid(p3, gray_hvcc, gray_item, xmp)
    print(f"✓ {p3}  {n3} B（同结构，灰瓦片 0x80 ⇒ p0.5=128 走 B 口径）")

    # 无 XMP 变体：同结构但 idat 里没有 XMP ⇒ 未登记 URN 必须仍判「无增益图」（候选的语义门有牙）。
    p4 = os.path.join(a.outdir, 'synth_heic_grid_aux_noxmp.heic')
    n4 = build_aux_grid(p4, hvcc, item, b'')
    print(f"✓ {p4}  {n4} B（无 XMP：未登记 URN 的负控载体）")
    return 0


if __name__ == '__main__':
    sys.exit(main())
