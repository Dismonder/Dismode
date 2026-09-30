namespace Dismode.Windows.Power;

public sealed record PowerSchemeActionState(
    Guid ActiveSchemeId,
    bool ManagedSchemeExists);
