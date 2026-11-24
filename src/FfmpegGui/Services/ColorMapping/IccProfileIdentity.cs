using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>Profile bytes and matrix-engine support are independent of container ICC capability.</summary>
public enum IccProfileKind
{
    Absent,
    MatrixTrc,
    LutOrUnsupported,
    MalformedOrUnreadable,
}

/// <summary>Immutable, byte-exact ICC identity. S1 validates structure; it does not execute LUTs.</summary>
public sealed class IccProfileIdentity
{
    private readonly byte[]? _bytes;
    private readonly Dictionary<string, (int Offset, int Size)>? _tags;
    private readonly double[]? _matrix;

    internal IccProfileIdentity(IccProfileKind kind, string? issueCode = null, string? detail = null,
        byte[]? bytes = null, Dictionary<string, (int Offset, int Size)>? tags = null,
        double[]? matrix = null, string? externalPath = null)
    {
        Kind = kind;
        IssueCode = issueCode;
        Detail = detail;
        _bytes = bytes;
        _tags = tags;
        _matrix = matrix;
        ExternalPath = externalPath;
        if (bytes == null) return;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        DeviceClass = IccProfileAnalyzer.FourCc(bytes, 12);
        DataSpace = IccProfileAnalyzer.FourCc(bytes, 16);
        Pcs = IccProfileAnalyzer.FourCc(bytes, 20);
        Version = $"{bytes[8]}.{bytes[9] >> 4}.{bytes[9] & 15}";
    }

    public static IccProfileIdentity Absent { get; } = new(IccProfileKind.Absent);
    public IccProfileKind Kind { get; }
    public string? IssueCode { get; }
    public string? Detail { get; }
    public string? Sha256 { get; }
    public string? DeviceClass { get; }
    public string? DataSpace { get; }
    public string? Pcs { get; }
    public string? Version { get; }
    public string? ExternalPath { get; }
    public bool IsValid => Kind is IccProfileKind.MatrixTrc or IccProfileKind.LutOrUnsupported;
    public bool IsRgbImageProfile => DataSpace == "RGB " && DeviceClass is "scnr" or "mntr" or "prtr";
    // A reverse-only LUT cannot describe the source pixels to a display CMM.
    public bool CanDescribeRgbSource => IsRgbImageProfile && (_matrix != null
        || HasTag("A2B0") || HasTag("A2B1") || HasTag("A2B2")
        || HasTag("D2B0") || HasTag("D2B1") || HasTag("D2B2") || HasTag("D2B3"));

    public ReadOnlySpan<byte> ProfileBytes => _bytes;
    public byte[] CopyProfileBytes() => ProfileBytes.ToArray();
    public bool HasTag(string signature) => _tags?.ContainsKey(signature) == true;
    public ReadOnlySpan<byte> GetTagBytes(string signature)
        => _tags != null && _tags.TryGetValue(signature, out var t)
            ? ProfileBytes.Slice(t.Offset, t.Size) : default;

    public ColorSpaceDescriptor? CreateMatrixTrcDescriptor(string? descriptionHint = null)
    {
        if (Kind != IccProfileKind.MatrixTrc || _matrix == null) return null;
        var curve = IccProfileAnalyzer.ReadSharedCurve(this);
        if (!curve.HasValue) return null;
        var d = ColorSpaceDescriptor.FromIcc((double[])_matrix.Clone(), curve.Value,
            $"ICC:{(string.IsNullOrWhiteSpace(descriptionHint) ? "profile" : descriptionHint)}", ExternalPath);
        ColorSpaceIdentify.RefineKnownSpace(d, curve.Value);
        d.Confidence = DescriptorConfidence.IccMetric;
        return d;
    }
}

internal static class IccProfileAnalyzer
{
    // Bound untrusted metadata before allocation. Typical matrix/LUT profiles are KiB to a few MiB.
    private const int MaxProfileBytes = 32 * 1024 * 1024;
    private const int MaxTags = 4096;
    private static readonly string[] MatrixTags = { "rXYZ", "gXYZ", "bXYZ" };
    private static readonly string[] TrcTags = { "rTRC", "gTRC", "bTRC" };

    internal static IccProfileIdentity FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return IccProfileIdentity.Absent;
        try
        {
            path = Path.GetFullPath(path);
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxProfileBytes) return Invalid("IccProfileSizeLimit", "ICC exceeds the profile size limit");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return Analyze(bytes, path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Invalid("IccProfileMissing", "ICC file does not exist");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return Invalid("IccProfileUnreadable", $"cannot read ICC file ({ex.GetType().Name})");
        }
    }

    internal static IccProfileIdentity FromBase64(string? value)
    {
        if (value == null) return IccProfileIdentity.Absent;
        var text = value.AsSpan().Trim();
        if (text.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)) text = text[7..];
        if (text.Length > ((MaxProfileBytes + 2L) / 3) * 4 + 4096)
            return Invalid("IccProfileSizeLimit", "ICC exceeds the profile size limit");
        try { return Analyze(Convert.FromBase64String(text.ToString())); }
        catch (FormatException) { return Invalid("IccProfileMalformed", "ICC field is not valid base64"); }
    }

    internal static IccProfileIdentity FromBytes(ReadOnlySpan<byte> bytes)
        => bytes.Length <= MaxProfileBytes ? Analyze(bytes.ToArray())
            : Invalid("IccProfileSizeLimit", "ICC exceeds the profile size limit");

    private static IccProfileIdentity Invalid(string code, string detail)
        => new(IccProfileKind.MalformedOrUnreadable, code, detail);

    private static IccProfileIdentity Malformed(string detail) => Invalid("IccProfileMalformed", detail);

    // Structural acceptance is independent of engine support and writer-version policy.
    // In particular, opaque LUTs and legacy headers need not be matrix-executable to be carried.
    internal static bool ValidateStructure(ReadOnlySpan<byte> bytes, out string? detail)
    {
        detail = ReadTagDirectory(bytes, out var tags);
        if (detail == null)
            detail = ValidateTagPayloads(bytes, tags, ChannelCount(FourCc(bytes, 16)),
                ChannelCount(FourCc(bytes, 20)), out _);
        return detail == null;
    }

    private static string? ReadTagDirectory(ReadOnlySpan<byte> bytes,
        out Dictionary<string, (int Offset, int Size)> tags)
    {
        tags = new(StringComparer.Ordinal);
        if (bytes.Length < 132 || bytes.Length > MaxProfileBytes) return "invalid ICC size or missing tag table";
        if (FourCc(bytes, 36) != "acsp") return "missing ICC header signature";
        uint declaredSize = U32(bytes, 0);
        if (declaredSize < 132 || declaredSize > bytes.Length) return "invalid declared ICC size";
        uint count = U32(bytes, 128);
        if (count is 0 or > MaxTags || 132UL + count * 12UL > declaredSize) return "invalid ICC tag count";
        uint tableEnd = 132 + count * 12;
        var intervals = new List<(int Offset, int Size)>((int)count);
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + i * 12;
            string sig = FourCc(bytes, entry);
            uint off = U32(bytes, entry + 4), len = U32(bytes, entry + 8);
            // Private raw tags need only a readable type signature here (lcms uses "data123", 7 bytes).
            // Known tags retain their 8-byte header and stronger payload minima below.
            if (!IsSignature(bytes.Slice(entry, 4)) || off < tableEnd || (off & 3) != 0
                || len < (IsKnownTagSignature(sig) ? 8U : 4U)
                || (ulong)off + len > declaredSize || !IsSignature(bytes.Slice((int)off, 4))
                || !tags.TryAdd(sig, ((int)off, (int)len)))
                return "invalid, duplicate, unaligned or out-of-bounds ICC tag";
            intervals.Add(((int)off, (int)len));
        }
        // ICC allows multiple signatures to share exactly the same complete payload.
        // Only partial aliases are invalid; directory order has no semantic significance.
        intervals.Sort();
        for (int i = 1; i < intervals.Count; i++)
        {
            var a = intervals[i - 1];
            var b = intervals[i];
            if (a != b && a.Offset + a.Size > b.Offset) return "ICC tags partially overlap";
        }
        return null;
    }

    private static string? ValidateTagPayloads(ReadOnlySpan<byte> bytes,
        Dictionary<string, (int Offset, int Size)> tags, int channels, int pcsChannels, out bool hasLut)
    {
        hasLut = false;
        foreach (var (sig, loc) in tags)
        {
            var tag = bytes.Slice(loc.Offset, loc.Size);
            bool forward = IsTransformTag(sig, "A2B", '2') || IsTransformTag(sig, "D2B", '3');
            bool reverse = IsTransformTag(sig, "B2A", '2') || IsTransformTag(sig, "B2D", '3');
            if (forward || reverse)
            {
                hasLut = true;
                int input = forward ? channels : pcsChannels;
                int output = reverse ? channels : pcsChannels;
                // Unknown device spaces stay opaque. The matrix engine separately rejects
                // unsupported models; structural extraction must not invent channel counts.
                if (input != 0 && output != 0
                    && !HasValidLutLayout(tag, input, output, reverse, sig[0] == 'D' || sig.StartsWith("B2D", StringComparison.Ordinal)))
                    return $"invalid layout of ICC transform tag {sig}";
            }
            else if (sig is "pre0" or "pre1" or "pre2" or "gamt")
            {
                if (!HasValidOptionalLutLayout(tag, sig, pcsChannels)) return $"invalid layout of ICC transform tag {sig}";
            }
            else if (sig is "rXYZ" or "gXYZ" or "bXYZ" or "wtpt")
            {
                if (tag.Length < 20 || FourCc(tag, 0) != "XYZ ") return $"invalid ICC XYZ tag {sig}";
            }
            else if (sig is "rTRC" or "gTRC" or "bTRC" or "kTRC")
            {
                if (CurveLength(tag) < 0) return $"invalid ICC curve tag {sig}";
            }
        }
        return null;
    }

    private static IccProfileIdentity Analyze(byte[] bytes, string? externalPath = null)
    {
        if (bytes.Length > MaxProfileBytes) return Invalid("IccProfileSizeLimit", "ICC exceeds the profile size limit");
        if (bytes.Length < 132 || FourCc(bytes, 36) != "acsp") return Malformed("missing ICC header/tag table");
        uint declaredSize = U32(bytes, 0);
        if (declaredSize < 132 || declaredSize > bytes.Length) return Malformed("invalid declared ICC size");
        string cls = FourCc(bytes, 12), data = FourCc(bytes, 16), pcs = FourCc(bytes, 20);
        int channels = ChannelCount(data), pcsChannels = ChannelCount(pcs);
        if (bytes[8] is not (2 or 4) || cls is not ("scnr" or "mntr" or "prtr" or "spac" or "link" or "abst" or "nmcl")
            || channels == 0 || (cls == "link" ? pcsChannels == 0 : pcs is not ("XYZ " or "Lab "))
            || U32(bytes, 64) > 3)
            return Malformed("invalid ICC version, device class, color space, PCS or rendering intent");
        string? structureIssue = ReadTagDirectory(bytes, out var tags);
        if (structureIssue != null) return Malformed(structureIssue);
        structureIssue = ValidateTagPayloads(bytes, tags, channels, pcsChannels, out bool hasLut);
        if (structureIssue != null) return Malformed(structureIssue);
        bool hasRgbModel = data == "RGB " && Array.TrueForAll(MatrixTags, tags.ContainsKey)
            && Array.TrueForAll(TrcTags, tags.ContainsKey);
        bool hasGrayModel = data == "GRAY" && tags.ContainsKey("kTRC");
        bool hasNamedColors = cls == "nmcl" && (tags.ContainsKey("ncl2") || tags.ContainsKey("ncol"));
        if (!hasLut && !hasRgbModel && !hasGrayModel && !hasNamedColors) return Malformed("ICC has no color transform");

        double[]? matrix = null;
        if (hasRgbModel && pcs == "XYZ " && cls is "scnr" or "mntr" or "prtr")
        {
            matrix = new double[9];
            for (int c = 0; c < 3; c++)
            {
                int off = tags[MatrixTags[c]].Offset + 8;
                for (int r = 0; r < 3; r++) matrix[3 * r + c] = S15Fixed16(bytes, off + 4 * r);
            }
            double det = matrix[0] * (matrix[4] * matrix[8] - matrix[5] * matrix[7])
                       - matrix[1] * (matrix[3] * matrix[8] - matrix[5] * matrix[6])
                       + matrix[2] * (matrix[3] * matrix[7] - matrix[4] * matrix[6]);
            if (Math.Abs(det) < 1e-10) return Malformed("ICC colorant matrix is singular");
        }
        // Never use a hybrid profile's incidental matrix/TRC as a replacement for its LUT.
        bool supported = matrix != null && !hasLut
            && ReadSharedCurve(bytes, tags) != null;
        return new(supported ? IccProfileKind.MatrixTrc : IccProfileKind.LutOrUnsupported,
            bytes: bytes, tags: tags, matrix: matrix, externalPath: externalPath);
    }

    private static bool IsTransformTag(string sig, string prefix, char last)
        => sig.StartsWith(prefix, StringComparison.Ordinal) && sig[3] >= '0' && sig[3] <= last;

    private static int ChannelCount(string sig) => sig switch
    {
        "GRAY" => 1,
        "RGB " or "XYZ " or "Lab " or "Luv " or "YCbr" or "Yxy " or "HSV " or "HLS " or "CMY " => 3,
        "CMYK" => 4,
        _ when sig.EndsWith("CLR", StringComparison.Ordinal) => sig[0] switch
        {
            >= '2' and <= '9' => sig[0] - '0',
            >= 'A' and <= 'F' => sig[0] - 'A' + 10,
            _ => 0,
        },
        _ => 0,
    };

    // Optional preview/gamut metadata is never an executable source transform here.
    // ICC.1:2010 9.2.38-40 permits mAB only for pre0; the other forms use mBA or legacy LUTs.
    private static bool HasValidOptionalLutLayout(ReadOnlySpan<byte> tag, string signature, int pcsChannels)
    {
        if (tag.Length < 12 || pcsChannels is < 1 or > 15 || tag[8] != pcsChannels) return false;
        string type = FourCc(tag, 0);
        if (type is not ("mft1" or "mft2" or "mBA ") && !(signature == "pre0" && type == "mAB ")) return false;
        int output = tag[9];
        // The standard gamut output is one channel. Preserve lcms's bounded PCS=3 legacy
        // payload as opaque metadata, without granting it a source-transform capability.
        if (signature == "gamt" ? output != 1 && (pcsChannels != 3 || output != 3) : output != pcsChannels) return false;
        return HasValidLutLayout(tag, pcsChannels, output, type == "mBA ", false);
    }

    private static bool HasValidLutLayout(ReadOnlySpan<byte> tag, int input, int output, bool reverse, bool mpe)
    {
        string type = FourCc(tag, 0);
        if (mpe)
        {
            if (type != "mpet" || tag.Length < 16 || U16(tag, 8) != input || U16(tag, 10) != output) return false;
            uint n = U32(tag, 12);
            ulong start = 16UL + n * 8UL;
            if (n == 0 || start > (ulong)tag.Length) return false;
            for (int i = 0; i < n; i++)
            {
                uint off = U32(tag, 16 + 8 * i), len = U32(tag, 20 + 8 * i);
                if (off < start || (off & 3) != 0 || len < 12 || (ulong)off + len > (ulong)tag.Length) return false;
            }
            return true;
        }
        if (tag.Length < 12 || tag[8] != input || tag[9] != output) return false;
        if (type is "mft1" or "mft2")
        {
            int start = type == "mft1" ? 48 : 52;
            if (tag.Length < start || tag[10] < 2) return false;
            int inEntries = type == "mft1" ? 256 : U16(tag, 48);
            int outEntries = type == "mft1" ? 256 : U16(tag, 50);
            if (inEntries is < 2 or > 4096 || outEntries is < 2 or > 4096) return false;
            long cells = output;
            for (int i = 0; i < input; i++)
            {
                cells *= tag[10];
                if (cells > tag.Length) return false;
            }
            return start + (input * inEntries + cells + output * outEntries) * (type == "mft1" ? 1 : 2) <= tag.Length;
        }
        if (type != (reverse ? "mBA " : "mAB ") || tag.Length < 32) return false;
        Span<int> offsets = stackalloc int[5];
        for (int i = 0; i < offsets.Length; i++)
        {
            uint off = U32(tag, 12 + 4 * i);
            if (off != 0 && (off < 32 || (off & 3) != 0 || off >= tag.Length)) return false;
            offsets[i] = (int)off;
        }
        if (offsets[0] == 0 || (offsets[1] == 0) != (offsets[2] == 0) || (offsets[3] == 0) != (offsets[4] == 0)
            || (offsets[3] == 0 && input != output)) return false;
        int matrixChannels = reverse ? input : output;
        if (offsets[1] != 0 && matrixChannels != 3) return false;
        for (int i = 0; i < offsets.Length; i++)
        {
            int off = offsets[i], end = tag.Length;
            if (off == 0) continue;
            foreach (int other in offsets)
                if (other > off && other < end) end = other;
            var stage = tag.Slice(off, end - off);
            if (i == 1)
            {
                if (stage.Length < 48) return false;
            }
            else if (i == 3)
            {
                if (stage.Length < 20 || stage[16] is not (1 or 2)) return false;
                long cells = output * stage[16];
                for (int j = 0; j < input; j++)
                {
                    if (stage[j] < 2) return false;
                    cells *= stage[j];
                    if (cells > stage.Length) return false;
                }
                if (20 + cells > stage.Length) return false;
            }
            else
            {
                int channels = i == 4 ? (reverse ? output : input) : matrixChannels;
                for (int j = 0; j < channels; j++)
                {
                    int length = CurveLength(stage);
                    if (length < 0) return false;
                    if (j == channels - 1) break;
                    int aligned = (length + 3) & ~3;
                    if (aligned > stage.Length) return false;
                    stage = stage[aligned..];
                }
            }
        }
        return true;
    }

    private static int CurveLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return -1;
        ulong required = FourCc(data, 0) switch
        {
            "curv" => 12UL + U32(data, 8) * 2UL,
            "para" => U16(data, 8) switch { 0 => 16UL, 1 => 24UL, 2 => 28UL, 3 => 32UL, 4 => 40UL, _ => 0 },
            _ => 0,
        };
        return required != 0 && required <= (ulong)data.Length ? (int)required : -1;
    }

    internal static TransferCurve? ReadSharedCurve(IccProfileIdentity identity)
        => ReadSharedCurve(identity.GetTagBytes("rTRC"), identity.GetTagBytes("gTRC"), identity.GetTagBytes("bTRC"));

    private static TransferCurve? ReadSharedCurve(byte[] bytes, Dictionary<string, (int Offset, int Size)> tags)
    {
        var r = tags["rTRC"]; var g = tags["gTRC"]; var b = tags["bTRC"];
        return ReadSharedCurve(bytes.AsSpan(r.Offset, r.Size), bytes.AsSpan(g.Offset, g.Size), bytes.AsSpan(b.Offset, b.Size));
    }

    private static TransferCurve? ReadSharedCurve(ReadOnlySpan<byte> red, ReadOnlySpan<byte> green, ReadOnlySpan<byte> blue)
    {
        int length = CurveLength(red);
        if (length < 0 || length != CurveLength(green) || length != CurveLength(blue)) return null;
        var r = red[..length];
        if (!r.SequenceEqual(green[..length]) || !r.SequenceEqual(blue[..length])) return null;
        if (FourCc(r, 0) == "curv")
        {
            int n = (int)U32(r, 8);
            if (n == 0) return TransferCurve.Linear;
            if (n == 1)
            {
                double gamma = U16(r, 12) / 256.0;
                return gamma > 0 ? Claim(TransferCurve.FromParametric(gamma, 1, 0, 0, 0, 0, 0, "ICC curv gamma")) : null;
            }
            var lut = new float[n];
            for (int i = 0; i < n; i++) lut[i] = U16(r, 12 + i * 2) / 65535f;
            return lut[0] < lut[^1] ? TransferCurve.FromTable(lut, $"ICC curv[{n}]") : null;
        }
        int fn = U16(r, 8);
        double g = S15Fixed16(r, 12);
        double a = fn == 0 ? 1 : S15Fixed16(r, 16);
        double b = fn == 0 ? 0 : S15Fixed16(r, 20);
        if (g <= 0 || a <= 0) return null;
        double c = fn >= 3 ? S15Fixed16(r, 24) : 0;
        double d = fn switch { 0 => 0, 1 or 2 => -b / a, _ => S15Fixed16(r, 28) };
        double e = fn == 2 ? S15Fixed16(r, 24) : fn == 4 ? S15Fixed16(r, 32) : 0;
        double f = fn == 2 ? e : fn == 4 ? S15Fixed16(r, 36) : 0;
        // The existing matrix kernel normalizes black to zero and has no constant-toe inverse.
        // Keep those profiles opaque rather than round-tripping through an approximation.
        if (c < 0 || d >= 1 || (d > 0 && c == 0) || e != 0 || f != 0 || a * Math.Max(0, d) + b < -1e-5)
            return null;
        return Claim(TransferCurve.FromParametric(g, a, b, c, d, e, f, $"ICC para{fn}"));
    }

    private static TransferCurve Claim(TransferCurve c) => TransferCurve.TryClaimNamed(c) ?? c;
    // Tag signatures recognized by the vendored lcms tag registry, including legacy/vendor tags.
    // Unknown private signatures stay opaque; a standard tag cannot evade its header minimum.
    private static bool IsKnownTagSignature(string sig) => sig is
        "A2B0" or "A2B1" or "A2B2" or "B2A0" or "B2A1" or "B2A2"
        or "D2B0" or "D2B1" or "D2B2" or "D2B3" or "B2D0" or "B2D1" or "B2D2" or "B2D3"
        or "rXYZ" or "gXYZ" or "bXYZ" or "rTRC" or "gTRC" or "bTRC" or "kTRC"
        or "pre0" or "pre1" or "pre2" or "gamt" or "wtpt" or "bkpt" or "lumi"
        or "calt" or "targ" or "chad" or "chrm" or "clro" or "clrt" or "clot" or "ciis"
        or "cprt" or "crdi" or "data" or "dtim" or "dmnd" or "dmdd" or "devs"
        or "meas" or "ncol" or "ncl2" or "resp" or "rig0" or "rig2" or "desc" or "dscm"
        or "pseq" or "psid" or "psd0" or "psd1" or "psd2" or "psd3" or "ps2s" or "ps2i"
        or "scrd" or "scrn" or "tech" or "bfd " or "vued" or "view" or "vcgt" or "meta"
        or "cicp" or "arts" or "MHC2";

    private static bool IsSignature(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) if (b < 32 || b > 126) return false;
        return true;
    }
    internal static string FourCc(ReadOnlySpan<byte> b, int offset) => Encoding.ASCII.GetString(b.Slice(offset, 4));
    private static ushort U16(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt16BigEndian(b[offset..]);
    private static uint U32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32BigEndian(b[offset..]);
    private static double S15Fixed16(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadInt32BigEndian(b[offset..]) / 65536.0;
}
