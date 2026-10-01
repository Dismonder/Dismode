using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Profiles;
using Dismode.Windows.Processes;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Sessions;

[TestClass]
public sealed class AutoGameDetectionServiceTests
{
    [TestMethod]
    public async Task ScanAsyncDetectsRunningGameWhenProcessMatches()
    {
        string gameExe = @"C:\Games\CyberGame\CyberGame.exe";
        ManualGameProfile profile = new(
            GameProfileId.Create(),
            "CyberGame",
            gameExe,
            new string('A', 64),
            @"C:\Games\CyberGame",
            [],
            OptimizationPreset.Safe,
            isEnabled: true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        FakeProfileRepository repository = new([profile]);
        FakeProcessInventory inventory = new([
            new ProcessSnapshot(
                ProcessId: 1234,
                Name: "CyberGame",
                StartedAtUtc: DateTimeOffset.UtcNow,
                ExecutablePath: gameExe,
                SessionId: 1,
                WorkingSetBytes: 500L * 1024 * 1024,
                TotalProcessorTime: TimeSpan.FromSeconds(5),
                HasMainWindow: true)
        ]);

        AutoGameDetectionService service = new(inventory);
        AutoGameDetectionResult result = await service.ScanAsync(repository, currentSessionId: 1);

        Assert.IsTrue(result.IsGameDetected);
        Assert.IsNotNull(result.DetectedGame);
        Assert.AreEqual("CyberGame", result.DetectedGame.DisplayName);
        Assert.IsNotNull(result.GameProcess);
        Assert.AreEqual(1234, result.GameProcess.ProcessId);
    }

    [TestMethod]
    public async Task ScanAsyncReturnsNoneWhenNoGameIsRunning()
    {
        FakeProfileRepository repository = new([
            new(
                GameProfileId.Create(),
                "CyberGame",
                @"C:\Games\CyberGame\CyberGame.exe",
                new string('A', 64),
                @"C:\Games\CyberGame",
                [],
                OptimizationPreset.Safe,
                isEnabled: true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)
        ]);

        FakeProcessInventory inventory = new([
            new ProcessSnapshot(
                ProcessId: 5678,
                Name: "notepad",
                StartedAtUtc: DateTimeOffset.UtcNow,
                ExecutablePath: @"C:\Windows\notepad.exe",
                SessionId: 1,
                WorkingSetBytes: 20L * 1024 * 1024,
                TotalProcessorTime: TimeSpan.FromSeconds(1),
                HasMainWindow: true)
        ]);

        AutoGameDetectionService service = new(inventory);
        AutoGameDetectionResult result = await service.ScanAsync(repository, currentSessionId: 1);

        Assert.IsFalse(result.IsGameDetected);
        Assert.IsNull(result.DetectedGame);
        Assert.IsNull(result.GameProcess);
    }

    private sealed class FakeProfileRepository : IGameProfileRepository
    {
        private readonly List<ManualGameProfile> _profiles;

        public FakeProfileRepository(IEnumerable<ManualGameProfile> profiles)
        {
            _profiles = profiles.ToList();
        }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<ManualGameProfile>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ManualGameProfile>>(_profiles);

        public ValueTask<ManualGameProfile?> FindAsync(GameProfileId profileId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_profiles.FirstOrDefault(p => p.ProfileId == profileId));

        public ValueTask UpsertAsync(ManualGameProfile profile, CancellationToken cancellationToken)
        {
            _profiles.RemoveAll(p => p.ProfileId == profile.ProfileId);
            _profiles.Add(profile);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteAsync(GameProfileId profileId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(_profiles.RemoveAll(p => p.ProfileId == profileId) > 0);
        }
    }

    private sealed class FakeProcessInventory : IProcessInventory
    {
        private readonly IReadOnlyList<ProcessSnapshot> _snapshots;

        public FakeProcessInventory(IReadOnlyList<ProcessSnapshot> snapshots)
        {
            _snapshots = snapshots;
        }

        public IReadOnlyList<ProcessSnapshot> Capture() => _snapshots;
    }
}
