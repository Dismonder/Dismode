namespace Dismode.Windows.Gpu;

public readonly record struct GpuAdapterId(ulong Luid, uint PhysicalIndex);

public readonly record struct GpuTelemetrySnapshot(
    GpuAdapterId Adapter,
    double? ThreeDPercent,
    ulong? DedicatedBytes,
    double? CopyPercent = null,
    double? ComputePercent = null,
    double? VideoDecodePercent = null,
    double? VideoEncodePercent = null,
    double? VideoProcessingPercent = null);

internal enum GpuEngineType { ThreeD, Copy, Compute, VideoDecode, VideoEncode, VideoProcessing, Other }
internal enum GpuCounterKind { Engine, AdapterMemory, ProcessMemory }
internal readonly record struct GpuCounterIdentity(
    GpuAdapterId Adapter, int ProcessId, int EngineId, GpuEngineType EngineType, GpuCounterKind Kind);
internal readonly record struct GpuCounterReading(GpuCounterIdentity Identity, long Value, long Timestamp, bool Valid);

internal interface IGpuCounterSource : IDisposable
{
    bool TryRead(out ReadOnlySpan<GpuCounterReading> readings);
    void Stop();
}

/// <summary>Odczyt licznikow WDDM bez sterownikow producenta i bez zmiany ustawien GPU.</summary>
public sealed class GpuTelemetrySampler : IDisposable
{
    private readonly IGpuCounterSource _source;
    private Dictionary<GpuCounterIdentity, GpuCounterReading> _previous = [];
    private Dictionary<GpuCounterIdentity, GpuCounterReading> _current = [];
    private readonly Dictionary<(GpuAdapterId Adapter, int Engine, GpuEngineType Type), double> _engines = [];
    private readonly HashSet<(GpuAdapterId Adapter, GpuEngineType Type)> _invalidTypes = [];
    private readonly Dictionary<GpuAdapterId, AdapterState> _adapters = [];
    private bool _disposed;

    public GpuTelemetrySampler() : this(new PdhGpuCounterSource()) { }

    internal GpuTelemetrySampler(IGpuCounterSource source) => _source = source;

    public GpuTelemetrySnapshot? Sample(int? gameProcessId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_source.TryRead(out ReadOnlySpan<GpuCounterReading> readings))
        {
            _previous.Clear();
            return null;
        }

        _current.Clear();
        _engines.Clear();
        _invalidTypes.Clear();
        _adapters.Clear();
        foreach (GpuCounterReading reading in readings)
        {
            GpuCounterIdentity id = reading.Identity;
            if (!reading.Valid || reading.Value < 0 || reading.Timestamp < 0)
            {
                if (id.Kind == GpuCounterKind.Engine)
                {
                    _invalidTypes.Add((id.Adapter, id.EngineType));
                }

                continue;
            }

            AdapterState state = _adapters.GetValueOrDefault(id.Adapter);
            if (id.Kind == GpuCounterKind.AdapterMemory)
            {
                state.DedicatedBytes = (ulong)reading.Value;
            }
            else if (id.Kind == GpuCounterKind.ProcessMemory)
            {
                state.GameMemory |= id.ProcessId == gameProcessId && reading.Value > 0;
            }
            else
            {
                // Nowy kontekst czeka na druga probke; nie uniewaznia pozostalych procesow.
                _current[id] = reading;
                bool hasPrevious = _previous.TryGetValue(id, out GpuCounterReading previous);
                if (hasPrevious && reading.Value >= previous.Value && reading.Timestamp > previous.Timestamp)
                {
                    // GPU Engine uzywa PERF_100NSEC_TIMER: oba pola sa w jednostkach 100 ns.
                    double percent = 100d * (reading.Value - previous.Value) / (reading.Timestamp - previous.Timestamp);
                    var engine = (id.Adapter, id.EngineId, id.EngineType);
                    _engines[engine] = _engines.GetValueOrDefault(engine) + percent;
                    state.GameBusy |= id.ProcessId == gameProcessId
                        && id.EngineType == GpuEngineType.ThreeD && percent > 0;
                }
                else if (!hasPrevious)
                {
                    // Pierwszy odczyt instancji liczy sie jako 0 w tej probce, gdy baza juz istnieje.
                    if (_previous.Count > 0)
                    {
                        var engine = (id.Adapter, id.EngineId, id.EngineType);
                        _engines[engine] = _engines.GetValueOrDefault(engine);
                    }
                }
                else
                {
                    // Reset lub wrap: nie mieszamy niepelnego okna z poprawnymi deltami.
                    _invalidTypes.Add((id.Adapter, id.EngineType));
                }
            }

            _adapters[id.Adapter] = state;
        }

        (_previous, _current) = (_current, _previous);
        foreach (var (engine, percent) in _engines)
        {
            if (!_invalidTypes.Contains((engine.Adapter, engine.Type)))
            {
                AdapterState state = _adapters[engine.Adapter];
                state.SetEngine(engine.Type, Math.Clamp(percent, 0, 100));
                _adapters[engine.Adapter] = state;
            }
        }

        GpuAdapterId? selected = SelectAdapter();
        if (selected is not GpuAdapterId adapter)
        {
            return null;
        }

        AdapterState result = _adapters[adapter];
        if (result.ThreeD is null && result.DedicatedBytes is null)
        {
            return null;
        }

        return new(adapter, result.ThreeD, result.DedicatedBytes, result.Copy, result.Compute,
            result.VideoDecode, result.VideoEncode, result.VideoProcessing);
    }

    private GpuAdapterId? SelectAdapter()
    {
        GpuAdapterId? busy = null;
        GpuAdapterId? memory = null;
        GpuAdapterId? highest = null;
        int busyCount = 0;
        int memoryCount = 0;
        double maximum = -1;
        foreach (var (adapter, state) in _adapters)
        {
            if (state.GameBusy)
            {
                busy = adapter;
                busyCount++;
            }

            if (state.GameMemory)
            {
                memory = adapter;
                memoryCount++;
            }

            if (state.ThreeD is double percent)
            {
                if (percent > maximum)
                {
                    maximum = percent;
                    highest = adapter;
                }
                else if (percent == maximum)
                {
                    highest = null;
                }
            }
        }

        if (busyCount == 1)
        {
            return busy;
        }

        if (busyCount == 0 && memoryCount == 1)
        {
            return memory;
        }

        if (highest is not null)
        {
            return highest;
        }

        if (_adapters.Count == 1)
        {
            foreach (GpuAdapterId adapter in _adapters.Keys)
            {
                return adapter;
            }
        }

        return null;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _previous.Clear();
        _current.Clear();
        _source.Stop();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _source.Dispose();
            _disposed = true;
        }
    }

    private struct AdapterState
    {
        internal double? ThreeD;
        internal double? Copy;
        internal double? Compute;
        internal double? VideoDecode;
        internal double? VideoEncode;
        internal double? VideoProcessing;
        internal ulong? DedicatedBytes;
        internal bool GameBusy;
        internal bool GameMemory;

        internal void SetEngine(GpuEngineType type, double percent)
        {
            // Silniki tego samego typu moga pracowac rownolegle; raportujemy najbardziej zajety.
            switch (type)
            {
                case GpuEngineType.ThreeD: ThreeD = Math.Max(ThreeD ?? 0, percent); break;
                case GpuEngineType.Copy: Copy = Math.Max(Copy ?? 0, percent); break;
                case GpuEngineType.Compute: Compute = Math.Max(Compute ?? 0, percent); break;
                case GpuEngineType.VideoDecode: VideoDecode = Math.Max(VideoDecode ?? 0, percent); break;
                case GpuEngineType.VideoEncode: VideoEncode = Math.Max(VideoEncode ?? 0, percent); break;
                case GpuEngineType.VideoProcessing: VideoProcessing = Math.Max(VideoProcessing ?? 0, percent); break;
                case GpuEngineType.Other: break;
                default: break;
            }
        }
    }
}
