# 各格式真实出口 + 回读 + 增益图/HEIC + oxipng 色彩载体 审查报告（a9e2830..957cfa8）

- 路：FMT（第 2 路）。被测二进制 = `publish/build/FFmpegPictureUI-v1.6.0-beta17-x64-full/FfmpegGui.exe`（HEAD `957cfa8`，tag `1.6.0BETA17`）。
- 回读工具：exiftool / ffprobe / pngcicp / jxlinfo / avifenc / oxipng（绝对路径见 `_spec.md` §3）。
- 子进程 TMP 全部钉死 `tests/output/_tmp/review-2026-11-16/fmt/tmp`。
- **本机环境提示**：exiftool 后端 `perl532.dll` 在本机持续报 `code 126`（DLL 加载失败），exiftool 调用需多次 burn-in 才偶发成功。凡依赖 exiftool 的回读/手动造源均受此影响；下方读数均来自成功那一次或 beta17 宿主自带的 exiftool 调用。

## 0. 结论摘要

1. **最高级别 = LOW**。本路 16 个独占文件未发现默认路径上的静默色彩错误；真实产物回读与本批声明的口径全部一致。
2. **oxipng（跨模块重点）实测结论：不破坏 PNG 色彩载体**。直接 `oxipng -o max` 重写带 iCCP+cICP 的 PNG，cICP 保留、iCCP 保留、解包后 ICC 字节 sha256 逐位相同；APP 侧 oxipng 参数显式**不带 `--strip`**。端到端 max vs `--png-optimizer off` 两者 cICP 都在、iCCP 都不在——iCCP 缺席是**色彩管线的 CICP 优先策略（NoIccCarrier，日志明写）**，不是 oxipng 剥离。该结论只登记现象，定级交总控（见 §5）。
3. **928ec89 `route AVIF ICC through avifenc` 真生效**：P3→AVIF 实跑命令含 `avifenc --cicp 12/13/0 --icc ...`；exiftool 回读 `ProfileDescription=smpte432/sRGB` + `ColorPrimaries=SMPTE EG 432-1` + `Matrix=Identity`。`verify-prophoto-faithful` 41/42 绿，carry→AVIF 命令行确含 `--icc`、nclx=unspecified（不伪造 BT.709/601）。
4. **AVIF 16bit 不降位**：rgb48be 输入 → `gbrp12le` 输出（`--depth 12,4` sato+dimg 双 item），无 8bit 静默降位。
5. **JPEG/WebP/JXL 回读与既有口径一致**：JPEG `yuvj444p color_range=pc`（auto=pc）、WebP `yuv420p color_range=tv`（恒 tv）、两者均携带 P3 ICC；JXL `Primaries=P3 / Transfer=sRGB`。
6. **三 HEIC 增益图夹具行为正确**：网格 aux+XMP 走口径 A（p0.5=5，headroom=2.0，rc=0）；灰瓦片走口径 B 对称（p0.5=128，rc=0）；无 XMP 件**不被误判为无损增益图**——结构命中 Apple aux 但元数据缺失 ⇒ rc=3 诚实降级为基础 SDR 图（日志明写，非静默）。URN 改写负控证明探针真读 URN。

## 1. 覆盖清单

| 独占文件 | 结论 |
|---|---|
| src/FfmpegGui/Services/ExifToolService.cs | 写入顺序正确（安全复制排除色彩标签、ICC 最后权威覆盖）；FMT-01（残留 tmp，LOW） |
| src/FfmpegGui/Services/GainMapDecoder.cs | 实测通过：A/B 双口径、Apple aux、失败路径诚实降级 |
| src/FfmpegGui/Services/IsoBmffGainMapProbe.cs | 实测通过：URN 识别、网格分块、负控有牙 |
| src/FfmpegGui/Services/FfmpegCommandBuilder.cs | 实测通过：avifenc --icc 路由、WebP 恒 tv、JPEG yuvj、PNG cICP 优先 |
| tests/data/synth_heic_grid_aux_gainmap.heic | 探针 auxapplegrid 11/0；e2e rc=0 HDR 64×48 peak 812 nits |
| tests/data/synth_heic_grid_aux_gainmap_b.heic | e2e rc=0，口径 B 对称 p0.5=128 |
| tests/data/synth_heic_grid_aux_noxmp.heic | e2e rc=3 诚实降级；FMT-02（注释语义，LOW） |
| tests/projects/ServiceProbe/Parts/09_ProbeGainMapIsoBmff.cs | §1h/§1l 负控全部 PASS（见 §3） |
| tests/projects/ServiceProbe/Parts/13_ProbeFaithful.cs | 随 prophoto 门禁 41 绿 |
| tests/projects/ServiceProbe/Parts/19_ProbeVerdict.cs | 随增益图门禁 fail-closed 断言全绿 |
| tests/projects/ServiceProbe/Parts/24_ProbeWire.cs | 随色彩接线路径 (13) 段全绿 |
| tests/scripts/_make-synth-aux-gainmap.py | 夹具结构与探针读数一致（grid 2×2 / tail 2×u16） |
| tests/scripts/_make-synth-heic-gridgainmap.py | 同上；FMT-02 注释 |
| tests/scripts/verify-color-wiring.ps1 | 跑通 RC=1，3 条 FAIL 全为裸跑环境（perl DLL/tmp 记账），(13) CICP 优先段全 PASS |
| tests/scripts/verify-prophoto-faithful.ps1 | 跑通 RC=1，唯一 FAIL 为截短 ICC 负控夹具被 perl DLL 污染，AVIF ICC 主断言全 PASS |
| tests/scripts/verify-gainmap-isobmff.ps1 | 跑通 RC=1，164 PASS；7 条 FAIL 全为裸跑环境；本路 §1h/§1l 新断言全 PASS |

## 2. 发现详情

### FMT-01（LOW）：安全复制元数据在 exiftool 中途失败时于产物目录残留 `*_exiftool_tmp`
1. **级别 + id**：LOW，FMT-01
2. **位置**：`src/FfmpegGui/Services/ExifToolService.cs:781`（`CopyMetadataAsync`，`-overwrite_original` 直写产物）；调用点 `QueueProcessor.cs:2964`。
3. **现象**：beta17 headless 转换两个 HEIC 增益图件后，用户输出目录里除正式 png 外多出一个 `<名>.png_exiftool_tmp`。
4. **复现步骤**：
   `FfmpegGui.exe --headless --log-level debug -i tests/data/synth_heic_grid_aux_gainmap.heic -o <out> --format png --bit-depth 16`，列 `<out>` 目录。
5. **证据**：`tests/output/_tmp/review-2026-11-16/fmt/e2e_f1/` 下 `synth_heic_grid_aux_gainmap.png_exiftool_tmp`=13167B（正式 png=14125B）；`e2e_f2/` 下 `..._b.png_exiftool_tmp`=341B。日志 `logs/fmt/e2e_f1.out` / `e2e_f2.out` 同段出现 `Failed to load Perl DLL "perl532.dll" code 126`。本批新 `CopyIccProfileAsync` 的 tempTarget 已 `TryDelete`+`CleanStaleExifToolTemp`，但 `CopyMetadataAsync` 这条直写路径未同款清理。
6. **风险评估**：不污染图像内容、不丢色彩标记（残留件与正式件并存）；仅在 perl DLL 加载失败的偶发环境触发。与本批「不留第二份产物」目标相悖。
7. **建议**：在 `CopyMetadataAsync` 对 targetPath 的 `-overwrite_original` 调用后，于 finally 同款调 `CleanStaleExifToolTemp(targetPath)`。不落地修改。

### FMT-02（LOW）：随件 `synth_heic_grid_aux_noxmp.heic` 注释语义与实际探针结果不符
1. **级别 + id**：LOW，FMT-02
2. **位置**：`tests/scripts/_make-synth-heic-gridgainmap.py:113`（noxmp 分支注释「无 XMP：未登记 URN 的负控载体」）。
3. **现象**：随件本身的 auxC URN 是**已登记**的 Apple URN `urn:com:apple:photo:2020:aux:hdrgainmap`，探针判定 `hasGainMap=True form=aux-apple-hdrgainmap determinate=True`；它不是「未登记 URN」负控件。
4. **复现步骤**：`ServiceProbe.exe gainmap-isobmff tests/data/synth_heic_grid_aux_noxmp.heic`。
5. **证据**：`logs/fmt/probe_f3_noxmp_default.txt`。它真正的负控身份来自 `verify-gainmap-isobmff.ps1` §1l 在**副本**上把 URN 改写成未知串后得到 `hasGainMap=False`；e2e 对随件本身跑出 rc=3 诚实降级（`logs/fmt/e2e_f3noxmp.out`）。
6. **风险评估**：仅注释/命名层；不影响判定。后续读者可能误以为随件本身就是「无增益图」负控。
7. **建议**：把生成器注释改写为「Apple aux 结构 + 无 Exif/XMP ⇒ 结构命中但元数据缺失 ⇒ rc=3 诚实降级；未登记 URN 负控由门禁在副本现场改写字节得到」。

## 3. 门禁与负控实测

TMP 钉 `tests/output/_tmp/review-2026-11-16/fmt/tmp`，逐条裸跑（未走 runner、未全量、未重建）：

| 门禁 | rc | 计数 | 日志 | 说明 |
|---|---|---|---|---|
| verify-gainmap-isobmff.ps1 | 1 | PASS=164 FAIL=7 SKIP=9 | `logs/fmt/gate_gainmap.txt` | 本路 §1h/§1l（aux+grid 三件）断言全 PASS；7 条 FAIL（§3b/§3f-1/§3f-8/§3g-2/§1j-7/§3c）均为裸跑下 exiftool perl532.dll code 126 导致回读空/产物数 0 的环境性红，非本路独占文件逻辑回归 |
| verify-prophoto-faithful.ps1 | 1 | PASS=41 FAIL=1 | `logs/fmt/gate_prophoto.txt` | 唯一 FAIL = 「截短 ICC 正向锁」被 exiftool 版本横幅污染（环境）；carry→AVIF `--icc`、nclx unspecified、ICC v4.4.0、PNG ICC 逐字节相同等主断言全 PASS |
| verify-color-wiring.ps1 | 1 | PASS=159 FAIL=3 | `logs/fmt/gate_colorwiring.txt` | 3 条 FAIL（(1)逐字节对照、(11)48-bit 回读空、(11)中转目录计数）均 perl DLL/tmp 记账环境性；(13) CICP 优先段（P3 产物不含 iCCP、仍写 cICP、exiftool 不重复抽 ICC）全 PASS |

**本路自做负控（夹具副本，原件已还原）**：复制 `synth_heic_grid_aux_gainmap.heic`，把 offset 475 的 Apple URN 字节原位改写成未知串 `urn:com:zzzz:unknown:vendor:aux:gainmap`，探针即由 `form=aux-apple-hdrgainmap` 翻转为 `form=aux-vendor-gainmap`（`logs/fmt/` 探针输出见本轮轨迹），证明探针真读 URN 字段而非恒真；副本已删、原件 `Test-Path` 仍为 True。另：无 XMP 随件 e2e 跑出 rc=3（见 §0.6），证明失败路径不静默。

## 4. 未验证项与原因

- **exiftool 重写 iCCP 后 cICP 是否逐块保留**：想直接复跑 exiftool `-icc_profile<=` 在带 cICP+iCCP 的 PNG 上观察 chunk，被本机 `perl532.dll code 126` 卡住（重试 10 次未成功写入）。间接证据：beta17 端到端 CICP 优先路径产物（`tests/output/_tmp/.../ox_max/ox_off`）最终 cICP=12/13/0/1 均在；但**显式 CarryIcc 双写（ICC+CICP 同写 PNG）路径下 exiftool 后置嵌入 iCCP 是否会丢掉 cICP** 未直接回读。
- **CICP 不可命名 ⇒ exit 91 零产物的本路亲手复跑**：用无 ICC 源 PNG + `--color-space "Wide Gamut RGB"` 未触发拒绝（源无 ICC，引擎直通写 bt709）；真正的 exit 91 负控需要「源自带 Wide Gamut ICC」，而手动嵌入 ICC 被 perl DLL 阻断。该负控由 `verify-color-wiring.ps1` §(14) 覆盖（本路只跑了整条门禁、未单独抽出该段观察其 PASS/FAIL）。
- **ffmpeg/libaom 单程 AVIF 路径（非 avifenc）的对照回读**：日志确认「计划要求附 ICC 时切 avifenc」，纯 ffmpeg 路径在需要 ICC 时不被选用；未单独构造「不需要 ICC 的 AVIF 纯 ffmpeg 出口」做 nclx 对照。
- **APNG 帧保持**：代码 `PngOptimizerService.ContainsActlBeforeIdat` 结构级识别 acTL 并跳过 oxipng（未实测转一张 APNG 数帧）。
- 门禁 7+1+3 条 FAIL 的真实定性（环境 vs 回归）未在 runner 环境复核，按 §9 交总控。

## 5. 跨模块候选（只登记现象，不下结论；禁止本路给 CRITICAL）

### oxipng 对 PNG 色彩载体（跨 COLOR / UI / BUILD）—— 现象登记，待总控裁决
- **现象（实测，非推断）**：
  - 直接 `oxipng -o max`（APP 实际参数 `<in> -o max --interlace keep --nb --nc --np --ng --out <out>`，**显式不带 `--strip`**）重写带 iCCP(395B)+cICP(12/13/0/1) 的 PNG：cICP chunk 保留、iCCP chunk 保留（395→397B，zlib 重压缩），**解包后 ICC 字节 sha256 = `EBF95B6F1BB8B90EBA1F9DECE987DE184FA7456069FCB1D2A2C3EF88147B5B95`，与源逐位相同**；仅 IDAT 重压（2293→1536B）。
  - beta17 端到端 PNG→PNG：默认 max（产物 1127B，`[pngopt] 2135→1187 -44.4%`）与 `--png-optimizer off`（2075B）两者**都**写出 cICP=12/13/0/1、**都不含 iCCP**。日志明写「源 ICC 未携带——CICP 已精确表达同一空间（自动推荐档遵循 CICP 优先）」「NoIccCarrier：源带 ICC 但产物改用等价 CICP 描述」。
- **判读（待总控）**：iCCP 在 max/off 两档同时缺席 ⇒ 不是 oxipng 剥离，而是 CICP 优先色彩策略的刻意结果（COLOR 路裁决对象）；oxipng 本身只重压 IDAT、未触碰 cICP/iCCP。APP 参数不带 `--strip`，oxipng 默认保留「仍合法的 chunk」。
- 产物：`tests/output/_tmp/review-2026-11-16/fmt/oxsrc.png`（源）、`oxsrc_oximax.png`（直接 oxipng）、`ox_max/`、`ox_off/`；日志 `logs/fmt/ox_max.out`、`ox_off.out`。

## 6. 复现命令附录

```powershell
$base="C:\PLAN\ffmpegPictureUI"; $work="$base\tests\output\_tmp\review-2026-11-16\fmt"
$env:TMP=$work+"\tmp"; $env:TEMP=$work+"\tmp"; $env:TMPDIR=$work+"\tmp"; $env:LC_ALL=$null
$exe="$base\publish\build\FFmpegPictureUI-v1.6.0-beta17-x64-full\FfmpegGui.exe"
# 增益图三件
& $exe --headless --log-level debug -i $base\tests\data\synth_heic_grid_aux_gainmap.heic -o $work\e2e_f1 --format png --bit-depth 16
& $exe --headless --log-level debug -i $base\tests\data\synth_heic_grid_aux_gainmap_b.heic -o $work\e2e_f2 --format png --bit-depth 16
& $exe --headless --log-level debug -i $base\tests\data\synth_heic_grid_aux_noxmp.heic -o $work\e2e_f3noxmp --format png --bit-depth 16
# oxipng 直测
& "$base\publish\PLAN\artifacts\oxipng.exe" -o max $work\oxsrc.png -o $work\oxsrc_oximax.png   # 实际APP参数见 PngOptimizerService.BuildArgs
& "$base\tools\src\pngcicp\bin\Release\net11.0\pngcicp.exe" info $work\oxsrc_oximax.png
# AVIF
& $exe --headless --log-level debug -i $work\oxsrc.png -o $work\avif_p3 --format avif
& "$base\publish\PLAN\artifacts\avifenc.exe" $work\ppsrc.png $work\pp_direct.avif --icc <prophoto.icc> -j 1
# 格式回读
& $exe --headless --log-level debug -i $work\oxsrc.png -o $work\fmt_jpg --format jpg
& $exe --headless --log-level debug -i $work\oxsrc.png -o $work\fmt_webp --format webp
& $exe --headless --log-level debug -i $work\oxsrc.png -o $work\fmt_jxl --format jxl
# 门禁
pwsh -NoProfile -File $base\tests\scripts\verify-gainmap-isobmff.ps1
pwsh -NoProfile -File $base\tests\scripts\verify-prophoto-faithful.ps1
pwsh -NoProfile -File $base\tests\scripts\verify-color-wiring.ps1
```
