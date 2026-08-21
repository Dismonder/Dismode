namespace GameShift.Core.Activation;

public sealed record GameShiftLaunchOptions(
    bool StartInBackground,
    string? GameExecutablePath)
{
    public static GameShiftLaunchOptions Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool background = false;
        string? executablePath = null;

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(
                    argument,
                    "--background",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (background)
                {
                    throw new ArgumentException(
                        "Opcję --background podano więcej niż raz.",
                        nameof(arguments));
                }

                background = true;
                continue;
            }

            if (string.Equals(
                    argument,
                    "--launch-through-gameshift",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (executablePath is not null
                    || index + 1 >= arguments.Count)
                {
                    throw new ArgumentException(
                        "Opcja uruchomienia gry wymaga jednej ścieżki EXE.",
                        nameof(arguments));
                }

                executablePath = NormalizeExecutablePath(
                    arguments[++index]);
                continue;
            }

            throw new ArgumentException(
                $"Nieznany argument uruchomienia: {argument}",
                nameof(arguments));
        }

        return new(background, executablePath);
    }

    public static string NormalizeExecutablePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 32_767)
        {
            throw new ArgumentOutOfRangeException(
                nameof(path),
                "Ścieżka EXE przekracza limit Windows.");
        }

        string fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath)
            || !string.Equals(
                Path.GetExtension(fullPath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "GameShift przyjmuje wyłącznie pełną ścieżkę pliku EXE.",
                nameof(path));
        }

        return fullPath;
    }
}
