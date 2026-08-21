using GameShift.Core.Actions;
using GameShift.Core.Domain.Identifiers;

namespace GameShift.Core.Recovery;

public sealed class TypedRecoveryOperation<TState> : IRecoveryOperation
    where TState : notnull
{
    private readonly ActionRecoveryCoordinator<TState> _coordinator;
    private readonly IReversibleAction<TState> _action;
    private readonly ActionExecutionContext _context;

    public TypedRecoveryOperation(
        ActionRecoveryCoordinator<TState> coordinator,
        IReversibleAction<TState> action,
        ActionExecutionContext context)
    {
        if (action.Descriptor.ActionId != context.ActionId)
        {
            throw new ArgumentException(
                "The action descriptor must match the execution context.",
                nameof(context));
        }

        _coordinator = coordinator;
        _action = action;
        _context = context;
    }

    public ActionId ActionId => _context.ActionId;

    public ValueTask<ActionRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken) =>
        _coordinator.RecoverAsync(_action, _context, cancellationToken);
}

