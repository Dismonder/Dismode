using System.Globalization;
using System.Text;

namespace Dismode.Windows.Processes;

internal sealed class PresentMonCsvParser
{
    private int _processIdIndex = -1;
    private int _swapChainIndex = -1;
    private int _frameTimeIndex = -1;
    private int _gpuBusyIndex = -1;
    private int _displayedTimeIndex = -1;
    private readonly bool _trackDisplay;
    private string? _lastSwapChain;

    // Kolumny wyswietlania czyta tylko parser utworzony z trackDisplay: przy
    // wylaczonym ustawieniu ramka ma dokladnie te same pola co dotychczas.
    internal PresentMonCsvParser(bool trackDisplay = false)
    {
        _trackDisplay = trackDisplay;
    }

    internal bool HasValidHeader =>
        _processIdIndex >= 0
        && _swapChainIndex >= 0
        && _frameTimeIndex >= 0;

    internal bool TryParse(
        string line,
        out PresentMonFrame frame)
    {
        frame = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (!HasValidHeader)
        {
            ReadHeader(SplitCsv(line));
            return false;
        }

        int highestIndex = Math.Max(
            _processIdIndex,
            Math.Max(
                _swapChainIndex,
                Math.Max(
                    _frameTimeIndex,
                    Math.Max(_gpuBusyIndex, _displayedTimeIndex))));
        ReadOnlySpan<char> processField = default;
        ReadOnlySpan<char> chainField = default;
        ReadOnlySpan<char> timeField = default;
        ReadOnlySpan<char> gpuField = default;
        ReadOnlySpan<char> displayedField = default;
        int offset = 0;
        for (int column = 0; column <= highestIndex; column++)
        {
            if (offset > line.Length)
            {
                return false;
            }

            ReadOnlySpan<char> field = ReadField(line.AsSpan(), ref offset);
            if (column == _processIdIndex)
            {
                processField = DecodeField(field, column == 0);
            }
            else if (column == _swapChainIndex)
            {
                chainField = DecodeField(field, column == 0);
            }
            else if (column == _frameTimeIndex)
            {
                timeField = DecodeField(field, column == 0);
            }
            else if (column == _gpuBusyIndex)
            {
                gpuField = DecodeField(field, column == 0);
            }
            else if (column == _displayedTimeIndex)
            {
                displayedField = DecodeField(field, column == 0);
            }
        }

        if (!int.TryParse(
                processField,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId)
            || processId <= 0
            || chainField.IsWhiteSpace()
            || !double.TryParse(
                timeField,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double frameTimeMilliseconds)
            || !double.IsFinite(frameTimeMilliseconds)
            || frameTimeMilliseconds is <= 0 or > 10_000)
        {
            return false;
        }

        double? gpuBusyMilliseconds = null;
        if (_gpuBusyIndex >= 0
            && double.TryParse(
                gpuField,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedGpuBusy)
            && double.IsFinite(parsedGpuBusy)
            && parsedGpuBusy is >= 0 and <= 10_000)
        {
            gpuBusyMilliseconds = parsedGpuBusy;
        }

        // Ramka bez DisplayedTime (NA, puste albo 0) nie trafila na ekran:
        // zgubiona. Przyblizenie: wartosc nieczytelna albo ponad 10 s tez
        // liczy sie jako zgubiona. Bez samej kolumny nie wiemy nic, wiec DisplayTracked
        // zostaje false i liczy sie tylko FPS prezentacji.
        bool displayTracked = _displayedTimeIndex >= 0;
        double? displayedTimeMilliseconds = null;
        if (displayTracked
            && double.TryParse(
                displayedField,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedDisplayed)
            && double.IsFinite(parsedDisplayed)
            && parsedDisplayed is > 0 and <= 10_000)
        {
            displayedTimeMilliseconds = parsedDisplayed;
        }

        // Zachowujemy doslowny klucz (takze nienumeryczny), bez nowego napisu
        // dla kolejnych klatek tego samego swapchaina.
        if (!chainField.SequenceEqual(_lastSwapChain.AsSpan()))
        {
            _lastSwapChain = chainField.ToString();
        }

        frame = new(
            processId,
            _lastSwapChain!,
            frameTimeMilliseconds,
            gpuBusyMilliseconds,
            displayTracked,
            displayedTimeMilliseconds);
        return true;
    }

    private static ReadOnlySpan<char> ReadField(ReadOnlySpan<char> line, scoped ref int offset)
    {
        int start = offset;
        bool quoted = false;
        while (offset < line.Length)
        {
            char character = line[offset++];
            if (character == '"')
            {
                if (quoted && offset < line.Length && line[offset] == '"')
                {
                    offset++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                return line[start..(offset - 1)];
            }
        }

        offset = line.Length + 1;
        return line[start..];
    }

    private static ReadOnlySpan<char> DecodeField(ReadOnlySpan<char> field, bool trimBom)
    {
        if (field.Length >= 2 && field[0] == '"' && field[^1] == '"'
            && !field[1..^1].Contains('"'))
        {
            field = field[1..^1];
        }
        else if (field.Contains('"'))
        {
            // Rzadkie escapowanie tylko w potrzebnej kolumnie. Ta sama
            // tolerancja niezamknietych cudzyslowow co w parserze naglowka.
            StringBuilder decoded = new();
            bool quoted = false;
            for (int index = 0; index < field.Length; index++)
            {
                if (field[index] != '"')
                {
                    decoded.Append(field[index]);
                }
                else if (quoted && index + 1 < field.Length && field[index + 1] == '"')
                {
                    decoded.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }

            field = decoded.ToString().AsSpan();
        }

        return trimBom ? field.TrimStart('\uFEFF') : field;
    }

    internal static string[] SplitCsv(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        List<string> fields = [];
        StringBuilder current = new();
        bool insideQuotes = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (insideQuotes
                    && index + 1 < line.Length
                    && line[index + 1] == '"')
                {
                    _ = current.Append('"');
                    index++;
                }
                else
                {
                    insideQuotes = !insideQuotes;
                }

                continue;
            }

            if (character == ',' && !insideQuotes)
            {
                fields.Add(current.ToString());
                _ = current.Clear();
                continue;
            }

            _ = current.Append(character);
        }

        fields.Add(current.ToString());
        if (fields.Count > 0)
        {
            fields[0] = fields[0].TrimStart('\uFEFF');
        }

        return [.. fields];
    }

    private void ReadHeader(IReadOnlyList<string> fields)
    {
        _processIdIndex = FindColumn(fields, "ProcessID");
        _swapChainIndex = FindColumn(fields, "SwapChainAddress");
        _frameTimeIndex = FindColumn(fields, "FrameTime");
        _gpuBusyIndex = FindColumn(fields, "GPUBusy");
        _displayedTimeIndex = _trackDisplay
            ? FindColumn(fields, "DisplayedTime")
            : -1;
        if (!HasValidHeader)
        {
            _processIdIndex = -1;
            _swapChainIndex = -1;
            _frameTimeIndex = -1;
            _displayedTimeIndex = -1;
        }
    }

    private static int FindColumn(
        IReadOnlyList<string> fields,
        string name)
    {
        for (int index = 0; index < fields.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(fields[index], name))
            {
                return index;
            }
        }

        return -1;
    }
}

internal readonly record struct PresentMonFrame(
    int ProcessId,
    string SwapChainAddress,
    double FrameTimeMilliseconds,
    double? GpuBusyMilliseconds = null,
    bool DisplayTracked = false,
    double? DisplayedTimeMilliseconds = null)
{
    internal bool IsDropped =>
        DisplayTracked && DisplayedTimeMilliseconds is null;
}
