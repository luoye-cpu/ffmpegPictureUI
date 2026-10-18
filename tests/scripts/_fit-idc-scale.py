"""索尼 IDC  adjustment 字段的**量纲标定夹具**：喂进 (IDC 原值, 外部实测值) 样本对, 拟合 y = a·x + b。

为什么需要它（不是"顺手多写一个工具"）：`-idc` 已经能读出源 RAW 里的生效数值面
（BrightnessAdj=200、SharpnessAdj=… 这一类 int32），但**这些数的量纲本机无一手材料**——
索尼没公布, exiftool 只做 PrintConv 表而不给单位。要把它们映进 DNG 的表达位
（BaselineExposure / ProfileToneCurve 等）就必须先知道 a、b；而 a、b 只能从
"同一场景在 Imaging Edge 里按已知档位导出、再实测"这条链取。本件负责那条链的**算与拒**：

  · 算：最小二乘 a/b + 残差；
  · 拒：样本数不足、残差超容差、x 无自由度（退化）⇒ **不出表**、非零退出。
    绝不返回 NaN / 不写"未标定"当"已标定"（NaN 进 JSON 会直接把下游写崩）。

输入形态（一个 JSON 文件，人工从导出实测里填）：
  [{"field": "BrightnessAdj",
    "samples": [{"idc": -5, "measured": 0.9}, {"idc": 0, "measured": 1.0}, ...]}, ...]

用法：
  py _fit-idc-scale.py --selftest                 # 合成自检（必须恢复出已知斜率）
  py _fit-idc-scale.py calibration.json           # 真标定；全过才出表
"""
import argparse
import json
import sys

MIN_N = 5          # 少于 5 个点不配谈"拟合"
MAX_REL_RESID = 0.02   # 最大残差 / 量程 的上限


def fit(xs, ys):
    """最小二乘。返回 (a, b, maxAbsResid)；退化时返回 None（不返回 NaN）。"""
    n = len(xs)
    sx = float(sum(xs)); sy = float(sum(ys))
    sxx = float(sum(x * x for x in xs)); sxy = float(sum(x * y for x, y in zip(xs, ys)))
    den = n * sxx - sx * sx
    if den == 0.0:
        return None                      # 所有 x 相同 ⇒ 斜率无定义
    a = (n * sxy - sx * sy) / den
    b = (sy - a * sx) / n
    resid = [abs(y - (a * x + b)) for x, y in zip(xs, ys)]
    return a, b, max(resid)


def fit_one(rec):
    """返回 (表项 dict 或 None, 原因串)。"""
    field = rec.get("field", "?")
    samples = rec.get("samples") or []
    xs = [float(s["idc"]) for s in samples]
    ys = [float(s["measured"]) for s in samples]
    if len(xs) < MIN_N:
        return None, "%s 样本数 %d < %d" % (field, len(xs), MIN_N)
    r = fit(xs, ys)
    if r is None:
        return None, "%s 的 idc 取值只有一个点（x 无自由度）⇒ 斜率无定义" % field
    a, b, mr = r
    span = max(ys) - min(ys)
    # span 为 0 时 measured 是常数：斜率必须为 0 才可信, 否则就是噪声放大
    scale = span if span > 0 else (abs(max(ys)) if max(ys) else 1.0)
    rel = mr / scale if scale else 0.0
    if rel > MAX_REL_RESID:
        return None, "%s 最大残差 %.6g / 量程 %.6g = %.1f%% > %.1f%%" % (
            field, mr, scale, 100.0 * rel, 100.0 * MAX_REL_RESID)
    return {"field": field, "slope": a, "intercept": b,
            "n": len(xs), "maxResid": mr, "relResid": rel}, "%s a=%.10g b=%.10g n=%d 残差 %.1f%%" % (
            field, a, b, len(xs), 100.0 * rel)


def selftest():
    """合成数据由**这里**生成, 拟合由被测的 fit() 做 —— 两个独立来源, 恒等才算有牙。"""
    fails = []
    n = 0

    def ck(cond, msg):
        nonlocal n
        n += 1
        print(("PASS " if cond else "FAIL ") + msg)
        if not cond:
            fails.append(msg)

    # ① 精确恢复已知斜率（生成常数与被测函数无关）
    TRUE_A, TRUE_B = 0.02, -1.0
    xs = [-5.0, -2.0, 0.0, 3.0, 5.0, 8.0]
    ys = [TRUE_A * x + TRUE_B for x in xs]
    r = fit(xs, ys)
    ck(r is not None and abs(r[0] - TRUE_A) < 1e-12 and abs(r[1] - TRUE_B) < 1e-12,
       "① 合成斜率恢复 a=%.12g b=%.12g（真值 %.12g / %.12g）" % (r[0], r[1], TRUE_A, TRUE_B))

    # ② 反控：期望值若手抄错一个量级, ① 的判据必须抓得住（即 ① 不是恒真）
    ck(abs(r[0] - 0.2) > 1e-3, "② 恢复值不等于错位一个量级的 a（防 ① 恒真）")

    # ③ 容差内噪声仍然出表
    ys2 = [TRUE_A * x + TRUE_B + (0.0005 if i % 2 else -0.0005) for i, x in enumerate(xs)]
    item3, why = fit_one({"field": "BrightnessAdj",
                          "samples": [{"idc": x, "measured": y} for x, y in zip(xs, ys2)]})
    ck(item3 is not None, "③ 容差内噪声出表（%s）" % why)

    # ④ 样本数不足 ⇒ 拒
    item, why = fit_one({"field": "SharpnessAdj",
                         "samples": [{"idc": x, "measured": TRUE_A * x + TRUE_B} for x in xs[:MIN_N - 1]]})
    ck(item is None, "④ 样本数不足必须拒（%s）" % why)

    # ⑤ 残差超容差 ⇒ 拒（拿一条真直线 + 一个离群点）
    ys5 = [TRUE_A * x + TRUE_B for x in xs]
    ys5[3] += 0.9
    item, why = fit_one({"field": "ColorNoiseReduction",
                         "samples": [{"idc": x, "measured": y} for x, y in zip(xs, ys5)]})
    ck(item is None, "⑤ 离群点残差超容差必须拒（%s）" % why)

    # ⑥ x 无自由度 ⇒ 拒且不得交 NaN
    item, why = fit_one({"field": "HDRTone",
                         "samples": [{"idc": 3, "measured": 0.1 * i} for i in range(6)]})
    ck(item is None and "无定义" in why, "⑥ 退化 x 必须拒并点名成因（%s）" % why)

    # ⑦ 出表项必须能进 JSON（allow_nan=False）—— 下游拿 NaN 会直接写崩
    js = json.dumps(item3, allow_nan=False)
    try:
        json.dumps({"x": float("nan")}, allow_nan=False)
        raised = False
    except ValueError:
        raised = True
    ck("slope" in js and raised, "⑦ 表项可进 JSON, 且同一条判据对 NaN 确实会抛（防恒真）")

    print("")
    print("PASS=%d FAIL=%d" % (n - len(fails), len(fails)))
    return 0 if not fails else 1


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("calibration", nargs="?", help="(IDC 原值, 实测值) 样本对的 JSON 文件")
    ap.add_argument("--selftest", action="store_true", help="跑合成自检")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.calibration:
        print("用法: _fit-idc-scale.py [--selftest | calibration.json]")
        return 2
    recs = json.load(open(a.calibration, encoding="utf-8"))
    if isinstance(recs, dict):
        recs = [recs]
    table = []
    reasons = []
    for rec in recs:
        item, why = fit_one(rec)
        (table if item else reasons).append(item or why)
        print(("OK   " if item else "REJECT ") + why)
    if reasons or not table:
        print("\n== 拒出表 ==")
        for r in reasons:
            print("  " + r)
        print("未全数过容差 ⇒ 不产出量表（宁缺毋滥）")
        return 1
    print(json.dumps({"scaleTable": table, "minN": MIN_N,
                      "maxRelResid": MAX_REL_RESID}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
