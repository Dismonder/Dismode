using System.Globalization;
using GameShift.Core.History;

namespace GameShift.UI.ViewModels;

public sealed class HistoryListItem
{
    public HistoryListItem(SessionSummary summary)
    {
        Summary = summary;
        GameDisplayName = summary.GameDisplayName;
        EndedAtLabel = summary.EndedAtUtc
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture);
        DurationLabel =
            FormatDuration(summary.EndedAtUtc - summary.StartedAtUtc);
        StatusLabel = summary.Status switch
        {
            SessionCompletionStatus.Completed => "Zakończona",
            SessionCompletionStatus.RestoredWithConflicts =>
                "Przywrócona z konfliktami",
            SessionCompletionStatus.PartiallyRestored =>
                "Częściowo przywrócona",
            SessionCompletionStatus.RecoveredAfterCrash =>
                "Odzyskana po awarii",
            SessionCompletionStatus.FailedBeforeApply =>
                "Przerwana przed zmianami",
            _ => "Nieznany wynik",
        };
        ActionSummary =
            $"{summary.RestoredActionCount}/{summary.AppliedActionCount} "
            + "zmian przywrócono";
        IssueSummary = summary.ConflictCount == 0 && summary.ErrorCount == 0
            ? "Bez konfliktów i błędów"
            : $"{summary.ConflictCount} konfliktów, "
                + $"{summary.ErrorCount} błędów";
        FrameRateSummary = FormatFrameRate(summary.FrameRateStatistics);
    }

    public SessionSummary Summary { get; }

    public string GameDisplayName { get; }

    public string EndedAtLabel { get; }

    public string DurationLabel { get; }

    public string StatusLabel { get; }

    public string ActionSummary { get; }

    public string IssueSummary { get; }

    public string FrameRateSummary { get; }

    public override string ToString() =>
        $"{GameDisplayName}. {StatusLabel}. {EndedAtLabel}. "
        + $"{DurationLabel}. {ActionSummary}. {FrameRateSummary}. "
        + $"{IssueSummary}";

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{(int)duration.TotalHours} godz. {duration.Minutes} min");
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{Math.Max(0, duration.Minutes)} min");
    }

    private static string FormatFrameRate(
        SessionFrameRateStatistics? statistics)
    {
        if (statistics is null)
        {
            return "Pomiar FPS: brak wystarczających prawdziwych próbek";
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "Pomiar interwałowy: śr. {0:0.0} FPS • {1:0.00} ms • "
                + "najniższa próbka {2:0.0} FPS • "
                + "najwyższa próbka {3:0.00} ms • {4}",
            statistics.AverageFramesPerSecond,
            statistics.AverageFrameTimeMilliseconds,
            statistics.MinimumFramesPerSecond,
            statistics.MaximumFrameTimeMilliseconds,
            FormatSampleCount(statistics.SampleCount));
    }

    private static string FormatSampleCount(int count)
    {
        int lastTwoDigits = count % 100;
        int lastDigit = count % 10;
        string noun = count == 1
            ? "próbka"
            : lastDigit is >= 2 and <= 4
                && lastTwoDigits is not (>= 12 and <= 14)
                    ? "próbki"
                    : "próbek";
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{count} {noun}");
    }
}
