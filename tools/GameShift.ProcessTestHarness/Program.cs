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
    Text = "GameShift Process Test Harness",
};
using System.Windows.Forms.Timer? exitAfterSpawnTimer =
    exitAfterSpawnMilliseconds is int delay
        ? new()
        {
            Interval = delay,
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
