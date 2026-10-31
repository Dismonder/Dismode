using Dismode.Windows.Platform;
using Microsoft.Win32;

namespace Dismode.IntegrationTests.Platform;

[TestClass]
public sealed class GamingEnvironmentRegistryTests
{
    [TestMethod]
    [DataRow(1, true)]
    [DataRow(0, false)]
    public void HistoricalCaptureDeterminesBackgroundRecording(int value, bool expected)
    {
        FakeRegistry reader = new() { HistoricalCapture = value };

        Assert.AreEqual(expected, WindowsGamingEnvironmentDiagnostic.Evaluate(reader).BackgroundRecordingEnabled);
    }

    [TestMethod]
    public void MissingHistoricalCaptureIsUnknownEvenWhenCaptureIsAvailable()
    {
        Assert.IsNull(WindowsGamingEnvironmentDiagnostic.Evaluate(new FakeRegistry()).BackgroundRecordingEnabled);
    }

    [TestMethod]
    public void PolicyDisablesBackgroundRecordingBeforeReadingUserSetting()
    {
        FakeRegistry reader = new() { Policy = 0, DenyUserRead = true };

        Assert.IsFalse(WindowsGamingEnvironmentDiagnostic.Evaluate(reader).BackgroundRecordingEnabled);
    }

    [TestMethod]
    public void AccessDeniedIsUnknown()
    {
        FakeRegistry reader = new() { HistoricalCapture = 1, DenyUserRead = true };

        WindowsGamingDiagnosticReport report = WindowsGamingEnvironmentDiagnostic.Evaluate(reader);

        Assert.IsNull(report.BackgroundRecordingEnabled);
        Assert.IsFalse(report.Recommendations.Any(r => r.Severity == DiagnosticSeverity.Success));
    }

    [TestMethod]
    public void PolicyReadDeniedIsUnknown()
    {
        FakeRegistry reader = new() { HistoricalCapture = 1, DenyPolicyRead = true };

        Assert.IsNull(WindowsGamingEnvironmentDiagnostic.Evaluate(reader).BackgroundRecordingEnabled);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow("1")]
    [DataRow(2)]
    public void PolicyDoesNotEnableRecordingOrAcceptInvalidValues(object policy)
    {
        FakeRegistry reader = new() { Policy = policy };

        Assert.IsNull(WindowsGamingEnvironmentDiagnostic.Evaluate(reader).BackgroundRecordingEnabled);
    }

    private sealed class FakeRegistry : IGamingEnvironmentRegistryReader
    {
        public object? HistoricalCapture { get; init; }
        public object? Policy { get; init; }
        public bool DenyUserRead { get; init; }
        public bool DenyPolicyRead { get; init; }

        public object? ReadValue(RegistryHive hive, string subKey, string valueName)
        {
            if (hive == RegistryHive.LocalMachine && subKey == @"SOFTWARE\Policies\Microsoft\Windows\GameDVR" && valueName == "AllowGameDVR")
            {
                return DenyPolicyRead ? throw new UnauthorizedAccessException() : Policy;
            }

            if (hive == RegistryHive.CurrentUser && subKey == @"Software\Microsoft\Windows\CurrentVersion\GameDVR" && valueName == "HistoricalCaptureEnabled")
            {
                return DenyUserRead ? throw new UnauthorizedAccessException() : HistoricalCapture;
            }

            return valueName switch
            {
                "HwSchMode" => 2,
                "AllowAutoGameMode" => 1,
                "AppCaptureEnabled" => 1,
                _ => throw new InvalidOperationException("Unexpected registry read."),
            };
        }
    }
}
