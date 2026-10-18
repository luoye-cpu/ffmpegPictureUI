"""独立读回 DNG 的 profile 标签 —— 判"-idc-look 的曲线到底写没写、写对没有"。

为什么不采信 dngtool 自己打印的行, 也不依赖 exiftool:
  · 本机 exiftool 13.59 解析 ProfileIFD 时直接报 "Bad format ... parsing aborted" ⇒ 它读不出这块;
  ⇒ 按规范原文自己读 (DNG_Spec_1_7_1_0.pdf):
      ProfileToneCurve   50940 (0xC6FC)  Type FLOAT, Count = Samples*2,
                          "Usage: IFD 0 or Camera Profile IFD"，每对 (x,y) 在 [0,1] 线性域、
                          x 严格递增，"DNG readers should interpolate the curve using a cubic spline"
      ProfileEmbedPolicy 50941 (0xC6FD)  Type SHORT
      ColorMatrix1       50717 (0xC61D)  Type SLONG×9 —— 基准 profile 还在的凭据
      ExtraCameraProfiles 50933 (0xC6F5) LONG, 指向 camera profile 块 (块 = BOM+magic 0x4352+IFD 偏移,
                          偏移相对块起点) —— 本工具**不**走这条路(实测 SDK 1.7.1 的 extras 体积预算
                          不闭合: 块头后面紧跟的就是 StripOffsets, 条带数据盖掉 profile 目录),
                          但读手仍要能识别它, 否则"为什么不用它"这条判断无法复核。
用法:
  py _readback-dng-profile.py <dng> --anchor                     # 只报看到什么
  py _readback-dng-profile.py <dng> --expect-curve "0:0 1:1" [--expect-policy N]
  py _readback-dng-profile.py <dng> --expect-no-tone
末行 `PASS=n FAIL=m`。
"""
import argparse
import os
import struct
import sys

T_TONE = 0xC6FC
T_POLICY = 0xC6FD
T_CM1 = 0xC61D
T_EXTRA = 0xC6F5
T_NAME = 0xC6F8
MAGIC_PROFILE = 0x4352

SZ = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 4, 11: 4, 12: 8}


class Tiff:
    def __init__(self, b):
        if b[:2] == b"II":
            self.e = "<"
        elif b[:2] == b"MM":
            self.e = ">"
        else:
            raise SystemExit("FAIL 不是 TIFF 头: %r" % b[:2])
        self.b = b

    def u16(self, o):
        return struct.unpack_from(self.e + "H", self.b, o)[0]

    def u32(self, o):
        return struct.unpack_from(self.e + "I", self.b, o)[0]

    def ifd(self, off):
        n = self.u16(off)
        if not 1 <= n <= 600:
            raise SystemExit("FAIL IFD 条目数 %d 不合理 @%d" % (n, off))
        out = {}
        for j in range(n):
            e = off + 2 + 12 * j
            t, ty, c = struct.unpack_from(self.e + "HHI", self.b, e)
            out[t] = (ty, c, self.u32(e + 8))
        return out

    def payload(self, ty, c, valfield, base=0):
        """值字段 ≤4 字节时它就是值; 否则是偏移。base: IFD0 用 0, profile 块内用块起点
        (规范: profile 块内 "All offsets are relative to the start of the byte order mark")。"""
        total = c * SZ.get(ty, 4)
        if total <= 4:
            return struct.pack(self.e + "I", valfield)[:total]
        return self.b[base + valfield:base + valfield + total]

    def numbers(self, ty, c, valfield, base=0):
        fmt = {1: "B", 3: "H", 4: "I", 7: "B", 9: "i", 11: "f", 12: "d"}[ty]
        raw = self.payload(ty, c, valfield, base)
        return list(struct.unpack(self.e + ("%d" % c) + fmt, raw))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dng")
    ap.add_argument("--anchor", action="store_true")
    ap.add_argument("--expect-curve")
    ap.add_argument("--expect-policy", type=int)
    ap.add_argument("--expect-no-tone", action="store_true")
    ap.add_argument("--against", help="同输入但不带 -idc-look 的产物, 用于比 IFD0 标签集合的差")
    a = ap.parse_args()

    tf = Tiff(open(a.dng, "rb").read())
    ifd0 = tf.ifd(tf.u32(4))
    tone = tf.numbers(*ifd0[T_TONE], base=0) if T_TONE in ifd0 else None
    policy = int(tf.numbers(*ifd0[T_POLICY], base=0)[0]) if T_POLICY in ifd0 else None

    extras = []
    if T_EXTRA in ifd0:
        ty, c, vf = ifd0[T_EXTRA]
        for o in ([int(x) for x in tf.numbers(ty, c, vf)] if c > 1 else [vf]):
            sub = tf.ifd(o + tf.u32(o + 4))
            nm = None
            if T_NAME in sub:
                ty2, c2, v2 = sub[T_NAME]
                nm = tf.payload(ty2, c2, v2, o).decode("utf-8", "replace").rstrip("\x00")
            extras.append({"off": o, "magic_ok": tf.u16(o + 2) == MAGIC_PROFILE, "name": nm})

    line = ("IFD0 标签 %d 个; ProfileToneCurve=%s(%s 点); ProfileEmbedPolicy=%s; ColorMatrix1=%s; "
            "ExtraCameraProfiles=%s"
            % (len(ifd0), "有" if tone else "无", len(tone) // 2 if tone else 0, policy,
               "有" if T_CM1 in ifd0 else "无", len(extras)))
    print(line)
    if tone:
        print("   曲线:", " ".join("%.6g/%.6g" % (tone[i], tone[i + 1]) for i in range(0, len(tone) - 1, 2)))
    for p in extras:
        print("   extra @%d magic_ok=%s name=%s" % (p["off"], p["magic_ok"], p["name"]))
    if a.anchor:
        print("PASS=1 FAIL=0")
        return 0

    bad = []
    if a.expect_no_tone:
        if tone is not None:
            bad.append("不该有 ProfileToneCurve, 实得 %d 点" % (len(tone) // 2))
    if a.against:
        # 与"同一输入不加 -idc-look"的产物比 IFD0 标签集合: 差集必须恰好是 {ProfileToneCurve}。
        # 这比"要求某条标签存在"更硬 —— 它同时排除"挤掉别的标签"和"多写别的标签"两个方向。
        tf2 = Tiff(open(a.against, "rb").read())
        base0 = tf2.ifd(tf2.u32(4))
        added = sorted(set(ifd0) - set(base0))
        removed = sorted(set(base0) - set(ifd0))
        print("   对比 %s: 多出 %s 少了 %s" % (
            os.path.basename(a.against),
            ",".join("0x%04X" % t for t in added) or "无",
            ",".join("0x%04X" % t for t in removed) or "无"))
        if added != [T_TONE]:
            bad.append("加 -idc-look 后新增标签应为且仅为 0xC6FC, 实得 %s"
                       % (",".join("0x%04X" % t for t in added) or "空"))
        if removed:
            bad.append("加 -idc-look 后丢了标签 %s ⇒ 主 profile 被改坏"
                       % ",".join("0x%04X" % t for t in removed))
        if T_TONE in base0:
            bad.append("对照件本身就有 ProfileToneCurve ⇒ 这条判据没意义, 换夹具")
    if a.expect_curve is not None:
        if tone is None:
            bad.append("IFD0 里没有 ProfileToneCurve")
        else:
            want = [float(t) for pair in a.expect_curve.replace(",", " ").split() for t in pair.split(":")]
            if len(tone) != len(want):
                bad.append("曲线长度期望 %d 实得 %d" % (len(want), len(tone)))
            elif any(abs(x - y) > 1e-6 for x, y in zip(want, tone)):
                bad.append("曲线值不符: 期望 %s 实得 %s" % (want, ["%.7g" % x for x in tone]))
            elif not all(0.0 <= v <= 1.0 for v in tone):
                bad.append("曲线值越出 [0,1]")
            elif any(tone[i + 2] <= tone[i] for i in range(0, len(tone) - 2, 2)):
                bad.append("曲线 x 未严格递增")
        if a.expect_policy is not None and policy != a.expect_policy:
            bad.append("policy 期望 %s 实得 %s" % (a.expect_policy, policy))

    for x in bad:
        print("FAIL " + x)
    print("PASS=%d FAIL=%d" % (0 if bad else 1, len(bad)))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
