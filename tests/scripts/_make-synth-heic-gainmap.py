# -*- coding: utf-8 -*-
"""生成 HEIC 增益图**合成测试件**（供 verify-gainmap-isobmff.ps1 使用）。

用法：
    python _make-synth-heic-gainmap.py --ffmpeg <ffmpeg.exe> --seine <seine_sdr_gainmap_srgb.avif> --outdir <dir>

产出两个件：
  · synth_heic_gainmap.heic        正向：hvc1 增益图 item 是**真实 HEVC 码流**（长度前缀 + 真 hvcC）
  · synth_heic_gainmap_fakehevc.heic  负控：hvcC 存在但增益图 item 字节是 **AV1 OBU**
                                     ⇒ 长度前缀必然断链 ⇒ 探针必须 fail-closed 点名（证明转换不是无脑照搬）

为什么是合成件：真实 HEIC 增益图素材搜寻三条线全断（imaging.org 付费墙 / toGainMapHDR 的 DJI 样本
无 tmap / Awesome-Gain-Maps 只有 JPEG）。但 hvc1 解码路径**不需要真素材**也能覆盖 —— 只要容器合法、
增益图 item 是真实 HEVC 码流。⚠ 它覆盖的是「**解码路径**」，**不代表**拿到了真实 HEIC 增益图素材。

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


def parse_iloc(data, start):
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


# ───────────────────────── HEVC 工具 ─────────────────────────

def split_annexb(d):
    """按起始码切 NAL，返回 [(type, payload_bytes)]。"""
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
    """由参数集构造 hvcC（HEVCDecoderConfigurationRecord）。

    ⚠ 只填**被本仓解析器与 ffmpeg 用到**的字段：configurationVersion / lengthSizeMinusOne / numOfArrays /
      参数集。profile-tier-level 等字段填常见占位值（ffmpeg 走 `-f hevc` 裸流，参数集在流内，不读它们）。
    """
    hdr = bytearray(23)
    hdr[0] = 1                                     # configurationVersion
    hdr[1] = 0x01                                  # profile_space=0, tier=0, profile_idc=1 (Main)
    hdr[2:6] = b'\x60\x00\x00\x00'                 # general_profile_compatibility_flags
    hdr[6:12] = b'\x90\x00\x00\x00\x00\x00'        # general_constraint_indicator_flags
    hdr[12] = 120                                  # general_level_idc (L4.0)
    hdr[13], hdr[14] = 0xF0, 0x00                  # reserved + min_spatial_segmentation_idc
    hdr[15] = 0xFC                                 # reserved + parallelismType
    hdr[16] = 0xFD                                 # reserved + chromaFormat=1 (4:2:0)
    hdr[17] = 0xF8                                 # reserved + bitDepthLumaMinus8=0
    hdr[18] = 0xF8                                 # reserved + bitDepthChromaMinus8=0
    hdr[19], hdr[20] = 0x00, 0x00                  # avgFrameRate
    hdr[21] = 0xFC | (length_size - 1)             # cfr/numTemporalLayers/temporalIdNested + lengthSizeMinusOne
    hdr[22] = len(paramsets)                       # numOfArrays
    body = bytearray(hdr)
    for nal_type, payload in paramsets:
        body.append(0x80 | nal_type)               # array_completeness=1 | NAL_unit_type
        body += struct.pack('>H', 1)               # numNalus
        body += struct.pack('>H', len(payload)) + payload
    return plain_box('hvcC', bytes(body))


def to_length_prefixed(annexb, length_size=4):
    """Annex-B → 长度前缀（每个 NAL 前放 length_size 字节大端长度）。"""
    out = bytearray()
    for _, payload in split_annexb(annexb):
        out += len(payload).to_bytes(length_size, 'big') + payload
    return bytes(out)


# ───────────────────────── 主流程 ─────────────────────────

# ⚠⚠ **不要再试「无 `hvcC` 的 `hev1`」这条路**（2026-09-20 已**证伪**，省得重犯）：
#   ISO/IEC 14496-15 里 `hev1` 只是**允许参数集出现在样本流内**，但 **`hvcC` 仍是必需的 sample entry**
#   —— `lengthSizeMinusOne` 等**容器级**配置只在它里面（没有它连「长度字段几个字节」都不知道）。
#   ⇒ **没有 `hvcC` 的文件是畸形件**，不是「合法但未支持」⇒ 探针对它的 **fail-closed 是正确行为，不是缺口**。
#   ⚠ 我一度按「`hev1` 可无 `hvcC`」的错误假设造了变体、并准备去改产品代码，**查规范后撤回**。
#   **教训：先查规范，再动手**；「我以为的合法输入」必须用规范原文确认，别用直觉。
def build(ffmpeg, seine, out_path, gainmap_item_bytes, hvcc_box, w, h, payload):
    def infe(iid, itype):
        return full_box('infe', struct.pack('>HH', iid, 0) + itype.encode() + b'\x00', ver=2)

    iinf = full_box('iinf', struct.pack('>H', 3) + infe(1, 'hvc1') + infe(2, 'tmap') + infe(3, 'hvc1'))
    pitm = full_box('pitm', struct.pack('>H', 1))
    hdlr = full_box('hdlr', struct.pack('>I', 0) + b'pict' + b'\x00' * 12 + b'\x00')
    iref = full_box('iref', plain_box('dimg', struct.pack('>HHH', 2, 1, 3)))

    ispe = full_box('ispe', struct.pack('>II', w, h))
    ipco = plain_box('ipco', hvcc_box + ispe)
    ipma = full_box('ipma', struct.pack('>I', 2) + struct.pack('>HB', 1, 2) + b'\x81\x82'
                                       + struct.pack('>HB', 3, 2) + b'\x81\x82')
    iprp = plain_box('iprp', ipco + ipma)

    base = gainmap_item_bytes                        # 底图与增益图用同一份码流（本件只覆盖解码路径）

    def iloc_bytes(o1, o2, o3):
        body = struct.pack('>H', 3)
        for iid, off, ln in ((1, o1, len(base)), (2, o2, len(payload)), (3, o3, len(gainmap_item_bytes))):
            body += struct.pack('>HH', iid, 0) + struct.pack('>H', 1) + struct.pack('>II', off, ln)
        return full_box('iloc', struct.pack('>BB', 0x44, 0x00) + body)

    ftyp = plain_box('ftyp', b'heic' + struct.pack('>I', 0) + b'mif1' + b'heic' + b'tmap')
    meta_len = len(full_box('meta', hdlr + pitm + iinf + iref + iprp + iloc_bytes(0, 0, 0)))
    o1 = len(ftyp) + meta_len + 8
    o2 = o1 + len(base)
    o3 = o2 + len(payload)
    meta = full_box('meta', hdlr + pitm + iinf + iref + iprp + iloc_bytes(o1, o2, o3))
    blob = ftyp + meta + plain_box('mdat', base + payload + gainmap_item_bytes)
    open(out_path, 'wb').write(blob)
    return len(blob)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ffmpeg', required=True)
    ap.add_argument('--seine', required=True, help='seine_sdr_gainmap_srgb.avif（取 tmap 载荷）')
    ap.add_argument('--outdir', required=True)
    a = ap.parse_args()

    os.makedirs(a.outdir, exist_ok=True)
    tmp = os.path.join(a.outdir, '_hevc_src.h265')
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
    w, h = 64, 48

    # tmap 载荷（从库内真实 AVIF 样本取）
    seine = open(a.seine, 'rb').read()
    sm = box(seine, ['meta'], 0, len(seine))
    sb_s, sb_e = sm[0] + sm[2] + 4, sm[0] + sm[1]
    sil = box(seine, ['iloc'], sb_s, sb_e)
    slocs = parse_iloc(seine, sil[0] + sil[2])
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

    # 负控用的 AV1 增益图字节（从样本的增益图 item 取）
    av1_id = None
    for typ, bp, size, hdr in boxes(seine, p, siinf[0] + siinf[1]):
        if typ == 'infe':
            q = bp + hdr + 4
            iid = struct.unpack('>H', seine[q:q + 2])[0]
            if seine[q + 4:q + 8].decode('latin1') == 'av01':
                av1_id = iid
    av1_item = slice_item(seine, slocs[av1_id]) if av1_id else None
    print(f"AV1 增益图 item {len(av1_item) if av1_item else -1} B（负控用）")

    p1 = os.path.join(a.outdir, 'synth_heic_gainmap.heic')
    n1 = build(a.ffmpeg, a.seine, p1, item, hvcc, w, h, payload)
    print(f"✓ {p1}  {n1} B（正向：真 HEVC 码流 + 真 hvcC）")

    if av1_item:
        p2 = os.path.join(a.outdir, 'synth_heic_gainmap_fakehevc.heic')
        n2 = build(a.ffmpeg, a.seine, p2, av1_item, hvcc, w, h, payload)
        print(f"✓ {p2}  {n2} B（负控：hvcC 在，但 item 字节是 AV1 ⇒ 长度前缀必断链）")
    return 0


if __name__ == '__main__':
    sys.exit(main())
