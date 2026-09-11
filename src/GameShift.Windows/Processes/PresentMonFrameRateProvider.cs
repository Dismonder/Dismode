using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using GameShift.Windows.NativeInterop;

namespace GameShift.Windows.Processes;

public sealed class PresentMonFrameRateProvider : IFrameRateProvider
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

    private readonly string _executablePath;
    private readonly string _sessionName;
    private readonly bool _trackGpu;
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
    private BenchmarkFrameCollector? _benchmarkCollector;
    private bool _disposed;

    public PresentMonFrameRateProvider(
        string? executablePath = null,
        TimeProvider? timeProvider = null)
    {
        _executablePath = Path.GetFullPath(
            executablePath
            ?? PresentMonComponent.ResolveDefaultExecutablePath());
        _sessionName = CreateCaptureSessionName(
            GetCurrentUserIdentityKey());
        _trackGpu = false;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal PresentMonFrameRateProvider(
        string executablePath,
        string captureSessionIdentityKey,
        TimeProvider? timeProvider = null,
        bool trackGpu = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            captureSessionIdentityKey);
        _executablePath = Path.GetFullPath(executablePath);
        _sessionName = CreateCaptureSessionName(
            captureSessionIdentityKey);
        _trackGpu = trackGpu;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal int? ActiveCaptureProcessId => _captureIdentity?.ProcessId;

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
            if (_captureProcess is not null
                && !_captureProcess.HasExited
                && captureTargets.SequenceEqual(_targetProcessIds))
            {
                FrameRateSample sample = CreateCurrentSample();
                if (sample.Status == FrameRateStatus.Measuring)
                {
                    _captureProducedFrame = true;
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
                    StartCapture(captureTargets);
                    return FrameRateSample.Restarting(
                        selectedTarget.Value);
                }

                // Restart nie pomogl, a PresentMon nadal chodzi i nie oddaje
                // ani jednej klatki. Wczesniej wracalo stad "czekam na gre" —
                // w kolko, bez konca i bez powodu. Gracz widzial myslniki
                // i mial prawo uznac, ze pomiar jest zepsuty, bo byl.
                if (!_captureProducedFrame
                    && _captureStartedAtUtc is DateTimeOffset stalledSince
                    && _timeProvider.GetUtcNow() - stalledSince
                        >= FirstFrameTimeout)
                {
                    return FrameRateSample.Failed(DescribeSilentCapture());
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

            try
            {
                StartCapture(captureTargets);
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
                            + "Uruchom GameShift i zaakceptuj żądanie UAC.")
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
            await StopCaptureCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
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

    /// <summary>
    /// Names of tools that capture frames through the same ETW providers as
    /// PresentMon. Only one of them gets the events; the rest see the session
    /// start, deliver nothing, and report lost events. AMD ships one inside
    /// Adrenalin and Intel ships one as a service, so a machine can easily
    /// have two running before GameShift adds a third.
    /// </summary>
    private static readonly (string ProcessName, string Owner)[]
        CompetingCaptureTools =
        [
            ("PresentMon-x64", "AMD Adrenalin (metryki wydajności)"),
            ("PresentMon", "PresentMon firmy Intel"),
            ("PresentMonService", "usługa PresentMon firmy Intel"),
            ("RTSS", "RivaTuner Statistics Server"),
            ("CapFrameX", "CapFrameX"),
            ("FrameView", "NVIDIA FrameView"),
        ];

    /// <summary>
    /// Explains a capture that runs but never produces a frame. Guessing is
    /// worse than useless here, so the message names what is actually running
    /// on this machine and repeats PresentMon's own last words verbatim.
    /// </summary>
    private string DescribeSilentCapture()
    {
        string diagnostic;
        lock (_observationSync)
        {
            diagnostic = _lastDiagnostic ?? string.Empty;
        }

        string[] found = CompetingCaptureTools
            .Where(tool => Process
                .GetProcessesByName(tool.ProcessName)
                .Any(process =>
                {
                    process.Dispose();
                    return true;
                }))
            .Select(tool => tool.Owner)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        StringBuilder message = new(
            "PresentMon działa, ale nie dostaje ani jednej klatki.");
        if (found.Length > 0)
        {
            message.Append(
                " Na tym komputerze mierzy już co innego: ");
            message.Append(string.Join(", ", found));
            message.Append(
                ". Zdarzenia klatek trafiają tylko do jednego odbiorcy, więc "
                + "wyłącz tamten pomiar i spróbuj ponownie.");
        }
        else
        {
            message.Append(
                " Najczęstsza przyczyna to porzucona sesja pomiaru, która "
                + "została po wcześniejszym uruchomieniu i nadal przechwytuje "
                + "zdarzenia klatek. Sprawdź poleceniem "
                + "\"logman query -ets\", czy działa sesja o nazwie "
                + "zaczynającej się od GameShift, i zatrzymaj ją poleceniem "
                + "\"logman stop NAZWA -ets\".");
        }

        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            message.Append(" PresentMon zgłasza: ");
            message.Append(diagnostic);
        }

        return message.ToString();
    }

    private bool _staleSessionsChecked;

    /// <summary>
    /// Clears sessions an earlier GameShift left running before starting a new
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
        IReadOnlyList<string> stopped = EtwSessionCleanup.StopStaleSessions(
            [.. EtwSessionCleanup.LegacySessionNames, _sessionName]);
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

    private void StartCapture(int[] targets)
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
        foreach (int processId in targets)
        {
            startInfo.ArgumentList.Add("--process_id");
            startInfo.ArgumentList.Add(
                processId.ToString(CultureInfo.InvariantCulture));
        }

        startInfo.ArgumentList.Add("--output_stdout");
        startInfo.ArgumentList.Add("--no_console_stats");
        startInfo.ArgumentList.Add("--v2_metrics");
        startInfo.ArgumentList.Add("--no_track_display");
        if (!_trackGpu)
        {
            startInfo.ArgumentList.Add("--no_track_gpu");
        }
        startInfo.ArgumentList.Add("--no_track_input");
        startInfo.ArgumentList.Add("--set_circular_buffer_size");
        startInfo.ArgumentList.Add(
            PresentEventBufferSize.ToString(
                CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--terminate_on_proc_exit");
        startInfo.ArgumentList.Add("--stop_existing_session");
        startInfo.ArgumentList.Add("--session_name");
        startInfo.ArgumentList.Add(_sessionName);

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
            CaptureProcessIdentity captureIdentity =
                CaptureOwnedChildIdentity(process);
            CancellationTokenSource captureCancellation = new();
            _captureProcess = process;
            _captureIdentity = captureIdentity;
            _captureCancellation = captureCancellation;
            _targetProcessIds = [.. targets];
            _captureStartedAtUtc = _timeProvider.GetUtcNow();
            _captureProducedFrame = false;
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
        PresentMonCsvParser parser = new();
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
            if (!_observations.TryGetValue(
                    key,
                    out Queue<FrameObservation>? stream))
            {
                stream = new();
                _observations.Add(key, stream);
            }

            stream.Enqueue(
                new(observedAtUtc, frame.FrameTimeMilliseconds));
            while (stream.Count > MaximumObservationsPerStream)
            {
                _ = stream.Dequeue();
            }

            RemoveStaleObservations(observedAtUtc);
        }
    }

    private FrameRateSample CreateCurrentSample()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_observationSync)
        {
            RemoveStaleObservations(now);
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

            return new(
                FrameRateStatus.Measuring,
                framesPerSecond,
                averageFrameTime,
                best.Value.Key.ProcessId,
                $"FPS zmierzony przez PresentMon {PresentMonComponent.Version} "
                + "dla zweryfikowanego PID gry.");
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
            }
        }
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
                + "GameShift zachował uchwyt i nie wykonał kill.");
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
                    + "procesowi potomnemu GameShift. Kill został zablokowany.",
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
                + "potomnym GameShift.");
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
        string userIdentityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userIdentityKey);
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(userIdentityKey));
        return $"GameShift-{Convert.ToHexString(hash.AsSpan(0, 8))}";
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
        double FrameTimeMilliseconds);

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
