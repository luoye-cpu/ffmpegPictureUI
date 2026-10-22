#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""_check-dng-private-carry.py —— 厂商私有件的携带/丢弃锁（第 77 条 ㉑ 组用）

立锁动因（账本 §20 实测）：私有件"在不在产物里、值对不对"完全由复制阶段 `-all:all` 的副作用决定，
一条判据都没有 ⇒ 静默带（Canon 267 条）与静默丢（Fuji 的镜头三表 24 条）在日志里长得一模一样。

三档定性，权威来自**第二个实现自己的回话**（本机 2026-10-20 实测，不猜）：
  1. 住在 ExifIFD `0x927C` MakerNote **块**里的厂商表 ⇒ 块整体有可写目标 ⇒ 到得了产物。
     到得了就两条硬判据：**同名键必须同值**、**源键必须被产物覆盖**（组内一条不许少）。
  2. 住在厂商**自己的 IFD 表**（RAF 的 `FujiIFD/FujiIFD2`、ARW 的 `SonyIDC/SR2/SR2SubIFD`、
     RW2 的 `PanasonicRaw`）⇒ exiftool 对 `-<组>:all` 直接回
     `Warning: No writable tags set from <源>`（或 "0 updated / 1 unchanged"）⇒ 到不了 = 格式边界
     ⇒ 点名条数、不判红；这类里的镜头三表另有标准载体（OpcodeList3），要救得做**换算**。
  3. 字节布局/载荷自指（`IFD1:*`、`SubIFD:*`、`Preview*Start`、`RawData`、`Strip*`、
     `BitsPerSample`、`Compression`、ICC 的 `Matrix*` …）⇒ 描述的是"各自这份文件自己"，
     照抄即撒谎 ⇒ 不参与对撞。

用法: py _check-dng-private-carry.py <源 raw> <产物 dng> [<dngtool 直出件（未跑产品后置的基线）>]
退出 0 = 携带的厂商注记组逐条同值且组内不缺；1 = 破坏或空跑；2 = 读取失败。

⚠ 已点名的"可修缺口"（KNOWN_OPEN_DEFECT）**不再写条数常数**：到达数由本脚本现场测量，
   用的是产品实际发出的那条 exiftool 命令形状（PRODUCT_CARRY_ARGS，逐字照抄 ExifToolService.cs:510-516），
   并用同一把尺在"未后置的基线件"上要求"缺=全部"当负控（账本 §34/§39）。
"""
import os
import re
import shutil
import subprocess
import sys

EX = os.environ.get('FFMPEGGUI_EXIFTOOL') or os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
    'publish', 'PLAN', 'exiftool', 'exiftool.exe')

# 不是"厂商注记组"的组名：标准档 + 工具/系统档 + 各文件自己的图像目录 + ICC 结构档
SKIP_GROUP_PREFIX = ('IFD0', 'IFD1', 'IFD2', 'IFD3', 'SubIFD', 'ExifIFD', 'GPS', 'InteropIFD',
                     'File', 'System', 'Composite', 'XMP', 'Iptc', 'Photoshop', 'MakerNotes',
                     'MPImage', 'QuickTime', 'Track', 'Item', 'Keys', 'MXF', 'HDP3',
                     'ExifTool', 'ICC', 'PreviewIFD', 'PrintIM', 'Huffman', 'Lang')

# 第 3 档：布局/载荷自指的名字（即使落在厂商组里也不对撞）
LAYOUT_NAME = re.compile(
    r'(?:Offsets?|Starts?|Ends?|Lengths?|ByteCounts|BitsPerSample|Compression|Width|Height|Strip|'
    r'Positioning|SampleFormat|FillOrder|RowsPerStrip|Orientation|RawData|'
    r'BlackLevelData|SensorProfile|ProfileCMMType|ProfileVersion|DeviceManufacturer|'
    r'DeviceModel|ColorSpaceData|ProfileClass|Matrix\d?|MediaWhitePoint|DeviceAttr|'
    r'ProfileDateTime)$')

# 第 2 档：工具自己说「没有可写目标」的组（实测回话登记在此；改动要重跑探针）
# 每个组名都对应一次本机探针（`exiftool -TagsFromFile <源> -<组>:all <DNG>`），回话分两种，
# 结果一样：**没有任何条目到达**：
#   · 明说 `Warning: No writable tags set from <源>` ⇒ FujiIFD / FujiIFD2 / RAF / RAF2 /
#     Version0 / Version1 / SonyIDC(?) / SR2 / SR2SubIFD / SR2DataIFD
#   · 不报错但 "0 image files updated / 1 unchanged"、条目变化 = +0 ⇒ PanasonicRaw / MinoltaRaw
#     / NikonCapture / SamsungRaw / SigmaRaw / OlympusRaw / LeicaRawPanasonic / SonyIDC；
#     `MakerUnknown`（Hasselblad CFV-50c 的未识别块）由 §22 的探针补：工具回 No writable tags set
NO_WRITABLE_TARGET = ('FujiIFD', 'FujiIFD2', 'SonyIDC', 'SR2', 'SR2SubIFD', 'SR2DataIFD',
                      'PanasonicRaw', 'OlympusRaw', 'LeicaRawPanasonic', 'NikonCapture',
                      'SamsungRaw', 'SigmaRaw', 'RAF', 'RAF2', 'Version0', 'Version1',
                      'MinoltaRaw', 'MakerUnknown')

# 未命名私有 ID（`组名_0xHHHH` 形状）⇒ 没有标准位可放，归「丢弃点名」而不是判红
UNNAMED = re.compile(r'_(?:0x)?[0-9a-fA-F]{3,8}$')

# 已点名的**可修缺口**（不是格式边界）：这里只登记"为什么它算可修缺口"，**条数一律现场测量**。
# 出处（账本 §34/§39）：旧版这句里写死的"93/198"不是读数；而我先前复量出的"后置能带 217 条"
# 也已被撤回——那是 `-all:all` 写在 `-TagsFromFile` **之前**的非规范顺序造出来的假到达数，
# 产品不会那样发。⇒ 现场测量必须逐字用 `PRODUCT_CARRY_ARGS`（产品 CopyMetadataAsync 的顺序），
# 打印形状 measured=<到达>/<该组非布局键总数> 并把缺项逐名点出。
# 判据的牙不变：既不在 NO_WRITABLE_TARGET、也不在这张表里的"整组未携带" ⇒ 一律判红。
KNOWN_OPEN_DEFECT = {('Canon', '.cr3'): '厂商注记整组未到 ⇒ 通路形状问题、非格式边界（到达数现场测量）'}

# ⚠⚠ 参数**顺序**即产品实际发出的顺序：`src/FfmpegGui/Services/ExifToolService.cs:510-516`
#   （CopyMetadataAsync；DNG 出口 protectColorMetadata=false ⇒ 走的就是这一条）。
#   默认档下 BuildAbsorbExclusions(null) 返回空串 ⇒ 无隐私排除；dropOrientation=true ⇒ 尾随 `--Orientation `。
#   改这里之前先想清楚：任何"顺手调整顺序"都会造出**假到达数**（§39 实测：非规范顺序 217 条、产品形状 0 条）。
PRODUCT_CARRY_ARGS = ['-overwrite_original', '-m', '-TagsFromFile', '@SRC@',
                      '-all:all', '-ICC_Profile', '--Orientation', '@DST@']


def productCarryProbe(src, dng):
    """把产物复制一份，按**产品命令形状**跑一次后置搬运。返回 (临时件, exiftool rc, 回话)。"""
    tmp = os.path.join(os.path.dirname(os.path.abspath(dng)) or '.',
                       '_privpost-productshape.dng')
    shutil.copyfile(dng, tmp)
    cmd = [EX] + [a.replace('@SRC@', src).replace('@DST@', tmp) for a in PRODUCT_CARRY_ARGS]
    p = subprocess.run(cmd, capture_output=True, text=True,
                       encoding='utf-8', errors='replace')
    return tmp, p.returncode, ((p.stdout or '') + (p.stderr or '')).strip()


def measureCarry(srcGroup, inv, group):
    """同一把尺量"这一组到了几条"。返回 (到达数, 缺项名列表, 该组非布局键总数)。"""
    names = sorted(k for k in srcGroup if not LAYOUT_NAME.search(k))
    got = inv.get(group, {})
    arrived = [k for k in names if k in got]
    missing = [k for k in names if k not in got]
    return len(arrived), missing, len(names)

# 已点名的「有标准载体、但本轮还没换算」条目（账本 §21）；新增同类一律判红
KNOWN_UNTRANSLATED = {
    'PhaseOne:SensorDefects': '标准载体 = opFixBadPixelsList(opcode 5)，待换算',
}


def inventory(path):
    p = subprocess.run([EX, '-a', '-u', '-G1', '-s', '-e', path],
                       capture_output=True, text=True, encoding='utf-8', errors='replace')
    out = {}
    for line in (p.stdout or '').splitlines():
        m = re.match(r'^\[([^\]]+)\]\s+(\S+)\s*:\s(.*)$', line.strip())
        if m:
            out.setdefault(m.group(1), {})[m.group(2)] = m.group(3).strip()
    return out


def priv(g):
    return {k: v for k, v in g.items()
            if not any(k.startswith(x) for x in SKIP_GROUP_PREFIX)}


def main(argv):
    if len(argv) < 3:
        print('PRIV-CARRY FAIL 用法: <源 raw> <产物 dng>')
        return 2
    s, d = inventory(argv[1]), inventory(argv[2])
    if not s or not d:
        print('PRIV-CARRY FAIL 读取失败（源组=%d 产物组=%d）' % (len(s), len(d)))
        return 2
    ps, pd = priv(s), priv(d)
    shared = sorted(set(ps) & set(pd))
    lost, bad_vals, parked, compared = [], [], [], 0
    for g in shared:
        for k, v in ps[g].items():
            if LAYOUT_NAME.search(k):
                continue
            full = '%s:%s' % (g, k)
            if k not in pd[g]:
                # 三类"缺席"各有来路，都**点名**而不是一律判红：
                #   · 未命名私有 ID（`组_0xHHHH`）⇒ 连名字都没有 ⇒ 无标准位；
                #   · 二进制载荷（exiftool 自己只印 `(Binary data N bytes…)`，逐字对撞无从做起）
                #     ⇒ 复制工具不写、也没有命名标准位（实测 Canon:CRWParam/Flavor、Minolta:PreviewImage）；
                #   · 已点名的"有标准载体但还没换算"（PhaseOne:SensorDefects ⇒ opFixBadPixelsList）。
                # 其余任何**有名字的标量条目**在源里有、产物里没有 ⇒ 判红，这才是这把尺的牙。
                if full in KNOWN_UNTRANSLATED:
                    parked.append('%s=%s（%s）' % (full, v[:24], KNOWN_UNTRANSLATED[full]))
                elif UNNAMED.search(k) or v.startswith('(Binary data'):
                    parked.append('%s=%s（%s）' % (full, v[:24],
                                                  '未命名私有 ID⇒无标准位' if UNNAMED.search(k)
                                                  else '二进制载荷⇒无命名标准位且复制工具不写'))
                else:
                    lost.append('%s=%s' % (full, v[:34]))
            else:
                compared += 1
                if pd[g][k] != v:
                    bad_vals.append('%s 源=[%s] 产物=[%s]' % (full, v[:26], pd[g][k][:26]))
    gone = sorted(set(ps) - set(pd))
    ext = os.path.splitext(argv[1])[1].lower()
    suspect = []
    # 现场测量的**基线件**：调用方可给第 3 个参数 = 同一件源用 dngtool 直出、未跑产品后置的产物
    # （闸 ㉑ 传的那个）。没给就退化成"被检产物自己"，并在文案里如实写明是哪一种基线。
    base = argv[3] if len(argv) > 3 and os.path.exists(argv[3]) else argv[2]
    baseLabel = 'dngtool 直出件(未后置)' if base != argv[2] else '被检产物本身'
    openDefects = [g for g in gone
                   if not any(g.startswith(x) for x in NO_WRITABLE_TARGET)
                   and (g, ext) in KNOWN_OPEN_DEFECT]
    ncBreak = []          # 现场测量这把尺自己的破口（跑不起来 / 基线不是"缺=全部"）⇒ 判红
    for g in openDefects:
        bInv = inventory(base)
        mb, missB, tot = measureCarry(ps[g], bInv, g)
        tmp, prc, ptalk = productCarryProbe(argv[1], base)
        if prc != 0:
            print('   WARN 可修缺口（未判红，但要点名） %s=%d 条：measured=未测'
                  '（后置探针 exiftool rc=%d ⇒ 只记未判、不记绿）:: %s'
                  % (g, len(ps[g]), prc, ptalk[:160]))
            ncBreak.append('%s 后置探针跑不起来（exiftool rc=%d）⇒ 到达数无从测' % (g, prc))
            continue
        pInv = inventory(tmp)
        mp, missP, _ = measureCarry(ps[g], pInv, g)
        # 负控（同一把尺）：未跑后置的基线件必须报"缺=全部"，否则"到达数"是尺子自己造出来的
        if len(missB) != tot:
            ncBreak.append('%s 负控破口：未后置基线本应缺=全部(%d)，实缺 %d ⇒ 这把尺在基线上就已到达'
                           % (g, tot, len(missB)))
        print('   WARN 可修缺口（未判红，但要点名） %s=%d 条：%s'
              % (g, len(ps[g]), KNOWN_OPEN_DEFECT[(g, ext)]))
        print('     现场测量 基线=%s · 未后置侧 measured=%d/%d（缺=%d）· 产品命令形状后置侧 '
              'measured=%d/%d（缺=%d，探针件 %s，exiftool rc=%d）'
              % (baseLabel, mb, tot, len(missB), mp, tot, len(missP),
                 os.path.basename(tmp), prc))
        print('     缺项逐名（后置侧，前 40 条）: %s' % (', '.join(missP[:40]) or '无'))
    for g in gone:
        if any(g.startswith(x) for x in NO_WRITABLE_TARGET):
            continue
        if g in openDefects:
            continue
        suspect.append('%s=%d 条整组未携带，且不在任何点名表里' % (g, len(ps[g])))
    for k in suspect:
        print('   FAIL 未点名的整组丢失 %s' % k)
    print('PRIV-CARRY 厂商组 源=%d 产物=%d ｜ 两组都在=%s ｜ 对撞键数=%d 组内缺键=%d 值不同=%d'
          % (len(ps), len(pd), ','.join(shared) or '无', compared, len(lost), len(bad_vals)))
    for k in lost[:10]:
        print('   FAIL 组内缺键 %s' % k)
    for k in bad_vals[:10]:
        print('   FAIL 值不同 %s' % k)
    for k in parked[:12]:
        print('   已点名未转 %s' % k)
    if gone:
        print('   丢弃点名（整组未携带）: ' + ', '.join(
            '%s=%d%s' % (g, len(ps[g]),
                         '（无可写目标⇒只能靠换算进标准载体）'
                         if any(g.startswith(x) for x in NO_WRITABLE_TARGET) else '')
            for g in gone))
    for k in ncBreak:
        print('   FAIL 现场测量这把尺破口 %s' % k)
    if not shared or compared < 30:
        # 源侧本来就没有多少厂商注记条目（本机实测：扫描仪 NEF 只有 8 条）⇒ **不给绿灯**，
        # 但也不假装它是缺陷：报 SKIP，由闸按"真判件数"下限去防整批空跑。
        print('PRIV-CARRY SKIP 对撞键数只有 %d、两组都在的厂商组=%s ⇒ 素材太小，未判（不算绿）'
              % (compared, ','.join(shared) or '无'))
        return 0 if not lost and not bad_vals and not ncBreak else 1
    ok = not lost and not bad_vals and not suspect and not ncBreak
    print('PRIV-CARRY %s' % ('OK 携带的厂商注记组逐条同值且组内不缺' if ok else 'FAIL'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
