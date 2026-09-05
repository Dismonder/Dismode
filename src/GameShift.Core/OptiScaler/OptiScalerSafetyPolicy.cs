namespace GameShift.Core.OptiScaler;

public enum OptiScalerReleaseChannel
{
    Stable = 0,
    Beta = 1,
    Nightly = 2,
    DlssNeuralRendering = 3,
}

public static class OptiScalerReleaseChannelPolicy
{
    public static bool IsExperimental(OptiScalerReleaseChannel channel) =>
        channel switch
        {
            OptiScalerReleaseChannel.Stable => false,
            OptiScalerReleaseChannel.Beta
                or OptiScalerReleaseChannel.Nightly
                or OptiScalerReleaseChannel.DlssNeuralRendering => true,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
        };
}

public enum OptiScalerProxy
{
    Dxgi = 0,
    Winmm = 1,
    Version = 2,
    D3d12 = 3,
}

public enum OptiScalerSafetyBlockReason
{
    None = 0,
    GameRunning = 1,
    OfflineUseNotConfirmed = 2,
    AntiCheatDetected = 3,
    ExperimentalUseNotConfirmed = 4,
    GpuNotSupported = 5,
    DriverTooOld = 6,
    NeuralRenderingModelMissing = 7,
}

public sealed record OptiScalerSafetyDecision(
    bool CanInstall,
    OptiScalerSafetyBlockReason BlockReason,
    string? Evidence = null);

public static class OptiScalerSafetyPolicy
{
    private static readonly string[] AntiCheatMarkers =
    [
        "easyanticheat",
        "battleye",
        "beclient",
        "beservice",
        "vgk.sys",
        "eaanticheat",
        "ace-base",
        "xhunter",
        "robloxplayerbeta.exe",
    ];

    public static OptiScalerSafetyDecision Evaluate(
        bool gameIsRunning,
        bool offlineUseConfirmed,
        IEnumerable<string> gameFiles)
    {
        ArgumentNullException.ThrowIfNull(gameFiles);

        if (gameIsRunning)
        {
            return new(false, OptiScalerSafetyBlockReason.GameRunning);
        }

        foreach (string gameFile in gameFiles)
        {
            if (AntiCheatMarkers.Any(marker =>
                    gameFile.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return new(
                    false,
                    OptiScalerSafetyBlockReason.AntiCheatDetected,
                    gameFile);
            }
        }

        return offlineUseConfirmed
            ? new(true, OptiScalerSafetyBlockReason.None)
            : new(false, OptiScalerSafetyBlockReason.OfflineUseNotConfirmed);
    }

    public static string GetProxyFileName(OptiScalerProxy proxy) => proxy switch
    {
        OptiScalerProxy.Dxgi => "dxgi.dll",
        OptiScalerProxy.Winmm => "winmm.dll",
        OptiScalerProxy.Version => "version.dll",
        OptiScalerProxy.D3d12 => "d3d12.dll",
        _ => throw new ArgumentOutOfRangeException(nameof(proxy), proxy, null),
    };
}
