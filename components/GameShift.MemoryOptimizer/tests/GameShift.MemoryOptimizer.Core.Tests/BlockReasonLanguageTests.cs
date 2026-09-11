using GameShift.MemoryOptimizer.Core.Activity;
using GameShift.MemoryOptimizer.Core.Native;
using GameShift.MemoryOptimizer.Core.Optimization;
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
            string opis = MemoryUiFormatting.DescribeServiceMessage(decyzja.Message);
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

    /// <summary>
    /// Bierze komunikaty z prawdziwego silnika, nie z przepisanych kopii.
    /// <para>
    /// Sciezka na ekran jest ta sama, co dla powodow blokady: komunikat wyniku
    /// idzie do ostatniego wyniku, do historii i do menu w trayu. Silnik daje
    /// sie zlozyc z atrap, wiec nie ma powodu ufac przepisanym literalom.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task WynikUdanejOptymalizacjiJestPoPolsku()
    {
        OptimizationResult wynik = await Uruchom(new AtrapaOperacji(udane: true));

        Assert.AreEqual(OptimizationState.Completed, wynik.State);
        SprawdzPolski(wynik.Message, "udana optymalizacja");
    }

    [TestMethod]
    public async Task WynikCzesciowejPorazkiJestPoPolsku()
    {
        OptimizationResult wynik = await Uruchom(new AtrapaOperacji(udane: false));

        SprawdzPolski(wynik.Message, "czesc obszarow sie nie udala");
    }

    [TestMethod]
    public async Task KomunikatODrugiejOptymalizacjiJestPoPolsku()
    {
        // Bez wyscigu: pierwsza optymalizacja wisi na strazniku, dopoki
        // druga nie dostanie odpowiedzi.
        BramkaStraznika bramka = new();
        using MemoryOptimizerEngine silnik = new(
            new AtrapaOperacji(udane: true),
            new AtrapaMetryk(),
            bramka);
        Task<OptimizationResult> pierwsza = silnik.OptimizeAsync(
            OptimizationTrigger.Manual,
            MemoryOptimizerSettings.BasicAreas,
            new(),
            CancellationToken.None);
        await bramka.Weszla.Task;

        OptimizationResult druga = await silnik.OptimizeAsync(
            OptimizationTrigger.Manual,
            MemoryOptimizerSettings.BasicAreas,
            new(),
            CancellationToken.None);
        bramka.Zwolnij.SetResult(GuardDecision.Allowed);
        _ = await pierwsza;

        Assert.AreEqual(OptimizationState.AlreadyRunning, druga.State);
        SprawdzPolski(druga.Message, "druga optymalizacja naraz");
    }

    private static Task<OptimizationResult> Uruchom(AtrapaOperacji operacje)
    {
        MemoryOptimizerSettings ustawienia = new()
        {
            AdvancedModeEnabled = true,
            AdvancedWarningAccepted = true,
            ManualAreas = MemoryArea.WorkingSet,
        };

        MemoryOptimizerEngine silnik = new(
            operacje,
            new AtrapaMetryk(),
            new AllowAllGameActivityGuard());
        return silnik.OptimizeAsync(
            OptimizationTrigger.Manual,
            ustawienia.ManualAreas,
            ustawienia,
            CancellationToken.None);
    }

    private static void SprawdzPolski(string komunikat, string skad)
    {
        string opis = MemoryUiFormatting.DescribeServiceMessage(komunikat);
        Assert.AreNotEqual(komunikat, opis,
            $"Komunikat ({skad}) wraca bez tlumaczenia: \"{opis}\". " +
            "Prawdopodobnie zmienila sie jego tresc w warstwie Core.");
        Assert.IsFalse(
            opis.Contains("optimization", StringComparison.OrdinalIgnoreCase),
            $"W opisie ({skad}) zostal angielski tekst: \"{opis}\".");
    }

    private sealed class AtrapaOperacji(bool udane) : IMemoryAreaOperations
    {
        public MemoryAreaResult Execute(
            MemoryArea area,
            MemoryOptimizerSettings settings,
            CancellationToken cancellationToken) =>
            new(area, udane, TimeSpan.Zero, udane ? null : 5, "ok");
    }

    private sealed class AtrapaMetryk : IMemoryMetricsSource
    {
        public MemorySnapshot Capture() =>
            new(DateTimeOffset.UtcNow, 1000, 500, 2000, 1000, 50);
    }

    private sealed class BramkaStraznika : IGameActivityGuard
    {
        internal TaskCompletionSource Weszla { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<GuardDecision> Zwolnij { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<GuardDecision> EvaluateAsync(
            CancellationToken cancellationToken)
        {
            Weszla.SetResult();
            return new(Zwolnij.Task.WaitAsync(cancellationToken));
        }
    }

    [TestMethod]
    public void NazwaProcesuZostajeWOpisie()
    {
        string opis = MemoryUiFormatting.DescribeServiceMessage(
            "Optimization blocked by known game process RobloxPlayerBeta.");

        StringAssert.Contains(opis, "RobloxPlayerBeta");
    }

    [TestMethod]
    public void NieznanyKomunikatPrzechodziBezZmian()
    {
        // Lepiej pokazac oryginal niz zgubic informacje.
        const string obcy = "Cos zupelnie innego.";

        Assert.AreEqual(obcy, MemoryUiFormatting.DescribeServiceMessage(obcy));
    }

    [TestMethod]
    public void PustyKomunikatDajePustyOpis()
    {
        Assert.AreEqual(string.Empty, MemoryUiFormatting.DescribeServiceMessage("   "));
    }
}
