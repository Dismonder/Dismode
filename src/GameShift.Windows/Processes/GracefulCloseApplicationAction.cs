using System.ComponentModel;
using System.Diagnostics;
using GameShift.Core.Actions;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Windows.Security;

namespace GameShift.Windows.Processes;

public sealed class GracefulCloseApplicationAction :
    IReversibleAction<ApplicationProcessState>
{
    private static readonly TimeSpan DefaultCloseTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RestartObservationTimeout =
        TimeSpan.FromSeconds(10);

    private readonly ProcessIdentity _expectedIdentity;
    private readonly ApplicationRestartDescriptor _restartDescriptor;
    private readonly IProcessIdentityProvider _identityProvider;
    private readonly TimeSpan _closeTimeout;
    private int? _restartedProcessId;

    public GracefulCloseApplicationAction(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        ApplicationRestartDescriptor restartDescriptor,
        IProcessIdentityProvider? identityProvider = null,
        TimeSpan? closeTimeout = null)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                expectedIdentity.ExecutablePath,
                restartDescriptor.ExecutablePath))
        {
            throw new ArgumentException(
                "The restart executable must match the approved process identity.",
                nameof(restartDescriptor));
        }

        TimeSpan effectiveTimeout = closeTimeout ?? DefaultCloseTimeout;
        if (effectiveTimeout <= TimeSpan.Zero
            || effectiveTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(closeTimeout),
                effectiveTimeout,
                "The close timeout must be between zero and one minute.");
        }

        _expectedIdentity = expectedIdentity;
        _restartDescriptor = restartDescriptor;
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        _closeTimeout = effectiveTimeout;
        Descriptor = new(
            actionId,
            SystemTargetKind.UserApplication,
            expectedIdentity.RuntimeKey.ToString(),
            OptimizationActionKind.GracefulCloseApplication);
    }

    public ActionDescriptor Descriptor { get; }

    public int? LastRestartedProcessId => _restartedProcessId;

    public async ValueTask<PreparedAction<ApplicationProcessState>> PrepareAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);

        return new(
            new ApplicationProcessState(IsRunning: true),
            new ApplicationProcessState(IsRunning: false),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<ApplicationProcessState> preparedAction,
        CancellationToken cancellationToken)
    {
        if (_restartDescriptor.Restartability
            != ApplicationRestartability.Restartable)
        {
            return ActionValidationResult.Blocked(
                "Safe mode only closes applications marked as fully restartable.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                CurrentWindowsIdentity.GetUserSid().Value,
                _expectedIdentity.UserSid))
        {
            return ActionValidationResult.Blocked(
                "The application belongs to a different Windows user.");
        }

        try
        {
            IReadOnlyList<ProcessIdentity> equivalents =
                await FindEquivalentInteractiveProcessesAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            if (equivalents.Count != 1
                || equivalents[0].RuntimeKey != _expectedIdentity.RuntimeKey)
            {
                return ActionValidationResult.Blocked(
                    "Safe mode requires exactly one matching top-level application window.");
            }

            using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);
            return ProcessWindowHelper.HasInteractiveWindow(process)
                ? ActionValidationResult.Allowed()
                : ActionValidationResult.Blocked(
                    "The application does not expose a closeable top-level window.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionValidationResult.Blocked(exception.Message);
        }
    }

    public async ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<ApplicationProcessState> preparedAction,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessIdentity> interactiveProcesses =
            await FindEquivalentInteractiveProcessesAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        if (interactiveProcesses.Count != 1
            || interactiveProcesses[0].RuntimeKey
                != _expectedIdentity.RuntimeKey)
        {
            throw new InvalidOperationException(
                "The approved top-level application window changed before close.");
        }

        using (Process process =
            await ProcessTargetGuard.OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            if (!ProcessWindowHelper.RequestGracefulClose(process))
            {
                throw new InvalidOperationException(
                    "Windows did not accept a standard close request.");
            }
        }

        using CancellationTokenSource closeTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        closeTimeout.CancelAfter(_closeTimeout);

        try
        {
            while (true)
            {
                bool targetStillRunning =
                    await _identityProvider
                        .MatchesRuntimeIdentityAsync(
                            _expectedIdentity,
                            closeTimeout.Token)
                        .ConfigureAwait(false);
                IReadOnlyList<ProcessIdentity> remainingWindows =
                    await FindEquivalentInteractiveProcessesAsync(
                            closeTimeout.Token)
                        .ConfigureAwait(false);
                if (!targetStillRunning && remainingWindows.Count == 0)
                {
                    return;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        closeTimeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The application did not exit after the standard close request; "
                + "it was not force-killed.");
        }
    }

    public async ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<ApplicationProcessState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                preparedAction.DesiredState,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ApplicationProcessState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessIdentity> equivalents =
            await FindEquivalentInteractiveProcessesAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        return new(equivalents.Count > 0);
    }

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<ApplicationProcessState> preparedAction,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessIdentity> existing =
            await FindEquivalentInteractiveProcessesAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return;
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                CurrentWindowsIdentity.GetUserSid().Value,
                _expectedIdentity.UserSid))
        {
            throw new InvalidOperationException(
                "The application cannot be restarted in a different user session.");
        }

        string currentHash = await ExecutableFileHasher.ComputeSha256Async(
                _restartDescriptor.ExecutablePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                currentHash,
                _expectedIdentity.ExecutableSha256))
        {
            throw new InvalidOperationException(
                "The restart executable changed after the application was closed.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = _restartDescriptor.ExecutablePath,
            WorkingDirectory = _restartDescriptor.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (string argument in _restartDescriptor.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process restarted = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows did not start the approved application.");
        _restartedProcessId = restarted.Id;

        using CancellationTokenSource observationTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        observationTimeout.CancelAfter(RestartObservationTimeout);

        try
        {
            while (true)
            {
                IReadOnlyList<ProcessIdentity> interactiveProcesses =
                    await FindEquivalentInteractiveProcessesAsync(
                            observationTimeout.Token)
                        .ConfigureAwait(false);
                if (interactiveProcesses.Count > 0)
                {
                    return;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        observationTimeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The approved application did not expose a verified window "
                + "after restart.");
        }
    }

    public async ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<ApplicationProcessState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                preparedAction.OriginalState,
                cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        ApplicationProcessState expectedState,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ProcessIdentity> equivalents =
                await FindEquivalentInteractiveProcessesAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            bool isRunning = equivalents.Count > 0;
            return isRunning == expectedState.IsRunning
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed(
                    "The application running state does not match the expected state.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionVerificationResult.Failed(exception.Message);
        }
    }

    private async ValueTask<IReadOnlyList<ProcessIdentity>>
        FindEquivalentInteractiveProcessesAsync(
            CancellationToken cancellationToken)
    {
        string processName = Path.GetFileNameWithoutExtension(
            _expectedIdentity.ExecutablePath);
        List<ProcessIdentity> identities = [];

        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (!ProcessWindowHelper.HasInteractiveWindow(process))
                {
                    continue;
                }

                ProcessIdentity? identity =
                    await _identityProvider
                        .TryCaptureAsync(process, cancellationToken)
                        .ConfigureAwait(false);
                if (identity is not null
                    && IsEquivalentExecutable(identity))
                {
                    identities.Add(identity);
                }
            }
        }

        return identities;
    }

    private bool IsEquivalentExecutable(ProcessIdentity identity) =>
        identity.SessionId == _expectedIdentity.SessionId
        && StringComparer.OrdinalIgnoreCase.Equals(
            identity.ExecutablePath,
            _expectedIdentity.ExecutablePath)
        && StringComparer.OrdinalIgnoreCase.Equals(
            identity.ExecutableSha256,
            _expectedIdentity.ExecutableSha256)
        && StringComparer.OrdinalIgnoreCase.Equals(
            identity.UserSid,
            _expectedIdentity.UserSid);

}
