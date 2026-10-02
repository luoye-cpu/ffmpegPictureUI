# Changelog

All notable changes to this project are documented in this file.

> ℹ️ The "probe mode count / gate script count" recorded in each release entry is the
> **baseline at that release**. These numbers keep growing as development continues;
> the **authoritative live values** are `$Probes` and `$expectedScriptCount` in
> `tests/scripts/_run-step3-gates.ps1`.

[简体中文](CHANGELOG.md)

---

## 📝 Changelog

### v1.6.0-beta4 (2026-10-01) — gain-map JPEGs no longer pose as lossless, float JXL can be read back, `--jxl-modular` wired; first L3 suites join the gate manifest (60→65)

> **Lineage**: unlike beta3, this **is** a behavioural change — 3 product files under `src/` were modified after
> the beta3 package was cut (`CjxlService.cs` / `QueueProcessor.cs` / `ImageEncoderArgs.cs`), and each of the four
> fixes below is backed by artifact-level evidence.
> ⚠ Added 2026-10-02: a **New** section (thumbnails) also lands in beta4 — one more product file,
> `ThumbnailService.cs` (new), plus `ExifToolService.cs` / `FormatCapabilitiesService.cs` /
> `QueueProcessor.cs` / `CliParser.cs` / two model files.

**✨ New**

- **Thumbnails (EXIF IFD1): `--thumbnail` / `--thumbnail-size` / `--thumbnail-quality`.** The advanced codec
  panel gains a `ThumbnailPanel` (enable + long edge + quality), offered only for containers where delivery
  was **measured**: `jpg / png / apng / webp / avif / jxl` (the criterion = after writing,
  `exiftool -b -ThumbnailImage` must return image data **exactly as long as `ThumbnailLength` declares**; apng
  is additionally cross-checked with a second, independent reader — `cjxl` + `jxlinfo` report 2230 B for both
  apng and png, versus 250 B for the dangling file ffmpeg produces).
  `tiff / gif / jxr / dng` were all measured to be rejected by exiftool ("0 image files updated" **while still
  exiting 0**, so the exit code alone would be a false green) and stay closed — with the box ticked, an unsupported container **says so out loud** instead of quietly
  emitting a file with no thumbnail. The implementation is a **single post-pass**: shrink the **finished
  artifact** into an 8-bit JPEG (`scale` clamps both dimensions to the long edge, never upscales, even-sized),
  then embed it with `exiftool -ThumbnailImage<=` — no cjxl/avifenc command-line surgery and no second copy
  of the tool-detection logic. The call is folded into the **metadata phase** (inside `RestoreMetadataAsync`, before the colour
  post-steps), preserving this repository's invariant "metadata first, colour labels have the last word".
  `StripAll` / `StripExifAll` take precedence — a new feature must not open a privacy hole.
  Gate: a new `ServiceProbe thumbnail` mode, measured **40/0** (2026-10-02 on this machine, 10 s; two mutation
  checks — M1 `33/7`, M2 `38/2`), now part of the default `$Probes` list (the 23rd mode).
  ⚠ **Not yet determined**: whether viewers actually **display** the thumbnail (not checked in Explorer or any
  third-party viewer here); and because EXIF has no ICC slot, a small preview of a wide-gamut artifact is
  read as sRGB and comes out slightly under-saturated.

**🐛 Fixed**

- **U1 (first half) | JPEGs carrying a gain map are no longer counted as "decode-free repack"**: when the source
  JPEG contains an Ultra HDR / ISO 21496-1 gain map, the `--lossless_jpeg=1` "lossless" claim **silently degraded**
  into a float re-encode — the gain map is flattened, the original JPEG can never be recovered, yet the log still
  said "lossless". The single decision point `CjxlService.UsesLosslessJpegRewrap` grew from 3 conditions to **5**
  (new: the source must not carry a gain map, and modular mode must not be forced; the gain map is decided by
  `GainMapDecoder.JpegContainer` reading the first 256 KB, cached per path with a 512-entry cap). The condition
  lives in **that predicate** rather than only in the command line because the predicate decides two things at once:
  whether `--lossless_jpeg=1` is emitted, **and** whether metadata restoration may carry the source's orientation
  tag. Missing it means a route whose pixels really were decoded to float still counts as "pixels untouched", so
  `Orientation=8` stays on an already-rotated image = viewers rotate it twice (same family as A-28).
  `BuildCjxlArguments` additionally announces it: "this JPEG carries a gain map ⇒ no lossless repack".
- **U1 (second half) | float JXL input no longer fails silently**: `djxl`'s PPM/PNM output does not support float
  images ⇒ the relay always exits non-zero, and the old code just `return false` there (job marked failed, no
  second route). It now falls back to **reading `.jxl` directly with ffmpeg**; the terminal status names which
  route was used (`已完成 (ffmpeg 直读 JXL)`) and, on failure, reports both exit codes.
- **U5 | `--jxl-modular` promoted from a blank to a real argument**: the option was collected by the UI and stored
  in the model but never read by any consumer ⇒ ticking it did nothing. It now emits `--modular=1`. It is
  **structurally exclusive** with jbrd losslessness (jbrd relies on the varloss channel); when both are given,
  modular wins and the override is announced.
- **U4b | tune values libaom-av1 cannot express are no longer dropped in silence (second half landed the same day)**:
  `--avif-tune MS_SSIM` (SVT-only) on libaom previously emitted **neither a flag nor a word** ⇒ `rc=0`, an artifact
  as usual, and the user believed the knob had been applied (provable straight from the code: the value normalizes
  to `ms_ssim` and fell into the switch's gap). It now prints the reason plus the usable values, per this repo's
  existing principle — unavailable must be announced. An empty selection stays silent, so "not chosen" is never
  reported as "failed".
  The **second half** is the other half of the same hole: `NormalizeTuneToken` also folded *unrecognized* display
  strings into the empty string, and the empty string simultaneously meant "not chosen / default", so an
  out-of-domain value like `--avif-tune film` was likewise "no flag, no word"; and that switch has only libaom and
  SVT branches, so on the hardware AVIF backends (nvenc/qsv/amf/vaapi) any selected tune was skipped wholesale.
  ⚠ That hardware case **is reachable straight from the panel** (my first read of it was backwards): the tune
  combo defaults to `avif.tune.iq` (`MainWindow.xaml.cs:916-918`, `FillComboByLoc(…, 4, …)`) and lives inside
  `LibaomAvifPanel` (`MainWindow.xaml:551-555`) — pick a hardware encoder and the panel is hidden, yet the
  collector still reads that combo's `SelectedItem` (`MainWindow.xaml.cs:2840`, gated only by the "advanced codec
  options" master switch), so `tune=IQ` gets swallowed without the user touching it; replaying an old preset does
  the same (`Models/PresetData.cs:168` carries `EncoderName`). **The CLI cannot reach it**: `-e/--encoder` takes
  the EncoderBackend enum and no flag sets `FfmpegOptions.Encoder`.
  Now: only the empty string and the two default labels map to "silent";
  anything unrecognized is passed through verbatim and named by the dispatch layer, and a central notice after
  the tune switch covers "this backend has no tune path at all".
  The judgment is the **N-segment naming arm** of the knob-liveness suite (manifest entry 65): an inexpressible
  value must either exit non-zero or say so — only "rc=0 with not a single word" is red (a `--avif-tune film` arm
  was added with this batch).

**✅ Tests**

- **New 8-bit down-convert identity assertion in `ServiceProbe`** (`PROBE RESULT: pass=39 fail=0`): an 8-bit
  source promoted through `format=rgb48le` always yields code values that are exact multiples of **257** (that
  is precisely the precondition for lossless recovery), so the assertion places a 256-step ramp as `v*257`
  directly into a 16-bit buffer and pushes it through the kernel's 16→8 exit, requiring **identity per code
  value** across all 16 dither phases × {analytic, table-driven} exits — measured max deviation **0/255**.
  Two anti-vacuity controls ship with it (fixture and artifact must both be full-range ramps with ≥250 distinct
  values; the minimum deviation against `want+1` must be exactly 1 — reading 0 there would mean the identity
  assertion isn't comparing anything at all).
  ⇒ This turns #12 (`BayerCenter` centering) from "I read the code and believe it's correct" into an asserted fact —
  the implementation itself already shipped in v1.6.0; what this batch adds is the judgment. The old form subtracted
  `2/16`, which pushed +1 onto 6 of the 16 phases for code values that were exactly representable.
- **First L3 suites moved out of the gitignored directory into the managed manifest (60 → 65)**:
  `t69/pkg-smoke3`, `t76/jbrd-e2e`, `t76/orientation-rewrap`, `t77/gainmap-thirdparty`, `t79/knob-liveness` lived
  only under `tests/output/` (`.gitignore:78`) ⇒ one `git clean -xdf` would erase them and no round ever ran them.
  They now live in `tests/scripts/` with runtime-derived paths (`$PSScriptRoot`). Default subject under test = the
  **freshly built Release binary** (run every round; missing ⇒ refuse to run, no Debug fallback). Setting
  `$env:PKG_VER` raises them to L3 against the shipped package; a missing package, or a `ProductVersion` that does
  not match the expected version ⇒ `exit 2` with **no readings produced at all** (prevents "beta4 readings taken
  against the beta3 package").

### v1.6.0-beta3 (2026-10-01) — two "false-green machines" fixed, first L3 (shipped-package) evidence; includes the 09-30 orientation/geometry batch

> **Lineage (important — do not read this as a second behavioural change)**: zero product source files under
> `src/` changed after the beta2 package was cut (10-01 08:23) —
> `find src \( -name '*.cs' -o -name '*.xaml' \) -newermt "2026-10-01 08:23"` returns **0** — ⇒ **the only
> product-behaviour difference between beta3 and beta2 is the version string.** The DLL hash did change
> (`8FF98B12BE0B71CD` → `41b44697a8289d44`) purely because the version string is compiled into assembly
> metadata, not because code changed; don't treat a hash delta as evidence of an implementation change.
> Everything else in this batch is on the test/gate/documentation side. The 2026-09-30 second block below
> (orientation labels and geometry probing) was already carried by the beta2 package; this entry puts it on
> the release record.

**🐛 Fixes (test/gate side only; product behaviour unchanged)**

- **The "which build am I testing" label was dead in 35 gates (40 sites).** The label tested
  `-like '*\Debug\*'` against a path assembled with **forward slashes**, so it never matched, and the
  silent `Release missing → fall back to Debug` path printed `(Release)`. Measured: one gate reported
  `PASS=22 FAIL=0` while running a Debug binary dated `09-30 22:11` — older than every change in this batch.
  Pattern now accepts both separators (`'*[/\\]Debug[/\\]*'`); semantics verified on PS 5.1 and PS 7.
- **A full round no longer tolerates missing test targets.** `bin/` is git-ignored, so externally wiped
  build outputs are invisible to `git status`; the STALE gate only compared mtimes and short-circuited on
  `$null`, so "absent" slipped through more easily than "stale". Five artifacts are now fail-closed
  (FfmpegGui.dll/exe, ServiceProbe, UiTestHost, GainMapTestHost) and the round header pins their hashes
  (`artifact-sha16: …`), replacing the old habit of hand-copying the DLL prefix into conclusions.
  This round: **83 steps, all exit 0**, bound to `FfmpegGui.dll=8FF98B12BE0B71CD` — the same bytes as in
  the shipped package.

**✅ Tests (before this batch the gate manifest had **zero** L3: nothing tested the shipped package)**

- Five new artifact-level suites against the package: `t76/jbrd-e2e.ps1` (JPEG↔JXL lossless
  round-trip, **89/2** — byte-identical returns, anti-vacuity arms, three conflict classes,
  backend flip, independent hand-built reference), `t76/orientation-rewrap.ps1` (14/0),
  `t77/gainmap-thirdparty.ps1` (**13/0** — libultrahdr itself certifies the artifact as Ultra HDR and
  every declared value is reconciled against the file; previously GainMap numbers were only regexed out
  of the product's own log, i.e. L1), `t69/pkg-smoke3.ps1` (17/0) and `t79/knob-liveness.ps1`
  (**18 knobs = 15 live / 1 dead / 1 rejected / 1 undecidable**), which is how "missing vs unavailable"
  advanced encoder settings are now measured instead of inferred from help text.
- Full ledger: **`docs/PIPELINE_TEST_AUDIT_2026-10-01.md`** — evidence-level census of the 60 manifest
  gates (L3 0 / L2+ 24 / L2 17 / L1 19), per-format availability matrix (including 5 encoder names that
  do not exist in this build at all), and the recommended coverage scope.

**🐛 Known unfixed (newly found this round, all reproducible)**

- **Gain-map JPEG → JXL**: with `--lossless_jpeg=1` a JPEG carrying MPF/APP2 **silently degrades** to
  `lossy, 32-bit float` (it should name that it cannot), and that artifact **cannot be read back**
  (`djxl` route: 0 products, rc=1).
- **`--jxl-modular` is never emitted on the cjxl backend** (it works on the ffmpeg-libjxl route) — same
  option, one live path and one silently dead.
- **Out-of-whitelist `--avif-tune` values are dropped silently** (no error, no notice).

**🐛 Fixes (release tooling: a fail-open that could ship a truncated distribution)**

- **`pack.ps1` did not abort when it failed to clean the previous output directory.** After `Remove-Item`
  reported "the file is being used by another process", the script went straight on to compress and still
  printed `✅ compression done` and `🎉 all 2 versions packed!`. Measured on that run: the on-disk output
  directory held only **58 files** (`PLAN/exiftool/` and `PLAN/artifacts/` entirely gone) while the archive
  was a complete 580 files ⇒ directory and archive in two different states. Now: (1) a failed cleanup
  `throw`s (never pack from a stale directory); (2) a pre-compression completeness gate checks every
  required member with `Test-Path` (`FfmpegGui.exe`, the three legal files, `licenses/`, and in full mode
  ffmpeg, cjxl, djxl, jxlinfo, exiftool, JxrEncApp, JxrDecApp, avifenc, dngtool plus the four PLAN
  subdirectories) and enforces a file-count floor (full ≥ 560, app ≥ 8), refusing to pack otherwise.
  Both modes now print `completeness gate passed: 17 / 580 files`.
- **New independent release check `tests/output/t83/verify-package.ps1`** (16/0): it does not trust the
  script's own "done" — it verifies archive entry counts, required members, **directory vs archive
  reconciliation**, byte-identical hashes of six bundled tools against the repo's `publish/PLAN`, and the
  in-package `ProductVersion`.
- ⚠ Evidence-handling lesson (full text in `docs/PIPELINE_TEST_AUDIT_2026-10-01.md §7.2`): `7z l` prints
  paths with **backslashes**, so grepping with forward slashes makes all six tools score 0 hits — which is
  exactly how I came to falsely report "the beta3 package is missing exiftool/artifacts". An absence claim
  is only admissible after the same command has produced a hit on a known-present sample.

**🐛 Fixes (second block of this batch: orientation labels and geometry probing, 2026-09-30)**

- **Camera/phone TIFFs carrying `Orientation=8` produced "streaked/scrambled" JXL and PNG output**: ffmpeg
  **autorotates** tagged stills (measured: a 6000×4000 16-bit TIFF decodes with a
  `displaymatrix rotation=90` attached, so the frame is really 4000×6000), while the colour engine read
  `ffprobe stream=width,height` (= the **coded** 6000×4000) ⇒ the encode step declared rotated pixels with
  unrotated geometry, **wrapping every row**. Measured against ground truth that output scores
  **PSNR 7.97 dB**; re-reading the same buffer as 4000×6000 and rotating it back gives **inf**
  (byte-identical) ⇒ the geometry label was wrong, not the pixels — and not JXL's fault (the PNG outlet
  measured the same 7.97 dB). Turning on "max dimension" hid the bug, because that relay already normalises
  coded == display. **The defect is outlet-independent; the legacy route was always correct.**
- **Quality analysis was wrong in both directions for these files**: its precheck also compared coded sizes
  ⇒ (1) it refused to compare a **correct** output, reporting "source (6000x4000) vs output (4000x6000)
  resolution mismatch"; (2) for a **geometrically wrong** output the precheck passed and the user only saw
  the tail of ffmpeg's stderr. The precheck and the frame-size computation now both use display geometry,
  and the message names the autorotation.
- **Image-size probing existed in four places, all coded-only** (colour engine / GainMap decoder /
  quality analysis / the queue's GainMap-dedicated pipeline) ⇒ fixing one left three behind. All four now
  delegate to a single `ImageGeometry` (coded size + per-frame `displaymatrix` rotation, swapping width and
  height at ±90), and a structural lock forbids a fifth.
- **The picture still came out sideways after the pixels were fixed: metadata restore copied the source `Orientation` onto the already-upright product.** Neither restore path excluded the tag — `-all:all` (full copy) nor the safe-mode `-EXIF:all` list (which already drops the whole `StripOffsets/RowsPerStrip/PhotometricInterpretation` geometry family but had missed the one member that makes a viewer rotate again). Measured: running the product's own restore over a 4000x6000 untagged product yields `Orientation : 8` ⇒ correct pixels, wrong tag. Both paths now strip it. The single exception is the **JPEG→JXL no-decode rewrap** (`--lossless_jpeg=1`, DCT coefficients copied verbatim ⇒ the tag is still true and must be kept); that decision now comes from one implementation shared with the cjxl command-line builder. ⚠ The exclusion syntax was measured, not guessed: copying the GPS/XMP group-wildcard style into `--Orientation:all` **does not work**.
- **The decoded-buffer check went from a lower bound to an exact equality**: the old `rawLen < w*h*6` is
  **blind to transposition** (the byte count is identical either way), which is one of the direct reasons
  this defect could ship silently.

**🧪 Tests**

- New probe `ServiceProbe orientmeta` (8 assertions): M1/M3 strip the tag on both restore paths, M2 is the control arm
  (Make/Model must still arrive, otherwise "the copy never ran" would turn M1 green), M4 is the counter-example arm
  (the tag must survive when `dropOrientation:false`), M5 covers the rewrap predicate; wired into
  `verify-geometry-orientation.ps1` section ⑥ plus three structural locks. Mutation check: reverting the
  exclusion to an empty string ⇒ `pass=6 fail=2`.


- New gate `verify-geometry-orientation.ps1` **18/0** (companion probe `ServiceProbe orientation`, 15
  assertions): fixture self-checks (coded 64×32 while `rotation` must be 90 ⇒ the two geometries must
  differ or the gate is vacuous), 16-bit outlet **byte-identical** to an independent reference, 8-bit
  outlet ≥45 dB, with "max dimension 16" the filter must take the portrait branch `scale=-2:16` and yield
  8×16, and an untagged negative control must not invent a rotation. ⚠ Mutation check: reverting the
  rotation compensation to identity (= back to coded) ⇒ **7 FAIL**.
- New structural lock `_probe-geometry-single-source-scan.ps1` (entry 59 of the manifest); exit codes 1 and
  0 verified on the red and green states respectively.
- **The full `GainMapTestHost` suite is now wired into the gate manifest** (entry 60,
  `verify-gainmap-host.ps1`, plus a fifth STALE pair for that exe): nothing used to execute that host, so the
  four `sdr` expectations that the 2026-09-20 "SDR ⇒ plain JPEG" ruling had invalidated sat red in the tree
  for ten days without any gate reacting. Those expectations were replaced by **six stricter contract
  assertions** (the degradation must be named, the product must be a complete JPEG, no `MPImage2`/`hdrgm`
  residue is allowed, the base-image size must not change, and the decoder must classify it as *not*
  UltraHDR) ⇒ the suite is now **86/0** and the new gate **14/0** (mutation check: disabling the
  zero-headroom branch ⇒ `PASS=3 FAIL=11`). **No product behaviour changed** — the four reds were a stale
  test expectation, not a defect.
- Two more **oriented-input cases** were added (`E1` nine checks + `E2` eight ⇒ suite **103/0**, gate **18/0**):
  `E1` pins the GainMap decoder to display geometry and carries its own counter-arm (46.22 dB against the
  display reference vs 12.95 dB when the same reference is re-laid-out with coded stride); `E2` pins quality
  analysis so it no longer rejects oriented pairs, while `E2g2` guarantees a *genuinely* mismatched axis is
  still rejected — which is what keeps "green" from being bought by loosening the criterion.

### v1.6.0-beta2 (2026-09-30) — three cjxl fixes: auto photon noise end to end / lossless size (16-bit carrier) / JPEG→JXL recompression actually activated

**🐛 Fixes**

- **JXL jobs with "Auto-read input photo ISO" always failed on the default route**: the UI-preview
  placeholder `auto` for `--photon_noise_iso` was handed straight to cjxl (its parser only accepts a
  float, `ParseFloat && >= 0`) ⇒ the encoder exited 1 before reading stdin, produced no file, the UI
  stayed on "processing", and 30 minutes later reported only "timed out or cancelled". Root cause: the
  "resolve ISO from EXIF" step was inlined in the legacy dispatch branch, which the engine route no
  longer reaches ⇒ the resolution was hoisted above the fork so all three cjxl outlets share one
  resolved option set; the argument builder now also fails closed (a real run omits the flag rather
  than sending a placeholder, and says why).
- **Photon noise on top of lossless (distance=0) silently broke bit-exactness**: measured on an
  891×981 16-bit image — `-d 0` round-trips byte-identical, while `-d 0 --photon_noise_iso=400` moves
  **99.46%** of the 16-bit samples away from the source (mean deviation 30875/65535), yet jxlinfo
  reports `(possibly) lossless` for both (the noise is synthesized by the decoder from frame-header
  parameters, so the file only grows by 10 B ⇒ size cannot reveal it) ⇒ that combination is no longer
  sent to the encoder, and both photon-noise controls are greyed out while lossless is checked
  (unchecking restores them; the user's intent is not swallowed).
- **No more 30-minute hang when an external encoder exits early**: all **five** two-process pipes in the
  codebase (engine cjxl outlet, legacy external-encoder pipe, djxl→cjxl, djxl→ffmpeg, djxl→cjpegli) now
  follow one contract — a failed transfer immediately kills the writer (which otherwise blocks forever on
  a stdout nobody reads), and **a truncated stream can no longer be counted as success** (the two djxl
  pipes previously looked only at the reader's exit code, so a thrown transfer could still yield
  "completed" plus metadata restore); the exception text now carries the encoder's own stderr — users
  used to see the broken-pipe symptom instead of the cause. `verify-color-wiring.ps1 (10h)` locks the
  requirement per site, with a discovery gate on the process→process pipe count (a 6th pipe that is not
  in the table fails first).

- **Lossless JPEG XL from an 8-bit source was delivered as 16-bit, making the file larger than the
  source PNG**: engine pass-through/carry plans always carry 16-bit (that is the **intermediate**
  precision), and the cjxl outlet mistook it for the **delivered** sample depth ⇒ same pixels,
  11,485.5 kB at 8-bit vs **20,233.6 kB at 16-bit (+76.2%)**, turning "6% smaller than the source PNG
  (12,163.9 kB)" into "66% larger". Fixed at the outlet: the PPM/PAM sample depth now comes from
  `plan.TargetBitDepth` + the container's own `MapPixFmt`, and the down-shift is done by ffmpeg's
  `format=rgb24` (measured bit-exact). ⚠ Dead end on record: changing the planning layer to pass 8-bit
  first made the engine kernel's `EncodeTo8` (`code16/65535*255` in float) non-identity for ×257 codes
  (0..91 exact, ~half of 92..255 one low) ⇒ **that turned lossless into ±1 lossy**. A/B on one frozen
  fixture with the pre-fix binary as the control arm: **8,206 kB → 4,584 kB (−44.1%)**, both bit-exact.
- **JPEG → JPEG XL lossless recompression (jbrd) was never activated**: three independent failures
  stacked — the data model defaulted to `false`, the collector fell back to `false` whenever the panel
  was hidden, and the value was unconditionally ANDed with a "preserve Ultra HDR gain map" box that is
  checked by default ⇒ by default JPEG→JXL silently ran a `-d 3.8` **lossy** pixel re-encode while the
  user believed they got lossless. And not one of the 95 gate scripts ever asked whether **the app**
  emits `--lossless_jpeg=1`. Now: **the switch is on by default**
  (`FfmpegOptions.DefaultJxlLosslessJpeg = true`) and JPEG input + JXL output prefers repacking;
  measured 3,042 kB from a 3,209 kB JPEG (0.95×) with jxlinfo reporting
  `JPEG bitstream reconstruction data available`. The switch and the per-job effective value are now
  separate concerns (the effective one must be ANDed with "input really is a JPEG", otherwise
  `ImageEncoderArgs` — which only reads the boolean and cannot see the input — would push **every
  PNG→JXL job out of the colour engine**).
- **JPEG and JPEG XL now share one "Lossless JPEG Recompression" control** (new `JxlLosslessJpegPanel`,
  placed outside the per-format panels for the same reason as `JpegGainMapPanel`: two controls would
  mean two sources of truth).
- **Once the switch defaulted to on, the `-e Ffmpeg` backend's quality axis was swallowed by
  `-distance 0`**: that legacy-pipeline special case translated "the user wants jbrd" into "always emit
  `-distance 0`", on a backend that cannot do recompression at all ⇒ the same JPEG produced
  **byte-identical output at q30/q50/q75/q90** (59,685 B, evidence in `tests/output/t70/`) — the slider
  moved, the product did not. The special case is gone and jxl quality now goes through the single
  `BuildJxlOptions` scale (probe `qa-①` locks q30⇒`-d 10.5` and q90⇒`-d 1.5`); "this backend cannot do
  jbrd" is now announced in the job log instead of being faked with a parameter.
- **The same "middleware precision mistaken for delivery depth" defect also existed at the JXR outlet**:
  the relay container, `-pix_fmt` and the TIFF extras all picked their depth from `plan.OutBitDepth`,
  so even 8-bit material was delivered in a 16-bit carrier. A/B on the same input (**old shipped
  package as the control arm**, evidence `tests/output/t71/jxr-exact.ps1`):
  **877.2 kB → 186.8 kB (−78.7%)**, and both sides decode back **bit-identical** — the carrier changed,
  the pixels did not.
- Two honesty fixes on the way: the "generate command" button used to print `--lossless_jpeg=1`
  **unconditionally** (ignoring the switch and the quality step — a command that would never run) and
  now uses the same builder as execution; and the `-e Ffmpeg` backend's inability to write jbrd was
  only recorded in a source comment, so it now goes into the job log.

**🧪 Gates** (each group mutation-tested)

- `ServiceProbe wire` +7: command-line construction rules (never emit `auto` for real runs, preview
  keeps it, no noise at distance=0, plus a positive control) ＋ `qa-①②③` for the quality axis
  (the switch must not swallow it, real lossless is decided by `Lossless`, `-modular` follows the
  single source of truth)
- `verify-color-wiring.ps1` new `(10g)` +9 (real product CLI end to end + three source-level structural
  locks), `(10h)` (kill the writer in all five two-process pipes; a truncated stream may not count as
  success) and `(10j)` +3 (cjxl/JXR outlets take the delivery depth from `TargetBitDepth`, not from the
  middleware depth)
- `UiTestHost` new `J9d-a~d` +4: photon-noise control availability state machine with lossless on/off,
  plus `J16a/b` for collection from the shared recompression panel
- `verify-jxl-codestream-route.ps1` gained `⑫` (4 checks): a **file-level** judgement for jbrd —
  JPEG→JXL→JPEG comes back **byte-identical** to the source (same SHA256 prefix 19EA0030240ACB77),
  with an anti-vacuity arm (switching jbrd off ⇒ the round trip is **no longer** byte-identical).
  Before this, the repo only ever checked that the forward file contains a reconstruction box; nobody
  compared the round-tripped file itself.
- `verify-color-wiring.ps1` gained `(12)` (8 checks ⇒ that gate is now 140/0): orientation
  self-consistency — a fixture pre-flight first (coded=640x480 with Orientation=8; if that fails the
  whole section goes red instead of green), then three arms asserting "pixels rotated ⇒ the tag must
  go" for jpg/png/webp, one arm asserting "pixels untouched ⇒ the orientation must travel with the
  output" for jxl(jbrd), and an anti-vacuity arm (jxl with jbrd off ⇒ the tag must now be dropped).
  ⚠ The first version read the JXL size with `ffprobe` and produced a false red: for JXL, ffprobe
  returns the **display** size (it applies the container's own Orientation) — only jxlinfo's first line
  gives the coded size. Using jxlinfo, the same assertion holds.
- `verify-jxl-codestream-route.ps1` new `⑩` (does the app itself emit `--lossless_jpeg=1`, with an
  independent jxlinfo read-back, a PNG-input negative control and an XAML single-instance lock) and
  `⑪` (the jxl sub-case of `EncoderUnsupportedSetting` driven **live** for the first time: four arms
  that differ pairwise by exactly one variable — G1↔G3 by input extension, G1↔G4 by switch value — so
  "the block consumes both the input and the value" is falsifiable without mutating the source)
- `_lib-reject.ps1`: **26 `file:line` locators re-anchored to the current tree** (`ColorVerdict.cs`,
  `ColorEngineRouter.cs` and `ColorStrategyPlanning.cs` had all drifted ⇒ self-test back to **40/0**,
  and the mutation arm reproduces the documented `pass=35 fail=6`). ⚠ This library is **not in the
  60-script manifest** (`_lib-*` files are only dot-sourced), so locator drift there can never turn a
  full round red — recorded in its NOTES N7, together with a correction to the jxl sub-case's
  precondition (it needs a JPEG input).
- **Same batch also fixes a "false caption" found by the packaged smoke test**: the JXR outlet picked the
  relay container for its progress line and its failure message with `out8` (the on-disk **middleware**
  depth), while the container itself is chosen by `deliver8` (the **delivery** depth) ⇒ for an 8-bit
  passthrough source it wrote `in.bmp` but announced "raw → TIFF". Two predicates in one block saying two
  different things is the recurring "two independent derivations" shape in this repo. Both now read
  `deliver8`, and `verify-color-wiring.ps1 (11)` gained an assertion pinning **the announced container name
  to the relay filename in the command line**.
- Section (11) of `verify-color-wiring.ps1` and section ③ of `verify-jxr-anim-input.ps1` were the
  **mirror assertions** of the JXR change — they pinned "the relay is always 48-bit". They are rewritten
  to the new contract and **strengthened**: a 2×2 measured table (8-bit no-alpha `24-bit BGR` /
  16-bit `48-bit RGB` / 8-bit alpha `32-bit BGRA` / 16-bit alpha `64-bit RGBA`, code table taken
  verbatim from JxrDecApp's own usage), and the decode code is now derived from the **product's**
  PixelFormat — so if the depth drifts again, the red lands on "expected vs measured disagree" instead
  of "the product will not decode", which reads like a broken file. New size anchors in the same batch:
  same material, 8-bit delivery 30,925 B < 16-bit delivery 151,899 B (alpha: 8,292 B < 81,655 B).
  Both gates now **129/0** and **59/0**.

**ℹ️ This build also contains the still-uncommitted `--preserve-structure` and same-batch output
conflict-guard work from the working tree (the 2026-09-28 batch, not written in this round).**

### v1.6.0 (2026-09-28) — Full bilingual localization + UI usability fixes + global exception safety net + color matrix correctness fixes

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

**🎨 Color matrix correctness fixes (2026-09-28)**

- **PQ transfer constant** — `m1` corrected from `2615/16384` to `2610/16384` (SMPTE ST 2084).
  Both implementations (`TransferCurve` / `SimdPixelOps`) carried the **same** wrong constant, so cross-check
  tests always passed and the error survived for a long time
- **SMPTE 240M transfer** — exponent `0.44 → 0.45`, code-domain breakpoint `0.07611 → 0.0912`
  (matches zimg / libplacebo). This curve lives in the H2 denylist and only runs in-process, so it had no external reference
- **H1 luma-domain scaling** — added encode-side `EncodeScale` to pair with decode-side `LinearScale`. Fixes
  SDR→PQ being **49× over-exposed**, PQ↔HLG differing by **10×**, and non-tone-mapped PQ→SDR being **49× too dark**
- **HLG target inverse OOTF** — the 48-bit main loop was **missing it entirely**; the float path also passed
  normalized values where absolute nits were required
- **nits semantics derived from the curve** — "1.0 = 10000 / 1000 nits" for PQ/HLG no longer depends on whether
  the caller remembers to pass the parameter
- Measured: PQ(BT.2020)→sRGB vs zimg **PSNR 7.26dB → 23.20dB**, mean ΔE **46.5 → 3.30**

**🖼️ GainMap (Ultra HDR / ISO 21496-1) fixes**

- **XMP lookup restricted to gain-map namespaces** — it previously matched by tag suffix, so a camera's
  `EXIF:Gamma` (typically 2.2) could be read as the gain map gamma, **corrupting the gain of legacy Ultra HDR files**
- **Base SOS lookup failure no longer silently emits a "fake Ultra HDR"** — it now reports failure instead of
  shipping a plain JPEG labelled Ultra HDR
- **JPEG segment scan handles markers without a length field** (TEM / RSTn / fill bytes)
- **Decoder hardening** — `long` arithmetic with a per-frame pixel cap (an int overflow could bypass the length
  check); sampling indices clamped to the **decoded buffer** (only x1/y1 were clamped before)
- **Peak luminance now uses `alternateHdrHeadroom`** — third-party files (Adobe etc.) are no longer over-estimated
  just because `gainMapMax` is larger

**🧪 Gate hardening**

- **PQ absolute anchors** now use zimg-measured code values (`0.580694` / `0.751812`) with tolerances tightened
  from `1/1/3` to `0.5/0.5/1.0`, so the anchors can actually catch constant-level errors
- **Fixed two probes that bypassed production scaling** (`xcheck` / `hlgwire`) — they hand-built specs and
  therefore never measured the real engine path; one had a genuine defect permanently waived as a "known non-defect"
- Noted for the record: zimg's `smpte240m` and `bt709` outputs are **bit-identical** (pure γ2.4). This project
  implements the SMPTE 240M-1995 definition, which is **not** isomorphic to zimg's form — recorded in code
  comments so nobody "aligns" it to zimg by mistake

**✅ Verification**
- Full grid **360 cells green** (`PASS=24 FAIL=0`); `color-strategy` 78/0, `decision-delivery` 84/0,
  `color-wiring` 105/0, `gamut-map` 36/0, `prophoto-faithful` 27/0, `gainmap-managed` 22/0,
  `gainmap-engine` 138/0, `gainmap-isobmff` 52/0
- Probes `contract` 136/0, `selftest` 36/0, `curve` 44/0, `hlgwire` 3/3; build with 0 warnings / 0 errors

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
