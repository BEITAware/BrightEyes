using System.Windows;

namespace BrightEyes.Themes;

public static class ThemeManager
{
    public const string DefaultTheme = "Aero Classic";

    public static IReadOnlyList<string> AvailableThemes { get; } =
    [
        DefaultTheme,
        "Aero Lab",
        "Luna",
        "Polar Day",
        "Polar Night",
        "High Contrast"
    ];

    public static string CurrentTheme { get; private set; } = DefaultTheme;

    public static void Apply(string themeName)
    {
        if (!AvailableThemes.Contains(themeName, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown theme: {themeName}", nameof(themeName));

        IList<ResourceDictionary> dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        int themeIndex = FindActiveThemeIndex(dictionaries);
        if (themeIndex < 0)
            throw new InvalidOperationException("The application theme dictionary is not loaded.");

        dictionaries[themeIndex] = new ResourceDictionary
        {
            Source = new Uri($"Themes/{themeName}/Theme.xaml", UriKind.Relative)
        };
        CurrentTheme = themeName;
    }

    private static int FindActiveThemeIndex(IList<ResourceDictionary> dictionaries)
    {
        for (int index = 0; index < dictionaries.Count; index++)
        {
            string? source = dictionaries[index].Source?.OriginalString;
            if (source is not null &&
                source.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase) &&
                source.EndsWith("/Theme.xaml", StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }
}
