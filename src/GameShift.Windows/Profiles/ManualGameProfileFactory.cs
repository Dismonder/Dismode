using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Profiles;
using GameShift.Windows.Processes;

namespace GameShift.Windows.Profiles;

public sealed class ManualGameProfileFactory
{
    private readonly TimeProvider _timeProvider;
    private readonly LocalGameArtworkResolver _artworkResolver;

    public ManualGameProfileFactory(
        TimeProvider? timeProvider = null,
        LocalGameArtworkResolver? artworkResolver = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _artworkResolver = artworkResolver ?? new();
    }

    public async ValueTask<ManualGameProfile> CreateAsync(
        string displayName,
        string executablePath,
        IEnumerable<string>? launchArguments,
        OptimizationPreset preset,
        CancellationToken cancellationToken)
    {
        DetectedGame manualGame = new(
            "Manual",
            Path.GetFileName(executablePath),
            displayName,
            executablePath,
            launchArguments?.ToArray() ?? [],
            Confidence: 100);
        return await CreateCoreAsync(
                manualGame,
                preset,
                artworkPath: null,
                resolveArtwork: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<ManualGameProfile> CreateDetectedAsync(
        DetectedGame game,
        OptimizationPreset preset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        return CreateCoreAsync(
            game,
            preset,
            artworkPath: null,
            resolveArtwork: true,
            cancellationToken);
    }

    internal ValueTask<ManualGameProfile> CreateDetectedWithArtworkAsync(
        DetectedGame game,
        OptimizationPreset preset,
        string? artworkPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        return CreateCoreAsync(
            game,
            preset,
            artworkPath,
            resolveArtwork: false,
            cancellationToken);
    }

    private async ValueTask<ManualGameProfile> CreateCoreAsync(
        DetectedGame game,
        OptimizationPreset preset,
        string? artworkPath,
        bool resolveArtwork,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(game.ExecutablePath);
        string fullPath = Path.GetFullPath(game.ExecutablePath);
        if (!File.Exists(fullPath)
            || !string.Equals(
                Path.GetExtension(fullPath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                "The selected manual game executable does not exist.",
                fullPath);
        }

        string workingDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                "The selected executable has no working directory.");
        string sha256 = await ExecutableFileHasher.ComputeSha256Async(
                fullPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (resolveArtwork)
        {
            GameArtwork? artwork = await _artworkResolver.ResolveAsync(
                    game with
                    {
                        ExecutablePath = fullPath,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            artworkPath = artwork?.LocalPath;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        return new(
            GameProfileId.Create(),
            game.DisplayName,
            fullPath,
            sha256,
            workingDirectory,
            game.LaunchArguments,
            preset,
            isEnabled: true,
            now,
            now,
            artworkPath);
    }
}
