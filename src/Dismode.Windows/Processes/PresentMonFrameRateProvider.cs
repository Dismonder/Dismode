using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Dismode.Core.Performance;
using Dismode.Windows.NativeInterop;
using Dismode.Windows.Sessions;

namespace Dismode.Windows.Processes;

public sealed class PresentMonFrameRateProvider : IFrameRateProvider, ISessionFrameRateStatisticsSource
{
    private static readonly TimeSpan ObservationWindow =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProcessStopTimeout =
        TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FirstFrameTimeout =
        TimeSpan.FromSeconds(8);
    private const int MaximumObservationsPerStream = 2_048;
    private const int MaximumInitialCaptureRestarts = 1;
    private const int PresentEventBufferSize = 8_192;
    private static readonly TimeSpan UnexpectedExitBackoff =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StreamSummaryInterval =
        TimeSpan.FromSeconds(10);

    private readonly string _executablePath;
    private readonly string _sessionName;
    private readonly string _legacySessionName;
    private readonly bool _trackGpu;
    private readonly Func<CancellationToken, ValueTask<bool>>?
        _displayTrackingSetting;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _observationSync = new();
    private readonly Dictionary<FrameStreamKey, Queue<FrameObservation>>
        _observations = [];
    private Process? _captureProcess;
    private CancellationTokenSource? _captureCancellation;
    private Task _standardOutputTask = Task.CompletedTask;
    private Task _standardErrorTask = Task.CompletedTask;
    private CaptureProcessIdentity? _captureIdentity;
    private int[] _targetProcessIds = [];
    private string? _lastDiagnostic;
    private DateTimeOffset? _captureStartedAtUtc;
    private int _initialCaptureRestartCount;
    private bool _captureProducedFrame;
    private string? _stoppedCaptureDiagnostic;
    private string? _unexpectedExitMessage;
    private int[] _unexpectedExitTargets = [];
    private DateTimeOffset _unexpectedExitBackoffUntilUtc;
    private BenchmarkFrameCollector? _benchmarkCollector;
    private SessionFrameRateStatisticsAccumulator? _sessionStatistics;
    private FrameStreamKey? _statisticsStream;
    private readonly Dictionary<FrameStreamKey, PendingStatistics>
        _pendingStatistics = [];
    private bool _captureTracksDisplay;
    private bool _disposed;

    public PresentMonFrameRateProvider(
        string? executablePath = null,
        TimeProvider? timeProvider = null,
        PresentMonCaptureRole captureRole = PresentMonCaptureRole.Session,
        Func<CancellationToken, ValueTask<bool>>? displayTrackingSetting = null)
    {
        _executablePath = Path.GetFullPath(
            executablePath
            ?? PresentMonComponent.ResolveDefaultExecutablePath());
        // Sesja gry zostaje przy dotychczasowej nazwie, bo po niej sprzatamy
        // sierote ze starszych wydan. Nakladka dostaje wlasna — wspolna
        // nazwa sprawiala, ze oba pomiary zatrzymywaly sie nawzajem.
        string identityKey = captureRole == PresentMonCaptureRole.Session
            ? GetCurrentUserIdentityKey()
            : $"{GetCurrentUserIdentityKey()}|overlay";
        _sessionName = CreateCaptureSessionName(identityKey);
        _legacySessionName = CreateLegacyCaptureSessionName(identityKey);
        _trackGpu = false;
        _displayTrackingSetting = displayTrackingSetting;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal PresentMonFrameRateProvider(
        string executablePath,
        string captureSessionIdentityKey,
        TimeProvider? timeProvider = null,
        bool trackGpu = false,
        Func<CancellationToken, ValueTask<bool>>? displayTrackingSetting = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            captureSessionIdentityKey);
        _executablePath = Path.GetFullPath(executablePath);
        _sessionName = CreateCaptureSessionName(
            captureSessionIdentityKey);
        _legacySessionName = CreateLegacyCaptureSessionName(
            captureSessionIdentityKey);
        _trackGpu = trackGpu;
        _displayTrackingSetting = displayTrackingSetting;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal int? ActiveCaptureProcessId => _captureIdentity?.ProcessId;

    internal string CaptureSessionName => _sessionName;

    internal void SetSessionStatistics(SessionFrameRateStatisticsAccumulator statistics)
    {
        lock (_observationSync)
        {
            if (ReferenceEquals(_sessionStatistics, statistics))
            {
                return;
            }

            _sessionStatistics = statistics;
            _statisticsStream = null;
            _pendingStatistics.Clear();
        }
    }

    void ISessionFrameRateStatisticsSource.SetSessionStatistics(
        SessionFrameRateStatisticsAccumulator statistics) => SetSessionStatistics(statistics);

    public static PresentMonFrameRateProvider CreateBenchmarkProvider(
        string? executablePath = null,
        TimeProvider? timeProvider = null) =>
        new(
            executablePath
            ?? PresentMonComponent.ResolveDefaultExecutablePath(),
            $"{GetCurrentUserIdentityKey()}|benchmark",
            timeProvider,
            trackGpu: true);

    public async ValueTask<IReadOnlyList<double>> CaptureFrameTimesAsync(
        int processId,
        TimeSpan measurementDuration,
        CancellationToken cancellationToken) =>
        (await CaptureBenchmarkAsync(
                processId,
                measurementDuration,
                cancellationToken)
            .ConfigureAwait(false)).FrameTimesMilliseconds;

    public async ValueTask<PresentMonBenchmarkCapture> CaptureBenchmarkAsync(
        int processId,
        TimeSpan measurementDuration,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (measurementDuration <= TimeSpan.Zero
            || measurementDuration > TimeSpan.FromSeconds(120))
        {
            throw new ArgumentOutOfRangeException(
                nameof(measurementDuration),
                "Benchmark capture must be between zero and 120 seconds.");
        }

        FrameRateSample sample = await SampleAsync(
                [processId],
                cancellationToken)
            .ConfigureAwait(false);
        if (sample.Status is FrameRateStatus.MissingComponent
            or FrameRateStatus.InvalidComponent
            or FrameRateStatus.AccessDenied
            or FrameRateStatus.Failed)
        {
            throw new InvalidOperationException(sample.Message);
        }

        BenchmarkFrameCollector collector = new(processId);
        lock (_observationSync)
        {
            if (_benchmarkCollector is not null)
            {
                throw new InvalidOperationException(
                    "A raw PresentMon benchmark capture is already active.");
            }

            _benchmarkCollector = collector;
        }

        try
        {
            await Task.Delay(
                    measurementDuration,
                    _timeProvider,
                    cancellationToken)
                .ConfigureAwait(false);
            return collector.SnapshotDominantCapture();
        }
        finally
        {
            lock (_observationSync)
            {
                if (ReferenceEquals(_benchmarkCollector, collector))
                {
                    _benchmarkCollector = null;
                }
            }

            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask<FrameRateSample> SampleAsync(
        IReadOnlyCollection<int> processIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        FrameRateSample sample = await SampleCoreAsync(
                processIds,
                cancellationToken)
            .ConfigureAwait(false);

        // Wpis tylko przy zmianie: inaczej sekunda gry to linia w pliku.
        string offered = string.Join(
            ",",
            processIds.Where(processId => processId > 0).Distinct().Order());
        if (sample.Status != _lastLoggedStatus
            || sample.ProcessId != _lastLoggedProcessId
            || offered != _lastLoggedOffered)
        {
            _lastLoggedStatus = sample.Status;
            _lastLoggedProcessId = sample.ProcessId;
            _lastLoggedOffered = offered;
            FrameCaptureLog.Write(
                $"sample status={sample.Status} measured={sample.ProcessId} "
                    + $"offered=[{offered}] "
                    + $"fps={sample.FramesPerSecond?.ToString("F1", CultureInfo.InvariantCulture)} "
                    + $"msg={sample.Message}"
                    + $" candidates={DescribeCandidates(processIds)}");
        }

        return sample;
    }

    /// <summary>
    /// The offered processes as the target selection sees them, for the log:
    /// whether each has a window, its working set and name. Read only when a
    /// log line is written, so it costs nothing per tick.
    /// </summary>
    private static string DescribeCandidates(
        IReadOnlyCollection<int> processIds)
    {
        List<string> described = [];
        foreach (int processId in processIds.Where(id => id > 0).Distinct())
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                described.Add(
                    $"{process.ProcessName}:{processId}"
                        + $":win={process.MainWindowHandle != IntPtr.Zero}"
                        + $":ws={process.WorkingSet64 / (1024 * 1024)}MB");
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException)
            {
                // Tylko opis do dziennika; nie moze kosztowac probki.
                described.Add($"?:{processId}");
            }
        }

        return "[" + string.Join(" ", described) + "]";
    }

    private FrameRateStatus? _lastLoggedStatus;
    private int? _lastLoggedProcessId;
    private string? _lastLoggedOffered;
    private DateTimeOffset _lastStreamSummaryUtc;
    private string? _lastLoggedStderr;

    private async ValueTask<FrameRateSample> SampleCoreAsync(
        IReadOnlyCollection<int> processIds,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        int[] targets = processIds
            .Where(processId => processId > 0)
            .Distinct()
            .Order()
            .ToArray();
        if (targets.Length == 0)
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
            return FrameRateSample.WaitingForGame();
        }

        int? selectedTarget = SelectCaptureTargetProcessId(targets);
        if (selectedTarget is null)
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
            return FrameRateSample.WaitingForGame();
        }

        int[] captureTargets = [selectedTarget.Value];
        await _lifecycleGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
            if (_captureProcess is { HasExited: true }
                && captureTargets.SequenceEqual(_targetProcessIds)
                && !_captureProducedFrame
                && !HasObservations())
            {
                // PresentMon died on its own before delivering a frame (no
                // elevation, lost ETW session, crash). Keep its last words and
                // back off instead of respawning it on every poll.
                await StopCaptureCoreAsync().ConfigureAwait(false);
                _unexpectedExitMessage =
                    string.IsNullOrWhiteSpace(_stoppedCaptureDiagnostic)
                        ? "PresentMon zakończył pomiar bez danych klatek."
                        : _stoppedCaptureDiagnostic;
                _unexpectedExitTargets = captureTargets;
                _unexpectedExitBackoffUntilUtc =
                    nowUtc + UnexpectedExitBackoff;
                return FrameRateSample.Failed(_unexpectedExitMessage);
            }

            if (_unexpectedExitMessage is not null
                && _captureProcess is null
                && nowUtc < _unexpectedExitBackoffUntilUtc
                && captureTargets.SequenceEqual(_unexpectedExitTargets))
            {
                return FrameRateSample.Failed(_unexpectedExitMessage);
            }

            if (_captureProcess is not null
                && !_captureProcess.HasExited
                && captureTargets.SequenceEqual(_targetProcessIds))
            {
                FrameRateSample sample = CreateCurrentSample();
                if (sample.Status == FrameRateStatus.Measuring)
                {
                    _captureProducedFrame = true;
                    _unexpectedExitMessage = null;
                    return sample;
                }

                if (!_captureProducedFrame
                    && _captureStartedAtUtc is DateTimeOffset startedAtUtc
                    && _timeProvider.GetUtcNow() - startedAtUtc
                        >= FirstFrameTimeout
                    && _initialCaptureRestartCount
                        < MaximumInitialCaptureRestarts)
                {
                    int restartCount =
                        _initialCaptureRestartCount + 1;
                    await StopCaptureCoreAsync().ConfigureAwait(false);
                    _initialCaptureRestartCount = restartCount;
                    StartCapture(captureTargets, _captureTracksDisplay);
                    return FrameRateSample.Restarting(
                        selectedTarget.Value);
                }

                // Restart nie pomogl, a PresentMon nadal chodzi i nie oddaje
                // ani jednej klatki. To nie jest awaria: gra, ktora sie
                // laduje, nie wyswietla nic — Valheim przez pierwsze ~30 s.
                // Wczesniej zwracalismy tu blad obwiniajacy inne narzedzia
                // pomiarowe, a na tej samej maszynie, przy tej samej usludze
                // Intela, klatki przyszly, gdy tylko gra zaczela rysowac.
                // Wieczne myslniki, ktore kiedys tu byly, mialy inna
                // przyczyne: nakladka i sesja zatrzymywaly sobie nawzajem
                // sesje ETW (patrz PresentMonCaptureRole). Mowimy wiec prawde:
                // pomiar dziala i czeka.
                if (!_captureProducedFrame
                    && _captureStartedAtUtc is DateTimeOffset stalledSince
                    && _timeProvider.GetUtcNow() - stalledSince
                        >= FirstFrameTimeout)
                {
                    return new(
                        FrameRateStatus.WaitingForGame,
                        FramesPerSecond: null,
                        FrameTimeMilliseconds: null,
                        selectedTarget.Value,
                        "PresentMon działa, ale gra nie wyświetliła jeszcze "
                            + "żadnej klatki — zwykle to ekran ładowania albo "
                            + "zminimalizowane okno. Pomiar ruszy sam, gdy gra "
                            + "zacznie rysować.");
                }

                return sample;
            }

            await StopCaptureCoreAsync().ConfigureAwait(false);
            PresentMonComponentInspection inspection =
                await PresentMonComponent.InspectAsync(
                        _executablePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!inspection.IsReady)
            {
                return inspection.State switch
                {
                    PresentMonComponentState.Missing =>
                        FrameRateSample.MissingComponent(
                            inspection.Message),
                    PresentMonComponentState.AccessDenied =>
                        new(
                            FrameRateStatus.AccessDenied,
                            FramesPerSecond: null,
                            FrameTimeMilliseconds: null,
                            ProcessId: null,
                            inspection.Message),
                    _ => FrameRateSample.InvalidComponent(
                        inspection.Message),
                };
            }

            bool trackDisplay = await ReadDisplayTrackingAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                StartCapture(captureTargets, trackDisplay);
                return FrameRateSample.Starting(selectedTarget.Value);
            }
            catch (Exception exception) when (
                exception is
                    Win32Exception
                    or InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException)
            {
                await StopCaptureCoreAsync().ConfigureAwait(false);
                return exception is UnauthorizedAccessException
                    or Win32Exception { NativeErrorCode: 5 }
                        ? new(
                            FrameRateStatus.AccessDenied,
                            FramesPerSecond: null,
                            FrameTimeMilliseconds: null,
                            ProcessId: null,
                            "Windows odmówił uruchomienia PresentMon. "
                            + "Uruchom Dismode i zaakceptuj żądanie UAC.")
                        : FrameRateSample.Failed(
                            $"PresentMon nie rozpoczął pomiaru: "
                            + exception.Message);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            _unexpectedExitMessage = null;
            await StopCaptureCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private bool HasObservations()
    {
        lock (_observationSync)
        {
            return _observations.Values.Any(stream => stream.Count > 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopCaptureCoreAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool _staleSessionsChecked;

    /// <summary>
    /// Clears sessions an earlier Dismode left running before starting a new
    /// capture. Once per provider: the orphan can only predate us, and probing
    /// on every restart would be noise.
    /// </summary>
    private void StopStaleSessionsOnce()
    {
        if (_staleSessionsChecked)
        {
            return;
        }

        _staleSessionsChecked = true;
        // Wlasna nazwa sesji MUSI byc na tej liscie. Sprzatanie obejmowalo
        // tylko nazwy z dawnych wydan, a biezaca jest budowana z odcisku
        // uzytkownika — wiec osierocona sesja o AKTUALNEJ nazwie nie byla
        // zatrzymywana przez nikogo i przezywala kazdy restart. Zmierzone
        // dzis: cztery kolejne pomiary zwrocily zero klatek, dopoki nie
        // zatrzymalem sesji recznie przez logman.
        // Wydania sprzed zmiany nazwy budowaly te sama nazwe z prefiksem
        // GameShift; ich sierota tak samo zjada klatki kazdemu pomiarowi.
        IReadOnlyList<string> stopped = EtwSessionCleanup.StopStaleSessions(
            [
                .. EtwSessionCleanup.LegacySessionNames,
                _legacySessionName,
                _sessionName,
            ]);
        if (stopped.Count > 0)
        {
            lock (_observationSync)
            {
                _lastDiagnostic =
                    "Zatrzymano porzuconą sesję pomiaru z wcześniejszego "
                    + "uruchomienia: "
                    + string.Join(", ", stopped)
                    + ". Blokowała odczyt klatek.";
            }
        }
    }

    // Ustawienie jest czytane przy starcie kazdego pomiaru; blad odczytu
    // zostawia sledzenie wylaczone, czyli dotychczasowe zachowanie.
    private async ValueTask<bool> ReadDisplayTrackingAsync(
        CancellationToken cancellationToken)
    {
        if (_displayTrackingSetting is null)
        {
            return false;
        }

        try
        {
            return await _displayTrackingSetting(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
                and not OutOfMemoryException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> BuildCaptureArguments(
        IReadOnlyList<int> targets,
        bool trackGpu,
        bool trackDisplay,
        string sessionName)
    {
        List<string> arguments = [];
        foreach (int processId in targets)
        {
            arguments.Add("--process_id");
            arguments.Add(
                processId.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("--output_stdout");
        arguments.Add("--no_console_stats");
        arguments.Add("--v2_metrics");
        if (!trackDisplay)
        {
            arguments.Add("--no_track_display");
        }

        if (!trackGpu)
        {
            arguments.Add("--no_track_gpu");
        }

        arguments.Add("--no_track_input");
        arguments.Add("--set_circular_buffer_size");
        arguments.Add(
            PresentEventBufferSize.ToString(
                CultureInfo.InvariantCulture));
        arguments.Add("--terminate_on_proc_exit");
        arguments.Add("--stop_existing_session");
        arguments.Add("--session_name");
        arguments.Add(sessionName);
        return arguments;
    }

    private void StartCapture(int[] targets, bool trackDisplay)
    {
        StopStaleSessionsOnce();
        if (targets.Length != 1)
        {
            throw new ArgumentException(
                "PresentMon accepts exactly one verified process ID per capture.",
                nameof(targets));
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = _executablePath,
            WorkingDirectory =
                Path.GetDirectoryName(_executablePath)
                ?? throw new InvalidOperationException(
                    "Katalog PresentMon jest niedostępny."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in BuildCaptureArguments(
                     targets,
                     _trackGpu,
                     trackDisplay,
                     _sessionName))
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        bool processStarted = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Windows nie uruchomił PresentMon.");
            }

            processStarted = true;
            FrameCaptureLog.Write(
                $"capture start presentmon={process.Id} target={targets[0]} "
                    + $"args={string.Join(' ', startInfo.ArgumentList)}");
            CaptureProcessIdentity captureIdentity =
                CaptureOwnedChildIdentity(process);
            CancellationTokenSource captureCancellation = new();
            _captureProcess = process;
            _captureIdentity = captureIdentity;
            _captureCancellation = captureCancellation;
            _targetProcessIds = [.. targets];
            _captureStartedAtUtc = _timeProvider.GetUtcNow();
            _captureProducedFrame = false;
            _captureTracksDisplay = trackDisplay;
            lock (_observationSync)
            {
                _observations.Clear();
                _lastDiagnostic = null;
            }

            _standardOutputTask = ReadStandardOutputAsync(
                process,
                targets.ToHashSet(),
                captureCancellation.Token);
            _standardErrorTask = ReadStandardErrorAsync(
                process,
                captureCancellation.Token);
        }
        catch (Exception startupException)
        {
            PresentMonCaptureCleanupException? cleanupException = processStarted
                ? TryTerminateNewlyStartedProcess(process)
                : null;
            process.Dispose();
            if (cleanupException is not null)
            {
                throw new AggregateException(
                    "Nie udało się bezpiecznie wycofać startu PresentMon.",
                    startupException,
                    cleanupException);
            }

            throw;
        }
    }

    private async Task ReadStandardOutputAsync(
        Process process,
        HashSet<int> targets,
        CancellationToken cancellationToken)
    {
        PresentMonCsvParser parser = new(_captureTracksDisplay);
        try
        {
            while (true)
            {
                string? line = await process.StandardOutput
                    .ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (!parser.TryParse(line, out PresentMonFrame frame)
                    || !targets.Contains(frame.ProcessId))
                {
                    continue;
                }

                AddObservation(frame);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is
                IOException
                or InvalidOperationException
                or ObjectDisposedException)
        {
            lock (_observationSync)
            {
                _lastDiagnostic =
                    $"Błąd odczytu PresentMon: {exception.Message}";
            }
        }
    }

    private async Task ReadStandardErrorAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                string? line = await process.StandardError
                    .ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (line.Contains(
                        "requires elevated privilege",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lock (_observationSync)
                {
                    _lastDiagnostic = line.Trim();
                }

                // Powtorzona linia (np. utracone zdarzenia co sekunde) idzie
                // do pliku raz, inaczej zalalaby dziennik.
                string trimmed = line.Trim();
                if (trimmed != _lastLoggedStderr)
                {
                    _lastLoggedStderr = trimmed;
                    FrameCaptureLog.Write("presentmon stderr: " + trimmed);
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is
                IOException
                or InvalidOperationException
                or ObjectDisposedException)
        {
            lock (_observationSync)
            {
                _lastDiagnostic =
                    $"Błąd diagnostyki PresentMon: {exception.Message}";
            }
        }
    }

    private void AddObservation(PresentMonFrame frame)
    {
        DateTimeOffset observedAtUtc = _timeProvider.GetUtcNow();
        FrameStreamKey key = new(
            frame.ProcessId,
            frame.SwapChainAddress);
        lock (_observationSync)
        {
            _benchmarkCollector?.Add(frame);
            if (_sessionStatistics is not null)
            {
                if (key == _statisticsStream)
                {
                    _sessionStatistics.Add(
                        frame.FrameTimeMilliseconds,
                        frame.DisplayedTimeMilliseconds,
                        frame.IsDropped);
                }
                else
                {
                    if (!_pendingStatistics.TryGetValue(key, out PendingStatistics? pending))
                    {
                        pending = new();
                        _pendingStatistics.Add(key, pending);
                    }

                    pending.Present.Add(frame.FrameTimeMilliseconds);
                    if (frame.DisplayedTimeMilliseconds is double displayed)
                    {
                        (pending.Displayed ??= new()).Add(displayed);
                    }
                    else if (frame.IsDropped)
                    {
                        pending.Dropped++;
                    }
                }
            }
            if (!_observations.TryGetValue(
                    key,
                    out Queue<FrameObservation>? stream))
            {
                stream = new();
                _observations.Add(key, stream);
            }

            stream.Enqueue(
                new(
                    observedAtUtc,
                    frame.FrameTimeMilliseconds,
                    frame.DisplayTracked,
                    frame.DisplayedTimeMilliseconds));
            while (stream.Count > MaximumObservationsPerStream)
            {
                _ = stream.Dequeue();
            }

            RemoveStaleObservations(observedAtUtc);
        }
    }

    private FrameRateSample CreateCurrentSample()
    {
        FrameRateSample sample = CreateCurrentSampleCore(
            out string? streamSummary);

        // Zapis poza blokada: ta sama blokada chroni czytnik klatek, a dysk
        // albo antywirus potrafi zatrzymac zapis na dluzej.
        if (streamSummary is not null)
        {
            FrameCaptureLog.Write(streamSummary);
        }

        return sample;
    }

    private FrameRateSample CreateCurrentSampleCore(
        out string? streamSummary)
    {
        streamSummary = null;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_observationSync)
        {
            RemoveStaleObservations(now);
            if (now - _lastStreamSummaryUtc >= StreamSummaryInterval)
            {
                _lastStreamSummaryUtc = now;
                streamSummary =
                    "streams "
                        + (_observations.Count == 0
                            ? "none"
                            : string.Join(
                                " ",
                                _observations.Select(pair =>
                                    $"pid={pair.Key.ProcessId}"
                                        + $"/sc={pair.Key.SwapChainAddress}"
                                        + $"/n={pair.Value.Count}"
                                        + "/avgMs="
                                        + pair.Value
                                            .Average(observation =>
                                                observation.FrameTimeMilliseconds)
                                            .ToString(
                                                "F2",
                                                CultureInfo.InvariantCulture))));
            }

            KeyValuePair<FrameStreamKey, Queue<FrameObservation>>? best =
                _observations
                    .Where(pair => pair.Value.Count > 0)
                    .OrderByDescending(pair => pair.Value.Count)
                    .ThenByDescending(pair => pair.Value.Last().ObservedAtUtc)
                    .Cast<
                        KeyValuePair<
                            FrameStreamKey,
                            Queue<FrameObservation>>?>()
                    .FirstOrDefault();
            if (best is null)
            {
                return _captureProcess is { HasExited: true }
                    ? FrameRateSample.Failed(
                        string.IsNullOrWhiteSpace(_lastDiagnostic)
                            ? "PresentMon zakończył pomiar bez danych klatek."
                            : _lastDiagnostic)
                    : FrameRateSample.WaitingForGame(
                        _targetProcessIds.SingleOrDefault());
            }

            double averageFrameTime = best.Value.Value.Average(
                observation => observation.FrameTimeMilliseconds);
            double framesPerSecond = 1000d / averageFrameTime;
            if (!double.IsFinite(framesPerSecond)
                || framesPerSecond is <= 0 or > 10_000)
            {
                return FrameRateSample.Failed(
                    "PresentMon zwrócił nieprawidłowy czas klatki.");
            }

            SelectStatisticsStream(best.Value.Key);

            // Pola wyswietlania tylko przy sledzeniu wyswietlania; bez niego
            // (ustawienie wylaczone albo brak kolumny) probka jest taka jak
            // dotychczas.
            double? displayedFramesPerSecond = null;
            int? droppedFrames = null;
            if (best.Value.Value.Any(observation => observation.DisplayTracked))
            {
                int displayedCount = 0;
                double displayedTotal = 0d;
                int dropped = 0;
                foreach (FrameObservation observation in best.Value.Value)
                {
                    if (observation.DisplayedTimeMilliseconds is double displayed)
                    {
                        displayedCount++;
                        displayedTotal += displayed;
                    }
                    else if (observation.DisplayTracked)
                    {
                        dropped++;
                    }
                }

                if (displayedCount > 0)
                {
                    double displayedFps = 1000d * displayedCount / displayedTotal;
                    if (double.IsFinite(displayedFps)
                        && displayedFps is > 0 and <= 10_000)
                    {
                        displayedFramesPerSecond = displayedFps;
                    }
                }

                droppedFrames = dropped;
            }

            return new(
                FrameRateStatus.Measuring,
                framesPerSecond,
                averageFrameTime,
                best.Value.Key.ProcessId,
                $"FPS zmierzony przez PresentMon {PresentMonComponent.Version} "
                + "dla zweryfikowanego PID gry.",
                displayedFramesPerSecond,
                droppedFrames);
        }
    }

    private void RemoveStaleObservations(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - ObservationWindow;
        foreach (FrameStreamKey key in _observations.Keys.ToArray())
        {
            Queue<FrameObservation> stream = _observations[key];
            while (stream.TryPeek(out FrameObservation observation)
                && observation.ObservedAtUtc < cutoff)
            {
                _ = stream.Dequeue();
            }

            if (stream.Count == 0)
            {
                _ = _observations.Remove(key);
                _pendingStatistics.Remove(key);
            }
        }
    }

    private void SelectStatisticsStream(FrameStreamKey key)
    {
        // Pierwszy odczyt wybiera strumien jak pomiar okienkowy. Histogram
        // zachowuje tez klatki sprzed tego wyboru, bez limitu kolejki wykresu.
        // Po wyborze kazda klatka tego strumienia trafia wprost do sesji.
        if (_pendingStatistics.Remove(key, out PendingStatistics? pending))
        {
            _sessionStatistics?.Merge(
                pending.Present,
                pending.Displayed,
                pending.Dropped);
        }

        _statisticsStream = key;
        _pendingStatistics.Clear();
    }

    internal static int? SelectCaptureTargetProcessId(
        IReadOnlyCollection<int> processIds)
    {
        List<CaptureTargetCandidate> candidates = [];
        foreach (int processId in processIds)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                process.Refresh();
                if (process.HasExited)
                {
                    continue;
                }

                candidates.Add(
                    new(
                        processId,
                        process.MainWindowHandle != IntPtr.Zero,
                        Math.Max(0, process.WorkingSet64),
                        process.StartTime.ToUniversalTime()));
            }
            catch (Exception exception) when (
                exception is
                    ArgumentException
                    or InvalidOperationException
                    or NotSupportedException
                    or UnauthorizedAccessException
                    or Win32Exception)
            {
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.HasMainWindow)
            .ThenByDescending(candidate => candidate.WorkingSetBytes)
            .ThenByDescending(candidate => candidate.StartedAtUtc)
            .ThenBy(candidate => candidate.ProcessId)
            .Select(candidate => (int?)candidate.ProcessId)
            .FirstOrDefault();
    }

    private async ValueTask StopCaptureCoreAsync()
    {
        Process? process = _captureProcess;
        CaptureProcessIdentity? captureIdentity = _captureIdentity;
        CancellationTokenSource? captureCancellation =
            _captureCancellation;
        Task standardOutputTask = _standardOutputTask;
        Task standardErrorTask = _standardErrorTask;

        if (process is null)
        {
            captureCancellation?.Dispose();
            ResetCaptureState();
            return;
        }

        if (captureIdentity is null)
        {
            throw new PresentMonCaptureCleanupException(
                "Brak dowodu własności aktywnego procesu PresentMon. "
                + "Dismode zachował uchwyt i nie wykonał kill.");
        }

        Exception? gracefulStopFailure = null;
        if (!process.HasExited)
        {
            try
            {
                await TerminateNamedSessionAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is
                    InvalidOperationException
                    or NotSupportedException
                    or Win32Exception)
            {
                gracefulStopFailure = exception;
            }

            _ = await WaitForExitWithTimeoutAsync(process)
                .ConfigureAwait(false);
        }

        if (!process.HasExited)
        {
            if (!MatchesOwnedChildIdentity(process, captureIdentity))
            {
                throw new PresentMonCaptureCleanupException(
                    "Proces PresentMon nie odpowiada już zweryfikowanemu "
                    + "procesowi potomnemu Dismode. Kill został zablokowany.",
                    gracefulStopFailure);
            }

            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch (Exception exception) when (
                exception is
                    InvalidOperationException
                    or NotSupportedException
                    or Win32Exception)
            {
                throw new PresentMonCaptureCleanupException(
                    "Windows odmówił zakończenia własnego procesu PresentMon.",
                    exception);
            }

            if (!await WaitForExitWithTimeoutAsync(process)
                    .ConfigureAwait(false)
                || !process.HasExited)
            {
                throw new PresentMonCaptureCleanupException(
                    "Własny proces PresentMon nie zakończył się po "
                    + "ograniczonym żądaniu kill.",
                    gracefulStopFailure);
            }
        }

        // Bezwarunkowo, takze gdy proces zniknal sam. Zatrzymanie sesji
        // siedzialo wylacznie w galezi "proces jeszcze zyje", wiec PresentMon
        // padniety albo ubity z zewnatrz zostawial sesje dzialajaca. Od tego
        // momentu kazdy pomiar klatek na tej maszynie — nasz i kazdego innego
        // narzedzia — widzial pustke. Sesja nie ma prawa przezyc pomiaru.
        _ = EtwSessionCleanup.StopStaleSessions([_sessionName]);

        captureCancellation?.Cancel();
        Exception? readerFailure = await ObserveReaderCompletionAsync(
                standardOutputTask,
                standardErrorTask)
            .ConfigureAwait(false);
        captureCancellation?.Dispose();
        process.Dispose();
        lock (_observationSync)
        {
            // Readers have drained, so this is PresentMon's final word;
            // ResetCaptureState is about to clear it.
            _stoppedCaptureDiagnostic = _lastDiagnostic;
        }

        ResetCaptureState();
        if (readerFailure is not null)
        {
            throw new PresentMonCaptureCleanupException(
                "PresentMon zakończył się, ale nie udało się domknąć "
                + "strumieni telemetrii.",
                readerFailure);
        }
    }

    private async Task TerminateNamedSessionAsync()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _executablePath,
            WorkingDirectory =
                Path.GetDirectoryName(_executablePath)
                ?? throw new InvalidOperationException(
                    "Katalog PresentMon jest niedostępny."),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--terminate_existing_session");
        startInfo.ArgumentList.Add("--session_name");
        startInfo.ArgumentList.Add(_sessionName);

        using Process terminator = new()
        {
            StartInfo = startInfo,
        };
        if (!terminator.Start())
        {
            throw new InvalidOperationException(
                "Windows nie uruchomił kontrolowanego terminatora PresentMon.");
        }

        CaptureProcessIdentity terminatorIdentity =
            CaptureOwnedChildIdentity(terminator);

        if (await WaitForExitWithTimeoutAsync(terminator)
                .ConfigureAwait(false))
        {
            return;
        }

        if (!terminator.HasExited)
        {
            if (!MatchesOwnedChildIdentity(
                    terminator,
                    terminatorIdentity))
            {
                throw new PresentMonCaptureCleanupException(
                    "Terminator PresentMon utracił zweryfikowaną tożsamość; "
                    + "kill został zablokowany.");
            }

            terminator.Kill(entireProcessTree: false);
        }

        if (!await WaitForExitWithTimeoutAsync(terminator)
                .ConfigureAwait(false)
            || !terminator.HasExited)
        {
            throw new PresentMonCaptureCleanupException(
                "Kontrolowany terminator PresentMon nie zakończył się "
                + "w wymaganym czasie.");
        }
    }

    private CaptureProcessIdentity CaptureOwnedChildIdentity(Process process)
    {
        process.Refresh();
        if (process.HasExited)
        {
            throw new InvalidOperationException(
                "PresentMon zakończył się przed potwierdzeniem własności.");
        }

        int processId = process.Id;
        DateTime startedAtUtc = process.StartTime.ToUniversalTime();
        string executablePath = ReadExecutablePath(process);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                executablePath,
                _executablePath))
        {
            throw new InvalidOperationException(
                "Uruchomiony proces nie odpowiada przypiętemu PresentMon.");
        }

        IReadOnlyDictionary<int, int> parents =
            new ProcessParentMapProvider().Capture();
        if (!parents.TryGetValue(processId, out int parentProcessId)
            || parentProcessId != Environment.ProcessId)
        {
            throw new InvalidOperationException(
                "Uruchomiony PresentMon nie jest bezpośrednim procesem "
                + "potomnym Dismode.");
        }

        process.Refresh();
        if (process.HasExited
            || process.Id != processId
            || process.StartTime.ToUniversalTime() != startedAtUtc)
        {
            throw new InvalidOperationException(
                "Tożsamość procesu PresentMon zmieniła się podczas kontroli.");
        }

        return new(
            processId,
            startedAtUtc,
            executablePath,
            parentProcessId);
    }

    private static bool MatchesOwnedChildIdentity(
        Process process,
        CaptureProcessIdentity expected)
    {
        try
        {
            process.Refresh();
            if (process.HasExited
                || process.Id != expected.ProcessId
                || process.StartTime.ToUniversalTime()
                    != expected.StartedAtUtc
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    ReadExecutablePath(process),
                    expected.ExecutablePath))
            {
                return false;
            }

            IReadOnlyDictionary<int, int> parents =
                new ProcessParentMapProvider().Capture();
            return parents.TryGetValue(
                    expected.ProcessId,
                    out int parentProcessId)
                && parentProcessId == expected.ParentProcessId
                && parentProcessId == Environment.ProcessId;
        }
        catch (Exception exception) when (
            exception is
                ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or UnauthorizedAccessException
                or Win32Exception)
        {
            return false;
        }
    }

    private static PresentMonCaptureCleanupException?
        TryTerminateNewlyStartedProcess(
        Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return null;
            }

            process.Kill(entireProcessTree: false);
            if (!process.WaitForExit(
                    checked((int)ProcessStopTimeout.TotalMilliseconds))
                || !process.HasExited)
            {
                return new PresentMonCaptureCleanupException(
                    "Nowo uruchomiony PresentMon nie zakończył się po "
                    + "wycofaniu startu.");
            }

            return null;
        }
        catch (Exception exception) when (
            exception is
                InvalidOperationException
                or NotSupportedException
                or Win32Exception)
        {
            return new PresentMonCaptureCleanupException(
                "Nie udało się zakończyć nowo uruchomionego PresentMon.",
                exception);
        }
    }

    private static unsafe string ReadExecutablePath(Process process)
    {
        const int maximumWindowsPath = 32_767;
        char[] path = new char[maximumWindowsPath];
        uint length = checked((uint)path.Length);
        fixed (char* pathPointer = path)
        {
            if (!ProcessNativeMethods.QueryFullProcessImageName(
                    process.SafeHandle,
                    flags: 0,
                    pathPointer,
                    ref length))
            {
                throw new Win32Exception();
            }

            return Path.GetFullPath(
                new string(pathPointer, 0, checked((int)length)));
        }
    }

    private static async ValueTask<bool> WaitForExitWithTimeoutAsync(
        Process process)
    {
        if (process.HasExited)
        {
            return true;
        }

        using CancellationTokenSource timeout =
            new(ProcessStopTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }

        return process.HasExited;
    }

    private static async ValueTask<Exception?> ObserveReaderCompletionAsync(
        Task standardOutputTask,
        Task standardErrorTask)
    {
        List<Exception> failures = [];
        foreach (Task reader in new[]
                 {
                     standardOutputTask,
                     standardErrorTask,
                 })
        {
            try
            {
                await reader.WaitAsync(ProcessStopTimeout)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures),
        };
    }

    private void ResetCaptureState()
    {
        _captureProcess = null;
        _captureIdentity = null;
        _captureCancellation = null;
        _standardOutputTask = Task.CompletedTask;
        _standardErrorTask = Task.CompletedTask;
        _targetProcessIds = [];
        _captureStartedAtUtc = null;
        _captureProducedFrame = false;
        _initialCaptureRestartCount = 0;
        lock (_observationSync)
        {
            if (_statisticsStream is null && _observations.Count > 0)
            {
                FrameStreamKey best = _observations
                    .OrderByDescending(pair => pair.Value.Count)
                    .ThenByDescending(pair => pair.Value.Last().ObservedAtUtc)
                    .First().Key;
                SelectStatisticsStream(best);
            }

            _statisticsStream = null;
            _pendingStatistics.Clear();
            _observations.Clear();
            _lastDiagnostic = null;
        }
    }

    private static string GetCurrentUserIdentityKey()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
    }

    internal static string CreateCaptureSessionName(
        string userIdentityKey) =>
        CreateCaptureSessionName("Dismode", userIdentityKey);

    /// <summary>
    /// The name releases published as GameShift gave the same user's
    /// session. Stopped alongside the current name, never created.
    /// </summary>
    internal static string CreateLegacyCaptureSessionName(
        string userIdentityKey) =>
        CreateCaptureSessionName("GameShift", userIdentityKey);

    private static string CreateCaptureSessionName(
        string productPrefix,
        string userIdentityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userIdentityKey);
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(userIdentityKey));
        return $"{productPrefix}-{Convert.ToHexString(hash.AsSpan(0, 8))}";
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record CaptureProcessIdentity(
        int ProcessId,
        DateTime StartedAtUtc,
        string ExecutablePath,
        int ParentProcessId);

    private readonly record struct FrameStreamKey(
        int ProcessId,
        string SwapChainAddress);

    private readonly record struct FrameObservation(
        DateTimeOffset ObservedAtUtc,
        double FrameTimeMilliseconds,
        bool DisplayTracked = false,
        double? DisplayedTimeMilliseconds = null);

    private sealed class PendingStatistics
    {
        internal FrameTimeHistogram Present { get; } = new();

        // Tworzony przy pierwszej wyswietlonej klatce (~2,4 MB).
        internal FrameTimeHistogram? Displayed { get; set; }

        internal int Dropped { get; set; }
    }

    private readonly record struct CaptureTargetCandidate(
        int ProcessId,
        bool HasMainWindow,
        long WorkingSetBytes,
        DateTime StartedAtUtc);
}

internal sealed class PresentMonCaptureCleanupException :
    InvalidOperationException
{
    internal PresentMonCaptureCleanupException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
