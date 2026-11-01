using Dismode.SystemOptimizer.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Dismode.SystemOptimizer;

public sealed partial class MainWindow
{
    private readonly TacticalVfxService _effects = new();

    private void StopPresentationAnimations()
    {
        _effects.Stop();
        OperationProgressRing.IsActive = false;
        OperationProgressRing.Opacity = 0;
    }

    private void AnimatePage(string page) =>
        _effects.Fade(GetPageElement(page), IsPresentationVisible);

    private void TweenExperimentProgress(double targetValue)
    {
        double previous = ExperimentProgressBar.Value;
        ExperimentProgressBar.Value = targetValue;
        _effects.Tween(
            ExperimentProgressBar,
            previous,
            targetValue,
            IsPresentationVisible);
    }

    private void ToggleDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle
            || toggle.Tag is not string targetName
            || RootLayout.FindName(targetName) is not UIElement target)
        {
            return;
        }

        bool expanded = toggle.IsChecked == true;
        target.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded)
        {
            _effects.Fade(target, IsPresentationVisible);
        }
    }

    private Microsoft.UI.Xaml.Controls.ScrollViewer GetPageElement(string page) => page switch
    {
        "hardware" => HardwarePage,
        "games" => GamesPage,
        "experiments" => ExperimentsPage,
        "global" => GlobalPage,
        "laboratory" => LaboratoryPage,
        "danger" => DangerPage,
        "history" => HistoryPage,
        _ => OverviewPage,
    };
}
