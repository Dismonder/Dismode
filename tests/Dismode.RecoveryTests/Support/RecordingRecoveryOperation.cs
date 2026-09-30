using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Recovery;

namespace Dismode.RecoveryTests.Support;

internal sealed class RecordingRecoveryOperation : IRecoveryOperation
{
    private readonly string _name;
    private readonly IList<string> _recoveryOrder;

    public RecordingRecoveryOperation(
        string name,
        IList<string> recoveryOrder)
    {
        _name = name;
        _recoveryOrder = recoveryOrder;
        ActionId = ActionId.Create();
    }

    public ActionId ActionId { get; }

    public ValueTask<ActionRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken)
    {
        _recoveryOrder.Add(_name);
        return ValueTask.FromResult(
            new ActionRecoveryResult(ActionRecoveryStatus.Restored, null));
    }
}

