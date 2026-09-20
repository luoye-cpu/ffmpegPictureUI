# 第三方组件许可声明 · Third-Party Notices

本文件列出 FfmpegPictureUI 分发物中包含或调用的全部第三方组件，以及各自的版权与许可。
This file lists every third-party component bundled with or invoked by FfmpegPictureUI, together with its copyright and license.

**本文件不构成对任何第三方许可的修改，也不对其施加额外限制。**
**Nothing in this file modifies, or adds any restriction to, any third-party license.**

完整许可原文位于本仓库的 [`licenses/`](licenses/) 目录，并在发行包内随附。
Full license texts are provided in the [`licenses/`](licenses/) directory of this repository and are bundled with the distribution.

---

## 1. 组件总表 · Component summary

### 1.1 随发行包分发 · Bundled with the distribution

| 组件 Component | 版本/位置 Version / Location | 许可 License | 版权 Copyright |
|---|---|---|---|
| **FfmpegPictureUI**（本项目） | `FfmpegGui.exe` | **GPL-3.0-only** | (C) 2025-2026 luoye-cpu |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.0.4 | MIT | (c) AvaloniaUI OÜ |
| Avalonia.Angle.Windows.Natives | 2.1.27548 | MIT | (c) AvaloniaUI OÜ |
| SkiaSharp (+ NativeAssets) | 3.119.4 | MIT | (c) 2015-2016 Xamarin, Inc. / 2017-2018 Microsoft |
| HarfBuzzSharp (+ NativeAssets) | 8.3.1.3 | MIT | (c) 2015-2016 Xamarin, Inc. / 2017-2018 Microsoft |
| MicroCom.Runtime | 0.11.4 | MIT | (c) .NET Foundation / AvaloniaUI OÜ |
| Tmds.DBus.Protocol | 0.92.0 | MIT | (c) Tom Deseyn |
| Google Skia（SkiaSharp 底层引擎） | — | BSD-3-Clause | (c) Google Inc. |
| **libjxl** — `cjxl` / `djxl` / `cjpegli` / `djpegli` / `jxlinfo` | `PLAN/jxl/bin/` | BSD-3-Clause | (c) the JPEG XL Project Authors |
| ↳ brotli | `PLAN/jxl/bin/` | MIT | (c) 2009-2016 the Brotli Authors |
| ↳ giflib | `PLAN/jxl/bin/` | MIT | (c) 1997 Eric S. Raymond |
| ↳ highway | `PLAN/jxl/bin/` | Apache-2.0 | (c) the Highway Authors |
| ↳ libjpeg-turbo | `PLAN/jxl/bin/` | BSD-3-Clause + IJG + zlib | (c) 2009-2024 libjpeg-turbo Project |
| ↳ libpng | `PLAN/jxl/bin/` | libpng License (zlib 式) | (c) 1995-2024 the PNG Reference Library Authors |
| ↳ libwebp | `PLAN/jxl/bin/` | BSD-3-Clause | (c) 2010 Google Inc. |
| ↳ sjpeg | `PLAN/jxl/bin/` | Apache-2.0 | (c) Google Inc. |
| ↳ skcms | `PLAN/jxl/bin/` | BSD-3-Clause | (c) 2018 Google Inc. |
| ↳ zlib | `PLAN/jxl/bin/` | zlib | (c) 1995-2022 Jean-loup Gailly and Mark Adler |
| **ExifTool** — `exiftool.exe` + Perl 模块 | 13.59，`PLAN/exiftool/` | **GPL-1.0-or-later 或 Artistic-1.0-Perl**（本项目按 **GPL-1.0+** 分支使用） | (c) 2003-2026 Phil Harvey |
| ↳ Strawberry Perl — `perl.exe`, `perl532.dll` | `PLAN/exiftool/` | Artistic-1.0-Perl 或 GPL-1.0-or-later | (c) the Perl 5 / Strawberry Perl authors |
| **dngtool**（本项目自研 CLI） | `PLAN/artifacts/` | **GPL-3.0-only** | (C) 2025-2026 luoye-cpu |
| ↳ LibRaw（dngtool 静态链接） | `PLAN/artifacts/` | **LGPL-2.1 或 CDDL-1.0**（按 LGPL-2.1 §3 升级为 GPL-3.0，见 §6） | (c) 2008-2025 LibRaw LLC |
| ↳ Adobe DNG SDK 1.7.1（dngtool 静态链接） | `PLAN/artifacts/` | **Adobe DNG SDK License Agreement**（见 §4） | (c) 2006-2019 Adobe |
| ↳ zlib（dngtool 静态链接） | `PLAN/artifacts/` | zlib | (c) 1995-2026 Jean-loup Gailly and Mark Adler |
| **libavif** — `avifenc.exe` | `PLAN/artifacts/` | BSD-2-Clause | (c) 2019 Joe Drago |
| ↳ AOM (libaom) | `PLAN/artifacts/` | BSD-2-Clause + AOM 专利授权 | (c) 2016 Alliance for Open Media |
| **jxrlib** — `JxrEncApp.exe`, `JxrDecApp.exe` | `PLAN/artifacts/` | BSD-2-Clause | (c) Microsoft Corp. |

### 1.2 仅构建期使用（不随发行包分发）· Build-time only (not distributed)

| 组件 Component | 许可 License |
|---|---|
| Avalonia.BuildServices 11.3.2 | MIT |
| Microsoft.NET.ILLink.Tasks 11.0.0-rc.1 | MIT |
| .NET Runtime / SDK（Microsoft） | MIT |

### 1.3 外部工具（不随本仓库分发）· External tools (not distributed here)

| 工具 Tool | 许可 License | 说明 Note |
|---|---|---|
| **FFmpeg / FFprobe** | LGPL-2.1-or-later 或 GPL-2.0-or-later / GPL-3.0，**取决于构建配置** | 以独立子进程调用。若随包分发，其构建配置必须不含 `--enable-nonfree`，否则该二进制不可再分发（FFmpeg `configure` 原文：`the resulting libs and binaries will be unredistributable`）。捆绑时必须随附其对应源码。 |
| FFmpeg 商标 | — | FFmpeg 是 Fabrice Bellard 的商标。本项目与 FFmpeg 项目无隶属关系。 |

---

## 2. MIT License

适用于 §1.1 中标注 MIT 的全部组件（Avalonia 系列、SkiaSharp、HarfBuzzSharp、MicroCom.Runtime、Tmds.DBus.Protocol、brotli、giflib）。

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

各组件的具体版权行见 §1.1 表格。

---

## 3. BSD 2-Clause / BSD 3-Clause License

适用于 libavif、AOM、jxrlib（BSD-2）；libjxl、Skia、libwebp、skcms、libjpeg-turbo（BSD-3）。

```
BSD 2-Clause License

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

BSD 3-Clause 版本在上述条款后追加：

```
3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.
```

各组件版权行见 §1.1；完整原文见 [`licenses/`](licenses/)：
`BSD-2-Clause-AOM.txt`、`BSD-2-Clause-jxrlib.txt`、`BSD-2-Clause-libavif.txt`、`BSD-3-Clause-libjxl.txt`。

---

## 4. Adobe DNG SDK License Agreement

**适用组件**：Adobe DNG SDK 1.7.1，由本项目自研 CLI `dngtool` 静态链接后以 `dngtool.exe` 形式分发。

**完整原文**：[`licenses/Adobe-DNG-SDK-LICENSE.txt`](licenses/Adobe-DNG-SDK-LICENSE.txt)

**关键条款摘要**（不构成替代，请以原文为准）：

- **§1 许可授予**：Adobe 授予非独占、全球、免版税的许可，可为**任何目的**使用、复制、制作衍生作品、公开展示/表演、**分发及再许可**本软件。⇒ 静态链接并分发二进制是被允许的。
- **§2 通知义务（本项目须履行）**：不得移除软件或文档中的任何版权或其他声明，且**分发时须以人类可读形式随附这些声明**。
- **§5**：若以**商业产品**形式分发，须承诺为 Adobe 抗辩并赔偿（`defend, indemnify and hold harmless Adobe`）。免费开源分发不触发。
- **§6 商标**：Adobe 与 DNG logo 是 Adobe Inc. 的商标，未经另行书面许可不得用于背书或推广任何产品。

### DNG 规范专利许可要求的声明

Adobe DNG Specification patent license 要求：任何分发 Compliant Implementation 者，**必须在其源代码与文档中以显著方式包含以下声明**（原文照录）：

> **This product includes DNG technology under license by Adobe.**

该声明已包含在本仓库的 [`NOTICE`](NOTICE) 与项目文档中。

---

## 5. Apache License 2.0

**适用组件**：highway、sjpeg（libjxl 的依赖）。

**完整原文**：[`licenses/Apache-2.0.txt`](licenses/Apache-2.0.txt)

依 Apache-2.0 §4，分发时须：(a) 随附本许可副本；(b) 修改过的文件须带有显著的修改声明；(c) 保留所有版权、专利、商标与归属声明；(d) 若组件自带 `NOTICE` 文件，须在分发中包含其内容。上述组件在本项目内**未被修改**。

---

## 6. LibRaw（LGPL-2.1 / CDDL-1.0 双许可）

**适用组件**：LibRaw，由 `dngtool` 静态链接。

LibRaw 的声明原文（`tools/src/libraw/COPYRIGHT`）：

> LibRaw is free software; you can redistribute it and/or modify it under the terms of the **one of two licenses as you choose**:
> 1. GNU LESSER GENERAL PUBLIC LICENSE version 2.1
> 2. COMMON DEVELOPMENT AND DISTRIBUTION LICENSE (CDDL) Version 1.0

**本项目选择 LGPL-2.1 分支，并依 LGPL-2.1 §3 的升级条款，将 LibRaw 的使用置于 GNU General Public License version 3 之下。**

LGPL-2.1 §3 原文允许：`(If a newer version than version 2 of the ordinary GNU General Public License has appeared, then you can specify that version instead if you wish.)`

⇒ 升级后 LibRaw 的义务由本项目的 GPL-3.0 许可覆盖，其完整文本见 [`LICENSE`](LICENSE)。
LGPL-2.1 原文仍随附于 [`licenses/LGPL-2.1-LibRaw.txt`](licenses/LGPL-2.1-LibRaw.txt) 以供查考。

> ⚠️ **未选择 CDDL-1.0 分支**：CDDL-1.0 与 GPL 不兼容，不得与 GPL-3.0 作品组合分发。

---

## 7. ExifTool 与 Perl

ExifTool 的许可声明原文（`Image/ExifTool.pm`）：

> `Legal:  Copyright (c) 2003-2026, Phil Harvey`
> `        This library is free software; you can redistribute it and/or modify it under the same terms as Perl itself.`

Perl 5 采用双许可：**Artistic License 1.0 或 GNU GPL 1.0-or-later**。

**本项目明确按 GNU GPL version 1.0 or later 分支使用 ExifTool。**

> ⚠️ **不得使用 Artistic-1.0 分支**：Artistic License 1.0 与 GPL 不兼容，与 GPL-3.0 作品组合分发会产生许可冲突。

GPL-1.0-or-later 允许升级至 GPL-3.0，故与 [`LICENSE`](LICENSE) 一致。

---

## 8. zlib License

**适用组件**：zlib（libjxl 依赖、`dngtool` 静态链接）。

```
Copyright notice:

 This software is provided 'as-is', without any express or implied
 warranty.  In no event will the authors be held liable for any damages
 arising from the use of this software.

 Permission is granted to anyone to use this software for any purpose,
 including commercial applications, and to alter it and redistribute it
 freely, subject to the following restrictions:

 1. The origin of this software must not be misrepresented; you must not
    claim that you wrote the original software. If you use this software
    in a product, an acknowledgment in the product documentation would be
    appreciated but is not required.
 2. Altered source versions must be plainly marked as such, and must not be
    misrepresented as being the original software.
 3. This notice may not be removed or altered from any source distribution.
```

版权：(c) 1995-2026 Jean-loup Gailly and Mark Adler。原文见 [`licenses/zlib.txt`](licenses/zlib.txt)。

---

## 9. 发行包内的许可文件 · License files inside the distribution

以二进制形式分发时，发行包根目录须包含：

```
LICENSE                  ← GNU GPL-3.0 逐字未修改全文
NOTICE                   ← 本项目版权 + 必需声明（含 Adobe DNG 声明）
THIRD-PARTY-NOTICES.md   ← 本文件
licenses/                ← 各第三方许可原文
```

此外，`PLAN/jxl/bin/` 目录随上游二进制自带 10 份许可文件（`LICENSE.libjxl`、`LICENSE.brotli`、
`LICENSE.giflib`、`LICENSE.highway`、`LICENSE.libjpeg-turbo`、`LICENSE.libpng`、`LICENSE.libwebp`、
`LICENSE.sjpeg`、`LICENSE.skcms`、`LICENSE.zlib`），`PLAN/exiftool/` 自带 ExifTool 与 Perl 的许可文件。
这些文件**必须保留，不得删除**。

---

## 10. 无担保声明 · Disclaimer

本软件及上述全部第三方组件均按 **"AS IS"** 提供，不附带任何明示或默示的担保。
完整免责条款见 [`LICENSE`](LICENSE) 第 15、16 节及各第三方许可原文。
