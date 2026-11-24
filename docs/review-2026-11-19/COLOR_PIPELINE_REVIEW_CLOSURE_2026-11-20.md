# 色彩管线复审与修复收口 · 2026-11-20 批（2026-11-21 收尾）

## 结论与范围

本轮重新审查当前未提交更改的来源解释、像素变换、编码器出口、ICC/CICP 后置、任务终态、GUI/CLI 预设及判据。既往某些“ICC 嵌入失败”并不表示交付件缺少 ICC；不能据这些日志一律拒绝可解码输入。已将必需描述、原字节携带和兼容性增强分开处理，并修复独立审查确认的错色路径。本次确认的修复已按下列分批读数验证；结论限于已验范围，不等于 104 条全量基线或所有排列的无缺陷声明。

没有运行 104 条全量基线，没有出包、改版本、提交或改写历史。本记录是更正与新增证据，不回改此前的时间点记录。

## ICC 为什么会“失败”

1. **重复操作造成假失败。** AVIF 已由 avifenc 编码写入 ICC，旧引擎 Map 后置仍交给 ExifTool 再写；后面的 AVIF 跳过分支来不及阻止前一次。普通 Map 还有两段标准 ICC 后置。现统一到 [QueueProcessor 的交付检查](../../src/FfmpegGui/Services/QueueProcessor.cs)，已交付的 profile 先读回，不重复写 AVIF/JXL。
2. **不同原因被折成 null。** [IccProfileService](../../src/FfmpegGui/Services/IccProfileService.cs) 的历史路径把不可表达的 HDR/TRC、缓存落盘失败、生成失败及外部提取失败合并为“无法生成”。当前命名/标准 RGB ICC 优先使用托管构建；命名缓存验证 v4.4/ID 与实际色彩模型，不能只因文件存在而复用。
3. **0 files updated 不是可靠结论。** 它可能是内容已经相同，也可能是所选写入工具不支持该容器。现在先读回实际字节；原样携带要求与原 profile 一致，生成目标要求与经验证的目标一致。
4. **真实输出义务不同。** JPEG/WebP 广色域、TIFF 的唯一描述缺失不能报完整成功；准确 cICP/nclx 或容器约定的隐式 sRGB 已独立证实时，失败的冗余 ICC 可以降为警告，保留可用文件，但不能宣称已嵌入。坏内嵌输入 ICC 的告警继续与此分开。

[原生 ICC 读取器](../../src/FfmpegGui/Services/IccContainerReader.cs) 对 JPEG APP2 分段、WebP ICCP、经典 TIFF/BigTIFF 做有界读取，使已存在的 profile 不因缺少外部写入工具而“读不见”。AVIF 以主项关联的 colr 读取；需要核验 JXL ICC 时使用 djxl 原 ICC 输出，带超时、取消和临时件清理。JXL 标准色彩也可能由编码器改用等价结构化字段表达，不能把 `--icc` 参数本身当作原始 profile 已交付的证据。

## 实际修复

- **窄范围 PNG：** 主解码、H1、最长边预缩放及专用管道在第一次转换/缩放前消费合法 RGB cICP 的窄范围，再写 full 标记。96×72→48×36 回归独立读三块中心，实际 235/16 端点变为 255/0；不把 range=0 当坏标签。
- **来源优先级：** 有效外部 ICC 覆盖内嵌 LUT 或具名源；保真捷径读取最终选定的来源，不复活已被覆盖的 ProPhoto 描述。明确 CP/CT 目标不再被 faithful 快捷分支吞掉。无可靠 P/T 的降级计划仍保留外部编码器及目标位深。
- **不能执行的转换：** 可解码输入保持原容错政策；用户明确选择 legacy、又要求该执行器无法命名的自定义原色矩阵转换时，以独立合同说明需要 H1/CMS。标准 ICC 的曲线系数版本或定点舍入差异不作为输入拒绝条件。
- **专用出口：** cjpegli 的显示与执行共用一次 FFmpeg/ICC 决策；源 alpha 来自同一次完整探测，临时 ICC 在 finally 清理。JXR 必需描述用统一交付校验。GainMap 广色域底图必须实读匹配 profile；sRGB 只有确认为无 ICC 的情况下才能按容器惯例降级。
- **XYB：** cjpegli 的专用 A2B LUT ICC 不再被 PreserveAll 或源 RGB ICC 回带覆盖。原生 XYB profile 实读为 v4.4.0，ICC.1 ID 独立重算相符；不将它冒充标准 RGB 矩阵 profile。
- **命名输出 profile：** 不能只因文件名是 `prophoto-rgb.icc` 就相信其语义。用户命名目录和自产缓存均核实矩阵/白点与曲线；放错的标准 profile 被忽略，改用匹配的构建结果，不拒绝整个输入。
- **GUI/CLI：** 简洁预设应用只拥有一次最终刷新；同代请求和跨格式请求分别防过期提交，用户后续选择取消旧 pending。质量显示刷新不再把 77 反算成 76。最终编码器确定后重放色度和位深。内置 AOM/SVT/NVENC/QSV 预设记录具体 EncoderName，旧 backend-only 预设保留兼容当前选择。CLI 显式轴覆盖预设，未指定轴不再被默认值覆盖。

## 定向证据

色彩处理冻结构建：产品 `FFE5D5B0F450A127`，ServiceProbe `CB176557F535BF8A`，UiCore `5A2138513DEEBAFB`，UiCli `89A07C67260893D1`，均为 DLL SHA-256 前 16。相关 Release 构建 0 警告、0 错误。见 [构建/身份](../../tests/output/_tmp/color-final-7de6424bf3d64f308b258f9b2e153a6c/identity.txt) 与 [构建日志](../../tests/output/_tmp/color-final-7de6424bf3d64f308b258f9b2e153a6c/build.log)。

| 定向检查 | 实际结果 | 证据 |
|---|---:|---|
| plan / iccname / wire / decision | 50/0 · 33/0 · 106/0 · 7/0 | [同轮服务与图片出口](../../tests/output/_tmp/color-final-7de6424bf3d64f308b258f9b2e153a6c/gates.log) |
| ProPhoto faithful | 42/0 | [同轮门禁](../../tests/output/_tmp/color-final-7de6424bf3d64f308b258f9b2e153a6c/gates.log) |
| JPEG/WebP/PNG、窄范围预缩放、XYB 元数据保护 | 62/0 | [同轮门禁](../../tests/output/_tmp/color-final-7de6424bf3d64f308b258f9b2e153a6c/gates.log) |
| AVIF/JPEG 输出矩阵、ICC 数值、ID、独立负控 | 153/0 | [同轮门禁](../../tests/output/_tmp/color-final-7de6424bf3d64f308b258f9b2e153a6c/gates.log) |
| matrix / colormath | 15/0 · 19/0 | [GUI/GainMap 轮](../../tests/output/_tmp/color-final-gui-gm-68f673eb9c7c4daf804ebd621fe29ff7/gates.log) |
| 六格式 P3/sRGB 语义 | 6/0，6/6 格到场锁 | [GUI/GainMap 轮](../../tests/output/_tmp/color-final-gui-gm-68f673eb9c7c4daf804ebd621fe29ff7/gates.log) |
| GainMap 完整指定语料与矩阵/TRC/ID 负控 | 225/0 | [GUI/GainMap 轮](../../tests/output/_tmp/color-final-gui-gm-68f673eb9c7c4daf804ebd621fe29ff7/gates.log) |
| GUI 综合门禁 | 505/0（含反向自检按预期转红） | [GUI/GainMap 轮](../../tests/output/_tmp/color-final-gui-gm-68f673eb9c7c4daf804ebd621fe29ff7/gates.log) |

无 ExifTool 的真实产品 CLI：sRGB JPEG 为进程/任务 0/0、有产物；需要 ICC 的 P3 JPEG 为进程/任务 1/91、无新产物；avifenc P3 AVIF 为 0/0，独立 ExifTool 读回 v4.4 ICC 及 P3/sRGB/identity/full nclx。见 [三臂结果](../../tests/output/_tmp/icc-noexif-final-2ea14f59d9ca467e9e9cb90d5c6cff78/summary.json) 与 [详细降级日志](../../tests/output/_tmp/icc-noexif-final-2ea14f59d9ca467e9e9cb90d5c6cff78/debug/icc-diagnostics.json)。随后只将两条新增 ICC 告警的 ASCII `WARN:` 加上现有日志分级器识别的告警标记，未更改颜色/编码控制流。该日志修订的产品为 `AA9C0F8A7639D19D`；ServiceProbe、UiCore、UiCli 的自身 DLL 指纹不变。默认 Info JSONL 实跑得到恰好一条 `ICC_NOT_DELIVERED`、级别 warn、进程/任务 0/0，并保留 JPEG；见 [Info 级别证据](../../tests/output/_tmp/icc-info-final-0933022b61eb452baa526332a74c5045/summary.json)。这个日志可见性修订之后的构建和定向回归单独留在 [最终日志轮](../../tests/output/_tmp/color-final-logging-2a0794f1965f47a1b4b0d6719d0fe56a/gates.log) 与 [DLL 身份](../../tests/output/_tmp/color-final-logging-2a0794f1965f47a1b4b0d6719d0fe56a/identity.txt)，不冒用 FFE5 的整组读数。

最终日志构建 `AA9C0F8A7639D19D` 上的受管复验：plan / iccname / wire / decision 为 **50/0、33/0、106/0、7/0**；接线 **162/0**、输入 token/范围/JSONL **107/0**、JPEG/WebP/PNG/XYB **62/0**。对应 [最终日志轮](../../tests/output/_tmp/color-final-logging-2a0794f1965f47a1b4b0d6719d0fe56a/gates.log)。此前 FFE5 构建的 GUI 505/0、GainMap 225/0 和 AVIF/JPEG 153/0 仍按原构建标识保留，最后两处字符串分级修改未改其像素处理逻辑。

## 2026-11-21 的准入再校正

最后加跑 quick 独立网格时，`AA9C0F8A7639D19D` 得到 **24/1**：主网格 72 格与独立 NUM 比较均通过，LEGACY 48 格中的 12 个标准 BT.2020 输入被我新增的严格 `IsPreciselyCicpNameable` 前置条件拒绝。这是新引入的误拒，不能通过将拒绝列为可接受结果来消红。原红轮见 [结构与 quick 轮](../../tests/output/_tmp/color-final-structure-7e0529287e894b24b6382a17c4b18902/gates.log)。

独立解压该源 iCCP，实际 r/g/b TRC 为 para type 3：`g=2.22222900390625, a=0.90966796875, b=0.09033203125, c=0.22222900390625, d=0.08123779296875`，是标准分段曲线的系数版本，不是此前临时判断的 γ2.4。现只针对 legacy 无法命名的源原色矩阵设置路由限制，不再把严格 ICC/CICP 曲线等同性当成输入准入条件。

准入修订后的产品为 `8CD5BD2E468ABB84`，ServiceProbe 仍为 `CB176557F535BF8A`；Release 构建 0 警告、0 错误。plan / iccname / wire / decision 已再过 **50/0、33/0、106/0、7/0**；整条 quick 与图片门禁的最终读数见 [准入复验轮](../../tests/output/_tmp/color-admission-final-32814e27de154372a00343764e61d863/gates.log) 与 [最终身份](../../tests/output/_tmp/color-admission-final-32814e27de154372a00343764e61d863/identity.txt)。最终整条 quick **25/0**、JPEG/WebP/PNG/XYB **62/0**。主网格 **72/72** 做过独立矩阵/TRC/原生 RGB 码值比较，红 0；往返 **9 组**红 0；LEGACY 层实跑 **48 格**、违规 0，其中 4 格按稳定原因码干净拒绝。LEGACY 中 13 个引擎格因 zimg 参考的 BT.709 曲线口径不适用而没有做该项方向性像素比较，仍有标签和字节对照约束；不能将其说成 48 格都经过绝对数值对拍。Adobe 轴因本机缺少对应 profile 记 SKIP，未缩入标准 72 格分母。详见 [最终逐项网格](../../tests/output/_tmp/color-admission-final-32814e27de154372a00343764e61d863/verify-color-matrix-full.ps1.txt)。测试结构另外通过逐字拆分 22/0、镜像/条数锁 91/0、模块账本 12/0，见 [结构轮日志](../../tests/output/_tmp/color-final-structure-7e0529287e894b24b6382a17c4b18902/gates.log)。

## 判据与剩余边界

[出口门禁](../../tests/scripts/verify-color-out-matrix.ps1) 对实际提取的 ICC 验证矩阵/白点/TRC、版本/ID/cicp，修改真实矩阵或 TRC 后重算正确 ID、保留正确 cicp 的反例仍必须判负。[GainMap 门禁](../../tests/scripts/verify-gainmap-engine.ps1) 独立核 sRGB D50 与稠密曲线；不要求两个后端的 ICC 序列化字节一致。[token 门禁](../../tests/scripts/verify-color-token-normalize.ps1) 联合进程码、JSONL 唯一终态与产物核对，不用旧文件证明本轮成功。

范围仍有明确限制：Windows LUT/CMS 的准确变换只承诺已验证的静态 RGB PNG 同位深路径；复杂 MPE、任意 LUT→其它容器、所有真实硬件编码器与复杂派生项排列均未穷举。视频测试仍仅证明 MP4/H.264 抽帧冲突处理，不计入图片格式覆盖。GUI 测试走真实控件事件，但没有人为控制全部异步完成乱序，也不等同于实际点击入队后对所有格式逐项编码。前次复审中出现的性能计时红项本轮未做成对对照复测，未提高性能阈值；104 条全量基线本轮未运行。不得以这些定向结果宣布无条件发布就绪。
