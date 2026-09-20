# JXL 管线问题清单与交接材料（2026-09-15 分析，同日已修复 P0 全部 + P1-4/5/6）

## 色彩矩阵子系统补全（2026-09-16，构建 0/0 + 门禁 44/0、19/0 通过）

> 缺口分析六项盘点后施工，全部完成：
> - ✅ P1-① HLG OOTF 接线：`TransformSpec` 增加 `SrcIsHlgScene/DstIsHlgScene/HlgSystemGamma` 字段；PlanCore 按 HLG 曲线装配（gamma 取自 `Bt2100Ootf.SystemGamma(名义峰值)`，与 PQ 同一 nits 来源）；内核解码后 Apply（场景光→显示光）、编码前 ApplyInverse。PQ/其他路径零改动。
> - ✅ P1-② OutMatrix 白名单补 `bt2020ncl`/`ycgco`/`ycgcocn`/`ycgcocs`（FfmpegCommandBuilder.cs:653）。
> - ✅ P2-④ DCI-P3 ICC 生成条目：`IccgenPrimaries = null`（影院白无法用标准 primaries 键表达，PreserveAll 对该 Spec 不再生成错误色域 ICC；消费方核实为 ffmpeg iccgen 链）。
> - ✅ P2-③ UI ColorMatrixCombo 下拉补全 10 个矩阵 token（与引擎白名单对齐）。
> - ✅ P3-⑥ ictcp 死条目删除；`MapPixFmt` 核实已有 gbrp/rgb24/rgb48 RGB 分支，无需改。
> - 遗留（低优）：P2-⑤ P3 原生输出路径（OutPrimaries 无 P3 选项，P3→P3 被 BT.2020 折返）未做，需 Spec 扩展。

## 全量审查与实机测试结论（2026-09-15 下午，交接重点）

### 实机测试结果（ffmpeg git-2026-08-06 libjxl + cjxl/djxl v0.11.2 自编译，测试目录 <test-dir>）

| 测试 | 结果 |
|---|---|
| djxl 直接输出 .jxl | ✅ 确认失败（"can't decode to the file extension '.jxl'"）——P0-1 修复前提成立 |
| JPEG 重构型 JXL → ppm 流 → cjxl -d 0（奇/偶尺寸） | ✅ 重编码成功，djxl 可再解码验证 |
| djxl→cjpegli 管道 + `-p 2 --noadaptive_quantization` | ✅ 产出 Progressive yuvj444p JPEG |
| 最长边 JXL 无损中转（3000x2250→scale） | ✅ 编码成功；**但暴露 P0-N1：scale 分支写反**（见下） |
| cjpegli 参数逐一验证 | ✅ `--noadaptive_quantization`（32717 vs 30955 字节）、`--chroma_subsampling 420`（yuvj420p）、`-p 2`（Progressive）、`--fixed_code`（必须配 `-p 0`，现有降级逻辑正确）全部生效 |

### 新发现（全量审查，已人工复核，按严重度排序）

> **✅ 修复状态（2026-09-15 晚，`dotnet build` 0 警告 0 错误 + 实机复测通过）**：
> P0-N1 ✅（分支对调，实测 3000x2250→2000x1500、纵向 1000x2000→750x1500）；
> P0-N2 ✅（JxlPipelineService 双管道并发泵）+ PipeDjxlToCjxlAsync 核实 stdout 已有独立消费者、无死锁，仅修裸等待；
> P0-N3 ✅（QueueProcessor.TryDeleteIcc 委托带保护的 IccProfileService 版本）；
> P1-N4 ✅（三处 CancellationToken.None 等待全部改 linkedToken + 取消即 Kill）；
> P1-N5 ✅（IsAnimated 对 .jxl 输入做 ffprobe count_frames 探测，带缓存）；
> P1-N6 ✅（MaxDimension 预缩放跳过动图输入，含动画 AVIF 探测）；
> P1-N7 ✅（HLG 注入 Rec2100HLG）；
> P1-N8 ✅（para type2 c/e 参数归位）；
> P1-N9 ✅（DCI-P3 Spec Wp=0.314/0.351，Display P3 保持 D65）；
> P2 已修 4 项：djxl threads 透传、管道超时 10→30 分钟对齐、截断输出清理（CleanupTruncatedOutput）、icc_embed 临时 PNG finally 清理。
> **仍未修（P2 遗留，交接后可做）**：JxrService 无超时守卫、RawService 取消孤儿进程、CjxlService.RunAsync 死代码、多处 RunAsync 未传 ct、djxl→cjxl 管道未接超广色域 ICC 保真、HLG 色调映射未接 BT.2100 OOTF、DjxlService/CjpegliService 5 分钟硬超时。

**P0-N1 scale 滤镜横竖分支写反** — `FfmpegCommandBuilder.cs:729-731`
`w>=h` 分支生成 `scale=-2:{maxLen}` 锁的是**高度**（短边）。实测 3000x2250 → 2666x2000（最长边 2666 > 限制 2000），任何非方形图都超限。实机测试直接复现。修复：`w>=h → scale={maxLen}:-2`（锁宽），`w<h → scale=-2:{maxLen}`（锁高）。影响最长边预缩放（QueueProcessor.cs:345）与主命令两条路。

**P0-N2 djxl→cjpegli 管道双管道死锁** — `JxlPipelineService.cs:97-118`
先 `await` 完成 djxl stdout→cjpegli stdin 拷贝，之后才开始读 cjpegli stdout。输出超过管道缓冲（~64KB）时 cjpegli 阻塞在 stdout 写、停止消费 stdin → djxl 阻塞 → 死锁到 5 分钟超时。修复：stdout→文件拷贝与 stdin 拷贝并发执行，全部完成后再关 stdin 等退出。注意：`PipeDjxlToCjxlAsync`（QueueProcessor.cs）是先 `CopyToAsync` 后 `WaitForExitAsync`，同样存在此型风险，需一并修。

**P0-N3 管道路径可能删除用户权威 ICC** — `QueueProcessor.cs:2127-2175` + `3547`
QueueProcessor 私有 `TryDeleteIcc`（:3547）无条件删除；而 `BuildPipeColorArgs`（:3329）经 `ResolveFaithfulUltraWideIcc → GetFaithfulIcc → FindUserNamedIcc` 拿到的可能是 `PLAN/iccs/` 下**用户提供的 ICC**。`IccProfileService.TryDeleteIcc`（IccProfileService.cs:73）明确注释共享 ICC"绝不删除"并有 `IsSharedNamedIcc` 保护，但 QueueProcessor 的私有同名方法绕过了它。修复：QueueProcessor 删除处改调 `IccProfileService.TryDeleteIcc`，或仅删除确认为本任务生成的临时 ICC。

**P1-N4 管道等待用 CancellationToken.None 无限挂起** — `QueueProcessor.cs:3013（同型 2287/3263）`
取消/超时/传输异常后 `WaitForExitAsync(CancellationToken.None)`：djxl 阻塞在无读者的 stdout，含 Kill 的 finally 永不可达 → 任务永久挂起。修复：等待用 linkedToken；取消路径先 `Kill(true)` 再收尸。

**P1-N5 动图 JXL 不被识别** — `QueueProcessor.cs:2631/3853-3863`
`IsAnimated` 只查 `.gif/.apng`/输出格式/帧率，动图 JXL 落入单帧管线静默丢帧。修复：`.jxl` 输入用 ffprobe 帧数或容器 box 检测动画。

**P1-N6 MaxDimension 预缩放破坏动图输入** — `QueueProcessor.cs:341-407` vs `:221`
GIF/动画 AVIF 被缩成单帧 PNG，但 `inputExt` 在预缩放前已捕获，仍路由进动图两步法。修复：动图输入跳过预缩放，scale 滤镜并入动图管线。

**P1-N7 HLG JXL 被误标 PQ** — `QueueProcessor.cs:3140-3144`
HLG（`arib-std-b67`）与 PQ 一起注入 `DecodedUltraHdrColorSpace="Rec2100PQ"` → 显示端 EOTF 错误。修复：按 `colorTrc` 区分注入 `Rec2100HLG`。

**P1-N8 ICC para type-2 曲线参数错位** — `IccProfileService.cs`（ReadIccCurve case 2）
ICC parametricCurveType 函数 2 应为 `(aX+b)^g for X≥−b/a; cX for X<−b/a`，代码把 c 写进 e 位（`FromParametric(g2, a, b, 0, -b/a, cc, 0)`）：线性 toe 段丢失（c=0），幂段被加偏移（E=cc）。部分 Adobe RGB ICC 会解码错误。修复：改 `(g2, a, bb, cc, -bb/a, 0, 0)`。

**P1-N9 DCI-P3 Spec 白点用 D65** — `ColorSpaceRegistry.cs`（Spec "DCI-P3" Wp=D65）
DCI 剧场白点是 (0.314, 0.351)，PrimariesTable 的 smpte431 是对的，但 Spec 用 D65 → `LinearMatrix("DCI-P3",...)` 矩阵转换有轻微色偏。Display P3 用 D65 是对的，别一起改。修复：DCI-P3 Spec 白点改 0.314/0.351。

**P2 集合**（一览）
- `DjxlService.RunAsync` threads 参数被静默忽略（DjxlService.cs:139）
- `JxrService` 调用无超时守卫，可无限阻塞队列（JxrService.cs:173；QueueProcessor.cs:1663 未传 ct）
- `RawService` 取消时 dngtool 成孤儿进程（RawService.cs:235,248）
- 管道硬编码 5/10 分钟超时偏短，`CjxlService` 已是 30 分钟，不一致（JxlPipelineService.cs:118、DjxlService.cs:154、QueueProcessor.cs:2198）
- `ProcessCjxlAsync` 的 `icc_embed_{Guid}.png` 输入副本从不删除（QueueProcessor.cs:1272 附近）
- 失败/取消后残留截断输出文件不清理（JxlPipelineService.cs:118）
- `CjxlService.RunAsync` 遗留死代码硬编码 `--lossless_jpeg=1`（CjxlService.cs:208）
- 多处 `FfmpegRunner.RunAsync` 未传 ct，"停止"后子进程跑到自然结束
- djxl→cjxl 管道未接超广色域 ICC 保真逻辑（ProPhoto 源被误标 sRGB，QueueProcessor.cs:2943）
- HLG 色调映射未接 BT.2100 OOTF（ColorStrategyPlanning 自注释已知，Bt2100Ootf 类闲置）

### 建议修复顺序（交接给下一个 agent）

1. P0-N1（一行对调，实机已复现，最快见效）
2. P0-N3（用户数据安全，改一处调用即可）
3. P0-N2 + P1-N4（同一类管道时序问题，建议一起修）
4. P1-N5/N6/N7/N8/N9
5. P2 集合酌情

---

> 来源：针对「输入 JXL → 输出 JXL 崩溃」「JXL2JPEG + 最长边限制爆炸」「cjpegli 高级参数不生效」三类问题的代码级排查。
> **修复状态（2026-09-15，`dotnet build` 0 警告 0 错误通过）**：
> - ✅ P0-1 已修：`ProcessJxlInputAsync` 增加 `effectiveType` 重路由（QueueProcessor.cs），JXL→JXL 的 JPEG 重构型输入改走解码→重编码管线；命令预览 `BuildJxlInputCommand` 同步一致（含审查补修的第二分支判断）。
> - ✅ P0-2 已修：最长边预缩放对 JXL 输入改用 JXL 无损中转（`-distance 0`），保留 HDR/广色域并使 DetectType 可识别；失败回退 PNG 并日志明示削顶风险。**但 scale 滤镜本身写反（P0-N1），修复后才能真正生效**。
> - ✅ P0-3 已修：`TryPipeDjxlToCjpegliAsync` 改签名为接收完整 `FfmpegOptions`，参数统一由 `BuildCjpegliArguments` 生成（新增 `includeThreads` 开关），色度采样/渐进/fixed_code/自适应量化/ICC 全部生效（实机验证通过）。
> - ✅ P1-4 已修：预览 djxl 输出 `--output_format=png` → `pnm`，与实际执行一致。
> - ✅ P1-5 已修：`JxlInspector.DetectType` 重写为 ISOBMFF box 长度链完整遍历（jbrd 优先），窗口扫描降为兜底。
> - ✅ P1-6 部分：djxl→cjxl 无损语义明示日志已加；色彩探测失败静默 sRGB 化的根本问题未动（见 P2 集合 djxl→cjxl ICC 条目）。
> - ⏳ 未动：`--psnr`/`--jpeg_encoder` 为 libjxl 上游移除，非缺陷。

以下为原始分析（file:line 基于修复前快照，修复处已在上述清单标注）。

## P0-1 JXL→JXL：JPEG 重封装型输入被 djxl 强行输出 .jxl（崩溃/错标主现场）

- 位置：`src/FfmpegGui/Services/QueueProcessor.cs:2557-2568`（`ProcessJxlInputAsync` 的 `JpegReconstruction` 分支）
- 现象：该分支**不检查 `targetFmt`**，直接 `djxl input.jxl output.jxl`。djxl 按扩展名决定输出格式，不支持输出 `.jxl` → 退出码非零（报"失败"）；部分版本会写出 JPEG 内容到 `.jxl` 名下（错标文件）。
- 修复方向：`targetFmt == "jxl"` 时不应进入重构分支，改走 NativeCodestream 的重编码路径（`PipeDjxlToCjxlAsync`，QueueProcessor.cs:2617）；或先无损还原 JPEG 再 `cjxl --lossless_jpeg=1` 二次无损封装。
- 关联：`targetFmt` 在 :2546 已取得，仅差一个条件判断。

## P0-2 JXL→JPEG + 最长边限制：预缩放偷换输入类型 + HDR 削顶

- 位置：
  - 预处理：`QueueProcessor.cs:341-386`（统一 ffmpeg 缩放，中间文件固定 `.png`，:354）
  - 类型探测：`src/FfmpegGui/Services/JxlInspector.cs:20-45`（仅识别 ISOBMFF `JXL ` / 裸码流 `FF 0A`）
- 现象链：
  1. 预缩放把 JXL 换成 PNG 临时文件，但 `inputExt` 在替换前已捕获（QueueProcessor.cs:221），仍路由进 `ProcessJxlInputAsync`；
  2. `DetectType` 读 PNG magic → 返回 `Unknown` → 落入兜底 ffmpeg 分支（QueueProcessor.cs:2683-2691）；
  3. djxl→cjpegli 管道、cjxl 管道全部被绕过，cjpegli 高级参数也不生效（见 P1-1）。
- "爆炸"点：HDR（PQ/HLG）JXL 被塞进 PNG 中转——PNG 表达不了 PQ TRC，高光削顶；广色域仅靠 ffprobe 探测回注（:359-364），探测失败即静默 sRGB 化。
- 修复方向：预处理中间文件按目标选 PFM/PAM（HDR 保留）或至少 JXL（djxl 可读）；或在预缩放后显式重设路由标记。

## P0-3 JXL→JPEG：cjpegli 管道绕过 BuildCjpegliArguments，高级参数全丢

- 位置：`src/FfmpegGui/Services/JxlPipelineService.cs:43-47`
- 现象：`TryPipeDjxlToCjpegliAsync` 手拼参数只有 `--distance` + `-x color_space`。以下参数在该路径（JXL 输入转 JPEG 的**唯一主路径**）全部失效：
  - `CjpegliChromaSubsampling`（色度采样）
  - `CjpegliProgressiveId`（渐进模式）
  - `CjpegliOptimize` / `--fixed_code`
  - `CjpegliAdaptiveQuant` / `--noadaptive_quantization`
  - ICC 路径（`iccPath` 参数从未传入）
  - `threads` 收下后被有意忽略（:44 注释）
- 对照：`CjpegliService.BuildCjpegliArguments`（CjpegliService.cs:249-315）完整生成这些参数，但该管道没有调用它。
- 修复方向：管道内改用 `BuildCjpegliArguments("-", outputPath, item.Options, jxlMeta)`，与 `PipeFfmpegToCjpegliAsync`（QueueProcessor.cs:2130）对齐。

## P1-4 命令预览与实际执行不一致（djxl PNG 输出已失效）

- 位置：`src/FfmpegGui/MainWindow.xaml.cs:3088`
- 现象：预览生成 `djxl in.jxl --output_format=png - | cjpegli ...`，但 QueueProcessor.cs:2651 注释已确认 djxl v0.12+ 不支持 PNG 输出（实际执行走 `.pnm`）。用户复制的命令必失败。
- 修复方向：预览改为 `--output_format=pnm`（或与 JxlPipelineService 一致的 ppm/pam 逻辑）。

## P1-5 JXL 类型探测只有 64KB 窗口，jbrd box 可能漏检

- 位置：`JxlInspector.cs:25-37`
- 现象：只读前 64KB 找 `jbrd` box；大元数据 box（exif/xml/大 ICC）排在前面的文件会被误判为 `NativeCodestream`，本应无损还原的文件被有损重编码。
- 修复方向：解析 ISOBMFF box 长度链遍历（而非固定窗口字节搜索），或至少扩大窗口 + 提示不确定性。

## P1-6 djxl→cjxl 管道（JXL→JXL 主路径）的 HDR/无损语义风险

- 位置：`QueueProcessor.cs:2885-2983`
- 现象：
  - PPM/PAM 裸流不携带色彩，仅靠 `ProbeInputColorMetadata` 探测 + `-x color_space` 补标（:2896-2897, 2908）；探测失败 → 静默 sRGB 化。
  - 用户勾「无损」得到的是"解码像素级无损"，与源码流并非位等价；无日志说明语义差异。
  - `ProbeInputColorMetadata` 对奇偶尺寸无影响，但奇数尺寸 JXL 经此管道往返安全；偶数化偏移只发生在最长边 `-2` 缩放（FfmpegCommandBuilder.cs:720-728），带来 ±1px 宽高比偏差，且与"不超限直接原图"（:717-718）造成同批文件行为不一致。
  - 缩放失败时静默继续用原图（QueueProcessor.cs:382），同一任务可能走出与预期不同的路径。

## 其他已核实（非缺陷）

- `--psnr` / `--jpeg_encoder`（cjpegli sjpeg 后端）已随新版 libjxl 移除，CjpegliService.cs:280-284 有注释说明，属有意行为。
- ffmpeg libjxl 静图分支参数（distance/effort/modular）传递完整（FfmpegCommandBuilder.cs:430-490）。
- JXL 为 RGB 原生格式，不写 YUV 矩阵标签（FfmpegCommandBuilder.cs:28-31），奇偶尺寸对 JXL 目标本身无编码器约束。

## 建议修复顺序

1. P0-1（一行条件判断，止血 JXL→JXL 崩溃）
2. P0-3（管道改用 BuildCjpegliArguments，解决参数不生效）
3. P0-2（预缩放中间格式 + 路由标记）
4. P1-4 / P1-5 / P1-6（一致性收尾）
