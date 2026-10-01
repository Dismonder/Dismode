using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Windows.NativeInterop;

namespace Dismode.Windows.Processes;

/// <summary>
/// Lowers a background process's memory priority for the length of a session
/// and puts it back afterwards.
/// <para>
/// This is a lever for a different resource than the affinity mask. The mask
/// takes cores away; this decides whose pages go first when physical memory
/// runs short. On a 16 GB machine a modern game, a browser and a launcher do
/// not all fit, and the memory manager has to trim someone. By default every
/// user process has the same priority, so it trims by recency — and a game
/// that streams a new area is exactly the process whose pages were touched
/// least recently. A hard page fault on the render path is a hitch measured
/// in tens of milliseconds, which is a worse stutter than any scheduler
/// contention this module has measured. Marking the background's pages as
/// the ones to repurpose first keeps the game's resident.
/// </para>
/// <para>
/// What this does not do: it frees nothing and forces nothing out. Pages of a
/// low-priority process stay resident until something else needs the memory,
/// and when they are repurposed they go to the standby list like any others.
/// That is deliberate: emptying working sets or purging the standby list
/// causes the faults it claims to prevent, and the aggressive profile rules
/// it out for that reason.
/// </para>
/// <para>
/// Only ever lowers. Raising a process's memory priority would let it hold
/// pages at the game's expense, and five — the default — is already the
/// ceiling this call allows a user process anyway.
/// </para>
/// </summary>
public sealed class ProcessMemoryPriorityAction :
    IReversibleAction<ProcessMemoryPriorityState>
{
    private readonly ProcessIdentity _expectedIdentity;
    private readonly uint _desiredPriority;
    private readonly IProcessIdentityProvider _identityProvider;

    public ProcessMemoryPriorityAction(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        uint desiredPriority = ProcessNativeMethods.MemoryPriorityVeryLow,
        IProcessIdentityProvider? identityProvider = null)
    {
        if (desiredPriority is < ProcessNativeMethods.MemoryPriorityVeryLow
            or >= ProcessNativeMethods.MemoryPriorityNormal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(desiredPriority),
                desiredPriority,
                "Ta akcja sluzy wylacznie do obnizania priorytetu pamieci "
                    + "ponizej domyslnego.");
        }

        _expectedIdentity = expectedIdentity;
        _desiredPriority = desiredPriority;
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        Descriptor = new(
            actionId,
            SystemTargetKind.Process,
            expectedIdentity.RuntimeKey.ToString(),
            OptimizationActionKind.LowerProcessMemoryPriority);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<ProcessMemoryPriorityState>>
        PrepareAsync(
            ActionExecutionContext context,
            CancellationToken cancellationToken)
    {
        ProcessMemoryPriorityState originalState =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        return new(
            originalState,
            new ProcessMemoryPriorityState(_desiredPriority),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessMemoryPriorityState> preparedAction,
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
                        + "zmieniamy mu priorytetu pamięci.");
            }

            ProcessMemoryPriorityState current = Read(process);
            if (current != preparedAction.OriginalState)
            {
                return ActionValidationResult.Blocked(
                    "Priorytet pamięci procesu zmienił się po przygotowaniu "
                        + "akcji.");
            }

            if (current.MemoryPriority
                <= preparedAction.DesiredState.MemoryPriority)
            {
                // Ktos juz obnizyl ten proces — czesto on sam. Zapis nic by
                // nie zmienil, a zostawilby wpis w dzienniku sugerujacy, ze
                // to nasza decyzja.
                return ActionValidationResult.Blocked(
                    "Proces ma już priorytet pamięci nie wyższy niż "
                        + "docelowy.");
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
        PreparedAction<ProcessMemoryPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await WriteAsync(
                preparedAction.DesiredState.MemoryPriority,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessMemoryPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(preparedAction.DesiredState, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ProcessMemoryPriorityState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        return Read(process);
    }

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessMemoryPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await WriteAsync(
                preparedAction.OriginalState.MemoryPriority,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessMemoryPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(preparedAction.OriginalState, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Reads the process's memory priority through the documented
    /// GetProcessInformation call. Exposed for tests and diagnostics; the
    /// action itself always goes through the identity-validated path.
    /// </summary>
    internal static ProcessMemoryPriorityState Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        MemoryPriorityInformation information = new();
        if (ProcessNativeMethods.GetProcessInformation(
                process.SafeHandle,
                ProcessInformationClass.ProcessMemoryPriority,
                ref information,
                checked((uint)Marshal.SizeOf<MemoryPriorityInformation>()))
            == 0)
        {
            throw new Win32Exception();
        }

        return new(information.MemoryPriority);
    }

    private async ValueTask WriteAsync(
        uint memoryPriority,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        Write(process, memoryPriority);
    }

    /// <summary>
    /// Writes a memory priority to an already-opened process. The action
    /// itself always goes through the identity-validated path above; this is
    /// for the descendant sweep, which acts on processes nobody journaled.
    /// </summary>
    internal static void Write(Process process, uint memoryPriority)
    {
        ArgumentNullException.ThrowIfNull(process);
        MemoryPriorityInformation information = new()
        {
            MemoryPriority = memoryPriority,
        };
        if (ProcessNativeMethods.SetProcessInformation(
                process.SafeHandle,
                ProcessInformationClass.ProcessMemoryPriority,
                in information,
                checked((uint)Marshal.SizeOf<MemoryPriorityInformation>()))
            == 0)
        {
            throw new Win32Exception();
        }
    }

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        ProcessMemoryPriorityState expectedState,
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
            ProcessMemoryPriorityState actual = Read(process);
            return actual == expectedState
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed(
                    $"Oczekiwano priorytetu pamięci "
                        + $"{expectedState.MemoryPriority}, odczytano "
                        + $"{actual.MemoryPriority}.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionVerificationResult.Failed(exception.Message);
        }
    }
}
