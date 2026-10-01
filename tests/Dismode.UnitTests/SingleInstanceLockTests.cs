using Dismode.Core.Activation;

namespace Dismode.UnitTests;

[TestClass]
public sealed class SingleInstanceLockTests
{
    [TestMethod]
    public void SecondAcquireFailsUntilTheFirstIsReleased()
    {
        string name = SingleInstanceLock.BuildName(
            "Test",
            Guid.NewGuid().ToString("D"));

        using (SingleInstanceLock? first = SingleInstanceLock.TryAcquire(name))
        {
            Assert.IsNotNull(first);
            Assert.IsNull(
                SingleInstanceLock.TryAcquire(name),
                "Ten sam watek nie moze dostac blokady drugi raz.");
            Assert.IsNull(
                Task.Run(() => SingleInstanceLock.TryAcquire(name)).Result,
                "Inny watek tez nie.");
        }

        using SingleInstanceLock? again = SingleInstanceLock.TryAcquire(name);
        Assert.IsNotNull(again, "Po zwolnieniu nazwa jest znow wolna.");
    }

    [TestMethod]
    public void NameDependsOnComponentAndUserWithoutExposingTheSid()
    {
        const string sid = "S-1-5-21-1000-2000-3000-1001";
        string ui = SingleInstanceLock.BuildName("UI", sid);
        string host = SingleInstanceLock.BuildName("SessionHost", sid);
        string otherUser = SingleInstanceLock.BuildName("UI", sid + "5");

        Assert.StartsWith("Local\\Dismode.UI.", ui);
        Assert.AreNotEqual(ui, host);
        Assert.AreNotEqual(ui, otherUser);
        Assert.AreEqual(ui, SingleInstanceLock.BuildName("UI", " " + sid + " "));
        Assert.DoesNotContain("1001", ui);
    }
}
