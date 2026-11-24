# 色彩矩阵管线完整修复计划 · 2026-11-21

> 当前实施批次由维护者授权完整修复与测试。本文件以 [复审报告](COLOR_MATRIX_PIPELINE_REVIEW_2026-11-21.md) 和实现代理读取的现行源码为基线；不重述已过时的 CM-01–CM-10 历史结论。计划不是验证结果。基线目录见文末，计划行内“待运行”只有集成代理实测后才可转为通过。

## 1. 修复目标和完成定义

将管线做成四个不可互相冒充的合同：输入源事实（布局、实际 P/T/M/range 与 ICC）→首次 Y′CbCr→RGB 解码合同→像素/输出合同（编码器、chroma、range、bit depth、矩阵/ICC/CICP）→终态/产物证据。

对每个被宣称“修复”的格，须有实际路由、请求与观察命令、独立像素或结构 oracle、真实容器回读、item/进程状态及产物存在性；负控必须仍红。只修明确支持域；不能运行的硬件登记为未验证，不推导通过。

**本次完成定义：** CM-11 与 CM-12 回归从已复现到修复；输入/输出矩阵的代表组合正确且失败可见；sato 不支持或畸形操作不再静默给零/猜 stream；ICC读取不能把后续页面/非法结构误当首图安全 ICC；策略/NUM/交付门禁无伪造 PASS；所有读数绑定本轮冻结 DLL。旧 50+ 文件改动必须原样保留，不reset/clean/commit。

## 2. 逐发现处置矩阵

| 项 | 类型/级别 | 修复/处理 | 最小验收与边界 | 负责人工作区 |
|---|---|---|---|---|
| CM-11 opaque ICC安全计划绕过显式 out-matrix | 已实测 P1 | 安全计划也执行唯一 `ApplyExplicitOutMatrix`。auto维持容错；Redundant保留None及原ICC降级，不新增P/T；Carried只登记可承载M，不近似未知源色彩，靠最终实物回读兑现；Rejected须Action/Verdict=Reject且Degradations为空，原ICC拒绝不得被反转。 | 普通PNG显式BT.709对JPG拒绝正控；opaque A2B ICC PNG + BT.709 同样任务91/零产物；opaque ICC + auto依原政策；JPG601或PNGrgb冗余；AVIF可承载M。联合JSONL终态、产物、实际矩阵与唯一播报。 | repair-icc-plan：`ColorStrategyPlanning.cs`；repair-tests：JPG门禁 |
| CM-12 Cjxl pipe未消费输入矩阵 | 已实测 P1 | 所有到达 Cjxl pipe 的 YUV 解码入口，在首个RGB sink前消费统一 input matrix + source range，一次且仅一次；矩阵/range先于几何缩放。RGB/灰度 no-op，JXR已转目标 PAM 不重复。覆盖 direct-remap分支和普通 `BuildPipeColorArgs` fallback。 | 64x48/更高位深小YUV；显式709/601、PC/TV；产物RGB48/JXL读回对独立FFmpeg参考，错矩阵与错range负控；命令/结构确认单一输入滤镜位置。至少主映射和普通pipe两路。 | repair-queue：`QueueProcessor.cs`/必要时InputMatrix |
| sato BSR和未支持操作落默认0 | 实现缺口，P1正确性 | 随包libavif核实BSR是一元运算：L<=0给0，否则floor(log2 L)。payload低2位bit_depth表示有符号运算宽度8/16/32/64，绝非AV1输入位深；现实现只支持32-bit，其余明确拒绝。匹配signed32饱和、除0保左值及POW语义，校验保留位/精确长度/栈结构，不接受未执行token。 | BSR正值/最高位/零/负值、32-bit signed饱和及运算边界；不支持运算位宽/坏算式明确拒绝。端到端K解码，常见12+4（实际AV1两输入为12-bit主+8-bit扩展）继续通过。 | repair-sato：`AvifSampleTransform.cs`；repair-tests协调K |
| sato dimg item→ffprobe stream关联猜测、depth混淆 | 待确认静态风险 | 优先按dimg item原始OBU分别解码，或有可靠item ID才能映射绝对stream index；不按流序/primary位置猜。分别保存ArithmeticBitDepth、input AV1 av1C/pixi位深及输出sato pixi位深；不能要求payload算术32与输入12/8相等。 | 多轨/打乱/重复/缺失关联fixture；合法12+4正控。主/扩展两份 av1C/pixi、算术32及输出16各自独立断言；关联不明不能产看似成功输出。 | repair-queue + repair-sato + repair-tests |
| TIFF ICC仅首IFD及原生可读无ICC不fallback | 风险澄清及边界加强 | 现产品默认抽帧为首页，IFD0无ICC时true/null且不读后页是正确策略，不能为“修复”把IFD1 profile冒充IFD0。保留首页语义，新增首IFD offset、完整目录含next-pointer边界检查；解析未知/截断有限失败，fallback不得改变选中页。 | 双页TIFF仅IFD0 ICC、仅IFD1 ICC、每页不同ICC、两页无ICC；reader与首页选择一致。坏IFD/tag应有界失败，不能把后页ICC误带入首页。 | repair-icc-reader + repair-tests |
| ICC source原样carry校验弱于身份解析、DetectPrimaries未验XYZ type | 静态风险 | 原样profile未经身份结构验证不得 carry/解释；`rXYZ/gXYZ/bXYZ` 检查type/size/range、对齐/重叠沿用 parser规则；opaque合法LUT仍可字节原样携带，不要求执行LUT。 | 有效MatrixTRC、有效A2B profile字节正控；伪造XYZ type、越界、重叠、未对齐/重复tag负控。源ICC字节和输出carry精确哈希；GainMap primaries错误type不得被识为标准色域。 | repair-icc-reader + repair-tests |
| E16 H.264单轴冲突 | 现有判据已增强，非当前缺陷 | 保留现有首帧BT.709/tv源参考、错M/range负控、终态/cICP。不重复写弱版。新加只测当前组合未涉及的显式输入矩阵覆盖或不可信源边界，不能推翻帧优先警告策略。 | 跑现有 token 门禁并确认固定fixture对每个产品臂实际生效；参照与产品共享FFmpeg/swscale，不能称标准独立数学oracle。 | repair-tests：token门禁 |
| matrix-full NUM/RT/LBL/位深伪绿空间 | 覆盖债 | 保留独立 RGB→XYZ/TRC oracle。加原生16-bit低位阶梯、全格实际到场与精确拒绝码。RT改完整预期格数。LBL不能以任意ICC存在/泛“降级”替代目标ICC矩阵/TRC正确性；主 NUM 外 AVIF/WebP位深需独立检查。JXL应点名实际encoder。 | 不扩成全笛卡尔：针对性低位阶梯每channel覆盖非256倍低位码；RT源×目标预期逐格到场；标签用解析后的profile语义；位深核 `jxlinfo`/AVIF bitstream或已验证probe；legacy跳过格明示。 | repair-tests/grid-tests：matrix-full |
| verify-color-strategy 人造AVIF拒绝PASS、终态薄弱 | 覆盖债 | 删除合成 `$res Ok=true` 冒充产品执行；跑真实明确显式能力拒绝，或计为SKIP且不算执行格。各真实格检查item_completed.code、进程码、文件、裁决和标签。legacy非承载sample-transform拒绝应准确验证。 | 用真实16-bit P3 AVIF/legacy臂验证能力拒绝、任务码/进程码/零产物；引擎臂继续真实样本交付；策略 parity只比较真实共路由格。 | repair-tests/strategy-tests |
| 输出矩阵×AVIF/JXL具体编码器 | 覆盖债/部分未验证 | 验证FFmpeg/libaom、avifenc ICC、Cjxl和libjxl独立实际命令；SVT是软件backend，单独验证真实编码器名与像素/交付。 | 至少AVIF libaom+avifenc做709/601、PC/TV配对，原生Y/Cb/Cr+主项nclx/ICC；JXL Cjxl/libjxl命令与像素读回。编码器命令不匹配直接失败。 | repair-tests/out-matrix、repair-queue |
| 硬件 AV1 (NVENC/QSV/AMF/VAAPI等) | 实机环境未验证，不是实现已修 | 只在有对应实际设备时运行；明确记录device、驱动、ffmpeg build与选中EncoderName；不得以列表/配置/软件替代。无设备写`NOT_RUN`，留后续设备矩阵，不计绿。 | 设备上有损/无损适用模式、实际nclx M/range/chroma/bit depth、真实解码样本和task终态。 | 整合/最终复审代理；硬件可用再安排 |
| BT.2020 CL、ICtCp、奇异布局/未知probe、完整range×layout×route×容器 | 明确边界/覆盖债 | 对尚不支持域明确警告/拒绝，不以普通NC 3×3变换冒充。用pairwise与边界等值切片，保持矩阵×range×layout×route×output axes分离。 | 先证明token被拒绝或无效时无滤镜；支持项必须用原生分量参考。未知probe不默认为TV。 | 不阻塞P1；列为环境/能力后续范围 |

## 3. 阶段/文件所有权

1. **冻结与契约并行（当前）**：保存tracked diff/status/HEAD/已修改与未跟踪文件SHA、build DLL identity。tracked文件的修复前状态可由HEAD加patch重建；untracked源码只有哈希，没有原文快照，不能声称全树均可完整逐行增量对比。所有改源码代理只在发出的write scope内修改；不得copy/clean旧树。baseline路径由owner广播。
2. **输入与ICC计划并行**：CM-11 `ColorStrategyPlanning.cs`、CM-12 `QueueProcessor.cs`/InputMatrix、ICC `IccProfileService`/reader/identity、sato `AvifSampleTransform`/ISO probe。`QueueProcessor.cs` 是共享热点，单owner，其他人用消息传接口建议，不编辑该文件。sato队列stream map由queue owner实现，parser作者提供精确定义。
3. **判据并行**：范围严格拆为 token/out-matrix/JPEG-WebP、matrix-full、strategy；每个文件唯一owner。追加 task-9 `repair-probes` 以已有默认 `iccname` mode承载，不新增mode：其允许文件范围是 [46_ProbeIccName.cs](../../tests/projects/ServiceProbe/Parts/46_ProbeIccName.cs)、[61_ProbeIcc.cs](../../tests/projects/ServiceProbe/Parts/61_ProbeIcc.cs)，及 [verify-probe-mirror-parity.ps1](../../tests/scripts/verify-probe-mirror-parity.ps1) 中现有 `iccname` 精确断言条数锁。[_module-registry.json](../../tests/scripts/_module-registry.json) 当前没有ServiceProbe逐mode条数锁，不人为新增第二真值；只核其相关现有登记。只补sato运算/运算位深/非法payload、ICC合法LUT/结构与TIFF首页关联fixture；先协商接口，不能镜像实现或减少旧断言掩盖红。若要新增门禁/ServiceProbe mode，必须另获lead批准并同步runner清单、条数、模块账本、coverage登记。
4. **冻结、构建、定向验证串行**：task-7 依赖 task-1/2/3/4/5/6/**9** 全部完成且lead明确发出冻结。所有源码/判据代理提交结果且退出编辑后才冻结。先单线程Release build；确认产品/ServiceProbe DLL SHA后跑受管相关门禁分批（首批 `verify-color-token-normalize.ps1,verify-color-out-matrix.ps1,verify-jpg-webp-matrix.ps1`；第二批 `verify-color-matrix-full.ps1,verify-color-strategy.ps1`，通过runner的`-Scripts`选择）。按AGENTS固定`TMP/TEMP/TMPDIR=tests/output/_tmp/round-tmp`。任何改源码后重build冻结再重跑受影响门禁；绝不跑104全量基线，除非再获用户明确请求。
5. **最终审计交付**：检查每项finding被修/降为明确不支持/待真实硬件，不写“完整修复”掩盖Not-run；逐个结果绑定commit/HEAD、DLL hash、runner调用、输出目录与实际退出/断言计数。最终报告/索引由独立文档owner操作。

## 4. 用例组合缩减方案

不执行无限排列。各轴：

- 输入：`ColorMatrix` auto / BT.601 group / BT.709 / BT.2020NC；layout 420/422/444及RGB/GBR/gray/pal；8-bit代表、一个原生10-bit；源range PC/TV/unknown；真实路线 legacy/H1/Cjxl/JXR/预缩放。
- 输出：`ColorOutMatrix` auto/601/709/2020NC/rgb；format PNG/JPG/WebP/AVIF/JXL/JXR；具体编码器 FFmpeg libaom、avifenc ICC、Cjxl、libjxl、真实可用SVT/hardware；chroma420/444、out range、depth8/10/12/16、lossy/lossless、strategy none/map/carry/cicp-only/manual。
- 独立抽样：pairwise覆盖基础轴；全覆盖关键交互 `input M × source range × actual layout × route` 和 `output M × encoder × output range/chroma/depth`；错误M、错误range、错误layout作为明确负控。未涉及到的跨乘积记coverage ledger，不用“总格数”冒充完整笛卡尔覆盖。

### 当前已分配的具体测试切片（待执行，不计通过）

- **token 输入分量切片：** 原生10-bit 420样本，源PC/TV × 显式709/2020NC；legacy PNG16 对同源显式参考，H1 Display P3对正确/错M参考，预计至少八个真实产品格。另Cjxl PC/TV × 709/601四格对真实P3尾链参考；验证首RGB sink前matrix恰一次。RGB no-op/已转目标PAM通过精确路由接口与负控另锁。
- **JPG/ICC切片：** 同一opaque LUT PNG的auto、601冗余、709拒绝及无ICC709拒绝正控，联合JSONL终态。ICC parser反射当前冻结DLL，用TIFF首/后页、错XYZ type、非法目录结构fixtures验证，避免制造第二份解析实现当产品正确性的替身。
- **out-matrix切片：** K补BSR/signed32、不支持运算精度/坏payload；sato合法16-bit常见配方必须继续bit-exact。L补只改变输出PC/TV的实际Y/Cb/Cr读回；具体encoder先锁命令，SVT/硬件由整合代理取证可用性，不用软件替代通过。
- **grid/strategy切片：** matrix-full保留源独立RGB数学oracle、加原生低位阶梯、支持位深与标签/RT到场保护；strategy伪PASS改真实产品终态。两文件独立owner，只由repair-tests汇总，不并发改同文件。

## 5. 代理验收接口

各实现/测试代理须回报：改动文件与函数；请求合同；是否消费matrix/range及在首个RGB sink位置；normal/rejected/unsupported状态及task/进程码；pixel/container oracle；边界与实际未测项。只返回“已修改”不算收口。

整合构建门禁输出必须记录：build command/rc、`FfmpegGui.dll`及`ServiceProbe.dll` SHA256/版本、受管runner命令及manifest身份、每个脚本pass/fail与产物目录。全量104 gates严禁默认运行。

## 6. 基线与注意事项

仓内只读记录目录：`tests/output/_tmp/color-matrix-repair-baseline-20261121-e6ea2413ecde4f468fdd2b5c4223dced/`。其中 `git-status.txt`、`head.txt`、`working-tree.patch`（包含修复前所有**tracked文件**工作树diff）、空`index.patch`、`numstat.txt`、`source-file-hashes.json`（当时已修改/未跟踪源及脚本的hash，不含原文）与`identity.json`（Release指纹）。HEAD是 `b085facabeb4ceece785dc39d49447e2d6ba57a1`；50个跟踪修改+10个untracked文件；`FfmpegGui.dll` SHA `8CD5BD2E...EB92A`，`ServiceProbe.dll` SHA `CB176557...B9FEE`。之前一次git `--output`参数未成功时没有损坏工作树；随后已重试正确PowerShell参数并核对patch 651,882 bytes。所有临时环境定向 `tests/output/_tmp/round-tmp`。当前报告中的证据是既有审查运行，不是本修复批验证读数。

**baseline可恢复性限制（整合前追加）：** tracked文件可以用HEAD和patch进行前后增量审阅；初始untracked源码（含 `IccContainerReader.cs`、`IccProfileIdentity.cs`、`FfmpegCommandBuilder.InputMatrix.cs`）只留下hash，无法仅凭本目录重建旧原文，也不能据hash判定具体哪几行属于本轮新增。已要求这些文件owner在尚未编辑时补存将改区域的原文片段、行号和hash；若已经编辑且没有原片段，只能注明证据不足，禁止事后虚造baseline。无需复制全源码树。整合代理应把可证明的前后片段和基于当前实现的复核分开，最终文档不得宣称完整全树逐行增量审查。

**task-9验收加入task-7：** 使用已有默认 `iccname` mode承载新增断言，构建冻结宿主后必须实际执行 `iccname`，并跑对应mirror与module-registry结构检查；精确条数唯一冻结点在mirror脚本，不在module-registry另复制一份。新增断言数以owner实际新增的独立Check site精确计数为准，通过数只在verifier执行后填，不把静态注册当运行通过。
