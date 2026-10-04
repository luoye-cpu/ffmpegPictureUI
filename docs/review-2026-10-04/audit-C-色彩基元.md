# 线 C 审查报告：色彩基元与数值正确性（2026-10-04）

范围：`ColorSpaceRegistry`（色度表 / 白点 / 色度矩阵 / Bradford / CICP token 映射 / 友好名解析）、
`ColorMapping/TransferCurve.cs`、`ToneMapping.cs`、`Bt2446MethodA.cs`、`Bt2100Ootf.cs`、
`IccProfileService.cs` + `IccProfileBuilder.cs`、`ColorSpaceDescriptor.cs` + `ColorSpaceIdentify.cs`、
`ColorIntent.cs` 的 `EquivalenceTolerances`。

方法：**以代码为准**；用 `ServiceProbe` 取读数（`colormath`/`matrix`/`curve`/`pq`/`ootf`/`bt2446`/`icc`/`iccname`）
＋ 自写 Python（`C:/tmp`，未入仓）独立复算矩阵 / 曲线 / ICC tag，并与 lcms（`iccgen` 产物）、
BT.2446-1 标准正文（`tools/std/bt2446-1.txt`）对拍。全程只读，未改 `src/`、`tests/`。

---

## 0. 一页结论

- **基元数值本身几乎全对**：色度表、Bradford、PQ/HLG/sRGB/BT.709/ProPhoto 曲线、BT.2446A、HLG OOTF、
  色调映射单调/端点/消色差、ICC 的 `wtpt`/`rXYZ/gXYZ/bXYZ`/`chad`/`rTRC` —— 逐项对拍通过
  （`colormath 12/0`、`matrix 15/0`、`curve 44/0`、`pq 13/0`、`ootf 11/0`、`bt2446 18/0`、`iccname 10/0`）。
- **新发现 6 条**（P2×1、P3×5），**无 P0/P1**。最实质的一条是 **D50 白点在本仓存在两个互不相同的定义**
  （`ColorSpaceRegistry.IccD50Xy` 用 xy 反解出 Z=0.82510，ICC PCS/`IccProfileBuilder`/lcms 用 Z=0.8249），
  使 **ICC 判色域**带 2~4e-4 系统偏差（CICP→CICP 映射不受影响，已数值证明抵消）。
- 另有 5 条**已登记但仍未结案**（P4.1/P4.2 等），本报告只点名、不重复计数（见 §4）。

---

## 1. 发现表（新发现，按定级）

| 级别 | 位置 | 问题 | 为何错（数值对拍） | 证据 | 门禁覆盖? |
|---|---|---|---|---|---|
| **P2** | `ColorSpaceRegistry.cs:679` `IccD50Xy={0.34570,0.35850}` vs `IccProfileBuilder.cs:31` `D50Xyz={0.9642,1.0,0.8249}` | **D50 白点两套定义**。`XyzFromXy(0.34570,0.35850)` → XYZ **(0.96430,1,0.82510)**，而 ICC PCS D50 是 **(0.9642,1,0.8249)**（Z 差 2.0e-4） | `ColorantsD50()` 用 xy 版；ICC 里 `rXYZ/gXYZ/bXYZ` 与 lcms 产物用 XYZ 版 ⇒ 判色域比对带系统偏差：本仓 `ColorantsD50(bt709)` vs lcms sRGB ICC `rXYZ` max\|Δ\|=**3.84e-4**；vs 自写 ICC max\|Δ\|=**2.15e-4**（ProPhoto 同理 2.15e-4）。均吃进 `KnownSpaceTol=2e-3` 的余量（占 ~10–20%）。**CICP→CICP 映射不受影响**：数值证明 `inv(C2020(D50))·C709(D50)` 在 D50xy / D50xyz / 直连三种算法下逐元素相同（6 位小数一致）⇒ 白点误差在复合中精确抵消 | Python 复算 `ColorSpaceRegistry` 与 lcms stdicc 实测 rXYZ 对撞（本报告 §2 行 3–4） | ❌ 无。`colormath`/`iccname` 容差 2e-3 盖住了它 |
| **P3** | `TransferCurve.cs:110-111` `Bt709 = (1/0.45, 1/1.099, 0.099/1.099, 1/4.5, 0.08125)` | BT.709 用**圆整** α=1.099/β=0.099，而标准精确值 α=1.09929682680944（BT.709-6）、lcms/ICC 用精确值 | 本仓 `A=1/1.099=0.909918`；精确 `1/α=0.909672`；lcms ICC `rTRC` 实测 `a=0.909668`。\|A−A_lcms\|=**2.50e-4 > CurveParams=1e-4** ⇒ 一条 bt709 ICC 与 CICP `bt709` **被判不等价**（`TransferCurve.Equivalent` 返回 false）。像素差 max=**0.064 LSB@8bit / 16.4 LSB@16bit**（小，但违反本项目"曲线必须精确"口径，且审计 §3 自己用的是 α=1.0993） | 本仓 lcms 产物 `rTRC: para type=3 2.2222 0.9097 0.0903 0.2222 0.0812`（`icc-dump`）；Python 复算 | ⚠ 部分：`curve` 只测本仓锚点，未与 lcms 的 bt709 ICC 对撞 |
| **P3** | `IccProfileService.cs:537` `info.Version = $"{bytes[8]}.{bytes[9]}.{bytes[10]}"` | ICC 版本号是 **BCD**，`bytes[9]` 直接按十进制打印 | lcms 产物头 `04 40 00 00` 应显示 **4.4.0**，实得 **4.64.0**（`0x40` 当 64）。自写产物 `04 00 00 00` 恰好正确，故只误导 lcms/第三方 profile 的读数 | `ServiceProbe icc` 打印 `version=4.64.0`（本报告 §2 行 12）；Python 读头 `04400000` | ❌ 无（纯展示字段，未参与决策） |
| **P3** | `tests/ServiceProbe/Program.cs:7812` `EnumerateS15` 用 **unsigned** `ReadBe32/65536` | `icc-dump` 把 XYZ/sf32 的**负** s15Fixed16 分量显示成 ~65535.99 | BT.2020 ICC `rXYZ` 的 Z 分量真值 **−0.0019**（signed），`icc-dump` 显示 `65535.9981`；Display P3 同理。看报告的人会误判为"ICC 写坏"。`icc` 探针用的 `ReadS15` 是 signed、正确 | `icc-dump stdicc_bt2020...` → `rXYZ: XYZ [0.6735, 0.2790, 65535.9981]`；Python signed 读得 −0.0019 | ❌ 无（诊断探针自身的显示缺陷；`tests/` 只读，未改） |
| **P3** | `TransferCurve.cs:344-348` `HlgEotf` | HLG 解码对**负输入**不做符号镜像（`x≤0.5` 分支 `x²/3` 把负值变正），与类头声明"参数形曲线对负输入做符号镜像外推（矩阵混色会产生负值）"的**统一策略不一致** | `HlgEotf(−0.1)=0.00333`（正），而非 −0.00333；`FromLinear` 侧 `HlgOetf` 对负值给 0。PQ 侧是 `x≤0→0`（钳黑），参数形是符号镜像 ⇒ 三套口径。可达性窄（容器编码值一般 [0,1]，仅 float 混色可能出现负） | 代码读码 + Python 复算 | ❌ 无 |
| **P3** | `IccProfileService.cs:463-470` `NamedIccSlug` | 只认 `prophoto` / `bt2100-pq`；`ResolveNamedIcc("P3 PQ",…)` 因 slug=null 且回退 `GetOrGenerateStandardIcc(...,smpte2084)` 而返回 **null**，即便 P3 PQ 归一化后就是 BT.2020 PQ（其 ICC 存在） | `ServiceProbe icc "P3 PQ"` ⇒ `icc=(null) FAIL`（而 `icc "BT.2020 PQ"` 正常产 `named_bt2100-pq.v2.icc`）。**生产 0 消费**（`ResolveNamedIcc` 仅被 `tests/ServiceProbe` 与 `tests/scripts/ensure-fixtures.ps1` 调用）⇒ 影响面仅诊断/夹具，非产品像素 | `ServiceProbe icc` 两行对比 | ❌ 无（`icc` mode 未接默认 22-mode 清单） |

> 说明：第 6 条的"生产 0 消费"与 `HANDOVER.md:1174`「`ResolveNamedIcc` 仓内不可达」一致，不重复计为产品缺陷。

---

## 2. 数值对拍表

| # | 量 | 代码值 | 标准值 / 参照 | 偏差 | 判定 |
|---|---|---|---|---|---|
| 1 | BT.709 / BT.2020 / sRGB / Display P3 / DCI-P3 / Adobe RGB / ProPhoto 原色 xy + 白点 | 见 `ColorSpaceRegistry.cs:562-610` | H.273 / BT.2020 / EG 432-1 / ROMM | 与 zimg 逐元素 ≤4.9e-6（A 档）或 ≤2.4e-4（B 档：C 白点/影院白位数） | ✅ `colormath 12/0` |
| 2 | `smpte170m≡smpte240m`（唯一合法重复对） | 同 SMPTE-C 原色 | zimg 同判 | 0（白名单内） | ✅ 白名单外重复 0 对 |
| 3 | `ColorantsD50(bt709)` vs lcms sRGB ICC `rXYZ` | `+0.436070,+0.385115,+0.143111;…` | lcms `+0.436035,+0.385117,+0.143051;…` | max **3.84e-4** | ⚠ 见 P2（D50 双定义） |
| 4 | `IccProfileBuilder`（D50xyz）vs lcms ICC | `+0.436045,+0.385076,+0.143078;…` | 同上 | max **2.15e-4**（lcms 色度仅 4 位小数） | ✅ 属精度地板 |
| 5 | `BradfordAdapt` 复合 | `M⁻¹·L·M`（`ColorSpaceRegistry.cs:779-788`） | ICC.1 色适应 | `bt2020↔bt709` 互逆残差 **6.7e-16** | ✅ |
| 6 | `chad`（sRGB/DisplayP3/BT.2020，D65 原白） | `+1.04788,+0.02292,−0.05022; +0.02959,+0.99048,−0.01707; −0.00925,+0.01508,+0.75168` | 标准 Bradford D65→D50 | max 4.9e-4（D50 位数） | ✅ **方向正确**（与 lcms 一致，native→D50；解开 FIX_PLAN §6 待核项） |
| 7 | `chad`（ProPhoto，D50 原白） | ≈单位阵（`+0.99995…`） | 原白=D50 ⇒ 恒等 | 6.9e-6 | ✅ |
| 8 | PQ 常数 m1/m2/c1/c2/c3 | `2610/16384, 2523/4096·128, 3424/4096, 2413/4096·32, 2392/4096·32` | SMPTE ST 2084:2014 | 逐字一致 | ✅ |
| 9 | PQ EOTF(0.5) | **92.2457 nits** | 92.2457 nits（ST 2084 复算） | 0（往返 2.2e-15） | ✅（`pq` 探针"期望 92.7"略偏，仅锚点文案问题） |
| 10 | HLG a/b/c + OETF/EOTF + γ=1.2 | `0.17883277/0.28466892/0.55991073`；γ(1000)=1.2 | BT.2100-2 Table 5 / Note 5f | 0（往返 2.2e-16） | ✅ |
| 11 | HLG OOTF | α=L_W；(1,1,1)→1000 nits；逆 OOTF 往返 4.4e-16 | BT.2100 Note 5h（不钳超白） | 0 | ✅ `ootf 11/0` |
| 12 | sRGB para（g,a,b,c,d） | `2.4, 1/1.055, 0.055/1.055, 1/12.92, 0.04045` | IEC 61966-2-1 | vs lcms ICC `a=0.94786` 差 6e-6 | ✅ 等价 |
| 13 | **BT.709 para（g,a,b,c,d）** | `2.2222, 0.909918, 0.090082, 0.2222, 0.08125` | 精确 `0.909672`；lcms ICC `0.909668` | **2.50e-4 > 1e-4** | ⚠ 见 P3 |
| 14 | ProPhoto 曲线（1.8+线性趾） | `C=1/16, D=1/32`，拐点连续 | ROMM / IEC 61966-2-5 | 拐点跳变 2.2e-19 | ✅ |
| 15 | BT.2446A 全部常数 | ρ=1+32(L/10000)^(1/12.4)、Step2 三段、Table3 色差、Table4 指数 a1/b1/c1/a2/b2/c2、T=70 | `tools/std/bt2446-1.txt` Table 2/3/4 | 逐字一致；端点 0→0、1→1；E(255)=1.246567→999.8 nits | ✅ `bt2446 18/0` |
| 16 | ICC `wtpt` | `0.9642 / 1.0 / 0.8249`（两路一致） | ICC PCS D50 | 0 | ✅ |
| 17 | ICC `rXYZ/gXYZ/bXYZ`（ProPhoto 自写） | `+0.79772,+0.28805,−0.00000;…` | Lindbloom D50 复算 | max **7.1e-6** | ✅ |
| 18 | ICC `rTRC`（ProPhoto / BT.2020PQ） | para type3 / curv 1024（PQ） | IEC 61966-2-5 / ST 2084 | PQ LUT 单调，max\|Δ\|=**4.0e-5**（u16 量化地板 1.5e-5） | ✅ |
| 19 | ICC 头 class/cs/pcs/desc | `mntr / RGB / XYZ / mluc`；`chad` 存在；**无 `chrm`** | ICC.1:2010 v4 matrix/TRC | `chrm` 在 v4 可选（skcms 同样不写） | ✅（与 lcms 的差异属允许） |
| 20 | 不同空间两两 D50 矩阵最小差 | — | — | **最小非零差 = 0.0192（bt709 vs bt470bg）**；Display P3 vs sRGB=**0.0931** | ✅ 见 Q6 结论 |

---

## 3. 已核实无问题（逐条回应任务 6 问）

1. **色度与白点**：9 个 token + 别名的原色/白点全部与标准一致（BT.709/sRGB/BT.2020/Display P3/DCI-P3/Adobe RGB/ProPhoto/SMPTE-C/P22/Illuminant C）；
   `smpte428`（XYZ 三角，xy 表达不了）与 `film` **故意不收录**走 fail-closed —— 旧"5 token 共用 P22"与"smpte428 冒充 smpte431"两处 P1 已确证修好（`colormath 12/0`）。
2. **色度矩阵**：`PrimariesToXyz`（Lindbloom 解 S）、`BradfordAdapt`（`M⁻¹·L·M`）、`ColorantsD50`、`LinearMatrixBetweenPrimaries` 的构造与乘序全对；
   与 lcms 产物、zimg、发布值三路对拍一致（§2 行 1/3/4/5）。`bt2020↔bt709` 互逆残差 6.7e-16。
3. **传递曲线**：PQ/HLG 常数与公式逐字对标准；sRGB/BT.709/ProPhoto/gamma 的锚点与**双向互逆**全部通过（`curve 44/0`，往返 ≤8e-15）；
   `SdrWhiteNits=203`（BT.2408 图形白）、`PqPeakNits=10000`、HLG 1000 语义正确（PQ code 0.5807 ↔ 203 nits 实测吻合）。
4. **色调映射**：reinhard(segmented)/hable/mobius 均单调、端点 0→0 与峰值→1、消色差保持；除零/负值/越界均有守卫
   （mobius 分母 1e-12、peak−1 取 1e-6；ApplyRgb maxY≤1e-6 短路；BT.2446A 负值钳 0）。`bt2446` 的 knee 跳变 5.5e-4 是**建议书常数自身**的取整性质，非实现缺陷。
5. **ICC 生成**：`wtpt`（D50 0.9642/1/0.8249）、`chad`（方向与 lcms 一致）、`rXYZ/gXYZ/bXYZ`（D50 适应）、`rTRC/gTRC/bTRC`（para type3 / PQ curv）、头 class/cs/pcs/desc 全部正确；
   自写路径与 `iccgen`(lcms) 路径**色度一致到 ~2e-4（lcms 4 位小数地板）**；`chrm` 在 v4 非必需，省略合法。
6. **`EquivalenceTolerances`**：**不会**把不同空间判成同一空间。数值证据：注册表内任意两个**不同原色**空间的 D50 矩阵最小差 = **0.0192**，远大于 `MatrixElements=1e-5`；
   唯一 <1e-5 的一对是 `smpte170m≡smpte240m`（原色真的相同），且二者曲线不同（`Gamma22` vs `Smpte240M`）⇒ `SameCurveAs` 仍返回 false。Display P3 vs sRGB 差 0.093，无混淆风险。
   ⚠ 但 `MatrixToIdentity/MatrixElements/CurveParams/Chromaticity` **四个常量仍是死的**，真正生效的是散落硬编码的 `1e-5`（`ColorSpaceDescriptor.cs:208`）、`1e-4`（`TransferCurve.cs:367`）、`2e-3`（`ColorSpaceIdentify.cs:32`）、`5e-3`（`ColorSpaceDescriptor.cs:182`）、`1e-9`（`ColorTransformPlan.cs:760`）—— 见 §4。

---

## 4. 未覆盖 / 已登记未结案（点名）

以下均为**旧审计已登记、本轮复核仍未修**（非"已结案其实没修"，故不计入 §1）：

| 项 | 位置 | 状态 | 本轮核实 |
|---|---|---|---|
| `ColorMatch RGB` 无色度定义（Rp/Gp/Bp/Wp 全空）⇒ 任何矩阵转换 fail-closed，注释"像素经矩阵转换到 BT.2020"对它是**空头** | `ColorSpaceRegistry.cs:142-144` | USABILITY_AUDIT_2026-10-02 #7，🔵 | 复核成立：`NamedSpaceColorantsD50("ColorMatch RGB")=null`；`icc "ColorMatch RGB"` 只产 BT.2020 ICC（OutPrimaries 归一），无像素转换 |
| `EquivalenceTolerances` 4/6 常量零消费 + 散落魔数（口径失控） | `ColorIntent.cs:134-148` | AUDIT §4.5 / FIX_PLAN P4.1，**未结案** | grep 复核：消费点仍只有 `ContainmentEpsilon`(2) 与 `MinConfidenceForMapping`(5) |
| `KnownSpaceTol=2e-3` 吸附 + 缺"偏移 1e-3~2e-3 合成 ICC 必须报差异"夹具 | `ColorSpaceIdentify.cs:32` | AUDIT §4.5 / FIX_PLAN P4.2，**未结案** | 复核成立：`RefineKnownSpace` 命中即 `d.RgbToXyzD50=best`（采用理论值），2e-3 内静默吸附 |
| `ResolvePrimariesToken` 危险反查（`bt2020` 可命中 DCI-P3/ProPhoto 色度） | `ColorSpaceRegistry.cs:521-534` | USABILITY_AUDIT #3，🟡 | 复核成立：仍按 `Spec.OutPrimaries` 遍历取**首个**匹配；全仓 **0 消费点**（仅定义） |
| `ValidateCicpTriple` 白名单只 4 个 primaries ⇒ 合法 token 误报警告 | `ColorSpaceRegistry.cs:707` | USABILITY_AUDIT #4，🟡 | 复核成立：`bt470m/bt470bg/smpte170m/smpte240m/jedec-p22` 均会触发"非工具链常用值"假警告 |

**能力边界（fail-closed，非缺陷）**：`smpte428`/`film` 不收录；`log100`/`log316` CICP 明确不支持；
`ResolveNamedIcc` 仓内不可达（`HANDOVER.md:1174`）。

**未做的外部验证（本轮受限）**：本机无 exiftool、无 PIL/lcms-python、无独立 CMM，故自写 ICC **未过第三方 CMM 的端到端打开测试**；
已用 lcms（`iccgen` 产物）的 `rXYZ/gXYZ/bXYZ/chad/wtpt/rTRC` 逐项对撞替代（§2 行 3/4/6/12）。

**临时脚本**：`C:/tmp/auditC_icc.py`、`auditC_num.py`、`auditC_pair.py`、`auditC_pqlut.py`、`auditC_d50.py`、`auditC_bt709.py`（均未入仓）。
