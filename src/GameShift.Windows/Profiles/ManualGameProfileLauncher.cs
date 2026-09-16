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

    /// <param name="attachOnly">
    /// Gdy true, brak dzialajacego egzemplarza jest bledem, a nie powodem
    /// do uruchomienia gry. Automat dolaczajacy do wykrytej gry musi tak
    /// pracowac: miedzy wykryciem a startem sesji gra mogla sie zakonczyc,
    /// a uruchomienie jej od nowa bez klikniecia uzytkownika byloby
    /// dzialaniem, na ktore nikt sie nie zgodzil.
    /// </param>
    internal async ValueTask<LaunchedGameProcess>
        LaunchOrAttachAfterVerificationAsync(
            ManualGameProfile profile,
            string verifiedExecutableSha256,
            bool attachOnly,
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
                attachOnly,
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
                attachOnly: false,
                cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<LaunchedGameProcess> LaunchCoreAsync(
        ManualGameProfile profile,
        bool attachToRunningProcess,
        bool verifyExecutableBeforeLaunch,
        bool attachOnly,
        CancellationToken cancellationToken)
    {
        if (attachOnly && !attachToRunningProcess)
        {
            throw new ArgumentException(
                "Attach-only launches require attaching to the running process.",
                nameof(attachOnly));
        }

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

            if (attachOnly)
            {
                throw new InvalidOperationException(
                    "Gra nie jest już uruchomiona. GameShift dołącza "
                    + "automatycznie tylko do działającej gry i nie "
                    + "uruchamia jej samodzielnie.");
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
                // Launchery sklepow i zabezpieczenia DRM czesto przejmuja
                // uruchomienie: proces wystartowany przez nas konczy sie od
                // razu, a wlasciwa gra dziala juz pod innym PID. Zanim
                // uznamy to za blad, szukamy tego przejetego procesu.
                ProcessIdentity? relaunched =
                    await WaitForRelaunchedGameAsync(
                            profile,
                            timeout.Token)
                        .ConfigureAwait(false);
                if (relaunched is not null)
                {
                    return new(relaunched, WasAlreadyRunning: false);
                }

                throw new InvalidOperationException(
                    "The selected game exited before identity verification. "
                        + "Jesli gra wymaga launchera sklepu, uruchom ja stamtad "
                        + "- GameShift dolaczy sie do dzialajacego procesu.");
            }

            await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    timeout.Token)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits briefly for the game to reappear under a new process id. A store
    /// launcher or DRM wrapper commonly restarts the executable, which ends
    /// the process GameShift started; without this the launch is reported as
    /// a failure even though the game is coming up normally.
    /// </summary>
    private async ValueTask<ProcessIdentity?> WaitForRelaunchedGameAsync(
        ManualGameProfile profile,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ProcessIdentity? identity =
                await TryFindRunningGameAsync(profile, cancellationToken)
                    .ConfigureAwait(false);
            if (identity is not null)
            {
                return identity;
            }

            try
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(250),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
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
                            ProcessImagePath.TryRead(process),
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
