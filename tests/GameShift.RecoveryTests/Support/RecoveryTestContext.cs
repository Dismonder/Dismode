using GameShift.Contracts.Protocol;
using GameShift.Core.Actions;
using GameShift.Core.Domain.Identifiers;

namespace GameShift.RecoveryTests.Support;

internal sealed class RecoveryTestContext : IDisposable
{
    private readonly string _directory;

    public RecoveryTestContext()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift.RecoveryTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        JournalPath = Path.Combine(_directory, "recovery.journal");
        ActionId = ActionId.Create();
        ExecutionContext = new(
            SessionId.Create(),
            ActionId,
            IdempotencyKey.Create(),
            DateTimeOffset.UtcNow);
    }

    public string JournalPath { get; }

    public ActionId ActionId { get; }

    public ActionExecutionContext ExecutionContext { get; }

    public void Dispose()
    {
        string expectedRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "GameShift.RecoveryTests"));
        string actualDirectory = Path.GetFullPath(_directory);

        if (!actualDirectory.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to clean a directory outside the recovery-test root.");
        }

        if (Directory.Exists(actualDirectory))
        {
            Directory.Delete(actualDirectory, recursive: true);
        }
    }
}

