#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""_check-exif-standard-coverage.py —— 标准位覆盖率对撞（第 77 条 ⑳ 组用）

规矩（2026-10-19 用户定）：**凡有 EXIF/GPS 标准位、任何 conformant 读者都能解码的，必须出现在产物里**；
不能解码的厂商私有件一律不进产物。读数一律由 exiftool（第二个独立实现）给出，不读我方写侧日志。

白名单只用**可复核的 SDK 限制**（本机源码实测）：
  0x011A/0x011B/0x0128 —— writer 只在传入 dng_resolution 时写（dng_image_writer.cpp:7013-7027），
                           而 dng_negative::BuildExifBlock 的该参数默认 NULL（dng_negative.h:423）
  0x9101/0x9102         —— tcComponentsConfiguration / tcCompressedBitsPerPixel 在
                           dng_image_writer.cpp 里不存在（grep 0 命中）⇒ 无发射路径
  0x0001/0x0002         —— InteropIFD：SDK 无写入路径（tcInteropIndex 0 命中）

用法:
  py _check-exif-standard-coverage.py <源 raw/dng> <产物 dng> [--allow 0x011A,0x011B,...]
  py _check-exif-standard-coverage.py <文件> --list              # 只列这个文件的标准位
退出 0 = 覆盖完整；1 = 判红（点名缺哪些 tag ID / 值不等）；2 = 用法/解析失败。

两档判据（**存在性不够，还要值**）：
  ① 存在性：源里的标准位必须能在产物里读到（同 tag ID、同 IFD 档）。
  ② 值：交集标签逐条比 exiftool -v2 打出的「显示值 (原始分数)」整串 ⇒ SDK 若把
     0x9204 的 -1/3 写成 1/-3 也算不同（这里要的是"逐字同值"，语义等价由白名单点名）。
  ⚠ 空值标签（源里有 tag 但值为空串，如本仓 7S 的 0x010E ImageDescription）不进分母
    ——它没有内容可搬 ⇒ 但必须打印 空值跳过=<名字>，因为"跳过"与"漏了"不能靠沉默区分。
"""
import re
import subprocess
import sys
from fractions import Fraction

EX = r'C:\PLAN\ffmpegPictureUI\publish\PLAN\exiftool\exiftool.exe'
# ⚠ "有 EXIF 标准位"这句话不由我说了算：注册表交给**读者自己**——exiftool 把每个标签按它命中的
#   **表**归组：公开注册表是 `IFD0 / ExifIFD / GPS / InteropIFD`，厂商自己的标签落在
#   `Panasonic / PanasonicRaw / Sony / CanonMsg / Olympus / Pentax …`。只数前者 ⇒ 我既不会把厂商
#   私有 ID 当成"必须搬的标准位"（跨厂商实测：RW2 的 104 条里 82+22 条属于厂商表），也不会因为
#   "exiftool 念得出名字"就把私有件算成标准位。剔除的条数一律打印，绝不静默。
STD_GROUPS = {'IFD0', 'ExifIFD', 'GPS', 'InteropIFD'}


def name_groups(path):
    # 返回 {标签名: {它出现在哪些组}}；用 -G1 读，与 -v2 的 ID 档互相独立
    out = subprocess.run([EX, '-G1', '-s', '-a', '-e', '-api', 'RequestAll=3', path],
                         capture_output=True, text=True, encoding='utf-8',
                         errors='replace').stdout
    d = {}
    for line in out.splitlines():
        if not line.startswith('[') or ']' not in line:
            continue
        grp = line[1:line.index(']')]
        rest = line[line.index(']') + 1:].split(':', 1)
        if len(rest) < 2:
            continue
        d.setdefault(rest[0].strip(), set()).add(grp)
    return d
NAME_RE = re.compile(r'^[ |\d()+-]*\d+\)\s+([^=]+?)\s*=\s*(.*)$')
TAG_RE = re.compile(r'-\s+Tag 0x([0-9a-fA-F]{4})')
DIR_RE = re.compile(r'\+\s+\[(\w+)\s+directory with (\d+) entries\]')
# IFD0/ExifIFD/Interop 同属 TIFF 目录、ID 空间一致 ⇒ 合并一档；GPS 的 ID 会撞（0x0001 既是
# InteropIndex 又是 GPSLatitudeRef）⇒ 必须单独一档。
MAIN_DIRS = ('IFD0', 'ExifIFD', 'InteropIFD')
# ⚠ 分母只收"**描述场景/相机/设置**"的那一档；描述**这份文件自己的像素布局**的目录一律不进。
#   原因（跨厂商实测撞出来的判据缺陷）：Leica RWL / Panasonic RW2 的 IFD0 有 60~70 条
#   `ImageWidth/BitsPerSample/StripOffsets/预测器/厂商私有几何` 这类结构标签 ⇒ 把它们算进
#   "必须搬"的分母，会报出 54 条假缺项，把真缺项淹掉。结构标签**照抄**更糟：产物会对着
#   自己的像素说谎（源 Compression=6 而 DNG 无损档是 7）。
#   SubIFD*/MakerNotes/PrintIM/GeoTiff/ExifTHUMB 等目录同理 ⇒ 只登记、只打印，不进分母。
STRUCT_DIRS = ('SubIFD', 'SubIFD1', 'SubIFD2', 'MakerNotes', 'PrintIM', 'GeoTiff',
               'ExifTHUMP', 'Adobe_CM', 'JpegTables', 'IccView', 'MixMP', 'OtherStream')
# ⚠ IFD0 的分母是**白名单**（只数描述性的那些），不是黑名单（剔结构）——原因见上面的实测：
#   RW2/RWL 的 IFD0 用的是 TIFF/EP 那套图像几何 ID（0x0008/0x0009/0x000A/0x000B/0x0017/0x0018…），
#   黑名单永远补不全，而且这些都是"必须让产物自己描述"的值。
IFD0_DESC = {0x010E, 0x010F, 0x0110, 0x0112, 0x011A, 0x011B, 0x0128, 0x0131, 0x0132, 0x013B, 0x8298}
# 旧的结构性黑名单留着只为**打印**（让人看得见被剔掉的是什么），不再参与分母计算。
IFD0_STRUCT = {0x00FE, 0x0100, 0x0101, 0x0102, 0x0103, 0x0106, 0x0111, 0x0115, 0x0116, 0x0117,
               0x011C, 0x011D, 0x011E, 0x011F, 0x0120, 0x0121, 0x0122, 0x0123, 0x0124, 0x0125,
               0x013A, 0x0140, 0x0141, 0x0142, 0x0143, 0x0144, 0x0145, 0x0146, 0x0147, 0x014A,
               0x014B, 0x0153, 0x0200, 0x0201, 0x0202, 0x0203, 0x0204, 0x0205, 0x0206, 0x0207,
               0x0208, 0x0209, 0x020A, 0x020B, 0x020C, 0x020D, 0x020E, 0x020F, 0x0210, 0x0211,
               0x0212, 0x0213, 0x0214, 0x022F, 0x014C, 0x0150, 0x0151, 0x0152}


def parse(path):
    """把 exiftool -v2 的目录树压成 {tagID: (名字, 值)} 两档（主 IFD 档 / GPS 档）+ 每个 ID 的归属目录。

    ⚠ 归属必须由**缩进深度栈**决定，不能"记最后一个目录名"：子目录走完后同级标签还会继续出现
      （本机实测踩到 —— GPS 子目录之后 IFD0 剩下的 0xc6xx 标签全被记进了 GPS 档，
       产物 GPS 档读数从 7 虚高到 28 ⇒ "缺=0"会因为分档错了而假过）。
      深度取标记字符所在的列：目录行取 '+'，标签行取 '-'；进入新目录时压栈，
      遇到深度 <= 栈顶的行时弹栈 —— 弹到比自己浅的那一层为止。
    """
    out = subprocess.run([EX, '-v2', '-a', '-e', path], capture_output=True,
                         text=True, encoding='utf-8', errors='replace').stdout
    main, gps, other, dirs = {}, {}, {}, {}
    stack = []                      # [(该目录**条目**所在的竖线层数, 目录名)]
    pending = None

    def bars(line, idx):
        return line[:idx].count('|')

    for line in out.splitlines():
        m = DIR_RE.search(line)
        if m:
            # 目录行自己挂在父目录的条目层 ⇒ 它的条目比它深一层。
            # ⚠ 不能用标记字符的**列号**：本机实测 IFD0 的条目在 1 层竖线，而它的子目录
            #   （ExifIFD/GPS）目录行也在 1 层竖线、条目在 2 层 —— 用列号会让"回到父目录"
            #   这一弹栈条件永不成立，于是子目录之后的 IFD0 标签继续记进子目录档。
            depth = bars(line, line.index('+')) + 1
            while stack and stack[-1][0] > depth:
                stack.pop()
            stack.append((depth, m.group(1)))
            pending = None
            continue
        m = TAG_RE.search(line)
        if m:
            depth = bars(line, line.index('-'))
            while stack and stack[-1][0] > depth:
                stack.pop()
            cur = stack[-1][1] if stack else None
            tid = int(m.group(1), 16)
            tgt = gps if cur == 'GPS' else (main if cur in MAIN_DIRS else None)
            if tgt is None and pending is not None and cur in STRUCT_DIRS:
                other.setdefault(tid, (cur, pending))   # 只登记，不进分母（见 STRUCT_DIRS 的说明）
            if tgt is not None and pending is not None:
                if tid not in tgt:
                    tgt[tid] = pending
                    dirs[tid] = cur
                else:
                    dirs.setdefault(tid, cur)
            pending = None
            continue
        m = NAME_RE.match(line)
        if m:
            pending = (m.group(1).strip(), m.group(2).strip())
    if not main:
        raise ValueError('没解析出任何 IFD0/ExifIFD 条目（exiftool -v2 的口径变了？）')
    return main, gps, dirs, other


def main(argv):
    if len(argv) < 3:
        print('用法: _check-exif-standard-coverage.py <源> <产物> [--allow 0x…] | <文件> --list')
        return 2
    try:
        if '--list' in argv:
            m, g, _, _ = parse(argv[1])
            print('STANDARD-LIST 标准位=%d GPS=%d' % (len(m), len(g)))
            for t in sorted(m):
                print('   0x%04X %-28s = %s' % (t, m[t][0], m[t][1][:52]))
            for t in sorted(g):
                print('   GPS 0x%04X %-24s = %s' % (t, g[t][0], g[t][1][:52]))
            return 0
        allow = set()
        if '--allow' in argv:
            allow = {int(x, 16) for x in argv[argv.index('--allow') + 1].split(',') if x.strip()}
        sm, sg, smd, other = parse(argv[1])
        dm, dg, dmd, _ = parse(argv[2])
    except (ValueError, OSError) as exc:
        print('EXIF-COVERAGE FAIL 解析失败: %s' % exc)
        return 2

    ZERO_RE = re.compile(r'^\s*(?:0|\d+\s*\(0/\d+\)|\(0/\d+\)|0\.0+)\s*$')
    # TIFF 的"未定义有理数"就是 `0/0`（分母为 0 ⇒ 没有值），exiftool 显示成 `undef (0/0)`；
    # UserComment 只带字符集前缀（`ASCII`/`UNICODE`）而正文为空 ⇒ 也是没有内容。
    # 这两形**过去**没被认成空 ⇒ SRW 的 0x920A 与 CR3 的 0xA430 被报成"标准位丢失"（假红）。
    UNDEF_RATIONAL_RE = re.compile(r'^\s*(?:undef\s+)?\(0/0\)\s*$')
    CHARSET_ONLY = {'ASCII', 'UNICODE', 'JIS', 'Undefined'}

    def rel_dev(a, b):
        # 两边都能解析成同一个量纲时给出相对偏差；解析不了（字符串/枚举档）返回空串。
        # 立这条读数的原因：APEX 那一族（0x9201/0x9202/0x9203/0x9205）由两条换算路径给出
        # —— 一条是 LibRaw/DNG SDK 从浮点反算，一条是 exiftool 复制时经 PrintConv 往返
        # —— 两边都不是源件那个分数的逐位拷贝。"差 0.04%" 与 "差了 3 档" 在判据里必须分开，
        # 而这只能靠**量出来**，不能靠把标签整个放行。
        ka, kb = num_key(a), num_key(b)
        try:
            xs = [float(x) for x in (ka if ka is not None else ())]
            ys = [float(y) for y in (kb if kb is not None else ())]
            if len(xs) != len(ys) or not xs:
                return ''
            d = max(abs(x - y) / max(abs(x), 1e-9) for x, y in zip(xs, ys))
            return ' · 相对偏差=%.3g' % d
        except (TypeError, ValueError):
            return ''

    def is_empty_val(v):
        v = v.strip()
        return (v in ('', '(none)', '-') or v in CHARSET_ONLY
                or bool(UNDEF_RATIONAL_RE.match(v)) or bool(ZERO_RE.match(v)))
    # **允许"源值空/零 ⇒ 产物缺席"的标签是一份点名名单，不是"凡零值都放行"**：
    # 本机实测（7S 无镜头件 vs 同法产物，逐个标签查在场与否）——零/空值 11 条里只有这 4 条
    # 真的缺席：0x010E(空串)、0x829D、0x920A、0xA405（SDK 按 EXIF 的 "0=unknown" 不写）。
    # 其余 7 条（ExposureCompensation/MaxApertureValue/CustomRendered/SceneCaptureType/
    # Contrast/Saturation/Sharpness）源值也是 0 而产物**照样在场** ⇒ 它们不许走这条后门。
    # ⇒ 名单外的零/空值标签缺席一律判红。牙在 ⑳f：往源件注入非零 FNumber/FocalLength/In35mm，
    #   同一把尺必须要求这三条在场且同值（证明这条轴是"值"，不是"标签"）。
    OMIT_IF_ZERO_OR_EMPTY = {0x010E, 0x829D, 0x920A, 0x9286, 0xA405}
    # **字节布局自指**档：这两条的含义就是"**这份文件里**预览/缩略图 JPEG 的起始偏移与长度"
    # ⇒ 从源件照抄会让产物指到自己文件的错误位置，是撒谎而不是搬运。同一内容（预览的字节长度）
    # 由产物自己的结构位承载 ⇒ 闸用 ⑳j 拿"源 0x0202 == 产物 PreviewImageLength"对撞来补这一格。
    BYTE_LAYOUT = {0x0201, 0x0202}

    def empty_or_zero_only(d):
        return {t for t, (nm, v) in d.items()
                if t in OMIT_IF_ZERO_OR_EMPTY and is_empty_val(v)}

    # 注：exiftool 的 -v2 段里条目名不带组前缀 ⇒ 这里按"目录归属"过滤：main 桶里出现过的
    #     IFD0 结构标签用位置法剔除（同一桶里 ExifIFD 的 ID 空间不重叠，除 0x011A 这类同名不同档）
    # IFD0 档里剔掉 TIFF 结构标签（值就是这份文件自己的字节布局/编码参数）；
    # ExifIFD / Interop / GPS 三档整档都是描述性的 ⇒ 一条不剔。
    sifd0 = {t for t, d in smd.items() if d == 'IFD0' and t not in IFD0_DESC}
    sm = {t: v for t, v in sm.items() if t not in sifd0}
    # 第二刀：只留**公开注册表**里的名字（exiftool 的归组给出）。第一版没这一刀，
    # 于是 Leica RWL / Panasonic RW2 的 53 条厂商私有 ID 被当成"必须搬的标准位"，
    # 真缺项反而被淹掉 —— 是跨厂商跑量撞出来的，不是推出来的。
    src_groups = name_groups(argv[1])
    unreg = {t for t, (nm, v) in sm.items() if not (src_groups.get(nm, set()) & STD_GROUPS)}
    sm = {t: v for t, v in sm.items() if t not in unreg}
    se, ge = empty_or_zero_only(sm), empty_or_zero_only(sg)
    miss = sorted(set(sm) - se - set(dm) - allow - BYTE_LAYOUT)
    gmiss = sorted(set(sg) - ge - set(dg) - allow)
    lay = sorted((set(sm) - set(dm)) & BYTE_LAYOUT)
    waived = sorted((set(sm) - set(dm)) & allow)
    print('EXIF-COVERAGE 源标准位=%d(空/零值跳过=%d, 结构自指剔除=%d) 产物=%d 缺(不豁免)=%d 白名单内=%s ｜ '
          'GPS 源=%d(空/零值跳过=%d) 产物=%d 缺=%d ｜ 厂商命名空间剔除=%d '
          'IFD0 非描述性剔除=%d 其他目录条目(只登记)=%d'
          % (len(sm), len(se), len(sifd0), len(dm), len(miss),
             ','.join('%04X' % t for t in waived) or '无',
             len(sg), len(ge), len(dg), len(gmiss), len(unreg), len(sifd0), len(other)))
    if lay:
        print('   字节布局自指(不照抄, 由 ⑳j 用产物自己的结构位对撞同一内容)=%s'
              % ','.join('%04X:%s' % (t, sm[t][0]) for t in lay))
    if se or ge:
        print('   按 unknown 省略(点名名单内, 其余零值标签缺席一律判红)=%s' % ','.join(
            ['%s%04X:%s' % ('GPS ' if t in ge else '', t, (sg if t in ge else sm)[t][0])
             for t in sorted(se | ge)]))
    for t in miss:
        print('   FAIL 缺 0x%04X %-26s 源值=%s' % (t, sm[t][0], sm[t][1][:48]))
    for t in gmiss:
        print('   FAIL 缺 GPS 0x%04X %-23s 源值=%s' % (t, sg[t][0], sg[t][1][:48]))

    # ② 值档：交集逐字比「显示值 (原始分数)」整串
    vbad, vform, vskip = [], [], []
    # 0x0103 Compression: 结构标签，描述的是**这份产物自己的**像素编码
    #   （源 ARW=6 JPEG-in-TIFF；DNG 无损档=7 lossless JPEG、JXL 档=35001）⇒ 不等才是对的，
    #   逐字相同反而是产物对自己的像素说谎。用 --values-strict 把它也拉回分母（当对照臂用）。
    # 放行的是**两份不同性质的东西**，都点名、都不是"凡不等都放行"：
    #   0x0103 Compression —— 描述这份产物自己的像素编码（源=6 JPEG-in-TIFF，产物=7/35001）；
    #   0x010F/0x0110/0x0131/0x9000 —— 生产者身份件：Make/Model 由 SDK 按 Adobe 的机型库键名
    #     写成去厂商前缀的机型名（Adobe 自己的转换器也这么写，PS 按那个键查镜头/机身库），
    #     Software 是"谁生成了这份产物"的诚实署名，ExifVersion 由 DNG 规范声明。
    #   ⇒ 这四条照抄源值反而让 PS 认不出机型；它们**不属于**"源里有、普通软件能解码、却不搬"。
    #   `--values-strict`（对照臂）把它们全部拉回分母。
    PRODUCER_DECLARED = {0x010F, 0x0110, 0x0131, 0x9000}
    vallow = set() if '--values-strict' in argv else {0x0103} | PRODUCER_DECLARED

    DATE_RE = re.compile(r'^(\d{4})[.:](\d{2})[.:](\d{2})(.*)$')

    def canon_date(v):
        # `2021.01.03 10.17.04` 与 `2021:01:03 10:17:04` 是同一个瞬间的两种写法
        # —— 源件用 '.'（部分扫描仪/机身这么写），exiftool 写入时规范化成 EXIF 的 ':'。
        # 只规范化分隔符，不解析时区、不四舍五入 ⇒ 不会把"改了一个数"洗成相等。
        m = DATE_RE.match(v.strip())
        if not m:
            return v.strip()
        rest = m.group(4)
        rest = re.sub(r'([ .])', lambda x: ':' if x.group(1) == '.' else x.group(1), rest)
        return '%s:%s:%s%s' % (m.group(1), m.group(2), m.group(3), rest)

    def num_key(v):
        # 把 `8 (80/10)` / `70 200 0 0 (70/1 200/1 0/1 0/1)` 的分数部分解析成有理数元组；
        # 解析不了（字符串/枚举/整数档）返回 None ⇒ 调用方退回逐字比对。
        i = v.find(' (')
        if i < 0 or not v.endswith(')'):
            return None
        toks = v[i + 2:-1].split()   # i 指向分数段前那个空格，+2 才跳过左括号
        if not toks or not all(re.match(r'^-?\d+/\d+$', x) for x in toks):
            return None
        try:
            return tuple(Fraction(x) for x in toks)
        except (ValueError, ZeroDivisionError):
            return None

    if '--values' in argv:
        for bucket_s, bucket_d, label in ((sm, dm, ''), (sg, dg, 'GPS ')):
            for t in sorted(set(bucket_s) & set(bucket_d) - allow - vallow):
                a, b = bucket_s[t][1], bucket_d[t][1]
                if a == b or canon_date(a) == canon_date(b) or (is_empty_val(a) and is_empty_val(b)):
                    continue
                if a == '[snip]' or b == '[snip]':
                    # exiftool 对"长/二进制值"在 -v2 里直接印 `[snip]` ⇒ 这一格**我的读法看不见值**。
                    # 实测 Olympus E-10 的 0x9286：`-b` 取出的字节数是 **0** ⇒ 源里本就没有正文，
                    # 产物写进去的也只有字符集前缀 ⇒ 不是"丢了内容"，是"没有内容可比"。
                    # 登记成 不可比（计数、点名），不许混进"不等"，也不许悄悄从分母里消失。
                    vskip.append('%s0x%04X %s: 源=[%s] 产物=[%s]' % (label, t, bucket_s[t][0], a[:20], b[:20]))
                    continue
                ka, kb = num_key(a), num_key(b)
                # exiftool 写入时会把分数约分（800/100 ⇒ 8/1、450/100 ⇒ 45/1、5074/2560 ⇒ 2537/1280）
                # ⇒ 有理数相同而字面不同 = 同一个数换了写法，PS 读出的值一致，不是内容丢失。
                # 真漂移（9.375 vs 9.3786、2.06 vs 2）分数不同 ⇒ 照样判红。
                if ka is not None and ka == kb:
                    vform.append('%s0x%04X %s: 源=[%s] 产物=[%s]' % (label, t, bucket_s[t][0], a[:34], b[:34]))
                    continue
                vbad.append('%s0x%04X %s: 源=[%s] 产物=[%s]%s' % (
                    label, t, bucket_s[t][0], a[:34], b[:34], rel_dev(a, b)))
        devs = [float(x) for x in re.findall(r'相对偏差=([0-9.eE+-]+)', ' '.join(vbad))]
        print('EXIF-VALUES 交集逐字对撞 比较=%d 不等=%d 同值异形式=%d 不可比=%d 最大相对偏差=%s 不等标签=%s 放行=%s'
              % (len(set(sm) & set(dm)) + len(set(sg) & set(dg)) - len(vallow), len(vbad),
                 len(vform), len(vskip),
                 ('%.3g' % max(devs)) if devs else 'n/a',
                 ','.join(sorted({re.search(r'0x[0-9A-Fa-f]{4}', x).group(0) for x in vbad})) or '无',
                 ','.join('%04X' % t for t in sorted(vallow)) or '无'))
        for line in vform:
            print('   形式 %s' % line)
        for line in vskip:
            print('   不可比 %s' % line)
        for line in vbad:
            print('   FAIL 值不等 %s' % line)

    ok = not miss and not gmiss and not vbad
    print('EXIF-COVERAGE %s' % ('OK 源里的标准位在产物里全部可解码且同值' if ok else 'FAIL'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
