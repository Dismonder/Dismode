using System.Diagnostics;
using GameShift.Core.Cpu;
using GameShift.Core.Domain.Processes;
using GameShift.Windows.Cpu;
using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Cpu;

/// <summary>
/// The whole restraint chain against a real process: a spinner this test owns
/// and kills, measured by the real inventory, restrained by the real actuator.
/// Everything below this level has been exercised with stubs; this is the only
/// place that proves the pieces agree with each other and with Windows.
/// </summary>
[TestClass]
public sealed class LiveProBalanceTests
{
    private Process? _spinner;

    [TestCleanup]
    public void StopSpinner()
    {
        if (_spinner is null)
        {
            return;
        }

        try
        {
            if (!_spinner.HasExited)
            {
                _spinner.Kill(entireProcessTree: true);
                _spinner.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            _spinner.Dispose();
            _spinner = null;
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task RealSpinnerIsRestrainedAndItsPriorityIsRestored()
    {
        _spinner = StartSpinner();
        ProcessPriorityClass originalPriority = _spinner.PriorityClass;
        Assert.AreEqual(
            ProcessPriorityClass.Normal,
            originalPriority,
            "Test zaklada, ze proces startuje z priorytetem Normal.");

        PriorityProBalanceActuator actuator = new();
        int spinnerId = _spinner.Id;
        ProBalanceSupervisor supervisor = new(
            // Wylacznie nasz proces: z pelnym probnikiem ten test obnizalby
            // priorytet najciezszym procesom maszyny deweloperskiej.
            new FilteredCpuProcessSource(() => [spinnerId]),
            actuator,
            static () => new HashSet<int>(),
            settings: new ProBalanceSettings
            {
                // Maszyna testowa nie musi byc obciazona, a zachowanie silnika
                // przy obciazeniu jest sprawdzane osobno.
                BackgroundLoadCores = 0,
                MinimumRestraint = TimeSpan.FromSeconds(1),
                Cooldown = TimeSpan.FromSeconds(1),
            });

        try
        {
            bool restrained = false;
            for (int index = 0; index < 25 && !restrained; index++)
            {
                await supervisor.TickAsync(
                    TestContext.CancellationTokenSource.Token);
                _spinner.Refresh();
                restrained =
                    _spinner.PriorityClass == ProcessPriorityClass.BelowNormal;
                if (!restrained)
                {
                    await Task.Delay(400);
                }
            }

            Assert.IsTrue(
                restrained,
                "Proces palacy rdzen nie zostal ograniczony w 25 probkach.");
        }
        finally
        {
            // Zatrzymanie ma oddac to, co wzielo — niezaleznie od wyniku.
            await supervisor.DisposeAsync();
        }

        _spinner.Refresh();
        Assert.AreEqual(
            originalPriority,
            _spinner.PriorityClass,
            "Priorytet nie wrocil do stanu sprzed sesji.");
    }

    [TestMethod]
    [Timeout(60_000)]
    public void ActuatorNeverRaisesPriority()
    {
        // Ograniczanie moze tylko obnizac. Proces juz uspiony przez kogos
        // innego ma zostac nietkniety, inaczej GameShift po cichu awansowalby
        // cudza decyzje.
        _spinner = StartSpinner();
        _spinner.PriorityClass = ProcessPriorityClass.Idle;

        PriorityProBalanceActuator actuator = new();
        bool applied = actuator
            .RestrainAsync(
                new(_spinner.Id, _spinner.StartTime.ToUniversalTime()),
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        Assert.IsFalse(applied, "Aktuator ruszyl proces juz o niskim priorytecie.");
        _spinner.Refresh();
        Assert.AreEqual(ProcessPriorityClass.Idle, _spinner.PriorityClass);
    }

    [TestMethod]
    [Timeout(60_000)]
    public void ActuatorRefusesAProcessWhoseIdentityNoLongerMatches()
    {
        // Windows nadaje numery procesow ponownie. Miedzy ograniczeniem a
        // zwolnieniem oryginal moze sie zakonczyc, a jego numer trafic do
        // czegos innego — wtedy "przywrocenie oryginalnego priorytetu" byloby
        // wpisaniem cudzego ustawienia obcemu procesowi.
        _spinner = StartSpinner();
        PriorityProBalanceActuator actuator = new();

        // Ten sam numer, ale czas startu z przeszlosci: tak wyglada numer
        // odziedziczony po zakonczonym procesie.
        ProcessRuntimeKey impostor = new(
            _spinner.Id,
            _spinner.StartTime.ToUniversalTime().AddHours(-1));

        bool applied = actuator
            .RestrainAsync(impostor, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        Assert.IsFalse(
            applied,
            "Aktuator ruszyl proces o niezgodnej tozsamosci.");
        _spinner.Refresh();
        Assert.AreEqual(
            ProcessPriorityClass.Normal,
            _spinner.PriorityClass,
            "Priorytet obcego procesu zostal zmieniony.");
    }

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A process that keeps one core busy. Started without a window and killed
    /// in cleanup, so a failing test cannot leave a core spinning.
    /// </summary>
    private static Process StartSpinner()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$end = (Get-Date).AddMinutes(3); "
            + "while ((Get-Date) -lt $end) { $null = 1 }");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic procesu obciazajacego.");

        // Pierwszy odczyt czasu procesora musi miec co odjac, a PowerShell
        // potrzebuje chwili, zanim zacznie faktycznie palic rdzen.
        Thread.Sleep(1500);
        process.Refresh();
        return process;
    }
}
