using System.ComponentModel;
using System.Diagnostics;
using GameShift.Core.Domain.Processes;
using GameShift.Core.Profiles;
using GameShift.Windows.Processes;
using GameShift.Windows.Security;

namespace GameShift.Windows.Profiles;

public sealed class ManualGameProfileLauncher
{
    private static readonly TimeSpan IdentityObservationTimeout =
        TimeSpan.FromSeconds(10);

    private readonly IProcessIdentityProvider _identityProvider;

    public ManualGameProfileLauncher(
        IProcessIdentityProvider? identityProvider = null)
    {
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
    }

    public async ValueTask<LaunchedGameProcess> LaunchAsync(
        ManualGameProfile profile,
        CancellationToken cancellationToken)
    {
        return await LaunchCoreAsync(
                profile,
                attachToRunningProcess: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<LaunchedGameProcess> LaunchOrAttachAsync(
        ManualGameProfile profile,
        CancellationToken cancellationToken)
    {
        return await LaunchCoreAsync(
                profile,
                attachToRunningProcess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<LaunchedGameProcess>
        LaunchOrAttachAfterVerificationAsync(
            ManualGameProfile profile,
            string verifiedExecutableSha256,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            verifiedExecutableSha256);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                verifiedExecutableSha256,
                profile.ExecutableSha256))
        {
            throw new InvalidOperationException(
                "The supplied executable verification does not match the profile.");
        }

        return await LaunchCoreAsync(
                profile,
                attachToRunningProcess: true,
                verifyExecutableBeforeLaunch: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<LaunchedGameProcess> LaunchCoreAsync(
        ManualGameProfile profile,
        bool attachToRunningProcess,
        CancellationToken cancellationToken) =>
        await LaunchCoreAsync(
                profile,
                attachToRunningProcess,
                verifyExecutableBeforeLaunch: true,
                cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<LaunchedGameProcess> LaunchCoreAsync(
        ManualGameProfile profile,
        bool attachToRunningProcess,
        bool verifyExecutableBeforeLaunch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsEnabled)
        {
            throw new InvalidOperationException(
                "The selected game profile is disabled.");
        }

        if (verifyExecutableBeforeLaunch)
        {
            string currentHash =
                await ExecutableFileHasher.ComputeSha256Async(
                        profile.ExecutablePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    currentHash,
                    profile.ExecutableSha256))
            {
                throw new InvalidOperationException(
                    "The game executable changed after the profile was approved.");
            }
        }

        if (attachToRunningProcess)
        {
            ProcessIdentity? runningIdentity =
                await TryFindRunningGameAsync(
                        profile,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (runningIdentity is not null)
            {
                return new(
                    runningIdentity,
                    WasAlreadyRunning: true);
            }
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = profile.ExecutablePath,
            WorkingDirectory = profile.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (string argument in profile.LaunchArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows did not start the selected game executable.");

        using CancellationTokenSource timeout =
            new(IdentityObservationTimeout);

        while (true)
        {
            ProcessIdentity? identity =
                await _identityProvider
                    .TryCaptureAsync(process.Id, timeout.Token)
                    .ConfigureAwait(false);
            if (identity is not null)
            {
                EnsureMatchesProfile(profile, identity);
                return new(
                    identity,
                    WasAlreadyRunning: false);
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "The selected game exited before identity verification.");
            }

            await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    timeout.Token)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<ProcessIdentity?> TryFindRunningGameAsync(
        ManualGameProfile profile,
        CancellationToken cancellationToken)
    {
        string processName = Path.GetFileNameWithoutExtension(
            profile.ExecutablePath);
        int currentSessionId =
            Process.GetCurrentProcess().SessionId;
        List<ProcessIdentity> matches = [];
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessIdentity? identity = null;
                try
                {
                    if (process.SessionId != currentSessionId
                        || !StringComparer.OrdinalIgnoreCase.Equals(
                            process.MainModule?.FileName,
                            profile.ExecutablePath))
                    {
                        continue;
                    }

                    identity = await _identityProvider.TryCaptureAsync(
                            process.Id,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is
                        InvalidOperationException
                        or NotSupportedException
                        or UnauthorizedAccessException
                        or Win32Exception)
                {
                }

                if (identity is not null)
                {
                    EnsureMatchesProfile(profile, identity);
                    matches.Add(identity);
                }
            }
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                "Wykryto kilka uruchomionych egzemplarzy tej gry. "
                + "Zamknij duplikaty i ponów przygotowanie sesji."),
        };
    }

    private static void EnsureMatchesProfile(
        ManualGameProfile profile,
        ProcessIdentity identity)
    {
        string currentSid = CurrentWindowsIdentity.GetUserSid().Value;
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                identity.ExecutablePath,
                profile.ExecutablePath)
            || !StringComparer.OrdinalIgnoreCase.Equals(
                identity.ExecutableSha256,
                profile.ExecutableSha256)
            || !StringComparer.OrdinalIgnoreCase.Equals(
                identity.UserSid,
                currentSid))
        {
            throw new InvalidOperationException(
                "The launched process does not match the approved game profile.");
        }
    }
}
