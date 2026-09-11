using System.ComponentModel;
using System.Diagnostics;
using GameShift.Core.Actions;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Windows.NativeInterop;

namespace GameShift.Windows.Processes;

/// <summary>
/// Obniza priorytet wejscia-wyjscia procesu tla i przywraca go po sesji.
/// <para>
/// Maska powinowactwa odbiera procesowi tla rdzenie, ale nie odbiera mu
/// dysku. Kopia zapasowa, indeksowanie albo pobieranie aktualizacji moze
/// siedziec na dwoch rdzeniach i nadal zapychac kolejke odczytow, a gra czeka
/// na swoje zasoby. To jest ta sama rywalizacja, tylko o inny zasob.
/// </para>
/// <para>
/// Czego ta akcja NIE robi: nie izoluje gry od tla. Operacje juz przekazane
/// do urzadzenia zostana wykonane, kolejka w kontrolerze nie zna tego
/// priorytetu, a wspoldzielone struktury systemu plikow dalej sa wspolne.
/// Skala zysku zalezy od tego, czy w ogole wystepuje konflikt, i pozostaje
/// do zmierzenia na zywej sesji.
/// </para>
/// </summary>
public sealed class ProcessIoPriorityAction :
    IReversibleAction<ProcessIoPriorityState>
{
    private readonly ProcessIdentity _expectedIdentity;
    private readonly uint _desiredPriority;
    private readonly IProcessIdentityProvider _identityProvider;

    public ProcessIoPriorityAction(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        uint desiredPriority,
        IProcessIdentityProvider? identityProvider = null)
    {
        if (desiredPriority > IoPriorityNativeMethods.IoPriorityNormal)
        {
            // Podnoszenie priorytetu wejscia-wyjscia procesowi tla nie ma
            // uzasadnienia i moglo by zaszkodzic grze. Obnizamy albo nic.
            throw new ArgumentOutOfRangeException(
                nameof(desiredPriority),
                desiredPriority,
                "Ta akcja służy wyłącznie do obniżania priorytetu.");
        }

        _expectedIdentity = expectedIdentity;
        _desiredPriority = desiredPriority;
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        Descriptor = new(
            actionId,
            SystemTargetKind.Process,
            expectedIdentity.RuntimeKey.ToString(),
            OptimizationActionKind.RestrictProcessAffinity);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<ProcessIoPriorityState>> PrepareAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        ProcessIoPriorityState originalState =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        return new(
            originalState,
            new ProcessIoPriorityState(_desiredPriority),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessIoPriorityState> preparedAction,
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
                        + "zmieniamy mu priorytetu wejścia-wyjścia.");
            }

            if (preparedAction.OriginalState.Priority
                <= preparedAction.DesiredState.Priority)
            {
                // Proces juz czyta z nizszym albo rownym priorytetem. Zapis
                // nic by nie zmienil, a zostawilby wpis w journalu sugerujacy,
                // ze to my go tam wpedzilismy.
                return ActionValidationResult.Blocked(
                    "Proces ma już priorytet wejścia-wyjścia nie wyższy niż "
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
        PreparedAction<ProcessIoPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await WriteAsync(
                preparedAction.DesiredState.Priority,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessIoPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(preparedAction.DesiredState, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ProcessIoPriorityState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        int status = IoPriorityNativeMethods.NtQueryInformationProcess(
            process.Handle,
            IoPriorityNativeMethods.ProcessIoPriority,
            out uint priority,
            sizeof(uint),
            out _);
        if (status != IoPriorityNativeMethods.StatusSuccess)
        {
            throw new InvalidOperationException(
                "Nie udało się odczytać priorytetu wejścia-wyjścia procesu "
                    + $"(NTSTATUS 0x{status:X8}).");
        }

        return new(priority);
    }

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessIoPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await WriteAsync(
                preparedAction.OriginalState.Priority,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessIoPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(preparedAction.OriginalState, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask WriteAsync(
        uint priority,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        int status = IoPriorityNativeMethods.NtSetInformationProcess(
            process.Handle,
            IoPriorityNativeMethods.ProcessIoPriority,
            in priority,
            sizeof(uint));
        if (status != IoPriorityNativeMethods.StatusSuccess)
        {
            throw new InvalidOperationException(
                "Nie udało się ustawić priorytetu wejścia-wyjścia procesu "
                    + $"(NTSTATUS 0x{status:X8}).");
        }
    }

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        ProcessIoPriorityState expectedState,
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
            int status = IoPriorityNativeMethods.NtQueryInformationProcess(
                process.Handle,
                IoPriorityNativeMethods.ProcessIoPriority,
                out uint actual,
                sizeof(uint),
                out _);
            if (status != IoPriorityNativeMethods.StatusSuccess)
            {
                return ActionVerificationResult.Failed(
                    $"Odczyt zwrócił NTSTATUS 0x{status:X8}.");
            }

            return actual == expectedState.Priority
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed(
                    $"Oczekiwano priorytetu {expectedState.Priority}, "
                        + $"odczytano {actual}.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionVerificationResult.Failed(exception.Message);
        }
    }
}
