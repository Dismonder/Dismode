using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dismode.Core.Activation;

/// <param name="ShowOnly">
/// Bring the running window to the front and nothing else. The executable
/// path is still required and validated (the sender passes its own), so an
/// old client that never sends this member keeps its meaning: false. The
/// member is left out of the payload when false, because an older window
/// rejects members it does not know, and a plain game launch must keep
/// working across a mixed set of components.
/// </param>
public sealed record UiActivationRequest(
    int SchemaVersion,
    Guid RequestId,
    DateTimeOffset RequestedAtUtc,
    string GameExecutablePath,
    bool KeepWindowHidden,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool ShowOnly = false);

public static class UiActivationProtocol
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    public static readonly TimeSpan MaximumClockSkew =
        TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string GetPipeName(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        string fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(userSid.Trim())))[..20];
        return $"Dismode.UI.Activation.{fingerprint}";
    }

    public static byte[] Serialize(UiActivationRequest request)
    {
        Validate(request, DateTimeOffset.UtcNow);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            request,
            SerializerOptions);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "Żądanie aktywacji przekracza dozwolony rozmiar.");
        }

        return payload;
    }

    public static UiActivationRequest DeserializeAndValidate(
        ReadOnlySpan<byte> payload,
        DateTimeOffset nowUtc)
    {
        if (payload.IsEmpty || payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "Żądanie aktywacji ma nieprawidłowy rozmiar.");
        }

        UiActivationRequest request;
        try
        {
            request = JsonSerializer.Deserialize<UiActivationRequest>(
                payload,
                SerializerOptions)
                ?? throw new InvalidDataException(
                    "Żądanie aktywacji jest puste.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Żądanie aktywacji nie jest prawidłowym JSON.",
                exception);
        }

        Validate(request, nowUtc);
        return request with
        {
            RequestedAtUtc = request.RequestedAtUtc.ToUniversalTime(),
            GameExecutablePath =
                DismodeLaunchOptions.NormalizeExecutablePath(
                    request.GameExecutablePath),
        };
    }

    private static void Validate(
        UiActivationRequest request,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != CurrentSchemaVersion
            || request.RequestId == Guid.Empty)
        {
            throw new InvalidDataException(
                "Żądanie aktywacji ma niezgodny kontrakt.");
        }

        DateTimeOffset requestedAt =
            request.RequestedAtUtc.ToUniversalTime();
        TimeSpan age = nowUtc.ToUniversalTime() - requestedAt;
        if (age.Duration() > MaximumClockSkew)
        {
            throw new InvalidDataException(
                "Żądanie aktywacji jest nieaktualne.");
        }

        _ = DismodeLaunchOptions.NormalizeExecutablePath(
            request.GameExecutablePath);
    }
}
