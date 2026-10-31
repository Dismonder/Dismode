using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dismode.Windows.Gpu;

internal interface IPdhGpuApi
{
    uint Open(out nint query);
    uint Add(nint query, string path, out nint counter);
    uint Collect(nint query);
    uint Read(nint counter, ref uint size, out uint count, nint buffer);
    void Close(nint query);
}

internal sealed class PdhGpuCounterSource : IGpuCounterSource
{
    private const uint MoreData = 0x800007D2;
    private const uint NoData = 0x800007D5;
    private const uint NoInstance = 0x800007D1;
    private readonly IPdhGpuApi _api;
    private readonly TimeProvider _clock;
    private readonly CounterBuffer[] _counters =
    [
        new(@"\GPU Engine(*)\Utilization Percentage", GpuCounterKind.Engine),
        new(@"\GPU Adapter Memory(*)\Dedicated Usage", GpuCounterKind.AdapterMemory),
        new(@"\GPU Process Memory(*)\Dedicated Usage", GpuCounterKind.ProcessMemory),
    ];
    private Dictionary<string, NameEntry> _names = new(StringComparer.Ordinal);
    private Dictionary<string, NameEntry> _currentNames = new(StringComparer.Ordinal);
    private GpuCounterReading[] _readings = new GpuCounterReading[256];
    private QueryHandle? _query;
    private long? _failedAt;
    private bool _disposed;

    internal PdhGpuCounterSource(IPdhGpuApi? api = null, TimeProvider? clock = null)
    {
        _api = api ?? new NativePdhGpuApi();
        _clock = clock ?? TimeProvider.System;
    }

    public bool TryRead(out ReadOnlySpan<GpuCounterReading> readings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        readings = default;
        if (_failedAt is long failed && _clock.GetElapsedTime(failed) < TimeSpan.FromSeconds(30))
        {
            return false;
        }

        if (_query is null && !Open())
        {
            Fail();
            return false;
        }

        uint status = _api.Collect(_query!.DangerousGetHandle());
        if (status != 0 && status != NoData)
        {
            Fail();
            return false;
        }

        if (status == NoData)
        {
            _names.Clear();
            _currentNames.Clear();
            return true;
        }

        _currentNames.Clear();
        int written = 0;
        foreach (CounterBuffer counter in _counters)
        {
            if (counter.Handle != 0 && !ReadCounter(counter, ref written))
            {
                Fail();
                return false;
            }
        }

        (_names, _currentNames) = (_currentNames, _names);
        readings = _readings.AsSpan(0, written);
        return true;
    }

    private bool Open()
    {
        uint status = _api.Open(out nint query);
        if (query != 0)
        {
            _query = new(query, _api);
        }

        if (status != 0 || _query is null)
        {
            return false;
        }

        bool any = false;
        foreach (CounterBuffer counter in _counters)
        {
            // Poszczegolne obiekty licznikow moga byc niedostepne niezaleznie od pozostalych.
            if (_api.Add(query, counter.Path, out nint handle) == 0)
            {
                counter.Handle = handle;
                any = true;
            }
        }

        _failedAt = null;
        return any;
    }

    private unsafe bool ReadCounter(CounterBuffer counter, ref int written)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint size = counter.Buffer?.Length ?? 0;
            uint status = _api.Read(counter.Handle, ref size, out uint count,
                counter.Buffer?.DangerousGetHandle() ?? 0);
            if (status == MoreData)
            {
                if (size == 0 || size > 16 * 1024 * 1024)
                {
                    return false;
                }

                counter.Buffer?.Dispose();
                counter.Buffer = new(Math.Max(size, 16 * 1024));
                continue;
            }

            if (status is NoData or NoInstance || (status == 0 && count == 0))
            {
                return true;
            }

            if (status != 0 || counter.Buffer is null || count > counter.Buffer.Length / sizeof(RawCounterItem))
            {
                return false;
            }

            nint start = counter.Buffer.DangerousGetHandle();
            RawCounterItem* items = (RawCounterItem*)start;
            if (_readings.Length < written + count)
            {
                Array.Resize(ref _readings, Math.Max(_readings.Length * 2, checked(written + (int)count)));
            }

            for (int index = 0; index < count; index++)
            {
                RawCounterItem item = items[index];
                long offset = item.Name - start;
                if (offset < 0 || offset >= counter.Buffer.Length || offset % sizeof(char) != 0)
                {
                    return false;
                }

                ReadOnlySpan<char> text = new((void*)item.Name, (int)((counter.Buffer.Length - offset) / sizeof(char)));
                int length = text.IndexOf('\0');
                if (length < 0)
                {
                    return false;
                }

                text = text[..length];
                if (!_names.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(text, out NameEntry entry))
                {
                    if (!TryParseIdentity(text, counter.Kind, out GpuCounterIdentity identity))
                    {
                        continue;
                    }

                    entry = new(text.ToString(), identity);
                }

                _currentNames[entry.Name] = entry;
                _readings[written++] = new(entry.Identity, item.Value.FirstValue, item.Value.SecondValue,
                    item.Value.Status is 0 or 1);
            }

            return true;
        }

        return false;
    }

    internal static bool TryParseIdentity(ReadOnlySpan<char> name, GpuCounterKind kind, out GpuCounterIdentity id)
    {
        id = default;
        int pid = 0;
        if (kind != GpuCounterKind.AdapterMemory)
        {
            if (!Consume(ref name, "pid_") || !TakeNumber(ref name, out uint process) || process > int.MaxValue)
            {
                return false;
            }

            pid = (int)process;
        }

        if (!Consume(ref name, "luid_0x") || name.Length < 8
            || !uint.TryParse(name[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high))
        {
            return false;
        }

        name = name[8..];
        if (!Consume(ref name, "_0x") || name.Length < 8
            || !uint.TryParse(name[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low))
        {
            return false;
        }

        name = name[8..];
        if (!Consume(ref name, "_phys_") || !TakeNumber(ref name, out uint physical, trailingSeparator: kind == GpuCounterKind.Engine))
        {
            return false;
        }

        int engine = 0;
        GpuEngineType type = default;
        if (kind == GpuCounterKind.Engine)
        {
            if (!Consume(ref name, "eng_") || !TakeNumber(ref name, out uint number) || number > int.MaxValue
                || !Consume(ref name, "engtype_") || name.IsEmpty || name.Contains('#'))
            {
                return false;
            }

            engine = (int)number;
            type = name switch
            {
                "3D" => GpuEngineType.ThreeD,
                "Copy" => GpuEngineType.Copy,
                "Compute" or "Compute_0" or "Compute_1" => GpuEngineType.Compute,
                "VideoDecode" => GpuEngineType.VideoDecode,
                "VideoEncode" => GpuEngineType.VideoEncode,
                "VideoProcessing" => GpuEngineType.VideoProcessing,
                _ => GpuEngineType.Other,
            };
        }
        else if (!name.IsEmpty)
        {
            return false;
        }

        id = new(new(((ulong)high << 32) | low, physical), pid, engine, type, kind);
        return true;
    }

    private static bool Consume(ref ReadOnlySpan<char> text, ReadOnlySpan<char> prefix)
    {
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        text = text[prefix.Length..];
        return true;
    }

    private static bool TakeNumber(ref ReadOnlySpan<char> text, out uint value, bool trailingSeparator = true)
    {
        int end = trailingSeparator ? text.IndexOf('_') : text.Length;
        value = 0;
        if (end <= 0 || !uint.TryParse(text[..end], NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        text = trailingSeparator ? text[(end + 1)..] : text[end..];
        return true;
    }

    private void Fail()
    {
        Stop();
        _failedAt = _clock.GetTimestamp();
    }

    public void Stop()
    {
        _query?.Dispose();
        _query = null;
        _failedAt = null;
        _names.Clear();
        _currentNames.Clear();
        foreach (CounterBuffer counter in _counters)
        {
            counter.Handle = 0;
            counter.Buffer?.Dispose();
            counter.Buffer = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawCounter
    {
        internal uint Status;
        internal uint TimeLow;
        internal uint TimeHigh;
        internal long FirstValue;
        internal long SecondValue;
        internal uint MultiCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawCounterItem
    {
        internal nint Name;
        internal RawCounter Value;
    }

    private readonly record struct NameEntry(string Name, GpuCounterIdentity Identity);

    private sealed class CounterBuffer(string path, GpuCounterKind kind)
    {
        internal string Path { get; } = path;
        internal GpuCounterKind Kind { get; } = kind;
        internal nint Handle { get; set; }
        internal NativeBuffer? Buffer { get; set; }
    }

    private sealed class NativeBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal uint Length { get; }
        internal NativeBuffer(uint length) : base(true)
        {
            Length = length;
            SetHandle(Marshal.AllocHGlobal(checked((int)length)));
        }

        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); return true; }
    }

    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        // SafeHandle zamyka cale zapytanie wraz z licznikami takze po porzuceniu konsumenta.
        private readonly IPdhGpuApi _api;
        internal QueryHandle(nint query, IPdhGpuApi api) : base(true)
        {
            _api = api;
            SetHandle(query);
        }

        protected override bool ReleaseHandle() { _api.Close(handle); return true; }
    }
}

internal sealed partial class NativePdhGpuApi : IPdhGpuApi
{
    public uint Open(out nint query) => PdhOpenQueryW(0, 0, out query);
    public uint Add(nint query, string path, out nint counter) => PdhAddEnglishCounterW(query, path, 0, out counter);
    public uint Collect(nint query) => PdhCollectQueryData(query);
    public uint Read(nint counter, ref uint size, out uint count, nint buffer) =>
        PdhGetRawCounterArrayW(counter, ref size, out count, buffer);
    public void Close(nint query) => _ = PdhCloseQuery(query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhOpenQueryW(nint dataSource, nuint userData, out nint query);
    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);
    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);
    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetRawCounterArrayW(nint counter, ref uint size, out uint count, nint buffer);
    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
