# ffmpegPictureUI 全色彩管线修复方案（2026-11-17）

> 对应审查：`docs/review-2026-11-17/COLOR_PIPELINE_INDEPENDENT_AUDIT_2026-11-17.md`
> 审计基线：`main@a3de42a`，版本位 `1.6.0-beta17`，`1.6.0BETA17^{commit}=957cfa8aa8c947e9cad533d085ec84877883ac07`
> 本文状态：**修复方案设计，未修改产品源码、未提交、未推送、未出包、未移动 tag**
> 用户已明确：不转 beta18；后续按 beta17 重出，tag 不得移动。版本/打包动作不属于本方案的执行范围，必须单独批准。

---

## 0. 结论先行

本轮审查确认的 **5 HIGH + 1 MEDIUM** 不是六个独立小 bug，而是四层“单一真值”缺失：

| 层 | 缺什么 | 造成的直接缺陷 |
|---|---|---|
| ICC 身份层 | “ICC 是否存在 / 是什么类型 / 能否矩阵化 / 能否携带”被压成一个 nullable 描述符 | F-01、F-03，并放大 F-02 |
| 来源声明层 | 程序自产中间件靠输入路径扩展名猜，而不是显式 provenance | F-05 |
| 输入矩阵层 | 传统链与 H1 各写一套“矩阵是否已消费”的判断 | F-04 |
| 外部进程路径层 | exiftool 切了 `WorkingDirectory`，参数仍用调用方相对路径 | F-06 |

因此修复顺序必须是：

1. **先拆身份/能力，再谈携带和映射**。合法 LUT ICC 必须至少被识别为“有 ICC”，不能在描述符为 null 时返回“无 ICC”。
2. **把“携带原 ICC”和“做像素映射”分开**。LUT 不能用一个 3×3 矩阵近似；没有 CMS 时映射必须明确拒绝，不能退回 sRGB 惯例。
3. **矩阵快路保持现状，为目标 ICC 增加独立的 LUT/CMS 慢路**。不是继续堆矩阵常数，也不是把 `IsMappable` 强行改成 true。
4. **来源、矩阵、路径各自收敛成单一真值对象**，由所有执行分支共同消费，禁止复制判断。

完整修法必须包含 **ICC LUT/CMS 慢路**。只做“合法 LUT ICC 携带、映射一律拒绝”是恢复正确性的必要第一步，但不是完整功能；本方案把它定为 **S1 安全收口**，完整交付要求 **S2 CMS 慢路** 一并完成。

---

## 1. 范围与成功定义

### 1.1 本方案覆盖

- F-01：合法 LUT/A2B/B2A ICC 被误判为“无 ICC”。
- F-02：显式 `--icc-file` 缺失/不可读/合法但不支持映射被静默忽略。
- F-03：源 ICC 与容器 CICP 冲突时无警告，LUT ICC 被 CICP 顶掉。
- F-04：H1 Map 分支不消费显式 `--color-matrix`。
- F-05：RAW 自产线性中间件的 provenance 因 `InputPath` 改写而失效。
- F-06：exiftool 子进程使用相对路径但在工具目录工作，导致后置元数据恢复失败。
- 与上述缺陷同链路的 **GainMap 第二入口**、**直通/legacy/engine 三通路**、**外部 ICC 显式输入契约**。
- 修复所需的 ServiceProbe 模式、门禁、登记表、测试夹具与独立 oracle。
- 审查报告中已定性为“测试假红/门禁陈旧”的 JXR 回读、`verify-color-wiring` 旧断言、`decision` 超时等收口项，避免修完后仍被旧门禁挡住。

### 1.2 明确不做

- 不改版本号、不移动/重打 `1.6.0BETA17`、不自行出包。
- 不用近似矩阵替换 LUT；不通过拟合把三维 CLUT 压成 3×3。
- 不把 PQ/HLG、透明 ProPhoto alpha、GainMap HDR 重建等“尚无逐值像素参考”的组合顺手宣称通过；如触及，只允许增加 oracle 后另行验收。
- 不改真实 UI 面板的显隐/禁用逻辑（本轮审查已把它单列为未覆盖项）；若修复必须动 UI，另开工作包并做真实桌面复核。
- 不把历史报告、代码注释或存量门禁绿态当作修复正确性的证明。

### 1.3 完成定义（Definition of Done）

一条 finding 只有同时满足以下五项才算关闭：

1. 有可复现的失败用例（或负控）并保存红态证据；
2. 产品代码在同一条执行链上修复，不是只修一个入口；
3. 有与产品代码独立的 oracle 或结构回读，不以“日志说对了”自证；
4. 门禁断言同步改写，且能对旧缺陷转红（负控有效）；
5. 最终读数绑定当轮 DLL 指纹，`git status -uno` 干净、tag 未动。

完整交付还必须满足：

- 合法 LUT ICC 在所有可携带出口要么原样携带，要么明确拒绝；**任何路径都不得再以“源无 ICC”或 sRGB 惯例覆盖它**。
- 显式 `--icc-file` 的三类失败（缺失、不可读、结构无效）全部 fail-closed，exit 91、零产物、稳定原因码。
- 显式映射 LUT 源到目标空间时，S2 必须通过独立 LittleCMS/Pillow oracle；S1 必须明确拒绝，不得静默产出。
- H1 与直通/legacy 对同一输入矩阵给出可解释的产物差异，且 H1 产物通过独立矩阵 oracle。
- RAW 真实队列的源描述来自 provenance，而不是临时文件名猜测；DNG 默认路由与“无标签 sRGB”路由不再像素相等。
- exiftool 相对路径的读、写、`-TagsFromFile`、`-stay_open` 全部通过；`wire` 转绿。

---

## 2. 缺陷—根因—工作包映射

| Finding | 严重度 | 现有根因接缝 | 修复工作包 | 最终判据 |
|---|---|---|---|---|
| F-01 | HIGH | `ColorSpaceIdentify.FromIccFile` 把“合法 profile 存在”和“能读出 colorants+curve”合并为返回 null；`ColorStrategyPlanning.PlanCarry` 只在 `SourceIccPath != null` 时进携带分支；`GainMapJpegCodec.ResolveEncodeParams` 自行探测 ICC | WP-01 / WP-02 / WP-03 | 两个真实 LUT profile 被识别为 `LutA2B`；携带出口保留原 ICC；推荐出口不再生成共同 sRGB ICC；映射出口 S1 明确拒绝、S2 过 oracle |
| F-02 | HIGH | `ColorIntentFactory.FromOptions` 的 `--icc-file` 分支只做 `IsValidIccProfile` + `FromIccFile`，失败静默；legacy 无消费者；`verify-color-wiring` 仍在断言坏 ICC 回落惯例 | WP-02 | 缺失/不可读/结构无效均 exit 91、零产物、`ExplicitIcc*` 原因码；合法矩阵 ICC 仍映射；合法 LUT ICC 不被写成“无 ICC” |
| F-03 | HIGH | ICC/CICP 优先级实际由“ICC 能否矩阵化”决定；LUT 描述符 null 时 CICP 直接胜出且无警告 | WP-02 / WP-03 | ICC 优先；无法证明 CICP 等值时抑制冲突 CICP并 `[warn]`；S2 可证明等价才双写 |
| F-04 | HIGH | H1 的 `vf` 只有 `format=rgb48le`/尺寸 scale；传统链的 `scale=in_color_matrix` 插入判据未复用 | WP-04 | `bt470bg` / `bt709` 的 engine H1 产物不同且各有独立 oracle；日志写真实消费矩阵；显式 engine 不可应用时 91 |
| F-05 | HIGH | `QueueProcessor` 把 `captured.InputPath` 改成 `*_raw.tiff`，`ColorIntentFactory` 用 `RawService.IsRawFile(inputPath)` 判断自产中间件 | WP-05 | `ColorSourceProvenance` 显式写入；RAW 中间件被识别为 `bt2020/linear`；DNG 默认与无标签 sRGB 不再像素相等；无 provenance 的负控不得被提升 |
| F-06 | MEDIUM | `ExifToolService` 多个进程站点把 `WorkingDirectory` 设为工具目录，但参数仍是调用方相对路径；绝对路径合并探测成功、相对返回 `File not found` | WP-06 | 所有公开入口绝对化源/目标/`-TagsFromFile`/tag 路径；相对 CLI 元数据恢复成功；`wire` 101/0（按新断言数绑定） |

---

## 3. 目标架构与冻结接口

> 这一节是并行工作包的共同契约。各工作包可以并行实现，但**不得各自发明第二套状态枚举/判断**。

### 3.1 ICC 身份：三层结果，不再用一个 nullable 描述符表达

新增 `IccProfileAnalysis`（建议文件 `src/FfmpegGui/Services/ColorMapping/IccProfileAnalysis.cs`）：

```csharp
public enum IccProfileStatus
{
    Absent,          // 没有 ICC 数据（文件确实无 ICC / 显式路径未给）
    Valid,           // 结构合法（签名、长度、tag 表可走）
    Unreadable,      // 文件存在但无法读取（权限/IO）
    Malformed        // 结构非法（签名、声明长度、tag 越界等）
}

public enum IccProfileKind
{
    None,
    MatrixTrc,          // 能构造现有 ColorSpaceDescriptor（rXYZ/gXYZ/bXYZ + rTRC/gTRC/bTRC）
    LutA2B,             // 有可用 A2B0..3（lut8/lut16/mAB）
    LutB2AOnly,         // 只有 B2A，不能把 device RGB 解到 PCS
    ValidButUnsupported // 合法 ICC，但无当前可解释的 RGB→PCS 变换
}

public sealed class IccProfileAnalysis
{
    public IccProfileStatus Status;
    public IccProfileKind Kind;
    public string? SourcePath;          // 稳定来源：显式外部 ICC / 原始容器；不承诺临时件仍存在
    public string? Sha256;
    public string? Description;
    public string? DataColorSpace;      // 例如 "RGB "
    public string? Pcs;                 // "XYZ " / "Lab "
    public int VersionMajor;
    public int DeclaredRenderingIntent;
    public bool HasA2B;
    public bool HasB2A;
    public int[] A2BIntents;            // 0..3 中实际存在者
    public int[] B2AIntents;
    public ColorSpaceDescriptor? MatrixDescriptor; // 仅 MatrixTrc 非空
    public string? Detail;
}
```

关键规则：

- `Absent` 只表示“数据确实不存在”，**绝不**表示“存在但读不出矩阵/曲线”。
- `Valid` + `LutA2B` 是合法 ICC；它的 `MatrixDescriptor == null` 是正常状态，不是错误。
- `ValidButUnsupported` 是合法 ICC 但不支持当前映射；它仍然可以被携带，不能被当作 `Absent`。
- `Malformed` 与 `Unreadable` 用于显式外部 ICC 的 fail-closed；嵌入源 ICC 的 `Malformed` 只警告并按 CICP/惯例继续（不把源文件直接判死）。
- `SourcePath` 对嵌入 ICC 指向原始容器输入路径；对显式 `--icc-file` 指向用户给定文件。**不要**把识别阶段已 `TryDeleteIcc` 的临时路径当成稳定路径。
- 新增分析字段必须进入 `ColorSpaceDescriptor.Clone()`（不能只克隆旧字段，导致缓存共享后状态漂移）。

实现方式：

- `ColorSpaceIdentify.IdentifyProfile(path, descriptionHint)` 返回完整 `IccProfileAnalysis`。
- 保留 `FromIccFile` 作为**矩阵专用兼容入口**，它只返回 `MatrixDescriptor`；调用方不得再用它判断“源是否有 ICC”。
- 新增 `ProbeInputIccProfile(inputPath)`（与 `ProbeInputColorMetadata` 共用 10 s 缓存），缓存 `IccProfileAnalysis`，执行侧需要时从原始 `inputPath` / 显式路径重新提取，不依赖已删临时件。
- `ColorMetadata` 增加 `IccProfileAnalysis? iccProfile` 和 `bool HasIcc`；旧的 `iccDescriptor` 保留为矩阵快路字段。

### 3.2 光栅源声明：显式 provenance 替代扩展名猜测

新增运行时结构（建议放 `FfmpegOptions` 下方，或者独立 `ColorSourceProvenance.cs`）：

```csharp
public enum ColorSourceOrigin
{
    None,
    RawIntermediate,      // 本程序 RAW 预处理生成
    UltraHdrDecoded,      // 本程序 Ultra HDR 解码生成
    ContainerDeclared     // 外部容器/命令行声明，不得当作“已知源”越过探测
}

public sealed class ColorSourceProvenance
{
    public ColorSourceOrigin Origin;
    public string? OriginalInputPath;   // 用户原始输入（RAW/Ultra HDR）
    public string? IntermediatePath;    // 实际送编码器的临时件
    public string? DeclaredPrimaries;   // 仅对 RawIntermediate/UltraHdrDecoded 信任
    public string? DeclaredTrc;
}
```

字段放置选择：**放 `FfmpegOptions` 作为运行时字段**，理由：

1. `FfmpegOptions.DeepClone()` 走 JSON，所有新增运行时字段会自动 clone；`Q` 队列、stdin、legacy/engine 分支都拿同一份。
2. 不改变 `ColorIntentFactory.FromOptions(options, inputPath)` 的现有签名，避免几十个调用点各传一份 provenance 而产生漂移。
3. `QueueItem.SourceInputPath` 继续保留，作为用户可见原始路径与 exiftool 后置恢复的路径来源。
4. **不进 `PresetData`**，防止预设把一次 RAW 中间件状态持久化。

`ColorIntentFactory` 的信任条件改为：

```csharp
bool isSelfMadeIntermediate =
    o.ColorSourceProvenance?.Origin is ColorSourceOrigin.RawIntermediate
                                or ColorSourceOrigin.UltraHdrDecoded;
```

不再出现 `RawService.IsRawFile(inputPath)` 决定自产中间件。`SourceIsRaw` 使用：

```csharp
bool sourceIsRaw =
    provenance?.Origin == ColorSourceOrigin.RawIntermediate
    || RawService.IsRawFile(provenance?.OriginalInputPath ?? inputPath);
```

`ContainerDeclared` 可以保留 `ColorSourceDecl*` 的现有下游行为，但**不赋予“本程序已知源”的置信度提升**；它必须仍走真正的标签/ICC 探测。

### 3.3 携带 vs 映射：两个独立决策

新增/扩展 `IccCarryPolicy`（现有 `Decide` 只接受“源真有 ICC”的 bool，必须升级为接受身份对象）：

```csharp
public static CarryIccOutcome Decide(
    ColorStrategy declared,
    IccProfileAnalysis? sourceIcc,
    bool containerCanCarryIcc,
    bool containerCanBothIccAndCicp,
    bool mappingRequired);
```

语义表：

| 源 ICC 状态 | Recommended（无显式目标） | CarryIcc | Manual/显式目标 | BakeCicpOnly |
|---|---|---|---|---|
| Absent | 现有 CICP/惯例路径 | 现有四结局 | 现有矩阵/CICP 路径 | 现有路径 |
| MatrixTrc valid | 现有矩阵快路/携带 | 携带原 ICC | 矩阵快路映射 | 现有 CICP 路径 |
| LutA2B valid | **携带原 ICC**；映射时 S1 明确拒绝、S2 走 CMS | 携带原 ICC；容器不可携带 ⇒ 拒绝 | S1 拒绝，S2 CMS 映射 | 先评估 ICC；不能评估则拒绝，不能静默取 CICP |
| LutB2AOnly / ValidButUnsupported | 可携带则携带；要求映射 ⇒ 拒绝 | 可携带则携带；否则拒绝 | 拒绝；S2 也不得假装可映射 | 拒绝 |
| Malformed / Unreadable（显式） | exit 91 | exit 91 | exit 91 | exit 91 |

补充规则：

- **携带不可能和像素映射同时成立**：当 `CarryIcc` 被功能覆盖（GainMap 底图映射、HDR 降 SDR）时，必须走映射；映射后的产物写目标 ICC，不得把源 LUT ICC 贴到已变换像素上。
- 推荐路径不得再把“合法 LUT ICC”当“无 ICC”去生成标准 sRGB ICC；LUT 不能矩阵化时，携带是默认，S1 映射拒绝，S2 映射由 CMS 完成。
- 输出容器能否携带任意 ICC 需要独立能力位，不能只看 `ExitWritesIcc`。AVIF 在显式硬件 AV1 编码器时仍不写 ICC 的旧口径已由 `AGENTS.md §7` 更正；本方案要求按当前编码器分支判定。
- 显式 `--icc-file` 的外部文件是稳定路径，可进 `SourceIccPath`；嵌入源 ICC 的临时件不是稳定路径，必须由后置/编码侧从原始输入重新提取。

### 3.4 ICC/CICP 优先级

新增一个中央判断（建议 `ColorSourceConflictPolicy`）：

1. **先确认 ICC 是合法显式数据**（`Valid` 而不是 `Malformed`）。
2. **再尝试证明 CICP 与 ICC 等价**：
   - `MatrixTrc`：用现有 `ColorSpaceDescriptor` 与 `ColorSpaceRegistry` token 比较，可精确判定。
   - `LutA2B`：S1 无法证明；S2 用 CMS 对 profile 的 PCS/目标做有限采样证明；无法证明时视为“未验证冲突”。
3. 规则：
   - 可证明等价 ⇒ 允许 ICC+CICP 双写，无警告。
   - 不能证明等价 ⇒ **ICC 优先**；输出若容器同时支持 ICC/CICP，抑制 CICP 只写 ICC，并产生 `[warn]` 稳定原因码。
   - `BakeCicpOnly` 是用户显式选择：若 ICC 无法评估，不得静默用 CICP 代表源身份；S1 明确拒绝，S2 先评估再按用户要求只写 CICP。
   - 任何输出都不得写出与所携带 ICC 自相矛盾的 CICP。

### 3.5 LUT/CMS 慢路

#### 为什么不能用矩阵增强替代

ICC 的 A2B/B2A 可以是：

- 多段 1D 曲线 + 3×3 矩阵 + **三维 CLUT**；
- 跨通道耦合（一个输出通道依赖 R/G/B 三者）；
- PCS 为 Lab 或 XYZ；
- 按渲染意图（感知/相对色度/饱和度/绝对色度）选择不同 A2B/B2A。

一个全局 3×3 矩阵 + 三条独立 TRC 不能表达三维 CLUT 的跨通道非线性；拟合矩阵只能近似，且会把误差隐藏在“标签正确”的产物里。因此：

- `ColorSpaceDescriptor.IsMappable` 继续只表示“矩阵快路可映射”，不扩大语义。
- 新增 `IccLutTransform` 作为**独立慢路**，按 profile 类型分派。
- 现有 matrix/TRC 快路保持原样；快路 vs 慢路的分派点是 `IccProfileAnalysis.Kind`，不是 `SourceIccPath` 是否存在。

#### 慢路实现范围（第一版完整交付）

建议新增：

| 文件 | 职责 |
|---|---|
| `Services/ColorMapping/IccProfileParser.cs` | 解析 ICC header、tag table、`rXYZ/gXYZ/bXYZ/rTRC/gTRC/bTRC`、`A2B0..3`、`B2A0..3`、`mAB `/`mBA `/`lut8`/`lut16`、`curv`/`para`、`wtpt/bkpt/chad` |
| `Services/ColorMapping/IccLutTransform.cs` | 1D/3D CLUT、三角形/四面体插值、Lab/XYZ PCS 转换、渲染意图选择、缓存 |
| `Services/ColorMapping/IccTransformEngine.cs` | 源 RGB 编码值 → PCS → 目标线性 RGB；按目标 primaries 矩阵和 TRC 输出；不改变现有 `ColorKernels` 矩阵语义 |
| `Services/ColorMapping/IccTransformPlan.cs` | 把 CMS 计划接入 `ColorTransformPlan` / `ColorBackend`，让 `RawColorPipeline` 或 in-process 出口消费 |

支持边界（必须写进代码与测试，不得默默扩大）：

- 仅支持数据色彩空间 `RGB `；CMYK/Lab 设备源、非 RGB profile 一律 `ValidButUnsupported`（可携带、不可映射）。
- 支持 A2B 源到 PCS 的正向变换；B2A-only 不做假装映射。
- 默认渲染意图：profile header 声明值（若对应 A2B 不存在则按 `A2B1 → A2B0 → A2B2 → A2B3` 回退）；黑点补偿默认 **off**。产品 UI 暂不暴露意图选择；S2 验收使用固定 intent + 固定 BPC 以保证 oracle 可比较。
- 不把现有 GMO（`GamutMapScalar`）与 ICC 感知意图叠加。用户显式 `--color-gamut-map` + LUT 源在 S2 首版 **明确拒绝**（`IccLutGamutMapUnsupported`），待单独设计 PCS 后处理再开。
- 16-bit 中间件优先：CLUT 以 16-bit 精度读入，像素在 float 线性域输出，再走现有编码。

#### 安全与性能

- ICC 是不可信输入：profile 长度上限 32 MiB；tag 偏移/长度必须落在声明范围内；CLUT 每维 ≤256（或按规范上限，超过即 `Malformed`/`Unsupported`）；禁止整数溢出和巨大临时分配。
- 解析失败必须是 `Unreadable`/`Malformed`，不能退化成“无 ICC”。
- `IccLutTransform` 缓存 key = profile SHA256 + intent + BPC；不可变对象，支持多线程只读。
- 性能验收：LUT 慢路只允许在 `Lut*` 源上启用；非 LUT 路径（矩阵/TRC、无 ICC、CICP）不得有可测回归。4K RGB48 单帧的 LUT 路径以本轮 `bench` 同机对照，若超过既有预算则记录并优化，不能悄悄关掉。

### 3.6 H1 输入矩阵：单一 `InputMatrixDirective`

新增 `InputMatrixDirective`（建议 `FfmpegCommandBuilder` 内部 public static 类型，或独立文件）：

```csharp
public enum InputMatrixApplicability { Applicable, SourceIsRgb, GroupedUnsupported, ProbeUnknown }
public sealed class InputMatrixDirective
{
    public bool Required;              // 用户显式给了 --color-matrix
    public string? NormalizedMatrix;   // ToFfmpegInputMatrixName 的产物
    public string? InputRange;         // 现有 ProbeInputColorRange
    public InputMatrixApplicability Applicability;
    public string ReasonCode;
    public string? FilterFragment;     // 例如 scale=in_color_matrix=bt709:in_range=tv:out_range=tv
}
```

唯一真值：由 `FfmpegCommandBuilder.ResolveInputMatrixDirective(inputPath, options, probeMeta, groupForm)` 产生；以下两处共同消费：

1. 传统链 `BuildColorArgsSplit` 的 `-colorspace` 声明与 `-vf scale=in_color_matrix=...` 插入；
2. H1 的 `RawColorPipeline` 滤镜链。

行为契约：

- 用户未显式给 `--color-matrix` ⇒ `Required=false`，不改变任何既有 auto 像素路径。
- `Required=true` 且非 RGB、单流 ⇒ `Applicable`；传统链与 H1 都必须插入**同一个** `FilterFragment`。
- `Required=true` 且 `SourceIsRgb` ⇒ 警告“矩阵只保留容器声明，不改变像素”；不插滤镜。
- `Required=true` 且 `GroupedUnsupported` / `ProbeUnknown`：
  - 显式 `--color-engine engine` ⇒ `Action=Reject`，exit 91、零产物，日志点名无法应用；
  - `auto` 路由在 H1 执行前预检，回退 legacy（legacy 仍按同一 directive 插滤镜）；不得先写产物再回退。
- H1 滤镜链顺序：矩阵/范围 scale → `format=rgb48le` → 尺寸 scale；不得把尺寸 scale 放在矩阵 scale 之前，否则矩阵作用在拉伸后的 YUV 上，语义仍错。
- 直接 ffmpeg 组的 `-colorspace` 是**输出/声明**，不是“已消费矩阵”证据；日志必须区分：
  - `input_declared`：`-colorspace` 声明；
  - `input_matrix_consumed`：`scale=in_color_matrix` 真正改变解码。
- `ColorStrategyPlanning` 里现有“矩阵无载体/不适用”的固定文本必须改为读取 directive 的 ReasonCode，不得继续写一条与执行层相反的理由。

### 3.7 RAW provenance

写入点（`QueueProcessor`）：

1. 在改写 `captured.InputPath` 之前保存 `captured.SourceInputPath = originalInputPath`（现有）；
2. RAW 预处理成功、得到 `rawTiff` 后，在同一个原子块中设置：
   - `Options.ColorSourceProvenance = new { Origin=RawIntermediate, OriginalInputPath=captured.SourceInputPath, IntermediatePath=rawTiff, DeclaredPrimaries="bt2020", DeclaredTrc="linear" }`
   - 同步保留现有 `ColorSourceDeclPrimaries/Trc`（兼容 BuildColorArgsSplit）；
3. Ultra HDR 解码写入 `Origin=UltraHdrDecoded`，原始为 JPEG，中间件为 PFM/TIFF；
4. 任何其他路径不得设置 `RawIntermediate` / `UltraHdrDecoded`。

消费规则：

- `ColorIntentFactory` 只在 `Origin` 是自产枚举时信任 `DeclaredPrimaries/Trc`；
- 描述符名称写“RAW 中间件（dngtool 去马赛克线性输出）”或“Ultra HDR 解码中间件”，不再依赖路径扩展名；
- `ColorIntent.SourceIsRaw` 与 `ColorTransformPlan.SourceIsRaw` 使用 provenance 的 `OriginalInputPath`；
- 中间件被 `QueueProcessor` 清理后，后置元数据恢复仍用 `SourceInputPath`，不用中间件路径。

负控：

- 直接构造 `FfmpegOptions`，设置 `ColorSourceDeclPrimaries/Trc` 但 `ColorSourceProvenance=None`，送 `ColorIntentFactory.FromOptions(temp.tiff)` ⇒ 不得被提升为 `bt2020/linear`，必须按普通无标签 TIFF 走惯例。
- 这保证修复不是“谁填了声明字段谁就赢”。

### 3.8 exiftool 路径

在 `ExifToolService` 内新增公共辅助，所有入口在构造参数前完成：

```csharp
private static string AbsArg(string path, string caller)
{
    // 捕捉当前进程 CWD，而不是 exiftool 的 WorkingDirectory
    var full = Path.GetFullPath(path); // 相对路径按调用方 CWD 解析
    return full;
}
```

必须覆盖的进程站点（以当前代码为准，新增站点也要覆盖）：

- `ReadIsoAsync`
- `ReadColorTagsOnceAsync`
- 单 tag 读取（`GetTagAsync` / 对应的 `RunAsync` 调用）
- `CopyMetadataAsync` / `CopyMetadataSafeAsync`（源与目标、`-TagsFromFile`）
- `EmbedIccProfileFromFileAsync`（ICC 源与产物目标）
- 通用 `RunAsync`
- `-stay_open` 批处理（每个文件参数都要在发送前归一化）
- 任何 `-o` / `-overwrite_original` / `-tagsFromFile` 路径

约束：

- `WorkingDirectory` 继续指向 exiftool 目录（`EnsureExifToolDllDirOnPath` 的 PATH 注入保留）；只改参数路径，不改变工具目录工作目录。
- 相对路径仍按用户调用时的 CWD 解析；不能按 exiftool 目录解析。
- 带空格、CJK、`[ ]` 的路径必须用现有参数引号策略正确引用；不允许把绝对化后的路径直接裸拼。
- 失败仍然保留现有警告/色彩保护模式：元数据恢复失败不能让已经正确的颜色产物变成零产物。
- `wire` 的 100/1 必须由这条修复转绿；若新增断言导致条数变化，按新条数绑定，不沿用旧 101 的假设。

---

## 4. 工作包

### WP-00：测试基线与 oracle（先做，不改产品）

**目标**：把六条缺陷变成可重复的红/绿判据，并准备独立参考。

**新增/修改**：

- `tests/scripts/verify-color-icc-profiles.ps1`（新门禁，F-01/F-02/F-03 + GainMap 第二入口）
- `tests/scripts/verify-raw-provenance.ps1`（新门禁，F-05）
- `tests/scripts/_make-icc-lut-reference.py`（tracked；用 Pillow `ImageCms` 生成参考，禁止被产品代码调用）
- `tests/fixtures/color/icc-lut-reference.json`（由上面的脚本生成并入库，含两个 profile 的 SHA、intent、BPC、125 个 patch 的 16-bit 期望值）
- 扩展现有 `verify-color-matrix-full.ps1`（F-04，不新增门禁）
- 扩展 `verify-color-wiring.ps1`（F-02 外部 ICC 段改 fail-closed；F-06 相对路径；JXR PATH 假红修复）
- `ServiceProbe` 新/扩展模式：
  - `icc-profile`：输入路径 → `IccProfileAnalysis` JSON（状态/种类/A2B 意图/描述符 null 与否/SHA）
  - `icc-lut`：输入 profile + 目标 profile/目标空间 + patch 表 → 变换后的 16-bit RGB 值
  - 扩展 `plan` / `contract` 输出：`SourceIccStatus`、`SourceIccKind`、`SourceIccSha`、`Action`、`ReasonCode`
- `tests/projects/Tests.Shared/TestGroups.cs` 登记上述模式；`_probe-test-group-coverage.ps1` 同步。

**Oracle 约定**：

- LUT：Pillow 12.3.0 `PIL.ImageCms`（LittleCMS）在**独立进程**生成参考；产品实现不得 import/调用 Python。参考 JSON 是静态入库，门禁只读 JSON 对撞产品 `icc-lut`。已实测两个真实 profile 均能被 `ImageCms.buildTransformFromOpenProfiles(..., renderingIntent=1)` 打开并出像素，oracle 路径成立。
- 矩阵：复用 `probe_math_oracle.py` 的 NumPy 参考，不重复造。
- RAW：新增 `tests/scripts/_probe-raw-provenance-oracle.py`，读取中间件 `rgb48`，按 linear Rec.2020 → 目标空间公式计算期望；不 import 产品代码。
- 参考生成脚本记录：Pillow 版本、profile SHA256、intent、BPC、像素采样。任何参数改变都视为新参考，不能悄悄改 JSON。

**红态证据**：

- 两个真实 LUT profile 在当前基线上分别得到“源无 ICC”；保存 stdout、退出码、产物哈希到 `tests/output/_tmp/color-fix-20261117/`。
- 三组缺失/无效 ICC PNG 当前仍 1393 B 同 SHA；保存。
- 当前 H1 两档 4472 B 同 SHA；保存。
- DNG `default_png_pixels.rgb48` 与 `intermediate_png_pixels.rgb48` 同 SHA `E35438F4…`；保存。
- exiftool 相对路径 `File not found`；保存。

**完成判据**：红态证据可重放；参考 JSON 能被独立校验；新门禁在旧代码上红，并且不是“脚本没输出”的假红。

---

### WP-01：ICC 身份层（F-01 的结构修复）

**目标**：让“有 ICC / 无 ICC”和“能否矩阵化”彻底分离。此包先不实现 CMS。

**改文件**：

- `Services/ColorMapping/IccProfileAnalysis.cs`（新增）
- `Services/ColorMapping/ColorSpaceIdentify.cs`
- `Services/ColorMapping/ColorIntent.cs`
- `Services/ColorMapping/ColorIntentFactory.cs`
- `Services/FfmpegCommandBuilder.cs`（缓存与 `ProbeInputIccDescriptor` 旁路 `ProbeInputIccProfile`）
- `src/FfmpegGui/Services/ColorMapping/ColorSpaceDescriptor.cs`（Clone/字段）
- `Services/GainMapJpegCodec.cs`（只做第二入口的身份读取切换）

**实现步骤**：

1. 增加 `IccProfileParser` 的“结构分析”最小版本：只做 header/tag table/关键 tag 存在性，不读 CLUT 数据；`Valid + LutA2B` 必须能被判定。
2. `ColorSpaceIdentify.IdentifyProfile` 先用 parser 得出 `Status/Kind`，再只在 `MatrixTrc` 时调用现有 `FromIccFile` 填 `MatrixDescriptor`。
3. `ProbeInputIccDescriptor` 的调用方改用 `ProbeInputIccProfile`；对矩阵快路仍只取 `MatrixDescriptor`。
4. `ColorIntent` 增加 `SourceIccAnalysis`（或 `SourceIcc`），`Clone()` 深拷贝；`ColorTransformPlan` 增加 `SourceIccStatus/Kind/Sha`，只用于计划和日志，不把临时路径当执行路径。
5. `GainMapJpegCodec.ResolveEncodeParams` 的 `iccNeeded` 判断从“描述符是否非空”改为“源 ICC 是否存在 + 输出能否携带”，并记录 LUT 身份；暂不映射（映射留给 WP-03）。
6. 所有 `SourceIccPath != null` 的携带分支改为看 `SourceIccStatus == Valid`，路径是否可用于执行由执行侧从原始输入重建。

**测试**：

- `ServiceProbe icc-profile` 对两个真实 LUT profile 断言：
  - `Status=Valid`
  - `Kind=LutA2B`
  - `HasA2B=true`，A2B 意图集合非空
  - `MatrixDescriptor=null` **不是** `Absent`
  - `Sha` 与报告给定 SHA 一致
- 对无 ICC 的图像断言 `Absent`。
- 对损坏字节断言 `Malformed`，对不存在/无权限断言 `Missing`/`Unreadable`（字段可合并为状态）。
- 负控：旧的 `IsValidIccProfile=true + FromIccFile=null` 组合不得再产生 `Absent`。

**完成判据**：F-01 不再出现“源无 ICC”的假陈述；合法 LUT 源在计划层有 `LutA2B` 身份；矩阵快路对既有图像逐字节不变（用 sRGB/Pro/Photo 现有 oracle 对撞）。

**回滚**：本包只新增字段和旁路，可独立 revert；若 revert，F-01 安全修复回退到旧缺陷，不允许带着旧缺陷出包。

---

### WP-02：来源语义、显式 ICC fail-closed、CICP 冲突（F-01/F-02/F-03）

**目标**：把“携带/映射/拒绝”收敛到单一策略，并让显式外部 ICC 的失败成为真正的失败。

**改文件**：

- `Services/ColorMapping/IccCarryPolicy.cs`
- `Services/ColorMapping/ColorStrategyPlanning.cs`
- `Services/ColorMapping/ColorIntentFactory.cs`
- `Services/ColorMapping/ColorSourceConflictPolicy.cs`（新增）
- `Services/ColorMapping/ColorIntent.cs`（错误码/警告字段）
- `Services/ColorMapping/ImageEncoderArgs.cs`（稳定 ASCII 原因与中文 Reason）
- `Services/QueueProcessor.cs`（错误码/任务终局；GainMap 覆盖）
- `Services/GainMapEncoder.cs` / `Services/GainMapJpegCodec.cs`（若映射/携带发生）
- `tests/scripts/verify-color-wiring.ps1`（旧断言同批改写）

**实现步骤**：

1. **外部 ICC 校验前移**：新增 `ColorInputContract.Validate(options)`，在 `ColorIntentFactory.FromOptions` 最前面和 legacy 命令行装配前各调用一次（同一对象，不复制规则）。
   - `IccFilePath` 为空 ⇒ 跳过；
   - 不存在 ⇒ `ExplicitIccNotFound`；
   - 存在但 `File.Exists=false`/目录/IO 失败 ⇒ `ExplicitIccUnreadable`；
   - 结构非法 ⇒ `ExplicitIccMalformed`；
   - 合法但不支持的 RGB 变换/非 RGB profile ⇒ `ExplicitIccUnsupported`（可携带场景除外）。
   - **不 catch-and-ignore**；异常映射为对应原因码，不能把显式输入静默降为“源内嵌 ICC / 惯例 sRGB”。
2. `ColorIntentFactory` 的 `--icc-file` 分支：
   - 合法 `MatrixTrc` ⇒ 现有 `src` + `SourceIccPath`（外部稳定路径）；
   - 合法 `LutA2B` ⇒ `SourceIccAnalysis` 填 LUT，`src` 可以为 null，但 `SourceIccPath` 保留为外部路径；
   - 不可读/非法 ⇒ 通过 `ColorInputContract` 返回 fatal，不进入规划。
3. `IccCarryPolicy.Decide` 接收 `SourceIccAnalysis` 和 `mappingRequired`；所有调用点（engine 计划、legacy 计划、QueueProcessor 的 ICC 后置、GainMap）统一改为它，禁止再用 `SourceIccPath`/`AttachIcc` 字段形状推断。
4. `ColorSourceConflictPolicy`：
   - 输入 ICC + CICP 都有：按 3.4 判断等价；
   - 不可证明 ⇒ `[warn] COLOR_ICC_CICP_CONFLICT`，输出抑制冲突 CICP，只保留 ICC；
   - `BakeCicpOnly` 遇到不可评估 ICC ⇒ `IccCicpOnlyRequiresCms`（S1 拒绝，S2 复核后再放行）。
5. legacy：
   - 显式外部 ICC 至少完成校验；
   - 合法 `MatrixTrc` 可在 legacy 按现有 token/`zscale` 路径使用；
   - 合法 `LutA2B` 无 CMS 时，若要求像素映射 ⇒ `LegacyCannotMapLutIcc`，不得静默忽略；
   - 不要求映射且容器可携带 ⇒ 允许带 ICC 的零像素路径；不能携带 ⇒ 明确拒绝。
6. GainMap 覆盖：
   - GainMap 底图必须映射时，`CarryIcc` 被覆盖为映射；映射后的 JPEG 写目标基色 ICC，不能写源 LUT ICC；
   - `ResolveEncodeParams` 不再因 `ProbeInputIccDescriptor` 返回 null 就走“无 CICP 且无可用 ICC ⇒ sRGB 惯例”；先看 `HasIcc + Kind`，`Lut` 时进入 CMS/拒绝。
7. 错误码/日志：
   - 新增集中常量：`COLOR_ICC_*`、`COLOR_CICP_*`、`COLOR_MATRIX_*`、`COLOR_RAW_PROVENANCE_*`、`COLOR_EXIFTOOL_*`；
   - 终局文本保持 ASCII 原因 + 现有中文 Reason 同义句；不得只写一句“回退惯例”。
8. 修改 `verify-color-wiring.ps1` 的旧断言：
   - 原 `(8)` 中“无效/不存在 ICC 回落惯例且不冒认外部 ICC”必须改为“exit 91、零产物、`ExplicitIcc*` 原因码”；
   - 原“无 ICC 默认直通”保留为另一条正控；
   - 外部 valid LUT 的正控加入，不能只测 missing。

**测试矩阵**：

- 外部 ICC：不存在、目录、0 B、127 B、坏签名、合法 MatrixTrc、合法 LutA2B、非 RGB profile。
- 每个失败：`exit=91`、`products=0`、原因码存在、没有 `source=...convention` 的替代产物。
- 合法 MatrixTrc：与内嵌同 profile 的映射结果对撞（同 PSNR/产物哈希）。
- 合法 LutA2B：`icc-profile` 身份正控；推荐/携带不退回 sRGB；显式映射在 S1 `ColorAction.Reject`。
- 冲突：LUT ICC + P3 CICP → `COLOR_ICC_CICP_CONFLICT`，输出无矛盾 CICP；MatrixTrc ICC + 反向 CICP → 同。
- no-conflict：一致的 MatrixTrc + CICP → 不产生警告。
- `BakeCicpOnly` + LUT → S1 reject；S2 评估后再决定。

**完成判据**：F-02 三组 1393 B 同 SHA 产物变成零产物；显式坏 ICC 不再有“成功产物”；F-03 有稳定警告且无 CICP 覆盖 ICC。

---

### WP-03：LUT/CMS 慢路（F-01 完整功能）

**目标**：在不破坏矩阵快路的前提下，让 `LutA2B` 源可以真正映射到显式目标/推荐目标，并通过独立 CMS oracle。

**前置**：WP-01 的 `IccProfileAnalysis` 接口合并；WP-02 的 `ColorBackend`/拒绝策略合并。**不要**在 WP-01 里提前把 LUT 声明为可映射。

**改文件**：

- `Services/ColorMapping/IccProfileParser.cs`（新增，完整 tag/CLUT 解析）
- `Services/ColorMapping/IccLutTransform.cs`（新增）
- `Services/ColorMapping/IccTransformEngine.cs`（新增）
- `Services/ColorMapping/ColorTransformPlan.cs`（新增 `ColorBackend.Cms` 或复用未消费的 `Lut` 命名，需在代码注释中写清语义）
- `Services/ColorMapping/ColorStrategyPlanning.cs`（按 Kind 分派；目标显式时启用 CMS）
- `Services/ColorMapping/ColorKernels.cs` 或 `Services/ColorMapping/ColorMappingEngine` 的像素循环（只新增 CMS 分支，不改矩阵分支）
- `Services/RawColorPipeline.cs`（若 LUT 源进入 in-process RGB 路径，接 CMS 分支）
- `Services/GainMapJpegCodec.cs` / `GainMapEncoder.cs`（GainMap 底图 CMS）
- `Services/ColorMapping/IccCarryPolicy.cs`（`mappingRequired` 的最终判定）

**实现步骤**：

1. 解析器只读 header/tag table → tag 内容；所有偏移/长度做边界检查。先把 `lut8`/`lut16` 和 `mAB `/`mBA ` 读取为明确的 `IccLutModel`（输入表、CLUT、输出表、矩阵、PCS）。
2. 实现 PCS：
   - XYZ：ICC XYZ 16-bit 编码按 v2/v4 规则还原；
   - Lab：ICC Lab 16-bit 编码 → 浮点 Lab → XYZ D50；
   - 白点/色度适配：按 `wtpt`/`chad`/`bkpt` 的最小正确路径实现，不能忽略 `chad` 导致 D50 错。
3. 插值：
   - 输入一维表逐通道；
   - 3D CLUT 采用四面体插值（与 LittleCMS 对比；如果 oracle 显示差异，先修正索引/边界，不得加容差掩盖）；
   - 输出一维表；
   - 对 `lut8` 的 8-bit 表值按规范归一化，不把 8-bit 表当 16-bit。
4. 目标：
   - 目标描述符为矩阵/TRC：用现有 `ColorSpaceDescriptor` 的 `RgbToXyzD50` 逆变换；
   - 目标为显式标准空间：现有 registry/token 生成；
   - 输出编码走现有 `ColorKernels.EncodeFromLinearInPlace`/编码分支，不另造第二套 TRC。
5. 计划：
   - `Kind=MatrixTrc` 仍走现有 `ColorBackend.InProcess`/矩阵路径；
   - `Kind=LutA2B` + `mappingRequired` ⇒ `ColorBackend.Cms`；
   - `Kind=LutB2AOnly`/`ValidButUnsupported` ⇒ `Reject`；
   - `CarryIcc` + 不映射 ⇒ 仍 `CarryIcc`，不因 CMS 可用就改变用户策略。
6. 渲染意图：
   - 默认 profile header intent；若对应 A2B 不存在，按 `1→0→2→3` 回退，并记录实际使用的 intent；
   - BPC off；
   - 不接受 `--color-gamut-map` 与 LUT 同时开启（S2 首版显式拒绝）。
7. 失败语义：
   - 解析/内存/不支持 ⇒ `IccMappingUnsupported`（合法但不可映射）或 `IccProfileMalformed`（结构非法）；
   - 不得生成近似 sRGB ICC；
   - 不得在计划层说 `Map` 而执行层用矩阵近似。

**测试**：

- `icc-lut` 对两个真实 profile × 目标 `sRGB`/`Display P3` × intent 固定值 × 125 patch，与 `icc-lut-reference.json` 对撞：
  - 16-bit 每通道容差 ≤2 LSB（若四面体与 LittleCMS 完全一致则应为 0；容差是防浮点路径差异，不是掩盖错误）；
  - 任何单点超差都必须检查组内最大误差和具体 patch，不允许只报平均。
- 端到端 PNG（无损）：
  - `sRGB_ICC_v4_Appearance` 源 → P3 目标，像素与 oracle 逐 patch 对撞；
  - `MartiMaria_browsertest_A2B` 源 → P3 目标，同上；
  - 两个源产物不得因为都写同一个 sRGB ICC 而变得不可区分。
- AVIF/WebP/JXL：
  - 无损/近无损档做结构 + 像素阈值；有损档做 PSNR 阈值，不用逐字节；
  - 输出 ICC 与目标 profile SHA 对齐（或明确的容器能力拒绝）。
- GainMap：
  - LUT 源 + SDR 底图：若不映射，仍可携带源 ICC；
  - LUT 源 + HDR 底图：CMS 映射底图，JPEG 写目标基色 ICC，不写源 LUT ICC；
  - 无 CMS 可评估时 S1 拒绝，S2 过像素 oracle。
- 负控：矩阵源仍走旧路径，`icc-lut` 不改变其产物；LUT 源请求 `--color-gamut-map` 明确拒绝，不静默忽略。

**完成判据**：S2 的 LUT 映射产物通过 LittleCMS 独立 oracle；推荐路径不再把 LUT 源压成共同 sRGB ICC；用户显式目标得到正确像素而非只有标签。

**回滚**：CMS 提交独立；revert 后只回到 S1 的“携带或拒绝”，不会回到静默 sRGB。若不能保持这个中间态，不允许拆出该提交。

---

### WP-04：H1 输入矩阵（F-04）

**目标**：让 H1 和传统链共用同一份“输入矩阵是否已消费”的真值。

**改文件**：

- `Services/FfmpegCommandBuilder.cs`（`BuildColorArgsSplit` 的 `declMatrix`/scale 段）
- `Services/FfmpegCommandBuilder.Decision.cs`（日志/计划/verdict 文本）
- `Services/RawColorPipeline.cs`（H1 滤镜链）
- `Services/ColorMapping/ColorStrategyPlanning.cs`（删除“矩阵无载体”假陈述）
- `Services/ColorMapping/ColorEngineRouter.cs`（auto 预检回退/显式拒绝）
- `tests/projects/Tests.Shared/TestGroups.cs` 的 `matrix` 模式输出（如需要）

**实现步骤**：

1. 抽出 `ResolveInputMatrixDirective`，在传统链和 H1 都调用；现有 `BuildColorArgsSplit` 的判据原样迁移，不改变 auto 路径。
2. `RawColorPipeline`：
   - 保留 `format=rgb48le` 与 max-dimension scale；
   - 当 directive `Applicable` 时，在 `format` 前插入 `scale=in_color_matrix=...`（范围参数与现有传统链完全同源），再执行几何 scale；
   - 当 `SourceIsRgb` 时只警告；
   - `GroupedUnsupported`/`ProbeUnknown` 且显式 engine ⇒ 抛出/返回计划 Reject，零产物；
3. `ColorEngineRouter`：
   - `auto` 在 H1 开始前用同一 directive 预检；不可应用则回退 legacy；
   - 显式 `engine` 不捕获 typed reject 后静默回退；错误必须透传终局 exit 91。
4. 日志：
   - 计划/verdict 使用 `InputMatrixDirective.ReasonCode`，删除“backends do not consume input matrix / no carrier”的固定句；
   - 日志明确写出 `scale=in_color_matrix=...` 和输入像素格式/RGB 判定来源。
5. 输出标签：
   - 输入端 `-colorspace` 仍是声明；
   - 输出端 `-colorspace` / `-color_primaries` 仍按现有 caps 写入；
   - 不因修 F-04 把输出标签误改成输入矩阵。

**测试**：

- 直通 ffmpeg 组：`bt470bg` / `bt709` × engine/legacy，产物应分别不同；用独立 `ffmpeg` 直通命令做参考（不通过产品代码）。
- H1 组：同输入 MJPEG，`--color-matrix bt470bg` / `bt709` × 目标 P3，engine 两档产物 SHA 不同；legacy 两档也不同；H1 与参考的 per-channel 误差阈值（无损 PNG 优先逐字节/≤1 LSB）。
- 日志断言：
  - engine/legacy 都有 `scale=in_color_matrix=bt470bg`/`bt709`；
  - 不再出现“矩阵不被引擎消费”；
  - 直通组的 `input_declared` 与 `input_matrix_consumed` 分开。
- RGB 源 + `--color-matrix`：只警告，不插滤镜，产物可读。
- grouped/未知像素格式 + 显式 engine：exit 91、零产物、原因码；auto 回退 legacy 后正常产物。
- 输出矩阵 `verify-color-out-matrix` 不受影响。

**完成判据**：F-04 的 4472 B 同 SHA 现象消失；两个矩阵档在 engine H1 上不再同产物；oracle 与直通参考一致。

---

### WP-05：RAW provenance（F-05）

**目标**：来源信息由写者显式设置，`ColorIntentFactory` 不再靠临时文件名猜。

**改文件**：

- `Models/FfmpegOptions.cs`（`ColorSourceProvenance` 运行时字段，`DeepClone` 自动携带）
- `Services/QueueProcessor.cs`（RAW/Ultra HDR 写者）
- `Services/ColorMapping/ColorIntentFactory.cs`（信任条件）
- `Services/ColorMapping/ColorIntent.cs` / `ColorTransformPlan.cs`（`SourceIsRaw`）
- `Services/RawColorPipeline.cs`（如需把 provenance 传给 in-process 引擎）
- `tests/projects/ServiceProbe`/`Tests.Shared`（plan 模式输出 provenance）

**实现步骤**：

1. 新增 `ColorSourceOrigin`/`ColorSourceProvenance`。
2. `QueueProcessor` 在 RAW 预处理完成、`captured.InputPath = rawTiff` 同一处，原子写入 `Origin/OriginalInputPath/IntermediatePath/DeclaredPrimaries=bt2020/DeclaredTrc=linear`；`SourceInputPath` 保留。
3. Ultra HDR 解码同样写 `UltraHdrDecoded`。
4. `ColorIntentFactory` 的 `isSelfMadeIntermediate` 只看 provenance；删除 `RawService.IsRawFile(inputPath)` 对临时件的判断。
5. `SourceIsRaw` 用 provenance 原始路径；`RawService.IsRawFile` 仅用于外部输入判断，不用中间件路径。
6. 兼容旧队列项：`Provenance=None` 时不得凭 `ColorSourceDecl*` 猜；如果 RAW 预处理路径未设置 provenance，这属于实现遗漏，测试应红，而不是在产品里 fallback。
7. 不把 provenance 放进 `PresetData`；`DeepClone` 仍通过 JSON 自动拷贝，补一条单测断言 clone 后仍保留。

**测试**：

- `ServiceProbe plan` 三组：
  1. 直接 `FromOptions(raw.dng)`：无 provenance 时按原 RAW 探测；
  2. `FromOptions(raw.tiff)` + `Origin=RawIntermediate` + 声明：源为 `bt2020/linear`，`SourceIsRaw=true`；
  3. `FromOptions(raw.tiff)` + 只有 `ColorSourceDecl*`、`Origin=None`：不得提升，按无标签 TIFF 惯例。
- 真实队列 DNG（`tests/fixtures/raw_src_8bit.dng`）：
  - 目标 P3 PNG / sRGB JPEG / GIF，Map 与 no-map 两档；
  - 日志不得出现“按 sRGB 惯例”；
  - `raw\default_png_pixels.rgb48` 与 `raw\intermediate_png_pixels.rgb48` 不得再同 SHA；用 `_probe-raw-provenance-oracle.py` 算期望，逐通道阈值 ≤1 LSB/16-bit（按路径）。
- 无 provenance 的无标签 TIFF 负控：与普通惯例路径一致，不得被当作 `bt2020/linear`。
- 组合：RAW + GainMap、RAW + 目标矩阵、RAW + HDR 目标，至少各 1 条；没有 oracle 的组合不得写成“像素通过”，只做结构/日志断言。

**完成判据**：DNG 默认路由与无标签 sRGB 路由像素差异可解释且过 oracle；`SourceIsRaw` 不再依赖 `inputPath` 扩展名。

---

### WP-06：exiftool 路径（F-06）

**目标**：所有 exiftool 文件参数在进入进程前绝对化；不改变工具目录和 PATH 机制。

**改文件**：

- `Services/ExifToolService.cs`
- `tests/scripts/verify-color-wiring.ps1`（相对路径用例；wire 假红修复）
- 可能涉及的 `QueueProcessor` 后置调用（确保传原始 `SourceInputPath` 而不是中间件）

**实现步骤**：

1. 新增 `AbsArg`，捕捉 `Environment.CurrentDirectory`，对路径做 `GetFullPath`；参数为空/不存在要按现有语义处理，不能把空字符串变成 CWD。
2. 对每个 `ProcessStartInfo.Arguments` 构造点逐个替换：源、目标、`-TagsFromFile`、`-o`、`-stay_open` 文件、ICC 源/目标、批量列表。
3. 保留 `WorkingDirectory=工具目录`、`EnsureExifToolDllDirOnPath`、重试 `perl532.dll code 126`。
4. 统一引号：`QuoteArg(fullPath)`；带空格/CJK/方括号路径必须在单测/门禁中覆盖。
5. 日志中的文件名仍可显示原相对路径（用户可读），但传给进程的必须是绝对路径；不要让日志改参数。
6. 失败时保留 `[warn]` 与颜色保护模式；元数据失败不能使颜色产物消失。

**测试**：

- 绝对路径合并探测：4 个标签 + ICC base64 成功。
- 相对路径合并探测：同 4 个标签，不再 `File not found`。
- 相对源 + 相对输出目录 CLI：PNG/JPEG 的 `DateTimeOriginal`/`ICC_Profile` 与绝对路径组逐值一致。
- 带空格/CJK 目录的同一组。
- `ServiceProbe wire`：新断言数下 100% PASS；若新增“相对路径”断言，计数按实际条数绑定，不能沿用旧 101 假设。
- exiftool 缺失/破损：仍走现有警告 + sRGB 惯例/拒绝语义；该失败不得被误归因到 F-06。

**完成判据**：`wire` 的 F-12 症状从产品缺陷变为通过；相对路径后置元数据恢复不再跳过。

---

### WP-07：门禁、登记与文档

**目标**：修复不是“代码绿了”，而是“新契约被门禁锁住，旧缺陷负控会红”。

**门禁变更**：

- 新增 `verify-color-icc-profiles.ps1`、`verify-raw-provenance.ps1`；总门禁从 **104 → 106**。
- `_run-step3-gates.ps1` 同批改齐四处：
  1. `$scriptList` 两条新项；
  2. `$expectedScriptCount = 106`；
  3. runner 散文里的条数陈述（`Test-RunnerShape` ⑰ 三形态）；
  4. 新增 ServiceProbe mode 进 `tests/projects/Tests.Shared/TestGroups.cs` 组表；
  并由 `_probe-test-group-coverage.ps1` 认领 `icc-profile`/`icc-lut`。
- `_module-registry.json` 增加两条 gate carrier；`verify-module-registry.ps1` 重新生成 `docs/TESTING.md` 的 module-index 段（不手改生成段）。
- `verify-probe-mirror-parity` 同批更新：`ServiceProbe` 新派发 mode 的断言条数逐名钉死，`tests/**/Modes/*.cs` 只许一份定义；新增 `icc-profile`/`icc-lut` 后若未登记，会红。
- 新测试/新解析器不得新增 `TMP` 根；临时 ICC 优先在内存解析，确需落盘必须 `finally` 删除或落在 `tests/output/_tmp`，并核对 `_probe-host-temp-sites.ps1` 站点表。
- `verify-color-wiring.ps1` F-02 旧断言改为 fail-closed，并在同一脚本中加：
  - 合法 LUT `--icc-file` 正控；
  - 缺失/无效负控；
  - 相对路径 F-06 用例；
  - JXR 回读的 PATH 前置修复（这不是产品 bug，不能用产品代码绕过）。
- `verify-color-matrix-full.ps1` 加 H1 两档 × engine/legacy × 直通参考与日志断言。
- `verify-color-strategy.ps1` / `verify-decision-delivery.ps1` 随新语义更新计数与期望；不得为了让旧断言绿而放宽。重点：
  - `verdict=41/1` 的判据分歧在 ICC 身份变化后重新核对；
  - `decision` 185.2 s 超时先做稳定性分析，不把超时读数当产品修复基线；如确属超时表过低，按 `_module-registry.json` 的 `locks.script-timeouts.table` 只抬不降，并记录理由。
- `verify-gainmap-isobmff` 的负控前提单独裁决：如果“负控本身不成立”，改夹具/断言；不得把它算成产品缺陷。
- CJK 硬编码扫描：新增用户可见文案优先走现有 `Loc` 资源；新增日志若确实要写中文，按 `_probe-cjk-hardcode-scan.ps1` 实测更新基线并记录原因，不能无理由抬阈值。

**文档变更**：

- `docs/review-2026-11-17/COLOR_PIPELINE_FIX_PLAN_2026-11-17.md`（本文）转成执行记录时补：
  - 每个 WP 的实际提交哈希；
  - 红态证据路径；
  - 最终 DLL 指纹；
  - 逐条 closure 表。
- 新增 `COLOR_PIPELINE_FIX_RESULT_2026-11-17.md`（修复完成后写，不提前写读数）。
- `AGENTS.md §7` 只加“更正条目”，不回改带日期账本；本方案要求的 AVIF ICC 口径、beta17 重出等若与旧条目冲突，按新条目处理。

**验收命令（只跑变更链路，不跑全量 104）**：

```powershell
$env:LC_ALL = $null
dotnet build ffmpegPictureUI.sln -c Release

pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1 -Scripts `
  verify-color-icc-profiles.ps1, `
  verify-color-wiring.ps1, `
  verify-color-matrix-full.ps1, `
  verify-color-strategy.ps1, `
  verify-raw-provenance.ps1, `
  verify-dng-nonraw-input.ps1, `
  verify-gainmap-isobmff.ps1, `
  verify-gainmap-managed.ps1, `
  verify-tiff-icc.ps1, `
  verify-color-engine-xcheck.ps1

pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1 -ListUnits
pwsh -NoProfile -File tests/scripts/_release-gate-check.ps1
```

- `-FullBaseline` 仅用户明确要求时使用；本方案不得自行全量。
- 每个读数记录 `FfmpegGui.dll` 与 `ServiceProbe.dll` 前 16 指纹；不以 `.exe` 哈希判定同一产物。
- 起轮后不再改 `src/` 与运行器；红态证据另存，不用“改断言”抹掉。

---

## 5. 并行执行编排

> 按用户偏好：能并行的拆开，但共享文件的写入必须有单一 owner。

### Wave 0（串行，1 人）

- WP-00：测试夹具/oracle/服务探针模式/红态证据。
- 产出：新门禁骨架、参考 JSON、所有红态日志。

### Wave 1（三路并行）

| 路 | 负责 | 独占文件 | 依赖 |
|---|---|---|---|
| A | WP-01 ICC 身份 + WP-05 provenance 的接口部分 | `ColorSpaceIdentify.cs`、`IccProfileAnalysis.cs`、`ColorIntent.cs`、`ColorIntentFactory.cs`、`FfmpegOptions.cs`、`QueueProcessor.cs` | WP-00 |
| B | WP-04 H1 矩阵 | `FfmpegCommandBuilder*.cs`、`RawColorPipeline.cs`、`ColorEngineRouter.cs` | 接口约定：`InputMatrixDirective` 由 B 定义，A 不改 |
| C | WP-06 exiftool + F-06 门禁 | `ExifToolService.cs`、`verify-color-wiring.ps1` 的 F-06 段 | WP-00 |

### Wave 2（两路 + 集成）

| 路 | 负责 | 说明 |
|---|---|---|
| A2 | WP-02 携带/外部 ICC/冲突 + WP-05 剩余整合 | 必须在 WP-01 合并后进行；改 `ColorIntentFactory` / `ColorStrategyPlanning` / `QueueProcessor` |
| D | WP-03 CMS 慢路 | 可先在新文件实现解析/插值；接入 `ColorTransformPlan` 要等 WP-01 接口冻结；不与 A2 同时改 `ColorStrategyPlanning` |
| E | WP-07 门禁/文档 | 等 A2/B/C/D 的接口和提交稳定后统一登记；负责负控转红验证 |

### 合并顺序

1. WP-01（身份）
2. WP-05（provenance）与 WP-02（语义）按 A2 顺序提交；每步保留可回滚点
3. WP-04（H1）
4. WP-06（exiftool）
5. WP-03（CMS）
6. WP-07（门禁登记/文档/最终读数）

**冲突红线**：

- 同一文件不许两路同时编辑；若必须交叉，以“先提交者冻结接口，后提交者只加调用”的方式串行。
- 不得在 `ColorIntentFactory` 里出现第二个 provenance 判断；不得在 `FfmpegCommandBuilder`/`RawColorPipeline` 里出现第二套矩阵适用性判断；不得在 `ExifToolService` 里只修一个进程站点。

---

## 6. 验收矩阵（按 finding）

### F-01

| 用例 | 期望 |
|---|---|
| `ServiceProbe icc-profile` × 两个真实 LUT profile | `Valid/LutA2B`、SHA 对应、A2B intents 非空、MatrixDescriptor=null 不等于 Absent |
| `CarryIcc` → PNG/AVIF/JXL/WebP/TIFF | 容器可携带时原 ICC SHA 一致；不可携带时 exit 91 + `ContainerCannotCarryIcc`，不得声称源无 ICC |
| `Recommended` → PNG | 保留源 LUT ICC；不得以 sRGB `cICP` 替代；不得生成共同 sRGB ICC |
| 两个不同 LUT 源 → AVIF | 两个产物不得仅因同一 sRGB ICC 而不可区分；若携带，ICC SHA 分别等于源；若 CMS 映射，目标 ICC 可相同但像素必须过各自 oracle |
| `Manual` 目标 P3 × 两个源 | S1 exit 91 `IccLutRequiresCms`；S2 产物过 `icc-lut-reference.json` |
| GainMap SDR / HDR | SDR 可携带时保留源 ICC；HDR 映射后写基色 ICC；无 CMS 时明确拒绝，不按 sRGB |

### F-02

| 用例 | 期望 |
|---|---|
| `--icc-file` 不存在 / 目录 / 0 B / 127 B / 坏签名 | exit 91、零产物、`ExplicitIccNotFound/Unreadable/Malformed` |
| 合法 MatrixTrc 外部 ICC | 与同 profile 内嵌映射同结果；不得被忽略 |
| 合法 LutA2B 外部 ICC | `icc-profile` 识别；推荐/携带正确；显式映射 S1 拒绝、S2 oracle |
| legacy + 外部 ICC | 不存在/无效同样 91；有效但不支持映射时明确 `LegacyCannotMapLutIcc`，不得静默忽略 |
| 旧 `verify-color-wiring` 断言 | 已改为 fail-closed；负控可把旧代码打回红 |

### F-03

| 用例 | 期望 |
|---|---|
| LUT ICC + P3 CICP（PNG/AVIF） | `COLOR_ICC_CICP_CONFLICT` 警告；输出不写矛盾 CICP；推荐路径保留 ICC |
| MatrixTrc ICC + 反向 CICP | 同一冲突策略；ICC 优先 |
| ICC 与 CICP 可证明等价 | 无冲突警告，可 ICC+CICP 双写 |
| `BakeCicpOnly` + 不可评估 LUT | S1 拒绝，S2 评估后再按用户选择 |

### F-04

| 用例 | 期望 |
|---|---|
| 直通 ffmpeg `bt470bg` vs `bt709` | engine/legacy 各自两档产物不同；与直通参考一致 |
| H1 Map 目标 P3，两档矩阵 | engine 不再 4472 B 同 SHA；legacy 两档也不同 |
| H1 日志 | 出现 `scale=in_color_matrix=bt470bg`/`bt709`；不再出现“矩阵无载体” |
| RGB 源 + 显式矩阵 | 警告，不插滤镜，产物可读 |
| grouped/未知 + 显式 engine | 91、零产物、原因码；auto 回退 legacy 正常 |

### F-05

| 用例 | 期望 |
|---|---|
| `ServiceProbe plan` provenance 三组 | 自产 `RawIntermediate` 得到 `bt2020/linear`；仅声明无 provenance 不提升 |
| 真实 DNG 队列 → P3 PNG / sRGB JPEG / GIF | 日志不写“按 sRGB 惯例”；像素过独立 raw oracle |
| DNG 默认 vs 无标签 sRGB | 不再同像素 SHA |
| `DeepClone()` | provenance 保留；`PresetData` 不含 provenance |
| RAW + GainMap / 目标矩阵 | 至少结构/日志正控；无 oracle 的组合不宣称像素通过 |

### F-06

| 用例 | 期望 |
|---|---|
| 绝对路径合并探测 | 4 标签 + ICC base64 |
| 相对路径合并探测 | 同结果，不再 `File not found` |
| 相对源 + 相对输出目录 CLI | `DateTimeOriginal`/`ICC_Profile` 与绝对组一致 |
| 空格/CJK 路径 | 同上 |
| `wire` | 新断言数下 PASS；错误码/日志不含 `File ... does not exist` |
| exiftool 缺失/破损 | 现有警告与色彩保护不回归 |

---

## 7. 风险、回滚与发布门

### 7.1 主要风险

| 风险 | 控制 |
|---|---|
| 把 LUT 解析成矩阵近似，产物“看起来对”但误差被隐藏 | 硬分派：`Lut*` 不得走 `ColorKernels` 矩阵快路；oracle 逐 patch，不只看平均/PSNR |
| ICC 临时件在识别后被删，携带路径悬空 | `SourceIccAnalysis` 不依赖 `TryDeleteIcc` 的临时路径；执行侧从 `SourceInputPath`/外部稳定路径重建 |
| CICP 与 ICC 双写但值矛盾 | 中央 conflict policy；不可证明等价时抑制 CICP，宁可 ICC-only |
| H1 矩阵插入顺序错误，scale 作用于已拉伸帧 | 滤镜链顺序固定为 matrix/range scale → format → geometry；独立直通 ffmpeg 参考 |
| provenance 被预设/JSON 固化 | `ColorSourceProvenance` 不进 `PresetData`；DeepClone 单测；无 provenance 负控 |
| exiftool 绝对化只修一个入口 | 公共 `AbsArg` + 全站点清单；`wire` + 相对 CLI 双用例 |
| 新增门禁只加脚本不改登记，变成假绿 | WP-07 四处登记 + module registry + `_probe-test-group-coverage`；负控必须能打回红 |
| 性能回归 | LUT 慢路仅对 `Lut*` 源启用；矩阵/无 ICC 路径不加额外探测；`icc-profile` 缓存；4K bench 对照 |

### 7.2 回滚粒度

- WP-01：独立可回滚，但回滚后 F-01 恢复到旧缺陷；只能作为开发中间态，不能作为发布态。
- WP-02：独立可回滚；必须保留 WP-01 的身份层。回滚后显式 ICC 又变成静默，禁止发布。
- WP-03：独立可回滚到 S1（携带/拒绝），不会自动回到 sRGB；这是允许的中间态。
- WP-04：独立可回滚，回滚即 F-04 重新打开；H1 的 auto 回退不得依赖 CMS。
- WP-05：独立可回滚，回滚后 provenance 不能靠 `IsRawFile(temp)` 补；禁止发布。
- WP-06：独立可回滚；回滚后 wire 会红，必须在发布前恢复。

### 7.3 发布门

- 版本位与 tag：保持 `1.6.0-beta17` / `1.6.0BETA17 → 957cfa8`，本方案不移动 tag。
- 用户已定“不转 beta18”：修复完成后的重出（同版本）需要按 `AGENTS.md §8.1` 单独豁免；未获批准前不打包。
- 出包前必须：
  1. 变更链路门禁全绿；
  2. 新门禁负控能把旧代码打红；
  3. `ServiceProbe engine` / `color-wiring` / `decision` / `verdict` / `gainmap-isobmff` 的读数稳定且有归类；
  4. `_release-gate-check.ps1` exit 0；
  5. 绑定 `FfmpegGui.dll` / `ServiceProbe.dll` 指纹。
- 不宣布“全绿”除非有最终树上的完整选中链路日志与指纹；不把 JXR PATH 假红、exiftool 126、旧断言过期算成产品失败。

---

## 8. 需要拍板/留档的事项

以下不是阻塞实现的技术空白，而是产品口径，建议按推荐值执行并在修复结果文档留档：

1. **CMS 实现选择**：推荐纯托管 `IccLutTransform`，不引入 lcms2 原生 DLL。理由：保持 AOT/打包与现有纯托管架构；Pillow `ImageCms` 仅作独立 oracle，不进产品依赖。
2. **S1 阶段语义**：合法 LUT ICC 默认“携带或拒绝”，不近似矩阵化、不静默 sRGB。必须明确这是**正确性收口**，不是最终功能；S2 才提供完整映射。
3. **CICP 冲突默认**：ICC 优先；不可证明等价时抑制 CICP 并警告。`BakeCicpOnly` 也不得在无法评估 ICC 时强行用 CICP。
4. **LUT 渲染意图**：首版用 profile header，BPC off，不叠加产品 GMO；UI 暂不暴露意图选择。若产品需要“感知/相对色度”选项，另开 UI + 计划工作包。
5. **H1 显式 engine 失败码**：沿用既有契约拒绝 exit 91；auto 回退 legacy 的决策必须写日志，不伪造 engine 成功。
6. **F-05 provenance 位置**：`FfmpegOptions` 运行时字段 + `QueueItem.SourceInputPath` 保留；不改 `FromOptions` 签名，避免大面积漂移。
7. **F-06 范围**：修 `ExifToolService` 所有文件参数；不只修 `ReadColorTagsOnceAsync`。其他外部工具（ffmpeg/dngtool/jxlinfo）若也有同类相对路径问题，另立审计，不混入本 finding。

---

## 9. 执行顺序（最短可验收路径）

1. **WP-00**：固定基线、生成 oracle、写红态证据。
2. **WP-01**：ICC 身份层（先不改像素）。
3. **WP-02**：外部 ICC fail-closed、携带/映射策略、CICP 冲突；此时 F-01 的“错误识别”消掉，F-02/F-03 关闭，但 LUT 映射仍明确拒绝。
4. **WP-04**：H1 矩阵，独立于 ICC 线，可并行完成。
5. **WP-05**：RAW provenance，接入 WP-01 的来源对象。
6. **WP-06**：exiftool 路径，独立完成并将 wire 转绿。
7. **WP-03**：CMS 慢路，完成 F-01 的完整映射能力。
8. **WP-07**：门禁登记、旧断言改写、独立 oracle 对撞、最终读数与修复报告。

允许并行：WP-04、WP-06 可在 WP-01/WP-02 期间并行；WP-03 可先做新文件，接口冻结后接入。禁止并行：`ColorIntentFactory`、`ColorStrategyPlanning`、`QueueProcessor` 的多路同时写；`ExifToolService` 只允许一个 owner。

---

## 10. 附：本方案不接受的“省事修法”

以下做法会让读数变绿但保留错误语义，明确禁止：

- 只把 `ColorSpaceIdentify.FromIccFile` 的返回 null 改为返回一个空描述符，让 `SourceIccPath` 有值：会把 LUT 伪装成矩阵空间，后续映射仍错。
- 把 `IsMappable` 改成只看 ICC 是否存在：LUT 仍无法用 3×3 矩阵正确映射。
- 遇到 LUT 就统一生成 sRGB ICC：F-01 的 AVIF 同 ICC 现象就是这种错误。
- 外部 `--icc-file` 缺失仍回落惯例：F-02 的 1393 B 同 SHA 产物会再次出现。
- H1 只把 `-colorspace` 写到 `-i` 前：输出标签变了、像素没变，F-04 仍存在。
- RAW 用 `inputPath` 的扩展名判断：临时件 `*_raw.tiff` 会再次被当成普通无标签 TIFF。
- exiftool 只把 `WorkingDirectory` 改回 CWD：会重新引入 `perl532.dll code 126` 的环境问题，且没有修参数路径；必须绝对化参数。
- 门禁只加脚本、不改 `$scriptList`/`$expectedScriptCount`/散文/`TestGroups`：新增条目不进入 runner，等于没有门禁。
- 用产品自身输出当“oracle”：LUT CMS 必须用独立 Pillow/ImageCms 或静态参考，产品自证不算。

---

**方案结束**
