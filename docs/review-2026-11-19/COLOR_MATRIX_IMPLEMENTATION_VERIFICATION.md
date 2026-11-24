# 色彩矩阵修复：实施与验证记录

## 本轮已验证的结果

- 产品与 ServiceProbe Release 编译：0 警告、0 错误。产物 SHA-256 前 16 位为 `FfmpegGui.dll=9076E34D0032CB1B`、`ServiceProbe.dll=609B4A796B2FF85F`。
- `pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1 -Probes 'matrix,colormath,plan' -Scripts 'verify-color-token-normalize.ps1,verify-color-out-matrix.ps1,verify-jpg-webp-matrix.ps1'`：退出 0。入口、出口、JPEG/WebP 与 ICC 分别 96 / 136 / 48 通过，0 失败；三个探针分别 15 / 19 / 43 通过。这是 3/104 条定向门禁，不是全量基线。
- `Tests.UiCore.exe ui-preset-roundtrip`：53 通过，0 失败（`Tests.UiCore.dll=C09C33448435915A`；该轮引用产品 `A14BA120FCECD7A7`）。覆盖新预设的显式 `auto`、旧缺字段、保存与 JSON 往返、先切 cjpegli 后恢复 XYB。
- H1 的 BT.709 / BT.601 输入对比：与从原 YUV 独立生成的正确参考约 53.08 dB，交叉矩阵只有约 30.4–30.6 dB。最长边缩到 32×24 的原生 RGB48 与正确顺序参考逐像素一致，错序/交叉不等；JXR 用 JxrDecApp 回读与原输入参考逐像素一致。见 [输入门禁](../../tests/scripts/verify-color-token-normalize.ps1)。
- 名义有限/满幅之外另验证实际样本：JPEG、AVIF、WebP 读回 Y 或 nclx；JPEG/WebP 显式冗余不换采样格式；GIF→AVIF 的 BT.709 与 601 不只标签不同，原生 YUV 帧散列不同。见 [输出矩阵门禁](../../tests/scripts/verify-color-out-matrix.ps1) 和 [JPEG / WebP / ICC 门禁](../../tests/scripts/verify-jpg-webp-matrix.ps1)。
- DNG 队列：用 Adobe DNG SDK 单独输出 linear Rec.2020 TIFF，从该 TIFF 独立构造 sRGB 传递的 Rec.2020 参考。产品与正确参考 86.249 dB；把同一 TIFF 误当 sRGB 的负控仅 11.163 dB。无 `assuming sRGB` 降级。[本次日志](../../tests/output/_tmp/matrix-dng-baa616e42ec94367b700c06fd19c7b2c/product.log)、[正确参考](../../tests/output/_tmp/matrix-dng-baa616e42ec94367b700c06fd19c7b2c/independent.png)。
- AVIF 元数据回退：显式 PLAN 无 exiftool、绝对 avifenc 路径、外部 Display P3 profile；产品退出 0，明确跳过 FFmpeg remux，ExifTool 独立读回产物 ICC Profile Description `smpte432/sRGB`，产物仍含 nclx。[隔离日志](../../tests/output/_tmp/matrix-noexif-ddb1ee1e456a4ca481a8661bbcd6e0ca/product.log)。
- ICC LUT：用无矩阵/TRC 后备的 mft2 A2B 合成 RGB profile。PNG→PNG 携带验证全部像素和 ICC 字节完全相同；Windows CMS 相对色度转换的 3 个 RGB 块与独立 Pillow oracle 全部同值；坏外部 profile 的 JSONL 任务码 91、进程码 1、无产物。

## 独立矩阵网格与终态复验

- 快档 `verify-color-matrix-full.ps1`：25 通过、0 失败；72/72 格独立 RGB 原生码值对拍，48 格 legacy/engine 对照（含 4 个真实的 JXR 契约拒绝），17/17 组 JSONL 拒绝正负控；无 Adobe ICC 的轴未覆盖。[网格输出](../../tests/output/_gate_script_verify-color-matrix-full.ps1.txt#L83-L131)。同轮出口门禁 136/0、JPEG/WebP/ICC 48/0、plan 43/0；DLL 分别是产品 `AFB18E85DA8B3F1D`、ServiceProbe `7EB143C853291144`。
- NUM 直接从原始 RGB 构造独立参考：从标准 xy/D65 求原色矩阵，显式实现分段传递，按原生 8/16 位码值比较。旧网格曾把纯 γ2.4 像素贴上 BT.709 的 ICC，也曾用默认有损 JXL 去比无损误差。改正造图并要求 JXL 无损之后，换回两种旧产物的突变实验仍判红。旧 ICC 缺席负控改为显式制造带 ICC 的正件、剥 ICC 而不改像素的负件。
- `item_completed` 延到编码、后置元数据、矩阵结果、失败文件清理都已收尾，不再在编码器先设置成功状态时提前发终结事件。
- 显式 AVIF 矩阵在后置元数据处理后，以独立 ffprobe 复读主图实际 M 和像素格式，再发表承载成功；复读缺失或与请求相异会拒绝。GIF→AVIF 的门禁另外选择多帧彩色轨道，从其解码所有帧的原生 YUV 样本做 601/709 哈希 A/B，并独立读取轨道矩阵。
- H.264 + MP4 独立重封装负控：容器 `colr` 是 `bt2020nc`，解码帧仍为 `bt709`。队列在像素转换前检查，真实冲突返回 `COLOR_STREAM_FRAME_CONFLICT`、任务码 91、进程码 1、零产物；同标签正控正常。探测限定 MP4/MOV/M4V/MKV/WebM 的首批解码包，不承诺整片逐帧扫描。

## 预设复验

- 扩展 N10e：将保存的 `xyb` 暂置于不支持它的 MJPEG 后端，界面暂显示 auto；手动切到兼容的 cjpegli 后恢复 `xyb`。新旧预设的 auto/缺字段用例仍过。Release 产品 `CA1D36459920C99B`、UI 宿主 `C876466EF6831833`，编译 0 警告 / 0 错误，`ui-preset-roundtrip` 53/0。[UI 日志](../../tests/output/_tmp/matrix-preset-final-035f51bed03d4edc89cafe0069ca1d9f/ui.log)。

## 最终入口与动画回读

- Release 产品 `F76B7445DC7D9FC7`、ServiceProbe `8ADBA5C6456898C5`，编译 0 警告、0 错误。入口门禁 99/0，JPEG/WebP/ICC 48/0，`matrix` / `colormath` / `plan` 探针分别 15/0、19/0、43/0。
- 动画 AVIF→GIF 使用实际选中彩色轨道的布局和范围，而不是主图或 alpha 的探测字段。由同一 BT.709 AVIF 反转 GIF，`bt470bg` 与 `bt709` 两臂均退出 0、均实际读回 10 帧，10 帧 RGB 散列均不同。[BT.601 输入解释日志](../../tests/output/_tmp/matrix-anim-back-8519c768746e41029ff1660d8527f758/bt470bg.txt#L15-L22)、[BT.709 输入解释日志](../../tests/output/_tmp/matrix-anim-back-8519c768746e41029ff1660d8527f758/bt709.txt#L15-L22)。初次受管反转断言未找到 `.gif`，独立产物验证查明目录查找器未包含 `.gif`；修复后，同一对产品 DLL、ServiceProbe DLL 上的受管出口门禁 138/0、进程退出 0。[双向动画及出口汇总](../../tests/output/_gate_script_verify-color-out-matrix.ps1.txt#L117-L167)。

## 不在已证通过范围

- Windows LUT/CMS 只开放已验证的静态 RGB PNG→PNG 路径和具名 SDR 目标。MPE 相对浮点 LUT、GainMap、其它容器/后端或不支持的组合不会走矩阵近似；以拒绝代替假成功。尚无 ICC LUT 的 HDR/GainMap 转换验收。
- 未穷举真实硬件 AV1、动画中后段/跨流的颜色标签冲突、复杂 stream group、JXR YCC/PTM、AVIF aux/tmap/sato 全套重封装等排列。实测无 ExifTool 时已证保留静态 AVIF 的 ICC+nclx，但未用这些复杂派生项逐一重放，也未模拟 ExifTool 可启动但实际写入失败的每种情形。
- 未运行全量 104 条测试，无出包、版本或历史改动。
