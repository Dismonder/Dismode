namespace Dismode.Windows.Sessions;

public sealed record SessionPlanItem(
    string Code,
    string Description,
    string Risk,
    string Recovery);
