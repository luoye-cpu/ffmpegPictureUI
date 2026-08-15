using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FfmpegGui.Services;

// ═══════════════════════════════════════════════════════════
// x86-64 指令集层级（Linux ABI 标准）
// ═══════════════════════════════════════════════════════════
public enum X86Level
{
    Baseline = 0,
    V2 = 2,  // SSE3/4.1/4.2/SSSE3/POPCNT
    V3 = 3,  // AVX/AVX2/BMI1/BMI2/FMA/LZCNT
    V4 = 4,  // AVX-512F/BW/CD/DQ/VL
}

// ═══════════════════════════════════════════════════════════
// 核心 CPU 特性检测 — 跨平台、可扩展、安全回退
//
// 设计原则:
//  1. 架构守卫: Detect() 根据 ProcessArchitecture 分发，永不跨架构访问类型
//  2. 已知安全类型直接访问 IsSupported (.NET 所有平台均存在)
//  3. 可能不存在的类型 (AVX10/AMX/SVE) 通过反射探测，失败静默回退 false
//  4. 所有公共属性均为 bool，调用方无需 try-catch
// ═══════════════════════════════════════════════════════════
public static class CpuFeatureService
{
    private static bool _detected;

    // ── 架构 ──
    public static bool IsX86   { get; private set; }
    public static bool IsX64   { get; private set; }
    public static bool IsArm64 { get; private set; }

    // ── x86 基础 ──
    public static bool HasSse2   { get; private set; }
    public static bool HasSse3   { get; private set; }
    public static bool HasSsse3  { get; private set; }
    public static bool HasSse41  { get; private set; }
    public static bool HasSse42  { get; private set; }
    public static bool HasPopcnt { get; private set; }
    public static bool HasAes    { get; private set; }

    // ── x86 AVX ──
    public static bool HasAvx   { get; private set; }
    public static bool HasAvx2  { get; private set; }
    public static bool HasFma   { get; private set; }
    public static bool HasBmi1  { get; private set; }
    public static bool HasBmi2  { get; private set; }
    public static bool HasLzcnt { get; private set; }

    // ── x86 AVX-512（反射探测，旧运行时可能不存在）──
    public static bool HasAvx512F  { get; private set; }
    public static bool HasAvx512BW { get; private set; }
    public static bool HasAvx512DQ { get; private set; }
    public static bool HasAvx512VL { get; private set; }
    public static bool HasAvx512CD { get; private set; }
    public static bool HasAvx512Vnni { get; private set; }
    public static bool HasAvx512Bf16 { get; private set; }
    public static bool HasAvxVnni    { get; private set; }

    // ── x86 AVX10（.NET 9+ / 未来运行时，反射探测）──
    public static bool HasAvx10v1 { get; private set; }
    public static bool HasAvx10v2 { get; private set; }

    // ── x86 AMX（Sapphire Rapids+，反射探测）──
    public static bool HasAmxTile { get; private set; }
    public static bool HasAmxBf16 { get; private set; }
    public static bool HasAmxInt8 { get; private set; }

    // ── ARM ──
    public static bool HasAdvSimd { get; private set; }
    public static bool HasNeon    => HasAdvSimd;
    public static bool HasSve     { get; private set; }
    public static bool HasSve2    { get; private set; }
    public static bool HasArmAes  { get; private set; }
    public static bool HasCrc32   { get; private set; }

    // ── 综合 ──
    public static bool     HasAnySimd      { get; private set; }
    public static X86Level X86FeatureLevel { get; private set; } = X86Level.Baseline;
    public static string   BestSimdTag     { get; private set; } = "none";

    // ═══════════════════════════════════════════
    // 检测入口
    // ═══════════════════════════════════════════
    public static void Detect()
    {
        if (_detected) return;
        _detected = true;

        var arch = RuntimeInformation.ProcessArchitecture;
        IsX86   = arch is Architecture.X86;
        IsX64   = arch is Architecture.X64;
        IsArm64 = arch is Architecture.Arm64;

        if (IsX86 || IsX64)
            DetectX86();
        else if (IsArm64)
            DetectArm();

        DetermineBestSimdTag();
        DetermineX86Level();
        HasAnySimd = BestSimdTag != "none";
    }

    // ═══════════════════════════════════════════
    // x86 检测
    // ═══════════════════════════════════════════
    private static void DetectX86()
    {
        // 这些类型在所有 .NET 运行时均存在，直接访问 IsSupported
        Safe(() => HasSse2   = System.Runtime.Intrinsics.X86.Sse2.IsSupported);
        Safe(() => HasSse3   = System.Runtime.Intrinsics.X86.Sse3.IsSupported);
        Safe(() => HasSsse3  = System.Runtime.Intrinsics.X86.Ssse3.IsSupported);
        Safe(() => HasSse41  = System.Runtime.Intrinsics.X86.Sse41.IsSupported);
        Safe(() => HasSse42  = System.Runtime.Intrinsics.X86.Sse42.IsSupported);
        Safe(() => HasPopcnt = System.Runtime.Intrinsics.X86.Popcnt.IsSupported);
        Safe(() => HasAes    = System.Runtime.Intrinsics.X86.Aes.IsSupported);
        Safe(() => HasAvx    = System.Runtime.Intrinsics.X86.Avx.IsSupported);
        Safe(() => HasAvx2   = System.Runtime.Intrinsics.X86.Avx2.IsSupported);
        Safe(() => HasFma    = System.Runtime.Intrinsics.X86.Fma.IsSupported);
        Safe(() => HasBmi1   = System.Runtime.Intrinsics.X86.Bmi1.IsSupported);
        Safe(() => HasBmi2   = System.Runtime.Intrinsics.X86.Bmi2.IsSupported);
        Safe(() => HasLzcnt  = System.Runtime.Intrinsics.X86.Lzcnt.IsSupported);

        // 以下类型可能不在旧运行时中存在 → 反射探测
        var x86Asm = typeof(System.Runtime.Intrinsics.X86.Avx2).Assembly;
        HasAvx512F    = ProbeX86(x86Asm, "Avx512F");
        HasAvx512BW   = ProbeX86(x86Asm, "Avx512BW");
        HasAvx512DQ   = ProbeX86(x86Asm, "Avx512DQ");
        HasAvx512VL   = ProbeX86(x86Asm, "Avx512VL");
        HasAvx512CD   = ProbeX86(x86Asm, "Avx512CD");
        HasAvx512Vnni = ProbeX86(x86Asm, "Avx512Vnni");
        HasAvx512Bf16 = ProbeX86(x86Asm, "Avx512Bf16");
        HasAvxVnni    = ProbeX86(x86Asm, "AvxVnni");
        HasAvx10v1    = ProbeX86(x86Asm, "Avx10v1");
        HasAvx10v2    = ProbeX86(x86Asm, "Avx10v2");
        HasAmxTile    = ProbeX86(x86Asm, "AmxTile");
        HasAmxBf16    = ProbeX86(x86Asm, "AmxBf16");
        HasAmxInt8    = ProbeX86(x86Asm, "AmxInt8");
    }

    // ═══════════════════════════════════════════
    // ARM 检测
    // ═══════════════════════════════════════════
    private static void DetectArm()
    {
        Safe(() => HasAdvSimd = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported);
        Safe(() => HasArmAes  = System.Runtime.Intrinsics.Arm.Aes.IsSupported);
        Safe(() => HasCrc32   = System.Runtime.Intrinsics.Arm.Crc32.IsSupported);

        var armAsm = typeof(System.Runtime.Intrinsics.Arm.AdvSimd).Assembly;
        HasSve  = ProbeArm(armAsm, "Sve");
        HasSve2 = ProbeArm(armAsm, "Sve2");
    }

    // ═══════════════════════════════════════════
    // 确定最优 SIMD 标识
    // ═══════════════════════════════════════════
    private static void DetermineBestSimdTag()
    {
        if (HasAvx10v2)   { BestSimdTag = "avx10.2"; return; }
        if (HasAvx10v1)   { BestSimdTag = "avx10.1"; return; }
        if (HasAvx512F)   { BestSimdTag = "avx512";  return; }
        if (HasAvx2)      { BestSimdTag = "avx2";    return; }
        if (HasAvx)       { BestSimdTag = "avx";     return; }
        if (HasSse41)     { BestSimdTag = "sse4";    return; }
        if (HasSse2)      { BestSimdTag = "sse2";    return; }
        if (HasSve2)      { BestSimdTag = "sve2";    return; }
        if (HasSve)       { BestSimdTag = "sve";     return; }
        if (HasAdvSimd)   { BestSimdTag = "neon";    return; }
        BestSimdTag = "none";
    }

    // ═══════════════════════════════════════════
    // 确定 x86-64 特性层级
    // ═══════════════════════════════════════════
    private static void DetermineX86Level()
    {
        if (!IsX64) return;
        if (HasAvx512F && HasAvx512BW && HasAvx512CD && HasAvx512DQ && HasAvx512VL)
            { X86FeatureLevel = X86Level.V4; return; }
        if (HasAvx && HasAvx2 && HasBmi1 && HasBmi2 && HasFma && HasLzcnt)
            { X86FeatureLevel = X86Level.V3; return; }
        if (HasSse3 && HasSse41 && HasSse42 && HasSsse3 && HasPopcnt)
            { X86FeatureLevel = X86Level.V2; return; }
        X86FeatureLevel = X86Level.Baseline;
    }

    // ═══════════════════════════════════════════
    // 反射辅助（NativeAOT 安全）
    // ═══════════════════════════════════════════
    // 这些类型可能不存在于旧运行时（AVX512/AVX10/AMX/SVE），故用反射探测。
    // NativeAOT 下安全：csproj 已通过 TrimmerRootAssembly 保留整个
    // System.Runtime.Intrinsics 程序集，类型与 IsSupported 属性均不会被裁剪。
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "System.Runtime.Intrinsics 由 csproj TrimmerRootAssembly 整体保留，反射类型必存在")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "同上：root 程序集后 PublicProperties 元数据必然保留")]
    private static bool ProbeX86(Assembly asm, string typeName)
    {
        try
        {
            var t = asm.GetType($"System.Runtime.Intrinsics.X86.{typeName}");
            if (t == null) return false;
            var p = t.GetProperty("IsSupported", BindingFlags.Public | BindingFlags.Static);
            return p != null && (bool)p.GetValue(null)!;
        }
        catch { return false; }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "System.Runtime.Intrinsics 由 csproj TrimmerRootAssembly 整体保留，反射类型必存在")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "同上：root 程序集后 PublicProperties 元数据必然保留")]
    private static bool ProbeArm(Assembly asm, string typeName)
    {
        try
        {
            var t = asm.GetType($"System.Runtime.Intrinsics.Arm.{typeName}");
            if (t == null) return false;
            var p = t.GetProperty("IsSupported", BindingFlags.Public | BindingFlags.Static);
            return p != null && (bool)p.GetValue(null)!;
        }
        catch { return false; }
    }

    private static void Safe(Action a) { try { a(); } catch { } }

    // ═══════════════════════════════════════════
    // 优先级标签 — 外部二进制文件选择
    // ═══════════════════════════════════════════
    private static string[]? _simdPriorityTags;

    /// <summary>返回按优先级降序排列的 SIMD 标签，用于匹配外部二进制文件名</summary>
    public static string[] GetSimdPriorityTags()
    {
        if (_simdPriorityTags != null) return _simdPriorityTags;
        Detect();
        var t = new List<string>();
        if (HasAvx10v2)   t.Add("avx10.2");
        if (HasAvx10v1)   t.Add("avx10");
        if (HasAvx512Vnni) t.Add("avx512_vnni");
        if (HasAvx512F)   t.Add("avx512");
        if (HasAvx2)      t.Add("avx2");
        if (HasAvx)       t.Add("avx");
        if (HasSse41)     t.Add("sse4");
        if (HasSse2)      t.Add("sse2");
        if (HasSve2)      t.Add("sve2");
        if (HasSve)       t.Add("sve");
        if (HasAdvSimd)   t.Add("neon");
        t.Add("generic");
        return _simdPriorityTags = t.ToArray();
    }

    // ═══════════════════════════════════════════
    // 公共 API
    // ═══════════════════════════════════════════

    public static string Summary()
    {
        Detect();
        var p = new List<string> { IsArm64 ? "ARM64" : IsX64 ? "x64" : "x86" };
        if (IsX64) p.Add($"v{(int)X86FeatureLevel}");
        p.Add(BestSimdTag);
        return string.Join(" ", p);
    }

    public static string FullReport()
    {
        Detect();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("══════ CPU 特性检测报告 ══════");
        sb.Append("架构: ").AppendLine(IsArm64 ? "ARM64" : IsX64 ? "x86-64" : "x86");
        if (IsX64) sb.Append("x86-64 层级: v").Append((int)X86FeatureLevel).AppendLine();
        sb.Append("最优 SIMD: ").AppendLine(BestSimdTag);

        if (IsX86 || IsX64)
        {
            sb.AppendLine("── x86 ──");
            sb.AppendLine(Flag("SSE2",HasSse2)+Flag("SSE3",HasSse3)+Flag("SSSE3",HasSsse3));
            sb.AppendLine(Flag("SSE4.1",HasSse41)+Flag("SSE4.2",HasSse42)+Flag("POPCNT",HasPopcnt));
            sb.AppendLine(Flag("AVX",HasAvx)+Flag("AVX2",HasAvx2)+Flag("FMA",HasFma));
            sb.AppendLine(Flag("AVX-512F",HasAvx512F)+Flag("BW",HasAvx512BW)+Flag("DQ",HasAvx512DQ));
            sb.AppendLine(Flag("AVX10.1",HasAvx10v1)+Flag("AVX10.2",HasAvx10v2));
            sb.AppendLine(Flag("AMX-TILE",HasAmxTile)+Flag("AMX-BF16",HasAmxBf16)+Flag("AMX-INT8",HasAmxInt8));
        }
        if (IsArm64)
        {
            sb.AppendLine("── ARM ──");
            sb.AppendLine(Flag("NEON",HasAdvSimd)+Flag("SVE",HasSve)+Flag("SVE2",HasSve2));
        }
        sb.AppendLine("═══════════════════════════════");
        return sb.ToString();
    }

    private static string Flag(string n, bool v) => $"  {n,-12}: {(v ? "✅" : "❌")}";
}