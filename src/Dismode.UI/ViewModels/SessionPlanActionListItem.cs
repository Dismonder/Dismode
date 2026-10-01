using Dismode.UI.Services;

namespace Dismode.UI.ViewModels;

public sealed class SessionPlanActionListItem
{
    public SessionPlanActionListItem(
        SessionPlanActionClientSnapshot action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Code = action.Code;
        Description = action.Description;
        RiskLabel = $"Ryzyko: {action.Risk}";
        RecoveryLabel = $"Powrót: {action.Recovery}";
    }

    public string Code { get; }

    public string Description { get; }

    public string RiskLabel { get; }

    public string RecoveryLabel { get; }

    public override string ToString() =>
        $"{Code}. {Description}. {RiskLabel}. {RecoveryLabel}";
}
