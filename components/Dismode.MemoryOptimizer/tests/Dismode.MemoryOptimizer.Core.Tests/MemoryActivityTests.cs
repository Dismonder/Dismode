// SPDX-License-Identifier: GPL-3.0-only
using Dismode.MemoryOptimizer.Presentation;

namespace Dismode.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class MemoryActivityTests
{
    [TestMethod]
    public void LateOperationReplyCannotRestartHiddenIndicators()
    {
        MemoryActivityState hidden = MemoryActivityState.Create(false, false, true, true, true);
        Assert.AreEqual(new MemoryActivityState(false, false, false, false), hidden);

        MemoryActivityState restored = MemoryActivityState.Create(true, false, true, true, true);
        Assert.IsTrue(restored.RamUsage);
        Assert.IsTrue(restored.Optimize);
        Assert.IsTrue(restored.Save);
        Assert.IsFalse(restored.Compact);
    }

    [TestMethod]
    public void OnlyTheVisibleSurfaceAnimatesDuringAnOperation()
    {
        MemoryActivityState compact = MemoryActivityState.Create(true, true, true, false, true);
        Assert.AreEqual(new MemoryActivityState(false, false, false, true), compact);
        Assert.AreEqual(new MemoryActivityState(true, false, false, false),
            MemoryActivityState.Create(true, false, false, false, true));
    }

    [TestMethod]
    public void DisabledAnimationsKeepTheReadingButStopBusyLoops()
    {
        Assert.AreEqual(new MemoryActivityState(true, false, false, false),
            MemoryActivityState.Create(true, false, true, true, false));
        Assert.AreEqual(new MemoryActivityState(false, false, false, false),
            MemoryActivityState.Create(true, true, true, true, false));
    }
}
