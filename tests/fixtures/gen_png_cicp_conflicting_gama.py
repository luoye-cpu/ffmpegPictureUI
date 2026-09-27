#!/usr/bin/env python3
"""生成 `tests/fixtures/png_cicp_conflicting_gama.png` —— #31(ii) 门禁负控夹具。

用途（不是被测物，是**尺子的对照件**）：门禁要断言"产品产物写了 cICP 就不该再留
gAMA/cHRM/sRGB"。没有对照件时，这条断言可能只是因为**遍历读不到那些块**而恒真
（本仓就踩过一次：PowerShell 里 `[byte]0x10 -shl 8` 被截断成 0 ⇒ 任何 IDAT>255 B 的
PNG 都从第一个 IDAT 起失步）。所以夹具必须**同时**满足：
  1) 真实存在 cICP + gAMA + cHRM 三块（⇒ "读法能抓到"这一条有牙）；
  2) 每个块 CRC 正确、ffmpeg `-v error` 解码零告警（⇒ 不是脏文件被放过）。

重新生成（改分辨率/换曲线时跑这个即可，产物要一并提交）：
    publish/PLAN/ffmpeg-full/ffmpeg.exe -y -v error -f lavfi -i color=c=red:s=16x16:d=1 \\
        -frames:v 1 -pix_fmt rgb24 <临时源>.png
    <产品> --headless -i <临时源>.png -f png --bit-depth 8 --color-space sRGB --color-engine engine -o <目录>
    python tests/fixtures/gen_png_cicp_conflicting_gama.py <产品产物>.png
"""
import pathlib
import struct
import subprocess
import sys
import zlib

GAMA = 45455           # γ = 1/2.2 —— 与 cICP.transfer=13(sRGB 分段) 数值矛盾，正是要造的形状
CHRM = (0.3127, 0.3290, 0.6400, 0.3300, 0.3000, 0.6000, 0.1500, 0.0600)   # D65 + BT.709/sRGB 原色
STRIP = (b'gAMA', b'cHRM', b'sRGB')          # 先归一化，保证夹具形状唯一


def chunk(type_: bytes, data: bytes) -> bytes:
    body = type_ + data
    return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xFFFFFFFF)


def build(src_png: str, dst_png: str) -> int:
    b = pathlib.Path(src_png).read_bytes()
    if b[:8] != b'\x89PNG\r\n\x1a\n':
        raise SystemExit('输入不是 PNG：%s（先按文件头注释里的命令产出带 cICP 的产物）' % src_png)
    ihdr_len = struct.unpack('>I', b[8:12])[0]
    end_ihdr = 8 + 12 + ihdr_len
    kept, i = [], end_ihdr
    has_cicp = False
    while i + 12 <= len(b):
        ln = struct.unpack('>I', b[i:i + 4])[0]
        typ = b[i + 4:i + 8]
        if typ == b'cICP':
            has_cicp = True
        if typ not in STRIP:
            kept.append((typ, b[i + 8:i + 8 + ln]))
        if typ == b'IEND':
            break
        i += 12 + ln
    if not has_cicp:
        raise SystemExit('输入里没有 cICP ⇒ 造不出"与低优先级块并存"的形状。'
                         '请用产品（engine + --color-space sRGB）的产物做输入。')
    out = bytearray(b[:end_ihdr])
    out += chunk(b'gAMA', struct.pack('>I', GAMA))
    out += chunk(b'cHRM', struct.pack('>8I', *(int(v * 100000 + 0.5) for v in CHRM)))
    for typ, data in kept:
        out += chunk(typ, data)
    dst = pathlib.Path(dst_png)
    dst.write_bytes(bytes(out))

    # 自验：块序 + CRC 全对 + 尺寸（不信任"写成功了"这件事，重新读回来量）
    rb = dst.read_bytes()
    names, bad, i = [], 0, 8
    while i + 12 <= len(rb):
        ln = struct.unpack('>I', rb[i:i + 4])[0]
        typ = rb[i + 4:i + 8]
        body = rb[i + 4:i + 8 + ln]
        want = struct.unpack('>I', rb[i + 8 + ln:i + 12 + ln])[0]
        if (zlib.crc32(body) & 0xFFFFFFFF) != want:
            bad += 1
        names.append(typ.decode('latin1'))
        if typ == b'IEND':
            break
        i += 12 + ln
    need = {'IHDR', 'cICP', 'gAMA', 'cHRM'}
    print('wrote %s (%d B) chunks=%s bad_crc=%d' % (dst, len(rb), names, bad))
    if bad or not need.issubset(set(names)):
        raise SystemExit('夹具自检失败（缺块或 CRC 错）')
    # ⚠ **对照件必须足够大，让 IDAT 跨过 255 B**：本仓踩过 PowerShell 的
    #   `[byte]0x10 -shl 8 == 0`（移位保留左操作数类型 ⇒ 长度高字节被截断），
    #   症状正是"从第一个大 IDAT 起遍历失步"。夹具只有几十字节时，这条 bug 会躲过对照件
    #   ⇒ "读法抓得到 cICP+gAMA" 就没牙了。写成硬自检，别让以后换素材的人无意削弱它。
    i, max_idat = 8, 0
    while i + 12 <= len(rb):
        ln = struct.unpack('>I', rb[i:i + 4])[0]
        if rb[i + 4:i + 8] == b'IDAT':
            max_idat = max(max_idat, ln)
        if rb[i + 4:i + 8] == b'IEND':
            break
        i += 12 + ln
    print('       最长 IDAT = %d B（对照件要求 > 255 B，否则类型截断类 bug 不会被暴露）' % max_idat)
    if max_idat <= 255:
        raise SystemExit('夹具 IDAT 太短 ⇒ 对照件没牙。换更大的源（如 64x64 噪声）重新生成。')
    return 0


if __name__ == '__main__':
    here = pathlib.Path(__file__).resolve().parent
    src = sys.argv[1] if len(sys.argv) > 1 else None
    if not src:
        raise SystemExit('用法：gen_png_cicp_conflicting_gama.py <带 cICP 的产物 PNG>')
    sys.exit(build(src, str(here / 'png_cicp_conflicting_gama.png')))
