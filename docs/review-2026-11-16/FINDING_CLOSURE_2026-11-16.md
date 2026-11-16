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

| ID | 级别 | 处置 | 理由（一句） | 对应提交 / 批次 |
|---|---|---|---|---|
| COLOR-01 | HIGH | 修复 | legacy 分支 `pngCicpFirst` 不看 `DeclaredStrategy`，显式 carry→PNG 静默丢 iCCP | 待重出 beta17 的 src 批 |
| COLOR-07 | HIGH | 修复 | `planPromisesGeneratedIcc` 短路把 legacy 直连 carry 误判为"计划自生成"，WebP 零 ICC 零 CICP（本批新引入，最优先） | 待重出 beta17 的 src 批 |
| MA-01 | HIGH | 修复 | beta17 发布提交上 `verify-decision-delivery`78/9、`verify-probe-mirror-parity`89/2 两红未登记；修绿+抬表+出包前必跑变更链路清单，红门禁不随包发布 | 待重出 beta17 的 src/tests 批 |
| COLOR-02 | MEDIUM | 修复 | `decision-delivery` ⑦ 断言仍按旧 ffmpeg 位深标记/`-color_trc` 位置，avif16 `--depth 12,4` 已 fail-closed 91 | 待重出 beta17 的 tests 批 |
| COLOR-03 | MEDIUM | 修复（文档侧） | 7 份带日期账本写"AVIF 不写 ICC"旧口径；按 §6 不回改，由 AGENTS §7 更正条目兜底 | 本提交（AGENTS §7 更正 1） |
| COLOR-06 | MEDIUM | 修复 | 门禁用 exiftool 判 ICC、ffprobe 判 CICP，从不数值对撞"ICC 内部原色==容器 CICP" | 待重出 beta17 的 tests 批 |
| COLOR-08 | MEDIUM | 接受/待批 | 存量（a9e2830 前已存在）链末 t=iec61966-2-1 与标签 transfer=bt709 不一致，本批未触及 | 待重出 beta17 的 批 |
| UI-01 | MEDIUM | 修复 | 管道分支（cjxl/cjpegli/djxl）取消返回"已停止/超时"，与 `IsStopped` 精确匹配不一致 ⇒ 漏重排、错收进"仅显示报错" | 待重出 beta17 的 src 批 |
| UI-02 | MEDIUM | 修复 | 优雅停止（StopAfterCurrentQueue）走 break 不 Cancel ⇒ `IsRunning` 恒真，简洁模式误拒启动 | 待重出 beta17 的 src 批 |
| UI-04 | MEDIUM | 修复 | `c5f18e7` oxipng 面板迁到顶层+"PNG 显示/APNG 隐藏"判定，全 tests 树零 UI 断言 | 待重出 beta17 的 tests 批 |
| BUILD-04 | MEDIUM | 修复 | app 包无 oxipng.exe 而默认 pngopt=max，启动横幅 8 工具不点名 oxipng、首启无预警 | 待重出 beta17 的 src/pack 批 |
| GATE-01 | MEDIUM | 修复 | `verify-ui-split-union`7P/4F 根因＝旧参照宿主裸开窗未预置工具路径稳态，P1 拿 0 条 `[detect]` 行、早返回吞 P2-P5（非口径漂移/非产品回归） | 待重出 beta17 的 tests 批（对齐旧单体 bootstrap 或 P1 早返回改记 SKIP） |
| GATE-02 | MEDIUM | 修复 | contract 源码调用位实取 102 vs 登记表锁 101（总量 1006 vs 1005），`928ec89` 加断言未同批抬表 | 待重出 beta17 的 tests 批 |
| MA-02 | MEDIUM | 修复（文档侧） | "AVIF 写不进 ICC"口径反向且已误导本批任务书前提；AGENTS §7 现行口径须更正 | 本提交（AGENTS §7 更正 1） |
| MA-03 | MEDIUM | 修复 | 默认 max 的能力侧（app 缺 exe）/提示侧（横幅不点名）/验证侧（面板零断言）三方口径未对齐（=BUILD-04+UI-04+采集侧） | 待重出 beta17 的 批 |
| COLOR-05 | LOW | 不改 | 兼容性正控通过：坏版本被拒 rc=2、未知参数告警后忽略、WebP 恒 tv/JPEG auto 压回 pc 未漂移 | — |
| FMT-01 | LOW | 修复 | `CopyMetadataAsync`（`-overwrite_original` 直写产物路径）中途失败残留 `*_exiftool_tmp`，未像 ICC 嵌入路径那样 finally 清理 | 待重出 beta17 的 src 批 |
| FMT-02 | LOW | 修复 | `synth_heic_grid_aux_noxmp.heic` 生成器注释称"未登记 URN 负控"，随件实为已登记 Apple URN（负控身份来自门禁现场改写字节） | 待重出 beta17 的 tests 批 |
| UI-03 | LOW | 修复 | `Start()` 重启旧 CTS 只 Cancel 不 Dispose（`RebuildSemaphore` 有 Dispose，此处无） | 待重出 beta17 的 src 批 |
| UI-05 | LOW | 修复 | 用户点停止后日志连出"队列已停止"与"队列已完成"两行矛盾文案（无原因区分位） | 待重出 beta17 的 src 批 |
| UI-06 | LOW | 修复 | 旧代位深探测只丢结果不取消在跑的 ffprobe；回灌 Post 无 try/catch | 待重出 beta17 的 src 批 |
| BUILD-01 | LOW | 修复 | `pack.ps1` 全档模式裸调 `robocopy`（无全路径），PATH 不含 System32 的会话在 Step 2 中止 | 待重出 beta17 的 pack 批 |
| BUILD-02 | LOW | 修复（文档侧） | app/full 两包 exe sha256 仅 6B 差（bundle header nonce），AGENTS §4"用 dll 对账"对 AOT 单包不适用，须写清口径 | 待重出 beta17 的 pack 批（写入口径） |
| BUILD-03 | LOW | 接受 | 5 条构建警告（CS8604/CS8602×2/CA1416/IL3002）全存量，本批重写零新增、无 IL2026/IL2075 | 后续单独清理 |
| BUILD-05 | LOW | 不改 | THIRD-PARTY-NOTICES 版本（oxipng 10.2.1/avifenc 1.4.2/cjxl v0.12.0）与实际二进制对得上，合规无害 | — |
| BUILD-06 | LOW | 不改 | ISA/优化偏好两侧对撞一致（均 `x86-64-v3`/Speed/win-x64） | — |
| BUILD-07 | LOW | 修复（文档侧） | `FFMPEGGUI_FFMPEG_DIR` 斜杠归一化已在基线 `AppSettingsService.Norm()`=GetFullPath，§7"未了结"应划掉 | 本提交（AGENTS §7 更正 3） |
| BUILD-08 | LOW | 不改+待办 | AOT 下 CPU/GPU 检测不失真（CPUID 直读、反射被守住）——否定结论成立；建议补 headless `--diagnose` 开关使可被门禁锁定 | 待办（不动作） |
| GATE-03 | LOW | 修复（文档侧） | `docs/INDEX.md` 漏登 `review-2026-11-15/` 三篇（14/16 已登） | 本提交（INDEX.md） |
| GATE-04 | LOW | 接受 | CHANGELOG trace 缺口（beta16 微基准/beta17 全量 5 warnings/beta15 contract 164/无包哈希），CHANGELOG 冻结至 release 阶段补读数路径 | 待 release 批 |
| GATE-05 | LOW | 接受 | runner 被残留死锁（已死 pid）+ STALE mtime 闸挡住的环境摩擦，非代码缺陷 | 不动作（可后续加死锁 reap） |
| GATE-06 | LOW | 修复（文档侧） | AGENTS §7"473/ui-color=89"是 2026-11-13 快照，现值 487/103；自动生成的 module-index 段与账本一致 | 本提交（AGENTS §7 更正 2） |
| MA-04 | LOW | 修复（重构） | "carry"在 engine/legacy/直连三处不共用单一真值（COLOR-01+COLOR-07 的共同结构成因），建议抽一处判定 | 待重出 beta17 的 重构批 |
| COLOR-04 | INFO | 不改 | 正控通过：PNG/AVIF 双写时 ICC 内部 cicp 与容器 cICP/nclx 逐值一致，"播放器行为分叉"CRITICAL 候选不成立 | — |

## 本提交（docs 更正批）实际收口

| finding | 落点 |
|---|---|
| COLOR-03 / MA-02 | `AGENTS.md` §7 更正 1（AVIF 自 `928ec89` 起经 avifenc 双写 ICC+CICP；旧口径仅适用 webm；7 份带日期账本清单；实测锚） |
| GATE-06 | `AGENTS.md` §7 更正 2（UI 三把尺现值 84+59+103+169+72=487，473 为 2026-11-13 快照） |
| BUILD-07 | `AGENTS.md` §7 更正 3（斜杠归一化已在 `AppSettingsService.Norm()`，从待办划掉） |
| GATE-03 | `docs/INDEX.md` 补 `review-2026-11-15` 三篇登记；登记本批 `CHANGES_48H_REVIEW_RESULT` 与本文件及 `review-evidence/` 证据目录 |

> 其余 29 条（COLOR-01/07、UI-01/02/04、BUILD-04、GATE-01/02、MA-01/03/04、COLOR-02/06/08、FMT-01/02、UI-03/05/06、BUILD-01/02/03/05/06/08、GATE-04/05、COLOR-04/05）由重出 beta17 的 src/tests/pack 修复批落地，本批不动代码。
