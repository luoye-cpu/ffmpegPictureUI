# D1 · 门禁覆盖账（HEAD = c18dc4a / v1.6.0-beta7，只读审查）

被审对象：`tests/scripts/verify-*.ps1` + `_run-step3-gates.ps1` 清单 + 进程内探针宿主（`tests/ServiceProbe`、`tests/GainMapTestHost`、`tests/{core,color,engine,encode,metadata,gainmap,geometry,io,diagnostics}`、`tests/Ui*`）。
对账来源：`docs/review-2026-10-10/lanes/A1..A5、B1、B3、C1..C3` 的 §0 / §3 / §4。
约束遵守：未改任何源文件、无 git 写操作、未跑整轮门禁、未跑计时；只跑了一条秒级静态探针（§1-U9）。

---

## 0 一页结论

**覆盖计数：本轮已落盘的缺陷主张 N = 49 条 = 有牙 2 / 无牙 16 / 零覆盖 31**（2 + 16 + 31 = 49 ✓）。
逐 lane 分布（`有牙/无牙/零`）：A1 `2/1/5`、A2 `0/3/2`、A3 `0/3/1`、A4 `0/1/5`、A5 `0/2/2`、B1 `0/4/5`、B3 `0/1/5`、C2 `0/0/5`、C3 `0/1/1`。

五条主干结论：

1. **清单本体是干净的，"清单外"才是账**。`_run-step3-gates.ps1` 的 `$scriptList` 实测 **73 条**、`$expectedScriptCount = 73`、`tests/scripts/*.ps1` 在盘 **115 个**（全部 git 跟踪），73 条**逐名存在**、无幽灵条目；不在清单里的 **42 个** 里，只有 3 个是 `verify-*` 命名（`verify-color-matrix-full` / `verify-oracle-matrix` / `verify-release-package`）——其余是 `_lib-*`/`_gen-*`/`run-*`/`_probe-*` 诊断件。**没有一条 2026-10-10 lane 的新红落进这 73 条里的任何断言。**

2. **覆盖账本身被一条守卫自己伪造了**（最要害的一条）。`_probe-test-group-coverage.ps1` 实测 **PASS=8 FAIL=0**（§1-U9），它自己打印 `仅组表有（由专项门禁脚本调用，正常）= 40：bench, bitdepth, faithful, gainmap-encode, gainmap-decode, gainmap-struct, …`——**"由专项门禁脚本调用"这一句是推断，不是判据**。其 ③(b) 取证是"mode 名出现在**任何** `tests/scripts/*.ps1` 的**文本**里"（含注释、含不在清单的脚本、含它自己第 20 行的注释），③(c) 是"本组 `Program.cs` 有分派分支"，⑤ 只拦"声明了却没实现"、**不拦"实现了却没人跑"**。后果：`bench`、`bitdepth`、`gainmap-encode/decode/struct` 经证明零调用点（§1-U10/U11），锁仍全绿。

3. **A3-N1（16-bit 表快路径漏判据）在结构上不可被现有任何断言观察到**，两道独立封锁：① 建表阈值 `pixels < 256_000` ⇒ 不建表，而**全部端到端夹具都是 512×384 = 196,608 px**（§1-U12）⇒ 引擎路永远走解析式；② 唯一的同形对照（`selftest` 0) 表 vs 解析）5 个用例的目标曲线全是 SDR ⇒ `EncodeScale ≡ 1`，正是漏判据的那一位**永远等于常量**（§1-U13）。判"无牙"，不是判"零覆盖"——断言在，操作数空间里没有能触发它的一格。

4. **性能门禁的现状是"有门、门绕开产品在跑的路、而且已红的两格连看守者都没有"**。`bench` 的 `PROBE RESULT: pass=9 fail=2` 没有任何 73 条内的脚本跑它（§1-U10）；三条门槛里只有 `H1 ≤ H2×2.0` 是相对的、另两条是绝对值（`单核 ≥40 MPix/s`、`扩展比 ≥4×`），且 bench **不绑 DLL/构建指纹**（§4）。`PQ→sRGB` 那格 FAIL 的成因（表被 `LinearScale≠1` 恒关掉）与 A3-N1 是同一族，长期以"基线读数"存在而**没进任何清单/红账**。

5. **A2 的 γ2.4 分歧被自家门禁定性成"已知非缺陷"并精确豁免 4 格**。`verify-color-engine-xcheck.ps1` 的 `$knownNonDefects` 逐字写着"zimg 参照口径分歧：zimg 的 bt709 是纯 γ2.4（与定义式分歧）"，豁免 `sRGB→BT.2020` / `BT.2020→sRGB` 共 4 格（2 图 × 2 用例），而 A2 §0#2 把同一件事判为**真缺陷-标准**（光域最大差 4748 LSB@16 = 7.25% FS）。⇒ 这不是覆盖缺口，是**定性冲突**：豁免一旦写下，该缺陷的回归与修复都读不出来。

---

## 1 方法（每个数量都给命令）

纪律：每条"零覆盖/不存在"之前，先对一个已知存在的串做正向命中（表 U-PC）。所有计数命令逐字列在下面，可整条复跑。

| 编号 | 用途 | 命令（在 `C:\PLAN\ffmpegPictureUI` 下） | 读数 |
|---|---|---|---|
| U1 | `verify-*.ps1` 在盘数 | `ls tests/scripts/verify-*.ps1 \| wc -l` | **58** |
| U2 | `tests/scripts` 全部 `.ps1` | `ls tests/scripts/*.ps1 \| wc -l` | **115** |
| U3 | git 跟踪数 / 未跟踪数 | `git ls-files tests/scripts/*.ps1 \| wc -l`；`git ls-files --others --exclude-standard tests/scripts/ \| head` | **115** / **无** ⇒ 115 个都不是产物目录，`output/` 规则没混进来 |
| U4 | 运行器清单条数 | `sed -n '1040,1272p' tests/scripts/_run-step3-gates.ps1 \| grep -vE '^\s*#' \| grep -oE "'[A-Za-z0-9_.-]+\.ps1'" \| tr -d "'" \| sort \| wc -l` | **73**（数组闭合在 `:1272`；起点 `:1040` `$scriptList = @(`） |
| U5 | 受管基线常量 | `grep -n "expectedScriptCount = " tests/scripts/_run-step3-gates.ps1` | `:1039: $expectedScriptCount = 73` |
| U6 | 清单 ⇄ 在盘 双向差 | `comm -23 /tmp/runner_list.txt /tmp/all_scripts.txt`；`comm -13 … \| wc -l` | **清单有而文件不存在 = 0 条**；**在盘而不在清单 = 42 条**（§3 列名） |
| U7 | `docs/TESTING.md §6` 条数 | `grep -nE '^[0-9]+\.' docs/TESTING.md \| awk -F: '$1>=573 && $1<=5498' \| wc -l`（§6 边界：`:573` `## 六、注意事项`，`:5499` `## 七`） | **122 条**（编号 1…122 连续）——它是**教训账**，不是门禁清单；门禁条数住在 `:447` 一句散文里 |
| U8 | TESTING.md 的门禁条数陈述 | `grep -n "脚本门禁清单" docs/TESTING.md` | `:447` "`_run-step3-gates.ps1` 的**脚本门禁清单**当前 **73 条**" ⇒ 与 U4/U5 一致 |
| U9 | **实跑一条守卫看它怎么说自己** | `TMP=… TEMP=… powershell -NoProfile -File tests/scripts/_probe-test-group-coverage.ps1` | `PASS=8 FAIL=0`；打印 `仅组表有（由专项门禁脚本调用，正常）= 40：bench, bitdepth, faithful, firstframe, gainmap, gainmap-decode, gainmap-encode, gainmap-isobmff, gainmap-struct, gamut, icc, icc-dump, orientation, orientmeta, peak, peakmeasure, png3, pq, psnr, simdswitch, tooldetect, ui-…, ultrahdr, xcheck`；另打印 `组表并集 = 66 个 mode；运行器 $Probes = 26 个` |
| U10 | `bench` 有没有被任何清单脚本当参数调用 | 对 U4 的 73 个文件名逐个 `grep -F 'bench' tests/scripts/<f>` | **0 命中**（全 `tests/scripts` 只有 `run-thread-tests.ps1:84` 的 `dng_bench_$t` 文件名，且该脚本**不在清单**）⇒ `bench` 零调用点 |
| U11 | `gainmap-encode/decode/struct` | `grep -rn "gainmap-encode\|gainmap-decode\|gainmap-struct" tests/scripts/` | **NONE**（三个 `Tests.GainMap*.exe` 由谁跑：无人） |
| U12 | 建表阈值 vs 夹具像素数 | `grep -n "256_000" src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs` + `grep -hoE "size=[0-9]+x[0-9]+" tests/scripts/generate-sources.ps1 \| sort -u` | 阈值 `:1087` `pixels < 256_000 ⇒ 不建表`；静像夹具只有 `512x384`(=196,608) 与 `256x256`(=65,536) ⇒ **所有 e2e 门禁永远不建表** |
| U13 | 表 vs 解析同形对照的操作数空间 | `sed -n '35,55p' tests/diagnostics/Modes/selftest.cs`（5 个用例的 `dp/dt`）+ `sed -n '74,106p' tests/diagnostics/DiagnosticsGroup.cs`（`Spec()` 里 `EncodeScale = sdr / Nits(dst.Curve)`） | 5 格目标曲线 ∈ {`iec61966-2-1`, `bt709`} ⇒ `Nits(dst)=203` ⇒ `EncodeScale ≡ 1.0`，**没有一格是 PQ/HLG 目标** |
| U14 | `FindPixelColorEntry` 接线 | `grep -rn "FindPixelColorEntry" --include=*.cs --include=*.ps1 . \| grep -v "/obj/\|/bin/\|tests/output/\|docs/"` | src **1** 处（定义 `ColorTransformPlan.cs:888`）+ `tests/core/Modes/contract.cs` 6 处 + `tests/ServiceProbe/Program.cs` 6 处；`tests/scripts/` **0** ⇒ 生产零调用点，断言只喂手搓串 |
| U15 | `IsValidIccProfile` 的断言极性 | `grep -rn "IsValidIccProfile" --include=*.cs tests/ \| grep -v "/obj/\|/bin/\|tests/output/"` | 4 处调用（`tests/metadata/MetadataGroup.cs:160,243`、`ServiceProbe:1162,7920`），**全部是 `Check(IsValidIccProfile(x), "ICC 魔数有效")` 形正例**，0 处负样本 |
| U16 | 内存钳制口径 | `grep -rn "ClampConcurrency\|TotalAvailableMemoryBytes" --include=*.cs --include=*.ps1 tests/` | **0 命中**（src 侧 `QueueProcessor.cs:57/142/147` 在） |
| U17 | 引擎路挂死守卫 | `grep -n "CancelAfter\|WaitAsync\|Task.WhenAny\|-progress\|Heartbeat" src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs` + `grep -n "RawColorPipeline\|ColorMapping" tests/scripts/verify-ffmpeg-heartbeat.ps1` | 前者命中**只有 `:566` 一句注释**（说落盘走 `FfmpegRunner`）；`RunToEndAsync`（`:1322-1341`）全文无超时/心跳；后者对 `RawColorPipeline` **0 命中**（判据面是 `FfmpegRunner.cs` + `QueueProcessor`） |
| U18 | `IsQueueRunning` 幂等 | `grep -rn "IsQueueRunning" --include=*.cs tests/` | **0 命中**（正例对照：同轮 `grep -rn "JobObject" src/` = 0，`grep -rn "Kill(true" src/` = 20 ⇒ 管道通得到） |
| U19 | `--color-709-curve` / `ColorHdrPeakNits` 测试面 | `grep -rn "Color709Curve\|ColorHdrPeakNits" --include=*.cs --include=*.ps1 tests/` | 709：`tests/engine/Modes/engineab.cs:132-204`（**引擎 H1 端到端真产物字节**）；peak：`peak.cs:58/77`、`peakmeasure.cs:61`、`verify-color-peak.ps1:84/138/139`，**全是 PQ/hable 档，无 HLG L_W 格** |
| U20 | xcheck 格子与豁免 | `sed -n '43,49p' tests/scripts/verify-color-engine-xcheck.ps1`（`$cases` 5 条）+ `sed -n '62,69p'`（`$knownNonDefects` 6 条） | 10 格 = 2 图 × 5 用例；**无 `SDR→PQ` / `SDR→HLG` 用例**；6 条豁免里 4 条的理由逐字是"zimg 的 bt709 是纯 γ2.4（与定义式分歧）" |
| U21 | colormath 容差分档 | `sed -n '61,95p' tests/color/Modes/colormathagainstzimg.cs` | A 档 `1e-5`（6 token）／**B 档 `5e-4`**：`["bt470m"]`、`["smpte431"]`，理由串写"白点位数上限"；A1 实测把锥矩阵末位换成 1.0296 后 `smpte431` 残差 2.36e-4→4.03e-6 ⇒ **B 档容差正好吞掉这条缺陷** |
| U22 | GainMap 往返闭环的阈值与夹具 | `sed -n '1235,1240p' tests/GainMapTestHost/Program.cs`（`var threshold = 30.0`；注释实测 sdr 46.4 / hdr1000 36.5 / hdr4000 **33.6**）+ `sed -n '1286,1342p'`（`CreateHdrTestPixels`，内含逐字注释 `注意: 避免极端饱和色 (JPEG YUV 色度重采样 overshoot → clip)`） | 5 格（sdr/hdr1000/hdr1000rgb/hdr4000/hdr1000ds8）统一 30 dB；hdr4000 余量 **3.6 dB**；夹具刻意避开越界色 ⇒ F3 永不触发 |
| U23 | 增益量化参照是不是自比 | `grep -rn "LogGainToByteScalar" --include=*.cs tests/ \| grep -v "/obj/\|/bin/\|tests/output/"` | `tests/GainMapTestHost/Program.cs:611`、`tests/gainmap-decode/GainMapDecodeGroupD.cs:350` 都用 **`SimdPixelOps.LogGainToByteScalar` 自己**生成 `gainRef` ⇒ 自己跟自己比 |
| U24 | F5/F6 观测面 | `sed -n '580,584p;847,851p' src/FfmpegGui/Services/GainMapEncoder.cs`（AVIF 恒 `"gray"`；`channels = multiChannel ? 3 : 1` + `flags |= 0x80`）+ `grep -rn "multiChannel" tests/gainmap/` + `grep -rn "1999-02-22\|1999/02/22" tests/ src/` | avifgm 唯一编码用例 `GainMapGroup.cs:392 multiChannel: false` ⇒ 分叉格从不构造；rdf 拼错串在 `GainMapEncoder.cs:899`，**tests 侧 0 命中** |
| U25 | S3 剥 ICC 的两格前提 | `sed -n '451,462p;185,195p;219,223p' tests/scripts/verify-color-strategy.ps1` | `L-cicp-only-p3-avif` / `L-cicp-only-hdr-png` 有 `-not $r.Icc`，**且带取数尺正对照**（`前置-取数尺有牙`，`:222-223`）⇒ 尺子不空转；但 png 那格的源是 `src_hdr_pq.png`（generate-sources `:50`：只挂 cICP，**无 iCCP**）⇒ "删已在文件里的 ICC"这个动作从不被要求 |
| U26 | 整轮日志现状（本机不可复核） | `tail -25 tests/output/gates-round-beta7.log` + `grep -n "script-list=" tests/output/gates-round-beta7.log` | `:57` `script-list=73 条（受管基线 73）`；日志**截断**于 `verify-format-regression`，无末轮汇总；`verify-color-wiring` **56/50**、`verify-color-strategy` **47/26** 成片红，成因逐字是 `UnauthorizedAccessException: Access to the path 'C:\Users\20210\AppData\Local\Temp\cms_*.png' is denied` 与 `解码失败 (exit -13)` ⇒ 本机 %TEMP% 拦截，**整轮绿/红基线不可引用** |

**表 U-PC（正向命中自证，证明上面的 grep 管道抓得到东西）**

| 判"0"的主张 | 同轮正例 | 正例读数 |
|---|---|---|
| `bench` 零调用（U10） | `xcheck` 用同一查法 | `verify-color-engine-xcheck.ps1:77` 命中（§1-U27） |
| `FindPixelColorEntry` 生产零调用（U14） | `ColorTransformPlan.cs` 内另一常量 | A4 §1：`grep -c MatrixIsIdentity` = 2（定义 `:940` + 调用 `:727`） |
| 内存钳制零覆盖（U16） | `ComputeAdaptiveThreads` 同查法 | `tests/GainMapTestHost/Program.cs:964` 等 5 处命中 |
| `IsQueueRunning` 零覆盖（U18） | `Kill(true` 同查法 | src 20 处命中 |
| `1999/02/22` tests 零命中（U24） | `XMP-hdrgm:Channels` 同查法 | `verify-gainmap-engine.ps1:249/250` 命中（3/1 正负控对） |
| `IccD50Xy/0.9642/0.34570` tests 零命中 | `ColorantsD50` src 侧 | A1 §3-E 实读 `ColorSpaceRegistry.cs:706` vs `IccProfileBuilder.cs:31` |

| 编号 | 命令 | 读数 |
|---|---|---|
| U27 | `grep -rlE "[\"']xcheck([ \"']\|$)" <清单 73 文件>` | `verify-color-engine-xcheck.ps1` ⇒ 查法有效 |
| U28 | 同查法逐个跑（`bench`/`bitdepth`/`gainmap-encode`…） | `bench`/`bitdepth`/`gamut`/`gainmap-encode`/`-decode`/`-struct` = **NONE**；`icc`/`icc-dump`/`faithful`/`peak`/`peakmeasure`/`psnr`/`pq`/`png3`/`simdswitch`/`firstframe`/`orientation`/`orientmeta`/`ultrahdr`/`gainmap-isobmff`/`gainmap` = 各有归属脚本 |
| U29 | `grep -rn "bench" --include=*.cs tests/ \| grep -v output` | `tests/diagnostics/Program.cs:50`（`mode == "" \|\| mode == "bench"` ⇒ 默认必跑该组时会跑，**但没有清单脚本跑这个组**）、`tests/ServiceProbe/Program.cs:175`（同一 mode 的第二份实现） |

---

## 2 代码面 → 门禁对账表

状态定义：**有牙** = 指名脚本 + 指名断言，且（操作数自由度 ≥3 或有外部/负控对照）；**无牙** = 有断言但满足下列之一：操作数恒等（自由度 <3 / 关键算子被钉成常量）、自己跟自己比、豁免表吸收、缺陷量级 < 自家阈值；**零覆盖** = 无任何断言观测。

### A1 原色 / 矩阵（8）

| # | 缺陷主张（标签） | 状态 | 观测它的脚本·断言 / 无牙理由 |
|---|---|---|---|
| A1-N1 | `BrdfordAdapt` 第三行末元 1.0633（非 1.0296），ICC chad/rXYZ 与参照差 4.9e-4/2.1–2.8e-4（真缺陷-精度） | **无牙** | `tests/color/Modes/colormathagainstzimg.cs` 有 11 格矩阵对拍，但 `smpte431`/`bt470m` 落在 **B 档 `5e-4`**（U21），残差 2.36e-4 恒在阈内；且 B 档理由串把它误记为"白点位数上限"⇒ 容差**按该缺陷的量级定制**。修好（换 1.0296）不会更红，只会从 2.36e-4 变 4.03e-6 ⇒ 断言无判别力 |
| A1-N2 | 产品内两份 D50（`ColorSpaceRegistry.cs:706` 推得 Z/Y=0.825105 vs `IccProfileBuilder.cs:31` 0.8249），`ColorantsD50` 与落盘 `wtpt` 永不相等（真缺陷-精度 + 同一事实两个写者） | **零覆盖** | U18 行末：`IccD50Xy`/`0.9642`/`0.34570` 在 `tests/` **0 命中**；`verify-tiff-icc`/`verify-gainmap-thirdparty` 只读 rXYZ 色度、不读 `wtpt` |
| A1-N3 | GainMap 底图 `gamutMtx == null` 只在 `wideBase` 支出声，sRGB 底（默认）静默"只改标签"（真缺陷-静默失效） | **零覆盖** | `verify-gainmap-engine.ps1` 的 `--jpeg-gain-map-base-gamut bogus` 只测**非法 token ⇒ exit 2**（TESTING.md §3.4 行），`film`/`smpte428`/表外**合法** token 从未进用例；`_probe-gui-control-wiring` 是 XAML 接线锁，管不到运行期分支 |
| A1-N4 | `film` 未收录但已 fail-closed（能力缺口） | **有牙** | `colormathagainstzimg.cs:106` `Check(whyNull.ContainsKey(tok), …)` —— 该 mode 在运行器 `$Probes` 默认清单内（U9 打印 `运行器 $Probes = 26`），实测读数 `tests/output/_baseline_modes/color__colormath.txt` `pass=12 fail=0` |
| A1-N5 | `smpte428` 未收录且已 fail-closed（能力缺口；排除理由与错因另计文档过期） | **有牙** | 同上 `:103-104` 把 `["smpte428"] = "xy 不可表达 ⇒ 故意不收录"` 钉进 `whyNull`；⚠ 副作用：A1 实测证明该理由是**假的**（det(N)=1、与 zimg 差 4.87e-6），照 A1 方向"补对值"反被这条判红 ⇒ 锁的方向性有问题，但"不许静默填一组看着像的"这一判据本身有牙 |
| A1-N6 | `Invert3x3` 奇异返回零矩阵，公开文档承诺"返回 null"是假的；`GainMapEncoder:138` 只判 `!= null` 就乘像素（真缺陷-静默失效，潜伏） | **零覆盖** | `grep -rn "Invert3x3" tests/` = **0**；无退化原色夹具 |
| A1-N7 | `ResolvePrimariesToken` 按 `Spec.OutPrimaries` 遍历取首个匹配 ⇒ `"bt2020"` 命中 DCI-P3 Spec（真缺陷-静默失效，零消费点） | **零覆盖** | `grep -rn "ResolvePrimariesToken" tests/` = **0**；A1 自证生产 0 消费点 ⇒ 连"接线"这一说都无从断言 |
| A1-N8 | 别名表把裸 `"601"/"bt601"` 猜成 `smpte170m`（EBU 625 是另一套原色），CLI 可直达（真缺陷-标准） | **零覆盖** | `tests/` 内 `smpte170m` 只作为矩阵 token 出现在 colormath A 档（1e-5），**没有任何一格把 `"601"` 喂进去** ⇒ 别名改错读不出来；`verify-cli-strict`/`_lib-axes` 只查 token 白名单形状，不查别名解析结果 |

### A2 传递函数 / OOTF（5）

| # | 缺陷主张 | 状态 | 依据 |
|---|---|---|---|
| A2-2 | 传统链仍有 5 格把 `t=bt709` 写进命令行又写 `-color_trc bt709`，像素是 γ2.4、标签是定义式 ⇒ 光域最大差 **4748 LSB@16 = 7.25% FS**（真缺陷-标准） | **无牙** | 唯一能读到这件事的门禁是 `verify-color-engine-xcheck.ps1`（外部参照逐像素），而它把这 4 格**逐格写进 `$knownNonDefects` 豁免**，豁免理由原文 = "zimg 参照口径分歧：zimg 的 bt709 是纯 γ2.4（与定义式分歧）"（U20）⇒ 断言存在但被永久免检；`verify-color-strategy` ③e parity 只比 `VerdictKind`，不比像素 |
| A2-9 | zimg HDR 线性域 1.0↔100 nits vs 引擎 203 ⇒ 同"SDR→HDR"意图两管线差 **2.03×**，容器标签无法表达（真缺陷-精度） | **无牙** | xcheck `$cases` 的 HDR 方向只有 `PQ(BT.2020)→sRGB`，**没有 `sRGB→PQ` 用例**（U20）⇒ 恰恰是该分歧的可观测方向没有格子；`verify-decision-delivery` ⑰/⑰b 钉的是标签与位深兑现，不比亮度 |
| A2-10 | `ForCicpTransfer("smpte170m") => Gamma22` 对 BT.601/709 分段 EOTF 不成立（最大 2752 LSB@16 = 4.20% FS），zimg 又做成 γ2.4，且不在 `ZimgDivergentTransfer` 黑名单里（真缺陷-标准） | **零覆盖** | U19：`smpte170m` 在 `tests/color/Modes/curve.cs`、`tests/core/Modes/contract.cs` **0 命中**（正例对照同查法在 colormath 命中）⇒ 曲线侧没有这一格 |
| A2-11 | 趾段↔幂段衔接使 BT.709 **往返不闭合 maxRT = 16.26 LSB@16**，而注释写"实测差 ≤10 LSB"（真缺陷-精度 + 注释读数不可复现） | **无牙** | 16.26 落在该文件自己声明的 **24 LSB 放宽阈**内 ⇒ 门禁不红；没有任何断言复核"≤10"这个数字，也没有往返闭合的独立判据 |
| A2-14 | `--color-hdr-peak` 承诺能设 HLG 的 L_W，实际接不到（`nitsOfLinearOne:` 具名实参全仓 0 调用点 ⇒ α 恒 1000、γ 恒 1.2）（真缺陷-静默失效） | **零覆盖** | U19：`ColorHdrPeakNits` 的测试面全在 PQ/hable 档（`peak.cs:58/77`、`verify-color-peak.ps1:84/138/139`）；`verify-hlg-route.ps1` 的 OOTF 判据很强（`:199-201` 与标准定义对拍、`:207+` 还自带"满量程盲区"的反省段），但**从不带 `--color-hdr-peak` 跑 HLG** ⇒ 选项→L_W 那根线没人量 |

### A3 逐像素内核（4）

| # | 缺陷主张 | 状态 | 依据 |
|---|---|---|---|
| A3-N1 | 16-bit 主出口 `fast` 判据（`ColorKernels.cs:503-504`）只查 `scale/toneOn/srcHlg/gamut`，漏 `encScaleOn` 与 `dstHlg`；8-bit 兄弟 `:696` 查 5 条 ⇒ SDR→PQ 且 ≥256,000 px 时参考白编成 PQ 码 65535（应 38055，**nits 差 49×**）（真缺陷-静默失效） | **无牙**（两道独立封锁） | 本轮源码复核：`:503-504` 确无 `encScaleOn/dstHlg`，`:568-580` 那两段只在解析支，`:696-698` 兄弟出口 5 条件齐全。① 唯一的同形对照 `selftest` 0)（`:51-53` 表 vs 解析，`Check` 在 `:60+`）——5 格目标曲线全 SDR ⇒ `EncodeScale ≡ 1.0`，**被漏的那一位恒等于常量**（U13）；② 端到端侧 `verify-usability-e2e` 的 `B-color` 段**确有** `SDR → BT.2020 PQ → png` 格（`:214`），但夹具 512×384 = 196,608 px < 256,000 ⇒ 引擎根本不建表（U12），且该格判据只有 ffprobe 标注兑现 + `PSNR≥40dB` vs 源，**对 49× 亮度错既不敏感也不是它要防的东西**（PQ 编码后与 sRGB 源本就不同域） |
| A3-N2 | `TransformRgba8` 全文不读 `Tone/LinearScale/EncodeScale/SrcIsHlgScene/DstIsHlgScene` 五个算子，只对 `clamp=false` 抛；产品调用数 0（真缺陷-静默失效，潜伏） | **无牙** | `contract.cs:821` C13 确实用 `TransformRgba8`，判据是"精确可表示码值 no-op ⇒ `changed == 0`" ⇒ 输入被刻意选在**中性、恒等 spec** 上，五个未接算子恒取默认值（`ColorKernels.cs:26/36` 默认 1.0）⇒ 构造恒等，自由度 <3 |
| A3-N3 | `SimdPixelOps.LogGainToByteScalar` 用截断而非取整 ⇒ 增益图系统性偏暗 −0.5 LSB 均值（真缺陷-精度） | **无牙** | U23：唯一把它放进断言的两处，都用**同一个 `LogGainToByteScalar` 生成参照**（`GainMapTestHost:611`、`GainMapDecodeGroupD:350` 的 `gainRef[...] = SimdPixelOps.LogGainToByteScalar(...)`）⇒ 自己跟自己比；端到端只有 30 dB 往返阈（U22），−0.5 LSB 远在阈内 |
| A3-N5 | `Exp2Approx` 在 `x = n + 0.5` 相位 gather `Exp2Lut[1025]`（表长 1025）⇒ 4 字节越界读（边界项，潜伏） | **零覆盖** | `grep -rn "Exp2Approx" tests/` = **0**；无相位扫描夹具 |

### A4 等价判定与容差（6）

| # | 缺陷主张 | 状态 | 依据 |
|---|---|---|---|
| A4-C1 | `EquivalenceTolerances` 四常量（`MatrixToIdentity/MatrixElements/CurveParams/Chromaticity`）消费点 = 0，类注释仍写"禁止散落魔数"（真缺陷-标准） | **零覆盖** | A4 §1 已用正例自证（`ChromEqual` 1 处=定义、`MatrixIsIdentity` 2 处=定义+调用）；本轮复核：`tests/scripts/` 里没有任何一把尺要求"等价判定必须引用同一组常量" ⇒ 死常量复活/再次漂移都不红 |
| A4-C2 | `KnownSpaceTol = 2e-3` 吸附把下游 1e-5 等价判定变成**由构造保证为真**，且无任何偏移夹具（真缺陷-标准） | **零覆盖** | A4 §1：`grep KnownSpaceTol tests/` = **0**（同模式对 `PrimariesToken ==` 命中 `tests/core/Modes/plan.cs:148`）⇒ "没有偏移夹具"不是搜索假阴性 |
| A4-C5 | 不变量检测器 `FindPixelColorEntry` 生产零调用点（真缺陷-静默失效：守卫存在但没接线） | **零覆盖**（针对接线；针对检测器本体是**有牙**） | U14：本体由 `contract.cs:624/625/632/634/636` 钉住，且带**误杀负控**（`C8 无误杀：无色彩滤镜的命令行`）⇒ 本体有牙。但"生产链路必须调用它"没有任何断言：`tests/scripts/` 0 命中 ⇒ 该锁可以永远只在探针里自转 |
| A4-C6 | `S1` 走 carry 时日志宣称"源 ICC 由后置步骤嵌入"（`QueueProcessor.cs:1394/1931`），而后置门条件是 `EffectiveColorStrategy == CarryIcc` ⇒ 声明 S1 时恒不成立，日志与代码互相矛盾（真缺陷-静默失效） | **零覆盖** | `grep -rn "后置步骤嵌入\|由后置" tests/` = **0**（正例：同查法在 src 命中 `:1394`、`:1931`）⇒ 没有任何断言要求"这句话为真" |
| A4-C9 | `Kind ⇔ Degradations.Count > 0` 是构造恒等（`ProjectVerdict` 用同一个 `degs` 现算 Kind）⇒ 抓不到漏登记（门禁侧·构造恒等） | **无牙** | 该不变量确实被 `ServiceProbe verdict` 断言（实测 `pass=41 fail=0`，读数 `tests/output/_baseline_modes/core__verdict.txt`），但 A4 已定案它是恒真式；真正有牙的是 per-Code 正/负控对 ⇒ 账应记在"恒真式不该算覆盖" |
| A4-C10 | `FfmpegCommandBuilder.Decision.cs:572` `catch { eff = declared; }` 与 `:625` `catch { return null; }` 可让**裁决本身静默消失**（契约 Reject 不生效）（真缺陷-静默失效） | **零覆盖** | 本轮实读两处在位（`:572` 退回声明值、`:625` 返回 null，均无日志）。`verify-color-strategy` 的 `ExpectRejectFromVerdict` 是**读日志里的裁决行**决定期望，裁决行缺失时该格退化为"无输出"；仅有的兜底是 `裁决parity-覆盖格数 ≥12`（U25 行引），它拦的是"两边都读不到"，**不拦"某一边因吞异常而读不到"** ⇒ 无断言观测该 catch |

### A5 ICC/CICP 标注（4）

| # | 缺陷主张 | 状态 | 依据 |
|---|---|---|---|
| A5-6 | `IsValidIccProfile` 只查 `acsp` 魔数 + 长度 ≥128，负样本（128B 全零+acsp / 截断 300B / 头 size 被改坏）**全部判合法**；它同时是缓存命中门与用户 PLAN/iccs 接受门（真缺陷-静默失效） | **无牙** | U15：4 处调用全是正例极性（"ICC 魔数有效"），0 处负样本 ⇒ 断言只能证明"合法的被接"，证不了"坏的被拒"。`verify-gainmap-isobmff` / `verify-tiff-icc` 读的是产物标签，不经过这个门 |
| A5-7 | `S3`（仅 CICP）的 `StripIcc` 只清计划意图，交付链没有"删掉已在文件里的 ICC"的动作 ⇒ PNG/JPEG/WebP 可交付 cICP + 源 iCCP 两套矛盾描述（真缺陷-标签自相矛盾） | **无牙** | U25：`-not $r.Icc` 的两格有、且带**取数尺正对照**（不是空转），但 ①png 格的源不带 iCCP ⇒ "剥离"这个动作从不被要求；②只有 avif/png 两格，**webp 无格**；③jpg 那格被设计成 Reject（`verdict-替代方案-jpg`），也测不到"残留"。⇒ 有断言、构造不覆盖缺陷路径 |
| A5-10 | `IccProfileBuilder.BuildText` 写的 `textType` 不带 NUL 终止符（`cprt` 标签），而 lcms 写侧恒带、同文件 `BuildMluc:279` 反倒知道要 NUL（真缺陷-标准，低危） | **零覆盖** | `grep -rn "BuildText" tests/` = **0**；`verify-png-signature`/`verify-tiff-icc` 不解析 `cprt` 的 textType 尾部 |
| A5-11 | `DetectIccPrimaries` 不校验 tag 类型，而同仓兄弟 `ReadIccColorants:197` 校验（`!= "XYZ "` ⇒ null）⇒ 两读路不对称，rXYZ 为别的类型时被当 XYZ 喂给 cICP 判定（真缺陷-标准） | **零覆盖** | U18 行：`DetectIccPrimaries` 在 `tests/scripts/` 的唯一命中是 `ensure-fixtures.ps1:104` 的**注释**（"真正的判据是…`DetectIccPrimaries`"），0 个用例；无"非 XYZ 类型但 ≥20B"的负样本 |

### B1 GainMap 数学（9）

| # | 缺陷主张 | 状态 | 依据 |
|---|---|---|---|
| B1-F1 | 元数据声明 `offsetSDR = offsetHDR = 1/64`（`GainMapEncoder.cs:887/924/925`），前向增益不用它 ⇒ 编码正向不是解码逆运算（解码式在 `GainMapDecoder.cs:1194/1208` 用了）；1000nits +15.6 nits、4000nits +82 nits | **无牙** | 唯一能观测它的面是 `GainMapTestHost` 的 5 格编解码往返（U22），阈值统一 **30 dB**，实测 hdr4000 = **33.6 dB** ⇒ 现状（含该误差）就被校准在阈上 3.6 dB；没有任何一格把 offset 单独置 0 做正/负控。`verify-gainmap-thirdparty.ps1` 用 libultrahdr 独立读文件（这是好判据），但只比 `maxContentBoost == 2^log2 (±2%)` 与 `gamma == 1.0`，**不比 offset 与恢复值的一致性** |
| B1-F2 | 增益字节与降采样字节都是 `(byte)` 截断而非 round-to-nearest ⇒ 恒负偏（1000nits 恒暗 0.31–0.63%） | **无牙** | 同 U23（自比参照）+ 同 U22（30 dB 阈吸收） |
| B1-F3 | 底图通道 >1 被 `FloatToSrgb8` 截到 1，而增益按截前值算 ⇒ BT.2020 源 + 默认 sRGB 底时 29.4% 颜色越界、饱和 2020 红峰值 R 恢复暗 39% | **无牙** | U22：往返夹具 `CreateHdrTestPixels` 源码里**逐字写着"避免极端饱和色"**，色块全是近中性低饱和 ⇒ 越界分支从不进入；`oog` 组（越界网格）测的是 zimg vs H1 的裁切幅度（`GainMapGroup.cs:323 Check(worst <= 128)`），不是增益 vs 钳位次序 |
| B1-F4 | `multiChannel = true` 时增益图仍硬编码 `--chroma_subsampling 420`，参照 RGB 增益图走 4:4:4 ⇒ 逐通道增益被色度降采样摧毁 | **零覆盖** | U24 + `grep -rn "chroma_subsampling\|4:4:4" tests/scripts/` 的命中全属于 webp/avif/jxl 的**目标色度**轴（`_lib-axes.ps1:233`、`_lib-reject.ps1` 的 webp 4:4:4 用例），**没有一条**读增益图 JPEG 自身的采样 |
| B1-F5 | AVIF 出口恒按 `gray` 编增益图（`:582`），却在 tmap 元数据按 multiChannel 置 flags bit7（`:849`）⇒ 宣称 3 通道、交付 1 通道 | **零覆盖** | U24：avifgm 组唯一编码用例 `multiChannel: false` ⇒ 分叉格从未构造；`GainMapGroup.cs:230 Check(pl.Length == 1 + 21 + meta.Channels * 40)` 用的 `meta.Channels` 来自**同一个 box 的解析**⇒ 即使构造也不会红（自比） |
| B1-F6 | XMP 的 RDF 命名空间 URI 拼错（`1999-02-22-rdf-syntax-ns#` 应为 `1999/02/22-rdf-syntax-ns#`），且 hdrgm 写在主图 XMP（参照写副图） | **零覆盖** | U24：`1999-02-22` 只在 `GainMapEncoder.cs:899`，tests 侧 0 命中。⚠ 更坏的一面：`verify-gainmap-engine.ps1:249/250` 能用 exiftool 读到 `XMP-hdrgm:Channels = 3/1` 并给出绿 ⇒ **exiftool 容忍了这个错 URI，于是"读得到"被误当成"写对了"**；对外互操作面（严格 RDF 读者）无人验 |
| B1-F8 | 解码侧只取通道 0 的 `gainMapMin/Max/Gamma/offset`（`:768/:792`），第三方多通道件的逐通道区间被按通道 0 统一施加 ⇒ 色偏 | **无牙** | `hdr1000rgb` 格确实走多通道往返（`Program.cs:47` + `:56`），但判据仍是 30 dB PSNR（U22）⇒ 通道 0 统一施加的色差被算进 MSE 而没被单独点名；`verify-gainmap-engine` 只验**写侧**声明的 `Channels`，不验**读侧**逐区间 |
| B1-F9 | 峰值自动档在 GUI 不可达：`ParseGainMapNits` 夹 [200,10000]、模型默认 1000 ⇒ `>0 用户优先` 恒成立；400nits 素材只用 42.5% 量程（能力缺口 + 静默质量损失） | **零覆盖** | 没有任何断言构造 `JpegGainMapTargetNits = 0`（auto）这一档并检查它是否生效；`_probe-gui-control-wiring.ps1` 只拦"XAML 声明却没接线"的下拉，而这一条**接了线、只是取值域把 auto 挤掉了** |
| B1-F10 | 缺参照那道暗像素增益上限守卫（`sdr < 2/255 ⇒ gain ≤ 2.3 log2`，`gainmapmath.cpp:775-780`），与 F1 叠加成黑位借光 halo | **零覆盖** | `tests/` 无"近黑像素 + 高 boost"用例；往返夹具背景亮度区间是 `0.02..0.2 × 峰值`（U22 源码），换算后远高于 `2/255` 的触发线 ⇒ 该形从未被构造 |

### B3 GainMap 端到端接线（6）

| # | 缺陷主张 | 状态 | 依据 |
|---|---|---|---|
| B3-N1 | `avif + GainMap`：HDR 模式对 avif 可见但 GainMap 子面板只在 jpg/jpeg 显示 ⇒ 五个子选项藏在隐形控件里被采集；且 avif 出口硬编码 `-crf 18 -b:v 0`，用户在 AVIF 面板设的质量/cpu-used/tune/位深**无声丢弃** | **零覆盖** | `verify-gainmap-engine.ps1` 与 `verify-gainmap-isobmff.ps1` 都不构造"avif 目标 + GainMap 开关"格；`verify-ui-*` 的 `ui-panel-visibility` 测面板显隐、`ui-gainmap-toggle` 测开关，但两腿组合到 avif 的这一格没有。B3 自己的 N7 也把它登记成缺口 |
| B3-N2 | Ultra HDR 输入 × GainMap 输出：`DecodedUltraHdrPeakNits` 被写进 options 且被色彩装配层读取，但 `GainMapJpegCodec.ResolveEncodeParams` 不读它，只重新探测（此时输入已换成线性 PFM、无 PQ 标签）⇒ `IsHdrInput=false ⇒ headroom=1 ⇒` 走"SDR ⇒ 普通 JPEG"短路，增益图不写 | **零覆盖** | B3 自标"端到端未测"；`verify-gainmap-engine` 的 `isHdrInput` 换源格是 **PNG cICP+ICC** 形态、不是 UltraHDR **输入**形态；无任何脚本喂 `*ultra*hdr*.jpg` 作输入并检查产物含 MPImage2 |
| B3-N3 | 复选框"增益图质量跟随主图"实为恒 75：两出口都写 `>=0 ? 值 : 75`，底图用 `Options.Quality` ⇒ 主图 90 时增益图仍 75，与 zh/en 字典文案正面矛盾 | **无牙** | 现有断言只锁**哨兵被消费**：`tests/ui-cli/uicliGroup.cs:113-116` 的 M1(D4) 组注释逐字写 `JpegGainMapQuality 里=「用默认 75」` ⇒ 测试把"恒 75"当成**期望值**钉住了。没有任何断言比较"底图质量 vs 增益图质量"（读增益 JPEG 的量化表/尺寸） |
| B3-N4 | GainMap 峰值来源标签说谎：`JpegGainMapTargetNits` 模型默认 1000 ⇒ 永远 `>0` ⇒ `PeakSourceText` 恒打"用户指定"，GUI 用户从没指定过；同一行还会打 `源峰值 203 nits（来源：用户指定）` | **零覆盖** | `grep -rn "PeakSourceText\|来源：用户指定" tests/` = **0**（正例：同查法命中 `verify-color-peak.ps1:84` 的 `intentPeak=1500.00` ⇒ 管道能读到 peak 类日志，只是没有 GainMap 侧这一行） |
| B3-N6 | 预设回放：模型腿 `if (p.JpegGainMap)` 关不掉开关（UI 腿能关）；`Downsample`/`MultiChannel` 无条件覆写，违反本仓"旧预设不得覆盖"规则 | **零覆盖** | `uicore` 的 N 组预设两腿往返只测 `ColorStrategy`（B3-N7 自陈）；`_probe-settings-clone-coverage.ps1` 锁的是 `Save()` 手写克隆表（清单第 54 条），管不到回放语义 |
| B3-N7 | 门禁格子缺口本身：`verify-dryrun-parity` 四臂无 gainmap；`ProbeDecision` 网格从不置 `JpegGainMap`；`uicore` N 组预设两腿只测 `ColorStrategy` | **零覆盖**（=主张自身） | 本轮复核：`grep -n "gainmap" tests/scripts/verify-dryrun-parity.ps1` = 0（正例：同查法对 `dng`/`jxr` 臂有命中，TESTING.md `:447` 记该脚本 16/0 含 dng 臂）⇒ B3 的三条都成立 |

### C2 并发 / 进程 / IO（5）

| # | 缺陷主张 | 状态 | 依据 |
|---|---|---|---|
| C2-1 | 引擎线的托管行带并行度恒 = 全机核数、与 `-j` 脱钩（`QueueProcessor.cs:1413-1415` 硬编码 `1` ⇒ `Dop = ComputeAdaptiveThreads(1) = 20`） | **零覆盖**（针对接线） | `ComputeAdaptiveThreads` 本体有 5 条断言（`GainMapTestHost:964-978` D17a–e）；`selftest.cs:227-229` 测 `ColorScheduler.ForItem(2, …)` ——**并行度参数恒写死 2**，没有任何断言钉"调用点把用户的 `-j` 传下来"⇒ 公式有牙、接线零覆盖 |
| C2-2 | 引擎（默认执行路径）的两个 ffmpeg 子进程**零挂死守卫**：`RunToEndAsync` 无 `CancelAfter`/`WhenAny`/`-progress`，6 个站点全走它 | **零覆盖** | U17（源码 + 门禁判据面双向 0）。同族四条锁实测都不表态：`_probe-stderr-drain-scan`（实测 60/60 PASS）只问 stderr 有无人排；`_probe-sync-read-before-wait-scan` 实测 `A 类 62 处 / B 类 7 处 / 命中 0`，判据不含"有没有超时"；`_probe-ct-chain-closure-scan` 自报跳过面"调用点不持有 ct 17 处 / 括号不配平 124 处"；`_probe-cancel-propagation-scan` 只数 `FfmpegRunner.RunAsync` 的 27 个调用点 ⇒ **`RunToEndAsync` 恰好落在四条锁的判据面之外** |
| C2-3 | `RunToEndAsync` 先排 stderr 到 EOF 再排 stdout ⇒ 子进程写 stdout 即双向阻塞；现有两条探针**结构上**测不到这一形 | **零覆盖**（探针覆盖缺口） | 同 U17 后半：`_probe-sync-read-before-wait-scan` 把 `ReadToEndAsync` 排除在判据外，`_probe-stderr-drain-scan` 只判 stderr ⇒ 没有一条锁问"stdout 排空次序"。注：当前无 stdout 写方 ⇒ 该形**未复现**，记的是缺口 |
| C2-5 | 「开始」按钮无 `IsQueueRunning` 幂等守卫，且 `Start()` 在 UI 线程同步 `prev.Wait(3 s)` ⇒ 在跑项被静默取消 + UI 冻结 | **零覆盖** | U18：`IsQueueRunning` 在 `tests/` **0 命中**（正例 `Kill(true` = 20 命中）。`ui-*` 五组 409 条断言里有点控件、有读 `CommandText`，但没有"连点两次开始"这一形 |
| C2-6 | 内存钳制口径用**物理内存总量**（`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`）而非可用量 ⇒ 本机算出上限 168/42，`-j ≤ 42` 时钳制恒不生效；CPU 轴仍缺 | **零覆盖** | U16（0 命中）。同族可复用门 `verify-gainmap-memory.ps1` 实测存在、且设计不错（①声明预留 ≥ 实测峰值 ②GainMap 峰值 > plainMB ③64MB 过小负控），但它钉的是**每任务预留数值**，与**钳制口径**是两件事 ⇒ 门在、判据打不到 |

### C3 性能门禁（2，余为性能项见 §4）

| # | 主张 | 状态 | 依据 |
|---|---|---|---|
| C3-5 | 两兄弟内核快路径判据不对称，而 8-bit 主力出口 `TransformRgb48leToRgb8` 在 bench 里**没有格子** | **零覆盖** | `grep -n "TransformRgb48leToRgb8" tests/diagnostics/Modes/bench.cs` = **0**（同查法对 `TransformRgb48le` 命中 `:120`、`:134` ⇒ 非搜索假阴性） |
| C3-11 | 性能门禁"有门，但门的覆盖面恰好绕开产品在跑的那条路" | **无牙** | bench 的 3 条 `Check`（`single ≥ 40`、`扩展比 ≥ 4×`、`H1 ≤ H2×2.0`）都在**表驱动格**上；解析式只 `Console.WriteLine`，`singleSlow/bestSlow` 算了从不进 `Check` ⇒ 产品在跑的 HDR/GMO 路无门槛；且这 3 条 Check 的宿主 `bench` 本身零调用点（U10）⇒ 连"绕开"这件事都不再被任何批次观察到 |

---

## 3 清单一致性差异

### 3-A 三个数

| 口径 | 数量 | 命令 |
|---|---|---|
| `docs/TESTING.md §6` 编号条数 | **122** | U7（`§6` = `## 六、注意事项`，`:573`–`:5499`） |
| `docs/TESTING.md` 声明的门禁清单条数 | **73** | U8（`:447`），另 `:501-502` 记 `$expectedScriptCount` 72→73 与 `shape-selfcheck=OK` |
| 运行器 `$scriptList` 实测 | **73** | U4；`$expectedScriptCount = 73`（U5）；日志 `script-list=73 条（受管基线 73）`（U26） |
| `tests/scripts/verify-*.ps1` 在盘 | **58** | U1 |
| `tests/scripts/*.ps1` 在盘（全部） | **115** | U2；git 跟踪 115、未跟踪 0（U3） |

**判：条数账本身一致**（73 = 73 = 73），73 条**逐名在盘**（U6：清单有而文件不存在 = **0 条**），`_archived_diag/` 里 3 个脚本（`final-verify.ps1` / `regtest-fix.ps1` / `verify-remaining.ps1`）**不在** 115 的计数里，也没被算进清单 ⇒ 没有把产物目录当脚本的污染。

### 3-B 不在清单里的 42 个（列名，U6）

**真正的缺口候选（`verify-*` 命名，3 条）** —— 标签：**覆盖缺口 / 有意不入列，需分别定案**

| 脚本 | 现状 |
|---|---|
| `verify-oracle-matrix.ps1` | **有意**不入列（2.5 h 长跑 > `ScriptTimeoutSec 900`），TESTING.md `:2908-2909` 已登记为手工长跑件；判：文档在位、非遗漏 |
| `verify-release-package.ps1` | 清单内有近邻 `verify-package-smoke.ps1` / `verify-jbrd-e2e.ps1`（L3 出货包），本条是否被它们覆盖 **未判定**（本 lane 未逐条读它的断言集） |
| `verify-color-matrix-full.ps1` | 名称与 `verify-color-matrix`-族（`_probe-matrix-structure` / `_lib-matrix` / `verify-color-strategy`）重叠，未见 TESTING.md 归属说明 ⇒ 标签：**清单遗漏 或 已废弃未清理**（二选一未定案） |

**`_probe-*` 诊断件 20 条**（不在清单，全为一次性/定位用）：`_probe-caps-e1-e2`、`_probe-gpu-stage1/2/3`、`_probe-hdr-peak-oracle`、`_probe-icc-affects-decode`、`_probe-jxl-webp-pixels`、`_probe-p7d-bisect`、`_probe-png-cicp-production`、`_probe-sws-downshift`、`_probe-sws-pos`、`_probe-sws-yuv`、`_probe-zimg-noop-check`、`_probe-zimg-tagged`、`_probe-zimg-transfer-table`、`_probe-ztf-locate`。TESTING.md `:184-185` 已把 `_probe-hdr-peak-oracle` / `_probe-p7d-bisect` / `_probe-zimg-noop-check` 显式登记为"死诊断 / 固定目录不派工"，`:149` 把 `_probe-gpu-stage1` / `_probe-caps-e1-e2` / `_probe-sws-downshift` / `_probe-ztf-locate` / `_probe-zimg-tagged` / `_probe-zimg-transfer-table` 登记为"不接入清单" ⇒ 这 20 条里**账是齐的**，标签：**未处置但已登记**。

**共享库 5 条**：`_lib-axes`、`_lib-color-assets`、`_lib-matrix`、`_lib-oracle`、`_lib-reject`。`:284` 逐字声明 `_lib-color-assets` "**不登记进** `_run-step3-gates.ps1` 的脚本清单"⇒ 合规。

**harness / 生成器 14 条**：`_accept-ui-split`、`_gen-mode-split(-reduce)`、`_gen-ui-split`、`_run-step3-gates`、`archive-diagnostics`、`cleanup-structure`、`ensure-fixtures`、`generate-sources`、`insert_cicp`、`probe-gainmap-tiff`、`run-ps-verify`、`run-color-regress`、`run-color-tests`、`run-full-matrix-test(-safe)`、`run-quality-tests`、`run-thread-tests` ⇒ 非门禁，正常。

### 3-C 清单/文档侧的三处过期（标签：**文档过期**）

1. **`verify-remaining.ps1` 的路径漂了但文档没改。** TESTING.md `:273` 把 `verify-webp-hdr-fix.ps1 / verify-tiff-icc.ps1 / verify-remaining.ps1` 并列为"人工读数脚本"，而同行的 `$tableOnly` 注解也按它在 `tests/scripts` 叙述；实际文件在 **`tests/scripts/_archived_diag/verify-remaining.ps1`**（`ls` 实测），`tests/scripts/` 顶层**没有** ⇒ 按文档路径去找会得"脚本已不存在"的假结论。
2. **`_probe-test-group-coverage.ps1` 的 ③(b) 取证口径与它的名字不符。** 它名叫"覆盖一致性锁"，实测（U9）把 40 个 mode 一律标为"由专项门禁脚本调用，**正常**"，但③(b) 的实现是 `[regex]::Escape($mode)` 在**全部 `tests/scripts/*.ps1` 文本**（含注释、含不在清单的脚本）里的**子串匹配**（源码 `:91-96`、`:107-110`），③(c) 只要求"本组 `Program.cs` 里该 mode 名是个字符串字面量"。⇒ 一个从不被任何门禁执行的 mode 可以**靠该锁自己的注释**通过取证：实测 `bitdepth` 在 `tests/scripts` 的两处命中**都不是调用位**：`_probe-test-group-coverage.ps1:20` 的**注释**（"直接调用的（如 `bitdepth`/`faithful`/…）"）与 `verify-png-structure.ps1:468` 的**夹具文件名** `neg_bitdepth.png`；再核 `grep -rn "'bitdepth'\|\"bitdepth\"" --include=*.ps1 --include=*.cs .`（排除 output/bin/obj）= 只有 `tests/encode/Program.cs:58`、`tests/ServiceProbe/Program.cs:117`、`tests/Tests.Shared/TestGroups.cs:33` 三处**分派/声明**，0 处调用 ⇒ `bitdepth` 零执行却取证通过（U28 + `grep -rn "bitdepth" tests/scripts/`）。标签：**真缺陷-静默失效（守卫自身即假绿源）**，与项目记忆「守卫自己就是最大假绿源」同族，且这一条是**新变体**：不是"枚举为空"，而是"证据源把守卫自己算进去"。
3. **`tests/gainmap-{encode,decode,struct}` 三个拆分工程无执行者。** `TestGroups.cs:44-46` 声明它们，`tests/gainmap-encode/Program.cs:43` 有分派分支（满足③(c)），但 U11 证明 73 条清单里没有任何脚本跑这三个 exe；`verify-gainmap-host.ps1:28` 跑的是**旧的单体 `GainMapTestHost.exe`**。⇒ 拆分是"改哪块跑哪块"的手工工程（TESTING.md §1.1 口径），但清单/锁把它的 mode 算成"由专项门禁调用"，读者会误以为已接。标签：**覆盖缺口 + 文档过期**。
   ⚠ 反面对照（证明"拆分 ≠ 没人跑"这条判据是有效的、不是逢拆分都判红）：`tests/ui-{cli,color,core,mode,sweep}` 五个拆分工程**有**执行者 —— `verify-ui-split-union.ps1:139-156` 逐个 `Tests.Ui*.exe` 调用，且带反控（比较行数 >2800、旧宿主须解析出 17 个组）。

---

## 4 性能门禁现状

**基线**：`tests/output/_baseline_modes/bench_rep1.txt` 末行 `PROBE RESULT: pass=9 fail=2`；`_summary.csv` 行 `"diagnostics","bench","1","9","2","0","40.19",…` ⇒ 退出码 **1**、40.19 s。三条复跑 rep1/2/3 全在位。

| 问 | 答 | 证据 |
|---|---|---|
| **归谁守？** | **没有人守。** `bench` 既不在运行器默认 `$Probes`（26 条，U9 打印实测 26），也不被任何清单脚本当参数调用（U10 逐文件 0 命中；正例对照 U27 `xcheck` 命中 ⇒ 查法有效）。`_baseline_modes/` 是 2026-10-07 一次性基线批次（`docs/TEST_REPORT_BASELINE_2026-10-07.md:70/349`）产物，批次脚本不在 73 条里。⇒ 标签：**零覆盖（该基线读数无看守者）** | U9/U10/U27 |
| **门槛是绝对值还是相对？** | 混合，共 3 条 `Check`：`bench.cs:151 Check(single >= 40, …单核 ≥40MPix/s)` = **绝对值**；`:154 Check(best/single >= 4.0, …多核扩展比 ≥4×)`（仅 ≥8MP 格）= **绝对值门槛、相对量**；`:74 Check(h1 <= h2 * 2.0, …端到端 H1 ≤ H2×2.0)` = **纯相对**。绝对值那两条不随机器换代调整，机器换强则永不红、换弱则成片假红 | `tests/diagnostics/Modes/bench.cs:74/151/154`（C3-11 逐字引用，本轮复核在位） |
| **绑 DLL / 构建指纹吗？** | **没有。** bench 只打印 `tools-root`（rep1 首行），不打印被测 `FfmpegGui.dll` sha256、不打印构建配置（Release/Debug）、不做 STALE 新鲜度比对。对比：本仓其它门禁**已经有**这套纪律 —— `verify-gainmap-memory.ps1:16-17` 打印 `[gate] exe=… (Release/Debug fallback)` 并点名"回退是静默的 ⇒ 门禁可能测的不是你以为的那个二进制"；`verify-gainmap-thirdparty.ps1:30` 打印 `SUBJ(被测) = v$pkgVer … ProductVersion=$pkgPV`；运行器另有 4 对 STALE 闸（TESTING.md `:2909`）。⇒ 性能门是这条纪律的**漏网之鱼**：`_summary.csv` 里 bench 的 `pass=9 fail=2` 无法归因到任何 DLL，而 `bench_rep1..3.txt` 的 6.5/7.0/7.7/7.8/8.2/8.7 MPix/s（`docs/TEST_REPORT_BASELINE_2026-10-07.md:333`）**没有任何一条被登记过被测二进制指纹**。标签：**门禁缺口** | rep1 首行 + 上述行引 |
| **`PQ→sRGB 单核 ≥40 MPix/s` 为什么长期无人处置？** | **两个原因叠加，且都不是"已登记为已知红"**：① **没进清单** —— 本 lane 逐查 `docs/TESTING.md`、`docs/HANDOVER.md`、`_baseline_modes/_summary.csv` 与 `docs/PIPELINE_PERF_ANALYSIS_2026-10-09.md`，**没有任何一处把 `pass=9 fail=2` 或这条 FAIL 登记成"故意留红/已知红"**（对照：`verify-color-strategy` 的 F3 留红是被**逐字登记**的，TESTING.md `:266`"其中 F3 故意留红，钉 #35 那笔…的账"）；② **成因与 A3-N1 同族但当时只被当成性能** —— rep1 逐字打 `参考（解析式单核）= 8.1 MPix/s 快路径增益 = **1.0×**`，即表在这格**完全没生效**，原因是 `fast` 要求 `Math.Abs(scale-1) < 1e-12`，而 PQ 源的 `LinearScale = 10000/203 ≈ 49.3` ⇒ 表被整体关掉（`ColorKernels.cs:503-504` + C3 静5）。⇒ 判读：这条红是**真缺陷的第二块读数板**（A3-N1 是它正确性那一面，这里是性能那一面），长期以"基线里的一个数字"存在，没进红账、没进清单、没人看守。标签：**零覆盖 + 文档过期（`ColorKernels.cs:124` 仍写"16-bit 源走解析式（避免 256KB 大表打爆缓存）"，把这条 FAIL 当成设计意图）** |
| **`H1 ≤ H2×2.0` 在机器状态变化下的有效性** | 这是三条里**唯一相对**的一条，rep1 实得 1.59×（H2=171 ms / H1=272 ms）。它同时依赖两个被测臂：`H2` 是**外部 zscale 单进程**，`H1` 是**引擎三进程 + 内存变换**。因此：① H2 随随包 ffmpeg 换代而变（A1 §1 已实测 zimg 快照从 2026-08-06 换到 2026-10-07，两版读数恰好逐位相同 ⇒ 侥幸，不是保证），② H1 里含**进程启动固定成本**，而 C1 的 HEAD 表证否了"固定成本可忽略"（默认参数下 jpg 引擎路 = ffmpeg 2 + ffprobe 3 + exiftool 7），③ 本仓 §2.2 的 `--dry-run` 下界 0.78–0.81 s 是 **2K 帧编码时间的同量级数**（272 ms）⇒ 一张 2K PNG 的 H1/H2 比值里，**探测链的差异和色彩变换的差异不可分离**。⇒ 判：**该相对门槛在小帧/高固定成本机器上会被编排开销单独推到 2.0 以上**（红），或在换强 CPU 上永远够不着（绿）；它不是色彩变换性能的量具。标签：**未判定（缺一次"同机 H1 拆成 编排 + 变换 两段"的读数）**，另附一条：**它同样不绑构建指纹** |

**另两处与性能门相关的登记（不计入 §2 的 N）**：
- **C3-1 / 静5（性能项，非正确性缺陷）**：`fast` 被 `LinearScale≠1` 恒关掉 ⇒ HDR/GMO/tonemap 路全走 6-pow 解析路；实测上限区间 = 表 55.9–127.1 vs 解析 6.0–9.9 MPix/s。与 A3-N1 是**同一个判据的两面**：A3 漏判 ⇒ 正确性风险，C3 关判 ⇒ 性能损失。修的时候**必须一起改**（把解码查表与编码解析拆开），否则要么修了性能留了正确性、要么反过来。
- **C3-2（性能项）**：`ColorKernels.cs:775-781` 的 `EncodeTo8` 先算 `curve.FromLinear()`（1 次 pow）再判断有没有表 ⇒ 表路每像素白付 3 次 pow。8-bit 主力出口，且（C3-5）bench 里没格子。

---

## 5 最小补锁清单（按能抓住的缺陷排序）

每条给：复用哪条同族门 / 判据打什么 / 要什么夹具。⚠ 全部要求**同时**给正向对照与负向对照，并遵守"缺夹具必须 fail-closed 点名、不许 SKIP 给绿"（本仓既有纪律：`verify-gainmap-thirdparty.ps1:59-60`、`verify-gainmap-isobmff` 缺样本即红、`_probe-test-group-coverage` 的 ④-a/b/c 三件反控）。

| 优先级 | 补什么 | 复用的门（同族） | 判据（打什么） | 需要的夹具 |
|---|---|---|---|---|
| **P0-a** | 钉住 **A3-N1**（16-bit 表快路径漏 `encScaleOn`/`dstHlg`） | `tests/diagnostics/Modes/selftest.cs` 0) 段（已有"表 vs 解析"对照，扩它的操作数空间即可）+ `tests/core/Modes/contract.cs` | ① 把 `selftest` 的 cases 加两格：`sRGB→BT.2020 PQ`、`sRGB→BT.2020 HLG`（**SDR 源 ⇒ `LinearScale` 恰为 1**，正是漏判据的触发面），断言 `表 vs 解析 逐码差 ≤ 1 LSB` ⇒ 现在的 `fast` 判据会当场红；② **跨建表阈值**的活体对照：同一素材 **506×506（255,236 px ≥ 256,000）vs 505×506（< 256,000）**，断言两者结果差 ≤2 LSB ⇒ 把 `RawColorPipeline.cs:1087` 那道阈值从"天然遮住缺陷"变成"被观察到"；③ 端到端一条 `--bit-depth 16 --color-space BT.2020 PQ`，读产物**白样本码值**，判据 = 参考白应落 **PQ 38055 ± 2**（不是 65535）——这条是绝对量具，不受 256k 阈值影响 | ①②纯合成帧，零素材成本；③一张 **≥256,000 px** 的 16-bit SDR PNG（**当前 `generate-sources.ps1` 全表最大只有 512×384 = 196,608 ⇒ 必须新增一格 `size=640x480`(307,200) 或更大，否则任何端到端补锁都是空转** —— 这是本轮最省事也最关键的一条前置） |
| **P0-b** | 钉住 **C2-2**（引擎子进程零挂死守卫） | `verify-ffmpeg-heartbeat.ps1`（它就是干这个的，只是判据面只覆盖 `FfmpegRunner.cs` + `QueueProcessor`）+ `_probe-sync-read-before-wait-scan.ps1` | 加一条**类级结构锁**（与 `:260` 的 `bool heartbeat = true` 那条同形）：`src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs` 里每个 `Process.Start(` 站点所在方法**必须**含 `CancelAfter` / `WaitAsync(` / `Task.WhenAny` / `-progress` 之一，命中数必须 = 站点数（当前必须是 0/7 ⇒ 红）；**外加一条负控**：往该文件注入一个已知带 `WaitForExit()` 的站点形态，锁必须转红（本仓"枚举为空必须拦"的既有纪律）。行为侧另加一条：`ffmpeg` 换成一个 `ping -t` 式哑进程 ⇒ 门禁必须观察到"被判死并有日志点名" | 一个可控挂死的假 ffmpeg（`_probe-cancel-propagation-scan` / `ExternalToolsDetector.cs:495` 已有"模拟工具缺失"的先例可抄）；不需要真素材 |
| **P0-c** | 钉住 **A4-C10**（两处吞异常可让裁决消失） | `verify-color-strategy.ps1` ③e 的 `ExpectRejectFromVerdict` + `ServiceProbe verdict`（实测 41/0） | 一条**结构锁** + 一条**行为锁**：① `FfmpegCommandBuilder.Decision.cs` 的 `catch` 块**不得为空体退回声明值 / 不得 `return null` 而不打日志** ⇒ 判据 = "带 `catch` 的行之后必须存在一条 `Log`/`Warn` 调用"（剥注释后比对，本仓已有先例：`verify-decision-delivery` ⑯ 三条结构锁"匹配前先剥整行注释"）；② 行为侧：构造一次必然抛的输入（如意图装配阶段坏 ICC），断言 `[色彩裁决]` 整块**必须出现在日志里**（哪怕是 `Reject`）——缺行 = 红，而不是像现在这样退化成"该格无输出" | 一个会让 `ColorIntentFactory.FromOptions` 抛的文件（128 B 全零 + `acsp` 头即可，与 P1-c 共用夹具） |
| **P1-a** | 钉住 **B1-F1/F2/F3**（增益 offset 不互逆 / 截断 / 钳前算增益） | `tests/GainMapTestHost` 的 B 段往返（已有 5 格 + 30 dB）与 `verify-gainmap-thirdparty.ps1`（已有 libultrahdr 独立读者，最硬的判据面） | **不要再动 30 dB**（阈值已被现状校准，U22 余量 3.6 dB）；改为加**逐算子正/负控**：① F1 —— 在 host 里加一格"offset 置 0 编一遍、置 1/64 编一遍"，断言两遍的**恢复值差 = (boost−1)/64**（解析式独立算出期望，不用产品函数当参照，破掉 U23 的自比）；② F2 —— 参照换成 `Math.Round` 版**本地复刻式**（本仓既有范式：B1 §3.5 / A3 §1 的"逐行转写复算"），断言 `|产品 − 产品| = 0` 不成立、`产品 vs 取整版 恒 ≤ −1 LSB` 且均值 < 0 即红；③ F3 —— 现有 `CreateHdrTestPixels` **刻意避饱和**（U22），新增一格 **BT.2020 饱和红峰值像素（R>1 映射到 sRGB 底）**，判据 = "钳位必须回灌增益"：断言恢复值误差 ≤ 1 个增益档，而不是靠 PSNR 摊平 | ①②零素材（纯 float 阵）；③一张"BT.2020 PQ 饱和红"16-bit PNG + 一次 `--jpeg-gain-map-base-gamut srgb`；⚠ 本机 %TEMP% 按可执行文件被拦 ⇒ **端到端臂必须先把 TMP/TEMP 指到仓外可写卷**，否则这三格会以 `exit -13` 假红（U26 实测成因逐字在案） |
| **P1-b** | 钉住 **A5-6**（`IsValidIccProfile` 负样本全过） | `tests/metadata/Modes`（`icc` mode，`MetadataGroup.cs:160/243`）+ `verify-tiff-icc.ps1` | 把 4 处调用从"纯正例"扩成**三件负例**：(a) 128 B 全零 + `acsp`；(b) 合法 profile 截断到 300 B；(c) 头 `size` 字段改成与实际字节不等。判据 = 三者 `IsValidIccProfile` **必须 false**，且 `GetOrLoad` 走"拒绝 + 点名"路径不得进缓存。⚠ 必须同时钉住"缓存命中门"（`IccProfileService.cs:341/456`）与"用户 PLAN/iccs 接受门"（`:443`）两个消费面，否则只测函数不测接线 ⇒ 与 A4-C5 同一个坑 | 三个字节级构造件（A5 本轮已在仓外目录复算过同一组负样本，形状可直接照搬）；不需真 ICC 生态 |
| **P1-c** | 钉住 **A5-7**（S3 交付 cICP + 源 iCCP 两套矛盾描述） | `verify-color-strategy.ps1` ④ 段（`-not $r.Icc` 判据与 `前置-取数尺有牙` 的尺子正对照**都已经在位**，只是操作数不对，U25） | 把 `cicp-only-*-png` 的源换成 **自带 iCCP 的 Display P3 PNG**（`_lib-color-assets.ps1` 的 `New-P3IccAsset` 现成，别新增第二套生成口径），格式轴从 {avif, png} 补 **webp**（libwebp 丢 iccgen ⇒ 唯一必须后置的容器）；判据不变：`-not $r.Icc` + 同格必须有 cICP ⇒ 现交付链没有"删已在文件里的 ICC"的动作会当场红 | `p3.icc` + 一张已嵌 iCCP 的 PNG（`_lib-color-assets` 自产） |
| **P2-a** | 把 **性能门**接进批次并绑指纹（§4 全部四条） | `_probe-test-group-coverage.ps1`（补"有没有执行者"这一判据，见 P2-b）+ `verify-color-peak.ps1`（已有 `RunTool $pr @('peakmeasure', …)` 这种"清单脚本直接调 ServiceProbe mode"的范式） | ① 新增/接线一条 `bench` 门：**只跑绝对值门槛的那 3 条 Check 之外**，额外断言 `参考（解析式单核）` 与 `快路径增益` 两行存在且**增益 ≥ 4×**（这一条会立刻抓住 C3-1/静5 的"表被恒关掉"，且它同时是 A3-N1 的第二块读数板）；② 汇总前打印被测 `FfmpegGui.dll` sha256 前 16 + 构建配置（抄 `verify-gainmap-memory.ps1:16-17`）；③ 把 `H1 ≤ H2×2.0` 拆成 `H1编排`（dry-run 下界）与 `H1变换` 两段，或**明确降级为"仅记录、不判"并把 `≥40 MPix/s` 改成随机器登记的相对基线**；④ 绝对值门槛必须允许"机器档位登记"，否则换机即成片假红（本仓 §6 第 103 条同族教训：时间型下限没有同夹具实测背书就是许愿） | 一张 ≥256,000 px 的合成帧（与 P0-a 共用）；⚠ **不绑 TMP 的臂会全红** ⇒ 同 P1-a 的环境前置 |
| **P2-b** | 修 **覆盖锁自己**（§3-C 第 2/3 条） | `_probe-test-group-coverage.ps1`（就地改） | ③ 的取证 (b) 现在把**守卫自己的注释**与**不在清单的脚本**都算成证据 ⇒ 判据应收成"**该 mode 是否出现在 `$scriptList` 内某脚本的 argv 位置上**"（即匹配 `@('mode` / `Exec $x "mode` 这类调用形，而不是全文子串）；并新增 ⑥：**组表 mode 并集里，凡"既不在 `$Probes`、又不在任何清单脚本 argv 上"的，必须逐名登记进一份显式豁免表，未登记的 ⇒ 红**。当前会被该新判据抓出的第一批（实测 U10/U11/U28）：`bench`、`bitdepth`、`gainmap-encode`、`gainmap-decode`、`gainmap-struct`（这 5 条查法可靠、引号形与子串形双向 0 命中）；`gamut` 列为**待复核定案**——它同样 quoted-token 0 命中，但 `gamutfit`（在 `$Probes` 内）会掩盖它的子串取证，两者是否同一条 mode 需实读 `tests/engine/Program.cs` 的分派表才能定案 | 零夹具；纯静态。同时把 `tests/gainmap-*` 三工程与 `verify-gainmap-host`（旧单体）的关系在 §1.1 写清 |
| **P2-c** | 其余 21 条零覆盖的**分诊**（不一把全补） | 各同族门 | 按"能不能用一条结构锁表达"分两档：**能**（`Invert3x3` 奇异返回、`DetectIccPrimaries` tag 类型、`BuildText` NUL、`FindPixelColorEntry` 接线、`ClampConcurrencyByMemory` 口径、`IsQueueRunning` 幂等、`PeakSourceText` 说谎）⇒ 每档一条结构/日志锁，成本个位行；**不能**（`chroma_subsampling` 420/444、AVIF 通道宣称 vs 交付、预设回放两腿、UltraHDR 输入×GainMap 输出、`--color-hdr-peak`→HLG L_W、"601" 别名、`ResolvePrimariesToken` 反查、GainMap 表外原色静默）⇒ 需要先造夹具或先做 A/B 读数，交主循环派工 | 逐条不同；`ResolvePrimariesToken` 一条注明"当前 0 消费点 ⇒ 属潜伏，按本仓纪律登记不派工"（同 `_probe-hdr-peak-oracle` 的处置形态） |

---

## 6 未证边界（本 lane 判不了的，写清缺哪条证据）

1. **§2 表的 N=49 是"本 lane 逐条取过证的集合"，不是全部缺陷主张。** 下列主张**未判定（本 lane 未取证）**，不进 N：A2-13（ICC TRC 派生自声明值 vs 链末实际曲线，需出口侧回读）、A2-16（`hable:param=0.5` 死参数）、A2-N10（`Equivalent` 的 1e-4 容差 vs lcms 取整差 2.5e-4 ⇒ BT.709-ICC 源被判不可命名空间）、A3-N4（`RawGamutFit` 抽样步长与行宽成整数倍 ⇒ `gamutfit` mode 实测 19/0 但无相位对照）、A3-N6/N7/N8（舍入方式清点、`eotfTable8` 死开关、尾元素边界）、A3-N10（`GamutMapScalar` 凸包/保亮度侧）、A4-C3/C7（CarryIcc 委托定案、表曲线不参与等价）、A5-3（Adobe RGB 无损出口被死代码供着）、A5-4（`GuessColorSpace` 兜底丢 TRC）、A5-8（JXL bare codestream vs 容器化）、A5-9（PQ `curv` 1024 项里 57 项为 0）、B1-F7（门禁注释与自家读数矛盾）、B3-N8/N9/N10、C1 全 9 条候选（性能项/未判定）、C2-7/C8/C9/C10、C3-3/4/6/7/8/9/10/12/13/14。**这些条要定案，缺的多半是端到端读数而不是断言**。
2. **`gates-round-beta7.log` 不能作为"73 条当前状态"的读数。** 它截断于 `verify-format-regression`，无末轮汇总；且本机 %TEMP% 按可执行文件被拦造成 `verify-color-wiring 56/50`、`verify-color-strategy 47/26` 成片红（U26，成因逐字 `UnauthorizedAccessException … cms_*.png … is denied` / `解码失败 (exit -13)`）。⇒ 本 lane 的所有"覆盖 vs 无覆盖"结论都是**静态对账 + 探针文本对账**，不含"这条门当前是不是绿"的判断。**要一份可信的 73 条现状，必须在一台 TMP 可写的机器上重跑**，且按 §3.4 的有效性前置（整轮 `全部通过` ⟺ `failedItems` 为空 ⟺ 源码漂移检查通过）互证。
3. **本 lane 只实跑了一条门禁**（`_probe-test-group-coverage.ps1`，PASS=8/FAIL=0，TMP/TEMP 指到 `tests/output/_d1probe`，跑后已清空该目录，`git status` 复核工作树未新增任何文件）。其余全部读数是**日志回读**（`_baseline_modes/*`、`gates-round-beta7.log`）或**源码行读**。**未做任何变异验证**——变异必须改源+重建被测配置，越出只读约束；⇒ §5 各条"判据打什么"都是**方向**，是否真有牙必须由变异实测（删判据 ⇒ 必须红）确认，遵守既有教训「变异测试必须重建被测配置否则假绿」。
4. **`tests/` 内两份实现并存的归因未定。** `bench` 同时存在于 `tests/diagnostics/Modes/bench.cs`（`_baseline_modes` 的读数来源，`_summary.csv` 记组=diagnostics）与 `tests/ServiceProbe/Program.cs:175/7305`。两份是否逐字同源、漂移了会不会互掩，本 lane 未核（`verify-ui-split-verbatim.ps1` 那套逐字锁只覆盖 UI 组，不覆盖 ServiceProbe ⇄ 拆分工程）。
5. **`_probe-test-group-coverage` ③(b) 子串取证的确切危害面未量化。** 本 lane 证明了 `bitdepth` 靠守卫自己的注释通过取证；但"40 个 mode 里究竟有几个是**仅**靠 ③(b)/(c) 而非真被调用"没有逐名定案（§1-U28 只覆盖其中 23 个的查法，UI 系 17 个未逐个查，它们经 `verify-ui-split-union.ps1` 确实有执行者）。
6. **`.ps1` 清单外的执行者未盘。** 本 lane 只回答"73 条清单 + `$Probes` 26 mode 跑了什么"。`tests/*/[八] 组工程` 是否被 IDE 任务、CI 配置或 `docs/TEST_EXECUTION_LEAD_2026-10-07.md` 的排程跑到，未查。
7. **A3-N1 与 C3-1/静5 是否同一处代码的一次修复能同时收掉，未证。** 两者共享 `ColorKernels.cs:503-504` 这个判据，但 A3 的补法是"追加 `&& !encScaleOn && !dstHlg`"（收紧、走解析式 ⇒ 正确性上来、性能不变），C3 的补法是"把解码查表与编码解析拆开"（性能上来、正确性要看新写法）。⇒ 若只按其中一条修，另一条会继续存在。这条需要实现臂做一次同时覆盖两判据的方案，本 lane 不给结论。
