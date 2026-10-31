using Dismode.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Dismode.UI.Controls;

public sealed partial class SettingsOptionTile : UserControl
{
    private Storyboard? _detailAnimation;

    public SettingsOptionTile()
    {
        InitializeComponent();
    }

    public string Glyph { get; set; } = string.Empty;

    public string Benefit { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public UIElement? Details { get; set; }

    public ToggleSwitch? Option { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (Option is null)
        {
            return;
        }

        Option.Toggled += OnStateChanged;
        AccentSurface.Opacity = Option.IsOn == true ? 1 : 0;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _detailAnimation?.Stop();
        CompleteDetailTransition();
        if (Option is null)
        {
            return;
        }

        Option.Toggled -= OnStateChanged;
    }

    private void OnDetailSizeChanged(object sender, SizeChangedEventArgs args) =>
        DetailClip.Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height);

    private void OnMoreClicked(object sender, RoutedEventArgs args)
    {
        bool expanded = MoreLink.IsChecked == true;
        double height = DetailHost.ActualHeight;
        double opacity = DetailHost.Opacity;
        _detailAnimation?.Stop();
        ChevronRotation.Angle = expanded ? 180 : 0;
        if (!TacticalVfxService.Instance.IsEnabled
            || !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            CompleteDetailTransition();
            return;
        }

        DetailHost.Visibility = Visibility.Visible;
        DetailHost.Height = height;
        DetailHost.Opacity = opacity;
        DetailText.Measure(new Size(Math.Max(0, ActualWidth - 32), double.PositiveInfinity));
        DoubleAnimation resize = new()
        {
            From = height,
            To = expanded ? DetailText.DesiredSize.Height : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        DoubleAnimation fade = new()
        {
            From = opacity,
            To = expanded ? 1 : 0,
            Duration = resize.Duration,
        };
        Storyboard.SetTarget(resize, DetailHost);
        Storyboard.SetTargetProperty(resize, "Height");
        Storyboard.SetTarget(fade, DetailHost);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _detailAnimation = new();
        _detailAnimation.Children.Add(resize);
        _detailAnimation.Children.Add(fade);
        _detailAnimation.Completed += (_, _) =>
        {
            _detailAnimation.Stop();
            CompleteDetailTransition();
        };
        _detailAnimation.Begin();
    }

    private void CompleteDetailTransition()
    {
        bool expanded = MoreLink.IsChecked == true;
        DetailHost.Height = expanded ? double.NaN : 0;
        DetailHost.Opacity = expanded ? 1 : 0;
        DetailHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStateChanged(object sender, RoutedEventArgs args)
    {
        if (!TacticalVfxService.Instance.IsEnabled
            || !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            AccentSurface.Opacity = Option?.IsOn == true ? 1 : 0;
            return;
        }

        DoubleAnimation fade = new()
        {
            To = Option?.IsOn == true ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
        };
        Storyboard.SetTarget(fade, AccentSurface);
        Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard animation = new();
        animation.Children.Add(fade);
        animation.Begin();
    }
}
