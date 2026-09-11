using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Processes;

/// <summary>
/// The part of the descendant sweep that needs no live process: which ids it
/// even looks at. The value checks that follow are exercised against real
/// processes elsewhere; the walk over the parent map is pure and used to
/// exist only as a comment.
/// </summary>
[TestClass]
public sealed class InheritedRestraintSweeperCandidateTests
{
    private const int Root = 100;

    [TestMethod]
    public void WalksDescendantsBreadthFirstAndSkipsUnrelatedTrees()
    {
        Dictionary<int, int> parents = new()
        {
            [200] = Root,
            [201] = Root,
            [300] = 200,
            [900] = 800,
            [901] = 900,
        };

        IReadOnlyList<(int ProcessId, bool IsOrphan)> candidates =
            InheritedRestraintSweeper.SelectCandidates(
                Root,
                parents,
                allowOrphans: false);

        int[] expected = [200, 201, 300];
        CollectionAssert.AreEquivalent(expected, ProcessIds(candidates));
        Assert.IsTrue(candidates.All(candidate => !candidate.IsOrphan));
        Assert.IsFalse(
            candidates.Any(candidate => candidate.ProcessId == Root),
            "Korzen nie jest swoim potomkiem.");
    }

    [TestMethod]
    public void OrphansAreOfferedOnlyWhenAllowedAndFlagged()
    {
        // 555 ma rodzica 554, ktorego w mapie nie ma: to wnuk za zakonczonym
        // procesem posrednim, albo zupelnie obcy proces po zakonczonym
        // rodzicu. Sweeper nie potrafi tego odroznic po mapie, wiec oddaje go
        // z flaga i kaze sprawdzic pelny odcisk. 900 ma zywego rodzica 800,
        // ktory sam nie ma rodzica — sierota jest 800, nie 900.
        Dictionary<int, int> parents = new()
        {
            [200] = Root,
            [555] = 554,
            [900] = 800,
            [800] = 799,
        };

        IReadOnlyList<(int ProcessId, bool IsOrphan)> withOrphans =
            InheritedRestraintSweeper.SelectCandidates(
                Root,
                parents,
                allowOrphans: true);
        IReadOnlyList<(int ProcessId, bool IsOrphan)> withoutOrphans =
            InheritedRestraintSweeper.SelectCandidates(
                Root,
                parents,
                allowOrphans: false);

        int[] expectedWithOrphans = [200, 555, 800];
        int[] expectedWithoutOrphans = [200];
        CollectionAssert.AreEquivalent(
            expectedWithOrphans,
            ProcessIds(withOrphans));
        Assert.IsFalse(
            withOrphans.Single(candidate => candidate.ProcessId == 200).IsOrphan);
        Assert.IsTrue(
            withOrphans.Single(candidate => candidate.ProcessId == 555).IsOrphan);
        Assert.IsTrue(
            withOrphans.Single(candidate => candidate.ProcessId == 800).IsOrphan);
        CollectionAssert.AreEquivalent(
            expectedWithoutOrphans,
            ProcessIds(withoutOrphans));
    }

    [TestMethod]
    public void SurvivesACycleFromRecycledIds()
    {
        // Po recyklingu numerow mapa potrafi twierdzic, ze rodzic jest
        // dzieckiem swojego dziecka. Przejscie ma sie skonczyc, a nie krecic.
        Dictionary<int, int> parents = new()
        {
            [200] = Root,
            [Root] = 200,
        };

        IReadOnlyList<(int ProcessId, bool IsOrphan)> candidates =
            InheritedRestraintSweeper.SelectCandidates(
                Root,
                parents,
                allowOrphans: true);

        int[] expected = [200];
        CollectionAssert.AreEquivalent(expected, ProcessIds(candidates));
    }

    [TestMethod]
    public void NeverOffersItselfOrTheSystemProcesses()
    {
        Dictionary<int, int> parents = new()
        {
            [Environment.ProcessId] = Root,
            [4] = Root,
            [0] = Root,
            [200] = Root,
        };

        IReadOnlyList<(int ProcessId, bool IsOrphan)> candidates =
            InheritedRestraintSweeper.SelectCandidates(
                Root,
                parents,
                allowOrphans: true);

        int[] expected = [200];
        CollectionAssert.AreEquivalent(expected, ProcessIds(candidates));
    }

    private static int[] ProcessIds(
        IReadOnlyList<(int ProcessId, bool IsOrphan)> candidates) =>
        [.. candidates.Select(candidate => candidate.ProcessId)];
}
