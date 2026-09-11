using GameShift.Windows.Sessions;

namespace GameShift.Windows.Processes;

public static class BackgroundOptimizationAdvisor
{
    public const long SignificantMemoryThresholdBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> HeavyBackgroundApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "firefox",
        "brave",
        "opera",
        "opera_gx",
        "vivaldi",
        "discord",
        "discordcanary",
        "discordptb",
        "spotify",
        "slack",
        "teams",
        "skype",
        "telegram",
        "viber",
        "epicgameslauncher",
        "galaxyclient",
        "battle.net",
        "obs64",
        "obs32",
    };

    public static bool ShouldRecommendOptimization(
        string processName,
        long workingSetBytes,
        bool hasMainWindow,
        bool isAggressive)
    {
        if (workingSetBytes >= SignificantMemoryThresholdBytes)
        {
            return true;
        }

        if (HeavyBackgroundApps.Contains(processName))
        {
            return true;
        }

        return isAggressive && hasMainWindow;
    }

    public static BackgroundProcessActionMode RecommendAction(
        string processName,
        bool hasMainWindow,
        bool canClose,
        bool isAggressive)
    {
        if (isAggressive && canClose && hasMainWindow)
        {
            return BackgroundProcessActionMode.CloseAndRestore;
        }

        // Tryb agresywny obiecuje najsilniejsze odwracalne ograniczenia,
        // a jedyna dzwignia CPU, ktora w pomiarze ruszyla czas klatki, to
        // twarda maska cwiartki — czyli pelny pakiet. Tryb zwykly zostaje
        // przy BelowNormal + EcoQoS, tak jak dotad.
        return isAggressive
            ? BackgroundProcessActionMode.RestrainBackground
            : BackgroundProcessActionMode.LowerPriorityAndEcoQos;
    }

    public static long EstimatePotentialMemorySavings(
        long workingSetBytes,
        BackgroundProcessActionMode action)
    {
        if (workingSetBytes <= 0)
        {
            return 0;
        }

        return action switch
        {
            BackgroundProcessActionMode.CloseAndRestore => workingSetBytes,
            BackgroundProcessActionMode.LowerPriorityAndEcoQos
                or BackgroundProcessActionMode.RestrainBackground =>
                    (long)(workingSetBytes * 0.6),
            _ => (long)(workingSetBytes * 0.3),
        };
    }
}
