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

        string[] fields = SplitCsv(line);
        if (!HasValidHeader)
        {
            ReadHeader(fields);
            return false;
        }

        int highestIndex = Math.Max(
            _processIdIndex,
            Math.Max(
                _swapChainIndex,
                Math.Max(
                    _frameTimeIndex,
                    Math.Max(_gpuBusyIndex, _displayedTimeIndex))));
        if (fields.Length <= highestIndex
            || !int.TryParse(
                fields[_processIdIndex],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId)
            || processId <= 0
            || string.IsNullOrWhiteSpace(fields[_swapChainIndex])
            || !double.TryParse(
                fields[_frameTimeIndex],
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
                fields[_gpuBusyIndex],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedGpuBusy)
            && double.IsFinite(parsedGpuBusy)
            && parsedGpuBusy is >= 0 and <= 10_000)
        {
            gpuBusyMilliseconds = parsedGpuBusy;
        }

        // Ramka bez DisplayedTime (NA, puste albo 0) nie trafila na ekran:
        // zgubiona. Bez samej kolumny nie wiemy nic, wiec DisplayTracked
        // zostaje false i liczy sie tylko FPS prezentacji.
        bool displayTracked = _displayedTimeIndex >= 0;
        double? displayedTimeMilliseconds = null;
        if (displayTracked
            && double.TryParse(
                fields[_displayedTimeIndex],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedDisplayed)
            && double.IsFinite(parsedDisplayed)
            && parsedDisplayed is > 0 and <= 10_000)
        {
            displayedTimeMilliseconds = parsedDisplayed;
        }

        frame = new(
            processId,
            fields[_swapChainIndex],
            frameTimeMilliseconds,
            gpuBusyMilliseconds,
            displayTracked,
            displayedTimeMilliseconds);
        return true;
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
