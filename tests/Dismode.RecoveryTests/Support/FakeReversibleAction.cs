using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;

namespace Dismode.RecoveryTests.Support;

internal sealed class FakeReversibleAction : IReversibleAction<TestTargetState>
{
    private readonly TestTargetState _originalState;
    private readonly TestTargetState _desiredState;
    private readonly ActionValidationResult _validation;

    public FakeReversibleAction(
        ActionId actionId,
        TestTargetState originalState,
        TestTargetState desiredState,
        ActionValidationResult? validation = null)
    {
        _originalState = originalState;
        _desiredState = desiredState;
        _validation = validation ?? ActionValidationResult.Allowed();
        CurrentState = originalState;
        Descriptor = new(
            actionId,
            SystemTargetKind.WindowsService,
            "Dismode.TestService",
            OptimizationActionKind.StopApprovedService);
    }

    public ActionDescriptor Descriptor { get; }

    public TestTargetState CurrentState { get; private set; }

    public int ApplyCount { get; private set; }

    public int CompensateCount { get; private set; }

    public Action? AfterPrepare { get; set; }

    public ValueTask<PreparedAction<TestTargetState>> PrepareAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        PreparedAction<TestTargetState> prepared = new(
                _originalState,
                _desiredState,
                DateTimeOffset.UtcNow)
        { IsUnchanged = _originalState == _desiredState };
        AfterPrepare?.Invoke();
        return ValueTask.FromResult(prepared);
    }

    public ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<TestTargetState> preparedAction,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CurrentState == preparedAction.OriginalState
            ? _validation
            : ActionValidationResult.Blocked("State changed after preparation."));

    public ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<TestTargetState> preparedAction,
        CancellationToken cancellationToken)
    {
        ApplyCount++;
        CurrentState = preparedAction.DesiredState;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<TestTargetState> preparedAction,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            CurrentState == preparedAction.DesiredState
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed("Desired state was not observed."));

    public ValueTask<TestTargetState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CurrentState);

    public ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<TestTargetState> preparedAction,
        CancellationToken cancellationToken)
    {
        CompensateCount++;
        CurrentState = preparedAction.OriginalState;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<TestTargetState> preparedAction,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            CurrentState == preparedAction.OriginalState
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed("Original state was not restored."));

    public void SimulateExternalChange(TestTargetState externalState)
    {
        CurrentState = externalState;
    }
}

