using System.Globalization;
using Dismode.MemoryOptimizer.Core.Models;

namespace Dismode.MemoryOptimizer.Presentation;

internal sealed record TrayMenuState
{
    public uint? Percent { get; init; }
    public string PercentText { get; init; } = "—";
    public string AvailableText { get; init; } = "—";
    public string UsageText { get; init; } = "Brak aktualnego pomiaru";
    public string PressureText { get; init; } = "";
    public string PressureBrush { get; init; } = "MemoryMutedBrush";
    public string StatusText { get; init; } = "Brak połączenia z usługą";
    public string DetailText { get; init; } = "Otwórz panel, aby sprawdzić połączenie.";
    public string StatusBrush { get; init; } = "MemoryWarningBrush";
    public string PauseText { get; init; } = "Wstrzymaj";
    public bool IsBusy { get; init; }
    public bool CanOptimize { get; init; }
    public bool CanPause { get; init; }

    public static TrayMenuState Create(
        MemoryOptimizerStatus? status,
        bool canOptimize,
        bool canPause,
        IFormatProvider? culture = null)
    {
        if (status is null)
        {
            return new();
        }

        culture ??= CultureInfo.CurrentCulture;
        MemorySnapshot memory = status.Memory;
        uint percent = Math.Min(memory.MemoryLoadPercent, 100);
        bool automatic = status.Settings.AutomationEnabled || status.Settings.ScheduleEnabled;
        bool busy = status.IsOptimizationRunning;
        string detail = busy
            ? "Trwa sprawdzanie zabezpieczeń i wykonanie profilu."
            : $"Próg: {status.Settings.AvailableMemoryThresholdPercent}% wolnego RAM · " +
                $"odstęp: {status.Settings.CooldownMinutes} min";
        if (!busy && status.LastResult is { } result)
        {
            detail = result.State switch
            {
                OptimizationState.Completed or OptimizationState.PartiallyCompleted =>
                    "Ostatnia zmiana dostępnego RAM: " +
                    ((result.After.AvailablePhysicalBytes / 1048576m) -
                        (result.Before.AvailablePhysicalBytes / 1048576m))
                    .ToString("+0;-0;0", culture) + " MB" +
                    (result.State == OptimizationState.PartiallyCompleted ? " · częściowo" : ""),
                OptimizationState.Blocked => "Ostatnia próba zablokowana: " +
                    MemoryUiFormatting.DescribeServiceMessage(result.Message),
                _ => "Ostatnia próba: " +
                    MemoryUiFormatting.DescribeServiceMessage(result.Message),
            };
        }

        return new()
        {
            Percent = percent,
            PercentText = percent.ToString(culture) + "%",
            AvailableText = MemoryUiFormatting.FormatBytes(memory.AvailablePhysicalBytes, culture),
            UsageText = MemoryUiFormatting.FormatBytes(memory.UsedPhysicalBytes, culture) +
                " z " + MemoryUiFormatting.FormatBytes(memory.TotalPhysicalBytes, culture),
            PressureText = percent >= 90 ? "Wysokie użycie" : percent >= 80 ? "Podwyższone" : "",
            PressureBrush = percent >= 90 ? "MemoryDangerBrush" : percent >= 80
                ? "MemoryWarningBrush" : "MemoryAccentBrush",
            StatusText = busy ? "Optymalizacja w toku" : status.IsPaused
                ? "Automat wstrzymany" : automatic ? "Automat aktywny" : "Tryb ręczny",
            StatusBrush = busy || status.IsPaused ? "MemoryWarningBrush" : "MemorySuccessBrush",
            DetailText = detail,
            PauseText = status.IsPaused ? "Wznów" : "Wstrzymaj",
            IsBusy = busy,
            CanOptimize = canOptimize && !busy,
            CanPause = canPause && automatic,
        };
    }
}
