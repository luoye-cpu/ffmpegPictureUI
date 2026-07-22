# 🖼️ FFmpegPictureUI — FFmpeg 图片转换器

**v1.5.0** — Cross-platform batch image/animation/video converter built on Avalonia UI.  
基于 Avalonia UI 的跨平台批量图片/动图/视频转换工具，封装 `ffmpeg` + 外部编码器。

[中文](#-核心功能) | [English](#-core-features) | QQ 群：754439779

---

## ✨ 核心功能

| 功能 | 说明 |
|---|---|
| **多语言界面** | 中文 / English 一键切换，右上角按钮即时生效 |
| **多格式支持** | JPEG (含 JPEG LI)、PNG、WebP、AVIF、JPEG XL、TIFF、JPEG XR、APNG、GIF |
| **多编码器后端** | AOM / SVT-AV1 / NVENC / QSV / VAAPI / AMF / cjxl / cjpegli |
| **AVIF 深度优化** | 自适应量化(aq-mode)、CDEF 滤波、帧内块复制、胶片颗粒合成、NVENC AQ 强度/空间自适应、QSV 低功耗、7 档精细预设 |
| **色彩管理** | sRGB/BT.709/BT.2020 PQ/HLG；CICP(H.273)始终启用；4 种 ICC 模式；zscale 烘焙；HDR→SDR 降级 |
| **29 个内置预设** | JPEG LI×3 / JXL×4 / AVIF AOM×4 / SVT×4 / NVENC×3 / QSV×3 / WebP×3 / PNG×2 / TIFF / Ultra HDR / GIF |
| **智能默认值** | 不勾选高级面板即可获得优化输出——所有参数已设高质量默认值 |
| **批量队列** | 拖放添加、并发 1–128、队列完成后停止、失败重试 |
| **简洁模式** | 同窗口极简视图，一键拖放自动编码 |
| **元数据编辑** | ~90 字段 9 大分类，双击文件即编辑 |
| **隐私清理** | 一键删除 GPS/时间/相机/EXIF/XMP |
| **质量分析** | 编码后 SSIM + PSNR，自动检测无损 |
| **GPU 加速** | ANGLE/D3D11 硬件渲染 + NVENC/QSV/VAAPI/AMF 硬件编码 |
| **双色主题** | 深色/浅色一键切换 |
| **CPU 指令集** | 自动检测 AVX2/AVX-512/NEON，优先使用优化二进制 |
| **PLAN 便携包** | 自动识别程序目录下 PLAN/ 组件包，拷贝即用 |
| **Gain Map HDR** | Ultra HDR JPEG 输出，兼容普通查看器 |
| **PNG 高级选项** | 6 种预测模式（含中文场景说明）+ 打印 DPI（可选，默认不设） |

---

## ✨ Core Features

| Feature | Description |
|---|---|
| **Bilingual UI** | 中文 / English one-click toggle |
| **Multi-format** | JPEG (incl. JPEG LI), PNG, WebP, AVIF, JPEG XL, TIFF, JPEG XR, APNG, GIF |
| **Encoder backends** | AOM / SVT-AV1 / NVENC / QSV / VAAPI / AMF / cjxl / cjpegli |
| **AVIF deep tuning** | aq-mode, CDEF, intrabc, film grain, NVENC AQ/spatial-AQ, QSV low-power, 7-level presets |
| **Color management** | sRGB/BT.709/BT.2020 PQ/HLG; CICP always-on; 4 ICC modes; zscale bake; HDR→SDR |
| **29 built-in presets** | JPEG LI×3 / JXL×4 / AVIF AOM×4 / SVT×4 / NVENC×3 / QSV×3 / WebP×3 / PNG×2 / TIFF / Ultra HDR / GIF |
| **Smart defaults** | Optimized output even without advanced panel — all params have high-quality defaults |
| **Batch queue** | Drag & drop, concurrency 1–128, stop-after-queue, retry |
| **Simple mode** | Minimal one-click drag-to-encode overlay |
| **Metadata editor** | ~90 fields in 9 categories, double-click to edit |
| **Privacy cleaning** | Strip GPS, timestamps, camera, EXIF, XMP |
| **Quality analysis** | SSIM + PSNR post-encode, auto lossless detection |
| **GPU acceleration** | ANGLE/D3D11 rendering + NVENC/QSV/VAAPI/AMF encoding |
| **Dual theme** | Dark / Light mode |
| **CPU SIMD** | Auto-detect AVX2/AVX-512/NEON |
| **PLAN portable** | Auto-detect PLAN/ component pack, copy-and-run |
| **Gain Map HDR** | Ultra HDR JPEG with backward compatibility |
| **PNG options** | 6 prediction modes + optional print DPI |

---

## 🔧 外部工具 / External Tools

| 工具 Tool | 用途 Purpose |
|---|---|
| `ffmpeg` + `ffprobe` | 核心编解码 / Core codec |
| `cjxl` / `djxl` / `cjpegli` | JXL 转码 / JPEG-LI 编码 |
| `ultrahdr_app` | Gain Map / Ultra HDR JPEG |
| `JxrEncApp` / `JxrDecApp` | JPEG XR 编解码 |
| `exiftool` | 元数据编辑 / Metadata |
| `dcraw` | RAW 照片预处理 |

---

## 🚀 快速开始 / Quick Start

需要 .NET 10 Runtime：[下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

```bash
git clone https://github.com/luoye-cpu/PLAN-1.git
cd PLAN-1/ffmpegPictureUI
dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release
dotnet run --project src/FfmpegGui/FfmpegGui.csproj
```

或从 [Releases](https://github.com/luoye-cpu/PLAN-1/releases) 下载完整包（含 ffmpeg + 外部工具，解压即用）。

---

## 📝 更新日志 / Changelog

### v1.5.0 (2026-07-23)

**🆕 新增**
- 中英双语界面，右上角一键切换
- AVIF 深度编码选项：AOM (aq-mode/CDEF/intrabc/胶片颗粒)、NVENC (7 档预设/AQ 强度/空间自适应)、QSV (7 档预设/低功耗)、VAAPI/AMF 独立面板
- PNG 预测模式友好名称 + DPI 设置（可选，不影响画质）
- 29 个内置预设（新增 AVIF AOM 极致、SVT 极致/快速批量、NVENC 平衡、QSV 平衡）
- 硬件编码器预设从 3 档扩展到 7 档精细控制

**🔧 优化**
- 不勾选高级面板也能获得高质量输出——所有参数已设智能默认值
- NVENC/QSV 默认使用最高质量预设 (p7/veryslow)
- 外部工具面板改为按编码器类型动态切换独立子面板

**📦 从 v1.4.5 以来的完整更新**
- ICC 色彩管理 v2.0（4 种模式 + zscale 烘焙 + iccgen）
- CICP(H.273) 色彩标记始终启用 + HDR→SDR 自动降级
- 简洁模式（同窗口极简视图，拖放自动编码）
- GPU 硬件加速 UI 渲染 + 硬件编码
- 预设系统 v2.0（内置 29 个 + 二级管理窗口 + 用户 CRUD）
- PLAN 便携包自动识别 + 工具后台并行检测
- 配置和预设便携化存储（零 AppData 依赖）
- 平台抽象层——为 Linux 迁移准备
- CPU 指令集检测重构（AVX10/AMX/SVE 跨架构安全）

<details>
<summary>v1.4.5 及更早</summary>

- **v1.4.5** — Windows 搜索拖放修复、JXL 无损重封装
- **v1.3.0** — UI 卡片重构、双色主题、exiftool 隐私清理
- **v1.2.0** — 批量队列、元数据编辑器、格式筛选、预设 v1.0
- **v1.0.0** — 初始发布

</details>

---

## 📄 License / 许可

GPL-3.0-only. 完整文本含全部依赖第三方许可证见 [LICENSE](../LICENSE)。
