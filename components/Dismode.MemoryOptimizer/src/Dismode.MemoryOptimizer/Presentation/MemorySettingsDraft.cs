using Dismode.MemoryOptimizer.Core.Models;

namespace Dismode.MemoryOptimizer.Presentation;

// The draft owns no IPC or UI objects. A reply may update the baseline, but it
// must never discard edits made while a request was in flight.
internal sealed class MemorySettingsDraft
{
    private int _saveGate;

    public MemoryOptimizerSettings Baseline { get; private set; } = new();

    public MemoryOptimizerSettings Current { get; private set; } = new();

    public bool IsInitialized { get; private set; }

    public bool IsSaving => Volatile.Read(ref _saveGate) != 0;

    public bool HasInvalidInput { get; private set; }

    public bool HasChanges => IsInitialized &&
        (HasInvalidInput || !Equivalent(Baseline, Current));

    public bool Receive(MemoryOptimizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MemoryOptimizerSettings normalized = MemoryOptimizerSettings.Normalize(settings);
        bool changed = !IsInitialized || !Equivalent(Baseline, normalized);
        Current = IsInitialized ? Merge(Baseline, Current, normalized) : Copy(normalized);
        Baseline = Copy(normalized);
        IsInitialized = true;
        return changed;
    }

    public void Edit(MemoryOptimizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (IsInitialized)
        {
            Current = Copy(settings);
            HasInvalidInput = false;
        }
    }

    public void MarkInvalidInput()
    {
        if (IsInitialized)
        {
            HasInvalidInput = true;
        }
    }

    public void Discard()
    {
        Current = Copy(Baseline);
        HasInvalidInput = false;
    }

    public Task<bool> SaveAsync(
        Func<MemoryOptimizerSettings, CancellationToken, Task<MemoryOptimizerSettings>> write,
        CancellationToken cancellationToken) =>
        PersistAsync(Current, write, windowPreferencesOnly: false, cancellationToken);

    public Task<bool> SaveWindowPreferencesAsync(
        bool compact,
        bool alwaysOnTop,
        Func<MemoryOptimizerSettings, CancellationToken, Task<MemoryOptimizerSettings>> write,
        CancellationToken cancellationToken) =>
        PersistAsync(
            Baseline with { CompactMode = compact, CompactAlwaysOnTop = alwaysOnTop },
            write,
            windowPreferencesOnly: true,
            cancellationToken);

    private async Task<bool> PersistAsync(
        MemoryOptimizerSettings submitted,
        Func<MemoryOptimizerSettings, CancellationToken, Task<MemoryOptimizerSettings>> write,
        bool windowPreferencesOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (!IsInitialized || Interlocked.CompareExchange(ref _saveGate, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            MemoryOptimizerSettings saved = MemoryOptimizerSettings.Normalize(
                await write(MemoryOptimizerSettings.Normalize(submitted), cancellationToken)
                    .ConfigureAwait(true));
            if (windowPreferencesOnly)
            {
                Receive(saved);
                Current = Current with
                {
                    CompactMode = saved.CompactMode,
                    CompactAlwaysOnTop = saved.CompactAlwaysOnTop,
                };
            }
            else
            {
                Current = Merge(submitted, Current, saved);
                Baseline = Copy(saved);
            }

            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _saveGate, 0);
        }
    }

    private static MemoryOptimizerSettings Copy(MemoryOptimizerSettings value) =>
        value with { ExcludedProcesses = value.ExcludedProcesses.ToArray() };

    private static bool Equivalent(
        MemoryOptimizerSettings left,
        MemoryOptimizerSettings right) =>
        left with { ExcludedProcesses = Array.Empty<string>() } ==
            right with { ExcludedProcesses = Array.Empty<string>() } &&
        SameExclusions(left.ExcludedProcesses, right.ExcludedProcesses);

    private static bool SameExclusions(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        new HashSet<string>(left, StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private static T Choose<T>(T baseline, T local, T remote) =>
        EqualityComparer<T>.Default.Equals(baseline, local) ? remote : local;

    private static MemoryOptimizerSettings Merge(
        MemoryOptimizerSettings baseline,
        MemoryOptimizerSettings local,
        MemoryOptimizerSettings remote) => remote with
        {
            AutomationEnabled = Choose(baseline.AutomationEnabled, local.AutomationEnabled, remote.AutomationEnabled),
            AutomationPaused = remote.AutomationPaused,
            AvailableMemoryThresholdPercent = Choose(baseline.AvailableMemoryThresholdPercent, local.AvailableMemoryThresholdPercent, remote.AvailableMemoryThresholdPercent),
            MaximumIdleCpuPercent = Choose(baseline.MaximumIdleCpuPercent, local.MaximumIdleCpuPercent, remote.MaximumIdleCpuPercent),
            IdleCpuMinutes = Choose(baseline.IdleCpuMinutes, local.IdleCpuMinutes, remote.IdleCpuMinutes),
            CooldownMinutes = Choose(baseline.CooldownMinutes, local.CooldownMinutes, remote.CooldownMinutes),
            PollIntervalSeconds = Choose(baseline.PollIntervalSeconds, local.PollIntervalSeconds, remote.PollIntervalSeconds),
            ScheduleEnabled = Choose(baseline.ScheduleEnabled, local.ScheduleEnabled, remote.ScheduleEnabled),
            ScheduleIntervalMinutes = Choose(baseline.ScheduleIntervalMinutes, local.ScheduleIntervalMinutes, remote.ScheduleIntervalMinutes),
            AdvancedModeEnabled = Choose(baseline.AdvancedModeEnabled, local.AdvancedModeEnabled, remote.AdvancedModeEnabled),
            AdvancedWarningAccepted = Choose(baseline.AdvancedWarningAccepted, local.AdvancedWarningAccepted, remote.AdvancedWarningAccepted),
            GlobalWorkingSet = Choose(baseline.GlobalWorkingSet, local.GlobalWorkingSet, remote.GlobalWorkingSet),
            GlobalWorkingSetWarningAccepted = Choose(baseline.GlobalWorkingSetWarningAccepted, local.GlobalWorkingSetWarningAccepted, remote.GlobalWorkingSetWarningAccepted),
            ManualAreas = Choose(baseline.ManualAreas, local.ManualAreas, remote.ManualAreas),
            AutomaticAreas = Choose(baseline.AutomaticAreas, local.AutomaticAreas, remote.AutomaticAreas),
            ExcludedProcesses = (SameExclusions(baseline.ExcludedProcesses, local.ExcludedProcesses)
                ? remote.ExcludedProcesses : local.ExcludedProcesses).ToArray(),
            NotificationsEnabled = Choose(baseline.NotificationsEnabled, local.NotificationsEnabled, remote.NotificationsEnabled),
            StartWithWindows = Choose(baseline.StartWithWindows, local.StartWithWindows, remote.StartWithWindows),
            StartMinimized = Choose(baseline.StartMinimized, local.StartMinimized, remote.StartMinimized),
            Hotkey = Choose(baseline.Hotkey, local.Hotkey, remote.Hotkey),
            CompactMode = Choose(baseline.CompactMode, local.CompactMode, remote.CompactMode),
            CompactAlwaysOnTop = Choose(baseline.CompactAlwaysOnTop, local.CompactAlwaysOnTop, remote.CompactAlwaysOnTop),
        };
}
