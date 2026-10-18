"""造一个"某项改动已被抹平"的合成 ARW: 把生效版本目录里某个 tag 的值改成初始版本的值。

定位不靠自己的第二份解析器, 而是问 `dngtool -idc` 要它自己用的地址
(versionOffsets[0] = 初始版, chosenOffset = 生效版) —— 夹具与被判据共用同一个读者,
所以定位错会当场一起错, 不会"夹具悄悄指到别处"。
抹平后再由 exiftool 独立确认该 tag 两版同值, 才算夹具成立。

用法: py _make-synth-idc-revert.py <源.arw> <出.arw> <tag 如 0x8016>
"""
import argparse
import json
import struct
import subprocess
import sys

TOOL = r"C:\PLAN\ffmpegPictureUI\publish\PLAN\artifacts\dngtool.exe"
TSZ = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 4, 10: 8}


def idc_json(path):
    p = subprocess.run([TOOL, "-idc", path], capture_output=True, text=True, errors="replace")
    if p.returncode != 0:
        raise SystemExit("REJECT -idc 读不动 %s: %s" % (path, (p.stdout + p.stderr).strip()[:120]))
    return json.loads(p.stdout)


def find_entry(buf, dirOff, tag):
    """在一个版本目录里找 tag; 返回 (值字段地址, 现值)。要求恰好一条且内联。"""
    n = struct.unpack_from("<H", buf, dirOff)[0]
    if not 1 <= n <= 400:
        raise SystemExit("REJECT 目录 0x%X 条目数 %d 不合理" % (dirOff, n))
    hits = []
    for j in range(n):
        e = dirOff + 2 + 12 * j
        t, ty, cnt = struct.unpack_from("<HHI", buf, e)
        if t != tag:
            continue
        if ty not in TSZ:
            raise SystemExit("REJECT tag 0x%04X 类型 %d 不认识" % (tag, ty))
        if cnt * TSZ[ty] > 4:
            raise SystemExit("REJECT tag 0x%04X 非内联(type%d count%d) ⇒ 本夹具不改它" % (tag, ty, cnt))
        hits.append((e + 8, ty, cnt))
    if len(hits) != 1:
        raise SystemExit("REJECT tag 0x%04X 在目录 0x%X 里命中 %d 条(需要恰好 1)" % (tag, dirOff, len(hits)))
    return hits[0]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src"); ap.add_argument("dst"); ap.add_argument("tag")
    a = ap.parse_args()
    tag = int(a.tag, 16)

    d = idc_json(a.src)
    offs = d["versionOffsets"]
    if len(offs) < 2:
        raise SystemExit("REJECT 只有 %d 个版本目录 ⇒ 没有「初始 vs 生效」可抹" % len(offs))
    base, chosen = offs[0], d["chosenOffset"]
    if base == chosen:
        raise SystemExit("REJECT 生效版本就是初始版本 ⇒ 该件本就没有改动")

    buf = bytearray(open(a.src, "rb").read())
    aRef, tyR, cntR = find_entry(buf, base, tag)
    aHit, tyH, cntH = find_entry(buf, chosen, tag)
    if (tyR, cntR) != (tyH, cntH):
        raise SystemExit("REJECT 两处的 type/count 形态不同")
    before = struct.unpack_from("<I", buf, aHit)[0]
    want = struct.unpack_from("<I", buf, aRef)[0]
    struct.pack_into("<I", buf, aHit, want)
    open(a.dst, "wb").write(bytes(buf))
    print("OK 0x%04X 生效版本 %d -> %d (目录 0x%X, 初始版目录 0x%X)" % (tag, before, want, chosen, base))
    return 0


if __name__ == "__main__":
    sys.exit(main())
