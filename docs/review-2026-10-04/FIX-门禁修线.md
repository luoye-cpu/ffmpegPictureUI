# FIX — 门禁修线（2026-10-04 轮，针对 `C-门禁脚本可靠性.md` 的 P1-4 / P1-5）

- **任务**：按本轮复查报告 `docs/review-2026-10-04/C-门禁脚本可靠性.md` 的 §2.2（P1-4）与 §2.1（P1-5）
  修复两条门禁，并对**每条改动做变异验证**（改坏⇒转红、改回⇒转绿）。
- **授权范围内的改动**：**只有两个文件** —— `tests/scripts/verify-colorfmt-matrix.ps1`、
  `tests/scripts/verify-encoder-knob-liveness.ps1`。`src/**`、`docs/**`（既有文件）、其余 `tests/scripts/**` **零改动**；
  未执行任何 `git add` / `git commit`。变异体与全部实测产物都在**系统临时目录** `C:/Users/20210/AppData/Local/Temp/fix-*`（= Git Bash 的 `/tmp/fix-*`）。
- **宿主**：pwsh **7.6.6**（实跑）；PS **5.1.26100.9549**（静态解析，真 5.1 宿主）。
- **基线来源**：先改前各实跑一次取数（下 §1），改后再各实跑一次确认仍然绿（下 §4）。

---

## 〇、结论速览

| 条目 | 改前形状 | 改后 | 变异验证 | 结果 |
|---|---|---|---|---|
| **P1-4** `verify-colorfmt-matrix` ⚠ 无上限（结构性绿灯通道） | 基线 `PASS=2 FAIL=0 WARN=4 exit 0`；⚠ 涨到多少都绿 | **新增 ⚠ 条数上限闸**：`$maxWarn = 6`（= 实测 4 **+** 明示余量 2）；超限 ⇒ FAIL + exit 1 + **逐条点名**；另加**判据格数下限**（`≥6` 格） | ✅ 有（6/7/8 三档 + OLD 对照） | ✅ **修好**，当前环境仍绿 |
| **P1-5** `verify-encoder-knob-liveness` `$minLive = 19`余量 0 | 阈值恰等于实测活臂数 ⇒ 正常波动即红（实测纯色夹具掉到 18 就红） | **`$minLive = 17`**（= 实测 19 **−** 明示余量 2），取值口径写进注释（含"改夹具先看这里"） | ✅ 有（纯色夹具 ×2、禁 3 臂） | ✅ **修好**，当前环境仍绿 |
| PS5.1 兼容（改后两个文件） | — | 真 5.1 宿主 `ParseFile` | — | ✅ **2/2 PARSEOK，0 错误**，无 PS7+ 语法裸token |
| ⚠ 新发现（**本轮未改**） | `verify-colorfmt-matrix` ICC 读取**跨格污染** | 见 §5.1（有实证，属超出本次授权的行为改动） | ✅ 有（误打误撞的反向证据） | ⚠ 建议下轮修 |

三条想起的结论先行：

1. 两条闸现在都**有牙**（Δ1 即转红），且**都不是贴着实测写的**（分别留了 ±2 的明示余量）。
2. 「余量」在本仓是**刚刚付出代价买来的东西**：上一轮的 CJK 门禁就是因为 `$BASE_CS_UI` 贴着实测写、余量 0，
   换来一个"换个机子就假红"的教训。本轮两条闸都按 **「实测值 ± 明示余量」** 取值，把口径、测得时间、
   什么情况下必须回来重算**全部写进注释**，避免下一次有人照当前读数再写死一次。
3. 本轮的反面证据同样说明：**闸门一旦 100% 依赖"当前环境下的读数"，它就不稳**。上面两条闸的余量只是
   "容忍正常漂移"，真正的不稳定源（§6.1 的跨格污染、夹具对图像内容的敏感性）要另行处理。

---

## 1. 改前实测基线（2026-10-04，本机）

| 脚本 | 实跑命令 | 读数 | exit | 耗时 |
|---|---|---|---|---|
| `verify-colorfmt-matrix.ps1` | `pwsh -File tests/scripts/verify-colorfmt-matrix.ps1` | `PASS=2 FAIL=0 WARN=4` | **0** | 55 s |
| `verify-encoder-knob-liveness.ps1` | `pwsh -File tests/scripts/verify-encoder-knob-liveness.ps1` | `活=19 被拒=1 未判=1 缺口=1 合计=22`；`PASS=19 FAIL=0` | **0** | 260 s |

两条都与复查报告 §2.2 / §2.1 记载的基线一致（`WARN=4`、`活=19`）⇒ **"$minLive=19 恰等于实测" 这个指控成立**。

---

## 2. 修复一：P1-4 `verify-colorfmt-matrix` —— 给 ⚠ 设条数上限

### 2.1 改了什么（含 file:line，改后行号）

| file:line | 改动 |
|---|---|
| `verify-colorfmt-matrix.ps1:39-54` | **头部判据改写**：原来写的是「`⚠` ⇒ WARN（**不计入 FAIL**）」，现在写明 —— **单条** ⚠ 仍不判 FAIL（它是当前判据下的已知假警报），但 **⚠ 的条数有显式上限 `$maxWarn`**；并整段记录**取值口径**（实测 4 / 余量 2 / `$maxWarn=6`）、余量吸收的是什么、什么时候必须回来重定（判据修好 ⇒ 收到 0+2；矩阵变宽 ⇒ 重测再 +2；不许靠抬 `$maxWarn` 消红）。 |
| `:83` | `$pass = 0; $fail = 0; $warn = 0` ⇒ 追加 `$cells = 0`（判据格数计数器）。 |
| `:85-90` | 新增 `$maxWarn = 6` 与 `$warnItems = New-Object System.Collections.ArrayList`，注释说明二者用途（上限值 / 可点名凭据）。 |
| `:94` | 每格开跑前 `$cells++`（无论该格最终 ✅/⚠/❌，**走完判定就算一格** ⇒ 计数与"实际有多少格判了"严格一致）。 |
| `:109-120` | 每格落 ⚠ 时把**点名凭据**（`$f 格：有 ICC(大小/md5) 但非 P3 参照（期望参照 md5=…）`）压进 `$warnItems`；`$warnRow` 供下面撤账用。 |
| `:129` | CICP 覆盖宣判时同步撤账：`$warn--; $null = $warnItems.Remove($warnRow)` ⇒ **`$warn` 计数与 `$warnItems` 条目严格 1:1**（否则点名会点错）。 |
| `:138-147` | **⚠ 条数上限闸**：`if ($warn -gt $maxWarn) { $fail++; 打 ❌ 行; foreach ($w in $warnItems) 逐条点名 }`。 |
| `:148-151` | **判据格数下限闸**：`if ($cells -lt 6) { $fail++; ❌ 点名 }`（把"某格被 `continue` 吞掉/列表被改空 ⇒ 断言变少仍 exit 0"这条假绿通道堵掉）。 |
| `:156` | 汇总行同步改写：`PASS=… FAIL=… WARN=…（表格型；WARN<=$maxWarn 时不计入 FAIL，超限即判红并逐条点名，见头部）`。⚠ 保留 `PASS=n` 形态。 |

### 2.2 为什么是 6（不是别的数）

按本仓对 CJK 门禁的处置口径（**阈值不许贴着实测写，否则余量 0 ⇒ 下个 Rou / 换个机子就假红**），
取值写成**「实测 + 明示余量」**，并把理由写进代码：

```
实测 WARN 基线 = 4        （2026-10-04 本机实测：jpg / webp / tiff / jxl 四格落 ⚠；png / avif 因 CICP=smpte432 落 ✅）
明示余量       = 2
$maxWarn       = 4 + 2 = 6
```

- **余量 2 吸收的是单格到成对的正常漂移**：某格因工具链/产品换版从"CICP 命中"退化成"ICC-only"（这类一格就是 +1 ⚠），
  或新增/换掉一个受测格式时的一格波动。
- **它明确不是免罚额度**：`≥7` 条 ⚠ 即"矩阵半数以上格子失去判别力"，必须红并逼人来修判据或修产品。
- **Δ1 即有判别力**：刀口实测在 **6 / 7**（下 §2.3），与 CJK 门禁的处置形状一致（Absatz 后有意为之处撕 legitimacy）。
- 反向留给下轮的钉子写在注释 `:51-53`：真正把判据改成"色度是否为 P3"（而非与参照 ICC 逐字节相等）后，
  那 4 条已知假警报会消失、基线回到 `WARN=0`，那时 `$maxWarn` **必须跟着往下收**，不许继续留 6 ⇒ 免的任务 командиров过去了 throttle 还在。

### 2.3 变异验证（每条必须有）

变异一律在 `/tmp/fix-cfmt/` 镜像里做（`src`、`publish` 用 junction 挂真目录；`tests/output/validate/jpgp3` 与脚本都是副本 ⇒ 仓库零写）。
**变异方式**：把矩阵加宽（`$o` 每格唯一 + 追加 `jpg/webp/tiff/jxl` 四趟**真实转换**），ICC 抽取与判定逻辑**一行未改**
⇒ 每格的判定路径与基线逐字相同。**红**只能归因于 ⚠ 条数超限，不会被别的闸混进证据。

| # | 变异 | NEW（修复后） | OLD（修复前，`/tmp/fix-old/…OLD` 同样加宽） |
|---|---|---|---|
| 基线 | 无（6 格） | `PASS=2 FAIL=0 WARN=4` **exit=0**（55 s） | `PASS=2 FAIL=0 WARN=4` **exit=0** |
| **V1** | ⚠ = **6**（多跑 jpg / webp 两趟） | `PASS=2 FAIL=0 WARN=6` **exit=0**（59 s）✔ 上限**含端点**仍绿 | —（无上限，当然绿） |
| **V2** | ⚠ = **7**（多跑 jpg / webp / tiff） | `PASS=2 FAIL=1 WARN=7` **exit=1** + **7 条点名**（56 s） | — |
| **V3** | ⚠ = **8**（多跑 jpg / webp / tiff / jxl） | `PASS=2 FAIL=1 WARN=8` **exit=1** + **8 条点名**（70 s） | `PASS=2 FAIL=0 WARN=8` **exit=0** ← **结构性绿灯通道实证**：同一变异、OLD 版照样全绿且不吭声 |

⇒ **刀口 = 6/7**（第六条仍绿、第七条即红），且与上轮 M3 指控的形状完全吻合（"每格都是 ⚠ 仍 exit 0"）。

V3 NEW 实测输出（`/tmp/fix-r-cfmt2-warn8.txt`，原样摘录尾部）：

```
❌ ⚠ 条数超上限：实得 WARN=8 > 上限 6（= 实测基线 4 + 明示余量 2，取值口径见头部）⇒ 矩阵半数以上格子失去判别力，判红
      ⚠ 超限条：jpg 格：有 ICC(584B/7FDF17C0) 但非 P3 参照（期望参照 md5=DE6F1D2E，判据逐字节相等，已知假警报）
      ⚠ 超限条：webp 格：有 ICC(584B/7FDF17C0) 但非 P3 参照（期望参照 md5=DE6F1D2E，判据逐字节相等，已知假警报）
      ⚠ 超限条：tiff 格：有 ICC(584B/7FDF17C0) 但非 P3 参照（期望参照 md5=DE6F1D2E，判据逐字节相等，已知假警报）
      ⚠ 超限条：jxl 格：有 ICC(584B/7FDF17C0) 但非 P3 参照（期望参照 md5=DE6F1D2E，判据逐字节相等，已知假警报）
      ⚠ 超限条：jpg 格：有 ICC(584B/7FDF17C0) 但非 P3 参照（期望参照 md5=DE6F1D2E，判据逐字节相等，已知假警报）
      ⚠ 超限条：webp 格：有 ICC(584B/7FDF17C0) 但非 P3 参照（期望参照 md5=DE6F1D2E，判据逐字节相等，已知假警报）
      ⚠ 超限条：tiff 格：有 ICC(584B/7FDF17C0) 但非 P3 参照（期望参照 md5=DE6F1D2E，判据逐字节相等，已知假警报）
      ⚠ 超限条：jxl 格：有 ICC(584B/7FDF17C0) 但非 P3 参照（期望参照 md5=DE6F1D2E，判据逐字节相等，已知假警报）

PASS=2 FAIL=1 WARN=8（表格型；WARN<=6 时不计入 FAIL，超限即判红并逐条点名，见头部）
EXIT=1
```

V3 OLD 实测输出（`/tmp/fix-r-cfmt2-warn8-old.txt`，原样摘录尾部）：

```
PASS=2 FAIL=0 WARN=8（表格型；WARN 见头部说明，不计入 FAIL）
EXIT=0
```

**改回⇒转绿**：仓库内原位重跑改后脚本 ⇒ `PASS=2 FAIL=0 WARN=4`，**exit=0**（§4）。

### 2.4 保留原意（这条最重要）

- **单一已知的降级 ⚠ 仍然允许存在**（当前 `WARN=4` 绿、`WARN=6` 也绿），没有把 ⚠ 一律判红 ⇒ 正常跑不会失败。
- **不擅自升格判据**：⚠ 的根因（判据是"与 `_probe-jpg-p3-icc.ps1` 参照 ICC 逐字节相等"，而产物是另一份合法的 Display P3 字节）
  没有变，头部注释 `:55-59` 保留了原解释并说明"由上限闸兜底"，把"改成按色度判 P3"的待办钉在这儿。

### 2.5 假绿形状自查（改后）

| 常见假绿形状 | 本改动的处理 |
|---|---|
| 日志为空时裸否定式断言恒真 | 未引入任何否定式断言；新增两闸都是**计数比较**（`$warn -gt $maxWarn` / `$cells -lt 6`），两侧都是整数计数，不存在"两侧皆空 ⇒ 恒真"。 |
| 未设条数下限 | 新增 `$cells -lt 6` ⇒ 判红；且 `$fail` 计数进入既有 `if ($fail -gt 0) { exit 1 }`。 |
| 异常被吞按成功算 | 无 `try/catch`、无 `-EA SilentlyContinue` 包住断言；`$warnItems.Remove()` 的返回值显式 `$null =` 丢弃（不影响判据）。 |
| "红了但 exit 0" | 超限闸直接 `$fail++` ⇒ 复用既有 exit 1 路径，实测 V2/V3 exit=1 ✔。 |
| 点名/不可见 | 逐条打印 `$warnItems`（规格含格式 + ICC 大小/md5 + 期望参照 md5）。 |
| 替配额悄悄变成 set 漏洞 | 上限值单一来源 `$maxWarn`（`:89`），头部与643线行都用同一变量插值 ⇒ 改一处即同步（不会出现"注释说 6、代码是别的值"）。 |

---

## 3. 修复二：P1-5 `verify-encoder-knob-liveness` —— 给活臂下限留余量

### 3.1 改了什么（含 file:line，改后行号）

| file:line | 改动 |
|---|---|
| `verify-encoder-knob-liveness.ps1:264-267` | 汇总前的口径注释：把原来的「① 活臂数 ≥ `$minLive`（本轮实测 `活=19`，见基线读数）」改成指向新专段，并说明②是**比例闸**（天然带余量，不需要再留额外余量）。 |
| `:271-294` | **新增 `$minLive` 取值口径专段**（详见下 §3.2）：记录"原来为什么错 / 错成什么样 / 实测值与测得时间 / 余量为什么是 2 / 改夹具·换机·换工具链前必须先看这里 / 严禁照当前读数写死"。 |
| `:295` | `$minLive = 19` ⇒ **`$minLive = 17`**（唯一一处行为改动）。 |
| `:298-301` | 判红逻辑未动（`$c['live'] -lt $minLive` ⇒ `$boundFail++` ⇒ 计入 `$nFail` ⇒ exit 1），只是阈值换值。 |

### 3.2 为什么是 17（写在注释里的口径）

```
实测活臂数  = 19   （2026-10-04 实测：本机 Release 产物 + publish/PLAN 工具链 + src_8bit.png 照片样夹具；
                    同批读数：被拒 1、未判 1、缺口 1、合计 22）
明示余量    = 2
$minLive    = 19 − 2 = 17
```

**余量为什么留 2**（写进注释 `:282-288`）—— 本套件共 22 臂，**单臂/成对波动**有三类真实来源：

1. **后端/工具链换版**：同一旋钮的命令行形态会变（实测 `--avif-tune psnr` 发 `-tune psnr`，而 `--avif-tune IQ` 发 `-aom-params tune=iq`），
   libaom / cjxl 换版后某臂的 `$pat` 取不到值 ⇒ 少一条活臂；
2. **编码器默认值随版本变**：原本能被本夹具区分的两端变成同产物 ⇒ 该臂从 `live` 落 `undecidable`；
3. **JXL / GIF / AVIF 对输入内容敏感**：换一张**合法**的照片样夹具就可能掉 1 条。

2 条足够吸收这三类**正常漂移**；但**远小于整批塌方**的幅度（真事故一次掉 5 条以上，那时**应该**红）。
注释里把这条写成"**既不许喊狼来了、也不许被当成免罚额度的刀口**"，并钉了两条纪律：

- 改夹具 / 换机 / 换 PLAN 工具链 / 加臂减臂 ⇒ 先在新环境量实测，再按「**实测 − 2**」重算；
- **严禁**把 `$minLive` 直接改成"刚才跑出来的那个数"（那又是余量 0，就是本条要治的形状）。

### 3.3 同类阈值全库自查（要求是"一并扫一遍"）

| 写死的数 | 性质 | 是否需要留余量 |
|---|---|---|
| `-q 10/95`、`--webp-compression 0/6`、`--png-dpi 72/1200`、`--jxl-effort 1/9` … | **夹具取值**（要的就是两极端值） | 否 |
| `$wantArms = $rows.Count + $naming.Count` | **按结构算出**（跟着清单走） | 否 |
| `$maxUndecidable = Floor($judgedArms / 3)` | **比例闸**（跟着已判臂数自动缩放 ⇒ 天然带余量） | 否 |
| `$minLive` | **照实测写死的绝对数** | ✅ **唯一一处**，本轮已改为 17 |

⇒ 结论：**本脚本只有 `$minLive` 一处属于该形状**，已在本次处理。

### 3.4 变异验证

变异在 `/tmp/fix-knob/` 镜像里做（同上：junction 挂 `src` / `publish`，夹具与脚本副本在镜像内）。

| # | 变异 | NEW（`$minLive=17`） | OLD（`$minLive=19`） |
|---|---|---|---|
| 基线 | 无（原照片样夹具） | `活=19 缺口=1` ⇒ `PASS=19 FAIL=0` **exit=0**（260 s，仓库内原位） | `活=19` ⇒ exit=0 |
| **M1** | **纯色夹具**（脚本头 `:118-119` 自陈的退化形状：512×384 8-bit RGB 纯深蓝，替换 `$src`） | `活=18 死=1 被拒=1 未判=1` ⇒ `PASS=18 FAIL=1` **exit=1**（126 s）<br>红**只剩一个真缺陷**：`K gif --gif-dither：旋钮是死的（两端都没发到编码器）`；**没有** `活臂数下限` 那一行 | `活=18` ⇒ `PASS=18 FAIL=2` **exit=1**（155 s）<br>多出的一条正是 `FAIL 活臂数下限：活=18 < 19` ⇒ **这条就是上轮报告预言的假红**（复核报告 §2.1 实测值为 18，本轮复现一致） |
| **M2** | **临时摘掉 3 条臂**（`lab='-q'` / `--webp-preset` / `--avif-cpu-used` 三条改 `expect='gap'` ⇒ 模拟"这 3 条臂失去判别力"） | `活=16 缺口=4` ⇒ `FAIL 活臂数下限：活=16 < 17` ⇒ `PASS=16 FAIL=1` **exit=1**（166 s）✔ **低于新阈值仍转红 ⇒ 闸没被放松成摆设** | 同形状更红（余量更少），无需对照 |

M1 NEW / OLD 的关键差一行，原样摘录：

```
# OLD（/tmp/fix-r-knob-solid-old.txt）
---- 旋钮活性明细桶: 活=18 死(两端都没发)=1 被拒(已播报)=1 未判(夹具无判别力)=1 … 合计=22 ----
FAIL  活臂数下限：活=18 < 19 ⇒ 判据整体失去判别力（全未判不许算绿）
---- 旋钮活性: PASS=18 FAIL=2 越界被拒(不计入)=1 未判(不计入)=1 缺口(未做)=1 合计=22 ----
EXIT=1

# NEW（/tmp/fix-r-knob-solid-new.txt）
---- 旋钮活性明细桶: 活=18 死(两端都没发)=1 被拒(已播报)=1 未判(夹具无判别力)=1 … 合计=22 ----
---- 旋钮活性: PASS=18 FAIL=1 越界被拒(不计入)=1 未判(不计入)=1 缺口(未做)=1 合计=22 ----
--- 判红/待补明细 ---
   K gif --gif-dither：旋钮是死的（两端都没发到编码器） :: 1579B(7335DD99DE4D7DF8) vs 1579B(...)｜-gifflags ([^\s"]+)=<未下发>/<未下发>
EXIT=1
```

**诚实说明**：M1 的 NEW **并没有变成全绿** —— 因为纯色夹具下 `K gif --gif-dither` 是**真 inert**（解码产物逐字节相同、
两端命令行都查无此参），这条红是**正确行为**，不该被余量吃掉。余量做到的事是：**把" Trait d臂 Regression"与
"判据自身的环境漂移"分开** —— 旧写法把两者叠成一个复合红（`FAIL=2`），读者分不清"这次红到底是不是我的锅"；
新写法只保留真缺陷那一条（`FAIL=1`），而 **M2 证明判别阈值本身的刀口还在**（掉 3 条就红）。

**改回⇒转绿**：仓库内原位重跑 ⇒ `活=19` / `PASS=19 FAIL=0` / **exit=0**（§4）。

### 3.5 假绿形状自查（改后）

- 改动是**纯阈值下调 + 注释**，没有新增任何断言、没有新增 `continue`、没有新增异常捕获
  ⇒ 不可能引入"日志为空恒真""异常被吞按成功算"这类形状。
- `.ps1` 原有两条闸（`$wantArms` 条数闸、`$maxUndecidable` 比例闸）与两者的 `$nFail` 归集、exit 1 路径**一行未动**。
- `$minLive` 只有**一个定义点**（`:295`）与**一个使用点**（`:298`）⇒ 不存在"两处阈值不一致"的形状。

---

## 4. 改后实跑结论（当前环境，仓库内原位）

| 脚本 | 命令 | 读数 | exit | 耗时 |
|---|---|---|---|---|
| `verify-colorfmt-matrix.ps1` | `pwsh -NoLogo -NoProfile -File tests/scripts/verify-colorfmt-matrix.ps1` | **`PASS=2 FAIL=0 WARN=4`**（`WARN<=6`） | **0** | 55 s |
| `verify-encoder-knob-liveness.ps1` | `pwsh -NoLogo -NoProfile -File tests/scripts/verify-encoder-knob-liveness.ps1` | **`活=19`；`PASS=19 FAIL=0 越界被拒=1 未判=1 缺口=1 合计=22`** | **0** | 260 s |

colorfmt 改后实跑（原样摘录，`/tmp/fix-r-cfmt-new-inrepo.txt`）：

```
参照 P3 ICC md5 = DE6F1D2E（来自 _probe-jpg-p3-icc.ps1，色度已核）
格式     ICC(大小/md5)          CICP/其他                        判定
png    584B/7FDF17C0        iec61966-2-1,smpte432          ✅ CICP=P3
jpg    584B/7FDF17C0        unknown,unknown                ⚠ 有 ICC 但非 P3 参照（见头部：判据是字节相等，已知假警报）
webp   584B/7FDF17C0        unknown,unknown                ⚠ 有 ICC 但非 P3 参照（见头部：判据是字节相等，已知假警报）
tiff   584B/7FDF17C0        unknown,unknown                ⚠ 有 ICC 但非 P3 参照（见头部：判据是字节相等，已知假警报）
jxl    584B/7FDF17C0        jxlinfo: JPEG XL image, 64x4…  ⚠ 有 ICC 但非 P3 参照（见头部：判据是字节相等，已知假警报）
avif   584B/7FDF17C0        iec61966-2-1,smpte432          ✅ CICP=P3

PASS=2 FAIL=0 WARN=4（表格型；WARN<=6 时不计入 FAIL，超限即判红并逐条点名，见头部）
EXIT=0
```

⇒ **两条闸改完后在当前环境仍全绿**（没有把门禁改红），同时各自的变异实验证明**它们仍然有牙**。

---

## 5. PS5.1 兼容性结论

方法：**真 5.1 宿主**（`powershell.exe` = `5.1.26100.9549`）调
`[System.Management.Automation.Language.Parser]::ParseFile()`；再用 token 流扫 PS7+ 语法
（**排除** `StringLiteral / StringExpandable / HereString* / Comment` token ⇒ "出现在字符串里允许"）。

| 文件（改后） | 解析 | tokens | errors | PS7+ 语法体检 |
|---|---|---|---|---|
| `tests/scripts/verify-colorfmt-matrix.ps1` | **PARSEOK** | 921 | **0** | **CLEAN** |
| `tests/scripts/verify-encoder-knob-liveness.ps1` | **PARSEOK** | 2758 | **0** | **CLEAN** |

⇒ **2/2 PASS、0 错误**；无裸 `&&` / `||` / `??` / `??=` / `?.` / `?[` / 三元 `?:`（`&&` 仍只出现在 knob `:102`
的 `-join ' && '` 字符串内）。**宿主版本与检查脚本输出见 `/tmp/fix-ps51-result-final.txt`。**
⚠ 与既有 `| knock on` 同款**遗留不变**：静态兼容 ≠ 双宿主可用，运行器取自身进程路径作宿主 ⇒ PS5.1 分支从未真正执行过（上轮 §四遗留）。

**文件编码核对**（本仓要求"保持原文件 BOM 状态"）：

| 文件 | 改前 | 改后 |
|---|---|---|
| `verify-colorfmt-matrix.ps1` | UTF-8 **无 BOM**、**CRLF** | UTF-8 **无 BOM**、**CRLF**（未变） |
| `verify-encoder-knob-liveness.ps1` | UTF-8 **无 BOM**、**LF** | UTF-8 **无 BOM**、**LF**（未变；中途被我用 PS 重写时误转过 CRLF，已用字节级还原纠正） |

---

## 6. 遗留 / 建议

### 6.1 ⚠ 新发现（**本轮未改**，超出授权，建议下轮处理）：`verify-colorfmt-matrix` 的 ICC 读取**跨格污染**

- **位置**：`verify-colorfmt-matrix.ps1:106-107`（`-w "$d/%f.icc"` + `$iccFile = "$d/$($out.BaseName).icc"`）。
  六格的产物基名都是 `src` ⇒ 六格**共用同一个 `$d/src.icc`**；抽取失败时不会先删、也不检查是否本次生成。
- **实证**（本次变异时误打误撞拿到）：当我把每格的 ICC 抽取改成落在各自目录后，`jxl` / `avif` 两格的 `$iccFile` **不再存在**
  （`fmtmatrix_722ffe79/jxl_5/` 只有 `src.jxl`、`avif_6/` 只有 `src.avif`，而 `jpg_2/` 里有 `src.icc` 584 B）
  ⇒ 说明**基线的 `jxl` / `avif` 两格显示的 `584B/7FDF17C0` 其实是从更早的某格残留下来的**，不是本格产物里的 ICC。
  （脚本头 `:91` 自己也写着"JXL 另用 jxlinfo，因 exiftool/ffprobe 读不到其 ICC" —— 这条是作者已知的，但"读到的是上一格的残留"显然不是预期。）
- **对本轮新闸的影响：**有限但要在案。当前 `WARN=4` 里 `jpg/webp/tiff` 三格是**真**读数，只有 `jxl` 一格依赖残留，
  而 `jxl`/`avif` 两格最终都被 CICP 判据覆盖 ⇒ 计数结果为净零影响。即便按诚实口径（`jxl` 不计）重算是 `WARN=3`，
  `$maxWarn=6` 仍有 3 条余量 ⇒ **本次取值不因这条发现而失守**。
- **真正的风险在另一方向**：如果某格**自身**抽取失败、且它没有 CICP 命中，残留文件会让它**不落到**
  `❌ 产物无任何 P3 描述` ⇒ 那才是一条货真价实的假绿通道。
- **建议（下轮）**：抽取前先删 `$iccFile`，或按格使用唯一文件名（如 `"$d/icc_$f.icc"`），抽不到就按"本格无 ICC"处理；
  修完后把 `$maxWarn` 按新基线（`预计 WARN=3 + 2 = 5`）重定。

### 6.2 其它建议

1. **P1-4 的真正根治**（`:43` 的待办）：把 ⚠ 的判据从"与参照 ICC 逐字节相等"改成"解析 ICC 的 rXYZ/gXYZ/bXYZ 是否 Display P3"，
   届时那 4 条已知假警报消失，⚠ 可升格为 FAIL，`$maxWarn` 收到 `0 + 2 = 2`。**这是让这条锁真正有判别力的唯一路径**，
   本轮的上限闸只是把它从"永不红"变成"大面积失守才红"。
2. **P1-5 的后续**：本轮只加了 2 条余量，没解决"`undecidable` 依赖夹具内容"的根因。建议给几对已知夹具敏感的臂
   （`--avif-row-mt`、`--avif-tune`、`--jxl-modular`）补一张**专用** fixture（多 tile / 高熵），把 `未判=1` 也压下去。
3. **上轮未返工的 6 条**（`C-门禁脚本可靠性.md` §2.3）本轮仍未处理：`jbrd:240` 空值恒真、`jbrd:163` `$null.FullName`、
   `gainmap3p:141` 裸否定式、`orientation` 三处 `throw`、`package-smoke` 无显式 `exit 0`、`knob` 的 `nocmd` 前缀措辞
   （`nocmd` 打印 `WARN` 前缀却计入 `$nFail` ⇒ 输出行写 WARN、实际会 exit 1，标签与后果不一致）。
4. **双宿主实跑**：静态 PARSEOK 不等于可用；建议至少在 PS5.1 下各跑一次 `verify-encoder-knob-liveness`
   与 `verify-jbrd-e2e`，核对中文判据（`命令行`、`增益图/降级`）在 5.1 下的读数。
5. **README.md / README.en.md 在我作业期间被别的线改动过**（`git status` 里出现而本次开门时没有）——本轮未触碰，仅登记。

---

## 7. 取证环境（可复现）

- 临时目录（**全在仓库外**）：`C:/Users/20210/AppData/Local/Temp/`
  - `fix-cfmt/`、`fix-knob/`：两个脚本的运行镜像（`src`、`publish` 为 junction，指向仓库只读内容；
    `tests/output/**` 是镜像内独立目录 ⇒ 所有写操作都落在 Temp）；
  - `fix-old/`：两个脚本的**改前副本**（`.OLD`），用于 NEW/OLD 对照；
  - `fix-gen-mutants.py` / `fix-gen-mutants2.py` / `fix-gen-knob-solid.py`：变异体生成器（每次替换都断言"唯一命中"，防打漏）；
  - `fix-run-colorfmt.ps1` / `fix-run-colorfmt2.ps1` / `fix-run-knob.ps1`：运行器（逐个 `pwsh -File`，把输出与 `EXIT=` 落盘）；
  - `fix-ps51-check.ps1`：PS5.1 静态解析 + PS7+ 语法体检脚本；
  - `fix-r-*.txt`：全部实跑原始输出；`fix-r-log*.txt`：分批运行日志。
- 实跑统计：colorfmt **9 次**（基线 2 + 变异 7，55–72 s/次）、knob **5 次**（基线 2 + 变异 3，126–260 s/次）、
  PS5.1 解析 **2 轮**。
- **仓库写入核对**：`git status --short` 与本轮开门时一致（除我这两个脚本 + 本报告），未见新增未跟踪文件；
  脚本自身的运行目录 `tests/output/validate/l3_knob` 是 knob 的常规工作目录（每次开跑自删重建），`tests/output/` 被 `.gitignore` 排除。
- 未执行 `git add` / `git commit`；`src/**`、`docs/**`（既有文件）、其余 `tests/scripts/**` **零改动**。
