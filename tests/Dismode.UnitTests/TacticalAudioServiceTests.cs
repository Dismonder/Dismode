using Dismode.UI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dismode.UnitTests;

[TestClass]
public sealed class TacticalAudioServiceTests
{
    [TestMethod]
    public void PinForPlaybackCopiesDataToPinnedObjectHeap()
    {
        byte[] source = [1, 2, 3, 4];

        byte[] pinned = TacticalAudioService.PinForPlayback(source);

        CollectionAssert.AreEqual(source, pinned);
        Assert.AreNotSame(source, pinned);
        // The pinned object heap is reported as the highest generation.
        Assert.AreEqual(GC.MaxGeneration, GC.GetGeneration(pinned));
    }
}
