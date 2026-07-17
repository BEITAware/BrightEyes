using BrightEyes.Processing;
using BrightEyes.Processing.BuiltIn;
using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Execution;
using BrightEyes.Renderer;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace BrightEyes.PerformanceTests;

internal static class Program
{
    private const double DecodeBudgetMilliseconds = 1_000.0;

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            string repositoryRoot = FindRepositoryRoot(args.FirstOrDefault());
            RunPassPolicyTests();
            DecodeMeasurement summary = await MeasureResourceDecodeAsync(repositoryRoot);
            GpuPipelineMeasurement gpuSummary = await MeasureDefaultGpuPipelineAsync(repositoryRoot);

            Console.WriteLine();
            Console.WriteLine("PASS: pipeline architecture and resource decode performance");
            Console.WriteLine(
                $"512x512 files={summary.Count}, median={summary.MedianMilliseconds:0.00} ms, " +
                $"p95={summary.P95Milliseconds:0.00} ms, max={summary.MaximumMilliseconds:0.00} ms");
            Console.WriteLine(
                $"one-pass GPU pipeline files={gpuSummary.Count}, renderer init={gpuSummary.RendererInitializationMilliseconds:0.00} ms, " +
                $"median={gpuSummary.MedianMilliseconds:0.00} ms, max={gpuSummary.MaximumMilliseconds:0.00} ms");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void RunPassPolicyTests()
    {
        ProcessDefinitionAttribute terminal = DefinitionOf<ViewRenderingProcess>();
        ProcessDefinitionAttribute pointwise = DefinitionOf<ColorSpaceConversionProcess>();
        ProcessDefinitionAttribute neighborhood = DefinitionOf<GaussianBlurProcess>();
        ProcessDefinitionAttribute observer = DefinitionOf<HistogramProcess>();

        Assert(
            !PipelinePassPolicy.RequiresMaterializationBefore(terminal, true),
            "The terminal must consume decoder normalization and pointwise operations in its one physical pass.");
        Assert(
            !PipelinePassPolicy.RequiresMaterializationBefore(pointwise, true),
            "A pointwise process must not force an intermediate texture.");
        Assert(
            PipelinePassPolicy.RequiresMaterializationBefore(neighborhood, true),
            "A neighborhood process must materialize deferred decoder normalization.");
        Assert(
            PipelinePassPolicy.RequiresMaterializationBefore(observer, true),
            "An observer must see a fully materialized pointwise stream.");

        PropertyInfo[] publicFrameProperties = typeof(GpuImageFrame).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert(
            publicFrameProperties.All(property =>
                !property.Name.Contains("Metadata", StringComparison.OrdinalIgnoreCase) &&
                !property.PropertyType.Name.Contains("Metadata", StringComparison.OrdinalIgnoreCase)),
            "GpuImageFrame must not expose or transport process metadata.");

        AssertHdrProcessParametersRemainOnePass();

        Console.WriteLine("PASS: pass policy preserves SDR/HDR one-pass, boundary, observer, and metadata-free frame contracts");
    }

    private static void AssertHdrProcessParametersRemainOnePass()
    {
        DetectedImageMetadata pqMetadata = DetectedImageMetadata.Unknown(".jpg") with
        {
            HasUltraHdrGainMap = true,
            Primaries = ColorPrimaries.Rec709,
            Transfer = ColorTransferFunction.Srgb,
            PhotometricModel = PhotometricModel.DisplayLinearAbsolute,
            ReferenceWhiteNits = 100.0f,
            NominalPeakNits = 1_000.0f,
            BackendCapabilities = new[]
            {
                new BackendCapability("StarwardUltraHdr", true, "test", true, true, 10),
                new BackendCapability("PhotoSauce", true, "test", false, true, 16)
            }
        };
        var pqSource = new RawImage("pq-process-test.jpg", Array.Empty<byte>(), pqMetadata, "test");
        var pqDecoder = new DecoderProcess();
        pqDecoder.RecalculateAutomaticParameters(new ProcessAutoContext(pqSource, DisplayColorMetadata.SdrFallback));
        Assert(
            pqDecoder.Parameters.Contains(pqDecoder.OutputTransfer) &&
            pqDecoder.Parameters.Contains(pqDecoder.OutputPrimaries) &&
            pqDecoder.Parameters.Contains(pqDecoder.OutputReferenceWhiteNits) &&
            pqDecoder.Parameters.Contains(pqDecoder.OutputNominalPeakNits),
            "Decoder output interpretation is not represented by the process parameter system.");
        Assert(
            pqDecoder.DecodedOutputColorMetadata.Normalize().Equals(ColorSourceMetadata.PqRec2020Opaque.Normalize()),
            "The application did not adjust the decoder process to PQ/Rec.2020.");
        Assert(
            ImageDecodeService.OutputMatchesConfiguredContract(pqSource, pqDecoder, DecoderBackend.StarwardUltraHdr),
            "PQ/Rec.2020 decoder parameters must remain eligible for terminal fusion.");
        Assert(
            !ImageDecodeService.OutputMatchesConfiguredContract(pqSource, pqDecoder, DecoderBackend.PhotoSauce),
            "A fallback backend that violates decoder parameters must be normalized inside the decoder node.");

        DetectedImageMetadata hlgMetadata = DetectedImageMetadata.Unknown(".png") with
        {
            HasCicp = true,
            Primaries = ColorPrimaries.Rec2020,
            Transfer = ColorTransferFunction.Hlg,
            PhotometricModel = PhotometricModel.SceneLinearRelative,
            ReferenceWhiteNits = 203.0f,
            NominalPeakNits = 1_000.0f,
            BackendCapabilities = new[]
            {
                new BackendCapability("PhotoSauce", true, "test", true, true, 16)
            }
        };
        var hlgSource = new RawImage("hlg-process-test.png", Array.Empty<byte>(), hlgMetadata, "test");
        var hlgDecoder = new DecoderProcess();
        hlgDecoder.RecalculateAutomaticParameters(new ProcessAutoContext(hlgSource, DisplayColorMetadata.SdrFallback));
        Assert(
            hlgDecoder.DecodedOutputColorMetadata.Transfer == ColorTransferFunction.Hlg &&
            hlgDecoder.DecodedOutputColorMetadata.Primaries == ColorPrimaries.Rec2020,
            "The application did not adjust the decoder process to HLG/Rec.2020.");
        Assert(
            ImageDecodeService.OutputMatchesConfiguredContract(hlgSource, hlgDecoder, DecoderBackend.PhotoSauce),
            "HLG/Rec.2020 decoder parameters must remain eligible for terminal fusion.");
        var hlgView = new ViewRenderingProcess();
        hlgView.RecalculateAutomaticParameters(new ProcessAutoContext(hlgSource, DisplayColorMetadata.SdrFallback));
        Assert(
            hlgView.ApplyHlgOotf.Value &&
            !hlgView.SourceIsRelative.Value &&
            Math.Abs(hlgView.SourceReferenceWhiteNits.Value - 203.0f) < 0.001f,
            "The application did not fully adjust the view process for HLG source semantics.");
    }

    private static async Task<DecodeMeasurement> MeasureResourceDecodeAsync(string repositoryRoot)
    {
        string resources = Path.Combine(repositoryRoot, "Resources");
        string[] files = Directory.EnumerateFiles(resources, "*.png", SearchOption.AllDirectories)
            .Where(Is512SquarePng)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert(files.Length > 0, "No 512x512 PNG test resources were found.");

        var preflight = new MetadataPreflightService();
        var decodeService = new ImageDecodeService();

        // PhotoSauce codec registration is application-startup work. Warm it once
        // so every measured sample represents an opened-image decode, not JIT.
        await DecodeOneAsync(files[0], preflight, decodeService);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var elapsed = new List<double>(files.Length);
        foreach (string file in files)
        {
            var stopwatch = Stopwatch.StartNew();
            DecodedImageFrame frame = await DecodeOneAsync(file, preflight, decodeService);
            stopwatch.Stop();

            Assert(frame.Width == 512 && frame.Height == 512, $"Unexpected decoded size for {file}.");
            Assert(frame.Storage == DecodedPixelStorage.Bgra8, $"PhotoSauce did not preserve native BGRA layout for {file}.");
            Assert(frame.PixelBytes?.Length == 512 * 512 * 4, $"Unexpected decoded byte count for {file}.");
            elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
            Console.WriteLine($"{stopwatch.Elapsed.TotalMilliseconds,8:0.00} ms  {Path.GetFileName(file)}");
        }

        double[] ordered = elapsed.Order().ToArray();
        double maximum = ordered[^1];
        Assert(
            maximum < DecodeBudgetMilliseconds,
            $"A 512x512 preflight + CPU decode exceeded {DecodeBudgetMilliseconds:0} ms (max {maximum:0.00} ms).");

        return new DecodeMeasurement(
            ordered.Length,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            maximum);
    }

    private static async Task<GpuPipelineMeasurement> MeasureDefaultGpuPipelineAsync(string repositoryRoot)
    {
        string[] files = Directory.EnumerateFiles(Path.Combine(repositoryRoot, "Resources"), "*.png", SearchOption.AllDirectories)
            .Where(Is512SquarePng)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        Assert(files.Length > 0, "No 512x512 PNG resources were available for the GPU pipeline test.");

        using var window = new TestWindow();
        using var renderer = new RendererD3D11(window.Handle, 512, 512);
        var initialization = Stopwatch.StartNew();
        renderer.Initialize();
        initialization.Stop();

        ProcessRegistry registry = ProcessRegistry.Discover();
        var preflight = new MetadataPreflightService();
        var coordinator = new PipelineExecutionCoordinator(() => renderer);
        var elapsed = new List<double>(files.Length);

        foreach (string file in files)
        {
            var stopwatch = Stopwatch.StartNew();
            RawImage source = await preflight.InspectAsync(file, CancellationToken.None);
            using var document = new BrightEyes.Processing.Documents.ImageDocument(
                source,
                registry,
                renderer.DisplayColorMetadata);
            await coordinator.ExecuteAsync(document);
            stopwatch.Stop();

            Assert(document.HasLastValidFrame, $"The default GPU pipeline failed for {file}.");
            Assert(
                ReferenceEquals(document.DecodedGpuFrame, document.FinalGpuFrame),
                "The default pipeline allocated an intermediate image instead of retaining the one-pass decoder upload.");
            elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
            renderer.ClearExternalTexture();
        }

        await VerifyGpuPassBoundariesAsync(files[0], registry, preflight, coordinator, renderer);

        double[] ordered = elapsed.Order().ToArray();
        double maximum = ordered[^1];
        Assert(
            maximum < DecodeBudgetMilliseconds,
            $"A 512x512 default GPU pipeline exceeded {DecodeBudgetMilliseconds:0} ms (max {maximum:0.00} ms).");
        Console.WriteLine(
            $"PASS: default GPU pipeline retained decoder uploads without an RGBA16F intermediate " +
            $"(max {maximum:0.00} ms)");

        return new GpuPipelineMeasurement(
            ordered.Length,
            initialization.Elapsed.TotalMilliseconds,
            Percentile(ordered, 0.50),
            maximum);
    }

    private static async Task VerifyGpuPassBoundariesAsync(
        string file,
        ProcessRegistry registry,
        MetadataPreflightService preflight,
        PipelineExecutionCoordinator coordinator,
        RendererD3D11 renderer)
    {
        RawImage pointwiseSource = await preflight.InspectAsync(file, CancellationToken.None);
        using (var pointwiseDocument = new BrightEyes.Processing.Documents.ImageDocument(
                   pointwiseSource,
                   registry,
                   renderer.DisplayColorMetadata))
        {
            var toP3 = (ColorSpaceConversionProcess)pointwiseDocument.Insert(
                registry.GetRequired(BuiltInProcessIds.ColorSpaceConversion),
                2);
            toP3.OutputGamut.SetUserValue(ColorPrimaries.DisplayP3);
            var toRec709 = (ColorSpaceConversionProcess)pointwiseDocument.Insert(
                registry.GetRequired(BuiltInProcessIds.ColorSpaceConversion),
                3);
            toRec709.InputGamut.SetUserValue(ColorPrimaries.DisplayP3);
            await coordinator.ExecuteAsync(pointwiseDocument);
            Assert(pointwiseDocument.HasLastValidFrame, "The pointwise GPU pipeline failed.");
            Assert(
                ReferenceEquals(pointwiseDocument.DecodedGpuFrame, pointwiseDocument.FinalGpuFrame),
                "Multiple non-identity CST processes unexpectedly allocated an intermediate GPU frame.");
            renderer.ClearExternalTexture();
        }

        RawImage neighborhoodSource = await preflight.InspectAsync(file, CancellationToken.None);
        using (var neighborhoodDocument = new BrightEyes.Processing.Documents.ImageDocument(
                   neighborhoodSource,
                   registry,
                   renderer.DisplayColorMetadata))
        {
            await coordinator.ExecuteAsync(neighborhoodDocument);
            AssertNoProcessErrors(neighborhoodDocument, "The initial one-pass pipeline failed before adding blur.");
            var blur = (GaussianBlurProcess)neighborhoodDocument.Insert(
                registry.GetRequired(BuiltInProcessIds.GaussianBlur),
                2);
            await coordinator.ExecuteAsync(neighborhoodDocument);
            AssertNoProcessErrors(neighborhoodDocument, "The first neighborhood GPU pipeline failed.");
            Assert(
                !ReferenceEquals(neighborhoodDocument.DecodedGpuFrame, neighborhoodDocument.FinalGpuFrame),
                "A neighborhood process did not create its required materialization boundary.");
            blur.Radius.SetUserValue(12);
            blur.Sigma.SetUserValue(5.5f);
            await coordinator.ExecuteAsync(neighborhoodDocument);
            AssertNoProcessErrors(
                neighborhoodDocument,
                "Re-executing Gaussian blur after a parameter change reused an invalid decoded texture.");
            renderer.ClearExternalTexture();
        }

        RawImage observerSource = await preflight.InspectAsync(file, CancellationToken.None);
        using (var observerDocument = new BrightEyes.Processing.Documents.ImageDocument(
                   observerSource,
                   registry,
                   renderer.DisplayColorMetadata))
        {
            await coordinator.ExecuteAsync(observerDocument);
            AssertNoProcessErrors(observerDocument, "The initial one-pass pipeline failed before adding an observer.");
            IImageProcess observer = observerDocument.Insert(registry.GetRequired(BuiltInProcessIds.Waveform), 2);
            await coordinator.ExecuteAsync(observerDocument);
            AssertNoProcessErrors(observerDocument, "The observer failed before the display terminal.");

            Assert(observerDocument.Move(observer, observerDocument.Processes.Count), "Could not move the observer after the terminal.");
            await coordinator.ExecuteAsync(observerDocument);
            AssertNoProcessErrors(observerDocument, "Moving the observer after the terminal affected the image pipeline.");

            Assert(observerDocument.Move(observer, 2), "Could not move the observer back before the terminal.");
            await coordinator.ExecuteAsync(observerDocument);
            AssertNoProcessErrors(observerDocument, "Moving the observer back before the terminal affected the image pipeline.");
            renderer.ClearExternalTexture();
        }

        Console.WriteLine("PASS: CST, repeated blur, and observer moves preserve GPU frame ownership and pass boundaries");
    }

    private static void AssertNoProcessErrors(
        BrightEyes.Processing.Documents.ImageDocument document,
        string message) =>
        Assert(
            document.HasLastValidFrame && document.Processes.All(process => process.State != ProcessState.Error),
            message + " " + string.Join(" | ", document.Processes
                .Where(process => process.State == ProcessState.Error)
                .Select(process => process.Diagnostic)));

    private static async Task<DecodedImageFrame> DecodeOneAsync(
        string path,
        MetadataPreflightService preflight,
        ImageDecodeService decodeService)
    {
        RawImage source = await preflight.InspectAsync(path, CancellationToken.None);
        var decoder = new DecoderProcess();
        decoder.RecalculateAutomaticParameters(new ProcessAutoContext(source, DisplayColorMetadata.SdrFallback));
        return await decodeService.DecodeAsync(source, decoder, CancellationToken.None);
    }

    private static bool Is512SquarePng(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using FileStream stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length) return false;
        return header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
               ReadBigEndian32(header[16..20]) == 512 &&
               ReadBigEndian32(header[20..24]) == 512;
    }

    private static uint ReadBigEndian32(ReadOnlySpan<byte> bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private static ProcessDefinitionAttribute DefinitionOf<T>() =>
        typeof(T).GetCustomAttribute<ProcessDefinitionAttribute>() ??
        throw new InvalidOperationException($"{typeof(T).Name} has no process definition.");

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * ordered.Count) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    private static string FindRepositoryRoot(string? suppliedPath)
    {
        if (!string.IsNullOrWhiteSpace(suppliedPath))
        {
            string candidate = Path.GetFullPath(suppliedPath);
            if (File.Exists(Path.Combine(candidate, "BrightEyes.csproj"))) return candidate;
        }

        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BrightEyes.csproj")))
                return directory.FullName;

        throw new DirectoryNotFoundException("Could not locate the BrightEyes repository root.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record DecodeMeasurement(
        int Count,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds);

    private sealed record GpuPipelineMeasurement(
        int Count,
        double RendererInitializationMilliseconds,
        double MedianMilliseconds,
        double MaximumMilliseconds);

    private sealed class TestWindow : NativeWindow, IDisposable
    {
        public TestWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "BrightEyes performance test",
                Width = 512,
                Height = 512
            });
        }

        public void Dispose()
        {
            DestroyHandle();
            GC.SuppressFinalize(this);
        }
    }
}
