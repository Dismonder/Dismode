using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Dismode.SystemOptimizer.Presentation;

internal sealed class TacticalVfxService
{
    private readonly Dictionary<DependencyObject, Storyboard> _running = [];
    private readonly UISettings _settings = new();

    public bool IsEnabled { get; set; } = true;

    public void Fade(UIElement element, bool isVisible) =>
        Animate(element, "Opacity", 0.86, 1, isVisible);

    public void Tween(DependencyObject element, double from, double to, bool isVisible) =>
        Animate(element, "Value", from, to, isVisible);

    public void Stop()
    {
        foreach (Storyboard storyboard in _running.Values)
        {
            storyboard.Stop();
        }

        _running.Clear();
    }

    private void Animate(DependencyObject element, string property, double from, double to, bool isVisible)
    {
        if (_running.Remove(element, out Storyboard? previous))
        {
            previous.Stop();
        }

        if (!IsEnabled || !isVisible || !_settings.AnimationsEnabled || Math.Abs(from - to) < 0.001)
        {
            return;
        }

        DoubleAnimation animation = new()
        {
            From = from,
            To = to,
            Duration = new(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, property);
        Storyboard storyboard = new();
        storyboard.Children.Add(animation);
        _running[element] = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (_running.TryGetValue(element, out Storyboard? current) && ReferenceEquals(current, storyboard))
            {
                _running.Remove(element);
            }
        };
        storyboard.Begin();
    }
}
