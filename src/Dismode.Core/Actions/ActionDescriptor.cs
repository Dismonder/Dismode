using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Actions;

public sealed record ActionDescriptor
{
    public ActionDescriptor(
        ActionId actionId,
        SystemTargetKind targetKind,
        string targetId,
        OptimizationActionKind actionKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        ActionId = actionId;
        TargetKind = targetKind;
        TargetId = targetId.Trim();
        ActionKind = actionKind;
    }

    public ActionId ActionId { get; }

    public SystemTargetKind TargetKind { get; }

    public string TargetId { get; }

    public OptimizationActionKind ActionKind { get; }
}

