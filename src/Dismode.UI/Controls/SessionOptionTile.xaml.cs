using Dismode.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Dismode.UI.Controls;

public sealed partial class SessionOptionTile : UserControl
{
    public SessionOptionTile()
    {
        InitializeComponent();
    }

    public string Glyph { get; set; } = string.Empty;

    public string Benefit { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public CheckBox? Option { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (Option is null)
        {
            return;
        }

        Option.Style = (Style)Resources["OptionSwitchStyle"];
        Option.Checked += OnStateChanged;
        Option.Unchecked += OnStateChanged;
        Option.Click += OnClicked;
        AccentSurface.Opacity = Option.IsChecked == true ? 1 : 0;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (Option is null)
        {
            return;
        }

        Option.Checked -= OnStateChanged;
        Option.Unchecked -= OnStateChanged;
        Option.Click -= OnClicked;
    }

    private static void OnClicked(object sender, RoutedEventArgs args) =>
        TacticalAudioService.Instance.PlayToggle();

    private void OnStateChanged(object sender, RoutedEventArgs args)
    {
        if (!TacticalVfxService.Instance.IsEnabled
            || !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            AccentSurface.Opacity = Option?.IsChecked == true ? 1 : 0;
            return;
        }

        DoubleAnimation fade = new()
        {
            To = Option?.IsChecked == true ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
        };
        Storyboard.SetTarget(fade, AccentSurface);
        Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard animation = new();
        animation.Children.Add(fade);
        animation.Begin();
    }
}
