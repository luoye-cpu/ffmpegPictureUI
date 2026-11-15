# 色彩矩阵与输入/输出组合独立分析（2026-11-14）

> 本文是一次**独立复核**：结论只来自本轮的源码阅读、纯托管探针、`ServiceProbe`、以及端到端产物的 `ffprobe` / `exiftool` / `pngcicp` 读数。  
> `docs/review-2026-11-14/` 下已有的 `ICC_VERSION_COMPARISON.md`、`COLOR_UI_LINKAGE_AUDIT.md`、`COLOR_ANNOTATION_PRIORITY.md` 等**没有**被当作本轮结论依据；本文也不改写它们。
> 证据根：`C:\PLAN\ffmpegPictureUI\tests\output\_tmp\colormatrix-analysis\`。

---

## 0. 结论摘要

### 0.1 一句话结论

**3×3 矩阵数学层是自洽且可用的；真正的问题不在矩阵算式，而在“矩阵语义 → 容器标签 → 实际产物”这条链路上：输入矩阵多数情况下没有像素作用，输出矩阵的 `Carried` 不保证落地，源元数据复制会绕过规划层写出与像素不一致的 CICP，且 ICC 探测存在 fail-open 的偶发失败。**

### 0.2 最重要的发现（按严重度）

| # | 结论 | 级别 | 关键证据 |
|---|---|---|---|
| 1 | 源同时带 ICC 与冲突 CICP 时，产物会保留源 cICP（`9/16` = BT.2020/PQ），而计划和像素是 P3；产物没有 iCCP。 | **高** | `grid3/conflict_to_png/src_icc_cicp_conflict.png`：源 iCCP 363B + cICP 9/16；产物仅 cICP 9/16、无 iCCP；`pngcicp info` / `ffprobe` 均读 `bt2020/smpte2084` |
| 2 | `--color-matrix` 的输入矩阵声明在实测 JPEG/AVIF 上**不改变像素**；`-colorspace` 放在 `-i` 前只是元数据，真正的输入矩阵转换要 `scale=in_color_matrix=`。 | **高** | `src_yuv_untagged.jpg` 的 `bt601` vs `bt709` 产物 PSNR=inf；手动加 `scale=in_color_matrix=bt709` 后 PSNR=28.55 |
| 3 | `ResolveOutMatrixCapability` 判 `Carried` 不等于产物 nclx 真的写入了该矩阵。AVIF 默认 `gbrp` 时 `-colorspace` 被忽略，nclx 保持 identity。 | **高** | `matrix/outmatrix_avif_bt709/src_srgb.avif`：`pix_fmt=gbrp`、`color_space=gbr`；仅 `--chroma 4:2:0 --bit-depth 8` 后才 `yuv420p/color_space=bt709` |
| 4 | 显式 `CarryIcc` 在“目标可精确 CICP 命名”的出口上并不会主动附 ICC；P3 ICC→PNG 的日志写“源 ICC 未携带”，但产物实际有 iCCP 354B，这是 `-map_metadata 0` 的副产物，不是计划承诺。 | **中高** | `grid3/p3_icc_png_logged/src_p3_icc.png`：cICP 12/13 + iCCP 354B；日志明确“CICP 已足以正确承载” |
| 5 | `exiftool` ICC 读取存在 fail-open：`ReadColorTagsOnceAsync` 空 stdout 直接返回 false，不检查退出码；外层 `catch {}` 吞异常。实测 `exiftool -ver` 10 次 **6 成功 / 4 失败**（`perl532.dll code 126`），导致同一输入的颜色决策在多次运行间漂移。 | **高** | `src_p3_icc.png → avif`：auto 3 次中 2 次 P3、1 次 bt709；`exiftool -ver` 10 次 6/4；TIFF HDR 首跑无 ICC、3 次重跑全部成功 |
| 6 | AVIF 静态出口丢 alpha 且**没有警告**；`RawColorPipeline` 只在增益图出口点名，普通静态 AVIF 出口没走那条提示。 | **中高** | `alpha/rgba_to_avif/src_rgba_trans.avif`：324B、`yuv420p`、无 alpha 辅助流；日志无 alpha/透明警告 |
| 7 | 低置信度源（无 ICC/无 CICP）走“惯例 sRGB”时，`p.UnlabeledAssumedSrgb = !caps.CanCicp` 绕过了 `UnlabeledMeansSrgb` 能力位；TIFF 因此可以出**无 ICC 的未标注 TIFF**，而 TIFF 的能力表明确写 `UnlabeledMeansSrgb=false`。 | **中高** | `grid/src_srgb_to_tiff/src_srgb.tiff`：rgb24、无 ICC、exit 0；`ColorTransformPlan.cs` 能力表 tiff `UnlabeledMeansSrgb=false`；`ColorStrategyPlanning.cs:905-914` |
| 8 | `ColorMatch RGB` 没有物理色度（`Rp/Gp/Bp/Wp` 全缺），`FromNamedSpace` 返回 NULL；手动目标不拒绝，输出 cICP `9/1`（BT.2020/SDR），即被静默当作 BT.2020。 | **中** | `run.log`：`ColorMatch RGB,NULL`；`manual/ColorMatch_RGB/src_srgb.png` cICP `9/1` |
| 9 | ProPhoto→AVIF/JXL 不是“携带 ICC”，而是归一 BT.2020：AVIF `bt2020/bt709`、JXL `bt2020/iec61966`，无 ProPhoto ICC；AVIF 可能削色。`CanCarryArbitraryIcc=true` 不能推出“会携带”。 | **中** | `prophoto/to_avif`、`to_jxl` 的 ffprobe；`ColorIntentFactory.cs` AVIF `ExitWritesIcc=false` |
| 10 | 输出矩阵 token 是**族查表**，不是真实 3×3：`MatrixTokenFor` 把 `smpte432/smpte431→bt709`、`bt470bg/smpte170m/bt470m/jedec-p22→smpte170m`；P3/DCI 没有专属 H.273 matrix token。 | **中** | `ColorTransformPlan.cs:1232-1238`；9×9 矩阵显示这些空间之间并非 identity |
| 11 | `jpg/webp` 输出矩阵被 `ContainerYuvMatrix` 强制成 `bt470bg`；而 `MatrixTokenFor` 对 `smpte170m` 也给 `smpte170m`。计划和命令的 token 字符串不一致，只是同属 601 族。 | **中低** | `ColorSpaceRegistry.cs:671-675`；`ColorTransformPlan.cs:1232-1238` |
| 12 | `ColorSpaceRegistry` 顶部注释写“软件最低 BT.709，BT.601/bt470m/bt470bg/smpte170m 一律归入 BT.709”，但代码实际保留并使用 601 族输出矩阵。 | **低（文档/契约漂移）** | `ColorSpaceRegistry.cs:18` vs `ColorStrategyPlanning.cs:1469-1499`、`709-713` |

---

## 1. 范围、方法与证据边界

### 1.1 范围

本文回答五件事：

1. 3×3 矩阵如何算、是否自洽、哪些空间不能算；
2. 输入侧：源描述符从哪来、输入矩阵是否真的作用于像素；
3. 输出侧：H.273 矩阵 token 的能力判定、交叉一致性闸、容器标注阶梯；
4. 输入 × 输出组合的实际产物行为（含 ICC/CICP/alpha/范围）；
5. 真实冲突与实现缺口，按可修复性排序。

**不在本文范围**：Legacy/RAW/DNG 管线的完整色彩转换、ICC 规范版本与许可、UI 布局、AOT/AVX/NET 版本升级。它们只在影响上述链路时被提及。

### 1.2 方法

- **源码**：只读 `Services/ColorMapping/`、`FfmpegCommandBuilder`、`QueueProcessor`、`PngCicpService`、`ExifToolService`、`IccProfileService` 的实现与注释；注释只作为“设计意图”，不作结论；结论以实测为准。
- **纯托管探针**：`tests/output/_tmp/colormatrix-analysis/Program.cs`，直接调用 `ColorSpaceRegistry.LinearMatrixBetweenPrimaries` / `ColorSpaceDescriptor.FromNamedSpace` / `FromIcc`，输出 9×9 共 72 组矩阵。
- **ServiceProbe**：`ServiceProbe matrix`（矩阵锚点）与 `ServiceProbe colormath`（曲线/参照对账）。
- **端到端**：CLI headless，`--color-engine engine|legacy|auto`，产物用 `ffprobe`、`exiftool`、`pngcicp info` 读回；无像素变化的组合用 PSNR 判定。
- **对照**：手动 `ffmpeg` 命令验证 `-colorspace` 与 `scale=in_color_matrix=` 的差异。

### 1.3 环境与复现约束

- Windows / PowerShell 7 / .NET 11；`FfmpegGui.exe` 为本轮 Release 构建。
- **必须**把 `TMP/TEMP/TMPDIR` 钉到 `tests/output/_tmp/colormatrix-analysis/round-tmp`。不钉会出 `cms_*.rgb48 Permission denied`、JXL/ICC 假失败（exit 92），这类不是产品缺陷。
- `*_exiftool_tmp` 是 exiftool 写完后的临时副本残留，**不是第二份产物**；报告与统计时必须排除。
- `grid` / `grid2` 中早期 JXL/PQ 失败是 TMP 未钉住的假失败，已排除，不写进结论。

### 1.4 证据边界

- 本轮所有读数都来自当前机器、当前二进制；没有引用旧文档的结论，也没有使用历史提交的构建。
- 旧文档只用于知道“曾经讨论过什么”，不作为证据。

---

## 2. 色彩空间与 3×3 矩阵模型

### 2.1 唯一算式

`ColorSpaceDescriptor.MatrixTo(dst)` 的语义是 **`M(dst←src)`**，作用在**线性 RGB**上：

```
M(dst←src) = inv(dst.RgbToXyzD50) · src.RgbToXyzD50
```

- PCS 固定为 **XYZ D50**；源/目标白点不同由 `RgbToXyzD50` 内部的 Bradford 适应吸收。
- 任一矩阵缺失/奇异 → `null`，调用方必须放弃映射，不能只改标签。
- 每行和自洽校验：`|sum(row)-1| > 5e-3` → `null`。源码注释明确这个阈值是给 ProPhoto 等空间“自身 D50 定义差异”留的余量，**不是精度阈值**；不能用“实测偏差 1e-15”反推阈值错误。

`Equivalent` 比较曲线 + 9 项矩阵，矩阵容差 `1e-5`；`sRGB` 与 `BT.709` 矩阵数值相同但曲线不同，所以 `Equivalent=false`。
`ContainsGamut` 用原色三角形的几何包含，不是“矩阵元素全非负”；BT.2020→BT.709 有负系数是正常的。

### 2.2 9×9 探针结果

72 组 `src→dst` 全部 `OK`，最大行和偏差 **1.332e-15**，多数在 `1e-16~1e-15`。

| src → dst | m00 | m01 | m02 | m10 | m11 | m12 | m20 | m21 | m22 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| BT.709 → BT.2020 | 0.6274039 | 0.3292830 | 0.0433131 | 0.0690973 | 0.9195404 | 0.0113623 | 0.0163914 | 0.0880133 | 0.8955953 |
| BT.2020 → BT.709 | 1.6604910 | -0.5876411 | -0.0728499 | -0.1245505 | 1.1328999 | -0.0083494 | -0.0181508 | -0.1005789 | 1.1187297 |
| Display P3 → BT.709 | 1.2249402 | -0.2249402 | 0.0000000 | -0.0420570 | 1.0420570 | 0.0000000 | -0.0196376 | -0.0786360 | 1.0982736 |
| BT.2020 → Display P3 | 1.3435783 | -0.2821797 | -0.0613986 | -0.0652975 | 1.0757879 | -0.0104905 | 0.0028218 | -0.0195985 | 1.0167767 |
| DCI-P3 → BT.709 | 1.1575164 | -0.1549624 | -0.0025540 | -0.0415001 | 1.0455679 | -0.0040679 | -0.0180500 | -0.0785783 | 1.0966283 |

完整 72 组见 `COLOR_MATRIX_9X9_2026-11-14.csv`（与本报告同目录）。

关键读数：

- `smpte170m ↔ smpte240m` 矩阵完全相同（identity 对）；两者的 transfer 不同，不能因此认为整条描述符等价。
- `bt709 ↔ sRGB` 矩阵数值相同，但曲线不同，`Equivalent=false`。
- `bt470bg ↔ smpte170m` 矩阵**不相同**；`bt470m`、`jedec-p22` 与 `smpte170m` 也不相同。能力表把它们归入同一“601 族”是**标注/落地语义**，不是数学等价。
- `smpte428` / `film` 在 `ServiceProbe colormath` 中 `→bt709` 返回 `null`，是**有意不收录**（精度/xy 表达不了），不是“算不出来”；`ColorSpaceDescriptor.FromCicp` 对未知 token 也返回 null，不猜。

### 2.3 与 zimg 的独立对账

`ServiceProbe colormath` 19 项 PASS / 0 FAIL：

- 多数 token 与随包 ffmpeg 的 zimg 参照最大差 `4.92e-6`；
- `bt470m` 为 `1.73e-4`，来自 Illuminant C 白点的 4 位小数限制，不是实现错误；
- 明确把 `smpte170m≡smpte240m` 作为“同矩阵对”白名单，只允许这一对。

### 2.4 具名空间表

| 具名空间 | OutPrimaries | OutTrc | OutMatrix | 探针 |
|---|---|---|---|---|
| sRGB | bt709 | iec61966-2-1 | bt709 | OK |
| BT.709 | bt709 | bt709 | bt709 | OK |
| Display P3 | smpte432 | iec61966-2-1 | bt709 | OK |
| DCI-P3 | bt2020 | bt709 | bt2020nc | OK（源传输 smpte428，归一化） |
| P3 PQ | bt2020 | smpte2084 | bt2020nc | OK |
| P3 HLG | bt2020 | arib-std-b67 | bt2020nc | OK |
| BT.2020 PQ | bt2020 | smpte2084 | bt2020nc | OK |
| BT.2020 HLG | bt2020 | arib-std-b67 | bt2020nc | OK |
| BT.2020（SDR） | bt2020 | bt709 | bt2020nc | OK |
| Adobe RGB | bt2020 | bt709 | bt2020nc | OK（源 gamma2.2） |
| ProPhoto RGB | bt2020 | bt709 | bt2020nc | OK（源 D50 + 1.8+toe） |
| ColorMatch RGB | bt2020 | bt709 | bt2020nc | **NULL** |

两个结构性事实：

1. **所有具名描述符的 `PrimariesToken` 都是 null**（不是漏填）。`FromNamedSpace` 注释明确“具名空间不等于任何 CICP token，不可用于 H2”；`IsCicpNameable` 因此对具名路径为 false。ICC 路径会由 `RefineKnownSpace` 补 token，具名路径不会。
2. `ColorMatch RGB` 的 Spec 只有 `OutPrimaries/OutTrc/OutMatrix`，**没有 `Rp/Gp/Bp/Wp` 物理色度**。`NamedSpaceColorantsD50` 返回 null，`FromNamedSpace` 无法构造描述符，所以探针是 `NULL`。

### 2.5 ColorMatch 的两种语义必须分开

- **作为手动目标**：只需要 `OutPrimaries=bt2020`，不需要源自身色度；所以它能“工作”，但产物是 BT.2020/SDR（cICP `9/1`），不是 ColorMatch。实测不拒绝、exit 0、无提示。
- **作为源描述符**：需要物理色度来算源→目标矩阵；`FromNamedSpace` 拿不到，构造失败。若源是 ColorMatch ICC，`FromIcc` 仍可能从 ICC 矩阵构造，但 `RefineKnownSpace` 无法按名字吸附，识别链并不完整。

因此，“ColorMatch 手动目标静默归一 BT.2020”不是矩阵算错，而是**目标语义被产品化成 BT.2020 容器**；问题在于没有对用户显式说明。

### 2.6 矩阵 token 的族映射（这是“标签”不是“矩阵”）

`ColorTransformPlan.MatrixTokenFor`：

| 目标 primaries token | 输出 matrix token |
|---|---|
| bt2020 | bt2020nc |
| bt709 / smpte432 / smpte431 | bt709 |
| bt470bg / smpte170m / bt470m / jedec-p22 | smpte170m |
| 其他 | null |

这张表说明：

- **Display P3/DCI-P3 没有 P3 专用 H.273 matrix token**，只能写 `bt709`；
- **BT.470M/JEDEC-P22 被并入 601 族 `smpte170m`**；
- 输出 token 是**目标 primaries 的族查表**，不是从真实 `p.Matrix` 反算出来的；
- `p.Matrix`（浮点 3×3，像素转换用）与 `p.OutMatrix`（字符串，容器标注用）是两套东西，源码中没有强制一致性校验，只有显式 `--color-out-matrix` 时的交叉闸。

---

## 3. 输入侧：组合与真实行为

### 3.1 源描述符优先级（源码）

`ColorIntentFactory.FromOptions` 的源优先级：

1. `--icc-file`（用户显式，最高；但不置 `SourceIccPath`，所以“携带源 ICC”路径拿不到它）；
2. 自产 RAW/UltraHDR 中间件声明；
3. 源内嵌 ICC 度量识别；
4. 容器 CICP 标签；
5. 行业惯例假定 sRGB。

ICC 优先于容器 CICP；`FromCicp` 只在无 ICC 时启用。无 ICC/无 CICP → `AssumeByConvention` 假定 sRGB，置信度 `CodecDefault`；规划层 `MinConfidenceForMapping` 闸门禁止改像素，计划落 `Action=None`，仅保证标注。

这条优先级本身是合理的；问题在 ICC 探测失败时，第 3 步会**静默退化**到第 5 步，见 3.4。

### 3.2 输入矩阵：实测没有像素作用

`FfmpegCommandBuilder` 在 `-i` 前写 `-colorspace`，值来自 `dec.DeclMatrix`，经 `ToFfmpegInputMatrixName` 白名单（7 项：`bt709/bt2020nc/bt470bg/smpte170m/smpte240m/fcc/rgb`）。`bt601`、`bt470m`、`smpte431`、`smpte432`、`jedec-p22` 不在白名单，返回 null 并跳过。

**但把 `-colorspace` 放在 `-i` 前，对实测的 JPEG/AVIF 解码器并不改变 RGB 像素**；真正的输入矩阵转换要由 `scale=in_color_matrix=`（或 zscale）完成。直接对照：

```
# A：仅 -colorspace 放 -i 前
ffmpeg -colorspace bt709 -i src_yuv_untagged.jpg out_a.png

# B：真正的输入矩阵转换
ffmpeg -i src_yuv_untagged.jpg -vf "scale=in_color_matrix=bt709:in_range=pc:out_range=pc" out_b.png

PSNR(out_a, out_b) average = 28.55 dB
```

而产品自身 `bt601` vs `bt709` 的产物：

- `src_yuv_untagged.jpg`：`--color-engine engine` 与 `legacy` 均 PSNR=inf；
- `src_yuv601.jpg`：engine/legacy 的 auto vs `--color-matrix bt709` 均 PSNR=inf；
- `src_yuv_709.avif`：auto/`bt601`/`bt709` 均 PSNR=inf。

结论：**`FfmpegCommandBuilder.cs` 里“2026-09-16 修复角色错位、输入矩阵影响像素解读”的注释，在 JPEG/AVIF 上没有被运行时证实**。`-colorspace` 只是把声明写进了输入侧元数据，解码器仍按自己的默认矩阵出 RGB。引擎路径之所以说“无承载”，是因为它拿到的是已解码 RGB；Legacy 路径写了 `-colorspace`，但同样没有像素效果。

因此：

- 引擎路径：`--color-matrix` 对像素无作用，日志明确“本次交付中无承载”；
- Legacy 路径：`-colorspace` 对 JPEG/AVIF 无像素作用，且**不告警**；
- 裸 CLI `--color-matrix` 在未开高级色彩时甚至点不到名，是独立 UX 缺口。

### 3.3 输入矩阵 token 域与白名单的错位

输入白名单只有 7 项；输出 token 域 11 项；两者不是同一个域：

- 输入缺 `bt601/bt470m/smpte431/smpte432/jedec-p22`；
- 输出多 `auto/bt601/ycgco/xyb`；
- `bt601` 在输出归一为 `smpte170m`，输入不归一、也不在白名单，所以 `--color-matrix bt601` 连 `-colorspace` 都不会写。

这造成“CLI 接受但静默不生效”的组合：`bt601`、`bt470m`、`smpte431`、`smpte432`、`jedec-p22`。如果没有高级 UI 的提示，用户会以为矩阵已经应用。

另有一条**独立绕过**：Ultra HDR 中间件路径在 `FfmpegCommandBuilder.cs:1017` 写死 `bt709`，用户声明的矩阵值整体被绕过。该路径不在本次端到端样本中，但源码事实明确，属于同一类“输入矩阵不可达”。

### 3.4 ICC 探测的 fail-open（本轮最关键的可靠性问题）

`ProbeInputColorMetadataCore` 用 `ExifToolService.ReadColorTagsAsync` 一次取标签和 base64 ICC：

- `ReadColorTagsAsync` 只在 `timedOut` 时重试（5s→20s）；
- `ReadColorTagsOnceAsync` 在 stdout 为空时直接 `return (result,false)`，**不检查进程退出码**；
- 外层 `catch {}` 把异常吞掉；
- 探测缓存 10s TTL，可能把退化结果短暂缓存。

实测 `exiftool -ver` 顺序 10 次：**6 成功、4 失败**，失败信息：

```
Failed to load Perl DLL "perl532.dll" code 126
```

这不是矩阵数学问题，但会直接打断 ICC 识别：ICC 读不到 → 源描述符退化为“惯例 sRGB/CodecDefault” → 目标从 P3 变 sRGB 或 bt709。

端到端证据：

- `src_p3_icc.png → avif`，`--color-engine auto` 3 次：2 次 `CarryIcc` + 输出 `smpte432`，1 次 `CodecDefault` + 输出 `bt709`；三次产物字节数均为 1797。
- `--color-engine engine` 稳定 `CarryIcc`；`--color-engine legacy` 也能写 `smpte432`。
- `src_hdr_pq.png → tiff`：首跑 `[tiff] ⚠️ 无法生成目标 ICC`、产物无 ICC、exit 0；3 次重跑全部 `[tiff] 已嵌入目标色彩空间 ICC（bt709/iec61966-2-1）`，exiftool 读回 `RGB built-in`。
- 手动复现 `GetOrGenerateStandardIcc` 的 ffmpeg 命令（HDR 源当 carrier）返回 rc=0、3902B PNG，说明 ICC 生成本身成功；首跑失败更可能是后续 exiftool 提取/回读偶发失败，而不是 `iccgen` 不支持。

### 3.5 输入组合实测摘要

| 输入 | 标签 | 结果 |
|---|---|---|
| `src_srgb.png` | 无 ICC/无 cICP | `CodecDefault`，`Action=None`，不改像素；PNG 无标注可接受（`UnlabeledMeansSrgb=true`） |
| `src_p3_icc.png` | iCCP P3 363B | 识别 P3；PNG→cICP 12/13 + iCCP；JPG/TIFF/WebP ICC；AVIF/JXL CICP |
| `src_p3_cicp.png` | cICP 12/13 | 识别 P3；PNG cICP；AVIF/JXL cICP；JPG 生成 P3 ICC 兜底 |
| `src_icc_cicp_conflict.png` | iCCP P3 + cICP 9/16 | 源描述按 ICC=P3；产物 cICP 却保留 9/16（BT.2020/PQ），无 iCCP |
| `src_yuv601.jpg` | JPEG 601 标记 | `CodecDefault`（低置信度）；`--color-matrix` 无像素作用 |
| `src_yuv_untagged.jpg` | 实际按 601 解 | 低置信度；`--color-matrix` 无像素作用 |
| `src_yuv_709.avif` | nclx matrix=bt709，primaries/trc unknown | 低置信度（缺 primaries/trc，不能 `FromCicp`）；`--color-matrix` 无像素作用 |
| `src_pq.png` | 16-bit PQ 但无 cICP/ICC | 按 sRGB 处理；AVIF 输出 `gbrp12le` + SampleTransform，但标签是 sRGB（**未标注 HDR 不会被推断**） |
| `src_hdr_pq.png` | cICP 9/16/full | 正确识别 PQ/BT.2020；各出口见 5.2 |
| `src_prophoto_icc.png` | iCCP ProPhoto 544B | 识别 ProPhoto；PNG/TIFF/JPG 携带 ICC；AVIF/JXL 归一 BT.2020 |
| `src_rgba_trans.png` | alpha=128 | PNG/WebP 保留 alpha；AVIF 丢失 alpha（见 4.7） |

---

## 4. 输出侧：能力、标注与落地

### 4.1 输出矩阵承载判定（`ResolveOutMatrixCapability`）

唯一真值函数。`auto` 返回 `NotApplicable`（不代表任何矩阵；auto 的推导值另由 `MatrixTokenFor` 给出）。`bt601` 在进入判定前归一为 `smpte170m`。

图例：`R`=Redundant，`C`=Carried，`X`=Rejected。

| 格式 / 后端 | rgb | bt709 | bt601 | bt470bg | smpte170m | smpte240m | fcc | bt2020nc | ycgco | xyb |
|---|---|---|---|---|---|---|---|---|---|---|
| jpg/jpeg（mjpeg） | X | X | R | R | R | X | X | X | X | X |
| jpg（Cjpegli） | R | X | X | X | X | X | X | X | X | C |
| webp | X | X | R | R | R | X | X | X | X | X |
| png/apng/tiff/bmp/ppm/gif | R | X | X | X | X | X | X | X | X | X |
| avif（Ffmpeg/libaom） | C | C | C | C | C | C | X | C | X | X |
| avif（avifenc） | X | X | X | X | X | X | X | X | X | X |
| jxl（cjxl / libjxl） | R | X | X | X | X | X | X | X | X | C |
| dng / jxr | R | X | X | X | X | X | X | X | X | X |
| 未知格式 | X | X | X | X | X | X | X | X | X | X |

要点：

- **`Carried` 是“容器/后端能承载”的判断，不是“本次出口一定落地”的保证**。AVIF 的 `Carried + Requires-PixFmt-Switch` 就是典型：源码注释已承认切 pix_fmt 由出口层负责，通用块未命中时 libaom 自选 `gbrp`，`-colorspace` 被忽略。
- **JPG/WebP 没有矩阵标注位**：601 族被判 `Redundant` 是因为解码端固定按 BT.601 解读，不是容器写了 601。`bt709` 等被判 `Rejected`，因为写不进去，转换只会制造色偏。
- **PNG/TIFF/BMP/PPM/GIF 是 RGB 语义**：`rgb` 冗余，YUV 矩阵没有作用点。
- **JXL 的 `xyb` 是 libjxl 内部感知表示，不是 H.273**；`Carried` 不覆盖 `p.OutMatrix`，由出口层按 `cjxl -xyb` / libjxl 开关落地。
- **AVIF 的 avifenc 路径被 fail-closed**（`backend != Ffmpeg` → Rejected）；产品静态出口不使用它。
- **JPG × Cjpegli** 不走规划层判定，由出口层直接调 `ResolveOutMatrixCapability(..., Cjpegli)`；`xyb=Carried`。

### 4.2 交叉一致性闸

`ApplyExplicitOutMatrix` 在显式 token 时执行：

- 覆盖前读 `p.OutMatrix`（由目标 primaries 推导的 `bt2020nc/bt709/smpte170m/null`）；
- 仅 H.273 YUV 族参与（`rgb`/`xyb` 不参与）；
- 同值或 601 族互通 → 放行；
- 推导为 null（primaries 未标注）→ 无基准，放行；
- 跨族 → 把 `OutMatrixVerdict` 翻成 `Rejected`，`Action=Reject`，**exit 91、零产物**，模板 4。

实测：

- AVIF + `bt2020nc`（源 sRGB/目标 sRGB，推导 bt709）→ exit 1、0 文件、模板 4 conflict；
- JPG + `bt709` → exit 1、0 文件；
- PNG + `bt709` → exit 1、0 文件；
- `bt601` 和 `rgb` 在低置信度源上只登记、不生效（因为推导值为 null，交叉闸没有基准）。

### 4.3 标注推导阶梯（`SetLabelsFrom`，2026-11-14 口径）

1. **源 ICC 零改动携带**：仅当 `CICP 不能正确承载` 且 `CanCarryArbitraryIcc && ExitWritesIcc && !hdr`；容器允许共存且空间可精确命名时，CICP 只作附加标签。
2. **CICP 优先**：`IsPreciselyCicpNameable(dst) && CapsAcceptsCicp(...)` → 只写 CICP 三元组，**不再附自生成 ICC**；HDR 也走这里（iccgen 不支持 PQ/HLG）。
3. **CICP 不可达**：按具体原因（空间不可命名 / 容器无 CICP / 三元组不等价 / 外部编码器枚举写不出）走 ICC 兜底：携带源 ICC 或由 CICP 生成标准 ICC，并置 `CicpUnavailable`。
4. **两者皆不可达**：`EnsureAnnotationNotSilentlyMissing`：若像素未动或容器无色彩通路且目标等价 sRGB，记 `UnlabeledAssumedSrgb`；否则 `Action=Reject`。

**关键实现差异**：

- `Action=CarryIcc` 不等于产物真的带 ICC。P3 ICC→PNG 的日志写“源 ICC 未携带——CICP 已足以正确承载”，但产物同时有 `cICP 12/13` 和 `iCCP 354B`。这是因为 `-map_metadata 0` 仍在复制源 ICC side data，而规划/日志没有感知。
- `p.AttachIcc=true` 只是**计划层**的“要求”；后置步骤失败（例如 TIFF HDR 首跑 `GetOrGenerateStandardIcc` 返回 null）时，`EnsureAnnotationNotSilentlyMissing` 早已放行，产物可以无 ICC 且 exit 0。
- 低置信度分支 `p.UnlabeledAssumedSrgb = !caps.CanCicp` 忽略 `caps.UnlabeledMeansSrgb`，是 TIFF/PPM 未标注输出的直接原因（见 6.C-06）。

### 4.4 格式能力表（`ColorTransformPlan.FormatColorCaps`）

`ICC`=CanCarryArbitraryIcc；`CICP`=CanCicp；`Gen`=CanGenerateIccFromCicp；`RGB`=RgbNative；`HDR`=CanHdr；`Unlab`=UnlabeledMeansSrgb；`Both`=CanHaveBothIccAndCicp。

| 格式 | ICC | CICP | Gen | 深度 | RGB | HDR | Unlab | Both | 备注 |
|---|---|---|---|---|---|---|---|---|---|
| png/apng | ✅ | ✅ | ✅ | 16 | ✅ | ✅ | ✅ | ✅ | cICP + iCCP 可共存 |
| jpg/jpeg/jpegli | ✅ | ❌ | ✅ | 8 | ❌ | ❌ | ✅ | ❌ | APP2 ICC；增益图 |
| webp | ✅ | ❌ | ✅ | 8 | ❌ | ❌ | ✅ | ❌ | `EncoderWritesGeneratedIcc=false`，生成 ICC 只能后置 |
| tiff/tif | ✅ | ❌ | ✅ | 16 | ✅ | ❌ | ❌ | ❌ | `NoCicpReason=ContainerNonNormative`；ICC 必须后置 |
| jxl | ✅ | ✅ | ✅ | 32 | ✅ | ✅ | ❌ | ❌ | ICC 与 CICP 不可共存；外部枚举仅 sRGB/P3/PQ/HLG |
| avif | ✅（表） | ✅ | ✅ | 12 | ❌ | ✅ | ❌ | ✅ | 静态出口 `ExitWritesIcc=false`；16-bit 走 SampleTransform |
| heic/heif | ❌ | ❌ | ❌ | 8 | ❌ | ❌ | ❌ | ❌ | 本工具链无 heif muxer/heifenc |
| jxr | ❌ | ❌ | ❌ | 16 | ✅ | ❌ | ❌ | ❌ | 容器实测可经 exiftool 带 ICC，但本轮有意未翻位 |
| gif | ❌ | ❌ | ❌ | 8 | ✅ | ❌ | ❌ | ❌ | 调色板；引擎特殊放行→sRGB 无标注 |
| webm | ❌ | ❌ | ❌ | 8 | ❌ | ❌ | ❌ | ❌ | v1 保守；Matroska Colour 未实测 |
| ppm | ❌ | ❌ | ❌ | 16 | ✅ | ❌ | ❌ | ❌ | 裸流中间格式 |
| bmp | 默认 | 默认 | 默认 | 8 | 默认 | 默认 | 默认 | 默认 | 未在能力表登记，`CapsFor` 返回最保守默认 |
| 未知 | ❌ | ❌ | ❌ | 8 | ❌ | ❌ | ❌ | ❌ | `CapsFor` 最保守默认 |

**必须区分两件事**：

- `CanCarryArbitraryIcc=true` 是**容器/上游编码器**的能力（AVIF 的依据是 avifenc E1 实测）；
- `PlanPolicy.ExitWritesIcc=false` 是**本产品静态出口**的事实（AVIF 的 ffmpeg muxer 不写 ICC、exiftool 后置报 0 updated 但 rc=0）。

把前者当“会写 ICC”是 AVIF 这类问题反复出现的根因。

### 4.5 各容器的实际标注行为（本轮实测）

| 容器 | 实际可写的标注 | 本轮观察 | 备注 |
|---|---|---|---|
| PNG | cICP + iCCP | P3 ICC→PNG 两者都有；P3 CICP→PNG 仅 cICP；sRGB 无标注 | `PngCicpService` 写 cICP，`-map_metadata` 可能带 iCCP |
| JPEG | APP2 ICC；无矩阵/CICP | P3/ProPhoto ICC 可读回；601 无矩阵标签 | 解码端固定 601 |
| WebP | ICCP；无矩阵/CICP | P3 ICC 可读回；601 无矩阵标签 | libwebp 按 601 解读 |
| TIFF | ICC；无规范 CICP | P3/ProPhoto ICC 可读回；HDR 首跑可无 ICC | ICC 后置，须在元数据恢复之后 |
| AVIF | nclx CICP；静态不写 ICC | P3/ProPhoto 无 ICC；颜色靠 CICP | `ExitWritesIcc=false` |
| JXL | cICP 或 ICC 二选一 | P3 走 cICP；ProPhoto 归一 BT.2020 无 ICC | `CanHaveBoth=false` |
| GIF/PPM/WebM | 无 | 无标注；GIF 引擎特殊放行 sRGB | `HasColorPath=false` |
| JXR | 容器可 ICC，服务未接 | 本轮未产出 | 能力位保守 |

### 4.6 AVIF 输出矩阵的落地缺口

源码注释（`ResolveOutMatrixCapability` 的 AVIF 分支）已承认：

> `Carried + Requires-PixFmt-Switch`：切 pix_fmt 由出口层负责，通用块未命中 `-pix_fmt` 时 libaom 自选 `gbrp`，`-colorspace` 被忽略，nclx 保持 identity。

实测：

| 命令 | pix_fmt | nclx matrix | primaries/trc | 结果 |
|---|---|---|---|---|
| `--color-out-matrix bt709`（无 chroma/bit-depth） | `gbrp` | `gbr`（identity） | bt709/iec61966 | exit 0、1 文件；矩阵未落地 |
| `+ --chroma 4:2:0 --bit-depth 8` | `yuv420p` | `bt709` | unknown/unknown | 矩阵落地，但 primaries/trc 缺失 |
| `--color-out-matrix bt2020nc`（源 sRGB） | — | — | — | exit 1、0 文件（跨族拒绝） |
| `--color-out-matrix rgb` | `gbrp` | `gbr` | 源标签 | `--chroma` 不会强制成 YUV |

这不是“矩阵算法错”，而是**判定层与出口层之间缺一条强制 pix_fmt/写入 nclx 的契约**。`Carried` 在没有 `--chroma` 时是空头支票。

### 4.7 AVIF 静态出口的 alpha 丢失

`src_rgba_trans.png`（alpha=128）：

- → PNG：alpha=128 保持；
- → WebP：alpha=129（有损量化，可接受）；
- → AVIF：324B、`yuv420p`、**无 alpha 辅助流**，日志没有透明/alpha 警告。

源码 `RawColorPipeline.cs:334-346` 明确写了“AVIF 容器能带 alpha，但本出口底图按 yuv420p 编码、容器只装底图/增益图/tmap 三个 item ⇒ 源带 alpha 时会被拍平”，并说“至少必须点名（不许静默丢）”。**这条点名只覆盖增益图出口**；普通静态 AVIF 出口没有走到那条提示。因此这是一条**实现缺口**，不是设计不支持。

### 4.8 范围与容器出口

- JPEG auto 档把 `tv` 压回 `pc`（`yuvj444p,pc`，命令 `-color_range pc`）；显式 `--color-range tv` 才 `yuv444p,tv`，命令带 `-color_range tv -strict unofficial`，并 `EmitWarn` 点名 libjpeg/WIC 发灰。
- WebP auto/pc/tv 产物均 `yuv420p,tv`；显式 `pc` 打印“ignored ... limited-range only ... measured -3.66 dB”。
- PNG RGB 原生产物 `pc`；AVIF `pc`。
- `ContainerYuvMatrix` 对 `jpg/jpeg/webp` 强制 `bt470bg`，其他 null；RGB 原生 `png/tiff/apng/jxl` 不写输出 `-colorspace`，避免 YUV 矩阵标签与 cHRM 冲突。

---

## 5. 端到端组合矩阵

> 以下都是本轮实际产物读数；“—/n/a”表示未构造该组合，不表示产品不支持。  
> `cICP` 写作 `primaries/transfer`；`ICC` 写作 profile 描述；`matrix` 写作 nclx/ffprobe 的 color_space。

### 5.1 P3 源

| 输入 | 输出 | 引擎/参数 | 产物读数 | 判定 |
|---|---|---|---|---|
| P3 ICC 363B | PNG | engine, carry=all | `rgb24`；cICP `12/13` + iCCP 354B；exit 0 | 颜色正确；日志说“ICC 未携带”，与实际不符 |
| P3 ICC | JPEG | engine | `yuvj444p`、`bt470bg`；ICC P3；exit 0 | 正确（ICC + 601） |
| P3 ICC | TIFF | engine | `rgb24`；ICC P3；exit 0 | 正确 |
| P3 ICC | WebP | engine | `yuv420p`；ICC P3；exit 0 | 正确（ICC 携带） |
| P3 ICC | AVIF | engine | `gbrp`；cICP `smpte432/iec61966`；无 ICC；exit 0 | 正确（CICP 精确承载） |
| P3 ICC | AVIF | auto | 同上；3 次中 1 次 `bt709` | **不稳定**：ICC 探测偶发失败导致退化 |
| P3 ICC | JXL | engine | `rgb24`；cICP `smpte432/iec61966`；无 ICC | 正确 |
| P3 CICP | PNG | engine | cICP `12/13`；无 ICC | 正确 |
| P3 CICP | JPEG | engine | `yuvj444p`、`bt470bg`；生成 ICC P3 | 正确（CICP→ICC 兜底） |
| P3 CICP | AVIF | engine | `gbrp`；cICP `smpte432/iec61966` | 正确 |
| P3 CICP | JXL | engine | CICP `smpte432/iec61966` | 正确 |

### 5.2 HDR（BT.2020/PQ，cICP 9/16）

| 输出 | 产物读数 | 判定 |
|---|---|---|
| PNG | `rgb48be`；cICP `9/16`；无 ICC | 正确（PNG 经 cICP 承载 HDR） |
| AVIF | 主图 `gbrp12le` + 第二 `yuv444p` 流 `title=SampleTransform`；cICP `9/16` | 正确（16-bit 走 12+4 派生项，不是 alpha/不是错误） |
| JXL | `rgb48le`；cICP `9/16` | 正确 |
| TIFF | 首跑 `⚠️ 无法生成目标 ICC`、无 ICC、exit 0；3 次重跑 `已嵌入目标 ICC（bt709/iec61966-2-1）`、exiftool 读回 `RGB built-in` | **偶发**；不是稳定 TIFF 缺陷，但首跑失败会出无标注 TIFF |
| JPEG | `yuvj420p`；Hable tonemap peak=10000nit；ICC sRGB | 正确（SDR 落点 + ICC） |
| WebP | `yuv420p`；tonemap；无标注 | 可接受（WebP `UnlabeledMeansSrgb=true`） |

### 5.3 ProPhoto ICC（544B，v4.4.0）

| 输出 | 产物读数 | 判定 |
|---|---|---|
| PNG | `rgb24`；ICC `ProPhoto RGB 4.4.0`；无 cICP | 正确（源 ICC 保真） |
| TIFF | `rgb24`；ICC `ProPhoto RGB` | 正确 |
| JPEG | `yuvj444p`、`bt470bg`；ICC `ProPhoto RGB` | 正确（ICC + 601） |
| AVIF | `gbrp12le`；cICP `bt2020/bt709`；无 ICC | **归一 BT.2020**，可能削色；不是“携带 ProPhoto” |
| JXL | `rgb24`；cICP `bt2020/iec61966`；无 ICC | **归一 BT.2020**；不是“携带 ProPhoto” |

`ProPhoto→AVIF/JXL` 的归一化是 `ColorSpaceRegistry` 顶部注释里写明的超广色域策略；问题不在“有没有做”，而在**是否对用户明确说明会改像素/可能削色**。

### 5.4 alpha

| 输入 | 输出 | 结果 |
|---|---|---|
| RGBA alpha=128 | PNG | alpha=128 保持 |
| RGBA alpha=128 | WebP | alpha=129（有损量化） |
| RGBA alpha=128 | AVIF | `yuv420p`、无 alpha 辅助流、无警告 → **丢失** |

### 5.5 手动目标

| 手动目标 | 产物 cICP | 结果 |
|---|---|---|
| Display P3 | `12/13` | 正确保留 P3 |
| DCI-P3 | `9/1` | 归一 BT.2020/SDR |
| Adobe RGB | `9/1` | 归一 BT.2020/SDR |
| ProPhoto RGB | `9/1` | 归一 BT.2020/SDR |
| ColorMatch RGB | `9/1` | 归一 BT.2020/SDR；无拒绝、无提示 |
| BT.2020 | `9/1` | 本身即 BT.2020/SDR |

只有 Display P3 保留自己的标签；其余超广手动目标全部归一到 BT.2020。`ColorMatch` 没有物理色度，但作为**目标**只用到 `OutPrimaries`，所以仍能输出；这是“目标归一”而不是“矩阵算错”。

### 5.6 输出矩阵显式选择

| 格式 | token | 实测 |
|---|---|---|
| AVIF | bt709（无 chroma） | exit 0、1 文件、`gbrp`、`color_space=gbr`；矩阵未落地 |
| AVIF | bt709 + 4:2:0/8 | `yuv420p`、`color_space=bt709`、primaries/trc unknown |
| AVIF | bt2020nc（源 sRGB） | exit 1、0 文件、模板 4 conflict |
| AVIF | rgb | `gbrp`、`color_space=gbr` |
| JPG | bt709 | exit 1、0 文件（跨族拒绝） |
| JPG | bt601 | 接受，产物无矩阵差异（低置信度源推导 null） |
| PNG | bt709 | exit 1、0 文件（无矩阵概念） |
| PNG | rgb | 接受（冗余） |

### 5.7 范围

| 出口 | auto | 显式 `--color-range tv` | 显式 `--color-range pc` |
|---|---|---|---|
| JPEG | `yuvj444p,pc` | `yuv444p,tv` + `-strict unofficial`；警告发灰 | `pc` 可保留 |
| WebP | `yuv420p,tv` | `tv` | 强制 `tv`，打印 ignored/-3.66 dB |
| PNG | `pc` | — | — |
| AVIF | `pc` | — | — |

---

## 6. 真实冲突与实现缺口

### 6.1 高优先级

**C-01 源 ICC + 冲突 CICP：旧 cICP 赢，产物标签与像素不一致。**

- 源：`src_icc_cicp_conflict.png`，iCCP P3 363B + cICP `9/16/full`。
- 计划：`CarryIcc`，源描述 P3；命令 `-color_primaries smpte432 -color_trc iec61966-2-1 ... -map_metadata 0`。
- 产物：仅 cICP `9/16`、无 iCCP；`ffprobe` 读 `bt2020/smpte2084`。
- 根因：源 cICP 经 `-map_metadata 0` 原样复制；`PngCicpService` 见已有 cICP 就不覆盖；`TryStripChunksSupersededByCicp` 只剥 `gAMA/cHRM/sRGB`，不剥 cICP/iCCP，所以 stale cICP 存活。
- 这是本报告最高风险项：**消费端会按 BT.2020/PQ 解读 P3 SDR 像素**。

**C-02 输入矩阵声明不作用像素。**

- 现象见 3.2；`-colorspace` 在 `-i` 前对 JPEG/AVIF 不改变 RGB，真正要 `scale=in_color_matrix=`。
- 影响：`--color-matrix` 在 Legacy 路径看似接受、实际无效果；引擎路径至少说“无承载”，Legacy 无警告。Ultra HDR 中间件路径更直接：`FfmpegCommandBuilder.cs:1017` 写死 `bt709`，用户值被整体绕过。
- 这不是“矩阵算错”，是**载体选错**。

**C-03 AVIF `Carried` 不落地。**

- 默认 `gbrp` 时 nclx identity；显式 bt709 被忽略。
- 只有强制 YUV pix_fmt 才写 nclx；而强制后 primaries/trc 又可能 unknown。
- 影响：用户选择“输出矩阵 bt709”，产物实际没有该矩阵；`OutMatrixVerdict=Carried` 与产物不符。

**C-04 `CarryIcc` 不是“强制携带”。**

- `SetLabelsFrom` 阶梯①要求 `!cicpCanCarry`；P3→PNG 的 CICP 可精确承载，所以走阶梯②“只写 CICP”。
- 日志说“源 ICC 未携带”，但 `-map_metadata 0` 把 ICC 带进了 PNG。产物当前“恰好”两者都有，但这是元数据复制的副产物，不是计划承诺。
- 影响：用户显式选择“携带 ICC”在 CICP 可命名的出口上得不到保证；AVIF 更是 `ExitWritesIcc=false`，永远不写。

**C-05 AVIF 静态出口丢 alpha 且不告警。**

- 证据见 4.7；源码承认是“实现缺口、必须点名”，但普通静态出口未点名。
- 影响：透明素材静默变不透明。

**C-06 低置信度 TIFF 无 ICC 仍 exit 0。**

- `src_srgb.png → tiff`：`rgb24`、无 ICC、无 cICP、exit 0。
- `ColorTransformPlan` 对 tiff 明确 `UnlabeledMeansSrgb=false`；但 `PlanCarry` 低置信度分支 `p.UnlabeledAssumedSrgb = !caps.CanCicp` 直接置 true，`EnsureAnnotationNotSilentlyMissing` 视为已标注而放行。
- 影响：TIFF 查看器行为不一致，未标注不等于 sRGB；这是**能力位被绕过**，不是“惯例允许”。

### 6.2 中优先级

**C-07 ICC 探测 fail-open + exiftool 环境不稳定。**

- `ReadColorTagsOnceAsync` 不检查退出码；`catch {}` 吞异常；只对 timeout 重试。
- `exiftool -ver` 10 次 6/4 失败，`perl532.dll code 126`；这不是偶发噪声，已经影响 P3→AVIF 与 HDR→TIFF 的颜色决策。
- 影响：同一输入、同一参数，多次运行可能给出 P3 或 bt709 两种标签；用户无法复现。

**C-08 `ColorMatch RGB` 的源/目标语义不一致。**

- 目标：可用（只看 OutPrimaries），但产物是 BT.2020/SDR，无拒绝/无提示。
- 源：`FromNamedSpace` NULL，因为 Spec 缺物理色度；源 ICC 路径可能绕过，但名字吸附链不完整。
- 影响：用户以为选了 ColorMatch，实际得到 BT.2020 容器；或源 ColorMatch 无法构造描述符。

**C-09 ProPhoto 超广归一化被误读为“携带 ICC”。**

- AVIF/JXL 都归一 BT.2020；AVIF 可能削色，JXL 用 BT.2020 SDR 标签。
- `CanCarryArbitraryIcc=true` 只说明容器/上游有能力；静态出口 `ExitWritesIcc=false` 才是事实。
- 影响：以为“无损保真”，实际改了像素/标签。

**C-10 输出 token 族映射不是真实矩阵。**

- `smpte432/smpte431→bt709`、`bt470bg/smpte170m/bt470m/jedec-p22→smpte170m`。
- 9×9 矩阵显示这些空间之间并非 identity；`smpte170m` 与 `smpte240m` 才是同矩阵。
- 影响：不能用“输出 token 相同”推断“矩阵相同”；也不能用“矩阵不同”推断“token 必须不同”。

**C-11 输出矩阵 token 域没有 P3/DCI 专属矩阵。**

- 输出域 11 项不含 `smpte431/smpte432/bt470m/jedec-p22`；Display P3/DCI-P3 的矩阵只能写 `bt709`。
- 影响：用户无法显式请求“P3 YUV 矩阵”；`--color-out-matrix` 的语义是“容器矩阵标签”，不是“色域转换矩阵”。

**C-12 JPG/WebP 的 plan token 与命令 token 不一致。**

- `MatrixTokenFor` 对 smpte170m 给 `smpte170m`；`ContainerYuvMatrix` 对 jpg/jpeg/webp 强制 `bt470bg`。
- 两者同属 601 族，产物无标签，实际解码按 601；但审计时字符串不一致，容易被误判为“计划未生效”。

**C-13 TIFF HDR ICC 生成偶发失败，日志误报“无法生成”。**

- 首跑 `无法生成目标 ICC`；3 次重跑成功；手动 `iccgen` 命令 rc=0、3902B PNG。
- `GetOrGenerateStandardIcc` 在 `ExtractIccToTempFile` 失败时返回 null，且 `catch { return null; }` 不输出 stderr；所以“生成失败”可能只是提取/回读失败。
- 影响：诊断困难；首跑可出无 ICC TIFF 且 exit 0。

### 6.3 低优先级 / 文档与观测

**C-14 `CanCarryArbitraryIcc` 与 `ExitWritesIcc` 没有在 UI/文档中分家。**

- AVIF 表写 `CanCarryArbitraryIcc=true`，实际 `ExitWritesIcc=false`；用户/审计容易把前者当承诺。

**C-15 P3 ICC→PNG 日志与产物不一致。**

- 日志“源 ICC 未携带”，产物 iCCP 354B；原因是 `-map_metadata` 复制，计划未感知。
- 影响：审计无法用日志判断“ICC 是否携带”；应以产物 chunk 读回为准。

**C-16 注册表顶部注释与代码的 601 族策略冲突。**

- 注释“软件最低 BT.709，BT.601/bt470m/bt470bg/smpte170m 一律归入 BT.709”；代码实际保留 601 族输出矩阵，并在 JPG/WebP 强制 `bt470bg`。
- 影响：读注释会得出错误结论；以代码为准，但应改注释或明确“最低 BT.709”只指 primaries/trc。

**C-17 具名描述符 `PrimariesToken=null` 是设计，但会让人误判 `IsCicpNameable`。**

- 具名路径不能用于 H2；ICC 路径会 `RefineKnownSpace` 补 token。两路径行为不同，审计时容易混淆。

---

## 7. 修复建议（按优先级）

### P0：先保证“产物标签与像素一致”

1. **冲突输入显式裁决**：源 ICC 与源 CICP 不一致时，二选一：
   - 以 ICC 为权威（当前源描述符优先级），并在输出前**剥掉/重写**源 cICP，禁止 `-map_metadata 0` 把旧 cICP 复制到产物；
   - 或直接 fail-closed（exit 91）并提示“源 ICC 与 cICP 冲突，请指定权威描述”。
   - 无论哪种，都不能让 `PngCicpService` 看到“已有 cICP”就跳过。
2. **ICC 探测 fail-closed**：`ReadColorTagsOnceAsync` 检查退出码；非 0（尤其 code 126）视为失败并重试；重试仍失败时**不缓存、不按无 ICC 处理**，而是点名并使任务失败/降级到显式用户选择。
3. **后置写入门**：计划要求 `AttachIcc` 时，写完必须读回；读不到 ICC 就不能 exit 0（TIFF HDR 首跑这类）。

### P1：让矩阵选项“说到做到”

4. **输入矩阵**：Legacy 路径要真正加 `scale=in_color_matrix=<token>:in_range=...`（或 zscale）并验证像素变化；否则把 `--color-matrix` 明确标为“仅声明/不保证像素转换”，并在 Legacy 也点名“无承载”。
5. **AVIF 输出矩阵**：`Carried` 必须配套强制 pix_fmt/写 nclx 的出口契约；没有 `--chroma` 时不要宣称矩阵已落地，或直接要求 `--chroma` 才能接受 YUV matrix token。
6. **CarryIcc 语义**：如果 UI 提供“携带 ICC”，就必须有强制携带路径（即使 CICP 可命名）；当前只能在 CICP 不可达时携带，应在 UI/日志中改名或明确“CICP 优先，ICC 仅兜底”。
7. **AVIF alpha**：普通静态 AVIF 出口检测 alpha，支持 alpha 辅助流，或至少 `EmitWarn` 点名“透明会丢失”；不能静默。
8. **低置信度标注**：`p.UnlabeledAssumedSrgb` 必须尊重 `caps.UnlabeledMeansSrgb`；TIFF/PPM/JXR 等 `false` 的格式，要么附 ICC，要么拒绝。
9. **ProPhoto/Adobe/DCI/ColorMatch 归一化**：在 UI/报告中显式提示“将归一 BT.2020，可能削色”；或提供“携带命名 ICC”的出口选择。

### P2：可观测性与文档

10. **计划感知 metadata copy**：`-map_metadata 0` 复制 ICC/cICP 的事实要进入计划/日志；至少让“CarryIcc/未携带”与实际 chunk 对齐，或统一由后置步骤接管 ICC。
11. **能力表拆分**：`CanCarryArbitraryIcc`（容器/上游）与 `ExitWritesIcc`（本出口）分列展示；GUI 不应把前者当承诺。
12. **token 族表与文档**：明确 `MatrixTokenFor` 是“容器矩阵标签的族映射”，不是真实矩阵；修正 `ColorSpaceRegistry` 顶部“最低 BT.709”的表述。
13. **测试**：把“源 ICC + 冲突 cICP”“AVIF 显式矩阵无 chroma”“输入矩阵 PSNR”“AVIF alpha”“TIFF 低置信度”做成门禁；这些格目前没有稳定的自动判据。

---

## 8. 未验证项（明确不做结论）

- ProPhoto→AVIF/JXL 在 `--color-carry-scope all` 下是否改为携带/拒绝；
- DCI-P3 源（smpte431）→AVIF 的 `bt709` matrix 白点冲突样本；
- `--color-out-matrix xyb` 在 JXL/Cjpegli 的实际落地；
- AVIF `--lossless` 的 identity matrix 强制；
- TIFF/WebP/AVIF 的 alpha 全组合；
- `ColorMatch` 源 ICC 的 `RefineKnownSpace` 吸附；
- HEIC/HEIF/JXR 的实际输出（能力表保守/无写入通路）；
- 全量 ICC v2/v4 在不同容器中的读回一致性。

这些不是“已验证不存在”，而是“本轮没有构造样本”。

---

## 9. 复现命令与证据索引

### 9.1 固定环境

```powershell
$root='C:\PLAN\ffmpegPictureUI\tests\output\_tmp\colormatrix-analysis'
$env:TMP="$root\round-tmp"; $env:TEMP="$root\round-tmp"; $env:TMPDIR="$root\round-tmp"
$env:FFMPEGGUI_PLAN_DIR='C:\PLAN\ffmpegPictureUI\publish\PLAN'
```

### 9.2 探针

```powershell
# 9×9 + named + ICC
dotnet run --project "$root\colormatrix-analysis.csproj"
# 矩阵锚点
& 'C:\PLAN\ffmpegPictureUI\tests\projects\ServiceProbe\bin\Release\net11.0\win-x64\ServiceProbe.exe' matrix
# 曲线/参照对账
& 'C:\PLAN\ffmpegPictureUI\tests\projects\ServiceProbe\bin\Release\net11.0\win-x64\ServiceProbe.exe' colormath
```

### 9.3 直接验证 `-colorspace` 与 `scale`

```powershell
$ff='C:\PLAN\ffmpegPictureUI\publish\PLAN\ffmpeg-full\ffmpeg.exe'
& $ff -y -hide_banner -i "$root\corpus\src_yuv_untagged.jpg" -frames:v 1 "$root\matrix\direct_default.png"
& $ff -y -hide_banner -i "$root\corpus\src_yuv_untagged.jpg" `
  -vf "scale=in_color_matrix=bt709:in_range=pc:out_range=pc" -frames:v 1 "$root\matrix\direct_bt709.png"
& $ff -hide_banner -i "$root\matrix\direct_default.png" -i "$root\matrix\direct_bt709.png" `
  -lavfi "[0:v][1:v]psnr" -f null -
# 期望平均 PSNR ≈ 28.55 dB；而 -colorspace bt709 -i ... 的两份产物是 inf
```

### 9.4 证据索引

| 证据 | 路径 |
|---|---|
| 72 组矩阵 | `run.log`、`COLOR_MATRIX_9X9_2026-11-14.csv` |
| 曲线/zimg 对账（19 PASS） | `colormath.log` |
| 冲突样本（源/产物） | `corpus/src_icc_cicp_conflict.png`、`grid3/conflict_to_png/` |
| P3 ICC→PNG（cICP+iCCP） | `grid3/p3_icc_png_logged/` |
| P3 ICC→AVIF 漂移 | `grid3/auto_avif_1..3/`、`grid3/p3icc_avif_engine/` |
| P3 ICC→WebP/JPG/TIFF | `webp/src_p3_icc/`、`jpg/src_p3_cicp/`、`grid3/` |
| ProPhoto 各出口 | `prophoto/to_png_logged/`、`to_avif/`、`to_jxl/`、`to_tiff/`、`to_jpg/` |
| HDR 各出口 | `hdr/hdr_pq_to_*`、`hdr/tiff_retry1..3.log` |
| alpha | `alpha/rgba_to_avif/`、`alpha/rgba_to_png/`、`alpha/rgba_to_webp/` |
| 手动目标 | `manual/Display_P3`、`DCI-P3`、`Adobe_RGB`、`ProPhoto_RGB`、`ColorMatch_RGB`、`BT.2020` |
| AVIF 矩阵落地 | `matrix/outmatrix_avif_*`、`matrix/avif_bt709_420/` |
| 输入矩阵无效果 | `matrix/legacy_yuv601_*`、`matrix/untagged_yuv_*`、`matrix/avif709_to_png_*` |
| JPEG/WebP range | `range/` |
| sRGB→TIFF 无 ICC | `grid/src_srgb_to_tiff/` |
| exiftool 稳定性 | 本文 §3.4 的 10 次 `-ver` 读数 |

---

## 附录 A：术语

- **CICP**：ITU-T H.273 的 `colour_primaries / transfer_characteristics / matrix_coefficients` 三元组；容器里常写作 nclx（AVIF/HEIF）或 cICP（PNG 3rd Ed）。
- **ICC PCS**：ICC 的 D50 XYZ 连接空间；本文所有矩阵都乘到 D50 再复合。
- **true matrix**：`p.Matrix`，浮点 3×3，像素转换用。
- **token**：`p.OutMatrix`，字符串，容器标注/命令用。
- **Carried**：容器/后端“能承载该矩阵标签”，不等于本次产物一定写入。
- **Redundant**：该 token 与目标/解码端固定语义相同，写了也没有额外信息。
- **Rejected**：该 token 在该格式没有正确承载位，或会制造解码端色偏。
