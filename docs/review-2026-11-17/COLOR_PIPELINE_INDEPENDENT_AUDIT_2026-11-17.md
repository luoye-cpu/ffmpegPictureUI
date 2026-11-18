# ffmpegPictureUI 全色彩管线独立审查（2026-11-17）

> 状态：审查已收口；确认 5 条 HIGH、1 条 MEDIUM；未覆盖项单列，不写成通过。
> 基线：`main@a3de42a`；版本位 `1.6.0-beta17`；本轮不改产品源码、不提交、不移动 tag、不出包。
> 当前构建：Release 成功，0 errors / 5 个存量 warnings。
> 被测产物：`FfmpegGui.dll=2222A3C1363F8BD3…`、`ServiceProbe.dll=2E800BEB79EFE261…`。
> 证据根：`tests/output/_tmp/color-audit-20261117-0eeb3a3d/`。

## 0. Findings 总表（供后续修复 agent 接手）

| ID | 严重度 | 问题 | 主要影响 | 建议修复方向 |
|---|---|---|---|---|
| F-01 | HIGH | 合法 LUT/A2B/B2A ICC 被当成“无 ICC” | 推荐路径静默改写色彩身份；carry 全拒绝；GainMap 第二入口也误按 sRGB | 区分 ICC 存在性与 matrix/TRC 可映射性；携带解耦；增加完整 CMS 慢路 |
| F-02 | HIGH | 显式 `--icc-file` 缺失或 LUT 不被支持时被静默忽略 | 用户显式输入不生效，缺文件也不 fail-closed | 显式路径错误硬失败；合法但不支持的 ICC 给独立拒绝原因或进入 CMS |
| F-03 | HIGH | LUT ICC 与 CICP 冲突时无警告，ICC 优先级被“可矩阵化”改变 | 原 ICC 被丢弃，按 CICP 重建色彩身份 | 先识别合法 ICC 并裁决冲突，再决定映射能力；冲突必须显式可见 |
| F-04 | HIGH | H1 Map 解码不消费 `--color-matrix` | 显式 YUV 输入矩阵纠偏无效，仍成功交付 | H1 复用传统链的矩阵覆盖与守卫；不能应用时阻塞显式 engine |
| F-05 | HIGH | RAW 临时 TIFF 路径使自产线性来源声明失效 | 线性 Rec.2020/Rec.709 被当作 sRGB；Map 与 None 均可产生错色 | 传结构化 RAW provenance，不靠当前文件扩展名猜来源；补真实队列测试 |
| F-06 | MEDIUM | exiftool 切换工作目录后仍使用相对源路径 | 合并探测失败；真实 CLI 后置元数据恢复警告并跳过；wire F-12 红 | 在 exiftool 公共入口统一归一化源/目标绝对路径，并补相对路径双用例 |

所有修复均仅为建议，本轮没有落地。详细证据、边界和未覆盖项见下文。

## 1. 审查目标与判定口径

本轮不采信历史报告、代码注释或“门禁曾经为绿”作为当前证明，重新核对：

- UI/CLI 选项到 `FfmpegOptions` 的真实映射；auto/engine/legacy 路由；
- ICC、CICP、EXIF 的识别与优先级；外部 ICC 指定；无标签与冲突标签；
- 自研 RGB 矩阵、白点适应、传递函数、HLG OOTF、RGB↔YUV、范围与位深量化；
- 推荐/携带/CICP-only/手动策略、色域与传递意图、降级和 fail-closed 边界；
- H1 像素内核、ffmpeg、Cjxl、AVIF、JXR、Jpegli、JPEG/PNG/WebP/TIFF/GIF/APNG 出口；
- 元数据恢复、ICC 后置、CICP 保留、oxipng 与 strip 的交互；
- 动画、RAW、GainMap、HDR、透明通道与高位深组合；
- 独立工具回读，以及不调用产品矩阵代码的数值 oracle。

组合结论分三类：**正确执行**（像素、标签和承诺一致）、**明确警告后继续**（损失已登记且可见）、**fail-closed**（明确拒绝且无错误产物）。重点排除“成功退出但错色/丢标签/错误降级”。

## 2. 当前源码重新核出的机制图

1. **路由**：`ColorEngineRouter` 决定 engine 请求与输出是否可编码。显式 engine 遇阻塞硬失败；auto 可回退传统管线。动画、DNG 和多数外部后端有专门阻塞/例外，不能用一个总开关推断所有格式。
2. **意图识别**：`ColorIntentFactory.FromOptions` 提取 ICC，再读取容器 CICP/EXIF。`ColorSpaceIdentify.FromIccFile` 只有在 RGB colorants 与三条曲线都能读出时才返回描述符；描述符不可用时退回 CICP，再退回惯例。
3. **规划**：`ColorIntentFactory.FromMetadata` 构造源/目标与策略，`ColorStrategyPlanning` 推荐或显式裁决；随后处理 ICC 版本、CICP-only、色彩损失与容器标注规则。部分携带路径会提前返回，必须单独验证其是否跳过后置规则。
4. **裁决**：`ColorVerdict.ApplyVerdict` 在真正执行前处理拒绝、降级、显式输出矩阵和 HDR/SDR 能力。
5. **执行**：`QueueProcessor` 在格式能力与引擎外另有传统 ffmpeg/外部编码器路径；H1 路径由 `ColorEncodingHelper` 调 `ColorKernels`，之后进入 PNG/JXL/AVIF/JXR/JPEG 等专用封装。
6. **后置**：元数据恢复与 ICC/CICP 后置在 `QueueProcessor` 中再次判断策略；PNG 还可能经 oxipng；AVIF 有 avifenc 与显式 ICC 特例。后置失败现在多为警告继续，不能把“进程成功”直接等同于色彩承诺兑现。
7. **GainMap 是第二套语义入口**：`GainMapJpegCodec.EncodeParams.Resolve` 自己读取 ICC/CICP 并构建底图/增益图参数；主色彩规划中的识别修复不会自动覆盖这条路径，需独立组合验证。

## 3. 已确认缺陷（动态证据已闭环）

### F-01｜HIGH｜合法 LUT/A2B/B2A ICC 被误报为“无 ICC”，携带全面拒绝、推荐路径改写色彩身份

**源码机制**：`ColorSpaceIdentify.FromIccFile` 把“能提取 matrix/TRC”与“存在合法 ICC”合并为一个返回值；`ColorIntentFactory` 在返回 null 后退回 CICP/惯例，警告文本变成 `source has no ICC/CICP color description`。显式 carry 又要求 `src != null`，于是合法 ICC 连“原样携带”入口都进不去。

**真实 profile，不是合成损坏样本**：

- `sRGB_ICC_v4_Appearance.icc`：合法 `mntr/RGB/Lab`，含 A2B0/A2B1/B2A0/B2A1，无 matrix/TRC；源 ICC SHA256=`eb7536f90e734be7…`。
- `MartiMaria_browsertest_A2B.icc`：合法 `scnr/RGB/XYZ`，含 A2B0/A2B1/A2B2；源 ICC SHA256=`4e6508181299faffe337…`。

**动态矩阵**（`cases/lut-icc/summary.json`）：

| 源 | 策略/出口 | 当前结果 | 独立回读 |
|---|---|---|---|
| 两类 LUT ICC | 显式 carry × PNG/JXL/AVIF/WebP | 全部 rc=1、引擎 exit 91、零产物 | 日志错误声称源无 ICC/CICP |
| 两类 LUT ICC | 推荐 × PNG | rc=0、308 B | 原 iCCP 消失，只写 sRGB cICP=1/13 |
| 两类 LUT ICC | 推荐 × JXL | rc=0、237 B | bt709/sRGB，无原 ICC |
| 两类 LUT ICC | 推荐 × AVIF | rc=0、2432 B | 两个不同源都得到同一份新生成的 sRGB ICC，SHA256=`9cb56d7db87edb30…` |
| LUT display ICC | 显式目标 Display P3 × PNG | rc=0 | 像素按“sRGB（惯例假定）”处理，源 LUT 未参与 |
| LUT display ICC + `--jpeg-gain-map true` → JPEG | rc=0，按 SDR 普通 JPEG 交付 | `GainMapJpegCodec` 也报“无 CICP 也无可用 ICC”，按 sRGB 解码；证明第二语义入口同样受影响 |

**判定**：不是“不支持完整 LUT 映射”本身必然构成缺陷，而是**存在性识别错误、错误警告、携带与映射能力耦合、默认路径静默替换色彩身份**共同构成缺陷。至少应区分 `ValidButUnsupportedByFastPath` 与 `Absent/Invalid`；不能继续把前者当后者。

### F-02｜HIGH｜显式 `--icc-file` 缺失或不能走 matrix/TRC 快路时被静默忽略

`ColorIntentFactory` 读取显式 ICC 失败后直接落回内嵌 ICC/CICP，最终可能走 sRGB 惯例；没有“用户指定文件被忽略”的独立错误或警告。

三组同目标 Display P3 的 PNG 输出逐字节相同：

1. 源内嵌合法 LUT display ICC，未指定外部 ICC；
2. 显式指定一个不存在的 `--icc-file`；
3. 显式指定合法 scanner A2B ICC。

三者均为 1393 B，SHA256=`2BF4F81D913CD417CDE9AE15CDDDCFCA9D9BCC5FEE33F612D19F70EC1F5AEC2D`，日志均把源写成 `sRGB (惯例假定)`。这同时证明：不存在的显式文件没有 fail-closed；合法 LUT 外部 ICC 没有真正参与源语义计算；三案没有被区分。

**边界建议（不是本轮已实施修复）**：显式路径不存在、不可读或 ICC 结构无效必须 fail-closed；合法但当前 CMS 不支持的 profile 必须给出独立拒绝原因，或进入完整 LUT/CMS 慢路，不能退回惯例继续。

### F-03｜HIGH｜LUT ICC 与 CICP 冲突时，ICC 是否优先取决于“能否矩阵化”，且无警告

构造源同时携带合法 LUT display ICC 与 P3 CICP。按正常优先级，ICC 是显式色彩数据，不能被静默抹掉；当前却因为 LUT 描述符返回 null 而只采用 P3 CICP。推荐 PNG 成功生成 P3 ICC，日志 `warnings=0`，原 LUT 被丢弃，用户无法知道发生了冲突和替换。

这不是 F-01 的简单重复：即使产品决定“暂不支持 LUT 映射”，也应先确认 ICC 存在并裁决 ICC/CICP 冲突；映射能力不足只能导致显式拒绝或进入 CMS 慢路，不能改变语义优先级。

### F-04｜HIGH｜H1 Map 分支不消费 `--color-matrix`，两个不同输入矩阵得到同一错误输出

同一 BT.601 像素、BT.709 声明的 MJPEG 夹具，分别指定 `--color-matrix bt470bg` 与 `bt709`：

| 路径/目标 | bt470bg 与 bt709 的结果 | 判定 |
|---|---|---|
| engine，目标 sRGB（Action=None，由 ffmpeg 重编码） | 两档输出不同；且逐档与 legacy 完全相同 | 正确：直接 ffmpeg 链的 `scale=in_color_matrix` 生效 |
| legacy，目标 Display P3 | 两档输出不同（`4B153BA0…` / `A4B5028E…`） | 正确：输入矩阵真实参与像素转换 |
| engine，目标 Display P3（Action=Map，H1） | 两档输出**逐字节相同**（均 `A7190016…`） | **错误：显式矩阵未参与 H1 解码** |

H1 日志明确显示解码命令只有 `-vf "format=rgb48le"`，没有 `scale=in_color_matrix`；计划文本虽写“无承载”，但队列只有源惯例警告，没有“用户矩阵被忽略”的 `[warn]`，也不 fail-closed。另一个口径矛盾是：直通路径实际应用了矩阵，决策文本仍写“无承载”。

**修复边界建议**：把 legacy 已验证的“仅用户显式矩阵 + 非 RGB 输入才插 `scale=in_color_matrix`”规则共享给 H1 解码；RGB 输入继续明确警告。若暂时不接，auto 遇 Map + 显式矩阵应回退 legacy，显式 engine 应拒绝，不能继续产出。直通/H1 两种执行形态必须使用同一份“是否已消费”真值，避免日志与事实相反。

### F-05｜HIGH｜RAW 自产线性中间件的来源判据在真实队列失效，整段色彩语义退回 sRGB

这是本轮影响面最广的已闭环问题之一。DNG 前置阶段明确执行：

- `dngtool -rdp -rdspace rec2020` 回读：`render=linear rec2020 16-bit (WB+profile applied)`；
- 队列写入 `ColorSourceDeclPrimaries=bt2020`、`ColorSourceDeclTrc=linear`，日志也明确声明；
- 但 `captured.InputPath` 随即被改成临时 `*_raw.tiff`，`TryRunColorEngineAsync(captured, captured.InputPath, …)` 把这个 TIFF 路径传给 `ColorIntentFactory`；
- 工厂的 `isSelfMadeIntermediate` 却用 `RawService.IsRawFile(inputPath)` 判断。临时 TIFF 不是 RAW 扩展名，因此刚写入的声明不被接受，最终打出 `source has no ICC/CICP … assuming sRGB`。

**像素闭环**：把同一 DNG 独立重建出的线性 Rec.2020 TIFF 直接作为无标签输入，再按 BT.2020/PQ 转换，所得 PNG 与完整 DNG 默认路由的解码像素完全相同：

`default_png_pixels.rgb48 = intermediate_png_pixels.rgb48 = E35438F423EFAD05E58FFAA200CC62192AED4907A0A35115A26EA857BDBA64F9`

这排除了“只是日志写错”：真实 DNG 路由与“完全不知道中间件是线性 Rec.2020”的路由逐值一致。

已观察到的两种错误形态：

1. **Map**：默认 PNG/AVIF/TIFF 将实际线性 Rec.2020（或 Rec.709）中间件当作 sRGB，再做矩阵/TRC 映射；输出 BT.2020/PQ 或 ICC 标签本身可以正确，但像素来源已错。
2. **None**：`--raw-color-target rec709` 的 JPEG 与默认 GIF 被判成“sRGB→sRGB”直通。ffmpeg 命令只有输入 `-color_trc linear` 声明，没有线性→sRGB 的像素变换；线性码值被直接当作 SDR 输出。

`--color-strategy carry` 的 RAW→TIFF 因源无 ICC/CICP 而 exit 91、零产物，属于明确 fail-closed，不另计缺陷。

**修复建议**：不要用“当前待编码文件扩展名”推断色彩来源。预处理时应随任务保存结构化的 `RawIntermediate` provenance（渲染空间、线性 TRC、原始 RAW 路径），识别层优先消费这份事实；执行层仍使用临时 TIFF。若沿用现有字段，至少把“是否自产中间件”作为显式布尔/来源对象传入 `FromOptions`，而不是让它再次猜路径。必须新增真实队列端到端断言：直接调用 `FromOptions(rawPath)` 的计划单测覆盖不到这个回归。

### F-06｜MEDIUM｜exiftool 子进程切换工作目录后仍使用相对源路径，导致合并读取与元数据恢复失败

`ExifToolService.ReadColorTagsOnceAsync` 把 `WorkingDirectory` 设为 exiftool 所在目录，但命令行仍传调用方原始的 `imagePath`。独立 net11 宿主复现：

- 绝对路径：合并 ICC 描述符两次均成功，`sRGB/bt709 (ICC curv[1024])`；
- 相对路径：合并描述符为 null，`exifReadFailed=True`，错误为 `File not found - tests/output/sources/src_photo.jpg`；旧两步法不切换工作目录，仍能成功。

这直接解释了 `ServiceProbe wire` 的 F-12 红（100/1），**不是 ICC 样本坏，也不是 `perl532.dll` 假红**。真实 CLI 对照进一步表明：

- 相对路径输入的 P3 PNG 仍能由上游归一化后正确识别，输出 P3 cICP，不能把它夸大为当前 CLI 错色；
- 但后置元数据恢复再次拿原始相对路径调用 exiftool，日志为 `File 'tests\…\p3_icc.png' does not exist for -tagsFromFile option`，随后 `[warn]` 跳过恢复，任务仍成功。

**修复建议**：所有 exiftool 公共入口在构造进程前统一 `Path.GetFullPath(imagePath)`（输出路径同理），不要依赖各调用点是否已归一化；同时保留当前“失败即警告、色彩保护模式不盲目回退”的边界。门禁应增加相对路径的识别与元数据恢复双用例。

## 4. 自研矩阵与“为什么不能矩阵化”的独立判断

### 4.1 已核到的数学结构

- `PrimariesToXyz` 由原色列矩阵与白点缩放推导 RGB→XYZ，结构正确。
- Bradford 使用 `M⁻¹·diag(ρdst/ρsrc)·M`，不是错误的对角阵直乘；D50→D65 确实进入白点适应。
- ProPhoto 使用 D50、gamma 1.8 与线性域 1/512 趾（编码域约 1/32）；sRGB 使用分段 EOTF/OETF；PQ/HLG、范围与 16-bit 量化有独立内核。
- 当前 `curve 44/0`、`matrix 15/0`、`colormath 19/0`、`ootf 11/0` 为绿；此外已用独立 NumPy oracle 完成小图解析路径与大图查表快路的逐值对照（不调用产品矩阵代码）。

**独立数值结果**（`cases/math-oracle/summary.json`，输出均先解码为原始 `rgb48le` 再比较）：

| 用例 | 像素规模/实际路径 | 独立参考 | 结果 |
|---|---|---|---|
| sRGB→Display P3 | 8×8，解析路径 | float64 矩阵 + 实际 float32 运算序 + sRGB 曲线 | 192/192 通道值零差 |
| Display P3→sRGB | 8×8，解析路径 | 同上，反向矩阵 | 192/192 通道值零差 |
| ProPhoto→sRGB | 8×8，解析路径 | 理论 ProPhoto 矩阵 + Bradford + 1.8/线性趾 | max=1 LSB，无一值 >1 LSB |
| ProPhoto→sRGB | 同上 | 独立解析 ICC `para3` 的实际定点参数，理论矩阵命中 | 192/192 通道值零差 |
| sRGB→Display P3 | 512×512，查表快路 | 独立重建 EOTF/OETF 表、float32 矩阵和线性域插值 | 786432/786432 通道值零差 |
| ProPhoto→sRGB | 512×512，查表快路 | 实际 ICC 曲线 + 独立查表快路参考 | 786432/786432 通道值零差 |

ProPhoto 的理想曲线趾点按**线性域 1/512**定义，对应编码域约 1/32；把编码域阈值误写成 1/512 会产生伪差异，本探针已纠正。实际 profile 的 `para3` 参数为 `g=1.8000030517578125, a=1, b=0, c=0.0625, d=0.03125`，所以与理想 1.8 公式存在 1 LSB 级差别是合理的；用实际参数则逐值一致。近已知 ICC 命中理论矩阵是有意的去噪策略，与原 ICC 的 s15Fixed16 colorants 不保证逐值相同，本轮不把它误判为矩阵错误。

#### 4.1.1 跨格式组合回读（已执行）

证据：`cases/combo/summary.json`。独立回读统一给 exiftool 子进程补 `exiftool_files` PATH，并重试已知的 `perl532.dll code 126`；早期一次 sRGB→JPEG 的 ICC 空读已重读推翻，不计产品缺陷。

| 组合 | 当前实测 | 裁决 |
|---|---|---|
| P3 CICP-only → TIFF / JPEG / WebP | 三格式均成功，ICC 描述均为 P3/sRGB，三者 ICC SHA256=`08C13713…` 完全一致 | 正确执行；旧的“提前返回跳过 ICC 生成”没有在这三格式复现 |
| sRGB CICP → JPEG carry | 成功，重读确认 sRGB ICC 存在 | 正确执行；首次空读是 exiftool 进程故障 |
| BT.2020 PQ / HLG → AVIF carry | 12-bit、full、RGB/identity，nclx 分别 `9/16/0` 与 `9/18/0`；无 ICC | 标签正确；本批尚未做 HDR 像素逐值参考 |
| 透明 ProPhoto ICC → PNG / AVIF carry | 两格式 ICC 均与源 `DF61C994…` 完全相同；AVIF 主图不写冲突 nclx | ICC/透明度容器语义正确；alpha 像素尚未逐值比较 |
| 未标注源 → PNG / AVIF / JXL / TIFF / GIF | 均有 `[warn]`；PNG/JXL/AVIF 为 sRGB 语义，TIFF 也生成 sRGB ICC，GIF 明确警告容器无法携带色彩描述 | 属于“警告继续”，不是静默；GIF 的像素仍按 sRGB 惯例 |
| 未标注源 + `--strip-metadata` → PNG | 普通元数据剥离，但 cICP `1/13/0/1` 保留 | 正确：颜色语义不随普通元数据被剥掉 |
| 合法 LUT ICC + P3 CICP 冲突源 → PNG | 无任何 `[warn]`；日志只采用 P3 CICP；输出写入新 P3 ICC，原 LUT 被丢弃 | **缺陷**：ICC 是否参与优先级竟取决于能否矩阵化，且冲突被静默处理 |
| P3 ICC APNG → APNG（推荐/carry） | 两路均保留 3 帧，ICC=`EBF95B6F…` 与源一致，cICP=`12/13/0/1` | 正确执行 |
| P3 CICP-only APNG → APNG | 保留 3 帧与 `12/13/0/1`，无 ICC | 正确执行 |
| APNG + 显式 engine；APNG → AVIF 16-bit 单帧出口 | 分别 exit 90 / exit 91，均零产物，明确 `AnimatedInput` | fail-closed，未静默丢帧；不把“尚未支持动画引擎/该 AVIF 出口”误记为色彩缺陷 |
| P3 ICC APNG → GIF | 保留 3 帧；日志明确位深、色域裁切、隐含 sRGB、无 ICC 载体等降级 | 明确警告继续 |
| DNG 默认 → PNG/AVIF/TIFF，Rec.709 JPEG/GIF | 标签可正确，但源自产线性声明失效；DNG 与无标签中间件像素完全相同 | **F-05，错误执行** |
| BT.2020 PQ AVIF + GainMap → JPEG/AVIF/JXL | JPEG 双图 MPF + sRGB ICC；AVIF 有 `tmap`；JXL `jxlinfo` 读出 `jhgm`、82 B 增益图 + 61 B 元数据；底图均为 sRGB | 结构/标签正确；HDR 重建像素尚未做独立参考 |
| SDR ProPhoto/LUT + GainMap → JPEG | 明确提示无 HDR 增益，按普通 JPEG 输出；ProPhoto 使用具名矩阵，LUT 则误按 sRGB | ProPhoto 为明确警告继续；LUT 归入 F-01 |
| exiftool 缺失/缺 `perl5*.dll` | 推荐 PNG/JXL 明确两次 `[warn]` 后按 sRGB 惯例；JPEG 由 ffmpeg 保留源 P3 ICC；显式 carry（含 PNG→JXL）exit 91、零产物；CICP-only 不依赖 ICC 读取 | 符合“允许惯例但运行中提示”的既定策略；仍有误识别风险，但不是静默 |
| 相对路径 P3 PNG → PNG | 色彩识别正确，P3 cICP 保留；后置 exiftool 因相对路径报文件不存在，元数据恢复跳过 | **F-06，警告继续但丢普通元数据** |

同一批另有两条门禁口径需要分开裁决：

- `verdict UnlabeledAssumedSrgb` 仍报 `S2 × sRGB × jpg` 未登记降级码；但该格式组合实际已有 sRGB ICC 和“sRGB 标签源”警告，不能再简单按“未标注”解释，需修判据或给出更精确的产品语义证明。
- `engine` 在隔离重跑中由并发时的 19/4 变为 **35/0**，先前 4 条不计产品缺陷。`decision` 在隔离中仍 185.2 s 超时，尚未完成，不能写成通过或产品正确性失败。
- `verify-color-wiring` 的 5 条 JXR 位深/alpha 红是**测试回读环境假红**：同批产物在补 `exiftool_files` PATH 后分别读回 24-bit BGR、48-bit RGB、32-bit BGRA、64-bit RGBA，全部符合期望；不能记成产品位深或 alpha 缺陷。
- 顶层 `verify-decision-delivery` 已隔离完成 **84/0（6 SKIP）**，`verify-prophoto-faithful` **42/0**；内部 `ServiceProbe decision` 的 180 s 超时与前者不是同一项，仍需单独长时复跑。

### 4.2 不是“矩阵不够强”，而是对象类型不同

matrix/TRC profile 的表达是：每通道一维解码曲线 → 线性 RGB → 一个 3×3 矩阵 → PCS。LUT profile 可以包含 A2B/B2A 多维表、CLUT、Lab/XYZ PCS、跨通道耦合、设备校准、渲染意图与色域压缩。**一般不存在与整条 LUT 管线等价的单个 3×3 矩阵**；增加矩阵常数或强行拟合只能近似某个样本集，不能通用解决。

合理架构是：

- 保留 matrix/TRC 快路；
- 对 LUT/A2B/B2A 增加完整解释器或成熟 CMS（例如 lcms2）慢路；
- 把“原样携带 ICC”与“能否映射像素”解耦；
- 无法映射时明确拒绝或按明确策略携带，不得改写成无标签 sRGB；
- HDR、渲染意图、黑点和色域压缩必须成为 CMS 调用参数，不能只把 LUT 结果塞进现有 sRGB 规划后宣称等价。

本轮只确认缺陷与技术方向，未获授权实施完整 CMS。

## 5. 已完成的静态/服务探针读数

| 探针/门禁 | 当前读数 | 说明 |
|---|---:|---|
| contract | 164/0 | 服务级契约 |
| curve | 44/0 | 传递函数 |
| matrix | 15/0 | 服务级矩阵 |
| colormath | 19/0 | 色彩数学 |
| ootf | 11/0 | HLG OOTF |
| hlgwire | 3/0 | HLG 外部链路 |
| plan | 39/0 | 规划 |
| selftest | 40/0，1 SKIP | 引擎自检；SKIP 为执行路径构造限制，不算完整覆盖 |
| iccname | 17/0 | 命名空间 |
| verify-color-caps | 16/0 | 格式能力 |
| ServiceProbe engine | 35/0 | 隔离复跑推翻并发时 19/4，不计产品回归 |
| ServiceProbe verdict | 41/1 | `S2×sRGB×JPEG` 判据与“实际生成 sRGB ICC”冲突，源码与动态结果更支持判据过期 |
| ServiceProbe decision | 185.2 s 超时 | 未完成，不能写成通过或直接归因为色彩缺陷；与 decision-delivery 不是同一项 |
| ServiceProbe wire | 100/1 | F-12 已由独立宿主定位为相对路径 + 工作目录切换，见 F-06 |
| verify-decision-delivery | 84/0，6 SKIP | 隔离完成 |
| verify-prophoto-faithful | 42/0 | 隔离完成 |
| verify-color-engine-xcheck | 全通过，白名单豁免 2 格 | 隔离完成 |
| verify-color-wiring | 157/5 | 5 条均为 JXR 回读缺 exiftool PATH 的假红；同批产物补 PATH 后 24/48/32/64-bit 与 alpha 全符合预期 |
| verify-gainmap-isobmff | 173/1，9 SKIP | 唯一红为 JPEG 负控素材前提自检，尚未裁决为产品回归 |
| thumbnail / bench | 33/7；36/1 | 非色彩正确性主结论；bench 为多核扩展比 3.7× 未达 4× 阈值 |

这些读数绑定本文件顶部 DLL 指纹；完整隔离日志见 `logs/gates-isolated.log`，F-12 的相对/绝对路径复现见 `logs/f12-host-run*.log`。

## 6. 组合矩阵进度

| 组合域 | 状态 |
|---|---|
| 真实 LUT ICC × carry/推荐/显式目标 × PNG/JXL/AVIF/WebP | 已完成，见 F-01 |
| 外部 ICC：不存在/合法 LUT/合法 matrix | 缺失与 LUT 已确认异常；合法 matrix 正控已纳入同一探针 |
| CICP-only P3/sRGB → TIFF/JPEG/WebP | 已完成；P3 ICC 三格式一致，sRGB JPEG 重读正确 |
| PQ/HLG → AVIF，显式 HDR 目标 | carry 已完成，标签正确；显式映射与 HDR 数值 oracle 未闭环 |
| 透明 ProPhoto → AVIF/PNG | 容器/ICC 已完成；alpha 像素未逐值比较 |
| LUT+CICP 冲突、无标签源、strip | 已完成；冲突静默丢弃 LUT 为缺陷，其余见 4.1.1 |
| sRGB↔P3、ProPhoto D50→sRGB D65 独立数值 oracle | 小图解析与大图查表均完成，实际 ICC 曲线口径下逐值零差 |
| APNG/动画 | P3 ICC/CICP、推荐/carry、GIF、静态 PNG/WebP 及不支持出口的拒绝路径已回读；多帧转单帧未单列为色彩缺陷 |
| RAW | 真实 DNG、默认/显式 Rec.709/carry、Map/None 及独立像素等价对照已完成，见 F-05 |
| GainMap | PQ→JPEG/AVIF/JXL 的结构与标签、SDR ProPhoto/LUT 对照已完成；HDR 重建像素未做独立参考 |
| exiftool 缺失/破损 | PNG/JPEG/JXL/CICP 的推荐/carry 已完成；原始附件对应的“破损 exiftool + PNG→JXL”已单独重跑 |
| 相对路径 | 绝对/相对 CLI 对照与独立 DLL 探针完成，见 F-06 |
| 全部外部编码器 | 本报告没有宣称穷尽；已完成项仅限上文逐格点名的出口 |

## 7. 当前未裁决与未覆盖项

1. PQ/HLG 显式映射、透明 ProPhoto alpha、GainMap HDR 重建尚未完成逐值像素参考；当前只能确认路由、结构和标签，不能扩写为像素正确。
2. `ServiceProbe decision` 只有 185.2 s 超时记录；`verdict` 的判据分歧与 `gainmap-isobmff` 的负控素材前提仍需单独收口，均不写成通过。
3. UI 隐藏/禁用状态与实际入队选项的一致性尚未完成真实桌面矩阵复核，特别是高级面板收起后仍保留的值。
4. 本轮只做审查与标注，不实施 LUT/CMS、RAW provenance、H1 矩阵或 exiftool 路径修复；修复由其他 agent 接手。
