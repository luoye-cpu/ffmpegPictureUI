# 色彩矩阵管线复审补充与定向复现 · 2026-11-21

> 本文是 [2026-11-19 矩阵管线初审](../review-2026-11-19/COLOR_MATRIX_PIPELINE_AUDIT.md) 与 [2026-11-20 复审收口](../review-2026-11-19/COLOR_PIPELINE_REVIEW_CLOSURE_2026-11-20.md) 的**追加更正与新增证据**，不回写旧读数。范围只聚焦当前工作区 Release 中两条仍可复现的问题及矩阵测试闭环；不宣称其它历史问题已被本轮重测。

## 1. 当前结论

- **P1 已复现：Cjxl 的 FFmpeg→cjxl 专用 pipe 未消费显式输入矩阵。** 对真实 BT.709 limited YUV420 AVIF，显式 `bt709` 与 `bt470bg` 两臂都进入该 pipe、返回成功；产品 RGB48 解码结果逐样本相同。独立的相同输入、相同输出 P3 尾链参考可区分两矩阵：产品 `bt470bg` 臂与正确 `bt470bg` 参考的 PSNR 为 **30.5899 dB**，却与正确 `bt709` 参考逐字节相同。
- **P1 已复现：opaque ICC 的安全降级计划绕过显式输出矩阵拒绝。** 普通 PNG + `--color-out-matrix bt709` 对 JPG 是拒绝正控（任务码 91、零产物）；附带 A2B opaque ICC 的同一 RGB PNG + 相同参数却任务码 0、生成 JPEG，报告请求的矩阵未生效，实际输出仍是 `bt470bg`，且与“不指定输出矩阵”臂的 JPEG SHA 相同。
- **E16 旧弱判据描述已过时，应撤回。** 当前 [verify-color-token-normalize.ps1](../../tests/scripts/verify-color-token-normalize.ps1#L440-L555) 已改成四种单字段 nclx 冲突、正确 `bt709/tv` 源解码对拍、错误矩阵/范围负控、任务终态和 PNG cICP 检查。它仍是固定 BT.709/H.264/MP4→PNG 的冲突优先级测试，不代表所有 P/T/M/range 的独立规范级数学 oracle。
- 当前 `matrix-full` 的 RGB 数值对拍已使用从**源像素和标准参数独立构造**的 RGB→XYZ/传递函数参照，旧报告中“NUM 从产品目标标签自参照”的说法不再适用于当前脚本。该 NUM 测试验证 SDR RGB 色域/TRC 转换，不验证 Y′CbCr 输入矩阵解码。

## 2. 两项确认缺陷

### CM-11 · ICC 安全降级绕过输出矩阵裁决（P1）

`PlanIccSafety` 的一般 `ICC_SOURCE_UNDESCRIBED` 分支在 [ColorStrategyPlanning.cs](../../src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs#L41-L54) 允许 Recommended/BakeCicpOnly 任务继续，但该条件未排除非空 `OutMatrixMode`。随后 `Plan` 在 `p.IccSafetyCode != null` 时于 [第 91–99 行](../../src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs#L91-L99) 提前返回，先于 [第 100–105 行](../../src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs#L100-L105) 的 `ApplyExplicitOutMatrix`。同文件另一个 `safeRoute` 条件已排除 `OutMatrixMode`（第 31–32 行），但未描述源 ICC 的容错分支没有同等保护。

当前 Release 的定向复现绑定 `FfmpegGui.dll` SHA-256 前缀 `8CD5BD2E468ABB84`。三臂结果：

| 臂 | 进程码 / item code | 观察 |
|---|---:|---|
| 无 ICC + 显式 `bt709` → JPG | 1 / 91 | 正确拒绝、无产物。 |
| opaque ICC + 显式 `bt709` → JPG | 0 / 0 | 警告 `ICC_SOURCE_UNDESCRIBED`，计划 `None`，显式值未兑现；生成 JPEG，实际矩阵 `bt470bg`。 |
| opaque ICC + auto → JPG | 0 / 0 | 生成同样的 `bt470bg` JPEG；与上一臂文件 SHA 完全相同。 |

结论严格限于这份 A2B opaque profile、JPG 出口、默认推荐策略与指定矩阵组合。arm2/arm3 的产品输出 JPEG 未发现 ICC，且日志记录 ExifTool 元数据恢复因输入源相对路径不可用而失败；不把这项试验外推成所有输入路径都会丢 ICC。详见 [当前 Release 三臂复现报告](../../tests/output/_tmp/audit-20261121-icc-src-d7c8c6559ebe47e9a5786606dcab5550/result.md#L12-L26)、[arm2 JSONL](../../tests/output/_tmp/audit-20261121-icc-src-d7c8c6559ebe47e9a5786606dcab5550/arm2-opaque-icc-explicit/log.jsonl#L4-L13) 与[结构化结果](../../tests/output/_tmp/audit-20261121-icc-src-d7c8c6559ebe47e9a5786606dcab5550/result.json#L15-L24)。

**修复验收建议：** 安全计划可以继续采用“像素不转、来源色彩不宣称”的降级，但必须仍经过输出矩阵能力裁决。对 `Rejected` 明确输出拒绝并保持任务码 91、零产物；对 `Carried/Redundant` 不得借由矩阵 token 宣称实际像素转换发生。回归至少保留上述普通输入正控、opaque ICC 显式拒绝臂、opaque ICC auto 容错臂和一档该格式支持的显式冗余值。

### CM-12 · Cjxl 专用 pipe 漏掉输入矩阵像素变换（P1）

[PipeFfmpegToCjxlAsync](../../src/FfmpegGui/Services/QueueProcessor.cs#L5114-L5205) 将白名单输入矩阵作为 `-colorspace` 放在 `-i` 前（第 5164–5171 行），但其像素链只拼 `dec.PixelChains`、窄范围 PNG 展开和几何缩放（第 5173–5199 行）；没有在第一个 RGB sink 前添加 `scale=in_color_matrix=...:in_range=...`。这与主 FFmpeg 构建路径明确执行输入矩阵滤镜的逻辑不同，见 [FfmpegCommandBuilder.cs](../../src/FfmpegGui/Services/FfmpegCommandBuilder.cs#L111-L121)。`-colorspace` 是输入声明，不足以替代实际 YUV→RGB 系数转换。

当前工作区 Release 证据绑定 EXE ProductVersion `1.6.0-rc1+b085facabeb4ceece785dc39d49447e2d6ba57a1`、产品 DLL SHA-256 前缀 `8CD5BD2E468ABB84`。输入夹具是 64×48、`yuv420p/tv/bt709` AVIF；三臂为 auto、显式 `bt709`、显式 `bt470bg`，并指定 JXL/Cjxl、legacy、Display P3。三臂均确实进入 FFmpeg→cjxl pipe，进程码、JSONL `item_completed.code` 与 JXL 读回均为 0。实际产品滤镜的首项为 `format=rgb48le`，之前没有输入矩阵滤镜；显式 `bt709` 与 `bt470bg` 两臂 JXL/RGB48 读回逐字节相同。独立参考对相同 YUV 源先显式用对应矩阵及 TV range 转 RGB，再用相同 P3 尾链，并分别经 cjxl 编码；bt709/BT.470BG 参考有 9,216/9,216 个 RGB 通道样本不同，MAE 1,103.2998、RMSE 1,936.3227、最大差 10,429、PSNR 30.5899 dB。产品显式 `bt470bg` 臂却与 BT.709 参考逐字节相同。详见[复现报告](../../tests/output/_tmp/audit-20261121-cjxl-src-848f576d-0142-486c-915e-0290e7a71147/result.md#L3-L7)、[滤镜证据](../../tests/output/_tmp/audit-20261121-cjxl-src-848f576d-0142-486c-915e-0290e7a71147/result.md#L17-L25)、[参考比较](../../tests/output/_tmp/audit-20261121-cjxl-src-848f576d-0142-486c-915e-0290e7a71147/result.md#L27-L41)及[数值矩阵](../../tests/output/_tmp/audit-20261121-cjxl-src-848f576d-0142-486c-915e-0290e7a71147/pixel-metrics.json#L34-L41)。

**修复验收建议：** 将已解析的 `InputDecodeContract`（实际像素模型、可执行矩阵 token、range 和是否已消费）复用到此 pipe，仅对真实 Y′CbCr 输入在首个 RGB sink 前应用一次；RGB/灰度、窄范围 RGB、JXR 已转为目标像素的 PAM 不能重复应用。回归须保留 BT.709 与 601 的正确参考、错误矩阵负控、TV/PC 负控、首个 RGB sink 顺序，以及 JXL 产物真实读回。

## 3. 现有矩阵测试的有效范围与限制

### 有效且不应被低估的覆盖

- `verify-color-matrix-full.ps1` 的 NUM 参照在[第 414–492 行](../../tests/scripts/verify-color-matrix-full.ps1#L414-L492)由标准原色、白点及传递函数构造；[第 542–599 行](../../tests/scripts/verify-color-matrix-full.ps1#L542-L599)从原始 RGB 源生成目标参照，并对 PNG/TIFF/JXL 实际读回码值。当前 quick 的预期是 72 个 RGB 数值格；full 若相关格均可用则 NUM 预期 108 格。JXL 数值格要求无损模式、输出位深和尺寸匹配后才比较。
- 同一脚本的 quick 网格仍有 25/0、72/72 NUM 的历史受管读数；LEGACY 层实跑 48 格，含 4 个按能力契约拒绝，另 13 格跳过方向性像素比较。对应读数绑定当前同一产品 DLL 前缀 `8CD5BD2E468ABB84`，但属已有记录，不是本轮重跑；见[准入最终日志](../../tests/output/_tmp/color-admission-final-32814e27de154372a00343764e61d863/gates.log#L1-L25)、[网格逐项输出](../../tests/output/_tmp/color-admission-final-32814e27de154372a00343764e61d863/verify-color-matrix-full.ps1.txt#L86-L113)。13 个跳过方向性对拍的格必须继续作为明确边界，不得描述成 48 格都通过了独立绝对像素 oracle。
- 当前 E16 已不再是“产品两臂只互比”的弱判据。它锁 H.264/MP4 BT.709/tv 基线，单独改 P/T/M/range nclx 字段，验证 stream/frame 标记、warn、成功任务、输出 cICP，并将各产品 RGB48 与显式正确 `bt709/tv` 解码参考逐字节对拍；错误矩阵和错误范围是独立负控。证据形态见[脚本](../../tests/scripts/verify-color-token-normalize.ps1#L440-L555)。但它检验的是元数据冲突时的优先级与该固定源的解码结果，不是所有 P/T/M 组合的标准数学 oracle。

### 仍未闭环的组合

| 轴 | 已有证据 | 主要缺格/风险 |
|---|---|---|
| 输入 Y′CbCr 矩阵 × range | 现有 token 门禁有 601/709、PC/TV 和像素参考切片；有窄范围 PNG、缩放、JXR 专项。 | 支持矩阵 token × 420/422/444 × 8/10/12-bit × PC/TV × route 的交叉尚未闭环；`bt2020nc`、`smpte240m`、`fcc`、identity 请求需覆盖，但要用确实可区分的源分量，不用 RGB 源假装矩阵输入。Cjxl 专用 pipe 目前已由本轮证据确认缺失。 |
| 输入像素模型 × 路由 | RGB、YUV、gray/pal8、窄范围 PNG 等分别有入口检查。 | 不可把 `!RGB` 等同 YUV；应明确 RGB/GBR、gray、pal8、grouped/tiled、多流、未知 probe 的 no-op/拒绝/警告策略，并覆盖 H1、legacy、最长边、JXR、Cjxl pipe。 |
| 输出矩阵 × 容器/后端 | 输出矩阵门禁针对 AVIF/JPEG/WebP/JXL 等有承载、冗余、拒绝切片。 | 这些结果不能概括成所有具体 encoder 已交付。特别要分 AVIF FFmpeg/libaom 与 avifenc ICC、SVT/硬件后端，以及 Cjxl/libjxl；硬件没有实际运行就保持未验证。 |
| 输出矩阵 × range/chroma/bit depth/lossless | 有真实 AVIF 矩阵及位深检查，JPEG/WebP 冗余像素切片。 | 在 YUV 输入矩阵已应用后，代表性 `bt709/601/2020nc/rgb` × 420/444 × PC/TV × 有损/无损需验证实际 nclx M、样本码值和解码像素；规划 Verdict 不能替代编码后回读。 |
| ICC安全计划 × 显式矩阵 | 本轮 ICC×JPG 运行时 3 臂揭露绕过。 | 覆盖 `Rejected/Carried/Redundant` 三类能力；区分 ICC 不可解释时的像素保真降级与显式输出合同，验证 task code/产物/实际 M 和播报唯一性。 |
| 数值网格 × 输入 YUV 解码 | 现有 NUM 对 SDR RGB 色域/TRC 做源独立 oracle。 | 新增专门的 Y′CbCr 原始分量 oracle，固定 primaries/TRC、逐项改变 matrix/range；输出 RGB48 数值，禁止复用产品选择的解码 matrix 或只互比产品臂。 |
| 异常/派生数据 | AVIF Sample Transform 有 16-bit 乘加用例。 | `sato` 的 BSR、声明位深不符、item/stream 非顺序对应，以及较少见的 BT.2020 CL/ICtCp、特殊采样和 ICC/CICP 冲突，单列风险，不混同普通 3×3 Y′CbCr 矩阵。 |

矩阵常量/API 自测、命令字符串、容器标签、像素参考、产物终态应作为不同证据列；任何一列都不能替代其余列。

### 独立复核后的补充边界

- **E16 的当前源码状态与历史失败记录分开。** 当前源码已具备上述强化断言；本收口没有重跑这份新版本脚本，不能据静态存在断言它在当前构建上通过。`ProductPng → ProductConvert` 默认显式选 legacy，输出只取首帧；两帧元数据预检不等同两帧产品像素都已验证。P/T 优先级主要由实际输出标签确认，M/range 才有分别错误的像素解码负控。参照不依赖产品的色彩决策，但与产品共用 FFmpeg/swscale。历史文档“冲突即 91”不能作为现行行为，当前 [队列入口](../../src/FfmpegGui/Services/QueueProcessor.cs#L423-L434) 通常告警并采用可信帧声明；帧无可信 P/T 且要求目标/GainMap 才进入另一拒绝边界。
- **RGB NUM 不等于原生 16-bit 细节保留测试。** 当前 [16-bit 夹具](../../tests/scripts/verify-color-matrix-full.ps1#L144-L172) 先由 8-bit 基图展码，再生成宽色域源；数值比较是有效的，但应另造低位非零阶梯证明真实 16-bit 精度。该网格请求 engine、GMO off、JXL 无损，但未锁定具体 JXL encoder。RT 只是自洽，且[到场门槛](../../tests/scripts/verify-color-matrix-full.ps1#L601-L627)仅要求完成组数大于 0，不是全格到场锁。
- **位深/标签支持格仍有假绿空间。** 主网格[支持档](../../tests/scripts/verify-color-matrix-full.ps1#L317-L320) 只要成功且有产物即可先计 ok；[承诺账](../../tests/scripts/verify-color-matrix-full.ps1#L380-L383)还依赖已播报升降档。对未进 NUM 的 AVIF/WebP 应单独强制交付位深等于请求，或验证确定的能力降级。LBL 以原色词形、任何 ICC 存在或泛降级词通过，不足以证明 ICC 的矩阵/TRC 语义；[LEGACY 字节对照](../../tests/scripts/verify-color-matrix-full.ps1#L1109-L1129) 的 JXL/JXR 分支只测 legacy，不能将方向性比较被跳过的引擎格一律说成仍有字节兜底。
- **AVIF 输出用例已经有真实样本检查，不应描述成纯标签测试。** [K/L 用例](../../tests/scripts/verify-color-out-matrix.ps1#L1020-L1155)分别覆盖常见 sato bit-exact、avifenc ICC 两种表示与 Y 平面端点/红块区分；后者参考依赖另一产品臂的 RGB 和标准公式，未覆盖原始源独立全链和 Cb/Cr。下一批优先补同参 PC/TV 配对及原生 Y/Cb/Cr。SVT 是软件编码器，NVENC/QSV/AMF/VAAPI 才分别属于硬件路径；固定 backend=Ffmpeg 不等于固定具体 EncoderName，需通过预设及真实 `[cmd]` 验证没有换编码器。
- **策略门禁中的人造 PASS 不是执行证据。** [verify-color-strategy.ps1](../../tests/scripts/verify-color-strategy.ps1#L275-L285) 对 legacy 16-bit AVIF 直接登记 PASS 与 `Ok=true`，没有实际运行这些拒绝臂；另外部分成功格仅检产物/读标签、未联合进程和 item code。该覆盖债应通过真实短拒绝用例或明确 SKIP 记录修正，不能把计数当成已执行组合数。
- **ICC 容器静态风险另列待复现。** [TIFF 原生读取](../../src/FfmpegGui/Services/IccContainerReader.cs#L112-L151)只看首个 IFD，[提取流程](../../src/FfmpegGui/Services/IccProfileService.cs#L44-L50)在原生可读但未见 profile 时不回退 ExifTool。后续 IFD profile 是否对应实际选中页面需造 fixture 后裁定。`preserveSourceBytes` 的通用结构验证弱于 `IccProfileIdentity`，但当前部分 carry 调用已先做身份检查，因此不能据局部校验差异断言坏 ICC 已穿透。另，`ResolveIccVersion` 固定 V44 是当前写入策略，[队列](../../src/FfmpegGui/Services/QueueProcessor.cs#L380-L381)已有旧 v2 偏好被忽略的提示，不列为“静默版本缺陷”。

## 4. 分批执行建议

1. **批 A：修复 CM-11 与 CM-12，先补现成最小回归。** 只改 ICC 安全计划/输出矩阵裁决接线与 Cjxl pipe 输入解码合同；新增正常、负控与终态断言。验收固定 DLL 哈希，任务 91/零产物的拒绝，实际 RGB48 与独立参考匹配；不启动全量门禁。
2. **批 B：填输入矩阵代表性组合。** 选择可鉴别 YUV 小图，覆盖 `bt470bg`/`bt709`/`bt2020nc`，PC/TV，420 与 444（必要时另一个 10-bit 切片）；分 legacy/H1/JXR/Cjxl 代表路线，另对 RGB/GBR/gray/pal8 断言不调用 YUV 矩阵滤镜。用 pairwise/边界覆盖，避免笛卡尔全排列。
3. **批 C：输出合同按后端切片。** 至少覆盖 AVIF libaom 与 avifenc ICC、JXL Cjxl 与 libjxl、JPEG/WebP；对 `Carried/Redundant/Rejected` 分别验证任务状态、编码器命令、实际 nclx/ICC、range/chroma/位深和原生或独立解码像素。真实硬件一旦不能运行则记为未验证，不计绿。
4. **批 D：稀有语法与适配层。** sato BSR/位深/关联顺序，复杂 ICC/CICP 与高阶矩阵编码，GUI save/apply 预设和 JSONL 拒绝状态。每个新增测试沿用现有平铺门禁与登记规约。

**Definition of Done：** CM-11/12 的产品回归真实红转绿；正确/错误矩阵与 range 的独立参考能区分；未承载矩阵不能以“成功”冒充已兑现；任务 code、进程 code、文件是否存在、标签与像素各自有断言；报告中每个读数都绑定本轮 DLL 指纹。没有用户明确要求时不跑 104 条全量基线。

## 5. 执行边界

本次定向运行由并行审查子任务完成，未运行全量 104 门禁，未 build，也未修改产品源码或测试判据。证据与所有调试输出均位于仓内 `tests/output/_tmp`。当前工作树原有 50 个跟踪文件修改及若干 untracked 源文件；本追加报告不清理、不覆盖这些更改。具体被测 Release 身份与每项复现见上述报告，不把旧 `publish` 包结果当成当前工作区证据。
