using System.Collections.ObjectModel;

namespace GameShift.Windows.Processes;

public sealed record ApplicationRestartDescriptor
{
    private const int MaximumArgumentCount = 64;
    private const int MaximumArgumentLength = 4096;
    private const int MaximumTotalArgumentLength = 16 * 1024;

    public ApplicationRestartDescriptor(
        string executablePath,
        string workingDirectory,
        IEnumerable<string>? arguments,
        ApplicationRestartability restartability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "The restart executable path must be fully qualified.",
                nameof(executablePath));
        }

        if (!Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException(
                "The restart working directory must be fully qualified.",
                nameof(workingDirectory));
        }

        string[] argumentArray = arguments?.ToArray() ?? [];
        if (argumentArray.Length > MaximumArgumentCount
            || argumentArray.Any(argument =>
                argument is null || argument.Length > MaximumArgumentLength)
            || argumentArray.Sum(argument => argument.Length)
                > MaximumTotalArgumentLength)
        {
            throw new ArgumentException(
                "The restart arguments exceed the safe profile limits.",
                nameof(arguments));
        }

        ExecutablePath = Path.GetFullPath(executablePath);
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        Arguments = new ReadOnlyCollection<string>(argumentArray);
        Restartability = restartability;
    }

    public string ExecutablePath { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyList<string> Arguments { get; }

    public ApplicationRestartability Restartability { get; }
}
