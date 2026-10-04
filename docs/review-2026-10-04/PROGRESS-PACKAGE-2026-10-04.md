# 任务进度打包（2026-10-04）

> 状态：**进行中 / 已暂停**。用户指令「停止手里的任务，直接打包当前任务进度」⇒ 本文件为阶段性快照，
> **不含任何新代码改动**。所有源码改动在 14:04–14:05 之间已落地并重建（见 §5 重建状态）。
> 门禁全量 67 条尚未跑完（见 §4）。

---

## 1. 本期目标回顾

1. 修复「RAW 转 TIF 无法指定目标色彩空间」缺陷 → 已扩展为「高级色彩参数」复选框语义重构 + RAW 默认输出档修正。
2. 用户裁定：**复选框是纯 UI 开关**，勾选/取消不得改变策略；模式 1（Recommended）允许 `auto` 精确参数；模式 2/3/4 必须手动指定。
3. RAW 转 TIF 例外档：默认 **16-bit + Rec.2020**（SDR）。
4. 完整审查整个色彩实现的正确性 → 4 条只读审计线（A/B/C/D），发现并修复 3 个 P1 缺陷，补 2 条永久门禁。

---

## 2. 已完成的改动（已落地、已重建）

### 2.1 复选框语义重构（P1-1，2026-10-04）
- `Models/FfmpegOptions.cs`：新增 `HasExplicitColorTarget`（仅认 `ColorPrimaries`/`ColorTrc`，**不含** `ColorMatrix`），
  与既有 `HasExplicitColorParams`（含 Matrix）解耦。文档注释明确 `ColorMatrix` 是**输入声明**而非目标轴。
- `Services/FfmpegCommandBuilder.Decision.cs`：`bool advanced = options.HasExplicitColorTarget;`（原 `HasExplicitColorParams`）。
- `Services/ColorEncodingHelper.cs`：3 处改用 `HasExplicitColorTarget`。
- `Services/FfmpegCommandBuilder.cs`：`noExplicitTarget` / `NeedsHdrToSdrTonemap` 改用 `HasExplicitColorTarget`。
- `tests/ServiceProbe/Program.cs`：新增永久锁「ColorMatrix must not eat ColorSpace target」（线 92→93），
  回归锁；**变异验证**：故意用 `ColorMatrix` 吃掉目标 → 门禁转红，确认锁有效。

### 2.2 完整审查修复（3 个 P1，2026-10-04）
- **JXL 输入目标被吞**（线B P1-1，已修）：SDR JXL 输入声明原先写进 `ColorPrimaries/ColorTrc`（输出目标轴）⇒
  被静默吞掉。现改走 `ColorSourceDeclPrimaries/Trc`（与 RAW 同路）。实测 `-i x.jxl -f png --color-space "Display P3"` ⇒
  产物 `smpte432` ✓。同批把 `ColorIntentFactory`「自产中间件」分支收紧为只认 RAW 与 Ultra HDR 解码。
- **RAW 默认档 HDR 承载力判据错位**（线D P1-2，已修）：旧判据 `BitDepth ?? 8`（请求档），实际交付位深跟随源（RAW 中间件恒 16-bit）。
  现按**实际交付档**判 ⇒ ARW→PNG/JXL 等 16-bit 容器默认档变 `Rec.2020 + PQ`（实测 `rgb48be, smpte2084, bt2020`）；
  `webp`/`jpg`（8-bit 上限）仍为 `bt709/sRGB`；TIFF 例外档仍为 `16-bit + Rec.2020 + SDR`。
- **Ultra HDR 增益图解码**（线D P1-1 部分修）：补 `ColorSourceDeclPrimaries="bt709"; ColorSourceDeclTrc="linear";`
  输入侧声明，并接 `it.HdrPeakNits = o.DecodedUltraHdrPeakNits`。⇒ 用户显式目标在 Ultra HDR 输入下生效
  （实测 `--color-space sRGB` ⇒ `决策：Map`）。

### 2.3 RAW 默认档位深 / TIFF 例外档（2026-10-04）
- `QueueProcessor.cs:640`：`rawTargetBitDepth = captured.Options.BitDepth ?? Math.Min(16, rawCaps.MaxBitDepth);`（原 `?? 8`）。
- `QueueProcessor.cs:608`：RAW 默认档且 Format 为 tiff/tif 时 `BitDepth = 16`（置于用户目标判定**之前**）。
- TIFF ICC 后处理门控放宽：`else if (opts.HasExplicitColorTarget && !string.IsNullOrWhiteSpace(opts.ColorPrimaries))`（原 `HasExplicitColorParams`）。

### 2.4 永久门禁（新增 items 66 / 67，已接线 + 变异验证）
- `tests/scripts/_probe-jxl-input-target.ps1`（第 66 条）：JXL 输入 + `--color-space "Display P3"` ⇒ 产物 `color_primaries=smpte432`；
  负控（不给目标 ⇒ 非 smpte432）。
- `tests/scripts/_probe-raw-default-tier.ps1`（第 67 条）：ARW→PNG 默认 ⇒ `rgb48*+bt2020+smpte2084`；webp ⇒ 非 bt2020；
  tiff ⇒ `16 16 16` + 目标 ICC + 日志目标。
- 两条均在 PS5.1 + 运行器宿主下端到端绿、变异验证转红。

### 2.5 门禁清单接线（67 条）
- `_run-step3-gates.ps1`：`$expectedScriptCount = 67`，`$scriptList` 增 2 条；四类散文计数同步
  （⑰ 散文条数针 + 常量 + 分类账 `4+7+56=67`）。
- 同文件多处门禁脚本修订（`verify-color-token-normalize`、`verify-colorfmt-matrix`、`verify-encoder-knob-liveness`、
  `verify-gainmap-thirdparty`、`verify-jbrd-e2e`、`verify-orientation-rewrap`、`verify-package-smoke`、`_probe-cjk-hardcode-scan`、
  `_lib-axes`、`_lib-matrix`）。

### 2.6 文档
- `CHANGELOG.md` / `CHANGELOG.en.md`：新增「行为变更」三节（复选框语义、完整审查修复、AVIF 增益图 range 标签）。
- `docs/review-2026-10-04/AUDIT-色彩实现-2026-10-04.md` + `audit-A/B/C/D-*.md`：完整审查报告。
- `docs/review-2026-10-04/FIX-P1-1-复选框语义-2026-10-04.md`：P1-1 结案。
- `docs/RAW2TIF_QA_DEFECT_2026-10-03.md`、`docs/RAW_COLOR_TARGET_MODE_PLAN_2026-10-04.md`：缺陷与方案。

---

## 3. 文件改动清单（git status --short 汇总，35 改 + 7 新增）

**产品源码（src/FfmpegGui，已重建）**
- `Models/FfmpegOptions.cs`（+92）、`Models/PresetData.cs`、`CliParser.cs`、`MainWindow.xaml(.cs)`
- `Services/ColorEncodingHelper.cs`、`Services/ColorMapping/ColorIntentFactory.cs`、`Services/ColorMapping/ImageEncoderArgs.cs`
- `Services/ColorSpaceRegistry.cs`、`Services/FfmpegCommandBuilder(.Decision).cs`、`Services/IsoBmffGainMapWriter.cs`
- `Services/QueueProcessor.cs`（+258）、`Services/ColorMapping/RawGamutFit.cs`（新增）
- `Resources/Locales/en-US.json`、`zh-CN.json`

**测试宿主**
- `tests/ServiceProbe/Program.cs`（+285）、`tests/GainMapTestHost/Program.cs`

**门禁脚本（tests/scripts）**
- `_run-step3-gates.ps1`、`_probe-cjk-hardcode-scan.ps1`、`_lib-axes.ps1`、`_lib-matrix.ps1`
- `verify-color-token-normalize.ps1`、`verify-colorfmt-matrix.ps1`、`verify-encoder-knob-liveness.ps1`
- `verify-gainmap-thirdparty.ps1`、`verify-jbrd-e2e.ps1`、`verify-orientation-rewrap.ps1`、`verify-package-smoke.ps1`
- `_probe-jxl-input-target.ps1`（新增）、`_probe-raw-default-tier.ps1`（新增）

**文档**
- `CHANGELOG.md`、`CHANGELOG.en.md`、`README.md`、`README.en.md`、`docs/HANDOVER.md`
- `docs/JXL_RAW_16BIT_ADVISORY_2026-10-02.md`、`docs/TESTING.md`
- `docs/RAW2TIF_QA_DEFECT_2026-10-03.md`、`docs/RAW_COLOR_TARGET_MODE_PLAN_2026-10-04.md`
- `docs/review-2026-10-03/`、`docs/review-2026-10-04/`（新增）

> 合计 +1598 / -152 行（相对 HEAD）。

---

## 4. 门禁全量运行状态（67 条）

| 窗口 | 范围 | 结果 |
|---|---|---|
| 1 | 前 10 条 | ✅ 10/10 绿 |
| 2 | 次 10 条 | ✅ 10/10 绿 |
| 3 | 第 21–30 条 | 🟡 3 红 → 2 已修：`verify-color-token-normalize` 33/0、`verify-gainmap-host` 18/0；**1 待修 `verify-hlg-route`** |
| 4–7 | 第 31–67 条 | ⬜ 未跑（47 条） |

**窗口 3 遗留问题**
- `verify-hlg-route.ps1`（2 FAIL，待修）：HLG 与 PQ 源峰值都被读成 `203 nit` ⇒ `$pk1 -ne $pk2` 不成立；
  PSNR 有限但 < 40 dB 阈值不达标。疑点：日志路径 `<srcdir>/hlg2sdr.log`/`pq2sdr.log` 是否读对、`Get-MeasuredPeak`
  正则 `采用\s*(\d+(?:\.\d+)?)\s*nit` 对 HLG 是否匹配。
- `verify-gainmap-host.ps1` **PS5.1 不兼容**：`-ArgumentList @()` 触发 `ParameterBindingValidationException`，
  运行器宿主（PS7）下绿，但违反双宿主合规要求，需修。

---

## 5. 重建状态

- `src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.dll` mtime = 2026-10-04T14:05，晚于源 `QueueProcessor.cs`（14:04）
  ⇒ 源码改动后已重建。
- ⚠ **未重确认的**：四项目（src + ServiceProbe + UiTestHost + GainMapTestHost）联合重建 + STALE 新鲜度闸，
  本打包未重跑；若恢复工作应先按纪律重建四项目并核 STALE，再续跑门禁。
- 自研引擎为默认路径；legacy 为显式逃生门（`--color-engine legacy`）。

---

## 6. 仍待处理项（恢复后按序）

**P0 / 阻断**
1. `verify-hlg-route.ps1` 转绿（窗口 3 遗留）。
2. `verify-gainmap-host.ps1` PS5.1 兼容修复（双宿主合规）。

**P1 / 规划层裁定**
3. **Ultra HDR 无目标直通**：未给目标时 `决策：None` 直通；因源曲线声明 `linear`、引擎 HDR 认定按曲线类型（PQ/HLG），
   `Ultra HDR → SDR` 仍不挂色调映射（>1.0 截顶）。需在**规划层**决定「线性 HDR 中间件」的 HDR 认定与默认目标。
   （用户目标已生效，仅无目标项未决。）

**P2 / 审计遗留（20+ 项中挑重点）**
4. `tif` 别名广播一致性。
5. `QueueProcessor` 死分支清理。
6. `UseAdvancedColorParameters` 零读取点（语义是否真被消费）。
7. D50 两处定义合并。
8. 多通道增益图逐通道元数据。

**收尾**
9. 跑完窗口 4–7（47 条），整轮 67/67 绿。
10. `pack.ps1 -Mode all` 重新打包 + `verify-release-package` + L3 五套。
11. **提交 / 打标签 `1.6.0BETA4` / 推送**：按纪律需用户明确授权；`git add` 由维护者执行，未经实机测试不提交。

---

## 7. 纪律提示

- 不提交：未经实机测试不得提交；未明确要求不主动提交；`git add` 由维护者执行。
- 改 `src/FfmpegGui` 必须重建四项目并核 STALE 闸；访问 internal 用反射，勿改 `FfmpegGui.csproj`。
- 每条新断言/新锁必须变异验证；改门禁断言须补负控，不得为绿而放宽。
- 长跑（>20 min）前台发起，转后台仍会被回收；超 20 min 须逐窗推进。
- 同文件同刻仅一个写者（含 `docs/**`）；并发 Edit 互覆风险已记入环境坑。

---

*本快照为「停止调试、打包进度」指令的产物，不含任何新代码。恢复工作时从上述 §6 续推。*
