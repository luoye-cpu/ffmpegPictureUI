# 色彩矩阵 I/O 完整修复实现计划（2026-11-14）

> **2026-11-15 更正（不回改历史读数）**：本文正文的 PNG/APNG 段落与旧读数按当时“ICC+CICP 双写”
> 策略记录；用户当日最新口径已改为 **PNG/APNG 只在 CICP 无法精确表达时才使用 ICC**
> （CICP-first）。最新实现、168 格矩阵与 ICC 审计见
> `docs/review-2026-11-15/COLOR_MATRIX_IO_ANALYSIS_2026-11-15.md` 与
> `docs/review-2026-11-15/ICC_STANDARD_AUDIT_2026-11-15.md`。本文正文的 2066B/双写读数不再作为
> 当前验收；保留为时间点记录。

状态：**P0 已落地并验证；P1-A/P1-B/P1-C/P1-C′/P1-C″/P1-D 已落地并验证；P1-E、P2 待办**。本计划承接 `COLOR_MATRIX_IO_ANALYSIS_2026-11-14.md` 的 C-01…C-17，不重做分析，不把旧审查文档当依据。CICP-only（源仅 cICP、无源 ICC）的 `CarryIcc` 路径已在 2026-11-15 补上 ICC 内 `cicp` 标注：生成标准 ICC 的判定不再要求 `Action=CarryIcc`，而是看 `AttachIcc+IccPrimaries`（CICP-only 分支把 Action 置为 `None`，仍承诺附 ICC）。同日又修了两处出口缺口：源 PNG cICP 为 PQ/HLG 时跳过 iccgen（冲突样本从零产物变 2066B），以及引擎路径按 `NeedsPostEmbedIcc`/源 ICC 承诺放行 webp 后置（P3→webp 恢复 ICC 4.4.0 + cicp）。

定向门禁当前**未全绿**。2026-11-15 复跑读数：`verify-color-wiring.ps1` 最近一次 PASS=146 / FAIL=12（该次 exiftool `perl532.dll code 126` 高频抖动，ICC 回读类断言连锁失败；同一构建在 exiftool 正常时基线为 154/5）、`verify-decision-delivery.ps1` PASS=88 / FAIL=6（p3/webp 三条已转绿；剩余 `⑯` 降档点名、`⑰b legacy` 4 项（16-bit AVIF 在 legacy 引擎下 exit 91，不是 ICC 回归）、`⑱-4` 形状锁）、`verify-prophoto-faithful.ps1` PASS=39 / FAIL=0。wiring 稳定失败集中在 (10) JXL 旧契约两项、(11) 8/16-bit 回读空 + 中转清理、(13) 合并探测点名。P1-C 已按用户明确口径落地（显式 CarryIcc = 强制附 ICC + 保留 CICP），并落到“源 ICC 优先改写为覆盖源色域的最窄标准空间”；P1-C′ 已落地“源图自带 ICC 一律重写为等价 v4.4.0（不是只改版本头）”；P1-C″ 已落地“ICC 内部额外写 `cicp` tag（ICC.1:2022 / H.273）”，并已覆盖源 ICC、标准自生成、CICP-only 三种 CarryIcc 形态；`src_icc_cicp_conflict` 已从零产物改为 2066B 标准 ICC；引擎路径的 webp 后置也已接通。P1-D 落地 AVIF alpha。JXR/`perl532` 故障未归因前不写成“已确认非本轮”。

## 0. 基线与边界

- 仓库：`C:\PLAN\ffmpegPictureUI`，分支 `main`，HEAD `a9e2830`。
- 工作树已有 2026-11-14 色彩标注批改动；本计划**不回退**既有改动，只在其上增量修复。
- 不跑全量 104 条门禁。每批只跑所在链路的针对性门禁；全量入口不自行使用。
- 修复完成前不改 `docs/` 中既有四份 11-14 审查文档；本文件是新计划，不是对旧文档的改写。
- 目标不是“让日志好看”，而是让**计划、命令、像素、产物标签**四者同源，且失败必须可见。

## 1. 修复批次

### P0-A：陈旧 cICP 覆盖（C-01 / C-15）

**问题**：源 ICC 与源 CICP 冲突时，`-map_metadata 0` 把旧 cICP 复制进产物；`PngCicpService.DecideLabel` 一见已有 cICP 就 `SkipAlreadyLabeled`，产物保留 `9/16`，像素却是 P3。

**改点**：

- `QueueProcessor.cs` PNG 3.0 段：在 `DecideLabel` 之后读回产物现有 cICP；若已有 cICP 与计划三元组不一致，强制 `WriteCicp`，由 `TryInsertCicp` 删除旧块并写计划值。
- 日志新增一条显式覆盖语句，说明旧值/计划值；不再用“已有 cICP”当作“无需标注”。
- 保留 iCCP（若存在且与计划一致）；不一致的 iCCP 是否剥离另列 P1，不在本批扩大。

**验收**：

- 重跑 `corpus/src_icc_cicp_conflict.png → PNG`，产物 cICP 必须是 `12/13`，不得是 `9/16`；`ffprobe` 的 `color_primaries/color_transfer` 与 cICP 同源。
- 同批回归 `corpus/src_p3_cicp.png → PNG`，计划与现有 cICP 已一致时不得无谓重写。

**本轮读数**：`p0_smoke/conflict_default/src_icc_cicp_conflict.png` 1665B、cICP `12/13`（源 `9/16`）；`p0_smoke/p3_cicp/src_p3_cicp.png` 1665B、cICP `12/13`。`p3_icc_png` 仍保留 `12/13` + iCCP 354B。
**未闭合**：`--color-strategy carry --color-carry-scope all` 的同一冲突样本走 `-vf iccgen`，ffmpeg 报 `Not yet implemented in FFmpeg`、exit 1、零产物；这是 CarryIcc 路径的既有缺陷，不把 P0-A 的默认路径结论扩展成 carry 路径已验证。

### P0-B：exiftool 读取 fail-closed（C-07）

**问题**：`ReadColorTagsOnceAsync` 不检查进程退出码，空 stdout 直接返回成功；外层 `catch {}` 吞异常。`code 126` 时上层按“无 ICC”继续，颜色决策漂移。

**改点**：

- `ExifToolService` 增加带状态的读取结果（Tags / Failed / Failure），保留旧 `ReadColorTagsAsync` 兼容包装。
- 单趟读取检查 `p.ExitCode`；非 0、空 stdout、JSON 解析失败都记 `Failed`，并带 stderr 摘要。
- `ReadColorTagsResultAsync` 对 `timeout || failed` 重试；两趟仍失败则返回 `Failed=true`，不缓存成“无 ICC”。
- `FfmpegCommandBuilder.ProbeInputColorMetadataCore` 把失败状态写入 `ColorMetadata.exifReadFailed`；`ColorIntentFactory` 在无 ICC、无有效 CICP、又读到 `exifReadFailed` 时**不**走 `AssumeByConvention`，返回明确原因（fail-closed）。
- `IccProfileService.ExtractIccToTempFile` 同样检查 exiftool 退出码；非 0 不按“无 ICC”静默成功。

**验收**：

- 人为把 `exiftool.exe` 替换为立即 `exit 126` 的探针（只读测试夹具，不动发布件），源无 CICP 时必须拒绝/点名，不得按 sRGB 直通。
- 正常 exiftool 下 P3 ICC→AVIF 连续 3 次不再出现 2/3 + 1/3 的标签漂移。

**本轮读数**：`FFMPEGGUI_EXIFTOOL_PATH=C:\Windows\System32\where.exe` + `src_srgb.png → png`，item exit 90，日志 `源色彩语义读取失败（exiftool: exiftool exit 2: ERROR: Invalid pattern is specified in "path:pattern".）；已拒绝按惯例假定 sRGB`。正常 `exiftool` 在本机仍偶发 `Failed to load Perl DLL "perl532.dll" code 126`；本逻辑把它变成显式失败/重试，不再当作“无 ICC”，但这也让依赖 exiftool 的门禁在环境抖动时转红（见 §4）。

### P0-C：低置信度标注尊重能力位（C-06）

**问题**：`p.UnlabeledAssumedSrgb = !caps.CanCicp` 让 TIFF 这类 `UnlabeledMeansSrgb=false` 的容器绕过能力位，产出无 ICC 的 TIFF 仍 exit 0。

**改点**：

- 低置信度分支改为 `p.UnlabeledAssumedSrgb = caps.UnlabeledMeansSrgb || !caps.HasColorPath`：有色彩通路的容器（TIFF）不再用“无 CICP 通路”倒推“可以无标注”；GIF/JXR 这类**物理无通路**容器保留例外。
- `EnsureAnnotationNotSilentlyMissing` 的“像素未改且等价 sRGB”放行条件加能力位：有色彩通路的容器必须 `UnlabeledMeansSrgb=true` 才允许；`!HasColorPath` 的物理无通路容器保留既有例外。

**验收**：

- `src_srgb.png → tiff` 不得再产出“无 ICC、无 cICP、exit 0”的 TIFF；要么附 ICC，要么 Reject（exit 91）。
- `src_srgb.png → png`、`→ webp` 的既有 sRGB 无标注路径不得回归。

**本轮读数**：`p0_smoke/srgb_tiff/run.log` exit 91、零产物；`src_srgb.png → png/webp` 仍 exit 0 且日志保留 `UnlabeledAssumedSrgb`。`verify-color-wiring` 的 `wire` 探针在补回 GIF/JXR 例外后 105/0（此前 103/2：GIF/JXR 计划被误拒）。

### P0-D：ICC 写入后置校验（C-13 / C-04 的一部分）

**问题**：TIFF HDR 首跑日志“无法生成目标 ICC”后仍 exit 0；`ExtractIccToTempFile` 失败被 `catch { return null; }` 吞掉，日志无法区分“生成失败”和“提取失败”。

**改点**：

- `GetOrGenerateStandardIcc` 在 `ExtractIccToTempFile` 返回 null 时记录明确失败原因，并对同一张已生成的 PNG 再做一次提取（只重试提取，不重跑整个 ffmpeg）。
- `QueueProcessor` TIFF 分支在 `tiffIccExit == 0` 后读回 ICC；读不到时至少 `EmitWarn` 并记录“承诺未兑现”。是否把 exit 0 改为失败，待本批读回探针落地后按结果收紧。
- `CanCarryArbitraryIcc` 与 `ExitWritesIcc` 的展示分列放到 P2，不在本批改 UI。

**验收**：

- 连续 5 次 HDR→TIFF，产物 ICC 读回必须全部成功；若某次失败，日志必须出现 `[tiff] ICC 读回失败`，不得只说“已嵌入”。
- 正常 `src_srgb.png → tiff` 在 P0-C 后要么有 ICC，要么明确拒绝，不再静默。

**本轮读数**：`p0_smoke/hdr_tiff/src_hdr_pq.tiff` 21144B；`exiftool` 首次 `perl532.dll code 126`，重试读到 `ProfileDescription=RGB built-in`、`ProfileVersion=4.4.0`。TIFF 分支的“已嵌入/读回确认”日志写在 item Log，成功项 CLI 默认不回显，因此本条只以产物读回为证据；GUI/队列日志的逐项显示仍需后续核验。

### P1-A：输入矩阵“说到做到”（C-02）

- 已选方案 A：Legacy 路径在 `-i` 之后、用户显式给了 `--color-matrix` 且输入为非 RGB、非分块流组时，追加 `scale=in_color_matrix=<token>`；实测无矩阵的对照臂与 `bt709` 臂 PSNR=28.554 dB（不再是 `inf`）。
- 输入有范围标记时一并带 `in_range/out_range`；没有范围标记时不猜。
- RGB 输入与 grouped/tiled 输入分别 `EmitWarn`：矩阵只保留容器声明、未作用于像素。
- 只对**用户显式** `options.ColorMatrix` 生效；auto/派生矩阵不加滤镜，避免改变既有 auto 像素。
- Ultra HDR 路径写死 `bt709` 的旁路仍单独点名为 C-02b，未在本批改。

### P1-B：AVIF 输出矩阵落地（C-03）

- 已落地：AVIF 显式 YUV matrix + 无 `--chroma` + 无 `--bit-depth` 时，出口强制 `-pix_fmt yuv420p`（位深跟随 `EffectiveBitDepth` 并受 AVIF 12-bit 上限钳制），并 `EmitWarn` 说明“钉 YUV 4:2:0 才能让矩阵进 nclx；gbrp identity 会静默忽略”。
- `rgb` 仍走 gbrp/identity；`xyb` 仍不覆盖 `p.OutMatrix`；两者不与 YUV token 混用。
- 实测 `src_srgb.png → avif --color-out-matrix bt709`：`pix_fmt=yuv420p`、`color_space=bt709`；`color_primaries/color_transfer` 仍为 `unknown`，说明矩阵已落地但 P/T 标签仍是该样本的既有缺口。

### P1-C：CarryIcc 语义（C-04 / C-14）

- **口径已定（2026-11-14，用户明确）**：显式选择「携带 ICC」后必须真正附 ICC，并且**不得放弃 CICP 标注**；源有 ICC 时优先改写为**能覆盖源色域的最窄标准空间**（Rec.709 / Display P3 / Rec.2020 等），不再只做“换等价标签”或“原样携带厂商 profile”。
- 实现分层（按容器物理能力，不静默择一）：
  - **能同时承载 ICC + CICP**：PNG、APNG（本轮补上 APNG 的 cICP 通路；AVIF 能力表为 true，但当前 ffmpeg 静态 AVIF 出口写不进 ICC，见下）。
  - **不能同时承载**：JXL（CICP 与 ICC 互斥）、TIFF/JPEG/WebP（无 CICP 通路）⇒ 显式 CarryIcc 若两者都要求，**fail-closed**（exit 91，零产物），并在拒绝文案里点名容器限制与可用替代；绝不静默剥掉 CICP 或 ICC。
  - **出口不写 ICC**：静态 AVIF 的 ffmpeg 出口（`ExitWritesIcc=false`）⇒ 显式 CarryIcc 同样拒绝，不用“CICP-only”冒充携带；AVIF 要真正双写需另立 avifenc 两步法出口（本批未接，已登记为后续）。
- 计划层补口：
  - `PlanCarry` 的“源无 ICC、只有 CICP”分支不再只继承 CICP：显式 CarryIcc 且容器能双承载时，用源 CICP 的 primaries/transfer 生成标准 ICC（`AttachIcc=true`、`IccPrimaries/IccTrc/IccMatrix`），同时保留 `CarryCicpLabels` 写出的 CICP。
  - `PlanCarry` 的源 ICC 分支先取 `NameableSupersetFor`（按容器 CICP 能力）的“最窄覆盖标准空间”；无 CICP 通路的 JPEG/WebP/TIFF 再退回 `NarrowestStandardGamutFor`（只按 `ContainsGamut` 选同一候选表）。源与标准空间等价时像素零改动（Action 仍为 `CarryIcc`）；标准空间更宽时由 `ColorMappingEngine.Plan` 求矩阵，Action 落 `Map`，不再把“携带”伪装成零映射。
  - 找不到覆盖标准空间（如 ProPhoto 超出 BT.2020）⇒ 保留源 ICC，并走既有 L0 超广色域保真或源 ICC 回退；不伪造 bt2020 标签。
  - 标准空间已给出目标 CICP 时**不再用源 CICP 覆盖目标标签**；源 ICC 与源 cICP 冲突的样本因此不会把 PQ/HLG 源标签留在 SDR 标准空间产物上。
  - `PlanCarry` 的源 ICC 分支在 `ExitWritesIcc=false` 时显式 CarryIcc 直接拒绝（旧行为会静默退化成 CICP-only）。
  - `EnforceContainerAnnotationRules` 对 `DeclaredStrategy==CarryIcc` 且 `!CanHaveBothIccAndCicp` 的容器改为契约拒绝，不再走“优先剥 CICP”的旧择一逻辑；Recommended 的自动择一保持不变。
- 标准 ICC 生成/落盘：
  - `IccProfileBuilder.SpaceFromDescriptor` / `SpaceFromCicp` 直接取描述符物理 xy，生成 matrix/TRC v4 ICC；`IccProfileService.GetOrGenerateStandardIcc` 优先走纯托管生成，失败才回退 `iccgen`。
  - PNG/APNG 的 ICC 后置改走 `PngCicpService.TryWriteIccp`（纯托管 zlib iCCP，保留 cICP）；不再依赖 exiftool 的 `perl532.dll` 运行时。其他容器仍走 exiftool；`EmbedIccProfileFromFileAsync` 已改为唯一临时副本 + 失败不覆盖原产物，并把 exiftool 工作目录固定在工具目录。
- 覆盖扩展：`QueueProcessor` 的 PNG 3.0 cICP 后置从 `.png` 扩到 `.apng`；APNG 与 PNG 共享同一 cICP/iCCP 并写判定（此前 APNG 只带 iCCP、没有 cICP）。
- 本轮输出格式矩阵（显式 `--color-strategy carry`；均为实测读数）：
  | 源 | 输出 | 结果 |
  |---|---|---|
  | `src_p3_icc.png`（Display P3 ICC） | PNG | exit 0；2066B，`cICP=12/13 + iCCP 389B`（托管标准 P3，ICC 616B，含 `chrm` + `cicp`） |
  | `src_p3_icc.png` | APNG | exit 0；2666B，`cICP=12/13 + iCCP 389B`（同标准 P3 ICC，ICC 内 `cicp=12/13/0/1`） |
  | `src_p3_cicp.png`（仅 CICP） | PNG / APNG | exit 0；**本轮修复后**分别 **2066B / 2666B**，`cICP=12/13 + iCCP 389B`；ICC 616B/4.4.0，`[ICC-cicp]=12/13/0/1`；日志 `standard ICC with cicp tag written (replaced iccgen profile lacking cicp)`。修复前 2031B/iCCP 354B 的 iccgen profile 只有 4.4 头、无 `cicp`，属历史读数。CICP-only → JPEG 仍按 S2 约束拒绝（exit 91，零产物；JPEG 无 CICP 通路，当前 CarryIcc 分支要求 ICC+CICP 同写，未放宽为仅 ICC）。 |
  | `src_adobe_icc.png`（Adobe RGB ICC） | PNG | exit 0；3152B，`cICP=9/1 + iCCP 378B`；计划 `Map` 到 `bt2020/bt709`，ICC 616B（与 `stdbuilt_bt2020_bt709.v44cicp.icc` 同哈希，含 `chrm` + `cicp=9/1/0/1`） |
  | 同上 | JPEG | exit 0；**1852B**，`ProfileVersion=4.4.0`，`ProfileDescription=bt2020/BT.709`，`[ICC-cicp]=9/1/0/1`（修复后不再被出口语义的 bt709 profile 覆盖；JPEG 无 CICP 通路） |
  | `src_p3_icc.png` | WebP | exit 0；1910B；ICC 4.4.0 + `[ICC-cicp]=12/13/0/1`（libwebp 丢弃 iccgen 的 ICC，引擎路径按 `NeedsPostEmbedIcc` 后置写入） |
  | `src_p3_icc.png` | JPEG | exit 0；2511B，ICC 内 `cicp=12/13/0/1`（exiftool 正常轮）；2026-11-15 后一轮 exiftool `perl532` 抖动，JPEG 后置嵌入失败，产物 2479B/无 `cicp`，属工具运行时故障，代码路径未回退 |
  | `src_prophoto_icc.png` | PNG | 走 L0 超广色域保真：1967B、iCCP 306B、无 CICP；BT.2020 不覆盖 ProPhoto ⇒ 保留源 ICC，不伪造标准标签；ICC 内不写 `cicp`（ProPhoto 不是 H.273 可命名空间） |
  | `src_icc_cicp_conflict.png` | PNG | **已闭合（2026-11-15）**：2066B，`cICP=12/13 + iCCP 389B`，ICC 616B/4.4.0，ICC 内 `cicp=12/13/0/1`；命令不再带 `-vf iccgen`。修复点：显式 CarryIcc 且源帧实际 cICP 为 PQ/HLG（源 ICC 与 cICP 冲突）时跳过 iccgen，避免 `Not yet implemented` 零产物；标准 ICC 由后置步骤写入。 |
  | AVIF / JXL | — | exit 91、零产物；AVIF 为出口不写 ICC，JXL 为 ICC/CICP 互斥 |
- 本批不改 UI/枚举；`verify-color-wiring` 的 (10) 段仍按旧 CarryIcc 契约断言，需在新语义落定后同批更新断言，**不得用放宽断言换绿**。
- 验收读数（本轮）：上述 P3/Adobe/CICP-only 格均由 CLI 独立读回 `iCCP` 大小与哈希；标准 P3 源产物 iCCP 载荷 616B、源 ICC 584B，二者不同，确认不是“换标签”；Adobe 源产物 iCCP 载荷 616B 与 `stdbuilt_bt2020_bt709.v44cicp.icc` 哈希一致。标准 ICC 现带 `chrm` 色度标签（exiftool `ChromaticityChannel1` 读回 P3 红 x=0.67999、BT.2020 红 x=0.70799）和 `cicp` 字段（exiftool 的 `[ICC-cicp] ColorPrimaries/TransferCharacteristics/MatrixCoefficients/VideoFullRangeFlag`），这是本轮补上的读者侧可度量性。

### P1-C′：源 ICC 重写 v4.4（本轮新增，用户明确要求）

**问题**：既有 `CarryIcc` 对“源图自带 ICC”只做容器搬运；源 profile 若是 v2/v4.3，产物会原样带出旧版本。仅改版本头会留下 v2 的 `desc`/`cprt` 类型和缺失的 `chad`，形成“4.4 头 + v2 tag”的混合非法 profile。

- `IccProfileBuilder.RgbSpace` 增加直接矩阵字段 `D50Colorants`（源 rXYZ/gXYZ/bXYZ，行主序）/ `D50WhitePoint` / `Chad`；`BuildCore` 有直接矩阵时不再由原色 xy 反推，避免二次 Bradford 适应；`tagWtpt` 用源白点，`tagChrm` 只在原色齐全时写。
- 新增 `IccProfileBuilder.SpaceFromIccMatrix`：从 ICC 实测矩阵 + 共享 TRC 生成 v4.4；源 profile 若为已知空间，仍用 `SpaceFromDescriptor` 的 `chrm` 原色，但矩阵覆盖为源实测矩阵（逐点保色度）。
- 新增 `IccProfileService.NormalizeSourceIccToV44`：`04 40 00 00` 且 `desc`/`cprt` 为 v4 mluc、`chad` 存在，并且已命名的源空间不欠 `cicp` tag 时才原样复用；否则按“描述符 → 直接矩阵/TRC”重建 `srcv44_v3_cicp_<hash>.icc`；LUT/设备链接/三通道 TRC 不一致无法无损重建 → 返回 null。`ExtractIccToTempFile` / `ExtractIccFromBase64` 对源 ICC 一律走该入口，失败 fail-closed（不把旧版 profile 带进产物）。
- `PngCicpService.TryWriteIccp` 在 exiftool/pngopt 退出瞬间可能遇到输出句柄共享冲突，`IOException` 按 100/200ms 退避重试 2 次；仍失败才走 exiftool/报错。`TryReadIccp` 纯托管解压 iCCP，`ExtractIccToTempFile` 优先走它，避免 exiftool 留下 `_exiftool_tmp` 后读不到产物自身 ICC；`QueueProcessor` 的 `CarryIcc` 后置读回即使 `DecideOutputColor.NeedsPostEmbedIcc=false` 也进入，发现产物带 v2 时把已重写的 v4.4 回写（PNG/APNG 走 `TryWriteIccp`，其他容器走 exiftool）。
- UI 文案（`zh-CN.json` / `en-US.json`）与 `MainWindow.xaml.cs` 注释同步为“源 ICC 先按实测色度/TRC 重写为等价 v4.4.0，再按携带策略写入；无法重建的旧版不携带”。

**实测读数（本轮）**：
- v2 ProPhoto 源 profile 单独探针：432B/2.0.0 → 592B/4.4.0，含 `desc`/`cprt`/`chad`/`chrm`；源与重写后 9 项 D50 矩阵 `max_abs_diff=0`。
- 自制 v2 未知超广色域源 `src_custom_v2.png`（432B/2.0.0，Custom Wide）→ PNG：exit 0，1947B，iCCP 286B，`ProfileVersion=4.4.0`；日志 `source ICC rewritten to v4.4.0 (was 2.0.0)`。
- 同一源 → APNG：exit 0，2547B，iCCP 286B，`ProfileVersion=4.4.0`。
- 同一源 → JPEG：exit 0，2439B，`ProfileVersion=4.4.0`（JPEG 无 CICP 通路）。
- 回归：`src_p3_icc.png → PNG` 2066B，`cICP=12/13 + iCCP 389B`，ICC 616B/`ProfileVersion=4.4.0`；`src_adobe_icc.png → PNG` 3152B，`cICP=9/1 + iCCP 378B`，ICC 616B/4.4.0。旧读数 2052B/3138B 是加入 `cicp` tag 前的历史值，不要当当前态。
- 构建：`dotnet build ffmpegPictureUI.sln -c Release -m:1` → **0 errors / 6 warnings**。

### P1-C″：ICC 内部 CICP 标注（ICC.1:2022 `cicp` tag，本轮新增，用户追加要求）

**规范依据**：ICC.1:2022 / v4.4 的 `cicpTag`（signature `'cicp'`，type `cicpType`）12 字节布局：0-3 type signature、4-7 reserved=0、8 `ColourPrimaries`、9 `TransferCharacteristics`、10 `MatrixCoefficients`、11 `VideoFullRangeFlag`，字段按 ITU-T H.273 编码；RGB/XYZ 数据空间的 `MatrixCoefficients` 必须为 0，profile class 限 Input/Display。本仓标准 ICC 为 Display(`mntr`) + RGB，合规。参考：
`https://archive.color.org/groups/hdr/ICC.1_CICP_tag.pdf`、`https://archive.color.org/specification/ICC.1-2022-05.pdf`。

- `IccProfileBuilder.RgbSpace` 增加 `CicpPrimaries/CicpTransfer/CicpMatrix/CicpFullRange` 四个字节字段与 `HasCicp`；`SpaceFromDescriptor` 用 `AvifCicp.FromSourceTokens` 复用既有 H.273 token→code 映射，RGB 时固定 `MatrixCoefficients=0`、`VideoFullRangeFlag=1`。
- `BuildCore` 新增 `BuildCicp`（12 字节 `cicpType`）并把 `cicp` 加进 tag 目录；**只在 v4.4 写**，`BuildV2` 维持 v2 结构（v2 规范无此 tag，避免混合非法 profile）。
- 标准 ICC 缓存名改为 `stdbuilt_<primaries>_<trc>.v44cicp.icc`，旧 592B 无 `cicp` 缓存视为不同产物；`BuildStandardIccFromCicp` 复用条件同时要求 `chrm` 与 `cicp` 存在。源 ICC 正常化缓存改为 `srcv44_v3_cicp_<hash>.icc` 并新增 `SourceIccNeedsCicpTag`：源已 4.4 但 H.273 可命名空间缺 `cicp` 时仍重建，不能以“版本已 4.4”为由跳过额外标注。
- `FfmpegCommandBuilder.Decision`：显式 `CarryIcc` 且源 PNG/APNG 的 cICP transfer 为 PQ(16)/HLG(18) 时跳过 `RunIccgen`。原因是源 ICC 与容器 cICP 冲突时，iccgen 读的是帧标签而非 ICC，ffmpeg 会对 PQ 报 `Not yet implemented`；跳过之后标准 ICC 仍由 `QueueProcessor` 的 `wantsGeneratedPlanIcc` 后置写入，`src_icc_cicp_conflict.png → PNG` 从零产物变为 2066B/iCCP 389B。
- 不伪造：ProPhoto/未知自定义空间无法命名 CICP（`PrimariesToken=null`），不写 `cicp`；源 ICC fail-closed 路径保持 fail-closed。
- CICP-only 的 `CarryIcc` 路径原来没有进入生成 ICC 的判定：`PlanCarry` 在该分支把 `Action` 置为 `None`（只继承 CICP），而 `QueueProcessor` 的 `wantsGeneratedPlanIcc` 旧条件要求 `Action=CarryIcc`，于是新加的后置块不触发，ffmpeg `-vf iccgen` 产出的 4.4 profile（无 `cicp`）被原样保留。修复为按 `AttachIcc + IccPrimaries + IccPath 为空 + Action∈{CarryIcc,None}` 判定“计划要求自生成标准 ICC”，并让 `CarryIcc` 后置块在“版本已 4.4 但缺 cicp”时用纯托管标准 ICC 替换，而不是按版本头直接跳过；`generatedReplacement` 只删源临时件、不删标准缓存。`Action` 限定避免了 `Map` 引擎路径被出口语义 ICC 二次覆盖：实测 Adobe RGB→JPEG 曾先后嵌入 `stdbuilt_bt2020_bt709.v44cicp.icc` 和 `stdbuilt_bt709_...`，最终把 bt2020 标成 bt709；限定后只剩目标 profile，`[ICC-cicp]=9/1/0/1`。

**实测读数（本轮）**：
- `ServiceProbe icc "Display P3"`：标准 ICC 616B/4.4.0；exiftool `[ICC-cicp] ColorPrimaries=SMPTE EG 432-1`、`TransferCharacteristics=sRGB or sYCC`、`MatrixCoefficients=Identity matrix`、`VideoFullRangeFlag=Full`。
- `ServiceProbe icc "sRGB"`：标准 ICC 612B/4.4.0；`[ICC-cicp] ColorPrimaries=BT.709`、`TransferCharacteristics=sRGB or sYCC`、`MatrixCoefficients=Identity`、`VideoFullRangeFlag=Full`。
- `src_p3_icc.png → PNG/APNG/JPEG`：PNG 2066B/iCCP 389B/`cICP=12/13`/ICC-cicp 12/13/0/1；APNG 2666B/同；JPEG 2511B/ICC-cicp 同（JPEG 无容器 cICP，ICC 标注独立存在）。
- `src_adobe_icc.png → PNG`：3152B/iCCP 378B/`cICP=9/1`/ICC 616B；ICC-cicp 9/1/0/1。
- `src_prophoto_icc.png → PNG`：1967B/iCCP 306B，无 `cicp`（ProPhoto 不是 H.273 可命名空间，不能凭空标）。
- `src_custom_v2.png → PNG`：1947B/iCCP 286B，4.4.0，无 `cicp`（未知自定义空间，按同一原则不伪造）。
- `src_p3_cicp.png → PNG/APNG`：修复后 2066B/2666B，`iCCP=389B`，`cICP=12/13`，ICC 4.4.0 且 `[ICC-cicp]` 四字段为 SMPTE EG 432-1 / sRGB or sYCC / Identity / Full；产物 `custom/cicp_only_p3e/src_p3_cicp.png`、`custom/verify_cicp_apng/src_p3_cicp.apng`。同源 PNG/APNG 的 `-vf iccgen` profile 仍先由 ffmpeg 生成，再由后置标准 ICC 替换；读取顺序在 `pngopt` 之后，故最终 iCCP 为 389B 而非 iccgen 的 354B。
- `src_adobe_icc.png → JPEG`（修复后）：1852B、4.4.0、`ProfileDescription=bt2020/BT.709`、`[ICC-cicp]=9/1/0/1`；验证 `wantsGeneratedPlanIcc` 只对 CarryIcc/None 放行，避免 Map 引擎路径再被 `dec.OutPrimaries=bt709` 的出口 ICC 覆盖。
- `src_icc_cicp_conflict.png → PNG`（修复后）：2066B，`cICP=12/13 + iCCP 389B`，ICC 616B/4.4.0；命令不再出现 `-vf iccgen`，日志 `standard ICC with cicp tag written`。修复：`FfmpegCommandBuilder.Decision` 对显式 CarryIcc 且源 PNG cICP transfer=16/18（PQ/HLG）跳过 iccgen，避免源 ICC 与 cICP 冲突时 ffmpeg 的 `Not yet implemented` 零产物。
- `verify-prophoto-faithful.ps1`：**PASS=39 / FAIL=0**（新增 §ICC cicp tag 一条）；`verify-color-wiring.ps1` 最近 149/6（exiftool 抖动；正常基线 154/5）、`verify-decision-delivery.ps1` 87/7（均无新增 CICP 回归；剩余为既有项）。
- 注意：`cicp` tag 使标准 profile 从 592B 增至 612/616B，对应 iCCP 从 375B 增至 389B；旧的 592B/iCCP 375B/3138B 读数是本轮前历史值，不再作为当前断言。

### P1-D：AVIF alpha（C-05）

- 已落地播报：普通静态 AVIF 出口在源有 alpha 且非 GIF→AVIF 时 `EmitWarn`，明确“当前 yuv 路径不带 alpha 辅助项，透明请改用 PNG/WebP 或等待 AVIF alpha path”。
- 实测 `src_rgba_trans.png → avif`：日志出现该警告，产物 324B，alpha 仍被丢弃；真正 alpha 辅助流支持另立实现批。

### P1-E：归一化提示（C-08 / C-09）

- `ColorMatch` 作为手动目标、DCI-P3/Adobe/ProPhoto 归一 BT.2020 时，计划日志/UI 明说“像素将归一 BT.2020，可能削色；不是携带原 ICC”。
- `ColorMatch` 源描述符仍为 null 的事实保留，不伪造物理色度；源 ICC 路径可用时必须点名。

### P2：可观测性与文档（C-10 / C-11 / C-12 / C-16 / C-17）

- `CanCarryArbitraryIcc` 与 `ExitWritesIcc` 分列展示。
- `MatrixTokenFor` 注释明确是“容器矩阵标签的族映射，不是真实矩阵”。
- 修正 `ColorSpaceRegistry` 顶部“最低 BT.709”的表述，区分 primaries/trc 与 matrix 族。
- 具名描述符 `PrimariesToken=null` 与 ICC 路径 `RefineKnownSpace` 的差异写进审计注释。
- 新增门禁：冲突 ICC/CICP、AVIF 显式矩阵无 chroma、输入矩阵 PSNR、AVIF alpha、TIFF 低置信度。

## 2. 执行顺序与停点

1. P0-A → 构建 → 冲突样本 + P3 CICP 回归。
2. P0-C → 构建 → sRGB→TIFF / PNG / WebP 回归。
3. P0-B → 构建 → exiftool 正常路径回归；故障注入探针另批。
4. P0-D → 构建 → HDR→TIFF 连续 5 次读回。
5. P1 按 A→B→C→D→E 顺序，每批单独构建、单独报告。
6. P2 文档/门禁最后收口。

**停点**：任何一批若导致既有 `verify-color-wiring`、`verify-decision-delivery`、`verify-prophoto-faithful` 红，先停该批，回到上一构建；不得用放宽断言换绿。

## 3. 本轮已开始

- [x] 建立计划文件。
- [x] P0-A 代码 + 冲突/P3 默认路径读数。
- [x] P0-C 代码 + sRGB→TIFF/PNG/WebP 读数。
- [x] P0-B 代码 + exiftool 故障注入读数。
- [x] P0-D 代码 + HDR→TIFF 产物 ICC 读回（单次）。
- [x] P1-A 代码 + 输入矩阵 PSNR 读数。
- [x] P1-B 代码 + AVIF `pix_fmt/color_space` 读数。
- [x] P1-D 代码 + AVIF alpha 告警读数。
- [ ] P0-B 正常 exiftool 连续 3 次（P3 ICC→AVIF）稳定性。
- [ ] P0-D 连续 5 次 HDR→TIFF 读回稳定性。
- [ ] P1-A grouped/tiled 与 RGB 输入的点名路径实测。
- [ ] P1-B 引擎出口（`--color-engine engine`）的 AVIF 矩阵读回。
- [x] P1-C（口径定稿：显式 CarryIcc 强制附 ICC + 保留 CICP；PNG/APNG 双标定已实测，AVIF/JXL 不可双承载时 fail-closed）。
- [x] P1-C′（源 ICC 一律重写等价 v4.4.0：v2 ProPhoto 与自制 v2 未知超广色域 fixture 的 PNG/APNG/JPEG 读回；D50 矩阵 max diff=0；构建 0 errors/6 warnings）。
- [x] P1-C″（ICC.1:2022 `cicp` tag：标准 P3 616B/sRGB 612B、PNG/APNG/JPEG 产物 exiftool `[ICC-cicp]` 读回、CICP-only `CarryIcc` 的 PNG 2066B/iCCP 389B/12/13/0/1 与 APNG 2666B 同形、`src_icc_cicp_conflict` 2066B 标准 ICC、`verify-prophoto-faithful` 39/0；未知/ProPhoto 不伪造）。
- [x] P1-C″ 出口覆盖：引擎路径按 `NeedsPostEmbedIcc`/源 ICC 承诺放行 webp 后置；P3→webp 1910B、ICC 4.4.0、`[ICC-cicp]=12/13/0/1`。
- [ ] P1-E（归一化提示未实施）。
- [ ] P2（能力分列、注释、注册表表述、新增门禁）。

## 4. 本轮门禁读数

**构建**：`dotnet build ffmpegPictureUI.sln -c Release -m:1` 成功，0 错误 / 6 警告（既有 5 项：`RawColorPipeline` CS8604、`MainWindow` CS8602、`QueueProcessor` CS8602、`Win32TrayIcon` CA1416/IL3002；本轮新增 1 项为色彩后置相关警告）。

| 入口 | 结果 | 说明 |
|---|---|---|
| `verify-color-wiring.ps1` | PASS=146 / FAIL=12（2026-11-15 最近一次复跑） | 该次 exiftool `perl532.dll code 126` 高频抖动，ICC 回读/ICY 断言连锁失败（`(11)` alpha 回读、`(12)` 取向、`(13)` 合并探测）；同一构建在 exiftool 正常时为 154/5（`(10)` 2、`(11)` 2、`(13)` 1）。ICC CICP 改动未新增独立失败，未用放宽断言换绿 |
| `verify-decision-delivery.ps1` | PASS=88 / FAIL=6（2026-11-15 最近一次复跑） | p3/webp 三条已转绿（引擎路径按 `NeedsPostEmbedIcc` 放行）；剩余 `⑯` 降档点名、`⑰b legacy` 4 项（16-bit AVIF + legacy 退 91，与本轮 ICC 无关）、`⑱-4` 形状锁；ICC CICP 改动未新增失败 |
| `verify-prophoto-faithful.ps1` | PASS=39 / FAIL=0（P1-C″ 后复跑） | 通过；新增 `§ICC cicp tag` 一条（Display P3 标准 ICC 4.4.0 + CICP=12/13/0/1）；ProPhoto 像素逐位一致、AVIF 不谎报 ICC、carry→PNG 的 ICC 与源逐字节相同 |
| `verify-color-wiring` 内的 `thumbnail` 探针 | FAIL=1/2 | 缩略图声明-数据闭合；与本轮矩阵改动无直接关系，单独登记 |
| `verify-color-wiring` 内的 `bench` 探针 | FAIL=1/3 | 性能阈值读数受负载影响；不调阈值换绿 |

**未跑**：`verify-color-strategy`、`verify-png-optimizer`、全量 104 条。按仓库硬规矩⑥，全量入口只在用户明确要求时使用。

> **磁盘满阻断（2026-11-15 00:xx）**：最后一次 `verify-color-wiring` 启动时 C: 盘无剩余空间，`PowerShell` 与 `computer_use_tool` 均报 `write non-interactive command script failed: no space on device (system_error=28)`，故 `chrm` 补丁后的最终 `verify-color-wiring` 未能重跑；已按 `_sweep-output.py --runs --max-age-days 3 --apply` 清掉 215 个陈旧轮次目录，释放约 6.93 GB 候选空间，但卷级剩余仍低于 1 GB。上表读数为可用证据的最后一轮，不冒充 `chrm` 后全绿。
>
> **2026-11-15 01:xx 续记**：卷级剩余已恢复至约 24.4 GB；上表 154/6 与 38/0 是清理后实际复跑读数。`verify-wiring` 的 6 条失败中仍以 JXL/JXR 旧契约与 exiftool 抖动为主，未把 P1-C′ 的源 ICC 重写算作通过。 
>
> **2026-11-15 02:xx P1-C″ 续记**：ICC 内嵌 `cicp` tag 落地后再复跑：`verify-prophoto-faithful` 39/0（新增 §ICC cicp tag 通过）；`verify-color-wiring` 154/5（较前少 1 条，CICP 未新增失败）；`verify-decision-delivery` 87/7（无新增失败）。标准 P3 ICC 616B、sRGB 612B；PNG 的 iCCP 由 375B 增至 389B，Adobe PNG 3152B/iCCP 378B。旧 592B/375B/3138B 读数是 P1-C″ 前历史值，不得混用。
>
> **2026-11-15 03:xx CICP-only / 冲突样本续记**：`wantsGeneratedPlanIcc` 改为 `AttachIcc+IccPrimaries+IccPath 空+Action∈{CarryIcc,None}`，CICP-only 的 PNG 从 2031B/iCCP 354B（iccgen 4.4 无 `cicp`）变为 2066B/iCCP 389B，ICC 616B/4.4.0 且 `[ICC-cicp]=12/13/0/1`；APNG 2666B 同形。`src_icc_cicp_conflict.png` 显式 carry 从 ffmpeg `-vf iccgen` 的 `Not yet implemented` 零产物，改为跳过 iccgen（Detection 读 PNG cICP transfer=16/18）+ 后置标准 ICC，2066B/iCCP 389B 成功。Adobe RGB→JPEG 也改为 1852B/4.4.0/`bt2020/BT.709`/`[ICC-cicp]=9/1/0/1`，不再被出口语义的 bt709 profile 覆盖。worst-case exiftool 抖动下 `verify-color-wiring` 146/12，正常 exiftool 基线 154/5；未把环境抖动算作本批回归。
>
> **2026-11-15 04:xx 引擎后置续记**：`QueueProcessor` 的引擎早退条件从只看 `wantsGeneratedPlanIcc` 改为 `wantsGeneratedPlanIcc || dec.NeedsPostEmbedIcc || planPromisesSourceIcc`；P3→WebP（默认策略）恢复 1910B、ICC 4.4.0、`[ICC-cicp]=12/13/0/1`，`verify-decision-delivery` 从 85/9（同构建首轮）回到 **88/6**；Adobe RGB→JPEG 仍 1852B/4.4.0/bt2020+BT.709，未被 bt709 覆盖。剩余 6 条为 `⑯` 降档点名、`⑰b legacy` 4 条（`--bit-depth 12` + legacy 撞 16-bit AVIF 自研出口的 exit 91，不是 ICC 回归）、`⑱-4` 形状锁。

## 5. 2026-11-15 统一口径续记（最新）

### 5.1 口径落点

- **CICP 与 ICC 不冲突**：同一产物只允许一组同源描述；计划承诺 ICC 时，容器 cICP 必须与
  `IccPrimaries/IccTrc` 完全一致，不一致以 ICC 为准同步；原始源 ICC 无可命名令牌时不猜 cICP。
- **CICP 无法表达才用 ICC**：PNG/APNG 可双写；JXL 互斥，CICP 可表达 ⇒ CICP-only，不可表达
  ⇒ ICC-only；静态 AVIF 出口不写 ICC，可命名 ⇒ CICP-only，不可命名 ⇒ exit 91；JPEG/WebP/TIFF
  容器无 cICP 通路，由标准 ICC + ICC 内 `cicp` 承担。
- **显式 `CarryIcc` 不再因容器不能双写而 fail-closed**：按容器能力落 ICC+CICP / ICC-only /
  CICP-only；CICP-only 源到 JPEG/WebP/TIFF 也改为生成等价标准 ICC，不再误拒。
- **源 ICC 优先标准空间**：`NameableSupersetFor`/`NarrowestStandardGamutFor` 选“覆盖源色域的最窄
  标准空间”（Rec.709 / Display P3 / BT.2020）；等价则像素零改动，更宽则矩阵映射并登记损失。
- **源 ICC 一律 v4.4**：源正常化缓存 `srcv44_v3_cicp_*`；P3 616B/4.4.0 + `cicp`，ProPhoto
  544B/4.4.0 无 `cicp`；标准缓存 `stdbuilt_*.v44cicp.icc`。
- **ICC 内写 `cicp`**：12B，signature/type `'cicp'`，P3=`12/13/0/1`、BT.2020 SDR=`9/1/0/1`、
  sRGB=`1/13/0/1`；RGB/XYZ 的 MatrixCoefficients=0。

### 5.2 本轮实测读数（`tests\output\_tmp\colormatrix-analysis\unify*`）

| 用例 | 结果 |
|---|---|
| CICP-only P3→JPEG | exit 0，2511B，ICC 4.4.0 + `[ICC-cicp]=12/13/0/1` |
| CICP-only P3→WebP | exit 0，1910B，ICC 4.4.0 + `[ICC-cicp]=12/13/0/1` |
| CICP-only P3→TIFF | exit 0，12900B，ICC 已嵌入 |
| ICC+cICP 冲突→PNG | exit 0，2066B，iCCP 389B + cICP 12/13（源旧 9/16 被覆盖） |
| ICC P3→PNG | exit 0，2066B，iCCP 389B + cICP 12/13 |
| ICC P3→JXL | exit 0，1832B，jxlinfo P3/sRGB，无 ICC（CICP-only） |
| ICC P3→AVIF | exit 0，1797B，ffprobe smpte432/iec61966-2-1，无 ICC（CICP-only） |
| Adobe ICC→JPEG | exit 0，1852B，4.4.0，bt2020/BT.709，`[ICC-cicp]=9/1/0/1` |
| Adobe ICC→JXL | exit 0，1807B，jxlinfo Rec.2100/709，无 ICC |
| ProPhoto ICC→PNG | exit 0，1967B，iCCP 306B，无 cICP，ICC 4.4.0 |
| ProPhoto ICC→JXL | exit 0，2465B，544B ICC，无 cICP（ICC-only） |
| ProPhoto ICC→AVIF | exit 1（91），零产物（不可命名 + 出口不写 ICC） |

### 5.3 ICC 结构/体积审计

已落盘：`docs\review-2026-11-15\ICC_STANDARD_AUDIT_2026-11-15.md`。核心结论：
标准 P3 616B / sRGB 612B / BT.2020 SDR 616B / ProPhoto 544B；12 tag，v4.4.0，
`desc`/`cprt` 为 `mluc`，`chad`/`chrm`/`cicp` 在位；`ProfileID=0` 是 ICC 允许的未计算值，
`ProfileCMMType` 空是可选字段；616B 是“保留 `chrm` + 完整 desc/cprt + cicp”的紧凑形态，
`icc_simplify` 516B 会削可选 tag，不建议作为默认交付。

### 5.4 门禁

- `verify-prophoto-faithful.ps1`：**PASS=39 / FAIL=0**（正向锁改为只保留 128B 头，修掉旧
  “一半截断 exiftool 不报 Bad length”的失效锁；carry→AVIF 仍按契约拒绝、carry→PNG 逐字节同源）。
- `verify-color-wiring.ps1`：**PASS=160 / FAIL=2**（JXL 旧断言改后复跑；剩余两条都是
  `(11)`：16-bit 容器像素格式回读因 exiftool `perl532` 抖动读空、中转目录 62→68 未清理；
  均非本轮 ICC/CICP 改动直接引入，未用放宽断言换绿）。
- `verify-decision-delivery.ps1`：**PASS=86 / FAIL=8**（其中 `⑫`/`⑭` 是 exiftool `perl532`
  抖动导致 ICC 红原色读空；其余 6 条为既有的 `⑯` 降档点名、`⑰b legacy` 4 条与 `⑱-4` 形状锁）。
- 未跑 `verify-color-strategy`、`verify-png-optimizer` 与全量；按仓库硬规矩⑥不自行全量。


### 5.5 仍未闭合

- `--icc-file` 外部 ICC：仍只作“源空间判定”，未进入 v4.4+`cicp` 重写/携带；
  `--icc-file + carry → PNG` 实测仍 Reject（源无内嵌 ICC 可携带）。
- 静态 AVIF 的 ICC-only 需 avifenc 两步法，未接。
- `ProfileID` 可选计算（会改变所有缓存/产物字节）未做。
- `verify-color-wiring` 的 `(11)/(12)/(13)` 非本轮失败需下一轮单独收口。

