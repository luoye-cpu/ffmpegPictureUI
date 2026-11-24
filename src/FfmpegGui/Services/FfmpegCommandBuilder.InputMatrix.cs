using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FfmpegGui.Models;

namespace FfmpegGui.Services;

public static partial class FfmpegCommandBuilder
{
    internal enum InputMatrixUse { None, Convert, NotApplicable, UnsupportedToken, Reject }

    internal readonly record struct InputMatrixDirective(InputMatrixUse Use, string? Matrix, string? Range, string? Diagnostic)
    {
        // Do not set out_range here: the original range describes Y′CbCr, not the RGB
        // produced by this stage. The downstream encoder chooses its own output range.
        public string? BuildFilter(string rgbFormat) => Use == InputMatrixUse.Convert
            ? $"scale=in_color_matrix={Matrix}{(Range != null ? $":in_range={Range}" : "")},format={rgbFormat}"
            : null;
    }

    internal static InputMatrixDirective ResolveInputMatrix(FfmpegOptions? options, string inputPath)
    {
        var token = ColorSpaceRegistry.NormalizeMatrixToken(options?.ColorMatrix);
        if (string.IsNullOrWhiteSpace(token) || token == "auto") return default;

        var matrix = ColorSpaceRegistry.ToFfmpegInputMatrixName(token);
        if (matrix == null)
            return new(InputMatrixUse.UnsupportedToken, null, null,
                $"[matrix] --color-matrix={token} ignored for pixel conversion: unsupported YUV coefficients");

        // The original file in a pipe is a metadata source, not the decoded pipe's pixel layout.
        var meta = inputPath == "-" ? default : ProbeInputColorMetadata(inputPath);
        return ResolveInputMatrixForLayout(token, matrix, meta, inputPath);
    }

    internal static InputMatrixDirective ResolveInputMatrix(FfmpegOptions options, string inputPath, ColorMetadata selectedStream)
    {
        var token = ColorSpaceRegistry.NormalizeMatrixToken(options.ColorMatrix);
        if (string.IsNullOrWhiteSpace(token) || token == "auto") return default;
        var matrix = ColorSpaceRegistry.ToFfmpegInputMatrixName(token);
        return matrix == null ? new(InputMatrixUse.UnsupportedToken, null, null,
            $"[matrix] --color-matrix={token} ignored for pixel conversion: unsupported YUV coefficients")
            : ResolveInputMatrixForLayout(token, matrix, selectedStream, inputPath);
    }

    private static InputMatrixDirective ResolveInputMatrixForLayout(string token, string matrix, ColorMetadata meta, string inputPath)
    {
        bool yuv = IsYcbcrPixelFormat(meta.pixFmt);
        if (!yuv)
            return new(InputMatrixUse.NotApplicable, null, null, $"[matrix] --color-matrix={token} ignored for pixel conversion: "
                + (meta.pixFmt is { Length: > 0 } ? $"decoded layout {meta.pixFmt} is not YUV" : "decoded YUV layout not established"));
        if (matrix == "rgb")
            return new(InputMatrixUse.Reject, null, null,
                "[matrix] COLOR_INPUT_MATRIX_IDENTITY_ON_YUV: RGB/GBR identity cannot interpret ordinary YUV planes");
        if (InputIsGroupDerived(inputPath))
            return new(InputMatrixUse.Reject, null, null,
                "[matrix] COLOR_INPUT_MATRIX_GROUP_UNSUPPORTED: explicit input matrix is not supported for a derived stream group");
        return new(InputMatrixUse.Convert, matrix, meta.colorRange, null);
    }

    internal static string? PngNarrowRangeFilter(string inputPath)
    {
        if (!ProbeInputColorMetadata(inputPath).NarrowRangePng) return null;
        // Core PNG decoders can signal TV range without expanding RGB samples.
        // Work at the native 8/16-bit RGB depth; alpha is never range-expanded.
        const string e = "clip(floor((val-16*if(gt(maxval,255),256,1))*maxval/(219*if(gt(maxval,255),256,1))+0.5),0,maxval)";
        return $"lutrgb=r='{e}':g='{e}':b='{e}',setparams=range=pc";
    }

    internal sealed class InputMatrixContractException(string reason) : InvalidOperationException(reason);

    // A container colr atom can disagree with the decoded picture VUI. Keep the scope bounded:
    // inspect the first decoded frames of video containers before overriding decoder metadata.
    private readonly record struct VideoColorEvidence(string? Primaries, string? Transfer, string? Matrix, string? Range,
        string? Conflict, bool VariesWithinSample);

    private static async Task<VideoColorEvidence> ReadVideoColorEvidenceAsync(string inputPath, CancellationToken ct)
    {
        if (Path.GetExtension(inputPath).ToLowerInvariant() is not (".mp4" or ".mov" or ".m4v" or ".mkv" or ".webm")) return default;
        if (FindFfprobe() is not { } ffprobe) return default;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var info = new ProcessStartInfo(ffprobe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in new[] { "-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#12",
                "-show_entries", "stream=color_space,color_range,color_primaries,color_transfer:frame=color_space,color_range,color_primaries,color_transfer",
                "-of", "json", "-i", inputPath }) info.ArgumentList.Add(arg);
            using var p = Process.Start(info);
            if (p == null) return default;
            using var kill = timeout.Token.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr, p.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (p.ExitCode != 0) return default;
            using var doc = JsonDocument.Parse(await stdout.ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0
                || !doc.RootElement.TryGetProperty("frames", out var frames) || frames.GetArrayLength() == 0) return default;
            static string? Known(JsonElement e, string field) => e.TryGetProperty(field, out var v)
                && v.ValueKind == JsonValueKind.String && v.GetString() is { } s
                && s is not ("" or "unknown" or "unspecified" or "reserved" or "N/A") ? s : null;
            string? conflict = null;
            bool varies = false;
            var fields = new[] { "color_primaries", "color_transfer", "color_space", "color_range" };
            foreach (var field in fields)
            {
                var stream = Known(streams[0], field);
                var first = Known(frames[0], field);
                if (stream != null && first != null && stream != first)
                    conflict ??= $"{field}: stream {stream} versus decoded frame {first}";
            }
            var containerDisagrees = conflict != null;
            string? PreferFirstFrame(string field)
            {
                var stream = Known(streams[0], field);
                var first = Known(frames[0], field);
                foreach (var frame in frames.EnumerateArray())
                {
                    var other = Known(frame, field);
                    if (other != null && first != null && other != first)
                    {
                        varies = true;
                        conflict ??= $"{field}: decoded frames disagree ({first} versus {other})";
                    }
                }
                // Once this container declaration is contradicted, do not fill missing
                // P/T from that same declaration (e.g. a false PQ source classification).
                return first ?? (containerDisagrees && (field is "color_primaries" or "color_transfer") ? "unknown" : stream);
            }
            var primaries = PreferFirstFrame("color_primaries");
            var transfer = PreferFirstFrame("color_transfer");
            var matrix = PreferFirstFrame("color_space");
            var range = PreferFirstFrame("color_range");
            return new(primaries, transfer, matrix, range, conflict, varies);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (JsonException) { }
        ct.ThrowIfCancellationRequested();
        return default;
    }

    private static bool IsYcbcrPixelFormat(string? pixFmt)
    {
        // Positive families only: gray, pal8, GBR and undecoded hardware surfaces
        // are not Y′CbCr just because they did not match an RGB prefix.
        return pixFmt is { Length: > 0 } && (pixFmt.StartsWith("yuv", StringComparison.Ordinal)
            || pixFmt is "uyvy422" or "yuyv422" or "yvyu422" or "uyyvyy411" or "vyuy422"
                or "nv12" or "nv21" or "nv16" or "nv20le" or "nv20be" or "nv24" or "nv42"
                or "p010le" or "p010be" or "p012le" or "p012be" or "p016le" or "p016be"
                or "p210le" or "p210be" or "p212le" or "p212be" or "p216le" or "p216be"
                or "p410le" or "p410be" or "p412le" or "p412be" or "p416le" or "p416be"
                or "ayuv64le" or "ayuv64be" or "vuya" or "vuyx" or "uyva"
                or "y210le" or "y210be" or "y212le" or "y212be" or "y216le" or "y216be"
                or "xv30le" or "xv30be" or "xv36le" or "xv36be" or "xv48le" or "xv48be");
    }
}
