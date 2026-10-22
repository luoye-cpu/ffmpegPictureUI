#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_mutate-dng-lens.py —— 造"某一类缺陷"的镜头 opcode 副本（第 77 条 ⑱d 组用）

为什么单独一个工具而不是塞进读手：读手只该负责**判**，造变异件是另一件事；混在一起时
"造件"和"判件"共用同一个入口，判据走偏了造件也会跟着走偏（本轮 ⑱f 空跑就是这么溜过去的）。

它复用 `_readback-dng-lens.py` 的 TIFF/opcode 解析（那是被审的**判据**，不是写侧逻辑），
只往字节里注入十类缺陷之一，然后打印"改了哪个偏移、从什么值改成什么值"⇒ 门禁日志里有读数可追。

用法: py _mutate-dng-lens.py <输入 dng> <输出 dng> <kind>
kind:
  k4       暗角最高次项 ×1.2          ⇒ 期望读手报「逐结点回算残差」
  v1val    1.3 兜底支的一项 +0.05      ⇒ 期望读手报「WarpRectilinear 对畸变表回算残差」
  flags14  清掉 WarpRectilinear2 的 optional 位 ⇒ 期望「没标 optional」
  vigopt   给 FixVignetteRadial 加上 optional 位 ⇒ 期望「被标 optional」
  center   把 warp 的光心挪到 0.44      ⇒ 期望「光心不一致」
  order    交换 [14] 与 [1] 的先后      ⇒ 期望「不紧跟」（规范 p.105 的跳过规则只在相邻时成立）
  dup14    把 ID=1 的 code 字段改成 14  ⇒ 期望「镜头支重复」（同一校正被施加两次）
  dropvig  整条摘掉 FixVignetteRadial   ⇒ 无点名时期望「暗角支缺席且写侧没点名」
  dropw1   整条摘掉 WarpRectilinear     ⇒ 无点名时期望「有 1.6 支却无 1.3 兜底支」
  dropboth 两支 warp 全摘              ⇒ 无点名时期望「畸变两支 warp 全缺席且写侧没点名」
  后三种都是**就地重发列表**（count 与 byteCount 成对改、总长不变）；给读手配一份点名过的
  stderr（lens-skipped=vignette / warp-legacy / distortion）时它必须**判绿** ⇒ 两条分支双向都有格子
  arw-off-0x7031 / arw-off-0x7036 / arw-off-0x7034
        ⇒ 输入换成**源 ARW**：把图像 SubIFD 里那一路"机内开关"清零，造出合法的"明确关"素材
          （写侧必须点名 switch-off 且不写那一支 / 不加那一份 CA，读侧必须同一把尺 ⇒ ⑱h 用它）
退出 0 = 已写出（并打印改动明细）；1 = 该产物不含此变异所需的支；2 = 用法/形状不符。
"""
import importlib.util
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SPEC = importlib.util.spec_from_file_location('lensread', os.path.join(HERE, '_readback-dng-lens.py'))
LR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LR)


def flip_source_switch(t, data, buf, src, dst, kind):
    """arw-off-0x<TAG>：把**源 ARW** 图像 SubIFD 里的三态开关清零 ⇒ 机内"明确关"。

    这不是缺陷而是合法素材变体，给 ⑱h 用：写侧必须点名 `lens-skipped=<项>:switch-off` 且不写那一支，
    读侧必须用**同一把尺**（不得再期望那一支、或那一份 CA 因子）。两侧不同尺在这条通道上已经错过一次
    （开关按参数表那种 type=8 去读 ⇒ 三条腿全被判成"关"）。"""
    tag = int(kind.split('0x')[-1], 16)
    found = None
    for off in t.sub_ifds():
        d = t.ifd(off)
        if tag in d:
            found = d[tag]
            break
    if found is None:
        print('MUTATE FAIL %s：图像 SubIFD 里没有 0x%04X ⇒ 造不出"明确关"的素材' % (kind, tag))
        return 1
    ty, cnt, val, e = found
    if cnt != 1 or ty not in (3, 8):
        print('MUTATE FAIL %s：0x%04X 是 type=%d count=%d ⇒ 不是可就地清零的内联开关（不敢乱改）'
              % (kind, tag, ty, cnt))
        return 2
    old = struct.unpack_from('<I', data, e + 8)[0]
    struct.pack_into('<I', buf, e + 8, 0)
    if len(buf) != len(data):
        print('MUTATE FAIL 清零改变了总长（%d→%d）⇒ 实现有误' % (len(data), len(buf)))
        return 1
    open(dst, 'wb').write(bytes(buf))
    print('MUTATE OK kind=%s 输出=%s · 源开关 0x%04X（entry@%d type=%d count=%d）值字段 %d → 0'
          % (kind, os.path.basename(dst), tag, e, ty, cnt, old))
    return 0


def main(argv):
    if len(argv) < 4:
        print('用法: _mutate-dng-lens.py <in.dng> <out.dng> <kind>')
        return 2
    src, dst, kind = argv[1], argv[2], argv[3]
    data = open(src, 'rb').read()
    t = LR.Tiff(data, '<')
    buf = bytearray(data)
    if kind.startswith('arw-off-'):
        return flip_source_switch(t, data, buf, src, dst, kind)
    scans = [t.ifd(t.u32(4))] + [t.ifd(off) for off in t.sub_ifds()]
    hit = None
    for bi, d in enumerate(scans):
        if LR.TAG_OPC3 in d:
            ty, cnt, val, e = d[LR.TAG_OPC3]
            if cnt > 4:
                hit = (val, cnt, e)
                break
    if hit is None:
        print('MUTATE FAIL %s 里没有 51022 ⇒ 无从变异' % src)
        return 1
    val, cnt, entry_off = hit
    ops, _ = LR.parse_opcodes(data[val:val + cnt])
    by = {}
    for o in ops:
        by.setdefault(o['code'], o)

    def hdr(o):
        return val + o['start'] - 16          # code(4) minVersion(4) flags(4) size(4) 的起点

    if kind == 'k4':
        o = by.get(LR.ID_VIG)
        if not o:
            return report_fail(kind, '没有暗角支')
        at = val + o['start'] + 4 * 8
        old = struct.unpack_from('>d', data, at)[0]
        struct.pack_into('>d', buf, at, old * 1.2 if old else 0.002)
        detail = '暗角 k4 %g → %g @%d' % (old, struct.unpack_from('>d', bytes(buf), at)[0], at)
    elif kind == 'v1val':
        o = by.get(LR.ID_WARP1)
        if not o:
            return report_fail(kind, '没有 1.3 兜底支')
        at = val + o['start'] + 4 + 8          # 平面 0 的第 2 个 double = r² 项
        old = struct.unpack_from('>d', data, at)[0]
        struct.pack_into('>d', buf, at, old + 0.05)
        detail = 'warp1 平面0 r² 项 %g → %g @%d' % (old, old + 0.05, at)
    elif kind in ('flags14', 'vigopt'):
        o = by.get(LR.ID_WARP2 if kind == 'flags14' else LR.ID_VIG)
        if not o:
            return report_fail(kind, '缺少所需 opcode')
        at = hdr(o) + 8
        old = struct.unpack_from('>I', data, at)[0]
        struct.pack_into('>I', buf, at, old ^ 1)
        detail = '%s flags %d → %d @%d' % (kind, old, old ^ 1, at)
    elif kind == 'center':
        w2, vg = by.get(LR.ID_WARP2), by.get(LR.ID_VIG)
        if not (w2 and vg):
            return report_fail(kind, '需要暗角与 warp 两支都在')
        at = val + w2['start'] + 4 + 19 * 8 * 3        # 三支系数之后的光心 x
        struct.pack_into('>d', buf, at, 0.44)
        detail = 'warp2 光心 x → 0.44 @%d' % at
    elif kind == 'order':
        w2, w1 = by.get(LR.ID_WARP2), by.get(LR.ID_WARP1)
        if not (w2 and w1):
            return report_fail(kind, '需要两支 warp 都在')
        rec = lambda o: data[val + o['start'] - 16: val + o['start'] + o['size']]
        i2, i1 = w2['index'], w1['index']
        lo, hi = (i2, i1) if i2 < i1 else (i1, i2)
        o_lo = w2 if i2 < i1 else w1
        o_hi = w1 if i2 < i1 else w2
        a_lo = val + o_lo['start'] - 16
        a_hi = val + o_hi['start'] - 16
        n_lo, n_hi = 16 + o_lo['size'], 16 + o_hi['size']
        assert a_lo + n_lo == a_hi, '两支不相邻，order 变异无法就地交换'
        swapped = rec(o_hi) + rec(o_lo)
        buf[a_lo:a_lo + n_lo + n_hi] = swapped
        detail = '就地交换 [%d]↔[%d] 两支顺序（总长不变）@%d' % (o_lo['code'], o_hi['code'], a_lo)
    elif kind == 'dup14':
        w1 = by.get(LR.ID_WARP1)
        if not w1:
            return report_fail(kind, '没有 1.3 兜底支可冒充 1.6 支')
        at = val + w1['start'] - 16                      # 该支头部的 code 字段
        old = struct.unpack_from('>I', data, at)[0]
        struct.pack_into('>I', buf, at, LR.ID_WARP2)
        detail = '把 ID=%d 那支的 code 字段改成 %d ⇒ 列表里出现两支 14 @%d' % (old, LR.ID_WARP2, at)
    elif kind in ('dropvig', 'dropw1', 'dropboth'):
        drop = {'dropvig': (LR.ID_VIG,), 'dropw1': (LR.ID_WARP1,),
                'dropboth': (LR.ID_WARP1, LR.ID_WARP2)}[kind]
        keep = [o for o in ops if o['code'] not in drop]
        if len(keep) == len(ops):
            return report_fail(kind, '要摘的支不在（%s）' % ','.join(str(x) for x in drop))
        blob = struct.pack('>I', len(keep))
        for o in keep:
            blob += struct.pack('>IIII', o['code'], o['minVersion'], o['flags'], o['size']) + o['body']
        if len(blob) >= cnt:
            return report_fail(kind, '重发的列表不更短（%d ≥ %d）⇒ 不敢就地覆盖' % (len(blob), cnt))
        # 覆盖时**总长必须不变**：bytearray 的切片赋值若放进更短的片段会整体左移，
        # TIFF 里所有偏移立刻失步（本轮实测撞见过一次：读手报"载荷越界"而不是"暗角缺席"）。
        buf[val:val + cnt] = blob + bytes(cnt - len(blob))
        # TIFF entry 的布局是 tag(2) type(2) **count(4)** value(4) ⇒ count 在 entry+4。
        # 写成 entry+8 会去改**载荷偏移**（本轮实测撞见）。count 与载荷必须成对改
        # （本仓在 51022 与 0x14A 上各踩过一次）。
        struct.pack_into('<I', buf, entry_off + 4, len(blob))
        detail = ('摘掉 ID=%s：%d 支→%d 支，列表 %d→%d B，51022 的 count 同步改到 %d'
                  % ('/'.join(str(x) for x in drop), len(ops), len(keep), cnt, len(blob), len(blob)))
    else:
        print('MUTATE FAIL 不认识 kind=%s' % kind)
        return 2

    if len(buf) != len(data):
        print('MUTATE FAIL 变异改变了文件总长（%d→%d）⇒ TIFF 偏移全体失步，这种副本不能用'
              % (len(data), len(buf)))
        return 1
    open(dst, 'wb').write(bytes(buf))
    print('MUTATE OK kind=%s 输出=%s · %s' % (kind, os.path.basename(dst), detail))
    return 0


def report_fail(kind, why):
    print('MUTATE FAIL kind=%s：%s' % (kind, why))
    return 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
