using System.ComponentModel;
using System.Diagnostics;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Windows.NativeInterop;

namespace Dismode.Windows.Processes;

/// <summary>
/// Obniza klase priorytetu szeregowania GPU procesu tla i przywraca ja po
/// sesji.
/// <para>
/// Maska powinowactwa odbiera tlu rdzenie, priorytet wejscia-wyjscia dysk, a
/// to jest trzeci zasob, o ktory gra rywalizuje: karta graficzna. Przegladarka
/// z odtwarzaczem wideo albo kodowanie w tle potrafi zajmowac planiste GPU
/// tak samo, jak indeksowanie zajmuje dysk.
/// </para>
/// <para>
/// Czego ta akcja NIE robi: nie izoluje gry od tla. To wskazowka dla planisty
/// grafiki — polecenia juz wyslane do karty zostana dokonczone, a proces bez
/// kontekstu grafiki nie ma czego szeregowac. Zysk w czasie klatki jest
/// <b>niezmierzony</b>.
/// </para>
/// <para>
/// Dziala tylko na procesach z klasa Normal albo nizsza. Proces z wyzszym
/// priorytetem GPU zostalby obnizony, a przywrocenie go wymagaloby uprawnienia,
/// ktorego nie mamy — zostalby na stale nizej.
/// </para>
/// </summary>
public sealed class ProcessGpuPriorityAction :
    IReversibleAction<ProcessGpuPriorityState>
{
    private readonly ProcessIdentity _expectedIdentity;
    private readonly int _desiredPriorityClass;
    private readonly IProcessIdentityProvider _identityProvider;

    public ProcessGpuPriorityAction(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        int desiredPriorityClass = GpuPriorityNativeMethods.BelowNormal,
        IProcessIdentityProvider? identityProvider = null)
    {
        if (desiredPriorityClass is < GpuPriorityNativeMethods.Idle
            or > GpuPriorityNativeMethods.Normal)
        {
            // Podnoszenie priorytetu GPU procesowi tla nie ma uzasadnienia,
            // a wartosci powyzej Normal wymagaja uprawnienia. Obnizamy albo
            // nic.
            throw new ArgumentOutOfRangeException(
                nameof(desiredPriorityClass),
                desiredPriorityClass,
                "Ta akcja służy wyłącznie do obniżania priorytetu GPU.");
        }

        _expectedIdentity = expectedIdentity;
        _desiredPriorityClass = desiredPriorityClass;
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        Descriptor = new(
            actionId,
            SystemTargetKind.Process,
            expectedIdentity.RuntimeKey.ToString(),
            OptimizationActionKind.LowerProcessGpuPriority);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<ProcessGpuPriorityState>>
        PrepareAsync(
            ActionExecutionContext context,
            CancellationToken cancellationToken)
    {
        ProcessGpuPriorityState originalState =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        return new(
            originalState,
            new ProcessGpuPriorityState(_desiredPriorityClass),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessGpuPriorityState> preparedAction,
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
                        + "zmieniamy mu priorytetu GPU.");
            }

            // Odczyt ponowny, nie zapisany stan z przygotowania: proces,
            // ktory w miedzyczasie sam zmienil klase, nie moze zostac przez
            // nas „obnizony" — odtwarzanie nadpisaloby jego wlasny wybor.
            ProcessGpuPriorityState current = Read(process);
            if (current != preparedAction.OriginalState)
            {
                return ActionValidationResult.Blocked(
                    "Priorytet GPU procesu zmienił się po przygotowaniu "
                        + "akcji.");
            }

            if (current.PriorityClass > GpuPriorityNativeMethods.Normal)
            {
                return ActionValidationResult.Blocked(
                    "Proces ma podwyższony priorytet GPU; nie potrafilibyśmy "
                        + "go przywrócić bez dodatkowych uprawnień.");
            }

            if (current.PriorityClass
                <= preparedAction.DesiredState.PriorityClass)
            {
                return ActionValidationResult.Blocked(
                    "Proces ma już priorytet GPU nie wyższy niż docelowy.");
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
        PreparedAction<ProcessGpuPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await WriteAsync(
                preparedAction.DesiredState.PriorityClass,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessGpuPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(preparedAction.DesiredState, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ProcessGpuPriorityState> ReadCurrentStateAsync(
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

    internal static ProcessGpuPriorityState Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        int status = GpuPriorityNativeMethods
            .D3DKMTGetProcessSchedulingPriorityClass(
                process.Handle,
                out int priorityClass);
        if (status != GpuPriorityNativeMethods.StatusSuccess)
        {
            throw new InvalidOperationException(
                "Nie udało się odczytać priorytetu GPU procesu "
                    + $"(NTSTATUS 0x{status:X8}).");
        }

        return new(priorityClass);
    }

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessGpuPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await WriteAsync(
                preparedAction.OriginalState.PriorityClass,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessGpuPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(preparedAction.OriginalState, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask WriteAsync(
        int priorityClass,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        int status = GpuPriorityNativeMethods
            .D3DKMTSetProcessSchedulingPriorityClass(
                process.Handle,
                priorityClass);
        if (status != GpuPriorityNativeMethods.StatusSuccess)
        {
            throw new InvalidOperationException(
                "Nie udało się ustawić priorytetu GPU procesu "
                    + $"(NTSTATUS 0x{status:X8}).");
        }
    }

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        ProcessGpuPriorityState expectedState,
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
            ProcessGpuPriorityState actual = Read(process);
            return actual == expectedState
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed(
                    $"Oczekiwano klasy GPU {expectedState.PriorityClass}, "
                        + $"odczytano {actual.PriorityClass}.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionVerificationResult.Failed(exception.Message);
        }
    }
}
