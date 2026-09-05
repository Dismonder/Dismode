using GameShift.Contracts.SystemOptimization;

namespace GameShift.SystemAgent.SystemOptimization;

internal interface ISystemTweakRuntime
{
    ValueTask<SystemTweakRuntimeResult> ApplyAsync(
        SystemTweakRuntimeRequest request,
        int applicationIndex,
        CancellationToken cancellationToken);

    ValueTask<SystemTweakRuntimeResult> RestoreAsync(
        SystemTweakRuntimeRequest request,
        int applicationIndex,
        CancellationToken cancellationToken);

    ValueTask<bool> IsOperationTargetActiveAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    ValueTask<string> GetVerifiedStateHashAsync(
        SystemTweakRuntimeRequest request,
        int applicationIndex,
        CancellationToken cancellationToken);
}

internal sealed record SystemTweakRuntimeRequest(
    Guid OperationId,
    string OwnerSid,
    string GameExecutableHash,
    TweakSelection Selection);
