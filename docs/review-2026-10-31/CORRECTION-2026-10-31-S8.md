# 更正条目 · 2026-10-31 · S8 修复落地、第 87 条门禁入列、三条表外待决的兑现

**性质**：本文件是对 `docs/review-2026-10-31/AUDIT-独立性审查-2026-10-31.md`（下称**原档**）的**追加更正条目**。
原档**一字未改**——它自己 §3 立的规矩（带日期的账本是时间点记录，旧路径旧读数不回改，要更正就新写一条）
同样适用于它自己。凡本文件与原档冲突处，以本文件为准，因为本文件的每个数字都能指到一条带日期的命令与日志。

**本文件不重新判 S1–S10**，只做三件事：① 结算原档 §7.3"表外另三条"与 §7.2 第 3 条的现状；
② 把 S8 修复落定后的**判据与读数口径**写清（含一处与原档 65269 的口径差，不抹平）；
③ 登记条数漂移 86→87 与当轮指纹。

---

## 0. 逐条结算

| 原档位置 | 原记（原文要点） | 现状（2026-10-31） | 依据 |
|---|---|---|---|
| §7.3 表外 ① | `_probe-white-point-kernel.ps1`"按设计就是红的（S8 未修 ⇒ `PASS=21 FAIL=2 SKIP=0 ENV=0`）……正确时机 = **S8 修复落地那一批**，届时 86→87 同批改四处可让整轮保持绿（G20）" | **已兑现**：S8 修复与入列同批，`$scriptList` 第 87 条、`$expectedScriptCount = 87`、runner 内三处散文条数同改；修复件 **27/0/0/0** | `tests/output/_tmp/run87_final4.log`；`_run-step3-gates.ps1` self-sha256 `8711987BFBC52A01` |
| §7.3 表外 ② | "S8 的修法形状已被 G19 收窄：除数**必须按输入位深选**、**不得钳位/压平 16-bit 顶段**（`band_uniq=252` 塌成 1 就是第二颗牙）⇒ `/65283` 一刀切已被排除" | **已按此落定并实测**：见 §1；`band_uniq=252` 在全绿轮里为 PASS，且**未修件下也仍绿**（正是它证明"只动 8-bit 侧"没有压平真 16-bit 顶段）；它抓的是另一族假修复——"一刀切 `/65283` 或钳位"会让 B 组塌缩转红。本轮没有做这一支变异（第 87 条只做了 S8 本体的对照臂），故按 G19 的说法记为**设计前提已具备、变异未跑** | 第 87 条 B 组读数；§2 对照臂 |
| §7.3 表外 ③ | "`AGENTS.md §2` 仍写「84 条」，而运行器的受管基线此刻是 **86** ⇒ 订正归维护者" | **已订正为 87**（`AGENTS.md` 与 `docs/TESTING.md` 同批），并在 `docs/TESTING.md` 追加"第二十二次接线 1 条"条目；历史条目里的 84/85/86 读数按时间点记录**不回改** | `git diff AGENTS.md docs/TESTING.md`；子集输出 `script-list=87 条（受管基线 87）` |
| §7.2 第 3 条 | "色彩引擎的 10/12-bit 源、非中性色：本轮刻意只用中性轴，S8 在那两族的形状**未量**" | **仍开放**（本轮只补了**载体**边界，不是位深族）：`png8_gray` 与 8-bit jpeg 的扩位顶码实测 **65280** ≠ 65283 ⇒ 这两族白点归一后 0.99995，8-bit 交付仍 255，**16-bit 交付会得 65532 而非 65535**；10/12-bit 源**未实测**，一律按 65535 | `tests/output/_tmp/s8probe/`（数据）+ `tests/output/_tmp/probe_depth.py`（逐点 256 级写手）；§1 边界段 |
| §7.2 第 1 条 | "门禁牙齿的其余 83 条未做变异" | **本轮把余量又减一条**：第 87 条已做双向变异取证（§2），故按 87 条基线算，未做变异的余量是 **85 条**（实做过的只有第 84 条 G11 与本轮第 87 条） | 原档 §G9/§G11 + 本文件 §2 |
| §S8 ③ | "<16-bit 源交付成 16-bit 时产物白 = **65269**（`lanes/pixel-truth/out/R16/sw12.png`，双侧读手逐样本相等）" | **不改判，只标口径**：本仓对照臂的 B3 臂（P3 纯白块 → sRGB → PNG16）实测停在 **65283**。两臂**不是同一夹具、不是同一读数**：原档那条是 4096 px **斜坡件**且经过一次非恒等变换，本条是全白块、白点过矩阵后仍 1.0。两者都成立；判据分界看"是否顶到 65535"，不看顶到哪一个小数 | §3 口径差专段 |
| §7.3 表内 IL/环境 | 需要维护者在手开普通终端跑一次 whoami 拷贝 | **本轮未处置**（环境未变）；但把本轮 SKIP 的具体条目钉住：`_probe-env-trust-baseline` 唯一 SKIP = **①c 无 `%APPDATA%\FFmpegPictureUI\settings.json`**（来源只有 env + 仓默认），读数 **10/0/1** | 直接跑该探针的输出；§4 |

---

## 1. S8 修法（落定形状，只写已落地的接口与边界）

| 面 | 内容 |
|---|---|
| 契约载体 | `ColorKernels.TransformSpec.SrcCodeMax`（`double`，默认 `65535.0`）+ 常量 `SrcCodeMax8 = 65283.0`（LF/无 BOM） |
| 取值规则 | 源位深 **≤8-bit ⇒ 65283**；其余（含位深探测失败、管道输入）**⇒ 65535 = 改前行为**，绝不猜 65283 |
| 作用面（解码侧） | `BuildTables(curve, srcCodeMax)` 的 **Eotf** 行（`Oetf` 仍 `/65535`）、`TransformRgb48leCore` / `TransformRgb48leToRgb8` 的非表分支、`MeasurePeakLinearRgb48` |
| 不动面 | 索引域 `CurveTables.Oetf`、`BuildEncodeLut16`；编码侧 `EncodeTo8` 的 `code16 / 65535 * 255` —— 都在线性 [0,1] 里自洽，是另一把尺子，跟着改会把真 16-bit 源顶段 252 码压平（G16/G19） |
| 传递 | **执行体单点置位**（推翻原"`ColorIntent → PlanPolicy → ColorTransformPlan.ToSpec` 四手传递"设想）：`RawColorPipeline.cs:173` 的 `spec.SrcCodeMax = ResolveSrcCodeMax(inputPath);`；intent/policy/plan 三个类**零改动** |
| 幂等性 | **`IsIdentity` 不纳入 `SrcCodeMax`**（见 §1.1 两版修订：纳入会破坏无损往返）。恒等臂本就不做重采样 ⇒ 不置位、保持 65535 |
| 已知边界 | ① `png8_gray` / 8-bit jpeg 顶码 65280（16-bit 交付 65532）；② 10/12-bit 未实测；③ JXL/JXR 容器内 ICC/位深不可判（原档 §4.1） |

### 1.1 两版修法：第一版被实测否掉，现行是第二版

| 版 | 形状 | 读数 | 结论 |
|---|---|---|---|
| 第一版 | `IsIdentity` **纳入** `SrcCodeMax` 判据（恒等臂也置位 65283） | 第 87 条 27/0 全绿，但 `verify-package-smoke.ps1` 的 A2「往返逐比特相同（无损仍是无损）」**转红**（产物 3896 kB） | **已废弃**：恒等臂上 65283/65535 不成对 ⇒ 净 ×1.00386 ⇒ `a_src/a_out` 差异 **403,523 / 5,760,000 字节（7.0%）全部 +1** ⇒ 无损往返被破。取证：`tests/output/validate/l3_pkg_smoke/{a_src,a_out}.raw` |
| 第二版（现行） | `IsIdentity` **不**纳入 `SrcCodeMax`；置位点收窄为 `if (!spec.IsIdentity) spec.SrcCodeMax = ResolveSrcCodeMax(inputPath);` | 第 87 条 **27/0/0/0**；A2 **PASS 往返逐比特相同 exit=0 产物=4297.9 kB**（对照组未修件 4322.2 kB） | 采纳 |

**为什么第一版是错的**：S8 的缺陷域是"**非恒等**变换把 8-bit 源扩位后的样本仍按 `/65535` 归一"。恒等臂根本不做色域/曲线变换，把一个分位数判据挂到它身上就是让"不变"变成"变"——第 87 条不抓 A2，所以只有整轮里的 `verify-package-smoke` 能抓到它。这条正好说明**第 87 条单独全绿 ≠ 本批无回归**，整轮不可省。

改动落点（两处，均为字节层核对过 BOM/EOL）：`Services/ColorMapping/ColorKernels.cs`（64,544 B / 1032 LF / 无 BOM）、`Services/ColorMapping/RawColorPipeline.cs`（111,563 B / 1410 CRLF / 无 BOM）。

---

## 2. 对照臂（"这条锁有牙"的证明，可复现）

**做法**：把修复件的唯一判据点反退回改前行为，**只动一处**，其余代码/夹具/命令完全不变。

1. `RawColorPipeline.ResolveSrcCodeMax` 首行插入 `return 65535.0; // CONTROL-ARM-TEMP`；
2. `dotnet build ffmpegPictureUI.sln -c Release`（必须整建，见 §4 的 STALE 坑）；
3. `pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1 -Scripts '_probe-white-point-kernel.ps1'`；
4. 记读数 → **还原**（`rg CONTROL-ARM src/` 应为 0 命中）→ 重建 → 修复件复跑。

**读数**：

| 件 | 结果 | 红在哪 |
|---|---|---|
| 修复件 | **PASS=27 FAIL=0 SKIP=0 ENV=0**（19.9 s） | — |
| 对照臂（`SrcCodeMax` 恒 65535） | **PASS=23 FAIL=4** | A 组两臂 `max=254`；**B3 两行 `max=65283` / `blk_min=blk_max=65283`**；B 组（真 16-bit 顶段 `band_uniq=252`）与 C 组（直通/legacy）**仍绿** |

⇒ 新判据只抓 S8 本体，不误伤 16-bit 臂与直通臂；反之"改成 `/65283` 一刀切或钳位"会让 B 组 `band_uniq` 塌缩转红（原档 G19 的第二颗牙）。
日志：`tests/output/_tmp/control_unfixed.log`（对照臂）、`tests/output/_tmp/run87_final4.log`（修复件）。

**关于原档 §7.3 表外① 的 `PASS=21 FAIL=2`**：那是**加入 B3 臂之前**的版本读数（当时共 23 条断言、A 组两臂红）。
现版本共 27 条断言、未修件 4 红（A 组 2 + B3 2）。算术自洽：`21+2 = 23`，`23+4 = 27`。两组读数不矛盾，别当成"红数变多了"。

---

## 3. 口径差专段：`65283` 与 `65269` 都成立

原档 §S8 ③ 记的是「**<16-bit 源交付成 16-bit** 时产物白 = **65269**」，一手件是 `lanes/pixel-truth/out/R16/sw12.png`，
两侧读手（重写读手 + 随包 ffmpeg）逐样本相等。本仓对照臂的另一条臂给出 **65283**。差异不是谁错了，是**臂不同**：

| | 原档 §S8 ③ 那条臂 | 本仓 B3 臂（`K8_to_png16`） |
|---|---|---|
| 夹具 | `sw12.png`：**斜坡**，4096 px，含全码域分布 | `w8_p3.png`：**全白块** 12288 px |
| 变换 | 非恒等（斜坡件走完 P3→sRGB 的完整矩阵+曲线） | P3→sRGB，但输入是全白 ⇒ 矩阵行和为 1、白点仍 1.0 |
| 交付顶码 | 65269（`… 54983, 61779, 65269`） | 65283 |
| 用途 | 证明"偏置只落在 <16-bit 源这一侧" | 给修复划**分界线**：必须顶到 65535 |

⇒ 判据与注释一律写"**是否顶到 65535**"，不写"应当等于 65283/65269"。本轮已按此口径改了 `ColorKernels.cs`、
`RawColorPipeline.cs` 与探针头注的措辞（早期一句"非推测的 65269"把一条**实测**说成了推测，已自我更正）。

---

## 4. 条数漂移与子集读数（含两处容易被当成矛盾的数字）

1. **86 与 87**：原档 §7.3 引的 `script-list=86 条（受管基线 86）` 是**当轮**读数；本批按 `AGENTS.md` §2 同批改齐四处
   （`$scriptList` / `$expectedScriptCount` / 三处散文条数 / `TestGroups.cs`——本探针不新增 ServiceProbe mode，故第四处不动），
   改后读数 `script-list=87 条（受管基线 87）`。历史读数保留，不当成冲突。
2. **`_probe-env-trust-baseline` 的 `11/0/0` 与 `10/0/1`**：原档 §G20 记该探针 `exit=0 PASS=11 FAIL=0 SKIP=0 @1.9 s`；**本轮两次独立跑均为 10/0/1**。
   差的那一项就是唯一 SKIP = **①c**（无 `%APPDATA%\FFmpegPictureUI\settings.json`，来源只有 env + 仓默认）⇒ 原档那次 ①c 是 PASS。
   `11 = 10 + 1`、`SKIP 1` 顶掉 `PASS 1`：**断言一条没少、也没有转红**，只是机器状态变了。把 SKIP 当通过会掩盖这一类差异，故以实测为准。
3. **子集 = 不构成基线**：`-Scripts` 只跑 4 条（`_probe-i18n-scan` 11/0、第 84 条 30/0、第 86 条 10/0/1、第 87 条 27/0/0/0），
   运行器自己打了"不构成基线"。宣布整体全绿仍需整轮 43~52 min。
4. **归属表不用手改**：第 87 条入列后补跑 `_probe-test-group-coverage` ⇒ `PASS=37 FAIL=0`，⑪-c「已进受管清单的脚本必须从归属表删掉（漏删 0 条）」通过、⑪-e「清单外 **35** 支 == 被点名 7 + 族内 **14** + 显式表 14 + 未归属 0」自洽（此前"族内 15"）。⇒ 该探针按清单自动重算归属，**不需要**同批改 `TestGroups.cs` 或任何归属表；`verify-probe-mirror-parity` 同时 **87/0**（执行面 61 个 `Probe*` 仍只有一份定义）。
5. **STALE 坑（复现时必踩）**：只编 `FfmpegGui.csproj` 不更新 `tests/projects/ServiceProbe/bin/**/FfmpegGui.dll` ⇒ 下一轮**拒跑**；
   **改注释也会触发**（比的是 mtime）。必须 `dotnet build ffmpegPictureUI.sln -c Release`。
   另：本机 `dotnet` 须显式 `C:\Program Files\dotnet\dotnet.exe` 并补 `DOTNET_ROOT/APPDATA/LOCALAPPDATA`，否则 NuGet 报 `Value cannot be null. (Parameter 'path1')`。

---

## 5. 仍未回应 / 仍开放（留给下一批）

- 原档 §7.3 表内 8 项（S1/S2/S3·S4/S5/S6/S7/S9/IL）**本轮一条未动**，全部仍等维护者裁决。
- 原档 §7.2 其余 6 条缺口（"其余 83 条门禁未做变异"、JXL/JXR 独立读手、换臂代价、cjxl 结构化读数、`-1073741511` 边界、3 处 `CK $true`）**未动**；其中变异那一条的余量按 87 条基线折算为 85 条。
- 本批新增的两处 S8 敞口：gray8 / 8-bit jpeg 顶码 65280 的载体细分；10/12-bit 扩位顶码实测。
- **未 push**：第二版修法后整轮 87 条已跑（§7：85 绿 / 2 红，两红均定性为既存/工具口径）⇒ 具备申请推送的条件，但仍**等维护者点头**才推（纪律：不自行 push）。

---

## 6. 附：当轮指纹与日志

| 项 | 值 |
|---|---|
| 产物指纹（第一版修法那一轮，`run87_final4.log`） | `FfmpegGui.dll=33DF225A6709452F` `FfmpegGui.exe=D32D5A1D3BB8A686` `ServiceProbe.exe=97179BAF0B6BD8CF` `UiTestHost.exe=F0BC7FC29B12F292` `GainMapTestHost.exe=D2F3AE6B58EC70B5` |
| 产物指纹（**第二版修法**整轮，`run_full_87b.log`） | `FfmpegGui.dll=97E2C47D2B42B8E9` `FfmpegGui.exe=D32D5A1D3BB8A686` `ServiceProbe.exe=97179BAF0B6BD8CF` `UiTestHost.exe=F0BC7FC29B12F292` `GainMapTestHost.exe=D2F3AE6B58EC70B5` —— 只有 `FfmpegGui.dll` 随 §1.1 的源码改动而变 |
| 运行器 | `runner-self-sha256=8711987BFBC52A01 lines=1665 bytes=171169`（启动与收尾两次一致）；`shape-selfcheck=OK（17 针）` |
| 探针 | `_probe-white-point-kernel.ps1` sha256[:16] = `140CF2E6E3FC668D`，684 行、LF、带 BOM |
| 日志 | `tests/output/_tmp/run87_final4.log`（修复件四子集）/ `run87_final5.log`（第 87 条 + 归属/镜像/CJK 三条自洽复核）/ `control_unfixed.log`（对照臂）/ `whitepoint_d0b2e3c3/`（final5 那一轮的产物轮次目录，保留下来的唯一一份）/ `run87_final6.log`（文档全部落地后复跑 `_probe-cjk-hardcode-scan` **14/0** 与 `_probe-i18n-scan` **11/0**，证明新写的两份文档没把它们弄红） |
| 顶码实测 | `tests/output/_tmp/s8probe/`：6 种载体（`png8_rgb` / `png8_gray` / `bmp8` / `webp8` / `jpg8` / `png16`）各 1536 B = **256 级 × 3 通道 u16 逐点**，含斜坡原件的 4 份源（`ramp8.png/.bmp/.jpg/.webp`、`ramp16.png`、`ramp8rgb.png`）与生成脚本 `probe_depth.py`（在 `tests/output/_tmp/` 根，**不在** `s8probe/` 内） |

---

## 7. 整轮 87 条基线（2026-10-31，第二版修法后）—— 可宣布的那一轮

| 项 | 读数 |
|---|---|
| 命令 | `$env:LC_ALL=$null` → `TMP/TEMP/TMPDIR = tests/output/_tmp/scratchroot` → `pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1`（整轮，不带 `-Scripts`） |
| 结果 | **87 条里 85 绿 / 2 红**，`EXITCODE=1`；各脚本自报耗时合计 **2664 s ≈ 44.4 min**（落在 43~52 min 区间，无早期截断） |
| 两条红 | `_probe-stderr-merge-scan.ps1`（§7.1）、`verify-dng-metadata-transport.ps1` 的 ㉛（§8）——**均为既存/工具口径，与本批产品改动无关** |
| 指纹 | `FfmpegGui.dll=97E2C47D2B42B8E9` `FfmpegGui.exe=D32D5A1D3BB8A686` `ServiceProbe.exe=97179BAF0B6BD8CF` `UiTestHost.exe=F0BC7FC29B12F292` `GainMapTestHost.exe=D2F3AE6B58EC70B5`（**与当轮日志同段**，跑后未动 `src/`） |
| 运行器 | `runner-self-sha256=8711987BFBC52A01 lines=1665 bytes=171169`（启动与收尾两次一致）；`shape-selfcheck=OK（17 针）`；`script-list=87 条（受管基线 87）` |
| 环境 | `env-cleaned=LC_ALL,LANG,LC_CTYPE`；`TMP/TEMP/TMPDIR` 全程钉在 `tests/output/_tmp/scratchroot`（当轮末 `_probe-env-trust-baseline` ④c 复核仍在位） |
| 日志 | `tests/output/_tmp/run_full_87b.log` |

### 7.1 第一轮那 43 项是怎么没的（环境假红占大头，不可当作已修）

第一轮（`tests/output/_tmp/run_full_87.log`）报 **43 项失败 / 34 支脚本**（零断言与退出码会各记一次，故项数 > 支数）。逐支重跑（`rerun_red_87.log`）后 30 支转绿，剩下 4 支再逐条定性：

| 批次 | 项/支 | 归属 | 依据 |
|---|---|---|---|
| 30 支 | — | **环境假红**：ffmpeg 系在系统 `%TEMP%` 被沙箱拦（典型 `[dngtool] unknown option: --version`、`完成 1 项, 失败 1 项`）；从外层进程重定向 `TMP/TEMP/TMPDIR` 进产物区后全绿 | `run_full_87.log` vs `run_full_87b.log`；`AGENTS.md` §4 已记此坑 |
| 4 项（⑬） | `verify-decision-delivery.ps1` | **门禁读手陈旧**：随包 `jxlinfo.exe`（1,743,872 B / 2026-10-29 21:29:08）把旧单行 `Color space: sRGB primaries` 拆成 `Color space: RGB` + `Primaries: sRGB` 两行，旧 `JxlCs()` 只取首条 `Color space` 行 ⇒ 永远读不到 primaries。**已修读手**（`$ls` 取全场 + `$pr` 取 `^Primaries\s*:`，合成 `"<cs> \| <pr>"`），**断言 `-match` 字面一字未动** ⇒ **92/0** | 修复件产物与 beta10 对照臂**同 sha256** `1FFF9C8795ECBED3911837C725B7B594C7164BACC93801687BA80523E5A80C6D` ⇒ 目标本就已兑现。日志 `dd_rerun_v2.log` |
| 4 项（㉛ 之外的 DNG） | `verify-dng-metadata-transport.ps1` | **本机 Python 缺 `rawpy`**：`_cmp-raw-plane-identity.py:15 import rawpy` → `ModuleNotFoundError`。`pip install rawpy`（**0.27.1**，写用户 profile）后 **104/1** | 单点复测 `RAW-PLANE OK … 逐位相同（24337152 点全等，min=510 max=16383）rc=0`；日志 `dng_rerun_v2.log` |
| 1 项（A2） | `verify-package-smoke.ps1` | **第一版修法的真实回归**（§1.1）⇒ 第二版已消除（A2 PASS） | `control_a2_pkg.log` / `run87_v2.log` |

### 7.2 `_probe-stderr-merge-scan.ps1` 的红：既存，且与本批无关

- 读数 `pass=5 fail=1`，站点 `verify-jxr-highbitdepth.ps1:527` 与 `:648`（`$r7back` / `leg_restore` 两处 `Exec … -show_entries stream=pix_fmt …`，属 ⑨/⑩ 的"探测环境已还原"收尾自证）。
- `git status --porcelain` 对 `_probe-stderr-merge-scan.ps1` 与 `verify-jxr-highbitdepth.ps1` **均无输出 ⇒ 与 HEAD 逐字节相同**，本批没碰过它们。
- 探针自己给出的出口是"在调用行留『合并取数已论证』并说明为什么值在 stderr"，**本轮没做**（改判据站点不在本批范围）。

## 8. ㉛ 定性收口：exiftool 13.55 → 13.60 的 MakerNote 标签枚举漂移（三版 A/B 实测）

㉛ 的红是 `total=9526（期望 9517）· audited=2367（24.9%）· files=44`，且三项自洽 `all_equal_total=True`。"总数对不上而自洽"只可能来自**输入（语料）或读手（工具）**变了，不可能是产品——`_audit-dng-meta-carry.py` 全文只有两处 `subprocess.run`，都调 exiftool（:368 `-s3 -Make -Model`、:372 `-a -u -G1 -s -e`），**零接触产品二进制**。

**A/B 做法**：把该脚本复制到 `tests/output/_tmp/etcmp/`，只改**两行**——`R` 钉成仓根、`EX` 由 `[exiftool.exe]` 换成 `[publish/PLAN/exiftool/exiftool_files/perl.exe, <该版 exiftool 脚本>]`；规则、语料（`tests/output/raw_samples*` 共 44 件）、参数一字未动。

| exiftool | 来源 | `ledger` 行数 | 对期望 9517 |
|---|---|---|---|
| 13.50 | MetaCPAN `Image-ExifTool-13.50.tar.gz`，用随包 `perl.exe` 跑 | 9513 | −4 |
| **13.55** | 同上 13.55（[官方存档](https://exiftool.org/history.html) 标 13.55 为 production release） | **9517** | **±0 ⇒ 期望值就是用它量的** |
| **13.60** | **随包现行**：`publish/PLAN/exiftool/exiftool.exe -ver` = 13.60，整树 mtime 2026-10-29 21:27:40 | 9526 | **+9** |

13.60 那一列取自**整轮门禁自己的产物** `tests/output/results/dng-meta/dng-audit/ledger.csv`（同语料、同脚本），所以不是"我另跑的副本对不上"。

**逐条差（13.60 − 13.55）**：

| 夹具 | 行数差 | 具体标签 |
|---|---|---|
| `Canon_EOS_40D_0.cr2` | +2 | `Canon:FirmwareInfo`、`Canon:TimeStamp` 新解出（267→269） |
| `Canon_EOS40D.CR2` | +2 | 同上（267→269） |
| `Canon_EOSR6.CR3` | +5 | `Canon:ExitPupilCode` / `Extender` / `LensAFCount` / `LensDiaphragmCount` / `LensFirmwareVersion` / `LensISCount` 新解出、`Canon:Canon_0x4027` 改名为具名标签（220→225） |
| 5 件富士 RAF（`FinePix_X10_2` / `X-S1_3` / `XS1` / `X-A1` / `X-A10`） | 0（改名） | `RAF:RAFVersion` → `RAF:FirmwareVersion` |
| 其余 36 件 | 0 | — |

净差 **+2+2+5 = +9**，全部落在**厂商 MakerNote 标签枚举**上。

**时间线自洽**：期望值 9517 由提交 `ed094a4`（**2026-10-23**）钉入门禁；随包工具链整树 mtime 2026-10-29 21:27:40（与 `jxlinfo.exe` 同批重装）；exiftool **13.60 的官方发布日是 2026-10-28**（[history](https://exiftool.org/history.html)）⇒ 那次重装把 13.55 换成 13.60，期望值随之悬空。

**处置（本轮不改）**：改期望值 = 改判据口径，属独立一批；且这个数字**必须与工具版本一起钉**。建议二选一：
1. 最小：`9517 → 9526`，并在该行注释记 `exiftool 13.60`；
2. 更好：门禁先断言 `exiftool -ver`，版本不符 ⇒ 直接点名"期望值需随工具版本重钉"，而不是让 ㉛ 看起来像产品漂移。

## 9. 本批最终落点

| 面 | 最终改动 | 状态 |
|---|---|---|
| 产品 | `Services/ColorMapping/ColorKernels.cs`、`Services/RawColorPipeline.cs`（§1 / §1.1 第二版） | 编译 0 警告 0 错误；第 87 条 27/0；A2 PASS |
| 门禁 | `_run-step3-gates.ps1`（第 87 条四处同批：`$scriptList`/`$expectedScriptCount`/三处散文，第四处不适用）；`verify-decision-delivery.ps1` 的 `JxlCs()` 读手 | 87/87 可跑，85 绿 |
| 文档 | 本文件 + `docs/HANDOVER.md` H-19 追补 + `AGENTS.md`/`docs/TESTING.md` 86→87 + `docs/INDEX.md` | 新写的文档复跑 `_probe-cjk-hardcode-scan` 14/0、`_probe-i18n-scan` 11/0 未破 |
| 未做 | ㉛ 重钉（§8 二选一）、`_probe-stderr-merge-scan` 两站点论证、S8 两处敞口（gray8/jpg8 顶码 65280、10/12-bit 未实测） | 均属独立批次 |
| 推送 | **未 push**，也不自行 push——等维护者点头 | — |
