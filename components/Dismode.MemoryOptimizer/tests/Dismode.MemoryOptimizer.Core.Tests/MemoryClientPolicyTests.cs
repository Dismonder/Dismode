using System.Text.Json;
using Dismode.MemoryOptimizer.Core.Ipc;
using Dismode.MemoryOptimizer.Presentation;

namespace Dismode.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class MemoryClientPolicyTests
{
    [TestMethod]
    public void OptimizeGetsLongerTimeoutThanStatusCalls()
    {
        Assert.AreEqual(
            TimeSpan.FromSeconds(3),
            MemoryClientPolicy.ExchangeTimeoutFor(
                MemoryOptimizerCommand.GetStatus));
        Assert.IsGreaterThanOrEqualTo(
            TimeSpan.FromSeconds(60),
            MemoryClientPolicy.ExchangeTimeoutFor(
                MemoryOptimizerCommand.Optimize));
        Assert.AreEqual(
            TimeSpan.FromSeconds(3),
            MemoryClientPolicy.ConnectTimeout);
    }

    [TestMethod]
    public void IpcDecodingFailuresAreExpectedClientFailures()
    {
        Assert.IsTrue(MemoryClientPolicy.IsExpectedClientFailure(
            new InvalidDataException("bad frame")));
        Assert.IsTrue(MemoryClientPolicy.IsExpectedClientFailure(
            new JsonException("schema drift")));
        Assert.IsTrue(MemoryClientPolicy.IsExpectedClientFailure(
            new IOException("pipe closed")));
        Assert.IsFalse(MemoryClientPolicy.IsExpectedClientFailure(
            new ArgumentException("bug")));
    }
}
