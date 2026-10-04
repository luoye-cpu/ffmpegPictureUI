# D｜UI / CLI / 双语资源 / 项目文件 / 文档账本 —— 一致性独立复查

- **范围**：`e7de117 → bdd4b0b`（7 个提交）
- **复查角色**：D（与同期 A/B/C 三份并行，本报告只写这一个文件）
- **执行方式**：**纯只读**。未执行 `dotnet build`、未改动任何源码、未 `git add/commit`。取证手段 = `git diff/show/archive` + 静态读代码 + `ls` + 我自己在临时目录复算（不改仓库）。
- **工作树状态**：干净（`git status` 只有未跟踪的 `docs/review-2026-10-03/`）⇒ 所有 `HEAD` 侧读数**精确等于 `bdd4b0b`**；`e7de117` 侧用 `git archive e7de117 src/FfmpegGui | tar -x` 导到 `%TEMP%` 后同工具复算（不走 `git show` ⇒ 不经控制台代码页）。
- **我没有跑 / 无法结构性判定的部分**（下文一律显式标注，不用估算冒充实测）：需要真跑 `ffmpeg/exiftool/avifenc/cjxl` 的产物级读数（`thumbnail 40/0`、`M1 33/7`、`M2 38/2`、`liveness`、`UI 五套 399/0` 等）、以及 `dotnet build` 对本批 csproj 改动的最终裁决。

---

## 0. 结论台（按严重程度）

| # | 级别 | 一句话 | 位置 |
|---|---|---|---|
| D-1 | **【严重】** | CJK 门禁脚本自称「余量 0、已被基线精确吸收」，**实测余量 19**；同一次提交既**松了尺子**又把**基线抬高 3** ⇒ 把一次本应判红的 +12 洗成了绿。 | `tests/scripts/_probe-cjk-hardcode-scan.ps1:298` / `:314` / `:355-394` |
| D-2 | **【严重（待构建裁决）】** | 新 `icon.ico`（4971 B）**每条目的载荷仍是 PNG**（`89 50 4E 47`），与 csproj 注释「真 ICO」自相矛盾；`<ApplicationIcon>` 是 csc 唯一会解析 .ico 的位置，是否编得过**只能靠构建判**。 | `src/FfmpegGui/FfmpegGui.csproj:20-24`、`Resources/icon.ico` |
| D-3 | 【中等】 | `HdrModeCombo` 复用了历史上那只「藏起来仍被读」的形状：采集/预设两侧都**不看可见性**取 `SelectedIndex`。 | `MainWindow.xaml.cs:2940` / `:5768` / `:2382` |
| D-4 | 【中等】 | 同一个「哪些容器能吃 HDR」的事实被编了**三份**（UI 硬编码串 / `ColorTransformPlan.SupportsGainMap` / `FormatCapabilitiesService.SupportsGainMap`），第三份对 AVIF 仍为 false 且**零消费者** ⇒ 双真值温床。 | `MainWindow.xaml.cs:2382`、`ColorTransformPlan.cs:588`、`FormatCapabilitiesService.cs:86/229` |
| D-5 | 【中等】 | 新 `--thumbnail*` 三个旗标**没进 `--help`**（本次只给 `--hdr-mode` 补了文案），且没有任何门禁守 `--help` 完备性。 | `CliParser.cs:354-355` vs `:750-752` |
| D-6 | 【中等】 | `PresetData.UseAdvancedCodec` 是**空枪轴**：GUI 保存侧不写、回放侧不读 ⇒ 用户预设里 `EnableThumbnail=true` 在未勾「高级编码选项」时**静默不生效**。本次新增的 ThumbnailPanel 正好挂在被它管着的面板里。 | `Models/PresetData.cs:25`、`MainWindow.xaml.cs:2863/5826/5944` |
| D-7 | 【中等】 | 第九次抬基线的逐条归因与实测对不上（登记 +3 UI / TAG 归因到一个**不在语料内**的文件）。 | `ps1:279-298` |
| D-8 | 【轻微】 | `--thumbnail-size/quality` 无取值域校验，越界被 `ThumbnailService` **静默钳到** 16–512 / 1–100。 | `CliParser.cs:751-752`、`ThumbnailService.cs:38-50` |
| D-9 | 【轻微】 | `obj/**/Avalonia/resources` 是 09-30 的旧产物、不含 `icon_raw.png`（`strings` 命中 0），带着旧<｜hy_place▁holder▁no▁813｜>哈希 ⇒ 增量构建后须复核。 | `obj/Debug|Release/net11.0/win-x64/Avalonia/resources` |
| D-10 | 【轻微】 | 只有主窗口有图标；`ProgressWindow/ImageDetailWindow/PresetManagerWindow/FormatFilterWindow` 都没设 `Icon`。 | `MainWindow.xaml:8` |
| D-11 | 【轻微】 | 新计数规则②实现有 bug：语句**自身首行**带 sink 时不刷新 `$stmtSink` ⇒ 部分真诊断续行仍落 UI 桶（方向 fail-closed，不造成假绿，但数字不再可信）。 | `ps1:365-394`，证据 `RawColorPipeline.cs:519-522` |
| D-12 | 【轻微】 | 账本文档有残留措辞 / markdown 破表：「两仓暂长期双份」与紧接着的「旧两仓已删」共舞；`HANDOVER_2026-09-22.md:49` 续行缺前导 `|` ⇒ 渲染到表格外。 | `docs/HANDOVER_2026-09-22.md:48-49` |
| D-13 | 【轻微】 | 「受管基线 65」只在 `_run-step3-gates.ps1` 与 `docs/TESTING.md:261` 两处同步；脚本注释声称的「四处文档」里 `docs/HANDOVER.md` 查不到 65，另有一处是 gitignored 的 `MEMORY.md`。 | `ps1:920/1003`、`docs/TESTING.md:261` |

**已核实无问题（详细证据在第 6 节）**：csproj 除图标外没有动任何引用（`GainMapTestHost` 那条猜测不成立）、双语键完备且无英文占位、两处逐字段克隆点齐全、`-e av1_nvenc` 硬失败可证实、L3 60→65 计数真实、缩略图能力位与 UI 阀门同表。

---

## 1. 【最高优先级】`src/FfmpegGui/FfmpegGui.csproj` 的 +13 行

### 1.1 逐行账户（实测 `git diff e7de117..HEAD`）

| 位置 | 改动 | 性质 |
|---|---|---|
| `:19` | `<Version>1.6.0-beta3</Version>` → `1.6.0-beta4` | 常规，`AssemblyVersion/FileVersion` 未动（`:25-26`），与 CHANGELOG 的 beta4 对得上 |
| `:20-23` | 4 行注释，声明「exe/文件图标：真 ICO（16/24/32/48/64 五档，由 Resources/icon_raw.png 生成）」 | **见 1.3，与产物字节不符** |
| `:24` | 新增 `<ApplicationIcon>Resources\icon.ico</ApplicationIcon>` | **唯一有构建风险的一行** |
| `:96-100` | 5 行注释（托盘图标未收录的历史 + 为什么运行时用 PNG） | 无害 |
| `:101` | 新增 `<AvaloniaResource Include="Resources/icon_raw.png" />` | 与 `App.xaml.cs:78` / `MainWindow.xaml:8` 的运行时 URI **同名同一份** |
| — | **没有**新增/删除任何 `ItemGroup`、`ProjectReference`、`InternalsVisibleTo`、`TargetFramework` 条件组 | ✅ |

**关于「可能动了 `tests/GainMapTestHost` 之类的引用」的怀疑 —— 不成立**：整份 diff 里唯一与跨程序集有关的是 `:84` `<InternalsVisibleTo Include="GainMapTestHost" />`，它**在本范围之外就已存在**（diff 不含该行）。本批没有触及任何 `ProjectReference` / `InternalsVisibleTo` / 任何 `TargetFrameworks` 分支。

### 1.2 文件存在性（`ls` 核验）

| csproj 里写的路径 | 磁盘实际 | 结论 |
|---|---|---|
| `Resources\icon.ico`（相对 csproj 目录 ⇒ `src/FfmpegGui/Resources/icon.ico`） | 存在，**4971 B**（工作树干净 ⇒ 就是 HEAD 的 blob） | ✅ 路径对得上 |
| `Resources/icon_raw.png` | 存在，911 B，PNG 头 `89504E47` + IHDR 64×64 8-bit RGBA | ✅ |

**未发现**：重复引用、与现有 `Content/EmbeddedResource`（`:112-114` Locale json）冲突、`None`/`AvaloniaResource` 双重收录、`;-list` 语法问题。`<AvaloniaResource Include="**/*.xaml" />` 与可能存在的 Avalonia SDK 默认 glob 是否重复 —— 这是**本范围之前就有**的形态，本批没碰它，风险不因本次改动变化。

### 1.3 【严重·待构建裁决】新 .ico 的字节与注释自相矛盾

我用结构化解析读了 `Resources/icon.ico`（不是猜）：

```
count=5 size=4971    ICONDIR: type=1 (icon)
16x16 planes=0 bpp=32 bytes=572  off=86   payload[0:4]=89504e47  is_png=True
24x24 planes=0 bpp=32 bytes=877  off=658  payload[0:4]=89504e47  is_png=True
32x32 planes=0 bpp=32 bytes=1070 off=1535 payload[0:4]=89504e47  is_png=True
48x48 planes=0 bpp=32 bytes=1555 off=2605 payload[0:4]=89504e47  is_png=True
64x64 planes=0 bpp=32 bytes=811  off=4160 payload[0:4]=89504e47  is_png=True
（条目偏移与长度首尾相接、无空洞：86+572=658=entry2.off ✅）
```

即：**它是结构合法的 ICONDIR（5 档尺寸占位都对），但每一个条目的载荷仍然是 PNG**。

这与 csproj 注释（`:20-23`）声称的「真 ICO」不符 —— 那份注释把「Pillow 写出的 ICO 每条目是 PNG 载荷」当作**旧文件**的缺陷来批评，而换上来的新文件**仍然是同一种 ICO**，只是从 1 档变成 5 档。同一段逻辑在 `App.xaml.cs:76-77` 里也重复了一次。

**风险区间**：运行时（托盘 `App.xaml.cs:78`、窗口 `MainWindow.xaml:8`）读的是 **PNG**，不受影响 ⇒ 功能面上最坏后果只是「exe 文件本身的图标不对」；但 `<ApplicationIcon>` 会被 SDK 交给 csc 做 Win32 资源，而 csc 是**唯一真正解析 .ico 结构**的位置 ⇒ 若它不接受 PNG 载荷条目，后果是**构建失败**（不是图标缺一张）。**这条我无法用静态方法判定，只能留给构建取证。**（顺带：历史上那条「改 csproj ⇒ NETSDK1060」的纪律，按 `docs/archive/BUILD_HANDOVER.md:56-76` 自己的取证，根因是环境缺 `APPDATA` 导致 NuGet 静态构造抛异常，与 csproj 内容**无因果**；把这次改动和 NETSDK1060 直接挂钩的担心，从证据上不成立 —— 但 `<ApplicationIcon>` 是另一回事。）

### 1.4 【轻微】旧资产残留已存在

`src/FfmpegGui/obj/Debug|Release/net11.0/win-x64/Avalonia/resources`（均为 09-30 产物，Debug 侧 105546 B）经 `strings` 检查：**`icon_raw.png` 命中 0 次**；同目录 `Resources.Inputs.cache` 里存的是旧输入集哈希（09-28）。按 Avalonia 资源编译的做法，新增 item 会改变输入集哈希 ⇒ 理论上会重编；但**如果抽不出这一点**，请在你那次构建之后补一刀自证：对新的 `resources` 再跑一次 `strings | grep icon_raw.png`，命中 1 才算真裹进去了。（旧的 `resources` 里也能搜到 `icon` 字样，那是别的文本 —— Locale/选项串 —— 不是本资源，别拿它当证据。）

---

## 2. UI / CLI 与选项可达性

### 2.1 新旗标确实接上了（**已核实**）

| 旗标 | 解析 | 校验 | 下游落地 |
|---|---|---|---|
| `--thumbnail` | `CliParser.cs:750` `ParseBoolStrict`（`:986-991`，非 true/false 抛 `--{key} 取值无效`） | ✅ | `FfmpegOptions.EnableThumbnail`（`:406`）→ `ThumbnailService.ApplyAsync`（`:103`）→ `QueueProcessor.cs:1610-1612` |
| `--thumbnail-size` | `:751` `ParseIntStrict`（`:932-938`） | ⚠ 无域校验（见 D-8） | `ThumbnailService.ClampLongEdge`（`:38-39`）16–512 |
| `--thumbnail-quality` | `:752` 同上 | ⚠ 无域校验 | `MapQualityToQscale`（`:45-50`）1–100 → `-qscale:v` |
| `--jxl-modular` | `:732`（本范围前已在，本批给它补了实参落点） | ✅ | `ImageEncoderArgs.cs:202/208` 与 `FfmpegCommandBuilder.cs:446` 都发 `-modular 1`；`CjxlService.cs:274/395` 处理它与无损重封装的互斥 |
| `--hdr-mode` | `:773-780` `ParseTokenStrict`（`:1034-1040`）白名单 `gainmap|traditional` | ✅ | `FfmpegOptions.HdrMode`（`:327-344`），是 `JpegGainMap` 的**强类型视图**（getter/setter 都打到同一位） ⇒ 「两种写法同一个真值」的声称成立 |

- 并有 ServiceProbe 把 CLI 可达性钉住：`tests/ServiceProbe/Program.cs:444-453`（T20 三个键）+ `:481-483`（结构锁，直接断言那三行 `case` 存在）。

### 2.2 【中等·D-5】新旗标没进 `--help`

`git diff` 里给帮助文本补的是 `--hdr-mode`（`CliParser.cs:354-355`）；全文件搜 `thumbnail` 只命中 `:750-752` 三行 case，**帮助文本里一条都没有**（`--jxl-modular` 同样没有早些时候进去）。现状是「能跑、查不到」。额外注意：**没有任何门禁守 `--help` 完备性**（`tests/scripts/verify-cli-strict.ps1` 里 `grep -n "help"` 零命中）⇒ 这类缺口是静默累积型的。

### 2.3 ThumbnailPanel：「隐藏后仍被读」这次**修对了**（**已核实无问题**）

历史嫌疑（`MainWindow.xaml.cs:2840` 附近 / `MainWindow.xaml:551-555` 的 `AvifTuneCombo`）在本次新控件上没有重演：

- 可见性/使能由**能力表**给：`MainWindow.xaml.cs:2486-2497`，`thumbSupported = FormatCapabilitiesService.GetCapabilities(ThumbnailService.CapabilityKey(fmt)) is { SupportsThumbnail: true }` —— 与写侧 `ThumbnailService.cs:106` 用的是**同一个判据**，没有第二份口径。
- 不支持的容器**顺手把勾选复位**（`:2494`），不是只藏起来。
- XAML 注释（`MainWindow.xaml:784-785`）点名 `UpdateCodecPanelVisibility + ThumbnailService.CapabilityKey`，与代码一致。
- 采集侧（`:2863`：`useAdvCodec && IsChecked`）与命令预览侧（`:1404-1406`）用的是**同一个 `thumbOn` 表达式** ⇒ 预览与实跑不会说话不一致。
- ⚠ 但那个 `useAdvCodec` 闸门只在采集侧存在、预设保存侧没有（见 D-6）。

### 2.4 【中等·D-3】`HdrModeCombo` 仍是一只「看不见也照读」的控件

```
MainWindow.xaml:498      <ComboBox x:Name="HdrModeCombo" IsVisible="False" .../>   ← 在 AdvancedColorPanel 内
MainWindow.xaml.cs:3756  UseAdvancedColor_IsCheckedChanged ⇒ AdvancedColorPanel.IsVisible = IsChecked
MainWindow.xaml.cs:2382  HdrModeCombo.IsVisible = fmt is "jpg" or "jpeg" or "avif"
MainWindow.xaml.cs:2940  JpegGainMap = HdrModeCombo?.SelectedIndex == 1,     ← **不看 useAdv / 不看 IsVisible**
MainWindow.xaml.cs:5768  JpegGainMap = HdrModeCombo?.SelectedIndex == 1,     ← 预设保存侧同样不看
MainWindow.xaml.cs:6067  if (HdrModeCombo != null) HdrModeCombo.SelectedIndex = p.JpegGainMap ? 1 : 0;  ← 预设回放不看当前格式
```

- 形状与 CHANGELOG 自己点名的旧缺陷**完全同构**（用户在高级色彩面板里选了 GainMap → 关掉总开关 → 面板藏了，值照样被采集）。
- 这不是本次新引入（旧的 `JpegGainMapEnableCheck` 同款），但本次把它**从 CheckBox 换成 ComboBox 并扩到 AVIF**，层面变大了，且 CHANGELOG 没把它列为已知缺口。
- 附带一个方向相反的可达性问题：**AVIF 选了 GainMap 之后没有任何跟随的参数 UI** —— `JpegGainMapPanel`（增益图类型/底色域/降采样/质量）的可见性被写死成只看 jpg/jpeg（`:2477`、`:3771`、`:5577-5579`）。若 AVIF 的 tmap 路线也需要这些参数，用户在 GUI 里够不到；若不需要，至少该有一句说明（现在只有 `tip.gainmap.enable` 提到「增益图适用于 JPEG 与 AVIF」）。

补一句一致性小毛病：`MainWindow.xaml:496` 注释说可见性「由 `UpdateOptionAvailability` 按**容器能力**给」，实际给它的是 `UpdateCodecPanelVisibility(string fmt)`（`:2368`，经 `UpdateOptionAvailability` 在 `:2253` 间接调用 ⇒ 函数名写错），而且**是按硬编码格式串给，不是按能力位**（见 D-4）。

### 2.5 【中等·D-4】三份「哪些容器能吃 HDR」

1. UI：`fmt is "jpg" or "jpeg" or "avif"` —— `MainWindow.xaml.cs:2382`（硬编码串）；
2. 规划层能力矩阵：`ColorTransformPlan.cs:542/544/546`（jpg/jpeg/jpegli）+ `:588`（**avif，本批新增**，注释写明由 `IsoBmffGainMapWriter` 承载 `tmap`）；
3. `FormatCapabilitiesService.SupportsGainMap`：全仓只有 `:86` 一处赋 `true`（**只有 jpg**），`:229` 兜 false ⇒ **avif 这里仍是 false**；而 grep 显示它**一个真实消费者都没有**（只有 `ColorEngineRouter.cs:294/376`、`ColorStrategyPlanning.cs:33` 的注释提到同名但这张表本身不是它被读的地方 —— 规划层读的是第 2 份）。

⇒ 现在它是**既陈旧、又被闲置**的能力位：将来谁按它给 AVIF 闸门，会与另两份事实相反。讽刺的是，**同一批里的缩略图恰恰是按能力表正确实现的**（`FormatCapabilitiesService.cs:231-247` + `ThumbnailService.cs:106`）——同一张能力表，两种用法，说明 HDR 这边的收敛是漏了而不是有意留的。

### 2.6 【中等·D-6】`UseAdvancedCodec` 空枪轴 ⇒ 新 Thumbnail 在预设路径上静默失效

```
Models/PresetData.cs:25          public bool UseAdvancedCodec { get; set; }   ← 轴存在
Services/PresetManagerService.cs:134  UseAdvancedCodec = true,               ← 只有**内置预设**写它
MainWindow.xaml.cs:5826          EnableThumbnail = EnableThumbnailCheck?.IsChecked ?? false,   ← BuildPresetData 不写 UseAdvancedCodec
MainWindow.xaml.cs:5944          EnableThumbnailCheck.IsChecked = p.EnableThumbnail;           ← ApplyPresetData 也不回放 UseAdvancedCodec
MainWindow.xaml.cs:2863          var thumbOn = useAdvCodec && (EnableThumbnailCheck?.IsChecked ?? false);  ← 只有实跑/预览这条路径有闸门
```

用户在勾着「高级编码选项」时勾了缩略图并保存预设 ⇒ 预设里 `EnableThumbnail=true`、`UseAdvancedCodec=false`。之后若用户当前会话没勾总开关就应用该预设：**复选框被置位、面板是隐藏的、采集返回 false、预设作者以为它开了**。同样的形状适用于 `TiffDpi`（`:5825`）、`AvifTune`（`:5754`）等一切只存在于 `AdvancedCodecPanel`（`:3777`）里的项 —— 所以这是**既有结构缺陷**，但本次新增的 ThumbnailPanel 是第一个被它影响的新功能，且 CHANGELOG 的「预设/Old 回放」口径里没提。

修法有两支，任选其一并登记：① `BuildPresetData` 写 + `ApplyPresetData` 回放 `UseAdvancedCodec`；② 让 `BuildPresetData` 对依赖该闸门的值也带上同一个 `useAdvCodec &&`（与采集侧同一个表达式）。

### 2.7 图标三处落地一致性

- 托盘：`App.xaml.cs:56 Icon = LoadTrayIcon()` → `:78` 读 `avares://FfmpegGui/Resources/icon_raw.png`（先 `AssetLoader.Exists` 再 `Open`）；
- 主窗口：`MainWindow.xaml:8 Icon="avares://FfmpegGui/Resources/icon_raw.png"` —— **同一条 URI**（✅ 不存在「托盘有、窗口没有」）；
- exe 文件：`csproj:24 ApplicationIcon` → **另一份文件**（见 1.3）；
- 【轻微·D-10】`ProgressWindow/ImageDetailWindow/PresetManagerWindow/FormatFilterWindow` 均**未设 `Icon`** ⇒ 次级窗口仍是一张空脸。
- 验证它有没有牙：`tests/UiTestHost/Program.cs:417-460` 的 C20 有三条独立对照（`AssetLoader` 认得 URI / 把同一资源先整读进内存再解码 / 与磁盘那份原 PNG 比较尺寸），并有「连对照件都解成 1×1 就记 SKIP」的反假绿闸门 —— 这是对「不到位托盘」最有力的一条；**但它验的是资源可达性，没有直接断言 `App._trayIcon.Icon != null`**。

---

## 3. 双语资源与 CJK 硬编码门禁

### 3.1 双语完备性（**已核实无问题**，脚本实测）

- 新增 UI 文案 6 键 + 改写 1 键：两语言文件同批进驻，键集合完全对齐。
- 我按 `{ext:Loc <key>}` 反查了全部 XAML/AXAML：**290 个被引用键，`zh-CN.json` 缺 0、`en-US.json` 缺 0**；`zh-only = 0`、`en-only = 0`（无单边残留、无英文占位）。
- 抽查：`thumbnail.*` 5 键 + `hdr.mode.gainmap/traditional` + `tip.gainmap.enable` 的中英文案逐条存在且语义对齐。
- `MainWindow.xaml:787/788/790/792/794` 新控件全部走 `{ext:Loc}`，**没有硬编码中文**。`HdrModeCombo` 的选项由代码侧 `FillComboByLoc(…, 0, "hdr.mode.traditional","hdr.mode.gainmap")`（`MainWindow.xaml.cs:949-950`）填充 ⇒ 双语都覆盖。

### 3.2 【严重·D-1】CJK 门禁的账与数不符（我实跑了它）

我在 HEAD（= `bdd4b0b`）实跑 `tests/scripts/_probe-cjk-hardcode-scan.ps1`：**`PASS=14 FAIL=0`**，读数：

```
语料自检：90 个源文件（XAML/AXAML 7 ≥5；C# 83 ≥30）
XAML/AXAML = 13   （基线 13，余量 0）     ← XAML 侧确实顶到底
C# 总计     = 1355（基线 1320，余量 -35）  ← ℹ 桶，已超登记值 35
  [tag]     = 487 （基线 459，余量 -28）   ← ℹ 桶，已超 28
  其余      = 868 （基线 861，余量 -7）    ← ℹ 桶，已超 7
     **UI 面** = 821 （基线 840，余量 19） ← 判据项：**余量 19，不是 0**
    诊断 sink   = 47（不判）
```

脚本自己在 `:298` 写着「余量 0 ⇒ 已被基线精确吸收；后续若再涨仍会判红」。**这句话现在是假的**：判据桶还剩 19 行空间。

更紧的问题是：**同一个提交同时松了尺子、又抬了基线**。

| | 旧尺子 + 旧基线 837 | 新尺子 + 新基线 840 |
|---|---|---|
| 同一份 HEAD 源码的 UI 桶读数 | **849**（见下方注释的复算法：旧尺子 Ui=849 Diag=21 Other=870 Cs=1357） | **821**（Diag=47 Other=868 Cs=1355） |
| 判定 | **超基线 12 ⇒ 判红** | **余量 19 ⇒ 绿** |

注：旧尺子那一列是**可复算的实测** —— 我把 `git diff e7de117..HEAD` 之前的 `Measure-Cjk`（即去掉 `ps1:355-394` 那两处口径修正的版本）在同一份 HEAD 源码上跑出来的数字是 Ui=849 / Diag=21 / Other=870 / Cs=1357。

也就是说：口径②的两处「修假阳性」（自述 ① 行尾注释 2 行、② 诊断续行 31 行）把 **26 行从判据桶搬到不判的诊断桶**、再剔除 2 行计数；叠加同批把 `$BASE_CS_UI` 从 837 抬到 840（`:314`）⇒ **合计让判据桶松了约 31 行**。这正是本仓 `TESTING.md §6` 反复警告的形状：门禁看起来是绿的，而门票上的数字没人复核。

> 顺带一个「尺子已被当成产品事实使用」的直接证据：`MainWindow.xaml.cs:6084-6086` 的注释明写「本处是新增行且该锁余量为 0，用中文会立刻把它顶红 ⇒ 日志串保持 ASCII」。开发者因为这个**错位的余量读数**把自己改成英文 —— 锁的行为已经在影响产品文案了，那它的读数就必须准。

### 3.3 【中等·D-7】第九次抬基线的逐条归因对不上

登记（`ps1:279-298`）声称：CS_UI **+3**（`QueueProcessor.cs:4192-4193` 两条 + `ImageEncoderArgs.cs:362` 一条比对常量），并且「CS_TAG +16 = `tests/ServiceProbe/Program.cs` 0c 段」。

我用**同一把尺子**对 `e7de117`（`git archive` 导出）与 `HEAD` 各测一遍，∆：

```
∆ XAML=0   ∆ Cs=+40   ∆ Tag=+29   ∆ Other=+11   ∆ Ui=+11   ∆ Diag=0
按文件拆分（仅列有增量的文件；Ui 列的括号内是该文件 base→head 的 Ui 读数）：
  Services\QueueProcessor.cs                  Ui +5   (114 → 119)   dCs=+8  dTag=+3
  Services\ColorMapping\RawColorPipeline.cs   Ui +5   ( 41 →  46)   dCs=+9  dTag=+4
  Services\ColorMapping\ImageEncoderArgs.cs   Ui +1   (  5 →   6)   dCs=+1
  Services\IsoBmffGainMapWriter.cs            Ui  0   (  0 →   0)   dCs=+12 dTag=+12
  Services\GainMapEncoder.cs                  Ui  0                  dCs=+7  dTag=+7
  Services\GainMapDecoder.cs                  Ui  0                  dCs=+3  dTag=+3
（∑dCs = 8+9+1+12+7+3 = 40 ✅ 与总量一致；∑dUi = 11 ✅）
```

- 登记说是 +3，实测 **+11**；`RawColorPipeline.cs` 的这 5 行**完全没被登记**（代表性行：`:520-522` 的 cjxl 出口解释、`:543-544` 的 PNG 中转失败拒绝）。
- 「CS_TAG +16 来自 `tests/ServiceProbe/Program.cs`」这条**不可能成立**：该门禁的语料写死为 `$root/src/FfmpegGui`（`ps1:402`），`tests/` 一个字都扫不到。真实的 TAG 增量分布在 `IsoBmffGainMapWriter.cs`(+12) / `GainMapEncoder.cs`(+7) / `RawColorPipeline.cs`(+4) / `QueueProcessor.cs`(+3) / `GainMapDecoder.cs`(+3)（这是我的逐文件 ∆Cs/∆Ui 统计口径）。
- 【轻微·D-11】顺便发现新规则②有实现缺陷：`$stmtSink` 只在「非续行」时重算（`:392`），而一条语句的首行**常常紧跟在某个未闭合行之后**（如 `if (!x)` 换行后接 `log?.Invoke(`）⇒ 语句自身带 sink 也不刷新 `$stmtSink`，其后继行仍按 UI 计。证据：`RawColorPipeline.cs:519`（带 sink，计入诊断）与其续行 `:520-522`（本应随语句算诊断，实测计入 UI）。方向上它是 fail-closed（宁可多算 UI），所以**不构成假绿**；但它让「UI 面」这个数对同一份源码在不同写法下不稳定 —— 这也是 3.2 里那两个数不能直接互推的原因之一。

---

## 4. 预设与持久化幻觉风险

### 4.1 【已核实无问题】两处逐字段克隆点都补上了

`AppSettingsService.Save()`（`AppSettingsService.cs:158-198`）的手写克隆表**不需要改**：三个新字段落在 `FfmpegOptions` / `PresetData`，不在 `AppSettings`。真正对口的两处：

- `MainWindow.xaml.cs:3491-3493`（UI 侧 Clone）：`EnableThumbnail / ThumbnailLongEdge / ThumbnailQuality` ✅
- `QueueProcessor.cs:4920-4922`（队列侧第二处克隆点，注释还特意写明「逐字段手抄的第二处克隆点，漏抄即丢」）：三项齐全 ✅

其余接线：`Models/FfmpegOptions.cs:105-107`（预设→options，其中两项是「有值才覆盖」的 nullable 语义）、`:406-410`（默认 160/75）、`Models/PresetData.cs:133-138`（null=沿用默认）、`MainWindow.xaml.cs:5944-5946`（预设→UI 回放，带 `HasValue` 判空）、`QueueProcessor.cs:1610-1612`（唯一调用点挂在 `RestoreMetadataAsync` 内，20 个调用点共用 ⇒ 「元数据先写、色彩标签最后定」的不变量保持）。

### 4.2 【轻微】版本兼容性：**没有版本号这条握手**

`Models/PresetData.cs` 头部无 `Version` / schema 字段；缺少新字段时 `System.Text.Json` 给的是**静默默认值**（`bool`→false、`int?`→null），既不报错也不播报。对这三个字段来说后果是安全的（false + 沿用默认），这也是本仓一贯做法；只是「旧预设缺新字段」这件事对用户完全无声 —— 与本仓「静默 < 响亮但看不懂 < 响亮且点名」的价值序有微弱张力（当前这批字段恰好不需要响亮）。

### 4.3 【中等】唯一的真幻觉源 = D-6（`UseAdvancedCodec`）

见 2.6。它不是「忘了进克隆表」，而是「闸门自身没参与持久化」：隐藏的控制面板 + 预设回放的组合下，用户的预设值既不报错也不生效。

---

## 5. 文档账本与真实性

### 5.1 【已核实无问题 / 部分只能信其措辞】

| CHANGELOG 声明 | 能否结构性验证 | 结论 |
|---|---|---|
| 缩略图只对 6 种容器开放 | ✅ 可 | `FormatCapabilitiesService.cs:246-247` 的 `foreach` 只给 `jpg/png/apng/webp/avif/jxl` 置 true；UI 侧 `:2486-2497` 用同一判据 ⇒ **属实** |
| 「≥2 个独立读者交叉核过」（apng 用 `cjxl`+`jxlinfo`，2250 B vs 250 B） | ❌ 需 exiftool/jxlinfo 实跑 | **只能信其措辞**；我无法在此核验。它与「给 gif 开一条假能力 ⇒ 应红 2 条」的 M2 变异设计互相咬合，但那是「有人做过实验」的痕迹，不是我能复算的读数 |
| UI 面板只看这 6 种、其余出声拒绝 | ✅ 可 | `ThumbnailService.cs:107-112` 返回 -1 且 `emit` 点名；`:114-124` 隐私/工具缺失同样出声 |
| `ServiceProbe thumbnail` 40/0、M1 33/7、M2 38/2 | ❌ 需实跑 | **只能是账本读数** |
| `-e av1_nvenc` 硬失败、CLI 够不到 `FfmpegOptions.Encoder` | ✅ 可，且**成立** | `CliParser.cs:161-162` 对未知编码器 `throw ArgumentException("未知编码器: …")`，headless catch（`Program.cs:287-291` / `:352-355`）返回 2/1；全仓 grep `options.Encoder =` **没有任何 CLI 赋点**（只有 `MainWindow.xaml.cs:3451` 与 `QueueProcessor.cs:4866` 两处克隆）⇒ 「这一支 CLI 走不到」属实 |
| L3 判据 gitignored → 受管清单 60 → 65 | ✅ 可，且**成立** | `_run-step3-gates.ps1:1003 expectedScriptCount=65`；我按括号配平抽出 `$scriptList`：**65 项、去重后仍 65、无重复**；末 5 项正是那 5 条且 `ls` 文件都在；`$tableOnly=4` / `$noSelfSummary=7` 与注释自称一致；超时针 `= 900` 全仓恰好 1 处、旧值 `= 480` 全仓 0 处（`:924` 的形态针同步改过）⇒ **这条完全落地** |
| CHANGELOG 里引用的行号（`:2840`、`:916-918`） | ⚠ 已漂移 | 实际采集点在 `:2894`（`AvifTune`）、`FillComboByLoc(AvifTuneCombo)` 在 `:928`。本仓 `COLOR_TRAPS §十四` 早已立规「行号不是稳定标识符」，这里又踩了一次 ⇒ 【轻微】，建议改成具名 handler |

### 5.2 【轻微·D-13】「四处文档同步」没同步到四处

`_run-step3-gates.ps1:920` 声称 65 这个数同时记在 `.workbuddy-ai/memory/MEMORY.md` / `docs/HANDOVER.md` / `docs/TESTING.md §3.5`。实测：

- `docs/TESTING.md:261` ✅ 写着「当前 **65 条**（2026-10-01 再接线…由 60 → 65）」，并附完整来源链；
- `docs/HANDOVER.md` ❌ grep 不到 65（它的 `script-list=N 条（受管基线 N）` 全是 09-x 的历史读数，最新只到 49）；
- `MEMORY.md` ⚠ 所在目录 `.workbuddy-ai/` 早被 `.gitignore:159` 忽略（本批又给 `.gitignore` 加了 `.commandcode/`、`.workbuddy/`，`:179-180`）⇒ 这一处对任何 fresh clone 都不可核。

⇒ 声称的「四处」实际只有两处核得到：运行器自身的常量与 `docs/TESTING.md:261`；`docs/HANDOVER.md` 查不到，`MEMORY.md` 不在版本控制里。条数锁本身（`if ($actualScriptCount -ne $expectedScriptCount)`）仍然有效，所以这只是账本失准，不影响判决。

### 5.3 README 最终状态（**已核实无问题**）

- 两份 README 都停在「单仓 `ColorGainKit` + 仓内两个互不引用工程」这一版：`github.com/luoye-cpu` 的链接在 README 里只剩主仓 + `ColorGainKit` 两个，没有散落到 `/ColorMatrix`、`/GainMapKit` 的旧仓链接；中英文表述一致（英文原句：they ship as **a single public repository** … **two projects that do not reference each other**）。
- 没有发现前一版「两个 MIT 独立库（两个仓）」的残留措辞，也没有自相矛盾：许可段同步加了一节范围区分，与正文互指一致。
- 【轻微】唯一的小疙瘩：`README.md:5` 仍写「v1.6.0 — 2026-09-28 发布」，而 csproj 已是 `1.6.0-beta4`、CHANGELOG 另有 beta4 节。这是旧有做法（README 只讲正式发布版），不构成假话，但读者对不上号。

### 5.4 【轻微·D-12】`HANDOVER_2026-09-22.md` 连续 4 次更正后的病灶

读了最终版（`docs/HANDOVER_2026-09-22.md:48-49`）：

- **语义通顺、没说反**：`ColorKit` 已不存在 → 改单仓 → 旧两仓网页端删除（404 取证）→ bundle 退路可还原（克隆回到 `e0d2b35` / `250a7593`、工作树干净）→ 本地旧目录已清理。3 次记账更正 + 1 次补分隔符（`23efc80` 把「…通过…）本机旧目录」改成「…通过…）；本机旧目录」）都落实了，读起来不断句。✅
- **残留措辞**：第 48 行末仍写「**两仓**暂长期双份」，而紧跟的第 49 行说旧两仓已删 ⇒ 现在只有「本仓 + 1 个抽出的仓」两份，措辞没跟着这次合并改。
- **markdown 破表**：第 49 行是一条续行，**没有前导 `|`** ⇒ GFM 会把这一大段顶到表格外当普通段落，读起来像多出来一坨游离文字。改成在行首加 `|`（或并入上一行）即可。
- （一行闲话：后两条是我这轮发现的格式层缺陷，内容层无错。）

---

## 6. 已核实无问题（证据速查）

1. **csproj 没动任何引用/条件组** —— `git diff` 只有 Version + ApplicationIcon + 一条 AvaloniaResource；`InternalsVisibleTo(GainMapTestHost)` 是既有条目。
2. **`--thumbnail*` / `--jxl-modular` / `--hdr-mode` 全部真接上了**（CLI → options → Clone → 队列 → 服务 → 实参），并被 ServiceProbe T20 与 `verify-encoder-knob-liveness.ps1:127` 双向钉住。
3. **`HdrMode` 与 `JpegGainMap` 确实是同一个真值**（`FfmpegOptions.cs:327-344` 的 getter/setter），不存在「两个开关两种写法」的双真值；旧 `--jpeg-gain-map` / 旧预设自动兼容的声称成立。
4. **双语键集合完全对齐**，新 UI 文案 0 硬编码中文（含 ComboBox 的选项也走 `FillComboByLoc`）。
5. **XAML 硬编码中文没有增加**（13/13），新增注释都在注释里、被门禁正确剥离。
6. **两处逐字段克隆点都补了**，没有「漏一个字段被静默重置」的幻觉。
7. **缩略图的能力位是单一真值**（写侧与 UI 侧共用 `FormatCapabilitiesService.GetCapabilities(CapabilityKey(fmt))`），并且不支持时**同时隐藏 + 复位勾选**，本批这块做得比历史形状干净。
8. **运行时的托盘/主窗图标同源同盘**（URI 逐字相同），不存在「托盘有、窗口没有」。
9. **`-e av1_nvenc` 走不到 GainMap 的 tune 播报支**这一诚实声明，代码级可证 ⇒ 属实。
10. **L3 60→65 真实**：清单 65 项去重无重复、5 个脚本都在盘上、超时针 900 恰好一针（旧值 480 已无残留）、`tableOnly`/`noSelfSummary` 的 4/7 未变。

---

## 7. 建议的修复顺序（不改源码，只给顺序）

1. **D-1**（先修「读数」再谈其他）：把 `$BASE_CS_UI` 按实测下调到 **821**（并把 Cs/Tag/Other 三个 ℹ 桶同步到 1355/487/868），或在登记里如实写「本次既改口径又抬基线，实际余量 19」——二者选一，但**必须让 `余量 0` 这句话重新为真**；顺手把 3.3 的归因修正（`tests/` 不在语料里那条是硬错）。
2. **D-2**（等你那次构建）：构建后第一件事是确认 `ApplicationIcon` 是否通过；若通过但 exe 图标不对，把 csproj 注释改成实话（这份 ico 仍是 PNG 载荷条目，只是 5 档）；若不通过，就把 `ApplicationIcon` 撤掉或换真 BMP 载荷的 ico —— 托盘/窗口不受影响，所以这一行是纯收益。
3. **D-3 + D-6**：先给 `useAdv`/可见性闸门一个**唯一判定函数**（采集/预览/预设保存三处共用），再决定要不要把 `UseAdvancedCodec` 纳入预设往返。
4. **D-4**：三选一收敛「哪些容器能吃 HDR」，并把没有消费者的 `FormatCapabilitiesService.SupportsGainMap` 要么删、要么接到 GUI 上（推荐后者，与缩略图的做法一致）。
5. **D-5**：给 `--thumbnail*` / `--jxl-modular` 补 `--help`，并考虑加一条「帮助文本必须覆盖所有 `case`」的结构锁（否则下次还会漏）。
6. **D-8～D-13**（零散，合并一批处理）：`--thumbnail-size/quality` 的越界要出声、`README.md:5` 的版本串、`HANDOVER_2026-09-22.md` 的「两仓」措辞与第 49 行缺前导 `|`、`TESTING.md` 与运行器之外的第三四处同步、次级窗口补 `Icon`。

---

*报告结束。所有读数为 `bdd4b0b`（工作树干净）上的实测量；凡标「需实跑」的账本读数，本报告只确认其**能否被结构性**验证，不声称复算过它们的数值。*
