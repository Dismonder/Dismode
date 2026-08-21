using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Profiles;
using GameShift.Windows.Processes;
using GameShift.Windows.Sessions;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class SavedBackgroundRuleResolverTests
{
    [TestMethod]
    public void ResolvesOnlyExactSafeSavedRules()
    {
        const int currentSessionId = 7;
        string gamePath = @"C:\Games\Example\game.exe";
        string chatPath = @"C:\Apps\Chat\chat.exe";
        string syncPath = @"C:\Apps\Sync\sync.exe";
        GameOptimizationPreferences preferences = CreatePreferences(
            new(chatPath, SavedBackgroundActionMode.CloseAndRestore),
            new(syncPath, SavedBackgroundActionMode.LowerPriorityAndEcoQos),
            new(
                @"C:\Apps\Ignored\ignored.exe",
                SavedBackgroundActionMode.Ignore));
        SavedBackgroundRuleResolver resolver = new(
            new FakeProcessInventory(
            [
                CreateProcess(
                    processId: 101,
                    name: "chat",
                    chatPath,
                    currentSessionId,
                    hasMainWindow: true),
                CreateProcess(
                    processId: 102,
                    name: "sync",
                    syncPath,
                    currentSessionId),
                CreateProcess(
                    processId: 103,
                    name: "ignored",
                    @"C:\Apps\Ignored\ignored.exe",
                    currentSessionId,
                    hasMainWindow: true),
                CreateProcess(
                    processId: 104,
                    name: "chat",
                    @"C:\Other\chat.exe",
                    currentSessionId,
                    hasMainWindow: true),
            ]));

        SavedBackgroundRuleResolution result = resolver.Resolve(
            preferences,
            gamePath,
            currentSessionId,
            maximumProcessCount: 64);

        Assert.AreEqual(2, result.ConfiguredRuleCount);
        Assert.AreEqual(2, result.MatchedRuleCount);
        Assert.HasCount(2, result.Selections);
        Assert.IsTrue(result.Selections.Any(selection =>
            selection.ProcessId == 101
            && selection.ActionMode
                == BackgroundProcessActionMode.CloseAndRestore));
        Assert.IsTrue(result.Selections.Any(selection =>
            selection.ProcessId == 102
            && selection.ActionMode
                == BackgroundProcessActionMode.LowerPriorityAndEcoQos));
    }

    [TestMethod]
    public void SkipsRulesThatAreNotCurrentlySafeOrApplicable()
    {
        const int currentSessionId = 7;
        string gamePath = @"C:\Games\Example\game.exe";
        GameOptimizationPreferences preferences = CreatePreferences(
            new(
                @"C:\Apps\Steam\steam.exe",
                SavedBackgroundActionMode.LowerPriority),
            new(
                @"C:\Apps\Busy\busy.exe",
                SavedBackgroundActionMode.LowerPriority),
            new(
                @"C:\Apps\Windowless\tool.exe",
                SavedBackgroundActionMode.CloseAndRestore),
            new(
                @"C:\Apps\OtherSession\tool.exe",
                SavedBackgroundActionMode.LowerPriority));
        SavedBackgroundRuleResolver resolver = new(
            new FakeProcessInventory(
            [
                CreateProcess(
                    201,
                    "steam",
                    @"C:\Apps\Steam\steam.exe",
                    currentSessionId),
                CreateProcess(
                    202,
                    "busy",
                    @"C:\Apps\Busy\busy.exe",
                    currentSessionId,
                    priorityClass: "High"),
                CreateProcess(
                    203,
                    "tool",
                    @"C:\Apps\Windowless\tool.exe",
                    currentSessionId),
                CreateProcess(
                    204,
                    "tool",
                    @"C:\Apps\OtherSession\tool.exe",
                    currentSessionId + 1),
            ]));

        SavedBackgroundRuleResolution result = resolver.Resolve(
            preferences,
            gamePath,
            currentSessionId,
            maximumProcessCount: 64);

        Assert.AreEqual(4, result.ConfiguredRuleCount);
        Assert.AreEqual(0, result.MatchedRuleCount);
        Assert.IsEmpty(result.Selections);
    }

    [TestMethod]
    public void DuplicateWindowsBlockCloseButNotPriorityRules()
    {
        const int currentSessionId = 7;
        string gamePath = @"C:\Games\Example\game.exe";
        string closePath = @"C:\Apps\Duplicate\window.exe";
        string lowerPath = @"C:\Apps\Workers\worker.exe";
        GameOptimizationPreferences preferences = CreatePreferences(
            new(closePath, SavedBackgroundActionMode.CloseAndRestore),
            new(lowerPath, SavedBackgroundActionMode.LowerPriority));
        SavedBackgroundRuleResolver resolver = new(
            new FakeProcessInventory(
            [
                CreateProcess(
                    301,
                    "window",
                    closePath,
                    currentSessionId,
                    hasMainWindow: true),
                CreateProcess(
                    302,
                    "window",
                    closePath,
                    currentSessionId,
                    hasMainWindow: true),
                CreateProcess(
                    303,
                    "worker",
                    lowerPath,
                    currentSessionId),
                CreateProcess(
                    304,
                    "worker",
                    lowerPath,
                    currentSessionId),
            ]));

        SavedBackgroundRuleResolution result = resolver.Resolve(
            preferences,
            gamePath,
            currentSessionId,
            maximumProcessCount: 64);

        Assert.AreEqual(2, result.ConfiguredRuleCount);
        Assert.AreEqual(1, result.MatchedRuleCount);
        Assert.HasCount(2, result.Selections);
        Assert.IsTrue(result.Selections.All(selection =>
            selection.ActionMode
                == BackgroundProcessActionMode.LowerPriority));
    }

    private static GameOptimizationPreferences CreatePreferences(
        params SavedBackgroundProcessRule[] rules) =>
        new(
            GameProfileId.Create(),
            SavedGamePriorityMode.AboveNormal,
            rules,
            DateTimeOffset.UtcNow);

    private static ProcessSnapshot CreateProcess(
        int processId,
        string name,
        string executablePath,
        int sessionId,
        bool hasMainWindow = false,
        string priorityClass = "Normal") =>
        new(
            processId,
            name,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            executablePath,
            sessionId,
            WorkingSetBytes: 128 * 1024 * 1024,
            TotalProcessorTime: TimeSpan.FromSeconds(1),
            hasMainWindow,
            priorityClass);

    private sealed class FakeProcessInventory(
        IReadOnlyList<ProcessSnapshot> processes) : IProcessInventory
    {
        public IReadOnlyList<ProcessSnapshot> Capture() => processes;
    }
}
