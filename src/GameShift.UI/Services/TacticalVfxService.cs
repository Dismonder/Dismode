using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
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

    /// <summary>
    /// Wejscie strony po przelaczeniu zakladki: krotkie wyplyniecie w gore
    /// z zanikaniem. Animacja niejawna warstwy kompozycji odpala sie sama,
    /// gdy strona dostaje Visibility.Visible, i liczy ja kompozytor poza
    /// watkiem UI, wiec nie obciaza przelaczania. Wylaczona, gdy efekty sa
    /// wylaczone w GameShift albo animacje sa wylaczone w systemie.
    /// </summary>
    public void ConfigurePageEntrance(IEnumerable<UIElement> pages)
    {
        bool animate = IsEnabled
            && new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        foreach (UIElement page in pages)
        {
            try
            {
                if (!animate)
                {
                    ElementCompositionPreview.SetImplicitShowAnimation(page, null);
                    continue;
                }

                Compositor compositor =
                    ElementCompositionPreview.GetElementVisual(page).Compositor;
                ElementCompositionPreview.SetIsTranslationEnabled(page, true);
                CompositionEasingFunction easing =
                    compositor.CreateCubicBezierEasingFunction(
                        new Vector2(0.1f, 0.9f),
                        new Vector2(0.2f, 1f));

                ScalarKeyFrameAnimation fade =
                    compositor.CreateScalarKeyFrameAnimation();
                fade.Target = "Opacity";
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f, easing);
                fade.Duration = TimeSpan.FromMilliseconds(220);

                Vector3KeyFrameAnimation slide =
                    compositor.CreateVector3KeyFrameAnimation();
                slide.Target = "Translation";
                slide.InsertKeyFrame(0f, new Vector3(0f, 18f, 0f));
                slide.InsertKeyFrame(1f, Vector3.Zero, easing);
                slide.Duration = TimeSpan.FromMilliseconds(320);

                CompositionAnimationGroup entrance =
                    compositor.CreateAnimationGroup();
                entrance.Add(fade);
                entrance.Add(slide);
                ElementCompositionPreview.SetImplicitShowAnimation(
                    page,
                    entrance);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException)
            {
                // Strona bez animacji nadal dziala; efekt jest opcjonalny.
            }
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
