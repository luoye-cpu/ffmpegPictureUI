# 许可证条款穷尽核验报告

**核验对象**：FfmpegPictureUI v1.6.0 发行包内**每一个**随附组件
**核验日期**：2026-09-20
**核验方式**：一手原文（官方下载包 / 组件自带 LICENSE / 官方网页条款）+ 二进制实测（PE 导入表、依赖遍历、架构判定）
**可信度分级**：**一级** = 官方原文实测（下载/读取原始文件）｜**二级** = 官方网页条款｜**三级** = 推断（明确标注）

> 本报告是对 `docs/DISTRIBUTION_COMPLIANCE_AUDIT.md` 的**条款层深化与更正**。凡与上一版冲突，以本报告为准。

---

## 1. 逐组件条款核验

### 1.1 表格说明
「分发义务」一栏只列**以二进制形式分发时**必须做的事。

| # | 组件（发行包内位置） | 许可（核验级别） | 分发义务 | 与 GPL-3.0 兼容 |
|---|---|---|---|---|
| 1 | `FfmpegGui.exe`（自有 NativeAOT） | **GPL-3.0-only**（一级：`FfmpegGui.csproj:21`） | 随附 GPL-3.0 全文 + 提供对应源码 | — |
| 2 | `libSkiaSharp.dll`、`libHarfBuzzSharp.dll`、`av_libglesv2.dll` | **MIT**（一级：`project.assets.json`） | 随附版权声明 + 许可全文 | ✅ |
| 3 | `PLAN/jxl/bin/` 22 个 exe（cjxl/djxl/cjpegli/djpegli/jxlinfo…） | **BSD-3**（libjxl）+ 传递依赖（一级：`publish/PLAN/jxl/bin/LICENSE.*` 共 10 份） | 随附版权 + 许可全文 | ✅ |
| 3a | ↳ `LICENSE.brotli` | MIT（一级） | 同上 | ✅ |
| 3b | ↳ `LICENSE.giflib` | MIT（一级） | 同上 | ✅ |
| 3c | ↳ `LICENSE.highway` | **Apache-2.0**（一级） | 同上 + 保留 NOTICE | ✅ |
| 3d | ↳ `LICENSE.libjpeg-turbo` | BSD-3 + IJG（一级） | 同上 | ✅ |
| 3e | ↳ `LICENSE.libpng` | libpng/zlib 式（一级） | 同上 | ✅ |
| 3f | ↳ `LICENSE.libwebp` | BSD-3（一级） | 同上 | ✅ |
| 3g | ↳ `LICENSE.sjpeg` | **Apache-2.0**（一级） | 同上 + 保留 NOTICE | ✅ |
| 3h | ↳ `LICENSE.skcms` | BSD-3（一级） | 同上 | ✅ |
| 3i | ↳ `LICENSE.zlib` | zlib（一级） | 同上 | ✅ |
| 4 | `PLAN/exiftool/`（exiftool.exe + perl.exe + **438 个 .pm**） | **"same terms as Perl" = Artistic-1.0-Perl 或 GPL-1.0-or-later**（一级：`lib/Image/ExifTool.pm:11-13` 原文） | 随附许可 + 版权。**必须选 GPL-1.0+ 分支**（见 §1.3） | ✅（走 GPL 分支） |
| 5 | `PLAN/artifacts/dngtool.exe`（自有） | **LibRaw: LGPL-2.1 或 CDDL-1.0**（一级：`tools/src/libraw/COPYRIGHT` 原文 "one of two licenses as you choose"）**+ Adobe DNG SDK 许可**（一级：官方 `LICENSE.txt`）**+ zlib**（一级） | 见 §1.2、§1.4 | ✅ |
| 6 | `PLAN/artifacts/avifenc.exe` | **libavif BSD-2**（一级：`tools/src/libavif/LICENSE`）+ **aom BSD-2**（一级：`tools/src/aom/LICENSE`） | 随附版权 + 许可全文 | ✅ |
| 7 | `PLAN/artifacts/JxrEncApp.exe`、`JxrDecApp.exe` | **BSD-2-Clause（Microsoft）**（一级：`tools/src/jxrlib/LICENSE`） | 随附版权 + 许可全文 | ✅ |
| 8 | `PLAN/artifacts/ultrahdr_app.exe` | **MIT 与 Apache-2.0 双许可**（一级：`tools/src/libultrahdr/LICENSE` 原文 "both the MIT license and the Apache License"） | 任选其一，随附版权 + 许可 | ✅ |
| 9 | `PLAN/artifacts/libjpeg-9__.dll` | IJG（三级：未核验原文，因**该文件应删除**，见 §2.3） | **无** —— 建议删除 | — |
| 10 | `PLAN/ffmpeg-full/`（ffmpeg/ffprobe/ffplay + 15 dll） | **`nonfree and unredistributable`**（一级：FFmpeg `configure:4831-4832`） | **不可分发** | ❌ |

### 1.2 Adobe DNG SDK —— 已取得官方原文，**允许分发**

**原文取得**：`https://www.adobe.com/go/dng_sdk` → `dng_sdk_1_7_1_2724_20260908.zip`（80.5 MB，已下载）→ 包内 `LICENSE.txt`（5,283 B）。

**§1 许可授予（原文）**：
> `Adobe hereby grants you a non-exclusive, worldwide, royalty free license to use, reproduce, prepare derivative works from, publicly display, publicly perform, distribute and sublicense the Software for any purpose.`

**结论（一级，已从原文核实）**：
- ✅ 允许**静态链接进自有程序并分发该二进制**
- ✅ 允许再分发 SDK 本身及源代码（`distribute and sublicense`）
- ✅ **无**"仅限配合 Adobe 产品使用"的用途限制
- ✅ **无**禁止与 GPL 组合的条款
- ✅ **无需**注册或单独签署 —— `NOTICE TO USER` 明示：任何下载/使用/分发**即构成接受**
- ⚠️ **§2 是当前实际违反的一条**：
  > `You will not remove any copyright or other notice included in the Software or Documentation and you will include such notices in any copies of the Software that you distribute in human-readable format.`
  → 仓库内的 `tools/src/dng_sdk/dng_sdk_1_7_1/` **确无 `LICENSE.txt`**（已实测 `ls`），发行包内也没有 → **必须补**
- ⚠️ **§5**：以**商业产品**形式分发时，须承诺对 Adobe 作出赔偿抗辩（`defend, indemnify and hold harmless Adobe`）。免费开源分发不触发。
- ⚠️ **§6**：Adobe 与 DNG logo 为商标，不得用于背书或推广（除非另行协议）。

**与 GPL-3.0 的兼容性**：**兼容**。Adobe §2（保留法律声明）与 §6（商标权保留）恰好落在 GPL-3.0 §7 允许的附加条款 (b) 与 (e) 之内；§1 授予的分发/再许可权**宽于** GPL 所需，不构成 §10 禁止的"额外限制"。

**另一项独立义务 —— DNG 规范专利许可（二级：官方网页原文）**：
> `Any Compliant Implementation distributed under this license must include the following notice displayed in a prominent manner within its source code and documentation: "This product includes DNG technology under license by Adobe."`

⚠️ 该条款要求的是**这句确切的英文原文**，且须同时出现在**源代码与文档**中。`README.md:396` 目前只有中文意译「本产品包含 Adobe 授权的 DNG 技术」——**应补上确切英文句**。

### 1.3 ExifTool 的许可必须走 GPL 分支

**原文**（`publish/PLAN/exiftool/exiftool_files/lib/Image/ExifTool.pm:11-13`）：
> `Legal:  Copyright (c) 2003-2026, Phil Harvey (philharvey66 at gmail.com)`
> `        This library is free software; you can redistribute it and/or modify it under the same terms as Perl itself.`

Perl 5 的许可是 **Artistic-1.0-Perl 或 GPL-1.0-or-later** 双许可。关键点：
- **Artistic License 1.0 与 GPL 不兼容**（FSF 明确列为 GPL-incompatible）
- **GPL-1.0-or-later 与 GPL-3.0 兼容**（"or later" 允许选择 GPLv3）

⇒ 因此**必须声明按 GPL-1.0+ 分支使用**，否则会引入 GPL 不兼容组件。这一点在 `THIRD-PARTY-NOTICES.md` 里要写明。

> 附注：随包 `publish/PLAN/exiftool/exiftool_files/LICENSE` 实际是 **GPL-3.0 全文**（不是 GPL-1.0），可视为 Perl 许可的 GPL 分支实例化，不构成冲突。

### 1.4 LibRaw —— LGPL-2.1 的处置

**原文**（`tools/src/libraw/COPYRIGHT`）：
> `LibRaw is free software; you can redistribute it and/or modify it under the terms of the one of two licenses as you choose: 1. GNU LESSER GENERAL PUBLIC LICENSE version 2.1 ... 2. COMMON DEVELOPMENT AND DISTRIBUTION LICENSE (CDDL) Version 1.0`

- **"or" 关系**（二选一，非 and）
- **CDDL-1.0 与 GPL 不兼容**，故只能走 LGPL-2.1 分支
- **LGPL-2.1 §3 允许升级**：`(If a newer version than version 2 of the ordinary GNU General Public License has appeared, then you can specify that version instead if you wish.)` → 可指定 **GPL-2.0-or-later** → 进而 **GPL-3.0**。该链条成立。
- **一旦转为 GPL-3.0，LGPL 的全部义务（提供可重链接目标文件）消失** ⇒ 这是**最省事路径**，零代码改动。

**关键补充事实**：`tools/src/libraw/CMakeLists.txt:119` 为 `target_link_libraries(libraw_static PUBLIC dng_sdk zlib_static)`，且 `:116` 定义了 `USE_DNGSDK` ⇒ **该 LibRaw 静态库内部已混入 Adobe DNG SDK 代码**，故不能只按 LGPL 处理，Adobe 的通知义务必须一并履行。

⚠️ **发现一处事实错误**：`tools/src/libraw/CMakeLists.txt:3` 的注释写 `DNG SDK 1.7.1 (Adobe, MIT)` —— **与官方 `LICENSE.txt` 不符**（不是 MIT，是 Adobe DNG SDK License Agreement）。应更正。

---

## 2. 二进制实测（本次新增，均为一级证据）

### 2.1 运行时依赖 —— 精确到具体 DLL

用 PE 导入名解析实测（`publish/PLAN/artifacts/*`）：

| 文件 | 架构 | 非系统依赖 | 结论 |
|---|---|---|---|
| `dngtool.exe` | x64 | `MSVCP140.dll`、`VCRUNTIME140.dll`、`VCRUNTIME140_1.dll`、`WS2_32.dll` | **必须 VC++ 2015-2022 x64 Redist** |
| `avifenc.exe` | x64 | `VCRUNTIME140.dll` | **必须 VC++ Redist** |
| `JxrEncApp.exe` / `JxrDecApp.exe` | x64 | `VCRUNTIME140.dll` | **必须 VC++ Redist** |
| `ultrahdr_app.exe` | x64 | 仅 `KERNEL32.dll` | ✅ 自包含 |
| `libjpeg-9__.dll` | x64 | `libgcc_s_dw2-1.dll`（**包内不存在**） | ❌ 无法加载 |
| `FfmpegGui.exe` | x64 | 无（NativeAOT） | ✅ 自包含 |

⚠️ 因此「使用说明.txt」里的「**无需安装任何运行环境**」是**错误陈述**：`dngtool`（RAW/DNG 核心功能）、`avifenc`、`JxrEncApp/JxrDecApp` 在未装 VC++ Redist 的干净 Windows 上会直接启动失败。

### 2.2 `artifacts.7z` 不只是重复，是**过期**重复

`7z l artifacts.7z` 实测清单：

```
2026-08-13  D.... artifacts
2026-05-27  ....A  12408832  artifacts\avifenc.exe
2026-08-15  ....A   8030208  artifacts\dngtool.exe
2026-08-11  ....A    310272  artifacts\JxrDecApp.exe
2026-08-11  ....A    312320  artifacts\JxrEncApp.exe
2025-05-30  ....A    355891  artifacts\libjpeg-9__.dll
5 files, 1 folders
```

而当前 `artifacts/` 有 **6** 个文件 —— 多出 `ultrahdr_app.exe`（2026-09-01 加入）。⇒ 该 7z 是 **2026-08-15 的旧快照**，用户若解压它会得到**缺少 Ultra HDR 能力的旧工具集**。比单纯重复更危险。

### 2.3 【新发现】`libjpeg-9__.dll` 是孤儿文件

两条独立证据：
1. **遍历 `publish/` 下全部 exe/dll，无任何文件引用 `libjpeg-9__`**（实测：零命中）。
2. **它自身依赖 `libgcc_s_dw2-1.dll`，而包内只有 `libgcc_s_seh-1.dll`**（后者属 Perl）⇒ **该 DLL 连加载都做不到**。

⇒ 约 348 KB 死重，且它**唯一**会带来的 IJG 许可义务也随之消失。**应删除**。

### 2.4 `jxl/lib` 下的编译期产物

实测数量：`.lib` **9 个**（`jxl.lib` 15.6 MB 等）、`.pc` **7 个**、`.cmake` **3 个**。

均为**编译期**元数据：`.lib` 是静态导入库、`.pc` 是 pkg-config 描述、`.cmake` 是 CMake 包配置。本应用通过**调用 `cjxl.exe` 子进程**工作，**不做链接** ⇒ 运行时零用途，白占约 20 MB。

---

## 3. 对你三个问题的正式回答

### 3.1 关于「发布 2 个版本」——方向正确，但有一处必须纠正

**你的方案**：带 ffmpeg 版走其他渠道手动分发；GitHub 只发不带 ffmpeg 版。

**评估**：**双版本策略本身是行业标准做法，采纳**。但有一个致命误解必须澄清：

> ⚠️ **「换成别的渠道分发」不能解决 `--enable-nonfree` 问题。**

依据（一级原文）：
- FFmpeg 自己的构建系统把该配置的许可字段写成：`license="nonfree and unredistributable"`（`configure:4831-4832`），且选项帮助文本为 `allow use of nonfree code, the resulting libs and binaries will be unredistributable`（`configure:101-102`）。
- GPL-3.0 §0 对 convey 的定义：`To "convey" a work means any kind of propagation that enables other parties to make or receive copies.`；对 propagate 的定义明确包含 `making available to the public`。

⇒ **网盘、QQ 群、U 盘、邮件、私链，全部构成 convey，全部是分发**。渠道不改变行为性质，没有任何渠道能豁免。

**正确的做法不是改渠道，而是换 ffmpeg 构建**。好消息是：**换掉之后"带 ffmpeg 版"完全可行**，而且因为你项目本身就是 GPL-3.0，捆绑 GPL 构建的 ffmpeg 是最省事的组合（许可同族，无冲突）。

**推荐构建**（一级：已核对 BtbN 的变体脚本原文）：

| 变体 | 实际 configure 参数 | 可否再分发 | 失去的能力 |
|---|---|---|---|
| `ffmpeg-master-latest-win64-lgpl-shared` | `--enable-version3 --disable-debug`（**无 `--enable-gpl`、无 `--enable-nonfree`**） | ✅ 可 | libx264/libx265 等 GPL-only 编码器 |
| `ffmpeg-master-latest-win64-gpl-shared` | `--enable-gpl --enable-version3 --disable-debug` | ✅ 可 | 无 |
| `win64-nonfree`（**切勿使用**） | 在 gpl 基础上加 `--enable-nonfree` | ❌ **不可** | — |

**建议选 `lgpl-shared`**：本应用的核心功能是图像转换 + 视频**解码**，输出为动图。`libx264`/`libx265` 是**编码器**，仅在输出 H.264/H.265 视频时才需要 —— 本应用不需要；而内置的 native H.264/H.265 **解码器不受 `--enable-gpl` 影响**，视频输入能力**无损失**。选 LGPL 可同时免掉「提供 ffmpeg 对应源码」这项最重的义务。
> ⚠️ 注意选 **`-shared`** 变体：静态 exe 会触发 LGPL-2.1 §6 的可重链接义务。

### 3.2 为什么是 GPL-3.0 —— 分两层，答案不同

**第一层：为什么必须随包附 GPL 全文？**
因为这是 GPL **授予你分发权的条件**，不是可选项。
- GPL-3.0 §4：`you must ... give all recipients a copy of this License along with the Program`
- §6 对以二进制形式分发同样要求提供 Corresponding Source
- 不附 = 你没有分发权 = 分发即侵权。
- 而且你的包里**确实存在 GPL 组件**：ExifTool（须走 GPL-1.0+ 分支，见 §1.3）；若采纳带 ffmpeg 版则还有 ffmpeg。⇒ **GPL 文本是必需的**。

**第二层：为什么"你的项目"选 GPL-3.0？—— 关键结论：没有任何一条法律硬性要求**

逐项排查：

| 依赖 | 是否逼你用 GPL |
|---|---|
| NuGet 全部依赖（Avalonia/SkiaSharp/HarfBuzzSharp…） | ❌ 全 MIT，允许闭源 |
| cjxl / avifenc / jxrlib / ultrahdr | ❌ 全 BSD / Apache / MIT，允许闭源 |
| ExifTool | ❌ 双许可，可选 Artistic 分支；且以**独立子进程**调用 |
| FFmpeg | ❌ 独立子进程 ⇒ 不传染（GPL-3.0 §5 aggregate 条款；FSF FAQ：`pipes, sockets and command-line arguments` 属于两个独立程序间的通信机制） |
| **dngtool 静态链接 LibRaw（LGPL-2.1）** | ✅ **唯一真正的推力** |

⇒ **GPL-3.0 是"当前最省事的选择"，而非"法律强制"**。你有两条路：

- **路线 A（推荐，零改动）**：保持 GPL-3.0。LibRaw 走 LGPL-2.1 §3 转为 GPL-3.0，LGPL 义务整体消失。
- **路线 B（若要改成 MIT/Apache-2.0）**：必须把 LibRaw 改为**动态链接 `libraw.dll`**，并在发行包内附 LGPL-2.1 全文 + 提供可重链接的目标文件或书面要约。

**顺带说明**：你**不需要为了"能分发"而改许可**。GPL-3.0 本身不阻碍分发 —— 它只要求你开源、附许可、提供源码。上一版报告说的"商业闭源不可行"仍然成立，但那是**你的选择**，不是缺陷。

### 3.3 为什么要删那几样东西 —— 逐项理由

| 对象 | 为什么删 | 证据 |
|---|---|---|
| `settings.json` | ① 泄露作者绝对路径 `<repo-root>\publish\PLAN\ffmpeg-full`；② 这是**运行时用户状态**不是程序文件，随包分发等于让每个用户开局继承你的设置；③ 该路径在用户机器上不存在 ⇒ ffmpeg 反而显示 ❌，与"开箱可用"自相矛盾 | `publish/settings.json:2` |
| `artifacts.7z` | **过期重复**：是 2026-08-15 快照，缺 `ultrahdr_app.exe`（2026-09-01 加入）。用户解压会得到**能力残缺的旧工具集** | §2.2 的 `7z l` 实测 |
| `jxl/lib/*.lib`（9 个，约 20 MB） | **编译期**静态导入库。应用调用 `cjxl.exe` 子进程、不做链接 ⇒ 运行时零用途 | §2.4 |
| `jxl/lib/pkgconfig/*.pc`（7 个）、`lib/cmake/**`（3 个） | **编译期** pkg-config / CMake 元数据，仅用于"开发时链接 libjxl"⇒ 终端用户零用途 | §2.4 |
| `ffmpeg-full-2026.9.2-setup.exe`（27 MB） | 第三方 ffmpeg **安装器**，即 ffmpeg 的**第二份拷贝**（同样带 nonfree 问题）；且不属于任何一个产品版本 | `ls -la publish/` |
| **`libjpeg-9__.dll`（本次新增发现）** | **孤儿文件**：无任何 exe/dll 引用它，且它自身依赖的 `libgcc_s_dw2-1.dll` 包内不存在 ⇒ 连加载都做不到。删掉同时免掉 IJG 许可义务 | §2.3 |

---

## 4. 双语 README 方案（GitHub 最常见做法）

**结论：弃用当前的 `<style>` + `<input type=radio>` 单文件方案，改为「双文件 + 顶部语言互链」。**

这是 GitHub 生态的事实标准，原因：
- GitHub **会剥离 Markdown 中的 `<style>`**，导致当前方案失效（详见审计报告 §5.1：失效模式是**两版同时渲染**，不是隐藏）
- 双文件零 CSS 依赖，GitHub 文件列表、搜索、发布页、各类镜像站均正常
- 双语可独立维护，根治「改了中文忘改英文」（v1.5.3 的 13/12 条目不对等即此症）

**文件命名（两种，均常见）**：

```
方案一（中文优先，适合你当前用户群）
  README.md           ← 中文（GitHub 默认展示）
  README.en.md        ← English

方案二（国际曝光优先）
  README.md           ← English（GitHub 默认展示）
  README.zh-CN.md     ← 简体中文
```

**每个文件顶部固定一行互链**：

```markdown
**简体中文** | [English](README.en.md)
```

或方案二：

```markdown
[简体中文](README.zh-CN.md) | **English**
```

要点：当前语言**加粗不加链接**，另一种语言加链接。这一行放在 `<h1>` 之后、正文之前。

**配套调整**（建议同时做，否则双语拆分收益减半）：
1. 把更新日志抽出为独立 `CHANGELOG.md`（README 现为 53 KB，其中更新日志占 48.9% 且是同一份的两遍）；README 只留最近 1–2 版 + 链接
2. 新增 `THIRD-PARTY-NOTICES.md`（承载 §1 表格里的全部许可与版权声明）
3. 删除 README 中的 `<style>` 块、两个 `<input>`、两个 `<label>`，以及 `<div class="zh">` / `<div class="en">` 包裹层
4. 修断链：`README.md:394` / `:788` 的 `../LICENSE` → `LICENSE`
5. 统一仓库地址：`README.md:67/68`、`:463/464` 的 `PLAN-1` → `ffmpegPictureUI`（`git remote` 实为 `ffmpegPictureUI.git`）

---

## 5. 对上一版报告的更正

| 上一版结论 | 更正后 | 依据 |
|---|---|---|
| Adobe DNG SDK「许可原文缺失，再分发条款无法核实」，列为 P1 风险 | ✅ **已取得官方原文：许可宽松，明确允许分发与再许可**。真正的问题是**缺 §2 通知义务**，属易修项，**不是阻断项** | §1.2，官方 `LICENSE.txt` 实测 |
| Adobe 与 GPL-3.0 兼容性「需人工确认」 | ✅ **兼容**（附加条款落在 GPL-3.0 §7 允许清单 (b)(e) 内） | §1.2 |
| `libjpeg-9__.dll` 作为需履行 IJG 通知义务的组件列出 | ❌ **该文件是孤儿且无法加载，应删除**，其许可义务随之消失 | §2.3 |
| 「VC++ 运行库未声明未捆绑」 | 精确化：`dngtool.exe` 需 `MSVCP140 + VCRUNTIME140 + VCRUNTIME140_1`；`avifenc`/`JxrEnc/JxrDec` 需 `VCRUNTIME140`；`ultrahdr_app` 自包含 | §2.1 |
| `artifacts.7z`「与 artifacts/ 重复」 | 更严重：是**过期**重复（缺 `ultrahdr_app.exe`） | §2.2 |
| LibRaw「静态链接可能违反 LGPL §6」 | 成立，但**有零成本解法**：走 LGPL-2.1 §3 升级为 GPL-3.0，义务整体消失 | §1.4 |

**上一版未变的核心结论**：随包 `ffmpeg.exe` 因 `--enable-nonfree` **不可再分发**（本次已取得 `configure` 原文，证据等级由"官方合规清单"升级为"FFmpeg 构建系统自身声明"）。

---

## 6. 待你决策的事项

| # | 决策点 | 选项 |
|---|---|---|
| 1 | ffmpeg 构建替换 | (a) `lgpl-shared`（义务最轻，推荐）｜(b) `gpl-shared`（全功能） |
| 2 | 项目许可是否维持 GPL-3.0 | (a) 维持（零改动，推荐）｜(b) 改 MIT/Apache-2.0（需把 LibRaw 改为动态链接） |
| 3 | 双语 README 默认语言 | (a) 中文优先 `README.md` + `README.en.md`（推荐）｜(b) 英文优先 `README.md` + `README.zh-CN.md` |
| 4 | 是否立即执行整改 | 按 §4 配套调整 + 生成 `THIRD-PARTY-NOTICES.md` |

---

## 附：本次核验的一手来源清单

| 组件 | 来源 | 取得方式 |
|---|---|---|
| Adobe DNG SDK | `https://www.adobe.com/go/dng_sdk` → `dng_sdk_1_7_1_2724_20260908.zip` | **已下载**（80.5 MB），读取包内 `LICENSE.txt` |
| DNG 规范专利许可 | `https://helpx.adobe.com/camera-raw/digital-negative.html` | 官方网页原文 |
| FFmpeg | `https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/configure` | 读取 `:101-102`、`:4831-4832` |
| FFmpeg 合规清单 | `https://ffmpeg.org/legal.html` | 官方网页原文 |
| libjxl 系 | `publish/PLAN/jxl/bin/LICENSE.*`（10 份） | 仓库内实际文件 |
| ExifTool | `publish/PLAN/exiftool/exiftool_files/lib/Image/ExifTool.pm:11-13` | 仓库内实际文件 |
| LibRaw | `tools/src/libraw/COPYRIGHT`、`LICENSE.LGPL`、`LICENSE.CDDL` | 仓库内实际文件 |
| aom / libavif / jxrlib / libultrahdr / jpegli / zlib / dcraw | `tools/src/*/LICENSE*` | 仓库内实际文件 |
| BtbN 构建变体 | `FFmpeg-Builds` 的 `variants/defaults-gpl.sh`、`defaults-lgpl.sh`、`win64-nonfree.sh` | 脚本原文 |
