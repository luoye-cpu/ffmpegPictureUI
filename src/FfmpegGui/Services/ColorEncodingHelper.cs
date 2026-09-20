using System;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 色彩空间编码名称映射：将 FFmpeg 色彩参数翻译为 cjxl/cjpegli 的 -x color_space=... 参数。
    /// cjxl/cjpegli 需要显式的色彩编码名称来正确标记和编码原始像素数据（如 PPM 管道）。
    /// </summary>
    public static class ColorEncodingHelper
    {
        /// <summary>
        /// 根据 FfmpegOptions 计算 cjxl/cjpegli 的 -x color_space 值。
        /// 优先使用高级参数 (ColorPrimaries+ColorTrc)，否则回退到简化 ColorSpace。
        /// </summary>
        /// <returns>cjxl 兼容的色彩编码缩写；null 表示不指定（使用编码器默认）</returns>
        public static string? MapToCjxlColorSpace(Models.FfmpegOptions options)
        {
            // ── 高级模式：使用 primaries + transfer 精确映射 ──
            if (options.UseAdvancedColorParameters
                && !string.IsNullOrWhiteSpace(options.ColorPrimaries))
            {
                return MapPrimariesTransfer(options.ColorPrimaries, options.ColorTrc);
            }

            // ── 简化模式：按 ColorSpace 显示名统一解析（与 ffmpeg 直接路径同源同义，P4）──
            // 简化仅指 UI 少暴露控件，功能不缩水：完整覆盖 sRGB/BT.709/Display P3/P3 PQ/BT.2020 PQ/HLG。
            if (!string.IsNullOrWhiteSpace(options.ColorSpace)
                && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return ColorSpaceRegistry.CjxlColorSpace(options.ColorSpace);
            }

            // auto / 未设置 → 不指定（编码器自动检测或使用默认 sRGB）
            return null;
        }

        /// <summary>
        /// 根据探测到的色彩元数据直接映射（用于 auto 模式下色彩自动检测）。
        /// 不限制位深：8-bit 广色域（如 P3 sRGB 内容）也能正确标记，
        /// PPM/PAM 管道流不携带色彩标签，必须依赖此映射显式标记。
        /// </summary>
        public static string? MapToCjxlColorSpace(FfmpegCommandBuilder.ColorMetadata hdrMeta)
        {
            return MapPrimariesTransfer(hdrMeta.colorPrimaries, hdrMeta.colorTrc);
        }

        /// <summary>
        /// **引擎 cjxl 出口专用**：由规划层的**目标描述符**映射 cjxl 的 <c>-x color_space=</c>。
        /// <para>
        /// 为什么需要这个重载（2026-09-17 接入 cjxl 出口时新增）：上面两个重载吃的是
        /// <c>FfmpegOptions</c> / 探测元数据，而引擎出口的**唯一权威色彩语义是计划**
        /// （<c>ColorTransformPlan.Dst</c>，它是按容器能力钳制后的结果）。
        /// 若出口回头去读 options 或重新探测，就等于把「决策」在出口里**重算一遍** ——
        /// 会出现"计划说 A、出口标 B"的两套口径（本项目反复修过的缺陷形状）。
        /// </para>
        /// <para>
        /// 映射规则复用同一份 <see cref="MapPrimariesTransfer"/>（不另写一份表）。
        /// ⚠ 返回 null 有两种含义，调用方必须区分处理：
        /// <list type="bullet">
        ///   <item>描述符为 null ⇒ 语义未知；</item>
        ///   <item>描述符可命名、但**落不进 cjxl 的 <c>color_space</c> 枚举**
        ///         （枚举只有 sRGB / DisplayP3 / Rec2100PQ / Rec2100HLG，见
        ///         <c>ColorTransformPlan.JxlColorSpaceEnums</c>；典型是 **SDR BT.2020**、
        ///         DCI-P3、ProPhoto 这类具名空间）⇒ **只能靠 ICC 描述**。</item>
        /// </list>
        /// 故出口在拿到 null 时必须检查计划是否给了 ICC；两者皆无 ⇒ **显式拒绝**
        /// （绝不产出无色彩描述的 JXL）。
        /// </para>
        /// </summary>
        public static string? MapToCjxlColorSpace(ColorMapping.ColorSpaceDescriptor? d)
            => d == null ? null : MapPrimariesTransfer(d.PrimariesToken, d.Curve.CicpToken);

        /// <summary>
        /// 目标描述符是否为 HDR（PQ/HLG）—— 决定 cjxl 出口**必须**声明 <c>--intensity_target</c>。
        /// <para>
        /// 依据：`CjxlService.BuildCjxlArguments` 已登记的实测结论 —— 缺了它 libjxl 会回落到
        /// **默认峰值**，HDR 画面的显示亮度就错了（不是"少个标签"，是亮度语义错）。
        /// </para>
        /// </summary>
        public static bool IsHdrDescriptor(ColorMapping.ColorSpaceDescriptor? d)
            => d != null
               && (d.Curve.Kind == ColorMapping.TransferCurve.CurveKind.Pq
                   || d.Curve.Kind == ColorMapping.TransferCurve.CurveKind.Hlg);

        /// <summary>
        /// 根据探测到的色彩元数据计算 --intensity_target（用于 auto 模式下 HDR 自动检测）。
        /// </summary>
        public static int MapToIntensityTarget(FfmpegCommandBuilder.ColorMetadata hdrMeta)
        {
            if (hdrMeta.bitDepth <= 8)
                return 0;
            var t = hdrMeta.colorTrc ?? "";
            if (t.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
                || t.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase))
            {
                // P8: 优先采用源峰值亮度(MaxCLL/mastering)，否则回退 1000。
                if (hdrMeta.peakNits > 0)
                    return (int)Math.Clamp(Math.Round(hdrMeta.peakNits), 100, 10000);
                return 1000;
            }
            return 0;
        }

        /// <summary>
        /// 根据 FfmpegOptions 计算 --intensity_target 值 (nits)。
        /// 仅对 HDR 传输函数 (PQ/HLG) 生效。
        /// </summary>
        /// <returns>nits 值；0 表示不指定</returns>
        public static int MapToIntensityTarget(Models.FfmpegOptions options)
        {
            // 检查是否为 HDR 传输函数
            var trc = options.ColorTrc;
            if (options.UseAdvancedColorParameters && !string.IsNullOrWhiteSpace(trc))
            {
                if (trc.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
                    || trc.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase))
                {
                    if (options.JpegGainMapTargetNits > 0)
                        return options.JpegGainMapTargetNits;
                    return 1000;
                }
            }

            // 简化模式：目标归一化后为 HDR 传递(PQ/HLG)时需设置 intensity_target。
            // 委托 ColorSpaceRegistry（全流程单一色彩真值）判定，取代旧硬编码名单：
            // 覆盖 BT.2020 PQ/HLG/BT.2020 及 P3 PQ(已归一 Rec2100PQ)——旧名单漏 P3 PQ
            // 会令其 Rec2100PQ 标记缺峰值亮度声明（libjxl 回落默认峰值）。
            if (!string.IsNullOrWhiteSpace(options.ColorSpace) && !options.UseAdvancedColorParameters)
            {
                var spec = ColorSpaceRegistry.Resolve(options.ColorSpace);
                if (spec != null && (spec.OutTrc == "smpte2084" || spec.OutTrc == "arib-std-b67"))
                {
                    if (options.JpegGainMapTargetNits > 0)
                        return options.JpegGainMapTargetNits;
                    return 1000;
                }
            }

            return 0;
        }

        /// <summary>
        /// 精确映射：primaries + transfer → cjxl color_space 缩写
        /// </summary>
        public static string? MapPrimariesTransfer(string? primaries, string? transfer)
            => ColorSpaceRegistry.MapPrimariesTransferToCjxl(primaries, transfer);
    }
}
