using BrightEyes.Processing.Contracts;
using System.Reflection;

namespace BrightEyes.Processing;

public sealed record RegistrationDiagnostic(string Severity, string Message, string? TypeName = null);

public sealed class ProcessRegistrationException : Exception
{
    public ProcessRegistrationException(IReadOnlyList<RegistrationDiagnostic> diagnostics)
        : base("Process registration failed:\n" + string.Join("\n", diagnostics.Select(item => item.Message)))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<RegistrationDiagnostic> Diagnostics { get; }
}

public sealed class ProcessRegistry
{
    private readonly IReadOnlyDictionary<string, ProcessDescriptor> _byId;

    private ProcessRegistry(
        IReadOnlyList<ProcessDescriptor> descriptors,
        IReadOnlyList<RegistrationDiagnostic> diagnostics)
    {
        Descriptors = descriptors;
        Diagnostics = diagnostics;
        _byId = descriptors.ToDictionary(item => item.Definition.StableId, StringComparer.Ordinal);
    }

    public IReadOnlyList<ProcessDescriptor> Descriptors { get; }
    public IReadOnlyList<RegistrationDiagnostic> Diagnostics { get; }

    public static ProcessRegistry Discover(params Assembly[] approvedExtensionAssemblies)
    {
        Assembly mainAssembly = typeof(ProcessRegistry).Assembly;
        Assembly[] assemblies = new[] { mainAssembly }
            .Concat(approvedExtensionAssemblies ?? Array.Empty<Assembly>())
            .Distinct()
            .ToArray();
        var descriptors = new List<ProcessDescriptor>();
        var diagnostics = new List<RegistrationDiagnostic>();

        foreach (Assembly assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
                diagnostics.AddRange(ex.LoaderExceptions
                    .Where(error => error is not null)
                    .Select(error => new RegistrationDiagnostic("Error", error!.Message, assembly.FullName)));
            }

            foreach (Type type in types.Where(type => !type.IsAbstract && typeof(IImageProcess).IsAssignableFrom(type)))
            {
                ProcessDefinitionAttribute? definition = type.GetCustomAttribute<ProcessDefinitionAttribute>();
                if (definition is null)
                {
                    diagnostics.Add(new("Error", $"Process type {type.FullName} has no ProcessDefinitionAttribute.", type.FullName));
                    continue;
                }
                Validate(type, definition, diagnostics);
                descriptors.Add(new(type, definition));
            }
        }

        foreach (IGrouping<string, ProcessDescriptor> duplicate in descriptors.GroupBy(
                     item => item.Definition.StableId,
                     StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            diagnostics.Add(new(
                "Error",
                $"Duplicate process ID '{duplicate.Key}': {string.Join(", ", duplicate.Select(item => item.ProcessType.FullName))}."));
        }

        if (diagnostics.Any(item => item.Severity == "Error"))
            throw new ProcessRegistrationException(diagnostics);

        return new ProcessRegistry(
            descriptors.OrderBy(item => item.Definition.RibbonTab, StringComparer.Ordinal)
                .ThenBy(item => item.Definition.RibbonGroup, StringComparer.Ordinal)
                .ThenBy(item => item.Definition.RibbonOrder)
                .ToArray(),
            diagnostics);
    }

    public ProcessDescriptor GetRequired(string stableId) =>
        _byId.TryGetValue(stableId, out ProcessDescriptor? descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Process ID '{stableId}' is not registered.");

    public bool TryGet(string stableId, out ProcessDescriptor descriptor) =>
        _byId.TryGetValue(stableId, out descriptor!);

    private static void Validate(
        Type type,
        ProcessDefinitionAttribute definition,
        ICollection<RegistrationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(definition.StableId))
            diagnostics.Add(new("Error", $"Process {type.FullName} has an empty stable ID.", type.FullName));
        if (type.GetConstructor(Type.EmptyTypes) is null)
            diagnostics.Add(new("Error", $"Process {type.FullName} requires a public parameterless constructor.", type.FullName));
        if (definition.Version < 1)
            diagnostics.Add(new("Error", $"Process {definition.StableId} has invalid version {definition.Version}.", type.FullName));
        if (!definition.AllowMultiple && definition.StableId.EndsWith("oscilloscope", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new("Warning", $"Analysis process {definition.StableId} does not allow multiple instances.", type.FullName));

        var parameterIds = new HashSet<string>(StringComparer.Ordinal);
        foreach ((PropertyInfo property, ProcessParameterAttribute attribute) in ProcessReflection.GetParameterProperties(type))
        {
            if (!parameterIds.Add(attribute.StableId))
                diagnostics.Add(new("Error", $"Process {definition.StableId} repeats parameter ID '{attribute.StableId}'.", type.FullName));
            if (!typeof(Parameters.IProcessParameter).IsAssignableFrom(property.PropertyType))
                diagnostics.Add(new("Error", $"Property {type.Name}.{property.Name} does not implement IProcessParameter.", type.FullName));
            if (!double.IsNaN(attribute.Minimum) && !double.IsNaN(attribute.Maximum) && attribute.Minimum > attribute.Maximum)
                diagnostics.Add(new("Error", $"Parameter {definition.StableId}/{attribute.StableId} has an inverted range.", type.FullName));
        }
    }
}
