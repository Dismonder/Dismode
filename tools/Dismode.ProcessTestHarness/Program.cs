using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

ApplicationConfiguration.Initialize();

string? readyFile = ReadOption(args, "--ready-file");
string? childReadyFile = ReadOption(args, "--spawn-child-ready-file");
int? exitAfterSpawnMilliseconds = ReadIntegerOption(
    args,
    "--exit-after-spawn-ms",
    minimum: 10,
    maximum: 10_000);
// Dziecko spawnowane z opoznieniem, a nie od razu po pokazaniu okna: test,
// ktory sprawdza dziedziczenie ustawien procesu (maska powinowactwa
// przechodzi na potomkow utworzonych po jej nalozeniu), potrzebuje dziecka
// urodzonego juz PO tym, jak sesja zdazyla ruszyc rodzica.
int? spawnChildAfterMilliseconds = ReadIntegerOption(
    args,
    "--spawn-child-after-ms",
    minimum: 10,
    maximum: 60_000);
// Dziecko spawnowane na sygnal: test tworzy ten plik dopiero wtedy, gdy
// sprawdzil, ze rodzic jest juz ograniczony. Czasomierz zalezal od
// obciazenia maszyny — osiem sekund raz wystarczalo, raz nie.
string? spawnChildTriggerFile = ReadOption(args, "--spawn-child-when-file");
bool ignoreClose = args.Contains(
    "--ignore-close",
    StringComparer.OrdinalIgnoreCase);
bool isChild = args.Contains(
    "--child",
    StringComparer.OrdinalIgnoreCase);
using Form window = new()
{
    FormBorderStyle = FormBorderStyle.FixedToolWindow,
    Location = new Point(-32_000, -32_000),
    ShowInTaskbar = true,
    Size = new Size(1, 1),
    StartPosition = FormStartPosition.Manual,
    Text = "Dismode Process Test Harness",
};
using System.Windows.Forms.Timer? spawnChildTriggerTimer =
    spawnChildTriggerFile is null
        ? null
        : new()
        {
            Interval = 200,
        };
using System.Windows.Forms.Timer? exitAfterSpawnTimer =
    exitAfterSpawnMilliseconds is int delay
        ? new()
        {
            Interval = delay,
        }
        : null;
// Zakotwiczony tutaj, nie w obsludze zdarzenia: timer WinForms, do ktorego
// odwoluje sie tylko jego wlasna procedura Tick, jest cyklem bez korzenia
// i GC potrafi go zebrac, zanim odmierzy czas. Dziecko wtedy nigdy nie
// powstaje i test czeka na plik, ktorego nie bedzie.
using System.Windows.Forms.Timer? spawnChildTimer =
    spawnChildAfterMilliseconds is int spawnDelay
        ? new()
        {
            Interval = spawnDelay,
        }
        : null;

window.FormClosing += (_, eventArgs) =>
{
    if (ignoreClose)
    {
        eventArgs.Cancel = true;
    }
};

window.FormClosed += (_, _) =>
{
    Application.Exit();
};

window.Shown += (_, _) =>
{
    if (readyFile is not null)
    {
        File.WriteAllText(
            readyFile,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    if (childReadyFile is not null && !isChild)
    {
        if (spawnChildTriggerTimer is not null && spawnChildTriggerFile is not null)
        {
            spawnChildTriggerTimer.Tick += (_, _) =>
            {
                if (!File.Exists(spawnChildTriggerFile))
                {
                    return;
                }

                spawnChildTriggerTimer.Stop();
                SpawnChild(childReadyFile);
            };
            spawnChildTriggerTimer.Start();
        }
        else if (spawnChildTimer is not null)
        {
            spawnChildTimer.Tick += (_, _) =>
            {
                spawnChildTimer.Stop();
                SpawnChild(childReadyFile);
            };
            spawnChildTimer.Start();
        }
        else
        {
            SpawnChild(childReadyFile);
        }
    }

    if (exitAfterSpawnTimer is not null && !isChild)
    {
        exitAfterSpawnTimer.Tick += (_, _) =>
        {
            exitAfterSpawnTimer.Stop();
            window.Close();
        };
        exitAfterSpawnTimer.Start();
    }
};

Application.Run(window);
return;

static void SpawnChild(string childReadyFile)
{
    ProcessStartInfo startInfo = new()
    {
        FileName = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The harness executable path is unavailable."),
        UseShellExecute = false,
    };
    startInfo.ArgumentList.Add("--ready-file");
    startInfo.ArgumentList.Add(childReadyFile);
    startInfo.ArgumentList.Add("--child");
    Process.Start(startInfo)?.Dispose();
}

static string? ReadOption(string[] arguments, string option)
{
    int index = Array.FindIndex(
        arguments,
        argument => string.Equals(
            argument,
            option,
            StringComparison.OrdinalIgnoreCase));

    if (index < 0)
    {
        return null;
    }

    if (index == arguments.Length - 1)
    {
        throw new ArgumentException($"{option} requires a value.");
    }

    return Path.GetFullPath(arguments[index + 1]);
}

static int? ReadIntegerOption(
    string[] arguments,
    string option,
    int minimum,
    int maximum)
{
    int index = Array.FindIndex(
        arguments,
        argument => string.Equals(
            argument,
            option,
            StringComparison.OrdinalIgnoreCase));

    if (index < 0)
    {
        return null;
    }

    if (index == arguments.Length - 1
        || !int.TryParse(
            arguments[index + 1],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int value)
        || value < minimum
        || value > maximum)
    {
        throw new ArgumentException(
            $"{option} requires an integer from {minimum} to {maximum}.");
    }

    return value;
}
