// SPDX-License-Identifier: GPL-3.0-only
using Dismode.MemoryOptimizer.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Dismode.MemoryOptimizer;

internal sealed class TacticalVfxService
{
    private readonly Dictionary<DependencyObject, Storyboard> _running = [];
    private readonly UISettings _settings = new();

    public bool IsEnabled { get; set; } = true;

    public bool AnimationsEnabled => IsEnabled && _settings.AnimationsEnabled;

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

    private void Animate(
        DependencyObject element,
        string property,
        double from,
        double to,
        bool isVisible)
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
            if (_running.TryGetValue(element, out Storyboard? current) &&
                ReferenceEquals(current, storyboard))
            {
                _running.Remove(element);
            }
        };
        storyboard.Begin();
    }
}

public sealed partial class MainWindow
{
    private readonly TacticalVfxService _effects = new();

    private void StopPresentationAnimations()
    {
        _effects.Stop();
        RamUsageRing.IsActive = false;
        OptimizeProgressRing.IsActive = false;
        OptimizeProgressRing.Opacity = 0;
        SaveProgressRing.IsActive = false;
        SaveProgressRing.Opacity = 0;
        CompactBusyProgress.IsIndeterminate = false;
        CompactBusyProgress.Opacity = 0;
    }

    private void UpdateActivityIndicators()
    {
        MemoryActivityState activity = MemoryActivityState.Create(
            _refreshPolicy.IsVisible,
            _refreshPolicy.IsCompact,
            _operationGate.IsRunning || (_status?.IsOptimizationRunning ?? false),
            _settingsDraft.IsSaving,
            _effects.AnimationsEnabled);
        RamUsageRing.IsActive = activity.RamUsage;
        OptimizeProgressRing.IsActive = activity.Optimize;
        OptimizeProgressRing.Opacity = activity.Optimize ? 1 : 0;
        SaveProgressRing.IsActive = activity.Save;
        SaveProgressRing.Opacity = activity.Save ? 1 : 0;
        CompactBusyProgress.IsIndeterminate = activity.Compact;
        CompactBusyProgress.Opacity = activity.Compact ? 1 : 0;
    }

    private void AnimateCurrentPage()
    {
        foreach (UIElement page in new UIElement[]
                 { OverviewPage, AutomationPage, ProcessesPage, HistoryPage, AdvancedPage })
        {
            if (page.Visibility == Visibility.Visible)
            {
                _effects.Fade(page, _refreshPolicy.IsVisible);
            }
        }
    }

    private void TweenRamUsage(double targetPercent)
    {
        double prevRing = RamUsageRing.Value;
        double prevCompact = CompactRamProgress.Value;
        RamUsageRing.Value = targetPercent;
        CompactRamProgress.Value = targetPercent;
        _effects.Tween(RamUsageRing, prevRing, targetPercent, _refreshPolicy.IsVisible);
        _effects.Tween(CompactRamProgress, prevCompact, targetPercent, _refreshPolicy.IsVisible);
    }

    private void ToggleDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle ||
            toggle.Tag is not string targetName ||
            RootLayout.FindName(targetName) is not UIElement target)
        {
            return;
        }

        bool expanded = toggle.IsChecked == true;
        target.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded)
        {
            _effects.Fade(target, _refreshPolicy.IsVisible);
        }
    }
}
