using System.Collections.ObjectModel;

namespace GameShift.MemoryOptimizer.Core.Models;

[Flags]
public enum MemoryArea
{
    None = 0,
    CombinedPageList = 1,
    ModifiedFileCache = 2,
    ModifiedPageList = 4,
    RegistryCache = 8,
    StandbyList = 16,
    StandbyListLowPriority = 32,
    SystemFileCache = 64,
    WorkingSet = 128,
}

public enum OptimizationTrigger
{
    Manual = 1,
    LowMemory = 2,
    Schedule = 3,
    Hotkey = 4,
}

public enum OptimizationState
{
    Completed = 1,
    PartiallyCompleted = 2,
    Blocked = 3,
    AlreadyRunning = 4,
    Cancelled = 5,
    Failed = 6,
}

public sealed record MemorySnapshot(
    DateTimeOffset CapturedAtUtc,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    ulong TotalVirtualBytes,
    ulong AvailableVirtualBytes,
    uint MemoryLoadPercent,
    ulong CommitLimitBytes = 0,
    ulong AvailableCommitBytes = 0)
{
    public ulong UsedPhysicalBytes =>
        TotalPhysicalBytes >= AvailablePhysicalBytes
            ? TotalPhysicalBytes - AvailablePhysicalBytes
            : 0;

    public ulong CommittedBytes =>
        CommitLimitBytes >= AvailableCommitBytes
            ? CommitLimitBytes - AvailableCommitBytes
            : 0;
}

public sealed record MemoryAreaResult(
    MemoryArea Area,
    bool Succeeded,
    TimeSpan Duration,
    int? Win32Error,
    string Message,
    ulong WorkingSetBytesReleased = 0);

public sealed record OptimizationResult(
    Guid OperationId,
    OptimizationTrigger Trigger,
    MemoryArea RequestedAreas,
    OptimizationState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    MemorySnapshot Before,
    MemorySnapshot After,
    IReadOnlyList<MemoryAreaResult> AreaResults,
    string Message)
{
    public long AvailableMemoryDeltaBytes =>
        checked((long)After.AvailablePhysicalBytes -
            (long)Before.AvailablePhysicalBytes);

    public ulong WorkingSetBytesReleased =>
        AreaResults.Aggregate(
            0UL,
            static (total, item) =>
                checked(total + item.WorkingSetBytesReleased));
}

public sealed record GuardDecision(bool IsBlocked, string Code, string Message)
{
    public static GuardDecision Allowed { get; } = new(false, "allowed", "");
}

public sealed record MemoryOptimizerStatus(
    bool IsPaused,
    bool IsOptimizationRunning,
    MemorySnapshot Memory,
    MemoryOptimizerSettings Settings,
    OptimizationResult? LastResult,
    string ServiceVersion,
    string StatusMessage);

public sealed record ComponentStatusDocument(
    int SchemaVersion,
    bool IsPaused,
    bool IsOptimizationRunning,
    DateTimeOffset UpdatedAtUtc,
    string ServiceVersion);

public sealed record KnownGameEntry(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ExecutableNames);

public sealed record KnownGamesDocument(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<KnownGameEntry> Games);

public sealed record ActiveGameDocument(
    int SchemaVersion,
    string GameId,
    string DisplayName,
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record ProcessMemoryItem(
    int ProcessId,
    string Name,
    ulong WorkingSetBytes,
    bool IsExcluded);

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000,
}

public readonly record struct HotkeyBinding(
    HotkeyModifiers Modifiers,
    uint VirtualKey,
    string CanonicalText)
{
    public static bool TryParse(string? value, out HotkeyBinding binding)
    {
        binding = default;
        string[] parts = (value ?? string.Empty)
            .Split(
                '+',
                StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        foreach (string modifier in parts[..^1])
        {
            HotkeyModifiers parsed = modifier.ToUpperInvariant() switch
            {
                "ALT" => HotkeyModifiers.Alt,
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };
            if (parsed == HotkeyModifiers.None || (modifiers & parsed) != 0)
            {
                return false;
            }

            modifiers |= parsed;
        }

        string key = parts[^1].ToUpperInvariant();
        uint virtualKey;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            virtualKey = key[0];
        }
        else if (key.Length is 2 or 3 && key[0] == 'F' &&
                 int.TryParse(
                     key.AsSpan(1),
                     System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out int functionKey) &&
                 functionKey is >= 1 and <= 12)
        {
            virtualKey = checked((uint)(0x70 + functionKey - 1));
        }
        else
        {
            return false;
        }

        List<string> canonical = [];
        AddModifier(HotkeyModifiers.Control, "Ctrl");
        AddModifier(HotkeyModifiers.Alt, "Alt");
        AddModifier(HotkeyModifiers.Shift, "Shift");
        AddModifier(HotkeyModifiers.Windows, "Win");
        canonical.Add(key);
        binding = new(modifiers, virtualKey, string.Join('+', canonical));
        return true;

        void AddModifier(HotkeyModifiers modifier, string name)
        {
            if ((modifiers & modifier) != 0)
            {
                canonical.Add(name);
            }
        }
    }
}

public sealed record MemoryOptimizerSettings
{
    public const int CurrentSchemaVersion = 2;
    public const MemoryArea BasicAreas =
        MemoryArea.WorkingSet | MemoryArea.StandbyListLowPriority;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool AutomationEnabled { get; init; } = true;

    public bool AutomationPaused { get; init; }

    public int AvailableMemoryThresholdPercent { get; init; } = 20;

    public int MaximumIdleCpuPercent { get; init; } = 5;

    public int IdleCpuMinutes { get; init; } = 5;

    public int CooldownMinutes { get; init; } = 30;

    public int PollIntervalSeconds { get; init; } = 15;

    public bool ScheduleEnabled { get; init; }

    public int ScheduleIntervalMinutes { get; init; } = 240;

    public bool AdvancedModeEnabled { get; init; }

    public bool AdvancedWarningAccepted { get; init; }

    public bool GlobalWorkingSet { get; init; }

    public bool GlobalWorkingSetWarningAccepted { get; init; }

    public MemoryArea ManualAreas { get; init; } = BasicAreas;

    public MemoryArea AutomaticAreas { get; init; } = BasicAreas;

    public IReadOnlyList<string> ExcludedProcesses { get; init; } =
        Array.Empty<string>();

    public bool NotificationsEnabled { get; init; } = true;

    public bool StartWithWindows { get; init; } = true;

    public bool StartMinimized { get; init; } = true;

    public string Hotkey { get; init; } = "Ctrl+Shift+M";

    public bool CompactMode { get; init; }

    public bool CompactAlwaysOnTop { get; init; }

    public static MemoryOptimizerSettings Normalize(
        MemoryOptimizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool advancedEnabled = settings.AdvancedModeEnabled &&
            settings.AdvancedWarningAccepted;
        MemoryArea manualAreas = NormalizeAreas(
            settings.ManualAreas,
            advancedEnabled);
        MemoryArea automaticAreas = NormalizeAreas(
            settings.AutomaticAreas,
            advancedEnabled);

        bool globalWorkingSet = advancedEnabled &&
            settings.GlobalWorkingSet &&
            settings.GlobalWorkingSetWarningAccepted;
        IReadOnlyList<string> exclusions = globalWorkingSet
            ? Array.Empty<string>()
            : new ReadOnlyCollection<string>(
                settings.ExcludedProcesses
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Select(static name => Path.GetFileNameWithoutExtension(
                        name.Trim()))
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Take(256)
                    .ToArray());

        return settings with
        {
            SchemaVersion = CurrentSchemaVersion,
            AvailableMemoryThresholdPercent = Math.Clamp(
                settings.AvailableMemoryThresholdPercent,
                5,
                50),
            MaximumIdleCpuPercent = Math.Clamp(
                settings.MaximumIdleCpuPercent,
                1,
                50),
            IdleCpuMinutes = Math.Clamp(settings.IdleCpuMinutes, 1, 30),
            CooldownMinutes = Math.Clamp(settings.CooldownMinutes, 5, 1440),
            PollIntervalSeconds = Math.Clamp(
                settings.PollIntervalSeconds,
                5,
                300),
            ScheduleIntervalMinutes = Math.Clamp(
                settings.ScheduleIntervalMinutes,
                30,
                10_080),
            AdvancedModeEnabled = advancedEnabled,
            AdvancedWarningAccepted = advancedEnabled,
            GlobalWorkingSet = globalWorkingSet,
            ManualAreas = manualAreas,
            AutomaticAreas = automaticAreas,
            ExcludedProcesses = exclusions,
            Hotkey = HotkeyBinding.TryParse(
                settings.Hotkey,
                out HotkeyBinding hotkey)
                    ? hotkey.CanonicalText
                    : "Ctrl+Shift+M",
        };
    }

    private static MemoryArea NormalizeAreas(
        MemoryArea areas,
        bool advancedEnabled)
    {
        const MemoryArea allAreas =
            MemoryArea.CombinedPageList |
            MemoryArea.ModifiedFileCache |
            MemoryArea.ModifiedPageList |
            MemoryArea.RegistryCache |
            MemoryArea.StandbyList |
            MemoryArea.StandbyListLowPriority |
            MemoryArea.SystemFileCache |
            MemoryArea.WorkingSet;

        MemoryArea normalized = areas & allAreas;
        if (!advancedEnabled)
        {
            normalized &= BasicAreas;
        }

        if ((normalized & MemoryArea.StandbyList) != 0 &&
            (normalized & MemoryArea.StandbyListLowPriority) != 0)
        {
            normalized &= ~MemoryArea.StandbyListLowPriority;
        }

        return normalized == MemoryArea.None ? BasicAreas : normalized;
    }
}
