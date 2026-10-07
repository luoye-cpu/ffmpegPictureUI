# Changelog

All notable changes to this project are documented in this file.

> ℹ️ The "probe mode count / gate script count" recorded in each release entry is the
> **baseline at that release**. These numbers keep growing as development continues;
> the **authoritative live values** are `$Probes` and `$expectedScriptCount` in
> `tests/scripts/_run-step3-gates.ps1`.

[简体中文](CHANGELOG.md)

---

## 📝 Changelog

### v1.6.0-beta6 (2026-10-07) — 4 silent-defect fixes (incl. "you gave the flag but were told you didn't") + full audit of the advanced encoder options + second-level test split (gates 72→73)

> **Lineage**: **11 product files** under `src/` changed (+346/−49 lines); the tests went through a
> **second-level split** (5 monolithic group files → 31 one-file-per-mode files); one new gate (**#73**).
> Every fix is backed by **product-level / byte-level** evidence (of the 73 managed gates only
> `probe:runner` fails — and that is a **local sandbox limitation**, fully characterised as **not a product defect**).

**🔴 `manual` colour strategy: giving only `--color-primaries` was wrongly rejected, with a message that contradicted the facts (and a **silent non-delivery** behind it)**

- User-visible symptom (`--color-strategy manual --color-primaries bt2020`): rejected, zero products,
  and the message claimed "**no target colour description was provided**" — while the user **had** provided primaries.
- Root cause in **three layers**, each measured:
  ① `ColorIntentFactory` looked at `ColorTrc` **only** once `HasExplicitColorTarget` (CP **or** CT non-empty)
  held ⇒ with primaries alone `Target=null` ⇒ the planner rejected immediately;
  ② `FfmpegCommandBuilder.Decision`'s advanced branch likewise keyed on `ColorTrc` only
  ⇒ even once admitted, the artifact's labels **still carried the source's P3** (`smpte432`) —
  a **silent non-delivery**, more insidious than the rejection;
  ③ after relaxing the predicate, the descriptor requires **both halves to be nameable** ⇒ one half alone
  raised `目标语义 bt2020/ 无法命名`.
- **Fix**: the predicate is now "CP **or** CT present ⇒ explicit target", with the missing half filled in from the
  **source semantics**; both call sites share one rule. `ManualIntent` assembly is wired up too
  (`ManualIntent.Create` had **zero call sites repo-wide** — a "declared but not implemented" case).
- **Measured**: `--color-primaries bt2020` ⇒ artifact `color_primaries=bt2020` (before: rc=1, no product);
  `bt709`/`smpte432` each honoured; `--color-trc linear` ⇒ `jxlinfo` reports *Linear transfer function*.
  ⇒ **not merely admitted, but actually delivered**.

**🔴 `--dry-run` preview and real execution were not the same source ⇒ the preview **under-reported the real encoder** (which makes audits falsely negative)**

- Two measured divergences: ① `-f jxr -e Jxr` preview printed only `ffmpeg`, while the real encoder is **JxrEncApp**;
  ② `-f dng --dng-bit-depth 8` preview **omitted `-4`** and `-jxlq`, and used the **deprecated `-q`**
  (`-q` is demosaic quality; dngtool read it as JXL quality 3 ⇒ **badly lossy**).
- **Impact**: any audit that trusts dry-run gets a **false negative**
  — measured: trusting dry-run alone would have reported two defects that **do not exist**
  ("dng-bit-depth has no effect", "the JXR backend is not wired").
- **Fix = a structural single source of truth** (not "add two arguments"): extracted
  `RawService.BuildEncodeToDngArguments` (preview+execute 2→1) and `JxrService.ResolveQuality`
  (two real exits + preview 3→1) ⇒ **drift is now structurally impossible**.

**🟠 14 "advanced encoder option ignored/downgraded" notices were **completely invisible in the GUI****

- The product is a `WinExe` (no console in GUI mode) and those notices all went through `Console.WriteLine`
  ⇒ headless users saw them, **GUI users did not** (a "gates see it, users don't" blind spot).
- **Fix**: keep the Console output (headless runs and gates grep that text — **byte-for-byte unchanged**) and add
  `AsyncLocal<Action<string>?> WarnSink` + `EmitWarn()` forwarding to the GUI panel `EncoderOptionWarning`.
  All 14 call sites now go through `EmitWarn` with the **message strings untouched**.
- ⚠ `AsyncLocal` rather than a plain static: queue items are processed **concurrently on multiple threads**,
  so an ambient value would leak one item's notices into another's.
- ⚠ **Known boundary**: conversion-time (queue-run) notices still only reach headless stdout + the detail window
  — wiring them to the panel makes `item.Log` echo back to stdout through `FlushItemLog`, **doubling the notices**
  and breaking the gates' greps (measured, then deliberately reverted; `QueueProcessor` ended at 0 diff).

**🟠 Two APIs skipped the colour-token normaliser ⇒ passing a user-facing name gave `exit -22` and no product**

- `RawColorPipeline.WriteRgb48ToPngAsync` and `GainMapEncoder.EncodeAvifFileAsync` interpolated
  `primaries/trc/matrix` straight into the command line, **bypassing** `ColorSpaceRegistry`'s normalisers
  — while the repo's own comments state, verbatim, "not normalising ⇒ ffmpeg says `Unable to parse`
  ⇒ **exit -22, no product**", and `FfmpegOptions`' setters **do** call them ⇒ an **inconsistent rule**.
- **Measured**: `-color_primaries srgb` ⇒ `[Eval] Undefined constant … in 'srgb'`, no product;
  after normalising (`srgb→bt709`) ⇒ rc=0, normal artifact. Post-fix, the `xcheck` SDR cells went from
  a **hard crash** to **73.19 dB all-green**.

**🚦 Gates 72 → 73: new `verify-dryrun-parity.ps1`**

- Locks **same-source-ness** between the `--dry-run` preview and real execution: criteria
  **A over-report / B under-report (encoder-class tools only) / C argument-level byte equality**,
  plus negative controls and a per-arm "real side non-empty" guard; 4 arms (jxr/png/jpg/dng). Measured **16/0**, ~8 s.
- ⚠ **Wiring it exposed a false negative in the gate itself**: the first version's header claimed it locked
  **two** defects but had **no dng arm** ⇒ criterion ② was **locked by nothing**. Mutation test (dng preview forced
  to 16-bit) still read `pass=10 fail=0` **all green**; after adding the dng arm + criterion C the same mutation
  ⇒ `pass=15 fail=1` **turned red**.
- ⚠ Boundaries (stated, not hidden): `JxrEncApp` has **no argument-level lock** (its real side goes through a
  runtime temp name, so byte comparison would false-red); gif/webp/apng/tiff have **no arms yet**.
  Depends on the in-repo fixture `tests/fixtures/raw_src_8bit.dng`; if missing the arm SKIPs by name (fail-closed).

**🧪 Tests: second-level split — one file per mode**

- Motive (the user's words): "tests should be individual projects, so you can test just one part instead of running
  a huge suite for a one-line change".
- Five monolithic group files (**2437 / 1274 / 1076 / 728 / 693 lines**) →
  **five 53–136-line shared bases + 31 `Modes/<mode>.cs` files**; the classes became `partial`
  ⇒ **`Program.cs` and every call site are unchanged, character for character**.
- **Three acceptance checks**: ① **verbatim** — every code line in `Modes/*.cs` is found in the pre-split files
  (zero rewrites); ② **zero reading drift** — `core 332/1`, `color 126/0`, `engine 82/1`, `geometry 75/1`,
  `diagnostics 82/6`, identical to the digit; ③ **all structural gates green**. A single mode now runs
  standalone (measured 113–288 ms each).

**🔧 Other**

- `_lib-matrix.ps1`'s A30 evidence anchors drifted when 31 lines were inserted into a source file ⇒ **re-pinned**
  (`821→852`, `773→804`), with a mutation test proving the lock still has teeth (reverting the pin ⇒ `pass=7 fail=1`).
- `verify-ui-split-verbatim.ps1` gained a **registered-divergence group** (exactly one: `GroupJ_`); the other 16
  groups are still compared verbatim; mutation-tested (renaming a real assertion in `GroupL_` ⇒ immediate red).
- **Corrected** two earlier conclusions: NVENC `-rc`'s "three visibly different outputs" **cannot be reproduced
  here** (`av1_nvenc` reports `No capable devices found`, and no raw log backs the numbers) ⇒ P0 → P1;
  avifenc `--target-size` measured as **stepped and missing the target** (2000→3570, 10000→7930, 60000→7930)
  ⇒ P0 → P2.

**🔧 Build hygiene**

- Restored a **0-warning / 0-error** build: after the L-1 fix changed the nullable flow-analysis state,
  `src.Curve` in `ColorIntentFactory` raised `CS8602`. Two author-side causes: ① the early returns guarantee
  `src` is non-null, but the analyser does not converge on "reassigned across several `if`s + early exit";
  ② my own `src?.PrimariesToken` (null-conditional) **re-widens `src` to possibly-null**. Fix = drop the
  unnecessary `?.` and use one `!` (null-forgiving) to state the known invariant to the compiler —
  **no runtime behaviour change**.
  ⚠ Deliberately **not** adding an `if (src == null) { … }` guard: that would duplicate an existing Chinese
  message, and `_probe-cjk-hardcode-scan`'s "C# UI surface" bucket **counts by line with zero headroom**
  ⇒ it would tip to 834 > 833 (measured).

**🐛 Fixed a gate false-red caused by an environment-dependent assertion name**

- `verify-ui-split-union.ps1` hard-requires "old-host assertion-name union == 409 and extra == 0".
  The previous version wrote J1z as `if (hw == null) Skip(...) else Check(...)` ⇒ the **assertion name
  appeared/disappeared with the environment** ⇒ the union count oscillated between **409 / 408**
  (measured: 409 earlier, then 5/5 consecutive runs took the SKIP path ⇒ 408) ⇒ periodic false red.
- Root cause was an **author-side design defect**: the sibling `L1a`/`L1b` are stable precisely because
  they call `Check(...)` **unconditionally**.
- Fix = emit **both** unconditionally (same shape as `L1a`/`L1b`): `Skip` still names "not verified" only when
  the precondition truly fails, while `Check` is unconditional with the criterion designed to **pass** when the
  precondition is absent — because **no hardware item means no icon contamination**, which is an expected fact,
  not "untested" (that is what the Skip names). ⇒ stable name, stable count; **neither a false red nor a silent
  disappearance**.
- Measured: `ui-sweep` 3 consecutive runs all **139/0 skip=1** with the assertion name always present;
  union **11/0 (new=409 old=409)**.

**📌 Gate baseline (at this release)**: **73** managed scripts; **26** modes in `ServiceProbe`'s default probe list.
⚠ The authoritative live values are `$Probes` and `$expectedScriptCount` in `_run-step3-gates.ps1`.

---

### v1.6.0-beta5 (2026-10-05) — 10 P0 fixes (incl. 2 silent data/privacy defects) + physical split of the test projects (gates 68→72)

> **Lineage**: this is a large set of **real behaviour changes** — 11 product files under `src/`
> changed (+547/−31 lines) — plus a structural overhaul of the tests and gates
> (three monolithic test hosts → 17 per-subsystem projects). Every fix is backed by product-level evidence.

**🔴 Privacy: JXL output used to retain GPS silently (P0)**

- Root cause (three layers, each measured): ① `jxl` is the default output format and
  `--jxl-lossless-jpeg` (the `jbrd` lossless rewrap) is on by default, and it copies the
  **source JPEG codestream verbatim** (including EXIF/GPS); ② the default path went through the
  `CopyMetadataSafeAsync` safe-copy, whose strip branch only covered `png/apng`; ③ even once covered,
  the **exclusion form `--GPS:all` does not work on JXL** (exiftool reports "1 image files updated"
  while GPS remains) — only the **delete form `-GPS:all=`** works.
- **Fix**: an explicit delete after the copy; the strip trigger widened from "`--strip-metadata` only"
  to "any strip switch enabled".
- ⚠ **The design conflict this exposed is now explicit, never silent**: jbrd can only keep its
  "restore the original JPEG byte-for-byte" promise if nothing touches the container metadata —
  but that is exactly where GPS lives, so **the two are structurally incompatible**.
  Per the ruling that "lossless repacking is the absolute default", the **default preserves
  reversibility** and names the tradeoff (including two alternatives) on the **status line, which is
  visible at the default log level**; an explicit `--strip-gps`/`--strip-metadata` **gives up the
  rewrap and actually strips**.

**🔴 Lossless reversibility destroyed by the tool's own step, and djxl's silent downgrade misreported (P0)**

- Root cause: after `cjxl` produced a `jbrd`-carrying JXL, the metadata-restore step **rewrote the
  container** ⇒ `djxl` **refused lossless reconstruction** and **silently downgraded** to
  `--pixels_to_jpeg` pixel re-encoding, **still exiting 0** ⇒ the status read
  "completed (djxl reconstructed JPEG)", **the opposite of the truth**. Result: the round trip was no
  longer byte-identical and chroma went `yuvj420p → yuvj444p`.
- **Fix**: skip metadata restore when `jbrd` really ran; on the reverse leg, **detect djxl's
  downgrade signal** and name it instead of claiming a reconstruction.
- Measured: `verify-jbrd-e2e` went **52/8 → 100/0**; mutation (reverting the fix) turns it red at 97/3.

**🔴 8-bit output silently dropped tone mapping and three other operators (P0, silently wrong pixels)**

- `TransformRgb48leToRgb8` only read `SrcCurve` + matrix + `GamutMap` + `Clamp` and **never read**
  `Tone`/`LinearScale`/`EncodeScale`/`SrcIsHlgScene` ⇒ on **default-reachable** paths
  (`-f gif`/`-f jpg`/`--bit-depth 8`) HDR→SDR applied **no tone mapping** (clipped highlights,
  ~57% too bright) and HLG sources skipped the BT.2100 OOTF. Measured: at `--bit-depth 8`,
  `hable`/`none`/`reinhard` produced **the same SHA**, while the log still printed "active=yes".
- **Fix**: apply the same four operators in the **same order** as the 16-bit sibling
  (HLG OOTF → LinearScale → ToneMap → matrix → GMO → EncodeScale → inverse OOTF → clamp), and fall
  back to the analytic path when those operators are active.
  Verified: 8-bit means **73.8/91.6/92.6** ≈ 16-bit **74/92/92.9** (identical before the fix).
- Also fixed: an explicit `--color-tone-map none` was **silently upgraded to Hable** (its value was
  indistinguishable from "unspecified") ⇒ an explicit flag now lets the explicit choice **win over**
  the automatic fallback (`none` now takes the existing honest rejection path, exit 91).

**🔴 Remaining P0s (each reproduced before fixing)**

- **Malformed CLI values crashed the process**: `-q abc` exited `-532462766` (unhandled CLR exception)
  instead of the contractual `2`. Fix: catch `ArgumentException` at the single entry point ⇒
  `错误: …` + exit 2 (parameter errors **only**; real crashes are not disguised).
- **Repeated `-i` silently discarded earlier inputs**: `-i a -i b -i a` reported "found **1**"
  (the comma form reported 3) ⇒ now it **appends**.
- **An output directory under an input root caused self-ingestion**: re-running the same command
  re-read the previous run's output (found 1→2→3, accumulating `pic_1/pic_2.png`) ⇒ the output
  subtree is now **excluded** during enumeration.
- **A 135.8 s startup stall when a tool was missing** (looked like a hang): the recursive scan walked
  every PATH directory (67,558 directories measured) ⇒ PATH directories no longer enter the recursive
  face (PATH is still resolved non-recursively elsewhere), `Program Files` keeps only named
  subdirectories, and the per-root budget became a "remaining total budget" ⇒ measured
  **135.8 s → 9.2 s**, with all 8 tools still detected.
- **Pre-existing outputs were overwritten silently** (irreversible): added `--no-overwrite`
  (existing file ⇒ **refuse and name it**, counted as a failure, exit 1); ⚠ the default **still
  overwrites** (existing semantics preserved).
- **A "green measured against the wrong encoder" false green**: without `settings.json`
  (gitignored; it holds machine-absolute paths) `JxlLibDir` is empty ⇒ the encoder **silently fell
  back to ffmpeg's mjpeg**, and the group **still reported all-green with identical summary
  numbers** — only the measured value in an assertion (2694B→2553B) exposed it. Fix:
  `JxlLibDir`/`WindowsArtifactsDir` are now **derived from the repo root**, so a fresh clone gets the
  same encoder out of the box.

**🧪 Physical split of the test projects (user directive: "one test group per part, no more running the whole project for a one-line change")**

- The three monolithic hosts (`ServiceProbe` 577 KB / `UiTestHost` 238 KB / `GainMapTestHost` 98 KB)
  became **17 independent projects** plus 2 shared bases (`Tests.Shared` / `UiHostShared`). Each
  project ships its own `Main`, runs standalone via `dotnet run`, supports `--list` and single-mode
  runs, and **exits 2 fail-closed** on an unknown mode.
- **Reading conservation (independently re-verified by the Lead)**: `UiTestHost` 409/409
  (missing=0 extra=0, stable over 5 consecutive rounds and 3 alternating-order stress rounds);
  `GainMapTestHost` **103 ✅ = 103 ✅ with zero line differences**; `ServiceProbe` identical per mode.
- ⚠ **All three legacy hosts are retained** as the compatibility layer (**29 gate scripts** hard-code
  their exes, and the runner's STALE gate compares the freshness of all four artifacts).
- Four **"ordering luck" hidden dependencies** surfaced by the split are recorded and turned into
  **named entry preconditions** (e.g. `UiHost.ApplyToolPaths()` must run **before** `BootWindow()`).
  Such dependencies **do not show up as red — they silently run fewer assertions**, so summary
  numbers alone can never reveal them.

**🚦 Gates 68 → 72**

- **#69** `_probe-test-group-coverage.ps1`: coverage consistency between the group table
  (`TestGroups`) and the runner's `$Probes`. Its criterion ⑤ requires that every declared mode
  **actually enters the dispatch chain** — motivated by a **false coverage** found during review:
  the `engine` group declared 4 modes but implemented 1 ⇒ `engine` 35/0→**0/0**,
  `simdswitch` 28/0→**0/0**, i.e. **63 assertions silently vanished** while all gates stayed green.
- **#70/#71**: a **verbatim lock** for the UiTestHost split (17 group bodies compared line-by-line,
  2991 lines; 19/0) and a **union lock** (the old host's assertion-name set must equal the union of
  the 5 projects, with both difference directions empty; 9/0).
- **#72** `verify-usability-e2e.ps1`: a **user-perceivable usability** end-to-end matrix
  (A format conversions 8×6 + B colour-target delivery + C batch/directory + D animation frames;
  81 cells). ⚠ It **already existed but was not on the manifest** ⇒ nobody ran it ⇒ it had
  accumulated 2 self-defects nobody noticed (`ffprobe -of csv=p=0` emits columns **in a different
  order than requested** ⇒ 12 HDR cells falsely red; `Start-Process -ArgumentList <array>` **does not
  quote per element** ⇒ `"Display P3"` was split ⇒ 3 cells permanently red).
  **Both were defects in the gate itself; the product was fine.**

**📌 Gate baselines (as of this release)**

- **72** managed scripts, **26** default probe modes; the authoritative full gate suite reports
  **exit=0, "all passed"**.
- ⚠ Counts recorded in each version entry are that release's baseline; the live authoritative values
  are `$Probes` and `$expectedScriptCount` in `_run-step3-gates.ps1`.

### v1.6.0-beta4 (2026-10-01) — gain-map JPEGs no longer pose as lossless, float JXL can be read back, `--jxl-modular` wired; first L3 suites join the gate manifest (60→65)

> **Lineage**: unlike beta3, this **is** a behavioural change — 3 product files under `src/` were modified after
> the beta3 package was cut (`CjxlService.cs` / `QueueProcessor.cs` / `ImageEncoderArgs.cs`), and each of the four
> fixes below is backed by artifact-level evidence.
> ⚠ Added 2026-10-02: a **New** section (thumbnails) also lands in beta4 — one more product file,
> `ThumbnailService.cs` (new), plus `ExifToolService.cs` / `FormatCapabilitiesService.cs` /
> `QueueProcessor.cs` / `CliParser.cs` / two model files.

**🆕 New option (2026-10-04): `--raw-color-target` / GUI "RAW output color target", three tiers**

- **`rec2020` (default) = existing behaviour, unchanged** (prefer Rec.2020 expression: HDR-transfer-capable containers
  and TIFF ⇒ `bt2020`, everything else ⇒ `bt709`).
- **`auto`**: adds a **content judgement** on top of that default — if the image's **actual color usage does not exceed
  the bt709 range**, Rec.2020 is not used.
  ⚠ **Scope is limited to the "SDR-transfer bt2020 tiers"** (currently the TIFF exception tier); **HDR-transfer tiers
  (avif / jxl / png-16bit) are not affected** — bt2020 is part of BT.2100/PQ, and dropping only the primaries would
  produce `bt709 + PQ`, a legal but **non-standard** combination (the log names that tier as not downgraded).
- **`rec709`**: forces `bt709 + sRGB` (maximum compatibility), overriding container capability.
- **Criterion**: apply the `bt2020 → bt709` matrix and take the minimum component, **counting only pixels with
  luminance ≥ 0.25 × Y(p99.9)** — shadow noise pushes chromaticity out of gamut while being completely invisible, so it
  must be excluded (measured: every out-of-gamut pixel in the A7II sample sits in the two darkest deciles and drops to
  **0** past the floor, whereas the yellow-flower sample peaks at **20.98%** in the brightest decile).
  Thresholds: out-of-gamut pixels **< 0.1%** and worst negative excursion **> −0.5%**.
  ⚠ **Three-state**: unmeasurable (size mismatch / no pixels above the floor) ⇒ **conservatively keep `bt2020`**;
  "unmeasurable" is never treated as "fits".
- The option is stored in **presets**; the GUI control lives in the "advanced color parameters" panel
  (ASCII tokens + localized label/tooltip).
- **Measured** (ARW → TIFF, A7II white-paper/line-art sample): default ⇒ `bt2020` (**regression lock: default unchanged**);
  `auto` ⇒ measured out-of-bt709 = **0.0000%** ⇒ **`bt709`**; `rec709` ⇒ `bt709`; invalid value ⇒ **exit code 2** naming the value domain.
- New `ServiceProbe gamutfit` (**15 assertions**, synthetic data, no assets needed): matrix **single-source lock**
  (this kernel's `M2020To709` is derived from `ColorSpaceRegistry`; maintaining the pair independently in two places
  **measured 2.7e-4 apart** ⇒ would let the judgement "say it fits" while the pipeline actually clips), forward/inverse
  consistency, threshold shape, **three-state**, plus `EpsNeg` and **luminance-floor negative controls**.
  **Mutation-verified** (`FloorFactor=0` + `EpsNeg=0` ⇒ `pass=11 fail=4`, and the red ones are the *behavioural*
  assertions (d)(e); restoring gives `15/0`) ⇒ probe default manifest **25 → 26 modes**, whole-round total
  **673 → 688 / 0 fail / 1 skip**
  (⚠ corrected during the 2026-10-04 review: it previously read `669 → 688`, off by 4. `688 − 15 (gamutfit) = 673`;
  `669` was a stale earlier same-day reading (03:10), during which the `wire` probe itself grew **89 → 93 (+4)**,
  unrelated to this change).
- Maintenance: CJK gate criterion `CsUi` **822 → 828** (+6, caused by a later same-day batch of **unregistered**
  UI-bucket Chinese text; proven unrelated to this change by a rollback-controlled experiment — commenting out the
  6 new `[RAW]` log lines moves `[tag] 493 → 487` while `CsUi` stays put); 4 `FfmpegCommandBuilder.cs` line anchors in
  `_lib-matrix.ps1` re-pinned by **+4** (977→981 / 987→991 / 1019→1023 / 2061→2065).

**🔧 Behaviour change (RAW default output target, 2026-10-03)**

- **For RAW input with no user-selected target gamut, the output target is now chosen in three tiers**
  (previously always `bt709|bt2020 + linear`, i.e. the **linear** intermediate was delivered as-is):
  - container can carry an HDR **transfer** (`avif` / `jxl` / 16-bit `png`, …) ⇒ **Rec.2020 + PQ**;
  - **TIFF is the sole exception** ⇒ **16-bit + Rec.2020 + SDR** (BT.2020 primaries + sRGB curve, described
    by a generated BT.2020 ICC); ⚠ the bit depth default of **16-bit** was **pinned explicitly on 2026-10-04** —
    before that it merely "happened to" follow the probed source depth, so a 12-bit camera would have yielded 12-bit;
  - everything else (`webp` / `jpg` / `jpegli` / `gif` / `jxr` / `ppm`, …) ⇒ **bt709 + sRGB** (default route).
  An explicitly chosen target gamut still wins, and is likewise clamped by container capability
  (`GetFormatColorCapabilities` remains the single source of truth).
- **Incidental fix: the colour engine's "pass-through" defect on RAW jobs.** `ColorPrimaries/ColorTrc`
  used to serve **both** as the input declaration (`BuildColorArgsSplit`, emitted before `-i`) and as the
  output semantics (`DecideOutputColor`), while dngtool's intermediate is **linear** — one field pair
  cannot express two values: the input was correctly tagged `linear`, yet the output was dragged along by
  that same `linear` ⇒ **not a single pixel was transformed** (measured: the ARW→TIF product is
  byte-identical to the intermediate). Now split: new **runtime** fields `ColorSourceDeclPrimaries/Trc`
  feed only the input side and the engine's source descriptor, while `ColorPrimaries/ColorTrc` denote the
  output target only **on the two RAW / JXL self-produced-intermediate paths**; the source descriptor is built via `FromCicp(...)`, whose `CicpTag` confidence
  exactly meets the mapping gate. Measured: ARW→TIF `decision: Map`, product `bt2020/iec61966-2-1`;
  ARW→AVIF product `yuv420p12le + color_primaries=bt2020 + color_transfer=smpte2084`.
  ⚠ Scoped during the 2026-10-04 review: the split **only covers the RAW / JXL paths**. The branch at
  `FfmpegCommandBuilder.cs:832-852` **still** treats `ColorPrimaries/ColorTrc` as the pre-`-i` **input
  declaration** whenever `ColorSourceDecl*` is absent (see the comment at `:813-822` and the same finding
  logged in `docs/review-2026-10-04/audit-B-执行落盘.md`) ⇒ read standalone, the original wording was an
  over-broad universal claim.
- **Fixed: `--color-space` (non-advanced mode) used to be overwritten by the RAW default target** (fixed
  2026-10-04). The RAW default block's "did the user already pick a target?" test only looked at
  `UseAdvancedColorParameters` + `ColorPrimaries`/`ColorTrc`, **missing `ColorSpace` in non-advanced mode** —
  exactly the path taken by the CLI `--color-space` and by the GUI friendly colour-space name ⇒ the RAW default
  block still fired and **overwrote** an explicit user choice (measured: `--color-space sRGB` delivered
  `bt2020 + sRGB` instead of the BT.709 primaries the user asked for). A new `rawUserChoseTarget` test fixes it:
  an explicit choice on **any** colour-target axis (the friendly `ColorSpace` name / `ColorPrimaries` / `ColorTrc`)
  is no longer overwritten by the RAW defaults; the **input declaration** `ColorSourceDecl*` was moved out of that
  test and is now **always written** (the dngtool intermediate is **still linear** even when the user picked a target).
- **The RAW tier's HDR test switched from `CanHdrWith(JpegGainMap)` to `CanHdr`**: `jpg` + gain map is **no longer**
  counted as the HDR tier ⇒ the default target for such output moves from **Rec.2020 + PQ** to **bt709 + sRGB**
  (an Ultra HDR **base image** must stay SDR; counting `jpg`+gain map as HDR would write the base image as PQ,
  which is semantically wrong).

**🔧 Behaviour change (precise colour parameters take effect + checkbox semantics, 2026-10-04)**

- **The CLI switches `--color-primaries` / `--color-trc` used to be dead switches.** They (and the
  same-named dropdowns in the GUI advanced panel) only counted as the **output target** when
  `UseAdvancedColorParameters` was true — and **that boolean is only ever set by the GUI; `CliParser`
  never sets it** ⇒ on the command line they only landed in the **input declaration** (before `-i`) and
  could not change the output gamut (measured: ARW→TIF delivered `bt2020` whether or not they were
  passed; the `bt709` the user asked for never took effect). The **semantic tests are now value-derived**
  (new `FfmpegOptions.HasExplicitColorParams` = any of primaries / transfer / matrix non-empty); 12 semantic
  read sites no longer consult that UI boolean, and are now split by role: **wherever the target identity is
  decided, `HasExplicitColorTarget` is used** (primaries / transfer only — `--color-matrix` is an **input
  matrix declaration**, not a target axis), while `HasExplicitColorParams` (matrix included) is used for
  **input declarations / "did the user give precise parameters"**. Measured ARW→TIF: `--color-primaries bt709 --color-trc srgb`
  ⇒ product ICC measured **BT.709** (`rXYZ=[0.4360,0.2225,0.0139]`).
- **The "Advanced Color ⚙" checkbox is now explicitly a pure UI switch** (panel visibility / whether
  modes 2, 3, 4 can be picked) and **no longer takes part in colour decisions** — checking it and leaving
  it unchecked **no longer produce different strategies**. The three precise-parameter dropdowns
  (primaries / transfer / matrix) gained a first entry **`auto` (= unspecified)** as the default:
  **mode 1 (Recommended)** allows `auto` (falling back to the automatic default: BT.2020 when the
  container can carry it, otherwise sRGB/BT.709), while **modes 2/3/4 require a manual choice** (the UI
  names what is missing). Previously "checked but untouched" submitted `bt709/iec61966-2-1`, a different
  target from "unchecked"; it now submits `null`, making the two **identical**.
- **TIFF now always declares its target colour.** A TIFF whose target came from the precise parameters
  previously got **no target ICC** (the post-step gate only looked at the friendly colour-space name)
  ⇒ the product carried no colour declaration at all; the **RAW default tier** (no target chosen) was
  excluded the same way ⇒ the ARW→TIF default product had pixels zscale-converted to `bt2020` yet
  **declared nothing** (downstream reads it as sRGB — i.e. "the log claims bt2020 while the product
  declares nothing"). Both paths now embed the target ICC. Measured, three cells (ARW→TIF): precise
  parameters ⇒ 584 B **BT.709**; `--color-space sRGB` ⇒ 584 B **BT.709**; default tier ⇒ 584 B **BT.2020**.

**🔧 Behaviour change (full-audit fixes: JXL input target / RAW default-tier bit depth / Ultra HDR peak, 2026-10-04)**

- **For a JXL *input*, an explicitly chosen target gamut used to be silently swallowed** (fixed per the
  2026-10-04 full audit, line B P1-1). The SDR-JXL "input colour declaration" was written into
  `ColorPrimaries/ColorTrc`, which `DecideOutputColor` treats as the **output target** ⇒
  `-i x.jxl -f png --color-space "Display P3"` still produced CICP `bt709` (P3 swallowed, no warning).
  It now goes through `ColorSourceDeclPrimaries/Trc` (the same path as RAW) ⇒ measured product `smpte432` ✓.
  In the same batch, `ColorIntentFactory`'s "self-produced intermediate" branch was tightened to recognise
  **only RAW and Ultra HDR decode**, so a `ColorSourceDecl*` set by the CLI/other paths is no longer
  mistaken for a "known source".
- **The RAW default tier judged "HDR capability" from the wrong bit depth** (same audit, line D P1-2):
  the old test used `BitDepth ?? 8` (the **requested** depth) while the delivered depth **follows the source**
  (the RAW intermediate is always 16-bit) ⇒ a 16-bit HDR-capable container was misjudged as 8-bit
  ⇒ the default tier dropped to `bt709/sRGB` while the product was actually `rgb48be` tagged SDR.
  It now uses the **delivered** depth ⇒ **the default tier for ARW→PNG / JXL and other 16-bit containers
  becomes `Rec.2020 + PQ`** (measured `rgb48be, smpte2084, bt2020`); `webp` / `jpg` (8-bit cap) stay
  `bt709/sRGB`; the **TIFF exception tier stays `16-bit + Rec.2020 + SDR`**.
- **Ultra HDR gain-map decode path: peak wired into the intent + input-side declaration added**
  (same audit, line D P1-1, partial fix). The decoded product is **linear** HDR (1.0 = SDR white 203 nits);
  previously there was neither an input-side colour declaration nor any wiring of `DecodedUltraHdrPeakNits`
  into `it.HdrPeakNits` ⇒ a user-chosen target was blocked by the confidence gate and headroom stayed 1.
  Both are now supplied ⇒ **an explicit target works with an Ultra HDR input** (measured `--color-space sRGB`
  ⇒ `decision: Map`).
  ⚠ **Still open**: with **no target**, this path has no default target (`decision: None`, pass-through);
  and because the source curve is declared `linear` while the engine recognises HDR by **curve type** (PQ/HLG),
  `Ultra HDR → SDR` still gets **no tone mapping** (values >1.0 clipped). This needs a **planning-layer**
  decision about how "linear HDR intermediates" are recognised and what their default target is.

**🔧 Behaviour change (the GUI "RAW output gamut mode" combo was previously **entirely inert**, fixed 2026-10-04)**

- **The GUI combo for `--raw-color-target` (`RawColorTargetCombo`) was a "dead control"** (P0, claim ≠ delivery):
  it was declared in `MainWindow.xaml`, resolved via `FindControl` in the `.cs`, and read in two places —
  but it had **neither an initial selection nor a `SelectionChanged` handler** (all 8 of its sibling combos
  **do** get an initial selection). ⇒ With nothing selected, Avalonia's `ComboBox` reports
  `SelectedItem == null` ⇒ `RawColorTarget` was **always null** ⇒ the `?? "rec2020"` fallback in
  `QueueProcessor` **always won** ⇒ **choosing `auto` / `rec709` in the GUI did nothing at all**, and the
  command preview never refreshed. ⚠ **The CLI has always worked** (measured: `rec709` yields a BT.709 ICC)
  ⇒ the defect was **GUI-only**. Added `SelectedIndex = 0` (= the `rec2020` default tier, so no existing
  behaviour changes) plus a `SelectionChanged` handler.
  **Permanent lock**: new gate 68 `_probe-gui-control-wiring.ps1` — a structural lock requiring every
  `x:Name` combo in XAML to be wired via **one of three legitimate paths** (direct `.cs` assignment /
  `FillComboByLoc` / XAML attributes), with a control (≥ 8 judged combos, guarding against an extractor
  that silently matches nothing) and a named `RawColorTargetCombo` lock.
  Mutation-verified: removing that combo's init + handler ⇒ **77/0 → 73/4 red**; restoring ⇒ **77/0**.
- **`--raw-color-target rec709` is no longer silently dropped when a target gamut is also given** (P1):
  the `rawUserChoseTarget` gate swallows the whole default-tier block (including `rec709`); the old log
  said only "user specified a target gamut ⇒ keeping the user's config" and **never mentioned `rec709`**
  ⇒ users believed `rec709` had taken effect while it was discarded, contradicting the `CliParser` help text
  ("overrides container capability"). The ruling keeps **the explicit user target winning** (priority
  unchanged) but now **names the conflict** and gives an actionable hint (drop the explicit target gamut).
  ⚠ Only `rec709` conflicts: `rec2020` *is* the default tier and `auto`'s scope is the TIFF SDR exception
  tier only ⇒ neither is announced (avoiding noise).
- **The animated-fallback clone dropped 3 fields (P1, fixed)**: `CloneOptionsForFfmpeg` copies field by field
  and missed the newly added `ColorSourceDeclPrimaries/Trc` and `RawColorTarget` (plus the pre-existing
  `ColorFromRawDefault`). Without them, `ColorSourceDecl*` is empty on the animated fallback ⇒
  `BuildColorArgsSplit` falls through to the branch that **treats `ColorPrimaries/ColorTrc` (now output
  targets) as the pre-`-i` input declaration** — exactly the defect that field pair was introduced to kill
  ⇒ the earlier fix had covered only the non-animated path. All 4 fields are now copied.

**🔧 Behaviour change (an HDR→SDR tone map that is skipped is now named out loud, 2026-10-04)**

- **With no HDR headroom the engine no longer goes silently "off" — it states that the map is an identity
  and that your request was *not* dropped.** Mechanism: `ToneMapParams.Active = Mode != None && Headroom > 1+1e-6`,
  and `Headroom = PeakNits/SdrWhiteNits`; when the **whole-frame measured content peak ≤ the SDR reference
  white (203 nit)**, the clamp in `ApplyMeasuredPeak` pulls `PeakNits` to exactly 203 ⇒ `Headroom == 1.0`
  ⇒ `Active = false` ⇒ the kernel's `toneOn = false` ⇒ **an explicit `--color-tone-map hable` is never run**.
  ⚠ This is **mathematically correct** (with no headroom Hable is an identity over that range), so the
  **algorithm was not changed**; but the log used to contradict itself (one line said `tonemap=off` while
  another said `tonemap=Hable`) ⇒ **the outcome could not be attributed**. A new explicit line now reads:
  `⚠️ measured content peak 199nit ≤ SDR reference white 203nit ⇒ no HDR headroom for this image; the tone map
  (Hable) is an **identity** here and is passed through as headroom-free (**not** a dropped user request;
  Headroom=1.00)`.
- **New "kernel state" audit log** (an auditability gap): whether the HLG BT.2100 OOTF is actually applied
  **used to be unobtainable from any log** (diagnosis had to work backwards from numbers, which once caused a
  misdiagnosis). Every run now reports the source/target curve names, `HLG source OOTF=on/off(γ)`,
  `HLG target inverse OOTF=on/off`, and the tone map's **mode / active / headroom / adopted peak**.
  ⚠ Mode and active are printed **separately**: `active=no` has two opposite meanings
  ("the user did not ask" vs "asked, but this image has no headroom ⇒ identity"), so printing only one is ambiguous.
- ⚠ Incidental **bookkeeping honesty** (wording only, not a behaviour change): the planning layer's
  `Backend=H2(FfmpegFilter)` currently has **no executor anywhere in the tree** (execution is always fulfilled
  by the in-process H1 kernel), so the old log's claim that "zscale executes it" did not match reality; it now
  carries `[⚠ no H2 executor in this tier; fulfilled by the in-process H1 kernel (bookkeeping item)]`.
  **A real H2 execution path is a separate task.**

**🔧 Behaviour change (range flag of the base image in AVIF gain-map products, 2026-10-03)**

- **The base image of an AVIF gain-map product no longer receives a hard-coded "full" range flag**
  (`IsoBmffGainMapWriter.PatchColr`): `PatchColr` used to **overwrite** the `full_range` bit of the base image's
  `colr` box from the caller's argument, and `Build()` defaulted `baseFullRangeFallback` to `true` (full) ⇒ **the base
  image of every AVIF gain-map product** was tagged full range while the pixels are actually **limited/tv**
  (ffmpeg emits `color_range=tv`; the last byte of the container `colr` box is `0x00`) ⇒ a downstream reader that
  honours "full" maps Y∈[16,235] onto [0,255], i.e. **lifted blacks and compressed contrast**. Now the bit is
  **inherited read-only from the base image's own `colr`** (new `ReadNclxFullRange`), with the fallback default
  flipped `true → false` (limited/tv, matching what ffmpeg actually produces); when the base image carries its own
  nclx box, that box's bit always wins, and inheritance is **bidirectional** (a genuinely full-range base image is
  still written full).
  ⚠ This changes the label of **already-shipped artifacts**: AVIF gain-map files produced before this change carry
  a base-image range tag that contradicts their pixels ⇒ they should be re-exported.
  Permanent lock: a new `ServiceProbe avifgm` mode (25th in the default manifest) checks end to end that the
  product's base-image `colr` last byte agrees with the base image's own range (three parts: ① unit-level —
  `PatchColr` invoked by reflection must inherit read-only and bidirectionally; ② `Build`'s `baseFullRangeFallback`
  default must be `false`; ③ a real ffmpeg-encoded tv base image put through the product writer must still yield
  `0x00` in the product's base image).

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
