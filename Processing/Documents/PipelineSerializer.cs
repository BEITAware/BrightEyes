using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;
using System.Text.Json;
using System.IO;
using System.Text.Json.Serialization;
using BrightEyes.Processing.BuiltIn;

namespace BrightEyes.Processing.Documents;

public sealed class PipelineSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(ImageDocument document, string filePath, CancellationToken cancellationToken = default)
    {
        var model = new PipelineFile(
            3,
            document.Source.ContentIdentity,
            document.Processes.Select(process => new ProcessFile(
                process.Descriptor.Definition.StableId,
                process.Descriptor.Definition.Version,
                process.IsEnabled,
                process.Parameters.Select(parameter => new ParameterFile(
                    parameter.Definition.StableId,
                    parameter.IsAutomatic,
                    parameter.UserOverrideValue is null
                        ? null
                        : JsonSerializer.Serialize(parameter.UserOverrideValue, parameter.ValueType, Options))).ToArray())).ToArray());

        await using FileStream stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, model, Options, cancellationToken).ConfigureAwait(false);
        document.MarkSaved();
    }

    public async Task<bool> TryLoadAsync(
        ImageDocument document,
        ProcessRegistry registry,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return false;
        await using FileStream stream = File.OpenRead(filePath);
        PipelineFile model = await JsonSerializer.DeserializeAsync<PipelineFile>(stream, Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("过程文件为空。");
        if (model.SchemaVersion > 3)
            throw new NotSupportedException($"过程文件 schema {model.SchemaVersion} 高于当前支持版本 3。");

        var restored = new List<IImageProcess>();
        ProcessFile? legacyMapper = model.Processes.LastOrDefault(process =>
            process.StableId == BuiltInProcessIds.RenderMapper);
        foreach (ProcessFile savedProcess in model.Processes.Where(process =>
                     process.StableId != BuiltInProcessIds.RenderMapper))
        {
            ProcessDescriptor descriptor = registry.GetRequired(savedProcess.StableId);
            if (savedProcess.Version > descriptor.Definition.Version)
                throw new NotSupportedException($"过程 {savedProcess.StableId} 的保存版本 {savedProcess.Version} 高于当前版本 {descriptor.Definition.Version}。");
            IImageProcess process = descriptor.Create();
            IEnumerable<ParameterFile> savedParameters = savedProcess.StableId == BuiltInProcessIds.ViewRendering && legacyMapper is not null
                ? legacyMapper.Parameters.Concat(savedProcess.Parameters)
                    .GroupBy(parameter => parameter.StableId, StringComparer.Ordinal)
                    .Select(group => group.Last())
                : savedProcess.Parameters;
            foreach (ParameterFile savedParameter in savedParameters)
            {
                IProcessParameter? parameter = process.Parameters.FirstOrDefault(item => item.Definition.StableId == savedParameter.StableId);
                if (parameter is null) continue;
                if (savedParameter.IsAutomatic)
                {
                    parameter.RestoreAutomatic();
                }
                else if (savedParameter.UserOverrideJson is not null)
                {
                    object? value = JsonSerializer.Deserialize(savedParameter.UserOverrideJson, parameter.ValueType, Options);
                    parameter.SetUserValue(value);
                }
            }
            if (descriptor.Definition.CanDisable) process.IsEnabled = savedProcess.IsEnabled;
            restored.Add(process);
        }
        document.RestorePipeline(restored);
        return string.Equals(model.SourceIdentity, document.Source.ContentIdentity, StringComparison.Ordinal);
    }

    public static string GetDefaultSidecarPath(string imagePath) => imagePath + ".bepipeline.json";

    private sealed record PipelineFile(int SchemaVersion, string SourceIdentity, IReadOnlyList<ProcessFile> Processes);
    private sealed record ProcessFile(string StableId, int Version, bool IsEnabled, IReadOnlyList<ParameterFile> Parameters);
    private sealed record ParameterFile(string StableId, bool IsAutomatic, string? UserOverrideJson);
}
