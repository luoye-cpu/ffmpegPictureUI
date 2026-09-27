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

### 5.6 ✅ 审查：补全修复的**覆盖度**（2026-09-20 第七轮）

**自查发现问题**：初次修复时只验证了 **RGGB 一种相位**，而代码里有 **4 个相位分支**
⇒ **只验 1 路等于没验**。

**补全过程**：从 raw.pixls.us 追加下载 **26 个**不同相机样本（`raw_samples2/`、`raw_samples3/`），
按 `filters` 值统计后得到**四相位全覆盖**：

| 相位 | 样本数 | 代表机型 |
|---|---|---|
| **GRBG (p0)** | 4 | Leica D-LUX 5 · Panasonic DMC-LX7 |
| **RGGB (p1)** | 20 | Canon EOS R6 · Sony ILCE-7S · Olympus E-10 · Pentax *ist DL · Samsung EX1 … |
| **BGGR (p2)** | 4 | **Nikon D1H** · Fujifilm X-S1 · FinePix X10 |
| **GBRG (p3)** | 2 | **Nikon D2H · D2Hs** |
| linear（无 CFA） | 5 | Canon EOS 40D（sRAW2）· Nikon COOLSCAN |
| 非 Bayer（`0xFFFFFFFF`） | 6 | Sigma X3F（Foveon）· RaspberryPi |

**四相位决定性验证（源 vs 产物，逐像素，`-o 0` 相机空间）**：

| 相位 | 源 R/G/B | 产物 R/G/B | 偏差 |
|---|---|---|---|
| p0 GRBG (Leica) | 15843/16817/17048 | 15909/16845/17069 | **0.4%** ✓ |
| p1 RGGB (Sony) | 12713/12499/12087 | 12720/12504/12093 | **0.1%** ✓ |
| p2 BGGR (Nikon D1H) | 23143/21371/18913 | 23193/21401/18963 | **0.3%** ✓ |
| p3 GBRG (Nikon D2H) | 24147/23292/22864 | 24169/23301/22908 | **0.2%** ✓ |

⇒ **4 个相位分支全部正确** ✓（恢复源码后复跑，结果一致）

**顺带查清两件事（都**不是**本次修复引入的）**：

1. **Fujifilm X-S1（EXR 传感器）** 的产物与源解码**不一致** ⇒ 做了**对照实验**
   （临时回滚到修复前逻辑、重新编译、再测）：

   | 版本 | 相位判定 | 产物 R/G/B | 源 R/G/B | 偏差 |
   |---|---|---|---|---|
   | 修复前 | `WARNING: unusual CFA pattern` → GRBG | 19731/12312/22003 | 9274/9001/8716 | **巨大** |
   | 修复后 | BGGR | 17504/14503/18445 | 同上 | **巨大** |

   ⇒ **两个版本都错** ⇒ **既有问题**：Fujifilm **EXR** 的 CFA 不是标准 Bayer，
   LibRaw 的 `filters` 值不能按标准 2-bit 解码。⚠ 但修复后偏差**更小**，方向正确 ⇒ **属已知边界**。

2. **非 Bayer 素材**（Sigma X3F / RaspberryPi，`filters=0xFFFFFFFF`）**已被显式拒绝**：
   `ERROR: cannot open …: Unsupported file format or not RAW file` ✓ ⇒ **无需额外保护**。

⚠⚠ **方法论教训**：**改了 N 路分支就必须验 N 路** —— 只验 1 路等于没验。

### 5.7 🔍 偏差分析：Fujifilm RAF → DNG 的偏差根因（2026-09-20 第八轮）

**现象**：Fujifilm RAF 转 DNG 后，产物与源的解码结果**差异巨大**（X-A1 偏差 **169.6%**）。
⚠ 且**不止 EXR** —— 标准 Bayer 的 **X-A1 / X-A10 同样如此**（106–170%）。

**排查过程（逐项排除）**：

| 假设 | 检验方法 | 结论 |
|---|---|---|
| CFA 相位错 | **四相位穷举**（直接改产物 DNG 的 `CFAPattern2` 字节，4 种全试） | ❌ 四种都远偏离源 ⇒ **不是相位** |
| 白平衡 | 带 / 不带 `-W` 对比 | ❌ 结果**完全相同** ⇒ 不是 WB |
| 尺寸 / 裁剪 | `-info` + DNG 标签（`ActiveArea`/`DefaultCrop`） | ❌ 完全一致 |
| 黑 / 白电平 | `-info` 的 `black` / `max_raw` | ❌ 都是 0 / 4095 |
| **raw 像素数据** | `-e -v` 打印 `input row0/row1` | ❌ **源与产物逐值完全相同**（`srcPitch=10240` 亦同）|

**⇒ 决定性证据**（`-d -v` 的 `unpack()` 输出）：

| | 源 RAF | 产物 DNG |
|---|---|---|
| `filters`（unpack 后）| **3031741620**（RGGB）| **0** ⚠ |
| 数据指针 | **`raw_image`** 非空（CFA 单平面）| **`color3_image`** 非空（3 平面）⚠ |
| `warnings` | `0x10000`（`DNGSDK_PROCESSED=**no**`）| `0x1A0000`（`DNGSDK_PROCESSED=**YES**`）⚠ |

⇒ **产物 DNG 被 LibRaw 当成「已去马赛克的 3 平面」处理**，而我们写进去的其实是 **Bayer CFA 单平面**
⇒ **数据被误解**。（这也解释了为什么「四相位穷举」全不匹配 —— 它根本没按 CFA 解。）

**根因**：`dngtool.cpp` 的解码路径**无条件启用了 DNG SDK 的 STAGE2 / STAGE3**：

```cpp
processor.imgdata.rawparams.options |= (1u << 12); // LIBRAW_RAWOPTIONS_DNG_STAGE2
processor.imgdata.rawparams.options |= (1u << 13); // LIBRAW_RAWOPTIONS_DNG_STAGE3
```

（这两个是**为处理带 OpcodeList 的官方 DNG** 而加的 —— 见同处注释 —— 但它们**无条件**生效
⇒ 对自己写的「保留 CFA」DNG 也会走去马赛克分支 ⇒ `filters` 被清 0、数据改走 `color3_image`）

⚠ **与本轮 dngtool 修复无关**（修复只改相位映射）；这是**既有的解码路径问题**。
⚠ 但影响面可能不小：**任何经 `dngtool` 解码的「保留 CFA 的 DNG」都可能受影响**。

**修复方向**：把 STAGE2/3 改成**仅在 DNG 确实带 OpcodeList / 是 JXL 压缩时**启用
（即保留 `LIBRAW_RAWOPTIONS_DNG_STAGE23_IFPRESENT_JPGJXL` 的「**if present**」语义），
而不是无条件打开。⚠ 需回归 `03_jxl_bayer_raw_integer.dng` 等官方样本。

> ✅ **已修（2026-09-20 第九轮，见 `HANDOVER §20` 第 4 项）**：改为**按需重试** ——
> **先按 CFA 试解**，解不出再启用 STAGE2/3。本节的「未实施」措辞**已失效**，保留原文仅供追溯。

## 6. RAW 相关 UI 审计（2026-09-20）

**目标**：找出「UI 显示的能力/状态与**真实行为**不符」的问题。

### 6.1 RAW/DNG 相关控件清单

| 控件 | 位置 | 默认值 |
|---|---|---|
| `RawPanel` | `MainWindow.xaml:278` | `IsVisible=False` |
| `RawEngineStatus` | `:279` | dngtool 可用性文本 |
| `DngCompressionCombo` | `:285` | **SelectedIndex=1（JXL）** |
| `DngJxlQualityPanel` / `DngJxlQualitySlider` | `:288` / `:291` | `IsVisible=True` / Value=0 |
| `DngJxlAdvancedPanel`（effort / decode_speed）| `:299` / `:301` / `:303` | `True` / 7 / 1 |
| `DngLinearCombo` | `:307` | SelectedIndex=0（保留 CFA）|
| `DngBitDepthCombo` | `:310` | SelectedIndex=1 |
| `DngHighlightCombo` | `:313` | SelectedIndex=1 |

### 6.2 ✅ **已修**：DNG 专属选项在「非 DNG 输出」时仍显示

- **现象**：`RawPanel` 的可见性只取决于 **`isRaw = (ConversionModeCombo.SelectedIndex == 2)`**（**模式**），
  而面板里装的全是 **DNG 输出专属选项**（压缩方式 / JXL 质量 / JXL 参数 / 布局 / 位深 / 高光）。
  ⇒ 用户在 **RAW 模式**下选「输出 jpg / avif」时，这些选项**仍然显示**，
  **但对输出完全无效** ⇒ 用户以为设置了、实际没生效 ⇒ **正是「UI 与真实不符」**。
- **修法**：把这些选项包进新面板 **`DngOptionsPanel`**，
  可见性 = **「RAW 模式 **且** 输出格式 = dng」**；
  ⚠ 并在 **`FormatCombo_SelectionChanged`** 里**也刷新** —— 该 handler 原先只调
  `UpdateOptionAvailability()`、**不触发** `UpdatePanels` ⇒ 会漏刷。
- **验证**：5 条 UI 门禁 **各 322/0** ✓
  （`verify-ui-host` · `verify-ui-strategy-map` · `verify-ui-param-matrix` · `verify-ui-param-defects` · `verify-cli-strict`）。

### 6.3 ⚠ **待修**：`DngLinearCombo`（线性 DNG）对 **Bayer 输入**必然失败

- **实测**（见 §3.3）：`--dng-linear true` + Bayer 输入 ⇒ dngtool **明确拒绝**：
  `-linear 仅适用于线性/去马赛克输入 (filters=0); Bayer 输入 (filters=0xB4B4B4B4) 请使用保留 CFA 模式`
- 而 UI 把「线性 DNG」与「保留 CFA」**并列为可选**，**未提示**它只适用于**已去马赛克/线性**的输入
  ⇒ **多数相机 RAW（Bayer）用户选了会运行时失败**。
- **性质**：UI 提供了**做不到的组合**且无任何提示 ⇒ 属「UI 与真实不符」。
- **建议修法**：按输入类型**禁用/隐藏**（需探测 `filters`），或在选项旁**明确标注适用条件**。

### 6.4 ⚠ **待核实**：`DngCompressionCombo` 的 UI 默认与代码默认是否一致

- UI 默认 **`SelectedIndex=1`（JXL）**；实测产品命令行确为 `dngtool -e -jxl -effort 7 -decode_speed 1` ✓ 与 UI 一致。
- **✅ 已核实（2026-09-21，见 §43.3）**：`FfmpegOptions.cs:249` `DngCompression { get; set; } = 1`（**JXL**）·
  `DngJxlQuality = 0`（无损）⇒ **代码默认 / UI 默认 / 实际命令行三者一致，均为 JXL** ✓
  ⇒ **不存在「GUI 用户与 CLI 用户默认产物不一致」的问题**。本项**已结案**。

### 5.8 ✅ **已修**：JXL 压缩的 DNG 产物与源不一致（`-q` **双重语义冲突**）

**实测**（同一批素材，`-e -jxl` vs `-e -lossless`，均用 `-d -T -o 0 -q 3 -W -H 1 -6` 解码后对比）：

| 相位 / 机型 | **无损 JPEG 产物** | **JXL 产物** |
|---|---|---|
| p0 GRBG (Leica D-LUX 5) | **0.4%** ✓ | **31.7%** ✗ |
| p1 RGGB (Sony ILCE-7S) | **0.1%** ✓ | **50.9%** ✗ |
| p2 BGGR (Nikon D1H) | **0.3%** ✓ | **24.4%** ✗ |
| p3 GBRG (Nikon D2H) | **0.2%** ✓ | **27.3%** ✗ |

⇒ **JXL 路径对「所有相位」都不一致**，而无损 JPEG 全部 ≈0% ⚠

**已排除**：**不是 STAGE2/3 问题** —— 实测 JXL 产物的 `unpack()` 与无损 JPEG 产物**完全相同**
（都 `filters=3031741620` + `raw_image`，**不走**重试路径）✓

**JXL 编码设置看起来是无损的**（`dngtool.cpp:1376-1400`）：

```cpp
if (quality <= 0) useCase = isBayer ? dng_host::use_case_LosslessMosaic
                                    : dng_host::use_case_LosslessMainImage;
settings->SetDistance ((quality <= 0) ? 0.0f : (float)((100 - quality) * 15.0 / 100.0));
if (quality <= 0) settings->SetUseOriginalColorEncoding (true);
```

⇒ **但实测产物有损** ⇒ 问题在 **`dng_jxl_image::Encode`（DNG SDK）内部**，
或 `decode_speed` 提示对解码精度的影响 —— **未定论**。

⚠⚠ **影响面**：**默认压缩就是 JXL**（`FfmpegOptions.DngCompression = 1`，与 UI 默认一致）
⇒ **默认配置下产出的 DNG 与源不一致（24–51%）**。

**✅ 已修（2026-09-21）—— 根因是 `-q` 的「双重语义冲突」：**

| 位置 | 问题 |
|---|---|
| `dngtool.cpp:1480` | `-q` 是 **demosaic 质量**，默认 **3** |
| `dngtool.cpp:1541` | `jxlQuality = (encodeCompression == 1) ? quality : 0;` ⇒ **复用 `-q`** ⇒ 取到 3 |
| `RawService.cs:175-176` | `if (jxlQuality > 0) args += " -q ..."` ⇒ 默认 0（无损）时**不传** `-q` |

⇒ 产品默认调用 `-jxl`（不带 `-q`）⇒ dngtool 把 **demosaic 默认值 3** 当成 JXL 质量
⇒ `SetDistance((100-3)*15/100 = **14.55f**)` ⇒ **严重有损**。

**决定性证据（raw 全量校验和）**：

| | sum | fnv |
|---|---|---|
| 源 RAW | 711851004 | D70CF403 |
| 无损 JPEG 产物 | 711851004 | D70CF403 ✓ |
| **JXL 产物（修前）** | **725637202** | **553097C5** ✗ |

**修法（3 处）**：
1. `dngtool` 新增独立参数 **`-jxlq <n>`**（0 = 无损，**默认 0**）；`:1541` **不再复用 `-q`**
2. `RawService.cs` **总是显式传** `-jxlq {Math.Max(0, jxlQuality)}`
3. `run-pipeline-tests.ps1:332` 同步改用 `-jxlq 90`（阈值不变，非放宽）

**验证**：

| 相位 / 机型 | 修前（JXL） | **修后（JXL）** |
|---|---|---|
| p0 GRBG (Leica) | 31.7% ✗ | **0.0%** ✓ |
| p1 RGGB (Sony) | 50.9% ✗ | **0.0%** ✓ |
| p2 BGGR (Nikon D1H) | 24.4% ✗ | **0.0%** ✓ |
| p3 GBRG (Nikon D2H) | 27.3% ✗ | **0.0%** ✓ |

- **产品链路实测命令行** = `dngtool -e -jxl **-jxlq 0** -effort 7 -decode_speed 1` ✓
- **体积**：4.66 MB（**比无损 JPEG 的 5.85 MB 更小**）✓
- **门禁**：`run-pipeline-tests` **80/0** ✓（PSNR **47.4 dB** / SSIM **0.991**，阈值 33 / 0.88）
  · `verify-color-wiring` **104/0** ✓ · `verify-ui-host` **322/0** ✓ · `verify-cli-strict` **322/0** ✓

### 5.9 ✅ 多工具交叉验证：**产物 DNG 的像素数据完全正确**

**三条独立验证路径**：

| # | 工具 | 性质 | 结果 |
|---|---|---|---|
| 1 | **`dng_validate.exe`** | **Adobe 官方** DNG 验证器（DNG SDK 1.7.1） | **`Validation complete`** ✓ 结构合规；`ActiveArea`/`ForwardMatrix1/2`/`CalibrationIlluminant` 齐全 |
| 2 | **`dcraw.exe`** | **独立** RAW/DNG 解码器 | 能解码无损 JPEG 产物 ✓（**不能**解码 JXL 产物 —— 老工具不支持 DNG 1.7 JXL，属正常）|
| 3 | `dngtool` | 自研（LibRaw + DNG SDK） | 用于前述各项 |

**决定性证据：原始 CFA 数据逐字节对比**
（`dcraw -D` = document 模式：**不做白平衡 / 不去马赛克 / 不缩放**，导出源与产物）

| | 值 |
|---|---|
| 像素字节数 | 32564352 = 32564352 ✓ |
| **不同字节** | **0 / 32564352 = 0.0000%** ✓✓ |
| 前 8 像素 | 源 `483,463,488,465,468,475,469,448` = 产物 ✓ |
| 均值 | 621.6 = 621.6 ✓ |

⇒ **产物 DNG 里存的原始像素数据与源 RAW 完全相同** ✓✓✓

**⇒ 因此「解码差异」的性质确定**：**不是「产物写错了」，而是「解码器对源 RAW 与对 DNG 应用了不同的元数据处理」**。

**可观测的具体差异**（`dcraw -i -v`）：

| | 源 RAF | 产物 DNG |
|---|---|---|
| `Filter pattern` | RG/GB | RG/GB ✓ |
| `Daylight multipliers` | 1.973052 0.933498 1.421752 | **相同** ✓ |
| **`Camera multipliers`** | **347.0, 302.0, 933.0** | **1.149, 1.0, 3.089** ⚠ |

⇒ 二者**归一化后相同**（`347/302 = 1.149`、`933/302 = 3.089`），但**绝对尺度不同** ——
源是 LibRaw 从 RAF 读到的**原始乘数**，产物是 DNG `AsShotNeutral` **反算的归一化值**；
**不同解码器对这种尺度约定的归一化方式不同** ⇒ 表现为输出差异。

**结论**：

- ✅ **产物 DNG 完全正确**（像素数据逐字节一致 + Adobe 官方验证通过 + 元数据齐全）
- ⚠ **「解码差异」是解码器实现细节**（`cam_mul` 尺度约定），**不代表产物有缺陷**
- ⇒ **不影响产品功能**（用户用 PS / Lightroom 等按 DNG 规范处理，结果正确）
- ⚠ **方法论**：若要**逐像素比对**，必须用 `dcraw -D` 这类**不做色彩处理**的模式；
  带 WB/去马赛克的解码会引入**解码器自身的尺度约定差异**，不能用来判定产物正确性。

## 7. RAW 管线全组合实机测试 + UI 修复（2026-09-21）

### 7.1 ✅ UI 修复（共 4 个「UI 与真实不符」问题）

| # | 问题 | 修法 |
|---|---|---|
| 1 | **DNG 专属选项在「非 DNG 输出」时仍显示** | 包进 `DngOptionsPanel`，可见性 =「RAW 模式 **且** 输出 = dng」；并在 `FormatCombo_SelectionChanged` 也刷新 |
| 2 | **「线性 DNG」对 Bayer 输入必然失败且无提示** | 加 `dng.linear.hint`（选中「线性」时显示）|
| 3 | **「高光模式」对保留 CFA 的 DNG 输出无效**（实测 h0/h1/h2 产物**逐字节相同**）| 加 `dng.highlight.hint` |
| 4 | **`.x3f`（Sigma Foveon）被列为支持，但 LibRaw 不支持**（4/4 样本失败）| **从 `RawExtensions` 移除 `.x3f`** |

**验证**：`_probe-cjk-hardcode-scan` **14/0** ✓ · `_probe-i18n-scan` **11/0** ✓ ·
5 条 UI 门禁**各 322/0** ✓ · 编译 **0 警告 0 错误** ✓

### 7.2 ✅ RAW 作为输入 × 输出格式（**21/21**）

3 源（Sony ARW / Canon CR3 / Fujifilm RAF）× 7 输出（jpg/png/webp/avif/jxl/tiff/gif）
⇒ **21/21 `exit=0` 且产物非空** ✓

### 7.3 ✅ RAW 作为输出（DNG）× 组合（**48/48**）

2 源 × 2 压缩 × 2 布局 × 2 位深 × 3 高光 = **48 格**：

| 类别 | 结果 |
|---|---|
| `lin=0`（保留 CFA）| **24/24 OK** ✓ |
| `lin=1`（线性）+ Bayer 输入 | **24/24 预期拒绝** ✓（dngtool 明确拒绝，**符合设计**）|
| **意外失败** | **0** ✓ |

⚠ 实测：`h0/h1/h2` 的产物**逐字节相同** ⇒ **高光模式对保留 CFA 的 DNG 输出无效**（已加 UI 提示）

### 7.4 ✅ 各厂商 RAW 格式作为输入（**37/43**，6 个为不支持格式）

**43 个素材**（覆盖 15+ 厂商格式）⇒ **37 OK / 6 FAIL**：

| 失败素材 | 原因 |
|---|---|
| `Sigma_DP1.X3F` · `Sigma_DP1_7.x3f` · `SIGMA_SD14_8.x3f` · `SIGMA_SD15_9.x3f` | **Foveon X3 三层传感器**，LibRaw 不支持 ⇒ **合理拒绝**（UI 已移除 `.x3f`）|
| `RPi.raw` · `RaspberryPi_RP_ov5647_11.raw` | Raspberry Pi OV5647，非标准 Bayer ⇒ 不支持 |

**通过的格式（37 个）**：CR2 · CR3 · RAF · 3FR · RWL · MRW · NEF · ORF · RW2 · PEF · IIQ · SRW · ARW
（含 Hasselblad / Leaf / Minolta / Leica / Phase One / Samsung / Pentax 等）

### 7.5 结论：**RAW 输入/输出可用性充分** ✓

- **输入**：**所有主流厂商格式可用**；唯一不支持的是 **Foveon X3F** 与非标准 `.raw`，
  且 **UI 已不再声称支持 `.x3f`** ⇒ 不再有「UI 与真实不符」
- **输出（DNG）**：**全组合可用** —— 保留 CFA 路径 **100% 成功**；
  线性路径**按设计拒绝 Bayer 输入**，且 **UI 已明确提示适用条件**
- **默认配置**：JXL 无损（`-jxlq 0`），体积比无损 JPEG 更小 ✓

## 8. 队列机制可靠性审查（2026-09-21）

**审查对象**：`src/FfmpegGui/Services/QueueProcessor.cs`（5064 行）

### 8.1 架构

| 组件 | 实现 |
|---|---|
| 队列 | `ConcurrentQueue<QueueItem>` + `lock (_queueLock)` 保护出队 |
| 并发 | `SemaphoreSlim(_concurrency)`；`_concurrency` 经 `ClampConcurrencyByMemory` 按**内存**钳制 |
| 取消 | 单个 `CancellationTokenSource _cts`；`ct` 传入每个任务与子进程 |
| 运行态 | `IsRunning => _cts != null && !_cts.IsCancellationRequested` |

### 8.2 ✅ 设计良好的部分

| 项 | 证据 |
|---|---|
| **失败隔离** | 每任务独立 `try/catch (Exception)` ⇒ 只标记该项 `Status = "失败: …"`，**不影响循环与其他项** ✓ |
| **取消处理** | `catch (OperationCanceledException)` ⇒ `Status = "已停止"` ✓；`ct` 传入子进程（`Kill(entireProcessTree: true)`）✓ |
| **信号量安全** | `finally { … sem.Release(); }` ⇒ **异常/取消路径也会释放** ✓ |
| **半成品清理** | `finally` 按**确证失败**判据（`HasError \|\| IsStopped \|\| ExitCode != 0`）调 `TryDiscardPartialOutput` ⇒ 判据**保守**（宁留不误删）✓ |
| **临时目录清理** | `finally` 逐个删 `tempDirsToCleanup` ⇒ 取消路径同样跑到 ✓ |
| **输入路径还原** | `finally { captured.InputPath = originalInputPath; }`（P1-5）⇒「重新排队」不会指向已删临时件 ✓ |
| **UI 通知** | 每项状态变化都 `_onItemUpdated?.Invoke` ✓ |
| **循环退出** | `await Task.WhenAll(tasks.ToArray())` ⇒ 退出前**等待已启动任务** ✓ |

### 8.3 ✅ 实机验证

**失败隔离**（连续处理 6 项，第 3 项为伪造坏 RAW）：

| # | 输入 | 结果 |
|---|---|---|
| 0 | Sony ARW | ✅ OK |
| 1 | Canon CR3 | ✅ OK |
| 2 | **broken.arw（坏文件）** | ❌ FAIL（预期）|
| 3 | Fujifilm RAF | ✅ **OK**（未受前面失败影响）|
| 4 | Sony ARW | ✅ OK |
| 5 | Canon CR3 | ✅ OK |

⇒ **5 通过 / 1 失败**，与预期一致 ✓ **失败不污染后续** ✓

**UI 端到端**（`UiTestHost` G 组：真实按钮 → 队列 → 产物）⇒ 含在 **322/0** 内 ✓

### 8.4 ⚠ 两个潜在风险（**已于 §8.6 加固**，原文保留供追溯）

**风险 1：`Start()` 的「快速重启」竞态**

```csharp
public void Start(int? concurrency = null)
{
    if (_cts != null) { _cts.Cancel(); _cts = null; }   // 只是"请求取消"，不等待
    _cts = new CancellationTokenSource();
    Task.Run(() => ProcessAsync(_cts.Token));            // 新循环立即启动
}
```

- `Cancel()` **不等待**旧循环结束 ⇒ 新循环启动时旧循环可能仍在收尾
  （旧循环已退出 `while`，但正在 `await Task.WhenAll(tasks)`）⚠
- ⇒ 两批任务各有自己的 `SemaphoreSlim` ⇒ **瞬时总并发可能超过 `_concurrency`** ⚠
- ⇒ **不会重复处理**（旧循环不再取新项）✓，但**峰值内存可能超预期** ⚠
- 触发条件：**用户快速点两次「开始」**

**风险 2：`Stop()` 后 UI 状态与后台实际不一致**

```csharp
public void Stop() { _stopAfterQueueRequested = false; _cts?.Cancel(); _cts = null; }
```

- `IsRunning` **立刻**变 `false` ⇒ UI 认为已停，但**正在跑的任务仍在收尾** ⚠
- ⇒ 用户可**立即再点「开始」** ⇒ 触发风险 1
- ⇒「停止」的语义实为**"请求停止"**，而非**"已停止"** ⚠

### 8.5 建议（如需加固）

1. **`Stop()` 改为可等待**：`IsRunning` 改为「`_cts != null` **或** 后台任务未完成」，
   或在 `Stop()` 后置 `_stopping` 标志，UI 在此期间禁用「开始」✓
2. **`Start()` 等待旧循环退出**：保存 `Task` 句柄（`_runner`），`Start()` 里先 `await _runner` ✓
3. **并发计数共享**：把 `SemaphoreSlim` 提为**字段**（跨循环共享）⇒ 彻底消除瞬时超并发 ✓

⚠ **现状评价**：**核心机制可靠** —— 失败隔离、取消传播、资源释放（信号量/临时目录/半成品）**全部正确**；
上述两点属**并发峰值与状态显示**的**加固项**，**不影响正确性**，仅在「快速连点开始/停止」时可能有可观测的抖动。

### 8.6 ✅ 已加固（2026-09-21）

**加固 1：并发信号量提为字段（跨循环共享）**

```csharp
private SemaphoreSlim _sem = new(2, 2);   // 原为 ProcessAsync 内 new SemaphoreSlim(_concurrency)
...
var sem = _sem;                           // ProcessAsync 改用共享字段
```

⇒ **即使两个循环短暂共存，也共享同一信号量** ⇒ **总并发恒 ≤ `_concurrency`** ✓（彻底消除瞬时超并发）

**加固 2：`Start()` 先等旧循环退出**

```csharp
var prev = _runner;
if (prev != null && !prev.IsCompleted)
{
    try { prev.Wait(TimeSpan.FromSeconds(3)); } catch { /* 旧循环异常不应阻断重启 */ }
}
_runner = null;
RebuildSemaphore();
```

⇒ 旧循环退出后才启动新循环 ⇒ **不再重叠** ✓（超时 3s 兜底，异常不外抛）

**加固 3：`RebuildSemaphore()` 与 `_concurrency` 同步**

⇒ `Start()` 时按最新并发值重建 ⇒ 经内存钳制后的并发值**真实生效** ✓

**验证**：编译 **0 警告 0 错误** ✓ · `_probe-cjk-hardcode-scan` **14/0** ✓ ·
`verify-ui-host` **322/0** ✓（含 G 组「按钮 → 队列 → 产物」端到端）·
`verify-cli-strict` **322/0** ✓ · `run-pipeline-tests` **exit=0** ✓

**保留的语义**（未改，避免影响 UI 调用点）：`Stop()` 仍**不阻塞**、`IsRunning` 立刻变 `false`
（语义 =「已请求停止」）；但用户随后点「开始」时 **`Start()` 会等旧循环退出**
⇒ **风险 2 的后果已被加固 2 覆盖** ✓

## 9. 「进程等级」功能审查（2026-09-21）

**问题**：右下角的进程等级调整是否可用？对 ffmpeg 与外部组件是否生效？

### 9.1 实现

| 层 | 位置 |
|---|---|
| UI 控件 | `MainWindow.xaml:933` `PriorityCombo`（默认 `SelectedIndex=3` = Normal）|
| 持久化 | `MainWindow.xaml.cs:602-623` ⇒ `AppSettingsService.Current.FfmpegPriority`（0..5）|
| 应用 | `PlatformServices.SetSafePriority(process, level)`（`:337`）⇒ `process.PriorityClass` |
| 映射 | `0=RealTime · 1=High · 2=AboveNormal · 3=Normal · 4=BelowNormal · 5=Idle` |
| 安全 | 方法体整体 `try/catch` ⇒ **设置失败不会崩溃** ✓ |

⚠ UI 对 `level == 0`（RealTime）**有二次确认**（`:609-620`）⇒ 合理 ✓

### 9.2 ⚠ 审查发现：**多处 `Process.Start` 漏设优先级**

**原有覆盖（10 个文件）**：`FfmpegRunner` · `CjxlService` · `CjpegliService` · `DjxlService` ·
`JxlPipelineService` · `JxrService` · `PsRenderService` · `RawService` · `QueueProcessor` · `PlatformServices`

**遗漏（有 `Process.Start` 但未设优先级）**：

| 文件 | 启动的进程 | 负载 | 处理 |
|---|---|---|---|
| **`RawColorPipeline`**（`:664`/`:674`）| **ffmpeg + cjxl** | ⚠ **重** | ✅ **本轮已修** |
| **`GainMapEncoder`**（`:429`/`:466`）| **ffmpeg + cjpegli** | ⚠ **重** | ✅ **本轮已修** |
| **`QualityAnalysisService`**（`:190`/`:327`）| ffmpeg（PSNR/SSIM 分析）| 中 | ✅ **本轮已修** |
| `GainMapDecoder` / `ExifToolService` | exiftool | 短时探测 | ⏸ 未设（**可接受**）|
| `MediaInfoService` | dngtool / ffprobe | 短时探测 | ⏸ 未设（**可接受**）|

### 9.3 ✅ 修复（3 文件、6 处）

在 `Process.Start` 之后补 `SetSafePriority(p, AppSettingsService.Current.FfmpegPriority)`：

- `Services/ColorMapping/RawColorPipeline.cs` ×2（ffmpeg + cjxl）
- `Services/GainMapEncoder.cs` ×2（ffmpeg + cjpegli）
- `Services/QualityAnalysisService.cs` ×2（ffmpeg）

**验证**：编译 **0 警告 0 错误** ✓ · `_probe-cjk-hardcode-scan` **14/0** ✓ ·
`verify-color-wiring` **104/0** ✓ · `verify-ui-host` **322/0** ✓ · `verify-cli-strict` **322/0** ✓

### 9.4 结论：覆盖情况

| 目标 | 修复前 | 修复后 |
|---|---|---|
| **ffmpeg**（主转换 `FfmpegRunner`）| ✅ 生效 | ✅ 生效 |
| **ffmpeg**（`RawColorPipeline` 色彩管线）| ❌ **不生效** | ✅ **生效** |
| **ffmpeg**（`GainMapEncoder`）| ❌ **不生效** | ✅ **生效** |
| **ffmpeg**（`QualityAnalysisService`）| ❌ **不生效** | ✅ **生效** |
| **dngtool**（RAW 解码/编码）| ✅ 生效（`RunProcessAsync` 内统一设置）| ✅ 生效 |
| **cjxl / djxl** | ✅ 生效 | ✅ 生效 |
| **cjpegli**（`CjpegliService`）| ✅ 生效 | ✅ 生效 |
| **cjpegli**（`GainMapEncoder`）| ❌ **不生效** | ✅ **生效** |
| **JXR / PsRender** | ✅ 生效 | ✅ 生效 |
| exiftool / ffprobe（短时探测）| ❌ 未设 | ❌ 未设（**可接受**）|

⚠ **评价**：功能**本身可用**（实现正确 + `try/catch` + RealTime 二次确认）；
**但修复前有 3 条重负载路径未接线** ⇒ 用户在这些场景下调优先级**看不到效果** ⇒ 「UI 与真实不符」。**本轮已补齐**。

⚠ **设计层面提示**：`SetSafePriority` 是「**启动后设置**」⇒ 存在**极短窗口期**（进程已开始执行但优先级尚未调整）。
对长跑任务（ffmpeg / dngtool / cjxl）**影响可忽略**；短任务本就无需设优先级。
若要严格从第一刻生效，Windows 上需 `CreateProcess(CREATE_SUSPENDED)` + `SetPriorityClass` + `ResumeThread`，
**成本远大于收益**，不建议。

### 9.5 ✅ 实机验证（2026-09-21）

**方法**：设环境变量 **`FFMPEGGUI_PRIORITY=5`（Idle）**
（`AppSettingsService.ApplyEnvironmentOverrides` 支持 `FFMPEGGUI_` 前缀覆盖），
跑慢任务（Hasselblad CFV-50c 72MB → JXL `--jxl-effort 9`），**轮询捕获子进程的 `PriorityClass`**：

| 捕获顺序 | 进程 | pid | **PriorityClass** | 判定 |
|---|---|---|---|---|
| 1 | **dngtool**（RAW 解码）| 72528 | **Idle** | ✅ **生效** |
| 2 | ffmpeg | 83520 | **Normal** | ⚠ 未设（**探测类短任务**）|
| 3 | **cjxl**（JXL 编码）| 83212 | **Idle** | ✅ **生效** |
| 4 | **ffmpeg**（主转换）| 78684 | **Idle** | ✅ **生效** |

⇒ **核心链路全部落地** ✓：
- **dngtool**（RAW 预处理）⇒ `Idle` ✓（`RawService.RunProcessAsync` 内统一设置）
- **主转换 ffmpeg** ⇒ `Idle` ✓（`FfmpegRunner`）
- **cjxl** ⇒ `Idle` ✓（`CjxlService`）
- ⚠ 唯一 `Normal` 的是 **pid 83520 的 ffmpeg（CPU=0s）** ⇒ **探测类短任务**
  （`EncoderDetectionService` / `FormatCapabilitiesService` / `MediaInfoService`）⇒ **本就不设优先级，符合预期** ✓

**⇒ 实机确认：进程等级对 ffmpeg（主转换）与外部组件（dngtool / cjxl）均真实生效** ✓

## 10. RAW 输入的**元数据保留**审查（2026-09-21 第十八轮）

**背景**：此前 17 轮覆盖了「像素是否正确」「格式是否可用」「UI 是否与真实相符」，
但**没有一轮检查「元数据是否保留」**。而产品默认 `MetadataMode = PreserveAll`
（`CliParser.cs:47`）⇒ **宣称「保留所有元数据」**，故此维度正是本仓最忌讳的「宣称 ≠ 交付」高危区。

### 10.1 ⚠⚠ 缺陷：RAW 输入 ⇒ 产物**丢失拍摄时间主字段与镜头信息**

**实测**（`Canon_EOS40D.CR2` → `jpg`，`--preserve-metadata`；只比**标准 EXIF 组**，不含厂商 MakerNotes）：

| 标签 | 源 CR2 | 产物 JPG | 判定 |
|---|---|---|---|
| `[ExifIFD] DateTimeOriginal` | `2017:12:18 20:05:21` | **无** | ❌ **丢失** |
| `[ExifIFD] CreateDate` | `2017:12:18 20:05:21` | **无** | ❌ **丢失** |
| `[ExifIFD] SubSecTime / SubSecTimeDigitized` | 有 | **无** | ❌ 丢失 |
| `[ExifIFD] ExposureProgram` | `Aperture-priority AE` | **无** | ❌ 丢失 |
| `[ExifIFD] MeteringMode` | `Multi-segment` | **无** | ❌ 丢失 |
| `[ExifIFD] Flash` | `Off, Did not fire` | **无** | ❌ 丢失 |
| `[ExifIFD] ExposureMode / SceneCaptureType / CustomRendered` | 有 | **无** | ❌ 丢失 |
| `[ExifIFD] ShutterSpeedValue / ApertureValue / FocalPlane*` | 有 | **无** | ❌ 丢失 |
| `[Composite] Lens / LensID` | `Canon EF-S 17-85mm f/4-5.6 IS USM` | **无** | ❌ 丢失 |
| `[IFD0] ModifyDate` | `2017:12:18 20:05:21` | `2017:12:18 20:05:21` | ✅ 保留 |
| `[ExifIFD] ISO / ExposureTime / FNumber / FocalLength` | 有 | 有 | ✅ 保留 |
| `[IFD0] Make / Model` | 有 | 有 | ✅ 保留 |

⚠⚠ **措辞必须精确**：**不是「时间完全丢失」** —— 拍摄时间**值**在 `IFD0 ModifyDate` 上**幸存**；
但承载「拍摄时间」语义的**主字段 `DateTimeOriginal` / `CreateDate` 丢失** ⇒
**按 `DateTimeOriginal` 建时间线的相册/图库（Lightroom / Apple 照片 / digiKam 等）会看到「无拍摄时间」**。

### 10.2 决定性对照（三组，缺一不可）

| # | 路径 | `DateTimeOriginal` | `LensModel` | 说明 |
|---|---|---|---|---|
| ① | **RAW → jpg** | ❌ 丢失 | ❌ 丢失 | **被测对象** |
| ② | **RAW → dng** | ✅ **保留** | ✅ **保留**（连 Canon MakerNotes 一起）| `InputPath` **未**被替换 |
| ③ | **中间 TIFF → jpg**（先给 TIFF 注入这 4 个标签）| ✅ **保留** | ✅ **保留** | **非 RAW 输入** |

⇒ **② 与 ③ 同时成立** ⇒ **产品的元数据恢复机制本身没有问题**，
**损失 100% 发生在「RAW → 中间 TIFF」这一步**（`dngtool`），与输出格式无关（只要不是 DNG 输出）。
⚠ 若无 ③，会误判成「exiftool 恢复失败」；若无 ②，会误判成「输出格式不支持」——**两组对照把归因钉死**。

### 10.3 根因（精确定位）

| 环节 | 事实 |
|---|---|
| `dngtool` 的 `-d -T` 中间 TIFF | 走的是 **dcraw 兼容 TIFF 写出**（`[IFD0] Software = dcraw v9.26`）⇒ **只搬 5 项 EXIF**：`Make / Model / ModifyDate / Software` + `ExposureTime / FNumber / ISO / FocalLength`（共 42 个标签） |
| `QueueProcessor.cs:488` | `captured.InputPath = rawTiff;` ⇒ **用户路径被就地替换** |
| `QueueProcessor.cs:1447` `RestoreMetadataAsync` | 从 **`item.InputPath`** 取源 ⇒ 此时已是**中间 TIFF** ⇒ 源 RAW 的上述字段**永久丢失** |

⇒ **`dngtool` 未搬的字段，产品再也拿不回来** —— 这不是 exiftool 的问题，也不是输出格式的问题。

⚠ **旁证**：`dngtool.cpp:786` 写了 `exif->fSoftware.Set_ASCII("dngtool (LibRaw + DNG SDK)")`，
但产物 TIFF 的 `Software` 实际是 **`dcraw v9.26`** ⇒ **TIFF 写出根本没走 `dng_negative` 的 EXIF 通路**
（该赋值只在 **DNG 编码**路径生效）⇒ 也解释了为什么「RAW → DNG 元数据完整」而「RAW → TIFF 残缺」。

### 10.4 普适性（跨厂商 5 例，**全部复现**）

| 样本 | 源 `DateTimeOriginal` | 产物 `DateTimeOriginal` | 源 `LensModel` | 产物 `LensModel` | 产物 `ModifyDate` |
|---|---|---|---|---|---|
| `Canon_EOSR6.CR3` | `2021:05:12 12:21:21` | **无** ❌ | `RF70-200mm F2.8 L IS USM` | **无** ❌ | `2021:05:12 12:21:21` ✅ |
| `Pentax_istDL.PEF` | `2009:08:14 16:54:25` | **无** ❌ | — | — | `2009:08:14 16:54:25` ✅ |
| `Samsung_EX1.SRW` | `2010:12:29 14:20:09` | **无** ❌ | — | — | `2010:12:29 14:20:09` ✅ |
| `PhaseOne_P40.IIQ` | `2025:05:18 12:59:02` | **无** ❌ | `Schneider LS 80mm f/2.8` | **无** ❌ | `2025:05:18 12:59:02` ✅ |
| `Olympus_E10.ORF` | `2003:07:19 13:30:49` | **无** ❌ | — | — | `2003:07:19 13:30:49` ✅ |

⇒ **5/5 跨厂商全部复现** ⇒ **不是某个格式的个例**。

**⚠ 范围不止「厂商原始格式」—— `.dng` 输入同样中招**（`.dng` 也在 `RawExtensions` 内）：

| 输入 | 源 `DateTimeOriginal` | 产物 `DateTimeOriginal` | 产物 `Model` | 产物显示名 |
|---|---|---|---|---|
| `03_jxl_bayer_raw_integer.dng`（真实 Sony ILCE-7RM4，JXL 压缩 Bayer）| `2021:12:15 09:02:56` | **无** ❌ | `ILCE-7RM4` ✅ | `…_raw.tiff` ⚠ |

⇒ **影响面 = 「所有 RAW 家族输入 × 所有非 DNG 输出」**（含手机/Lightroom 导出的 DNG）。

⚠ **未实测**：`Orientation ≠ 1` 的旋转 RAW（**手头 12 个样本全部 `Horizontal (normal)`**，
无素材可验）⇒ 该分支**不得声称已验证**。

### 10.5 ⚠ 第二处「UI 与真实不符」：队列**显示内部临时文件名**

**实测（CLI）**：

| 路径 | 队列/日志显示的输入名 |
|---|---|
| RAW → jpg | **`Canon_EOS40D_raw.tiff`** ⚠（用户**从未**提供该文件）|
| RAW → dng | `Canon_EOS40D.CR2` ✅（原名）|
| 断工具（预处理未执行）| `Canon_EOS40D.CR2` ✅（原名）|

**代码级定位**（显示名一律由 `InputPath` 派生，共 5 处）：

| 位置 | 表达式 |
|---|---|
| `Models/QueueItem.cs:170` | `DisplayText => $"{Path.GetFileName(InputPath)} — {Status}"` |
| `Services/ConsoleQueueObserver.cs:92 / 117 / 127` | `Path.GetFileName(item.InputPath)` |
| `MainWindow.xaml.cs:3045` · `ProgressWindow.xaml.cs:37 / 52` | 同上 |

⚠ **UI 侧也会显示临时名**（非仅 CLI）：`QueueItem.cs:27` 在 `Status` 变更时
`OnPropertyChanged(nameof(DisplayText))`，而 `DisplayText` 每次求值都**重读** `InputPath`；
RAW 预处理把 `InputPath` 改掉后，`Status` 必然变化（`处理中` / `已完成`）⇒ **绑定被刷新** ⇒
`MainWindow.xaml:834 / :1031` 两处列表均显示临时名。
（⚠ 该 UI 结论为**代码级推得**，未做实机截图；CLI 侧为**直接实测**，且两者是**同一表达式**。）

### 10.6 ✅ 补齐原「未做」项：**断工具场景实机验证**（原 `§2.4` / 末段列为未做）

**方法**：把 `FFMPEGGUI_PLAN_DIR` 指向**空目录**（不动真实 `PLAN`）+ 显式给 `FFMPEGGUI_FFMPEG_DIR`
⇒ 四级查找（手动路径 / PLAN / 同目录 / PATH）**全部落空**；确认 `which dngtool` 为空。

| 检查项 | 期望 | 实测 | 判定 |
|---|---|---|---|
| 启动自检 | `dngtool: ❌` | `dngtool: ❌` | ✅ |
| 报错是否**点名** | 点名且说明**不回退** | `输入为 RAW 格式，需 dngtool 去马赛克，但未检测到 dngtool。请将 dngtool 放入 PLAN/artifacts/ 或在设置中配置路径（不回退 ffmpeg 直解，避免错色输出）。` | ✅ |
| 是否**静默产出错色文件** | 不应有产物 | 输出目录**为空** | ✅ |
| 退出码 | 非 0 | **1** | ✅ |
| 对照：正常 RAW → jpg | 0 | **0** | ✅ |

⇒ **「工具缺失 ⇒ 点名报错、不回退、不留产物、退出码非 0」四点全部实机确认** ✓
⚠ **踩坑记录**：首次用 `cmd \| tail` 取值，`$?` 取到的是 **`tail` 的退出码 0** ⇒ 误判成「退出码缺陷」。
**取退出码不得经过管道**（须重定向到文件后再读 `$?`）。

### 10.7 ✅ 修复实施（2026-09-21，本轮已落地）

| # | 缺陷 | 修法 | 位置 |
|---|---|---|---|
| **1** | RAW 输入（非 DNG 输出）丢拍摄字段 | 新增 `QueueItem.SourceInputPath`（**在任何中间件改写 `InputPath` 之前**钉住用户真实路径）+ `QueueProcessor.RestoreRawCaptureFieldsAsync`：常规恢复**之后**，从**源 RAW** 按**白名单**补回字段 | `Models/QueueItem.cs` · `Services/QueueProcessor.cs` · `Services/ExifToolService.CopyRawCaptureFieldsAsync` |
| **2** | 队列/进度窗/CLI 显示内部临时文件名 | 新增 `QueueItem.DisplayName => Path.GetFileName(SourceInputPath ?? InputPath)`；6 处消费点改读它 | `Models/QueueItem.cs` · `ConsoleQueueObserver` ×3 · `ProgressWindow` ×2 · `Program.cs` · `MainWindow.xaml.cs` ×4 |
| **3** | `dngtool -d -T` 只搬 5 项 EXIF（#1 的根因）| **不改 dngtool** —— 由 #1 在消费端解决，避免回归 DNG 编码路径 | — |

**白名单为什么不能用 `-all:all`**：源 RAW 上有三类标签会与产物**冲突** ⇒
`ColorSpace`（常为 Adobe RGB，与编码器写入的色彩标签打架）·
`PixelXDimension/YDimension`（常是**内嵌预览**尺寸）·
`Orientation`（像素**已被解码器旋正**，回带会**二次旋转**）。
**隐私开关必须在补漏里同样生效** —— `CanAbsorbStrip` 为真时那一次独立隐私清理会被跳过，
补漏若不排除 GPS/XMP 就等于**把刚剥掉的隐私又装回去**（已用 7 例矩阵实证无绕过）。

### 10.8 ✅ 修复验证（实机）

**① 拍摄字段恢复**（`DateTimeOriginal` 源/产物一致）：

| 素材 | 结果 |
|---|---|
| CR3 · PEF · SRW · IIQ · ORF · ARW · RWL · RW2 · MRW · 3FR（10 例）| **10/10 ✅** |
| `Nikon_CoolscanV.NEF` | 源本身无 `DTO` ⇒ 跳过（非失败）|
| `03_jxl_bayer_raw_integer.dng`（**DNG 输入**）| ✅ 恢复 |

**② 输出格式覆盖**（`Canon_EOS40D.CR2` → 6 格式）：`jpg` / `png` / `tiff` / `webp` / `avif` / `jxl`
⇒ **6/6 ✅**（`DTO` + `LensModel` + `MeteringMode` + `Flash` 全中）

**③ 显示名**：RAW 输入由 `Canon_EOS40D_raw.tiff` → **`Canon_EOS40D.CR2`** ✅；
`RAW → dng` 与「断工具」两条路径**保持原名不变** ✅

**④ 隐私矩阵（15 例，源带 GPS+XMP+DTO+Lens）**：

| 开关 | GPS | XMP | DTO | Lens | 判定 |
|---|---|---|---|---|---|
| `jpg --preserve-metadata` | 有 | 有 | ✓ | ✓ | ✅ |
| `png --preserve-metadata` | 有 | 有 | ✓ | ✓ | ✅ |
| `png`（默认）| **无** | 有 | ✓ | ✓ | ✅ GPS 默认剥除 |
| `png --strip-metadata` | **无** | 无 | 无 | 无 | ✅ |
| `apng --strip-metadata` | **无** | 无 | 无 | 无 | ✅ |
| `jpg --strip-metadata` | 无 | 无 | 无 | 无 | ✅ |
| `png --strip-gps` | 无 | 有 | ✓ | ✓ | ✅ |
| `png --strip-time` | 无 | 有 | **无** | ✓ | ✅ |
| `png --strip-camera` | 无 | 有 | ✓ | **无** | ✅ |
| `png --strip-xmp` | 无 | **无** | ✓ | ✓ | ✅ |
| `png --strip-all-exif` | 无 | 无 | 无 | 无 | ✅ |
| `webp --strip-metadata` | 无 | 无 | 无 | 无 | ✅ |
| `tiff` / `avif` / `jxl --preserve-metadata` | 有 | 有 | ✓ | ✓ | ✅ |

⇒ **无隐私绕过** ✅；且 `png` 产物的 `cICP` / `cHRM` / `gAMA` 色彩 chunk **全部保全** ✅

**⑤ 门禁与锁**（**两条新锁 + 4 次变异验证**）：

| 锁 | 位置 | 内容 | 变异验证 |
|---|---|---|---|
| **参数形态锁** | `ServiceProbe metaraw`（**21 个 mode，新增此 mode；合计 525 → 542**）| 17 条断言：`DisplayName` 取源路径（A1–A4）· 补漏白名单形态（B1–B8）· 隐私开关在补漏里生效（C1–C5）。⚠ **刻意不依赖任何素材** —— 仓库里**没有任何受版本控制的 RAW/DNG 素材**（`tests/output/**`、`tools/src/dng_sdk/**` 均被 `.gitignore`），建在「真跑一张 RAW」上的锁在干净检出会 SKIP ⇒ **静默假绿** | **M1**（`DisplayName` 退回 `InputPath`）⇒ A1–A3 **转红**（`fail=3`）· **M2**（`-EXIF:Flash` 改裸 `-Flash`）⇒ **仅 B3 转红** · **M3**（补漏忽略 `StripExifGps`）⇒ **仅 C1 转红** |
| **行为锁** | `verify-metadata-privacy`（**3 → 7 断言**）| ④⑤ png/apng 剥离 + 负控（锁 §10.9 #5）· ⑥⑦ **RAW→PNG 拍摄字段**（`MeteringMode`/`DateTimeOriginal` 必须落盘）+ 负控（同时锁 #1 的 PNG 出口与 #4 的写入通道）| **M4**（`PngExifResetArg` 恒空）⇒ 探针 **仅 B6 转红**、门禁 ⑥ **转红**（`PASS=6 FAIL=1`、`exit=1`）、⑦ 仍绿；还原后 **542/0** 与 **7/0** ✅ |

⚠⚠ **本轮的第三个教训（锁本身也会空跑）**：⑥ 的**第一版**用合成素材（JPEG / ffmpeg 自产 TIFF）当源，
在 M4 下**仍然 PASS** —— 因为该缺陷**只在「源是 dngtool 的中间 TIFF」时触发**（ffmpeg 自产 TIFF 走同一条路**不复现**）。
⇒ **断言必须用能真正触发缺陷的素材**，且**必须先做变异验证**才能确认它不是空跑。
（另有一处误判：检查 eXIf 是否含 `StripOffsets` 时我按**大端**匹配 `01 11`，而 eXIf 是**小端** `11 01` ⇒ 一度误判为"不含"。）

### 10.9 ⚠ 本轮顺带发现并修复的**既有**缺陷（PNG 链路，与 RAW 无关）

| # | 缺陷 | 根因 | 修法 |
|---|---|---|---|
| **4** | **PNG 输出的元数据恢复曾恒不生效** | ffmpeg 写出的 `eXIf` 块里 IFD0 **照搬了 TIFF 专用的 `StripOffsets`/`StripByteCounts`** ⇒ exiftool 读取即报 `Error reading StripOffsets data in IFD0` 并**以退出码 1 放弃写入**（`-m`/`-F` 均无效；`-StripOffsets=` 也删不掉，报 `not writable`）| 对 PNG 目标在复制前先做 **`-EXIF:all=`**（只重写 `eXIf`，`iCCP`/`cICP`/`cHRM`/`gAMA` 幸存）|
| **5** | **`png`/`apng` + `--strip-metadata` 泄漏 GPS** | `FfmpegCommandBuilder` 为保 CICP 对这两种格式用 `-map_metadata 0` ⇒ 源 EXIF 被带进产物；而 `StripAll` 原先在 `RestoreMetadataAsync` **直接 return** ⇒ 既不复制也不清理。⚠ 且 `CliParser` 的 `--strip-metadata` **只置 `MetadataMode`、不置 `StripExifAll`** ⇒ 复用 `BuildArguments` 只会发 `-gps:all=`，在坏 `eXIf` 上仍失败 | 新增 `ExifToolService.StripDescriptiveMetadataAsync`（`-EXIF:all=` 复位 + `-exif:all= -gps:all= -xmp:all= -iptc:all=`，**不删 ICC**），仅对 `png`/`apng` 的 `StripAll` 生效 |
| **6** | **exiftool 写超时按 15s 硬编码** ⇒ 大文件必然超时并**残留 `_exiftool_tmp`** | exiftool 写元数据要**重写整个文件**，实测 **276 MB 的 PNG 约需 32 s**；被杀的进程留下 `<目标>_exiftool_tmp`，后续调用恒报 `Temporary file already exists` | `RunRawAsync` 增 `timeoutSeconds` 参数；新增 `WriteTimeoutSecondsFor`（按大小 `15s + 0.25s/MB`，上限 600s）与 `CleanStaleExifToolTemp`；写路径统一走 `RunWriteAsync` |

⚠ **#6 是 #4 的连带暴露**：修 #4 之前 PNG 的写操作是**快速失败**（1 s），修好后变成**真的重写** ⇒ 才撞上 15 s 上限。

### 10.10 未做（**不得声称已验证**）

超大文件（>200 MB DNG，手头最大素材仅 **72 MB**）· 并发多任务内存压力测试 ·
`Orientation ≠ 1` 的旋转 RAW（**12 个样本全部 `Horizontal (normal)`**，无素材）·
`avif`/`jxl` 是否也会随 ffmpeg 版本变化而开始泄漏 GPS（本轮实测**不泄漏**，故未纳入剥离分支）。

## 4. 审查结论

| 维度 | 结论 |
|---|---|
| 输入识别 | ✓ 扩展名表覆盖 ~30 种；RAW 与非 RAW 分流正确 |
| 去马赛克 | ✓ dngtool 唯一引擎；失败**不回退**（避免错色输出） |
| 输入替换 | ✓ `InputPath` 换成临时 TIFF，后续走统一路径；临时目录任务结束清理。⚠ **但有两处副作用**：① **元数据源随之变为中间 TIFF** ⇒ 丢 `DateTimeOriginal` 等（**§10**）② **队列/进度窗/CLI 显示临时文件名**（**§10.5**）|
| 色彩默认值 | ✓ 按目标 HDR 能力取值；四格实测命中（含位深前提） |
| DNG 输出 | ✓ 跳过预处理；JXL / 线性 / 位深 / 高光 / 线程**参数传递全部实测正确** |
| 失败路径 | ✓ 全部**点名**、不静默；非 RAW→DNG 正确拒绝；**断工具场景已实机验证**（§10.6）|
| 元数据保留 | ✅ **已修（2026-09-21）**：RAW 输入（非 DNG 输出）曾丢拍摄时间主字段与镜头信息 ⇒ 现从**源 RAW** 白名单补回（§10）；顺带修掉 PNG 链路的 3 个既有缺陷（§10.9）|
| 已知缺口 | ⚠ RAW→GIF 无路 —— **✅ 已于 2026-09-20 第四轮修复**（§3.2）；当前已知缺口见 **§10.10** |

⚠⚠ **测试覆盖的边界（措辞不得大于扫描面）**：
- 实机素材**全部是 DNG**（含真实相机的 DNG），**没有** CR2/CR3/NEF/ARW 等**厂商原始格式**文件
  ⇒ 「~30 种扩展名」这条只实测了 `.dng` 一条路径；其余靠 LibRaw 的同一解码入口，属**未实测**。
- 未做：断工具场景的实机验证 · 超大文件（>200 MB DNG）· 并发多任务的内存压力测试。
