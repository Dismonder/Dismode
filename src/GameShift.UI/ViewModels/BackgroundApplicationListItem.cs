using System.ComponentModel;
using GameShift.UI.Services;

namespace GameShift.UI.ViewModels;

public sealed class BackgroundApplicationListItem :
    INotifyPropertyChanged
{
    private bool _isSelected;

    public BackgroundApplicationListItem(
        DiscoveredProcessClientSnapshot process,
        bool canClose,
        bool canLowerPriority)
    {
        Process = process;
        List<BackgroundProcessActionOption> options = [];
        if (canLowerPriority)
        {
            options.Add(
                new(
                    BackgroundProcessClientActionMode
                        .LowerPriorityAndEcoQos,
                    "Ogranicz tło — BelowNormal, EcoQoS, rdzenie tła"));
            options.Add(
                new(
                    BackgroundProcessClientActionMode.LowerPriority,
                    "Tylko obniż priorytet"));
        }

        if (canClose)
        {
            options.Add(
                new(
                    BackgroundProcessClientActionMode.CloseAndRestore,
                    "Zamknij i przywróć"));
        }

        if (options.Count == 0)
        {
            throw new ArgumentException(
                "Proces musi mieć co najmniej jedno bezpieczne działanie.",
                nameof(canLowerPriority));
        }

        AvailableActions = options;
        _selectedAction = options[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DiscoveredProcessClientSnapshot Process { get; }

    public IReadOnlyList<BackgroundProcessActionOption>
        AvailableActions
    { get; }

    public string DisplayName => Process.Name;

    public string MemoryLabel =>
        Process.WorkingSetBytes >= 1024L * 1024 * 1024
            ? $"{Process.WorkingSetBytes / 1024d / 1024 / 1024:0.0} GB RAM"
            : $"{Process.WorkingSetBytes / 1024d / 1024:0} MB RAM";

    public string Details =>
        $"{Process.ExecutablePath} • PID {Process.ProcessId}";

    public string ClassificationLabel =>
        $"Opcjonalny • {Process.RecommendedAction}";

    public string ClassificationReason =>
        Process.ClassificationReason;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private BackgroundProcessActionOption _selectedAction;

    public BackgroundProcessActionOption SelectedAction
    {
        get => _selectedAction;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_selectedAction == value)
            {
                return;
            }

            _selectedAction = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(
                    nameof(SelectedAction)));
        }
    }

    public BackgroundApplicationClientSelection ToSelection() =>
        new(
            Process.ProcessId,
            Process.StartedAtUtc
                ?? throw new InvalidOperationException(
                    "Proces nie ma stabilnego czasu uruchomienia."),
            SelectedAction.Mode);

    public bool ApplySavedAction(
        BackgroundProcessClientActionMode mode)
    {
        BackgroundProcessActionOption? option =
            AvailableActions.FirstOrDefault(candidate =>
                candidate.Mode == mode);
        if (option is null)
        {
            return false;
        }

        SelectedAction = option;
        IsSelected = true;
        return true;
    }

    public override string ToString() =>
        $"{DisplayName}, {MemoryLabel}, "
        + (IsSelected ? "wybrana" : "niewybrana");
}

public sealed record BackgroundProcessActionOption(
    BackgroundProcessClientActionMode Mode,
    string Label)
{
    public override string ToString() => Label;
}
