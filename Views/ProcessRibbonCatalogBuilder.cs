using BrightEyes.ViewModels;
using System.Windows.Controls.Ribbon;
using System.Windows.Input;

namespace BrightEyes.Views;

internal static class ProcessRibbonCatalogBuilder
{
    public static void Populate(
        Ribbon ribbon,
        IReadOnlyList<ProcessRibbonGroupViewModel> groups,
        ICommand insertCommand,
        MouseButtonEventHandler mouseDown,
        System.Windows.Input.MouseEventHandler mouseMove)
    {
        int insertIndex = Math.Max(ribbon.Items.Count - 1, 0);
        foreach (IGrouping<string, ProcessRibbonGroupViewModel> tabGroup in groups.GroupBy(group => group.Tab))
        {
            string tabResource = $"ProcessTab_{tabGroup.Key}";
            string resolvedTab = ResourceText.Resolve(tabResource);
            var tab = new RibbonTab
            {
                Header = resolvedTab == tabResource ? tabGroup.Key : resolvedTab
            };
            foreach (ProcessRibbonGroupViewModel group in tabGroup)
            {
                var ribbonGroup = new RibbonGroup { Header = group.Header };
                foreach (ProcessCatalogItemViewModel item in group.Items)
                {
                    var button = new RibbonButton
                    {
                        Label = item.Name,
                        SmallImageSource = item.Icon,
                        ToolTip = item.Description,
                        DataContext = item,
                        Command = insertCommand,
                        CommandParameter = item
                    };
                    button.PreviewMouseLeftButtonDown += mouseDown;
                    button.PreviewMouseMove += mouseMove;
                    ribbonGroup.Items.Add(button);
                }
                tab.Items.Add(ribbonGroup);
            }
            ribbon.Items.Insert(insertIndex++, tab);
        }
    }
}
