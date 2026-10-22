#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_probe-sony-enciphered-lens.py —— 独立取证脚本（只读；除本文件与它自己的输出目录外不碰仓库）

要回答的问题
  索尼旧制式（Hasselblad Lunar = 索尼机身）的私有镜头校正三表（暗角/横向色散/畸变）到底能不能
  在本机独立取出来、并且与本仓 exiftool 逐值对上；以及 darktable 那条校验规则在旧/新制式上实测量是多少。

自实现的两个密码（都不抄字面量表）
  A) 替换密码：权威算法在 Sony.pm:11517 的注释 `c = (b*b*b) % 249`，取值 249-255 的字节不翻译。
     ⇒ 本脚本用公式自造 256 项正/反向码表，并**如实统计**单射性与「落在碰撞类上的字节数」。
  B) key-stream：Sony.pm:11488 `sub Decrypt($$$$)`（SR2SubIFD，密钥 = Sony 注记 0x7221）。
     旧制式的**计数前缀版**三表住在 SR2SubIFD 的 0x797d/0x7980/0x7982 ⇒ 顺带取证。

自主解析的链路（每步失败都点名，不抛异常）
  TIFF 头(小端) -> IFD0 -> 0x014a 图像 SubIFD -> 0x8769 ExifIFD -> 0x927C MakerNote
  -> 注记 IFD（块（偏移,长度）来自表项自身的 type/count：Sony.pm 里 0x94xx 没有自带长度字段）
  -> 0x2010 / 0x9405 / 0x9416 / 0xc634(SR2Private) 各块整块读出 -> 解密 -> 按 int16s[16]/[32]/[16] 小端取值
  子表分派不猜：0x9405 的**原始（未译）首字节**命中 Sony.pm:2030 的 a 档 /^[\x1b\x40\x7d]/
  或 :2044 的 b 档 /^[\x3a\xb3\x7e\x9a\x25\xe1\x76\x8b]/；b 档内 vig/ca 的偏移再按机型
  匹配 Sony.pm:9145-9193 的 Condition 正则。

对撞
  1) 取值臂：`exiftool -a -u -G1 -n`（LC_ALL/LANG=C，只解析 stdout）的十进制列表逐项比。
  2) 地址臂：`exiftool -a -u -n -v3` 里 exiftool 自己打出的「标签 + 绝对地址 + 原始字节」，
     与我算出的（绝对地址, 标签, 字节）比 —— 这一臂同时否证错误的指针口径。

用法
  py tests/scripts/_probe-sony-enciphered-lens.py [--out DIR] <arw> [<arw> ...]
  py tests/scripts/_probe-sony-enciphered-lens.py --default
退出码
  0 = 主臂（替换密码注记表 + 图像 SubIFD 明文位）与 exiftool 逐值相等
  1 = 主臂至少一族不等（两家真的对不上）
  2 = 跑不动（文件缺 / exiftool 不可用 / 块指针取不到）
"""

import os
import re
import struct
import subprocess
import sys
from collections import OrderedDict, Counter

MOD = 249                                     # 249 = 3 * 83


# ===========================================================================
# 1. 替换密码：由公式自造码表
# ===========================================================================

def build_tables():
    enc = [0] * 256
    preimages = {b: [] for b in range(256)}
    for b in range(256):
        # 注释：bytes with values 249-255 are not translated
        c = b if b >= MOD else (b * b * b) % MOD
        enc[b] = c
        preimages[c].append(b)
    dec = [preimages[c][:] for c in range(256)]
    fixed = [b for b in range(256) if enc[b] == b]
    collide = sorted(c for c, p in preimages.items() if len(p) > 1)
    return enc, dec, preimages, fixed, collide


ENC, DEC_LIST, PREIMAGES, FIXED_POINTS, COLLISION_IMAGES = build_tables()
COLLISION_SET = set(COLLISION_IMAGES)
FIXED_SET = set(FIXED_POINTS)


def decipher_bytes(data):
    """按反向表逐字节解密，同时统计「落在碰撞类 / 不动点 / 不翻译区」的字节个数。"""
    out = bytearray(len(data))
    st = Counter()
    for i, b in enumerate(data):
        if b in COLLISION_SET:
            st["on_collision_class"] += 1
            out[i] = DEC_LIST[b][0]        # 非单射时的取值约定：最小原像（本轮实测不会走到）
        elif b >= MOD:
            st["untranslated_249_255"] += 1
            out[i] = b
        elif b in FIXED_SET:
            st["fixed_point"] += 1
            out[i] = b
        else:
            out[i] = DEC_LIST[b][0]
            st["moved"] += 1
    st["length"] = len(data)
    return bytes(out), st


def encrypt_bytes(data):
    return bytes(ENC[b] for b in data)


# ===========================================================================
# 2. SR2SubIFD 的 key-stream（按 Sony.pm:11488 sub Decrypt 的算法自写）
# ===========================================================================

M32 = 0xFFFFFFFF


def sr2_decrypt(buf, key):
    nwords = len(buf) // 4                      # len 非 4 的倍数时尾字节不参与
    pad = [0] * 128
    k = key & M32
    for i in range(4):
        lo = (k & 0xFFFF) * 0x0EDD + 1
        hi = (k >> 16) * 0x0EDD + (k & 0xFFFF) * 0x02E9 + (lo >> 16)
        k = (((hi & 0xFFFF) << 16) + (lo & 0xFFFF)) & M32
        pad[i] = k
    pad[3] = ((pad[3] << 1) | ((pad[0] ^ pad[2]) >> 31)) & M32
    for i in range(4, 0x7F):
        pad[i] = (((pad[i - 4] ^ pad[i - 2]) << 1) |
                  ((pad[i - 3] ^ pad[i - 1]) >> 31)) & M32
    words = list(struct.unpack(">%dI" % nwords, buf[:nwords * 4])) if nwords else []
    i = 0x7F
    for j in range(nwords):
        val = pad[(i + 1) & 0x7F] ^ pad[(i + 65) & 0x7F]
        pad[i & 0x7F] = val
        words[j] = (words[j] ^ val) & M32
        i += 1
    head = struct.pack(">%dI" % nwords, *words) if nwords else b""
    return head + buf[nwords * 4:]


# ===========================================================================
# 3. 极简 TIFF/EXIF 解析（只读，小端）
# ===========================================================================

TYPE_SIZES = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8,
              11: 2, 12: 4, 13: 4, 14: 8, 15: 4, 16: 8}


class Tiff:
    def __init__(self, blob, log):
        self.b = blob
        self.log = log
        self.ok = False
        if len(blob) < 8:
            log("tiff", "文件不足 8 字节 -> 跑不动")
            return
        if blob[:2] != b"II":
            log("tiff", "头字节=%r 不是 'II'（本探针只处理小端 ARW）-> 跑不动" % (blob[:2],))
            return
        magic, ifd0 = struct.unpack_from("<HI", blob, 2)
        if magic != 42:
            log("tiff", "magic=%d 不是 42 -> 跑不动" % magic)
            return
        self.ifd0 = ifd0
        self.ok = True

    def u16(self, p):
        if p < 0 or p + 2 > len(self.b):
            raise IndexError(p)
        return struct.unpack_from("<H", self.b, p)[0]

    def slice(self, p, n):
        if p < 0 or n < 0 or p + n > len(self.b):
            raise IndexError(p)
        return self.b[p:p + n]

    def ifd(self, pos, base, label, maxn=4096):
        """解析一个 LE IFD：返回 OrderedDict tag -> dict。
        表项里 size>4 的值字段按「存储值 + base」当绝对地址解。"""
        out = OrderedDict()
        try:
            n = self.u16(pos)
        except IndexError:
            log_ = "IFD 起点 %d 越界" % pos
            self.log(label, log_ + " -> 取不到条目数")
            return out
        if n == 0 or n > maxn:
            self.log(label, "条目数=%d 不合理（0 或 >%d）-> 不解析" % (n, maxn))
            return out
        self.log(label, "IFD @%d(base=%d) 条目数=%d" % (pos, base, n))
        for i in range(n):
            e = pos + 2 + 12 * i
            try:
                tag = self.u16(e)
                ty = struct.unpack_from("<H", self.b, e + 2)[0]
                cnt = struct.unpack_from("<I", self.b, e + 4)[0]
                vf = struct.unpack_from("<I", self.b, e + 8)[0]
            except (IndexError, struct.error):
                self.log(label, "记录 %d 读越界 @%d -> 停在这" % (i, e))
                break
            ts = TYPE_SIZES.get(ty)
            if ts is None:
                self.log(label, "tag 0x%04x 类型 %d 未知 -> 跳" % (tag, ty))
                continue
            size = ts * cnt
            rec = dict(tag=tag, type=ty, count=cnt, size=size, vfield=vf,
                       entry=e, ptr=None, payload=None)
            if size <= 4:
                rec["ptr"] = e + 8
                try:
                    rec["payload"] = self.slice(e + 8, size)
                except IndexError:
                    self.log(label, "tag 0x%04x 内联载荷越界 -> 空" % tag)
            else:
                p = vf + base
                try:
                    rec["payload"] = self.slice(p, size)
                    rec["ptr"] = p
                except IndexError:
                    self.log(label, "tag 0x%04x 值指针存储值=%d +base=%d -> %d+%d 越界（%d）"
                             % (tag, vf, base, p, size, len(self.b)))
            out[tag] = rec
        return out


def pick_note_base(mn_abs, mn_len, ent, log):
    """注记块（0x94xx/0x9050/0x2010）的值指针基址有两种可能：
    0 = 相对 TIFF 头；mn_abs = 相对注记起点。用「块必须整体落在注记范围内」筛，
    平票时优先 0；两者都筛不掉就点名。"""
    cand = OrderedDict([("TIFFbase(0)", 0), ("Notebase(%d)" % mn_abs, mn_abs)])
    score = OrderedDict()
    blocks = [t for t in ent if t in (0x2010, 0x9050, 0x9400, 0x9401, 0x9402, 0x9403,
                                      0x9404, 0x9405, 0x9406, 0x9407, 0x940f, 0x9416)
              and ent[t]["size"] > 4]
    for name, b in cand.items():
        ok = 0
        detail = []
        for t in blocks:
            p = ent[t]["vfield"] + b
            inside = mn_abs <= p and p + ent[t]["size"] <= mn_abs + mn_len
            detail.append("0x%04x->%d%s" % (t, p, "" if inside else "(出界)"))
            ok += 1 if inside else 0
        score[name] = (ok, detail)
    log("note.base", "候选块数=%d，各基址命中数=%s"
        % (len(blocks), {k: v[0] for k, v in score.items()}))
    log("note.base", "TIFFbase(0) 明细=%s" % " ".join(score["TIFFbase(0)"][1][:12]))
    log("note.base", "Notebase 明细=%s" % " ".join(score["Notebase(%d)" % mn_abs][1][:12]))
    best = max(score.items(), key=lambda kv: kv[1][0])
    if best[1][0] == 0:
        return None, "两种基址都没法把任何一个块安置在注记范围内"
    return cand[best[0]], best[0]


# ===========================================================================
# 4. Tag9405a / 9405b / 其它子表的偏移（全部照 Sony.pm 的条目定义，不猜）
# ===========================================================================

TAG9405A_FIRST = (0x1b, 0x40, 0x7d)                      # Sony.pm:2030
TAG9405B_FIRST = (0x3a, 0xb3, 0x7e, 0x9a, 0x25, 0xe1, 0x76, 0x8b)   # Sony.pm:2044

TAG9405A = OrderedDict([                                  # Sony.pm:8941-8955
    ("vig", (0x064a, 16)),
    ("ca", (0x066a, 32)),
    ("dist", (0x06ca, 16)),
])

# Tag9405b：dist 固定 0x0064（Sony.pm:9127 Condition = Model 不以 DSC- 开头）；
# vig/ca 按机型分档。下列正则**逐字照抄 Sony.pm:9145-9193 的 Condition**（含 ^ 与 \b），
# 顺序也按 Sony.pm 里 0x034a/0x0350/0x035c/0x0368 与 0x037c/0x0384/0x039c/0x03b0/0x03b8 的档位配对。
TAG9405B = [
    # (偏移, 项数, 族, 正则（逐字照抄 Sony.pm 的 Condition），neg, 出处)
    (0x0064, 16, "dist", r"^DSC-", True, "Sony.pm:9125-9129 Condition !~ /^DSC-/"),
    (0x034a, 16, "vig", r"^(ILCA-(68|77M2)|ILCE-(5000|5100|6000|7|7R|7S|QX1)|Lusso)\b",
     False, "Sony.pm:9145-9149"),
    (0x0350, 16, "vig", r"^(ILCE-7M2)", False, "Sony.pm:9151-9155"),
    (0x035c, 16, "vig", r"^(ILCA-99M2|ILCE-(6100|6400|6600|7C|7M3|7RM3A?|7RM4A?|9|9M2)|ZV-E10)",
     False, "Sony.pm:9156-9160"),
    (0x0368, 16, "vig", r"^(ILCE-(6300|7RM2|7SM2))", False, "Sony.pm:9163-9167"),
    (0x037c, 32, "ca", r"^(ILCA-(68|77M2)|ILCE-(5000|5100|6000|7|7R|7S|QX1)|Lusso)\b",
     False, "Sony.pm:9173-9177"),
    (0x0384, 32, "ca", r"^(ILCE-7M2)", False, "Sony.pm:9178-9182"),
    (0x039c, 32, "ca", r"^(ILCE-(6300|7RM2|7SM2))", False, "Sony.pm:9183-9187"),
    (0x03b0, 32, "ca", r"^(ILCA-99M2|ILCE-6500)", False, "Sony.pm:9188-9192"),
    (0x03b8, 32, "ca", r"^(ILCE-(6100|6400|6600|7C|7M3|7RM3A?|7RM4A?|9|9M2)|ZV-E10)",
     False, "Sony.pm:9193-9197"),
]


def tag9405b_offsets(model):
    """按 Sony.pm 的 Condition 逐条独立判档（vig/ca/dist 各自的正则不同，不能凑对）。
    返回 (OrderedDict fam->(off,cnt), 说明列表) ；某族没有一档命中就不给偏移。"""
    picked = OrderedDict()
    why = []
    for off, cnt, fam, cond, neg, src in TAG9405B:
        hit = bool(re.search(cond, model or ""))
        if neg:
            hit = not hit
        if hit and fam not in picked:
            picked[fam] = (off, cnt)
            why.append("%s@0x%04x：机型 %r %s %s（%s）"
                       % (fam, off, model, "不匹配" if neg else "匹配", cond, src))
    offs = OrderedDict()
    for fam in ("vig", "ca", "dist"):
        if fam in picked:
            offs[fam] = picked[fam]
    return (offs if offs else None), why


# 图像 SubIFD 里认的标签（Exiv2 tags_int.cpp:814-819 的新制式 + 旧制式的 0x797d 系）
IMAGESUB_TAGS = {0x7032: "vig", 0x7035: "ca", 0x7037: "dist",
                 0x797d: "vig", 0x7980: "ca", 0x7982: "dist"}
# SR2SubIFD 里认的标签（Sony.pm:10571-10573）
SR2SUB_TAGS = {0x7032: "vig", 0x7035: "ca", 0x7037: "dist",
               0x797d: "vig", 0x7980: "ca", 0x7982: "dist"}


# ===========================================================================
# 5. 读数、形状、规则
# ===========================================================================

def i16le(buf, off, count):
    need = off + 2 * count
    if off < 0 or need > len(buf):
        return None, None, ("块内偏移 0x%x 起要 %d 字节，块长只有 %d -> 长度不够/偏移越界"
                            % (off, need, len(buf)))
    return list(struct.unpack_from("<%dh" % count, buf, off)), buf[off:need], None


def mono(xs):
    if len(xs) < 2:
        return None
    return all(xs[i] < xs[i + 1] for i in range(len(xs) - 1))


def shape(vals):
    if vals is None:
        return "无读数"
    nz = [v for v in vals if v != 0]
    signs = ["+" if v > 0 else ("-" if v < 0 else "0") for v in vals]
    runs = []
    for s in signs:
        if not runs or runs[-1][0] != s:
            runs.append([s, 1])
        else:
            runs[-1][1] += 1
    return ("n=%d nonzero=%d 非零下标=%s..%s 首=%s 符号游程=%s 非零段单调增=%s "
            "去掉第 0 项后非零段单调增=%s 非零极值=%s"
            % (len(vals), len(nz),
               vals.index(nz[0]) if nz else "-",
               len(vals) - 1 - vals[::-1].index(nz[-1]) if nz else "-",
               vals[0] if vals else "-",
               ",".join("%s:%d" % (s, c) for s, c in runs),
               mono(nz), mono([v for v in vals[1:] if v != 0]),
               ("%d..%d" % (min(nz), max(nz))) if nz else "-"))



DT_RULE_NAME = "darktable exif.cc:1396-1399  nc=dist[0]; nc<=16; 2*nc==ca[0]; nc==vig[0]"


def dt_rule(fams):
    if not all(fams.get(k) for k in ("vig", "ca", "dist")):
        return "未判（这一口径的三族不齐：%s）" % sorted(fams)
    vig, ca, dist = fams["vig"], fams["ca"], fams["dist"]
    nc = dist[0]
    c1 = nc <= 16
    c2 = (2 * nc == ca[0])
    c3 = (nc == vig[0])
    return ("nc=dist[0]=%d  [nc<=16]%s  [2*nc==ca[0]]%s（2*nc=%d vs ca[0]=%d）  "
            "[nc==vig[0]]%s（nc=%d vs vig[0]=%d）  => %s  数组长度 vig/ca/dist=%d/%d/%d"
            % (nc, c1, c2, 2 * nc, ca[0], c3, nc, vig[0],
               "合" if (c1 and c2 and c3) else "不合", len(vig), len(ca), len(dist)))


# ===========================================================================
# 6. exiftool
# ===========================================================================

def find_exiftool(root):
    p = os.path.join(root, "publish", "PLAN", "exiftool", "exiftool.exe")
    return p if os.path.isfile(p) else None


def run_exiftool(exe, path, extra):
    env = dict(os.environ)
    env["LC_ALL"] = "C"          # 不设的话 perl 往 stderr 喷 locale warning
    env["LANG"] = "C"
    try:
        pr = subprocess.run([exe, "-a", "-u", "-n"] + extra + [path], env=env,
                            capture_output=True, timeout=900)
    except Exception as e:                                      # noqa: BLE001
        return None, "调用失败: %r" % (e,)
    return pr.stdout.decode("latin-1"), pr.stderr.decode("latin-1")


NUMRE = re.compile(r"^-?\d+$")
OUTRE = re.compile(r"^\s*\[([^\]]+)\]\s+(.+?)\s*:\s*(.*)$")


def parse_exiftool_values(text):
    """'[Group]   Some Name : 1 2 3' -> {(group, NoSpaceName): [[ints], ...]}（只吃 stdout）"""
    got = OrderedDict()
    for line in text.splitlines():
        m = OUTRE.match(line)
        if not m:
            continue
        grp, name, val = m.group(1), m.group(2).strip().replace(" ", ""), m.group(3).strip()
        toks = val.split()
        got.setdefault((grp, name), [])
        if toks and all(NUMRE.match(t) for t in toks):
            got[(grp, name)].append([int(t) for t in toks])
    return got


def first_diff(a, b):
    if a == b:
        return None
    for i in range(max(len(a), len(b))):
        x = a[i] if i < len(a) else None
        y = b[i] if i < len(b) else None
        if x != y:
            return ("首个差异 index=%d 我方=%s exiftool=%s（我方 %d 项，exiftool %d 项）"
                    % (i, x, y, len(a), len(b)))
    return None


DIRRE = re.compile(r"\+\s*\[(.+?)\s+directory")
TAGRE = re.compile(r"-\s*Tag\s+0x([0-9a-fA-F]{1,4})\s+\((\d+)\s+bytes")
HEXRE = re.compile(r"([0-9a-fA-F]{4,8}):\s+((?:[0-9a-fA-F]{2}\s+){1,16})(?:\[|$)")


def parse_tag_dumps(text):
    """从 exiftool -v3 的输出抓「绝对地址 -> exiftool 自己打出的（标签, 段名, 字节）」。
    实测本仓 exiftool 的 verbose 走 **stdout**（stderr 0 行），所以这臂同样只解析 stdout。
    大标签会被 [snip] 截断，故 printed 可能短于 size；比对只覆盖 printed 的部分并如实报数。"""
    by_addr = {}
    section = "?"
    cur = None
    for line in text.splitlines():
        m = DIRRE.search(line)
        if m:
            section = m.group(1).strip()
            cur = None
            continue
        m = TAGRE.search(line)
        if m:
            cur = dict(section=section, tag=int(m.group(1), 16), size=int(m.group(2)),
                       bytes=bytearray(), addr=None)
            continue
        m = HEXRE.search(line)
        if m and cur is not None:
            bs = [int(x, 16) for x in m.group(2).split()]
            if len(bs) > cur["size"]:
                bs = bs[:cur["size"]]
            if cur["addr"] is None:
                cur["addr"] = int(m.group(1), 16)
                by_addr.setdefault(cur["addr"], []).append(cur)
            cur["bytes"].extend(bs)
            if len(cur["bytes"]) >= cur["size"]:
                cur = None
        elif cur is not None and cur["bytes"]:
            cur = None
    for lst in by_addr.values():
        for h in lst:
            h["bytes"] = bytes(h["bytes"])
    return by_addr


# ===========================================================================
# 7. 单件取证
# ===========================================================================

PRIMARY = ("Tag9405a", "Tag9405b", "Tag2010b", "Tag9416", "ImageSubIFD")


def probe(path, exe, outdir, L):
    def log(sec, msg):
        L.append("%-34s | %s" % (sec, msg))

    L.append("")
    L.append("############################ FILE %s" % path)
    if not os.path.isfile(path):
        log("file", "不存在 -> 跑不动")
        return "文件不存在", 2
    log("file", "size=%d" % os.path.getsize(path))
    with open(path, "rb") as f:
        blob = f.read()

    tf = Tiff(blob, log)
    if not tf.ok:
        return "非小端 TIFF", 2

    arrays = OrderedDict()       # "容器|标签|族" -> [int]
    meta = {}                   # 同上 -> dict(addr, tag, nbytes, raw, how)
    pre_arrays = {}             # 同上 -> 「不解密、直接在密文上取」的读数（旁证用）
    stats = OrderedDict()

    def take(cont, tagid, fam, buf, off, cnt, addr, how):
        vals, raw, err = i16le(buf, off, cnt)
        key = "%s|%04x|%s" % (cont, tagid, fam)
        if err:
            log(key, "取不到：%s" % err)
            return
        arrays[key] = vals
        meta[key] = dict(addr=addr, tag=tagid, nbytes=2 * cnt, raw=raw, how=how)
        log(key, "载荷绝对地址=%s(0x%x) 取 %d 项 = %s"
            % (addr, addr if addr else 0, cnt, " ".join(str(v) for v in vals)))

    # ---------------- IFD0 -----------------------------------------------
    ifd0 = tf.ifd(tf.ifd0, 0, "IFD0")
    model = make = ""
    for tag, r in ifd0.items():
        if r["tag"] in (0x0110, 0x010F) and r["payload"]:
            s = r["payload"].split(b"\0")[0].decode("latin-1").strip()
            if tag == 0x0110:
                model = s
            else:
                make = s
    log("ifd0", "Make=%r Model=%r 条目=%d 标签=%s"
        % (make, model, len(ifd0), ",".join("0x%04x" % t for t in sorted(ifd0))))

    # ---------------- 图像 SubIFD（IFD0 0x014a）：明文位 ------------------
    rec = ifd0.get(0x014A)
    subdirs = []
    if rec is None:
        log("ImageSubIFD", "IFD0 没有 0x014a -> 这一路取不到")
    else:
        try:
            if rec["type"] == 4:
                offs = list(struct.unpack_from("<%dI" % rec["count"], rec["payload"], 0))
            elif rec["type"] == 3:
                offs = [int(x) for x in struct.unpack_from("<%dH" % rec["count"], rec["payload"], 0)]
            else:
                offs = []
                log("ImageSubIFD", "0x014a 类型=%d 不是 SHORT/LONG 数组 -> 不解" % rec["type"])
        except struct.error as e:
            offs = []
            log("ImageSubIFD", "0x014a 载荷解不出来：%r" % (e,))
        log("ImageSubIFD", "0x014a count=%d SubIFD 偏移表=%s" % (rec["count"], offs))
        for o in offs:
            subdirs.append((o, tf.ifd(o, 0, "ImageSubIFD@%d" % o)))
    for o, d in subdirs:
        have = [t for t in IMAGESUB_TAGS if t in d]
        log("ImageSubIFD@%d" % o, "条目=%d 其中镜头表标签=%s"
            % (len(d), ["0x%04x" % t for t in sorted(have)] or "无（0x7032/35/37/797d/80/82 都不在）"))
        for tag in sorted(have):
            e = d[tag]
            if e["type"] != 8:
                log("ImageSubIFD@%d|%04x" % (o, tag),
                    "类型=%d 不是 int16s(8)，仍按 int16s 取但如实点名" % e["type"])
            fam = IMAGESUB_TAGS[tag]
            take("ImageSubIFD@%d" % o, tag, fam, e["payload"], 0, e["count"],
                 e["ptr"], "IFD0 0x014a -> SubIFD@%d 的表项 0x%04x，type=%d count=%d"
                            % (o, tag, e["type"], e["count"]))

    # ---------------- ExifIFD -> MakerNote -------------------------------
    exif = OrderedDict()
    e69 = ifd0.get(0x8769)
    if e69 is None or not e69["payload"]:
        log("ExifIFD", "IFD0 没有 0x8769（或载荷空）-> 注记这条路取不到，跑不动")
    else:
        ep = struct.unpack_from("<I", e69["payload"], 0)[0]
        exif = tf.ifd(ep, 0, "ExifIFD")
    note = None
    nrec = exif.get(0x927C)
    if nrec is None:
        log("MakerNote", "ExifIFD 没有 0x927C -> 注记取不到，跑不动")
    elif nrec["size"] <= 4:
        log("MakerNote", "0x927C size=%d 是内联值，不是指针 -> 跑不动" % nrec["size"])
    else:
        mn_abs = nrec["vfield"]
        mn_len = nrec["count"]
        log("MakerNote", "0x927C type=%d count=%d -> 注记 @%d 长 %d"
            % (nrec["type"], nrec["count"], mn_abs, mn_len))
        try:
            nent = tf.u16(mn_abs)
        except IndexError:
            nent = None
            log("MakerNote", "注记起点 %d 越界 -> 跑不动" % mn_abs)
        if nent:
            log("MakerNote", "注记起点处的 16 位条目数=%d（该制式没有 'SOny' 头，IFD 直接从注记起点开始）"
                % nent)
            ent = tf.ifd(mn_abs, 0, "MNoteIFD")
            nb, why = pick_note_base(mn_abs, mn_len, ent, log)
            if nb is None:
                log("MakerNote", "基址判不出来：%s -> 跑不动" % why)
            else:
                note = (mn_abs, mn_len, ent, nb)
                log("MakerNote", "采用基址=%d（%s）" % (nb, why))

    # ---------------- 注记内的加密块 ------------------------------------
    if note:
        mn_abs, mn_len, ent, nb = note
        for tag in (0x2010, 0x9050, 0x9400, 0x9401, 0x9402, 0x9403, 0x9404,
                    0x9405, 0x9406, 0x940f, 0x9416):
            r = ent.get(tag)
            if r is None:
                log("note-0x%04x" % tag, "注记 IFD 没有这个表项 -> 跳过（不是判它不存在于本制式）")
                continue
            if r["size"] <= 4:
                log("note-0x%04x" % tag, "表项 size=%d 不是块 -> 跳过" % r["size"])
                continue
            off = r["vfield"] + nb
            try:
                rawblk = tf.slice(off, r["size"])
            except IndexError:
                log("note-0x%04x" % tag, "值指针存储值=%d +base=%d -> %d+%d 越界 -> 取不到"
                    % (r["vfield"], nb, off, r["size"]))
                continue
            if not (mn_abs <= off and off + r["size"] <= mn_abs + mn_len):
                log("note-0x%04x" % tag, "块 [%d,%d) 不在注记 [%d,%d) 内 -> 基址判错，跳过"
                    % (off, off + r["size"], mn_abs, mn_abs + mn_len))
                continue
            clear, st = decipher_bytes(rawblk)
            st["roundtrip_ok"] = encrypt_bytes(clear) == rawblk
            stats["note-0x%04x" % tag] = st
            log("note-0x%04x" % tag, "size=%d（长度来自 IFD 表项的 count=%d，Sony.pm 的 0x94xx "
                "条目里没有自带长度字段）@%d base=%d 原始首字节=0x%02x 解密后首字节=0x%02x "
                "再加密回去=%s" % (r["size"], r["count"], off, nb, rawblk[0], clear[0],
                                     st["roundtrip_ok"]))
            log("note-0x%04x.stat" % tag,
                "块长=%d 其中 落在碰撞类的字节=%d 不动点字节(0/1/82-84/165-167/248)=%d "
                "不翻译区(249-255)=%d 被真正置换的=%d"
                % (st["length"], st["on_collision_class"], st["fixed_point"],
                   st["untranslated_249_255"], st["moved"]))
            if tag == 0x9405:
                if rawblk[0] in TAG9405A_FIRST:
                    cont, offs = "Tag9405a", TAG9405A
                    log("note-0x9405", "子表=Tag9405a，依据=原始首字节 0x%02x 命中 Sony.pm:2030 的 "
                        "a 档正则（字节集 1b/40/7d）；解密后首字节=%d"
                        "（对得上 Sony.pm:8884 注释『3 0 (0x1b = 27 0 enc.) … 也含 Lunar』）"
                        % (rawblk[0], clear[0]))
                elif rawblk[0] in TAG9405B_FIRST:
                    offs, whyb = tag9405b_offsets(model)
                    cont = "Tag9405b"
                    log("note-0x9405", "子表=Tag9405b，依据=原始首字节 0x%02x 命中 Sony.pm:2044 的 "
                        "b 档字节集（3a/b3/7e/9a/25/e1/76/8b）；解密后首字节=%d"
                        % (rawblk[0], clear[0]))
                    for w in whyb:
                        log("note-0x9405档", w)
                    if offs is None:
                        log("note-0x9405", "机型 %r 在 Sony.pm:9125-9197 里一族都没命中 -> 偏移取不到"
                            % model)
                        continue
                    missing = [f for f in ("vig", "ca", "dist") if f not in offs]
                    if missing:
                        log("note-0x9405", "机型 %r 这几族在 Sony.pm 里没有对应档位：%s -> 不硬编偏移"
                            % (model, missing))
                else:
                    log("note-0x9405", "原始首字节 0x%02x 既不在 a 档也不在 b 档 -> 子表判不出，跳过"
                        % rawblk[0])
                    continue
                for fam, (o, c) in offs.items():
                    key = "%s|%04x|%s" % (cont, o, fam)
                    vals, raw, err = i16le(clear, o, c)
                    if err:
                        log(key, "取不到：%s" % err)
                        continue
                    arrays[key] = vals
                    meta[key] = dict(addr=off + o, tag=o, nbytes=2 * c, raw=raw,
                                     how="注记块 0x9405 @%d（%s 档）+ 块内偏移 0x%04x"
                                         % (off, cont, o))
                    log(key, "解密后取 %d 项 = %s" % (c, " ".join(str(v) for v in vals)))
                    pre, _, err2 = i16le(rawblk, o, c)
                    if err2:
                        log(key + ".未解密", "同一偏移在密文上取不到：%s" % err2)
                    else:
                        pre_arrays[key] = pre
                        log(key + ".未解密", "同一偏移在密文上直接取 %d 项 = %s"
                            % (c, " ".join(str(v) for v in pre)))
                if cont == "Tag9405a" and len(clear) > 0x060a:
                    log("note-0x9405.probe", "正向命中探针（先证明探针活着）：明文 0x0600 "
                        "DistortionCorrParamsPresent=%d 0x0603 LensFormat=%d 0x0604 LensMount=%d "
                        "0x0605 LensType2=%d 0x0608 LensType=%d"
                        % (clear[0x0600], clear[0x0603], clear[0x0604],
                           struct.unpack_from("<H", clear, 0x0605)[0],
                           struct.unpack_from("<H", clear, 0x0608)[0]))
                else:
                    log("note-0x9405.probe", "探针偏移 0x0608 越出块长 %d 或子表非 a 档 -> 未读"
                        % len(clear))
            elif tag == 0x2010:
                take("Tag2010b", 0x1a23, "dist", clear, 0x1a23, 16, off + 0x1a23,
                     "注记 0x2010 块 @%d + 0x1a23（Sony.pm:6563 Tag2010b DistortionCorrParams int16s[16]）"
                     % off)
            elif tag == 0x9416:
                take("Tag9416", 0x004f, "dist", clear, 0x004f, 16, off + 0x004f,
                     "注记 0x9416 块 @%d + 0x004f（Sony.pm:9979 Tag9416）" % off)

    # ---------------- SR2Private -> SR2SubIFD（第二条密码）---------------
    sp = ifd0.get(0xC634)
    if sp is None:
        log("SR2Private", "IFD0 没有 0xc634 -> 这一路取不到")
    else:
        try:
            sr2_abs = struct.unpack_from("<I", sp["payload"], 0)[0]
        except struct.error:
            sr2_abs = None
            log("SR2Private", "0xc634 载荷取不到（type=%d count=%d）" % (sp["type"], sp["count"]))
        if sr2_abs is not None:
            log("SR2Private", "0xc634（type=%d count=%d）-> SR2Private IFD @%d"
                % (sp["type"], sp["count"], sr2_abs))
            sr2 = tf.ifd(sr2_abs, 0, "SR2Private")
            p0, p1, p2 = sr2.get(0x7200), sr2.get(0x7201), sr2.get(0x7221)
            if not (p0 and p1 and p2):
                log("SR2SubIFD", "SR2Private 里缺指针键：0x7200=%s 0x7201=%s 0x7221=%s -> 取不到"
                    % (bool(p0), bool(p1), bool(p2)))
            else:
                sub_off = struct.unpack_from("<I", p0["payload"], 0)[0]
                sub_len = struct.unpack_from("<I", p1["payload"], 0)[0]
                sub_key = struct.unpack_from("<I", p2["payload"], 0)[0]
                log("SR2SubIFD", "0x7200=%d 0x7201=%d 0x7221=0x%08x=%d"
                    % (sub_off, sub_len, sub_key, sub_key))
                try:
                    encblk = tf.slice(sub_off, sub_len)
                except IndexError:
                    encblk = None
                    log("SR2SubIFD", "块 [%d,%d) 越界（文件 %d）-> 取不到"
                        % (sub_off, sub_off + sub_len, len(blob)))
                if encblk is not None:
                    st = Counter()
                    st["length"] = len(encblk)
                    st["words_decrypted"] = len(encblk) // 4
                    st["tail_untouched_bytes"] = len(encblk) % 4
                    stats["SR2SubIFD(0x7221 key)"] = st
                    dec = sr2_decrypt(encblk, sub_key)
                    log("SR2SubIFD.cipher", "key-stream：len=%d 不是 4 的倍数，解密了 %d 个 32 位字，"
                        "尾 %d 字节不参与（Sony.pm:11494 words=int(len/4)）"
                        % (st["length"], st["words_decrypted"], st["tail_untouched_bytes"]))
                    n = struct.unpack_from("<H", dec, 0)[0]
                    log("SR2SubIFD", "解密后头 16 位条目数=%d。块内表项的值指针有两种口径，"
                        "两种都算，交给 exiftool 判：" % n)
                    log("SR2SubIFD.base", "A) TIFF-absolute：存储值就是 TIFF 相对偏移（=文件绝对地址，"
                        "本文件 TIFF 头在 0）→ 块内下标 = 存储值 - %d" % sub_off)
                    log("SR2SubIFD.base", "B) block-relative：存储值是相对块起点 → 块内下标 = 存储值")
                    for nm, rel in (("TIFFabs", 0), ("blkrel", sub_off)):
                        dd = OrderedDict()
                        bad = 0
                        for i in range(n):
                            e = 2 + 12 * i
                            if e + 12 > len(dec):
                                log("SR2SubIFD.%s" % nm, "条目 %d 头越出块长 %d -> 截断" % (i, len(dec)))
                                break
                            tg = struct.unpack_from("<H", dec, e)[0]
                            ty = struct.unpack_from("<H", dec, e + 2)[0]
                            ct = struct.unpack_from("<I", dec, e + 4)[0]
                            vf = struct.unpack_from("<I", dec, e + 8)[0]
                            ts = TYPE_SIZES.get(ty)
                            if ts is None:
                                log("SR2SubIFD.%s" % nm, "条目 %d tag 0x%04x 类型 %d 未知 -> 跳"
                                    % (i, tg, ty))
                                continue
                            sz = ts * ct
                            if sz <= 4:
                                pay, ab = dec[e + 8:e + 8 + sz], None
                            else:
                                q = (vf + rel) - sub_off
                                if q < 0 or q + sz > len(dec):
                                    bad += 1
                                    continue
                                pay, ab = dec[q:q + sz], sub_off + q
                            dd[tg] = dict(type=ty, count=ct, size=sz, payload=pay,
                                          ptr=vf, addr=ab, idx=i)
                        have = [t for t in SR2SUB_TAGS if t in dd]
                        log("SR2SubIFD.%s" % nm, "条目=%d 越界指针=%d 镜头表标签=%s"
                            % (len(dd), bad, ["0x%04x" % t for t in sorted(have)] or "无"))
                        for tg in sorted(have):
                            e = dd[tg]
                            take("SR2SubIFD[%s]" % nm, tg, SR2SUB_TAGS[tg], e["payload"], 0,
                                 e["count"], e["addr"],
                                 "SR2SubIFD 口径 %s：条目 %d 的指针存储值=%d，块内下标=%s"
                                 % (nm, e["idx"], e["ptr"],
                                    None if e["addr"] is None else e["addr"] - sub_off))

    # ================= exiftool 对撞（取值臂：只解析 stdout）==============
    L.append("-- 取值臂：exiftool -a -u -G1 -n（只解析 stdout）--")
    g1, g1err = run_exiftool(exe, path, ["-G1"])
    dump_mid(outdir, path, "exiftool-G1", g1, g1err)
    if g1 is None:
        log("exiftool", g1err)
        return "exiftool 跑不动", 2
    log("exiftool.stderr", "本轮 stderr %d 字节（LC_ALL=C 后应为 0）：%s"
        % (len(g1err or ""), (g1err or "").replace("\n", " / ")[:120]))
    xt = parse_exiftool_values(g1)
    xtarr = {}
    for (grp, nm), lst in xt.items():
        for arr in lst:
            xtarr.setdefault(nm, []).append((grp, arr))
    NAME = {"vig": "VignettingCorrParams", "ca": "ChromaticAberrationCorrParams",
            "dist": "DistortionCorrParams"}
    byfam = {}
    for key, vals in arrays.items():
        byfam.setdefault(key.split("|")[-1], []).append((key, vals))

    matched, nomatch = set(), set()
    for fam in ("vig", "ca", "dist"):
        canon = NAME[fam]
        cand = xtarr.get(canon, [])
        log("xt.%s" % fam, "exiftool 打了 %d 条 %s，组=%s"
            % (len(cand), canon, sorted({g for g, _ in cand}) or "无"))
        if not cand:
            log("xt.%s" % fam, "exiftool 本轮一条 %s 都没有 -> 这一族只能点名『exiftool 没给读数』，"
                "不判成不存在" % canon)
        for key, vals in byfam.get(fam, []):
            exact = [g for g, arr in cand if arr == vals]
            if exact:
                log("xcheck.%s" % fam, "%s == [%s]%s 逐值相等（%d 项）"
                    % (key, ",".join(sorted(set(exact))), canon, len(vals)))
                matched.add(key)
                continue
            near = [(first_diff(vals, arr), g, arr) for g, arr in cand]
            near = [x for x in near if x[0]]
            if not near:
                log("xcheck.%s" % fam, "%s => 没得比（exiftool 该族无读数）-> 未判" % key)
                continue
            near.sort(key=lambda x: (len(x[2]) != len(vals),))
            log("xcheck.%s" % fam, "%s != 最接近的 [%s]%s：%s"
                % (key, near[0][1], canon, near[0][0]))
            nomatch.add(key)

    conts = OrderedDict()
    for key in arrays:
        conts.setdefault(key.split("|")[0], []).append(key)
    confirmed, refuted = OrderedDict(), OrderedDict()
    for cont, keys in conts.items():
        ok = all(k in matched for k in keys)
        bad = [k for k in keys if k in nomatch]
        confirmed[cont] = ok
        refuted[cont] = bool(bad)
        log("verdict.%s" % cont,
            "该口径 %d 族读数：%s ⇒ %s"
            % (len(keys), "全与 exiftool 逐值相等" if ok else
               ("有 %d 族不等" % len(bad) if bad else "部分族 exiftool 无读数可比"),
               "已确认" if ok else ("被否证" if bad else "未判")))

    # ================= 地址臂：exiftool -v3 的地址与原始字节 ==============
    L.append("-- 地址臂：exiftool -a -u -n -v3 的地址与原始字节（同样只解析 stdout）--")
    v3, v3err = run_exiftool(exe, path, ["-v3"])
    dump_mid(outdir, path, "exiftool-v3", v3, v3err)
    if v3 is None:
        log("v3", v3err)
    else:
        dumps = parse_tag_dumps(v3)
        log("v3", "抓到 %d 个带地址的标签转储（stderr %d 字节）"
            % (len(dumps), len(v3err or "")))
        for key in sorted(meta):
            m = meta[key]
            hits = dumps.get(m["addr"], [])
            if not hits:
                log("addrcheck", "%s 我算的载荷绝对地址=%s(0x%x) 在 verbose 里没有标签转储"
                    "（大标签被 [snip] 截掉，或口径不对）-> 未判"
                    % (key, m["addr"], m["addr"] or 0))
                continue
            th = [h for h in hits if h["tag"] == m["tag"]]
            if not th:
                log("addrcheck", "%s 地址 %s 上 exiftool 挂的标签=%s，与我取的 0x%04x 不符 -> 不等"
                    % (key, m["addr"], ["0x%04x" % h["tag"] for h in hits], m["tag"]))
                continue
            h = th[0]
            n = min(len(h["bytes"]), m["nbytes"])
            eq = n and h["bytes"][:n] == m["raw"][:n]
            log("addrcheck", "%s @%s exiftool 挂标签 0x%04x 段=%r，比了 %d/%d 字节 ⇒ %s"
                % (key, m["addr"], h["tag"], h["section"], n, m["nbytes"],
                   "字节逐等" if eq else "字节不等"))

    # ================= darktable 规则实测量 ==============================
    L.append("-- darktable 规则实测量：%s --" % DT_RULE_NAME)
    for cont in conts:
        fams = {}
        for key in conts[cont]:
            fams[key.split("|")[-1]] = arrays[key]
        note_ = "已确认口径" if confirmed[cont] else ("被否证口径（不要引用）" if refuted[cont] else "未判口径")
        log("dt-rule.%s" % cont, "%s  [%s]" % (dt_rule(fams), note_))
    # 旁证：同一偏移**不解密**直接在密文上量，规则合不合？（判清旧读数读的是哪一种）
    pre_groups = OrderedDict()
    for key, pv in pre_arrays.items():
        pre_groups.setdefault(key.split("|")[0], {})[key.split("|")[-1]] = pv
    for cont, fams in pre_groups.items():
        log("dt-rule.%s.密文直读" % cont,
            "%s  [这是『没解密就读』的对照量，不是读数]" % dt_rule(fams))

    # ================= 形状普查 =========================================
    L.append("-- 形状普查（只报观察到的形状，不把候选约定写成结论）--")
    for key in sorted(arrays):
        if refuted.get(key.split("|")[0]) and not confirmed.get(key.split("|")[0]):
            continue
        log("shape.%s" % key, shape(arrays[key]))

    # ================= 跨口径对照：哪些存放位其实是同一张表 =================
    L.append("-- 跨口径对照（只报「相等/不相等」，不下约定结论）--")
    cont_fams = OrderedDict()
    for cont, keys in conts.items():
        if refuted.get(cont) and not confirmed.get(cont):
            continue
        d = {}
        for key in keys:
            d[key.split("|")[-1]] = arrays[key]
        cont_fams[cont] = d
    def strip_cmp(a, b):
        """假设 a 的第 0 项是计数 nc：比 a[1:1+nc] 与 b[0:nc]，并如实报两侧余量是不是全 0。
        不下「a[0] 就是条数」的结论——只是把这条假设代入后看两边对不对得上。"""
        nc = a[0]
        if nc <= 0 or nc > len(a) - 1 or nc > len(b):
            return None
        body, other = a[1:1 + nc], b[:nc]
        if body != other:
            return "第 0 项=%d 当作条数去比，前 %d 项**不等**（%s vs %s）" % (nc, nc, body, other)
        pad_a, pad_b = a[1 + nc:], b[nc:]
        return "前 %d 项逐等；A 侧余量 %s 是否全 0=%s；B 侧余量 %s 是否全 0=%s" % (
            nc, pad_a, all(v == 0 for v in pad_a), pad_b, all(v == 0 for v in pad_b))

    for fam in ("vig", "ca", "dist"):
        cs = [(c, f[fam]) for c, f in cont_fams.items() if f.get(fam)]
        for i in range(len(cs)):
            for j in range(i + 1, len(cs)):
                a, b = cs[i], cs[j]
                log("cross.%s" % fam, "%s vs %s -> %s"
                    % (a[0], b[0], "整段相等" if a[1] == b[1] else "不相等"))
                for x, y in ((a, b), (b, a)):
                    if len(x[1]) < 2:
                        continue
                    r = strip_cmp(x[1], y[1])
                    if r:
                        log("cross.%s.去首项" % fam,
                            "%s 去掉第 0 项（=%d）后 vs %s：%s" % (x[0], x[1][0], y[0], r))

    # ================= 汇总 =============================================
    prim_ok = [k for k in matched if k.split("|")[0].startswith(PRIMARY)]
    prim_bad = [k for k in nomatch if k.split("|")[0].startswith(PRIMARY)]
    sec = [k for k in matched if not k.split("|")[0].startswith(PRIMARY)]
    if prim_bad:
        return ("FAIL %s 主臂与 exiftool 不等：%s" % (os.path.basename(path), prim_bad)), 1
    if prim_ok:
        return ("OK %s 主臂 %d 族与 exiftool 逐值相等（%s）%s"
                % (os.path.basename(path), len(prim_ok),
                   ",".join(sorted({k.split("|")[0] for k in prim_ok})),
                   "；附带臂还确认了 %s" % sorted({k.split("|")[0] for k in sec}) if sec else "")), 0
    if sec:
        return ("未判 %s 主臂（注记替换密码表/图像 SubIFD）没有可比读数，只有附带臂 %s 相等"
                % (os.path.basename(path), sorted({k.split("|")[0] for k in sec}))), 2
    return ("未判 %s 我方读数与 exiftool 无一组可比" % os.path.basename(path)), 2


def dump_mid(outdir, path, suffix, out, err):
    stem = os.path.splitext(os.path.basename(path))[0]
    try:
        with open(os.path.join(outdir, "%s.%s.txt" % (stem, suffix)), "w",
                  encoding="utf-8", errors="replace") as f:
            f.write(out or "")
            f.write("\n######## STDERR ########\n")
            f.write(err or "")
    except Exception:                                          # noqa: BLE001
        pass


# ===========================================================================
# 8. 主流程
# ===========================================================================

DEFAULT_TARGETS = [
    os.path.join("tests", "output", "raw_samples3", "HASSELBLAD_Lunar_4.arw"),
    os.path.join("tests", "output", "raw_samples4", "Sony_ILCE7M2_IDC.ARW"),
    os.path.join("tests", "output", "raw_samples4", "Sony_ILCE7S.ARW"),
    os.path.join("tests", "output", "raw_samples", "Sony_ILCE7M2_IDC.ARW"),
    os.path.join("tests", "output", "raw_samples", "Sony_ILCE7S.ARW"),
]


# Sony.pm:1826-1850 自己列了 0x9400 的「加密首字节 -> 解密首字节」对照表。
# 用它校验本脚本**由公式自造**的反向码表：这不是抄 tr///，是拿另一处文档值对答案。
SONY9400_DECODED = [(0x07, 40), (0x09, 204), (0x0a, 202), (0x0c, 27), (0xd0, 58),
                    (0x23, 62), (0x24, 48), (0x26, 215), (0x28, 28), (0x31, 106),
                    (0x32, 89), (0x33, 63), (0x41, 92), (0x43, 232)]
# Sony.pm:1830 对这一条自己写着「0xd0 (e) H90, W650, W690: tag9400 decoding appears
# not valid/different」⇒ 它本来就不主张符合替换密码，不能当码表的反证。
SONY9400_SELF_FLAGGED = {0xd0: "Sony.pm:1830 自己标注该条 'tag9400 decoding appears "
                               "not valid/different'"}


def check_cipher_against_sony9400():
    """返回 (逐对结果, 真反证列表)。真反证 = 对不上且不被 Sony.pm 自己标注为无效。"""
    pairs, real = [], []
    for e, d in SONY9400_DECODED:
        got = DEC_LIST[e][0]
        ok = (got == d)
        pairs.append((e, got, d, ok))
        if not ok and e not in SONY9400_SELF_FLAGGED:
            real.append("加密 0x%02x 我解成 %d，Sony.pm 记 %d" % (e, got, d))
    return pairs, real


def main(argv):
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    args = argv[1:]
    outdir = os.path.join(root, "tests", "output", "sony-enciph")
    if args[:1] == ["--out"]:
        outdir = os.path.abspath(args[1])
        args = args[2:]
    if not args or args[:1] == ["--default"]:
        targets = [os.path.join(root, p) for p in DEFAULT_TARGETS]
    else:
        targets = [os.path.abspath(a) for a in args if a != "--default"]

    L = []
    L.append("== _probe-sony-enciphered-lens.py readout ==")
    L.append("script=%s" % os.path.abspath(__file__))
    L.append("outdir=%s" % outdir)
    exe = find_exiftool(root)
    L.append("exiftool=%s exists=%s" % (exe, bool(exe)))
    L.append("")
    L.append("-- 1. 替换密码码表：由公式 c=(b*b*b)%249 自造，未抄 exiftool 的 tr/// 字面量 --")
    L.append("cipher.note=%s" % "取值 249-255 的字节不翻译（Sony.pm:11517 注释）")
    L.append("cipher.enc[2..6]=%s  # 与 Sony.pm:11522 tr 搜索串的头几项 08 1b 40 7d d8 对齐"
             % [ENC[b] for b in (2, 3, 4, 5, 6)])
    L.append("cipher.image_count=%d of 256 -> 单射（双射）=%s"
             % (len(set(ENC)), len(set(ENC)) == 256))
    L.append("cipher.collision_classes=%s 个数=%d" % (COLLISION_IMAGES, len(COLLISION_IMAGES)))
    L.append("cipher.fixed_points(enc[b]==b 的字节)=%s 个数=%d" % (FIXED_POINTS, len(FIXED_POINTS)))
    fp_formula = [b for b in FIXED_POINTS if b < MOD]
    L.append("cipher.fixed_points.split=由公式产生的不动点(0..248)=%s 个数=%d ；"
             "不翻译区(249..255)天然自等 个数=%d"
             % (fp_formula, len(fp_formula), len(FIXED_POINTS) - len(fp_formula)))
    L.append("cipher.note_named_groups 实算：0-1->%s  82-84->%s  165-167->%s  248->%s"
             % ([(b, ENC[b]) for b in (0, 1)], [(b, ENC[b]) for b in (82, 83, 84)],
                [(b, ENC[b]) for b in (165, 166, 167)], [(248, ENC[248])]))
    L.append("cipher.reason=%s"
             % "249=3*83；立方在 Z/3 上是双射(0,1,8->0,1,2)，在 Z/83 上因 gcd(3,82)=1 也是双射，"
               "CRT ⇒ b^3 mod 249 在 0..248 上双射；249-255 走不翻译 ⇒ 整表 256→256 双射")
    L.append("cipher.roundtrip=%s" % all(DEC_LIST[ENC[b]][0] == b and len(DEC_LIST[b]) == 1
                                          for b in range(256)))
    pairs9400, real9400 = check_cipher_against_sony9400()
    n_ok = sum(1 for p in pairs9400 if p[3])
    L.append("cipher.vs_Sony.pm:1850 独立校验（拿 Sony.pm 自己记的 0x9400「加密首字节->解密首字节」"
             "对照表 %d 组对答案，不是抄 tr///）：对得上 %d/%d，真反证 %s"
             % (len(pairs9400), n_ok, len(pairs9400), real9400 or "无"))
    for e, got, d, ok in pairs9400:
        L.append("cipher.pair 加密0x%02x -> 我解=%d Sony.pm 记=%d %s%s"
                 % (e, got, d, "OK" if ok else "对不上",
                    "" if ok else "  [" + SONY9400_SELF_FLAGGED.get(e, "无反证说明") + "]"))
    L.append("")
    L.append("-- 2. 本机在位的件 --")
    for t in targets:
        L.append("target=%s exists=%s size=%s"
                 % (t, os.path.isfile(t),
                    os.path.getsize(t) if os.path.isfile(t) else "-"))
    L.append("说明：任务书把两支 Sony ARW 记在 raw_samples4/，本机实量 raw_samples4/ 只有 "
             "X-A1.raf / X-A10.raf，两支 ARW 实际在 raw_samples/ ⇒ 两种路径都进清单，"
             "不存在的按『文件不存在』点名。")

    if exe is None:
        L.append("")
        L.append("SONY-ENCIPH 未判 exiftool 不可用（%s 不在位）" % os.path.join(root, "publish"))
        write_readout(outdir, L)
        print(L[-1])
        return 2

    codes, sums, missing = [], [], []
    for t in targets:
        if not os.path.isfile(t):
            L.append("")
            L.append("############################ FILE %s" % t)
            L.append("file                               | 不存在 -> 这一条不跑")
            sums.append("文件不存在 %s" % t)
            missing.append(t)
            continue
        try:
            s, c = probe(t, exe, outdir, L)
        except Exception as e:                                  # noqa: BLE001
            import traceback
            L.append("EXCEPTION %r\n%s" % (e, traceback.format_exc()))
            s, c = "未判 探针自身异常 %r" % (e,), 2
        sums.append(s)
        codes.append(c)

    L.append("")
    L.append("== 每件汇总 ==")
    for s in sums:
        L.append("  * %s" % s)
    if missing:
        L.append("  · 清单里 %d 条路径本机不在位（%s）：只登记『文件不存在』，"
                 "不参与退出码，也不升级成任何读数判断。"
                 % (len(missing), ", ".join(os.path.relpath(x, root) for x in missing)))
    if not codes:
        L.append("SONY-ENCIPH 未判 清单里的路径本机都不在位，一件都没跑")
        write_readout(outdir, L)
        print(L[-1])
        return 2
    if any(c == 1 for c in codes):
        line = ("SONY-ENCIPH FAIL 主臂（替换密码注记表 / 图像 SubIFD 明文位）与 exiftool 至少一族不等；"
                "首差异见 xcheck.* 行（在位的 %d 件里退出码=%s）" % (len(codes), codes))
        rc = 1
    elif all(c == 0 for c in codes):
        line = ("SONY-ENCIPH OK 在位的 %d 件主臂三族都与 exiftool 逐值相等"
                "（详见各 xcheck.* / verdict.* / addrcheck 行；另有 %d 条路径不在位）"
                % (len(codes), len(missing)))
        rc = 0
    elif any(c == 0 for c in codes):
        line = ("SONY-ENCIPH FAIL 在位的 %d 件里有件跑不动（退出码=%s）；"
                "可判的件主臂与 exiftool 相等" % (len(codes), codes))
        rc = 1
    else:
        line = "SONY-ENCIPH 未判 没有一件完成主臂对撞（逐件退出码=%s）" % codes
        rc = 2
    L.append(line)
    write_readout(outdir, L)
    print(line)
    return rc


def write_readout(outdir, L):
    if not os.path.isdir(outdir):
        os.makedirs(outdir)
    p = os.path.join(outdir, "readout.txt")
    with open(p, "w", encoding="utf-8") as f:
        f.write("\n".join(L) + "\n")
    print("readout -> %s（%d 行）" % (p, len(L)))


if __name__ == "__main__":
    sys.exit(main(sys.argv))
