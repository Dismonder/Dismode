namespace GameShift.Windows.Sessions;

public sealed record BackgroundApplicationSelection(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    BackgroundProcessActionMode ActionMode =
        BackgroundProcessActionMode.CloseAndRestore);
