using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace GameShift.UI.Services;

public sealed class TacticalVfxService
{
    private static readonly Lazy<TacticalVfxService> s_instance = new(() => new TacticalVfxService());
    public static TacticalVfxService Instance => s_instance.Value;

    public bool IsEnabled { get; set; } = true;

    private TacticalVfxService()
    {
    }

    public void AnimatePulse(UIElement element)
    {
        if (!IsEnabled || element is null)
        {
            return;
        }

        try
        {
            Storyboard storyboard = new();
            DoubleAnimation animation = new()
            {
                From = 1.0,
                To = 0.45,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(2),
            };

            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch
        {
            // Fallback gracefully if compositor animation fails
        }
    }

    public void AnimateQuickFlash(UIElement element)
    {
        if (!IsEnabled || element is null)
        {
            return;
        }

        try
        {
            Storyboard storyboard = new();
            DoubleAnimation animation = new()
            {
                From = 0.3,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                AutoReverse = false,
            };

            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch
        {
        }
    }
}
