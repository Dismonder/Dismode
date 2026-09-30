using System.Text.Json;
using Dismode.Core.Domain.Processes;
using Dismode.Windows.Processes;

namespace Dismode.Windows.SystemOptimization;

public sealed record ActiveGameLease(
    string GameId,
    string DisplayName,
    int ProcessId,
    string ExecutablePath,
    string ExecutableSha256,
    DateTimeOffset ExpiresAtUtc);

public sealed class ActiveGameLeaseReader
{
    private const long MaximumDocumentBytes = 1024 * 1024;
    private static readonly TimeSpan MaximumFutureLease =
        TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly IProcessIdentityProvider _identityProvider;
    private readonly TimeProvider _timeProvider;

    public ActiveGameLeaseReader(
        string path,
        IProcessIdentityProvider? identityProvider = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static ActiveGameLeaseReader CreateForUser(string userSid)
    {
        ValidateSid(userSid);
        string path = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "Dismode",
            "Shared",
            userSid,
            "active-game-v1.json");
        return new(path);
    }

    public async ValueTask<ActiveGameLease?> TryReadAsync(
        string expectedUserSid,
        CancellationToken cancellationToken)
    {
        ValidateSid(expectedUserSid);
        try
        {
            FileInfo file = new(_path);
            if (!file.Exists
                || file.Length is <= 0 or > MaximumDocumentBytes)
            {
                return null;
            }

            await using FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            ActiveGameLeaseDocument? document =
                await JsonSerializer.DeserializeAsync<ActiveGameLeaseDocument>(
                        stream,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (document is null
                || document.SchemaVersion != 1
                || !IsOpaqueIdentifier(document.GameId)
                || string.IsNullOrWhiteSpace(document.DisplayName)
                || document.DisplayName.Length > 256
                || document.ProcessId <= 0
                || document.StartedAtUtc > now.AddMinutes(1)
                || document.ExpiresAtUtc <= now
                || document.ExpiresAtUtc - now > MaximumFutureLease)
            {
                return null;
            }

            ProcessIdentity? identity =
                await _identityProvider.TryCaptureAsync(
                        document.ProcessId,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (identity is null
                || identity.RuntimeKey.ProcessId != document.ProcessId
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    identity.UserSid,
                    expectedUserSid)
                || identity.ExecutableSha256.Length != 64
                || !identity.ExecutableSha256.All(char.IsAsciiHexDigit))
            {
                return null;
            }

            return new(
                document.GameId,
                document.DisplayName.Trim(),
                document.ProcessId,
                identity.ExecutablePath,
                identity.ExecutableSha256.ToUpperInvariant(),
                document.ExpiresAtUtc);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsOpaqueIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':');

    private static void ValidateSid(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (userSid.Length > 184
            || userSid.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character != '-'))
        {
            throw new ArgumentException(
                "The user SID is invalid.",
                nameof(userSid));
        }
    }

    private sealed record ActiveGameLeaseDocument(
        int SchemaVersion,
        string GameId,
        string DisplayName,
        int ProcessId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}
