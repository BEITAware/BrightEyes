using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;
using System.Text.Json;
using System.IO;
using System.Text.Json.Serialization;
using BrightEyes.Processing.BuiltIn;

namespace BrightEyes.Processing.Documents;

public sealed class PipelineSerializer
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(ImageDocument document, string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long revision = document.PipelineRevision;
        var model = new PipelineFile(
            4,
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

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            string destination = Path.GetFullPath(filePath);
            string temporary = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetRandomFileName());
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);
            try
            {
                await using (stream)
                    await JsonSerializer.SerializeAsync(stream, model, Options, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
            if (document.PipelineRevision == revision)
                document.MarkSaved();
        }
        finally
        {
            _saveGate.Release();
        }
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
        if (model.SchemaVersion > 4)
            throw new NotSupportedException($"过程文件 schema {model.SchemaVersion} 高于当前支持版本 4。");

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
