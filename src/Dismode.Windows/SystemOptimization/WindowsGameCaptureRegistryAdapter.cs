using System.Text.Json;
using Microsoft.Win32;

namespace Dismode.Windows.SystemOptimization;

public sealed class WindowsGameCaptureRegistryAdapter : IGameCaptureRegistryAdapter
{
    public const string SubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string ValueName = "HistoricalCaptureEnabled";
    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
    private const string InvalidSnapshotMessage = "Nie można bezpiecznie odczytać lub przywrócić ustawienia Game DVR.";

    public ValueTask<GameCaptureRegistryState> ReadAsync(string userSid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using RegistryKey? policy = Registry.LocalMachine.OpenSubKey(PolicyPath, writable: false);
        RegistryValueKind? policyKind = ValueKindIfPresent(policy, "AllowGameDVR");
        object? rawPolicy = policy?.GetValue("AllowGameDVR", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        bool policyAllowed = IsPolicyAllowed(rawPolicy, policyKind)
            && policyKind == ValueKindIfPresent(policy, "AllowGameDVR");

        // Otwieramy tylko zaladowany ul uzytkownika; nigdy HKCU konta SYSTEM.
        using RegistryKey user = Registry.Users.OpenSubKey(userSid, writable: false)
            ?? throw new InvalidOperationException("Rejestr użytkownika nie jest załadowany.");
        using RegistryKey? key = user.OpenSubKey(SubKeyPath, writable: false);
        RegistryValueKind? kind = ValueKindIfPresent(key, ValueName);
        object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (kind != ValueKindIfPresent(key, ValueName))
        {
            throw new InvalidOperationException(InvalidSnapshotMessage);
        }

        return ValueTask.FromResult(CreateSnapshot(userSid, kind, value, policyAllowed));
    }

    internal static GameCaptureRegistryState CreateSnapshot(
        string userSid, RegistryValueKind? kind, object? value, bool policyAllowed)
    {
        if (kind is null && value is null)
        {
            return new(userSid, false, null, null, policyAllowed);
        }

        if (kind is null or RegistryValueKind.Unknown || value is null)
        {
            throw new NotSupportedException(InvalidSnapshotMessage);
        }

        GameCaptureRegistryState state = new(userSid, true, kind, JsonSerializer.Serialize(value), policyAllowed);
        // Sprawdzamy zgodnosc typu i danych, zanim snapshot trafi do journala.
        _ = DeserializeValue(state);
        return state;
    }

    internal static bool IsPolicyAllowed(object? value, RegistryValueKind? kind) =>
        (value is null && kind is null)
        || (value is int allowed && allowed == 1 && kind == RegistryValueKind.DWord);

    private static RegistryValueKind? ValueKindIfPresent(RegistryKey? key, string name) =>
        key is not null && key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)
            ? key.GetValueKind(name)
            : null;

    public async ValueTask WriteAsync(
        GameCaptureRegistryState expected,
        GameCaptureRegistryState desired,
        CancellationToken cancellationToken)
    {
        if (!StringComparer.Ordinal.Equals(expected.UserSid, desired.UserSid)
            || !desired.IsPolicyAllowed
            || desired.IsPresent != (desired.ValueKind is not null && desired.ValueJson is not null))
        {
            throw new InvalidOperationException(InvalidSnapshotMessage);
        }

        object? value = desired.IsPresent ? DeserializeValue(desired) : null;
        GameCaptureRegistryState current = await ReadAsync(expected.UserSid, cancellationToken).ConfigureAwait(false);
        // Zastosowanie oczekuje dozwolonej polityki; przywracanie dziala takze po jej zmianie.
        if (current.WithoutPolicy() != expected.WithoutPolicy()
            || (expected.IsPolicyAllowed && !current.IsPolicyAllowed))
        {
            throw new InvalidOperationException("Polityka lub ustawienie Game DVR zmieniło się przed zapisem.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using RegistryKey user = Registry.Users.OpenSubKey(expected.UserSid, writable: true)
            ?? throw new InvalidOperationException("Rejestr użytkownika nie jest załadowany.");
        if (desired.IsPresent)
        {
            using RegistryKey key = user.CreateSubKey(SubKeyPath, writable: true);
            key.SetValue(ValueName, value!, desired.ValueKind!.Value);
            key.Flush();
        }
        else
        {
            // Brak wartosci w snapshotcie oznacza usuniecie, a nie zapis zera.
            using RegistryKey? key = user.OpenSubKey(SubKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            key?.Flush();
        }
    }

    private static object DeserializeValue(GameCaptureRegistryState state) => state.ValueKind switch
    {
        RegistryValueKind.DWord => JsonSerializer.Deserialize<int>(state.ValueJson!),
        RegistryValueKind.QWord => JsonSerializer.Deserialize<long>(state.ValueJson!),
        RegistryValueKind.String or RegistryValueKind.ExpandString =>
            JsonSerializer.Deserialize<string>(state.ValueJson!)
                ?? throw new InvalidDataException(InvalidSnapshotMessage),
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(state.ValueJson!)
            ?? throw new InvalidDataException(InvalidSnapshotMessage),
        RegistryValueKind.Binary or RegistryValueKind.None => JsonSerializer.Deserialize<byte[]>(state.ValueJson!)
            ?? throw new InvalidDataException(InvalidSnapshotMessage),
        _ => throw new NotSupportedException(InvalidSnapshotMessage),
    };
}
