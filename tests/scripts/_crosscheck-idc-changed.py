"""`dngtool -idc` 的 changed 面与 exiftool 的独立对账（第 77 条 ⑨⑩⑪ 的读手）。

为什么需要第二个读者: changed 是"源到底被改了什么"的读数, 如果只由 dngtool 自己说,
它写 14 还是写 0 都没人能否证。这里用 exiftool 把 SonyIDC 的**两个版本目录**各自读一遍,
按 tag 求差异, 再与我方 `-idc` 的 changed 逐条比集合与数值。

三条实测到的口径, 都写死在这里:
  · 名字表的 hex 位数是 **3~4 位** —— `0x201` 这种三位数若按四位正则匹配会被整条漏掉
    (第一版对账少一条就是这个成因);
  · 表里没有名字的条目 exiftool 印成 `SonyIDC_0x901c` ⇒ 要按这个形态反解 tag;
  · `0x0201/0x0202`(TIFF 的 JPEGInterchangeFormat/Length) 在两版里存的**都是 0**,
    但 exiftool 按 `IsOffset` 把 0 解析成"各自目录的基址" ⇒ 它报出一条**伪差异**
    (49578034 vs 49578824, 恰好是两个版本目录自己的地址) ⇒ 必须排除并点名。

用法: py _crosscheck-idc-changed.py <源.arw> [--expect-absent 0x8016]
末行输出 `REF=<n> MINE=<n>` 供门禁解析。
"""
import argparse
import json
import re
import subprocess
import sys

EXIF = r"C:\PLAN\ffmpegPictureUI\publish\PLAN\exiftool\exiftool.exe"
DNGTOOL = r"C:\PLAN\ffmpegPictureUI\publish\PLAN\artifacts\dngtool.exe"
PM = r"C:\PLAN\ffmpegPictureUI\publish\PLAN\exiftool\exiftool_files\lib\Image\ExifTool\SonyIDC.pm"
OFFSET_TAGS = {0x0201, 0x0202}


def name_to_tag():
    """整条表项用花括号配对取, 不能只匹配"`=> {` 紧跟 Name"——
    像 `{ Count => 4, Name => ... }` 这种首键不是 Name 的项会被静默漏掉(实测少 9 条)。"""
    src = open(PM, encoding="utf-8", errors="replace").read()
    out = {}
    for m in re.finditer(r"(0x[0-9a-fA-F]{3,4})\s*=>\s*\{", src):
        depth = 0
        e = m.end() - 1
        blk = ""
        for i in range(e, min(len(src), e + 4000)):
            if src[i] == "{":
                depth += 1
            elif src[i] == "}":
                depth -= 1
                if depth == 0:
                    blk = src[e:i + 1]
                    break
        n = re.search(r"Name\s*=>\s*'([^']+)'", blk)
        if n:
            out[n.group(1)] = int(m.group(1), 16)
    return out


def exiftool_versions(path):
    nm = name_to_tag()
    txt = subprocess.run([EXIF, "-a", "-u", "-G1", "-s", "-n", path],
                         capture_output=True, text=True, errors="replace").stdout
    v = [{}, {}]
    for L in txt.splitlines():
        m = re.match(r"\[Version([01])\]\s+(\S.*?)\s*:\s*(.*)$", L)
        if not m:
            continue
        gi, name, val = int(m.group(1)), m.group(2).strip(), m.group(3).strip()
        t = nm.get(name)
        if t is None:
            u = re.match(r"SonyIDC_0x([0-9a-fA-F]{3,4})$", name)
            if u:
                t = int(u.group(1), 16)
        if t is None:
            continue
        v[gi][t] = val
    return v


def mine_face(c, key):
    x = c[key]
    if isinstance(x, str):
        return [x]
    if c.get("type") in (5, 10):
        return ["%g" % (x[i] / x[i + 1]) if x[i + 1] else "NA" for i in range(0, len(x), 2)]
    return [str(t) for t in x]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("raw")
    ap.add_argument("--expect-absent", default=None, help="这个 tag 不该出现在 changed 里")
    a = ap.parse_args()

    p = subprocess.run([DNGTOOL, "-idc", a.raw], capture_output=True, text=True, errors="replace")
    if p.returncode != 0:
        print("FAIL -idc 读不动: %s" % (p.stdout + p.stderr).strip()[:160])
        print("REF=-1 MINE=-1")
        return 1
    d = json.loads(p.stdout)
    mine = {int(c["tag"], 16): c for c in d["changed"]}
    v = exiftool_versions(a.raw)
    both = set(v[0]) & set(v[1])
    raw_diff = {t for t in both if v[0][t] != v[1][t]}
    art = raw_diff & OFFSET_TAGS
    ref = raw_diff - OFFSET_TAGS

    bad = []
    if ref != set(mine):
        bad.append("集合不等: 仅 exiftool=%s 仅我方=%s" % (
            sorted("%04X" % x for x in ref - set(mine)),
            sorted("%04X" % x for x in set(mine) - ref)))
    for t in sorted(ref & set(mine)):
        c = mine[t]
        is_txt = c.get("type") == 2
        ea = [v[0][t]] if is_txt else v[0][t].split()
        eb = [v[1][t]] if is_txt else v[1][t].split()
        if ea != mine_face(c, "from") or eb != mine_face(c, "to"):
            bad.append("0x%04X 数值不等: exiftool %s->%s 我方 %s->%s" % (
                t, v[0][t], v[1][t], mine_face(c, "from"), mine_face(c, "to")))
    if len(v[0]) < 50 or len(v[1]) < 50:
        bad.append("exiftool 两版可映射条目数 %d/%d < 50 ⇒ 对账面不够宽" % (len(v[0]), len(v[1])))
    if d["changedCount"] != len(mine):
        bad.append("changedCount=%d 与 changed 数组长度 %d 自相矛盾" % (d["changedCount"], len(mine)))
    if a.expect_absent:
        ea = int(a.expect_absent, 16)
        if ea in mine:
            bad.append("要求 0x%04X 不在 changed 里, 但它还在" % ea)
        if ea in ref:
            bad.append("0x%04X 在 exiftool 侧仍判为差异 ⇒ 夹具没抹平(别急着怪判据)")

    print("两版条目 %d/%d; exiftool 差异 %d 条(伪差异 %d 条=%s, 成因: IsOffset 把存 0 解析成各自目录基址); 去伪后 %d 条; 我方 %d 条"
          % (len(v[0]), len(v[1]), len(raw_diff), len(art),
             ",".join("%04X" % x for x in sorted(art)) or "无", len(ref), len(mine)))
    for x in bad:
        print("FAIL " + x)
    print("REF=%d MINE=%d" % (len(ref), len(mine)))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
