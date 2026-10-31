using System.Globalization;
using Dismode.Core.History;

namespace Dismode.UI.ViewModels;

/// <summary>
/// One game on the results page. Artwork comes from the library profile when
/// the game is still there; a game removed from the library keeps its tile
/// with the letter fallback.
/// </summary>
public sealed class HistoryGameTile
{
    public HistoryGameTile(GameSessionHistory history, ProfileListItem? profile)
    {
        ArgumentNullException.ThrowIfNull(history);

        History = history;
        Profile = profile;
        GameDisplayName = history.GameDisplayName;
        Initial = GameDisplayName[..1].ToUpperInvariant();
        SessionsLabel = string.Create(
            CultureInfo.CurrentCulture,
            $"{FormatSessionCount(history.Totals.SessionCount)} · ostatnio "
                + $"{history.LatestSession.EndedAtUtc.ToLocalTime():d}");
        FrameRateLabel = history.LatestFrameRate is { } frameRate
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Ostatni pomiar: Śr. {frameRate.AverageFramesPerSecond:0.0} FPS")
            : "Brak pomiaru FPS";
        int troubled = history.Totals.TroubledSessionCount;
        ProblemLabel = string.Create(
            CultureInfo.CurrentCulture,
            $"Sesji z problemem: {troubled}");
        ProblemVisibility = troubled > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    public GameSessionHistory History { get; }

    public ProfileListItem? Profile { get; }

    public string GameDisplayName { get; }

    public string Initial { get; }

    public string SessionsLabel { get; }

    public string FrameRateLabel { get; }

    public string ProblemLabel { get; }

    public Microsoft.UI.Xaml.Visibility ProblemVisibility { get; }

    public override string ToString() =>
        ProblemVisibility == Microsoft.UI.Xaml.Visibility.Visible
            ? $"{GameDisplayName}. {SessionsLabel}. {FrameRateLabel}. {ProblemLabel}"
            : $"{GameDisplayName}. {SessionsLabel}. {FrameRateLabel}";

    private static string FormatSessionCount(int count)
    {
        int lastTwoDigits = count % 100;
        int lastDigit = count % 10;
        string noun = count == 1
            ? "sesja"
            : lastDigit is >= 2 and <= 4
                && lastTwoDigits is not (>= 12 and <= 14)
                    ? "sesje"
                    : "sesji";
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{count} {noun}");
    }
}
