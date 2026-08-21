using System.Diagnostics;
using System.Globalization;
using GameShift.Core.Domain.Processes;
using GameShift.Core.Profiles;
using GameShift.Windows.Processes;
using GameShift.Windows.Profiles;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class GameProfileLaunchTests
{
    [TestMethod]
    public async Task ManualProfileLaunchesVerifiedExeAndTracksChildTree()
    {
        string rootReadyFile = CreateReadyFilePath("root");
        string childReadyFile = CreateReadyFilePath("child");
        ManualGameProfileFactory factory = new();
        ManualGameProfile profile = await factory.CreateAsync(
            "Harness game",
            ProcessHarnessFixture.FindHarnessExecutable(),
            [
                "--ready-file",
                rootReadyFile,
                "--spawn-child-ready-file",
                childReadyFile,
            ],
            OptimizationPreset.Safe,
            CancellationToken.None);
        ManualGameProfileLauncher launcher = new();

        LaunchedGameProcess launched = await launcher.LaunchAsync(
            profile,
            CancellationToken.None);
        await WaitForFileAsync(rootReadyFile);
        await WaitForFileAsync(childReadyFile);

        int rootProcessId = ReadProcessId(rootReadyFile);
        int childProcessId = ReadProcessId(childReadyFile);
        try
        {
            Assert.AreEqual(
                rootProcessId,
                launched.Identity.RuntimeKey.ProcessId);
            Assert.AreEqual(
                profile.ExecutableSha256,
                launched.Identity.ExecutableSha256);

            ProcessTreeSnapshot tree =
                await new ProcessTreeTracker().CaptureAsync(
                    launched.Identity,
                    CancellationToken.None);
            ProcessTreeMember? child = tree.Members.SingleOrDefault(member =>
                member.Identity.RuntimeKey.ProcessId == childProcessId);

            Assert.IsNotNull(child);
            Assert.AreEqual(rootProcessId, child.ParentProcessId);
            Assert.AreEqual(1, child.Depth);
        }
        finally
        {
            await CloseProcessAsync(childProcessId);
            await CloseProcessAsync(rootProcessId);
            File.Delete(childReadyFile);
            File.Delete(rootReadyFile);
        }
    }

    [TestMethod]
    public async Task LauncherRejectsExecutableChangedAfterProfileApproval()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "GameShift.ProfileLaunchTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        string executablePath = Path.Combine(testDirectory, "ChangedGame.exe");
        File.Copy(
            ProcessHarnessFixture.FindHarnessExecutable(),
            executablePath);

        try
        {
            ManualGameProfile profile =
                await new ManualGameProfileFactory().CreateAsync(
                    "Changed game",
                    executablePath,
                    launchArguments: null,
                    OptimizationPreset.Safe,
                    CancellationToken.None);
            await File.AppendAllTextAsync(
                executablePath,
                "changed",
                CancellationToken.None);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => new ManualGameProfileLauncher()
                    .LaunchAsync(profile, CancellationToken.None)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LauncherFinishesIdentityCaptureAfterProcessStarts()
    {
        string readyFile = CreateReadyFilePath("cancellation-boundary");
        using CancellationTokenSource callerCancellation = new();
        CancelCallerAfterStartIdentityProvider identityProvider =
            new(callerCancellation);
        ManualGameProfile profile =
            await new ManualGameProfileFactory().CreateAsync(
                "Cancellation boundary",
                ProcessHarnessFixture.FindHarnessExecutable(),
                ["--ready-file", readyFile],
                OptimizationPreset.Safe,
                CancellationToken.None);
        int? processId = null;

        try
        {
            LaunchedGameProcess launched =
                await new ManualGameProfileLauncher(identityProvider)
                    .LaunchAsync(
                        profile,
                        callerCancellation.Token);
            processId = launched.Identity.RuntimeKey.ProcessId;

            Assert.IsTrue(callerCancellation.IsCancellationRequested);
            Assert.AreEqual(
                profile.ExecutableSha256,
                launched.Identity.ExecutableSha256);
        }
        finally
        {
            if (processId is null && File.Exists(readyFile))
            {
                processId = ReadProcessId(readyFile);
            }

            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            if (File.Exists(readyFile))
            {
                File.Delete(readyFile);
            }
        }
    }

    private static string CreateReadyFilePath(string role) =>
        Path.Combine(
            Path.GetTempPath(),
            $"gameshift-profile-{role}-{Guid.NewGuid():N}.ready");

    private static async Task WaitForFileAsync(string path)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(50),
                timeout.Token);
        }
    }

    private static int ReadProcessId(string readyFile) =>
        int.Parse(
            File.ReadAllText(readyFile),
            NumberStyles.None,
            CultureInfo.InvariantCulture);

    private static async Task CloseProcessAsync(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return;
            }

            ProcessWindowHelper.RequestGracefulClose(process);
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (ArgumentException)
        {
            // The controlled test process already exited.
        }
        finally
        {
            process?.Dispose();
        }
    }

    private sealed class CancelCallerAfterStartIdentityProvider(
        CancellationTokenSource callerCancellation)
        : IProcessIdentityProvider
    {
        private readonly ProcessIdentityProvider _inner = new();

        public ValueTask<ProcessIdentity?> TryCaptureAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            callerCancellation.Cancel();
            return _inner.TryCaptureAsync(processId, cancellationToken);
        }

        public ValueTask<ProcessIdentity?> TryCaptureAsync(
            Process process,
            CancellationToken cancellationToken)
        {
            callerCancellation.Cancel();
            return _inner.TryCaptureAsync(process, cancellationToken);
        }

        public ValueTask<bool> MatchesRuntimeIdentityAsync(
            ProcessIdentity expectedIdentity,
            CancellationToken cancellationToken) =>
            _inner.MatchesRuntimeIdentityAsync(
                expectedIdentity,
                cancellationToken);
    }
}
