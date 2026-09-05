using System.Text.Json;
using GameShift.MemoryOptimizer.Core.Ipc;
using GameShift.MemoryOptimizer.Core.Models;
using GameShift.MemoryOptimizer.Core.Native;

namespace GameShift.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class MemoryAreaAndSettingsTests
{
    private static readonly string[] ExpectedGameExclusion = ["game"];

    [TestMethod]
    public void MemoryAreaFlagsPreserveUpstreamValues()
    {
        Dictionary<string, int> expected = new(StringComparer.Ordinal)
        {
            [nameof(MemoryArea.CombinedPageList)] = 1,
            [nameof(MemoryArea.ModifiedFileCache)] = 2,
            [nameof(MemoryArea.ModifiedPageList)] = 4,
            [nameof(MemoryArea.RegistryCache)] = 8,
            [nameof(MemoryArea.StandbyList)] = 16,
            [nameof(MemoryArea.StandbyListLowPriority)] = 32,
            [nameof(MemoryArea.SystemFileCache)] = 64,
            [nameof(MemoryArea.WorkingSet)] = 128,
        };

        foreach ((string name, int value) in expected)
        {
            Assert.AreEqual(
                value,
                (int)Enum.Parse<MemoryArea>(name));
        }
    }

    [TestMethod]
    public void DefaultsEnableOnlySafeProfileAndAutomation()
    {
        MemoryOptimizerSettings settings =
            MemoryOptimizerSettings.Normalize(new());

        Assert.IsTrue(settings.AutomationEnabled);
        Assert.AreEqual(20, settings.AvailableMemoryThresholdPercent);
        Assert.AreEqual(5, settings.MaximumIdleCpuPercent);
        Assert.AreEqual(5, settings.IdleCpuMinutes);
        Assert.AreEqual(30, settings.CooldownMinutes);
        Assert.IsFalse(settings.ScheduleEnabled);
        Assert.IsFalse(settings.CompactAlwaysOnTop);
        Assert.AreEqual(2, settings.SchemaVersion);
        Assert.IsFalse(settings.AdvancedModeEnabled);
        Assert.AreEqual(
            MemoryOptimizerSettings.BasicAreas,
            settings.AutomaticAreas);
    }

    [TestMethod]
    public void VersionOneSettingsMigrateWithoutChangingCompactPinDefault()
    {
        MemoryOptimizerSettings settings = MemoryOptimizerSettings.Normalize(
            new()
            {
                SchemaVersion = 1,
                CompactMode = true,
            });

        Assert.AreEqual(MemoryOptimizerSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.IsTrue(settings.CompactMode);
        Assert.IsFalse(settings.CompactAlwaysOnTop);
    }

    [TestMethod]
    public void VersionOneSettingsJsonMigratesAndPreservesExistingOptions()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "automationEnabled": false,
              "automationPaused": true,
              "availableMemoryThresholdPercent": 25,
              "maximumIdleCpuPercent": 10,
              "compactMode": true,
              "hotkey": "Alt+F8"
            }
            """;

        MemoryOptimizerSettings settings = MemoryOptimizerSettings.Normalize(
            JsonSerializer.Deserialize<MemoryOptimizerSettings>(
                json,
                MemoryOptimizerProtocol.JsonOptions)!);

        Assert.AreEqual(2, settings.SchemaVersion);
        Assert.IsFalse(settings.AutomationEnabled);
        Assert.IsTrue(settings.AutomationPaused);
        Assert.AreEqual(25, settings.AvailableMemoryThresholdPercent);
        Assert.AreEqual(10, settings.MaximumIdleCpuPercent);
        Assert.IsTrue(settings.CompactMode);
        Assert.IsFalse(settings.CompactAlwaysOnTop);
        Assert.AreEqual("Alt+F8", settings.Hotkey);
    }

    [TestMethod]
    public void SnapshotJsonWithoutCommitFieldsDefaultsThemToZero()
    {
        const string json = """
            {
              "capturedAtUtc": "2026-08-28T12:00:00+00:00",
              "totalPhysicalBytes": 16000,
              "availablePhysicalBytes": 4000,
              "totalVirtualBytes": 128000000,
              "availableVirtualBytes": 127000000,
              "memoryLoadPercent": 75
            }
            """;

        MemorySnapshot snapshot = JsonSerializer.Deserialize<MemorySnapshot>(
            json,
            MemoryOptimizerProtocol.JsonOptions)!;

        Assert.AreEqual((ulong)0, snapshot.CommitLimitBytes);
        Assert.AreEqual((ulong)0, snapshot.AvailableCommitBytes);
        Assert.AreEqual((ulong)0, snapshot.CommittedBytes);
    }

    [TestMethod]
    public void SnapshotSeparatesCommitAccountingFromVirtualAddressSpace()
    {
        MemorySnapshot snapshot = new(
            DateTimeOffset.UtcNow,
            16_000,
            4_000,
            128_000_000,
            127_000_000,
            75,
            24_000,
            6_000);

        Assert.AreEqual((ulong)18_000, snapshot.CommittedBytes);
        Assert.AreEqual((ulong)12_000, snapshot.UsedPhysicalBytes);
    }

    [TestMethod]
    public void AdvancedConsentAllowsEveryUpstreamArea()
    {
        const MemoryArea all = (MemoryArea)255;
        MemoryOptimizerSettings settings = MemoryOptimizerSettings.Normalize(
            new()
            {
                AdvancedModeEnabled = true,
                AdvancedWarningAccepted = true,
                ManualAreas = all,
                AutomaticAreas = all,
            });

        Assert.AreEqual(
            all & ~MemoryArea.StandbyListLowPriority,
            settings.ManualAreas);
        Assert.AreEqual(
            all & ~MemoryArea.StandbyListLowPriority,
            settings.AutomaticAreas);
    }

    [TestMethod]
    public void StandbyVariantsAreMutuallyExclusive()
    {
        MemoryOptimizerSettings settings = MemoryOptimizerSettings.Normalize(
            new()
            {
                AdvancedModeEnabled = true,
                AdvancedWarningAccepted = true,
                ManualAreas = MemoryArea.StandbyList |
                    MemoryArea.StandbyListLowPriority,
            });

        Assert.IsTrue(
            (settings.ManualAreas & MemoryArea.StandbyList) != 0);
        Assert.IsFalse(
            (settings.ManualAreas & MemoryArea.StandbyListLowPriority) != 0);
    }

    [TestMethod]
    public void GlobalWorkingSetRequiresConsentAndDisablesExclusions()
    {
        MemoryOptimizerSettings rejected = MemoryOptimizerSettings.Normalize(
            new()
            {
                GlobalWorkingSet = true,
                ExcludedProcesses = ["game.exe"],
            });
        MemoryOptimizerSettings accepted = MemoryOptimizerSettings.Normalize(
            rejected with
            {
                AdvancedModeEnabled = true,
                AdvancedWarningAccepted = true,
                GlobalWorkingSet = true,
                GlobalWorkingSetWarningAccepted = true,
                ExcludedProcesses = ["game.exe"],
            });

        Assert.IsFalse(rejected.GlobalWorkingSet);
        CollectionAssert.AreEqual(
            ExpectedGameExclusion,
            rejected.ExcludedProcesses.ToArray());
        Assert.IsTrue(accepted.GlobalWorkingSet);
        Assert.HasCount(0, accepted.ExcludedProcesses);
    }

    [TestMethod]
    public void HotkeyIsCanonicalizedAndInvalidValueFallsBack()
    {
        MemoryOptimizerSettings canonical = MemoryOptimizerSettings.Normalize(
            new() { Hotkey = "shift + ctrl + f12" });
        MemoryOptimizerSettings fallback = MemoryOptimizerSettings.Normalize(
            new() { Hotkey = "run arbitrary command" });

        Assert.AreEqual("Ctrl+Shift+F12", canonical.Hotkey);
        Assert.AreEqual("Ctrl+Shift+M", fallback.Hotkey);
        Assert.IsTrue(HotkeyBinding.TryParse(
            canonical.Hotkey,
            out HotkeyBinding parsed));
        Assert.AreEqual((uint)0x7B, parsed.VirtualKey);
    }
}
