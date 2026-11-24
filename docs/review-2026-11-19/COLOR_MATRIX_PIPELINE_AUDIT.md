# 色彩矩阵管线审查 · 2026-11-19

**结论：先修运行时实际消费矩阵的两端，不要先加大全排列。** 入口有成功却按错误范围解码、或根本未应用显式矩阵的路线；出口有 AVIF 计划承诺与实物不符、JPEG/WebP 宣称冗余却改变像素的路线。三组单元探针通过并不反证这些问题：它们多在预处理、分派、编码或收尾层，而不在 3×3 乘法本身。

范围：当前源码 `b085facabeb4`。使用已有 Release：产品 `FfmpegGui.dll=6F62AE4192DBF322`、`ServiceProbe.dll=10A44732FC3121B7`（SHA-256 前 16 位）。[构建身份](../../tests/output/_tmp/matrix-lead-589e73807a9f40cf9c778fac9d56049f/baseline.txt)为 `1.6.0-rc1+2ddfd4f…`，该提交到工作树 HEAD 的跟踪差异只有项目版本位及两份变更日志，未重建、未验包。四个子代理分别取证入口、规划/像素、出口、UI/CLI；主审复核源码、两条受管门禁及 A/B 读数。本轮不改产品、门禁和历史账本。

## 一、已复现，按修复优先级排列

`P1`：可造成交付错色、无产物，或无声违背已接受的色彩请求。`P2`：诊断、接口或较窄边界问题。各条只声称已测的路由，不推及同格式的所有后端。

| ID | 优先级 | 复现及已证范围 | 代码与证据 |
|---|---|---|---|
| CM-01 | P1 | **满幅 AVIF 的源范围被当成 TV。** `yuv420p/pc/bt2020nc` → legacy PNG，只加与源相同的 `--color-matrix bt2020nc`；成功输出，但命令固定 `in_range=tv:out_range=tv`。解码 RGB MD5 与独立强制 TV 臂同为 `96b2345d…`，与 PC 正确臂 `fb20f352…` 不同。不是单纯 tag 问题。 | [ffprobe 请求](../../src/FfmpegGui/Services/FfmpegCommandBuilder.cs#L2341)、[前缀猜范围](../../src/FfmpegGui/Services/FfmpegCommandBuilder.cs#L2414-L2433)、[滤镜参数](../../src/FfmpegGui/Services/FfmpegCommandBuilder.cs#L101-L134)、[命令证据](../../tests/output/_tmp/80fb346c7dc84e4b91c65bade5ba8900/fullavif-dry.out)。 |
| CM-02 | P1 | **三个入口路径漏掉显式输入矩阵。** ① JPEG→Display P3/PNG：H1 Map 的 709/601 输出逐像素相同，legacy 正控不同；H1 解码滤镜只有 `format=rgb48le`。② legacy JPEG→PNG 开最长边 32 后，709/601 得到同一组 32×24 像素；不开时不同。③ JPEG→JXR 的 709/2020nc/省略，经独立 JxrDecApp 解码 RGB MD5 都为 `bf6a9340…`；同源 JPEG→PNG 正控可区分三档。 | [H1 解码](../../src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs#L90-L134)、[主审 A/B](../../tests/output/_tmp/matrix-lead-589e73807a9f40cf9c778fac9d56049f/differential.txt#L15-L24)、[队列预缩放](../../src/FfmpegGui/Services/QueueProcessor.cs#L1129-L1189)、[缩放证据](../../tests/output/_tmp/5c086b0f-ebad-429b-ad60-6f2f5e11abea/s709.log)、[JXR 编码入口](../../src/FfmpegGui/Services/QueueProcessor.cs#L4119-L4144)、[JXR 执行日志](../../tests/output/_tmp/80fb346c7dc84e4b91c65bade5ba8900/yuv.jpg-bt709-jxr-jxrout.out)。 |
| CM-03 | P1 | **CLI 明列的 `gbr` 能令 YUV 任务失败。** `yuvj420p` JPEG → PNG，auto 和 legacy 都发 `scale=in_color_matrix=rgb`，任务 FFmpeg `-22`、进程 `1`、零产物；709 正控成功。pal8/gray 也被当成 YUV 并报错，RGB24 对照不报错。关键是 `-colorspace` 与 `scale` 词表不同，且“不是 RGB”不等于“是 Y′CbCr”。 | [CLI 帮助](../../src/FfmpegGui/CliParser.cs#L492-L495)、[token 映射](../../src/FfmpegGui/Services/ColorSpaceRegistry.cs#L613-L641)、[失败日志](../../tests/output/_tmp/80fb346c7dc84e4b91c65bade5ba8900/yuv.jpg-gbr-real.out)。 |
| CM-04 | P1 | **AVIF 附 ICC 的专用出口改写显式矩阵。** RGB ICC PNG→AVIF，carry、显式 `bt709/tv/4:2:0`，计划报已承载；实际 avifenc `1/13/0 -y 444`，原始 `nclx` 是 Identity/full，ICC 存在。Manual + P3 同样把 `bt709` 和 `rgb` 交成同一份 `12/13/0`。YUV 源带 `nclx` 的对照：有损保留 M=1，无损改 M=0。证明交付表示与承诺不符，不代表这个 Identity 文件本身用了错系数。 | [取矩阵](../../src/FfmpegGui/Services/ColorMapping/AvifSampleTransform.cs#L338-L353)、[实际 avifenc 命令](../../src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs#L580-L612)、[carry 实测](../../tests/output/_tmp/16bb78a7aa77418da1feb37fab91b0b0/r_709tv.stdout#L15-L31)、[manual 实测](../../tests/output/_tmp/5c086b0f-ebad-429b-ad60-6f2f5e11abea/man709.log#L17-L35)。 |
| CM-05 | P1 | **普通 H1 编码也不兑现 `pc/tv`。** Display P3→AVIF、显式 709，两臂均 `yuv420p/tv/bt709`，原始 nclx full flag 均为 0，RGB MD5 同；legacy 正控分别读回 PC/TV。Display P3→JPEG 的两臂均 `yuvj420p/pc` 且 SHA 相同；TV 虽传了 `yuv420p`，命令没落实 range 或 JPEG limited 所需参数。 | [引擎像素格式分流](../../src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs#L1335-L1339)、[传统范围分流](../../src/FfmpegGui/Services/FfmpegCommandBuilder.cs#L758-L839)、[AVIF PC 实测](../../tests/output/_tmp/f9d39d35a28f49fc971f7ed6597b6923/range_engine_pc.stdout)、[JPEG TV 实测](../../tests/output/_tmp/5c086b0f-ebad-429b-ad60-6f2f5e11abea/engjpg_tv.log#L17-L30)。 |
| CM-06 | P1 | **冗余 BT.601 变更 JPEG/WebP 像素。** 同源无额外色彩配置，`auto` 与显式 `bt470bg` 的差异是后者强制加 `-pix_fmt yuvj420p/yuv420p`；两种容器的解码 RGB MD5 均变化。JPEG 门禁对比为 `34.284457 dB`；WebP auto SHA `9B6B2B1587840E05`，显式 601 族 `61ED9CB68B5A9299`。不是 JPEG ICC 时间戳或 WebP 容器字节差。 | [未按承载裁决过滤的像素格式分流](../../src/FfmpegGui/Services/FfmpegCommandBuilder.cs#L661-L728)、[主审 A/B](../../tests/output/_tmp/matrix-lead-589e73807a9f40cf9c778fac9d56049f/differential.txt#L1-L14)。 |
| CM-07 | P1 | **DNG 的自产 linear Rec.2020 语义进 H1 前丢失。** 受控 DNG 经 SDK 实际渲染 16-bit linear Rec.2020；队列也说已声明 `bt2020/linear`，但随即报源按 sRGB 假定；引擎输入是临时 TIFF，识别条件仍按扩展名要求 RAW。当前对照两个最终 PNG 的 RGB48 MD5不等（其输出 P/M 分别为 BT.2020 和 BT.709），所以只复证错误的源识别与分派，不重述旧报告的逐像素相等。DNG `ColorMatrix1/2` 是相机校准，不是 H.273 输入矩阵。 | [真实队列写临时 TIFF 和声明](../../src/FfmpegGui/Services/QueueProcessor.cs#L919-L936)、[识别条件](../../src/FfmpegGui/Services/ColorMapping/ColorIntentFactory.cs#L123-L151)、[SDK 与实际队列证据](../../tests/output/_tmp/matrix-lead-589e73807a9f40cf9c778fac9d56049f/raw-differential.txt#L1-L19)。 |
| CM-08 | P2 | **矩阵播报既成功又未生效。** 普通 AVIF H1 实物 M=1，日志先报落地、后报“该出口无矩阵承载/规划层未参与”；JPEG 冗余模板也可重复。不可只在成功处把标志设为真：CM-04 的确存在计划成功而执行未兑现。 | [成功分支及消费位](../../src/FfmpegGui/Services/QueueProcessor.cs#L1384-L1416)、[收尾兜底](../../src/FfmpegGui/Services/QueueProcessor.cs#L1623-L1640)、[矛盾日志](../../tests/output/_tmp/ec9e6e3407394ebbbaa218cc739ca1a2/engine_709.stdout#L17-L46)。 |
| CM-09 | P2 | **普通出口与 cjpegli/cjxl 的拒绝任务码不同。** PNG 拒 BT.709：进程 `1`、任务 `91`、零产物；`-f jpg -e cjpegli`、`-f jxl -e cjxl --color-engine legacy` 同样进程 `1`、零产物，JSONL 没有任务 `code`。cjpegli 文案说 `exit 91` 不是实有码。 | [异常收口](../../src/FfmpegGui/Services/QueueProcessor.cs#L1651-L1655)、[PNG JSONL](../../tests/output/_tmp/dba4a350f5af4f5c8f63d90db4a64524/png_reject.jsonl#L14)、[cjpegli JSONL](../../tests/output/_tmp/dba4a350f5af4f5c8f63d90db4a64524/cjpegli_reject.jsonl#L7-L11)、[cjxl JSONL](../../tests/output/_tmp/dba4a350f5af4f5c8f63d90db4a64524/cjxl_legacy_reject.jsonl#L6)。 |
| CM-10 | P2 | **窄路径的证据来源混用。** (a) 63×47 RGB→PNG/legacy P3，`auto/bt709` 变 64×48，而 `gbr` 保持 63×47；显式两臂均声称矩阵不改像素。(b) SDR JXL 管道把探测值 `gbr` 写回用户选项；未传矩阵与用户显式给 2020nc 都被报成用户指定 `gbr`，未证明 RGB 像素错。(c) 只改 10-bit AVIF 容器 M、不改码流，形成 stream=709/frame=2020nc；两份输入解码像素相同，产品重新编码时冲突臂被静默统一成 709，源/产物 `38.425339 dB`，一致臂为 `inf`；不裁定冲突文件的规范优先级。 | [RGB 几何条件](../../src/FfmpegGui/Services/FfmpegCommandBuilder.Decision.cs#L405-L443)、[JXL 用户项覆写](../../src/FfmpegGui/Services/QueueProcessor.cs#L7275-L7278)、[JXL 日志](../../tests/output/_tmp/80fb346c7dc84e4b91c65bade5ba8900/sdr.jxl-bt2020nc-real.out#L10-L14)、[冲突臂命令](../../tests/output/_tmp/80fb346c7dc84e4b91c65bade5ba8900/tagged10-colr709-avif-dry.out)。 |

## 二、静态发现及待验证边界

- **AVIF 后置重封装：最高优先级补端到端负控。** 单独执行产品的 `-map 0 -map_metadata 1 -c copy`，真实双标定 AVIF 的 `colrprof` 消失，nclx 与 ffprobe 标签保持，命令退出 0。产品在 exiftool 缺失时可能调用原命令，只验证描述性字段；AVIF 后置 ICC 又跳过。[回退分支](../../src/FfmpegGui/Services/QueueProcessor.cs#L3245-L3259)、[替换条件](../../src/FfmpegGui/Services/QueueProcessor.cs#L3344-L3380)、[原 AVIF](../../tests/output/_tmp/16bb78a7aa77418da1feb37fab91b0b0/r_709/tagged-rgb.avif)、[重封装 AVIF](../../tests/output/_tmp/fc75ffd08424488fb343397cb7b01710/icc-remux.avif)。本轮只实证底层命令，未实跑无 exiftool 的整个产品队列；亦未把 alpha/sato/tmap 均会丢写成事实。
- **GUI 预设两处：代码确定路径，未实际起 GUI。** 矩阵在[后端之前应用](../../src/FfmpegGui/MainWindow.xaml.cs#L6890-L6902)，[编码器回放](../../src/FfmpegGui/MainWindow.xaml.cs#L6993-L7021)后不会重放已经落回 auto 的 `xyb`，最小格为 JPEG 的 `cjpegli + xyb` 保存→mjpeg→应用。另[保存](../../src/FfmpegGui/MainWindow.xaml.cs#L6736-L6740)把显式 auto 合并为 null，[恢复](../../src/FfmpegGui/MainWindow.xaml.cs#L7287-L7300)遇 null 不改，不能区分旧预设缺字段和新预设意图清除。实际/预览动图克隆漏输出矩阵是另一项静态风险，不和已复现结果混记。
- **GIF→AVIF、硬件、特殊矩阵仍需分路。** GIF→AVIF 的 BT.709 已实测 rc=0、仍为 `smpte170m`，但[有未生效警告](../../tests/output/_tmp/dba4a350f5af4f5c8f63d90db4a64524/gif_avif_warn.jsonl#L63)，不计静默错色。当前随包 avifenc 对静态纯红 `-y444` 的 M=1/5/9 编出了不同的 Y′CbCr 样本，不能把历史“固定 601”当现行普遍机制；动态 GIF 的系数还没做同一对照。SVT/NVENC/QSV/AMF 未跑实际硬编；不能把枚举、GUI 模拟或 ffmpeg 自报能力算实交付。BT.2020 CL、chroma-derived、ICtCp、JXR YCC/PTM 与 GainMap 专路未做真实分量 oracle。
- **旧问题不直接照抄。** [11-17 审查](../review-2026-11-17/COLOR_PIPELINE_INDEPENDENT_AUDIT_2026-11-17.md)的 H1 显式输入矩阵与 RAW 来源问题本轮重验。外置 ICC 不存在现在会[警告后继续](../../tests/output/_tmp/matrix-lead-589e73807a9f40cf9c778fac9d56049f/differential.txt#L25-L28)，不再等同旧结论“静默忽略”；LUT ICC 全路由和冲突处理未重跑。把“已知合法但非 matrix/TRC”与“缺失/不可读”分开是后续组合，不能拿历史断言替代现行策略验收。

## 三、本轮门禁与红项归因

```powershell
pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1 -Probes matrix,colormath,plan -Scripts verify-color-out-matrix.ps1,verify-jpg-webp-matrix.ps1
```

| 项目 | 实际结果 | 能说明什么 |
|---|---|---|
| `matrix` / `colormath` / `plan` | 15/0、19/0、43/0 | 这些已有单元断言通过；不覆盖分派中丢失的意图。 |
| [输出矩阵门禁原始日志](../../tests/output/_tmp/matrix-lead-589e73807a9f40cf9c778fac9d56049f/_gate_script_verify-color-out-matrix.ps1.txt) | 110 PASS / 8 FAIL | D2/D3/D4/E1/E2 五条为 CM-06 的真实行为缺陷。A7 钉着旧的“未强制 YUV”告警，实际现在强制 YUV 且实物 M=1；应换真实落地判据。A9、K1 均是特定日志锚缺失：A9 实物 Identity 和像素 bit-exact 仍通过；K1 双 item、K2 往返 RGB48 MD5 仍通过，不能从日志格式断言功能坏。 |
| [JPEG/WebP 门禁原始日志](../../tests/output/_tmp/matrix-lead-589e73807a9f40cf9c778fac9d56049f/_gate_script_verify-jpg-webp-matrix.ps1.txt) | 29 PASS / 1 FAIL | legacy 三原色与错误矩阵负控均过，engine 臂锁的是命令，不是像素。唯一红为无标签 PNG→JPEG 无 ICC；[任务日志](../../tests/output/jpgwebp_matrix_ce66128d85d14ff4aadf4db3c2a12609/legacy_jpg8/log.txt#L4-L14)明确走低置信、未标注按 sRGB 的现行分支，ExifTool 实物读取也无 ICC。应把“无标签策略”和“承诺携带时不丢 ICC”拆成两臂；不把此红算作默认 BT.601 像素失败。 |

[运行器汇总](../../tests/output/_tmp/matrix-lead-589e73807a9f40cf9c778fac9d56049f/gate.log)：受管子集 2/104 门禁，退出 `1`；不是全量基线。没有运行 matrix-full、UI 受管门禁、硬件全组或打包测试。

**重要的判据缺口：** [matrix-full 默认 quick](../../tests/scripts/verify-color-matrix-full.ps1#L241-L242) 的主网格是 PNG/JXL × 8/16 bit，不含 AVIF/WebP；[主网格 NUM](../../tests/scripts/verify-color-matrix-full.ps1#L397-L415)从 `$g.out` 本身用目标 P/T 生成参照，再比 `$g.out`，不能证明源→目标算对。脚本另有[从原始源生成的 LEGACY 参照](../../tests/scripts/verify-color-matrix-full.ps1#L868-L907)，但限制格式且部分 BT.709 方向不判 H1；往返也不能代替独立正向 oracle。因此修的是主网格判据和缺格，不是把整支门禁一律作废。另，[输入 token E/F](../../tests/scripts/verify-color-token-normalize.ps1#L217-L283)偏选项位置、告警和范围，没有真正覆盖所有 `scale=in_color_matrix` 词及对应 Y′CbCr 像素；[GUI J19g](../../tests/projects/UiTestHost/Program.cs#L3554-L3563)仅测回放 helper，不经过完整保存/应用。

## 四、全局修复与覆盖计划（按依赖，不铺全排列）

| 顺序 | 可独立分工的工作 | 最小覆盖切片与必须观测的证据 |
|---|---|---|
| 0 | 先增加可鉴别负控，锁定策略边界；不得改弱门槛让整轮变绿 | 成对使用 RGB 非中性色块、Y′ 黑白阶及已知 Cb/Cr 的 YUV 小图。每格记录真实 route、任务码、有效矩阵/范围、像素面、标签、完成播报。负控至少包括“保留标签但用错系数”“同像素带错标签”“错误范围但 nclx 字面正确”。 |
| 1A | 输入组：先统一像素模型、来源与可执行解码参数，再接所有实际解码点（CM-01/02/03/07） | `601/709 × full/limited` 8-bit 选角；另 10-bit BT.2020 NC、合法 GBR、普通 YUV 上的 Identity 请求、gray/pal8；路由选 legacy、H1 None/Map、最长边开/关、JXR 各一对。RAW 独立比较同源 SDK linear→目标 TRC 的参考像素。只断言输入矩阵修正发生在第一次 YUV→RGB，不能在事后通过修改 RGB 标签补救。 |
| 1B | 输出组：把 `Redundant/Carried/Rejected` 与有效 range/chroma/bitdepth 在**具体编码器**上一起裁决（CM-04/05/06） | 同一 RGB 源依次覆盖 S1 None/Map、S2 carry、S3 CICP-only、S4 manual；AVIF 分 FFmpeg/libaom 与 avifenc ICC，选 `rgb/709/601 同族/跨族`，有损与无损各一、pc/tv A/B，分别查原始 nclx、ICC 和 Y′CbCr 码值。JPEG/WebP 的 601 冗余必须与 auto 解码一致。avifenc 不支持的组合必须明确降级或拒绝，不能只换标签。 |
| 2 | 收尾组在 1A/1B 之后接入真实交付结果；UI/CLI 可先补预设和结构化失败用例 | 矩阵终态唯一：交付、冗余、忽略、拒绝不能互相矛盾，JSONL 任务 91 与进程 1 各自锁。检查 exiftool 在/缺/失败下的 AVIF ICC/nclx 及必要时 alpha/sato/tmap，确认元数据处理前后不减损。GUI 走完整 save/apply/提交，覆盖 `cjpegli→mjpeg→回放`、显式 auto 与旧字段缺省、GIF 两步和动画回退。 |
| 3 | 补稀有组合与独立 oracle，再做受管回归 | 目标顺序：真实 CL/ICtCp、P/T 与 M 独立的 ICC/CICP 容器冲突、HDR/GainMap/透明和 16-bit、SVT/硬件真实交付。主 NUM 从源计算期望：Y′CbCr 按标准系数与范围逆变换，RGB 色域/TRC 用 zimg 或独立标准公式，最后单独纳入编解码量化容差。跑相关 1–3 条门禁/批，不把硬件不可用记作绿；新增项目按仓库清单/账本登记。 |

实现上应区分 **用户意图 → 源/中间件事实 → 可执行解码合同 → 可执行出口合同 → 最终交付结果**。尤其不能用同一字符串同时代表 `-colorspace` token、swscale 系数名、真实像素布局和产物声明；也不能把“计划认为能承载”当作“编码器已经交付”。本审查未决定是否更改历史预设兼容策略或宣告所有恒亮/硬件组合受支持。
