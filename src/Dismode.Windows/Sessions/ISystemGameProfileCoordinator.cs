using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;

namespace Dismode.Windows.Sessions;

public interface ISystemGameProfileCoordinator
{
    ValueTask<SystemGameProfileOperationResult> ActivateAsync(
        GameProfileId profileId,
        ProcessIdentity gameIdentity,
        CancellationToken cancellationToken);

    ValueTask<SystemGameProfileOperationResult> RestoreAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken);
}

public sealed record SystemGameProfileOperationResult(
    bool Succeeded,
    bool WasApplied,
    string Message)
{
    public static SystemGameProfileOperationResult Applied(string message) =>
        new(Succeeded: true, WasApplied: true, message);

    public static SystemGameProfileOperationResult Restored(string message) =>
        new(Succeeded: true, WasApplied: false, message);

    public static SystemGameProfileOperationResult Skipped(string message) =>
        new(Succeeded: true, WasApplied: false, message);

    public static SystemGameProfileOperationResult Failed(string message) =>
        new(Succeeded: false, WasApplied: false, message);
}

internal sealed class NullSystemGameProfileCoordinator :
    ISystemGameProfileCoordinator
{
    internal static NullSystemGameProfileCoordinator Instance { get; } = new();

    private NullSystemGameProfileCoordinator()
    {
    }

    public ValueTask<SystemGameProfileOperationResult> ActivateAsync(
        GameProfileId profileId,
        ProcessIdentity gameIdentity,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            SystemGameProfileOperationResult.Skipped(
                "System Optimizer nie został podłączony."));

    public ValueTask<SystemGameProfileOperationResult> RestoreAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            SystemGameProfileOperationResult.Skipped(
                "Nie było zewnętrznego profilu do przywrócenia."));
}
