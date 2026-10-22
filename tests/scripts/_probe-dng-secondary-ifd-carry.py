#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""_probe-dng-secondary-ifd-carry.py —— DNG→DNG 再编码里的次级 IFD / 主 IFD 内容位（第 77 条 ㉖b）

夹具 `base.dng`：主图 64×64 + 透明遮罩 NSFT=4 + 深度 NSFT=8 + 语义遮罩 NSFT=65540
+ MaskedAreas(50830) + XMP(700) + 预览 NSFT=1。

判据分三层，每层只量"能被读者正确解码"这一件事：

  1) **搬运层（有牙）**：输入里 NSFT=4/8/65540 的次级 IFD，产物必须也有同值 IFD；
     其中遮罩/深度**用 Adobe 自己的 `dng_validate -transparency / -depthMap` 解出来，
     与源件逐像素相同**（这是"PS 能正确识别"的机器版判据，不是看标签在不在）；
     MaskedAreas(50830) 的 4 个坐标值必须逐数相同；语义遮罩比 52526/52528/52536 的值。
     另外 `dng_validate -2` 必须能渲染产物（rc=0）。
  2) **预览层**：预览/替代族（NSFT 0/1/16/17/32/33/0x10001）里的 **JPEG 预览**必须按原字节搬
     （产物任一 IFD 里能找到逐字节相同的一份）；不是 JPEG 的压缩形才允许降级成点名
     `carry-skipped=secondary-ifd-other`，没点名 = 静默丢位 = 退 1。
  3) **出声层**：XMP 包(0x02BC) dngtool 写不了（qDNGUseXMP=0），必须点名
     `carry-skipped=dng-main-content-bit`；点了名记"在册欠账"行（不记成已搬，也不判红）。
  3) **替代出口**：工具也可以选择**拒绝出件**（非零 + 点名 refuse-secondary-ifd-loss + 无产物），
     但点名的 NewSubFileType 必须覆盖输入里全部"没有写通路/搬不动"的那些（不许漏）。

退出码：0 = 1)+2) 成立，或走了 3) 且点名覆盖完整；
        1 = 静默丢位 / 搬运后内容不对 / 拒绝了但点名不全；
        2 = 跑不动（尺子无效、dngtool 崩、缺 dng_validate ⇒ 只记未判，不记绿）。

用法：py _probe-dng-secondary-ifd-carry.py <base.dng> <dngtool.exe> <outdir> [dng_validate.exe]
"""
import os
import importlib.util
import re
import shutil
import subprocess
import sys

_S = importlib.util.spec_from_file_location(
    'secfix', os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           '_make-secondary-ifd-fixture.py'))
sf = importlib.util.module_from_spec(_S)
_S.loader.exec_module(sf)

CARRYABLE = (4, 8, 0x10004)                       # dng_info 按整值认、SDK 读写侧都有
OTHER_FAMILY = {0, 1, 16, 17, 32, 33, 0x10001}    # 预览/增强/增益图：本轮只点名
MASK_TAGS = (254, 256, 257, 258, 259, 262, 273, 277, 278, 279, 284, 320, 330)
SEMANTIC_TAGS = (52526, 52528, 52536)             # SemanticName / InstanceID / MaskSubArea
REFUSE_KEY = 'refuse-secondary-ifd-loss'
SKIP_OTHER_KEY = 'carry-skipped=secondary-ifd-other'
SKIP_XMP_KEY = 'carry-skipped=dng-main-content-bit'
FASTFAIL = -1073740791          # pwsh/.NET 的 Int32 形状
FASTFAIL_U = 3221226505         # 无符号形状（0xC0000409 = __fastfail）


def nsfts_of(path):
    ifs, _b = sf.enumerate_ifds(path)
    out = []
    for (_o, ns, _d) in ifs:
        if ns not in out:
            out.append(ns)
    return out


def tag_table(path, nsft):
    """{tag: (ty, cnt, byteCount, raw_bytes)}，同一 NSFT 只取第一张 IFD。"""
    ifs, b = sf.enumerate_ifds(path)
    for (_o, ns, d) in ifs:
        if ns == nsft:
            out = {}
            for t, (ty, cnt, bc, v) in d.items():
                raw = b[v:v + bc] if bc > 4 else __import__('struct').pack('<I', v)[:bc]
                out[t] = (ty, cnt, bc, raw)
            return out
    return None


def _scalar(raw, bc):
    import struct
    return struct.unpack('<I', raw.ljust(4, b'\0'))[0] if bc <= 4 else int.from_bytes(raw, 'little')


def ifd_jpeg(path, nsft):
    """取某张 IFD 的 JPEG 原字节：0x0201/0x0202 或条带 0x0111/0x0117；不是 JPEG 返回 None。"""
    t = tag_table(path, nsft)
    if not t:
        return None
    with open(path, 'rb') as f:
        b = f.read()
    if 0x0201 in t and 0x0202 in t:
        off = _scalar(t[0x0201][3], t[0x0201][2])
        ln = _scalar(t[0x0202][3], t[0x0202][2])
        out = b[off:off + ln]
    elif 0x0111 in t and 0x0117 in t:
        offs, lens = t[0x0111], t[0x0117]
        o = ([int.from_bytes(offs[3][4 * i:4 * i + 4], 'little') for i in range(offs[1])]
             if offs[2] > 4 else [_scalar(offs[3], offs[2])])
        l = ([int.from_bytes(lens[3][4 * i:4 * i + 4], 'little') for i in range(lens[1])]
             if lens[2] > 4 else [_scalar(lens[3], lens[2])])
        out = b''.join(b[x:x + y] for x, y in zip(o, l))
    else:
        return None
    return out if out[:2] == b'\xff\xd8' else None


def named_nsfts(talk):
    m = re.search(r'NewSubFileType=\[([0-9,]*)\]', talk)
    return set(int(x) for x in m.group(1).split(',') if x) if m else set()


def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    return p.returncode, (p.stdout or '') + (p.stderr or '')


def dump_map(dv, path, base, what):
    """用 Adobe 读者导出遮罩/深度图；返回 (tif 路径|None, 说话)。"""
    out = base + '.tif'
    if os.path.exists(out):
        os.remove(out)
    rc, talk = run([dv, '-' + what, base, path])
    return (out if rc == 0 and os.path.exists(out) else None), 'rc=%d' % rc


def pixels_equal(a, b):
    """逐像素比；返回 (相等?, 读数)。没有 tifffile ⇒ 退化成逐字节比并标明口径。"""
    try:
        import tifffile
        import numpy as np
        A = tifffile.imread(a)
        B = tifffile.imread(b)
        if A.shape != B.shape or A.dtype != B.dtype:
            return False, 'shape %s/%s dtype %s/%s' % (A.shape, B.shape, A.dtype, B.dtype)
        d = int(np.abs(A.astype('int64') - B.astype('int64')).max())
        return (d == 0), 'shape=%s dtype=%s max|Δ|=%d' % (A.shape, A.dtype, d)
    except ImportError:
        with open(a, 'rb') as f:
            A = f.read()
        with open(b, 'rb') as f:
            B = f.read()
        return (A == B), '无 tifffile ⇒ 逐字节比 TIFF（%d B）%s' % (len(A), '相同' if A == B else '不同')


def main(argv):
    if len(argv) < 4:
        print(__doc__)
        return 2
    src, dngtool, outdir = argv[1], argv[2], argv[3]
    dv = argv[4] if len(argv) > 4 else None
    out = os.path.join(outdir, '_secfixture-reencoded.dng')
    if not os.path.exists(src):
        print('SEC-CARRY FAIL 夹具不在位: %s' % src)
        return 2

    # 尺子验牙①：同一件自己跟自己比必须逐项相同（否则尺子无效）
    try:
        sA = nsfts_of(src)
    except Exception as e:                                    # noqa: BLE001
        print('SEC-CARRY FAIL 尺子读不动夹具: %r' % (e,))
        return 2
    print('SEC-CARRY 读数 输入 NSFT 集合=%s' % sorted(sA))

    if os.path.exists(out):
        os.remove(out)
    if outdir and not os.path.isdir(outdir):
        os.makedirs(outdir)
    rc, talk = run([dngtool, '-e', '-lossless', '-v', '-i', src, '-O', out])
    print('SEC-CARRY 读数 dngtool rc=%d；产物=%s' %
          (rc, ('%d B' % os.path.getsize(out)) if os.path.exists(out) else '无'))
    if rc in (FASTFAIL, FASTFAIL_U):
        print('SEC-CARRY FAIL dngtool 崩溃（rc=%d=0xC0000409）⇒ 尺子没量到，只记未判' % rc)
        return 2

    # ── 出口三：拒绝出件（也必须点名完整） ──
    if rc != 0 or not os.path.exists(out):
        if REFUSE_KEY in talk and not os.path.exists(out):
            need = set(ns for ns in sA if ns != 0 and ns not in OTHER_FAMILY)
            got = named_nsfts(talk)
            missing = sorted(need - got)
            if not missing and need:
                print('SEC-CARRY OK 臂B：非零退出 rc=%d + 无产物 + 点名 %s + 覆盖全部无写通路的 NSFT %s'
                      % (rc, REFUSE_KEY, sorted(need)))
                return 0
            print('SEC-CARRY FAIL 拒绝了但点名不全（应点名 %s，实得 %s，漏 %s）'
                  % (sorted(need), sorted(got), missing))
            return 1
        tail = ' ⏎ '.join(l.strip() for l in talk.splitlines() if l.strip())[-400:]
        print('SEC-CARRY FAIL 重编码没出件（rc=%d）且没点名 %s :: %s ⇒ 只记未判'
              % (rc, REFUSE_KEY, tail))
        return 2

    fails, notes, pending = [], [], []

    # ── 出口一/二：出了件 ⇒ 搬运层 + 出声层 ──
    sB = nsfts_of(out)
    print('SEC-CARRY 读数 输出 NSFT 集合=%s' % sorted(sB))
    for ns in CARRYABLE:
        if ns in sA and ns not in sB:
            fails.append('次级 IFD 整张消失 NSFT=%d' % ns)

    # 1a) 遮罩/深度：Adobe 读者解出来逐像素比
    if dv is None:
        pending.append('未判 像素对撞：没给 dng_validate 路径')
    else:
        for ns, what, redump in ((4, 'transparency', True), (8, 'depthMap', True)):
            if ns not in sA or ns not in sB:
                continue
            fa, aa = dump_map(dv, src, os.path.join(outdir, '_src_' + what), what)
            fb, bb = dump_map(dv, out, os.path.join(outdir, '_out_' + what), what)
            if not fa or not fb:
                pending.append('未判 %s 像素：dng_validate 导出失败（源 %s，产物 %s）' % (what, aa, bb))
                continue
            eq, reading = pixels_equal(fa, fb)
            print('SEC-CARRY 读数 %s 像素对撞：%s %s' % (what, '相同' if eq else '不同', reading))
            if not eq:
                fails.append('%s(NSFT=%d) 搬运后像素不同（%s）' % (what, ns, reading))

    # 1b) 语义遮罩：值级对撞（没有公开的导出面 ⇒ 不写成像素已验）
    if 0x10004 in sA and 0x10004 in sB:
        ta = tag_table(src, 0x10004) or {}
        tb = tag_table(out, 0x10004) or {}
        for t in SEMANTIC_TAGS:
            if t not in ta:
                continue
            if t not in tb:
                fails.append('语义遮罩丢标签 0x%X' % t)
            elif ta[t][3] != tb[t][3]:
                fails.append('语义遮罩 0x%X 值不同（源 %d B，产物 %d B）'
                             % (t, ta[t][2], tb[t][2]))
        if not any(t in ta for t in SEMANTIC_TAGS):
            pending.append('未判 语义遮罩：源里没有 52526/52528/52536，值级无从对撞')

    # 1c) MaskedAreas：坐标值必须逐数相同
    ma = tag_table(src, 0)
    mb = tag_table(out, 0)
    if ma and 50830 in ma:
        if not mb or 50830 not in mb:
            fails.append('主 IFD 的 MaskedAreas(50830) 在产物里缺席')
        elif ma[50830][3] != mb[50830][3]:
            fails.append('MaskedAreas 坐标值不同（源 %d B，产物 %d B）'
                         % (ma[50830][2], mb[50830][2]))

    # 1e) **50830 单独存在**（一个次级 IFD 都没有）也必须搬 —— 带全副夹具时这条空档量不到：
    #     dngtool 的搬运入口原来只在"有可搬次级 IFD"时才调用，`MaskedAreas` 会被静默跳过（账本 §40.8）。
    only = os.path.join(outdir, '_secfixture-maskonly.dng')
    try:
        sf.build(only, drop=('transparency', 'depth', 'semantic', 'preview'))
        oifs = nsfts_of(only)
        oma = tag_table(only, 0) or {}
        if oifs != [0] or 50830 not in oma:
            pending.append('未判 50830-only 变体不成立（NSFT=%s，50830 在=%s）'
                           % (oifs, 50830 in oma))
        else:
            oout = os.path.join(outdir, '_secfixture-maskonly-reenc.dng')
            if os.path.exists(oout):
                os.remove(oout)
            rc2, talk2 = run([dngtool, '-e', '-lossless', '-i', only, '-O', oout])
            if rc2 != 0 or not os.path.exists(oout):
                fails.append('50830-only 件没出件（rc=%d）:: %s'
                             % (rc2, ' '.join(talk2.split())[-140:]))
            else:
                mb2 = tag_table(oout, 0) or {}
                if 50830 not in mb2:
                    fails.append('50830 单独存在时产物里缺席（静默丢位）')
                elif mb2[50830][3] != oma[50830][3]:
                    fails.append('50830 单独存在时坐标值不同（源 %d B，产物 %d B）'
                                 % (oma[50830][2], mb2[50830][2]))
                else:
                    print('SEC-CARRY 读数 50830-only：产物里坐标值逐字节相同（%d B）'
                          % mb2[50830][2])
    except Exception as e:                                    # noqa: BLE001
        pending.append('未判 50830-only 变体构建失败（%r）' % (e,))

    # 1d) 产物必须能被 Adobe 渲染
    if dv is not None:
        rrc, _t = run([dv, '-2', os.path.join(outdir, '_render'), out])
        print('SEC-CARRY 读数 dng_validate -2 渲染产物 rc=%d' % rrc)
        if rrc != 0:
            fails.append('dng_validate -2 渲染产物失败 rc=%d' % rrc)

    # 2a) 预览/替代族：JPEG 预览必须按**原字节**搬（产物里要能找到逐字节相同的一份）；
    #     不是 JPEG（未压缩/Deflate）才允许降级成"点名"，没点名就是静默丢位。
    out_jpegs = set()
    for ns in sB:
        j = ifd_jpeg(out, ns)
        if j:
            out_jpegs.add(j)
    for ns in sA:
        if ns not in OTHER_FAMILY:
            continue
        if ns == 0:
            continue          # NSFT=0 是主图（重编码走主路径，不要求 JPEG 逐字节相同）
        j = ifd_jpeg(src, ns)
        if j is None:
            if SKIP_OTHER_KEY in talk:
                pending.append('在册欠账 NSFT=%d 的预览不是 JPEG（未压缩/Deflate），工具已点名' % ns)
            else:
                fails.append('输入 NSFT=%d 的预览不是 JPEG，产物没有，工具也没点名 %s'
                             % (ns, SKIP_OTHER_KEY))
        elif j not in out_jpegs:
            fails.append('NSFT=%d 的预览 JPEG（%d B）在产物里找不到逐字节相同的一份'
                         % (ns, len(j)))
        else:
            print('SEC-CARRY 读数 preview NSFT=%d：JPEG 原字节逐字节相同（%d B）' % (ns, len(j)))

    # 2b) 出声层：XMP(700) 没有写通路，必须点名
    if ma and 700 in ma:
        if SKIP_XMP_KEY in talk:
            pending.append('在册欠账 XMP(700) dngtool 写不了（qDNGUseXMP=0），工具已点名')
        else:
            fails.append('输入有 XMP(700)，产物没有，工具也没点名 %s' % SKIP_XMP_KEY)

    for line in notes:
        print('SEC-CARRY 读数 %s' % line)
    for line in pending:
        print('SEC-CARRY %s' % line)
    if fails:
        for line in fails:
            print('SEC-CARRY FAIL %s' % line)
        return 1
    print('SEC-CARRY OK 搬运层：遮罩/深度逐像素相同、语义与 MaskedAreas（含 50830 单独存在）值相同、'
          '预览 JPEG 原字节相同、产物可渲染；出声层：XMP 已点名')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
