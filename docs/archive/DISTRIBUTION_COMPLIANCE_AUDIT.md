# 版权 · 许可证 · 分发合规审计报告

**审计对象**：FfmpegPictureUI v1.6.0（`<repo-root>`）
**审计日期**：2026-09-20
**审计范围**：版权归属、许可证合规、二进制分发可行性、Markdown 文档完善性、双语结构正确性、更新日志合理性与合规性
**方法**：仓库实测（只读）+ 官方许可条款比对。所有结论附文件路径:行号或命令输出。

> **免责声明**：本报告为工程合规审计，非法律意见。涉及 Adobe、FFmpeg 构建、商标的条目已在正文标注「需法务/上游确认」。

---

## 0. 裁决摘要

| 维度 | 裁决 | 关键依据 |
|---|---|---|
| 版权归属 | ✅ 清晰 | 单一版权人 `luoye-cpu`，署名与仓库地址一致 |
| 源代码开源（GPL-3.0） | ⚠️ 基本可行 | 需修正 LICENSE 文本被改动、声明失实、仓库地址矛盾 |
| **二进制分发（含 PLAN 完整版）** | ❌ **当前不可用** | **随包 ffmpeg.exe 为 `--enable-nonfree` 构建，不可再分发** |
| 二进制分发（单文件版，不含外部工具） | ⚠️ 需整改后可用 | 缺 LICENSE 随包、缺 About 窗口（GPL §5d） |
| 商业/闭源分发 | ❌ 不可行 | GPL-3.0 强传染 + FFmpeg GPL 构建 |
| Markdown 完善性 | ⚠️ 明显不足 | 8 类标准开源工程文件全缺；README 53KB 且 94.7% 双语重复 |
| 双语结构正确性 | ❌ 机制失效 | GitHub 剥离 `<style>` ⇒ 中英两版同时渲染，切换不可用 |
| 更新日志 | ⚠️ 部分不合理 | 日期/版本号自洽，但 SemVer 被系统性稀释、验证数字过期、破坏性变更未标注 |

**一句话结论**：源码开源这条路径基本走得通；**以现有打包产物对外分发则存在硬阻断**——不是"有瑕疵"，而是"分发行为本身违法"，根源是那个 ffmpeg 二进制的构建参数。

---

## 1. 版权与许可证

### 1.1 版权归属：清晰，但缺少源代码级声明

| 项 | 结论 | 证据 |
|---|---|---|
| 版权人数量 | 单一 | `git log --format="%an <%ae>" \| sort -u` → `luoye-cpu <3412829159@qq.com>`，无第二作者 |
| 署名一致性 | ✅ 一致 | `LICENSE:4` 署名 `luoye-cpu` 与 `README.md:73` 的 `github.com/luoye-cpu/ffmpegPictureUI` 吻合 |
| 项目元数据 | ✅ 已声明 | `src/FfmpegGui/FfmpegGui.csproj:21` `<PackageLicenseExpression>GPL-3.0-only</PackageLicenseExpression>` |
| 源文件版权头 | ❌ 全部缺失 | `src/FfmpegGui/**` 的 .cs/.xaml 无 `Copyright`/`SPDX` 声明（唯一命中是 `ExifToolService.cs:558/739/827` 中作为**元数据字段名**的 "Copyright" 字符串，非声明） |

### 1.2 【P0】LICENSE 文本被修改，违反 GPL-3.0 自身规定

**事实**：
- `LICENSE:4` 在标题（1–2 行）与 FSF 版权行（`LICENSE:6`）**之间插入了项目版权行** `Copyright (c) 2025-2026 luoye-cpu` —— 该位置位于许可证正文内部。
- GPL-3.0 第 0 段（`LICENSE:8-9`）自身写明：`changing it is not allowed`。
- GPL-3.0 原有的 "How to Apply These Terms to Your New Programs" 附录被删除，`LICENSE:627` 起替换为 "Third-Party License Notices"。
- **同仓库内存在正确范例**：`publish/PLAN/exiftool/exiftool_files/LICENSE:1-5` 是**未修改**原文（FSF 版权在第 5 行，无插入行）。同一项目对两份 GPL 文本的处理方式不一致。

**后果**：可能被下游主张许可文本无效。虽属常见做法，但既然文件内自带 "changing is not allowed"，这就是可被指摘的瑕疵。

**修法**：
1. `LICENSE` 恢复为 GPL-3.0 **逐字未修改**全文（含 How to Apply 附录）。
2. 项目版权行移入独立的 `NOTICE` 文件，或写入各源文件头（`SPDX-License-Identifier: GPL-3.0-only` + `Copyright (c) 2025-2026 luoye-cpu`）。
3. 第三方声明移入独立 `THIRD-PARTY-NOTICES.md`。

### 1.3 【P0】第三方声明与"真实捆绑内容"直接矛盾

这是本审计最实质的发现之一：**LICENSE 声称不捆绑的东西，全部被捆绑了。**

| 声明位置 | 声明内容 | 实测 |
|---|---|---|
| `LICENSE:685` | `External Tools (NOT bundled / 外部安装，不包含在发行版中)` | ❌ 标题即失实 |
| `LICENSE:687-694` | FFmpeg/FFprobe：`Must be installed separately` | ❌ 已捆绑 |
| `LICENSE:696-700` | ExifTool：`Invoked as a separate child process`（列在 NOT bundled 组） | ❌ 已完整捆绑 |
| `LICENSE:702-707` | cjpegli/djxl：`Placed in the configured ... directory` | ❌ 已捆绑 |
| `LICENSE:670-675` | 仅捆绑 cjxl.exe | ❌ 实际捆绑 37 个 exe |

**捆绑实据**（`publish/` 共 **598** 个文件，非目录快照所示的 221）：

- **FFmpeg 完整捆绑**：`publish/PLAN/ffmpeg-full/` 含 `ffmpeg.exe`、`ffprobe.exe`、`ffplay.exe` + `avcodec-63.dll`(71.4 MB)、`avfilter-12.dll`、`avformat-63.dll` 等 15 个 dll。
- **ExifTool 完整捆绑**：`publish/PLAN/exiftool/` 含 `exiftool.exe`、`exiftool(-k).exe`、`perl.exe`、`perl532.dll`，以及 **438 个 `.pm` 模块**（`publish/PLAN/exiftool/exiftool_files/lib/Image/ExifTool.pm`）。这是 ExifTool 被完整再分发的确证。
- **其他**：`publish/PLAN/artifacts/` 含 `dngtool.exe`(8.0 MB)、`avifenc.exe`(11.8 MB)、`JxrEncApp.exe`、`JxrDecApp.exe`、`ultrahdr_app.exe`；`publish/PLAN/jxl/bin/` 含 22 个 exe；`publish/ffmpeg-full-2026.9.2-setup.exe`(27 MB) 是一个被直接再分发的第三方安装器。

**后果**：虚假声明会削弱合规抗辩，并可能被认定为故意隐瞒。ExifTool 的版权方（Phil Harvey）与 FFmpeg 项目均可据此主张侵权。

### 1.4 【P0】随包 ffmpeg.exe 为 `--enable-nonfree` 构建 —— 不可再分发

**实测命令与输出**（`./publish/PLAN/ffmpeg-full/ffmpeg.exe -version`）：

```
ffmpeg version git-2026-09-01-c27482a18d
configuration: --prefix=/home/dominic/ffmpeg/bin ... --enable-gpl --enable-nonfree
               --enable-version3 ... --enable-libfdk-aac ... --enable-libx264 ...
```

**为何这是硬阻断**：

1. **FFmpeg 官方合规清单第 1 条原文**要求：`Compile FFmpeg **without** "--enable-gpl" and **without** "--enable-nonfree".`（来源：https://ffmpeg.org/legal.html ）。本构建**两项皆用**，直接落在清单禁止项上。
2. `--enable-gpl` 使 GPL 覆盖整个 FFmpeg；`--enable-version3` 将许可升级为 **GPLv3**；而 `--enable-nonfree` 会引入**与 GPL 不兼容的非自由组件**（本构建中为 `--enable-libfdk-aac`，Fraunhofer FDK AAC 许可）。
3. 三者叠加的结果：该二进制**在 GPL 体系下无法被合法授权分发**——GPL 要求整体可以 GPL 分发，而 libfdk-aac 的许可禁止这样做。这是 FFmpeg 社区与下游发行方长期一致的解读。
4. FFmpeg 官方同时声明：`FFmpeg is not available under any other licensing terms, especially not proprietary/commercial ones, not even in exchange for payment.` ⇒ 不存在"付费取得分发权"的退路。
5. 该 ffmpeg 本身是**第三方构建**（Linux 交叉编译，`--prefix=/home/dominic/ffmpeg/bin`），来源不可审计。

**⚠️ 必须澄清的一个误区**：`LICENSE:690-694` 用「以独立子进程调用 ⇒ 属于 GPL mere aggregation 范畴」为 FFmpeg 开脱。这个论点**在法理上本身站得住**（进程隔离的独立程序聚合，不传染你的代码）——但它**只能免除"你的代码被 GPL 传染"**，**完全不能免除"你分发了那个二进制"**。分发行为本身受该二进制自身许可约束。在当前事实下，这个辩护**无效**。

**修法（二选一，推荐 B）**：
- **A. 替换构建**：改用 `--enable-gpl --enable-version3`（**去掉 `--enable-nonfree` 与 `--enable-libfdk-aac`**）的可再分发构建，并随包提供对应源码（GPLv3 义务）；或改用纯 LGPL 构建以降低义务。
- **B. 不捆绑（推荐）**：`PLAN/ffmpeg-full/` 移出发行包，改为首次运行引导用户指向自备 ffmpeg。这**同时消除最大的合规负担、包体（71 MB dll）与"不可复现打包"问题**，且与"单文件版"现有行为一致。

### 1.5 【P0】发行包内没有任何许可证文件

**事实**：`find publish -maxdepth 3 -iname "LICENSE*"` → **空**。`grep -n "LICENSE\|NOTICE\|COPYING" pack.ps1` → **无命中**。`pack.ps1:130` 只做 `robocopy PLAN → 输出\PLAN` 并生成「使用说明.txt」。

发行包内仅有的 11 个许可文件全部是第三方自带的：
```
publish/PLAN/exiftool/exiftool_files/LICENSE
publish/PLAN/exiftool/exiftool_files/Licenses_Strawberry_Perl.zip
publish/PLAN/jxl/bin/LICENSE.{brotli,giflib,highway,libjpeg-turbo,libjxl,libpng,libwebp,sjpeg,skcms,zlib}
```

**违反条款**：GPL-3.0 §4（`give all recipients a copy of this License along with the Program`）、§5、§6。以二进制形式分发时，**必须随附完整且未修改的许可证文本**，并履行 Corresponding Source 义务。

### 1.6 【P0】第三方声明中「不受 GPL 约束」构成 GPL §7 禁止的额外限制

**事实**（`LICENSE:640-641`）：

> These dependencies are licensed under their own terms and are **not covered by the GPL 3.0** that applies to FfmpegPictureUI itself.
> 它们各受自身许可证约束，**不受 FfmpegPictureUI 本身的 GPL 3.0 约束**。

在 ExifTool（GPL-1.0+/Artistic）与 FFmpeg（GPLv3 构建）**已被捆绑进同一发行包**的事实下，这句话实质上是宣称"包内的 GPL 组件不受 GPL 约束"，构成 GPL-3.0 §7 / §10 所禁止的 **further restrictions**。

**修法**：删除该句，改为中立表述——「各组件依其自身许可授权，本项目不对其施加额外限制；其许可全文见 `THIRD-PARTY-NOTICES.md`」。

### 1.7 ~~【P1】Adobe DNG SDK：许可原文缺失，再分发条款无法核实~~ → **已更正，见 `LICENSE_TERMS_VERIFICATION.md` §1.2**

> ⚠️ **本节结论已被推翻，保留原文仅为留痕。** 后续已从 Adobe 官方下载包取得 `LICENSE.txt` 原文（`https://www.adobe.com/go/dng_sdk`），核实结论为：**许可宽松，明确允许分发与再许可，且与 GPL-3.0 兼容**。真正的问题只是**未履行其 §2 的通知义务**（须随附版权与许可声明）——属易修项，**不是阻断项**。以 `docs/LICENSE_TERMS_VERIFICATION.md` 为准。

- 源码存在于 `tools/src/dng_sdk/dng_sdk_1_7_1/`，源码头（如 `dng_sdk/source/dng_1d_function.h:2-6`）写明：`Copyright 2006-2019 Adobe Systems Incorporated / All Rights Reserved. / Adobe permits you to use, modify, and distribute this file in accordance with the terms of the **Adobe license agreement accompanying it**.`
- **该 license agreement 文件在仓库内不存在**（`find . -iname "*adobe*"` 仅命中无关的 ICC profile 与 `.obj`）。`DNG_ReadMe.txt` 全文无 license/grant/redistribute 字样。
- `dngtool` 是**自研 CLI**（`tools/src/dngtool/dngtool.cpp` 自述 "LibRaw + Adobe DNG SDK 1.7.1"），**静态链接** DNG SDK 与 LibRaw 后以 `publish/PLAN/artifacts/dngtool.exe`（8.0 MB）**二进制形式随包分发**。
- `README.md:396` 声明「本产品包含 Adobe 授权的 DNG 技术」——但仓库内无任何可核验的授权文件。

**结论**：**无法核实再分发条款，需人工向 Adobe 确认**。若 Adobe 条款含使用范围或再分发限制，该限制会传递至整个 GPL 作品，触发 §7 冲突。

**附带问题（LGPL）**：LibRaw 为 LGPL-2.1 **或** CDDL-1.0 双许可。**静态链接进 `dngtool.exe` 而不提供可重新链接的目标文件**，可能违反 LGPL-2.1 §6（须允许用户以修改后的库重新链接）。

### 1.8 【P1】GPL-3.0 §5d 未履行：UI 无「适当法律声明」入口

GPL-3.0 §5d 要求交互式程序显示 **Appropriate Legal Notices**（版权声明 + 无担保声明 + 许可查看方式）。

**事实**：`grep -rn "AboutWindow\|关于" src/FfmpegGui/*.cs src/FfmpegGui/*.xaml` → **无命中**。仅有 4 个窗口（MetadataEditor / FormatFilterWindow / ImageDetailWindow / PresetManagerWindow），无 About。

**旁证**：FFmpeg 官方合规清单第 10 条同样要求 `Mention "This software uses libraries from the FFmpeg project under the LGPLv2.1" in your program "about box"`。

**修法**：新增「关于」窗口，显示：本项目版权与 GPL-3.0 全文入口、无担保声明、第三方组件清单及其许可。

### 1.9 【P1】随包 BSD/LGPL 二进制未附其许可与版权声明

`publish/PLAN/artifacts/`（JxrEncApp/JxrDecApp/avifenc/dngtool/ultrahdr_app）与 `publish/PLAN/ffmpeg-full/` 内**均无 LICENSE**。BSD-2/BSD-3 与 LGPL-2.1 都要求**二进制分发时复现版权声明与许可全文**。

> ⚠️ **更正**：原列出的 `libjpeg-9__.dll` 经实测为**孤儿文件**（无任何 exe/dll 引用，且自身依赖的 `libgcc_s_dw2-1.dll` 包内不存在 ⇒ 无法加载），**应直接删除**，其 IJG 许可义务随之消失。详见 `LICENSE_TERMS_VERIFICATION.md` §2.3。

### 1.10 【P2】依赖清单与实际不符

- `LICENSE:665` 列出的 `System.Drawing.Common` 在 `src/FfmpegGui/obj/project.assets.json` 的解析图中**不存在**（grep 计数 = 0）。
- 实际依赖图（实测）为：Avalonia 12.0.4 系列、Avalonia.Angle.Windows.Natives、Avalonia.BuildServices 11.3.2、HarfBuzzSharp 8.3.1.3(+NativeAssets)、MicroCom.Runtime 0.11.4、SkiaSharp 3.119.4(+NativeAssets)、Tmds.DBus.Protocol 0.92.0、Microsoft.NET.ILLink.Tasks 11.0.0-rc.1。其中 Angle/BuildServices/ILLink 未在 LICENSE 中列出。
- **正面结论**：全部依赖为 **MIT/BSD 系，与 GPL-3.0 兼容**；`csproj` 仅 3 个 `PackageReference`，未发现 LGPL/商业/自定义许可的 NuGet 包。

### 1.11 已排除的疑点（正面结论）

| 疑点 | 结论 |
|---|---|
| `NVIDIA Corporation/` 是否含专有 SDK | ❌ **空目录**（0 文件，仅空子目录 `umdlogs/`），无 NVENC/Video Codec SDK 证据 |
| `tools/` 第三方源码是否随仓库公开 | ✅ **不随 git 分发**——`.gitignore:84-95` 排除了 aom/libjxl/jxrlib/jpegli/libavif/libultrahdr/zlib/dng_sdk/libraw 等；`git ls-files` 仅 **90** 个文件，`tools/` 下只跟踪 6 个（`BUILD_TOOLS.md`、`build_tools.ps1`、`tools/src/{Directory.Build.props,dngjxlprobe,dngtool,pngcicp}`） |
| 第三方源码是否自带许可 | ✅ 基本齐全（aom/jxrlib/libjxl/libavif/jpegli/zlib/dcraw/libraw/libultrahdr 各有 LICENSE）；**唯一缺失：`dng_sdk`** |

---

## 2. 分发会碰到什么问题

### 2.1 阻断项（P0）

| # | 问题 | 证据 | 后果 |
|---|---|---|---|
| **D-1** | 随包 ffmpeg.exe 为 `--enable-nonfree` 构建 | `ffmpeg.exe -version` 的 configuration 行；FFmpeg 官方合规清单第 1 条 | **分发行为本身违法**；ExifTool/FFmpeg 版权方可发侵权通知 |
| **D-2** | 发行包内无任何许可证文件 | `find publish -maxdepth 3 -iname "LICENSE*"` 空；`pack.ps1` 无复制逻辑 | 违反 GPL §4/§5/§6 |
| **D-3** | ExifTool / FFmpeg 被捆绑但声明为「NOT bundled」 | `LICENSE:685-701` vs `publish/PLAN/{exiftool,ffmpeg-full}/`（438 个 .pm、15 个 dll） | 虚假声明，削弱抗辩 |
| **D-4** | 完整包**无法被第三方复现** | `pack.ps1:18` 的 `$PlanSource = publish\PLAN`；`.gitignore:16` 忽略 `publish/`；无下载清单/版本锁定/校验和 | 任何人 clone 后跑 `-Mode full` 只得空壳包（`pack.ps1:134` 仅打印警告）；外部工具来源不可审计 |
| **D-5** | **VC++ 运行库未声明未捆绑** | `dngtool.exe`/`avifenc.exe` 导入 `MSVCP140`/`VCRUNTIME140`；`publish/PLAN/artifacts/` 内无对应 dll；而「使用说明.txt」写「**无需安装任何运行环境**」 | 干净 Windows 上 dngtool（RAW/DNG 核心）与 avifenc 直接启动失败；属宣传与实现不一致 |
| **D-6** | `publish/settings.json` 泄露作者绝对路径 | `publish/settings.json:2` → `"FfmpegDirectory": "<repo-root>\\publish\\PLAN\\ffmpeg-full"` | 用户按指引解压后 ffmpeg 显示 ❌；暴露作者目录结构 |

### 2.2 高风险项（P1）

| # | 问题 | 证据 | 后果 |
|---|---|---|---|
| D-7 | 仓库地址三处矛盾，clone 第一步即失败 | `README.md:67/463` = `PLAN-1.git`；`:68/464` = `cd PLAN-1/ffmpegPictureUI`；`:71/467` Releases = `ffmpegPictureUI`；`git remote` = `ffmpegPictureUI.git`；`PACKAGING_SPEC.md:256` 与随包「使用说明.txt」= `PLAN-1` | 新用户无法按文档开始 |
| D-8 | 发行包含构建期产物 | `publish/PLAN/jxl/lib/*.lib`（`jxl.lib` 15.6 MB 等 **9 个 .lib**）+ **8 个 .pc** + 3 个 .cmake | 完整包无谓膨胀 20 MB+，把源码级构建元数据发给终端用户 |
| D-9 | 重复载荷 | `publish/PLAN/artifacts.7z`(4.1 MB) 经 `7z l` 验证与 `artifacts/` 内容相同；`ffmpeg-full-2026.9.2-setup.exe`(27 MB) 滞留 | 双份体积浪费 |
| D-10 | 单文件版/完整版混装于同一 `publish/` 根 | `publish/` 根同时有 `FfmpegGui.exe`+3 个原生 dll+`Resources/` 与 `PLAN/` | 若直接打包该目录将产出四不像产物 |
| D-11 | `PlanFolderPath` 逐级向上查找 PLAN | `PlatformServices.cs:204-238` | 解压位置决定行为，可能静默拾取祖先目录中他人放置的无关 `PLAN` → 工具来源不可信 |
| D-12 | `.git` 达 249 MB | `git count-objects -vH` → `size-pack: 190.45 MiB`；历史含 70.5 MB 的 `avcodec-63.dll`、≥4 个 23.1 MB 的 `FfmpegGui.exe` | clone 需下载约 190–250 MB，远超 90 个跟踪文件的表象 |
| D-13 | 缺工具时无引导，仅状态栏 ❌ + 执行期异常 | `ExternalToolsDetector.cs:424,448`；`RawColorPipeline.cs:48,569` 抛 `InvalidOperationException` | 新用户不知"为何点转换没反应"；单文件版开箱不可用 |
| D-14 | `../LICENSE` 断链（双语同错） | `README.md:394`、`README.md:788`（README 在仓库根，`../LICENSE` 指向仓库外） | 合规声明无法一键查证 |

### 2.3 改进项（P2）

| # | 问题 | 证据 |
|---|---|---|
| D-15 | 版本号三处漂移 | `pack.ps1:7` 默认 `1.5.2`；`FfmpegGui.csproj:18` `1.6.0`；`publish/PLAN/使用说明.txt` 写 `v1.5.5` |
| D-16 | `PACKAGING_SPEC.md` 结构图与实物不符 | `:45` 的 `FfmpegGui.dll`、`:74` 的 `FFmpegPictureUI.runtimeconfig.json`、`:70-73` 的 `libgcc_s_seh-1.dll`/`libstdc++-6.dll` 在 AOT 产物中**均不存在** |
| D-17 | `pack.ps1` 无 7z 时**静默不压缩**；使用说明模板缺失时 `[3/4]` 静默跳过 | `pack.ps1:186-189`（无 else）、`:139`（无 else） |
| D-18 | README 前提条件自相矛盾 | `:62` 要求「.NET 11 运行时」 vs `:306`「NativeAOT 编译，无需 .NET Runtime」 |
| D-19 | 无 CI、无 git tag | `.github/` 不存在；`git tag` 最高 `1.5.5`，缺 `1.5.2`/`1.5.6`/`1.6.0` |

### 2.4 已核实的正面结论

- ✅ **GUI 本体确为 NativeAOT，免 .NET 运行时属实**：`publish/FfmpegGui.exe` 含 NativeAOT 标记（命中 `RhNewObject`/`RhpNewFast`/`System.Private.CoreLib`），无 `hostfxr`/`DOTNET_BUNDLE` 字符串。
- ✅ **`publish/` 内无 `.pdb`**（`csproj` 已设 `DebugType=none`）。
- ✅ **无测试残留**：无 `.log`/`.cache`/`tests/output` 类文件。
- ✅ **无超大 git 跟踪二进制**：最大跟踪文件仅 196 KB（`docs/TESTING.md`）。

---

## 3. 「是否可用」——分场景判定

| 场景 | 可用性 | 说明 |
|---|---|---|
| **个人/内部自用** | ✅ 可用 | 不分发则不触发任何许可义务；现有 LICENSE 声明瑕疵不构成实际风险 |
| **仅开源源码（GPL-3.0）** | ⚠️ 修后可 | 必须修：LICENSE 逐字原文、删除失实声明、删除「不受 GPL 约束」表述、统一仓库地址、补源文件 SPDX 头 |
| **分发单文件版（不含 PLAN）** | ⚠️ 修后可 | 需补：随包 LICENSE + THIRD-PARTY-NOTICES、About 窗口（§5d）、VC++ 运行库声明、剥离 `settings.json`。**若用户自备 ffmpeg，则 D-1 不适用** |
| **分发完整版（含 PLAN）** | ❌ **不可用** | D-1（nonfree ffmpeg）+ D-2（无 LICENSE）+ D-3（虚假声明）+ D-5（VC++ 缺失）+ D-6（路径泄露）叠加 |
| **商业 / 闭源分发** | ❌ 不可行 | GPL-3.0 强传染；且随包 ffmpeg 为 GPLv3 构建，FFmpeg 官方明确"不存在其他许可条款，付费也不行" |
| **上架应用商店** | ❌ 不可行 | 多数商店（含 Microsoft Store 对 GPL 应用的附加条款、Apple App Store）与 GPL-3.0 存在冲突；且 nonfree ffmpeg 阻断 |

**判定要点**：能否分发**取决于是否捆绑 ffmpeg**，而不取决于你的代码写得多干净。这是本次审计最重要的工程含义。

---

## 4. Markdown 文档完善性

### 4.1 【P0】标准开源工程文件 8 类全缺

| 文件 | 状态 | 文件 | 状态 |
|---|---|---|---|
| `CHANGELOG.md` | ❌ 缺失 | `NOTICE` / `THIRD_PARTY_LICENSES` | ❌ 缺失 |
| `CONTRIBUTING.md` | ❌ 缺失 | `AUTHORS` / `CONTRIBUTORS` | ❌ 缺失 |
| `CODE_OF_CONDUCT.md` | ❌ 缺失 | `docs/README.md`（索引） | ❌ 缺失 |
| `SECURITY.md` | ❌ 缺失 | `.github/`（CI / Issue / PR 模板） | ❌ 缺失 |
| `LICENSE` | ✅ 存在 | | |

**特别指出**：仓库内已有 **49 条**门禁脚本（`tests/scripts/_run-step3-gates.ps1:752` `$expectedScriptCount = 49`），却**未接线到任何 CI**（`.github/` 不存在）。这是"有资产未变现"——投入最大的部分反而没有对外可见的质量信号。

### 4.2 README 的结构性问题

- **体量**：`README.md` 53 KB。多数渲染器首屏与摘要抽取预算在 10–20 KB 量级。
- **双语重复率**：中文块 `:6-400`（394 行）+ 英文块 `:402-794`（392 行）= 786 行 / 全文 830 行 = **94.7%**。
- **更新日志占比**：`:187-391` + `:583-785` = 406 行，占 README **48.9%**，且是**同一份日志的两遍**。更新日志内嵌 README 的代价：① 每次发版需双语两处同步（§5.2 的 13/12 不对等即实证）；② GitHub Releases 无法自动引用；③ 每次改版制造巨大 diff 噪声。
- **无文档索引**：全篇仅 `:181`/`:577` 各一处提到 `docs/TESTING.md`。`docs/` 下另有 9 个 md（+archive 5 个），README 的「项目结构」章节**未列出任何一个**，用户无法发现 `docs/DNG_BUILD.md` 这类真正面向用户的文档。
- **`<details>` 折叠副作用**：`:352`/`:380`/`:748`/`:776` 在 GitHub 可展开，但 v1.4.5 及更早的**全部版本历史**都在折叠内 ⇒ 在 npm/PyPI/部分 IDE 预览与纯文本抽取中丢失，Release Notes 无法引用。

### 4.3 公开信息中的内部内容

**先纠正一个可能的高估**：`git ls-files '*.md'` 仅 **5** 个——`PACKAGING_SPEC.md`、`README.md`、`docs/DNG_BUILD.md`、`docs/TESTING.md`、`tools/BUILD_TOOLS.md`。`docs/` 下的 `HANDOVER.md`(233 KB)、`MANUAL_VERIFICATION.md`、`COLOR_ENGINE_INTERFACE_PLAN.md`、`EXHAUSTIVE_TEST_PLAN.md`、`GAINMAP_HEIC_AVIF_ANALYSIS.md`、`RAW_PIPELINE_AUDIT.md`、`TOOLCHAIN_UPGRADE_PLAN.md`、`archive/` **全部未跟踪**（`git status --porcelain docs/` 显示 `??`）。**即这些内部记录尚未公开，实际泄露风险低于表面。**

**但已跟踪的 README 中确有内部信息**：

| 行号 | 内容 | 问题 |
|---|---|---|
| `:67-68` / `:463-464` | `git clone .../PLAN-1.git` + `cd PLAN-1/ffmpegPictureUI` | 仓库名错误（见 D-7） |
| `:229` / `:626` | `tests/GainMapTestHost/PLAN/`（该目录实际存在） | 暴露内部测试目录布局 |
| `:271` | 「视觉模型（**SenseNova 6.8 Flash Lite**）交叉验证」 | 暴露内部工具链与第三方模型名 |
| `:197` / `:593` | 内部脚本名 `_probe-i18n-scan.ps1` | 内部实现细节 |
| `:211` / `:608` | 内部门禁数字 | 见 §6.4 |
| `:127` / `:523` | `FFMPEGGUI_PLAN_DIR`；「PLAN 便携包」 | **`PLAN` 是内部代号，对外零解释**，用户无法理解 |

---

## 5. 双语结构正确性

### 5.1 【P0】切换机制在 GitHub/Gitee/npm 下失效，失效模式是「两版同时显示」

**实现方式**：`README.md:1-4` 是两个 `<input type="radio" class="lang-switch">` + 两个 `<label>`；`:6` 与 `:402` 分别是 `<div class="zh">` / `<div class="en">`；`:796-830` 是唯一的 `<style>` 块。

**结构正确性核对（这部分是合格的）**：`~` 兄弟选择器要求 input/label/div 同级。`:1-4`（后接空行 `:5`）按 CommonMark 合为一个 HTML block，`:6`、`:402` 为 type-6 block，四者均为容器顶层兄弟节点 ⇒ **嵌套关系满足要求**。

**但关键缺陷**：三条决定行为的规则——`.lang-switch{display:none}`(`:798`)、`div.en{display:none}`(`:825`)、`#tab-en:checked ~ div.zh/.en`(`:828-829`)——**全部只定义在这个 `<style>` 内，页面其余部分无任何 inline 兜底**。

**推理**（严格按 CSS 语义）：
- GitHub / Gitee / npm 等渲染器**剥离 `<style>`** ⇒ 上述四条规则**同时消失**。
- `:825` 消失 ⇒ `div.en` 恢复默认 `display:block` ⇒ **`:402-794` 的英文全文与 `:6-400` 的中文全文一并渲染**。
- `:828/829` 消失 ⇒ 点击 label 无任何效果。
- **结果不是"都隐藏"，而是"都显示"**：用户看到约 786 行重复正文、**两个 `<h1>`**（`:8` 与 `:404`，锚点同名冲突）、两份 11 个版本的更新日志。
- 另一种情形（渲染器保留 `<style>` 但剥离 `<input>`，如 GitHub 仅对任务列表保留 `input[type=checkbox][disabled]`）：此时 `:825` 生效 ⇒ 只显示中文，而两个 label 变成**死按钮**，英文内容对用户**完全不可达**。

**⚠️ 置信度说明**：本审计为离线只读环境，无法实际启动 GitHub/Gitee 渲染管线。上述结论基于「`<style>` 不在 GitHub HTML 白名单内」这一平台既定行为。**建议以一次真实 push 预览复核。**

**修法（推荐方案①）**：
1. **`README.md`（中文，默认）+ `README.en.md`（英文），顶部一行互链。** GitHub 会自动显示语言入口，零 CSS 依赖，搜索/发布页均可用。
2. 若坚持单文件：改用 `<details><summary>`（本文件 `:352`/`:380`/`:748`/`:776` 已验证 GitHub 支持折叠）。代价是无法"切换"只能"折叠"。
3. **当前方案不可通过"加兜底"救活**——兜底只能写在 `<style>` 外，而 `<style>` 正是被剥离的对象。建议直接弃用。

### 5.2 内容对等性：大体对等，3 处不对等

| 核对项 | 中文 | 英文 | 结论 |
|---|---|---|---|
| 章节标题数（`^##`） | 26（`:6-400`） | 26（`:402-794`） | ✅ 对等 |
| 表格行数（`^\|`） | 48 | 48 | ✅ 对等 |
| 核心功能表 | 23（`:16-43`） | 23（`:412-439`） | ✅ 对等 |
| 外部工具依赖表 | 8（`:44-56`） | 8（`:440-452`） | ✅ 对等 |
| 更新日志版本数 | 11（`:187-391`） | 11（`:583-785`） | ✅ 对等 |
| 项目结构代码块 | `:158-184` | `:554-580` | ✅ 结构逐行一致，仅 9 处描述性尾注翻译 |
| 数字类（359ms→17.6ms、20×、~90 字段、1-128 并发、10 个根因） | — | — | ✅ 中英一致 |

**不对等处**：

1. **v1.5.3 条目数 zh 13 条 vs en 12 条**（zh `:273-293` / en `:670-688`）。差异在 zh `:293` 单独列出「已集成 GainMapDecoder（SrgbToLinearRgba）」，en `:688` 将其合并进上一句。**语义等价但结构不对等**，双语 diff 会误报。
2. **许可章节信息量不对等**：en `:788` 明确列举第三方许可（Avalonia MIT、SkiaSharp MIT、FFmpeg LGPL/GPL、libjxl BSD 3-Clause、ExifTool GPL 等），zh `:394` 仅笼统写「含全部依赖的第三方许可证声明」——**中文用户看不到清单**。
3. **`../LICENSE` 双语同错**（`:394` / `:788`）：README 位于仓库根，`../LICENSE` 指向仓库**外**，法务关键链接失效。

**法律语义强度核对**：强传染性表述两边**强度对等**（zh `:398` / en `:792` 均含 `you must also make the source code available under GPL 3.0`），**不存在中文弱化英文的陷阱**——这一点是合格的。

---

## 6. 更新日志合理性与合规性

### 6.1 日期与版本号：自洽 ✅

| 版本 | 日期 | 版本 | 日期 |
|---|---|---|---|
| v1.6.0 | 2026-09-16 | v1.5.1 | 2026-07-27 |
| v1.5.6 | 2026-09-06 | v1.5.0 | 2026-07-23 |
| v1.5.5 | 2026-08-30 | v1.5.0 BETA3 | 2026-07-15 |
| v1.5.4 | 2026-08-16 | v1.5.0 BETA2 | 2026-07-14 |
| v1.5.3 | 2026-08-15 | v1.5.0 BETA | 2026-07-14 |
| v1.5.2 | 2026-08-13 | | |

- 日期严格单调递减，**无缺失、无矛盾**。中英两版日期完全一致。
- **唯一瑕疵**：BETA2 与 BETA **同日**（`README.md:363` / `:370`），且 BETA2 排在 BETA 之前，无时间戳无法判定同日先后。
- **版本号 vs 代码：一致，无漂移** ✅ —— `src/FfmpegGui/FfmpegGui.csproj:18-20` 为 `<Version>1.6.0</Version>` / `AssemblyVersion 1.6.0.0` / `FileVersion 1.6.0.0`，与 README `:10` / `:406` 头部的「v1.6.0 — 2026-09-16」吻合。
- **但 git tag 脱节**：`git tag` 最高为 `1.5.5`，且缺 `1.5.2`。README 宣称 v1.6.0 已于 2026-09-16 发布，**仓库无对应 tag** ⇒ 无法从 tag 复现该版本源码。

### 6.2 【P1】SemVer 语义被系统性稀释

| 升级 | 声明类型 | 实际内容 | 判定 |
|---|---|---|---|
| v1.5.0→v1.5.1 | patch | `:311-319` 色彩管线修复 | ✅ 合规 |
| v1.5.1→v1.5.2 | patch | `:294-309` 含「编码增强」：JXL 无损重封装开关、光子噪声自动 ISO、**NativeAOT 打包** | ❌ **含新功能，应为 minor** |
| v1.5.2→v1.5.3 | patch | `:276` 新增 `PsnrCalculator.cs`、`:289` 新增 `SimdPixelOps.cs` + 目标域质量分析 | ❌ **明显新功能，应为 minor** |
| v1.5.3→v1.5.4 | patch | `:243-272` 共 18 条，含「布局重构」 | ⚠️ 边界，倾向 minor |
| v1.5.4→v1.5.5 | patch | `:233-242` 4 条纯管道修复 | ✅ 合规 |
| v1.5.5→v1.5.6 | patch | `:213-232`「格式色彩能力约束」= 新增能力模型 + 目录结构规范化 | ❌ 倾向 minor |
| v1.5.6→v1.6.0 | minor | `:189-212` 双语本地化 + UI 修复 + 异常兜底 | ✅ 合规 |

**结论**：v1.5.1 → v1.5.6 连续 6 个 patch 中，**3 个（v1.5.2 / v1.5.3 / v1.5.6）实质是 minor**。依赖方按 SemVer 假设「patch 无新功能」将落空。

### 6.3 【P1】破坏性/行为性变更未标注

| 变更 | 所在版本 | 问题 |
|---|---|---|
| `-vsync 0` → `-fps_mode passthrough` | **patch v1.5.6**（`:221` / `:618`） | CLI 参数行为变更，写在 patch 内 |
| `AnimationLoop` 默认值 `0 → -1` | v1.5.6（`:228`） | 改变静态图/动画判定默认行为 |
| 「ICC 系统重写」「CICP 始终开启」 | v1.5.0（`:320-354`） | 输出文件色彩元数据**默认行为变更**，旧用户产物 ICC 结构会变 |

**以上均无 `Breaking Change` / 迁移说明标注**，且更新日志**没有"升级须知"章节**。

### 6.4 【P1】验证声明数字已过期

**README 声明**（`:211` zh / `:608` en）：「探针 **17 mode** 全绿、脚本门禁 **16 个**全绿」。

**实测**：
- `tests/scripts/_run-step3-gates.ps1:3` 的默认 `$Probes` = `contract,wire,verdict,selftest,iccname,plan,curve,matrix,ootf,hlgwire,bt2446,decision,engine,runner,settings,procstreams,encoding,ipc,i18n,geometry` ⇒ **20 个 mode**。
- 同文件 `:752` `$expectedScriptCount = 49`，清单实数 **49 条**。

**即低估了 3 个 mode、33 条门禁**。且该脚本 `:775-777` 自带「基线漂移即 FAIL」机制，说明清单仍在增长 ⇒ **把这类数字写进历史 changelog 条目必然过期**。

**修法**：历史条目改为「当次发布时的门禁基线」，并注明「数量随开发增长，实时数以 `_run-step3-gates.ps1` 的 `$expectedScriptCount` 为准」；或发版时由脚本自动注入。

### 6.5 合规性小结

- ✅ 无「版本日期缺失」；日期无矛盾。
- ✅ 无对外许可承诺的表述错误（除 §1.3 的捆绑声明失实，属 LICENSE 问题）。
- ❌ 破坏性变更未标注，可能误导升级用户。
- ❌ 无 git tag 对应，Releases 与 changelog 无法对应。
- ⚠️ BETA/BETA2 同日无时间戳，未采用 SemVer 预发布号（`-beta.1`/`-beta.2`）。

---

## 7. 综合建议

### 7.1 修复顺序（按"是否阻断分发"排序）

#### 第一优先：让"分发"这件事合法（不做完不要发任何二进制）

1. **处理 ffmpeg（最高优先级，二选一）**
   - **推荐**：`PLAN/ffmpeg-full/` **移出发行包**，改为首次运行引导用户自备 ffmpeg（复用现有 `PlanFolderPath` 探测 + 一个引导弹窗）。一并解决 D-1、D-3、D-5 的一部分、D-8，并让包体缩小约 100 MB。
   - 备选：替换为 `--enable-gpl --enable-version3`（**无 `--enable-nonfree`、无 `--enable-libfdk-aac`**）的可再分发构建，并在发行页提供**对应源码**（GPLv3 §6 义务）。
2. **发行包加入许可证文件**：根目录放**逐字未修改**的 `LICENSE`（GPL-3.0 全文）+ 新建 `THIRD-PARTY-NOTICES.md`（收录 Avalonia/SkiaSharp/HarfBuzzSharp 的 MIT、libjxl 系 BSD-3、jxrlib BSD-2、ExifTool GPL-1.0+/Artistic、LibRaw LGPL-2.1、Adobe DNG SDK 条款、FFmpeg 构建配置与源码获取方式）。**并在 `pack.ps1` 加断言：缺失即 `throw`**（当前脚本无任何许可校验）。
3. **修正 LICENSE 文本**：恢复逐字原文（含 How to Apply 附录）；作者版权行移入 `NOTICE` 或源文件 SPDX 头；第三方声明移出到独立文件。
4. **删除 `LICENSE:640-641`** 的「not covered by the GPL 3.0」表述（GPL §7 further restriction 风险）。
5. **修正捆绑声明**：`LICENSE:685-701` 的「NOT bundled」标题与逐条描述改为与事实一致；若采纳建议 1，则改为「ffmpeg 不捆绑，由用户自备」并保留 ExifTool 等实际捆绑项的准确描述。
6. **新增「关于」窗口**（GPL §5d + FFmpeg 合规清单第 10 条）：显示项目版权、无担保声明、GPL 全文入口、第三方组件与许可。
7. **VC++ 运行库**：二选一——把 `msvcp140.dll`/`vcruntime140.dll`/`vcruntime140_1.dll` 放入 `PLAN/artifacts/`；或在 README 与「使用说明.txt」明确写「需 VC++ 2015–2022 x64 Redistributable」。**同时修正「无需安装任何运行环境」这句话。**
8. **向 Adobe 确认 DNG SDK 再分发条款**；若不允许，`dngtool.exe` 改为可选外置下载。同时评估 LibRaw 静态链接的 LGPL-2.1 §6 义务（提供可重链接的目标文件或改为动态链接）。

#### 第二优先：让"分发"这件事可复现、可信

9. **剥离 `publish/settings.json`**（含作者绝对路径）；`pack.ps1` 加显式排除规则（当前 `robocopy /E` 无排除项）。
10. **清理发行内容**：删 `artifacts.7z`（与 `artifacts/` 重复）、`ffmpeg-full-2026.9.2-setup.exe`、`jxl/lib/*.lib`、`*.pc`、`*.cmake`；严格按 `publish/build/<包名>` 组装，`publish/` 根只留输入源，**分离单文件版与完整版**。
11. **为 PLAN 建可复现来源**：独立发布仓库或 Release 附件 + `MANIFEST.txt`（工具名 + 版本 + 来源 URL + SHA256）；`pack.ps1` 增加 `-PlanSource` 参数与存在性**硬校验**（缺失即 `throw`，而非现在的"打印警告后继续"）。
12. **统一仓库地址**：全部改为 `github.com/luoye-cpu/ffmpegPictureUI`；`cd` 改为 `cd ffmpegPictureUI`；`PACKAGING_SPEC.md:256` 与「使用说明.txt」同步；在 `csproj` 加 `<RepositoryUrl>` 作为权威来源。
13. **`PlanFolderPath` 收敛**：发行版只保留「exe 同目录 `PLAN/`」与 `FFMPEGGUI_PLAN_DIR`；逐级向上查找用 `#if DEBUG` 或环境变量门控。
14. **补 git tag**：`v1.5.2`、`v1.5.6`、`v1.6.0`（统一 `v` 前缀）；发版流程固化为「打 tag → 从 CHANGELOG 生成 Release Notes」。
15. **清理 `.git` 历史**（249 MB）：`git gc --aggressive` 或 `git filter-repo` 移除 `avcodec-63.dll`(70.5 MB) 等历史大 blob。

#### 第三优先：文档与工程化

16. **README 双语改双文件**：`README.md`（中文默认）+ `README.en.md`，顶部互链；**删除 `<style>`/`<input>` 方案**（该方案在 GitHub 下不可救）。
17. **抽出 `CHANGELOG.md`**（英文为主），README 只保留最近 1–2 版 + 链接。README 可从 53 KB 降至约 15 KB。
18. **修断链**：`README.md:394`/`:788` 的 `../LICENSE` → `LICENSE`。补 zh 侧第三方许可清单（与 en `:788` 对齐）。消除 v1.5.3 的 13/12 条目不对等。
19. **补 `SECURITY.md`、`CONTRIBUTING.md`、`CODE_OF_CONDUCT.md`、`AUTHORS`、`docs/README.md` 索引**。
20. **接线 CI**：新建 `.github/workflows/ci.yml`，把已有 **49 条**门禁脚本接入 —— 这是投入产出比最高的一项（资产已存在，只差接线）。同时加一条「README 中 URL 与 `git remote get-url origin` 一致」的校验。
21. **版本策略**：在 README 加一段说明；把 v1.5.2/v1.5.3/v1.5.6 按内容重新归位为 minor（或明确声明"本项目 patch 可含新功能"，二者选一但需明示）。为破坏性变更加 `**Breaking**` / `**Behavior change**` 标签与「升级须知」。
22. **清理内部信息**：移除 `README.md:229`/`:626` 的内部路径、`:271` 的视觉模型名、`:197`/`:593` 的内部脚本名；把 `PLAN` 重命名为对外可解释的名称（如 `portable-tools`），或在 README 首次出现处给出解释。
23. **补源文件 SPDX 头**：`// SPDX-License-Identifier: GPL-3.0-only` + 版权行。
24. **移除仓库根的非产品内容**：`NVIDIA Corporation/`（空目录）、`_guishot.ps1`。
25. **商标注意**：`FFmpeg` 是 Fabrice Bellard 的商标。产品名 `FfmpegPictureUI` 与「FFmpeg 图片转换器」属描述性使用，通常可接受，但建议在 README 加一句「FFmpeg 为 Fabrice Bellard 的商标，本项目与 FFmpeg 项目无隶属关系」以规避风险。**（需法务确认）**

### 7.2 最小可行发布路径（如果只做 5 件事）

若目标是**尽快发一个合规的二进制版本**，按此顺序：

1. 把 `PLAN/ffmpeg-full/` 移出包，改引导用户自备 ffmpeg → 消除 D-1/D-3/D-5 的主因
2. 发行包放**逐字 GPL-3.0 全文** + `THIRD-PARTY-NOTICES.md`，`pack.ps1` 加存在性断言
3. 删 `publish/settings.json` + 删 `artifacts.7z` / `.lib` / `.pc` / `setup.exe`
4. 加「关于」窗口（GPL §5d）
5. README 改双文件 + 统一仓库地址 + 修 `../LICENSE` 断链

做完这 5 项，**单文件版（用户自备 ffmpeg）即可合规分发**；完整版还需完成建议 8（Adobe 条款确认）。

---

## 附录：证据索引

| 结论 | 复现命令 / 位置 |
|---|---|
| ffmpeg 构建参数 | `./publish/PLAN/ffmpeg-full/ffmpeg.exe -version` |
| FFmpeg 官方合规清单 | https://ffmpeg.org/legal.html （清单第 1、10 条） |
| ExifTool 捆绑 | `find publish -name "*.pm" \| wc -l` → 438；`publish/PLAN/exiftool/exiftool_files/lib/Image/ExifTool.pm` |
| publish 文件数 | `find publish -type f \| wc -l` → 598 |
| 发行包无 LICENSE | `find publish -maxdepth 3 -iname "LICENSE*"` → 空 |
| 路径泄露 | `publish/settings.json:2` |
| 版本一致性 | `src/FfmpegGui/FfmpegGui.csproj:18-21` |
| 门禁实数 | `tests/scripts/_run-step3-gates.ps1:3`（20 mode）、`:752`（49 条） |
| 无 About 窗口 | `grep -rn "AboutWindow\|关于" src/FfmpegGui/*.cs src/FfmpegGui/*.xaml` → 空 |
| 版权人 | `git log --format="%an <%ae>" \| sort -u` |
| 跟踪文件数 | `git ls-files \| wc -l` → 90 |
| 仓库体积 | `git count-objects -vH` → size-pack 190.45 MiB |
| 双语 CSS 载体 | `README.md:796-830`（唯一 `<style>`） |
| 断链 | `README.md:394`、`README.md:788` |
| 仓库名矛盾 | `README.md:67` vs `README.md:71` vs `git remote -v` |
