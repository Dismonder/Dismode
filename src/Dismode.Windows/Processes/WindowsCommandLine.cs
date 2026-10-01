using System.Text;

namespace Dismode.Windows.Processes;

/// <summary>
/// Sklada wiersz polecen dla CreateProcess tak, jak rozbiera go
/// CommandLineToArgvW i biblioteka C: argument bez bialych znakow i
/// cudzyslowu idzie goly, kazdy inny w cudzyslowie, a odwrotne ukosniki sa
/// podwajane tylko tam, gdzie poprzedzaja cudzyslow. Te same reguly stosuje
/// Process.Start dla ArgumentList, wiec gra dostaje identyczne argumenty
/// niezaleznie od tego, ktora droga ja uruchomiono.
/// </summary>
public static class WindowsCommandLine
{
    public static string Build(string fileName, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        StringBuilder builder = new();
        AppendArgument(builder, fileName);
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument, nameof(arguments));
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    public static void AppendArgument(StringBuilder builder, string argument)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length > 0 && !NeedsQuotes(argument))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        int index = 0;
        while (index < argument.Length)
        {
            char current = argument[index++];
            if (current == '\\')
            {
                int backslashes = 1;
                while (index < argument.Length && argument[index] == '\\')
                {
                    index++;
                    backslashes++;
                }

                if (index == argument.Length)
                {
                    // Przed zamykajacym cudzyslowem kazdy ukosnik podwojnie,
                    // inaczej zjadlby cudzyslow.
                    builder.Append('\\', backslashes * 2);
                }
                else if (argument[index] == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1);
                    builder.Append('"');
                    index++;
                }
                else
                {
                    builder.Append('\\', backslashes);
                }
            }
            else if (current == '"')
            {
                builder.Append('\\');
                builder.Append('"');
            }
            else
            {
                builder.Append(current);
            }
        }

        builder.Append('"');
    }

    private static bool NeedsQuotes(string argument)
    {
        foreach (char character in argument)
        {
            if (char.IsWhiteSpace(character) || character == '"')
            {
                return true;
            }
        }

        return false;
    }
}
