namespace Dismode.UI.Services;

public sealed class SettingsThemePreferences(Func<string?> read, Action<string> write)
{
    public string Load()
    {
        try
        {
            string? theme = read();
            return IsValid(theme) ? theme! : "Default";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Default";
        }
    }

    public void Save(string theme)
    {
        if (!IsValid(theme))
        {
            throw new ArgumentException("Unknown theme.", nameof(theme));
        }

        try
        {
            write(theme);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Wybor nadal dziala w biezacym oknie bez dostepu do zapisu.
        }
    }

    private static bool IsValid(string? theme) => theme is "Dark" or "Light" or "Default";
}
