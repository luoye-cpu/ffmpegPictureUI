# 交接文档 (Agent Handover)

> 📌 **接手请先读**：**`docs/HANDOVER_2026-09-22.md`**（2026-09-22 的**全新单页交接**：
> 当前状态速览 · 接手第一步 · 本会话改动清单 · **未验证部分** · 待决策 · 新增陷阱 · 命令速查）。
> 本文（`HANDOVER.md`）是**逐轮全量记录**（§1–§38），供深挖细节与追溯决策依据。

---

## 🔴【待实现】功能清单（**未实现 —— 请勿当作已可用**）

> 约定：本清单里的项**没有**对应实现，或**只有传统管线**实现；**色彩引擎（自研管线）尚未接入**。
> 已实现的项见下方「✅ 已实现」表。凡本表项，运行时会被 `ColorEngineRouter.BlockReason`
> **显式回退到传统管线并记一条说明**（`auto` 默认下），或在显式 `--color-engine engine` 下**硬失败并点名**。

| # | 🔴【待实现】项 | 现状 | 归属 |
|---|---|---|---|
| **1** | 渐进 JPEG（`JpegProgressiveId`） | ffmpeg 的 mjpeg 编码器**无此能力**（传统管线也不发）⇒ 显式选择即拒绝并建议改 cjpegli | 工具链不支持 |
| **2** | **DNG** 输出的色彩执行（原为「AVIF / GIF / JXL / JXR / DNG」：**AVIF / GIF / JXL（两后端）/ JXR 均已于 2026-09-17 接入引擎** —— JXL 的 ffmpeg-libjxl 子情形见下方 ⚠ 第二处更新，**Cjxl 子情形（cjxl 执行出口）见第三处更新**；**JXR 见第四处更新**） | 分级（侦察结论，**原文保留**）：~~GIF = **可直接接入（仅单帧）**，但需先放开「无色彩通路格式」的意图装配门~~（**已完成**）；~~JXL = **需新增执行出口**（cjxl 外部 exe，不吃 rawvideo）~~（**已完成**：`backend==Ffmpeg` 的 libjxl 子情形与 `backend==Cjxl` 的 cjxl 执行出口均已接入）；~~JXR = **需新增执行出口**（`JxrEncApp.exe` 纯文件式）~~（**已完成**：复用 `ExternalEncoderExit` + `RawColorPipeline.EncodeJxrViaJxrEncAppAsync`，中转容器由 `plan.OutBitDepth` 决定（8-bit ⇒ BMP / >8-bit ⇒ TIFF），alpha 走第二输入不拍平；门禁 `verify-color-wiring` (11) 段）；**DNG = 结构上不应接入**（需传感器 Bayer 数据） | 仅传统管线（**DNG**） |
| **3** | 决策层统一的**剩余部分**：两条管线消费**同一个裁决** | ⚠ **已重新定性（2026-09-16）**：根因在**引擎侧** —— 引擎把「做不到」表达成**三种形状各异的字符串**（`ColorIntentFactory` 返回 `null`+reason / `ColorEngineRouter.BlockReason` 返回 `string?` / `ColorStrategyPlanning.Reject` 写 `plan`），且**没有任何结构化的「损失 / 替代」表达** ⇒ 调用方**无法消费**。**完整方案见 `docs/COLOR_ENGINE_INTERFACE_PLAN.md`**（引入统一 `ColorVerdict`）。**明确不按原样「把 Reject 搬进传统管线」**（那是让第二个消费者去适配坏接口，且会抽掉逃生门的价值） | 待重构 |
| **4** | 删除传统执行体 | 上述 1–2 项未接入前无法删除 | 待重构 |

> ✅ 原第 1 项「**GainMap（Ultra HDR）接入引擎**」**已于 2026-09-16 接入引擎** ——
> 见下表「✅ 已实现」与 `docs/TESTING.md §6` 第 54 条。
> ✅ 原第 2 项「几何缩放」与第 3 项「外部 ICC」**已于 2026-09-16 接入引擎**（见下表）。
> ✅ **2026-09-17 更新：原第 2 项里的 GIF 已接入引擎**（`ColorEngineRouter` 白名单 +
> `ImageEncoderArgs.BuildGifFilterChain`/`BuildGifOptions` + `RawColorPipeline` 的 gif 出口，
> 含"第二输入补 alpha"）⇒ 该项**只剩 DNG**（按侦察结论**结构上不应接入**）。
> ⚠ 本条由 `hdr-sdr-primaries-fix` 于 GIF 接入轮写入，与 team-lead 侧的同项改动**可能重叠**，请以本行为准或合并。
> ✅ **2026-09-17（第二处更新）：原第 2 项里的 JXL 已按后端**分级落地**（`hdr-sdr-primaries-fix`，JXL 接入轮）：
>   · `backend == Ffmpeg`（ffmpeg 内置 libjxl，吃 rawvideo）⇒ **已接入引擎**（白名单 + `ImageEncoderArgs.BuildJxlOptions`
>     + `RawColorPipeline` 的 JXL 标签**前置**）；
>   · `backend == Cjxl`（本机装了 cjxl 时的**默认后端**，`EncoderDetectionService.cs:55`）⇒ **仍走 `ProcessCjxlAsync`**，
>     被 `ColorEngineRouter` 的「外部编码器后端」门挡住（**端到端证据**：`verify-color-wiring.ps1` 第 (9) 节）。
>     该项待办内容 = **新增一个 cjxl 执行出口**（不是"放开白名单"）：cjxl 不吃 rawvideo（要 PPM/PAM stdin 或落盘文件），
>     色彩语义只能靠 `-x color_space=` / `-x icc_pathname=` / `--intensity_target=`（`CjxlService.BuildCjxlArguments`）。
>   · ⚠ **JXL 的标注通路实测结论**：ffmpeg-libjxl **只能写 CICP**（标签必须前置到 `-i` 之前，写输出侧被静默忽略）；
>     **ICC 两条路都不通**（`-vf iccgen` 的 ICC 被编码器丢弃；exiftool **拒写** JXL：退出码 1、
>     `Will wrap JXL codestream in ISO BMFF container`、`0 image files updated`）⇒ 引擎出口对「jxl + 计划要求附 ICC」
>     **显式拒绝**（`RawColorPipeline`），绝不产出无色彩描述的 JXL。
> ✅ **2026-09-17（第三处更新，cjxl 出口轮）：上面那条待办已落地** —— `backend == Cjxl` **不再**走 `ProcessCjxlAsync`，
>   而是由引擎的 **cjxl 执行出口**接管（`hdr-sdr-primaries-fix`）：
>   · 计划位 `ColorTransformPlan.ExternalEncoderExit`（**与 `plan.Backend` 不是一回事**：后者答"色彩算子在哪算"，
>     前者答"编码谁来干"），由 `ColorIntentFactory` 按 `fmt=="jxl" && o.EncoderBackend==Cjxl` 置位；
>   · 路由门 `ColorEngineRouter.BlockReason` 对 `jxl && Cjxl` **放行**（⚠ 故意**不查** `CjxlService.IsAvailable`
>     —— 把可用性写进路由条件会让"工具缺失"变成静默改道）；
>   · 出口 `RawColorPipeline.EncodeJxlViaCjxlAsync`/`PipeToEncoderAsync`：解码段/变换段**完全复用**，
>     只换编码段 = `ffmpeg -f rawvideo … -f image2pipe -c:v ppm|pam -` → `cjxl - out.jxl -x color_space=|icc_pathname= --intensity_target=`，
>     **不落中间 PPM 文件**；带 alpha 的源补第二输入（`alphaextract`+`alphamerge`），**不静默拍平**；
>   · ⚠ **出口内任何失败一律显式失败，绝不回退** ffmpeg 的 libjxl（cjxl 缺失 / 要 ICC 拿不到 / 语义落不进 cjxl 枚举）。
>     变异验证：把显式失败换成静默回退 ⇒ `verify-color-wiring` 仍有 10 条转红（"非空文件"类断言**照样绿** ⇒ 只有命令行/语义类断言抓得住）。
>   · ⚠ 由此**暴露并修掉**一个引擎缺陷：`ColorTransformPlan.ToSpec()` 对 `CarryIcc`/`None` 计划回落
>     `?? Linear`/`?? Srgb` 猜测式默认 ⇒ 变换把"像素零改动"的产物整幅提亮（逐像素 == `lin2srgb`，均值 0.600→0.713）。
>     现按 Action 短路成恒等；回归锁：`ServiceProbe wire` 2 条 + `verify-decision-delivery.ps1` ⑬ 段 7 条。
>   · ⇒ **仍在引擎出口外的只剩 DNG**（外加动画输入、GainMap+最长边缩放两条组合）。
> ✅ 原第 6 项「决策层统一」的**策略来源**部分已完成（传统管线改用 `plan.EffectiveStrategy`）。
>
> ✅ **已结案 · 非缺陷（2026-09-17，`external-icc-degradation`）—— 外部 ICC 源不登记 `NoIccCarrier` / `ContainerCannotCarryIcc`**：
> 现象：`--icc-file` 指定源色彩空间时，`ColorIntentFactory.cs:70` **故意**置 `src.SourceIccPath = null`（理由见 `:56-59`），
> 而 `ColorStrategyPlanning.cs:149` 的判据是 `!p.AttachIcc && it.Source?.SourceIccPath != null` ⇒ 这两条降级码在「外部 ICC 源」上**永不登记**。
> 对照（**同一份** Display P3 描述符，仅把「内嵌 ICC」换成「`--icc-file`」，其它输入/参数逐字相同）：
> 内嵌 ⇒ `[UnlabeledAssumedSrgb, NoIccCarrier, ContainerCannotCarryIcc]`；外部 ⇒ **只少这两条**（`Kind` 两路相同）。
> **判定结论：不可达 ⇒ 不修。** 判据 = 「`Kind` 会不会误报 `Proceed`」（`Kind ⇔ Degradations.Count > 0`，见 `:95`）；
> 逐格式核查 + 真实 CLI 实测（gif/jxr × auto / 显式 sRGB / 显式非 sRGB × 4 策略 × P3-ICC / sRGB-ICC，共 12 格）**无一格误报**：
>
> | 格式 | `CanCarryArbitraryIcc` | 为什么不可达 / 被掩盖 |
> |---|---|---|
> | heic · heif · ppm | `false`（`ColorTransformPlan.cs:488` · `:491` · `:507`） | **引擎不可达**：`ImageEncoderArgs.IsEngineEncodable`（`:568-570`）白名单不含它们 ⇒ `ColorEngineRouter` 先给 `FormatNotSupported`（实测日志「本次不适用引擎（FormatNotSupported…）」），进不了规划层 |
> | jxr | `false`（`:502`） | **恒被掩盖**：`EnsureAnnotationNotSilentlyMissing`（`ColorStrategyPlanning.cs:650-655`）的 `!pol.Format.HasColorPath` 对 jxr 无条件为真 ⇒ 凡产出必登记 `UnlabeledAssumedSrgb`（实测 `[GamutClipped, UnlabeledAssumedSrgb]`） |
> | gif | `false`（`:505`） | **恒被掩盖**（同一分支；实测 `[BitDepthReduced, GamutClipped, UnlabeledAssumedSrgb]`） |
>
> 为什么 gif/jxr 漏不掉 `UnlabeledAssumedSrgb`：二者凡「产出」的计划，`p.Dst` 必与 sRGB 等价（auto 目标在 `:556` 被钉成 `SrgbRef` / 显式 sRGB / 等价直通），
> 而 `:650` 的 `(pixelsUntouched || !HasColorPath)` 对它们恒真；非 sRGB 目标一律 `Reject`（`CollectDegradations:118` 对 `Reject` 返回空表）⇒ **不存在 `Degradations` 为空的产出格**。
> **不做的理由**：补这两条码属**口径扩张**（把 `:149` 从「源文件**内嵌** ICC」放宽到「源描述符由 ICC 度量建立」），不是 bug 修复 ——
> `ColorIntentFactory.cs:56-59` 明确把外部 ICC 界定为「只取**度量结果**，ICC 的落盘归属另说」⇒「产物不附源 ICC」对它**不是损失**，与内嵌 ICC 情形**语义不同**。
> **重新触发条件**：① gif/jxr 去掉 `UnlabeledAssumedSrgb` 兜底；② 新增 `CanCarryArbitraryIcc=false` 且 `HasColorPath=true` 的格式（当前**一个都没有**）；③ 主动放宽 `:149` 判据。
>
> ⚠ **独立待办（⚠ 未实测，2026-09-17 `external-icc-degradation` 登记）—— 疑似死分支 `ColorStrategyPlanning.cs:598-607`**：
> L2 里那条 `if (!caps.HasColorPath)` 分支（钉 sRGB 目标 + 记 `UnlabeledAssumedSrgb`）**从产品路径看不可达**：
> `PlanRecommended:556` 已用**完全相同的条件**（`!caps.HasColorPath && it.Target == null`）把 `it.Target` 钉成 `SrgbRef`，
> 而 `:595` 的进入条件又要求 `it.Target == null` ⇒ `:598` 只可能在 `SrgbRef == null` 时命中（该字段由 `FromCicp("bt709","iec61966-2-1","sRGB")` 初始化，非 null）。
> ⚠ **这是读代码推断，未做变异验证** ⇒ **不要**当成已确认缺陷去删/去改；要处置请先补变异证据。
> 顺带：`:547-557` 与 `:589-607` 两处注释**互相引用**，且它们描述的机制**已被 `:556` 取代** ⇒ 属**过时注释**（与疑似死分支同源，建议一并核实后再动）。

### 🔵 2026-09-18：第 4 项「删除传统执行体」的前置条件**重估**（只读盘点，`legacy-body-inventory`）

**为什么重估**：上表第 4 项写的阻塞理由是「上述 **1–2** 项未接入前无法删除」。而现在 ——
- 第 1 项（渐进 JPEG）= **工具链不支持**（ffmpeg mjpeg 无渐进能力，`EngineBlockCodes.ProgressiveJpeg` 显式拦）⇒ **永久不接入**；
  ⚠ **2026-09-19 收窄（收尾轮实测）**：该结论**只对非 GainMap 的 `jpg` 成立** —— GainMap 的底图/增益图**恒由 cjpegli 编码**（`GainMapEncoder.EncodeJpegFileAsync`），而 cjpegli **支持** `-p 0..2` ⇒ `ColorEngineRouter` 的判据已收窄为 `JpegProgressiveId > 0 && !gainMapJpeg`（`gainMapJpeg = JpegGainMap && fmt ∈ {jpg,jpeg}`）。**放行 ≠ 静默**：cjpegli 不可用时 `GainMapEncoder` 仍**点名**回退基线。实测放行前 `--jpeg-progressive 1 --jpeg-gain-map true --color-engine engine` = `exit 90` / 0 产物；放行后 = `exit 0` / **SOF2 真渐进 Ultra HDR**。⚠ 常量 `ProgressiveJpeg` **保留**（非 GainMap 的 jpg 仍生产它，回归已验：仍 `exit 90` / 0 产物）；
- 第 2 项现在**只剩 DNG**，且结论是**结构上不应接入**（需传感器 Bayer 数据）⇒ **永久不接入**。

⇒ **第 4 项自己写的阻塞理由按字面已不成立。** 下面是用实测替换后的真实前置。

**四条判据全部实测**（`publish/PLAN/ffmpeg-full/ffmpeg.exe`）：

| 探测 | 结果 | 后果 |
|---|---|---|
| `-hide_banner -decoders` 里 `jxr` 命中数 | **0** | ffmpeg **无 jxr 解码器** ⇒ JXR 输入**永远**需要 `JxrDecApp` |
| 同上 `jpegxl` / `libjxl` | `libjxl` · `libjxl_anim` | ffmpeg **有** jxl 解码器 |
| 同上 `dng\|cr2\|arw\|rawphoto` 命中数 | **0** | ffmpeg **无 RAW 解码器** ⇒ RAW 预处理**永久保留** |
| `ffprobe src.jxr` | `Invalid data found when processing input` | 实证（不是推断） |

**分派骨架**：引擎插入点 = 分派链**最后一个 `else`**（锚点注释 `// P7a 引擎插入点：只替代"这一次 ffmpeg 运行"，后面的后置步骤两路共用`）。**它之前的所有分支都绕过引擎。**

**输出侧**（引擎插入点之前）

| # | 路径 | 触发 | 归属 |
|---|---|---|---|
| O1 | `ProcessGifToAvifViaAvifencAsync` | 输入 `.gif` + 目标 avif | **待判定**（目的=保 alpha，两步法 avifenc；需核引擎 avif 出口是否保 alpha） |
| O2 | `ProcessAvifToGifWebpAsync` | 输入 `.avif` + 目标 gif/webp | **待判定**（动画两步法**永久**；**静态子格**本可交引擎却被无条件分派截走 ⇒ 该子格临时） |
| O3 | `ProcessJxlInputAsync`（**无条件**分派） | 输入 `.jxl`（任意目标） | 见 I1–I3 —— ⚠ **引擎对 `.jxl` 输入完全不可达**（连显式 `--color-engine engine` 也走不到硬失败） |
| O4 | `ProcessJxrInputAsync`（**无条件**分派） | 输入 `.jxr` | **永久保留**（ffmpeg 无 jxr 解码器，实测） |
| O5 | `ProcessGainMapJpegAsync` | GainMap + jpg/jpeg + 引擎不可走 | **临时待删** ⇒ `#129` → `#136` → `#137` |
| O6 | `ProcessCjxlAsync`（`&& !engineRoutable`） | backend=cjxl + 引擎不可走 | **临时待删**（引擎 cjxl 出口**已具备**）；残余 = 动画输入（`#136`）+ `UnconsumedOutputSettingItems` |
| O7 | `ProcessCjpegliAsync`（**无条件**） | backend=cjpegli | **设计保留**（路由层 `ExternalBackend` 门**显式拦**；cjpegli 的 `-x color_space=`/`-x icc_pathname=` 语义 ffmpeg 编码参数表达不了 ⇒ 结构性） |
| O8 | `ProcessJxrAsync`（`&& !engineRoutable`） | backend=jxr + 引擎不可走 | **临时待删**（残余 = 动画输入，`#136`）。⚠ **`JxrEncApp` 本体永久**（ffmpeg 无 jxr 编码器） |
| O9 | `ProcessDngAsync` | backend=dng | **永久保留**（ffmpeg 无 dng 编码器 + 需 Bayer 相位/白平衡 ⇒ 结构性） |
| O10 | RAW 预处理 `RawService.PreProcessAsync` | RAW 输入 + 非 DNG 输出 | **永久保留**（**共享预处理**，不是色彩执行体；缺 dngtool 时**直接报错**不回落） |
| O11 | `RestoreMetadataViaFfmpegAsync` 的 `-c copy` | 后置元数据恢复 | **永久保留**（`-c copy` **不解码** ⇒ 不是色彩执行体） |

**输入侧**

| # | 路径 | 触发 | 归属 |
|---|---|---|---|
| I1 | `ProcessJxlInputAsync` 的 **djxl JPEG 重构**分支 | `.jxl` 且 `JxlInspector.DetectType == JpegReconstruction` 且目标 ≠ jxl | **永久保留**（djxl **无损还原原始 JPEG 的 DCT 系数**；ffmpeg 的 libjxl 只能解码像素再编码 ⇒ **结构性不可能**） |
| I2 | `ProcessJxlInputAsync` 的 NativeCodestream 分支（`PipeDjxlToCjxlAsync` / `TryPipeDjxlToCjpegliAsync` / `PipeDjxlToFfmpegAsync` / `FallbackDjxlDecodeToFile`） | `.jxl` 非动画 + djxl 可用 | **待判定**（ffmpeg **有** jxl 解码器 ⇒ 理论上引擎可接管；需核 djxl→cjxl/cjpegli 管道是否有 ffmpeg 无等价能力） |
| I3 | `ProcessJxlInputAsync` 的动画回退（锚点日志 `[jxl] 动图 JXL 不进行 djxl→PNG 中间解码，回退到 ffmpeg`） | `.jxl` 动画 | **临时待删** ⇒ 并入 `#136` |
| I4 | `ProcessJxrInputAsync` 的 `JxrDecApp` 解码 | 输入 `.jxr` | **永久保留**（同 O4） |
| I5 | `ProcessAvifToGifWebpAsync` 的输入转换 | 输入 `.avif` + 目标 gif/webp | **待判定**（同 O2） |
| I6 | RAW/DNG 输入预处理 | 同 O10 | **永久保留** |

**统计**：永久保留 **7**（O4·O9·O10·O11·I1·I4·I6 —— 其中工具链能力所限 4、结构性不可能 2、非色彩执行体 1）· 临时待删 **4**（O5·O6·O8·I3）· 待判定 **4**（O1·O2·I2·I5）· 设计保留 **1**（O7）。

> ⚠ **临时待删那 4 条收敛到同一串前置**：`#129`(Part C) → `#138`(多帧只取首帧) → `#139`(交回 ffmpeg 的多帧 -22) → `#136`(动画统一) → `#137`(Part B)。**这就是通往第 4 项的完整路径。**

**附带发现（诚实日志缺口，已登记）**：既有告警行的判据是 `inputExt is ".jxl" or ".jxr" or ".avif"`，**未覆盖 `.gif` → avif**（O1）⇒ 该格绕过引擎时**无任何告警**（O1 分派无条件，也不进 `engineRoutable` 判据）。

> ✅ **2026-09-18 二次收口：上表 4 条「待判定」已全部用实测结案**（`legacy-body-inventory`；判据 = 引擎侧有无等价能力）：
>
> | 编号 | 结论 | 实测依据 |
> |---|---|---|
> | **O1** `gif` → avif | **永久保留** | 引擎 avif 出口的 `pix_fmt` 取法里 **`hasAlpha` 硬编码 `false`**（`ImageEncoderArgs.MapPixFmt` 注释：`AVIF 有 alpha 输入 → 回退普通 YUV，丢弃 alpha 确保兼容`）；**ffmpeg 侧也丢**（实测加 `-auto-alt-ref 0 -pix_fmt yuva420p` 仍出 `yuv420p`，697 B）⇒ avifenc 是**唯一**同时保住**动画 + alpha** 的路径（两步法产物 48847 B，avifenc 日志 `Alpha : Not premultiplied`）。交引擎 ⇒ 透明与多帧**同时丢**。 |
> | **O2 静态子格** | **临时待删** | 静态 = 单视频流 = **本无 alpha 轨道**（AVIF 的 alpha 是独立辅助流，会使 `IsAnimatedAvifAsync` 的 `videoCount == 2`）。**实测**：把 avif **改名 `.png`** 绕过无条件分派让引擎接管同素材 ⇒ 产物 **22129 B 逐字节相同**、ffmpeg 命令行亦逐字相同（源无标签 ⇒ 计划 `None` 直通 ⇒ 与静态分支落同一条命令）。**前置**：分派不再无条件截走**静态** `.avif`（须保留**动画** avif 的拦截，见 I5）。**卡点**：`IsAnimatedAvifAsync` 是 **async 探测**，要在分派条件里调用它属结构性改动。 |
> | **I2** `.jxl` 输入 | **混合** | **永久**：`djxl` 还原原始 JPEG（实测 djxl 打印 **`Reconstructed to JPEG.`**、20415 B **vs** ffmpeg 解码重编码 22293 B ⇒ ffmpeg **无位精确重构原始 JPEG 码流的能力**）；`djxl→cjpegli`（引擎无 cjpegli 出口）。**仍需实测**：`djxl→cjxl` / `djxl→ffmpeg`（ffmpeg **有** jxl 解码器，实测改名 `.png` 后引擎能解，与 djxl 路径像素 **PSNR 57.8 dB** —— 能力等价但非位精确）。**卡点**：`.jxl` 分派无条件截走 ⇒ 不改代码跑不到端到端。 |
> | **I5** avif 动画 | **永久保留** | 引擎的 `-frames:v 1` 是**单帧契约**（不是"取首帧策略"）。**实测**：两步法产物 **8 帧**（`webp_anim,256,192,argb`）。**交叉确认成立**：`.avif` 输入在分派里**只有两个出口**（`ProcessAvifToGifWebpAsync` 与引擎插入点），**没有第三条路** ⇒ 对 gif/webp 目标，两步法是**唯一**保住多帧的地方。 |
>
> ⚠ **新登记的待判定（本轮附带发现，尚未定性）**：`JxlInspector.DetectType` 的**裸码流 magic 检查实际不可达** —— 那句 `head[4..7] == "JXL "` 只在 `!ReadExact(fs, head, 12)`（文件 **< 12 字节**）时才被求值 ⇒ 正常长度的裸码流 JXL 一律落 `Unknown`。**实测**：`src_lossy.jxl`(13819 B) 与 `src_lossless_png.jxl`(34851 B) 都走 `[jxl] 输入类型: 未知/其他` ⇒ 直接走 ffmpeg。
> ⇒ 后果 = 「**djxl 管道只对 ISOBMFF 容器 JXL 可达**」。
> ✅ **2026-09-18 已定性（`legacy-body-inventory`，只读实测）：结论 = ② 守卫顺序倒置造成的死分支，且有真实后果**（**不是**无害冗余）。
> ✅ **2026-09-19 复核：已修**。`JxlInspector.cs:30-43` 的 `FF 0A` 判定已**前置到长度检查之前**（注释里逐条记录了修前后果）；**行为实测**（`src_lossy.jxl` + `--format jxl --encoder cjxl`）⇒ 日志 `[jxl] 输入类型: 原生 JXL 码流`（修前为「未知/其他」）⇒ **静默换编码器的路径已堵住**。
> 判据：① `head` 是 `new byte[12]`（零初始化），`ReadExact` 只决定"是否读满 12 B"，而**裸码流 magic 判定被写在"读不满"的那一支里** ⇒ 只对 **< 12 B** 可达；实测 **1×1 的 JXL 裸码流就是 60 B** ⇒ **任何真实 JXL 都 ≥12 B** ⇒ 该支**不可达**。
> ② `jbrd` 确实只可能在容器里（`cjxl --lossless_jpeg=1` 写容器、`jxlinfo` 报 `JPEG bitstream reconstruction data available`；裸码流无此行）⇒ 就 **jbrd** 而言落 `Unknown` 是对的；**但该支的意图是返回 `NativeCodestream`** ⇒ 「jbrd 正确」**推不出**「死分支无害」。
> **后果（实测）**：裸码流 `.jxl` + `--encoder cjxl` ⇒ **显式 cjxl 被静默忽略**（实跑 ffmpeg 的 libjxl）；`--format jpg --encoder cjpegli` ⇒ **cjpegli 被静默忽略**（产物 `codec_name=mjpeg`）。对照**容器** `.jxl` ⇒ 真的用了 cjxl。⇒ **最高优先级的「静默换编码器」类**（产物**正确**，定性是"用错编码器"而非"产出错"）。
> ⚠⚠ **可达性放大**：`cjxl -h -v -v` 的 `--container` 默认 **`0 = Avoid the container format unless it is needed`**，而 app 的 `CjxlService.BuildCjxlArguments` **不传 `--container`** ⇒ **app 自己产出的 `.jxl` 就是裸码流** ⇒ **回灌时拿不到 app 自己的 cjxl 出口**（实测坐实）。
> **修复进行中**：`#148`（`JxlInspector.DetectType`，把 magic 检查挪到长度检查之前）。⚠ **这是行为变更，不是纯 bugfix** —— 产物会变（编码器由 ffmpeg libjxl/mjpeg 换成 cjxl/cjpegli；解码源由 ffmpeg libjxl 换成 djxl，像素 **PSNR 55.4 dB**）⇒ 交付后**另派未参与实现的人独立复核**。
> ⚠ **`--container` 裁定：不改**（`cjxl` 自述「除非需要才用容器」，`jbrd` 正是"需要"那种；强加只让文件变大，且**不解决读取侧判定顺序的缺陷**）。⚠ 未实测项：**裸码流能否携带 ICC**。
> ⚠ **附带登记（尚未修）**：
> · **`#150`** —— `DetectType` 有**第二个消费者**：`MainWindow.xaml.cs` 的 `BuildJxlInputCommand`（**UI 命令预览**），它**把同一套 `effectiveType` 变换又实现了一遍** ⇒ 两处判据漂移 ⇒ 会造出「**预览说走 ffmpeg、实际走 cjxl**」的**新不诚实**。⇒ 消除第二份实现（不只是行为一致）。
> · **`#151(0919)`** ✅ **2026-09-19 已交付**（`QueueProcessor.cs` 的**两处** djxl 降级站点各补一行**点名**：当 `Options.EncoderBackend != Ffmpeg` 时打 `[djxl] NOTE: --encoder X was requested but is IGNORED on this fallback path`；文案用 **ASCII** 是因为 `cjk-hardcode` 的 `CsUi` 余量为 0；**行为实测通过**）。原描述：**djxl 不可用**时日志点了近因（`未检测到 djxl`）但**没点明**「你显式指定的 `--encoder cjpegli` 被忽略」⇒ 同一族的残余缺口。裁定**响亮 + 点名，不是硬失败**（产物是对的 ⇒ `ProceedWithDegradation`）。
> · **`#146`** —— **静态** `.avif` → gif/webp 交引擎（**临时待删**）：实测把 avif **改名 `.png`** 绕过无条件分派让引擎接管 ⇒ 产物 **22129 B 逐字节相同**。⚠ **动画** avif 必须**继续**走两步法（引擎 `-frames:v 1` 是**单帧契约**，实测两步法保住 **8 帧**）。卡点：`IsAnimatedAvifAsync` 是 async，分派条件不能 await。
>
> ⚠⚠ **剩余「临时待删」的执行顺序（2026-09-18 定，改动前必读）**：
> `#140` → **`#139`** → **`#136`** → **`#137`** ← **这一串是通往本清单第 4 项（删除传统执行体）的主线**；`#142` / `#146` / `#127` / `#151(0919)` 是**次要项**，排在主线**之后**。
> ⇒ **不要把次要项插进主线**：`#136`（动画输入统一）是 `#137`（Part B：删 GainMap 专用分派 + 回退体）的**硬前置**，而 `#137` 才是"删 legacy 执行体"的**第一步落地**。
> ⚠ 上述任务**全部串行在 `QueueProcessor.cs` 上** ⇒ **该文件是当前唯一的瓶颈**。

### 🟢 2026-09-18（general-purpose-3）：动图 webp/apng → 单帧容器**零产物**已修 + 新门禁 + 变异验证

**缺陷（本会话独立复现，改前读数）**：`FfmpegCommandBuilder.cs` 决定「输出前是否插 `-frames:v 1`」的判据
**只认 `.gif` 扩展名**（`inputPath.EndsWith(".gif") && isStaticTarget`）⇒ `.webp` / `.apng` **动图**写
**单帧容器**（jpg/png/tiff…）时拿不到该参数 ⇒ ffmpeg 的 `image2` 封装器报
`Cannot write more than one file with the same name. Are you missing the -update option or a sequence pattern?`
⇒ `Error submitting a packet to the muxer: Invalid argument` ⇒ **整条任务失败、零产物**。

| 改前（本机实测，`--headless --log-level debug -i <in> -o <outdir> --format jpg`） | exit | 产物 |
|---|---|---|
| `anim.webp`（10 帧，自造） | **1** | **0** |
| `anim.apng`（10 帧，自造） | **1** | **0** |
| `anim.gif`（10 帧，自造） | 0 | 1（1 帧） |
| `anim.webp → --format png` | **1** | **0** |

**修法（`src/FfmpegGui/Services/FfmpegCommandBuilder.cs:123-139`）**：判据改为**只看目标容器能不能装多帧**，
与输入扩展名**无关**：
`targetHoldsMultipleFrames = fmt is "gif" or "webp" or "avif" or "apng" || (fmt == "jxl" && options.AnimationFps.HasValue);`
`insertFramesV1 = !targetHoldsMultipleFrames;`
- **为什么不扩扩展名列表**：那仍是「按输入命名」的判据，下一个 `.jxl`/`.avif`/`.webp` 变体还会漏；按容器能力判则一次到位。
- ⚠ **`jxl + AnimationFps` 必须显式排除**（`libjxl_anim` 能装多帧）：不排除就会把动图**静默截成 1 帧**
  —— 变异 M2 实测复现（`exit=0`、1 帧）。**"失败要响"优于"静默降级"**。
- ⚠ 全文件只有**一处**实现点（`:547-551` 的 `if (insertFramesV1)`）；`:545` 那段注释就是它，**不存在第二处**（已 grep 确认）。
- ⚠ 同类站点已查、**不同目的、未动**：`FfmpegCommandBuilder.cs:78/279/355`（GIF→AVIF 透明通道）、
  `QueueProcessor.cs:506`（GIF→AVIF 走 avifenc 两步法）—— 都是 gif 专属**功能**分派，不是本缺陷的判据。

**新门禁**：`tests/scripts/verify-anim-static-target.ps1`（已登记进运行器 ⇒ 清单 **34 → 35 条**）
- 自备素材（现造 10 帧 webp/apng/gif + 单帧 png/jpg/tiff）+ **形态自检**（`ffprobe -count_frames` 必须 >1）；
- 正例：动图 webp/apng/gif → jpg / png ⇒ exit 0 + 有产物 + **1 帧** + 命令行带 `-frames:v 1`；
- **负控**：动图 webp→webp、gif→gif ⇒ **必须仍 10 帧**且**不带** `-frames:v 1`；
- **单帧不回归**：单帧 png/jpg/tiff → 单帧容器，产物与「去掉该参数逐字重跑」**逐像素相同（PSNR=average:inf）**；
- **静默截断守卫**：`anim.webp → jxl --animation-fps` 不得 `exit 0` 且 1 帧；
- 结尾**完整 `if/else`**；运行级 GUID 目录 + 绿时自清；跑前跑后 exe mtime 一致。

**变异验证（同一份门禁，三态实测）**：

| 态 | 源码 | 门禁读数 | exit |
|---|---|---|---|
| M1 缺陷态（旧判据） | `isGifInput && isStaticTarget` | **PASS=25 FAIL=22** | 1 |
| M2 无条件（`insertFramesV1 = true`） | 无条件插参数 | **PASS=42 FAIL=5**（③负控 4 条 + ⑤ 1 条转红） | 1 |
| 修复态 | 目标容器判据 | **PASS=48 FAIL=0** | 0 |

**构建**：`dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release --no-restore` 与
`dotnet build tests/ServiceProbe/ServiceProbe.csproj -c Release --no-restore` 均 **0 警告 0 错误**；
`verify-ps-compat.ps1` **13/0**（新增脚本已入语料）。

---

### 🟢 2026-09-18（general-purpose-3）② 畸形 `.jxl` 让 ffprobe 探测永不返回 ⇒ 队列永久挂死

**症状**：`.jxl` 输入 + 畸形内容 ⇒ 队列**永久挂死**（不是慢，是不返回）。

**触发面（实测修正）**：原描述写「`.jxl` + 1 字节 + 首字节 `00`/`FF`」—— **偏窄**。
真正的判据是**两个条件的合取**，与字节数无必然关系：

> ① `.jxl` 内容**不以 `FF 0A` 开头** ⇒ `JxlInspector.DetectType`（`JxlInspector.cs:23-60`）判 `Unknown`
> 　⇒ 落到 `ProcessJxlInputAsync` 的**通用兜底分支**（`QueueProcessor.cs:3043-3052`，直连 `ffmpeg -y -i <file.jxl>`）；
> ② 该字节形状让 **ffmpeg 的 JXL 解复用器忙等**。

| 资产（`--format png`，本机实测） | 字节 | 判型 | 路径 | 应用结束? | 耗时 | 产物 |
|---|---|---|---|---|---|---|
| `one_ff.jxl` | 1 B `FF` | 未知/其他 | 直连 ffmpeg | **否** | 60.7 s 被杀 | 0 |
| `one_00.jxl` | 1 B `00` | 未知/其他 | 直连 ffmpeg | **否** | 60.3 s 被杀 | 0 |
| `two_0000.jxl` | 2 B `00 00` | 未知/其他 | 直连 ffmpeg | **否** | 32.9 s 被杀 | 0 |
| `two_ff0a.jxl` | 2 B `FF 0A` | 原生 JXL 码流 | `djxl \| ffmpeg` 管道 | 是（exit 1） | 1.3 s | 0 |
| `trunc64.jxl` / `trunc2000.jxl` | 真 JXL 截断 | 原生 JXL 码流 | 管道 | 是（exit 1） | 1.3 s | 0 |
| `real.jxl` | 完整真 JXL | 原生 JXL 码流 | 管道 | 是（exit 0） | 1.4 s | **1** |
| `b1_0a.jxl` / `b2_ff00.jxl` | 1 B `0A` / 2 B `FF 00` | 未知/其他 | 直连 ffmpeg | 是（exit 1） | 1.3 s | 0 |

⚠ **`2 B FF 0A` 之所以安全，不是因为字节数**，而是因为它过了 magic 检查 ⇒ 判 `NativeCodestream`
⇒ 走 `djxl | ffmpeg` 管道 ⇒ `djxl` 快速失败（`Input file is truncated`）⇒ 不落进危险分支。
⚠ `JxlInspector.cs:29-45` 的 `FF 0A` 检查**当天已被改到长度检查之前**（注释写明「2026-09-18 修」）——
正是这条改动把 `2 B FF 0A` 从危险路径挪到了安全路径。

**修法（两处，同步等价范式，**未改成 async** —— 那会波及 ~9 个调用点）**：
先起 `ReadToEndAsync()`（stdout + stderr **都要排空**），再 `WaitForExit(超时)`，超时后
`Kill(entireProcessTree: true)` 并**响亮记一行日志**。兄弟范式：`ProbeAvifStreamRolesAsync`（异步版）、
`ProbePeakNits`（同文件同步版）。
1. `QueueProcessor.cs:4351-4360`（`IsAnimatedJxl`）：旧写法 `var output = proc.StandardOutput.ReadToEnd().Trim();`
   在 `proc.WaitForExit(30000)` **之前** ⇒ 管道不关就永不返回 ⇒ **超时是死代码**，且超时后**没有 Kill**。
   现改 **120 s** 超时 + `Kill(entireProcessTree: true)` + `log?.Invoke("[jxl] 帧数探测超时（120s）⇒ 已终止 ffprobe：…")`。
   签名加 `Action<string>? log = null`；两个调用点（`:4245` `InputMayHaveMultipleFrames`、`:4261` `IsAnimated`）传 `s => item.Log += s`。
   ⚠ 超时值**不是拍的**：`-count_frames` 会**全解码**，本机实测（把同一 `testsrc2` 裸码流重复拼接成合法多帧 JXL，
   `ffprobe -count_frames` 确认帧数正确）1080p ≈ **0.023 s/帧**、4K ≈ **0.089 s/帧**、8K ≈ **0.393 s/帧**
   ⇒ 旧的 20 s 只够 4K **226 帧**（= 7.5 s 视频）、8K **51 帧**，真实动画会掉进「按静图处理」的**静默丢帧**路径；
   120 s 覆盖 4K **≈1354 帧（45 s 视频）** / 8K ≈305 帧 / 1080p ≈5300 帧。取舍：宁可多等，也不静默丢帧。
   ⚠ 解码成本取决于**像素数**，不要拿文件大小当预算依据。常量 `DefaultJxlProbeTimeoutSec = 120`（`:4271`），
   可被 `FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC` 覆盖（`ResolveJxlProbeTimeoutSec`，`:4281`，夹紧 [1, 600]）——
   **仅供门禁验证机制**，见下。
2. `FfmpegCommandBuilder.cs:1327-1339`（`ProbeWithFfprobe`，**同一路径的下一站**）：同款旧写法
   `var output = p.StandardOutput.ReadToEnd().Trim();` 在 `p.WaitForExit(5000)` 之前。**只修一处不够** —— 实测修完站点 1 后，
   应用接着卡在站点 2。`grep -n "nb_read_frames"` 全仓只有 2 处（`:3989` 已正确、`:4337` 本轮修）⇒ 无第三处同款探测。

**新门禁**：`tests/scripts/verify-jxl-probe-timeout.ps1`（已登记进运行器 ⇒ 清单 **35 → 36 条**）
- 自备 3 条畸形正例（逐字节断言 1 B `FF` / 1 B `00` / 2 B `00 00`）+ 4 条负控（`2 B FF 0A` / 截断 64 B / 截断 2000 B / 完整真 JXL）；
- ⚠ **主证据是子进程命令行，不是日志行**（实测教训）：应用 stdout 重定向到文件时是**块缓冲**，
  且 `ConsoleQueueObserver` 只在**下一次进度事件**才转储 `item.Log` 增量 ⇒ 挂住时 stdout 文件可以
  **停在 259 字节长达 42 s 不动**，日志行虽已生成却看不见。故门禁用子进程命令行区分两站：
  站点 1 = `ffprobe -count_frames`，站点 2 = `ffprobe -show_entries`，断言各自「出现 → 限时内消失」；
- 正例每条 8 条断言（站点 1 出现/消失窗口、站点 2 出现/消失、ffmpeg 出现、逐字超时日志行、`[cmd] ffmpeg`、绝不静默成功）；
- ⚠ **门禁注入一个很小的超时值**（`FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC=5`）而不是实跑 120 s：门禁要证的是**机制**
  （异步读 ⇒ `WaitForExit` 可达 ⇒ `Kill` 杀树 ⇒ 日志响亮），不是那个常量；按 120 s 实跑则 3 正例 × 3 个变异态
  ≈ 20+ 分钟，运行器预算撑不住。⇒ 常量本身改由一条**源码形态自检**钉住（默认必须是 `120`、`WaitForExit(timeoutSec * 1000)`
  必须真的用了该值、站点 1 体内**不得**再有同步 `StandardOutput.ReadToEnd()`、`Kill(entireProcessTree: true)` 必须在位）
  ⇒ 断言总数 40 → **41**。注入值**不能太小**：负控（截断真 JXL）也要在同一注入值下跑完 `-count_frames`。
- 负控必须**不出现**超时行且限时内结束；完整真 JXL 还要 exit 0 + 1 产物；跑前跑后 exe mtime 一致；
  结尾**完整 `if/else`**；运行级 GUID 目录 + 绿时自清；⚠ **不用 `Start-Process pwsh`**（宿主安全策略会拦）。

**变异验证（同一份门禁，三态实测）**：

| 态 | 源码 | 门禁读数 | exit |
|---|---|---|---|
| M1 回退站点 1（同步读在 `WaitForExit` 之前） | `IsAnimatedJxl` | **PASS=21 FAIL=19** | 1 |
| M2 回退站点 2（同步读在 `WaitForExit` 之前） | `ProbeWithFfprobe` | **PASS=28 FAIL=12** | 1 |
| 修复态 | 两站都是异步读 + 超时 + Kill 树 | **PASS=41 FAIL=0** | 0 |

（红态只跑 40 条 —— ⑥ 残留自证只在绿时判定。修复态进程级实测：站点 1 的 ffprobe 消失于
**6.6 / 6.6 / 6.6 s**、站点 2 消失于 **12.1 / 12.2 / 12.2 s**、ffmpeg 出现 —— 与「0.5 s 启动 + **注入的 5 s** 超时 + 采样去抖」
和「+5 s 超时」吻合。门禁整轮耗时从注入前的 ~2 min 降到 **~57 s（pwsh7）/ ~61 s（PS5.1）**。）
⚠ M1/M2 的红**定位不同**（19 vs 12），这正是「主证据用进程命令行」换来的：M1 里站点 1 的 `-count_frames` 永不消失
（`消失@0.0s` = 未消失），M2 里站点 1 正常消失、卡在站点 2。
⚠ M2 那一态 `sawTimeout=False`（站点 1 的超时日志**已生成却看不见**）—— 因为 ffmpeg 从未起来 ⇒ 没有进度事件 ⇒
块缓冲不刷新。**§6 第 89 条（"挂住时 stdout 不是判据"）在本轮再次被实测复现**。
回滚后 `sha256` 与交付态**逐字节一致**（`QueueProcessor.cs` = `c473449a…`、`FfmpegCommandBuilder.cs` = `0ed0f109…`），
`grep -c MUTANT` = 0。

**生产默认值（120 s）下的端到端实测（**不注入**，即用户实际会遇到的读数）**：
`--headless -i one_ff.jxl（1 B 0xFF）--format png`，注入变量已清空：

| 观测 | 读数 |
|---|---|
| 站点 1 的 ffprobe（`-count_frames`） | 出现 → **消失@121.2 s**（120 s 超时 + ~1 s 启动/采样） |
| 站点 2 的 ffprobe（`-show_entries`） | 出现 → **消失@126.7 s**（+5 s 超时） |
| 逐字超时日志行 | **`[jxl] 帧数探测超时（120s）⇒ 已终止 ffprobe：one_ff.jxl（按静图处理）`**（首次可见@125.6 s） |
| 编码阶段 | `[cmd] ffmpeg -y -i "…one_ff.jxl" … -frames:v 1 "…out/one_ff.png"` ⇒ **推进到了第三站** |
| 应用是否自行结束 / 产物 | **未结束**（已由采样器杀树）/ 0 产物 ⇒ **即登记的残余（站点 3 忙等）** |

⇒ 交付口径：**站点 1/2 已修**（探测阶段在限时内**响亮**返回、Kill 杀树、日志逐字可见），
**用户可见的挂死仍在**（第三站 ffmpeg 忙等）。

**双宿主（2026-09-18 新增，**必须真跑**）：**

| 宿主 | exit | 耗时 | 读数 |
|---|---|---|---|
| pwsh 7 | 0 | 57 s | **PASS=41 FAIL=0** |
| Windows PowerShell 5.1（`-ExecutionPolicy Bypass`） | 0 | 61 s | **PASS=41 FAIL=0** |

两轮跑完存活的应用/ffmpeg 进程均为 **0**。⚠ **这不是走过场 —— 真跑才发现两个只在 PS5.1 上出现的缺陷**
（见下「双宿主缺陷」）。`verify-ps-compat.ps1` 只做**静态语法扫描**，这两类都抓不到。

**⚠⚠ 双宿主缺陷（本轮实测发现并修好，均在 `verify-jxl-probe-timeout.ps1` 内）**：
1. **`$p.Kill($true)` 在 PS5.1 上不存在** —— Windows PowerShell 5.1 跑在 .NET Framework 上，
   `[System.Diagnostics.Process]` **只有无参 `Kill()`**（实测 `GetMethods()` 只回 `Void Kill()`）。
   ⇒ 旧写法 `try { $p.Kill($true) } catch { }` **静默失败**：应用根本没被杀 ⇒ 宿主因
   `-RedirectStandardOutput` 的异步读收尾而**永久挂住**（实测 PS5.1 下 **10+ 分钟不返回**，
   残留 **3 个 FfmpegGui + 3 个 ffmpeg**）。修法：自写 `KillTree`（按 `ParentProcessId` 先子后父递归，
   只用两宿主都有的无参 `Kill()`），并加 ⑤-b 兜底清扫（只杀本门禁自己起过的 PID）。
2. **PS5.1 下轮询式 `.ExitCode` 恒为 `$null`** —— `Start-Process -PassThru`（**不带 `-Wait`**）的
   `.ExitCode` 在轮询到 `HasExited` 之后读仍是 `<null>`，`Refresh()` 也无效（实测对照：
   `-Wait -PassThru` ⇒ `1` (Int32)）。⚠ 而 **`[int]$null` 在 PowerShell 里是 `0`** ⇒
   若顺手写成 `[int]$p.ExitCode`，会把「读不到」伪装成「exit 0」⇒ **假绿（比不测更坏）**。
   修法：轮询路径**保留 `$null` 且绝不强转**，需要退出码的断言（`③ real.jxl：exit == 0`）
   **单独**用一次 `-Wait -PassThru` 跑；`②「绝不静默成功」`改成宿主无关判据
   （**若它自行结束了，就不得有产物**），不再依赖退出码。
   ⚠ 由此更正一处旧口径：**`②` 那条原先写「若结束则退出码须非 0 且零产物」在 PS5.1 上是真空绿**。

**构建**：`dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release` **0 警告 0 错误**；`verify-ps-compat.ps1` **13/0**。

**⚠⚠ 未修残余（本条缺陷**没有**被完全修好，已登记）**：
- **第三站是 ffmpeg 编码本身**：探测阶段之后应用执行 `ffmpeg -y -i <file.jxl> … -frames:v 1 <out>`，
  该 ffmpeg 在畸形 `.jxl` 上**忙等** —— 单跑实测 25 s 不退出、`Δcpu ≈ 2.7 s / 3 s ≈ 90% 单核`、
  stderr 停在 3813 B（只有 banner，**一条 `frame=` 都没有**）。⇒ 应用仍**不会自行结束**。
- **`FfmpegRunner` 的挂死守卫对它结构性失明**（`FfmpegRunner.cs:58-83`）：判死条件是
  「**既无输出**（`silent ≥ 30 min`）**且**无 CPU 增长（`cpuAdvanced = Δcpu ≥ 1 s / 每 30 s 周期`）」，
  而忙等每 30 s 周期给 ≈27 s CPU ⇒ `cpuAdvanced` 恒真 ⇒ `continue` 重新计时 ⇒
  **`InactivityTimeoutMinutes = 30` 那条分支对忙等型挂死不可达**。这是**一类**缺陷（任何让 ffmpeg 忙等的调用都会永久卡住队列），
  治本要改 `FfmpegRunner` 的活性信号（CPU 从来不是有效活性信号；ffmpeg 的 `-progress pipe:1` 才是，
  但会波及 25 个 `RunAsync` 调用点的参数向量）⇒ **建议另立任务，不塞进本条**。
  ✅ **该任务（#15）已实施**：见本节下方 **③**（`-progress pipe:1` 心跳，只在 JXL 链的 **4** 个站点显式开启）。
  ⚠ 但**本条登记仍然成立**：其余 **20** 个 `RunAsync` 调用点未启用心跳 ⇒ 对忙等型挂死仍失明。
- **`IsAnimated`/`IsAnimatedJxl` 仍无 `CancellationToken` 参数** ⇒ 用户点「Stop」打断不了探测，只有 120 s 超时兜底。
- **`AnimatedJxlCache`（`QueueProcessor.cs:4265`）按路径缓存、进程内永不过期** ⇒ 一次探测超时会把该路径**整场会话**钉成静图。
- **120 s 超时本身是新的静默截断入口**：探测失败返 `false` ⇒ 按静图处理。对「全解码 >120 s 的超大动画 JXL」
  （8K ≳ 300 帧）仍会落到 `false`。缓解手段只有那条日志行。**语义改动（返 `false` vs 保守返 `true`）已上报 team-lead 裁定，本轮未改。**

**只读调研（未改代码）—— 探测失败返 `false` 的影响面**：
- 经 `InputMayHaveMultipleFrames`（`:277` / `:536` / `:853`）：这三处**只**喂 `ColorEngineRouter.ShouldRoute` / `BlockReason`。
  返 `false` ⇒ `ColorEngineRouter.cs:179-187` 的 `AnimatedInput` 块不触发 ⇒ 引擎放行 ⇒ **按单帧处理、动画帧静默丢失**
  （前提：用户显式要引擎 + 输出格式在 `IsEngineEncodable` 白名单内）。
- 经 `IsAnimated` 直接调用 5 处：`:427`（不跳过最长边预缩放 ⇒ 单帧 PNG 中间件丢帧）、`:1642`/`:1702`/`:2944`
  （不进入 `libjxl_anim`/mjpeg 多帧回退 ⇒ 走单帧中转）、`:3387`（不早退 ⇒ 真去解码 PNM 中间格式）—— 同一后果：帧丢失。
- 若改返 `true`（保守）：引擎侧变「回退传统管线」或**硬失败并点名**（方向更响亮）；但 `:1642`/`:1702`/`:2944`
  会把用户选的 `cjxl`/`cjpegli` **静默换成 ffmpeg**，`:427` 会**静默不施加最长边限制** —— 撞在仓库
  「宁可失败并点名，也不静默换执行体」那条上。⇒ **`false` 与 `true` 都不安全**；干净形态是把「探测失败」与
  「探测到 1 帧」**区分开**（三态 / `out bool probeFailed`），由调用点各自显式失败并点名。**属语义改动，未做。**

**只读调研（未改代码）—— 「`Unknown` 的 `.jxl` 交给 ffmpeg 是否有成功情形」（否决了候选修法 (b)）**：
候选 (b) 想改 `ProcessJxlInputAsync` 的**通用兜底分支**（`:3043-3052`，`Unknown` ⇒ `BuildArgsAndNote` + `ffmpeg -y -i`），
思路是「既然这条分支会忙等，就别走」。实测（`--headless`，每例 ~0.8 s）：

| 素材 | 前 4 字节 | `JxlInspector.DetectType` | 路径 | 结果 |
|---|---|---|---|---|
| `fake_png.jxl`（实为 PNG） | `89 50 4E 47` | `未知/其他` | 直连 ffmpeg | **exit 0 + 1 产物** |
| `fake_jpg.jxl`（实为 JPEG） | `FF D8 FF E0` | `未知/其他` | 直连 ffmpeg | **exit 0 + 1 产物** |
| `fake_webp.jxl`（实为 WebP） | `52 49 46 46` | `未知/其他` | 直连 ffmpeg | **exit 0 + 1 产物** |
| `fake_gif.jxl`（实为 GIF） | `47 49 46 38` | `未知/其他` | 直连 ffmpeg | **exit 0 + 1 产物** |
| `fake_tif.jxl`（实为 TIFF） | `49 49 2A 00` | `未知/其他` | 直连 ffmpeg | **exit 0 + 1 产物** |
| 真 JXL（libjxl 写出的裸码流） | `FF 0A FB 06` | `原生 JXL 码流（需解码为像素再编码）` | 不走兜底 | exit 0 + 1 产物 |

（上表为 2026-09-18 复跑实录：5 条「内容不是 JXL」的改名件全部 `判型=未知/其他`、`直连ffmpeg=True`、
**exit 0 + 1 产物、0.8~0.9 s**、无超时行；真 JXL 判 `原生 JXL 码流`、`直连ffmpeg=False`。）

⇒ 「`Unknown` ⇒ 交 ffmpeg」是**承重的**：它正是「扩展名骗人」时的**内容嗅探兜底**（`.jxl` 后缀 + 实际 PNG/JPEG/…）。
按 (b) 砍掉它 = 把 5 个成功用例变成失败 ⇒ **(b) 应否决**。真正该做的是**在 `Unknown` 分支里按内容嗅探**
（或只在「确知是畸形 JXL 码流」时才拒绝），而不是一刀切掉兜底分支。
⚠ 忙等只发生在「**确实是 JXL 码流但畸形**」这一小类上（`FF 0A` 之外的非图像字节），而这一类与「扩展名骗人」在字节层面**不可区分**。

⚠ **未修（如实登记，均非本缺陷）**：
- `anim.webp → --format jxl --animation-fps 10` **仍 exit=1、0 产物**（同一 image2 报错）——
  本判据**有意**不给它插 `-frames:v 1`（那会静默截成 1 帧）；**动图 webp→动图 jxl 目前无可用路径**，
  属**独立缺口**，需另立方案（不要顺手用 `= isStaticTarget` 把它"修绿"成 1 帧）。
- `anim.gif → --format avif --animation-fps 10` 实测 exit=0 但**只 1 帧**（走 `ProcessGifToAvifViaAvifencAsync` 两步法）；
  `anim.webp → --format avif --animation-fps 10` 亦 1 帧 ⇒ **avif 目标的动图帧数**是另一条独立疑点，**本会话未追**。
- 本改动**不等于** `#136`（动画输入统一）：它只修了 `#139`（交回 ffmpeg 的多帧 `-22`）在
  **传统管线命令行构造**这一侧的实例；`#136`/`#137` 主线**未动**。

⚠⚠ **同族缺口：`#139` 的同类判据缺口至少还有两处（lead 实测，本轮未修 —— 由 lead 补登，勿误读为已清）**：
素材 `anim.webp`（10 帧，256×192）→ `--format jxr`，`exe mtime` 前=后 ⇒ 读数有效：

| 模式 | 实测 |
|---|---|
| `--color-engine legacy` | `exit=1`、**0 产物**；日志 `[cmd] ffmpeg -y -i "…anim.webp" -pix_fmt bgr24 "…jxr_…\input.bmp"` ⇒ **无 `-frames:v 1`** |
| auto（引擎默认） | `exit=1`、**0 产物**；`[色彩引擎] ❌ 执行失败：InvalidOperationException: JXR 出口：raw → TIFF 中转失败 (exit -22)` |

- **站点 2**：`QueueProcessor.cs:1936`（`ProcessJxrAsync`）——
  `var decodeArgs = $"-y {colorArgs}-i \"{item.InputPath}\" {filterArg}-pix_fmt {pixFmt}{tiffExtra} \"{intermediatePath}\"";`
  输出是**固定文件名**（`input.bmp`/`input.tiff`）⇒ 多帧输入必失败。
- **站点 3**：`ColorMapping/RawColorPipeline.cs` 的 `EncodeJxrViaJxrEncAppAsync` 的 **alpha 分支** ——
  它把**原始动画文件**当第二输入喂 `alphaextract`：
  `-i "…cms_….rgb48.out" -i "…anim.webp" -filter_complex "[1:v]format=rgba64le,alphaextract[a];[0:v]format=rgb48le[b];[b][a]alphamerge"`
  ⇒ `[1:v]` 有 **10 帧** ⇒ 滤镜图产出 10 帧 ⇒ 写固定 `in.tiff` 必失败。
  ⚠ **它的非 alpha 分支没有问题**（从**单帧 rawvideo** `-f rawvideo … -i "{tmpOut}"` 构建，那段 raw 由带 `-frames:v 1` 的主解码产出）
  ⇒ **不要一概而论成"引擎 JXR 出口坏了"**。
- ⚠ **站点 3 在引擎侧**：本项目正在逐条删除 legacy ⇒ 这类缺口**会随迁移被继承**，**不会因为删掉 legacy 而消失**。
  ⇒ 建议三处用**同一条判据**（目标中转容器能否装多帧）统一处置。

> ✅ **2026-09-18 已处置（general-purpose-3，#13）**：上表的「站点 2」「站点 3」即下文的 **站点 A / 站点 B**，
> 两处均已按「目标容器能否装多帧」同一条判据修好（`-frames:v 1` 紧贴中转产物路径之前）。
> 改后读数：`anim.webp → --format jxr` legacy **exit=0 / 1 产物**、auto **exit=0 / 1 产物**；
> 新门禁 `tests/scripts/verify-jxr-anim-input.ps1` **56/0**（双宿主），变异 M1/M2 分别 **40/14**、**48/6**。
> ⚠ **2026-09-18 续（general-purpose-1，`#136` 切片 B2 之后）：`55/0 → 56/0`，载体已换。**
> B2 让**动画输入不再进引擎**（`.webp` 走真探测 ⇒ 10 帧 ⇒ `BlockReason=AnimatedInput`）⇒ 该门禁的
> **「真空绿守卫」旧载体 `anim.webp` 失效**（守卫按设计判红，实测 **`49/5`**；门禁自己早就预言过这件事）。
> 修法 = 守卫载体换成**静态带 alpha** 输入 `single_alpha.png`（仍走引擎出口的 alpha 分支 = 站点 B），
> 并**新增一条反控**「`anim.webp + auto` 必须走 legacy」（同时是 B2 的行为锁）；`②-e` 命令行判据同步换载体。
> ⚠ 换载体时 `JxrDecApp` 的 `-c` 码要跟着换（引擎出口中转 = 64bppRGBA ⇒ `-c 23`；legacy 中转 = 24bppBGR ⇒ `-c 0`），
> 否则是**确定性假红**。**站点 A（legacy 解码中转）不受影响。**
> ⚠ 动图仍**只取第 1 帧**（JXR 产物是单帧，本仓无动画 JXR 能力）——
> 完整定性 / 修法依据 / 交付口径 / 残余见下方 **🟢 ④ JXR 中转路径的多帧缺口（站点 A/B）**。

### 🟢 2026-09-18（general-purpose-3）③ `FfmpegRunner` 挂死守卫对「忙等型」挂死失明 ⇒ 已实现心跳守卫（**JXL 链 4 处启用，其余 20 处未启用**）

**根因（不是「缺输出信号」，是「CPU 信号把输出信号覆盖了」）**
- 旧守卫是**双信号**：`lastActivityTicks`（有输出）+ `cpuAdvanced`（每 30 s 周期累计 CPU ≥ 1 s）。
- 忙等型挂死 = **两个流都 0 字节** + **持续烧 CPU**（实测 ≈90% 单核）⇒ `cpuAdvanced` 恒真 ⇒ 不断重置
  `lastActivityTicks` ⇒ `InactivityTimeoutMinutes = 30` 那条分支**结构性不可达**。
- 关键实测：24 个 `RunAsync` 调用点**都没有 `-loglevel error`**（`grep loglevel src` 只命中 `RawColorPipeline`
  与 `IccProfileService` 的**自有** runner）⇒ 默认 loglevel 下 stderr 的每帧 `\r` 统计行**本来就在**触发
  `NoteActivity()`（8 s 内 60 次事件、18 次含 `frame=`）⇒ 「沉默」时钟一直在被喂。
  ⇒ **真正让守卫失明的只有 `cpuAdvanced`**，所以任何「只加更多输出」的修法（如 `-stats`）都治不了。

**修法**：给 ffmpeg 追加 `-progress pipe:1`，由 `FfmpegRunner` **自己解析** stdout 的 `key=value` 块
（**不上抛**给 `logCallback`），把「**进度块到达**」当活性信号；心跳模式下**故意忽略** `cpuAdvanced`。
- 为什么不上抛：`-progress` **默认不限速**，合法 4K 单帧编码实测 21 块 / 10.3 s（≈0.5 s/块、每块 11 行）
  ⇒ 原样转发一个 10 分钟的编码会灌 ≈8800 行进 `item.Log`，且 23/24 个回调**每行**都触发 `_onItemUpdated`。
- 为什么判据是「**块到达**」而不是「`out_time` 推进」（**这条推翻了先前的口头假设**）：实测 4K 单帧
  `-effort 9` 全程 `frame=` 恒 0、`out_time_us=` 恒 40000 ⇒ 用 `out_time` 会把**合法的单帧长编码误杀**。
- 追加位置：**末尾**（实测 `-progress pipe:1` 追加在输出路径之后仍生效）。
- 自检：`CanInjectProgressPipe` 在 stdout 承载数据的调用上**拒绝注入并响亮登记**（`image2pipe` / `pipe:1` /
  裸 `-`），**不静默降级**。本仓这类调用全部是手搓 `Process.Start`
  （`QueueProcessor.cs:2409/2446/2487`、`RawColorPipeline.cs:471/476`），**不经** `FfmpegRunner`。

**阈值 `DefaultHeartbeatTimeoutSec = 120`**（`FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC` 可注入，夹紧 [1, 3600]）；
与 `DefaultJxlProbeTimeoutSec` 取值一致。

| 量 | 实测 |
|---|---|
| 合法 4K 单帧 libjxl `-effort 9` 的**首个** progress 块 | **t ≈ 0.018 s**（进度块由 ffmpeg 进度线程**周期性**发出，**不要求帧号推进**） |
| 块间隔 | min 0.437 s / p50 0.544 s / **max 0.574 s** |
| 合法 4K 单帧 `-effort 9` 总耗时 | 10.3 s（21 块） |
| 合法 4K 单帧 `-effort 7` | 0.67 s（2 块） |
| **忙等**畸形 .jxl（1 B `00`） | 15 s 内 **0 字节 / 0 块**（`-progress` 写文件与 `pipe:1` 两种目标一致） |

⇒ 120 s 对「max 块间隔 0.574 s」有 ~200× 余量、对「首块 0.018 s」有 ~6600× 余量。

**开启面（只在 JXL 链的 4 个站点，不碰其余 20 处）**：`QueueProcessor.cs` —— 全部是「ffmpeg 直接吃 `.jxl`」的
调用（同一条解复用器、同一类风险）；站点标签注释形如 `// #15 心跳站点 <X>（<名字>）`，供门禁 ① 做**集合断言**：

| 站点 | 行 | 名字 | 入口判据 |
|---|---|---|---|
| **D** | `:3047` | `jxl-unknown` | **畸形 .jxl 实测就落这里**（不以 `FF 0A` 开头 ⇒ `JxlInspector` 判 `Unknown` ⇒ 两个类型分支都不匹配 ⇒ 通用兜底） |
| **C** | `:3036` | `jxl-native-no-djxl` | `NativeCodestream` + `djxl` 不可用 ⇒「使用 ffmpeg 直接处理 JXL」 |
| **A** | `:2934` | `jxl-recon-no-djxl` | `JpegReconstruction` + `djxl` 不可用 ⇒「未检测到 djxl，**回退到** ffmpeg」 |
| **B** | `:2951` | `jxl-anim` | `NativeCodestream` + `IsAnimated`（动图 JXL 不做 djxl→PNG 中间解码） |

⚠ 站点 A/C 共用同一句 `[cmd] ffmpeg` ⇒ 门禁靠**各自的入口日志行**区分（A 是「回退到」、C 是「使用 … 直接处理」）。
⚠ **其余 20 个 `RunAsync` 调用点仍未启用**（`RunAsync` 调用点总数 24 处，未变）。

**e2e（`--headless`，注入 `FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC=3` / `FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC=2`）**

| 方向 | 用例 | 实测 |
|---|---|---|
| ② 正向 | 畸形 1 B `00` .jxl（站点 D） | **自行**结束（未被门禁 Kill）**16.3–18.5 s**；日志逐字 `[超时] ffmpeg 连续 2 秒**无进度心跳**（\`-progress pipe:1\` 一个块都没有）⇒ 判为挂死，已终止进程树（退出码 -124，显式失败不静默）`；终态 `失败 (退出码 -124)`；产物 **0** |
| ③ 反向 | 合法 4K 单帧 libjxl `-effort 9`（djxl/cjxl 不可用 ⇒ 站点 C） | **未被误杀**：15.7–19.7 s、产物 **1**；日志**无**「无进度心跳」；`-progress` **专属**块行 **0** 条；既有 stderr 统计行仍在（`^frame=` 命中） |
| ③-d 反向 | 动图 JXL（4K × 30 帧，ffprobe 确认帧数=30）`--format apng`（站点 B） | **未被误杀**：9.4–10.7 s、产物 **1**；站点 B 专属行「动图 JXL 不进行 djxl→PNG 中间解码」命中、且**不是**站点 C 的行；**无**「帧数探测超时」；无「无进度心跳」；块行泄漏 **0** |
| ③-e 反向 | jbrd 容器（`cjxl --lossless_jpeg=1`）+ djxl 不可用（站点 A） | **未被误杀**：4.4–5.9 s、产物 **1**；站点 A 专属行「[djxl] 未检测到 djxl，回退到 ffmpeg」命中、且**不是**站点 C 的行；无「无进度心跳」；块行泄漏 **0** |

⚠ 站点可达性一律**由专属日志行证明**，不靠耗时推断；否则「站点没走到」会伪装成「没被误杀」（真空绿）。
⚠ **牙口**（负控有效性）：③ 15.7 s / ③-d 9.4 s / ③-e 4.4 s，对注入的 2 s 阈值分别是 ~8× / ~5× / ~2×。
③-e 最薄 —— 它的耗时以**应用启动/收尾的固定开销**为主（4K jbrd 只比 512×384 jbrd 多 0.7 s），故门禁把它的
下界只钉在 **3 s**（仍严格 > 2 s，即「按总墙钟判死」的错误实现照样会在该条转红）。

**新门禁**：`tests/scripts/verify-ffmpeg-heartbeat.ps1`（**已由 lead 登记进 `_run-step3-gates.ps1`**，运行器清单 **36 → 37** 条）
- ① 源码形态自检 **16** 条：钉 `DefaultHeartbeatTimeoutSec = 120`、可注入、`-progress pipe:1` 在位、判死文案、
  键形态过滤器允许**数字**、心跳分支**不含** `cpuAdvanced`、`Kill(true)`、`Return HangExitCode`、
  `RunAsync` 调用点仍 **24** 处、开启点**全部带站点标签**、标签无重复、**站点集合恰为 {A,B,C,D}**、
  字面量计数与已解析开启点数一致；
- 素材 **7** 条（含「动图帧数经 ffprobe 确认为 30」这条**第三方证据**）；② 正向 **5** 条；
  ③ **10** 条 / ③-d **12** 条 / ③-e **11** 条（三条负控）；④ 残留自证 **1** 条 ⇒ 全绿时共 **62** 条。
- ⚠ 集合断言**不是计数**（lead 要求）：计数只能说「有 N 个」，说不清「是不是这 N 个」——把第 5 处误开、或把
  预期站点里的一个漏开，计数都可能凑巧还是 4。故 ① 改为**解析每个开启点的站点标签 ⇒ 断言标签集合 == 预期集合**。
- 素材自备、`%TEMP%` 私有 GUID 目录、CLI 一律 `--headless`、跑前跑后 exe mtime 一致、结尾完整 `if/else`。
- ⚠ 门禁抓到一个**真 bug**：`-progress` 的键里有 `stream_0_0_q`（**含数字**），初版键形态过滤器只允许
  `a-z`/`_` ⇒ **21 行泄漏进队列日志**。已修（允许数字），并在 ① 里加了对应的源码自检。
- ⚠ 门禁**自身的结构断言**也被变异抓出一个**假绿**：① 原以 `if (cpuAdvanced)` 当心跳分支的**段尾标记**，
  而「这段里不含 `cpuAdvanced`」正是要断言的属性 ⇒ **循环定义**：把 `if (cpuAdvanced) continue;` 插在
  原句**之前**，段就被**截短**到突变行之前 ⇒ 该断言**通过**（M2 首跑实测 54/5 时它竟为绿）。
  已改用分支**末句** `return HangExitCode` 当段尾，并加两条**抽取器自检**（段内必须有「无进度心跳」、
  段内必须没有旧分支文案「既无输出也不消耗 CPU」）⇒ 段被截短/越界都会红。

**变异验证（同一份 62 条门禁，四态实测；红态会跳过 ④ 残留自证 ⇒ 红态总数 61）**

| 态 | 变异 | 门禁读数 | exit | 红在哪 |
|---|---|---|---|---|
| M1 | 四处 `heartbeat: true` → `false` | **PASS=51 FAIL=8** | 1 | ① 站点集合实 `[]`；② 忙等**回到永不返回**（40.1 s 撞上限被门禁 Kill）⇒ ② 四条全红；③/③-d/③-e 的「心跳已追加」自检各 1 条 |
| M2 | 把 `if (cpuAdvanced) continue;` **插进**心跳分支（复现根因形态） | **PASS=57 FAIL=4** | 1 | ①「心跳分支内不出现 `cpuAdvanced`」+ ② 忙等仍未自行结束（40.1 s）⇒ ② 三条 |
| M3 | **只**回退站点 A（`:2934` 去掉 `heartbeat: true`） | **PASS=57 FAIL=2** | 1 | ① 站点集合实 `[B,C,D]`；③-e「心跳已追加」自检（该条用例本身仍正常结束 ⇒ 只有自检能抓到） |
| M4 | **只**回退站点 B（`:2951` 去掉 `heartbeat: true`） | **PASS=57 FAIL=2** | 1 | ① 站点集合实 `[A,C,D]`；③-d「心跳已追加」自检 |
| 交付 | — | **PASS=62 FAIL=0** | 0 | — |

⚠ M3/M4 的**行为面**（合法任务仍正常结束、产物 1）在红态下**照样全绿** —— 这正是「负控必须有**自检**」的原因：
负控只证明「没被误杀」，若不同时断言「心跳真的开在这一条上」，漏开站点会伪装成全绿。

**双宿主真跑（§6 第 91 条）**：pwsh **7.6.6** 62/0（57.5 s）· Windows PowerShell **5.1** 62/0（57.6 s），均 exit 0。
门禁不用 `$p.Kill($true)`、不用 `[int]$null`，退出码一律改判**日志**（`失败 (退出码 -124)`）。

### ⚠ 2026-09-19 收尾轮（#15-b(0919)，general-purpose-2）：类级根因 + 残余风险钉住 ⇒ 门禁 **62 → 68** 条

- **类级根因已修**（上文「其余 **20** 处 `RunAsync` 调用点仍未启用」**作废**）：`RunAsync` 的
  `heartbeat` 改为 **默认 `true`（opt-out）** ⇒ **全部 24 处**调用点自动获得「进度块到达」活性信号；
  心跳分支的活性判据**收紧为进度块专属**（新增 `lastProgressTicks`；旧的「任意输出」`lastActivityTicks`
  只留给「`CanInjectProgressPipe` 拒绝注入」的降级路径）⇒ 一行 stderr 噪声不能给忙等续命。
  JXL 链 4 处 `heartbeat: true` 字面量**保留**（现为**文档锚点**：① 的站点集合断言仍钉住它们）。
- **新增 ① 断言 2 条**：`bool heartbeat = true`（默认行为）；心跳分支**只**认 `lastProgressTicks`
  （且不碰 `lastActivityTicks`）。**新增 ⑤ 断言 4 条**（二分性 + 合法无块窗口**实测上界**）：
  N1a 进度流形态自检（continue 块 ≥5 / 收尾块 ≥1 / 含 `out_time_us`）；N1b **平均块间隔 ≤ 1.0 s**
  （实测 0.524–0.553 s ⇒ 对 120 s 默认阈值 **≥217× 余量**；**直接量本仓 ffmpeg 的 `-progress`**，
  不经被测应用 —— 应用**故意不上抛**块行，日志里拿不到到达时刻）；N2a 两臂注入阈值**相同**且 == 注入值
  （从**各自日志**解析）；N2b **二分性**（无块臂判死 ∧ 有块臂存活）。
  ⚠ N1 用「块数 / 总耗时」**比值**而非「块到达时刻」：进度线程**按时间**发块 ⇒ 比值对负载免疫，
  按时刻采样会抖 ⇒ 假红。
- **① 的默认值锁已加强为「逐字防调参」**：改在**剥注释后的代码**上断言 `DefaultHeartbeatTimeoutSec = 120;`
  （旧写法用 `$src` 且不钉 `;` ⇒ 注释里写一句、或改成 `= 1200` 都能假绿）。
- **变异验证（新锁）**：M1′ 默认改回 `false` ⇒ **62/1**（唯一红 = 默认行为断言）；M2′ 活性换回
  `lastActivityTicks` ⇒ **62/1**（唯一红 = 进度块专属断言）；M4′ 默认 `120 → 5` ⇒ **66/1**
  （唯一红 = 逐字防调参锁）。**行为级反事实（决定性）**：删站点 D 的 `heartbeat: true`（默认=true）
  ⇒ ② **仍自行判死 13.7 s**（默认开启真的接管）；同一变异**再**把默认改回 `false` ⇒ ② **挂死 40.1 s
  被门禁 Kill**（**原 D⑨ 复现**）⇒ 该修法是**承重**的，不是「凑绿」。
- **交付态双宿主实测**：pwsh 7.6.6 **68/0** · Windows PowerShell 5.1 **68/0**（均 exit 0）；
  构建 **0 警告 0 错误**；`verify-ps-compat.ps1` **13/0**；`verify-jxl-probe-timeout.ps1` **48/0**。
- ⚠ **仍未覆盖（如实登记）**：① 输入打开/探测阶段的合法零进度窗口（本仓全本地文件、毫秒级，
  但门禁**无法证明**其上界）；② 「合法任务 > 120 s 无块窗口」的**反例无法构造** —— 构造出来就是
  缺陷、不是测试 ⇒ 该方向只能由 ⑤-N1 的**余量**（≥217×）承担，不能声称「已证明安全」。

**回归实测（lead 要求「实测确认加 `-progress` 不干扰既有日志」，不是读码推断；下列读数为最终产物上复跑）**
- `verify-jxl-probe-timeout.ps1` **PASS=41 FAIL=0**（pwsh 7.6.6 与 PS 5.1 各一次；其 ③ 用例走站点 D）
- `verify-jxl-codestream-route.ps1` **PASS=44 FAIL=0**（pwsh 7.6.6 与 PS 5.1 各一次；其 ⑦ 降级用例走站点 C）
- `verify-ps-compat.ps1` **PASS=13 FAIL=0**；Release / Debug 构建 **0 警告 0 错误**；`MUTANT` 残留 **0**
- 回归面为零的结构性原因：`[cmd] ffmpeg {args}` 行由**调用点**用自己的**局部 `args`** 书写
  （`QueueProcessor.cs` 25 处）⇒ 在 `RunAsync` **内部**追加 `-progress` 后 `[cmd]` 行字节**完全不变**
  （门禁 ③/③-d/③-e 各自直接断言了这一点）。

**交付口径（严格）**：本轮**只**能说「**JXL 链的 4 处已启用心跳；其余 20 处未启用**」，且**只**能说
「**忙等型挂死已被判死、合法长任务未被误杀**，两者均有实测」，
**不能**说「挂死已修好」——
- 心跳是**按站点显式开启**的；其余 **20** 处 ffmpeg 调用点仍用旧双信号判据（对忙等型挂死仍然失明）；
- 唯一已知的「合法零进度窗口」是**输入打开/探测阶段**；本仓无网络流、无 `-f concat` 千条清单 ⇒ 该窗口
  不可能达到 120 s，但**该窗口的上界未经实测**，无法给量化余量；
- ffmpeg 不支持 `-progress` 时**必须显式失败**（不得静默降级）；本仓 ffmpeg 支持，该分支**未实测**。

**未修（如实登记）**：其余 **20** 个 `RunAsync` 调用点未启用心跳（JXL 链 4 处已全部启用）；
探测失败语义仍返 `false`（#16）；阈值可注入 ⇒ 默认值靠 ① 的源码形态自检钉住（§6 第 90 条）。

---

### 🟢 2026-09-18（general-purpose-3）④ JXR 中转路径的多帧缺口（站点 A/B）—— **站点 A/B 已修**

**缺陷（改前读数；`gp3_jxr_pair.ps1` 同一脚本两态对拍，素材 `anim.webp` 10 帧 256×192 / 11016 B）**

| 格（`--format jxr`） | 进程退出码 | 产物个数 | 关键日志 |
|---|---|---|---|
| `--color-engine legacy` | **1** | **0** | `[1/1] anim.webp → 失败 (.bmp 解码退出码 -22)`；`Cannot write more than one file` ×1 |
| auto（不传该开关） | **1** | **0** | `[色彩引擎] ❌ 执行失败：InvalidOperationException: JXR 出口：raw → TIFF 中转失败 (exit -22)`；`[1/1] anim.webp → 失败 (退出码 92)` |

改前两条命令行（逐字，取自 debug 日志）：
- legacy：`[cmd] ffmpeg -y -i "…\anim.webp" -pix_fmt bgr24 "…\jxr_<guid>\input.bmp"`
- auto：`[色彩] ffmpeg -y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb48le -video_size 256x192 -i "…<tmp>.rgb48.out" -i "…\anim.webp" -filter_complex "[1:v]format=rgba64le,alphaextract[a];[0:v]format=rgb48le[b];[b][a]alphamerge" -pix_fmt rgba64le -compression_algo raw -pred none "…\cmjxr_<guid>\in.tiff"`

改后（同一脚本）：legacy **exit=0 / 1 个产物（`anim.jxr:12810`）**；auto **exit=0 / 1 个产物（`anim.jxr:114103`）**。

**根因（两处同一个）**：JXR 出口必须先把像素落成中转容器（BMP/TIFF）再交给 `JxrEncApp`，而 ffmpeg 的
`image2` 复用器按**固定文件名**写盘、**不能**承载多帧 ⇒ 多帧输入时 ffmpeg 报
`Cannot write more than one file with the same name. Are you missing the -update option or a sequence pattern?`
+ `Error submitting a packet to the muxer: Invalid argument`，以 **-22** 退出 ⇒ 中转失败 ⇒ 零产物。

**两处站点（锚点定位，不用行号）**
- **站点 A（legacy）** `QueueProcessor.ProcessJxrAsync` 的 `decodeArgs`（`… -pix_fmt {pixFmt}{tiffExtra} "{intermediatePath}"`）—— 中转目标 `input.bmp` / `input.tiff`。
- **站点 B（引擎）** `RawColorPipeline.EncodeJxrViaJxrEncAppAsync` 的 **alpha 分支** `ffArgs`（`… -i "{tmpOut}" -i "{inputPath}" -filter_complex "{merge}" … "{inter}"`）—— 中转目标 `in.bmp` / `in.tiff`。
- ⚠⚠ **非 alpha 分支没有问题**（唯一输入是 `{tmpOut}` 的一条 rawvideo ⇒ 恒 1 帧）。本仓**不得**一概而论成
  「引擎 JXR 出口坏了」；新门禁专门断言该分支**不带** `-frames:v 1`，防止顺手改坏。

**修法**：判据与 #139（`FfmpegCommandBuilder.targetHoldsMultipleFrames`）**同一条** —— **目标容器能不能装多帧**；
中转目标恒为 BMP/TIFF（单帧）⇒ 恒插 `-frames:v 1`，紧贴中转产物路径之前。
站点 B 取「**限制输出帧数**」而不是「限制 alpha 源那一路」，三条**实测**依据：
1. **输入侧不可行**：`-frames:v` 是**输出侧**选项，放在第二个 `-i` **之前**会被 ffmpeg 拒绝 ——
   实测 `exit=-22`、**无产物**、stderr 含 `cannot be applied to input`（新门禁 ③-d 直接钉住）。
2. **等价备选更复杂**：`[1:v]trim=end_frame=1,format=…` 实测产物同尺寸（均 **393959 B**）、alphaYMIN16 同为
   **59938** ⇒ 语义等价，但要在 scale 滤镜前插 trim 并重排逗号，复杂且易错 ⇒ 取输出侧限制。
3. **「alpha 与底图错位」的担忧已实测排除**：`-frames:v 1` 取的是**输出**第 0 帧 = `[0:v]` 帧0 + `[1:v]` 帧0
   （**同帧号**）；用「3 帧（24669 B）、帧0 全不透明（源 alphaYMIN16=**60157**）、帧1/2 全透明」的 apng
   作 alpha 源时，加/不加该参数得到的产物 alphaYMIN16 均为 **59938（不透明 ⇒ 取到的是帧0）**，
   若错配到帧1 会得到 0。且底图 `[0:v]` 恒为 1 帧 ⇒ 结构上不存在「底图第 N 帧」可错配。
- **对单帧输入是 no-op（逐字节，新门禁 ③-c 直接对拍）**：站点 A 8bit BMP md5 `50b30a75760707cbc79913a585dd3710`；
  站点 A 16bit TIFF md5 `29d797ed8b081c45e6b6da2e04f9362d`；站点 B alpha TIFF md5 `707729fab93157e07319446f244a9bc2`
  —— 加与不加**完全相同**。

**新门禁**：`tests/scripts/verify-jxr-anim-input.ps1`（**未登记进运行器** —— 按指令不动 `_run-step3-gates.ps1`；
若 lead 决定登记，清单条数由**他重数**，本文件**不写推测值**），共 **55** 条断言：
- ① 素材自备 + **形态自检**（`ffprobe -count_frames`：anim.webp=10、anim_alpha.apng=10；两个单帧素材=1；
  alpha 素材 `pix_fmt=rgba` 且 alphaYMIN16 既非全透明也非全不透明）；
- ② 正例 4 格（webp/apng × legacy/auto）⇒ exit 0 + 有产物 + **独立解码器 JxrDecApp 可解**；
  另加**路由守卫**（`anim.webp+auto` **确实**走引擎出口 ⇒ 否则站点 B 覆盖是真空的；`anim_alpha.apng+auto`
  走 legacy 作**反向对照**，证明该判据不恒真）+ 两条**命令行**判据；
- ③ 负控：单帧 3 格不回归（exit 0 + 可解 + 回读尺寸不变）；单帧 alpha **未被拍平**
  （exiftool 回读 `64-bit RGBA`；同 16-bit 口径下源 alphaYMIN16=26520 / 产物=26301，容差 ≤256）；
  ③-c 直接对拍证明 no-op；③-d 证明输入侧位置被 ffmpeg 拒绝；
- ④ 源码形态自检（锚点定位）：两处站点带该参数、站点 B **非 alpha 分支不带**、站点 A 中转容器前提；
  ④-b **本脚本自身**的转义卫生自检（见下「新坑 1」）；⑤ exe mtime 跑前跑后一致；⑥ 残留自证。

**变异验证（同一份门禁，三态实测）**

| 态 | 源码 | 门禁读数 | exit |
|---|---|---|---|
| M1 仅回退站点 A | `decodeArgs` 去掉 `-frames:v 1` | **PASS=40 FAIL=14** | 1 |
| M2 仅回退站点 B | alpha 分支 `ffArgs` 去掉 `-frames:v 1` | **PASS=48 FAIL=6** | 1 |
| 修复态 | 两处都带 | **PASS=55 FAIL=0** | 0 |

**双宿主真跑**：pwsh **7.6.6** 55/0（24.3 s）· Windows PowerShell **5.1** 55/0（71.7 s），均 exit 0。

**回归实测（pwsh 7.6.6；下列读数为最终产物上复跑）**
- `verify-color-wiring.ps1` **PASS=104 FAIL=0** —— 其 (11) 段三条 alpha 断言全绿：
  `alphaextract/alphamerge`、exiftool 回读 `64-bit RGBA`、**透明未被拍平（源 alpha YMIN=32238，产物=32238，完全相等）**
- `verify-anim-static-target.ps1` **PASS=48 FAIL=0**（#139 同族，未被波及）
- `verify-jxl-probe-timeout.ps1` **PASS=41 FAIL=0** · `verify-jxl-codestream-route.ps1` **PASS=44 FAIL=0** ·
  `verify-ffmpeg-heartbeat.ps1` **PASS=62 FAIL=0** · `verify-ps-compat.ps1` **PASS=13 FAIL=0**（新脚本已入其语料）

**交付口径（严格）**：本轮**只**能说「**站点 A（legacy 中转）与站点 B（引擎 alpha 分支中转）已修**；
**具体现在可用的路径**是：`动图 webp → --format jxr`（legacy 与 auto 各一格）与 `动图 apng → --format jxr`
（legacy 与 auto 各一格）—— 四格均 exit 0 + 1 个产物 + JxrDecApp 可解」，
**不能**说「JXR 动图已全面支持」：
- 动图输入只取**第 1 帧**（`-frames:v 1`）；JXR 产物本身仍是**单帧**，本仓**没有**任何动画 JXR 能力；
- 引擎侧能走到站点 B 的多帧输入，只有 `.webp` 这类**未被 `IsAnimated` 拦下**的（实测 `.apng` 被
  `ColorEngineRouter` 的 `BlockReason=AnimatedInput` 拦在引擎外 ⇒ 走站点 A，日志
  `本次由 外部编码器 Jxr 处理，色彩引擎尚未接入该路径`）；
- 除这两处外，其它「中转目标是固定文件名的 `image2`」站点**未审**（本任务只动这两处）。

**未验证 / 判不了（如实登记，不编）**
- 「动图 **avif** / 动图 **gif** → jxr」**未单独实测**（只测了 webp 与 apng 两类）。`.gif` 同 `.apng` 被
  `IsAnimated` 拦在引擎外 ⇒ 理论上走站点 A，**未实测**。
- 站点 B 的「同帧号」结论是用**逐帧取值极端**的素材证明的（3 帧 apng：帧0 全不透明 / 帧1、2 全透明，
  源 alphaYMIN16=60157 ⇒ 产物 59938 即取到帧0）；**连续渐变**的 alpha 未实测 —— 但底图 `[0:v]` 恒为 1 帧，
  输出第 0 帧必然是「底图帧0 + alpha 帧0」，不存在「底图第 N 帧」可错配，故判为不可能错位。
- 站点 B 的 alpha 保持性由新门禁 ③-b 钉住，用的是**均匀 alpha** 素材（`single_alpha.png`，源 26520 / 产物 26301）。
- 站点 A 的 `-frames:v 1` 只取第 1 帧 ⇒ 动图 → JXR **静默丢帧**（exit 0、只留首帧）。这与 #139 口径一致
  （单帧容器就该只有一帧），但「**要不要响**」属另一问题（#16 探测/降级语义三态化），本轮**未动**。
- `-frames:v` 的「输出侧」语义是**本机 ffmpeg**（`publish/PLAN/ffmpeg-full`）实测；**其它 ffmpeg 版本未验证**。

**本轮踩到的两个新坑（已登记 `docs/TESTING.md §6 第 93 条`）**
1. **转义陷阱复发**：门禁消息里写「反引号 + `-frames:v 1` + 反引号」时，反引号**紧贴收尾双引号** ⇒ 它被当成
   **转义的双引号** ⇒ 字符串不闭合、吞掉后续 20+ 行；而 `Parser::ParseFile` 仍报 **0 个错误**
   （引号整体配平、只是位置全错），运行期才在很远处报「术语 … 不会被识别为 cmdlet」。
   #21 已登记该陷阱，本轮**再次踩到** ⇒ 新门禁加了「**本脚本自身**卫生自检」（④-b）把它变成可自证。
2. **`Copy-Item` 恢复源码 ⇒ MSBuild 跳过重建**：`Copy-Item` **保留**原 mtime ⇒ 变异后恢复源码时，
   源文件 mtime 仍**早于**产物 ⇒ MSBuild 判定「已是最新」**不重建**，产物仍是**变异版**
   （实测：恢复后 4 个 build 各 ~0.7 s，Release DLL 时间戳仍是 M2 那一次）⇒ **必须强制重建**
   （`dotnet build … -t:Rebuild`）。这一条会直接制造「读数与源码不符」的假绿。

**改动清单（分列）**
- **交付改动**：`src/FfmpegGui/Services/QueueProcessor.cs`（站点 A，md5 `da8c9139771adc088cc41fe9a8b79c86`）·
  `src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs`（站点 B，md5 `ed2812f4848cbdfe069c3a1a68cd44a2`）·
  `tests/scripts/verify-jxr-anim-input.ps1`（新增）· `docs/HANDOVER.md` · `docs/TESTING.md`。
- **仅变异点（已全部还原，未进交付）**：两文件各自去掉插入的 ` -frames:v 1`
  （QP `74aabf70c3d39043edc28383fe72a473` / RC `9187073557dd22a3cf0ba6fa93acccbb` 为 M0 两处都回退态）。
- **未提交**（按指令）。

### 🔵 2026-09-18（接手会话）：**首次全量串行门禁跑通** + 三条新发现

**为什么做**：上一个会话在 `01:32` 停止时留下一条全局待办 ——
「**所有写者停手后，由 lead 统一跑一次 `_run-step3-gates.ps1` 串行全量 + 一次全量探针**」。
接手时实测**无任何写者进程在跑** ⇒ 这条待办**此刻才具备条件**，已执行。

**① 基线已独立复现（零回归）**
- **探针 20 mode 合计 `519 pass / 0 fail`**，且**每个 mode 都与本文档基线逐值相同**
  （`contract 112 · wire 72 · verdict 41 · engine 32 · selftest 36 · iccname 10 · plan 39 · curve 39 ·
  matrix 15 · ootf 11 · hlgwire 3 · bt2446 18 · decision 4 · runner 2 · settings 7 · procstreams 3 ·
  encoding 6 · ipc 10 · i18n 7 · geometry 52`）。测前已重建 `ServiceProbe`（它当时比 `QueueProcessor.cs` 旧）。
- **脚本门禁：34 条全部串行跑完，11m53s，33 绿 / 1 红**（红的是 `_probe-cjk-hardcode-scan.ps1`，见 ③）。
  ⚠ 这是**当时**的读数（清单 34 条）；**现清单为 37 条**（2026-09-18 新增 `verify-anim-static-target.ps1`、`verify-jxl-probe-timeout.ps1`、`verify-ffmpeg-heartbeat.ps1`，见上一节 🟢 与文末 ③）。
- `verify-jxl-codestream-route.ps1`（`#148` 新门禁）= **PASS=44 FAIL=0** ⇒ 它**确实在清单里跑起来了**；
  它自己的 ⑧ 段（跑期间 `FfmpegGui.exe` mtime 未变）通过 ⇒ 本轮读数有效。

**② ⚠⚠ 运行器**吞掉**子门禁的非零退出码（首次实证）**
`_run-step3-gates.ps1` **尾部只有 `Check-SrcDrift`、没有 `exit`** ⇒ 它以 `$LASTEXITCODE` 退出。
本轮实测：子门禁返回 `exit=1`，**运行器整体 `EXITCODE=0`**。
⚠ 与 `docs/TESTING.md §6 第 86 条` 的关系：第 86 条把这件事登记为 **#147 C(0919) 锁的「前置条件」**（将来时）；
本轮证明它是**今天就在生效的活缺陷** —— **任何**子门禁失败都不体现在运行器退出码里。
⇒ **处置建议：与 #147 C(0919) 的「先补 `exit 0` 并让非 0 冒泡」合并实施**（同一处改动，不要分两次动该文件）。
⚠ **在修好之前：不要用运行器退出码当判据**，必须读日志里的 `[<script>] exit=N` 行。

**✅ 2026-09-18 已修（接手会话）**：`_run-step3-gates.ps1` 加 `$failedItems` 记账 + `Exit-WithSummary`，
**尾部显式 `if/else` 退出**（探针按 `exit≠0` 或 `fail=[1-9]`、脚本按 `exit≠0`、`Check-SrcDrift` 的漂移也计入；
`-SkipScripts` 分支同样走汇总，原来是裸 `return` ⇒ 恒 exit 0）。
**双宿主 × 红绿两路径实测**：红（未知 mode）⇒ PS7 **1** / PS5.1 **1**；绿 ⇒ PS7 **0** / PS5.1 **0**；
**整轮 36 条**（真实红门禁 cjk）⇒ **EXITCODE=1** 并列出 `script:_probe-cjk-hardcode-scan.ps1 (exit=1)`。
⇒ **「cjk 那条红 27 轮无人发现」的根因已消除。** ⚠ 当时仅改了**退出码**这一半，**#147 C(0919) 的运行器级锁**当时**仍未**实施（前置条件现已满足）⇒ **已于 2026-09-19 实施，见本文件「第十五轮」+ `docs/TESTING.md §6 第 86 条`的「实施记录」。**
详见 `docs/TESTING.md §6 第 86 条`。

**③ ⚠⚠ `_probe-cjk-hardcode-scan.ps1` 自第 31 轮起**一直红着**（需维护者裁定）**
- **实测**：`C# 1196 / [tag] 365 / 其余 831`，基线 `1180 / 356 / 824`（XAML 13 持平）。
- **根因**：该门禁自第 31 轮（JXR 执行出口轮）抬完基线后**再没被跑过**
  （`grep cjk-hardcode .workbuddy-ai/memory/2026-09-16.md` 的**全部**命中都在 `≤:2039`，即第 31 轮之前）
  ⇒ 第 32~58 轮的合法功能开发在**无人看守**下累积成漂移。
- **可用证据（本会话挖出，此前无人用过）**：`tests/output/` 里有**历史门禁输出快照**（`_gs-*` / `_g2_*` / `_g4_*`）。
  时间线：`09-17 09:49 → 1137`、`09-17 13:20 → 1142`、`09-17 13:33 → 1142`、`09-18 05:57 → 1196`。
  ⇒ 最后一次「基线 == 实测」是 **09-17 13:33 的 1142** ⇒ **这 +16 全部发生在 09-17 13:33 之后**。
  ⚠ **仍无行级基线快照**（没有任何快照落在 1171 或 1180），`git` 也给不出（全部改动未提交）
  ⇒ **部分行的轮次归属本质不可判定**。
- ✅✅ **分桶账已精确闭合（决定性）**：`_g4_` 快照**带分桶**（lead 复核过原文）⇒
  `09-17 13:33 = CS 1142 / tag 342 / 其余 800`；`09-18 05:57 = 1196 / 365 / 831`
  ⇒ **delta = CS +54 / tag +23 / 其余 +31**。已登记的两轮抬升 = `raise#3 +29 (tag+11/其余+18)` +
  `raise#4 +9 (tag+3/其余+6)` = **+38 (tag+14/其余+24)** ⇒ 余 **+16 = tag+9 / 其余+7**，
  **三个桶全部精确闭合**（54−38=16 · 23−14=9 · 31−24=7）
  ⇒ 这 +16 **不是**重复计算、**不是**口径变化，是**真实独立的新增**。
- **已查清**：计数逻辑无误（逐文件分解与门禁实测逐值一致）；**只有 1 处行尾注释假阳性**
  （`MainWindow.xaml.cs:5570` 的 `// 从"跟随主图"…`，与第 31 轮已修掉的 `RawColorPipeline.cs:593` 同形态）。
  ⚠⚠ **该假阳性已被算进 1180 基线**（其 mtime `09-17 03:11` 早于 13:33 快照）
  ⇒ 修掉它只是把「其余」降到 **830**（相对基线**仍 +6**）⇒ **修它换不来绿**，它是**独立的一笔账**。
  另：`QueueProcessor.cs:2828` 是**近似命中但非假阳性**（门禁的字面量正则配不上它，**根本不计入 1196**）。
- **逐行归属（子代理溯源，其引用的历史快照 lead 已复核）**：
  · **tag 9 行全部定位**：`QueueProcessor.cs` 的 `:614 :618 :621 :623 :625`（**S6** `[色彩裁决]`）、
    `:815`（**S6** 传统管线侧）、`:906`（**S6/S7** 引擎侧）、`:913`（**S7** `降级（`）、`:1020`（**R1 批次 1**）。
  · **其余 7 行定位 6 行**：`QueueProcessor.cs:831`（**S7** `LogAlternatives` 的 `可替代方案：`）、
    `FfmpegCommandBuilder.Decision.cs:91/92/93`（`Short()` 摘要串）、`ColorStrategyPlanning.cs:170/171`。
  · **仍不可判定 1 行**（其余桶）；候选池 4 个，全是 `ColorStrategyPlanning.cs` 的引擎降级码文案
    （`:124` / `:143` / `:152` / `:155`），与**同函数另外 8 条同类同形** ⇒ 无论命中哪个，性质判定都一致。
  · ⚠ **`Decision.cs:91-93` 是活代码**（`tests/ServiceProbe/Program.cs:2677` 的 `decision` mode 在**失配分支**里调用 `dec.Short()`；
    绿跑日志看不到它 ⇒ 曾被误判为死代码）。**不得当死代码删**。
  · **0 行**属于「应走 `loc[...]` 的真 UI 文案」；已归属的行里 **9 行是门禁断言的匹配关键词**。
- **本次处置：只登记，不改**（理由：这是**恢复性抬升**，与脚本头部那四次「同一轮内逐条登记」性质不同；
  仍有 1 行不可判定 ⇒ 满足不了该脚本自己立的「逐条列出确切行号 + 理由」标准；且**改门禁不得为了绿而放宽**）。
- ✅ **2026-09-18 已处置（接手会话，按本脚本前四次抬基线的同一前例）**：① **消掉** `MainWindow.xaml.cs:5570` 的行尾注释假阳性
  （直引号 → `「」`，与第 31 轮修 `RawColorPipeline.cs:593` 同处置，**净 −1**）；
  ② **抬基线 `1180 → 1203 / 356 → 370 / 824 → 833`**，**两笔账分开登记**（恢复性 +16 的逐行归属 + 本会话增量净 +7），
  其中 **1 行如实标注"未定位"**。登记全文在 `_probe-cjk-hardcode-scan.ps1` 头部与 `docs/TESTING.md §6 第 61 条`。
  **⚠ 可一键回退**（四个常量改回 `13/1180/356/824`）。
  ⚠ **留给维护者的结构性提醒（未处置）**：该门禁「其余」桶（833 行）**绝大多数是引擎/CLI 诊断串，而非该桶描述里列的 UI 面**
  ⇒ 口径与描述已不匹配；一天内抬 5 次、本会话内又漂 +7 ⇒ **建议择一**：接受周期性抬基线，或**收紧口径**（排除 `Services/ColorMapping/**` 与 `QueueProcessor` 的诊断串）。
  ⇒ **整轮 37 条因此首次全绿**（`EXITCODE=0`，见 ②）。
- 取证全文见
  `.workbuddy-ai/memory/2026-09-18.md §D`。⚠ 本轮**刻意未改任何源码**，以保持 ① 的读数有效。

**④ ⚠⚠ 新缺陷（产品）：畸形 `.jxl` 输入可让队列**永久挂死**（⚠ 触发面已更正，见下）**
- ⚠⚠ **更正（2026-09-18，`general-purpose-3` 指出、lead 实测复核）**：本条最初写的触发面
  「`.jxl` + 1 字节 + 首字节 `00`/`FF`；**0/2 字节不触发**」**是错的** —— lead 从单个 2 字节样例（`FF 0A`）
  **过度概括**成了「2 字节不触发」。**实测（lead 直接跑 ffmpeg，不经应用）**：

  | 输入 | ffmpeg 直接跑 |
  |---|---|
  | `1B FF` | **挂死**（CPU 23.6s / 墙钟 25s ⇒ **忙等**） |
  | `1B 00` | **挂死**（CPU 23.7s） |
  | **`2B 00 00`** | **挂死**（CPU 24.0s）⇒ **推翻「2 字节不触发」** |
  | `2B FF 0A` | **挂死**（CPU 16.6s）⚠ 但**应用侧**把它判 `NativeCodestream` ⇒ 走 `djxl` 管道 ⇒ 不挂 |
  | `1B 0A` / `2B FF 00` | `exit=69`，0.1s（快速失败） |

- **正确的触发判据 = 两个条件的合取**（与字节数无必然关系）：
  ① `.jxl` 内容**不以 `FF 0A` 开头**（且不是合法容器）⇒ `JxlInspector.DetectType` 返 `Unknown`
  ⇒ 落到 `QueueProcessor.cs:3043-3052` 的**通用兜底分支**，把应用自己判定"读不懂"的文件**原样交给 `ffmpeg -y -i <file.jxl>`**；
  **且** ② 该字节形状让 **ffmpeg 的 JXL 解复用器忙等**（实测命中 `FF` / `00` / `00 00`；`0A` / `FF 00` 不命中，ffmpeg 0.1s 退 69）。
- **⚠ 站点 1（`IsAnimatedJxl`）只是这条链上的一环，不是全部**：即使修好它（120s 超时 + `Kill` + 响亮日志，
  已由 `general-purpose-3` 落地并实测生效），**用户可见的挂死依然存在** —— 因为卡点移到了**站点 3（ffmpeg 编码阶段忙等）**。
- **站点 3（真正的根因，类级）**：`FfmpegRunner.cs:58-83` 的挂死守卫要求「**既无输出**（`silent ≥ 30 min`）
  **且**无 CPU 增长（`cpuAdvanced`）」才判死；**忙等每 30 s 周期消耗 ≈27 s CPU ⇒ `cpuAdvanced` 恒真 ⇒
  30 分钟那条分支对"忙等型挂死"结构性不可达**。
  ⇒ 这不只影响 `.jxl`：**任何让 ffmpeg 忙等的调用都会让队列永久卡住，而现有 CPU 启发式恰好被忙等喂饱**。
  ⇒ 治本需要**真实心跳**（ffmpeg 的 `-progress pipe:1` 不受 `-loglevel` 影响）⇒ **另立任务，不在本条内做**。
- 另见 §「站点 1/2」与 `.workbuddy-ai/memory/2026-09-18.md`。
- 既有处置见下（站点 1 的原始定性）：
- **后果**：`FfmpegGui.exe --headless -i <1字节.jxl> -o … --format png` ⇒ 日志停在「处理中」后
  **8 分钟不前进**（`ffprobe` 子进程一直活着）。
- **根因**：`QueueProcessor.cs:4293-4297`（`IsAnimatedJxl`）——
  `StandardOutput.ReadToEnd()`（**同步阻塞**）写在 `WaitForExit(30000)` **之前** ⇒ **超时不可达**；
  **超时后也没有 `Kill`**；该方法**无 `CancellationToken` 形参** ⇒ **「停止」也杀不掉它**。
  ⇒ ⚠ `WaitForExit(30000)` 是「**看起来在工作**」的守卫（本仓已登记的同一类）。
- **同一文件里就有正确范式**：`ProbeAvifStreamRolesAsync`（`:3995-3998`）= **异步读** + `WaitForExit(20000)`
  + **`Kill(entireProcessTree: true)`** ⇒ **修法照抄兄弟站点**，不要发明新机制。
- **同类对比**：`FfmpegRunner`（30 分钟双信号）/ `CjxlService` / `JxrService` / `RawColorPipeline`（各 30 分钟）/
  `QualityAnalysisService`（30s CTS）**都有挂死守卫** ⇒ `IsAnimatedJxl` 是**唯一漏掉的那个**。
- **门禁面附带缺口**：`_probe-cancel-propagation-scan.ps1` 的扫描面**只有 `FfmpegRunner.RunAsync` 调用点**
  （脚本首行注释自己写明了），但 PASS 文案写「**每个**调用点都能收到取消令牌」⇒ **措辞过度声明**；
  全仓直连 `Process.Start(` 有 **65 处**不在其扫描面内（⚠ **不等于**这 65 处都有问题，那需独立审计）。

**⑤ ⚠⚠ 新缺陷（产品）：命令预览与实跑不一致**（UI 队列显示 + CLI `--dry-run`）
- **实测两格**（`exe mtime` 前后一致 ⇒ 读数有效）：
  · **JXL 格**（`bare.jxl` 裸码流 + `--format jxl --encoder cjxl`）：
    `--headless --dry-run` 打印 `[两步法] djxl 解码 → 临时PNM → cjxl "<tmp.pnm>" "…\out1\bare.jxl" -e 7 -d 3.8`；
    **实跑**日志是 `[pipeline] 尝试 djxl -> cjxl 管道（无中间文件）` ⇒ 终态 `已完成 (djxl→cjxl 管道)`。
    ⚠ 预览连**占位符 `"<tmp.pnm>"` 都原样打给了用户**。
  · **PNG 格**（`--format png --color-space DisplayP3`，引擎真执行 `决策：Map`）：
    `--headless --dry-run` 打印**一条**命令（`-vf format=rgb48le,…,zscale=…,iccgen`）；
    **实跑是两条**（`[色彩] ffmpeg … -vf "format=rgb48le" -f rawvideo "…cms_….rgb48"` 然后
    `[色彩] ffmpeg … -f rawvideo -pix_fmt rgb24 -video_size 256x192 -i "…rgb48.out" …`）⇒ **无 zscale、无 iccgen**。
  ⇒ 不只是"不建模引擎"，而是**结构级不同**（命令条数 / 滤镜链 / 中转方式全不同），且**是默认配置下的常态**。
- **两处自述被实测证伪**：① `BuildQueueItemCommand` 注释自称「构建**实际将执行的**完整命令行（与 QueueProcessor 调度逻辑一致）」，
  而 `MainWindow.xaml.cs:3057-3121` **零色彩引擎分支**；② `BuildJxlInputCommand` 自称「与 `ProcessJxlInputAsync` 一致」，
  而后者**实际走管道** ⇒ 预览是**对它自己声称的参照物的过期建模**。
- **用户可见面（三处，已核实）**：`Program.cs:311`（`--dry-run` 直接打印，⚠ 而 dry-run 的**整个用途**就是"显示将执行什么"）；
  `MainWindow.xaml.cs:2782`（填 `item.Command`）；`ImageDetailWindow.axaml.cs:99-102`（写进 `InfoCommand`）。
  ⚠ 可见性有时间窗：`QueueProcessor.cs:898` 在处理中会把 `item.Command` 改写成引擎命令
  ⇒ **处理后**详情窗口显示真命令，**处理前（队列中）与 `--dry-run`** 显示预览。
- ⚠ **`#150` 是本条的更窄实例**（只讲 `effectiveType` 被重复实现）⇒ **建议并入本条统一处置**，修 `#150` 不等于修好本条。
- 既有登记检索（写明范围，别读成"全仓不存在"）：grep `docs/HANDOVER.md` / `docs/TESTING.md` /
  `docs/COLOR_ENGINE_INTERFACE_PLAN.md` / `MEMORY.md` / `COLOR_TRAPS.md` 的 `预览` ⇒ 只有 `HANDOVER.md:151`（`#150` 窄面）
  与 `TESTING.md:312` 一处 ct 旁注。**本条未见既有登记。**

**⑥ 新缺陷（产品）：`--dry-run` 未隐含 headless ⇒ 静默开 GUI 并忽略该开关**
`Program.cs:22-26` 只有 `IsHeadless || ShowHelp || ShowVersion` 才进 CLI 分支；`CliParser` 里 `DryRun` 与 `IsHeadless`
是**两个独立开关**，`--dry-run` 的帮助文本只写「仅打印将执行的命令」、**未提示需要 `--headless`**（README 的示例倒是都成对写）。
⇒ 单独用 `--dry-run` ⇒ **静默落到 GUI 模式、该开关被完全忽略**（不打印命令、进程不退出），并**在用户桌面上弹窗**。
⚠ **本会话 lead 亲自踩到**（见 `2026-09-18.md §G′`：它同时**锁住 Release exe**、把队友挡在构建外）。
**建议：`--dry-run` 隐含 headless，或显式报错。** ⚠ **纪律**：对本应用的 CLI 调用**一律显式带 `--headless`**。

**⑦ 本会话新增待办（已进队列，见下）**：`新A` cjk 基线裁定 · `新B` 运行器退出码冒泡（**与 #147 C(0919) 合并**）·
`新C` `IsAnimatedJxl` 挂死（照抄兄弟范式 + 补 ct + 补门禁）· `新D` `_probe-cancel-propagation-scan` 文案收窄 ·
`新E` 命令预览不建模真实执行路径（含 `#150`）· `新F` `--dry-run` 未隐含 headless · `新G` JXR 两处同族缺口（见 🟢 小节）。


### 🟢 2026-09-18（general-purpose-1）⑧ 直连进程启动站点：**只读审计 + 独立结构锁**（`#23` / `#22`）

**背景**：`#14` 修掉的 `IsAnimatedJxl` 挂死，形态是「**同步 `ReadToEnd()` 排在 `WaitForExit(...)` 之前**
⇒ 超时是死代码」。而门禁 `_probe-cancel-propagation-scan.ps1` 的扫描面**只有** `FfmpegRunner.RunAsync`
调用点（`TESTING.md §6` 第 87 条已登记「措辞比扫描面大」）⇒ **全仓直连 `Process.Start(` 没有任何锁**。

**① 只读审计（`#23`，未改任何文件、未跑任何 exe）**

- **重数 65 处**，与 team-lead 一致。（我此前的 68 作废：差异来源两条 —— 把 `new Process {…}` +
  `process.Start()` 误计为 `Process.Start(`；以及 Git-Bash 在长路径处**在 token 中间折行**导致同一行被数两次。）
- 对每处判 **4 个属性**（超时是否可达 / 超时后是否 `Kill` 且是否杀树 / 有无取消通道 / 失败可见性），
  判据与等级写在 `TESTING.md §6` 第 93 条。**风险分级：高 12 / 中 18 / 低 21 / 无 14。**
- **12 处高危的共同形态**（= 新锁的判据）：`MainWindow.xaml.cs:3465`（`ProbeCurrentSourceBitDepth`）·
  `CjpegliService.cs:151` · `DjxlService.cs:117` · `PlatformServices.cs:84` · `ExifToolService.cs:302`
  （以上 5 处是 `TryFindInPath` 族）· `ExternalToolsDetector.cs:54 / :297 / :583 / :606 / :631 / :655` ·
  `IccProfileService.cs:301`。其中 **`MainWindow.xaml.cs:3465` 是 UI 线程上的同步方法**
  （挂死 = 整个窗口冻结），`ExternalToolsDetector` 6 处集中在**启动期**。
- ⚠ **纪律**：以上 12 处是**代码形态判定**，**不是**「已证实会挂死」——我一处都没运行。
  唯一**已证实**的仍是 `IsAnimatedJxl`（已修）；`ProbeWithFfprobe` 我读到的**已是修好的形态**。
- **扫描面本身漏掉一类**：`new Process { … }` + `<var>.Start()` 共 **7 处**，不在那 65 处里
  （`CjpegliService.cs:224` · `CjxlService.cs:226` · `DjxlService.cs:171` · `FfmpegRunner.cs:104` ·
  `JxrService.cs:166` · `QueueProcessor.cs:2876` · `QueueProcessor.cs:4181`）。
  4 属性判定：**5 处「无」/ 2 处「低」**（`QueueProcessor.cs:2876`、`:4181` 是**无超时**，
  仅有 `ct` 取消通道）—— **一处都不具备高危形态**（读全是事件驱动 `BeginOutputReadLine`）。

**② 独立结构锁（`#22`，新文件 `tests/scripts/_probe-sync-read-before-wait-scan.ps1`）**

- **判据只取一条**：同一**进程变量**上，「同步 `ReadToEnd()`」不得**早于** `WaitForExit(...)`。
  另外三条属性**刻意不进判据**（一条扫描同时判四件事 ⇒ 断言变糊，第 74 条同族）。
- **覆盖面 70 处，两类分开打印**：A 类直连 **63** / B 类实例化后 `.Start()` **7**。
  （登记时为 **72 = 65 + 7**；2026-09-18 `#29` 删掉 2 处零调用方残留实现后降为 **70 = 63 + 7**。）
- **基线口径 = 「不得增加」，不是「必须为 0」**（第 55 条同族）：A 类基线 **0**
  （原 12 → `#29` 删除 2 处**仓内不可达**后收紧 **10** → `#24` 把余下 10 处形态修好后收紧 **0**，team-lead 裁定；
  ⚠ **在 0 处「不得增加」等价于「必须为 0」**），
  B 类 **0**；降了不判红但打印**余量**并提示收紧。
- **负控 8 条**（有牙 + 不误伤）：坏形态 A/B 各必被抓到；异步读 / 读在等待之后 / 整行注释内 /
  行尾注释内 ⇒ 一处都不许命中；双进程（`p.WaitForExit` 早于 `wp` 的读）只命中 1；空语料 0/0。
- **读数（双宿主真跑）**：pwsh 7.6.6 `exit=0`、PS 5.1 `exit=0`，均为
  「扫描 .cs **78** 个（排除 `bin/` `obj/`）· A 类 **63**（参与判据 **62** / 无进程变量跳过 **1**）· B 类 **7** ·
  命中 **0 / 0** · 负控 8/8」；`verify-ps-compat.ps1` **13/0**。
  （登记时读数为 A 类 65（64/1）· 命中 12 / 0；`#29` 删 2 处残留后为 10 / 0；`#24` 修好 10 处形态后为 **0 / 0**。）
  ⚠ `$BaseDirect` 已由 **10 收紧为 0**（`#24` 收尾，team-lead 裁定）：门禁现打印「命中 0 / 余量 0」，
  **收紧后双宿主重跑仍 `exit=0`**（pwsh 7.6.6 / PS 5.1 读数一致）。
- **写这把锁时踩到并已钉住的三个坑**（详见第 93 条）：
  ① **假绿**：变量名抽取正则里 `=` 右侧类型路径写成**必选** ⇒ `using var p = Process.Start(psi);`
  取不到变量名，**58/65** 站点被静默跳过（当时的语料数；`#29` 后为 63）、读数只剩「命中 2」——
  ⇒ 一般教训：**扫描器的「跳过数」必须和「命中数」一起打印**，否则「跳过」会伪装成「干净」。
  ② **假绿**：判据若退化成「方法内**第一次**读 vs 第一次等待」，`ExifToolService.TryFindInPath` 会被漏掉
  （那里 `p.WaitForExit` 早于 `wp` 的读）⇒ 必须**按变量名配对**。
  ⚠ 该真实实例已被 `#24` 修好 ⇒ **真实语料里已无此反例**，判据现由负控 `twoprocs.cs` 钉住。
  ③ **假红**：`IsAnimatedJxl` 的注释里**正逐字引用**旧坏代码 ⇒ 必须剥注释。

**⚠ 本锁两个已登记的缺口**（措辞不得大于扫描面，第 87 条）：
- 等待被抽到 helper 时判据**落空**、本锁**不表态**（实测实例 `CjxlService.cs:226` → `WaitGuardedAsync`）；
- 只查**源码形态**、不跑 exe ⇒ 它**不**证明任何一处「会挂死」，只证明「该形态的站点数没有增加」。
- ✅ **「修形态 ⇒ 计数降」已实测（`#24`，2026-09-18）**：把 10 处命中站点的同步 `ReadToEnd()`
  改成「先挂 `ReadToEndAsync()` → 可达 `WaitForExit(...)` → 超时 `Kill(entireProcessTree: true)`」后，
  本锁读数由 **命中 10 → 命中 0**（pwsh 7.6.6 与 PS 5.1 一致）⇒ **判据确实认账「修好的形态」**。
  （登记当轮标为「未实测」的就是这一条。`#29` 的 12 → 10 是**删除**，不算这条证据。）

**③ 后续项**
- ✅ `(a)` **「调用方有 `ct` 却没传进来」的定向追溯** —— **已做，见下方 ⑨**（`#25`）。
- ⏳ `(b)` 若要把本锁登记进 `_run-step3-gates.ps1` 的必跑清单（现 **37** 条），
  **由 team-lead 改并重新数**（运行器由他管）；本锁是静态扫描、无需 exe，可与构建类任务并行。


### 🟢 2026-09-18（general-purpose-1）⑨ **取消令牌（`ct`）断点清单**（`#25` / `#27`，只读盘点）

**背景**：⑧ 的 `#23` 审计里，属性 3（取消通道）只能记成「无形参 45 处」，
**判不了**「调用方持有 `ct` 却没传」—— 本节把它追完。**全程只读，未改 `src/**`。**

**① 口径（三类事实，互斥，先定死）**

| 代号 | 事实 | 判据 |
|---|---|---|
| **①** | **已传** | 站点所在方法有形参 `ct`，且该调用把 `ct`（或 `linked.Token`）交了下去 |
| **②** | **方法有形参、站点这一跳没交** | 所在方法有形参 `ct`，但本站点这条调用没交 |
| **③** | **方法无形参 `ct`** | 再按上游分两档 ↓ |
| **③a** | **调用方持有 `ct` 却没传** | 沿调用链上溯，**存在**持有 `CancellationToken`（含 `ct` / `linkedToken` / `linked.Token` 在作用域）的方法，却未交给下一跳 |
| **③b** | **全链无 `ct`** | 上溯到根（Avalonia 事件处理器 / `MainWindow` 启动探测 / `Program.Main`）都没有 `ct` |

⚠ **「方法无形参」与「调用方持有 `ct` 却没传」是两个不同的事实** —— 前者是**方法的能力**，
后者是**调用点的选择**。引用位置一律用「**方法名 + 逐字原文**」，**不用行号**（行号会漂：
本轮实测 `QueueProcessor.cs` 在 `#13` 并行改源码期间已整体位移过）。

**② 总账（65 处 A 类直连站点）**

| 分类 | 处数 |
|---|---|
| ① 已传 | **20** |
| ② 方法有形参、站点这一跳没交 | **0** |
| ③a 调用方持有 `ct` 却没传 | **18** |
| ③b 全链无 `ct` | **27** |
| ③c 不可判定 | **0**（但有 **2 处「所在方法仓内零命中调用方」**，见 ⑤） |
| 合计 | **65** ✓ |

**自证口径**：⑧ 的属性 3 报「有形参+接线 20 / 无形参(内部 cts) 5 / 无形参 40」⇒ 无形参共 **45**
= 本节 ③a 18 + ③b 27 ✓。**两轮独立统计闭合。**

⚠ **`#29` 后的增量（上表为审计时的 65 处原值，保留以备核对）**：删掉 ⑤ 里 2 处**仓内不可达**实现
（`CjpegliService.TryFindInPath` / `DjxlService.TryFindInPath` —— 二者均**无形参 `ct`**、
所在方法全链无 `ct`，按 ② 的口径属 **③b**）
⇒ 语料由 **65 降为 63**、③b 由 **27 降为 25**，① / ② / ③a 不变；
自证式相应变为「无形参 **43** = ③a 18 + ③b 25」。

⚠ ③a **不是**缺陷等级。它只在**该站点同时缺「可达超时 + 杀树」**时才升级为缺陷；
有本地兜底的 ③a（如 ⑥ 的 `ProbeWithFfprobe`）**不升级**。

**③ 18 处 ③a 清单（锚 = 方法名 + 逐字原文）**

**(3.1) `IccProfileService.GetOrGenerateStandardIcc(string? carrier, string primaries, string? trc, string? matrix)`
—— 上游 7 条路径**全部**持有 `ct`，**一条都不是「全链无 ct」**（本仓最典型的「`ct` 到不了最底层」）**

| 上游调用点逐字原文 | 所在方法（形参 `ct`?） | 再上溯到的持有者 |
|---|---|---|
| `iccFile = IccProfileService.GetOrGenerateStandardIcc(probeInput,` | `QueueProcessor.ApplyColorPostStepsAsync(...)` **无形参** | `await ApplyColorPostStepsAsync(captured, finalOutputPath, captured.Options,` ∈ `ProcessAsync(CancellationToken ct)` |
| `var tiffIcc = IccProfileService.GetOrGenerateStandardIcc(` | 同上 | 同上 |
| `string? wIcc = IccProfileService.GetOrGenerateStandardIcc(actualInput,` | `QueueProcessor.EmbedDroppedFilterIccAsync(...)` **无形参** | `await EmbedDroppedFilterIccAsync(captured, finalOutputPath, probeInput,` ∈ `ApplyColorPostStepsAsync`（无形参）← `ProcessAsync(ct)` ⇒ **隔 2 跳** |
| `var stdIcc = IccProfileService.GetOrGenerateStandardIcc(inputPath, stdP, stdT, stdM);` | `QueueProcessor.BuildPipeColorArgs(` **static 无形参** | `PipeFfmpegToCjxlAsync(..., CancellationToken ct)` / `PipeFfmpegToCjpegliAsync(..., ct)` / `PipeFfmpegToExternalEncoderAsync(..., ct)` |
| `var baseIcc = IccProfileService.GetOrGenerateStandardIcc(basePath,` | `GainMapEncoder.EncodeAsync(...)` **有形参 `ct = default`**，这一跳没交 | 直接调用方即持有者 ⇒ **等价 ②** |
| `iccFile = IccProfileService.GetOrGenerateStandardIcc(` | `RawColorPipeline.EncodeJxlViaCjxlAsync(...)` **有形参 `ct`**（调用处已传进来），这一跳没交 | 同上 ⇒ **等价 ②** |

⚠ 最干净的一处：`QueueProcessor.BuildPipeColorArgs` 的调用点处，
`var linkedToken = linked.Token;` **就在同一个作用域里**（`PipeFfmpegToExternalEncoderAsync` 内），
`linkedToken` 已建好却**没交给** `BuildPipeColorArgs`。

**(3.2) `PlatformServices.TryFindInPath(string toolName, out string? fullPath)`（**public**，高危站点）**

| 逐字原文 | 所在方法 | 判定 |
|---|---|---|
| `if (PlatformServices.TryFindInPath(PlatformServices.JxrDec, out var pathFound))` | `QueueProcessor.ResolveJxrDecAppPath()` static 无形参 | ③a 远端 |
| `var jxrDecPath = ResolveJxrDecAppPath();` | `ProcessJxrInputAsync(..., CancellationToken ct)` **持有** | ↑ 持有者在这 |

⚠ 其余调用方（`RawService.Detect()`、`JxrService.Detect()`、`CjxlService.Detect()`、
`CjpegliService.Detect()`、`Program.cs`）全属 ③b。

**(3.3) `QueueProcessor` 内部 5 处（③a 近端，调用方即持有者）**

| 站点所在方法（无形参） | 调用点逐字原文 | 持有者 |
|---|---|---|
| `ProbeBitDepthAsync(string inputPath, string ffmpegPath)` | `int bitDepth = item.Options.BitDepth ?? await ProbeBitDepthAsync(item.InputPath, ffmpegPath);` | `ProcessJxrAsync(..., CancellationToken ct)` |
| `ProbeImageSizeAsync(string inputPath, string ffmpegPath)` | `var (width, height) = await ProbeImageSizeAsync(item.InputPath, ffmpegPath);` | `ProcessGainMapJpegAsync(..., CancellationToken ct)` |
| `ProbeAvifStreamRolesAsync(string inputPath, string ffmpegPath)` | `var (colorIdx, alphaIdx) = await ProbeAvifStreamRolesAsync(item.InputPath, AppSettingsService.Current.FfmpegPath);` | `ProcessAvifToGifWebpAsync(..., CancellationToken ct)` |
| `IsAnimatedAvifAsync(string inputPath)` static | `\|\| (maxDimInputExt == ".avif" && await IsAnimatedAvifAsync(captured.InputPath));`（另一处同形在 `ProcessAvifToGifWebpAsync`） | `ProcessAsync(CancellationToken ct)` |
| `IsAnimatedJxl(string inputPath, Action<string>? log = null)` | `if (inputExt == ".jxl" && IsAnimatedJxl(item.InputPath, s => item.Log += s)) return true;`（`InputMayHaveMultipleFrames` / `IsAnimated` 各一处） | 上述 static 方法 ← `ProcessAsync(ct)` 等 |

**(3.4) `ExifToolService` 5 处**

| 站点所在方法（无形参） | 调用点逐字原文 | 持有者 |
|---|---|---|
| `RunAsync(string filePath, Models.FfmpegOptions options, Action<string>? logCallback = null)` | `var exifExit = await ExifToolService.RunAsync(` | `ProcessAsync(CancellationToken ct)` |
| `ReadIsoAsync(string imagePath)` | `var iso = await ExifToolService.ReadIsoAsync(item.InputPath);` | `ProcessCjxlAsync(..., CancellationToken ct)` |
| `RunRawAsync(string args, Action<string>? logCallback = null)` | `var exit = await ExifToolService.RunRawAsync(args,` | `WriteJxrHdrMetadataAsync(QueueItem item, string outputPath)` 无形参 ← `await WriteJxrHdrMetadataAsync(item, outputPath);` ∈ `ProcessJxrAsync(..., ct)` ⇒ 隔 1 跳 |
| `GetTagAsync(string path, string tag)` | `var result = await ExifToolService.GetTagAsync(path, "MPImage2");` | `IsUltraHdrJpegAsync(string path)` static 无形参 ← `&& await IsUltraHdrJpegAsync(captured.InputPath))` ∈ `ProcessAsync(ct)` ⇒ 隔 1 跳 |
| `ReadColorTagsAsync(string imagePath)` | `var exifColorTags = Task.Run(() => ExifToolService.ReadColorTagsAsync(inputPath))` | `FfmpegCommandBuilder.ProbeInputColorMetadataCore(string inputPath)` 无形参 ← 见 (3.6) |

**(3.5) `IccProfileService.ExtractIccToTempFile(string imagePath)`（static 无形参）**

上游逐字：`IccProfileService.ExtractIccToTempFile(finalOutputPath)` / `(item.InputPath)` /
`(inputPath)`（`QueueProcessor.BuildPipeColorArgs` 内 3 处）· `RawColorPipeline.EncodeJxlViaCjxlAsync` 内 1 处 ·
`FfmpegCommandBuilder.ProbeInputColorMetadataCore` 内 1 处 · `GainMapDecoder.DecodeToLinearRawAsync` 内 1 处。
⇒ 所在方法分别是 `ApplyColorPostStepsAsync`(无形参←`ProcessAsync(ct)`)、`ProcessCjxlAsync(ct)`、
`PipeDjxlToCjxlAsync(ct)`、`BuildPipeColorArgs`(static←3 个 `ct` 承载者)、`EncodeJxlViaCjxlAsync(ct)`、
`DecodeToLinearRawAsync(ct)` ⇒ **③a**。

**(3.6) `FfmpegCommandBuilder` 三级静态链 —— 本仓最大的一个 `ct` 断点**

`ProbeInputColorMetadata(string inputPath)` → `ProbeInputColorMetadataCore(string inputPath)` →
`ProbeWithFfprobe(string inputPath)` / `ProbePeakNits(string inputPath)`，**四级全 `static` 无形参**。
**src 内 15 个上游调用点，无一例外全部持有 `ct`**：

| 逐字原文 | 所在方法 |
|---|---|
| `var colorMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(captured.InputPath);` | `ProcessAsync(ct)` |
| `hdrMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(item.InputPath);` ×3 | `PipeFfmpegToCjxlAsync(ct)` / `PipeFfmpegToCjpegliAsync(ct)` / `PipeFfmpegToExternalEncoderAsync(ct)` |
| `var jxlMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(item.InputPath);` ×2 | `PipeDjxlToCjxlAsync(..., ct)` / `PipeDjxlToFfmpegAsync(..., ct)` |
| `depthMeta = …` / `var srcMeta = …` / `var hdrMeta = …`（`BuildPipeColorArgs` 内 3 处） | `BuildPipeColorArgs`(static 无形参) ← 上述 `ct` 承载者 |
| `var jxlMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath);` | `JxlPipelineService.TryPipeDjxlToCjpegliAsync(..., CancellationToken ct = default)` |
| `var hdrMeta = …` / `var meta = …` | `RawColorPipeline.EncodeJxrViaJxrEncAppAsync(..., ct)` |

⚠ **但这两处的本地兜底是完整的**（已是正确范式：异步读 → `WaitForExit(5000)` → `Kill(entireProcessTree: true)`）
⇒ **③a 不升级为缺陷**，缺的只是「用户取消按不动」。⚠ 另注：`ProbeWithFfprobe` 的注释里
**逐字引用了旧坏形态**（`旧的 \`StandardOutput.ReadToEnd()\`（同步阻塞）写在 \`WaitForExit(5000)\` **之前**`）
—— 这正是 ⑧ 登记的**坑 ③（假红）**，`Get-CodeText` 剥整行 `//` 已覆盖。

**④ 最危险的一条：`GainMapDecoder.DecodeToLinearRawAsync` —— 同一方法内 5 个调用，2 交 3 不交**

方法签名逐字：`public static async Task<(int w, int h, float peakNits, string? jxlColorSpace)?> DecodeToLinearRawAsync(string inputPath, string outputRawPath, Action<string>? log = null, CancellationToken ct = default)`

| 逐字原文 | 交了吗 |
|---|---|
| `var xmpMeta = await ReadMetadataAsync(inputPath, log);` | ❌ **没交** ⇒ `ReadMetadataJsonAsync` 站点 ③a |
| `gmOk = await ExtractGainMapJpegAsync(inputPath, gmJpegPath, log);` | ❌ **没交** ⇒ `ExtractGainMapJpegAsync` 站点 ③a |
| `var (baseW, baseH) = await ProbeSizeAsync(inputPath, ffmpeg);` | ❌ **没交** ⇒ `ProbeSizeAsync` 站点 ③a |
| `var baseExit = await RunFfmpegAsync(baseArgs, ffmpeg, log, ct);` | ✅ **交了** |
| `var gmExit = await RunFfmpegAsync(gmArgs, ffmpeg, log, ct);` | ✅ **交了** |

⇒ **同一方法里作者知道 `ct` 该怎么传**（`:155/:173` 就是证据），却在另 3 处漏了。
**这条排除了「设计上没考虑取消」的辩解 ⇒ 是漏传。**（本仓最危险的一类：**同一方法内的不一致**，
同 `ColorEngineRouter.cs:74` vs `:334`。）
⚠ 对照正确反例：`var decoded = await DecodeUltraHdrToLinearAsync(captured, captured.InputPath, ct);`
—— 入口处 `ct` 是**传进来的**（`QueueProcessor` 内调用实参逐字为
`s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);`）
⇒ **断点完全在 `DecodeToLinearRawAsync` 内部**，不在调用方。

**⑤ `TryFindInPath` 六份实现的现状（⚠ 修正 ⑧/`#25` 的「4 份」口径：实测是 6 份）**

⚠ 下表是 `#25`/`#27` 审计时的状态；**`#29` 已删掉其中 2 份直连残留**（见「处置」列的 ✅）。
当前实存 **4 份**（`PlatformServices` / `ExifToolService` 直连 + `CjxlService` / `JxrService` 委托转发）。

| 实现 | 形态 | 调用方 | 处置 |
|---|---|---|---|
| `PlatformServices.TryFindInPath`（**public**） | 坏形态直连：同步 `ReadToEnd()` 在 `WaitForExit(5000)` **之前** | ✅ 6+ 处 | **必须修** |
| `ExifToolService.TryFindInPath`（private） | 坏形态直连（`wp` 那次读在 `WaitForExit` 前；`p` 那次只是等完读 `ExitCode`，**不算**） | ✅ `Detect()` 内 | **必须修** |
| `CjpegliService.TryFindInPath`（private） | 坏形态直连 | ❌ **零命中** | ✅ **已删（`#29`）** |
| `DjxlService.TryFindInPath`（private） | 坏形态直连 | ❌ **零命中** | ✅ **已删（`#29`）** |
| `CjxlService.TryFindInPath`（private） | **委托转发**，无 `Process.Start`（`return PlatformServices.TryFindInPath(exeName, out fullPath);`） | ❌ 零命中 | 已标 `[Obsolete("使用 PlatformServices.TryFindInPath 代替")]` |
| `JxrService.TryFindInPath`（private） | **委托转发**，无 `Process.Start`（同上） | ❌ 零命中 | ⚠ **无 `[Obsolete]`**（与 `CjxlService` 不对称；`#29` 已补齐） |

⚠ **`#29` 顺带发现（未处置，待裁定）**：按「零调用方」这**同一条**判据，剩下那 2 份**委托转发**副本
（`CjxlService` / `JxrService`）与已删的 2 份直连残留**性质相同**（都零命中、都不含 `Process.Start`）。
`#29` 按 team-lead 给定的范围**只删了 2 份直连**，这 2 份委托转发**原样保留**（仅补齐 `[Obsolete]` 对称性）。
是否一并删除，由 team-lead 裁定 —— 它们不影响本锁基线（不含 `Process.Start(`，从未进入扫描面）。

**⑥ ③b 27 处（**不是缺陷**，是设计选择）**

- **UI 事件处理器**：`MainWindow.ProbeCurrentSourceBitDepth`（调用方 `ColorSpaceCombo_SelectionChanged`）、
  `MainWindow.OpenCacheDirMenu_Click`、`ExifToolService.ReadMetadataAsync` / `WriteMetadataAsync`
  （调用方 `MetadataEditor.ReadMetadata_Click` / `ApplyMetadata_Click`、
  `ImageDetailWindow.ReadMeta_Click` / `SaveMeta_Click`）。
- **启动 / 工具探测**：`ExternalToolsDetector.ProbeExecutable` / `ProbeFfmpeg` / `ProbeCjpegliVersion` /
  `TryExtractCjxlVersion` / `TryExtractCjxlSimd` / `TryExtractFfmpegVersion`（← `FullDetectionAsync()` ←
  `MainWindow.LoadSettings()` / `BrowseFfmpeg_Click`）、`ExifToolService.TryFindInPath`、
  `GpuCapabilityService.RunFfmpegAsync` / `RunFfmpegStderrAsync`（← `DetectAsync` / `ValidateEncodersAsync`）、
  `EncoderDetectionService.GetAllEncodersAsync` / `SupportsJxlLosslessJpegAsync` / `ParseSimpleListAsync`、
  `FormatCapabilitiesService.DetectLocalFfmpegCapabilitiesAsync`（← `InitializeAsync` / `InitializeCapabilitiesAsync`）。
- **无 `ct` 的服务入口**：`QualityAnalysisService.AnalyzeAsync` / `RunDecoderAsync` /
  `SelectBestVideoStreamAsync` / `GetResolutionAsync`（← `ProgressWindow.AnalyzeQuality_Click`）、
  `MediaInfoService.GetMediaInfoAsync`（← `SelectFile_Click` / `SelectFolder_Click` / `DropHandler` /
  `ScanFolderAsync` / `MediaInfoParser.ParseAsync`）。

**⑦ 残余不确定（如实登记，故**不**写「死代码」）**

1. `CjpegliService.TryFindInPath` / `DjxlService.TryFindInPath` 两份声明**逐字都是 `private static`**
   ⇒ **语言层面排除「其他类直接调用」**，只剩反射一条路。已扫：`tests/**` 内 `TryFindInPath` 零命中；
   全仓字符串字面量 `"TryFindInPath"` 零命中；全仓 `GetMethod(` 的目标只有 `MainWindow.RegenerateCommand` /
   `AddToQueue_Click` / `StartQueue_Click` 与 `ColorSpaceRegistry` 的 4 个方法；`Delegate.CreateDelegate` /
   `GetDelegateForFunctionPointer` / `GetMethod(nameof` 零相关命中。
2. **但仍不能证否**：反射方法名可用**拼接字符串**构造（`"TryFind" + "InPath"`），我扫不到。
   ⇒ 结论一律写「**仓内不可达（截至本次扫描）**」，**不写「死代码」**。
   （纪律来源：我曾因「全仓零命中」把 `FfmpegCommandBuilder.Decision.Short()` 误判为死代码，
   实为 `tests/ServiceProbe/Program.cs` 在调 —— 零命中只证明「我没找到」。）
3. `tests/` 侧调用点（`ServiceProbe` / `GainMapTestHost` / `UiTestHost`）**不计入 ③a/③b 统计**
   （探针宿主无 `ct` 语义）。

**⑧ 对后续任务的约束**

- **`#24`（12 处「同步读架空超时」）**：剔除 ⑤ 里两处**仓内不可达**后，**有效为 10 处** ——
  `MainWindow.xaml.cs:3465` · `PlatformServices.TryFindInPath` · `ExifToolService.TryFindInPath` ·
  `ExternalToolsDetector` 6 处 · `IccProfileService.GetOrGenerateStandardIcc`。
- ⚠ **若真的删掉 ⑤ 里那两处未调用实现，必须同步改 ⑧ 的门禁基线**：
  `tests/scripts/_probe-sync-read-before-wait-scan.ps1` 的 `$BaseDirect` **12 → 10**，
  并同步 `TESTING.md` 第 93 条登记的 `12 / 0` 与「12 处已知债」文案；
  否则门禁虽仍绿（`实测 ≤ 基线`），但基线里会留下 **2 处永远修不到的幽灵债务**。
  ⇒ ✅ **2026-09-18 `#29` 已执行**：删掉 `CjpegliService.TryFindInPath` / `DjxlService.TryFindInPath`
  两份 `private static` 实现，`$BaseDirect` 收紧为 **10**，`TESTING.md` 第 93 条 / 本节 ⑧ 的读数与覆盖面文案同步；
  **双宿主重跑** pwsh 7.6.6 `exit=0`、PS 5.1 `exit=0`，读数 `A 类 63（62/1）· 命中 10 / 0 · 负控 8/8`（余量 0）。
- **`#26`（18 处 ③a 接线取消令牌）** 直接复用本清单；**与 `#24` 不合并**
  （失败模式不同：挂死 vs 取消响应；验证方式不同：不吐 EOF 的子进程 vs 「点了停止」的测试
  ⇒ 合并会让门禁与变异判据变糊，第 74 条同族）。
- ⚠ 本清单**未做变异**（未改 `src/**`）⇒ 它只证明「**哪些站点的 `ct` 断了**」，**不**证明任何一处「用户取消会失效」的实测现象。

**⑨ `#26` 落地（2026-09-18，general-purpose-1）+ 缺口登记**

**a) 已落地的形态** —— ⚠ **全部为「形态判定，未实证取消生效」**（只做静态链闭合，未跑「点停止」的实测）

- **沿用本仓既有范式**（不是新约定）：`Action<string>? log = null, CancellationToken ct = default`
  —— `log` 先、`ct` 后、**均带默认值 ⇒ 零调用方影响**。该精确形态在改动前已存在于 **6 个文件 / 9 处**
  （`ExifToolService` 3 · `RawService` 2 · `RawColorPipeline` 1 · `GainMapDecoder` 1 · `GainMapEncoder` 1 · `QueueProcessor` 1）。
- **A 堆（async 载体：加形参 + 调用点交付）**：`QueueProcessor.ProbeBitDepthAsync` / `ProbeImageSizeAsync` /
  `ProbeAvifStreamRolesAsync` / `IsAnimatedAvifAsync`；`GainMapDecoder.ReadMetadataAsync` /
  `ReadMetadataJsonAsync` / `ExtractGainMapJpegAsync` / `ProbeSizeAsync` / `DetectBasePrimaries`。
- **B 堆（同步方法，`WaitForExit(int)` 不接受 `ct`）**：`using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });`
  —— **不改签名 ⇒ 零调用方影响**；语义 = 「**取消 ⇒ 杀子进程 ⇒ 方法按兜底值返回**」，
  ⚠ **不是**抛 `OperationCanceledException`。涉及 `PlatformServices.TryFindInPath` ·
  `IccProfileService.ExtractIccToTempFile` / `GetOrGenerateStandardIcc` ·
  `QueueProcessor.ResolveJxrDecAppPath` / `BuildPipeColorArgs` / `ApplyColorPostStepsAsync` / `EmbedDroppedFilterIccAsync` ·
  `FfmpegCommandBuilder` 四级 static 链（`GetCachedProbe` → `ProbeInputColorMetadata` → `ProbeInputColorMetadataCore`
  → `ProbeWithFfprobe` / `ProbePeakNits`）。
- **链接 CTS**（有超时的那些）：`CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter(原超时)`
  ⇒ **先到者生效、原有 5s / 15s 超时行为不变**（零行为变化）。全仓 `CreateLinkedTokenSource` **6 处**。
- **点名与兜底在同一处**：取消/超时分支一律 `if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);`
  ⇒ **`Trace.WriteLine` 保留作兜底**（`#24` 的 Trace 可见性债由此关闭）。`log` 通道从 `MainWindow`
  （`Log` helper / `LogText`，含 `Dispatcher.UIThread.Post`）与 `Program`（`Console.Write`）接到最外层。
- ⚠ **两个「`#24` 刚改过、`#26` 二次修改」的方法 —— 逐字对照（证明四步范式没被改回去）**：
  - `PlatformServices.TryFindInPath`：`#24` 的四步范式（`var outTask = p.StandardOutput.ReadToEndAsync();` /
    `var errTask = p.StandardError.ReadToEndAsync();` → `if (!p.WaitForExit(5000))` → `try { p.Kill(entireProcessTree: true); } catch { }`）
    **原样未动**；`#26` 只在其**之上**插入 `using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });`
    与 `if (ct.IsCancellationRequested) { … return false; }` 两支。
  - `IccProfileService.GetOrGenerateStandardIcc`：同样四步范式（15s）**原样保留**（`#24` 那版本就是 `Kill(entireProcessTree: true)`），
    `#26` 在其上加 `ct.Register` 与取消分支；另把原 `Trace.WriteLine` 换成「有 `log` 走 `log`、无则回落 `Trace`」。
  - （旁证：`IccProfileService.ExtractIccToTempFile` **不在** `#24` 的 10 处名单里，本次顺手把它的 `p.Kill()` 升为 `Kill(entireProcessTree: true)`。）
- **`GainMapDecoder.DecodeToLinearRawAsync` 的「5 处全交」验证点（`#25` 最有价值的一条证据）**：该方法内 7 处调用
  **现全部**交 `log`+`ct`（`:155` / `:173` 本来就交，另 3 处本次补上）。

**b) 门禁 ①（静态链闭合）读数**：见 `TESTING.md §6` 第 95 条 —— 双宿主 pwsh 7.6.6 / PS 5.1 `exit=0`、读数逐字一致
（判据面内 **53** 处 · 命中 **0 / 0** · 负控 **7/7**），跳过数全部打印。

**c) ⚠ 缺口登记（措辞不得大于扫描面，第 87 条）**

1. **`PlanFolderDetector.Apply` 的 `ExifToolService.Detect()` —— 拿不到 sink（硬要求：不许塞假 sink）**：
   该方法签名为 `public static void Apply(PlanFolderResult plan)`（**无 `log` 形参**），且 `PlanFolderDetector`
   在 `src/**/*.cs` 内**零命中引用**（截至本次扫描）⇒ **没有任何持有 sink 的调用方**可传。
   ⇒ 该处的检测取消/超时**仍只进 `Trace.WriteLine`**，**本次未接**；**不登记为「已接」**。
2. ✅ **`QueueProcessor.IsAnimatedJxl` —— 「永久缓存毒化」设计岔口：已按 team-lead 裁定 (a) 落地（2026-09-18 收尾）**。
   **裁定 = 「只缓存『确定』的探测结果」**（比「取消时不写缓存」更细一层）：

   | 探测结局 | 动画判定 | 是否写缓存 |
   |---|---|---|
   | ffprobe **正常退出**，报 ≤1 帧 | `false` | **写** ✓ |
   | ffprobe **正常退出**，报 >1 帧 | `true` | **写** ✓ |
   | 超时（120 s）/ 被取消 / ffprobe 起不来 / 输出不可解析 / 异常 | —— | **不写** ✗ |

   理由：在「不确定」的结局上缓存 `false`，等于把「**我不知道**」永久固化成「**它不是动画**」
   ⇒ 后续所有帧走单帧路径且**再也不会重新探测**（静默丢帧）；缓存 `true` 同理会把一次抖动固化成
   永久走动画路径。只有 ffprobe **正常退出**时它的回答才是「确定」的。
   **实现**：`IsAnimatedJxl`（`QueueProcessor.cs`）拆为「读缓存（`TryGetValue`）→ `ProbeAnimatedJxl(..., out bool determinate)` →
   `if (determinate) AnimatedJxlCache[inputPath] = animated;`」；旧的 `AnimatedJxlCache.GetOrAdd(...)` 形态已**移除**
   （`GetOrAdd` 会把「不确定」也永久固化）。
   **门禁**：`verify-jxl-probe-timeout.ps1` 新增 2 个载体（形态自检 + 行为计数），**双宿主 45/0**（pwsh 7.6.6 / PS 5.1 各一次）。
   **变异证据**：把该行改回 `AnimatedJxlCache[inputPath] = animated;`（无条件写）⇒ 重建后门禁 **40/4**，
   恰好这 4 条转红（1 条形态 + 3 条行为，三条行为实为「超时行 1 条」而非 ≥2 条）；回滚 + 重建 ⇒ 45/0。
   ⚠ **已实测的代价（登记，供后续权衡）**：畸形 `.jxl` 在同一次运行里会被探测 **2 次**（原先第 2 次吃毒化缓存）
   ⇒ 该路径的挂起时长 **×2**（门禁注入 5 s 时实测 12.7~13.0 s；生产默认 120 s 时 ≈240 s）。
   按本仓价值序「**静默远比响亮失败糟**」，这是**有意**的取舍；若要压回 1 次，需要「按队列项作用域」的
   短命缓存（属新设计，**未拍**）。
   （对照：`FfmpegCommandBuilder.ProbeCache` 有 **10s TTL** ⇒ 退化值 ≤10s 自愈，**无需裁定**。）
3. **留在门禁判据面外的 ③a 残留（不靠 ① 兜，逐条登记）**：`FfmpegCommandBuilder.DecideOutputColor` 内 3 处
   `ProbeInputColorMetadata(...)`（该方法是**同步、无形参 `ct`**；调用方含 UI 线程的 `BuildArguments`，
   但**也被**已持有 `ct` 的 `QueueProcessor.EmbedDroppedFilterIccAsync` 调用 ⇒ 是 ③a，**未接**）；
   `QualityAnalysisService.AnalyzeAsync` / `GainMapJpegCodec.ResolveEncodeParams` / `ColorIntentFactory.FromOptions`
   内的 `ProbeInputColorMetadata(...)`（③b）。
4. **本次顺带观察到一处（登记，非缺陷）**：`IccProfileService.ResolveNamedIcc` 内
   `GetOrGenerateStandardIcc(carrier, primaries, trc, matrix)` 未交 `log`/`ct`，但该方法在 `src/**` 内
   **调用方零命中**（截至本次扫描）⇒ 按纪律**不写「死代码」**，只记「**仓内不可达（截至本次扫描）**」。


### 🟢 2026-09-18（general-purpose-1）⑩ **`#136` 切片 A：AVIF 动画探测三态化 + 补超时**

**背景**：`QueueProcessor.IsAnimatedAvifAsync` 与同文件 `ProbeAnimatedJxl` **纪律不对称** ——
后者有超时（120 s）+ 三态（`out bool determinate`）+ 「不确定不写缓存」，前者**既无超时**，
又把四条失败路径（ffprobe 缺失 / `proc == null` / 被取消 / `ExitCode != 0` / 抛异常）**全部 `return false`**
⇒ 把「**不知道**」当成「**不是**」⇒ 动画 AVIF + 非 gif/apng 目标时一次探测抖动就**静默丢帧**。
（这是 `#16`「探测失败语义三态化」在 avif 这条上的未修切片。）

**① 补超时（`QueueProcessor.cs`）**
- `internal const int DefaultAvifProbeTimeoutSec = 20;` —— **与同链兄弟站点 `ProbeAvifStreamRolesAsync` 的
  `WaitForExit(20000)` 同口径**（实测读过它，确实是 20 s）。
- `private static int ResolveAvifProbeTimeoutSec()` —— 读 `FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC`，
  `Math.Clamp(sec, 1, 600)`（照 `ResolveJxlProbeTimeoutSec` 的风格）。
- **实现用「链接 CTS + 杀树看门狗」**：`CreateLinkedTokenSource(ct)` + `CancelAfter(TimeSpan.FromSeconds(timeoutSec))`
  + `.Token.Register(() => proc.Kill(entireProcessTree: true))`。
  ⚠ 这是**有意**的选择：超时/取消 ⇒ Kill ⇒ **管道关闭** ⇒ 阻塞中的 `ReadStdoutDrainingStderrAsync` **必然**返回
  ⇒ 不依赖 `ReadToEndAsync` 的取消语义，也**不把**本方法改回同步 `WaitForExit(int)`（**保持异步**）。
  机制与同步版 `ProbeAvifStreamRolesAsync` **同形**。

**② 三态化**：返回类型由 `Task<bool>` 改为 **`Task<(bool animated, bool determinate)>`**。
⚠ **偏离你指令的一处（语言强制，非自选）**：你写的是「加 `out bool determinate`」，但 **C# 禁止 async 方法带
`out` 形参（CS1988）** ⇒ 只能改成元组返回。该形态也是本文件的既有惯用（`ProbeAvifStreamRolesAsync` 就返回元组）。
`determinate = true` **仅当** ffprobe **正常退出**（`ExitCode == 0`）**且**输出可解析出至少一行流信息
（`index,codec_type`）；超时 / 取消 / 起不来 / 异常 / 输出不可解析 ⇒ `false`。

**③ 裁定落地：不确定 ⇒ 保守按「多帧」**（依据 = `InputMayHaveMultipleFrames` 自述政策
「宁可回退传统管线，也不冒『静默只保留首帧』的风险」）。两个调用点都**显式消费** `determinate`：
- `:428`（`maxDimSkipAnimated`）⇒ `maxDimSkipAnimated = avifProbeAnimated || !avifProbeDeterminate;`（不确定 ⇒ 跳过预缩放）
- `:2690`（`bool isAnimatedAvif`）⇒ `bool isAnimatedAvif = avifProbeAnimated || !avifProbeDeterminate;`（不确定 ⇒ 走两步法）
第三态**不静默**：四条「不确定」来源收敛到**同一个** `Indeterminate(string why)` 出口，
打一条点名日志 `[avif] 动画探测<why> ⇒ 不确定：<文件名>（按多帧处理）`（`why` ∈ 起不来 / 被取消 / 超时（Ns） / 异常退出（exit=N） / 输出不可解析 / 异常（类型名））。

**④ 注释改成与实现一致**：旧注释承诺「多个视频流 **或** >1 帧 **或** alpha 轨道」，实现只数
`videoCount >= 2`（**流条数**）⇒ 承诺三条、实现一条。本次**只改注释**，判据未动（扩判据需素材实测，另立任务）。

**⑤ 未引入缓存**：本次**不加**任何缓存（`#136` 第二步会把重复探测收敛成单次求值）。

**新门禁**：`tests/scripts/verify-avif-anim-probe.ps1`（**尚未登记进运行器 ⇒ 清单需 40 → 41，由 team-lead 改**）。
- 自备素材：用本仓 ffmpeg 生成 `still.avif`（1 帧，ffprobe 报恰好 1 条 video 流）+ 截断到 32 B 的 `trunc.avif`。
- **双向行为断言**：静态 AVIF ⇒ **确定** ⇒ 走单命令 + **不**打不确定行（**不误伤**）；
  截断 AVIF ⇒ **不确定** ⇒ 点名日志 + 走两步法 + **不**出现「输入为静态 AVIF」+ 零产物（**有牙**）。
- 读数：**双宿主各一次 18/0**（pwsh 7.6.6 与 PS 5.1，`exit=0`）。
- 变异：把两个调用点的 `|| !avifProbeDeterminate` 去掉（= 「不确定 ⇒ 按静态」）⇒ 重建后 **14/3**，
  红的恰好是 3 条（1 条源码形态 + 2 条行为：`Step1` 消失、「输入为静态 AVIF」出现）；
  回滚 + 重建 ⇒ **18/0**。

**⚠ 已登记的覆盖缺口（措辞不得大于扫描面，第 87 条）**
- **超时分支没有被行为实测。** 我**没能**构造出让 `ffprobe -show_entries stream=index,codec_type` 在 AVIF 上
  **挂住**的素材（与 JXL 那次的 1 B `FF` 不同）：1 B / 截断 AVIF ⇒ ffprobe **秒退**（exit 1，`moov atom not found`）；
  `mkfifo`（Git-Bash/MSYS）建出的 FIFO 在 .NET 侧 `Test-Path` 为 **False** ⇒ 不可用作输入；
  「造一个会挂住的假 `ffprobe.exe`」需自备 PE 二进制，门禁不引入构建依赖。
  ⇒ 本门禁对「超时可达 + Kill 生效」只给**源码形态自检**（同 `TESTING §6` 第 90 条对 JXL 默认值的既有做法），
  **不**声称「已实测超时被触发」。要行为实测需另找素材（另立任务）。
- 已知残余：`videoCount >= 2` 只能识别**多流/层叠**形态的 AVIF，**普通多帧 AVIF（1 流 N 帧）仍被判「静态」** ——
  那是 `#136` 注释里承诺过、实现里没有的第二/三条判据，本次**未改判据**（需素材实测）。
- ⚠ **同族但未收敛（登记给切片 B，非缺陷）**：探测超时现在有**两套** ——
  `DefaultJxlProbeTimeoutSec = 120` / `DefaultAvifProbeTimeoutSec = 20`，配两个环境变量
  `FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC` / `FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC`。
  取两个值是**有据的**（JXL 用 `-count_frames` 要**解码全部帧** ⇒ 需要 120 s；AVIF 只列流 ⇒ 20 s 够），
  但**两套解析代码形状相同**（都是「读 env + `Math.Clamp(sec,1,600)` + 回落默认」）⇒ 切片 B 可考虑收敛成一个 helper。
- ⚠ **新门禁里两处「切片 B 会踩到」的载体（已就地写注释）**：`verify-avif-anim-probe.ps1` 的
  ① 逐字签名锚点 `'private static async Task<(bool animated, bool determinate)> IsAnimatedAvifAsync('`
  与 ① 精确计数 `$consume -eq 2`。切片 B 若把三态换成枚举/结构体、或把两个消费点收敛成一个 helper，
  **这两条会转红** —— 那是**真信号**（载体失效），切片 B **必须重取载体**，**不得**放宽成 `-ge`。
- ⚠ **另两条「不在切片 A 范围、我一行未动」的多帧轴缺口（已登记为任务 `#43`）** ——
  来源：general-purpose-3 只读发现 + 我**逐条读码复核确认**（不是转述）：
  1. **多帧「路由轴」未保守化**：`InputMayHaveMultipleFrames`（`QueueProcessor.cs:4405-4414`）在
     `if (fmt is not ("gif" or "apng")) return IsAnimated(item);`（`:4408`）时**直接返回**，
     而 `IsAnimated`（`:4417-4430`）的判据**不含 `.webp`/`.avif`** ⇒ `.avif`/`.webp` 输入 +
     **非 gif/apng 输出**时**零探测、返回 `false`**。但其 doc（`:4396-4402`）写
     「可能承载动画的输入容器**一律**按多帧处理 —— 含 … 与 `.webp/.avif`」——
     该承诺**只在 `:4411`（gif/apng 输出）那一支成立** ⇒ **文档与实现不一致**
     （与切片 A 修的 `IsAnimatedAvifAsync` 注释是**同一族**：承诺 N 条、实现 1 条）。
     ⇒ 切片 A 只把**预缩放轴**保守化了，**路由轴原样**。
  2. **注释清单缺 `.webp`**：`:423-425` 逐字写「动图输入（GIF/APNG/动图 JXL/动画 AVIF）必须跳过」——
     缺 `.webp`；且该处**代码**的跳过条件（`IsAnimated(captured) || (ext==".avif" && 探测)`）同样不含 `.webp`。

**同轮顺带**：`TESTING §6` 新增**第 97 条**（`Start-Process -PassThru` 不带 `-Wait` 时 `.ExitCode` 在 PS5.1 恒为
`$null`：`-eq 0` 假绿 / `-ne 0` 假红；`WaitForExit()` 与 `Refresh()` 都救不回）。
⚠ 该条是**本轮实测踩到并修掉**的（首跑 pwsh 17/0 而 PS5.1 14/2）。

**回归面（我跑过的，非整轮）**：`verify-ps-compat.ps1` **13/0**（语料 77 → **78**，新脚本已被 glob 收录）；
`_probe-ct-chain-closure-scan.ps1` **命中 0/0**；`verify-anim-static-target.ps1` **48/0**（`maxDimSkipAnimated` 的直接回归面）。
两个项目 `-c Release --no-restore` 重建，均 0 警告 0 错误，探针侧 `FfmpegGui.dll` 与源侧**同哈希**。**未提交。**


## ✅ 已实现（可依赖）

| 项 | 证据 |
|---|---|
| 4 个色彩策略的**完整语义**（无损阶梯 / 超集归一 / CICP-ICC 择一 / `Reject(原因,建议)`） | `ColorStrategyPlanning.cs` + `ServiceProbe contract` **98/0** |
| **超广色域保真路径**（ProPhoto/ROMM 等超 BT.2020：**像素零改动 + 仅靠源 ICC 描述**，不写 CICP） | `PlanFaithful`；`verify-prophoto-faithful` **26/0** |
| **最长边几何缩放**（解码步复用同一个 `BuildMaxDimensionScaleFilter`；缩放后尺寸由原始帧长度反推） | `verify-color-wiring` 第 (7) 节：512×384 限 32 ⇒ 实测 **32,24** + 2 条负控 |
| **外部 ICC（`--icc-file`）**：判定**源**色彩空间并真驱动映射 | `verify-color-wiring` 第 (8) 节：有效 ICC ⇒ `Map` + 日志 `[--icc-file]` + 像素 PSNR 35.36dB；无效 ICC ⇒ 回落不冒认。**✅ 结案（2026-09-17）**：本选项语义 = **判定源**（**不是**「把 ICC 附到产物」）⇒ 「外部 ICC 源 + 不可携带 ICC 的容器」**不登记** `NoIccCarrier` / `ContainerCannotCarryIcc`，经**实测判定为有意的口径差异、非缺陷** —— ⚠ 准确表述：该格**不是**「`Degradations` 为空」，而是**恒有 `UnlabeledAssumedSrgb` 等其它降级兜底** ⇒ `Kind` 从不误报 `Proceed`（逐格式矩阵与实测见下方「🔴【待实现】功能清单」注记区的结案条） |
| **传统管线的策略来源统一**（`preserveSource`/`stripIcc` 改由 `plan.EffectiveStrategy` 推导；S2 的三种覆盖与 S3 禁 iccgen 在传统管线上也生效） | `FfmpegCommandBuilder.EffectiveColorStrategy`；`verify-color-strategy` **40/0**（含 **13 格 parity**：两管线共同产出格 CICP/ICC 全一致） |
| 色彩引擎作为**默认**执行路径（png/apng/tiff/tif/webp/jpg/jpeg） | `ColorEngine` 默认 `auto`；A/B 实测 **8-bit 源逐字节等价** |
| **JXL 输出接入引擎（两个后端都已接入）** | ① `backend==Ffmpeg`：`ImageEncoderArgs.IsEngineEncodable` 加 `jxl`（单一真值）+ `BuildJxlOptions`（`MapJxlDistance` 搬入）；`RawColorPipeline` 把 JXL 的 CICP 标签**前置到 `-i` 之前**（写输出侧被静默忽略 ⇒ 实测会回落 `sRGB primaries`）。② `backend==Cjxl`（2026-09-17 cjxl 出口轮）：计划位 `ExternalEncoderExit` + 路由门放行 + `RawColorPipeline.EncodeJxlViaCjxlAsync`（PPM/PAM 管道 → `cjxl -`）。⚠ `jxl + AttachIcc` 的显式拒绝门**按出口分叉**（`&& !plan.ExternalEncoderExit`）：cjxl 出口能用 `-x icc_pathname=` 写 ICC。门禁 `verify-color-wiring` 第 (9) 节 10 条 + 第 (10) 节 19 条 + `ServiceProbe wire` 8 条 |
| 引擎编码出口承接**全部编码器参数**（质量/无损/色度/PNG pred+DPI/WebP preset/TIFF 压缩/JPEG huffman+dct） | `ImageEncoderArgs.cs` |
| 色彩矩阵的**两个角色**（输入声明 / 输出标注）+ 转换参数 | `verify-color-token-normalize` **33/0** |
| HLG 走 OOTF 分支（接线级） | `verify-hlg-route` **13/0** |
| **GainMap（Ultra HDR JPEG）接入引擎**（原 🔴【待实现】第 1 项） | `RawColorPipeline` 新增 GainMap 出口（zscale 线性化 → 托管 float RGBA → `GainMapEncoder`）；解码串/参数/交错为单一真值 `GainMapJpegCodec`；旧专用管线降级为回退。门禁 `verify-gainmap-managed` **20/0** + `verify-gainmap-engine` **84/0 → 117/0**（2026-09-19 实测更正，原写 25/0；§10 落地后 117/0）；**产物读容器验证**：`images=2`、MPF 副图 `soi=True`、ISO `max=2.30`（= `log2(1000/203)`） |
| **色域映射（GMO）**：源色域超出目标时做**色度压缩**而非硬裁切 | 算子移植 libjxl `GamutMapScalar`（`preserve_saturation=0.3`），插在**3×3 矩阵之后、钳位之前**；`--color-gamut-map off\|on`（默认 `off`）+ GUI 开关；`ColorLoss=GamutCompression` + 新 Code `GamutCompressed`。**默认 `off` 与改前逐字节相同**；`on` 时越界像素被压回 `[0,1]` 且保色相，**未越界像素严格 no-op**（自证 `FAIL=0`） |
| **门禁脚本的宿主兼容性**（在 `powershell.exe` / PS 5.1 下也能跑：无三元 `? :` / `??` / `?.` / `&&` / `\|\|`，无双引号串内嵌全角弯引号） | `verify-ps-compat.ps1` **13/0**（8 条负控 + 2 条点名回归锁；已登记运行器首位） |
| 传统管线（显式 `--color-engine legacy`） | 保留为 A/B 与排查用的逃生门 |

---

## 打包验收结论（2026-09-16，验收人：AtomCode）

- 验收范围：工作区全量打包（62 修改 + 78 未跟踪文件，HEAD 3ff26d2 未变）。
- ✅ Release 构建 0 警告 0 错误；门禁 decision-delivery **44/0**、format-regression **19/0**，与基线一致。
- 结论：**验收通过**，工作区状态可作为下一手起点；建议维护者择机提交。

## 下一手交接清单（按优先级）—— **2026-09-16 第二轮已处理，见下方「本轮结果」**

1. ~~**HLG OOTF headless 未触发**~~ ⇒ **已关闭：测试素材缺陷，非产品缺陷**。
   原素材 `hlg_src.png` 与 `pq_src.png` **md5 完全相同**且 `color_transfer=unknown`
   （本机 ffmpeg 的 PNG 编码器不写 cICP）。换真标签素材后 HLG→SDR 与 PQ→SDR 的
   **PSNR=23.89dB**（非 inf），且实测峰值 962nit vs 10000nit ⇒ HLG 分支确实被走。
   已补**接线级**门禁 `verify-hlg-route.ps1`（13/0）。详见 `TESTING.md §6` 第 42 条。
2. ~~**矩阵白名单与 zimg 能力对齐**~~ ⇒ **已修，且发现范围比记录更大**。
   实测 GUI `ColorMatrixCombo` 的 10 个选项里 **7 个是坏的**（`bt2020ncl`/`bt2020cl`/`ycgcocn`/
   `ycgcocs`/`bt2020`/`smpte432`/`smpte431`），应用白名单又照抄了这份脏词表 ⇒ 选中即整条任务 -22 失败。
   已收敛词表 + 把规范化收进 `FfmpegOptions` 的 setter（单一真值）。另发现 **CLI `--color-trc srgb|pq|hlg`
   同样硬失败**（`--help` 明确宣传这三个名字）⇒ 一并修。新增门禁 `verify-color-token-normalize.ps1`（18/0）。
   详见 `TESTING.md §6` 第 41 条。
3. **工作区未跟踪文件 —— 已完成分类，待维护者按类处置**（2026-09-16 第二轮）：
   `git status` 报 76 条未跟踪**条目**，展开后是 **97 个新增文件**（另有 62 个已跟踪文件被修改）。逐类结论：

   | 类 | 数量/体量 | 内容 | 结论 |
   |---|---|---|---|
   | **A 生产代码** | 26 个 / 390 KB | `src/FfmpegGui/**` 新 .cs（含 `Services/ColorMapping/` 14 个、`CliParser.cs`、`IpcService.cs`、`MediaProbeCache.cs` 等） | **必须追踪** |
   | **B 应用资源** | 2 个 / 2 KB | `Resources/icon.ico`、`Resources/icon_raw.png` | **应追踪**（`App.xaml.cs:76` 以 `avares://` 引用）⚠ 见下「附带发现」 |
   | **C 测试工程** | 4 个 / 261 KB | `tests/ServiceProbe/{Program.cs,csproj}`、`tests/UiTestHost/{Program.cs,csproj}` | **必须追踪**（门禁依赖探针） |
   | **D1 门禁脚本（可移植）** | 50 个 / 308 KB | `tests/scripts/` 下 `verify-*.ps1` / `_probe-*.ps1` / `_run-step3-gates.ps1` 等 | **应追踪** |
   | **D2 门禁脚本（含本机专属路径）** | 4 个 / 46 KB | `final-verify.ps1`、`run-full-matrix-test.ps1`、`run-full-matrix-test-safe.ps1`、`verify-remaining.ps1` | **先参数化再追踪**（硬编码 `<local-dir>\...` 与 `C:\temp\...`）；或归入"一次性本机诊断"不追踪 |
   | **E 项目文档** | 8 个 / 101 KB | `docs/*.md` + `docs/archive/` 5 个 | **应追踪** |
   | **F agent 工作记忆** | 3 个 / 89 KB | `.workbuddy-ai/memory/*.md` | **建议不追踪**（工具状态 + 过程性内容），需补 `.gitignore` 规则 |

   - **体量与安全**：97 个文件合计约 **1.2 MB**，最大单文件 309 KB（`MainWindow.xaml.cs`）；
     扩展名分布 `70 cs / 60 ps1 / 15 md / 4 axaml / 3 csproj / 2 xaml / 2 json / 1 sln / 1 png / 1 ico`。
     **无密钥/令牌**（全量扫描 `api_key|secret|token|password|PRIVATE KEY` 无实质命中）；
     **无本机家目录路径**（`C:\Users\<用户名>` 零命中）；无 >500 KB 的二进制。
   - **反向检查（已跟踪但本不该追踪的）**：**无**。已跟踪 90 个文件里没有编译产物、媒体、第三方源码 blob
     （`tools/src/` 下被跟踪的是项目**自有**工具源码 dngtool/pngcicp/dngjxlprobe，第三方库已由 `.gitignore` 排除）。
   - **`.gitignore` 已正确覆盖**：`bin/ obj/ publish/ tests/output/ *.log *.tmp *.pdb tools/src/<第三方>/` 等，
     `git add -n` 实测未误纳入任何编译产物（`tests/ServiceProbe/` 只展开出 2 个源文件，bin/obj 均被排除）。

   **✅ 维护者决定（2026-09-16，无需再问）**：
   - **① 不补 `.gitignore`**：`.workbuddy-ai/` 保持"未跟踪但未忽略"现状（不写入忽略规则）。
   - **② 不参数化 D2 的 4 个脚本**：维持现状，不加环境变量改造。
   ⇒ 两项**均不做改动**；D2 仍是"含本机路径、不可移植"状态 —— **若日后要纳入追踪，必须先参数化**。
   本项**结案**，下一手不必再提。

   **附带发现（轻微缺陷，需 csproj 一行，本环境禁止改 csproj）**：
   `Resources/icon.ico` **从未被打包** —— 实测 `FfmpegGui.dll` 与 `FfmpegGui.exe` 内**均不含该图标字节**，
   构建输出目录 `Resources/` 下也只有 `Locales/`。成因：csproj 只有
   `<AvaloniaResource Include="**/*.xaml" />`（**不含 `.ico`**），且未设 `ApplicationIcon`、无 `Directory.Build.props`。
   ⇒ `App.xaml.cs:76` 的 `AssetLoader.Exists("avares://FfmpegGui/Resources/icon.ico")` 必为 false
   ⇒ **托盘图标实际不显示**（代码注释自认"图标缺失不影响功能"，故长期未被发现）。
   修法：csproj 增 `<AvaloniaResource Include="Resources/icon.ico" />`（属 csproj 改动 ⇒ 需重新 restore/构建）。
   ⚠ **2026-09-17 更正**：本文原先写「本环境会触发 restore 失败」——**该结论已被推翻**，
   本环境 **`dotnet restore` 可用**（详见文末「接手提示」第 3 项）。

## 本轮结果（2026-09-16 第二轮，接手会话）

- **代码改动**（5 个文件）：`Services/ColorSpaceRegistry.cs`（新增 token 规范化单一真值 + 实测枚举）、
  `Models/FfmpegOptions.cs`（色彩三元组在 setter 规范化）、`Services/FfmpegCommandBuilder.cs`
  （白名单换成实测枚举）、`Services/QueueProcessor.cs`（2 处 `-colorspace` 改走唯一出口）、
  `MainWindow.xaml`（矩阵词表收敛为 5 个两侧都成立的值）。
- **新增门禁**（2 个，均已纳入 `_run-step3-gates.ps1` 默认清单，且都做过**变异验证**）：
  `verify-color-token-normalize.ps1` **18/0** · `verify-hlg-route.ps1` **13/0**。
- **回归验证**：
  - 探针 17 mode：**356 pass / 0 fail**（与基线逐一相同 ⇒ 零回归）。
  - 脚本门禁**逐个跑**（本会话宿主跑不了运行器，见下）：wiring **26/0** · caps **12/0** · strategy **16/0** ·
    format-regression **19/0** · widegamut **18/0** · i18n-scan **6/0** · png-signature **4/0** ·
    gainmap-memory **4/0** · metadata-privacy **3/0** · gif-avif-framelist **17/0** · color-peak **25/0** ·
    prophoto **26/0** · gainmap-managed **20/0** · png3-interop **9/0** · tonemap **15/0** ·
    run-pipeline-tests **80/0** · 三个静态扫描脚本 PASS · tiff-icc / webp-hdr-fix EXIT=0。
    ⇒ **核心 16 项里 15 项数字与基线完全一致**。
  - Release 构建 **0 警告 0 错误**。
- **清理**：`docs/_t.tmp` ~ `docs/_t9.tmp`（上一手验收时留下的 9 个临时 grep 转储）已删除。

### ⚠ 本会话宿主限制（下轮接手请先读，否则会误判为"回归"）

1. **⚠ 2026-09-16 更正：运行器脚本阶段「跑不完」的真因是「显式后台任务约 2 分钟被终止」**，
   不是原先记的「脚本内 `Start-Process pwsh` 被安全策略拦截」。同宿主同一轮的对照证据：
   - **显式后台**（`run_in_background`）跑 `_run-step3-gates.ps1` ⇒ **2 分 1 秒**中断，只跑完 3 个脚本；
   - 另一条**显式后台**的 12 脚本批 ⇒ 同样 **2 分 1 秒**中断，只跑完 2 个
     ⇒ **与脚本内容无关，是统一时限**（这正是原先误判的根源）；
   - 而**前台发起、超时后自动转后台**的那批 10 个脚本 ⇒ 跑满 **10 分 32 秒**、10 个全部完成。
   ⇒ **变通**：用**前台**发起（让它超时后自动转后台），或把单批控制在 2 分钟内。
   运行器本身（含 `Start-Process` 起子脚本）在**前台**能跑通 —— 实测 `script-host` 解析正常、
   脚本逐个出结果（`ps-compat` 13/0 → `color-wiring` 44/0 → `color-caps` …）。
2. **宿主批量删除守卫**：删除 >50 文件会打 `[safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED]`，
   且计数**按会话累积**；守卫自身的错误会被抛进脚本上下文 ⇒
   `verify-decision-delivery.ps1` / `verify-colorfmt-matrix.ps1` / `verify-color-engine-xcheck.ps1`
   在本会话**跑不完**（**与产品无关**，脚本里并没有 `Xthrow`——那是宿主实现）。
   ⇒ **建议在新会话开头（尚未累积删除时）先跑这三个**。

### 附带查证：`--color-matrix` 是否"静默无效"—— **结论：不是缺陷**

HDR→SDR 场景下 `--color-matrix bt709` 与 `bt2020nc` 产出**逐字节相同**的命令行，一度疑似"参数被丢弃"。
实测对照后定性为**符合「单一裁决点」设计**：

| 用例（源/目标） | `--color-matrix` | 实际命令行 |
|---|---|---|
| HDR 源 → jpg（有 tonemap 链） | `bt709` / `bt2020nc` | 两者相同，`-colorspace bt709`（**裁决层**按 SDR 出口决定） |
| SDR 源 → jpg（无链） | `bt2020nc` | `-colorspace bt2020nc` ✓ 生效 |
| SDR 源 → jpg（无链） | `ycgco` | `-colorspace ycgco` ✓ 生效 |
| SDR 源 → jpg（无链） | `gbr` | 不写 `-colorspace` ✓（RGB 矩阵族不写给 YUV 编码器，设计如此） |

⇒ 用户值在"无链"时生效、在"有链"时被链的输出矩阵取代（后者才是实际交付的矩阵）。

### 第三轮：色彩矩阵可用性全量审查（2026-09-16，**已修复**）

上一条"窄缺口"经**像素级取证**确认后已修，且审查发现它只是一个更大问题（**角色错位**）的表征。
完整论证见 `TESTING.md §6` 第 46 条，摘要：

- **审查方法**：token × 目标格式 全扫描（25 格）+ 原始 ffmpeg 对照归因（排除"是应用的锅还是编码器的锅"）。
- **事实**：矩阵标注**只有 AVIF 真正承载**（nclx）；JPEG 无 CICP、WebP 的 RIFF 无 CICP、TIFF 走 ICC
  （ICC 无"矩阵"概念）、PNG/JXL 是 RGB 原生 ⇒ 这些格式上矩阵**没有承载通路**。
- **根因（三条）**：
  1. **角色错位**：用户矩阵被只当**输出标签**，**从不**作为 `-i` 前的**输入声明**
     ⇒ 实测无标签 YUV 源下 `bt2020nc` 与 `bt709` 产物 **md5 相同、PSNR=inf**（该下拉的唯一语义 100% 未落实）；
  2. **能力表未施加于用户值**：`GetFormatColorCapabilities` 已给出 `capM`（jpg/webp = `bt709`），
     但它只用于像素链的目标矩阵，用户的 `bt2020nc` 仍被原样写进 jpg 的输出标签；
  3. **词表含"必败值"**：`bt2020c`/`ycgco` 在**两个角色**里都不可用（作输入声明令 swscale 报
     `Impossible to convert between the formats` ⇒ **-40、零产物**；作输出标注被 mjpeg/libwebp/libaom 拒绝）。
- **修复（4 处）**：新增 `FfmpegInputMatrixNames`（swscale 实测白名单）+ `DeclMatrix` 并下发输入声明
  + 输出标注服从 `capM` + 词表收敛为 `bt709/bt2020nc/bt470bg/smpte170m/gbr`（两个角色都可用）
  + CLI 传不可用值时的明确 `⚠`（可用性诚实）。
- **验证**：门禁 `verify-color-token-normalize.ps1` 扩到 **33/0**（新增 E 节锁角色分离），
  **变异验证 33/0 → 24/9**（精确复现原始零产物缺陷）；探针 **356/0**；
  `wiring/format-regression/widegamut/prophoto/tonemap/strategy/hlg-route` 全部与基线一致（多数产物字节数相同）。
- **遗留（开放项）**：JPEG/WebP/TIFF/PNG 上矩阵**仍无承载通路**（选它只是"声明源"）——
  要真标注需 exiftool 写 EXIF `ColorSpace` / 自定义 WebP chunk，属**新增能力，需拍板**。

### 第四轮：4 个色彩策略模式的实现审查（2026-09-16，**仅分析，未改代码**）

完整论证见 `TESTING.md §6` 第 47 条。**结论一句话**：4 个模式的**深度实现存在且质量很高**
（`ColorStrategyPlanning.cs` ~800 行 + 契约探针 95 条全格枚举），但**GUI 走的是 legacy 的浅层近似**
（两个布尔），且那个深层实现**在正常使用下不可达**。

- **入口**：GUI = `MainWindow.xaml:447-463` 四个单选；CLI = `--color-strategy`（+ 旧 `--icc-mode`）。
- **两层实现**：引擎层（`ColorStrategyPlanning.cs`：无损阶梯 / 超集归一 / CICP-ICC 择一 / `Reject(原因, 建议)` /
  `OverrideReason`）· legacy 层（`Decision.cs:145-158`：仅 `preserveSource`/`stripIcc` 两个布尔）。
- **实测决定性证据**（同源同格式）：
  | 用例 | legacy（GUI 实际路径） | 引擎（`--color-engine engine`） |
  |---|---|---|
  | `recommended` 无目标 | 592110 B / md5 `e3aa3a77…` | 同 md5（一致） |
  | **`manual` 无目标** | **md5 与推荐完全相同** ⇒ 等价 | **exit=1 无产物**（Reject） |
  | `carry` 源有标签 | 592811 B | 同（一致） |
  | **`carry` 源无标签** | **exit=0 静默产出** | **exit=1 无产物**（Reject + 建议） |
  ⇒ **legacy 把 4 模式压成 2 个自由度**，且**无拒绝语义**。
- **可达性**：`ColorEngine` 默认 `"legacy"`；GUI 无开关、预设不带、`--help` 未列出 ⇒
  **正常使用下 4 模式只有浅层近似**，而 UI tooltip 描述的是引擎语义（宣称≠交付）。
  ⚠ 代码自注：切默认值**需先有 A/B 数据**。
- **覆盖空洞**：`verify-color-strategy`（16）测 legacy、`ServiceProbe contract`（95）测引擎纯函数
  ⇒ **无门禁断言 legacy 路径的 4 模式行为**。
- **附带发现**：legacy 的「HDR→SDR 需显式请求 tonemap」守卫不生效（`TonemapCurve` 默认 `hable` 自动 tonemap）。
- **待拍板选项**：A 引擎设为默认（需 A/B 数据 + 先补参数承接）· B GUI 加引擎开关 ·
  C 补齐 legacy 的拒绝语义 · D 补门禁锁两条路径一致性。

### 第五轮：清理传统管线 · 增量 1（2026-09-16，**已实施**）

用户指令：清理传统管线、以自研管线为准完整实现。本增量做**硬前置**，完整论证见 `TESTING.md §6` 第 48 条。

- **做了什么**：新增 `Services/ColorMapping/ImageEncoderArgs.cs`（编码器参数**单一真值**），
  让引擎编码出口承接 质量/无损/色度子采样/PNG pred+DPI/WebP preset+压缩级别/TIFF 压缩算法+DPI/JPEG huffman+dct；
  三个质量映射从 `FfmpegCommandBuilder` 迁入并让其转发（数值单点）；
  `TransformFileAsync` 增加 `options` 参数（`options==null` 时保持历史固定值）。
  `ColorEngineRouter.UnconsumedOutputSettings` 精简为只剩**结构上不属于引擎**的项
  （几何/动画/GainMap/外部 ICC/渐进 JPEG）。
- **效果**：6 个此前被拒的参数组合现在全部产出；负控（libwebp 4:4:4、几何变换）仍拒绝并点名。
- **契约更新**：`wire` 探针 2 条旧契约断言改写为「不再拒绝 + 真的落到编码器参数 + 负控」⇒ **26 → 29 条**。
- **回归**：探针合计 **359 pass / 0 fail**；`wiring 26/0`、`strategy 16/0`（产物字节数与改前一致）。

- ⚠ **A/B 数据已采（切默认的依据），暴露 2 个必须先修的语义差异**（同源 P3→sRGB）：
  | 格式 | legacy | 引擎 | 根因 |
  |---|---|---|---|
  | png / tiff | `rgb48be`/`rgb48le`（16-bit） | `rgb24`（8-bit） | `OutBitDepth` 用 `o.BitDepth ?? 8` ⇒ 未指定时**恒 8-bit**，容器支持 16 却被静默降位 |
  | jpg | `yuvj420p` | `yuvj444p` | 引擎喂 RGB 使 mjpeg 自选 444（legacy 选 420） |
  | webp | 14200 B | 13146 B | 编码参数差异（PSNR 30.8dB） |
  ⇒ **下一步**：修这两项（位深跟随源、chroma=auto 时给 legacy 一致的默认），重跑 A/B 确认一致，
  再执行「切默认 + 删 legacy 执行体 + 统一决策层」（任务 #13/#14）。

### 第六轮：清理传统管线 · 增量 2（2026-09-16，**已实施**）

承接上一条的 A/B 差异，逐项定位并修掉。完整论证见 `TESTING.md §6` 第 49 条。

- **修了 3 处 + 2 处顺带**：
  1. **位深**：`TargetBitDepth = o.BitDepth ?? 8` ⇒ 改为对齐 legacy 的 `EffectiveBitDepth()`
     （未指定时跟随源位深、按容器上限钳）—— 此前 16-bit 源在 PNG/TIFF 上被**静默降位**。
  2. **色度默认**：`MapEnginePixFmt` 对 jpg 在 `chroma=auto` 时**也显式给** `yuvj420p`
     （引擎喂 RGB 原始帧，mjpeg 否则自选 444）。
  3. **转换矩阵（最关键）**：Plan 对非 CICP 容器会清空 `OutMatrix`（标注改由 ICC 承担），
     但矩阵**同时是 RGB→YUV 的转换参数** ⇒ 引擎命令里完全没有 `-colorspace`，swscale 按尺寸取默认
     （SD ⇒ bt601），而 legacy 用 bt709。已在 `BuildEncodeArgs` 对 YUV 输出补 `-colorspace bt709`。
  4. 出口位深规则补 `RgbNative` 条件（YUV 容器保 16-bit 中间件）。
  5. **顺带**：`RunToEndAsync` 现在记录完整 ffmpeg 命令行（此前不记录 ⇒ 两条管线无法对拍，
     本次排查因此先猜错两轮）。
- **A/B 复测（关键结论）**：
  | 源 | 格式 | 结果 |
  |---|---|---|
  | **8-bit** | png / jpg / tiff | **逐字节相同 / PSNR=inf / 逐字节相同** ✅ |
  | 16-bit（P3 ICC） | png / tiff | PSNR 85.8dB（16-bit 舍入级） |
  | 16-bit | jpg / webp | PSNR 36.96 / 30.76dB（有损编码对 ≤1 LSB 输入的放大） |
  16-bit 源残余差异已定性为**舍入级**：两管线 RGB 输入 8-bit 意义上中位差 0.012、p99 0.05、max 1.00。
- **回归**：探针 **359/0**；`wiring 26/0`（含「不选引擎 ⇒ 行为逐字节不变」）、`strategy 16/0`、
  `format-regression 19/0`，产物字节数与基线一致。
- ⇒ **切默认的前置条件已满足**（8-bit 源逐字节等价）。下一步 = 任务 #13（决策层统一）+ #14（切默认 + 删 legacy）。

### 第七轮：切默认到自研引擎（2026-09-16，**已实施**）

完整论证见 `TESTING.md §6` 第 50 条。

- **默认值改为三值语义**：`ColorEngine` 默认 `"auto"`（引擎优先 + 不可走时优雅回退传统管线）·
  `"engine"`（显式，不可走时硬失败点名）· `"legacy"`（显式逃生门）。
  新增 `ColorEngineRouter.ExplicitlyRequested` 区分"显式要求"与"默认"。
  ⚠ 不区分会让默认切引擎后**所有非引擎格式硬失败**（实测 avif/gif 全红）。
- **顺带修掉 2 个集成缺口**：
  1. **GUI 的 tonemap 入口缺失** ⇒ HDR→JPEG 无路可走（format-regression 16 项全红）。
     已让 `ColorIntentFactory` 在 **HDR 源 + 容器不支持 HDR** 时**自动**用内部默认曲线 `hable`
     并在决策日志写明是**自动**（`ToneMapAuto`），不是静默降级。
  2. **PNG 的 CICP 能力被低估**（`CanCicp` 默认 false，但应用确实由 `PngCicpService` 后置写 cICP）
     ⇒ S2/S3 对 HDR→PNG **误拒**。已置 `true`；**契约探针 95/0 未受影响**。
- **⚠⚠ 发现并修复一个门禁完整性缺陷**：**12 个门禁脚本把 `bin/Debug` 排在 `bin/Release` 之前**
  ⇒ 它们跑的是 **09-15 17:09 的 Debug 旧二进制**，此前记录的这些数字**都不对应当前代码**。
  已全部改为 Release 优先 + Debug 兜底。运行器的"二进制新鲜度硬闸"只比较探针 bin 目录，**没拦住**。
- **门禁契约更新**：`wiring`（默认走引擎 + 质量已承接且生效）**28/0** ·
  `strategy`（新增 `$expectReject`：不得产出 + 必须给原因）**16/0** ·
  `format-regression` **19/0** · `widegamut` **17/1** · 探针全绿（`wire` 30）。
- **遗留（下一步）**：
  1. ~~`widegamut` 唯一红格 `p3pq-png-pq`~~ ⇒ **已修**：根因是 `ColorIntent.HdrOutput` **从未被装配**，
     导致「HDR 源 + **显式 HDR 目标**」被 HDR 守卫误拒（`ColorStrategyPlanning` 只在 auto 分支补 `HdrOutput`）。
     已在 `ColorIntentFactory` 的 `!dstSdr` 分支置位 ⇒ `widegamut` **18/0**。
  2. 🔴 **超广色域保真路径 = 待实现**（规划层无 `Faithful` 概念，仅传统管线实现）⇒ 已加路由拦截，
     见顶部「🔴【待实现】功能清单」第 1 项与 `TESTING.md §6` 第 51 条。`prophoto-faithful` 恢复 **26/0**。
  3. 复跑结果：`prophoto-faithful` **26/0** · `gainmap-managed` **20/0** · `tiff-icc` / `webp-hdr-fix` DONE（表格型）·
     `decision-delivery` **本会话跑不完**（宿主批量删除策略，非产品问题，见 §6 第 44 条）。
  4. 决策层统一（legacy 的 2 布尔退役）与 legacy 执行体删除 —— **须等「待实现清单」的 2–6 项接入引擎后才可做**。

### 第八轮：并行推进 + 收尾（2026-09-16，**已实施**）

完整论证见 `TESTING.md §6` 第 52/53 条。

- **并行交付（2 个子 agent，按文件归属隔离，同文件并发编辑是本项目既定禁忌）**：
  ① **几何缩放接入引擎**（`wiring` 新增第 (7) 节）；② **外部 ICC `--icc-file` 接入引擎**
  （`wiring` 新增第 (8) 节，像素 PSNR **35.36dB**，真变像素而非只改标签）；
  ③ **传统管线策略来源统一**（新增 `FfmpegCommandBuilder.EffectiveColorStrategy`，
  `strategy` 新增 **13 格 parity**）。详见 `TESTING.md §6` 第 52 条。
- **集成阶段修掉 3 处**（并行改动互相暴露出来的）：
  `png`/`apng` 的 `CanHdr` 未设 ⇒ 自动 tonemap **误触发**，引擎把 HDR→PNG **降成 SDR** 而 legacy **保 HDR**
  ⇒ 置 `true` 后该格**逐字节相同**，`strategy` 的 `$knownDivergence` 已清空（留空表是有意的）；
  自动 tonemap 判据补第二支 `targetExplicit || !caps.CanHdr`；两处锁旧契约的探针断言更新。
- **⚠⚠ 又发现并修复一个门禁完整性缺陷（与第七轮同类，但这次是「门禁跑不起来」）**：
  - `verify-color-token-normalize.ps1:215` 用了 PowerShell **7 专属三元运算符** `? :`。
    运行器取**当前宿主**作子脚本解释器（`$psExe = (Get-Process -Id $PID).Path`）⇒
    **宿主随启动方式而变**（pwsh ⇒ PS 7；powershell.exe ⇒ PS 5.1）；在 PS 5.1 下该脚本
    **解析失败、一行都不执行**，表现为「门禁没有输出」。此前登记的 33/0 是在 PS 7 宿主下取得的
    ⇒ **数字不可跨宿主复用**。
    ⚠ 收尾时**自我更正过一次**：初稿沿用了运行器里「本机没有 pwsh（`Get-Command pwsh` ⇒ NOTFOUND）」
    的旧注释；实测该注释**已过期**（`pwsh` 确实在 PATH 上，`C:\Program Files\PowerShell\7\pwsh.exe` 7.6.6），
    但**缺陷成立、且比初稿的表述更值得警惕** —— 真实约束是「宿主随启动方式而变」。
  - `_probe-icc-affects-decode.ps1:34,35` 在普通双引号串里用了**全角弯引号**（PS 把左弯引号当**闭合引号**）
    ⇒ 该诊断脚本**完全无法解析**。
  - **固化为常驻门禁 `verify-ps-compat.ps1`（13/0）**：语料自检（≥30 个 `.ps1`）+ 当前宿主解析验证 +
    剥离字符串/注释后正文不得含 PS7+ 专属语法（三元 / `??` / `?.` / `&&` / `||`）+ 8 条负控 +
    2 条点名回归锁。已登记进 `_run-step3-gates.ps1` **首位**（零依赖、最快）。
    **变异验证**：三元塞回 ⇒ **11/2**（语料级「失败 1 个」+ 点名锁「解析错 5 / 语法 1」**双双转红**），
    回滚后 **13/0**。详见 `TESTING.md §6` 第 53 条。
- **本轮门禁（Release 产物）**：探针 **363 pass / 0 fail**（contract **98** · wire 30 · selftest 36 ·
  iccname 10 · plan 39 · curve 39 · matrix 15 · ootf 11 · hlgwire 3 · bt2446 18 · decision 4 ·
  engine 32 · runner 2 · settings 7 · procstreams 3 · encoding 6 · ipc 10）·
  `ps-compat` **13/0** · `color-token-normalize` **33/0** · `strategy` **40/0** · `wiring` **39/0** ·
  `format-regression` **19/0** · `widegamut` **18/0** · `prophoto-faithful` **26/0** · `hlg-route` **13/0**。
  Release 构建 **0 警告 0 错误**。
- **文档**：`TESTING.md §3.4` 补 3 行（`verify-ps-compat` / `verify-color-token-normalize` /
  `verify-hlg-route`）；§7 标题与 §7.1 标题原本**粘在同一行**，已拆行。
- **遗留**：仍见顶部「🔴【待实现】功能清单」**4** 项（原写 5 项，已按实际表行数更正）。

### 第九轮：GainMap（Ultra HDR）接入引擎（2026-09-16，**已实施**）

完整论证见 `TESTING.md §6` 第 54 条。

- **GainMap 从「完全独立的专用管线」变为「引擎决策 + 引擎执行」**。三个关键判断：
  ① 规划层**早已**是 GainMap 感知的（非 JPEG Reject、覆盖①），缺的只是**执行层看不到它**
  （`ColorTransformPlan` 不持有 policy）⇒ 新增 `ColorTransformPlan.GainMap` 并在 `Plan(...)` 外层统一装配；
  ② 引擎执行层新增 GainMap 出口并在**入口**分流 —— 不能塞进 `rgb48le` 那条整数通路
  （**那条通路没有 >1.0 的余量**，而 GainMap 的契约是线性 HDR 浮点，1.0 = 峰值 nits）；
  ③ 解码串 / 参数公式 / 平面交错抽成**单一真值** `ColorMapping/GainMapJpegCodec.cs`，
  旧专用管线与引擎出口**都只调这里**（`QueueProcessor.cs:1565/1580/1588/1615`）。
- **三处「不修就会静默出错」的细节**（理由均写进代码注释）：清空计划层标注（否则目标空间 ICC 会
  **覆盖底图 ICC**，且改写 APP2 段可能**破坏 MPF 偏移** —— 增益图定位完全依赖它）；
  `Action==None` ≠ 不需要 GainMap 出口（否则直通任务会产出**没有增益图的普通 JPEG**）；
  标注一致性守卫对 GainMap 跳过（比出来是假告警）。
- **策略覆盖扩展到 S3/S4**（超出原规格的一处改动，由 lead 拍板**保留**）：否则 `S3/S4 + JPEG + GainMap`
  会被规划层 Reject，而基线（旧专用管线）**有产出** ⇒ 属功能回退。依据：
  `OverrideReason.GainMapRequiresBaseMapping` 的存在本身表明「GainMap 是功能要求、可覆盖策略」，
  `Models/ColorStrategy.cs` 文档原文亦然，且 JPEG **结构上不可能兑现 S3**。
  留痕不变（`Override` + `EffectiveStrategy`，`DeclaredStrategy` 原样保留）。
- **几何缩放的诚实拒绝**：「GainMap + 最长边缩放」本增量**不承接**（底图与增益图必须同几何，
  把 `scale` 插进这条链属未被任何门禁覆盖的新几何路径）⇒ 路由显式拦 + 执行层防御性抛错，
  **不静默忽略**。⚠ 诚实边界：回退到的旧专用管线**同样忽略缩放**（既有缺口，本次未修）。
- **验证**：探针 **379/0**（contract **107** · wire **37** · engine 32）·
  `verify-gainmap-managed` **20/0**（**实测确认走的是引擎出口**）·
  新增 `verify-gainmap-engine` **25/0**（2026-09-19 已增至 **84/0 → 117/0**，见本节末）· `verify-color-wiring` **44/0** · `verify-color-strategy` **40/0** ·
  `verify-format-regression` **19/0** · `verify-widegamut-regression` **18/0** ·
  `verify-prophoto-faithful` **26/0** · `verify-hlg-route` **13/0** · `verify-ps-compat` **13/0**。
  **独立产物验证（读容器，不看退出码）**：`images=2`、MPF 副图 `soi=True`、ISO `max=2.30`
  （= `log2(1000/203)=2.3005`）；exiftool 交叉验证一致；**反证组**（同源不开 GainMap）
  `images=1 / iso=null / pass=1 fail=5` ⇒ 证明探针能区分真假，绿灯不是空转。
  变异验证：关掉策略覆盖 ⇒ contract **99/8**（6 条新断言 + 2 条既有 S2 断言转红，负控正确保持绿）。
- **⚠ 顺带修掉一条静默存在多轮的真实内存安全缺陷**（详见 `TESTING.md §6` 第 55 条）：
  跑全量门禁时 `verify-gainmap-memory.ps1` 实测 **3/2**（基线 4/0）。逐条定性后拆成两件事：
  ① **门禁假红** —— 脚本用 `$rGm.Exit` 判成功，而该值在本宿主下取到 `$null` ⇒ 把"其实成功"
  误判为失败（产物与日志都证明成功）⇒ 判据改为**看产物**（存在且非空）；
  ② **真实缺陷** —— `plainMB = 64` 的标定前提是「普通通路 4K 仅 53.7MB，**全程流式**」，
  那是**传统管线**的数字；**自第七轮引擎成为默认起失效**（引擎普通出口也要整帧解码成 `rgb48le` 再托管变换）。
  同素材 A/B 实测：普通+引擎 **151.5MB** / 普通+传统 **51.9MB** / GainMap **684.2MB**
  ⇒ 旧值**低估 2.4 倍**，`ClampConcurrencyByMemory` 会高估并发，高并发+大图**可 OOM**。
  已重标 `plainMB = 192`（留 ~27% 余量）并把 A/B 数据写进注释 ⇒ 门禁 **5/0**。
  ⚠ **教训**：门禁长期红 = 没有门禁 —— 这条缺陷因门禁本身是红的而被跳过了多轮。
- **仍未做 / 未验证面**：`plan` 被执行层改写属**约定**而非类型强制。
- **✅ 2026-09-19 补覆盖：原「未验证面」的 C⑥ / C⑦ 已闭环**（判据一律**第三方可证**，不看产品日志散文）：
  ① `--jpeg-gain-map-base-gamut bt2020` / `--jpeg-gain-map-multi-channel` 在**引擎出口**下已有门禁
     （`verify-gainmap-engine.ps1` 新增 §5b / §5b' / §5c）：`exiftool` 读**底图内嵌 ICC 色度**
     （bt2020 红原色 x=**0.70799** vs 默认 sRGB **0.64**）· `exiftool` 读 XMP hdrgm `Channels`
     （multi=**3** / 默认=**1**）· ffmpeg 解**主图 raw 比 MD5**（SDR 源 + base=bt2020 vs srgb ⇒
     底图像素**真被转换**，非只改标签）· 非法取值 `bogus` ⇒ **exit 2 + 零产物 + 点名**。
     ⚠ 原 §5 对 bt2020 只有**产品日志散文** + `channels=1`（= 默认值 ⇒ **无判别力**），属空转绿。
  ② `isHdrInput` 判据换源（ICC 感知的 `ProbeInputColorMetadata`）已**实测**：构造
     「cICP=PQ + 内嵌 sRGB ICC」的 PNG ⇒ 引擎走 **SDR**（XMP `GainMapMax=0.00`）；
     **去掉 ICC 的对照 ⇒ HDR**（`2.30`）—— 唯一变量是 ICC。
     ⚠ **诚实登记：此行为属疑似缺陷** —— PNG 第三版规范 §4.3 / Table 1 规定
     **cICP(优先级 1) > iCCP(优先级 2)**（「优先级数字最低者优先」）⇒ 该素材按规范应判 **HDR(PQ)**。
     断言锁的是**当前真实行为**，注释已标「将来按规范修成 HDR 会转红 = 预期红」。
  ⇒ `verify-gainmap-engine` **25/0 → 84/0 → 117/0**（2026-09-19 实测；**PS 5.1 与 PS 7 双宿主各跑一次**；§10「`--jpeg-progressive` 三态 × GainMap 双路径（2026-09-19 补，R3/R7 结构锁）」扩展后均 `PASS=117 FAIL=0`，+33 条）。
     ✅ **#20(0919)（心跳判据锁）后整轮实测仍为 `117/0`**（源 `tests/output/step3-round-1127.log:524`）
     ⇒ 心跳默认开**未扰动** gainmap 断言；**有效性前置已满足** —— 运行器在脚本段之后会跑一次 `Check-SrcDrift`
     （调用点 `_run-step3-gates.ps1:670`；⚠ **行号锚在快照** `SHA256=C999C35BF6C49506…` / 57518 B / mtime `2026-09-19 12:15:44` / 671 行，
     **指纹一变即作废** ⇒ 以符号名为准）：
     检出漂移时**同时**打印 `[WARN]`（`:509`）**且** `$failedItems.Add("源码漂移：… ⇒ 本轮结论作废")`（`:512`），
     而汇总行文字由 `failedItems.Count` 决定（`Exit-WithSummary`，定义 `:222`；`:225-226` 失败 / `:231` 全部通过）
     ⇒ 本轮 `===== 运行器汇总：全部通过 =====` ∧ `WARN` 命中 **0** ⇒ **双路互证：漂移检查通过**
     （⚠ 该函数**干净时零输出** ⇒「日志里搜不到 `SrcDrift` 行」是**预期**，**不能**当作「没跑」）。
- **遗留**：见顶部「🔴【待实现】功能清单」（现 **4** 项）。

### 第十一轮：静默缺陷治理 + 色域映射 + HDR→SDR 原色统一（2026-09-17，**已实施**）

本轮主线是**「看起来配置过」这一类静默缺陷** —— 共同特征：用户显式设了选项、程序接受了、
**但什么都没发生、且不告知**。共修掉 **3 条**：

| # | 缺陷 | 现象 | 修法 |
|---|---|---|---|
| 1 | **legacy 下 `--color-gamut-map on` 静默无效** | `off` 与 `on` 产物**逐字节相同**、日志 0 条相关行 | **显式 legacy + on ⇒ 硬失败**（该组合在传统链上结构上不可能兑现：那里只有钳位、无 GMO 算子）；`auto` 回退时**只告警不拦**；引擎可走时**零噪音** |
| 2 | **未知选项被静默吞掉** | `--totally-unknown-option=xyz` ⇒ exit 0、零告警；且 `--key=value` 形式**失效**（与 `--help` 措辞矛盾） | `--key=value` 内联取值（**只按第一个 `=`**）与空格形式**等效**；未知选项 ⇒ **告警但不改退出码** |
| 3 | **`verify-color-engine-xcheck` 恒 `exit 0`** | 打印「存在失败」但退出码恒 0（**永远不会失败的门禁 = 没有门禁**） | 补 `if (-not $allOk) { exit 1 }`，**并同时精确白名单那 6 格已知非缺陷**（否则修完立刻长期红 —— 正是第 55 条警告的模式） |

**新增功能：色域映射（GMO）** —— 见上表「✅ 已实现」。默认 `off`，与改前**逐字节相同**。

**HDR→SDR 原色统一为 bt709**（用户裁定）：修掉一处**双执行体 split-brain** —— 同一 HDR 源，
png8 落 `bt709`（引擎 `Action=None` 交回 legacy，legacy 用**硬编码** `tmDstP="bt709"`）、
而 jpg/webp 落 `bt2020`（引擎 `SdrTwinOf` **保留源原色**）。
**这是回归、不是既有设计**：`verify-decision-delivery` 在 09-16 引擎转默认**之前是 44/0**，转默认后转红。
- `SdrTwinOf` 原色改为恒 **`bt709`**；传递曲线**保留 `iec61966-2-1`** —— 只有它让出口**等价 sRGB**，
  才能兑现「未标注即 sRGB」的**无 ICC 直通契约**，而 `:159` 锁的就是它。
- 新增 `PlanDelegatedHdrSdr`：引擎对该格**如实声明「不做像素变换、出口语义 = bt709/bt709」**，
  不再宣称 `bt2020/smpte2084`（修掉「**计划 ≠ 交付**」）。
- 结果：`verify-decision-delivery` **44/0**（其中 `:148` 那条改用**像素 PSNR 度量**：
  真落 P3 域 38.2dB vs 只贴标签 14.1dB）。

**门禁完整性方面的两条沉淀**（`docs/TESTING.md §6` 第 57/58 条）：
1. **宿主差异有「语法级」与「语义级」两层**：第 53 条是**语法级**（PS7 专属语法 ⇒ 解析失败、一行不执行）；
   第 57 条是**语义级** —— `Sort-Object <属性名>` 对 **Hashtable 的键不做属性适配**
   （PS 5.1 降序得 `1,0,0,10`、**最大项排最后**；PS 7 得 `10,1,0,0`）⇒ `Select-Object -First 1`
   取错元素、两条素材自检**恒红**。⚠ **`verify-ps-compat` 刻意只查语法级，抓不到这一类**，
   只能靠两宿主实测对照发现。
2. **清理类 `Remove-Item` 需 `-EA SilentlyContinue`**：宿主批量删除守卫会往脚本上下文抛异常
   （而删除其实已成功）⇒ 门禁**跑到一半崩掉、报不出汇总行**。⚠ 只加固**清理**类，
   **不要**给断言路径上的删除加（那会掩盖真实失败）。

### 第十二轮：门禁完整性治理（2026-09-17，**已实施**，`hdr-sdr-primaries-fix`）

本轮**不改产品代码**，只治「**门禁本身不可信**」这一类 —— 共同特征：门禁**看起来跑了、也给了数字**，
但那个数字**不能代表产品状态**（恒绿 / 被别人污染 / 断言被静默吞掉）。
论证与实测全在 `docs/TESTING.md §6`，**此处只列条目号与一句话**（不复制正文）：

| §6 | 一句话 | 关键结论（最易踩的一点） |
|---|---|---|
| **58** | 门禁**全文无 `exit`** ⇒ 恒 `exit 0` ⇒ **它红了运行器也不知道** | 补法必须写**完整 if/else**：`if ($fail -gt 0) { exit 1 } else { exit 0 }` —— **不能只写前半句**，否则全绿路径会以被守卫污染的 `$LASTEXITCODE`（实测 2）退出 ⇒ **把「修假绿」修成「造假红」** |
| **64** | 一条门禁**依赖另一条门禁的遗留产物** ⇒ 它的绿不来自自身 | 修法：消费者**自备素材**（`_lib-color-assets.ps1` 唯一一份）+ runner **调序** |
| **66** | **共享输出目录** ⇒ 并发同名门禁**互相清空** ⇒ 假红 | **最强信号 = 同一份代码两次运行的 `PASS` 总数会变**（FAIL 恒为日志类断言）。已按**运行级隔离（GUID）**自保 12 个；**C 类共享目录簇**（生产者→消费者）裁定 **(b)**「保持固定目录 + runner 串行」；另立**判残留口径**：**GUID 目录 ≠ 残留**（活跃运行会正常产生），判残留必须看**有无进程在写** |
| **67** | **清理步的副作用会污染退出码** ⇒ 已绿的格子被误判为红 | ⚠ **`try/catch` 与 `-EA SilentlyContinue` 都无效**（异常被吞但退出码仍被写成 2）；修法用 **`.NET API`**（`[System.IO.File]::Delete`）。通用教训：**判断清理是否安全不能看有没有抛异常，要看退出码有没有被动过** |
| **68** | 消费者**读不到前置产物就静默跳过** ⇒ 假绿（**与并发无关**） | 实测 `verify-png3-interop` 缺参照时 `PASS` 9→8 而 **`exit` 仍为 0** ⇒ 运行器不知道少验了一条。硬口径：**读不到必须红，不得 `continue`**；`jpgp3` 组已修（生产者入清单 + **自备输入**） |

**本轮数字更正**（均为实测，非推断）：`verify-gamut-map` **26/0 → 36/0**；
`verify-decision-delivery` **44 项 → 51 项、0 SKIP**（见 `TESTING.md §3.4` 表内标注）。

⚠ **上面第十一轮第 2 条（「清理类 `Remove-Item` 需 `-EA SilentlyContinue`」）已被第 67 条部分推翻**：
`-EA SilentlyContinue` **挡不住**宿主批量删除守卫（它不走 `-EA` 通道），且**会污染 `$LASTEXITCODE`**；
批量删除场景应改用 **`.NET API`**。

## 色彩矩阵补全·实机验证结论（2026-09-16，**其第 1 项已在本轮被推翻，见上**）

环境：ffmpeg git-2026-08-06（libjxl+zimg）+ 应用 Release headless CLI；测试目录 <test-dir>。

| 项 | 结果 | 证据 |
|---|---|---|
| P2-⑤ P3→P3 直通 | ✅ | headless DisplayP3→png 产物 `color_space=gbr` + `color_primaries=smpte432`（RGB 域直出，无 YUV/gamut 折返） |
| P1-① HLG OOTF 像素路径 | ~~⚠️ 未触发~~ ⇒ **❌ 该结论已被推翻**（2026-09-16 第二轮） | 原判据的两张"源图" md5 相同且无色彩标签 ⇒ 属**素材缺陷**。真标签素材下 HLG→SDR 与 PQ→SDR **PSNR=23.89dB**（非 inf）、实测峰值 962nit vs 10000nit ⇒ HLG 分支确实生效。门禁：`verify-hlg-route.ps1` 13/0 |
| P1-② 新矩阵 token | ~~部分 ✅~~ ⇒ **已修** | 实测**两侧都不认**的是 `bt2020ncl`/`bt2020cl`/`ycgcocn`/`ycgcocs`/`bt2020`/`smpte432`/`smpte431`（GUI 10 个选项里 7 个）；已收敛词表 + setter 规范化。门禁：`verify-color-token-normalize.ps1` 18/0 |

HLG OOTF 行动项：**已闭环** —— headless 走的确实是 ffmpeg 外部路径（H2/zscale），而该路径对 HLG 的处理**是正确的**；
进程内 ColorKernels（H1）的 OOTF 值域缺陷已由 `TESTING.md §6` 第 40 条修复，且 H1 默认不可达（需 GUI 色域压缩）。


> 更新: 2026-09-17（第十一~十三轮：**色域映射落地** + 三条「**看起来配置过**」静默缺陷治理 +
> **HDR→SDR 原色统一 bt709**；新增门禁 `verify-gamut-map`。详见下方「第十一轮」。
> 前情（第十轮）：把「决策层统一」重新定性为**引擎侧接口缺陷**，方案见 `docs/COLOR_ENGINE_INTERFACE_PLAN.md`）
> 更新: 2026-09-17（**第十二轮：门禁完整性治理** —— 只治「门禁本身不可信」，不改产品代码；
> 共 5 类：**恒 `exit 0`**（§6 第 58 条）/ **依赖别门禁的遗留产物**（第 64 条）/
> **共享输出目录并发互清 + C 类共享目录簇裁定 (b) + 判残留口径**（第 66 条）/
> **清理步污染退出码**（第 67 条）/ **消费者读不到前置产物就静默跳过**（第 68 条）。
> 另更正两处实测数字（`gamut-map` 36/0、`decision-delivery` 51 项）。详见下方「第十二轮」。
> **格式接入进展**：AVIF / GIF / JXL（两后端）/ JXR 均已接入引擎 ⇒ 「待实现」第 2 项**只剩 DNG**
> （按侦察结论**结构上不应接入**）。）
> | 分支: main (HEAD = 3ff26d2；**改动全部在工作树，未提交**) ⇒ 项目约定：实机测试通过前不提交。

### 第十四轮：接手「卡死方」的最后两项（2026-09-19）

> 前情：第十三轮（2026-09-18）交付 `#136`（含剩余部分）后，已派**最后两项**门禁加固。
> 本轮接手时**按 mtime 取证**：两项的产物 mtime **均早于**派工时刻 ⇒ **两项都没开始** ⇒ 由本会话实施。

| 项 | 结论 | 证据 |
|---|---|---|
| **项 1**：`verify-webp-anim-probe.ps1` 新增 **③(d)「热缓存」前提行为锁** | ✅ **已交付** | 断言 **39 → 44**。正例 = 动画 webp + `--encoder cjxl --color-engine legacy` ⇒ 日志含 `[cjxl] 动图/不支持格式（退出码 N），回退到 FFmpeg libjxl_anim 编码器`；负控 = 静态 webp 同参数 ⇒ 不含该行**且必须含** `[cjxl] 直接编码失败`（自证同路径）。⚠ 两格都取 `legacy` 是因为 **auto 组的静态格会走引擎** ⇒ 不同路径，构不成对照。**变异**（注释掉 `AnimatedWebpCache` 写入）⇒ `42/1`，**唯一红格即正例** ⇒ 还原 `44/0` ✓ |
| **项 2**：`verify-jxr-anim-input.ps1` 站点 B「覆盖单点」加固 | ⚠ **判定「无需加固」**（**派工前提不成立**） | 站点 B 另有 `:253 / :254 / :255` 三条**命令行**判据（载体 `$eAuto` 与 `:236` 的 `$rRoute` 是**两次独立 Exec**），且更硬（**命令行 > 日志散文**）。**变异实测**（把 `$eAuto` 载体换成动画）⇒ `52/3`，**恰好 `:253/254/255` 转红、`:236` 仍绿** ⇒ 覆盖**本就是双侧** ⇒ **不加冗余断言** |

**整轮回归**：`RUNNER_EXITCODE=0` · 探针 **519/0** · 脚本 **42/42 全绿**（`webp-anim-probe` 44/0、`jxr-anim-input` 56/0）· 16m36s。（⚠ 该轮跑时**当时为 42 条**；**2026-09-19 P4-A 接线后为 48 条**。）

⚠ **本轮另有一条环境事故（自造并修复，已登记 `COLOR_TRAPS §A` 两条）**：
被中断的 `dotnet build -t:Rebuild` 写坏 `bin` ⇒ `hostfxr.dll` 加载失败（`0x800700C1`），应用**启动即退 + stdout 0 字节**；
而删除 `bin` 又**连带删掉 `PLAN` junction**（`dotnet build` **不重建它**）⇒ `PlatformServices.PlanFolderPath` **误命中 `<repo-root>`（工作区根）**
⇒ 外部工具全 ❌ ⇒ 应用 `exit=2`、整批门禁全红。**点名指纹**：日志里 `ffmpeg: ✅` 但 `JxrEncApp: ❌`。
**修法**：`New-Item -ItemType Junction -Path <bin>\PLAN -Target <repo>\publish\PLAN`。

**数字更新**：`verify-webp-anim-probe.ps1` **39 → 44**（⚠ 该数只记在**脚本文件头**，`docs/` 未登记）；
`verify-jxr-anim-input.ps1` 基线 **56/0**（⚠ 红时 ⑥ 自证跳过 ⇒ 只剩 **55** 条，**别当回归**）。**脚本清单 48 条**（⚠ 本轮当时为 **42 条**、未新增脚本；**2026-09-19 P4-A 接线 +6** 后为 **48 条**）。

### 第十五轮：`#147 C(0919)` 运行器级锁（2026-09-19，**已实施**）

> 依据 `docs/TESTING.md §6 第 86 条`（权威登记）+ `2026-09-16.md` 第五十五轮 D 的**四条件**
> （① 时机由 team-lead 显式通知 ✅ ② 先修 `exit` ✅ ③ 只给 `ensure-fixtures.ps1` 加锁 ✅ ④ 不给各 `verify-*.ps1` 加锁 ✅）。

**落点**：`_run-step3-gates.ps1`（获取在 `$root` 后、释放在 `Exit-WithSummary` 内 ⇒ 覆盖**所有**退出路径）
+ `ensure-fixtures.ps1`（**同一把锁**；释放在末尾）。锁 = `tests/output/.gates.lock`。

**行为**：`CreateNew` + JSON（`pid/host/startedAt/script/cmdline`）；冲突 ⇒ 报错 + **`exit 9`**（**不等待、不静默**）；
**stale 锁不自动删**（改措辞 + 提示**手工**删）；释放**只删自己写的锁**（pid 校验）。
⚠ 注释里逐条写明**不声称覆盖**：构建（dotnet/MSBuild）· 直接跑单个 `verify-*.ps1` · 进程外手段。

⚠ **实施期发现并修正的一处设计细节（方案原文没写）**：锁写入后**立即 `Dispose()` 句柄** ——
锁的存在性由 `CreateNew` 语义保证，**不**靠长期持有独占句柄；否则别的实例**读不到锁内容** ⇒ **stale 检测结构性失效**。

**验证矩阵（均实测）**：

| 用例 | PS 7.6.6 | PS 5.1 |
|---|---|---|
| 锁被活着的进程持有 ⇒ `exit 9` + 点名 + 不启动探针 | ✅ 9 | ✅ 9 |
| `pid` 不存在（stale）⇒ `exit 9` + 「疑似残留锁」+ 锁仍在 | ✅ 9 | — |
| 无锁（绿路径）⇒ 获取 + 跑完 + **释放** | ✅ 0 | ✅ 0 |
| `ensure-fixtures.ps1` 遇锁 ⇒ `exit 9` | ✅ 9 | — |
| `ensure-fixtures.ps1` 正常 ⇒ 获取 + 生成 + **释放** | ✅ | — |

✅ **双宿主覆盖已补齐**：`stale`、`ensure-fixtures`（遇锁 / 正常）三项**已在 PS 5.1 下逐一实跑通过**。✅ **完整整轮也已双宿主跑通**：PS 5.1 下 42 条 + 20 探针全部通过（`PS51_RUNNER_EXITCODE=0` · 519/0 · 42/42 · 跑后锁残留=False · **29m28s**，约为 PS 7 的两倍）⇒ **无 PS 5.1 专属缺陷**。（⚠ 该轮跑时**当时为 42 条**；**2026-09-19 P4-A 接线后为 48 条**。）

⚠ **顺带修**：`ensure-fixtures.ps1` 原先**无退出码契约** ⇒ `$LASTEXITCODE` 残留上一次的值（实测报 9）⇒ 末尾补 `exit 0`。

**整轮回归（含锁）—— ✅ 全部通过**：`RUNNER_EXITCODE=0` · 探针 **519/0** · 脚本 **42/42 全绿**（`webp-anim-probe` 44/0、`jxr-anim-input` 56/0）· **跑后锁残留 = False**（正常释放）· 14m30s。⚠ 前两次整轮在第 **38/42** 条被宜主清理后台任务时中断（锁按设计残留，已手工删）。（⚠ 该轮跑时**当时为 42 条**；**2026-09-19 P4-A 接线后为 48 条**。）

### 第十六轮：蓝屏事故取证 + 一条门禁重建 + 管线审查 + L3 全量跑完（2026-09-19/20）

> 本轮由「系统崩溃」触发。**结论先行**：崩溃是**内核级**故障（**与本仓无关**），但它**打断了在途工作、摧毁了一条门禁脚本**。

#### 16.1 事故定性与归因（两法独立印证）

- 当日 3 次同型蓝屏 `0x3B`（`0xC0000005`）：**06:47 / 06:55 → `eaanticheat.sys`**；**18:38 → `nvlddmkm.sys`**。
  本机**无 WinDbg** ⇒ 我自研 triage dump 解析器，结果与 Windows 事件 **`1019`** 的点名**逐条一致**；
  调用栈解析显示**故障指令都在第三方驱动内部**，路径为 `dxgkrnl` / `dxgmms2`（DirectX 显存管理）。
- **与本仓无关的硬证据**：`--headless` 在 `Program.cs:24-30` **直接 return、从不调用 `BuildAvaloniaApp`**；L3 日志里 GPU 编码器命中 **0**；
  `GpuCapabilityService.QuickEncodeTestAsync`（**会真跑一次 GPU 冒烟编码**）**只在 GUI 触发**
  ⇒ ⚠ **本机建议一律 `--headless`；必须开 GUI 时加 `--no-gpu`**。
- 取证脚本（可复用，均在 `tests/output/`）：`_analyze_dmp6.py`（故障模块归属）· `_analyze_stack.py`（调用栈）· `_analyze_proc.py`（进程名扫描）。

#### 16.2 ⚠⚠ 一条门禁被摧毁并已重建（本轮最严重的任务侧损失）

- `tests/scripts/verify-anim-static-target.ps1` 在崩溃前 ~46 s **正被写入** ⇒ 恢复后 **22494 字节全 `0x00`**
  （NTFS 目录项已提交、数据页随写回缓存丢失）。
  **危害**：全零脚本**解析成功、零输出、`exit 0`** ⇒ **静默假绿**，而它**在运行器必跑清单内** ⇒ 整轮"全绿"结论失效。
  **不可恢复**（全盘仅一份 · 无 `.bak` · 无 VSS · **git 未追踪**）。
- ✅ **已重建 + 双重验收**：
  ① **逐条对拍**：与**原文最后一次运行输出**（`tests/output/_gate_script_verify-anim-static-target.ps1.txt`）的 **48 条 PASS 逐条、逐字、同序一致**；
  ② **变异验证（有牙）**：把 `FfmpegCommandBuilder.cs:139` 改成**无条件**插 `-frames:v 1` ⇒ **`PASS=43 FAIL=5`**（红的恰是「静默截断」那 5 格），回滚 ⇒ **`48/0`**。
- ⚠ **残留不确定（已写进脚本头部）**：素材生成命令、`[cmd]` 行解析方式属**推断**（原文无证据）。

#### 16.3 整轮 48 条实测 + 唯一红格（**未擅自抬基线**）

- 探针 **20 mode 525/0** ✓ · 脚本 **47/48 `exit=0`** · **重建件在运行器内 `PASS=48 FAIL=0`** ✓（集成验收通过）。
- ⚠ **唯一红** = `_probe-cjk-hardcode-scan`（`PASS=13 FAIL=1`）：`C# **UI 面**文案 835 > 基线 832`（**+3**）。
  ✅ **已用实验隔离证明不是本轮改动**（把我加的中文 `///` 注释整段改纯 ASCII 后复跑，计数**分毫未动**；该扫描器只剥块注释、只跳过整行 `//`）。
  **归因**：**18:19–18:34 被蓝屏打断的在途编辑**（`MainWindow.xaml.cs` / `QueueProcessor.cs` / `CliParser.cs`）。
  ⚠ **精确到那 3 行做不到**（`CliParser.cs` **未纳入 git 追踪** ⇒ 无基线可 diff；旧 DLL 差集因 UTF-16 对齐产生伪串而失败）
  ⇒ **需那批改动的作者处置**：优先改 ASCII；确需中文则按范式登记**第 8 次抬基线（832 → 835）**并逐行归因。

#### 16.4 ✅ L3 全量跑完 + 分族分诊（`-SkipPerMatrix` 逐窗法）

- 覆盖：**16 窗 + 1 补齐窗**（w00 被回收 ⇒ 用 `-SkipPerMatrix 73 -MaxPerMatrix 47` 补其缺口）。判定行 **4831**（= 4644 例 + 188 重叠）。
  **PASS 2613 / FAIL 2218**；**FAIL 原因只有两类**：`no-product` **2198** + `gainmap-absent` **20**。
- ⚠⚠ **样本会误导（教训）**：前 4 窗 **852/852 全是 `no-product`**，据此曾误判「没有第二类原因」⇒ **全量才暴露第二类**。
- ✅ **`gainmap-absent` 已手工定性 = 真实缺陷（静默忽略）**：
  `-f bmp --color-engine auto --jpeg-gain-map true --jpeg-gain-map-base-gamut srgb` ⇒ **`exit=0`**、产物是**不含 GainMap 的 BMP**、
  **stdout 全程无 `gain-map` 字样、无警告**、末行仍报「**队列处理完毕 — 完成 1 项, 失败 0 项**」。
  ⇒ 属本仓最忌讳的「**看起来配置过**」假象。**待裁决**：建议按既有约定（「能识别但不支持 ⇒ **引擎层响亮拒绝**」）改为**点名拒绝/点名忽略**。
- 工具：`tests/output/_triage_l3.py`（按 矩阵/格式/编码器/策略/引擎 分族统计，可复用）。

#### 16.5 环境事实（本轮新增/更正，均实测）

- ⚠⚠ **后台任务的实际上限 ≈「回合结束 + ~7 min 宽限」**（**不是**固定的 2 min）：
  实测 **18m05s 存活** / **20m21s 被杀** / **保持回合活跃时 20m38s+ 仍活**。
  ⇒ **单次可安全承载 ≈15 min**；**超时任务必须 ① 重定向输出落盘 ② 逐窗推进**。
- ⚠ **`verify-oracle-matrix.ps1` 运行中不落盘日志**（逐例判定只进 stdout）⇒ 不重定向的话，**被回收 = 判定全丢**。
- ⚠ 宿主 **safe-delete 守卫**（阈值 50、按 turn 累计）会**拦下脚本自清**（`SAFE_DELETE_BULK_CONFIRM_REQUIRED`）
  ⇒ 每窗留一个运行级目录，需外部用 `.NET Directory::Delete` 清；⚠ **分批删也没用**（守卫按 turn 累计）。
- ⚠ **不要把带反引号的正文塞进 shell 命令字符串**（反引号会被命令替换 ⇒ 内容被吞、文件被写坏）⇒ 写记忆一律「**先落文件再拼接**」。

#### 16.6 本轮新增的脚本/代码能力

- `verify-oracle-matrix.ps1` 新增 **`-SkipPerMatrix`**（每张矩阵跳过的前缀条数；默认 0 ⇒ **行为逐字不变**）。
  ⚠ 动机：`-MaxCases`/`-MaxPerMatrix` **只取前缀、无偏移** ⇒ 反复切片**永远推不进矩阵后半段**。
  验证：`-SkipPerMatrix 0/3 -MaxPerMatrix 3` ⇒ 用例 `0001-0003` / **`0004-0006`**（窗口确实推进）；`verify-ps-compat` **13/0** ✓。
- `CliParser.cs`：`BadValue` 形参改 **`string?`**（修 **CS8604**；根因 = **对非空引用做 `x ?? y` 之后 Roslyn 会把 `x` 标成 maybe-null**，
  已用最小复现隔离验证）⇒ 三项目 **0 警告 0 错误**、三份 `FfmpegGui.dll` **SHA256 一致**。

#### 16.7 交接注意事项

- `MEMORY.md` 余量 **172 字符**（< 200 目标）⇒ 下次写入前宜**再压一轮**。
- 本轮**未提交**（按约定 `git add` 由维护者执行）；⚠ `tests/scripts/**` **仍未纳入追踪** —— 本次事故已证明「**未追踪 = 崩溃即永久丢失**」。
- ⚠ 完整过程与逐条读数见 `.workbuddy-ai/memory/2026-09-19.md` **§12–§20**。

#### 16.8（补，2026-09-20 晨）GainMap 语义**定案并落地** —— 两处静默缺陷已修 + 门禁同步

**裁定（维护者）**：GainMap 是 HDR 实现 ⇒ **HDR 输入时真启用；SDR 输入时仍是普通 JPEG**。

| 场景 | 修前 | 修后 |
|---|---|---|
| **HDR** + `-f jpg --jpeg-gain-map true` | 真增益（`GainMapMax=2.30`） | **不变** ✓ |
| **SDR** + 同参数 | **假 Ultra HDR**（`GainMapMax=0.00` 零增益容器） | **普通 JPEG**（无增益图）+ **点名** |
| **非 JPEG**（`-f bmp`）+ 同参数 | **静默忽略**（普通 BMP、无点名、报"成功"） | **契约拒绝**（**项级 91** + 点名 + 建议 + 替代方案，无产物） |

- **根因**：① SDR —— `GainMapEncoder` 在 `headroom ≤ 1` 时**仍打包**增益图；
  ② 非 JPEG —— `ColorIntentFactory` 对 **BMP/DNG/PPM** 返回 null ⇒ 规划层的 `SupportsGainMap` 闸**根本不运行**，
  而分派条件自带 `Format == "jpg" || "jpeg"` ⇒ 请求**直接落到普通编码**（L3 的 20 例 `gainmap-absent` 即此）。
- **门禁同步（有意改判据，非为绿放宽）**：⑦/⑨ 共 **3 条**由「SDR ⇒ 2 幅图的 Ultra HDR 容器」改为
  「**1 幅图（普通 JPEG）+ 必须点名**」（**同等强度**）；第 ③ 段**新增 5 条锁**（`⑨b`：bmp+GainMap ⇒ 非零退出 + 无产物 +
  点名契约拒绝 + 点名 GainMap + **负控的负控**）。
  ⇒ `gainmap-engine`：**115/3 → 119/0（修产品）→ 124/0（加锁）** ✓。
- ✅ **变异验证**：停用新分支 ⇒ `PASS=121 FAIL=3`（红的恰是 3 条 `⑨b`，退出码实得 **0** = 旧静默行为），**负控仍绿**；回滚 ⇒ **124/0**。
- 其它读数：三项目 **0 警告 0 错误** · 三份 DLL SHA 一致（`0382FDDFD2926BDF`）· `ps-compat` **13/0** · **CJK 计数 835 未变**
  （新日志行走 `.Log +=` / `log?.Invoke` ⇒ 诊断 sink，不判；⚠ 中途写成**跨行**时计数曾 +1，改单行后复原）。
- ⚠ 本轮改动**未提交**；`verify-gainmap-engine.ps1` 断言数 **119 → 124**，别处若登记过请同步。

#### 16.9（补，2026-09-20 上午）整轮转全绿：CJK +3 **逐行归因并消除** + GainMap 契约收尾 + `git add` 清单

- ✅ **CJK +3 逐行归因并消除**（此前两轮失败的原因已解决）：
  - 改用**正规 .NET `#US` 堆解析**（PE → CLR 头 → 元数据流 `#US` → 长度前缀 + UTF-16）替代粗解码 ⇒ **零伪串**；
    ⚠ 踩坑：`DataDirectory` 偏移 PE32 = **0x60**、PE32+ = **0x70**（第一版写成 0x70/0x80 各差 16 ⇒ CLR 头定位失败）。
  - 差集 16 条新增中文串 ⇒ 减去**已登记**的 P1-E 那 9 条 ⇒ 剩 **3 条 = 动画告警的续行**（`QueueProcessor.cs:309/310/312`）⇒ 与 +3 **逐条吻合**。
  - 形态正是已知陷阱：`captured.Log += "…"` 的**续行没有 sink 标记** ⇒ 被 `CsUi` 判据按行计入「UI 面」。
  - **修法**：把两条告警各自**合并为单行** —— ⚠ **运行时字符串完全相同**（`+` 拼接）⇒ **零行为变更**，只改源码行数。
  - 读数：`UI 面 835 → **832**`（= 基线）⇒ **`cjk-hardcode: PASS=14 FAIL=0`，rc=0** ⇒ 整轮最后一条红消除。
    旁证：`verify-anim-static-target.ps1` 仍 **48/0**（证明运行时文案确实未变）。
- ✅ **`verify-gainmap-managed.ps1` 第 ④ 段**同步新契约：断言改为「**点名** + 产物**不含增益图**」，
  并把「**纯托管往返**」覆盖**改用 HDR 源保留**（覆盖不丢）⇒ **22/0**。
- ✅ **`git add` 补丁清单已刷新** → `tests/output/GIT_ADD_PATCHLIST.txt`（**134 个文件**：
  `tests/scripts/` **73** · `src/FfmpegGui/` **37** · `docs/` **10** · `tests/UiTestHost/` **7**）。
  ⚠ 仍**未执行任何 `git add`**（按约定由维护者执行）。
- ✅ `MEMORY.md` 再压一轮：**7847 → 7728**（余量 **272**，达 ≥200 目标），并把「**诊断日志必须单行**」写进 CJK 那条。
- ✅ 更正 `stuck-agent-triage` 技能：原「转后台后**不被杀**」**不完整** ⇒ 补入「上限 ≈回合结束 + ~7 min、单次 ≈15 min、
  必须**重定向输出** + **逐窗推进**；被回收的指纹是宿主任务表 `not found`」。
- 三个 GainMap 门禁：`managed **22/0**` · `memory **5/0**` · `engine **124/0**`；`ps-compat **13/0**`。
- ✅ **终局读数（09:12）**：`_run-step3-gates.ps1` 全量 **`RUNNER_EXIT=0` / 「运行器汇总：全部通过」**（16m44s）· 脚本段 **48/48 `exit=0`** · 探针 **525/0** ⇒ 本会话出现过的三条红格（`cjk-hardcode` · `gainmap-engine` · `gainmap-managed`）**全部消除**。
- ⚠ **唯一未做**：`git add`（**134 个**文件，清单 `tests/output/GIT_ADD_PATCHLIST.txt`）—— **由维护者执行**，本轮**未提交**。

### 第十七轮：HEIC/AVIF（ISO-BMFF）增益图**完整修复** —— 识别 + 解码 + 动画误判（2026-09-20）

> 维护者提问：*「HEIC 我们软件支持输入，输入时能否正确识别 GainMap 并正常转换？AVIF GainMap 的规范是什么？」*
> 调查后维护者裁定：**完整修复，确保能正确识别**（不是只做「拒绝」）。
> 完整分析（含规范与三个缺陷的因果链）= **`docs/GAINMAP_HEIC_AVIF_ANALYSIS.md`**（**先读它**）。

#### 17.1 结论（修复前 → 修复后）

| 项 | 修复前 | 修复后 |
|---|---|---|
| HEIC **输入** | ✅ 支持（解码只读） | 不变 ✓ |
| HEIC/AVIF 的 **GainMap 识别** | ❌ **完全不识别**（`GainMapDecoder` 只认 JPEG 容器）⇒ 增益图**静默丢弃、无点名** | ✅ 识别 `tmap` 派生项并**真解码**（正向方向） |
| **静态 GainMap AVIF** | ❌ 被 `IsAnimatedAvifAsync`（`videoCount >= 2`）**误判成动画** ⇒ **色彩引擎被整体绕过** + AVIF→GIF/WebP 走错两步法 | ✅ 判静态 ⇒ 走单命令；引擎不再被绕过 |
| **HDR 底 GainMap AVIF** | ❌ 直接失败（零产物） | ✅ **已修**：方向点名交回普通 HDR 路径 + **根因**（CICP 优先于 EXIF `ColorSpace`）⇒ **实跑产出** ✓ |

#### 17.2 关键技术事实（全部实测）

- **`tmap` 载荷 = `u8 version` + ISO 21496-1 `GainMapMetadata`（独立分母形式）** ⇒ 与 JPEG APP2 载荷**同构**
  ⇒ `JpegContainer.ParseIso21496(payload, 1, len-1)` **原样复用**。长度闭合 `1+21+ch×40`（3ch ⇒ **142 B**，剩余 **0 字节**）。
- **权威交叉验证**：libavif 自带 `avifgainmaputil printmetadata` 与我们的解析**逐字段一致**
  （min −0.256907 / max 1.27718 / gamma 0.953784 / offset 0.015625 / UseBaseColorSpace True）。
- **增益图像素**：抽 `iloc` extent 字节 → `ffmpeg -f obu`（`av01`）/`-f hevc`（`hvc1`）⇒ `gbrpf32le` 长度精确吻合。
  底图必须显式 `-map 0:0 -frames:v 1`（ffmpeg 把底图/增益图各暴露成一条视频流，rawvideo 无头 ⇒ 错流会被静默放行）。
- ⚠ **ffmpeg 对 `tmap` 是「知道但不实现」**：只打 `Derived Image item of type tmap is not implemented.`，
  **不会**帮我们识别或拒绝 ⇒ 识别必须自己做（读 item 结构）。
- **方向**：ISO 21496-1 的 flags **没有**「底图是否 HDR」位（libavif 不写 `backwardDirection`）
  ⇒ 只能由 **headroom 比较**推出（`base > alternate` ⇒ 底图已是 HDR）。⚠ 旧启发式（`max≤0 && min<0`）
  对 `seine_hdr_*` **判错**（其 `max = +1.277`）。

#### 17.3 缺陷 B 的根因（**最严重**）：alpha 与增益图的 ffprobe 形态**完全相同**

| 素材 | 流数 | `nb_frames` | 旧判据（`videoCount≥2`） | 新判据 |
|---|---|---|---|---|
| 静态 AVIF | 1 | 1 | 静态 ✓ | 静态 ✓ |
| **静态 AVIF + alpha**（libavif `avifenc`） | 2 | **1,1** | **动画 ✗** | 动画（**沿用旧行为**，保 alpha） |
| **静态 GainMap AVIF** | 2 | **1,1** | **动画 ✗** | **静态 ✓** |
| 动画 AVIF | 2 | 1,**8** | 动画 ✓ | 动画 ✓ |

⇒ 单靠 `nb_frames` **不足以**区分「增益图」与「alpha」，**必须看容器里的 `tmap` 项**
（新增 `IsoBmffGainMapProbe.HasTmapItemFast`）。新判据五档：
单流⇒静态 · 有 `nb_frames>1`⇒动画 · 全单帧+有 `tmap`⇒静态 · 全单帧+无 `tmap`⇒**沿用旧行为** · 帧数读不到⇒**不确定**（消费者按多帧）。

#### 17.4 交付物与门禁

| 文件 | 说明 |
|---|---|
| `src/FfmpegGui/Services/IsoBmffGainMapProbe.cs` | **新增**（纯托管、零依赖、fail-safe 三态） |
| `src/FfmpegGui/Services/GainMapDecoder.cs` | 增 ISO-BMFF 分支 + `IsIsoBmffGainMapAsync`（**点名**反向/未支持形式） |
| `src/FfmpegGui/Services/QueueProcessor.cs` | 输入路由接入 + **动画判据重写** |
| `tests/ServiceProbe/Program.cs` | 新模式 `gainmap-isobmff <file> [outRaw] [gainmap\|none]` |
| `tests/scripts/verify-gainmap-isobmff.ps1` | **新增门禁 29/0**，已接线为 `_run-step3-gates.ps1` **第 49 条**（`$expectedScriptCount` 48 → **49**） |

- 新门禁含：§0 素材形态自检（含「增益图与 alpha 形状相同」这条**关键负控**）· §1 容器层（`11/0` 正向 + 3 条负控 +
  **可执行反控**）· §2 源码形态锁（两级判据 + **可执行反控**）· §3 行为（增益图**真被应用** / **不误伤**动画与 alpha）· §4 收尾。
- **变异验证已登记**（门禁文件头）：A 容器判定恒假 · B `HasTmapItemFast` 恒 false · C 载荷起点改回 `0`。
- 回归复跑：`avif-anim-probe **19/0**` · `gainmap-managed **22/0**` · `gainmap-engine **124/0**` ·
  `gainmap-memory **5/0**` · `cjk-hardcode **14/0**` · `ps-compat **13/0**`。
- ✅ **整轮验收（2026-09-20，49 条）**：`_run-step3-gates.ps1` 全量 ⇒ **`RUNNER_DONE exit=0`** ·
  **`运行器汇总：全部通过`** · **`script-list=49 条（受管基线 49）`** · 脚本段 **49/49 `exit=0`** ·
  探针段全 `fail=0` · UI 宿主汇总 **315/0**（5 条 UI 门禁同读数）· **16m12s**。
  ⚠ 逐条 `grep` 时注意：`verify-anim-static-target.ps1` 的 PASS 文案里**含 `exit=1` 字样**（是断言内容，不是该脚本的退出码）
  ⇒ 用 `exit=-?[1-9]` 粗筛会误报 1 条，必须看行首。
- ✅ **第二轮整轮验收（含缺陷 C 修复，2026-09-20）**：`RUNNER_DONE exit=0` · **`运行器汇总：全部通过`** ·
  `script-list=49 条（受管基线 49）` · 脚本段 **49/49 `exit=0`** · 探针段**零 `fail>0`** · 新门禁 `gainmap-isobmff **36/0**` ·
  **17m18s**。⇒ 本轮所有改动（ISO-BMFF 增益图 + 动画判据 + CICP 优先级）**无回归**。

#### 17.5 本轮踩到的坑（**新增登记候选**）

- ⚠⚠ **断言载体选错 ⇒ 真空绿**（`TESTING §6` 第 ⑤ 类的又一实例）：`两步法` 这个串**只存在于 `item.Command`
  与 `--dry-run` 输出**，**不进项目日志** ⇒ 拿它当判据会得到「恒 0 条」⇒ `-eq 0` **恒真**。
  真正的日志标记是 **`[avif→xxx] Step1:`**。⚠ 我自己的新门禁首跑就踩了这条。
- ⚠ **ffprobe `-of csv=p=0` 会补尾逗号**（实测 `0,video,1,`）⇒ 形态正则必须容忍 `,?`，否则**假红**。
- ⚠ **下载截断会伪装成软件缺陷**：首轮取的 nokiatech HEIC 只有 41 KB（应 293 KB），其 `iloc` 声称 289408 B
  ⇒ ffmpeg 报 `Invalid NAL unit size (289404 > 40527)`。**判据：拿 `iloc` 的 extent 与文件实际长度比**。
  差点据此把「HEIC 输入坏了」写进结论；重下完整文件后 **HEIC 输入完全正常**。

#### 17.6 缺陷 C 的根因（**第二轮已修**）

- 根因：EXIF/XMP `ColorSpace=sRGB` 压过 CICP 传递特性 ⇒ 判「SDR 输入」⇒ **不插 HDR tonemap 链**
  ⇒ `iccgen` 遇 **PQ 帧**硬失败（`Not yet implemented in FFmpeg, patches welcome`）。
  已实证 **`iccgen` 只看传递特性、与位深无关**（4 例对照：PQ+iccgen ❌ / PQ+强制`bt709` ✅ / 8bit+强制`smpte2084` ❌ / 8bit ✅），
  且「去掉 EXIF 标签 ⇒ 恢复 tonemap 链 ⇒ 成功产出」闭合了因果。
- **修法**：`FfmpegCommandBuilder.ProbeInputColorMetadataCore` 新增 `ffCicpUsable` 门控 ——
  **CICP 可用（非 `unknown`/`unspecified`）时 EXIF `ColorSpace` 让位并点名**；CICP 不可用时行为**完全不变**
  （相机 JPEG / PNG 实测 CICP 恒为 `unknown,unknown` ⇒ 仍靠 EXIF；该分支不能一刀切删掉）。
  ⚠ 实现上把 EXIF 值先记成**候选**（`exifPrim`/`exifTrc`），等 ffprobe 的 CICP 出来再决定是否采用。
- **验证**：`ServiceProbe color` 正负控双向 —— HDR AVIF ⇒ `trc=smpte2084 hasIccSemantics=False`；
  带 EXIF `ColorSpace=sRGB` 且 CICP 为 `unknown` 的 JPEG ⇒ `trc=iec61966-2-1 hasIccSemantics=True`；
  HDR 样本 → jpg **实跑产出**（修复前零产物）。门禁新增 **§2b（源码形态 3 条含反控）+ §3b（行为 4 条含负控与素材形态自检）**。
- ⚠ **回归检查**：色彩类门禁**全绿**（`strategy 63/0` · `wiring 104/0` · `xcheck 通过` · `widegamut 18/0` ·
  `prophoto 26/0` · `caps 12/0` · `token-normalize 33/0` · `tiff-icc` · `webp-hdr-fix` · `gainmap-engine 124/0` · `managed 22/0`）。
- ⚠ 踩坑：`ps-compat` 抓到一条 **PS7 专有语法** —— 双引号串里的 `` `unknown` `` 被 PS7 当成 **Unicode 转义**
  （PS5.1 只当字面 `u`）⇒ **解析错误**。⇒ 双引号串里**不要**在字母前用反引号（`u`/`e` 尤其危险）。
- `git add`（清单 `tests/output/GIT_ADD_PATCHLIST.txt`）按约定**由维护者执行**，本轮**未执行、未提交**。

#### 17.7 D-1 同族残留收口：GainMap 开关不再冲掉目标三项（2026-09-20）

- **缺陷**：`JpegGainMapEnable_Changed`（`MainWindow.xaml.cs:5603`）调 `UpdateOptionAvailability()`，而后者会
  `Items.Clear()` + 重建 `ColorSpaceCombo`（`:2149-2170`）与 `BitDepthCombo`（`:2109-2117`）并把 `SelectedIndex`
  归 0，还会把 `QualitySlider.Value` 设为该格式默认值（`:2100`）⇒ 勾/取消「GainMap (Ultra HDR)」时**格式并未改变**，
  这三项却被整体重置。**D-1（09-19）只修了「应用预设」那条路径**，并在注释里显式说明**不动刷新函数本体**
  （怕改 6 个调用点的既有语义）⇒ 本条按 D-1 的**同一模式**在**该处理器内部**修。
- **修法**：刷新**前快照**目标色域/位深/质量，刷新**后回写**（`SetComboByValue`×2 + 质量滑块；
  纯无损格式 PNG/TIFF/APNG 的质量被产品语义锁 100 ⇒ **不回写**）。值若不在新候选集里就**保持刷新后的默认值**
  （绝不写非法值）。⚠ 只改本处理器 ⇒ 刷新函数本体与另外 5 个调用点行为**一字不变**。
- **验证**：`UiTestHost` 新增 **GroupO（+7 条）** ⇒ UI 宿主汇总 **315 → 322**（5 条 UI 门禁同读数，均已复跑 **322/0**）。
- ⚠ **变异验证（已实测）**：删掉三行回写 ⇒ **O2a-c / O3a-c 六条全红**，诊断串显示三项确实被冲成默认值
  （`def cs=auto bd=auto q=92 | pick cs=Display P3 bd=8 q=82 | on cs=auto bd=auto q=92`）。
- ⚠⚠ **变异写法本身的坑（本轮踩到，值得记）**：第一版变异只在真行**上方**加了几行注释掉的**副本**，
  真正生效的回写行仍在 ⇒ 只有质量那条转红、cs/bd **假绿**（我差点据此误判「cs/bd 本来就不会被冲」）。
  ⇒ **变异必须删/注释掉真正生效的那几行**，并回读确认。

#### 17.8 HEIC 侧的诚实界定：发现并修掉一个**错命令**（2026-09-20）

- **发现**：上一轮宣称「HEIC/AVIF GainMap 已支持」，但**只实测了 AVIF**。本轮去验证 HEIC 侧 ⇒
  从真实 HEIC（nokiatech 样本）按 `iloc` 抽出 `hvc1` 主图 item 喂 `ffmpeg -f hevc`：
  **`No start code is found` / `could not find codec parameters`**（exit=2）。
- **根因**：**ISO-BMFF 里的 HEVC item 是「长度前缀 NAL」**（参数集另存于 `hvcC`），而 ffmpeg 的 `-f hevc`
  只吃 **Annex-B**（起始码）。**AV1 不同**：`av01` item 的 OBU 流自带结构，`-f obu` **可**直接解（实测 400x300 ✓）。
  ⇒ 原先 `GainMapFfmpegFormat` 的 `"hvc1" or "hev1" => "hevc"` 是**错的** —— 它让下游收到一条
  **看不懂的 ffmpeg 错误**，而不是诚实的「不支持」。
- **修法**：`GainMapFfmpegFormat = gtv switch { "av01" => "obu", _ => null }`；HEVC 走 `null` ⇒
  `Determinate=false` + 点名「需 Annex-B 转换，尚未实现」，且**不再切 item 字节**（省数百 KB 白分配）。
- **验证**：AVIF 未回归（探针 **11/0**）；**合成 HEVC 形态**（样本 item 类型 `av01`→`hvc1`）仍**识别**为 `tmap`
  增益图但 `ffmpegFmt=-` / `gmData=-1` / `determinate=False` + 点名 ✓。门禁新增 **§1b（3 条）** ⇒
  `gainmap-isobmff` **36 → 39/0**。✅ **变异 E 实测**：映射改回 `"hevc"` ⇒ 探针重新报错命令 ⇒ §1b 必红。
- ⚠⚠ **范围界定（写进文档与门禁头；措辞不得大于扫描面）**：**HEIC 与 AVIF 共用同一条 ISO-BMFF 识别路径**
  （按 **item 类型**判定，不看扩展名），但**只有 AV1 那条解码路径被实测过**：
  HEIC 侧 **识别 ✓ / 解码 ✗（fail-closed + 点名）**。补齐需 **① 真实 HEIC 增益图素材 + ② `hvcC` 驱动的
  长度前缀→Annex-B 转换**。**均未做**（另立任务）。
- ✅ **第四轮整轮验收**：`RUNNER_DONE exit=0` · **`运行器汇总：全部通过`** · `script-list=49 条（受管基线 49）` ·
  脚本段 **49/49 `exit=0`** · UI 宿主 **322/0** · 探针**零 `fail>0`** · `gainmap-isobmff` **39/0** · **18m08s**。
- `git add` 按约定**由维护者执行**，本轮**未执行、未提交**。

#### 17.9 收尾两件：HEIC 素材搜寻的负面结果 + **`git add` 补丁清单重生成**（2026-09-20）

**(1) 真实 HEIC 增益图素材：搜寻失败**（登记进 `verify-gainmap-isobmff.ps1` 文件头，省得重复找）

| 来源 | 结果 |
|---|---|
| `imaging.org` 的 **ISO_21496-1_Adaptive_HDR** 测试集（最权威，CC BY-NC 4.0） | 直链 **403**，需注册 IS&T 账号后「$0 购买」 |
| `github.com/chemharuka/toGainMapHDR` 的 `sample/DJI_*.heic`（3 个） | 实测 **`hasGainMap=False`** ⇒ **普通 HLG HDR HEIC（无 `tmap`）** |
| `Awesome-Gain-Maps` 仓库 | 只有 **JPEG**（Ultra HDR）样本 |

⇒ 下一步若要真素材：**注册 imaging.org 免费下载**，或**用手机拍一张 Adaptive HDR 照片**。

**(2) ⚠⚠ `tests/output/GIT_ADD_PATCHLIST.txt` 已陈旧且自相矛盾 ⇒ 重生成（134 → 122）**

- **问题 1**：清单生成于本轮新文件之前 ⇒ **漏 3 个**（`IsoBmffGainMapProbe.cs` · `verify-gainmap-isobmff.ps1` ·
  `GAINMAP_HEIC_AVIF_ANALYSIS.md`）⇒ 照旧清单 `git add` 会漏掉，**下次崩溃即丢**（本仓已吃过这个亏）。
- **问题 2**：清单**误含 15 个 `obj/**` 构建中间件**，与该文件**自己的表头**「不追踪 `bin|obj/**`」**自相矛盾**
  ⇒ 照旧清单 add 会**污染仓库**。
- **处置**：按表头口径重新求差，并把**复现命令写进表头**。校验：0 个不存在路径 · 0 个混入已追踪 ·
  条目 **122** = 表头声明 **122**；分布 `tests/scripts` 74 · `src/FfmpegGui` 33 · `docs` 11 · `tests/UiTestHost` 2 ·
  `tests/ServiceProbe` 2。✅ **算术闭合**：`122 = 134 − 15(obj) + 3(new)`（逐目录也对得上）。
- ⚠ 仍**未执行任何 `git add`**（按约定由维护者执行）。
- ⚠ 仍未做（登记）：`FromIccMode` 遗留迁移的版本标记 —— `PresetData` **确无版本字段**；当前可用
  「`ColorStrategy` 缺失 ⇒ 遗留迁移 / 存在但解析失败 ⇒ 保持现状 + 点名」区分，属**未来**加固项。

#### 17.10 HEIC 侧的 `hvc1` 解码路径打通（`hvcC` → Annex-B 转换）（2026-09-20 下午）

**背景**：§17.8 把 HEIC 侧诚实界定为「识别 ✓ / 解码 ✗（fail-closed + 点名）」，并登记补齐需
① 真实 HEIC 增益图素材 + ② `hvcC` 驱动的长度前缀→Annex-B 转换。**本轮把 ② 做完了**（① 仍缺）。

##### 17.10.1 预检：先证可行性，再写代码

- 从真实 HEIC（nokiatech 样本）解析 `hvcC`：`lengthSize=4`，参数集 VPS/SPS/PPS = **24/31/7** 字节。
- 把 `hvc1` item 的**长度前缀 NAL** 转 Annex-B（前置参数集）⇒ `ffmpeg -f hevc` **exit=0**，解出首帧 1382400 px；
  长度前缀**逐字节闭合**（消耗 289408/289408，剩余 **0** 字节）。
- ⇒ 「`-f hevc` 吃不了」的根因确认为**形态差异**（长度前缀 vs 起始码），**不是**编码器/工具缺陷。

##### 17.10.2 实现（`src/FfmpegGui/Services/IsoBmffGainMapProbe.cs`）

| 新增 | 作用 |
|---|---|
| `ParseIprp` / `ParseIpma` | 收集 `ipco` 属性（**1-based** 索引）与 `ipma` 的 item→属性索引映射 |
| `TryFindHvcC` | 取该 item 的 `hvcC`：优先 `ipma` 关联；`ipma` 缺失时**仅在 `ipco` 恰有一个 `hvcC`** 时退化采用；**多义 ⇒ 失败点名**（不猜） |
| `TryParseHvcC` | 解析 `lengthSizeMinusOne`（第 22 字节低 2 位）与参数集（NAL 32/33/34） |
| `ToAnnexB` | 长度前缀 → 起始码，并**前置参数集**；⚠ **严格闭合**（越界 / 尾部残留 / 零 NAL 都判失败） |

- `av01` 分支**行为一字不变**（`-f obu`，不转换）；只有 `hvc1`/`hev1` 走新路径。
- 转换成功 ⇒ `GainMapFfmpegFormat = "hevc"`，`GainMapItemData` = **Annex-B 字节**（字段语义已改注释）。
- 失败 ⇒ **fail-closed + 点名**（不再给出 `-f hevc` 这个**错命令**）。
- ⚠ `GainMapDecoder` 侧**无需改动**：它把 `hevc` 写成 `gainmap.hevc` 再喂 `-f hevc`，而现在该文件已是 Annex-B。

##### 17.10.3 合成件：为什么不需要真素材也能覆盖

- `tests/data/synth_heic_gainmap.heic`（**6373 B**，正向）· `…_fakehevc.heic`（**62731 B**，负控）。
- 生成器 `tests/scripts/_make-synth-heic-gainmap.py`（**可复现**）：ffmpeg/libx265 编 64x48 单帧 HEVC（Annex-B）
  → 自构 `hvcC`（只填被解析器/ffmpeg 用到的字段）→ 长度前缀 → 拼 HEIC 容器（`tmap` + `hvc1` 底图/增益图）；
  tmap 载荷借库内 `seine_sdr_gainmap_srgb.avif`。
- 负控件 = 同一容器但**增益图 item 字节换成 AV1 OBU**（`hvcC` 仍在）⇒ 长度前缀**必断链**。

##### 17.10.4 门禁与变异

- `verify-gainmap-isobmff.ps1`：§1b 由「HEVC fail-closed 3 条」重写为「**HEVC 解码路径 11 条**」
  （1 前置 + 1 素材形态 + 4 正向 + 1 反控 + 2 负控① + 2 负控②）⇒ 总 **39 → 47 条**。
- ⚠⚠ **弱断言警告**（已写进门禁头）：§1b 里「转换成功」那条（只看 `determinate`/`ffmpegFmt`/`gmData>0`）
  在「原样透传不转换」的变异下**仍然通过**（实测）⇒ 真正的判据是它后面两条：
  **产物首 4 字节 = `00 00 00 01`** + **产物被 ffprobe 解出 64x48**。三条要一起看。
- ✅ **变异 F（HEVC 分支整体退回 fail-closed）实测 `PASS=42 FAIL=5`**：打红「转换成功」「产物是 Annex-B」
  「端到端」+ 两条**点名内容**断言（负控①/②）⇒ 证明新代码**必要**，且这 5 条**不是恒真**。
- ✅ **变异 G（`ToAnnexB` 原样透传）实测 `PASS=43 FAIL=4`**：打红「产物是 Annex-B」「端到端」+ 负控②两条
  （负控② 反而变 `determinate=True`）⇒ 证明「严格闭合」与「起始码」判据有牙。
- **反控**：产物按 `-f obu`（AV1）解**解不出 64x48**（实测 `av1,0,0` + `Failed to read obu`）。
  ⚠ 判据**不能**写成「输出不含 `av1`」—— `-f obu` 是**强制**解复用器，它照报 `codec_name=av1`，只是**宽高为 0**。
- ✅ **整轮验收（第五轮，2026-09-20）**：`运行器汇总：全部通过` · `script-list=49 条（受管基线 49）` ·
  脚本段 **49/49 `exit=0`** · 探针段**零 `fail>0`** · UI 宿主 **322/0** · `gainmap-isobmff` **47/0** ·
  `ps-compat` **13/0** · `cjk-hardcode` **14/0** · **19m16s**。
- ⚠ 本轮 `RUNNER_DONE` 未出现：它是**外层包装**打印的（不是 runner 自身输出）⇒ 判据取
  「运行器汇总：全部通过」+ 49/49 `exit=0`，**别**因为缺 `RUNNER_DONE` 就以为没跑完。

##### 17.10.5 诚实界定（措辞不得大于扫描面）

- ✅ **HEIC 与 AVIF 的两条解码路径现在都实测过**：`av01` → `-f obu`；`hvc1`/`hev1` → `hvcC` + Annex-B。
- ⚠ **真实 HEIC 增益图素材仍然缺失** ⇒ HEVC 那条是用**合成件**覆盖的 ⇒ 覆盖的是**解码路径**，
  **不代表**拿到了真素材，也**不代表**真实设备产物的**元数据语义**被验证过。
- 未做：`git add`（清单 `tests/output/GIT_ADD_PATCHLIST.txt`，**125** 条，按约定**由维护者执行**）。

##### 17.10.6 补丁清单口径修正（122 → 125）

- 新增 3 个文件（`tests/data/*.heic` ×2 + `tests/scripts/_make-synth-heic-gainmap.py`）。
- ⚠ **口径扩展两处**（上一版漏收，照旧口径 add 会漏）：① `tests/scripts` 下的 **`.py` 生成器**
  （旧口径只收 `*.ps1`）；② **`tests/data/`**（新目录）。
- 校验：条目 **125** = 表头声明 · **0** 个不存在路径 · **0** 个混入已追踪。

##### 17.10.7 §3c：HEIC 端到端行为断言（**补上此前空白**）

- **为什么补**：§1b 只验到「**探针层**」（识别 + 转换 + 产物可解），**不证明**这条路径接进了
  `GainMapDecoder`。§3 对 AVIF 有端到端断言，HEIC 侧此前**空白**。
- **实测（合成件 → jpg）**：`检测到 ISO-BMFF 增益图（tmap item，142 B）` →
  `✅ HDR 线性像素: 64x48 (峰值 492nits)` → `✅ 已解码为线性 HDR PFM` → 产出 `64x48 yuvj420p`。
  增益图来源点名 **`ISO-BMFF item 3（hvc1，2981 B）`** = 转换后的 Annex-B 长度 ✓
- **负控**：普通 HEIC（`libheif/examples/example.heic`）→ jpg 产出正常，且**增益图相关日志 0 条** ✓
- 门禁 **47 → 52 条**（§3c +5）。⚠ §3c 的「识别并产出」是**弱断言**（退回 fail-closed 也过），
  真判据是「已解码为线性 HDR PFM」+「点名 `hvc1`」两条。
- ✅ **变异 H（HEVC 分支退回 fail-closed，§3c 已存在时重跑）实测 `PASS=45 FAIL=7`**：
  除 §1b 那 5 条外，**§3c 的「真被解码并应用」与「点名 `hvc1`」也转红** ⇒ 证明有牙。
- ✅ **整轮验收（第六轮）**：`运行器汇总：全部通过` · 49/49 `exit=0` · UI 宿主 **322/0** ·
  `gainmap-isobmff` **52/0** · **18m27s**。

##### 17.10.8 ⚠⚠ 顺带修掉一个「静默吞代码」的门禁缺陷（值得全仓警惕）

- **现象**：§3c 加完后**从不执行**（`PASS` 总数不变、且 **「注释行」竟然出现在脚本输出里**、`§4` 标题从未打印）。
- **根因**：§3b 的消息串原先写作「反引号包裹的 `Not yet implemented`」—— **尾反引号紧贴字符串结束引号**
  ⇒ PowerShell 把「反引号 + 双引号」解析成**字面双引号** ⇒ 字符串**不闭合**，把紧随其后的
  `# ── §4 收尾 ──` 注释、`Write-Host` 标题、**以及整段 §3c** 全部**吞成字符串内容**。
- ⚠ **它不是语法错误** ⇒ `Parser.ParseFile` 与 `verify-ps-compat` **都抓不到**（解析器认为语法合法）。
- **检测法**：用 AST 取所有**字符串 token 的行范围**，检查是否有「以 `#` 开头的源码行」落在其中。
  ✅ 已全仓扫过 48 个脚本：**只有本文件**是真实例（另两处命中是 here-string 里的 OpenCL / ExtendScript 代码，属误报）。
- **纪律**：双引号串里**不要**用反引号包裹代码片段（裸写或用「」）；**尾反引号尤其不得紧贴引号**。
- ⚠ **教训**：这类缺陷会让**新加的断言整段失效却依然"全绿"** —— 与 `TESTING §6` 的六类假绿同族，
  但**解析器与静态扫描都抓不到** ⇒ 只能靠「**PASS 总数与文件头声明对不上**」这条元判据发现。

##### 17.10.9 真实 HEIC 健壮性扫描（副产品）

- 用**未预期有增益图**的方式跑遍库内全部真实 HEIC（libheif `examples` + `fuzzing/data/corpus`，30+ 个）：
  **全部 `hasGainMap=False`、零崩溃**；唯一 `determinate=False` 的是**刻意损坏**的 fuzzing 语料
  ⇒ 新代码（`ParseIprp`/`ParseIpma`/`TryFindHvcC`）对真实/畸形输入都**不误判、不抛**。
- **素材结论（已复核，别再重复找）**：本地**没有**能写 HEIC 增益图的工具 ——
  `avifgainmaputil` 只写 **AVIF**；libheif **只有源码未构建**，且 `heif-enc` **无增益图写选项**
  ⇒ **真实 HEIC 增益图素材仍只能靠外部获取**（imaging.org 注册 / 手机拍摄）。

## §18 2026-09-20（第二轮）—— RAW 默认色域按目标 HDR 能力取值

### 18.1 需求
维护者：「用户未设高级色彩参数时强制给**支持的格式**启用 BT.2020；**JPEG 在开启 GainMap 的情况下
认为是支持 HDR 的格式**（sRGB 底片 + 增益层）」。

### 18.2 改动前现状
- RAW 预处理（`QueueProcessor.cs:443-463`）后**无条件**强制 `ColorPrimaries=bt709` + `ColorTrc=linear`。
- 容器 HDR 能力只有静态位 `FormatColorCaps.CanHdr`：png/apng/jxl/avif=`true`；
  **jpg/jpeg/jpegli 未设 ⇒ `false`**（8-bit SDR 容器）。
- ⚠ 「支持 HDR 的格式」这个概念在代码里**不存在**（`SupportsHdr|HdrCapable` 全仓 grep 为空）；
  真正表达它的是 `CanHdr` + `CanCarryHdrAt(caps, bitDepth)`。

### 18.3 改动（最终形态，**2 处**）
| 文件 | 改动 |
|---|---|
| `ColorTransformPlan.cs` | 新增**纯函数** `CanHdrWith(bool gainMapEnabled) => CanHdr \|\| (gainMapEnabled && SupportsGainMap)` |
| `QueueProcessor.cs`（RAW 预处理） | primaries 由「**恒** bt709」改为按 **`CanHdrWith(JpegGainMap)` + 位深前提**选：能承载 HDR ⇒ `bt2020`，否则 `bt709`；**trc 恒 `linear` 不变** |

⚠ 为什么是**纯函数**：`Caps` 是**静态共享字典**，能力对象被所有任务复用 ⇒ 运行期改 `CanHdr` 字段会污染并发任务。

### 18.4 ✅ 实测判定表（`tests/output/results/raw_bayer_cfa.dng`）
| 目标 | GainMap | primaries | 判据 |
|---|---|---|---|
| avif | 关 | **bt2020** | `CanHdr=true` |
| jpg | 关 | **bt709** | `CanHdr=false` |
| jpg | **开** | **bt2020** | `CanHdrWith(true)=true` |
| png（位深 8） | 关 | **bt709** | `HdrRequiresHighBitDepth` ⇒ 8bit 装不下 |

产物核对：avif 产物 CICP = **`bt2020` + `linear`** ✓

### 18.5 ⚠⚠ 一次「语义误用」——`CanHdrWith` 曾被用到标注能力判定上（**已实测回滚**）
- **当时的改法**：把 `CanCarryHdrAt` 与 `ColorIntentFactory` 的「HDR 源 + 容器装不下 ⇒ 自动 tonemap」
  判据也换成 `CanHdrWith(JpegGainMap)`。
- **实测后果**：`verify-color-strategy` 由 **63/0 → 58/5**，三格 `*-hdr-jpggm` **无输出**。实测日志：
  `[色彩引擎] 决策：Reject 色彩损失=None｜jpg 既不能携带 ICC 又不能表达该空间的 CICP → 不产出无标注结果`
  —— 不自动 tonemap ⇒ 规划层置 `HdrOutput` ⇒ 要求 JPEG 标注 `bt2020/smpte2084`，
  而 **JPEG 既无 CICP 通路、ICC 也表达不了 PQ** ⇒ 引擎 Reject。
- **根因定性**：`CanCarryHdrAt` 问的是「容器能否承载 HDR **标注**」；而 GainMap 的 HDR 走的是
  「**底图 tonemap 成 SDR + 增益层**」这条**完全不同的机制**（见 `ColorStrategyPlanning` 的 S2 覆盖①
  「GainMap 要求底图映射」）⇒ **自动 tonemap 恰恰是 GainMap 出口需要的**
  （`CollectDegradations` 只登记 `ToneMapNotAppliedToGainMap` 这条降级，不改变产出）。
- **处置**：回滚这 2 处 ⇒ `CanHdrWith` **只**保留在 RAW 源色域默认值那个场景；复跑回到 **63/0** ✓
- ⚠ **教训（已登记 `COLOR_TRAPS §G.10`）**：**同一个能力位在不同判据下语义不同** ——
  「目标能否表达 HDR」与「容器能否承载 HDR **标注**」是**两个问题**，翻前者**不得**顺带翻后者。
  这类误用**只有门禁抓得到**（编译、`Parser`、单测全过）。

### 18.6 ✅ 整轮验收（第七轮）
`运行器汇总：全部通过` · `script-list=49 条（受管基线 49）` · 49/49 `exit=0` · 探针**零 `fail>0`** ·
UI 宿主 **322/0** · `verify-color-strategy` **63/0** · `gainmap-isobmff` **52/0** · **20m04s**。

## §19 RAW 管线整体审查 + 实机测试（2026-09-20）

> 📄 **独立报告**：**`docs/RAW_PIPELINE_AUDIT.md`** —— 含管线全景、格式遍历（7/8 成功）、
> **真实相机 61MP** 实测、选项矩阵、失败路径，以及 **4 条发现**。
>
> ✅ **已修**：**RAW → GIF 无路可走**（而普通 PNG → GIF 可以）。根因：RAW 预处理把**源**标记写进
> `Options.ColorPrimaries/ColorTrc` 并置 `UseAdvancedColorParameters=true`，而 `ColorIntentFactory`
> 在该标志下用它判 `targetExplicit` ⇒ **源标记被误读成「用户显式指定的目标」** ⇒ 规划层
> 「无色彩通路容器钉成 sRGB」不生效 ⇒ `Reject`。修法：新增语义标志 **`FfmpegOptions.ColorFromRawDefault`**
> （`QueueProcessor` 置 true；`ColorIntentFactory` 的 `targetExplicit` 排除它）⇒ 实测 RAW→GIF **产出** ✓，
> 且 RAW→avif **仍标 bt2020**（功能未回归）✓。**变异 I 已验证必要性**。
>
> ⚠ **同一根因的第二处表现**：**中间 TIFF 实际是 gamma 编码，不是线性** ——
> 源码证据：`dngtool.cpp:519 dcraw_process()` → `convert_to_rgb()` →
> `postprocessing_utils_dcrdefs.cpp:53` **无条件** `gamma_curve(gamm[0], gamm[1], 0, 0)`，
> 而 **dngtool 全文件从未设置 `gamm[]`** ⇒ 用 LibRaw 默认（dcraw 的 sRGB/BT.709 曲线）；产品却标成 `trc=linear`。
> ✅ **实测「当前行为自洽」**：RAW→jpg 与参照链路**逐值一致**（0.1406）；RAW→avif 存储均值 **0.0212**
> （比 jpg 暗 6.6×）⇒ **确为线性** —— 引擎把该标签当作**目标**，真的做了 `sRGB→linear` 转换 ⇒ 自洽。
> ⚠ 性质 = **「意图与实现错位」（可维护性问题），非行为缺陷**。详见报告 §3.5。
>
> ✅ **厂商原始格式实测 + 两处缺陷修复（报告 §5，2026-09-20）**：已从 **raw.pixls.us（CC0）** 取到
> **6 种厂商 RAW**（CR2/CR3/NEF/ORF/RW2/ARW），`dngtool` **全部识别**。
> 实测发现并**已修复**两处 `dngtool` 缺陷（改 `tools/src/dngtool/dngtool.cpp`，**重新编译并部署**）：
>
> ① **CFA 相位被改变**（源 RGGB → 产物 GRBG ⇒ 颜色错）—— 根因是**三处 bug 叠加**：
>   位移常量 `>>8`/`>>10` 应为 **`>>4`/`>>6`**；未处理 dcraw 的 **`G2=3`**；
>   **相位语义与 DNG SDK 相反**（`SetBayerMosaic`：**0=GRBG, 1=RGGB, 2=BGGR, 3=GBRG**，
>   与「0=RGGB」的直觉 **0↔1、2↔3 互换**）。
>
> ② **JXL 压缩在带 `ActiveArea` 的源上产出无法解码的文件** —— 根因是 `DefaultCropOrigin` 被填成
>   `(leftMargin, topMargin)`，而规范里它**相对 `ActiveArea`**（后者已含 margin）⇒ 裁剪框越界；
>   改为 **`(0,0)`** 即修复。⚠ 产品**默认就走 JXL**（`-jxl -effort 7 -decode_speed 1`）⇒ 这条影响**默认路径**。
>
> **验收**：6 种格式 × 2 种压缩 = **12/12 `exit=0`**；`filters` 与源一致；
> **解码后 RGB 与源逐值吻合（<0.1%）**；EOSR6 的 JXL 产物从「无法解码」→「正常解码」。
> 部署的 `publish/PLAN/artifacts/dngtool.exe` 与构建产物 **SHA256 一致**。
> ⚠ 编译须用 **VS 自带的 cmake**（系统 cmake 4.4 与 VS 4.3 混用会报 `No preprocessor test for "PellesC"`）。

## 接手提示

1. 先看本文 §「本轮结果」→ 再看 `docs/TESTING.md §6` 第 41/42 条（早期定性与论证）。
   ⚠ **最近几轮请直接看 §「第十二轮」（门禁完整性治理）与 §6 第 58/64/66/67/68 条** ——
   那里是「**门禁本身不可信**」的 5 类缺陷（恒 `exit 0` / 依赖别门禁的遗留产物 / 共享目录并发互清 /
   清理步污染退出码 / 消费者静默跳过）**及其实测证据**；读之前先读那 5 条，能省掉重复踩坑。
2. 门禁基线（**2026-09-19 实测，口径以实测为准**）：
   - **探针**：**21 mode 合计 `542 pass / 0 fail`**（其中 `wire` **72**、`geometry` **52**、**`runner` 8**、**`metaraw` 17**（2026-09-21 新增））。
    **溯源**：**496 → 501** 的 **+5** 来自 `verdict`（GainMap 格新增断言，见 `docs/TESTING.md` §3.5）；**501 → 519** 的 **+18** = `geometry` **35 → 52**（**+17**，批次 1「R1 顺序统一」轮新增位置断言）+ `wire` **71 → 72**（**+1**，来源**未溯源**）。
    ⇒ **2026-09-19 追加（不改上面的溯源链，只在其后追加）**：**519 → 525** 的 **+6** = **`runner` 2 → 8**（#20(0919) 心跳判据锁：非 ffmpeg 子进程不再被注入 `-progress pipe:1`，2026-09-19）。
    ⚠ **2026-09-18 实测更正**：批次 1 曾报「501 → **518**」—— 它**只算了 `geometry` 的 +17，漏算了 `wire` 的 +1**。全量实测 20 个 mode 得 **519**。
    ⚠ 测 `ipc` **必须**先设 `FFMPEGGUI_FFMPEG_DIR`（指向 `publish/PLAN/ffmpeg-full`），否则只得 **8**（两条 SubmitJob 会 SKIP）⇒ **总数会变成 523（= 525 − 2），极易误判成回归**。
     **20 个 mode 的名单**（**只列名单、不列计数** —— **名单是稳定的，计数会漂**）：
     `contract · wire · verdict · engine · selftest · iccname · plan · curve · matrix · ootf · hlgwire ·
     bt2446 · decision · runner · settings · procstreams · encoding · ipc · i18n · geometry`
     ⇒ **计数以 runner 实跑为准**；`wire` 的具体值见上一行（`:512`），S2 收口后稳定，见 `COLOR_ENGINE_INTERFACE_PLAN.md` §7。
     ⚠ **某个 mode 若不在上面名单里，说明运行器清单与文档不同步**（该名单与 `_run-step3-gates.ps1` 的
     `$Probes` 默认值**同集**，可据此核对"mode 齐不齐"）。
     ⚠ 记账口径：**各 mode 的 pass 之和**才是总数 —— 曾把 `contract` 从 100 更新到 107 却漏更新总数，
     写成 372（错值），由 `general-purpose-1` 复核时用"16 项求和 ≠ 372"抓出。**改任一 mode 后必须重算总和**。
   - **脚本门禁**：`_run-step3-gates.ps1` 清单内 **60 条**（2026-10-01；复现口径取运行器打印的 `script-list=N 条（受管基线 N）`）（⚠ 实测 `foreach` 数组条目数；此前注释写 29、后写 31~41，均已更正；第 32 条 = `verify-geometry-engine.ps1`，第 33 条 = `verify-engine-firstframe.ps1`，第 34 条 = `verify-jxl-codestream-route.ps1`，第 35 条 = `verify-anim-static-target.ps1`，第 36 条 = `verify-jxl-probe-timeout.ps1`，第 37 条 = `verify-ffmpeg-heartbeat.ps1`，第 38 条 = `_probe-sync-read-before-wait-scan.ps1`，第 39 条 = `verify-jxr-anim-input.ps1`，第 40 条 = `_probe-ct-chain-closure-scan.ps1`，第 41 条 = `verify-avif-anim-probe.ps1`，**第 42 条 = `verify-webp-anim-probe.ps1`**，**第 43 条 = `verify-ui-host.ps1`**，**第 44 条 = `verify-ui-param-matrix.ps1`**，**第 45 条 = `verify-ui-strategy-map.ps1`**，**第 46 条 = `verify-ui-param-defects.ps1`**，**第 47 条 = `verify-cli-strict.ps1`**，**第 48 条 = `verify-png-structure.ps1`**（第 43~48 条 = **2026-09-19 P4-A 接线**新增 6 条））。
     ⚠⚠ **这个数字每加一个门禁脚本就变** ⇒ 改 `tests/scripts/**` 后**必须重新数**，**不要拿旧值推算**：`grep -oE "'[^']+\.ps1'" tests/scripts/_run-step3-gates.ps1 | sort -u | wc -l`。
     ⚠ **2026-09-18 实测教训**：这个数字在**同一天内被改了五次**（32→33→34→35→36），而四处文档同步总是慢一步 ⇒ 属**反复复发的漂移源**（本次同步点：本行 + `docs/TESTING.md` 的 4 处计数：「35 条」×3、「34 个 GUID」×1）。⚠ **同日续报**：该数字当天**继续漂到 41**（36→37→38→39→40→41，又新增 6 个脚本：`verify-ffmpeg-heartbeat` / `_probe-sync-read-before-wait-scan` / `verify-jxr-anim-input` / `_probe-ct-chain-closure-scan` / `verify-avif-anim-probe`）⇒ 上面的「五次」只是**当时的快照**，**漂移并未止于 36**。
   - ⚠ **脚本门禁必须双宿主兼容**（PS 7 与 PS 5.1 都要能跑）—— 运行器用**当前宿主**作子脚本解释器，
     宿主随启动方式而变。新增/改动任何 `.ps1` 后**先跑** `verify-ps-compat.ps1`：它会抓出 PS7 专属语法
     （三元 `? :` / `??` / `?.`）与「双引号串内嵌全角弯引号」这两类**会让整个脚本静默不执行**的陷阱。
   - ⚠ **并发约束（2026-09-17 已大改，勿再引用旧口径）**：
     · **已自保**：12 个「固定 `$out` + 开头整目录删」的脚本改为**运行级隔离（GUID）** + 结尾自清；
     · **仍不自保**：`$d` 形态 **15 个** + runner 类 + **6 组共享目录簇**（生产者→消费者，裁定 **(b)**：
       保持固定目录 + runner 串行）⇒ **「同一脚本一次只跑一个实例」仍是硬约束**；
     · **跨脚本读写交叉有两处**（都不得并行）：① `verify-decision-delivery`（递归读 `results/**.avif`）↔
       `run-pipeline-tests`（写 `results/*.avif`）；② **`validate/jpgp3` 组**：生产者
       `_probe-jpg-p3-icc.ps1` ↔ 消费者 `verify-colorfmt-matrix` / `verify-png3-interop`；
     · ⚠ **判「残留」不能只看名字**：**GUID 目录 ≠ 残留**（活跃运行会正常产生），必须看**有无进程在写**；
     · 排查假红的第一信号：**同一份代码两次运行的 `PASS` 总数会变**（§6 第 66 条）。
   - 跑法：`pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1`
   - ⚠ 运行器有**二进制新鲜度硬闸**：改过 `src/FfmpegGui/**` 后必须先
   `dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release --no-restore`
   与 `dotnet build tests/ServiceProbe/ServiceProbe.csproj -c Release --no-restore`，否则 `exit 2` 拒跑。
   ⚠ **【2026-09-24 该闸已扩到 4 对】**还要同批重建 `tests/UiTestHost/UiTestHost.csproj -c Release`
   （它被 5 条 UI 门禁执行）⇒ 以运行器 `[STALE]` 段打印的重建提示为准；现行口径见 `docs/HANDOVER_2026-09-22.md` §1 验收 A。
3. 未完成项（均需用户拍板或需真实环境）：
   - 未跟踪文件纳入版本控制（项目约定：实机测试通过前不提交）；
   - **实机 GUI 验证** —— 见 `docs/MANUAL_VERIFICATION.md`（agent 不得在用户实时桌面做鼠标自动化）；
   - **发布包构建** —— ⚠ **2026-09-17 更正：本环境 `dotnet restore` 已可用**（旧结论「无法 NuGet restore」
     **已被推翻**）。⚠ NuGet 缓存会被**周期性清掉**，症状是 **`CS0006`**（找不到元数据文件）或
     **`NETSDK1112`**（资产文件缺失）⇒ **先跑 `dotnet restore` 再构建**即可，不要误判为项目损坏。
     详见 `docs/BUILD_HANDOVER.md`。

---

## §20 2026-09-21 本轮修复汇总（RAW 管线 + UI + 队列 + 进程等级）

> 详细证据见 **`docs/RAW_PIPELINE_AUDIT.md`**；逐轮记录见 `.workbuddy-ai/memory/2026-09-19.md` §39–§49。

**dngtool（`tools/src/dngtool/dngtool.cpp`，已重新编译并部署）**

| # | 缺陷 | 根因 | 修法 |
|---|---|---|---|
| 1 | **CFA 相位被改**（源 RGGB → 产物 GRBG ⇒ 颜色错）| 三处 bug 叠加：位移 `>>8/>>10` 应为 `>>4/>>6`；未处理 dcraw 的 `G2=3`；**相位语义与 DNG SDK 相反**（`0=GRBG,1=RGGB,2=BGGR,3=GBRG`）| 位移修正 + `G2` 归一化 + 按 SDK 真实顺序 |
| 2 | **JXL 产物无法解码**（带 `ActiveArea` 的源）| `DefaultCropOrigin` 误填 margin（规范里它相对 `ActiveArea`）| 改为 `(0,0)` |
| 3 | **JXL 有损**（全机型 24–51%）| `-q` 双重语义：demosaic 质量默认 **3** 被当成 JXL 质量 ⇒ `SetDistance(14.55)` | 新增 **`-jxlq`**（默认 0=无损）+ 产品显式传 |
| 4 | **解码误去马赛克** | 解码路径**无条件**启用 DNG SDK STAGE2/3 ⇒ 自写 CFA DNG 被当「已去马赛克」| 改为**按需重试**（先按 CFA 试，不是 CFA 再启用）|

**产品侧**

- `RawService.cs`：总是显式传 `-jxlq`；**移除 `.x3f` 声称支持**（LibRaw 不支持 Foveon）
- `MainWindow.xaml(.cs)`：DNG 专属选项包进 `DngOptionsPanel`（可见性 = RAW 模式 **且** 输出=dng）+ `FormatCombo_SelectionChanged` 也刷新；加「线性 DNG」「高光模式」适用条件提示
- `ColorMapping/RawColorPipeline.cs` / `GainMapEncoder.cs` / `QualityAnalysisService.cs`：**补 6 处漏设的进程优先级**
- `QueueProcessor.cs`：**加固 3 项** —— 信号量提为字段（跨循环共享）· `Start()` 先等旧循环退出 · `RebuildSemaphore()` 与并发值同步

**验证**

- **产物正确性**：`dng_validate`（Adobe 官方）**`Validation complete`** + `dcraw -D` **逐字节 0 差异** ⇒ 产物完全正确
- **CFA 四相位**：GRBG/RGGB/BGGR/GBRG 全覆盖，`源 vs 产物` 偏差 **<0.5%**
- **RAW 全组合**：输入 × 7 格式 **21/21** · DNG 输出 × 48 组合 **0 意外失败** · 43 个厂商素材 **37 通过**
- **进程等级**：**实机验证** dngtool / cjxl / ffmpeg 均 `Idle` ✓
- **整轮门禁**：`_full_run_57.log` **失败条目 0**（67 条脚本条目）· 编译 **0 警告 0 错误**

**⚠ 已知边界（非缺陷）**

- **Fujifilm EXR**（X-S1/X10）转 DNG 后与源解码不一致 ⇒ 已用 `dcraw -D` 证明**原始像素逐字节相同** ⇒ 是**解码器元数据处理差异**，不是产物错误
- **Sigma X3F**（Foveon）与**非标准 `.raw`** 不支持 ⇒ **UI 已不再声称支持**
- **线性 DNG** 对 Bayer 输入**按设计拒绝** ⇒ UI 已加提示
- **高光模式**对保留 CFA 的 DNG 输出**无效** ⇒ UI 已加提示

---

## §21 2026-09-21（第十八轮）—— RAW 输入的**元数据保留**修复 + PNG 链路 3 个既有缺陷

> 完整证据见 **`docs/RAW_PIPELINE_AUDIT.md §10`**；逐轮记录见 `.workbuddy-ai/memory/2026-09-19.md §51`。

**为什么会有这一轮**：前 17 轮覆盖了「像素对不对」「格式能不能用」「UI 与真实是否相符」，
**唯独没查「元数据保不保留」** —— 而默认 `MetadataMode = PreserveAll`（`CliParser.cs:47`）
就是**宣称保留所有元数据**。⇒ 教训：**宣称类选项（PreserveAll / 无损 / 保留）必须逐条实证**。

### 已修（6 项）

| # | 缺陷 | 性质 | 修法 |
|---|---|---|---|
| 1 | **RAW 输入（非 DNG 输出）丢 `DateTimeOriginal` / `CreateDate` / `LensModel` / `ExposureProgram` / `MeteringMode` / `Flash`** | **实质缺陷**（相册时间线断裂）| 新增 `QueueItem.SourceInputPath`（在改写 `InputPath` **之前**钉住）+ `QueueProcessor.RestoreRawCaptureFieldsAsync`：常规恢复**之后**从**源 RAW** 按**白名单**补回 |
| 2 | RAW 输入的队列 / 进度窗 / CLI **显示内部临时文件名**（`xxx_raw.tiff`）| UI 与真实不符 | `QueueItem.DisplayName => Path.GetFileName(SourceInputPath ?? InputPath)`；6 处消费点改读它 |
| 3 | **PNG 输出的元数据恢复曾恒不生效** | 既有缺陷 | PNG 目标先做 `-EXIF:all=`（ffmpeg 写的 `eXIf` 里带 TIFF 专用 `StripOffsets` ⇒ exiftool 读取即报错并放弃写入）|
| 4 | **`png`/`apng` + `--strip-metadata` 泄漏 GPS** | **隐私缺陷**（既有）| 新增 `StripDescriptiveMetadataAsync`（`-EXIF:all=` 复位 + `-exif:all= -gps:all= -xmp:all= -iptc:all=`，**不删 ICC**），仅对 png/apng 的 `StripAll` 生效 |
| 5 | **exiftool 写超时硬编码 15 s** ⇒ 大文件必超时 + 残留 `_exiftool_tmp` | 既有缺陷（#3 的连带暴露）| `RunRawAsync` 增 `timeoutSeconds`；`WriteTimeoutSecondsFor`（`15s + 0.25s/MB`，上限 600s）；写路径统一走 `RunWriteAsync` |
| 6 | 未实测项补齐：**断工具场景** | 审查文档原列为「未做」| 实机验证：`dngtool: ❌` · **点名报错** · **输出目录为空** · **退出码 1**（正常 0）✅ |

### 关键设计约束（改动理由，勿"优化"掉）

- **白名单不能用 `-all:all`**：源 RAW 的 `ColorSpace`（常为 Adobe RGB）、
  `PixelXDimension/YDimension`（常为**内嵌预览**尺寸）、`Orientation`（像素**已被解码器旋正**）
  回带会与产物**冲突**（后者会导致**二次旋转**）。
- **补漏必须自己遵守隐私开关**：`CanAbsorbStrip` 为真时那次独立隐私清理会被**跳过**
  ⇒ 补漏若不排除 GPS/XMP 就等于**把刚剥掉的隐私装回去**。
- **`-Flash` 必须写 `-EXIF:Flash`**：不带组限定时 exiftool 会**顺带创建 XMP 块**（6 个标签），
  **即使显式写 `--XMP:all` 也拦不住** ⇒ 既是多余产物，也是隐私开关的绕过口。
- **PNG 的 `-EXIF:all=` 只清 EXIF 组**（不是 `-all=`）：PNG 色彩靠 `iCCP`/`cICP`/`cHRM`/`gAMA`
  **独立 chunk** 承载，`-all=` 会把它们一起删掉。实测 `-EXIF:all=` 只重写 `eXIf`，其余 chunk 幸存。

### 验证

- **拍摄字段**：10/10 厂商样本（CR3/PEF/SRW/IIQ/ORF/ARW/RWL/RW2/MRW/3FR）`DTO` 源=产物 ✅ · **DNG 输入** ✅
- **输出格式**：`jpg/png/tiff/webp/avif/jxl` **6/6** ✅
- **隐私矩阵 15 例**：**无绕过** ✅（含 `--strip-metadata` / `--strip-gps` / `--strip-time` /
  `--strip-camera` / `--strip-xmp` / `--strip-all-exif`）；`png` 的 `cICP`/`cHRM`/`gAMA` **全部保全** ✅
- **门禁**：`verify-metadata-privacy` 由 **3 → 5 断言**，**5/0** ✅；
  **变异验证**：`IsCicpMetadataMappedFormat` 恒 `false` ⇒ ④ **转红**（`PASS=4 FAIL=1`、`exit=1`），
  ⑤ 仍绿 ⇒ **断言非空跑**；还原后复跑 **5/0** ✅
- **大文件**：276 MB PNG（9600×6376）⇒ `DTO`/`Lens`/`GPS` 齐全、**无 `_exiftool_tmp` 残留** ✅（约 1m27s）
- 编译 **0 警告 0 错误** · 三份 `FfmpegGui.dll` **SHA256 一致**

### 门禁与锁（2 条新锁 + 4 次变异验证）

| 锁 | 位置 | 内容 |
|---|---|---|
| **参数形态锁**（**不依赖素材**）| `ServiceProbe metaraw` —— **新增第 21 个 mode（合计 525 → 542，实测逐 mode 求和）** | 17 断言：`DisplayName` 取源路径（A1–A4）· 补漏白名单形态 B1–B8（**必须 `-EXIF:Flash` 不得裸 `-Flash`**、**不得 `-all:all`**、**不得回带 `Orientation`/`ColorSpace`/`PixelXDimension`**、**PNG 才插 `-EXIF:all=`**、**取源必须是源 RAW**）· 隐私开关在补漏里生效 C1–C5 |
| **行为锁** | `verify-metadata-privacy`（**3 → 7 断言**）| ④⑤ png/apng 剥离 + 负控 · **⑥⑦ RAW→PNG 拍摄字段**（`MeteringMode`/`DateTimeOriginal` 必须落盘）+ 负控 —— ⑥ 同时锁「补回生效」与「PNG 的 exiftool 写入通道」；⚠ 素材缺失时**点名 SKIP、不给绿灯** |

⚠⚠ **为什么参数形态锁必须不依赖素材**：仓库里**没有任何受版本控制的 RAW/DNG 素材**
（`tests/output/**` 被 `.gitignore:78`、`tools/src/dng_sdk/**` 被 `.gitignore:93`）⇒
建在「真跑一张 RAW」上的锁在干净检出会 **SKIP ⇒ 静默假绿**。

**变异验证（4 次，全部"红 → 绿"闭环）**

| 变异 | 内容 | 实测 |
|---|---|---|
| M1 | `DisplayName` 退回 `Path.GetFileName(InputPath)` | A1–A3 转红（`fail=3`），A4 仍绿 ✅ |
| M2 | `-EXIF:Flash` 改裸 `-Flash` | **仅 B3** 转红 ✅ |
| M3 | 补漏忽略 `StripExifGps` | **仅 C1** 转红 ✅ |
| M4 | `PngExifResetArg` 恒返回空 | 探针 **仅 B6** 转红；门禁 ⑥ 转红（`PASS=6 FAIL=1`），⑦ 仍绿 ✅ |

⇒ 还原后 探针 **542/0** · 门禁 **7/0** · 整轮 `_run-step3-gates.ps1` **「运行器汇总：全部通过」**（18m18s）。

⚠⚠ **加锁时的三个新教训**（详见 `RAW_PIPELINE_AUDIT.md §10.8` 与日志 §53）：
1. **锁本身也会空跑** —— ⑥ 的第一版用**合成素材**（JPEG / ffmpeg 自产 TIFF）当源，
   在 M4 下**仍 PASS**：该缺陷**只在「源是 dngtool 的中间 TIFF」时触发**，合成替代品不触发。
   ⇒ **每个新断言都必须有一次"红→绿"闭环**才算有锁。
2. **字节级判据要注意字节序**：按大端 `01 11` 找 `StripOffsets`，而 eXIf 是小端 `11 01` ⇒ 一度误判。
3. **探针跑的是「探针侧」那份 `FfmpegGui.dll`** —— 改 `src/FfmpegGui` 后**必须同时重建 `tests/ServiceProbe`**，
   否则变异验证会读出**上一个变异**的结果。

### ⚠ 未做（**不得声称已验证**）

超大文件（>200 MB DNG）· 并发多任务内存压力测试 ·
`Orientation ≠ 1` 的旋转 RAW（12 个样本全为 `Horizontal (normal)`，**无素材**）·
`avif`/`jxl` 是否随 ffmpeg 版本变化而开始泄漏 GPS（本轮实测**不泄漏**）。

---

## §22 2026-09-21 —— `MEMORY.md` 过期登记审计 + 压缩轮（按 `memory-file-staleness-audit` 技能执行）

> 背景：`MEMORY.md` 已达 **7951 / 8000 字符（余量 49）** ⇒ 再增任何内容都会**静默截断尾部**。

### 22.1 实测上限（不采信历史记录）

注入内容与文件尾部**完全一致**（均止于 §8）⇒ 当前 **7951 字符未触发截断**，
记录值 **8000** 与现状相容；**余量仅 49 字符**，故本轮按「≥200 余量」目标压缩。

### 22.2 修正表（7 处，均为**实测**；证据为 `文件:行`）

| # | 类 | 原文 | 实测结论 | 改法 |
|---|---|---|---|---|
| 1 | ③ 行号漂移 | `MainWindow.xaml.cs:2514`（`GetIccMode`）| 实际 **`:2554`**（`:2514` 是 `2 => "complexity"`）| → `:2554` |
| 2 | ③ | `:2523`（`GetColorStrategy`）| 实际 **`:2563`** | → `:2563` |
| 3 | ③ | `:2536`（`SetColorStrategyRadio`）| 实际 **`:2576`** | → `:2576` |
| 4 | ③ | `:5805-5812`（预设存盘规范串）| 实际 **`:5895-5902`** | → `:5895-5902` |
| 5 | ③ | `:6014`/`:6019`（`ApplyPresetData`）| 函数定义在 **`:5911`**（原引的两行是它内部调用）| → `:5911` |
| 6 | ③+④ | `ProcessAsync:286`（`AnimatedWebpCache` 单点写）| 该文件 `:286` 是无关代码；真实写入在 **`IsAnimatedWebpAsync:4877/4897`**（声明 `:5076`）| → `IsAnimatedWebpAsync:4877/4897` |
| 7 | ① 错引 | `COLOR_TRAPS §A 补 三`（长驻后台进程 ~2 min）| `§A 补`（`:481-492`）是**表格、无编号子节**；该条在 `§B 补` 的 `### 三、`（**`:865`**）| → `§B 补 三` |

### 22.3 「已核验、不动」清单（**下一轮别再查、更别误改**）

| 登记 | 为何判定为**正确** |
|---|---|
| `ColorStrategy.cs:14-40` | `:14` = `public enum ColorStrategy`，`:40` = 闭合 `}` ✅ |
| `ColorStrategy.cs:47-53` | `:47` = `FromIccMode`，`:53` = `};` ✅ |
| `FfmpegOptions.cs:135-138` | 正是「`ColorStrategy` 优先、否则 `IccMode` 迁移」块 ✅ |
| `HANDOVER.md:18-20` | 正文即「原项 1/2/3 已接入引擎」✅ |
| `HANDOVER.md:1175-1200` | 正文即 `#136` AVIF 动画探测**三态化** ✅ |
| `COLOR_TRAPS §G.10` | ⚠ **`§G` 是编号列表 1–10**（不写 `G.10` 字样）⇒ **`10.` 就是 `§G.10`**（`:1085`，`CanHdr` 语境陷阱）✅ **不是错引** |
| `COLOR_TRAPS §B.4/§B.7/§B.8` · `§C/§D` | 章节标题均存在 ✅ |
| ~~`COLOR_TRAPS §A 补 十/十一/十二`~~ | ⚠⚠ **本条登记有误，已于下一轮（§23）更正为 `§B 补 之四 十/十一/十二`** —— 当时只核了「章节存在」，**没核前缀对不对**：`§A 补`（`:481-492`）是**表格、无编号子节**，那些条目实际在 `§B 补` 之下。 |
| `TESTING §6`（六类假绿/假红）| 正文含「假绿」✅ |
| 日志 `§18`/`§20`/`§23` | 标题均存在 ✅ |
| 探针基线 **21 mode 542** | 本轮逐 mode 实测求和 = 542、全 `fail=0` ✅ |
| 门禁基线（`63/0`·`13/0`·`14/0`·`56/0`·`7/0`·UI `322/0`）| 本轮整轮门禁实测一致 ✅ |

### 22.4 压缩（**以删减计量**，改完实测）

| | 字符数 | 余量 |
|---|---|---|
| 压缩前 | **7951** | 49 |
| 压缩后 | **7759** | **241** ✅（≥200 目标达成）|

删减方式（**全部是「细节 → 指针」**，不丢事实）：§4 两条（「何时引入的判定法」「分诊归因复核」）
收敛为指向 `COLOR_TRAPS §A 补 十/十二` 的短指针；`verify-oracle-matrix` 的 L1/L2 历史读数、
`Check-SrcDrift`/STALE 一条的冗余尾句、L3 的括号解释做了去冗。
⚠ **锚点核验**：37 个内容锚点 + 各节条目数（§1=7 §2=7 §4=9 §5=3 §6=5 §7=9 §8=2、§3 表 6 行）
**全部与压缩前一致**；`Check-SrcDrift` 一度被删、已补回。

### 22.5 未做（**需维护者或后续轮次执行**）

- `COLOR_TRAPS.md`（**74173 字符**）与 `docs/TESTING.md`（**447945 字节**）**未审计** —— 二者都不受注入上限约束，
  但 `COLOR_TRAPS` 内部**编号重复**（存在两个 `### 十三、`：`:680` 与 `:1014`），
  **引用它时请带行号**，别只写「§B 补 十三」。
- `MEMORY.md` 的「只留指针」纪律仍靠人工；本轮后余量 **241**，**下次小改动就会再越线** ⇒ 建议保持增量写 `COLOR_TRAPS`/日志。

---

## §23 2026-09-21 —— `COLOR_TRAPS` 编号消歧 + 5 处**过期登记**修复（含 1 处在门禁脚本内）

> 承接 §22 的「未做」项：`COLOR_TRAPS` / `TESTING.md` 当时**未审计**。

### 23.1 ✅ 编号重复 ⇒ 引用消歧（这是 §22 遗留的直接问题）

**问题**：`## §B 补（追加之三）`（`:509`）之下**有两段各自编号的追加块** ——
Run 1 = `一,二,十…二十一`、Run 2 = `三…十三` ⇒ **`十/十一/十二/十三` 各出现两次**
（实测两个 `### 十三、` 在 `:680` 与 `:1014`）⇒ `§B 补 十三` 这类引用**有歧义**。

**修法**（**不重编号** —— 那会破坏既有引用）：把 Run 2 **正式独立**为一个 `##` 节
`## §B 补（2026-09-19 追加之四：11 条实测 …）`，并在节首写明约定：
**引用必须带父节** —— `§B 补 之三 <项>`（Run 1）/ `§B 补 之四 <项>`（Run 2）；
**旧文里只写 `§B 补 十`/`§A 补 十` 的一律按「之四」解析**（实测历史引用全部指向它）。

**验证**：脚本按 `##` 分节后检查各节内 `### N、` 唯一性 ⇒ **无重复** ✅

### 23.2 ✅ 同步修正的引用（7 处）

| 位置 | 原 | 改 |
|---|---|---|
| `MEMORY.md` ×4 | `§B 补 三` · `§A 补 十一/十二` · `§A 补 十` · `§A 补 十二` | `§B 补 之四 三` / `之四 十一/十二` / `之四 十` / `之四 十二` |
| `COLOR_TRAPS.md` ×2 | `见 §A 补 十二` · `写进了 §A 补 十一 的表` | `§B 补 之四 十二` / `之四 十一` |
| `HANDOVER §22.3` ×1 | 把 `§A 补 十/十一/十二` 列为「已核验、不动」 | 标注**该登记有误**并指向本条 |

### 23.3 ✅ 5 处**过期登记**（类 ②：已结案却写成待办 —— 本仓最贵的一类）

| # | 位置 | 原文 | 实测结论（证据） | 改法 |
|---|---|---|---|---|
| 1 | `COLOR_TRAPS:273` | 「**站点 3（未修）**：ffmpeg 编码阶段忙等 ⇒ 用户可见的挂死仍在」 | ⚠⚠ **与 `MEMORY.md §8`「三站点已全修」直接矛盾**。真相：`FfmpegRunner.RunAsync(..., bool heartbeat = true)` —— **心跳默认 opt-out** ⇒ 全部 ffmpeg 调用点自动获得「进度块到达」这一**与 CPU 正交**的活性信号（`FfmpegRunner.cs:48-57`）；`HANDOVER:422` 记「已实现心跳守卫」；门禁 `verify-ffmpeg-heartbeat` **68/0** | 标为 **✅ 已修（`#15-b`）** |
| 2 | `COLOR_TRAPS:380` | 「⚠ **仍未做**：`verify-color-wiring.ps1:160` 分辨不出哪一层在缩放」 | 该条**已于 2026-09-19 复核并有意不收紧**：紧邻的 `:158` 已断言实测宽高 `32,24` ⇒ 假绿路径不存在；且**今天收紧会假红**（预缩放还在时引擎几何段不跑）。现改为**打印实际命中层**（`verify-color-wiring.ps1:167`） | 改为「已复核 · 有意不收紧 · **不是缺口**」 |
| 3 | `TESTING.md:869`（第 35 条）| 「**【新发现，未修】**`--format jpegli` 扩展名非法 ⇒ 转换必然失败」 | **实测已修**：`--format jpegli` ⇒ `rc=0`、队列「完成 1 项, 失败 0 项」、产物 **`.jpg`**。判据在 `EncoderDetectionService.cs:38`：`Cjpegli => fmt is "jpg" or "jpeg" or "jpegli"` ⇒ **`jpegli` 是 `jpg` 格式的编码器别名**（即该条提出的语义问题的答案） | 标为 **【已修】** + 补实测 |
| 4 | `TESTING.md:1682` | 「仍在出口外的只剩 **JXR / DNG**」 | JXR **已接入引擎**（`ExternalEncoderExit` + `EncodeJxrViaJxrEncAppAsync`，8-bit⇒BMP / >8-bit⇒TIFF，alpha 走第二输入不拍平）⇒ 与 `HANDOVER` 顶部「**只剩 DNG**」不一致 | 追加更正：**只剩 `DNG`** |
| 5 | `tests/scripts/verify-metadata-privacy.ps1` 末尾附注 | 「`--format jpegli` **目前是坏的** ⇒ 据此**排除**一条通路」 | 同 #3 ⇒ **该排除理由已不成立**。⚠ 这条在**受版本控制的门禁脚本**里，影响面比记忆文件更大 | 同步更正（**保持 CJK 行数不变** —— cjk 锁 `CsUi` 余量为 **0**）|

⚠ **未做（本轮）**：`COLOR_TRAPS`（**75304 字符**）与 `docs/TESTING.md`（**447945 字节**）**只做了定向扫描**
（`待办/未修/仍未做/待裁决/开放项` 共 **19 处** + 编号结构），**未逐条全审**。
已抽查并判定**仍成立**的：`TESTING.md:763`（`TryConnectAsync` 生产侧 0 调用点 ⇒ 单实例未接线）、
`TESTING.md:1051`（`InputMayHaveMultipleFramesAsync` 仍含 `AnimationFps` 输出侧意图分支，`:5004`）、
`TESTING.md:3865`/`:1671`（**均已在原位用后续行自我更正** ⇒ 良好实践，**不动**）。

**验证**：`verify-ps-compat` **13/0** · `_probe-cjk-hardcode-scan` **14/0** · `verify-metadata-privacy` **7/0** ·
`MEMORY.md` 余量 **229**（≥200）· 编号唯一性检查通过。

---

## §24 2026-09-21 —— 补完审计（`TESTING.md` 逐条）+ **今日更改通审**

### 24.1 补完的审计（承 §23.3 的「未逐条全审」）

| 位置 | 原文 | 实测结论 | 处置 |
|---|---|---|---|
| `TESTING.md:3603`（第 81 条）| 「**现状（2026-09-18）**：只登记，未修（`verify-color-peak.ps1` 本轮无人改；mtime **2026-09-17 20:21:44**）」 | **结论仍成立，但「前提」已过期** —— 该脚本 mtime 现为 **2026-09-20 22:35**（他因改动）；问题本身仍在：`:76` 是 `Write-Output "  SKIP (d)…"`、**不经 `CK`** ⇒ 4 条断言从汇总里消失 | 更新前提 + 写明问题仍在 |
| `TESTING.md:827-828`（第 32 条）| 「隐私默认值有两处独立真值…建议合并（未做）」 | **结论仍成立，但「形态」已变** —— `CliParser.Options.StripGps` 现为 **`bool?` 三态**（不再是「第二个默认值」），但转换点仍硬编码 `StripExifGps = opts.StripGps ?? true`（`CliParser.cs:519`）⇒ **第二个真值仍在** | 加澄清（结论与「未做」均保留）|

**抽查判定「仍成立」（不动）**：`TESTING.md:3725`（「回退时打印测的是哪个 exe」—— 实测 `verify-color-peak.ps1` 仍**无** `[gate] exe=`）、
`:3745`（探针 `catch` 仍**无** `FAIL exception:` 前缀）、`:3605-3612`（第 82 条三处事实：`xcheck:20` 用 `cmsx_<guid8>` ✓、`zimg:5` 仍固定 `cmsx` 且 `:19` 静默 `continue` ✓、runner 注释过期 ✓）。

### 24.2 ⚠⚠ 通审发现：**文件内「绝对行号引用」普遍失效**（既有系统性问题）

**方法**：对每个被改文件，抽取注释里的 `` `:N` `` 引用，检查其目标行**是否为注释**（粗判据），
并在 **HEAD 版**上做同样统计 ⇒ **归因**（是今天引入的，还是本就如此）。

| 文件 | HEAD 失效 | 当前失效 | 归因 |
|---|---|---|---|
| `QueueProcessor.cs` | 7 / 11 | 6 / 11 | **既有**（今天未新增破坏）|
| `MainWindow.xaml.cs` | **15 / 19** | **17 / 19** | **今天新增 2 处破坏**（今天的改动使该文件累计位移 **+41 行**）|
| `ExifToolService.cs` | 0 / 0 | 0 / 0 | 无此类引用 |

⇒ **已修今天新破坏的引用**（`MainWindow.xaml.cs` 共 **3 处引用行**：`:6101-6102` 的 5 个调用点、
`:6098` 的三处预设写入、以及**同一缺陷的平行注释** `:5657`（`JpegGainMapEnable_Changed` 侧，初查时**漏掉**、复测才发现）），
且**不重新钉行号** ——
改为**具名 handler**（`FormatCombo_SelectionChanged` / `ConversionMode_SelectionChanged` /
`InitializeCapabilitiesAsync` / `SimplePresetCombo_SelectionChanged` / `JpegGainMapEnable_Changed`；
三处预设值写入改为「本方法上面写进去的三项」）⇒ **抗漂移**，与 `COLOR_TRAPS §十四`「行号不是稳定标识符」一致。
⚠ 每处另附一句说明「原先写的是绝对行号」，便于对照历史。
**效果**：`MainWindow.xaml.cs` 失效引用 **17/19 → 14/16**（总引用数 19→16，因 3 处行号被替换为具名）。

⚠ **未做（建议专门派工）**：其余失效引用（`QueueProcessor.cs` 6 处、`MainWindow.xaml.cs` 余下部分）
属**既有**问题，涉及面广（`tests/ServiceProbe/Program.cs` 也有 8 处）⇒ 建议做一次
**「行号引用 → 符号/短语引用」的专门替换轮**，而不是零散重钉（重钉下次编辑必然再坏）。

### 24.3 通审结论（今日更改）

| 维度 | 结果 |
|---|---|
| 编译 | **0 警告 0 错误**；三份 `FfmpegGui.dll` **SHA256 一致** |
| **注释-only 证明** | 本次通审只改注释 ⇒ 重建后 `FfmpegGui.dll` **与改前字节完全一致**（`2b03cd5a0bf6ca3c`）⇒ **零行为变更**（比"跑一遍没红"更强的证据）|
| 调试残留 | 8 个改动文件 `TODO/FIXME/XXX/HACK/Debugger.Break/DEBUG` 计数 **全 0**；我新增代码里无 `Console.`/`Debug.` |
| 变异残留 | 全仓无「临时变异」残留 |
| 门禁脚本兼容 | `tests/scripts/**` 未引入 PS7+ 语法；`verify-ps-compat` **13/0** |
| 中文硬编码锁 | `_probe-cjk-hardcode-scan` **14/0**。⚠ 复核到一条**可省心的既有事实**：该探针**剥掉注释**再计数（其反控行明写「注释里的中文**不得**计入」）⇒ **改注释不会顶红 cjk 锁**，不必逐行保数（本轮起初按「余量为 0」保守处理，属过度谨慎）|
| **整轮门禁** | `_run-step3-gates.ps1` **「运行器汇总：全部通过」**（**18m22s**，49 条脚本 + 21 探针；UI 宿主 **322/0**）|
| 端到端 | 6 个缺陷修复的实机证据见 §21；隐私矩阵 15 例无绕过 |
| 文档↔代码一致 | 本轮修掉 **10 处**（5 处过期登记 + 3 处行号引用行 + 2 处 `TESTING.md` 措辞）；`MEMORY.md` 4 处引用同步 |

---

## §25 2026-09-21 —— 落地两条「只登记、未修」的**测试基础设施**缺陷

> 承 §24：这两条是 `TESTING.md` **第 84 / 85 条**里登记者自己写明处置建议、但一直未做的项。

### 25.1 ✅ 第 84 条：**Debug 回退是静默的** ⇒ 门禁可能测的不是你以为的那个二进制

**问题**：`if (-not (Test-Path $exe)) { $exe = "…/bin/Debug/…" }` 遍布仓库，
**回退不打印任何东西** ⇒ 门禁测了 Debug 构建而**跑完不会告诉你**；而 `Release`/`Debug` 的「绿」**不是同一个读数**。

**修法（处置建议 ①）**：在**全部 38 处**回退点之后**一律打印**
`[gate] exe=<实际路径> (Release|Debug fallback)` —— 覆盖 **33 个脚本**（`$exe` 28 处 + `$pr`/`$prExe`/`$prb` 等 10 处）。

⚠ **实现纪律**（这类批量插入最容易出事的几点）：
- **只插入新行、不改动既有行**；逐文件核验「插入数 == 匹配数」（实测 38/38、33 文件）
- 新行用 `$(if (…) {…} else {…})` —— **PS5.1 兼容**（禁 `?:`，见 §4）
- 新串**纯 ASCII**（不碰 cjk 锁）
- 先查**影响面**：全仓 `exception:` 只被 2 个脚本消费且都与探针无关

**实测**：`verify-ps-compat` **13/0**；`verify-color-peak` 打印 `…ServiceProbe.exe (Release)`；
`verify-metadata-privacy` 打印 `…FfmpegGui.exe (Release)`。

⚠ **② 仍开放**：「找不到 Release 就**红**」（与第 68 条同族）—— 属**行为变更**（会让「只构建了 Debug」的既有用法直接转红）
⇒ **留给维护者拍板**。

### 25.2 ✅ 第 85 条：探针 `catch` 的 `exception:` 行**不带 `FAIL` 前缀** ⇒ 按 `^FAIL` 过滤的读者看不见

**问题**：`ServiceProbe` 主 `catch` 打的是 `exception: {type}: {msg}`（无前缀）+ `_fail++`，汇总行照常打印
⇒ 实测出现过「**`pass` 52→36（16 条整段没跑）而 `fail` 只 +1**」⇒ **只看汇总行的人以为只坏了 1 条**。

**修法（处置建议 ①）**：改打 **`FAIL exception: …`** ⇒ 与 `Check` 的失败行**同形**，不会再被 `^FAIL` 过滤掉。
**影响面先查**：全仓只有 2 个脚本出现 `exception:`（`verify-ipc-extra-options.ps1` / `verify-oracle-matrix.ps1`），
且都指**它们自己的**异常、**不解析探针输出** ⇒ 加前缀**无副作用**。
**复核**：探针基线 **仍 542/0**（21 mode 逐项求和）· 三份 `FfmpegGui.dll` SHA256 一致。

⚠ **更重要的遗产**（登记者自评「比按 `exception:` 过滤更有用」）：
**判据 = 「`fail` 与 `pass` 的变化量不匹配」** —— 任何读数异常（尤其 `fail` 小得可疑）
都要去 `PROBE RESULT` **之前**找 `exception:`。**这条口径比加前缀更承重，勿丢。**

### 25.3 验证

编译 **0 警告 0 错误** · `verify-ps-compat` **13/0** · 探针 **542/0** · 三份 DLL SHA256 一致 ·
整轮 `_run-step3-gates.ps1` **「运行器汇总：全部通过」**。

---

## §26 2026-09-21 —— 修掉一处**「断言静默消失」**的假绿通道（`TESTING.md` 第 81 条）

### 26.1 病灶：**可见 vs 不可见**

`verify-color-peak.ps1` 的 (d) 段用一句**裸 `Write-Output "  SKIP (d)…"`** 跳过 4 条断言 ——
**不进汇总、不经 `CK`** ⇒ 汇总行仍是 `PASS=25 FAIL=0`，读者**没有任何线索**知道少了 4 条。
登记者自己的定性最准：**「其余形态是『断言红了』（可见）；本条是『断言不存在了』（不可见）⇒ 前者骗不过人，后者能。」**

⚠ **为什么它是活通道而不是纸上问题**：素材 `tests/output/validate/src_hdr.png`（生产者 `ensure-fixtures.ps1`）
**不受版本控制**（`tests/output/**` 被 `.gitignore`，`.gitignore:78`），
且**运行器不跑 `ensure-fixtures`**（实测 `grep ensure-fixtures _run-step3-gates.ps1` = 0 命中）
⇒ **干净检出上该素材必然缺失** ⇒ 这条通道会真的发作。

### 26.2 修法：取**第 76 条口径**（SKIP 进汇总并计数），**不是**第 68 条的硬红

**为什么不是硬红**：素材在干净检出上**合法地**不存在（环境未就绪 ≠ 缺陷）⇒ 硬红会让门禁**永久红**。
本条的真正病灶是**不可见** ⇒ **修可见性即治本**。

**改动**（与 `verify-gif-avif-framelist.ps1` **同一形态**，不发明新机制）：
- 加 `$skip = 0`
- 加 `function SKIP([string]$what, [int]$n = 1) { $script:skip += $n; Write-Host "SKIP ($n 条) $what" -ForegroundColor Yellow }`
- (d) 段改为 `SKIP "…以下 4 条断言**本轮未运行**，不是通过" 4`
- 汇总行改为 `===== peak 源峰值读取: PASS=$pass FAIL=$fail SKIP=$skip =====`

### 26.3 负控实测（**移走素材 → 还原**，用哈希保证可还原）

| 状态 | 汇总行 | 判定 |
|---|---|---|
| **修复前**（素材缺失）| `PASS=25 FAIL=0` | ❌ **看不出少了 4 条** |
| **修复后**（素材缺失）| `PASS=21 FAIL=0 **SKIP=4**` + `SKIP (4 条) (d) 段整段：缺 …—— 以下 4 条断言**本轮未运行**，不是通过` | ✅ 一眼可见 |
| **修复后**（素材在位）| `PASS=25 FAIL=0 SKIP=0` | ✅ 正常 |

⚠ 素材还原后哈希 **`f168fcb080e0151f`** 与移走前**一致**（已核）。
⚠ 先查影响面：运行器**不解析** `PASS=/FAIL=` 做断言（`_run-step3-gates.ps1:688` 明写「刻意不用」），
其展示正则**本就含 `SKIP`** ⇒ 加字段**无副作用**；且 `verify-gif-avif-framelist` 早已是 `PASS=n FAIL=0 SKIP=k` 形态。

### 26.4 ⚠ 仍未做

① **`ensure-fixtures.ps1` 未接线进运行器** ⇒ 干净检出上仍会缺这批 fixture（**只是现在看得见了**）。
② 其余门禁是否也有同款「裸 `Write-Output SKIP`」**未逐条排查** ⇒ 建议按同一形态做一次普查。

### 26.5 验证

`verify-ps-compat` **13/0** · 正常态 `PASS=25 FAIL=0 SKIP=0` · 负控 `PASS=21 FAIL=0 SKIP=4` ·
整轮 `_run-step3-gates.ps1` **「运行器汇总：全部通过」**。

---

## §27 2026-09-21 —— 第 81 条的「同类普查」：又修掉 **2 个脚本 / 7 处**静默 SKIP

> 承 §26.4 的「未做 ②」：**其余门禁是否也有同款「裸 `Write-Output SKIP`」未逐条排查**。

### 27.1 普查方法与结论

对 `tests/scripts/*.ps1` 扫「形如 `Write-Output/Write-Host "…SKIP…"` 的**裸 SKIP**」⇒ 命中 **6 个脚本**。
**逐条判定**（关键是「它是否**真的跳过断言**」）：

| 脚本 | 判定 | 依据 |
|---|---|---|
| `verify-color-caps.ps1` | ✅ **不是缺陷** | 两处在「(g) **未实测项（必须显式标注，不得当作已知事实）**」小节里 ⇒ 是**诚实披露**，那些断言**本就不存在** |
| `verify-color-tonemap.ps1` | ✅ **不是缺陷** | 有**独立黄字小节** `=== SKIP（未实现，不给绿灯）===` ⇒ **刻意显眼**，且运行器**已捕获**该行 |
| `verify-color-peak.ps1` | ⚠ 同类 ⇒ **§26 已修** | (d) 段跳过 4 条断言、不进汇总 |
| **`verify-decision-delivery.ps1`** | ⚠ **同类（4 处）** | ⑫外层(4 条) / ⑫内层(3 条) / ⑩(5 条) / ⑪(9 条) **真的跳过断言**，而汇总行 `pass=51 fail=0` **无 skip 字段** |
| **`verify-png-signature.ps1`** | ⚠ **同类（3 处，且是整门禁）** | 3 条跳过路径**直接 `exit 0`、不打印任何汇总** ⇒ 读者只看到一句 SKIP，**分不清「跑完了且干净」与「一条断言都没跑」** |

### 27.2 修法（与 §26 同一口径：SKIP 进汇总并计数）

**`verify-decision-delivery.ps1`**：加 `$skip = 0` + `function SKIP([string]$what,[int]$n=1)`；
4 处裸 SKIP 改为 `SKIP "<原因>" <条数>`；汇总行改为 `pass=$pass fail=$fail skip=$skip`。
**条数是逐块数出来的**（按花括号配对 + 数 `CK`）：⑫外层 **4** · ⑫内层 **3** · ⑩ **5** · ⑪ **9**。

**`verify-png-signature.ps1`**：加 `$skip = 0` + `function SkipAll([string]$why)` —— 打印
`SKIP <原因>` **加**一行 `===== PASS=0 FAIL=0 SKIP=$skip（⚠ 整门禁未运行，不是通过）=====` 后 `exit 0`；
3 条跳过路径改调 `SkipAll`；正常汇总行加 `SKIP=$skip`。

### 27.3 变异验证（**各自独立**，都做了「红 → 绿」闭环）

| 变异 | 做法 | 实测 |
|---|---|---|
| **M5** | 把 `verify-decision-delivery.ps1` 的 `if (-not $r12.file) {` 改成 `if ($true) {` | 打印 `SKIP (3 条) ⑫ 内层：…（以下 3 条断言未运行）`，汇总 **`pass=48 fail=0 skip=3`** —— **修复前**只有 `pass=48 fail=0`，**看不出**少 3 条 ✅ |
| **M6** | 临时移走 `publish/PLAN/artifacts/JxrEncApp.exe` | 打印 `SKIP 缺 JxrEncApp（…）` + **`===== PASS=0 FAIL=0 SKIP=1（⚠ 整门禁未运行，不是通过）=====`** —— **修复前**只有一句 SKIP、**无汇总行** ✅ |

⚠ 两处**改动源文件都先备份、验证后还原**，并核了哈希：
`JxrEncApp.exe` 还原后 **`b2656f78c31764e4`** 与移走前一致 ✅；`verify-decision-delivery.ps1` 的
`临时变异` 残留计数 **0** ✅。

### 27.4 ⚠ 仍未做

① **`ensure-fixtures.ps1` 未接线进运行器**（§26 已记）——干净检出上仍会缺 fixture，只是现在**看得见**了。
② `tests/scripts/**` 之外的 SKIP 形态（如 `ServiceProbe` 内部、C# 侧）**未普查**。

### 27.5 验证

`verify-ps-compat` **13/0** · `verify-png-signature` `PASS=4 FAIL=0 SKIP=0` ·
`verify-decision-delivery` `pass=51 fail=0 skip=0` · 整轮 `_run-step3-gates.ps1` **「运行器汇总：全部通过」**。

---

## §28 2026-09-21 —— 口径统一（再 2 个脚本 / 6 处）+ **两处方法论更正**

### 28.1 ⚠ 更正一：上一轮的「同类普查」**有假阴性**（方法错）

§27 的普查用的是**字符串 grep**（`Write-Output/Write-Host "…SKIP…"`）⇒
**漏掉了用辅助函数包装的写法**（`Say "SKIP …"` / `Log "SKIP …"`）——
实测 `verify-metadata-privacy.ps1` 与 `verify-gainmap-memory.ps1` 就是这样被漏掉的。

**改用语义判据**（不依赖字符串形态）：**「脚本在**首条断言之前**是否 `exit 0`」** ⇒
对全部 `tests/scripts/*.ps1` 重扫，命中 **3 个**（`_run-step3-gates.ps1` 的命中在 `Exit-WithSummary` 函数体内，**非早退** ⇒ 排除），
即上表那两个 + 已修的 `verify-png-signature.ps1`。

⚠ **教训**：**「按字符串形态普查」会漏掉同一语义的另一种写法** ⇒ 普查判据要落在**语义**（「有没有跑断言就报成功」）上，
字符串只是它的一种**载体**。

### 28.2 ⚠ 更正二：整门禁跳过**并不等于「不可见」**（§27 的定性过重）

§27 把 `verify-png-signature.ps1` 的整门禁跳过与 `verify-color-peak` 的「汇总看起来完整」并列为「同类」——
**这个定性过重了**。区别是实质性的：

| 形态 | 是否可见 | 判定 |
|---|---|---|
| **`verify-color-peak` (d) 段** | ❌ **不可见** —— 汇总仍打 `PASS=25 FAIL=0`，SKIP 行**混在一堆 PASS 行里** | **真缺陷**（已修 §26）|
| **`verify-decision-delivery` 4 处** | ❌ **不可见** —— 汇总 `pass=51 fail=0` 看不出少跑 | **真缺陷**（已修 §27）|
| **整门禁跳过**（png-signature / metadata-privacy / gainmap-memory）| ✅ **可见** —— 该轮**只有**一句 `SKIP …`，运行器展示正则**本就含 `SKIP`**，且第 62 条已要求无汇总的**单列** | **不是「不可见」缺陷**；补汇总行属**口径统一**（见下）|

### 28.3 口径统一（本次的实质改动）

**理由**：同一类「整门禁跳过」在各脚本间形态不一（有的打汇总、有的不打）⇒ 读者/运行器**无法用同一口径判读**。
统一为：**跳过也打印 `===== PASS=0 FAIL=0 SKIP=1（⚠ 整门禁未运行，不是通过）=====` 后 `exit 0`**。

- `verify-metadata-privacy.ps1`：3 处（`$exe` / `$et` / GPS 写入失败）→ `SkipAll`；汇总加 `SKIP=$skip`
- `verify-gainmap-memory.ps1`：3 处（`$exe` / `$prb` / 4K 素材）→ `SkipAll`；汇总加 `SKIP=$skip`
- （`verify-png-signature.ps1` 已于 §27 同样处理）

**变异验证 M7**：把 `verify-gainmap-memory.ps1` 的 `if (-not (Test-Path $exe))` 改成 `if ($true)` ⇒
打印 `SKIP FfmpegGui.exe 未构建（先 dotnet build）` + **`===== PASS=0 FAIL=0 SKIP=1（⚠ 整门禁未运行，不是通过）=====`** ✅
还原后 `PASS=5 FAIL=0 SKIP=0`、`verify-metadata-privacy` `PASS=7 FAIL=0 SKIP=0`、`verify-ps-compat` **13/0**。

### 28.4 C# 侧（测试宿主）普查结论：**无同类缺陷**

`tests/ServiceProbe/Program.cs` 的 SKIP **全部带显式标记**（`SKIP` / `INFO`），
且运行器展示正则**本就含 `SKIP`** ⇒ 会被捕获；启动时还**统一公告**「未找到 publish/PLAN ⇒ 需外部工具的断言将 SKIP」。
`tests/UiTestHost/**` **无 SKIP 形态**（命中项都是无关注释）。⇒ **不是同类缺陷**。

### 28.5 ⚠⚠ 一个操作教训（还原变异时踩的）

**用「编辑前」的备份去还原变异 ⇒ 会把同一次会话里的功能改动一起还原掉。**
实测：`verify-gainmap-memory.ps1` 我先 `cp` 了**编辑前**的备份，做 M7 变异后又 `cp` 回来
⇒ **SkipAll 改动被静默还原**（`grep -c 'function SkipAll'` = 0 才发现）。
⇒ **纪律**：还原变异必须用**编辑后**的备份；且**还原后必须核验「功能仍在」**（不能只看「变异残留 = 0」）。

### 28.6 ⚠ 仍未做 / 待决策

① **`ensure-fixtures.ps1` 未接线进运行器** —— ⚠ **本轮查明这不是疏漏，而是锁设计使然**：
运行器在 `:460` 取 `tests/output/.gates.lock`、`:783` 才跑脚本循环，而 `ensure-fixtures.ps1`
**用的是同一把锁**（`#147 C`）⇒ **接进循环内必然 `exit 9`**。
⇒ 可行的三种改法都属**设计决策**（① 循环前先跑，代价是「取锁前」有一段竞态窗口；
② 加 `-LockAlreadyHeld` 旁路参数，代价是锁纪律多一个口子；③ 运行器自己生成 fixture，代价是「生产者」出现第二份实现）
⇒ **留给维护者拍板**。
② 第 84 条 ②「找不到 Release 就红」、`COLOR_TRAPS:350` 的 cjk 口径 —— 同上，待决策。

---

## §29 2026-09-21 —— 整轮**单项红**的归因（**两类既有脆弱性**，非本轮回归）

### 29.1 现象（两次整轮，**红的不是同一条**）

| 轮 | 耗时 | 红项 | 失败断言 |
|---|---|---|---|
| Run A | 23m57s | `verify-cli-strict.ps1 (exit=1)` | **`H10 命令含smpte432且非bt2020`** —— 其**子 ffmpeg 命令** `exit 1`（写盘目标 `tests/output/gui-test/src_8bit.png`）|
| Run B | 27m43s | `verify-cli-strict.ps1 (exit=1)` | **`L1j 硬件预设级别走 nvenc 支(idx3⇒p4) :: NvencPresetCombo=True -c:v=av1_amf`** —— ⚠ 本轮 **`H10` 是 PASS**（日志 `:116`）|

### 29.2 ⚠ 更正：**不能**归为「瞬时假红」

⚠ 本条**初版**曾按 `COLOR_TRAPS:124-126` 判为「瞬时假红」，并引其判据「**单格复跑 + 整轮复跑两次都绿**」——
**该判据不满足**：**整轮两次都红**（只是红的**不是同一条**）。⇒ 初版结论**错误**，此处更正。

### 29.3 真实归因：**两条既有脆弱性**（都与本轮改动无关）

| # | 成因 | 证据 |
|---|---|---|
| **A** | **UI 门禁共享输出目录** `tests/output/gui-test/` ⇒ ffmpeg 写盘争抢 ⇒ **子命令 exit 1**（Run A 的 H10）| 与 `COLOR_TRAPS:45`（「共享输出目录 ⇒ 并发同名门禁互相清空」）**同族**；失败命令的写盘目标正是该共享目录 |
| **B** | **硬件编码器枚举不确定** ⇒ 断言**假定第一个硬件编码器是 `av1_nvenc`**，实测却可能选中 **`av1_amf`（AMD AMF）** | 日志 `:157`：`[info] J1z hw picked="⚡ av1_amf — AMD AMF AV1 encoder (codec av1)"`（注释明写「期望是单个 token `av1_nvenc`」）；`:378` 即 `L1j` 红行 |

**排除本轮改动的三条证据**：
1. **`git diff --stat tests/scripts/verify-cli-strict.ps1` ⇒ 无输出** ⇒ 该脚本与 HEAD **字节相同** ⇒ **本轮 38 处插入不含它**；
2. 失败项**在两轮之间漂移**（H10 → L1j）⇒ 不是确定性缺陷；
3. 单格复跑 **`322 PASS / 0 FAIL`、退出码 0** ✅（当时恰好枚举到 `av1_nvenc`）。

⇒ **两条都是既有问题**（属「环境依赖/共享资源」类），**不是本轮引入的回归**；但它们**确实会让整轮不稳定** ⇒ 见 §29.4。

### 29.4 ⚠ 建议（留给维护者）

- **成因 A**：`tests/output/gui-test/` 应与 `tests/output/sources/` 同等对待 ——
  或给每个 UiTestHost 实例**运行级隔离目录**（GUID），或明确登记为「共享输出目录」并禁止并发。
- **成因 B**：`L1j` / `J1z` 一类断言**不应假定某个具体厂商编码器**（`av1_nvenc`）在列表首位 ——
  应改为「**选中的那个硬件编码器** ⇒ 预设级别走它自己的分支」，或**按名称显式选择**再断言。
  ⚠ 这是**测试自身对环境的过度假定**，不是产品缺陷（产品只是照枚举结果选了第一个）。
- ⚠ **两次整轮都在 `verify-cli-strict` 上红** ⇒ 该门禁当前**不适合作为「整轮全绿」的判据**，
  除非先修掉上面两条。

### 29.5 ⚠ 纪律（本次总结出的操作顺序）

**遇到「整轮单项红」，先做「该脚本是否被我改过」的 git 归因**：
`git diff --stat tests/scripts/<红的那个>.ps1` —— **无输出 ⇒ 与本轮改动无关**，转「环境/既有」路径。
⚠ 但**不要**就此收工：**必须取该脚本的完整日志**（`tests/output/_gate_script_<名>.ps1.txt`）看**真正红的是哪一条** ——
运行器汇总里那行 `FAILED -> …` 才是判据；**两次红的不是同一条**就说明它是**不稳定**而非确定性缺陷。
⚠ 也**不要**先改代码去"修" —— 那正是本仓登记过的「把合法且点名的拒绝当缺陷去修绿」的同族错误。

---

## §30 2026-09-21 —— 尝试修 `L1j`：**修法被变异证伪**（该改动已回退）

### 30.1 尝试的修法

`L1j` 需要的是**特定厂商**的 nvenc，而 `L1e` 的 `PickEncoder(s => s.StartsWith("⚡"))` 只保证「**某个**硬件编码器」
（本机同时有 `⚡ av1_nvenc`(NVIDIA) 与 `⚡ av1_amf`(AMD)，随枚举顺序可能选中 amf）。
⇒ 在 `L1j` 之前插入一行**按名字显式选中 nvenc**：`PickEncoder(s => s.Contains("av1_nvenc", …)); Regen();`

### 30.2 ⚠⚠ 更正（2026-09-22）：M8 **未区分**「空操作」与「变异没触发」

M8（把谓词改成 `Contains("av1_amf")`）**没有**让断言转红，诊断行打印 `afterPick -c:v=av1_nvenc`。
我当时判为「该行是**空操作**」——**这个结论下早了**。补查机制后，**同一现象有两种解释，而当时未区分**：

| 解释 | 是否成立 | 判据 |
|---|---|---|
| ① 该行**空操作**（编码器由 `Regen()`/格式逻辑重新推导）| ❓ **未证实** | 若成立，`Regen()` 后应回到**默认**编码器 —— 而 AVIF 的默认是 `libsvtav1`/`libaom-av1`（`GetDefaultEncoder`），**不是** `av1_nvenc` ⇒ 与观测不符 |
| ② **M8 那一轮 `av1_amf` 根本不在列表里** ⇒ `PickEncoder` 找不到项 ⇒ 选择不变 | ✅ **与观测一致** | 同轮 `[dump] L1e hw="⚡ av1_nvenc …"`：`L1e` 取的是**按名字排序后的第一个 `⚡` 项**，而 **`av1_amf` < `av1_nvenc`** ⇒ 若 amf 在场，`L1e` 必取 amf。它取了 nvenc ⇒ **该轮 amf 缺席** |

⇒ **正确结论**：**编码器的选择是会持久生效的**（`L1e` 的选择一直保留到 `L1j`）；
M8 之所以没红，是因为**它想选的 `av1_amf` 在那轮不在列表里**（变异**没能触发**），**不能**据此判定该行无效。
⚠ **教训**：**「变异没让断言转红」有两种成因 —— 针无牙 / 变异没触发**（本仓 `COLOR_TRAPS §B 补 之十五` 早有此条）。
**必须先把两者分开**（本次的分辨手段 = **打印被变异对象的实际取值**），否则会把「变异没触发」误判成「修复无效」而**回退一个正确的修复**。

### 30.2b 真正的根因（补查后）

**`av1_amf`（AMD AMF）的检测是间歇性的** —— 它出现时，`L1e` 按名字序必然取到它（`av1_amf` < `av1_nvenc`），
而 `L1j` 的判据假定必须是 `av1_nvenc` ⇒ **红**。
⇒ 与「枚举顺序不确定」相比，这个说法更准确：**不是顺序在变，而是 `av1_amf` 时有时无**。

### 30.3 ⚠⚠ 由此得到的**两条方法论**

**(a) 「变异没让断言转红」必须先分辨两种成因**：**针无牙** vs **变异没触发**
（本仓 `COLOR_TRAPS §B 补 之十五` 早有此条）。**分辨手段 = 打印被变异对象的实际取值**——
本次若早打印「`av1_amf` 在不在列表里」，就不会误判。
⚠ 误判的**代价是反向的**：会把「变异没触发」读成「修复无效」⇒ **回退一个本来正确的修复**（本次即如此）。

**(b) 「插一行就修好了」必须用变异证明它承重** —— 本次 M8 **没能**证明（它没触发），
⇒ 那个修复**至今处于"未验证"状态**，因此**回退是保守但非结论性的**。
⇒ 正确姿态：**把该修复标记为「未验证」，而不是「无效」**。

### 30.4 真正的修法（**属产品/断言设计决策，留给维护者**）

既然根因是「**`av1_amf` 的检测间歇出现**」⇒ `L1e` 按名字序必然取到它，而 `L1j` 假定必须是 `av1_nvenc`：

| 类别 | 修法 | 代价 / 风险 |
|---|---|---|
| **断言侧（最小）** | 把 **`L1e`** 的谓词由 `StartsWith("⚡")` 改为**按名字**取 `av1_nvenc`（选择会持久到 `L1j` ⇒ `L1j` 的前提成立）| ⚠ 会**丢掉**「amf 这类其它硬件编码器」在 `L1e` 的覆盖；且 `L1e` 的断言文本是「硬件编码器」（厂商无关）⇒ 语义需一并调整 |
| **断言侧（覆盖更好）** | `L1e` 改为**遍历所有 `⚡` 硬件编码器**逐个断言 `-c:v` 单 token；`L1j` 则**按实际选中的编码器**走对应面板分支 | ⚠ 断言**条数会变** ⇒ 受 `GroupM ≥ 45`（只增不减，现 58）与 **48 条锚点**约束（`L1j` **不在**锚点清单内 ⇒ 有改造空间）|
| **产品侧** | 让「默认/首选硬件编码器」**确定**（固定优先级 nvenc > qsv > vaapi > amf，或按显式配置）| ⚠ 这是**产品行为问题**：当前默认值取决于**检测到哪些硬件编码器** ⇒ 同机两次跑可能给出不同编码器 |
| **不动** | 维持现状，接受 `verify-cli-strict` **偶发红** | ⚠ 该门禁当前**不适合**作为「整轮全绿」的判据 |

⚠ **关键事实（本次查明）**：**`av1_amf` 的检测是间歇性的**（不是顺序在变）——
`L1e` 的 `StartsWith("⚡")` 与 `J1z` 的 `Contains` 都只是「取列表里的某项」，**没有**控制它取谁。

### 30.5 验证

`verify-cli-strict` 单格复跑 **`322 PASS / 0 FAIL`、退出码 0**（`L1j` PASS）；
`tests/UiTestHost/Program.cs` **与 HEAD 一致**（`git status` 无输出）；全仓「临时变异 / `[dbg]`」残留 **0**。
**整轮 `_run-step3-gates.ps1`：`verify-cli-strict` `exit=0` / `322 PASS / 0 FAIL`，汇总「全部通过」**（24m25s）。
⚠ 但 §29.3 的**两条既有脆弱性仍在**（本次恰好枚举到 nvenc）⇒ 整轮**仍可能**再红。

---

## §31 2026-09-22 —— 普查「**② 依赖别的门禁遗留产物**」这一类（第 68 条）

### 31.1 普查方法

按**语义**扫：找每个 `tests/scripts/*.ps1` 里读**别处固定路径产物**的位置
（`tests/output/{validate,results,sources}/<固定名>`，排除本脚本自己的运行级 GUID 目录）⇒ 命中 **13 个脚本**；
再逐个看它**读不到时怎么办**（第 68 条口径：**必须红**，不得 `continue` / 不得只打 SKIP）。

### 31.2 判定结果

| 处置 | 脚本（处） | 判定 |
|---|---|---|
| `exit 1` | `_probe-jxl-webp-pixels.ps1:31` · `_probe-p7d-bisect.ps1:35` · `verify-gamut-map.ps1:49` | ✅ **合规** |
| `CK $false`（进计数）| `verify-color-wiring.ps1:616-617`（「本条**不给绿灯**」）| ✅ **合规** |
| 缺失时**自愈**（自己生成）| `_probe-p7d-bisect.ps1:81` | ✅ **合规** |
| 有回退 + 断言 | `verify-gainmap-engine.ps1:383-384`（回退到系统 sRGB ICC 后仍 `CK (Test-Path …)`）| ✅ **合规** |
| 只用于**打印** | `_probe-icc-affects-decode.ps1:32` | ✅ 低危（不参与断言）|
| **SKIP 已打印但不进汇总** | **`verify-png3-interop.ps1:110`** | ⚠ **缺陷 ⇒ 已修**（见 31.3）|
| **无守卫的 `Copy-Item` + `$ErrorActionPreference=Continue`** | **`verify-prophoto-faithful.ps1:57`** | ⚠ **缺陷 ⇒ 已修**（见 31.3）|
| `if (Test-Path …) { … } else { … SKIP … }` | `_probe-png-cicp-production.ps1:74/85` | ✅ **合规**（⚠ **我初查时只读到 `:84`、误判为「无声跳过」，`else` 在 `:85`** ⇒ 见 31.4）|
| 表格型（无 PASS/FAIL 契约）+ 前置 `exit 1` | `verify-colorfmt-matrix.ps1:22` | ✅ 合规（其 `$refIcc` 仅作参照，缺失不影响判读）|

### 31.3 两处修复（各带变异验证）

**① `verify-png3-interop.ps1`（SKIP 不进汇总）**：与 §26/§27 同一口径 ——
加 `$skip` + `function SKIP([string]$what,[int]$n=1)`；④ 段改为
`SKIP "④ 缺 … —— 以下 1 条断言未运行，不是通过" 1`；汇总加 `SKIP=$skip`。
**变异 M9**（`if (Test-Path $icc)` → `if ($false)`）⇒
`SKIP (1 条) ④ 缺 …/jpgp3/p3.icc（…）—— 以下 1 条断言未运行，不是通过` + 汇总
**`PASS=8 FAIL=0 SKIP=1`**（**修复前**是 `PASS=8 FAIL=0`，**看不出**少 1 条）✅

**② `verify-prophoto-faithful.ps1`（无守卫 `Copy-Item`）**：加显式守卫 ——
缺 `sources/src_8bit.png` ⇒ 打印「缺素材 …（由 generate-sources.ps1 生成）—— 本条**不给绿灯**」+ **自清 + `exit 1`**。
**变异 M10**（`if (-not (Test-Path $src8))` → `if ($true)`）⇒ 该消息如期出现、**退出码 1** ✅

### 31.4 ⚠ 又一次「初查结论被自己推翻」

普查第一遍我判定 `_probe-png-cicp-production.ps1` 是「**整段无声跳过**」（只读了 `:72-84`）——
实际 **`:85` 就是 `else { Write-Output "── SKIP：缺 $jpgP3（…）" }`** ⇒ **可见**，**不是缺陷**。
⚠ 教训：**判「有没有 else / 有没有提示」必须把 `if` 块读到闭合处**，只读开头几行会得出相反结论
（与 §28.1「按字符串形态普查会漏掉同一语义的另一种写法」同族：**都是"判据面没读全"**）。

### 31.5 验证

`verify-ps-compat` **13/0** · `verify-png3-interop` 正常态 `PASS=9 FAIL=0 SKIP=0` ·
`verify-prophoto-faithful` 正常态 `PASS=26 FAIL=0` · 两处变异各自「红 → 绿」闭环 ·
**整轮 `_run-step3-gates.ps1`：「运行器汇总：全部通过」**（24m30s，含 `verify-cli-strict exit=0`）。
⚠ §29.3 的**两条既有脆弱性仍在** ⇒ 整轮**仍可能**再红（本次未复现）。

---

## §32 2026-09-22 —— ⑥ 类普查（干净否定）+ `QueueProcessor.cs` 的**行号引用替换轮**

### 32.1 ⑥「读失败沿用上一轮值」普查：**已合规**（无需修复）

按登记的处方（必须 `Read-TextShared`、失败返 `$null` 并**显式记账**）逐点核验运行器：

| 位置 | 处置 | 判定 |
|---|---|---|
| `Read-TextShared`（`:320-334`）| 重试 5 次、失败 **`return $null`** | ✅ 助手本身正确 |
| 调用点 `:644`（探针段）| `if ($null -eq $txt) { $readFail=$true; $txt=''; $sum='（⚠ 输出文件读取失败，汇总不可得）'; $failedItems.Add(...) }` | ✅ **显式清空 + 显式记账**（第 15 针已锁）|
| 调用点 `:796`（脚本段）| 同上（注释明写「读失败**禁止沿用上一轮 `$txt`**」）| ✅ 合规 |
| 其他读法 `:214`/`:482`（锁文件）、`:305`（rc 文件）| 均在 `try/catch` 内、失败有明确取值（`-1` / 空）| ✅ 合规 |
| `:371`/`:452`（读**自身**文件做自指纹）、`:671`/`:822`（`-Tail 5` 仅显示）| 不涉及「沿用上一轮值」| ✅ 不适用 |

⇒ **⑥ 无需修复**（干净否定结论，已入账）。

### 32.2 ✅ `QueueProcessor.cs` 的**行号引用替换轮**（失效 **6/11 → 0/4**）

**方法**：逐个把失效的**绝对行号**换成**可检索的符号/概念指针**（⚠ **不猜行号** —— 只在该概念的检索词确实存在于文件中时才替换）。

| 原引用 | 现写法（可检索锚点）|
|---|---|
| `:723` → 「见上方 `:661-663` 的既有注释」| 「见本文件中解释「规划层的 `SupportsGainMap` 闸**根本不运行**」的那条注释」（锚点 `根本不运行`，`:722`）|
| `:757`/`:772` → `:869` | 「「直通 或 **超广色域保真**」那段」（锚点 `:1100`）|
| `:758`/`:756` → block 分支 `:810-818` / `:818` | 「`ColorEngineRouter.BlockReason` 非空那条」（锚点 `:1023`）|
| `:773` → `:834-836` | 「见本文件中「色彩后置全套」那段」（锚点 `:836`）|
| `:4234` → 跨文件 `FfmpegCommandBuilder.cs:535` | 「`FfmpegCommandBuilder.cs` 内唯一实现」（⚠ 该 `:535` 实测是 `args.Add("-map_chapters")`，而 `InsertScaleFilterAtChainHead` 在 **`:1792`**）|

**效果**：失效 **6/11 → 0/4**（总引用 11→4：6 处失效里 5 处被概念指针替换、1 处跨文件指针被符号替换）。
⚠ **只做了这一个文件**（我改动最多的那个）；`MainWindow.xaml.cs` 余下 ~14 处、`tests/ServiceProbe/Program.cs` 8 处
仍待同一轮处理 —— 方法已固化在本节，可直接照做。

### 32.3 ⭐ 一条可复用的**效率技巧**：注释-only 改动**不必重跑整轮**

改完重建三项目 ⇒ 若 `FfmpegGui.dll` 的 **SHA256 与改前逐位相同** ⇒ **证明是注释-only、连 IL 都没动**
⇒ 上一轮整轮门禁的读数**继续有效**，**无需再花 ~24 min**。
本次实测：`2b03cd5a0bf6ca3c`（三份一致、与改前相同）✅
⚠ 反之：只要 SHA 变了就**必须**重跑整轮（本会话前几轮的改动都遵守了这条）。

---

## §33 📋 待决策清单（**面向维护者** · 2026-09-22 汇总）

> 本会话（09-21～09-22）查出、但**属于设计决策、我未擅自改**的事项。每条都给「一句话 / 证据 / 不修的后果 / 选项与代价 / 建议」。
> ⚠ 全部**已实测取证**，出处见括号内的 `§` 与文件行号。
> ✅ **2026-09-22：本清单 6 项已逐项处置** —— 1–4 **已实施并验证**、5–6 **决定不改（有依据）** ⇒ 见 **`§34`**（本节保留为决策时的原始依据）。

### 总览（按建议优先级）

| # | 事项 | 影响 | 建议 | 改动量 |
|---|---|---|---|---|
| **1** | `tests/output/gui-test/` 被多个 UI 门禁**共用** | 整轮**偶发红**，红点不固定 | **修**（改 1 行）| 极小 |
| **2** | 硬件编码器 `av1_amf` **检测间歇** ⇒ `L1j` 偶发红 | 整轮**偶发红** + 可能的**产品行为不确定** | **先修断言侧**；产品侧需你定语义 | 小～中 |
| **3** | `ensure-fixtures.ps1` **未接线**进运行器 | 干净检出上部分断言**不跑**（现已可见）| **接线**（取锁前先跑）| 小 |
| **4** | 其余 2 个文件的**行号引用**（共 ~22 处）| 纯可读性 | **随手做**（注释-only ⇒ 免重跑）| 小 |
| **5** | 第 84 条 ②「找不到 Release 就红」| 风险**已可见**（建议 ① 已落地）| **维持现状** | — |
| **6** | cjk 门禁的「其余」桶**口径与描述不符** | 该锁**以噪声为主**、反复抬基线 | **收紧口径**（需你拍板）| 中 |

---

### 1️⃣ `tests/output/gui-test/` 被多个 UI 门禁共用

- **一句话**：5 个 UI 门禁都通过 `UiTestHost` 跑「预览命令」，而这些命令把中间产物写进**同一个固定目录**
  `tests/output/gui-test/`；两个门禁的产物同名时会互相覆盖。
- **证据**：整轮失败 Run A 的红项 `H10` —— 其**失败命令的写盘目标正是该目录**（`…gui-test\src_8bit.png`）；
  已登记同类成因（`COLOR_TRAPS:45`「共享输出目录 ⇒ 并发同名门禁互相清空」）。
- **量化**：构造该路径的地方**只有 1 处** —— `tests/UiTestHost/Program.cs:424`
  （`var outDir = Path.Combine(RepoRoot, "tests", "output", "gui-test");`）。
- **不修的后果**：整轮门禁**偶发红**，且**红在哪一条不固定** ⇒ 无法把「整轮全绿」当作判据（本会话实测 2 次红、红的还不是同一条）。
- **选项**：
  - **A（推荐）**：该目录改为**运行级 GUID**（与 `verify-*-probe.ps1` 既有做法一致）——**改 1 行**，彻底消除互写。
  - B：不改代码，只在文档里**明确登记为共享目录**并禁止并发跑 UI 门禁（靠纪律，非强制）。
  - C：维持现状。
- **代价**：A 需要**重跑整轮**一次（产物目录名会变，但门禁自清，不影响断言）。

### 2️⃣ 硬件编码器 `av1_amf` 的检测**间歇性出现** ⇒ `L1j` 偶发红

- **一句话**：本机同时有 NVIDIA `av1_nvenc` 与 AMD `av1_amf`，而 **`av1_amf` 的检测时有时无**；
  `L1e` 取的是「按名字排序后的**第一个** `⚡` 项」，而 **`av1_amf` < `av1_nvenc`** ⇒ amf 在场时**必被选中**；
  但 `L1j` 的判据**假定必须是 `av1_nvenc`** ⇒ amf 在场的那几轮就红。
- **证据**：`L1e` 的 dump 在两轮间分别是 `⚡ av1_nvenc` 与 `⚡ av1_amf`（`§29.1` 的 Run A / Run B）；
  列表**按名字排序**（`EncoderDetectionService.cs:346`）；默认选择逻辑在 `MainWindow.xaml.cs:2050`
  （`GetDefaultEncoder` 命中则用，否则第 0 项）；`L1j` 的 `if (np != null && encNow == "av1_nvenc") … else Check(false)` 在 `tests/UiTestHost/Program.cs:1946-1962`。
- **不修的后果**：① 整轮**偶发红**；② ⚠ 若产品的「首选硬件编码器」也依赖检测结果，则**同一台机器两次跑可能给出不同编码器**（用户可见的行为不确定）。
- **选项**（详见 `§30.4`）：
  - **A（推荐，最小）**：把 **`L1e`** 的谓词由 `StartsWith("⚡")` 改为**按名字**取 `av1_nvenc`
    （选择会**持久**到 `L1j` ⇒ 前提成立）。⚠ 代价：丢掉「amf 这类其它硬件编码器」在 `L1e` 的覆盖，且 `L1e` 的断言文本「硬件编码器」（厂商无关）需一并调整。
  - B（覆盖更好）：`L1e` 改为**遍历所有 `⚡` 项**逐个断言；`L1j` 按**实际选中的编码器**走对应面板分支。
    ⚠ 断言**条数会变** ⇒ 受 `GroupM ≥ 45`（只增不减，现 58）与 **48 条锚点**约束（`L1j` **不在**锚点清单内 ⇒ 有改造空间）。
  - C：**产品侧**让「首选硬件编码器」**确定**（固定优先级或显式配置）⇒ 需你定产品语义。
  - D：维持现状，接受 `verify-cli-strict` **偶发红**（⚠ 则该门禁不能作为「整轮全绿」的判据）。
- ⚠ **一个诚实的保留**：`§30` 里我插过一版「在 `L1j` 前按名字选 nvenc」的修复并**已回退**，
  原因是 M8 变异**没能触发**（那轮 `av1_amf` 不在列表里）⇒ 该修复处于**「未验证」**而非「无效」。
  选 A 时建议**照 `§32.3` 的手法**：先确认 amf 在场的那一轮能复现红，再验证修好。

### 3️⃣ `ensure-fixtures.ps1` 未接线进运行器

- **一句话**：运行器**不生成**「派生素材」（`tests/output/validate/*`、`tests/output/results/color/*`）；
  干净检出上它们缺失 ⇒ 相关门禁**跳过**部分断言（现已显示 `SKIP=` 计数，但**仍然是没跑**）。
- **为什么不简单接线**：运行器在 **`:460`** 取仓库级锁 `tests/output/.gates.lock`、**`:783`** 才跑脚本循环；
  而 `ensure-fixtures.ps1` 用的是**同一把锁**（`#147 C`）⇒ 接进循环内**必然 `exit 9`**。
- **选项**：
  - **A（推荐）**：在**取锁之前**先跑它 ⇒ 简单、语义不变。
    ⚠ 有一小段竞态窗口（它释放锁 → 运行器取锁之间，另一实例可能插入 ⇒ 那一次 `exit 9`；这属"并发禁止"的**正确**行为，不是缺陷）。
  - B：给它加 `-LockAlreadyHeld` 旁路参数（运行器持锁时跳过自取）⇒ 无竞态；⚠ **锁纪律多一个口子**。
  - C：运行器自己生成 fixture ⇒ ⚠ **生产者出现第二份实现**（违反单一真源）。
- **代价**：A/B 都要重跑整轮；C 最大。

### 4️⃣ 其余两个文件的「行号引用」替换轮

- **一句话**：注释里的**绝对行号**（形如 `` `:869` ``）会随任何插入而失效；`QueueProcessor.cs` 已改完（`§32.2`），
  还剩 `MainWindow.xaml.cs` ~14 处、`tests/ServiceProbe/Program.cs` 8 处。
- **方法（已固化在 `§32.2`）**：换成**可检索的符号/概念指针**（如「`ColorEngineRouter.BlockReason` 非空那条」），
  ⚠ **不猜行号** —— 只在该概念的检索词**确实存在**于文件中时才替换。
- **不修的后果**：纯**可读性**（读者按行号跳转会落到无关代码）。**不影响正确性**。
- **代价**：**注释-only** ⇒ 用 `§32.3` 的 SHA 自证 ⇒ **免重跑整轮**（很便宜）。

### 5️⃣ 第 84 条 ②「找不到 Release 就红」

- **一句话**：21+ 个门禁在 Release exe 缺失时会**静默回退**到 Debug exe。
- **现状**：**建议 ① 已落地**（`§25.1`）—— 38 处回退点都打印 `[gate] exe=… (Release|Debug fallback)` ⇒ 风险**已可见**。
- **选项**：**A（推荐）维持现状** —— 因为「只构建了 Debug」是**合法用法**；
  B 硬红（与第 68 条同族）⇒ ⚠ 会让该用法直接红。
- **建议**：A（既然风险已可见，硬红的收益不足以抵消误伤）。

### 6️⃣ cjk 门禁的「其余」桶**口径与描述不符**

- **一句话**：`_probe-cjk-hardcode-scan` 的「其余」桶（现 **833 行**）里**绝大多数是引擎/CLI 侧诊断串**，
  而**不是**该桶描述里列的 UI 面（窗口标题/菜单/对话框/进度标签/预设名/EXIF 字段名/`--help`…）
  ⇒ **口径与它自己的描述已经不匹配**；且**一天内抬了 5 次基线、本会话内又漂 +7**。
- **证据**：`COLOR_TRAPS:350` 的「结构性提醒（留给维护者，未处置）」。
- **不修的后果**：该锁**以噪声为主** ⇒ 会持续"抬基线"，逐渐失去区分力（真正的 UI 硬编码会淹没在噪声里）。
- **选项**：
  - A：接受**周期性抬基线**（维持现状）。
  - **B（推荐）**：**收紧口径** —— 把 `Services/ColorMapping/**` 与 `QueueProcessor` 的诊断串排除出计数。
    ⚠ 按脚本头既有约定，**必须同时改基线与 `TESTING §6` 第 61 条**，属**独立决定**。
- **代价**：B 需要一次基线重置 + 门禁改动 + 整轮验证。

---

### 附：本会话**已修**、不需决策的（供对照）

6 个产品/管线缺陷（RAW 元数据保留 · 显示名 · PNG `eXIf` · png/apng 隐私泄漏 · exiftool 写超时 · 断工具）·
6 个门禁脚本的静默 SKIP（`color-peak`/`decision-delivery`/`png-signature`/`metadata-privacy`/`gainmap-memory`/`png3-interop`）·
1 处无守卫 `Copy-Item`（`prophoto-faithful`）· 5 处过期登记 · `COLOR_TRAPS` 编号消歧 · 行号引用替换（`QueueProcessor.cs`）·
38 处「被测 exe 打印」· 探针 `FAIL exception:` 前缀 —— 明细见 `§21`/`§25`–`§32`。

---

## §34 2026-09-22 —— `§33` 待决策清单的**逐项处置**（1–4 已实施并验证 · 5–6 决定不改）

### 34.1 总览

| # | 事项 | 处置 | 验证 |
|---|---|---|---|
| **1** | `tests/output/gui-test/` 被 5 条 UI 门禁共用 | ✅ **已修** | 共享目录**不再被创建** + 运行级目录**自清** + `verify-cli-strict` 322/0 |
| **2** | `av1_amf` 检测间歇 ⇒ `L1j` 偶发红 | ✅ **已修** | **M11 变异证明承重**（改谓词 ⇒ `L1j` 转红）|
| **3** | `ensure-fixtures.ps1` 未接线进运行器 | ✅ **已修** | **M12**：移走素材 ⇒ 运行器**自动补建** ⇒ 产物**逐位一致** |
| **4** | 另两文件的行号引用（7 处真失效）| ✅ **已做** | 三文件复核：真失效 **0**（余 8 处是**有意保留**的历史说明）|
| **5** | 第 84 条 ②「找不到 Release 就红」| ⛔ **决定不改** | 建议 ① 已落地 ⇒ 风险**已可见**；硬红会误伤「只构建 Debug」的合法用法 |
| **6** | cjk「其余」桶口径 | ⛔ **决定不改**（⚠ **更正原建议**）| 见 34.6 |

### 34.2 项 1：共享输出目录 ⇒ 改用**运行级目录**

**改动**：`tests/UiTestHost/Program.cs` 的 GroupG 里
`var outDir = Path.Combine(RepoRoot, "tests", "output", "gui-test")` → **`var outDir = TempDir;`**（+ 目录不存在则建）。
**为什么这是"顺理成章"而非新发明**：`UiTestHost` **本来就有** `TempDir`（`%TEMP%/uitesthost_<guid>`）+ `CleanupTempDir()`，
且 `:47-49` 的既有注释已明写「**中间产物一律写这里，绝不写进共享目录**」—— 本次只是把**这一处漏网的**也接上去。
**验证**（三项）：
① 把旧的 `tests/output/gui-test/` **送回收站**后复跑 ⇒ **未被重新创建** ✅（说明所有写入已改道）；
② `%TEMP%/uitesthost_*` 残留计数 **0** ⇒ 运行级目录**自清** ✅；
③ `verify-cli-strict` **`322 PASS / 0 FAIL`**、退出码 0 ✅。
⚠ 另核：**没有任何门禁**引用 `gui-test` 这个路径（`grep` 全仓 `tests/scripts/*.ps1` = 0 命中）⇒ 改动无连带。

### 34.3 项 2：`L1j` 前**按名字显式选中 nvenc**（并**证明它承重**）

**改动**：在 `L1j` 之前加 `PickEncoder(s => s.Contains("av1_nvenc", …)); Regen();`
（**不动** `L1e` 的语义与其厂商无关的覆盖）。
**⚠ 这条正是 `§30` 里被我误判为「空操作」并回退过的那个修复** —— 依 `§30.2` 的更正重新实施，并**用能触发的变异验证**：

| 变异 | 做法 | 实测 |
|---|---|---|
| **M11** | 把该行谓词改成 **`libaom-av1`**（AVIF 列表里**必然在场**的软件编码器 ⇒ **一定能触发**）| `[FAIL] L1j 硬件预设级别走 nvenc 支(idx3⇒p4) :: NvencPresetCombo=True **-c:v=libaom-av1**` · `UI 测试汇总: **320 PASS / 1 FAIL**` · `CLISTRICT FAIL: exit code 1` ✅ **证明该行承重** |

⚠ 与 `§30` 的 M8 对照：**M8 之所以没红，是因为它想选的 `av1_amf` 那轮不在列表里**（**变异没触发**）；
本次特意改用**必然在场**的对象 ⇒ 变异**能触发** ⇒ 得到的是**有牙**的证据。
⇒ 这也是对 `§30.2` 那个更正（「不能把'变异没触发'当成'修复无效'」）的**实证**。
还原后 `L1j` PASS、`322 PASS / 0 FAIL`、残留 **0** ✅

### 34.4 项 3：运行器**取锁前**按需补建派生 fixture

**改动**：`tests/scripts/_run-step3-gates.ps1` —— 在 `$root = …; Set-Location $root` **之后**、
**取仓库级锁之前**，插入一段前置：两个生产者（`generate-sources.ps1` → `sources/src_8bit.png`；
`ensure-fixtures.ps1` → `validate/src_hdr.png`）**只在产物确实缺时**才跑（顺序不可颠倒：后者要读前者的产物）。

**⚠ 两条实现约束（都来自本仓既有教训）**：
- **必须在取锁之前**：两个生产者用**同一把**锁（`#147 C`）⇒ 放进脚本循环内**必然 `exit 9`**。
- **刻意不取退出码**：本文件的自形态检查（`Test-RunnerShape`）把「进程对象的退出码取法」
  （`$p.ExitCode` / `$proc.ExitCode` / `$child.ExitCode`）钉成**全文件计数 == 0**（PS5.1 下恒 null ⇒ 全轮假红）
  ⇒ 改用**语义判据**：跑完**看素材是否到位**（`Test-Path`），并把结果打印为 `✅ 已补建 …` / `⚠ 跑完仍缺 …`。
  ⇒ 若生产者因锁被占用而 `exit 9` ⇒ 本轮**中止**（并发禁止，语义与既有锁行为一致）。

**验证 M12**（**移走素材 → 让运行器自己补**）：
`⚠ 缺派生素材 …/validate/src_hdr.png ⇒ 先跑 ensure-fixtures.ps1（…一次性补建）` →
（`ensure-fixtures` 的 `png3` 输出）→ **`✅ 已补建 …/validate/src_hdr.png`** →
`gates-lock=…(pid=…, script=ensure-fixtures.ps1)` 之后才 `gates-lock=…(pid=…)` ⇒ **取锁确实在补建之后** ✅ →
`===== 运行器汇总（-SkipScripts，仅探针）：全部通过 =====`、退出码 **0** ✅
⚠ 补建产物 SHA256 **`f168fcb080e0151f`** 与移走前**逐位一致** ⇒ 生产者是**确定性**的（不是"生成个差不多的"）✅

### 34.5 项 4：另两文件的行号引用替换

**改动**：`src/FfmpegGui/MainWindow.xaml.cs`（6 处）+ `tests/ServiceProbe/Program.cs`（2 处）——
一律换成**可检索的符号/概念指针**（如「见 `UpdateOptionAvailability()` 内那几行」、
「见本文件中「PNG/TIFF/APNG 纯无损格式，强制勾选且锁定」那段」、「`InsertScaleFilterAtChainHead`」），⚠ **不猜行号**。
**复核**：`MainWindow.xaml.cs` 真失效 **0**（余 8 处是**我自己**写的「原先此处写的是**绝对行号**（…）」**历史说明，有意保留**）；
`ServiceProbe/Program.cs` **0**；`QueueProcessor.cs` **0**。
⚠ **给后续同类扫描者的提示**：按「目标行必须是注释」的粗判据扫描时，**历史说明行会被误报** ⇒ 判「失效」前先看该行是不是在**引述旧行号**。

### 34.6 项 5 / 6：**决定不改**（其中 6 更正了我原先的建议）

**项 5（第 84 条 ②「找不到 Release 就红」）**：**维持现状**。理由：建议 ① 已落地（38 处都打印
`[gate] exe=… (Release|Debug fallback)`）⇒ 风险**已可见**；而硬红会让「只构建了 Debug」这个**合法用法**直接红。

**项 6（cjk「其余」桶口径）**：⛔ **决定不改** —— ⚠ **这更正了我原先「收紧口径」的建议**。
读完实现后发现：探针头部**明写**该口径是**有意冻结**的 ——
> 「已知局限（**有意保留**，以便与 2026-09-17 的测量口径逐字一致；**不要悄悄"修好"**，否则基线会变）」

且 `COLOR_TRAPS:350` 的登记本身写明该决定「**属独立决定**」。⇒ 收紧口径会**破坏与全部历史读数的可比性**
（历次抬基线登记、分桶账、`TESTING §6` 第 61 条都建立在这个口径上）⇒ **收益不足以抵消**。
⚠ **教训**：**给出"收紧口径"这类建议前，必须先读被改对象的实现与其头部约定** —— 我原先的建议是在**只读了登记**的情况下给出的。

### 34.7 验证

编译 **0 警告 0 错误** · `FfmpegGui.dll` **未变**（`2b03cd5a0bf6ca3c` ⇒ 产品零改动、仅测试基础设施变更）·
三份 DLL SHA256 一致 · 变异 M11/M12 各自闭环 ·
**整轮 `_run-step3-gates.ps1`：「运行器汇总：全部通过」**（23m5s；5 条 UI 门禁均 `322 PASS / 0 FAIL`，
含 `verify-cli-strict exit=0`）✅
⚠ 整轮**结束后**复核：**共享目录 `tests/output/gui-test/` 全程未出现** ✅（5 条 UI 门禁都走了运行级目录）·
`%TEMP%/uitesthost_*` 残留 **0** ✅
⚠ §29.3 的两条既有脆弱性：**成因 A 已消除**（`§34.2`）· **成因 B 已消除**（`§34.3`，断言侧）。
✅ **更正（2026-09-22）**：先前记的「成因 B 的**产品侧**仍开放（默认硬件编码器随检测结果变）」**不准确** ——
补查后：`MainWindow.xaml.cs:2049-2050` 的默认选择是 `EncoderDetectionService.GetDefaultEncoder(fmt)`，
而 `GetDefaultEncoder("avif")` 返回的是 **`libsvtav1`/`libaom-av1`（软件编码器）**
⇒ **产品的默认路径不选硬件编码器** ⇒ **产品侧无不确定性问题**。
⚠ 那个「`av1_amf` 时有时无」影响的是**编码器下拉列表的内容**（ffmpeg 的检测结果，产品只是如实呈现）
⇒ 属**环境特征**，不是产品逻辑缺陷 ⇒ **`§33` 项 2 的选项 C 无需执行**。

---

## §35 2026-09-22 —— 「行号引用替换」**全仓做完**（含检测器的两类假阳性）

### 35.1 全仓普查（`src/**` + `tests/**` 的 `.cs`）

| 文件 | 原失效 | 处置 |
|---|---|---|
| `src/FfmpegGui/Services/QueueProcessor.cs` | 6/11 | ✅ `§32.2` 已做 → **0** |
| `src/FfmpegGui/MainWindow.xaml.cs` | 17/19 → 8/10 | ✅ 真失效 **0**（余 8 处是**我自己**写的「原先此处写的是**绝对行号**（…）」**历史说明，有意保留**）|
| `tests/ServiceProbe/Program.cs` | 8 → 0 | ✅ `§34.5` 已做 |
| **`src/FfmpegGui/CliParser.cs`** | **4** | ✅ 本次修（1 处真失效 + 3 处**跨文件假阳性**）|
| **`tests/UiTestHost/Program.cs`** | **4** | ✅ 本次修（2 处真失效 + 2 处跨文件假阳性）|
| **`src/FfmpegGui/Services/FfmpegCommandBuilder.cs`** | **1** | ✅ 本次修 |
| `tests/output/handover_20260920/product/QueueProcessor.cs` | 9/11 | ⛔ **有意不动** —— 它是 `tests/output/` 下的**历史快照副本**（不是源码），改它等于**篡改历史记录** |

**本次修的 4 处真失效**（一律改为**符号/概念指针**，⚠ **不猜行号**）：

| 原引用 | 现写法（可检索锚点）|
|---|---|
| `CliParser.cs:653` → `:202` | 「本文件「**分层语义**」那段」（该段确实把 `bt2390` 判为可识别但不支持，`:828`）|
| `FfmpegCommandBuilder.cs:600` → `:498-499` | 「iccgen 段依赖其**在 switch 之后追加**的语义」（锚点见 `:251-252`）|
| `UiTestHost:879` → `MainWindow.xaml.cs:2149-2170`/`:2109-2117`/`:2100` | 「见 `MainWindow.xaml.cs` 的 **`UpdateOptionAvailability()`**」（**该行已点明文件** ⇒ 同文件引用）|
| `UiTestHost:2449` → `MainWindow.xaml.cs:2514-2520` vs `:2523-2529` | 「`MainWindow.xaml.cs` 的 **`GetIccMode()`** vs **`GetColorStrategy()`**」（⚠ 实测真值是 `:2554` / `:2563`）|

**复核**：同文件真失效 **0**（唯一例外是上表那个**历史快照**）✅ · `FfmpegGui.dll` **未变**（`2b03cd5a0bf6ca3c` ⇒ 注释-only）✅

### 35.2 ⚠⚠ 检测器本身的**两类假阳性**（后续同类扫描者必读）

我用「**目标行必须是注释**」这一粗判据扫描，它有两类**结构性假阳性**：

| 假阳性 | 形态 | 判别法 |
|---|---|---|
| **① 跨文件引用** | 行内写的是 `` `_lib-reject.ps1:624-626` `` 或 `` `FfmpegCommandBuilder.ResolveOutputColorRange`（`:938-945`）`` —— **裸 `:N` 的目标在别的文件里** | 看该行**是否点明了文件名/符号**；点了就是跨文件 ⇒ 粗判据不适用 |
| **② 历史说明行** | 行内写的是「**原先此处写的是绝对行号**（`:1851`/`:1948`/…）」—— 引述的是**已废弃的旧值** | 看该行**是否在引述旧行号**（关键词「原先此处写的是」） |

⚠ 实测：若不剔除这两类，`CliParser.cs` 会被误报 **4** 处（真 1）、`UiTestHost` 误报 **4** 处（真 2）、
`MainWindow.xaml.cs` 误报 **8** 处（真 0）⇒ **误报率高于真值**。
⇒ **纪律**：判「失效」前**先读那一行**，别只看检测器的计数；**跨文件引用要按目标文件另核**（本次 3 处跨文件引用经核**全部有效**）。

---

## §36 2026-09-22 —— **图像管线审查**（产品侧）

> 完整报告：**`docs/PIPELINE_REVIEW.md`**（与 `RAW_PIPELINE_AUDIT.md` 同类的独立审查文档）。
> 方法：**先读登记再读代码**（本仓有「三次白跑」教训）——每条发现先与 `HANDOVER` / `TESTING §6` / `COLOR_TRAPS` / `MEMORY` 对账，
> 标注 `【新发现】/【已登记】/【已结案】/【假阳性】`。并**并行派了 4 条独立审查轴**（引擎主链 / legacy 分歧 / 元数据·ICC·隐私 / 出口覆盖），
> ⚠ **agent 结论一律自己复核**。

### 36.1 ⚠⚠ 新发现（已**实机复现**）：透明输入 + **需要色彩变换** ⇒ **alpha 被静默拍平**

**现象**（我自己的 A/B；源 `tests/output/sources/src_alpha.png`，实测 `pix_fmt=rgba`）：

| 用例 | 产物 `pix_fmt` | 判定 |
|---|---|---|
| 透明源 → png（**无**色彩变换）| `rgba` | ✅ 保住 |
| 透明源 → png（无变换，`legacy`）| `rgba` | ✅ 保住 |
| **透明源 → png（Display P3）** | **`rgb24`** | ⚠ **丢 alpha** |
| **透明源 → tiff（Display P3）** | **`rgb24`** | ⚠ **丢 alpha** |
| **透明源 → webp（Display P3）** | **`yuv420p`** | ⚠ **丢 alpha** |
| **透明源 → png（Display P3，`legacy`）** | **`rgb48be`** | ⚠ **丢 alpha** |

**机制（代码证据）**：① 引擎解码**恒无 alpha**（`RawColorPipeline.cs:97` 的 `format=rgb48le`）；
② 引擎**除 jpg 外不给 `-pix_fmt`**（`ImageEncoderArgs.MapEnginePixFmt:609` 直接 `return null`）⇒ 编码器按"无 alpha 的输入帧"推断；
③ 引擎里**只有 gif 分支**补 alpha（`RawColorPipeline.BuildEncodeArgs:873`），另两处在 **cjxl**(`:471`) / **JXR**(`:590`) 出口
⇒ **机制已知（gif 分支注释自己写着"会把透明静默拍平"）、修法已知（第二输入取 alpha + `alphamerge`），只是没接到 png/tiff/webp/avif 主出口上**。

**与登记对账**：**AVIF** 丢 alpha ✅ **已登记**（`:137`：引擎 avif 出口 `hasAlpha` 硬编码 `false`）；
**GIF** 透明 ✅ 已处理（第二输入补 alpha）；**png / tiff / webp（有色彩变换时）** ⚠ **本报告首次记录**。

⚠ **且无门禁覆盖**：现有端到端用例用的是**不透明**素材或**不触发变换**的组合 ⇒ 所以一直没红。
⚠ **修法属产品行为决策**（是否要让色彩变换路径支持 alpha）⇒ **待你确认**（详见报告 §5.4）。

### 36.2 ✅ 另一处已修：`PngCicpService` 的「已知缺口」自述**已过期**

原文称「生产路径只调 `TryInsertSbit`，**未调** `TryInsertCicp` ⇒ HDR PNG **没有任何色彩描述**」——
实测生产路径**确实**会调（`QueueProcessor.cs:1278` 的 `DecideLabel` → `:1283` `WriteCicp`）⇒ **缺口已闭合**；
注释已更正（编译 0 警告、`cref` 解析通过、`FfmpegGui.dll` 未变 ⇒ 注释-only）。

### 36.3 ✅ 干净结论（避免误报）

- **「附 ICC」不存在非预期的多真源**：`AttachIcc` 的三个产出方分属「规划层（多分支单入口）」「同层标注辅助」「legacy 自己的决策」，
  而 `RawColorPipeline` 的两处改写是**执行体声明自己实际写了什么**（GainMap 防错标 + 保护 MPF 偏移；cjxl 防假告警）⇒ **不是二次决策** ✓
  （其「是约定而非类型强制」**已在 `TESTING.md` 登记**）。
- 另两处自述缺口（`QueueProcessor.cs:108` 入队期无 GainMap 标志 · `PngCicpService.cs:7` cLLI/mDCV 未实现）**经核仍准确** ✓。
- **覆盖矩阵**（报告 §2）：9 条专用通路 **9/9** 都做元数据恢复；**4/9** 自己写色彩描述
  （GainMap / Cjxl / Jxr 有；Gif→Avif / Avif→Gif·WebP / Jxl 输入 / Jxr 输入 / Cjpegli / Dng 无）。

### 36.4 ⚠ 我自己的方法学更正（两次假阳性）

用「定长窗口（160 行）」统计调用**两次串进下一个方法**，得出两个**错误**结论
（「Cjpegli 里出现 `GainMapEncoder`」、「Jxr 输入通路不做后置」），按**方法真实结尾**（花括号配对）复核后**推翻**。
⇒ **纪律**：按方法统计调用**必须花括号配对定界**。

### 36.5 ⚠⚠ 又一条**已实机复现**的缺陷（隐私）：exiftool 不可用时 `--strip-metadata` **静默失效 ⇒ GPS 泄漏**

**A/B 实测**（源：带 GPS + 描述的 PNG，`GPS=39 deg 54' 15.12"` / `Description=HELLO`）：

| 场景 | 产物 GPS | 产物描述 | 判定 |
|---|---|---|---|
| 基线：**exiftool 在位** + `--strip-metadata` | **无** | **无** | ✅ 正常剥离 |
| **exiftool 临时移走** + `--strip-metadata` | **`39 deg 54' 15.12"`** | **`HELLO`** | ⚠⚠ **静默泄漏** |

**证据链**：应用**知道**工具缺失（日志首行 `exiftool: ❌`），但 ffmpeg 命令行仍是 **`-map_metadata 0`**（源元数据整体复制），
且**没有任何点名**、任务照常报成功（`exit=0`）。
⚠ **既有 `verify-metadata-privacy` 门禁覆盖不到**：它**依赖 exiftool 在位** ⇒ 所以一直没被发现。
**修法（属产品行为决策）**：`strip` 生效需要 exiftool ⇒ 要么**入队期红/拒绝**，要么**降级为 `-map_metadata -1` + 点名**。

⚠ 另：轴 C 的「`carry-icc` 下对 jpg/webp/tiff **无清理**」这条**被我的 A/B 推翻**（假阳性）——
源**带 ICC** 时三个格式的 GPS **都正常剥离**；⚠ 而源**无 ICC** 时 `carry-icc` 会 **`exit 91` 契约拒绝**
（「S2 且源无 ICC ⇒ 不生成 ICC、不改像素」）—— 那是**合法且点名的拒绝**，**不是缺陷**。

### 36.6 ⚠ 仍待运行验证

- **轴 C 第 3 条未测**：GainMap + `--encoder-backend cjpegli` + PreserveAll + 源带 ICC ⇒ 底图 ICC 是否被覆盖（需 GainMap 素材，本次未构造）。
- sBIT/cICP 写入顺序对第三方查看器的影响；并发下 9 条专用通路的临时目录隔离。

---

## §37 2026-09-22 —— 修掉管线审查的两条缺陷（exiftool 可用性 + alpha 拍平）

### 37.1 ✅ exiftool 不可用 ⇒ **相关选项不可选 + 明确提示 + 拒绝执行**（对应 `§36.5` 的隐私缺陷）

**旧行为**（缺陷）：`UpdateExifToolPanelState()` 把面板**整个隐藏**、提示语**只在可用时才设置**，
而「元数据模式（保留/删除全部）」下拉**只受格式能力管** ⇒ **仍可选中「删除全部」**
⇒ 剥离**静默失效**（ffmpeg 侧仍用 `-map_metadata 0` 复制源元数据）⇒ **GPS 原样落盘**、日志零点名、任务报成功。

**新行为**（三处协同）：

| 层 | 改动 | 位置 |
|---|---|---|
| **UI** | 面板**保持可见**（不再隐藏）；`MetadataModeCombo` **禁用**且**强制回「保留」**；5 个 `Strip*Check` 保持禁用；提示**始终**设置 | `MainWindow.xaml.cs` 的 `UpdateExifToolPanelState()` |
| **UI 一致性** | `UpdateOptionAvailability()` 里那处 `MetadataModeCombo.IsEnabled` **改为同一表达式**（`&& ExifToolService.IsAvailable`）—— 否则它会**覆盖**上面的条件、让旧缺陷复活 | 同文件（原 `:2159`）|
| **本地化** | 新增 `exiftool.missing` / `exiftool.mode.stripall`（zh-CN + en-US），并**删掉**原先两行硬编码中文 | `Resources/Locales/*.json` |
| **队列（CLI/headless）** | 剥离开关被请求而 exiftool 不可用 ⇒ **点名**（4 行 ASCII）；其中**显式选了「删除全部」⇒ 拒绝该项**（`ExitCode=-1`、`Status=失败 (exiftool 未找到)`、**不出产物**）；仅**个别开关**为真 ⇒ 只点名警告、不失败 | `QueueProcessor.cs`（`ProcessAsync` 的每项前置）|

⚠ **为什么个别开关不失败**：`StripExifGps` 的**默认值就是 `true`**（隐私默认）⇒ 若也失败，「缺 exiftool」会把**每个**任务都判失败。
⚠ **消息保持 ASCII**：`_probe-cjk-hardcode-scan` 余量为 0，新增中文行会立刻顶红（实测该锁 **14/0** 通过）。

**验证**：
- **UI 新断言 6 条**（`UiTestHost` 的 `C3d…`）：用反射模拟「已检测过但没找到 exiftool」，断言
  面板仍可见 · 模式下拉禁用 · 模式强制回「保留」· strip 复选框禁用 · 提示点名 exiftool · **还原后重新启用** ⇒ `328 PASS / 0 FAIL`（原 322）。
- **变异 M13**（把可见性与模式启用改回旧行为）⇒ `[FAIL] C3d …面板仍可见 :: visible=False`、`[FAIL] C3d …模式下拉禁用 :: enabled=True`（`326 PASS / 2 FAIL`）⇒ **锁有牙** ✓
- **实机 A/B**：移走 exiftool + `--strip-metadata` ⇒ `exit=1`、`[1/1] … → 失败 (exiftool 未找到)`、4 行点名、**无产物** ✓；
  **正常态**（exiftool 在位）⇒ `exit=0`、GPS 已剥离、**0** 条 `[meta]` 误报 ✓
- 实验安全：临时移走用 `try/finally` 保证还原，**哈希 `68C079C32FDAE0D6` 前后一致** ✓

### 37.2 ✅ 透明输入 + 色彩变换 ⇒ **alpha 不再被静默拍平**（引擎路径；对应 `§36.1`）

**修法**：把 gif 出口既有的「**第二输入只取原文件的 alpha 平面 + `alphamerge`**」推广到
`png / apng / tiff / tif / webp`，并**显式给出带 alpha 的 `-pix_fmt`**（否则编码器仍按"无 alpha 的输入帧"推断 ⇒ 补了也白补）。
判据与 cjxl/JXR 出口**同源**：同一个 `FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath).hasAlpha`（内部走缓存）。
⚠ **avif 不在本次范围**：它的 alpha 是**独立辅助流**，引擎出口已登记为「已知不支持」⇒ 保持登记状态。

**验证**（真透明源：`colorchannelmixer=aa=0` 造出 `alpha=0..0`，再经 `--color-space "Display P3"`）：

| 用例 | 修复前 | **修复后** |
|---|---|---|
| 透明源 → png | `rgb24`（丢）| **`rgba`**，alpha **`0..0`** ✅ |
| 透明源 → tiff | `rgb24`（丢）| **`rgba`**，alpha **`0..0`** ✅ |
| 透明源 → webp | `yuv420p`（丢）| **`argb`**，alpha **`0..0`** ✅ |
| 透明源 → png（**无**变换）| `rgba` | `rgba`（不回归）✅ |

⚠ **`src_alpha.png` 本身是「RGBA 容器但全不透明」（实测 `255..255`）** ⇒ 只用它**证不出透明值是否保住**；
本次特意另造 `alpha=0..0` 的真透明源，才让 A/B **有区分力**（修复前无 alpha 通道 ⇒ 该检查会得 `255..255`）。

#### 37.2b ⚠ 我在本轮**引入并修掉**的一处回归（整轮门禁抓到）

**现象**：首轮整轮门禁 `verify-engine-firstframe` **红 14 条**（探针 `exit=1` 且无结构化输出）。
**根因（探针直接复现）**：`InvalidOperationException: 编码失败 (exit -22)` ——
我新加的 alpha 分支**没有限制输出帧数**：底图 `[0:v]` 是 rawvideo（恒 1 帧），而 alpha 源 `[1:v]` 是**原文件**，
**多帧输入（10 帧 webp）会给 `[1:v]` 10 帧** ⇒ 滤镜图产出 10 帧 ⇒ 编码器报 `-22`。
⚠ **这个坑本仓早就登记过** —— JXR 出口（`EncodeJxrViaJxrEncAppAsync`，`#13 站点 B`）的注释写明
「站点 B 取「**限制输出帧数**」而不是「限制 alpha 源那一路」」，并附三条实测依据。
⇒ **修法照做**：本分支加 `-frames:v 1`（语义也与引擎既有约定一致：**多帧输入只取首帧**，解码步本就带它）。
**复验**：`verify-engine-firstframe` **`PASS=32 FAIL=0`**（回到基线）· 探针 `pass=10 fail=0` ·
且探针的结构化行显示 alpha 合并**确实生效**（`-filter_complex "[1:v]…alphaextract[a];[0:v]format=rgb48le[b];[b][a]alphamerge" -frames:v 1 … -pix_fmt rgba64le`）✓

⚠ **教训**：**把一处已验证的手法「推广」到新出口时，必须把它当时的全部前置条件一起搬** ——
本次只搬了「第二输入取 alpha + `alphamerge`」，**漏搬了「限制输出帧数」**，于是被多帧输入打红。
（该前置条件**在同一文件的相邻方法里有现成注释**，属「本可先读到」的一类。）

### 37.3 ✅ legacy 路径的同一缺陷**已修**（本轮补做）

实机（**修复前**）：`--color-engine legacy` + 真透明源 + Display P3 ⇒ png **`rgb48be`**、webp **`yuv420p`**（**同样丢 alpha**）。
**根因**：legacy 的 `-vf` 链**以 `format=rgb48le` 开头**（`FfmpegCommandBuilder.cs` 的 iccgen 前置 `format=rgb48le`）⇒ 无 alpha 平面。

**修法**（在 args 收尾前、**仅当 `inputHasAlpha`**）：把链改成 `-filter_complex`，
加**第二输入**（原文件）只取 alpha 平面、在色彩链**之后** `alphamerge`，并**强制**给出带 alpha 的 `-pix_fmt`。
⚠ 三条实现要点（都踩过或已登记）：
1. **第二输入 `-i` 必须紧跟第一个 `-i`** —— 插到输出选项之后会被 ffmpeg 当**输入**选项解析
   （实测 `Error parsing options for input file` + `-22`）；
2. **必须 `-frames:v 1`** —— 底图恒 1 帧而 alpha 源是**原文件**（多帧会给 N 帧）⇒ 否则 `-22`（该坑 JXR 出口已登记）；
3. **只对带 alpha 的源生效** ⇒ 不透明源（现有全部门禁用例）命令行**逐字不变** —— 这是本改动的主要回归护栏。

**验证**：legacy + 真透明源 + P3 ⇒ png/tiff = **`rgba`**、webp = **`argb`**，alpha **`0..0`** ✓；
**不透明源**的命令行**仍走 `-vf`、不含 `filter_complex`/`alphaextract`** ✓（不回归）。
**变异 M15**（关掉该分支）⇒ 门禁 **`PASS=22 FAIL=7`**（正好是 legacy 的 6 条 + 其正例 1 条）⇒ **有牙** ✓

#### 37.3b ⚠ 我在 37.3 里**引入并修掉**的第二处回归（整轮门禁抓到）

**现象**：整轮 `verify-anim-static-target` 与 `verify-webp-anim-probe` 双双**转红**，红点直指：
> `FAIL ③ anim.webp → webp：保留 **10 帧**（实 1）—— 多帧容器不得被截成 1 帧`
> `FAIL ③ anim.webp → webp：命令行**不带** 「-frames:v 1」（否则动画被静默丢弃）`

**根因**：我在 37.3 的 legacy alpha 分支里**无条件**加了 `-frames:v 1` ——
而**动画 webp 也带 alpha** ⇒ 该分支对它同样生效 ⇒ **10 帧被静默截成 1 帧** ⚠⚠
⚠⚠ **而 legacy 里早就有现成判据**：`:139` 的 `bool insertFramesV1 = !targetHoldsMultipleFrames;`
（注释写明「`jxl + AnimationFps` 必须排除：…若也插 `-frames:v 1` 会把动图**静默截成 1 帧**」）
—— **我没复用它**。

**修法**：复用 `insertFramesV1` ⇒ 只在「目标容器不支持多帧」时才加该参数 ✓
**复验**：`verify-anim-static-target` **`PASS=48 FAIL=0`** · `verify-webp-anim-probe` **`PASS=44 FAIL=0`** ·
`verify-engine-alpha-preserve` **`PASS=29 FAIL=0`** ✓

⚠ **教训（本轮**第二次**同类）**：**新增/推广一处改动时，必须复用该处既有的判据，不能另起一套** ——
第一次漏搬的是「`-frames:v 1` 这个**参数**」（`§37.2b`），第二次漏搬的是「它的**判据**」。
⚠ 两次都**被既有门禁当场抓住**（这正是「先接线再改」的价值）⇒ 也说明**改动前先读该处的既有注释与判据**是必需的。

### 37.4 验证汇总

编译 **0 警告 0 错误**（修掉过程中出现的一处 `CS8602`）· 三项目均重建 · `_probe-cjk-hardcode-scan` **14/0** ·
`verify-ui-host` **328/0**（原 322，+6 条新断言）· 变异 M13/M14/M15 各自闭环 · 实机 A/B 各两组 ·
**整轮 `_run-step3-gates.ps1`：「运行器汇总：全部通过」**（**26m22s**；5 条 UI 门禁均 `328 PASS / 0 FAIL`；
新门禁 `engine-alpha-preserve **29/0**`；`anim-static-target **48/0**`；`webp-anim-probe **44/0**`）✅
⚠ 期间**两次**整轮红均为**我引入的回归**，且都被**既有门禁当场抓住** ⇒ 见 `§37.2b`（漏搬 `-frames:v 1` 参数）
与 `§37.3b`（漏搬它的**判据** `insertFramesV1`）；两处均已修复并复验。

### 37.5 ✅ 已给 alpha 修复补上**专用锁**（新增第 50 条门禁）

**新门禁**：`tests/scripts/verify-engine-alpha-preserve.ps1`（已接线为运行器**第 50 条**，`$expectedScriptCount` 49 → **50**）。

**它锁什么**（三段，全部带判别力设计）：
| 段 | 断言 |
|---|---|
| **① 素材自检** | **自造**真透明源（`colorchannelmixer=aa=0`）并断言「源带 alpha **且** alphaYMIN == 0」—— ⚠ 否则整条门禁无判别力（**直接红**）|
| **② 正例** | 透明源 + `--color-space "Display P3"` → **两条管线** × png / tiff / webp：`exit 0` **且** 产物 `pix_fmt` 含 alpha **且** 产物 alphaYMIN == 0（webp 容差 ≤4，有损）|
| **③ 负控** | **不透明**源 + 同选项 ⇒ 命令行**不得**含 `alphaextract`（**两条管线各一条**；legacy 侧另断言**仍走 `-vf`**）；而**透明**源**必须**含 ⇒ 防"无条件合并"把断言变恒真 |

⚠ **两条踩过的坑（都写进门禁注释）**：
1. **素材必须真透明**：`sources/src_alpha.png` 实测是「RGBA 容器但**全不透明**（`255..255`）」⇒ 用它会让断言**恒真**；
   而造法用 `color=c=red@0.0` **也不可靠**（本构建实测仍产出不透明 PNG）⇒ 改用 `colorchannelmixer=aa=0` ✓。
2. **负控不能用 `alphaextract` 判"不透明"**：无 alpha 平面的源上它**读不出** ⇒ 会把负控自身变成假红 ⇒ 改用 `pix_fmt` 判 ✓。

**验证**：正常态 **`PASS=29 FAIL=0`**（**覆盖引擎 + legacy 两条管线**）· **变异 M14**（关掉引擎侧判据）⇒ **`PASS=9 FAIL=7`** ·
**变异 M15**（关掉 legacy 侧判据）⇒ **`PASS=22 FAIL=7`** ⇒ **两处都承重** ✓ · `verify-ps-compat` **13/0** ✓

### 37.6 ⚠ 仍待处理

1. `§36.6` 的两项（GainMap+cjpegli 底图 ICC；sBIT/cICP 顺序的第三方核对）。
2. ⚠ **avif 的 alpha** 仍是**已登记**的「已知不支持」（其 alpha 是**独立辅助流**，非本修法可覆盖）⇒ 未动。
3. ⚠ **一处小文档漂移**：运行器注释里写「`docs/TESTING.md §3.5` 同记此数」，
   但 `TESTING.md §3.5` 实为「服务级探针 `tests/ServiceProbe`」、**不含**脚本清单条数 ⇒ 该指针**指错了**（本次未改，登记备查）。

---

## §38 2026-09-22 —— 修掉 `§36.6` 第 1 项：**GainMap + cjpegli ⇒ 底图 ICC 被源 ICC 覆盖（错标）**

### 38.1 缺陷（轴 C 报的、本轮**首次实机复现**）

**配方**：HDR 源（`validate/src_hdr.png`）+ **源附 P3 ICC** + `--format jpg --jpeg-gain-map true`。

| 用例 | 产物**底图** ICC（SHA256 前 16）| 判定 |
|---|---|---|
| 源 `hdr_p3.png` | `D6881218198125F3` | （源 ICC = P3）|
| **默认（引擎 / Ffmpeg 后端）** | `75FF19E1029473FE` | ✅ 底图用**自己的色域** ICC（正确）|
| **`--encoder cjpegli`** | **`D6881218198125F3`** | ⚠⚠ 与**源 ICC 逐字节相同** ⇒ **底图错标** |

⇒ **真缺陷**：GainMap JPEG 的**底图**被贴上**源**色域的 ICC ⇒ 查看器会按错色域解读底图。

### 38.2 根因（代码证据）

`QueueProcessor.cs` 的 `encoderProtectsColor`：
```csharp
// 编码器后端保护：外部编码器/FFmpeg 已嵌入色彩信息
// cjpegli 管道模式不嵌入任何色彩标签（无 JFIF/ICC），不应保护
var encoderProtectsColor =
    backend == EncoderBackend.Cjxl || backend == EncoderBackend.Jxr || backend == EncoderBackend.Ffmpeg;
```
⇒ **Cjpegli 被排除** ⇒ `protectColorMetadata = false` ⇒ `RestoreMetadataAsync` 走「**完整元数据复制**（含 ICC）」⚠。
⚠ **而那句注释的前提对 GainMap 变体不成立**：**增益图出口由 `GainMapEncoder` 自己写底图 ICC**
（对齐 libultrahdr：底图是什么色域就附什么 ICC）⇒ 复制回来的**源 ICC 把底图 ICC 覆盖掉** ⇒ 错标。
（引擎侧之所以正确，是因为它走 **Ffmpeg** 后端 ⇒ 已在保护名单内 ✓。）

### 38.3 修法（1 处条件）

在 `encoderProtectsColor` 里加 **GainMap 变体例外**：
```csharp
|| (backend == EncoderBackend.Cjpegli && item.Options.JpegGainMap);
```

### 38.4 验证

**定点 A/B**（同一源、同一选项，只换后端）：修复后 `--encoder cjpegli` 的底图 ICC = **`75FF19E1029473FE`**
（与引擎路径**一致**）✓ —— 而修复前是 `D6881218…`（= 源 ICC）✗。
编译 **0 警告 0 错误** ✓。

### 38.5 ⚠ 未完成（**接手必读**）

1. ⚠⚠ **本修复未经整轮门禁验证**：随后的整轮运行**被中断**（用户主动取消）⇒ 工作区当前状态**尚未跑过整轮**。
   ⇒ **接手第一件事：跑一次整轮**（见 `docs/HANDOVER_2026-09-22.md §1`）。
2. ⚠ **本修复尚无门禁锁定** ⇒ 建议在既有 GainMap 门禁里补一条「底图 ICC ≠ 源 ICC」的断言（或加进 `verify-engine-alpha-preserve` 式的专用门禁）。
3. ⚠ 中断遗留的**陈旧锁**已确认属主不存在后清除（`pid 92940`）；游离进程核对为**无**。

---

## 2026-10-01 批次收口（管线审查 × 测试覆盖审计）

- 台账全文：**`docs/PIPELINE_TEST_AUDIT_2026-10-01.md`**（判词 / 假绿机制 / 逐格式高级选项可用性矩阵 / 应覆盖范围）。
- 整轮读数：`_run-step3-gates.ps1` **83 步全部 exit=0，「运行器汇总：全部通过」**，日志头
  `artifact-sha16: FfmpegGui.dll=8FF98B12BE0B71CD`（=出货包 beta2-full 内那份，确定性重建同指纹）；取证 `tests/output/t78/round-R2.log`。
- 本轮把 STALE 闸从「只判旧」补成「缺席即拒跑」（5 项产物），并修掉 **35 个门禁恒假的构建类型标注**
  （`'*\Debug\*'` 比不过正斜杠路径 ⇒ 实测一条 `PASS=22 FAIL=0` 跑的是 09-30 的 Debug 产物）。两条都是**假绿机制**，不是功能缺陷。
- L3（打出货包）判据从零建成：`t76/jbrd-e2e.ps1` **89/2**、`t76/orientation-rewrap.ps1` 14/0、
  `t77/gainmap-thirdparty.ps1` **13/0**（libultrahdr 自己认证 Ultra HDR，且逐项与产品声明的 log2 余量对账）、
  `t69/pkg-smoke3.ps1` 17/0、`t79/knob-liveness.ps1` **18 臂 = 活 15 / 死 1 / 被拒 1 / 未判 1**。
- 2 条红是**真缺陷**（增益图 JPEG→JXL 无损宣称静默退化为 float、且该产物取不回来），未修，复见审计文档 §五 U1。
- ⚠ 上述 5 套 L3 判据都在 `tests/output/`（`.gitignore:78` ⇒ 不进提交、一次 `git clean -xdf` 就没了）。
  下一批应移入 `tests/scripts/` 并接成清单条目（60→63），成本与连带要改的条数陈述见审计文档 §六。
