# 外部工具链升级规划 (Toolchain Upgrade Plan)

> 生成日期: 2026-09-01
> 目标: 升级本地自编译依赖、补充 V3/AVX-512 性能优化、固化构建可复现性

---

## 一、升级目标总览

| 库 | 当前版本 | 目标版本 | 关键收益 |
|---|---|---|---|
| libjxl (cjxl/djxl/cjpegli) | v0.11.2 | **v0.12.0** | 渐进无损体积 -30~40% / 编码 2-5× 提速 / clang-cl Windows 构建全面提速 |
| libultrahdr (ultrahdr_app) | v1.4.0+ | **v2.0.2** | 新增 HEIF/HEIC/AVIF 容器 (ISO 21496-1)，统一透明检测 |
| libaom (libaom.dll) | v3.14.1 | **v3.15.0** | AV1 编码 IQ 调优 + 速度/质量平衡优化 |
| libavif (avifenc) | v1.4.2 | 保持 v1.4.2 | 已最新；改为**链接本地 aom 3.15.0** |
| zlib | 2026-06 (commit) | **v1.3.2** | 安全修复 + 性能优化 |
| jxrlib (JXR) | 2026-07-14 | 保持 | 微软已停维护，社区 fork 已最新 |
| LibRaw (dngtool) | 0.22 | 保持 | 已最新 |
| DNG SDK | 1.7.1 | 保持 | 已最新 |

---

## 二、SIMD / 性能优化方案

| 优化项 | 方案 | 预期收益 |
|---|---|---|
| 基线 SIMD | `/arch:AVX2`（沿用） | 已在用 |
| 高端 SIMD | 新增 `-SimdLevel AVX512` → `/arch:AVX512` | aom/jxl/avif 编码再提 10-20% |
| 按需分发 | AVX2 与 AVX512 分目录产物，运行时探测 | 覆盖 SSE2~AVX512 机器 |
| 本地 aom 联动 | libavif `-DAVIF_LOCAL_AOM=ON` | 强制使用刚编译的 aom 3.15.0 |
| PGO（可选，不自动执行） | Clang 两轮 profile 训练 | 热路径再提 5-15% |

> 说明: 本环境为 **VS 2026 + CMake 4.3.1 + NASM 3.02**，无 Clang 工具链，
> PGO 需要 Clang，列为可选不纳入本次自动构建。

---

## 三、执行步骤

1. **环境准备** — 确认 VS 2026 + CMake 4.3.1 + NASM 3.02 + git 2.55 ✅
2. **更新源码** — libultrahdr→2.0.2、libaom→3.15.0、zlib→1.3.2、libjxl→0.12.0
3. **编译顺序**：
   - aom 3.15.0（静态库）
   - libavif（链接本地 aom）
   - libultrahdr 2.0.2（启用 HEIF/AVIF）
   - jxrlib（AVX2）
   - dngtool（LibRaw 0.22 + DNG SDK 1.7.1 + 自带 libjxl 0.8）
   - **libjxl 0.12.0（独立 → cjxl/djxl/cjpegli）**
4. **部署** — 覆盖 `publish/PLAN/` 对应目录
5. **验证** — 版本输出确认

---

## 四、风险与回滚

- libjxl 0.12.0 移除内置 jpegli → 迁移到独立 `google/jpegli`，需确认 cjpegli 产物生成方式
- libultrahdr 2.x API 有破坏性变更 → 需回归 Ultra HDR 编码链路
- 回滚：保留 `tools/src/` 旧 git 提交，`git checkout` 旧 tag 即可回退

---

## 五、性能优化分阶段落地 (2026-09-02~03)

> 原则：每阶段改动后均通过 GainMapTestHost 完整回归 + GUI/NativeAOT 启动验证，
> 确保"软件可用性不受影响"。所有新增能力均为向后兼容的增量 API，不改动现有串行调用。

### Phase 0 — 基础设施层（✅ 完成）
| 组件 | 文件 | 作用 |
|------|------|------|
| MediaProbeCache | `Services/MediaProbeCache.cs` (新) | ffprobe/dngtool 结果缓存（键=路径+修改时间，24h TTL） |
| BufferPool | `Services/BufferPool.cs` (新) | ArrayPool 封装，池化 byte/float/ushort 大数组，降 Gen2 GC |
| 集成点 | `MediaInfoService.GetMediaInfoAsync` | 媒体信息探测走缓存；`GainMapEncoder` 全流程数组池化 |

### Phase 1 — 编解码加速（✅ 完成）
| 项 | 结论 |
|----|------|
| ExifTool 批量并行 | 新增 `ReadTagsBatchAsync`（`-stay_open` 复用 + 分块并发），不改旧 API |
| Gain Map 池化 | normHdr/sdrLinear/gainMap/raw 缓冲全部池化，单张 60MP 峰值内存显著下降 |
| SimdPixelOps H2/H3/H4 | **保持标量**（实测 AVX2 gather 比标量 MathF 慢，无净收益）；H1 已有 AVX2 |

### 工具链兼容性修复（关键，随升级暴露）
1. **ffmpeg image2 muxer**：新版要求写单图加 `-frames:v 1 -update 1`，否则 `GainMapEncoder` 的 raw→PNG 静默失败。
2. **独立 cjpegli 无 PNG 解码**：libjxl 0.12 移除内置 jpegli 后，独立 google/jpegli 默认 `BUNDLE_LIBPNG=NO`，
   导致 `cjpegli` 无法读中间 PNG。修复：重编加 `-DJPEGLI_BUNDLE_LIBPNG=ON`，已固化到 `build_tools.ps1`。
3. **libavif 链接本地 aom**：改用 `-DAVIF_CODEC_AOM=SYSTEM`（配合 `AVIF_CODEC_AOM_DECODE=OFF`，因 aom 仅编解码器）
   + `-DAVIF_LIBYUV=LOCAL` 后 `libyuv (1924)` 正确启用。

### Phase 2 — 流式管道（✓ 代码库已具备）
- `JxlPipelineService.TryPipeDjxlToCjpegliAsync` / djxl→cjxl：进程 stdout→stdin 直连，免中间文件，已在 QueueProcessor 接线并端到端验证。
- 渐进式 JPEG：`CjpegliProgressiveId`（0=baseline/2=progressive）已在 UI + 预设实现。
- cjpegli 对 PNG-from-stdin 支持有限，ffmpeg→cjpegli 全流式暂不纳入（保留 PNG 中转，风险更低）。

### Phase 3 — 调度（✓ 已具备）
- 队列已有内存感知并发限制（`ClampConcurrencyByMemory` 每任务 200MB）+ 自适应线程分配（`ComputeAdaptiveThreads`）。
- `build_tools.ps1 -SimdLevel AVX512 -Avx512Libs` AVX-512 分发开关已就绪（需目标 CPU 支持）。

### 端到端验证矩阵（✅ 全通过）
| 后端 | 格式 | 结果 |
|------|------|------|
| cjpegli | JPEG | ✅ (cjpegli 管道) |
| cjxl | JXL | ✅ (cjxl 管道) |
| avifenc | AVIF | ✅ |
| ultrahdr/jpegli | Gain Map 闭环 | ✅ GainMapTestHost 86/86 |
| NativeAOT | 整体发布 | ✅ GUI 启动无崩溃 |