using GameShift.MemoryOptimizer.Core.Activity;
using GameShift.MemoryOptimizer.Core.Models;
using GameShift.MemoryOptimizer.Presentation;

namespace GameShift.MemoryOptimizer.Core.Tests;

/// <summary>
/// Pilnuje, ze zaden powod blokady nie wyjdzie na ekran po angielsku.
/// <para>
/// Warstwa Core pisze komunikaty po angielsku, interfejs jest polski.
/// Tlumaczenie dopasowuje sie po tresci komunikatu, bo kod powodu nie dociera
/// do warstwy widoku. Gdyby ktos przeredagowal zdanie w straczniku, samo
/// dopasowanie przestaloby dzialac po cichu — na ekranie znowu pojawilby sie
/// angielski. Dlatego test bierze komunikaty prosto ze straznika, a nie ich
/// przepisane kopie.
/// </para>
/// </summary>
[TestClass]
public sealed class BlockReasonLanguageTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

    private static IEnumerable<GuardDecision> WszystkieBlokady()
    {
        yield return GameActivityEvaluator.Evaluate(
            new ActiveGameDocument(1, "roblox", "Roblox", 42,
                Now.AddMinutes(-1), Now.AddMinutes(1)),
            null,
            [new(42, "RobloxPlayerBeta", true, true)],
            Now);

        yield return GameActivityEvaluator.Evaluate(
            null,
            new KnownGamesDocument(1, Now,
                [new("dbd", "Dead by Daylight", ["DeadByDaylight-Win64-Shipping.exe"])]),
            [new(7, "DeadByDaylight-Win64-Shipping", false, false)],
            Now);

        yield return GameActivityEvaluator.Evaluate(
            null,
            null,
            [new(9, "EasyAntiCheat_EOS", false, false)],
            Now);

        yield return GameActivityEvaluator.Evaluate(
            null,
            null,
            [new(11, "JakisNieznanyProgram", true, true)],
            Now);
    }

    [TestMethod]
    public void KazdyPowodBlokadyMaPolskiOpis()
    {
        int sprawdzone = 0;
        foreach (GuardDecision decyzja in WszystkieBlokady())
        {
            Assert.IsTrue(decyzja.IsBlocked,
                $"Przypadek {decyzja.Code} mial blokowac, a nie blokuje.");
            string opis = MemoryUiFormatting.DescribeBlockReason(decyzja.Message);
            Assert.AreNotEqual(decyzja.Message, opis,
                $"Powod {decyzja.Code} wraca bez tlumaczenia: \"{opis}\". " +
                "Prawdopodobnie zmienila sie tresc komunikatu w GameActivityEvaluator.");
            Assert.IsFalse(opis.Contains("Optimization blocked", StringComparison.Ordinal),
                $"W opisie powodu {decyzja.Code} zostal angielski tekst: \"{opis}\".");
            sprawdzone++;
        }

        Assert.AreEqual(4, sprawdzone,
            "Straznik ma cztery galezie blokady; jesli doszla piata, dopisz ja tutaj i do tlumaczenia.");
    }

    [TestMethod]
    public void NazwaProcesuZostajeWOpisie()
    {
        string opis = MemoryUiFormatting.DescribeBlockReason(
            "Optimization blocked by known game process RobloxPlayerBeta.");

        StringAssert.Contains(opis, "RobloxPlayerBeta");
    }

    [TestMethod]
    public void NieznanyKomunikatPrzechodziBezZmian()
    {
        // Lepiej pokazac oryginal niz zgubic informacje.
        const string obcy = "Cos zupelnie innego.";

        Assert.AreEqual(obcy, MemoryUiFormatting.DescribeBlockReason(obcy));
    }

    [TestMethod]
    public void PustyKomunikatDajePustyOpis()
    {
        Assert.AreEqual(string.Empty, MemoryUiFormatting.DescribeBlockReason("   "));
    }
}
