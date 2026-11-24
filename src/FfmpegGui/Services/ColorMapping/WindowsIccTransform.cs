using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>Executes ICC profile transforms through the Windows color management module.</summary>
internal static unsafe class WindowsIccTransform
{
    private const string Library = "mscms.dll";
    private const uint ProfileMemory = 2, ProfileRead = 1, OpenExisting = 3;
    private const uint RelativeColorimetric = 1, BestMode = 3, IndexDontCare = 0;
    private const uint DontUseEmbeddedWcsProfiles = 1;
    // icm.h BM_16b_RGB = 0x000A: three 16-bit channels, no alpha.
    // The Windows ICC CMM reads/writes BGR WORDs for this format, despite the Learn byte table.
    private const uint Bm16bRgb = 0x000A;
    private const int BytesPerPixel = 6;
    private const int ScratchBatchBytes = 1024 * 1024;

    internal static bool IsAvailable { get; } = ProbeAvailability();

    /// <summary>Converts tightly packed RGB48LE rows. Input and output must not overlap.</summary>
    internal static void ConvertRgb48(ReadOnlySpan<byte> input, Span<byte> output,
        int width, int height, IccProfileIdentity src, IccProfileIdentity dst)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("Windows ICC color management is unavailable.");
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        if (!src.IsValid || !src.CanDescribeRgbSource || src.ProfileBytes.IsEmpty)
            throw new ArgumentException("Expected an ICC profile that can describe RGB source pixels.", nameof(src));
        if (!dst.IsValid || !dst.IsRgbImageProfile || dst.ProfileBytes.IsEmpty)
            throw new ArgumentException("Expected an RGB destination ICC profile.", nameof(dst));
        // This CMM can silently substitute a matrix for an unsupported relative MPE tag.
        // Decline that transform rather than report it as a faithful ICC conversion.
        if (src.HasTag("D2B1") || dst.HasTag("B2D1"))
            throw new NotSupportedException("Windows ICC color management does not support relative floating-point ICC LUTs.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        int rowBytes = checked(width * BytesPerPixel);
        int length = checked(rowBytes * height);
        int stride = checked(rowBytes + 3) & ~3;
        if (input.Length < length) throw new ArgumentException("Source RGB48 buffer is too short.", nameof(input));
        if (output.Length < length) throw new ArgumentException("Destination RGB48 buffer is too short.", nameof(output));
        input = input[..length];
        output = output[..length];
        if (input.Overlaps(output)) throw new ArgumentException("RGB48 buffers must not overlap.", nameof(output));

        nint source = OpenProfile(src, "source");
        try
        {
            nint target = OpenProfile(dst, "destination");
            try
            {
                nint* profiles = stackalloc nint[2] { source, target };
                uint* intents = stackalloc uint[2] { RelativeColorimetric, RelativeColorimetric };
                nint transform = CreateMultiProfileTransform(profiles, 2, intents, 2, BestMode, IndexDontCare);
                if (transform == 0) throw NativeError(nameof(CreateMultiProfileTransform));
                try { TranslateRgb48(transform, input, output, width, height, rowBytes, stride); }
                finally { _ = DeleteColorTransform(transform); }
            }
            finally { _ = CloseColorProfile(target); }
        }
        finally { _ = CloseColorProfile(source); }
    }

    private static void TranslateRgb48(nint transform, ReadOnlySpan<byte> input, Span<byte> output,
        int width, int height, int rowBytes, int stride)
    {
        // Odd widths need two padding bytes per native row. Use bounded scratch bands for the
        // channel reorder and padding; even widths can have native output written into place.
        int bandRows = Math.Min(height, Math.Max(1, ScratchBatchBytes / stride));
        int bandBytes = bandRows * stride;
        byte[] source = ArrayPool<byte>.Shared.Rent(bandBytes);
        byte[]? target = null;
        try
        {
            if (stride != rowBytes) target = ArrayPool<byte>.Shared.Rent(bandBytes);
            for (int y = 0; y < height;)
            {
                int rows = Math.Min(bandRows, height - y);
                var packedOutput = output.Slice(y * rowBytes, rows * rowBytes);
                var nativeOutput = target == null ? packedOutput : target.AsSpan(0, rows * stride);
                for (int i = 0; i < rows; i++)
                {
                    var paddedRow = source.AsSpan(i * stride, stride);
                    SwapRedBlue(input.Slice((y + i) * rowBytes, rowBytes), paddedRow[..rowBytes]);
                    paddedRow[rowBytes..].Clear();
                }
                fixed (byte* sourceBits = source, outputBits = nativeOutput)
                {
                    if (TranslateBitmapBits(transform, sourceBits, Bm16bRgb, (uint)width, (uint)rows, (uint)stride,
                        outputBits, Bm16bRgb, (uint)stride, 0, 0) == 0)
                        throw NativeError(nameof(TranslateBitmapBits));
                }
                for (int i = 0; i < rows; i++)
                    SwapRedBlue(nativeOutput.Slice(i * stride, rowBytes), packedOutput.Slice(i * rowBytes, rowBytes));
                y += rows;
            }
        }
        finally
        {
            if (target != null) ArrayPool<byte>.Shared.Return(target);
            ArrayPool<byte>.Shared.Return(source);
        }
    }

    // Supports the same slice at both ends so even-width native output can be swapped in place.
    private static void SwapRedBlue(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var src = MemoryMarshal.Cast<byte, ushort>(source);
        var dst = MemoryMarshal.Cast<byte, ushort>(target);
        for (int i = 0; i < src.Length; i += 3)
        {
            ushort red = src[i];
            dst[i] = src[i + 2];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = red;
        }
    }

    private static nint OpenProfile(IccProfileIdentity identity, string role)
    {
        ReadOnlySpan<byte> bytes = identity.ProfileBytes;
        fixed (byte* data = bytes)
        {
            var profile = new Profile { Type = ProfileMemory, Data = data, Length = (uint)bytes.Length };
            // OpenColorProfileW normally copies the bytes into the handle, but would prefer an
            // embedded Windows profile over the ICC tables. Explicitly opt out for that tag.
            bool embeddedWcs = identity.HasTag("MS00");
            nint handle = embeddedWcs
                ? WcsOpenColorProfileW(&profile, 0, 0, ProfileRead, 0, OpenExisting, DontUseEmbeddedWcsProfiles)
                : OpenColorProfileW(&profile, ProfileRead, 0, OpenExisting);
            if (handle == 0)
                throw NativeError($"{(embeddedWcs ? nameof(WcsOpenColorProfileW) : nameof(OpenColorProfileW))} ({role} ICC)");
            return handle;
        }
    }

    private static Win32Exception NativeError(string operation)
    {
        int code = Marshal.GetLastPInvokeError();
        return new Win32Exception(code, $"{operation} failed (Win32 error {code}).");
    }

    private static bool ProbeAvailability()
    {
        if (!OperatingSystem.IsWindows() || !BitConverter.IsLittleEndian
            || !NativeLibrary.TryLoad(Library, typeof(WindowsIccTransform).Assembly,
                DllImportSearchPath.System32, out nint module)) return false;
        try
        {
            return NativeLibrary.TryGetExport(module, nameof(OpenColorProfileW), out _)
                && NativeLibrary.TryGetExport(module, nameof(WcsOpenColorProfileW), out _)
                && NativeLibrary.TryGetExport(module, nameof(CloseColorProfile), out _)
                && NativeLibrary.TryGetExport(module, nameof(CreateMultiProfileTransform), out _)
                && NativeLibrary.TryGetExport(module, nameof(DeleteColorTransform), out _)
                && NativeLibrary.TryGetExport(module, nameof(TranslateBitmapBits), out _);
        }
        finally { NativeLibrary.Free(module); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Profile
    {
        internal uint Type;
        internal byte* Data;
        internal uint Length;
    }

    [DllImport(Library, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenColorProfileW(Profile* profile, uint access, uint shareMode, uint creationMode);

    [DllImport(Library, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WcsOpenColorProfileW(Profile* profile, nint camp, nint gmmp,
        uint access, uint shareMode, uint creationMode, uint flags);

    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CloseColorProfile(nint profile);

    [DllImport(Library, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateMultiProfileTransform(nint* profiles, uint profileCount,
        uint* intents, uint intentCount, uint flags, uint preferredCmm);

    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DeleteColorTransform(nint transform);

    [DllImport(Library, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int TranslateBitmapBits(nint transform, byte* source, uint inputFormat,
        uint width, uint height, uint inputStride, byte* target, uint outputFormat, uint outputStride,
        nint callback, nint callbackData);
}
