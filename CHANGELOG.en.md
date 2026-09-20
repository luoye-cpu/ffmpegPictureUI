# Changelog

All notable changes to this project are documented in this file.

> ℹ️ The "probe mode count / gate script count" recorded in each release entry is the
> **baseline at that release**. These numbers keep growing as development continues;
> the **authoritative live values** are `$Probes` and `$expectedScriptCount` in
> `tests/scripts/_run-step3-gates.ps1`.

[简体中文](CHANGELOG.md)

---

## 📝 Changelog

### v1.6.0 (2026-09-16) — Full bilingual localization + UI usability fixes + global exception safety net

**🌐 Bilingual localization (i18n)**
- Hardcoded Chinese **122 → 9** (the rest are dynamic placeholders and language self-names, intentionally kept); `<sys:String>` options **67 → 0**
- New `FillComboByLoc`: ComboBox options are now filled from C# and refresh live on language switch
  (⚠ `<sys:String>` is element content — `{ext:Loc}` does not work there, so that whole class stayed Chinese in the English UI)
- Covered the main window plus 6 standalone windows (ImageDetail / MetadataEditor / PresetManager / FormatFilter / Progress)
- Locale keys **223 → 568**, both languages always in sync
- New structural lock `_probe-i18n-scan.ps1` (key-set parity + XAML reference integrity), wired into the gate suite

**🖥️ UI usability fixes**
- Left panel **276 → 300px**: usable width ~236 → ~260, removing several "exactly full" clipping edges
- Fixed silent clipping caused by horizontal StackPanels with long text (now wrap / split)
- Component-level `NumericUpDown` fix: unified font size 13 and `MinWidth=88`, spinner buttons narrowed to 18px
- New **⚙ settings menu** (top-right): rendering backend (manual GPU choice) / theme / language / cache folder
- New `AppSettings.RenderingMode` (auto / angle / vulkan / software), each keeping a software fallback

**🛡️ Stability**
- New **global exception safety net**: subscribes to Avalonia `Dispatcher` + `AppDomain` + `TaskScheduler`,
  turning unhandled exceptions from 28 `async void` handlers into "log and continue" instead of silent process death;
  crashes are written to `crash.log` in the cache folder

**✅ Verification**
- Full gate suite: **17 probe modes green**, **16 script gates green**; build with 0 warnings / 0 errors

### v1.5.6 (2026-09-06) — Full real-machine testing + format color capability constraints + HDR end-to-end fixes

**🔧 HDR end-to-end fixes (10 root causes)**
- **HDR→TIFF sRGB tonemap fix** — RGB-native formats no longer skip tonemap; TIFF/PNG 16-bit HDR→SDR now converts correctly
- **iccgen HDR compatibility** — force `format=rgb48le` before high-bitdepth YUV input (works around iccgen's upstream "Not yet implemented" limit)
- **AVIF odd-dimension fix** — `scale=ceil(iw/2)*2:ceil(ih/2)*2` before zscale to avoid libzimg error 1027
- **GIF→still first frame** — `insertFramesV1` flag + `-frames:v 1` grabs the first frame
- **Static AVIF→WebP/GIF two-step** — `IsAnimatedAvifAsync()` probes for animated AVIF; falls back to plain ffmpeg for static input
- **`-vsync 0` → `-fps_mode passthrough`** — compatible with newer ffmpeg
- **RAW preprocessing advanced color injection** — dngtool linear TIFF sets `UseAdvancedColorParameters=true` + `ColorPrimaries=bt709` + `ColorTrc=linear`
- **DNG HDR→all formats** — jpg/png/webp/avif/tiff/jxl all pass; color metadata propagated (`rgb48le,linear,bt709`)
- **AnimationLoop default 0→-1** — prevents still images being treated as animations

**📂 Directory normalization**
- Cleaned up scattered temp dirs in the repo root (`temp_e2e/`, `temp_headless_test/`, `output/`) and unified them into `tests/output/`
- Archived 32 one-shot diagnostic scripts to `tests/scripts/_archived_diag/` (git-ignored)
- Removed 40+ accidentally-tracked external tool binaries from the git index (`tests/GainMapTestHost/PLAN/`)
- Removed nested git repos (gitlinks) — 4 dirs including `tools/src/aom`
- `.gitignore` fully rewritten to cover all binaries/third-party sources/temp artifacts

### v1.5.5 (2026-08-30) — Native JXL→PNG pipe transport fix

**🐛 Pipe transport fix (test-driven)**
- **JXL→PNG/WebP/AVIF/TIFF/GIF pipe PAM recognition failure** — when `djxl` decodes JXL with an alpha channel it outputs a PAM stream, but ffmpeg's `-f image2pipe` can't recognize it (`Stream #0:0: Video: none, none` / `Could not find codec parameters`). Now uses dedicated demuxers per stream type: PAM→`pam_pipe`, PPM→`ppm_pipe` — both verified exit=0 with correct output
- **Covers every path through `PipeDjxlToFfmpegAsync`** — no-alpha (PPM) unaffected; all alpha-containing JXL paths fixed end-to-end

**✅ Verification**
- Project compiles (0 errors)
- End-to-end test: alpha JXL `djxl --output_format=pam` → `ffmpeg -f pam_pipe -i -` → PNG, exit=0, image correctly produced

### v1.5.4 (2026-08-16) — Color pipeline overhaul + UI option-effectiveness audit + layout rework

**🎨 Color pipeline fixes (test-driven)**
- **Chroma subsampling effective at all bit depths** — fixed the bug where 10/12/16-bit output was always 4:2:0 (`MapPixFmt` high-bitdepth branch ignored Chroma); 4:4:4/4:2:2 now correctly mapped at every bit depth (libaom verified `yuv444p10le`/`yuv422p10le`)
- **PC-range TIFF → TV-range AVIF fix** — output-side `-color_range` declaration; RGB full-range input no longer compressed by the limited matrix (Y domain measured: pc=10-239 / tv=26-220)
- **TV/PC range auto-follows input** — pix_fmt inference (RGB/yuvj→pc, YUV limited→tv); TV-range input → TV output with zero data stretch (PSNR 53dB)
- **AVIF bit depth clamped per encoder** — libaom/NVENC→12-bit, SVT/QSV/AMF→10-bit (prevents 16-bit input encode failure)
- **UI filters options per encoder** — `UpdateChromaBitDepthOptions()`: SVT/QSV/AMF/WebP show auto/4:2:0 only; libaom/NVENC show 8/10/12-bit depths
- **ColorRange control hidden per format** — unsupported formats (JPEG/WebP/PNG etc.) hide the whole group instead of just disabling; auto-reset to auto on format switch

**🔧 Option-effectiveness overhaul (preview matches enqueue)**
- New TIFF DPI control (`TiffDpiBox`, verified 300dpi write)
- Simple-mode presets completed (cjpegli backend/PSNR, cjxl family, JxlEffort/Modular, WebpCompressionLevel, TiffDpi)
- WebP lossless compression level shown only in lossless mode

**🎯 Defaults and built-in presets rework**
- Model defaults unified: `Chroma=auto`, `StripExifGps=true`, `CjpegliChromaSubsampling=auto`, `CjpegliProgressiveId=2` (progressive verified to compress better: 5-38% smaller files)
- WebP/TIFF dropdowns default to the advanced-panel-off values (picture/lzw)
- All 29 presets redone: GIF preset format fixed (was JPEG), SVT/QSV/WebP chroma 444→420 (verified 420-only), JXL chroma→auto, extreme compression enables cjpegli progressive, PNG fast archive gains lossless

**🖥 Top layout rework**
- FFmpeg dir + output dir merged into one row (flexible Grid columns, output dir 1.4× weight)
- Fixed Grid column misalignment (the output-dir label had fallen into a 12px fixed column causing truncated/squashed text)
- Default window 1100×850 (fits 150% DPI 1080p screens); right-side rows ratio 2:1:2 (queue priority)

**🌍 Other**
- cjpegli args adapted to current libjxl (`--fixed_code`/`--noadaptive_quantization`; removed obsolete `--jpeg_encoder`/`--psnr`)
- Window title version auto-syncs with assembly version (`NormalizeAppTitleVersion`) — release notes can't be missed anymore
- UI testing methodology: UIA bounding boxes + vision model (SenseNova 6.8 Flash Lite) cross-validation

### v1.5.3 (2026-08-15) — Native PSNR + target-domain quality analysis + SIMD speedup

**📊 .NET native PSNR (replaces the ffmpeg psnr filter)**
- New `PsnrCalculator.cs` — pure .NET, **zero external dependencies**, 60MP image **20× faster** (359ms→17.6ms)
- Auto dispatch: AVX512BW → AVX2 → SSE2 → scalar fallback
- Bit-depth normalization: `MaxValue = (1 << bitsPerSample) - 1`, unified over 8/10/16-bit
- Multi-frame semantics: `average` = global MSE aggregation; `min`/`max` = per-frame extremes (matches ffmpeg)

**🎯 Target-domain quality analysis (cross-format domain bias solved)**
- RGB formats (PNG/TIFF/JXL/APNG/GIF/BMP) → RGB domain (rgb24/rgb48le)
- YUV formats (JPEG/WebP/AVIF/HEIC/JXR) → YUV domain (yuv444p/yuv444p16le)
- **Bit-exact match** with the ffmpeg psnr filter (0.0000dB, D37 assertion)
- `scale=out_range=pc` unifies full range (eliminates limited vs full confusion)
- UI analysis results labeled `(RGB)` / `(YUV)`

**⚡ GainMap SIMD speedup**
- New `SimdPixelOps.cs` — 4 hot spots, data-driven decisions
- `FloatToSrgb8` AVX2 **2.5×** (0.15→0.06ms, 1024-entry LUT + gather interpolation)
- Integrated into GainMapEncoder (ReinhardToSdr / WriteBgra8PngAsync batch / ComputeGainMap grayscale) and GainMapDecoder (SrgbToLinearRgba)

### v1.5.2 (2026-08-13) — Pipeline fixes & metadata enhancements

**🔧 Pipeline fixes**
- **Fixed ffprobe color field misalignment** — comma-separated `-show_entries` only honored the last field; actual output is `pix_fmt,color_space,color_primaries,color_transfer` (no bits_per_raw_sample), so primaries/transfer were continuously swapped. Fixed parsing + rewrote the bit-depth parser (covers yuvj420p→8, yuv420p10le→10, rgb48le→16)
- **Fixed RAW preprocess temp dir leak** — `raw_{GUID}` dirs now cleaned up after tasks
- **JXR command display was outdated** — now shows the actual PPM/PAM pipe commands

**🖼 Encoding enhancements**
- **JPEG XL lossless repack toggle** — independently switchable in advanced options (off → explicit `--lossless_jpeg=0` re-encode)
- **Photon-noise auto ISO** — optionally reads ISO from each input photo's EXIF (per-image, more accurate than fixed values)

**🏗 Platform**
- **NativeAOT packaging** — 2 release variants (single-file / full with PLAN), all NativeAOT-compiled, no .NET Runtime required
- **PLAN ffmpeg dir fuzzy matching** — dir names containing "ffmpeg-full" are auto-recognized (e.g., ffmpeg-full-2026.7.24)

---

### v1.5.1 (2026-07-27) — Color pipeline fixes

**🎨 Color management fixes**
- **Fixed SDR→HDR pixels not converted** — for metadata-less 16-bit TIFF etc., manually selecting BT.2020 PQ/HLG only tagged HDR without performing the electro-optical conversion (dark image). `BuildColorArgsSplit` simple mode now returns the actual input color and adds generic target-gamut zscale conversion
- **Fixed JXL→PNG hang** — in pipe mode `inputPath="-"` was passed to the ffprobe probe, causing infinite blocking; three probe functions now have pipe guards
- **HDR→SDR exclusion** — simple mode zscale conversion excludes HDR→SDR (handled by tonemap to avoid highlight clipping)

---

### v1.5.0 (2026-07-23) — Stable Release

**🎛 Encoders & quality**
- **AVIF deep optimization** — libaom gains aq-mode (adaptive quantization), CDEF directional enhancement filtering, intrabc, film-grain synthesis (denoise 0-50); NVENC gains aq-strength + spatial-aq; QSV/VAAPI gain low-power mode
- **Hardware encoders, 7 fine presets** — NVENC (p1~p7) / QSV (veryfast~veryslow) / VAAPI (compression_level 1~7) with independent panels, highest quality by default
- **29 built-in presets** — AOM×4 / SVT×4 / NVENC×3 / QSV×3 / JPEG LI×3 / JXL×4 / WebP×3 / PNG×2 / TIFF / Ultra HDR / GIF, all using up-to-date parameters
- **Smart defaults** — optimized output without ticking "Advanced codec options" (cpu-used=4, still-picture=1, aq-mode=variance, huffman=optimal...)
- **PNG enhancements** — 6 prediction modes with use-case descriptions + DPI print resolution

**🌐 UI & experience**
- **Bilingual UI** — 中文 / English one-click toggle, top-right button, instant effect, JSON resource files
- **Simple mode** — minimal overlay view in the same window; drag-drop straight to queue; auto-encode toggle
- **GPU UI acceleration** — Windows ANGLE/D3D11 rendering, GPU/CPU switchable
- **Portable deployment** — config and presets stored next to the exe, zero %AppData% usage, copy-and-run

**🎨 Color management**
- **ICC system rewrite** — 4 new modes: ①No ICC (CICP) ②Carry ICC ③Bake+embed ④Bake only; zscale pixel baking; iccgen auto-generates standard ICC
- **CICP always on** — H.273 color tags effective in all modes; auto-embeds ICC for non-CICP formats when not sRGB
- **HDR→SDR auto fallback** — automatic tonemapping when the output format doesn't support HDR; double-conversion conflict detection & locking
- **Quick colorspace select** — sRGB / BT.709 / BT.2020 PQ / BT.2020 HLG; BT.2020 auto ≥10-bit

**🔧 Tools & architecture**
- **Tool panel rework** — 3-column horizontal layout (JXL library | exiftool | artifacts); compact status bar shows ✅/❌ automatically after background detection; PLAN portable package auto-recognized
- **GPU encoder detection** — auto-detects NVENC/QSV/AMF availability at startup, verified per encoder at runtime
- **Detection module rewrite** — fully async background pipeline, real timeout protection, incremental logging
- **Packaging optimization** — trimmed debug symbols ~100MB; Resources/Locales/ multilingual resources auto-included

**🐛 Fixes & polish**
- Eliminated duplicate settings.json I/O; 7 external tools detected in parallel
- Windows Search drag-drop paths resolved correctly
- Output type WinExe, no CMD window

<details>
<summary>v1.5.0 Beta details</summary>

### v1.5.0 BETA3 (2026-07-15)
- **Colorspace rework** — simplified selector: sRGB / BT.709 / BT.2020 PQ / BT.2020 HLG; BT.601 removed; auto-fills primaries/trc/matrix after selection; BT.2020 auto ≥10-bit based on source bit depth
- **ICC system rewrite** — 4 new modes (No ICC / Carry ICC / Bake+embed / Bake only); zscale pixel baking; iccgen auto-generates standard ICC; bake target follows colorspace selection
- **CICP always on** — H.273 tags effective in all modes; auto-embeds ICC for non-CICP formats when not sRGB
- **HDR→SDR fallback** — auto zscale+tonemap when output format doesn't support HDR; bit-depth comparison warnings
- **Double-conversion locking** — manual color params locked during ICC baking; 6 conflict scenarios detected
- **Packaging optimization** — removed native .pdb debug symbols ~100MB; all 55 pipeline test matrix cases pass

### v1.5.0 BETA2 (2026-07-14)
- **Simple mode** — minimal overlay view in the same window; drag-drop straight to queue; auto-encode toggle; presets synced with the main UI
- **GPU encoder detection** — auto-detects QSV/NVENC/AMF at startup; per-encoder runtime verification; ✅⚡⚠️❌ color status hints
- **GPU UI acceleration** — ANGLE/D3D11 rendering (Windows); one-click GPU/CPU switch; `--no-gpu` CLI fallback
- **Startup optimization** — duplicate I/O eliminated; 7 external tools detected in parallel via Task.WhenAll; GPU verification deferred to background
- **Portable deployment** — config/presets stored in `presets/` next to the exe; zero %AppData% dependency; copy-and-run

### v1.5.0 BETA (2026-07-14)
- **ICC color management v1** — external .icc/.icm loading; exiftool/iccgen embedding; zscale pixel baking; full sRGB~Rec.2100 colorspace mapping
- **Preset system v2.0** — 24 built-in presets + secondary management window + user JSON preset CRUD
- **Tool panel rework** — 3-column horizontal layout; compact status bar shows ✅/❌ after background detection; PLAN portable package auto-recognized
- **Detection module rewrite** — fully async 3-stage background pipeline; 8s timeout per step; incremental Dispatcher logging
- **AVIF encoder panels** — AOM/SVT/NVENC/QSV/AMF get independent options; panels switch dynamically on encoder change
- **Animation & RAW** — video-to-animation duration limits; dngtool RAW decode auto-detection; extended RAW format support

</details>

<details>
<summary>v1.4.5 & Earlier</summary>

- **v1.4.5** — Windows Search drag-drop paths resolved correctly (Shell namespace); JXL lossless JPEG repack (direct DCT coefficient copy, 5-10× faster); Linux ARM migration analysis completed
- **v1.3.0** — Card-based UI redesign (rounded shadow card containers); dark/light dual theme one-click toggle; GridSplitter flexible 3-region layout; ExifTool privacy cleaning (selective GPS/time/camera/EXIF/XMP removal)
- **v1.2.0** — Batch queue engine (ConcurrentQueue + 1-128 concurrency + failure retry); metadata editor (~90 fields, 9 categories, double-click to edit); format filter window; preset system v1.0; CPU SIMD auto-detection
- **v1.0.0** — Initial release: JPEG/PNG/WebP/AVIF/JXL/TIFF multi-format encoding; quality slider; external encoder integration (ffmpeg/cjxl/cjpegli); CLI build and preview

</details>
