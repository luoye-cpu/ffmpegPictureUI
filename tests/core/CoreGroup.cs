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

namespace Tests.Core
{
    /// <summary>
    /// 子系统测试组：**core**（决策层：契约 / 裁决 / 计划 / 路由 / 落盘）—— 2026-10-05 从
    /// ServiceProbe 的单个 577 KB `Program.cs` 中物理拆出，使"改决策层这一块"只需跑本工程，
    /// 不再跑整个宿主。
    ///
    /// <para><b>覆盖的 mode</b>：`contract` `verdict` `plan` `decision` `wire` `runner` `settings`
    /// （见 <see cref="TestGroups"/>）。</para>
    ///
    /// <para><b>抽取规则</b>：方法体**逐字**从旧宿主搬来，字符串一个字未改（门禁在 grep 那些文本）。
    /// 共享 helper 一律转发 <see cref="Harness"/>，不复制实现。仅 `Dump` 与 `Runner` 探针
    /// 一同搬来（全仓只有它一个调用者）。</para>
    /// </summary>
    // After the two-step mode split (_gen-mode-split.ps1 + _gen-mode-split-reduce.ps1),
    // this file keeps ONLY the shared base: helpers used by >=2 modes plus the Run*
    // forwarding entry points. Each Probe* body (and its exclusive helpers) was copied
    // VERBATIM into Modes/<mode>.cs; those files are the other half of the same class
    // (partial class), so every call site is unchanged.
    // Assertion count is unchanged: acceptance = per-mode readings identical.
    internal static partial class CoreGroup
    {
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);

        internal static void RunContract() => ProbeContract();

        internal static void RunVerdict() => ProbeVerdict();

        internal static void RunPlan(string[] args) => ProbePlan(args);

        internal static void RunDecision() => ProbeDecision();

        internal static void RunWire() => ProbeWire();

        internal static void RunRunner(string[] args) => ProbeRunnerGuard(args).GetAwaiter().GetResult();

        internal static void RunSettingsSave() => ProbeSettingsSave();
    }
}
