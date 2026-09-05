using System.ServiceProcess;

namespace GameShift.MemoryService;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22631))
        {
            return 2;
        }

        MemoryOptimizerWindowsService service = new();
        if (args.Contains("--console", StringComparer.OrdinalIgnoreCase) ||
            Environment.UserInteractive)
        {
            await MemoryOptimizerWindowsService.RunConsoleAsync(
                CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        ServiceBase.Run(service);
        return 0;
    }
}
