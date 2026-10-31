using Dismode.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Dismode.UI.Controls;

public sealed partial class SessionOptionTile : UserControl
{
    private Storyboard? _detailAnimation;

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
        _detailAnimation?.Stop();
        CompleteDetailTransition();
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
