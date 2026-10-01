using Dismode.Core.OptiScaler;

namespace Dismode.UnitTests.OptiScaler;

[TestClass]
public sealed class NeuralRenderingPolicyTests
{
    private static readonly OptiScalerSafetyDecision Allowed =
        new(true, OptiScalerSafetyBlockReason.None);

    [TestMethod]
    [DataRow("32.0.15.6636", 566, 36)]
    [DataRow("32.0.16.1656", 616, 56)]
    [DataRow("31.0.15.3699", 536, 99)]
    public void WindowsDriverVersionMapsToBrandedNvidiaVersion(
        string windowsVersion,
        int expectedMajor,
        int expectedMinor)
    {
        Assert.IsTrue(NvidiaDriverVersion.TryFromWindowsDriverVersion(
            windowsVersion,
            out NvidiaDriverVersion version));
        Assert.AreEqual(expectedMajor, version.Major);
        Assert.AreEqual(expectedMinor, version.Minor);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("32.0.15")]
    [DataRow("32.0.15.66x6")]
    [DataRow("32.0.1.6")]
    public void MalformedDriverVersionIsRejected(string? windowsVersion)
    {
        Assert.IsFalse(NvidiaDriverVersion.TryFromWindowsDriverVersion(
            windowsVersion,
            out _));
    }

    [TestMethod]
    public void BrandedVersionFormatsWithPaddedMinor()
    {
        Assert.AreEqual("616.05", new NvidiaDriverVersion(616, 5).ToString());
    }

    [TestMethod]
    public void BlockedBaseDecisionIsReturnedUnchanged()
    {
        OptiScalerSafetyDecision blocked = new(
            false,
            OptiScalerSafetyBlockReason.AntiCheatDetected,
            "EasyAntiCheat.sys");

        OptiScalerSafetyDecision decision = NeuralRenderingPolicy.Evaluate(
            blocked,
            SupportedCapability());

        Assert.AreSame(blocked, decision);
    }

    [TestMethod]
    [DataRow(GeForceGeneration.PreRtx)]
    [DataRow(GeForceGeneration.Rtx20)]
    [DataRow(GeForceGeneration.Rtx30)]
    [DataRow(GeForceGeneration.Rtx40)]
    public void GenerationsBelowRtx50AreBlocked(GeForceGeneration generation)
    {
        OptiScalerSafetyDecision decision = NeuralRenderingPolicy.Evaluate(
            Allowed,
            SupportedCapability() with { Generation = generation });

        Assert.IsFalse(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.GpuNotSupported,
            decision.BlockReason);
    }

    [TestMethod]
    public void NonGeForceHardwareIsBlocked()
    {
        OptiScalerSafetyDecision decision = NeuralRenderingPolicy.Evaluate(
            Allowed,
            new(
                IsNvidiaGeForce: false,
                GeForceGeneration.Unknown,
                DriverVersion: null,
                NeuralRenderingModelAvailable: false));

        Assert.IsFalse(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.GpuNotSupported,
            decision.BlockReason);
    }

    [TestMethod]
    public void DriverOlderThanMinimumIsBlocked()
    {
        OptiScalerSafetyDecision decision = NeuralRenderingPolicy.Evaluate(
            Allowed,
            SupportedCapability() with { DriverVersion = new(616, 55) });

        Assert.IsFalse(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.DriverTooOld,
            decision.BlockReason);
    }

    [TestMethod]
    public void UnknownDriverVersionIsBlocked()
    {
        OptiScalerSafetyDecision decision = NeuralRenderingPolicy.Evaluate(
            Allowed,
            SupportedCapability() with { DriverVersion = null });

        Assert.IsFalse(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.DriverTooOld,
            decision.BlockReason);
    }

    [TestMethod]
    public void MinimumDriverVersionIsAccepted()
    {
        OptiScalerSafetyDecision decision = NeuralRenderingPolicy.Evaluate(
            Allowed,
            SupportedCapability() with
            {
                DriverVersion = NeuralRenderingPolicy.MinimumDriverVersion,
            });

        Assert.IsTrue(decision.CanInstall);
    }

    [TestMethod]
    public void MissingModelIsBlockedEvenOnSupportedHardware()
    {
        OptiScalerSafetyDecision decision = NeuralRenderingPolicy.Evaluate(
            Allowed,
            SupportedCapability() with { NeuralRenderingModelAvailable = false });

        Assert.IsFalse(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.NeuralRenderingModelMissing,
            decision.BlockReason);
    }

    [TestMethod]
    public void SupportedHardwareWithModelIsAllowed()
    {
        OptiScalerSafetyDecision decision = NeuralRenderingPolicy.Evaluate(
            Allowed,
            SupportedCapability());

        Assert.IsTrue(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.None,
            decision.BlockReason);
    }

    [TestMethod]
    public void NeuralRenderingChannelIsExperimental()
    {
        Assert.IsTrue(OptiScalerReleaseChannelPolicy.IsExperimental(
            OptiScalerReleaseChannel.DlssNeuralRendering));
    }

    [TestMethod]
    [DataRow("NVIDIA GeForce RTX 5070", GeForceGeneration.Rtx50)]
    [DataRow("NVIDIA GeForce RTX 5090 Laptop GPU", GeForceGeneration.Rtx50)]
    [DataRow("NVIDIA GeForce RTX 4080", GeForceGeneration.Rtx40)]
    [DataRow("NVIDIA GeForce RTX 3060 Ti", GeForceGeneration.Rtx30)]
    [DataRow("NVIDIA GeForce RTX 2060", GeForceGeneration.Rtx20)]
    [DataRow("NVIDIA GeForce GTX 1080 Ti", GeForceGeneration.PreRtx)]
    [DataRow("AMD Radeon RX 9070 XT", GeForceGeneration.Unknown)]
    [DataRow("Intel Arc B580", GeForceGeneration.Unknown)]
    [DataRow(null, GeForceGeneration.Unknown)]
    [DataRow("", GeForceGeneration.Unknown)]
    public void AdapterDescriptionMapsToGeneration(
        string? description,
        GeForceGeneration expected)
    {
        Assert.AreEqual(expected, GeForceGenerationParser.Parse(description));
    }

    [TestMethod]
    public void GenerationNewerThanRtx50StillSatisfiesTheGate()
    {
        // A future card must not be rejected as unknown hardware.
        GeForceGeneration generation =
            GeForceGenerationParser.Parse("NVIDIA GeForce RTX 6080");

        Assert.IsTrue(generation >= GeForceGeneration.Rtx50);
    }

    private static NvidiaGpuCapability SupportedCapability() => new(
        IsNvidiaGeForce: true,
        GeForceGeneration.Rtx50,
        new NvidiaDriverVersion(616, 56),
        NeuralRenderingModelAvailable: true);
}
