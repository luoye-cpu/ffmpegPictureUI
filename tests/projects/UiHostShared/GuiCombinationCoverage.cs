using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using FfmpegGui;
using FfmpegGui.Models;
using FfmpegGui.Services;
using FfmpegGui.Services.ColorMapping;

namespace UiHostShared;

/// <summary>
/// Model-based coverage for the static-image colour controls. The domains and
/// target templates are collected from the live MainWindow, then every member
/// of each disclosed Cartesian family is sent to the product planners/builders.
/// This is deliberately not called pixel-exhaustive: real pixel conversions are
/// a separate layer in verify-gui-color-combinations.ps1.
/// </summary>
public static class GuiCombinationCoverage
{
    private static MainWindow W = null!;
    private static string SrcDir = "";
    private static string WorkDir = "";
    private static readonly string RepoRoot = FindRepoRoot();

    private static T Find<T>(string name) where T : Control => W.FindControl<T>(name)
        ?? throw new InvalidOperationException("live GUI control missing: " + name);
    private static void Pump(int n = 6) { for (int i = 0; i < n; i++) Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "ffmpegPictureUI.sln"))) return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("ffmpegPictureUI.sln");
    }

    private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags BFS = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly ColorStrategy[] Strategies =
    {
        ColorStrategy.Recommended, ColorStrategy.CarryIcc,
        ColorStrategy.BakeCicpOnly, ColorStrategy.Manual
    };

    public static int Run(MainWindow window, string srcDir, string workDir, string reportPath)
    {
        W = window;
        SrcDir = srcDir;
        WorkDir = workDir;
        var report = new Report();
        try
        {
            typeof(MainWindow).GetField("_inputPath", BF)!.SetValue(W, Path.Combine(SrcDir, "src_8bit.png"));
            Find<CheckBox>("UseAdvancedCodec").IsChecked = false;
            Find<CheckBox>("LosslessCheck").IsChecked = false;
            Find<RadioButton>("IccModeNone").IsChecked = true;
            Find<CheckBox>("ColorGamutMapCheck").IsChecked = false;

            var inputs = new[]
            {
                new InputShape("rgba8_png", Path.Combine(SrcDir, "src_8bit.png"), false),
                new InputShape("yuv_tilegrid_heic", Path.Combine(RepoRoot,
                    "tests", "data", "synth_heic_tilegrid_plain.heic"), true)
            };
            foreach (var input in inputs)
            {
                if (!File.Exists(input.Path)) throw new FileNotFoundException("coverage input missing", input.Path);
                input.Meta = FfmpegCommandBuilder.ProbeInputColorMetadata(input.Path, null, CancellationToken.None);
            }

            // MainWindow refreshes hardware encoders after delayed runtime validation.
            // Wait for that same terminal state before capturing the live domains; otherwise
            // the probe nondeterministically sees the startup-only software subset.
            WaitOnUi(GpuCapabilityService.DetectAsync(), "GPU capability detection");
            WaitOnUi(GpuCapabilityService.ValidateEncodersAsync(), "GPU encoder runtime validation");
            SettleEncoderRefresh(window);
            report.GpuDomainFinalized = true;

            var states = CollectStates(window);
            report.FormatEncoderStates = states.Count;
            report.TargetTemplates = states.Sum(s => s.Templates.Count);
            report.States.AddRange(states.Select(s => new StateReport {
                Format = s.Format, Encoder = s.EncoderLabel,
                InputMatrices = s.InputMatrices, Ranges = s.Ranges, Engines = s.Engines, ToneMaps = s.ToneMaps,
                OutputMatrices = s.OutMatrices, Chromas = s.Chromas, BitDepths = s.BitDepths,
                IccVersions = s.IccVersions, LosslessValues = s.LosslessValues,
                Targets = s.Templates.Select(t => t.Name).ToArray()
            }));
            if (states.Count == 0 || report.TargetTemplates == 0)
                throw new InvalidOperationException("live GUI returned no format/encoder/target state");

            EnumerateUniversal(inputs, states, report);

            var pipe = typeof(QueueProcessor).GetMethod("BuildPipeColorArgs", BFS)
                ?? throw new MissingMethodException("QueueProcessor.BuildPipeColorArgs");
            var outputDir = Path.Combine(WorkDir, "gui-color-combination-plans");
            Directory.CreateDirectory(outputDir);

            foreach (var input in inputs)
            {
                foreach (var state in states)
                {
                    string outputPath = Path.Combine(outputDir, input.Name + "." + Extension(state.Format));
                    foreach (var template in state.Templates)
                    {
                        foreach (string engine in state.Engines)
                        foreach (string matrix in state.InputMatrices)
                        foreach (string range in state.Ranges)
                        foreach (string outMatrix in state.OutMatrices)
                        {
                            report.CoreEnumerated++;
                            var options = template.Options.DeepClone();
                            options.ColorEngine = engine;
                            options.ColorMatrix = NullAuto(matrix);
                            options.ColorRange = NullAuto(range);
                            options.ColorOutMatrix = NullAuto(outMatrix);
                            CheckOne(input, state, template.Name, options, outputPath,
                                Capability(options), report, pipe);
                        }
                    }

                    // Full strategy × tone-map × gamut-map product for every named target.
                    // Custom P/T targets are also fully crossed with Recommended + tone × gamut.
                    foreach (var template in state.Templates)
                    foreach (string tone in state.ToneMaps)
                    foreach (var strategy in Strategies)
                    foreach (bool gamut in new[] { false, true })
                    {
                        bool custom = template.Name.StartsWith("custom:", StringComparison.Ordinal);
                        if (custom && strategy != ColorStrategy.Recommended) continue;
                        report.PolicyEnumerated++;
                        var options = template.Options.DeepClone();
                        options.ColorToneMap = tone == "auto" ? "none" : tone;
                        options.ColorToneMapExplicit = tone == "none";
                        options.ColorStrategy = strategy;
                        options.ColorGamutMap = gamut ? "on" : "off";
                        // MainWindow shows an orange warning for an incomplete explicit triple,
                        // but does not block collection/enqueue. Keep these states executable in
                        // the model and send them to the real builders instead of pruning them.
                        if (strategy != ColorStrategy.Recommended &&
                            (string.IsNullOrWhiteSpace(options.ColorPrimaries) ||
                             string.IsNullOrWhiteSpace(options.ColorTrc) ||
                             string.IsNullOrWhiteSpace(options.ColorMatrix)))
                        {
                            report.UiWarningStates++;
                        }
                        CheckOne(input, state, template.Name, options, outputPath,
                            Capability(options), report, pipe);
                    }

                    // Full chroma × bit-depth × output-matrix × input-matrix product with
                    // target/policy held at the real GUI's basic-auto template.
                    var basicAuto = state.Templates.First(t => t.Name == "basic:auto");
                    foreach (string chroma in state.Chromas)
                    foreach (string depth in state.BitDepths)
                    foreach (string outMatrix in state.OutMatrices)
                    foreach (string matrix in state.InputMatrices)
                    {
                        report.GeometryEnumerated++;
                        if (string.Equals(ColorSpaceRegistry.NormalizeOutMatrixToken(outMatrix),
                            "rgb", StringComparison.OrdinalIgnoreCase) && chroma != "auto")
                        {
                            report.UiLockedStates++;
                            continue;
                        }
                        var options = basicAuto.Options.DeepClone();
                        options.Chroma = chroma;
                        options.BitDepth = depth == "auto" ? null : int.Parse(depth);
                        options.ColorOutMatrix = NullAuto(outMatrix);
                        options.ColorMatrix = NullAuto(matrix);
                        CheckOne(input, state, basicAuto.Name, options, outputPath,
                            Capability(options), report, pipe);
                    }
                }
            }
            long familyTotal = report.CoreEnumerated + report.PolicyEnumerated + report.GeometryEnumerated;
            long familyAccounted = report.CommandsBuilt + report.ExpectedRejects + report.UiLockedStates;
            if (familyTotal != familyAccounted)
                throw new InvalidOperationException($"command-family classification gap: {familyAccounted} != {familyTotal}");
        }
        catch (Exception ex)
        {
            Fail(report, "coverage harness", ex.ToString());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"GUI-COMBINATION states={report.FormatEncoderStates} templates={report.TargetTemplates} "
            + $"universal={report.UniversalEnumerated} universal-planned={report.UniversalPlanned} "
            + $"universal-ui-warnings={report.UniversalUiWarnings} universal-ui-locked={report.UniversalUiLocked} "
            + $"universal-input-reject={report.UniversalInputRejected} universal-output-reject={report.UniversalOutputRejected} "
            + $"core={report.CoreEnumerated} policy={report.PolicyEnumerated} geometry={report.GeometryEnumerated} "
            + $"built={report.CommandsBuilt} pipe={report.PipeCommandsBuilt} rejects={report.ExpectedRejects} "
            + $"ui-warnings={report.UiWarningStates} ui-locked={report.UiLockedStates} fail={report.FailureCount}");
        Console.WriteLine("GUI-COMBINATION-REPORT=" + reportPath);
        foreach (string failure in report.Failures.Take(20)) Console.WriteLine("[FAIL] " + failure);
        return report.FailureCount == 0 ? 0 : 1;
    }

    private static (string Use, string? Diagnostic, string? ScaleFilter) InputMatrix(
        FfmpegOptions options, string inputPath, FfmpegCommandBuilder.ColorMetadata meta)
    {
        var method = typeof(FfmpegCommandBuilder).GetMethods(BFS).Single(m =>
            m.Name == "ResolveInputMatrix" && m.GetParameters() is { Length: 3 } p
            && p[2].ParameterType == typeof(FfmpegCommandBuilder.ColorMetadata));
        object result = method.Invoke(null, new object?[] { options, inputPath, meta })!;
        var type = result.GetType();
        string use = type.GetProperty("Use")!.GetValue(result)!.ToString()!;
        string? diagnostic = type.GetProperty("Diagnostic")!.GetValue(result) as string;
        string? scaleFilter = type.GetMethod("BuildFilter")!.Invoke(result, new object[] { "rgb24" }) as string;
        return (use, diagnostic, scaleFilter);
    }

    private static void CheckOne(InputShape input, EncoderState state, string template,
        FfmpegOptions options, string outputPath, OutMatrixCapability cap,
        Report report, MethodInfo pipe)
    {
        string key = $"{input.Name}|{state.Format}|{state.EncoderLabel}|{template}|"
            + $"{options.ColorEngine}|{options.ColorMatrix ?? "auto"}|{options.ColorRange ?? "auto"}|"
            + $"{options.ColorOutMatrix ?? "auto"}|{options.Chroma}|{options.BitDepth?.ToString() ?? "auto"}|"
            + $"{options.ColorToneMap}|{options.ColorStrategy}|gamut={options.ColorGamutMap}";
        try
        {
            var inputMatrix = InputMatrix(options, input.Path, input.Meta);
            bool validYuvMatrix = input.DerivedGroup &&
                options.ColorMatrix is "bt709" or "bt2020nc" or "bt470bg" or "smpte170m";
            if (validYuvMatrix && inputMatrix.Use != "Convert")
                throw new InvalidOperationException($"valid YUV matrix did not resolve to Convert: {inputMatrix.Use} {inputMatrix.Diagnostic}");

            if (inputMatrix.Use == "Reject")
            {
                if (validYuvMatrix) throw new InvalidOperationException("valid YUV matrix was rejected");
                report.ExpectedRejects++;
                return;
            }
            if (cap == OutMatrixCapability.Rejected)
            {
                report.ExpectedRejects++;
                return;
            }

            string command = FfmpegCommandBuilder.BuildArguments(options, input.Path, outputPath);
            report.CommandsBuilt++;
            if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("empty command");
            if (input.DerivedGroup && inputMatrix.Use == "Convert")
            {
                if (!command.Contains("-filter_complex", StringComparison.Ordinal) || HasSimpleVf(command))
                    throw new InvalidOperationException("derived-group matrix conversion left the complex chain: " + command);

                // The external-encoder pipe is a separate product builder. Exercise its plain
                // PPM/PAM branch as well as the main command; BuildArguments alone cannot see it.
                bool external = options.EncoderBackend is EncoderBackend.Cjxl or EncoderBackend.Cjpegli;
                bool plainPipe = string.IsNullOrWhiteSpace(options.ColorPrimaries)
                    && string.IsNullOrWhiteSpace(options.ColorTrc)
                    && (string.IsNullOrWhiteSpace(options.ColorSpace) || options.ColorSpace == "auto");
                if (external && plainPipe)
                {
                    var clone = options.DeepClone();
                    object result = pipe.Invoke(null, new object?[]
                        { clone, input.Path, null, CancellationToken.None, input.Meta, false })!;
                    var tuple = (ITuple)result;
                    string pipeOutput = (string)tuple[1]!;
                    string? icc = tuple[2] as string;
                    if (!string.IsNullOrEmpty(icc) && File.Exists(icc)) File.Delete(icc);
                    report.PipeCommandsBuilt++;
                    if (!pipeOutput.Contains("-filter_complex", StringComparison.Ordinal) || HasSimpleVf(pipeOutput))
                        throw new InvalidOperationException("external pipe used -vf for a derived group: " + pipeOutput);
                }
            }
        }
        catch (Exception ex)
        {
            Fail(report, key, ex is TargetInvocationException tie ? tie.InnerException?.ToString() ?? ex.ToString() : ex.ToString());
        }
    }

    private static OutMatrixCapability Capability(FfmpegOptions options) =>
        ColorMappingEngine.ResolveOutMatrixCapability(
            options.Format, options.ColorOutMatrix, options.EncoderBackend, options.Encoder);

    private static bool HasSimpleVf(string args)
    {
        int i = args.IndexOf("-vf", StringComparison.Ordinal);
        while (i >= 0)
        {
            bool left = i == 0 || char.IsWhiteSpace(args[i - 1]);
            bool right = i + 3 >= args.Length || char.IsWhiteSpace(args[i + 3]);
            if (left && right) return true;
            i = args.IndexOf("-vf", i + 3, StringComparison.Ordinal);
        }
        return false;
    }

    private static void EnumerateUniversal(InputShape[] inputs, List<EncoderState> states, Report report)
    {
        // Full discrete static-colour Cartesian accounting. Every decisive cell calls the
        // product's own input-matrix / output-matrix / strategy resolvers. Axes which do not
        // participate in those decisions are multiplied explicitly, not sampled or omitted.
        foreach (var input in inputs)
        foreach (var state in states)
        foreach (var template in state.Templates)
        {
            var caps = state.OutMatrices.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(m => m, m => {
                var o = template.Options.DeepClone();
                o.ColorOutMatrix = NullAuto(m);
                report.UniversalDecisionEvaluations++;
                return Capability(o);
            });
            foreach (string matrix in state.InputMatrices)
            {
                var o = template.Options.DeepClone();
                o.ColorMatrix = NullAuto(matrix);
                var inputResolution = InputMatrix(o, input.Path, input.Meta);
                report.UniversalDecisionEvaluations++;
                bool validYuvMatrix = input.DerivedGroup && o.ColorMatrix is "bt709" or "bt2020nc" or "bt470bg" or "smpte170m";
                if (validYuvMatrix && inputResolution.Use == "Convert")
                {
                    if (string.IsNullOrWhiteSpace(inputResolution.ScaleFilter)
                        || !inputResolution.ScaleFilter.Contains("in_color_matrix=" + o.ColorMatrix, StringComparison.Ordinal))
                        throw new InvalidOperationException("valid YUV matrix lost its independent ScaleFilter: " + inputResolution.ScaleFilter);
                }
                bool inputRejected = inputResolution.Use == "Reject";
                foreach (var strategy in Strategies)
                {
                    bool strategyWarning = strategy != ColorStrategy.Recommended
                        && (string.IsNullOrWhiteSpace(o.ColorPrimaries)
                            || string.IsNullOrWhiteSpace(o.ColorTrc)
                            || string.IsNullOrWhiteSpace(o.ColorMatrix));
                    report.UniversalDecisionEvaluations++;
                    foreach (string outMatrix in state.OutMatrices)
                    foreach (string chroma in state.Chromas)
                    {
                        bool chromaLocked = string.Equals(ColorSpaceRegistry.NormalizeOutMatrixToken(outMatrix),
                            "rgb", StringComparison.OrdinalIgnoreCase) && chroma != "auto";
                        long multiplier = checked(state.Engines.LongLength * state.Ranges.LongLength
                            * state.ToneMaps.LongLength * 2L * state.BitDepths.LongLength
                            * state.IccVersions.LongLength * state.LosslessValues.LongLength); // gamut-map × live lossless domain
                        report.UniversalEnumerated += multiplier;
                        report.UniversalDecisionCells++;
                        if (strategyWarning) report.UniversalUiWarnings += multiplier;
                        if (chromaLocked) report.UniversalUiLocked += multiplier;
                        else if (inputRejected) report.UniversalInputRejected += multiplier;
                        else if (caps[outMatrix] == OutMatrixCapability.Rejected) report.UniversalOutputRejected += multiplier;
                        else report.UniversalPlanned += multiplier;
                    }
                }
            }
        }
        long classified = report.UniversalPlanned + report.UniversalUiLocked
            + report.UniversalInputRejected + report.UniversalOutputRejected;
        if (classified != report.UniversalEnumerated)
            throw new InvalidOperationException($"universal classification gap: {classified} != {report.UniversalEnumerated}");
        if (report.UniversalEnumerated <= 0 || report.UniversalPlanned <= 0)
            throw new InvalidOperationException("universal discrete Cartesian coverage is empty");
    }

    private static List<EncoderState> CollectStates(MainWindow window)
    {
        var result = new List<EncoderState>();
        var format = Find<ComboBox>("FormatCombo")!;
        foreach (string formatLabel in format.Items.OfType<string>().ToArray())
        {
            Find<ComboBox>("FormatCombo").SelectedItem = formatLabel;
            Pump(8);
            SettleEncoderRefresh(window);
            var encoder = Find<ComboBox>("EncoderCombo")!;
            foreach (string encoderLabel in encoder.Items.OfType<string>().ToArray())
            {
                encoder.SelectedItem = encoderLabel;
                Pump(12);
                SettleEncoderRefresh(window);
                if (!Equals(encoder.SelectedItem, encoderLabel)) continue;
                var state = new EncoderState(formatLabel, encoderLabel,
                    EncoderInfo.ParseEncoderName(encoderLabel), EncoderInfo.ParseBackend(encoderLabel));
                state.InputMatrices = Find<ComboBox>("ColorMatrixCombo")!.Items.OfType<string>().ToArray();
                var rangeCombo = Find<ComboBox>("ColorRangeCombo")!;
                string[] liveRanges = rangeCombo.IsVisible
                    ? rangeCombo.Items.OfType<string>().Select(MapRange).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    : new[] { "auto" };
                state.Ranges = liveRanges.Length > 0 ? liveRanges : new[] { "auto" };
                state.Engines = Find<ComboBox>("ColorEngineCombo")!.Items.OfType<string>().ToArray();
                state.ToneMaps = Find<ComboBox>("ColorToneMapCombo")!.Items.OfType<string>().ToArray();
                state.OutMatrices = Find<ComboBox>("OutputMatrixCombo")!.Items.OfType<string>()
                    .Select(MapOutMatrix).Where(v => v != null).Cast<string>().ToArray();
                state.Chromas = Find<ComboBox>("ChromaCombo")!.Items.OfType<string>().ToArray();
                state.BitDepths = Find<ComboBox>("BitDepthCombo")!.Items.OfType<string>().ToArray();
                var iccCombo = Find<ComboBox>("IccVersionCombo")!;
                state.IccVersions = state.Format == "jpg"
                    ? iccCombo.Items.OfType<string>().Select(MapIccVersion).Distinct().ToArray()
                    : new[] { "not-applicable" };
                var lossless = Find<CheckBox>("LosslessCheck")!;
                state.LosslessValues = lossless.IsVisible && lossless.IsEnabled
                    ? new[] { false, true } : new[] { false };
                if (state.InputMatrices.Length == 0 || state.Ranges.Length == 0 || state.Engines.Length == 0
                    || state.ToneMaps.Length == 0 || state.OutMatrices.Length == 0 || state.Chromas.Length == 0
                    || state.BitDepths.Length == 0 || state.IccVersions.Length == 0 || state.LosslessValues.Length == 0)
                    throw new InvalidOperationException($"empty live domain: {formatLabel}/{encoderLabel}");

                foreach (bool advanced in new[] { false, true })
                {
                    Find<CheckBox>("UseAdvancedColor")!.IsChecked = advanced;
                    ResetAdvancedAuto();
                    foreach (string space in Find<ComboBox>("ColorSpaceCombo")!.Items.OfType<string>().ToArray())
                    {
                        Find<ComboBox>("ColorSpaceCombo")!.SelectedItem = space;
                        Pump(8);
                        var collected = Collect(window, Path.Combine(SrcDir, "src_8bit.png"));
                        state.Templates.Add(new TargetTemplate((advanced ? "advanced:" : "basic:") + space, collected));
                    }
                }

                // Custom target family: every primaries × TRC pair, based on the real advanced-auto
                // GUI template. Input matrix is an independent axis in the core product.
                var customBase = state.Templates.First(t => t.Name == "advanced:auto").Options;
                string[] primaries = Find<ComboBox>("ColorPrimariesCombo")!.Items.OfType<string>()
                    .Select(NullAuto).Where(v => v != null).Cast<string>().ToArray();
                string[] trcs = Find<ComboBox>("ColorTrcCombo")!.Items.OfType<string>()
                    .Select(NullAuto).Where(v => v != null).Cast<string>().ToArray();
                foreach (string p in primaries)
                foreach (string t in trcs)
                {
                    var options = customBase.DeepClone();
                    options.ColorPrimaries = p;
                    options.ColorTrc = t;
                    options.ColorMatrix = null;
                    state.Templates.Add(new TargetTemplate($"custom:{p}/{t}", options));
                }
                result.Add(state);
            }
        }
        return result;
    }

    private static FfmpegOptions Collect(MainWindow window, string input)
    {
        var method = typeof(MainWindow).GetMethod("CollectOptionsFromUiAsync", BF)!;
        var task = (Task<FfmpegOptions>)method.Invoke(window, new object?[] { input, false })!;
        for (int spin = 0; spin < 5000 && !task.IsCompleted; spin++)
        {
            Pump(1);
            Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("CollectOptionsFromUiAsync did not settle");
        return task.GetAwaiter().GetResult() ?? throw new InvalidOperationException("GUI collector returned null");
    }

    private static void WaitOnUi(Task task, string name)
    {
        for (int spin = 0; spin < 120000 && !task.IsCompleted; spin++)
        {
            Pump(1);
            Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException(name + " did not settle");
        task.GetAwaiter().GetResult();
        Pump(12);
    }

    private static void SettleEncoderRefresh(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("RefreshEncoderListAsync", BF)!;
        var task = (Task)method.Invoke(window, null)!;
        WaitOnUi(task, "RefreshEncoderListAsync");
    }

    private static void ResetAdvancedAuto()
    {
        Find<ComboBox>("ColorSpaceCombo")!.SelectedItem = "auto";
        Find<ComboBox>("ColorPrimariesCombo")!.SelectedItem = "auto";
        Find<ComboBox>("ColorTrcCombo")!.SelectedItem = "auto";
        Find<ComboBox>("ColorMatrixCombo")!.SelectedItem = "auto";
        Find<ComboBox>("ColorRangeCombo")!.SelectedItem = "auto";
        Find<ComboBox>("ColorToneMapCombo")!.SelectedItem = "auto";
        Find<ComboBox>("ColorEngineCombo")!.SelectedItem = "auto";
        Pump(8);
    }

    private static string MapRange(string label)
    {
        var method = typeof(MainWindow).GetMethod("MapColorRangeValue", BFS)!;
        return (string)method.Invoke(null, new object?[] { label })!;
    }

    private static string MapIccVersion(string label)
    {
        var method = typeof(MainWindow).GetMethod("MapIccVersionToken", BFS)!;
        return (string)method.Invoke(null, new object?[] { label })!;
    }

    private static string? MapOutMatrix(string label)
    {
        var method = typeof(MainWindow).GetMethod("MapOutMatrixValue", BFS)!;
        return (string?)method.Invoke(null, new object?[] { label });
    }

    private static string? NullAuto(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "auto" ? null : value;

    private static string Extension(string format) => format switch
    {
        "jpegli" => "jpg", "apng" => "png", _ => format
    };

    private static void Fail(Report report, string key, string detail)
    {
        report.FailureCount++;
        if (report.Failures.Count < 100) report.Failures.Add(key + " :: " + detail);
        else if (report.Failures.Count == 100) report.Failures.Add("failure detail truncated after 100 entries");
    }

    private sealed class InputShape
    {
        public string Name;
        public string Path;
        public bool DerivedGroup;
        public FfmpegCommandBuilder.ColorMetadata Meta;
        public InputShape(string name, string path, bool group) { Name = name; Path = path; DerivedGroup = group; }
    }

    private sealed class TargetTemplate
    {
        public string Name;
        public FfmpegOptions Options;
        public TargetTemplate(string name, FfmpegOptions options) { Name = name; Options = options; }
    }

    private sealed class EncoderState
    {
        public string Format;
        public string EncoderLabel;
        public string Encoder;
        public EncoderBackend Backend;
        public string[] InputMatrices = Array.Empty<string>();
        public string[] Ranges = Array.Empty<string>();
        public string[] Engines = Array.Empty<string>();
        public string[] ToneMaps = Array.Empty<string>();
        public string[] OutMatrices = Array.Empty<string>();
        public string[] Chromas = Array.Empty<string>();
        public string[] BitDepths = Array.Empty<string>();
        public string[] IccVersions = Array.Empty<string>();
        public bool[] LosslessValues = Array.Empty<bool>();
        public List<TargetTemplate> Templates = new();
        public EncoderState(string format, string label, string encoder, EncoderBackend backend)
        { Format = NormalizeFormat(format); EncoderLabel = label; Encoder = encoder; Backend = backend; }
        private static string NormalizeFormat(string value) => value switch
        {
            "JPEG" => "jpg", "PNG" => "png", "WebP" => "webp", "AVIF" => "avif",
            "JPEG XL" => "jxl", "TIFF" => "tif", "JPEG XR" => "jxr", _ => value.ToLowerInvariant()
        };
    }

    public sealed class StateReport
    {
        public string Format { get; set; } = "";
        public string Encoder { get; set; } = "";
        public string[] InputMatrices { get; set; } = Array.Empty<string>();
        public string[] Ranges { get; set; } = Array.Empty<string>();
        public string[] Engines { get; set; } = Array.Empty<string>();
        public string[] ToneMaps { get; set; } = Array.Empty<string>();
        public string[] OutputMatrices { get; set; } = Array.Empty<string>();
        public string[] Chromas { get; set; } = Array.Empty<string>();
        public string[] BitDepths { get; set; } = Array.Empty<string>();
        public string[] IccVersions { get; set; } = Array.Empty<string>();
        public bool[] LosslessValues { get; set; } = Array.Empty<bool>();
        public string[] Targets { get; set; } = Array.Empty<string>();
    }

    public sealed class Report
    {
        public int Schema { get; set; } = 1;
        public bool GpuDomainFinalized { get; set; }
        public string Scope { get; set; } = "live-GUI static colour: full discrete Cartesian accounting + three exhaustive command families + independent pixel routes; free numeric and all-pixel exhaustive out of scope";
        public string[] Inputs { get; set; } = new[] { "rgba8_png", "yuv_tilegrid_heic" };
        public string[] Families { get; set; } = new[] {
            "universal = input × format/encoder × target-template × input-matrix × strategy × output-matrix × chroma × engine × range × tone-map × gamut-map × bit-depth × ICC-version × lossless (product-resolver Cartesian accounting)",
            "core = input × format/encoder × target-template × engine × input-matrix × range × output-matrix",
            "policy = input × format/encoder × target-template × tone-map × strategy × gamut-map (GUI warning states counted separately and still built)",
            "geometry = input × format/encoder × chroma × bit-depth × output-matrix × input-matrix"
        };
        public List<StateReport> States { get; set; } = new();
        public int FormatEncoderStates { get; set; }
        public int TargetTemplates { get; set; }
        public long CoreEnumerated { get; set; }
        public long PolicyEnumerated { get; set; }
        public long GeometryEnumerated { get; set; }
        public long CommandsBuilt { get; set; }
        public long PipeCommandsBuilt { get; set; }
        public long ExpectedRejects { get; set; }
        public long UiWarningStates { get; set; }
        public long UiLockedStates { get; set; }
        public long UniversalEnumerated { get; set; }
        public long UniversalDecisionCells { get; set; }
        public long UniversalDecisionEvaluations { get; set; }
        public long UniversalPlanned { get; set; }
        public long UniversalUiWarnings { get; set; }
        public long UniversalUiLocked { get; set; }
        public long UniversalInputRejected { get; set; }
        public long UniversalOutputRejected { get; set; }
        public int FailureCount { get; set; }
        public List<string> Failures { get; set; } = new();
    }
}
