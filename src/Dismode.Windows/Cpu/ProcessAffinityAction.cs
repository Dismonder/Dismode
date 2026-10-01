using System.ComponentModel;
using System.Diagnostics;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Windows.Processes;

namespace Dismode.Windows.Cpu;

/// <summary>
/// Restricts a process to a set of logical processors, and puts the original
/// mask back when the session ends.
/// <para>
/// Affinity is the one knob here that can make things worse rather than merely
/// not better: a mask that is too narrow serialises a game's threads, and a
/// mask applied to the wrong process can stall the shell. So the desired mask
/// has to be a subset of what the process is already allowed to use — Dismode
/// narrows, never widens — and protected processes are refused outright.
/// </para>
/// </summary>
public sealed class ProcessAffinityAction :
    IReversibleAction<ProcessAffinityState>
{
    private readonly ProcessIdentity _expectedIdentity;
    private readonly ulong _desiredMask;
    private readonly IProcessIdentityProvider _identityProvider;

    public ProcessAffinityAction(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        ulong desiredMask,
        IProcessIdentityProvider? identityProvider = null)
        : this(actionId, expectedIdentity, identityProvider)
    {
        if (desiredMask == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(desiredMask),
                desiredMask,
                "Pusta maska odebralaby procesowi wszystkie rdzenie.");
        }

        _desiredMask = desiredMask;
    }

    private ProcessAffinityAction(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        IProcessIdentityProvider? identityProvider)
    {
        _expectedIdentity = expectedIdentity;
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        Descriptor = new(
            actionId,
            SystemTargetKind.Process,
            expectedIdentity.RuntimeKey.ToString(),
            OptimizationActionKind.RestrictProcessAffinity);
    }

    /// <summary>
    /// An instance that can only reverse: recovery reads the original mask
    /// from the journal and never asks what the desired one was, so the
    /// caller does not have to know — or recompute — the mask that was
    /// applied. That matters after a crash: whether this machine would
    /// qualify for a mask today has nothing to do with whether one was put
    /// on a process yesterday.
    /// </summary>
    public static ProcessAffinityAction ForRecovery(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        IProcessIdentityProvider? identityProvider = null) =>
        new(actionId, expectedIdentity, identityProvider);

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<ProcessAffinityState>> PrepareAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (_desiredMask == 0)
        {
            throw new InvalidOperationException(
                "Ta instancja sluzy wylacznie do odtwarzania; nie ma maski "
                    + "do nalozenia.");
        }

        ProcessAffinityState originalState =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        return new(
            originalState,
            new ProcessAffinityState(_desiredMask),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessAffinityState> preparedAction,
        CancellationToken cancellationToken)
    {
        try
        {
            using Process process = await ProcessTargetGuard
                .OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);

            if (BackgroundApplicationGuard.IsProtectedProcessName(
                    process.ProcessName))
            {
                return ActionValidationResult.Blocked(
                    $"Proces „{process.ProcessName}” jest chroniony i nie "
                        + "zmieniamy mu przypisania rdzeni.");
            }

            ulong current = (ulong)process.ProcessorAffinity.ToInt64();
            if (current != preparedAction.OriginalState.Mask)
            {
                return ActionValidationResult.Blocked(
                    "Przypisanie rdzeni zmienilo sie po przygotowaniu akcji.");
            }

            ulong desired = preparedAction.DesiredState.Mask;
            if ((desired & current) != desired)
            {
                // Rozszerzanie maski dawaloby procesowi rdzenie, ktorych
                // wlasciciel mu swiadomie odebral. Zwezamy albo nic.
                return ActionValidationResult.Blocked(
                    "Zadana maska wykracza poza rdzenie, ktore proces ma "
                        + "obecnie przydzielone.");
            }

            return ActionValidationResult.Allowed();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionValidationResult.Blocked(exception.Message);
        }
    }

    public async ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessAffinityState> preparedAction,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        process.ProcessorAffinity =
            (nint)(long)preparedAction.DesiredState.Mask;
    }

    public async ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessAffinityState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(preparedAction.DesiredState, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ProcessAffinityState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        return new((ulong)process.ProcessorAffinity.ToInt64());
    }

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessAffinityState> preparedAction,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        process.ProcessorAffinity =
            (nint)(long)preparedAction.OriginalState.Mask;
    }

    public async ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessAffinityState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(preparedAction.OriginalState, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        ProcessAffinityState expectedState,
        CancellationToken cancellationToken)
    {
        try
        {
            using Process process = await ProcessTargetGuard
                .OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);
            ulong actual = (ulong)process.ProcessorAffinity.ToInt64();
            return actual == expectedState.Mask
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed(
                    $"Oczekiwano maski 0x{expectedState.Mask:X}, "
                        + $"odczytano 0x{actual:X}.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionVerificationResult.Failed(exception.Message);
        }
    }
}
