using System.Diagnostics;
using GameShift.Core.Domain.Processes;
using GameShift.Windows.Processes;
using GameShift.Windows.SystemOptimization;

namespace GameShift.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class ActiveGameLeaseReaderTests
{
    private const string OwnerSid = "S-1-5-21-1000";

    [TestMethod]
    public async Task ValidLeaseUsesVerifiedRuntimeIdentity()
    {
        using LeaseContext context = new(
            OwnerSid,
            DateTimeOffset.UtcNow.AddMinutes(1));

        ActiveGameLease? lease = await context.Reader.TryReadAsync(
            OwnerSid,
            CancellationToken.None);

        Assert.IsNotNull(lease);
        Assert.AreEqual("game-a", lease.GameId);
        Assert.AreEqual(4242, lease.ProcessId);
        Assert.AreEqual(new string('A', 64), lease.ExecutableSha256);
    }

    [TestMethod]
    public async Task ExpiredOrDifferentUserLeaseIsRejected()
    {
        using LeaseContext expired = new(
            OwnerSid,
            DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.IsNull(await expired.Reader.TryReadAsync(
            OwnerSid,
            CancellationToken.None));

        using LeaseContext wrongUser = new(
            "S-1-5-21-2000",
            DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.IsNull(await wrongUser.Reader.TryReadAsync(
            OwnerSid,
            CancellationToken.None));
    }

    private sealed class LeaseContext : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift-ActiveGameLeaseTests",
            Guid.NewGuid().ToString("N"));

        internal LeaseContext(string processOwnerSid, DateTimeOffset expiresAtUtc)
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "active-game-v1.json");
            File.WriteAllText(
                path,
                $$"""
                {
                  "schemaVersion": 1,
                  "gameId": "game-a",
                  "displayName": "Test Game",
                  "processId": 4242,
                  "startedAtUtc": "{{DateTimeOffset.UtcNow:O}}",
                  "expiresAtUtc": "{{expiresAtUtc:O}}"
                }
                """);
            Reader = new(
                path,
                new FixedIdentityProvider(processOwnerSid),
                TimeProvider.System);
        }

        internal ActiveGameLeaseReader Reader { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FixedIdentityProvider(string ownerSid) :
        IProcessIdentityProvider
    {
        public ValueTask<ProcessIdentity?> TryCaptureAsync(
            int processId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ProcessIdentity?>(
                new(
                    new(processId, DateTimeOffset.UtcNow),
                    @"C:\Games\TestGame.exe",
                    new string('A', 64),
                    publisher: null,
                    ownerSid,
                    sessionId: 1,
                    parentRuntimeKey: null));

        public ValueTask<ProcessIdentity?> TryCaptureAsync(
            Process process,
            CancellationToken cancellationToken) =>
            TryCaptureAsync(process.Id, cancellationToken);

        public ValueTask<bool> MatchesRuntimeIdentityAsync(
            ProcessIdentity expectedIdentity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }
}
