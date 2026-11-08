# -*- coding: utf-8 -*-
"""生成 JXL 增益图（jhgm box）**合成测试件**（供读侧门禁 verify-jxl-gainmap-* 家族使用）。

用法：
    python _make-jxl-gainmap-jhgm.py --outdir <dir>            # 落盘两支夹具
    python _make-jxl-gainmap-jhgm.py --outdir <dir> --verify   # 重读夹具按布局逐字段校验

产出两支（都进 --outdir，由门禁以 tests/data 为 outdir 调用并入库）：
  · synth_jxl_gainmap.jxl       正向：sig+ftyp+jhgm+jxlc（jxlc = 底图裸码流）
  · synth_jxl_gainmap_base.jxl  负控：sig+ftyp+jxlc，无 jhgm（证明探测器不是见容器就报）

⚠ 坏载荷变体**不提交、不由本脚本生成**——由门禁运行时对正向件副本做确定性变异
  （BE16 元数据长 → 0xFFFF ⇒ 超出盒剩余空间），见门禁 §2。

jhgm 载荷字段序（一手依据 = vendored tools/src/libjxl/lib/extras/gain_map.cc:118-145，
主 agent 已按此布局手工组装并经随包 jxlinfo 0.12.0 正控验证）：
    version(1B)=0x00 + BE16 元数据长 + ISO 21496-1 blob（容器层不透明）
    + 颜色编码长(1B)=0x00 + BE32 alt ICC 长=0x00000000 + 增益图裸码流(剩余全部，尺寸不落盘)
容器布局（实测）：sig=`00 00 00 0C 'JXL ' 0D 0A 87 0A`(12B) + ftyp(20B) + jhgm + jxlc，
jhgm 必须在 jxlc **之前**（vendored lib/extras/enc/jxl.cc:331-350 先 box 后帧；
imazen/zenjxl #22：码流 >4096B 时尾随 box 会被部分读者漏读）。

构件来源：
  · 两支 tiny 裸 JXL 码流 <- 随包 cjxl 0.12.0 对 16x16 PPM 的真实编码产物
    （tests/output/_tmp/jxl-gainmap-probe-20261108/{base,gm}.jxl，hex 内嵌于此，无工具依赖）
  · ISO 21496-1 blob <- 确定性 ASCII 占位（容器层不透明，jxlinfo 只校验长度闭合）
    ⚠ 它覆盖的是「容器/盒层与长度闭合」，**不代表**拿到了语义层 ISO 元数据判据——
    那要等写侧写出器的真实 BuildIso21496Metadata 产物。
"""
import argparse
import hashlib
import os
import struct
import sys

# ───────────────────────── 常量（确定性，改一个字节都算夹具版本变更） ─────────────────────────

SIG = bytes.fromhex('0000000C4A584C200D0A870A')

# 底图裸码流：16x16 sRGB 红色块，cjxl -q 100 -e 3（50 B，FF 0A 开头）
BASE_CODESTREAM = bytes.fromhex(
    'ff0a43404071210800010094004b1209121838044148817cc4e079628c13e3c48931ce01189bc1641043075e50a09b720800')

# 增益图裸码流：16x16 sRGB 绿色块，cjxl -q 100 -e 3（38 B，FF 0A 开头）
GM_CODESTREAM = bytes.fromhex(
    'ff0a43404071210800010064004b1209121838044148817cc460e2c43807007892aa40e287f3')

# 合成 ISO 21496-1 blob：88 字节确定性 ASCII 占位（容器层不透明）
_ISO_TEXT = b'synthetic ISO 21496-1 gain map metadata blob (container-layer opaque placeholder).'
ISO_BLOB = _ISO_TEXT.ljust(88, b'.')
assert len(ISO_BLOB) == 88, len(ISO_BLOB)

POSITIVE_NAME = 'synth_jxl_gainmap.jxl'
BASE_NAME = 'synth_jxl_gainmap_base.jxl'


# ───────────────────────── box 工具（与 _make-synth-heic-gainmap.py 同族） ─────────────────────────

def plain_box(typ, payload):
    return struct.pack('>I', 8 + len(payload)) + typ.encode('latin1') + payload


def walk_boxes(data, start, end):
    """顶层 box 遍历：yield (typ, payload_offset, payload_len, box_offset, box_size)。
    size==0 ⇒ 延伸到 end；size==1 ⇒ 64-bit XL 头。畸形（越界/最小头不足）抛 ValueError。"""
    p = start
    while p < end:
        if p + 8 > end:
            raise ValueError('box header truncated at %d' % p)
        size = struct.unpack('>I', data[p:p + 4])[0]
        typ = data[p + 4:p + 8].decode('latin1')
        hdr = 8
        if size == 1:
            if p + 16 > end:
                raise ValueError('XL box header truncated at %d' % p)
            size = struct.unpack('>Q', data[p + 8:p + 16])[0]
            hdr = 16
        elif size == 0:
            size = end - p
        if size < hdr or p + size > end:
            raise ValueError('box size invalid (typ=%r size=%d at %d)' % (typ, size, p))
        yield typ, p + hdr, size - hdr, p, size
        p += size


# ───────────────────────── 组装 ─────────────────────────

def build_jhgm_payload():
    return (bytes([0])                                  # jhgm_version
            + struct.pack('>H', len(ISO_BLOB))          # BE16 元数据长
            + ISO_BLOB                                  # ISO 21496-1 blob（不透明）
            + bytes([0])                                # 颜色编码长 = 0（无）
            + struct.pack('>I', 0)                      # BE32 alt ICC 长 = 0（无）
            + GM_CODESTREAM)                            # 增益图裸码流（尺寸不落盘，= 剩余全部）


def build_positive():
    return SIG + plain_box('ftyp', b'jxl ' + struct.pack('>I', 0) + b'jxl ') \
        + plain_box('jhgm', build_jhgm_payload()) + plain_box('jxlc', BASE_CODESTREAM)


def build_base():
    return SIG + plain_box('ftyp', b'jxl ' + struct.pack('>I', 0) + b'jxl ') \
        + plain_box('jxlc', BASE_CODESTREAM)


def sha16(data):
    return hashlib.sha256(data).hexdigest()[:16]


def emit(outdir):
    os.makedirs(outdir, exist_ok=True)
    for name, blob in ((POSITIVE_NAME, build_positive()), (BASE_NAME, build_base())):
        path = os.path.join(outdir, name)
        open(path, 'wb').write(blob)
        print('✓ %s  %d B  sha256[:16]=%s' % (path, len(blob), sha16(blob)))


# ───────────────────────── --verify：重读磁盘文件、按布局契约逐字段校验（不复用组装函数） ─────────────────────────

def check(name, ok, detail):
    print('%s %s %s' % ('✓' if ok else '✗', name, detail))
    return ok


def verify(outdir):
    ok_all = True

    # 正向件
    p = os.path.join(outdir, POSITIVE_NAME)
    try:
        data = open(p, 'rb').read()
    except OSError as e:
        print('✗ 正向件缺失：%s（%s）' % (p, e))
        return False
    ok_all &= check('positive/sig', data[:12] == SIG, '首 12B 容器签名')
    boxes = []
    try:
        boxes = list(walk_boxes(data, 12, len(data)))
        ok_all &= check('positive/boxwalk', True, 'box 链闭合：%s' % [t for t, *_ in boxes])
    except ValueError as e:
        ok_all &= check('positive/boxwalk', False, str(e))
    types = [t for t, *_ in boxes]
    ok_all &= check('positive/order', types == ['ftyp', 'jhgm', 'jxlc'], '盒序（jhgm 必须在 jxlc 前）')
    jhgm = next((b for b in boxes if b[0] == 'jhgm'), None)
    if jhgm is None:
        ok_all &= check('positive/jhgm', False, '无 jhgm box')
    else:
        _, off, ln, _, _ = jhgm
        payload = data[off:off + ln]
        cur = 0
        version = payload[cur]
        cur += 1
        meta_len = struct.unpack('>H', payload[cur:cur + 2])[0]
        cur += 2
        meta_ok = cur + meta_len <= len(payload)
        cur += meta_len if meta_ok else 0
        color_enc_size = payload[cur] if cur < len(payload) else -1
        cur += 1
        icc_len = struct.unpack('>I', payload[cur:cur + 4])[0] if cur + 4 <= len(payload) else -1
        cur += 4
        gm = payload[cur:] if cur <= len(payload) else b''
        ok_all &= check('positive/jhgm.version', version == 0, 'version=%d' % version)
        ok_all &= check('positive/jhgm.metaLen', meta_ok and meta_len == len(ISO_BLOB),
                        'metaLen=%d（预期 %d）' % (meta_len, len(ISO_BLOB)))
        ok_all &= check('positive/jhgm.colorEncSize', color_enc_size == 0, '颜色编码长=%s' % color_enc_size)
        ok_all &= check('positive/jhgm.altIccLen', icc_len == 0, 'alt ICC 长=%s' % icc_len)
        ok_all &= check('positive/jhgm.gainmap', gm[:2] == b'\xff\x0a',
                        '增益图=%dB FF0A 开头=%s' % (len(gm), gm[:2] == b'\xff\x0a'))
        fixed_fit = meta_ok and (1 + 2 + meta_len + 1 + 4) <= len(payload)
        ok_all &= check('positive/jhgm.fieldsFit', fixed_fit,
                        '固定字段闭合（1+2+%d+1+4=%d ≤ 载荷 %d）' % (meta_len, 8 + meta_len, len(payload)))
        ok_all &= check('positive/jhgm.gainmap.bytes', gm == GM_CODESTREAM,
                        '增益图与内嵌码流逐字节一致（%dB，metaLen 错位会被此检查抓到）' % len(gm))
    jxlc = next((b for b in boxes if b[0] == 'jxlc'), None)
    ok_all &= check('positive/jxlc', jxlc is not None and data[jxlc[1]:jxlc[1] + 2] == b'\xff\x0a',
                    'jxlc 载荷 FF0A 开头=%s' % (jxlc is not None))

    # 底图件（负控）
    q = os.path.join(outdir, BASE_NAME)
    try:
        data2 = open(q, 'rb').read()
    except OSError as e:
        print('✗ 底图件缺失：%s（%s）' % (q, e))
        return False
    ok_all &= check('base/sig', data2[:12] == SIG, '首 12B 容器签名')
    boxes2 = []
    try:
        boxes2 = list(walk_boxes(data2, 12, len(data2)))
    except ValueError as e:
        ok_all &= check('base/boxwalk', False, str(e))
    types2 = [t for t, *_ in boxes2]
    ok_all &= check('base/no-jhgm', types2 == ['ftyp', 'jxlc'], '无 jhgm（负控形态）')
    jxlc2 = next((b for b in boxes2 if b[0] == 'jxlc'), None)
    ok_all &= check('base/jxlc', jxlc2 is not None and data2[jxlc2[1]:jxlc2[1] + 2] == b'\xff\x0a',
                    'jxlc 载荷 FF0A 开头=%s' % (jxlc2 is not None))

    print('VERIFY: %s' % ('PASS' if ok_all else 'FAIL'))
    return ok_all


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--outdir', required=True, help='夹具输出目录（门禁以 tests/data 调用）')
    ap.add_argument('--verify', action='store_true', help='重读已落盘夹具按布局逐字段校验')
    a = ap.parse_args()

    if a.verify:
        return 0 if verify(a.outdir) else 1

    emit(a.outdir)
    # 幂等自证：再跑一次组装，字节必须一致（不写盘，只比对）
    for name, blob in ((POSITIVE_NAME, build_positive()), (BASE_NAME, build_base())):
        path = os.path.join(a.outdir, name)
        if open(path, 'rb').read() != blob:
            print('✗ 幂等自证失败：%s 重组装字节与磁盘不一致' % name)
            return 1
    print('幂等自证：PASS（重组装与磁盘一致）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
