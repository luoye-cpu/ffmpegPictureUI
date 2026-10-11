# A1 · 原色表与矩阵推导（primaries / matrix derivation）— 只读审查

- 仓库：`C:\PLAN\ffmpegPictureUI`　HEAD=`c18dc4a`（v1.6.0-beta7，2026-10-10 08:20 +0800）　工作树干净
- 范围：`src/FfmpegGui/Services/ColorSpaceRegistry.cs`、`src/FfmpegGui/Services/ColorMapping/ColorSpaceDescriptor.cs`、`LinearMatrixBetweenPrimaries` 全部消费者
- 本轮**未修改任何源文件**；所有读数为实测或原文回读

实测臂（都跑过，非估）：

| 臂 | 内容 | 读数 |
|---|---|---|
| ① 本仓内核 | `tests\ServiceProbe\bin\Release\net11.0\win-x64\ServiceProbe.exe colormath` | **PROBE RESULT: pass=12 fail=0** |
| ② zimg 参照 | 本机 `publish\PLAN\ffmpeg-full\ffmpeg.exe` = **git-2026-10-07-e5a08f7c0e**（审计旧文档用的是 2026-08-06，版本已换代）；`zscale=pin=<T>:tin=linear:min=gbr:p=bt709:t=linear:m=gbr`，单位基向量走 `pipe:0/pipe:1`（不落盘） | 11 行矩阵全部复现到 5 位小数（含本仓**不实现**的 `film`、`smpte428`） |
| ③ 独立推导 | 自建 py：Lindbloom 解 S → `PrimariesToXyz`、`M⁻¹·L·M` 适应、伴随矩阵求逆；输入取各标准公布的 xy/白点 | 7 格与 ② 逐元素 0.00000；`bt470m` 1.1e-4 / `film` 1.3e-4 / `smpte428` 1.0e-4 / `smpte431` 2.4e-4（→ 见 §3-N1，这最后一项被证伪为另一因） |
| ④ ICC/lcms 参照 | `ffmpeg … iccgen=color_primaries=<T>` 出图 → 解 `iCCP` → 读 `rXYZ/gXYZ/bXYZ/wtpt/chad/chrm`（16.16 定点） | 见 §3-N1/N2 |
| ⑤ 本地标准常数 | `tools\src\jpegli\third_party\lcms\src\cmswtpnt.c`、`tools\src\libultrahdr\lib\include\ultrahdr\icc.h`、`tools\src\libjxl\lib\jxl\cms\color_encoding_cms.h` | 见各条证据 |

探针新鲜度核验（不是靠 mtime 猜）：①打印的判据文案与 HEAD 源文件 `tests\color\Modes\colormathagainstzimg.cs:82-83` 字面一致（`B 档=白点位数上限（Illuminant C）`／`（影院白 0.314,0.351）`），且打印的 `本仓=[1.4860,…]` 与③用 HEAD 常数重算的结果逐位相同 ⇒ 该 exe 编的是本轮读到的这套数学。exe mtime 2026-10-09 23:54 早于 HEAD 提交时刻，但 `git show --stat c18dc4a` 只动 CHANGELOG/docs/csproj，`ColorSpaceRegistry.cs` 自 2026-10-04 未变 ⇒ 对 A1 范围等价于 HEAD。**本轮没有重新构建**（构建会写 bin/obj，越出只读约束）。

---

## 0 一页结论

| 级 | 结论 | 标签 | 证据行 / 读数 |
|---|---|---|---|
| — | **旧主张 1/2/3/5 已修且修法正确**（四套 SD 原色分开、`smpte428` 摘出、重复对收敛到 1 对白名单、五格贴 zimg）；探针 pass=12 fail=0 | 已修-修法正确 | `ColorSpaceRegistry.cs:598-621`、①②③ |
| P2 | **色适应常数 `1.0633` 与全生态参照不一致，被误记成"白点位数上限"而锁进 B 档**：`smpte431` 的 2.36e-4 实差在把 `BradfordAdapt` 第三行末位换成 `1.0296` 后降到 **4.03e-6**（= zimg 5 位读数的舍入地板，即逐位吻合），白点仍是 RP 431-2 的 (0.314,0.351) 未动。`smpte428` 同理 1.0e-4→4.87e-6。ICC 比对端 `ColorantsD50("bt709")` 与本机 lcms 生成的 ICC 差 3.84e-4 → 同一改动降到 6.25e-6 | 真缺陷-精度（改法是**升位**，不涉降级授权） | `ColorSpaceRegistry.cs:808`、§3-D 表、`④` |
| P2 | **D50 PCS 白点在产品内有两种写法**：`IccD50Xy={0.34570,0.35850}` 推得 Z/Y=**0.825105**，而纯托管 ICC 写出端用 `D50Xyz={0.9642,1.0,0.8249}`（= ICC 规范 PCS 值）。⇒ `ColorantsD50`（识别/比对用）与 `IccProfileBuilder`（写盘）对同一空间差 **1.87e-4(bt709) / 2.08e-4(bt2020)**，且 `ColorantsD50` 的列和（=白点）永远不等于任何真实 ICC 的 `wtpt` | 真缺陷-精度 + 同一事实两个写者 | `ColorSpaceRegistry.cs:706` vs `IccProfileBuilder.cs:31`、§3-E |
| P2 | **GainMap 底图"表外原色 → 不映射且零日志"**：`gamutMtx==null` 只在 `wideBase` 分支出声，sRGB 底（产品默认）静默继续 ⇒ 正是禁止的"只改标签"。根因是 `LinearMatrixBetweenPrimaries` 的 null 混装三种语义（同 token／同值／未知），调用方无从区分。可达输入：`film`、`smpte428`、任何表外 token（CLI `--color-primaries film` 或素材自带标签） | 真缺陷-静默失效 | `GainMapEncoder.cs:137-147`、`ColorSpaceRegistry.cs:715-718`、`GainMapJpegCodec.cs:112-115` |
| P2 | **`smpte428` 的排除理由不成立**（登记错因，且错因被写进锁）：ST 428-1 原色 (1,0)(0,1)(0,0)+白 ⅓,⅓ 走本仓 `PrimariesToXyz` 时 `det(N)=1`（实测打印），RGB→XYZ 恰为单位阵、Y 行 = (0,1,0)（= XYZ 三角的签名），③ 复现 zimg 到 4.9e-6。⇒ "xy 参数化根本无法表达／归一化退化"为假；旧读数 max|Δ|1.99 的因是当时的**冒名 smpte431**，不是不可表达 | 文档过期（错因）+ 能力缺口（值仍缺，已 fail-closed） | `ColorSpaceRegistry.cs:623-632`、`tests\color\Modes\colormathagainstzimg.cs:104,107`、§3-G |
| P3 | **`Invert3x3` 奇异返回零矩阵，而公开包装的文档承诺"奇异返回 null"是假的**；`MatrixTo` 靠行和校验兜住，`PrimariesToXyz`／`LinearMatrix`／`LinearMatrixBetweenPrimaries` 三条路径没有兜 ⇒ 退化原色会静默产出全零矩阵，经 `GainMapEncoder:138` 只判 `!= null` 直接乘像素（全黑）。当前表内 9 格均非退化 ⇒ 潜伏 | 真缺陷-静默失效（潜伏，未测触发） | `ColorSpaceRegistry.cs:684-685` vs `:837-844`、`:785`、`:719-722` |
| P3 | **`ResolvePrimariesToken` 仍在做被点名禁止的反查**：按 `Spec.OutPrimaries` 遍历取首个匹配 ⇒ `"bt2020"` 命中 **DCI-P3** Spec（P3 原色 + DCI 影院白）。实测本次 grep **0 消费点**（同批正例对照 `ContainmentEpsilon` 有命中 ⇒ 非空断言） | 真缺陷-静默失效（潜伏，零消费点） | `ColorSpaceRegistry.cs:552-555`（对照注释 `:565-568`） |
| P3 | **别名表里有一处真猜测**：`GetPrimaries` 把裸 `"601"/"bt601"` 填成 `smpte170m`（=BT.601-525/SMPTE-C），而 BT.601 的 625 分支是另一套原色（EBU）。CLI 可直达（`NormalizePrimariesToken` 对未知 token 原样放行 → `ColorIntentFactory:266` → `FromCicpTokens`）。GUI 下拉只有 auto/bt709/bt2020/smpte432，不受影响 | 真缺陷-标准（轻，猜测式默认）／可达性=仅 CLI | `ColorSpaceRegistry.cs:652`、`MainWindow.xaml:437-444` |
| — | 正面：①原色表 9 格逐元素核过（见 §3-A 双锚表）；②**矩阵布局四处同形**（构造 `PrimariesToXyz`／乘序 `Mat3Mul`／执行 `SimdPixelOps`、`ColorKernels:843-845`／ICC 读写 `ReadIccColorants`、`DetectIccPrimaries`、`rXYZ=adapted[0,3,6]`、`BuildSf32` 行主序），无转置；③`chad` 方向 native→D50 与 lcms 一致；④`M⁻¹·L·M` 复合正确 | — | §3-B、§3-C |
| — | `film` 未收录 = 能力缺口、已 fail-closed，修法方向正确（残差 1.5e-4 在两种 CAT 下都不消失 ⇒ 真属 xy 位数）；`smpte428` 的"两锚冲突已被可表达性消解"这句**不成立**，冲突仍在（`iccgen` 给 428 的 `chrm` 是 (0.735,0.265)(0.274,0.718)(0.167,0.009)，与 zimg 的 XYZ 三角不同） | 能力缺口(已fail-closed) | `②` `film` 行、`④` |

---

## 1 方法（含我自己被推翻的三条）

做法：②/③/④ 三臂互不通告；先复算本仓常数（③用 HEAD 源文件里的 xy/白点逐字抄），再与 ②（外部实现）和 ④（外部 CMM 落盘值）比；⑤ 只用本地源码，不凭记忆下参照值。所有 ffmpeg 调用走管道，无产物落盘。

被推翻的假设（若不写出来，这三条会变成假陈述）：

1. **「lcms 与规范 Bradford 的第三行差异不影响结果」——错。** 我第一版按 `%.5f` 打印残差，四个 token 都是 `0.00000`，据此判定 CAT 变体不可区分、准备把 N1 撤掉。改用全精度后发现敏感度是 **1.0e-4～2.8e-4**，恰好就是 B 档残差的量级。`%.5f` 的显示精度把一条 P2 藏掉了 ⇒ 数值判据的打印位数本身是风险源。
2. **「`PrimariesXyD50` 不做 D50→D65 逆适应 ⇒ `ContainsGamut` 会把 sRGB⊂P3 判成不包含」——错。** 我实测到 `native=True / D50=False` 的翻转并准备定案为真缺陷；追下去发现那是**共享蓝原色**时叉积恰为 `0` 与 `-0.000000` 的符号假象：按代码实际使用的 `ContainmentEpsilon = 1e-6`（`ColorIntent.cs:146`），native 与 D50 两侧同判 `True`。⇒ 该自辩前提（射影变换保"点在三角形内"）在数学与实测两侧都成立，`ColorSpaceDescriptor.cs:254-257` 那条"不许用严格不等式"的注释正是这条的护栏，且它在起作用。
3. **「`colormathagainstzimg.cs` 的 zimg 参照表可能是抄本仓输出（循环论证）」——错。** 参照表里 `film`、`smpte428` 两行本仓返回 null（不可能被抄出来），而 ② 用本机 ffmpeg 独立测得这两行逐位等于表值；③ 又从标准 xy 独立复现全表 ⇒ 参照链非循环。

harness 事实（供后人复跑）：沙箱拒绝在仓库外写文件（`Error opening output …: Permission denied`），故 `②/④` 全部改 `pipe:0/pipe:1`；`②` 用的是 2026-10-07 的 ffmpeg，而 `COLOR_MATRIX_AUDIT_2026-09-24.md §2` 声明的是 2026-08-06 —— 两版 zimg 读数本次逐位相同，故跨版本可比。

---

## 2 旧主张复验（`docs/COLOR_MATRIX_AUDIT_2026-09-24.md §2`，当时 pass=6 fail=6）

| # | 旧主张 | HEAD 判定 | 现行行号证据 + 读数 |
|---|---|---|---|
| 1 | 4 个 SD token 与 `jedec-p22` 共用同一组常数（实差 0.052–0.461），根因是"同原色族"注释 | **已修，且修法正确** | `ColorSpaceRegistry.cs:605/608/610/614` 四格现为四套不同常数；:592-596 的旧注释已被改写成拆族理由。① 读数：`bt470m 1.76e-4 / bt470bg 3.38e-6 / smpte170m 4.91e-6 / smpte240m 4.91e-6`。③ 独立复算 4 格对 zimg 分别 1.1e-4／0／0／0。luma 那把独立尺（§3-A）：`bt470m` 只在配 Illuminant C 时才复现本机 `libjxl/metrics.cc:172` 写死的 0.299/0.587/0.114（换 D65 变 0.290/0.606/0.105）⇒ 白点判对；`bt470bg` 的 EBU 值本机无锚，故该格只靠 ①②③ 三锚 |
| 2 | `smpte428` 被写成 `smpte431` 的副本（实差 1.99） | **已不复存在（冒名已摘）**；但**排除理由错**，见 §3-G | `ColorSpaceRegistry.cs:623-632` 现在不收录该 token；① 打印 `smpte428→bt709 返回 null ⇒ 只允许出现在「故意不收录」名单里` = PASS。旧文档"实差 1.99"经我复算是冒名 smpte431 的后果（③ 用真 428 常数可做到 4.9e-6）⇒ 属**未复现**（旧读数成立但归因错） |
| 3 | "不同标准不得算成同一矩阵" 11 对违规 | **已修**：只剩 `smpte170m≡smpte240m` 1 对，且该对是合法的（两锚同判），已在白名单里点名 | ① 打印 `本仓把不同标准算成同一矩阵的 token 对：smpte170m≡smpte240m` + `PASS 同矩阵对全部落在白名单内（实得 1 对，白名单外 0 对）`。② 独立实测：zimg 的 170m 与 240m 两行逐位相同 ⇒ 这一对重复是外部事实不是本仓偷懒。白名单文本 `tests\color\Modes\colormathagainstzimg.cs:145-148` |
| 4 | `film` 未收录 = fail-closed（能力缺口，不是错值） | **仍成立，定性不变** | `ColorSpaceRegistry.cs:634-636`；① 打印 `film→bt709 返回 null …` = PASS。② 本机 zimg 有 `film` 行；③ 用 `film` 公布 xy 复算残差 1.3e-4，且在 1.0296/1.0633 两种 CAT 下**都不消失**（1.52e-4/1.57e-4）⇒ 这一格的 B 档理由（xy 位数）站得住 |
| 5 | 五格（bt2020/smpte432/smpte431/jedec-p22/bt709）与 zimg ≤2.4e-4 一致 | **已复验且更好**：三格 1e-6～5e-6；`smpte431` 名义 2.36e-4 但**不是水位**，是 §3-D 那个可一常数修掉的偏置 | ① 读数 `bt2020 1.14e-6 / smpte432 3.95e-6 / jedec-p22 4.92e-6 / smpte431 2.36e-4`；③ 用标准 xy 复算 `bt2020/smpte432/jedec-p22` 全为 0.00000（贴到 5 位小数舍入地板） |
| 附 | 审计 §0 第 2 条与 registry:624 都写 "CICP 10 = ST 428-1" | **未判定（索引归属没在本机钉死）** | 本机 `-f lavfi iccgen` 打印的自选值表是 `smpte428=10, smpte431=11, smpte432=12, film=8, bt2020=9, bt470m=4, bt470bg=5, smpte170m=6, smpte240m=7`；而 png 出口在 `-color_primaries <T> -color_trc -colorspace -color_range` 全给齐时**没写出 cICP**（11 个 token 全 `no-cICP`）⇒ 容器级 CICP 索引这条**未测**，不能据此判 registry 注释是错。需要：H.273 表 2 原文或一个能落盘 cICP 的出口。 |

---

## 3 新发现分层细节

### 3-A 原色表逐格核对（zimg 实测锚 + 外部实现写死的 luma 系数）

③ 用表内 xy/白点独立推导 `token→bt709`，与 ②（本机 zimg）比；再用"该空间自身 RGB→XYZ 的 Y 行 = 外部实现里写死的 luma 系数（本机 file:line）"作第二把独立尺（这把尺与 zimg 无关，能单独否证白点选择；找不到本机锚的格子明示"空转"，不拿记忆当锚）。

| token | ③ vs ② | Y 行（③） | 对照的 luma 锚（只认本机源码 file:line） | 判定 |
|---|---|---|---|---|
| `bt709` | 0.00000 | 0.21264 0.71517 0.07219 | `tools/src/libjxl/lib/jxl/dec_xyb.cc:192` = 0.2126/0.7152/0.0722 | ✅ |
| `bt2020` | 0.00000 | 0.26270 0.67800 0.05930 | `tools/src/libjxl/lib/extras/tone_mapping.cc:33` = 0.2627/0.6780/0.0593 | ✅ 逐位 |
| `smpte432` | 0.00000 | 0.22897 0.69174 0.07929 | `tools/src/libultrahdr/lib/src/gainmapmath.cpp:156` = 0.2289746/0.6917385/0.0792869 | ✅ 逐位 ⇒ **白点=D65 被独立证实** |
| `bt470m` | 1.76e-4（CAT 不敏感） | 0.29894 0.58663 0.11444 | `tools/src/libjxl/lib/extras/metrics.cc:172` = 0.299/0.587/0.114 | ✅ **Illuminant C 被独立证实**；反事实：同一原色配 D65 → 0.28966/0.60564/0.10470（偏离公布值 1e-2 量级）⇒ 白点选择有可算判据，不靠注释 |
| `bt470bg` | 0.00000 | 0.22200 0.70665 0.07134 | 本机未找到 EBU 0.222/0.707/0.071 的锚 ⇒ luma 尺**空转** | ①②③ 三锚一致（xy 与 zimg 逐位）；luma 那把尺未判定 |
| `smpte170m` | 0.00000 | 0.21238 0.70106 0.08656 | 本机无锚 ⇒ 空转 | ①②③ 一致；luma 未判定 |
| `smpte240m` | 0.00000 | 同 170m（逐位相同） | 与 170m 同 SMPTE-C ⇒ 合法重复 | ✅ |
| `smpte431` | 2.36e-4 → **4.03e-6**（改 CAT 后） | 0.20949 0.72160 0.06891 | 本机无 DCI 0.2094/0.7216/0.0689 的锚（我原本按记忆引用，已撤）⇒ 空转 | 原色/白点由 ①②③ 判为对（且 ② 与 ③ 只在换 CAT 后才逐位）；luma 未判定 |
| `jedec-p22` | 0.00000 | 0.23175 0.67225 0.09600 | 无锚 ⇒ 空转 | ①②③ 一致 |
| `film`（未收录） | 1.3~1.5e-4（CAT 不敏感） | 0.25359 0.67834 0.06808 | 无锚 | 能力缺口，B 档理由成立 |
| `smpte428`（未收录） | 1.0e-4 → **4.87e-6** | 0.00000 1.00000 0.00000 | —（Y 行 (0,1,0) 是"RGB≡XYZ"的几何签名，自洽非外锚） | 可表达且可精确（→§3-G） |

⇒ 四格（bt709 / bt2020 / smpte432 / bt470m）用**与 zimg 无关**的第二把尺（本机 libjxl / libultrahdr 里写死的 luma 系数）单独钉住，其中 `bt470m` 的 C-vs-D65 与 `smpte432` 的 D65 都是被这条否证过的 ⇒ 旧主张 1 的"白点/原色填对了吗"这一问，答案是"填对了"，且有独立判据。

表内原文（回读确认属于 `PrimariesTable`，非别处）：

```csharp
// ColorSpaceRegistry.cs:610-614
["smpte170m"] = new() { Token = "smpte170m", R = new[] { 0.630, 0.340 }, G = new[] { 0.310, 0.595 }, B = new[] { 0.155, 0.070 }, W = D65 },
// SMPTE 240M：**与 smpte170m 同原色（SMPTE-C），差异只在传递函数** — 附录 A 与 zimg 两锚同判这一点。
// ⚠ 这是本表**唯一一对合法的重复**（FIX_PLAN P1 的"同矩阵白名单"预期只含这一对）；
//   白名单外出现重复即意味着有新的两格被压成同一组常数，必须报错。
["smpte240m"] = new() { Token = "smpte240m", R = new[] { 0.630, 0.340 }, G = new[] { 0.310, 0.595 }, B = new[] { 0.155, 0.070 }, W = D65 },
```

### 3-B 矩阵布局一致性（构造／乘序／执行／ICC 读写四处，全部行主序 × 列向量，无转置）

```csharp
// IccProfileService.cs:164-166（读 ICC 判色域：got[通道][XYZ] 对 m[XYZ][通道]）
                for (int c = 0; c < 3; c++)
                    for (int k = 0; k < 3; k++)
                        err = Math.Max(err, Math.Abs(got[c * 3 + k] - m[k * 3 + c]));   // got[通道][XYZ] vs m[XYZ][通道]
```
```csharp
// IccProfileBuilder.cs:137-139（写 ICC：rXYZ/gXYZ/bXYZ = 行主序矩阵的三列）
                var tagRXyz = BuildXyz(adapted[0], adapted[3], adapted[6]);
                var tagGXyz = BuildXyz(adapted[1], adapted[4], adapted[7]);
                var tagBXyz = BuildXyz(adapted[2], adapted[5], adapted[8]);
```
```csharp
// SimdPixelOps.cs:275-277（执行端）      /  ColorKernels.cs:843-845（参考实现，同式）
rgba[i]     = m0 * r + m1 * g + m2 * b;
rgba[i + 1] = m3 * r + m4 * g + m5 * b;
rgba[i + 2] = m6 * r + m7 * g + m8 * b;
```
`BuildSf32`（`IccProfileBuilder.cs:237-241`）按 `m[0..8]` 顺序落盘 = 行主序，与 ICC 对 `sf32` 的规定同形 ✅。自洽性另测得一条硬证据：`chad · W_native = D50Xyz`，即写出的 `rXYZ+gXYZ+bXYZ` 列和恒等于 `wtpt`（0.9642,1,0.8249）⇒ 纯托管写出的 ICC 内部自洽，`COLOR_MATRIX_AUDIT_2026-09-24 §4.2` 挂着的"chad 方向待核"这一条**可判已定：方向 native→D50 正确**，与 lcms 同向（`cmswtpnt.c:260` `_cmsAdaptationMatrix(&Bradford, NULL, &Dn, cmsD50_XYZ())`、`cmsvirt.c:148`）。白点常数的本地锚：`tools/src/libjxl/lib/jxl/cms/color_encoding_cms.h:444-457` 给出 kD65=(0.3127,0.3290)、kDCI=(0.314,0.351)、kE=(1/3,1/3) ⇒ 表里 `smpte431` 的影院白与 428 该用的 E 白都是本机可查的公布值，不是记忆。

### 3-C 色适应与求逆的退化行为

- 复合顺序正确：`return Mat3Mul(Minv, LaM); // M⁻¹ · L · M`（`:812-814`），且 `L=diag(coneD/coneS)`（`:811`）在 `srcW==dstW` 时精确给单位阵 ⇒ 同白点不引入误差。
- **无守卫的退化入口**（潜伏，本轮未测到产品可达输入）：`:811` 的除法不查 `coneS[k]==0`；`XyzFromXy`（`:776-777`）不查 `xy[1]==0`；`PrimariesToXyz` 的 `var invN = Invert3x3(N);`（`:785`）不查 `invN` 是否为奇异返回的全零 ⇒ 任一环节给退化常数，`LinearMatrixBetweenPrimaries` 会返回全零或 NaN 矩阵，而唯一会拦它的是 `ColorSpaceDescriptor.MatrixTo` 的行和校验（`:179-183`），`GainMapEncoder`/`RawGamutFit.LoadPair` 都不过这一道。
- `InvertMatrix` 的文档与实现分叉：

```csharp
// ColorSpaceRegistry.cs:684-685
/// <summary>3x3 求逆（行主序）；奇异矩阵返回 null。供引擎组合 ICC 矩阵用。</summary>
public static double[]? InvertMatrix(double[] m9) => m9 != null && m9.Length == 9 ? Invert3x3(m9) : null;
// ColorSpaceRegistry.cs:844
double inv = Math.Abs(det) < 1e-12 ? 0 : 1.0 / det;      // ⇒ 奇异返回**全零矩阵**，不是 null
```
⇒ `MatrixTo` 里 `if (inv == null) return null;`（`:173-174`）对奇异是死分支，实际兜底的是后面的行和判据。数值条件另记：阈值 1e-12 是**绝对值**，`det` 落在 1e-12～1e-8 区间时返回 1e8 量级矩阵，行和判据（5e-3）能拦下大部分，但这条依赖"矩阵是白点归一色度矩阵"这一隐含前提，`Invert3x3` 本身没有该前提。

### 3-D N1（P2）CAT 常数被误记为"B 档白点位数上限"

```csharp
// ColorSpaceRegistry.cs:806-808
internal static double[] BradfordAdapt(double[] srcW, double[] dstW)
{
    var M = new[] { 0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0633 };
```

区分两种解释的读数（同一推导，只换末位；② 是本机 zimg 实测，故不是拿记忆当锚）：

| 输入常数 | 结果 vs zimg（`smpte431`） | vs zimg（`smpte428`） | vs 本机 lcms ICC 的 `rXYZ/gXYZ/bXYZ`（bt709） | vs lcms `chad` |
|---|---|---|---|---|
| 现状：xy 同、W=(0.314,0.351)、row3=**1.0633** | **2.361e-4** | 1.000e-4 | 3.84e-4 | 5.10e-4 |
| 只把 row3 换 **1.0296** | **4.03e-6**（逐位吻合） | **4.87e-6**（逐位吻合） | 1.86e-4 | 5.3e-5 |

⇒ 定案：`smpte431` 的 2.3e-4 与"白点位数"**无关**（RP 431-2 的影院白就只公布到 0.314,0.351，没有更多位可取），是 CAT 常数。`COLOR_MATRIX_AUDIT` B 档口径（`ColorSpaceRegistry.cs:616`、`colormathagainstzimg.cs:68-72`）对 `smpte431` 这一格是**错的归因**，对 `bt470m/film` 两格仍成立（两格换 CAT 都不降）。
外部参照（本地源码，非记忆）：`cmswtpnt.c:238-242` 与 `cmsps2.c:1193`（lcms 的 Bradford 末位 **1.0296**）、`libultrahdr/icc.h:125-132` 的 `kSRGB`（"taken from skcms"）—— 我的推导在 row3=1.0296 时给 `kSRGB` **1.38e-5**、给 lcms 落盘值 **6.25e-6**；row3=1.0633 时分别 2.16e-4 / 3.84e-4。⇒ 两个要读/比的 CMM 都站在 1.0296 一侧。
是否算"改标签/降级"：不算。这是把实现提到 zimg+lcms+skcms 的水位，B 档两格（431 与将来的 428）可升 A 档；`bt470m` 留 B 档。**未测**的一点：改完对现有 54/73 条门禁的连锁（`RawGamutFit.M2020To709` 派生自 D65↔D65，CAT 不参与，预期不动；但 `DetectIccPrimaries`/`RefineKnownSpace` 的 2e-3 预算会变宽 → 需要一次回归）。

### 3-E N2（P2）D50 白点在产品内有两种表示

```csharp
// ColorSpaceRegistry.cs:701-706
    var adapt = BradfordAdapt(XyzFromXy(d.W), XyzFromXy(IccD50Xy));
    return Mat3Mul(adapt, PrimariesToXyz(d.R, d.G, d.B, d.W));
}
// ICC PCS 白点 D50（xy）
private static readonly double[] IccD50Xy = { 0.34570, 0.35850 };
// IccProfileBuilder.cs:31
private static readonly double[] D50Xyz = { 0.9642, 1.0, 0.8249 };
```
`XyzFromXy({0.34570,0.35850})` = `(0.964296, 1.0, 0.825105)`，与 ICC 规范 PCS 的 `(0.9642, 1.0, 0.8249)` 差 **2.06e-4**（`Z/Y` 一项）。后果实测：`ColorantsD50` 与 `IccProfileBuilder` 对同一空间的色度矩阵差 **1.87e-4 (bt709) / 2.08e-4 (bt2020)**；`ColorantsD50` 的列和恒为 0.964296，而它要比的真实 ICC 的 `wtpt` 是 0.9642。可达性：`ColorantsD50` 有 4 个产品消费点（`ColorSpaceDescriptor.cs:80`、`ColorSpaceIdentify.cs:64`、`IccProfileService.cs:161`、`ColorScheduler.cs:159`）；`IccProfileBuilder` 走 ProPhoto 保真路径（`IccProfileService.cs:427,459-460`）。
定性：不违反标准（两种 D50 写法都在业界出现），属**无意义精度损失 + 同一事实两个写者**；修法方向 = 统一成 ICC 规范 PCS 三元组（或把 `IccPcsWhiteXy` 换成能表示它的 xy = (0.345703, 0.358539)，注意现值 y 少了 3.9e-5 就复现不出 0.8249）。**注意 `IccPcsWhiteXy` 的类型选择（xy）本身就复现不了规范 PCS 三元组**，这是这条分叉的成因。

### 3-F N3（P2）表外原色 → sRGB 底图静默"只改标签"

```csharp
// GainMapEncoder.cs:137 / 138 / 144 / 146（节选，中间像素调用与日志省略）
var gamutMtx = ColorSpaceRegistry.LinearMatrixBetweenPrimaries(sourcePrimaries, baseTok);
if (gamutMtx != null)
else if (wideBase)
    log?.Invoke($"[GainMap] ⚠️ 未能求出 {sourcePrimaries ?? "(未知)"} → {baseTok} 矩阵，底图将不映射（仅改标签，存在色偏风险）\n");
```
`baseGamut` 默认 `"srgb"`（`:86`）⇒ 默认档走的是**不打印**那一支。输入来源是原样透传的探测标签：

```csharp
// GainMapJpegCodec.cs:112-115
p.SrcPrim = !string.IsNullOrWhiteSpace(meta.colorPrimaries)
            && meta.colorPrimaries != "unspecified" && meta.colorPrimaries != "unknown"
    ? meta.colorPrimaries!
    : (ColorSpaceRegistry.FfmpegTokens(meta.sourceGamutName).primaries ?? "bt709");
```
⇒ **SD 素材（601 系标签）确实会流进 GainMap 底图映射**：`bt470bg`/`smpte170m`/`bt470m` 现在都算得出正确矩阵（旧版本这里是拿 P22 乘像素，即 §2 主张 1 的活体面）；`film`/`smpte428`/其它表外标签则落进静默支。
根因是 null 的语义混装：

```csharp
// ColorSpaceRegistry.cs:715-719
if (string.Equals(fromToken, toToken, StringComparison.OrdinalIgnoreCase)) return null;
…
if (f == null || t == null) return null;
if (f == t) return null;                                    // 同一原色（别名）→ 无需映射
```
三种 null（无需映射／表外未知／解析失败）不可区分 ⇒ 调用方只能全当"无需映射"。方向：把结果分三态（或让调用方对 `sourcePrimaries` 先过 `GetPrimaries` 判在表内），并且 sRGB 底图支也要出声。

### 3-G N4（P2）`smpte428` 的排除理由与写进锁的错因

```csharp
// ColorSpaceRegistry.cs:625-627
//   蓝原色 y=0 ⇒ PrimariesToXyz() 的归一化退化，**xy 参数化根本无法表达它**——
//   实测贴不上 zimg（max|Δ| 1.9，即 ~199% 满量程），第二套独立推导同样失败，
//   故 FIX_PLAN P1 定案：这一格不存在"该填什么值"，只存在"不填"。
```
实测否证（②/③，非推断）：以 (1,0)(0,1)(0,0)+白(1/3,1/3) 代入 `PrimariesToXyz` ⇒ `det(N)=1.0`（不退化）、RGB→XYZ = 单位阵、Y 行 = (0,1,0)；再接 `M⁻¹·L·M` 到 D65、乘 `inv(bt709)` ⇒ 与 zimg 实测差 **4.87e-6**（5 位小数逐位吻合）。旧读数 1.99 的因是当时 428 被抄成 431。同一句错因被固化进断言文案：

```csharp
// tests\color\Modes\colormathagainstzimg.cs:103-104（节选）
["film"] = "未收录（精度不足）",
["smpte428"] = "xy 不可表达 ⇒ 故意不收录，不许挑一组「看着像」的补回来",
```
⇒ 若照原样"补回来"会被这条判红。**但**：④ 实测 `iccgen=color_primaries=smpte428` 写出的 `chrm` 是 `(0.735,0.265)(0.274,0.718)(0.167,0.009)`（**不是** XYZ 三角），与 ② 冲突 ⇒ 收录前必须先用 ST 428-1 原文/H.273 表 2 裁决取哪一组，**这条我判"未判定(需要标准原文)"**，只把"排除理由是假的"定案。另：428 的 luma 若按 lcms 那组 `chrm` 会得到非 (0,1,0) 的 Y 行，与"RGB≡XYZ"的定义矛盾 ⇒ 我倾向 zimg 一侧，但不以倾向当结论。

### 3-H 归一化表 / 猜测式默认（只做原色与矩阵侧，容差归 A4）

- **"最接近理论值吸附"** 1 处：`ColorSpaceIdentify.cs:69-73` `KnownSpaceTol = 2e-3`（:32）命中后 `d.RgbToXyzD50 = best;`（采用理论值覆盖度量值）。原色侧影响：吸附后 `:79-80` 用 `GetPrimaries(bestToken)` 回填 `Rp/Gp/Bp/Wp`，即**标签决定原生色度**，D50 侧则来自 `ColorantsD50`（受 N1/N2 偏置）。容差合不合身是 A4 的事；我这边的事实是：N1+N2 已吃掉 2e-3 预算里约 **1/4～1/5**（3.84e-4 + D50 表示差），把这两条修掉可释放到 6e-6。
- **默认值填掉 token 落空**：`NormalizePrimariesToken`（`:423-435`）与 `NormalizeMatrixToken` 对未知值**原样返回**（不猜）✅；`ColorSpaceIdentify.NormalizePrimaries`（`:177-181`）把空/unspecified 填 `bt709`、`NormalizeTransfer` 填 `iec61966-2-1`，但同时把置信度降到 `CodecDefault`（`:147-152`）⇒ 有置信度闸门，属已登记的惯例假定不是静默猜。唯一真猜测 = §0 里的 `"601"/"bt601" → smpte170m`（`:652`）。
- 旧文档记过的"整幅提亮"事故（`SrcCurve ?? TransferCurve.Linear`）：已被显式短路修掉（`ColorTransformPlan.cs:386` 的成因注释 + `:400-410` 的 `if (Action is ColorAction.CarryIcc or ColorAction.None)` → 同曲线+无矩阵的恒等 spec），但 `:413 SrcCurve = SrcCurve ?? TransferCurve.Linear,` 这个猜测默认**仍在**，对 Map 类计划依赖"PlanCore 一定填了 SrcCurve/DstCurve"这一句注释保证 ⇒ **未判定**，需要：构造一条非 Carry/None 且 `SrcCurve==null` 的计划，看是否真能静默按 linear 解码。
- `iccgen` 侧观察（外部工具，非本仓数学）：本机 `ffmpeg -h filter=iccgen` 打印的 `color_primaries` 取值表是 `bt470m=4 / bt470bg=5 / smpte170m=6 / smpte240m=7 / film=8 / smpte428=10 / smpte431=11`，与 CICP 存在整体错位；但**按名字**解析是正确的（④ 实测 `bt470m` 得 NTSC 的 `chrm`、`bt470bg` 得 EBU 的 `chrm`、`ebu3213` 与 `jedec-p22` 落盘值逐位相同 ⇒ registry `:653` 的 `["p22" or "ebu3213"] → jedec-p22` 别名**被实测证实**）。产品只传名字（`IccProfileService.cs:353`：`zscale=…:p={primaries}:t={trc}:m={matrix},iccgen`，`iccgen` 无参数）⇒ 不受影响；只有把数字原样拼进去才会错。

---

## 4 未证边界（本轮判不了的，写清缺哪条证据）

1. **H.273 表 2 的索引归属**（428 是 10 还是 11、431 反之）与 `ST 428-1` 的原色公布值：本机 png 出口 11 个 token 全部 **没写出 cICP**（判据 `no-cICP`），`iccgen` 的数字表又不是 CICP 编号 ⇒ 缺标准原文或一个可落盘 cICP 的出口。旧文档"实差 1.99 / CICP 10"两句都因此只能标未判定。
2. **zimg 的 CAT 常数**：我只能证明"用 1.0296 复现 zimg 逐位、用 1.0633 差 2.3e-4"（②/③），zimg 源码不在本机（`tools/src` 无 zimg，`find` 只命中测试脚本）⇒ 常数出处是**行为反推**，不是读到源码。补法：拉 zimg `src/zimg/colorspace/` 或把残差表贴进 FIX_PLAN 附录 A 由维护者裁。
3. **`smpte170m` / `jedec-p22` 的 luma 独立尺**：①②③ 三锚互证 xy 一致，但没找到 BT.601-525 与 JEDEC P22 公布的 luma 系数，"Y 行 = 公布值"这把尺对这两格空转 ⇒ 未判定。
4. **改 CAT/D50 常数的连锁影响**：`RawGamutFit.M2020To709` 声称与 `LinearMatrixBetweenPrimaries` 同源（`RawGamutFit.cs:61-69`），D65↔D65 不含适应项，预期不动；但 `DetectIccPrimaries`（`IccProfileService.cs:161-170`）与 `RefineKnownSpace` 的匹配裕度会变 ⇒ 未测（本轮不跑整轮门禁）。
5. **`LoadPair` 的 `catch {}`**（`RawGamutFit.cs:98`）把注册表异常咽掉退回后备常数，且后备与派生值历史上差 2.7e-4（文件自述 `:64-65`）⇒ 若 N1 落地而后备常数不更新，"判据与产物脱钩"会以 4e-4 复活。**未测**：本轮没有构造注册表抛异常的场景。
6. **`-color-primaries film` 走 CLI 到出口标注的完整链**：我只核到 `ValidateCicpTriple`（`:734` 的 `knownPrim` 名单外只告警不拦）与 GUI 下拉不含 SD/表外 token；"表外 primaries 在 jxl/avif 出口是否会造成像素-标签失配"属 A2/A4 的裁决层，需要一次端到端读数。
