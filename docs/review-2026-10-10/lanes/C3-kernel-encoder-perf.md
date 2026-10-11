# C3 — 进程内内核吞吐 + 外部编码器旋钮性能账（HEAD=c18dc4a，只读审查）

判据前提：性能优化不改产物（含标签/精度）；不以降级换性能。本报告每条成本都标了**实测 / 静态结构 / 代码自估**，
自估一律不写成读数；"没有 X"类结论一律配正向命中（§1）。

---

## 0 一页结论（三档分开）

### 0-A 实测档（读数全部指到日志/探针输出，可复核）

| # | 读数 | 来源（必指到日志） | 条件（自带，过期重跑） |
|---|---|---|---|
| 实1 | **表驱动快路径**：8.8 MP 单核 55.9–63.9 MPix/s；dop=20 达 376–475 MPix/s | `tests/output/_baseline_modes/bench_rep1.txt`（同 rep2） | Release、cores=20、SyntheticFrame16、best-of-3 取 min、2026-10-07 04:49 |
| 实2 | **解析式（无表）单核 = 6.0–9.9 MPix/s**；8.8 MP 帧 dop=1 用 **1358.2 ms** | 同上（每格尾行 `参考（解析式单核）= …`） | 同上 |
| 实3 | **`PQ→sRGB` 那格快路径增益 = 1.0×**（表完全没生效），并且 **FAIL 单核≥40 MPix/s（实 6.5 / 7.7）** | 同上：`FAIL PQ→sRGB(变曲线+裁切) 单核 ≥40MPix/s` | 同上；该基线整体 `pass=9 fail=2` |
| 实4 | 端到端 2K PNG sRGB→P3：**H1(引擎三进程+内存变换)=272 ms vs H2(zscale 单进程)=171 ms = 1.59×** | `bench_rep1.txt` 倒数第 4 行 | 同上，门槛 `H1 ≤ H2×2.0` 仍绿 |
| 实5 | 随包 ffmpeg 的 **ppm/pam 编码器私有选项为 0 项**（`-h encoder=ppm` 只行列能力与像素格式）；带 `-compression_level 0` 写 PPM ⇒ rc=0、有产物 ⇒ **确认 no-op 且零成本** | 本轮只读探测（`publish/PLAN/ffmpeg-full/ffmpeg.exe -h encoder=ppm/pam`）+ 一次 32×32 落盘试写（产物已删） | HEAD=c18dc4a、同包 ffmpeg |
| 实6 | **cjpegli 根本没有 `--num_threads`**：`-h -v -v`（全量帮助）选项只有 `-d/-q/--chroma_subsampling/-p/--xyb/--std_quant/--noadaptive_quantization/--fixed_code/--target_size/--num_reps/--quiet/-v/-x/--disable_output` | 本轮只读探测（`publish/build/…/PLAN/jxl/bin/cjpegli.exe -h -v -v`，46 行全量） | 该二进制 = 出货包内 cjpegli |
| 实7 | **cjxl 有 `--num_threads=N`**（-1=机器默认 / 0=单线程），`-e/--effort` 域 1..10、**默认 7** | 本轮只读探测（`cjxl.exe -h -v -v`） | 同上 |
| 实8 | **avifenc `-j` 默认 `all`、`-s` 默认 6（0..10）** | 本轮只读探测（`publish/…/PLAN/artifacts/avifenc.exe --help`） | avifenc 1.4.2 系 |
| 实9 | **libaom-av1：`-cpu-used` 默认 1（0..8）、`-row-mt` 默认 auto、`-still-picture` 默认 false**；`libwebp`/`ppm`/`pam` 线程能力 = **none**；`mjpeg` = frame+slice；`jpegxl` 的 `-effort` 默认 7 | 本轮只读探测（`-h encoder=libaom-av1/webp/mjpeg/jpegxl/png/ppm/pam`） | 同包 ffmpeg |
| 实10 | **`av1_vaapi` 在随包构建里确实不存在**（`Codec is not recognized`），而 **`av1_d3d12va` / `av1_vulkan` / `av1_mf` 确实存在**（`-encoders`） | 本轮只读探测（`-h encoder=av1_vaapi` / `-encoders`） | 同包 ffmpeg；`ImageEncoderArgs.cs:362-364` 的文档**未过期** |
| 实11 | 环境级阻塞**仍在**：ffmpeg 写 `%TEMP%` ⇒ `Permission denied` / `Error muxing a packet`（本轮撞到两次） | 本轮探测（写 `/tmp` = 用户 Temp 被拦） | 复验 10-09 §6；⇒ **引擎路/GainMap 路的计时批次必须把输出指到仓外可写卷**，本 lane 不做计时 |

### 0-B 静态结构档（算术/计数，不含任何时间假设）

| # | 结构事实 | 出处 |
|---|---|---|
| 静1 | 每任务建表 **2 张 × 384 KB = 768 KB 全部落 LOH**（float[65536]=256 KB + ushort[65536]=128 KB），并做 **262,144 次曲线求值（PQ 类 ≈524,288 次 pow）**；跨任务/跨通道/跨位深**零缓存** | `ColorKernels.cs:456-463` + `RawColorPipeline.cs:1085-1091` |
| 静2 | 引擎 8 MP 单张的**盘上流量 = 199 MB**（解码写 49.77 + 变换读 49.77 + 变换写 49.77 + 编码读 49.77）；HDR 实测峰值路再多一遍读 ⇒ **249 MB** | `RawColorPipeline.cs:132-134, 1105-1125, 1183-1204` |
| 静3 | 解析式主循环每像素 = **6 次曲线求值（含 pow）+ 3 次 double 除法 + 3 次 float→double 往返**；表路每像素 = 6 次查表 + 0 pow | `ColorKernels.cs:543-548, 585-587` vs `:512-526` |
| 静4 | **8-bit 主力出口的 `EncodeTo8` 无条件先算一次 `curve.FromLinear()`（=1 次 pow），随后才判断有没有表** ⇒ 表路下每像素白付 3 次 pow | `ColorKernels.cs:775-781`（见 §3 C3-2） |
| 静5 | 表快路径被 4 个条件整体关掉：`LinearScale≠1` / tonemap 激活 / HLG 源 / GMO 开启 ⇒ **PQ/HLG 的 LinearScale 恒 ≈49.3 或 ≈4.9，永远 ≠1** ⇒ 所有 HDR 任务必走解析路 | `ColorKernels.cs:503-504`；`DiagnosticsGroup.cs:104`（`LinearScale = Nits(src)/sdr`） |
| 静6 | 流式档（帧 >160 MB）**完全串行**（`for` 循环，`sched.RunBands` 不参与），且 tile 按 256 KB 工作集算行数 ⇒ 8192 宽时 **5 行/块**，块数 = h/5 | `RawColorPipeline.cs:1147-1177` + `ColorScheduler.cs:35,29,61-68` |
| 静7 | GainMap 的 SIMD 编码出口按 **128 像素**分块，每块每通道各跑一次 `FloatToSrgb8` ⇒ 8 MP ≈ **194,400 次调用**，每次含 2 个长度校验 + 1 次设置读取（`AppSettingsService.Current`）+ 1 次 `Avx2.IsSupported` | `GainMapEncoder.cs:396-411`、`SimdPixelOps.cs:77-88`、`SimdKernelRouting.cs:62` |
| 静8 | `ColorMapping/` 目录 **0 处 SIMD、0 处 stackalloc、0 处 GCHandle**（正向控制见 §1）⇒ 产品逐像素内核连"数组固定"都没有，全靠边界检查 | 全目录 grep |
| 静9 | `TransformRgba8` / `TransformRgba`（float 路）**产品内 0 调用者**，只有 `tests/core/Modes/contract.cs`、`tests/diagnostics/selftest`、`tests/color/hlgwire` 在用 ⇒ `BuildEncodeLut16` 那份成本不在产品热路上（注释"供预览与缩略图"**已过期**） | grep 调用者 |
| 静10 | 引擎的两条 ffmpeg 命令 **不含任何线程参数**（`-threads`/`-filter_threads` 在 `Services/ColorMapping/` 0 命中），而传统管线无条件发 `-threads` | `FfmpegCommandBuilder.cs:99-100`（正向）vs §1 反向命中 |

### 0-C 代码自估档（**不许当读数**，只是作者留下的量级直觉）

| # | 原话 | 出处 | 与本轮证据的关系 |
|---|---|---|---|
| 估1 | "2×65536 次 pow ≈ 几 ms" | `RawColorPipeline.cs:1082` | 静1 把次数算实了（每任务 262,144 次，不是 131,072）；"几 ms" 仍是自估 ⇒ 见 §4 G2 |
| 估2 | "消除每像素 6 次 pow（实测单核 8.8→预计 50+ MPix/s）" | `ColorKernels.cs:445` | 实1/实2 显示真值是 6–10 → 56–127 MPix/s，**预计偏保守但方向对** |
| 估3 | SimdPixelOps 头 2.5×(H1)/慢 2×(H2)/1.9×(H3)/6.7×(H4) | `SimdPixelOps.cs:10-16` | 2026-08-15、**4096 像素**样本；样本比产品帧小 2000 倍 ⇒ 不能外推到 8 MP |
| 估4 | "我方 CPU 表驱动内核实测基准：单核 232 MPix/s、20 核 1100 MPix/s" | `tests/scripts/_probe-gpu-stage1.ps1:48` | 与实1（55.9–127）**不同口径/不同夹具** ⇒ 两者不能互换引用，需 §4 G1 统一 |
| 估5 | "同变换 CPU ≈7.5 ms（20核）/ 128 ms（按 68 MPix/s 估）" | `_probe-gpu-stage3.ps1:50` | 同上 |

### 0-D 收益排序（只按证据档位排，不承诺数值）

1. **C3-1（实测已红 + 静态可推 + 修法位精确）**：把"查表解码"与"解析编码"拆开 ⇒ 恢复 HDR/GMO/tonemap 路的解码侧快路径，8.5–12.8× 是实测上限区间（实1 vs 实2）。
2. **C3-2（静态确定 + 需实测幅度）**：`EncodeTo8` 的无条件下游计算 ⇒ 表路每像素省 3 次 pow。
3. **C3-4 建表缓存 / C3-3 峰值二次读 / C3-6 cjxl PNG 中转不压缩**：都不改产物，都是每任务固定成本。
4. **C3-8 AVIF GUI/headless 参数面不对称**：先证明是性能还是产物差，再谈改。
5. **C3-9 GPU**：只登记可达性与判据；本轮**不给任何收益承诺**。
6. **C3-11 门禁**：现有 bench 有门槛但恰好漏掉产品在跑的那条路（实3 的 FAIL 就是证据）。

---

## 1 方法与正向命中自证

只读审查：未改任何源文件；无 git 写操作；无计时批次（§0-A 的实5–实11 是**能力探测**：`--help` / `-h encoder=` / `-encoders`，不测时间）。
唯一一次落盘试写（32×32 PPM）写在 `tests/output/_c3probe.ppm`，**已删除**，`git status` 除 lane C2 自己的 3 个 `tempc2probe*.txt` 外无我造成的改动。

"没有 X" 类结论的正反控制（同一条 grep 里一起做）：

| 断言 | 正向命中（同模式必有输出） | 反向命中（被审面 0 行） |
|---|---|---|
| `ColorMapping/` 里没有任何 SIMD/intrinsic | `grep -rln "System.Runtime.Intrinsics" Services/` → **4 个文件**：`CpuFeatureService.cs`、`PsnrCalculator.cs`、`SimdKernelRouting.cs`、`SimdPixelOps.cs` | `grep -rn "System.Runtime.Intrinsics\|IsSupported\|Vector<" Services/ColorMapping/` → **(above = full list)，0 行** |
| `stackalloc` 在产品像素路径稀缺但不为 0（避免误报） | `GainMapEncoder.cs:396-397`（`Span<float> rBuf = stackalloc float[512]`） | `Services/ColorMapping/` 目录 0 处（§0-B 静8） |
| 引擎两条命令没发线程参数 | `FfmpegCommandBuilder.cs:99 args.Add("-threads")`、`RawService.cs:178/251` | `Services/ColorMapping/` 内 `threads` 仅 1 处**注释**（`ImageEncoderArgs.cs:52`），0 处命令行 |
| 全仓没发 `-filter_threads`/`-filter_complex_threads` | `FfmpegCommandBuilder.cs:99`（同族参数存在且被发） | Grep 工具全 `src/`：`-filter_threads\|-filter_complex_threads\|-filter_thread_type` → **No matches found** |
| 产品里没有任何 hwaccel/hw 帧通路 | `GpuCapabilityService.cs:181/227`（`-hwaccels`、`-init_hw_device` 探测确实在发） | 全 `src/FfmpegGui`：`hwaccel\|init_hw_device\|program_opencl\|hwupload` 命中 7 行，**全部落在 GpuCapabilityService.cs 的探测里**，管线文件 0 命中 |
| 曲线表没有任何缓存 | `ColorTransformPlan.cs:623 MatrixCache`（同类缓存确实存在） | `BuildTables` 调用者仅 1 处 = `RawColorPipeline.cs:1090`，无字典/无 TTL |

行号归属核对：本报告所有 `文件:行` 均在 HEAD=c18dc4a、工作树干净的前提下按**当前文件内容**逐行读出的
（`RawColorPipeline.cs` 1348 / `ColorTransformPlan.cs` 1072 / `ImageEncoderArgs.cs` 1032 / `ColorKernels.cs` 876 / `SimdPixelOps.cs` 414 / `TransferCurve.cs` 437 / `CjxlService.cs` 606 / `CjpegliService.cs` 318 / `ExifToolService.cs` 1463 / `GpuCapabilityService.cs` 510 —— 与任务书给的行数一致，说明行号基准未漂移）。

---

## 2 与 2026-10-09 P6/P7 的复验表

| 10-09 的账 | 现状 | 标签 | 我这边的更正 |
|---|---|---|---|
| P7：`format=rgb48le` 恒挂（旧 `:99`）**改变产物 ⇒ 不能当性能项删** | 代码未动：`RawColorPipeline.cs:99` `string vf = scaled ? $"format=rgb48le,{scaleFilter}" : "format=rgb48le";` | **未复现（结论仍成立）** | 字节代价现在算得实：8 MP = **49.77 MB/帧**（`w*h*6`），比 rgb24 多 **24.88 MB**；但删它会改产物 ⇒ 保持只记账。**新增可动的部分在别处**：同一帧的盘上往返共 4 趟（静2），其中 3 趟与 rgb48le 无关（§3 C3-3/C3-5） |
| P7：YUV 目标恒插一次 `scale`（旧 `Decision.cs:377-378`） | 原样：`var needsEven = outMatrix == "bt709" \|\| outMatrix?.StartsWith("bt2020") == true;` / `var preScale = needsEven ? "scale=ceil(iw/2)*2:ceil(ih/2)*2:flags=lanczos," : "";` | **未判定(需 A/B 产物哈希)** | 登记的"改产物"对**奇数尺寸**成立；**偶数尺寸**下 `ceil(iw/2)*2 == iw` ⇒ scale 目标尺寸与源相同，理论上退化成一次纯格式搬运（不重采样）。若实测证明偶数尺寸逐字节 no-op，则"偶数尺寸不插 scale"是**纯性能项**（省一整趟 swscale）。配方见 §4 G-偶 |
| P7：`-compression_level 0` 对 ppm/pam 是 no-op | 原样：`RawColorPipeline.cs:598`（pam）与 `:604`（ppm） | **未复现（no-op 成立），且成本 = 0** | 实5：`-h encoder=ppm/pam` 私有选项 **0 项** ⇒ 该项连发错都算不上（通用 `AVCodecContext.compression_level`，编码器不读）。⇒ 它**既不是性能项也不必保留**，属"命令行里一句不兑现的话"。反向发现见 **C3-6**：真正该发 `-compression_level 0` 的地方（cjxl 的 PNG 中转 `:561-563`）**没发** |
| P7：曲线表 `BuildTables` 每次 384 KB / 2×65536 次 pow、自估"几 ms"、25 万像素门槛 ⇒ 未测出量级前不排名 | 原样：`TransferFor`… `RawColorPipeline.cs:1087 if (!spec.ClampLinearBeforeEncode \|\| pixels < 256_000) return (null, null);` | **可排名了（部分）**：建表次数被算实（静1：每任务 2 曲线 = 262,144 次求值），但**时间仍是自估** | 门槛本身可以算：用实1/实2 的**实测速率**反推 break-even ≈ **8 万**像素（若建表 10 ms）/ **4 万**（若 5 ms），而现门槛是 25.6 万 ⇒ 门槛偏高（见 §4 G2 拿读数）。**更干净的修法不是调门槛而是缓存**（C3-4），那样门槛可以降到 0 而不引入固定开销 |
| P7：全帧缓冲不进池是**有意的正确性取舍**（旧 `:1134-1138`） | 代码未动：`RawColorPipeline.cs:1138-1144` | **决策：尊重，不作为优化目标**。但**理由待复验** | `BufferPool.RentByte` 第一层已经处理了超限：`BufferPool.cs:20-21` `=> length > MaxPooledLength ? new byte[length] : ArrayPool<byte>.Shared.Rent(length)` ⇒ "请求超限会拿到较小数组"这个危险在下一层已被挡住，`RentSmart` 的 4 MB 阈值是**第二层独立策略**（不池化大数组），不是正确性必需。标签：**未判定(需 1 行 Runtime 探针 `ArrayPool<byte>.Shared.Rent(300MB).Length`)**。结论不变，只是别把"防越界"当理由写在决策前 |
| P6：落盘中转的可管道化点（引擎路本机不可测 ⇒ 只登记） | 阻塞仍在（实11：ffmpeg 写 Temp 被拦） | **未复现（阻塞事实成立）** | 引擎路其实**不需要**Temp 也能记账：静2 的 199 MB/张是纯算术，不依赖计时。§4 的配方把中间件/输出都指到仓外可写卷（10-09 已警告：指进仓库树内会挂死 47 min，别再用那招） |
| P4：引擎路径不吃线程参数；`-filter_threads` 本仓一处都没发 | 原样（§1 正反控制各一条） | **未复现（结论成立）** | 滤镜图形状侧我补一条：解码段滤镜只有 `format=rgb48le[,scale]`（1–2 节点），**编码段最重**：alpha 出口是 `[1:v]scale,format,alphaextract;[0:v]format;alphamerge,format` 4–5 节点（`:594-595`、`:1057-1058`），且第二输入是**原文件 ⇒ 等于把源再解码一遍**。这不是线程问题，是"一次能完成的事排成两趟"，见 C3-7 |
| §4 被否证候选（PsRenderService 睡 1 s / QueueItem.Log 平方 / 切 cjpegli 提速） | 不复述、不复活 | — | 本轮顺带一条**支持"cjpegli 不是提速选项"的新证据**：实6 证明它连 `--num_threads` 都没有 ⇒ 单进程单线程，多核收益只能来自队列并发（交 C2） |

---

## 3 新发现

### C3-1 表快路径被 4 个条件整体关掉，HDR/GMO/tonemap 路跑 6-pow 解析路 —— 【性能项】（且现门禁已红）

`ColorKernels.cs:503-504`：

```csharp
bool fast = eotf != null && oetf != null && Math.Abs(scale - 1.0) < 1e-12 && !toneOn
            && !spec.SrcIsHlgScene && !gamut;
```

PQ/HLG 的 `LinearScale` 恒为 `Nits(src)/203`（`DiagnosticsGroup.cs:104`，与生产 `ToSpec()` 同口径）= **49.26 或 4.93**，
永远不等于 1 ⇒ **一切 HDR 任务连查表解码都用不上**。实测证据是实3：`PQ→sRGB` 那格 `快路径增益 = 1.0×` 并 **FAIL**。
GMO 开启（广→窄压缩）与 tonemap 激活同理（`:495-504`）。

**修法（分两半，边界要说清）**：
- 位精确那半：解析循环 `ColorKernels.cs:546-548` 与建表 `:459` 是**同一个表达式**——

```csharp
:459   for (int i = 0; i < 65536; i++) t.Eotf[i] = (float)curve.ToLinear(i / 65535.0);
:546               float r = (float)srcC.ToLinear(s[o + 0] / 65535.0);
```
`Eotf[code]` ≡ `(float)ToLinear(code/65535.0)` ⇒ 把解析路的**解码**换成查表，逐位相同（随后 `r *= (float)scale`、
OOTF 的 `double nr = r` 都不变）。⇒ 每像素省 3 次 pow + 3 次 double 除法，**不改产物**，覆盖 `scale≠1`/`srcHlg`/`toneOn`/`gamut` 全部四条路。
HLG 也一样：`MeasurePeakLinearRgb48` 只是因为 OOTF 需要三元组才把**整张表**禁用（`:610`），曲线侧本身仍是逐通道的。
- 会改产物那半：编码侧换成 `EncodeLerp(oetf, x)`（`:585-587` → `:821-830`）**不是**位精确 —— 插值误差 ~0.1 LSB@16bit。
  今天 SDR 路已经在用它，但把 GMO/tonemap 路从解析式切过去 = 改产物 ⇒ **需授权**，不能算性能项。

预期收益量级：上限 = 实1/实2 之比（**8.5–12.8×**，8.8 MP dop=1 1358 ms → 23.5–45 ms 级），只换解码侧大概吃到其中一半上下 ⇒ **必须实测**（§4 G1）。
判据：换前后 rgb48 输出**逐字节全等**（不是 PSNR 阈值），吞吐 best-of-3。

### C3-2 8-bit 主力出口的 `EncodeTo8` 先算 pow 再决定用不用表 —— 【真缺陷-静默浪费(性能项)】

`ColorKernels.cs:775-781`：

```csharp
private static byte EncodeTo8(ushort[]? oetf, TransferCurve curve, float linear, bool clamp, float t8)
{
    double e = clamp ? Math.Clamp(curve.FromLinear(linear), 0.0, 1.0) : curve.FromLinear(linear);
    double code16 = oetf != null ? EncodeLerp(oetf, linear) : Math.Round(e * 65535.0);
```

`oetf != null`（表路）时 `e` 算完就丢 ⇒ 每像素白付 **3 次 `FromLinear`（各含 1 次 pow + 1 次除法 + 1 次 Round）**。
而 `TransformRgb48leToRgb8` 是 8-bit 出口的主力（`RawColorPipeline.cs:1117/1167`），jpg/png/webp/gif 的 8-bit 交付全走它。
`FromLinear` 是带 5 分支 switch 的 struct 方法、内含运行时指数 `Math.Pow` ⇒ 内联/消除的可能性低（**静态结构观察**，幅度待测）。
修法：把 `double e = …` 挪进 `oetf == null` 分支（等价改写，**逐位不改产物**）。测法：§4 G1 的第 4 臂。

### C3-3 HDR 峰值实测把同一个 50 MB 中间件读第二遍 —— 【性能项(可融合)】

`RawColorPipeline.cs:163-173`：`MeasureContentPeakNitsAsync(tmp, …)` 先整趟顺序读 `tmp` 求峰值（`:1191` 开新 FileStream），
随后 `TransformWholeFrameAsync` 又把同一个 `tmp` 整趟读进 `buf`（`:1105-1108`）。
峰值必须先于变换知道（`spec.Tone.PeakNits` 喂内核）⇒ **依赖是真的，但重复读盘不是**：整帧模式下第二次读完全可以
从已在内存里的 `buf` 扫（或第一趟把 `buf` 读满后就地扫）。代价：**每张 HDR 图多 49.77 MB 磁盘读**（静2 的第 5 趟）。
`out8` 之外不改产物（纯消除 IO）。注意：流式档（tier L）不能这么合，内存受控是它的目的（`ColorScheduler.cs:35`）。

### C3-4 曲线表每任务重建，零跨任务/跨通道/跨位深缓存 —— 【性能项】

`BuildTables` 全仓唯一调用点是 `RawColorPipeline.cs:1090`，且 `TablesFor` 每任务调一次、一次建 src+dst 两张
⇒ 1000 张同曲线批 = **768 MB LOH 流水 + 2.6 亿次 pow**（静1 的算术 ×1000）。
可缓存性判定：**能**，键 = `(曲线等价类, 位深)`；`TransferCurve` 是 `readonly struct` 且已自带等价判据
`Equivalent(other)`（`TransferCurve.cs:364-378`，容差 1e-4）⇒ 与 `ColorTransformPlan.cs:623 MatrixCache` 同形状即可（已有先例，不是新发明）。
表内容纯函数、只读 ⇒ 不改产物。**注意**：`Equivalent` 的 1e-4 容差用来当缓存键会把"两个不同 ICC para 曲线"并成一张表
⇒ 缓存键建议用参数字段的**精确**组合（或 `ReferenceEquals` 对已注册常量），别复用那把为比较而放宽的尺子。
顺带：有了缓存，`pixels < 256_000` 那把门槛（`TransferFor`… `:1087`）的立论（固定开销摊不平）基本消失。

### C3-5 两兄弟内核的快路径判据不对称，8-bit 主力出口在 bench 里没有格子 —— 【性能项 + 门禁缺口】

16-bit 路把 `gamut` 当作"必须放弃表"的条件（`:503-504`），而 8-bit 路只在
`scaleOn || toneOn || encScaleOn || srcHlg || dstHlg` 时放弃表（`:696-698`）——**gamut 不在其中**，
因为它只有一个循环、表只作用在解码/编码两端。⇒ 同一张 GMO 任务，8-bit 出口在查表、16-bit 出口在跑 6 pow。
这条不对称正是 C3-1 的解法形状（把 16-bit 也拆成"两端可表、中间必解析"）。
同时 `bench.cs` 只测 `CK.TransformRgb48le`（`:120/:134`），**完全没测 `TransformRgb48leToRgb8`**，
而后者才是默认 8-bit 交付的主力 ⇒ 主力出口吞吐**无人看守**。

### C3-6 cjxl 出口的 PNG 中转发出了**全量 zlib 压缩**，而同仓别处已有"中转用 `-compression_level 0`"的先例 —— 【性能项】

`RawColorPipeline.cs:561-563`（无 `-compression_level`）：

```csharp
var ffPngArgs = $"-y -hide_banner -loglevel error -f rawvideo -pix_fmt {pixIn} -video_size {w}x{h} "
    + $"-i \"{rawPath}\" -i \"{inputPath}\" -map 0:v -map_metadata 1 "
    + $"-vf \"format={ppmPix}\" -f image2 \"{pngTmp}\"";
```

对照 `QueueProcessor.cs:833/849`：那里的 PNG 中转就写了 `-compression_level 0`。
PNG 是**无损**容器 ⇒ zlib 级别只改文件大小与耗时，**像素与 cjxl 读回完全相同** ⇒ 交付的 JXL 逐字节不变（判据就是这条）。
16-bit 8 MP 中转 PNG ≈ 50 MB 样本喂给默认级别（`avctx->compression_level<0 ⇒ Z_DEFAULT_COMPRESSION`），
这张图中转**只活几毫秒**。收益 = 一次 50 MB 级 zlib 编码，必测（§4 G6）；量级我不敢报数。
（注：这条不违反 P7 的"ppm/pam 上 `-compression_level 0` 是 no-op"——那是 ppm/pam，实5 已定案；这里是 png。）

### C3-7 alpha 出口把源文件再解码一遍，只为一维 alpha —— 【性能项(形状)】

`:594-595`（cjxl）/`:1057-1058`（png/tiff/webp）/`:734-735`（jxr）三处同型：
`[1:v]{scale,}format=…,alphaextract[a];[0:v]format=…[b];[b][a]alphamerge`，第二输入 = **原文件**。
注释自己承认代价（`:917` "GIF 任务多一次源解码（单帧，实测可忽略）"—— **代码自估，且只对 GIF 测过**）。
形状上这是"能一步完成的事排成两趟"：解码段本来就能把 alpha 一起带出来（`-vf format=rgba64le` 而非 rgb48le），
代价是主通路要多一个平面。**会不会改产物：会**（rgb48le 主通路是 P7 已登记的产物相关项）⇒ 只登记，不排名，交主循环决定要不要开这条格。

### C3-8 AVIF 的 `-still-picture` / `-row-mt` 只在 GUI 侧有默认值，CLI/headless 恒 null ⇒ 同一张图两种编码形状 —— 【未判定(需 A/B 产物哈希 + 墙钟)】

`ImageEncoderArgs.cs:652-661` 两支都要求**有值才发**：

```csharp
else if (o.AvifStillPicture == true) { args.Add("-still-picture"); args.Add("1"); }
if (o.AvifRowMt == true)             { args.Add("-row-mt"); args.Add("1"); }
```

GUI 恒给 true（`MainWindow.xaml:600-601` `IsChecked="True"`；`MainWindow.xaml.cs:2933-2934` `useAdvCodec ? … : true`），
而 `FfmpegOptions.AvifStillPicture/AvifRowMt` 是 `bool?`（`:294-295`）、CliParser 只在用户显式给 `--avif-still-picture` 时赋值
⇒ headless 默认**一个都不发** ⇒ libaom 落回自己的默认（实9：`-still-picture` 默认 **false**、`-row-mt` 默认 **auto**）。
性能与产物都可能差 ⇒ 必须 A/B（§4 G5）。这条**不是**性能许愿：先证明差在哪一侧，再决定是"补默认值"还是"改 GUI 默认"。
同族小账：`avifenc` 侧 `-s` 取 `AvifCpuUsed ?? 5`（`QueueProcessor.cs:3841`）而 avifenc 自己的默认是 **6**（实8）⇒ 比上游默认慢一档；
且 `AvifCpuUsed` 一个字段同时当 libaom 的 `0..8`（`ImageEncoderArgs.cs:642`）和 avifenc 的 `0..10` 用 —— 域不同名同一把旋钮（**跨后端旋钮语义复用**，性能面只登记，域正确性归 C1/A 侧）。

### C3-9 GPU：产品里 0 条 hw 帧通路，检测表比随包构建少 3 个编码器 —— 【可达性登记，不承诺收益】

- 全仓 `hwaccel|init_hw_device|program_opencl|hwupload` 的 7 处命中**全在 `GpuCapabilityService` 的探测命令里**（`:181/:227`），
  管线文件 0 命中 ⇒ 引擎与传统管线**永远只喂 CPU 帧**。`_probe-gpu-stage1/2/3.ps1` 是 OpenCL `program_opencl` 吞吐探针，
  产品从未接线（stage1 头部注释自己写着"program_opencl 只能逐平面访问…无法一次表达跨通道 3×3 矩阵"）。
- `GpuCapabilityService.cs:270-282` 的 GPU 编码器表只有 8 项（mjpeg/av1 × qsv/nvenc/amf/vaapi）。
  实10：随包 ffmpeg **有 `av1_d3d12va`、`av1_vulkan`、`av1_mf`**、**没有 `av1_vaapi`** ⇒ 表里 `av1_vaapi` 恒判 `NotCompiled`（正确），
  但**存在的 3 个 Vulkan/D3D12/MF AV1 编码器从不被检测、也从不被 `ClassifyAvifBackend` 认得**（`ImageEncoderArgs.cs:385-393` 落 `AvifBackend.Other`）
  ⇒ 面板的"GPU 可用"与"这个后端能不能收这些参数"两处都少了一块。设备探测里有 `vulkan`（`:209`）却没有 `opencl`/`d3d12va` ⇒ 探测与消费不对称。
- 判据（不给收益承诺）：**任何 GPU 结论必须在那台机器上先证明"帧进得去"** —— 一条最小可跑：
  `-init_hw_device d3d12va -f rawvideo … -i raw -vf hwupload -c:v av1_d3d12va`（或 vulkan 同形）能否 rc=0 且产物能被解回。
  无 HDR/无独显机器上跑不出来只能记"不可达"，不得写成"GPU 没收益"，也不得写成"有收益"。
- 顺带一条启动侧结构观察（**静态**，非每图成本）：`DetectAsync` 串行起 **1 次 `-hwaccels` + 5 次 `-init_hw_device` + 1 次 `-encoders`**
  = 7 个 ffmpeg 进程（`:181-210, :328`），且 `MainWindow.xaml.cs:1301` 是 `await`（在"[detect] 全部检测完成"之前）；
  运行时验证 `ValidateEncodersAsync` 已甩到 `Task.Run` 后台（`:1326-1340`），每个候选编码器最多 8 s（`:366`）⇒
  **可能与首批图像任务抢 CPU**，归因时要把这段后台活动隔离（共享机器读数先归因）。

### C3-10 NativeAOT：SIMD 选路用的是真硬件位，但有一条基于误解的配置 —— 【文档过期/误解】

- 选路面（正确）：`SimdKernelRouting.cs:52-55` 用 `Avx512BW.IsSupported / Avx2.IsSupported / Sse2.IsSupported` 三级真检测，
  `:62` 的 `FloatToSrgb8UsesSimd` 同；判定**不在热循环内**（每缓冲一次，但被 C3-7 那条 128 像素分块放大成每 128 像素一次 —— 仍非逐像素）。
  ⇒ 符合"『本机没有 X』不是设计依据"。任务书点名的 `Vector512.IsHardwareAccelerated` 形态在产品像素路径里**没有对应物**
  （因为像素路径根本没有向量化，§1 反向命中）；`Vector512` 只出现在 `PsnrCalculator.cs:220+`（验证工具，非产品热路）。
- 误解面：`FfmpegGui.csproj:94-99`

```xml
  <!-- NativeAOT 反射安全 (防止 System.Runtime.Intrinsics 被裁剪) -->
  <ItemGroup>
    <TrimmerRootAssembly Include="System.Runtime.Intrinsics" />
  </ItemGroup>
```

  NativeAOT 下 intrinsics 是**编译期**替换（`IsSupported` 读运行时特性位，代码本体不受裁剪影响），
  把整个 `System.Runtime.Intrinsics` 根起来是基于"会被裁剪"的旧假设 ⇒ 无害但**误导后人**（后人会以为删了它 SIMD 就失效）。
  同文件已有 `<IlcOptimizationPreference>Speed</IlcOptimizationPreference>`（`:63`），方向对。
  ⇒ 标签：**文档过期**（注释层面的误解），不是性能项；真要改的话改注释、别动行为。
- AOT 吞吐风险：**未判定(需一条 AOT 档 bench 读数)**。现 bench/`_baseline_modes` 的读数全部来自 `net11.0/win-x64` 的 **JIT 档**；
  AOT 档无 tiering/OSR，静态推断不出快慢 ⇒ §4 G9 要求同夹具在 AOT 档再跑一遍 bench 并对比表。

### C3-11 性能门禁现状：**有门，但门的覆盖面恰好绕开了产品在跑的那条路** —— 【文档过期 + 门禁缺口】

看守者：`tests/diagnostics/Modes/bench.cs`（`Program.cs:50`：不带 mode 时**默认必跑**）+ 方法论确实是 best-of-N 取 min（`:110-124`，注释还留了
"同一用例 2.9×/3.2×/5.2× 飘"的教训）。绝对门槛只有 3 条：

```csharp
:151   Check(single >= 40, $"{name} 单核 ≥40MPix/s（实 {single:F1}）");
:154   Check(best / Math.Max(1e-6, single) >= 4.0, $"{name} 多核扩展比 ≥4×（实 …）");   // 仅 ≥8MP
:74    Check(h1 <= h2 * 2.0, $"端到端 H1 ≤ H2×2.0 …");
```

盲区（逐条指到行）：
1. 门槛只打在**表驱动路**上；解析式只 `Console.WriteLine`（`:145`），`singleSlow`/`bestSlow` 算了但从不进 `Check`
   ⇒ 产品 HDR/GMO 路（C3-1）**无门槛**，而且现成的 2 条 FAIL 就挂在基线里（`PROBE RESULT: pass=9 fail=2`，实3）。
2. bench 绕过 `TablesFor`，直接 `CK.BuildTables` 在计时**之外**（`:104`）⇒ 产品每任务真付的建表成本**不在任何读数里**（C3-4）。
3. `TransformRgb48leToRgb8`（8-bit 主力出口）**没有格子**（C3-5）。
4. 盘上流量（静2 的 199 MB/张）、进程数、`PipeToEncoderAsync` 的管道 buffer（默认 `CopyToAsync`，未显式给 size，`:838`）
   —— 都不在任何门禁里。
5. 另有两处只**要求日志含 `MPix/s` 字样**（`verify-color-strategy.ps1:334/357`、`verify-color-wiring.ps1:124`）⇒
   那是"引擎真的跑了"的结构断言，不是回归阈值。

⇒ 正确表述（比"无人看守"精确）：**表驱动 SDR 直通路有人看守；HDR/GMO/8-bit/建表/IO 这五块没人看守，且已红的两格正是没人看守那块的边缘**。

### C3-12 `ColorKernels` 头部的设计理由与自家实测相反 —— 【文档过期】

`ColorKernels.cs:124`：` • 8-bit 源走 256 项表（L1 常驻）；16-bit 源走解析式 pow（避免 256KB 大表打爆缓存）；`
与实1/实2 直接矛盾（8.8 MP 上表路 55.9–127.1 MPix/s vs 解析 6.0–9.9，即"打爆缓存"的代价远小于省掉 pow 的收益），
也与已出货的 `CurveTables` 快路径（`:440-463`，正是 16-bit 定义域表）矛盾。⇒ 这句话现在**只剩历史价值**，
继续留着会让下一个优化 HDR 路的人拿它当"已评估过"的凭据。

### C3-13 内核里唯一的向量化收益点在 GainMap，而它的分块尺寸把收益吃回去一半 —— 【性能项(需实测)】

`SimdPixelOps.cs` 全类只有一个 SIMD 内核（`FloatToSrgb8Avx2`，`:101-151`），其余 H2/H3/H4 都是标量，
理由写在文件头（估3，2026-08-15、4096 像素样本）。产品里它的唯一调用者是
`GainMapEncoder.cs:407`，被 128 像素的分块（`:398-410`）切成三段 strided 拷贝（`*4+c` 取、`*4+dstC` 存）
⇒ 交错/去交错两趟标量 + 每次调用一遍校验与选路（静7）。
把 chunk 提到 4096–65536（`stackalloc` 上限内或走 `BufferPool.RentFloat`）**逐位不改产物**（每元素独立，无跨块状态；
但注意同函数里的 Bayer/相位不在这个函数内 ⇒ 无相位风险）。估3 的 AVX2 优势是在 4096 长度上测的，正好提示分块应该 ≥4096。
另外 `:413-414` 的 `raw[i*4+3]=255` 是独立一趟，可与主循环融合（同样不改产物）。

另需复验的一条历史结论：`SimdPixelOps.cs:15`（"教训: AVX2 gather 延迟高…"；另见 `:204`/`:235` 两处 `(gather …) 慢 N×`）
把"AVX2 不如标量"归因为 **gather 延迟**（不是访存不友好）。
⇒ 这条结论**从未在 8 MP 帧上复验过**（样本 4096 像素，估3），而它是"曲线不能向量化"目前唯一的挡箭牌。
对照臂已排进 §4 **G12**（对数表查表路 + 编译期常数 `MathF.Pow(x, 1f/2.4f)`）。

### C3-14 质量-速度旋钮默认值清点（png / webp / jxl / avif / jpegli）—— 结论：**没有"默认最高档"式的浪费，但有两处结构性无杠杆**

逐项按"默认发什么 / 上游默认是什么 / 差多少"清点（默认值=实9 探测；映射函数为静态结构）：

| 格式 | 产品默认发的 | 上游默认 | 判定 |
|---|---|---|---|
| png | `-compression_level MapPngCompression(Quality)`，`Quality` 默认 75（`FfmpegOptions.cs:147`）→ `(100-75)*9/100=2.25` → **2** | ffmpeg png 的 `compression_level` 未设 ⇒ zlib `Z_DEFAULT_COMPRESSION`(6) | **无浪费**：默认反而比上游快（级别 2）。但注意语义耦合：质量轴被同时当压缩轴用（`ImageEncoderArgs.cs:71`），PNG 是无损格式 ⇒ "高画质⇒高压缩⇒慢"这条耦合是**语义选择**不是性能缺陷，动它要授权（会改产物体积） |
| webp（有损） | 只发 `-q:v {quality}`；`-preset` 仅在用户选了时发 | `-quality` 默认 75、`-preset` 默认 none | **无杠杆**：实9 显示 ffmpeg 的 `libwebp` 只暴露 `lossless/preset/quality`，**没有 cpu-used/threads**，且 `Threading capabilities: none` ⇒ 单核。想提速只能靠队列并发（交 C2）或换 preset（改产物） |
| webp（无损） | `-lossless 1 -q:v 100 -compression_level {WebpCompressionLevel ?? 4}`（`ImageEncoderArgs.cs:157-161`） | libwebp 无损默认 config level 4 同族 | **无浪费**（默认 4 是中档），且 preset 冲突已出声（`:166`） |
| jxl（ffmpeg libjxl） | `-distance …`，`-effort` **仅在有值时发**（`:232/:238`） | `-effort` 默认 **7**（实9） | **无浪费**：不发=用上游默认 |
| jxl（cjxl 出口） | `-e {JxlEffort ?? 7}`（`CjxlService.cs:447-449`）恒发 | cjxl 默认 **7**（实7） | **无浪费**（发的就是它自己的默认，只是显式化）；`--num_threads` 仅在 `Threads>0` 时发（`:451-452`）⇒ headless 默认不发=cjxl 用机器默认多线程，**正确** |
| jpegli(cjpegli) | `--distance …`，无速度轴 | 实6：全量帮助里**没有任何速度/线程旋钮** | **无杠杆**；单进程单线程（结案 P4 的"是否支持 num_threads"） |
| avif(libaom) | `-cpu-used` 仅有值时发；`-still-picture`/`-row-mt` 仅有值时发 | `-cpu-used` 默认 1、`-row-mt` auto、`-still-picture` false | 默认值本身**不浪费**（cpu-used 1 非最慢档）；问题在 **C3-8 的 GUI/CLI 不对称** |
| avif(avifenc) | `-s {AvifCpuUsed ?? 5}`、`-j` 仅在 `threads>=2` 发 | `-s` 默认 **6**、`-j` 默认 **all**（实8） | **轻微浪费**：`-s 5` 比上游默认慢一档（`QueueProcessor.cs:3841`）；`-j` 不发=all 是快的一侧，`-j 1` 已被正确避开（`:3846-3851`，P7h 段错误规避成立）。另：`-s` 的 0..10 域与 libaom 的 0..8 共用同一字段（跨后端复用，见 C3-8） |
| mjpeg | `-q:v {MapJpegQuality}`；`-huffman/-dct` 仅在选了时发 | 实9：线程能力 frame+slice ⇒ 天然可并行 | **无浪费**；P5 那 1.4 s 差额仍只有总账（10-09 已判"先拆分再谈优化"，本轮不重复排名） |

⇒ 清点结论：这一项**没有**任务书设想的"默认 effort 最高档"式浪费；真正能省的是
① `avifenc -s 5→6`（若实测无产物差 ⇒ 纯性能，但**会改产物体积**的可能性高，须先 A/B），
② webp/jpegli 两条**结构性无杠杆**（别再往这上面排优化项），
③ C3-6 那条被漏掉的 `-compression_level 0`。

---

## 4 需要实测的格子（配方给主循环，安静机器 + best-of-3 取 min + 产物数守卫）

前置（两条硬要求，来自 10-09 §1/§5/§6 的教训）：
- 每个臂都带**夹具自检**（输入存在且非零）+ **产物数守卫**（不足记 `INVALID`，不当读数）+ **限时杀树**。
- **输出/中间件一律指到仓外可写卷**；实11 已复验 ffmpeg 写 `%TEMP%` 仍被拦；10-09 已警告"TMP 指进仓库树内 ⇒ 挂死 47 min"，别再用那招。
- 计时一律 `Start-Process -PassThru` + `WaitForExit`（`& $exe` 量到的是进程创建时间）。

| 格子 | 测什么 | 判据（含"改不改产物"） |
|---|---|---|
| **G1 内核四臂 A/B**（最高优先，解 C3-1/C3-2/C3-5/C3-13） | 在 `bench.cs` 的形状上加 4 臂：① 现解析路 ② **只换解码查表**（编码仍 `ToU16(FromLinear)`）③ 解码+编码都查表 ④ 现 8-bit 出口 `TransformRgb48leToRgb8`。用例至少含 `PQ→sRGB(scale=49.3)`、`HLG→sRGB+tonemap`、`P3→sRGB+GMO`、`sRGB→P3` | ①vs② **rgb48 逐字节全等** ⇒ ②是纯性能项；②vs③ 允许 ≤1 LSB@16bit ⇒ ③属改产物、须授权；④ 现在没有任何读数，先拿到基线。全部 best-of-3 |
| **G2 建表成本与 break-even** | 单条 `CK.BuildTables(curve)` 计时（Parametric / Pq / Hlg 三种曲线各测，best-of-9）⇒ 用实1/实2 的速率算 `pixels` 门槛该是多少；现门槛 256,000（`RawColorPipeline.cs:1087`） | 只读诊断，不改产物。产出：`表路 vs 解析路` 在 25K/50K/100K/256K 四档的交叉点 |
| **G3 跨任务表缓存** | 100 张同曲线批（`-j 1`），开关缓存比墙钟 + `GC.GetTotalAllocatedPools`/LOH 计数 | 产物 md5 必须全等（表是纯函数）⇒ 纯性能项。同时报每任务临时目录峰值字节（应不变） |
| **G4 HDR 峰值二次读消除** | 整帧模式下 `MeasureContentPeakNitsAsync` 扫内存缓冲 vs 现再开一次 FileStream，比墙钟与**盘读字节数**（PerfCounter 或 `fsutil` 侧量） | 峰值读数必须逐位相同（同一缓冲同一函数）⇒ 纯性能项；流式档不参与 |
| **G-偶 偶数尺寸的 `scale` 是否 no-op** | 同素材两个尺寸（4000×6000 与 4001×6000），legacy YUV 目标路分别在插/不插 `scale=ceil(iw/2)*2:…` 下跑，比产物 md5 | **偶数尺寸全等 ⇒ "偶数不插"是纯性能项**；奇数尺寸不等 ⇒ 保持现状（P7 结论）。这一格直接决定 P7 第二条能不能收窄 |
| **G5 AVIF GUI/headless 参数面** | 同一 `nbig` 素材、`-c:v libaom-av1`，四臂：都不发 / 只 `-still-picture 1` / 只 `-row-mt 1` / 两个都发。比 md5 + 墙钟 | 若 md5 全等而墙钟差 ⇒ 补默认值是**纯性能项**（并修 GUI/CLI 不一致）；若 md5 变 ⇒ 属改产物，需授权，且要先解释今天 GUI≠CLI 是不是既有缺陷 |
| **G6 cjxl PNG 中转加 `-compression_level 0`** | `RawColorPipeline.cs:561-563` 那条中转命令加/不加，比 **交付 JXL 的 md5** + 墙钟 | JXL md5 全等 ⇒ 纯性能项（PNG 无损 ⇒ 只是中间体积/耗时）。同时报中转 PNG 字节数 |
| **G7 GainMap SIMD 分块尺寸** | `GainMapEncoder.cs:398` 的 128 换 4096/16384/65536，比 `WriteBgra8PngAsync` 段耗时 + 产物 md5 | md5 全等 ⇒ 纯性能项。顺带把 `alpha=255` 那趟（`:413`）合进主循环再测一臂 |
| **G8 流式档串行** | 造一帧 >160 MB 的输入（如 8192×4096 rgb48 = 201 MB ⇒ 触发 `Streaming`），比现串行流式 vs 行带并行流式（同一 tile 行数）的 `ProcessMs` 与 RSS | 行带不引入跨块状态（相位用绝对坐标，`ColorKernels.cs:760-761`）⇒ 产物 md5 应全等；**RSS 升了就判负**（流式的目的就是内存受控） |
| **G9 AOT 档吞吐对照** | `pack.ps1 -Mode aot` 产物跑同一 bench 批次，与 JIT 档逐格对齐（表路/解析路/端到端 H1/H2） | 不改产物；只回答"AOT 下内联/向量化有没有掉"。同时把 `_probe-gpu-stage1.ps1:48` 记的 232/1100 MPix/s 与 `bench_rep1` 的 55.9–127 **对齐口径**（估4 vs 实1，两者不能混用） |
| **G10 cjpegli 并发** | 实6 已定案：**无 `--num_threads`** ⇒ 不再有"补参数"这一格；改为测 `-j N` 下 cjpegli 进程数与墙钟（交 C2 线程策略 lane） | 10-09 §3-P4 那条"该工具是否真支持此参数本机未测"可以结案：**不支持，恒 false 的门是正确实现**（标签：未复现） |
| **G11 引擎命令的线程参数（P4 续）** | §6 解堵后：引擎两条命令补 `-threads {options.Threads}`，比 `-j 8` 墙钟 + 每任务 ffmpeg CPU% 峰值 + RSS | 标签仍是**未判定**：10-09 已说"没有可测判据前不改"。判据同 P4（墙钟降而 RSS 不升才算收益） |
| **G12 曲线内核的向量化可行性**（解 C3-13 的"历史结论要不要结案"） | 在 `bench.cs` 形状上加 3 臂，同尺寸同夹具：① 现解析式 ② **对数表查表**（`log2`+`exp2`，256 KB，顺序/随机各一臂）③ `MathF.Pow(x, 1f/2.4f)`（编译期常数）。各测 dop=1 与 dop=20 的 MPix/s，并记产物最大误差（LSB@16bit） | ②/③ 的误差 ⇒ **改产物**，必须先有精度授权才谈收益；本格的产出是"AVX2 gather 慢于标量 MathF"这条 2026-08 结论在**8 MP 帧**上是否还成立（`SimdPixelOps.cs:10-16` 的样本只有 4096 像素） |

需要授权才能动的（本 lane **不排名、不动手**，只点名）：C3-1 第 2 半（编码侧换表）、C3-7（主通路带 alpha 平面 ⇒ 动 rgb48le）、
C3-8 若产物变（AVIF 默认值）、P7 已登记的 `rgb48le` 恒挂与 YUV 恒插 `scale`（奇数那半）。
