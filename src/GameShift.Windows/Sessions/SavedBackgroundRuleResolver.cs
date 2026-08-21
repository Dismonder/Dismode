using GameShift.Core.Profiles;
using GameShift.Windows.Processes;

namespace GameShift.Windows.Sessions;

public sealed class SavedBackgroundRuleResolver
{
    private readonly IProcessInventory _processInventory;

    public SavedBackgroundRuleResolver(
        IProcessInventory? processInventory = null)
    {
        _processInventory = processInventory ?? new ProcessInventory();
    }

    public SavedBackgroundRuleResolution Resolve(
        GameOptimizationPreferences preferences,
        string gameExecutablePath,
        int currentSessionId,
        int maximumProcessCount)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameExecutablePath);
        if (currentSessionId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentSessionId),
                currentSessionId,
                "The Windows session identifier cannot be negative.");
        }

        if (maximumProcessCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumProcessCount),
                maximumProcessCount,
                "The process limit must be positive.");
        }

        Dictionary<string, SavedBackgroundActionMode> configuredRules =
            preferences.BackgroundRules
                .Where(rule =>
                    rule.ActionMode != SavedBackgroundActionMode.Ignore)
                .ToDictionary(
                    rule => rule.ExecutablePath,
                    rule => rule.ActionMode,
                    StringComparer.OrdinalIgnoreCase);
        if (configuredRules.Count == 0)
        {
            return SavedBackgroundRuleResolution.Empty;
        }

        ProcessSnapshot[] inventory = _processInventory.Capture().ToArray();
        Dictionary<string, int> interactiveInstanceCounts =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessSnapshot process in inventory)
        {
            if (!process.HasMainWindow
                || string.IsNullOrWhiteSpace(process.ExecutablePath)
                || !TryGetFullPath(
                    process.ExecutablePath,
                    out string executablePath))
            {
                continue;
            }

            interactiveInstanceCounts[executablePath] =
                interactiveInstanceCounts.GetValueOrDefault(executablePath)
                + 1;
        }
        HashSet<string> matchedRulePaths =
            new(StringComparer.OrdinalIgnoreCase);
        List<BackgroundApplicationSelection> selections = [];

        foreach (ProcessSnapshot process in inventory
                     .OrderByDescending(item => item.WorkingSetBytes)
                     .ThenBy(item => item.ProcessId))
        {
            if (selections.Count >= maximumProcessCount
                || process.StartedAtUtc is not DateTimeOffset startedAtUtc
                || string.IsNullOrWhiteSpace(process.ExecutablePath)
                || !TryGetFullPath(
                    process.ExecutablePath,
                    out string executablePath)
                || !configuredRules.TryGetValue(
                    executablePath,
                    out SavedBackgroundActionMode savedMode)
                || !IsCurrentlyEligible(
                    process,
                    executablePath,
                    savedMode,
                    gameExecutablePath,
                    currentSessionId,
                    interactiveInstanceCounts))
            {
                continue;
            }

            selections.Add(
                new(
                    process.ProcessId,
                    startedAtUtc,
                    MapAction(savedMode)));
            matchedRulePaths.Add(executablePath);
        }

        return new(
            selections,
            configuredRules.Count,
            matchedRulePaths.Count);
    }

    private static bool IsCurrentlyEligible(
        ProcessSnapshot process,
        string executablePath,
        SavedBackgroundActionMode savedMode,
        string gameExecutablePath,
        int currentSessionId,
        Dictionary<string, int> interactiveInstanceCounts)
    {
        ProcessClassification classification =
            ProcessClassificationService.Classify(
                process,
                currentSessionId,
                gameExecutablePath);
        if (process.ProcessId == Environment.ProcessId
            || classification.Kind
            != ProcessSafetyClassification.OptionalUser)
        {
            return false;
        }

        return savedMode switch
        {
            SavedBackgroundActionMode.CloseAndRestore =>
                process.HasMainWindow
                && interactiveInstanceCounts.TryGetValue(
                    executablePath,
                    out int instanceCount)
                && instanceCount == 1,
            SavedBackgroundActionMode.LowerPriority
                or SavedBackgroundActionMode.LowerPriorityAndEcoQos =>
                    process.PriorityClass is "Normal" or "BelowNormal",
            _ => false,
        };
    }

    private static BackgroundProcessActionMode MapAction(
        SavedBackgroundActionMode savedMode) =>
        savedMode switch
        {
            SavedBackgroundActionMode.CloseAndRestore =>
                BackgroundProcessActionMode.CloseAndRestore,
            SavedBackgroundActionMode.LowerPriorityAndEcoQos =>
                BackgroundProcessActionMode.LowerPriorityAndEcoQos,
            _ => BackgroundProcessActionMode.LowerPriority,
        };

    private static bool TryGetFullPath(
        string executablePath,
        out string fullPath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(executablePath)
                || !string.Equals(
                    Path.GetExtension(executablePath),
                    ".exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                fullPath = string.Empty;
                return false;
            }

            fullPath = Path.GetFullPath(executablePath);
            return true;
        }
        catch (Exception exception) when (
            exception is
                ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            fullPath = string.Empty;
            return false;
        }
    }
}

public sealed record SavedBackgroundRuleResolution(
    IReadOnlyList<BackgroundApplicationSelection> Selections,
    int ConfiguredRuleCount,
    int MatchedRuleCount)
{
    public static SavedBackgroundRuleResolution Empty { get; } =
        new([], 0, 0);
}
