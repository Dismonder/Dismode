using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Dismode.UI;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, SolidColorBrush> _themeBrushes = new(StringComparer.Ordinal);

    private SolidColorBrush ThemeBrush(string key)
    {
        if (!_themeBrushes.TryGetValue(key, out SolidColorBrush? brush))
        {
            brush = new(ReadThemeBrush(key).Color);
            _themeBrushes.Add(key, brush);
        }

        return brush;
    }

    private SolidColorBrush ReadThemeBrush(string key)
    {
        string theme = RootLayout.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        if (new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            theme = "HighContrast";
        }

        ResourceDictionary colors = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
        return (SolidColorBrush)colors[key];
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        foreach ((string key, SolidColorBrush brush) in _themeBrushes)
        {
            brush.Color = ReadThemeBrush(key).Color;
        }

        UpdateTitleBarTheme(RootLayout.ActualTheme);
    }
}
