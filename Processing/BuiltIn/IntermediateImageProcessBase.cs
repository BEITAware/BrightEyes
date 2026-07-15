using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;
using BrightEyes.Renderer;

namespace BrightEyes.Processing.BuiltIn;

public abstract class IntermediateImageProcessBase : ProcessBase
{
    [ProcessParameter("input.primaries", "Parameter_InputPrimaries", ProcessParameterEditor.Enum, 800, AutoValueSource = "Upstream metadata", IsAdvanced = true)]
    public ProcessParameter<ColorPrimaries> InputPrimaries { get; } = new(ColorPrimaries.Rec709);

    [ProcessParameter("input.transfer", "Parameter_InputTransfer", ProcessParameterEditor.Enum, 801, AutoValueSource = "Upstream metadata", IsAdvanced = true)]
    public ProcessParameter<ColorTransferFunction> InputTransfer { get; } = new(ColorTransferFunction.Linear);

    [ProcessParameter("input.photometric", "Parameter_InputPhotometric", ProcessParameterEditor.Enum, 802, AutoValueSource = "Upstream metadata", IsAdvanced = true)]
    public ProcessParameter<PhotometricModel> InputPhotometric { get; } = new(PhotometricModel.DisplayLinearRelative);

    [ProcessParameter("input.reference-white", "Parameter_InputReferenceWhite", ProcessParameterEditor.Number, 803, Unit = "nit", Minimum = 1, Maximum = 10000, Step = 1, AutoValueSource = "Upstream metadata", IsAdvanced = true)]
    public ProcessParameter<float> InputReferenceWhite { get; } = new(100.0f);

    [ProcessParameter("input.peak", "Parameter_InputPeak", ProcessParameterEditor.Number, 804, Unit = "nit", Minimum = 1, Maximum = 10000, Step = 10, AutoValueSource = "Upstream metadata", IsAdvanced = true)]
    public ProcessParameter<float> InputPeak { get; } = new(100.0f);

    [ProcessParameter("input.range", "Parameter_InputRange", ProcessParameterEditor.Enum, 805, AutoValueSource = "Upstream metadata", IsAdvanced = true)]
    public ProcessParameter<ColorRange> InputRange { get; } = new(ColorRange.Full);

    [ProcessParameter("input.alpha", "Parameter_InputAlpha", ProcessParameterEditor.Enum, 806, AutoValueSource = "Upstream metadata", IsAdvanced = true)]
    public ProcessParameter<AlphaRepresentation> InputAlpha { get; } = new(AlphaRepresentation.Straight);

    [ProcessParameter("output.primaries", "Parameter_OutputPrimaries", ProcessParameterEditor.Enum, 900, AutoValueSource = "Process declaration", IsAdvanced = true)]
    public ProcessParameter<ColorPrimaries> OutputPrimaries { get; } = new(ColorPrimaries.Rec709);

    [ProcessParameter("output.transfer", "Parameter_OutputTransfer", ProcessParameterEditor.Enum, 901, AutoValueSource = "Process declaration", IsAdvanced = true)]
    public ProcessParameter<ColorTransferFunction> OutputTransfer { get; } = new(ColorTransferFunction.Linear);

    [ProcessParameter("output.photometric", "Parameter_OutputPhotometric", ProcessParameterEditor.Enum, 902, AutoValueSource = "Process declaration", IsAdvanced = true)]
    public ProcessParameter<PhotometricModel> OutputPhotometric { get; } = new(PhotometricModel.DisplayLinearRelative);

    [ProcessParameter("output.reference-white", "Parameter_OutputReferenceWhite", ProcessParameterEditor.Number, 903, Unit = "nit", Minimum = 1, Maximum = 10000, Step = 1, AutoValueSource = "Process declaration", IsAdvanced = true)]
    public ProcessParameter<float> OutputReferenceWhite { get; } = new(100.0f);

    [ProcessParameter("output.peak", "Parameter_OutputPeak", ProcessParameterEditor.Number, 904, Unit = "nit", Minimum = 1, Maximum = 10000, Step = 10, AutoValueSource = "Process declaration", IsAdvanced = true)]
    public ProcessParameter<float> OutputPeak { get; } = new(100.0f);

    [ProcessParameter("output.range", "Parameter_OutputRange", ProcessParameterEditor.Enum, 905, AutoValueSource = "Process declaration", IsAdvanced = true)]
    public ProcessParameter<ColorRange> OutputRange { get; } = new(ColorRange.Full);

    [ProcessParameter("output.alpha", "Parameter_OutputAlpha", ProcessParameterEditor.Enum, 906, AutoValueSource = "Process declaration", IsAdvanced = true)]
    public ProcessParameter<AlphaRepresentation> OutputAlpha { get; } = new(AlphaRepresentation.Straight);

    public override ImageSemantics OutputSemantics => new(
        OutputPrimaries.Value,
        OutputTransfer.Value,
        OutputPhotometric.Value,
        OutputReferenceWhite.Value,
        OutputPeak.Value,
        OutputRange.Value,
        OutputAlpha.Value);

    protected void SetInputAutomatic(ImageSemantics input, string source = "Upstream effective metadata")
    {
        SetAutomatic(InputPrimaries, input.Primaries, source);
        SetAutomatic(InputTransfer, input.Transfer, source);
        SetAutomatic(InputPhotometric, input.PhotometricModel, source);
        SetAutomatic(InputReferenceWhite, input.ReferenceWhiteNits, source);
        SetAutomatic(InputPeak, input.NominalPeakNits, source);
        SetAutomatic(InputRange, input.Range, source);
        SetAutomatic(InputAlpha, input.Alpha, source);
    }

    protected void SetOutputAutomatic(ImageSemantics output, string source)
    {
        SetAutomatic(OutputPrimaries, output.Primaries, source);
        SetAutomatic(OutputTransfer, output.Transfer, source);
        SetAutomatic(OutputPhotometric, output.PhotometricModel, source);
        SetAutomatic(OutputReferenceWhite, output.ReferenceWhiteNits, source);
        SetAutomatic(OutputPeak, output.NominalPeakNits, source);
        SetAutomatic(OutputRange, output.Range, source);
        SetAutomatic(OutputAlpha, output.Alpha, source);
    }

    public override IReadOnlyList<string> ValidateSemanticInput(ImageSemantics input)
    {
        var warnings = new List<string>();
        AddMismatch(warnings, "primaries", input.Primaries, InputPrimaries.Value);
        AddMismatch(warnings, "transfer", input.Transfer, InputTransfer.Value);
        AddMismatch(warnings, "photometric model", input.PhotometricModel, InputPhotometric.Value);
        AddMismatch(warnings, "range", input.Range, InputRange.Value);
        AddMismatch(warnings, "alpha", input.Alpha, InputAlpha.Value);
        if (Math.Abs(input.ReferenceWhiteNits - InputReferenceWhite.Value) > 0.01f)
            warnings.Add($"上游 reference white 为 {input.ReferenceWhiteNits:0.##} nit，本过程按 {InputReferenceWhite.Value:0.##} nit 解释。 ");
        if (Math.Abs(input.NominalPeakNits - InputPeak.Value) > 0.01f)
            warnings.Add($"上游 nominal peak 为 {input.NominalPeakNits:0.##} nit，本过程按 {InputPeak.Value:0.##} nit 解释。 ");
        return warnings;
    }

    protected static void SetAutomatic<T>(ProcessParameter<T> parameter, T value, string source)
    {
        parameter.SetDetectedValue(value, source, MetadataConfidence.High);
        parameter.SetAutomaticValue(value, source);
    }

    private static void AddMismatch<T>(ICollection<string> warnings, string name, T upstream, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(upstream, expected))
            warnings.Add($"上游 {name} 为 {upstream}，本过程按 {expected} 解释。 ");
    }
}
