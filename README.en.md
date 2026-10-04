[简体中文](README.md) | **English**

# 🖼️ FFmpegPictureUI — FFmpeg Image Converter

**v1.6.0** — 2026-09-28 Release · Cross-platform batch image / animation / video converter built on Avalonia UI, wrapping `ffmpeg` / `ffprobe` plus external encoders (`cjxl` / `djxl` / `cjpegli` / `JxrEncApp` / `JxrDecApp`).

> ℹ️ This README documents the **released stable v1.6.0**; **this working tree is on the `v1.6.0-beta4` development line** (latest entries in [CHANGELOG.en.md](CHANGELOG.en.md)).

QQ Group: 754439779 | [Join Group](https://qm.qq.com/q/M2181PvCkW)

---

## ✨ Core Features

| Feature | Description |
|---|---|
| **Multi-format** | JPEG, PNG, WebP, AVIF, JPEG XL, TIFF — plus animated: GIF, WebP (animated), APNG, AVIF (animated), JPEG XL (animated). JPEG-LI is integrated as the `cjpegli` encoder option for JPEG |
| **Encoder backend** | Per-format switchable `ffmpeg` / `cjxl` / `cjpegli`; `cjxl` used for JXL lossless JPEG repack |
| **Quality control** | Quality slider (snap-to-tick) + format-aware numeric input (JPEG q:v 2-31, JXL distance 0-15, etc.) |
| **Advanced codec options** | Per-format advanced panels: DCT algo, progressive mode, Huffman optimize, adaptive quant, sjpeg backend, PSNR target, lossless compression level, row-mt, still-picture, modular mode |
| **Color management** | Quick select sRGB/BT.709/Display P3/BT.2020 PQ/HLG; dual tagging via CICP (H.273) and ICC; **4 color strategies** (Recommended / Carry source ICC / CICP only / Manual); `iccgen` auto-generates standard ICC; **ProPhoto/ROMM bit-exact fidelity (described by ICC only, no normalization/clipping)**; **pure-managed ICC writer** (spaces iccgen can't name, no external .icc files); zscale bidirectional baking; HDR→SDR tonemap fallback; BT.2020 auto bit-depth linkage; TV/PC color range (auto from input, AVIF native support); Gain Map RGB suggestion |
| **JXL Intelligence** | Auto-detects JPEG-reconstruction vs native codestream; byte-level inspection (`JxlInspector`); picks the optimal pipeline |
| **JPEG-LI** | `cjpegli` as a JPEG encoder backend with full advanced config (chroma subsampling, progressive mode, etc.) |
| **CPU SIMD** | Auto-detects AVX2/AVX/SSE4-capable binaries; runtime probe validates compatibility |
| **Batch queue** | Drag & drop; configurable concurrency (1–128); stop-after-queue |
| **Metadata editing** | ~90-field exiftool editor in 9 categories (Basic, DateTime, Camera, Shooting, GPS, Image, IPTC, XMP, Color); double-click a file to open it |
| **Privacy cleaning** | Strip GPS, timestamps, camera info, all EXIF, XMP |
| **Quality analysis** | SSIM + PSNR post-encode; auto-detects lossless; **.NET native PSNR** (AVX512/AVX2/SSE2, 20× faster than the ffmpeg filter); **target-domain analysis** (RGB-native formats → RGB, YUV-native → YUV, bit-exact with ffmpeg 0.0000dB); **bit-depth normalized** (8/10/16-bit, MaxValue scaling) |
| **Presets** | 29 built-in presets with a secondary management window; save/load/import user presets |
| **Dual theme** | Dark/Light mode; queue text auto-adapts |
| **Bilingual UI** | 中文 / English one-click toggle, top-right button, JSON resource files |
| **Format filter** | Checkbox window to enable/disable recognized image formats; persists to settings |
| **Animation mode** | Mode toggle (Still/Animated); FPS/loop/scale/duration controls (auto or manual); per-format animated advanced panels; video input support |
| **Lossless lock** | PNG/TIFF/APNG auto-lock quality at max and disable the slider |
| **Search drag-drop** | Windows Search result files correctly resolved via Shell namespace paths |
| **Gain Map (Ultra HDR)** | **Fully managed encode/decode with no libultrahdr dependency**: encode uses the chosen JPEG backend (`cjpegli`/`ffmpeg mjpeg`) and emits ISO 21496-1 + XMP(hdrgm) + MPF triple metadata; decode reads the ISO/MPF container directly (exiftool only cross-checks/falls back); base image is either **sRGB SDR** (default, Android/libultrahdr compatible) or **BT.2100 PQ HDR** (experimental, full HDR preserved in the container) |
| **Real-time progress** | Detail window shows live command + progress for all backends: ffmpeg/cjxl/cjpegli/djxl/pipes |

---

## 🔧 External Dependencies

| Tool | Status | Role |
|---|---|---|
| `ffmpeg` + `ffprobe` | ✅ Required | Core encoding/decoding, media probing |
| `cjxl` / `djxl` / `cjpegli` | ⭐ Recommended | JXL transcode, decoding, JPEG-LI encoding |
| `JxrEncApp` / `JxrDecApp` | ⭐ Recommended | JPEG XR encoding/decoding (Microsoft jxrlib) |
| `avifenc` | ⚪ Optional | GIF → AVIF two-step encoding with alpha preservation |
| `dngtool` | ⭐ Recommended | DNG 1.7 JXL decode/encode, RAW demosaicing (LibRaw + Adobe DNG SDK) |
| `exiftool` | ⚪ Optional | Metadata editing, privacy cleaning, ICC embedding |

---

## 🚀 Quick Start

### Prerequisites

- **OS**: Windows 10/11 (other .NET 11 platforms should work)
- **.NET 11 Runtime**: [Download](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)
- **FFmpeg**: Install and make sure `ffmpeg -version` works in a terminal

```bash
# Clone and build
git clone https://github.com/luoye-cpu/ffmpegPictureUI.git
cd ffmpegPictureUI
dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release
dotnet run --project src/FfmpegGui/FfmpegGui.csproj
```

Or download from the [Releases page](https://github.com/luoye-cpu/ffmpegPictureUI/releases).

---

## ⚙️ Headless CLI

`--headless` mode enables batch conversion without any GUI — ideal for scripts, CI, and server background jobs.

### Basic usage

```bash
# Batch-convert all images in a folder to JXL (lossless, preserving structure)
FfmpegGui.exe --headless -i D:\Photos -o D:\Converted -f jxl -e Cjxl --preserve-structure

# Convert to AVIF with a given quality / concurrency
FfmpegGui.exe --headless -i *.jpg -o out -f avif -q 85 -j 4

# Use a preset
FfmpegGui.exe --headless -i input.mov -o out -f jxl --preset "JXL Lossless"

# Preview commands without executing (dry-run)
FfmpegGui.exe --headless --dry-run -i *.jpg -o out -f jxl -e Cjxl

# Dual structured logging (console + JSONL file)
FfmpegGui.exe --headless -i *.jpg -o out -f jxl \
  --log-file logs/convert.jsonl --log-format JsonLines
```

### Main parameters

| Parameter | Description |
|------|------|
| `-i/--input` | Input file/dir/glob (comma-separated for multiples) |
| `-o/--output` | Output directory |
| `--preserve-structure` | Preserve the input directory structure |
| `-f/--format` | Output format (jpg/png/webp/avif/jxl/jxr/tiff/gif/dng) |
| `-e/--encoder` | Encoder backend (Ffmpeg/Cjxl/Cjpegli/Jxr/Dng) |
| `-q/--quality` | Quality 1-100 |
| `-j/--concurrency` | Concurrent tasks (default: CPU core count) |
| `--auto-threads` | Auto-allocate per-task thread count |
| `--settings <file>` | Use a custom config file |
| `--log-file <file>` | Log output file |
| `--log-level <lvl>` | Error/Warn/Info/Debug/Trace |
| `--log-format <fmt>` | Text/JsonLines |
| `--dry-run` | Print commands without executing |
| `--preset <name>` | Use a built-in / user preset |
| `--strip-*` | Privacy cleaning (--strip-gps/--strip-time/--strip-camera/--strip-all-exif/--strip-xmp) |

### Config layering priority

```
Environment variables (FFMPEGGUI_*) > --settings file > portable settings.json > user config dir > defaults
```

Common environment variables: `FFMPEGGUI_FFMPEG_DIR`, `FFMPEGGUI_OUTPUT_DIR`, `FFMPEGGUI_JXL_LIB_DIR`, `FFMPEGGUI_PLAN_DIR`, `FFMPEGGUI_GPU`.

External-tool detection fallback: `FFMPEGGUI_EXT_SEARCH_DIRS` (`;`-separated directory list). When set, the "extended system search"
step uses **only** these roots instead of recursively walking `%LOCALAPPDATA%\Programs`, `C:\Program Files` and every PATH entry.
Unset means the default behaviour is unchanged.

### System tray

Closing the main window minimizes to the system tray by default (instead of quitting) so the queue keeps running in the background. Tray menu: "Show Main Window / Pause Queue / Exit".

---

## 🔬 JXL Pipeline

The app inspects the JXL file type at the byte level and picks the optimal path:

### Scene A — JPEG Reconstruction

```
.jxl (JPEG-wrapped)  ──djxl──▶  .jpg (bit-exact, zero quality loss)
```

### Scene B — Native Codestream

```
.jxl (native)  ──djxl──▶  PNG stream ══pipe══▶  cjpegli  ──▶  .jpg (preferred)
                ──djxl──▶  temp PNG   ──▶  cjpegli  ──▶  .jpg (fallback)
                ──ffmpeg libjxl──▶  mjpeg      ──▶  .jpg (last resort)
```

---

## 🏗️ Project Structure

```
ffmpegPictureUI/
├── src/FfmpegGui/
│   ├── Models/           AppSettings, FfmpegOptions, QueueItem, PresetData
│   ├── Services/         FfmpegCommandBuilder, FfmpegRunner, QueueProcessor,
│   │                     CjxlService, DjxlService, CjpegliService,
│   │                     JxlInspector, JxlPipelineService, JxrService,
│   │                     ExternalToolsDetector, CpuFeatureService,
│   │                     ExifToolService, FormatCapabilitiesService,
│   │                     EncoderDetectionService, QualityAnalysisService,
│   │                     ColorEncodingHelper, IccProfileService,
│   │                     PresetManagerService, RawService,
│   │                     GpuCapabilityService, PlatformServices,
│   │                     LocalizationService, PsRenderService, PsnrCalculator,
│   │                     SimdPixelOps (Photoshop ACR verification, .NET native PSNR, SIMD)
│   ├── Controls/         MetadataEditor
│   ├── Resources/Locales/ zh-CN.json, en-US.json
│   ├── LocExtension.cs   XAML localization markup extension
│   ├── MainWindow.xaml   Primary UI
│   ├── MainWindow.xaml.cs UI logic
│   ├── FormatFilterWindow.axaml  Format filter dialog
│   ├── PresetManagerWindow.axaml Preset manager window
│   ├── ProgressWindow.xaml Progress UI
├── tools/                Verification utilities
├── tests/                Testing (output/ git-ignored, see docs/TESTING.md)
└── publish/              Publish output
```

---

## 📦 Extracted standalone libraries (MIT, usable on their own)

Two parts of this project are **fully managed, make no process calls and read no app settings**;
they ship as a single public repository, **[ColorGainKit](https://github.com/luoye-cpu/ColorGainKit)**,
under **MIT**, so you can use it without taking on this project's GPL-3.0. It contains
**two projects that do not reference each other**:

| Project | Contains | Deliberately excludes | Pre-release self-verification (measured locally) |
|---|---|---|---|
| `src/ColorMatrix` (namespace `ColorMatrix`) | Color-math kernel: CICP/H.273 primaries tables and matrix derivation, Bradford chromatic adaptation, standard transfer functions (sRGB / PQ / HLG …), BT.2100 OOTF, BT.2446 Method A, tone-mapping operators, per-pixel kernels and the AVX2 path; the **pixel-side gain-map math** (`ComputeGainMapGray`, segmented Reinhard) also lives here | ffmpeg / cjxl command orchestration, the planning layer, settings singletons | Build: **0 warnings, 0 errors**; zero-dependency self-test **43 PASS / 0 FAIL**; primaries table cross-checked token-by-token against zimg |
| `src/GainMapKit` (namespace `GainMapKit`) | Gain-map **container layer**: Ultra HDR JPEG parsing (ISO 21496-1 metadata + MPF), and **probing and writing** the `tmap` derived item in AVIF / HEIC | applying the gain to pixels, encode scheduling, XMP `hdrgm` reading | Build: **0 warnings, 0 errors**; self-test **78 PASS / 0 FAIL**, 6 of 6 targeted mutations caught; output accepted by `avifdec`, which reads back the requested CICP |

The two projects are independent (measured: the container layer's type references into the kernel = 0,
and neither has a `ProjectReference` to the other) ⇒ take just one if you only need one.
⚠ Which means "produce a gain map" is **split** across them: pixel-side math in `ColorMatrix`,
container read/write in `GainMapKit`, while encode/decode scheduling (actually spawning
ffmpeg / cjxl / ultrahdr_app) stays in this project and was not extracted.

In-repo docs: overview `README.md`, details `ColorMatrix.md` / `GainMapKit.md`,
provenance `THIRD-PARTY-NOTICES.md`.

**Why MIT is possible**: all code in these two parts was written by this repository's sole author
(`git log` author de-duplication = 1 person, including every commit touching those files), and it
contains no ffmpeg source. The only third-party **code** is one gamut-mapping operator in the kernel
ported line-by-line from libjxl (BSD-3-Clause); its attribution obligation is discharged by that
repository's `THIRD-PARTY-NOTICES.md`.

**Relationship to this repository**: the corresponding code **here remains authoritative** — that
repository holds extracted copies (no `ProjectReference` cutover yet), so changes to that logic must be
made in both places. §7 of each in-repo document lists the deliberate differences to map back.

---

## 📝 Changelog

Current version **v1.6.0** (2026-09-28). Full release history: **[CHANGELOG.en.md](CHANGELOG.en.md)**.

> ℹ️ "Current version" above refers to the **released stable build** (git tag `1.6.0` @ 2026-09-28).
> **This working tree is on the `v1.6.0-beta4` development line** (`<Version>` in `src/FfmpegGui/FfmpegGui.csproj`
> = `1.6.0-beta4`); its changes are recorded under the `v1.6.0-beta4` entry of **[CHANGELOG.en.md](CHANGELOG.en.md)**.
> The README body has not been kept in step with the beta line.


## 📄 License

This project is licensed under the **GNU General Public License v3.0 (GPL 3.0)**.

- ⚠ Scope: this project itself is GPL-3.0; the library extracted from it —
  **[ColorGainKit](https://github.com/luoye-cpu/ColorGainKit)**, containing the `ColorMatrix` color-math
  kernel and the `GainMapKit` gain-map container layer — is published separately under **MIT** and may be
  used independently of this project and of the GPL (see the "Extracted standalone library" section above).
- Full license text: [LICENSE](LICENSE)
- Copyright and required attributions: [NOTICE](NOTICE)
- Third-party component notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
- Full third-party license texts: [licenses/](licenses/)

> This product includes DNG technology under license by Adobe. (dngtool uses the Adobe DNG SDK.)

> ⚠️ GPL 3.0 is a strong copyleft license. If you distribute modified versions of this software (including in binary form), you must also make the source code available under GPL 3.0.

> FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project. This project is not affiliated with, sponsored by, or endorsed by the FFmpeg project.
