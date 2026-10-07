using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Tests.Shared;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;
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
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using AAS = FfmpegGui.Services.AppSettingsService;

namespace Tests.Geometry
{
    /// <summary>
    /// 子系统测试组：**geometry**（缩放 / 首帧 / 取向）—— 2026-10-05 从 ServiceProbe 的单个
    /// 577 KB <c>Program.cs</c> 中物理拆出，使"改几何这一块"只需跑本工程，不再跑整个宿主。
    ///
    /// <para><b>覆盖的 mode</b>：<c>geometry</c> <c>firstframe</c> <c>orientation</c>
    /// <c>orientmeta</c>（见 <c>TestGroups</c>）。</para>
    ///
    /// <para><b>抽取原则</b>：<c>Probe*</c> 方法体**逐字复制**自
    /// <c>tests/ServiceProbe/Program.cs</c>（不改逻辑、不改字符串 —— 门禁在 grep 那些文本），
    /// 共享 helper 一律**转发**到 <c>Harness</c>。本组四个 probe 各自只用局部函数
    /// （<c>MakeSource</c> / <c>Cap</c> / <c>Psnr</c> / <c>Run</c> …）与 <c>Check</c>，
    /// 无需额外搬运类级 helper。</para>
    /// </summary>
    // After the two-step mode split (_gen-mode-split.ps1 + _gen-mode-split-reduce.ps1),
    // this file keeps ONLY the shared base: helpers used by >=2 modes plus the Run*
    // forwarding entry points. Each Probe* body (and its exclusive helpers) was copied
    // VERBATIM into Modes/<mode>.cs; those files are the other half of the same class
    // (partial class), so every call site is unchanged.
    // Assertion count is unchanged: acceptance = per-mode readings identical.
    internal static partial class GeometryGroup
    {
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);

        private static void Skip(string msg) => Harness.Skip(msg);

        internal static void RunGeometry(string[] a) => ProbeGeometry(a).GetAwaiter().GetResult();

        internal static void RunFirstframe(string[] a) => ProbeFirstframe(a).GetAwaiter().GetResult();

        internal static void RunOrientation(string[] a) => ProbeOrientation(a).GetAwaiter().GetResult();

        internal static void RunOrientMeta(string[] a) => ProbeOrientMeta(a).GetAwaiter().GetResult();
    }
}
