# -*- coding: utf-8 -*-
"""生成 **Apple aux 形态**（urn:com:apple:photo:2020:aux:hdrgainmap）HEIC 增益图合成测试件。

规格依据：SPEC.md（tests/output/_tmp/aux-spec-20261110/SPEC.md §5）+ raw/ 证据：
  · Apple 官方（E5）：元数据 = 主图 Exif item 的 AppleMakerNote（keys 33/48）；aux item =
    8-bit 单通道灰度、主图 1/4 尺寸；URN = urn:com:apple:photo:2020:aux:hdrgainmap。
  · exiftool Apple.pm（E7）：0x0021=HDRHeadroom、0x0030=HDRGain，均 rational64s。
  · MakerNotes.pm（随包 13.61 实读）：Apple MN 判定 = 内容以 "Apple iOS\0" 开头，
    IFD 在 MakerNote 起点后 14 字节（`Start => $valuePtr + 14`）。
  · headroom 公式（johncf metadata.py / Apple 官方，maker33≥1 线性段）：
    stops = -0.303·maker48 + 2.303 ⇒ headroom = 2^stops。本件 maker33=1.5、maker48=0.5
    ⇒ stops = 2.1515 ⇒ headroom ≈ 4.4536×（落在 SPEC 要求的 2.0~4.5 线性段）。

用法：
    python _make-synth-aux-gainmap.py --ffmpeg <ffmpeg.exe> --outdir <dir>

产出五件：
  · aux_apple_gainmap.heic    A 件（正控）：primary(hvc1 64x48) + aux(hvc1 灰度 32x24,
                              auxC URN=2020 hdrgainmap) + Exif(AppleMakerNote 33/48)
                              + iref auxl(aux→primary) + iref cdsc(Exif→primary)
  · aux_apple_noaux.heic      B 件（负控）：同 A 但去掉 aux item / auxC / Exif = 纯 SDR
  · aux_apple_otherurn.heic   C 件（语义反控）：auxC URN = urn:com:example:other
                              （预期按无增益图处理）
  · aux_iso21496.heic         D 件（AU2-X1 正控）：primary(hvc1 64x48) + aux(hvc1 灰度 32x24,
                              auxC URN=urn:iso:std:iso:ts:21496:-1，item 数据前置
                              version(0) + ISO 21496-1 GainMapMetadata 62 B + HEVC 流)
                              + iref auxl；⇒ 探针结构尝试成立（Determinate=true，
                              ISO 2^logBoost 公式族，headroom 4×）
  · aux_iso21496_bad.heic     D-bad（AU2-X1 负控）：同 D 但 version 段破坏（0→1）
                              ⇒ 探针 fail-closed 点名「aux-21496 元数据结构未证实」

⚠ 合成件覆盖的是「**解码路径**」（容器结构 → 探针提取 → exiftool 元数据 → Apple 公式施加；
  AU2-X1 起含 aux-21496 前置元数据结构尝试），**不代表**真实 iPhone 产物的全部语义被验证
  （真机件仍缺，见 verify-gainmap-isobmff.ps1 头部登记）。
"""
import argparse
import os
import struct
import subprocess
import sys

URN_APPLE = "urn:com:apple:photo:2020:aux:hdrgainmap"
URN_OTHER = "urn:com:example:other"
URN_ISO = "urn:iso:std:iso:ts:21496:-1"          # D 件（AU2-X1）：aux-21496 防御性形态
MAKER33_NUM, MAKER33_DEN = 3, 2      # HDRHeadroom = 1.5（≥1.0 ⇒ 公式第二分支）
MAKER48_NUM, MAKER48_DEN = 1, 2      # HDRGain = 0.5（>0.01 ⇒ 线性段）
# D 件 ISO 21496 元数据参数（与 A 件同族：min=0 / gamma=1；max 取整数族 log2(4)=2 ⇒ headroom 4×。
# A 件的 2.1515 是 Apple stops 公式反推值，ISO 语法无此约束 ⇒ 取干净整数使方向判定与峰值亮度可复核）。


# ───────────────────────── box 工具（与 _make-synth-heic-gainmap.py 同族） ─────────────────────────

def full_box(typ, payload, ver=0, flags=0):
    return struct.pack('>I', 8 + 4 + len(payload)) + typ.encode() + struct.pack('>I', (ver << 24) | flags) + payload


def plain_box(typ, payload):
    return struct.pack('>I', 8 + len(payload)) + typ.encode() + payload


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
    """由参数集构造 hvcC（同 _make-synth-heic-gainmap.py：只填被解析器/ffmpeg 用到的字段）。"""
    hdr = bytearray(23)
    hdr[0] = 1
    hdr[1] = 0x01
    hdr[2:6] = b'\x60\x00\x00\x00'
    hdr[6:12] = b'\x90\x00\x00\x00\x00\x00'
    hdr[12] = 120
    hdr[13], hdr[14] = 0xF0, 0x00
    hdr[15] = 0xFC
    hdr[16] = 0xFC                              # chromaFormat 由调用方按实际补（见 encode 参数）
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


def set_chroma_format(hvcc_box, chroma_format_idc):
    """改写 hvcC 第 17 字节的 chromaFormat（低 5 位）：0=mono 1=4:2:0。"""
    b = bytearray(hvcc_box)
    b[8 + 16] = (b[8 + 16] & 0xE0) | (chroma_format_idc & 0x1F)
    return bytes(b)


def to_length_prefixed(annexb, length_size=4):
    out = bytearray()
    for _, payload in split_annexb(annexb):
        out += len(payload).to_bytes(length_size, 'big') + payload
    return bytes(out)


# ───────────────────────── Exif / AppleMakerNote 合成 ─────────────────────────

def build_apple_exif():
    """构造 Exif item 载荷：4B tiff_header_offset + TIFF(IFD0 → ExifIFD → MakerNote)。

    MakerNote 形态按随包 exiftool 13.61 的 MakerNotes.pm 判定：
      · 前 10 字节 = "Apple iOS\\0"；IFD 从 MakerNote 起点后 14 字节开始（Start = valuePtr + 14）；
      · IFD 内 offset **相对 MakerNote 起点**（exiftool `Base => $start - 14`——实测校准，见本函数
        尾注：若该基准读不出 tag，依次试「相对 TIFF 头」「相对 IFD」两种变体并以 exiftool 读数定案）。
    tag 0x0021 = HDRHeadroom（rational64s 3/2 = 1.5）；0x0030 = HDRGain（rational64s 1/2 = 0.5）。
    """
    # ── MakerNote（自成一体，先算好再嵌进 Exif IFD）──
    # header 14B + count 2B + 3×entry 36B + next 4B = 56B 处放值区
    mn = bytearray()
    mn += b'Apple iOS\x00'                     # 10B 签名（判定条件）
    mn += struct.pack('>HH', 0x0001, 0x0000)   # 4B：真机件此处的版本/标志占位（exiftool 不读）
    ifd_off = 14
    off_v33 = 14 + 2 + 3 * 12 + 4              # 56
    off_v48 = off_v33 + 8                      # 64
    mn += struct.pack('>H', 3)
    # 0x0001 MakerNoteVersion，LONG(4) count=1，值 inline = 1
    mn += struct.pack('>HHI', 0x0001, 4, 1) + struct.pack('>I', 1)
    # 0x0021 HDRHeadroom，RATIONAL(5) count=1，offset（相对 MakerNote 起点）
    mn += struct.pack('>HHI', 0x0021, 5, 1) + struct.pack('>I', off_v33)
    # 0x0030 HDRGain，RATIONAL(5) count=1，offset
    mn += struct.pack('>HHI', 0x0030, 5, 1) + struct.pack('>I', off_v48)
    mn += struct.pack('>I', 0)                 # next IFD = 0
    assert len(mn) == off_v33, f"MakerNote 布局失配：{len(mn)} != {off_v33}"
    mn += struct.pack('>II', MAKER33_NUM, MAKER33_DEN)
    mn += struct.pack('>II', MAKER48_NUM, MAKER48_DEN)

    # ── Exif IFD：0x9000 ExifVersion（inline 4B）+ 0x927C MakerNote（offset）──
    # 布局：count 2B + 2×entry 24B + next 4B = 30B；MakerNote 值紧跟其后。
    # ⚠ TIFF 内一切 offset 的基准 = **TIFF 头**（'MM' 位置），不是 IFD 起点（首版少加 8 ⇒
    #   exiftool 读 IFD0 时 Make/ExifIFD/MakerNote 全部错位 ⇒ TIFF 目录零 entry 静默放弃）。
    exif_ifd_len = 2 + 2 * 12 + 4              # 30
    off_mn = 8 + 36 + exif_ifd_len             # 74：IFD0@+8 长 36 ⇒ ExifIFD@TIFF+44，值紧随
    exif_ifd = bytearray()
    exif_ifd += struct.pack('>H', 2)
    exif_ifd += struct.pack('>HHI', 0x9000, 7, 4) + b'0232'
    exif_ifd += struct.pack('>HHI', 0x927C, 7, len(mn)) + struct.pack('>I', off_mn)
    exif_ifd += struct.pack('>I', 0)
    assert len(exif_ifd) == exif_ifd_len
    exif_ifd += mn

    # ── IFD0：0x010F Make="Apple"（offset）+ 0x8769 ExifIFD pointer（LONG inline）──
    # 布局：count 2B + 2×entry 24B + next 4B = 30B；"Apple\0"（6B）与 Exif IFD 排 IFD0 之后。
    ifd0_len = 2 + 2 * 12 + 4                  # 30
    off_make = 8 + ifd0_len                    # 38
    off_exif = off_make + 6                    # 44
    ifd0 = bytearray()
    ifd0 += struct.pack('>H', 2)
    ifd0 += struct.pack('>HHI', 0x010F, 2, 6) + struct.pack('>I', off_make)
    ifd0 += struct.pack('>HHI', 0x8769, 4, 1) + struct.pack('>I', off_exif)
    ifd0 += struct.pack('>I', 0)
    assert len(ifd0) == ifd0_len
    ifd0 += b'Apple\x00'
    ifd0 += exif_ifd

    tiff = b'MM' + struct.pack('>H', 42) + struct.pack('>I', 8) + bytes(ifd0)
    # Exif item 头部：u32(6) + "Exif\0\0" + TIFF。
    # ⚠ 随包 exiftool 13.61 的读法（QuickTime.pm，随包源实测核对）：
    #   `my $n = unpack('N', $buff); $start = 4 + $n;  # skip "Exif\0\0" header if it exists`
    #   ⇒ TIFF 头必须落在 item + 4 + n = +10 处，前缀即 u32(6) + "Exif\0\0"（6B 标识）。
    #   首版按「offset=6 ⇒ TIFF 在 item+6，垫 2B 填充」布局 ⇒ exiftool 的 start=10 落进 TIFF
    #   中段（字节序判定失败）⇒ TIFF 目录零 entry 静默放弃（-v3 实测定位）。
    return struct.pack('>I', 6) + b'Exif\x00\x00' + tiff         # exif_tiff_header_offset = 6（TIFF 头在 item 数据 +6）


def build_iso21496_payload(version=0):
    """构造 D 件（AU2-X1）的 aux item 数据前置段：`version(1B) + ISO 21496-1 GainMapMetadata`。

    与 tmap 载荷**同构**（ISO 21496-1 的元数据语法唯一，载体可插在 tmap item 数据或 aux item
    数据，语法不变——SPEC §7.1 防御性前瞻设计的依据）。布局（独立分母、单通道，与 libavif tmap
    写侧同款）：
        u8 version（AU2-X1 探针的 magic/version 判据位；D-bad 件写 1 破坏它）
        u16 min_version = 0 / u16 writer_version = 0
        u8 flags = 0x40（bit6 useBaseColorSpace；bit7=0 ⇒ 单通道）
        u32×2 baseHdrHeadroom = 0/1
        u32×2 alternateHdrHeadroom = 2/1        （log2(4) = 2 ⇒ headroom 4×）
        每通道 5 对 (N,D)：min 0/1 · max 2/1 · gamma 1/1 · baseOffset 0/1 · alternateOffset 0/1
    总长 = 1 + 2 + 2 + 1 + 16 + 1×40 = 62 B（与读侧 GainMapDecoder 长度闭合公式一致）。
    解析侧复用 JpegContainer.ParseIso21496（off=1 跳过 version 字节，与 tmap 载荷同一调用形态）。
    """
    b = bytearray()
    b += struct.pack('>B', version)
    b += struct.pack('>HH', 0, 0)                            # min_version / writer_version
    b += struct.pack('>B', 0x40)                             # flags: useBaseColorSpace，单通道
    b += struct.pack('>II', 0, 1)                            # baseHdrHeadroom = 0/1
    b += struct.pack('>II', 2, 1)                            # alternateHdrHeadroom = 2/1
    b += struct.pack('>II', 0, 1)                            # gainMapMin = 0/1
    b += struct.pack('>II', 2, 1)                            # gainMapMax = 2/1
    b += struct.pack('>II', 1, 1)                            # gainMapGamma = 1/1
    b += struct.pack('>II', 0, 1)                            # baseOffset = 0/1
    b += struct.pack('>II', 0, 1)                            # alternateOffset = 0/1
    assert len(b) == 62, f"ISO 21496 元数据长度失配：{len(b)} != 62"
    return bytes(b)


# ───────────────────────── HEIC 容器组装 ─────────────────────────

def build_heic(primary_bytes, primary_hvcc, primary_wh,
               aux_bytes, aux_hvcc, aux_wh, urn,
               exif_bytes, primary_colr, aux_colr):
    """组装 HEIC。urn=None 时不含 aux item / auxC / Exif（B 件）。
    D 件（AU2-X1）：urn=URN_ISO 且 exif_bytes=None —— 有 aux item 但无 Exif item
    （aux-21496 形态的元数据前置在 aux item 数据里，不需要 Exif 载体）。"""
    pw, ph = primary_wh
    have_aux = urn is not None
    have_exif = exif_bytes is not None
    aw, ah = aux_wh if have_aux else (0, 0)

    def infe(iid, itype):
        return full_box('infe', struct.pack('>HH', iid, 0) + itype.encode() + b'\x00', ver=2)

    items = [infe(1, 'hvc1')]
    if have_aux:
        items += [infe(2, 'hvc1')]
        if have_exif:
            items += [infe(3, 'Exif')]
    iinf = full_box('iinf', struct.pack('>H', len(items)) + b''.join(items))
    pitm = full_box('pitm', struct.pack('>H', 1))
    hdlr = full_box('hdlr', struct.pack('>I', 0) + b'pict' + b'\x00' * 12 + b'\x00')

    iref_boxes = b''
    if have_aux:
        # auxl：from aux item → to primary（ISO 23008-12 方向）
        # ⚠ 写成 from+count+to 三值形态：ffmpeg 的 mov_read_iref **恒按「有 reference_count」读**，
        #   两值形态会被它解析成「count=1 但 0 个目标」⇒ AVERROR_INVALIDDATA ⇒ "error reading header"
        #   （2026-11-10 实测：B 件无 iref 能读、A/C 件带两值 iref 即拒）。三值形态下本仓探针的
        #   「首值==元素数 ⇒ 带 count」判别法同样收敛（dimg 老合成件同形先例）。
        iref_boxes += plain_box('auxl', struct.pack('>HHH', 2, 1, 1))
        # cdsc：from Exif item → to primary（描述主图的元数据；D 件无 Exif ⇒ 不写）
        if have_exif:
            iref_boxes += plain_box('cdsc', struct.pack('>HHH', 3, 1, 1))
    iref = full_box('iref', iref_boxes) if iref_boxes else b''

    # ispe 挂在各自 item 上（HEIC 主图解码尺寸由 HEVC 流决定，ispe 是容器声明面，
    # 探针 TryFindIspe / grid 拼接消费它）。
    # ⚠ ipco 必须**真的**是 'ipco' box——首版清理冗余时丢了包裹，属性裸拼进 iprp ⇒
    #   探针 ParseIprp 的 `type == "ipco"` 分支永不命中 ⇒ hasGainMap=False（python box 遍历实测定位）。
    ispe_p = full_box('ispe', struct.pack('>II', pw, ph))
    if have_aux:
        auxc = full_box('auxC', urn.encode() + b'\x00')
        ispe_a = full_box('ispe', struct.pack('>II', aw, ah))
        # primary: 1=hvcC 2=colr 3=ispe；aux: 4=hvcC 5=auxC 6=ispe（索引从 1 计，按 ipco 出现顺序）
        ipco = plain_box('ipco', primary_hvcc + primary_colr + ispe_p
                         + aux_hvcc + auxc + ispe_a)
        ipma = full_box('ipma', struct.pack('>I', 2)
                        + struct.pack('>HB', 1, 3) + b'\x81\x82\x83'
                        + struct.pack('>HB', 2, 3) + b'\x84\x85\x86')
    else:
        ipco = plain_box('ipco', primary_hvcc + primary_colr + ispe_p)
        ipma = full_box('ipma', struct.pack('>I', 1)
                        + struct.pack('>HB', 1, 3) + b'\x81\x82\x83')
    iprp = plain_box('iprp', ipco + ipma)

    mdat_payload = primary_bytes
    iloc_entries = [(1, len(primary_bytes))]
    if have_aux:
        mdat_payload += aux_bytes
        iloc_entries += [(2, len(aux_bytes))]
        if have_exif:
            mdat_payload += exif_bytes
            iloc_entries += [(3, len(exif_bytes))]

    def iloc_bytes():
        body = struct.pack('>H', len(iloc_entries))
        off = 0
        for iid, ln in iloc_entries:
            body += struct.pack('>HH', iid, 0) + struct.pack('>H', 1) + struct.pack('>II', off, ln)
            off += ln
        return full_box('iloc', struct.pack('>BB', 0x44, 0x00) + body)

    ftyp = plain_box('ftyp', b'heic' + struct.pack('>I', 0) + b'mif1' + b'heic')
    meta_len = len(full_box('meta', hdlr + pitm + iinf + iref + iprp + iloc_bytes()))
    base_off = len(ftyp) + meta_len + 8
    off = base_off
    fixed = []
    for iid, ln in iloc_entries:
        fixed.append((iid, off, ln))
        off += ln

    def iloc_bytes_fixed():
        body = struct.pack('>H', len(iloc_entries))
        for iid, o, ln in fixed:
            body += struct.pack('>HH', iid, 0) + struct.pack('>H', 1) + struct.pack('>II', o, ln)
        return full_box('iloc', struct.pack('>BB', 0x44, 0x00) + body)

    meta = full_box('meta', hdlr + pitm + iinf + iref + iprp + iloc_bytes_fixed())
    blob = ftyp + meta + plain_box('mdat', mdat_payload)
    return blob


# ───────────────────────── 主流程 ─────────────────────────

def encode_hevc(ffmpeg, gray, src_filter):
    """编码一帧 HEVC，返回 (annexb, chroma_format_idc)。gray=True 走 4:0:0 单通道。
    src_filter 必须是**完整**的 lavfi 图（含尺寸，如 "gradients=s=64x48:c0=red:c1=blue"）。"""
    pix = 'gray' if gray else 'yuv420p'
    r = subprocess.run(
        [ffmpeg, '-y', '-v', 'error', '-f', 'lavfi', '-i', src_filter,
         '-frames:v', '1', '-pix_fmt', pix, '-c:v', 'libx265',
         '-x265-params', 'log-level=error', '-f', 'hevc', '-'],
        capture_output=True)
    if r.returncode != 0:
        return None, None, r.stderr.decode('utf-8', 'replace')[:400]
    return r.stdout, (0 if gray else 1), None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ffmpeg', required=True)
    ap.add_argument('--outdir', required=True)
    a = ap.parse_args()
    os.makedirs(a.outdir, exist_ok=True)

    # ── 主图：64x48 渐变（RGB 4:2:0）──
    src, cfmt, err = encode_hevc(a.ffmpeg, False, 'gradients=s=64x48:c0=red:c1=blue')
    if src is None:
        src, cfmt, err = encode_hevc(a.ffmpeg, False, 'testsrc2=s=64x48')
    if src is None:
        print(f"✗ 主图 HEVC 编码失败: {err}")
        return 1
    nals = split_annexb(src)
    psets = [(t, p) for t, p in nals if t in (32, 33, 34)]
    primary = to_length_prefixed(src)
    hvcc_p = build_hvcc(psets)
    print(f"主图 {len(primary)} B（NAL {len(nals)} 个，参数集 {[t for t, _ in psets]}，4:2:0）")

    # ── aux：32x24 灰度渐变（4:0:0 单通道；x265 不支持 400 时回退 4:2:0 三通道同值）──
    aux, aux_cfmt, aerr = encode_hevc(a.ffmpeg, True, 'gradients=s=32x24:c0=black:c1=white')
    if aux is None:
        print(f"ℹ 4:0:0 灰度编码失败（{(aerr or '').splitlines()[-1] if aerr else '?'}）⇒ 回退 4:2:0")
        aux, aux_cfmt, aerr = encode_hevc(a.ffmpeg, False, 'gradients=s=32x24:c0=black:c1=white')
        if aux is None:
            print(f"✗ aux HEVC 编码失败: {aerr}")
            return 1
    anals = split_annexb(aux)
    asets = [(t, p) for t, p in anals if t in (32, 33, 34)]
    aux_item = to_length_prefixed(aux)
    hvcc_a = set_chroma_format(build_hvcc(asets), aux_cfmt)
    print(f"aux {len(aux_item)} B（chroma_format_idc={aux_cfmt}，{'4:0:0' if aux_cfmt == 0 else '4:2:0'}）")

    # ── colr：nclx（primaries=12 smpte432/P3-D65, transfer=13 sRGB, matrix=6 bt2020nc, full_range）──
    #   Apple 真机主图形态（SPEC §5：P3 nclx 或 P3 ICC）；aux 无 colr（untagged linear，官方口径）。
    colr_p = plain_box('colr', b'nclx' + struct.pack('>HHHB', 12, 13, 6, 1))

    exif = build_apple_exif()
    print(f"Exif item {len(exif)} B（AppleMakerNote 33=3/2 48=1/2 ⇒ headroom≈4.4536）")

    n1 = build_heic(primary, hvcc_p, (64, 48), aux_item, hvcc_a, (32, 24),
                    URN_APPLE, exif, colr_p, b'')
    p1 = os.path.join(a.outdir, 'aux_apple_gainmap.heic')
    open(p1, 'wb').write(n1)
    print(f"✓ {p1}  {len(n1)} B（A 件：aux + Exif + iref auxl/cdsc）")

    n2 = build_heic(primary, hvcc_p, (64, 48), None, None, None, None, None, colr_p, b'')
    p2 = os.path.join(a.outdir, 'aux_apple_noaux.heic')
    open(p2, 'wb').write(n2)
    print(f"✓ {p2}  {len(n2)} B（B 件：纯 SDR 负控）")

    n3 = build_heic(primary, hvcc_p, (64, 48), aux_item, hvcc_a, (32, 24),
                    URN_OTHER, exif, colr_p, b'')
    p3 = os.path.join(a.outdir, 'aux_apple_otherurn.heic')
    open(p3, 'wb').write(n3)
    print(f"✓ {p3}  {len(n3)} B（C 件：URN 语义反控）")

    # ── D 件（AU2-X1，2026-11-10）：auxC URN=urn:iso:std:iso:ts:21496:-1，
    #    aux item 数据前置 version(0) + ISO 21496-1 GainMapMetadata（62 B）+ HEVC 流；
    #    探针结构尝试成立 ⇒ Determinate=true（ISO 2^logBoost 公式族恢复）。
    aux_iso_payload = build_iso21496_payload(0) + aux_item
    n4 = build_heic(primary, hvcc_p, (64, 48), aux_iso_payload, hvcc_a, (32, 24),
                    URN_ISO, None, colr_p, b'')
    p4 = os.path.join(a.outdir, 'aux_iso21496.heic')
    open(p4, 'wb').write(n4)
    print(f"✓ {p4}  {len(n4)} B（D 件：aux-21496 防御性形态正控，前置元数据 62 B + 流 {len(aux_item)} B）")

    # D-bad（负控）：同 D 但 version 段破坏（0→1）⇒ 探针 magic/version 不匹配 ⇒
    # fail-closed 点名「aux-21496 元数据结构未证实」（防御性识别的牙）。
    aux_iso_bad_payload = build_iso21496_payload(1) + aux_item
    n5 = build_heic(primary, hvcc_p, (64, 48), aux_iso_bad_payload, hvcc_a, (32, 24),
                    URN_ISO, None, colr_p, b'')
    p5 = os.path.join(a.outdir, 'aux_iso21496_bad.heic')
    open(p5, 'wb').write(n5)
    print(f"✓ {p5}  {len(n5)} B（D-bad：version 段破坏负控 ⇒ 结构未证实）")
    return 0


if __name__ == '__main__':
    sys.exit(main())