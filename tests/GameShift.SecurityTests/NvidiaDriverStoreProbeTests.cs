using GameShift.Core.OptiScaler;
using GameShift.Windows.OptiScaler;

namespace GameShift.SecurityTests;

[TestClass]
public sealed class NvidiaDriverStoreProbeTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "gameshift-driverstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void RemoveRoot()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void MissingDriverStoreYieldsNoModelFiles()
    {
        NvidiaDriverStoreSnapshot snapshot = CreateProbe(
            driverStoreRoot: Path.Combine(_root, "does-not-exist"),
            description: "NVIDIA GeForce RTX 5070",
            driverVersion: "32.0.16.1656").Probe();

        Assert.AreEqual(0, snapshot.ModelFiles.Count);
        Assert.IsNull(snapshot.ModelDirectory);
        Assert.IsFalse(snapshot.Capability.NeuralRenderingModelAvailable);
    }

    [TestMethod]
    public void UnsignedModelFilesAreNotAccepted()
    {
        // Files are written unsigned, so the Authenticode gate must drop them
        // even though the names and location look right.
        string directory = Path.Combine(_root, "nv_dispi.inf_amd64_abc123");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "nvngx_dlssnr.dll"),
            "not a real model");

        NvidiaDriverStoreSnapshot snapshot = CreateProbe(
            _root,
            "NVIDIA GeForce RTX 5070",
            "32.0.16.1656").Probe();

        Assert.AreEqual(0, snapshot.ModelFiles.Count);
        Assert.IsFalse(snapshot.Capability.NeuralRenderingModelAvailable);
    }

    [TestMethod]
    public void AdapterDetailsAreReportedFromTheInjectedReader()
    {
        NvidiaDriverStoreSnapshot snapshot = CreateProbe(
            _root,
            "NVIDIA GeForce RTX 5070",
            "32.0.16.1656").Probe();

        Assert.IsTrue(snapshot.Capability.IsNvidiaGeForce);
        Assert.AreEqual(
            GeForceGeneration.Rtx50,
            snapshot.Capability.Generation);
        Assert.AreEqual(
            new NvidiaDriverVersion(616, 56),
            snapshot.Capability.DriverVersion);
    }

    [TestMethod]
    public void NonNvidiaAdapterIsReportedAsUnsupported()
    {
        NvidiaDriverStoreSnapshot snapshot = CreateProbe(
            _root,
            "AMD Radeon RX 9070 XT",
            "32.0.31041.1004").Probe();

        Assert.IsFalse(snapshot.Capability.IsNvidiaGeForce);

        OptiScalerSafetyDecision decision = NeuralRenderingPolicy.Evaluate(
            new(true, OptiScalerSafetyBlockReason.None),
            snapshot.Capability);

        Assert.IsFalse(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.GpuNotSupported,
            decision.BlockReason);
    }

    [TestMethod]
    public void RealMachineProbeDoesNotThrow()
    {
        // Exercises the registry reader and the real DriverStore layout.
        NvidiaDriverStoreSnapshot snapshot = new NvidiaDriverStoreProbe().Probe();

        Assert.IsNotNull(snapshot.Capability);
    }

    private static NvidiaDriverStoreProbe CreateProbe(
        string driverStoreRoot,
        string description,
        string driverVersion) =>
        new(
            driverStoreRoot,
            signatureVerifier: null,
            adapterReader: () => (description, driverVersion));
}
