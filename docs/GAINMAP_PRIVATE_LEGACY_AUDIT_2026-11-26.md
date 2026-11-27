# 私有与早期 GainMap 支持审计（重点 HEIC）

日期：2026-11-26  
范围：`GainMapDecoder` / ISO-BMFF 探针 / Apple MakerNote / OnePlus 文件尾私有 HDR，以及 Apple v1/v2/v3 JPEG。  
被测产物：`src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.dll`，SHA-256 前 16 位 `7B1D9CEAA053291D`。

## 1. 结论

**原支持并不完全正确。Apple auxC/grid HEIC 与 Apple v2 JPEG 存在确定性像素语义错误，本轮已修复并用独立公式、公开样本和真实样本数值锚验证。**

- Apple v2 的 `HDRGainMapHeadroom` 是**线性倍率**，旧实现误当 log2；`4.532783` 因此被错误放大为 `2^4.532783 = 23.1475×`，正确值是 `4.532783×`、`log2 = 2.180397`。
- Apple/厂商 aux HEIC 旧实现按增益图像素分位猜测“128 中性”并切换成带负下沿的 ISO 曲线。该分支没有规范依据，且与公开前向写入器相逆关系不一致，已删除；当前统一使用 0 基 Rec.709 Apple 公式。
- DJI 真实文件的 Apple MakerNote 使用 TIFF `SRATIONAL(type 10)`，旧纯托管解析器不支持并逐件回退 exiftool；现已补齐 `RATIONAL(5)`、`SRATIONAL(10)`、`FLOAT(11)`。
- ISO 21496 HEIC `tmap + grid` 的真实 iPhone 样本保持原读数并通过；Apple v1/v3 JPEG 也未发生行为回归。
- OnePlus 文件尾 `local.hdr.linear.mask` 的结构关联、尺寸关系和当前样本 HDR 像素均通过，但其 144 字节私有参数块没有公开规范，**只能判定“结构解析与当前样本自洽”，不能宣称私有语义已被独立证明**。

## 2. 缺陷与修复

### D1：Apple v2 headroom 单位错误

旧链路把 `HDRGainMapHeadroom=4.532783` 直接写入 `GainMapMax/HDRCapacityMax`，后级再执行 `2^x`。libavif `appleXMPToGainMap` 则把同名字段读为 `hdrHeadroomLinear`，再写 `alternateHdrHeadroom = log2(hdrHeadroomLinear)`；其 v2 公开测试锚为 `2.1804`。

修复位置：

- `src/FfmpegGui/Services/GainMapDecoder.cs:1573`：线性值解析后执行 `MathF.Log2`，并拒绝 `headroom <= 1` 的伪正余量。
- Apple v1 无 headroom 时继续采用既有隐式 `8×`；v3 继续让 Adobe `hdrgm` 元数据优先。

### D2：Apple aux HEIC 公式由像素分位猜测

旧实现以 `p0.5` 判断“0 基”或“128 对称基”。后一分支会改写 `GainMapMin=-GainMapMax`、改走 ISO 指数曲线，并可能把私有 RGB 辅助图按另一套通道语义处理。

公开前向写入器 `toGainMapHDR` 先计算 `(HDR/SDR - 1)/(headroom - 1)`，再施加 Rec.709 类 OETF；其逆变换正是当前 0 基 Rec.709 公式。像素分位不能改变容器语义。

修复位置：

- `src/FfmpegGui/Services/GainMapDecoder.cs:735`：删除分位判别、对称 128 分支及两个只服务该猜测的辅助函数。
- `src/FfmpegGui/Services/GainMapDecoder.cs:743`：由已解析的 `meta.GainMapMax` 得到线性 headroom，调用 `ApplyGainMapApple`。
- Apple aux 与 JPEG v1/v2 没有被粗暴合并：aux 使用 Rec.709 Apple 公式；JPEG v1/v2 仍走与 libavif 一致的通用指数映射。

独立数值锚：128 灰瓦片、底图最大线性值 `0.2146`、XMP 线性 headroom `2.0` 时，

\[
HDR = 0.2146 \times \left[1 + (2-1)\times \left(\frac{128/255+0.099}{1.099}\right)^{1/0.45}\right]
\approx 0.270714
\]

实测 `0.2701`，容差 `±0.012`。旧对称分支会错误保持在约 `0.2146`。

### D3：DJI MakerNote 的 SRATIONAL 编码未被纯托管解析

修复位置：`src/FfmpegGui/Services/AppleMakerNoteParser.cs:212-263`。

真实 DJI 样本修复后先由纯托管解析器读到 `tag33/48=0/0`，再正确回退 Apple XMP；不再因为 type 10 不支持而逐件依赖 exiftool。合成 A 夹具也改为直接生成 SRATIONAL，既有 8 项托管/exiftool parity 对拍继续覆盖该形态。

### D4：门禁自身存在恒真匹配与错误方向断言

`verify-gainmap-engine.ps1` 的 `PatHit` 实现按 `|` 分割，实际调用全部使用 `=`，导致正则为 null、匹配恒真。旧断言还要求 Apple v2 必须比 v1 亮，恰好把单位错误固化成期望。

本轮已：

- 改为只按第一个 `=` 分割，并加入“不存在标记必须不匹配”的负控；
- 将 v2 日志锚改为 `log2=2.180397`、`920 nits`；
- 将 v1/v2 像素断言改为独立数值锚：v2 `cap=4.5328/max=2.8438±0.02`，v1 `cap=8/max=4.2149±0.02`。

## 3. 支持矩阵（仅对本次覆盖范围作结论）

| 形态 | 当前结论 | 证据边界 |
|---|---|---|
| HEIC ISO 21496 `tmap`、单图/grid | **已验证形态正确** | 真实 iPhone 结构 9/9、HDR 像素 4/4；修复前后读数不变 |
| HEIC Apple auxC/grid、单色增益图 | **原实现有误，已修复** | 3 件真实 DJI + 普通/128 灰瓦片独立公式锚 |
| HEIC 厂商 aux + Apple XMP | **按同一 Apple 公式修复** | 只覆盖与该语义相同的辅助图；不保证所有厂商 URN/私有曲线 |
| HEIC OnePlus 文件尾私有 HDR mask | **结构与当前样本自洽** | 真实文件像素 4/4、真机形态 9/9；144 字节参数块语义未独立证实 |
| HEIC aux-21496 前置元数据形态 | **合成结构通过** | 尚缺真实第三方样本，不能写成真机语义验证 |
| JPEG Apple v1 | **保持正确读数** | `8× / maxLinear=4.2149`，4/4 |
| JPEG Apple v2 | **单位错误已修复** | `4.5328× / log2=2.180397 / maxLinear=2.8438`，4/4 |
| JPEG Apple v3 / Adobe `hdrgm` 优先 | **保持不变** | `3.3812× / maxLinear=2.0526`，4/4 |

## 4. 同文件修复前后读数

`capLinear` 是元数据能力上限，`maxLinear` 是实际解码像素最大值；两者分列，避免用“峰值不超过自己推导的上限”冒充语义正确。

| 样本 | 修复前 cap / max | 修复后 cap / max | 说明 |
|---|---:|---:|---|
| DJI 0226 aux HEIC | 11.1750 / 6.0071 | 3.4822 / 2.8224 | 单位与曲线纠偏 |
| DJI 0616 aux HEIC | 11.1750 / 5.3170 | 3.4822 / 2.7612 | 单位与曲线纠偏 |
| DJI 0927 aux HEIC | 11.1750 / 1.6473 | 3.4822 / 1.6457 | 上限纠正，像素近似不变的对照件 |
| Apple v1 JPEG | 8.0000 / 4.2149 | 8.0000 / 4.2149 | 不变 |
| Apple v2 JPEG | 23.1475 / 8.7984 | 4.5328 / 2.8438 | 单位纠偏 |
| Apple v3 JPEG | 3.3812 / 2.0526 | 3.3812 / 2.0526 | 不变 |
| iPhone ISO tmap/grid HEIC | 4.4335 / 2.0444 | 4.4335 / 2.0444 | 不变 |
| OnePlus 私有尾注 HEIC | 1.1450 / 1.1396 | 1.1450 / 1.1396 | 不变；不外推私有语义 |
| 128 灰瓦片合成件 | 4.0000 / 0.2146 | 2.0000 / 0.2701 | 独立期望 0.270714 |

公开样本字节与 SHA-256 登记在证据根 `corpus/sources.json`；DJI 0927 另做独立重下载，哈希与仓内样本完全一致。

## 5. 构建与门禁

整套 `ffmpegPictureUI.sln -c Release -m:1`：0 警告、0 错误。`git diff --check` 通过，`verify-gainmap-engine.ps1` PowerShell 解析错误数为 0。

| 验证项 | 最终结果 | 备注 |
|---|---:|---|
| 受管 `verify-gainmap-isobmff` | **174 PASS / 0 FAIL / 9 SKIP** | 9 条为未注入个人 OnePlus corpus 的真机臂 |
| 显式 OnePlus corpus 直跑同一门禁 | **183 PASS / 0 FAIL / 0 SKIP** | `VENDOR_HEIC_CORPUS_DIR` 只读指向真机目录；9 条真机臂全部执行 |
| 受管 `verify-gainmap-engine` | **225 PASS / 0 FAIL** | 含 Apple 独立锚、PatHit 负控、三件第三方 ISO JPEG |
| 受管 `verify-gainmap-managed` | **49 PASS / 0 FAIL** | 通过 |
| 受管 `verify-gainmap-memory` | **22 PASS / 0 FAIL / 1 SKIP** | 十几 GB 重型压力臂未跑，SKIP 不算通过 |
| 受管 `verify-probe-mirror-parity` | **93 PASS / 0 FAIL** | 通过 |
| 受管 `verify-gainmap-host` 复跑 | **18 PASS / 6 FAIL** | D8/D10/D12：tonemap、P3、RAW 注入；不是本轮改动路径 |
| 受管 `verify-gainmap-thirdparty` 复跑 | **21 PASS / 3 FAIL** | 2 条 Perl DLL 加载失败；1 条默认 JPEG 出口 XMP 契约红 |

说明：

- engine 目标脚本为 225/0，但该 runner 轮总退出仍被前置 `bench` 的两条计时断言拉红；性能计时不改写成 GainMap 语义失败，也不隐去。
- 本轮没有执行 106 条全量基线；上述均为显式子集。
- 灰瓦片裸 `gainmap-stats` 仍显示 `2 PASS / 2 FAIL`，失败来自对该低亮度夹具不适用的“必须超过 SDR 白点”通用断言。受管门禁消费的是独立 `cap/max` 数值锚，不把这两条强行算作通过。

## 6. 未了结与边界

- OnePlus 144 字节私有参数块需要公开规范或更多设备样本交叉验证，当前不宣称完全支持所有 OnePlus/OPlus 私有变体。
- aux-21496 目前只有合成形态证据；未知厂商 RGB aux 也不能由 Apple 单色公式外推。
- host 与 third-party 的剩余红需要独立批次处理，不能借本轮 Apple 修复宣布全仓 GainMap 全绿。
- 本轮修改尚未 Git 提交，也未更新 RC3 安装包。工作区另有上一项 JXR/跨格式任务的未提交修改，未计入本报告的产品改动范围。

## 7. 修改文件与证据索引

本轮产品/判据文件：

- `src/FfmpegGui/Services/GainMapDecoder.cs`
- `src/FfmpegGui/Services/AppleMakerNoteParser.cs`
- `tests/scripts/_make-synth-aux-gainmap.py`
- `tests/scripts/_make-synth-heic-gridgainmap.py`（同文件还含上一任务修改，提交时需拆分说明）
- `tests/scripts/verify-gainmap-isobmff.ps1`
- `tests/scripts/verify-gainmap-engine.ps1`

证据根：

`tests/output/_tmp/gainmap-private-legacy-audit/67253624f1de46deb23371df50d258e5/`

关键文件：

- `gainmap-before-after.json`：同文件前后 cap/max 与逐样本日志路径。
- `corpus/sources.json`：公开样本 URL、大小、SHA-256。
- `gate-logs/`：受管门禁最终日志副本。
- `oneplus-vendor-gate-direct-v2.log`：通过 `VENDOR_HEIC_CORPUS_DIR` 只读引用个人 OnePlus corpus 的补充门禁，183/0/0。
- `reference/toGainMapHDR/`：用于公式核对的第三方前向写入器源码。

上游依据：

1. libavif Apple XMP 读取：<https://raw.githubusercontent.com/AOMediaCodec/libavif/main/apps/shared/avifjpeg.c>
2. libavif Apple v2 数值测试：<https://github.com/AOMediaCodec/libavif/blob/main/tests/gtest/avifjpeggainmaptest.cc>
3. Apple 公开公式讨论：<https://developer.apple.com/forums/thread/756526>
4. `toGainMapHDR` 前向 Metal kernel：<https://raw.githubusercontent.com/chemharuka/toGainMapHDR/main/toGainMapHDR/CustomFilter/GainMapKernel.ci.metal>
5. `toGainMapHDR` headroom 调用与元数据：<https://raw.githubusercontent.com/chemharuka/toGainMapHDR/main/toGainMapHDR/main.swift>、<https://raw.githubusercontent.com/chemharuka/toGainMapHDR/main/toGainMapHDR/Resource/Metadata.swift>
