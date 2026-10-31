using System.Security.Principal;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Recovery;
using Microsoft.Win32;

namespace Dismode.Windows.SystemOptimization;

public sealed class GameCaptureRegistryAction :
    IReversibleAction<GameCaptureRegistryState>,
    IRecoveryDecisionProvider<GameCaptureRegistryState>
{
    private readonly string _userSid;
    private readonly IGameCaptureRegistryAdapter _adapter;

    public GameCaptureRegistryAction(
        ActionId actionId,
        string userSid,
        IGameCaptureRegistryAdapter? adapter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        SecurityIdentifier sid = new(userSid);
        if (!sid.IsAccountSid()
            && !sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal))
        {
            throw new ArgumentException("Wymagany jest SID użytkownika interaktywnego.", nameof(userSid));
        }

        _userSid = sid.Value;
        _adapter = adapter ?? new WindowsGameCaptureRegistryAdapter();
        Descriptor = new(
            actionId,
            SystemTargetKind.OperatingSystemSetting,
            $"{_userSid}\\{WindowsGameCaptureRegistryAdapter.SubKeyPath}",
            OptimizationActionKind.ConfigureGameCapture);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<GameCaptureRegistryState>> PrepareAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        GameCaptureRegistryState original = await ReadCurrentStateAsync(context, cancellationToken)
            .ConfigureAwait(false);
        return new(original, DisabledState(), DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<GameCaptureRegistryState> preparedAction,
        CancellationToken cancellationToken)
    {
        GameCaptureRegistryState current = await ReadCurrentStateAsync(context, cancellationToken)
            .ConfigureAwait(false);
        return current.IsPolicyAllowed
            && current == preparedAction.OriginalState
            && preparedAction.DesiredState == DisabledState()
                ? ActionValidationResult.Allowed()
                : ActionValidationResult.Blocked(
                    "Polityka Game DVR lub zmiana ustawienia blokuje operację.");
    }

    public async ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<GameCaptureRegistryState> preparedAction,
        CancellationToken cancellationToken)
    {
        ActionValidationResult validation = await ValidateAsync(context, preparedAction, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.BlockingReason);
        }

        if (preparedAction.OriginalState != preparedAction.DesiredState)
        {
            await _adapter.WriteAsync(preparedAction.OriginalState, preparedAction.DesiredState, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<GameCaptureRegistryState> preparedAction,
        CancellationToken cancellationToken) =>
        VerifyStateAsync(preparedAction.DesiredState, cancellationToken);

    public ValueTask<GameCaptureRegistryState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken) =>
        _adapter.ReadAsync(_userSid, cancellationToken);

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<GameCaptureRegistryState> preparedAction,
        CancellationToken cancellationToken)
    {
        GameCaptureRegistryState current = await ReadCurrentStateAsync(context, cancellationToken)
            .ConfigureAwait(false);
        RestoreDecision<GameCaptureRegistryState> decision = DecideRecovery(
            preparedAction.OriginalState, preparedAction.DesiredState, current);
        if (decision.Kind == RestoreDecisionKind.PreserveCurrentAndReportConflict)
        {
            throw new InvalidOperationException("Zachowano zewnętrzną zmianę ustawienia Game DVR.");
        }

        if (decision.Kind == RestoreDecisionKind.RestoreOriginal)
        {
            await _adapter.WriteAsync(current, preparedAction.OriginalState, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<GameCaptureRegistryState> preparedAction,
        CancellationToken cancellationToken) =>
        VerifyStateAsync(preparedAction.OriginalState, cancellationToken);

    public RestoreDecision<GameCaptureRegistryState> DecideRecovery(
        GameCaptureRegistryState originalState,
        GameCaptureRegistryState appliedState,
        GameCaptureRegistryState currentState) =>
        // Polityka blokuje tylko zastosowanie; przywracanie porownuje sama wartosc.
        ThreeWayReconciler.Decide(originalState.WithoutPolicy(), appliedState.WithoutPolicy(), currentState.WithoutPolicy());

    private GameCaptureRegistryState DisabledState() =>
        new(_userSid, true, RegistryValueKind.DWord, "0", true);

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        GameCaptureRegistryState expected,
        CancellationToken cancellationToken)
    {
        GameCaptureRegistryState current = await _adapter.ReadAsync(_userSid, cancellationToken)
            .ConfigureAwait(false);
        return current.WithoutPolicy() == expected.WithoutPolicy()
            ? ActionVerificationResult.Verified()
            : ActionVerificationResult.Failed("Odczyt Game DVR nie zgadza się ze stanem oczekiwanym.");
    }
}

public sealed record GameCaptureRegistryState(
    string UserSid,
    bool IsPresent,
    RegistryValueKind? ValueKind,
    string? ValueJson,
    bool IsPolicyAllowed)
{
    public GameCaptureRegistryState WithoutPolicy() => this with { IsPolicyAllowed = true };
}

public interface IGameCaptureRegistryAdapter
{
    ValueTask<GameCaptureRegistryState> ReadAsync(string userSid, CancellationToken cancellationToken);

    ValueTask WriteAsync(
        GameCaptureRegistryState expected,
        GameCaptureRegistryState desired,
        CancellationToken cancellationToken);
}
