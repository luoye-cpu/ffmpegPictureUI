using System;
using System.IO;
using System.Text;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;   // 色彩映射引擎的曲线表（P1）
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;     // 执行内核（P3）
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;     // PSNR/ΔE 度量
using CS = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor;
using CE = FfmpegGui.Services.ColorMapping.ColorMappingEngine;
using CI = FfmpegGui.Services.ColorMapping.ColorIntent;
using CA = FfmpegGui.Services.ColorMapping.ColorAction;
using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
using CL = FfmpegGui.Services.ColorMapping.ColorLoss;
using ST = FfmpegGui.Models.ColorStrategy;
using DC = FfmpegGui.Services.ColorMapping.DescriptorConfidence;
using OR = FfmpegGui.Services.ColorMapping.OverrideReason;
using AC = FfmpegGui.Services.ColorMapping.AnnotChoice;
using CSC = FfmpegGui.Services.ColorMapping.CarryScope;
using MI = FfmpegGui.Services.ColorMapping.ManualIntent;
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;   // S5′ 裁决探针
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;         // `--raw-color-target auto` 判据内核（2026-10-04）
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;          // S5′ 裁决探针
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;     // S5′ 裁决探针
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using GMJ = FfmpegGui.Services.ColorMapping.GainMapJpegCodec;   // GainMap 解码口径/编码参数单一真值（gmgamut）
using AAS = FfmpegGui.Services.AppSettingsService;

namespace ServiceProbe
{
    /// <summary>
    /// 服务级探针 —— 直接调用真实服务实现做单元级验证，无需跑完整编码队列。
    ///
    /// 用法:
    ///   ServiceProbe gainmap &lt;jpeg&gt; [写出副图路径]   纯托管容器解析 (ISO 21496-1 / MPF / 副图切割)
    ///   ServiceProbe icc &lt;色彩空间名&gt;                 解析/生成命名 ICC 并打印头 + tag 表
    ///   ServiceProbe cicp &lt;文件&gt;                     ffprobe 风格的色彩元数据探测结果 (Registry 解释)
    ///   ServiceProbe matrix                           线性域 primaries 矩阵：对公开发布值 + 互逆自洽
    ///   ServiceProbe curve                            传递函数表：锚点 / 往返互逆 / 单调 / 与 SimdPixelOps 一致
    ///   ServiceProbe gamut &lt;jpeg&gt; [token]            读底图 ICC 判原色
    ///
    /// 退出码: 0 = 全部断言通过 (若给了断言), 1 = 失败; 输出为 key=value 行, 便于脚本 grep。
    /// </summary>
    internal static partial class Program
    {

    }
}

