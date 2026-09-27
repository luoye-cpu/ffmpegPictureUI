# 更新日志

本项目所有值得注意的变更都记录在此文件。

> ℹ️ 各版本条目中记录的「探针 mode 数 / 门禁脚本数」为**当次发布时的基线**。
> 这些数量随开发持续增长，**实时权威值**以 `tests/scripts/_run-step3-gates.ps1` 中的
> `$Probes` 与 `$expectedScriptCount` 为准。

[English](CHANGELOG.en.md)

---

## 📝 更新日志

### v1.6.0 (2026-09-28) — 双语完整本地化 + UI 可用性修复 + 全局异常兜底 + 色彩矩阵正确性修复

**🌐 双语本地化补全（i18n）**
- 硬编码中文 **122 → 9** 处（剩余均为动态占位值与语言自称，按设计不迁移）；`<sys:String>` 选项 **67 → 0**
- 新增 `FillComboByLoc` 机制：ComboBox 选项改由 C# 填充，支持语言切换实时刷新
  （⚠ `<sys:String>` 是元素内容，`{ext:Loc}` 在那里不生效，此前整类选项在英文界面下仍是中文）
- 覆盖主窗口 + 6 个独立窗口（ImageDetail / MetadataEditor / PresetManager / FormatFilter / Progress）
- 语言文件 key **223 → 568**，两语言集合始终一致
- 新增结构锁 `_probe-i18n-scan.ps1`（两语言 key 一致性 + XAML 引用完整性），已纳入门禁套件

**🖥️ UI 可用性修复**
- 左侧面板 **276 → 300px**：内容可用宽 ~236 → ~260，消除多处「恰好用满」的临界裁剪
- 修复多处横向 StackPanel + 长文本导致的**静默裁剪**（改为折行 / 拆行）
- `NumericUpDown` 组件级修复：统一字号 13 与 `MinWidth=88`，微调按钮收窄至 18px，把宽度让给数值本身
- 右上角新增 **⚙ 设置菜单**：渲染后端（手动选 GPU）/ 主题 / 语言 / 缓存目录
- 新增 `AppSettings.RenderingMode`（auto / angle / vulkan / software），每种模式都保留软件渲染兜底

**🛡️ 稳定性**
- 新增**全局异常兜底**：订阅 Avalonia `Dispatcher` + `AppDomain` + `TaskScheduler`，
  把 28 处 `async void` 的未处理异常从「进程无声消失」降级为「记录 + 继续」，异常落盘至缓存目录 `crash.log`

**🎨 色彩矩阵正确性修复（2026-09-28）**

- **PQ 传递函数常数修正** — `m1` 由 `2615/16384` 修正为 `2610/16384`（SMPTE ST 2084 标准值）。
  两处实现（`TransferCurve` / `SimdPixelOps`）曾**同时写错**，导致互检式测试必然通过、长期未被发现
- **SMPTE 240M 传递函数修正** — 幂 `0.44 → 0.45`、编码域断点 `0.07611 → 0.0912`（与 zimg / libplacebo 一致）；
  该曲线在 H2 口径黑名单内只走进程内路径，此前无外部参照可校
- **H1 亮度域换算** — 新增编码侧 `EncodeScale`，与解码侧 `LinearScale` 构成完整换算链。修复：
  SDR→PQ **过曝 49×**、PQ↔HLG 亮度差 **10×**、无 tone mapping 的 PQ→SDR 整体 **暗 49×**
- **HLG 目标侧补上 BT.2100 逆向 OOTF** — 48-bit 主循环原先**整段缺失**，float 路径还存在「把归一值当 nits」的错位
- **nits 语义按曲线定义兜底** — PQ/HLG 的「1.0 = 10000 / 1000 nits」不再依赖调用方是否记得传参
- 实测效果：PQ(BT.2020)→sRGB 与 zimg 对拍 **PSNR 7.26dB → 23.20dB**，平均 ΔE **46.5 → 3.30**

**🖼️ GainMap（Ultra HDR / ISO 21496-1）修复**

- **XMP 元数据读取限定增益图命名空间** — 此前按标签后缀匹配，`EXIF:Gamma`（相机 JPEG 常见，典型值 2.2）
  会被误当成增益图 gamma，导致**旧版 Ultra HDR 文件整图增益错乱**
- **底图 SOS 定位失败不再静默产出「假 Ultra HDR」** — 改为宣告失败，不产出自称 Ultra HDR 的普通 JPEG
- **JPEG 段扫描正确处理无长度字段标记**（TEM / RSTn / 填充字节），此前会跳位错乱而找不到 SOS
- **解码健壮性** — 尺寸运算改 `long` 并设单帧像素上限（防整数溢出绕过长度自检）、
  采样索引改按**解码缓冲区**钳位（此前只钳 x1/y1，x0/y0 漏钳）
- **峰值亮度改用 `alternateHdrHeadroom`** — 第三方文件（Adobe 等）不再因 `gainMapMax` 更大而高估峰值

**🧪 测试门禁加固**

- **PQ 绝对锚点**改为 zimg 实测码值（`0.580694` / `0.751812`），容差由 `1/1/3` 收紧到 `0.5/0.5/1.0`，
  使锚点真正能抓住常数级错误（旧锚点码值精度不足 + 容差过松，正是 m1 错误长期存活的原因）
- **修正两处探针绕过生产换算的构造点**（`xcheck` / `hlgwire`）— 此前它们手搓 spec，测的不是真实引擎路径，
  其中一个还把真实缺陷误定性为「已知非缺陷的口径分歧」而永久豁免
- 补充记录：zimg 的 `smpte240m` 与 `bt709` 输出**逐位相同**（均为纯 γ2.4），本仓按 SMPTE 240M-1995 定义式实现，
  与 zimg 形态不同构 —— 已写入代码注释，防止被误「对齐」成 zimg

**✅ 验证**
- 全层网格 **full 360 格全绿**（`PASS=24 FAIL=0`）；`color-strategy` 78/0、`decision-delivery` 84/0、
  `color-wiring` 105/0、`gamut-map` 36/0、`prophoto-faithful` 27/0、`gainmap-managed` 22/0、
  `gainmap-engine` 138/0、`gainmap-isobmff` 52/0
- 探针 `contract` 136/0、`selftest` 36/0、`curve` 44/0、`hlgwire` 3/3；编译 0 警告 0 错误

### v1.5.6 (2026-09-06) — 完整实机测试 + 格式色彩能力约束 + HDR 全链路修复

**🔧 HDR 全链路修复（10 个根因修复）**
- **HDR→TIFF sRGB tonemap 修复** — RGB 原生格式不再跳过 tonemap，TIFF/PNG 16bit HDR→SDR 正确转换
- **iccgen HDR 兼容** — 高位深 YUV 输入前强制 `format=rgb48le`（规避 iccgen 上游 "Not yet implemented" 限制）
- **AVIF 奇数尺寸修复** — zscale 前 `scale=ceil(iw/2)*2:ceil(ih/2)*2` 避免 libzimg 1027 错误
- **GIF→静态目标首帧** — `insertFramesV1` 标记 + `-frames:v 1` 取首帧
- **静态 AVIF→WebP/GIF 两步法** — `IsAnimatedAvifAsync()` 探测动画 AVIF，静态失败回退普通 ffmpeg
- **`-vsync 0` → `-fps_mode passthrough`** — 新版 ffmpeg 兼容
- **RAW 预处理高级色彩注入** — dngtool 线性 TIFF 设置 `UseAdvancedColorParameters=true` + `ColorPrimaries=bt709` + `ColorTrc=linear`
- **DNG HDR→各格式全通** — jpg/png/webp/avif/tiff/jxl 全部通过，色彩元数据正确传递（`rgb48le,linear,bt709`）
- **AnimationLoop 默认值 0→-1** — 防止静态图被当动画处理

**📂 目录结构规范化**
- 根目录散落临时目录清理（`temp_e2e/`、`temp_headless_test/`、`output/`），统一规范到 `tests/output/`
- 一次性诊断脚本（32 个）归档到 `tests/scripts/_archived_diag/`（git 忽略）
- 从 git 索引移除被误跟踪的 40+ 外部工具二进制（`tests/GainMapTestHost/PLAN/`）
- 移除嵌套 git 仓库 gitlink（`tools/src/aom` 等 4 个）
- `.gitignore` 全面重写，覆盖所有二进制/第三方源码/临时产物路径

### v1.5.5 (2026-08-30) — 原生 JXL 转 PNG 管道传输修复

**🐛 管道传输修复（实测驱动）**
- **JXL→PNG/WebP/AVIF/TIFF/GIF 管道 PAM 识别失败** — `djxl` 解码含 alpha 通道的 JXL 时输出 PAM 流，但 ffmpeg 使用 `-f image2pipe` 无法识别 PAM（`Stream #0:0: Video: none, none` / `Could not find codec parameters`）。按流类型改用专用 demuxer：PAM→`pam_pipe`、PPM→`ppm_pipe`，两者均实测 exit=0 正常产出
- **覆盖全部走 `PipeDjxlToFfmpegAsync` 的转换路径** — 无 alpha（PPM）不受影响，含 alpha 的 JXL 全链路修复

**✅ 验证**
- 项目编译通过（0 错误）
- 端到端实测：含 alpha 的 JXL `djxl --output_format=pam` → `ffmpeg -f pam_pipe -i -` → PNG，exit=0，图像正确产出

### v1.5.4 (2026-08-16) — 色彩管线全面修复 + UI 选项生效性审查 + 布局重构

**🎨 色彩管线修复（实测驱动）**
- **色度子采样全位深生效** — 修复 10/12/16-bit 输出恒为 4:2:0 的 Bug（`MapPixFmt` 高位深分支忽略 Chroma），4:4:4/4:2:2 在全部位深下正确映射（libaom 实测 `yuv444p10le`/`yuv422p10le`）
- **PC 范围 TIFF → TV 范围 AVIF 修复** — 输出侧补 `-color_range` 声明，RGB 全范围输入不再被 limited 矩阵压缩（实测 Y 域：pc=10-239 / tv=26-220）
- **TV/PC 范围自动跟随输入** — pix_fmt 推断（RGB/yuvj→pc，yuv limited→tv），输入 TV 范围图片 → 输出 TV，数据零拉伸（PSNR 53dB）
- **AVIF 位深按编码器 clamp** — libaom/NVENC→12-bit，SVT/QSV/AMF→10-bit（防 16-bit 输入编码失败）
- **UI 按编码器过滤选项** — `UpdateChromaBitDepthOptions()`：SVT/QSV/AMF/WebP 仅显示 auto/4:2:0；libaom/NVENC 显示 8/10/12 位深
- **ColorRange 控件按格式隐藏** — 不支持的格式（JPEG/WebP/PNG 等）整组隐藏而非仅禁用，切换格式自动复位 auto

**🔧 选项生效性全面修复（预览与入队行为一致）**
- 新增 TIFF DPI 控件（`TiffDpiBox`，实测 300dpi 正确写入）
- 简洁模式预设补齐字段（cjpegli 后端/PSNR、cjxl 系列、JxlEffort/Modular、WebpCompressionLevel、TiffDpi）
- WebP 无损压缩级别仅无损模式显示

**🎯 默认值与内置预设重做**
- 模型默认值统一：`Chroma=auto`、`StripExifGps=true`、`CjpegliChromaSubsampling=auto`、`CjpegliProgressiveId=2`（渐进实测压缩率更高：体积小 5-38%）
- WebP/TIFF 下拉默认对齐高级面板关闭时的值（picture/lzw）
- 29 个内置预设全面重做：GIF 预设格式修正（原为 JPEG）、SVT/QSV/WebP 色度 444→420（实测仅支持 420）、JXL 色度→auto、极限压缩启用 cjpegli 渐进、PNG 快速存档补无损

**🖥 顶部布局重构**
- FFmpeg 目录 + 输出目录合并一行（Grid 弹性列宽，输出目录 1.4 倍权重）
- 修复 Grid 列错位（输出目录标签曾落入 12px 固定列导致残字/塌陷）
- 窗口默认 1100×850（适配 150% DPI 1080p 屏），右侧区域行比例 2:1:2（队列优先）

**🌍 其他**
- cjpegli 参数适配当前 libjxl 版本（`--fixed_code`/`--noadaptive_quantization`，移除已失效的 `--jpeg_encoder`/`--psnr`）
- 窗口标题版本号自动同步程序集版本（`NormalizeAppTitleVersion`），发版不再遗漏
- UI 测试方法论：UIA 边界框 + 视觉模型（SenseNova 6.8 Flash Lite）交叉验证

### v1.5.3 (2026-08-15) — 原生 PSNR + 目标域质量分析 + SIMD 加速

**📊 .NET 原生 PSNR（替代 ffmpeg psnr filter）**
- 新增 `PsnrCalculator.cs` — 纯 .NET 实现，**无外部依赖**，60MP 大图 **20× 加速**（359ms→17.6ms）
- 自动 dispatch: AVX512BW → AVX2 → SSE2 → 标量回退
- 位深归一化: `MaxValue = (1 << bitsPerSample) - 1`，8/10/16-bit 统一计算
- 多帧语义: `average` = 全局 MSE 聚合，`min`/`max` = 逐帧极值（与 ffmpeg 一致）

**🎯 目标域质量分析（彻底解决跨格式域偏差）**
- RGB 系格式（PNG/TIFF/JXL/APNG/GIF/BMP）→ RGB 域（rgb24/rgb48le）
- YUV 系格式（JPEG/WebP/AVIF/HEIC/JXR）→ YUV 域（yuv444p/yuv444p16le）
- 与 ffmpeg psnr filter **逐位一致（0.0000dB，D37 断言）**
- `scale=out_range=pc` 统一 full range（消除 limited vs full 值域错乱）
- UI 质量分析结果标注 `(RGB)` / `(YUV)` 域

**⚡ GainMap SIMD 加速**
- 新增 `SimdPixelOps.cs` — 4 个热点，实测数据驱动取舍
- `FloatToSrgb8` AVX2 **2.5×**（0.15→0.06ms，1024 项 LUT + gather 插值）
- 已集成 GainMapEncoder（ReinhardToSdr / WriteBgra8PngAsync 批量转换 / ComputeGainMap 灰度）
- 已集成 GainMapDecoder（SrgbToLinearRgba）

### v1.5.2 (2026-08-13) — 管线修复与元数据增强

**🔧 管线修复**
- **修复 ffprobe 色彩字段错位解析** — `-show_entries` 逗号分隔仅最后一个字段生效，实际输出 `pix_fmt,color_space,color_primaries,color_transfer`（无 bits_per_raw_sample），此前 primaries/transfer 一直被交换。修正解析 + 位深解析器重写（yuvj420p→8、yuv420p10le→10、rgb48le→16 全覆盖）
- **修复 RAW 预处理临时目录泄漏** — `raw_{GUID}` 目录任务完成后统一清理
- **JXR 命令显示过期** — 显示实际 PPM/PAM 管道命令

**🖼 编码增强**
- **JPEG XL 无损重封装开关** — 高级选项可独立关闭（关闭时显式 `--lossless_jpeg=0` 重新编码）
- **光子噪声自动 ISO** — 可选从每张输入照片 EXIF 自动读取 ISO（每图独立，比固定值准确）

**🏗 平台**
- **NativeAOT 打包** — 2 版本发布（单文件版 / 完整版含 PLAN），全部 NativeAOT 编译，无需 .NET Runtime
- **PLAN ffmpeg 目录模糊匹配** — 目录名包含 "ffmpeg-full" 即自动识别（如 ffmpeg-full-2026.7.24）

---

### v1.5.1 (2026-07-27) — 色彩管线修复

**🎨 色彩管理修复**
- **修复 SDR→HDR 像素未转换** — 16-bit TIFF 等无元数据输入手动指定 BT.2020 PQ/HLG 时，输出仅有 HDR 标签但像素未做电光转换（画面偏暗）。`BuildColorArgsSplit` 简化模式改为返回实际输入色彩，新增通用目标色域 zscale 转换逻辑
- **修复 JXL→PNG 卡死** — 管道模式下 `inputPath="-"` 被传给 ffprobe 探测函数导致无限阻塞。三个探测函数添加管道守卫
- **HDR→SDR 排除判断** — 简化模式 zscale 转换排除 HDR→SDR 场景（交给 tonemap 处理，避免高光裁剪）

---

### v1.5.0 (2026-07-23) — 正式版

**🎛 编码器与质量**
- **AVIF 深度优化** — libaom 新增 aq-mode（自适应量化，Variance/Complexity）、CDEF 方向增强滤波、帧内块复制(intrabc)、胶片颗粒合成(denoise 0-50)；NVENC 新增 aq-strength(0-15)+空间自适应量化(spatial-aq)；QSV/VAAPI 新增低功耗模式(low_power)
- **硬件编码器 7 档精细预设** — NVENC(p1~p7) / QSV(veryfast~veryslow) / VAAPI(compression_level 1~7) 独立面板，默认最高质量(p7/veryslow/7)
- **29 个内置预设** — 覆盖 AOM×4 / SVT×4 / NVENC×3 / QSV×3 / JPEG LI×3 / JXL×4 / WebP×3 / PNG×2 / TIFF / Ultra HDR / GIF，全部使用最新参数
- **智能默认值** — 不勾选"高级编码选项"即可获得优化输出，所有参数内置高质量默认（cpu-used=4, still-picture=1, aq-mode=variance, huffman=optimal...）
- **PNG 增强** — 6 种预测模式带中文场景说明 + DPI 打印分辨率（默认不设，纯可选）

**🌐 界面与体验**
- **双语界面** — 中文 / English 一键切换，右上角按钮即时生效，JSON 资源文件
- **简洁模式** — 同窗口极简覆盖视图，拖放文件直接入队，自动编码开关
- **GPU UI 加速** — Windows ANGLE/D3D11 渲染，GPU/CPU 按钮可切换
- **便携化部署** — 配置与预设存于 exe 同目录，零 %AppData% 依赖，拷贝即用

**🎨 色彩管理**
- **ICC 系统重写** — 4 种新模式：①无ICC(CICP) ②携带ICC ③烘焙+嵌入 ④仅烘焙；zscale 像素烘焙；iccgen 自动生成标准 ICC
- **CICP 始终启用** — H.273 色彩标记在所有模式生效；非 CICP 格式非 sRGB 时自动嵌入 ICC
- **HDR→SDR 自动降级** — 输出格式不支持 HDR 时自动色调映射；双重转换冲突检测与锁定
- **色彩空间快速选择** — sRGB / BT.709 / BT.2020 PQ / BT.2020 HLG；BT.2020 自动 ≥10-bit

**🔧 工具与架构**
- **工具面板重构** — 3 列水平布局（JXL库|exiftool|artifacts）；紧凑状态栏后台检测完自动显示 ✅/❌；PLAN 便携包自动识别
- **GPU 编码器检测** — 启动时自动检测 NVENC/QSV/AMF 可用性并逐编码器运行时验证
- **检测模块重写** — 全异步后台管线，真实超时保护，增量日志
- **打包优化** — 精简调试符号 ~100MB；Resources/Locales/ 多语言资源自动包含

**🐛 修复与优化**
- 消除重复 settings.json I/O；7 个外部工具并行检测
- Windows 搜索拖放路径正确解析
- 输出类型 WinExe，无 CMD 窗口

<details>
<summary>v1.5.0 Beta 版本详情</summary>

### v1.5.0 BETA3 (2026-07-15)
- **色彩空间重构** — 简化选择器：sRGB / BT.709 / BT.2020 PQ / BT.2020 HLG；移除 BT.601；选择后自动填充 primaries/trc/matrix；BT.2020 根据源位深自动 ≥10-bit
- **ICC 系统重写** — 4 种新模式（无ICC / 携带ICC / 烘焙+嵌入 / 仅烘焙）；zscale 像素烘焙；iccgen 自动生成标准 ICC；烘焙目标跟随色彩空间选择
- **CICP 始终启用** — H.273 标记在所有模式生效；非 CICP 格式非 sRGB 时自动嵌入 ICC
- **HDR→SDR 降级** — 输出格式不支持 HDR 时自动 zscale+tonemap；位深比较警告
- **双重转换锁定** — ICC 烘焙时锁定手动色彩参数，6 种冲突场景检测
- **打包优化** — 移除原生 .pdb 调试符号 ~100MB；55 项管线测试矩阵全部通过

### v1.5.0 BETA2 (2026-07-14)
- **简洁模式** — 同窗口极简覆盖视图；拖放直接入队；自动编码开关；预设同步主界面
- **GPU 编码器检测** — 启动时自动检测 QSV/NVENC/AMF；逐编码器运行时验证；✅⚡⚠️❌ 彩色状态提示
- **GPU UI 加速** — ANGLE/D3D11 渲染（Windows）；GPU/CPU 一键切换；`--no-gpu` 命令行回退
- **启动优化** — 消除重复 I/O；7 个外部工具 Task.WhenAll 并行检测；GPU 验证延迟到后台
- **便携化部署** — 配置/预设存于 exe 同目录 `presets/`；零 %AppData% 依赖；拷贝即用

### v1.5.0 BETA (2026-07-14)
- **ICC 色彩管理 v1** — 外部 .icc/.icm 加载；exiftool/iccgen 嵌入；zscale 像素烘焙；sRGB~Rec.2100 完整色彩空间映射
- **预设系统 v2.0** — 24 内置预设 + 二级管理窗口 + 用户 JSON 预设 CRUD
- **工具面板重构** — 3 列水平布局；紧凑状态栏后台检测完自动显示 ✅/❌；PLAN 便携包自动识别
- **检测模块重写** — 全异步 3 阶段后台管线；每步 8s 超时；增量 Dispatcher 日志
- **AVIF 编码器面板** — AOM/SVT/NVENC/QSV/AMF 各自独立选项，编码器切换时动态切换面板
- **动图与 RAW** — 视频转动图时长限制；dngtool RAW 解码自动检测；扩展 RAW 格式支持

</details>

<details>
<summary>v1.4.5 及更早</summary>

- **v1.4.5** — Windows 搜索结果拖放路径正确解析（Shell 命名空间）；JXL 无损 JPEG 重封装（直接复制 DCT 系数，5-10× 速度）；Linux ARM 迁移技术分析完成
- **v1.3.0** — UI 卡片化重构（圆角阴影卡片容器）；深色/浅色双主题一键切换；GridSplitter 弹性三区布局；ExifTool 隐私清理（GPS/时间/相机/EXIF/XMP 选择性删除）
- **v1.2.0** — 批量队列引擎（ConcurrentQueue + 并发 1-128 + 失败重试）；元数据编辑器（~90 字段 9 大分类 + 双击编辑）；格式筛选窗口；预设系统 v1.0；CPU SIMD 指令集自动检测
- **v1.0.0** — 初始发布：JPEG/PNG/WebP/AVIF/JXL/TIFF 多格式编码；质量滑块；外部编码器集成（ffmpeg/cjxl/cjpegli）；命令行构建与预览

</details>
