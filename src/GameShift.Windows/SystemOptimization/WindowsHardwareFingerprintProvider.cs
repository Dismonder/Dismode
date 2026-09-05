using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameShift.Contracts.SystemOptimization;
using GameShift.Core.Devices;
using GameShift.Core.SystemOptimization;
using GameShift.Windows.Devices;
using Microsoft.Win32;

namespace GameShift.Windows.SystemOptimization;

public sealed partial class WindowsHardwareFingerprintProvider :
    IHardwareFingerprintProvider
{
    private const int MaximumInventoryItems = 16;

    private readonly WindowsDeviceFormFactorDetector _formFactorDetector = new();

    public HardwareFingerprint Capture()
    {
        (string cpuVendor, string cpuFamily) = ReadCpuIdentity();
        (string[] graphics, string[] drivers) = ReadGraphicsIdentity();
        string[] networkClasses = ReadNetworkClasses();
        long physicalMemory = ReadPhysicalMemoryBytes();
        bool isLaptop = _formFactorDetector.Detect() == DeviceFormFactor.Laptop;
        string windowsBuild = ReadWindowsBuild();
        string architecture = RuntimeInformation.OSArchitecture.ToString();
        string hash = ComputeHash(
            windowsBuild,
            architecture,
            cpuVendor,
            cpuFamily,
            graphics,
            drivers,
            networkClasses,
            physicalMemory,
            isLaptop);

        return new(
            SchemaVersion: 1,
            windowsBuild,
            architecture,
            cpuVendor,
            cpuFamily,
            graphics,
            drivers,
            networkClasses,
            physicalMemory,
            isLaptop,
            hash);
    }

    private static (string Vendor, string Family) ReadCpuIdentity()
    {
        try
        {
            using RegistryKey? cpu = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                writable: false);
            string vendor = Normalize(cpu?.GetValue("VendorIdentifier") as string);
            string identifier = Normalize(cpu?.GetValue("Identifier") as string);
            string model = Normalize(cpu?.GetValue("ProcessorNameString") as string);
            return (
                string.IsNullOrWhiteSpace(vendor) ? "Unknown" : vendor,
                string.Join(
                    " | ",
                    new[] { identifier, model }
                        .Where(value => !string.IsNullOrWhiteSpace(value))));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return ("Unknown", "Unknown");
        }
    }

    private static (string[] Adapters, string[] Drivers) ReadGraphicsIdentity()
    {
        SortedSet<string> adapters = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> drivers = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? video = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Video",
                writable: false);
            foreach (string keyName in video?.GetSubKeyNames().Take(32) ?? [])
            {
                using RegistryKey? adapter = video?.OpenSubKey(
                    $@"{keyName}\0000",
                    writable: false);
                AddNormalized(
                    adapters,
                    adapter?.GetValue("Device Description") as string);
                AddNormalized(
                    adapters,
                    ReadRegistryString(adapter?.GetValue(
                        "HardwareInformation.AdapterString")));
                AddNormalized(
                    drivers,
                    adapter?.GetValue("DriverVersion") as string);
                if (adapters.Count >= MaximumInventoryItems
                    && drivers.Count >= MaximumInventoryItems)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }

        return (
            adapters.Take(MaximumInventoryItems).ToArray(),
            drivers.Take(MaximumInventoryItems).ToArray());
    }

    private static string[] ReadNetworkClasses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter =>
                    adapter.NetworkInterfaceType is not (
                        NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel))
                .Select(adapter =>
                    $"{adapter.NetworkInterfaceType}:{Normalize(adapter.Description)}")
                .Where(value => value.Length <= 260)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumInventoryItems)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static long ReadPhysicalMemoryBytes()
    {
        MemoryStatus status = new()
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatus>()),
        };
        return GlobalMemoryStatusEx(ref status)
            ? checked((long)Math.Min(status.TotalPhysical, long.MaxValue))
            : 0;
    }

    private static string ReadWindowsBuild()
    {
        Version version = Environment.OSVersion.Version;
        string updateBuildRevision = "0";
        try
        {
            using RegistryKey? currentVersion = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                writable: false);
            updateBuildRevision = Convert.ToString(
                    currentVersion?.GetValue("UBR"),
                    System.Globalization.CultureInfo.InvariantCulture)
                ?? "0";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }

        return $"{version.Major}.{version.Minor}.{version.Build}.{updateBuildRevision}";
    }

    private static string ComputeHash(
        string windowsBuild,
        string architecture,
        string cpuVendor,
        string cpuFamily,
        IReadOnlyList<string> graphics,
        IReadOnlyList<string> drivers,
        IReadOnlyList<string> networkClasses,
        long physicalMemory,
        bool isLaptop)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                windowsBuild,
                architecture,
                cpuVendor,
                cpuFamily,
                graphics = graphics.Order(StringComparer.Ordinal).ToArray(),
                drivers = drivers.Order(StringComparer.Ordinal).ToArray(),
                networkClasses = networkClasses.Order(StringComparer.Ordinal).ToArray(),
                physicalMemory,
                isLaptop,
            });
        return "sha256:"
            + Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static void AddNormalized(
        SortedSet<string> values,
        string? candidate)
    {
        string normalized = Normalize(candidate);
        if (!string.IsNullOrWhiteSpace(normalized) && normalized.Length <= 256)
        {
            values.Add(normalized);
        }
    }

    private static string ReadRegistryString(object? value) =>
        value switch
        {
            string text => text,
            byte[] bytes when bytes.Length <= 1024 =>
                Encoding.Unicode.GetString(bytes).TrimEnd('\0'),
            _ => string.Empty,
        };

    private static string Normalize(string? value) =>
        string.Join(
            ' ',
            (value ?? string.Empty)
                .Split(
                    [' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries));

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GlobalMemoryStatusEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }
}
