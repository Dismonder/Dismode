using Dismode.Core.Interaction;
using Dismode.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Dismode.UI;

internal sealed class EdgePanelHandleWindow : Window, IDisposable
{
    private readonly EdgePanelNativeWindow _native;
    private readonly Border _pill;
    private readonly Grid _hitArea;
    private Storyboard? _hover;
    private bool _inside;
    internal RectInt32 HandleBounds { get; private set; }
    internal EdgePanelRect HandleRect => EdgePanelNativeWindow.ToEdgeRect(HandleBounds);
    internal EdgePanelRect MonitorBounds => EdgePanelNativeWindow.ToEdgeRect(_native.MonitorBounds);
    internal event Action<bool>? PointerChanged;
    internal RectInt32 WorkArea => _native.WorkArea;

    internal EdgePanelHandleWindow()
    {
        Title = "Dismode — uchwyt panelu";
        _pill = new()
        {
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            CornerRadius = new(3),
            Background = (Brush)Application.Current.Resources["DismodeAccentBrush"],
            Opacity = new EdgePanelPreferences().HandleOpacity,
            IsHitTestVisible = false,
        };
        // Staly obszar trafienia dziala takze przy 90% przezroczystosci.
        _hitArea = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _hitArea.Children.Add(_pill);
        _hitArea.PointerEntered += OnPointerEntered;
        _hitArea.PointerExited += OnPointerExited;
        Content = _hitArea;
        _native = new(this, roundCorners: false, enableHotStrip: true);
        _native.PointerChanged += OnNativePointerChanged;
        _native.GeometryChanged += OnGeometryChanged;
        Reposition();
        Show();
    }

    internal void SetTransparency(int percent) => _pill.Opacity = new EdgePanelPreferences(true, percent).HandleOpacity;

    internal void Show() => _native.Show();

    internal void AssertTopmost() => _native.AssertTopmost();

    private void OnPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        SetPointerInside(true);
    }

    private void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        SetPointerInside(EdgePanelNativeWindow.PointerInsideHotZone(MonitorBounds, HandleRect));
    }

    private void OnNativePointerChanged(bool inside) =>
        SetPointerInside(inside || EdgePanelNativeWindow.PointerInsideHotZone(MonitorBounds, HandleRect));

    private void SetPointerInside(bool inside)
    {
        if (_inside == inside)
        {
            return;
        }

        _inside = inside;
        _hover?.Stop();
        double width = inside ? 10 : 6;
        _pill.Background = new SolidColorBrush(inside ? Microsoft.UI.ColorHelper.FromArgb(255, 102, 235, 242)
            : ((SolidColorBrush)Application.Current.Resources["DismodeAccentBrush"]).Color);
        if (TacticalVfxService.Instance.IsEnabled && new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            DoubleAnimation animation = new()
            {
                From = _pill.ActualWidth,
                To = width,
                Duration = new(TimeSpan.FromMilliseconds(180)),
                EnableDependentAnimation = true,
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, _pill);
            Storyboard.SetTargetProperty(animation, "Width");
            _hover = new();
            _hover.Children.Add(animation);
            _hover.Begin();
        }
        else
        {
            _pill.Width = width;
        }

        PointerChanged?.Invoke(inside);
    }

    private void OnGeometryChanged() => DispatcherQueue.TryEnqueue(Reposition);

    internal void Reposition()
    {
        RectInt32 work = _native.WorkArea;
        int width = (int)Math.Ceiling(12 * _native.Scale);
        int height = (int)Math.Ceiling(96 * _native.Scale);
        RectInt32 bounds = new(work.X + work.Width - width, work.Y + (work.Height - height) / 2, width, height);
        if (bounds != HandleBounds)
        {
            HandleBounds = bounds;
            _native.Position(bounds);
        }

        _native.PositionHotStrip(EdgePanelMetrics.GetHotStripBounds(MonitorBounds));
    }

    public void Dispose()
    {
        _hover?.Stop();
        _hitArea.PointerEntered -= OnPointerEntered;
        _hitArea.PointerExited -= OnPointerExited;
        _native.PointerChanged -= OnNativePointerChanged;
        _native.GeometryChanged -= OnGeometryChanged;
        _native.Hide();
        _native.Dispose();
        Close();
    }
}
