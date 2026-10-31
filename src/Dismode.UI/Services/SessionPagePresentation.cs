using Dismode.Core.History;

namespace Dismode.UI.Services;

internal static class SessionPagePresentation
{
    public static (int Count, long WorkingSetBytes) BackgroundSnapshot(IEnumerable<long> readings)
    {
        int count = 0;
        long bytes = 0;
        foreach (long reading in readings)
        {
            count++;
            bytes += reading;
        }

        return (count, bytes);
    }

    public static TimeSpan? RecordedPlayTime(IEnumerable<SessionSummary> history)
    {
        long? total = null;
        foreach (SessionSummary summary in history)
        {
            if (summary.DurationSeconds is long seconds)
            {
                total = (total ?? 0) + seconds;
            }
        }

        return total is long recorded ? TimeSpan.FromSeconds(recorded) : null;
    }

    public static IReadOnlyList<string> OptionLabels(
        bool remember, bool automatic, bool preferredCores, bool awake,
        bool lateRules, bool refreshRate, bool tracking, bool overlay,
        bool proBalance, int selectedApplications)
    {
        List<string> actions = [];
        if (remember)
        {
            actions.Add("Pamiętaj mój wybór");
        }

        if (automatic)
        {
            actions.Add("Start automatyczny");
        }

        if (preferredCores)
        {
            actions.Add("Mocne rdzenie");
        }

        if (awake)
        {
            actions.Add("Ekran nie zasypia");
        }

        if (lateRules)
        {
            actions.Add("Pilnuj tła");
        }

        if (refreshRate)
        {
            actions.Add("Pełne odświeżanie");
        }

        if (tracking)
        {
            actions.Add("Mierz FPS");
        }

        if (overlay)
        {
            actions.Add("Licznik w grze");
        }

        if (proBalance)
        {
            actions.Add("Ograniczaj procesy tła w trakcie gry");
        }

        if (selectedApplications > 0)
        {
            actions.Add("Aplikacje w tle");
        }

        return actions;
    }
}
