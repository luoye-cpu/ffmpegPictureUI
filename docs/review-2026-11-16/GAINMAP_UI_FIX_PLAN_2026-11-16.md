# Gain Map UI / AVIF 底图 CICP 修复计划（2026-11-16）

## 0. 目标与边界

本计划只修 **Gain Map 出口的 UI 暴露 / 能力真值 / AVIF 底图标注 / 计划层告警**，
不改变 Gain Map 的像素算法（Reinhard、底图矩阵、增益图降采样、JPEG/AVIF/JXL 打包结构）。

已确认事实（来源：`GAINMAP_UI_AND_BASE_GAMUT_ANALYSIS_2026-11-16.md`）：

- `GainMapEncoder.FormatCarriesGainMap` 是运行时唯一格式真值：`jpg/jpeg/jpegli/avif/jxl` 都支持；
- 主面板 `HdrModeCombo` 当前只对 `jpg/jpeg/avif` 可见，但 `JpegGainMapPanel` 只在 `jpg/jpeg` 显示；
- AVIF 后端可产出 `tmap`；SDR AVIF 走了短路，底图 `colr` 未重写，实测 `primaries/transfer=unknown`；
- AVIF 宽底（Rec.2020）当前 `cTrc=1`（BT.709），而 `BuildBaseBgra8` 用的是 sRGB 曲线 ⇒ 标签与字节不一致；
- 计划层用 SDR bt709 目标计算 `GamutOutsideDestination`，在“GainMap + Rec.2020 底图”时产生假
  `GamutClipped` 告警；实际 `RawColorPipeline.TransformGainMapAsync` 独立解码源，不消费计划层像素；
- `GainMapEncoder` 对 AVIF/JXL 的 `xmp` metadata 明确拒绝（AVIF 只有 ISO 形式，JXL 只有 jhgm/ISO）。

## 1. 改动清单

### A. UI 显隐与状态单一真值（`MainWindow.xaml` / `MainWindow.xaml.cs`）

1. `UpdateCodecPanelVisibility` / `UpdateGainMapPanelVisibility` / `HdrMode_Changed` /
   `UpdateIccPanelVisibility` 全部改用 `GainMapEncoder.FormatCarriesGainMap(fmt)` 判断格式承载能力，
   不再手写 `jpg/jpeg` 列表。
2. `HdrModeCombo` 对 `jpg/jpeg/jpegli/avif/jxl` 可见；当前格式不支持 GainMap 且 HdrMode 仍为 1 时
   复位到 0，避免隐藏状态残留。
3. `JpegGainMapPanel` 对 JPEG 族、AVIF、JXL 显示；JXL 的 `JpegGainMapBaseCombo` 禁用并强制 `srgb`
   （JXL v1 jhgm 只支持 sRGB 底图）。
4. AVIF / JXL 的 metadata 只允许 ISO：隐藏 `JpegGainMapMetadataLabel/Combo/Note`，并把索引归一到 0；
   `ParseGainMapMetadata` 按格式 clamp，`BuildOptions` 不依赖隐藏控件残留值。
5. `ParseGainMapBaseGamut` 对 JXL 强制 `srgb`，AVIF/JPEG 仍按用户选择。
6. `BuildOptions.JpegGainMap` 统一为 `HdrMode==1 && FormatCarriesGainMap(fmt)`，避免 PNG 等不支持格式
   在隐藏 HdrMode 后仍把 `JpegGainMap=true` 传给后端。

### B. AVIF 底图 CICP 与宽底曲线（`GainMapEncoder.cs` / `IsoBmffGainMapWriter.cs`）

1. `IsoBmffGainMapWriter` 新增单图 AVIF `RewriteSingleColr`：
   - `TryExtractSingle` 保留原始 `ftyp`；
   - 用 `PatchColr` 补/改 nclx（保留原 `full_range`，无 colr 时补一个）；
   - 重建 `ftyp + meta（hdlr/pitm/iinf/iprp/iloc）+ mdat`；
   - 仅用于 SDR AVIF 短路（HDR 路径继续用既有三 item `Build`）。
2. `EncodeAvifGainMapAsync` SDR 短路不再直接写 `baseBytes`，改为走 `RewriteSingleColr`。
3. AVIF 宽底 `cTrc` 统一为 13（sRGB），与 `BuildBaseBgra8` 的 8-bit sRGB 字节一致；`trc` 输入 token
   同步改为 `iec61966-2-1`，并更新相关注释。
4. SDR 底图 `cPrim/cMtx` 仍按 `wideBase` 取 9/1 或 1/1，`full_range` 继承底图实际值。

### C. 计划层告警口径（`ColorIntent` / `PlanPolicy` / `ColorTransformPlan` / `ColorStrategyPlanning`）

1. `ColorIntent` 增加 `GainMapBaseGamut`；`ColorIntentFactory` 从 `o.JpegGainMapBaseGamut` 透传；
   `ToPolicy` 映射到 `PlanPolicy.GainMapBaseGamut`。
2. `ColorTransformPlan` 增加 `GainMapBaseGamut`，在 `ColorMappingEngine.Plan` 的 GainMap 唯一装配点赋值。
3. `CollectDegradations`：GainMap + `bt2020` 底 + 非 ICC 派生的 bt709/P3/bt2020 源 ⇒ 抑制假
   `GamutClipped`（实际底图在 Rec.2020 内）；`DstGamutLabelOf` 在宽底时用 `SDR(bt2020 base)` 标签。
   ProPhoto/ICC 派生源不抑制，保留真实越界告警。

### D. 能力真值与文案

1. `FormatCapabilitiesService` 的 AVIF `SupportsGainMap = true`，CicpNote 补 tmap。
2. `QueueProcessor` 的 GainMap 拒绝文案从“JPEG 族唯一”改为 jpg/AVIF(tmap)/JXL(jhgm) 三种结构；
   同批更新 `verify-color-matrix-full.ps1` 的文案锁。
3. `FfmpegOptions.cs` 的“仅 JPEG 输出”旧注释同步。

### E. 输入路径健壮性

1. `ColorIntentFactory.FromOptions` 入口把 `inputPath` 规范成绝对路径；
   修复 ProPhoto ICC-only 相对路径调用 exiftool 时的 `File not found`（exit 90）。

### F. 测试 / 构建 / 真机 GUI

1. `dotnet build ffmpegPictureUI.sln -c Release -m:1` 必须 0 error；0 新增警告。
2. 目标门禁（不全量）：`verify-ui-host`、`verify-gainmap-isobmff`、`verify-color-matrix-full`
   （或 `verify-color-strategy`，按改动链路选择），失败必须逐条归因。
3. 运行时矩阵复跑：
   - HDR AVIF `srgb|bt2020` 底：均有 tmap；宽底 CICP = 9/13/0，sRGB 底 = 1/13/0；
   - SDR AVIF 六格：底图 CICP 不再是 unknown；宽底 9/13/0，sRGB 底 1/13/0；
   - JPEG Rec2020 底：计划层不再输出假 `GamutClipped`；
   - AVIF + metadata xmp：仍明确拒绝（后端闸门）；
   - ProPhoto ICC-only 相对路径：exit 0。
4. 真机 GUI（`computer_use_tool`，`plane="cu"`）：
   - 启动 `FfmpegGui.exe`，选择 AVIF，确认 HdrMode 与 GainMap 面板可见；
   - 在 GainMap 面板确认 metadata 只有 ISO（AVIF 下 XMP/both 不显示），底图可选 Rec.2020；
   - 切 JPEG（选 XMP）再切 AVIF，确认自动回 ISO、格式切换不卡死；
   - 选 AVIF Rec.2020 底执行一次真实队列，回读产物确认 `tmap` 与 `bt2020/13/bt2020nc`；
   - 选 JXL，确认 GainMap 面板可见且底图锁定 sRGB（不产生宽底拒绝）；
   - 记录截图/产物/ffprobe/exiftool 读数。

## 2. 明确不做

- 不改 GainMap 像素算法、不改 JPEG/JXL 既有打包结构；
- 不改 `QueueProcessor` 的 GainMap 分派逻辑（只改文案）；
- 不把 `FormatCapabilitiesService` 当运行时闸门（仍是描述性能力表，运行时以 `FormatCarriesGainMap` 为准）；
- 不移动 tag、不改版本位、不建分支、不提交；
- 不跑全量 104 条门禁（`-FullBaseline` 需用户明确要求）。

## 3. 验收口径

- 代码：0 error；UI 四个显隐入口不再各写一份格式列表；
- 产物：AVIF 底图 `colr` 可被 exiftool/ffprobe 读出正确 primaries/transfer；
- 计划：GainMap 宽底不再报假 `GamutClipped`，ProPhoto 越界仍报；
- GUI：真实点击路径可复现“AVIF GainMap 面板可见、metadata 仅 ISO、切格式不残留”；
- 证据：所有读数落 `tests/output/_tmp/gainmap-fix-20261116/`，不写进被 tracked 的旧账本。

## 4. 本轮执行结果（2026-11-16）

### 4.1 构建

`dotnet build ffmpegPictureUI.sln -c Release -m:1`：**0 error / 5 warning**。
5 条警告均为既有文件（`RawColorPipeline.cs:830`、`QueueProcessor.cs:1590/2697`、
`Win32TrayIcon.cs:78/150`），本轮新增改动未引入新警告。

### 4.2 运行时 AVIF 矩阵（headless，`tests/output/_tmp/gainmap-fix-20261116/`）

| 用例 | exit | 产物读数 |
|---|---:|---|
| SDR `src_srgb.avif` + sRGB 底 | 0 | 底图 `bt709/iec61966-2-1/bt709`，无 tmap（SDR 短路） |
| SDR `src_rec2020.avif` + Rec.2020 底 | 0 | 底图 `bt2020/iec61966-2-1/bt2020nc`，无 tmap（SDR 短路） |
| HDR `src_srgb_pq.avif` + sRGB 底 | 0 | 底图 `bt709/13/bt709`，含 tmap |
| HDR `src_rec2020_pq.avif` + Rec.2020 底 | 0 | 底图 `bt2020/13/bt2020nc`，含 tmap（容器 `iinf` 报 `tmap` item） |
| JPEG `src_rec2020_pq.avif` + Rec.2020 底 | 0 | 底图 ICC `bt2020/sRGB`；日志无 `GamutClipped` 行 |
| ProPhoto ICC-only（相对路径） | 0 | 修复前 exit 90；修复后 exit 0，元数据复制警告不阻断产物 |

SDR 底图两格均用 ffmpeg 解码 PNG 验证成功（512×384，exit 0），确认 `colr` 重写未破坏码流。
HDR 宽底用 `ffmpeg -v trace` 确认 `iinf` 含 `item_type tmap`、`iloc item_count 3`、`dimg` 引用齐全。

### 4.3 UI / 门禁

- `pwsh -NoProfile -File tests/scripts/verify-ui-host.ps1 -TimeoutSec 300`：**497 PASS / 0 FAIL**，
  5 个拆分臂全绿；覆盖真实 `MainWindow` 的格式/参数/面板联动，但**未覆盖**本次新增的 AVIF
  GainMap 面板断言（下一轮若要锁死，应同批改 `UiTestHost/Program.cs` + `_gen-ui-split.ps1`）。
- `verify-gainmap-isobmff.ps1`：**174 PASS / 0 FAIL / 9 SKIP**（最终 Release 构建、门禁运行期间未再重建）。
  覆盖 AVIF 底图 CICP（`1/13/0`、`9/13/0`）、tmap+dimg、SDR 底图与 HDR 三 item 全链。唯一的 WARN 是既有
  §3f-10“拼错的 BT.2020 PPP 与合法产物逐字节相同”，与本轮改动无交集，登记为待修。
- `verify-color-matrix-full.ps1` 的 GainMap 拒绝文案锁已同批改为
  “JPEG 的 APP2/XMP+MPF、AVIF 的 tmap 或 JXL 的 jhgm 结构”。

### 4.4 真机 GUI 阻塞

`computer_use_tool` 首次 `cu.list_apps()` 报 `COMPUTER_USE_TOOL_RUNTIME_FAILED`（主桌面控制未启动）；
随后 `cu.switch_desktop("main")` 两次报 `user authorization for exclusive (main desktop) mode timed out`，
`cu.switch_desktop("pip")` 报 `PIP_REQUIRES_MAIN_SESSION`（用户拒绝输入 Windows 密码），
`interaction_request_action(type=pipControl)` 被用户跳过。

⇒ **真实鼠标/截图 GUI 验证未完成**；本轮没有伪造“已点击”的读数。代码侧 UI 诚实性由
`UiTestHost` 的真实 `MainWindow` 497 条断言 + 本轮 headless 矩阵佐证，但不能替代用户桌面的实际点击。
若需要完成真机 GUI 验收，需要重新授权 `main` 桌面接管；届时按 §4.2 的同一输入执行：
AVIF → 开 GainMap → 选 Rec.2020 底 → 切 JPEG(XMP) → 切回 AVIF → 确认 metadata 回 ISO、面板不消失。

### 4.5 JXL 实机验证（2026-11-16）

用当前 Release 的 `FfmpegGui.exe` 在本机真实跑 JXL（非测试桩），输入为
`tests/output/_tmp/gainmap-color-matrix-20261116/` 下的 HDR/SDR AVIF：

| 用例 | exit | 产物 | 容器/解码读数 |
|---|---:|---|---|
| HDR sRGB 源 + sRGB 底 | 0 | `src_srgb_pq.jxl` 4,278 B | `jxlinfo -v` 报 `jhgm` 773 B（696 B 增益图 + 61 B 元数据）；`djxl` 解码 512×384，exit 0 |
| HDR Rec.2020 源 + sRGB 底 | 0 | `src_rec2020_pq.jxl` 4,259 B | `jhgm` 706 B；底图 `Primaries: sRGB`、`Transfer: sRGB`；`djxl` exit 0 |
| SDR Rec.2020 源 + sRGB 底 | 0 | `src_rec2020.jxl` 3,497 B | `jhgm_present=False`，日志明确“SDR，按普通 JXL 输出”；`djxl` exit 0 |
| HDR Rec.2020 源 + **bt2020 底** | 1（预期失败） | 零产物 | 日志点名 `JXL gain-map exit v1 supports an sRGB base only`，不静默改写为 sRGB |

结论：JXL 的 `jhgm` 写侧、ISO 21496-1 载荷、裸码流守卫和 `djxl` 回读都在真机通过；
JXL v1 的 sRGB 底限制是**已知设计边界**，CLI 也按 fail-closed 拒绝宽底，不是本轮新增回归。

真机 GUI 仍受 `switch_desktop("main")` 授权超时阻塞（`user authorization for exclusive
(main desktop) mode timed out`），因此本轮没有做 JXL 面板的真实鼠标点击；JXL 编码链本身已实机验证。
