# RAW 管线审查报告（2026-09-20）

> **方法**：代码审查 + **实机文件测试**（非推断）。
> **素材**：`tools/src/dng_sdk/dng_sdk_1_7_1/sample_files/` 下的 **Adobe DNG SDK 官方样本**
> （稳定、在追踪面内），含**真实相机** RAW（Sony ILCE-7RM4）。
> **工具**：`publish/PLAN/artifacts/dngtool.exe`（实测 `-engines`：`libraw ✅ + dngsdk ✅ DNG 1.7 JXL`）。

## 1. 管线全景（按实测路径绘制）

```
输入识别（RawService.RawExtensions，~30 种扩展名）
  ↓
┌─ 目标是 dng？── 是 → ProcessDngAsync（QueueProcessor.cs:2250）
│                      dngtool -e 直接 RAW→DNG，**跳过预处理**
│                      ⚠ 输入必须是 RAW/DNG，否则「失败 (非 RAW 输入)」
└─ 否
   ├─ dngtool 不可用 → 抛错，**不回退** ffmpeg 直解（避免 Bayer 解出错色却报「已完成」）
   └─ dngtool 可用 → dngtool -d -T -o 0 -q 3 -W -H<mode> -6
                     ↓ 线性 16-bit TIFF（临时目录 raw_{guid}/，任务结束清理）
                     ↓ captured.InputPath = rawTiff        ← 输入被「狸猫换太子」
                     ↓ 色彩默认值（仅当用户未设高级参数）：
                         primaries = CanHdrWith(JpegGainMap) + 位深前提 ? bt2020 : bt709
                     ↓ 后续 = 普通「TIFF → 目标格式」流程（引擎或传统管线）
```

`dngtool` 参数含义：`-d` 线性 RGB · `-T` 输出 TIFF · `-o 0` 相机色彩空间 · `-q 3` AHD 去马赛克 ·
`-W` 相机白平衡 · `-H` 高光（0 裁剪 / 1 恢复 / 2 blend）· `-6` 16-bit。

## 2. 实机测试矩阵

### 2.1 输出格式遍历（`05_PGTM2_unsigned8.dng`，200×200）

| 目标 | exit | 产物 | 备注 |
|---|---|---|---|
| jpg | 0 | 1394 B | primaries=bt709（8bit 容器） |
| avif | 0 | 726 B | **bt2020** ✓ |
| png | 0 | 2292 B | bt709（位深 8 ⇒ 位深前提不满足） |
| tiff | 0 | 242708 B | 16-bit 未压缩，尺寸合理（200×200×6 ≈ 240000） |
| webp | 0 | 494 B | bt709 |
| jxl | 0 | 465 B | **bt2020** ✓ |
| **gif** | **1** | **0** | **契约拒绝**（见 §3.2） |
| dng | 0 | 5428 B | DNG→DNG；`dngtool -info` 校验通过（200×200 / max_raw=65535） |

### 2.2 真实相机 RAW（`03_jxl_bayer_raw_integer.dng` —— **Sony ILCE-7RM4，9600×6376，JXL 压缩 Bayer**）

| 目标 | exit | 耗时 | 产物 | 产物 CICP |
|---|---|---|---|---|
| jpg | 0 | **18 s** | 1.67 MB | — |
| avif | 0 | **74 s** | 382 KB | **`linear,bt2020`** ✓ |

### 2.3 选项矩阵（200×200 样本）

| 选项 | exit | 实测命令行（参数传递） |
|---|---|---|
| `--dng-highlight-mode 0/1/2` | 0/0/0 | `-H 0` / `-H 1` / `-H 2` ✓ |
| `--threads 1` | 0 | `-threads 1` ✓ |
| `--dng-compression 1 --dng-jxl-quality 90` | 0 | `-jxl -q 90 -effort 7 -decode_speed 1` ✓ |
| `--dng-bit-depth 8` | 0 | `-4` ✓ **实证生效**：产物 `max_raw` 65535 → **256** |
| `--dng-linear true`（61MP **Bayer** 输入） | **1** | 见 §3.3 |

### 2.4 失败路径（负控）

| 输入 | 目标 | 结果 |
|---|---|---|
| 普通 PNG | dng | **exit=1，0 产物** + 点名「失败 (非 RAW 输入)」 ✓ |
| dngtool 缺失 | 任意 | 抛错 + 点名（**不回退** ffmpeg）—— 代码审查确认（未做实机断工具验证） |

## 3. 发现

### 3.1 ✅ 已修：日志与事实不符（`QueueProcessor.cs`）

- **现象**：预处理成功后打印「色彩空间: **BT.709** + 线性传输」，**写死**。
- **实测**：目标 avif 时实际是 `bt2020`（**同一轮日志的下一行**就写着 `primaries=bt2020`）⇒ 自相矛盾。
- **处置**：该行改为不含色域的「已完成预处理（色彩标记见下一条）」，实际标记由紧随其后的日志给出。
- ⚠ 这正是本仓最忌讳的「宣称≠交付」。门禁**不依赖**该文案（已 grep 全部门禁确认），改动影响面 = 0。

### 3.2 ✅ **已修（2026-09-20）**：RAW → GIF 曾「无路可走」，而普通 PNG → GIF 可以

- **实测对比（修复前）**（同一次运行、同一 GIF 目标）：

  | 输入 | 结果 | 裁决 |
  |---|---|---|
  | 普通 PNG | exit=0，产出 ✓ | `None` + `ProceedWithDegradation` |
  | **RAW** | **exit=1，无产物** ✗ | **`Reject`**「gif 既不能携带 ICC 又不能表达该空间的 CICP → 不产出无标注结果」 |

- **根因（已定位到具体判据）**：RAW 预处理把**源**标记写进 `Options.ColorPrimaries/ColorTrc`
  并置 `UseAdvancedColorParameters = true`；而 `ColorIntentFactory` 在该标志下用
  「`ColorTrc` 非空」判定 **`targetExplicit`** ⇒ **把「源标记」误读成「用户显式指定的目标」**
  ⇒ `it.Target` 非 null ⇒ 规划层「无色彩通路容器 ⇒ 把目标钉成 sRGB」那条**不生效** ⇒ 拒绝。
  ⇒ 本质是**同一组字段既当源标记又当目标标记**（语义双关）。

- **修法（已实施，最小侵入）**：新增语义标志 **`FfmpegOptions.ColorFromRawDefault`** ——
  `QueueProcessor` 在注入 RAW 默认色域时置 `true`；`ColorIntentFactory` 的 `targetExplicit` 加
  `&& !o.ColorFromRawDefault`。
  ⚠ **只影响「意图装配」**；**输出标注**走另一条独立的线（`EffectiveOutputColorTokens`）
  ⇒ 「RAW 默认色域按目标 HDR 能力取值」那条功能**保留**。

- **✅ 实测（修复后）**：

  | 用例 | 修复前 | 修复后 |
  |---|---|---|
  | **RAW → GIF** | ❌ exit=1，0 产物 | ✅ **exit=0，产出 1 个**（裁决 `ProceedWithDegradation`） |
  | RAW → avif | ✅ bt2020 | ✅ **仍 bt2020**（产物 CICP `linear,bt2020`）—— **功能未回归** |
  | 普通 PNG → GIF | ✅ 产出 | ✅ 仍产出 |

- ✅ **变异 I（新逻辑的必要性）**：把 `ColorFromRawDefault` 置回 `false` ⇒
  **RAW → GIF 重新失败**（exit=1 + `Reject`）⇒ 证明该标志是修复的关键（断言非恒真）。

- ⚠ **范围澄清（用户报告为「任何动图都报错」）**：实测**只有 GIF 失败** ——
  `apng / webp / avif` 均 `exit=0` 正常产出（见 §2.1 与本节复现）。

### 3.3 ℹ **非缺陷**：`--dng-linear` + Bayer 输入被 dngtool 明确拒绝

- 实测日志（**dngtool 自己点名**）：
  `ERROR: -linear 仅适用于线性/去马赛克输入 (filters=0); Bayer 输入 (filters=0xB4B4B4B4) 请使用保留 CFA 模式`
  ⇒ 退出码 **7**，产品正确报出「dngtool 编码失败」。
- **定性**：**合法且点名**的拒绝 ✓ 不是静默失败。
- **可选改进（未做）**：产品层可**提前**拦住该组合并给友好提示，而不是让 dngtool 报错。

### 3.4 ℹ **虚警澄清**：日志「乱码」不是编码问题

- 现象：同一批命令里部分日志显示成 `鏈�鎸囧畾…`（UTF-8 被按 GBK 渲染的典型形态）。
- **实测**：`jpg.log` 与 `tiff.log` 同一行的**字节完全相同**
  （`5b 52 41 57 5d 20 e6 9c aa …` = `[RAW] 未指定…`）⇒ **文件是正常 UTF-8**，
  乱码只出现在**终端显示**环节。
- ⚠ 与本仓既有的「跨宿主日志编码不同（PS5.1 写 UTF-16LE）」是**两回事**，后者是真问题。

### 3.5 ⚠⚠ **深入验证：中间产物的实际编码曲线 + 标记语义错位**（**与 §3.2 同一根因**）

**(a) 中间 TIFF 实际是「gamma 编码」，不是线性** —— 源码证据链（可复现）：

1. `dngtool.cpp:519` → `processor.dcraw_process()` → `:528 processor.dcraw_ppm_tiff_writer()`
2. `libraw/src/postprocessing/dcraw_process.cpp:254` → `convert_to_rgb()`
3. `libraw/src/postprocessing/postprocessing_utils_dcrdefs.cpp:53` → 在 `convert_to_rgb` 内**无条件**执行
   `gamma_curve(gamm[0], gamm[1], 0, 0)`
4. **dngtool 全文件从未设置 `gamm[]`**（`grep -n "gamm" dngtool.cpp` 为空）⇒ 用 LibRaw 默认
   （dcraw 的 **sRGB / BT.709 曲线**）

⇒ 中间 TIFF 是 **`相机色域 + BT.709 gamma`**，**不是线性**；而产品把它标成 `trc=linear` ⇒ **标记与事实不符**。

**(b) `ColorPrimaries/ColorTrc` 的语义错位**：
RAW 预处理把**源**信息写进 `Options.ColorPrimaries/ColorTrc` 并置 `UseAdvancedColorParameters=true`，
而 `ColorIntentFactory` 在**该标志下**用「`ColorTrc` 非空」判 **`targetExplicit`**，并经
`EffectiveOutputColorTokens` 取**输出** token ⇒ **「源标记」被当成「目标/输出标记」**。
⚠ 这与 §3.2（RAW → GIF 被拒）**是同一个根因**：两个现象、一个成因。

**(c) ✅ 实测「当前行为自洽，无可见损害」**（**真实相机内容** 61MP，逐像素统计）：

| 链路 | 存储均值（归一化） | 判读 |
|---|---|---|
| 参照：`dngtool` TIFF → `ffmpeg` 直通 → jpg | 0.1406 | gamma 编码（≈ sRGB） |
| **产品：RAW → jpg** | **0.1406** | 与参照**逐值一致** ⇒ 直通，观感正确 |
| **产品：RAW → avif**（标 `linear,bt2020`） | **0.0212** | **比 jpg 暗 6.6×** ⇒ 正是 `sRGB→linear` 后的量级（由 0.1406 反推预期 ≈0.0175）|

⇒ **两条路径都正确**，但**机制不同**：
- **jpg**（目标 sRGB）：源（对中间 TIFF 探测 ⇒ 无标记 ⇒ 惯例假定 sRGB）+ 目标 sRGB
  ⇒ 引擎自报 `决策：None｜源=目标 → 直通` ⇒ 输出 = TIFF 的 gamma 像素；jpg 无 CICP ⇒ **观感正确** ✓
- **avif**（目标 = `linear`，来自 `EffectiveOutputColorTokens`）：源 sRGB + 目标 linear
  ⇒ 引擎自报 `决策：Map 后端=H2` ⇒ **真的做了 `sRGB→linear` 转换** ⇒ 输出**确为线性** + 标 `linear`
  ⇒ **自洽正确** ✓（这正是上表 0.0212 的来源）

**(d) 性质（本轮实测后**重判**，比初稿更准确）**：**不是行为缺陷，而是「意图与实现错位」** ——
代码注释写的是「把**源**标为 bt709+linear」，而该标签**实际扮演的是「目标/输出」**
（经 `targetExplicit` → `EffectiveOutputColorTokens`）；**源**则由对中间 TIFF 的探测**独立得出**
（无标记 ⇒ 惯例假定 sRGB —— **恰好正确**，因为 TIFF 确实是 BT.709 gamma 编码 ≈ sRGB）。
⇒ **净效果自洽**（两条路径都正确），但**可维护性差**：注释描述的机制与实际执行的机制**不是同一个**，
后来者若照注释去「修正」（例如让 dngtool 输出真线性、却不改源探测）会**破坏**当前的自洽。
⚠ 建议（未实施，**不属紧急缺陷**）：把注释改成与实现一致，或把该标签改走**源描述**通道。

## 5. 厂商原始格式实测（2026-09-20 第二轮；素材取自互联网）

### 5.1 素材来源与识别

**raw.pixls.us**（**CC0 公共领域**）⇒ 清单端点 `https://raw.pixls.us/json/getrepository.php?set=all`
（2016 条记录）。本轮下载 **6 种厂商格式**（共 ~31 MB）到 `tests/output/raw_samples/`：

| 文件 | 相机 | 尺寸 | filters | max_raw |
|---|---|---|---|---|
| `Canon_EOS40D.CR2` | Canon EOS 40D（sRAW2） | 1944×1296 | 0（已去马赛克） | 16224 |
| `Canon_EOSR6.CR3` | Canon EOS R6 | 3407×2271 | **3031741620** | 16383 |
| `Nikon_CoolscanV.NEF` | Nikon COOLSCAN V ED | 818×546 | 0 | 65535 |
| `Olympus_E10.ORF` | Olympus E-10 | 2256×1684 | **3031741620** | 1023 |
| `Panasonic_LX7.RW2` | Panasonic DMC-LX7 | 1384×1384 | 3789677025 | 4095 |
| `Sony_ILCE7S.ARW` | Sony ILCE-7S | 2784×1872 | **3031741620** | 16383 |

魔数校验（确认是真厂商格式）：CR2 `49492a00…43 52`、CR3 `…ftypcrx `（ISOBMFF）、NEF/ARW TIFF-LE、
ORF `MMOR`（大端）、RW2 `IIU\0`。
✅ **`dngtool -info` 全部正确识别**（make / model / 尺寸 / filters / max_raw 均合理）。

### 5.2 ⚠⚠ 缺陷①：转 DNG 后 **CFA 相位被改变 ⇒ 颜色错误**

**12/12 格 `exit=0`**（即"能转"），但产物**颜色不对**：

| | CFAPattern（exiftool） |
|---|---|
| 源 `Sony_ILCE7S.ARW` | `[Red,Green][Green,Blue]` = **RGGB** |
| 产物 DNG（ljpeg / jxl 均同） | `[Green,Red][Blue,Green]` = **GRBG** ⚠ |

**独立佐证（解码后 RGB 均值，逐像素采样）**：

| | R | G | B |
|---|---|---|---|
| Olympus 源 | 17578 | **20105** | 12558 |
| Olympus → DNG | 25388 | **11483** | 26172 |
| Sony 源 | 12713 | 12498 | 12086 |
| Sony → DNG | 23665 | **7117** | 15061 |

⇒ **G 通道大幅下降**，正是 CFA 相位错位的典型症状 —— 与 exiftool 的相位变化**两条独立证据吻合**。

⚠ **不是厂商格式特有**：**对照实验**（Adobe 官方 `03_jxl_bayer_raw_integer.dng`，源 `filters=3031741620`）
⇒ **DNG → DNG 也变成 `3789677025` / GRBG** ⇒ **缺陷通用**。
规律：源 `filters=0xB4B4B4B4` ⇒ 产物 `0xE1E1E1E1`（相位错）；源本身为 `0xE1E1E1E1` 的不受影响。

**✅ 已修（2026-09-20）** —— 根因是 `tools/src/dngtool/dngtool.cpp` 里**三处 bug 叠加**：

| # | bug | 详情 |
|---|---|---|
| ① | **位移常量错** | dcraw `FC(row,col)` 的位移是 `(0,0)→0, (0,1)→2, (1,0)→**4**, (1,1)→**6**`；代码写成 `>>8` / `>>10` |
| ② | **未处理 `G2 = 3`** | dcraw 用 `3` 表示**第二个绿**；代码的四个相位模式只用 0/1/2 ⇒ 常见值 `0xB4B4B4B4`（= `[0,1,3,2]`）**四个模式全不匹配** ⇒ 落 `else`（日志指纹：`WARNING: unusual CFA pattern, using phase 0`）|
| ③ | **相位语义与 DNG SDK 相反** | `dng_negative::SetBayerMosaic(phase)` 的真实映射（`dng_sdk/source/dng_negative.cpp:2957`）是 **0=GRBG, 1=RGGB, 2=BGGR, 3=GBRG** —— 与「0=RGGB」的直觉 **0↔1、2↔3 互换** |

**修法**：位移改 `4`/`6`；先把 `G2(3)` 归一化成 `G(1)`；再按 **SDK 的真实相位顺序**匹配（退化默认 `RGGB`）。

**验证**：

| | phase | 产物 CFAPattern |
|---|---|---|
| 修前 | 0（+ WARNING） | `[Green,Red][Blue,Green]` = **GRBG** ✗ |
| **修后** | **1 (RGGB)** | **`[Red,Green][Green,Blue]` = RGGB** ✓ 与源一致 |

**解码对比（决定性，`-o 0` 相机空间，逐像素采样）**：

| 文件 | 源 G | 修前产物 G | **修后产物 G** |
|---|---|---|---|
| Olympus_E10 | 20105.2 | 11483.7 ✗ | **20104.8** ✓ |
| Sony_ILCE7S | 12498.5 | 7117.7 ✗ | **12504.2** ✓ |
| Canon_EOSR6 | 9537.9 | — | **9540.4** ✓ |

⇒ **修后与源逐值吻合（差异 <0.1%）** ✓；`filters` 也全部与源一致。

### 5.3 ⚠ 缺陷②：JXL 压缩 DNG 在**带 `ActiveArea` 的源**上产出**无法解码**的文件

`Canon_EOSR6`（源 `raw_width=3584` > `width=3407`，margin `156,108`）：
- **ljpeg 产物**：正常解码（46.4 MB TIFF）✓
- **JXL 产物**：**解码失败** ✗
  ```
  *** Error: Default crop extends outside ActiveArea ***
  [dngtool] ERROR: unpack failed: Unsupported file format or not RAW file
  ```

**✅ 已修（2026-09-20）** —— 根因：`dngtool.cpp` 把 margin 填进了 `DefaultCropOrigin`：

```cpp
negative->SetDefaultCropSize(activeW, activeH);
negative->SetDefaultCropOrigin(leftMargin, topMargin);   // ← 错：DNG 规范里 origin 是**相对 ActiveArea** 的
negative->SetActiveArea(dng_rect(topMargin, leftMargin, topMargin + activeH, leftMargin + activeW));
```

⇒ `ActiveArea` 本身**已含 margin**，`origin` 又加一次 ⇒ **裁剪框跑到 ActiveArea 之外**。
⚠ **ljpeg 路径不做该校验**所以看不出来（但产物同样带着错误的裁剪框，换解码器/换软件就可能暴露）；
**JXL 路径会校验** ⇒ 直接报错。

**修法**：先设 `ActiveArea`，再设 `DefaultCropSize(activeW, activeH)` + **`DefaultCropOrigin(0, 0)`**。

**验证**：EOSR6 的 JXL 产物 ⇒ **解码成功**（46.4 MB TIFF），只剩无害的 padding warning ✓

⚠ **严重性说明**：产品链路**默认就走 JXL**（实测命令 `dngtool -e -jxl -effort 7 -decode_speed 1`）
⇒ **修复前，「带 ActiveArea 的源」在默认配置下就会产出无法解码的 DNG**。

### 5.4 JXL 压缩比（能压，但不代表能用）

| 文件 | 无损 JPEG | JXL q=90 | 比值 |
|---|---|---|---|
| Canon_EOS40D | 10.8 MB | 121 KB | 89× |
| Canon_EOSR6 | 7.88 MB | 136 KB | 58× |
| Nikon_CoolscanV | 2.57 MB | 94 KB | 27× |
| Olympus_E10 | 2.56 MB | 45.5 KB | 56× |
| Panasonic_LX7 | 2.12 MB | 76.6 KB | 28× |
| Sony_ILCE7S | 5.85 MB | 221 KB | 27× |

### 5.5 对三个问题的直接回答（**两处缺陷均已修复**）

| 问题 | 答案 |
|---|---|
| 厂商原始格式素材 | ✅ **能找到**（raw.pixls.us，CC0），`dngtool` **全部识别** |
| **私有格式转 DNG 可用吗？** | ✅ **已修，可用** —— 原缺陷①（CFA 相位被改 ⇒ 颜色错）已修复并验证 |
| **JXL 压缩 DNG 可用吗？** | ✅ **已修，可用** —— 原缺陷②（带 `ActiveArea` 的源产出无法解码）已修复并验证 |

**修复摘要（2026-09-20，改 `tools/src/dngtool/dngtool.cpp` 共 4 个 bug 点，重新编译并部署）**：

1. CFA 相位推导：位移 `>>8` / `>>10` → **`>>4` / `>>6`**
2. CFA 相位推导：处理 dcraw 的 **`G2 = 3`**（第二个绿）
3. CFA 相位语义：按 **DNG SDK 的真实顺序**（`0=GRBG, 1=RGGB, 2=BGGR, 3=GBRG`）
4. 裁剪框：`DefaultCropOrigin` 由 `(leftMargin, topMargin)` → **`(0,0)`**（规范里它相对 `ActiveArea`）

**验收**：6 种厂商格式 × 2 种压缩 = **12/12 `exit=0`**；
`filters` 全部与源一致（`3031741620` 不再被改成 `3789677025`）；
**解码后 RGB 均值与源逐值吻合（差异 <0.1%）**；
EOSR6 的 JXL 产物**从「无法解码」变为「正常解码」**（46.4 MB TIFF）。
部署的 `publish/PLAN/artifacts/dngtool.exe` 与构建产物 **SHA256 一致**。

## 4. 审查结论

| 维度 | 结论 |
|---|---|
| 输入识别 | ✓ 扩展名表覆盖 ~30 种；RAW 与非 RAW 分流正确 |
| 去马赛克 | ✓ dngtool 唯一引擎；失败**不回退**（避免错色输出） |
| 输入替换 | ✓ `InputPath` 换成临时 TIFF，后续走统一路径；临时目录任务结束清理 |
| 色彩默认值 | ✓ 按目标 HDR 能力取值；四格实测命中（含位深前提） |
| DNG 输出 | ✓ 跳过预处理；JXL / 线性 / 位深 / 高光 / 线程**参数传递全部实测正确** |
| 失败路径 | ✓ 全部**点名**、不静默；非 RAW→DNG 正确拒绝 |
| 已知缺口 | ⚠ RAW→GIF 无路（§3.2） |

⚠⚠ **测试覆盖的边界（措辞不得大于扫描面）**：
- 实机素材**全部是 DNG**（含真实相机的 DNG），**没有** CR2/CR3/NEF/ARW 等**厂商原始格式**文件
  ⇒ 「~30 种扩展名」这条只实测了 `.dng` 一条路径；其余靠 LibRaw 的同一解码入口，属**未实测**。
- 未做：断工具场景的实机验证 · 超大文件（>200 MB DNG）· 并发多任务的内存压力测试。
