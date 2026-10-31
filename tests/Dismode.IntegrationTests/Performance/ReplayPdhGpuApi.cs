using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Dismode.Windows.Gpu;

namespace Dismode.IntegrationTests.Performance;

// Fixed WDDM counter corpus; no Windows calls or hardware access.
internal sealed class ReplayPdhGpuApi : IPdhGpuApi
{
    private readonly Payload[] _payloads;
    private long _sample;
    private int _counter;
    internal int Opens { get; private set; }
    internal int Closes { get; private set; }
    internal int ResizeRequests { get; private set; }
    internal bool Removed { get; set; }
    internal uint ItemStatus { get; set; }

    internal ReplayPdhGpuApi()
    {
        _payloads =
        [
            CreatePayload(128, index => string.Create(CultureInfo.InvariantCulture,
                $"pid_{(index % 2 == 0 ? 42 : 1000) + index / 2}_luid_0x00000000_0x{1 + index % 2:X8}_phys_0_eng_0_engtype_3D")),
            CreatePayload(2, index => string.Create(CultureInfo.InvariantCulture,
                $"luid_0x00000000_0x{index + 1:X8}_phys_0")),
            CreatePayload(64, index => string.Create(CultureInfo.InvariantCulture,
                $"pid_{42 + index}_luid_0x00000000_0x00000001_phys_0")),
        ];
    }

    public uint Open(out nint query) { Opens++; _counter = 0; query = 1; return 0; }
    public uint Add(nint query, string path, out nint counter) { counter = ++_counter; return 0; }
    public uint Collect(nint query) { _sample++; return 0; }
    public void Close(nint query) => Closes++;

    public uint Read(nint counter, ref uint size, out uint count, nint buffer)
    {
        Payload payload = _payloads[(int)counter - 1];
        count = Removed ? 0 : (uint)payload.NameOffsets.Length;
        if (Removed)
        {
            return 0x800007D5;
        }

        if (size < payload.Bytes.Length)
        {
            size = (uint)payload.Bytes.Length;
            ResizeRequests++;
            return 0x800007D2;
        }

        Marshal.Copy(payload.Bytes, 0, buffer, payload.Bytes.Length);
        int stride = Marshal.SizeOf<PdhGpuCounterSource.RawCounterItem>();
        int rawOffset = (int)Marshal.OffsetOf<PdhGpuCounterSource.RawCounterItem>("Value");
        int firstOffset = (int)Marshal.OffsetOf<PdhGpuCounterSource.RawCounter>("FirstValue");
        int secondOffset = (int)Marshal.OffsetOf<PdhGpuCounterSource.RawCounter>("SecondValue");
        for (int index = 0; index < count; index++)
        {
            int item = index * stride;
            Marshal.WriteIntPtr(buffer, item, buffer + payload.NameOffsets[index]);
            Marshal.WriteInt32(buffer, item + rawOffset, unchecked((int)ItemStatus));
            Marshal.WriteInt64(buffer, item + rawOffset + firstOffset,
                counter == 1 ? _sample * 50_000 : 512 * 1024 * 1024);
            Marshal.WriteInt64(buffer, item + rawOffset + secondOffset, _sample * TimeSpan.TicksPerSecond);
        }

        return 0;
    }

    private static Payload CreatePayload(int count, Func<int, string> name)
    {
        string[] names = Enumerable.Range(0, count).Select(name).ToArray();
        int size = count * Marshal.SizeOf<PdhGpuCounterSource.RawCounterItem>();
        byte[] bytes = new byte[size + names.Sum(item => (item.Length + 1) * sizeof(char))];
        int[] offsets = new int[count];
        for (int index = 0; index < count; index++)
        {
            offsets[index] = size;
            size += Encoding.Unicode.GetBytes(names[index], bytes.AsSpan(size));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(size), 0);
            size += sizeof(char);
        }

        return new(bytes, offsets);
    }

    private sealed record Payload(byte[] Bytes, int[] NameOffsets);
}
