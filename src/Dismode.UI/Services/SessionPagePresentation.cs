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

    public static string DescribeOptions(
        bool remember, bool automatic, bool preferredCores, bool awake,
        bool lateRules, bool refreshRate, bool tracking, bool overlay,
        bool proBalance, int selectedApplications)
    {
        List<string> actions = [];
        if (remember)
        {
            actions.Add("zapamięta ustawienia");
        }

        if (automatic)
        {
            actions.Add("automatycznie dołączy do gry uruchomionej poza Dismode");
        }

        if (preferredCores)
        {
            actions.Add("wybierze wydajne rdzenie");
        }

        if (awake)
        {
            actions.Add("nie pozwoli ekranowi zasnąć");
        }

        if (lateRules)
        {
            actions.Add("przypilnuje zapisanych reguł tła także po starcie");
        }

        if (refreshRate)
        {
            actions.Add("ustawi najwyższe odświeżanie");
        }

        if (tracking)
        {
            actions.Add("zmierzy FPS");
        }

        if (overlay)
        {
            actions.Add("pokaże nakładkę");
        }

        if (proBalance)
        {
            actions.Add("przyhamuje aktywne aplikacje w tle");
        }

        if (selectedApplications > 0)
        {
            actions.Add($"zastosuje wybrane działania w tle ({selectedApplications})");
        }

        return actions.Count == 0
            ? "Uruchomi grę bez dodatkowych zmian."
            : "Dismode: " + string.Join(" · ", actions) + ".";
    }
}
