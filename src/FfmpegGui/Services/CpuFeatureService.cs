using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    // NativeAOT 的 System.Runtime.Intrinsics.X86.*.IsSupported 反映的是
    // **编译目标 ISA**，不是裸 CPU 的 CPUID。.NET 11 AOT 基线为 x86-64-v2，
    // 因此 AOT 包在 AVX2 机器上也会把 Avx.IsSupported 报成 false。
    // 下面把“硬件 CPUID”“当前进程可用”“外部工具可用”三件事分开：
    //   Has* / BestSimdTag / X86FeatureLevel  = 硬件 CPUID（用于报告与外部工具选路）
    //   Runtime* / RuntimeBestSimdTag         = 当前进程 JIT/AOT 实际可执行的 ISA
    //   CanUse*                               = 硬件 + OS XSTATE 双重门（外部工具可安全运行）
    public static bool   IsAotRuntime         { get; private set; }
    public static string RuntimeBestSimdTag   { get; private set; } = "none";
    public static string ExternalBestSimdTag  { get; private set; } = "none";
    public static bool   RuntimeHasAvx        { get; private set; }
    public static bool   RuntimeHasAvx2       { get; private set; }
    public static bool   CanUseAvx            { get; private set; }
    public static bool   CanUseAvx2           { get; private set; }
    public static bool   CanUseAvx512         { get; private set; }
    public static bool   CanUseAmx            { get; private set; }

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
        IsAotRuntime = !RuntimeFeature.IsDynamicCodeSupported;

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
        // ── ① 当前进程运行时实际可执行的 ISA（JIT/AOT 视角）──
        // 注意：NativeAOT 下这些值由编译目标 ISA 决定，不等于裸 CPU 的 CPUID。
        bool rtSse2 = false, rtSse3 = false, rtSsse3 = false, rtSse41 = false, rtSse42 = false;
        bool rtPopcnt = false, rtAes = false, rtAvx = false, rtAvx2 = false, rtFma = false;
        bool rtBmi1 = false, rtBmi2 = false, rtLzcnt = false;
        Safe(() => rtSse2 = System.Runtime.Intrinsics.X86.Sse2.IsSupported);
        Safe(() => rtSse3 = System.Runtime.Intrinsics.X86.Sse3.IsSupported);
        Safe(() => rtSsse3 = System.Runtime.Intrinsics.X86.Ssse3.IsSupported);
        Safe(() => rtSse41 = System.Runtime.Intrinsics.X86.Sse41.IsSupported);
        Safe(() => rtSse42 = System.Runtime.Intrinsics.X86.Sse42.IsSupported);
        Safe(() => rtPopcnt = System.Runtime.Intrinsics.X86.Popcnt.IsSupported);
        Safe(() => rtAes = System.Runtime.Intrinsics.X86.Aes.IsSupported);
        Safe(() => rtAvx = System.Runtime.Intrinsics.X86.Avx.IsSupported);
        Safe(() => rtAvx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported);
        Safe(() => rtFma = System.Runtime.Intrinsics.X86.Fma.IsSupported);
        Safe(() => rtBmi1 = System.Runtime.Intrinsics.X86.Bmi1.IsSupported);
        Safe(() => rtBmi2 = System.Runtime.Intrinsics.X86.Bmi2.IsSupported);
        Safe(() => rtLzcnt = System.Runtime.Intrinsics.X86.Lzcnt.IsSupported);
        RuntimeHasAvx = rtAvx;
        RuntimeHasAvx2 = rtAvx2;
        RuntimeBestSimdTag = rtAvx2 ? "avx2"
            : rtAvx ? "avx"
            : rtSse41 ? "sse4"
            : rtSse2 ? "sse2"
            : "none";

        // ── ② 裸 CPUID：硬件到底有什么（不受 AOT/JIT 目标 ISA 影响）──
        bool cpuidOk = false;
        int maxBasic = 0, maxExt = 0;
        int leaf1Ecx = 0, leaf1Edx = 0;
        int leaf7Ebx = 0, leaf7Ecx = 0, leaf7Edx = 0, leaf7Sub1Eax = 0;
        int extEcx = 0;
        Safe(() =>
        {
            var l0 = System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0);
            maxBasic = l0.Eax;
            var le = System.Runtime.Intrinsics.X86.X86Base.CpuId(unchecked((int)0x80000000), 0);
            maxExt = le.Eax;
            if (maxBasic >= 1)
            {
                var l1 = System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);
                leaf1Ecx = l1.Ecx; leaf1Edx = l1.Edx;
            }
            if (maxBasic >= 7)
            {
                var l7 = System.Runtime.Intrinsics.X86.X86Base.CpuId(7, 0);
                leaf7Ebx = l7.Ebx; leaf7Ecx = l7.Ecx; leaf7Edx = l7.Edx;
                var l7s1 = System.Runtime.Intrinsics.X86.X86Base.CpuId(7, 1);
                leaf7Sub1Eax = l7s1.Eax;
            }
            if (maxExt >= unchecked((int)0x80000001))
            {
                var le1 = System.Runtime.Intrinsics.X86.X86Base.CpuId(unchecked((int)0x80000001), 0);
                extEcx = le1.Ecx;
            }
            cpuidOk = maxBasic >= 1;
        });

        if (cpuidOk)
        {
            HasSse2   = Bit(leaf1Edx, 26);
            HasSse3   = Bit(leaf1Ecx, 0);
            HasSsse3  = Bit(leaf1Ecx, 9);
            HasSse41  = Bit(leaf1Ecx, 19);
            HasSse42  = Bit(leaf1Ecx, 20);
            HasPopcnt = Bit(leaf1Ecx, 23);
            HasAes    = Bit(leaf1Ecx, 25);
            HasAvx    = Bit(leaf1Ecx, 28);
            HasFma    = Bit(leaf1Ecx, 12);
            HasBmi1   = maxBasic >= 7 && Bit(leaf7Ebx, 3);
            HasAvx2   = maxBasic >= 7 && Bit(leaf7Ebx, 5);
            HasBmi2   = maxBasic >= 7 && Bit(leaf7Ebx, 8);
            HasLzcnt  = maxExt >= unchecked((int)0x80000001) && Bit(extEcx, 5);

            HasAvx512F    = maxBasic >= 7 && Bit(leaf7Ebx, 16);
            HasAvx512BW   = maxBasic >= 7 && Bit(leaf7Ebx, 30);
            HasAvx512DQ   = maxBasic >= 7 && Bit(leaf7Ebx, 17);
            HasAvx512VL   = maxBasic >= 7 && Bit(leaf7Ebx, 31);
            HasAvx512CD   = maxBasic >= 7 && Bit(leaf7Ebx, 28);
            HasAvx512Vnni = maxBasic >= 7 && Bit(leaf7Ecx, 11);
            HasAvx512Bf16 = maxBasic >= 7 && Bit(leaf7Sub1Eax, 5);
            HasAvxVnni    = maxBasic >= 7 && Bit(leaf7Sub1Eax, 4);

            // AMX: CPUID.7.0:EDX[22]=AMX_BF16, [24]=AMX_TILE, [25]=AMX_INT8
            HasAmxTile    = maxBasic >= 7 && Bit(leaf7Edx, 24);
            HasAmxBf16    = maxBasic >= 7 && Bit(leaf7Edx, 22);
            HasAmxInt8    = maxBasic >= 7 && Bit(leaf7Edx, 25);

            // AVX10: CPUID.24H:0 EAX[7:0] = AVX10 version (1 => 10.1, 2 => 10.2).
            // 旧 CPU 上 maxBasic < 0x24，保持 false。
            if (maxBasic >= 0x24)
            {
                var l24 = System.Runtime.Intrinsics.X86.X86Base.CpuId(0x24, 0);
                int avx10Version = l24.Eax & 0xFF;
                HasAvx10v1 = avx10Version >= 1;
                HasAvx10v2 = avx10Version >= 2;
            }
        }
        else
        {
            // CPUID 不可用（极旧/受限环境）⇒ 退回运行时探测，至少保持旧行为。
            HasSse2 = rtSse2; HasSse3 = rtSse3; HasSsse3 = rtSsse3;
            HasSse41 = rtSse41; HasSse42 = rtSse42; HasPopcnt = rtPopcnt; HasAes = rtAes;
            HasAvx = rtAvx; HasAvx2 = rtAvx2; HasFma = rtFma;
            HasBmi1 = rtBmi1; HasBmi2 = rtBmi2; HasLzcnt = rtLzcnt;
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

        // ── ③ OS XSTATE 门：硬件有 AVX 不代表当前 OS 已启用 YMM/ZMM/TILE 状态 ──
        bool osxsave = Bit(leaf1Ecx, 27);
        ulong xstate = GetEnabledXStateFeaturesMask();
        bool osAvx;
        bool osAvx512;
        bool osAmx;
        if (OperatingSystem.IsWindows())
        {
            if (xstate != 0)
            {
                osAvx = (xstate & 0x6UL) == 0x6UL;          // XMM + YMM
                osAvx512 = (xstate & 0xE6UL) == 0xE6UL;     // XMM/YMM + opmask/ZMM
                osAmx = (xstate & 0x60000UL) == 0x60000UL;  // XTILECFG + XTILEDATA
            }
            else
            {
                // GetEnabledXStateFeatures 不可用（极旧 Windows）：按 CPUID OSXSAVE 判定。
                osAvx = osxsave && HasAvx;
                osAvx512 = osAvx;
                osAmx = false;
            }
        }
        else
        {
            // 非 Windows 无 XGETBV 封装；OSXSAVE 置位表示 OS 已开 XSAVE。
            osAvx = osxsave && HasAvx;
            osAvx512 = osAvx;
            osAmx = false;
        }

        CanUseAvx    = HasAvx && osAvx;
        CanUseAvx2   = HasAvx2 && osAvx;
        CanUseAvx512 = HasAvx512F && osAvx512;
        CanUseAmx    = HasAmxTile && osAmx;
    }

    private static ulong GetEnabledXStateFeaturesMask()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try { return GetEnabledXStateFeatures(); }
        catch { return 0; }
    }

    [DllImport("kernel32.dll")]
    private static extern ulong GetEnabledXStateFeatures();

    private static bool Bit(int value, int bit) => ((value >> bit) & 1) != 0;

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
        if (HasAvx10v2)   { BestSimdTag = "avx10.2"; }
        else if (HasAvx10v1) { BestSimdTag = "avx10.1"; }
        else if (HasAvx512F) { BestSimdTag = "avx512"; }
        else if (HasAvx2)    { BestSimdTag = "avx2"; }
        else if (HasAvx)     { BestSimdTag = "avx"; }
        else if (HasSse41)   { BestSimdTag = "sse4"; }
        else if (HasSse2)    { BestSimdTag = "sse2"; }
        else if (HasSve2)    { BestSimdTag = "sve2"; }
        else if (HasSve)     { BestSimdTag = "sve"; }
        else if (HasAdvSimd) { BestSimdTag = "neon"; }
        else                 { BestSimdTag = "none"; }

        // 外部工具选路必须看“硬件 + OS 状态”双重门；AOT 进程内不可执行
        // 不等于外部 AVX2 二进制不能跑。
        if (HasAvx10v2 && CanUseAvx)       ExternalBestSimdTag = "avx10.2";
        else if (HasAvx10v1 && CanUseAvx)  ExternalBestSimdTag = "avx10.1";
        else if (CanUseAvx512)             ExternalBestSimdTag = "avx512";
        else if (CanUseAvx2)               ExternalBestSimdTag = "avx2";
        else if (CanUseAvx)                ExternalBestSimdTag = "avx";
        else if (HasSse41)                  ExternalBestSimdTag = "sse4";
        else if (HasSse2)                   ExternalBestSimdTag = "sse2";
        else if (HasSve2)                   ExternalBestSimdTag = "sve2";
        else if (HasSve)                    ExternalBestSimdTag = "sve";
        else if (HasAdvSimd)                ExternalBestSimdTag = "neon";
        else                                ExternalBestSimdTag = "none";
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
        if (HasAvx10v2 && CanUseAvx) t.Add("avx10.2");
        if (HasAvx10v1 && CanUseAvx) t.Add("avx10");
        if (CanUseAvx512 && HasAvx512Vnni) t.Add("avx512_vnni");
        if (CanUseAvx512) t.Add("avx512");
        if (CanUseAvx2)   t.Add("avx2");
        if (CanUseAvx)    t.Add("avx");
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
        if (IsX64) sb.Append("x86-64 层级（硬件 CPUID）: v").Append((int)X86FeatureLevel).AppendLine();
        sb.Append("最优 SIMD（硬件）: ").AppendLine(BestSimdTag);
        if (IsX86 || IsX64)
        {
            sb.Append("进程内可用 SIMD: ").AppendLine(RuntimeBestSimdTag);
            sb.Append("外部工具可用 SIMD: ").AppendLine(ExternalBestSimdTag);
            if (!string.Equals(RuntimeBestSimdTag, BestSimdTag, StringComparison.Ordinal))
            {
                sb.AppendLine(IsAotRuntime
                    ? "说明: 当前为 NativeAOT 构建（.NET 11 基线 x86-64-v2），硬件 AVX/AVX2 不参与进程内内核；" +
                      "外部 ffmpeg/cjxl 等工具仍按硬件能力选择。"
                    : "说明: 当前进程运行时未启用全部硬件 SIMD（OS/JIT 配置限制）；外部工具仍按硬件能力选择。");
            }
            sb.AppendLine("── x86（硬件 CPUID）──");
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