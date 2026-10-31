using Dismode.Windows.Power;

namespace Dismode.IntegrationTests.Power;

[TestClass]
public sealed class ExecutionStateInhibitorTests
{
    [TestMethod]
    public void AcquireAndReleaseAreIdempotent()
    {
        // Prawdziwe zadanie zasilania, trzymane ulamek sekundy: sprawdza, ze
        // wywolania Win32 przechodza i ze powtorne wywolania nic nie psuja.
        // Reczny dowod dzialania: `powercfg /requests` w trakcie sesji (wpis
        // "Dismode: sesja gry ...") i po niej (brak wpisu).
        using WindowsExecutionStateInhibitor inhibitor = new();

        Assert.IsTrue(inhibitor.TryAcquire("Dismode: test"));
        Assert.IsTrue(
            inhibitor.TryAcquire("Dismode: test"),
            "Powtorne wziecie przy trzymanym zadaniu nie tworzy drugiego.");

        inhibitor.Release();
        inhibitor.Release();
        inhibitor.Dispose();

        Assert.IsTrue(
            inhibitor.TryAcquire("Dismode: test"),
            "Po zwolnieniu mozna wziac zadanie od nowa.");
        inhibitor.Release();
    }

    [TestMethod]
    public void ReleaseWithoutAcquireDoesNothing()
    {
        using WindowsExecutionStateInhibitor inhibitor = new();

        inhibitor.Release();
        inhibitor.Dispose();
    }

    [TestMethod]
    public void BlankReasonIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            using WindowsExecutionStateInhibitor inhibitor = new();
            _ = inhibitor.TryAcquire(" ");
        });
}
