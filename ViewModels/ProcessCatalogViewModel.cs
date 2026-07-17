using BrightEyes.Processing.Contracts;
using System.Windows;
using System.Windows.Media;

namespace BrightEyes.ViewModels;

public sealed class ProcessCatalogItemViewModel
{
    public ProcessCatalogItemViewModel(ProcessDescriptor descriptor)
    {
        Descriptor = descriptor;
        Name = ResourceText.Resolve(descriptor.Definition.NameResourceKey);
        Description = ResourceText.Resolve(descriptor.Definition.DescriptionResourceKey);
        Icon = System.Windows.Application.Current?.TryFindResource(descriptor.Definition.IconResourceKey) as ImageSource;
    }

    public ProcessDescriptor Descriptor { get; }
    public string Name { get; }
    public string Description { get; }
    public ImageSource? Icon { get; }
    public string ExecutionLabel => Descriptor.Definition.RequiresIntermediateTexture
        ? $"{Descriptor.Definition.Role} / intermediate texture"
        : $"{Descriptor.Definition.Role} / fusible";
}

public sealed class ProcessRibbonGroupViewModel
{
    public ProcessRibbonGroupViewModel(string tab, string group, IReadOnlyList<ProcessCatalogItemViewModel> items)
    {
        Tab = tab;
        Group = group;
        Items = items;
    }

    public string Tab { get; }
    public string Group { get; }
    public string Header => ResourceText.Resolve($"ProcessGroup_{Group}");
    public IReadOnlyList<ProcessCatalogItemViewModel> Items { get; }
}

internal static class ResourceText
{
    public static string Resolve(string resourceKey)
    {
        object? value = System.Windows.Application.Current?.TryFindResource(resourceKey);
        return value?.ToString() ?? resourceKey;
    }
}
