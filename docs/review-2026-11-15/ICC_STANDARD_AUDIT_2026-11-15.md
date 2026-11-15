# ICC 标准性、体积与可用性审计（2026-11-15）

> 范围：当前工作树（`C:\PLAN\ffmpegPictureUI`，未提交）中由 `IccProfileBuilder` /
> `IccProfileService` 生成的**标准 ICC**、源 ICC 的 v4.4 重写结果，以及各出口容器的
> ICC / CICP 可用性。
>
> 方法：重新跑产品 CLI（`--color-strategy carry --color-carry-scope all`），对所有产物用
> `pngcicp info`、`exiftool`、`jxlinfo`、`ffprobe` 独立回读；ICC 结构用**字节级**解析
> （大端 tag directory）复核，不引用旧结论。测试环境固定为
> `TMP/TEMP/TMPDIR=tests\output\_tmp\colormatrix-analysis\round-tmp`，
> `FFMPEGGUI_PLAN_DIR=publish\PLAN`。

---

## 1. 结论（先给结果）

1. **标准 ICC 结构合规**：v4.4.0、Display Device、RGB / XYZ PCS、D50、`acsp`、
   `desc`/`cprt` 为 v4 `mluc`、`chad`、`chrm`、`wtpt`、三通道 XYZ/TRC、`cicp` 全部在位；
   12 个 tag，目录/偏移 4 字节对齐。
2. **体积已接近“可读性优先”的最小集合，不是冗余严重**：Display P3 616 B、sRGB 612 B、
   BT.2020 SDR 616 B、ProPhoto 544 B（无 `chrm`/无 `cicp`）。
   `icc_simplify` 可将 P3 压到 516 B，但它删/改了可选 tag；为了 `chrm` 的独立原色读数与
   完整 `desc`/`cprt`，保留 616 B 是合理取舍。可再省约 24 B（缩短 `cprt`）或 36 B（删
   `chrm`），不建议为几十字节牺牲兼容性。
3. **`ProfileID=0`、`ProfileCMMType` 为空不是非标准**：ICC.1:2022 允许“未计算 Profile ID”
   置零，也允许 preferred CMM 为空；多数验证器只告警。若要求“严格可校验”，可后续计算
   Profile ID（MD5，flags/ID 置零后写入 84–99 字节），但这会改变所有缓存与产物字节，
   本轮未做。
4. **ICC 内 `cicp` 已落地且合规**：signature/type 均为 `'cicp'`，12 B；
   `[8]=ColourPrimaries`、`[9]=TransferCharacteristics`、`[10]=MatrixCoefficients`、
   `[11]=VideoFullRangeFlag`。P3 实测 `63 69 63 70 00 00 00 00 0C 0D 00 01`，
   BT.2020 SDR 为 `09 01 00 01`，sRGB 为 `01 0D 00 01`；RGB/XYZ 的 MatrixCoefficients=0。
5. **可用性按容器分层（2026-11-15 最新口径）**：PNG/APNG **CICP 优先**——CICP 能精确表达时
   只写 `cICP`，不再写 `iCCP`；只有源空间无法用 CICP 命名（如 ProPhoto）才回退 ICC-only。
   JPEG/JPEGLI/WebP/TIFF 容器无 cICP 通路，由“标准 ICC + ICC 内 `cicp`”承担；
   JXL 的 ICC/CICP 互斥，CICP 能表达时 CICP-only，不能表达时 ICC-only（ProPhoto JXL 已实测 544 B ICC）；
   静态 AVIF 的 ffmpeg 出口不写 ICC，只走 CICP，ProPhoto 类不可命名源按契约拒绝（exit 91）。
6. **源 ICC 一律重写 v4.4**：P3 源正常化缓存 616 B/4.4.0，带 `cicp`；ProPhoto 源正常化
   缓存 544 B/4.4.0，无可命名 CICP，故不写 `cicp`。标准空间缓存与源正常化缓存均以
   `v44cicp` 命名区分。

---

## 2. 标准 ICC 的字节级读数

样本：`tests\output\_tmp\colormatrix-analysis\round-tmp\stdbuilt_smpte432_iec61966-2-1.v44cicp.icc`
（Display P3 + sRGB TRC）。

### 2.1 头与 tag 目录

| 项 | 实测 |
|---|---|
| Profile size | 616 B |
| Version | 4.4.0（头字节 `04 40 00 00`） |
| Class | Display Device (`mntr`) |
| Color space | RGB |
| PCS | XYZ |
| File signature | `acsp` |
| PCS illuminant | `0.9642 1 0.82491`（D50） |
| Rendering intent | Media-Relative Colorimetric |
| ProfileCreator | `FFGU` |
| ProfileID | 0（未计算；ICC 允许） |
| Tag count | 12 |

tag 目录（大端解析；数据偏移/长度）：

| tag | 长度 | 说明 |
|---|---:|---|
| `rXYZ` / `gXYZ` / `bXYZ` | 20 / 20 / 20 | D50 适配后的原色矩阵 |
| `wtpt` | 20 | D50 白点 |
| `rTRC` / `gTRC` / `bTRC` | 32（三通道共享同一数据块） | `para` type 3，sRGB 曲线 |
| `chad` | 44 | D65→D50 色适应矩阵（s15Fixed16 3×3） |
| `chrm` | 36 | 3 通道色度（P3：0.67999/0.32001、0.265/0.69、0.14999/0.06） |
| `cicp` | 12 | 见 2.2 |
| `desc` | 56 | v4 `mluc`，`smpte432/sRGB` |
| `cprt` | 78 | v4 `mluc`，`No copyright, use freely` |

目录 148 B + 头 128 B + tag 数据 338 B + 对齐 2 B = 616 B。
tag 数据偏移均为 4 字节对齐，无越界/重叠；三通道 TRC 共享数据块是 ICC 允许的写法。

### 2.2 ICC 内部 `cicp`（P3 样本）

12 字节原文：

```
63 69 63 70  00 00 00 00  0C 0D 00 01
'c' 'i' 'c' 'p'  reserved   P  T  M  F
```

- `P=0x0C=12`：SMPTE EG 432-1（Display P3 primaries）
- `T=0x0D=13`：sRGB / sYCC transfer
- `M=0x00=0`：Identity matrix（RGB/XYZ 必须 0）
- `F=0x01=1`：Full range

其它标准空间：BT.2020 SDR 为 `09 01 00 01`（BT.2020 primaries、BT.709 transfer）；
sRGB 为 `01 0D 00 01`。`cicp` tag 仅用于 Input/Display class；本生成器只产 Display，
不会把它写进设备链接/输出 class。

### 2.3 体积：616 B 是否“最优”

| 方案 | 估算体积 | 取舍 |
|---|---:|---|
| 当前 12 tag（含 `chrm`/`cicp`/完整 `desc`/`cprt`） | 616 B | 兼容性与可读性最好 |
| 删 `chrm` | 580 B（约 −36 B） | exiftool/校色工具失去独立原色读数，不建议 |
| 缩短 `cprt`（如 `No copyright`） | 592 B（约 −24 B） | 需 bump 缓存 revision；收益很小 |
| `icc_simplify` 实测 | 516 B | 会删/改可选 tag；只适合“最小传输”，不适合作为标准交付 |
| 完整 `ProfileID` | 不增加体积（头内字段） | 会改变所有缓存/产物字节，需单独决定 |

结论：616 B 已是“保留 `chrm` + 完整 v4 `desc`/`cprt` + `cicp`”的紧凑形态；每张图多几十字节
没有实际收益，默认保留。若后续有批量小图标/极低体积场景，可把 `cprt` 缩短作为独立优化项，
不要动 `chrm`。

### 2.4 可选字段的合规性

- **ProfileID=0**：ICC.1:2022 明确定义“未计算时置零”；不是格式错误。部分验证器把它当
  warning“Profile ID not set”，可接受。
- **ProfileCMMType 为空**：表示没有 preferred CMM；同样是合法可选字段，不是缺 tag。
- **`ProfileDateTime=2026:01:01`**：固定值用于确定性缓存；不影响合规。
- **`chad`**：D50 PCS + D65 原色必须携带；本 profile 有 44 B `chad`。
- **`desc`/`cprt` 类型**：v4 必须是 `mluc`；实测是 `mluc`，不是 v2 的 `desc`/`text`。

因此：**结构标准、可被 exiftool/lcms 解析；“严格校验”层面只剩 ProfileID 可选告警**。

---

## 3. 源 ICC 的 v4.4 重写

| 源 | 正常化缓存 | 体积 | 版本 | `cicp` | 结论 |
|---|---|---:|---|---|---|
| Display P3 | `srcv44_v3_cicp_4b48ac02b4865002.icc` | 616 B | 4.4.0 | `12/13/0/1` | 等价 v4.4 + CICP |
| ProPhoto | `srcv44_v3_cicp_20c9b0e84cb58e80.icc` | 544 B | 4.4.0 | 无 | 空间超出 BT.2020，不伪造 CICP |
| Custom WideB | `srcv44_v3_cicp_20c9...`（同族未知空间） | 544 B | 4.4.0 | 无 | 保留源矩阵，不写假标签 |

- 重写不是只改版本头：`IccProfileService.NormalizeSourceIccToV44` 用实测 `rXYZ/gXYZ/bXYZ`、
  白点、`chad`、共享 TRC 重建；LUT/设备链接/三通道 TRC 不一致时 fail-closed。
- 干净 v4.4 且已含 `cicp` 的源可复用；干净 v4.4 但缺 `cicp` 的源会重建，避免“版本是 4.4、
  内部没有 CICP”的半套状态。
- `PLAN/iccs` 的命名 ICC（如 `named_prophoto-rgb.v3.v44.icc`，544 B）实测为 4.4.0，
  Display/RGB/XYZ；ProPhoto 无 `cicp`，符合“无法命名不伪造”。

---

## 4. 容器可用性矩阵（本轮实测）

命令模板：

```powershell
FfmpegGui.exe --headless -i <src> --format <fmt> --output <dir> `
  --log-level Debug --color-strategy carry --color-carry-scope all
```

| 源 → 出口 | 结果 | 产物描述 | 备注 |
|---|---|---|---|
| CICP-only P3 → JPEG | exit 0，2511 B | ICC 4.4.0 + `[ICC-cicp]=12/13/0/1` | 容器无 cICP，ICC 内 `cicp` 承担；不再 fail-closed |
| CICP-only P3 → WebP | exit 0，1910 B | ICC 4.4.0 + `[ICC-cicp]=12/13/0/1` | 后置嵌入；`ICC-only` 路径 |
| CICP-only P3 → TIFF | exit 0，12900 B | ICC 已嵌入（616 B 标准 P3） | exiftool 回读偶发 `perl532.dll code 126`，但产物头/tag 已核 |
| ICC P3 + cICP 9/16 冲突 → PNG | exit 0，1665 B | cICP 12/13，**无 iCCP** | CICP 优先：统一到可精确表达的 P3 标准空间，不再双写 |
| ICC P3 → PNG | exit 0，1665 B | **cICP 12/13，无 iCCP** | 最新口径：CICP 可表达 ⇒ ICC-only 不必要 |
| ICC P3 → APNG | exit 0，2265 B | cICP 12/13，无 iCCP | 与 PNG 同策略（APNG 复用 cICP） |
| Adobe ICC → PNG | exit 0，2762 B | cICP 9/1（BT.2020/SDR），无 iCCP | 标准超集 BT.2020，CICP 可命名 ⇒ CICP-only |
| ICC P3 → JPEG | exit 0 | 标准 ICC 4.4.0 + `cicp` | ICC-only 容器 |
| ICC P3 → JXL | exit 0，1832 B | `jxlinfo` P3/sRGB，无 ICC | 互斥容器，CICP 可表达 ⇒ CICP-only |
| ICC P3 → AVIF | exit 0，1797 B | `ffprobe` smpte432/iec61966-2-1，无 ICC | 静态 AVIF 出口只写 CICP |
| CICP-only P3 → JXL | exit 0，1832 B | P3/sRGB | CICP-only |
| CICP-only P3 → AVIF | exit 0，1797 B | smpte432/iec61966-2-1 | CICP-only |
| Adobe ICC → JPEG | exit 0，1852 B | ICC 4.4.0，desc bt2020/BT.709，`cicp=9/1/0/1` | 标准超集 BT.2020，矩阵映射 |
| Adobe ICC → JXL | exit 0，1807 B | `jxlinfo` Rec.2100 / 709，无 ICC | CICP 可命名 ⇒ CICP-only |
| ProPhoto ICC → PNG | exit 0，1967 B | iCCP 306 B，无 cICP，ICC 4.4.0 | 无法命名，保留源 ICC，抑制 CICP |
| ProPhoto ICC → JXL | exit 0，2465 B | 544 B ICC，无 CICP | CICP 不可表达 ⇒ ICC-only（cjxl） |
| ProPhoto ICC → AVIF | exit 1（91），无产物 | — | 静态 AVIF 不写 ICC 且 ProPhoto 不可命名 ⇒ 契约拒绝 |

说明：

- JPEG/TIFF/WebP 的“CICP”不是容器级 CICP，而是 **ICC 内部 `cicp` tag**；这解决“容器表达不了
  CICP”时仍有机器可读语义。
- JXL 的 ICC 与 CICP **互斥**；当前策略按“CICP 能表达则 CICP-only，否则 ICC-only”落，
  不再对显式 `CarryIcc` 一刀切 fail-closed。
- AVIF 的 `FormatColorCaps.CanHaveBothIccAndCicp=true` 描述的是 **avifenc**；静态 ffmpeg 出口
  实际不写 ICC，`ExitWritesIccFor("avif")=false` 是当前产品真值。
- exiftool 的 `perl532.dll code 126` 是随包 Perl 运行时抖动，不是容器拒绝；本审计对 TIFF 等
  采用“重试 + 字节级解析”双路径，未把空回读当成“无 ICC”。

---

## 5. CICP / ICC 统一规则（代码落点）

1. `PlanCarry` 对源 ICC：
   - 先求“能覆盖源色域的最窄标准空间”（PNG/APNG/JXL 等有 CICP 通路用 `NameableSupersetFor`，
     JPEG/WebP/TIFF 无 CICP 通路用 `NarrowestStandardGamutFor`）。
   - 标准空间与源等价 ⇒ 像素零改动；更宽 ⇒ 矩阵映射（`Action=Map`），损失登记
     `RoundTripQuantization`。
   - 出口不写 ICC（静态 AVIF）时：标准空间可 CICP 命名 ⇒ CICP-only；不可命名 ⇒ 拒绝。
   - 出口写 ICC 时：标准空间成功 / 等价令牌 ⇒ 标准 ICC + 同源 CICP；无可命名令牌 ⇒ 源 ICC，
     **抑制 CICP**。
2. `CarryCicpLabels` 与 `EnforceContainerAnnotationRules`：
   - 计划承诺 ICC 时，容器 CICP 必须与 `IccPrimaries/IccTrc` 完全一致；冲突则以 ICC 为准同步。
   - 原始源 ICC（`IccPath != null` 且无令牌）优先保留 ICC、抑制 CICP。
   - 互斥容器（JXL）显式 `CarryIcc` 不再返回 exit 91：`CICP` 写得下且 ICC 是可命名标准令牌
     ⇒ CICP-only；否则 ICC-only。
   - **PNG/APNG（2026-11-15 最新）**：CICP 优先。CICP 能精确表达且容器可写时，即使策略是
     显式 `CarryIcc` 或 `Recommended` 已置 `AttachIcc=true`，也剥离 ICC / 不挂 `iccgen`；
     只有源空间无法用 CICP 命名（如 ProPhoto）才 ICC-only。JPEG/WebP/TIFF 的 `CanCicp=false`
     时按 ICC-only（ICC 内 `cicp` 兜底）。
3. 结果：**同一产物里不会出现“ICC 说 A、容器 cICP 说 B”**；容器没有 cICP 时，ICC 内 `cicp`
   仍描述同一空间；CICP 无法表达时才由 ICC 独担。

---

## 6. 未完成 / 边界（必须如实登记）

1. **`--icc-file` 外部 ICC 尚未纳入“v4.4 + 内嵌 cicp”统一重写**：当前它只作为“源空间判定”
   的描述符，不设 `SourceIccPath`；`--icc-file` + `--color-strategy carry` 到 PNG 实测仍
   Reject（源无 ICC 可携带）。这是既有设计（避免“计划说携带外部 ICC、产物没写”），
   但不在本轮统一口径内；要纳入需单独设计“外部 ICC 作为可携带源”并补回归。
2. **`PLAN/iccs` 命名 ICC** 已验证为 v4.4，但未逐一覆盖所有命名空间；本轮只核了 ProPhoto/P3。
3. **静态 AVIF 仍不能写 ICC**：如需真正 ICC-only AVIF，需要 avifenc 两步法；本轮未接。
4. **ProfileID=0**：合规但部分验证器告警；若要求“完全严格”，需计算 MD5 并 bump 缓存 revision。
5. **exiftool **：`perl532.dll code 126` 的根因已定位为 exiftool 启动器按**当前工作目录**加载 DLL；
   产品所有 `ProcessStartInfo` 已把 `WorkingDirectory` 钉到 exiftool 目录，并对源读取的 `code 126`
   增加第三次尝试。矩阵最终轮不再有读取失败残留；TIFF ICC 回读仍应用字节级解析复核。
6. **门禁（最终构建重跑）**：`verify-prophoto-faithful` **38/0**——修了测试侧 `Run` 未回写 `LASTEXITCODE`
   与 exiftool 夹具工作目录后，ProPhoto recommended/carry→AVIF 均按新契约拒绝。
   `verify-color-wiring` 官方 runner **159/3**——两条为 `(11)` alpha 的 exiftool PixelFormat 读空，一条为
   `(11)` 中转目录残留（跑前 8 / 跑后 14）；不再有 16/32-bit 读空与 `(13)` 源 ICC 点名失败。
   `verify-decision-delivery` **87/7**——`⑭`/`⑯` 仍是 exiftool 读空，`⑰b legacy` 4 条是 16-bit
   AVIF 在 legacy 引擎下的既有基线，`⑱-4` 形状锁。`verify-color-strategy` 本轮未重跑，
   旧读数 70/8 不可当最终验收（其中 C1–C5 是旧 PNG 双写/旧 carry 契约期望）。

> **2026-11-15 二次收口更正（提交 `e2b9d2e`）**：contract 探针在按 CICP-first/标准空间改写
> C1/C2/C5 后为 **161/0**；`verify-color-wiring` 最新官方 runner 为 **98/37**，同轮 contract 161/0，
> 失败集中在缺 P3 广色域素材的 SKIP 与 geometry/thumbnail/bench 环境探针，不能当色彩回归，
> 上段 159/3 已过期。`tests\output` 已在收尾清理中清空，下文证据路径不再在盘上；本节读数保留为记录。


---

## 7. 证据路径

- 标准/源缓存：`tests\output\_tmp\colormatrix-analysis\round-tmp\`
  - `stdbuilt_smpte432_iec61966-2-1.v44cicp.icc`（616 B）
  - `stdbuilt_bt2020_bt709.v44cicp.icc`（616 B）
  - `stdbuilt_bt709_iec61966-2-1.v44cicp.icc`（612 B）
  - `srcv44_v3_cicp_4b48ac02b4865002.icc`（616 B，P3）
  - `srcv44_v3_cicp_20c9b0e84cb58e80.icc`（544 B，ProPhoto/未知）
- 本轮产物：`tests\output\_tmp\colormatrix-analysis\unify1..unify9\`；
  最终矩阵 `tests\output\_tmp\colormatrix-analysis\matrix1115e\`
- 结构复核：`icc_simplify` 输出 `tests\output\_tmp\colormatrix-analysis\icc_simplify_p3.icc`（516 B）
- 门禁日志：`tests\output\_tmp\colormatrix-analysis\gate_fix13_summary.txt`（最终构建重跑：
  wiring/prophoto/decision 的关键读数；原始长日志因磁盘压力已截断）
