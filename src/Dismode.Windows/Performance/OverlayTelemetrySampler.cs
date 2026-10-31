using System.Diagnostics;
using Dismode.Windows.Cpu;
using Dismode.Windows.Gpu;

namespace Dismode.Windows.Performance;

public readonly record struct OverlayTelemetrySnapshot(LogicalProcessorLoad? Cpu, GpuTelemetrySnapshot? Gpu,
    GpuVendorTelemetrySnapshot? Vendor = null);

/// <summary>Jeden worker jest wlascicielem samplerow; UI czyta tylko kopie snapshotu.</summary>
public sealed class OverlayTelemetrySampler : IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly OverlayTelemetrySession _session;
    private Thread? _worker;
    private OverlayTelemetrySnapshot _snapshot;
    private int? _processId;
    private long _generation;
    private bool _active;
    private bool _vendorTelemetryEnabled;
    private bool _disposed;

    public OverlayTelemetrySampler() : this(new(), new(), TimeProvider.System, vendor: new GpuVendorTelemetrySource()) { }

    internal OverlayTelemetrySampler(LogicalProcessorLoadSampler cpu, GpuTelemetrySampler gpu, TimeProvider clock,
        Action<Exception>? log = null, IGpuVendorTelemetrySource? vendor = null) => _session = new(cpu, gpu, clock, log, vendor);

    public OverlayTelemetrySnapshot Snapshot
    {
        get { lock (_sync) { return _snapshot; } }
    }

    public void SetActive(bool active, int? gameProcessId, bool vendorTelemetryEnabled = false)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active == active && (!active || _processId == gameProcessId)
                && _vendorTelemetryEnabled == vendorTelemetryEnabled)
            {
                return;
            }

            _active = active;
            _processId = gameProcessId;
            _vendorTelemetryEnabled = vendorTelemetryEnabled;
            _generation++;
            _snapshot = default;
            if (active && _worker is null)
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "Dismode overlay telemetry" };
                _worker.Start();
            }

            _wake.Set();
        }
    }

    private void Run()
    {
        long generation = 0;
        try
        {
            while (true)
            {
                bool active;
                int? processId;
                bool vendorTelemetryEnabled;
                long requestedGeneration;
                lock (_sync)
                {
                    if (_disposed)
                    {
                        break;
                    }

                    active = _active;
                    processId = _processId;
                    vendorTelemetryEnabled = _vendorTelemetryEnabled;
                    requestedGeneration = _generation;
                }

                OverlayTelemetrySnapshot sample = default;
                try
                {
                    if (generation != requestedGeneration)
                    {
                        _ = _session.Sample(false, null);
                        generation = requestedGeneration;
                    }

                    sample = _session.Sample(active, processId, vendorTelemetryEnabled);
                }
                catch (Exception exception)
                {
                    _session.BackOff(exception);
                }

                lock (_sync)
                {
                    // Ukrycie lub zmiana PID podczas odczytu uniewaznia wynik, bez zamykania uchwytu w UI.
                    if (!_disposed && _active && generation == _generation)
                    {
                        _snapshot = sample;
                    }
                }

                _wake.WaitOne(active ? 1000 : Timeout.Infinite);
            }
        }
        finally
        {
            CompleteDisposal();
        }
    }

    public void Dispose()
    {
        bool inline;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _snapshot = default;
            inline = _worker is null;
            _wake.Set();
        }

        // Bez workera nigdy nie otwarto PDH. W innym przypadku sprzata wylacznie worker po odczycie.
        if (inline)
        {
            CompleteDisposal();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return new(_completed.Task);
    }

    private void CompleteDisposal()
    {
        try { _session.Dispose(); }
        catch (Exception exception) { _session.Log(exception); }
        finally
        {
            _wake.Dispose();
            _completed.TrySetResult();
        }
    }
}

internal sealed class OverlayTelemetrySession : IDisposable
{
    private readonly LogicalProcessorLoadSampler _cpu;
    private readonly GpuTelemetrySampler _gpu;
    private readonly IGpuVendorTelemetrySource? _vendor;
    private readonly TimeProvider _clock;
    private OverlayTelemetrySnapshot _snapshot;
    private long? _lastSample;
    private int? _processId;
    private bool _active;
    private bool _vendorTelemetryEnabled;
    private bool _disposed;
    private readonly Action<Exception>? _log;
    private long? _failedAt;

    internal OverlayTelemetrySession(LogicalProcessorLoadSampler cpu, GpuTelemetrySampler gpu, TimeProvider clock,
        Action<Exception>? log = null, IGpuVendorTelemetrySource? vendor = null)
    {
        _cpu = cpu;
        _gpu = gpu;
        _clock = clock;
        _log = log;
        _vendor = vendor;
    }

    public OverlayTelemetrySnapshot Sample(bool active, int? gameProcessId, bool vendorTelemetryEnabled = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vendorTelemetryEnabled && (!active || !vendorTelemetryEnabled || _processId != gameProcessId))
        {
            _vendor?.Stop();
            _snapshot = _snapshot with { Vendor = null };
        }

        if (_active && (!active || _processId != gameProcessId))
        {
            _cpu.Reset();
            _gpu.Reset();
            _lastSample = null;
            _snapshot = default;
        }

        _active = active;
        _vendorTelemetryEnabled = active && vendorTelemetryEnabled;
        _processId = gameProcessId;
        if (active && (_failedAt is null || _clock.GetElapsedTime(_failedAt.Value) >= TimeSpan.FromSeconds(30))
            && (_lastSample is null || _clock.GetElapsedTime(_lastSample.Value) >= TimeSpan.FromSeconds(1)))
        {
            try
            {
                _snapshot = new(_cpu.Sample(), _gpu.Sample(gameProcessId));
                if (_vendorTelemetryEnabled && _snapshot.Gpu is { } gpu)
                {
                    try { _snapshot = _snapshot with { Vendor = _vendor?.Sample(gpu.Adapter) }; }
                    catch (Exception exception) { _vendor?.Stop(); Log(exception); }
                }
                _lastSample = _clock.GetTimestamp();
                _failedAt = null;
            }
            catch (Exception exception)
            {
                BackOff(exception);
            }
        }

        return _snapshot;
    }

    internal void BackOff(Exception exception)
    {
        _failedAt = _clock.GetTimestamp();
        _snapshot = default;
        _lastSample = null;
        _cpu.Reset();
        try { _gpu.Reset(); }
        catch (Exception cleanupException) { Log(cleanupException); }
        _vendor?.Stop();
        Log(exception);
    }

    internal void Log(Exception exception)
    {
        try
        {
            if (_log is not null) { _log(exception); }
            else { Trace.TraceError("Overlay telemetry failed: {0}", exception); }
        }
        catch (Exception)
        {
            // Awaria loggera rowniez nie moze zakonczyc workera ani aplikacji.
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _gpu.Dispose();
            _vendor?.Dispose();
            _disposed = true;
        }
    }
}
