using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FfmpegGui.Models;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 预设管理服务：管理用户预设的存储/读取/删除，
    /// 以及开发者内置预设的定义。
    /// 用户预设存储在 exe 同目录下的 presets/ 子目录中（便携模式）。
    /// </summary>
    public static class PresetManagerService
    {
        private static readonly string PresetsDir = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath!) ?? ".",
            "presets");

        // ── 内置预设定义 ──
        // 说明：
        //  - JPEG 预设统一使用 cjpegli (JPEG LI) 后端
        //  - AVIF 提供 4 套编码器: AOM (cpu-used+aq-mode) / SVT (preset+tune) / NVENC (preset+aq+spatial-aq) / QSV (preset+low_power)
        //  - PNG 支持 DPI 和预测模式
        //  - Gain Map (Ultra HDR) 已考虑
        //  - 共 29 个内置预设

        private static readonly List<PresetEntry> BuiltInPresets = new()
        {
            // ═══════════════════════════════════════════
            //  JPEG / JPEG LI (cjpegli)
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "📸 JPEG LI — 高质量 (d=2.0, 4:4:4)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "JPEG", Quality = 92, Chroma = "4:4:4",
                    ColorSpace = "auto", BitDepth = "auto",
                    EncoderBackend = "Cjpegli",
                    JpegHuffman = "optimal",
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },
            new PresetEntry
            {
                Name = "📸 JPEG LI — 平衡 (d=4.0, 4:2:0)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "JPEG", Quality = 84, Chroma = "4:2:0",
                    ColorSpace = "auto", BitDepth = "auto",
                    EncoderBackend = "Cjpegli",
                    JpegHuffman = "optimal",
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 32
                }
            },
            new PresetEntry
            {
                Name = "📸 JPEG LI — 极限压缩 (d=6.0, 渐进)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "JPEG", Quality = 76, Chroma = "4:2:0",
                    ColorSpace = "auto", BitDepth = "auto",
                    EncoderBackend = "Cjpegli",
                    JpegHuffman = "optimal", CjpegliProgressiveId = 2, // 2026-08-16 实测渐进压缩率更高
                    MetadataMode = "StripAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 64
                }
            },

            // ═══════════════════════════════════════════
            //  JPEG XL
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "✨ JPEG XL — 视觉无损 (d=1.0, e=7)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "JPEG XL", Quality = 93, Chroma = "auto",
                    BitDepth = "auto", ColorSpace = "auto",
                    JxlEffort = 7, JxlModular = false,
                    JxlPreserveUltrahdr = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },
            new PresetEntry
            {
                Name = "✨ JPEG XL — 平衡 (d=3.0, e=5)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "JPEG XL", Quality = 80, Chroma = "auto",
                    BitDepth = "auto", ColorSpace = "auto",
                    JxlEffort = 5, JxlModular = false,
                    JxlPreserveUltrahdr = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 32
                }
            },
            new PresetEntry
            {
                Name = "✨ JPEG XL — 无损 (d=0, Modular)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "JPEG XL", Quality = 100, Chroma = "auto",
                    BitDepth = "auto", ColorSpace = "auto",
                    Lossless = true,
                    JxlEffort = 9, JxlModular = true,
                    JxlPreserveUltrahdr = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 1, MaxQueueSize = 4
                }
            },
            new PresetEntry
            {
                Name = "⚡ JPEG → JXL 极速无损重封装 (不解码)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "JPEG XL", Quality = 100, Chroma = "auto",
                    BitDepth = "auto", ColorSpace = "auto",
                    JxlLosslessJpeg = true,
                    JxlEffort = 7,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 64
                }
            },

            // ═══════════════════════════════════════════
            //  AVIF — AOM (libaom-av1)
            //  说明: cpu-used 越低质量越好但越慢; aq-mode=variance 适合照片;
            //        CDEF/Intrabc 保持默认开启; 胶片颗粒默认关闭
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "🚀 AVIF AOM — 极致质量 (cpu=2, CRF 15, 4:4:4 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 92, Chroma = "4:4:4",
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifCpuUsed = 2, AvifStillPicture = true, AvifRowMt = true,
                    AvifTune = "IQ (图像优化)",
                    AvifAqMode = "variance",
                    AvifEnableCdef = true, AvifEnableIntrabc = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 1, MaxQueueSize = 8
                }
            },
            new PresetEntry
            {
                Name = "🚀 AVIF AOM — 高质量 (cpu=4, CRF 22, 4:4:4 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 88, Chroma = "4:4:4",
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifCpuUsed = 4, AvifStillPicture = true, AvifRowMt = true,
                    AvifTune = "IQ (图像优化)",
                    AvifAqMode = "variance",
                    AvifEnableCdef = true, AvifEnableIntrabc = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },
            new PresetEntry
            {
                Name = "🚀 AVIF AOM — 平衡 (cpu=5, CRF 30, 4:2:0 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 80, Chroma = "4:2:0",
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifCpuUsed = 5, AvifStillPicture = true, AvifRowMt = true,
                    AvifAqMode = "variance",
                    AvifEnableCdef = true, AvifEnableIntrabc = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 32
                }
            },
            new PresetEntry
            {
                Name = "🚀 AVIF AOM — 无损 (CRF 0, 4:4:4, cpu=1)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 100, Chroma = "4:4:4",
                    BitDepth = "auto", ColorSpace = "auto",
                    Lossless = true,
                    EncoderBackend = "Ffmpeg",
                    AvifCpuUsed = 1, AvifStillPicture = true, AvifRowMt = false,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 1, MaxQueueSize = 4
                }
            },

            // ═══════════════════════════════════════════
            //  AVIF — SVT-AV1 (libsvtav1)
            //  说明: preset 越低质量越好; VMAF tune 在人眼感知上表现好
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "🚀 AVIF SVT — 极致质量 (preset 2, CRF 18, 4:2:0 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 92, Chroma = "4:2:0", // libsvtav1 仅支持 4:2:0（2026-08-16 实测）
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifSvtPreset = 2, AvifSvtTune = "VMAF (主观)",
                    AvifStillPicture = true, AvifRowMt = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },
            new PresetEntry
            {
                Name = "🚀 AVIF SVT — 高质量 (preset 4, CRF 24, 4:2:0 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 88, Chroma = "4:2:0", // libsvtav1 仅支持 4:2:0
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifSvtPreset = 4, AvifSvtTune = "VMAF (主观)",
                    AvifStillPicture = true, AvifRowMt = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 3, MaxQueueSize = 24
                }
            },
            new PresetEntry
            {
                Name = "🚀 AVIF SVT — 平衡 (preset 7, CRF 32, 4:2:0 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 80, Chroma = "4:2:0",
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifSvtPreset = 7, AvifSvtTune = "VMAF (主观)",
                    AvifStillPicture = true, AvifRowMt = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 32
                }
            },
            new PresetEntry
            {
                Name = "🚀 AVIF SVT — 快速批量 (preset 10, CRF 38, 4:2:0 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 73, Chroma = "4:2:0",
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifSvtPreset = 10, AvifSvtTune = "VMAF (主观)",
                    AvifStillPicture = true, AvifRowMt = true,
                    MetadataMode = "StripAll", AutoThreads = true,
                    Concurrency = 6, MaxQueueSize = 64
                }
            },

            // ═══════════════════════════════════════════
            //  AVIF — NVIDIA NVENC (av1_nvenc)
            //  说明: p7 最好最慢; aq-strength=8 默认, 越高平坦区域越激进
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "🚀 AVIF NVENC — 高质量 (p7, AQ=8, 4:4:4 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 90, Chroma = "4:4:4",
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifHwPresetLevel = 7,
                    AvifNvencAqStrength = 8, AvifNvencSpatialAq = true,
                    AvifStillPicture = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },
            new PresetEntry
            {
                Name = "🚀 AVIF NVENC — 平衡 (p4, AQ=8, 4:2:0 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 82, Chroma = "4:2:0",
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifHwPresetLevel = 4,
                    AvifNvencAqStrength = 8, AvifNvencSpatialAq = true,
                    AvifStillPicture = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 32
                }
            },
            new PresetEntry
            {
                Name = "🚀 AVIF NVENC — 快速 (p1, AQ=4, 4:2:0 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 80, Chroma = "4:2:0",
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifHwPresetLevel = 1,
                    AvifNvencAqStrength = 4, AvifNvencSpatialAq = true,
                    AvifStillPicture = true,
                    MetadataMode = "StripAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 64
                }
            },

            // ═══════════════════════════════════════════
            //  AVIF — Intel QSV (av1_qsv)
            //  说明: veryslow 最好最慢; low_power=0 使用 EU 着色器质量更好
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "🚀 AVIF QSV — 高质量 (slow, CRF 24, 4:2:0 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 88, Chroma = "4:2:0", // av1_qsv 仅 nv12/p010le（2026-08-16 实测）
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifHwPresetLevel = 5, // slow
                    AvifLowPower = false,
                    AvifStillPicture = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },
            new PresetEntry
            {
                Name = "🚀 AVIF QSV — 平衡 (medium, CRF 32, 4:2:0 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 80, Chroma = "4:2:0",
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifHwPresetLevel = 4, // medium
                    AvifLowPower = false,
                    AvifStillPicture = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 3, MaxQueueSize = 24
                }
            },
            new PresetEntry
            {
                Name = "🚀 AVIF QSV — 快速 (veryfast, CRF 40, 4:2:0 10-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "AVIF", Quality = 73, Chroma = "4:2:0",
                    BitDepth = "10", ColorSpace = "auto",
                    EncoderBackend = "Ffmpeg",
                    AvifHwPresetLevel = 1, // veryfast
                    AvifLowPower = false,
                    AvifStillPicture = true,
                    MetadataMode = "StripAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 64
                }
            },

            // ═══════════════════════════════════════════
            //  WebP — 有损 + 无损
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "🌐 WebP — 高质量有损 (q=92, 4:2:0)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "WebP", Quality = 92, Chroma = "4:2:0", // libwebp 仅支持 yuv420p（2026-08-16 实测）
                    BitDepth = "auto", ColorSpace = "auto",
                    WebpPreset = "picture",
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 32
                }
            },
            new PresetEntry
            {
                Name = "🌐 WebP — 平衡有损 (q=80, 4:2:0)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "WebP", Quality = 80, Chroma = "4:2:0",
                    BitDepth = "auto", ColorSpace = "auto",
                    WebpPreset = "photo",
                    MetadataMode = "StripAll", AutoThreads = true,
                    Concurrency = 6, MaxQueueSize = 64
                }
            },
            new PresetEntry
            {
                Name = "🌐 WebP — 无损 (压缩级别 4)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "WebP", Quality = 100, Chroma = "auto",
                    BitDepth = "auto", ColorSpace = "auto",
                    Lossless = true, WebpCompressionLevel = 4,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },

            // ═══════════════════════════════════════════
            //  PNG — 无损
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "🖼 PNG — 最大压缩 (level 9, mixed)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "PNG", Quality = 0, Chroma = "auto",
                    BitDepth = "auto", ColorSpace = "auto",
                    Lossless = true, PngPred = "mixed",
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },
            new PresetEntry
            {
                Name = "🖼 PNG — 快速存档 (level 3, sub)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "PNG", Quality = 67, Chroma = "auto",
                    BitDepth = "auto", ColorSpace = "auto",
                    Lossless = true, PngPred = "sub",
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 4, MaxQueueSize = 32
                }
            },

            // ═══════════════════════════════════════════
            //  TIFF
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "🖨 TIFF — LZW 压缩存档 (16-bit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "TIFF", Quality = 0, Chroma = "auto",
                    BitDepth = "16", ColorSpace = "auto",
                    TiffCompressionAlgo = "lzw", Lossless = true,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },

            // ═══════════════════════════════════════════
            //  Gain Map (Ultra HDR) JPEG
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "🌅 Ultra HDR — Gain Map JPEG (1000 nit)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "JPEG", Quality = 85, Chroma = "4:2:0",
                    BitDepth = "auto", ColorSpace = "auto",
                    EncoderBackend = "Cjpegli",
                    JpegGainMap = true,
                    JpegGainMapQuality = 75,
                    JpegGainMapTargetNits = 1000,
                    MetadataMode = "PreserveAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },

            // ═══════════════════════════════════════════
            //  GIF 动图
            // ═══════════════════════════════════════════
            new PresetEntry
            {
                Name = "🎬 GIF — 调色板优化 (无限循环)",
                Source = "builtin",
                Data = new PresetData
                {
                    Format = "GIF", Quality = 85, Chroma = "auto",
                    BitDepth = "auto", ColorSpace = "auto",
                    GifPaletteOptimize = true, GifDither = true,
                    AnimationLoop = 0,
                    MetadataMode = "StripAll", AutoThreads = true,
                    Concurrency = 2, MaxQueueSize = 16
                }
            },
        };

        // ── 公共 API ──

        /// <summary>获取所有可用预设（内置 + 用户）</summary>
        public static List<PresetEntry> GetAllPresets()
        {
            var list = new List<PresetEntry>();
            list.AddRange(BuiltInPresets);
            list.AddRange(LoadUserPresets());
            return list;
        }

        /// <summary>获取用户预设列表</summary>
        public static List<PresetEntry> LoadUserPresets()
        {
            var list = new List<PresetEntry>();
            try
            {
                if (!Directory.Exists(PresetsDir)) return list;

                foreach (var file in Directory.EnumerateFiles(PresetsDir, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var data = PresetData.FromJson(json);
                        list.Add(new PresetEntry
                        {
                            Name = Path.GetFileNameWithoutExtension(file),
                            Source = "user",
                            FilePath = file,
                            Data = data
                        });
                    }
                    catch { /* 跳过损坏的预设文件 */ }
                }
            }
            catch { }
            return list;
        }

        /// <summary>保存当前设置为用户预设</summary>
        /// <returns>成功返回 true，名称重复返回 false</returns>
        public static bool SaveUserPreset(string name, PresetData data)
        {
            try
            {
                Directory.CreateDirectory(PresetsDir);

                // 清理文件名中的非法字符
                var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                var filePath = Path.Combine(PresetsDir, safeName + ".json");

                if (File.Exists(filePath))
                    return false; // 同名预设已存在

                var json = data.ToJson();
                File.WriteAllText(filePath, json);
                return true;
            }
            catch { return false; }
        }

        /// <summary>覆盖同名用户预设（用于更新）</summary>
        public static bool OverwriteUserPreset(string name, PresetData data)
        {
            try
            {
                Directory.CreateDirectory(PresetsDir);
                var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                var filePath = Path.Combine(PresetsDir, safeName + ".json");
                var json = data.ToJson();
                File.WriteAllText(filePath, json);
                return true;
            }
            catch { return false; }
        }

        /// <summary>删除用户预设</summary>
        public static bool DeleteUserPreset(string name)
        {
            try
            {
                var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                var filePath = Path.Combine(PresetsDir, safeName + ".json");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>导入外部预设文件到用户预设目录</summary>
        public static bool ImportExternalPreset(string sourcePath)
        {
            try
            {
                var json = File.ReadAllText(sourcePath);
                var data = PresetData.FromJson(json);
                var name = Path.GetFileNameWithoutExtension(sourcePath);
                return SaveUserPreset(name, data);
            }
            catch { return false; }
        }
    }
}
