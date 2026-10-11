# A4 · 等价判定与容差审查（lane A4，只读）

被审对象：HEAD = `c18dc4a`，工作树干净。范围文件：
`src/FfmpegGui/Services/ColorMapping/ColorSpaceIdentify.cs`(193) · `ColorIntent.cs`(258，`EquivalenceTolerances`) ·
`ColorStrategyPlanning.cs`(1102) · `ColorTransformPlan.cs`(1072) · `QueueProcessor.cs` 的 S1-carry/None 委托点。
实跑：`tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe verdict` → **pass=41 fail=0**（唯一一次实跑；DLL 构建 10-09 23:54，
`find src -name '*.cs' -newer <该 DLL>` 为空 ⇒ 二进制与 HEAD 源同步）。未改任何源文件、无 git 写操作、未做变异验证（需改源）。

---

## 0 一页结论

| # | 结论 | 标签 | 证据强度 |
|---|---|---|---|
| C1 | 旧 §4.5 第 6 条**在 HEAD 仍然成立**：`MatrixToIdentity`/`MatrixElements`/`CurveParams`/`Chromaticity` 消费点 = **0**；`ContainmentEpsilon`=2、`MinConfidenceForMapping`=4(src)+1(test)。该类注释仍写"禁止散落魔数"，且"probe 有专测"对 4 个常量不成立 | 文档过期 + 真缺陷-标准(口径) | grep 计数，正例已自证 |
| C2 | 旧 §4.5 第 2 条**仍成立且更严重**：`KnownSpaceTol = 2e-3` 的吸附（:73）会把下游 1e-5 等价判定变成**由构造保证为真**——凡被吸附进已知空间的 ICC，其矩阵与理论值逐元素相等（同一 `ColorantsD50(key)` 现算），1e-5 那把尺**失去分辨力**。仍无任何偏移夹具 | 真缺陷-标准（静默判同） | 读码 + 构造链闭合；偏移夹具**未测** |
| C3 | 旧 §0 第 5 条：委托点搬到 `QueueProcessor.cs:1375-1398`，保真判据仍只认 ProPhoto（`IccProfileBuilder.cs:110-114`）。本次新增三段**静态**证据说明该矛盾在 HEAD 可达（详见 §3-N1/N2），但"产物是否真被归一"仍需端到端回读 | 未判定(需端到端产物色度回读) + 真缺陷-静默失效(检测器无调用点) | 静态链完整，交付侧事实未取 |
| C4 | 旧 HANDOVER 疑似死分支：HEAD 行号 = `ColorStrategyPlanning.cs:738-748`。可判定 **741-747 永不可达**，738-740 只在 `SrgbRef == null` 时可达（而 `SrgbRef` 可静态证明非 null）⇒ 整块是死代码 + 过时注释，**不是现行缺陷** | 文档过期（"未做变异验证"的悬案可按约束矛盾定案） | 前置条件全枚举，见 §2-4 |
| C5 | **新**：不变量检测器 `FindPixelColorEntry`（专门抓"计划说不改像素、命令行却有像素色彩入口"）**在生产代码里零调用点**，只被两个测试用**手搓命令串**测过 | 真缺陷-静默失效（守卫存在但没接线） | grep 计数 + 正例自证 |
| C6 | **新**：`S1` 走 carry 时日志宣称"源 ICC 由后置步骤嵌入"，而后置步骤的门条件是 `EffectiveColorStrategy==CarryIcc`（声明 S1 时恒为 `Recommended`）⇒ 生产日志与生产代码互相矛盾 | 真缺陷-静默失效 | 两处原文对撞，见 N2 |
| C7 | **新**：表曲线不参与等价（`ReferenceEquals(Lut, other.Lut)`）⇒ 数值与 sRGB 相同的 curv 表 ICC 恒被判"要改像素"，多做一次重采样 | 真缺陷-精度（可证的最小损失）/ 能力缺口 | 读码；实测 LSB 影响**未测** |
| C8 | **新**：吸附的二阶传导——命名一旦落到具名空间，legacy 归一链按**注册表理论常数 + 名义传递函数**重算像素（实测曲线被丢弃）⇒ 同一输入在两管线口径不同 | 真缺陷-精度 | 读码 |
| C9 | **新**：`Kind ⇔ Degradations.Count>0` 这条不变量是**构造恒等**（`ProjectVerdict` 用同一个 `degs` 现算 Kind），它抓不到"漏登记"；真正有牙的是 per-Code 正/负控对（verdict 41/0 实测） | 门禁侧：构造恒等型断言 | 实跑 |
| C10 | **新**：两处 `catch` 吞异常会让**裁决本身静默消失**（`Decision.cs:572` 退回声明值、`:625` 返回 null ⇒ 契约 Reject 不生效） | 真缺陷-静默失效 | 读码；异常可达性**未测** |

一句话给维护者：**这一层的"尺"不止不统一，而且最松的那把（识别层 2e-3）会把后面两把紧尺（1e-5 / 1e-9）的输入抹平**；
配套的检测器（`FindPixelColorEntry`）已经写好了却没有接进生产链路。

---

## 1 方法与被推翻的假设

**方法**
1. 计数一律用 `grep -rn <常量名> --include=*.cs src/ tests/` 并**先对一个已知存在的串做正例命中**：
   `grep -c ChromEqual src/…/ColorTransformPlan.cs` = **1**（只有定义），同一命令对 `MatrixIsIdentity` = **2**（定义 :940 + 调用 :727）
   ⇒ "零引用"的判定成立，且证明管道能抓到调用点。
2. `KnownSpaceTol` 在 `tests/` 全目录命中 **0**；同一 grep 模式对 `PrimariesToken ==` 命中 `tests/core/Modes/plan.cs:148`
   ⇒ "无偏移夹具"的判定不是搜索假阴性。
3. 可达性判定用**前置条件全枚举 + 约束矛盾**（§2-4），不用"看起来进不去"。
4. 只跑 `verdict` 一条探针模式；`verify-*.ps1` 整轮未跑（TMP/TEMP 已指向 `tests/output/pipeline-audit-2026-10-10`）。

**本次被推翻/纠正的假设（含我自己中途的错判）**
- 我最初推断"Map 计划从不写 `ColorLoss` ⇒ 映射计划会自称 `ColorLoss=None`"——**错**。`ColorStrategyPlanning.cs:359` 统一调用
  `ClassifyColorLoss`（:1086-1101），Map 支会得 `RoundTripQuantization`/`Clipping`/`GamutCompression`/`ToneMapped`。该假设撤回。
- 我最初怀疑 `ToSpec()` 把 carry/None 短路成 `SrcCurve=DstCurve=Srgb` 会引入 sRGB 往返重量化——**错**。
  `TransformSpec.LinearScale/EncodeScale` 字段默认就是 `1.0`（`ColorKernels.cs:26,36`），故恒等 spec 满足
  `IsIdentity`（:86-91），`TransformRgba` 在 :305 `if (spec.IsIdentity) return;` 直接跳过。该短路是正确的。
- 旧 §4.5 的原文行号归属有误（文档过期）：它写 "`ColorTransformPlan.cs:760` 的 `1e-9`、`:208` 的 `1e-5`"。
  HEAD 上 `1e-9` 在 `ColorTransformPlan.cs:943`；而 `ColorTransformPlan.cs:208` 是 `public int OutBitDepth = 16;`，
  `1e-5` 实际在 **`ColorSpaceDescriptor.cs:208`**。两处旧引用现在都对不上。
- HANDOVER 那条"疑似死分支"的方向要反过来读：不是"整块 598-607 可能永不命中"，而是**块内只有那条 `Reject` 可能命中**，
  真正承载 GIF 结局的 741-747 已被 :696 的钉目标取代（见 §2-4）。

---

## 2 旧主张复验表

### 2-1 `EquivalenceTolerances` 四常量死 ⇒ **仍成立**（标签：真缺陷-标准(口径失控的最小可证形式) + 文档过期）

HEAD 逐个常量的消费点（src 与 tests 分列）：

| 常量 | 声明 | src 消费点 | tests 消费点 | 实际在用的同语义尺 |
|---|---|---|---|---|
| `MatrixToIdentity = 1e-5` | `ColorIntent.cs:138` | **0** | **0** | `ColorTransformPlan.cs:943` 用的是 **1e-9**（值也不同） |
| `MatrixElements = 1e-5` | `:140` | **0** | **0** | `ColorSpaceDescriptor.cs:208` 内联 `1e-5`（值同、引用断） |
| `CurveParams = 1e-4` | `:142` | **0** | **0** | `TransferCurve.cs:367` 内联 `const double tol = 1e-4` |
| `Chromaticity = 1e-4` | `:144` | **0** | **0** | `ColorTransformPlan.cs:968` 的 `ChromEqual` —— **该函数本身零引用**（见 N4） |
| `ContainmentEpsilon = 1e-6` | `:146` | 2（`ColorSpaceDescriptor.cs:245`、`ColorTransformPlan.cs:962`） | 0 | — |
| `MinConfidenceForMapping` | `:148` | 4（`ColorStrategyPlanning.cs:504/514/544/718`） | 1（`tests/core/Modes/wire.cs:606`、`tests/ServiceProbe/Program.cs:3734`） | — |

```
ColorIntent.cs:134  /// <summary>等价判定阈值集中处（规划 §5′.2）；probe 有专测，禁止散落魔数。</summary>
ColorIntent.cs:138  public const double MatrixToIdentity = 1e-5;
ColorIntent.cs:140  public const double MatrixElements = 1e-5;
```
"probe 有专测"这一句：`EquivalenceTolerances` 在 tests 里只被 `MinConfidenceForMapping` 引用过一次（枚举序锁），
四个死常量**没有任何专测** ⇒ 注释与现实不符（文档过期）。

**自由度交代（防"失控"滥用）**：本节判的不是"等价判定失控"，而是"同一语义判据被 6 个不同量级的阈值分别实现，
且集中声明处零引用"。可变操作数 ≥ 3：①阈值（1e-12 / 1e-9 / 1e-6 / 1e-5 / 1e-4 / 2e-3 / 5e-3）②比较对象
（与理论值 / 与对侧描述符 / 与单位阵 / 与行和）③生效阶段（识别层→规划层→内核层）。三个轴都能逐格点名（§3-N3、§3-N5）。

### 2-2 `ColorSpaceIdentify.cs:32/73` 吸附 ⇒ **仍吸附，且比旧文说的更糟**（标签：真缺陷-标准）

```
ColorSpaceIdentify.cs:31  /// <summary>色度矩阵匹配容差（ICC 为 16.16 定点，各家实现取整差异 <1e-3）。</summary>
ColorSpaceIdentify.cs:32  private const double KnownSpaceTol = 2e-3;
ColorSpaceIdentify.cs:72      d.Name = $"{bestLabel} (ICC 识别, {curve.Name}, err={bestErr:E1})";
ColorSpaceIdentify.cs:73      d.RgbToXyzD50 = best;                                          // 采用理论值，消除定点取整噪声
```
HEAD 复验三件事：
1. **仍吸附**：`FromIccFile`(:47) 每次都调 `RefineKnownSpace`，`best` 来自 `ColorSpaceRegistry.ColorantsD50(key)`（:64）——现算、非缓存，
   与 `ColorSpaceDescriptor.FromCicp` 的 `ColorantsD50(primariesToken)`（`ColorSpaceDescriptor.cs:80`）是**同一个函数同一组常数**。
   ⇒ 吸附后 `Equivalent(other)` 的逐元素差 ≈ 1e-17，`ColorSpaceDescriptor.cs:208` 那把 1e-5 的尺**不可能失配**。
   换句话说：`IsSrgbEquivalent()`（`ColorStrategyPlanning.cs:881-882`）对 ICC 源是"吸不吸附"的二值问题，不是精度问题。
2. **传递性方向**：`Equivalent` 本身对称（逐元素绝对差 + `Kind` 相等），但**类结构是"两边各自与理论值对齐"**，
   不是"两边互相比"。于是 A≡T、B≡T ⇒ A≡B，而原始 |A−B| 可达 4e-3（=2×2e-3），是 1e-5 的 400 倍。
   ⇒ 判同的**实际边界是 2e-3，不是 1e-5**，且日志里除 `err=` 文本外没有任何结构化痕迹（`ToLogLine()` 在生产零调用点，见 N6）。
3. **无夹具**：`KnownSpaceTol` 在 `tests/` 全目录零命中；正例对照 `PrimariesToken ==` 命中 `tests/core/Modes/plan.cs:148`。
   现有唯一相关断言是**同向**的（"标准 ICC 必须能归入已知空间"），没有任何"偏移 ICC 必须报差异"的反向格 ⇒ 见 §4-B2。
4. 注释自证不足：`16.16 定点`的量化步长是 1/65536 ≈ **1.5e-5**，注释却写"取整差异 <1e-3"并把尺放到 **2e-3**。
   真实分歧来自各家 D50 白点/适应矩阵选择（量级 1e-4），2e-3 无出处。

### 2-3 旧 §0 第 5 条（CarryIcc/None 交回 ffmpeg）⇒ **委托点已迁、判据未改，仍未定案**（标签：未判定(需 X 证据)）

- 委托点 HEAD：`QueueProcessor.cs:1375-1398`（旧 `1126-1131` 已失效）。
  ```
  QueueProcessor.cs:1377  && (plan.Action == ColorMapping.ColorAction.None
  QueueProcessor.cs:1378      || plan.Action == ColorMapping.ColorAction.CarryIcc))
  QueueProcessor.cs:1396      Log("[色彩引擎] 计划为直通（不改像素）⇒ 本次由 ffmpeg 完成重编码\n");
  QueueProcessor.cs:1397      return (false, 0, plan);
  ```
- 保真判据 HEAD：`IccProfileBuilder.cs:110-114` 仍是 `return s?.Key == "ProPhoto RGB";`（旧 `:113` 命中，行号未变）。
- **本次新增的静态可达性**（不是定案，只是把"能不能到达"钉死）：
  `EffectiveColorStrategy` 对声明值非 S2 的任务**直接返回声明值**（`FfmpegCommandBuilder.Decision.cs:548`
  `if (declared != Models.ColorStrategy.CarryIcc) return declared;`）⇒ `preserveSource`(:164) 为假 ⇒
  D1 超广归一块（`Decision.cs:393`，条件含 `!preserveSource`、`!d.Faithful`）对 **S1 + Adobe RGB 源 ICC** 成立，
  `IsUltraWide("Adobe RGB")`（`ColorSpaceRegistry.cs:200`，`NeedsBt2020Normalize = Kind==UltraWide`，:42）为真 ⇒ 往命令行塞像素链。
  同一格规划层给出的却是 `Action=CarryIcc / ColorLoss=None`（`ColorStrategyPlanning.cs:706-714`）。
- **仍不能定案的部分**：产物像素/色度是否真的被改、源 ICC 是否真的丢——需要端到端回读（配方见 §4-B1）。
  现有端到端锁 `verify-decision-delivery.ps1` ⑬(:580-615) 用的是 **Display P3 源 `a2_srgb_p3.png`**，
  P3 非超广 ⇒ D1 永不触发 ⇒ 该格**不在争议操作数上**（正例：`CK ($ps -ge 99.0) "⑬ auto 目标（CarryIcc，像素零改动）…"`）。

### 2-4 HANDOVER 疑似死分支 ⇒ **可按约束矛盾定案：741-747 不可达**（标签：文档过期；非现行缺陷）

HEAD 行号：`ColorStrategyPlanning.cs:738-748`（HANDOVER 写 `:598-607`、`PlanRecommended:556` ⇒ 现 `:696`）。

进入该分支的全部前置（逐条给出行号）：
1. `!srcHdr`（:735）；2. `it.Target == null`（:735）；3. `!canCarrySourceIcc`（:735）；4. `!expressible`（:735）；
5. `!caps.HasColorPath`（:738）。
反证链：
- `caps` 在 :651 绑定为 `pol.Format`，`HasColorPath`(:738) 与 :696 读的是**同一实例同一计算属性**（`ColorTransformPlan.cs:120`），696→738 之间无任何对 `caps`/`pol.Format` 的写入；
- :696 `if (!caps.HasColorPath && it.Target == null && SrgbRef != null) it.Target = SrgbRef;` 之后直到 :735 没有任何语句把 `it.Target` 置回 null（:700-727 只读、只 return）；
- `SrgbRef`（:19-20 `ColorSpaceDescriptor.FromCicp("bt709","iec61966-2-1","sRGB")`）非空可静态证明：`FromCicp` 的三个 fail-closed 出口
  （`ColorSpaceDescriptor.cs:77/79/81`）对 `GetPrimaries("bt709")`（表内 :598）、`ForCicpTransfer("iec61966-2-1")`（:387 → Srgb）、
  `ColorantsD50("bt709")`（:697-703）都取不到 null。
⇒ 结论：**`it.Target==null ∧ !caps.HasColorPath` 与 `SrgbRef != null` 三者不能同时成立**。
所以 :738 唯一可达条件是 `SrgbRef == null`，而那时 :740 立刻 `Reject` 返回 ⇒ **:741-747（把源映射到 sRGB 并无标注输出）永不可达**。
可达性判据的自由度 = 3（`SrgbRef` 非空性、696→735 之间 `it.Target` 的变更、`caps` 实例稳定性），已逐条排除。

补充两条实测/搜索读数：
- 无任何断言匹配 :745 的产物文案：`grep -rn "无任何色彩管理通路" tests/` 只命中 `contract.cs:204` / `verdict.cs:227` 的 **HasColorPath 断言与注释**，不含 :745 那句 Reason ⇒ 删除 :741-747 不会让任何现有断言变红（**未做变异验证，不可改源**）。
- GIF 那格的现行结局确实由活路径承担：`verdict.cs:227` 用例（"S1 推荐 + sRGB（带 ICC）+ GIF ⇒ 映射到 sRGB 工作空间、无标注输出"）走的是 :696 钉目标 + `PlanCore` + `EnsureAnnotationNotSilentlyMissing`(:790)，与死块无关。
⇒ 对 HANDOVER 的答复：**可结办**，定性为"死代码 + :547-557/:589-607 那两处互相引用的过时注释"，不是缺陷。

---

## 3 新发现

### N1 检测器写好了、没接线：`FindPixelColorEntry` 生产零调用点（标签：真缺陷-静默失效）

`ColorTransformPlan.cs:888` 定义的正是本 lane 想要的守卫：
```
ColorTransformPlan.cs:914  bool noTransform = p.Action is ColorAction.None or ColorAction.CarryIcc or ColorAction.Reject;
ColorTransformPlan.cs:925  if (noTransform && pixelEntries > 0)
ColorTransformPlan.cs:926      return $"计划为 {p.Action}（不改像素），但命令行含像素色彩入口 [{first}]";
```
调用点计数：`grep -rn FindPixelColorEntry src/ tests/ServiceProbe/Program.cs tests/core tests/color tests/scripts`
⇒ src 内**只有定义行**；调用全部在 `tests/ServiceProbe/Program.cs:2073-2085` 与 `tests/core/Modes/contract.cs:624-636`，
且入参是**手搓字符串**（`contract.cs:623`：`string zs = "-vf format=gbrpf32le,zscale=…"`），从未喂进**真实构建的命令行**。
正例自证：同一 grep 里 `pH1/pCarry` 等调用命中 ⇒ 管道能看见调用点。
后果：§2-3 那格（S1-carry + D1 zscale）在生产里**没有任何一处会比较"计划宣称"与"实际命令行"**；
`QueueProcessor.cs:1403-1409` 的"标注一致性守卫"只比 `OutPrimaries`，对 carry 计划的空标签直接跳过。

### N2 生产日志与生产代码互相矛盾：carry 的源 ICC 到底谁嵌（标签：真缺陷-静默失效）

宣称侧：
```
QueueProcessor.cs:1393  if (plan.Action == ColorMapping.ColorAction.CarryIcc)
QueueProcessor.cs:1394      Log("[色彩引擎] 保真路径：像素零改动；源 ICC 由后置步骤嵌入"
```
执行侧（同一文件）：
```
QueueProcessor.cs:1972  bool effCarry = FfmpegCommandBuilder.EffectiveColorStrategy(item.Options, item.InputPath)
QueueProcessor.cs:1973                  == Models.ColorStrategy.CarryIcc;
QueueProcessor.cs:1984  if (protectColorMetadata && effCarry && (!userSpecifiedColor || containerDropsIcc))
QueueProcessor.cs:1986      var iccExit = await ExifToolService.CopyIccProfileAsync(item.InputPath, outputPath, …
```
`effCarry` 只看**声明策略**（`Decision.cs:548` 对非 S2 早退），而 S1 的 L1 携带计划（`ColorStrategyPlanning.cs:706-714`）
`EffectiveStrategy` 仍是 `Recommended` ⇒ **S1-carry 走进 :1394 的"由后置步骤嵌入"文案，却在 :1984 被门挡掉**。
`Degradations` 也救不了：④`NoIccCarrier` 的判据是 `!p.AttachIcc`（`ColorStrategyPlanning.cs:250`），而 carry 计划恰好 `AttachIcc=true` ⇒ 恒不登记。
操作数：①声明策略(S1/S2) ②容器(编码器是否落盘 ICC) ③计划 `AttachIcc` —— ≥3，且每一支都能独立翻转 ⇒ 可判。
唯一未知是"ffmpeg 自己会不会把 iCCP 抄过去"（PNG 有可能）⇒ 归入 §4-B1 端到端一起定案。

### N3 表曲线不参与等价：恒等被判"要改像素"（标签：真缺陷-精度 / 能力缺口）

```
TransferCurve.cs:364  public bool Equivalent(TransferCurve other)
TransferCurve.cs:366      if (Kind != other.Kind) return false;
TransferCurve.cs:373      CurveKind.Table => ReferenceEquals(Lut, other.Lut),      // 表曲线仅当同一实例时视为等价
```
ICC 的 `curv` 多值表 → `TransferCurve.FromTable`（`IccProfileService.cs:271`），`Kind=Table`；
与解析式 sRGB 描述符比较时 `Kind` 就不同 ⇒ `ColorSpaceDescriptor.SameCurveAs`(:192) 假 ⇒ `PlanCore` 置 `NeedsCurveChange=true`
（`ColorTransformPlan.cs:705`）⇒ 走 Map、出口量化。矩阵部分此时往往已被 N5/§2-2 吸附成逐元素相等。
"三要素同一把尺"的回答：**不是**。矩阵 1e-5、曲线参数 1e-4（:367）、表曲线**无容差概念（要么同实例要么不等价）**、
色度 1e-4（死函数）。同一个"是否等价"问题上有四种互不通约的算法。
现有测试**把这条例外当成正确**来断言（因此永远不会因为它变红）：
`tests/core/Modes/plan.cs:149-150` `Check(d2.Curve.Equivalent(…Srgb) || d2.Curve.Kind == …CurveKind.Table, …)`。
最小损失量级（表→解析往返 ±1 LSB@8bit）**未测**。

### N4 `ChromEqual` 零引用：第四把尺子写在没人走的地方（标签：真缺陷-标准(纪律) / 死代码）

```
ColorTransformPlan.cs:967  private static bool ChromEqual(double[] a, double[] b)
ColorTransformPlan.cs:968      => Math.Abs(a[0] - b[0]) < 1e-4 && Math.Abs(a[1] - b[1]) < 1e-4;
```
`grep -c ChromEqual` 在该文件 = 1（仅定义）；对照 `MatrixIsIdentity` = 2（定义 :940 + 调用 :727）⇒ 判定可靠。
含义：`EquivalenceTolerances.Chromaticity = 1e-4`（"原色色度 xy 逐项容差 ⇒ 判为同原色"）**既没有常量消费者，也没有函数消费者**
——色度维度在当前实现里从不单独参与等价判定（实际参与的是 :208 的 D50 矩阵比较）。
这比 §2-1 更进一层：死的不是常量，是**这一整个判据维度**（想按色度判同空间的意图没有实现）。

### N5 吸附的二阶传导：命名一旦确立，legacy 用理论常数+名义曲线重算像素（标签：真缺陷-精度）

`ColorSpaceIdentify.cs:73` 只吸附矩阵，但下游把**实测曲线也丢了**：
```
ColorSpaceRegistry.cs:219  public static string? BuildUltraWideToBt2020Filter(string? displayName, bool hdr, bool hlg, bool allowApproximateCurve = true)
ColorSpaceRegistry.cs:221      var spec = Resolve(displayName);
ColorSpaceRegistry.cs:223      var encIn = ResolveInputCurve(displayName, allowApproximateCurve);
ColorSpaceRegistry.cs:225      var m = LinearMatrix(displayName, "BT.2020");   // 线性域 primaries 矩阵(与传递无关)
```
调用点 `Decision.cs:409` 没传 `allowApproximateCurve`（默认 **true**，仅"新引擎传 false"的注释在 :217-218 上方）。
⇒ 一个被吸附成 "Adobe RGB" 的自定义 ICC，其像素按**注册表 Adobe 常数 + 名义 γ2.2** 归一到 BT.2020；
进程内引擎那条（`PlanCore` 用描述符的实测 `Curve` + 吸附后的矩阵）**口径不同**。
"还有哪些格子采用理论值"的清单（除 :73）：
- `ColorSpaceIdentify.cs:80` `if (def != null) { d.Rp = def.R; d.Gp = def.G; d.Bp = def.B; d.Wp = def.W; }` —— 实测色度坐标被理论值整组替换；
- `ColorSpaceIdentify.cs:84-85` 具名分支同上；
- `ColorSpaceRegistry.cs:223/:225`（上式，名义曲线+理论矩阵）；
- 反例（保留实测，值得记）：`ColorSpaceIdentify.cs:55` 注释"曲线保持 ICC 原样"—— 吸附**不碰曲线**，这正是 N3 与 N5 分叉的根源。

### N6 `Kind ⇔ Degradations` 是构造恒等；`err=` 诊断无出口（标签：门禁侧·构造恒等 + 能力缺口）

```
ColorStrategyPlanning.cs:93  Kind = p.Action == ColorAction.Reject
ColorStrategyPlanning.cs:95      : degs.Count > 0 ? VerdictKind.ProceedWithDegradation : VerdictKind.Proceed,
ColorStrategyPlanning.cs:61  /// 即 <c>Kind == Proceed</c> ⇔ <c>Degradations.Count == 0</c>（自洽性由 <c>ServiceProbe verdict</c> 断言）。
```
Kind 与 Degradations 由**同一个 `degs` 现算**，所以那条"自洽性"断言在数学上不可能失配——它验证的是投影函数自身的定义，
**不能**发现"该登记却没登记"。实跑 `verdict` 的 224 格自洽（`PASS verdict 自洽性：224 格（产出 148 含降级 63、拒绝 76）零违规`）
因此属于恒绿；有牙的是同一模式里 7 组**正向/负控成对**断言（换容器/换目标后 Code 必须消失），这部分我跑过、全绿。
旁路检查（Reject 返回空表）：`Reject` 助手只把 `Alternatives` 暂存进 `p.Verdict`（:835），
`ProjectVerdict` 对 Reject 走 `degs`（`CollectDegradations:119` 对 Reject 直接返回空）⇒ 无"拒绝却登记损失"的旁路，方向正确。
`err=` 唯一载体是 `ColorSpaceIdentify.cs:72` 的 `d.Name`，而 `ColorTransformPlan.ToLogLine()`（:333）**在生产代码零调用点**
（仅 `tests/ServiceProbe/Program.cs:1342`、`tests/core/Modes/plan.cs:54` 打印）⇒ 吸附误差在生产日志里看不见。

### N7 两处吞异常会让裁决本身消失（标签：真缺陷-静默失效；只数色彩/GainMap 相关）

```
FfmpegCommandBuilder.Decision.cs:572  catch { eff = declared; }        // EffectiveColorStrategy
FfmpegCommandBuilder.Decision.cs:625  catch { return null; }           // ResolveLegacyVerdictPlan
```
:572 的降级方向 = 退回**声明值** ⇒ S2 被覆盖成映射的场景会按"携带"发命令行（`preserveSource=true`）而计划其实是映射；
:625 的降级方向 = 返回 null ⇒ `QueueProcessor.cs:1038-1064` 的 `legacyPlan/legacyVerdict` 双双为 null，
`legacyReject` 恒 false ⇒ **契约 Reject 在传统管线上不再生效，且日志里连 `[色彩裁决]` 都不出现**
（:1048 `if (legacyVerdict != null && engPlan == null)` 是唯一打印入口）。两处都不写日志、不带异常上下文。
`ColorIntentFactory.cs:75 / :117` 的两个 `catch { /* … */ }` 方向是 fail-safe（退回标签路径），记为**已文档化的可接受降级**，不计入缺陷。
异常实际可达性：**未测**（需注入一次装配异常，或造一个会让 `Plan` 抛的输入）。

### N8 猜测式默认 `?? "bt709" / ?? "iec61966-2-1"`（标签：能力缺口，非现行缺陷）

```
ColorStrategyPlanning.cs:856  var d = ColorSpaceDescriptor.FromCicp(m.Primaries ?? "bt709", m.Transfer ?? "iec61966-2-1", m.Space);
ColorStrategyPlanning.cs:857  if (d != null) { d.Confidence = DescriptorConfidence.NamedIcc; return d; }
```
`m.Primaries != null || m.Transfer != null` 即进这一支 ⇒ 用户只给 primaries 时**默认按 sRGB 曲线**建目标，
同时置信度**抬到 NamedIcc**（高于闸门 `MinConfidenceForMapping`）。缺失那一半没有任何日志说明"是补的"。
缓解：`ManualIntent.ToString()`(:129-131) 会把 `-` 打出来。判为能力缺口/可见性不足，不是错值。

### N9 门禁侧现有断言清单（这一层现在被谁看着）

| 断言 | 位置 | 有没有牙 |
|---|---|---|
| `ICC(tag) 归入已知空间 token=` | `tests/core/Modes/plan.cs:148`（`ServiceProbe/Program.cs:1450` 同段） | 有（反向：偏移过大→token=null→红），但**只测同向**（生成标准 ICC 必须归入） |
| `ICC({tag}) 与 CICP({prim}) 实质同一空间（≤1e-3）` | `tests/core/Modes/plan.cs:162` | **无牙/构造恒等**：矩阵在 :73 已被赋成同一个 `ColorantsD50` 值，复合必为单位阵（误差 ~1e-17）；真不归入时上一行已先红 |
| `d2.Curve.Equivalent(Srgb) \|\| Kind==Table` | `plan.cs:149-150` | **无牙**：把 N3 的例外写成了合取 |
| 恒等 spec 必须是恒等 | `ServiceProbe/Program.cs:3539-3546` | 有（原文注明"把短路删掉 ⇒ 0.6 会变成 0.798 ⇒ 转红"），但用的是 `TransformPixelReference`（参考算子），**不是**生产表驱动内核 |
| 8-bit 源降位必须恒等（16 相位 × 解析/表两条路） | `ServiceProbe/Program.cs:7134-7176` + 对照组 `7176-7194` | **有牙**（自带"零偏差也可能是没在比"的反证夹具）——本 lane 见到的最好的一条锁 |
| `Kind ⇔ Degradations` 自洽 224 格 | `verdict` 模式（实跑绿） | **无牙**（见 N6）；同段的 7 组 Code 正/负控**有牙** |
| `FindPixelColorEntry` C8 组 | `contract.cs:615-636` | 抓的是**手搓字符串**，非真实命令行（N1） |
| 枚举序锁 `MinConfidenceForMapping` | `tests/core/Modes/wire.cs:601-606` | 有（针对"恰好等于阈值"的定时炸弹，已登记于 `RAW2TIF_QA_DEFECT_2026-10-03.md`） |
| 端到端 ⑬ auto 目标逐像素相同 | `verify-decision-delivery.ps1:580-615` | 有，但操作数是 **P3**（非超广），不覆盖 §2-3 那格 |

---

## 4 未证边界（要什么证据才能定案）

- **B1 · 定 §2-3 / C3（S1-carry 被 legacy 归一）**：需要一次端到端，四格即可判：
  源 = 内嵌 **Adobe RGB ICC** 的 PNG（`--icc-file` 造的格不算，`ColorIntentFactory.cs:70` 显式把 `SourceIccPath` 置 null，carry 支不可达）；
  输出 = `png` 与 `jpg` 各一次（jpg 走 `fmtAllowsWide=false` 的 sRGB 支，`Decision.cs:415`），策略 = 默认 `recommended`。
  回读三件事：①`exiftool -a -s -ICC_Profile:ChromaticityChannel1` 与源逐值对照（源 ICC 是否还在）；
  ②`ffprobe` 出口 `color_primaries`；③**底图样本**与源解码的逐字节/psnr 对照。
  再加一条把 C5 钉死的静态/半静态锁：对**同一次执行实际使用的命令行**调 `FindPixelColorEntry(args, plan)`，非 carry 才允许。
  判定门槛：像素变 = 真缺陷-精度（且属"只改标签"的镜像：**只改像素却说没改**）；像素不变但 ICC 丢 = 真缺陷-标准；都不变 = 归**未复现**。
- **B2 · 定 §2-2 的实际危害**：需要"偏移量 1e-3 / 1.5e-3 / 2e-3 / 2.1e-3 的合成 ICC"四格夹具（旧 §4.5 与 FIX_PLAN 都点过名，HEAD 仍无）。
  断言方向应是：**落在 1e-3~2e-3 的偏移必须能在裁决里以结构化字段读出偏差值**（而不是只进 `d.Name` 的 `err=` 文本），
  并且必须能翻转一次等价判定（否则 1e-5 那把尺永远没有作用对象）。合成 ICC 需要写文件，本 lane 只读 ⇒ 未做。
- **B3 · N3 的损失量级**：需要"curv 表(TRC) = sRGB 数值"的 ICC → 目标 sRGB 的**显式目标**格，
  对比 `Action=Map` 的产物与直通产物在 8/16bit 出口的逐样本最大偏差；顺带把恒等 spec 的锁
  从 `TransformPixelReference` 换成生产内核 `TransformRgb48le`（表驱动路径在恒等 spec 下不做整体跳过：
  `ColorKernels.cs:305` 的 `IsIdentity` 短路只覆盖 `TransformRgba`，`TransformRgba8`(:326)/`TransformRgb48le`(:439,466) 无该判断）。
- **B4 · §2-4 的结办方式**：约束矛盾已成立，但"删掉 741-747 不会让任何断言变红"只有变异验证能定；本 lane 不可改源 ⇒ 记**静态定案 + 变异未做**。
- **B5 · N7**：需要一次异常注入（或一个能让 `ColorIntentFactory/Plan` 抛的输入），确认 `[色彩裁决]` 段确实整块消失、退出码不再被 91 拦住。
- **B6 · 未复现项**：`ColorTransformPlan.cs:922-924` 的"H2 置位反向自检"、`:913 wantsH1` 双映射检测 —— HEAD 上 H2 后端已删（`ColorTransformPlan.cs:800-817` 注释），
  两支在真实规划里都不可达；本轮**未复现**其现行后果，仅按 `2026-10-05` 的删除记录与注释归为潜伏自检。
