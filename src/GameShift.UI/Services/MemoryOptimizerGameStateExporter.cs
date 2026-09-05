using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameShift.UI.Services;

public sealed record KnownGameExportItem(
    Guid Id,
    string DisplayName,
    string ExecutablePath);

public sealed class MemoryOptimizerGameStateExporter
{
    private const int MaximumDocumentBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _directory;
    private readonly string _knownGamesPath;
    private readonly string _activeGamePath;

    public MemoryOptimizerGameStateExporter(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (userSid.Length > 184 ||
            userSid.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "SID użytkownika jest nieprawidłowy.",
                nameof(userSid));
        }

        _directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "GameShift",
            "Shared",
            userSid);
        _knownGamesPath = Path.Combine(_directory, "known-games-v1.json");
        _activeGamePath = Path.Combine(_directory, "active-game-v1.json");
    }

    public async Task ExportKnownGamesAsync(
        IEnumerable<KnownGameExportItem> games,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(games);
        KnownGameEntry[] entries = games
            .Where(static game =>
                game.Id != Guid.Empty &&
                !string.IsNullOrWhiteSpace(game.DisplayName) &&
                !string.IsNullOrWhiteSpace(game.ExecutablePath))
            .Select(static game => new KnownGameEntry(
                game.Id.ToString("D"),
                game.DisplayName.Trim(),
                [Path.GetFileName(game.ExecutablePath)]))
            .Where(static game =>
                !string.IsNullOrWhiteSpace(game.ExecutableNames[0]))
            .DistinctBy(static game => game.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static game => game.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(4096)
            .ToArray();
        await WriteAtomicAsync(
            _knownGamesPath,
            new KnownGamesDocument(1, DateTimeOffset.UtcNow, entries),
            cancellationToken);
    }

    public async Task RenewActiveGameLeaseAsync(
        Guid gameId,
        string displayName,
        int processId,
        CancellationToken cancellationToken)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException(
                "Identyfikator gry nie może być pusty.",
                nameof(gameId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                "PID gry musi być dodatni.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await WriteAtomicAsync(
            _activeGamePath,
            new ActiveGameDocument(
                1,
                gameId.ToString("D"),
                displayName.Trim(),
                processId,
                now,
                now.AddSeconds(12)),
            cancellationToken);
    }

    private async Task WriteAtomicAsync<T>(
        string targetPath,
        T document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            document,
            JsonOptions);
        if (payload.Length == 0 || payload.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                "Neutralny dokument Memory Optimizer przekracza limit 1 MiB.");
        }

        string temporaryPath = targetPath + ".tmp-" +
            Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                Encoding.UTF8.GetString(payload),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record KnownGamesDocument(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        IReadOnlyList<KnownGameEntry> Games);

    private sealed record KnownGameEntry(
        string Id,
        string DisplayName,
        IReadOnlyList<string> ExecutableNames);

    private sealed record ActiveGameDocument(
        int SchemaVersion,
        string GameId,
        string DisplayName,
        int ProcessId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}
