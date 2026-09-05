using GameShift.Core.OptiScaler;

namespace GameShift.UnitTests.OptiScaler;

[TestClass]
public sealed class OptiScalerIniPatcherTests
{
    private const string Sample =
        "; OptiScaler configuration\r\n"
        + "[Upscalers]\r\n"
        + "; auto picks the best available\r\n"
        + "Dx12Upscaler=auto\r\n"
        + "Dx11Upscaler=auto\r\n"
        + "VulkanUpscaler=auto\r\n"
        + "\r\n"
        + "[DLSS]\r\n"
        + "Enabled=auto\r\n"
        + "Preset=auto\r\n"
        + "\r\n"
        + "[DlssNr]\r\n"
        + "Enabled=auto\r\n"
        + "Intensity=auto\r\n";

    [TestMethod]
    public void NeuralRenderingSettingsAreAllApplied()
    {
        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            Sample,
            OptiScalerIniPatcher.NeuralRenderingSettings);

        Assert.AreEqual(0, result.NotFound.Count);
        Assert.AreEqual(5, result.Applied.Count);
        StringAssert.Contains(result.Content, "Dx12Upscaler=dlss\r\n");
        StringAssert.Contains(result.Content, "Dx11Upscaler=dlss_12\r\n");
        StringAssert.Contains(result.Content, "VulkanUpscaler=dlss\r\n");
    }

    [TestMethod]
    public void SameKeyInDifferentSectionsIsDisambiguated()
    {
        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            Sample,
            OptiScalerIniPatcher.NeuralRenderingSettings);

        int dlssSection = result.Content.IndexOf(
            "[DLSS]",
            StringComparison.Ordinal);
        int nrSection = result.Content.IndexOf(
            "[DlssNr]",
            StringComparison.Ordinal);

        // Both sections declare Enabled; each must have been rewritten in place.
        StringAssert.Contains(
            result.Content[dlssSection..nrSection],
            "Enabled=true");
        StringAssert.Contains(
            result.Content[nrSection..],
            "Enabled=true");
    }

    [TestMethod]
    public void UntouchedKeysAndCommentsSurvive()
    {
        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            Sample,
            OptiScalerIniPatcher.NeuralRenderingSettings);

        StringAssert.Contains(result.Content, "; OptiScaler configuration");
        StringAssert.Contains(result.Content, "; auto picks the best available");
        StringAssert.Contains(result.Content, "Preset=auto");
        StringAssert.Contains(result.Content, "Intensity=auto");
    }

    [TestMethod]
    public void CarriageReturnsArePreserved()
    {
        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            Sample,
            OptiScalerIniPatcher.NeuralRenderingSettings);

        Assert.AreEqual(
            Sample.Count(character => character == '\r'),
            result.Content.Count(character => character == '\r'));
        Assert.AreEqual(
            Sample.Count(character => character == '\n'),
            result.Content.Count(character => character == '\n'));
    }

    [TestMethod]
    public void LineFeedOnlyFilesKeepTheirEndings()
    {
        string unixStyle = Sample.Replace("\r\n", "\n");

        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            unixStyle,
            OptiScalerIniPatcher.NeuralRenderingSettings);

        Assert.AreEqual(0, result.NotFound.Count);
        Assert.IsFalse(result.Content.Contains('\r'));
    }

    [TestMethod]
    public void PatchIsIdempotent()
    {
        OptiScalerIniPatchResult first = OptiScalerIniPatcher.Apply(
            Sample,
            OptiScalerIniPatcher.NeuralRenderingSettings);
        OptiScalerIniPatchResult second = OptiScalerIniPatcher.Apply(
            first.Content,
            OptiScalerIniPatcher.NeuralRenderingSettings);

        Assert.AreEqual(first.Content, second.Content);
    }

    [TestMethod]
    public void MissingKeysAreReportedRatherThanAppended()
    {
        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            "[Upscalers]\r\nDx12Upscaler=auto\r\n",
            OptiScalerIniPatcher.NeuralRenderingSettings);

        Assert.AreEqual(1, result.Applied.Count);
        Assert.AreEqual(4, result.NotFound.Count);
        Assert.IsFalse(result.Content.Contains("DlssNr"));
    }

    [TestMethod]
    public void CommentedOutKeyIsNotTreatedAsDeclaration()
    {
        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            "[DlssNr]\r\n; Enabled=auto\r\n",
            [new("DlssNr", "Enabled", "true")]);

        Assert.AreEqual(1, result.NotFound.Count);
        StringAssert.Contains(result.Content, "; Enabled=auto");
    }

    [TestMethod]
    public void KeysOutsideTheRequestedSectionAreLeftAlone()
    {
        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            "[Other]\r\nEnabled=auto\r\n",
            [new("DlssNr", "Enabled", "true")]);

        Assert.AreEqual(1, result.NotFound.Count);
        StringAssert.Contains(result.Content, "Enabled=auto");
    }

    [TestMethod]
    public void SpacingAroundTheKeyIsPreserved()
    {
        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            "[DlssNr]\r\n  Enabled = auto\r\n",
            [new("DlssNr", "Enabled", "true")]);

        StringAssert.Contains(result.Content, "  Enabled =true");
    }

    [TestMethod]
    public void FileWithoutTrailingNewlineIsHandled()
    {
        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            "[DlssNr]\r\nEnabled=auto",
            [new("DlssNr", "Enabled", "true")]);

        Assert.AreEqual(0, result.NotFound.Count);
        Assert.AreEqual("[DlssNr]\r\nEnabled=true", result.Content);
    }
}
