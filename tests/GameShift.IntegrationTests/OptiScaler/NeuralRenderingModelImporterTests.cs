using GameShift.Windows.OptiScaler;

namespace GameShift.IntegrationTests.OptiScaler;

[TestClass]
public sealed class NeuralRenderingModelImporterTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void CreateDirectory()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "gameshift-nr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void RemoveDirectory()
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

    [TestMethod]
    public void MissingFileIsRejected()
    {
        NeuralRenderingModelImportResult result = new NeuralRenderingModelImporter()
            .Inspect(Path.Combine(_directory, "nvngx_dlssnr.dll"));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(
            NeuralRenderingModelRejection.NotFound,
            result.Rejection);
    }

    [TestMethod]
    public void FileUnderAnotherNameIsRejected()
    {
        // OptiScaler szuka tych bibliotek wylacznie pod ich wlasnymi nazwami,
        // wiec kopia pod inna nazwa po cichu nic by nie dala. nvngx_dlssg.dll
        // to generator klatek — inny plik, nie nasza sprawa.
        string path = Path.Combine(_directory, "nvngx_dlssg.dll");
        File.WriteAllBytes(path, [0x4D, 0x5A]);

        NeuralRenderingModelImportResult result =
            new NeuralRenderingModelImporter().Inspect(path);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(
            NeuralRenderingModelRejection.WrongFileName,
            result.Rejection);
        Assert.IsNull(result.FileName);
    }

    [TestMethod]
    public void TheDlssRuntimeGoesThroughTheSameGate()
    {
        // Neural Rendering jezdzi na nvngx_dlss.dll, a sterownik 616.x nie
        // niesie ani jego, ani modelu. Oba wiec przychodza od uzytkownika
        // i oba musza przejsc te sama kontrole podpisu.
        string path = Path.Combine(_directory, "nvngx_dlss.dll");
        File.WriteAllBytes(path, [0x4D, 0x5A, .. new byte[512]]);

        NeuralRenderingModelImportResult result =
            new NeuralRenderingModelImporter().Inspect(path);

        Assert.AreEqual("nvngx_dlss.dll", result.FileName);
        Assert.AreEqual(
            NeuralRenderingModelRejection.SignatureInvalid,
            result.Rejection);
        Assert.IsTrue(
            result.IsOverridable,
            "Zla sygnatura ma byc do przejscia swiadoma zgoda.");
    }

    [TestMethod]
    public void UnsignedFileIsRejected()
    {
        // Tak wyglada kopia krazaca po GitHubie: nazwa i metadane moga
        // mowic "NVIDIA", ale bez waznego podpisu nie ma jak tego sprawdzic.
        string path = Path.Combine(_directory, "nvngx_dlssnr.dll");
        File.WriteAllBytes(path, [0x4D, 0x5A, .. new byte[512]]);

        NeuralRenderingModelImportResult result =
            new NeuralRenderingModelImporter().Inspect(path);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(
            NeuralRenderingModelRejection.SignatureInvalid,
            result.Rejection);
        Assert.AreEqual("nvngx_dlssnr.dll", result.FileName);
    }
}
