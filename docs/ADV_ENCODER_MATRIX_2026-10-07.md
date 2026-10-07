# 高级编码选项 × 编码器后端 —— 实机生效矩阵（2026-10-07）

> 取证员：matrix-runner（共享任务 task-3）
> 方法：`--dry-run` 看命令行 + **真跑**看产物字节/容器真值。每个「生效/无效」结论都附原始命令与原始输出。
> 原始日志：`tests/output/_advmatrix/`（117 个 `.txt`）；摘要索引：`tests/output/_advmatrix/EVIDENCE_INDEX.txt`
> **未改任何 `src/` 或 `tests/` 源码/门禁脚本**；本文件是唯一写入的源码树外文档。

## 0. 必读环境（不设则全是假红）

```powershell
$rd = "C:\PLAN\ffmpegPictureUI\tests\output\_ramdisk"
New-Item -ItemType Directory -Path $rd -Force | Out-Null
$env:TEMP = $rd; $env:TMP = $rd
$env:FFMPEGGUI_RAMDISK = $rd
$env:FFMPEGGUI_PLAN_DIR = "C:\PLAN\ffmpegPictureUI\publish\PLAN"
```

沙箱禁止子进程写 `%TEMP%`；不设 `TEMP`/`TMP` 时 ffmpeg 会 `Permission denied`（exit -13）。
抓输出一律 `cmd /c "<exe> <args> > out.txt 2>&1"`。
产品 exe：`src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe`
工具真路径（**注意不在 PLAN 根，而在 `jxl\bin\`**）：
`publish\PLAN\jxl\bin\cjxl.exe`、`publish\PLAN\jxl\bin\cjpegli.exe`、`publish\PLAN\artifacts\dngtool.exe`

---

## 1. 三条最重要的结论（先说结论）

### 结论 A ⚠️ **`--dry-run` 不是可信的取证工具**（缺陷 D-1）

`--dry-run` 走 `MainWindow.BuildQueueItemCommand`（`MainWindow.xaml.cs:3323`），**与真实执行路径不是同一份代码**。
实测两处**直接撒谎**：

| 场景 | `--dry-run` 打印 | 真跑实际发生 | 影响 |
|---|---|---|---|
| `-f dng` | `dngtool … -jxl -effort 7 -decode_speed 1`（**无** `-4`、**无** `-H`） | `-4`/**真的被传入且生效** | 误判「bit-depth 失效」 |
| `-f jxr` | `ffmpeg … -pix_fmt yuv444p16le`（ffmpeg！） | 实际调 **JxrEncApp**，产物是真 `jpegxr` | 误判「JXR 后端没接上」 |

对照：真实 `-f dng` 路径 `RawService.cs:243-247` **确实**按 `bitDepth <= 8 → -4`、`highlightMode != 1 → -H` 拼参数；
真实 `-f jxr` 走 `QueueProcessor.ProcessJxrAsync`（`:2761`）→ `JxrEncApp`。
**⇒ 本报告中凡「dry-run 没打印」的结论，一律以真跑产物为准复核过。**

### 结论 B ⚠️ **AVIF 跨后端残留：aom 私有参数会被拼到 libsvtav1 上，静默无效**（缺陷 D-2）

- GUI **不给 `-c:v`**（AVIF 命令里没有编码器选择），ffmpeg 为 `.avif` 自选默认 =
  `Stream #0:0 -> #0:0 (png (native) -> av1 (libaom-av1))` ⇒ **默认路线 cpu-used 确实生效**。
- 但用户在 GUI 里把编码器切成 `libsvtav1` 后，`--avif-cpu-used` / `--avif-still-picture` / `--avif-row-mt`
  **仍被原样拼进命令行**，而这三个是 **libaom 私有项**。

**最小复现（真跑，逐字节）**：
```
ffmpeg -y -i src8.png -c:v libsvtav1 -crf 16 -cpu-used 4 -still-picture 1 -map_metadata 0 svt_gui.avif
  => with aom params: 4934 bytes md5=41FF67938DF05811819BDFE9946735C6
  => without:         4934 bytes md5=41FF67938DF05811819BDFE9946735C6   <== 完全一致
```
ffmpeg 只给一行**易被日志淹没**的警告：
```
[out#0/avif] Codec AVOption cpu-used (Quality/Speed ratio modifier) has not been used for any stream.
[out#0/avif] Codec AVOption still-picture (...) has not been used for any stream.
```
SVT 自报配置**不变**：`preset / tune / pred struct : 8 / PSNR / random access`、`AQ mode : 2`。
`-cpu-used 0 / 4 / 8` 三档产物**逐字节相同**（3132 bytes，md5 `05B8CF6A96EA7CAEDA345B3A2AFE7E66`）。

### 结论 C ✅ **属性（belong-to-backend）判定本身写得很扎实，大部分「表面像缺陷」实为已知非缺陷**

`ImageEncoderArgs.cs:611` 用 `backend == AvifBackend.Libaom && o.AvifCpuUsed.HasValue` 严格门控；
tune 轴有专门语言无关归一（`:378 NormalizeTuneToken`）与逐档播报。
实测 `-tune vmaf`（GUI 明确播报并**不**发参数）确属正确：
```
libaom -tune vmaf => exit=-22  Unable to parse "tune" option value "vmaf"
```
`tune=iq` → `-usage allintra -aom-params tune=iq`，真跑 exit=0、3828 bytes ✅。

---

## 2. 主表：格式 × 后端 × 选项

图例：**OK** = 命令行出现且真生效 ｜ **静默无效** = 出现但不生效/不报错 ｜ **报错** = 任务失败 ｜
**残留** = 跨后端仍被拼入 ｜ **不适用** = 该后端无此选项（CLI 也不接受） ｜ **未验证** = 无证据

### 2.1 `-f avif`

| 选项（CLI 键） | 默认(libaom) | libsvtav1 | 命令行出现 | 被接受 | 真生效 | 结论 |
|---|---|---|---|---|---|---|
| `--avif-cpu-used` | ✅ | ❌ | 是（两后端都发） | 是（仅警告） | libaom:是 / svt:**否** | **残留 + 静默无效**（D-2） |
| `--avif-still-picture` | ✅ | ❌ | 是 | 是（仅警告） | libaom:是 / svt:**否** | **残留 + 静默无效**（D-2） |
| `--avif-row-mt` | ✅ | ❌ | 是 | 是（仅警告） | libaom:是 / svt:**否** | **残留 + 静默无效**（D-2） |
| `--avif-tune=psnr/ssim` | ✅ | ❌ | `-tune psnr` | 是 | 是 | OK（svt 侧不适用，见 D-2 同源） |
| `--avif-tune=iq` | ✅ | ❌ | `-usage allintra -aom-params tune=iq` | 是 | 是（3828 B，exit=0） | OK |
| `--avif-tune=vmaf` | 播报后不发 | — | **否** | — | — | OK（libaom 构建无 VMAF，播报正确） |
| `--avif-tune=film` | — | — | **否** | — | — | ⚠️ 静默丢弃（低危，见 D-3） |
| `--avif-aq-mode` / `--avif-cdef-level` | — | — | — | — | — | **CLI 键不存在**（面板有控件） |
| `--avif-svt-preset` / `--avif-svt-tune` | — | — | — | — | — | **CLI 键不存在** |
| `--avif-nvenc-*` / `--avif-qsv-*` / `--avif-vaapi-*` | — | — | — | — | — | **CLI 键不存在** |

**证据（raw）**
```
# CLI 只有 4 个 avif 键（CliParser.cs:862-865）
--avif-svt-preset => ABSENT     --avif-aq-mode   => ABSENT
--avif-svt-tune   => ABSENT     --avif-cdef-level=> ABSENT
--avif-nvenc-preset=> ABSENT    --avif-qsv-preset=> ABSENT
--avif-vaapi-quality=> ABSENT   --avif-preset    => ABSENT

# 默认后端真身份
ffmpeg -y -i src8.png -crf 16 out.avif
  Stream #0:0 -> #0:0 (png (native) -> av1 (libaom-av1))
```
日志：`logs/avif_*.txt`、`logs/avifkey_*.txt`、`logs/aviftune_*.txt`、`av1probe/*.txt`

> ⚠️ **未验证项**：`av1_nvenc` / `av1_qsv` / `av1_amf` / `av1_vaapi` 四个硬件后端
> 在本机**无对应硬件**（`EncoderDetectionService` 的可用性诚实原则会判其不可用），
> 我**无法实测**其参数行为，故本表对这四个后端写「未验证」，不作推断。

### 2.2 `-f jxl`

| 选项 | `-e Cjxl`（外部） | `-e Ffmpeg`（libjxl） | 真生效 | 结论 |
|---|---|---|---|---|
| `--jxl-effort=7` | `-e 7` | `-effort 7` | 是 | OK（两侧键名不同但都对） |
| `--jxl-modular=true` | `--modular=1` | `-modular 1` | 是 | OK |
| `--cjxl-progressive=true` | `--progressive` | （不发） | cjxl 接受（exit=0） | OK（libjxl 无对应项） |
| `--cjxl-photon-noise-iso=1200` | `--photon_noise_iso=1200` | （不发） | cjxl 接受 | OK |
| `--cjxl-auto-photon-noise=true` | `--photon_noise_iso=auto` | （不发） | cjxl 接受 | OK |
| `--jxl-lossless-jpeg` | 视输入/格式相与后生效 | — | 是（CLI 侧归一，`CliParser.cs:534`） | OK |
| `--jxl-preserve-ultrahdr` | **不发** | **不发** | — | OK（cjxl **不支持**，见下） |

**证据**
```
# cjxl 完整选项集（cjxl -h）：-d/-q/-e，进阶需 -v -v；无 preserve_ultrahdr
cjxl src8.png uhdr.jxl -e 7 -d 2.4 --preserve_ultrahdr
  => Unknown argument: --preserve_ultrahdr      exit=1
# GUI 实发组合被 cjxl 正常接受
cjxl src8.png ok.jxl --modular=1 -e 7 -d 2.4 --progressive --photon_noise_iso=1200
  => exit=0  "Encoding [Modular, d2.400, effort: 7]"  Compressed to 4973 bytes
```
日志：`logs/jxl_*.txt`、`logs/jxl_opt_*.txt`、`jpegprobe/cjxl_help.txt`

### 2.3 `-f jpg`

| 选项 | `-e Cjpegli` | `-e Ffmpeg`(mjpeg) | 结论 |
|---|---|---|---|
| `--jpg`/`--jpeg-huffman` | **不发**（cjpegli 反向用 `--fixed_code`） | `-huffman 1` | 见 D-4 |
| `--jpeg-dct=float` | **不发** | `-dct float` | mjpeg 接受；cjpegli 侧不适用 |
| `--jpeg-progressive=1` | **不发**（另有 `cjpegli-progressive`） | **不发 + 明确播报** | OK（mjpeg 无 progressive，播报正确） |
| `--cjpegli-chroma=4:4:4` | `--chroma_subsampling 4:4:4` | **不发** | OK（真生效） |
| `--cjpegli-progressive=0/1/2` | `-p 0/1/2` | **不发** | OK（真生效） |
| `--cjpegli-optimize` | 仅当 **false** 才发 `--fixed_code` | — | ⚠️ 见 D-4 |
| `--cjpegli-adaptive-quant` | 仅当 **false** 才发 `--noadaptive_quantization` | — | ⚠️ 见 D-4 |
| `--cjpegli-backend` | **永不下发** | — | ⚠️ 死键（见 D-4） |
| `--cjpegli-psnr` | **永不下发** | — | ⚠️ 死键（见 D-4） |

**证据（真跑，profile 真值）**
```
cjpegli src8.png p0.jpg --distance 6.2 -p 0 => 6544 B, ffprobe profile=Baseline
cjpegli src8.png p2.jpg --distance 6.2 -p 2 => 6731 B, ffprobe profile=Progressive
# cjpegli 完整选项（cjpegli -v -v --help）：--disable_output/-x/-d/-q/--chroma_subsampling/-p/
#   --xyb/--std_quant/--noadaptive_quantization/--fixed_code/--target_size/--num_reps/--quiet/-v
#   ⇒ 无 --optimize、无 --backend、无 --psnr
```
mjpeg 播报原文：
```
⚠ [jpeg] --jpeg-progressive ignored: ffmpeg's mjpeg encoder has no progressive support,
   so the output is baseline. Use -e Cjpegli for progressive JPEG.
```
日志：`logs/jpg_*.txt`、`logs/jpg_opt_*.txt`、`logs/cjprog_*.txt`、`jpegprobe/cjpegli_help_vv.txt`

### 2.4 `-f png` / `-f apng`

| 选项 | 命令行 | 真生效 | 结论 |
|---|---|---|---|
| `--png-pred=none/sub/up/avg/paeth/mixed` | `-pred <v>` 六个值**逐一原样透传** | ✅ 字节随动 | OK |
| `--png-dpi=300` | `-dpi 300` | ✅ | OK |
| `--bit-depth=16` | `-pix_fmt rgb48le` | ✅ | OK |
| `--chroma=4:4:4`（png） | `-pix_fmt …` | ✅ | OK |
| `--animation-fps/-loop/-scale-w`（apng） | `-f apng -c:v apng -vf fps=…,scale=…` `-plays 0` | ✅ | OK |

**证据**
```
pred none=8312 B / up=8477 B / mixed=8338 B   (三者互不相同 ⇒ 真生效)
apng: ffmpeg … -f apng -c:v apng -vf fps=10,scale=160:-1:flags=lanczos -pred up -dpi 150 -plays 0 …
```
日志：`logs/pngpred_*.txt`、`logs/fmt_apng.txt`、`pngprobe/`

### 2.5 `-f tiff` / `-f webp` / `-f gif`

| 格式/选项 | 命令行 | 真生效 | 结论 |
|---|---|---|---|
| tiff `--tiff-compression=lzw` | `-compression_algo lzw` | ✅ | OK |
| tiff `--tiff-dpi=300` | `-dpi 300` | ✅ | OK |
| webp `--webp-preset=drawing` | `-preset drawing` | ✅ | OK |
| webp `--webp-compression=N` | `-compression_level N` | **单独用时生效**；**被 `-preset` 覆盖** | ⚠️ 见 D-5 |
| gif `--gif-palette=true/false` | 决定 `palettegen` 滤镜是否进入 | ✅ 8678 vs 9556 B | OK |
| gif `--gif-dither=true/false` | `paletteuse=dither=bayer` / 无 | ✅ 8678 vs 8600 B | OK |

**证据（webp 真跑字节）**
```
-q:v 75 -compression_level 0        => 5732 B
-q:v 75 -compression_level 6        => 4308 B      <== level 真生效
-q:v 75 -preset drawing -compression_level 0 => 4608 B
-q:v 75 -preset drawing -compression_level 6 => 4608 B   <== 完全相同 ⇒ 被 preset 覆盖
```
GUI 对此有**准确播报**：
```
⚠ [webp] -compression_level is pinned by -preset drawing (measured byte-identical output for
   level 0/4/6); use --webp-preset none to let -compression_level apply.
```
**证据（gif 真跑字节）**：`gif_pal 8678` / `gif_nopal 9556` / `gif_dither 8678` / `gif_nodither 8600`（四者互异）
日志：`logs/fmt_*.txt`、`logs/webp_*.txt`、`logs/gif_*.txt`、`webpprobe/`

### 2.6 `-f jxr` / `-f dng`

| 选项 | 命令行（真实执行） | 真生效 | 结论 |
|---|---|---|---|
| jxr（任何高级选项） | 真实走 `JxrEncApp`（`ProcessJxrAsync`） | 产物是真 `jpegxr` | OK（**dry-run 预览失实**，D-1） |
| dng `--dng-compression=1/0` | `-jxl` / `-lossless` | ✅ | OK |
| dng `--dng-jxl-quality` | `-jxlq`（另有 `-q` 见下） | ✅ | OK |
| dng `--dng-jxl-effort=9` | `-effort 9` | ✅ | OK |
| dng `--dng-linear=true` | `-linear` | ✅ | OK |
| dng `--dng-bit-depth=8` | `-4`（**dry-run 不打印**） | ✅ **真生效** | OK（D-1） |
| dng `--dng-highlight-mode=3` | `-H 3`（**dry-run 不打印**） | ❌ 该输入上无效果 | 已知非缺陷（见 3.2） |

**证据**
```
# 真跑 jxr：产物容器真值
ffprobe src8.jxr => codec_name=jpegxr  width=320 height=240 pix_fmt=bgr24   (70468 B)
# 真跑 dng：bit-depth 逐字节
def: 1307014 B md5=C7B3EC7AC4471DA54B4ECEEE8A4D9575
bd8:  546798 B md5=7FE490D7A7B8EE99A9E7C133FB9E35B3   <== 不同 ⇒ 生效
h3 : 1307014 B md5=C7B3EC7AC4471DA54B4ECEEE8A4D9575   <== 相同
# dngtool 自身用法（真值）
dngtool (no args): -H <n> highlight mode | -4 / -6 bit depth | -jxl [-q <n>] | -effort <1-9> | -decode_speed <1-4> | -linear
dngtool -compression 1 => [dngtool] unknown option: -compression   exit=1
```
日志：`logs/RUN_jxr*.txt`、`logs/RUN_dng*.txt`、`logs/dngS_*.txt`、`dngprobe/dngtool_noargs.txt`

---

## 3. 缺陷清单（含最小复现）

### D-1 ⚠️ 中高：`--dry-run` 与真实执行路径不一致，预览会撒谎
- **位置**：`Program.cs:343` 调 `MainWindow.BuildQueueItemCommand`；DNG 预览在 `MainWindow.xaml.cs:3329-3339`，
  JXR 预览落到 `:3386` 的 `FfmpegCommandBuilder` 默认分支。
- **现象**：`-f dng` 预览漏 `-4`/`-H`（真实路径 `RawService.cs:243-247` 会发）；`-f jxr` 预览显示 `ffmpeg`，真实调 `JxrEncApp`。
- **最小复现**：
  ```powershell
  # 预览说没有 -4
  FfmpegGui.exe --headless --dry-run -i t_h0.dng -o out -f dng -e Dng --dng-bit-depth=8
  #    => dngtool -e -i t_h0.dng -O … -jxl -effort 7 -decode_speed 1      （无 -4）
  # 真跑产物字节却变了 ⇒ -4 真的传了
  FfmpegGui.exe --headless -i t_h0.dng -o o1 -f dng -e Dng
  FfmpegGui.exe --headless -i t_h0.dng -o o2 -f dng -e Dng --dng-bit-depth=8
  #    => 1307014 B  vs  546798 B
  ```
- **影响**：任何以 `--dry-run` 为准的自动化断言/审计都会得到**假阴性**（把生效的说成失效）。
  本任务若只信 dry-run，会误报两条不存在的缺陷。

### D-2 ⚠️ 中：AVIF aom 私有参数跨后端残留 ⇒ libsvtav1 上静默无效
- **位置**：`ImageEncoderArgs.cs:611-629` 门控的是 `AvifBackend.Libaom`，但 `AvifBackend` 来自
  `FfmpegOptions.Encoder`；而实际 ffmpeg 命令行**不写 `-c:v`**（由 ffmpeg 自选默认编码器）。
- **最小复现**：
  ```
  ffmpeg -y -i src8.png -c:v libsvtav1 -crf 16 -cpu-used 4 -still-picture 1 -map_metadata 0 a.avif
  ffmpeg -y -i src8.png -c:v libsvtav1 -crf 16                            -map_metadata 0 b.avif
  => a 与 b 均 4934 B、md5 41FF67938DF05811819BDFE9946735C6（逐字节相同）
  => 仅有一行 "Codec AVOption cpu-used ... has not been used for any stream"
  ```
  三档 `-cpu-used 0/4/8` 亦逐字节相同（md5 `05B8CF6A…`）。
- **影响**：用户以为设了速度/静帧档，实际零效果，且**默认日志级别下几乎不会注意到**。
  这与本仓既有原则「不可用必须出声」相悖（`-tune vmaf` 就做到了出声，此处没有）。

### D-3 低：`--avif-tune=film` 静默丢弃
- 认不出的 tune 值（如 `film`）命令行**不发、也不播报**；
  而同族的 `vmaf` 会播报。同一开关两套口径。
- 复现：`--avif-tune=film` ⇒ 命令行无任何 tune 相关 token，输出无提示。
- （`NormalizeTuneToken` 注释称认不出的值「原样交给分发层点名」，但实测 `film` 未被点名。）

### D-4 低-中：cjpegli 四个选项是死键 / 单向开关
- **`--cjpegli-backend`、`--cjpegli-psnr`：永不下发**。`CjpegliService.cs:257-261` 已被注释掉，
  理由（当前 cjpegli 不支持 `--jpeg_encoder`/`--psnr`）经实测**成立**：
  `cjpegli -v -v --help` 的完整选项表里**没有**这两个参数。
  ⇒ 不是「实现漏了」，而是**上游工具不支持**；但 CLI 仍接受这两个键且**不报错、不播报**，用户以为设上了。
- **`--cjpegli-optimize` / `--cjpegli-adaptive-quant` 只支持「关」**：
  代码是 `if (!opts.CjpegliOptimize) sb.Append(" --fixed_code")`。
  cjpegli 的实际选项是反向的（`--noadaptive_quantization`、`--fixed_code`），
  且**优化/自适应在 cjpegli 里是默认开**，所以「true」= 不发参数 = 默认行为，**语义正确**。
  但用户传 `--cjpegli-optimize=true` 与不传**完全无法区分**，属可接受的已知设计（见 3.2）。
- 复现：
  ```powershell
  FfmpegGui.exe --headless --dry-run -i src8.png -o out -f jpg -e Cjpegli --cjpegli-psnr=40 --cjpegli-backend=libjpeg
  #  => cjpegli "…src8.png" "…src8.jpg" --distance 6.2 -p 2      （两个键都没出现，且无任何提示）
  ```

### D-5 低：webp `-compression_level` 在被 `-preset` 覆盖时仍照发
- GUI **已准确播报**该覆盖（这一点做得对），但仍把已知无效的 `-compression_level` 拼进命令行。
- 复现见 2.5；`preset drawing` + `cl0`/`cl6` 均 4608 B。

---

## 4. 已知非缺陷（附理由）

| # | 表面现象 | 为何不是缺陷 |
|---|---|---|
| N-1 | `--dng-highlight-mode` 改值产物逐字节相同 | **dngtool 侧无效果**，非 GUI 问题。直连 `dngtool -H 0/1/3/9` 在同一输入上产 4 个**完全相同**的 md5（`5C0E69E6…`）。GUI 已正确下发 `-H`。 |
| N-2 | `--jxl-preserve-ultrahdr` 命令行里从不出现 | cjxl **不支持**该参数：`cjxl --preserve_ultrahdr` ⇒ `Unknown argument` exit=1。不发是**正确**的。 |
| N-3 | mjpeg 上 `--jpeg-progressive` 不发 | ffmpeg mjpeg 编码器无 progressive，GUI **明确播报**并指引改用 `-e Cjpegli`。口径正确。 |
| N-4 | `--avif-tune=vmaf` 不发参数 | 本机 libaom **构建无 VMAF**：`-tune vmaf` ⇒ exit=-22 `Unable to parse "tune" option value "vmaf"`。GUI **主动播报**并说明改走 libsvtav1。 |
| N-5 | `-e` 不接受 `libaom-av1`/`av1_nvenc` 等具体编码器名 | 设计使然：`-e` 只收 5 个 `EncoderBackend` 枚举（`Ffmpeg`/`Cjxl`/`Cjpegli`/`Jxr`/`Dng`，`EncoderDetectionService.cs:14-21`）。传别的名会 `错误: 未知编码器: X`（exit=2）——**报错而非静默**，符合「可用性诚实」。 |
| N-6 | `--chroma=4:4:4` 在 libsvtav1 上无对应子采样 | 已有专门拦截（`ImageEncoderArgs.cs:969-988`）把「静默降级为 4:2:0」判为不可承接。属已识别并已处理。 |
| N-7 | `--avif-svt-preset` 等 10 个键「未知选项（已忽略）」 | 这些键在 CLI 中**本来就不存在**（`CliParser.cs` 里 avif 只有 4 个 case），GUI 面板控件只走 GUI 路径。CLI 会显式打印 `⚠️ 未知选项 X（已忽略）` ⇒ **出声**，非静默。 |
| N-8 | cjpegli 上 `--jpeg-huffman`/`--jpeg-dct` 不出现 | 这两个键语义属 ffmpeg/mjpeg；cjpegli 用 `--fixed_code` 表达同类语义。跨后端**未错发**属正确行为。 |

---

## 5. 逐格式清点（一句话版）

| 格式 | 合法后端 | 高级面板选项总评 |
|---|---|---|
| png | Ffmpeg | ✅ 全 OK（pred 6 值、dpi、bit-depth、chroma 均真生效） |
| apng | Ffmpeg | ✅ OK（fps/loop/scale-w、pred、dpi） |
| tiff | Ffmpeg | ✅ OK（compression_algo、dpi、bit-depth） |
| webp | Ffmpeg | ⚠️ preset OK；compression_level 被 preset 覆盖（D-5，已播报） |
| gif | Ffmpeg | ✅ OK（palette/dither 真改字节） |
| jxr | Jxr / Ffmpeg | ✅ 真跑正常（真 JxrEncApp、真 jpegxr）；**dry-run 预览失实**（D-1）。详见「未验证」注 |
| dng | Dng / Ffmpeg | ✅ bit-depth/linear/effort/compression 真生效；highlight-mode 为工具侧无效果（N-1）；dry-run 预览漏参数（D-1） |
| jxl | Cjxl / Ffmpeg | ✅ effort/modular/progressive/photon-noise 两侧均正确；preserve-ultrahdr 正确不发（N-2） |
| jpg | Cjpegli / Ffmpeg | ⚠️ chroma/progressive 真生效；huffman/dct 在 mjpeg 上 OK；**cjpegli-backend / cjpegli-psnr 为死键**（D-4） |
| avif | Ffmpeg(libaom 默认) | ⚠️ 默认路线 OK；**切 libsvtav1 后 aom 参数残留且静默无效**（D-2）；10 个面板选项无 CLI 键（N-7） |

### 「未验证」的诚实声明
- **`av1_nvenc` / `av1_qsv` / `av1_amf` / `av1_vaapi` 四个硬件 AVIF 后端**：本机无对应硬件，
  按 `EncoderDetectionService` 的可用性判定属不可用，**无法实测**，不作任何生效性推断。
- **`mjpeg_nvenc` / `mjpeg_qsv` / `mjpeg_amf` / `mjpeg_vaapi`、`png_vaapi`、`av1_mf` / `av1_vulkan` / `av1_d3d12va`**：
  同上，未验证。
- **`--jpeg-gain-map*` 系列（Ultra HDR）**：本次未覆盖到命令行取证，**未验证**。
- **`librav1e`**：本机 ffmpeg 未编译该编码器，未验证。

---

## 6. 复现整套取证的入口

```powershell
$rd = "C:\PLAN\ffmpegPictureUI\tests\output\_ramdisk"
New-Item -ItemType Directory -Path $rd -Force | Out-Null
$env:TEMP = $rd; $env:TMP = $rd
$env:FFMPEGGUI_RAMDISK = $rd
$env:FFMPEGGUI_PLAN_DIR = "C:\PLAN\ffmpegPictureUI\publish\PLAN"
$amat = "C:\PLAN\ffmpegPictureUI\tests\output\_advmatrix"
$exe  = "C:\PLAN\ffmpegPictureUI\src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe"
$ff   = "C:\PLAN\ffmpegPictureUI\publish\PLAN\ffmpeg-full\ffmpeg.exe"

# 素材
cmd /c "`"$ff`" -y -hide_banner -f lavfi -i `"testsrc2=s=320x240:d=1`" -frames:v 1 `"$amat\src\src8.png`" > nul 2>&1"

# 关键复现（D-2）
cmd /c "`"$ff`" -y -hide_banner -i `"$amat\src\src8.png`" -c:v libsvtav1 -crf 16 -cpu-used 4 `"$amat\av1probe\r.avif`" > `"$amat\logs\repro_d2.txt`" 2>&1"
Get-Content "$amat\logs\repro_d2.txt" | Select-String "has not been used"

# 关键复现（D-1）
cmd /c "`"$exe`" --headless --dry-run -i `"$amat\src\src8.png`" -o `"$amat\out`" -f dng --dng-bit-depth=8 > `"$amat\logs\repro_d1.txt`" 2>&1"
```
原始日志目录：`tests/output/_advmatrix/`（`logs/` 117 个 txt + `av1probe/` `webpprobe/` `dngprobe/` `jpegprobe/` `jxlprobe/` `pngprobe/`）
