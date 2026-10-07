using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Tests.Shared;
using FfmpegGui.Services;
// ⚠ 与拆分前 `ServiceProbe/Program.cs` 顶部的 using-alias **逐字一致**：
//   `GF` = `RawGamutFit`（原 Program.cs:21）。拆出后必须自带，
//   否则 `GF.M2020To709` 等引用全部 CS0103（拆分时实测踩过）。
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;
// ⚠ 2026-10-05 审查补：`engine`(`ProbeEngineAb`) / `simdswitch`(`ProbeSimdSwitch`) 两个 mode
//   当初**漏搬**（组表声明了却没人实现 ⇒ 独立跑 0 条断言；旧宿主是 35/0 与 28/0）
//   ⇒ 现补搬。它们用到原宿主顶部的这些别名，**逐字照抄**（漏写即 CS0103；本次实测踩过）。
using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
using CE = FfmpegGui.Services.ColorMapping.ColorMappingEngine;
using CS = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor;
using ST = FfmpegGui.Models.ColorStrategy;
using OR = FfmpegGui.Services.ColorMapping.OverrideReason;
using AAS = FfmpegGui.Services.AppSettingsService;

namespace Tests.Engine
{
    /// <summary>
    /// 子系统测试组：**engine**（自研引擎与像素内核）—— 2026-10-05 从 ServiceProbe 的单个
    /// 577 KB `Program.cs` 中物理拆出，使"改引擎这一块"只需跑本工程，不再跑整个宿主。
    ///
    /// <para><b>覆盖的 mode</b>：`engine` `gamut` `gamutfit` `simdswitch`（见 `TestGroups`）。
    /// 本文件承载 `gamutfit` —— `RawGamutFit`（`--raw-color-target auto` 的降级判据内核）的纯函数判据。</para>
    ///
    /// <para><b>独立运行</b>：<c>dotnet run --project tests/engine</c>（或直接跑产物 exe）。
    /// 也可由聚合入口 `ServiceProbe gamutfit` 调用（保既有门禁契约零改动）。</para>
    /// </summary>
    // After the two-step mode split (_gen-mode-split.ps1 + _gen-mode-split-reduce.ps1),
    // this file keeps ONLY the shared base: helpers used by >=2 modes plus the Run*
    // forwarding entry points. Each Probe* body (and its exclusive helpers) was copied
    // VERBATIM into Modes/<mode>.cs; those files are the other half of the same class
    // (partial class), so every call site is unchanged.
    // Assertion count is unchanged: acceptance = per-mode readings identical.
    internal static partial class EngineGroup
    {
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);

        internal static void RunGamutFit() => ProbeGamutFit();

        internal static void RunEngineAb() => ProbeEngineAb();

        internal static void RunSimdSwitch() => ProbeSimdSwitch();

        internal static void RunGamut(string[] a) => ProbeGamut(a);
    }
}
