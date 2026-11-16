# Finding 闭环表（`a9e2830..957cfa8` 48h 审查 → 重出 beta17）

> **版本策略（2026-11-16 定）**
> - 本轮版本位固定 `1.6.0-beta17`：**不升版本、不打新 tag、不出包**；修复只落在 `main`。
> - `tag 1.6.0BETA17` 仍指 `957cfa8`；本批修复停在"可重出 beta17"状态，**重出需用户明确指令**才跑 `pack.ps1`。
> - 出包前须 `tests/scripts/_release-gate-check.ps1` 全绿（红态禁止出包，见 `docs/RELEASE_GATES.md`）。

- 生成日期：2026-11-16
- 用途：重出 beta17 交付时的闭环凭据。每条 finding 的处置、理由、落点。
- 总数：**34**（HIGH 3 / MEDIUM 12 / LOW 18 / INFO 1），与 `CHANGES_48H_REVIEW_RESULT_2026-11-16.md` §2 统一发现表一一对应。
- 处置三态：**修复**（动代码/门禁/文档）｜**接受**（已知问题、记录在案、本批不处理）｜**不改**（正控通过 / 无问题 / 环境噪声）。
- 本文档为重出 beta17 的闭环凭据；代码侧修复由独立 src/tests 批落地，本表只登记落点，不改 `src/`、`tests/`、`CHANGELOG`、版本位。
- **本表随每批提交增量更新**：已收口行落点写 `提交 <短哈希>`；读数见各提交 body 与 `review-evidence/logs/`。UI 路与判为接受/不改的行暂留待最终表。

| ID | 级别 | 处置 | 理由（一句） | 对应提交 / 批次 |
|---|---|---|---|---|
| COLOR-01 | HIGH | 修复 | legacy 分支 `pngCicpFirst` 不看 `DeclaredStrategy`，显式 carry→PNG 静默丢 iCCP | 提交 `521b318`（共用判定 `IccCarryPolicy`） |
| COLOR-07 | HIGH | 修复 | `planPromisesGeneratedIcc` 短路把 legacy 直连 carry 误判为"计划自生成"，WebP 零 ICC 零 CICP（本批新引入，最优先） | 提交 `521b318`（共用判定 `IccCarryPolicy`） |
| MA-01 | HIGH | 修复 | beta17 发布提交上 `verify-decision-delivery`78/9、`verify-probe-mirror-parity`89/2 两红未登记；修绿+抬表+出包前必跑变更链路清单，红门禁不随包发布 | 提交 `a15efcd`（decision-delivery 7 断言 + mirror-parity 抬表 + 红态闸 `_release-gate-check.ps1`） |
| COLOR-02 | MEDIUM | 修复 | `decision-delivery` ⑦ 断言仍按旧 ffmpeg 位深标记/`-color_trc` 位置，avif16 `--depth 12,4` 已 fail-closed 91 | 提交 `49dbd3a`（legacy SDR 直通对齐写 `cICP=1/13`） |
| COLOR-03 | MEDIUM | 修复（文档侧） | 7 份带日期账本写"AVIF 不写 ICC"旧口径；按 §6 不回改，由 AGENTS §7 更正条目兜底 | 提交 `7695161`（AGENTS §7 更正 1） |
| COLOR-06 | MEDIUM | 修复 | 门禁用 exiftool 判 ICC、ffprobe 判 CICP，从不数值对撞"ICC 内部原色==容器 CICP" | 提交 `55369ff`（ICC/CICP 数值对撞门禁） |
| COLOR-08 | MEDIUM | 接受/待批 | 存量（a9e2830 前已存在）链末 t=iec61966-2-1 与标签 transfer=bt709 不一致，本批未触及 | 提交 `55369ff`（HDR→SDR 链末 sRGB 对齐） |
| UI-01 | MEDIUM | 修复 | 管道分支（cjxl/cjpegli/djxl）取消返回"已停止/超时"，与 `IsStopped` 精确匹配不一致 ⇒ 漏重排、错收进"仅显示报错" | 待重出 beta17 的 src 批（UI 路进行中） |
| UI-02 | MEDIUM | 修复 | 优雅停止（StopAfterCurrentQueue）走 break 不 Cancel ⇒ `IsRunning` 恒真，简洁模式误拒启动 | 待重出 beta17 的 src 批（UI 路进行中） |
| UI-04 | MEDIUM | 修复 | `c5f18e7` oxipng 面板迁到顶层+"PNG 显示/APNG 隐藏"判定，全 tests 树零 UI 断言 | 待重出 beta17 的 tests 批（UI 路进行中） |
| BUILD-04 | MEDIUM | 修复 | app 包无 oxipng.exe 而默认 pngopt=max，启动横幅 8 工具不点名 oxipng、首启无预警 | 提交 `17e4c7d`（app 档随附 oxipng；⚠ appstage 两态归因未验证，见 LOW/INFO 节） |
| GATE-01 | MEDIUM | 修复 | `verify-ui-split-union`7P/4F 根因＝旧参照宿主裸开窗未预置工具路径稳态，P1 拿 0 条 `[detect]` 行、早返回吞 P2-P5（非口径漂移/非产品回归） | 待重出 beta17 的 tests 批（UI 路进行中） |
| GATE-02 | MEDIUM | 修复 | contract 源码调用位实取 102 vs 登记表锁 101（总量 1006 vs 1005），`928ec89` 加断言未同批抬表 | 提交 `a15efcd`（调用位抬表） |
| MA-02 | MEDIUM | 修复（文档侧） | "AVIF 写不进 ICC"口径反向且已误导本批任务书前提；AGENTS §7 现行口径须更正 | 提交 `7695161`（AGENTS §7 更正 1） |
| MA-03 | MEDIUM | 修复 | 默认 max 的能力侧（app 缺 exe）/提示侧（横幅不点名）/验证侧（面板零断言）三方口径未对齐（=BUILD-04+UI-04+采集侧） | 提交 `17e4c7d`（三方口径对齐） |
| COLOR-05 | LOW | 不改 | 兼容性正控通过：坏版本被拒 rc=2、未知参数告警后忽略、WebP 恒 tv/JPEG auto 压回 pc 未漂移 | —（正控通过） |
| FMT-01 | LOW | 接受 | `CopyMetadataAsync`（`-overwrite_original` 直写产物路径）中途失败残留 `*_exiftool_tmp`，未像 ICC 嵌入路径那样 finally 清理 | 提交 `6593af8`（perl532.dll PATH 根因 + 临时件清理；tmp 残留**接受为已知**，见 LOW/INFO 节） |
| FMT-02 | LOW | 修复 | `synth_heic_grid_aux_noxmp.heic` 生成器注释称"未登记 URN 负控"，随件实为已登记 Apple URN（负控身份来自门禁现场改写字节） | 提交 `6593af8`（生成器注释/夹具修正） |
| UI-03 | LOW | 修复 | `Start()` 重启旧 CTS 只 Cancel 不 Dispose（`RebuildSemaphore` 有 Dispose，此处无） | 待重出 beta17 的 src 批（UI 路进行中） |
| UI-05 | LOW | 修复 | 用户点停止后日志连出"队列已停止"与"队列已完成"两行矛盾文案（无原因区分位） | 待重出 beta17 的 src 批（UI 路进行中） |
| UI-06 | LOW | 修复 | 旧代位深探测只丢结果不取消在跑的 ffprobe；回灌 Post 无 try/catch | 待重出 beta17 的 src 批（UI 路进行中） |
| BUILD-01 | LOW | 修复 | `pack.ps1` 全档模式裸调 `robocopy`（无全路径），PATH 不含 System32 的会话在 Step 2 中止 | 提交 `17e4c7d`（robocopy 全路径） |
| BUILD-02 | LOW | 修复（文档侧） | app/full 两包 exe sha256 仅 6B 差（bundle header nonce），AGENTS §4"用 dll 对账"对 AOT 单包不适用，须写清口径 | 提交 `17e4c7d`（AOT 哈希口径写入） |
| BUILD-03 | LOW | 接受 | 5 条构建警告（CS8604/CS8602×2/CA1416/IL3002）全存量，本批重写零新增、无 IL2026/IL2075 | 后续单独清理（接受） |
| BUILD-05 | LOW | 不改 | THIRD-PARTY-NOTICES 版本（oxipng 10.2.1/avifenc 1.4.2/cjxl v0.12.0）与实际二进制对得上，合规无害 | —（合规无害） |
| BUILD-06 | LOW | 不改 | ISA/优化偏好两侧对撞一致（均 `x86-64-v3`/Speed/win-x64） | —（对撞一致） |
| BUILD-07 | LOW | 修复（文档侧） | `FFMPEGGUI_FFMPEG_DIR` 斜杠归一化已在基线 `AppSettingsService.Norm()`=GetFullPath，§7"未了结"应划掉 | 提交 `7695161`（AGENTS §7 更正 3） |
| BUILD-08 | LOW | 不改+待办 | AOT 下 CPU/GPU 检测不失真（CPUID 直读、反射被守住）——否定结论成立；建议补 headless `--diagnose` 开关使可被门禁锁定 | 待办（不动作） |
| GATE-03 | LOW | 修复（文档侧） | 门禁/断言侧存量红收口（nclx 矩阵断言、HEIC 增益图 sato 误触发、alpha 拍平日志、JXR 中转目录） | 提交 `e5ec98e` |
| GATE-04 | LOW | 接受 | CHANGELOG trace 缺口（beta16 微基准/beta17 全量 5 warnings/beta15 contract 164/无包哈希），CHANGELOG 冻结至重出阶段补读数路径 | 推迟至重出 beta17（触发条件见 LOW/INFO 节） |
| GATE-05 | LOW | 修复 | runner 被残留死锁（已死 pid）+ STALE mtime 闸挡住的环境摩擦 | 提交 `991f496`（死锁自动回收 + STALE 可操作提示） |
| GATE-06 | LOW | 修复（文档侧） | 门禁/账本侧存量红收口（同 `e5ec98e`：nclx/增益图/alpha/JXR 断言对齐） | 提交 `e5ec98e` |
| MA-04 | LOW | 修复（重构） | "carry"在 engine/legacy/直连三处不共用单一真值（COLOR-01+COLOR-07 的共同结构成因），抽一处判定 | 提交 `a15efcd` |
| COLOR-04 | INFO | 不改 | 正控通过：PNG/AVIF 双写时 ICC 内部 cicp 与容器 cICP/nclx 逐值一致，"播放器行为分叉"CRITICAL 候选不成立 | —（正控通过） |

## 已落哈希的提交一览（2026-11-16 增量）

| 提交 | 主题 | 覆盖 finding |
|---|---|---|
| `7695161` | docs/INDEX + AGENTS §7 更正 + 闭环表初版 | COLOR-03 / MA-02 / BUILD-07 |
| `521b318` | fix(color) 共用判定 `IccCarryPolicy` | COLOR-01 / COLOR-07 |
| `a15efcd` | test(gate) decision-delivery 7 断言 + mirror-parity 计数 + RELEASE_GATES + `_release-gate-check.ps1` | MA-01 / MA-04 / GATE-02 |
| `d9f19fb` | docs 旧措辞对齐固定 beta17 + RELEASE_GATES §6 tag 纪律 | （口径修订，无 finding） |
| `17e4c7d` | chore(pack) robocopy 全路径 / 红态闸接线 / app 档随附 oxipng / AOT 哈希口径 | BUILD-01 / BUILD-02 / BUILD-04 / MA-03 |
| `55369ff` | fix(color) HDR→SDR 链末 sRGB + ICC/CICP 数值对撞 | COLOR-08 / COLOR-06 |
| `6593af8` | fix(exif) `perl532.dll` PATH 根因 + 临时件清理 | FMT-01（部分/接受）/ FMT-02 |
| `991f496` | chore(gate-runner) 死锁自动回收 + STALE 可操作提示 | GATE-05 |
| `e5ec98e` | fix(gainmap/deadcode) nclx 矩阵断言 + HEIC 增益图 sato + alpha 拍平日志 + JXR 中转目录 | GATE-03 / GATE-06 及 4 条存量红 |
| `49dbd3a` | fix(color) legacy SDR 直通对齐写 `cICP=1/13` | COLOR-02 |

> **仍开放（等最终表）**：UI 路 `UI-01/02/03/04/05/06`、`GATE-01`（旧参照宿主 bootstrap 对齐）；
> 接受/不改项 `BUILD-03/05/06/08`、`COLOR-04/05`、`GATE-04`（推迟到重出）。这些行暂留待最终表收口，不猜落点。

## LOW / INFO 逐条处置（2026-11-16）

### FMT-01（exiftool tmp 残留）——**接受为已知残留**
- 现状：每条 PNG 出口旁留一个 `<out>.png_exiftool_tmp`（"1 image files unchanged" 路径不自删），六轮累计 252 个，字节与 final 不同。
- 处置：试过 `-tmpdir` 重定向能消，但**打坏 webp ICC**（prophoto 42→41），已回退。
- 理由：final PNG 的 cICP/ICC 由编码器侧写入并经门禁回读证明正确；旁路 `_exiftool_tmp` 是 exiftool 的中间件，不进交付物，**不为旁路文件牺牲色彩载体**。
- 是否单独立条：否，接受为已知残留；重出包前在 `publish/` 清理即可（不随包分发）。

### BUILD-04（app 档 oxipng）——**已修但归因未验证**
- 现状：`17e4c7d` 已让 app 档随附 oxipng.exe。
- ⚠ **appstage 两态实测归因不成立**：本机扩展搜索命中仓库 `publish/PLAN`，两态（随附前/后）都压了 oxipng；"随附后才生效"目前**仅代码推断**。
- 处置：标"未验证"，需在**隔离环境**（无 `publish/PLAN` 在 PATH/搜索路径）复测 app 档 oxipng 是否真生效。
- 是否单独立条：是——隔离复测通过前不得在出包说明里写"app 档 oxipng 已验证"。

### GATE-04（CHANGELOG 可追溯）——**按版本策略推迟，重出 beta17 必须补**
- 现状：CHANGELOG trace 缺口（beta16 微基准 / beta17 全量 5 warnings / beta15 contract 164 / 无包哈希）。
- 处置：本轮固定 beta17、不动 CHANGELOG。
- 触发条件：**一旦用户指令 `pack.ps1` 重出 beta17，出包前必须**把上述读数路径补进 CHANGELOG（zh/en 同批），否则重出包无法归因。

### AGENTS.md:208 §8.1 冲突——**不改 §8，挂起等用户拍板**
- 现状：§8.1"重出（同内容重新打包）一律升版本号、新打 annotated tag"与本轮"固定 beta17 重出"**直接冲突**。
- 处置：不改 §8，按 RELEASE_GATES §6 挂起；重出包在 CHANGELOG/包内显式标注"重出、源提交 `<hash>`、与 tag 1.6.0BETA17(957cfa8) 内容不同"。
- 待办：需用户显式给"固定 beta17 重出"开豁免；豁免落地前不得打新 tag。

### 其余 LOW/INFO 占位
- **BUILD-03**（5 条构建警告全存量）：接受，后续单独清理；**BUILD-05**（第三方版本对得上）/ **BUILD-06**（ISA/优化对撞一致）/ **COLOR-05**（兼容性正控通过）/ **COLOR-04**（双写 ICC/CICP 逐值一致）：不改，正控通过。
- **BUILD-08**（AOT CPU/GPU 检测不失真）：不改+待办，建议补 headless `--diagnose` 开关，不动作。
- 最终表随修复批收口时补齐；本节只落已定性项。
